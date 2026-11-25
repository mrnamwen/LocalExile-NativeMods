using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using DualMod.SiphonOrb.Shared;
using LocalExile.Client.ServerBridge;

namespace DualMod.SiphonOrb.Server;

/// <summary>
/// Outcome of committing a Steal transaction in the world backend (remove affix from source item,
/// consume 1 Empty Orb of Siphoning, and place the newly minted Charged Orb of Siphoning into inventory).
/// </summary>
public sealed record SiphonStealCommitResult(
    bool Success,
    string? Error,
    StoredAffixPayload? StolenAffix = null,
    uint CreatedChargedItemId = 0,
    ItemAffixView? UpdatedSourceView = null);

/// <summary>
/// Outcome of committing an Imbue transaction in the world backend (replace chosen affix on target item
/// with the Charged Orb's stored modifier and consume the Charged Orb).
/// </summary>
public sealed record SiphonImbueCommitResult(
    bool Success,
    string? Error,
    ItemAffixSnapshot? ReplacedAffix = null,
    StoredAffixPayload? AppliedAffix = null,
    ItemAffixView? UpdatedTargetView = null);

/// <summary>
/// Abstraction bridging <see cref="SiphonServerMod"/> to either a live LocalExile Gateway
/// (<c>SiphonLocalExileEntry</c>) or an in-memory test harness (<see cref="SimulatedSiphonWorldBackend"/>).
/// </summary>
public interface ISiphonWorldBackend
{
    /// <summary>Registers a brand-new custom currency definition into the server's live item catalog.</summary>
    IDisposable RegisterCurrency(CustomCurrencyItemDefinition definition);

    /// <summary>Grants <paramref name="count"/> empty Orbs of Siphoning into the player's inventory.</summary>
    Task<(bool Success, string Message, uint GrantedItemId)> GrantEmptyOrbsAsync(
        string connectionId,
        string characterName,
        ushort count,
        CancellationToken cancellationToken = default);

    /// <summary>Inspects the target item at <c>(inventoryId, itemId)</c> and returns its explicit affix view, or <c>null</c> if not found.</summary>
    Task<ItemAffixView?> InspectItemAsync(
        string connectionId,
        uint inventoryId,
        uint itemId,
        CancellationToken cancellationToken = default);

    /// <summary>Returns <c>true</c> if the target inventory slot is currently equipped on the player.</summary>
    bool IsItemEquipped(string connectionId, uint inventoryId, uint itemId);

    /// <summary>Reads the persistent <see cref="StoredAffixPayload"/> embedded on a Charged Orb of Siphoning.</summary>
    Task<StoredAffixPayload?> ReadStoredAffixAsync(
        string connectionId,
        uint currencyInventoryId,
        uint currencyItemId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically steals explicit affix <paramref name="affixIndex"/> from the source item, consumes 1 Empty Orb,
    /// creates a persistent Charged Orb of Siphoning holding the stolen mod, persists to save storage, and refreshes inventory live.
    /// </summary>
    Task<SiphonStealCommitResult> CommitStealAsync(
        string connectionId,
        string characterName,
        uint currencyInventoryId,
        uint currencyItemId,
        uint targetInventoryId,
        uint targetItemId,
        int affixIndex,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically replaces explicit affix <paramref name="replaceIndex"/> on the target item with the Charged Orb's stored mod
    /// (preserving exact rolled values), consumes the Charged Orb, persists to save storage, and refreshes inventory live.
    /// </summary>
    Task<SiphonImbueCommitResult> CommitImbueAsync(
        string connectionId,
        string characterName,
        uint currencyInventoryId,
        uint currencyItemId,
        uint targetInventoryId,
        uint targetItemId,
        int replaceIndex,
        CancellationToken cancellationToken = default);

    /// <summary>Sends a human-readable chat/system message to the player (used for vanilla fallback and error feedback).</summary>
    Task TellPlayerAsync(string connectionId, string message, CancellationToken cancellationToken = default);
}

/// <summary>
/// Thread-safe in-memory world backend for standalone hosting and comprehensive unit/E2E verification
/// of the full 5-step Orb of Siphoning workflow without requiring a live LocalExile Gateway process.
/// </summary>
public sealed class SimulatedSiphonWorldBackend : ISiphonWorldBackend
{
    private readonly ConcurrentDictionary<uint, CustomCurrencyItemDefinition> _registered = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<(uint InvId, uint ItemId), PortableItem>> _inventories = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<string>> _chatLog = new(StringComparer.Ordinal);
    private readonly HashSet<uint> _equippedInventories = [2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
    private int _nextItemId = 1000;

    /// <summary>All currently registered custom currency definitions.</summary>
    public IReadOnlyDictionary<uint, CustomCurrencyItemDefinition> RegisteredCurrencies => _registered;

    /// <summary>Returns all chat lines sent to <paramref name="connectionId"/>.</summary>
    public IReadOnlyList<string> GetChatLog(string connectionId)
    {
        if (!_chatLog.TryGetValue(connectionId, out var list))
            return [];
        lock (list) { return [.. list]; }
    }

    /// <summary>Places a test item into <paramref name="connectionId"/>'s simulated inventory.</summary>
    public PortableItem AddItem(string connectionId, PortableItem item)
    {
        var inv = _inventories.GetOrAdd(connectionId, _ => new ConcurrentDictionary<(uint, uint), PortableItem>());
        inv[(item.InventoryId, item.ItemId)] = item;
        return item;
    }

    /// <summary>Retrieves a simulated item by inventory &amp; item ID, or <c>null</c>.</summary>
    public PortableItem? GetItem(string connectionId, uint inventoryId, uint itemId) =>
        _inventories.TryGetValue(connectionId, out var inv) && inv.TryGetValue((inventoryId, itemId), out var item)
            ? item
            : null;

    /// <summary>Returns all items currently in <paramref name="connectionId"/>'s simulated inventories.</summary>
    public IReadOnlyList<PortableItem> GetAllItems(string connectionId) =>
        _inventories.TryGetValue(connectionId, out var inv) ? [.. inv.Values] : [];

    /// <summary>Resolves the <c>TypeHash</c> of a simulated item (matches <see cref="LocalExileCurrencyRegistrar.HandleUseItemOnPacketAsync"/>).</summary>
    public uint? ResolveTypeHash(string connectionId, uint inventoryId, uint itemId) =>
        GetItem(connectionId, inventoryId, itemId)?.TypeHash;

    /// <summary>Locates the first stack of <paramref name="typeHash"/> in <paramref name="connectionId"/>'s simulated inventory.</summary>
    public (uint InventoryId, uint ItemId)? LocateStack(string connectionId, uint typeHash)
    {
        if (!_inventories.TryGetValue(connectionId, out var inv))
            return null;

        PortableItem? found = inv.Values.FirstOrDefault(i => i.TypeHash == typeHash && i.StackSize > 0);
        return found is null ? null : (found.InventoryId, found.ItemId);
    }

    public IDisposable RegisterCurrency(CustomCurrencyItemDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        _registered[definition.TypeHash] = definition;
        return new ActionDisposable(() => _registered.TryRemove(definition.TypeHash, out _));
    }

    public Task<(bool Success, string Message, uint GrantedItemId)> GrantEmptyOrbsAsync(
        string connectionId,
        string characterName,
        ushort count,
        CancellationToken cancellationToken = default)
    {
        var def = SiphonServerMod.EmptyOrbDefinition;
        ushort clamped = Math.Clamp(count, (ushort)1, def.MaxStackSize);
        var inv = _inventories.GetOrAdd(connectionId, _ => new ConcurrentDictionary<(uint, uint), PortableItem>());

        // Merge into an existing empty orb stack in main inventory (inventoryId = 1) if room exists.
        PortableItem? existing = inv.Values.FirstOrDefault(i =>
            i.InventoryId == 1 && i.TypeHash == def.TypeHash && i.StackSize + clamped <= def.MaxStackSize);
        if (existing is not null)
        {
            PortableItem merged = existing with { StackSize = (ushort)(existing.StackSize + clamped) };
            inv[(1, existing.ItemId)] = merged;
            return Task.FromResult((true, $"Granted {clamped}x {def.DisplayName} (stack now {merged.StackSize}).", existing.ItemId));
        }

        uint newId = (uint)Interlocked.Increment(ref _nextItemId);
        var orb = new PortableItem(
            ItemId: newId,
            InventoryId: 1,
            TypeHash: def.TypeHash,
            ItemName: def.DisplayName,
            BaseType: def.DisplayName,
            ItemClass: def.ItemClassName,
            Rarity: ItemRarityKind.Normal,
            ItemLevel: 1,
            Identified: true,
            Corrupted: false,
            Mirrored: false,
            FracturedCount: 0,
            StackSize: clamped,
            Mods: [],
            Components: def.Components,
            VisualIdentity: def.VisualIdentityHash16);
        inv[(1, newId)] = orb;
        return Task.FromResult((true, $"Granted {clamped}x {def.DisplayName}.", newId));
    }

    public Task<ItemAffixView?> InspectItemAsync(
        string connectionId,
        uint inventoryId,
        uint itemId,
        CancellationToken cancellationToken = default)
    {
        PortableItem? item = GetItem(connectionId, inventoryId, itemId);
        return Task.FromResult(item is null ? null : LocalExileItemAffixService.Inspect(item));
    }

    public bool IsItemEquipped(string connectionId, uint inventoryId, uint itemId) =>
        _equippedInventories.Contains(inventoryId);

    public Task<StoredAffixPayload?> ReadStoredAffixAsync(
        string connectionId,
        uint currencyInventoryId,
        uint currencyItemId,
        CancellationToken cancellationToken = default)
    {
        PortableItem? orb = GetItem(connectionId, currencyInventoryId, currencyItemId);
        return Task.FromResult(orb is null ? null : LocalExileItemAffixService.ReadStoredAffix(orb));
    }

    public Task<SiphonStealCommitResult> CommitStealAsync(
        string connectionId,
        string characterName,
        uint currencyInventoryId,
        uint currencyItemId,
        uint targetInventoryId,
        uint targetItemId,
        int affixIndex,
        CancellationToken cancellationToken = default)
    {
        if (!_inventories.TryGetValue(connectionId, out var inv))
            return Task.FromResult(new SiphonStealCommitResult(false, "Inventory not found."));

        if (!inv.TryGetValue((currencyInventoryId, currencyItemId), out PortableItem? orb) ||
            orb.TypeHash != SiphonServerMod.EmptyOrbDefinition.TypeHash || orb.StackSize < 1)
            return Task.FromResult(new SiphonStealCommitResult(false, "Orb of Siphoning is no longer in your inventory."));

        if (!inv.TryGetValue((targetInventoryId, targetItemId), out PortableItem? target))
            return Task.FromResult(new SiphonStealCommitResult(false, "Target item is no longer in your inventory."));

        uint chargedId = (uint)Interlocked.Increment(ref _nextItemId);
        AffixStealResult steal = LocalExileItemAffixService.StealAffix(target, affixIndex, SiphonServerMod.ChargedOrbDefinition, chargedId);
        if (!steal.Success || steal.UpdatedSourceItem is null || steal.ChargedCurrencyItem is null || steal.StolenAffix is null)
            return Task.FromResult(new SiphonStealCommitResult(false, steal.Error ?? "Failed to siphon modifier."));

        // Consume 1 empty orb, update source item, and place newly created Charged Orb in main inventory (1).
        if (orb.StackSize <= 1)
            inv.TryRemove((currencyInventoryId, currencyItemId), out _);
        else
            inv[(currencyInventoryId, currencyItemId)] = orb with { StackSize = (ushort)(orb.StackSize - 1) };

        inv[(targetInventoryId, targetItemId)] = steal.UpdatedSourceItem;
        PortableItem chargedInBag = steal.ChargedCurrencyItem with { InventoryId = 1 };
        inv[(1, chargedId)] = chargedInBag;

        return Task.FromResult(new SiphonStealCommitResult(
            Success: true,
            Error: null,
            StolenAffix: steal.StolenAffix,
            CreatedChargedItemId: chargedId,
            UpdatedSourceView: steal.UpdatedSourceView));
    }

    public Task<SiphonImbueCommitResult> CommitImbueAsync(
        string connectionId,
        string characterName,
        uint currencyInventoryId,
        uint currencyItemId,
        uint targetInventoryId,
        uint targetItemId,
        int replaceIndex,
        CancellationToken cancellationToken = default)
    {
        if (!_inventories.TryGetValue(connectionId, out var inv))
            return Task.FromResult(new SiphonImbueCommitResult(false, "Inventory not found."));

        if (!inv.TryGetValue((currencyInventoryId, currencyItemId), out PortableItem? charged) ||
            charged.TypeHash != SiphonServerMod.ChargedOrbDefinition.TypeHash)
            return Task.FromResult(new SiphonImbueCommitResult(false, "Charged Orb of Siphoning is no longer in your inventory."));

        StoredAffixPayload? stored = LocalExileItemAffixService.ReadStoredAffix(charged);
        if (stored is null)
            return Task.FromResult(new SiphonImbueCommitResult(false, "Charged Orb of Siphoning contains no stored modifier."));

        if (!inv.TryGetValue((targetInventoryId, targetItemId), out PortableItem? target))
            return Task.FromResult(new SiphonImbueCommitResult(false, "Target item is no longer in your inventory."));

        AffixReplaceResult replace = LocalExileItemAffixService.ReplaceAffix(target, replaceIndex, stored);
        if (!replace.Success || replace.UpdatedTargetItem is null)
            return Task.FromResult(new SiphonImbueCommitResult(false, replace.Error ?? "Failed to imbue modifier."));

        inv.TryRemove((currencyInventoryId, currencyItemId), out _);
        inv[(targetInventoryId, targetItemId)] = replace.UpdatedTargetItem;

        return Task.FromResult(new SiphonImbueCommitResult(
            Success: true,
            Error: null,
            ReplacedAffix: replace.ReplacedAffix,
            AppliedAffix: stored,
            UpdatedTargetView: replace.UpdatedTargetView));
    }

    public Task TellPlayerAsync(string connectionId, string message, CancellationToken cancellationToken = default)
    {
        var list = _chatLog.GetOrAdd(connectionId, _ => []);
        lock (list) { list.Add(message); }
        return Task.CompletedTask;
    }

    private sealed class ActionDisposable(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;
        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }
}

/// <summary>
/// Server half of the Orb of Siphoning dual-side mod.
/// Orchestrates the complete 5-step Steal -&gt; Store on Charged Orb -&gt; Imbue -&gt; Consume flow via
/// <see cref="LocalExileCurrencyRegistrar"/> and <see cref="LocalExileItemAffixService"/>, with full
/// NativeMods Recombinator-style picker UI AND numbered chat fallback for vanilla 3.29 clients.
/// </summary>
public sealed class SiphonServerMod : IDisposable
{
    /// <summary>Appended 3.29.3d currency definition for the empty Orb of Siphoning.</summary>
    public static readonly CustomCurrencyItemDefinition EmptyOrbDefinition = new()
    {
        BaseItemTypesRowIndex = SiphonProtocol.EmptyOrbBaseItemTypesRow,
        ItemVisualIdentityRowIndex = SiphonProtocol.EmptyOrbVisualRow,
        CurrencyItemsRowIndex = SiphonProtocol.EmptyOrbCurrencyRow,
        BaseItemTypeId = SiphonProtocol.EmptyOrbBaseId,
        DisplayName = SiphonProtocol.EmptyOrbDisplayName,
        VisualIdentityId = SiphonProtocol.EmptyOrbVisualId,
        VisualIdentityHash16 = SiphonProtocol.EmptyOrbVisualHash16,
        IconDdsPath = SiphonProtocol.EmptyOrbIconPath,
        MaxStackSize = SiphonProtocol.EmptyOrbMaxStack,
        CurrencyUseType = CustomCurrencyItemDefinition.CurrencyUseTypeApplyToItem,
        CurrencyAction = CustomCurrencyItemDefinition.DefaultMagicOrRareTargetAction,
        Directions = SiphonProtocol.EmptyOrbDirections,
        Description = SiphonProtocol.EmptyOrbDescription,
        SupportsStoredMods = false
    };

    /// <summary>Appended 3.29.3d mod-bearing currency definition for the Charged Orb of Siphoning.</summary>
    public static readonly CustomCurrencyItemDefinition ChargedOrbDefinition = new()
    {
        BaseItemTypesRowIndex = SiphonProtocol.ChargedOrbBaseItemTypesRow,
        ItemVisualIdentityRowIndex = SiphonProtocol.ChargedOrbVisualRow,
        CurrencyItemsRowIndex = SiphonProtocol.ChargedOrbCurrencyRow,
        BaseItemTypeId = SiphonProtocol.ChargedOrbBaseId,
        DisplayName = SiphonProtocol.ChargedOrbDisplayName,
        VisualIdentityId = SiphonProtocol.ChargedOrbVisualId,
        VisualIdentityHash16 = SiphonProtocol.ChargedOrbVisualHash16,
        IconDdsPath = SiphonProtocol.ChargedOrbIconPath,
        MaxStackSize = SiphonProtocol.ChargedOrbMaxStack,
        CurrencyUseType = CustomCurrencyItemDefinition.CurrencyUseTypeApplyToItem,
        CurrencyAction = CustomCurrencyItemDefinition.DefaultMagicOrRareTargetAction,
        Directions = SiphonProtocol.ChargedOrbDirections,
        Description = SiphonProtocol.ChargedOrbDescription,
        SupportsStoredMods = true
    };

    private sealed record PendingSiphonSession(
        string SessionToken,
        string ConnectionId,
        string CharacterName,
        SiphonPickerMode Mode,
        uint CurrencyInventoryId,
        uint CurrencyItemId,
        uint TargetInventoryId,
        uint TargetItemId,
        StoredAffixPayload? StoredAffix,
        IReadOnlyList<SiphonAffixOption> Options);

    private readonly ClientBridgeHost _bridge;
    private readonly ISiphonWorldBackend _backend;
    private readonly LocalExileCurrencyRegistrar _registrar;
    private readonly bool _ownsRegistrar;
    private readonly ConcurrentDictionary<string, PendingSiphonSession> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _connectionCharacters = new(StringComparer.Ordinal);
    private readonly List<IDisposable> _subscriptions = [];
    private long _sessionCounter;
    private bool _disposed;

    public ISiphonWorldBackend Backend => _backend;
    public LocalExileCurrencyRegistrar CurrencyRegistrar => _registrar;

    public SiphonServerMod(
        ClientBridgeHost bridge,
        ISiphonWorldBackend? backend = null,
        LocalExileCurrencyRegistrar? registrar = null)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _backend = backend ?? new SimulatedSiphonWorldBackend();
        _registrar = registrar ?? new LocalExileCurrencyRegistrar();
        _ownsRegistrar = registrar is null;
    }

    /// <summary>Registers both custom currencies and attaches typed client bridge and currency-use handlers.</summary>
    public void Attach()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_subscriptions.Count > 0)
            return;

        _subscriptions.Add(_registrar.Register(EmptyOrbDefinition, HandleEmptyOrbUsedAsync));
        _subscriptions.Add(_registrar.Register(ChargedOrbDefinition, HandleChargedOrbUsedAsync));
        _subscriptions.Add(_backend.RegisterCurrency(EmptyOrbDefinition));
        _subscriptions.Add(_backend.RegisterCurrency(ChargedOrbDefinition));

        _subscriptions.Add(_bridge.On<SelectSiphonAffixRequest>(SiphonProtocol.ChannelName, (session, req) =>
        {
            if (!session.HasClientMod(SiphonProtocol.ModId))
                return;

            HandleAffixSelectionAsync(session.ConnectionId, req.SessionToken, req.AffixIndex).GetAwaiter().GetResult();
        }));

        _subscriptions.Add(_bridge.On<CancelSiphonPicker>(SiphonProtocol.ChannelName, (session, req) =>
        {
            if (!session.HasClientMod(SiphonProtocol.ModId))
                return;

            HandleCancelPickerAsync(session.ConnectionId, req.SessionToken).GetAwaiter().GetResult();
        }));
    }

    /// <summary>Associates a connection ID with its active character name.</summary>
    public void BindConnection(string connectionId, string characterName)
    {
        if (!string.IsNullOrWhiteSpace(connectionId) && !string.IsNullOrWhiteSpace(characterName))
            _connectionCharacters[connectionId] = characterName;
    }

    /// <summary>Clears transient pending picker state when a player disconnects.</summary>
    public void OnPlayerLeft(string connectionId)
    {
        _pending.TryRemove(connectionId, out _);
        _connectionCharacters.TryRemove(connectionId, out _);
    }

    /// <summary>Returns <c>true</c> if <paramref name="connectionId"/> currently has an open pending Siphon picker session.</summary>
    public bool HasPendingSession(string connectionId) => _pending.ContainsKey(connectionId);

    /// <summary>
    /// Handles the <c>/siphon</c> chat command:
    /// <list type="bullet">
    ///   <item><c>/siphon give [1..20]</c> — grants empty Orbs of Siphoning</item>
    ///   <item><c>/siphon pick &lt;1..N&gt;</c> — vanilla-client fallback to choose a modifier from an active prompt</item>
    ///   <item><c>/siphon cancel</c> — cancels an active prompt without spending an orb</item>
    ///   <item><c>/siphon</c> / <c>/siphon help</c> — displays usage instructions</item>
    /// </list>
    /// </summary>
    public async Task<string> HandleSiphonCommandAsync(
        string connectionId,
        string characterName,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        BindConnection(connectionId, characterName);
        string sub = args.Count > 0 ? args[0].Trim().ToLowerInvariant() : "help";

        switch (sub)
        {
            case "give":
                ushort count = 5;
                if (args.Count > 1 && ushort.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort parsed))
                    count = Math.Clamp(parsed, (ushort)1, EmptyOrbDefinition.MaxStackSize);

                var (_, message, _) = await _backend.GrantEmptyOrbsAsync(connectionId, characterName, count, cancellationToken).ConfigureAwait(false);
                return message;

            case "pick":
            case "select":
                if (!_pending.TryGetValue(connectionId, out PendingSiphonSession? active))
                    return "No active Orb of Siphoning selection. Right-click an Orb of Siphoning and left-click a Magic or Rare item first.";

                if (args.Count < 2 || !int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int oneBased) || oneBased < 1)
                    return $"Usage: /siphon pick <1..{active.Options.Count}> (or /siphon cancel)";

                return await HandleAffixSelectionAsync(connectionId, active.SessionToken, oneBased - 1, cancellationToken).ConfigureAwait(false);

            case "cancel":
                if (!_pending.TryGetValue(connectionId, out PendingSiphonSession? pending))
                    return "No active Orb of Siphoning selection to cancel.";

                return await HandleCancelPickerAsync(connectionId, pending.SessionToken, cancellationToken).ConfigureAwait(false);

            default:
                return "Orb of Siphoning: /siphon give [1..20] — grant empty orbs | Right-click Empty Orb -> click Magic/Rare item to steal a mod into a Charged Orb | Right-click Charged Orb -> click Magic/Rare item to replace a mod | Vanilla fallback: /siphon pick <1..N> or /siphon cancel.";
        }
    }

    /// <summary>
    /// Step 1 &amp; 2: Player right-clicked an Empty Orb of Siphoning and left-clicked a target item.
    /// Validates eligibility and opens the Steal modifier picker (or sends numbered chat fallback).
    /// </summary>
    public async Task<bool> HandleEmptyOrbUsedAsync(CurrencyUseContext ctx)
    {
        BindConnection(ctx.ConnectionId, ctx.CharacterName);

        if (_backend.IsItemEquipped(ctx.ConnectionId, ctx.TargetInventoryId, ctx.TargetItemId))
        {
            await _backend.TellPlayerAsync(ctx.ConnectionId, "Orb of Siphoning: Unequip the item before siphoning a modifier.").ConfigureAwait(false);
            return true;
        }

        ItemAffixView? view = await _backend.InspectItemAsync(ctx.ConnectionId, ctx.TargetInventoryId, ctx.TargetItemId).ConfigureAwait(false);
        if (view is null)
        {
            await _backend.TellPlayerAsync(ctx.ConnectionId, "Orb of Siphoning: Target item could not be found.").ConfigureAwait(false);
            return true;
        }

        if (LocalExileItemAffixService.ValidateStealTarget(view) is { } reason)
        {
            await _backend.TellPlayerAsync(ctx.ConnectionId, $"Orb of Siphoning: {reason}").ConfigureAwait(false);
            return true;
        }

        var options = view.Affixes.Select(a => new SiphonAffixOption(
            Index: a.Index,
            ModId: a.ModId,
            DisplayName: a.DisplayName,
            StatText: a.StatText,
            IsPrefix: a.IsPrefix,
            IsSuffix: a.IsSuffix,
            Group: a.Group,
            Tier: a.Tier,
            RolledValues: [.. a.RolledValues],
            Selectable: !a.IsFractured,
            DisabledReason: a.IsFractured ? "Fractured modifier cannot be siphoned." : null)).ToList();

        string token = NextSessionToken();
        var pending = new PendingSiphonSession(
            SessionToken: token,
            ConnectionId: ctx.ConnectionId,
            CharacterName: ctx.CharacterName,
            Mode: SiphonPickerMode.Steal,
            CurrencyInventoryId: ctx.CurrencyInventoryId,
            CurrencyItemId: ctx.CurrencyItemId,
            TargetInventoryId: ctx.TargetInventoryId,
            TargetItemId: ctx.TargetItemId,
            StoredAffix: null,
            Options: options);
        _pending[ctx.ConnectionId] = pending;

        await DispatchPickerAsync(ctx.ConnectionId, pending, view).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Step 4: Player right-clicked a Charged Orb of Siphoning and left-clicked a destination item.
    /// Reads the stored mod from the Charged Orb, checks compatibility against each destination affix,
    /// and opens the Imbue modifier picker (or sends numbered chat fallback).
    /// </summary>
    public async Task<bool> HandleChargedOrbUsedAsync(CurrencyUseContext ctx)
    {
        BindConnection(ctx.ConnectionId, ctx.CharacterName);

        if (_backend.IsItemEquipped(ctx.ConnectionId, ctx.TargetInventoryId, ctx.TargetItemId))
        {
            await _backend.TellPlayerAsync(ctx.ConnectionId, "Charged Orb of Siphoning: Unequip the destination item before imbuing.").ConfigureAwait(false);
            return true;
        }

        StoredAffixPayload? stored = await _backend.ReadStoredAffixAsync(ctx.ConnectionId, ctx.CurrencyInventoryId, ctx.CurrencyItemId).ConfigureAwait(false);
        if (stored is null)
        {
            await _backend.TellPlayerAsync(ctx.ConnectionId, "Charged Orb of Siphoning: This orb has no stored modifier.").ConfigureAwait(false);
            return true;
        }

        ItemAffixView? view = await _backend.InspectItemAsync(ctx.ConnectionId, ctx.TargetInventoryId, ctx.TargetItemId).ConfigureAwait(false);
        if (view is null)
        {
            await _backend.TellPlayerAsync(ctx.ConnectionId, "Charged Orb of Siphoning: Destination item could not be found.").ConfigureAwait(false);
            return true;
        }

        if (LocalExileItemAffixService.ValidateStealTarget(view) is { } reason)
        {
            await _backend.TellPlayerAsync(ctx.ConnectionId, $"Charged Orb of Siphoning: {reason}").ConfigureAwait(false);
            return true;
        }

        var options = new List<SiphonAffixOption>(view.Affixes.Count);
        foreach (ItemAffixSnapshot affix in view.Affixes)
        {
            (bool allowed, string? disabledReason) = LocalExileItemAffixService.CanReplaceAffix(view, affix.Index, stored);
            options.Add(new SiphonAffixOption(
                Index: affix.Index,
                ModId: affix.ModId,
                DisplayName: affix.DisplayName,
                StatText: affix.StatText,
                IsPrefix: affix.IsPrefix,
                IsSuffix: affix.IsSuffix,
                Group: affix.Group,
                Tier: affix.Tier,
                RolledValues: [.. affix.RolledValues],
                Selectable: allowed,
                DisabledReason: disabledReason));
        }

        if (options.All(o => !o.Selectable))
        {
            string why = options.FirstOrDefault()?.DisabledReason ?? "No compatible modifier slot on target item.";
            await _backend.TellPlayerAsync(ctx.ConnectionId, $"Charged Orb of Siphoning: Cannot imbue onto {view.ItemName} — {why}").ConfigureAwait(false);
            return true;
        }

        string token = NextSessionToken();
        var pending = new PendingSiphonSession(
            SessionToken: token,
            ConnectionId: ctx.ConnectionId,
            CharacterName: ctx.CharacterName,
            Mode: SiphonPickerMode.Imbue,
            CurrencyInventoryId: ctx.CurrencyInventoryId,
            CurrencyItemId: ctx.CurrencyItemId,
            TargetInventoryId: ctx.TargetInventoryId,
            TargetItemId: ctx.TargetItemId,
            StoredAffix: stored,
            Options: options);
        _pending[ctx.ConnectionId] = pending;

        await DispatchPickerAsync(ctx.ConnectionId, pending, view).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Step 3 &amp; Step 5: Executes the player's confirmed modifier selection (Steal or Imbue).
    /// </summary>
    public async Task<string> HandleAffixSelectionAsync(
        string connectionId,
        string sessionToken,
        int affixIndex,
        CancellationToken cancellationToken = default)
    {
        if (!_pending.TryGetValue(connectionId, out PendingSiphonSession? pending) ||
            !string.Equals(pending.SessionToken, sessionToken, StringComparison.Ordinal))
        {
            const string staleMsg = "Siphon session expired or invalid. Right-click the orb again.";
            await NotifyResultAsync(connectionId, sessionToken, false, SiphonPickerMode.Steal, staleMsg, closePicker: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            return staleMsg;
        }

        SiphonAffixOption? chosenOption = pending.Options.FirstOrDefault(o => o.Index == affixIndex);
        if (chosenOption is null || !chosenOption.Selectable)
        {
            string err = chosenOption?.DisabledReason ?? $"Invalid modifier selection ({affixIndex}).";
            await NotifyResultAsync(connectionId, sessionToken, false, pending.Mode, err, closePicker: false, cancellationToken: cancellationToken).ConfigureAwait(false);
            return err;
        }

        if (pending.Mode == SiphonPickerMode.Steal)
        {
            SiphonStealCommitResult commit = await _backend.CommitStealAsync(
                connectionId,
                pending.CharacterName,
                pending.CurrencyInventoryId,
                pending.CurrencyItemId,
                pending.TargetInventoryId,
                pending.TargetItemId,
                affixIndex,
                cancellationToken).ConfigureAwait(false);

            if (!commit.Success || commit.StolenAffix is null)
            {
                string err = commit.Error ?? "Failed to siphon modifier.";
                _pending.TryRemove(connectionId, out _);
                await NotifyResultAsync(connectionId, sessionToken, false, SiphonPickerMode.Steal, err, closePicker: true, cancellationToken: cancellationToken).ConfigureAwait(false);
                return err;
            }

            _pending.TryRemove(connectionId, out _);
            string okMsg = $"Siphoned [{commit.StolenAffix.SlotTag}] {commit.StolenAffix.DisplayText} into a Charged Orb of Siphoning!";
            await NotifyResultAsync(
                connectionId,
                sessionToken,
                success: true,
                mode: SiphonPickerMode.Steal,
                message: okMsg,
                resultingStored: ToDto(commit.StolenAffix),
                createdChargedItemId: commit.CreatedChargedItemId,
                closePicker: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return okMsg;
        }
        else
        {
            SiphonImbueCommitResult commit = await _backend.CommitImbueAsync(
                connectionId,
                pending.CharacterName,
                pending.CurrencyInventoryId,
                pending.CurrencyItemId,
                pending.TargetInventoryId,
                pending.TargetItemId,
                affixIndex,
                cancellationToken).ConfigureAwait(false);

            if (!commit.Success || commit.AppliedAffix is null)
            {
                string err = commit.Error ?? "Failed to imbue modifier.";
                _pending.TryRemove(connectionId, out _);
                await NotifyResultAsync(connectionId, sessionToken, false, SiphonPickerMode.Imbue, err, closePicker: true, cancellationToken: cancellationToken).ConfigureAwait(false);
                return err;
            }

            _pending.TryRemove(connectionId, out _);
            string replacedText = commit.ReplacedAffix?.StatText ?? "previous modifier";
            string okMsg = $"Imbued [{commit.AppliedAffix.SlotTag}] {commit.AppliedAffix.DisplayText} (replaced {replacedText})!";
            await NotifyResultAsync(
                connectionId,
                sessionToken,
                success: true,
                mode: SiphonPickerMode.Imbue,
                message: okMsg,
                resultingStored: ToDto(commit.AppliedAffix),
                closePicker: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return okMsg;
        }
    }

    /// <summary>Cancels the active Siphon Picker session without consuming any orb or modifying any item.</summary>
    public async Task<string> HandleCancelPickerAsync(
        string connectionId,
        string sessionToken,
        CancellationToken cancellationToken = default)
    {
        SiphonPickerMode mode = SiphonPickerMode.Steal;
        if (_pending.TryGetValue(connectionId, out PendingSiphonSession? existing) &&
            string.Equals(existing.SessionToken, sessionToken, StringComparison.Ordinal))
        {
            mode = existing.Mode;
            _pending.TryRemove(connectionId, out _);
        }

        const string msg = "Orb of Siphoning cancelled. No orbs were consumed.";
        await NotifyResultAsync(connectionId, sessionToken, success: false, mode: mode, message: msg, closePicker: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        return msg;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        foreach (IDisposable sub in _subscriptions)
            sub.Dispose();
        _subscriptions.Clear();
        _pending.Clear();
        _connectionCharacters.Clear();

        if (_ownsRegistrar)
            _registrar.Dispose();
    }

    private async Task DispatchPickerAsync(string connectionId, PendingSiphonSession pending, ItemAffixView view)
    {
        IClientSession session = _bridge.For(connectionId);
        if (session.HasClientMod(SiphonProtocol.ModId))
        {
            string prompt = pending.Mode == SiphonPickerMode.Steal
                ? "Select an explicit modifier to siphon into a Charged Orb."
                : "Select a compatible modifier to replace with the stored modifier.";

            var openMsg = new OpenSiphonPicker(
                SessionToken: pending.SessionToken,
                Mode: pending.Mode,
                CurrencyInventoryId: pending.CurrencyInventoryId,
                CurrencyItemId: pending.CurrencyItemId,
                TargetInventoryId: pending.TargetInventoryId,
                TargetItemId: pending.TargetItemId,
                TargetItemName: view.ItemName,
                TargetBaseType: view.BaseType,
                TargetRarity: view.Rarity.ToString(),
                StoredAffix: pending.StoredAffix is null ? null : ToDto(pending.StoredAffix),
                Affixes: pending.Options,
                PromptText: prompt);

            await session.SendAsync(SiphonProtocol.ChannelName, openMsg).ConfigureAwait(false);
            return;
        }

        // Graceful fallback for vanilla 3.29 clients without NativeMods: numbered chat menu.
        var sb = new StringBuilder();
        if (pending.Mode == SiphonPickerMode.Steal)
        {
            sb.Append(CultureInfo.InvariantCulture, $"[Orb of Siphoning] Select a modifier to steal from {view.ItemName}: ");
        }
        else
        {
            sb.Append(CultureInfo.InvariantCulture, $"[Charged Orb of Siphoning] Stored: [{pending.StoredAffix?.SlotTag}] {pending.StoredAffix?.DisplayText}. Replace on {view.ItemName}: ");
        }

        for (int i = 0; i < pending.Options.Count; i++)
        {
            SiphonAffixOption opt = pending.Options[i];
            if (i > 0) sb.Append(" | ");
            if (opt.Selectable)
                sb.Append(CultureInfo.InvariantCulture, $"({i + 1}) [{opt.SlotTag}] {opt.StatText}");
            else
                sb.Append(CultureInfo.InvariantCulture, $"({i + 1}) [LOCKED] {opt.StatText}");
        }

        sb.Append(" — Type '/siphon pick <number>' or '/siphon cancel'.");
        await _backend.TellPlayerAsync(connectionId, sb.ToString()).ConfigureAwait(false);
    }

    private async Task NotifyResultAsync(
        string connectionId,
        string sessionToken,
        bool success,
        SiphonPickerMode mode,
        string message,
        SiphonStoredAffixDto? resultingStored = null,
        uint? createdChargedItemId = null,
        bool closePicker = true,
        CancellationToken cancellationToken = default)
    {
        IClientSession session = _bridge.For(connectionId);
        if (session.HasClientMod(SiphonProtocol.ModId))
        {
            var note = new SiphonResultNotification(
                SessionToken: sessionToken,
                Success: success,
                Mode: mode,
                Message: message,
                ResultingStoredAffix: resultingStored,
                CreatedChargedItemId: createdChargedItemId,
                ClosePicker: closePicker);
            await session.SendAsync(SiphonProtocol.ChannelName, note, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        await _backend.TellPlayerAsync(connectionId, message, cancellationToken).ConfigureAwait(false);
    }

    private string NextSessionToken() =>
        $"siphon-{Interlocked.Increment(ref _sessionCounter).ToString(CultureInfo.InvariantCulture)}";

    private static SiphonStoredAffixDto ToDto(StoredAffixPayload payload) =>
        new(
            ModId: payload.ModId,
            DisplayName: payload.DisplayName,
            StatText: payload.DisplayText,
            IsPrefix: payload.IsPrefix,
            IsSuffix: payload.IsSuffix,
            Group: payload.Group,
            Tier: payload.Tier,
            RolledValues: [.. payload.RolledValues]);
}
