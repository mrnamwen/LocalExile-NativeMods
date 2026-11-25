using System.Text;
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
            var binEntry = Assert.Single(packedA.Manifest.Files, f => f.VirtualPath.EndsWith("atlas.dds"));
            Assert.Equal(2, binEntry.BlockCount);
            Assert.Equal("binary", binEntry.Encoding);

            // Text file encoded to UTF-16LE BOM CRLF
            var uiEntry = Assert.Single(packedA.Manifest.Files, f => f.VirtualPath.EndsWith("panel.ui"));
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
}
