namespace DualMod.SiphonOrb.Shared;

/// <summary>
/// Shared constants, appended 3.29.3d currency coordinates, virtual asset paths, and custom opcodes
/// compiled into both the Server and Client halves of the Orb of Siphoning dual-side mod.
/// </summary>
public static class SiphonProtocol
{
    public const string ModId = "siphon";
    public const string ChannelName = "siphon";

    // Virtual UI and item metadata paths (under Mods/Siphon/ for clean Waypoint isolation).
    public const string PickerUiPath = "Metadata/UI/Mods/Siphon/SiphonPicker.ui";
    public const string PickerWindowId = "siphon_picker_window";

    public const string EmptyOrbBaseId = "Metadata/Items/Currency/Mods/Siphon/SiphonOrbEmpty";
    public const string EmptyOrbItPath = "Metadata/Items/Currency/Mods/Siphon/SiphonOrbEmpty.it";
    public const string EmptyOrbVisualId = "SiphonOrbEmpty";
    public const string EmptyOrbIconPath = "Art/2DItems/Currency/Mods/Siphon/SiphonOrbEmpty.dds";
    public const string EmptyOrbDisplayName = "Orb of Siphoning";
    public const string EmptyOrbDirections = "Right click this item then left click a magic or rare item to siphon one of its modifiers.";
    public const string EmptyOrbDescription = "Extracts a chosen explicit modifier from a magic or rare item into a Charged Orb of Siphoning";

    public const string ChargedOrbBaseId = "Metadata/Items/Currency/Mods/Siphon/SiphonOrbCharged";
    public const string ChargedOrbItPath = "Metadata/Items/Currency/Mods/Siphon/SiphonOrbCharged.it";
    public const string ChargedOrbVisualId = "SiphonOrbCharged";
    public const string ChargedOrbIconPath = "Art/2DItems/Currency/Mods/Siphon/SiphonOrbCharged.dds";
    public const string ChargedOrbDisplayName = "Charged Orb of Siphoning";
    public const string ChargedOrbDirections = "Right click this item then left click a magic or rare item to replace one of its modifiers with the stored modifier.";
    public const string ChargedOrbDescription = "Contains a siphoned modifier. Replaces a chosen modifier on a magic or rare item";

    // Appended 3.29.3d currency table rows (NEVER replacing any stock currency row).
    // Verified stock counts in LocalExile/data/client.db:
    //   BaseItemTypes = 5461 (0..5460), ItemVisualIdentity = 15516 (0..15515), CurrencyItems = 1962 (0..1961).
    public const int EmptyOrbBaseItemTypesRow = 5461;
    public const int ChargedOrbBaseItemTypesRow = 5462;

    public const int EmptyOrbVisualRow = 15516;
    public const int ChargedOrbVisualRow = 15517;

    public const int EmptyOrbCurrencyRow = 1962;
    public const int ChargedOrbCurrencyRow = 1963;

    // Deterministic MurmurHash2 (seed 0) hashes of the metadata paths (0 collisions against stock BaseItemTypes).
    public const uint EmptyOrbTypeHash = 0x2B06B9D8u;   // 721861080
    public const uint ChargedOrbTypeHash = 0xBD658699u; // 3177547417

    // Collision-free 16-bit ItemVisualIdentity.HASH16 keys verified against all 15,516 stock rows.
    public const ushort EmptyOrbVisualHash16 = 0xD501;   // 54529
    public const ushort ChargedOrbVisualHash16 = 0xD502; // 54530

    public const ushort EmptyOrbMaxStack = 20;
    public const ushort ChargedOrbMaxStack = 1;

    /// <summary>Maximum number of explicit affix rows displayed in <c>SiphonPicker.ui</c>.</summary>
    public const int MaxPickerAffixRows = 6;

    /// <summary>Host-channel custom ruleset ID for Orb of Siphoning (0xA0=Host, 0xA1=HarvestGarden, 0xA2=Maxwell).</summary>
    public const byte SiphonRulesetId = 0xA3;

    /// <summary>Custom opcode IDs matching <c>mod.json</c> <c>nativeClient.customOpcodes</c>.</summary>
    public const ushort OpcodeOpenPicker = 1201;
    public const ushort OpcodeSelectAffix = 1202;
    public const ushort OpcodeCancelPicker = 1203;
    public const ushort OpcodeResultNotify = 1204;

    // Patched .datc64 tables overlaid via nativeClient.replaces.
    public const string BaseItemTypesDatPath = "Data/BaseItemTypes.datc64";
    public const string ItemVisualIdentityDatPath = "Data/ItemVisualIdentity.datc64";
    public const string CurrencyItemsDatPath = "Data/CurrencyItems.datc64";
}

/// <summary>
/// Operational mode of the Recombinator-style <c>SiphonPicker.ui</c> modal.
/// </summary>
public enum SiphonPickerMode
{
    /// <summary>Empty Orb of Siphoning: choose an explicit modifier on the source item to steal into a Charged Orb.</summary>
    Steal = 0,

    /// <summary>Charged Orb of Siphoning: choose a compatible explicit modifier on the destination item to replace with the stored modifier.</summary>
    Imbue = 1
}

/// <summary>
/// Self-contained wire DTO describing a stored modifier carried by a Charged Orb of Siphoning.
/// </summary>
public sealed record SiphonStoredAffixDto(
    string ModId,
    string DisplayName,
    string StatText,
    bool IsPrefix,
    bool IsSuffix,
    string Group,
    int Tier,
    IReadOnlyList<int> RolledValues)
{
    public string SlotTag => IsPrefix ? "Prefix" : IsSuffix ? "Suffix" : "Mod";
}

/// <summary>
/// Self-contained wire DTO representing one selectable modifier row inside <c>SiphonPicker.ui</c>.
/// </summary>
public sealed record SiphonAffixOption(
    int Index,
    string ModId,
    string DisplayName,
    string StatText,
    bool IsPrefix,
    bool IsSuffix,
    string Group,
    int Tier,
    IReadOnlyList<int> RolledValues,
    bool Selectable = true,
    string? DisabledReason = null)
{
    public string SlotTag => IsPrefix ? "Prefix" : IsSuffix ? "Suffix" : "Mod";
}

/// <summary>
/// Server -&gt; Client: opens (or refreshes) the native Recombinator-style <c>SiphonPicker.ui</c> modal
/// for either stealing a mod from a source item or imbuing a stored mod onto a target item.
/// </summary>
public sealed record OpenSiphonPicker(
    string SessionToken,
    SiphonPickerMode Mode,
    uint CurrencyInventoryId,
    uint CurrencyItemId,
    uint TargetInventoryId,
    uint TargetItemId,
    string TargetItemName,
    string TargetBaseType,
    string TargetRarity,
    SiphonStoredAffixDto? StoredAffix,
    IReadOnlyList<SiphonAffixOption> Affixes,
    string PromptText);

/// <summary>
/// Client -&gt; Server: player confirmed selection of <paramref name="AffixIndex"/> in the open Siphon Picker modal.
/// </summary>
public sealed record SelectSiphonAffixRequest(
    string SessionToken,
    int AffixIndex);

/// <summary>
/// Client -&gt; Server: player cancelled the open Siphon Picker modal without consuming any orb.
/// </summary>
public sealed record CancelSiphonPicker(
    string SessionToken);

/// <summary>
/// Server -&gt; Client: outcome of a Steal, Imbue, or Cancel operation (closes the picker when <paramref name="ClosePicker"/> is true).
/// </summary>
public sealed record SiphonResultNotification(
    string SessionToken,
    bool Success,
    SiphonPickerMode Mode,
    string Message,
    SiphonStoredAffixDto? ResultingStoredAffix = null,
    uint? CreatedChargedItemId = null,
    bool ClosePicker = true);
