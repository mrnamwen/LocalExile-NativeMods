using System.Buffers.Binary;
using System.Text;
using LocalExile.Client.AssetPipeline.Dat64;
using Xunit;

namespace LocalExile.Client.AssetPipeline.Tests;

public sealed class Dat64TablePatcherTests
{
    [Fact]
    public void PoeHashing_MatchesVerified329StockHashes()
    {
        // Verified empirically against stock 3.29.3d MtxTypes.datc64 & Maxwell contract (#57/#58)
        Assert.Equal(0x26BCA75Eu, PoeHashing.Hash32("Metadata/Items/Pets/Kiwi1"));
        Assert.Equal(0x4665A540u, PoeHashing.Hash32("Metadata/Items/Pets/AfterlifeCatPet"));
        Assert.Equal(0xA1EB592Eu, PoeHashing.Hash32("Metadata/Items/Pets/Mods/MaxwellPet"));
        Assert.Equal(0x1F3AB11Au, PoeHashing.Hash32("Metadata/Pet/Mods/Maxwell/MaxwellPet"));
        Assert.Equal((ushort)0xAE20, PoeHashing.FoldHash16(0x1F3AB11Au));
    }

    [Fact]
    public void Dat64TablePatcher_AppendsRowPreservingExistingOffsetsAndDoubleNulStrings()
    {
        byte[] stock = BuildSyntheticTable(
            rowStride: 28,
            rows:
            [
                ("Row/Zero", 111u, 10),
                ("Row/One", 222u, 20),
            ]);

        Dat64Table parsed = Dat64Table.Parse(stock);
        Assert.Equal(2, parsed.RowCount);
        Assert.Equal(28, parsed.RowStride);
        Assert.Equal("Row/Zero", parsed.ReadString(0, 0));
        Assert.Equal("Row/One", parsed.ReadString(1, 0));
        Assert.Equal(222u, parsed.ReadUInt32(1, 8));

        var patcher = new Dat64TablePatcher(parsed);
        byte[] appended = patcher.CloneRow(0);
        patcher.SetString(appended, 0, "Row/Two/Maxwell");
        Dat64TablePatcher.WriteUInt32(appended, 8, 333u);
        patcher.SetInt32Array(appended, 12, [18, 19]);
        int newIdx = patcher.AppendRow(appended);

        Assert.Equal(2, newIdx);
        Dat64Table patched = patcher.BuildAndVerify();
        Assert.Equal(3, patched.RowCount);
        Assert.Equal(28, patched.RowStride);

        // Existing rows remain byte-identical
        Assert.True(patched.GetRowSpan(0).SequenceEqual(parsed.GetRowSpan(0)));
        Assert.True(patched.GetRowSpan(1).SequenceEqual(parsed.GetRowSpan(1)));
        Assert.Equal("Row/Zero", patched.ReadString(0, 0));
        Assert.Equal("Row/One", patched.ReadString(1, 0));

        // Appended row has valid string (terminated with 4 zero bytes) and array
        Assert.Equal("Row/Two/Maxwell", patched.ReadString(2, 0));
        Assert.Equal(333u, patched.ReadUInt32(2, 8));
        Assert.Equal([18, 19], patched.ReadInt32Array(2, 12));

        ulong strOff = patched.ReadUInt64(2, 0);
        int termPos = (int)strOff + ("Row/Two/Maxwell".Length * 2);
        ReadOnlySpan<byte> varSpan = patched.VariableSection.Span;
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, varSpan.Slice(termPos, 4).ToArray());
    }

    [Fact]
    public void MaxwellDatPatcher_AppendsNewPetMtxAcrossThreeTablesWithoutTouchingStockRows()
    {
        byte[] stockMtx = BuildSyntheticMtxTypes();
        byte[] stockGs = BuildSyntheticMtxGs();
        byte[] stockPet = BuildSyntheticPet();

        var spec = new PetMtxPatchSpec();
        PatchedPetMtxResult res = MaxwellDatPatcher.AppendPetMtx(stockMtx, stockGs, stockPet, spec);

        Assert.Equal(1, res.MtxTypesRowIndex);
        Assert.Equal(1, res.MtxTypeGameSpecificRowIndex);
        Assert.Equal(1, res.PetRowIndex);
        Assert.Equal(0xA1EB592Eu, res.MtxHash32);
        Assert.Equal(0x1F3AB11Au, res.PetHash32);
        Assert.Equal((ushort)0xAE20, res.PetHash16);

        Dat64Table mtx = Dat64Table.Parse(res.MtxTypesBytes);
        Dat64Table gs = Dat64Table.Parse(res.MtxTypeGameSpecificBytes);
        Dat64Table pet = Dat64Table.Parse(res.PetBytes);

        // Stock Kiwi rows remain intact
        Assert.Equal("Metadata/Items/Pets/Kiwi1", mtx.ReadString(0, MaxwellDatPatcher.MtxTypesIdOffset));
        Assert.Equal("Metadata/Pet/Kiwi/Basic", pet.ReadString(0, MaxwellDatPatcher.PetIdOffset));

        // Appended Maxwell rows cross-reference cleanly
        MaxwellDatPatcher.VerifyAppendedPetMtx(mtx, gs, pet, spec, 1, 1, 1);

        // Appending a second time refuses duplicate IDs
        Assert.Throws<InvalidOperationException>(() =>
            MaxwellDatPatcher.AppendPetMtx(res.MtxTypesBytes, res.MtxTypeGameSpecificBytes, res.PetBytes, spec));
    }

    private static byte[] BuildSyntheticTable(int rowStride, (string Id, uint Hash, int Val)[] rows)
    {
        using var varMs = new MemoryStream();
        Span<byte> sentinel = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(sentinel, Dat64Table.Sentinel);
        varMs.Write(sentinel);

        byte[] fixedRows = new byte[rows.Length * rowStride];
        for (int i = 0; i < rows.Length; i++)
        {
            ulong strOff = (ulong)varMs.Length;
            varMs.Write(Encoding.Unicode.GetBytes(rows[i].Id));
            varMs.Write([0, 0, 0, 0]);

            Span<byte> row = fixedRows.AsSpan(i * rowStride, rowStride);
            BinaryPrimitives.WriteUInt64LittleEndian(row[0..8], strOff);
            BinaryPrimitives.WriteUInt32LittleEndian(row[8..12], rows[i].Hash);
            BinaryPrimitives.WriteInt32LittleEndian(row[12..16], rows[i].Val);
        }

        return new Dat64Table(rows.Length, rowStride, fixedRows, varMs.ToArray()).Serialize();
    }

    private static byte[] BuildSyntheticMtxTypes()
    {
        using var varMs = new MemoryStream();
        Span<byte> sentinel = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(sentinel, Dat64Table.Sentinel);
        varMs.Write(sentinel);

        ulong idOff = AppendStr(varMs, "Metadata/Items/Pets/Kiwi1");
        ulong nameOff = AppendStr(varMs, "Kiwi Pet");
        ulong descOff = AppendStr(varMs, "Summons your pet");

        byte[] row = new byte[MaxwellDatPatcher.MtxTypesRowStride];
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(MaxwellDatPatcher.MtxTypesIdOffset, 8), idOff);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(MaxwellDatPatcher.MtxTypesHash32Offset, 4), PoeHashing.Hash32("Metadata/Items/Pets/Kiwi1"));
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(MaxwellDatPatcher.MtxTypesNameOffset, 8), nameOff);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(MaxwellDatPatcher.MtxTypesDesc1Offset, 8), descOff);

        return new Dat64Table(1, MaxwellDatPatcher.MtxTypesRowStride, row, varMs.ToArray()).Serialize();
    }

    private static byte[] BuildSyntheticMtxGs()
    {
        using var varMs = new MemoryStream();
        Span<byte> sentinel = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(sentinel, Dat64Table.Sentinel);
        varMs.Write(sentinel);

        ulong ddsOff = AppendStr(varMs, "Art/2DItems/Pets/Kiwi1.dds");
        ulong col4Off = AppendInts(varMs, [15]);
        ulong col6Off = AppendInts(varMs, [18, 19]);

        byte[] row = new byte[MaxwellDatPatcher.MtxTypeGameSpecificRowStride];
        Dat64TablePatcher.WriteForeignRow(row, MaxwellDatPatcher.MtxGsTypeOffset, 0);
        Dat64TablePatcher.WriteUInt32(row, MaxwellDatPatcher.MtxGsHash32Offset, PoeHashing.Hash32("Metadata/Items/Pets/Kiwi1"));
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(MaxwellDatPatcher.MtxGsDdsFileOffset, 8), ddsOff);
        Dat64TablePatcher.WriteInt32(row, MaxwellDatPatcher.MtxGsCategoryOffset, 14);
        Dat64TablePatcher.WriteArrayHeader(row, MaxwellDatPatcher.MtxGsCol4Offset, 1, col4Off);
        Dat64TablePatcher.WriteInt32(row, MaxwellDatPatcher.MtxGsCol5Offset, 0);
        Dat64TablePatcher.WriteArrayHeader(row, MaxwellDatPatcher.MtxGsCol6Offset, 2, col6Off);

        return new Dat64Table(1, MaxwellDatPatcher.MtxTypeGameSpecificRowStride, row, varMs.ToArray()).Serialize();
    }

    private static byte[] BuildSyntheticPet()
    {
        using var varMs = new MemoryStream();
        Span<byte> sentinel = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(sentinel, Dat64Table.Sentinel);
        varMs.Write(sentinel);

        ulong idOff = AppendStr(varMs, "Metadata/Pet/Kiwi/Basic");
        byte[] row = new byte[MaxwellDatPatcher.PetRowStride];
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(MaxwellDatPatcher.PetIdOffset, 8), idOff);
        Dat64TablePatcher.WriteForeignRow(row, MaxwellDatPatcher.PetMtxGsKeyOffset, 0);
        Dat64TablePatcher.WriteUInt32(row, MaxwellDatPatcher.PetHash32Offset, 0x7C5A9464u);
        Dat64TablePatcher.WriteUInt16(row, MaxwellDatPatcher.PetHash16Offset, (ushort)0xE83E);

        return new Dat64Table(1, MaxwellDatPatcher.PetRowStride, row, varMs.ToArray()).Serialize();
    }

    private static ulong AppendStr(MemoryStream ms, string s)
    {
        ulong off = (ulong)ms.Length;
        ms.Write(Encoding.Unicode.GetBytes(s));
        ms.Write([0, 0, 0, 0]);
        return off;
    }

    private static ulong AppendInts(MemoryStream ms, int[] values)
    {
        ulong off = (ulong)ms.Length;
        Span<byte> buf = stackalloc byte[4];
        foreach (int v in values)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buf, v);
            ms.Write(buf);
        }

        return off;
    }
}
