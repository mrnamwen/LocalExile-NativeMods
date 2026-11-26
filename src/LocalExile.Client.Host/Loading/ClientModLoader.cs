using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
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
        // Always unify LocalExile.Client.Sdk with the host's own loaded copy (even when
        // LocalExile.Client.Host was loaded into hostfxr's IsolatedComponentLoadContext).
        if (string.Equals(assemblyName.Name, "LocalExile.Client.Sdk", StringComparison.OrdinalIgnoreCase))
        {
            return typeof(IClientMod).Assembly;
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

/// <summary>Result status for an entry processed by <see cref="ClientModLoader.LoadModsFromDirectory"/>.</summary>
public enum ModLoadStatus
{
    Loaded = 0,
    Disabled = 1,
    InvalidManifest = 2,
    MissingDependency = 3,
    DependencyDisabled = 4,
    CyclicDependency = 5,
    DependencyFailed = 6,
    LoadFailed = 7,
}

/// <summary>Diagnostic outcome for a single mod discovered during batch directory loading.</summary>
public sealed record ModLoadDiagnostic(
    string ModId,
    string ModDirectory,
    ModLoadStatus Status,
    string Message,
    LoadedClientMod? LoadedMod = null);

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

    internal Lock SyncRoot { get; } = new();
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
                .Select(m => new InstalledClientModInfo(m.Manifest.Id, m.Manifest.Version, m.Manifest.PayloadHash ?? m.Manifest.BundleSha256))];
        }
    }

    /// <summary>
    /// Scans <paramref name="modsDirectory"/> for mod subdirectories containing <c>mod.json</c>,
    /// enforces <c>Enabled</c> and <c>PayloadHash</c>, resolves <c>Depends</c> in deterministic topological order
    /// (detecting missing/disabled/cyclic dependencies), and loads all valid mods.
    /// </summary>
    public IReadOnlyList<ModLoadDiagnostic> LoadModsFromDirectory(string modsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);
        string fullRoot = Path.GetFullPath(modsDirectory);
        if (!Directory.Exists(fullRoot))
        {
            return [];
        }

        string[] subdirs = Directory.GetDirectories(fullRoot);
        Array.Sort(subdirs, StringComparer.OrdinalIgnoreCase);

        var diagnostics = new List<ModLoadDiagnostic>();
        var enabledEntries = new Dictionary<string, (ClientModManifest Manifest, string ModDir, string? AssemblyPath, List<string> Deps)>(StringComparer.OrdinalIgnoreCase);
        var disabledIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string modDir in subdirs)
        {
            string manifestPath = Path.Combine(modDir, "mod.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            ClientModManifest manifest;
            try
            {
                manifest = ClientModManifest.LoadFromFile(manifestPath);
            }
            catch (Exception ex)
            {
                diagnostics.Add(new ModLoadDiagnostic(Path.GetFileName(modDir), modDir, ModLoadStatus.InvalidManifest, $"{ex.GetType().Name}: {ex.Message}"));
                continue;
            }

            if (!manifest.Enabled)
            {
                disabledIds.Add(manifest.Id);
                diagnostics.Add(new ModLoadDiagnostic(manifest.Id, modDir, ModLoadStatus.Disabled, $"Client mod '{manifest.Id}' is disabled (enabled=false)."));
                continue;
            }

            if (enabledEntries.ContainsKey(manifest.Id))
            {
                diagnostics.Add(new ModLoadDiagnostic(manifest.Id, modDir, ModLoadStatus.InvalidManifest, $"Duplicate mod id '{manifest.Id}' in '{modDir}'."));
                continue;
            }

            string? assemblyPath = ResolveAssemblyPath(manifest, modDir);
            var deps = manifest.EffectiveDepends
                .Where(d => !string.IsNullOrWhiteSpace(d) && !string.Equals(d, "Game", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            enabledEntries[manifest.Id] = (manifest, modDir, assemblyPath, deps);
        }

        // Iteratively prune mods with missing or disabled dependencies until fixpoint.
        var prunedReasons = new Dictionary<string, (ModLoadStatus Status, string Message)>(StringComparer.OrdinalIgnoreCase);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (string id in enabledEntries.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                if (prunedReasons.ContainsKey(id))
                {
                    continue;
                }

                var entry = enabledEntries[id];
                foreach (string dep in entry.Deps)
                {
                    if (disabledIds.Contains(dep))
                    {
                        prunedReasons[id] = (ModLoadStatus.DependencyDisabled, $"Dependency '{dep}' of '{id}' is disabled.");
                        changed = true;
                        break;
                    }

                    if (prunedReasons.TryGetValue(dep, out var depReason))
                    {
                        prunedReasons[id] = (depReason.Status, $"Dependency '{dep}' of '{id}' could not be loaded: {depReason.Message}");
                        changed = true;
                        break;
                    }

                    if (!enabledEntries.ContainsKey(dep) && GetMod(dep) is not { IsDisabled: false })
                    {
                        prunedReasons[id] = (ModLoadStatus.MissingDependency, $"Dependency '{dep}' of '{id}' was not found.");
                        changed = true;
                        break;
                    }
                }
            }
        }

        foreach (string id in prunedReasons.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var entry = enabledEntries[id];
            var reason = prunedReasons[id];
            diagnostics.Add(new ModLoadDiagnostic(id, entry.ModDir, reason.Status, reason.Message));
            enabledEntries.Remove(id);
        }

        // Kahn topological sort over remaining enabled mods with deterministic OrdinalIgnoreCase ordering.
        var indegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var dependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in enabledEntries.Keys)
        {
            indegree[id] = 0;
            dependents[id] = [];
        }

        foreach (var kv in enabledEntries)
        {
            string id = kv.Key;
            foreach (string dep in kv.Value.Deps)
            {
                if (enabledEntries.ContainsKey(dep) && !string.Equals(dep, id, StringComparison.OrdinalIgnoreCase))
                {
                    indegree[id]++;
                    dependents[dep].Add(id);
                }
                else if (string.Equals(dep, id, StringComparison.OrdinalIgnoreCase))
                {
                    indegree[id]++;
                    dependents[id].Add(id);
                }
            }
        }

        var ready = indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key).ToList();
        ready.Sort(StringComparer.OrdinalIgnoreCase);
        var loadOrder = new List<string>(enabledEntries.Count);

        while (ready.Count > 0)
        {
            string current = ready[0];
            ready.RemoveAt(0);
            loadOrder.Add(current);

            foreach (string dependent in dependents[current])
            {
                indegree[dependent]--;
                if (indegree[dependent] == 0)
                {
                    ready.Add(dependent);
                    ready.Sort(StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        foreach (string cyclicId in indegree.Where(kv => kv.Value > 0).Select(kv => kv.Key).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var entry = enabledEntries[cyclicId];
            diagnostics.Add(new ModLoadDiagnostic(
                cyclicId,
                entry.ModDir,
                ModLoadStatus.CyclicDependency,
                $"Cyclic dependency detected involving '{cyclicId}' (depends: [{string.Join(", ", entry.Deps)}])."));
        }

        var failedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in loadOrder)
        {
            var entry = enabledEntries[id];
            string? failedDep = entry.Deps.FirstOrDefault(d => failedIds.Contains(d));
            if (failedDep is not null)
            {
                failedIds.Add(id);
                diagnostics.Add(new ModLoadDiagnostic(
                    id,
                    entry.ModDir,
                    ModLoadStatus.DependencyFailed,
                    $"Skipped '{id}' because dependency '{failedDep}' failed to load."));
                continue;
            }

            try
            {
                LoadedClientMod loaded = LoadMod(entry.Manifest, entry.ModDir, entry.AssemblyPath);
                diagnostics.Add(new ModLoadDiagnostic(id, entry.ModDir, ModLoadStatus.Loaded, "Loaded successfully.", loaded));
            }
            catch (Exception ex)
            {
                failedIds.Add(id);
                diagnostics.Add(new ModLoadDiagnostic(id, entry.ModDir, ModLoadStatus.LoadFailed, $"{ex.GetType().Name}: {ex.Message}"));
            }
        }

        return diagnostics;
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
        string? assemblyPath = ResolveAssemblyPath(manifest, fullDir);
        return LoadMod(manifest, fullDir, assemblyPath);
    }

    private static string? ResolveAssemblyPath(ClientModManifest manifest, string fullDir)
    {
        if (!string.IsNullOrWhiteSpace(manifest.Assembly))
        {
            return Path.Combine(fullDir, manifest.Assembly);
        }

        string convention = Path.Combine(fullDir, $"{manifest.Id}.Client.dll");
        return File.Exists(convention) ? convention : null;
    }

    /// <summary>
    /// Loads a client mod from an explicit <see cref="ClientModManifest"/> and optional compiled DLL path.
    /// </summary>
    public LoadedClientMod LoadMod(ClientModManifest manifest, string modDirectory, string? assemblyPath = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(modDirectory);

        if (!manifest.Enabled)
        {
            throw new InvalidOperationException($"Client mod '{manifest.Id}' is disabled in its manifest.");
        }

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
            TrackModDisposable(loaded, _runtime.FileOverlayService.RegisterDirectoryForMod(manifest.Id, payloadDir, priority: 0));
        }

        if (!string.IsNullOrWhiteSpace(assemblyPath))
        {
            if (!File.Exists(assemblyPath))
            {
                DisposeTracked(loaded);
                throw new FileNotFoundException($"Client mod assembly '{assemblyPath}' was not found.", assemblyPath);
            }

            if (!string.IsNullOrWhiteSpace(manifest.PayloadHash))
            {
                try
                {
                    VerifyAssemblyPayloadHash(assemblyPath, manifest.PayloadHash, manifest.Id);
                }
                catch
                {
                    DisposeTracked(loaded);
                    throw;
                }
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

    private static void VerifyAssemblyPayloadHash(string assemblyPath, string payloadHash, string modId)
    {
        string expected = payloadHash.Trim();
        if (expected.Length != 64 || !expected.All(char.IsAsciiHexDigit))
        {
            throw new InvalidDataException(
                $"Client mod '{modId}' has malformed payloadHash '{payloadHash}' (expected 64 hex characters for SHA-256).");
        }

        using FileStream fs = File.OpenRead(assemblyPath);
        byte[] actualHash = SHA256.HashData(fs);
        string actualHex = Convert.ToHexStringLower(actualHash);
        if (!string.Equals(actualHex, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"PayloadHash verification failed for client mod '{modId}' ('{Path.GetFileName(assemblyPath)}'): expected {expected}, actual {actualHex}.");
        }
    }

    /// <summary>
    /// Loads an already-instantiated in-process <see cref="IClientMod"/> (useful for unit tests and built-in mods)
    /// with full manifest permission checks, tracked disposables, and fault isolation.
    /// </summary>
    public LoadedClientMod LoadInProcessMod(ClientModManifest manifest, IClientMod instance, string? modDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(instance);

        if (!manifest.Enabled)
        {
            throw new InvalidOperationException($"Client mod '{manifest.Id}' is disabled in its manifest.");
        }

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
            RecordExternalFault(modId, ex);
            return false;
        }
    }

    internal void RecordExternalFault(string modId, Exception ex)
    {
        LoadedClientMod? mod = GetMod(modId);
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

                if (disabledNow)
                {
                    mod.FrameCallbacks.Clear();
                }
            }

            if (disabledNow)
            {
                DisposeTracked(mod);
            }
        }

        _runtime.Log("ERROR", modId, $"Fault wall caught {ex.GetType().Name}: {ex.Message}{(disabledNow ? " [MOD DISABLED]" : string.Empty)}");
        ModFaulted?.Invoke(modId, ex, disabledNow);
    }

    internal static void TrackModDisposable(LoadedClientMod mod, IDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        bool disposeImmediately = false;
        lock (mod.SyncRoot)
        {
            if (mod.IsDisabled)
            {
                disposeImmediately = true;
            }
            else
            {
                mod.TrackedDisposables.Add(resource);
            }
        }

        if (disposeImmediately)
        {
            try { resource.Dispose(); }
            catch { }
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
        IDisposable[] snapshot;
        lock (mod.SyncRoot)
        {
            snapshot = [.. mod.TrackedDisposables];
            mod.TrackedDisposables.Clear();
        }

        for (int i = snapshot.Length - 1; i >= 0; i--)
        {
            try { snapshot[i].Dispose(); }
            catch { }
        }
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

    public void TrackDisposable(IDisposable resource) =>
        ClientModLoader.TrackModDisposable(_mod, resource);
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
    public NativeCallResult CallEx(string symbolName, ReadOnlySpan<NativeCallArgument> args, bool returnIsFloat = false) =>
        inner.CallEx(symbolName, args, returnIsFloat);
    public NativeCallResult CallEx(nint targetAddress, ReadOnlySpan<NativeCallArgument> args, bool returnIsFloat = false) =>
        inner.CallEx(targetAddress, args, returnIsFloat);

    public IDisposable InstallHook(string symbolName, nint detourTarget, out nint originalTrampoline, HookType hookType = HookType.InlineDetour)
    {
        IDisposable sub = inner.InstallHook(symbolName, detourTarget, out originalTrampoline, hookType);
        track(sub);
        return sub;
    }

    public IDisposable InstallHook(string symbolName, nint detourTarget, out nint originalTrampoline, HookType hookType, uint vtableSlotIndex)
    {
        IDisposable sub = inner.InstallHook(symbolName, detourTarget, out originalTrampoline, hookType, vtableSlotIndex);
        track(sub);
        return sub;
    }

    public IDisposable InstallVtableHook(nint vtableAddress, uint slotIndex, nint detourTarget, out nint originalSlot)
    {
        IDisposable sub = inner.InstallVtableHook(vtableAddress, slotIndex, detourTarget, out originalSlot);
        track(sub);
        return sub;
    }

    public IDisposable InstallHookChain(string symbolName, HookPhase phase, Action<HookContext> callback, int priority = 0)
    {
        IDisposable sub = inner.InstallHookChain(symbolName, phase, callback, priority);
        track(sub);
        return sub;
    }

    public IDisposable InstallHookChain(nint address, string? expectedBytesHex, HookPhase phase, Action<HookContext> callback, int priority = 0)
    {
        IDisposable sub = inner.InstallHookChain(address, expectedBytesHex, phase, callback, priority);
        track(sub);
        return sub;
    }

    public IDisposable InstallVtableHookChain(nint vtableAddress, uint slotIndex, HookPhase phase, Action<HookContext> callback, int priority = 0)
    {
        IDisposable sub = inner.InstallVtableHookChain(vtableAddress, slotIndex, phase, callback, priority);
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
        var ui = new TrackedUiService(runtime.Ui, mod.Manifest.Id, track);
        track(ui);
        Ui = ui;
        Chat = new TrackedChatService(runtime.Chat, loader, mod.Manifest.Id, track);
        Files = new TrackedFileOverlayService(runtime.FileOverlayService, mod.Manifest.Id, track);
        Scripts = new TrackedScriptService(runtime.Scripts, loader, mod.Manifest.Id, track);
        var audio = new TrackedAudioService(runtime.Audio);
        track(audio);
        Audio = audio;
        Stats = new TrackedStatsService(runtime.Stats, loader, mod.Manifest.Id, track);
        var world = new TrackedWorldService(runtime.World);
        track(world);
        World = world;
        var lifecycle = new TrackedGameLifecycle(runtime.Lifecycle, loader, mod.Manifest.Id);
        track(lifecycle);
        Lifecycle = lifecycle;
        var screens = new TrackedScreensService(runtime.Screens, loader, mod.Manifest.Id, track);
        track(screens);
        Screens = screens;
        Input = new TrackedInputService(runtime.Input, loader, mod.Manifest.Id, track);
        var hover = new TrackedHoverService(runtime.Hover, loader, mod.Manifest.Id);
        track(hover);
        Hover = hover;
        var toasts = new TrackedToastService(runtime.Toasts, mod.Manifest.Id);
        track(toasts);
        Toasts = toasts;
        var preGame = new TrackedPreGameService(runtime.PreGame, loader, mod.Manifest.Id);
        track(preGame);
        PreGame = preGame;
        var itemFilter = new TrackedItemFilterService(runtime.ItemFilter, loader, mod.Manifest.Id);
        track(itemFilter);
        ItemFilter = itemFilter;
        var camera = new TrackedCameraService(runtime.Camera, mod.Manifest.Id);
        track(camera);
        Camera = camera;
        var tooltips = new TrackedTooltipService(runtime.Tooltips, loader, mod.Manifest.Id, track);
        track(tooltips);
        Tooltips = tooltips;
        StashAffinity = new TrackedStashAffinityService(runtime.StashAffinity, loader, mod.Manifest.Id, track);
        var stackExtension = new TrackedStackExtensionService(runtime.StackExtension, mod.Manifest.Id);
        track(stackExtension);
        StackExtension = stackExtension;
        GealBuiltins = new TrackedGealBuiltinRegistry(runtime.GealBuiltins, loader, mod.Manifest.Id, track);
        UiControls = new TrackedUiControlRegistry(runtime.UiControls, loader, mod.Manifest.Id, track);
        Rulesets = new TrackedRulesetRegistry(runtime.Rulesets, loader, mod.Manifest.Id, track);
        Opcodes = new TrackedOpcodeRegistry(runtime.Opcodes, loader, mod.Manifest.Id, track);
        Content = new TrackedContentRegistry(runtime.Content, track);
    }

    public IUiService Ui { get; }
    public IClipboardService Clipboard => _runtime.Clipboard;
    public IChatService Chat { get; }
    public IFileOverlayService Files { get; }
    public IScriptService Scripts { get; }
    public IAudioService Audio { get; }
    public IStatsService Stats { get; }
    public IWorldService World { get; }
    public IGameLifecycle Lifecycle { get; }
    public IScreensService Screens { get; }
    public IInputService Input { get; }
    public IHoverService Hover { get; }
    public IToastService Toasts { get; }
    public IPreGameService PreGame { get; }
    public IItemFilterService ItemFilter { get; }
    public ICameraService Camera { get; }
    public ITooltipService Tooltips { get; }
    public IStashAffinityService StashAffinity { get; }
    public IStackExtensionService StackExtension { get; }
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

    private sealed class TrackedUiService(UiService inner, string modId, Action<IDisposable> track) : IUiService, IDisposable
    {
        private bool _disposed;

        public IReadOnlyCollection<IUiWindow> OpenWindows => inner.OpenWindows;
        public bool IsCompactSocketsEnabled => inner.IsCompactSocketsEnabled;
        public void SetCompactSocketsEnabled(bool enabled)
        {
            if (Volatile.Read(ref _disposed))
            {
                return;
            }

            inner.SetCompactSocketsEnabledForOwner(modId, enabled);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, true))
            {
                return;
            }

            inner.ClearCompactSocketsForOwner(modId);
        }
        public IUiControl? GetRootControl() => inner.GetRootControl();
        public IUiControl? WrapControl(nint nativeHandle) => inner.WrapControl(nativeHandle);
        public IUiWindow Load(string virtualPath, string? windowId = null) { IUiWindow w = inner.Load(virtualPath, windowId); track(w); return w; }
        public IUiWindow CreateWindowInto(IUiControl parent, string virtualPath, string? windowId = null) { IUiWindow w = inner.CreateWindowInto(parent, virtualPath, windowId); track(w); return w; }
        public IUiWindow? GetWindow(string windowId) => inner.GetWindow(windowId);
        public bool CloseWindow(string windowId) => inner.CloseWindow(windowId);
        public void ReloadWindow(string windowId) => inner.ReloadWindow(windowId);
        public IDisposable RegisterControl<TControl>(string controlTypeName) where TControl : class, ICustomUiControl, new() { IDisposable d = inner.RegisterControl<TControl>(controlTypeName); track(d); return d; }
        public IDisposable RegisterControl(string controlTypeName, Func<ICustomUiControl> factory) { IDisposable d = inner.RegisterControl(controlTypeName, factory); track(d); return d; }

        public IUiControl CreateControl(IUiControl parent, UiControlKind kind, string? id = null)
        {
            IUiControl ctrl = inner.CreateControl(parent, kind, id);
            track(new ActionDisposable(ctrl.Destroy));
            return ctrl;
        }

        public IUiControl CreateFrame(IUiControl parent, string? id = null)
        {
            IUiControl ctrl = inner.CreateFrame(parent, id);
            track(new ActionDisposable(ctrl.Destroy));
            return ctrl;
        }

        public ILabel CreateLabel(IUiControl parent, string text = "", string? id = null)
        {
            ILabel ctrl = inner.CreateLabel(parent, text, id);
            track(new ActionDisposable(ctrl.Destroy));
            return ctrl;
        }

        public IButton CreateButton(IUiControl parent, string text = "", string? id = null)
        {
            IButton ctrl = inner.CreateButton(parent, text, id);
            track(new ActionDisposable(ctrl.Destroy));
            return ctrl;
        }

        public IPicture CreatePicture(IUiControl parent, string? texturePath = null, string? id = null)
        {
            IPicture ctrl = inner.CreatePicture(parent, texturePath, id);
            track(new ActionDisposable(ctrl.Destroy));
            return ctrl;
        }

        public IScrollableFrame CreateScrollableFrame(IUiControl parent, string? id = null)
        {
            IScrollableFrame ctrl = inner.CreateScrollableFrame(parent, id);
            track(new ActionDisposable(ctrl.Destroy));
            return ctrl;
        }

        public ITabContainer CreateTabContainer(IUiControl parent, string? id = null)
        {
            ITabContainer ctrl = inner.CreateTabContainer(parent, id);
            track(new ActionDisposable(ctrl.Destroy));
            return ctrl;
        }

        public IComboBox CreateComboBox(IUiControl parent, string? id = null)
        {
            IComboBox ctrl = inner.CreateComboBox(parent, id);
            track(new ActionDisposable(ctrl.Destroy));
            return ctrl;
        }

        public IListBox CreateListBox(IUiControl parent, string? id = null)
        {
            IListBox ctrl = inner.CreateListBox(parent, id);
            track(new ActionDisposable(ctrl.Destroy));
            return ctrl;
        }

        public IUiItemGrid CreateItemGrid(IUiControl parent, UiItemGridOptions options, string? id = null)
        {
            IUiItemGrid grid = inner.CreateItemGrid(parent, options, id);
            track(grid);
            return grid;
        }
    }

    private sealed class TrackedFileOverlayService(FileOverlayService inner, string modId, Action<IDisposable> track) : IFileOverlayService, IStockFileProvider
    {
        public IDisposable RegisterDirectory(string rootDirectory, int priority = 0) { IDisposable d = inner.RegisterDirectoryForMod(modId, rootDirectory, priority); track(d); return d; }
        public IDisposable RegisterVirtualFile(string virtualPath, ReadOnlyMemory<byte> rawBytes, int priority = 100) { IDisposable d = inner.RegisterVirtualFileForMod(modId, virtualPath, rawBytes, priority); track(d); return d; }
        public IDisposable RegisterVirtualTextFile(string virtualPath, string text, int priority = 100) { IDisposable d = inner.RegisterVirtualTextFileForMod(modId, virtualPath, text, priority); track(d); return d; }
        public IDisposable RegisterBundle(string bundlePath, string manifestPath, int priority = -10) { IDisposable d = inner.RegisterBundleForMod(modId, bundlePath, manifestPath, priority); track(d); return d; }
        public bool Exists(string virtualPath) => inner.Exists(virtualPath);
        public byte[]? ReadAllBytes(string virtualPath) => inner.ReadAllBytes(virtualPath);
        public string? ReadAllText(string virtualPath) => inner.ReadAllText(virtualPath);
        public byte[]? ReadStockGameFile(string virtualPath) => inner.ReadStockGameFile(virtualPath);
        public void Invalidate(string virtualPath) => inner.Invalidate(virtualPath);
        public IReadOnlyList<FileOverlayEntry> GetRegisteredOverlays() => inner.GetRegisteredOverlays();
    }

    private sealed class TrackedChatService(IChatService inner, ClientModLoader loader, string modId, Action<IDisposable> track) : IChatService
    {
        public IDisposable RegisterInterceptor(Func<string, string, bool> interceptor)
        {
            ArgumentNullException.ThrowIfNull(interceptor);
            IDisposable sub = inner.RegisterInterceptor((sender, text) =>
            {
                bool result = false;
                loader.ExecuteFaultIsolated(modId, () => result = interceptor(sender, text));
                return result;
            });
            track(sub);
            return sub;
        }

        public void SendLine(string text) => inner.SendLine(text);
    }

    private sealed class TrackedScriptService(IScriptService inner, ClientModLoader loader, string modId, Action<IDisposable> track) : IScriptService
    {
        public GealInvocationResult InvokeFunction(uint objectId, string functionName, params GealValue[] arguments) => inner.InvokeFunction(objectId, functionName, arguments);
        public GealInvocationResult InvokeEvent(uint objectId, string eventName, params GealValue[] arguments) => inner.InvokeEvent(objectId, eventName, arguments);
        public IReadOnlyList<GealScriptDiagnostic> ValidateScript(string gealSource) => inner.ValidateScript(gealSource);
        public IReadOnlyCollection<string> GetRegisteredBuiltins() => inner.GetRegisteredBuiltins();
        public IDisposable Register(string name, Func<uint, GealArguments, GealValue> callback) { IDisposable d = inner.Register(name, callback); track(d); return d; }
        public IDisposable Register(string name, Action<uint, GealArguments> callback) { IDisposable d = inner.Register(name, callback); track(d); return d; }
        public IDisposable RegisterChatInterceptor(Func<string, string, bool> interceptor)
        {
            ArgumentNullException.ThrowIfNull(interceptor);
            IDisposable d = inner.RegisterChatInterceptor((sender, text) =>
            {
                bool result = false;
                loader.ExecuteFaultIsolated(modId, () => result = interceptor(sender, text));
                return result;
            });
            track(d);
            return d;
        }
    }

    private sealed class TrackedAudioService(IAudioService inner) : IAudioService, IDisposable
    {
        private readonly HashSet<int> _ownedChannels = [];
        private readonly Lock _gate = new();

        public int ActiveChannelCount => inner.ActiveChannelCount;

        public int PlayEvent(string fmodEventPath, float volume = 1.0f)
        {
            int ch = inner.PlayEvent(fmodEventPath, volume);
            if (ch > 0)
            {
                lock (_gate) { _ownedChannels.Add(ch); }
            }

            return ch;
        }

        public int PlaySoundFile(string virtualOggPath, float volume = 1.0f) => PlaySoundFile(virtualOggPath, volume, loop: false);

        public int PlaySoundFile(string virtualOggPath, float volume, bool loop)
        {
            int ch = inner.PlaySoundFile(virtualOggPath, volume, loop);
            if (ch > 0)
            {
                lock (_gate) { _ownedChannels.Add(ch); }
            }

            return ch;
        }

        public AudioChannelHandle PlaySoundAtPosition(
            string pathOrEvent,
            float x,
            float y,
            float z,
            float volume = 1.0f,
            float maxDistance = 1200f,
            bool loop = false)
        {
            AudioChannelHandle ch = inner.PlaySoundAtPosition(pathOrEvent, x, y, z, volume, maxDistance, loop);
            if (ch.IsValid)
            {
                lock (_gate) { _ownedChannels.Add(ch.Value); }
            }

            return ch;
        }

        public void SetChannelPosition(AudioChannelHandle channel, float x, float y, float z, float maxDistance = 1200f) =>
            inner.SetChannelPosition(channel, x, y, z, maxDistance);

        public void SetChannelVolume(int channelHandle, float volume) => inner.SetChannelVolume(channelHandle, volume);

        public bool IsChannelPlaying(int channelHandle) => inner.IsChannelPlaying(channelHandle);

        public void StopChannel(int channelHandle)
        {
            lock (_gate) { _ownedChannels.Remove(channelHandle); }
            inner.StopChannel(channelHandle);
        }

        public void Dispose()
        {
            int[] channels;
            lock (_gate)
            {
                channels = [.. _ownedChannels];
                _ownedChannels.Clear();
            }

            foreach (int ch in channels)
            {
                try { inner.StopChannel(ch); }
                catch { }
            }
        }
    }

    private sealed class TrackedWorldService(IWorldService inner) : IWorldService, IDisposable
    {
        private readonly HashSet<uint> _ownedObjects = [];
        private readonly Lock _gate = new();

        public string CurrentAreaId => inner.CurrentAreaId;
        public IReadOnlyCollection<WorldObjectInfo> Objects => inner.Objects;
        public WorldObjectInfo? FindObject(uint objectId) => inner.FindObject(objectId);

        public uint SpawnClientObject(string metadataPath, float x, float y, float z = 0f)
        {
            uint id = inner.SpawnClientObject(metadataPath, x, y, z);
            lock (_gate) { _ownedObjects.Add(id); }
            return id;
        }

        public uint SpawnClientObject(string metadataPath, float x, float y, float z, float rotationRadians, float scale = 1.0f)
        {
            uint id = inner.SpawnClientObject(metadataPath, x, y, z, rotationRadians, scale);
            lock (_gate) { _ownedObjects.Add(id); }
            return id;
        }

        public bool RemoveClientObject(uint objectId)
        {
            lock (_gate) { _ownedObjects.Remove(objectId); }
            return inner.RemoveClientObject(objectId);
        }

        public void PlayEffect(uint targetObjectId, string effectPath) => inner.PlayEffect(targetObjectId, effectPath);
        public WorldEntitySnapshot? GetLocalPlayer() => inner.GetLocalPlayer();
        public IReadOnlyList<WorldEntitySnapshot> GetEntities(WorldEntityQuery? query = null) => inner.GetEntities(query);
        public WorldEntitySnapshot? GetEntity(uint entityId) => inner.GetEntity(entityId);
        public IReadOnlyList<WorldEntitySnapshot> GetWorldItems(bool filterVisibleOnly = false, float? maxDistance = null) => inner.GetWorldItems(filterVisibleOnly, maxDistance);
        public WorldPosition3D? GetEntityPosition(uint entityId) => inner.GetEntityPosition(entityId);

        public void Dispose()
        {
            uint[] ids;
            lock (_gate)
            {
                ids = [.. _ownedObjects];
                _ownedObjects.Clear();
            }

            foreach (uint id in ids)
            {
                try { inner.RemoveClientObject(id); }
                catch { }
            }
        }
    }

    private sealed class TrackedGameLifecycle(IGameLifecycle inner, ClientModLoader loader, string modId) : IGameLifecycle, IDisposable
    {
        private enum LifecycleEventKind : byte { Frame, AreaEntered, AreaLeft, Connected, Disconnected }

        private readonly List<(LifecycleEventKind Kind, Delegate Original, Delegate Wrapped)> _subscriptions = [];
        private readonly Lock _gate = new();
        private bool _disposed;

        public long FrameCount => inner.FrameCount;
        public bool IsConnected => inner.IsConnected;
        public string? CurrentAreaId => inner.CurrentAreaId;
        public AreaInfo? CurrentArea => inner.CurrentArea;

        public event Action<float>? Frame
        {
            add
            {
                if (value is null) return;
                Action<float> wrapped = dt => loader.ExecuteFaultIsolated(modId, () => value(dt));
                lock (_gate)
                {
                    if (_disposed) return;
                    _subscriptions.Add((LifecycleEventKind.Frame, value, wrapped));
                }

                inner.Frame += wrapped;
            }
            remove
            {
                if (value is null) return;
                if (RemoveSubscription(LifecycleEventKind.Frame, value) is Action<float> wrapped)
                {
                    inner.Frame -= wrapped;
                }
            }
        }

        public event Action<AreaEventArgs>? AreaEntered
        {
            add
            {
                if (value is null) return;
                Action<AreaEventArgs> wrapped = args => loader.ExecuteFaultIsolated(modId, () => value(args));
                lock (_gate)
                {
                    if (_disposed) return;
                    _subscriptions.Add((LifecycleEventKind.AreaEntered, value, wrapped));
                }

                inner.AreaEntered += wrapped;
            }
            remove
            {
                if (value is null) return;
                if (RemoveSubscription(LifecycleEventKind.AreaEntered, value) is Action<AreaEventArgs> wrapped)
                {
                    inner.AreaEntered -= wrapped;
                }
            }
        }

        public event Action<string>? AreaLeft
        {
            add
            {
                if (value is null) return;
                Action<string> wrapped = areaId => loader.ExecuteFaultIsolated(modId, () => value(areaId));
                lock (_gate)
                {
                    if (_disposed) return;
                    _subscriptions.Add((LifecycleEventKind.AreaLeft, value, wrapped));
                }

                inner.AreaLeft += wrapped;
            }
            remove
            {
                if (value is null) return;
                if (RemoveSubscription(LifecycleEventKind.AreaLeft, value) is Action<string> wrapped)
                {
                    inner.AreaLeft -= wrapped;
                }
            }
        }

        public event Action? Connected
        {
            add
            {
                if (value is null) return;
                Action wrapped = () => loader.ExecuteFaultIsolated(modId, value);
                lock (_gate)
                {
                    if (_disposed) return;
                    _subscriptions.Add((LifecycleEventKind.Connected, value, wrapped));
                }

                inner.Connected += wrapped;
            }
            remove
            {
                if (value is null) return;
                if (RemoveSubscription(LifecycleEventKind.Connected, value) is Action wrapped)
                {
                    inner.Connected -= wrapped;
                }
            }
        }

        public event Action<string?>? Disconnected
        {
            add
            {
                if (value is null) return;
                Action<string?> wrapped = reason => loader.ExecuteFaultIsolated(modId, () => value(reason));
                lock (_gate)
                {
                    if (_disposed) return;
                    _subscriptions.Add((LifecycleEventKind.Disconnected, value, wrapped));
                }

                inner.Disconnected += wrapped;
            }
            remove
            {
                if (value is null) return;
                if (RemoveSubscription(LifecycleEventKind.Disconnected, value) is Action<string?> wrapped)
                {
                    inner.Disconnected -= wrapped;
                }
            }
        }

        private Delegate? RemoveSubscription(LifecycleEventKind kind, Delegate original)
        {
            lock (_gate)
            {
                for (int i = _subscriptions.Count - 1; i >= 0; i--)
                {
                    var entry = _subscriptions[i];
                    if (entry.Kind == kind && entry.Original.Equals(original))
                    {
                        _subscriptions.RemoveAt(i);
                        return entry.Wrapped;
                    }
                }
            }

            return null;
        }

        public void Dispose()
        {
            (LifecycleEventKind Kind, Delegate Original, Delegate Wrapped)[] snapshot;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                snapshot = [.. _subscriptions];
                _subscriptions.Clear();
            }

            foreach (var entry in snapshot)
            {
                switch (entry.Kind)
                {
                    case LifecycleEventKind.Frame:
                        inner.Frame -= (Action<float>)entry.Wrapped;
                        break;
                    case LifecycleEventKind.AreaEntered:
                        inner.AreaEntered -= (Action<AreaEventArgs>)entry.Wrapped;
                        break;
                    case LifecycleEventKind.AreaLeft:
                        inner.AreaLeft -= (Action<string>)entry.Wrapped;
                        break;
                    case LifecycleEventKind.Connected:
                        inner.Connected -= (Action)entry.Wrapped;
                        break;
                    case LifecycleEventKind.Disconnected:
                        inner.Disconnected -= (Action<string?>)entry.Wrapped;
                        break;
                }
            }
        }
    }

    private sealed class FaultIsolatedRulesetProxy(IClientRuleset inner, ClientModLoader loader, string modId) : IClientRuleset
    {
        public byte RulesetId => inner.RulesetId;
        public string Name => inner.Name;

        public void OnAreaEntered(AreaEventArgs area) =>
            loader.ExecuteFaultIsolated(modId, () => inner.OnAreaEntered(area));

        public void OnRulesetUpdate(ReadOnlySpan<byte> payload)
        {
            LoadedClientMod? mod = loader.GetMod(modId);
            if (mod is not null && mod.IsDisabled)
            {
                return;
            }

            try
            {
                inner.OnRulesetUpdate(payload);
            }
            catch (Exception ex)
            {
                loader.RecordExternalFault(modId, ex);
            }
        }

        public void OnFrame(float deltaSeconds) =>
            loader.ExecuteFaultIsolated(modId, () => inner.OnFrame(deltaSeconds));

        public void OnAreaLeft() =>
            loader.ExecuteFaultIsolated(modId, inner.OnAreaLeft);
    }

    private sealed class TrackedStatsService(IStatsService inner, ClientModLoader loader, string modId, Action<IDisposable> track) : IStatsService
    {
        public int GetStat(string statKey, int defaultValue = 0) => inner.GetStat(statKey, defaultValue);
        public void SetLocalStat(string statKey, int value) => inner.SetLocalStat(statKey, value);

        public IDisposable OnStatChanged(string statKey, Action<int> listener)
        {
            ArgumentNullException.ThrowIfNull(listener);
            IDisposable d = inner.OnStatChanged(statKey, val => loader.ExecuteFaultIsolated(modId, () => listener(val)));
            track(d);
            return d;
        }

        public int ReadStat(uint entityId, int statId) => inner.ReadStat(entityId, statId);
        public bool TryReadStat(uint entityId, int statId, out int value) => inner.TryReadStat(entityId, statId, out value);
        public void SetLocalStat(uint entityId, int statId, int value) => inner.SetLocalStat(entityId, statId, value);
        public IReadOnlyDictionary<int, int> GetAllStats(uint entityId) => inner.GetAllStats(entityId);
        public int ReadLocalPlayerStat(int statId) => inner.ReadLocalPlayerStat(statId);
        public bool TryReadLocalPlayerStat(int statId, out int value) => inner.TryReadLocalPlayerStat(statId, out value);
        public IReadOnlyDictionary<int, int> GetLocalPlayerStats() => inner.GetLocalPlayerStats();
    }

    private sealed class FaultIsolatedCustomUiControlProxy(ICustomUiControl inner, ClientModLoader loader, string modId) : ICustomUiControl
    {
        public string ControlTypeName => inner.ControlTypeName;

        public void OnInitialize(IUiControl hostControl) =>
            loader.ExecuteFaultIsolated(modId, () => inner.OnInitialize(hostControl));

        public void OnPropertyChanged(string propertyName, string value) =>
            loader.ExecuteFaultIsolated(modId, () => inner.OnPropertyChanged(propertyName, value));

        public void OnFrame(float deltaSeconds) =>
            loader.ExecuteFaultIsolated(modId, () => inner.OnFrame(deltaSeconds));

        public void OnDestroy() =>
            loader.ExecuteFaultIsolated(modId, inner.OnDestroy);
    }

    private sealed class TrackedGealBuiltinRegistry(IGealBuiltinRegistry inner, ClientModLoader loader, string modId, Action<IDisposable> track) : IGealBuiltinRegistry
    {
        public IReadOnlyCollection<string> RegisteredNames => inner.RegisteredNames;

        public IDisposable Register(string name, Func<uint, GealArguments, GealValue> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            IDisposable d = inner.Register(name, (objId, args) =>
            {
                GealValue result = GealValue.Null;
                loader.ExecuteFaultIsolated(modId, () => result = handler(objId, args));
                return result;
            });
            track(d);
            return d;
        }

        public IDisposable Register(string name, Action<uint, GealArguments> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            IDisposable d = inner.Register(name, (objId, args) =>
                loader.ExecuteFaultIsolated(modId, () => handler(objId, args)));
            track(d);
            return d;
        }

        public bool IsRegistered(string name) => inner.IsRegistered(name);
        public GealInvocationResult Invoke(string name, uint objectId, params GealValue[] arguments) => inner.Invoke(name, objectId, arguments);
    }

    private sealed class TrackedUiControlRegistry(IUiControlRegistry inner, ClientModLoader loader, string modId, Action<IDisposable> track) : IUiControlRegistry
    {
        public IReadOnlyCollection<string> RegisteredControlTypes => inner.RegisteredControlTypes;

        public IDisposable Register<TControl>(string controlTypeName) where TControl : class, ICustomUiControl, new() =>
            Register(controlTypeName, static () => new TControl());

        public IDisposable Register(string controlTypeName, Func<ICustomUiControl> factory)
        {
            ArgumentNullException.ThrowIfNull(factory);
            IDisposable d = inner.Register(controlTypeName, () => new FaultIsolatedCustomUiControlProxy(factory(), loader, modId));
            track(d);
            return d;
        }

        public bool IsRegistered(string controlTypeName) => inner.IsRegistered(controlTypeName);
        public ICustomUiControl? CreateInstance(string controlTypeName) => inner.CreateInstance(controlTypeName);
    }

    private sealed class TrackedRulesetRegistry(IRulesetRegistry inner, ClientModLoader loader, string modId, Action<IDisposable> track) : IRulesetRegistry
    {
        public IReadOnlyCollection<byte> RegisteredIds => inner.RegisteredIds;

        public IDisposable Register<TRuleset>() where TRuleset : class, IClientRuleset, new()
        {
            var probe = new TRuleset();
            return Register(probe.RulesetId, static () => new TRuleset());
        }

        public IDisposable Register(byte rulesetId, Func<IClientRuleset> factory)
        {
            ArgumentNullException.ThrowIfNull(factory);
            IDisposable d = inner.Register(rulesetId, () => new FaultIsolatedRulesetProxy(factory(), loader, modId));
            track(d);
            return d;
        }

        public IDisposable RegisterNative<TRuleset>() where TRuleset : class, IClientRuleset, new()
        {
            var probe = new TRuleset();
            return RegisterNative(probe.RulesetId, static () => new TRuleset());
        }

        public IDisposable RegisterNative(byte rulesetId, Func<IClientRuleset> factory)
        {
            ArgumentNullException.ThrowIfNull(factory);
            IDisposable d = inner.RegisterNative(rulesetId, () => new FaultIsolatedRulesetProxy(factory(), loader, modId));
            track(d);
            return d;
        }

        public bool IsRegistered(byte rulesetId) => inner.IsRegistered(rulesetId);
        public bool TryDispatchUpdate(byte rulesetId, ReadOnlySpan<byte> payload) => inner.TryDispatchUpdate(rulesetId, payload);
    }

    private sealed class TrackedOpcodeRegistry(IOpcodeRegistry inner, ClientModLoader loader, string modId, Action<IDisposable> track) : IOpcodeRegistry
    {
        public IReadOnlyCollection<ushort> RegisteredOpcodes => inner.RegisteredOpcodes;

        public IDisposable RegisterHandler(ushort opcode, Action<ReadOnlyMemory<byte>> handler, string? groupName = null)
        {
            ArgumentNullException.ThrowIfNull(handler);
            IDisposable d = inner.RegisterHandler(opcode, payload => loader.ExecuteFaultIsolated(modId, () => handler(payload)), groupName);
            track(d);
            return d;
        }

        public IDisposable RegisterGroup(ushort minOpcode, ushort maxOpcode, Action<ushort, ReadOnlyMemory<byte>> handler, string? groupName = null)
        {
            ArgumentNullException.ThrowIfNull(handler);
            IDisposable d = inner.RegisterGroup(minOpcode, maxOpcode, (op, payload) => loader.ExecuteFaultIsolated(modId, () => handler(op, payload)), groupName);
            track(d);
            return d;
        }

        public IDisposable RegisterStreamHandler(ushort minOpcode, ushort maxOpcode, Func<ushort, INativePacketStream, ReadOnlyMemory<byte>, bool> handler, string? groupName = null)
        {
            ArgumentNullException.ThrowIfNull(handler);
            IDisposable d = inner.RegisterStreamHandler(
                minOpcode,
                maxOpcode,
                (op, stream, payload) =>
                {
                    bool handled = false;
                    loader.ExecuteFaultIsolated(modId, () => handled = handler(op, stream, payload));
                    return handled;
                },
                groupName);
            track(d);
            return d;
        }

        public bool IsRegistered(ushort opcode) => inner.IsRegistered(opcode);
        public bool TryDispatch(ushort opcode, ReadOnlyMemory<byte> payload) => inner.TryDispatch(opcode, payload);
    }

    private sealed class TrackedContentRegistry(IContentRegistry inner, Action<IDisposable> track) : IContentRegistry
    {
        public ContentRowMap? RowMap => inner.RowMap;
        public string? ContentRowMapDigest => inner.ContentRowMapDigest;
        public IReadOnlyList<DatRowDeclaration> DatRows => inner.DatRows;
        public IReadOnlyList<ItemBaseDeclaration> ItemBases => inner.ItemBases;
        public IReadOnlyList<WorldObjectDeclaration> WorldObjects => inner.WorldObjects;
        public IReadOnlyList<ObjectOverlayDeclaration> Overlays => inner.Overlays;
        public bool TryResolveRow(string tableName, string entryKey, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ContentRowEntry? entry) =>
            inner.TryResolveRow(tableName, entryKey, out entry);
        public IDisposable DeclareDatRow(DatRowDeclaration row) { IDisposable d = inner.DeclareDatRow(row); track(d); return d; }
        public IDisposable DeclareItemBase(ItemBaseDeclaration itemBase) { IDisposable d = inner.DeclareItemBase(itemBase); track(d); return d; }
        public IDisposable DeclareWorldObject(WorldObjectDeclaration worldObject) { IDisposable d = inner.DeclareWorldObject(worldObject); track(d); return d; }
        public IDisposable DeclareOverlay(ObjectOverlayDeclaration overlay) { IDisposable d = inner.DeclareOverlay(overlay); track(d); return d; }
        public uint ComputeDeterministicId(string modId, string entryKey) => inner.ComputeDeterministicId(modId, entryKey);
    }

    private sealed class TrackedScreensService(ScreensService inner, ClientModLoader loader, string modId, Action<IDisposable> track) : IScreensService, IDisposable
    {
        private enum ScreenEventKind : byte { ScreenChanged, PreGameScreenChanged, ActiveStashTabChanged, LeagueOverlayFilled }

        private readonly List<(ScreenEventKind Kind, Delegate Original, Delegate Wrapped)> _subscriptions = [];
        private readonly Lock _gate = new();
        private bool _disposed;

        public PreGameScreen CurrentPreGameScreen => inner.CurrentPreGameScreen;
        public GamePanel ActivePanels => inner.ActivePanels;
        public bool IsPanelOpen(GamePanel panel) => inner.IsPanelOpen(panel);
        public IUiControl? GetPanelControl(GamePanel panel) => inner.GetPanelControl(panel);
        public StashTabInfo? ActiveStashTab => inner.ActiveStashTab;
        public IReadOnlyList<LeagueCardInfo> ActiveLeagueCards => inner.ActiveLeagueCards;
        public IReadOnlyList<ISlotAttachment> GetSlotAttachments(ScreenSlot slot) => inner.GetSlotAttachments(slot);

        public ISlotAttachment RegisterSlot(ScreenSlot slot, string id, string virtualUiPath, SlotAttachmentOptions? options = null)
        {
            ISlotAttachment attachment = inner.RegisterSlot(slot, id, virtualUiPath, options);
            track(attachment);
            return attachment;
        }

        public ISlotAttachment RegisterSlotButton(ScreenSlot slot, string id, string text, Action onClick, SlotAttachmentOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(onClick);
            ISlotAttachment attachment = inner.RegisterSlotButton(slot, id, text, () => loader.ExecuteFaultIsolated(modId, onClick), options);
            track(attachment);
            return attachment;
        }

        public ILeagueModeRegistration RegisterLeagueCardMode(LeagueModeCheckboxOptions options, Action<string, bool>? onChanged = null)
        {
            Action<string, bool>? wrapped = onChanged is null
                ? null
                : (leagueId, isChecked) => loader.ExecuteFaultIsolated(modId, () => onChanged(leagueId, isChecked));
            ILeagueModeRegistration reg = inner.RegisterLeagueCardMode(options, wrapped);
            track(reg);
            return reg;
        }

        public ISlotAttachment AttachToControl(nint parentHandle, string virtualUiPath, string? id = null, float x = 0f, float y = 0f)
        {
            ISlotAttachment attachment = inner.AttachToControl(parentHandle, virtualUiPath, id, x, y);
            track(attachment);
            return attachment;
        }

        public event Action<ScreenChangedEventArgs>? ScreenChanged
        {
            add
            {
                if (value is null) return;
                Action<ScreenChangedEventArgs> wrapped = args => loader.ExecuteFaultIsolated(modId, () => value(args));
                lock (_gate)
                {
                    if (_disposed) return;
                    _subscriptions.Add((ScreenEventKind.ScreenChanged, value, wrapped));
                }

                inner.ScreenChanged += wrapped;
            }
            remove
            {
                if (value is null) return;
                if (RemoveSubscription(ScreenEventKind.ScreenChanged, value) is Action<ScreenChangedEventArgs> wrapped)
                {
                    inner.ScreenChanged -= wrapped;
                }
            }
        }

        public event Action<PreGameScreenChangedEventArgs>? PreGameScreenChanged
        {
            add
            {
                if (value is null) return;
                Action<PreGameScreenChangedEventArgs> wrapped = args => loader.ExecuteFaultIsolated(modId, () => value(args));
                lock (_gate)
                {
                    if (_disposed) return;
                    _subscriptions.Add((ScreenEventKind.PreGameScreenChanged, value, wrapped));
                }

                inner.PreGameScreenChanged += wrapped;
            }
            remove
            {
                if (value is null) return;
                if (RemoveSubscription(ScreenEventKind.PreGameScreenChanged, value) is Action<PreGameScreenChangedEventArgs> wrapped)
                {
                    inner.PreGameScreenChanged -= wrapped;
                }
            }
        }

        public event Action<ActiveStashTabChangedEventArgs>? ActiveStashTabChanged
        {
            add
            {
                if (value is null) return;
                Action<ActiveStashTabChangedEventArgs> wrapped = args => loader.ExecuteFaultIsolated(modId, () => value(args));
                lock (_gate)
                {
                    if (_disposed) return;
                    _subscriptions.Add((ScreenEventKind.ActiveStashTabChanged, value, wrapped));
                }

                inner.ActiveStashTabChanged += wrapped;
            }
            remove
            {
                if (value is null) return;
                if (RemoveSubscription(ScreenEventKind.ActiveStashTabChanged, value) is Action<ActiveStashTabChangedEventArgs> wrapped)
                {
                    inner.ActiveStashTabChanged -= wrapped;
                }
            }
        }

        public event Action<LeagueOverlayFilledEventArgs>? LeagueOverlayFilled
        {
            add
            {
                if (value is null) return;
                Action<LeagueOverlayFilledEventArgs> wrapped = args => loader.ExecuteFaultIsolated(modId, () => value(args));
                lock (_gate)
                {
                    if (_disposed) return;
                    _subscriptions.Add((ScreenEventKind.LeagueOverlayFilled, value, wrapped));
                }

                inner.LeagueOverlayFilled += wrapped;
            }
            remove
            {
                if (value is null) return;
                if (RemoveSubscription(ScreenEventKind.LeagueOverlayFilled, value) is Action<LeagueOverlayFilledEventArgs> wrapped)
                {
                    inner.LeagueOverlayFilled -= wrapped;
                }
            }
        }

        private Delegate? RemoveSubscription(ScreenEventKind kind, Delegate original)
        {
            lock (_gate)
            {
                for (int i = _subscriptions.Count - 1; i >= 0; i--)
                {
                    var entry = _subscriptions[i];
                    if (entry.Kind == kind && entry.Original.Equals(original))
                    {
                        _subscriptions.RemoveAt(i);
                        return entry.Wrapped;
                    }
                }
            }

            return null;
        }

        public void Dispose()
        {
            (ScreenEventKind Kind, Delegate Original, Delegate Wrapped)[] snapshot;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                snapshot = [.. _subscriptions];
                _subscriptions.Clear();
            }

            foreach (var entry in snapshot)
            {
                switch (entry.Kind)
                {
                    case ScreenEventKind.ScreenChanged:
                        inner.ScreenChanged -= (Action<ScreenChangedEventArgs>)entry.Wrapped;
                        break;
                    case ScreenEventKind.PreGameScreenChanged:
                        inner.PreGameScreenChanged -= (Action<PreGameScreenChangedEventArgs>)entry.Wrapped;
                        break;
                    case ScreenEventKind.ActiveStashTabChanged:
                        inner.ActiveStashTabChanged -= (Action<ActiveStashTabChangedEventArgs>)entry.Wrapped;
                        break;
                    case ScreenEventKind.LeagueOverlayFilled:
                        inner.LeagueOverlayFilled -= (Action<LeagueOverlayFilledEventArgs>)entry.Wrapped;
                        break;
                }
            }
        }
    }

    private sealed class TrackedInputService : IInputService
    {
        private readonly InputService _inner;
        private readonly ClientModLoader _loader;
        private readonly string _modId;
        private readonly Action<IDisposable> _track;

        public TrackedInputService(InputService inner, ClientModLoader loader, string modId, Action<IDisposable> track)
        {
            _inner = inner;
            _loader = loader;
            _modId = modId;
            _track = track;
            var keybinds = new TrackedKeybindRegistry(inner.KeybindRegistry, loader, modId, track);
            track(keybinds);
            Keybinds = keybinds;
        }

        public bool IsTextInputFocused => _inner.IsTextInputFocused;
        public KeyModifiers ActiveModifiers => _inner.ActiveModifiers;
        public (int X, int Y) MousePosition => _inner.MousePosition;
        public IKeybindRegistry Keybinds { get; }

        public IDisposable OnKey(Action<KeyEventArgs> handler, int priority = 0, bool allowWhenTextInputFocused = false)
        {
            ArgumentNullException.ThrowIfNull(handler);
            IDisposable sub = _inner.OnKey(args => _loader.ExecuteFaultIsolated(_modId, () => handler(args)), priority, allowWhenTextInputFocused);
            _track(sub);
            return sub;
        }

        public IDisposable OnMouse(Action<MouseEventArgs> handler, int priority = 0, bool allowWhenTextInputFocused = false)
        {
            ArgumentNullException.ThrowIfNull(handler);
            IDisposable sub = _inner.OnMouse(args => _loader.ExecuteFaultIsolated(_modId, () => handler(args)), priority, allowWhenTextInputFocused);
            _track(sub);
            return sub;
        }
    }

    private sealed class TrackedKeybindRegistry(KeybindRegistry inner, ClientModLoader loader, string modId, Action<IDisposable> track) : IKeybindRegistry, IDisposable
    {
        private readonly List<(Action<KeybindConflict> Original, Action<KeybindConflict> Wrapped)> _conflictSubs = [];
        private readonly Lock _gate = new();
        private bool _disposed;

        public string ConfigPath => inner.ConfigPath;
        public IReadOnlyList<KeybindEntry> Entries => inner.Entries;
        public IReadOnlyList<KeybindConflict> Conflicts => inner.Conflicts;

        public IDisposable Register(
            string id,
            string displayName,
            KeyChord defaultChord,
            Action onTriggered,
            bool allowWhenTextInputFocused = false,
            bool suppressGameInput = false)
        {
            ArgumentNullException.ThrowIfNull(onTriggered);
            IDisposable sub = inner.RegisterForMod(
                modId,
                id,
                displayName,
                defaultChord,
                () => loader.ExecuteFaultIsolated(modId, onTriggered),
                allowWhenTextInputFocused,
                suppressGameInput);
            track(sub);
            return sub;
        }

        public KeyChord? GetBinding(string id) => inner.GetBinding(id);
        public bool Rebind(string id, KeyChord newChord, out IReadOnlyList<KeybindConflict> conflicts) => inner.Rebind(id, newChord, out conflicts);
        public bool ResetBinding(string id) => inner.ResetBinding(id);
        public void Save() => inner.Save();
        public void Reload() => inner.Reload();

        public event Action<KeybindConflict>? ConflictDetected
        {
            add
            {
                if (value is null) return;
                Action<KeybindConflict> wrapped = c => loader.ExecuteFaultIsolated(modId, () => value(c));
                lock (_gate)
                {
                    if (_disposed) return;
                    _conflictSubs.Add((value, wrapped));
                }

                inner.ConflictDetected += wrapped;
            }
            remove
            {
                if (value is null) return;
                Action<KeybindConflict>? wrapped = null;
                lock (_gate)
                {
                    for (int i = _conflictSubs.Count - 1; i >= 0; i--)
                    {
                        if (_conflictSubs[i].Original.Equals(value))
                        {
                            wrapped = _conflictSubs[i].Wrapped;
                            _conflictSubs.RemoveAt(i);
                            break;
                        }
                    }
                }

                if (wrapped is not null)
                {
                    inner.ConflictDetected -= wrapped;
                }
            }
        }

        public void Dispose()
        {
            (Action<KeybindConflict> Original, Action<KeybindConflict> Wrapped)[] snapshot;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                snapshot = [.. _conflictSubs];
                _conflictSubs.Clear();
            }

            foreach (var item in snapshot)
            {
                inner.ConflictDetected -= item.Wrapped;
            }
        }
    }

    private sealed class TrackedHoverService(HoverService inner, ClientModLoader loader, string modId) : IHoverService, IDisposable
    {
        private readonly List<(Action<HoverChangedEventArgs> Original, Action<HoverChangedEventArgs> Wrapped)> _subscriptions = [];
        private readonly Lock _gate = new();
        private bool _disposed;

        public IUiControl? HoveredControl => inner.HoveredControl;
        public HoveredItemInfo? HoveredItem => inner.HoveredItem;
        public CursorItemInfo? CursorItem => inner.CursorItem;
        public HoveredItemInfo? RefreshHoveredItem() => inner.RefreshHoveredItem();
        public CursorItemInfo? RefreshCursorItem() => inner.RefreshCursorItem();

        public event Action<HoverChangedEventArgs>? HoverChanged
        {
            add
            {
                if (value is null) return;
                Action<HoverChangedEventArgs> wrapped = args => loader.ExecuteFaultIsolated(modId, () => value(args));
                lock (_gate)
                {
                    if (_disposed) return;
                    _subscriptions.Add((value, wrapped));
                }

                inner.HoverChanged += wrapped;
            }
            remove
            {
                if (value is null) return;
                Action<HoverChangedEventArgs>? wrapped = null;
                lock (_gate)
                {
                    for (int i = _subscriptions.Count - 1; i >= 0; i--)
                    {
                        if (_subscriptions[i].Original.Equals(value))
                        {
                            wrapped = _subscriptions[i].Wrapped;
                            _subscriptions.RemoveAt(i);
                            break;
                        }
                    }
                }

                if (wrapped is not null)
                {
                    inner.HoverChanged -= wrapped;
                }
            }
        }

        public void Dispose()
        {
            (Action<HoverChangedEventArgs> Original, Action<HoverChangedEventArgs> Wrapped)[] snapshot;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                snapshot = [.. _subscriptions];
                _subscriptions.Clear();
            }

            foreach (var item in snapshot)
            {
                inner.HoverChanged -= item.Wrapped;
            }
        }
    }

    private sealed class TrackedToastService(ToastService inner, string modId) : IToastService, IDisposable
    {
        public IReadOnlyList<ToastMessage> ActiveToasts => inner.ActiveToasts;
        public int QueuedCount => inner.QueuedCount;
        public int MaxVisibleToasts { get => inner.MaxVisibleToasts; set => inner.MaxVisibleToasts = value; }
        public TimeSpan DefaultDuration { get => inner.DefaultDuration; set => inner.DefaultDuration = value; }

        public ulong Show(string title, string message, ToastSeverity severity = ToastSeverity.Info, TimeSpan? duration = null) =>
            inner.ShowForMod(modId, title, message, severity, duration);

        public bool Dismiss(ulong toastId) => inner.Dismiss(toastId);
        public void Clear() => inner.ClearForMod(modId);
        public void Dispose() => inner.ClearForMod(modId);
    }

    private sealed class TrackedPreGameService(PreGameService inner, ClientModLoader loader, string modId) : IPreGameService, IDisposable
    {
        private readonly List<(Action<CharacterCreateContext> Original, Action<CharacterCreateContext> Wrapped)> _creatingSubs = [];
        private readonly List<(Action<CharacterCreatedEventArgs> Original, Action<CharacterCreatedEventArgs> Wrapped)> _createdSubs = [];
        private readonly Lock _gate = new();
        private bool _disposed;

        public event Action<CharacterCreateContext>? CharacterCreating
        {
            add
            {
                if (value is null) return;
                Action<CharacterCreateContext> wrapped = ctx => loader.ExecuteFaultIsolated(modId, () => value(ctx));
                lock (_gate)
                {
                    if (_disposed) return;
                    _creatingSubs.Add((value, wrapped));
                }

                inner.CharacterCreating += wrapped;
            }
            remove
            {
                if (value is null) return;
                Action<CharacterCreateContext>? wrapped = null;
                lock (_gate)
                {
                    for (int i = _creatingSubs.Count - 1; i >= 0; i--)
                    {
                        if (_creatingSubs[i].Original.Equals(value))
                        {
                            wrapped = _creatingSubs[i].Wrapped;
                            _creatingSubs.RemoveAt(i);
                            break;
                        }
                    }
                }

                if (wrapped is not null)
                {
                    inner.CharacterCreating -= wrapped;
                }
            }
        }

        public event Action<CharacterCreatedEventArgs>? CharacterCreated
        {
            add
            {
                if (value is null) return;
                Action<CharacterCreatedEventArgs> wrapped = args => loader.ExecuteFaultIsolated(modId, () => value(args));
                lock (_gate)
                {
                    if (_disposed) return;
                    _createdSubs.Add((value, wrapped));
                }

                inner.CharacterCreated += wrapped;
            }
            remove
            {
                if (value is null) return;
                Action<CharacterCreatedEventArgs>? wrapped = null;
                lock (_gate)
                {
                    for (int i = _createdSubs.Count - 1; i >= 0; i--)
                    {
                        if (_createdSubs[i].Original.Equals(value))
                        {
                            wrapped = _createdSubs[i].Wrapped;
                            _createdSubs.RemoveAt(i);
                            break;
                        }
                    }
                }

                if (wrapped is not null)
                {
                    inner.CharacterCreated -= wrapped;
                }
            }
        }

        public void Dispose()
        {
            (Action<CharacterCreateContext> Original, Action<CharacterCreateContext> Wrapped)[] creating;
            (Action<CharacterCreatedEventArgs> Original, Action<CharacterCreatedEventArgs> Wrapped)[] created;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                creating = [.. _creatingSubs];
                created = [.. _createdSubs];
                _creatingSubs.Clear();
                _createdSubs.Clear();
            }

            foreach (var item in creating)
            {
                inner.CharacterCreating -= item.Wrapped;
            }

            foreach (var item in created)
            {
                inner.CharacterCreated -= item.Wrapped;
            }
        }
    }

    private sealed class TrackedItemFilterService(ItemFilterService inner, ClientModLoader loader, string modId) : IItemFilterService, IDisposable
    {
        private readonly List<(Action<ItemFilterReloadedEventArgs> Original, Action<ItemFilterReloadedEventArgs> Wrapped)> _reloadSubs = [];
        private readonly Lock _gate = new();
        private bool _disposed;

        public ItemFilterState GetState() => inner.GetState();
        public bool Reload() => inner.Reload();
        public bool IsWorldItemVisible(uint entityId) => inner.IsWorldItemVisible(entityId);
        public ItemFilterResult GetWorldItemResult(uint entityId) => inner.GetWorldItemResult(entityId);
        public IReadOnlyList<WorldEntitySnapshot> GetVisibleGroundItems(float? maxDistance = null) => inner.GetVisibleGroundItems(maxDistance);
        public ItemFilterResult EvaluateItem(uint entityId) => inner.EvaluateItem(entityId);

        public event Action<ItemFilterReloadedEventArgs>? FilterReloaded
        {
            add
            {
                if (value is null) return;
                Action<ItemFilterReloadedEventArgs> wrapped = args => loader.ExecuteFaultIsolated(modId, () => value(args));
                lock (_gate)
                {
                    if (_disposed) return;
                    _reloadSubs.Add((value, wrapped));
                }

                inner.FilterReloaded += wrapped;
            }
            remove
            {
                if (value is null) return;
                Action<ItemFilterReloadedEventArgs>? wrapped = null;
                lock (_gate)
                {
                    for (int i = _reloadSubs.Count - 1; i >= 0; i--)
                    {
                        if (_reloadSubs[i].Original.Equals(value))
                        {
                            wrapped = _reloadSubs[i].Wrapped;
                            _reloadSubs.RemoveAt(i);
                            break;
                        }
                    }
                }

                if (wrapped is not null)
                {
                    inner.FilterReloaded -= wrapped;
                }
            }
        }

        public void Dispose()
        {
            (Action<ItemFilterReloadedEventArgs> Original, Action<ItemFilterReloadedEventArgs> Wrapped)[] snapshot;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                snapshot = [.. _reloadSubs];
                _reloadSubs.Clear();
            }

            foreach (var item in snapshot)
            {
                inner.FilterReloaded -= item.Wrapped;
            }
        }
    }

    private sealed class TrackedCameraService(CameraService inner, string modId) : ICameraService, IDisposable
    {
        private bool _disposed;

        public CameraState GetState() => inner.GetState();

        public bool TryWorldToScreen(float worldX, float worldY, float worldZ, out float screenX, out float screenY) =>
            inner.TryWorldToScreen(worldX, worldY, worldZ, out screenX, out screenY);

        public bool TryWorldToUiPosition(float worldX, float worldY, float worldZ, out float uiX, out float uiY) =>
            inner.TryWorldToUiPosition(worldX, worldY, worldZ, out uiX, out uiY);

        public void SetZoomOverride(float? zoomOverride)
        {
            if (Volatile.Read(ref _disposed))
            {
                return;
            }

            inner.SetZoomOverrideForOwner(modId, zoomOverride);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, true))
            {
                return;
            }

            inner.ClearZoomOverrideForOwner(modId);
        }
    }

    private sealed class TrackedTooltipService(TooltipService inner, ClientModLoader loader, string modId, Action<IDisposable> track) : ITooltipService, IDisposable
    {
        private bool _disposed;

        public bool IsCustomTooltipVisible => inner.IsCustomTooltipVisible;
        public TooltipContent? ActiveCustomTooltip => inner.ActiveCustomTooltip;
        public (float X, float Y) CustomTooltipPosition => inner.CustomTooltipPosition;

        public IDisposable RegisterItemTooltipAugmenter(Func<ItemTooltipContext, IReadOnlyList<TooltipSection>> augmenter, int priority = 0)
        {
            ArgumentNullException.ThrowIfNull(augmenter);
            IDisposable sub = inner.RegisterItemTooltipAugmenter(ctx =>
            {
                IReadOnlyList<TooltipSection> sections = [];
                loader.ExecuteFaultIsolated(modId, () => sections = augmenter(ctx) ?? []);
                return sections;
            }, priority);
            track(sub);
            return sub;
        }

        public void ShowTooltip(TooltipContent content, float screenX, float screenY)
        {
            if (Volatile.Read(ref _disposed))
            {
                return;
            }

            inner.ShowTooltipForMod(modId, content, screenX, screenY);
        }

        public void HideTooltip() => inner.HideTooltipForMod(modId);

        public IDisposable AttachTooltip(IUiControl control, Func<TooltipContent?> contentProvider)
        {
            ArgumentNullException.ThrowIfNull(control);
            ArgumentNullException.ThrowIfNull(contentProvider);
            IDisposable sub = inner.AttachTooltipForMod(modId, control, () =>
            {
                TooltipContent? content = null;
                loader.ExecuteFaultIsolated(modId, () => content = contentProvider());
                return content;
            });
            track(sub);
            return sub;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, true))
            {
                return;
            }

            inner.HideTooltipForMod(modId);
        }
    }

    private sealed class TrackedStashAffinityService(StashAffinityService inner, ClientModLoader loader, string modId, Action<IDisposable> track) : IStashAffinityService
    {
        public IDisposable RegisterRoute(string id, Func<StashAffinityItemContext, StashAffinityRoute?> router, int priority = 0)
        {
            ArgumentNullException.ThrowIfNull(router);
            IDisposable sub = inner.RegisterRoute(id, ctx =>
            {
                StashAffinityRoute? route = null;
                loader.ExecuteFaultIsolated(modId, () => route = router(ctx));
                return route;
            }, priority);
            track(sub);
            return sub;
        }

        public IDisposable RegisterStockAffinityRule(string id, Func<StashAffinityItemContext, byte?> affinitySelector, int priority = 0)
        {
            ArgumentNullException.ThrowIfNull(affinitySelector);
            IDisposable sub = inner.RegisterStockAffinityRule(id, ctx =>
            {
                byte? bit = null;
                loader.ExecuteFaultIsolated(modId, () => bit = affinitySelector(ctx));
                return bit;
            }, priority);
            track(sub);
            return sub;
        }

        public IReadOnlyList<StashAffinityRoute> EvaluateRoutes(StashAffinityItemContext context) => inner.EvaluateRoutes(context);
        public uint EvaluateStockAffinityMask(StashAffinityItemContext context) => inner.EvaluateStockAffinityMask(context);
    }

    private sealed class TrackedStackExtensionService(StackExtensionService inner, string modId) : IStackExtensionService, IDisposable
    {
        private bool _disposed;

        public bool IsExtendedU32StacksEnabled => inner.IsExtendedU32StacksEnabled;
        public bool IsFormatMillionsEnabled => inner.IsFormatMillionsEnabled;

        public void SetExtendedU32StacksEnabled(bool enabled)
        {
            if (Volatile.Read(ref _disposed))
            {
                return;
            }

            inner.SetExtendedU32StacksEnabledForOwner(modId, enabled);
        }

        public void SetFormatMillionsEnabled(bool enabled)
        {
            if (Volatile.Read(ref _disposed))
            {
                return;
            }

            inner.SetFormatMillionsEnabledForOwner(modId, enabled);
        }

        public bool SetEntityStackOverride(uint entityId, uint stackSize)
        {
            if (Volatile.Read(ref _disposed))
            {
                return false;
            }

            return inner.SetEntityStackOverrideForOwner(modId, entityId, stackSize);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, true))
            {
                return;
            }

            inner.ClearForOwner(modId);
        }
    }
}
