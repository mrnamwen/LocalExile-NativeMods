#nullable enable
// Ready-to-drop LocalExile Gateway IMod entry point for DualMod.LootTools (SDK 0.4.0).
// Requires ZERO LocalExile server modifications — uses verified public Gateway APIs:
// - player.Instance.Ground (Get, Origin, Within, Take, Add, Restore)
// - Game.SendWorldRemoval (0x02BD) + Game.SendGroundItem (0x02BB + 0x01A9, justDropped: false) — NEVER 0x01B7 Teleport
// - Game.PickUpGold, Game.TakeGroundStack (with Rest synced via Game.SendGroundItem so wake: true is preserved)
// - PlayerInventories.FreeCell (MainInventory + ExpandedInventory when player.ExpandedInventoryMode == 1) + Game.KeepOnGroundIfNotSaved + coalesced 0x00BF
// - InstanceItems.InventoryFull() (0x0117, at most once per AoE trigger)
// - GameEvents.AreaTick.Add(mod.Manifest.Id, GameEvents.Always(...)) for optional throttled auto-merge.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DualMod.LootTools.Server;
using DualMod.LootTools.Shared;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.ServerBridge;

public sealed class LootToolsLocalExileEntry : IMod, IDisposable
{
    private LocalExileGatewayHost? _gatewayHost;
    private LocalExileLootToolsBackend? _backend;
    private LootToolsServerMod? _serverMod;
    private IDisposable? _areaTickSub;

    public void Load(IModContext mod)
    {
        LootToolsSettings settings = LoadSettings(mod);

        _gatewayHost = LocalExileGatewayHost.Create(mod, builder => builder
            .WithBridgeOptions(new ClientBridgeOptions
            {
                ServerVersion = "LocalExile-3.29.3d",
                HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId,
                DefaultChannel = LootToolsProtocol.ChannelName,
            })
            .WithBus(
                attach: true,
                onDispatchError: (owner, ex) => mod.Log($"[loottools] gateway bus '{owner}' failed: {ex.Message}"))
            .OnPlayerLeaving((ctx, _) =>
            {
                if (((Player)ctx.Player).Closed)
                {
                    _backend?.ClearPlayerCache(ctx.ConnectionId);
                    _serverMod?.OnPlayerLeft(ctx.ConnectionId);
                }

                return Task.CompletedTask;
            }));

        _backend = new LocalExileLootToolsBackend(_gatewayHost);
        _serverMod = new LootToolsServerMod(_gatewayHost.Bridge, _backend, settings);
        _serverMod.Attach();

        // Throttled area tick hook for optional server-side auto-merge of nearby partial ground stacks.
        _areaTickSub = GameEvents.AreaTick.Add(mod.Manifest.Id, GameEvents.Always(async (player, _) =>
        {
            if (_serverMod is null || player.Closed || player.LeavingArea || player.Paused)
            {
                return;
            }

            string connId = $"{player.Connection.Remote}#{player.Id}";
            await _serverMod.OnAreaTickAsync(connId);
        }));

        mod.Log("Loaded DualMod.LootTools via LocalExileGatewayHost (zero server edits).");
    }

    public void Dispose()
    {
        _areaTickSub?.Dispose();
        _areaTickSub = null;
        _serverMod?.Dispose();
        _serverMod = null;
        _gatewayHost?.Dispose();
        _gatewayHost = null;
    }

    private static LootToolsSettings LoadSettings(IModContext mod)
    {
        try
        {
            return mod.Settings<LootToolsSettings>();
        }
        catch
        {
            return new LootToolsSettings();
        }
    }

    private sealed class LocalExileLootToolsBackend(LocalExileGatewayHost gatewayHost) : ILootToolsBackend
    {
        private readonly ConcurrentDictionary<(string ConnectionId, uint EntityId), (WorldItemObject Ground, object? OriginBy, bool Shared)> _liveSnapshots = new();

        public void ClearPlayerCache(string connectionId)
        {
            foreach (var key in _liveSnapshots.Keys)
            {
                if (string.Equals(key.ConnectionId, connectionId, StringComparison.Ordinal))
                {
                    _liveSnapshots.TryRemove(key, out _);
                }
            }
        }

        private bool TryGetPlayer(string connectionId, out Player player)
        {
            if (gatewayHost.TryGetPlayer(connectionId, out object? obj) && obj is Player p && !p.Closed)
            {
                player = p;
                return true;
            }

            player = null!;
            return false;
        }

        public bool TryGetSessionState(string connectionId, out LootSessionState state)
        {
            if (!TryGetPlayer(connectionId, out Player p))
            {
                state = default!;
                return false;
            }

            double areaTime = Game.AreaTime(p);
            (int X, int Y) at = p.Walk.Position(areaTime);

            state = new LootSessionState(
                CurrentGameWork: Game.CurrentGameWork,
                Closed: p.Closed,
                LeavingArea: p.LeavingArea,
                Paused: p.Paused,
                PlayerSent: p.Connection.PlayerSent,
                PlayerLife: (int)p.PlayerLife,
                RageRetired: p.RageRetired,
                HardcoreProgressBlocked: Game.HardcoreProgressBlocked(p),
                AllowsLoot: GameEvents.AllowsLoot(p),
                HasActiveTrade: p.Trade is not null,
                HasActiveSale: p.Sale is not null,
                LockstepClient: p.Connection.LockstepClient,
                ExpandedInventoryEnabled: p.ExpandedInventoryMode == 1,
                PlayerX: at.X,
                PlayerY: at.Y,
                AreaTimeSeconds: areaTime);
            return true;
        }

        public LootGroundItem? GetGroundItem(string connectionId, uint entityId)
        {
            if (!TryGetPlayer(connectionId, out Player p))
            {
                return null;
            }

            WorldItemObject? ground = p.Instance.Ground.Get(entityId);
            if (ground is null)
            {
                return null;
            }

            return SnapshotGroundItem(connectionId, p, entityId, ground);
        }

        public IReadOnlyList<LootGroundItem> GetGroundItemsWithin(string connectionId, int centerX, int centerY, double radiusCells)
        {
            if (!TryGetPlayer(connectionId, out Player p))
            {
                return [];
            }

            IReadOnlyList<(uint Id, WorldItemObject Item)> within = p.Instance.Ground.Within(centerX, centerY, radiusCells, static _ => true);
            List<LootGroundItem> result = new(within.Count);
            foreach ((uint id, WorldItemObject ground) in within)
            {
                result.Add(SnapshotGroundItem(connectionId, p, id, ground));
            }

            return result;
        }

        public (int X, int Y) FindWalkableDropCell(string connectionId, int originX, int originY, bool scatterNearPlayer)
        {
            if (!TryGetPlayer(connectionId, out Player p))
            {
                return (originX, originY);
            }

            if (scatterNearPlayer)
            {
                (int sx, int sy) = Game.ScatterDropPosition(p, (originX, originY), Game.AreaRng(p));
                if (p.Layout.WalkableFor(sx, sy, 1))
                {
                    return (sx, sy);
                }
            }

            if (p.Layout.WalkableFor(originX, originY, 1))
            {
                return (originX, originY);
            }

            return p.Walk.Position(Game.AreaTime(p));
        }

        public bool TryTakeGroundItem(string connectionId, uint entityId, LootGroundItem expected, out LootGroundItem claimed)
        {
            claimed = null!;
            if (!TryGetPlayer(connectionId, out Player p))
            {
                return false;
            }

            if (!_liveSnapshots.TryGetValue((connectionId, entityId), out var cached))
            {
                return false;
            }

            // Atomic single-winner claim via InstanceGround.Take(id, expected).
            WorldItemObject? taken = p.Instance.Ground.Take(entityId, cached.Ground);
            if (taken is null)
            {
                _liveSnapshots.TryRemove((connectionId, entityId), out _);
                return false;
            }

            claimed = expected;
            return true;
        }

        public uint AddGroundItem(string connectionId, LootGroundItem item, bool justDropped)
        {
            if (!TryGetPlayer(connectionId, out Player p))
            {
                return 0;
            }

            WorldItemObject sourceGround;
            object? originBy = null;
            bool shared = item.Shared;

            if (_liveSnapshots.TryGetValue((connectionId, item.EntityId), out var cached))
            {
                sourceGround = cached.Ground;
                originBy = cached.OriginBy;
                shared = cached.Shared;
            }
            else
            {
                ItemCatalog.BaseType? baseType = Game.ItemsFor(p).ByHash(item.TypeHash);
                Item created = baseType is not null
                    ? ItemCatalog.Create(baseType) with { StackSize = item.StackSize }
                    : throw new InvalidOperationException($"Unknown TypeHash 0x{item.TypeHash:X8} for ground item.");
                sourceGround = new WorldItemObject(item.GridX, item.GridY, created);
                originBy = shared ? null : p;
            }

            WorldItemObject relocated = sourceGround with
            {
                GridX = item.GridX,
                GridY = item.GridY,
                Item = sourceGround.Item with { StackSize = item.StackSize },
            };

            uint newId = p.Instance.Ground.Add(relocated, originBy, shared: shared, justDropped: justDropped);
            _liveSnapshots[(connectionId, newId)] = (relocated, originBy, shared);
            return newId;
        }

        public void RestoreGroundItem(string connectionId, LootGroundItem item)
        {
            if (!TryGetPlayer(connectionId, out Player p))
            {
                return;
            }

            if (_liveSnapshots.TryRemove((connectionId, item.EntityId), out var cached))
            {
                p.Instance.Ground.Restore(item.EntityId, cached.Ground, cached.OriginBy, cached.Shared);
            }
        }

        public Task SendWorldRemovalAsync(string connectionId, uint entityId, string log, CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            _liveSnapshots.TryRemove((connectionId, entityId), out _);
            if (!TryGetPlayer(connectionId, out Player p))
            {
                return Task.CompletedTask;
            }

            return Game.SendWorldRemoval(p, entityId, log);
        }

        public Task SendGroundItemAsync(string connectionId, uint entityId, LootGroundItem item, bool justDropped, string log, CancellationToken cancellationToken = default)
        {
            _ = item;
            _ = cancellationToken;
            _liveSnapshots.TryRemove((connectionId, entityId), out _);
            if (!TryGetPlayer(connectionId, out Player p))
            {
                return Task.CompletedTask;
            }

            WorldItemObject? live = p.Instance.Ground.Get(entityId);
            if (live is null)
            {
                return Task.CompletedTask;
            }

            // Always sync via SendGroundItem (0x02BB + 0x01A9 EnterWakeZone, justDropped: false); NEVER 0x01B7 Teleport.
            return Game.SendGroundItem(p, entityId, live, log, justDropped: justDropped);
        }

        public async Task<int> TryPickUpGoldAsync(string connectionId, LootGroundItem goldItem, CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            if (!TryGetPlayer(connectionId, out Player p))
            {
                return 0;
            }

            WorldItemObject? live = p.Instance.Ground.Get(goldItem.EntityId);
            if (live is null || !Game.IsGold(live))
            {
                return 0;
            }

            ushort amount = live.Item.StackSize;
            await Game.PickUpGold(p, goldItem.EntityId, live, "aoe-looted");
            return p.Instance.Ground.WasTaken(goldItem.EntityId) ? amount : 0;
        }

        public async Task<GroundStackPickupResult> TryPickUpStackableAsync(string connectionId, LootGroundItem stackItem, CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            if (!TryGetPlayer(connectionId, out Player p))
            {
                return new GroundStackPickupResult(false, false);
            }

            WorldItemObject? live = p.Instance.Ground.Get(stackItem.EntityId);
            if (live is null || Game.IsGold(live))
            {
                return new GroundStackPickupResult(false, false);
            }

            Game.GroundPickup? pickup = Game.TakeGroundStack(p, stackItem.EntityId, live, out bool full);
            if (pickup is null)
            {
                return new GroundStackPickupResult(false, InventoryFull: full);
            }

            // Sync removal + inventory update; if Rest is non-null, call Game.SendGroundItem so wake: true is NOT omitted!
            await Game.SendWorldRemoval(p, stackItem.EntityId, $"0x02bd remove world item 0x{stackItem.EntityId:x} (aoe stack pickup)");
            await Program.Send(p, pickup.Update, pickup.Log);
            if (pickup.Rest is { } rest)
            {
                await Game.SendGroundItem(
                    p,
                    pickup.RestId,
                    rest,
                    $"0x02bb create remainder 0x{pickup.RestId:x} from 0x{stackItem.EntityId:x}",
                    justDropped: false);
            }

            return new GroundStackPickupResult(
                PickedUp: true,
                InventoryFull: false,
                RemainderEntityId: pickup.RestId,
                RemainderItem: pickup.Rest is not null ? SnapshotGroundItem(connectionId, p, pickup.RestId, pickup.Rest) : null);
        }

        public async Task<(int PickedCount, int FullSkippedCount)> TryPickUpNonStackablesAsync(
            string connectionId,
            IReadOnlyList<LootGroundItem> candidates,
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            if (!TryGetPlayer(connectionId, out Player p) || candidates.Count == 0)
            {
                return (0, 0);
            }

            PlayerInventories items = Game.ItemsOf(p);
            bool allowExpanded = p.ExpandedInventoryMode == 1;
            int picked = 0;
            int fullSkipped = 0;
            List<uint> removedGroundIds = [];
            Dictionary<byte, List<InventoryItem>> addedByRow = [];

            foreach (LootGroundItem candidate in candidates)
            {
                WorldItemObject? ground = p.Instance.Ground.Get(candidate.EntityId);
                if (ground is null)
                {
                    continue;
                }

                if (Game.ShapeOf(ground.Item) is not { } shape)
                {
                    continue;
                }

                byte row = PlayerInventories.MainInventory;
                (byte X, byte Y)? cell = items.FreeCell(PlayerInventories.MainInventory, shape.Width, shape.Height, Game.ShapeOf);
                if (cell is null && allowExpanded)
                {
                    row = PlayerInventories.ExpandedInventory;
                    cell = items.FreeCell(PlayerInventories.ExpandedInventory, shape.Width, shape.Height, Game.ShapeOf);
                }

                if (cell is null)
                {
                    fullSkipped++;
                    continue;
                }

                if (p.Instance.Ground.Take(candidate.EntityId, ground) is null)
                {
                    continue;
                }

                InventoryItem placed = Game.KeepOnGroundIfNotSaved(
                    p,
                    ground,
                    () => items.Put(row, new InventoryItem(items.NewItemId(), cell.Value.X, cell.Value.Y, ground.Item)),
                    candidate.EntityId);

                removedGroundIds.Add(candidate.EntityId);
                if (!addedByRow.TryGetValue(row, out List<InventoryItem>? rowAdded))
                {
                    rowAdded = [];
                    addedByRow[row] = rowAdded;
                }

                rowAdded.Add(placed);
                picked++;
            }

            foreach (uint removedId in removedGroundIds)
            {
                _liveSnapshots.TryRemove((connectionId, removedId), out _);
                await Game.SendWorldRemoval(p, removedId, $"0x02bd remove world item 0x{removedId:x} (aoe pickup)");
            }

            foreach ((byte row, List<InventoryItem> added) in addedByRow)
            {
                byte[] packet = InstanceItems.InventoryChange(
                    PlayerInventories.InventoryId(row),
                    items.NextRevision(row),
                    [],
                    added);
                await Program.Send(p, packet, $"0x00bf inventory {PlayerInventories.InventoryId(row)}: aoe pickup ({added.Count} item(s))");
            }

            return (picked, fullSkipped);
        }

        public Task SendInventoryFullAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            if (!TryGetPlayer(connectionId, out Player p))
            {
                return Task.CompletedTask;
            }

            return Program.Send(p, InstanceItems.InventoryFull(), "0x0117 inventory full (loottools aoe)");
        }

        private LootGroundItem SnapshotGroundItem(string connectionId, Player p, uint id, WorldItemObject ground)
        {
            var origin = p.Instance.Ground.Origin(id);
            _liveSnapshots[(connectionId, id)] = (ground, origin.By, origin.Shared);

            ItemCatalog.BaseType? baseType = Game.ItemsFor(p).ByHash(ground.Item.TypeHash);
            bool isGold = Game.IsGold(ground);
            bool isTracked = IsTrackedGroundDrop(p, id);
            bool isOwnedByPlayer = ReferenceEquals(origin.By, p);
            bool lockstepAdmitted = !p.Connection.LockstepClient || p.Connection.WorldOutput.Has(id);
            string extraPayload = ComputeNonPlainPayloadHash(ground.Item);

            return new LootGroundItem
            {
                EntityId = id,
                GridX = ground.GridX,
                GridY = ground.GridY,
                TypeHash = ground.Item.TypeHash,
                MetadataPath = baseType?.Id ?? string.Empty,
                BaseName = baseType?.Name ?? string.Empty,
                StackSize = ground.Item.StackSize,
                MaxStack = (ushort)(baseType?.MaxStack ?? 1),
                Width = Math.Max((byte)1, baseType?.Width ?? (byte)1),
                Height = Math.Max((byte)1, baseType?.Height ?? (byte)1),
                IsGold = isGold,
                IsQuestItem = baseType?.Quest is not null,
                IsTrackedDrop = isTracked,
                Shared = origin.Shared,
                OwnerConnectionId = isOwnedByPlayer ? connectionId : (origin.By is null ? null : "__other__"),
                LockstepAdmitted = lockstepAdmitted,
                HeaderFlags = ground.Item.HeaderFlags,
                VisualIdentity = ground.Item.VisualIdentity,
                Corrupted = ground.Item.Corrupted,
                Influences = (byte)ground.Item.Influences,
                Quality = ground.Item.Quality,
                AlternateQualityType = ground.Item.AlternateQualityType,
                ExtraPayloadIdentity = extraPayload,
            };
        }

        private static bool IsTrackedGroundDrop(Player player, uint id)
        {
            string name = Program.CharacterName(player);
            if (player.Instance.IsStartWeapon(name, id))
            {
                return true;
            }

            if (CharacterSaves.Load(name)?.Tutorial is { } tutorial)
            {
                foreach (bool support in new[] { false, true })
                {
                    if (player.Instance.IsStartWeapon(TutorialObjects.DropKey(name, tutorial, support), id))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static string ComputeNonPlainPayloadHash(Item item)
        {
            bool isPlain = item.Price.Count == 0 && item.SkinTransferSource is null;
            if (isPlain)
            {
                bool hasBase = false;
                for (int i = 0; i < item.Components.Count; i++)
                {
                    string c = item.Components[i];
                    if (c == "Base") { hasBase = true; continue; }
                    if (c == "Stack" || c == "Quality" || Item.NoData.Contains(c)) continue;
                    isPlain = false;
                    break;
                }

                if (isPlain && hasBase)
                {
                    return string.Empty;
                }
            }

            byte[] payload = (item with { StackSize = 1 }).Payload();
            return Convert.ToHexString(payload);
        }
    }
}
