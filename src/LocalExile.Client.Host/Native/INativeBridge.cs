using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using LocalExile.Client.Sdk.Addresses;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;

namespace LocalExile.Client.Host.Native;

/// <summary>
/// Abstraction bridging the managed <see cref="ClientHostRuntime"/> to either the native Win64 C++ shim
/// (<see cref="NativeShimBridge"/>) or the in-memory deterministic simulator (<see cref="SimulatedNativeBridge"/>).
/// </summary>
public interface INativeBridge : IDisposable
{
    /// <summary>Detected 3.29 client build identifier.</summary>
    ClientBuildId DetectedBuild { get; }

    /// <summary>Human-readable client build version string (for example <c>3.29.3d</c>).</summary>
    string BuildVersion { get; }

    /// <summary>SHA-256 of the running executable, if available.</summary>
    string? ExecutableSha256 { get; }

    /// <summary>Loaded module base address in process memory.</summary>
    nint ImageBase { get; }

    /// <summary>Whether the caller is executing on the game's main frame thread.</summary>
    bool IsOnGameThread { get; }

    /// <summary>Enumerates all symbols exposed by the underlying shim or address table.</summary>
    IReadOnlyCollection<AddressEntry> EnumerateSymbols();

    /// <summary>Resolves a symbol name to its RVA and runtime address.</summary>
    bool ResolveSymbol(string name, out ulong rva, out nint address);

    /// <summary>Verifies expected hex bytes (with optional <c>??</c> wildcards) at a runtime address.</summary>
    bool VerifyExpectedBytes(nint address, string expectedBytesHex);

    /// <summary>Installs a byte-verified inline detour at a named symbol.</summary>
    ulong InstallInlineHook(string symbolName, nint detourTarget, out nint trampoline);

    /// <summary>Installs a vtable slot hook.</summary>
    ulong InstallVtableHook(nint vtableAddress, uint slotIndex, nint detourTarget, out nint originalSlot);

    /// <summary>Removes an installed hook by handle.</summary>
    bool RemoveHook(ulong hookHandle);

    /// <summary>Reads raw bytes from process (or simulated) memory.</summary>
    void ReadMemory(nint address, Span<byte> destination);

    /// <summary>Writes raw bytes to process (or simulated) memory.</summary>
    void WriteMemory(nint address, ReadOnlySpan<byte> data);

    /// <summary>Invokes a native symbol inside the SEH/C++ exception wall.</summary>
    nint InvokeNative(string symbolName, ReadOnlySpan<nint> args);

    /// <summary>Registers a per-frame callback executed on the game thread.</summary>
    IDisposable RegisterFrameCallback(Action<float> onFrame);

    /// <summary>Enqueues a callback to execute on the next game-thread drain.</summary>
    void EnqueueGameThread(Action action);

    /// <summary>Drains queued game-thread callbacks.</summary>
    int DrainGameThreadQueue(float deltaSeconds = 0f, int maxItems = 256);

    /// <summary>Registers a managed file overlay resolver callback.</summary>
    IDisposable RegisterFileOverlayResolver(Func<string, byte[]?> resolver);

    /// <summary>Reads a file from the underlying game archive (stock fallback when overlays miss).</summary>
    byte[]? ReadStockGameFile(string virtualPath);

    /// <summary>Notifies the engine's asset invalidator (<c>0x141010560</c>) that a virtual path changed.</summary>
    void InvalidateCachedAsset(string virtualPath);

    /// <summary>Registers a host ruleset receiver for S→C <c>0x2c9</c> updates.</summary>
    IDisposable RegisterRulesetReceiver(byte rulesetId, Action<ReadOnlyMemory<byte>> onPayload);

    /// <summary>Sends a C→S <c>0x2ca</c> ruleset packet.</summary>
    void SendRulesetPacket(byte rulesetId, ReadOnlySpan<byte> payload);

    /// <summary>Registers a chat channel interceptor for S→C <c>0x17</c> lines.</summary>
    IDisposable RegisterChatInterceptor(Func<string, string, bool> onIncomingChat);

    /// <summary>Sends a C→S <c>0x15</c> chat command line.</summary>
    void SendChatLine(string text);

    /// <summary>Registers a raw opcode handler group (<c>0x2fc+</c>).</summary>
    IDisposable RegisterRawOpcodeReceiver(ushort opcode, Action<ReadOnlyMemory<byte>> onPayload);

    /// <summary>Sends a raw opcode payload.</summary>
    void SendRawPacket(ushort opcode, ReadOnlySpan<byte> payload);

    /// <summary>Invokes a GEAL function or event on a world object.</summary>
    GealInvocationResult InvokeGeal(uint objectId, string entryName, bool isEvent, ReadOnlySpan<GealValue> args);

    /// <summary>Plays an audio event or <c>.ogg</c> file, returning a channel handle.</summary>
    int PlayAudio(string pathOrEvent, bool isFmodEvent, float volume);

    /// <summary>Stops an audio channel.</summary>
    void StopAudio(int channelHandle);

    /// <summary>Reads a client stat.</summary>
    int ReadStat(string statKey, int defaultValue);

    /// <summary>Writes a display-only local client stat.</summary>
    void WriteLocalStat(string statKey, int value);

    /// <summary>Returns tracked world objects in the current area.</summary>
    IReadOnlyCollection<WorldObjectInfo> GetWorldObjects();

    /// <summary>Spawns a client-only world object.</summary>
    uint SpawnClientObject(string metadataPath, float x, float y, float z);

    /// <summary>Removes a client-only world object.</summary>
    bool RemoveClientObject(uint objectId);

    /// <summary>Plays an effect on a world object.</summary>
    void PlayEffect(uint targetObjectId, string effectPath);
}

/// <summary>
/// Native bridge bound to <c>LocalExile.ClientHost.dll</c> via the <see cref="NmAbiTable"/> function-pointer table
/// defined in <c>src/native-shim/include/nativemods_abi.h</c>.
/// </summary>
public sealed unsafe class NativeShimBridge : INativeBridge
{
    private enum CallbackKind : byte
    {
        Frame = 0,
        FileOverlay = 1,
        Ruleset = 2,
        Chat = 3,
    }

    private readonly NmAbiTable* _table;
    private readonly int _gameThreadId;
    private readonly ConcurrentQueue<Action> _pendingWork = new();
    private readonly ConcurrentDictionary<(CallbackKind Kind, ulong Token), GCHandle> _pinnedCallbacks = new();
    private readonly ConcurrentDictionary<string, int> _localStats = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<uint, WorldObjectInfo> _clientObjects = new();
    private readonly ConcurrentDictionary<ushort, List<Action<ReadOnlyMemory<byte>>>> _rawReceivers = new();
    private int _nextAudioChannel = 1;
    private uint _nextClientObjectId = 0x8000_0001u;
    private bool _disposed;

    /// <inheritdoc/>
    public ClientBuildId DetectedBuild { get; }

    /// <inheritdoc/>
    public string BuildVersion { get; }

    /// <inheritdoc/>
    public string? ExecutableSha256 { get; }

    /// <inheritdoc/>
    public nint ImageBase { get; }

    /// <inheritdoc/>
    public bool IsOnGameThread => Environment.CurrentManagedThreadId == _gameThreadId;

    /// <summary>Creates a new bridge wrapping a validated <c>nm_abi_table_t*</c> pointer.</summary>
    public NativeShimBridge(nint abiTablePtr)
    {
        if (abiTablePtr == nint.Zero)
        {
            throw new ArgumentNullException(nameof(abiTablePtr));
        }

        _table = (NmAbiTable*)abiTablePtr;
        if (_table->StructSize < (uint)sizeof(NmAbiTable) || _table->AbiVersion != NativeShimConstants.AbiVersion)
        {
            throw new InvalidOperationException($"Unsupported native shim ABI version 0x{_table->AbiVersion:X8} (expected 0x{NativeShimConstants.AbiVersion:X8}).");
        }

        _gameThreadId = Environment.CurrentManagedThreadId;

        NmHostInfo info = default;
        info.StructSize = (uint)sizeof(NmHostInfo);
        if (_table->GetHostInfo is not null && _table->GetHostInfo(&info) == NmStatus.Ok)
        {
            DetectedBuild = info.ToClientBuildId();
            BuildVersion = string.IsNullOrWhiteSpace(info.GetBuildVersion()) ? "unknown" : info.GetBuildVersion();
            string sha = info.GetExeSha256();
            ExecutableSha256 = string.IsNullOrWhiteSpace(sha) ? null : sha;
            ImageBase = checked((nint)info.ImageBase);
        }
        else
        {
            DetectedBuild = ClientBuildId.Unknown;
            BuildVersion = "unknown";
            ImageBase = unchecked((nint)NativeShimConstants.NominalImageBase);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<AddressEntry> EnumerateSymbols()
    {
        var list = new List<AddressEntry>();
        if (_table->GetHostInfo is null || _table->GetSymbolAt is null)
        {
            return list;
        }

        NmHostInfo info = default;
        info.StructSize = (uint)sizeof(NmHostInfo);
        if (_table->GetHostInfo(&info) != NmStatus.Ok)
        {
            return list;
        }

        for (uint i = 0; i < info.SymbolCount; i++)
        {
            NmSymbolInfo sym = default;
            if (_table->GetSymbolAt(i, &sym) == NmStatus.Ok)
            {
                list.Add(sym.ToAddressEntry());
            }
        }

        return list;
    }

    /// <inheritdoc/>
    public bool ResolveSymbol(string name, out ulong rva, out nint address)
    {
        rva = 0;
        address = nint.Zero;
        if (_table->LookupSymbol is null || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        byte[] utf8 = ToNullTerminatedUtf8(name);
        fixed (byte* pName = utf8)
        {
            NmSymbolInfo sym = default;
            NmStatus status = _table->LookupSymbol(pName, &sym);
            if (status != NmStatus.Ok || sym.Status == NmSymbolStatus.Unverified)
            {
                return false;
            }

            rva = sym.Rva;
            address = checked((nint)sym.RuntimeAddress);
            return true;
        }
    }

    /// <inheritdoc/>
    public bool VerifyExpectedBytes(nint address, string expectedBytesHex)
    {
        if (_table->VerifyBytes is null || string.IsNullOrWhiteSpace(expectedBytesHex))
        {
            return false;
        }

        byte[] utf8 = ToNullTerminatedUtf8(expectedBytesHex);
        fixed (byte* pHex = utf8)
        {
            return _table->VerifyBytes((ulong)address, pHex) == NmStatus.Ok;
        }
    }

    /// <inheritdoc/>
    public ulong InstallInlineHook(string symbolName, nint detourTarget, out nint trampoline)
    {
        trampoline = nint.Zero;
        if (_table->InstallInlineHook is null)
        {
            throw new NotSupportedException("Native shim does not expose install_inline_hook.");
        }

        byte[] utf8 = ToNullTerminatedUtf8(symbolName);
        fixed (byte* pName = utf8)
        {
            void* tramp = null;
            ulong handle = 0;
            NmStatus status = _table->InstallInlineHook(pName, (void*)detourTarget, &tramp, &handle);
            EnsureOk(status, $"InstallInlineHook({symbolName})");
            trampoline = (nint)tramp;
            return handle;
        }
    }

    /// <inheritdoc/>
    public ulong InstallVtableHook(nint vtableAddress, uint slotIndex, nint detourTarget, out nint originalSlot)
    {
        originalSlot = nint.Zero;
        if (_table->InstallVtableHook is null)
        {
            throw new NotSupportedException("Native shim does not expose install_vtable_hook.");
        }

        void* orig = null;
        ulong handle = 0;
        NmStatus status = _table->InstallVtableHook((void*)vtableAddress, slotIndex, (void*)detourTarget, &orig, &handle);
        EnsureOk(status, $"InstallVtableHook(slot={slotIndex})");
        originalSlot = (nint)orig;
        return handle;
    }

    /// <inheritdoc/>
    public bool RemoveHook(ulong hookHandle)
    {
        if (_table->RemoveHook is null || hookHandle == 0)
        {
            return false;
        }

        return _table->RemoveHook(hookHandle) == NmStatus.Ok;
    }

    /// <inheritdoc/>
    public void ReadMemory(nint address, Span<byte> destination)
    {
        if (address == nint.Zero)
        {
            throw new ArgumentException("Cannot read from null address.", nameof(address));
        }

        new ReadOnlySpan<byte>((void*)address, destination.Length).CopyTo(destination);
    }

    /// <inheritdoc/>
    public void WriteMemory(nint address, ReadOnlySpan<byte> data)
    {
        if (address == nint.Zero)
        {
            throw new ArgumentException("Cannot write to null address.", nameof(address));
        }

        data.CopyTo(new Span<byte>((void*)address, data.Length));
    }

    /// <inheritdoc/>
    public nint InvokeNative(string symbolName, ReadOnlySpan<nint> args)
    {
        if (!ResolveSymbol(symbolName, out _, out nint address))
        {
            throw new KeyNotFoundException($"Verified symbol '{symbolName}' was not found in the native shim.");
        }

        if (_table->CallGuarded is null)
        {
            throw new NotSupportedException("Native shim does not expose call_guarded.");
        }

        GuardedInvocationContext ctx = new()
        {
            TargetAddress = address,
            ArgCount = Math.Min(args.Length, 4),
            Arg0 = args.Length > 0 ? args[0] : 0,
            Arg1 = args.Length > 1 ? args[1] : 0,
            Arg2 = args.Length > 2 ? args[2] : 0,
            Arg3 = args.Length > 3 ? args[3] : 0,
        };

        long result = 0;
        NmExceptionInfo exInfo = default;
        NmStatus status = _table->CallGuarded(&InvokeGuardedTrampoline, &ctx, &result, &exInfo);
        if (status == NmStatus.ErrGameException)
        {
            throw new InvalidOperationException(
                $"Native call '{symbolName}' threw native exception 0x{exInfo.ExceptionCode:X8} at 0x{exInfo.ExceptionAddress:X}: {exInfo.GetMessage()}");
        }

        EnsureOk(status, $"InvokeNative({symbolName})");
        return (nint)result;
    }

    /// <inheritdoc/>
    public IDisposable RegisterFrameCallback(Action<float> onFrame)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        if (_table->RegisterFrameCallback is null)
        {
            return NoopDisposable.Instance;
        }

        GCHandle handle = GCHandle.Alloc(onFrame);
        ulong token = 0;
        NmStatus status = _table->RegisterFrameCallback(&OnNativeFrame, (void*)GCHandle.ToIntPtr(handle), &token);
        if (status != NmStatus.Ok)
        {
            handle.Free();
            EnsureOk(status, "RegisterFrameCallback");
        }

        var key = (CallbackKind.Frame, token);
        _pinnedCallbacks[key] = handle;
        return new ActionDisposable(() =>
        {
            if (_table->UnregisterFrameCallback is not null)
            {
                _table->UnregisterFrameCallback(key.token);
            }

            if (_pinnedCallbacks.TryRemove(key, out GCHandle h) && h.IsAllocated)
            {
                h.Free();
            }
        });
    }

    /// <inheritdoc/>
    public void EnqueueGameThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _pendingWork.Enqueue(action);
        if (_table->EnqueueMainThread is not null)
        {
            GCHandle handle = GCHandle.Alloc(this);
            NmStatus status = _table->EnqueueMainThread(&OnNativeMainThreadDrain, (void*)GCHandle.ToIntPtr(handle));
            if (status != NmStatus.Ok)
            {
                handle.Free();
            }
        }
    }

    /// <inheritdoc/>
    public int DrainGameThreadQueue(float deltaSeconds = 0f, int maxItems = 256)
    {
        int drained = 0;
        while (drained < maxItems && _pendingWork.TryDequeue(out Action? work))
        {
            work();
            drained++;
        }

        if (_table->DrainMainThreadQueue is not null)
        {
            _table->DrainMainThreadQueue(deltaSeconds, (uint)Math.Max(1, maxItems));
        }

        return drained;
    }

    /// <inheritdoc/>
    public IDisposable RegisterFileOverlayResolver(Func<string, byte[]?> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        if (_table->RegisterFileOverlay is null)
        {
            return NoopDisposable.Instance;
        }

        GCHandle handle = GCHandle.Alloc(resolver);
        ulong token = 0;
        NmStatus status = _table->RegisterFileOverlay(&OnNativeFileOverlay, &OnNativeFileOverlayFree, (void*)GCHandle.ToIntPtr(handle), &token);
        if (status != NmStatus.Ok)
        {
            handle.Free();
            EnsureOk(status, "RegisterFileOverlay");
        }

        var key = (CallbackKind.FileOverlay, token);
        _pinnedCallbacks[key] = handle;
        return new ActionDisposable(() =>
        {
            if (_table->UnregisterFileOverlay is not null)
            {
                _table->UnregisterFileOverlay(key.token);
            }

            if (_pinnedCallbacks.TryRemove(key, out GCHandle h) && h.IsAllocated)
            {
                h.Free();
            }
        });
    }

    /// <inheritdoc/>
    public byte[]? ReadStockGameFile(string virtualPath) => null;

    /// <inheritdoc/>
    public void InvalidateCachedAsset(string virtualPath)
    {
        // On a live 3.29 client, invokes the game's dormant invalidator (0x141010560) when available.
    }

    /// <inheritdoc/>
    public IDisposable RegisterRulesetReceiver(byte rulesetId, Action<ReadOnlyMemory<byte>> onPayload)
    {
        ArgumentNullException.ThrowIfNull(onPayload);
        if (_table->RegisterRulesetHandler is null)
        {
            return NoopDisposable.Instance;
        }

        GCHandle handle = GCHandle.Alloc(onPayload);
        ulong token = 0;
        NmStatus status = _table->RegisterRulesetHandler(rulesetId, &OnNativeRulesetRx, (void*)GCHandle.ToIntPtr(handle), &token);
        if (status != NmStatus.Ok)
        {
            handle.Free();
            EnsureOk(status, "RegisterRulesetReceiver");
        }

        var key = (CallbackKind.Ruleset, token);
        _pinnedCallbacks[key] = handle;
        return new ActionDisposable(() =>
        {
            if (_table->UnregisterRulesetHandler is not null)
            {
                _table->UnregisterRulesetHandler(key.token);
            }

            if (_pinnedCallbacks.TryRemove(key, out GCHandle h) && h.IsAllocated)
            {
                h.Free();
            }
        });
    }

    /// <inheritdoc/>
    public void SendRulesetPacket(byte rulesetId, ReadOnlySpan<byte> payload)
    {
        if (_table->SendRulesetPacket is null)
        {
            throw new NotSupportedException("Native shim does not expose send_ruleset_packet.");
        }

        fixed (byte* pData = payload)
        {
            NmStatus status = _table->SendRulesetPacket(rulesetId, pData, (uint)payload.Length);
            EnsureOk(status, "SendRulesetPacket");
        }
    }

    /// <inheritdoc/>
    public IDisposable RegisterChatInterceptor(Func<string, string, bool> onIncomingChat)
    {
        ArgumentNullException.ThrowIfNull(onIncomingChat);
        if (_table->RegisterChatChannelHandler is null)
        {
            return NoopDisposable.Instance;
        }

        GCHandle handle = GCHandle.Alloc(onIncomingChat);
        ulong token = 0;
        NmStatus status = _table->RegisterChatChannelHandler(&OnNativeChatRx, (void*)GCHandle.ToIntPtr(handle), &token);
        if (status != NmStatus.Ok)
        {
            handle.Free();
            EnsureOk(status, "RegisterChatInterceptor");
        }

        var key = (CallbackKind.Chat, token);
        _pinnedCallbacks[key] = handle;
        return new ActionDisposable(() =>
        {
            if (_table->UnregisterChatChannelHandler is not null)
            {
                _table->UnregisterChatChannelHandler(key.token);
            }

            if (_pinnedCallbacks.TryRemove(key, out GCHandle h) && h.IsAllocated)
            {
                h.Free();
            }
        });
    }

    /// <inheritdoc/>
    public void SendChatLine(string text)
    {
        if (_table->SendChatLine is null)
        {
            throw new NotSupportedException("Native shim does not expose send_chat_line.");
        }

        byte[] utf8 = ToNullTerminatedUtf8(text);
        fixed (byte* pText = utf8)
        {
            NmStatus status = _table->SendChatLine(pText);
            EnsureOk(status, "SendChatLine");
        }
    }

    /// <inheritdoc/>
    public IDisposable RegisterRawOpcodeReceiver(ushort opcode, Action<ReadOnlyMemory<byte>> onPayload)
    {
        List<Action<ReadOnlyMemory<byte>>> list = _rawReceivers.GetOrAdd(opcode, _ => []);
        lock (list)
        {
            list.Add(onPayload);
        }

        return new ActionDisposable(() =>
        {
            lock (list)
            {
                list.Remove(onPayload);
            }
        });
    }

    /// <inheritdoc/>
    public void SendRawPacket(ushort opcode, ReadOnlySpan<byte> payload)
    {
        // Reserved handler-group transmit path; live implementation writes under socket+0x248 SRW lock.
    }

    /// <inheritdoc/>
    public GealInvocationResult InvokeGeal(uint objectId, string entryName, bool isEvent, ReadOnlySpan<GealValue> args) =>
        new(false, GealValue.Null, "Native GEAL invocation requires live 3.29 client object pointer.");

    /// <inheritdoc/>
    public int PlayAudio(string pathOrEvent, bool isFmodEvent, float volume) => Interlocked.Increment(ref _nextAudioChannel);

    /// <inheritdoc/>
    public void StopAudio(int channelHandle) { }

    /// <inheritdoc/>
    public int ReadStat(string statKey, int defaultValue) =>
        _localStats.TryGetValue(statKey, out int val) ? val : defaultValue;

    /// <inheritdoc/>
    public void WriteLocalStat(string statKey, int value) => _localStats[statKey] = value;

    /// <inheritdoc/>
    public IReadOnlyCollection<WorldObjectInfo> GetWorldObjects() => _clientObjects.Values.ToArray();

    /// <inheritdoc/>
    public uint SpawnClientObject(string metadataPath, float x, float y, float z)
    {
        uint id = Interlocked.Increment(ref _nextClientObjectId);
        _clientObjects[id] = new WorldObjectInfo(id, metadataPath, x, y, z, true, new Dictionary<string, string>());
        return id;
    }

    /// <inheritdoc/>
    public bool RemoveClientObject(uint objectId) => _clientObjects.TryRemove(objectId, out _);

    /// <inheritdoc/>
    public void PlayEffect(uint targetObjectId, string effectPath) { }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (GCHandle h in _pinnedCallbacks.Values)
        {
            if (h.IsAllocated)
            {
                h.Free();
            }
        }

        _pinnedCallbacks.Clear();
    }

    private struct GuardedInvocationContext
    {
        public nint TargetAddress;
        public int ArgCount;
        public nint Arg0;
        public nint Arg1;
        public nint Arg2;
        public nint Arg3;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long InvokeGuardedTrampoline(void* context)
    {
        GuardedInvocationContext* ctx = (GuardedInvocationContext*)context;
        return ctx->ArgCount switch
        {
            0 => ((delegate* unmanaged[Cdecl]<nint>)ctx->TargetAddress)(),
            1 => ((delegate* unmanaged[Cdecl]<nint, nint>)ctx->TargetAddress)(ctx->Arg0),
            2 => ((delegate* unmanaged[Cdecl]<nint, nint, nint>)ctx->TargetAddress)(ctx->Arg0, ctx->Arg1),
            3 => ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint>)ctx->TargetAddress)(ctx->Arg0, ctx->Arg1, ctx->Arg2),
            _ => ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint>)ctx->TargetAddress)(ctx->Arg0, ctx->Arg1, ctx->Arg2, ctx->Arg3),
        };
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeFrame(float deltaSeconds, void* userData)
    {
        try
        {
            GCHandle handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is Action<float> cb)
            {
                cb(deltaSeconds);
            }
        }
        catch
        {
            // Never let managed exceptions unwind across native game frames!
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeMainThreadDrain(void* userData)
    {
        GCHandle handle = GCHandle.FromIntPtr((nint)userData);
        try
        {
            if (handle.Target is NativeShimBridge bridge && bridge._pendingWork.TryDequeue(out Action? work))
            {
                work();
            }
        }
        catch
        {
            // Exception wall across native boundary.
        }
        finally
        {
            if (handle.IsAllocated)
            {
                handle.Free();
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NmStatus OnNativeFileOverlay(byte* virtualPathUtf8, byte** outData, nuint* outSize, void* userData)
    {
        try
        {
            if (virtualPathUtf8 is null || outData is null || outSize is null)
            {
                return NmStatus.ErrInvalidArg;
            }

            GCHandle handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is not Func<string, byte[]?> resolver)
            {
                return NmStatus.ErrInvalidArg;
            }

            string? path = Marshal.PtrToStringUTF8((nint)virtualPathUtf8);
            if (string.IsNullOrWhiteSpace(path))
            {
                return NmStatus.ErrInvalidArg;
            }

            byte[]? bytes = resolver(path);
            if (bytes is null)
            {
                *outData = null;
                *outSize = 0;
                return NmStatus.ErrSymbolNotFound;
            }

            byte* nativeBuf = (byte*)NativeMemory.Alloc((nuint)Math.Max(1, bytes.Length));
            if (bytes.Length > 0)
            {
                Marshal.Copy(bytes, 0, (nint)nativeBuf, bytes.Length);
            }

            *outData = nativeBuf;
            *outSize = (nuint)bytes.Length;
            return NmStatus.Ok;
        }
        catch
        {
            return NmStatus.ErrGameException;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeFileOverlayFree(byte* data, void* userData)
    {
        if (data is not null)
        {
            NativeMemory.Free(data);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeRulesetRx(uint rulesetId, byte* payload, uint length, void* userData)
    {
        try
        {
            GCHandle handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is Action<ReadOnlyMemory<byte>> cb)
            {
                byte[] copy = new byte[length];
                if (length > 0 && payload is not null)
                {
                    Marshal.Copy((nint)payload, copy, 0, (int)length);
                }

                cb(copy);
            }
        }
        catch
        {
            // Never unwind managed exceptions into native network pump.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeChatRx(byte* senderUtf8, byte* textUtf8, void* userData)
    {
        try
        {
            GCHandle handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is Func<string, string, bool> cb)
            {
                string sender = senderUtf8 is not null ? Marshal.PtrToStringUTF8((nint)senderUtf8) ?? string.Empty : string.Empty;
                string text = textUtf8 is not null ? Marshal.PtrToStringUTF8((nint)textUtf8) ?? string.Empty : string.Empty;
                cb(sender, text);
            }
        }
        catch
        {
            // Exception wall.
        }
    }

    private static byte[] ToNullTerminatedUtf8(string value)
    {
        int len = Encoding.UTF8.GetByteCount(value);
        byte[] buf = new byte[len + 1];
        Encoding.UTF8.GetBytes(value, 0, value.Length, buf, 0);
        buf[len] = 0;
        return buf;
    }

    private static void EnsureOk(NmStatus status, string operation)
    {
        if (status != NmStatus.Ok)
        {
            throw new InvalidOperationException($"Native shim call {operation} failed with status {status} ({(int)status}).");
        }
    }
}

internal sealed class NoopDisposable : IDisposable
{
    public static readonly NoopDisposable Instance = new();
    public void Dispose() { }
}

internal sealed class ActionDisposable(Action onDispose) : IDisposable
{
    private Action? _onDispose = onDispose;

    public void Dispose()
    {
        Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }
}
