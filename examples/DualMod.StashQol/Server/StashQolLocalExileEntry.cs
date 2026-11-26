#nullable enable
// Ready-to-drop LocalExile Gateway IMod entry point for DualMod.StashQol (SDK 0.3.0 / Core Hooks Part 2a).
// Wires StashQolServerMod onto LocalExileGatewayHost using LocalExile Core Hooks Part 2a APIs:
// - StashRules.AffinitiesOf / AccountStash.FindAffinityTab (Deposit All)
// - GameEvents.VendorQuote (Vendor All)
// - Saves.Atomically + 0x00BF InstanceItems.InventoryChange + Game.SendTabChange (Sort, Map 3->1, Blight Oils, Regex Roll)
// - Game.UseItem / stash Orb of Regret & Orb of Unmaking self-use (Ctrl+Shift+RMB Bulk Use)
// - GameEvents.LevelUp + Program.WorldOf(player).AllocatePassivesAsync (PoB Auto-Apply + HUD Toast)

using System;
using System.Collections.Concurrent;
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
    private LocalExileStashQolBackend? _backend;
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
                    _backend?.ClearPlayerCache(ctx.ConnectionId);
                    _serverMod?.OnPlayerLeft(ctx.ConnectionId);
                }

                return Task.CompletedTask;
            }));

        _backend = new LocalExileStashQolBackend(_gatewayHost, _statDescriptions, mod.Log);
        _serverMod = new StashQolServerMod(_gatewayHost.Bridge, _backend, settings);
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
        private readonly ConcurrentDictionary<(string ConnectionId, uint InventoryId, uint ItemId), Item> _workingRolledItems = new();
        private Dictionary<uint, (string TargetMetadata, string TargetName)>? _mapUpgradeCache;
        private ItemCatalog? _mapUpgradeCatalog;

        public void ClearPlayerCache(string connectionId)
        {
            foreach (var key in _workingRolledItems.Keys)
            {
                if (string.Equals(key.ConnectionId, connectionId, StringComparison.Ordinal))
                {
                    _workingRolledItems.TryRemove(key, out _);
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

        public QolVendorQuote QuoteVendor(string connectionId, int sourceTabIndex, IReadOnlyList<QolItemRecord> offered)
        {
            if (!TryGetPlayer(connectionId, out Player p)) return new QolVendorQuote([], [], "Player disconnected.");
            var stash = Game.PersonalStashOf(p);
            if (sourceTabIndex < 0 || sourceTabIndex >= stash.Count) return new QolVendorQuote([], [], "Source stash tab not found.");
            var catalog = Game.ItemsFor(p);
            HashSet<uint> wanted = [.. offered.Select(o => o.Id)];
            List<InventoryItem> liveItems = [.. stash.Inventory(sourceTabIndex).Items.Where(it => wanted.Contains(it.Id))];

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

        public QolItemRecord? SimulateCurrencyRoll(string connectionId, uint targetInventoryId, QolItemRecord target, string currencyMetadataPath, int rollIndex)
        {
            if (!TryGetPlayer(connectionId, out Player p)) return null;
            var catalog = Game.ItemsFor(p);
            var (_, live) = Game.Locate(Game.ItemsOf(p), Game.PersonalStashOf(p), targetInventoryId, target.Id);
            if (live is null) return null;

            var rollKey = (connectionId, targetInventoryId, target.Id);
            Item working = rollIndex <= 1
                ? live.Item
                : _workingRolledItems.TryGetValue(rollKey, out Item? prior) ? prior : live.Item;

            Random rng = Game.AreaRng(p);
            Item? rolled = currencyMetadataPath switch
            {
                CurrencyEffects.ChaosOrb => CurrencyEffects.Chaos(working, Game.RollsFor(p), rng),
                CurrencyEffects.OrbOfAlteration => CurrencyEffects.Alteration(working, Game.RollsFor(p), rng),
                CurrencyEffects.OrbOfAlchemy => CurrencyEffects.Alchemy(working, Game.RollsFor(p), rng),
                CurrencyEffects.OrbOfTransmutation => CurrencyEffects.Transmutation(working, Game.RollsFor(p), rng),
                CurrencyEffects.OrbOfAugmentation => CurrencyEffects.Augmentation(working, Game.RollsFor(p), rng),
                CurrencyEffects.OrbOfScouring => CurrencyEffects.Scouring(working, Game.RollsFor(p)),
                CurrencyEffects.RegalOrb => CurrencyEffects.Regal(working, Game.RollsFor(p), rng),
                CurrencyEffects.ExaltedOrb => CurrencyEffects.Exalted(working, Game.RollsFor(p), rng),
                CurrencyEffects.OrbOfAnnulment => CurrencyEffects.Annulment(working, Game.RollsFor(p), rng),
                CurrencyEffects.ChromaticOrb => catalog.ByHash(working.TypeHash) is { } reqType
                    ? CurrencyEffects.Chromatic(working, catalog.Requirements(reqType), rng) : null,
                CurrencyEffects.JewellersOrb => catalog.ByHash(working.TypeHash) is { } sockType
                    ? CurrencyEffects.Jewellers(working, sockType, rng) : null,
                CurrencyEffects.OrbOfFusing => catalog.ByHash(working.TypeHash) is { } linkType
                    ? CurrencyEffects.Fusing(working, linkType, rng) : null,
                _ => null,
            };

            if (rolled is null)
            {
                return null;
            }

            _workingRolledItems[rollKey] = rolled;
            return MapItem(live with { Item = rolled }, catalog, false, false);
        }

        public bool CommitRegexRoll(string connectionId, uint targetInventoryId, uint targetItemId, string currencyMetadataPath, int orbsConsumed, QolItemRecord finalItem, out string error)
        {
            error = string.Empty;
            var rollKey = (connectionId, targetInventoryId, targetItemId);
            if (!_workingRolledItems.TryRemove(rollKey, out Item? finalLiveItem))
            {
                error = "No rolled item state available to commit.";
                return false;
            }

            if (!TryGetPlayer(connectionId, out Player p))
            {
                error = "Player disconnected.";
                return false;
            }

            var items = Game.ItemsOf(p);
            var stash = Game.PersonalStashOf(p);
            var catalog = Game.ItemsFor(p);
            if (catalog.Find(currencyMetadataPath) is not { } orbType)
            {
                error = $"Unknown currency '{currencyMetadataPath}'.";
                return false;
            }

            var (spot, liveTarget) = Game.Locate(items, stash, targetInventoryId, targetItemId);
            if (liveTarget is null)
            {
                error = "Target item no longer exists.";
                return false;
            }

            if (Game.Worn(spot))
            {
                error = "Cannot commit regex roll on an equipped item.";
                return false;
            }

            if (spot.Tab is int publicCheckTab && stash.IsPublic(publicCheckTab))
            {
                error = "Cannot commit regex roll on an item in a public stash tab.";
                return false;
            }

            PlayerInventories.Payment? invPayment = null;
            AccountStash.Payment? stashPayment = null;
            InventoryItem? updatedTarget = null;
            string failureReason = string.Empty;

            bool committed = Saves.Atomically(() =>
            {
                int inInv = items.Count(PlayerInventories.MainInventory, orbType.Hash);
                int inStash = stash.CountOf(orbType.Hash);
                if (inInv + inStash < orbsConsumed)
                {
                    failureReason = "Insufficient currency.";
                    return false;
                }

                int fromInv = Math.Min(inInv, orbsConsumed);
                int fromStash = orbsConsumed - fromInv;
                if (fromInv > 0)
                {
                    invPayment = items.Pay(PlayerInventories.MainInventory, orbType.Hash, fromInv);
                    if (invPayment is null)
                    {
                        failureReason = "Failed to deduct currency from inventory.";
                        return false;
                    }
                }

                if (fromStash > 0)
                {
                    stashPayment = stash.Pay(orbType.Hash, fromStash);
                    if (stashPayment is null)
                    {
                        failureReason = "Failed to deduct currency from stash.";
                        return false;
                    }
                }

                if (spot.Row is byte targetRow)
                {
                    updatedTarget = items.Change(targetRow, targetItemId, _ => finalLiveItem);
                }
                else if (spot.Tab is int targetTab)
                {
                    updatedTarget = stash.Change(targetTab, targetItemId, _ => finalLiveItem);
                }

                if (updatedTarget is null)
                {
                    failureReason = "Failed to replace target item.";
                    return false;
                }

                return true;
            });

            if (!committed)
            {
                error = string.IsNullOrEmpty(failureReason) ? "Regex roll commit failed." : failureReason;
                return false;
            }

            // Emit 0x00BF inventory diff(s) for deducted currency and updated target item.
            if (invPayment is not null || (spot.Row is byte r && r == PlayerInventories.MainInventory && updatedTarget is not null))
            {
                List<uint> removed = [];
                List<InventoryItem> changed = [];
                if (invPayment is not null)
                {
                    removed.AddRange(invPayment.Removed);
                    removed.AddRange(invPayment.Changed.Select(c => c.Id));
                    changed.AddRange(invPayment.Changed);
                }

                if (spot.Row is byte targetRow && targetRow == PlayerInventories.MainInventory && updatedTarget is not null)
                {
                    removed.Add(updatedTarget.Id);
                    changed.Add(updatedTarget);
                }

                _ = Program.Send(p, InstanceItems.InventoryChange(
                    PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                    items.NextRevision(PlayerInventories.MainInventory),
                    removed,
                    changed), "0x00bf regex roll inventory");
            }
            else if (spot.Row is byte otherRow && updatedTarget is not null)
            {
                _ = Program.Send(p, InstanceItems.InventoryChange(
                    PlayerInventories.InventoryId(otherRow),
                    items.NextRevision(otherRow),
                    [updatedTarget.Id],
                    [updatedTarget]), "0x00bf regex roll target");
            }

            // Emit 0x00BF stash tab diff(s) for deducted stash currency and/or stash target item.
            Dictionary<int, (List<uint> Removed, List<InventoryItem> Changed)> tabDiffs = [];
            if (stashPayment is not null)
            {
                foreach (var (tabIdx, (rem, chg)) in stashPayment.Tabs)
                {
                    List<uint> tabRem = [.. rem, .. chg.Select(c => c.Id)];
                    tabDiffs[tabIdx] = (tabRem, [.. chg]);
                }
            }

            if (spot.Tab is int updatedTab && updatedTarget is not null)
            {
                if (!tabDiffs.TryGetValue(updatedTab, out var diff))
                {
                    diff = ([], []);
                    tabDiffs[updatedTab] = diff;
                }

                diff.Removed.Add(updatedTarget.Id);
                diff.Changed.Add(updatedTarget);
            }

            foreach (var (tabIdx, (rem, chg)) in tabDiffs)
            {
                _ = Game.SendTabChange(p, stash, tabIdx, rem, chg, $"0x00bf regex roll tab {tabIdx}");
            }

            log($"[stashqol] Committed regex roll ({orbsConsumed}x {currencyMetadataPath}) on item {targetItemId} (inv {targetInventoryId}).");
            return true;
        }

        public async Task<BulkStackedDeckResult> BulkOpenStackedDecksAsync(string connectionId, uint inventoryId, uint itemId, int maxCount, CancellationToken cancellationToken = default)
        {
            if (!TryGetPlayer(connectionId, out Player p)) return new BulkStackedDeckResult(false, 0, false, "Player disconnected.");
            var items = Game.ItemsOf(p);
            var stash = Game.PersonalStashOf(p);

            var (initialSpot, initialItem) = Game.Locate(items, stash, inventoryId, itemId);
            if (initialItem is null) return new BulkStackedDeckResult(false, 0, false, "Stacked Deck not found.");
            if (initialSpot.Tab is int pubTab && stash.IsPublic(pubTab)) return new BulkStackedDeckResult(false, 0, false, "Cannot use items from a public stash tab.");

            int opened = 0;
            bool stoppedFull = false;
            var useReq = new InstanceItems.UseItemRequest(inventoryId, itemId);

            for (int i = 0; i < maxCount && !cancellationToken.IsCancellationRequested; i++)
            {
                var (_, before) = Game.Locate(items, stash, inventoryId, itemId);
                if (before is null || before.Item.StackSize == 0)
                {
                    break;
                }

                if (items.FreeCell(PlayerInventories.MainInventory, 1, 1, Game.ShapeOf) is null)
                {
                    stoppedFull = true;
                    break;
                }

                ushort beforeStack = before.Item.StackSize;
                await Game.UseItem(p, useReq, "stashqol-bulk");

                var (_, after) = Game.Locate(items, stash, inventoryId, itemId);
                ushort afterStack = after?.Item.StackSize ?? 0;
                if (afterStack >= beforeStack)
                {
                    if (items.FreeCell(PlayerInventories.MainInventory, 1, 1, Game.ShapeOf) is null)
                    {
                        stoppedFull = true;
                    }

                    break;
                }

                opened++;
            }

            if (opened == 0)
            {
                return new BulkStackedDeckResult(false, 0, stoppedFull, stoppedFull ? "Main inventory is full." : "Could not open Stacked Deck.");
            }

            return new BulkStackedDeckResult(true, opened, stoppedFull);
        }

        public async Task<BulkRefundOrbResult> BulkUseRefundOrbAsync(string connectionId, uint inventoryId, uint itemId, bool isAtlasUnmaking, int maxCount, CancellationToken cancellationToken = default)
        {
            if (!TryGetPlayer(connectionId, out Player p)) return new BulkRefundOrbResult(false, 0, 0, "Player disconnected.");
            var items = Game.ItemsOf(p);
            var stash = Game.PersonalStashOf(p);

            var (initialSpot, initialItem) = Game.Locate(items, stash, inventoryId, itemId);
            if (initialItem is null) return new BulkRefundOrbResult(false, 0, 0, "Orb stack not found.");
            if (initialSpot.Tab is int pubTab && stash.IsPublic(pubTab)) return new BulkRefundOrbResult(false, 0, 0, "Cannot use orbs from a public stash tab.");

            int used = 0;
            var useReq = new InstanceItems.UseItemRequest(inventoryId, itemId);

            for (int i = 0; i < maxCount && !cancellationToken.IsCancellationRequested; i++)
            {
                var (_, before) = Game.Locate(items, stash, inventoryId, itemId);
                if (before is null || before.Item.StackSize == 0)
                {
                    break;
                }

                ushort beforeStack = before.Item.StackSize;
                int beforeBalance = isAtlasUnmaking ? Game.AtlasRespecPoints(p) : Program.WorldOf(p).RefundPoints;
                await Game.UseItem(p, useReq, "stashqol-bulk");

                var (_, after) = Game.Locate(items, stash, inventoryId, itemId);
                ushort afterStack = after?.Item.StackSize ?? 0;
                int afterBalance = isAtlasUnmaking ? Game.AtlasRespecPoints(p) : Program.WorldOf(p).RefundPoints;
                if (afterStack >= beforeStack && afterBalance <= beforeBalance)
                {
                    break;
                }

                used++;
            }

            int finalBalance = isAtlasUnmaking ? Game.AtlasRespecPoints(p) : Program.WorldOf(p).RefundPoints;
            return used > 0
                ? new BulkRefundOrbResult(true, used, finalBalance)
                : new BulkRefundOrbResult(false, 0, finalBalance, "Could not consume refund orb stack.");
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
            var items = Game.ItemsOf(p);
            var stash = Game.PersonalStashOf(p);

            var (curSpot, curInit) = Game.Locate(items, stash, currencyInventoryId, currencyItemId);
            if (curInit is null) return new BulkTargetedCurrencyResult(false, 0, string.Empty, "Currency stack not found.");
            if (curSpot.Tab is int curPub && stash.IsPublic(curPub)) return new BulkTargetedCurrencyResult(false, 0, string.Empty, "Cannot consume currency from a public stash tab.");

            var (tgtSpot, tgtInit) = Game.Locate(items, stash, targetInventoryId, targetItemId);
            if (tgtInit is null) return new BulkTargetedCurrencyResult(false, 0, string.Empty, "Target item not found.");
            if (tgtSpot.Tab is int tgtPub && stash.IsPublic(tgtPub)) return new BulkTargetedCurrencyResult(false, 0, string.Empty, "Cannot modify an item in a public stash tab.");

            bool isQualityOrb = currencyMetadataPath.Contains("Quality", StringComparison.OrdinalIgnoreCase);
            if (Game.Worn(tgtSpot) && !isQualityOrb)
            {
                return new BulkTargetedCurrencyResult(false, 0, string.Empty, "Cannot use this currency on an equipped item.");
            }

            var request = new InstanceItems.UseItemOn(currencyInventoryId, currencyItemId, targetInventoryId, targetItemId);
            int applied = 0;
            string stopReason = string.Empty;

            for (int i = 0; i < maxCount && !cancellationToken.IsCancellationRequested; i++)
            {
                var (_, curBefore) = Game.Locate(items, stash, currencyInventoryId, currencyItemId);
                var (_, tgtBefore) = Game.Locate(items, stash, targetInventoryId, targetItemId);
                if (curBefore is null || curBefore.Item.StackSize == 0)
                {
                    stopReason = "stack exhausted";
                    break;
                }

                if (tgtBefore is null)
                {
                    stopReason = "target unavailable";
                    break;
                }

                ushort beforeStack = curBefore.Item.StackSize;
                Item beforeTargetItem = tgtBefore.Item;

                await Game.UseItemOn(p, request, "stashqol-bulk");

                var (_, curAfter) = Game.Locate(items, stash, currencyInventoryId, currencyItemId);
                var (_, tgtAfter) = Game.Locate(items, stash, targetInventoryId, targetItemId);
                ushort afterStack = curAfter?.Item.StackSize ?? 0;

                if (afterStack >= beforeStack)
                {
                    stopReason = "target completed or max reached";
                    break;
                }

                applied++;
                if (afterStack == 0)
                {
                    stopReason = "stack exhausted";
                    break;
                }

                if (tgtAfter is not null && ReferenceEquals(tgtAfter.Item, beforeTargetItem))
                {
                    stopReason = "target unchanged";
                    break;
                }
            }

            if (applied == 0)
            {
                return new BulkTargetedCurrencyResult(false, 0, stopReason, string.IsNullOrEmpty(stopReason) ? "Currency could not be applied." : stopReason);
            }

            if (string.IsNullOrEmpty(stopReason))
            {
                stopReason = "reached action cap";
            }

            return new BulkTargetedCurrencyResult(true, applied, stopReason);
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

        private Dictionary<uint, (string TargetMetadata, string TargetName)> GetMapUpgradeTargets(ItemCatalog catalog)
        {
            if (_mapUpgradeCache is not null && ReferenceEquals(_mapUpgradeCatalog, catalog))
            {
                return _mapUpgradeCache;
            }

            Dictionary<uint, (string TargetMetadata, string TargetName)> upgrades = [];
            if (ClientTables.TableOrNull("Maps") is { } mapsTable)
            {
                foreach (var map in mapsTable)
                {
                    if (map.IntOrNull("MapUpgrade_BaseItemTypesKey") is int next
                        && catalog.Row(map.Int("BaseItemTypesKey")) is { Supported: true } from
                        && catalog.Row(next) is { Supported: true } to)
                    {
                        upgrades.TryAdd(from.Hash, (to.Id, to.Name));
                    }
                }
            }

            _mapUpgradeCatalog = catalog;
            _mapUpgradeCache = upgrades;
            return upgrades;
        }

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

            (string TargetMetadata, string TargetName)? mapUpgrade = GetMapUpgradeTargets(catalog).TryGetValue(item.TypeHash, out var upgrade)
                ? upgrade
                : null;

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
                MapUpgradeTargetMetadata = mapUpgrade?.TargetMetadata,
                MapUpgradeTargetName = mapUpgrade?.TargetName,
                Affinities = [.. catalog.Stash.AffinitiesOf(item.TypeHash)],
                ModLines = modLines,
            };
        }
    }
}
