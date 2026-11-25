using System.Buffers.Binary;

namespace LocalExile.Client.AssetPipeline.Dds;

/// <summary>
/// Supported DDS pixel formats for Path of Exile 3.29.3d textures (verified against stock <c>Bundles2</c> textures).
/// </summary>
public enum PoeDdsFormat
{
    /// <summary>DXGI_FORMAT_R8G8B8A8_UNORM (28), PoE header enum 21 (0x15). Used by stock 2D item/pet icons and lossless alpha textures.</summary>
    Rgba8 = 0,
    /// <summary>DXGI_FORMAT_BC1_UNORM (71), PoE header enum 205 (0xCD). Used by stock opaque colour textures (DXT1).</summary>
    Bc1 = 1,
    /// <summary>DXGI_FORMAT_BC3_UNORM (77), PoE header enum 207 (0xCF). Block-compressed RGBA (DXT5).</summary>
    Bc3 = 2,
}

public sealed record DdsOutput(
    byte[] DdsBytes,
    byte[] HeaderBytes,
    int Width,
    int Height,
    int MipCount,
    PoeDdsFormat Format);

public sealed record DdsValidationInfo(
    int Width,
    int Height,
    int MipCount,
    uint DxgiFormat,
    uint PoeHeaderFormat,
    int EmbeddedWidth,
    int EmbeddedHeight,
    int EmbeddedMipCount);

/// <summary>
/// Pure-C# Path of Exile 3.29.3d <c>.dds</c> (DX10) + <c>.dds.header</c> encoder and verifier.
/// Matches stock 3.29.3d streaming texture layout:
/// <list type="bullet">
///   <item><c>.dds</c>: 148-byte DX10 DDS header + complete mipmap chain down to 1x1.</item>
///   <item><c>.dds.header</c>: 28-byte PoE header (<c>[version=3, w, h, mips, poeFormat, embeddedLen, fullLen]</c>) + embedded small RGBA8 DDS mip-tail (&lt;= 19px).</item>
/// </list>
/// </summary>
public static class DdsTextureConverter
{
    private const uint DdsMagic = 0x20534444u; // "DDS "
    private const uint Dx10FourCc = 0x30315844u; // "DX10"
    private const int LegacyHeaderSize = 128;
    private const int Dx10HeaderSize = 148;
    private const int PoeHeaderPrefixSize = 28;
    private const uint PoeHeaderVersion = 3;

    public static DdsOutput ConvertFromRgba(RgbaImage image, PoeDdsFormat format, bool generateMipmaps = true)
    {
        ArgumentNullException.ThrowIfNull(image);

        IReadOnlyList<RgbaImage> mips = generateMipmaps ? image.GenerateMipChain() : [image];
        byte[] ddsBytes = BuildDx10Dds(mips, format);
        byte[] headerBytes = BuildPoeDdsHeader(mips, format, ddsBytes.Length);
        return new DdsOutput(ddsBytes, headerBytes, image.Width, image.Height, mips.Count, format);
    }

    public static DdsOutput ConvertImageFile(string inputPath, PoeDdsFormat format, int? resizeWidth = null, int? resizeHeight = null)
    {
        RgbaImage img = RgbaImage.DecodeWithImageMagick(inputPath, resizeWidth, resizeHeight);
        return ConvertFromRgba(img, format, generateMipmaps: true);
    }

    /// <summary>
    /// Converts a square source image (e.g. 512x512 black-background Nano Banana currency art) into a PoE 3.29.3d
    /// 1x1 currency icon <c>.dds</c> + <c>.dds.header</c> pair using exterior flood-fill alpha cutout,
    /// smooth silhouette edge feathering, and premultiplied-alpha area-averaged downsampling.
    /// Defaults to <c>78x78</c> <see cref="PoeDdsFormat.Rgba8"/> (matching stock 3.29.3d DXGI 28 / PoE format 21 currency icons).
    /// </summary>
    public static DdsOutput ConvertCurrencyIcon(
        RgbaImage source,
        int targetWidth = 78,
        int targetHeight = 78,
        PoeDdsFormat format = PoeDdsFormat.Rgba8,
        bool removeBlackBackground = true,
        byte hardThreshold = 18,
        byte softThreshold = 52,
        int featherRadius = 2)
    {
        ArgumentNullException.ThrowIfNull(source);
        RgbaImage processed = removeBlackBackground
            ? source.CutoutBlackBackgroundFloodFill(hardThreshold, softThreshold, featherRadius)
            : source;
        RgbaImage resized = processed.ResizePremultipliedArea(targetWidth, targetHeight);
        return ConvertFromRgba(resized, format, generateMipmaps: true);
    }

    /// <summary>
    /// Decodes an image file via ImageMagick and converts it to a PoE 3.29.3d 1x1 currency icon <c>.dds</c> + <c>.dds.header</c>.
    /// </summary>
    public static DdsOutput ConvertCurrencyIconFile(
        string inputPath,
        int targetWidth = 78,
        int targetHeight = 78,
        PoeDdsFormat format = PoeDdsFormat.Rgba8,
        bool removeBlackBackground = true,
        byte hardThreshold = 18,
        byte softThreshold = 52,
        int featherRadius = 2)
    {
        RgbaImage decoded = RgbaImage.DecodeWithImageMagick(inputPath);
        return ConvertCurrencyIcon(
            decoded,
            targetWidth,
            targetHeight,
            format,
            removeBlackBackground,
            hardThreshold,
            softThreshold,
            featherRadius);
    }

    public static void WritePair(string ddsPath, DdsOutput output)
    {
        ArgumentException.ThrowIfNullOrEmpty(ddsPath);
        ArgumentNullException.ThrowIfNull(output);

        string? dir = Path.GetDirectoryName(ddsPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllBytes(ddsPath, output.DdsBytes);
        File.WriteAllBytes(ddsPath + ".header", output.HeaderBytes);
    }

    /// <summary>
    /// Validates a <c>.dds</c> + <c>.dds.header</c> pair against PoE 3.29.3d invariants.
    /// </summary>
    public static DdsValidationInfo Validate(ReadOnlySpan<byte> ddsBytes, ReadOnlySpan<byte> headerBytes)
    {
        if (ddsBytes.Length < Dx10HeaderSize)
        {
            throw new InvalidDataException($"DDS file too short ({ddsBytes.Length} bytes).");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(ddsBytes[..4]) != DdsMagic)
        {
            throw new InvalidDataException("Invalid DDS magic.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(ddsBytes.Slice(4, 4)) != 124)
        {
            throw new InvalidDataException("Invalid DDS_HEADER dwSize (expected 124).");
        }

        int height = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(ddsBytes.Slice(12, 4)));
        int width = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(ddsBytes.Slice(16, 4)));
        int mips = Math.Max(1, checked((int)BinaryPrimitives.ReadUInt32LittleEndian(ddsBytes.Slice(28, 4))));
        uint fourCc = BinaryPrimitives.ReadUInt32LittleEndian(ddsBytes.Slice(84, 4));
        if (fourCc != Dx10FourCc)
        {
            throw new InvalidDataException($"Expected DX10 FourCC in PoE 3.29 DDS, got 0x{fourCc:X8}.");
        }

        uint dxgi = BinaryPrimitives.ReadUInt32LittleEndian(ddsBytes.Slice(128, 4));
        int expectedPayload = ComputeExpectedPayloadBytes(width, height, mips, dxgi);
        if (ddsBytes.Length != Dx10HeaderSize + expectedPayload)
        {
            throw new InvalidDataException(
                $"DDS payload size mismatch: expected {Dx10HeaderSize + expectedPayload} bytes for {width}x{height} ({mips} mips, DXGI {dxgi}), got {ddsBytes.Length}.");
        }

        if (headerBytes.Length < PoeHeaderPrefixSize + LegacyHeaderSize)
        {
            throw new InvalidDataException($".dds.header too short ({headerBytes.Length} bytes).");
        }

        uint ver = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes[..4]);
        uint hw = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes.Slice(4, 4));
        uint hh = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes.Slice(8, 4));
        uint hmips = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes.Slice(12, 4));
        uint poeFmt = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes.Slice(16, 4));
        uint embLen = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes.Slice(20, 4));
        uint fullLen = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes.Slice(24, 4));

        if (ver != PoeHeaderVersion)
        {
            throw new InvalidDataException($"Unexpected .dds.header version {ver} (expected {PoeHeaderVersion}).");
        }

        if (hw != (uint)width || hh != (uint)height || hmips != (uint)mips)
        {
            throw new InvalidDataException(
                $".dds.header dimensions ({hw}x{hh}, {hmips} mips) disagree with .dds ({width}x{height}, {mips} mips).");
        }

        if (fullLen != (uint)ddsBytes.Length)
        {
            throw new InvalidDataException($".dds.header fullLen ({fullLen}) disagrees with .dds byte length ({ddsBytes.Length}).");
        }

        if (headerBytes.Length != PoeHeaderPrefixSize + (int)embLen)
        {
            throw new InvalidDataException($".dds.header embedded length ({embLen}) disagrees with file size ({headerBytes.Length}).");
        }

        ReadOnlySpan<byte> embedded = headerBytes[PoeHeaderPrefixSize..];
        if (BinaryPrimitives.ReadUInt32LittleEndian(embedded[..4]) != DdsMagic)
        {
            throw new InvalidDataException("Embedded DDS in .dds.header missing 'DDS ' magic.");
        }

        int embH = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(embedded.Slice(12, 4)));
        int embW = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(embedded.Slice(16, 4)));
        uint embDepth = BinaryPrimitives.ReadUInt32LittleEndian(embedded.Slice(24, 4));
        int embMips = Math.Max(1, checked((int)BinaryPrimitives.ReadUInt32LittleEndian(embedded.Slice(28, 4))));
        if (embDepth != 1u)
        {
            throw new InvalidDataException($"Embedded DDS dwDepth mismatch: expected 1 (stock 3.29.3d), got {embDepth}.");
        }

        int expectedEmbPayload = ComputeExpectedPayloadBytes(embW, embH, embMips, dxgiFormat: 28);
        if (embedded.Length != LegacyHeaderSize + expectedEmbPayload)
        {
            throw new InvalidDataException(
                $"Embedded DDS payload mismatch: expected {LegacyHeaderSize + expectedEmbPayload} bytes, got {embedded.Length}.");
        }

        return new DdsValidationInfo(width, height, mips, dxgi, poeFmt, embW, embH, embMips);
    }

    public static uint ToDxgiFormat(PoeDdsFormat format) => format switch
    {
        PoeDdsFormat.Rgba8 => 28u, // DXGI_FORMAT_R8G8B8A8_UNORM
        PoeDdsFormat.Bc1 => 71u,   // DXGI_FORMAT_BC1_UNORM
        PoeDdsFormat.Bc3 => 77u,   // DXGI_FORMAT_BC3_UNORM
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static uint ToPoeHeaderFormat(PoeDdsFormat format) => format switch
    {
        PoeDdsFormat.Rgba8 => 21u,  // 0x15 (verified on AfterlifeCatPet.dds.header & Kiwi1.dds.header)
        PoeDdsFormat.Bc1 => 205u,   // 0xCD (verified on AfterLifeCat_colour_DXT1.dds.header & soulkeepercat_colour_dxt1.dds.header)
        PoeDdsFormat.Bc3 => 207u,   // 0xCF
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static byte[] BuildDx10Dds(IReadOnlyList<RgbaImage> mips, PoeDdsFormat format)
    {
        RgbaImage mip0 = mips[0];
        using var ms = new MemoryStream();

        byte[] header = new byte[Dx10HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), DdsMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), 124u); // DDS_HEADER size
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), 0x00021007u); // CAPS|HEIGHT|WIDTH|PIXELFORMAT|MIPMAPCOUNT
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12, 4), (uint)mip0.Height);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16, 4), (uint)mip0.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20, 4), 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24, 4), 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28, 4), (uint)mips.Count);
        // DDS_PIXELFORMAT at offset 76
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(76, 4), 32u);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(80, 4), 0x4u); // DDPF_FOURCC
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(84, 4), Dx10FourCc);
        // dwCaps at offset 108: COMPLEX | TEXTURE | MIPMAP
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(108, 4), mips.Count > 1 ? 0x00401008u : 0x00001000u);
        // DDS_HEADER_DXT10 at offset 128
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(128, 4), ToDxgiFormat(format));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(132, 4), 3u); // D3D10_RESOURCE_DIMENSION_TEXTURE2D
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(136, 4), 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(140, 4), 1u); // arraySize
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(144, 4), 0u);

        ms.Write(header);

        foreach (RgbaImage mip in mips)
        {
            byte[] encoded = format switch
            {
                PoeDdsFormat.Rgba8 => mip.Pixels,
                PoeDdsFormat.Bc1 => CompressBc1(mip),
                PoeDdsFormat.Bc3 => CompressBc3(mip),
                _ => throw new ArgumentOutOfRangeException(nameof(format)),
            };
            ms.Write(encoded);
        }

        return ms.ToArray();
    }

    private static byte[] BuildPoeDdsHeader(IReadOnlyList<RgbaImage> mips, PoeDdsFormat format, int fullDdsLength)
    {
        // Stock 3.29.3d embeds the mip-tail starting at the largest mip with max(w, h) <= 20
        // (e.g. 16x16 for 64x64, 19x19 for 78x78 & 156x156, 20x20 for stock 80x80 currency icons).
        int startMip = 0;
        for (int i = 0; i < mips.Count; i++)
        {
            if (Math.Max(mips[i].Width, mips[i].Height) <= 20)
            {
                startMip = i;
                break;
            }

            startMip = i;
        }

        var tailMips = new List<RgbaImage>(mips.Count - startMip);
        for (int i = startMip; i < mips.Count; i++)
        {
            tailMips.Add(mips[i]);
        }

        byte[] embeddedDds = BuildLegacyRgba8Dds(tailMips);
        byte[] output = new byte[PoeHeaderPrefixSize + embeddedDds.Length];

        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0, 4), PoeHeaderVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4, 4), (uint)mips[0].Width);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8, 4), (uint)mips[0].Height);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(12, 4), (uint)mips.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(16, 4), ToPoeHeaderFormat(format));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(20, 4), (uint)embeddedDds.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(24, 4), (uint)fullDdsLength);
        Buffer.BlockCopy(embeddedDds, 0, output, PoeHeaderPrefixSize, embeddedDds.Length);

        return output;
    }

    private static byte[] BuildLegacyRgba8Dds(List<RgbaImage> mips)
    {
        RgbaImage top = mips[0];
        using var ms = new MemoryStream();
        byte[] header = new byte[LegacyHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), DdsMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), 124u);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), 0x0002100Fu); // CAPS|HEIGHT|WIDTH|PITCH|PIXELFORMAT|MIPMAPCOUNT
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12, 4), (uint)top.Height);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16, 4), (uint)top.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20, 4), (uint)(top.Width * 4));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24, 4), 1u); // dwDepth = 1 (matches stock 3.29.3d embedded DDS)
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28, 4), (uint)mips.Count);
        // DDS_PIXELFORMAT: RGBA8 masks matching stock 3.29.3d embedded DDS headers
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(76, 4), 32u);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(80, 4), 0x41u); // DDPF_RGB | DDPF_ALPHAPIXELS
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(88, 4), 32u);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(92, 4), 0x000000FFu); // R
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(96, 4), 0x0000FF00u); // G
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(100, 4), 0x00FF0000u); // B
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(104, 4), 0xFF000000u); // A
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(108, 4), mips.Count > 1 ? 0x00401008u : 0x00001000u);

        ms.Write(header);
        foreach (RgbaImage m in mips)
        {
            ms.Write(m.Pixels);
        }

        return ms.ToArray();
    }

    private static int ComputeExpectedPayloadBytes(int width, int height, int mips, uint dxgiFormat)
    {
        int total = 0;
        for (int i = 0; i < mips; i++)
        {
            int mw = Math.Max(1, width >> i);
            int mh = Math.Max(1, height >> i);
            int levelBytes = dxgiFormat switch
            {
                28u => mw * mh * 4,
                71u or 72u => ((mw + 3) / 4) * ((mh + 3) / 4) * 8,
                77u or 78u or 98u or 99u => ((mw + 3) / 4) * ((mh + 3) / 4) * 16,
                _ => throw new InvalidDataException($"Unsupported DXGI format {dxgiFormat}."),
            };
            total += levelBytes;
        }

        return total;
    }

    private static byte[] CompressBc1(RgbaImage image)
    {
        int blocksX = (image.Width + 3) / 4;
        int blocksY = (image.Height + 3) / 4;
        byte[] output = new byte[blocksX * blocksY * 8];
        Span<byte> blockRgba = stackalloc byte[64];

        int outOffset = 0;
        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                ExtractBlock(image, bx * 4, by * 4, blockRgba);
                CompressBc1ColorBlock(blockRgba, output.AsSpan(outOffset, 8));
                outOffset += 8;
            }
        }

        return output;
    }

    private static byte[] CompressBc3(RgbaImage image)
    {
        int blocksX = (image.Width + 3) / 4;
        int blocksY = (image.Height + 3) / 4;
        byte[] output = new byte[blocksX * blocksY * 16];
        Span<byte> blockRgba = stackalloc byte[64];

        int outOffset = 0;
        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                ExtractBlock(image, bx * 4, by * 4, blockRgba);
                CompressBc3AlphaBlock(blockRgba, output.AsSpan(outOffset, 8));
                CompressBc1ColorBlock(blockRgba, output.AsSpan(outOffset + 8, 8));
                outOffset += 16;
            }
        }

        return output;
    }

    private static void ExtractBlock(RgbaImage image, int startX, int startY, Span<byte> dst64)
    {
        for (int py = 0; py < 4; py++)
        {
            int sy = Math.Min(startY + py, image.Height - 1);
            for (int px = 0; px < 4; px++)
            {
                int sx = Math.Min(startX + px, image.Width - 1);
                int srcIdx = ((sy * image.Width) + sx) * 4;
                int dstIdx = ((py * 4) + px) * 4;
                dst64[dstIdx] = image.Pixels[srcIdx];
                dst64[dstIdx + 1] = image.Pixels[srcIdx + 1];
                dst64[dstIdx + 2] = image.Pixels[srcIdx + 2];
                dst64[dstIdx + 3] = image.Pixels[srcIdx + 3];
            }
        }
    }

    private static void CompressBc1ColorBlock(ReadOnlySpan<byte> rgba64, Span<byte> dst8)
    {
        // Select endpoints via bounding-box inset in RGB space.
        int minR = 255, minG = 255, minB = 255;
        int maxR = 0, maxG = 0, maxB = 0;
        for (int i = 0; i < 64; i += 4)
        {
            int r = rgba64[i];
            int g = rgba64[i + 1];
            int b = rgba64[i + 2];
            if (r < minR) minR = r;
            if (g < minG) minG = g;
            if (b < minB) minB = b;
            if (r > maxR) maxR = r;
            if (g > maxG) maxG = g;
            if (b > maxB) maxB = b;
        }

        int insetR = (maxR - minR) >> 4;
        int insetG = (maxG - minG) >> 4;
        int insetB = (maxB - minB) >> 4;
        minR = Math.Min(255, minR + insetR);
        minG = Math.Min(255, minG + insetG);
        minB = Math.Min(255, minB + insetB);
        maxR = Math.Max(0, maxR - insetR);
        maxG = Math.Max(0, maxG - insetG);
        maxB = Math.Max(0, maxB - insetB);

        ushort c0 = PackRgb565(maxR, maxG, maxB);
        ushort c1 = PackRgb565(minR, minG, minB);

        // Ensure 4-color opaque mode (c0 > c1).
        if (c0 < c1)
        {
            (c0, c1) = (c1, c0);
        }

        if (c0 == c1)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(dst8[..2], c0);
            BinaryPrimitives.WriteUInt16LittleEndian(dst8.Slice(2, 2), c1);
            BinaryPrimitives.WriteUInt32LittleEndian(dst8.Slice(4, 4), 0u);
            return;
        }

        Span<int> palR = stackalloc int[4];
        Span<int> palG = stackalloc int[4];
        Span<int> palB = stackalloc int[4];
        UnpackRgb565(c0, out palR[0], out palG[0], out palB[0]);
        UnpackRgb565(c1, out palR[1], out palG[1], out palB[1]);
        palR[2] = ((2 * palR[0]) + palR[1]) / 3;
        palG[2] = ((2 * palG[0]) + palG[1]) / 3;
        palB[2] = ((2 * palB[0]) + palB[1]) / 3;
        palR[3] = (palR[0] + (2 * palR[1])) / 3;
        palG[3] = (palG[0] + (2 * palG[1])) / 3;
        palB[3] = (palB[0] + (2 * palB[1])) / 3;

        uint indices = 0;
        for (int p = 0; p < 16; p++)
        {
            int r = rgba64[p * 4];
            int g = rgba64[(p * 4) + 1];
            int b = rgba64[(p * 4) + 2];

            int bestIdx = 0;
            int bestErr = int.MaxValue;
            for (int k = 0; k < 4; k++)
            {
                int dr = r - palR[k];
                int dg = g - palG[k];
                int db = b - palB[k];
                int err = (dr * dr) + (dg * dg) + (db * db);
                if (err < bestErr)
                {
                    bestErr = err;
                    bestIdx = k;
                }
            }

            indices |= (uint)bestIdx << (p * 2);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(dst8[..2], c0);
        BinaryPrimitives.WriteUInt16LittleEndian(dst8.Slice(2, 2), c1);
        BinaryPrimitives.WriteUInt32LittleEndian(dst8.Slice(4, 4), indices);
    }

    private static void CompressBc3AlphaBlock(ReadOnlySpan<byte> rgba64, Span<byte> dst8)
    {
        byte minA = 255;
        byte maxA = 0;
        for (int i = 0; i < 64; i += 4)
        {
            byte a = rgba64[i + 3];
            if (a < minA) minA = a;
            if (a > maxA) maxA = a;
        }

        dst8[0] = maxA;
        dst8[1] = minA;

        if (maxA == minA)
        {
            dst8[2..8].Clear();
            return;
        }

        Span<int> pal = stackalloc int[8];
        pal[0] = maxA;
        pal[1] = minA;
        for (int k = 1; k <= 6; k++)
        {
            pal[k + 1] = (((7 - k) * maxA) + (k * minA)) / 7;
        }

        ulong bits = 0;
        for (int p = 0; p < 16; p++)
        {
            int a = rgba64[(p * 4) + 3];
            int bestIdx = 0;
            int bestErr = int.MaxValue;
            for (int k = 0; k < 8; k++)
            {
                int err = Math.Abs(a - pal[k]);
                if (err < bestErr)
                {
                    bestErr = err;
                    bestIdx = k;
                }
            }

            bits |= (ulong)bestIdx << (p * 3);
        }

        for (int i = 0; i < 6; i++)
        {
            dst8[2 + i] = (byte)((bits >> (i * 8)) & 0xFF);
        }
    }

    private static ushort PackRgb565(int r, int g, int b) =>
        (ushort)(((r & 0xF8) << 8) | ((g & 0xFC) << 3) | (b >> 3));

    private static void UnpackRgb565(ushort c, out int r, out int g, out int b)
    {
        int r5 = (c >> 11) & 0x1F;
        int g6 = (c >> 5) & 0x3F;
        int b5 = c & 0x1F;
        r = (r5 << 3) | (r5 >> 2);
        g = (g6 << 2) | (g6 >> 4);
        b = (b5 << 3) | (b5 >> 2);
    }
}
