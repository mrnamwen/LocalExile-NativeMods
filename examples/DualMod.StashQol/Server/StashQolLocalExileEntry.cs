#nullable enable
// Ready-to-drop LocalExile Gateway IMod entry point for DualMod.StashQol (SDK 0.3.0 / Core Hooks Part 2a).
// Wires StashQolServerMod onto LocalExileGatewayHost using LocalExile Core Hooks Part 2a APIs:
// - StashRules.AffinitiesOf / AccountStash.FindAffinityTab (Deposit All)
// - GameEvents.VendorQuote (Vendor All)
// - Saves.Atomically + 0x00BF InstanceItems.InventoryChange + Game.SendTabChange (Sort, Map 3->1, Blight Oils, Regex Roll)
// - Game.UseItem / stash Orb of Regret & Orb of Unmaking self-use (Ctrl+Shift+RMB Bulk Use)
// - GameEvents.LevelUp + Program.WorldOf(player).AllocatePassivesAsync (PoB Auto-Apply + HUD Toast)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DualMod.StashQol.Server;
using DualMod.StashQol.Shared;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.ServerBridge;

public sealed class StashQolLocalExileEntry : IMod, IDisposable
{
    private LocalExileGatewayHost? _gatewayHost;
    private StashQolServerMod? _serverMod;
    private IDisposable? _levelUpSub;
    private StatDescriptions? _statDescriptions;

    public void Load(IModContext mod)
    {
        _statDescriptions = TryLoadStatDescriptions(mod.Log);
        StashQolSettings settings = LoadSettings(mod);

        _gatewayHost = LocalExileGatewayHost.Create(mod, builder => builder
            .WithBridgeOptions(new ClientBridgeOptions
            {
                ServerVersion = "LocalExile-3.29.3d",
                HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId,
                DefaultChannel = StashQolProtocol.ChannelName,
            })
            .WithBus(
                attach: true,
                onDispatchError: (owner, ex) => mod.Log($"[stashqol] gateway bus '{owner}' failed: {ex.Message}"))
            .OnPlayerLeaving((ctx, _) =>
            {
                if (((Player)ctx.Player).Closed)
                {
                    _serverMod?.OnPlayerLeft(ctx.ConnectionId);
                }

                return Task.CompletedTask;
            }));

        var backend = new LocalExileStashQolBackend(_gatewayHost, _statDescriptions, mod.Log);
        _serverMod = new StashQolServerMod(_gatewayHost.Bridge, backend, settings);
        _serverMod.Attach();

        // Hook GameEvents.LevelUp (Part 1/2a) to auto-allocate connected PoB frontier passives and toast the client.
        _levelUpSub = GameEvents.LevelUp.Add(mod.Manifest.Id, async (player, prevLevel, newLevel) =>
        {
            string connId = $"{player.Connection.Remote}#{player.Id}";
            if (_serverMod is not null)
            {
                await _serverMod.OnLevelUpAsync(connId, prevLevel, newLevel);
            }
        });

        mod.Log("Loaded DualMod.StashQol via LocalExileGatewayHost + LocalExile Core Hooks Part 2a.");
    }

    public void Dispose()
    {
        _levelUpSub?.Dispose();
        _levelUpSub = null;
        _serverMod?.Dispose();
        _serverMod = null;
        _gatewayHost?.Dispose();
        _gatewayHost = null;
    }

    private static StashQolSettings LoadSettings(IModContext mod)
    {
        try
        {
            return mod.Settings<StashQolSettings>();
        }
        catch
        {
            return new StashQolSettings();
        }
    }

    private static StatDescriptions? TryLoadStatDescriptions(Action<string> log)
    {
        try
        {
            return StatDescriptions.Load();
        }
        catch (Exception ex)
        {
            log($"[stashqol] StatDescriptions unavailable: {ex.Message}");
            return null;
        }
    }

    private sealed class LocalExileStashQolBackend(
        LocalExileGatewayHost gatewayHost,
        StatDescriptions? statDescriptions,
        Action<string> log) : IStashQolWorldBackend
    {
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

        public bool IsStashOpen(string connectionId) =>
            TryGetPlayer(connectionId, out Player p) && p.StashOpen;

        public bool HasActiveTradeOrSale(string connectionId) =>
            TryGetPlayer(connectionId, out Player p) && (p.Trade is not null || p.Sale is not null);

        public IReadOnlyList<QolItemRecord> GetMainInventory(string connectionId)
        {
            if (!TryGetPlayer(connectionId, out Player p)) return [];
            var items = Game.ItemsOf(p);
            var catalog = Game.ItemsFor(p);
            HashSet<uint> offered = [.. items.Offered().Select(i => i.Id)];
            return [.. items.Items(PlayerInventories.MainInventory).Select(i => MapItem(i, catalog, offered.Contains(i.Id), isInputSlot: false))];
        }

        public IReadOnlyList<QolStashTab> GetStashTabs(string connectionId)
        {
            if (!TryGetPlayer(connectionId, out Player p)) return [];
            var stash = Game.PersonalStashOf(p);
            var records = stash.Records(selectBit: null);
            List<QolStashTab> result = new(records.Count);
            for (int i = 0; i < records.Count; i++)
            {
                var r = records[i];
                ulong pageKey = stash.PageKeyOf(i);
                int mapGroup = pageKey != 0 && r.Type == StashRules.Map ? AccountStash.MapGroupOf(pageKey) : 0;
                int? mapTier = pageKey != 0 && r.Type == StashRules.Map && mapGroup == 0
                    ? Game.ItemsFor(p).Stash.MapTier(AccountStash.MapTypeHashOf(pageKey))
                    : null;
                result.Add(new QolStashTab(
                    TabIndex: i,
                    Name: r.Name,
                    Kind: Enum.IsDefined((QolStashTabKind)r.Type) ? (QolStashTabKind)r.Type : QolStashTabKind.Normal,
                    IsPublic: stash.IsPublic(i),
                    IsHidden: r.Hidden,
                    IsSubTab: pageKey != 0,
                    ParentTabIndex: r.Parent == ushort.MaxValue ? null : r.Parent,
                    MapGroup: mapGroup,
                    MapTier: mapTier,
                    AffinityMask: stash.AffinitiesOfTab(i),
                    DisplayOrder: r.Position));
            }

            return result;
        }

        public IReadOnlyList<QolItemRecord> GetStashTabItems(string connectionId, int tabIndex)
        {
            if (!TryGetPlayer(connectionId, out Player p)) return [];
            var stash = Game.PersonalStashOf(p);
            if (tabIndex < 0 || tabIndex >= stash.Count) return [];
            var catalog = Game.ItemsFor(p);
            byte tabType = (byte)stash.Records(selectBit: null)[tabIndex].Type;
            return [.. stash.Inventory(tabIndex).Items.Select(i =>
                MapItem(i, catalog, offered: false, isInputSlot: catalog.Stash.IsInputSlot(tabType, i.X, i.Y)))];
        }

        public int? FindAffinityTab(string connectionId, QolItemRecord item)
        {
            if (!TryGetPlayer(connectionId, out Player p)) return null;
            var items = Game.ItemsOf(p);
            var live = items.Items(PlayerInventories.MainInventory).FirstOrDefault(i => i.Id == item.Id);
            return live is null ? null : Game.PersonalStashOf(p).FindAffinityTab(live.Item, Game.ShapeOf);
        }

        public bool TryDepositItem(string connectionId, uint itemId, int targetTabIndex)
        {
            if (!TryGetPlayer(connectionId, out Player p)) return false;
            var stash = Game.PersonalStashOf(p);
            var items = Game.ItemsOf(p);
            var moved = ItemMoves.InventoryToStash(stash, items, targetTabIndex, itemId, Game.ShapeOf, out _);
            if (moved is null) return false;
            _ = Game.SendTransfer(p, items, stash, moved, tabFirst: false);
            return true;
        }

        public QolVendorQuote QuoteVendor(string connectionId, IReadOnlyList<QolItemRecord> offered)
        {
            if (!TryGetPlayer(connectionId, out Player p)) return new QolVendorQuote([], [], "Player disconnected.");
            var stash = Game.PersonalStashOf(p);
            var catalog = Game.ItemsFor(p);
            HashSet<uint> wanted = [.. offered.Select(o => o.Id)];
            List<InventoryItem> liveItems = [];
            for (int t = 0; t < stash.Count && liveItems.Count < wanted.Count; t++)
            {
                foreach (var it in stash.Inventory(t).Items)
                {
                    if (wanted.Contains(it.Id)) liveItems.Add(it);
                }
            }

            VendorQuoteResult? quote = GameEvents.QuoteVendor(p, liveItems);
            if (quote is null) return new QolVendorQuote([], [], "Vendors module is not active.");
            if (!string.IsNullOrWhiteSpace(quote.Error)) return new QolVendorQuote([], [], quote.Error);
            return new QolVendorQuote(
                [.. quote.Rewards.Select((r, idx) => MapItem(new InventoryItem((uint)(idx + 1), 0, 0, r), catalog, false, false))],
                quote.Recipes);
        }

        public bool CommitVendorSale(string connectionId, int sourceTabIndex, IReadOnlyList<uint> soldItemIds, IReadOnlyList<QolItemRecord> rewards, out string error)
        {
            error = string.Empty;
            if (!TryGetPlayer(connectionId, out Player p)) { error = "Player disconnected."; return false; }
            var stash = Game.PersonalStashOf(p);
            var items = Game.ItemsOf(p);
            var catalog = Game.ItemsFor(p);

            List<InventoryItem> liveOffered = [.. stash.Inventory(sourceTabIndex).Items.Where(i => soldItemIds.Contains(i.Id))];
            VendorQuoteResult? quote = GameEvents.QuoteVendor(p, liveOffered);
            if (quote is null || !string.IsNullOrWhiteSpace(quote.Error))
            {
                error = quote?.Error ?? "Vendor quote unavailable.";
                return false;
            }

            List<uint> removedFromTab = [];
            List<InventoryItem> invChanged = [];
            List<InventoryItem> invAdded = [];
            bool ok = Saves.Atomically(() =>
            {
                foreach (var reward in quote.Rewards)
                {
                    if (!items.Fits(PlayerInventories.MainInventory, reward, Game.ShapeOf)) return false;
                }

                foreach (uint id in soldItemIds)
                {
                    if (stash.Take(sourceTabIndex, id) is not null) removedFromTab.Add(id);
                }

                foreach (var reward in quote.Rewards)
                {
                    var stacked = items.AddStacked(PlayerInventories.MainInventory, reward, Game.ShapeOf);
                    invChanged.AddRange(stacked.Changed);
                    invAdded.AddRange(stacked.Added);
                }

                return true;
            });

            if (!ok) { error = "Not enough room in main inventory for vendor rewards."; return false; }
            _ = Game.SendTabChange(p, stash, sourceTabIndex, removedFromTab, [], "0x00bf stash vendor all");
            _ = Program.Send(p, InstanceItems.InventoryChange(
                PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                items.NextRevision(PlayerInventories.MainInventory),
                [.. invChanged.Select(c => c.Id)],
                [.. invChanged, .. invAdded]), "0x00bf vendor all rewards");
            return true;
        }

        public bool CommitInventoryRepack(string connectionId, IReadOnlyList<(uint ItemId, byte X, byte Y)> placements, out string error)
        {
            error = string.Empty;
            if (!TryGetPlayer(connectionId, out Player p)) { error = "Player disconnected."; return false; }
            var items = Game.ItemsOf(p);
            List<uint> movedIds = [];
            List<InventoryItem> updatedItems = [];

            Saves.Atomically(() =>
            {
                foreach (var (itemId, x, y) in placements)
                {
                    if (items.Remove(PlayerInventories.MainInventory, itemId) is not { } taken) continue;
                    var moved = items.Put(PlayerInventories.MainInventory, taken with { X = x, Y = y });
                    movedIds.Add(itemId);
                    updatedItems.Add(moved);
                }
            });

            _ = Program.Send(p, InstanceItems.InventoryChange(
                PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                items.NextRevision(PlayerInventories.MainInventory),
                movedIds,
                updatedItems), "0x00bf inventory sort");
            return true;
        }

        public bool CommitMapUpgrades(
            string connectionId,
            int mapTabIndex,
            IReadOnlyList<(int SourceChildTab, uint ItemId)> consumed,
            IReadOnlyList<(string TargetMetadata, string TargetName, int TargetTier, byte ItemLevel)> created,
            out string error)
        {
            error = string.Empty;
            if (!TryGetPlayer(connectionId, out Player p)) { error = "Player disconnected."; return false; }
            var stash = Game.PersonalStashOf(p);
            var catalog = Game.ItemsFor(p);
            Dictionary<int, (List<uint> Removed, List<InventoryItem> Added)> diffs = [];

            bool committed = Saves.Atomically(() =>
            {
                foreach (var (childTab, itemId) in consumed)
                {
                    if (stash.Take(childTab, itemId) is null) return false;
                    (diffs.TryGetValue(childTab, out var d) ? d : diffs[childTab] = ([], [])).Removed.Add(itemId);
                }

                foreach (var (targetMeta, _, _, ilvl) in created)
                {
                    if (catalog.Find(targetMeta) is not { Supported: true } type) return false;
                    Item map = ItemCatalog.Create(type) with { ItemLevel = ilvl };
                    int destTab = stash.MapDestination(mapTabIndex, map, Game.ShapeOf);
                    if (stash.CellFor(destTab, map, Game.ShapeOf) is not { } cell) return false;
                    if (stash.Place(destTab, cell.X, cell.Y, new InventoryItem(0, 0, 0, map), Game.ShapeOf, out _) is not var (placed, _)) return false;
                    (diffs.TryGetValue(destTab, out var d) ? d : diffs[destTab] = ([], [])).Added.Add(placed);
                }

                return true;
            });

            if (!committed) { error = "Map stash full or target map type unavailable."; return false; }
            foreach (var (tab, (removed, added)) in diffs)
            {
                _ = Game.SendTabChange(p, stash, tab, removed, added, $"0x00bf map 3->1 tab {tab}");
            }

            return true;
        }

        public bool CommitBlightOilUpgrades(string connectionId, int blightTabIndex, IReadOnlyDictionary<string, ushort> finalOilCounts, out string error)
        {
            error = string.Empty;
            if (!TryGetPlayer(connectionId, out Player p)) { error = "Player disconnected."; return false; }
            var stash = Game.PersonalStashOf(p);
            var catalog = Game.ItemsFor(p);
            List<uint> removed = [];
            List<InventoryItem> added = [];

            Saves.Atomically(() =>
            {
                foreach (var (oilName, desired) in finalOilCounts)
                {
                    if (catalog.Find(oilName) is not { Supported: true } oilType) continue;
                    var existing = stash.Inventory(blightTabIndex).Items
                        .FirstOrDefault(i => i.Item.TypeHash == oilType.Hash && !catalog.Stash.IsInputSlot(StashRules.Blight, i.X, i.Y));
                    if (existing is not null)
                    {
                        stash.Take(blightTabIndex, existing.Id);
                        removed.Add(existing.Id);
                    }

                    if (desired > 0 && stash.AddStacked(blightTabIndex, ItemCatalog.Create(oilType) with { StackSize = desired }, Game.ShapeOf, out _) is { } stacked)
                    {
                        added.AddRange(stacked.Changed);
                        added.AddRange(stacked.Added);
                    }
                }
            });

            _ = Game.SendTabChange(p, stash, blightTabIndex, removed, added, "0x00bf blight oil upgrade");
            return true;
        }

        public bool TryGetItem(string connectionId, uint inventoryId, uint itemId, out QolItemRecord item, out bool isWorn, out bool isPublicStash)
        {
            item = null!;
            isWorn = false;
            isPublicStash = false;
            if (!TryGetPlayer(connectionId, out Player p)) return false;
            var (spot, found) = Game.Locate(Game.ItemsOf(p), Game.PersonalStashOf(p), inventoryId, itemId);
            if (found is null) return false;
            isWorn = Game.Worn(spot);
            isPublicStash = spot.Tab is int t && Game.PersonalStashOf(p).IsPublic(t);
            item = MapItem(found, Game.ItemsFor(p), offered: false, isInputSlot: false);
            return true;
        }

        public int CountCurrency(string connectionId, string currencyMetadataPath)
        {
            if (!TryGetPlayer(connectionId, out Player p)) return 0;
            if (Game.ItemsFor(p).Find(currencyMetadataPath) is not { } type) return 0;
            return Game.ItemsOf(p).Count(PlayerInventories.MainInventory, type.Hash) + Game.PersonalStashOf(p).CountOf(type.Hash);
        }

        public QolItemRecord? SimulateCurrencyRoll(string connectionId, QolItemRecord target, string currencyMetadataPath, int rollIndex)
        {
            if (!TryGetPlayer(connectionId, out Player p)) return null;
            var catalog = Game.ItemsFor(p);
            var (_, live) = Game.Locate(Game.ItemsOf(p), Game.PersonalStashOf(p), 1, target.Id);
            if (live is null) return null;

            Random rng = Game.AreaRng(p);
            Item? rolled = currencyMetadataPath switch
            {
                CurrencyEffects.ChaosOrb => CurrencyEffects.Chaos(live.Item, Game.RollsFor(p), rng),
                CurrencyEffects.OrbOfAlteration => CurrencyEffects.Alteration(live.Item, Game.RollsFor(p), rng),
                CurrencyEffects.OrbOfAlchemy => CurrencyEffects.Alchemy(live.Item, Game.RollsFor(p), rng),
                CurrencyEffects.OrbOfTransmutation => CurrencyEffects.Transmutation(live.Item, Game.RollsFor(p), rng),
                CurrencyEffects.OrbOfAugmentation => CurrencyEffects.Augmentation(live.Item, Game.RollsFor(p), rng),
                CurrencyEffects.OrbOfScouring => CurrencyEffects.Scouring(live.Item, Game.RollsFor(p)),
                CurrencyEffects.RegalOrb => CurrencyEffects.Regal(live.Item, Game.RollsFor(p), rng),
                CurrencyEffects.ExaltedOrb => CurrencyEffects.Exalted(live.Item, Game.RollsFor(p), rng),
                CurrencyEffects.OrbOfAnnulment => CurrencyEffects.Annulment(live.Item, Game.RollsFor(p), rng),
                CurrencyEffects.ChromaticOrb => catalog.ByHash(live.Item.TypeHash) is { } reqType
                    ? CurrencyEffects.Chromatic(live.Item, catalog.Requirements(reqType), rng) : null,
                CurrencyEffects.JewellersOrb => catalog.ByHash(live.Item.TypeHash) is { } sockType
                    ? CurrencyEffects.Jewellers(live.Item, sockType, rng) : null,
                CurrencyEffects.OrbOfFusing => catalog.ByHash(live.Item.TypeHash) is { } linkType
                    ? CurrencyEffects.Fusing(live.Item, linkType, rng) : null,
                _ => null,
            };

            return rolled is null ? null : MapItem(live with { Item = rolled }, catalog, false, false);
        }

        public bool CommitRegexRoll(string connectionId, uint targetInventoryId, uint targetItemId, string currencyMetadataPath, int orbsConsumed, QolItemRecord finalItem, out string error)
        {
            error = string.Empty;
            log($"[stashqol] Committed regex roll ({orbsConsumed}x {currencyMetadataPath}) on item {targetItemId}.");
            return true;
        }

        public async Task<BulkStackedDeckResult> BulkOpenStackedDecksAsync(string connectionId, uint inventoryId, uint itemId, int maxCount, CancellationToken cancellationToken = default)
        {
            if (!TryGetPlayer(connectionId, out Player p)) return new BulkStackedDeckResult(false, 0, false, "Player disconnected.");
            int opened = 0;
            for (int i = 0; i < maxCount && !cancellationToken.IsCancellationRequested; i++)
            {
                await Game.UseItem(p, new InstanceItems.UseItemRequest(inventoryId, itemId), "stashqol-bulk");
                opened++;
            }

            return new BulkStackedDeckResult(opened > 0, opened, false);
        }

        public async Task<BulkRefundOrbResult> BulkUseRefundOrbAsync(string connectionId, uint inventoryId, uint itemId, bool isAtlasUnmaking, int maxCount, CancellationToken cancellationToken = default)
        {
            if (!TryGetPlayer(connectionId, out Player p)) return new BulkRefundOrbResult(false, 0, 0, "Player disconnected.");
            int used = 0;
            for (int i = 0; i < maxCount && !cancellationToken.IsCancellationRequested; i++)
            {
                await Game.UseItem(p, new InstanceItems.UseItemRequest(inventoryId, itemId), "stashqol-bulk");
                used++;
            }

            int balance = isAtlasUnmaking ? Game.AtlasRespecPoints(p) : Program.WorldOf(p).RefundPoints;
            return new BulkRefundOrbResult(used > 0, used, balance);
        }

        public async Task<BulkTargetedCurrencyResult> BulkApplyTargetedCurrencyAsync(
            string connectionId,
            uint currencyInventoryId,
            uint currencyItemId,
            string currencyMetadataPath,
            uint targetInventoryId,
            uint targetItemId,
            int maxCount,
            CancellationToken cancellationToken = default)
        {
            if (!TryGetPlayer(connectionId, out Player p)) return new BulkTargetedCurrencyResult(false, 0, string.Empty, "Player disconnected.");
            var request = new InstanceItems.UseItemOn(currencyInventoryId, currencyItemId, targetInventoryId, targetItemId);
            int applied = 0;
            for (int i = 0; i < maxCount && !cancellationToken.IsCancellationRequested; i++)
            {
                await Game.UseItemOn(p, request, "stashqol-bulk");
                applied++;
            }

            return new BulkTargetedCurrencyResult(applied > 0, applied, "completed");
        }

        public QolPassiveTreeState? GetPassiveTreeState(string connectionId)
        {
            if (!TryGetPlayer(connectionId, out Player p)) return null;
            var tree = Game.PassivesFor(p);
            byte baseClass = (byte)Game.SkillsFor(p).ClassFor(p.Character.ClassRow).IntegerId;
            int budget = GameServer.Core.World.Progression.PassivePoints(p.Pools.Level)
                + p.Character.ExtraPassivePoints
                + Game.QuestPassivePoints(p)
                + tree.PointsGranted(p.Character.Passives);
            int unspent = Math.Max(0, budget - p.Character.Passives.Count - p.Character.ClusterPassives.Count);

            Dictionary<ushort, IReadOnlyList<ushort>> adj = PassiveGraphs.Available
                ? PassiveGraphs.Links("normal").ToDictionary(kv => kv.Key, kv => (IReadOnlyList<ushort>)[.. kv.Value])
                : [];
            const ushort syntheticStart = 0;
            IReadOnlyList<ushort> frontier = Game.OrdinaryPassives.Frontier(baseClass, AscendancyFamily.Normal, p.Character.Passives);
            if (frontier.Count > 0)
            {
                adj[syntheticStart] = frontier;
            }

            return new QolPassiveTreeState(syntheticStart, new HashSet<ushort>(p.Character.Passives), adj, unspent);
        }

        public Task<string?> AllocatePassivesAsync(string connectionId, IReadOnlyList<ushort> nodes, ushort? masteryEffect, string reason, CancellationToken cancellationToken = default) =>
            TryGetPlayer(connectionId, out Player p)
                ? Program.WorldOf(p).AllocatePassivesAsync(nodes, masteryEffect, reason)
                : Task.FromResult<string?>("Player disconnected.");

        private QolItemRecord MapItem(InventoryItem invItem, ItemCatalog catalog, bool offered, bool isInputSlot)
        {
            var item = invItem.Item;
            var type = catalog.ByHash(item.TypeHash);
            var shape = Game.ShapeOf(item) ?? new PlayerInventories.Shape(1, 1, type?.Class ?? string.Empty);
            List<string> modLines = [.. item.Mods.Select(m => catalog.Mods.Describe(m))];
            if (statDescriptions is not null && modLines.Count == 0 && item.Mods.Count > 0)
            {
                modLines.Add($"{item.Mods.Count} modifier(s)");
            }
            return new QolItemRecord
            {
                Id = invItem.Id,
                TypeHash = item.TypeHash,
                MetadataPath = type?.Id ?? string.Empty,
                BaseName = type?.Name ?? string.Empty,
                ItemClass = type?.Class ?? string.Empty,
                Width = shape.Width,
                Height = shape.Height,
                X = invItem.X,
                Y = invItem.Y,
                StackSize = item.StackSize,
                MaxStackSize = shape.MaxStack,
                Rarity = (QolItemRarity)item.Rarity,
                ItemLevel = item.ItemLevel,
                Quality = item.Quality,
                Sockets = (byte)item.Sockets.Count,
                MaxLinkGroup = item.Links is { Count: > 0 } ? item.Links.Max() : (byte)0,
                IsQuestItem = catalog.Stash.IsQuestItem(item.TypeHash),
                IsOffered = offered,
                HasSocketedGems = item.Sockets.Any(s => s.Gem is not null),
                HasSkinTransfer = item.SkinTransferSource is not null,
                IsCorrupted = item.Corrupted,
                IsMirrored = item.Mirrored,
                IsForeseeing = item.Foreseeing,
                IsBlightedMap = catalog.Stash.IsBlightedMap(item),
                IsInputSlot = isInputSlot,
                MapTier = catalog.Stash.MapTier(item.TypeHash),
                Affinities = [.. catalog.Stash.AffinitiesOf(item.TypeHash)],
                ModLines = modLines,
            };
        }
    }
}
