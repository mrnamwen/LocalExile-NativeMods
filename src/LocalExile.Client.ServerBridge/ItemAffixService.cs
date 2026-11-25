using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace LocalExile.Client.ServerBridge;

/// <summary>
/// Path of Exile item rarity mirrored numerically with LocalExile's <c>GameServer.Core.Protocol.Instance.ItemRarity</c>.
/// </summary>
public enum ItemRarityKind : byte
{
    /// <summary>Normal (white) item.</summary>
    Normal = 0,

    /// <summary>Magic (blue) item (up to 1 Prefix and 1 Suffix).</summary>
    Magic = 1,

    /// <summary>Rare (yellow) item (up to 3 Prefixes and 3 Suffixes, or 2/2 on Jewels).</summary>
    Rare = 2,

    /// <summary>Unique (brown/orange) item.</summary>
    Unique = 3
}

/// <summary>
/// Mod list section mirrored numerically with LocalExile's <c>GameServer.Core.Protocol.Instance.ModSection</c>.
/// </summary>
public enum ItemModSectionKind : byte
{
    /// <summary>Implicit modifier section (flag bit 4).</summary>
    Implicit = 4,

    /// <summary>Explicit modifier section (flag bit 5, identified items only).</summary>
    Explicit = 5,

    /// <summary>Enchantment modifier section (flag bit 6, rendered prominently above implicits).</summary>
    Enchant = 6,

    /// <summary>Scourge modifier section (flag bit 10).</summary>
    Scourge = 10,

    /// <summary>Hidden modifier section (flag bit 11).</summary>
    Hidden = 11
}

/// <summary>
/// Read-only snapshot of one explicit affix on an inspected item, including its exact rolled values
/// and formatted Path of Exile stat description text.
/// </summary>
/// <param name="Index">0-based index among the item's explicit affixes.</param>
/// <param name="ModId"><c>Mods.dat</c> string identifier (for example <c>IncreasedLife8</c>).</param>
/// <param name="DisplayName">Human-readable affix name from <c>Mods.dat</c> (for example <c>Vigorous</c>).</param>
/// <param name="StatText">Exact formatted PoE stat line (for example <c>+84 to maximum Life</c>).</param>
/// <param name="IsPrefix"><c>true</c> if <c>GenerationType == 1</c> (Prefix).</param>
/// <param name="IsSuffix"><c>true</c> if <c>GenerationType == 2</c> (Suffix).</param>
/// <param name="Group">Mod family/group exclusivity key (two affixes sharing a non-empty group cannot coexist on one item).</param>
/// <param name="Tier">1-based affix tier when known, or <c>0</c> if unknown.</param>
/// <param name="RolledValues">Exact integer values rolled for each stat of this mod, in <c>StatsKey1..8</c> order.</param>
/// <param name="ModHash16"><c>Mods.dat</c> 16-bit <c>HASH16</c> sent on the wire.</param>
/// <param name="IsFractured"><c>true</c> if this affix is fractured and therefore locked against removal or replacement.</param>
/// <param name="IsCrafted"><c>true</c> if this affix is a crafting bench modifier (<c>Domain == 9</c>).</param>
public sealed record ItemAffixSnapshot(
    int Index,
    string ModId,
    string DisplayName,
    string StatText,
    bool IsPrefix,
    bool IsSuffix,
    string Group,
    int Tier,
    IReadOnlyList<int> RolledValues,
    ushort ModHash16 = 0,
    bool IsFractured = false,
    bool IsCrafted = false)
{
    /// <summary>Short human-readable slot tag (<c>Prefix</c>, <c>Suffix</c>, or <c>Mod</c>).</summary>
    public string SlotTag => IsPrefix ? "Prefix" : IsSuffix ? "Suffix" : "Mod";
}

/// <summary>
/// Inspected summary of a target item and all of its explicit affixes.
/// </summary>
/// <param name="ItemId">Inventory item identifier.</param>
/// <param name="ItemName">Item display name (rare name + base type, magic name, or base type name).</param>
/// <param name="BaseType">Base item type name (for example <c>Astral Plate</c> or <c>Hubris Circlet</c>).</param>
/// <param name="Rarity">Item rarity.</param>
/// <param name="Affixes">All explicit affixes currently on the item in display order.</param>
/// <param name="InventoryId">Inventory ID containing the item.</param>
/// <param name="ItemClass"><c>ItemClasses.Id</c> (for example <c>Body Armour</c>, <c>Jewel</c>).</param>
/// <param name="ItemLevel">Item level (<c>1..100</c>).</param>
/// <param name="IsCorrupted"><c>true</c> when the item is corrupted.</param>
/// <param name="IsMirrored"><c>true</c> when the item is mirrored.</param>
/// <param name="IsIdentified"><c>true</c> when the item is identified.</param>
/// <param name="MaxPrefixes">Maximum allowed explicit prefixes on this item.</param>
/// <param name="MaxSuffixes">Maximum allowed explicit suffixes on this item.</param>
public sealed record ItemAffixView(
    uint ItemId,
    string ItemName,
    string BaseType,
    ItemRarityKind Rarity,
    IReadOnlyList<ItemAffixSnapshot> Affixes,
    uint InventoryId = 1,
    string ItemClass = "",
    int ItemLevel = 1,
    bool IsCorrupted = false,
    bool IsMirrored = false,
    bool IsIdentified = true,
    int MaxPrefixes = 3,
    int MaxSuffixes = 3);

/// <summary>
/// Persistent representation of an affix stored inside a Charged Currency item (such as Charged Orb of Siphoning).
/// Maps 1:1 to a single <c>ItemMod</c> serialized in the currency item's <c>Mods</c> component and persisted
/// in LocalExile's <c>characters.db</c> <c>item_mods</c> table.
/// </summary>
/// <param name="ModId"><c>Mods.dat</c> string identifier.</param>
/// <param name="RolledValues">Exact rolled stat values preserved across steal and imbue.</param>
/// <param name="DisplayText">Formatted PoE stat description text shown in UI and tooltips.</param>
/// <param name="IsPrefix"><c>true</c> if Prefix.</param>
/// <param name="Group">Mod family/group exclusivity key.</param>
/// <param name="ModHash16"><c>Mods.dat</c> 16-bit wire hash.</param>
/// <param name="DisplayName">Human-readable mod name (for example <c>Vigorous</c>).</param>
/// <param name="IsSuffix"><c>true</c> if Suffix.</param>
/// <param name="Tier">1-based tier when known, or <c>0</c>.</param>
/// <param name="IsCrafted"><c>true</c> if crafted (<c>Domain == 9</c>).</param>
public sealed record StoredAffixPayload(
    string ModId,
    IReadOnlyList<int> RolledValues,
    string DisplayText,
    bool IsPrefix,
    string Group,
    ushort ModHash16 = 0,
    string DisplayName = "",
    bool IsSuffix = false,
    int Tier = 0,
    bool IsCrafted = false)
{
    /// <summary>Short human-readable slot tag (<c>Prefix</c>, <c>Suffix</c>, or <c>Mod</c>).</summary>
    public string SlotTag => IsPrefix ? "Prefix" : IsSuffix ? "Suffix" : "Mod";
}

/// <summary>
/// Portable item modifier model used by <see cref="LocalExileItemAffixService"/> in standalone hosts and tests.
/// </summary>
public sealed record PortableItemMod(
    ItemModSectionKind Section,
    string ModId,
    ushort ModHash16,
    string DisplayName,
    string StatText,
    bool IsPrefix,
    bool IsSuffix,
    string Group,
    int Tier,
    IReadOnlyList<int> RolledValues,
    bool IsCrafted = false);

/// <summary>
/// Portable item model representing a gear or currency item for deterministic server/E2E manipulation.
/// </summary>
public sealed record PortableItem(
    uint ItemId,
    uint InventoryId,
    uint TypeHash,
    string ItemName,
    string BaseType,
    string ItemClass,
    ItemRarityKind Rarity,
    int ItemLevel,
    bool Identified,
    bool Corrupted,
    bool Mirrored,
    byte FracturedCount,
    ushort StackSize,
    IReadOnlyList<PortableItemMod> Mods,
    IReadOnlyList<string>? Components = null,
    ushort VisualIdentity = 0)
{
    /// <summary>Effective serialized components on this item.</summary>
    public IReadOnlyList<string> EffectiveComponents =>
        Components is { Count: > 0 } ? Components : ["Base", "Mods"];
}

/// <summary>
/// Result of stealing an affix from a source item.
/// </summary>
public sealed record AffixStealResult(
    bool Success,
    string? Error,
    StoredAffixPayload? StolenAffix,
    ItemAffixView? UpdatedSourceView,
    PortableItem? UpdatedSourceItem = null,
    PortableItem? ChargedCurrencyItem = null)
{
    /// <summary>Creates a failed steal result with <paramref name="error"/>.</summary>
    public static AffixStealResult Fail(string error) => new(false, error, null, null);
}

/// <summary>
/// Result of replacing an affix on a target item with a stored affix.
/// </summary>
public sealed record AffixReplaceResult(
    bool Success,
    string? Error,
    ItemAffixSnapshot? ReplacedAffix,
    StoredAffixPayload? AppliedAffix,
    ItemAffixView? UpdatedTargetView,
    PortableItem? UpdatedTargetItem = null)
{
    /// <summary>Creates a failed replace result with <paramref name="error"/>.</summary>
    public static AffixReplaceResult Fail(string error) => new(false, error, null, null, null);
}

/// <summary>
/// Reusable service for inspecting item affixes with formatted Path of Exile stat descriptions,
/// validating and stealing a chosen affix, embedding that affix persistently on a Charged Currency item,
/// and replacing a compatible affix on a second item while preserving exact rolled values.
/// Supports both standalone <see cref="PortableItem"/> models and live LocalExile Gateway <c>Item</c> records.
/// </summary>
public sealed class LocalExileItemAffixService
{
    private static readonly Regex PlaceholderRegex = new(@"\{(\d+)(?::([^}]+))?\}", RegexOptions.Compiled);
    private static readonly Regex TierSuffixRegex = new(@"(\d+)$", RegexOptions.Compiled);
    private readonly object? _defaultStatDescriptions;

    /// <summary>
    /// Creates an affix service with an optional default LocalExile <c>StatDescriptions</c> table
    /// used when formatting live Gateway item modifiers.
    /// </summary>
    public LocalExileItemAffixService(object? defaultStatDescriptions = null)
    {
        _defaultStatDescriptions = defaultStatDescriptions;
    }

    /// <summary>
    /// Computes the maximum allowed explicit prefixes and suffixes for an item's rarity and class.
    /// </summary>
    public static (int MaxPrefixes, int MaxSuffixes) GetAffixCaps(ItemRarityKind rarity, string itemClass)
    {
        if (rarity == ItemRarityKind.Magic)
            return (1, 1);

        if (rarity == ItemRarityKind.Rare)
        {
            bool smallClass = itemClass is "Jewel" or "AbyssJewel" or "AtlasRelic";
            return smallClass ? (2, 2) : (3, 3);
        }

        return (0, 0);
    }

    /// <summary>
    /// Inspects a <see cref="PortableItem"/> and returns its explicit affixes with formatted stat descriptions.
    /// </summary>
    public static ItemAffixView Inspect(PortableItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var (maxP, maxS) = GetAffixCaps(item.Rarity, item.ItemClass);
        var affixes = new List<ItemAffixSnapshot>();
        int explicitIdx = 0;

        foreach (PortableItemMod mod in item.Mods)
        {
            if (mod.Section != ItemModSectionKind.Explicit)
                continue;

            bool isFractured = explicitIdx < item.FracturedCount;
            affixes.Add(new ItemAffixSnapshot(
                Index: explicitIdx,
                ModId: mod.ModId,
                DisplayName: mod.DisplayName,
                StatText: mod.StatText,
                IsPrefix: mod.IsPrefix,
                IsSuffix: mod.IsSuffix,
                Group: mod.Group,
                Tier: mod.Tier,
                RolledValues: [.. mod.RolledValues],
                ModHash16: mod.ModHash16,
                IsFractured: isFractured,
                IsCrafted: mod.IsCrafted));
            explicitIdx++;
        }

        return new ItemAffixView(
            ItemId: item.ItemId,
            ItemName: item.ItemName,
            BaseType: item.BaseType,
            Rarity: item.Rarity,
            Affixes: affixes,
            InventoryId: item.InventoryId,
            ItemClass: item.ItemClass,
            ItemLevel: item.ItemLevel,
            IsCorrupted: item.Corrupted,
            IsMirrored: item.Mirrored,
            IsIdentified: item.Identified,
            MaxPrefixes: maxP,
            MaxSuffixes: maxS);
    }

    /// <summary>
    /// Validates whether an item is eligible for affix siphoning (stealing an explicit modifier).
    /// </summary>
    public static string? ValidateStealTarget(ItemAffixView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        if (view.IsCorrupted)
            return "Target item is corrupted and cannot be modified.";
        if (view.IsMirrored)
            return "Target item is mirrored and cannot be modified.";
        if (!view.IsIdentified)
            return "Target item must be identified before siphoning modifiers.";
        if (view.Rarity is not (ItemRarityKind.Magic or ItemRarityKind.Rare))
            return "Only Magic or Rare items can be siphoned.";
        if (view.Affixes.Count == 0)
            return "Target item has no explicit modifiers to siphon.";
        if (view.Affixes.All(a => a.IsFractured))
            return "All explicit modifiers on this item are fractured and cannot be siphoned.";

        return null;
    }

    /// <summary>
    /// Checks whether replacing the explicit affix at <paramref name="replaceIndex"/> on <paramref name="targetView"/>
    /// with <paramref name="storedAffix"/> satisfies Prefix/Suffix caps, fracture protection, and mod-group exclusivity.
    /// </summary>
    public static (bool Allowed, string? Reason) CanReplaceAffix(
        ItemAffixView targetView,
        int replaceIndex,
        StoredAffixPayload storedAffix)
    {
        ArgumentNullException.ThrowIfNull(targetView);
        ArgumentNullException.ThrowIfNull(storedAffix);

        if (targetView.IsCorrupted)
            return (false, "Target item is corrupted.");
        if (targetView.IsMirrored)
            return (false, "Target item is mirrored.");
        if (!targetView.IsIdentified)
            return (false, "Target item must be identified.");
        if (targetView.Rarity is not (ItemRarityKind.Magic or ItemRarityKind.Rare))
            return (false, "Only Magic or Rare items can be imbued.");
        if (replaceIndex < 0 || replaceIndex >= targetView.Affixes.Count)
            return (false, "Selected modifier index is out of range.");

        ItemAffixSnapshot targetAffix = targetView.Affixes[replaceIndex];
        if (targetAffix.IsFractured)
            return (false, "Fractured modifiers cannot be replaced.");

        // Evaluate resulting prefix/suffix counts after removing targetAffix and adding storedAffix.
        int prefixesAfter = 0;
        int suffixesAfter = 0;

        for (int i = 0; i < targetView.Affixes.Count; i++)
        {
            if (i == replaceIndex)
                continue;

            ItemAffixSnapshot remaining = targetView.Affixes[i];
            if (remaining.IsPrefix) prefixesAfter++;
            if (remaining.IsSuffix) suffixesAfter++;

            if (!string.IsNullOrWhiteSpace(storedAffix.Group) &&
                string.Equals(remaining.Group, storedAffix.Group, StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"Item already has another modifier in group '{storedAffix.Group}'.");
            }

            if (string.Equals(remaining.ModId, storedAffix.ModId, StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"Item already has modifier '{storedAffix.ModId}'.");
            }
        }

        if (storedAffix.IsPrefix) prefixesAfter++;
        if (storedAffix.IsSuffix) suffixesAfter++;

        if (prefixesAfter > targetView.MaxPrefixes)
            return (false, $"Cannot replace a Suffix with a Prefix when Prefixes are already full ({targetView.MaxPrefixes}/{targetView.MaxPrefixes}).");
        if (suffixesAfter > targetView.MaxSuffixes)
            return (false, $"Cannot replace a Prefix with a Suffix when Suffixes are already full ({targetView.MaxSuffixes}/{targetView.MaxSuffixes}).");

        return (true, null);
    }

    /// <summary>
    /// Steals the explicit affix at <paramref name="affixIndex"/> from <paramref name="source"/> and optionally embeds it
    /// onto a new <see cref="PortableItem"/> representing <paramref name="chargedCurrencyDef"/>.
    /// </summary>
    public static AffixStealResult StealAffix(
        PortableItem source,
        int affixIndex,
        CustomCurrencyItemDefinition? chargedCurrencyDef = null,
        uint newChargedItemId = 0)
    {
        ArgumentNullException.ThrowIfNull(source);

        ItemAffixView view = Inspect(source);
        if (ValidateStealTarget(view) is { } targetError)
            return AffixStealResult.Fail(targetError);

        if (affixIndex < 0 || affixIndex >= view.Affixes.Count)
            return AffixStealResult.Fail($"Affix index {affixIndex} is out of range (0..{view.Affixes.Count - 1}).");

        ItemAffixSnapshot chosen = view.Affixes[affixIndex];
        if (chosen.IsFractured)
            return AffixStealResult.Fail("Fractured modifiers cannot be siphoned.");

        // Remove the exact matching explicit mod while keeping implicits, enchants, and other explicits intact.
        var updatedMods = new List<PortableItemMod>(source.Mods.Count - 1);
        int seenExplicit = 0;
        PortableItemMod? removedRaw = null;
        foreach (PortableItemMod mod in source.Mods)
        {
            if (mod.Section == ItemModSectionKind.Explicit)
            {
                if (seenExplicit == affixIndex)
                {
                    removedRaw = mod;
                    seenExplicit++;
                    continue;
                }

                seenExplicit++;
            }

            updatedMods.Add(mod);
        }

        if (removedRaw is null)
            return AffixStealResult.Fail("Selected modifier could not be located on the item.");

        PortableItem updatedSource = source with { Mods = updatedMods };
        var payload = new StoredAffixPayload(
            ModId: chosen.ModId,
            RolledValues: [.. chosen.RolledValues],
            DisplayText: chosen.StatText,
            IsPrefix: chosen.IsPrefix,
            Group: chosen.Group,
            ModHash16: chosen.ModHash16,
            DisplayName: chosen.DisplayName,
            IsSuffix: chosen.IsSuffix,
            Tier: chosen.Tier,
            IsCrafted: chosen.IsCrafted);

        PortableItem? chargedItem = chargedCurrencyDef is not null
            ? CreateChargedCurrencyItem(chargedCurrencyDef, payload, newChargedItemId, source.InventoryId)
            : null;

        return new AffixStealResult(
            Success: true,
            Error: null,
            StolenAffix: payload,
            UpdatedSourceView: Inspect(updatedSource),
            UpdatedSourceItem: updatedSource,
            ChargedCurrencyItem: chargedItem);
    }

    /// <summary>
    /// Creates a persistent <see cref="PortableItem"/> representing a Charged Currency item carrying
    /// <paramref name="storedAffix"/> inside its <see cref="ItemModSectionKind.Enchant"/> mod list.
    /// </summary>
    public static PortableItem CreateChargedCurrencyItem(
        CustomCurrencyItemDefinition chargedCurrencyDef,
        StoredAffixPayload storedAffix,
        uint itemId,
        uint inventoryId = 1)
    {
        ArgumentNullException.ThrowIfNull(chargedCurrencyDef);
        ArgumentNullException.ThrowIfNull(storedAffix);

        var embeddedMod = new PortableItemMod(
            Section: ItemModSectionKind.Enchant,
            ModId: storedAffix.ModId,
            ModHash16: storedAffix.ModHash16,
            DisplayName: storedAffix.DisplayName,
            StatText: storedAffix.DisplayText,
            IsPrefix: storedAffix.IsPrefix,
            IsSuffix: storedAffix.IsSuffix,
            Group: storedAffix.Group,
            Tier: storedAffix.Tier,
            RolledValues: [.. storedAffix.RolledValues],
            IsCrafted: storedAffix.IsCrafted);

        return new PortableItem(
            ItemId: itemId,
            InventoryId: inventoryId,
            TypeHash: chargedCurrencyDef.TypeHash,
            ItemName: chargedCurrencyDef.DisplayName,
            BaseType: chargedCurrencyDef.DisplayName,
            ItemClass: chargedCurrencyDef.ItemClassName,
            Rarity: ItemRarityKind.Normal,
            ItemLevel: 1,
            Identified: true,
            Corrupted: false,
            Mirrored: false,
            FracturedCount: 0,
            StackSize: 1,
            Mods: [embeddedMod],
            Components: chargedCurrencyDef.Components,
            VisualIdentity: chargedCurrencyDef.VisualIdentityHash16);
    }

    /// <summary>Reads the <see cref="StoredAffixPayload"/> stored inside a Charged Currency <see cref="PortableItem"/>.</summary>
    public static StoredAffixPayload? ReadStoredAffix(PortableItem chargedCurrencyItem)
    {
        ArgumentNullException.ThrowIfNull(chargedCurrencyItem);

        PortableItemMod? mod =
            chargedCurrencyItem.Mods.FirstOrDefault(m => m.Section == ItemModSectionKind.Enchant) ??
            chargedCurrencyItem.Mods.FirstOrDefault(m => m.Section == ItemModSectionKind.Implicit) ??
            chargedCurrencyItem.Mods.FirstOrDefault(m => m.Section == ItemModSectionKind.Explicit);

        if (mod is null)
            return null;

        return new StoredAffixPayload(
            ModId: mod.ModId,
            RolledValues: [.. mod.RolledValues],
            DisplayText: mod.StatText,
            IsPrefix: mod.IsPrefix,
            Group: mod.Group,
            ModHash16: mod.ModHash16,
            DisplayName: mod.DisplayName,
            IsSuffix: mod.IsSuffix,
            Tier: mod.Tier,
            IsCrafted: mod.IsCrafted);
    }

    /// <summary>
    /// Replaces the explicit affix at <paramref name="replaceIndex"/> on <paramref name="target"/> with
    /// <paramref name="storedAffix"/>, preserving its exact rolled values and enforcing Prefix/Suffix and group rules.
    /// </summary>
    public static AffixReplaceResult ReplaceAffix(
        PortableItem target,
        int replaceIndex,
        StoredAffixPayload storedAffix)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(storedAffix);

        ItemAffixView view = Inspect(target);
        (bool allowed, string? reason) = CanReplaceAffix(view, replaceIndex, storedAffix);
        if (!allowed)
            return AffixReplaceResult.Fail(reason ?? "Selected modifier cannot be replaced.");

        ItemAffixSnapshot replaced = view.Affixes[replaceIndex];
        var replacementMod = new PortableItemMod(
            Section: ItemModSectionKind.Explicit,
            ModId: storedAffix.ModId,
            ModHash16: storedAffix.ModHash16,
            DisplayName: storedAffix.DisplayName,
            StatText: storedAffix.DisplayText,
            IsPrefix: storedAffix.IsPrefix,
            IsSuffix: storedAffix.IsSuffix,
            Group: storedAffix.Group,
            Tier: storedAffix.Tier,
            RolledValues: [.. storedAffix.RolledValues],
            IsCrafted: storedAffix.IsCrafted);

        var updatedMods = new List<PortableItemMod>(target.Mods.Count);
        int seenExplicit = 0;
        bool didReplace = false;
        foreach (PortableItemMod mod in target.Mods)
        {
            if (mod.Section == ItemModSectionKind.Explicit)
            {
                if (seenExplicit == replaceIndex)
                {
                    updatedMods.Add(replacementMod);
                    didReplace = true;
                    seenExplicit++;
                    continue;
                }

                seenExplicit++;
            }

            updatedMods.Add(mod);
        }

        if (!didReplace)
            return AffixReplaceResult.Fail("Target modifier index could not be located on the item.");

        PortableItem updatedTarget = target with { Mods = updatedMods };
        return new AffixReplaceResult(
            Success: true,
            Error: null,
            ReplacedAffix: replaced,
            AppliedAffix: storedAffix,
            UpdatedTargetView: Inspect(updatedTarget),
            UpdatedTargetItem: updatedTarget);
    }

    // -------------------------------------------------------------------------
    // Live LocalExile Gateway Item / ItemCatalog / ModCatalog / StatDescriptions bridge
    // -------------------------------------------------------------------------

    /// <summary>
    /// Inspects a live LocalExile Gateway <c>GameServer.Core.Protocol.Instance.Item</c> using the live
    /// <c>ItemCatalog</c> (and optional <c>StatDescriptions</c>) via reflection so <c>ServerBridge</c>
    /// stays decoupled from Gateway binaries while exposing exact live mod metadata and formatted PoE stat lines.
    /// </summary>
    public ItemAffixView InspectGatewayItem(
        uint inventoryId,
        uint itemId,
        object gatewayItem,
        object itemCatalog,
        object? statDescriptions = null)
    {
        ArgumentNullException.ThrowIfNull(gatewayItem);
        ArgumentNullException.ThrowIfNull(itemCatalog);

        Type itemType = gatewayItem.GetType();
        uint typeHash = GetProperty<uint>(itemType, gatewayItem, "TypeHash");
        byte rarityRaw = Convert.ToByte(GetProperty<object>(itemType, gatewayItem, "Rarity"), CultureInfo.InvariantCulture);
        var rarity = (ItemRarityKind)rarityRaw;
        int itemLevel = GetProperty<byte>(itemType, gatewayItem, "ItemLevel");
        bool identified = GetProperty<bool>(itemType, gatewayItem, "Identified");
        bool corrupted = GetProperty<bool>(itemType, gatewayItem, "Corrupted");
        bool mirrored = GetProperty<bool>(itemType, gatewayItem, "Mirrored");
        byte fracturedCount = GetProperty<byte>(itemType, gatewayItem, "FracturedCount");

        object? baseTypeObj = itemCatalog.GetType().GetMethod("ByHash", [typeof(uint)])?.Invoke(itemCatalog, [typeHash]);
        string baseName = baseTypeObj is not null ? GetProperty<string>(baseTypeObj.GetType(), baseTypeObj, "Name") ?? "Unknown Item" : $"Item 0x{typeHash:X8}";
        string itemClass = baseTypeObj is not null ? GetProperty<string>(baseTypeObj.GetType(), baseTypeObj, "Class") ?? "" : "";

        object? modCatalog = itemCatalog.GetType().GetProperty("Mods")?.GetValue(itemCatalog);
        var (maxP, maxS) = GetAffixCaps(rarity, itemClass);
        var affixes = new List<ItemAffixSnapshot>();

        if (GetProperty<IEnumerable>(itemType, gatewayItem, "Mods") is { } modsSeq)
        {
            int explicitIdx = 0;
            foreach (object? itemMod in modsSeq)
            {
                if (itemMod is null)
                    continue;

                Type imType = itemMod.GetType();
                byte sectionRaw = Convert.ToByte(GetProperty<object>(imType, itemMod, "Section"), CultureInfo.InvariantCulture);
                if (sectionRaw != (byte)ItemModSectionKind.Explicit)
                    continue;

                ushort modHash = GetProperty<ushort>(imType, itemMod, "Hash");
                int[] values = ExtractIntList(GetProperty<IEnumerable>(imType, itemMod, "Values"));

                (string modId, string displayName, string statText, bool isPrefix, bool isSuffix, string group, int tier, bool isCrafted) =
                    DescribeGatewayMod(modCatalog, statDescriptions ?? _defaultStatDescriptions, modHash, values);

                affixes.Add(new ItemAffixSnapshot(
                    Index: explicitIdx,
                    ModId: modId,
                    DisplayName: displayName,
                    StatText: statText,
                    IsPrefix: isPrefix,
                    IsSuffix: isSuffix,
                    Group: group,
                    Tier: tier,
                    RolledValues: values,
                    ModHash16: modHash,
                    IsFractured: explicitIdx < fracturedCount,
                    IsCrafted: isCrafted));
                explicitIdx++;
            }
        }

        string displayItemName = rarity == ItemRarityKind.Magic && affixes.Count > 0
            ? BuildMagicDisplayName(baseName, affixes)
            : baseName;

        return new ItemAffixView(
            ItemId: itemId,
            ItemName: displayItemName,
            BaseType: baseName,
            Rarity: rarity,
            Affixes: affixes,
            InventoryId: inventoryId,
            ItemClass: itemClass,
            ItemLevel: itemLevel,
            IsCorrupted: corrupted,
            IsMirrored: mirrored,
            IsIdentified: identified,
            MaxPrefixes: maxP,
            MaxSuffixes: maxS);
    }

    /// <summary>
    /// Reads a <see cref="StoredAffixPayload"/> stored on a live LocalExile Gateway Charged Currency <c>Item</c>
    /// (searching <c>ModSection.Enchant</c>, then <c>Implicit</c>, then <c>Explicit</c>).
    /// </summary>
    public StoredAffixPayload? ReadStoredAffixFromGatewayItem(
        object gatewayCurrencyItem,
        object itemCatalog,
        object? statDescriptions = null)
    {
        ArgumentNullException.ThrowIfNull(gatewayCurrencyItem);
        ArgumentNullException.ThrowIfNull(itemCatalog);

        Type itemType = gatewayCurrencyItem.GetType();
        if (GetProperty<IEnumerable>(itemType, gatewayCurrencyItem, "Mods") is not { } modsSeq)
            return null;

        object? chosen = null;
        int bestPriority = int.MaxValue;

        foreach (object? itemMod in modsSeq)
        {
            if (itemMod is null)
                continue;

            byte section = Convert.ToByte(GetProperty<object>(itemMod.GetType(), itemMod, "Section"), CultureInfo.InvariantCulture);
            int priority = section switch
            {
                (byte)ItemModSectionKind.Enchant => 0,
                (byte)ItemModSectionKind.Implicit => 1,
                (byte)ItemModSectionKind.Explicit => 2,
                _ => 99
            };

            if (priority < bestPriority)
            {
                bestPriority = priority;
                chosen = itemMod;
            }
        }

        if (chosen is null || bestPriority >= 99)
            return null;

        Type imType = chosen.GetType();
        ushort modHash = GetProperty<ushort>(imType, chosen, "Hash");
        int[] values = ExtractIntList(GetProperty<IEnumerable>(imType, chosen, "Values"));
        object? modCatalog = itemCatalog.GetType().GetProperty("Mods")?.GetValue(itemCatalog);

        (string modId, string displayName, string statText, bool isPrefix, bool isSuffix, string group, int tier, bool isCrafted) =
            DescribeGatewayMod(modCatalog, statDescriptions ?? _defaultStatDescriptions, modHash, values);

        return new StoredAffixPayload(
            ModId: modId,
            RolledValues: values,
            DisplayText: statText,
            IsPrefix: isPrefix,
            Group: group,
            ModHash16: modHash,
            DisplayName: displayName,
            IsSuffix: isSuffix,
            Tier: tier,
            IsCrafted: isCrafted);
    }

    /// <summary>
    /// Formats a PoE stat description template string (from <c>stat_descriptions.txt</c>) with concrete rolled values
    /// and standard PoE stat handlers (<c>negate</c>, <c>divide_by_one_hundred</c>, <c>divide_by_ten</c>,
    /// <c>per_minute_to_per_second</c>, <c>milliseconds_to_seconds</c>, etc.).
    /// </summary>
    public static string FormatStatTemplate(
        string template,
        IReadOnlyList<int> values,
        IReadOnlyList<(string Name, int Stat)>? handlers = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);

        double[] effective = new double[values.Count];
        bool[] isFloat = new bool[values.Count];
        for (int i = 0; i < values.Count; i++)
            effective[i] = values[i];

        if (handlers is not null)
        {
            foreach (var (name, statIndex1Based) in handlers)
            {
                int idx = statIndex1Based - 1;
                if (idx < 0 || idx >= effective.Length)
                    continue;

                switch (name)
                {
                    case "negate":
                        effective[idx] = -effective[idx];
                        break;
                    case "divide_by_ten":
                    case "divide_by_ten_0dp":
                    case "divide_by_ten_1dp_if_required":
                        effective[idx] /= 10.0;
                        isFloat[idx] = true;
                        break;
                    case "divide_by_one_hundred":
                    case "divide_by_one_hundred_2dp":
                    case "divide_by_one_hundred_2dp_if_required":
                    case "divide_by_one_hundred_and_negate":
                        effective[idx] = name.EndsWith("negate", StringComparison.Ordinal)
                            ? -effective[idx] / 100.0
                            : effective[idx] / 100.0;
                        isFloat[idx] = true;
                        break;
                    case "per_minute_to_per_second":
                    case "per_minute_to_per_second_0dp":
                    case "per_minute_to_per_second_1dp":
                    case "per_minute_to_per_second_2dp":
                    case "per_minute_to_per_second_2dp_if_required":
                        effective[idx] /= 60.0;
                        isFloat[idx] = true;
                        break;
                    case "milliseconds_to_seconds":
                    case "milliseconds_to_seconds_0dp":
                    case "milliseconds_to_seconds_1dp":
                    case "milliseconds_to_seconds_2dp":
                    case "milliseconds_to_seconds_2dp_if_required":
                        effective[idx] /= 1000.0;
                        isFloat[idx] = true;
                        break;
                    case "times_twenty":
                        effective[idx] *= 20.0;
                        break;
                    case "double":
                        effective[idx] *= 2.0;
                        break;
                }
            }
        }

        string formatted = PlaceholderRegex.Replace(template, match =>
        {
            if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx) ||
                idx < 0 || idx >= effective.Length)
            {
                return match.Value;
            }

            string spec = match.Groups[2].Success ? match.Groups[2].Value : "";
            double v = effective[idx];
            bool signed = spec.Contains('+', StringComparison.Ordinal);

            if (isFloat[idx] && Math.Abs(v - Math.Round(v)) > 0.0005)
            {
                string sign = signed && v >= 0 ? "+" : "";
                return sign + v.ToString("0.##", CultureInfo.InvariantCulture);
            }

            long rounded = (long)Math.Round(v);
            if (signed && rounded >= 0)
                return "+" + rounded.ToString(CultureInfo.InvariantCulture);

            return rounded.ToString(CultureInfo.InvariantCulture);
        });

        return formatted.Replace("\\n", " / ", StringComparison.Ordinal).Trim();
    }

    /// <summary>Evaluates a PoE <c>stat_descriptions.txt</c> condition token (<c>#</c>, <c>1|#</c>, <c>#|-1</c>, <c>5</c>, <c>!0</c>).</summary>
    public static bool MatchesStatCondition(string condition, int value)
    {
        if (string.IsNullOrEmpty(condition) || condition == "#")
            return true;

        if (condition.StartsWith('!'))
            return int.TryParse(condition[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int excluded) && value != excluded;

        int bar = condition.IndexOf('|');
        if (bar < 0)
            return int.TryParse(condition, NumberStyles.Integer, CultureInfo.InvariantCulture, out int exact) && value == exact;

        string lo = condition[..bar];
        string hi = condition[(bar + 1)..];
        bool loOk = lo == "#" || (int.TryParse(lo, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minVal) && value >= minVal);
        bool hiOk = hi == "#" || (int.TryParse(hi, NumberStyles.Integer, CultureInfo.InvariantCulture, out int maxVal) && value <= maxVal);
        return loOk && hiOk;
    }

    private static (string ModId, string DisplayName, string StatText, bool IsPrefix, bool IsSuffix, string Group, int Tier, bool IsCrafted)
        DescribeGatewayMod(object? modCatalog, object? statDescriptions, ushort modHash, int[] values)
    {
        if (modCatalog is null)
        {
            return ($"0x{modHash:X4}", $"Mod 0x{modHash:X4}", $"Modifier 0x{modHash:X4} ({string.Join(", ", values)})",
                false, false, "", 0, false);
        }

        object? mod = modCatalog.GetType().GetMethod("ByHash", [typeof(ushort)])?.Invoke(modCatalog, [modHash]);
        if (mod is null)
        {
            return ($"0x{modHash:X4}", $"Mod 0x{modHash:X4}", $"Modifier 0x{modHash:X4} ({string.Join(", ", values)})",
                false, false, "", 0, false);
        }

        Type mt = mod.GetType();
        string modId = GetProperty<string>(mt, mod, "Id") ?? $"0x{modHash:X4}";
        string displayName = GetProperty<string>(mt, mod, "Name") ?? "";
        int genType = GetProperty<int>(mt, mod, "GenerationType");
        int domain = GetProperty<int>(mt, mod, "Domain");
        bool isPrefix = genType == 1;
        bool isSuffix = genType == 2;
        bool isCrafted = domain == 9;

        int[] families = ExtractIntList(GetProperty<IEnumerable>(mt, mod, "Families"));
        string group = families.Length > 0
            ? "Family:" + string.Join(",", families.OrderBy(f => f))
            : GetProperty<string>(mt, mod, "ModType") ?? modId;

        int tier = ParseTrailingTier(modId);

        var statPairs = new List<(string StatName, int Value)>();
        if (GetProperty<IEnumerable>(mt, mod, "Stats") is { } statsSeq)
        {
            int idx = 0;
            foreach (object? statObj in statsSeq)
            {
                if (statObj is null)
                    continue;

                string statName = GetProperty<string>(statObj.GetType(), statObj, "Name") ?? $"stat_{idx}";
                int val = idx < values.Length ? values[idx] : 0;
                statPairs.Add((statName, val));
                idx++;
            }
        }

        string statText = FormatGatewayStats(statDescriptions, statPairs);
        if (string.IsNullOrWhiteSpace(statText))
            statText = !string.IsNullOrWhiteSpace(displayName) ? displayName : modId;

        return (modId, displayName, statText, isPrefix, isSuffix, group, tier, isCrafted);
    }

    private static string FormatGatewayStats(object? statDescriptions, List<(string StatName, int Value)> stats)
    {
        if (stats.Count == 0)
            return string.Empty;

        var lines = new List<string>();
        var consumed = new bool[stats.Count];
        MethodInfo? forMethod = statDescriptions?.GetType().GetMethod("For", [typeof(string)]);

        for (int i = 0; i < stats.Count; i++)
        {
            if (consumed[i])
                continue;

            object? desc = forMethod?.Invoke(statDescriptions, [stats[i].StatName]);
            if (desc is not null && TryFormatDescription(desc, stats, consumed, out string? line) && !string.IsNullOrWhiteSpace(line))
            {
                lines.Add(line);
                continue;
            }

            consumed[i] = true;
            lines.Add(HumanizeFallbackStat(stats[i].StatName, stats[i].Value));
        }

        return string.Join(" / ", lines);
    }

    private static bool TryFormatDescription(
        object desc,
        List<(string StatName, int Value)> stats,
        bool[] consumed,
        out string? formatted)
    {
        formatted = null;
        Type dt = desc.GetType();
        if (GetProperty<IEnumerable>(dt, desc, "Stats") is not { } descStatsSeq ||
            GetProperty<IEnumerable>(dt, desc, "Variants") is not { } variantsSeq)
        {
            return false;
        }

        var descStatNames = new List<string>();
        foreach (object? s in descStatsSeq)
        {
            if (s is string name)
                descStatNames.Add(name);
        }

        if (descStatNames.Count == 0)
            return false;

        int[] matchedValues = new int[descStatNames.Count];
        var matchedIndices = new List<int>();

        for (int d = 0; d < descStatNames.Count; d++)
        {
            for (int s = 0; s < stats.Count; s++)
            {
                if (!consumed[s] && string.Equals(stats[s].StatName, descStatNames[d], StringComparison.Ordinal))
                {
                    matchedValues[d] = stats[s].Value;
                    matchedIndices.Add(s);
                    break;
                }
            }
        }

        if (matchedIndices.Count == 0)
            return false;

        foreach (object? variant in variantsSeq)
        {
            if (variant is null)
                continue;

            Type vt = variant.GetType();
            var conditions = new List<string>();
            if (GetProperty<IEnumerable>(vt, variant, "Conditions") is { } condSeq)
            {
                foreach (object? c in condSeq)
                    conditions.Add(c?.ToString() ?? "#");
            }

            bool allMatch = true;
            for (int d = 0; d < matchedValues.Length && d < conditions.Count; d++)
            {
                if (!MatchesStatCondition(conditions[d], matchedValues[d]))
                {
                    allMatch = false;
                    break;
                }
            }

            if (!allMatch)
                continue;

            string text = GetProperty<string>(vt, variant, "Text") ?? "";
            var handlers = new List<(string Name, int Stat)>();
            if (GetProperty<IEnumerable>(vt, variant, "Handlers") is { } handlerSeq)
            {
                foreach (object? h in handlerSeq)
                {
                    if (h is ValueTuple<string, int> vtup)
                        handlers.Add(vtup);
                }
            }

            formatted = FormatStatTemplate(text, matchedValues, handlers);
            foreach (int idx in matchedIndices)
                consumed[idx] = true;
            return true;
        }

        return false;
    }

    private static string HumanizeFallbackStat(string statName, int value)
    {
        string clean = statName.Replace("_+%", "%", StringComparison.Ordinal)
            .Replace("_+", "", StringComparison.Ordinal)
            .Replace('_', ' ');
        string sign = value >= 0 ? "+" : "";
        return $"{sign}{value} {clean}".Trim();
    }

    private static string BuildMagicDisplayName(string baseName, IReadOnlyList<ItemAffixSnapshot> affixes)
    {
        string? prefix = affixes.FirstOrDefault(a => a.IsPrefix && !string.IsNullOrWhiteSpace(a.DisplayName))?.DisplayName;
        string? suffix = affixes.FirstOrDefault(a => a.IsSuffix && !string.IsNullOrWhiteSpace(a.DisplayName))?.DisplayName;

        string name = baseName;
        if (!string.IsNullOrWhiteSpace(prefix))
            name = $"{prefix} {name}";
        if (!string.IsNullOrWhiteSpace(suffix))
            name = $"{name} {suffix}";
        return name;
    }

    private static int ParseTrailingTier(string modId)
    {
        Match match = TierSuffixRegex.Match(modId);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            ? n
            : 0;
    }

    private static int[] ExtractIntList(IEnumerable? seq)
    {
        if (seq is null)
            return [];

        var list = new List<int>();
        foreach (object? item in seq)
        {
            if (item is not null)
                list.Add(Convert.ToInt32(item, CultureInfo.InvariantCulture));
        }

        return [.. list];
    }

    private static T GetProperty<T>(Type type, object instance, string propertyName)
    {
        object? val = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance);
        return val is T typed ? typed : default!;
    }
}
