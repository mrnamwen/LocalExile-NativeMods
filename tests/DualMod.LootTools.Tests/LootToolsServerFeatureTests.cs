using DualMod.LootTools.Server;
using DualMod.LootTools.Shared;
using LocalExile.Client.ServerBridge;
using Xunit;

namespace DualMod.LootTools.Tests;

public sealed class LootToolsServerFeatureTests
{
    private const string ConnId = "exile-loot-server";

    private sealed class NullPacketSink : IGatewayPacketSink
    {
        public Task SendPacketAsync(string connectionId, ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendChatReplyAsync(string connectionId, string text, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static (LootToolsServerMod Server, InMemoryLootToolsBackend Backend, InMemoryLootToolsBackend.PlayerWorldState Player) CreateHarness(LootToolsSettings? settings = null)
    {
        var bridge = new ClientBridgeHost(new NullPacketSink());
        var backend = new InMemoryLootToolsBackend();
        var player = backend.GetOrCreatePlayer(ConnId);
        player.Session = new LootSessionState(PlayerX: 100, PlayerY: 100);
        var server = new LootToolsServerMod(bridge, backend, settings);
        return (server, backend, player);
    }

    [Fact]
    public async Task LootMagnet_RelocatesEligibleItemsCleanly_EnforcesAllGuardrails_ZeroTeleport_ZeroDupes()
    {
        var (server, _, player) = CreateHarness();
        using var _ = server;

        // Walkability: reject (102, 100) so it chooses another walkable cell near (100, 100).
        player.IsWalkable = (x, y) => !(x == 102 && y == 100);

        player.Ground[0x100001] = new LootGroundItem { EntityId = 0x100001, GridX = 125, GridY = 100, TypeHash = 1, BaseName = "Chaos Orb", StackSize = 1, MaxStack = 20, Shared = true };
        player.Ground[0x100002] = new LootGroundItem { EntityId = 0x100002, GridX = 110, GridY = 100, TypeHash = 2, BaseName = "Quest Glyph", IsQuestItem = true };
        player.Ground[0x100003] = new LootGroundItem { EntityId = 0x100003, GridX = 110, GridY = 100, TypeHash = 3, BaseName = "Start Sword", IsTrackedDrop = true };
        player.Ground[0x100004] = new LootGroundItem { EntityId = 0x100004, GridX = 110, GridY = 100, TypeHash = 4, BaseName = "Other Private", Shared = false, OwnerConnectionId = "other-player" };
        player.Ground[0x100005] = new LootGroundItem { EntityId = 0x100005, GridX = 110, GridY = 100, TypeHash = 5, BaseName = "Own Private", Shared = false, OwnerConnectionId = ConnId };
        player.Ground[0x100006] = new LootGroundItem { EntityId = 0x100006, GridX = 250, GridY = 100, TypeHash = 6, BaseName = "Too Far Away" }; // dist 150 > clamped 80
        player.Ground[0x100007] = new LootGroundItem { EntityId = 0x100007, GridX = 110, GridY = 100, TypeHash = 7, BaseName = "Not Admitted", LockstepAdmitted = false };
        player.Ground[0x100008] = new LootGroundItem { EntityId = 0x100008, GridX = 115, GridY = 100, TypeHash = 8, BaseName = "Raced Away" };

        // Simulate concurrent pickup winning 0x100008 right before Take().
        player.OnBeforeTake = id =>
        {
            if (id == 0x100008)
            {
                player.Ground.Remove(0x100008);
            }
        };

        LootMagnetOutcome outcome = await server.ExecuteLootMagnetAsync(
            ConnId,
            new LootMagnetRequest(
                EntityIds: [0x100001, 0x100002, 0x100003, 0x100004, 0x100005, 0x100006, 0x100007, 0x100008, 0x100001], // duplicate 0x100001
                RadiusCells: 999)); // Server must clamp to <= 80!

        Assert.True(outcome.Success);
        Assert.Equal(2, outcome.RelocatedCount); // Only 0x100001 (shared) and 0x100005 (own private)
        Assert.Equal(0, player.TeleportPacketsSent);

        // Old IDs removed, new IDs created with justDropped == false and walkable position != blocked (102, 100).
        Assert.Equal([0x100001u, 0x100005u], player.SentWorldRemovals);
        Assert.Equal(2, player.SentGroundItemCreates.Count);
        Assert.All(player.SentGroundItemCreates, c =>
        {
            Assert.False(c.JustDropped);
            Assert.NotEqual((102, 100), (c.Item.GridX, c.Item.GridY));
        });

        // Private ownership preserved on relocated 0x100005.
        uint newPrivateId = outcome.RelocatedItems.Single(r => r.OldEntityId == 0x100005).NewEntityId;
        Assert.False(player.Ground[newPrivateId].Shared);
        Assert.Equal(ConnId, player.Ground[newPrivateId].OwnerConnectionId);

        // Guarded items untouched on ground.
        Assert.Contains(0x100002u, player.Ground.Keys);
        Assert.Contains(0x100003u, player.Ground.Keys);
        Assert.Contains(0x100004u, player.Ground.Keys);
        Assert.Contains(0x100006u, player.Ground.Keys);
        Assert.Contains(0x100007u, player.Ground.Keys);
        Assert.DoesNotContain(0x100008u, player.Ground.Keys); // Raced item never duplicated!
    }

    [Fact]
    public async Task LootMagnet_EnforcesBatchCap64_AndRejectsInactiveSessionStates()
    {
        var (server, _, player) = CreateHarness();
        using var _ = server;

        List<uint> ids = [];
        for (uint i = 1; i <= 80; i++)
        {
            uint id = 0x100000u + i;
            player.Ground[id] = new LootGroundItem { EntityId = id, GridX = 110, GridY = 100, TypeHash = 1, BaseName = "Scroll", StackSize = 1, MaxStack = 40 };
            ids.Add(id);
        }

        // Trade open -> rejected!
        player.Session = player.Session with { HasActiveTrade = true };
        LootMagnetOutcome blocked = await server.ExecuteLootMagnetAsync(ConnId, new LootMagnetRequest([.. ids], 60));
        Assert.False(blocked.Success);
        Assert.Equal(0, blocked.RelocatedCount);

        // Dead player -> rejected!
        player.Session = player.Session with { HasActiveTrade = false, PlayerLife = 0 };
        Assert.False((await server.ExecuteLootMagnetAsync(ConnId, new LootMagnetRequest([.. ids], 60))).Success);

        // Active session -> hard-capped to 64!
        player.Session = player.Session with { PlayerLife = 100 };
        LootMagnetOutcome ok = await server.ExecuteLootMagnetAsync(ConnId, new LootMagnetRequest([.. ids], 60));
        Assert.True(ok.Success);
        Assert.Equal(LootToolsProtocol.MaxBatchEntityCount, ok.RelocatedCount);
    }

    [Fact]
    public async Task LootMerger_CollapsesPartialStacksWithMaxStackSplit_RespectsStrictMetadataIdentity_AndGoldCap()
    {
        var (server, _, player) = CreateHarness();
        using var _ = server;

        // Group 1: 3 partial Chaos Orb stacks (8 + 9 + 7 = 24, MaxStack 20 -> 2 piles: 20 and 4).
        player.Ground[0x100001] = new LootGroundItem { EntityId = 0x100001, GridX = 105, GridY = 100, TypeHash = 0x1111, BaseName = "Chaos Orb", StackSize = 8, MaxStack = 20 };
        player.Ground[0x100002] = new LootGroundItem { EntityId = 0x100002, GridX = 108, GridY = 100, TypeHash = 0x1111, BaseName = "Chaos Orb", StackSize = 9, MaxStack = 20 };
        player.Ground[0x100003] = new LootGroundItem { EntityId = 0x100003, GridX = 112, GridY = 100, TypeHash = 0x1111, BaseName = "Chaos Orb", StackSize = 7, MaxStack = 20 };

        // Different corrupted flag -> MUST NOT merge with normal Chaos Orbs!
        player.Ground[0x100004] = new LootGroundItem { EntityId = 0x100004, GridX = 106, GridY = 100, TypeHash = 0x1111, BaseName = "Chaos Orb", StackSize = 5, MaxStack = 20, Corrupted = true };

        // Different quality -> MUST NOT merge!
        player.Ground[0x100005] = new LootGroundItem { EntityId = 0x100005, GridX = 106, GridY = 101, TypeHash = 0x2222, BaseName = "Gemcutter", StackSize = 3, MaxStack = 20, Quality = 5 };
        player.Ground[0x100006] = new LootGroundItem { EntityId = 0x100006, GridX = 107, GridY = 101, TypeHash = 0x2222, BaseName = "Gemcutter", StackSize = 3, MaxStack = 20, Quality = 10 };

        // Group 2: 3 Gold piles (40000 + 30000 + 10000 = 80000, cap 65535 -> 2 piles: 65535 and 14465).
        player.Ground[0x100010] = new LootGroundItem { EntityId = 0x100010, GridX = 103, GridY = 100, TypeHash = 0x9999, BaseName = "Gold", StackSize = 40000, MaxStack = 1, IsGold = true };
        player.Ground[0x100011] = new LootGroundItem { EntityId = 0x100011, GridX = 104, GridY = 100, TypeHash = 0x9999, BaseName = "Gold", StackSize = 30000, MaxStack = 1, IsGold = true };
        player.Ground[0x100012] = new LootGroundItem { EntityId = 0x100012, GridX = 105, GridY = 100, TypeHash = 0x9999, BaseName = "Gold", StackSize = 10000, MaxStack = 1, IsGold = true };

        // Group 3: 2 partial stacks whose total (15 + 10 = 25, MaxStack 20 -> 2 piles) does NOT shrink pile count -> untouched!
        player.Ground[0x100020] = new LootGroundItem { EntityId = 0x100020, GridX = 102, GridY = 102, TypeHash = 0x3333, BaseName = "Alteration", StackSize = 15, MaxStack = 20 };
        player.Ground[0x100021] = new LootGroundItem { EntityId = 0x100021, GridX = 103, GridY = 102, TypeHash = 0x3333, BaseName = "Alteration", StackSize = 10, MaxStack = 20 };

        // Non-stackables & quest items -> untouched!
        player.Ground[0x100030] = new LootGroundItem { EntityId = 0x100030, GridX = 101, GridY = 100, TypeHash = 0x4444, BaseName = "Iron Ring", StackSize = 1, MaxStack = 1 };
        player.Ground[0x100031] = new LootGroundItem { EntityId = 0x100031, GridX = 101, GridY = 101, TypeHash = 0x4444, BaseName = "Iron Ring", StackSize = 1, MaxStack = 1 };

        LootMergeOutcome outcome = await server.ExecuteLootMergeAsync(ConnId, new LootMergeRequest(null, 60));
        Assert.True(outcome.Success);
        Assert.Equal(2, outcome.GroupsMerged);
        Assert.Equal(6, outcome.PilesRemoved);
        Assert.Equal(4, outcome.PilesCreated);
        Assert.All(player.SentGroundItemCreates, c => Assert.False(c.JustDropped));

        // Verify exact Chaos Orb stacks (20 + 4 = 24), corrupted stack untouched (5).
        ushort[] chaosStacks = [.. player.Ground.Values.Where(g => g.TypeHash == 0x1111 && !g.Corrupted).Select(g => g.StackSize).OrderDescending()];
        Assert.Equal([20, 4], chaosStacks);
        Assert.Equal((ushort)5, player.Ground[0x100004].StackSize);

        // Verify exact Gold stacks (65535 + 14465 = 80000).
        ushort[] goldStacks = [.. player.Ground.Values.Where(g => g.IsGold).Select(g => g.StackSize).OrderDescending()];
        Assert.Equal([65535, 14465], goldStacks);

        // Non-shrinking group & mismatched quality & unstackables untouched.
        Assert.Contains(0x100020u, player.Ground.Keys);
        Assert.Contains(0x100021u, player.Ground.Keys);
        Assert.Contains(0x100005u, player.Ground.Keys);
        Assert.Contains(0x100006u, player.Ground.Keys);
        Assert.Contains(0x100030u, player.Ground.Keys);
    }

    [Fact]
    public async Task LootMerger_ConcurrentPickupRace_NeverDuplicatesAndRestoresUnshrinkableSingleClaim()
    {
        var (server, _, player) = CreateHarness();
        using var _ = server;

        // Case A: 3 piles of 5; concurrent player grabs pile 2 before merge claims it -> ONLY 5 + 5 = 10 merged (never 15!).
        player.Ground[0x100001] = new LootGroundItem { EntityId = 0x100001, GridX = 102, GridY = 100, TypeHash = 0x1111, BaseName = "Chaos Orb", StackSize = 5, MaxStack = 20 };
        player.Ground[0x100002] = new LootGroundItem { EntityId = 0x100002, GridX = 103, GridY = 100, TypeHash = 0x1111, BaseName = "Chaos Orb", StackSize = 5, MaxStack = 20 };
        player.Ground[0x100003] = new LootGroundItem { EntityId = 0x100003, GridX = 104, GridY = 100, TypeHash = 0x1111, BaseName = "Chaos Orb", StackSize = 5, MaxStack = 20 };

        // Case B: 2 piles of 4; concurrent player grabs pile 2 -> only 1 pile claimed -> MUST be restored untouched!
        player.Ground[0x100010] = new LootGroundItem { EntityId = 0x100010, GridX = 105, GridY = 100, TypeHash = 0x2222, BaseName = "Vaal Orb", StackSize = 4, MaxStack = 20 };
        player.Ground[0x100011] = new LootGroundItem { EntityId = 0x100011, GridX = 106, GridY = 100, TypeHash = 0x2222, BaseName = "Vaal Orb", StackSize = 4, MaxStack = 20 };

        player.OnBeforeTake = id =>
        {
            if (id == 0x100002) player.Ground.Remove(0x100002);
            if (id == 0x100011) player.Ground.Remove(0x100011);
        };

        LootMergeOutcome outcome = await server.ExecuteLootMergeAsync(ConnId, new LootMergeRequest(null, 60));
        Assert.True(outcome.Success);
        Assert.Equal(1, outcome.GroupsMerged);
        Assert.Equal(2, outcome.PilesRemoved);
        Assert.Equal(1, outcome.PilesCreated);

        // Chaos total on ground is 10 (never duped to 15).
        Assert.Equal(10, player.Ground.Values.Where(g => g.TypeHash == 0x1111).Sum(g => g.StackSize));
        // Vaal Orb pile 0x100010 was cleanly restored with no bogus removal/recreate!
        Assert.True(player.Ground.ContainsKey(0x100010));
        Assert.Equal((ushort)4, player.Ground[0x100010].StackSize);
        Assert.DoesNotContain(0x100010u, player.SentWorldRemovals);
    }

    [Fact]
    public async Task AoePickup_HandlesGold_Stackables_NonStackables_ExpandedInventory_AndSingleInventoryFullWhileContinuingSmallerItems()
    {
        var (server, _, player) = CreateHarness();
        using var _ = server;

        // Fill MainInventory almost completely: leave ONLY a single 1x1 cell at (11, 4) plus an existing partial Chaos stack (15/20) at (0, 0).
        LootGroundItem chaosProto = new() { TypeHash = 0x1111, BaseName = "Chaos Orb", StackSize = 15, MaxStack = 20, Width = 1, Height = 1 };
        player.MainInventory.Add(new InMemoryInventorySlotItem(player.NextInventoryItemId++, "Main", 0, 0, chaosProto, 15));
        for (int x = 0; x < InMemoryLootToolsBackend.MainGridWidth; x++)
        {
            for (int y = 0; y < InMemoryLootToolsBackend.MainGridHeight; y++)
            {
                if ((x == 0 && y == 0) || (x == 11 && y == 4)) continue;
                player.MainInventory.Add(new InMemoryInventorySlotItem(
                    player.NextInventoryItemId++,
                    "Main",
                    (byte)x,
                    (byte)y,
                    new LootGroundItem { TypeHash = 0xEEEE, BaseName = "Filler", Width = 1, Height = 1 },
                    1));
            }
        }

        // Ground items within pickup range:
        player.Ground[0x100001] = new LootGroundItem { EntityId = 0x100001, GridX = 102, GridY = 100, TypeHash = 0x9999, BaseName = "Gold", StackSize = 750, MaxStack = 1, IsGold = true };
        player.Ground[0x100002] = new LootGroundItem { EntityId = 0x100002, GridX = 103, GridY = 100, TypeHash = 0x1111, BaseName = "Chaos Orb", StackSize = 5, MaxStack = 20, Width = 1, Height = 1 }; // Stacks into existing 15/20!
        player.Ground[0x100003] = new LootGroundItem { EntityId = 0x100003, GridX = 104, GridY = 100, TypeHash = 0x5555, BaseName = "Two-Hand Sword", StackSize = 1, MaxStack = 1, Width = 2, Height = 4 }; // Won't fit!
        player.Ground[0x100004] = new LootGroundItem { EntityId = 0x100004, GridX = 105, GridY = 100, TypeHash = 0x5556, BaseName = "Body Armour", StackSize = 1, MaxStack = 1, Width = 2, Height = 3 }; // Won't fit!
        player.Ground[0x100005] = new LootGroundItem { EntityId = 0x100005, GridX = 106, GridY = 100, TypeHash = 0x6666, BaseName = "Diamond Ring", StackSize = 1, MaxStack = 1, Width = 1, Height = 1 }; // Fits in (11,4)!
        player.Ground[0x100006] = new LootGroundItem { EntityId = 0x100006, GridX = 107, GridY = 100, TypeHash = 0x7777, BaseName = "Quest Item", IsQuestItem = true }; // Skipped!

        AoePickupOutcome outcome = await server.ExecuteAoePickupAsync(
            ConnId,
            new AoePickupRequest([0x100001, 0x100002, 0x100003, 0x100004, 0x100005, 0x100006], 50));

        Assert.True(outcome.Success);
        Assert.Equal(750, outcome.GoldPickedTotal);
        Assert.Equal(750, player.GoldStash);
        Assert.Equal(2, outcome.PickedItemCount); // Chaos Orb + Diamond Ring
        Assert.Equal(2, outcome.InventoryFullSkipped); // Sword + Body Armour
        Assert.Equal(1, outcome.IgnoredOrRejectedCount); // Quest item
        // InventoryFull (0x0117) emitted AT MOST ONCE even though 2 items failed to fit!
        Assert.Equal(1, player.InventoryFullNotificationsSent);

        // Verify Chaos Orb stacked to 20 and Diamond Ring landed in (11, 4).
        Assert.Equal((ushort)20, player.MainInventory.Single(i => i.SourceItem.TypeHash == 0x1111).StackSize);
        Assert.Contains(player.MainInventory, i => i.SourceItem.TypeHash == 0x6666 && i.X == 11 && i.Y == 4);

        // Now enable ExpandedInventory (ExpandedInventoryMode == 1) and retry the 2 large items -> both fit in 4x5 Expanded!
        player.Session = player.Session with { ExpandedInventoryEnabled = true };
        AoePickupOutcome expandedOutcome = await server.ExecuteAoePickupAsync(
            ConnId,
            new AoePickupRequest([0x100003, 0x100004], 50));

        Assert.True(expandedOutcome.Success);
        Assert.Equal(2, expandedOutcome.PickedItemCount);
        Assert.Equal(0, expandedOutcome.InventoryFullSkipped);
        Assert.Equal(2, player.ExpandedInventory.Count);
        Assert.Equal(1, player.InventoryFullNotificationsSent); // Still 1!
    }

    [Fact]
    public async Task OnAreaTickAsync_RespectsAutoMergeToggleAndIntervalThrottle()
    {
        var (server, _, player) = CreateHarness(new LootToolsSettings(AutoMergeEnabled: true, AutoMergeIntervalSeconds: 1.0));
        using var _ = server;

        player.Ground[0x100001] = new LootGroundItem { EntityId = 0x100001, GridX = 102, GridY = 100, TypeHash = 0x1111, BaseName = "Chaos Orb", StackSize = 3, MaxStack = 20 };
        player.Ground[0x100002] = new LootGroundItem { EntityId = 0x100002, GridX = 103, GridY = 100, TypeHash = 0x1111, BaseName = "Chaos Orb", StackSize = 4, MaxStack = 20 };
        player.Session = player.Session with { AreaTimeSeconds = 10.0 };

        LootMergeOutcome? first = await server.OnAreaTickAsync(ConnId);
        Assert.NotNull(first);
        Assert.Equal(1, first!.GroupsMerged);

        // Drop two more stacks 0.4s later -> throttled (< 1.0s)!
        player.Ground[0x100010] = new LootGroundItem { EntityId = 0x100010, GridX = 102, GridY = 100, TypeHash = 0x2222, BaseName = "Alch", StackSize = 2, MaxStack = 20 };
        player.Ground[0x100011] = new LootGroundItem { EntityId = 0x100011, GridX = 103, GridY = 100, TypeHash = 0x2222, BaseName = "Alch", StackSize = 2, MaxStack = 20 };
        player.Session = player.Session with { AreaTimeSeconds = 10.4 };
        Assert.Null(await server.OnAreaTickAsync(ConnId));

        // 1.2s later -> runs!
        player.Session = player.Session with { AreaTimeSeconds = 11.2 };
        LootMergeOutcome? second = await server.OnAreaTickAsync(ConnId);
        Assert.NotNull(second);
        Assert.Equal(1, second!.GroupsMerged);
    }
}
