using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using LocalExile.Client.AssetPipeline.Dat64;
using LocalExile.Client.Cli;
using LocalExile.Client.Waypoint;
using Xunit;

namespace LocalExile.Client.Cli.Tests;

public sealed class NativeModsCliTests
{
    [Fact]
    public void New_Tier1_ScaffoldsValidSingleFileRoslynServerMod()
    {
        string tempDir = CreateTempDir("tier1");
        try
        {
            var (exit, stdout, stderr) = RunCli("new", "tier1", "LootPing", "--out", tempDir, "--author", "TestAuthor");
            Assert.Equal(NativeModsCliApp.ExitSuccess, exit);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains("loot-ping", stdout, StringComparison.Ordinal);

            string modJsonPath = Path.Combine(tempDir, "mod.json");
            string entryPath = Path.Combine(tempDir, "LootPingLocalExileEntry.cs");
            string settingsPath = Path.Combine(tempDir, "settings.json");
            string readmePath = Path.Combine(tempDir, "README.md");

            Assert.True(File.Exists(modJsonPath));
            Assert.True(File.Exists(entryPath));
            Assert.True(File.Exists(settingsPath));
            Assert.True(File.Exists(readmePath));

            string entryCode = File.ReadAllText(entryPath);
            Assert.Contains("LocalExileGatewayHost.Create", entryCode, StringComparison.Ordinal);
            Assert.Contains("OnClientReady", entryCode, StringComparison.Ordinal);
            Assert.Contains("RegisterSlotButtonAsync", entryCode, StringComparison.Ordinal);
            Assert.Contains("RegisterHotkeyAsync", entryCode, StringComparison.Ordinal);
            Assert.Contains("ShowToastAsync", entryCode, StringComparison.Ordinal);

            ValidationReport pkgReport = DualModPackageValidator.ValidatePackageDirectory(tempDir, new SemVer(0, 4, 0));
            Assert.True(pkgReport.IsValid, FormatDiagnostics(pkgReport.Diagnostics));
            Assert.Empty(pkgReport.Warnings);

            var (valExit, valOut, valErr) = RunCli("validate", tempDir);
            Assert.Equal(NativeModsCliApp.ExitSuccess, valExit);
            Assert.Equal(string.Empty, valErr);
            Assert.Contains("Validation VALID", valOut, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [Fact]
    public void New_Dual_ScaffoldsValidDualModWithZeroUiLinterAndValidatorDiagnostics()
    {
        string tempDir = CreateTempDir("dual");
        try
        {
            var (exit, stdout, stderr) = RunCli("new", "dual", "ArcaneCompass", "--out", tempDir);
            Assert.Equal(NativeModsCliApp.ExitSuccess, exit);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains("arcane-compass", stdout, StringComparison.Ordinal);

            string uiPath = Path.Combine(tempDir, "client", "Metadata", "UI", "Mods", "arcane-compass", "Panel.ui");
            Assert.True(File.Exists(Path.Combine(tempDir, "mod.json")));
            Assert.True(File.Exists(Path.Combine(tempDir, "settings.json")));
            Assert.True(File.Exists(Path.Combine(tempDir, "README.md")));
            Assert.True(File.Exists(Path.Combine(tempDir, "Shared", "DualMod.ArcaneCompass.Shared.csproj")));
            Assert.True(File.Exists(Path.Combine(tempDir, "Shared", "ArcaneCompassMessages.cs")));
            Assert.True(File.Exists(Path.Combine(tempDir, "Client", "DualMod.ArcaneCompass.Client.csproj")));
            Assert.True(File.Exists(Path.Combine(tempDir, "Client", "ArcaneCompassClientMod.cs")));
            Assert.True(File.Exists(Path.Combine(tempDir, "Server", "DualMod.ArcaneCompass.Server.csproj")));
            Assert.True(File.Exists(Path.Combine(tempDir, "Server", "ArcaneCompassServerMod.cs")));
            Assert.True(File.Exists(Path.Combine(tempDir, "Server", "ArcaneCompassLocalExileEntry.cs")));
            Assert.True(File.Exists(uiPath));

            // Freshly scaffolded .ui MUST pass UiLayoutLinter with ZERO diagnostics!
            IReadOnlyList<ValidationDiagnostic> uiDiags = UiLayoutLinter.Lint(File.ReadAllText(uiPath), "Metadata/UI/Mods/arcane-compass/Panel.ui");
            Assert.Empty(uiDiags);

            // Freshly scaffolded mod.json + package MUST pass DualModPackageValidator at both 0.4.0 and 1.0.0!
            ValidationReport report040 = DualModPackageValidator.ValidatePackageDirectory(tempDir, new SemVer(0, 4, 0));
            Assert.True(report040.IsValid, FormatDiagnostics(report040.Diagnostics));
            Assert.Empty(report040.Diagnostics);

            ValidationReport report100 = DualModPackageValidator.ValidatePackageDirectory(tempDir, new SemVer(1, 0, 0));
            Assert.True(report100.IsValid, FormatDiagnostics(report100.Diagnostics));
            Assert.Empty(report100.Diagnostics);

            var (valExit, valOut, valErr) = RunCli("validate", tempDir);
            Assert.Equal(NativeModsCliApp.ExitSuccess, valExit);
            Assert.Equal(string.Empty, valErr);
            Assert.Contains("Validation VALID", valOut, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [Fact]
    public void New_Content_ScaffoldsValidDeclarativeContentModThatValidatesAndMergesEndToEnd()
    {
        string tempRoot = CreateTempDir("content-e2e");
        string modDir = Path.Combine(tempRoot, "ember-shards");
        string stockDir = Path.Combine(tempRoot, "stock");
        string outDir = Path.Combine(tempRoot, "merged-cache");
        try
        {
            var (exit, _, stderr) = RunCli("new", "content", "EmberShards", "--out", modDir);
            Assert.Equal(NativeModsCliApp.ExitSuccess, exit);
            Assert.Equal(string.Empty, stderr);

            string patchPath = Path.Combine(modDir, "client", "patches", "ember-shards.datpatch.json");
            Assert.True(File.Exists(Path.Combine(modDir, "mod.json")));
            Assert.True(File.Exists(patchPath));
            Assert.True(File.Exists(Path.Combine(modDir, "README.md")));

            ValidationReport pkgReport = DualModPackageValidator.ValidatePackageDirectory(modDir, new SemVer(0, 4, 0));
            Assert.True(pkgReport.IsValid, FormatDiagnostics(pkgReport.Diagnostics));
            Assert.Empty(pkgReport.Diagnostics);

            var (valExit, valOut, valErr) = RunCli("validate", modDir);
            Assert.Equal(NativeModsCliApp.ExitSuccess, valExit);
            Assert.Equal(string.Empty, valErr);
            Assert.Contains("1 .datpatch.json file(s) checked", valOut, StringComparison.Ordinal);

            // End-to-end merge of freshly scaffolded content mod against synthetic stock tables!
            WriteSyntheticStockDirectory(stockDir);
            var (mergeExit, mergeOut, mergeErr) = RunCli("merge", "--stock", stockDir, "--out", outDir, modDir);
            Assert.Equal(NativeModsCliApp.ExitSuccess, mergeExit);
            Assert.Equal(string.Empty, mergeErr);
            Assert.Contains("Digest:", mergeOut, StringComparison.Ordinal);

            Assert.True(File.Exists(Path.Combine(outDir, "content-row-map.json")));
            Assert.True(File.Exists(Path.Combine(outDir, "Data", "BaseItemTypes.datc64")));
            Assert.True(File.Exists(Path.Combine(outDir, "Data", "ItemVisualIdentity.datc64")));
            Assert.True(File.Exists(Path.Combine(outDir, "Data", "CurrencyItems.datc64")));
            Assert.True(File.Exists(Path.Combine(outDir, "Data", "Mods.datc64")));
            Assert.True(File.Exists(Path.Combine(outDir, "Data", "Stats.datc64")));
            Assert.True(File.Exists(Path.Combine(outDir, "Metadata", "StatDescriptions", "ember-shards_custom_stats.txt")));

            ContentRowMap rowMap = ContentRowMap.Parse(File.ReadAllText(Path.Combine(outDir, "content-row-map.json")));
            Assert.Equal(64, rowMap.Digest.Length);
            Assert.Equal(["ember-shards"], rowMap.Mods);
            Assert.Contains(rowMap.Digest, mergeOut, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    [Fact]
    public void New_RefusesNonEmptyDirectoryWithoutForce_AndSucceedsWithForce()
    {
        string tempDir = CreateTempDir("force-check");
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "existing.txt"), "keep");

            var (failExit, _, failErr) = RunCli("new", "tier1", "sample-mod", "--out", tempDir);
            Assert.Equal(NativeModsCliApp.ExitFailure, failExit);
            Assert.Contains("--force", failErr, StringComparison.Ordinal);

            var (okExit, _, okErr) = RunCli("new", "tier1", "sample-mod", "--out", tempDir, "--force");
            Assert.Equal(NativeModsCliApp.ExitSuccess, okExit);
            Assert.Equal(string.Empty, okErr);
            Assert.True(File.Exists(Path.Combine(tempDir, "mod.json")));
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [Fact]
    public void Validate_FailsWithNonZeroExitCode_OnBrokenUiAndIllegalDatPatch_AndSupportsJsonOutput()
    {
        string tempDir = CreateTempDir("validate-fail");
        try
        {
            var (newExit, _, _) = RunCli("new", "dual", "broken-mod", "--out", tempDir);
            Assert.Equal(NativeModsCliApp.ExitSuccess, newExit);

            // Corrupt Panel.ui: <DEFINITIONS> at byte 0 (UI002) + duplicate sibling id (UI016)
            string uiPath = Path.Combine(tempDir, "client", "Metadata", "UI", "Mods", "broken-mod", "Panel.ui");
            File.WriteAllText(uiPath, """
            <DEFINITIONS>
            begin Frame
                id = dup_root;
                begin Label
                    id = dup_child;
                    text = "One";
                end
                begin Label
                    id = dup_child;
                    text = "Two";
                end
            end
            """);

            // Add an illegal .datpatch.json: appends to closed Stats, requests forbidden row 464, modifies Stats.Id, unknown column, unresolved $ref
            string patchDir = Path.Combine(tempDir, "patches");
            Directory.CreateDirectory(patchDir);
            File.WriteAllText(Path.Combine(patchDir, "broken.datpatch.json"), """
            {
              "schemaVersion": 1,
              "modId": "broken-mod",
              "allocateDeadStat": [
                {
                  "key": "bad_slot",
                  "preferredSlot": 464
                }
              ],
              "tables": {
                "Stats": {
                  "append": [
                    { "key": "illegal_append", "fields": { "Id": "nope" } }
                  ],
                  "modify": [
                    { "rowIndex": 99, "fields": { "Id": "breaks_checksum" } }
                  ]
                },
                "CurrencyItems": {
                  "append": [
                    {
                      "key": "bad_cur",
                      "fields": {
                        "BaseItemTypesKey": "$ref:BaseItemTypes:missing_key",
                        "NotARealColumn": 123
                      }
                    }
                  ]
                }
              }
            }
            """);

            var (exit, stdout, stderr) = RunCli("validate", tempDir);
            Assert.Equal(NativeModsCliApp.ExitFailure, exit);
            Assert.Contains("Validation FAILED", stdout, StringComparison.Ordinal);
            Assert.Contains("UI002", stderr, StringComparison.Ordinal);
            Assert.Contains("UI016", stderr, StringComparison.Ordinal);
            Assert.Contains("DAT006", stderr, StringComparison.Ordinal);
            Assert.Contains("DAT010", stderr, StringComparison.Ordinal);
            Assert.Contains("DAT019", stderr, StringComparison.Ordinal);
            Assert.Contains("DAT020", stderr, StringComparison.Ordinal);
            Assert.Contains("DAT025", stderr, StringComparison.Ordinal);

            // Verify structured --json output
            var (jsonExit, jsonOut, _) = RunCli("validate", tempDir, "--json");
            Assert.Equal(NativeModsCliApp.ExitFailure, jsonExit);
            using JsonDocument doc = JsonDocument.Parse(jsonOut);
            Assert.False(doc.RootElement.GetProperty("valid").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("errorCount").GetInt32() >= 6);
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [Fact]
    public void Validate_Against_PassesForCompatibleMods_AndDetectsCrossModDatPatchConflicts()
    {
        string tempRoot = CreateTempDir("validate-against");
        string modA = Path.Combine(tempRoot, "mod-a");
        string modB = Path.Combine(tempRoot, "mod-b");
        string modConflict = Path.Combine(tempRoot, "mod-conflict");
        try
        {
            Assert.Equal(0, RunCli("new", "content", "mod-a", "--out", modA).ExitCode);
            Assert.Equal(0, RunCli("new", "content", "mod-b", "--out", modB).ExitCode);

            // Compatible mods pass --against with 0 errors and 0 conflicts.
            var (okExit, okOut, okErr) = RunCli("validate", modA, "--against", modB);
            Assert.Equal(NativeModsCliApp.ExitSuccess, okExit);
            Assert.Equal(string.Empty, okErr);
            Assert.Contains("0 conflict(s)", okOut, StringComparison.Ordinal);

            // Create a conflicting mod sharing the exact same append key & dead-stat key as mod-a.
            Assert.Equal(0, RunCli("new", "content", "mod-conflict", "--out", modConflict).ExitCode);
            string conflictPatch = Path.Combine(modConflict, "client", "patches", "mod-conflict.datpatch.json");
            File.WriteAllText(conflictPatch, """
            {
              "schemaVersion": 1,
              "modId": "mod-conflict",
              "allocateDeadStat": [
                {
                  "key": "mod_a_power_pct",
                  "description": { "template": "{0:+d} Duplicate Key" }
                }
              ],
              "tables": {
                "ItemVisualIdentity": {
                  "append": [
                    { "key": "mod_a_orb_visual", "cloneFromRow": 0, "fields": { "Id": "ConflictVis" } }
                  ]
                }
              }
            }
            """);

            var (badExit, _, badErr) = RunCli("validate", modConflict, "--against", modA);
            Assert.Equal(NativeModsCliApp.ExitFailure, badExit);
            Assert.Contains("DPF002", badErr, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    [Fact]
    public void Merge_MultiModOrderIndependent_ProducesDeterministicDigestAndJsonSummary()
    {
        string tempRoot = CreateTempDir("cli-merge-order");
        string stockDir = Path.Combine(tempRoot, "stock");
        string modA = Path.Combine(tempRoot, "mod-alpha");
        string modB = Path.Combine(tempRoot, "mod-beta");
        string out1 = Path.Combine(tempRoot, "out-ab");
        string out2 = Path.Combine(tempRoot, "out-ba");
        try
        {
            WriteSyntheticStockDirectory(stockDir);
            Assert.Equal(0, RunCli("new", "content", "mod-alpha", "--out", modA).ExitCode);
            Assert.Equal(0, RunCli("new", "content", "mod-beta", "--out", modB).ExitCode);

            var (exit1, stdout1, stderr1) = RunCli("merge", "--stock", stockDir, "--out", out1, "--json", modA, modB);
            var (exit2, stdout2, stderr2) = RunCli("merge", "--stock", stockDir, "--out", out2, "--json", modB, modA);

            Assert.Equal(NativeModsCliApp.ExitSuccess, exit1);
            Assert.Equal(NativeModsCliApp.ExitSuccess, exit2);
            Assert.Equal(string.Empty, stderr1);
            Assert.Equal(string.Empty, stderr2);

            using JsonDocument j1 = JsonDocument.Parse(stdout1);
            using JsonDocument j2 = JsonDocument.Parse(stdout2);
            string digest1 = j1.RootElement.GetProperty("digest").GetString()!;
            string digest2 = j2.RootElement.GetProperty("digest").GetString()!;

            Assert.Equal(64, digest1.Length);
            Assert.Equal(digest1, digest2);

            ContentRowMap map1 = ContentRowMap.Parse(File.ReadAllText(Path.Combine(out1, "content-row-map.json")));
            ContentRowMap map2 = ContentRowMap.Parse(File.ReadAllText(Path.Combine(out2, "content-row-map.json")));
            Assert.Equal(digest1, map1.Digest);
            Assert.Equal(map1.Digest, map2.Digest);
            Assert.Equal(["mod-alpha", "mod-beta"], map1.Mods);
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    [Fact]
    public void Cli_HelpVersionAndUsageErrors_ReturnExpectedExitCodes()
    {
        var (helpExit, helpOut, _) = RunCli("--help");
        Assert.Equal(NativeModsCliApp.ExitSuccess, helpExit);
        Assert.Contains("nativemods new tier1", helpOut, StringComparison.Ordinal);
        Assert.Contains("nativemods validate", helpOut, StringComparison.Ordinal);
        Assert.Contains("nativemods merge", helpOut, StringComparison.Ordinal);

        var (verExit, verOut, _) = RunCli("--version");
        Assert.Equal(NativeModsCliApp.ExitSuccess, verExit);
        Assert.Contains("nativemods", verOut, StringComparison.Ordinal);

        var (unknownExit, _, unknownErr) = RunCli("unknown-cmd");
        Assert.Equal(NativeModsCliApp.ExitUsageError, unknownExit);
        Assert.Contains("Unknown command", unknownErr, StringComparison.Ordinal);

        var (badTemplateExit, _, badTemplateErr) = RunCli("new", "tier99", "foo");
        Assert.Equal(NativeModsCliApp.ExitUsageError, badTemplateExit);
        Assert.Contains("Unknown template", badTemplateErr, StringComparison.Ordinal);

        var (missingStockExit, _, missingStockErr) = RunCli("merge", "--out", "/tmp/x", "foo");
        Assert.Equal(NativeModsCliApp.ExitUsageError, missingStockExit);
        Assert.Contains("--stock", missingStockErr, StringComparison.Ordinal);
    }

    private static (int ExitCode, string Stdout, string Stderr) RunCli(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int exit = NativeModsCliApp.Run(args, stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static string CreateTempDir(string suffix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"nativemods-cli-tests-{suffix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTempDir(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string FormatDiagnostics(IReadOnlyList<ValidationDiagnostic> diags) =>
        string.Join(Environment.NewLine, diags.Select(d => $"{d.Severity} {d.Code} ({d.FilePath}): {d.Message}"));

    private static void WriteSyntheticStockDirectory(string stockDir)
    {
        string dataDir = Path.Combine(stockDir, "Data");
        Directory.CreateDirectory(dataDir);

        File.WriteAllBytes(Path.Combine(dataDir, "ItemVisualIdentity.datc64"), BuildStockItemVisualIdentity());
        File.WriteAllBytes(Path.Combine(dataDir, "BaseItemTypes.datc64"), BuildStockBaseItemTypes());
        File.WriteAllBytes(Path.Combine(dataDir, "CurrencyItems.datc64"), BuildStockCurrencyItems());
        File.WriteAllBytes(Path.Combine(dataDir, "Mods.datc64"), BuildStockMods());
        File.WriteAllBytes(Path.Combine(dataDir, "Stats.datc64"), BuildStockStats23362());
    }

    private static byte[] BuildStockItemVisualIdentity()
    {
        using var varMs = new MemoryStream();
        WriteSentinel(varMs);
        ulong idOff = AppendStr(varMs, "CurrencyRemoveMod");
        ulong ddsOff = AppendStr(varMs, "Art/2DItems/Currency/AnnullOrb.dds");
        ulong aoOff = AppendStr(varMs, "Metadata/Items/Currency/CurrencyOrb.ao");

        byte[] row = new byte[CurrencyDatPatcher.ItemVisualIdentityRowStride];
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(0, 8), idOff);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(8, 8), ddsOff);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(16, 8), aoOff);
        Dat64TablePatcher.WriteForeignRow(row, 24, 33UL);
        Dat64TablePatcher.WriteUInt16(row, 40, 32037);
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

        byte[] row = new byte[CurrencyDatPatcher.CurrencyItemsRowStride];
        Dat64TablePatcher.WriteForeignRow(row, 0, 0UL);
        Dat64TablePatcher.WriteInt32(row, 16, 20);
        Dat64TablePatcher.WriteInt32(row, 20, 2);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(24, 8), actOff);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(32, 8), dirOff);
        Dat64TablePatcher.WriteForeignRow(row, 40, null);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(56, 8), descOff);
        Dat64TablePatcher.WriteInt32(row, 129, 10000);
        return new Dat64Table(1, CurrencyDatPatcher.CurrencyItemsRowStride, row, varMs.ToArray()).Serialize();
    }

    private static byte[] BuildStockMods()
    {
        using var varMs = new MemoryStream();
        WriteSentinel(varMs);
        ulong idOff = AppendStr(varMs, "Strength1");
        ulong nameOff = AppendStr(varMs, "of the Brute");

        byte[] row = new byte[654];
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(0, 8), idOff);
        Dat64TablePatcher.WriteUInt16(row, 8, 22100);
        Dat64TablePatcher.WriteForeignRow(row, 10, 0UL);
        Dat64TablePatcher.WriteInt32(row, 26, 1);
        Dat64TablePatcher.WriteForeignRow(row, 30, 0UL);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(98, 8), nameOff);
        Dat64TablePatcher.WriteUInt32(row, 613, PoeHashing.Hash32("Strength1"));
        return new Dat64Table(1, 654, row, varMs.ToArray()).Serialize();
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
}
