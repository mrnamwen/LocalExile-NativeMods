using System.Diagnostics;

namespace LocalExile.Client.AssetPipeline.Dds;

/// <summary>
/// Row-major RGBA8 image (straight alpha, top-left origin).
/// </summary>
public sealed class RgbaImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public RgbaImage(int width, int height, byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (pixels.Length != checked(width * height * 4))
        {
            throw new ArgumentException($"Expected {width * height * 4} RGBA bytes for {width}x{height}, got {pixels.Length}.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public static RgbaImage CreateSolid(int width, int height, byte r, byte g, byte b, byte a)
    {
        byte[] pixels = new byte[checked(width * height * 4)];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = a;
        }

        return new RgbaImage(width, height, pixels);
    }

    /// <summary>
    /// Creates a flat tangent-space normal/AO/roughness map for PoE 3.29 <c>DielectricSpecGloss.fxgraph</c>
    /// (<c>R=128</c> normal X, <c>G=128</c> normal Y, <c>B=255</c> full indirect AO, <c>A=212</c> ~0.83 roughness when <c>UseRoughness=true</c>,
    /// matching stock <c>AfterLifeCat_normal_DXT5.dds</c> and matte fur).
    /// </summary>
    public static RgbaImage CreateFlatNormalGlossAo(int width = 64, int height = 64) =>
        CreateSolid(width, height, 128, 128, 255, 212);

    /// <summary>
    /// Generates a full box-filtered mipmap chain from mip 0 down to 1x1.
    /// </summary>
    public IReadOnlyList<RgbaImage> GenerateMipChain()
    {
        var mips = new List<RgbaImage> { this };
        RgbaImage current = this;
        while (current.Width > 1 || current.Height > 1)
        {
            current = current.DownsampleHalf();
            mips.Add(current);
        }

        return mips;
    }

    public RgbaImage DownsampleHalf()
    {
        int nextW = Math.Max(1, Width >> 1);
        int nextH = Math.Max(1, Height >> 1);
        byte[] dst = new byte[nextW * nextH * 4];

        for (int y = 0; y < nextH; y++)
        {
            int sy0 = Math.Min(y * 2, Height - 1);
            int sy1 = Math.Min((y * 2) + 1, Height - 1);
            for (int x = 0; x < nextW; x++)
            {
                int sx0 = Math.Min(x * 2, Width - 1);
                int sx1 = Math.Min((x * 2) + 1, Width - 1);

                int p00 = ((sy0 * Width) + sx0) * 4;
                int p10 = ((sy0 * Width) + sx1) * 4;
                int p01 = ((sy1 * Width) + sx0) * 4;
                int p11 = ((sy1 * Width) + sx1) * 4;

                int di = ((y * nextW) + x) * 4;
                for (int c = 0; c < 4; c++)
                {
                    int sum = Pixels[p00 + c] + Pixels[p10 + c] + Pixels[p01 + c] + Pixels[p11 + c];
                    dst[di + c] = (byte)((sum + 2) >> 2);
                }
            }
        }

        return new RgbaImage(nextW, nextH, dst);
    }

    public RgbaImage ResizeBilinear(int targetWidth, int targetHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetHeight);
        if (targetWidth == Width && targetHeight == Height)
        {
            return new RgbaImage(Width, Height, (byte[])Pixels.Clone());
        }

        byte[] dst = new byte[checked(targetWidth * targetHeight * 4)];
        for (int y = 0; y < targetHeight; y++)
        {
            float fy = ((y + 0.5f) * Height / targetHeight) - 0.5f;
            int y0 = Math.Clamp((int)MathF.Floor(fy), 0, Height - 1);
            int y1 = Math.Clamp(y0 + 1, 0, Height - 1);
            float ty = Math.Clamp(fy - y0, 0f, 1f);

            for (int x = 0; x < targetWidth; x++)
            {
                float fx = ((x + 0.5f) * Width / targetWidth) - 0.5f;
                int x0 = Math.Clamp((int)MathF.Floor(fx), 0, Width - 1);
                int x1 = Math.Clamp(x0 + 1, 0, Width - 1);
                float tx = Math.Clamp(fx - x0, 0f, 1f);

                int p00 = ((y0 * Width) + x0) * 4;
                int p10 = ((y0 * Width) + x1) * 4;
                int p01 = ((y1 * Width) + x0) * 4;
                int p11 = ((y1 * Width) + x1) * 4;
                int di = ((y * targetWidth) + x) * 4;

                for (int c = 0; c < 4; c++)
                {
                    float top = Pixels[p00 + c] + ((Pixels[p10 + c] - Pixels[p00 + c]) * tx);
                    float bot = Pixels[p01 + c] + ((Pixels[p11 + c] - Pixels[p01 + c]) * tx);
                    float val = top + ((bot - top) * ty);
                    dst[di + c] = (byte)Math.Clamp((int)MathF.Round(val), 0, 255);
                }
            }
        }

        return new RgbaImage(targetWidth, targetHeight, dst);
    }

    /// <summary>
    /// Decodes a JPEG, PNG, or other ImageMagick-supported image file into an <see cref="RgbaImage"/> using <c>magick</c>.
    /// </summary>
    public static RgbaImage DecodeWithImageMagick(string imagePath, int? resizeWidth = null, int? resizeHeight = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(imagePath);
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException($"Source image not found: '{imagePath}'.", imagePath);
        }

        string dimsOutput = RunProcessCaptureStdout("magick", ["identify", "-format", "%w %h", imagePath]).Trim();
        string[] parts = dimsOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[0], out int srcW) || !int.TryParse(parts[1], out int srcH))
        {
            throw new InvalidDataException($"Failed to read image dimensions via ImageMagick for '{imagePath}': '{dimsOutput}'.");
        }

        int width = resizeWidth ?? srcW;
        int height = resizeHeight ?? srcH;

        var args = new List<string> { imagePath, "-alpha", "on" };
        if (resizeWidth.HasValue && resizeHeight.HasValue)
        {
            args.Add("-resize");
            args.Add($"{width}x{height}!");
        }

        args.Add("-depth");
        args.Add("8");
        args.Add("rgba:-");

        byte[] raw = RunProcessCaptureBytes("magick", args);
        return new RgbaImage(width, height, raw);
    }

    public static RgbaImage DecodeBytesWithImageMagick(ReadOnlySpan<byte> imageBytes, string fileExtension, int? resizeWidth = null, int? resizeHeight = null)
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"le_img_{Guid.NewGuid():N}{fileExtension}");
        try
        {
            File.WriteAllBytes(tempFile, imageBytes.ToArray());
            return DecodeWithImageMagick(tempFile, resizeWidth, resizeHeight);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    private static string RunProcessCaptureStdout(string fileName, IReadOnlyList<string> arguments)
    {
        using var process = StartProcess(fileName, arguments);
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} exited with code {process.ExitCode}: {stderr}");
        }

        return stdout;
    }

    private static byte[] RunProcessCaptureBytes(string fileName, IReadOnlyList<string> arguments)
    {
        using var process = StartProcess(fileName, arguments);
        using var ms = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(ms);
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} exited with code {process.ExitCode}: {stderr}");
        }

        return ms.ToArray();
    }

    private static Process StartProcess(string fileName, IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        return Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
    }
}
