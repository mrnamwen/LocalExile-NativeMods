using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using LocalExile.Client.AssetPipeline.Dat64;
using LocalExile.Client.Cli;
using LocalExile.Client.Waypoint;
using Xunit;

namespace DualMod.ContentPackDemo.Tests;

public sealed class ContentPackDemoTests
{
    private const string DefaultLiveStockClientDir = "/home/nam/Git/LocalExile/Client";

    [Fact]
    public void BothShowcaseMods_PassPackageValidationAndCrossModConflictCheckWithZeroDiagnostics()
    {
        (string alphaDir, string betaDir) = ResolveShowcaseModDirs();

        // Both mods MUST pass DualModPackageValidator at both 0.4.0 and 1.0.0 with 0 errors and 0 warnings.
        foreach (SemVer sdkVer in new[] { new SemVer(0, 4, 0), new SemVer(1, 0, 0) })
        {
            ValidationReport alphaReport = DualModPackageValidator.ValidatePackageDirectory(alphaDir, sdkVer);
            Assert.True(alphaReport.IsValid, FormatDiagnostics(alphaReport.Diagnostics));
            Assert.Empty(alphaReport.Diagnostics);

            ValidationReport betaReport = DualModPackageValidator.ValidatePackageDirectory(betaDir, sdkVer);
            Assert.True(betaReport.IsValid, FormatDiagnostics(betaReport.Diagnostics));
            Assert.Empty(betaReport.Diagnostics);
        }

        DualModManifest alphaManifest = DualModManifest.LoadFromFile(Path.Combine(alphaDir, "mod.json"));
        DualModManifest betaManifest = DualModManifest.LoadFromFile(Path.Combine(betaDir, "mod.json"));
        Assert.Empty(DualModPackageValidator.DetectConflicts([alphaManifest, betaManifest]));

        // Also verify via nativemods validate --against (both directions)!
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int exit = NativeModsCliApp.Run(["validate", alphaDir, "--against", betaDir], stdout, stderr);
        Assert.Equal(NativeModsCliApp.ExitSuccess, exit);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.Contains("0 error(s), 0 warning(s), 0 conflict(s)", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Merge_ModAlphaAndModBetaTogether_SinglePassZeroConflictsDistinctRowsHashesDeadStatsAndStack65535()
    {
        (string alphaDir, string betaDir) = ResolveShowcaseModDirs();
        DatPatchDocument alphaPatch = DatPatchDocument.LoadFromFile(
            Path.Combine(alphaDir, "client", "patches", "aether-relics.datpatch.json"));
        DatPatchDocument betaPatch = DatPatchDocument.LoadFromFile(
            Path.Combine(betaDir, "client", "patches", "stack-mastery.datpatch.json"));

        InMemoryDat64Source stock = BuildSyntheticStockSource();
        Dat64SchemaRegistry schema = Dat64SchemaRegistry.LoadDefault329();

        string tempCacheDir = CreateTempDir("synthetic-merge");
        try
        {
            // Merge in BOTH orders ([alpha, beta] and [beta, alpha]) -> deterministic digest & bytes!
            DatPatchMergeResult resAB = DatPatchMerger.Merge(stock, [alphaPatch, betaPatch], schema, tempCacheDir);
            DatPatchMergeResult resBA = DatPatchMerger.Merge(stock, [betaPatch, alphaPatch], schema);

            Assert.Equal(resAB.RowMap.Digest, resBA.RowMap.Digest);
            Assert.Equal(64, resAB.RowMap.Digest.Length);
            Assert.Equal(["aether-relics", "stack-mastery"], resAB.RowMap.Mods);

            // Verify merged cache on disk
            string rowMapPath = Path.Combine(tempCacheDir, "content-row-map.json");
            Assert.True(File.Exists(rowMapPath));
            ContentRowMap reloaded = ContentRowMap.Parse(File.ReadAllText(rowMapPath));
            Assert.Equal(resAB.RowMap.Digest, reloaded.Digest);

            // All 5 shared tables touched cleanly in a single pass
            Dat64Table bit = Dat64Table.Parse(resAB.MergedTables["BaseItemTypes"]);
            Dat64Table ivi = Dat64Table.Parse(resAB.MergedTables["ItemVisualIdentity"]);
            Dat64Table cur = Dat64Table.Parse(resAB.MergedTables["CurrencyItems"]);
            Dat64Table mods = Dat64Table.Parse(resAB.MergedTables["Mods"]);
            Dat64Table stats = Dat64Table.Parse(resAB.MergedTables["Stats"]);

            // Distinct non-overlapping appended row counts (+2 on each appendable table; Stats strictly unchanged at 23,362!)
            Assert.Equal(3, bit.RowCount);
            Assert.Equal(3, ivi.RowCount);
            Assert.Equal(3, cur.RowCount);
            Assert.Equal(3, mods.RowCount);
            Assert.Equal(Dat64SchemaRegistry.StockStatsRowCount329, stats.RowCount);

            // Distinct non-overlapping row indices across ModAlpha & ModBeta
            ContentRowMapEntry alphaBit = resAB.RowMap.Tables["BaseItemTypes"].Entries["aether_shard"];
            ContentRowMapEntry betaBit = resAB.RowMap.Tables["BaseItemTypes"].Entries["stack_catalyst"];
            ContentRowMapEntry alphaIvi = resAB.RowMap.Tables["ItemVisualIdentity"].Entries["aether_shard_visual"];
            ContentRowMapEntry betaIvi = resAB.RowMap.Tables["ItemVisualIdentity"].Entries["stack_catalyst_visual"];
            ContentRowMapEntry alphaCur = resAB.RowMap.Tables["CurrencyItems"].Entries["aether_shard_currency"];
            ContentRowMapEntry betaCur = resAB.RowMap.Tables["CurrencyItems"].Entries["stack_catalyst_currency"];
            ContentRowMapEntry alphaMod = resAB.RowMap.Tables["Mods"].Entries["aether_relic_unique_mod"];
            ContentRowMapEntry betaMod = resAB.RowMap.Tables["Mods"].Entries["stack_mastery_boon"];

            Assert.NotEqual(alphaBit.RowIndex, betaBit.RowIndex);
            Assert.NotEqual(alphaIvi.RowIndex, betaIvi.RowIndex);
            Assert.NotEqual(alphaCur.RowIndex, betaCur.RowIndex);
            Assert.NotEqual(alphaMod.RowIndex, betaMod.RowIndex);

            // Cross-table $ref wiring (ItemVisualIdentity -> BaseItemTypes -> CurrencyItems, Mods -> BaseItemTypes.Implicit_ModsKeys)
            Assert.Equal((ulong)alphaIvi.RowIndex, bit.ReadForeignRow(alphaBit.RowIndex, CurrencyDatPatcher.BitItemVisualIdentityOffset));
            Assert.Equal((ulong)betaIvi.RowIndex, bit.ReadForeignRow(betaBit.RowIndex, CurrencyDatPatcher.BitItemVisualIdentityOffset));
            Assert.Equal([(ulong)alphaMod.RowIndex], bit.ReadForeignRowArray(alphaBit.RowIndex, CurrencyDatPatcher.BitImplicitModsOffset));
            Assert.Equal([(ulong)betaMod.RowIndex], bit.ReadForeignRowArray(betaBit.RowIndex, CurrencyDatPatcher.BitImplicitModsOffset));
            Assert.Equal((ulong)alphaBit.RowIndex, cur.ReadForeignRow(alphaCur.RowIndex, CurrencyDatPatcher.CurBaseItemTypesKeyOffset));
            Assert.Equal((ulong)betaBit.RowIndex, cur.ReadForeignRow(betaCur.RowIndex, CurrencyDatPatcher.CurBaseItemTypesKeyOffset));

            // Unique HASH32 (matching PoeHashing.Hash32) on BaseItemTypes and Mods
            uint expectedAlphaBitHash = PoeHashing.Hash32("Metadata/Items/Currency/Mods/aether-relics/AetherShard");
            uint expectedBetaBitHash = PoeHashing.Hash32("Metadata/Items/Currency/Mods/stack-mastery/StackCatalyst");
            Assert.Equal(expectedAlphaBitHash, bit.ReadUInt32(alphaBit.RowIndex, CurrencyDatPatcher.BitHash32Offset));
            Assert.Equal(expectedBetaBitHash, bit.ReadUInt32(betaBit.RowIndex, CurrencyDatPatcher.BitHash32Offset));
            Assert.NotEqual(expectedAlphaBitHash, expectedBetaBitHash);

            uint expectedAlphaModHash = PoeHashing.Hash32("AetherRelicUniqueMod1");
            uint expectedBetaModHash = PoeHashing.Hash32("StackMasteryBoon1");
            Assert.Equal(expectedAlphaModHash, mods.ReadUInt32(alphaMod.RowIndex, 613));
            Assert.Equal(expectedBetaModHash, mods.ReadUInt32(betaMod.RowIndex, 613));
            Assert.NotEqual(expectedAlphaModHash, expectedBetaModHash);

            // Unique non-zero HASH16 on ItemVisualIdentity and Mods
            ushort stockIviH16 = ivi.ReadUInt16(0, CurrencyDatPatcher.IviHash16Offset);
            ushort alphaIviH16 = ivi.ReadUInt16(alphaIvi.RowIndex, CurrencyDatPatcher.IviHash16Offset);
            ushort betaIviH16 = ivi.ReadUInt16(betaIvi.RowIndex, CurrencyDatPatcher.IviHash16Offset);
            Assert.Equal(3, new HashSet<ushort> { stockIviH16, alphaIviH16, betaIviH16 }.Count);
            Assert.NotEqual((ushort)0, alphaIviH16);
            Assert.NotEqual((ushort)0, betaIviH16);

            ushort stockModH16 = mods.ReadUInt16(0, 8);
            ushort alphaModH16 = mods.ReadUInt16(alphaMod.RowIndex, 8);
            ushort betaModH16 = mods.ReadUInt16(betaMod.RowIndex, 8);
            Assert.Equal(3, new HashSet<ushort> { stockModH16, alphaModH16, betaModH16 }.Count);

            // Zero foreignrow[] / row[] pointer-array aliasing across every merged table!
            DatPatchMerger.VerifyNoPointerArrayAliasing(schema.GetRequiredTable("BaseItemTypes"), bit, stockRowCount: 1);
            DatPatchMerger.VerifyNoPointerArrayAliasing(schema.GetRequiredTable("ItemVisualIdentity"), ivi, stockRowCount: 1);
            DatPatchMerger.VerifyNoPointerArrayAliasing(schema.GetRequiredTable("CurrencyItems"), cur, stockRowCount: 1);
            DatPatchMerger.VerifyNoPointerArrayAliasing(schema.GetRequiredTable("Mods"), mods, stockRowCount: 1);

            // Two distinct dead Stats rows allocated (99 & 101, never 464), preserving stock Stats.Id & HASH32 checksum!
            ContentRowMapEntry alphaStat = resAB.RowMap.Tables["Stats"].Entries["aether_resonance_pct"];
            ContentRowMapEntry betaStat = resAB.RowMap.Tables["Stats"].Entries["stack_mastery_drop_qty_pct"];
            Assert.Equal(99, alphaStat.RowIndex);
            Assert.Equal(101, betaStat.RowIndex);
            Assert.NotEqual(alphaStat.RowIndex, betaStat.RowIndex);
            Assert.NotEqual(464, alphaStat.RowIndex);
            Assert.NotEqual(464, betaStat.RowIndex);

            Assert.Equal("old_do_not_use_slot_99", alphaStat.StockId);
            Assert.Equal("old_do_not_use_slot_101", betaStat.StockId);
            Assert.Equal("old_do_not_use_slot_99", stats.ReadString(99, 0));
            Assert.Equal(PoeHashing.Hash32("old_do_not_use_slot_99"), stats.ReadUInt32(99, 34));
            Assert.Equal("old_do_not_use_slot_101", stats.ReadString(101, 0));
            Assert.Equal(PoeHashing.Hash32("old_do_not_use_slot_101"), stats.ReadUInt32(101, 34));

            // Mods.StatsKey1 wired to each mod's allocated dead stat via $ref:Stats:<key>
            Assert.Equal(99UL, mods.ReadForeignRow(alphaMod.RowIndex, 30));
            Assert.Equal(101UL, mods.ReadForeignRow(betaMod.RowIndex, 30));

            // UTF-16LE BOM + CRLF StatDescriptions overlays generated for BOTH mods
            AssertUtf16LeBomCrlfOverlay(
                resAB.GeneratedOverlays,
                "Metadata/StatDescriptions/aether-relics_custom_stats.txt",
                "old_do_not_use_slot_99",
                "{0:+d}% increased Aether Resonance");
            AssertUtf16LeBomCrlfOverlay(
                resAB.GeneratedOverlays,
                "Metadata/StatDescriptions/stack-mastery_custom_stats.txt",
                "old_do_not_use_slot_101",
                "{0:+d}% increased Stacked Currency Quantity");

            // In-place modify raised stock currency StackSize == 65535 (Wishlist #15 <= 65,535)!
            Assert.Equal(65535, cur.ReadInt32(0, CurrencyDatPatcher.CurStackSizeOffset));
            Assert.Equal(65535, cur.ReadInt32(0, CurrencyDatPatcher.CurCurrencyTabStackSizeOffset));
            Assert.Equal(65535, cur.ReadInt32(betaCur.RowIndex, CurrencyDatPatcher.CurStackSizeOffset));
            Assert.Equal(20, cur.ReadInt32(alphaCur.RowIndex, CurrencyDatPatcher.CurStackSizeOffset));
        }
        finally
        {
            DeleteTempDir(tempCacheDir);
        }
    }

    [Fact]
    public void Merge_LiveBundles2OrSyntheticFallback_CliEndToEndSmokeTest()
    {
        (string alphaDir, string betaDir) = ResolveShowcaseModDirs();
        string tempRoot = CreateTempDir("live-or-fallback");
        string outDir = Path.Combine(tempRoot, "merged-cache");
        try
        {
            string? liveBundles2Dir = ResolveLiveBundles2Dir();
            bool isLive = liveBundles2Dir is not null;
            string stockArg;

            if (isLive)
            {
                // Pass Bundles2 directly to also exercise ResolveStockRootDirectory!
                stockArg = liveBundles2Dir!;
            }
            else
            {
                stockArg = Path.Combine(tempRoot, "synthetic-stock");
                WriteSyntheticStockDirectory(stockArg);
            }

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            int exit = NativeModsCliApp.Run(
                ["merge", "--stock", stockArg, "--out", outDir, alphaDir, betaDir],
                stdout,
                stderr);

            Assert.Equal(NativeModsCliApp.ExitSuccess, exit);
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.Contains("Digest:", stdout.ToString(), StringComparison.Ordinal);

            ContentRowMap rowMap = ContentRowMap.Parse(File.ReadAllText(Path.Combine(outDir, "content-row-map.json")));
            Assert.Equal(64, rowMap.Digest.Length);
            Assert.Equal(["aether-relics", "stack-mastery"], rowMap.Mods);

            // Verify exact +2 growth on the 4 appendable tables and strict closed row count on Stats!
            Assert.Equal(rowMap.Tables["BaseItemTypes"].StockRowCount + 2, rowMap.Tables["BaseItemTypes"].MergedRowCount);
            Assert.Equal(rowMap.Tables["ItemVisualIdentity"].StockRowCount + 2, rowMap.Tables["ItemVisualIdentity"].MergedRowCount);
            Assert.Equal(rowMap.Tables["CurrencyItems"].StockRowCount + 2, rowMap.Tables["CurrencyItems"].MergedRowCount);
            Assert.Equal(rowMap.Tables["Mods"].StockRowCount + 2, rowMap.Tables["Mods"].MergedRowCount);
            Assert.Equal(Dat64SchemaRegistry.StockStatsRowCount329, rowMap.Tables["Stats"].StockRowCount);
            Assert.Equal(Dat64SchemaRegistry.StockStatsRowCount329, rowMap.Tables["Stats"].MergedRowCount);

            if (isLive)
            {
                // Live 3.29.3d stock row counts verified against real Bundles2
                Assert.True(rowMap.Tables["BaseItemTypes"].StockRowCount > 5000);
                Assert.True(rowMap.Tables["ItemVisualIdentity"].StockRowCount > 15000);
                Assert.True(rowMap.Tables["CurrencyItems"].StockRowCount > 1900);
                Assert.True(rowMap.Tables["Mods"].StockRowCount > 40000);
            }

            // Verify stock CurrencyRemoveMod StackSize == 65535 on the merged table on disk
            Dat64Table mergedBit = Dat64Table.Parse(File.ReadAllBytes(Path.Combine(outDir, "Data", "BaseItemTypes.datc64")));
            Dat64Table mergedCur = Dat64Table.Parse(File.ReadAllBytes(Path.Combine(outDir, "Data", "CurrencyItems.datc64")));
            int annulBitRow = mergedBit.FindRowByString(CurrencyDatPatcher.BitIdOffset, "Metadata/Items/Currency/CurrencyRemoveMod", StringComparison.OrdinalIgnoreCase);
            Assert.True(annulBitRow >= 0);

            int annulCurRow = -1;
            for (int r = 0; r < mergedCur.RowCount; r++)
            {
                if (mergedCur.ReadForeignRow(r, CurrencyDatPatcher.CurBaseItemTypesKeyOffset) == (ulong)annulBitRow)
                {
                    annulCurRow = r;
                    break;
                }
            }

            Assert.True(annulCurRow >= 0);
            Assert.Equal(65535, mergedCur.ReadInt32(annulCurRow, CurrencyDatPatcher.CurStackSizeOffset));
            Assert.Equal(65535, mergedCur.ReadInt32(annulCurRow, CurrencyDatPatcher.CurCurrencyTabStackSizeOffset));

            // Both Route A StatDescriptions files emitted on disk with UTF-16LE BOM + CRLF
            byte[] alphaOv = File.ReadAllBytes(Path.Combine(outDir, "Metadata", "StatDescriptions", "aether-relics_custom_stats.txt"));
            byte[] betaOv = File.ReadAllBytes(Path.Combine(outDir, "Metadata", "StatDescriptions", "stack-mastery_custom_stats.txt"));
            Assert.True(alphaOv.Length >= 2 && alphaOv[0] == 0xFF && alphaOv[1] == 0xFE);
            Assert.True(betaOv.Length >= 2 && betaOv[0] == 0xFF && betaOv[1] == 0xFE);

            string alphaText = Encoding.Unicode.GetString(alphaOv, 2, alphaOv.Length - 2);
            string betaText = Encoding.Unicode.GetString(betaOv, 2, betaOv.Length - 2);
            Assert.Contains(rowMap.Tables["Stats"].Entries["aether_resonance_pct"].StockId!, alphaText, StringComparison.Ordinal);
            Assert.Contains(rowMap.Tables["Stats"].Entries["stack_mastery_drop_qty_pct"].StockId!, betaText, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    private static string? ResolveLiveBundles2Dir()
    {
        string? envDir = Environment.GetEnvironmentVariable("LOCALEXILE_STOCK_CLIENT_DIR");
        string[] candidates = string.IsNullOrWhiteSpace(envDir)
            ? [Path.Combine(DefaultLiveStockClientDir, "Bundles2")]
            : [envDir, Path.Combine(envDir, "Bundles2"), Path.Combine(DefaultLiveStockClientDir, "Bundles2")];

        foreach (string dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, "_.index.bin")))
            {
                return dir;
            }
        }

        return null;
    }

    private static (string AlphaDir, string BetaDir) ResolveShowcaseModDirs()
    {
        string repoRoot = FindRepoRoot();
        string demoRoot = Path.Combine(repoRoot, "examples", "DualMod.ContentPackDemo");
        return (
            Path.Combine(demoRoot, "ModAlpha.AetherRelics"),
            Path.Combine(demoRoot, "ModBeta.StackMastery"));
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NativeMods.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate NativeMods.slnx walking up from AppContext.BaseDirectory.");
    }

    private static void AssertUtf16LeBomCrlfOverlay(
        IReadOnlyDictionary<string, byte[]> overlays,
        string virtualPath,
        string expectedStockId,
        string expectedTemplate)
    {
        Assert.True(overlays.TryGetValue(virtualPath, out byte[]? bytes), $"Missing generated overlay '{virtualPath}'.");
        Assert.NotNull(bytes);
        Assert.True(bytes!.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE, $"Overlay '{virtualPath}' must have UTF-16LE BOM.");

        string text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        Assert.Contains($"description\r\n\t1 {expectedStockId}\r\n", text, StringComparison.Ordinal);
        Assert.Contains(expectedTemplate, text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    private static string CreateTempDir(string suffix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"nativemods-contentpack-{suffix}-{Guid.NewGuid():N}");
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

        InMemoryDat64Source stock = BuildSyntheticStockSource();
        foreach (string tbl in new[] { "ItemVisualIdentity", "BaseItemTypes", "CurrencyItems", "Mods", "Stats" })
        {
            Assert.True(stock.TryReadTable(tbl, out byte[] bytes));
            File.WriteAllBytes(Path.Combine(dataDir, $"{tbl}.datc64"), bytes);
        }
    }

    private static InMemoryDat64Source BuildSyntheticStockSource() =>
        new(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["ItemVisualIdentity"] = BuildStockItemVisualIdentity(),
            ["BaseItemTypes"] = BuildStockBaseItemTypes(),
            ["CurrencyItems"] = BuildStockCurrencyItems(),
            ["Mods"] = BuildStockMods(),
            ["Stats"] = BuildStockStats23362(),
        });

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
