using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalExile.Client.Host.Native;
using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Addresses;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.Sdk.Raw;

namespace LocalExile.Client.Host.Services;

/// <summary>
/// Result of mounting a Waypoint-built <c>Mods/Client/.merged-cache/</c> directory into <see cref="FileOverlayService"/>
/// and <see cref="ContentRegistry"/> at <c>t=0</c> startup.
/// </summary>
public sealed record MergedCacheMountResult(
    bool Mounted,
    string MergedCacheDirectory,
    IReadOnlyList<string> MountedVirtualPaths,
    ContentRowMap? RowMap,
    IDisposable? OverlayHandle)
{
    /// <summary>SHA-256 digest of the mounted <see cref="RowMap"/>, if loaded.</summary>
    public string? ContentRowMapDigest => RowMap?.Digest;
}

/// <summary>
/// Layered file overlay service supporting virtual files, loose directories, and packed bundle overlays
/// with deterministic priority ordering, automatic UTF-16LE CRLF encoding for client text files,
/// and asset invalidation.
/// </summary>
public sealed class FileOverlayService : IFileOverlayService, IStockFileProvider
{
    private sealed record VirtualFileLayer(long Id, string NormalizedPath, byte[] Bytes, string OwnerModId, int Priority);
    private sealed record DirectoryLayer(long Id, string RootDirectory, string OwnerModId, int Priority, Dictionary<string, string> IndexedFiles, IReadOnlySet<string>? ExcludedNormalizedPaths = null);
    private sealed record BundleEntrySlice(
        string NormalizedPath,
        long Offset,
        int Size,
        int? UncompressedSize = null,
        int? BlockCount = null,
        int? FormatVersion = null,
        int BlockSize = 0x40000,
        string? Sha256 = null);
    private sealed record BundleLayer(long Id, string BundlePath, string ManifestPath, string OwnerModId, int Priority, Dictionary<string, BundleEntrySlice> Entries);

    private readonly INativeBridge _bridge;
    private readonly List<VirtualFileLayer> _virtualFiles = [];
    private readonly List<DirectoryLayer> _directories = [];
    private readonly List<BundleLayer> _bundles = [];
    private readonly Lock _gate = new();
    private Dictionary<string, Func<byte[]?>> _resolvedIndex = new(StringComparer.Ordinal);
    private long _nextLayerId = 1;

    /// <summary>Creates a new <see cref="FileOverlayService"/> wired to <paramref name="bridge"/>.</summary>
    public FileOverlayService(INativeBridge bridge)
    {
        _bridge = bridge;
        _bridge.RegisterFileOverlayResolver(ResolveOverlayOnly);
        _bridge.SetFileOverlayKnownPaths([]);
    }

    /// <inheritdoc/>
    public IDisposable RegisterDirectory(string rootDirectory, int priority = 0) =>
        RegisterDirectoryForMod("host", rootDirectory, priority);

    internal IDisposable RegisterDirectoryForMod(string ownerModId, string rootDirectory, int priority) =>
        RegisterDirectoryForMod(ownerModId, rootDirectory, priority, excludedNormalizedPaths: null);

    internal IDisposable RegisterDirectoryForMod(
        string ownerModId,
        string rootDirectory,
        int priority,
        IReadOnlySet<string>? excludedNormalizedPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        string full = Path.GetFullPath(rootDirectory);
        Dictionary<string, string> indexed = IndexDirectory(full, excludedNormalizedPaths);
        long id = Interlocked.Increment(ref _nextLayerId);
        var layer = new DirectoryLayer(id, full, ownerModId, priority, indexed, excludedNormalizedPaths);
        string[] knownPaths;
        lock (_gate)
        {
            _directories.Add(layer);
            knownPaths = RebuildResolvedIndexLocked();
        }

        _bridge.SetFileOverlayKnownPaths(knownPaths);

        return new ActionDisposable(() =>
        {
            string[] updated;
            lock (_gate)
            {
                _directories.RemoveAll(l => l.Id == id);
                updated = RebuildResolvedIndexLocked();
            }

            _bridge.SetFileOverlayKnownPaths(updated);
        });
    }

    /// <summary>
    /// Detects and mounts a <c>Mods/Client/.merged-cache/</c> directory at <c>t=0</c>: registers all merged
    /// <c>.datc64</c> tables and Route A <c>Metadata/StatDescriptions/*.txt</c> overlays (excluding metadata
    /// <c>content-row-map.json</c> from game file overlays) and parses <c>content-row-map.json</c> into
    /// <paramref name="contentRegistry"/> when provided.
    /// </summary>
    public MergedCacheMountResult MountMergedCache(
        string mergedCacheDirectory,
        ContentRegistry? contentRegistry = null,
        int priority = 500,
        Action<string, string, string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mergedCacheDirectory);
        string fullDir = Path.GetFullPath(mergedCacheDirectory);
        if (!Directory.Exists(fullDir))
        {
            return new MergedCacheMountResult(false, fullDir, [], null, null);
        }

        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ContentRowMap.FileName,
        };

        Dictionary<string, string> indexed = IndexDirectory(fullDir, excluded);
        ContentRowMap? rowMap = null;
        string rowMapPath = Path.Combine(fullDir, ContentRowMap.FileName);
        if (File.Exists(rowMapPath))
        {
            try
            {
                rowMap = ContentRowMap.LoadFromFile(rowMapPath);
                contentRegistry?.SetRowMap(rowMap);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
            {
                log?.Invoke("WARN", "content", $"Failed to parse '{rowMapPath}': {ex.Message}");
            }
        }

        if (indexed.Count == 0 && rowMap is null)
        {
            return new MergedCacheMountResult(false, fullDir, [], null, null);
        }

        IDisposable? overlayHandle = indexed.Count > 0
            ? RegisterDirectoryForMod("__merged-cache", fullDir, priority, excluded)
            : null;

        string[] mountedPaths = [.. indexed.Keys.OrderBy(p => p, StringComparer.Ordinal)];
        log?.Invoke(
            "INFO",
            "content",
            $"Mounted .merged-cache from '{fullDir}' at t=0 ({mountedPaths.Length} overlay file(s), digest={rowMap?.Digest ?? "<none>"}).");

        return new MergedCacheMountResult(
            Mounted: true,
            MergedCacheDirectory: fullDir,
            MountedVirtualPaths: mountedPaths,
            RowMap: rowMap,
            OverlayHandle: overlayHandle);
    }

    /// <inheritdoc/>
    public IDisposable RegisterVirtualFile(string virtualPath, ReadOnlyMemory<byte> rawBytes, int priority = 100) =>
        RegisterVirtualFileForMod("host", virtualPath, rawBytes, priority);

    internal IDisposable RegisterVirtualFileForMod(string ownerModId, string virtualPath, ReadOnlyMemory<byte> rawBytes, int priority)
    {
        string normalized = ClientTextEncoding.NormalizeVirtualPath(virtualPath);
        long id = Interlocked.Increment(ref _nextLayerId);
        var layer = new VirtualFileLayer(id, normalized, rawBytes.ToArray(), ownerModId, priority);
        string[] knownPaths;
        lock (_gate)
        {
            _virtualFiles.Add(layer);
            knownPaths = RebuildResolvedIndexLocked();
        }

        _bridge.SetFileOverlayKnownPaths(knownPaths);

        return new ActionDisposable(() =>
        {
            string[] updated;
            lock (_gate)
            {
                _virtualFiles.RemoveAll(l => l.Id == id);
                updated = RebuildResolvedIndexLocked();
            }

            _bridge.SetFileOverlayKnownPaths(updated);
        });
    }

    /// <inheritdoc/>
    public IDisposable RegisterVirtualTextFile(string virtualPath, string text, int priority = 100) =>
        RegisterVirtualTextFileForMod("host", virtualPath, text, priority);

    internal IDisposable RegisterVirtualTextFileForMod(string ownerModId, string virtualPath, string text, int priority)
    {
        ArgumentNullException.ThrowIfNull(text);
        byte[] encoded = ClientTextEncoding.EncodeTextForPath(virtualPath, text);
        return RegisterVirtualFileForMod(ownerModId, virtualPath, encoded, priority);
    }

    /// <inheritdoc/>
    public IDisposable RegisterBundle(string bundlePath, string manifestPath, int priority = -10) =>
        RegisterBundleForMod("host", bundlePath, manifestPath, priority);

    internal IDisposable RegisterBundleForMod(string ownerModId, string bundlePath, string manifestPath, int priority)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        Dictionary<string, BundleEntrySlice> entries = ParseBundleManifest(manifestPath);
        long id = Interlocked.Increment(ref _nextLayerId);
        var layer = new BundleLayer(id, Path.GetFullPath(bundlePath), Path.GetFullPath(manifestPath), ownerModId, priority, entries);
        string[] knownPaths;
        lock (_gate)
        {
            _bundles.Add(layer);
            knownPaths = RebuildResolvedIndexLocked();
        }

        _bridge.SetFileOverlayKnownPaths(knownPaths);

        return new ActionDisposable(() =>
        {
            string[] updated;
            lock (_gate)
            {
                _bundles.RemoveAll(l => l.Id == id);
                updated = RebuildResolvedIndexLocked();
            }

            _bridge.SetFileOverlayKnownPaths(updated);
        });
    }

    /// <inheritdoc/>
    public bool Exists(string virtualPath) => ReadAllBytes(virtualPath) is not null;

    /// <inheritdoc/>
    public byte[]? ReadAllBytes(string virtualPath)
    {
        string normalized = ClientTextEncoding.NormalizeVirtualPath(virtualPath);
        return ResolveOverlayOnly(normalized) ?? _bridge.ReadStockGameFile(normalized);
    }

    /// <inheritdoc/>
    public byte[]? ReadStockGameFile(string virtualPath)
    {
        string normalized = NativeShimBridge.NormalizeAndValidateVirtualPath(virtualPath);
        return _bridge.ReadStockGameFile(normalized);
    }

    /// <inheritdoc/>
    public string? ReadAllText(string virtualPath)
    {
        byte[]? bytes = ReadAllBytes(virtualPath);
        return bytes is null ? null : ClientTextEncoding.DecodeText(bytes);
    }

    /// <inheritdoc/>
    public void Invalidate(string virtualPath)
    {
        string normalized = ClientTextEncoding.NormalizeVirtualPath(virtualPath);
        string[]? updatedPaths = null;
        lock (_gate)
        {
            if (_directories.Count > 0)
            {
                for (int i = 0; i < _directories.Count; i++)
                {
                    DirectoryLayer d = _directories[i];
                    _directories[i] = d with { IndexedFiles = IndexDirectory(d.RootDirectory, d.ExcludedNormalizedPaths) };
                }

                updatedPaths = RebuildResolvedIndexLocked();
            }
        }

        if (updatedPaths is not null)
        {
            _bridge.SetFileOverlayKnownPaths(updatedPaths);
        }

        _bridge.InvalidateCachedAsset(normalized);
    }

    /// <inheritdoc/>
    public IReadOnlyList<FileOverlayEntry> GetRegisteredOverlays()
    {
        var result = new List<FileOverlayEntry>();
        lock (_gate)
        {
            foreach (VirtualFileLayer vf in _virtualFiles)
            {
                result.Add(new FileOverlayEntry(vf.NormalizedPath, FileOverlaySourceKind.VirtualFile, vf.OwnerModId, vf.Priority));
            }

            foreach (DirectoryLayer dir in _directories)
            {
                result.Add(new FileOverlayEntry("*", FileOverlaySourceKind.LooseDirectory, dir.OwnerModId, dir.Priority, dir.RootDirectory));
            }

            foreach (BundleLayer b in _bundles)
            {
                foreach (string path in b.Entries.Keys)
                {
                    result.Add(new FileOverlayEntry(path, FileOverlaySourceKind.BundleOverlay, b.OwnerModId, b.Priority, b.BundlePath));
                }
            }
        }

        return result;
    }

    private byte[]? ResolveOverlayOnly(string virtualPath)
    {
        string normalized = ClientTextEncoding.NormalizeVirtualPath(virtualPath);
        Dictionary<string, Func<byte[]?>> index = Volatile.Read(ref _resolvedIndex);
        return index.TryGetValue(normalized, out Func<byte[]?>? reader) ? reader() : null;
    }

    private string[] RebuildResolvedIndexLocked()
    {
        var winners = new Dictionary<string, (int Priority, int KindRank, long Id, Func<byte[]?> Reader)>(StringComparer.Ordinal);

        void Consider(string normalizedPath, int priority, int kindRank, long id, Func<byte[]?> reader)
        {
            if (winners.TryGetValue(normalizedPath, out var existing))
            {
                int p = priority.CompareTo(existing.Priority);
                if (p < 0) return;
                if (p == 0)
                {
                    int k = kindRank.CompareTo(existing.KindRank);
                    if (k < 0) return;
                    if (k == 0 && id < existing.Id) return;
                }
            }

            winners[normalizedPath] = (priority, kindRank, id, reader);
        }

        foreach (BundleLayer bundle in _bundles)
        {
            if (!File.Exists(bundle.BundlePath))
            {
                continue;
            }

            string bundlePath = bundle.BundlePath;
            foreach (KeyValuePair<string, BundleEntrySlice> kv in bundle.Entries)
            {
                BundleEntrySlice slice = kv.Value;
                Consider(kv.Key, bundle.Priority, 1, bundle.Id, () => ReadBundleSlice(bundlePath, slice));
            }
        }

        foreach (DirectoryLayer dir in _directories)
        {
            foreach (KeyValuePair<string, string> kv in dir.IndexedFiles)
            {
                string diskPath = kv.Value;
                Consider(kv.Key, dir.Priority, 2, dir.Id, () => File.Exists(diskPath) ? File.ReadAllBytes(diskPath) : null);
            }
        }

        foreach (VirtualFileLayer vf in _virtualFiles)
        {
            byte[] captured = vf.Bytes;
            Consider(vf.NormalizedPath, vf.Priority, 3, vf.Id, () => (byte[])captured.Clone());
        }

        var next = new Dictionary<string, Func<byte[]?>>(winners.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, (int Priority, int KindRank, long Id, Func<byte[]?> Reader)> kv in winners)
        {
            next[kv.Key] = kv.Value.Reader;
        }

        Volatile.Write(ref _resolvedIndex, next);
        return [.. next.Keys];
    }

    private static Dictionary<string, string> IndexDirectory(
        string rootDirectory,
        IReadOnlySet<string>? excludedNormalizedPaths = null)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(rootDirectory))
        {
            return map;
        }

        try
        {
            foreach (string fullPath in Directory.EnumerateFiles(rootDirectory, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(rootDirectory, fullPath);
                string normalized = ClientTextEncoding.NormalizeVirtualPath(rel);
                if (!string.IsNullOrEmpty(normalized) &&
                    (excludedNormalizedPaths is null || !excludedNormalizedPaths.Contains(normalized)))
                {
                    map[normalized] = fullPath;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return map;
    }

    private static byte[] ReadBundleSlice(string bundlePath, BundleEntrySlice slice)
    {
        if (slice.FormatVersion is int fv && fv != 1)
        {
            throw new InvalidDataException($"Unsupported bundle format version {fv}.");
        }

        using var fs = new FileStream(bundlePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] hdr = new byte[(int)Math.Min(16, fs.Length)];
        if (hdr.Length > 0)
        {
            fs.ReadExactly(hdr);
        }

        bool hasNmb1Magic = hdr.Length == 16 && hdr.AsSpan(0, 4).SequenceEqual("NMB1"u8);
        bool isNmb1 = slice.FormatVersion == 1 || (slice.FormatVersion is null && hasNmb1Magic && slice.Offset >= 16);

        if (isNmb1)
        {
            if (!hasNmb1Magic)
            {
                throw new InvalidDataException("Invalid NMB1 bundle header.");
            }

            int headerVersion = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(4, 4));
            int headerEntryCount = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(8, 4));
            int headerBlockSize = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(12, 4));
            if (headerVersion != 1)
            {
                throw new InvalidDataException($"Unsupported NMB1 header version {headerVersion}.");
            }

            if (headerEntryCount < 0)
            {
                throw new InvalidDataException($"Invalid NMB1 entry count {headerEntryCount}.");
            }

            if (headerBlockSize <= 0)
            {
                throw new InvalidDataException($"Invalid NMB1 block size {headerBlockSize}.");
            }

            if (slice.BlockSize <= 0)
            {
                throw new InvalidDataException($"Invalid NMB1 manifest block size {slice.BlockSize}.");
            }

            if (slice.BlockSize != headerBlockSize)
            {
                throw new InvalidDataException($"NMB1 block size mismatch: manifest {slice.BlockSize}, header {headerBlockSize}.");
            }

            if (slice.Offset < 16 || slice.Size < 0 || slice.Offset > fs.Length - slice.Size)
            {
                throw new InvalidDataException("NMB1 entry offset/size falls outside bundle bounds.");
            }

            fs.Seek(slice.Offset, SeekOrigin.Begin);
            byte[] raw = new byte[slice.Size];
            fs.ReadExactly(raw);

            if (slice.UncompressedSize is int expectedUncompressed && (expectedUncompressed < 0 || expectedUncompressed > raw.Length))
            {
                throw new InvalidDataException($"Invalid NMB1 uncompressedSize {expectedUncompressed} for stored size {raw.Length}.");
            }

            using var output = slice.UncompressedSize is int cap ? new MemoryStream(cap) : new MemoryStream();
            int pos = 0;
            int blocksRead = 0;
            while (pos < raw.Length)
            {
                if (pos + 6 > raw.Length)
                {
                    throw new InvalidDataException("Truncated NMB1 stored block header.");
                }

                int chunkLen = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(pos, 4));
                if (chunkLen < 0 || chunkLen > headerBlockSize)
                {
                    throw new InvalidDataException($"Invalid NMB1 stored block length {chunkLen}.");
                }

                if (raw[pos + 4] != 0xCC || raw[pos + 5] != 0x06)
                {
                    throw new InvalidDataException("Invalid NMB1 stored block marker (expected 0xCC 0x06).");
                }

                pos += 6;
                if (chunkLen > raw.Length - pos)
                {
                    throw new InvalidDataException("Truncated NMB1 stored block payload.");
                }

                output.Write(raw, pos, chunkLen);
                pos += chunkLen;
                blocksRead++;
            }

            if (blocksRead == 0)
            {
                throw new InvalidDataException("NMB1 entry contains no stored blocks.");
            }

            if (slice.BlockCount is int expectedBlocks && blocksRead != expectedBlocks)
            {
                throw new InvalidDataException($"NMB1 block count mismatch: expected {expectedBlocks}, read {blocksRead}.");
            }

            byte[] result = output.ToArray();
            if (slice.UncompressedSize is int expectedSize && result.Length != expectedSize)
            {
                throw new InvalidDataException($"NMB1 uncompressed size mismatch: expected {expectedSize}, got {result.Length}.");
            }

            if (!string.IsNullOrWhiteSpace(slice.Sha256) &&
                !Convert.ToHexStringLower(SHA256.HashData(result)).Equals(slice.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"NMB1 entry '{slice.NormalizedPath}' failed SHA-256 verification.");
            }

            return result;
        }

        if (slice.Offset < 0 || slice.Size < 0 || slice.Offset > fs.Length - slice.Size)
        {
            throw new InvalidDataException("Bundle entry offset/size falls outside bundle bounds.");
        }

        fs.Seek(slice.Offset, SeekOrigin.Begin);
        byte[] legacyRaw = new byte[slice.Size];
        fs.ReadExactly(legacyRaw);

        // Unwrap stored Oodle block prefix (0xCC 0x06) when present (10-verification.md §2).
        if (legacyRaw.Length >= 2 && legacyRaw[0] == 0xCC && legacyRaw[1] == 0x06)
        {
            return legacyRaw[2..];
        }

        return legacyRaw;
    }

    private static Dictionary<string, BundleEntrySlice> ParseBundleManifest(string manifestPath)
    {
        var map = new Dictionary<string, BundleEntrySlice>(StringComparer.Ordinal);
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        int? formatVersion = doc.RootElement.TryGetProperty("formatVersion", out JsonElement fv) && fv.ValueKind == JsonValueKind.Number
            ? fv.GetInt32()
            : null;
        int blockSize = doc.RootElement.TryGetProperty("blockSize", out JsonElement bs) && bs.ValueKind == JsonValueKind.Number
            ? bs.GetInt32()
            : 0x40000;

        if (doc.RootElement.TryGetProperty("files", out JsonElement files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in files.EnumerateArray())
            {
                string? path = item.TryGetProperty("path", out JsonElement p) ? p.GetString()
                    : item.TryGetProperty("virtualPath", out JsonElement vp) ? vp.GetString() : null;
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                long offset = item.TryGetProperty("offset", out JsonElement o) && o.ValueKind == JsonValueKind.Number ? o.GetInt64() : 0;
                int size = item.TryGetProperty("size", out JsonElement s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32()
                    : item.TryGetProperty("storedSize", out JsonElement ss) && ss.ValueKind == JsonValueKind.Number ? ss.GetInt32() : 0;
                int? uncompressedSize = item.TryGetProperty("uncompressedSize", out JsonElement us) && us.ValueKind == JsonValueKind.Number
                    ? us.GetInt32()
                    : null;
                int? blockCount = item.TryGetProperty("blockCount", out JsonElement bc) && bc.ValueKind == JsonValueKind.Number
                    ? bc.GetInt32()
                    : null;
                string? sha256 = item.TryGetProperty("sha256", out JsonElement sh) && sh.ValueKind == JsonValueKind.String
                    ? sh.GetString()
                    : null;

                string normalized = ClientTextEncoding.NormalizeVirtualPath(path);
                map[normalized] = new BundleEntrySlice(normalized, offset, size, uncompressedSize, blockCount, formatVersion, blockSize, sha256);
            }
        }

        return map;
    }
}

/// <summary>
/// Registry of custom GEAL built-in functions callable from <c>.ot</c> client scripts,
/// wired live to 3.29 <c>geal.builtin_factory_table</c> when running on the native shim.
/// </summary>
public sealed class GealBuiltinRegistry : IGealBuiltinRegistry
{
    private readonly INativeBridge? _bridge;
    private readonly Dictionary<string, (Func<uint, GealArguments, GealValue> Handler, bool HasReturnValue, IDisposable NativeSub)> _builtins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <summary>Creates a new <see cref="GealBuiltinRegistry"/> optionally backed by <paramref name="bridge"/>.</summary>
    public GealBuiltinRegistry(INativeBridge? bridge = null)
    {
        _bridge = bridge;
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<string> RegisteredNames
    {
        get { lock (_gate) { return [.. _builtins.Keys]; } }
    }

    /// <inheritdoc/>
    public IDisposable Register(string name, Func<uint, GealArguments, GealValue> handler) =>
        RegisterCore(name, hasReturnValue: true, handler);

    /// <inheritdoc/>
    public IDisposable Register(string name, Action<uint, GealArguments> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return RegisterCore(name, hasReturnValue: false, (objId, args) =>
        {
            handler(objId, args);
            return GealValue.Null;
        });
    }

    private ActionDisposable RegisterCore(string name, bool hasReturnValue, Func<uint, GealArguments, GealValue> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);

        string trimmedName = name.Trim();
        IDisposable nativeSub = NoopDisposable.Instance;
        lock (_gate)
        {
            if (_builtins.ContainsKey(trimmedName))
            {
                throw new InvalidOperationException($"GEAL built-in '{trimmedName}' is already registered.");
            }

            if (_bridge is not null)
            {
                nativeSub = _bridge.RegisterNativeGealBuiltin(trimmedName, hasReturnValue, handler);
            }

            _builtins[trimmedName] = (handler, hasReturnValue, nativeSub);
        }

        return new ActionDisposable(() =>
        {
            IDisposable? toDispose = null;
            lock (_gate)
            {
                if (_builtins.TryGetValue(trimmedName, out var entry))
                {
                    toDispose = entry.NativeSub;
                    _builtins.Remove(trimmedName);
                }
            }

            toDispose?.Dispose();
        });
    }

    /// <inheritdoc/>
    public bool IsRegistered(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        lock (_gate)
        {
            return _builtins.ContainsKey(name.Trim());
        }
    }

    /// <inheritdoc/>
    public GealInvocationResult Invoke(string name, uint objectId, params GealValue[] arguments)
    {
        Func<uint, GealArguments, GealValue>? handler;
        lock (_gate)
        {
            handler = _builtins.TryGetValue(name, out var entry) ? entry.Handler : null;
        }

        if (handler is null)
        {
            return new GealInvocationResult(false, GealValue.Null, $"GEAL built-in '{name}' is not registered.");
        }

        try
        {
            GealValue result = handler(objectId, new GealArguments(arguments));
            return new GealInvocationResult(true, result);
        }
        catch (Exception ex)
        {
            return new GealInvocationResult(false, GealValue.Null, ex.Message);
        }
    }
}

/// <summary>
/// Manages GEAL script invocation, validation/linting, and custom built-in registration.
/// </summary>
public sealed class ScriptService : IScriptService
{
    private static readonly Regex CallPattern = new(@"\b([A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Compiled);

    // Representative server-only GEAL built-ins known to fail client compilation (10-verification.md §4).
    private static readonly HashSet<string> KnownServerOnlyBuiltins = new(StringComparer.OrdinalIgnoreCase)
    {
        "SendOpenScreen", "SendOpenStashScreen", "SendOpenTradeScreen", "GrantItem", "DropItem",
        "SetQuestFlag", "SpawnMonster", "SpawnMonsterPack", "AddBuff", "RemoveBuff", "DealDamage",
    };

    private static readonly string[] CoreStockClientBuiltins =
    [
        "If", "While", "DoWhile", "Repeat", "ForEach", "IfState", "Delay", "Call",
        "PlayAnimation", "PlayEffect", "AddEffectPack", "PlayAudio", "PlayNPCAudio",
        "Transition", "DisableRendering", "ShowMinimapIcon", "DispatchRulesetEvent",
        "SetCustomCameraSpeed", "ATan2", "Xor",
    ];

    private readonly INativeBridge _bridge;
    private readonly GealBuiltinRegistry _builtins;
    private readonly ChatService? _chat;

    /// <summary>Creates a new <see cref="ScriptService"/>.</summary>
    public ScriptService(INativeBridge bridge, GealBuiltinRegistry builtins, ChatService? chat = null)
    {
        _bridge = bridge;
        _builtins = builtins;
        _chat = chat;
    }

    /// <inheritdoc/>
    public GealInvocationResult InvokeFunction(uint objectId, string functionName, params GealValue[] arguments)
    {
        if (_builtins.IsRegistered(functionName))
        {
            return _builtins.Invoke(functionName, objectId, arguments);
        }

        return _bridge.InvokeGeal(objectId, functionName, isEvent: false, arguments);
    }

    /// <inheritdoc/>
    public GealInvocationResult InvokeEvent(uint objectId, string eventName, params GealValue[] arguments) =>
        _bridge.InvokeGeal(objectId, eventName, isEvent: true, arguments);

    /// <inheritdoc/>
    public IReadOnlyList<GealScriptDiagnostic> ValidateScript(string gealSource)
    {
        var diagnostics = new List<GealScriptDiagnostic>();
        if (string.IsNullOrWhiteSpace(gealSource))
        {
            return diagnostics;
        }

        int braceDepth = 0;
        string[] lines = gealSource.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            string line = lines[i];
            foreach (char ch in line)
            {
                if (ch == '{')
                {
                    braceDepth++;
                }
                else if (ch == '}')
                {
                    braceDepth--;
                    if (braceDepth < 0)
                    {
                        diagnostics.Add(new GealScriptDiagnostic(true, "GEAL001", "Unmatched closing brace '}'.", lineNum));
                        braceDepth = 0;
                    }
                }
            }

            foreach (Match m in CallPattern.Matches(line))
            {
                string fn = m.Groups[1].Value;
                if (KnownServerOnlyBuiltins.Contains(fn))
                {
                    diagnostics.Add(new GealScriptDiagnostic(true, "GEAL002", $"Function '{fn}' is server-only and does not compile on the 3.29 client.", lineNum));
                }

                if (string.Equals(fn, "Repeat", StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(new GealScriptDiagnostic(false, "GEAL003", "GEAL 'Repeat(n)' has no client iteration watchdog (unlike While/DoWhile capped at 1000); verify the bound.", lineNum));
                }
            }
        }

        if (braceDepth > 0)
        {
            diagnostics.Add(new GealScriptDiagnostic(true, "GEAL004", $"Unclosed '{{' block ({braceDepth} remaining at EOF).", lines.Length));
        }

        return diagnostics;
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<string> GetRegisteredBuiltins()
    {
        var all = new HashSet<string>(CoreStockClientBuiltins, StringComparer.OrdinalIgnoreCase);
        foreach (string custom in _builtins.RegisteredNames)
        {
            all.Add(custom);
        }

        return all;
    }

    /// <inheritdoc/>
    public IDisposable Register(string name, Func<uint, GealArguments, GealValue> callback) =>
        _builtins.Register(name, callback);

    /// <inheritdoc/>
    public IDisposable Register(string name, Action<uint, GealArguments> callback) =>
        _builtins.Register(name, callback);

    /// <inheritdoc/>
    public IDisposable RegisterChatInterceptor(Func<string, string, bool> interceptor)
    {
        ArgumentNullException.ThrowIfNull(interceptor);
        return _chat is not null
            ? _chat.RegisterInterceptor(interceptor)
            : _bridge.RegisterChatInterceptor(interceptor);
    }
}

/// <summary>Managed <see cref="IClipboardService"/> backed by <see cref="INativeBridge"/>.</summary>
public sealed class ClipboardService(INativeBridge bridge) : IClipboardService
{
    /// <inheritdoc/>
    public string? GetText() => bridge.GetClipboardText();

    /// <inheritdoc/>
    public void SetText(string text) => bridge.SetClipboardText(text);
}

/// <summary>Managed <see cref="IChatService"/> backed by <see cref="INativeBridge"/>.</summary>
public sealed class ChatService(INativeBridge bridge) : IChatService
{
    /// <inheritdoc/>
    public IDisposable RegisterInterceptor(Func<string, string, bool> interceptor)
    {
        ArgumentNullException.ThrowIfNull(interceptor);
        return bridge.RegisterChatInterceptor(interceptor);
    }

    /// <inheritdoc/>
    public void SendLine(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        bridge.SendChatLine(text);
    }
}

/// <summary>Managed <see cref="IAudioService"/> implementation with live 3D camera-distance spatial attenuation.</summary>
public sealed class AudioService(INativeBridge bridge) : IAudioService
{
    private sealed record SpatialChannelState(
        float X,
        float Y,
        float Z,
        float BaseVolume,
        float MaxDistance,
        float LastAppliedVolume);

    private readonly HashSet<int> _activeChannels = [];
    private readonly Dictionary<int, SpatialChannelState> _spatialChannels = [];
    private readonly Lock _gate = new();

    /// <inheritdoc/>
    public int ActiveChannelCount
    {
        get { lock (_gate) { return _activeChannels.Count; } }
    }

    /// <summary>Number of active audio channels currently tracked with 3D spatial attenuation.</summary>
    public int SpatialChannelCount
    {
        get { lock (_gate) { return _spatialChannels.Count; } }
    }

    /// <inheritdoc/>
    public int PlayEvent(string fmodEventPath, float volume = 1.0f)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fmodEventPath);
        int handle = bridge.PlayAudio(fmodEventPath, isFmodEvent: true, Math.Clamp(volume, 0f, 1f));
        lock (_gate)
        {
            _activeChannels.Add(handle);
        }

        return handle;
    }

    /// <inheritdoc/>
    public int PlaySoundFile(string virtualOggPath, float volume = 1.0f) =>
        PlaySoundFile(virtualOggPath, volume, loop: false);

    /// <inheritdoc/>
    public int PlaySoundFile(string virtualOggPath, float volume, bool loop)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualOggPath);
        int handle = bridge.PlayAudio(virtualOggPath, isFmodEvent: false, Math.Clamp(volume, 0f, 1f), loop);
        lock (_gate)
        {
            _activeChannels.Add(handle);
        }

        return handle;
    }

    /// <inheritdoc/>
    public AudioChannelHandle PlaySoundAtPosition(
        string pathOrEvent,
        float x,
        float y,
        float z,
        float volume = 1.0f,
        float maxDistance = 1200f,
        bool loop = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathOrEvent);
        ValidateSpatialParams(x, y, z, maxDistance);

        float baseVolume = Math.Clamp(float.IsNaN(volume) ? 0f : volume, 0f, 1f);
        float attenuated = ComputeAttenuatedVolume(x, y, z, baseVolume, maxDistance);
        bool isFmodEvent = !pathOrEvent.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase);

        int handle = bridge.PlayAudio(pathOrEvent, isFmodEvent, attenuated, loop);
        if (handle > 0)
        {
            lock (_gate)
            {
                _activeChannels.Add(handle);
                _spatialChannels[handle] = new SpatialChannelState(x, y, z, baseVolume, maxDistance, attenuated);
            }
        }

        return new AudioChannelHandle(handle);
    }

    /// <inheritdoc/>
    public void SetChannelPosition(AudioChannelHandle channel, float x, float y, float z, float maxDistance = 1200f)
    {
        ValidateSpatialParams(x, y, z, maxDistance);
        int handle = channel.Value;
        if (handle <= 0)
        {
            return;
        }

        float baseVolume;
        lock (_gate)
        {
            baseVolume = _spatialChannels.TryGetValue(handle, out SpatialChannelState? existing)
                ? existing.BaseVolume
                : 1.0f;
        }

        float attenuated = ComputeAttenuatedVolume(x, y, z, baseVolume, maxDistance);
        lock (_gate)
        {
            _activeChannels.Add(handle);
            _spatialChannels[handle] = new SpatialChannelState(x, y, z, baseVolume, maxDistance, attenuated);
        }

        bridge.SetAudioVolume(handle, attenuated);
    }

    /// <inheritdoc/>
    public void SetChannelVolume(int channelHandle, float volume)
    {
        float clamped = Math.Clamp(float.IsNaN(volume) ? 0f : volume, 0f, 1f);
        SpatialChannelState? spatial;
        lock (_gate)
        {
            _spatialChannels.TryGetValue(channelHandle, out spatial);
        }

        if (spatial is not null)
        {
            float attenuated = ComputeAttenuatedVolume(spatial.X, spatial.Y, spatial.Z, clamped, spatial.MaxDistance);
            lock (_gate)
            {
                if (_spatialChannels.ContainsKey(channelHandle))
                {
                    _spatialChannels[channelHandle] = spatial with { BaseVolume = clamped, LastAppliedVolume = attenuated };
                }
            }

            bridge.SetAudioVolume(channelHandle, attenuated);
            return;
        }

        bridge.SetAudioVolume(channelHandle, clamped);
    }

    /// <inheritdoc/>
    public bool IsChannelPlaying(int channelHandle) =>
        bridge.IsAudioPlaying(channelHandle);

    /// <inheritdoc/>
    public void StopChannel(int channelHandle)
    {
        bridge.StopAudio(channelHandle);
        lock (_gate)
        {
            _activeChannels.Remove(channelHandle);
            _spatialChannels.Remove(channelHandle);
        }
    }

    /// <summary>
    /// Updates distance attenuation for all active 3D spatial channels on the game thread
    /// and prunes channels that have finished playing.
    /// </summary>
    public void TickSpatialChannels()
    {
        KeyValuePair<int, SpatialChannelState>[] snapshot;
        lock (_gate)
        {
            if (_spatialChannels.Count == 0)
            {
                return;
            }

            snapshot = [.. _spatialChannels];
        }

        WorldPosition3D? listener = TryGetListenerPosition();
        List<int>? ended = null;

        foreach (var (handle, state) in snapshot)
        {
            if (!bridge.IsAudioPlaying(handle))
            {
                ended ??= [];
                ended.Add(handle);
                continue;
            }

            float nextVolume = ComputeAttenuatedVolume(state.X, state.Y, state.Z, state.BaseVolume, state.MaxDistance, listener);
            if (MathF.Abs(nextVolume - state.LastAppliedVolume) >= 1e-4f)
            {
                bridge.SetAudioVolume(handle, nextVolume);
                lock (_gate)
                {
                    if (_spatialChannels.ContainsKey(handle))
                    {
                        _spatialChannels[handle] = state with { LastAppliedVolume = nextVolume };
                    }
                }
            }
        }

        if (ended is not null)
        {
            lock (_gate)
            {
                foreach (int ch in ended)
                {
                    _spatialChannels.Remove(ch);
                    _activeChannels.Remove(ch);
                }
            }
        }
    }

    private WorldPosition3D? TryGetListenerPosition()
    {
        CameraState camera = bridge.GetCameraState();
        if (camera.IsValid)
        {
            return camera.Target;
        }

        return bridge.GetLocalPlayerEntity()?.Position;
    }

    private float ComputeAttenuatedVolume(float x, float y, float z, float baseVolume, float maxDistance) =>
        ComputeAttenuatedVolume(x, y, z, baseVolume, maxDistance, TryGetListenerPosition());

    private static float ComputeAttenuatedVolume(
        float x,
        float y,
        float z,
        float baseVolume,
        float maxDistance,
        WorldPosition3D? listener)
    {
        if (listener is null)
        {
            return Math.Clamp(baseVolume, 0f, 1f);
        }

        float dx = x - listener.Value.X;
        float dy = y - listener.Value.Y;
        float dz = z - listener.Value.Z;
        float distance = MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        float gain = Math.Clamp(1.0f - (distance / maxDistance), 0f, 1f);
        return Math.Clamp(baseVolume * gain, 0f, 1f);
    }

    private static void ValidateSpatialParams(float x, float y, float z, float maxDistance)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Spatial audio coordinates must be finite numbers.");
        }

        if (!float.IsFinite(maxDistance) || maxDistance <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDistance), "Spatial audio maxDistance must be a finite positive value.");
        }
    }
}

/// <summary>Managed <see cref="IStatsService"/> implementation.</summary>
public sealed class StatsService(INativeBridge bridge) : IStatsService
{
    private readonly Dictionary<string, List<Action<int>>> _listeners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <inheritdoc/>
    public int GetStat(string statKey, int defaultValue = 0) => bridge.ReadStat(statKey, defaultValue);

    /// <inheritdoc/>
    public void SetLocalStat(string statKey, int value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statKey);
        bridge.WriteLocalStat(statKey, value);

        Action<int>[] snapshot;
        lock (_gate)
        {
            snapshot = _listeners.TryGetValue(statKey, out List<Action<int>>? list) ? [.. list] : [];
        }

        foreach (Action<int> listener in snapshot)
        {
            listener(value);
        }
    }

    /// <inheritdoc/>
    public IDisposable OnStatChanged(string statKey, Action<int> listener)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statKey);
        ArgumentNullException.ThrowIfNull(listener);
        lock (_gate)
        {
            if (!_listeners.TryGetValue(statKey, out List<Action<int>>? list))
            {
                list = [];
                _listeners[statKey] = list;
            }

            list.Add(listener);
        }

        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                if (_listeners.TryGetValue(statKey, out List<Action<int>>? list))
                {
                    list.Remove(listener);
                }
            }
        });
    }

    /// <inheritdoc/>
    public int ReadStat(uint entityId, int statId) =>
        bridge.TryReadEntityStat(entityId, statId, out int value) ? value : 0;

    /// <inheritdoc/>
    public bool TryReadStat(uint entityId, int statId, out int value) =>
        bridge.TryReadEntityStat(entityId, statId, out value);

    /// <inheritdoc/>
    public void SetLocalStat(uint entityId, int statId, int value) =>
        bridge.SetLocalEntityStat(entityId, statId, value);

    /// <inheritdoc/>
    public IReadOnlyDictionary<int, int> GetAllStats(uint entityId) =>
        bridge.GetAllEntityStats(entityId);

    /// <inheritdoc/>
    public int ReadLocalPlayerStat(int statId) => ReadStat(0u, statId);

    /// <inheritdoc/>
    public bool TryReadLocalPlayerStat(int statId, out int value) => TryReadStat(0u, statId, out value);

    /// <inheritdoc/>
    public IReadOnlyDictionary<int, int> GetLocalPlayerStats() => GetAllStats(0u);
}

/// <summary>Managed <see cref="IWorldService"/> implementation.</summary>
public sealed class WorldService(INativeBridge bridge, GameLifecycle lifecycle) : IWorldService
{
    /// <inheritdoc/>
    public string CurrentAreaId => lifecycle.CurrentAreaId ?? string.Empty;

    /// <inheritdoc/>
    public IReadOnlyCollection<WorldObjectInfo> Objects => bridge.GetWorldObjects();

    /// <inheritdoc/>
    public WorldObjectInfo? FindObject(uint objectId) =>
        bridge.GetWorldObjects().FirstOrDefault(o => o.ObjectId == objectId);

    /// <summary>
    /// Spawns a client-only world object from an extensionless <c>.ot</c> metadata path (optional <c>.ot</c> suffix).
    /// Non-<c>.ot</c> asset paths (e.g. <c>.ao</c>, <c>.epk</c>) are rejected by the native shim.
    /// </summary>
    public uint SpawnClientObject(string metadataPath, float x, float y, float z = 0f)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metadataPath);
        return bridge.SpawnClientObject(metadataPath, x, y, z);
    }

    /// <summary>
    /// Spawns a client-only world object with yaw rotation and uniform scale from an extensionless <c>.ot</c> metadata path
    /// (optional <c>.ot</c> suffix). Non-<c>.ot</c> asset paths (e.g. <c>.ao</c>, <c>.epk</c>) are rejected by the native shim.
    /// </summary>
    public uint SpawnClientObject(string metadataPath, float x, float y, float z, float rotationRadians, float scale = 1.0f)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metadataPath);
        if (!float.IsFinite(rotationRadians))
        {
            throw new ArgumentOutOfRangeException(nameof(rotationRadians), "Rotation must be a finite number.");
        }

        if (!float.IsFinite(scale) || scale <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "Scale must be a finite positive number.");
        }

        return bridge.SpawnClientObject(metadataPath, x, y, z, rotationRadians, scale);
    }

    /// <inheritdoc/>
    public bool RemoveClientObject(uint objectId) => bridge.RemoveClientObject(objectId);

    /// <summary>
    /// Plays an effect on <paramref name="targetObjectId"/> using an extensionless <c>.ot</c> metadata path
    /// (optional <c>.ot</c> suffix) or a bare GEAL entity function name. Non-<c>.ot</c> asset paths
    /// (such as <c>.ao</c> or <c>.epk</c>) are rejected cleanly by the native shim.
    /// </summary>
    public void PlayEffect(uint targetObjectId, string effectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(effectPath);
        bridge.PlayEffect(targetObjectId, effectPath);
    }

    /// <inheritdoc/>
    public WorldEntitySnapshot? GetLocalPlayer() => bridge.GetLocalPlayerEntity();

    /// <inheritdoc/>
    public IReadOnlyList<WorldEntitySnapshot> GetEntities(WorldEntityQuery? query = null)
    {
        WorldEntityQuery? effectiveQuery = query;
        if (query is { MaxDistance: not null, Origin: null })
        {
            WorldEntitySnapshot? player = bridge.GetLocalPlayerEntity();
            if (player is not null)
            {
                effectiveQuery = query with { Origin = player.Position };
            }
        }

        IReadOnlyList<WorldEntitySnapshot> entities = bridge.GetWorldEntities(effectiveQuery);
        if (effectiveQuery is null)
        {
            return entities;
        }

        // Apply client-side guard filtering so Managed + Native + Simulated bridges have 100% identical query semantics.
        List<WorldEntitySnapshot>? filtered = null;
        float? maxDistSq = effectiveQuery.MaxDistance is float md && md >= 0f ? md * md : null;
        WorldPosition3D? origin = effectiveQuery.Origin;
        int maxResults = effectiveQuery.MaxResults ?? int.MaxValue;

        for (int i = 0; i < entities.Count; i++)
        {
            WorldEntitySnapshot e = entities[i];
            if (!MatchesQuery(e, effectiveQuery, origin, maxDistSq))
            {
                filtered ??= [.. entities.Take(i)];
                continue;
            }

            if (filtered is not null)
            {
                if (filtered.Count >= maxResults)
                {
                    break;
                }

                filtered.Add(e);
            }
            else if (i >= maxResults)
            {
                filtered = [.. entities.Take(maxResults)];
                break;
            }
        }

        return filtered ?? entities;
    }

    /// <inheritdoc/>
    public WorldEntitySnapshot? GetEntity(uint entityId) => bridge.GetWorldEntity(entityId);

    /// <inheritdoc/>
    public IReadOnlyList<WorldEntitySnapshot> GetWorldItems(bool filterVisibleOnly = false, float? maxDistance = null) =>
        GetEntities(new WorldEntityQuery(
            RequiredKind: WorldEntityKind.WorldItem,
            MaxDistance: maxDistance,
            FilterVisibleOnly: filterVisibleOnly));

    /// <inheritdoc/>
    public WorldPosition3D? GetEntityPosition(uint entityId)
    {
        if (bridge.GetWorldEntity(entityId) is { } entity)
        {
            return entity.Position;
        }

        if (FindObject(entityId) is { } obj)
        {
            return new WorldPosition3D(obj.X, obj.Y, obj.Z);
        }

        return null;
    }

    private static bool MatchesQuery(WorldEntitySnapshot entity, WorldEntityQuery query, WorldPosition3D? origin, float? maxDistSq)
    {
        if (query.RequiredKind != WorldEntityKind.None && (entity.Kind & query.RequiredKind) != query.RequiredKind)
        {
            return false;
        }

        if (query.ExcludedKind != WorldEntityKind.None && (entity.Kind & query.ExcludedKind) != 0)
        {
            return false;
        }

        if (query.FilterVisibleOnly && !entity.IsFilterVisible)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(query.MetadataPrefix) &&
            !entity.MetadataPath.StartsWith(query.MetadataPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (maxDistSq.HasValue && origin.HasValue)
        {
            float dx = entity.Position.X - origin.Value.X;
            float dy = entity.Position.Y - origin.Value.Y;
            float dz = entity.Position.Z - origin.Value.Z;
            if ((dx * dx) + (dy * dy) + (dz * dz) > maxDistSq.Value)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Managed <see cref="IGameLifecycle"/> implementation.</summary>
public sealed class GameLifecycle : IGameLifecycle
{
    private readonly INativeBridge? _bridge;
    private long _frameCount;
    private string? _currentAreaId;
    private AreaInfo? _currentArea;
    private bool _hasObservedAreaState;

    /// <summary>Creates a new <see cref="GameLifecycle"/> optionally backed by <paramref name="bridge"/> for live area queries.</summary>
    public GameLifecycle(INativeBridge? bridge = null)
    {
        _bridge = bridge;
    }

    /// <inheritdoc/>
    public long FrameCount => Interlocked.Read(ref _frameCount);

    /// <inheritdoc/>
    public bool IsConnected { get; private set; }

    /// <inheritdoc/>
    public string? CurrentAreaId => _hasObservedAreaState ? _currentAreaId : (_currentAreaId ?? _bridge?.GetAreaInfo()?.AreaId);

    /// <inheritdoc/>
    public AreaInfo? CurrentArea => _hasObservedAreaState ? _currentArea : (_currentArea ?? _bridge?.GetAreaInfo());

    /// <inheritdoc/>
    public event Action<float>? Frame;

    /// <inheritdoc/>
    public event Action<AreaEventArgs>? AreaEntered;

    /// <inheritdoc/>
    public event Action<string>? AreaLeft;

    /// <inheritdoc/>
    public event Action? Connected;

    /// <inheritdoc/>
    public event Action<string?>? Disconnected;

    internal void RaiseFrame(float deltaSeconds)
    {
        Interlocked.Increment(ref _frameCount);
        Action<float>? snapshot = Frame;
        if (snapshot is null)
        {
            return;
        }

        foreach (Delegate d in snapshot.GetInvocationList())
        {
            try { ((Action<float>)d)(deltaSeconds); }
            catch { }
        }
    }

    internal void RaiseAreaEntered(AreaEventArgs args)
    {
        _hasObservedAreaState = true;
        _currentAreaId = args.AreaId;
        _currentArea = args.Area ?? _bridge?.GetAreaInfo();
        Action<AreaEventArgs>? snapshot = AreaEntered;
        if (snapshot is null)
        {
            return;
        }

        foreach (Delegate d in snapshot.GetInvocationList())
        {
            try { ((Action<AreaEventArgs>)d)(args); }
            catch { }
        }
    }

    internal void RaiseAreaLeft(string areaId)
    {
        _hasObservedAreaState = true;
        if (string.Equals(_currentAreaId, areaId, StringComparison.Ordinal) ||
            string.IsNullOrEmpty(areaId))
        {
            _currentAreaId = null;
            _currentArea = null;
        }
        else if (_currentArea is not null && string.Equals(_currentArea.AreaId, areaId, StringComparison.Ordinal))
        {
            _currentArea = null;
        }

        Action<string>? snapshot = AreaLeft;
        if (snapshot is null)
        {
            return;
        }

        foreach (Delegate d in snapshot.GetInvocationList())
        {
            try { ((Action<string>)d)(areaId); }
            catch { }
        }
    }

    internal void RaiseConnected()
    {
        IsConnected = true;
        Action? snapshot = Connected;
        if (snapshot is null)
        {
            return;
        }

        foreach (Delegate d in snapshot.GetInvocationList())
        {
            try { ((Action)d)(); }
            catch { }
        }
    }

    internal void RaiseDisconnected(string? reason)
    {
        IsConnected = false;
        _hasObservedAreaState = true;
        _currentAreaId = null;
        _currentArea = null;
        Action<string?>? snapshot = Disconnected;
        if (snapshot is null)
        {
            return;
        }

        foreach (Delegate d in snapshot.GetInvocationList())
        {
            try { ((Action<string?>)d)(reason); }
            catch { }
        }
    }
}

/// <summary>
/// Managed <see cref="IRulesetRegistry"/> implementation for 3.29 native <c>rulesets.creator_table</c>
/// free slots (<c>0x00..0xAE</c>). Rejects occupied stock slots (including <c>0xA0</c>, which remains
/// reserved exclusively for the <c>0x02C9</c> filter-hook host transport channel).
/// </summary>
public sealed class RulesetRegistry : IRulesetRegistry
{
    private readonly INativeBridge _bridge;
    private readonly Dictionary<byte, (Func<IClientRuleset> Factory, IClientRuleset Instance, IDisposable NativeSub, bool IsNativeCreatorTable)> _rulesets = [];
    private readonly Lock _gate = new();

    /// <summary>Creates a new <see cref="RulesetRegistry"/>.</summary>
    public RulesetRegistry(INativeBridge bridge)
    {
        _bridge = bridge;
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<byte> RegisteredIds
    {
        get { lock (_gate) { return [.. _rulesets.Keys]; } }
    }

    /// <inheritdoc/>
    public IDisposable Register<TRuleset>() where TRuleset : class, IClientRuleset, new()
    {
        var probe = new TRuleset();
        return Register(probe.RulesetId, static () => new TRuleset());
    }

    /// <inheritdoc/>
    public IDisposable Register(byte rulesetId, Func<IClientRuleset> factory) =>
        RegisterInternal(rulesetId, factory, requireNativeCreatorTableSlot: false);

    /// <inheritdoc/>
    public IDisposable RegisterNative<TRuleset>() where TRuleset : class, IClientRuleset, new()
    {
        var probe = new TRuleset();
        return RegisterNative(probe.RulesetId, static () => new TRuleset());
    }

    /// <inheritdoc/>
    public IDisposable RegisterNative(byte rulesetId, Func<IClientRuleset> factory) =>
        RegisterInternal(rulesetId, factory, requireNativeCreatorTableSlot: true);

    private ActionDisposable RegisterInternal(byte rulesetId, Func<IClientRuleset> factory, bool requireNativeCreatorTableSlot)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (rulesetId > IRulesetRegistry.MaxRulesetId)
        {
            throw new ArgumentOutOfRangeException(nameof(rulesetId), $"Ruleset id 0x{rulesetId:X2} exceeds 3.29 creator table max 0x{IRulesetRegistry.MaxRulesetId:X2}.");
        }

        bool isFreeNativeSlot = IRulesetRegistry.IsNativeSlotFree(rulesetId);
        if (rulesetId == IRulesetRegistry.DefaultHostChannelRulesetId || (requireNativeCreatorTableSlot && !isFreeNativeSlot))
        {
            throw new InvalidOperationException(
                $"Ruleset slot 0x{rulesetId:X2} is occupied by a stock 3.29.3d ruleset in rulesets.creator_table " +
                $"(0xA0 is reserved only for the 0x02C9 filter-hook host transport). " +
                $"Use IRulesetRegistry.FirstFreeNativeRulesetId (0x{IRulesetRegistry.FirstFreeNativeRulesetId:X2}) or another verified free slot (0xAE, 0xAA, 0xA9, 0xA3, 0x9C, 0x90).");
        }

        IClientRuleset instance = factory();
        lock (_gate)
        {
            if (_rulesets.ContainsKey(rulesetId))
            {
                throw new InvalidOperationException($"Ruleset slot 0x{rulesetId:X2} is already registered.");
            }

            IDisposable sub = isFreeNativeSlot
                ? _bridge.RegisterNativeRuleset(
                    rulesetId,
                    area =>
                    {
                        try { instance.OnAreaEntered(area); }
                        catch { }
                    },
                    payload =>
                    {
                        try { instance.OnRulesetUpdate(payload.Span); }
                        catch { }
                    },
                    dt =>
                    {
                        try { instance.OnFrame(dt); }
                        catch { }
                    },
                    () =>
                    {
                        try { instance.OnAreaLeft(); }
                        catch { }
                    })
                : _bridge.RegisterRulesetReceiver(rulesetId, payload =>
                {
                    try { instance.OnRulesetUpdate(payload.Span); }
                    catch { }
                });

            _rulesets[rulesetId] = (factory, instance, sub, isFreeNativeSlot);
        }

        return new ActionDisposable(() =>
        {
            IDisposable? sub = null;
            lock (_gate)
            {
                if (_rulesets.TryGetValue(rulesetId, out var entry))
                {
                    sub = entry.NativeSub;
                    _rulesets.Remove(rulesetId);
                }
            }

            sub?.Dispose();
        });
    }

    /// <inheritdoc/>
    public bool IsRegistered(byte rulesetId)
    {
        lock (_gate)
        {
            return _rulesets.ContainsKey(rulesetId);
        }
    }

    /// <inheritdoc/>
    public bool TryDispatchUpdate(byte rulesetId, ReadOnlySpan<byte> payload)
    {
        IClientRuleset? instance;
        lock (_gate)
        {
            instance = _rulesets.TryGetValue(rulesetId, out var entry) ? entry.Instance : null;
        }

        if (instance is null)
        {
            return false;
        }

        try { instance.OnRulesetUpdate(payload); }
        catch { }
        return true;
    }

    internal void NotifyAreaEntered(AreaEventArgs args)
    {
        IClientRuleset[] instances;
        bool nativeDrives = _bridge.DrivesNativeRulesetLifecycle;
        lock (_gate)
        {
            instances = [.. _rulesets.Values.Where(v => !(nativeDrives && v.IsNativeCreatorTable)).Select(v => v.Instance)];
        }

        foreach (IClientRuleset rs in instances)
        {
            try { rs.OnAreaEntered(args); }
            catch { }
        }
    }

    internal void NotifyFrame(float dt)
    {
        IClientRuleset[] instances;
        bool nativeDrives = _bridge.DrivesNativeRulesetLifecycle;
        lock (_gate)
        {
            instances = [.. _rulesets.Values.Where(v => !(nativeDrives && v.IsNativeCreatorTable)).Select(v => v.Instance)];
        }

        foreach (IClientRuleset rs in instances)
        {
            try { rs.OnFrame(dt); }
            catch { }
        }
    }

    internal void NotifyAreaLeft()
    {
        IClientRuleset[] instances;
        bool nativeDrives = _bridge.DrivesNativeRulesetLifecycle;
        lock (_gate)
        {
            instances = [.. _rulesets.Values.Where(v => !(nativeDrives && v.IsNativeCreatorTable)).Select(v => v.Instance)];
        }

        foreach (IClientRuleset rs in instances)
        {
            try { rs.OnAreaLeft(); }
            catch { }
        }
    }
}

/// <summary>Managed <see cref="IOpcodeRegistry"/> implementation for handler groups and reserved opcodes (<c>0x2fc+</c>).</summary>
public sealed class OpcodeRegistry(INativeBridge bridge) : IOpcodeRegistry
{
    private sealed record GroupEntry(ushort MinOpcode, ushort MaxOpcode, Action<ushort, ReadOnlyMemory<byte>>? Handler, IDisposable NativeSub);

    private readonly Dictionary<ushort, List<(Action<ReadOnlyMemory<byte>> Handler, IDisposable NativeSub)>> _handlers = [];
    private readonly List<GroupEntry> _groups = [];
    private readonly Lock _gate = new();

    /// <inheritdoc/>
    public IReadOnlyCollection<ushort> RegisteredOpcodes
    {
        get
        {
            lock (_gate)
            {
                var set = new HashSet<ushort>(_handlers.Keys);
                foreach (GroupEntry g in _groups)
                {
                    if (g.MinOpcode == g.MaxOpcode)
                    {
                        set.Add(g.MinOpcode);
                    }
                }

                return [.. set];
            }
        }
    }

    /// <inheritdoc/>
    public IDisposable RegisterHandler(ushort opcode, Action<ReadOnlyMemory<byte>> handler, string? groupName = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ValidateReservedOpcode(opcode);

        IDisposable nativeSub = bridge.RegisterRawOpcodeReceiver(opcode, handler);
        var entry = (handler, nativeSub);
        lock (_gate)
        {
            if (!_handlers.TryGetValue(opcode, out List<(Action<ReadOnlyMemory<byte>>, IDisposable)>? list))
            {
                list = [];
                _handlers[opcode] = list;
            }

            list.Add(entry);
        }

        return new ActionDisposable(() =>
        {
            nativeSub.Dispose();
            lock (_gate)
            {
                if (_handlers.TryGetValue(opcode, out List<(Action<ReadOnlyMemory<byte>>, IDisposable)>? list))
                {
                    list.Remove(entry);
                    if (list.Count == 0)
                    {
                        _handlers.Remove(opcode);
                    }
                }
            }
        });
    }

    /// <inheritdoc/>
    public IDisposable RegisterGroup(ushort minOpcode, ushort maxOpcode, Action<ushort, ReadOnlyMemory<byte>> handler, string? groupName = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ValidateReservedRange(minOpcode, maxOpcode);

        IDisposable nativeSub = bridge.RegisterRawOpcodeGroup(minOpcode, maxOpcode, handler);
        var entry = new GroupEntry(minOpcode, maxOpcode, handler, nativeSub);
        lock (_gate)
        {
            _groups.Add(entry);
        }

        return new ActionDisposable(() =>
        {
            nativeSub.Dispose();
            lock (_gate)
            {
                _groups.Remove(entry);
            }
        });
    }

    /// <inheritdoc/>
    public IDisposable RegisterStreamHandler(ushort minOpcode, ushort maxOpcode, Func<ushort, INativePacketStream, ReadOnlyMemory<byte>, bool> handler, string? groupName = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ValidateReservedRange(minOpcode, maxOpcode);

        IDisposable nativeSub = bridge.RegisterRawOpcodeStreamHandler(minOpcode, maxOpcode, handler);
        var entry = new GroupEntry(minOpcode, maxOpcode, null, nativeSub);
        lock (_gate)
        {
            _groups.Add(entry);
        }

        return new ActionDisposable(() =>
        {
            nativeSub.Dispose();
            lock (_gate)
            {
                _groups.Remove(entry);
            }
        });
    }

    /// <inheritdoc/>
    public bool IsRegistered(ushort opcode)
    {
        lock (_gate)
        {
            if (_handlers.TryGetValue(opcode, out var list) && list.Count > 0)
            {
                return true;
            }

            return _groups.Any(g => opcode >= g.MinOpcode && opcode <= g.MaxOpcode);
        }
    }

    /// <inheritdoc/>
    public bool TryDispatch(ushort opcode, ReadOnlyMemory<byte> payload)
    {
        Action<ReadOnlyMemory<byte>>[] snapshot;
        GroupEntry[] groupSnapshot;
        lock (_gate)
        {
            snapshot = _handlers.TryGetValue(opcode, out var list) && list.Count > 0
                ? [.. list.Select(e => e.Handler)]
                : [];
            groupSnapshot = [.. _groups.Where(g => opcode >= g.MinOpcode && opcode <= g.MaxOpcode && g.Handler is not null)];
        }

        if (snapshot.Length == 0 && groupSnapshot.Length == 0)
        {
            return false;
        }

        foreach (Action<ReadOnlyMemory<byte>> handler in snapshot)
        {
            try { handler(payload); }
            catch { }
        }

        foreach (GroupEntry group in groupSnapshot)
        {
            try { group.Handler!(opcode, payload); }
            catch { }
        }

        return true;
    }

    private static void ValidateReservedOpcode(ushort opcode)
    {
        if (opcode < IOpcodeRegistry.FirstReservedOpcode)
        {
            throw new ArgumentOutOfRangeException(
                nameof(opcode),
                $"Custom opcode 0x{opcode:X4} is below reserved minimum 0x{IOpcodeRegistry.FirstReservedOpcode:X4}.");
        }
    }

    private static void ValidateReservedRange(ushort minOpcode, ushort maxOpcode)
    {
        ValidateReservedOpcode(minOpcode);
        if (maxOpcode < minOpcode)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxOpcode),
                $"maxOpcode (0x{maxOpcode:X4}) must be >= minOpcode (0x{minOpcode:X4}).");
        }
    }
}

/// <summary>Managed <see cref="IContentRegistry"/> implementation tracking append-only dat rows, item bases, world objects, overlays, and the live <c>content-row-map.json</c>.</summary>
public sealed class ContentRegistry(FileOverlayService files) : IContentRegistry
{
    private readonly List<DatRowDeclaration> _datRows = [];
    private readonly List<ItemBaseDeclaration> _itemBases = [];
    private readonly List<WorldObjectDeclaration> _worldObjects = [];
    private readonly List<ObjectOverlayDeclaration> _overlays = [];
    private readonly Lock _gate = new();
    private ContentRowMap? _rowMap;

    /// <inheritdoc/>
    public ContentRowMap? RowMap => Volatile.Read(ref _rowMap);

    /// <inheritdoc/>
    public string? ContentRowMapDigest => Volatile.Read(ref _rowMap)?.Digest;

    /// <inheritdoc/>
    public IReadOnlyList<DatRowDeclaration> DatRows { get { lock (_gate) { return [.. _datRows]; } } }

    /// <inheritdoc/>
    public IReadOnlyList<ItemBaseDeclaration> ItemBases { get { lock (_gate) { return [.. _itemBases]; } } }

    /// <inheritdoc/>
    public IReadOnlyList<WorldObjectDeclaration> WorldObjects { get { lock (_gate) { return [.. _worldObjects]; } } }

    /// <inheritdoc/>
    public IReadOnlyList<ObjectOverlayDeclaration> Overlays { get { lock (_gate) { return [.. _overlays]; } } }

    /// <summary>Sets or replaces the live <see cref="ContentRowMap"/> loaded from <c>Mods/Client/.merged-cache/content-row-map.json</c>.</summary>
    public void SetRowMap(ContentRowMap? rowMap) => Volatile.Write(ref _rowMap, rowMap);

    /// <summary>Loads a <see cref="ContentRowMap"/> from a <c>content-row-map.json</c> file on disk and binds it as the live <see cref="RowMap"/>.</summary>
    public ContentRowMap LoadRowMapFromFile(string path)
    {
        ContentRowMap map = ContentRowMap.LoadFromFile(path);
        SetRowMap(map);
        return map;
    }

    /// <inheritdoc/>
    public bool TryResolveRow(string tableName, string entryKey, [NotNullWhen(true)] out ContentRowEntry? entry)
    {
        ContentRowMap? map = Volatile.Read(ref _rowMap);
        if (map is not null && map.TryResolveRow(tableName, entryKey, out entry))
        {
            return true;
        }

        entry = null;
        return false;
    }

    /// <summary>Looks up a live <see cref="DeclareDatRow"/> registration by table name and entry key.</summary>
    public bool TryGetDeclaredDatRow(string tableName, string entryKey, [NotNullWhen(true)] out DatRowDeclaration? declaration)
    {
        if (string.IsNullOrWhiteSpace(tableName) || string.IsNullOrWhiteSpace(entryKey))
        {
            declaration = null;
            return false;
        }

        lock (_gate)
        {
            foreach (DatRowDeclaration row in _datRows)
            {
                if (string.Equals(row.TableName, tableName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(row.EntryKey, entryKey, StringComparison.OrdinalIgnoreCase))
                {
                    declaration = row;
                    return true;
                }
            }
        }

        declaration = null;
        return false;
    }

    /// <inheritdoc/>
    public IDisposable DeclareDatRow(DatRowDeclaration row)
    {
        ArgumentNullException.ThrowIfNull(row);
        lock (_gate)
        {
            if (_datRows.Any(d =>
                string.Equals(d.TableName, row.TableName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(d.EntryKey, row.EntryKey, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Duplicate DatRow declaration for table '{row.TableName}', key '{row.EntryKey}'.");
            }
            _datRows.Add(row);
        }
        return new ActionDisposable(() => { lock (_gate) { _datRows.Remove(row); } });
    }

    /// <inheritdoc/>
    public IDisposable DeclareItemBase(ItemBaseDeclaration itemBase)
    {
        ArgumentNullException.ThrowIfNull(itemBase);
        lock (_gate)
        {
            if (_itemBases.Any(b => string.Equals(b.MetadataPath, itemBase.MetadataPath, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Duplicate ItemBase declaration for metadata path '{itemBase.MetadataPath}'.");
            }
            _itemBases.Add(itemBase);
        }
        return new ActionDisposable(() => { lock (_gate) { _itemBases.Remove(itemBase); } });
    }

    /// <inheritdoc/>
    public IDisposable DeclareWorldObject(WorldObjectDeclaration worldObject)
    {
        ArgumentNullException.ThrowIfNull(worldObject);
        lock (_gate)
        {
            if (_worldObjects.Any(w => string.Equals(w.MetadataPath, worldObject.MetadataPath, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Duplicate WorldObject declaration for metadata path '{worldObject.MetadataPath}'.");
            }
            _worldObjects.Add(worldObject);
        }

        IDisposable? overlaySub = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(worldObject.OtOverlayContent))
            {
                string otPath = worldObject.MetadataPath.EndsWith(".ot", StringComparison.OrdinalIgnoreCase)
                    ? worldObject.MetadataPath
                    : worldObject.MetadataPath + ".ot";
                overlaySub = files.RegisterVirtualTextFile(otPath, worldObject.OtOverlayContent);
            }
        }
        catch
        {
            lock (_gate) { _worldObjects.Remove(worldObject); }
            throw;
        }

        return new ActionDisposable(() =>
        {
            overlaySub?.Dispose();
            lock (_gate) { _worldObjects.Remove(worldObject); }
        });
    }

    /// <inheritdoc/>
    public IDisposable DeclareOverlay(ObjectOverlayDeclaration overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        IDisposable fileSub = files.RegisterVirtualTextFile(overlay.VirtualPath, overlay.Content);
        lock (_gate) { _overlays.Add(overlay); }
        return new ActionDisposable(() =>
        {
            fileSub.Dispose();
            lock (_gate) { _overlays.Remove(overlay); }
        });
    }

    /// <inheritdoc/>
    public uint ComputeDeterministicId(string modId, string entryKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryKey);
        byte[] bytes = Encoding.UTF8.GetBytes($"{modId.Trim().ToLowerInvariant()}:{entryKey.Trim().ToLowerInvariant()}");
        uint hash = 2166136261u;
        foreach (byte b in bytes)
        {
            hash ^= b;
            hash = unchecked(hash * 16777619u);
        }

        return hash;
    }
}

/// <summary>Backing <see cref="IRawMemory"/> service delegating to <see cref="IAddressLibrary"/> and <see cref="INativeBridge"/>.</summary>
public sealed class RawMemoryService(IAddressLibrary addresses, INativeBridge bridge) : IRawMemory
{
    /// <inheritdoc/>
    public nint GetAddress(string symbolName) => addresses.ResolveAddress(symbolName);

    /// <inheritdoc/>
    public T Read<T>(nint address) where T : unmanaged
    {
        Span<byte> buf = stackalloc byte[Unsafe.SizeOf<T>()];
        bridge.ReadMemory(address, buf);
        return MemoryMarshal.Read<T>(buf);
    }

    /// <inheritdoc/>
    public void Write<T>(nint address, T value) where T : unmanaged
    {
        Span<byte> buf = stackalloc byte[Unsafe.SizeOf<T>()];
        MemoryMarshal.Write(buf, in value);
        bridge.WriteMemory(address, buf);
    }

    /// <inheritdoc/>
    public byte[] ReadBytes(nint address, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        byte[] buf = new byte[count];
        if (count > 0)
        {
            bridge.ReadMemory(address, buf);
        }

        return buf;
    }

    /// <inheritdoc/>
    public void WriteBytes(nint address, ReadOnlySpan<byte> data)
    {
        if (!data.IsEmpty)
        {
            bridge.WriteMemory(address, data);
        }
    }

    /// <inheritdoc/>
    public IDisposable InstallHook(string symbolName, nint detourTarget, out nint originalTrampoline, HookType hookType = HookType.InlineDetour) =>
        InstallHook(symbolName, detourTarget, out originalTrampoline, hookType, vtableSlotIndex: 0);

    /// <inheritdoc/>
    public IDisposable InstallHook(
        string symbolName,
        nint detourTarget,
        out nint originalTrampoline,
        HookType hookType,
        uint vtableSlotIndex)
    {
        ulong handle = hookType switch
        {
            HookType.InlineDetour => bridge.InstallInlineHook(symbolName, detourTarget, out originalTrampoline),
            HookType.VtableSlot => bridge.InstallVtableHook(addresses.ResolveAddress(symbolName), vtableSlotIndex, detourTarget, out originalTrampoline),
            _ => throw new ArgumentOutOfRangeException(nameof(hookType)),
        };

        return new ActionDisposable(() => bridge.RemoveHook(handle));
    }

    /// <inheritdoc/>
    public IDisposable InstallVtableHook(nint vtableAddress, uint slotIndex, nint detourTarget, out nint originalSlot)
    {
        ulong handle = bridge.InstallVtableHook(vtableAddress, slotIndex, detourTarget, out originalSlot);
        return new ActionDisposable(() => bridge.RemoveHook(handle));
    }

    /// <inheritdoc/>
    public IDisposable InstallHookChain(string symbolName, HookPhase phase, Action<HookContext> callback, int priority = 0)
    {
        ulong handle = bridge.InstallInlineHookChain(symbolName, phase, priority, callback);
        return new ActionDisposable(() => bridge.RemoveHook(handle));
    }

    /// <inheritdoc/>
    public IDisposable InstallHookChain(nint address, string? expectedBytesHex, HookPhase phase, Action<HookContext> callback, int priority = 0)
    {
        ulong handle = bridge.InstallInlineHookChainAt(address, expectedBytesHex, phase, priority, callback);
        return new ActionDisposable(() => bridge.RemoveHook(handle));
    }

    /// <inheritdoc/>
    public IDisposable InstallVtableHookChain(nint vtableAddress, uint slotIndex, HookPhase phase, Action<HookContext> callback, int priority = 0)
    {
        ulong handle = bridge.InstallVtableHookChain(vtableAddress, slotIndex, phase, priority, callback);
        return new ActionDisposable(() => bridge.RemoveHook(handle));
    }

    /// <inheritdoc/>
    public nint Call(string symbolName, params nint[] args) => bridge.InvokeNative(symbolName, args);

    /// <inheritdoc/>
    public NativeCallResult CallEx(string symbolName, ReadOnlySpan<NativeCallArgument> args, bool returnIsFloat = false)
    {
        nint address = addresses.ResolveAddress(symbolName);
        return bridge.InvokeNativeEx(address, args, returnIsFloat);
    }

    /// <inheritdoc/>
    public NativeCallResult CallEx(nint targetAddress, ReadOnlySpan<NativeCallArgument> args, bool returnIsFloat = false) =>
        bridge.InvokeNativeEx(targetAddress, args, returnIsFloat);
}

/// <summary>Per-mod <see cref="IModScheduler"/> marshaling work onto the game thread via <see cref="INativeBridge"/>.</summary>
public sealed class GameThreadScheduler(INativeBridge bridge) : IModScheduler
{
    /// <inheritdoc/>
    public bool IsOnGameThread => bridge.IsOnGameThread;

    /// <inheritdoc/>
    public void RunOnGameThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (bridge.IsOnGameThread)
        {
            action();
            return;
        }

        if (!bridge.EnqueueGameThread(action))
        {
            throw new InvalidOperationException("Failed to enqueue callback onto the game thread because the queue is full or disposed.");
        }
    }

    /// <inheritdoc/>
    public Task RunOnGameThreadAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool enqueued = bridge.EnqueueGameThread(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                action();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        if (!enqueued)
        {
            tcs.TrySetException(new InvalidOperationException("Failed to enqueue callback onto the game thread because the queue is full or disposed."));
        }

        return tcs.Task;
    }

    /// <inheritdoc/>
    public Task<T> RunOnGameThreadAsync<T>(Func<T> func, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(func);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool enqueued = bridge.EnqueueGameThread(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        if (!enqueued)
        {
            tcs.TrySetException(new InvalidOperationException("Failed to enqueue callback onto the game thread because the queue is full or disposed."));
        }

        return tcs.Task;
    }
}

/// <summary>Default in-memory + console logger implementation for client mods.</summary>
public sealed class ModLogger(string modId, Action<string, string, string>? sink = null) : IModLogger
{
    /// <inheritdoc/>
    public void Debug(string message) => Emit("DEBUG", message);

    /// <inheritdoc/>
    public void Info(string message) => Emit("INFO", message);

    /// <inheritdoc/>
    public void Warn(string message) => Emit("WARN", message);

    /// <inheritdoc/>
    public void Error(string message, Exception? exception = null) =>
        Emit("ERROR", exception is null ? message : $"{message}: {exception}");

    private void Emit(string level, string message) => sink?.Invoke(level, modId, message);
}
