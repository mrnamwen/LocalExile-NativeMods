using System.Text;

namespace LocalExile.Client.Sdk.Engine;

/// <summary>
/// Identifies the backing source of a registered file overlay.
/// </summary>
public enum FileOverlaySourceKind
{
    /// <summary>An in-memory virtual file registered directly by a mod or server payload.</summary>
    VirtualFile = 0,

    /// <summary>A loose directory on disk mapped onto virtual game paths.</summary>
    LooseDirectory = 1,

    /// <summary>A packed <c>.bundle.bin</c> + manifest overlay registered into the index layer.</summary>
    BundleOverlay = 2,
}

/// <summary>
/// Describes a registered file overlay entry.
/// </summary>
/// <param name="VirtualPath">Normalized lowercase forward-slash virtual path (or directory root prefix).</param>
/// <param name="Kind">Source kind of the overlay.</param>
/// <param name="OwnerModId">Identifier of the mod or host component that registered this overlay.</param>
/// <param name="Priority">Resolution priority (higher values take precedence).</param>
/// <param name="SourceLocation">Optional backing file or directory path on disk.</param>
public sealed record FileOverlayEntry(
    string VirtualPath,
    FileOverlaySourceKind Kind,
    string OwnerModId,
    int Priority,
    string? SourceLocation = null);

/// <summary>
/// Encodes and decodes Path of Exile 3.29 client text assets using UTF-16LE with CRLF line endings
/// (with <c>0xFF 0xFE</c> BOM for <c>.ui</c>, <c>.ot</c>, <c>.it</c>, <c>.ao</c>, etc., and without BOM for <c>.sm</c>, <c>.mat</c>, <c>.amd</c>).
/// </summary>
public static class ClientTextEncoding
{
    private static readonly Encoding Utf16LeNoBom = new UnicodeEncoding(bigEndian: false, byteOrderMark: false);

    private static readonly HashSet<string> Utf16ClientExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ui", ".ot", ".otc", ".it", ".itc", ".ao", ".aoc", ".act", ".epk", ".ddt", ".pet", ".trl",
    };

    private static readonly HashSet<string> Utf16NoBomClientExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sm", ".mat", ".amd",
    };

    /// <summary>Normalizes a virtual game path to lowercase with forward slashes and no leading slash.</summary>
    public static string NormalizeVirtualPath(string virtualPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualPath);
        return virtualPath.Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();
    }

    /// <summary>Returns <c>true</c> when the virtual path extension requires UTF-16LE BOM CRLF encoding in the 3.29 client.</summary>
    public static bool RequiresUtf16LeBomCrlf(string virtualPath)
    {
        string ext = Path.GetExtension(virtualPath);
        return Utf16ClientExtensions.Contains(ext);
    }

    /// <summary>Returns <c>true</c> when the virtual path extension requires BOM-less UTF-16LE CRLF encoding in the 3.29 client (<c>.sm</c>, <c>.mat</c>, <c>.amd</c>).</summary>
    public static bool RequiresUtf16LeNoBomCrlf(string virtualPath)
    {
        string ext = Path.GetExtension(virtualPath);
        return Utf16NoBomClientExtensions.Contains(ext);
    }

    /// <summary>
    /// Encodes text as UTF-16LE with leading BOM (<c>0xFF 0xFE</c>) and CRLF line endings,
    /// stripping any pre-existing leading <c>\uFEFF</c> character so repeated encoding is idempotent.
    /// </summary>
    /// <param name="text">Input text with any line ending style.</param>
    /// <returns>Encoded byte array beginning with <c>0xFF 0xFE</c>.</returns>
    public static byte[] EncodeUtf16LeBomCrlf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string crlf = NormalizeToCrlfWithoutLeadingBom(text);
        byte[] payload = Encoding.Unicode.GetBytes(crlf);
        byte[] result = new byte[payload.Length + 2];
        result[0] = 0xFF;
        result[1] = 0xFE;
        Buffer.BlockCopy(payload, 0, result, 2, payload.Length);
        return result;
    }

    /// <summary>
    /// Encodes text as BOM-less UTF-16LE with CRLF line endings (used by <c>.sm</c>, <c>.mat</c>, <c>.amd</c>),
    /// stripping any pre-existing leading <c>\uFEFF</c> character so repeated encoding is idempotent.
    /// </summary>
    /// <param name="text">Input text with any line ending style.</param>
    /// <returns>Encoded UTF-16LE byte array without a BOM prefix.</returns>
    public static byte[] EncodeUtf16LeNoBomCrlf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string crlf = NormalizeToCrlfWithoutLeadingBom(text);
        return Utf16LeNoBom.GetBytes(crlf);
    }

    /// <summary>
    /// Encodes <paramref name="text"/> using the PoE 3.29 client encoding required by <paramref name="virtualPath"/>:
    /// UTF-16LE BOM CRLF, BOM-less UTF-16LE CRLF (<c>.sm</c>, <c>.mat</c>, <c>.amd</c>), or UTF-8.
    /// </summary>
    public static byte[] EncodeTextForPath(string virtualPath, string text)
    {
        ArgumentNullException.ThrowIfNull(virtualPath);
        ArgumentNullException.ThrowIfNull(text);

        if (RequiresUtf16LeBomCrlf(virtualPath))
        {
            return EncodeUtf16LeBomCrlf(text);
        }

        if (RequiresUtf16LeNoBomCrlf(virtualPath))
        {
            return EncodeUtf16LeNoBomCrlf(text);
        }

        return Encoding.UTF8.GetBytes(text);
    }

    /// <summary>
    /// Decodes a byte buffer from UTF-16LE (with or without BOM), UTF-16BE (with or without BOM), or UTF-8 into a managed string.
    /// </summary>
    public static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes[2..]);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes[3..]);
        }

        if (bytes.Length >= 2 && (bytes.Length & 1) == 0 && bytes.IndexOf((byte)0) >= 0)
        {
            int evenZeros = 0;
            int oddZeros = 0;
            for (int i = 0; i < bytes.Length; i += 2)
            {
                if (bytes[i] == 0)
                {
                    evenZeros++;
                }

                if (bytes[i + 1] == 0)
                {
                    oddZeros++;
                }
            }

            if (evenZeros > oddZeros)
            {
                string beText = Encoding.BigEndianUnicode.GetString(bytes);
                if (ContainsUnexpectedAsciiControlChar(beText))
                {
                    string leText = Encoding.Unicode.GetString(bytes);
                    if (!ContainsUnexpectedAsciiControlChar(leText))
                    {
                        return leText;
                    }
                }

                return beText;
            }

            return Encoding.Unicode.GetString(bytes);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static bool ContainsUnexpectedAsciiControlChar(string text)
    {
        foreach (char c in text)
        {
            if ((c >= '\x01' && c <= '\x08') || (c >= '\x0B' && c <= '\x0C') || (c >= '\x0E' && c <= '\x1F'))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeToCrlfWithoutLeadingBom(string text)
    {
        string stripped = text.TrimStart('\uFEFF');
        return stripped
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal);
    }
}

/// <summary>
/// Manages layered file overlays served to the 3.29 client without modifying stock <c>Content.ggpk</c> or bundle files.
/// Resolution order is highest priority first, with virtual files defaulting above loose directories and bundle overlays.
/// </summary>
public interface IFileOverlayService
{
    /// <summary>Registers a loose directory on disk whose relative paths mirror virtual game paths.</summary>
    /// <param name="rootDirectory">Directory on disk containing overlaid files.</param>
    /// <param name="priority">Resolution priority (default 0).</param>
    IDisposable RegisterDirectory(string rootDirectory, int priority = 0);

    /// <summary>Registers an in-memory binary virtual file at <paramref name="virtualPath"/>.</summary>
    /// <param name="virtualPath">Target virtual game path.</param>
    /// <param name="rawBytes">Exact raw bytes served to the client.</param>
    /// <param name="priority">Resolution priority (default 100).</param>
    IDisposable RegisterVirtualFile(string virtualPath, ReadOnlyMemory<byte> rawBytes, int priority = 100);

    /// <summary>
    /// Registers an in-memory text virtual file at <paramref name="virtualPath"/>.
    /// Automatically encodes <c>.ui</c>, <c>.ot</c>, <c>.it</c>, and related client text extensions
    /// as UTF-16LE with BOM and CRLF line endings; other text files are encoded as UTF-8.
    /// </summary>
    /// <param name="virtualPath">Target virtual game path.</param>
    /// <param name="text">Source text content.</param>
    /// <param name="priority">Resolution priority (default 100).</param>
    IDisposable RegisterVirtualTextFile(string virtualPath, string text, int priority = 100);

    /// <summary>Registers a packed mod bundle overlay (<c>.bundle.bin</c> + JSON manifest).</summary>
    /// <param name="bundlePath">Path to the <c>.bundle.bin</c> file on disk.</param>
    /// <param name="manifestPath">Path to the bundle's JSON manifest describing path offsets and sizes.</param>
    /// <param name="priority">Resolution priority (default -10).</param>
    IDisposable RegisterBundle(string bundlePath, string manifestPath, int priority = -10);

    /// <summary>Checks whether a virtual path exists in any overlay or underlying game archive.</summary>
    bool Exists(string virtualPath);

    /// <summary>Reads the raw bytes of a virtual path from the highest-priority overlay or game archive, or <c>null</c> if absent.</summary>
    byte[]? ReadAllBytes(string virtualPath);

    /// <summary>Reads and decodes a text file from the highest-priority overlay or game archive, or <c>null</c> if absent.</summary>
    string? ReadAllText(string virtualPath);

    /// <summary>Invalidates any cached engine representation of <paramref name="virtualPath"/> (for hot-reloading assets).</summary>
    void Invalidate(string virtualPath);

    /// <summary>Returns a snapshot of all currently registered overlays.</summary>
    IReadOnlyList<FileOverlayEntry> GetRegisteredOverlays();
}
