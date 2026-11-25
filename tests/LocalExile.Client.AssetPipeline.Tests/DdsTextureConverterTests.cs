using LocalExile.Client.AssetPipeline.Dds;
using Xunit;

namespace LocalExile.Client.AssetPipeline.Tests;

public sealed class DdsTextureConverterTests
{
    [Theory]
    [InlineData(64, 64, PoeDdsFormat.Bc1, 71u, 205u, 7, 16, 16, 5)]
    [InlineData(64, 32, PoeDdsFormat.Bc3, 77u, 207u, 7, 16, 8, 5)]
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
