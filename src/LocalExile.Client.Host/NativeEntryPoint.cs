using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using LocalExile.Client.Host.Addresses;
using LocalExile.Client.Host.Loading;
using LocalExile.Client.Host.Native;

namespace LocalExile.Client.Host;

/// <summary>
/// Managed entry point invoked by the native shim's CoreCLR bootstrapper (<c>nm_bootstrap_managed_host</c>).
/// Matches the contract documented in <c>nativemods_abi.h</c> and <c>coreclr_bootstrap.cpp</c>:
/// <c>int LocalExile.Client.Host.NativeEntryPoint.Initialize(IntPtr abiTablePtr)</c>.
/// </summary>
public static class NativeEntryPoint
{
    private static readonly Lock Gate = new();
    private static readonly Lock LogGate = new();

    /// <summary>Gets the active managed runtime booted via <see cref="Initialize"/>, if any.</summary>
    public static ClientHostRuntime? ActiveRuntime { get; private set; }

    /// <summary>
    /// Native bootstrap entry point invoked by <c>LocalExile.ClientHost.dll</c>.
    /// Configures file logging, discovers the matching 3.29 JSON address library, boots
    /// <see cref="ClientHostRuntime"/>, and auto-loads staged client mods.
    /// </summary>
    /// <param name="abiTablePtr">Pointer to a valid <c>nm_abi_table_t</c> structure.</param>
    /// <returns><c>0</c> (<see cref="NmStatus.Ok"/>) on success, or a negative <see cref="NmStatus"/> error code.</returns>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int Initialize(nint abiTablePtr)
    {
        return InitializeForNativeHost(abiTablePtr);
    }

    /// <summary>
    /// Native-callable diagnostic query returning active managed host counts (loaded mods,
    /// processed frame ticks, and resolved address-library symbols).
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int GetDiagnostics(int* outLoadedMods, long* outFrameCount, int* outAddressSymbols)
    {
        lock (Gate)
        {
            if (ActiveRuntime is null)
            {
                return (int)NmStatus.ErrNotInitialized;
            }

            if (outLoadedMods is not null)
            {
                *outLoadedMods = ActiveRuntime.Loader.LoadedMods.Count;
            }

            if (outFrameCount is not null)
            {
                *outFrameCount = ActiveRuntime.FrameCount;
            }

            if (outAddressSymbols is not null)
            {
                *outAddressSymbols = ActiveRuntime.Addresses.Entries.Count;
            }

            return (int)NmStatus.Ok;
        }
    }

    /// <summary>Native-callable shutdown helper mirroring <see cref="Shutdown"/>.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int ShutdownNative()
    {
        Shutdown();
        return (int)NmStatus.Ok;
    }

    /// <summary>
    /// Managed-callable entry point mirroring <see cref="Initialize"/> (used for testing and direct host activation).
    /// </summary>
    public static int InitializeManaged(nint abiTablePtr, ClientHostOptions? options = null)
    {
        if (abiTablePtr == nint.Zero)
        {
            return (int)NmStatus.ErrInvalidArg;
        }

        try
        {
            unsafe
            {
                NmAbiTable* table = (NmAbiTable*)abiTablePtr;
                if (table->StructSize < (uint)sizeof(NmAbiTable) || table->AbiVersion != NativeShimConstants.AbiVersion)
                {
                    return (int)NmStatus.ErrInvalidArg;
                }
            }

            lock (Gate)
            {
                ActiveRuntime?.Dispose();
                var bridge = new NativeShimBridge(abiTablePtr);
                var runtime = new ClientHostRuntime(bridge, options);
                runtime.Start();
                ActiveRuntime = runtime;
                return (int)NmStatus.Ok;
            }
        }
        catch
        {
            return (int)NmStatus.ErrRuntimeBootFailed;
        }
    }

    /// <summary>Shuts down the active managed host runtime if one was booted.</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            ActiveRuntime?.Dispose();
            ActiveRuntime = null;
        }
    }

    private static int InitializeForNativeHost(nint abiTablePtr)
    {
        string hostDir = ResolveHostDirectory();
        string logPath = ResolveLogPath(hostDir);
        bool logToConsole = string.Equals(Environment.GetEnvironmentVariable("NATIVEMODS_LOG_CONSOLE"), "1", StringComparison.Ordinal);

        void WriteLog(string level, string source, string message)
        {
            string line = string.Format(
                CultureInfo.InvariantCulture,
                "[MANAGED] [{0}] [{1}] {2}{3}",
                level,
                source,
                message,
                Environment.NewLine);

            lock (LogGate)
            {
                try
                {
                    using var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    byte[] utf8 = Encoding.UTF8.GetBytes(line);
                    stream.Write(utf8, 0, utf8.Length);
                    stream.Flush();
                }
                catch
                {
                    // Logging must never break native host bootstrap.
                }

                if (logToConsole)
                {
                    Console.Write(line);
                }
            }
        }

        if (abiTablePtr == nint.Zero)
        {
            WriteLog("ERROR", "bootstrap", "NativeEntryPoint.Initialize received null abiTablePtr.");
            return (int)NmStatus.ErrInvalidArg;
        }

        try
        {
            unsafe
            {
                NmAbiTable* table = (NmAbiTable*)abiTablePtr;
                if (table->StructSize < (uint)sizeof(NmAbiTable) || table->AbiVersion != NativeShimConstants.AbiVersion)
                {
                    WriteLog("ERROR", "bootstrap", $"ABI mismatch: structSize={table->StructSize}, abiVersion=0x{table->AbiVersion:X8}.");
                    return (int)NmStatus.ErrInvalidArg;
                }
            }

            lock (Gate)
            {
                ActiveRuntime?.Dispose();
                var bridge = new NativeShimBridge(abiTablePtr);
                WriteLog("INFO", "bootstrap", $"NativeShimBridge attached (build={bridge.BuildVersion}, imageBase=0x{(ulong)bridge.ImageBase:X}, sha256={bridge.ExecutableSha256 ?? "<none>"}).");

                JsonAddressLibrary? addressLib = TryDiscoverAddressLibrary(hostDir, bridge, WriteLog);
                var options = new ClientHostOptions
                {
                    AddressLibrary = addressLib,
                    LogSink = WriteLog,
                };

                var runtime = new ClientHostRuntime(bridge, options);
                runtime.Start();
                ActiveRuntime = runtime;

                bool enqueued = bridge.EnqueueGameThread(() =>
                {
                    lock (Gate)
                    {
                        if (!ReferenceEquals(ActiveRuntime, runtime) || runtime.IsDisposed)
                        {
                            return;
                        }
                    }

                    AutoLoadClientMods(hostDir, runtime, WriteLog);
                });
                if (!enqueued)
                {
                    WriteLog("ERROR", "loader", "Failed to enqueue client mod auto-load onto game thread queue.");
                }

                return (int)NmStatus.Ok;
            }
        }
        catch (Exception ex)
        {
            WriteLog("ERROR", "bootstrap", $"NativeEntryPoint.Initialize failed: {ex.GetType().Name}: {ex.Message}");
            return (int)NmStatus.ErrRuntimeBootFailed;
        }
    }

    private static string ResolveHostDirectory()
    {
        string? location = typeof(NativeEntryPoint).Assembly.Location;
        if (!string.IsNullOrWhiteSpace(location))
        {
            string? dir = Path.GetDirectoryName(location);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                return dir;
            }
        }

        return AppContext.BaseDirectory;
    }

    private static string ResolveLogPath(string hostDir)
    {
        string? managedEnv = Environment.GetEnvironmentVariable("NATIVEMODS_MANAGED_LOG_PATH");
        if (!string.IsNullOrWhiteSpace(managedEnv))
        {
            return managedEnv;
        }

        string? sharedEnv = Environment.GetEnvironmentVariable("NATIVEMODS_LOG_PATH");
        if (!string.IsNullOrWhiteSpace(sharedEnv))
        {
            return sharedEnv;
        }

        return Path.Combine(hostDir, "nativemods-shim.log");
    }

    private static JsonAddressLibrary? TryDiscoverAddressLibrary(
        string hostDir,
        NativeShimBridge bridge,
        Action<string, string, string> log)
    {
        List<string> candidates = [];
        string? envDir = Environment.GetEnvironmentVariable("NATIVEMODS_ADDRESSES_DIR");
        if (!string.IsNullOrWhiteSpace(envDir))
        {
            candidates.Add(envDir);
        }

        candidates.Add(Path.Combine(hostDir, "addresses"));
        candidates.Add(Path.Combine(hostDir, "data", "addresses"));

        foreach (string dir in candidates)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(bridge.ExecutableSha256))
            {
                JsonAddressLibrary? bySha = JsonAddressLibrary.TryLoadForExecutableSha256(dir, bridge.ExecutableSha256, bridge.ImageBase);
                if (bySha is not null)
                {
                    log("INFO", "addresses", $"Loaded JsonAddressLibrary by SHA-256 from '{dir}' (build={bySha.BuildVersion}, symbols={bySha.Entries.Count}, offsets={bySha.Offsets.Count}).");
                    return bySha;
                }
            }

            string versionFile = Path.Combine(dir, $"{bridge.BuildVersion}.json");
            if (!File.Exists(versionFile))
            {
                versionFile = Path.Combine(dir, "3.29.3d.json");
            }

            if (File.Exists(versionFile))
            {
                try
                {
                    JsonAddressLibrary loaded = JsonAddressLibrary.LoadFromFile(versionFile, bridge.ImageBase, bridge.ExecutableSha256);
                    log("INFO", "addresses", $"Loaded JsonAddressLibrary from '{versionFile}' (build={loaded.BuildVersion}, symbols={loaded.Entries.Count}, offsets={loaded.Offsets.Count}).");
                    return loaded;
                }
                catch (Exception ex)
                {
                    log("WARN", "addresses", $"Failed to parse '{versionFile}': {ex.Message}");
                }
            }
        }

        log("INFO", "addresses", "No external JSON address library found; using embedded shim symbol table.");
        return null;
    }

    private static void AutoLoadClientMods(
        string hostDir,
        ClientHostRuntime runtime,
        Action<string, string, string> log)
    {
        string? modsDir = Environment.GetEnvironmentVariable("NATIVEMODS_MODS_DIR");
        if (string.IsNullOrWhiteSpace(modsDir))
        {
            modsDir = Path.Combine(hostDir, "Mods", "Client");
        }

        if (!Directory.Exists(modsDir))
        {
            log("INFO", "loader", $"Mods directory '{modsDir}' not found; skipping client mod auto-load.");
            return;
        }

        IReadOnlyList<ModLoadDiagnostic> results = runtime.Loader.LoadModsFromDirectory(modsDir);
        int loadedCount = 0;
        foreach (ModLoadDiagnostic diag in results)
        {
            if (diag.Status == ModLoadStatus.Loaded && diag.LoadedMod is { } mod)
            {
                loadedCount++;
                log("INFO", "loader", $"Loaded client mod '{mod.Manifest.Id}' v{mod.Manifest.Version} (gen={mod.Generation}, assembly='{Path.GetFileName(mod.AssemblyPath ?? "<data-only>")}', collectibleAlc={mod.LoadContext?.IsCollectible ?? false}).");
            }
            else if (diag.Status == ModLoadStatus.Disabled)
            {
                log("INFO", "loader", $"Skipped disabled client mod '{diag.ModId}' in '{diag.ModDirectory}'.");
            }
            else
            {
                log("ERROR", "loader", $"Failed to load client mod '{diag.ModId}' from '{diag.ModDirectory}' [{diag.Status}]: {diag.Message}");
            }
        }

        log("INFO", "loader", $"Client mod auto-load complete: {loadedCount} mod(s) loaded from '{modsDir}'.");
    }
}
