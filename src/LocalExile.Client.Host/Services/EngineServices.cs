using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
/// Layered file overlay service supporting virtual files, loose directories, and packed bundle overlays
/// with deterministic priority ordering, automatic UTF-16LE BOM CRLF encoding for client text files,
/// and asset invalidation.
/// </summary>
public sealed class FileOverlayService : IFileOverlayService
{
    private sealed record VirtualFileLayer(long Id, string NormalizedPath, byte[] Bytes, string OwnerModId, int Priority);
    private sealed record DirectoryLayer(long Id, string RootDirectory, string OwnerModId, int Priority);
    private sealed record BundleEntrySlice(string NormalizedPath, long Offset, int Size);
    private sealed record BundleLayer(long Id, string BundlePath, string ManifestPath, string OwnerModId, int Priority, Dictionary<string, BundleEntrySlice> Entries);

    private readonly INativeBridge _bridge;
    private readonly List<VirtualFileLayer> _virtualFiles = [];
    private readonly List<DirectoryLayer> _directories = [];
    private readonly List<BundleLayer> _bundles = [];
    private readonly Lock _gate = new();
    private long _nextLayerId = 1;

    /// <summary>Creates a new <see cref="FileOverlayService"/> wired to <paramref name="bridge"/>.</summary>
    public FileOverlayService(INativeBridge bridge)
    {
        _bridge = bridge;
        _bridge.RegisterFileOverlayResolver(ResolveOverlayOnly);
    }

    /// <inheritdoc/>
    public IDisposable RegisterDirectory(string rootDirectory, int priority = 0) =>
        RegisterDirectoryForMod("host", rootDirectory, priority);

    internal IDisposable RegisterDirectoryForMod(string ownerModId, string rootDirectory, int priority)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        string full = Path.GetFullPath(rootDirectory);
        long id = Interlocked.Increment(ref _nextLayerId);
        var layer = new DirectoryLayer(id, full, ownerModId, priority);
        lock (_gate)
        {
            _directories.Add(layer);
        }

        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                _directories.RemoveAll(l => l.Id == id);
            }
        });
    }

    /// <inheritdoc/>
    public IDisposable RegisterVirtualFile(string virtualPath, ReadOnlyMemory<byte> rawBytes, int priority = 100) =>
        RegisterVirtualFileForMod("host", virtualPath, rawBytes, priority);

    internal IDisposable RegisterVirtualFileForMod(string ownerModId, string virtualPath, ReadOnlyMemory<byte> rawBytes, int priority)
    {
        string normalized = ClientTextEncoding.NormalizeVirtualPath(virtualPath);
        long id = Interlocked.Increment(ref _nextLayerId);
        var layer = new VirtualFileLayer(id, normalized, rawBytes.ToArray(), ownerModId, priority);
        lock (_gate)
        {
            _virtualFiles.Add(layer);
        }

        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                _virtualFiles.RemoveAll(l => l.Id == id);
            }
        });
    }

    /// <inheritdoc/>
    public IDisposable RegisterVirtualTextFile(string virtualPath, string text, int priority = 100) =>
        RegisterVirtualTextFileForMod("host", virtualPath, text, priority);

    internal IDisposable RegisterVirtualTextFileForMod(string ownerModId, string virtualPath, string text, int priority)
    {
        ArgumentNullException.ThrowIfNull(text);
        byte[] encoded = ClientTextEncoding.RequiresUtf16LeBomCrlf(virtualPath)
            ? ClientTextEncoding.EncodeUtf16LeBomCrlf(text)
            : Encoding.UTF8.GetBytes(text);
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
        lock (_gate)
        {
            _bundles.Add(layer);
        }

        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                _bundles.RemoveAll(l => l.Id == id);
            }
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
    public string? ReadAllText(string virtualPath)
    {
        byte[]? bytes = ReadAllBytes(virtualPath);
        return bytes is null ? null : ClientTextEncoding.DecodeText(bytes);
    }

    /// <inheritdoc/>
    public void Invalidate(string virtualPath)
    {
        string normalized = ClientTextEncoding.NormalizeVirtualPath(virtualPath);
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
        var candidates = new List<(int Priority, int KindRank, long Id, Func<byte[]?> Reader)>();

        lock (_gate)
        {
            foreach (VirtualFileLayer vf in _virtualFiles)
            {
                if (string.Equals(vf.NormalizedPath, normalized, StringComparison.Ordinal))
                {
                    candidates.Add((vf.Priority, 3, vf.Id, () => (byte[])vf.Bytes.Clone()));
                }
            }

            foreach (DirectoryLayer dir in _directories)
            {
                string candidatePath = ResolveLoosePath(dir.RootDirectory, normalized);
                if (File.Exists(candidatePath))
                {
                    candidates.Add((dir.Priority, 2, dir.Id, () => File.ReadAllBytes(candidatePath)));
                }
            }

            foreach (BundleLayer bundle in _bundles)
            {
                if (bundle.Entries.TryGetValue(normalized, out BundleEntrySlice? slice) && File.Exists(bundle.BundlePath))
                {
                    candidates.Add((bundle.Priority, 1, bundle.Id, () => ReadBundleSlice(bundle.BundlePath, slice)));
                }
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        // Highest priority wins; ties broken by VirtualFile > LooseDirectory > BundleOverlay, then most recently registered.
        candidates.Sort(static (a, b) =>
        {
            int p = b.Priority.CompareTo(a.Priority);
            if (p != 0)
            {
                return p;
            }

            int k = b.KindRank.CompareTo(a.KindRank);
            return k != 0 ? k : b.Id.CompareTo(a.Id);
        });

        return candidates[0].Reader();
    }

    private static string ResolveLoosePath(string rootDirectory, string normalizedVirtualPath)
    {
        string rel = normalizedVirtualPath.Replace('/', Path.DirectorySeparatorChar);
        string combined = Path.GetFullPath(Path.Combine(rootDirectory, rel));
        string rootPrefix = rootDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? rootDirectory
            : rootDirectory + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        if (File.Exists(combined))
        {
            return combined;
        }

        // Case-insensitive fallback on Linux filesystems.
        string? dir = Path.GetDirectoryName(combined);
        string fileName = Path.GetFileName(combined);
        if (dir is not null && Directory.Exists(dir))
        {
            foreach (string existing in Directory.EnumerateFiles(dir))
            {
                if (string.Equals(Path.GetFileName(existing), fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return existing;
                }
            }
        }

        return combined;
    }

    private static byte[] ReadBundleSlice(string bundlePath, BundleEntrySlice slice)
    {
        using var fs = new FileStream(bundlePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(slice.Offset, SeekOrigin.Begin);
        byte[] raw = new byte[slice.Size];
        int read = 0;
        while (read < raw.Length)
        {
            int n = fs.Read(raw, read, raw.Length - read);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        // Unwrap stored Oodle block prefix (0xCC 0x06) when present (10-verification.md §2).
        if (raw.Length >= 2 && raw[0] == 0xCC && raw[1] == 0x06)
        {
            return raw[2..];
        }

        return raw;
    }

    private static Dictionary<string, BundleEntrySlice> ParseBundleManifest(string manifestPath)
    {
        var map = new Dictionary<string, BundleEntrySlice>(StringComparer.Ordinal);
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
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
                string normalized = ClientTextEncoding.NormalizeVirtualPath(path);
                map[normalized] = new BundleEntrySlice(normalized, offset, size);
            }
        }

        return map;
    }
}

/// <summary>
/// Registry of custom GEAL built-in functions callable from <c>.ot</c> client scripts.
/// </summary>
public sealed class GealBuiltinRegistry : IGealBuiltinRegistry
{
    private readonly Dictionary<string, Func<uint, GealArguments, GealValue>> _builtins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <inheritdoc/>
    public IReadOnlyCollection<string> RegisteredNames
    {
        get { lock (_gate) { return [.. _builtins.Keys]; } }
    }

    /// <inheritdoc/>
    public IDisposable Register(string name, Func<uint, GealArguments, GealValue> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            if (_builtins.ContainsKey(name))
            {
                throw new InvalidOperationException($"GEAL built-in '{name}' is already registered.");
            }

            _builtins[name] = handler;
        }

        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                _builtins.Remove(name);
            }
        });
    }

    /// <inheritdoc/>
    public IDisposable Register(string name, Action<uint, GealArguments> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Register(name, (objId, args) =>
        {
            handler(objId, args);
            return GealValue.Null;
        });
    }

    /// <inheritdoc/>
    public bool IsRegistered(string name)
    {
        lock (_gate)
        {
            return _builtins.ContainsKey(name);
        }
    }

    /// <inheritdoc/>
    public GealInvocationResult Invoke(string name, uint objectId, params GealValue[] arguments)
    {
        Func<uint, GealArguments, GealValue>? handler;
        lock (_gate)
        {
            _builtins.TryGetValue(name, out handler);
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

    /// <summary>Creates a new <see cref="ScriptService"/>.</summary>
    public ScriptService(INativeBridge bridge, GealBuiltinRegistry builtins)
    {
        _bridge = bridge;
        _builtins = builtins;
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
}

/// <summary>Managed <see cref="IAudioService"/> implementation.</summary>
public sealed class AudioService(INativeBridge bridge) : IAudioService
{
    private readonly HashSet<int> _activeChannels = [];
    private readonly Lock _gate = new();

    /// <inheritdoc/>
    public int ActiveChannelCount
    {
        get { lock (_gate) { return _activeChannels.Count; } }
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
    public int PlaySoundFile(string virtualOggPath, float volume = 1.0f)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualOggPath);
        int handle = bridge.PlayAudio(virtualOggPath, isFmodEvent: false, Math.Clamp(volume, 0f, 1f));
        lock (_gate)
        {
            _activeChannels.Add(handle);
        }

        return handle;
    }

    /// <inheritdoc/>
    public void StopChannel(int channelHandle)
    {
        bridge.StopAudio(channelHandle);
        lock (_gate)
        {
            _activeChannels.Remove(channelHandle);
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

    /// <inheritdoc/>
    public uint SpawnClientObject(string metadataPath, float x, float y, float z = 0f)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metadataPath);
        return bridge.SpawnClientObject(metadataPath, x, y, z);
    }

    /// <inheritdoc/>
    public bool RemoveClientObject(uint objectId) => bridge.RemoveClientObject(objectId);

    /// <inheritdoc/>
    public void PlayEffect(uint targetObjectId, string effectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(effectPath);
        bridge.PlayEffect(targetObjectId, effectPath);
    }
}

/// <summary>Managed <see cref="IGameLifecycle"/> implementation.</summary>
public sealed class GameLifecycle : IGameLifecycle
{
    private long _frameCount;

    /// <inheritdoc/>
    public long FrameCount => Interlocked.Read(ref _frameCount);

    /// <inheritdoc/>
    public bool IsConnected { get; private set; }

    /// <inheritdoc/>
    public string? CurrentAreaId { get; private set; }

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
        Frame?.Invoke(deltaSeconds);
    }

    internal void RaiseAreaEntered(AreaEventArgs args)
    {
        CurrentAreaId = args.AreaId;
        AreaEntered?.Invoke(args);
    }

    internal void RaiseAreaLeft(string areaId)
    {
        if (string.Equals(CurrentAreaId, areaId, StringComparison.Ordinal))
        {
            CurrentAreaId = null;
        }

        AreaLeft?.Invoke(areaId);
    }

    internal void RaiseConnected()
    {
        IsConnected = true;
        Connected?.Invoke();
    }

    internal void RaiseDisconnected(string? reason)
    {
        IsConnected = false;
        CurrentAreaId = null;
        Disconnected?.Invoke(reason);
    }
}

/// <summary>Managed <see cref="IRulesetRegistry"/> implementation for 3.29 creator table slots (<c>0x00..0xAE</c>).</summary>
public sealed class RulesetRegistry : IRulesetRegistry
{
    private readonly INativeBridge _bridge;
    private readonly Dictionary<byte, (Func<IClientRuleset> Factory, IClientRuleset Instance, IDisposable NativeSub)> _rulesets = [];
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
    public IDisposable Register(byte rulesetId, Func<IClientRuleset> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (rulesetId > IRulesetRegistry.MaxRulesetId)
        {
            throw new ArgumentOutOfRangeException(nameof(rulesetId), $"Ruleset id 0x{rulesetId:X2} exceeds 3.29 creator table max 0x{IRulesetRegistry.MaxRulesetId:X2}.");
        }

        IClientRuleset instance = factory();
        lock (_gate)
        {
            if (_rulesets.ContainsKey(rulesetId))
            {
                throw new InvalidOperationException($"Ruleset slot 0x{rulesetId:X2} is already registered.");
            }

            IDisposable sub = _bridge.RegisterRulesetReceiver(rulesetId, payload => instance.OnRulesetUpdate(payload.Span));
            _rulesets[rulesetId] = (factory, instance, sub);
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

        instance.OnRulesetUpdate(payload);
        return true;
    }

    internal void NotifyAreaEntered(AreaEventArgs args)
    {
        IClientRuleset[] instances;
        lock (_gate)
        {
            instances = [.. _rulesets.Values.Select(v => v.Instance)];
        }

        foreach (IClientRuleset rs in instances)
        {
            rs.OnAreaEntered(args);
        }
    }

    internal void NotifyFrame(float dt)
    {
        IClientRuleset[] instances;
        lock (_gate)
        {
            instances = [.. _rulesets.Values.Select(v => v.Instance)];
        }

        foreach (IClientRuleset rs in instances)
        {
            rs.OnFrame(dt);
        }
    }

    internal void NotifyAreaLeft()
    {
        IClientRuleset[] instances;
        lock (_gate)
        {
            instances = [.. _rulesets.Values.Select(v => v.Instance)];
        }

        foreach (IClientRuleset rs in instances)
        {
            rs.OnAreaLeft();
        }
    }
}

/// <summary>Managed <see cref="IOpcodeRegistry"/> implementation for handler groups and reserved opcodes (<c>0x2fc+</c>).</summary>
public sealed class OpcodeRegistry(INativeBridge bridge) : IOpcodeRegistry
{
    private readonly Dictionary<ushort, List<(Action<ReadOnlyMemory<byte>> Handler, IDisposable NativeSub)>> _handlers = [];
    private readonly Lock _gate = new();

    /// <inheritdoc/>
    public IReadOnlyCollection<ushort> RegisteredOpcodes
    {
        get { lock (_gate) { return [.. _handlers.Keys]; } }
    }

    /// <inheritdoc/>
    public IDisposable RegisterHandler(ushort opcode, Action<ReadOnlyMemory<byte>> handler, string? groupName = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
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
    public bool IsRegistered(ushort opcode)
    {
        lock (_gate)
        {
            return _handlers.TryGetValue(opcode, out var list) && list.Count > 0;
        }
    }

    /// <inheritdoc/>
    public bool TryDispatch(ushort opcode, ReadOnlyMemory<byte> payload)
    {
        Action<ReadOnlyMemory<byte>>[] snapshot;
        lock (_gate)
        {
            if (!_handlers.TryGetValue(opcode, out var list) || list.Count == 0)
            {
                return false;
            }

            snapshot = [.. list.Select(e => e.Handler)];
        }

        foreach (Action<ReadOnlyMemory<byte>> handler in snapshot)
        {
            handler(payload);
        }

        return true;
    }
}

/// <summary>Managed <see cref="IContentRegistry"/> implementation tracking append-only dat rows, item bases, world objects, and overlays.</summary>
public sealed class ContentRegistry(FileOverlayService files) : IContentRegistry
{
    private readonly List<DatRowDeclaration> _datRows = [];
    private readonly List<ItemBaseDeclaration> _itemBases = [];
    private readonly List<WorldObjectDeclaration> _worldObjects = [];
    private readonly List<ObjectOverlayDeclaration> _overlays = [];
    private readonly Lock _gate = new();

    /// <inheritdoc/>
    public IReadOnlyList<DatRowDeclaration> DatRows { get { lock (_gate) { return [.. _datRows]; } } }

    /// <inheritdoc/>
    public IReadOnlyList<ItemBaseDeclaration> ItemBases { get { lock (_gate) { return [.. _itemBases]; } } }

    /// <inheritdoc/>
    public IReadOnlyList<WorldObjectDeclaration> WorldObjects { get { lock (_gate) { return [.. _worldObjects]; } } }

    /// <inheritdoc/>
    public IReadOnlyList<ObjectOverlayDeclaration> Overlays { get { lock (_gate) { return [.. _overlays]; } } }

    /// <inheritdoc/>
    public IDisposable DeclareDatRow(DatRowDeclaration row)
    {
        ArgumentNullException.ThrowIfNull(row);
        lock (_gate) { _datRows.Add(row); }
        return new ActionDisposable(() => { lock (_gate) { _datRows.Remove(row); } });
    }

    /// <inheritdoc/>
    public IDisposable DeclareItemBase(ItemBaseDeclaration itemBase)
    {
        ArgumentNullException.ThrowIfNull(itemBase);
        lock (_gate) { _itemBases.Add(itemBase); }
        return new ActionDisposable(() => { lock (_gate) { _itemBases.Remove(itemBase); } });
    }

    /// <inheritdoc/>
    public IDisposable DeclareWorldObject(WorldObjectDeclaration worldObject)
    {
        ArgumentNullException.ThrowIfNull(worldObject);
        IDisposable? overlaySub = null;
        if (!string.IsNullOrWhiteSpace(worldObject.OtOverlayContent))
        {
            string otPath = worldObject.MetadataPath.EndsWith(".ot", StringComparison.OrdinalIgnoreCase)
                ? worldObject.MetadataPath
                : worldObject.MetadataPath + ".ot";
            overlaySub = files.RegisterVirtualTextFile(otPath, worldObject.OtOverlayContent);
        }

        lock (_gate) { _worldObjects.Add(worldObject); }
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
    public IDisposable InstallHook(string symbolName, nint detourTarget, out nint originalTrampoline, HookType hookType = HookType.InlineDetour)
    {
        ulong handle = hookType switch
        {
            HookType.InlineDetour => bridge.InstallInlineHook(symbolName, detourTarget, out originalTrampoline),
            HookType.VtableSlot => bridge.InstallVtableHook(addresses.ResolveAddress(symbolName), 0, detourTarget, out originalTrampoline),
            _ => throw new ArgumentOutOfRangeException(nameof(hookType)),
        };

        return new ActionDisposable(() => bridge.RemoveHook(handle));
    }

    /// <inheritdoc/>
    public nint Call(string symbolName, params nint[] args) => bridge.InvokeNative(symbolName, args);
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

        bridge.EnqueueGameThread(action);
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
        bridge.EnqueueGameThread(() =>
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
        bridge.EnqueueGameThread(() =>
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
