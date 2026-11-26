namespace DualMod.StashQol.Shared;

/// <summary>
/// Shared constants and protocol identifiers for <c>DualMod.StashQol</c>.
/// </summary>
public static class StashQolProtocol
{
    public const string ModId = "stashqol";
    public const string ChannelName = "stashqol";

    public const string RegexRollerUiPath = "Metadata/UI/Mods/StashQol/RegexRoller.ui";
    public const string PobImportUiPath = "Metadata/UI/Mods/StashQol/PobImport.ui";

    public const string RegexRollerWindowId = "stashqol_regex_roller";
    public const string PobImportWindowId = "stashqol_pob_import";

    public const string BulkUseKeybindId = "stashqol.bulk_use";

    public const string SlotDepositAllId = "stashqol.deposit_all";
    public const string SlotSortInventoryId = "stashqol.sort_inventory";
    public const string SlotRegexRollerId = "stashqol.regex_roller";
    public const string SlotPobImportId = "stashqol.pob_import";
    public const string SlotVendorAllId = "stashqol.vendor_all";
    public const string SlotUpgradeMapsId = "stashqol.upgrade_maps";
    public const string SlotUpgradeOilsId = "stashqol.upgrade_oils";

    public const int MainInventoryWidth = 12;
    public const int MainInventoryHeight = 5;
    public const int MaxRegexRollsHardCap = 2000;
    public const int RegexTimeoutMilliseconds = 50;
    public const int MaxBulkUseHardCap = 500;
    public const ushort BlightMaxStackSize = 5000;

    public const string StackedDeckMetadata = "Metadata/Items/DivinationCards/DivinationCardDeck";
    public const string OrbOfRegretMetadata = "Metadata/Items/Currency/CurrencyPassiveRefund";
    public const string OrbOfUnmakingMetadata = "Metadata/Items/Currency/CurrencyAtlasPassiveRefund";
    public const string ChaosOrbMetadata = "Metadata/Items/Currency/CurrencyRerollRare";

    /// <summary>Canonical 13-tier linear Blight oil upgrade chain (3-to-1 per tier).</summary>
    public static readonly IReadOnlyList<string> BlightOilTiers =
    [
        "Clear Oil",
        "Sepia Oil",
        "Amber Oil",
        "Verdant Oil",
        "Teal Oil",
        "Azure Oil",
        "Indigo Oil",
        "Violet Oil",
        "Crimson Oil",
        "Black Oil",
        "Opalescent Oil",
        "Silver Oil",
        "Golden Oil",
    ];
}

/// <summary>Deposits eligible main-inventory items into affinity tabs (or fallback tab).</summary>
public sealed record DepositAllRequest(
    int? FallbackTabIndex,
    bool RespectAffinities = true);

/// <summary>Vendors eligible items in the specified personal stash tab via <c>VendorQuote</c>.</summary>
public sealed record VendorAllRequest(
    int TabIndex,
    bool Confirmed = false);

/// <summary>Sorts and deterministically bin-packs the character's 12×5 main inventory.</summary>
public sealed record InventorySortRequest(
    bool IncludeExpanded = false);

/// <summary>Upgrades 3-to-1 normal group-0 maps inside the specified Map Stash tab.</summary>
public sealed record UpgradeMapTiersRequest(
    int MapTabIndex,
    int? SpecificTier = null,
    bool IncludeCorrupted = false);

/// <summary>Cascades 3-to-1 upgrades across the 13-tier Blight oil chain in the Blight Stash tab.</summary>
public sealed record UpgradeBlightOilsRequest(
    int BlightTabIndex,
    string? StopAtTier = null);

/// <summary>Rolls a currency item onto a target item until its rendered modifiers match <paramref name="Pattern"/>.</summary>
public sealed record RollUntilRegexRequest(
    uint TargetInventoryId,
    uint TargetItemId,
    int TargetGridX,
    int TargetGridY,
    string CurrencyMetadataPath,
    string Pattern,
    int MaxRolls = 200,
    bool IgnoreCase = true,
    bool MatchFullCard = false);

/// <summary>Executes bulk stack use triggered by <c>Ctrl+Shift+RMB</c> on a hovered inventory or stash item.</summary>
public sealed record BulkUseRequest(
    uint HoveredInventoryId,
    uint HoveredItemId,
    int HoveredGridX,
    int HoveredGridY,
    string HoveredMetadataPath,
    uint HoveredStackSize,
    uint? CursorSourceInventoryId = null,
    uint? CursorItemId = null,
    string? CursorMetadataPath = null,
    uint CursorStackSize = 0,
    int MaxActions = 200);

/// <summary>Imports a PoB passive tree plan (Base64/zlib, XML, or node list) and optionally applies connected nodes.</summary>
public sealed record PobImportRequest(
    string Payload,
    bool AutoApplyOnLevelUp = true,
    bool ApplyImmediately = true);

/// <summary>Toggles automatic passive allocation on <c>GameEvents.LevelUp</c>.</summary>
public sealed record PobToggleAutoApplyRequest(bool Enabled);

/// <summary>Clears any stored PoB passive plan for the active character.</summary>
public sealed record PobClearPlanRequest();

/// <summary>Server-to-client QoL execution summary displayed via <c>IToastService</c> and open dialog status labels.</summary>
public sealed record StashQolToastNotification(
    string Feature,
    string Title,
    string Message,
    string Severity = "Info",
    int AffectedCount = 0,
    bool RequiresConfirmation = false,
    int? PendingTabIndex = null);
