using System.Collections.Concurrent;
using DualMod.IdolsAndGearVault.Shared;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.ServerBridge;

namespace DualMod.IdolsAndGearVault.Server;

/// <summary>
/// Per-player server-authoritative state for the 32-cell Idol Board and Gear Vault configuration.
/// </summary>
public sealed class PlayerIdolsVaultState
{
    public Lock Gate { get; } = new();
    public Dictionary<string, PlacedIdolRecord> PlacedIdols { get; } = new(StringComparer.Ordinal);
    public ushort TargetGearVaultTabIndex { get; set; } = IdolsVaultProtocol.DefaultGearVaultTabIndex;
    public string GearClassFilter { get; set; } = IdolsVaultProtocol.DefaultGearClassFilter;
    public bool StockAffinityEnabled { get; set; } = true;
    public byte StockAffinityBitId { get; set; } = IdolsVaultProtocol.DefaultStockAffinityBitId;
}

/// <summary>
/// Server half of <c>DualMod.IdolsAndGearVault</c> providing authoritative 32-cell Idol Board placement/removal
/// validation, Gear Vault affinity configuration &amp; tab item sync, and live S→C <c>0x02C9</c> ruleset telemetry
/// on verified free slot <see cref="IRulesetRegistry.FirstFreeNativeRulesetId"/> (<c>0xAE</c>).
/// </summary>
public sealed class IdolsVaultServerMod : IDisposable
{
    private readonly ClientBridgeHost _bridge;
    private readonly IIdolsVaultBackend _backend;
    private readonly IdolsVaultSettings _settings;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly ConcurrentDictionary<string, PlayerIdolsVaultState> _playerStates = new(StringComparer.Ordinal);
    private bool _disposed;

    public IdolsVaultServerMod(
        ClientBridgeHost bridge,
        IIdolsVaultBackend backend,
        IdolsVaultSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(backend);
        _bridge = bridge;
        _backend = backend;
        _settings = settings ?? new IdolsVaultSettings();
    }

    public IdolsVaultSettings Settings => _settings;

    public void Attach()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _subscriptions.Add(_bridge.On<PlaceIdolRequest>(IdolsVaultProtocol.ChannelName, async (session, req) =>
        {
            IdolBoardStateSync sync = PlaceIdol(session.ConnectionId, req);
            await session.SendAsync(IdolsVaultProtocol.ChannelName, sync);
            await SendBoardRulesetTelemetryAsync(session, sync.Idols.Length, sync.OccupiedCells);
            await session.SendAsync(
                IdolsVaultProtocol.ChannelName,
                new IdolsVaultToastNotification(
                    Title: "Idol Board",
                    Message: sync.StatusMessage,
                    Severity: sync.Succeeded ? "Success" : "Warning"));
        }));

        _subscriptions.Add(_bridge.On<RemoveIdolRequest>(IdolsVaultProtocol.ChannelName, async (session, req) =>
        {
            IdolBoardStateSync sync = RemoveIdol(session.ConnectionId, req);
            await session.SendAsync(IdolsVaultProtocol.ChannelName, sync);
            await SendBoardRulesetTelemetryAsync(session, sync.Idols.Length, sync.OccupiedCells);
            await session.SendAsync(
                IdolsVaultProtocol.ChannelName,
                new IdolsVaultToastNotification(
                    Title: "Idol Board",
                    Message: sync.StatusMessage,
                    Severity: sync.Succeeded ? "Info" : "Warning"));
        }));

        _subscriptions.Add(_bridge.On<SyncIdolBoardRequest>(IdolsVaultProtocol.ChannelName, async (session, _) =>
        {
            IdolBoardStateSync sync = GetBoardSnapshot(session.ConnectionId, "Idol Board synchronized.");
            await session.SendAsync(IdolsVaultProtocol.ChannelName, sync);
            await SendBoardRulesetTelemetryAsync(session, sync.Idols.Length, sync.OccupiedCells);
        }));

        _subscriptions.Add(_bridge.On<ConfigureGearVaultRequest>(IdolsVaultProtocol.ChannelName, async (session, req) =>
        {
            GearVaultStateSync sync = ConfigureGearVault(session.ConnectionId, req);
            await session.SendAsync(IdolsVaultProtocol.ChannelName, sync);
            await session.SendAsync(
                IdolsVaultProtocol.ChannelName,
                new IdolsVaultToastNotification(
                    Title: "Gear Vault",
                    Message: sync.StatusMessage,
                    Severity: sync.Succeeded ? "Success" : "Warning"));
        }));

        _subscriptions.Add(_bridge.On<SyncGearVaultRequest>(IdolsVaultProtocol.ChannelName, async (session, req) =>
        {
            GearVaultStateSync sync = GetGearVaultSnapshot(session.ConnectionId, req.GearClassFilter);
            await session.SendAsync(IdolsVaultProtocol.ChannelName, sync);
        }));
    }

    public void OnPlayerLeft(string connectionId) => _playerStates.TryRemove(connectionId, out _);

    /// <summary>
    /// Server-authoritative placement validation against the 32-cell 6×6 Idol Board geometry
    /// (4 blocked corner cells, allowed PoE 3.29 Idol footprints, non-overlapping occupancy, unique IdolId).
    /// </summary>
    public IdolBoardStateSync PlaceIdol(string connectionId, PlaceIdolRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(request);

        PlayerIdolsVaultState state = GetOrCreateState(connectionId);
        lock (state.Gate)
        {
            string idolId = (request.IdolId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(idolId))
            {
                return BuildBoardSyncLocked(state, "Idol placement rejected: IdolId is required.", succeeded: false);
            }

            if (!IdolBoardGeometry.CanPlace(
                    request.Col,
                    request.Row,
                    request.Width,
                    request.Height,
                    state.PlacedIdols.Values,
                    out string reason,
                    candidateIdolId: idolId))
            {
                return BuildBoardSyncLocked(state, $"Placement rejected: {reason}", succeeded: false);
            }

            string name = string.IsNullOrWhiteSpace(request.Name) ? $"Idol ({request.Width}x{request.Height})" : request.Name.Trim();
            string metadataPath = string.IsNullOrWhiteSpace(request.MetadataPath) ? "Metadata/Items/Idols/MinorIdol" : request.MetadataPath.Trim();
            string iconPath = string.IsNullOrWhiteSpace(request.IconPath) ? "Art/2DItems/Currency/Scarab.dds" : request.IconPath.Trim();
            string[] affixTags = request.AffixTags is { Length: > 0 } ? request.AffixTags : ["Idol", "Atlas"];
            string[] modLines = request.ModSummaryLines is { Length: > 0 } ? request.ModSummaryLines : ["+5% increased Pack Size"];

            var record = new PlacedIdolRecord(
                IdolId: idolId,
                Name: name,
                MetadataPath: metadataPath,
                Col: request.Col,
                Row: request.Row,
                Width: request.Width,
                Height: request.Height,
                Rarity: Math.Clamp(request.Rarity, 0, 3),
                IconPath: iconPath,
                AffixTags: affixTags,
                ModSummaryLines: modLines);

            state.PlacedIdols[idolId] = record;
            int occupied = IdolBoardGeometry.CountOccupiedCells(state.PlacedIdols.Values);
            return BuildBoardSyncLocked(
                state,
                $"Placed '{record.Name}' ({record.Width}x{record.Height}) at ({record.Col}, {record.Row}) [{occupied}/{IdolBoardGeometry.ActiveCellCount} cells].",
                succeeded: true);
        }
    }

    /// <summary>Server-authoritative Idol removal from the player's 32-cell Idol Board.</summary>
    public IdolBoardStateSync RemoveIdol(string connectionId, RemoveIdolRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(request);

        PlayerIdolsVaultState state = GetOrCreateState(connectionId);
        lock (state.Gate)
        {
            string idolId = (request.IdolId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(idolId) || !state.PlacedIdols.Remove(idolId, out PlacedIdolRecord? removed))
            {
                return BuildBoardSyncLocked(state, $"Idol '{idolId}' was not found on the board.", succeeded: false);
            }

            int occupied = IdolBoardGeometry.CountOccupiedCells(state.PlacedIdols.Values);
            return BuildBoardSyncLocked(
                state,
                $"Removed '{removed.Name}' from ({removed.Col}, {removed.Row}) [{occupied}/{IdolBoardGeometry.ActiveCellCount} cells].",
                succeeded: true);
        }
    }

    public IdolBoardStateSync GetBoardSnapshot(string connectionId, string statusMessage = "Idol Board snapshot.")
    {
        PlayerIdolsVaultState state = GetOrCreateState(connectionId);
        lock (state.Gate)
        {
            return BuildBoardSyncLocked(state, statusMessage, succeeded: true);
        }
    }

    /// <summary>Updates and validates a player's custom Gear Vault tab index, class filter, and stock affinity mapping.</summary>
    public GearVaultStateSync ConfigureGearVault(string connectionId, ConfigureGearVaultRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(request);

        PlayerIdolsVaultState state = GetOrCreateState(connectionId);
        ushort clampedTab;
        string normalizedClass;
        bool stockEnabled;
        byte clampedBit;

        if (!GearVaultClasses.IsValid(request.GearClassFilter))
        {
            lock (state.Gate)
            {
                GearVaultItemRecord[] currentItems = [.. _backend.GetVaultItems(
                    connectionId,
                    state.TargetGearVaultTabIndex,
                    state.GearClassFilter,
                    ClampMaxVaultEntries())];
                return new GearVaultStateSync(
                    state.TargetGearVaultTabIndex,
                    state.GearClassFilter,
                    state.StockAffinityEnabled,
                    state.StockAffinityBitId,
                    currentItems,
                    $"Unsupported Gear Vault class '{request.GearClassFilter}'.",
                    Succeeded: false);
            }
        }

        lock (state.Gate)
        {
            state.TargetGearVaultTabIndex = Math.Min(request.TargetTabIndex, IdolsVaultProtocol.MaxGearVaultTabIndex);
            state.GearClassFilter = GearVaultClasses.Normalize(request.GearClassFilter);
            state.StockAffinityEnabled = request.StockAffinityEnabled;
            state.StockAffinityBitId = Math.Min(request.StockAffinityBitId, IdolsVaultProtocol.MaxStockAffinityBitId);

            clampedTab = state.TargetGearVaultTabIndex;
            normalizedClass = state.GearClassFilter;
            stockEnabled = state.StockAffinityEnabled;
            clampedBit = state.StockAffinityBitId;
        }

        GearVaultItemRecord[] items = [.. _backend.GetVaultItems(connectionId, clampedTab, normalizedClass, ClampMaxVaultEntries())];
        return new GearVaultStateSync(
            TargetTabIndex: clampedTab,
            GearClassFilter: normalizedClass,
            StockAffinityEnabled: stockEnabled,
            StockAffinityBitId: clampedBit,
            Items: items,
            StatusMessage: $"Gear Vault configured: {normalizedClass} -> Tab {clampedTab} ({items.Length} item(s)).",
            Succeeded: true);
    }

    public GearVaultStateSync GetGearVaultSnapshot(string connectionId, string? gearClassOverride = null)
    {
        PlayerIdolsVaultState state = GetOrCreateState(connectionId);
        ushort tab;
        string filterClass;
        bool stockEnabled;
        byte stockBit;
        lock (state.Gate)
        {
            tab = state.TargetGearVaultTabIndex;
            filterClass = !string.IsNullOrWhiteSpace(gearClassOverride) && GearVaultClasses.IsValid(gearClassOverride)
                ? GearVaultClasses.Normalize(gearClassOverride)
                : state.GearClassFilter;
            stockEnabled = state.StockAffinityEnabled;
            stockBit = state.StockAffinityBitId;
        }

        GearVaultItemRecord[] items = [.. _backend.GetVaultItems(connectionId, tab, filterClass, ClampMaxVaultEntries())];
        return new GearVaultStateSync(
            TargetTabIndex: tab,
            GearClassFilter: filterClass,
            StockAffinityEnabled: stockEnabled,
            StockAffinityBitId: stockBit,
            Items: items,
            StatusMessage: $"Gear Vault synced: {items.Length} {filterClass} item(s) in Tab {tab}.",
            Succeeded: true);
    }

    private PlayerIdolsVaultState GetOrCreateState(string connectionId) =>
        _playerStates.GetOrAdd(connectionId, _ => new PlayerIdolsVaultState
        {
            TargetGearVaultTabIndex = (ushort)Math.Clamp(_settings.DefaultGearVaultTabIndex, 0, IdolsVaultProtocol.MaxGearVaultTabIndex),
            GearClassFilter = GearVaultClasses.Normalize(_settings.DefaultGearClassFilter),
            StockAffinityEnabled = _settings.EnableStockAffinityMapping,
            StockAffinityBitId = (byte)Math.Clamp(_settings.StockAffinityBitId, 0, IdolsVaultProtocol.MaxStockAffinityBitId),
        });

    private int ClampMaxVaultEntries() =>
        Math.Clamp(_settings.MaxVaultEntriesPerSync, IdolsVaultProtocol.MinVaultEntriesPerSync, IdolsVaultProtocol.MaxVaultEntriesPerSync);

    private static IdolBoardStateSync BuildBoardSyncLocked(PlayerIdolsVaultState state, string statusMessage, bool succeeded)
    {
        PlacedIdolRecord[] idols = [.. state.PlacedIdols.Values.OrderBy(i => i.Row).ThenBy(i => i.Col).ThenBy(i => i.IdolId, StringComparer.Ordinal)];
        int occupied = IdolBoardGeometry.CountOccupiedCells(idols);
        return new IdolBoardStateSync(
            Idols: idols,
            ActiveCellsTotal: IdolBoardGeometry.ActiveCellCount,
            OccupiedCells: occupied,
            StatusMessage: statusMessage,
            Succeeded: succeeded);
    }

    private Task SendBoardRulesetTelemetryAsync(IClientSession session, int idolCount, int occupiedCells)
    {
        byte[] inner =
        [
            (byte)Math.Clamp(idolCount, 0, byte.MaxValue),
            (byte)Math.Clamp(occupiedCells, 0, IdolBoardGeometry.ActiveCellCount),
        ];
        return _backend.SendBoardRulesetUpdateAsync(session.ConnectionId, IRulesetRegistry.FirstFreeNativeRulesetId, inner);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (IDisposable sub in _subscriptions)
        {
            sub.Dispose();
        }

        _subscriptions.Clear();
        _playerStates.Clear();
    }
}
