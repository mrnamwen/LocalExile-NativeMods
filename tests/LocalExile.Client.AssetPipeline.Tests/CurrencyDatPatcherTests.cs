using System.Buffers.Binary;
using System.Text;
using LocalExile.Client.AssetPipeline.Dat64;
using Xunit;

namespace LocalExile.Client.AssetPipeline.Tests;

public sealed class CurrencyDatPatcherTests
{
    [Fact]
    public void SiphonOrbSpecs_MatchVerified329ServerContractHashesAndMetadata()
    {
        IReadOnlyList<CurrencyItemPatchSpec> specs = CurrencyDatPatcher.CreateSiphonOrbSpecs();
        Assert.Equal(2, specs.Count);

        CurrencyItemPatchSpec empty = specs[0];
        Assert.Equal("Metadata/Items/Currency/Mods/Siphon/SiphonOrbEmpty", empty.BaseItemTypeId);
        Assert.Equal(0x2B06B9D8u, empty.BaseItemHash32);
        Assert.Equal("Orb of Siphoning", empty.DisplayName);
        Assert.Equal("SiphonOrbEmpty", empty.VisualIdentityId);
        Assert.Equal((ushort)0xD501, empty.VisualIdentityHash16);
        Assert.Equal("Art/2DItems/Currency/Mods/Siphon/SiphonOrbEmpty.dds", empty.IconDdsPath);
        Assert.Equal(20, empty.StackSize);
        Assert.Equal(2, empty.CurrencyUseType);
        Assert.Equal("remove_random_mod", empty.Action);
        Assert.False(empty.SupportsStoredMods);

        CurrencyItemPatchSpec charged = specs[1];
        Assert.Equal("Metadata/Items/Currency/Mods/Siphon/SiphonOrbCharged", charged.BaseItemTypeId);
        Assert.Equal(0xBD658699u, charged.BaseItemHash32);
        Assert.Equal("Charged Orb of Siphoning", charged.DisplayName);
        Assert.Equal("SiphonOrbCharged", charged.VisualIdentityId);
        Assert.Equal((ushort)0xD502, charged.VisualIdentityHash16);
        Assert.Equal("Art/2DItems/Currency/Mods/Siphon/SiphonOrbCharged.dds", charged.IconDdsPath);
        Assert.Equal(1, charged.StackSize);
        Assert.Equal(2, charged.CurrencyUseType);
        Assert.Equal("remove_random_mod", charged.Action);
        Assert.True(charged.SupportsStoredMods);
    }

    [Fact]
    public void AppendCurrencies_AppendsSiphonOrbsWithZeroAliasingAndValidItFiles()
    {
        (byte[] stockBit, byte[] stockIvi, byte[] stockCur) = BuildSyntheticStockCurrencyTables();
        IReadOnlyList<CurrencyItemPatchSpec> specs = CurrencyDatPatcher.CreateSiphonOrbSpecs();

        // Give the empty orb an explicit tag matching stock Orb of Annulment ([1353]) so we also verify
        // deep-copy non-aliasing on non-empty foreignrow[] arrays.
        CurrencyItemPatchSpec[] taggedSpecs =
        [
            specs[0] with { TagsKeys = [1353UL] },
            specs[1],
        ];

        PatchedCurrencyResult result = CurrencyDatPatcher.AppendCurrencies(stockBit, stockIvi, stockCur, taggedSpecs);
        Assert.Equal(2, result.Entries.Count);

        Dat64Table bit = Dat64Table.Parse(result.BaseItemTypesBytes);
        Dat64Table ivi = Dat64Table.Parse(result.ItemVisualIdentityBytes);
        Dat64Table cur = Dat64Table.Parse(result.CurrencyItemsBytes);

        Assert.Equal(3, bit.RowCount);
        Assert.Equal(3, ivi.RowCount);
        Assert.Equal(3, cur.RowCount);

        // Stock Orb of Annulment row 0 stays untouched
        Assert.Equal("Metadata/Items/Currency/CurrencyRemoveMod", bit.ReadString(0, CurrencyDatPatcher.BitIdOffset));
        Assert.Equal("CurrencyRemoveMod", ivi.ReadString(0, CurrencyDatPatcher.IviIdOffset));
        Assert.Equal([1353UL], bit.ReadForeignRowArray(0, CurrencyDatPatcher.BitTagsKeysOffset));
        Assert.Equal([99UL], cur.ReadForeignRowArray(0, CurrencyDatPatcher.CurUsageAchOffset));

        // Appended Empty Siphon Orb (row 1)
        PatchedCurrencyEntry emptyEntry = result.Entries[0];
        Assert.Equal(1, emptyEntry.BaseItemTypesRowIndex);
        Assert.Equal(1, emptyEntry.ItemVisualIdentityRowIndex);
        Assert.Equal(1, emptyEntry.CurrencyItemsRowIndex);
        Assert.Equal(0x2B06B9D8u, emptyEntry.BaseItemHash32);
        Assert.Equal((ushort)0xD501, emptyEntry.VisualIdentityHash16);

        // Non-empty TagsKeys on row 1 MUST have a distinct variable-section offset from template row 0!
        ulong stockTagsOff = bit.ReadUInt64(0, CurrencyDatPatcher.BitTagsKeysOffset + 8);
        ulong emptyTagsOff = bit.ReadUInt64(1, CurrencyDatPatcher.BitTagsKeysOffset + 8);
        Assert.NotEqual(stockTagsOff, emptyTagsOff);
        Assert.Equal([1353UL], bit.ReadForeignRowArray(1, CurrencyDatPatcher.BitTagsKeysOffset));

        // Template row's Usage_AchievementItemsKeys must NOT leak into appended currency rows
        Assert.Empty(cur.ReadForeignRowArray(1, CurrencyDatPatcher.CurUsageAchOffset));
        Assert.Empty(cur.ReadForeignRowArray(2, CurrencyDatPatcher.CurUsageAchOffset));

        // Verify .it files (UTF-16LE with BOM, CRLF)
        string emptyIt = DecodeUtf16WithBom(emptyEntry.ItFileBytes);
        Assert.Contains("version 3\r\n", emptyIt, StringComparison.Ordinal);
        Assert.Contains("extends \"Metadata/Items/Currency/StackableCurrency\"", emptyIt, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalStats", emptyIt, StringComparison.Ordinal);

        PatchedCurrencyEntry chargedEntry = result.Entries[1];
        string chargedIt = DecodeUtf16WithBom(chargedEntry.ItFileBytes);
        Assert.Contains("extends \"Metadata/Items/Currency/StackableCurrency\"", chargedIt, StringComparison.Ordinal);
        Assert.Contains("LocalStats", chargedIt, StringComparison.Ordinal);
        Assert.Contains("stat_description_list = \"Metadata/StatDescriptions/stat_descriptions.txt\"", chargedIt, StringComparison.Ordinal);
        Assert.Contains("mod_stat_description_list = \"Metadata/StatDescriptions/advanced_mod_stat_descriptions.txt\"", chargedIt, StringComparison.Ordinal);
        Assert.Contains("enable_rarity = \"normal\"", chargedIt, StringComparison.Ordinal);

        CurrencyDatPatcher.VerifyAppendedCurrencies(bit, ivi, cur, result.Entries);
    }

    [Fact]
    public void AppendCurrencies_SupportsLayeringMultipleModsAndAutoAllocatingHash16()
    {
        (byte[] stockBit, byte[] stockIvi, byte[] stockCur) = BuildSyntheticStockCurrencyTables();

        // Layer 1: Siphon Orbs
        PatchedCurrencyResult layer1 = CurrencyDatPatcher.AppendCurrencies(
            stockBit, stockIvi, stockCur, CurrencyDatPatcher.CreateSiphonOrbSpecs());

        // Layer 2: Another mod appends a shard that converts into SiphonOrbEmpty at full stack, with auto HASH16
        var shardSpec = new CurrencyItemPatchSpec
        {
            BaseItemTypeId = "Metadata/Items/Currency/Mods/OtherMod/SiphonShard",
            DisplayName = "Siphoning Shard",
            VisualIdentityId = "SiphonShardVisual",
            VisualIdentityHash16 = null, // auto-allocate collision-free u16
            IconDdsPath = "Art/2DItems/Currency/Mods/OtherMod/SiphonShard.dds",
            StackSize = 20,
            CurrencyUseType = 3,
            Action = string.Empty,
            Directions = "A stack of 20 shards becomes an Orb of Siphoning.",
            Description = string.Empty,
            FullStackBaseItemTypeId = "Metadata/Items/Currency/Mods/Siphon/SiphonOrbEmpty",
            TagsKeys = [231UL],
        };

        PatchedCurrencyResult layer2 = CurrencyDatPatcher.AppendCurrency(
            layer1.BaseItemTypesBytes,
            layer1.ItemVisualIdentityBytes,
            layer1.CurrencyItemsBytes,
            shardSpec);

        Dat64Table bit = Dat64Table.Parse(layer2.BaseItemTypesBytes);
        Dat64Table ivi = Dat64Table.Parse(layer2.ItemVisualIdentityBytes);
        Dat64Table cur = Dat64Table.Parse(layer2.CurrencyItemsBytes);

        Assert.Equal(4, bit.RowCount);
        Assert.Equal(4, ivi.RowCount);
        Assert.Equal(4, cur.RowCount);

        PatchedCurrencyEntry shardEntry = layer2.Entries[0];
        Assert.Equal(3, shardEntry.BaseItemTypesRowIndex);
        Assert.NotEqual((ushort)0, shardEntry.VisualIdentityHash16);
        Assert.NotEqual((ushort)0xD501, shardEntry.VisualIdentityHash16);
        Assert.NotEqual((ushort)0xD502, shardEntry.VisualIdentityHash16);
        Assert.Equal((ulong)layer1.Entries[0].BaseItemTypesRowIndex, cur.ReadForeignRow(3, CurrencyDatPatcher.CurFullStackBaseItemOffset));

        // Verify both layer 1 AND layer 2 entries still pass full verification on the layered tables
        CurrencyDatPatcher.VerifyAppendedCurrencies(bit, ivi, cur, [.. layer1.Entries, .. layer2.Entries]);
    }

    [Fact]
    public void AppendCurrencies_RejectsDuplicatesCollisionsAndForeignRowAliasing()
    {
        (byte[] stockBit, byte[] stockIvi, byte[] stockCur) = BuildSyntheticStockCurrencyTables();
        IReadOnlyList<CurrencyItemPatchSpec> siphon = CurrencyDatPatcher.CreateSiphonOrbSpecs();
        CurrencyItemPatchSpec taggedEmpty = siphon[0] with { TagsKeys = [1353UL] };
        PatchedCurrencyResult patched = CurrencyDatPatcher.AppendCurrencies(stockBit, stockIvi, stockCur, [taggedEmpty, siphon[1]]);

        // Re-appending same spec rejects duplicate BaseItemTypes.Id
        Assert.Throws<InvalidOperationException>(() =>
            CurrencyDatPatcher.AppendCurrency(patched.BaseItemTypesBytes, patched.ItemVisualIdentityBytes, patched.CurrencyItemsBytes, siphon[0]));

        // Distinct BaseItemTypes.Id but colliding HASH16 (0xD501) is rejected
        var dupHash16 = siphon[0] with
        {
            BaseItemTypeId = "Metadata/Items/Currency/Mods/Siphon/SiphonOrbThird",
            VisualIdentityId = "SiphonOrbThird",
            VisualIdentityHash16 = 0xD501,
        };
        Assert.Throws<InvalidOperationException>(() =>
            CurrencyDatPatcher.AppendCurrency(patched.BaseItemTypesBytes, patched.ItemVisualIdentityBytes, patched.CurrencyItemsBytes, dupHash16));

        // Zero HASH16 is rejected
        var zeroHash16 = dupHash16 with { VisualIdentityHash16 = 0 };
        Assert.Throws<ArgumentException>(() =>
            CurrencyDatPatcher.AppendCurrency(patched.BaseItemTypesBytes, patched.ItemVisualIdentityBytes, patched.CurrencyItemsBytes, zeroHash16));

        // Deliberately alias BaseItemTypes[1].TagsKeys offset back to stock row 0 -> VerifyAppendedCurrencies MUST fail!
        Dat64Table bit = Dat64Table.Parse(patched.BaseItemTypesBytes);
        Dat64Table ivi = Dat64Table.Parse(patched.ItemVisualIdentityBytes);
        Dat64Table cur = Dat64Table.Parse(patched.CurrencyItemsBytes);

        ulong stockTagsOff = bit.ReadUInt64(0, CurrencyDatPatcher.BitTagsKeysOffset + 8);
        byte[] aliasedBitBytes = (byte[])patched.BaseItemTypesBytes.Clone();
        int row1TagsOffPos = 4 + (1 * CurrencyDatPatcher.BaseItemTypesRowStride) + CurrencyDatPatcher.BitTagsKeysOffset + 8;
        BinaryPrimitives.WriteUInt64LittleEndian(aliasedBitBytes.AsSpan(row1TagsOffPos, 8), stockTagsOff);

        Assert.Throws<InvalidDataException>(() =>
            CurrencyDatPatcher.VerifyAppendedCurrencies(Dat64Table.Parse(aliasedBitBytes), ivi, cur, patched.Entries));

        // Deliberately alias CurrencyItems[1].Usage_AchievementItemsKeys back to stock row 0 -> MUST fail!
        ulong stockUsageCnt = cur.ReadUInt64(0, CurrencyDatPatcher.CurUsageAchOffset);
        ulong stockUsageOff = cur.ReadUInt64(0, CurrencyDatPatcher.CurUsageAchOffset + 8);
        byte[] aliasedCurBytes = (byte[])patched.CurrencyItemsBytes.Clone();
        int row1UsagePos = 4 + (1 * CurrencyDatPatcher.CurrencyItemsRowStride) + CurrencyDatPatcher.CurUsageAchOffset;
        BinaryPrimitives.WriteUInt64LittleEndian(aliasedCurBytes.AsSpan(row1UsagePos, 8), stockUsageCnt);
        BinaryPrimitives.WriteUInt64LittleEndian(aliasedCurBytes.AsSpan(row1UsagePos + 8, 8), stockUsageOff);

        Assert.Throws<InvalidDataException>(() =>
            CurrencyDatPatcher.VerifyAppendedCurrencies(bit, ivi, Dat64Table.Parse(aliasedCurBytes), patched.Entries));
    }

    private static (byte[] Bit, byte[] Ivi, byte[] Cur) BuildSyntheticStockCurrencyTables()
    {
        // Stock ItemVisualIdentity row 0 (Orb of Annulment)
        using var iviVar = new MemoryStream();
        WriteSentinel(iviVar);
        ulong iviIdOff = AppendStr(iviVar, "CurrencyRemoveMod");
        ulong iviDdsOff = AppendStr(iviVar, "Art/2DItems/Currency/AnnullOrb.dds");
        ulong iviAoOff = AppendStr(iviVar, "Metadata/Items/Currency/CurrencyOrb.ao");
        byte[] iviRow = new byte[CurrencyDatPatcher.ItemVisualIdentityRowStride];
        BinaryPrimitives.WriteUInt64LittleEndian(iviRow.AsSpan(CurrencyDatPatcher.IviIdOffset, 8), iviIdOff);
        BinaryPrimitives.WriteUInt64LittleEndian(iviRow.AsSpan(CurrencyDatPatcher.IviDdsFileOffset, 8), iviDdsOff);
        BinaryPrimitives.WriteUInt64LittleEndian(iviRow.AsSpan(CurrencyDatPatcher.IviAoFileOffset, 8), iviAoOff);
        Dat64TablePatcher.WriteForeignRow(iviRow, CurrencyDatPatcher.IviInventorySoundOffset, 33UL);
        Dat64TablePatcher.WriteUInt16(iviRow, CurrencyDatPatcher.IviHash16Offset, 32037);
        byte[] stockIvi = new Dat64Table(1, CurrencyDatPatcher.ItemVisualIdentityRowStride, iviRow, iviVar.ToArray()).Serialize();

        // Stock BaseItemTypes row 0 (Orb of Annulment, with non-empty TagsKeys=[1353])
        using var bitVar = new MemoryStream();
        WriteSentinel(bitVar);
        ulong bitIdOff = AppendStr(bitVar, "Metadata/Items/Currency/CurrencyRemoveMod");
        ulong bitNameOff = AppendStr(bitVar, "Orb of Annulment");
        ulong bitInhOff = AppendStr(bitVar, "Metadata/Items/Currency/StackableCurrency");
        ulong bitTagsOff = AppendForeignRowArray(bitVar, [1353UL]);
        byte[] bitRow = new byte[CurrencyDatPatcher.BaseItemTypesRowStride];
        BinaryPrimitives.WriteUInt64LittleEndian(bitRow.AsSpan(CurrencyDatPatcher.BitIdOffset, 8), bitIdOff);
        Dat64TablePatcher.WriteForeignRow(bitRow, CurrencyDatPatcher.BitItemClassOffset, 30UL);
        Dat64TablePatcher.WriteInt32(bitRow, CurrencyDatPatcher.BitWidthOffset, 1);
        Dat64TablePatcher.WriteInt32(bitRow, CurrencyDatPatcher.BitHeightOffset, 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bitRow.AsSpan(CurrencyDatPatcher.BitNameOffset, 8), bitNameOff);
        BinaryPrimitives.WriteUInt64LittleEndian(bitRow.AsSpan(CurrencyDatPatcher.BitInheritsFromOffset, 8), bitInhOff);
        Dat64TablePatcher.WriteInt32(bitRow, CurrencyDatPatcher.BitDropLevelOffset, 35);
        Dat64TablePatcher.WriteInt32(bitRow, CurrencyDatPatcher.BitSizeOnGroundOffset, 1);
        Dat64TablePatcher.WriteArrayHeader(bitRow, CurrencyDatPatcher.BitTagsKeysOffset, 1UL, bitTagsOff);
        Dat64TablePatcher.WriteInt32(bitRow, CurrencyDatPatcher.BitModDomainOffset, 43);
        Dat64TablePatcher.WriteForeignRow(bitRow, CurrencyDatPatcher.BitItemVisualIdentityOffset, 0UL);
        Dat64TablePatcher.WriteUInt32(bitRow, CurrencyDatPatcher.BitHash32Offset, PoeHashing.Hash32("Metadata/Items/Currency/CurrencyRemoveMod"));
        byte[] stockBit = new Dat64Table(1, CurrencyDatPatcher.BaseItemTypesRowStride, bitRow, bitVar.ToArray()).Serialize();

        // Stock CurrencyItems row 0 (Orb of Annulment, with non-empty Usage_AchievementItemsKeys=[99])
        using var curVar = new MemoryStream();
        WriteSentinel(curVar);
        ulong curActOff = AppendStr(curVar, "remove_random_mod");
        ulong curDirOff = AppendStr(curVar, "Right click this item then left click on a magic or rare item to apply it.");
        ulong curDescOff = AppendStr(curVar, "Removes a random modifier from an item");
        ulong curUsageOff = AppendForeignRowArray(curVar, [99UL]);
        byte[] curRow = new byte[CurrencyDatPatcher.CurrencyItemsRowStride];
        Dat64TablePatcher.WriteForeignRow(curRow, CurrencyDatPatcher.CurBaseItemTypesKeyOffset, 0UL);
        Dat64TablePatcher.WriteInt32(curRow, CurrencyDatPatcher.CurStackSizeOffset, 20);
        Dat64TablePatcher.WriteInt32(curRow, CurrencyDatPatcher.CurCurrencyUseTypeOffset, 2);
        BinaryPrimitives.WriteUInt64LittleEndian(curRow.AsSpan(CurrencyDatPatcher.CurActionOffset, 8), curActOff);
        BinaryPrimitives.WriteUInt64LittleEndian(curRow.AsSpan(CurrencyDatPatcher.CurDirectionsOffset, 8), curDirOff);
        Dat64TablePatcher.WriteForeignRow(curRow, CurrencyDatPatcher.CurFullStackBaseItemOffset, null);
        BinaryPrimitives.WriteUInt64LittleEndian(curRow.AsSpan(CurrencyDatPatcher.CurDescriptionOffset, 8), curDescOff);
        Dat64TablePatcher.WriteArrayHeader(curRow, CurrencyDatPatcher.CurUsageAchOffset, 1UL, curUsageOff);
        Dat64TablePatcher.WriteInt32(curRow, CurrencyDatPatcher.CurCurrencyTabStackSizeOffset, 10000);
        byte[] stockCur = new Dat64Table(1, CurrencyDatPatcher.CurrencyItemsRowStride, curRow, curVar.ToArray()).Serialize();

        return (stockBit, stockIvi, stockCur);
    }

    private static string DecodeUtf16WithBom(byte[] bytes)
    {
        Assert.True(bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE, "Expected UTF-16LE BOM.");
        return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
    }

    private static void WriteSentinel(MemoryStream ms)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buf, Dat64Table.Sentinel);
        ms.Write(buf);
    }

    private static ulong AppendStr(MemoryStream ms, string s)
    {
        ulong off = (ulong)ms.Length;
        ms.Write(Encoding.Unicode.GetBytes(s));
        ms.Write([0, 0, 0, 0]);
        return off;
    }

    private static ulong AppendForeignRowArray(MemoryStream ms, ulong[] values)
    {
        ulong off = (ulong)ms.Length;
        Span<byte> buf = stackalloc byte[16];
        foreach (ulong v in values)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buf[..8], v);
            BinaryPrimitives.WriteUInt64LittleEndian(buf[8..16], 0UL);
            ms.Write(buf);
        }

        return off;
    }
}
