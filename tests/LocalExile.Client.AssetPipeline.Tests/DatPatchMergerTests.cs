using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using LocalExile.Client.AssetPipeline.Dat64;
using Xunit;

namespace LocalExile.Client.AssetPipeline.Tests;

public sealed class DatPatchMergerTests
{
    [Fact]
    public void SchemaRegistry_Default329MatchesVerified329StridesAndPolicies()
    {
        Dat64SchemaRegistry schema = Dat64SchemaRegistry.LoadDefault329();

        // All 22 live-verified appendable registry tables must be present in the bundled schema
        Assert.Equal(22, Dat64SchemaRegistry.AppendableRegistryTables.Count);
        foreach (string tableName in Dat64SchemaRegistry.AppendableRegistryTables)
        {
            Assert.True(schema.TryGetTable(tableName, out Dat64TableSchema? tbl), $"Missing schema for appendable table '{tableName}'.");
            Assert.NotNull(tbl);
            Assert.True(tbl!.RowStride > 0);
        }

        // Verified 3.29.3d exact strides
        Assert.Equal(374, schema.GetRequiredTable("BaseItemTypes").RowStride);
        Assert.Equal(604, schema.GetRequiredTable("ItemVisualIdentity").RowStride);
        Assert.Equal(205, schema.GetRequiredTable("CurrencyItems").RowStride);
        Assert.Equal(180, schema.GetRequiredTable("MtxTypes").RowStride);
        Assert.Equal(68, schema.GetRequiredTable("MtxTypeGameSpecific").RowStride);
        Assert.Equal(138, schema.GetRequiredTable("Pet").RowStride);
        Assert.Equal(654, schema.GetRequiredTable("Mods").RowStride);
        Assert.Equal(41, schema.GetRequiredTable("ModType").RowStride);
        Assert.Equal(314, schema.GetRequiredTable("BuffDefinitions").RowStride);
        Assert.Equal(34, schema.GetRequiredTable("MinimapIcons").RowStride);
        Assert.Equal(244, schema.GetRequiredTable("BuffVisuals").RowStride);
        Assert.Equal(389, schema.GetRequiredTable("NPCTalk").RowStride);
        Assert.Equal(105, schema.GetRequiredTable("Stats").RowStride);
        Assert.Equal(153, schema.GetRequiredTable("ItemClasses").RowStride);
        Assert.Equal(662, schema.GetRequiredTable("WorldAreas").RowStride);
        Assert.Equal(64, schema.GetRequiredTable("Words").RowStride);

        // 30 dead stat slots, never containing row 464 (dummy_stat_display_nothing)
        Assert.Equal(30, Dat64SchemaRegistry.DeadStatSlots.Count);
        Assert.DoesNotContain(464, Dat64SchemaRegistry.DeadStatSlots);
        Assert.Contains("Stats", Dat64SchemaRegistry.ClosedRegistryTables);
        Assert.Contains("ItemClasses", Dat64SchemaRegistry.ClosedRegistryTables);
    }

    [Fact]
    public void Merge_MultiModConcurrentAppendsAcrossFourTables_ResolvesCrossTableRefsAndProducesDeterministicDigest()
    {
        InMemoryDat64Source stock = BuildSyntheticStockSource();

        // Mod B depends on Mod A and references Mod A's BaseItemTypes entry via $ref (FullStack_BaseItemTypesKey)
        // while both Mod A and Mod B concurrently append to BaseItemTypes, ItemVisualIdentity, CurrencyItems, and Mods!
        DatPatchDocument modA = DatPatchDocument.Parse("""
        {
          "schemaVersion": 1,
          "modId": "mod-alpha",
          "tables": {
            "ItemVisualIdentity": {
              "append": [
                {
                  "key": "alpha_orb_visual",
                  "cloneFromId": "CurrencyRemoveMod",
                  "fields": {
                    "Id": "AlphaOrbVisual",
                    "DDSFile": "Art/2DItems/Currency/Mods/Alpha/AlphaOrb.dds",
                    "AOFile": "Metadata/Items/Currency/CurrencyOrb.ao"
                  }
                }
              ]
            },
            "BaseItemTypes": {
              "append": [
                {
                  "key": "alpha_orb",
                  "cloneFromId": "Metadata/Items/Currency/CurrencyRemoveMod",
                  "fields": {
                    "Id": "Metadata/Items/Currency/Mods/Alpha/AlphaOrb",
                    "Name": "Orb of Alpha",
                    "ItemVisualIdentity": "$ref:ItemVisualIdentity:alpha_orb_visual",
                    "Implicit_ModsKeys": ["$ref:Mods:alpha_enchant"],
                    "TagsKeys": [1353]
                  }
                }
              ]
            },
            "CurrencyItems": {
              "append": [
                {
                  "key": "alpha_orb_currency",
                  "cloneFromId": "Metadata/Items/Currency/CurrencyRemoveMod",
                  "fields": {
                    "BaseItemTypesKey": "$ref:BaseItemTypes:alpha_orb",
                    "StackSize": 20,
                    "Description": "Alpha currency orb"
                  }
                }
              ]
            },
            "Mods": {
              "append": [
                {
                  "key": "alpha_enchant",
                  "cloneFromId": "Strength1",
                  "fields": {
                    "Id": "AlphaCustomEnchant1",
                    "Name": "of Alpha",
                    "Level": 1,
                    "Stat1Min": 10,
                    "Stat1Max": 20
                  }
                }
              ]
            }
          }
        }
        """);

        DatPatchDocument modB = DatPatchDocument.Parse("""
        {
          "schemaVersion": 1,
          "modId": "mod-beta",
          "depends": ["mod-alpha"],
          "tables": {
            "ItemVisualIdentity": {
              "append": [
                {
                  "key": "beta_shard_visual",
                  "cloneFromId": "CurrencyRemoveMod",
                  "fields": {
                    "Id": "BetaShardVisual",
                    "DDSFile": "Art/2DItems/Currency/Mods/Beta/BetaShard.dds"
                  }
                }
              ]
            },
            "BaseItemTypes": {
              "append": [
                {
                  "key": "beta_shard",
                  "cloneFromId": "Metadata/Items/Currency/CurrencyRemoveMod",
                  "fields": {
                    "Id": "Metadata/Items/Currency/Mods/Beta/BetaShard",
                    "Name": "Alpha Shard",
                    "ItemVisualIdentity": "$ref:ItemVisualIdentity:beta_shard_visual",
                    "TagsKeys": [231]
                  }
                }
              ]
            },
            "CurrencyItems": {
              "append": [
                {
                  "key": "beta_shard_currency",
                  "cloneFromRow": 0,
                  "fields": {
                    "BaseItemTypesKey": "$ref:BaseItemTypes:beta_shard",
                    "FullStack_BaseItemTypesKey": "$ref:BaseItemTypes:alpha_orb",
                    "StackSize": 20,
                    "Description": "Combine 20 into an Orb of Alpha"
                  }
                }
              ]
            },
            "Mods": {
              "append": [
                {
                  "key": "beta_affix",
                  "cloneFromId": "Strength1",
                  "fields": {
                    "Id": "BetaCustomAffix1",
                    "Name": "of Beta",
                    "Level": 50
                  }
                }
              ]
            }
          }
        }
        """);

        string tempCacheDir = Path.Combine(Path.GetTempPath(), $"nativemods-datmerger-{Guid.NewGuid():N}");
        try
        {
            // Merge in [modB, modA] order vs [modA, modB] order: deterministic sort MUST produce identical digests & bytes!
            DatPatchMergeResult res1 = DatPatchMerger.Merge(stock, [modB, modA], outputDirectory: tempCacheDir);
            DatPatchMergeResult res2 = DatPatchMerger.Merge(stock, [modA, modB]);

            Assert.Equal(res1.RowMap.Digest, res2.RowMap.Digest);
            Assert.Equal(["mod-alpha", "mod-beta"], res1.RowMap.Mods);

            // Verify merged cache output layout on disk
            Assert.True(File.Exists(Path.Combine(tempCacheDir, "content-row-map.json")));
            Assert.True(File.Exists(Path.Combine(tempCacheDir, "Data", "BaseItemTypes.datc64")));
            Assert.True(File.Exists(Path.Combine(tempCacheDir, "Data", "ItemVisualIdentity.datc64")));
            Assert.True(File.Exists(Path.Combine(tempCacheDir, "Data", "CurrencyItems.datc64")));
            Assert.True(File.Exists(Path.Combine(tempCacheDir, "Data", "Mods.datc64")));

            ContentRowMap reloadedMap = ContentRowMap.Parse(File.ReadAllText(Path.Combine(tempCacheDir, "content-row-map.json")));
            Assert.Equal(res1.RowMap.Digest, reloadedMap.Digest);

            Dat64Table bit = Dat64Table.Parse(res1.MergedTables["BaseItemTypes"]);
            Dat64Table ivi = Dat64Table.Parse(res1.MergedTables["ItemVisualIdentity"]);
            Dat64Table cur = Dat64Table.Parse(res1.MergedTables["CurrencyItems"]);
            Dat64Table mods = Dat64Table.Parse(res1.MergedTables["Mods"]);

            Assert.Equal(3, bit.RowCount);
            Assert.Equal(3, ivi.RowCount);
            Assert.Equal(3, cur.RowCount);
            Assert.Equal(3, mods.RowCount);

            // Cross-table $ref checks
            int alphaBitRow = res1.RowMap.Tables["BaseItemTypes"].Entries["alpha_orb"].RowIndex;
            int betaBitRow = res1.RowMap.Tables["BaseItemTypes"].Entries["beta_shard"].RowIndex;
            int alphaIviRow = res1.RowMap.Tables["ItemVisualIdentity"].Entries["alpha_orb_visual"].RowIndex;
            int betaIviRow = res1.RowMap.Tables["ItemVisualIdentity"].Entries["beta_shard_visual"].RowIndex;
            int alphaCurRow = res1.RowMap.Tables["CurrencyItems"].Entries["alpha_orb_currency"].RowIndex;
            int betaCurRow = res1.RowMap.Tables["CurrencyItems"].Entries["beta_shard_currency"].RowIndex;
            int alphaModRow = res1.RowMap.Tables["Mods"].Entries["alpha_enchant"].RowIndex;
            int betaModRow = res1.RowMap.Tables["Mods"].Entries["beta_affix"].RowIndex;

            Assert.Equal((ulong)alphaIviRow, bit.ReadForeignRow(alphaBitRow, CurrencyDatPatcher.BitItemVisualIdentityOffset));
            Assert.Equal((ulong)betaIviRow, bit.ReadForeignRow(betaBitRow, CurrencyDatPatcher.BitItemVisualIdentityOffset));
            Assert.Equal([(ulong)alphaModRow], bit.ReadForeignRowArray(alphaBitRow, CurrencyDatPatcher.BitImplicitModsOffset));
            Assert.Equal((ulong)alphaBitRow, cur.ReadForeignRow(alphaCurRow, CurrencyDatPatcher.CurBaseItemTypesKeyOffset));
            Assert.Equal((ulong)betaBitRow, cur.ReadForeignRow(betaCurRow, CurrencyDatPatcher.CurBaseItemTypesKeyOffset));
            Assert.Equal((ulong)alphaBitRow, cur.ReadForeignRow(betaCurRow, CurrencyDatPatcher.CurFullStackBaseItemOffset));

            // Auto-computed HASH32 = MurmurHash2-32(UTF-8 Id)
            uint expectedAlphaHash32 = PoeHashing.Hash32("Metadata/Items/Currency/Mods/Alpha/AlphaOrb");
            uint expectedBetaHash32 = PoeHashing.Hash32("Metadata/Items/Currency/Mods/Beta/BetaShard");
            Assert.Equal(expectedAlphaHash32, bit.ReadUInt32(alphaBitRow, CurrencyDatPatcher.BitHash32Offset));
            Assert.Equal(expectedBetaHash32, bit.ReadUInt32(betaBitRow, CurrencyDatPatcher.BitHash32Offset));
            Assert.Equal(expectedAlphaHash32, res1.RowMap.Tables["BaseItemTypes"].Entries["alpha_orb"].Hash32);

            // Auto-allocated collision-free non-zero HASH16 on ItemVisualIdentity and Mods
            ushort stockIviHash16 = ivi.ReadUInt16(0, CurrencyDatPatcher.IviHash16Offset);
            ushort alphaIviHash16 = ivi.ReadUInt16(alphaIviRow, CurrencyDatPatcher.IviHash16Offset);
            ushort betaIviHash16 = ivi.ReadUInt16(betaIviRow, CurrencyDatPatcher.IviHash16Offset);
            Assert.Equal(3, new HashSet<ushort> { stockIviHash16, alphaIviHash16, betaIviHash16 }.Count);
            Assert.NotEqual((ushort)0, alphaIviHash16);
            Assert.NotEqual((ushort)0, betaIviHash16);

            ushort stockModHash16 = mods.ReadUInt16(0, 8);
            ushort alphaModHash16 = mods.ReadUInt16(alphaModRow, 8);
            ushort betaModHash16 = mods.ReadUInt16(betaModRow, 8);
            Assert.Equal(3, new HashSet<ushort> { stockModHash16, alphaModHash16, betaModHash16 }.Count);
            Assert.Equal(alphaModHash16, res1.RowMap.Tables["Mods"].Entries["alpha_enchant"].Hash16);
            Assert.Equal(betaModHash16, res1.RowMap.Tables["Mods"].Entries["beta_affix"].Hash16);
        }
        finally
        {
            if (Directory.Exists(tempCacheDir))
            {
                Directory.Delete(tempCacheDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Merge_DeepCopiesAllVariableSectionArraysAndStrings_NeverAliasesForeignRowOffsets()
    {
        // Stock Pet row 0 has non-empty Pet.__col7 ([1]) and Pet.Skills ([42, 43]),
        // Stock ItemVisualIdentity row 0 has non-empty SMFiles (["Art/Models/Test.sm"]) and Pickup_AchievementItemsKeys ([77]),
        // Stock Mods row 0 has non-empty Families ([5]), SpawnWeight_TagsKeys ([10, 20]), and SpawnWeight_Values ([1000, 500]).
        InMemoryDat64Source stock = BuildSyntheticStockSource();

        // Clone rows WITHOUT explicitly overriding any array columns -> merger MUST deep-copy ALL of them!
        DatPatchDocument patch = DatPatchDocument.Parse("""
        {
          "schemaVersion": 1,
          "modId": "deep-copy-mod",
          "tables": {
            "Pet": {
              "append": [
                {
                  "key": "pet_clone_1",
                  "cloneFromRow": 0,
                  "fields": {
                    "Id": "Metadata/Pet/Mods/DeepCopy/Pet1"
                  }
                },
                {
                  "key": "pet_clone_2",
                  "cloneFromRow": 0,
                  "fields": {
                    "Id": "Metadata/Pet/Mods/DeepCopy/Pet2"
                  }
                }
              ]
            },
            "ItemVisualIdentity": {
              "append": [
                {
                  "key": "ivi_clone",
                  "cloneFromRow": 0,
                  "fields": {
                    "Id": "DeepCopyIvi"
                  }
                }
              ]
            },
            "Mods": {
              "append": [
                {
                  "key": "mod_clone",
                  "cloneFromRow": 0,
                  "fields": {
                    "Id": "DeepCopyMod1"
                  }
                }
              ]
            }
          }
        }
        """);

        DatPatchMergeResult result = DatPatchMerger.Merge(stock, [patch]);

        // 1. Pet.__col7 (offset 36) and Pet.Skills (offset 68): all 3 rows have identical VALUES but 3 STRICTLY DISTINCT variable offsets!
        Dat64Table pet = Dat64Table.Parse(result.MergedTables["Pet"]);
        Assert.Equal(3, pet.RowCount);
        Assert.Equal([1UL], pet.ReadForeignRowArray(0, MaxwellDatPatcher.PetCol7Offset));
        Assert.Equal([1UL], pet.ReadForeignRowArray(1, MaxwellDatPatcher.PetCol7Offset));
        Assert.Equal([1UL], pet.ReadForeignRowArray(2, MaxwellDatPatcher.PetCol7Offset));
        Assert.Equal([42UL, 43UL], pet.ReadForeignRowArray(1, MaxwellDatPatcher.PetSkillsOffset));

        ulong petCol7Off0 = pet.ReadUInt64(0, MaxwellDatPatcher.PetCol7Offset + 8);
        ulong petCol7Off1 = pet.ReadUInt64(1, MaxwellDatPatcher.PetCol7Offset + 8);
        ulong petCol7Off2 = pet.ReadUInt64(2, MaxwellDatPatcher.PetCol7Offset + 8);
        Assert.Equal(3, new HashSet<ulong> { petCol7Off0, petCol7Off1, petCol7Off2 }.Count);

        ulong petSkillsOff0 = pet.ReadUInt64(0, MaxwellDatPatcher.PetSkillsOffset + 8);
        ulong petSkillsOff1 = pet.ReadUInt64(1, MaxwellDatPatcher.PetSkillsOffset + 8);
        ulong petSkillsOff2 = pet.ReadUInt64(2, MaxwellDatPatcher.PetSkillsOffset + 8);
        Assert.Equal(3, new HashSet<ulong> { petSkillsOff0, petSkillsOff1, petSkillsOff2 }.Count);

        // 2. ItemVisualIdentity string[] (SMFiles offset 242) and foreignrow[] (Pickup_AchievementItemsKeys offset 226)
        Dat64Table ivi = Dat64Table.Parse(result.MergedTables["ItemVisualIdentity"]);
        Assert.Equal(["Art/Models/Test.sm"], ivi.ReadStringArray(0, 242));
        Assert.Equal(["Art/Models/Test.sm"], ivi.ReadStringArray(1, 242));
        Assert.NotEqual(ivi.ReadUInt64(0, 242 + 8), ivi.ReadUInt64(1, 242 + 8));
        Assert.Equal([77UL], ivi.ReadForeignRowArray(1, 226));
        Assert.NotEqual(ivi.ReadUInt64(0, 226 + 8), ivi.ReadUInt64(1, 226 + 8));

        // 3. Mods foreignrow[] (Families offset 110, SpawnWeight_TagsKeys offset 158) and i32[] (SpawnWeight_Values offset 174)
        Dat64Table mods = Dat64Table.Parse(result.MergedTables["Mods"]);
        Assert.Equal([5UL], mods.ReadForeignRowArray(1, 110));
        Assert.NotEqual(mods.ReadUInt64(0, 110 + 8), mods.ReadUInt64(1, 110 + 8));
        Assert.Equal([10UL, 20UL], mods.ReadForeignRowArray(1, 158));
        Assert.NotEqual(mods.ReadUInt64(0, 158 + 8), mods.ReadUInt64(1, 158 + 8));
        Assert.Equal([1000, 500], mods.ReadInt32Array(1, 174));
        Assert.NotEqual(mods.ReadUInt64(0, 174 + 8), mods.ReadUInt64(1, 174 + 8));

        // 4. VerifyNoPointerArrayAliasing catches deliberate foreignrow[] aliasing!
        byte[] corruptedPet = (byte[])result.MergedTables["Pet"].Clone();
        int row1Col7OffPos = 4 + (1 * MaxwellDatPatcher.PetRowStride) + MaxwellDatPatcher.PetCol7Offset + 8;
        BinaryPrimitives.WriteUInt64LittleEndian(corruptedPet.AsSpan(row1Col7OffPos, 8), petCol7Off0);
        Dat64TableSchema petSchema = Dat64SchemaRegistry.LoadDefault329().GetRequiredTable("Pet");
        Assert.Throws<InvalidDataException>(() =>
            DatPatchMerger.VerifyNoPointerArrayAliasing(petSchema, Dat64Table.Parse(corruptedPet), stockRowCount: 1));
    }

    [Fact]
    public void Merge_InPlaceModify_UpdatesCurrencyStackSizeModsAndSafeStatsColumns()
    {
        InMemoryDat64Source stock = BuildSyntheticStockSource();

        DatPatchDocument patch = DatPatchDocument.Parse("""
        {
          "schemaVersion": 1,
          "modId": "qol-tweaks",
          "tables": {
            "CurrencyItems": {
              "modify": [
                {
                  "id": "Metadata/Items/Currency/CurrencyRemoveMod",
                  "fields": {
                    "StackSize": 65535,
                    "CurrencyTab_StackSize": 65535
                  }
                }
              ]
            },
            "Mods": {
              "modify": [
                {
                  "id": "Strength1",
                  "fields": {
                    "IsEssenceOnlyModifier": true,
                    "IsUnscalable": true,
                    "MaxLevel": 100
                  }
                }
              ]
            },
            "Stats": {
              "modify": [
                {
                  "rowIndex": 0,
                  "fields": {
                    "IsLocal": true,
                    "Semantics": 3
                  }
                }
              ]
            }
          }
        }
        """);

        DatPatchMergeResult result = DatPatchMerger.Merge(stock, [patch]);

        Dat64Table cur = Dat64Table.Parse(result.MergedTables["CurrencyItems"]);
        Assert.Equal(1, cur.RowCount); // In-place modify does NOT append
        Assert.Equal(65535, cur.ReadInt32(0, CurrencyDatPatcher.CurStackSizeOffset));
        Assert.Equal(65535, cur.ReadInt32(0, CurrencyDatPatcher.CurCurrencyTabStackSizeOffset));

        Dat64Table mods = Dat64Table.Parse(result.MergedTables["Mods"]);
        Assert.Equal(1, mods.RowCount);
        Assert.True(mods.ReadBool(0, 382)); // IsEssenceOnlyModifier
        Assert.True(mods.ReadBool(0, 653)); // IsUnscalable
        Assert.Equal(100, mods.ReadInt32(0, 407)); // MaxLevel

        Dat64Table stats = Dat64Table.Parse(result.MergedTables["Stats"]);
        Assert.Equal(Dat64SchemaRegistry.StockStatsRowCount329, stats.RowCount);
        Assert.Equal("stock_stat_0", stats.ReadString(0, 0)); // Stock Id untouched
        Assert.True(stats.ReadBool(0, 9)); // IsLocal
        Assert.Equal(3u, stats.ReadEnumRow(0, 11)); // Semantics
    }

    [Fact]
    public void Merge_AllocateDeadStat_AllocatesAll30VerifiedSlotsPreservesChecksumGeneratesUtf16BomOverlayAndRejects31st()
    {
        InMemoryDat64Source stock = BuildSyntheticStockSource();

        // Mod 1 allocates 1 dead stat with multi-variant description AND links a custom Mods row to it via $ref:Stats:<key>
        DatPatchDocument mod1 = DatPatchDocument.Parse("""
        {
          "schemaVersion": 1,
          "modId": "siphon-stats",
          "allocateDeadStat": [
            {
              "key": "siphon_power_pct",
              "isPublic": true,
              "isLocal": false,
              "isWeaponLocal": false,
              "semantics": 1,
              "description": {
                "variants": [
                  { "condition": "1|#", "text": "{0:+d}% increased Siphoned Power" },
                  { "condition": "#|-1", "text": "{0:d}% reduced Siphoned Power", "handlers": ["negate 1"] }
                ]
              }
            }
          ],
          "tables": {
            "Mods": {
              "append": [
                {
                  "key": "mod_siphon_power",
                  "cloneFromRow": 0,
                  "fields": {
                    "Id": "SiphonPowerMod1",
                    "Name": "of Siphoning",
                    "StatsKey1": "$ref:Stats:siphon_power_pct",
                    "Stat1Min": 5,
                    "Stat1Max": 15
                  }
                }
              ]
            }
          }
        }
        """);

        // Mod 2 allocates the remaining 29 dead slots -> exactly 30/30!
        var mod2Specs = new List<DeadStatAllocationSpec>(29);
        for (int i = 0; i < 29; i++)
        {
            mod2Specs.Add(new DeadStatAllocationSpec
            {
                Key = $"extra_stat_{i:D2}",
                IsLocal = true,
                Description = new DeadStatDescriptionSpec
                {
                    Template = $"{{0:+d}} to Extra Stat {i}",
                },
            });
        }

        var mod2 = new DatPatchDocument
        {
            ModId = "zz-pack",
            AllocateDeadStat = mod2Specs,
        };

        DatPatchMergeResult result = DatPatchMerger.Merge(stock, [mod1, mod2]);

        // Stats row count MUST remain strictly 23,362 (0x5b42)!
        Dat64Table stats = Dat64Table.Parse(result.MergedTables["Stats"]);
        Assert.Equal(Dat64SchemaRegistry.StockStatsRowCount329, stats.RowCount);

        // Verify all 30 allocated rows match the verified DeadStatSlots, never include row 464, and preserve stock Id + HASH32!
        ContentTableRowMap statsMap = result.RowMap.Tables["Stats"];
        Assert.Equal(30, statsMap.Entries.Count);
        var allocatedRows = statsMap.Entries.Values.Select(e => e.RowIndex).OrderBy(r => r).ToList();
        Assert.Equal(Dat64SchemaRegistry.DeadStatSlots, allocatedRows);
        Assert.DoesNotContain(464, allocatedRows);

        ContentRowMapEntry siphonStatEntry = statsMap.Entries["siphon_power_pct"];
        Assert.Equal(99, siphonStatEntry.RowIndex);
        Assert.Equal("old_do_not_use_slot_99", siphonStatEntry.StockId);
        // Stock Id & HASH32 preserved byte-for-byte (LocalExile Stat.Checksum intact!)
        Assert.Equal("old_do_not_use_slot_99", stats.ReadString(99, 0));
        Assert.Equal(PoeHashing.Hash32("old_do_not_use_slot_99"), stats.ReadUInt32(99, 34));
        Assert.True(stats.ReadBool(99, 8)); // IsPublic
        Assert.False(stats.ReadBool(99, 9)); // IsLocal
        Assert.Equal(1u, stats.ReadEnumRow(99, 11)); // Semantics

        // Verify Mods.StatsKey1 resolved "$ref:Stats:siphon_power_pct" -> row 99!
        Dat64Table mods = Dat64Table.Parse(result.MergedTables["Mods"]);
        int modRow = result.RowMap.Tables["Mods"].Entries["mod_siphon_power"].RowIndex;
        Assert.Equal(99UL, mods.ReadForeignRow(modRow, 30)); // StatsKey1 at offset 30

        // Verify generated Route A Metadata/StatDescriptions/siphon-stats_custom_stats.txt (UTF-16LE BOM + CRLF)
        const string expectedPath = "Metadata/StatDescriptions/siphon-stats_custom_stats.txt";
        Assert.True(result.GeneratedOverlays.TryGetValue(expectedPath, out byte[]? overlayBytes));
        Assert.NotNull(overlayBytes);
        Assert.True(overlayBytes!.Length >= 2 && overlayBytes[0] == 0xFF && overlayBytes[1] == 0xFE, "Overlay must have UTF-16LE BOM.");

        string overlayText = Encoding.Unicode.GetString(overlayBytes, 2, overlayBytes.Length - 2);
        Assert.Contains("description\r\n", overlayText, StringComparison.Ordinal);
        Assert.Contains("\t1 old_do_not_use_slot_99\r\n", overlayText, StringComparison.Ordinal);
        Assert.Contains("\t2\r\n", overlayText, StringComparison.Ordinal);
        Assert.Contains("\t\t1|# \"{0:+d}% increased Siphoned Power\"\r\n", overlayText, StringComparison.Ordinal);
        Assert.Contains("\t\t#|-1 \"{0:d}% reduced Siphoned Power\" negate 1\r\n", overlayText, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", overlayText.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

        // 31st dead stat allocation MUST fail cleanly!
        var mod3Overflow = new DatPatchDocument
        {
            ModId = "zzz-overflow",
            AllocateDeadStat = [new DeadStatAllocationSpec { Key = "stat_31" }],
        };
        InvalidOperationException overflowEx = Assert.Throws<InvalidOperationException>(() =>
            DatPatchMerger.Merge(stock, [mod1, mod2, mod3Overflow]));
        Assert.Contains("30", overflowEx.Message, StringComparison.Ordinal);

        // Explicitly requesting forbidden row 464 MUST fail cleanly!
        var mod464 = new DatPatchDocument
        {
            ModId = "bad-464",
            AllocateDeadStat = [new DeadStatAllocationSpec { Key = "bad_dummy", PreferredSlot = 464 }],
        };
        InvalidOperationException ex464 = Assert.Throws<InvalidOperationException>(() =>
            DatPatchMerger.Merge(stock, [mod464]));
        Assert.Contains("464", ex464.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Merge_HardRejectsClosedTableAppendsDuplicateKeysMtxCol18CollisionsAndStatsIdEdits()
    {
        InMemoryDat64Source stock = BuildSyntheticStockSource();

        // 1. Hard-reject append on Stats
        DatPatchDocument appendStats = DatPatchDocument.Parse("""
        {
          "schemaVersion": 1,
          "modId": "bad-stats-append",
          "tables": {
            "Stats": {
              "append": [ { "key": "illegal_stat", "fields": { "Id": "my_illegal_stat" } } ]
            }
          }
        }
        """);
        InvalidOperationException statsEx = Assert.Throws<InvalidOperationException>(() => DatPatchMerger.Merge(stock, [appendStats]));
        Assert.Contains("closed table 'Stats'", statsEx.Message, StringComparison.Ordinal);

        // 2. Hard-reject append on ItemClasses
        DatPatchDocument appendItemClasses = DatPatchDocument.Parse("""
        {
          "schemaVersion": 1,
          "modId": "bad-itemclass-append",
          "tables": {
            "ItemClasses": {
              "append": [ { "key": "illegal_class", "fields": { "Id": "MyIllegalClass" } } ]
            }
          }
        }
        """);
        InvalidOperationException icEx = Assert.Throws<InvalidOperationException>(() => DatPatchMerger.Merge(stock, [appendItemClasses]));
        Assert.Contains("closed table 'ItemClasses'", icEx.Message, StringComparison.Ordinal);

        // 3. Hard-reject modifying Stats.Id (checksum preservation!)
        DatPatchDocument modifyStatsId = DatPatchDocument.Parse("""
        {
          "schemaVersion": 1,
          "modId": "bad-stats-id-modify",
          "tables": {
            "Stats": {
              "modify": [ { "rowIndex": 99, "fields": { "Id": "renamed_breaks_checksum" } } ]
            }
          }
        }
        """);
        InvalidOperationException statsIdEx = Assert.Throws<InvalidOperationException>(() => DatPatchMerger.Merge(stock, [modifyStatsId]));
        Assert.Contains("Checksum", statsIdEx.Message, StringComparison.Ordinal);

        // 4. Hard-reject duplicate (table, entryKey) across two mods
        DatPatchDocument dupA = DatPatchDocument.Parse("""
        {
          "schemaVersion": 1,
          "modId": "dup-a",
          "tables": {
            "ClientStrings": { "append": [ { "key": "shared_key", "fields": { "Id": "StrA", "Text": "A" } } ] }
          }
        }
        """);
        DatPatchDocument dupB = DatPatchDocument.Parse("""
        {
          "schemaVersion": 1,
          "modId": "dup-b",
          "tables": {
            "ClientStrings": { "append": [ { "key": "shared_key", "fields": { "Id": "StrB", "Text": "B" } } ] }
          }
        }
        """);
        InvalidOperationException dupEx = Assert.Throws<InvalidOperationException>(() => DatPatchMerger.Merge(stock, [dupA, dupB]));
        Assert.Contains("shared_key", dupEx.Message, StringComparison.Ordinal);

        // 5. Enforce unique MtxTypes.__col18 (offset 137): cloning stock Kiwi1 without overriding __col18 MUST fail,
        //    while overriding via byte offset "@137" with a unique value succeeds!
        DatPatchDocument mtxCollision = DatPatchDocument.Parse("""
        {
          "schemaVersion": 1,
          "modId": "mtx-bad",
          "tables": {
            "MtxTypes": {
              "append": [
                {
                  "key": "pet_mtx",
                  "cloneFromRow": 0,
                  "fields": { "Id": "Metadata/Items/Pets/Mods/NewPet" }
                }
              ]
            }
          }
        }
        """);
        InvalidOperationException mtxEx = Assert.Throws<InvalidOperationException>(() => DatPatchMerger.Merge(stock, [mtxCollision]));
        Assert.Contains("__col18", mtxEx.Message, StringComparison.Ordinal);

        DatPatchDocument mtxValid = DatPatchDocument.Parse("""
        {
          "schemaVersion": 1,
          "modId": "mtx-good",
          "tables": {
            "MtxTypes": {
              "append": [
                {
                  "key": "pet_mtx",
                  "cloneFromRow": 0,
                  "fields": {
                    "Id": "Metadata/Items/Pets/Mods/NewPet",
                    "@137": "NewPetShort"
                  }
                }
              ]
            }
          }
        }
        """);
        DatPatchMergeResult mtxRes = DatPatchMerger.Merge(stock, [mtxValid]);
        Dat64Table mtxTable = Dat64Table.Parse(mtxRes.MergedTables["MtxTypes"]);
        Assert.Equal("NewPetShort", mtxTable.ReadString(1, Dat64SchemaRegistry.MtxTypesShortIdOffset));

        // 6. Hard-reject exceeding 65,535 total rows (u16 ceiling)
        byte[] hugeClientStrings = BuildManyClientStringRows(Dat64SchemaRegistry.MaxTotalRows);
        var hugeSource = new InMemoryDat64Source(new Dictionary<string, byte[]> { ["ClientStrings"] = hugeClientStrings });
        InvalidOperationException ceilingEx = Assert.Throws<InvalidOperationException>(() => DatPatchMerger.Merge(hugeSource, [dupA]));
        Assert.Contains("65535", ceilingEx.Message, StringComparison.Ordinal);

        // 7. Hard-reject dependency cycles
        DatPatchDocument cycA = DatPatchDocument.Parse("""{ "schemaVersion": 1, "modId": "cyc-a", "depends": ["cyc-b"] }""");
        DatPatchDocument cycB = DatPatchDocument.Parse("""{ "schemaVersion": 1, "modId": "cyc-b", "depends": ["cyc-a"] }""");
        InvalidOperationException cycEx = Assert.Throws<InvalidOperationException>(() => DatPatchMerger.Merge(stock, [cycA, cycB]));
        Assert.Contains("cycle", cycEx.Message, StringComparison.OrdinalIgnoreCase);

        // 8. Hard-reject unresolved $ref and cross-table $ref type mismatch
        DatPatchDocument badRef = DatPatchDocument.Parse("""
        {
          "schemaVersion": 1,
          "modId": "bad-ref",
          "tables": {
            "CurrencyItems": {
              "append": [
                { "key": "c1", "fields": { "BaseItemTypesKey": "$ref:BaseItemTypes:does_not_exist" } }
              ]
            }
          }
        }
        """);
        Assert.Throws<InvalidOperationException>(() => DatPatchMerger.Merge(stock, [badRef]));

        DatPatchDocument wrongTableRef = DatPatchDocument.Parse("""
        {
          "schemaVersion": 1,
          "modId": "wrong-ref",
          "tables": {
            "ClientStrings": {
              "append": [ { "key": "s1", "fields": { "Id": "Str1", "Text": "Hi" } } ]
            },
            "CurrencyItems": {
              "append": [ { "key": "c1", "fields": { "BaseItemTypesKey": "$ref:ClientStrings:s1" } } ]
            }
          }
        }
        """);
        InvalidOperationException typeEx = Assert.Throws<InvalidOperationException>(() => DatPatchMerger.Merge(stock, [wrongTableRef]));
        Assert.Contains("mismatch", typeEx.Message, StringComparison.OrdinalIgnoreCase);

        // 9. Hard-reject explicit HASH16 collision against stock (32037) and HASH16 == 0
        DatPatchDocument dupHash16 = DatPatchDocument.Parse("""
        {
          "schemaVersion": 1,
          "modId": "dup-h16",
          "tables": {
            "ItemVisualIdentity": {
              "append": [ { "key": "v1", "fields": { "Id": "Vis1", "HASH16": 32037 } } ]
            }
          }
        }
        """);
        Assert.Throws<InvalidOperationException>(() => DatPatchMerger.Merge(stock, [dupHash16]));

        DatPatchDocument zeroHash16 = DatPatchDocument.Parse("""
        {
          "schemaVersion": 1,
          "modId": "zero-h16",
          "tables": {
            "ItemVisualIdentity": {
              "append": [ { "key": "v1", "fields": { "Id": "Vis1", "HASH16": 0 } } ]
            }
          }
        }
        """);
        Assert.Throws<InvalidOperationException>(() => DatPatchMerger.Merge(stock, [zeroHash16]));

        // 10. Hard-reject modifying forbidden Stats row 464
        DatPatchDocument modRow464 = DatPatchDocument.Parse("""
        {
          "schemaVersion": 1,
          "modId": "bad-mod-464",
          "tables": {
            "Stats": {
              "modify": [ { "rowIndex": 464, "fields": { "IsLocal": true } } ]
            }
          }
        }
        """);
        Assert.Throws<InvalidOperationException>(() => DatPatchMerger.Merge(stock, [modRow464]));
    }

    [Fact]
    public void Cli_MergeDatPatches_EmitsMergedCacheAndRowMapCleanly()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"nativemods-cli-merge-{Guid.NewGuid():N}");
        string stockDir = Path.Combine(tempRoot, "stock");
        string patchesDir = Path.Combine(tempRoot, "patches");
        string outDir = Path.Combine(tempRoot, "merged-cache");

        try
        {
            Directory.CreateDirectory(Path.Combine(stockDir, "Data"));
            Directory.CreateDirectory(patchesDir);

            InMemoryDat64Source stock = BuildSyntheticStockSource();
            foreach (string tbl in new[] { "ClientStrings", "Stats" })
            {
                Assert.True(stock.TryReadTable(tbl, out byte[] bytes));
                File.WriteAllBytes(Path.Combine(stockDir, "Data", $"{tbl}.datc64"), bytes);
            }

            File.WriteAllText(Path.Combine(patchesDir, "alpha.datpatch.json"), """
            {
              "schemaVersion": 1,
              "modId": "cli-alpha",
              "tables": {
                "ClientStrings": {
                  "append": [
                    { "key": "greeting", "fields": { "Id": "CliAlphaGreeting", "Text": "Hello Exile" } }
                  ]
                }
              },
              "allocateDeadStat": [
                {
                  "key": "cli_stat",
                  "description": { "template": "{0:+d} to CLI Testing" }
                }
              ]
            }
            """);

            int exitCode = Program.Main(["merge-dat-patches", "--client", stockDir, "--patches", patchesDir, "--out", outDir]);
            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(outDir, "content-row-map.json")));
            Assert.True(File.Exists(Path.Combine(outDir, "Data", "ClientStrings.datc64")));
            Assert.True(File.Exists(Path.Combine(outDir, "Data", "Stats.datc64")));
            Assert.True(File.Exists(Path.Combine(outDir, "Metadata", "StatDescriptions", "cli-alpha_custom_stats.txt")));

            ContentRowMap rowMap = ContentRowMap.Parse(File.ReadAllText(Path.Combine(outDir, "content-row-map.json")));
            Assert.Equal(64, rowMap.Digest.Length);
            Assert.Equal(1, rowMap.Tables["ClientStrings"].Entries["greeting"].RowIndex);
            Assert.Equal(99, rowMap.Tables["Stats"].Entries["cli_stat"].RowIndex);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static InMemoryDat64Source BuildSyntheticStockSource()
    {
        return new InMemoryDat64Source(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["ItemVisualIdentity"] = BuildStockItemVisualIdentity(),
            ["BaseItemTypes"] = BuildStockBaseItemTypes(),
            ["CurrencyItems"] = BuildStockCurrencyItems(),
            ["Mods"] = BuildStockMods(),
            ["MtxTypes"] = BuildStockMtxTypes(),
            ["Pet"] = BuildStockPet(),
            ["ClientStrings"] = BuildManyClientStringRows(1),
            ["Stats"] = BuildStockStats23362(),
            ["ItemClasses"] = BuildBlankSingleRow(153),
        });
    }

    private static byte[] BuildStockItemVisualIdentity()
    {
        using var varMs = new MemoryStream();
        WriteSentinel(varMs);
        ulong idOff = AppendStr(varMs, "CurrencyRemoveMod");
        ulong ddsOff = AppendStr(varMs, "Art/2DItems/Currency/AnnullOrb.dds");
        ulong aoOff = AppendStr(varMs, "Metadata/Items/Currency/CurrencyOrb.ao");
        ulong pickupOff = AppendForeignRowArray(varMs, [77UL]);
        ulong smStrOff = AppendStr(varMs, "Art/Models/Test.sm");
        ulong smArrOff = AppendUInt64Array(varMs, [smStrOff]);

        byte[] row = new byte[CurrencyDatPatcher.ItemVisualIdentityRowStride];
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(0, 8), idOff);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(8, 8), ddsOff);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(16, 8), aoOff);
        Dat64TablePatcher.WriteForeignRow(row, 24, 33UL);
        Dat64TablePatcher.WriteUInt16(row, 40, 32037);
        Dat64TablePatcher.WriteArrayHeader(row, 226, 1UL, pickupOff);
        Dat64TablePatcher.WriteArrayHeader(row, 242, 1UL, smArrOff);
        return new Dat64Table(1, CurrencyDatPatcher.ItemVisualIdentityRowStride, row, varMs.ToArray()).Serialize();
    }

    private static byte[] BuildStockBaseItemTypes()
    {
        using var varMs = new MemoryStream();
        WriteSentinel(varMs);
        ulong idOff = AppendStr(varMs, "Metadata/Items/Currency/CurrencyRemoveMod");
        ulong nameOff = AppendStr(varMs, "Orb of Annulment");
        ulong inhOff = AppendStr(varMs, "Metadata/Items/Currency/StackableCurrency");
        ulong tagsOff = AppendForeignRowArray(varMs, [1353UL]);

        byte[] row = new byte[CurrencyDatPatcher.BaseItemTypesRowStride];
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(0, 8), idOff);
        Dat64TablePatcher.WriteForeignRow(row, 8, 30UL);
        Dat64TablePatcher.WriteInt32(row, 24, 1);
        Dat64TablePatcher.WriteInt32(row, 28, 1);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(32, 8), nameOff);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(40, 8), inhOff);
        Dat64TablePatcher.WriteInt32(row, 48, 35);
        Dat64TablePatcher.WriteArrayHeader(row, 104, 1UL, tagsOff);
        Dat64TablePatcher.WriteInt32(row, 120, 43);
        Dat64TablePatcher.WriteForeignRow(row, 124, 0UL);
        Dat64TablePatcher.WriteUInt32(row, 140, PoeHashing.Hash32("Metadata/Items/Currency/CurrencyRemoveMod"));
        return new Dat64Table(1, CurrencyDatPatcher.BaseItemTypesRowStride, row, varMs.ToArray()).Serialize();
    }

    private static byte[] BuildStockCurrencyItems()
    {
        using var varMs = new MemoryStream();
        WriteSentinel(varMs);
        ulong actOff = AppendStr(varMs, "remove_random_mod");
        ulong dirOff = AppendStr(varMs, "Right click this item then left click on an item.");
        ulong descOff = AppendStr(varMs, "Removes a random modifier from an item");
        ulong usageOff = AppendForeignRowArray(varMs, [99UL]);

        byte[] row = new byte[CurrencyDatPatcher.CurrencyItemsRowStride];
        Dat64TablePatcher.WriteForeignRow(row, 0, 0UL);
        Dat64TablePatcher.WriteInt32(row, 16, 20);
        Dat64TablePatcher.WriteInt32(row, 20, 2);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(24, 8), actOff);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(32, 8), dirOff);
        Dat64TablePatcher.WriteForeignRow(row, 40, null);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(56, 8), descOff);
        Dat64TablePatcher.WriteArrayHeader(row, 64, 1UL, usageOff);
        Dat64TablePatcher.WriteInt32(row, 129, 10000);
        return new Dat64Table(1, CurrencyDatPatcher.CurrencyItemsRowStride, row, varMs.ToArray()).Serialize();
    }

    private static byte[] BuildStockMods()
    {
        using var varMs = new MemoryStream();
        WriteSentinel(varMs);
        ulong idOff = AppendStr(varMs, "Strength1");
        ulong nameOff = AppendStr(varMs, "of the Brute");
        ulong famOff = AppendForeignRowArray(varMs, [5UL]);
        ulong swTagsOff = AppendForeignRowArray(varMs, [10UL, 20UL]);
        ulong swValsOff = AppendInt32Array(varMs, [1000, 500]);

        byte[] row = new byte[654];
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(0, 8), idOff);
        Dat64TablePatcher.WriteUInt16(row, 8, 22100);
        Dat64TablePatcher.WriteForeignRow(row, 10, 0UL);
        Dat64TablePatcher.WriteInt32(row, 26, 1);
        Dat64TablePatcher.WriteForeignRow(row, 30, 0UL);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(98, 8), nameOff);
        Dat64TablePatcher.WriteArrayHeader(row, 110, 1UL, famOff);
        Dat64TablePatcher.WriteArrayHeader(row, 158, 2UL, swTagsOff);
        Dat64TablePatcher.WriteArrayHeader(row, 174, 2UL, swValsOff);
        Dat64TablePatcher.WriteUInt32(row, 613, PoeHashing.Hash32("Strength1"));
        return new Dat64Table(1, 654, row, varMs.ToArray()).Serialize();
    }

    private static byte[] BuildStockMtxTypes()
    {
        using var varMs = new MemoryStream();
        WriteSentinel(varMs);
        ulong idOff = AppendStr(varMs, "Metadata/Items/Pets/Kiwi1");
        ulong nameOff = AppendStr(varMs, "Kiwi Pet");
        ulong shortOff = AppendStr(varMs, "Kiwi1");

        byte[] row = new byte[MaxwellDatPatcher.MtxTypesRowStride];
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(0, 8), idOff);
        Dat64TablePatcher.WriteUInt32(row, 8, PoeHashing.Hash32("Metadata/Items/Pets/Kiwi1"));
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(12, 8), nameOff);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(Dat64SchemaRegistry.MtxTypesShortIdOffset, 8), shortOff);
        return new Dat64Table(1, MaxwellDatPatcher.MtxTypesRowStride, row, varMs.ToArray()).Serialize();
    }

    private static byte[] BuildStockPet()
    {
        using var varMs = new MemoryStream();
        WriteSentinel(varMs);
        ulong idOff = AppendStr(varMs, "Metadata/Pet/Kiwi/Basic");
        ulong col7Off = AppendForeignRowArray(varMs, [1UL]);
        ulong skillsOff = AppendForeignRowArray(varMs, [42UL, 43UL]);

        byte[] row = new byte[MaxwellDatPatcher.PetRowStride];
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(0, 8), idOff);
        Dat64TablePatcher.WriteForeignRow(row, 8, 0UL);
        Dat64TablePatcher.WriteUInt32(row, 24, 0x7C5A9464u);
        Dat64TablePatcher.WriteUInt16(row, 28, 0xE83E);
        Dat64TablePatcher.WriteArrayHeader(row, MaxwellDatPatcher.PetCol7Offset, 1UL, col7Off);
        Dat64TablePatcher.WriteArrayHeader(row, MaxwellDatPatcher.PetSkillsOffset, 2UL, skillsOff);
        return new Dat64Table(1, MaxwellDatPatcher.PetRowStride, row, varMs.ToArray()).Serialize();
    }

    private static byte[] BuildStockStats23362()
    {
        const int rowCount = Dat64SchemaRegistry.StockStatsRowCount329;
        const int stride = 105;

        using var varMs = new MemoryStream(8192);
        WriteSentinel(varMs);
        ulong emptyStrOff = AppendStr(varMs, string.Empty);

        byte[] fixedRows = new byte[rowCount * stride];
        for (int r = 0; r < rowCount; r++)
        {
            Span<byte> row = fixedRows.AsSpan(r * stride, stride);
            BinaryPrimitives.WriteUInt64LittleEndian(row[0..8], emptyStrOff);
        }

        // Populate row 0, forbidden row 464, and all 30 verified dead slots with authentic stock-like IDs & HASH32s
        void SetStatRow(int r, string id)
        {
            ulong off = AppendStr(varMs, id);
            Span<byte> row = fixedRows.AsSpan(r * stride, stride);
            BinaryPrimitives.WriteUInt64LittleEndian(row[0..8], off);
            BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(34, 4), PoeHashing.Hash32(id));
        }

        SetStatRow(0, "stock_stat_0");
        SetStatRow(464, "dummy_stat_display_nothing");
        foreach (int deadRow in Dat64SchemaRegistry.DeadStatSlots)
        {
            SetStatRow(deadRow, $"old_do_not_use_slot_{deadRow.ToString(CultureInfo.InvariantCulture)}");
        }

        return new Dat64Table(rowCount, stride, fixedRows, varMs.ToArray()).Serialize();
    }

    private static byte[] BuildManyClientStringRows(int rowCount)
    {
        const int stride = 52;
        using var varMs = new MemoryStream(256);
        WriteSentinel(varMs);
        ulong emptyOff = AppendStr(varMs, string.Empty);
        ulong id0Off = AppendStr(varMs, "GameName");
        ulong text0Off = AppendStr(varMs, "Path of Exile");

        byte[] fixedRows = new byte[rowCount * stride];
        for (int r = 0; r < rowCount; r++)
        {
            Span<byte> row = fixedRows.AsSpan(r * stride, stride);
            BinaryPrimitives.WriteUInt64LittleEndian(row[0..8], r == 0 ? id0Off : emptyOff);
            BinaryPrimitives.WriteUInt64LittleEndian(row[8..16], r == 0 ? text0Off : emptyOff);
        }

        return new Dat64Table(rowCount, stride, fixedRows, varMs.ToArray()).Serialize();
    }

    [Fact]
    public void Phase5FourNewAppendableTables_SchemaLookupLiveBundles2StrideAndMergeRoundTrip()
    {
        Dat64SchemaRegistry schema = Dat64SchemaRegistry.LoadDefault329();
        (string TableName, string BundlePath, int ExpectedStride, int ExpectedColumns)[] tables =
        [
            ("MinimapIcons", "data/minimapicons.datc64", 34, 9),
            ("BuffVisuals", "data/buffvisuals.datc64", 244, 24),
            ("NPCTalk", "data/npctalk.datc64", 389, 38),
            ("Words", "data/words.datc64", 64, 7),
        ];

        foreach ((string tableName, _, int expectedStride, int expectedColumns) in tables)
        {
            Assert.Contains(tableName, Dat64SchemaRegistry.AppendableRegistryTables);
            Dat64TableSchema tableSchema = schema.GetRequiredTable(tableName);
            Assert.Equal(expectedStride, tableSchema.RowStride);
            Assert.Equal(expectedColumns, tableSchema.Columns.Count);
        }

        // Verify exact row-stride match against live 3.29.3d Bundles2 when available on disk.
        string liveClientDir = Environment.GetEnvironmentVariable("LOCALEXILE_STOCK_CLIENT_DIR")
            ?? "/home/nam/Git/LocalExile/Client";
        if (File.Exists(Path.Combine(liveClientDir, "Bundles2", "_.index.bin")))
        {
            using Bundles2Reader bundles = Bundles2Reader.Open(liveClientDir);
            foreach ((string tableName, string bundlePath, int expectedStride, _) in tables)
            {
                byte[] raw = bundles.ReadFile(bundlePath);
                Dat64Table parsed = Dat64Table.Parse(raw);
                Assert.True(parsed.RowCount > 0, $"Live {bundlePath} should have rows.");
                Assert.Equal(expectedStride, parsed.RowStride);
            }
        }

        // Synthetic stock source for deterministic DatPatchMerger append round-trip across all 4 tables.
        var stock = new InMemoryDat64Source(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["MinimapIcons"] = BuildStockMinimapIcons(),
            ["BuffVisuals"] = BuildStockBuffVisuals(),
            ["NPCTalk"] = BuildStockNpcTalk(),
            ["Words"] = BuildStockWords(),
        });

        DatPatchDocument patch = DatPatchDocument.Parse("""
        {
          "schemaVersion": 1,
          "modId": "mod-league-tables",
          "tables": {
            "MinimapIcons": {
              "append": [
                {
                  "key": "custom_altar_icon",
                  "cloneFromId": "Shrine",
                  "fields": {
                    "Id": "CustomLeagueAltar",
                    "MinimapIconRadius": 18,
                    "LargemapIconRadius": 24,
                    "MinimapIconPointerMaxDistance": 600
                  }
                }
              ]
            },
            "BuffVisuals": {
              "append": [
                {
                  "key": "custom_league_buff_vis",
                  "cloneFromId": "StockBuffVisual",
                  "fields": {
                    "Id": "CustomLeagueBuffVisual",
                    "BuffDDSFile": "Art/2DArt/BuffIcons/CustomLeagueBuff.dds",
                    "BuffName": "League Surge",
                    "BuffDescription": "Empowered by the custom league altar."
                  }
                }
              ]
            },
            "NPCTalk": {
              "append": [
                {
                  "key": "custom_npc_greeting",
                  "cloneFromRow": 0,
                  "fields": {
                    "DialogueOption": "Tell me about the Custom League",
                    "Script": "custom_league_intro"
                  }
                }
              ]
            },
            "Words": {
              "append": [
                {
                  "key": "custom_unique_word",
                  "cloneFromId": "StockWord",
                  "fields": {
                    "Text": "Aetherbound",
                    "Text2": "Aetherbound",
                    "Inflection": "N"
                  }
                }
              ]
            }
          }
        }
        """);

        DatPatchMergeResult result = DatPatchMerger.Merge(stock, [patch], schema);
        Assert.Equal(4, result.MergedTables.Count);
        Assert.Equal(1, result.RowMap.Tables["MinimapIcons"].Entries["custom_altar_icon"].RowIndex);
        Assert.Equal(1, result.RowMap.Tables["BuffVisuals"].Entries["custom_league_buff_vis"].RowIndex);
        Assert.Equal(1, result.RowMap.Tables["NPCTalk"].Entries["custom_npc_greeting"].RowIndex);
        Assert.Equal(1, result.RowMap.Tables["Words"].Entries["custom_unique_word"].RowIndex);

        Dat64Table mergedIcons = Dat64Table.Parse(result.MergedTables["MinimapIcons"]);
        Assert.Equal(2, mergedIcons.RowCount);
        Assert.Equal(34, mergedIcons.RowStride);
        Assert.Equal("CustomLeagueAltar", mergedIcons.ReadString(1, 0));
        Assert.Equal(18, mergedIcons.ReadInt32(1, 8));
        Assert.Equal(24, mergedIcons.ReadInt32(1, 12));
        Assert.Equal(600, mergedIcons.ReadInt32(1, 18));

        Dat64Table mergedBuffs = Dat64Table.Parse(result.MergedTables["BuffVisuals"]);
        Assert.Equal(2, mergedBuffs.RowCount);
        Assert.Equal(244, mergedBuffs.RowStride);
        Assert.Equal("CustomLeagueBuffVisual", mergedBuffs.ReadString(1, 0));
        Assert.Equal("Art/2DArt/BuffIcons/CustomLeagueBuff.dds", mergedBuffs.ReadString(1, 8));
        Assert.Equal("League Surge", mergedBuffs.ReadString(1, 65));
        Assert.Equal("Empowered by the custom league altar.", mergedBuffs.ReadString(1, 105));

        Dat64Table mergedNpcTalk = Dat64Table.Parse(result.MergedTables["NPCTalk"]);
        Assert.Equal(2, mergedNpcTalk.RowCount);
        Assert.Equal(389, mergedNpcTalk.RowStride);
        Assert.Equal("Tell me about the Custom League", mergedNpcTalk.ReadString(1, 20));
        Assert.Equal("custom_league_intro", mergedNpcTalk.ReadString(1, 76));

        Dat64Table mergedWords = Dat64Table.Parse(result.MergedTables["Words"]);
        Assert.Equal(2, mergedWords.RowCount);
        Assert.Equal(64, mergedWords.RowStride);
        Assert.Equal("Aetherbound", mergedWords.ReadString(1, 4));
        Assert.Equal(PoeHashing.Hash32("Aetherbound"), mergedWords.ReadUInt32(1, 44));
        Assert.Equal("Aetherbound", mergedWords.ReadString(1, 48));
        Assert.Equal("N", mergedWords.ReadString(1, 56));
    }

    private static byte[] BuildStockMinimapIcons()
    {
        using var varMs = new MemoryStream();
        WriteSentinel(varMs);
        ulong idOff = AppendStr(varMs, "Shrine");
        byte[] row = new byte[34];
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(0, 8), idOff);
        Dat64TablePatcher.WriteInt32(row, 8, 12);
        Dat64TablePatcher.WriteInt32(row, 12, 16);
        Dat64TablePatcher.WriteInt32(row, 18, 400);
        return new Dat64Table(1, 34, row, varMs.ToArray()).Serialize();
    }

    private static byte[] BuildStockBuffVisuals()
    {
        using var varMs = new MemoryStream();
        WriteSentinel(varMs);
        ulong idOff = AppendStr(varMs, "StockBuffVisual");
        ulong ddsOff = AppendStr(varMs, "Art/2DArt/BuffIcons/Stock.dds");
        ulong nameOff = AppendStr(varMs, "Stock Buff");
        ulong descOff = AppendStr(varMs, "Stock description");
        ulong emptyOff = AppendStr(varMs, string.Empty);
        byte[] row = new byte[244];
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(0, 8), idOff);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(8, 8), ddsOff);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(65, 8), nameOff);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(105, 8), descOff);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(113, 8), emptyOff);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(122, 8), emptyOff);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(210, 8), emptyOff);
        return new Dat64Table(1, 244, row, varMs.ToArray()).Serialize();
    }

    private static byte[] BuildStockNpcTalk()
    {
        using var varMs = new MemoryStream();
        WriteSentinel(varMs);
        ulong optOff = AppendStr(varMs, "Introduction");
        ulong scriptOff = AppendStr(varMs, "intro");
        ulong emptyOff = AppendStr(varMs, string.Empty);
        byte[] row = new byte[389];
        Dat64TablePatcher.WriteForeignRow(row, 0, 1UL);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(20, 8), optOff);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(76, 8), scriptOff);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(164, 8), emptyOff);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(252, 8), emptyOff);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(381, 8), emptyOff);
        return new Dat64Table(1, 389, row, varMs.ToArray()).Serialize();
    }

    private static byte[] BuildStockWords()
    {
        using var varMs = new MemoryStream();
        WriteSentinel(varMs);
        ulong textOff = AppendStr(varMs, "StockWord");
        ulong emptyOff = AppendStr(varMs, string.Empty);
        byte[] row = new byte[64];
        Dat64TablePatcher.WriteInt32(row, 0, 1);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(4, 8), textOff);
        Dat64TablePatcher.WriteUInt32(row, 44, PoeHashing.Hash32("StockWord"));
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(48, 8), textOff);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(56, 8), emptyOff);
        return new Dat64Table(1, 64, row, varMs.ToArray()).Serialize();
    }

    private static byte[] BuildBlankSingleRow(int stride)
    {
        using var varMs = new MemoryStream(32);
        WriteSentinel(varMs);
        AppendStr(varMs, string.Empty);
        return new Dat64Table(1, stride, new byte[stride], varMs.ToArray()).Serialize();
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

    private static ulong AppendUInt64Array(MemoryStream ms, ulong[] values)
    {
        ulong off = (ulong)ms.Length;
        Span<byte> buf = stackalloc byte[8];
        foreach (ulong v in values)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buf, v);
            ms.Write(buf);
        }

        return off;
    }

    private static ulong AppendInt32Array(MemoryStream ms, int[] values)
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
