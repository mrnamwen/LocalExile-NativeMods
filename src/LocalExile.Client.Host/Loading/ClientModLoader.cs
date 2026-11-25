using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using LocalExile.Client.Host.Native;
using LocalExile.Client.Host.Services;
using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Addresses;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.Sdk.Protocol;
using LocalExile.Client.Sdk.Raw;

namespace LocalExile.Client.Host.Loading;

/// <summary>
/// Collectible <see cref="AssemblyLoadContext"/> loading a client mod from a private shadow copy on disk
/// so original build outputs are never locked and old mod versions can be fully collected on hot reload.
/// Shares <c>LocalExile.Client.Sdk</c> from the host's default context so SDK interfaces unify.
/// </summary>
public sealed class ClientModLoadContext : AssemblyLoadContext
{
    private readonly string _modDirectory;
    private readonly string _shadowDirectory;

    /// <summary>Gets the shadow-copy directory created for this load generation.</summary>
    public string ShadowDirectory => _shadowDirectory;

    /// <summary>Creates a new collectible load context for a client mod.</summary>
    public ClientModLoadContext(string modId, string modDirectory, string shadowRoot)
        : base($"ClientMod:{modId}:{Guid.NewGuid():N}", isCollectible: true)
    {
        _modDirectory = Path.GetFullPath(modDirectory);
        _shadowDirectory = Path.Combine(shadowRoot, $"{modId}-{Guid.NewGuid():N}");
    }

    /// <summary>Copies the assembly (and optional <c>.pdb</c>) to the shadow directory and loads it without locking the source file.</summary>
    public Assembly LoadShadowCopy(string assemblyPath)
    {
        Directory.CreateDirectory(_shadowDirectory);
        string fileName = Path.GetFileName(assemblyPath);
        string shadowPath = Path.Combine(_shadowDirectory, fileName);
        File.Copy(assemblyPath, shadowPath, overwrite: true);

        string pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
        if (File.Exists(pdbPath))
        {
            File.Copy(pdbPath, Path.ChangeExtension(shadowPath, ".pdb"), overwrite: true);
        }

        // Load from stream so neither Windows nor Linux keeps a file handle open on the shadow copy either,
        // enabling immediate shadow cleanup after GC unload!
        byte[] dllBytes = File.ReadAllBytes(shadowPath);
        string shadowPdb = Path.ChangeExtension(shadowPath, ".pdb");
        using var dllStream = new MemoryStream(dllBytes);
        if (File.Exists(shadowPdb))
        {
            byte[] pdbBytes = File.ReadAllBytes(shadowPdb);
            using var pdbStream = new MemoryStream(pdbBytes);
            return LoadFromStream(dllStream, pdbStream);
        }

        return LoadFromStream(dllStream);
    }

    /// <inheritdoc/>
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Always unify SDK and framework assemblies with the host's default context.
        if (string.Equals(assemblyName.Name, "LocalExile.Client.Sdk", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string candidate = Path.Combine(_modDirectory, assemblyName.Name + ".dll");
        if (File.Exists(candidate))
        {
            return LoadShadowCopy(candidate);
        }

        return null;
    }

    internal void TryCleanupShadowDirectory()
    {
        try
        {
            if (Directory.Exists(_shadowDirectory))
            {
                Directory.Delete(_shadowDirectory, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Result of unloading a client mod, including collectible <see cref="AssemblyLoadContext"/> GC verification.</summary>
/// <param name="ModId">Identifier of the unloaded mod.</param>
/// <param name="WasLoaded">Whether the mod existed in the loader.</param>
/// <param name="UnloadedCleanly"><c>true</c> if the <see cref="WeakReference"/> to the load context died after GC passes.</param>
/// <param name="GcPasses">Number of GC collection cycles performed to verify unload.</param>
public sealed record ModUnloadResult(string ModId, bool WasLoaded, bool UnloadedCleanly, int GcPasses);

/// <summary>Runtime state for a loaded client mod.</summary>
public sealed class LoadedClientMod
{
    /// <summary>Parsed manifest of the loaded mod.</summary>
    public ClientModManifest Manifest { get; }

    /// <summary>Source directory on disk.</summary>
    public string ModDirectory { get; }

    /// <summary>Resolved source assembly path (null for data-only payload mods).</summary>
    public string? AssemblyPath { get; }

    /// <summary>Current load generation counter (increments on every hot reload).</summary>
    public int Generation { get; }

    /// <summary>Whether the mod has been disabled due to repeated unhandled exceptions.</summary>
    public bool IsDisabled { get; internal set; }

    /// <summary>Reason the mod was disabled, if <see cref="IsDisabled"/> is <c>true</c>.</summary>
    public string? DisabledReason { get; internal set; }

    /// <summary>Total number of caught faults from this mod's callbacks or frame ticks.</summary>
    public int FaultCount { get; internal set; }

    /// <summary>Most recent exception caught inside the fault wall, if any.</summary>
    public Exception? LastFault { get; internal set; }

    internal ClientModLoadContext? LoadContext { get; set; }
    internal IClientMod? Instance { get; set; }
    internal List<IDisposable> TrackedDisposables { get; } = [];
    internal List<Action<float>> FrameCallbacks { get; } = [];

    internal LoadedClientMod(ClientModManifest manifest, string modDirectory, string? assemblyPath, int generation)
    {
        Manifest = manifest;
        ModDirectory = modDirectory;
        AssemblyPath = assemblyPath;
        Generation = generation;
    }
}

/// <summary>
/// Loads, reloads, unloads, and fault-isolates managed client mods using collectible shadow-copied
/// <see cref="ClientModLoadContext"/> instances, enforcing <c>SdkVersionRange</c> and <c>RawAccess</c> manifest checks.
/// </summary>
public sealed class ClientModLoader : IDisposable
{
    private readonly ClientHostRuntime _runtime;
    private readonly string _shadowRoot;
    private readonly int _maxFaultsBeforeDisable;
    private readonly Dictionary<string, LoadedClientMod> _mods = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _generations = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Dictionary<string, CancellationTokenSource> _reloadDebounces = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>Raised when a mod throws an unhandled exception inside the host fault wall.</summary>
    public event Action<string, Exception, bool>? ModFaulted;

    /// <summary>Raised when a mod finishes loading or hot-reloading.</summary>
    public event Action<LoadedClientMod>? ModLoaded;

    /// <summary>Raised when a mod is unloaded.</summary>
    public event Action<ModUnloadResult>? ModUnloaded;

    /// <summary>Creates a new <see cref="ClientModLoader"/> bound to <paramref name="runtime"/>.</summary>
    public ClientModLoader(ClientHostRuntime runtime, string? shadowRoot = null, int maxFaultsBeforeDisable = 3)
    {
        _runtime = runtime;
        _shadowRoot = shadowRoot ?? Path.Combine(Path.GetTempPath(), "localexile-client-mods-shadow", Guid.NewGuid().ToString("N"));
        _maxFaultsBeforeDisable = Math.Max(1, maxFaultsBeforeDisable);
    }

    /// <summary>Snapshot of all currently loaded client mods.</summary>
    public IReadOnlyCollection<LoadedClientMod> LoadedMods
    {
        get { lock (_gate) { return [.. _mods.Values]; } }
    }

    /// <summary>Gets a loaded mod by id, or <c>null</c> if not loaded.</summary>
    public LoadedClientMod? GetMod(string modId)
    {
        lock (_gate)
        {
            return _mods.TryGetValue(modId, out LoadedClientMod? mod) ? mod : null;
        }
    }

    /// <summary>Returns handshake metadata for all currently loaded, non-disabled mods.</summary>
    public IReadOnlyList<InstalledClientModInfo> GetInstalledModSummaries()
    {
        lock (_gate)
        {
            return [.. _mods.Values
                .Where(m => !m.IsDisabled)
                .Select(m => new InstalledClientModInfo(m.Manifest.Id, m.Manifest.Version, m.Manifest.PayloadHash))];
        }
    }

    /// <summary>
    /// Loads a client mod from a directory containing <c>mod.json</c> (and optionally a client DLL and/or <c>files/</c> payload).
    /// </summary>
    public LoadedClientMod LoadModFromDirectory(string modDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modDirectory);
        string fullDir = Path.GetFullPath(modDirectory);
        string manifestPath = Path.Combine(fullDir, "mod.json");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"Client mod manifest not found at '{manifestPath}'.", manifestPath);
        }

        ClientModManifest manifest = ClientModManifest.LoadFromFile(manifestPath);
        string? assemblyPath = null;
        if (!string.IsNullOrWhiteSpace(manifest.Assembly))
        {
            assemblyPath = Path.Combine(fullDir, manifest.Assembly);
        }
        else
        {
            string convention = Path.Combine(fullDir, $"{manifest.Id}.Client.dll");
            if (File.Exists(convention))
            {
                assemblyPath = convention;
            }
        }

        return LoadMod(manifest, fullDir, assemblyPath);
    }

    /// <summary>
    /// Loads a client mod from an explicit <see cref="ClientModManifest"/> and optional compiled DLL path.
    /// </summary>
    public LoadedClientMod LoadMod(ClientModManifest manifest, string modDirectory, string? assemblyPath = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(modDirectory);

        if (!SdkVersion.IsCompatible(manifest.SdkVersionRange))
        {
            throw new InvalidOperationException(
                $"Client mod '{manifest.Id}' requires SDK version '{manifest.SdkVersionRange}', but active SDK is '{SdkVersion.Current}'.");
        }

        string fullDir = Path.GetFullPath(modDirectory);
        int generation;
        lock (_gate)
        {
            if (_mods.ContainsKey(manifest.Id))
            {
                throw new InvalidOperationException($"Client mod '{manifest.Id}' is already loaded. Use ReloadModAsync to reload it.");
            }

            generation = _generations.TryGetValue(manifest.Id, out int prev) ? prev + 1 : 1;
            _generations[manifest.Id] = generation;
        }

        var loaded = new LoadedClientMod(manifest, fullDir, assemblyPath, generation);

        // Register loose payload directory if present.
        // WaypointModInstaller copies mod.json (with payloadDir: "client") into Mods/Client/<id>/mod.json
        // while deploying UTF-16LE loose overlays into Mods/Client/<id>/files/, so fall back to "files"
        // whenever manifest.PayloadDir does not exist in the deployed mod folder.
        string? payloadDir = null;
        if (!string.IsNullOrWhiteSpace(manifest.PayloadDir))
        {
            string declared = Path.Combine(fullDir, manifest.PayloadDir);
            if (Directory.Exists(declared))
            {
                payloadDir = declared;
            }
        }

        if (payloadDir is null)
        {
            string defaultFilesDir = Path.Combine(fullDir, "files");
            if (Directory.Exists(defaultFilesDir))
            {
                payloadDir = defaultFilesDir;
            }
        }

        if (payloadDir is not null)
        {
            loaded.TrackedDisposables.Add(_runtime.FileOverlayService.RegisterDirectoryForMod(manifest.Id, payloadDir, priority: 0));
        }

        if (!string.IsNullOrWhiteSpace(assemblyPath))
        {
            if (!File.Exists(assemblyPath))
            {
                DisposeTracked(loaded);
                throw new FileNotFoundException($"Client mod assembly '{assemblyPath}' was not found.", assemblyPath);
            }

            ClientModLoadContext alc = new(manifest.Id, fullDir, _shadowRoot);
            try
            {
                Assembly asm = alc.LoadShadowCopy(assemblyPath);
                Type? modType = asm.GetExportedTypes()
                    .FirstOrDefault(t => !t.IsAbstract && !t.IsInterface && typeof(IClientMod).IsAssignableFrom(t));

                if (modType is null)
                {
                    throw new InvalidOperationException(
                        $"Assembly '{Path.GetFileName(assemblyPath)}' for mod '{manifest.Id}' contains no public non-abstract IClientMod implementation.");
                }

                var instance = (IClientMod)(Activator.CreateInstance(modType)
                    ?? throw new InvalidOperationException($"Failed to instantiate '{modType.FullName}'."));

                loaded.LoadContext = alc;
                loaded.Instance = instance;

                var context = new ScopedClientContext(_runtime, this, loaded);
                instance.Load(context);
            }
            catch
            {
                DisposeTracked(loaded);
                loaded.Instance = null;
                loaded.LoadContext = null;
                alc.Unload();
                alc.TryCleanupShadowDirectory();
                throw;
            }
        }

        lock (_gate)
        {
            _mods[manifest.Id] = loaded;
        }

        ModLoaded?.Invoke(loaded);
        return loaded;
    }

    /// <summary>
    /// Loads an already-instantiated in-process <see cref="IClientMod"/> (useful for unit tests and built-in mods)
    /// with full manifest permission checks, tracked disposables, and fault isolation.
    /// </summary>
    public LoadedClientMod LoadInProcessMod(ClientModManifest manifest, IClientMod instance, string? modDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(instance);

        if (!SdkVersion.IsCompatible(manifest.SdkVersionRange))
        {
            throw new InvalidOperationException(
                $"Client mod '{manifest.Id}' requires SDK version '{manifest.SdkVersionRange}', but active SDK is '{SdkVersion.Current}'.");
        }

        int generation;
        lock (_gate)
        {
            if (_mods.ContainsKey(manifest.Id))
            {
                throw new InvalidOperationException($"Client mod '{manifest.Id}' is already loaded.");
            }

            generation = _generations.TryGetValue(manifest.Id, out int prev) ? prev + 1 : 1;
            _generations[manifest.Id] = generation;
        }

        var loaded = new LoadedClientMod(manifest, modDirectory ?? Environment.CurrentDirectory, null, generation)
        {
            Instance = instance,
        };

        try
        {
            var context = new ScopedClientContext(_runtime, this, loaded);
            instance.Load(context);
        }
        catch
        {
            DisposeTracked(loaded);
            loaded.Instance = null;
            throw;
        }

        lock (_gate)
        {
            _mods[manifest.Id] = loaded;
        }

        ModLoaded?.Invoke(loaded);
        return loaded;
    }

    /// <summary>
    /// Unloads a loaded client mod, disposes all tracked resources, unloads its collectible
    /// <see cref="AssemblyLoadContext"/>, and verifies collection using a <see cref="WeakReference"/>.
    /// </summary>
    public Task<ModUnloadResult> UnloadModAsync(string modId, int maxGcPasses = 12)
    {
        (bool found, WeakReference? alcRef, string? shadowDir) = DetachAndInitiateUnload(modId);
        if (!found)
        {
            return Task.FromResult(new ModUnloadResult(modId, WasLoaded: false, UnloadedCleanly: true, GcPasses: 0));
        }

        int passes = 0;
        bool clean = alcRef is null;
        while (alcRef is not null && alcRef.IsAlive && passes < maxGcPasses)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            passes++;
        }

        if (alcRef is not null)
        {
            clean = !alcRef.IsAlive;
        }

        if (shadowDir is not null)
        {
            try
            {
                if (Directory.Exists(shadowDir))
                {
                    Directory.Delete(shadowDir, recursive: true);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var result = new ModUnloadResult(modId, WasLoaded: true, UnloadedCleanly: clean, GcPasses: passes);
        ModUnloaded?.Invoke(result);
        return Task.FromResult(result);
    }

    // NoInlining guarantees no local strong references to ALC or mod instance remain on the caller's stack frame!
    [MethodImpl(MethodImplOptions.NoInlining)]
    private (bool Found, WeakReference? AlcRef, string? ShadowDir) DetachAndInitiateUnload(string modId)
    {
        LoadedClientMod? loaded;
        lock (_gate)
        {
            if (!_mods.Remove(modId, out loaded))
            {
                return (false, null, null);
            }
        }

        try
        {
            loaded.Instance?.Unload();
        }
        catch (Exception ex)
        {
            _runtime.Log("WARN", modId, $"Unload() threw an exception: {ex.Message}");
        }

        if (loaded.Instance is IDisposable disposableMod)
        {
            try { disposableMod.Dispose(); }
            catch (Exception ex) { _runtime.Log("WARN", modId, $"Dispose() threw an exception: {ex.Message}"); }
        }

        DisposeTracked(loaded);
        loaded.FrameCallbacks.Clear();
        loaded.Instance = null;

        ClientModLoadContext? alc = loaded.LoadContext;
        loaded.LoadContext = null;
        if (alc is null)
        {
            return (true, null, null);
        }

        string shadowDir = alc.ShadowDirectory;
        var weakRef = new WeakReference(alc, trackResurrection: true);
        alc.Unload();
        TryClearSystemTextJsonMetadataCache();
        return (true, weakRef, shadowDir);
    }

    private static void TryClearSystemTextJsonMetadataCache()
    {
        // Defense-in-depth: if a client mod invoked System.Text.Json.JsonSerializer directly on its own types,
        // flush STJ's hot-reload metadata cache so the collectible AssemblyLoadContext is not pinned.
        try
        {
            Type? handler = typeof(System.Text.Json.JsonSerializerOptions).Assembly
                .GetType("System.Text.Json.JsonSerializerOptionsUpdateHandler");
            handler?.GetMethod("ClearCache", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?.Invoke(null, [null]);
        }
        catch
        {
            // Best-effort only; MessageCodec itself uses CollectibleJsonCodec and never populates STJ caches for collectible types.
        }
    }

    /// <summary>
    /// Unloads an existing client mod cleanly and reloads it from its original directory/assembly path.
    /// </summary>
    public async Task<(ModUnloadResult Unload, LoadedClientMod Reloaded)> ReloadModAsync(string modId)
    {
        LoadedClientMod? existing = GetMod(modId)
            ?? throw new KeyNotFoundException($"Client mod '{modId}' is not currently loaded.");

        string dir = existing.ModDirectory;
        string? asmPath = existing.AssemblyPath;
        ClientModManifest manifest = File.Exists(Path.Combine(dir, "mod.json"))
            ? ClientModManifest.LoadFromFile(Path.Combine(dir, "mod.json"))
            : existing.Manifest;

        ModUnloadResult unload = await UnloadModAsync(modId).ConfigureAwait(false);
        LoadedClientMod reloaded = LoadMod(manifest, dir, asmPath);
        return (unload, reloaded);
    }

    /// <summary>
    /// Starts watching <paramref name="modsRootDirectory"/> for DLL or <c>mod.json</c> changes and automatically
    /// triggers debounced hot reloads on affected mods.
    /// </summary>
    public IDisposable WatchDirectoryForHotReload(string modsRootDirectory, TimeSpan? debounce = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modsRootDirectory);
        TimeSpan delay = debounce ?? TimeSpan.FromMilliseconds(250);
        var watcher = new FileSystemWatcher(Path.GetFullPath(modsRootDirectory))
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };

        void OnChange(object sender, FileSystemEventArgs e)
        {
            string ext = Path.GetExtension(e.FullPath);
            if (!string.Equals(ext, ".dll", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Path.GetFileName(e.FullPath), "mod.json", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string? modId = FindOwningModId(e.FullPath);
            if (modId is null)
            {
                return;
            }

            ScheduleDebouncedReload(modId, delay);
        }

        watcher.Changed += OnChange;
        watcher.Created += OnChange;
        watcher.Renamed += (s, e) => OnChange(s, e);

        lock (_gate)
        {
            _watchers.Add(watcher);
        }

        return new ActionDisposable(() =>
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            lock (_gate)
            {
                _watchers.Remove(watcher);
            }
        });
    }

    internal void TickAllMods(float deltaSeconds)
    {
        LoadedClientMod[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _mods.Values];
        }

        foreach (LoadedClientMod mod in snapshot)
        {
            if (mod.IsDisabled)
            {
                continue;
            }

            Action<float>[] callbacks;
            lock (_gate)
            {
                callbacks = [.. mod.FrameCallbacks];
            }

            foreach (Action<float> cb in callbacks)
            {
                if (mod.IsDisabled)
                {
                    break;
                }

                ExecuteFaultIsolated(mod.Manifest.Id, () => cb(deltaSeconds));
            }
        }
    }

    /// <summary>
    /// Runs a mod callback inside the host's exception wall. Repeated failures automatically disable the faulty mod
    /// without crashing the host or interrupting healthy mods.
    /// </summary>
    public bool ExecuteFaultIsolated(string modId, Action callback)
    {
        LoadedClientMod? mod = GetMod(modId);
        if (mod is not null && mod.IsDisabled)
        {
            return false;
        }

        try
        {
            callback();
            return true;
        }
        catch (Exception ex)
        {
            bool disabledNow = false;
            if (mod is not null)
            {
                lock (_gate)
                {
                    mod.FaultCount++;
                    mod.LastFault = ex;
                    if (mod.FaultCount >= _maxFaultsBeforeDisable && !mod.IsDisabled)
                    {
                        mod.IsDisabled = true;
                        mod.DisabledReason = $"Disabled after {mod.FaultCount} unhandled exception(s): {ex.GetType().Name}: {ex.Message}";
                        disabledNow = true;
                    }
                }

                if (disabledNow)
                {
                    DisposeTracked(mod);
                    mod.FrameCallbacks.Clear();
                }
            }

            _runtime.Log("ERROR", modId, $"Fault wall caught {ex.GetType().Name}: {ex.Message}{(disabledNow ? " [MOD DISABLED]" : string.Empty)}");
            ModFaulted?.Invoke(modId, ex, disabledNow);
            return false;
        }
    }

    internal void RegisterFrameCallback(LoadedClientMod mod, Action<float> callback)
    {
        lock (_gate)
        {
            mod.FrameCallbacks.Add(callback);
        }
    }

    internal void UnregisterFrameCallback(LoadedClientMod mod, Action<float> callback)
    {
        lock (_gate)
        {
            mod.FrameCallbacks.Remove(callback);
        }
    }

    private string? FindOwningModId(string filePath)
    {
        string full = Path.GetFullPath(filePath);
        lock (_gate)
        {
            foreach (LoadedClientMod mod in _mods.Values)
            {
                if (full.StartsWith(mod.ModDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    return mod.Manifest.Id;
                }
            }
        }

        return null;
    }

    private void ScheduleDebouncedReload(string modId, TimeSpan delay)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_reloadDebounces.TryGetValue(modId, out CancellationTokenSource? prev))
            {
                prev.Cancel();
                prev.Dispose();
            }

            cts = new CancellationTokenSource();
            _reloadDebounces[modId] = cts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                await _runtime.Scheduler.RunOnGameThreadAsync(() =>
                {
                    ReloadModAsync(modId).GetAwaiter().GetResult();
                }, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _runtime.Log("ERROR", modId, $"Hot reload failed: {ex.Message}");
            }
        });
    }

    private static void DisposeTracked(LoadedClientMod mod)
    {
        for (int i = mod.TrackedDisposables.Count - 1; i >= 0; i--)
        {
            try { mod.TrackedDisposables[i].Dispose(); }
            catch { }
        }

        mod.TrackedDisposables.Clear();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (FileSystemWatcher w in _watchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }

        _watchers.Clear();

        foreach (CancellationTokenSource cts in _reloadDebounces.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }

        _reloadDebounces.Clear();

        string[] ids;
        lock (_gate)
        {
            ids = [.. _mods.Keys];
        }

        foreach (string id in ids)
        {
            UnloadModAsync(id, maxGcPasses: 2).GetAwaiter().GetResult();
        }

        try
        {
            if (Directory.Exists(_shadowRoot))
            {
                Directory.Delete(_shadowRoot, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed class ScopedClientContext : IClientContext
{
    private readonly ClientHostRuntime _runtime;
    private readonly LoadedClientMod _mod;
    private readonly ScopedRawMemory _scopedRaw;

    public ScopedClientContext(ClientHostRuntime runtime, ClientModLoader loader, LoadedClientMod mod)
    {
        _runtime = runtime;
        _mod = mod;
        Logger = new ModLogger(mod.Manifest.Id, runtime.Log);
        INetChannel innerNet = runtime.Router.CreateChannel(mod.Manifest.Id, (id, action) => loader.ExecuteFaultIsolated(id, action));
        Net = new TrackedNetChannel(innerNet, TrackDisposable);
        Game = new ScopedGameServices(runtime, loader, mod, TrackDisposable);
        _scopedRaw = new ScopedRawMemory(runtime.RawMemory, TrackDisposable);
    }

    public ClientModManifest Manifest => _mod.Manifest;
    public string ModDirectory => _mod.ModDirectory;
    public string SdkVersion => Sdk.SdkVersion.Current;
    public IModLogger Logger { get; }
    public IModScheduler Scheduler => _runtime.Scheduler;
    public IAddressLibrary Addresses => _runtime.Addresses;
    public IGameServices Game { get; }
    public INetChannel Net { get; }

    public IRawMemory Raw
    {
        get
        {
            if (!_mod.Manifest.RawAccess)
            {
                throw new RawAccessDeniedException(_mod.Manifest.Id);
            }

            return _scopedRaw;
        }
    }

    public void TrackDisposable(IDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        _mod.TrackedDisposables.Add(resource);
    }
}

internal sealed class TrackedNetChannel(INetChannel inner, Action<IDisposable> track) : INetChannel
{
    public string ChannelId => inner.ChannelId;
    public NetTransportInfo Transport => inner.Transport;
    public void Send<T>(T message, string? messageType = null) => inner.Send(message, messageType);
    public void SendRaw(ushort opcode, ReadOnlySpan<byte> payload) => inner.SendRaw(opcode, payload);

    public IDisposable On<T>(Action<T> handler, string? messageType = null)
    {
        IDisposable sub = inner.On(handler, messageType);
        track(sub);
        return sub;
    }

    public IDisposable OnRaw(ushort opcode, Action<ReadOnlyMemory<byte>> handler)
    {
        IDisposable sub = inner.OnRaw(opcode, handler);
        track(sub);
        return sub;
    }
}

internal sealed class ScopedRawMemory(IRawMemory inner, Action<IDisposable> track) : IRawMemory
{
    public nint GetAddress(string symbolName) => inner.GetAddress(symbolName);
    public T Read<T>(nint address) where T : unmanaged => inner.Read<T>(address);
    public void Write<T>(nint address, T value) where T : unmanaged => inner.Write(address, value);
    public byte[] ReadBytes(nint address, int count) => inner.ReadBytes(address, count);
    public void WriteBytes(nint address, ReadOnlySpan<byte> data) => inner.WriteBytes(address, data);
    public nint Call(string symbolName, params nint[] args) => inner.Call(symbolName, args);

    public IDisposable InstallHook(string symbolName, nint detourTarget, out nint originalTrampoline, HookType hookType = HookType.InlineDetour)
    {
        IDisposable sub = inner.InstallHook(symbolName, detourTarget, out originalTrampoline, hookType);
        track(sub);
        return sub;
    }
}

internal sealed class ScopedGameServices : IGameServices
{
    private readonly ClientHostRuntime _runtime;
    private readonly ClientModLoader _loader;
    private readonly LoadedClientMod _mod;

    public ScopedGameServices(ClientHostRuntime runtime, ClientModLoader loader, LoadedClientMod mod, Action<IDisposable> track)
    {
        _runtime = runtime;
        _loader = loader;
        _mod = mod;
        Ui = new TrackedUiService(runtime.Ui, track);
        Files = new TrackedFileOverlayService(runtime.FileOverlayService, mod.Manifest.Id, track);
        Scripts = new TrackedScriptService(runtime.Scripts, track);
        Stats = new TrackedStatsService(runtime.Stats, track);
        GealBuiltins = new TrackedGealBuiltinRegistry(runtime.GealBuiltins, track);
        UiControls = new TrackedUiControlRegistry(runtime.UiControls, track);
        Rulesets = new TrackedRulesetRegistry(runtime.Rulesets, track);
        Opcodes = new TrackedOpcodeRegistry(runtime.Opcodes, track);
        Content = new TrackedContentRegistry(runtime.Content, track);
    }

    public IUiService Ui { get; }
    public IFileOverlayService Files { get; }
    public IScriptService Scripts { get; }
    public IAudioService Audio => _runtime.Audio;
    public IStatsService Stats { get; }
    public IWorldService World => _runtime.World;
    public IGameLifecycle Lifecycle => _runtime.Lifecycle;
    public IGealBuiltinRegistry GealBuiltins { get; }
    public IUiControlRegistry UiControls { get; }
    public IRulesetRegistry Rulesets { get; }
    public IOpcodeRegistry Opcodes { get; }
    public IContentRegistry Content { get; }

    public event Action<float>? Frame
    {
        add
        {
            if (value is not null)
            {
                _loader.RegisterFrameCallback(_mod, value);
            }
        }
        remove
        {
            if (value is not null)
            {
                _loader.UnregisterFrameCallback(_mod, value);
            }
        }
    }

    private sealed class TrackedUiService(IUiService inner, Action<IDisposable> track) : IUiService
    {
        public IReadOnlyCollection<IUiWindow> OpenWindows => inner.OpenWindows;
        public IUiWindow Load(string virtualPath, string? windowId = null) { IUiWindow w = inner.Load(virtualPath, windowId); track(w); return w; }
        public IUiWindow? GetWindow(string windowId) => inner.GetWindow(windowId);
        public bool CloseWindow(string windowId) => inner.CloseWindow(windowId);
        public void ReloadWindow(string windowId) => inner.ReloadWindow(windowId);
        public IDisposable RegisterControl<TControl>(string controlTypeName) where TControl : class, ICustomUiControl, new() { IDisposable d = inner.RegisterControl<TControl>(controlTypeName); track(d); return d; }
        public IDisposable RegisterControl(string controlTypeName, Func<ICustomUiControl> factory) { IDisposable d = inner.RegisterControl(controlTypeName, factory); track(d); return d; }
    }

    private sealed class TrackedFileOverlayService(FileOverlayService inner, string modId, Action<IDisposable> track) : IFileOverlayService
    {
        public IDisposable RegisterDirectory(string rootDirectory, int priority = 0) { IDisposable d = inner.RegisterDirectoryForMod(modId, rootDirectory, priority); track(d); return d; }
        public IDisposable RegisterVirtualFile(string virtualPath, ReadOnlyMemory<byte> rawBytes, int priority = 100) { IDisposable d = inner.RegisterVirtualFileForMod(modId, virtualPath, rawBytes, priority); track(d); return d; }
        public IDisposable RegisterVirtualTextFile(string virtualPath, string text, int priority = 100) { IDisposable d = inner.RegisterVirtualTextFileForMod(modId, virtualPath, text, priority); track(d); return d; }
        public IDisposable RegisterBundle(string bundlePath, string manifestPath, int priority = -10) { IDisposable d = inner.RegisterBundleForMod(modId, bundlePath, manifestPath, priority); track(d); return d; }
        public bool Exists(string virtualPath) => inner.Exists(virtualPath);
        public byte[]? ReadAllBytes(string virtualPath) => inner.ReadAllBytes(virtualPath);
        public string? ReadAllText(string virtualPath) => inner.ReadAllText(virtualPath);
        public void Invalidate(string virtualPath) => inner.Invalidate(virtualPath);
        public IReadOnlyList<FileOverlayEntry> GetRegisteredOverlays() => inner.GetRegisteredOverlays();
    }

    private sealed class TrackedScriptService(IScriptService inner, Action<IDisposable> track) : IScriptService
    {
        public GealInvocationResult InvokeFunction(uint objectId, string functionName, params GealValue[] arguments) => inner.InvokeFunction(objectId, functionName, arguments);
        public GealInvocationResult InvokeEvent(uint objectId, string eventName, params GealValue[] arguments) => inner.InvokeEvent(objectId, eventName, arguments);
        public IReadOnlyList<GealScriptDiagnostic> ValidateScript(string gealSource) => inner.ValidateScript(gealSource);
        public IReadOnlyCollection<string> GetRegisteredBuiltins() => inner.GetRegisteredBuiltins();
        public IDisposable Register(string name, Func<uint, GealArguments, GealValue> callback) { IDisposable d = inner.Register(name, callback); track(d); return d; }
        public IDisposable Register(string name, Action<uint, GealArguments> callback) { IDisposable d = inner.Register(name, callback); track(d); return d; }
    }

    private sealed class TrackedStatsService(IStatsService inner, Action<IDisposable> track) : IStatsService
    {
        public int GetStat(string statKey, int defaultValue = 0) => inner.GetStat(statKey, defaultValue);
        public void SetLocalStat(string statKey, int value) => inner.SetLocalStat(statKey, value);
        public IDisposable OnStatChanged(string statKey, Action<int> listener) { IDisposable d = inner.OnStatChanged(statKey, listener); track(d); return d; }
    }

    private sealed class TrackedGealBuiltinRegistry(IGealBuiltinRegistry inner, Action<IDisposable> track) : IGealBuiltinRegistry
    {
        public IReadOnlyCollection<string> RegisteredNames => inner.RegisteredNames;
        public IDisposable Register(string name, Func<uint, GealArguments, GealValue> handler) { IDisposable d = inner.Register(name, handler); track(d); return d; }
        public IDisposable Register(string name, Action<uint, GealArguments> handler) { IDisposable d = inner.Register(name, handler); track(d); return d; }
        public bool IsRegistered(string name) => inner.IsRegistered(name);
        public GealInvocationResult Invoke(string name, uint objectId, params GealValue[] arguments) => inner.Invoke(name, objectId, arguments);
    }

    private sealed class TrackedUiControlRegistry(IUiControlRegistry inner, Action<IDisposable> track) : IUiControlRegistry
    {
        public IReadOnlyCollection<string> RegisteredControlTypes => inner.RegisteredControlTypes;
        public IDisposable Register<TControl>(string controlTypeName) where TControl : class, ICustomUiControl, new() { IDisposable d = inner.Register<TControl>(controlTypeName); track(d); return d; }
        public IDisposable Register(string controlTypeName, Func<ICustomUiControl> factory) { IDisposable d = inner.Register(controlTypeName, factory); track(d); return d; }
        public bool IsRegistered(string controlTypeName) => inner.IsRegistered(controlTypeName);
        public ICustomUiControl? CreateInstance(string controlTypeName) => inner.CreateInstance(controlTypeName);
    }

    private sealed class TrackedRulesetRegistry(IRulesetRegistry inner, Action<IDisposable> track) : IRulesetRegistry
    {
        public IReadOnlyCollection<byte> RegisteredIds => inner.RegisteredIds;
        public IDisposable Register<TRuleset>() where TRuleset : class, IClientRuleset, new() { IDisposable d = inner.Register<TRuleset>(); track(d); return d; }
        public IDisposable Register(byte rulesetId, Func<IClientRuleset> factory) { IDisposable d = inner.Register(rulesetId, factory); track(d); return d; }
        public bool IsRegistered(byte rulesetId) => inner.IsRegistered(rulesetId);
        public bool TryDispatchUpdate(byte rulesetId, ReadOnlySpan<byte> payload) => inner.TryDispatchUpdate(rulesetId, payload);
    }

    private sealed class TrackedOpcodeRegistry(IOpcodeRegistry inner, Action<IDisposable> track) : IOpcodeRegistry
    {
        public IReadOnlyCollection<ushort> RegisteredOpcodes => inner.RegisteredOpcodes;
        public IDisposable RegisterHandler(ushort opcode, Action<ReadOnlyMemory<byte>> handler, string? groupName = null) { IDisposable d = inner.RegisterHandler(opcode, handler, groupName); track(d); return d; }
        public bool IsRegistered(ushort opcode) => inner.IsRegistered(opcode);
        public bool TryDispatch(ushort opcode, ReadOnlyMemory<byte> payload) => inner.TryDispatch(opcode, payload);
    }

    private sealed class TrackedContentRegistry(IContentRegistry inner, Action<IDisposable> track) : IContentRegistry
    {
        public IReadOnlyList<DatRowDeclaration> DatRows => inner.DatRows;
        public IReadOnlyList<ItemBaseDeclaration> ItemBases => inner.ItemBases;
        public IReadOnlyList<WorldObjectDeclaration> WorldObjects => inner.WorldObjects;
        public IReadOnlyList<ObjectOverlayDeclaration> Overlays => inner.Overlays;
        public IDisposable DeclareDatRow(DatRowDeclaration row) { IDisposable d = inner.DeclareDatRow(row); track(d); return d; }
        public IDisposable DeclareItemBase(ItemBaseDeclaration itemBase) { IDisposable d = inner.DeclareItemBase(itemBase); track(d); return d; }
        public IDisposable DeclareWorldObject(WorldObjectDeclaration worldObject) { IDisposable d = inner.DeclareWorldObject(worldObject); track(d); return d; }
        public IDisposable DeclareOverlay(ObjectOverlayDeclaration overlay) { IDisposable d = inner.DeclareOverlay(overlay); track(d); return d; }
        public uint ComputeDeterministicId(string modId, string entryKey) => inner.ComputeDeterministicId(modId, entryKey);
    }
}
