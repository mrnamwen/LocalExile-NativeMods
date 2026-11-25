using System.Buffers.Binary;
using System.Text;

namespace LocalExile.Client.AssetPipeline.Dat64;

/// <summary>
/// Specification for appending a brand-new custom Currency item across the three PoE 3.29.3d tables:
/// <c>Data/BaseItemTypes.datc64</c>, <c>Data/ItemVisualIdentity.datc64</c>, and <c>Data/CurrencyItems.datc64</c>,
/// together with its <c>Metadata/Items/Currency/....it</c> metadata file.
/// </summary>
public sealed record CurrencyItemPatchSpec
{
    /// <summary>Unique <c>BaseItemTypes.Id</c> (also determines <c>HASH32</c> and the <c>.it</c> virtual path).</summary>
    public required string BaseItemTypeId { get; init; }

    /// <summary>In-game display name shown on the item card and ground label.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Parent metadata path (<c>BaseItemTypes.InheritsFrom</c>). Defaults to <c>Metadata/Items/Currency/StackableCurrency</c>.</summary>
    public string InheritsFrom { get; init; } = "Metadata/Items/Currency/StackableCurrency";

    /// <summary>Row index into <c>Data/ItemClasses.datc64</c>. Row <c>30</c> is <c>StackableCurrency</c> in 3.29.3d.</summary>
    public ulong ItemClassRow { get; init; } = 30UL;

    /// <summary>Inventory cell width (1 for standard 1x1 currency orbs).</summary>
    public int Width { get; init; } = 1;

    /// <summary>Inventory cell height (1 for standard 1x1 currency orbs).</summary>
    public int Height { get; init; } = 1;

    /// <summary>Minimum drop level (<c>BaseItemTypes.DropLevel</c>).</summary>
    public int DropLevel { get; init; } = 1;

    /// <summary>Ground footprint size (<c>BaseItemTypes.SizeOnGround</c>; 1 for orbs).</summary>
    public int SizeOnGround { get; init; } = 1;

    /// <summary>Mod domain enum value (<c>BaseItemTypes.ModDomain</c>; 43 for stackable currency).</summary>
    public int ModDomain { get; init; } = 43;

    /// <summary>
    /// Optional explicit <c>BaseItemTypes.TagsKeys</c> row indices.
    /// Defaults to empty (<c>[]</c>); always allocated at a fresh variable-section offset (never aliased).
    /// </summary>
    public ulong[] TagsKeys { get; init; } = [];

    /// <summary>Unique <c>ItemVisualIdentity.Id</c>.</summary>
    public required string VisualIdentityId { get; init; }

    /// <summary>
    /// Explicit non-zero <c>ItemVisualIdentity.HASH16</c> indexed directly by PoE 3.29.3d's <c>Base::Read</c>
    /// (<c>0x1421ed3ea</c>). If <see langword="null"/>, a deterministic collision-free <c>u16</c> is allocated automatically.
    /// </summary>
    public ushort? VisualIdentityHash16 { get; init; }

    /// <summary>Virtual path to the 2D inventory icon <c>.dds</c> (e.g. <c>Art/2DItems/Currency/Mods/Siphon/SiphonOrbEmpty.dds</c>).</summary>
    public required string IconDdsPath { get; init; }

    /// <summary>Ground/drop animated object path (<c>ItemVisualIdentity.AOFile</c>).</summary>
    public string AoFile { get; init; } = "Metadata/Items/Currency/CurrencyOrb.ao";

    /// <summary>Optional <c>SoundEffects</c> row for <c>ItemVisualIdentity.InventorySoundEffect</c> (33 = standard currency orb click).</summary>
    public ulong? InventorySoundEffectRow { get; init; } = 33UL;

    /// <summary>Maximum inventory stack size (<c>CurrencyItems.StackSize</c>).</summary>
    public int StackSize { get; init; } = 20;

    /// <summary>Maximum Currency Stash Tab stack size (<c>CurrencyItems.CurrencyTab_StackSize</c>; 0 for non-stackable / unique-payload orbs).</summary>
    public int CurrencyTabStackSize { get; init; } = 10000;

    /// <summary>
    /// <c>CurrencyItems.CurrencyUseType</c>: <c>2</c> = right-click then left-click target item
    /// (Orb of Annulment / Exalted Orb / Chaos Orb / Charged Compass).
    /// </summary>
    public int CurrencyUseType { get; init; } = 2;

    /// <summary>
    /// Client-side currency action string (<c>CurrencyItems.Action</c>).
    /// <c>"remove_random_mod"</c> (verified UTF-16LE in 3.29.3d <c>Game.exe</c>) enables native right-click cursor-apply
    /// onto Magic/Rare gear emitting <c>C-&gt;S 0x0071</c> / <c>0x0072</c>.
    /// </summary>
    public string Action { get; init; } = "remove_random_mod";

    /// <summary>Usage instructions shown at the bottom of the currency hover tooltip (<c>CurrencyItems.Directions</c>).</summary>
    public string Directions { get; init; } = string.Empty;

    /// <summary>Optional gamepad instructions (<c>CurrencyItems.XBoxDirections</c>); defaults to <see cref="Directions"/>.</summary>
    public string? XBoxDirections { get; init; }

    /// <summary>Effect description shown on the currency hover tooltip (<c>CurrencyItems.Description</c>).</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Optional <c>BaseItemTypes.Id</c> that a full stack converts into (<c>CurrencyItems.FullStack_BaseItemTypesKey</c>); null for orbs.</summary>
    public string? FullStackBaseItemTypeId { get; init; }

    /// <summary>
    /// When <see langword="true"/>, the generated <c>.it</c> file adds <c>LocalStats {}</c> and <c>Mods { ... }</c>
    /// (matching stock <c>CurrencyItemisedSextantModifier.it</c> + <c>Equipment.it</c>) so PoE 3.29 natively renders
    /// stored item modifiers directly on this currency's hover tooltip card.
    /// </summary>
    public bool SupportsStoredMods { get; init; }

    /// <summary>Primary stat description list used when <see cref="SupportsStoredMods"/> is <see langword="true"/>.</summary>
    public string StatDescriptionList { get; init; } = "Metadata/StatDescriptions/stat_descriptions.txt";

    /// <summary>Advanced mod description list used when <see cref="SupportsStoredMods"/> is <see langword="true"/>.</summary>
    public string ModStatDescriptionList { get; init; } = "Metadata/StatDescriptions/advanced_mod_stat_descriptions.txt";

    /// <summary>Template stock currency <c>BaseItemTypes.Id</c> whose scalar defaults are cloned safely before overwriting.</summary>
    public string TemplateBaseItemTypeId { get; init; } = "Metadata/Items/Currency/CurrencyRemoveMod";

    /// <summary>Optional source PNG/image path for icon conversion in CLI batch mode.</summary>
    public string? SourceIconPath { get; init; }

    /// <summary>Computed 32-bit MurmurHash2 of <see cref="BaseItemTypeId"/> (<c>BaseItemTypes.HASH32</c>).</summary>
    public uint BaseItemHash32 => PoeHashing.Hash32(BaseItemTypeId);

    /// <summary>Virtual path of the item's <c>.it</c> metadata file (<c>{BaseItemTypeId}.it</c>).</summary>
    public string ItVirtualPath => BaseItemTypeId + ".it";
}

/// <summary>
/// Metadata and generated <c>.it</c> payload for a single appended currency item.
/// </summary>
public sealed record PatchedCurrencyEntry(
    CurrencyItemPatchSpec Spec,
    int BaseItemTypesRowIndex,
    int ItemVisualIdentityRowIndex,
    int CurrencyItemsRowIndex,
    uint BaseItemHash32,
    ushort VisualIdentityHash16,
    string ItVirtualPath,
    byte[] ItFileBytes);

/// <summary>
/// Serialized output of appending one or more custom currencies across the three 3.29.3d <c>.datc64</c> tables.
/// </summary>
public sealed record PatchedCurrencyResult(
    byte[] BaseItemTypesBytes,
    byte[] ItemVisualIdentityBytes,
    byte[] CurrencyItemsBytes,
    IReadOnlyList<PatchedCurrencyEntry> Entries);

/// <summary>
/// Reusable PoE 3.29.3d custom currency <c>.datc64</c> &amp; <c>.it</c> patcher.
/// Appends brand-new currency rows across <c>Data/BaseItemTypes.datc64</c>, <c>Data/ItemVisualIdentity.datc64</c>,
/// and <c>Data/CurrencyItems.datc64</c> without modifying any stock row, while deep-copying or clearing every
/// <c>foreignrow[]</c> / array variable-section offset so PoE 3.29's in-place loader resolution never aliases
/// across rows (see <c>.codexmind/regressions/dat64-foreignrow-aliasing.md</c>).
/// Supports layering cleanly onto already-patched tables so multiple currency mods can coexist.
/// </summary>
public static class CurrencyDatPatcher
{
    // Verified 3.29.3d live Bundles2 exact row strides and column offsets (PoE 1 schema, 0 bytes drift):

    // --- Data/BaseItemTypes.datc64 (5,461 stock rows, stride 374) ---
    public const int BaseItemTypesRowStride = 374;
    public const int BitIdOffset = 0;                          // string (8) - UNIQUE
    public const int BitItemClassOffset = 8;                   // foreignrow -> ItemClasses (16)
    public const int BitWidthOffset = 24;                      // i32 (4)
    public const int BitHeightOffset = 28;                     // i32 (4)
    public const int BitNameOffset = 32;                       // string (8)
    public const int BitInheritsFromOffset = 40;               // string (8)
    public const int BitDropLevelOffset = 48;                  // i32 (4)
    public const int BitFlavourTextOffset = 52;                // foreignrow -> FlavourText (16)
    public const int BitImplicitModsOffset = 68;               // foreignrow[] -> Mods (16)
    public const int BitSizeOnGroundOffset = 84;               // i32 (4)
    public const int BitSoundEffectOffset = 88;                // foreignrow -> SoundEffects (16)
    public const int BitTagsKeysOffset = 104;                  // foreignrow[] -> Tags (16)
    public const int BitModDomainOffset = 120;                 // enumrow/i32 (4)
    public const int BitItemVisualIdentityOffset = 124;        // foreignrow -> ItemVisualIdentity (16)
    public const int BitHash32Offset = 140;                    // u32 (4) - UNIQUE
    public const int BitVendorRecipeAchOffset = 144;           // foreignrow[] (16)
    public const int BitInflectionOffset = 160;                // string (8)
    public const int BitEquipAchOffset = 168;                  // foreignrow (16)
    public const int BitIsCorruptedOffset = 184;               // bool (1)
    public const int BitIdentifyAchOffset = 185;               // foreignrow[] (16)
    public const int BitIdentifyMagicAchOffset = 201;          // foreignrow[] (16)
    public const int BitFragmentBaseItemOffset = 217;          // row -> BaseItemTypes (8)
    public const int BitUnmodifiableOffset = 259;              // bool (1)
    public const int BitAchievement1Offset = 260;              // foreignrow[] (16)
    public const int BitAchievement2Offset = 276;              // foreignrow[] (16)
    public const int BitShopTagOffset = 294;                   // foreignrow (16)
    public const int BitCol32ArrayOffset = 310;                // array[] (16)
    public const int BitCol33ArrayOffset = 326;                // array[] (16)
    public const int BitCol34ArrayOffset = 342;                // array[] (16)
    public const int BitTalismanEnchantsOffset = 358;          // foreignrow[] (16)

    /// <summary>Every <c>foreignrow[]</c> column offset in <c>BaseItemTypes.datc64</c> (all verified non-aliased).</summary>
    public static readonly int[] BaseItemTypesForeignRowArrayOffsets =
    [
        BitImplicitModsOffset,
        BitTagsKeysOffset,
        BitVendorRecipeAchOffset,
        BitIdentifyAchOffset,
        BitIdentifyMagicAchOffset,
        BitAchievement1Offset,
        BitAchievement2Offset,
        BitTalismanEnchantsOffset,
    ];

    /// <summary>Additional opaque array columns in <c>BaseItemTypes.datc64</c> cleared to fresh empty headers.</summary>
    public static readonly int[] BaseItemTypesOpaqueArrayOffsets =
    [
        BitCol32ArrayOffset,
        BitCol33ArrayOffset,
        BitCol34ArrayOffset,
    ];

    // --- Data/ItemVisualIdentity.datc64 (15,516 stock rows, stride 604) ---
    public const int ItemVisualIdentityRowStride = 604;
    public const int IviIdOffset = 0;                          // string (8) - UNIQUE
    public const int IviDdsFileOffset = 8;                     // string (8)
    public const int IviAoFileOffset = 16;                     // string (8)
    public const int IviInventorySoundOffset = 24;             // foreignrow -> SoundEffects (16)
    public const int IviHash16Offset = 40;                     // u16 (2) - UNIQUE
    public const int IviAoFile2Offset = 42;                    // string (8)

    /// <summary>Every <c>foreignrow[]</c> column offset in <c>ItemVisualIdentity.datc64</c>.</summary>
    public static readonly int[] ItemVisualIdentityForeignRowArrayOffsets =
    [
        226, // Pickup_AchievementItemsKeys
        258, // Identify_AchievementItemsKeys
        282, // Corrupt_AchievementItemsKeys
        422, // KalandraAchievement
        439, // HeistAchievement
        531, // __col54
        547, // __col55
        588, // __col60
    ];

    /// <summary>Every <c>string[]</c> column offset in <c>ItemVisualIdentity.datc64</c>.</summary>
    public static readonly int[] ItemVisualIdentityStringArrayOffsets =
    [
        50,  // MarauderSMFiles
        66,  // RangerSMFiles
        82,  // WitchSMFiles
        98,  // DuelistDexSMFiles
        114, // TemplarSMFiles
        130, // ShadowSMFiles
        146, // ScionSMFiles
        242, // SMFiles
    ];

    // --- Data/CurrencyItems.datc64 (1,962 stock rows, stride 205) ---
    public const int CurrencyItemsRowStride = 205;
    public const int CurBaseItemTypesKeyOffset = 0;            // foreignrow -> BaseItemTypes (16) - UNIQUE
    public const int CurStackSizeOffset = 16;                  // i32 (4)
    public const int CurCurrencyUseTypeOffset = 20;            // i32 (4)
    public const int CurActionOffset = 24;                     // string (8)
    public const int CurDirectionsOffset = 32;                 // string (8)
    public const int CurFullStackBaseItemOffset = 40;          // foreignrow -> BaseItemTypes (16)
    public const int CurDescriptionOffset = 56;                // string (8)
    public const int CurUsageAchOffset = 64;                   // foreignrow[] (16)
    public const int CurScrollOffset = 80;                     // bool (1)
    public const int CurPossessionAchOffset = 81;              // foreignrow (16)
    public const int CurCol10Offset = 97;                      // foreignrow[] (16)
    public const int CurCol11Offset = 113;                     // i32[] (16)
    public const int CurCurrencyTabStackSizeOffset = 129;      // i32 (4)
    public const int CurXBoxDirectionsOffset = 133;            // string (8)
    public const int CurModifyMapsAchOffset = 145;             // foreignrow[] (16)
    public const int CurModifyContractsAchOffset = 161;        // foreignrow[] (16)
    public const int CurCombineAchOffset = 177;                // foreignrow[] (16)
    public const int CurChangedForHardmodeOffset = 193;        // bool (1)
    public const int CurDescriptionHardmodeOffset = 194;       // string (8)
    public const int CurIsGoldOffset = 202;                    // bool (1)

    /// <summary>Every <c>foreignrow[]</c> column offset in <c>CurrencyItems.datc64</c>.</summary>
    public static readonly int[] CurrencyItemsForeignRowArrayOffsets =
    [
        CurUsageAchOffset,
        CurCol10Offset,
        CurModifyMapsAchOffset,
        CurModifyContractsAchOffset,
        CurCombineAchOffset,
    ];

    /// <summary>
    /// Returns the locked Phase 5 Orb of Siphoning specs (Empty + Charged), matching the verified
    /// <c>LocalExileCurrencyRegistrar</c> server contract (<c>HASH32</c> / <c>HASH16</c> / <c>.it</c> components).
    /// </summary>
    public static IReadOnlyList<CurrencyItemPatchSpec> CreateSiphonOrbSpecs(
        string? emptySourceIconPath = null,
        string? chargedSourceIconPath = null) =>
    [
        new CurrencyItemPatchSpec
        {
            BaseItemTypeId = "Metadata/Items/Currency/Mods/Siphon/SiphonOrbEmpty",
            DisplayName = "Orb of Siphoning",
            VisualIdentityId = "SiphonOrbEmpty",
            VisualIdentityHash16 = 0xD501,
            IconDdsPath = "Art/2DItems/Currency/Mods/Siphon/SiphonOrbEmpty.dds",
            StackSize = 20,
            CurrencyTabStackSize = 10000,
            CurrencyUseType = 2,
            Action = "remove_random_mod",
            Directions = "Right click this item then left click a magic or rare item to siphon one of its modifiers.",
            Description = "Extracts a chosen explicit modifier from a magic or rare item into a Charged Orb of Siphoning",
            SupportsStoredMods = false,
            SourceIconPath = emptySourceIconPath,
        },
        new CurrencyItemPatchSpec
        {
            BaseItemTypeId = "Metadata/Items/Currency/Mods/Siphon/SiphonOrbCharged",
            DisplayName = "Charged Orb of Siphoning",
            VisualIdentityId = "SiphonOrbCharged",
            VisualIdentityHash16 = 0xD502,
            IconDdsPath = "Art/2DItems/Currency/Mods/Siphon/SiphonOrbCharged.dds",
            StackSize = 1,
            CurrencyTabStackSize = 0,
            CurrencyUseType = 2,
            Action = "remove_random_mod",
            Directions = "Right click this item then left click a magic or rare item to replace one of its modifiers with the stored modifier.",
            Description = "Contains a siphoned modifier. Replaces a chosen modifier on a magic or rare item",
            SupportsStoredMods = true,
            SourceIconPath = chargedSourceIconPath,
        },
    ];

    /// <summary>
    /// Appends a single custom currency item across the three <c>.datc64</c> tables.
    /// </summary>
    public static PatchedCurrencyResult AppendCurrency(
        ReadOnlySpan<byte> stockBaseItemTypes,
        ReadOnlySpan<byte> stockItemVisualIdentity,
        ReadOnlySpan<byte> stockCurrencyItems,
        CurrencyItemPatchSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return AppendCurrencies(stockBaseItemTypes, stockItemVisualIdentity, stockCurrencyItems, [spec]);
    }

    /// <summary>
    /// Appends one or more custom currency items across <c>BaseItemTypes.datc64</c>, <c>ItemVisualIdentity.datc64</c>,
    /// and <c>CurrencyItems.datc64</c>, verifying binary round-trip integrity, key uniqueness, and zero
    /// <c>foreignrow[]</c> offset aliasing across every row of all three tables.
    /// </summary>
    public static PatchedCurrencyResult AppendCurrencies(
        ReadOnlySpan<byte> stockBaseItemTypes,
        ReadOnlySpan<byte> stockItemVisualIdentity,
        ReadOnlySpan<byte> stockCurrencyItems,
        IReadOnlyList<CurrencyItemPatchSpec> specs)
    {
        ArgumentNullException.ThrowIfNull(specs);
        if (specs.Count == 0)
        {
            throw new ArgumentException("At least one CurrencyItemPatchSpec is required.", nameof(specs));
        }

        Dat64Table baseItemTypes = Dat64Table.Parse(stockBaseItemTypes);
        Dat64Table itemVisualIdentity = Dat64Table.Parse(stockItemVisualIdentity);
        Dat64Table currencyItems = Dat64Table.Parse(stockCurrencyItems);

        ValidateStride("BaseItemTypes.datc64", baseItemTypes, BaseItemTypesRowStride);
        ValidateStride("ItemVisualIdentity.datc64", itemVisualIdentity, ItemVisualIdentityRowStride);
        ValidateStride("CurrencyItems.datc64", currencyItems, CurrencyItemsRowStride);

        // Index existing unique keys so we reject collisions up front and can auto-allocate free HASH16s.
        var usedBitIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedBitHash32s = new HashSet<uint>();
        var bitRowById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int r = 0; r < baseItemTypes.RowCount; r++)
        {
            string id = baseItemTypes.ReadString(r, BitIdOffset);
            usedBitIds.Add(id);
            usedBitHash32s.Add(baseItemTypes.ReadUInt32(r, BitHash32Offset));
            bitRowById.TryAdd(id, r);
        }

        var usedIviIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedIviHash16s = new HashSet<ushort>();
        for (int r = 0; r < itemVisualIdentity.RowCount; r++)
        {
            usedIviIds.Add(itemVisualIdentity.ReadString(r, IviIdOffset));
            ushort h16 = itemVisualIdentity.ReadUInt16(r, IviHash16Offset);
            if (h16 != 0)
            {
                usedIviHash16s.Add(h16);
            }
        }

        var usedCurBitKeys = new HashSet<ulong>();
        for (int r = 0; r < currencyItems.RowCount; r++)
        {
            if (currencyItems.ReadForeignRow(r, CurBaseItemTypesKeyOffset) is ulong fk)
            {
                usedCurBitKeys.Add(fk);
            }
        }

        var bitPatcher = new Dat64TablePatcher(baseItemTypes);
        var iviPatcher = new Dat64TablePatcher(itemVisualIdentity);
        var curPatcher = new Dat64TablePatcher(currencyItems);

        // Pre-validate specs and allocate row indices in a two-pass loop so FullStackBaseItemTypeId can reference
        // another item in the same batch as well as any existing stock row.
        var staged = new List<(CurrencyItemPatchSpec Spec, ushort Hash16, int IviRow, int BitRow)>(specs.Count);

        foreach (CurrencyItemPatchSpec spec in specs)
        {
            ValidateSpec(spec);

            if (!usedBitIds.Add(spec.BaseItemTypeId))
            {
                throw new InvalidOperationException($"BaseItemTypes.datc64 already contains Id '{spec.BaseItemTypeId}'.");
            }

            uint hash32 = spec.BaseItemHash32;
            if (!usedBitHash32s.Add(hash32))
            {
                throw new InvalidOperationException($"BaseItemTypes.datc64 HASH32 collision (0x{hash32:X8}) for '{spec.BaseItemTypeId}'.");
            }

            if (!usedIviIds.Add(spec.VisualIdentityId))
            {
                throw new InvalidOperationException($"ItemVisualIdentity.datc64 already contains Id '{spec.VisualIdentityId}'.");
            }

            ushort hash16;
            if (spec.VisualIdentityHash16.HasValue)
            {
                hash16 = spec.VisualIdentityHash16.Value;
                if (hash16 == 0)
                {
                    throw new ArgumentException($"ItemVisualIdentity.HASH16 cannot be 0 for '{spec.VisualIdentityId}'.");
                }

                if (!usedIviHash16s.Add(hash16))
                {
                    throw new InvalidOperationException(
                        $"ItemVisualIdentity.datc64 HASH16 collision (0x{hash16:X4}) for '{spec.VisualIdentityId}'.");
                }
            }
            else
            {
                hash16 = AllocateUniqueHash16(spec.VisualIdentityId, usedIviHash16s);
            }

            // Resolve template rows (fallback to row 0 for minimal synthetic unit-test tables).
            int templateBitRow = bitRowById.TryGetValue(spec.TemplateBaseItemTypeId, out int foundBit) ? foundBit : 0;
            ulong templateIviKey = baseItemTypes.ReadForeignRow(templateBitRow, BitItemVisualIdentityOffset) ?? 0UL;
            int templateIviRow = templateIviKey < (ulong)itemVisualIdentity.RowCount ? (int)templateIviKey : 0;

            // 1. Append ItemVisualIdentity row (clearing all string[] & foreignrow[] columns to non-aliased empty headers).
            byte[] newIviRow = iviPatcher.CloneRow(templateIviRow);
            iviPatcher.SetString(newIviRow, IviIdOffset, spec.VisualIdentityId);
            iviPatcher.SetString(newIviRow, IviDdsFileOffset, spec.IconDdsPath);
            iviPatcher.SetString(newIviRow, IviAoFileOffset, spec.AoFile);
            if (spec.InventorySoundEffectRow.HasValue)
            {
                Dat64TablePatcher.WriteForeignRow(newIviRow, IviInventorySoundOffset, spec.InventorySoundEffectRow.Value);
            }

            Dat64TablePatcher.WriteUInt16(newIviRow, IviHash16Offset, hash16);
            iviPatcher.SetString(newIviRow, IviAoFile2Offset, string.Empty);

            foreach (int frCol in ItemVisualIdentityForeignRowArrayOffsets)
            {
                iviPatcher.SetForeignRowArray(newIviRow, frCol, ReadOnlySpan<ulong>.Empty);
            }

            foreach (int strArrCol in ItemVisualIdentityStringArrayOffsets)
            {
                Dat64TablePatcher.WriteArrayHeader(newIviRow, strArrCol, 0UL, iviPatcher.AppendForeignRowArray(ReadOnlySpan<ulong>.Empty).Offset);
            }

            int newIviRowIndex = iviPatcher.AppendRow(newIviRow);

            // 2. Append BaseItemTypes row (deep-copying TagsKeys and clearing every other foreignrow[]/array[] column).
            byte[] newBitRow = bitPatcher.CloneRow(templateBitRow);
            bitPatcher.SetString(newBitRow, BitIdOffset, spec.BaseItemTypeId);
            Dat64TablePatcher.WriteForeignRow(newBitRow, BitItemClassOffset, spec.ItemClassRow);
            Dat64TablePatcher.WriteInt32(newBitRow, BitWidthOffset, spec.Width);
            Dat64TablePatcher.WriteInt32(newBitRow, BitHeightOffset, spec.Height);
            bitPatcher.SetString(newBitRow, BitNameOffset, spec.DisplayName);
            bitPatcher.SetString(newBitRow, BitInheritsFromOffset, spec.InheritsFrom);
            Dat64TablePatcher.WriteInt32(newBitRow, BitDropLevelOffset, spec.DropLevel);
            Dat64TablePatcher.WriteForeignRow(newBitRow, BitFlavourTextOffset, null);
            Dat64TablePatcher.WriteInt32(newBitRow, BitSizeOnGroundOffset, spec.SizeOnGround);
            Dat64TablePatcher.WriteInt32(newBitRow, BitModDomainOffset, spec.ModDomain);
            Dat64TablePatcher.WriteForeignRow(newBitRow, BitItemVisualIdentityOffset, (ulong)newIviRowIndex);
            Dat64TablePatcher.WriteUInt32(newBitRow, BitHash32Offset, hash32);
            bitPatcher.SetString(newBitRow, BitInflectionOffset, string.Empty);
            Dat64TablePatcher.WriteForeignRow(newBitRow, BitEquipAchOffset, null);
            Dat64TablePatcher.WriteBool(newBitRow, BitIsCorruptedOffset, false);
            BinaryPrimitives.WriteUInt64LittleEndian(newBitRow.AsSpan(BitFragmentBaseItemOffset, 8), Dat64Table.NullForeignRow);
            Dat64TablePatcher.WriteBool(newBitRow, BitUnmodifiableOffset, false);
            Dat64TablePatcher.WriteForeignRow(newBitRow, BitShopTagOffset, null);

            foreach (int frCol in BaseItemTypesForeignRowArrayOffsets)
            {
                if (frCol == BitTagsKeysOffset)
                {
                    bitPatcher.SetForeignRowArray(newBitRow, frCol, spec.TagsKeys);
                }
                else
                {
                    bitPatcher.SetForeignRowArray(newBitRow, frCol, ReadOnlySpan<ulong>.Empty);
                }
            }

            foreach (int arrCol in BaseItemTypesOpaqueArrayOffsets)
            {
                bitPatcher.SetForeignRowArray(newBitRow, arrCol, ReadOnlySpan<ulong>.Empty);
            }

            int newBitRowIndex = bitPatcher.AppendRow(newBitRow);
            bitRowById[spec.BaseItemTypeId] = newBitRowIndex;
            staged.Add((spec, hash16, newIviRowIndex, newBitRowIndex));
        }

        // 3. Append CurrencyItems rows (clearing every foreignrow[] and i32[] column so no template achievements alias).
        var entries = new List<PatchedCurrencyEntry>(staged.Count);
        foreach (var (spec, hash16, newIviRowIndex, newBitRowIndex) in staged)
        {
            if (!usedCurBitKeys.Add((ulong)newBitRowIndex))
            {
                throw new InvalidOperationException($"CurrencyItems.datc64 already references BaseItemTypes row {newBitRowIndex}.");
            }

            int templateBitRow = bitRowById.TryGetValue(spec.TemplateBaseItemTypeId, out int foundBit) && foundBit < baseItemTypes.RowCount
                ? foundBit
                : 0;
            int templateCurRow = FindCurrencyRowByBaseItemRow(currencyItems, (ulong)templateBitRow);
            if (templateCurRow < 0)
            {
                templateCurRow = 0;
            }

            ulong? fullStackRow = null;
            if (!string.IsNullOrWhiteSpace(spec.FullStackBaseItemTypeId))
            {
                if (!bitRowById.TryGetValue(spec.FullStackBaseItemTypeId, out int fsRow))
                {
                    throw new InvalidOperationException(
                        $"FullStackBaseItemTypeId '{spec.FullStackBaseItemTypeId}' not found in BaseItemTypes.");
                }

                fullStackRow = (ulong)fsRow;
            }

            byte[] newCurRow = curPatcher.CloneRow(templateCurRow);
            Dat64TablePatcher.WriteForeignRow(newCurRow, CurBaseItemTypesKeyOffset, (ulong)newBitRowIndex);
            Dat64TablePatcher.WriteInt32(newCurRow, CurStackSizeOffset, spec.StackSize);
            Dat64TablePatcher.WriteInt32(newCurRow, CurCurrencyUseTypeOffset, spec.CurrencyUseType);
            curPatcher.SetString(newCurRow, CurActionOffset, spec.Action);
            curPatcher.SetString(newCurRow, CurDirectionsOffset, spec.Directions);
            Dat64TablePatcher.WriteForeignRow(newCurRow, CurFullStackBaseItemOffset, fullStackRow);
            curPatcher.SetString(newCurRow, CurDescriptionOffset, spec.Description);
            Dat64TablePatcher.WriteBool(newCurRow, CurScrollOffset, false);
            Dat64TablePatcher.WriteForeignRow(newCurRow, CurPossessionAchOffset, null);
            Dat64TablePatcher.WriteInt32(newCurRow, CurCurrencyTabStackSizeOffset, spec.CurrencyTabStackSize);
            curPatcher.SetString(newCurRow, CurXBoxDirectionsOffset, spec.XBoxDirections ?? spec.Directions);
            Dat64TablePatcher.WriteBool(newCurRow, CurChangedForHardmodeOffset, false);
            curPatcher.SetString(newCurRow, CurDescriptionHardmodeOffset, string.Empty);
            Dat64TablePatcher.WriteBool(newCurRow, CurIsGoldOffset, false);

            foreach (int frCol in CurrencyItemsForeignRowArrayOffsets)
            {
                curPatcher.SetForeignRowArray(newCurRow, frCol, ReadOnlySpan<ulong>.Empty);
            }

            curPatcher.SetInt32Array(newCurRow, CurCol11Offset, ReadOnlySpan<int>.Empty);

            int newCurRowIndex = curPatcher.AppendRow(newCurRow);
            byte[] itBytes = BuildItemMetadataItBytes(spec);

            entries.Add(new PatchedCurrencyEntry(
                spec,
                newBitRowIndex,
                newIviRowIndex,
                newCurRowIndex,
                spec.BaseItemHash32,
                hash16,
                spec.ItVirtualPath,
                itBytes));
        }

        Dat64Table patchedBit = bitPatcher.BuildAndVerify();
        Dat64Table patchedIvi = iviPatcher.BuildAndVerify();
        Dat64Table patchedCur = curPatcher.BuildAndVerify();

        VerifyAppendedCurrencies(patchedBit, patchedIvi, patchedCur, entries);

        return new PatchedCurrencyResult(
            patchedBit.Serialize(),
            patchedIvi.Serialize(),
            patchedCur.Serialize(),
            entries);
    }

    /// <summary>
    /// Generates the UTF-16LE (with BOM, CRLF) PoE 3.29.3d <c>.it</c> metadata file for a custom currency item.
    /// </summary>
    public static byte[] BuildItemMetadataItBytes(CurrencyItemPatchSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var sb = new StringBuilder();
        sb.Append("version 3\r\n");
        sb.Append("extends \"").Append(spec.InheritsFrom).Append("\"\r\n");

        if (spec.SupportsStoredMods)
        {
            sb.Append("\r\nBase\r\n{\r\n}\r\n");
            sb.Append("\r\nLocalStats\r\n{\r\n}\r\n");
            sb.Append("\r\nMods\r\n{\r\n");
            sb.Append("\tstat_description_list = \"").Append(spec.StatDescriptionList).Append("\"\r\n");
            if (!string.IsNullOrWhiteSpace(spec.ModStatDescriptionList))
            {
                sb.Append("\tmod_stat_description_list = \"").Append(spec.ModStatDescriptionList).Append("\"\r\n");
            }

            sb.Append("\r\n");
            sb.Append("\tenable_rarity = \"normal\"\r\n");
            sb.Append("\tenable_rarity = \"magic\"\r\n");
            sb.Append("\tenable_rarity = \"rare\"\r\n");
            sb.Append("\tenable_rarity = \"unique\"\r\n");
            sb.Append("}\r\n");
        }

        byte[] payload = Encoding.Unicode.GetBytes(sb.ToString());
        byte[] withBom = new byte[2 + payload.Length];
        withBom[0] = 0xFF;
        withBom[1] = 0xFE;
        Buffer.BlockCopy(payload, 0, withBom, 2, payload.Length);
        return withBom;
    }

    /// <summary>
    /// Verifies every appended currency row across <c>BaseItemTypes</c>, <c>ItemVisualIdentity</c>,
    /// and <c>CurrencyItems</c>, including global key uniqueness and zero <c>foreignrow[]</c> variable-section
    /// offset aliasing across all rows.
    /// </summary>
    public static void VerifyAppendedCurrencies(
        Dat64Table baseItemTypes,
        Dat64Table itemVisualIdentity,
        Dat64Table currencyItems,
        IReadOnlyList<PatchedCurrencyEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(baseItemTypes);
        ArgumentNullException.ThrowIfNull(itemVisualIdentity);
        ArgumentNullException.ThrowIfNull(currencyItems);
        ArgumentNullException.ThrowIfNull(entries);

        ValidateStride("BaseItemTypes.datc64", baseItemTypes, BaseItemTypesRowStride);
        ValidateStride("ItemVisualIdentity.datc64", itemVisualIdentity, ItemVisualIdentityRowStride);
        ValidateStride("CurrencyItems.datc64", currencyItems, CurrencyItemsRowStride);

        // Pre-index all foreignrow[] non-empty offsets per table so we catch ANY aliasing against ANY stock or appended row.
        foreach (PatchedCurrencyEntry entry in entries)
        {
            CurrencyItemPatchSpec spec = entry.Spec;
            int bitRow = entry.BaseItemTypesRowIndex;
            int iviRow = entry.ItemVisualIdentityRowIndex;
            int curRow = entry.CurrencyItemsRowIndex;

            // 1. BaseItemTypes field checks
            if (!string.Equals(baseItemTypes.ReadString(bitRow, BitIdOffset), spec.BaseItemTypeId, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"BaseItemTypes[{bitRow}].Id mismatch.");
            }

            if (baseItemTypes.ReadForeignRow(bitRow, BitItemClassOffset) != spec.ItemClassRow)
            {
                throw new InvalidDataException($"BaseItemTypes[{bitRow}].ItemClassesKey mismatch.");
            }

            if (baseItemTypes.ReadInt32(bitRow, BitWidthOffset) != spec.Width ||
                baseItemTypes.ReadInt32(bitRow, BitHeightOffset) != spec.Height)
            {
                throw new InvalidDataException($"BaseItemTypes[{bitRow}] Width/Height mismatch.");
            }

            if (!string.Equals(baseItemTypes.ReadString(bitRow, BitNameOffset), spec.DisplayName, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"BaseItemTypes[{bitRow}].Name mismatch.");
            }

            if (!string.Equals(baseItemTypes.ReadString(bitRow, BitInheritsFromOffset), spec.InheritsFrom, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"BaseItemTypes[{bitRow}].InheritsFrom mismatch.");
            }

            if (baseItemTypes.ReadForeignRow(bitRow, BitItemVisualIdentityOffset) != (ulong)iviRow)
            {
                throw new InvalidDataException($"BaseItemTypes[{bitRow}].ItemVisualIdentity mismatch.");
            }

            if (baseItemTypes.ReadUInt32(bitRow, BitHash32Offset) != entry.BaseItemHash32)
            {
                throw new InvalidDataException($"BaseItemTypes[{bitRow}].HASH32 mismatch.");
            }

            if (!baseItemTypes.ReadForeignRowArray(bitRow, BitTagsKeysOffset).SequenceEqual(spec.TagsKeys))
            {
                throw new InvalidDataException($"BaseItemTypes[{bitRow}].TagsKeys mismatch.");
            }

            // Global BaseItemTypes Id & HASH32 uniqueness
            for (int r = 0; r < baseItemTypes.RowCount; r++)
            {
                if (r == bitRow) continue;
                if (string.Equals(baseItemTypes.ReadString(r, BitIdOffset), spec.BaseItemTypeId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Duplicate BaseItemTypes.Id '{spec.BaseItemTypeId}' at row {r}.");
                }

                if (baseItemTypes.ReadUInt32(r, BitHash32Offset) == entry.BaseItemHash32)
                {
                    throw new InvalidDataException($"Duplicate BaseItemTypes.HASH32 0x{entry.BaseItemHash32:X8} at row {r}.");
                }
            }

            VerifyNoForeignRowArrayAliasing("BaseItemTypes", baseItemTypes, bitRow, BaseItemTypesForeignRowArrayOffsets);

            // 2. ItemVisualIdentity field checks
            if (!string.Equals(itemVisualIdentity.ReadString(iviRow, IviIdOffset), spec.VisualIdentityId, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"ItemVisualIdentity[{iviRow}].Id mismatch.");
            }

            if (!string.Equals(itemVisualIdentity.ReadString(iviRow, IviDdsFileOffset), spec.IconDdsPath, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"ItemVisualIdentity[{iviRow}].DDSFile mismatch.");
            }

            if (!string.Equals(itemVisualIdentity.ReadString(iviRow, IviAoFileOffset), spec.AoFile, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"ItemVisualIdentity[{iviRow}].AOFile mismatch.");
            }

            ushort actualHash16 = itemVisualIdentity.ReadUInt16(iviRow, IviHash16Offset);
            if (actualHash16 == 0 || actualHash16 != entry.VisualIdentityHash16)
            {
                throw new InvalidDataException($"ItemVisualIdentity[{iviRow}].HASH16 mismatch (expected 0x{entry.VisualIdentityHash16:X4}, got 0x{actualHash16:X4}).");
            }

            // Global ItemVisualIdentity Id & HASH16 uniqueness
            for (int r = 0; r < itemVisualIdentity.RowCount; r++)
            {
                if (r == iviRow) continue;
                if (string.Equals(itemVisualIdentity.ReadString(r, IviIdOffset), spec.VisualIdentityId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Duplicate ItemVisualIdentity.Id '{spec.VisualIdentityId}' at row {r}.");
                }

                if (itemVisualIdentity.ReadUInt16(r, IviHash16Offset) == entry.VisualIdentityHash16)
                {
                    throw new InvalidDataException($"Duplicate ItemVisualIdentity.HASH16 0x{entry.VisualIdentityHash16:X4} at row {r}.");
                }
            }

            VerifyNoForeignRowArrayAliasing("ItemVisualIdentity", itemVisualIdentity, iviRow, ItemVisualIdentityForeignRowArrayOffsets);

            // 3. CurrencyItems field checks
            if (currencyItems.ReadForeignRow(curRow, CurBaseItemTypesKeyOffset) != (ulong)bitRow)
            {
                throw new InvalidDataException($"CurrencyItems[{curRow}].BaseItemTypesKey mismatch.");
            }

            if (currencyItems.ReadInt32(curRow, CurStackSizeOffset) != spec.StackSize)
            {
                throw new InvalidDataException($"CurrencyItems[{curRow}].StackSize mismatch.");
            }

            if (currencyItems.ReadInt32(curRow, CurCurrencyUseTypeOffset) != spec.CurrencyUseType)
            {
                throw new InvalidDataException($"CurrencyItems[{curRow}].CurrencyUseType mismatch.");
            }

            if (!string.Equals(currencyItems.ReadString(curRow, CurActionOffset), spec.Action, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"CurrencyItems[{curRow}].Action mismatch.");
            }

            if (!string.Equals(currencyItems.ReadString(curRow, CurDirectionsOffset), spec.Directions, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"CurrencyItems[{curRow}].Directions mismatch.");
            }

            if (!string.Equals(currencyItems.ReadString(curRow, CurDescriptionOffset), spec.Description, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"CurrencyItems[{curRow}].Description mismatch.");
            }

            if (currencyItems.ReadInt32(curRow, CurCurrencyTabStackSizeOffset) != spec.CurrencyTabStackSize)
            {
                throw new InvalidDataException($"CurrencyItems[{curRow}].CurrencyTab_StackSize mismatch.");
            }

            ulong? actualFullStack = currencyItems.ReadForeignRow(curRow, CurFullStackBaseItemOffset);
            if (string.IsNullOrWhiteSpace(spec.FullStackBaseItemTypeId))
            {
                if (actualFullStack.HasValue)
                {
                    throw new InvalidDataException($"CurrencyItems[{curRow}].FullStack_BaseItemTypesKey expected NULL, got {actualFullStack.Value}.");
                }
            }
            else
            {
                int expectedFullStackRow = baseItemTypes.FindRowByString(BitIdOffset, spec.FullStackBaseItemTypeId, StringComparison.OrdinalIgnoreCase);
                if (expectedFullStackRow < 0 || actualFullStack != (ulong)expectedFullStackRow)
                {
                    throw new InvalidDataException($"CurrencyItems[{curRow}].FullStack_BaseItemTypesKey mismatch.");
                }
            }

            // Global CurrencyItems BaseItemTypesKey uniqueness
            for (int r = 0; r < currencyItems.RowCount; r++)
            {
                if (r == curRow) continue;
                if (currencyItems.ReadForeignRow(r, CurBaseItemTypesKeyOffset) == (ulong)bitRow)
                {
                    throw new InvalidDataException($"Duplicate CurrencyItems.BaseItemTypesKey {bitRow} at row {r}.");
                }
            }

            VerifyNoForeignRowArrayAliasing("CurrencyItems", currencyItems, curRow, CurrencyItemsForeignRowArrayOffsets);
        }
    }

    private static void VerifyNoForeignRowArrayAliasing(
        string tableName,
        Dat64Table table,
        int appendedRow,
        int[] foreignRowArrayColumnOffsets)
    {
        foreach (int colOffset in foreignRowArrayColumnOffsets)
        {
            ulong count = table.ReadUInt64(appendedRow, colOffset);
            if (count == 0)
            {
                continue;
            }

            ulong varOffset = table.ReadUInt64(appendedRow, colOffset + 8);
            for (int r = 0; r < table.RowCount; r++)
            {
                if (r == appendedRow) continue;
                if (table.ReadUInt64(r, colOffset) > 0 && table.ReadUInt64(r, colOffset + 8) == varOffset)
                {
                    throw new InvalidDataException(
                        $"{tableName}[{appendedRow}] foreignrow[] at offset {colOffset} aliases row {r} variable offset 0x{varOffset:X}; in-place foreignrow[] resolution would crash PoE 3.29.");
                }
            }
        }
    }

    private static void ValidateSpec(CurrencyItemPatchSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (string.IsNullOrWhiteSpace(spec.BaseItemTypeId))
        {
            throw new ArgumentException("BaseItemTypeId is required.", nameof(spec));
        }

        if (string.IsNullOrWhiteSpace(spec.DisplayName))
        {
            throw new ArgumentException("DisplayName is required.", nameof(spec));
        }

        if (string.IsNullOrWhiteSpace(spec.VisualIdentityId))
        {
            throw new ArgumentException("VisualIdentityId is required.", nameof(spec));
        }

        if (string.IsNullOrWhiteSpace(spec.IconDdsPath))
        {
            throw new ArgumentException("IconDdsPath is required.", nameof(spec));
        }

        if (spec.Width <= 0 || spec.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(spec), "Width and Height must be positive.");
        }

        if (spec.StackSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(spec), "StackSize must be positive.");
        }
    }

    private static ushort AllocateUniqueHash16(string visualIdentityId, HashSet<ushort> used)
    {
        ushort candidate = PoeHashing.FoldHash16(PoeHashing.Hash32(visualIdentityId));
        for (int attempt = 0; attempt < 65535; attempt++)
        {
            ushort h = (ushort)((candidate + attempt) & 0xFFFF);
            if (h != 0 && used.Add(h))
            {
                return h;
            }
        }

        throw new InvalidOperationException("Exhausted all 16-bit ItemVisualIdentity.HASH16 values.");
    }

    private static int FindCurrencyRowByBaseItemRow(Dat64Table currencyItems, ulong baseItemRow)
    {
        for (int r = 0; r < currencyItems.RowCount; r++)
        {
            if (currencyItems.ReadForeignRow(r, CurBaseItemTypesKeyOffset) == baseItemRow)
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
