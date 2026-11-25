namespace LocalExile.Client.AssetPipeline.Dat64;

/// <summary>
/// Spec for appending a brand-new Pet MTX entry across the three 3.29.3d tables:
/// <c>Data/MtxTypes.datc64</c>, <c>Data/MtxTypeGameSpecific.datc64</c>, and <c>Data/Pet.datc64</c>.
/// </summary>
public sealed record PetMtxPatchSpec
{
    public string MtxTypeId { get; init; } = "Metadata/Items/Pets/Mods/MaxwellPet";
    public string DisplayName { get; init; } = "Maxwell the Cat Pet";
    public string Description { get; init; } = "Summons your spinning Maxwell the Cat pet";
    public string PetId { get; init; } = "Metadata/Pet/Mods/Maxwell/MaxwellPet";
    public string IconDdsPath { get; init; } = "Art/2DItems/Pets/Mods/Maxwell/MaxwellPet.dds";
    public int MtxCategory { get; init; } = 14;
    public int[] MtxGameSpecificCol4 { get; init; } = [15];
    public int MtxGameSpecificCol5 { get; init; }
    public int[] PetSlots { get; init; } = [18, 19];
    public string TemplateMtxTypeId { get; init; } = "Metadata/Items/Pets/Kiwi1";
    public string TemplatePetId { get; init; } = "Metadata/Pet/Kiwi/Basic";

    public uint MtxHash32 => PoeHashing.Hash32(MtxTypeId);
    public uint PetHash32 => PoeHashing.Hash32(PetId);
    public ushort PetHash16 => PoeHashing.FoldHash16(PetHash32);
}

public sealed record PatchedPetMtxResult(
    byte[] MtxTypesBytes,
    byte[] MtxTypeGameSpecificBytes,
    byte[] PetBytes,
    int MtxTypesRowIndex,
    int MtxTypeGameSpecificRowIndex,
    int PetRowIndex,
    uint MtxHash32,
    uint PetHash32,
    ushort PetHash16);

/// <summary>
/// Appends a brand-new Pet MTX row cleanly across <c>MtxTypes.datc64</c>, <c>MtxTypeGameSpecific.datc64</c>,
/// and <c>Pet.datc64</c> (verified column offsets against 3.29.3d <c>schema.min.json</c> and stock tables).
/// Never modifies or replaces any stock pet row.
/// </summary>
public static class MaxwellDatPatcher
{
    // Verified 3.29.3d exact row strides and column offsets:
    public const int MtxTypesRowStride = 180;
    public const int MtxTypesIdOffset = 0;            // string (8)
    public const int MtxTypesHash32Offset = 8;        // u32 (4)
    public const int MtxTypesNameOffset = 12;         // string (8)
    public const int MtxTypesDesc1Offset = 28;        // string (8)

    public const int MtxTypeGameSpecificRowStride = 68;
    public const int MtxGsTypeOffset = 0;             // foreignrow -> MtxTypes (16)
    public const int MtxGsHash32Offset = 16;          // u32 (4)
    public const int MtxGsDdsFileOffset = 20;         // string (8)
    public const int MtxGsCategoryOffset = 28;        // i32 (4)
    public const int MtxGsCol4Offset = 32;            // i32[] (16)
    public const int MtxGsCol5Offset = 48;            // i32 (4)
    public const int MtxGsCol6Offset = 52;            // i32[] (16)

    public const int PetRowStride = 138;
    public const int PetIdOffset = 0;                 // string (8)
    public const int PetMtxGsKeyOffset = 8;           // foreignrow -> MtxTypeGameSpecific (16)
    public const int PetHash32Offset = 24;            // u32 (4)
    public const int PetHash16Offset = 28;            // u16 (2)

    public static PatchedPetMtxResult AppendPetMtx(
        ReadOnlySpan<byte> stockMtxTypes,
        ReadOnlySpan<byte> stockMtxTypeGameSpecific,
        ReadOnlySpan<byte> stockPet,
        PetMtxPatchSpec? spec = null)
    {
        spec ??= new PetMtxPatchSpec();

        Dat64Table mtxTypes = Dat64Table.Parse(stockMtxTypes);
        Dat64Table mtxGs = Dat64Table.Parse(stockMtxTypeGameSpecific);
        Dat64Table pet = Dat64Table.Parse(stockPet);

        ValidateStride("MtxTypes.datc64", mtxTypes, MtxTypesRowStride);
        ValidateStride("MtxTypeGameSpecific.datc64", mtxGs, MtxTypeGameSpecificRowStride);
        ValidateStride("Pet.datc64", pet, PetRowStride);

        if (mtxTypes.FindRowByString(MtxTypesIdOffset, spec.MtxTypeId) >= 0)
        {
            throw new InvalidOperationException($"MtxTypes.datc64 already contains Id '{spec.MtxTypeId}'.");
        }

        if (pet.FindRowByString(PetIdOffset, spec.PetId) >= 0)
        {
            throw new InvalidOperationException($"Pet.datc64 already contains Id '{spec.PetId}'.");
        }

        // 1. Append MtxTypes row (cloning scalar flags/ShopTag from stock Kiwi1 template row).
        int templateMtxRow = mtxTypes.FindRowByString(MtxTypesIdOffset, spec.TemplateMtxTypeId);
        if (templateMtxRow < 0)
        {
            templateMtxRow = 0;
        }

        var mtxPatcher = new Dat64TablePatcher(mtxTypes);
        byte[] newMtxRow = mtxPatcher.CloneRow(templateMtxRow);
        mtxPatcher.SetString(newMtxRow, MtxTypesIdOffset, spec.MtxTypeId);
        Dat64TablePatcher.WriteUInt32(newMtxRow, MtxTypesHash32Offset, spec.MtxHash32);
        mtxPatcher.SetString(newMtxRow, MtxTypesNameOffset, spec.DisplayName);
        mtxPatcher.SetString(newMtxRow, MtxTypesDesc1Offset, spec.Description);
        int newMtxRowIndex = mtxPatcher.AppendRow(newMtxRow);
        Dat64Table patchedMtxTypes = mtxPatcher.BuildAndVerify();

        // 2. Append MtxTypeGameSpecific row referencing newMtxRowIndex.
        int templateGsRow = FindMtxGsByTypeRow(mtxGs, (ulong)templateMtxRow);
        if (templateGsRow < 0)
        {
            templateGsRow = 0;
        }

        var gsPatcher = new Dat64TablePatcher(mtxGs);
        byte[] newGsRow = gsPatcher.CloneRow(templateGsRow);
        Dat64TablePatcher.WriteForeignRow(newGsRow, MtxGsTypeOffset, (ulong)newMtxRowIndex);
        Dat64TablePatcher.WriteUInt32(newGsRow, MtxGsHash32Offset, spec.MtxHash32);
        gsPatcher.SetString(newGsRow, MtxGsDdsFileOffset, spec.IconDdsPath);
        Dat64TablePatcher.WriteInt32(newGsRow, MtxGsCategoryOffset, spec.MtxCategory);
        gsPatcher.SetInt32Array(newGsRow, MtxGsCol4Offset, spec.MtxGameSpecificCol4);
        Dat64TablePatcher.WriteInt32(newGsRow, MtxGsCol5Offset, spec.MtxGameSpecificCol5);
        gsPatcher.SetInt32Array(newGsRow, MtxGsCol6Offset, spec.PetSlots);
        int newGsRowIndex = gsPatcher.AppendRow(newGsRow);
        Dat64Table patchedMtxGs = gsPatcher.BuildAndVerify();

        // 3. Append Pet row referencing newGsRowIndex (cloning scalar/bool columns from Kiwi row 0).
        int templatePetRow = pet.FindRowByString(PetIdOffset, spec.TemplatePetId);
        if (templatePetRow < 0)
        {
            templatePetRow = 0;
        }

        var petPatcher = new Dat64TablePatcher(pet);
        byte[] newPetRow = petPatcher.CloneRow(templatePetRow);
        petPatcher.SetString(newPetRow, PetIdOffset, spec.PetId);
        Dat64TablePatcher.WriteForeignRow(newPetRow, PetMtxGsKeyOffset, (ulong)newGsRowIndex);
        Dat64TablePatcher.WriteUInt32(newPetRow, PetHash32Offset, spec.PetHash32);
        Dat64TablePatcher.WriteUInt16(newPetRow, PetHash16Offset, spec.PetHash16);
        int newPetRowIndex = petPatcher.AppendRow(newPetRow);
        Dat64Table patchedPet = petPatcher.BuildAndVerify();

        VerifyAppendedPetMtx(patchedMtxTypes, patchedMtxGs, patchedPet, spec, newMtxRowIndex, newGsRowIndex, newPetRowIndex);

        return new PatchedPetMtxResult(
            patchedMtxTypes.Serialize(),
            patchedMtxGs.Serialize(),
            patchedPet.Serialize(),
            newMtxRowIndex,
            newGsRowIndex,
            newPetRowIndex,
            spec.MtxHash32,
            spec.PetHash32,
            spec.PetHash16);
    }

    public static void VerifyAppendedPetMtx(
        Dat64Table mtxTypes,
        Dat64Table mtxGs,
        Dat64Table pet,
        PetMtxPatchSpec spec,
        int expectedMtxRow,
        int expectedGsRow,
        int expectedPetRow)
    {
        ArgumentNullException.ThrowIfNull(mtxTypes);
        ArgumentNullException.ThrowIfNull(mtxGs);
        ArgumentNullException.ThrowIfNull(pet);
        ArgumentNullException.ThrowIfNull(spec);

        if (!string.Equals(mtxTypes.ReadString(expectedMtxRow, MtxTypesIdOffset), spec.MtxTypeId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Patched MtxTypes.Id verification failed.");
        }

        if (mtxTypes.ReadUInt32(expectedMtxRow, MtxTypesHash32Offset) != spec.MtxHash32)
        {
            throw new InvalidDataException("Patched MtxTypes.HASH32 verification failed.");
        }

        if (!string.Equals(mtxTypes.ReadString(expectedMtxRow, MtxTypesNameOffset), spec.DisplayName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Patched MtxTypes.Name verification failed.");
        }

        if (mtxGs.ReadForeignRow(expectedGsRow, MtxGsTypeOffset) != (ulong)expectedMtxRow)
        {
            throw new InvalidDataException("Patched MtxTypeGameSpecific.Type foreign row verification failed.");
        }

        if (mtxGs.ReadUInt32(expectedGsRow, MtxGsHash32Offset) != spec.MtxHash32)
        {
            throw new InvalidDataException("Patched MtxTypeGameSpecific.HASH32 verification failed.");
        }

        if (!string.Equals(mtxGs.ReadString(expectedGsRow, MtxGsDdsFileOffset), spec.IconDdsPath, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Patched MtxTypeGameSpecific.DDSFile verification failed.");
        }

        if (mtxGs.ReadInt32(expectedGsRow, MtxGsCategoryOffset) != spec.MtxCategory)
        {
            throw new InvalidDataException("Patched MtxTypeGameSpecific.Category verification failed.");
        }

        if (!mtxGs.ReadInt32Array(expectedGsRow, MtxGsCol6Offset).SequenceEqual(spec.PetSlots))
        {
            throw new InvalidDataException("Patched MtxTypeGameSpecific pet slot array verification failed.");
        }

        if (!string.Equals(pet.ReadString(expectedPetRow, PetIdOffset), spec.PetId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Patched Pet.Id verification failed.");
        }

        if (pet.ReadForeignRow(expectedPetRow, PetMtxGsKeyOffset) != (ulong)expectedGsRow)
        {
            throw new InvalidDataException("Patched Pet.MtxTypeGameSpecificKey verification failed.");
        }

        if (pet.ReadUInt32(expectedPetRow, PetHash32Offset) != spec.PetHash32 ||
            pet.ReadUInt16(expectedPetRow, PetHash16Offset) != spec.PetHash16)
        {
            throw new InvalidDataException("Patched Pet.HASH32/HASH16 verification failed.");
        }
    }

    private static int FindMtxGsByTypeRow(Dat64Table mtxGs, ulong mtxTypeRow)
    {
        for (int r = 0; r < mtxGs.RowCount; r++)
        {
            if (mtxGs.ReadForeignRow(r, MtxGsTypeOffset) == mtxTypeRow)
            {
                return r;
            }
        }

        return -1;
    }

    private static void ValidateStride(string tableName, Dat64Table table, int expectedStride)
    {
        if (table.RowStride != expectedStride)
        {
            throw new InvalidDataException(
                $"{tableName} stride mismatch: expected {expectedStride} bytes (3.29.3d), got {table.RowStride} bytes.");
        }
    }
}
