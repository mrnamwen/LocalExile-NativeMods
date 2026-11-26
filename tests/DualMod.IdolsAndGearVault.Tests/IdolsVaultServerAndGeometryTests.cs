using DualMod.IdolsAndGearVault.Server;
using DualMod.IdolsAndGearVault.Shared;
using LocalExile.Client.ServerBridge;
using Xunit;

namespace DualMod.IdolsAndGearVault.Tests;

public sealed class IdolsVaultServerAndGeometryTests
{
    private sealed class NullGatewaySink : IGatewayPacketSink
    {
        public Task SendPacketAsync(string connectionId, ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SendChatReplyAsync(string connectionId, string text, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    [Fact]
    public void IdolBoardGeometry_HasExact32ActiveCellsAnd4BlockedCorners()
    {
        Assert.Equal(6, IdolBoardGeometry.Columns);
        Assert.Equal(6, IdolBoardGeometry.Rows);
        Assert.Equal(32, IdolBoardGeometry.ActiveCellCount);

        int active = 0;
        int blocked = 0;
        for (int r = 0; r < 6; r++)
        {
            for (int c = 0; c < 6; c++)
            {
                if (IdolBoardGeometry.IsCornerBlocked(c, r))
                {
                    blocked++;
                    Assert.False(IdolBoardGeometry.IsActiveCell(c, r));
                }
                else
                {
                    active++;
                    Assert.True(IdolBoardGeometry.IsActiveCell(c, r));
                }
            }
        }

        Assert.Equal(4, blocked);
        Assert.Equal(32, active);
        Assert.True(IdolBoardGeometry.IsCornerBlocked(0, 0));
        Assert.True(IdolBoardGeometry.IsCornerBlocked(5, 0));
        Assert.True(IdolBoardGeometry.IsCornerBlocked(0, 5));
        Assert.True(IdolBoardGeometry.IsCornerBlocked(5, 5));

        // First free 1x1 cell skips blocked (0,0) -> (1,0).
        Assert.Equal((1, 0), IdolBoardGeometry.FindFirstFreeCell(1, 1, []));
        // First free 2x2 cell skips top corners -> (1,0).
        Assert.Equal((1, 0), IdolBoardGeometry.FindFirstFreeCell(2, 2, []));
        // First free 1x3 cell skips (0,0) -> (1,0).
        Assert.Equal((1, 0), IdolBoardGeometry.FindFirstFreeCell(1, 3, []));
    }

    [Fact]
    public void ServerMod_EnforcesBlockedCorners_MultiSizeFootprints_Overlap_AndRemoval()
    {
        var bridge = new ClientBridgeHost(new NullGatewaySink());
        var backend = new InMemoryIdolsVaultBackend();
        using var server = new IdolsVaultServerMod(bridge, backend);

        const string conn = "exile-board-1";

        // 1. All 4 blocked corners are rejected (both 1x1 and multi-cell footprints clipping a corner).
        foreach (var (col, row, w, h) in new[]
        {
            (0, 0, 1, 1),
            (5, 0, 1, 1),
            (0, 5, 1, 1),
            (5, 5, 1, 1),
            (0, 0, 2, 2),
            (3, 0, 3, 1), // clips (5,0)
            (0, 3, 1, 3), // clips (0,5)
            (4, 5, 2, 1), // clips (5,5)
        })
        {
            IdolBoardStateSync rejected = server.PlaceIdol(conn, new PlaceIdolRequest(
                IdolId: $"bad_{col}_{row}_{w}x{h}",
                Name: "Blocked Corner Test",
                MetadataPath: "Metadata/Items/Idols/MinorIdol",
                Col: col,
                Row: row,
                Width: w,
                Height: h));
            Assert.False(rejected.Succeeded);
            Assert.Contains("locked corner", rejected.StatusMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(rejected.Idols);
        }

        // 2. Disallowed footprint sizes (e.g. 2x3, 4x1, 0x1) are rejected.
        Assert.False(server.PlaceIdol(conn, new PlaceIdolRequest("bad_2x3", "Invalid", "Metadata/Items/Idols/X", 1, 1, 2, 3)).Succeeded);
        Assert.False(server.PlaceIdol(conn, new PlaceIdolRequest("bad_4x1", "Invalid", "Metadata/Items/Idols/X", 1, 1, 4, 1)).Succeeded);

        // 3. Place all 6 valid PoE 3.29 Idol footprints (1x1, 1x2, 2x1, 2x2, 1x3, 3x1) cleanly on the 32-cell board!
        Assert.True(server.PlaceIdol(conn, new PlaceIdolRequest("id_1x1", "Minor", "Metadata/Items/Idols/MinorIdol", 1, 0, 1, 1)).Succeeded);
        Assert.True(server.PlaceIdol(conn, new PlaceIdolRequest("id_3x1", "Noble", "Metadata/Items/Idols/NobleIdol", 2, 0, 3, 1)).Succeeded);
        Assert.True(server.PlaceIdol(conn, new PlaceIdolRequest("id_1x3", "Totemic", "Metadata/Items/Idols/TotemicIdol", 0, 1, 1, 3)).Succeeded);
        Assert.True(server.PlaceIdol(conn, new PlaceIdolRequest("id_2x2", "Conqueror", "Metadata/Items/Idols/ConquerorIdol", 1, 1, 2, 2)).Succeeded);
        Assert.True(server.PlaceIdol(conn, new PlaceIdolRequest("id_1x2", "Kamasan", "Metadata/Items/Idols/KamasanIdol", 3, 1, 1, 2)).Succeeded);
        IdolBoardStateSync afterSix = server.PlaceIdol(conn, new PlaceIdolRequest("id_2x1", "Burial", "Metadata/Items/Idols/BurialIdol", 4, 1, 2, 1));
        Assert.True(afterSix.Succeeded);
        Assert.Equal(6, afterSix.Idols.Length);
        Assert.Equal(1 + 3 + 3 + 4 + 2 + 2, afterSix.OccupiedCells); // 15 / 32 active cells

        // 4. Overlapping placement and duplicate IdolId are rejected.
        IdolBoardStateSync overlap = server.PlaceIdol(conn, new PlaceIdolRequest("id_overlap", "Overlap", "Metadata/Items/Idols/MinorIdol", 2, 2, 1, 1));
        Assert.False(overlap.Succeeded);
        Assert.Contains("overlaps", overlap.StatusMessage, StringComparison.OrdinalIgnoreCase);

        IdolBoardStateSync duplicate = server.PlaceIdol(conn, new PlaceIdolRequest("id_1x1", "Dup", "Metadata/Items/Idols/MinorIdol", 4, 2, 1, 1));
        Assert.False(duplicate.Succeeded);
        Assert.Contains("already placed", duplicate.StatusMessage, StringComparison.OrdinalIgnoreCase);

        // 5. Removing an Idol frees its cells and allows a new Idol to occupy them.
        IdolBoardStateSync removed = server.RemoveIdol(conn, new RemoveIdolRequest("id_2x2"));
        Assert.True(removed.Succeeded);
        Assert.Equal(5, removed.Idols.Length);
        Assert.Equal(11, removed.OccupiedCells);

        Assert.True(server.PlaceIdol(conn, new PlaceIdolRequest("id_replacement", "Replacement", "Metadata/Items/Idols/MinorIdol", 2, 2, 1, 1)).Succeeded);
        Assert.False(server.RemoveIdol(conn, new RemoveIdolRequest("does_not_exist")).Succeeded);
    }

    [Fact]
    public void ServerMod_CanPackExact32Of32ActiveCellsAndRejects33rdCell()
    {
        var bridge = new ClientBridgeHost(new NullGatewaySink());
        var backend = new InMemoryIdolsVaultBackend();
        using var server = new IdolsVaultServerMod(bridge, backend);

        const string conn = "exile-full-32";
        // Top row (cols 1..4 = 4 cells): 3x1 + 1x1
        Assert.True(server.PlaceIdol(conn, new PlaceIdolRequest("top_3x1", "Noble", "Metadata/Items/Idols/NobleIdol", 1, 0, 3, 1)).Succeeded);
        Assert.True(server.PlaceIdol(conn, new PlaceIdolRequest("top_1x1", "Minor", "Metadata/Items/Idols/MinorIdol", 4, 0, 1, 1)).Succeeded);

        // Middle 4 rows (rows 1..4, cols 0..5 = 24 cells): six 2x2 Conqueror Idols
        Assert.True(server.PlaceIdol(conn, new PlaceIdolRequest("m_0_1", "Conq1", "Metadata/Items/Idols/ConquerorIdol", 0, 1, 2, 2)).Succeeded);
        Assert.True(server.PlaceIdol(conn, new PlaceIdolRequest("m_2_1", "Conq2", "Metadata/Items/Idols/ConquerorIdol", 2, 1, 2, 2)).Succeeded);
        Assert.True(server.PlaceIdol(conn, new PlaceIdolRequest("m_4_1", "Conq3", "Metadata/Items/Idols/ConquerorIdol", 4, 1, 2, 2)).Succeeded);
        Assert.True(server.PlaceIdol(conn, new PlaceIdolRequest("m_0_3", "Conq4", "Metadata/Items/Idols/ConquerorIdol", 0, 3, 2, 2)).Succeeded);
        Assert.True(server.PlaceIdol(conn, new PlaceIdolRequest("m_2_3", "Conq5", "Metadata/Items/Idols/ConquerorIdol", 2, 3, 2, 2)).Succeeded);
        Assert.True(server.PlaceIdol(conn, new PlaceIdolRequest("m_4_3", "Conq6", "Metadata/Items/Idols/ConquerorIdol", 4, 3, 2, 2)).Succeeded);

        // Bottom row (cols 1..4 = 4 cells): two 2x1 Burial Idols
        Assert.True(server.PlaceIdol(conn, new PlaceIdolRequest("bot_1", "Burial1", "Metadata/Items/Idols/BurialIdol", 1, 5, 2, 1)).Succeeded);
        IdolBoardStateSync fullBoard = server.PlaceIdol(conn, new PlaceIdolRequest("bot_2", "Burial2", "Metadata/Items/Idols/BurialIdol", 3, 5, 2, 1));

        Assert.True(fullBoard.Succeeded);
        Assert.Equal(32, fullBoard.OccupiedCells);
        Assert.Equal(32, fullBoard.ActiveCellsTotal);
        Assert.Equal(10, fullBoard.Idols.Length);
        Assert.Null(IdolBoardGeometry.FindFirstFreeCell(1, 1, fullBoard.Idols));

        // Any 33rd cell placement (even on blocked corners) is rejected cleanly.
        Assert.False(server.PlaceIdol(conn, new PlaceIdolRequest("overflow", "Extra", "Metadata/Items/Idols/MinorIdol", 0, 0, 1, 1)).Succeeded);
    }

    [Fact]
    public void ServerMod_GearVaultConfiguration_FiltersAll8ClassesAndClampsLimits()
    {
        var bridge = new ClientBridgeHost(new NullGatewaySink());
        var backend = new InMemoryIdolsVaultBackend();
        using var server = new IdolsVaultServerMod(bridge, backend);

        const string conn = "exile-vault-1";
        backend.AddVaultItem(conn, new GearVaultItemRecord(101, "Gale Crown", "Royal Burgonet", "Metadata/Items/Armours/Helmets/HelmetStr1", GearVaultClasses.Helmets, 86, 2, 42, ["Defence", "Life"]));
        backend.AddVaultItem(conn, new GearVaultItemRecord(102, "Soul Shell", "Astral Plate", "Metadata/Items/Armours/BodyArmours/BodyStr1", GearVaultClasses.BodyArmours, 85, 2, 42, ["Defence", "Life"]));
        backend.AddVaultItem(conn, new GearVaultItemRecord(103, "Bramble Knot", "Amethyst Ring", "Metadata/Items/Rings/Ring1", GearVaultClasses.Rings, 84, 2, 42, ["Life", "Resistance"]));

        // Configure arbitrary TabIndex 42 (beyond stock 0..21 cap) for Body Armours.
        GearVaultStateSync sync = server.ConfigureGearVault(conn, new ConfigureGearVaultRequest(
            TargetTabIndex: 42,
            GearClassFilter: GearVaultClasses.BodyArmours,
            StockAffinityEnabled: true,
            StockAffinityBitId: 99)); // clamped to 21

        Assert.True(sync.Succeeded);
        Assert.Equal((ushort)42, sync.TargetTabIndex);
        Assert.Equal(GearVaultClasses.BodyArmours, sync.GearClassFilter);
        Assert.Equal((byte)21, sync.StockAffinityBitId);
        GearVaultItemRecord onlyBody = Assert.Single(sync.Items);
        Assert.Equal(102u, onlyBody.ItemId);

        // Invalid gear class is rejected without corrupting state.
        GearVaultStateSync invalid = server.ConfigureGearVault(conn, new ConfigureGearVaultRequest(10, "InvalidClass", true, 4));
        Assert.False(invalid.Succeeded);
        Assert.Equal(GearVaultClasses.BodyArmours, invalid.GearClassFilter);

        // Verify all 8 required gear classes classify cleanly from metadata paths.
        Assert.Equal(GearVaultClasses.Helmets, GearVaultClasses.Classify("Metadata/Items/Armours/Helmets/HelmetDexInt1"));
        Assert.Equal(GearVaultClasses.BodyArmours, GearVaultClasses.Classify("Metadata/Items/Armours/BodyArmours/BodyInt1"));
        Assert.Equal(GearVaultClasses.Gloves, GearVaultClasses.Classify("Metadata/Items/Armours/Gloves/GlovesStr1"));
        Assert.Equal(GearVaultClasses.Boots, GearVaultClasses.Classify("Metadata/Items/Armours/Boots/BootsDex1"));
        Assert.Equal(GearVaultClasses.Belts, GearVaultClasses.Classify("Metadata/Items/Belts/Belt3"));
        Assert.Equal(GearVaultClasses.Rings, GearVaultClasses.Classify("Metadata/Items/Rings/Ring2"));
        Assert.Equal(GearVaultClasses.Amulets, GearVaultClasses.Classify("Metadata/Items/Amulets/Amulet5"));
        Assert.Equal(GearVaultClasses.Weapons, GearVaultClasses.Classify("Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword1"));
    }
}
