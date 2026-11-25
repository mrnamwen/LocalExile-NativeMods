using DualMod.SiphonOrb.Server;
using LocalExile.Client.ServerBridge;
using Xunit;

namespace LocalExile.Client.SiphonOrb.Tests;

public sealed class CurrencyAndAffixServiceTests
{
    [Fact]
    public void CustomCurrencyDefinition_RejectsOverwritingStockRowsAndInvalidConfigs()
    {
        var baseDef = SiphonServerMod.EmptyOrbDefinition;

        Assert.Throws<ArgumentOutOfRangeException>(() => (baseDef with { BaseItemTypesRowIndex = 5460 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (baseDef with { ItemVisualIdentityRowIndex = 15515 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (baseDef with { CurrencyItemsRowIndex = 1961 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (baseDef with { VisualIdentityHash16 = 0 }).Validate());
        Assert.Throws<ArgumentException>(() => (baseDef with { BaseItemTypeId = "Metadata/Items/Currency/Test.it" }).Validate());
        Assert.Throws<InvalidOperationException>(() => (baseDef with { SupportsStoredMods = true, MaxStackSize = 10 }).Validate());
    }

    [Fact]
    public void CurrencyRegistrar_AppliesToLiveCatalogIdempotently_AndRemovesCleanlyOnDispose()
    {
        var fakeCatalog = new FakeItemCatalog(stockRowCount: 5461);
        var logs = new List<string>();

        using (var registrar = new LocalExileCurrencyRegistrar())
        {
            registrar.Register(SiphonServerMod.EmptyOrbDefinition);
            registrar.Register(SiphonServerMod.ChargedOrbDefinition);

            int first = registrar.ApplyToLiveCatalog(fakeCatalog, logs.Add);
            int second = registrar.ApplyToLiveCatalog(fakeCatalog, logs.Add);

            Assert.Equal(2, first);
            Assert.Equal(0, second); // Idempotent
            Assert.Equal(5463, fakeCatalog.RowsCount);
            Assert.True(fakeCatalog.HasHash(SiphonServerMod.EmptyOrbDefinition.TypeHash));
            Assert.True(fakeCatalog.HasHash(SiphonServerMod.ChargedOrbDefinition.TypeHash));
            Assert.True(fakeCatalog.HasVisual(SiphonServerMod.EmptyOrbDefinition.VisualIdentityId));
            Assert.True(fakeCatalog.HasVisual(SiphonServerMod.ChargedOrbDefinition.VisualIdentityId));
            Assert.Equal("remove_random_mod", fakeCatalog.GetAction(5461));
            Assert.Equal("remove_random_mod", fakeCatalog.GetAction(5462));
        }

        // After Dispose, every injected entry must be cleanly removed leaving stock rows untouched!
        Assert.Equal(5461, fakeCatalog.RowsCount);
        Assert.False(fakeCatalog.HasHash(SiphonServerMod.EmptyOrbDefinition.TypeHash));
        Assert.False(fakeCatalog.HasHash(SiphonServerMod.ChargedOrbDefinition.TypeHash));
        Assert.False(fakeCatalog.HasVisual(SiphonServerMod.EmptyOrbDefinition.VisualIdentityId));
        Assert.Null(fakeCatalog.GetAction(5461));
    }

    [Fact]
    public async Task CurrencyRegistrar_RoutesUseItemOnAndShiftRepeat_OnlyForRegisteredCustomCurrencies()
    {
        using var registrar = new LocalExileCurrencyRegistrar();
        CurrencyUseContext? captured = null;

        registrar.Register(SiphonServerMod.EmptyOrbDefinition, ctx =>
        {
            captured = ctx;
            return Task.FromResult(true);
        });

        const uint stockAnnulHash = 3081331240u; // Metadata/Items/Currency/CurrencyRemoveMod

        // 1. Stock currency C->S 0x0071 must return false so Gateway stock handler runs untouched.
        byte[] stockPkt = LocalExileCurrencyRegistrar.EncodeUseItemOn(1, 50, 1, 99);
        bool stockClaimed = await registrar.HandleUseItemOnPacketAsync(
            "conn-1", "Exile", stockPkt, (_, _) => stockAnnulHash);
        Assert.False(stockClaimed);
        Assert.Null(captured);

        // 2. Registered custom currency C->S 0x0071 must be claimed and routed with exact coordinates.
        byte[] customPkt = LocalExileCurrencyRegistrar.EncodeUseItemOn(1, 77, 1, 88);
        bool customClaimed = await registrar.HandleUseItemOnPacketAsync(
            "conn-1", "Exile", customPkt, (invId, itemId) => itemId == 77 ? SiphonServerMod.EmptyOrbDefinition.TypeHash : null);
        Assert.True(customClaimed);
        Assert.NotNull(captured);
        Assert.Equal(1u, captured!.CurrencyInventoryId);
        Assert.Equal(77u, captured.CurrencyItemId);
        Assert.Equal(1u, captured.TargetInventoryId);
        Assert.Equal(88u, captured.TargetItemId);
        Assert.False(captured.IsShiftRepeat);

        // 3. Stock currency Shift-repeat C->S 0x0072 returns false; custom currency returns true.
        byte[] stockShift = LocalExileCurrencyRegistrar.EncodeUseItemTypeOn(stockAnnulHash, 1, 88);
        Assert.False(await registrar.HandleUseItemTypeOnPacketAsync("conn-1", "Exile", stockShift, _ => (1u, 50u)));

        captured = null;
        byte[] customShift = LocalExileCurrencyRegistrar.EncodeUseItemTypeOn(SiphonServerMod.EmptyOrbDefinition.TypeHash, 1, 88);
        Assert.True(await registrar.HandleUseItemTypeOnPacketAsync("conn-1", "Exile", customShift, _ => (1u, 77u)));
        Assert.NotNull(captured);
        Assert.True(captured!.IsShiftRepeat);
        Assert.Equal(77u, captured.CurrencyItemId);
    }

    [Fact]
    public void ItemAffixService_StealsPreservesRolledValuesEmbedsOnChargedOrbAndReplacesOnSecondItem()
    {
        var sourcePlate = CreateRarePlate(
            itemId: 201,
            name: "Viper Shell",
            mods:
            [
                Mod(ItemModSectionKind.Implicit, "AllResImplicit1", 0x1001, "Implicit", "+12% to all Elemental Resistances", false, false, "ImplicitRes", 1, [12]),
                Mod(ItemModSectionKind.Explicit, "IncreasedLife10", 0x2001, "Prime", "+107 to maximum Life", true, false, "IncreasedLife", 1, [107]),
                Mod(ItemModSectionKind.Explicit, "LocalAddedPhysicalDamage8", 0x2002, "Flaring", "Adds 24 to 47 Physical Damage", true, false, "PhysicalDamage", 1, [24, 47]),
                Mod(ItemModSectionKind.Explicit, "FireResistance7", 0x2003, "of Tzteosh", "+46% to Fire Resistance", false, true, "FireResistance", 1, [46]),
            ]);

        // Steal multi-value mod (Adds 24 to 47 Physical Damage) at explicit index 1
        AffixStealResult steal = LocalExileItemAffixService.StealAffix(sourcePlate, affixIndex: 1, SiphonServerMod.ChargedOrbDefinition, newChargedItemId: 500);
        Assert.True(steal.Success);
        Assert.NotNull(steal.StolenAffix);
        Assert.Equal("LocalAddedPhysicalDamage8", steal.StolenAffix!.ModId);
        Assert.Equal([24, 47], steal.StolenAffix.RolledValues);

        // Source item keeps implicit + remaining 2 explicits untouched
        Assert.Equal(2, steal.UpdatedSourceView!.Affixes.Count);
        Assert.Single(steal.UpdatedSourceItem!.Mods, m => m.Section == ItemModSectionKind.Implicit);

        // Charged Orb stores exact stolen mod in Enchant section with StackSize = 1 and Rarity = Normal
        PortableItem chargedOrb = steal.ChargedCurrencyItem!;
        Assert.Equal(SiphonServerMod.ChargedOrbDefinition.TypeHash, chargedOrb.TypeHash);
        Assert.Equal(ItemRarityKind.Normal, chargedOrb.Rarity);
        Assert.Equal((ushort)1, chargedOrb.StackSize);
        Assert.Single(chargedOrb.Mods);
        Assert.Equal(ItemModSectionKind.Enchant, chargedOrb.Mods[0].Section);

        StoredAffixPayload? readBack = LocalExileItemAffixService.ReadStoredAffix(chargedOrb);
        Assert.NotNull(readBack);
        Assert.Equal("LocalAddedPhysicalDamage8", readBack!.ModId);
        Assert.Equal([24, 47], readBack.RolledValues);

        // Replace an affix on a second Rare item and confirm exact rolled values [24, 47] are preserved
        var targetItem = CreateRarePlate(
            itemId: 202,
            name: "Gloom Mantle",
            mods:
            [
                Mod(ItemModSectionKind.Explicit, "IncreasedLife3", 0x2010, "Hale", "+32 to maximum Life", true, false, "IncreasedLife", 8, [32]),
                Mod(ItemModSectionKind.Explicit, "ColdResistance4", 0x2011, "of the Yeti", "+29% to Cold Resistance", false, true, "ColdResistance", 4, [29]),
            ]);

        AffixReplaceResult imbue = LocalExileItemAffixService.ReplaceAffix(targetItem, replaceIndex: 0, readBack);
        Assert.True(imbue.Success);
        Assert.Equal("IncreasedLife3", imbue.ReplacedAffix!.ModId);
        Assert.Equal(2, imbue.UpdatedTargetView!.Affixes.Count);
        Assert.Equal("LocalAddedPhysicalDamage8", imbue.UpdatedTargetView.Affixes[0].ModId);
        Assert.Equal([24, 47], imbue.UpdatedTargetView.Affixes[0].RolledValues);
        Assert.Equal("ColdResistance4", imbue.UpdatedTargetView.Affixes[1].ModId);
    }

    [Fact]
    public void ItemAffixService_EnforcesPrefixSuffixCapsModGroupsFractureAndEdgeCases()
    {
        var storedPrefixLife = new StoredAffixPayload(
            ModId: "IncreasedLife10",
            RolledValues: [105],
            DisplayText: "+105 to maximum Life",
            IsPrefix: true,
            Group: "IncreasedLife",
            ModHash16: 0x2001,
            DisplayName: "Prime",
            IsSuffix: false,
            Tier: 1);

        // 1. Full 3P/3S Rare: Prefix CANNOT replace Suffix (would make 4P/2S), CAN replace Prefix
        var fullRare = CreateRarePlate(301, "Full Plate",
        [
            Mod(ItemModSectionKind.Explicit, "Armour1", 0x3001, "P1", "+100 Armour", true, false, "Armour", 1, [100]),
            Mod(ItemModSectionKind.Explicit, "Evasion1", 0x3002, "P2", "+100 Evasion", true, false, "Evasion", 1, [100]),
            Mod(ItemModSectionKind.Explicit, "EnergyShield1", 0x3003, "P3", "+50 ES", true, false, "ES", 1, [50]),
            Mod(ItemModSectionKind.Explicit, "FireRes1", 0x3004, "S1", "+40% Fire Res", false, true, "FireRes", 1, [40]),
            Mod(ItemModSectionKind.Explicit, "ColdRes1", 0x3005, "S2", "+40% Cold Res", false, true, "ColdRes", 1, [40]),
            Mod(ItemModSectionKind.Explicit, "LightRes1", 0x3006, "S3", "+40% Light Res", false, true, "LightRes", 1, [40]),
        ]);

        Assert.False(LocalExileItemAffixService.ReplaceAffix(fullRare, replaceIndex: 3, storedPrefixLife).Success); // Suffix -> 4P rejected!
        Assert.True(LocalExileItemAffixService.ReplaceAffix(fullRare, replaceIndex: 0, storedPrefixLife).Success);  // Prefix -> 3P allowed!

        // 2. Duplicate Mod Group check: replacing non-Life mod when Life is already on another slot fails; replacing the Life mod itself succeeds!
        var hasLifeRare = CreateRarePlate(302, "Life Plate",
        [
            Mod(ItemModSectionKind.Explicit, "IncreasedLife2", 0x3010, "Healthy", "+22 to maximum Life", true, false, "IncreasedLife", 9, [22]),
            Mod(ItemModSectionKind.Explicit, "FireRes1", 0x3004, "S1", "+40% Fire Res", false, true, "FireRes", 1, [40]),
        ]);

        var dupGroupResult = LocalExileItemAffixService.ReplaceAffix(hasLifeRare, replaceIndex: 1, storedPrefixLife);
        Assert.False(dupGroupResult.Success);
        Assert.Contains("group", dupGroupResult.Error!, StringComparison.OrdinalIgnoreCase);

        var upgradeSameGroup = LocalExileItemAffixService.ReplaceAffix(hasLifeRare, replaceIndex: 0, storedPrefixLife);
        Assert.True(upgradeSameGroup.Success);
        Assert.Equal([105], upgradeSameGroup.UpdatedTargetView!.Affixes[0].RolledValues);

        // 3. Edge cases: corrupted, mirrored, unidentified, 0-mod, normal, unique, fractured
        var corrupted = hasLifeRare with { Corrupted = true };
        Assert.False(LocalExileItemAffixService.StealAffix(corrupted, 0).Success);
        Assert.False(LocalExileItemAffixService.ReplaceAffix(corrupted, 0, storedPrefixLife).Success);

        var mirrored = hasLifeRare with { Mirrored = true };
        Assert.False(LocalExileItemAffixService.StealAffix(mirrored, 0).Success);

        var unid = hasLifeRare with { Identified = false };
        Assert.False(LocalExileItemAffixService.StealAffix(unid, 0).Success);

        var zeroMod = hasLifeRare with { Mods = [] };
        Assert.False(LocalExileItemAffixService.StealAffix(zeroMod, 0).Success);

        var normalItem = hasLifeRare with { Rarity = ItemRarityKind.Normal };
        Assert.False(LocalExileItemAffixService.StealAffix(normalItem, 0).Success);

        var uniqueItem = hasLifeRare with { Rarity = ItemRarityKind.Unique };
        Assert.False(LocalExileItemAffixService.StealAffix(uniqueItem, 0).Success);

        var fracturedFirst = hasLifeRare with { FracturedCount = 1 };
        Assert.False(LocalExileItemAffixService.StealAffix(fracturedFirst, 0).Success);
        Assert.True(LocalExileItemAffixService.StealAffix(fracturedFirst, 1).Success);
        Assert.False(LocalExileItemAffixService.ReplaceAffix(fracturedFirst, 0, storedPrefixLife).Success);
    }

    [Fact]
    public void StatDescriptionFormatter_FormatsConditionsHandlersAndPlaceholders()
    {
        Assert.Equal("+84 to maximum Life", LocalExileItemAffixService.FormatStatTemplate("{0:+d} to maximum Life", [84]));
        Assert.Equal("Adds 19 to 37 Fire Damage", LocalExileItemAffixService.FormatStatTemplate("Adds {0} to {1} Fire Damage", [19, 37]));
        Assert.Equal("1.5% reduced Mana Cost", LocalExileItemAffixService.FormatStatTemplate("{0}% reduced Mana Cost", [-150], [("negate", 1), ("divide_by_one_hundred", 1)]));
        Assert.Equal("2.5 Life Regenerated per second", LocalExileItemAffixService.FormatStatTemplate("{0} Life Regenerated per second", [150], [("per_minute_to_per_second", 1)]));

        Assert.True(LocalExileItemAffixService.MatchesStatCondition("#", -5));
        Assert.True(LocalExileItemAffixService.MatchesStatCondition("1|#", 1));
        Assert.False(LocalExileItemAffixService.MatchesStatCondition("1|#", 0));
        Assert.True(LocalExileItemAffixService.MatchesStatCondition("#|-1", -1));
        Assert.True(LocalExileItemAffixService.MatchesStatCondition("!0", 7));
        Assert.False(LocalExileItemAffixService.MatchesStatCondition("!0", 0));
    }

    private static PortableItem CreateRarePlate(uint itemId, string name, IReadOnlyList<PortableItemMod> mods) =>
        new(
            ItemId: itemId,
            InventoryId: 1,
            TypeHash: 0x11223344u,
            ItemName: name,
            BaseType: "Astral Plate",
            ItemClass: "Body Armour",
            Rarity: ItemRarityKind.Rare,
            ItemLevel: 84,
            Identified: true,
            Corrupted: false,
            Mirrored: false,
            FracturedCount: 0,
            StackSize: 1,
            Mods: mods);

    private static PortableItemMod Mod(
        ItemModSectionKind section, string modId, ushort hash, string name, string text,
        bool isPrefix, bool isSuffix, string group, int tier, IReadOnlyList<int> values) =>
        new(section, modId, hash, name, text, isPrefix, isSuffix, group, tier, values);

    // Mirrors LocalExile Gateway's private ItemCatalog fields for reflection contract verification.
    private sealed class FakeItemCatalog
    {
        public sealed record BaseType(
            int Row, string Id, string Name, uint Hash, byte Width, byte Height, ushort VisualIdentity,
            IReadOnlyList<string> Components, int MaxCharges, string Class = "", byte Stance = 0xff,
            int DropLevel = 0, ushort MaxStack = 1, int FullStackRow = -1, IReadOnlyList<object>? Implicits = null)
        {
            public int ModDomain { get; init; } = 1;
            public int ClassRow { get; init; } = -1;
            public byte GroundSize { get; init; } = 1;
            public bool Unmodifiable { get; init; }
            public bool CanBeFractured { get; init; }
            public bool CanBeMirrored { get; init; }
            public bool CanBeCorrupted { get; init; }
        }

        private readonly (List<BaseType> Rows, string? Error) _rows;
        private readonly Dictionary<uint, BaseType> _byHash = [];
        private readonly Dictionary<string, BaseType> _byId = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ushort> _visualsById = new(StringComparer.Ordinal);
        private readonly (Dictionary<int, string> Actions, Dictionary<int, byte> Quality, int QualityRows, byte[] Catalysts) _currencyUses;

        public FakeItemCatalog(int stockRowCount)
        {
            var list = new List<BaseType>(stockRowCount);
            for (int i = 0; i < stockRowCount; i++)
                list.Add(new BaseType(i, $"Stock/{i}", $"Stock {i}", (uint)(i + 1), 1, 1, 1, ["Base"], 0));

            _rows = (list, null);
            _currencyUses = ([], [], 0, []);
        }

        public int RowsCount => _rows.Rows.Count;
        public bool HasHash(uint hash) => _byHash.ContainsKey(hash);
        public bool HasVisual(string id) => _visualsById.ContainsKey(id);
        public string? GetAction(int row) => _currencyUses.Actions.GetValueOrDefault(row);
    }
}
