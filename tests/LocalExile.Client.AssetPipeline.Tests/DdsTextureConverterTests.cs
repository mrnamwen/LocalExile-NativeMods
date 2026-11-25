using LocalExile.Client.AssetPipeline.Dds;
using Xunit;

namespace LocalExile.Client.AssetPipeline.Tests;

public sealed class DdsTextureConverterTests
{
    [Theory]
    [InlineData(64, 64, PoeDdsFormat.Bc1, 71u, 205u, 7, 16, 16, 5)]
    [InlineData(64, 32, PoeDdsFormat.Bc3, 77u, 207u, 7, 16, 8, 5)]
    [InlineData(78, 78, PoeDdsFormat.Rgba8, 28u, 21u, 7, 19, 19, 5)]
    [InlineData(80, 80, PoeDdsFormat.Rgba8, 28u, 21u, 7, 20, 20, 5)]
    [InlineData(156, 156, PoeDdsFormat.Rgba8, 28u, 21u, 8, 19, 19, 5)]
    public void ConvertFromRgba_ProducesValidDx10DdsAndPoe329Header(
        int width,
        int height,
        PoeDdsFormat format,
        uint expectedDxgi,
        uint expectedPoeFmt,
        int expectedMips,
        int expectedEmbW,
        int expectedEmbH,
        int expectedEmbMips)
    {
        RgbaImage img = CreateGradientImage(width, height);
        DdsOutput output = DdsTextureConverter.ConvertFromRgba(img, format, generateMipmaps: true);

        Assert.Equal(width, output.Width);
        Assert.Equal(height, output.Height);
        Assert.Equal(expectedMips, output.MipCount);

        DdsValidationInfo info = DdsTextureConverter.Validate(output.DdsBytes, output.HeaderBytes);
        Assert.Equal(width, info.Width);
        Assert.Equal(height, info.Height);
        Assert.Equal(expectedMips, info.MipCount);
        Assert.Equal(expectedDxgi, info.DxgiFormat);
        Assert.Equal(expectedPoeFmt, info.PoeHeaderFormat);
        Assert.Equal(expectedEmbW, info.EmbeddedWidth);
        Assert.Equal(expectedEmbH, info.EmbeddedHeight);
        Assert.Equal(expectedEmbMips, info.EmbeddedMipCount);
    }

    [Fact]
    public void Validate_RejectsCorruptDdsLengthOrMismatchedHeader()
    {
        RgbaImage img = RgbaImage.CreateSolid(32, 32, 200, 100, 50, 255);
        DdsOutput output = DdsTextureConverter.ConvertFromRgba(img, PoeDdsFormat.Bc1);

        byte[] truncated = output.DdsBytes[..^4];
        Assert.Throws<InvalidDataException>(() => DdsTextureConverter.Validate(truncated, output.HeaderBytes));

        byte[] badHeader = (byte[])output.HeaderBytes.Clone();
        badHeader[0] = 99; // wrong version
        Assert.Throws<InvalidDataException>(() => DdsTextureConverter.Validate(output.DdsBytes, badHeader));

        // Embedded legacy DDS dwDepth at header offset 28 + 24 = 52 must be 1 (stock 3.29.3d)
        Assert.Equal((byte)1, output.HeaderBytes[52]);
        byte[] zeroDepthHeader = (byte[])output.HeaderBytes.Clone();
        zeroDepthHeader[52] = 0;
        Assert.Throws<InvalidDataException>(() => DdsTextureConverter.Validate(output.DdsBytes, zeroDepthHeader));
    }

    [Fact]
    public void CreateFlatNormalGlossAo_MatchesDielectricSpecGlossMatteFurPacking()
    {
        RgbaImage normal = RgbaImage.CreateFlatNormalGlossAo(16, 16);
        Assert.Equal(16, normal.Width);
        Assert.Equal(16, normal.Height);
        // R=128 (Nx=0), G=128 (Ny=0), B=255 (full AO), A=212 (~0.83 roughness with UseRoughness=true)
        Assert.Equal((byte)128, normal.Pixels[0]);
        Assert.Equal((byte)128, normal.Pixels[1]);
        Assert.Equal((byte)255, normal.Pixels[2]);
        Assert.Equal((byte)212, normal.Pixels[3]);
    }

    [Fact]
    public void CutoutBlackBackgroundFloodFill_ClearsExteriorWhilePreservingDarkInterior()
    {
        // Build a 64x64 synthetic orb on a near-black (#020202) exterior background with a bright metallic ring
        // enclosing a pure black (#000000) smoky interior core.
        const int size = 64;
        byte[] pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int dx = x - 32;
                int dy = y - 32;
                double r = Math.Sqrt((dx * dx) + (dy * dy));
                int p = ((y * size) + x) * 4;

                if (r > 22.0)
                {
                    // Exterior near-black background
                    pixels[p] = 2;
                    pixels[p + 1] = 2;
                    pixels[p + 2] = 2;
                    pixels[p + 3] = 255;
                }
                else if (r >= 15.0)
                {
                    // Bright metallic iron filigree ring
                    pixels[p] = 170;
                    pixels[p + 1] = 180;
                    pixels[p + 2] = 195;
                    pixels[p + 3] = 255;
                }
                else
                {
                    // Pure black interior core (MUST stay 100% opaque!)
                    pixels[p] = 0;
                    pixels[p + 1] = 0;
                    pixels[p + 2] = 0;
                    pixels[p + 3] = 255;
                }
            }
        }

        var src = new RgbaImage(size, size, pixels);
        RgbaImage cutout = src.CutoutBlackBackgroundFloodFill();

        // Corners are exterior -> fully transparent
        Assert.Equal((byte)0, cutout.Pixels[3]);
        Assert.Equal((byte)0, cutout.Pixels[(((size - 1) * size) + (size - 1)) * 4 + 3]);

        // Ring pixel at (32 + 18, 32) -> fully opaque
        int ringIdx = ((32 * size) + 50) * 4;
        Assert.Equal((byte)255, cutout.Pixels[ringIdx + 3]);

        // Pure black center pixel (32, 32) -> MUST remain 100% opaque (flood fill never reaches enclosed interior)
        int centerIdx = ((32 * size) + 32) * 4;
        Assert.Equal((byte)255, cutout.Pixels[centerIdx + 3]);
    }

    [Fact]
    public void ConvertCurrencyIcon_ProducesStockParity78x78Rgba8DdsAndHeader()
    {
        const int size = 128;
        byte[] pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int dx = x - 64;
                int dy = y - 64;
                int p = ((y * size) + x) * 4;
                if ((dx * dx) + (dy * dy) <= 44 * 44)
                {
                    pixels[p] = 90;
                    pixels[p + 1] = 220;
                    pixels[p + 2] = 255;
                    pixels[p + 3] = 255;
                }
                else
                {
                    pixels[p] = 1;
                    pixels[p + 1] = 1;
                    pixels[p + 2] = 2;
                    pixels[p + 3] = 255;
                }
            }
        }

        DdsOutput output = DdsTextureConverter.ConvertCurrencyIcon(new RgbaImage(size, size, pixels));
        DdsValidationInfo info = DdsTextureConverter.Validate(output.DdsBytes, output.HeaderBytes);

        Assert.Equal(78, info.Width);
        Assert.Equal(78, info.Height);
        Assert.Equal(7, info.MipCount);
        Assert.Equal(28u, info.DxgiFormat);
        Assert.Equal(21u, info.PoeHeaderFormat);
        Assert.Equal(19, info.EmbeddedWidth);
        Assert.Equal(19, info.EmbeddedHeight);
        Assert.Equal(5, info.EmbeddedMipCount);

        // Verify stock 80x80 byte-size parity too (exact match to real 3.29.3d CurrencyAddModToRare.dds: 34268 / 2248+28)
        DdsOutput output80 = DdsTextureConverter.ConvertCurrencyIcon(new RgbaImage(size, size, pixels), 80, 80);
        DdsValidationInfo info80 = DdsTextureConverter.Validate(output80.DdsBytes, output80.HeaderBytes);
        Assert.Equal(34268, output80.DdsBytes.Length);
        Assert.Equal(28 + 2248, output80.HeaderBytes.Length);
        Assert.Equal(20, info80.EmbeddedWidth);
        Assert.Equal(20, info80.EmbeddedHeight);
        Assert.Equal(5, info80.EmbeddedMipCount);
    }

    private static RgbaImage CreateGradientImage(int w, int h)
    {
        byte[] pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = ((y * w) + x) * 4;
                pixels[i] = (byte)(x * 255 / Math.Max(1, w - 1));
                pixels[i + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
                pixels[i + 2] = (byte)((x + y) & 0xFF);
                pixels[i + 3] = (byte)(255 - (y * 128 / Math.Max(1, h - 1)));
            }
        }

        return new RgbaImage(w, h, pixels);
    }
}
