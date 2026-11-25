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

                int a00 = Pixels[p00 + 3];
                int a10 = Pixels[p10 + 3];
                int a01 = Pixels[p01 + 3];
                int a11 = Pixels[p11 + 3];
                int sumA = a00 + a10 + a01 + a11;

                int di = ((y * nextW) + x) * 4;
                if (sumA == 0)
                {
                    dst[di] = 0;
                    dst[di + 1] = 0;
                    dst[di + 2] = 0;
                    dst[di + 3] = 0;
                }
                else if (a00 == 255 && a10 == 255 && a01 == 255 && a11 == 255)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        int sum = Pixels[p00 + c] + Pixels[p10 + c] + Pixels[p01 + c] + Pixels[p11 + c];
                        dst[di + c] = (byte)((sum + 2) >> 2);
                    }

                    dst[di + 3] = 255;
                }
                else
                {
                    // Alpha-weighted RGB averaging prevents transparent black background pixels from bleeding dark fringes into mipmaps.
                    for (int c = 0; c < 3; c++)
                    {
                        int wSum = (Pixels[p00 + c] * a00) + (Pixels[p10 + c] * a10) + (Pixels[p01 + c] * a01) + (Pixels[p11 + c] * a11);
                        dst[di + c] = (byte)Math.Clamp((wSum + (sumA >> 1)) / sumA, 0, 255);
                    }

                    dst[di + 3] = (byte)((sumA + 2) >> 2);
                }
            }
        }

        return new RgbaImage(nextW, nextH, dst);
    }

    /// <summary>
    /// Removes a dark/black exterior background via 4-connected flood-fill from all four image edges inward,
    /// followed by smooth anti-aliased edge feathering along the exterior silhouette boundary.
    /// Interior dark regions (such as iron filigree or smoky crystal cores) are untouched and stay 100% opaque.
    /// </summary>
    public RgbaImage CutoutBlackBackgroundFloodFill(
        byte hardThreshold = 18,
        byte softThreshold = 52,
        int featherRadius = 2)
    {
        if (softThreshold < hardThreshold)
        {
            throw new ArgumentException("softThreshold must be >= hardThreshold.", nameof(softThreshold));
        }

        if (featherRadius < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(featherRadius), "featherRadius cannot be negative.");
        }

        int total = Width * Height;
        byte[] output = (byte[])Pixels.Clone();
        bool[] exterior = new bool[total];
        var queue = new Queue<int>(Math.Max(64, (Width + Height) * 2));

        void TrySeedExterior(int x, int y)
        {
            int idx = (y * Width) + x;
            if (exterior[idx]) return;
            int p = idx * 4;
            byte a = output[p + 3];
            byte maxRgb = Math.Max(output[p], Math.Max(output[p + 1], output[p + 2]));
            if (a == 0 || maxRgb <= hardThreshold)
            {
                exterior[idx] = true;
                queue.Enqueue(idx);
            }
        }

        for (int x = 0; x < Width; x++)
        {
            TrySeedExterior(x, 0);
            TrySeedExterior(x, Height - 1);
        }

        for (int y = 0; y < Height; y++)
        {
            TrySeedExterior(0, y);
            TrySeedExterior(Width - 1, y);
        }

        while (queue.Count > 0)
        {
            int idx = queue.Dequeue();
            int x = idx % Width;
            int y = idx / Width;

            if (x > 0) TrySeedExterior(x - 1, y);
            if (x + 1 < Width) TrySeedExterior(x + 1, y);
            if (y > 0) TrySeedExterior(x, y - 1);
            if (y + 1 < Height) TrySeedExterior(x, y + 1);
        }

        // Clear exterior pixels to fully transparent black.
        for (int idx = 0; idx < total; idx++)
        {
            if (exterior[idx])
            {
                int p = idx * 4;
                output[p] = 0;
                output[p + 1] = 0;
                output[p + 2] = 0;
                output[p + 3] = 0;
            }
        }

        // Smoothly feather only a thin band (<= featherRadius pixels) immediately adjacent to the exterior silhouette.
        if (featherRadius > 0 && softThreshold > hardThreshold)
        {
            int[] dist = new int[total];
            Array.Fill(dist, -1);
            var edgeQueue = new Queue<int>();

            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int idx = (y * Width) + x;
                    if (exterior[idx]) continue;

                    bool touchesExterior =
                        (x > 0 && exterior[idx - 1]) ||
                        (x + 1 < Width && exterior[idx + 1]) ||
                        (y > 0 && exterior[idx - Width]) ||
                        (y + 1 < Height && exterior[idx + Width]);

                    if (touchesExterior)
                    {
                        dist[idx] = 1;
                        edgeQueue.Enqueue(idx);
                    }
                }
            }

            while (edgeQueue.Count > 0)
            {
                int idx = edgeQueue.Dequeue();
                int d = dist[idx];
                if (d >= featherRadius) continue;

                int x = idx % Width;
                int y = idx / Width;
                void TryNext(int nx, int ny)
                {
                    int nIdx = (ny * Width) + nx;
                    if (!exterior[nIdx] && dist[nIdx] < 0)
                    {
                        dist[nIdx] = d + 1;
                        edgeQueue.Enqueue(nIdx);
                    }
                }

                if (x > 0) TryNext(x - 1, y);
                if (x + 1 < Width) TryNext(x + 1, y);
                if (y > 0) TryNext(x, y - 1);
                if (y + 1 < Height) TryNext(x, y + 1);
            }

            float range = softThreshold - hardThreshold;
            for (int idx = 0; idx < total; idx++)
            {
                int d = dist[idx];
                if (d <= 0) continue;

                int p = idx * 4;
                byte origA = output[p + 3];
                if (origA == 0) continue;

                byte maxRgb = Math.Max(output[p], Math.Max(output[p + 1], output[p + 2]));
                if (maxRgb >= softThreshold) continue;

                // Blend distance-weighted luminance ramp so the outermost silhouette pixel anti-aliases cleanly
                // without eating into dark metallic interior edges.
                float lumAlpha = Math.Clamp((maxRgb - hardThreshold) / range, 0f, 1f);
                float distBias = (d - 1f) / Math.Max(1f, featherRadius);
                float newAlphaNorm = Math.Clamp(lumAlpha + (distBias * (1f - lumAlpha)), 0f, 1f);

                // De-fringe RGB slightly to cancel dark halo from black-matte anti-aliasing.
                if (newAlphaNorm > 0.05f && newAlphaNorm < 0.99f)
                {
                    float unpremul = 1f / Math.Max(newAlphaNorm, 0.35f);
                    output[p] = (byte)Math.Clamp((int)MathF.Round(output[p] * unpremul), 0, 255);
                    output[p + 1] = (byte)Math.Clamp((int)MathF.Round(output[p + 1] * unpremul), 0, 255);
                    output[p + 2] = (byte)Math.Clamp((int)MathF.Round(output[p + 2] * unpremul), 0, 255);
                }

                output[p + 3] = (byte)Math.Clamp((int)MathF.Round(origA * newAlphaNorm), 0, 255);
            }
        }

        return new RgbaImage(Width, Height, output);
    }

    /// <summary>
    /// High-quality premultiplied-alpha area-averaged resize (ideal for downsampling 512x512 icons to 78x78
    /// without shimmer or dark transparent-background fringe bleeding).
    /// </summary>
    public RgbaImage ResizePremultipliedArea(int targetWidth, int targetHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetHeight);

        if (targetWidth == Width && targetHeight == Height)
        {
            return new RgbaImage(Width, Height, (byte[])Pixels.Clone());
        }

        if (targetWidth > Width || targetHeight > Height)
        {
            return ResizeBilinear(targetWidth, targetHeight);
        }

        byte[] dst = new byte[checked(targetWidth * targetHeight * 4)];
        double scaleX = (double)Width / targetWidth;
        double scaleY = (double)Height / targetHeight;

        for (int ty = 0; ty < targetHeight; ty++)
        {
            double sy0 = ty * scaleY;
            double sy1 = (ty + 1) * scaleY;
            int iy0 = Math.Clamp((int)Math.Floor(sy0), 0, Height - 1);
            int iy1 = Math.Clamp((int)Math.Ceiling(sy1) - 1, iy0, Height - 1);

            for (int tx = 0; tx < targetWidth; tx++)
            {
                double sx0 = tx * scaleX;
                double sx1 = (tx + 1) * scaleX;
                int ix0 = Math.Clamp((int)Math.Floor(sx0), 0, Width - 1);
                int ix1 = Math.Clamp((int)Math.Ceiling(sx1) - 1, ix0, Width - 1);

                double sumR = 0, sumG = 0, sumB = 0, sumA = 0, sumArea = 0;

                for (int sy = iy0; sy <= iy1; sy++)
                {
                    double wy = Math.Min(sy + 1.0, sy1) - Math.Max(sy, sy0);
                    if (wy <= 0) continue;

                    for (int sx = ix0; sx <= ix1; sx++)
                    {
                        double wx = Math.Min(sx + 1.0, sx1) - Math.Max(sx, sx0);
                        if (wx <= 0) continue;

                        double area = wx * wy;
                        int p = ((sy * Width) + sx) * 4;
                        double a = Pixels[p + 3] / 255.0;
                        double wa = area * a;

                        sumR += Pixels[p] * wa;
                        sumG += Pixels[p + 1] * wa;
                        sumB += Pixels[p + 2] * wa;
                        sumA += a * area;
                        sumArea += area;
                    }
                }

                int di = ((ty * targetWidth) + tx) * 4;
                if (sumA <= 1e-6 || sumArea <= 0)
                {
                    dst[di] = 0;
                    dst[di + 1] = 0;
                    dst[di + 2] = 0;
                    dst[di + 3] = 0;
                }
                else
                {
                    dst[di] = (byte)Math.Clamp((int)Math.Round(sumR / sumA), 0, 255);
                    dst[di + 1] = (byte)Math.Clamp((int)Math.Round(sumG / sumA), 0, 255);
                    dst[di + 2] = (byte)Math.Clamp((int)Math.Round(sumB / sumA), 0, 255);
                    dst[di + 3] = (byte)Math.Clamp((int)Math.Round((sumA / sumArea) * 255.0), 0, 255);
                }
            }
        }

        return new RgbaImage(targetWidth, targetHeight, dst);
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
