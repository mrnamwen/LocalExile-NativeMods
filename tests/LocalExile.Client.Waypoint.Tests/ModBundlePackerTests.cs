using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalExile.Client.Host;
using LocalExile.Client.Host.Native;
using LocalExile.Client.Waypoint;
using Xunit;

namespace LocalExile.Client.Waypoint.Tests;

public sealed class ModBundlePackerTests
{
    [Fact]
    public void PackVerifyReadAndUnpack_IsDeterministicAndRoundTripsMultiBlockFiles()
    {
        string work = Path.Combine(Path.GetTempPath(), "nm-bundle-test-" + Guid.NewGuid().ToString("N"));
        string src = Path.Combine(work, "client");
        string outA = Path.Combine(work, "outA");
        string outB = Path.Combine(work, "outB");
        string unpacked = Path.Combine(work, "unpacked");

        Directory.CreateDirectory(Path.Combine(src, "Metadata", "UI", "Mods", "Demo"));
        Directory.CreateDirectory(Path.Combine(src, "Art", "2DArt", "Mods", "Demo"));

        try
        {
            string uiText = "// demo\n<DEFINITIONS>\nbegin Frame\n\tid = demo_win;\nend\n";
            File.WriteAllText(Path.Combine(src, "Metadata", "UI", "Mods", "Demo", "Panel.ui"), uiText, new UTF8Encoding(false));

            // Binary file larger than one 0x40000 (256 KiB) Oodle stored block to prove multi-block packing/unpacking
            byte[] largeBinary = new byte[ModBundlePacker.StoredBlockSize + 12345];
            new Random(42).NextBytes(largeBinary);
            File.WriteAllBytes(Path.Combine(src, "Art", "2DArt", "Mods", "Demo", "Atlas.dds"), largeBinary);

            var packedA = ModBundlePacker.Pack("demo-mod", "1.0.0", src, outA);
            var packedB = ModBundlePacker.Pack("demo-mod", "1.0.0", src, outB);

            // Deterministic filename, bundle SHA-256, and exact byte equality
            Assert.Equal(packedA.Manifest.BundleFile, packedB.Manifest.BundleFile);
            Assert.Equal(packedA.Manifest.BundleSha256, packedB.Manifest.BundleSha256);
            byte[] rawBundleBytes = File.ReadAllBytes(packedA.BundlePath);
            Assert.Equal(rawBundleBytes, File.ReadAllBytes(packedB.BundlePath));
            // Verify NMB1 header + Oodle stored block magic (0xCC 0x06) immediately after 16B header + 4B block length
            Assert.Equal("NMB1"u8.ToArray(), rawBundleBytes[..4]);
            Assert.Equal(0xCC, rawBundleBytes[20]);
            Assert.Equal(0x06, rawBundleBytes[21]);

            // Multi-block count check
            var binEntry = Assert.Single(packedA.Manifest.Files, f => f.VirtualPath.EndsWith("atlas.dds", StringComparison.Ordinal));
            Assert.Equal(2, binEntry.BlockCount);
            Assert.Equal("binary", binEntry.Encoding);

            // Text file encoded to UTF-16LE BOM CRLF
            var uiEntry = Assert.Single(packedA.Manifest.Files, f => f.VirtualPath.EndsWith("panel.ui", StringComparison.Ordinal));
            Assert.Equal("utf-16le-bom-crlf", uiEntry.Encoding);
            byte[] readUi = ModBundlePacker.ReadEntry(packedA.BundlePath, packedA.ManifestPath, "Metadata/UI/Mods/Demo/Panel.ui");
            Assert.Equal(0xFF, readUi[0]);
            Assert.Equal(0xFE, readUi[1]);
            string decodedUi = Encoding.Unicode.GetString(readUi, 2, readUi.Length - 2);
            Assert.Contains("\r\n", decodedUi);
            Assert.Contains("demo_win", decodedUi);

            // Verify passes cleanly
            var verify = ModBundlePacker.Verify(packedA.BundlePath, packedA.ManifestPath);
            Assert.True(verify.IsValid, string.Join("; ", verify.Errors));

            // Unpack restores exact bytes
            var extracted = ModBundlePacker.Unpack(packedA.BundlePath, packedA.ManifestPath, unpacked);
            Assert.Equal(2, extracted.Count);
            Assert.Equal(largeBinary, File.ReadAllBytes(Path.Combine(unpacked, "art", "2dart", "mods", "demo", "atlas.dds")));

            // Tampering a single byte must fail SHA-256 verification
            byte[] corrupted = File.ReadAllBytes(packedA.BundlePath);
            corrupted[^1] ^= 0xFF;
            File.WriteAllBytes(packedA.BundlePath, corrupted);

            var failedVerify = ModBundlePacker.Verify(packedA.BundlePath, packedA.ManifestPath);
            Assert.False(failedVerify.IsValid);
            Assert.NotEmpty(failedVerify.Errors);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void PackAndRegisterBundle_RoundTripsTextBomNoBomZeroByteAndMultiChunkThroughFileOverlayService()
    {
        string work = Path.Combine(Path.GetTempPath(), "nm-e2e-bundle-test-" + Guid.NewGuid().ToString("N"));
        string src = Path.Combine(work, "client");
        string outDir = Path.Combine(work, "out");

        Directory.CreateDirectory(Path.Combine(src, "Metadata", "UI", "Mods", "Demo"));
        Directory.CreateDirectory(Path.Combine(src, "Metadata", "Pet", "Mods", "Demo"));
        Directory.CreateDirectory(Path.Combine(src, "Art", "Models", "Mods", "Demo", "textures"));
        Directory.CreateDirectory(Path.Combine(src, "Art", "2DArt", "Mods", "Demo"));

        try
        {
            // BOM extensions (.ui, .ot)
            File.WriteAllText(
                Path.Combine(src, "Metadata", "UI", "Mods", "Demo", "Panel.ui"),
                "begin Frame\n\tid = panel;\nend\n",
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(src, "Metadata", "Pet", "Mods", "Demo", "Pet.ot"),
                "version 3\nextends \"Metadata/Pet/Pet\"\n",
                new UTF8Encoding(false));

            // No-BOM extensions (.sm, .mat, .amd): mix UTF-8, already-BOM-less UTF-16LE (including leading U+0400), and BOM-contaminated UTF-16LE
            Encoding utf16NoBom = new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
            File.WriteAllText(
                Path.Combine(src, "Art", "Models", "Mods", "Demo", "utf8.sm"),
                "version 6\nBoneGroups 0\n",
                new UTF8Encoding(false));
            File.WriteAllBytes(
                Path.Combine(src, "Art", "Models", "Mods", "Demo", "bomless.sm"),
                utf16NoBom.GetBytes("\u0400version 6\nBoneGroups 0\n"));
            File.WriteAllBytes(
                Path.Combine(src, "Art", "Models", "Mods", "Demo", "contaminated.sm"),
                [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("version 6\nBoneGroups 0\n")]);

            File.WriteAllBytes(
                Path.Combine(src, "Art", "Models", "Mods", "Demo", "textures", "mat_utf16.mat"),
                utf16NoBom.GetBytes("{\"version\":4}"));
            File.WriteAllText(
                Path.Combine(src, "Art", "Models", "Mods", "Demo", "anim_utf8.amd"),
                "version 5\n0\nBoneGroups 0\n",
                new UTF8Encoding(false));
            File.WriteAllBytes(
                Path.Combine(src, "Art", "Models", "Mods", "Demo", "anim_utf16.amd"),
                utf16NoBom.GetBytes("version 5\n0\nBoneGroups 0\n"));

            // 0-byte and multi-chunk binary files
            File.WriteAllBytes(Path.Combine(src, "Art", "2DArt", "Mods", "Demo", "empty.bin"), []);
            byte[] multiChunk = new byte[ModBundlePacker.StoredBlockSize + 12345];
            new Random(77).NextBytes(multiChunk);
            File.WriteAllBytes(Path.Combine(src, "Art", "2DArt", "Mods", "Demo", "atlas.dds"), multiChunk);

            var packed = ModBundlePacker.Pack("demo-e2e", "1.0.0", src, outDir);

            // Verify manifest encodings
            Assert.Equal("utf-16le-bom-crlf", packed.Manifest.Files.Single(f => f.VirtualPath.EndsWith("panel.ui", StringComparison.Ordinal)).Encoding);
            Assert.Equal("utf-16le-bom-crlf", packed.Manifest.Files.Single(f => f.VirtualPath.EndsWith("pet.ot", StringComparison.Ordinal)).Encoding);
            foreach (string noBomName in new[] { "utf8.sm", "bomless.sm", "contaminated.sm", "mat_utf16.mat", "anim_utf8.amd", "anim_utf16.amd" })
            {
                var entry = packed.Manifest.Files.Single(f => f.VirtualPath.EndsWith(noBomName, StringComparison.Ordinal));
                Assert.Equal("utf-16le-nobom-crlf", entry.Encoding);
            }

            // End-to-end through Host FileOverlayService.RegisterBundle
            using var bridge = new SimulatedNativeBridge();
            using var host = new ClientHostRuntime(bridge);
            using IDisposable sub = host.FileOverlayService.RegisterBundle(packed.BundlePath, packed.ManifestPath);

            // 0-byte & multi-chunk
            Assert.Empty(host.FileOverlayService.ReadAllBytes("Art/2DArt/Mods/Demo/empty.bin")!);
            Assert.Equal(multiChunk, host.FileOverlayService.ReadAllBytes("Art/2DArt/Mods/Demo/atlas.dds"));

            // BOM files (.ui, .ot)
            byte[] uiBytes = host.FileOverlayService.ReadAllBytes("Metadata/UI/Mods/Demo/Panel.ui")!;
            Assert.Equal(0xFF, uiBytes[0]);
            Assert.Equal(0xFE, uiBytes[1]);
            Assert.Equal("begin Frame\r\n\tid = panel;\r\nend\r\n", host.FileOverlayService.ReadAllText("Metadata/UI/Mods/Demo/Panel.ui"));

            byte[] otBytes = host.FileOverlayService.ReadAllBytes("Metadata/Pet/Mods/Demo/Pet.ot")!;
            Assert.Equal(0xFF, otBytes[0]);
            Assert.Equal(0xFE, otBytes[1]);
            Assert.Equal("version 3\r\nextends \"Metadata/Pet/Pet\"\r\n", host.FileOverlayService.ReadAllText("Metadata/Pet/Mods/Demo/Pet.ot"));

            // No-BOM files (.sm, .mat, .amd)
            void AssertNoBomRead(string vpath, string expectedText)
            {
                byte[]? bytes = host.FileOverlayService.ReadAllBytes(vpath);
                Assert.NotNull(bytes);
                Assert.True(bytes!.Length >= 2);
                Assert.Equal(0, bytes.Length & 1);
                Assert.False(bytes[0] == 0xFF && bytes[1] == 0xFE, $"Unexpected BOM in {vpath}");
                string? decoded = host.FileOverlayService.ReadAllText(vpath);
                Assert.Equal(expectedText, decoded);
                Assert.DoesNotContain('\0', decoded!);
            }

            AssertNoBomRead("Art/Models/Mods/Demo/utf8.sm", "version 6\r\nBoneGroups 0\r\n");
            AssertNoBomRead("Art/Models/Mods/Demo/bomless.sm", "\u0400version 6\r\nBoneGroups 0\r\n");
            AssertNoBomRead("Art/Models/Mods/Demo/contaminated.sm", "version 6\r\nBoneGroups 0\r\n");
            AssertNoBomRead("Art/Models/Mods/Demo/textures/mat_utf16.mat", "{\"version\":4}");
            AssertNoBomRead("Art/Models/Mods/Demo/anim_utf8.amd", "version 5\r\n0\r\nBoneGroups 0\r\n");
            AssertNoBomRead("Art/Models/Mods/Demo/anim_utf16.amd", "version 5\r\n0\r\nBoneGroups 0\r\n");
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void TextEncodersAndReader_StripMultipleLeadingBomsAndDecodeBomlessUtf16LeU0400()
    {
        const string doubleBomInput = "\uFEFF\uFEFFhello";

        byte[] noBomBytes = ModBundlePacker.EncodeClientTextNoBom(doubleBomInput);
        Assert.False(noBomBytes[0] == 0xFF && noBomBytes[1] == 0xFE);
        Assert.Equal("hello", Encoding.Unicode.GetString(noBomBytes));

        byte[] bomBytes = ModBundlePacker.EncodeClientText(doubleBomInput);
        Assert.Equal(0xFF, bomBytes[0]);
        Assert.Equal(0xFE, bomBytes[1]);
        Assert.Equal("hello", Encoding.Unicode.GetString(bomBytes, 2, bomBytes.Length - 2));

        string work = Path.Combine(Path.GetTempPath(), "nm-bom-read-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string doubleBomUtf16LePath = Path.Combine(work, "doublebom-le.sm");
            File.WriteAllBytes(doubleBomUtf16LePath, [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("\uFEFFhello")]);
            Assert.Equal("hello", DualModPackageValidator.ReadTextAutoEncoding(doubleBomUtf16LePath));

            string doubleBomUtf8Path = Path.Combine(work, "doublebom-utf8.sm");
            File.WriteAllBytes(doubleBomUtf8Path, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("\uFEFFhello")]);
            Assert.Equal("hello", DualModPackageValidator.ReadTextAutoEncoding(doubleBomUtf8Path));

            Encoding utf16LeNoBom = new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
            Encoding utf16BeNoBom = new UnicodeEncoding(bigEndian: true, byteOrderMark: false);

            string standaloneU0400Path = Path.Combine(work, "u0400.sm");
            File.WriteAllBytes(standaloneU0400Path, utf16LeNoBom.GetBytes("\u0400"));
            Assert.Equal("\u0400", DualModPackageValidator.ReadTextAutoEncoding(standaloneU0400Path));

            string dominantU0400Path = Path.Combine(work, "u0400a.sm");
            File.WriteAllBytes(dominantU0400Path, utf16LeNoBom.GetBytes("\u0400\u0400a"));
            Assert.Equal("\u0400\u0400a", DualModPackageValidator.ReadTextAutoEncoding(dominantU0400Path));

            string genuineBePath = Path.Combine(work, "genuine-be.sm");
            File.WriteAllBytes(genuineBePath, utf16BeNoBom.GetBytes("\u0400version 6\r\n"));
            Assert.Equal("\u0400version 6\r\n", DualModPackageValidator.ReadTextAutoEncoding(genuineBePath));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void ReadEntryVerifyAndUnpack_RejectsCorruptUncompressedSizeOffsetsChunkLenAndZeroBlock()
    {
        string work = Path.Combine(Path.GetTempPath(), "nm-bundle-corrupt-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            static byte[] BuildSingleBlockBundle(int chunkLen)
            {
                using var ms = new MemoryStream();
                Span<byte> hdr = stackalloc byte[16];
                "NMB1"u8.CopyTo(hdr[..4]);
                BinaryPrimitives.WriteInt32LittleEndian(hdr[4..8], ModBundlePacker.FormatVersion);
                BinaryPrimitives.WriteInt32LittleEndian(hdr[8..12], 1);
                BinaryPrimitives.WriteInt32LittleEndian(hdr[12..16], ModBundlePacker.StoredBlockSize);
                ms.Write(hdr);

                Span<byte> blkHdr = stackalloc byte[6];
                BinaryPrimitives.WriteInt32LittleEndian(blkHdr[..4], chunkLen);
                blkHdr[4] = 0xCC;
                blkHdr[5] = 0x06;
                ms.Write(blkHdr);
                ms.Write([0x41, 0x42, 0x43, 0x44]);
                return ms.ToArray();
            }

            byte[] normalBundle = BuildSingleBlockBundle(4);
            byte[] overflowChunkBundle = BuildSingleBlockBundle(int.MaxValue);
            string payloadSha = Convert.ToHexStringLower(SHA256.HashData([0x41, 0x42, 0x43, 0x44]));

            var cases = new (string Name, byte[] Bundle, long Offset, int UncompressedSize, int StoredSize, int BlockCount)[]
            {
                ("neg-uncompressed", normalBundle, 16, -1, 10, 1),
                ("intmax-uncompressed", normalBundle, 16, int.MaxValue, 10, 1),
                ("offset-overflow", normalBundle, long.MaxValue - 5, 4, 10, 1),
                ("chunklen-overflow", overflowChunkBundle, 16, 4, 10, 1),
                ("zero-block", normalBundle, 16, 0, 0, 0),
            };

            foreach (var (name, bundleBytes, offset, uncompressedSize, storedSize, blockCount) in cases)
            {
                string caseDir = Path.Combine(work, name);
                Directory.CreateDirectory(caseDir);
                string bundleFile = $"{name}.bundle.bin";
                string bundlePath = Path.Combine(caseDir, bundleFile);
                string manifestPath = Path.Combine(caseDir, "manifest.json");
                string unpackDir = Path.Combine(caseDir, "unpacked");

                File.WriteAllBytes(bundlePath, bundleBytes);
                var manifest = new ModBundleManifest(
                    ModBundlePacker.FormatVersion,
                    "demo-corrupt",
                    "1.0.0",
                    bundleFile,
                    Convert.ToHexStringLower(SHA256.HashData(bundleBytes)),
                    ModBundlePacker.StoredBlockSize,
                    [
                        new BundleFileEntry(
                            $"mods/demo/{name}.bin",
                            offset,
                            uncompressedSize,
                            storedSize,
                            blockCount,
                            payloadSha,
                            "binary")
                    ]);
                File.WriteAllText(
                    manifestPath,
                    JsonSerializer.Serialize(manifest, DualModManifest.SerializerOptions),
                    new UTF8Encoding(false));

                var verify = ModBundlePacker.Verify(bundlePath, manifestPath);
                Assert.False(verify.IsValid, name);
                Assert.NotEmpty(verify.Errors);
                Assert.Throws<InvalidDataException>(() => ModBundlePacker.ReadEntry(bundlePath, manifestPath, $"mods/demo/{name}.bin"));
                Assert.Throws<InvalidDataException>(() => ModBundlePacker.Unpack(bundlePath, manifestPath, unpackDir));
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void PackAndEncodeClientText_EmitsArtUiImages1TxtWithoutBom_WhileKeepingBomForOtherTxtFiles()
    {
        foreach (string variant in new[] { "art/uiimages1.txt", "Art/UIImages1.txt", "ART\\UIImages1.TXT", "/Art/UIImages1.txt" })
        {
            Assert.True(ModBundlePacker.IsClientTextExtension(variant), variant);
            Assert.True(ModBundlePacker.IsClientTextNoBomPath(variant), variant);
            Assert.True(ModBundlePacker.IsClientTextNoBomExtension(variant), variant);

            byte[] encoded = ModBundlePacker.EncodeClientText("\uFEFFArt/2DArt/UIImages/InGame/Icon 0 0 32 32\n", variant);
            Assert.True(encoded.Length >= 2 && (encoded.Length & 1) == 0, variant);
            Assert.False(encoded[0] == 0xFF && encoded[1] == 0xFE, $"Unexpected UTF-16LE BOM for {variant}");
            Assert.Equal("Art/2DArt/UIImages/InGame/Icon 0 0 32 32\r\n", Encoding.Unicode.GetString(encoded));
        }

        foreach (string otherTxt in new[] { "Metadata/Mods/Demo/notes.txt", "Art/Other/UIImages1.txt", "art/uiimages2.txt" })
        {
            Assert.True(ModBundlePacker.IsClientTextExtension(otherTxt), otherTxt);
            Assert.False(ModBundlePacker.IsClientTextNoBomPath(otherTxt), otherTxt);
            Assert.False(ModBundlePacker.IsClientTextNoBomExtension(otherTxt), otherTxt);

            byte[] encoded = ModBundlePacker.EncodeClientText("hello\nworld\n", otherTxt);
            Assert.True(encoded.Length >= 4, otherTxt);
            Assert.Equal(0xFF, encoded[0]);
            Assert.Equal(0xFE, encoded[1]);
            Assert.Equal("hello\r\nworld\r\n", Encoding.Unicode.GetString(encoded, 2, encoded.Length - 2));
        }

        string work = Path.Combine(Path.GetTempPath(), "nm-uiimages-bom-test-" + Guid.NewGuid().ToString("N"));
        string src = Path.Combine(work, "client");
        string outDir = Path.Combine(work, "out");
        Directory.CreateDirectory(Path.Combine(src, "Art", "Other"));
        Directory.CreateDirectory(Path.Combine(src, "Metadata", "Mods", "Demo"));

        try
        {
            // Seed Art/UIImages1.txt with a UTF-16LE BOM on disk to prove Pack strips it cleanly.
            File.WriteAllBytes(
                Path.Combine(src, "Art", "UIImages1.txt"),
                [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("Art/2DArt/UIImages/InGame/CustomIcon 0 0 64 64\n")]);
            File.WriteAllText(
                Path.Combine(src, "Art", "Other", "UIImages1.txt"),
                "other nested txt\n",
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(src, "Metadata", "Mods", "Demo", "notes.txt"),
                "mod notes\n",
                new UTF8Encoding(false));

            var packed = ModBundlePacker.Pack("uiimages-bom", "1.0.0", src, outDir);

            var uiImagesEntry = Assert.Single(packed.Manifest.Files, f => f.VirtualPath == "art/uiimages1.txt");
            Assert.Equal("utf-16le-nobom-crlf", uiImagesEntry.Encoding);
            byte[] uiImagesBytes = ModBundlePacker.ReadEntry(packed.BundlePath, packed.ManifestPath, "Art/UIImages1.txt");
            Assert.True(uiImagesBytes.Length >= 2 && (uiImagesBytes.Length & 1) == 0);
            Assert.False(uiImagesBytes[0] == 0xFF && uiImagesBytes[1] == 0xFE);
            Assert.Equal("Art/2DArt/UIImages/InGame/CustomIcon 0 0 64 64\r\n", Encoding.Unicode.GetString(uiImagesBytes));

            var nestedEntry = Assert.Single(packed.Manifest.Files, f => f.VirtualPath == "art/other/uiimages1.txt");
            Assert.Equal("utf-16le-bom-crlf", nestedEntry.Encoding);
            byte[] nestedBytes = ModBundlePacker.ReadEntry(packed.BundlePath, packed.ManifestPath, "Art/Other/UIImages1.txt");
            Assert.Equal(0xFF, nestedBytes[0]);
            Assert.Equal(0xFE, nestedBytes[1]);
            Assert.Equal("other nested txt\r\n", Encoding.Unicode.GetString(nestedBytes, 2, nestedBytes.Length - 2));

            var notesEntry = Assert.Single(packed.Manifest.Files, f => f.VirtualPath == "metadata/mods/demo/notes.txt");
            Assert.Equal("utf-16le-bom-crlf", notesEntry.Encoding);
            byte[] notesBytes = ModBundlePacker.ReadEntry(packed.BundlePath, packed.ManifestPath, "Metadata/Mods/Demo/notes.txt");
            Assert.Equal(0xFF, notesBytes[0]);
            Assert.Equal(0xFE, notesBytes[1]);
            Assert.Equal("mod notes\r\n", Encoding.Unicode.GetString(notesBytes, 2, notesBytes.Length - 2));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }
}
