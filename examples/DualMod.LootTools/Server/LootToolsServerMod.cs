using System.Collections.Concurrent;
using DualMod.LootTools.Shared;
using LocalExile.Client.ServerBridge;

namespace DualMod.LootTools.Server;

/// <summary>
/// Server half of <c>DualMod.LootTools</c> implementing filter-aware Loot Magnet relocation,
/// atomic Ground Loot Merger, AoE Pickup, and anti-exploit guardrails over <see cref="ClientBridgeHost"/>.
/// </summary>
public sealed class LootToolsServerMod : IDisposable
{
    private sealed record AutoMergePreference(bool Enabled, int RadiusCells, double LastRunAreaTime);

    private readonly ClientBridgeHost _bridge;
    private readonly ILootToolsBackend _backend;
    private readonly LootToolsSettings _settings;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly ConcurrentDictionary<string, AutoMergePreference> _autoMergePrefs = new(StringComparer.Ordinal);
    private bool _disposed;

    public LootToolsServerMod(
        ClientBridgeHost bridge,
        ILootToolsBackend backend,
        LootToolsSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(backend);
        _bridge = bridge;
        _backend = backend;
        _settings = settings ?? new LootToolsSettings();
    }

    public LootToolsSettings Settings => _settings;

    public void Attach()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _subscriptions.Add(_bridge.On<LootMagnetRequest>(LootToolsProtocol.ChannelName, async (session, req) =>
        {
            LootMagnetOutcome outcome = await ExecuteLootMagnetAsync(session.ConnectionId, req);
            await SendToastAsync(
                session,
                feature: "loot_magnet",
                title: "Loot Magnet",
                message: outcome.Message,
                severity: outcome.RelocatedCount > 0 ? "Success" : outcome.Success ? "Info" : "Warning",
                affectedCount: outcome.RelocatedCount);
        }));

        _subscriptions.Add(_bridge.On<AoePickupRequest>(LootToolsProtocol.ChannelName, async (session, req) =>
        {
            AoePickupOutcome outcome = await ExecuteAoePickupAsync(session.ConnectionId, req);
            string severity = !outcome.Success
                ? "Warning"
                : outcome.InventoryFullSkipped > 0
                    ? "Warning"
                    : (outcome.PickedItemCount > 0 || outcome.GoldPickedTotal > 0 ? "Success" : "Info");
            await SendToastAsync(
                session,
                feature: "aoe_pickup",
                title: "AoE Pickup",
                message: outcome.Message,
                severity: severity,
                affectedCount: outcome.PickedItemCount,
                goldPicked: outcome.GoldPickedTotal,
                inventoryFullSkipped: outcome.InventoryFullSkipped);
        }));

        _subscriptions.Add(_bridge.On<LootMergeRequest>(LootToolsProtocol.ChannelName, async (session, req) =>
        {
            LootMergeOutcome outcome = await ExecuteLootMergeAsync(session.ConnectionId, req);
            if (req.IsAutoMerge && outcome.PilesRemoved == 0)
            {
                return;
            }

            await SendToastAsync(
                session,
                feature: "loot_merge",
                title: req.IsAutoMerge ? "Auto Loot Merge" : "Loot Merger",
                message: outcome.Message,
                severity: outcome.PilesRemoved > 0 ? "Success" : outcome.Success ? "Info" : "Warning",
                affectedCount: outcome.GroupsMerged,
                mergedPilesRemoved: outcome.PilesRemoved,
                mergedPilesCreated: outcome.PilesCreated);
        }));

        _subscriptions.Add(_bridge.On<LootToolsSetAutoMergeRequest>(LootToolsProtocol.ChannelName, (session, req) =>
        {
            int clamped = ClampRadius(req.RadiusCells);
            _autoMergePrefs[session.ConnectionId] = new AutoMergePreference(req.Enabled, clamped, double.NegativeInfinity);
            return Task.CompletedTask;
        }));
    }

    public void OnPlayerLeft(string connectionId) => _autoMergePrefs.TryRemove(connectionId, out _);

    /// <summary>
    /// Optional throttled server-side auto-merge hook driven from LocalExile's <c>GameEvents.AreaTick</c>.
    /// </summary>
    public async Task<LootMergeOutcome?> OnAreaTickAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        bool enabled = _autoMergePrefs.TryGetValue(connectionId, out AutoMergePreference? pref)
            ? pref.Enabled
            : _settings.AutoMergeEnabled;
        if (!enabled)
        {
            return null;
        }

        if (!_backend.TryGetSessionState(connectionId, out LootSessionState session) || !session.IsEligibleForLootActions)
        {
            return null;
        }

        double interval = Math.Clamp(_settings.AutoMergeIntervalSeconds, LootToolsProtocol.MinAutoMergeIntervalSeconds, LootToolsProtocol.MaxAutoMergeIntervalSeconds);
        double lastRun = pref?.LastRunAreaTime ?? double.NegativeInfinity;
        if (session.AreaTimeSeconds - lastRun < interval)
        {
            return null;
        }

        int radius = pref?.RadiusCells ?? ClampRadius(_settings.MergeRadius);
        _autoMergePrefs[connectionId] = new AutoMergePreference(true, radius, session.AreaTimeSeconds);
        return await ExecuteLootMergeAsync(connectionId, new LootMergeRequest(null, radius, IsAutoMerge: true), cancellationToken);
    }

    /// <summary>
    /// Filter-Aware Loot Magnet: validates client-supplied entity IDs against server guardrails and cleanly relocates
    /// via atomic <c>Take</c> -> <c>Add(justDropped: false)</c> + <c>SendWorldRemoval</c> + <c>SendGroundItem</c>.
    /// NEVER uses <c>0x01B7</c> Teleport on WorldItems.
    /// </summary>
    public async Task<LootMagnetOutcome> ExecuteLootMagnetAsync(
        string connectionId,
        LootMagnetRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_backend.TryGetSessionState(connectionId, out LootSessionState session) || !session.IsEligibleForLootActions)
        {
            return new LootMagnetOutcome(false, 0, 0, [], "Loot Magnet unavailable in current session state.");
        }

        if (request.EntityIds is null || request.EntityIds.Length == 0)
        {
            return new LootMagnetOutcome(true, 0, 0, [], "No ground items supplied for Loot Magnet.");
        }

        int radius = ClampRadius(request.RadiusCells);
        int batchCap = ClampBatchCap();
        List<uint> uniqueIds = DeduplicateAndCap(request.EntityIds, batchCap);
        List<(uint OldId, uint NewId, int X, int Y)> relocated = [];
        int skipped = Math.Max(0, request.EntityIds.Length - uniqueIds.Count);

        foreach (uint entityId in uniqueIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            LootGroundItem? ground = _backend.GetGroundItem(connectionId, entityId);
            if (!IsGroundItemEligible(connectionId, session, ground, radius))
            {
                skipped++;
                continue;
            }

            (int targetX, int targetY) = _backend.FindWalkableDropCell(connectionId, session.PlayerX, session.PlayerY, scatterNearPlayer: true);

            // Atomic single-winner claim prevents duplicating against concurrent pickups or merges.
            if (!_backend.TryTakeGroundItem(connectionId, entityId, ground!, out LootGroundItem claimed))
            {
                skipped++;
                continue;
            }

            LootGroundItem movedItem = claimed with { GridX = targetX, GridY = targetY };
            uint newId = _backend.AddGroundItem(connectionId, movedItem, justDropped: false);
            LootGroundItem createdItem = movedItem with { EntityId = newId };

            await _backend.SendWorldRemovalAsync(
                connectionId,
                entityId,
                $"0x02bd remove world item 0x{entityId:x} (loot magnet -> 0x{newId:x})",
                cancellationToken);
            await _backend.SendGroundItemAsync(
                connectionId,
                newId,
                createdItem,
                justDropped: false,
                $"0x02bb relocate world item 0x{newId:x} to ({targetX}, {targetY})",
                cancellationToken);

            relocated.Add((entityId, newId, targetX, targetY));
        }

        string msg = relocated.Count > 0
            ? $"Pulled {relocated.Count} ground item(s) nearby."
            : "No eligible ground items were pulled.";
        return new LootMagnetOutcome(true, relocated.Count, skipped, relocated, msg);
    }

    /// <summary>
    /// Atomic Ground Loot Merger: groups compatible partial stacks by strict item identity, claims every pile atomically
    /// before summing so concurrent pickups never duplicate items, and re-emits minimal stacks with <c>justDropped: false</c>.
    /// </summary>
    public async Task<LootMergeOutcome> ExecuteLootMergeAsync(
        string connectionId,
        LootMergeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_backend.TryGetSessionState(connectionId, out LootSessionState session) || !session.IsEligibleForLootActions)
        {
            return new LootMergeOutcome(false, 0, 0, 0, [], "Loot Merger unavailable in current session state.");
        }

        int radius = ClampRadius(request.RadiusCells);
        int batchCap = ClampBatchCap();

        List<LootGroundItem> candidates = [];
        if (request.EntityIds is { Length: > 0 } suppliedIds)
        {
            foreach (uint id in DeduplicateAndCap(suppliedIds, batchCap))
            {
                LootGroundItem? ground = _backend.GetGroundItem(connectionId, id);
                if (IsGroundItemEligible(connectionId, session, ground, radius) && IsMergeablePartialStack(ground!))
                {
                    candidates.Add(ground!);
                }
            }
        }
        else
        {
            IReadOnlyList<LootGroundItem> nearby = _backend.GetGroundItemsWithin(connectionId, session.PlayerX, session.PlayerY, radius);
            foreach (LootGroundItem ground in nearby)
            {
                if (IsGroundItemEligible(connectionId, session, ground, radius) && IsMergeablePartialStack(ground))
                {
                    candidates.Add(ground);
                }
            }
        }

        if (candidates.Count < 2)
        {
            return new LootMergeOutcome(true, 0, 0, 0, [], "No partial ground stacks available to merge.");
        }

        // Strict identity grouping: identical item identity AND compatible visibility/ownership (never leak private items to shared!).
        var groups = candidates
            .GroupBy(c => (c.StackIdentityKey, c.Shared, Owner: c.Shared ? string.Empty : (c.OwnerConnectionId ?? string.Empty)))
            .Where(g => g.Count() >= 2)
            .ToList();

        int groupsMerged = 0;
        int totalRemoved = 0;
        int totalCreated = 0;
        List<uint> createdIds = [];

        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<LootGroundItem> ordered = [.. group
                .OrderBy(i => DistanceSquared(session.PlayerX, session.PlayerY, i.GridX, i.GridY))
                .ThenBy(i => i.EntityId)];

            int maxStack = ordered[0].EffectiveMaxStack;
            long previewTotal = 0;
            foreach (LootGroundItem item in ordered)
            {
                previewTotal += item.StackSize;
            }

            int previewPiles = (int)((previewTotal + maxStack - 1) / maxStack);
            if (previewPiles >= ordered.Count)
            {
                continue;
            }

            // Atomically claim every pile in this group FIRST before summing so concurrent pickups NEVER duplicate!
            List<LootGroundItem> claimed = new(ordered.Count);
            long claimedTotal = 0;
            foreach (LootGroundItem expected in ordered)
            {
                if (_backend.TryTakeGroundItem(connectionId, expected.EntityId, expected, out LootGroundItem taken))
                {
                    claimed.Add(taken);
                    claimedTotal += taken.StackSize;
                }
            }

            if (claimed.Count == 0)
            {
                continue;
            }

            int requiredPiles = (int)((claimedTotal + maxStack - 1) / maxStack);
            if (claimed.Count < 2 || requiredPiles >= claimed.Count)
            {
                // Concurrent pickups reduced the group so merging no longer shrinks pile count: restore untouched.
                foreach (LootGroundItem back in claimed)
                {
                    _backend.RestoreGroundItem(connectionId, back);
                }

                continue;
            }

            LootGroundItem anchor = claimed[0];
            (int dropX, int dropY) = _backend.FindWalkableDropCell(connectionId, anchor.GridX, anchor.GridY, scatterNearPlayer: false);

            List<(uint NewId, LootGroundItem Item)> createdForGroup = new(requiredPiles);
            long remaining = claimedTotal;
            while (remaining > 0)
            {
                ushort stack = (ushort)Math.Min(remaining, maxStack);
                remaining -= stack;
                LootGroundItem merged = anchor with
                {
                    GridX = dropX,
                    GridY = dropY,
                    StackSize = stack,
                };
                uint newId = _backend.AddGroundItem(connectionId, merged, justDropped: false);
                createdForGroup.Add((newId, merged with { EntityId = newId }));
            }

            // Remove all old IDs then create merged piles with justDropped: false to refresh ground labels & filter rules.
            foreach (LootGroundItem old in claimed)
            {
                await _backend.SendWorldRemovalAsync(
                    connectionId,
                    old.EntityId,
                    $"0x02bd remove merged ground pile 0x{old.EntityId:x}",
                    cancellationToken);
            }

            foreach ((uint newId, LootGroundItem mergedItem) in createdForGroup)
            {
                await _backend.SendGroundItemAsync(
                    connectionId,
                    newId,
                    mergedItem,
                    justDropped: false,
                    $"0x02bb create merged ground pile 0x{newId:x} (x{mergedItem.StackSize})",
                    cancellationToken);
                createdIds.Add(newId);
            }

            groupsMerged++;
            totalRemoved += claimed.Count;
            totalCreated += createdForGroup.Count;
        }

        string msg = groupsMerged > 0
            ? $"Merged {totalRemoved} stack(s) into {totalCreated} pile(s) across {groupsMerged} group(s)."
            : "No ground stacks could be consolidated further.";
        return new LootMergeOutcome(true, groupsMerged, totalRemoved, totalCreated, createdIds, msg);
    }

    /// <summary>
    /// Filter-Aware AoE Pickup: picks up gold, stackables, and non-stackables within server-clamped radius and batch cap,
    /// continues checking smaller items when inventory cannot fit a large item, and emits <c>0x0117 InventoryFull</c> at most once.
    /// </summary>
    public async Task<AoePickupOutcome> ExecuteAoePickupAsync(
        string connectionId,
        AoePickupRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_backend.TryGetSessionState(connectionId, out LootSessionState session) || !session.IsEligibleForLootActions)
        {
            return new AoePickupOutcome(false, 0, 0, 0, 0, "AoE Pickup unavailable in current session state.");
        }

        if (request.EntityIds is null || request.EntityIds.Length == 0)
        {
            return new AoePickupOutcome(true, 0, 0, 0, 0, "No ground items supplied for AoE Pickup.");
        }

        int radius = ClampRadius(request.RadiusCells);
        int batchCap = ClampBatchCap();
        List<uint> uniqueIds = DeduplicateAndCap(request.EntityIds, batchCap);
        int rejected = Math.Max(0, request.EntityIds.Length - uniqueIds.Count);

        List<LootGroundItem> goldCandidates = [];
        List<LootGroundItem> stackCandidates = [];
        List<LootGroundItem> nonStackCandidates = [];

        foreach (uint entityId in uniqueIds)
        {
            LootGroundItem? ground = _backend.GetGroundItem(connectionId, entityId);
            if (!IsGroundItemEligible(connectionId, session, ground, radius))
            {
                rejected++;
                continue;
            }

            if (ground!.IsGold)
            {
                goldCandidates.Add(ground);
            }
            else if (ground.MaxStack > 1)
            {
                stackCandidates.Add(ground);
            }
            else
            {
                nonStackCandidates.Add(ground);
            }
        }

        int goldPickedTotal = 0;
        int pickedItems = 0;
        int fullSkipped = 0;

        // 1. Gold piles -> league gold stash via Game.PickUpGold.
        foreach (LootGroundItem gold in goldCandidates.OrderBy(g => DistanceSquared(session.PlayerX, session.PlayerY, g.GridX, g.GridY)).ThenBy(g => g.EntityId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int addedGold = await _backend.TryPickUpGoldAsync(connectionId, gold, cancellationToken);
            if (addedGold > 0)
            {
                goldPickedTotal += addedGold;
            }
        }

        // 2. Stackables -> MainInventory + ExpandedInventory via Game.TakeGroundStack.
        foreach (LootGroundItem stack in stackCandidates.OrderBy(g => DistanceSquared(session.PlayerX, session.PlayerY, g.GridX, g.GridY)).ThenBy(g => g.EntityId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            GroundStackPickupResult res = await _backend.TryPickUpStackableAsync(connectionId, stack, cancellationToken);
            if (res.PickedUp)
            {
                pickedItems++;
            }
            else if (res.InventoryFull)
            {
                fullSkipped++;
            }
        }

        // 3. Non-stackables -> MainInventory (+ ExpandedInventory if granted), continuing past items too large to fit.
        if (nonStackCandidates.Count > 0)
        {
            List<LootGroundItem> orderedNonStack = [.. nonStackCandidates
                .OrderBy(g => DistanceSquared(session.PlayerX, session.PlayerY, g.GridX, g.GridY))
                .ThenBy(g => g.EntityId)];
            (int nonStackPicked, int nonStackFull) = await _backend.TryPickUpNonStackablesAsync(connectionId, orderedNonStack, cancellationToken);
            pickedItems += nonStackPicked;
            fullSkipped += nonStackFull;
        }

        // Emit S->C 0x0117 InventoryFull at most once per AoE trigger.
        if (fullSkipped > 0)
        {
            await _backend.SendInventoryFullAsync(connectionId, cancellationToken);
        }

        List<string> parts = [];
        if (pickedItems > 0)
        {
            parts.Add($"Picked up {pickedItems} item(s)");
        }

        if (goldPickedTotal > 0)
        {
            parts.Add($"+{goldPickedTotal} gold");
        }

        if (fullSkipped > 0)
        {
            parts.Add($"{fullSkipped} skipped (inventory full)");
        }

        string message = parts.Count > 0
            ? string.Join(", ", parts) + "."
            : "No eligible ground items picked up.";

        return new AoePickupOutcome(true, pickedItems, goldPickedTotal, fullSkipped, rejected, message);
    }

    private int ClampRadius(int requestedRadius) =>
        Math.Clamp(requestedRadius > 0 ? requestedRadius : _settings.MagnetRadius, LootToolsProtocol.MinRadiusCells, LootToolsProtocol.MaxRadiusCells);

    private int ClampBatchCap() =>
        Math.Clamp(_settings.MaxBatchSize, 1, LootToolsProtocol.MaxBatchEntityCount);

    private static List<uint> DeduplicateAndCap(uint[] ids, int cap)
    {
        HashSet<uint> seen = [];
        List<uint> result = new(Math.Min(ids.Length, cap));
        for (int i = 0; i < ids.Length && result.Count < cap; i++)
        {
            uint id = ids[i];
            if (id != 0 && seen.Add(id))
            {
                result.Add(id);
            }
        }

        return result;
    }

    private static bool IsGroundItemEligible(string connectionId, LootSessionState session, LootGroundItem? ground, int radiusCells)
    {
        if (ground is null)
        {
            return false;
        }

        if (ground.IsQuestItem || ground.IsTrackedDrop)
        {
            return false;
        }

        // Allocation / visibility check: shared or privately owned by this player.
        if (!ground.Shared && !string.Equals(ground.OwnerConnectionId, connectionId, StringComparison.Ordinal))
        {
            return false;
        }

        // Lockstep admission guard: never act on an item whose create hasn't been admitted yet.
        if (session.LockstepClient && !ground.LockstepAdmitted)
        {
            return false;
        }

        double maxDistSq = (double)radiusCells * radiusCells;
        return DistanceSquared(session.PlayerX, session.PlayerY, ground.GridX, ground.GridY) <= maxDistSq;
    }

    private static bool IsMergeablePartialStack(LootGroundItem ground) =>
        ground.IsStackableOnGround && ground.StackSize > 0 && ground.StackSize < ground.EffectiveMaxStack;

    private static double DistanceSquared(int x1, int y1, int x2, int y2)
    {
        double dx = x2 - x1;
        double dy = y2 - y1;
        return (dx * dx) + (dy * dy);
    }

    private static Task SendToastAsync(
        IClientSession session,
        string feature,
        string title,
        string message,
        string severity = "Info",
        int affectedCount = 0,
        int goldPicked = 0,
        int inventoryFullSkipped = 0,
        int mergedPilesRemoved = 0,
        int mergedPilesCreated = 0,
        CancellationToken cancellationToken = default) =>
        session.SendAsync(
            LootToolsProtocol.ChannelName,
            new LootToolsToastNotification(
                Feature: feature,
                Title: title,
                Message: message,
                Severity: severity,
                AffectedCount: affectedCount,
                GoldPicked: goldPicked,
                InventoryFullSkipped: inventoryFullSkipped,
                MergedPilesRemoved: mergedPilesRemoved,
                MergedPilesCreated: mergedPilesCreated),
            cancellationToken: cancellationToken);

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
        _autoMergePrefs.Clear();
    }
}
