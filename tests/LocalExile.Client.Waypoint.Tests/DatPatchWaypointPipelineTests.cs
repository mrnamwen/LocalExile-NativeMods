using System.Buffers.Binary;
using System.Text;
using LocalExile.Client.AssetPipeline.Dat64;
using LocalExile.Client.Host;
using LocalExile.Client.Host.Native;
using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.Sdk.Protocol;
using LocalExile.Client.ServerBridge;
using LocalExile.Client.Waypoint;
using Xunit;
using SdkContentRowMap = LocalExile.Client.Sdk.Extend.ContentRowMap;

namespace LocalExile.Client.Waypoint.Tests;

public sealed class DatPatchWaypointPipelineTests
{
    [Fact]
    public void DualModManifest_RoundTripsDatPatchesAndCatalogSummary()
    {
        var manifest = new DualModManifest
        {
            Id = "custom-orbs",
            Name = "Custom Orbs",
            Description = "Declarative .datpatch.json mod.",
            Author = "Community",
            Version = "1.0.0",
            CompatibleClientVersions = ["0.5.2-beta"],
            Kind = "feature",
            Depends = ["Game"],
            NativeClient = new NativeClientManifest
            {
                SdkVersionRange = ">=1.0.0 <2.0.0",
                PayloadDir = "client",
                DatPatches = ["Mods/custom-orbs/orbs.datpatch.json"],
            },
        };

        string json = manifest.ToJson();
        DualModManifest reparsed = DualModManifest.Parse(json);
        Assert.NotNull(reparsed.NativeClient);
        Assert.Equal(["Mods/custom-orbs/orbs.datpatch.json"], reparsed.NativeClient!.DatPatches);

        var entry = WaypointCatalogModEntry.FromManifest(reparsed, isProject: true, hasSettings: false);
        Assert.Equal(1, entry.NativeClient!.DatPatchesCount);

        ValidationReport report = DualModPackageValidator.ValidateManifest(reparsed, SemVer.Parse(SdkVersion.Current));
        Assert.True(report.IsValid);
    }

    [Fact]
    public void Validator_RejectsClosedTablesStatsAndItemClassesForAppend_AndInvalidDeadStatSlots()
    {
        const string badStatsAppendJson = """
        {
          "schemaVersion": 1,
          "modId": "bad-mod",
          "tables": {
            "Stats": {
              "append": [
                { "key": "illegal_stat", "fields": { "Id": "illegal_stat" } }
              ]
            },
            "ItemClasses": {
              "append": [
                { "key": "illegal_class", "fields": { "Id": "IllegalClass" } }
              ]
            },
            "WorldAreas": {
              "append": [
                { "key": "non_appendable_area", "fields": { "Id": "CustomArea" } }
              ]
            }
          },
          "allocateDeadStat": [
            { "key": "forbidden_dummy", "preferredSlot": 464 },
            { "key": "not_dead_slot", "preferredSlot": 12 }
          ]
        }
        """;

        ValidationReport report = DualModPackageValidator.ValidateDatPatchJson(badStatsAppendJson, expectedModId: "bad-mod");
        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, d => d.Code == "DAT004" && d.Message.Contains("Stats", StringComparison.Ordinal));
        Assert.Contains(report.Errors, d => d.Code == "DAT004" && d.Message.Contains("ItemClasses", StringComparison.Ordinal));
        Assert.Contains(report.Errors, d => d.Code == "DAT005" && d.Message.Contains("WorldAreas", StringComparison.Ordinal));
        Assert.Contains(report.Errors, d => d.Code == "DAT009" && d.Message.Contains("464", StringComparison.Ordinal));
        Assert.Contains(report.Errors, d => d.Code == "DAT009" && d.Message.Contains("12", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_TwoModsSharingSameDatTablesViaDatPatchesHaveZeroConflicts_DuplicateKeyConflicts()
    {
        DatPatchDocument patchA = DatPatchDocument.Parse("""
        {
          "schemaVersion": 1,
          "modId": "mod-alpha",
          "tables": {
            "BaseItemTypes": {
              "append": [{ "key": "alpha_orb", "fields": { "Name": "Alpha Orb" } }]
            }
          },
          "allocateDeadStat": [{ "key": "alpha_stat", "preferredSlot": 99 }]
        }
        """);

        DatPatchDocument patchB = DatPatchDocument.Parse("""
        {
          "schemaVersion": 1,
          "modId": "mod-beta",
          "tables": {
            "BaseItemTypes": {
              "append": [{ "key": "beta_orb", "fields": { "Name": "Beta Orb" } }]
            }
          },
          "allocateDeadStat": [{ "key": "beta_stat", "preferredSlot": 101 }]
        }
        """);

        // Zero conflicts when two mods both append distinct keys to BaseItemTypes!
        Assert.Empty(DualModPackageValidator.DetectDatPatchConflicts([patchA, patchB]));

        // Duplicate (table, entryKey) across mods -> CNF007, duplicate preferredSlot -> CNF008!
        DatPatchDocument patchCollision = DatPatchDocument.Parse("""
        {
          "schemaVersion": 1,
          "modId": "mod-gamma",
          "tables": {
            "BaseItemTypes": {
              "append": [{ "key": "alpha_orb", "fields": { "Name": "Collision Orb" } }]
            }
          },
          "allocateDeadStat": [{ "key": "gamma_stat", "preferredSlot": 99 }]
        }
        """);

        IReadOnlyList<CrossModConflict> conflicts = DualModPackageValidator.DetectDatPatchConflicts([patchA, patchCollision]);
        Assert.Contains(conflicts, c => c.Code == "CNF007" && c.Resource == "BaseItemTypes:alpha_orb");
        Assert.Contains(conflicts, c => c.Code == "CNF008" && c.Resource == "Stats[99]");
    }

    [Fact]
    public async Task WaypointInstaller_BuildsMergedCache_AutoMountsInHostT0_AndVerifiesServerBridgeDigest()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"nativemods-wp-datpatch-{Guid.NewGuid():N}");
        string localExileRoot = Path.Combine(tempRoot, "LocalExile");
        string pkgAlphaDir = Path.Combine(tempRoot, "pkg-alpha");
        string pkgBetaDir = Path.Combine(tempRoot, "pkg-beta");
        string pkgCollisionDir = Path.Combine(tempRoot, "pkg-collision");

        try
        {
            InMemoryDat64Source stock = BuildSyntheticStockSource();

            CreateModPackage(pkgAlphaDir, "mod-alpha", """
            {
              "schemaVersion": 1,
              "modId": "mod-alpha",
              "tables": {
                "ItemVisualIdentity": {
                  "append": [
                    {
                      "key": "alpha_visual",
                      "cloneFromId": "CurrencyRemoveMod",
                      "fields": {
                        "Id": "AlphaVisual",
                        "DDSFile": "Art/2DItems/Currency/Mods/Alpha/AlphaOrb.dds"
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
                        "ItemVisualIdentity": "$ref:ItemVisualIdentity:alpha_visual"
                      }
                    }
                  ]
                },
                "CurrencyItems": {
                  "append": [
                    {
                      "key": "alpha_currency",
                      "cloneFromRow": 0,
                      "fields": {
                        "BaseItemTypesKey": "$ref:BaseItemTypes:alpha_orb",
                        "StackSize": 20,
                        "Description": "Alpha currency"
                      }
                    }
                  ]
                }
              },
              "allocateDeadStat": [
                {
                  "key": "alpha_power",
                  "preferredSlot": 99,
                  "isPublic": true,
                  "description": {
                    "template": "{0:+d}% increased Alpha Power"
                  }
                }
              ]
            }
            """);

            CreateModPackage(pkgBetaDir, "mod-beta", """
            {
              "schemaVersion": 1,
              "modId": "mod-beta",
              "depends": ["mod-alpha"],
              "tables": {
                "ItemVisualIdentity": {
                  "append": [
                    {
                      "key": "beta_visual",
                      "cloneFromId": "CurrencyRemoveMod",
                      "fields": {
                        "Id": "BetaVisual",
                        "DDSFile": "Art/2DItems/Currency/Mods/Beta/BetaOrb.dds"
                      }
                    }
                  ]
                },
                "BaseItemTypes": {
                  "append": [
                    {
                      "key": "beta_orb",
                      "cloneFromId": "Metadata/Items/Currency/CurrencyRemoveMod",
                      "fields": {
                        "Id": "Metadata/Items/Currency/Mods/Beta/BetaOrb",
                        "Name": "Orb of Beta",
                        "ItemVisualIdentity": "$ref:ItemVisualIdentity:beta_visual"
                      }
                    }
                  ]
                },
                "CurrencyItems": {
                  "append": [
                    {
                      "key": "beta_currency",
                      "cloneFromRow": 0,
                      "fields": {
                        "BaseItemTypesKey": "$ref:BaseItemTypes:beta_orb",
                        "FullStack_BaseItemTypesKey": "$ref:BaseItemTypes:alpha_orb",
                        "StackSize": 10,
                        "Description": "Beta shard"
                      }
                    }
                  ]
                }
              }
            }
            """);

            CreateModPackage(pkgCollisionDir, "mod-collision", """
            {
              "schemaVersion": 1,
              "modId": "mod-collision",
              "tables": {
                "BaseItemTypes": {
                  "append": [
                    {
                      "key": "alpha_orb",
                      "cloneFromId": "Metadata/Items/Currency/CurrencyRemoveMod",
                      "fields": {
                        "Id": "Metadata/Items/Currency/Mods/Collision/Orb",
                        "Name": "Duplicate Key Orb"
                      }
                    }
                  ]
                }
              }
            }
            """);

            var installer = new WaypointModInstaller();
            var installOpts = new InstallOptions
            {
                CurrentSdkVersion = SemVer.Parse(SdkVersion.Current),
                Dat64Source = stock,
            };

            // 1. Install mod-alpha -> builds Mods/Client/.merged-cache/
            InstallAuditReport alphaReport = installer.Install(pkgAlphaDir, localExileRoot, installOpts);
            Assert.True(alphaReport.Succeeded, alphaReport.ToHumanReadableSummary());
            Assert.False(string.IsNullOrWhiteSpace(alphaReport.MergedCacheDigest));

            // 2. Install mod-beta targeting the EXACT SAME .datc64 tables -> ZERO conflicts, merged cache updated!
            InstallAuditReport betaReport = installer.Install(pkgBetaDir, localExileRoot, installOpts);
            Assert.True(betaReport.Succeeded, betaReport.ToHumanReadableSummary());
            Assert.Empty(betaReport.Conflicts);
            Assert.False(string.IsNullOrWhiteSpace(betaReport.MergedCacheDigest));
            Assert.NotEqual(alphaReport.MergedCacheDigest, betaReport.MergedCacheDigest);

            string mergedCacheDir = WaypointModInstaller.GetMergedCacheDirectory(localExileRoot);
            Assert.True(File.Exists(Path.Combine(mergedCacheDir, "Data", "BaseItemTypes.datc64")));
            Assert.True(File.Exists(Path.Combine(mergedCacheDir, "Data", "CurrencyItems.datc64")));
            Assert.True(File.Exists(Path.Combine(mergedCacheDir, "Data", "Stats.datc64")));
            Assert.True(File.Exists(Path.Combine(mergedCacheDir, "Metadata", "StatDescriptions", "mod-alpha_custom_stats.txt")));
            Assert.True(File.Exists(Path.Combine(mergedCacheDir, SdkContentRowMap.FileName)));

            DeploymentLedger ledger = WaypointModInstaller.ReadLedger(localExileRoot);
            Assert.Equal(betaReport.MergedCacheDigest, ledger.MergedCacheDigest);

            // 3. Attempting to install mod-collision with duplicate (BaseItemTypes, alpha_orb) is blocked with CNF007!
            InstallAuditReport collisionReport = installer.Install(pkgCollisionDir, localExileRoot, installOpts);
            Assert.False(collisionReport.Succeeded);
            Assert.Contains(collisionReport.Conflicts, c => c.Code == "CNF007" && c.Resource == "BaseItemTypes:alpha_orb");

            // 4. Host t=0 auto-mounts Mods/Client/.merged-cache/, resolves both mods' rows, and matches ServerBridge digest!
            using var sim = new SimulatedNativeBridge();
            var sentRulesetPackets = new List<(byte RulesetId, byte[] InnerPayload)>();
            sim.OnOutgoingRulesetPacket = (rulesetId, innerPayload) => sentRulesetPackets.Add((rulesetId, innerPayload.ToArray()));
            using var host = new ClientHostRuntime(sim, new ClientHostOptions
            {
                ModsRootDirectory = WaypointModInstaller.GetClientModsRoot(localExileRoot),
            });

            Assert.Equal(betaReport.MergedCacheDigest, host.Content.ContentRowMapDigest);
            Assert.True(host.Content.TryResolveRow("BaseItemTypes", "alpha_orb", out ContentRowEntry? alphaRow));
            Assert.True(host.Content.TryResolveRow("BaseItemTypes", "beta_orb", out ContentRowEntry? betaRow));
            Assert.True(host.Content.TryResolveRow("Stats", "alpha_power", out ContentRowEntry? statRow));
            Assert.Equal(99, statRow!.RowIndex);
            Assert.Equal("old_do_not_use_99", statRow.StockId);
            Assert.NotEqual(alphaRow!.RowIndex, betaRow!.RowIndex);

            // Server side loads content-row-map.json from LocalExile root and verifies client handshake digest!
            ServerContentRowMap? serverRowMap = ServerContentRowMap.TryLoadFromLocalExileRoot(localExileRoot);
            Assert.NotNull(serverRowMap);
            Assert.Equal(betaReport.MergedCacheDigest, serverRowMap!.Digest);
            Assert.Equal(alphaRow.RowIndex, serverRowMap.RequireRowIndex("BaseItemTypes", "alpha_orb"));
            Assert.Equal(betaRow.RowIndex, serverRowMap.RequireRowIndex("BaseItemTypes", "beta_orb"));

            var sink = new NullSink();
            using var serverBridge = new ClientBridgeHost(sink, new ClientBridgeOptions
            {
                ContentRowMap = serverRowMap.RowMap,
            });
            ClientHandshake hello = host.SendHandshake();
            Assert.Equal(betaReport.MergedCacheDigest, hello.ContentRowMapDigest);
            Assert.Single(sentRulesetPackets);
            (byte rulesetId, byte[] innerPayload) = sentRulesetPackets[0];
            byte[] rulesetBody = MessageCodec.EncodeRulesetBody(rulesetId, innerPayload);
            Assert.True(await serverBridge.HandleIncomingPacketAsync("conn-wp", IOpcodeRegistry.ClientRulesetOpcode, rulesetBody));
            Assert.True(serverBridge.For("conn-wp").IsContentRowMapDigestMatched);

            // 5. Uninstall mod-beta -> .merged-cache/ rebuilds cleanly back to alpha's digest!
            UninstallResult unBeta = installer.Uninstall("mod-beta", localExileRoot, installOpts);
            Assert.True(unBeta.Succeeded);
            DeploymentLedger afterBeta = WaypointModInstaller.ReadLedger(localExileRoot);
            Assert.Equal(alphaReport.MergedCacheDigest, afterBeta.MergedCacheDigest);

            // 6. Uninstall mod-alpha -> .merged-cache/ is removed and MergedCacheDigest is cleared!
            UninstallResult unAlpha = installer.Uninstall("mod-alpha", localExileRoot, installOpts);
            Assert.True(unAlpha.Succeeded);
            Assert.False(Directory.Exists(mergedCacheDir));
            DeploymentLedger afterAlpha = WaypointModInstaller.ReadLedger(localExileRoot);
            Assert.Null(afterAlpha.MergedCacheDigest);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static void CreateModPackage(string dir, string modId, string datPatchJson)
    {
        Directory.CreateDirectory(Path.Combine(dir, "client", "Mods", modId));
        var manifest = new DualModManifest
        {
            Id = modId,
            Name = modId,
            Description = $"Test mod {modId}",
            Author = "Test",
            Version = "1.0.0",
            CompatibleClientVersions = ["0.5.2-beta"],
            Kind = "feature",
            NativeClient = new NativeClientManifest
            {
                SdkVersionRange = ">=1.0.0 <2.0.0",
                PayloadDir = "client",
                DatPatches = [$"Mods/{modId}/content.datpatch.json"],
            },
        };

        File.WriteAllText(Path.Combine(dir, "mod.json"), manifest.ToJson(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, "client", "Mods", modId, "content.datpatch.json"), datPatchJson, new UTF8Encoding(false));
    }

    private static InMemoryDat64Source BuildSyntheticStockSource()
    {
        Dat64SchemaRegistry schema = Dat64SchemaRegistry.LoadDefault329();

        // ItemVisualIdentity (1 stock row)
        Dat64TableSchema iviSchema = schema.GetRequiredTable("ItemVisualIdentity");
        var iviVar = new MemoryStream();
        iviVar.Write([0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB]);
        byte[] iviRow = new byte[iviSchema.RowStride];
        ulong iviIdOff = AppendUtf16String(iviVar, "CurrencyRemoveMod");
        BinaryPrimitives.WriteUInt64LittleEndian(iviRow.AsSpan(iviSchema.PrimaryIdColumn!.Offset, 8), iviIdOff);
        BinaryPrimitives.WriteUInt16LittleEndian(iviRow.AsSpan(iviSchema.Hash16Column!.Offset, 2), 0x1111);
        byte[] iviBytes = BuildDatBytes(1, iviRow, iviVar.ToArray());

        // BaseItemTypes (1 stock row)
        Dat64TableSchema bitSchema = schema.GetRequiredTable("BaseItemTypes");
        var bitVar = new MemoryStream();
        bitVar.Write([0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB]);
        byte[] bitRow = new byte[bitSchema.RowStride];
        ulong bitIdOff = AppendUtf16String(bitVar, "Metadata/Items/Currency/CurrencyRemoveMod");
        BinaryPrimitives.WriteUInt64LittleEndian(bitRow.AsSpan(bitSchema.PrimaryIdColumn!.Offset, 8), bitIdOff);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bitRow.AsSpan(bitSchema.Hash32Column!.Offset, 4),
            PoeHashing.Hash32("Metadata/Items/Currency/CurrencyRemoveMod"));
        byte[] bitBytes = BuildDatBytes(1, bitRow, bitVar.ToArray());

        // CurrencyItems (1 stock row)
        Dat64TableSchema curSchema = schema.GetRequiredTable("CurrencyItems");
        var curVar = new MemoryStream();
        curVar.Write([0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB]);
        byte[] curRow = new byte[curSchema.RowStride];
        curSchema.TryResolveColumn("BaseItemTypesKey", out Dat64ColumnSchema? curBitCol);
        BinaryPrimitives.WriteUInt64LittleEndian(curRow.AsSpan(curBitCol!.Offset, 8), 0ul);
        BinaryPrimitives.WriteUInt64LittleEndian(curRow.AsSpan(curBitCol.Offset + 8, 8), 0ul);
        byte[] curBytes = BuildDatBytes(1, curRow, curVar.ToArray());

        // Stats (105 stock rows so dead slot 99 exists)
        Dat64TableSchema statsSchema = schema.GetRequiredTable("Stats");
        var statsVar = new MemoryStream();
        statsVar.Write([0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB]);
        const int stockStatsRows = 105;
        byte[] statsFixed = new byte[stockStatsRows * statsSchema.RowStride];
        for (int r = 0; r < stockStatsRows; r++)
        {
            string id = r == 99 ? "old_do_not_use_99" : $"stock_stat_{r}";
            ulong idOff = AppendUtf16String(statsVar, id);
            BinaryPrimitives.WriteUInt64LittleEndian(statsFixed.AsSpan(r * statsSchema.RowStride + statsSchema.PrimaryIdColumn!.Offset, 8), idOff);
        }
        byte[] statsBytes = BuildDatBytes(stockStatsRows, statsFixed, statsVar.ToArray());

        return new InMemoryDat64Source(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["ItemVisualIdentity"] = iviBytes,
            ["BaseItemTypes"] = bitBytes,
            ["CurrencyItems"] = curBytes,
            ["Stats"] = statsBytes,
        });
    }

    private static ulong AppendUtf16String(MemoryStream varStream, string text)
    {
        ulong offset = (ulong)varStream.Length;
        byte[] bytes = Encoding.Unicode.GetBytes(text);
        varStream.Write(bytes);
        varStream.Write([0x00, 0x00, 0x00, 0x00]);
        return offset;
    }

    private static byte[] BuildDatBytes(int rowCount, byte[] fixedSection, byte[] varSection)
    {
        byte[] output = new byte[4 + fixedSection.Length + varSection.Length];
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(0, 4), rowCount);
        Buffer.BlockCopy(fixedSection, 0, output, 4, fixedSection.Length);
        Buffer.BlockCopy(varSection, 0, output, 4 + fixedSection.Length, varSection.Length);
        return output;
    }

    private sealed class NullSink : IGatewayPacketSink
    {
        public Task SendPacketAsync(string connectionId, ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SendChatReplyAsync(string connectionId, string text, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
