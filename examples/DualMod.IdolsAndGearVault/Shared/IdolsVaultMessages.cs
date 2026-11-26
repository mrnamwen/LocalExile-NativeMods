namespace DualMod.IdolsAndGearVault.Shared;

/// <summary>
/// Shared constants, protocol identifiers, settings schema, and typed RPC records for
/// <c>DualMod.IdolsAndGearVault</c> (Phase 4 flagship showcase targeting SDK <c>1.1.0</c>).
/// </summary>
public static class IdolsVaultProtocol
{
    public const string ModId = "idolsvault";
    public const string ChannelName = "idolsvault";
    public const string PanelUiPath = "Metadata/UI/Mods/IdolsVault/IdolsVaultPanel.ui";
    public const string PanelWindowId = "idolsvault_panel";

    // Conflict-free managed ScreenSlot button identifiers.
    public const string SlotOpenPanelId = "idolsvault.open_panel";
    public const string SlotStashVaultId = "idolsvault.stash_vault";

    // Centralized keybind identifier.
    public const string TogglePanelKeybindId = "idolsvault.toggle_panel";

    // Phase 4 Stash Affinity service route/rule identifiers.
    public const string GearVaultRouteId = "idolsvault.gear_vault_route";
    public const string GearStockAffinityRuleId = "idolsvault.gear_stock_affinity";

    // Phase 4 Live Native Extend identifiers.
    public const string GealBuiltinIdolBoardCount = "IdolBoardCount";
    public const string CustomControlStatusBadge = "IdolVault.StatusBadge";
    public const byte NativeBoardRulesetId = 0xAE;

    // Defaults and guardrails.
    public const ushort DefaultGearVaultTabIndex = 4;
    public const ushort MaxGearVaultTabIndex = 255;
    public const string DefaultGearClassFilter = "Helmets";
    public const byte DefaultStockAffinityBitId = 4;
    public const byte MaxStockAffinityBitId = 21;
    public const int DefaultMaxVaultEntriesPerSync = 64;
    public const int MinVaultEntriesPerSync = 8;
    public const int MaxVaultEntriesPerSync = 256;
    public const int MaxTabChoicesInCombo = 48;
}

/// <summary>
/// Mod settings matching <c>settings.json</c> defaults for both client and server halves.
/// </summary>
public sealed record IdolsVaultSettings
{
    public int DefaultGearVaultTabIndex { get; init; } = IdolsVaultProtocol.DefaultGearVaultTabIndex;
    public string DefaultGearClassFilter { get; init; } = IdolsVaultProtocol.DefaultGearClassFilter;
    public bool EnableStockAffinityMapping { get; init; } = true;
    public int StockAffinityBitId { get; init; } = IdolsVaultProtocol.DefaultStockAffinityBitId;
    public bool ShowAltTooltipAffixTags { get; init; } = true;
    public int MaxVaultEntriesPerSync { get; init; } = IdolsVaultProtocol.DefaultMaxVaultEntriesPerSync;
}

/// <summary>
/// Supported equipment gear classes selectable in the Gear Vault affinity filter.
/// </summary>
public static class GearVaultClasses
{
    public const string Helmets = "Helmets";
    public const string BodyArmours = "Body Armours";
    public const string Gloves = "Gloves";
    public const string Boots = "Boots";
    public const string Belts = "Belts";
    public const string Rings = "Rings";
    public const string Amulets = "Amulets";
    public const string Weapons = "Weapons";

    /// <summary>Ordered list of all 8 supported Gear Vault classes.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Helmets,
        BodyArmours,
        Gloves,
        Boots,
        Belts,
        Rings,
        Amulets,
        Weapons,
    ];

    /// <summary>Normalizes a class label or returns <see cref="Helmets"/> if unknown.</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Helmets;
        }

        string trimmed = value.Trim();
        foreach (string known in All)
        {
            if (string.Equals(known, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        return trimmed.ToUpperInvariant() switch
        {
            "HELMET" or "HELMS" => Helmets,
            "BODYARMOUR" or "BODYARMOURS" or "BODY_ARMOURS" or "CHEST" => BodyArmours,
            "GLOVE" => Gloves,
            "BOOT" => Boots,
            "BELT" => Belts,
            "RING" => Rings,
            "AMULET" => Amulets,
            "WEAPON" => Weapons,
            _ => Helmets,
        };
    }

    /// <summary>Returns <c>true</c> if <paramref name="value"/> matches one of the 8 supported Gear Vault classes.</summary>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value.Trim();
        return All.Any(c => string.Equals(c, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Classifies an item from its <c>BaseItemTypes</c> metadata path, base name, and optional 1-based <c>ItemClassId</c>.
    /// Returns <c>null</c> if the item is not one of the 8 Gear Vault equipment classes.
    /// </summary>
    public static string? Classify(string? metadataPath, string? baseName = null, uint itemClassId = 0)
    {
        string path = (metadataPath ?? string.Empty).Replace('\\', '/');
        if (path.Contains("/Helmets/", StringComparison.OrdinalIgnoreCase))
        {
            return Helmets;
        }

        if (path.Contains("/BodyArmours/", StringComparison.OrdinalIgnoreCase))
        {
            return BodyArmours;
        }

        if (path.Contains("/Gloves/", StringComparison.OrdinalIgnoreCase))
        {
            return Gloves;
        }

        if (path.Contains("/Boots/", StringComparison.OrdinalIgnoreCase))
        {
            return Boots;
        }

        if (path.Contains("/Belts/", StringComparison.OrdinalIgnoreCase))
        {
            return Belts;
        }

        if (path.Contains("/Rings/", StringComparison.OrdinalIgnoreCase))
        {
            return Rings;
        }

        if (path.Contains("/Amulets/", StringComparison.OrdinalIgnoreCase))
        {
            return Amulets;
        }

        if (path.Contains("/Weapons/", StringComparison.OrdinalIgnoreCase))
        {
            return Weapons;
        }

        // Fallback to standard PoE 3.29 ItemClasses IDs when metadata path is abbreviated.
        string? byClassId = itemClassId switch
        {
            26 => Helmets,
            25 => BodyArmours,
            23 => Gloves,
            24 => Boots,
            22 => Belts,
            21 => Rings,
            20 => Amulets,
            6 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 14 or 15 or 16 or 17 or 32 or 33 or 34 => Weapons,
            _ => null,
        };
        if (byClassId is not null)
        {
            return byClassId;
        }

        if (!string.IsNullOrWhiteSpace(baseName))
        {
            if (baseName.Contains("Helmet", StringComparison.OrdinalIgnoreCase) ||
                baseName.Contains("Burgonet", StringComparison.OrdinalIgnoreCase) ||
                baseName.Contains("Circlet", StringComparison.OrdinalIgnoreCase) ||
                baseName.Contains("Mask", StringComparison.OrdinalIgnoreCase))
            {
                return Helmets;
            }

            if (baseName.Contains("Plate", StringComparison.OrdinalIgnoreCase) ||
                baseName.Contains("Regalia", StringComparison.OrdinalIgnoreCase) ||
                baseName.Contains("Garb", StringComparison.OrdinalIgnoreCase) ||
                baseName.Contains("Coat", StringComparison.OrdinalIgnoreCase))
            {
                return BodyArmours;
            }

            if (baseName.Contains("Gloves", StringComparison.OrdinalIgnoreCase) ||
                baseName.Contains("Gauntlets", StringComparison.OrdinalIgnoreCase) ||
                baseName.Contains("Mitts", StringComparison.OrdinalIgnoreCase))
            {
                return Gloves;
            }

            if (baseName.Contains("Boots", StringComparison.OrdinalIgnoreCase) ||
                baseName.Contains("Greaves", StringComparison.OrdinalIgnoreCase) ||
                baseName.Contains("Slippers", StringComparison.OrdinalIgnoreCase))
            {
                return Boots;
            }

            if (baseName.Contains("Belt", StringComparison.OrdinalIgnoreCase) ||
                baseName.Contains("Sash", StringComparison.OrdinalIgnoreCase) ||
                baseName.Contains("Vise", StringComparison.OrdinalIgnoreCase))
            {
                return Belts;
            }

            if (baseName.Contains("Ring", StringComparison.OrdinalIgnoreCase))
            {
                return Rings;
            }

            if (baseName.Contains("Amulet", StringComparison.OrdinalIgnoreCase) ||
                baseName.Contains("Talisman", StringComparison.OrdinalIgnoreCase))
            {
                return Amulets;
            }

            if (baseName.Contains("Sword", StringComparison.OrdinalIgnoreCase) ||
                baseName.Contains("Axe", StringComparison.OrdinalIgnoreCase) ||
                baseName.Contains("Bow", StringComparison.OrdinalIgnoreCase) ||
                baseName.Contains("Wand", StringComparison.OrdinalIgnoreCase) ||
                baseName.Contains("Claw", StringComparison.OrdinalIgnoreCase) ||
                baseName.Contains("Dagger", StringComparison.OrdinalIgnoreCase) ||
                baseName.Contains("Staff", StringComparison.OrdinalIgnoreCase) ||
                baseName.Contains("Mace", StringComparison.OrdinalIgnoreCase) ||
                baseName.Contains("Sceptre", StringComparison.OrdinalIgnoreCase))
            {
                return Weapons;
            }
        }

        return null;
    }
}

/// <summary>
/// Pure deterministic geometry rules for the PoE 3.29 32-cell Idol Board:
/// a 6×6 grid with the 4 corner cells <c>(0,0)</c>, <c>(5,0)</c>, <c>(0,5)</c>, <c>(5,5)</c> permanently blocked.
/// </summary>
public static class IdolBoardGeometry
{
    public const int Columns = 6;
    public const int Rows = 6;
    public const int TotalCells = Columns * Rows;
    public const int BlockedCornerCount = 4;
    public const int ActiveCellCount = TotalCells - BlockedCornerCount;

    /// <summary>Valid PoE 3.29 Idol footprints: 1×1, 1×2, 2×1, 2×2, 1×3, 3×1.</summary>
    public static IReadOnlyList<(int Width, int Height, string Archetype)> ValidIdolSizes { get; } =
    [
        (1, 1, "Minor Idol"),
        (1, 2, "Kamasan Idol"),
        (2, 1, "Burial Idol"),
        (2, 2, "Conqueror Idol"),
        (1, 3, "Totemic Idol"),
        (3, 1, "Noble Idol"),
    ];

    /// <summary>Returns <c>true</c> if <c>(col, row)</c> is one of the 4 blocked corner cells.</summary>
    public static bool IsCornerBlocked(int col, int row) =>
        (col == 0 || col == Columns - 1) && (row == 0 || row == Rows - 1);

    /// <summary>Returns <c>true</c> if <c>(col, row)</c> is in bounds and not a blocked corner cell.</summary>
    public static bool IsActiveCell(int col, int row) =>
        col >= 0 && col < Columns && row >= 0 && row < Rows && !IsCornerBlocked(col, row);

    /// <summary>Returns <c>true</c> if <c>(width, height)</c> is a legal PoE 3.29 Idol footprint.</summary>
    public static bool IsValidIdolSize(int width, int height) =>
        (width, height) is (1, 1) or (1, 2) or (2, 1) or (2, 2) or (1, 3) or (3, 1);

    /// <summary>
    /// Validates whether an Idol of size <c>(width, height)</c> can be placed at <c>(col, row)</c>
    /// without hitting blocked corners, out-of-bounds cells, invalid sizes, duplicate IDs, or existing Idols.
    /// </summary>
    public static bool CanPlace(
        int col,
        int row,
        int width,
        int height,
        IReadOnlyCollection<PlacedIdolRecord> existing,
        out string reason,
        string? candidateIdolId = null,
        string? ignoreIdolId = null)
    {
        if (!IsValidIdolSize(width, height))
        {
            reason = $"Invalid Idol footprint {width}x{height}. Allowed sizes: 1x1, 1x2, 2x1, 2x2, 1x3, 3x1.";
            return false;
        }

        if (col < 0 || row < 0 || col + width > Columns || row + height > Rows)
        {
            reason = $"Placement ({col}, {row}) with size {width}x{height} is outside the 6x6 Idol Board.";
            return false;
        }

        for (int r = row; r < row + height; r++)
        {
            for (int c = col; c < col + width; c++)
            {
                if (IsCornerBlocked(c, r))
                {
                    reason = $"Cell ({c}, {r}) is a locked corner cell on the 32-cell Idol Board.";
                    return false;
                }
            }
        }

        foreach (PlacedIdolRecord other in existing)
        {
            if (ignoreIdolId is not null && string.Equals(other.IdolId, ignoreIdolId, StringComparison.Ordinal))
            {
                continue;
            }

            if (candidateIdolId is not null && string.Equals(other.IdolId, candidateIdolId, StringComparison.Ordinal))
            {
                reason = $"Idol '{candidateIdolId}' is already placed on the board.";
                return false;
            }

            bool overlapsX = col < other.Col + other.Width && col + width > other.Col;
            bool overlapsY = row < other.Row + other.Height && row + height > other.Row;
            if (overlapsX && overlapsY)
            {
                reason = $"Placement ({col}, {row}) overlaps existing Idol '{other.Name}' ({other.IdolId}) at ({other.Col}, {other.Row}).";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>Finds the first row-major legal top-left coordinate for an Idol of size <c>(width, height)</c>.</summary>
    public static (int Col, int Row)? FindFirstFreeCell(
        int width,
        int height,
        IReadOnlyCollection<PlacedIdolRecord> existing)
    {
        if (!IsValidIdolSize(width, height))
        {
            return null;
        }

        for (int row = 0; row <= Rows - height; row++)
        {
            for (int col = 0; col <= Columns - width; col++)
            {
                if (CanPlace(col, row, width, height, existing, out _))
                {
                    return (col, row);
                }
            }
        }

        return null;
    }

    /// <summary>Counts total occupied active cells across all placed Idols.</summary>
    public static int CountOccupiedCells(IReadOnlyCollection<PlacedIdolRecord> existing)
    {
        int sum = 0;
        foreach (PlacedIdolRecord idol in existing)
        {
            sum += Math.Max(0, idol.Width * idol.Height);
        }

        return Math.Min(ActiveCellCount, sum);
    }
}

/// <summary>
/// Built-in Idol blueprint presets used by the Idol Board showcase UI for one-click placement testing.
/// </summary>
public sealed record IdolPreset(
    string PresetId,
    string Name,
    string MetadataPath,
    int Width,
    int Height,
    int Rarity,
    string IconPath,
    string[] AffixTags,
    string[] ModSummaryLines);

public static class IdolPresetCatalog
{
    public static IReadOnlyList<IdolPreset> Presets { get; } =
    [
        new(
            PresetId: "minor_1x1",
            Name: "Minor Idol of Fortune",
            MetadataPath: "Metadata/Items/Idols/MinorIdol",
            Width: 1,
            Height: 1,
            Rarity: 2,
            IconPath: "Art/2DItems/Currency/Scarab.dds",
            AffixTags: ["Idol", "Life", "PackSize"],
            ModSummaryLines:
            [
                "+6% increased Pack Size in your Maps",
                "12% increased Quantity of Items found in your Maps",
            ]),
        new(
            PresetId: "kamasan_1x2",
            Name: "Kamasan Idol of the Swarm",
            MetadataPath: "Metadata/Items/Idols/KamasanIdol",
            Width: 1,
            Height: 2,
            Rarity: 2,
            IconPath: "Art/2DItems/Currency/Scarab.dds",
            AffixTags: ["Idol", "Attack", "Breach"],
            ModSummaryLines:
            [
                "Your Maps contain an additional Breach",
                "Breaches in your Maps have 25% increased Monster Density",
            ]),
        new(
            PresetId: "burial_2x1",
            Name: "Burial Idol of Strongboxes",
            MetadataPath: "Metadata/Items/Idols/BurialIdol",
            Width: 2,
            Height: 1,
            Rarity: 2,
            IconPath: "Art/2DItems/Currency/Scarab.dds",
            AffixTags: ["Idol", "Strongbox", "Currency"],
            ModSummaryLines:
            [
                "Your Maps contain 2 additional Strongboxes",
                "Strongboxes in your Maps are at least Rare",
            ]),
        new(
            PresetId: "conqueror_2x2",
            Name: "Conqueror Idol of Dominance",
            MetadataPath: "Metadata/Items/Idols/ConquerorIdol",
            Width: 2,
            Height: 2,
            Rarity: 3,
            IconPath: "Art/2DItems/Currency/Scarab.dds",
            AffixTags: ["Idol", "Boss", "Atlas", "Life"],
            ModSummaryLines:
            [
                "Final Map Boss drops an additional Shaper or Elder Guardian Map",
                "+18% increased Effect of Explicit Modifiers on your Maps",
            ]),
        new(
            PresetId: "totemic_1x3",
            Name: "Totemic Idol of the Harvest",
            MetadataPath: "Metadata/Items/Idols/TotemicIdol",
            Width: 1,
            Height: 3,
            Rarity: 2,
            IconPath: "Art/2DItems/Currency/Scarab.dds",
            AffixTags: ["Idol", "Harvest", "Life"],
            ModSummaryLines:
            [
                "Your Maps contain The Sacred Grove",
                "+35% increased Lifeforce dropped by Harvest Monsters",
            ]),
        new(
            PresetId: "noble_3x1",
            Name: "Noble Idol of Delirium",
            MetadataPath: "Metadata/Items/Idols/NobleIdol",
            Width: 3,
            Height: 1,
            Rarity: 2,
            IconPath: "Art/2DItems/Currency/Scarab.dds",
            AffixTags: ["Idol", "Delirium", "Attack"],
            ModSummaryLines:
            [
                "Your Maps contain a Mirror of Delirium",
                "Delirium Rewards fill 20% faster in your Maps",
            ]),
    ];
}

/// <summary>Snapshot of a single server-validated Idol placed on the 32-cell Idol Board.</summary>
public sealed record PlacedIdolRecord(
    string IdolId,
    string Name,
    string MetadataPath,
    int Col,
    int Row,
    int Width,
    int Height,
    int Rarity,
    string IconPath,
    string[] AffixTags,
    string[] ModSummaryLines);

/// <summary>Client → Server request to place an Idol on the 32-cell Idol Board.</summary>
public sealed record PlaceIdolRequest(
    string IdolId,
    string Name,
    string MetadataPath,
    int Col,
    int Row,
    int Width,
    int Height,
    int Rarity = 2,
    string? IconPath = null,
    string[]? AffixTags = null,
    string[]? ModSummaryLines = null);

/// <summary>Client → Server request to remove an Idol from the 32-cell Idol Board.</summary>
public sealed record RemoveIdolRequest(string IdolId);

/// <summary>Client → Server request to synchronize the current Idol Board state.</summary>
public sealed record SyncIdolBoardRequest();

/// <summary>Server → Client authoritative Idol Board state snapshot.</summary>
public sealed record IdolBoardStateSync(
    PlacedIdolRecord[] Idols,
    int ActiveCellsTotal,
    int OccupiedCells,
    string StatusMessage,
    bool Succeeded = true);

/// <summary>Snapshot of an equipment item stored in the configured Gear Vault stash tab.</summary>
public sealed record GearVaultItemRecord(
    uint ItemId,
    string Name,
    string BaseName,
    string MetadataPath,
    string GearClass,
    int ItemLevel,
    int Rarity,
    ushort TabIndex,
    string[] AffixTags);

/// <summary>Client → Server request to configure the Gear Vault target tab, class filter, and stock affinity mapping.</summary>
public sealed record ConfigureGearVaultRequest(
    ushort TargetTabIndex,
    string GearClassFilter,
    bool StockAffinityEnabled,
    byte StockAffinityBitId);

/// <summary>Client → Server request to list matching items in the configured Gear Vault tab.</summary>
public sealed record SyncGearVaultRequest(string? GearClassFilter = null);

/// <summary>Server → Client Gear Vault configuration and item listing snapshot.</summary>
public sealed record GearVaultStateSync(
    ushort TargetTabIndex,
    string GearClassFilter,
    bool StockAffinityEnabled,
    byte StockAffinityBitId,
    GearVaultItemRecord[] Items,
    string StatusMessage,
    bool Succeeded = true);

/// <summary>Server → Client toast notification payload.</summary>
public sealed record IdolsVaultToastNotification(
    string Title,
    string Message,
    string Severity = "Info");
