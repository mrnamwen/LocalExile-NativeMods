using System.IO.Compression;
using System.Text;
using DualMod.StashQol.Server;
using DualMod.StashQol.Shared;
using LocalExile.Client.ServerBridge;
using Xunit;

namespace DualMod.StashQol.Tests;

public sealed class StashQolServerFeatureTests
{
    private const string ConnId = "exile-server-test";

    private sealed class NullGatewaySink : IGatewayPacketSink
    {
        public Task SendPacketAsync(string connectionId, ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendChatReplyAsync(string connectionId, string text, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static (StashQolServerMod Server, InMemoryStashQolWorldBackend Backend, InMemoryStashQolWorldBackend.PlayerState Player) CreateHarness(
        StashQolSettings? settings = null)
    {
        var bridge = new ClientBridgeHost(new NullGatewaySink());
        var backend = new InMemoryStashQolWorldBackend();
        var server = new StashQolServerMod(bridge, backend, settings);
        server.Attach();
        return (server, backend, backend.GetOrCreatePlayer(ConnId));
    }

    [Fact]
    public void DepositAll_RoutesAffinitiesAndFallback_MergesStacks_AndSkipsQuestAndOfferedItems()
    {
        var (server, backend, player) = CreateHarness();
        using var _ = server;

        player.StashTabs.AddRange(
        [
            new QolStashTab(0, "Dump", QolStashTabKind.Normal, DisplayOrder: 0),
            new QolStashTab(1, "Currency", QolStashTabKind.Currency, AffinityMask: 1u << 1, DisplayOrder: 1),
            new QolStashTab(2, "Maps", QolStashTabKind.Map, AffinityMask: 1u << 2, DisplayOrder: 2),
            new QolStashTab(3, "PublicShop", QolStashTabKind.Premium, IsPublic: true, AffinityMask: 1u << 1, DisplayOrder: 0),
        ]);
        player.TabItems[1] =
        [
            new QolItemRecord
            {
                Id = 50,
                MetadataPath = StashQolProtocol.ChaosOrbMetadata,
                BaseName = "Chaos Orb",
                ItemClass = "StackableCurrency",
                StackSize = 10,
                MaxStackSize = 20,
                Affinities = [1],
            },
        ];

        player.MainInventory.AddRange(
        [
            new QolItemRecord { Id = 1, MetadataPath = StashQolProtocol.ChaosOrbMetadata, BaseName = "Chaos Orb", ItemClass = "StackableCurrency", StackSize = 5, MaxStackSize = 20, Affinities = [1] },
            new QolItemRecord { Id = 2, MetadataPath = "Metadata/Items/Maps/MapKeyStrand", BaseName = "Strand Map", ItemClass = "Map", Affinities = [2] },
            new QolItemRecord { Id = 3, MetadataPath = "Metadata/Items/Armours/BodyArmours/BodyStr1", BaseName = "Plate Vest", ItemClass = "Body Armour", Width = 2, Height = 3 },
            new QolItemRecord { Id = 4, MetadataPath = "Metadata/Items/QuestItems/Act1/MedicineChest", BaseName = "Medicine Chest", ItemClass = "QuestItem", IsQuestItem = true },
            new QolItemRecord { Id = 5, MetadataPath = "Metadata/Items/Rings/Ring1", BaseName = "Iron Ring", ItemClass = "Ring", IsOffered = true },
        ]);

        // Refuses when stash is closed.
        player.StashOpen = false;
        DepositAllOutcome closedOutcome = server.ExecuteDepositAll(ConnId, new DepositAllRequest(FallbackTabIndex: 0));
        Assert.False(closedOutcome.Success);
        Assert.Equal(5, player.MainInventory.Count);

        // Deposits with stash open.
        player.StashOpen = true;
        DepositAllOutcome outcome = server.ExecuteDepositAll(ConnId, new DepositAllRequest(FallbackTabIndex: 0));
        Assert.True(outcome.Success);
        Assert.Equal(3, outcome.DepositedCount);
        Assert.Equal(2, outcome.SkippedCount);

        // Quest + offered items stay in MainInventory.
        Assert.Equal(2, player.MainInventory.Count);
        Assert.Contains(player.MainInventory, i => i.Id == 4);
        Assert.Contains(player.MainInventory, i => i.Id == 5);

        // Chaos merged into Currency affinity tab (10 + 5 = 15), NOT PublicShop!
        Assert.Equal((ushort)15, backend.GetStashTabItems(ConnId, 1).Single().StackSize);
        Assert.Empty(backend.GetStashTabItems(ConnId, 3));

        // Map routed to Maps affinity tab; Plate Vest fell back to Dump tab 0.
        Assert.Contains(backend.GetStashTabItems(ConnId, 2), i => i.Id == 2);
        Assert.Contains(backend.GetStashTabItems(ConnId, 0), i => i.Id == 3);
    }

    [Fact]
    public void VendorAll_RefusesUnsafeTabsAndProtectedItems_RequiresConfirmation_AndCommitsAtomically()
    {
        var (server, backend, player) = CreateHarness();
        using var _ = server;

        player.StashTabs.AddRange(
        [
            new QolStashTab(0, "Loot", QolStashTabKind.Normal),
            new QolStashTab(1, "PublicTrade", QolStashTabKind.Premium, IsPublic: true),
            new QolStashTab(2, "Folder", QolStashTabKind.Folder),
            new QolStashTab(3, "HiddenTab", QolStashTabKind.Normal, IsHidden: true),
            new QolStashTab(4, "Maps", QolStashTabKind.Map, IsSubTab: false),
            new QolStashTab(5, "Uniques", QolStashTabKind.Unique, IsSubTab: false),
        ]);
        player.TabItems[0] =
        [
            new QolItemRecord { Id = 10, BaseName = "Rustic Sash", ItemClass = "Belt", Rarity = QolItemRarity.Rare },
            new QolItemRecord { Id = 11, BaseName = "Hubris Circlet", ItemClass = "Helmet", HasSocketedGems = true },
            new QolItemRecord { Id = 12, BaseName = "Vaal Regalia", ItemClass = "Body Armour", HasSkinTransfer = true },
            new QolItemRecord { Id = 13, BaseName = "Ruby Ring", ItemClass = "Ring", Rarity = QolItemRarity.Magic },
        ];

        // Unsafe tabs are all rejected.
        Assert.False(server.ExecuteVendorAll(ConnId, new VendorAllRequest(1, Confirmed: true)).Success);
        Assert.False(server.ExecuteVendorAll(ConnId, new VendorAllRequest(2, Confirmed: true)).Success);
        Assert.False(server.ExecuteVendorAll(ConnId, new VendorAllRequest(3, Confirmed: true)).Success);
        Assert.False(server.ExecuteVendorAll(ConnId, new VendorAllRequest(4, Confirmed: true)).Success);
        Assert.False(server.ExecuteVendorAll(ConnId, new VendorAllRequest(5, Confirmed: true)).Success);

        // Unconfirmed request prompts confirmation without mutating tab.
        VendorAllOutcome unconfirmed = server.ExecuteVendorAll(ConnId, new VendorAllRequest(0, Confirmed: false));
        Assert.False(unconfirmed.Success);
        Assert.True(unconfirmed.RequiresConfirmation);
        Assert.Equal(4, backend.GetStashTabItems(ConnId, 0).Count);

        // Confirmed sells ONLY the 2 eligible items; socketed gem & skin transfer items remain untouched.
        VendorAllOutcome confirmed = server.ExecuteVendorAll(ConnId, new VendorAllRequest(0, Confirmed: true));
        Assert.True(confirmed.Success);
        Assert.Equal(2, confirmed.SoldCount);
        Assert.Equal(1, confirmed.RewardStacks);

        IReadOnlyList<QolItemRecord> remaining = backend.GetStashTabItems(ConnId, 0);
        Assert.Equal(2, remaining.Count);
        Assert.Contains(remaining, i => i.Id == 11);
        Assert.Contains(remaining, i => i.Id == 12);
        Assert.Single(player.MainInventory);
        Assert.Equal("Scroll Fragment", player.MainInventory[0].BaseName);
    }

    [Fact]
    public void InventorySort_Deterministic12x5BinPack_ZeroOverlaps_AndBlocksDuringTrade()
    {
        var (server, _, player) = CreateHarness();
        using var _ = server;

        player.MainInventory.AddRange(
        [
            new QolItemRecord { Id = 1, BaseName = "Chaos Orb", ItemClass = "Currency", Width = 1, Height = 1, X = 11, Y = 4 },
            new QolItemRecord { Id = 2, BaseName = "Astral Plate", ItemClass = "Body Armour", Width = 2, Height = 3, X = 9, Y = 0, Rarity = QolItemRarity.Rare },
            new QolItemRecord { Id = 3, BaseName = "Two-Hand Sword", ItemClass = "Two Hand Sword", Width = 2, Height = 4, X = 6, Y = 1, Rarity = QolItemRarity.Unique },
            new QolItemRecord { Id = 4, BaseName = "Quicksilver Flask", ItemClass = "UtilityFlask", Width = 1, Height = 2, X = 4, Y = 3 },
            new QolItemRecord { Id = 5, BaseName = "Iron Ring", ItemClass = "Ring", Width = 1, Height = 1, X = 3, Y = 4 },
        ]);

        // Blocked while trade/sale is active.
        player.HasActiveTradeOrSale = true;
        Assert.False(server.ExecuteInventorySort(ConnId, new InventorySortRequest()).Success);
        player.HasActiveTradeOrSale = false;

        InventorySortOutcome outcome = server.ExecuteInventorySort(ConnId, new InventorySortRequest());
        Assert.True(outcome.Success);
        Assert.True(outcome.MovedCount > 0);

        // Verify largest area item (2x4 sword) is placed at (0,0), followed by 2x3 armour at (2,0), with zero overlaps.
        QolItemRecord sword = player.MainInventory.Single(i => i.Id == 3);
        QolItemRecord plate = player.MainInventory.Single(i => i.Id == 2);
        Assert.Equal((byte)0, sword.X);
        Assert.Equal((byte)0, sword.Y);
        Assert.Equal((byte)2, plate.X);
        Assert.Equal((byte)0, plate.Y);

        bool[,] grid = new bool[StashQolProtocol.MainInventoryWidth, StashQolProtocol.MainInventoryHeight];
        foreach (QolItemRecord item in player.MainInventory)
        {
            Assert.InRange(item.X + item.Width, 1, StashQolProtocol.MainInventoryWidth);
            Assert.InRange(item.Y + item.Height, 1, StashQolProtocol.MainInventoryHeight);
            for (int dx = 0; dx < item.Width; dx++)
            {
                for (int dy = 0; dy < item.Height; dy++)
                {
                    Assert.False(grid[item.X + dx, item.Y + dy], $"Cell ({item.X + dx},{item.Y + dy}) overlapped!");
                    grid[item.X + dx, item.Y + dy] = true;
                }
            }
        }

        // Running sort again is idempotent (0 moved).
        InventorySortOutcome second = server.ExecuteInventorySort(ConnId, new InventorySortRequest());
        Assert.True(second.Success);
        Assert.Equal(0, second.MovedCount);
    }

    [Fact]
    public void MapTierUpgrade3To1_UpgradesGroup0NormalMapsOnly_AndSkipsCorruptedMirroredUniqueBlightedAndSpecialGroups()
    {
        var (server, backend, player) = CreateHarness();
        using var _ = server;

        player.StashTabs.AddRange(
        [
            new QolStashTab(10, "Maps", QolStashTabKind.Map, IsSubTab: false),
            new QolStashTab(11, "T1", QolStashTabKind.Map, IsSubTab: true, ParentTabIndex: 10, MapGroup: 0, MapTier: 1),
            new QolStashTab(12, "T2", QolStashTabKind.Map, IsSubTab: true, ParentTabIndex: 10, MapGroup: 0, MapTier: 2),
            new QolStashTab(13, "UniqueMaps", QolStashTabKind.Map, IsSubTab: true, ParentTabIndex: 10, MapGroup: 1, MapTier: 1),
        ]);

        QolItemRecord MakeStrand(uint id, byte ilvl, bool corrupted = false, bool mirrored = false, QolItemRarity rarity = QolItemRarity.Normal, bool blighted = false) => new()
        {
            Id = id,
            MetadataPath = "Metadata/Items/Maps/MapKeyStrand",
            BaseName = "Strand Map",
            ItemClass = "Map",
            Rarity = rarity,
            ItemLevel = ilvl,
            MapTier = 1,
            IsCorrupted = corrupted,
            IsMirrored = mirrored,
            IsBlightedMap = blighted,
            MapUpgradeTargetMetadata = "Metadata/Items/Maps/MapKeyDunes",
            MapUpgradeTargetName = "Dunes Map",
        };

        player.TabItems[11] =
        [
            MakeStrand(1, 70),
            MakeStrand(2, 68),
            MakeStrand(3, 72),
            MakeStrand(4, 71),                       // 4th normal -> leftover
            MakeStrand(5, 70, corrupted: true),      // skipped
            MakeStrand(6, 70, mirrored: true),       // skipped
            MakeStrand(7, 70, rarity: QolItemRarity.Unique), // skipped
            MakeStrand(8, 70, blighted: true),       // skipped
        ];
        player.TabItems[13] =
        [
            MakeStrand(20, 70),
            MakeStrand(21, 70),
            MakeStrand(22, 70), // Group 1 -> skipped!
        ];

        MapUpgradeOutcome outcome = server.ExecuteMapTierUpgrades(ConnId, new UpgradeMapTiersRequest(MapTabIndex: 10, SpecificTier: 1));
        Assert.True(outcome.Success);
        Assert.Equal(1, outcome.UpgradedCount);
        Assert.Equal(3, outcome.ConsumedMaps);

        // 1 T2 Dunes Map created with min ilvl = 68.
        IReadOnlyList<QolItemRecord> t2Maps = backend.GetStashTabItems(ConnId, 12);
        QolItemRecord upgraded = Assert.Single(t2Maps);
        Assert.Equal("Dunes Map", upgraded.BaseName);
        Assert.Equal(2, upgraded.MapTier);
        Assert.Equal((byte)68, upgraded.ItemLevel);

        // Leftover + 4 ineligible maps remain in T1; group-1 maps completely untouched.
        Assert.Equal(5, backend.GetStashTabItems(ConnId, 11).Count);
        Assert.Equal(3, backend.GetStashTabItems(ConnId, 13).Count);
    }

    [Fact]
    public void BlightOilUpgradeMax_Cascades13TierChain_IgnoresSpecialOilsAndInputSlots_AndRespects5000Cap()
    {
        var (server, backend, player) = CreateHarness();
        using var _ = server;

        player.StashTabs.Add(new QolStashTab(20, "Blight", QolStashTabKind.Blight));
        player.TabItems[20] =
        [
            // 27 Clear -> 9 Sepia -> 3 Amber -> 1 Verdant (plus 1 leftover Clear)
            new QolItemRecord { Id = 1, BaseName = "Clear Oil", StackSize = 28, MaxStackSize = 5000 },
            // Near 5000 cap: 9 Silver + 4998 Golden -> can only upgrade 2 Golden (consuming 6 Silver, leaving 3 Silver)!
            new QolItemRecord { Id = 2, BaseName = "Silver Oil", StackSize = 9, MaxStackSize = 5000 },
            new QolItemRecord { Id = 3, BaseName = "Golden Oil", StackSize = 4998, MaxStackSize = 5000 },
            // Special non-tiered oil must be untouched.
            new QolItemRecord { Id = 4, BaseName = "Tainted Oil", StackSize = 12, MaxStackSize = 5000 },
            // Anoint input slot oil must be untouched.
            new QolItemRecord { Id = 5, BaseName = "Clear Oil", StackSize = 3, MaxStackSize = 5000, IsInputSlot = true },
        ];

        BlightOilUpgradeOutcome outcome = server.ExecuteBlightOilUpgrades(ConnId, new UpgradeBlightOilsRequest(BlightTabIndex: 20));
        Assert.True(outcome.Success);
        Assert.Equal(9 + 3 + 1 + 2, outcome.TotalUpgrades);
        Assert.Equal("Golden Oil", outcome.HighestTierReached);

        IReadOnlyList<QolItemRecord> items = backend.GetStashTabItems(ConnId, 20);
        Assert.Equal((ushort)1, items.Single(i => i.BaseName == "Clear Oil" && !i.IsInputSlot).StackSize);
        Assert.Equal((ushort)1, items.Single(i => i.BaseName == "Verdant Oil").StackSize);
        Assert.Equal((ushort)3, items.Single(i => i.BaseName == "Silver Oil").StackSize);
        Assert.Equal((ushort)5000, items.Single(i => i.BaseName == "Golden Oil").StackSize);
        Assert.Equal((ushort)12, items.Single(i => i.BaseName == "Tainted Oil").StackSize);
        Assert.Equal((ushort)3, items.Single(i => i.BaseName == "Clear Oil" && i.IsInputSlot).StackSize);
    }

    [Fact]
    public void RollUntilRegex_MatchesBoundedRolls_EnforcesTimeoutAndSafetyGuards()
    {
        var (server, _, player) = CreateHarness(new StashQolSettings(MaxRegexRolls: 100));
        using var _ = server;

        player.CurrencyPool[StashQolProtocol.ChaosOrbMetadata] = 50;
        player.MainInventory.AddRange(
        [
            new QolItemRecord { Id = 100, BaseName = "Imperial Claw", Rarity = QolItemRarity.Rare },
            new QolItemRecord { Id = 101, BaseName = "Corrupted Claw", Rarity = QolItemRarity.Rare, IsCorrupted = true },
            new QolItemRecord { Id = 102, BaseName = "Mirrored Claw", Rarity = QolItemRarity.Rare, IsMirrored = true },
            new QolItemRecord { Id = 103, BaseName = "Locked Claw", Rarity = QolItemRarity.Rare, IsForeseeing = true },
        ]);
        player.WornItems.Add(new QolItemRecord { Id = 104, BaseName = "Equipped Helm", Rarity = QolItemRarity.Rare });

        // Safety guards reject corrupted, mirrored, Foreseeing, worn, and invalid regex.
        Assert.False(server.ExecuteRollUntilRegex(ConnId, new RollUntilRegexRequest(1, 101, 0, 0, StashQolProtocol.ChaosOrbMetadata, "Life")).Success);
        Assert.False(server.ExecuteRollUntilRegex(ConnId, new RollUntilRegexRequest(1, 102, 0, 0, StashQolProtocol.ChaosOrbMetadata, "Life")).Success);
        Assert.False(server.ExecuteRollUntilRegex(ConnId, new RollUntilRegexRequest(1, 103, 0, 0, StashQolProtocol.ChaosOrbMetadata, "Life")).Success);
        Assert.False(server.ExecuteRollUntilRegex(ConnId, new RollUntilRegexRequest(1, 104, 0, 0, StashQolProtocol.ChaosOrbMetadata, "Life")).Success);
        Assert.False(server.ExecuteRollUntilRegex(ConnId, new RollUntilRegexRequest(1, 100, 0, 0, StashQolProtocol.ChaosOrbMetadata, "[invalid(")).Success);

        // Default simulator hits "+90 to maximum Life" on roll #7 (20 + 7*10 = 90).
        RegexRollOutcome matchOutcome = server.ExecuteRollUntilRegex(
            ConnId,
            new RollUntilRegexRequest(1, 100, 0, 0, StashQolProtocol.ChaosOrbMetadata, @"\+9\d to maximum Life", MaxRolls: 25));
        Assert.True(matchOutcome.Success);
        Assert.True(matchOutcome.Matched);
        Assert.Equal(7, matchOutcome.RollsUsed);
        Assert.Equal(43, player.CurrencyPool[StashQolProtocol.ChaosOrbMetadata]);
        Assert.Contains("+90 to maximum Life", player.MainInventory.Single(i => i.Id == 100).ModLines);

        // Catastrophic backtracking pattern hits the 50ms regex timeout cleanly without hanging.
        player.CustomRollSimulator = (item, _, _) => item with { ModLines = [new string('a', 40) + "!"] };
        RegexRollOutcome timeoutOutcome = server.ExecuteRollUntilRegex(
            ConnId,
            new RollUntilRegexRequest(1, 100, 0, 0, StashQolProtocol.ChaosOrbMetadata, "(a+)+$", MaxRolls: 5));
        Assert.False(timeoutOutcome.Success);
        Assert.Contains("timed out", timeoutOutcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BulkStackUse_SupportsStackedDecksRefundOrbsAndTargetedOrbsWithGuards()
    {
        var (server, _, player) = CreateHarness();
        using var _ = server;

        player.StashTabs.AddRange(
        [
            new QolStashTab(0, "Currency", QolStashTabKind.Currency),
            new QolStashTab(1, "PublicTab", QolStashTabKind.Premium, IsPublic: true),
        ]);
        player.MaxMainInventorySlots = 5; // 1 slot already used by Stacked Decks + 1 by target chest = 3 free slots!
        player.MainInventory.AddRange(
        [
            new QolItemRecord { Id = 200, MetadataPath = StashQolProtocol.StackedDeckMetadata, BaseName = "Stacked Deck", StackSize = 10, MaxStackSize = 20 },
            new QolItemRecord { Id = 201, MetadataPath = "Metadata/Items/Armours/BodyArmours/BodyStr1", BaseName = "Astral Plate", Width = 1, Height = 1, Sockets = 6, MaxSockets = 6, MaxLinkGroup = 3, Quality = 10 },
        ]);
        player.TabItems[0] =
        [
            new QolItemRecord { Id = 300, MetadataPath = StashQolProtocol.OrbOfRegretMetadata, BaseName = "Orb of Regret", StackSize = 12, MaxStackSize = 40 },
            new QolItemRecord { Id = 301, MetadataPath = StashQolProtocol.OrbOfUnmakingMetadata, BaseName = "Orb of Unmaking", StackSize = 7, MaxStackSize = 40 },
            new QolItemRecord { Id = 302, MetadataPath = "Metadata/Items/Currency/CurrencyRerollSocketLinks", BaseName = "Orb of Fusing", StackSize = 20, MaxStackSize = 20 },
        ];
        player.TabItems[1] =
        [
            new QolItemRecord { Id = 400, MetadataPath = StashQolProtocol.OrbOfRegretMetadata, BaseName = "Orb of Regret", StackSize = 5, MaxStackSize = 40 },
        ];

        // 1. Stacked Decks stop cleanly when main inventory fills (3 free slots).
        BulkUseOutcome decks = await server.ExecuteBulkUseAsync(
            ConnId,
            new BulkUseRequest(1, 200, 0, 0, StashQolProtocol.StackedDeckMetadata, 10));
        Assert.True(decks.Success);
        Assert.Equal(3, decks.ActionsExecuted);
        Assert.Contains("inventory full", decks.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal((ushort)7, player.MainInventory.Single(i => i.Id == 200).StackSize);

        // 2. Orb of Regret from non-public stash tab succeeds; public stash tab is refused.
        BulkUseOutcome regrets = await server.ExecuteBulkUseAsync(
            ConnId,
            new BulkUseRequest(0x800, 300, 0, 0, StashQolProtocol.OrbOfRegretMetadata, 12));
        Assert.True(regrets.Success);
        Assert.Equal(12, regrets.ActionsExecuted);
        Assert.Equal(12, player.RefundPoints);

        BulkUseOutcome publicRegrets = await server.ExecuteBulkUseAsync(
            ConnId,
            new BulkUseRequest(0x801, 400, 0, 0, StashQolProtocol.OrbOfRegretMetadata, 5));
        Assert.False(publicRegrets.Success);
        Assert.Equal(12, player.RefundPoints);

        // 3. Orb of Unmaking from stash grants Atlas respec points.
        BulkUseOutcome unmakings = await server.ExecuteBulkUseAsync(
            ConnId,
            new BulkUseRequest(0x800, 301, 0, 0, StashQolProtocol.OrbOfUnmakingMetadata, 7));
        Assert.True(unmakings.Success);
        Assert.Equal(7, player.AtlasRespecPoints);

        // 4. Targeted Orb of Fusing stops immediately at 6-link (3 -> 6 takes 3 fusings, leaving 17).
        BulkUseOutcome fusings = await server.ExecuteBulkUseAsync(
            ConnId,
            new BulkUseRequest(
                HoveredInventoryId: 1,
                HoveredItemId: 201,
                HoveredGridX: 1,
                HoveredGridY: 0,
                HoveredMetadataPath: "Metadata/Items/Armours/BodyArmours/BodyStr1",
                HoveredStackSize: 1,
                CursorSourceInventoryId: 0x800,
                CursorItemId: 302,
                CursorMetadataPath: "Metadata/Items/Currency/CurrencyRerollSocketLinks",
                CursorStackSize: 20));
        Assert.True(fusings.Success);
        Assert.Equal(3, fusings.ActionsExecuted);
        Assert.Equal((byte)6, player.MainInventory.Single(i => i.Id == 201).MaxLinkGroup);
        Assert.Equal((ushort)17, player.TabItems[0].Single(i => i.Id == 302).StackSize);
    }

    [Fact]
    public async Task PobAutoApplyPassives_ParsesBase64XmlAndNodeLists_EnforcesFrontierOrderAndIsolatesMasteries()
    {
        var (server, _, player) = CreateHarness();
        using var _ = server;

        // Graph: Start(1) -> 10 -> 20 (mastery=555) -> 30 -> 40
        Dictionary<ushort, IReadOnlyList<ushort>> adj = new()
        {
            [1] = [10],
            [10] = [1, 20],
            [20] = [10, 30],
            [30] = [20, 40],
            [40] = [30],
        };
        player.PassiveTree = new QolPassiveTreeState(
            ClassStartNode: 1,
            AllocatedNodes: new HashSet<ushort>(),
            Adjacency: adj,
            UnspentPoints: 3);

        // Intentionally shuffled node order in PoB XML compressed with zlib + URL-safe Base64!
        const string xml = """
<PathOfBuilding>
  <Tree activeSpec="1">
    <Spec nodes="40,20,30,10" masteryEffects="{20,555}" />
  </Tree>
</PathOfBuilding>
""";
        string pobCode = EncodePobPayload(xml);

        // Import allocates 10 (normal batch), then 20 (isolated mastery=555 call), then 30 (normal batch).
        PobApplyOutcome importOutcome = await server.ImportAndApplyPobPlanAsync(
            ConnId,
            new PobImportRequest(pobCode, AutoApplyOnLevelUp: true, ApplyImmediately: true));
        Assert.True(importOutcome.Success);
        Assert.Equal<ushort>([10, 20, 30], importOutcome.AllocatedNodes);
        Assert.Equal(1, importOutcome.RemainingPlanned);

        // Verify mastery node 20 was isolated into its own AllocatePassivesAsync call!
        Assert.Equal(3, player.PassiveAllocationLog.Count);
        Assert.Equal<ushort>([10], player.PassiveAllocationLog[0].Nodes);
        Assert.Null(player.PassiveAllocationLog[0].MasteryEffect);
        Assert.Equal<ushort>([20], player.PassiveAllocationLog[1].Nodes);
        Assert.Equal((ushort)555, player.PassiveAllocationLog[1].MasteryEffect);
        Assert.Equal<ushort>([30], player.PassiveAllocationLog[2].Nodes);
        Assert.Null(player.PassiveAllocationLog[2].MasteryEffect);

        // Simulate LevelUp: +1 unspent passive point -> auto-allocates node 40!
        player.PassiveTree = player.PassiveTree! with { UnspentPoints = 1 };
        PobApplyOutcome levelUpOutcome = await server.OnLevelUpAsync(ConnId, previousLevel: 1, newLevel: 2);
        Assert.True(levelUpOutcome.Success);
        Assert.Equal<ushort>([40], levelUpOutcome.AllocatedNodes);
        Assert.Equal(0, levelUpOutcome.RemainingPlanned);
        Assert.Contains((ushort)40, player.PassiveTree.AllocatedNodes);

        // Plain node:mastery list parser also works cleanly.
        Assert.True(StashQolServerMod.TryParsePobPlan("10, 20:555, 30", autoApplyOnLevelUp: false, out QolPobPlan? plainPlan, out string err), err);
        Assert.Equal<ushort>([10, 20, 30], plainPlan!.Nodes);
        Assert.Equal((ushort)555, plainPlan.MasteryEffects[20]);
    }

    private static string EncodePobPayload(string xml)
    {
        byte[] raw = Encoding.UTF8.GetBytes(xml);
        using var ms = new MemoryStream();
        using (var zlib = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw, 0, raw.Length);
        }

        return Convert.ToBase64String(ms.ToArray()).Replace('+', '-').Replace('/', '_');
    }
}
