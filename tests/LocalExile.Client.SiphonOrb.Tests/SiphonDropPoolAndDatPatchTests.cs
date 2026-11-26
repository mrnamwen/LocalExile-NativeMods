using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using DualMod.SiphonOrb.Server;
using DualMod.SiphonOrb.Shared;
using LocalExile.Client.AssetPipeline.Dat64;
using LocalExile.Client.Sdk;
using LocalExile.Client.ServerBridge;
using LocalExile.Client.Waypoint;
using Xunit;

namespace LocalExile.Client.SiphonOrb.Tests;

public sealed class SiphonDropPoolAndDatPatchTests
{
    [Fact]
    public void SiphonDropSettings_ParsesSettingsJsonDefaultsOverridesAndRejectsInvalidConfigs()
    {
        string repoRoot = FindRepoRoot();
        string settingsPath = Path.Combine(repoRoot, "examples", "DualMod.SiphonOrb", "settings.json");
        Assert.True(File.Exists(settingsPath), $"Missing settings.json at {settingsPath}");

        // 1. Default schema parse
        SiphonDropSettings defaults = SiphonDropSettings.LoadFromFile(settingsPath);
        Assert.True(defaults.EnableNaturalDrops);
        Assert.Equal(25.0, defaults.DropWeight);
        Assert.Equal(45, defaults.MinAreaLevel);
        Assert.Equal(0, defaults.MaxAreaLevel);
        Assert.Equal(SiphonDropSource.All, defaults.ResolveSources());
        Assert.Equal(SiphonMonsterRarity.Any, defaults.ResolveMonsterRarity());

        // 2. User override overlay (e.g., data/mods/siphon.json)
        string schemaJson = File.ReadAllText(settingsPath);
        SiphonDropSettings overridden = SiphonDropSettings.ParseSettingsSchema(
            schemaJson,
            """
            {
              "enableNaturalDrops": true,
              "dropWeight": 42.5,
              "minAreaLevel": 68,
              "maxAreaLevel": 85,
              "sources": ["MonsterKill", "BossBonus", "MapCurrency"],
              "monsterRarity": ["Rare", "Unique", "Boss"]
            }
            """);
        Assert.True(overridden.EnableNaturalDrops);
        Assert.Equal(42.5, overridden.DropWeight);
        Assert.Equal(68, overridden.MinAreaLevel);
        Assert.Equal(85, overridden.MaxAreaLevel);
        Assert.Equal(
            SiphonDropSource.MonsterKill | SiphonDropSource.BossBonus | SiphonDropSource.MapCurrency,
            overridden.ResolveSources());
        Assert.Equal(
            SiphonMonsterRarity.Rare | SiphonMonsterRarity.Unique | SiphonMonsterRarity.Boss,
            overridden.ResolveMonsterRarity());

        // 3. Disabled natural drops parses cleanly
        SiphonDropSettings disabled = SiphonDropSettings.ParseSettingsSchema(
            schemaJson,
            """{ "enableNaturalDrops": false }""");
        Assert.False(disabled.EnableNaturalDrops);

        // 4. Validation rejects invalid weights, level bounds, and unknown/empty filters
        Assert.Throws<ArgumentOutOfRangeException>(() => (defaults with { DropWeight = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (defaults with { DropWeight = double.NaN }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (defaults with { MinAreaLevel = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (defaults with { MinAreaLevel = 101 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (defaults with { MaxAreaLevel = -1 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (defaults with { MinAreaLevel = 60, MaxAreaLevel = 50 }).Validate());
        Assert.Throws<ArgumentException>(() => (defaults with { Sources = [] }).Validate());
        Assert.Throws<ArgumentException>(() => (defaults with { Sources = ["InvalidSource"] }).Validate());
        Assert.Throws<ArgumentException>(() => (defaults with { MonsterRarity = [] }).Validate());
        Assert.Throws<ArgumentException>(() => (defaults with { MonsterRarity = ["Mythic"] }).Validate());
    }

    [Fact]
    public void SiphonOrbDatPatch_ParsesAndMergesCleanly_IncludingMultiModCoexistenceAndLive3293dBundles()
    {
        string repoRoot = FindRepoRoot();
        string patchPath = Path.Combine(repoRoot, "examples", "DualMod.SiphonOrb", "client", "patches", "siphon-orb.datpatch.json");
        Assert.True(File.Exists(patchPath), $"Missing declarative datpatch at {patchPath}");

        // Manifest remains valid with 0 errors and 0 warnings
        var manifest = DualModManifest.LoadFromFile(Path.Combine(repoRoot, "examples", "DualMod.SiphonOrb", "mod.json"));
        var report = DualModPackageValidator.ValidateManifest(manifest, SemVer.Parse(SdkVersion.Current));
        Assert.True(report.IsValid, string.Join(Environment.NewLine, report.Errors.Select(e => $"{e.Code}: {e.Message}")));
        Assert.Empty(report.Warnings);

        // 1. Parse siphon-orb.datpatch.json
        DatPatchDocument siphonPatch = DatPatchDocument.LoadFromFile(patchPath);
        Assert.Equal(DatPatchDocument.CurrentSchemaVersion, siphonPatch.SchemaVersion);
        Assert.Equal(SiphonProtocol.ModId, siphonPatch.ModId);
        Assert.True(siphonPatch.Tables.ContainsKey("ItemVisualIdentity"));
        Assert.True(siphonPatch.Tables.ContainsKey("BaseItemTypes"));
        Assert.True(siphonPatch.Tables.ContainsKey("CurrencyItems"));
        Assert.Equal(2, siphonPatch.Tables["ItemVisualIdentity"].Append.Count);
        Assert.Equal(2, siphonPatch.Tables["BaseItemTypes"].Append.Count);
        Assert.Equal(2, siphonPatch.Tables["CurrencyItems"].Append.Count);

        // 2. Merge siphon-orb.datpatch.json with a second independent currency mod touching the exact same 3 tables
        InMemoryDat64Source stock = BuildSyntheticStockCurrencyTables();
        DatPatchDocument companionPatch = DatPatchDocument.Parse("""
        {
          "schemaVersion": 1,
          "modId": "companion-orb",
          "tables": {
            "ItemVisualIdentity": {
              "append": [
                {
                  "key": "companion_orb_visual",
                  "cloneFromRow": 0,
                  "fields": {
                    "Id": "CompanionOrbVisual",
                    "DDSFile": "Art/2DItems/Currency/Mods/Companion/CompanionOrb.dds",
                    "AOFile": "Metadata/Items/Currency/CurrencyOrb.ao"
                  }
                }
              ]
            },
            "BaseItemTypes": {
              "append": [
                {
                  "key": "companion_orb",
                  "cloneFromId": "Metadata/Items/Currency/CurrencyRemoveMod",
                  "fields": {
                    "Id": "Metadata/Items/Currency/Mods/Companion/CompanionOrb",
                    "Name": "Orb of Companionship",
                    "ItemVisualIdentity": "$ref:ItemVisualIdentity:companion_orb_visual",
                    "TagsKeys": []
                  }
                }
              ]
            },
            "CurrencyItems": {
              "append": [
                {
                  "key": "companion_orb_currency",
                  "cloneFromId": "Metadata/Items/Currency/CurrencyRemoveMod",
                  "fields": {
                    "BaseItemTypesKey": "$ref:BaseItemTypes:companion_orb",
                    "StackSize": 20,
                    "Description": "Companion test currency"
                  }
                }
              ]
            }
          }
        }
        """);

        DatPatchMergeResult mergedA = DatPatchMerger.Merge(stock, [siphonPatch, companionPatch]);
        DatPatchMergeResult mergedB = DatPatchMerger.Merge(stock, [companionPatch, siphonPatch]);

        // Deterministic order-independent SHA-256 digest!
        Assert.Equal(mergedA.RowMap.Digest, mergedB.RowMap.Digest);
        Assert.Equal(["companion-orb", "siphon"], mergedA.RowMap.Mods);

        Dat64Table bit = Dat64Table.Parse(mergedA.MergedTables["BaseItemTypes"]);
        Dat64Table ivi = Dat64Table.Parse(mergedA.MergedTables["ItemVisualIdentity"]);
        Dat64Table cur = Dat64Table.Parse(mergedA.MergedTables["CurrencyItems"]);

        Assert.Equal(4, bit.RowCount); // 1 stock + 2 siphon + 1 companion
        Assert.Equal(4, ivi.RowCount);
        Assert.Equal(4, cur.RowCount);

        ContentRowMapEntry emptyBit = mergedA.RowMap.Tables["BaseItemTypes"].Entries["siphon_orb_empty"];
        ContentRowMapEntry chargedBit = mergedA.RowMap.Tables["BaseItemTypes"].Entries["siphon_orb_charged"];
        ContentRowMapEntry emptyIvi = mergedA.RowMap.Tables["ItemVisualIdentity"].Entries["siphon_orb_empty_visual"];
        ContentRowMapEntry chargedIvi = mergedA.RowMap.Tables["ItemVisualIdentity"].Entries["siphon_orb_charged_visual"];
        ContentRowMapEntry emptyCur = mergedA.RowMap.Tables["CurrencyItems"].Entries["siphon_orb_empty_currency"];
        ContentRowMapEntry chargedCur = mergedA.RowMap.Tables["CurrencyItems"].Entries["siphon_orb_charged_currency"];

        // Verify exact hashes, cross-table $ref keys, and currency metadata
        Assert.Equal(SiphonProtocol.EmptyOrbTypeHash, emptyBit.Hash32);
        Assert.Equal(SiphonProtocol.ChargedOrbTypeHash, chargedBit.Hash32);
        Assert.Equal(SiphonProtocol.EmptyOrbTypeHash, bit.ReadUInt32(emptyBit.RowIndex, CurrencyDatPatcher.BitHash32Offset));
        Assert.Equal(SiphonProtocol.ChargedOrbTypeHash, bit.ReadUInt32(chargedBit.RowIndex, CurrencyDatPatcher.BitHash32Offset));
        Assert.Equal(SiphonProtocol.EmptyOrbDisplayName, bit.ReadString(emptyBit.RowIndex, CurrencyDatPatcher.BitNameOffset));
        Assert.Equal(SiphonProtocol.ChargedOrbDisplayName, bit.ReadString(chargedBit.RowIndex, CurrencyDatPatcher.BitNameOffset));
        Assert.Equal((ulong)emptyIvi.RowIndex, bit.ReadForeignRow(emptyBit.RowIndex, CurrencyDatPatcher.BitItemVisualIdentityOffset));
        Assert.Equal((ulong)chargedIvi.RowIndex, bit.ReadForeignRow(chargedBit.RowIndex, CurrencyDatPatcher.BitItemVisualIdentityOffset));

        Assert.Equal(SiphonProtocol.EmptyOrbVisualHash16, emptyIvi.Hash16);
        Assert.Equal(SiphonProtocol.ChargedOrbVisualHash16, chargedIvi.Hash16);
        Assert.Equal(SiphonProtocol.EmptyOrbIconPath, ivi.ReadString(emptyIvi.RowIndex, CurrencyDatPatcher.IviDdsFileOffset));
        Assert.Equal(SiphonProtocol.ChargedOrbIconPath, ivi.ReadString(chargedIvi.RowIndex, CurrencyDatPatcher.IviDdsFileOffset));

        Assert.Equal((ulong)emptyBit.RowIndex, cur.ReadForeignRow(emptyCur.RowIndex, CurrencyDatPatcher.CurBaseItemTypesKeyOffset));
        Assert.Equal((ulong)chargedBit.RowIndex, cur.ReadForeignRow(chargedCur.RowIndex, CurrencyDatPatcher.CurBaseItemTypesKeyOffset));
        Assert.Equal(20, cur.ReadInt32(emptyCur.RowIndex, CurrencyDatPatcher.CurStackSizeOffset));
        Assert.Equal(1, cur.ReadInt32(chargedCur.RowIndex, CurrencyDatPatcher.CurStackSizeOffset));
        Assert.Equal(10000, cur.ReadInt32(emptyCur.RowIndex, CurrencyDatPatcher.CurCurrencyTabStackSizeOffset));
        Assert.Equal(0, cur.ReadInt32(chargedCur.RowIndex, CurrencyDatPatcher.CurCurrencyTabStackSizeOffset));
        Assert.Equal("remove_random_mod", cur.ReadString(emptyCur.RowIndex, CurrencyDatPatcher.CurActionOffset));
        Assert.Equal("remove_random_mod", cur.ReadString(chargedCur.RowIndex, CurrencyDatPatcher.CurActionOffset));

        // 3. Optional live 3.29.3d Bundles2 smoke merge if LocalExile client checkout is present
        const string liveClientDir = "/home/nam/Git/LocalExile/Client";
        if (File.Exists(Path.Combine(liveClientDir, "Bundles2", "_.index.bin")))
        {
            using var liveSource = new DirectoryDat64Source(liveClientDir);
            DatPatchMergeResult liveMerged = DatPatchMerger.Merge(liveSource, [siphonPatch]);

            Assert.Equal(SiphonProtocol.EmptyOrbBaseItemTypesRow, liveMerged.RowMap.Tables["BaseItemTypes"].Entries["siphon_orb_empty"].RowIndex);
            Assert.Equal(SiphonProtocol.ChargedOrbBaseItemTypesRow, liveMerged.RowMap.Tables["BaseItemTypes"].Entries["siphon_orb_charged"].RowIndex);
            Assert.Equal(SiphonProtocol.EmptyOrbVisualRow, liveMerged.RowMap.Tables["ItemVisualIdentity"].Entries["siphon_orb_empty_visual"].RowIndex);
            Assert.Equal(SiphonProtocol.ChargedOrbVisualRow, liveMerged.RowMap.Tables["ItemVisualIdentity"].Entries["siphon_orb_charged_visual"].RowIndex);
            Assert.Equal(SiphonProtocol.EmptyOrbCurrencyRow, liveMerged.RowMap.Tables["CurrencyItems"].Entries["siphon_orb_empty_currency"].RowIndex);
            Assert.Equal(SiphonProtocol.ChargedOrbCurrencyRow, liveMerged.RowMap.Tables["CurrencyItems"].Entries["siphon_orb_charged_currency"].RowIndex);
            Assert.Equal(SiphonProtocol.EmptyOrbTypeHash, liveMerged.RowMap.Tables["BaseItemTypes"].Entries["siphon_orb_empty"].Hash32);
            Assert.Equal(SiphonProtocol.ChargedOrbTypeHash, liveMerged.RowMap.Tables["BaseItemTypes"].Entries["siphon_orb_charged"].Hash32);
            Assert.Equal(SiphonProtocol.EmptyOrbVisualHash16, liveMerged.RowMap.Tables["ItemVisualIdentity"].Entries["siphon_orb_empty_visual"].Hash16);
            Assert.Equal(SiphonProtocol.ChargedOrbVisualHash16, liveMerged.RowMap.Tables["ItemVisualIdentity"].Entries["siphon_orb_charged_visual"].Hash16);
        }
    }

    [Fact]
    public void SiphonLocalExileEntry_CompilesViaRoslyn_AndExecutesLivePart2bCatalogAndDropPoolLifecycle()
    {
        string? gatewayBin = FindLocalExileGatewayBinDir();
        string? gameDll = FindLocalExileGameDll();
        if (gatewayBin is null || gameDll is null)
            return;

        string repoRoot = FindRepoRoot();
        string entryPath = Path.Combine(repoRoot, "examples", "DualMod.SiphonOrb", "Server", "SiphonLocalExileEntry.cs");
        string settingsPath = Path.Combine(repoRoot, "examples", "DualMod.SiphonOrb", "settings.json");
        string source = File.ReadAllText(entryPath);

        // Enforce Part 2b public API contract: ZERO private-field reflection, ZERO SQLite mutation!
        Assert.DoesNotContain("BindingFlags.NonPublic", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyToLiveCatalog", source, StringComparison.Ordinal);
        Assert.Contains("ItemCatalog.RegisterOverlay", source, StringComparison.Ordinal);
        Assert.Contains("DropDataProvider.Default.RegisterOverlay", source, StringComparison.Ordinal);

        // 1. Live Roslyn compilation against GameServer.Gateway.dll + game.dll (0 errors, 0 warnings!)
        byte[] compiledPe = RoslynEntryCompileHelper.CompileToBytesAndAssertSuccess(
            gatewayBin,
            "SiphonLocalExileEntryLiveDropsProbe",
            source,
            failOnWarnings: true,
            gameDll,
            typeof(SdkVersion).Assembly.Location,
            typeof(ClientBridgeHost).Assembly.Location,
            typeof(SiphonProtocol).Assembly.Location,
            typeof(SiphonServerMod).Assembly.Location);

        string dataDir = "/home/nam/Git/LocalExile/data";
        string? prevClientDb = Environment.GetEnvironmentVariable("Game_CLIENT_DB");
        string? prevWorldDb = Environment.GetEnvironmentVariable("Game_WORLD_DB");
        string? prevSqlDir = Environment.GetEnvironmentVariable("Game_SQL_DIR");
        Environment.SetEnvironmentVariable("Game_CLIENT_DB", Path.Combine(dataDir, "client.db"));
        Environment.SetEnvironmentVariable("Game_WORLD_DB", Path.Combine(dataDir, "world.db"));
        Environment.SetEnvironmentVariable("Game_SQL_DIR", "/home/nam/Git/LocalExile/sql");

        var alc = new LiveGatewayProbeAlc(Path.Combine(gatewayBin, "GameServer.Gateway.dll"));
        try
        {
            Assembly coreAsm = alc.LoadFromAssemblyPath(Path.Combine(gatewayBin, "GameServer.Core.dll"));
            Assembly gatewayAsm = alc.LoadFromAssemblyPath(Path.Combine(gatewayBin, "GameServer.Gateway.dll"));
            Assembly entryAsm = alc.LoadFromStream(new MemoryStream(compiledPe));

            Type entryType = entryAsm.GetType("SiphonLocalExileEntry", throwOnError: true)!;
            Type programType = gatewayAsm.GetType("Program", throwOnError: true)!;
            Type statCatalogType = gatewayAsm.GetType("StatCatalog", throwOnError: true)!;
            Type itemCatalogType = gatewayAsm.GetType("ItemCatalog", throwOnError: true)!;
            Type itemDropsType = gatewayAsm.GetType("ItemDrops", throwOnError: true)!;
            Type dropDataProviderType = gatewayAsm.GetType("DropDataProvider", throwOnError: true)!;
            Type dropPoolOverlaysType = gatewayAsm.GetType("DropPoolOverlays", throwOnError: true)!;
            Type dropContextType = gatewayAsm.GetType("DropContext", throwOnError: true)!;
            Type dropSourceMaskType = gatewayAsm.GetType("DropSourceMask", throwOnError: true)!;
            Type dropMonsterFilterType = gatewayAsm.GetType("DropMonsterFilter", throwOnError: true)!;
            Type dropPoolCategoryType = gatewayAsm.GetType("DropPoolCategory", throwOnError: true)!;
            Type gameEventOwnersType = gatewayAsm.GetType("GameEventOwners", throwOnError: true)!;
            Type modSettingsType = gatewayAsm.GetType("ModSettings", throwOnError: true)!;
            Type monsterRarityType = coreAsm.GetType("GameServer.Core.Protocol.Instance.MonsterRarity", throwOnError: true)!;

            // 2. Verify real LocalExile ModSettings.Parse + Merge + Bound<SiphonDropSettings> on settings.json!
            string settingsJson = File.ReadAllText(settingsPath);
            object parsedSchema = modSettingsType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, [settingsJson, null])!;
            var settingsLogs = new List<string>();
            object mergedDefaults = modSettingsType.GetMethod("Merge", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, [parsedSchema, null, (Action<string>)settingsLogs.Add, null])!;
            Assert.Empty(settingsLogs);
            MethodInfo boundGeneric = modSettingsType.GetMethod("Bound", BindingFlags.Public | BindingFlags.Static)!
                .MakeGenericMethod(typeof(SiphonDropSettings));
            SiphonDropSettings boundDefaults = (SiphonDropSettings)boundGeneric.Invoke(null, [mergedDefaults])!;
            boundDefaults.Validate();
            Assert.True(boundDefaults.EnableNaturalDrops);
            Assert.Equal(25.0, boundDefaults.DropWeight);
            Assert.Equal(45, boundDefaults.MinAreaLevel);
            Assert.Equal(0, boundDefaults.MaxAreaLevel);
            Assert.Equal(SiphonDropSource.All, boundDefaults.ResolveSources());
            Assert.Equal(SiphonMonsterRarity.Any, boundDefaults.ResolveMonsterRarity());

            // 3. Boot real ItemCatalog + ItemDrops BEFORE registering overlays to prove lazy pool rebuild!
            object statCatalog = Activator.CreateInstance(statCatalogType)!;
            SetStaticMember(programType, "Stats", statCatalog);
            gatewayAsm.GetType("ClusterJewels", throwOnError: true)!.GetMethod("LoadGameData")!.Invoke(null, null);

            ConstructorInfo catalogCtor = itemCatalogType.GetConstructors()[0];
            object itemCatalog = InvokeWithDefaults(catalogCtor, dataDir);
            SetStaticMember(programType, "Items", itemCatalog);

            ConstructorInfo itemDropsCtor = itemDropsType.GetConstructors()[0];
            object itemDrops = InvokeWithDefaults(itemDropsCtor, itemCatalog, null, dataDir);
            PropertyInfo poolRevisionProp = itemDropsType.GetProperty("PoolRevision")!;
            long initialPoolRevision = (long)poolRevisionProp.GetValue(itemDrops)!;

            const string owner = "siphon-live-drop-test";
            int moduleRank = (int)gameEventOwnersType.GetField("ModuleRank", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
            MethodInfo releaseOwner = gameEventOwnersType.GetMethod("Release", BindingFlags.Public | BindingFlags.Static)!;

            MethodInfo registerCatalog = entryType.GetMethod("RegisterCatalogOverlays", BindingFlags.Public | BindingFlags.Static)!;
            MethodInfo registerDrop = entryType.GetMethod("RegisterNaturalDropOverlay", BindingFlags.Public | BindingFlags.Static)!;
            MethodInfo byIdMethod = itemCatalogType.GetMethod("ById", [typeof(string)])!;
            MethodInfo byHashMethod = itemCatalogType.GetMethod("ByHash", [typeof(uint)])!;
            MethodInfo visualMethod = itemCatalogType.GetMethod("Visual", [typeof(string)])!;
            MethodInfo isVisualMethod = itemCatalogType.GetMethod("IsVisual", [typeof(ushort)])!;
            MethodInfo currencyActionMethod = itemCatalogType.GetMethod("CurrencyAction", [itemCatalogType.GetNestedType("BaseType")!])!;

            object dropDataDefault = dropDataProviderType.GetProperty("Default", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
            object dropOverlays = dropDataProviderType.GetProperty("Overlays", BindingFlags.Public | BindingFlags.Instance)!.GetValue(dropDataDefault)!;
            PropertyInfo snapshotProp = dropPoolOverlaysType.GetProperty("Snapshot", BindingFlags.Public | BindingFlags.Instance)!;

            try
            {
                var logs = new List<string>();

                // 4. Register ItemCatalog runtime overlays via compiled SiphonLocalExileEntry
                using IDisposable catalogHandle = (IDisposable)registerCatalog.Invoke(null, [owner, (Action<string>)logs.Add])!;
                object? emptyBase = byIdMethod.Invoke(itemCatalog, [SiphonProtocol.EmptyOrbBaseId]);
                object? chargedBase = byIdMethod.Invoke(itemCatalog, [SiphonProtocol.ChargedOrbBaseId]);
                Assert.NotNull(emptyBase);
                Assert.NotNull(chargedBase);
                Assert.Equal(emptyBase, byHashMethod.Invoke(itemCatalog, [SiphonProtocol.EmptyOrbTypeHash]));
                Assert.Equal(chargedBase, byHashMethod.Invoke(itemCatalog, [SiphonProtocol.ChargedOrbTypeHash]));
                Assert.Equal(SiphonProtocol.EmptyOrbVisualHash16, (ushort)visualMethod.Invoke(itemCatalog, [SiphonProtocol.EmptyOrbVisualId])!);
                Assert.Equal(SiphonProtocol.ChargedOrbVisualHash16, (ushort)visualMethod.Invoke(itemCatalog, [SiphonProtocol.ChargedOrbVisualId])!);
                Assert.True((bool)isVisualMethod.Invoke(itemCatalog, [SiphonProtocol.EmptyOrbVisualHash16])!);
                Assert.True((bool)isVisualMethod.Invoke(itemCatalog, [SiphonProtocol.ChargedOrbVisualHash16])!);
                Assert.Equal("remove_random_mod", (string?)currencyActionMethod.Invoke(itemCatalog, [emptyBase]));
                Assert.Equal("remove_random_mod", (string?)currencyActionMethod.Invoke(itemCatalog, [chargedBase]));

                // Idempotent second call when already present in Program.Items
                int countBeforeSecond = ((ICollection)itemCatalogType.GetProperty("All")!.GetValue(itemCatalog)!).Count;
                using IDisposable secondCatalogHandle = (IDisposable)registerCatalog.Invoke(null, [owner, (Action<string>)logs.Add])!;
                int countAfterSecond = ((ICollection)itemCatalogType.GetProperty("All")!.GetValue(itemCatalog)!).Count;
                Assert.Equal(countBeforeSecond, countAfterSecond);

                // 5. Disabled natural drops returns null and adds zero drop-pool entries
                int baselineDropOverlayCount = GetSnapshotEntries(snapshotProp, dropOverlays).Count;
                object? disabledHandle = registerDrop.Invoke(null, [owner, boundDefaults with { EnableNaturalDrops = false }, (Action<string>)logs.Add]);
                Assert.Null(disabledHandle);
                Assert.Equal(baselineDropOverlayCount, GetSnapshotEntries(snapshotProp, dropOverlays).Count);

                // 6. Register active natural currency drop-pool overlay via compiled SiphonLocalExileEntry
                var activeSettings = new SiphonDropSettings
                {
                    EnableNaturalDrops = true,
                    DropWeight = 1_000_000,
                    MinAreaLevel = 45,
                    MaxAreaLevel = 80,
                    Sources = SiphonDropSettings.DefaultSources,
                    MonsterRarity = SiphonDropSettings.DefaultMonsterRarity,
                };
                object? dropHandleObj = registerDrop.Invoke(null, [owner, activeSettings, (Action<string>)logs.Add]);
                Assert.NotNull(dropHandleObj);

                List<object> entries = GetSnapshotEntries(snapshotProp, dropOverlays);
                object registered = Assert.Single(entries, r => string.Equals((string)r.GetType().GetProperty("Owner")!.GetValue(r)!, owner, StringComparison.Ordinal));
                object dropEntry = registered.GetType().GetProperty("Entry")!.GetValue(registered)!;
                Assert.Equal(Enum.Parse(dropPoolCategoryType, "Currency"), dropEntry.GetType().GetProperty("Category")!.GetValue(dropEntry));
                Assert.Equal(SiphonProtocol.EmptyOrbBaseId, (string)dropEntry.GetType().GetProperty("ItemId")!.GetValue(dropEntry)!);
                Assert.Equal(1_000_000.0, (double)dropEntry.GetType().GetProperty("Weight")!.GetValue(dropEntry)!);
                Assert.Equal(45, (int)dropEntry.GetType().GetProperty("MinAreaLevel")!.GetValue(dropEntry)!);
                Assert.Equal(80, (int)dropEntry.GetType().GetProperty("MaxAreaLevel")!.GetValue(dropEntry)!);
                Assert.Equal(Enum.Parse(dropSourceMaskType, "All"), dropEntry.GetType().GetProperty("Sources")!.GetValue(dropEntry));
                Assert.Equal(Enum.Parse(dropMonsterFilterType, "Any"), dropEntry.GetType().GetProperty("Monsters")!.GetValue(dropEntry));

                // 7. Verify ItemDrops lazily rebuilt and honors area-level + source gates!
                long activePoolRevision = (long)poolRevisionProp.GetValue(itemDrops)!;
                Assert.True(activePoolRevision > initialPoolRevision);

                MethodInfo currencyDropMethod = itemDropsType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .First(m => m.Name == "CurrencyDrop" && m.GetParameters().Length >= 2);

                object? RollCurrency(int areaLevel, object dropContext, Random rng)
                {
                    ParameterInfo[] ps = currencyDropMethod.GetParameters();
                    object?[] args = new object?[ps.Length];
                    args[0] = areaLevel;
                    args[1] = rng;
                    for (int i = 2; i < ps.Length; i++)
                    {
                        if (ps[i].ParameterType == typeof(Nullable<>).MakeGenericType(dropContextType))
                            args[i] = dropContext;
                        else
                            args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
                    }
                    return currencyDropMethod.Invoke(itemDrops, args);
                }

                string? RolledId(object? drop)
                {
                    if (drop is null) return null;
                    object bt = drop.GetType().GetProperty("Type")!.GetValue(drop)!;
                    return (string)bt.GetType().GetProperty("Id")!.GetValue(bt)!;
                }

                object rareRarity = Enum.Parse(monsterRarityType, "Rare");
                object killCtx = InvokeStaticWithDefaults(dropContextType.GetMethod("MonsterKill")!, rareRarity);
                object containerCtx = InvokeStaticWithDefaults(dropContextType.GetMethod("Container")!);
                object strongboxCtx = InvokeStaticWithDefaults(dropContextType.GetMethod("Strongbox")!);
                object bossCtx = InvokeStaticWithDefaults(dropContextType.GetMethod("BossBonus")!);
                object mapCurrencyCtx = InvokeStaticWithDefaults(dropContextType.GetMethod("MapCurrency")!, rareRarity);

                var rng = new Random(1337);
                // Below MinAreaLevel (20 < 45) & above MaxAreaLevel (90 > 80) -> NEVER drops!
                for (int i = 0; i < 16; i++)
                {
                    Assert.NotEqual(SiphonProtocol.EmptyOrbBaseId, RolledId(RollCurrency(20, killCtx, rng)));
                    Assert.NotEqual(SiphonProtocol.EmptyOrbBaseId, RolledId(RollCurrency(90, killCtx, rng)));
                }

                // Within [45..80] across ALL 5 configured sources -> drops Empty Siphon Orb!
                Assert.Equal(SiphonProtocol.EmptyOrbBaseId, RolledId(RollCurrency(60, killCtx, rng)));
                Assert.Equal(SiphonProtocol.EmptyOrbBaseId, RolledId(RollCurrency(60, containerCtx, rng)));
                Assert.Equal(SiphonProtocol.EmptyOrbBaseId, RolledId(RollCurrency(60, strongboxCtx, rng)));
                Assert.Equal(SiphonProtocol.EmptyOrbBaseId, RolledId(RollCurrency(60, bossCtx, rng)));
                Assert.Equal(SiphonProtocol.EmptyOrbBaseId, RolledId(RollCurrency(60, mapCurrencyCtx, rng)));

                // 8. Survives DropDataProvider reload/reset without losing runtime overlays!
                dropDataProviderType.GetMethod("Reset", BindingFlags.Public | BindingFlags.Instance)!.Invoke(dropDataDefault, null);
                Assert.NotNull(byIdMethod.Invoke(itemCatalog, [SiphonProtocol.EmptyOrbBaseId]));
                Assert.Equal(SiphonProtocol.EmptyOrbBaseId, RolledId(RollCurrency(60, killCtx, rng)));

                // 9. Mod unload via GameEventOwners.Release cleanly removes BOTH catalog & drop-pool overlays!
                releaseOwner.Invoke(null, [owner, moduleRank]);

                Assert.Null(byIdMethod.Invoke(itemCatalog, [SiphonProtocol.EmptyOrbBaseId]));
                Assert.Null(byIdMethod.Invoke(itemCatalog, [SiphonProtocol.ChargedOrbBaseId]));
                Assert.Null(byHashMethod.Invoke(itemCatalog, [SiphonProtocol.EmptyOrbTypeHash]));
                Assert.Null(byHashMethod.Invoke(itemCatalog, [SiphonProtocol.ChargedOrbTypeHash]));
                Assert.Null(visualMethod.Invoke(itemCatalog, [SiphonProtocol.EmptyOrbVisualId]));
                Assert.DoesNotContain(
                    GetSnapshotEntries(snapshotProp, dropOverlays),
                    r => string.Equals((string)r.GetType().GetProperty("Owner")!.GetValue(r)!, owner, StringComparison.Ordinal));

                long postUnloadRevision = (long)poolRevisionProp.GetValue(itemDrops)!;
                Assert.True(postUnloadRevision > activePoolRevision);

                for (int i = 0; i < 32; i++)
                {
                    Assert.NotEqual(SiphonProtocol.EmptyOrbBaseId, RolledId(RollCurrency(60, killCtx, rng)));
                }
            }
            finally
            {
                releaseOwner.Invoke(null, [owner, moduleRank]);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("Game_CLIENT_DB", prevClientDb);
            Environment.SetEnvironmentVariable("Game_WORLD_DB", prevWorldDb);
            Environment.SetEnvironmentVariable("Game_SQL_DIR", prevSqlDir);
            alc.Unload();
        }
    }

    private static List<object> GetSnapshotEntries(PropertyInfo snapshotProp, object dropOverlays) =>
        ((IEnumerable)snapshotProp.GetValue(dropOverlays)!).Cast<object>().ToList();

    private static void SetStaticMember(Type type, string name, object value)
    {
        if (type.GetField(name, BindingFlags.Public | BindingFlags.Static) is { } field)
            field.SetValue(null, value);
        else
            type.GetProperty(name, BindingFlags.Public | BindingFlags.Static)?.SetValue(null, value);
    }

    private static object InvokeWithDefaults(ConstructorInfo ctor, params object?[] leadingArgs)
    {
        ParameterInfo[] ps = ctor.GetParameters();
        object?[] args = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            args[i] = i < leadingArgs.Length
                ? leadingArgs[i]
                : ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
        }
        return ctor.Invoke(args);
    }

    private static object InvokeStaticWithDefaults(MethodInfo method, params object?[] leadingArgs)
    {
        ParameterInfo[] ps = method.GetParameters();
        object?[] args = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            args[i] = i < leadingArgs.Length
                ? leadingArgs[i]
                : ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
        }
        return method.Invoke(null, args)!;
    }

    private static InMemoryDat64Source BuildSyntheticStockCurrencyTables()
    {
        var tables = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        // Stock ItemVisualIdentity (1 row, stride 604)
        byte[] iviBytes = BuildSyntheticTable(1, CurrencyDatPatcher.ItemVisualIdentityRowStride);
        var iviPatcher = new Dat64TablePatcher(Dat64Table.Parse(iviBytes));
        byte[] iviRow0 = iviPatcher.CloneRow(0);
        iviPatcher.SetString(iviRow0, CurrencyDatPatcher.IviIdOffset, "CurrencyRemoveMod");
        iviPatcher.SetString(iviRow0, CurrencyDatPatcher.IviDdsFileOffset, "Art/2DItems/Currency/CurrencyRemoveMod.dds");
        iviPatcher.SetString(iviRow0, CurrencyDatPatcher.IviAoFileOffset, "Metadata/Items/Currency/CurrencyOrb.ao");
        Dat64TablePatcher.WriteUInt16(iviRow0, CurrencyDatPatcher.IviHash16Offset, 0x1234);
        iviPatcher.UpdateRow(0, iviRow0);
        tables["ItemVisualIdentity"] = iviPatcher.BuildAndVerify().Serialize();

        // Stock BaseItemTypes (1 row, stride 374)
        byte[] bitBytes = BuildSyntheticTable(1, CurrencyDatPatcher.BaseItemTypesRowStride);
        var bitPatcher = new Dat64TablePatcher(Dat64Table.Parse(bitBytes));
        byte[] bitRow0 = bitPatcher.CloneRow(0);
        bitPatcher.SetString(bitRow0, CurrencyDatPatcher.BitIdOffset, "Metadata/Items/Currency/CurrencyRemoveMod");
        Dat64TablePatcher.WriteForeignRow(bitRow0, CurrencyDatPatcher.BitItemClassOffset, 30UL);
        Dat64TablePatcher.WriteInt32(bitRow0, CurrencyDatPatcher.BitWidthOffset, 1);
        Dat64TablePatcher.WriteInt32(bitRow0, CurrencyDatPatcher.BitHeightOffset, 1);
        bitPatcher.SetString(bitRow0, CurrencyDatPatcher.BitNameOffset, "Orb of Annulment");
        bitPatcher.SetString(bitRow0, CurrencyDatPatcher.BitInheritsFromOffset, "Metadata/Items/Currency/StackableCurrency");
        Dat64TablePatcher.WriteInt32(bitRow0, CurrencyDatPatcher.BitDropLevelOffset, 1);
        Dat64TablePatcher.WriteInt32(bitRow0, CurrencyDatPatcher.BitSizeOnGroundOffset, 1);
        Dat64TablePatcher.WriteInt32(bitRow0, CurrencyDatPatcher.BitModDomainOffset, 43);
        Dat64TablePatcher.WriteForeignRow(bitRow0, CurrencyDatPatcher.BitItemVisualIdentityOffset, 0UL);
        Dat64TablePatcher.WriteUInt32(bitRow0, CurrencyDatPatcher.BitHash32Offset, PoeHashing.Hash32("Metadata/Items/Currency/CurrencyRemoveMod"));
        bitPatcher.UpdateRow(0, bitRow0);
        tables["BaseItemTypes"] = bitPatcher.BuildAndVerify().Serialize();

        // Stock CurrencyItems (1 row, stride 205)
        byte[] curBytes = BuildSyntheticTable(1, CurrencyDatPatcher.CurrencyItemsRowStride);
        var curPatcher = new Dat64TablePatcher(Dat64Table.Parse(curBytes));
        byte[] curRow0 = curPatcher.CloneRow(0);
        Dat64TablePatcher.WriteForeignRow(curRow0, CurrencyDatPatcher.CurBaseItemTypesKeyOffset, 0UL);
        Dat64TablePatcher.WriteInt32(curRow0, CurrencyDatPatcher.CurStackSizeOffset, 20);
        Dat64TablePatcher.WriteInt32(curRow0, CurrencyDatPatcher.CurCurrencyUseTypeOffset, 2);
        curPatcher.SetString(curRow0, CurrencyDatPatcher.CurActionOffset, "remove_random_mod");
        Dat64TablePatcher.WriteInt32(curRow0, CurrencyDatPatcher.CurCurrencyTabStackSizeOffset, 5000);
        curPatcher.UpdateRow(0, curRow0);
        tables["CurrencyItems"] = curPatcher.BuildAndVerify().Serialize();

        return new InMemoryDat64Source(tables);
    }

    private static byte[] BuildSyntheticTable(int rowCount, int stride)
    {
        byte[] data = new byte[4 + rowCount * stride + 8 + 8];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0, 4), rowCount);
        data.AsSpan(4 + rowCount * stride, 8).Fill(0xBB);
        return data;
    }

    private sealed class LiveGatewayProbeAlc(string gatewayDllPath)
        : AssemblyLoadContext("SiphonLiveDropProbe-" + Guid.NewGuid().ToString("N"), isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(gatewayDllPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.Equals(assemblyName.Name, "LocalExile.Client.ServerBridge", StringComparison.OrdinalIgnoreCase))
                return typeof(ClientBridgeHost).Assembly;
            if (string.Equals(assemblyName.Name, "LocalExile.Client.Sdk", StringComparison.OrdinalIgnoreCase))
                return typeof(SdkVersion).Assembly;
            if (string.Equals(assemblyName.Name, "DualMod.SiphonOrb.Shared", StringComparison.OrdinalIgnoreCase))
                return typeof(SiphonProtocol).Assembly;
            if (string.Equals(assemblyName.Name, "siphon", StringComparison.OrdinalIgnoreCase))
                return typeof(SiphonServerMod).Assembly;

            string? path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is not null ? LoadFromAssemblyPath(path) : null;
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is not null ? LoadUnmanagedDllFromPath(path) : nint.Zero;
        }
    }

    private static string? FindLocalExileGatewayBinDir()
    {
        foreach (string config in new[] { "Release", "Debug" })
        {
            string candidate = Path.Combine("/home/nam/Git/LocalExile/src/GameServer.Gateway/bin", config, "net10.0");
            if (File.Exists(Path.Combine(candidate, "GameServer.Gateway.dll")) &&
                File.Exists(Path.Combine(candidate, "GameServer.Modding.dll")) &&
                File.Exists(Path.Combine(candidate, "GameServer.Core.dll")) &&
                Directory.Exists("/home/nam/Git/LocalExile/data"))
            {
                return candidate;
            }
        }
        return null;
    }

    private static string? FindLocalExileGameDll()
    {
        string[] candidates =
        [
            "/home/nam/Git/LocalExile/tests/GameServer.Gateway.Tests/bin/Release/net10.0/game.dll",
            "/home/nam/Git/LocalExile/Modules/Core/Game/bin/game.dll",
            "/home/nam/Git/LocalExile/tests/GameServer.Gateway.Tests/bin/Debug/net10.0/game.dll",
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "NativeMods.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException("Could not locate NativeMods repository root.");
    }
}
