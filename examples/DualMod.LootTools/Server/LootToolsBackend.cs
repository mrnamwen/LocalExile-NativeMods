using System.Collections.Concurrent;
using DualMod.LootTools.Shared;

namespace DualMod.LootTools.Server;

/// <summary>Configurable server settings for <see cref="LootToolsServerMod"/>.</summary>
public sealed record LootToolsSettings(
    int MagnetRadius = LootToolsProtocol.DefaultMagnetRadiusCells,
    int PickupRadius = LootToolsProtocol.DefaultPickupRadiusCells,
    int MergeRadius = LootToolsProtocol.DefaultMergeRadiusCells,
    int MaxBatchSize = LootToolsProtocol.MaxBatchEntityCount,
    bool AutoMergeEnabled = false,
    double AutoMergeIntervalSeconds = LootToolsProtocol.DefaultAutoMergeIntervalSeconds,
    bool ScopeMergeToFilter = false,
    bool ZoomOverrideEnabled = false,
    float ZoomOverrideValue = LootToolsProtocol.DefaultZoomOverride);

/// <summary>
/// Active player session guard snapshot mirroring LocalExile Gateway's anti-exploit preconditions.
/// </summary>
public sealed record LootSessionState(
    bool CurrentGameWork = true,
    bool Closed = false,
    bool LeavingArea = false,
    bool Paused = false,
    bool PlayerSent = true,
    int PlayerLife = 100,
    bool RageRetired = false,
    bool HardcoreProgressBlocked = false,
    bool AllowsLoot = true,
    bool HasActiveTrade = false,
    bool HasActiveSale = false,
    bool LockstepClient = true,
    bool ExpandedInventoryEnabled = false,
    int PlayerX = 0,
    int PlayerY = 0,
    double AreaTimeSeconds = 0.0)
{
    public bool IsEligibleForLootActions =>
        CurrentGameWork
        && !Closed
        && !LeavingArea
        && !Paused
        && PlayerSent
        && PlayerLife > 0
        && !RageRetired
        && !HardcoreProgressBlocked
        && AllowsLoot
        && !HasActiveTrade
        && !HasActiveSale;
}

/// <summary>Server-side ground world item record used by <see cref="LootToolsServerMod"/>.</summary>
public sealed record LootGroundItem
{
    public uint EntityId { get; init; }
    public int GridX { get; init; }
    public int GridY { get; init; }
    public uint TypeHash { get; init; }
    public string MetadataPath { get; init; } = string.Empty;
    public string BaseName { get; init; } = string.Empty;
    public ushort StackSize { get; init; } = 1;
    public ushort MaxStack { get; init; } = 1;
    public byte Width { get; init; } = 1;
    public byte Height { get; init; } = 1;
    public bool IsGold { get; init; }
    public bool IsQuestItem { get; init; }
    public bool IsTrackedDrop { get; init; }
    public bool Shared { get; init; } = true;
    public string? OwnerConnectionId { get; init; }
    public bool LockstepAdmitted { get; init; } = true;
    public ushort HeaderFlags { get; init; }
    public ushort VisualIdentity { get; init; }
    public bool Corrupted { get; init; }
    public byte Influences { get; init; }
    public byte Quality { get; init; }
    public byte? AlternateQualityType { get; init; }
    public string? ExtraPayloadIdentity { get; init; }

    /// <summary>Effective max stack size on the ground (gold caps at 65,535; stackables at <see cref="MaxStack"/>).</summary>
    public int EffectiveMaxStack => IsGold ? LootToolsProtocol.GoldMaxGroundStack : Math.Max(1, (int)MaxStack);

    /// <summary>Whether this ground item is eligible to merge stacks.</summary>
    public bool IsStackableOnGround => IsGold || MaxStack > 1;

    /// <summary>
    /// Strict stack compatibility key matching LocalExile's <c>ItemDrops.StackKey</c>:
    /// identical <c>TypeHash</c>, <c>HeaderFlags &amp; 0x01ff</c>, <c>VisualIdentity</c>,
    /// <c>Corrupted</c>, <c>Influences</c>, <c>Quality</c>, <c>AlternateQualityType</c>,
    /// and any extra non-stack item payload identity.
    /// </summary>
    public string StackIdentityKey =>
        $"{TypeHash:X8}:{(HeaderFlags & 0x01ff):X4}:{VisualIdentity:X4}:{(Corrupted ? 1 : 0)}:{Influences:X2}:{Quality:X2}:{AlternateQualityType?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}:{ExtraPayloadIdentity ?? string.Empty}";
}

/// <summary>Result of a Loot Magnet relocation trigger.</summary>
public sealed record LootMagnetOutcome(
    bool Success,
    int RelocatedCount,
    int SkippedCount,
    IReadOnlyList<(uint OldEntityId, uint NewEntityId, int GridX, int GridY)> RelocatedItems,
    string Message);

/// <summary>Result of a Ground Loot Merger trigger.</summary>
public sealed record LootMergeOutcome(
    bool Success,
    int GroupsMerged,
    int PilesRemoved,
    int PilesCreated,
    IReadOnlyList<uint> CreatedEntityIds,
    string Message);

/// <summary>Result of a single stackable ground pickup.</summary>
public sealed record GroundStackPickupResult(
    bool PickedUp,
    bool InventoryFull,
    uint RemainderEntityId = 0,
    LootGroundItem? RemainderItem = null);

/// <summary>Result of an AoE Pickup trigger.</summary>
public sealed record AoePickupOutcome(
    bool Success,
    int PickedItemCount,
    int GoldPickedTotal,
    int InventoryFullSkipped,
    int IgnoredOrRejectedCount,
    string Message);

/// <summary>
/// Abstraction bridging <see cref="LootToolsServerMod"/> to either LocalExile Gateway's live <c>InstanceGround</c>/<c>Game</c>
/// APIs (<c>LootToolsLocalExileEntry</c>) or <see cref="InMemoryLootToolsBackend"/> for unit &amp; integration tests.
/// </summary>
public interface ILootToolsBackend
{
    bool TryGetSessionState(string connectionId, out LootSessionState state);

    LootGroundItem? GetGroundItem(string connectionId, uint entityId);

    IReadOnlyList<LootGroundItem> GetGroundItemsWithin(string connectionId, int centerX, int centerY, double radiusCells);

    (int X, int Y) FindWalkableDropCell(string connectionId, int originX, int originY, bool scatterNearPlayer);

    /// <summary>Atomically removes and returns the ground item only if it still matches <paramref name="expected"/>.</summary>
    bool TryTakeGroundItem(string connectionId, uint entityId, LootGroundItem expected, out LootGroundItem claimed);

    /// <summary>Adds a new ground item (for relocation or merged piles) with <paramref name="justDropped"/> control.</summary>
    uint AddGroundItem(string connectionId, LootGroundItem item, bool justDropped);

    /// <summary>Restores a previously claimed ground item under its original id if a merge/transaction aborts.</summary>
    void RestoreGroundItem(string connectionId, LootGroundItem item);

    /// <summary>Syncs S→C <c>0x02BD</c> world object removal.</summary>
    Task SendWorldRemovalAsync(string connectionId, uint entityId, string log, CancellationToken cancellationToken = default);

    /// <summary>Syncs S→C <c>0x02BB</c> + <c>0x01A9</c> ground item creation (never <c>0x01B7</c> Teleport).</summary>
    Task SendGroundItemAsync(string connectionId, uint entityId, LootGroundItem item, bool justDropped, string log, CancellationToken cancellationToken = default);

    /// <summary>Picks up a ground gold pile via <c>Game.PickUpGold</c>.</summary>
    Task<int> TryPickUpGoldAsync(string connectionId, LootGroundItem goldItem, CancellationToken cancellationToken = default);

    /// <summary>Picks up a stackable ground item via <c>Game.TakeGroundStack</c> (syncing removal, inventory update, and <c>Rest</c> with <c>wake: true</c>).</summary>
    Task<GroundStackPickupResult> TryPickUpStackableAsync(string connectionId, LootGroundItem stackItem, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to pick up non-stackable ground items into <c>MainInventory</c> (and <c>ExpandedInventory</c> if enabled),
    /// continuing past large items that do not fit so smaller items can still be picked up, coalescing <c>0x00BF</c>.
    /// </summary>
    Task<(int PickedCount, int FullSkippedCount)> TryPickUpNonStackablesAsync(
        string connectionId,
        IReadOnlyList<LootGroundItem> candidates,
        CancellationToken cancellationToken = default);

    /// <summary>Emits S→C <c>0x0117</c> <c>InstanceItems.InventoryFull()</c> (called at most once per AoE trigger).</summary>
    Task SendInventoryFullAsync(string connectionId, CancellationToken cancellationToken = default);
}

/// <summary>Inventory item stored inside <see cref="InMemoryLootToolsBackend"/> for test verification.</summary>
public sealed record InMemoryInventorySlotItem(
    uint InventoryItemId,
    string Row,
    byte X,
    byte Y,
    LootGroundItem SourceItem,
    ushort StackSize);

/// <summary>
/// Thread-safe, deterministic in-memory <see cref="ILootToolsBackend"/> supporting concurrency/race injection,
/// 12×5 main + 4×5 expanded 2D inventory grids, walkability checks, and full packet audit logs.
/// </summary>
public sealed class InMemoryLootToolsBackend : ILootToolsBackend
{
    public const int MainGridWidth = 12;
    public const int MainGridHeight = 5;
    public const int ExpandedGridWidth = 4;
    public const int ExpandedGridHeight = 5;

    public sealed class PlayerWorldState
    {
        internal object Gate { get; } = new();
        public LootSessionState Session { get; set; } = new();
        public Dictionary<uint, LootGroundItem> Ground { get; } = [];
        public List<InMemoryInventorySlotItem> MainInventory { get; } = [];
        public List<InMemoryInventorySlotItem> ExpandedInventory { get; } = [];
        public int GoldStash { get; set; }
        public Func<int, int, bool>? IsWalkable { get; set; }
        public Action<uint>? OnBeforeTake { get; set; }
        public uint NextGroundEntityId { get; set; } = 0x100000;
        public uint NextInventoryItemId { get; set; } = 1;

        public List<uint> SentWorldRemovals { get; } = [];
        public List<(uint EntityId, LootGroundItem Item, bool JustDropped)> SentGroundItemCreates { get; } = [];
        public List<(string Row, int AddedCount)> SentInventoryChangeBatches { get; } = [];
        public int InventoryFullNotificationsSent { get; set; }
        public int TeleportPacketsSent { get; set; }
    }

    private readonly ConcurrentDictionary<string, PlayerWorldState> _players = new(StringComparer.Ordinal);

    public PlayerWorldState GetOrCreatePlayer(string connectionId) =>
        _players.GetOrAdd(connectionId, static _ => new PlayerWorldState());

    public bool TryGetSessionState(string connectionId, out LootSessionState state)
    {
        if (_players.TryGetValue(connectionId, out PlayerWorldState? p))
        {
            lock (p.Gate)
            {
                state = p.Session;
                return true;
            }
        }

        state = default!;
        return false;
    }

    public LootGroundItem? GetGroundItem(string connectionId, uint entityId)
    {
        if (!_players.TryGetValue(connectionId, out PlayerWorldState? p))
        {
            return null;
        }

        lock (p.Gate)
        {
            return p.Ground.TryGetValue(entityId, out LootGroundItem? item) ? item : null;
        }
    }

    public IReadOnlyList<LootGroundItem> GetGroundItemsWithin(string connectionId, int centerX, int centerY, double radiusCells)
    {
        if (!_players.TryGetValue(connectionId, out PlayerWorldState? p))
        {
            return [];
        }

        double r2 = radiusCells * radiusCells;
        lock (p.Gate)
        {
            return [.. p.Ground.Values
                .Where(g =>
                {
                    double dx = g.GridX - centerX;
                    double dy = g.GridY - centerY;
                    return (dx * dx) + (dy * dy) <= r2;
                })
                .OrderBy(g => g.EntityId)];
        }
    }

    public (int X, int Y) FindWalkableDropCell(string connectionId, int originX, int originY, bool scatterNearPlayer)
    {
        if (!_players.TryGetValue(connectionId, out PlayerWorldState? p))
        {
            return (originX, originY);
        }

        lock (p.Gate)
        {
            Func<int, int, bool>? walkable = p.IsWalkable;
            if (!scatterNearPlayer)
            {
                return walkable is null || walkable(originX, originY)
                    ? (originX, originY)
                    : (p.Session.PlayerX, p.Session.PlayerY);
            }

            (int dx, int dy)[] offsets =
            [
                (2, 0), (-2, 0), (0, 2), (0, -2),
                (2, 2), (-2, 2), (2, -2), (-2, -2),
                (1, 0), (0, 1), (-1, 0), (0, -1),
            ];
            foreach ((int dx, int dy) in offsets)
            {
                int cx = originX + dx;
                int cy = originY + dy;
                if (walkable is null || walkable(cx, cy))
                {
                    return (cx, cy);
                }
            }

            return (originX, originY);
        }
    }

    public bool TryTakeGroundItem(string connectionId, uint entityId, LootGroundItem expected, out LootGroundItem claimed)
    {
        claimed = null!;
        if (!_players.TryGetValue(connectionId, out PlayerWorldState? p))
        {
            return false;
        }

        p.OnBeforeTake?.Invoke(entityId);

        lock (p.Gate)
        {
            if (!p.Ground.TryGetValue(entityId, out LootGroundItem? current) || !ReferenceEquals(current, expected))
            {
                return false;
            }

            p.Ground.Remove(entityId);
            claimed = current;
            return true;
        }
    }

    public uint AddGroundItem(string connectionId, LootGroundItem item, bool justDropped)
    {
        PlayerWorldState p = GetOrCreatePlayer(connectionId);
        lock (p.Gate)
        {
            while (p.Ground.ContainsKey(p.NextGroundEntityId))
            {
                p.NextGroundEntityId++;
            }

            uint id = p.NextGroundEntityId++;
            p.Ground[id] = item with { EntityId = id };
            return id;
        }
    }

    public void RestoreGroundItem(string connectionId, LootGroundItem item)
    {
        PlayerWorldState p = GetOrCreatePlayer(connectionId);
        lock (p.Gate)
        {
            p.Ground[item.EntityId] = item;
        }
    }

    public Task SendWorldRemovalAsync(string connectionId, uint entityId, string log, CancellationToken cancellationToken = default)
    {
        _ = log;
        _ = cancellationToken;
        if (_players.TryGetValue(connectionId, out PlayerWorldState? p))
        {
            lock (p.Gate)
            {
                p.SentWorldRemovals.Add(entityId);
            }
        }

        return Task.CompletedTask;
    }

    public Task SendGroundItemAsync(string connectionId, uint entityId, LootGroundItem item, bool justDropped, string log, CancellationToken cancellationToken = default)
    {
        _ = log;
        _ = cancellationToken;
        if (_players.TryGetValue(connectionId, out PlayerWorldState? p))
        {
            lock (p.Gate)
            {
                p.SentGroundItemCreates.Add((entityId, item with { EntityId = entityId }, justDropped));
            }
        }

        return Task.CompletedTask;
    }

    public async Task<int> TryPickUpGoldAsync(string connectionId, LootGroundItem goldItem, CancellationToken cancellationToken = default)
    {
        if (!goldItem.IsGold || !TryTakeGroundItem(connectionId, goldItem.EntityId, goldItem, out LootGroundItem claimed))
        {
            return 0;
        }

        PlayerWorldState p = GetOrCreatePlayer(connectionId);
        lock (p.Gate)
        {
            p.GoldStash += claimed.StackSize;
        }

        await SendWorldRemovalAsync(connectionId, claimed.EntityId, "gold aoe-looted", cancellationToken);
        return claimed.StackSize;
    }

    public async Task<GroundStackPickupResult> TryPickUpStackableAsync(string connectionId, LootGroundItem stackItem, CancellationToken cancellationToken = default)
    {
        if (!_players.TryGetValue(connectionId, out PlayerWorldState? p))
        {
            return new GroundStackPickupResult(false, false);
        }

        if (!stackItem.IsStackableOnGround || stackItem.IsGold)
        {
            return new GroundStackPickupResult(false, false);
        }

        // Preview room across MainInventory (+ ExpandedInventory if enabled).
        int roomAvailable;
        lock (p.Gate)
        {
            roomAvailable = ComputeStackRoomLocked(p, stackItem);
        }

        if (roomAvailable < stackItem.StackSize)
        {
            // Matches LocalExile's TakeGroundStack preview behavior: returns full if entire stack cannot fit.
            return new GroundStackPickupResult(false, InventoryFull: true);
        }

        if (!TryTakeGroundItem(connectionId, stackItem.EntityId, stackItem, out LootGroundItem claimed))
        {
            return new GroundStackPickupResult(false, InventoryFull: false);
        }

        int leftover;
        uint restId = 0;
        LootGroundItem? restItem = null;
        lock (p.Gate)
        {
            leftover = PlaceStackableLocked(p, claimed);
            if (leftover > 0)
            {
                restItem = claimed with { StackSize = (ushort)leftover };
                while (p.Ground.ContainsKey(p.NextGroundEntityId))
                {
                    p.NextGroundEntityId++;
                }

                restId = p.NextGroundEntityId++;
                restItem = restItem with { EntityId = restId };
                p.Ground[restId] = restItem;
            }
        }

        await SendWorldRemovalAsync(connectionId, claimed.EntityId, "stackable aoe-looted", cancellationToken);
        if (restItem is not null)
        {
            // Sync remainder via SendGroundItem (wake: true) rather than omitting wake!
            await SendGroundItemAsync(connectionId, restId, restItem, justDropped: false, "stackable remainder", cancellationToken);
        }

        return new GroundStackPickupResult(true, InventoryFull: false, restId, restItem);
    }

    public async Task<(int PickedCount, int FullSkippedCount)> TryPickUpNonStackablesAsync(
        string connectionId,
        IReadOnlyList<LootGroundItem> candidates,
        CancellationToken cancellationToken = default)
    {
        if (!_players.TryGetValue(connectionId, out PlayerWorldState? p) || candidates.Count == 0)
        {
            return (0, 0);
        }

        int picked = 0;
        int fullSkipped = 0;
        List<uint> removedGroundIds = [];
        int mainAdded = 0;
        int expandedAdded = 0;

        foreach (LootGroundItem candidate in candidates)
        {
            (string Row, byte X, byte Y)? freeCell;
            lock (p.Gate)
            {
                freeCell = FindFreeCellLocked(p, candidate.Width, candidate.Height);
            }

            if (freeCell is null)
            {
                // Continue checking smaller candidates even if a large item did not fit.
                fullSkipped++;
                continue;
            }

            if (!TryTakeGroundItem(connectionId, candidate.EntityId, candidate, out LootGroundItem claimed))
            {
                continue;
            }

            lock (p.Gate)
            {
                // Re-verify cell under lock; restore ground item if raced.
                (string Row, byte X, byte Y)? cell = FindFreeCellLocked(p, claimed.Width, claimed.Height);
                if (cell is null)
                {
                    p.Ground[claimed.EntityId] = claimed;
                    fullSkipped++;
                    continue;
                }

                List<InMemoryInventorySlotItem> targetList = cell.Value.Row == "Expanded" ? p.ExpandedInventory : p.MainInventory;
                targetList.Add(new InMemoryInventorySlotItem(
                    p.NextInventoryItemId++,
                    cell.Value.Row,
                    cell.Value.X,
                    cell.Value.Y,
                    claimed,
                    claimed.StackSize));

                if (cell.Value.Row == "Expanded")
                {
                    expandedAdded++;
                }
                else
                {
                    mainAdded++;
                }

                removedGroundIds.Add(claimed.EntityId);
                picked++;
            }
        }

        foreach (uint removedId in removedGroundIds)
        {
            await SendWorldRemovalAsync(connectionId, removedId, "non-stackable aoe-looted", cancellationToken);
        }

        lock (p.Gate)
        {
            if (mainAdded > 0)
            {
                p.SentInventoryChangeBatches.Add(("Main", mainAdded));
            }

            if (expandedAdded > 0)
            {
                p.SentInventoryChangeBatches.Add(("Expanded", expandedAdded));
            }
        }

        return (picked, fullSkipped);
    }

    public Task SendInventoryFullAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (_players.TryGetValue(connectionId, out PlayerWorldState? p))
        {
            lock (p.Gate)
            {
                p.InventoryFullNotificationsSent++;
            }
        }

        return Task.CompletedTask;
    }

    private static int ComputeStackRoomLocked(PlayerWorldState p, LootGroundItem item)
    {
        int room = 0;
        room += ComputeRowStackRoom(p.MainInventory, MainGridWidth, MainGridHeight, item);
        if (p.Session.ExpandedInventoryEnabled)
        {
            room += ComputeRowStackRoom(p.ExpandedInventory, ExpandedGridWidth, ExpandedGridHeight, item);
        }

        return room;
    }

    private static int ComputeRowStackRoom(List<InMemoryInventorySlotItem> rowItems, int width, int height, LootGroundItem item)
    {
        int room = 0;
        foreach (InMemoryInventorySlotItem existing in rowItems)
        {
            if (existing.SourceItem.StackIdentityKey == item.StackIdentityKey && existing.StackSize < item.EffectiveMaxStack)
            {
                room += item.EffectiveMaxStack - existing.StackSize;
            }
        }

        bool[,] occ = BuildOccupancyGrid(rowItems, width, height);
        int freeSlots = CountNonOverlappingPlacements(occ, width, height, Math.Max(1, (int)item.Width), Math.Max(1, (int)item.Height));
        room += freeSlots * item.EffectiveMaxStack;
        return room;
    }

    private static int PlaceStackableLocked(PlayerWorldState p, LootGroundItem item)
    {
        int remaining = item.StackSize;
        int mainChanges = 0;
        int expandedChanges = 0;

        remaining = MergeIntoExistingStacks(p.MainInventory, item, remaining, ref mainChanges);
        if (remaining > 0 && p.Session.ExpandedInventoryEnabled)
        {
            remaining = MergeIntoExistingStacks(p.ExpandedInventory, item, remaining, ref expandedChanges);
        }

        remaining = PlaceNewStacksInRow(p, p.MainInventory, "Main", MainGridWidth, MainGridHeight, item, remaining, ref mainChanges);
        if (remaining > 0 && p.Session.ExpandedInventoryEnabled)
        {
            remaining = PlaceNewStacksInRow(p, p.ExpandedInventory, "Expanded", ExpandedGridWidth, ExpandedGridHeight, item, remaining, ref expandedChanges);
        }

        if (mainChanges > 0)
        {
            p.SentInventoryChangeBatches.Add(("Main", mainChanges));
        }

        if (expandedChanges > 0)
        {
            p.SentInventoryChangeBatches.Add(("Expanded", expandedChanges));
        }

        return remaining;
    }

    private static int MergeIntoExistingStacks(List<InMemoryInventorySlotItem> rowItems, LootGroundItem item, int remaining, ref int changes)
    {
        for (int i = 0; i < rowItems.Count && remaining > 0; i++)
        {
            InMemoryInventorySlotItem existing = rowItems[i];
            if (existing.SourceItem.StackIdentityKey != item.StackIdentityKey || existing.StackSize >= item.EffectiveMaxStack)
            {
                continue;
            }

            int add = Math.Min(remaining, item.EffectiveMaxStack - existing.StackSize);
            rowItems[i] = existing with { StackSize = (ushort)(existing.StackSize + add) };
            remaining -= add;
            changes++;
        }

        return remaining;
    }

    private static int PlaceNewStacksInRow(
        PlayerWorldState p,
        List<InMemoryInventorySlotItem> rowItems,
        string rowName,
        int gridW,
        int gridH,
        LootGroundItem item,
        int remaining,
        ref int changes)
    {
        while (remaining > 0)
        {
            (byte X, byte Y)? free = FindFreeCellInRow(rowItems, gridW, gridH, item.Width, item.Height);
            if (free is null)
            {
                break;
            }

            ushort take = (ushort)Math.Min(remaining, item.EffectiveMaxStack);
            rowItems.Add(new InMemoryInventorySlotItem(p.NextInventoryItemId++, rowName, free.Value.X, free.Value.Y, item, take));
            remaining -= take;
            changes++;
        }

        return remaining;
    }

    private static (string Row, byte X, byte Y)? FindFreeCellLocked(PlayerWorldState p, byte w, byte h)
    {
        if (FindFreeCellInRow(p.MainInventory, MainGridWidth, MainGridHeight, w, h) is { } mainCell)
        {
            return ("Main", mainCell.X, mainCell.Y);
        }

        if (p.Session.ExpandedInventoryEnabled &&
            FindFreeCellInRow(p.ExpandedInventory, ExpandedGridWidth, ExpandedGridHeight, w, h) is { } expCell)
        {
            return ("Expanded", expCell.X, expCell.Y);
        }

        return null;
    }

    private static (byte X, byte Y)? FindFreeCellInRow(List<InMemoryInventorySlotItem> rowItems, int gridW, int gridH, byte itemW, byte itemH)
    {
        int w = Math.Max(1, (int)itemW);
        int h = Math.Max(1, (int)itemH);
        bool[,] occ = BuildOccupancyGrid(rowItems, gridW, gridH);
        for (int x = 0; x <= gridW - w; x++)
        {
            for (int y = 0; y <= gridH - h; y++)
            {
                if (FitsAt(occ, x, y, w, h))
                {
                    return ((byte)x, (byte)y);
                }
            }
        }

        return null;
    }

    private static bool[,] BuildOccupancyGrid(List<InMemoryInventorySlotItem> items, int gridW, int gridH)
    {
        bool[,] occ = new bool[gridW, gridH];
        foreach (InMemoryInventorySlotItem it in items)
        {
            int w = Math.Max(1, (int)it.SourceItem.Width);
            int h = Math.Max(1, (int)it.SourceItem.Height);
            for (int dx = 0; dx < w; dx++)
            {
                for (int dy = 0; dy < h; dy++)
                {
                    int cx = it.X + dx;
                    int cy = it.Y + dy;
                    if (cx < gridW && cy < gridH)
                    {
                        occ[cx, cy] = true;
                    }
                }
            }
        }

        return occ;
    }

    private static int CountNonOverlappingPlacements(bool[,] occ, int gridW, int gridH, int w, int h)
    {
        int count = 0;
        for (int x = 0; x <= gridW - w; x++)
        {
            for (int y = 0; y <= gridH - h; y++)
            {
                if (!FitsAt(occ, x, y, w, h))
                {
                    continue;
                }

                for (int dx = 0; dx < w; dx++)
                {
                    for (int dy = 0; dy < h; dy++)
                    {
                        occ[x + dx, y + dy] = true;
                    }
                }

                count++;
            }
        }

        return count;
    }

    private static bool FitsAt(bool[,] occ, int x, int y, int w, int h)
    {
        for (int dx = 0; dx < w; dx++)
        {
            for (int dy = 0; dy < h; dy++)
            {
                if (occ[x + dx, y + dy])
                {
                    return false;
                }
            }
        }

        return true;
    }
}
