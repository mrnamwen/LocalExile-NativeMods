using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using LocalExile.Client.Sdk.Addresses;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.Sdk.Raw;

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

    /// <summary>Installs a pre/post inline hook chain callback on a named verified symbol.</summary>
    ulong InstallInlineHookChain(string symbolName, HookPhase phase, int priority, Action<HookContext> callback) =>
        throw new NotSupportedException("Hook chains are not supported by this bridge.");

    /// <summary>Installs a pre/post inline hook chain callback at an explicit address.</summary>
    ulong InstallInlineHookChainAt(nint address, string? expectedBytesHex, HookPhase phase, int priority, Action<HookContext> callback) =>
        throw new NotSupportedException("Hook chains are not supported by this bridge.");

    /// <summary>Installs a pre/post vtable hook chain callback on a vtable slot.</summary>
    ulong InstallVtableHookChain(nint vtableAddress, uint slotIndex, HookPhase phase, int priority, Action<HookContext> callback) =>
        throw new NotSupportedException("Hook chains are not supported by this bridge.");

    /// <summary>Removes an installed hook by handle.</summary>
    bool RemoveHook(ulong hookHandle);

    /// <summary>Reads raw bytes from process (or simulated) memory.</summary>
    void ReadMemory(nint address, Span<byte> destination);

    /// <summary>Writes raw bytes to process (or simulated) memory.</summary>
    void WriteMemory(nint address, ReadOnlySpan<byte> data);

    /// <summary>Invokes a native symbol inside the SEH/C++ exception wall.</summary>
    nint InvokeNative(string symbolName, ReadOnlySpan<nint> args);

    /// <summary>Invokes a native address with up to 8 mixed integer/float arguments inside the SEH/C++ exception wall.</summary>
    NativeCallResult InvokeNativeEx(nint targetAddress, ReadOnlySpan<NativeCallArgument> args, bool returnIsFloat = false) =>
        throw new NotSupportedException("Extended native calls are not supported by this bridge.");

    /// <summary>Registers a per-frame callback executed on the game thread.</summary>
    IDisposable RegisterFrameCallback(Action<float> onFrame);

    /// <summary>Enqueues a callback to execute on the next game-thread drain. Returns <c>false</c> when the queue is full or disposed.</summary>
    bool EnqueueGameThread(Action action);

    /// <summary>Drains queued game-thread callbacks.</summary>
    int DrainGameThreadQueue(float deltaSeconds = 0f, int maxItems = 256);

    /// <summary>Registers a managed file overlay resolver callback.</summary>
    IDisposable RegisterFileOverlayResolver(Func<string, byte[]?> resolver);

    /// <summary>
    /// Updates the native shim's lock-free O(1) pre-filter with the exact normalized virtual paths
    /// currently served by the managed file overlay resolver so stock game files bypass P/Invoke.
    /// </summary>
    void SetFileOverlayKnownPaths(IReadOnlyCollection<string> normalizedPaths) { }

    /// <summary>Whether the bridge exposes a live instance-server connection token for reconnect/zone-change detection.</summary>
    bool SupportsConnectionToken => false;

    /// <summary>
    /// Returns a non-zero connection token identifying the current live instance-server TCP socket
    /// (<c>0</c> when disconnected / login / character select). Changes on every zone transition.
    /// </summary>
    ulong GetActiveConnectionToken() => 0;

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

    /// <summary>Registers an inclusive raw opcode handler group (<c>0x2fc+</c>).</summary>
    IDisposable RegisterRawOpcodeGroup(ushort minOpcode, ushort maxOpcode, Action<ushort, ReadOnlyMemory<byte>> onPayload) =>
        NoopDisposable.Instance;

    /// <summary>Registers an inclusive stream-reading opcode handler group (<c>0x2fc+</c>).</summary>
    IDisposable RegisterRawOpcodeStreamHandler(ushort minOpcode, ushort maxOpcode, Func<ushort, INativePacketStream, ReadOnlyMemory<byte>, bool> onStream) =>
        NoopDisposable.Instance;

    /// <summary>Reads <paramref name="destination"/>.Length bytes from a native <c>BufferStreamIn</c> handle.</summary>
    bool StreamRead(nint streamHandle, Span<byte> destination) => false;

    /// <summary>Sends a raw opcode payload.</summary>
    void SendRawPacket(ushort opcode, ReadOnlySpan<byte> payload);

    /// <summary>Invokes a GEAL function or event on a world object.</summary>
    GealInvocationResult InvokeGeal(uint objectId, string entryName, bool isEvent, ReadOnlySpan<GealValue> args);

    /// <summary>Plays an audio event or <c>.ogg</c> file, returning a channel handle.</summary>
    int PlayAudio(string pathOrEvent, bool isFmodEvent, float volume);

    /// <summary>Plays an audio event or <c>.ogg</c> file with optional infinite looping, returning a channel handle.</summary>
    int PlayAudio(string pathOrEvent, bool isFmodEvent, float volume, bool loop) =>
        PlayAudio(pathOrEvent, isFmodEvent, volume);

    /// <summary>Updates linear volume (0.0 to 1.0) on an active audio channel.</summary>
    void SetAudioVolume(int channelHandle, float volume) { }

    /// <summary>Returns <c>true</c> if the audio channel is currently playing.</summary>
    bool IsAudioPlaying(int channelHandle) => false;

    /// <summary>Stops an audio channel.</summary>
    void StopAudio(int channelHandle);

    /// <summary>Returns the live native 3.29 UI root control pointer, or <c>0</c> if unavailable.</summary>
    nint GetNativeUiRoot() => nint.Zero;

    /// <summary>Creates and loads a live native 3.29 <c>Frame</c> window from a <c>.ui</c> virtual path (returns 0 when simulated or root not yet captured).</summary>
    nint CreateNativeUiWindow(string virtualPath) => nint.Zero;

    /// <summary>Creates and loads a live native 3.29 <c>Frame</c> window directly under <paramref name="parentControl"/>.</summary>
    nint CreateNativeUiWindowInto(nint parentControl, string virtualPath) => nint.Zero;

    /// <summary>Finds a child control by dot-separated id path inside a live native 3.29 UI control.</summary>
    nint FindNativeUiChild(nint parentControl, string dotPath) => nint.Zero;

    /// <summary>Sets visibility on a live native 3.29 UI control.</summary>
    void SetNativeUiVisible(nint control, bool visible) { }

    /// <summary>Sets text on a live native 3.29 Label (<c>isButton=false</c>) or Button (<c>isButton=true</c>).</summary>
    void SetNativeUiText(nint control, string text, bool isButton) { }

    /// <summary>Reads the absolute screen rectangle of a live native 3.29 UI control.</summary>
    bool TryGetNativeUiRect(nint control, out UiRect rect) { rect = default; return false; }

    /// <summary>Sets the relative position of a live native 3.29 UI control.</summary>
    void SetNativeUiPosition(nint control, float x, float y) { }

    /// <summary>Sets the size of a live native 3.29 UI control.</summary>
    void SetNativeUiSize(nint control, float width, float height) { }

    /// <summary>Sets the enabled state of a live native 3.29 UI control.</summary>
    void SetNativeUiEnabled(nint control, bool enabled) { }

    /// <summary>Reads the enabled state of a live native 3.29 UI control.</summary>
    bool TryGetNativeUiEnabled(nint control, out bool enabled) { enabled = false; return false; }

    /// <summary>Detaches <paramref name="childControl"/> from <paramref name="parentControl"/>.</summary>
    bool RemoveNativeUiChild(nint parentControl, nint childControl) => false;

    /// <summary>Destroys a live native 3.29 UI control.</summary>
    void DestroyNativeUiControl(nint control) { }

    /// <summary>Reads the current UTF-8 text of a live native 3.29 <c>TextBox</c>.</summary>
    string? GetNativeUiTextBoxText(nint textboxControl) => null;

    /// <summary>Sets the text of a live native 3.29 <c>TextBox</c>.</summary>
    void SetNativeUiTextBoxText(nint textboxControl, string text) { }

    /// <summary>Subscribes a managed callback to a live native 3.29 <c>TextBox</c> changed event.</summary>
    IDisposable SubscribeNativeUiTextBoxChanged(nint textboxControl, Action onChanged) => NoopDisposable.Instance;

    /// <summary>Subscribes a managed callback to a live native 3.29 <c>TextBox</c> submitted event.</summary>
    IDisposable SubscribeNativeUiTextBoxSubmitted(nint textboxControl, Action onSubmitted) => NoopDisposable.Instance;

    /// <summary>Reads the checked state of a live native 3.29 <c>CheckBox</c>.</summary>
    bool? GetNativeUiCheckBoxChecked(nint checkboxControl) => null;

    /// <summary>Sets the checked state of a live native 3.29 <c>CheckBox</c>.</summary>
    void SetNativeUiCheckBoxChecked(nint checkboxControl, bool isChecked) { }

    /// <summary>Subscribes a managed callback to a live native 3.29 <c>CheckBox</c> changed event.</summary>
    IDisposable SubscribeNativeUiCheckBoxChanged(nint checkboxControl, Action onChanged) => NoopDisposable.Instance;

    /// <summary>Subscribes a managed callback to a live native 3.29 Button's clicked event list.</summary>
    IDisposable SubscribeNativeUiClick(nint buttonControl, Action onClicked) => NoopDisposable.Instance;

    /// <summary>Reads text from the system clipboard, or <c>null</c> if unavailable.</summary>
    string? GetClipboardText() => null;

    /// <summary>Writes text to the system clipboard.</summary>
    void SetClipboardText(string text) { }

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
        UiClick = 4,
        TextBoxChanged = 5,
        TextBoxSubmitted = 6,
        CheckBoxChanged = 7,
        OpcodeRx = 8,
        HookChain = 9,
    }

    private sealed class UiEventSlot(NativeShimBridge bridge, CallbackKind kind)
    {
        public readonly NativeShimBridge Bridge = bridge;
        public readonly CallbackKind Kind = kind;
        public Action? Handler;
        public bool NativeSubscribed;
        public ulong ActiveToken;
    }

    private sealed class FrameCallbackWrapper(NativeShimBridge bridge, Action<float> callback)
    {
        public readonly NativeShimBridge Bridge = bridge;
        public readonly Action<float> Callback = callback;
    }

    private sealed class PendingWorkItem(NativeShimBridge bridge, Action work)
    {
        public readonly NativeShimBridge Bridge = bridge;
        public readonly Action Work = work;
    }

    private sealed class RulesetCallbackWrapper(NativeShimBridge bridge, Action<ReadOnlyMemory<byte>> callback)
    {
        public readonly NativeShimBridge Bridge = bridge;
        public readonly Action<ReadOnlyMemory<byte>> Callback = callback;
    }

    private sealed class ChatCallbackWrapper(NativeShimBridge bridge, Func<string, string, bool> callback)
    {
        public readonly NativeShimBridge Bridge = bridge;
        public readonly Func<string, string, bool> Callback = callback;
    }

    private sealed class HookChainWrapper(Action<HookContext> callback)
    {
        public readonly Action<HookContext> Callback = callback;
    }

    private sealed class OpcodeHandlerWrapper
    {
        public readonly NativeShimBridge Bridge;
        public readonly Action<ushort, ReadOnlyMemory<byte>>? ByteHandler;
        public readonly Func<ushort, INativePacketStream, ReadOnlyMemory<byte>, bool>? StreamHandler;

        public OpcodeHandlerWrapper(NativeShimBridge bridge, Action<ushort, ReadOnlyMemory<byte>> byteHandler)
        {
            Bridge = bridge;
            ByteHandler = byteHandler;
        }

        public OpcodeHandlerWrapper(NativeShimBridge bridge, Func<ushort, INativePacketStream, ReadOnlyMemory<byte>, bool> streamHandler)
        {
            Bridge = bridge;
            StreamHandler = streamHandler;
        }
    }

    private sealed unsafe class NativePacketStream(NativeShimBridge bridge, nint streamHandle) : INativePacketStream
    {
        public bool TryRead(Span<byte> destination) => bridge.StreamRead(streamHandle, destination);

        public byte[] ReadBytes(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (count == 0)
            {
                return [];
            }

            byte[] buf = new byte[count];
            if (!TryRead(buf))
            {
                throw new EndOfStreamException($"Failed to read {count} byte(s) from native packet stream.");
            }

            return buf;
        }

        public T Read<T>() where T : unmanaged
        {
            Span<byte> buf = stackalloc byte[Unsafe.SizeOf<T>()];
            if (!TryRead(buf))
            {
                throw new EndOfStreamException($"Failed to read {typeof(T).Name} ({buf.Length} bytes) from native packet stream.");
            }

            return MemoryMarshal.Read<T>(buf);
        }
    }

    private readonly struct GameThreadScope(bool prev) : IDisposable
    {
        public void Dispose() => s_inNativeGameFrame = prev;
    }

    private const int MaxMockPendingWork = 4096;
    private const ulong SyntheticStreamMagic = 0x4E4D53545245414Dul;

    [ThreadStatic]
    private static bool s_inNativeGameFrame;

    private readonly NmAbiTable* _table;
    private int _gameThreadId;
    private readonly ConcurrentQueue<Action> _pendingWork = new();
    private int _pendingWorkCount;
    private readonly ConcurrentDictionary<nint, GCHandle> _pendingWorkHandles = new();
    private readonly ConcurrentDictionary<(CallbackKind Kind, ulong Token), GCHandle> _pinnedCallbacks = new();
    private readonly ConcurrentDictionary<(CallbackKind Kind, nint Control), UiEventSlot> _uiEventSlots = new();
    private readonly ConcurrentDictionary<string, int> _localStats = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<uint, WorldObjectInfo> _clientObjects = new();
    private readonly ConcurrentDictionary<ushort, List<Action<ReadOnlyMemory<byte>>>> _rawReceivers = new();
    private int _nextAudioChannel = 1;
    private uint _nextClientObjectId = 0x8000_0001u;
    private ulong _managedFileOverlayToken;
    private bool _disposed;

    internal int PendingWorkHandleCount => _pendingWorkHandles.Count;

    /// <inheritdoc/>
    public ClientBuildId DetectedBuild { get; }

    /// <inheritdoc/>
    public string BuildVersion { get; }

    /// <inheritdoc/>
    public string? ExecutableSha256 { get; }

    /// <inheritdoc/>
    public nint ImageBase { get; }

    /// <inheritdoc/>
    public bool IsOnGameThread =>
        s_inNativeGameFrame ||
        (Volatile.Read(ref _gameThreadId) is int tid && tid != 0 && Environment.CurrentManagedThreadId == tid);

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

    private GameThreadScope EnterNativeGameThreadScope(bool captureGameThreadId)
    {
        if (captureGameThreadId && Volatile.Read(ref _gameThreadId) == 0)
        {
            Interlocked.CompareExchange(ref _gameThreadId, Environment.CurrentManagedThreadId, 0);
        }

        bool prev = s_inNativeGameFrame;
        s_inNativeGameFrame = true;
        return new GameThreadScope(prev);
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
    public ulong InstallInlineHookChain(string symbolName, HookPhase phase, int priority, Action<HookContext> callback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolName);
        ArgumentNullException.ThrowIfNull(callback);
        if (_table->InstallInlineChainHookSym is null)
        {
            throw new NotSupportedException("Native shim does not expose install_inline_chain_hook_sym.");
        }

        var wrapper = new HookChainWrapper(callback);
        GCHandle gcHandle = GCHandle.Alloc(wrapper);
        byte[] utf8 = ToNullTerminatedUtf8(symbolName);
        ulong handle = 0;
        fixed (byte* pName = utf8)
        {
            NmStatus status = _table->InstallInlineChainHookSym(
                pName,
                (int)phase,
                priority,
                &OnNativeHookChain,
                (void*)GCHandle.ToIntPtr(gcHandle),
                &handle);
            if (status != NmStatus.Ok)
            {
                gcHandle.Free();
                EnsureOk(status, $"InstallInlineHookChain({symbolName})");
            }
        }

        _pinnedCallbacks[(CallbackKind.HookChain, handle)] = gcHandle;
        return handle;
    }

    /// <inheritdoc/>
    public ulong InstallInlineHookChainAt(nint address, string? expectedBytesHex, HookPhase phase, int priority, Action<HookContext> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (address == nint.Zero)
        {
            throw new ArgumentException("Cannot install hook chain at null address.", nameof(address));
        }

        if (_table->InstallInlineChainHook is null)
        {
            throw new NotSupportedException("Native shim does not expose install_inline_chain_hook.");
        }

        var wrapper = new HookChainWrapper(callback);
        GCHandle gcHandle = GCHandle.Alloc(wrapper);
        byte[]? hexUtf8 = string.IsNullOrWhiteSpace(expectedBytesHex) ? null : ToNullTerminatedUtf8(expectedBytesHex);
        ulong handle = 0;
        fixed (byte* pHex = hexUtf8)
        {
            NmStatus status = _table->InstallInlineChainHook(
                unchecked((ulong)address),
                pHex,
                (int)phase,
                priority,
                &OnNativeHookChain,
                (void*)GCHandle.ToIntPtr(gcHandle),
                &handle);
            if (status != NmStatus.Ok)
            {
                gcHandle.Free();
                EnsureOk(status, $"InstallInlineHookChainAt(0x{(ulong)address:X})");
            }
        }

        _pinnedCallbacks[(CallbackKind.HookChain, handle)] = gcHandle;
        return handle;
    }

    /// <inheritdoc/>
    public ulong InstallVtableHookChain(nint vtableAddress, uint slotIndex, HookPhase phase, int priority, Action<HookContext> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (vtableAddress == nint.Zero)
        {
            throw new ArgumentException("Cannot install vtable hook chain at null vtable.", nameof(vtableAddress));
        }

        if (_table->InstallVtableChainHook is null)
        {
            throw new NotSupportedException("Native shim does not expose install_vtable_chain_hook.");
        }

        var wrapper = new HookChainWrapper(callback);
        GCHandle gcHandle = GCHandle.Alloc(wrapper);
        ulong handle = 0;
        NmStatus status = _table->InstallVtableChainHook(
            (void*)vtableAddress,
            slotIndex,
            (int)phase,
            priority,
            &OnNativeHookChain,
            (void*)GCHandle.ToIntPtr(gcHandle),
            &handle);
        if (status != NmStatus.Ok)
        {
            gcHandle.Free();
            EnsureOk(status, $"InstallVtableHookChain(slot={slotIndex})");
        }

        _pinnedCallbacks[(CallbackKind.HookChain, handle)] = gcHandle;
        return handle;
    }

    /// <inheritdoc/>
    public bool RemoveHook(ulong hookHandle)
    {
        if (_table->RemoveHook is null || hookHandle == 0)
        {
            return false;
        }

        bool removed = _table->RemoveHook(hookHandle) == NmStatus.Ok;
        if (_pinnedCallbacks.TryRemove((CallbackKind.HookChain, hookHandle), out GCHandle h) && h.IsAllocated)
        {
            h.Free();
        }

        return removed;
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

        if (args.Length > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Native calls support at most 8 arguments.");
        }

        if (args.Length > 4 || _table->CallGuarded is null)
        {
            if (_table->CallNativeGuarded is null)
            {
                throw new NotSupportedException("Native shim does not expose call_native_guarded.");
            }

            Span<NativeCallArgument> callArgs = stackalloc NativeCallArgument[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                callArgs[i] = NativeCallArgument.FromInt(args[i]);
            }

            return InvokeNativeEx(address, callArgs, returnIsFloat: false).AsIntPtr;
        }

        GuardedInvocationContext ctx = new()
        {
            TargetAddress = address,
            ArgCount = args.Length,
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
    public NativeCallResult InvokeNativeEx(nint targetAddress, ReadOnlySpan<NativeCallArgument> args, bool returnIsFloat = false)
    {
        if (targetAddress == nint.Zero)
        {
            throw new ArgumentException("Cannot invoke null target address.", nameof(targetAddress));
        }

        if (args.Length > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Win64 guarded native calls support at most 8 arguments.");
        }

        if (_table->CallNativeGuarded is null)
        {
            throw new NotSupportedException("Native shim does not expose call_native_guarded.");
        }

        NmNativeCallDesc desc = default;
        desc.TargetAddress = unchecked((ulong)targetAddress);
        desc.ArgCount = (uint)args.Length;
        desc.ReturnIsFloat = returnIsFloat ? (byte)1 : (byte)0;

        uint floatMask = 0;
        for (int i = 0; i < args.Length; i++)
        {
            desc.Args[i] = args[i].RawBits;
            if (args[i].IsFloat)
            {
                floatMask |= 1u << i;
            }
        }

        desc.FloatArgMask = floatMask;

        NmExceptionInfo exInfo = default;
        NmStatus status = _table->CallNativeGuarded(&desc, &exInfo);
        if (status == NmStatus.ErrGameException)
        {
            throw new InvalidOperationException(
                $"Native call 0x{(ulong)targetAddress:X} threw native exception 0x{exInfo.ExceptionCode:X8} at 0x{exInfo.ExceptionAddress:X}: {exInfo.GetMessage()}");
        }

        EnsureOk(status, $"InvokeNativeEx(0x{(ulong)targetAddress:X})");
        return new NativeCallResult(desc.OutInt, desc.OutFloatBits);
    }

    /// <inheritdoc/>
    public IDisposable RegisterFrameCallback(Action<float> onFrame)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        if (_table->RegisterFrameCallback is null)
        {
            return NoopDisposable.Instance;
        }

        GCHandle handle = GCHandle.Alloc(new FrameCallbackWrapper(this, onFrame));
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
    public bool EnqueueGameThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Volatile.Read(ref _disposed))
        {
            return false;
        }

        if (_table->EnqueueMainThread is not null)
        {
            var item = new PendingWorkItem(this, action);
            GCHandle handle = GCHandle.Alloc(item);
            nint ptr = GCHandle.ToIntPtr(handle);
            _pendingWorkHandles[ptr] = handle;

            NmStatus status = _table->EnqueueMainThread(&OnNativeMainThreadDrain, (void*)ptr);
            if (status != NmStatus.Ok)
            {
                if (_pendingWorkHandles.TryRemove(ptr, out GCHandle removed) && removed.IsAllocated)
                {
                    removed.Free();
                }

                if (status == NmStatus.ErrQueueFull)
                {
                    return false;
                }

                EnsureOk(status, "EnqueueMainThread");
            }

            return true;
        }

        while (true)
        {
            int cur = Volatile.Read(ref _pendingWorkCount);
            if (cur >= MaxMockPendingWork)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _pendingWorkCount, cur + 1, cur) == cur)
            {
                break;
            }
        }

        _pendingWork.Enqueue(action);
        return true;
    }

    /// <inheritdoc/>
    public int DrainGameThreadQueue(float deltaSeconds = 0f, int maxItems = 256)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxItems);
        if (maxItems == 0)
        {
            return 0;
        }

        int drained = 0;
        bool prev = s_inNativeGameFrame;
        s_inNativeGameFrame = true;
        try
        {
            while (drained < maxItems && _pendingWork.TryDequeue(out Action? work))
            {
                Interlocked.Decrement(ref _pendingWorkCount);
                work();
                drained++;
            }
        }
        finally
        {
            s_inNativeGameFrame = prev;
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
        Volatile.Write(ref _managedFileOverlayToken, token);
        return new ActionDisposable(() =>
        {
            if (Volatile.Read(ref _managedFileOverlayToken) == key.token)
            {
                Volatile.Write(ref _managedFileOverlayToken, 0);
            }

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
    public void SetFileOverlayKnownPaths(IReadOnlyCollection<string> normalizedPaths)
    {
        ArgumentNullException.ThrowIfNull(normalizedPaths);
        ulong token = Volatile.Read(ref _managedFileOverlayToken);
        if (token == 0 || _table->SetFileOverlayKnownPaths is null)
        {
            return;
        }

        int count = normalizedPaths.Count;
        if (count == 0)
        {
            _table->SetFileOverlayKnownPaths(token, null, 0);
            return;
        }

        byte** ptrs = (byte**)NativeMemory.Alloc((nuint)count, (nuint)sizeof(byte*));
        int allocated = 0;
        try
        {
            foreach (string path in normalizedPaths)
            {
                int byteLen = Encoding.UTF8.GetByteCount(path);
                byte* buf = (byte*)NativeMemory.Alloc((nuint)(byteLen + 1));
                Encoding.UTF8.GetBytes(path, new Span<byte>(buf, byteLen));
                buf[byteLen] = 0;
                ptrs[allocated++] = buf;
            }

            _table->SetFileOverlayKnownPaths(token, ptrs, (nuint)allocated);
        }
        finally
        {
            for (int i = 0; i < allocated; i++)
            {
                NativeMemory.Free(ptrs[i]);
            }

            NativeMemory.Free(ptrs);
        }
    }

    /// <inheritdoc/>
    public bool SupportsConnectionToken => _table->GetActiveConnectionToken is not null;

    /// <inheritdoc/>
    public ulong GetActiveConnectionToken()
    {
        if (_table->GetActiveConnectionToken is null)
        {
            return 0;
        }

        ulong token = 0;
        return _table->GetActiveConnectionToken(&token) == NmStatus.Ok ? token : 0;
    }

    /// <inheritdoc/>
    public byte[]? ReadStockGameFile(string virtualPath)
    {
        string normalized = NormalizeAndValidateVirtualPath(virtualPath);
        if (_table->FilesReadStock is null)
        {
            return null;
        }

        byte[] utf8 = ToNullTerminatedUtf8(normalized);
        byte* outData = null;
        nuint outSize = 0;
        try
        {
            fixed (byte* pPath = utf8)
            {
                NmStatus status = _table->FilesReadStock(pPath, &outData, &outSize);
                if (status != NmStatus.Ok || outData is null)
                {
                    return null;
                }
            }

            int len = checked((int)outSize);
            byte[] copy = new byte[len];
            if (len > 0)
            {
                Marshal.Copy((nint)outData, copy, 0, len);
            }

            return copy;
        }
        finally
        {
            if (outData is not null && _table->FreeFileOverlayBuffer is not null)
            {
                _table->FreeFileOverlayBuffer(outData);
            }
        }
    }

    /// <inheritdoc/>
    public void InvalidateCachedAsset(string virtualPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualPath);
        if (_table->FilesInvalidate is null)
        {
            return;
        }

        string normalized = ClientTextEncoding.NormalizeVirtualPath(virtualPath);
        byte[] utf8 = ToNullTerminatedUtf8(normalized);
        fixed (byte* pPath = utf8)
        {
            _table->FilesInvalidate(pPath);
        }
    }

    /// <inheritdoc/>
    public IDisposable RegisterRulesetReceiver(byte rulesetId, Action<ReadOnlyMemory<byte>> onPayload)
    {
        ArgumentNullException.ThrowIfNull(onPayload);
        if (_table->RegisterRulesetHandler is null)
        {
            return NoopDisposable.Instance;
        }

        GCHandle handle = GCHandle.Alloc(new RulesetCallbackWrapper(this, onPayload));
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

        GCHandle handle = GCHandle.Alloc(new ChatCallbackWrapper(this, onIncomingChat));
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
        ArgumentNullException.ThrowIfNull(onPayload);
        if (_table->RegisterOpcodeHandlerGroup is not null)
        {
            return RegisterRawOpcodeGroup(opcode, opcode, (_, payload) => onPayload(payload));
        }

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
    public IDisposable RegisterRawOpcodeGroup(ushort minOpcode, ushort maxOpcode, Action<ushort, ReadOnlyMemory<byte>> onPayload)
    {
        ArgumentNullException.ThrowIfNull(onPayload);
        if (_table->RegisterOpcodeHandlerGroup is null)
        {
            return NoopDisposable.Instance;
        }

        GCHandle handle = GCHandle.Alloc(new OpcodeHandlerWrapper(this, onPayload));
        ulong token = 0;
        NmStatus status = _table->RegisterOpcodeHandlerGroup(minOpcode, maxOpcode, &OnNativeOpcodeRx, (void*)GCHandle.ToIntPtr(handle), &token);
        if (status != NmStatus.Ok)
        {
            handle.Free();
            EnsureOk(status, $"RegisterRawOpcodeGroup(0x{minOpcode:X4}..0x{maxOpcode:X4})");
        }

        var key = (CallbackKind.OpcodeRx, token);
        _pinnedCallbacks[key] = handle;
        return new ActionDisposable(() =>
        {
            if (_table->UnregisterOpcodeHandlerGroup is not null)
            {
                _table->UnregisterOpcodeHandlerGroup(key.token);
            }

            if (_pinnedCallbacks.TryRemove(key, out GCHandle h) && h.IsAllocated)
            {
                h.Free();
            }
        });
    }

    /// <inheritdoc/>
    public IDisposable RegisterRawOpcodeStreamHandler(ushort minOpcode, ushort maxOpcode, Func<ushort, INativePacketStream, ReadOnlyMemory<byte>, bool> onStream)
    {
        ArgumentNullException.ThrowIfNull(onStream);
        if (_table->RegisterOpcodeHandlerGroup is null)
        {
            return NoopDisposable.Instance;
        }

        GCHandle handle = GCHandle.Alloc(new OpcodeHandlerWrapper(this, onStream));
        ulong token = 0;
        NmStatus status = _table->RegisterOpcodeHandlerGroup(minOpcode, maxOpcode, &OnNativeOpcodeRx, (void*)GCHandle.ToIntPtr(handle), &token);
        if (status != NmStatus.Ok)
        {
            handle.Free();
            EnsureOk(status, $"RegisterRawOpcodeStreamHandler(0x{minOpcode:X4}..0x{maxOpcode:X4})");
        }

        var key = (CallbackKind.OpcodeRx, token);
        _pinnedCallbacks[key] = handle;
        return new ActionDisposable(() =>
        {
            if (_table->UnregisterOpcodeHandlerGroup is not null)
            {
                _table->UnregisterOpcodeHandlerGroup(key.token);
            }

            if (_pinnedCallbacks.TryRemove(key, out GCHandle h) && h.IsAllocated)
            {
                h.Free();
            }
        });
    }

    /// <inheritdoc/>
    public bool StreamRead(nint streamHandle, Span<byte> destination)
    {
        if (streamHandle == nint.Zero || _table->StreamRead is null)
        {
            return false;
        }

        if (destination.IsEmpty)
        {
            return true;
        }

        fixed (byte* pDst = destination)
        {
            return _table->StreamRead((void*)streamHandle, pDst, (ulong)destination.Length) == NmStatus.Ok;
        }
    }

    /// <inheritdoc/>
    public void SendRawPacket(ushort opcode, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 65536)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), payload.Length, "Raw packet payload exceeds 65536 bytes.");
        }

        if (_table->SendRawPacket is null)
        {
            return;
        }

        fixed (byte* pPayload = payload)
        {
            NmStatus status = _table->SendRawPacket(opcode, pPayload, (uint)payload.Length);
            EnsureOk(status, $"SendRawPacket(0x{opcode:X4})");
        }
    }

    /// <inheritdoc/>
    public GealInvocationResult InvokeGeal(uint objectId, string entryName, bool isEvent, ReadOnlySpan<GealValue> args) =>
        new(false, GealValue.Null, "Native GEAL invocation requires live 3.29 client object pointer.");

    /// <inheritdoc/>
    public int PlayAudio(string pathOrEvent, bool isFmodEvent, float volume) =>
        PlayAudio(pathOrEvent, isFmodEvent, volume, loop: false);

    /// <inheritdoc/>
    public int PlayAudio(string pathOrEvent, bool isFmodEvent, float volume, bool loop)
    {
        byte[] utf8 = ToNullTerminatedUtf8(pathOrEvent);
        fixed (byte* pPath = utf8)
        {
            int handle = 0;
            if (isFmodEvent && _table->AudioPlayEvent is not null)
            {
                if (_table->AudioPlayEvent(pPath, volume, &handle) == NmStatus.Ok && handle > 0)
                {
                    return handle;
                }
            }
            else if (!isFmodEvent && _table->AudioPlayOgg is not null)
            {
                if (_table->AudioPlayOgg(pPath, volume, loop ? 1 : 0, &handle) == NmStatus.Ok && handle > 0)
                {
                    return handle;
                }
            }
        }

        return Interlocked.Increment(ref _nextAudioChannel);
    }

    /// <inheritdoc/>
    public void SetAudioVolume(int channelHandle, float volume)
    {
        if (_table->AudioSetVolume is not null && channelHandle > 0)
        {
            _table->AudioSetVolume(channelHandle, Math.Clamp(volume, 0f, 1f));
        }
    }

    /// <inheritdoc/>
    public bool IsAudioPlaying(int channelHandle)
    {
        if (_table->AudioIsPlaying is not null && channelHandle > 0)
        {
            int playing = 0;
            if (_table->AudioIsPlaying(channelHandle, &playing) == NmStatus.Ok)
            {
                return playing != 0;
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public void StopAudio(int channelHandle)
    {
        if (_table->AudioStop is not null && channelHandle > 0)
        {
            _table->AudioStop(channelHandle);
        }
    }

    /// <inheritdoc/>
    public nint GetNativeUiRoot()
    {
        if (_table->UiGetRoot is null)
        {
            return nint.Zero;
        }

        void* root = null;
        return _table->UiGetRoot(&root) == NmStatus.Ok ? (nint)root : nint.Zero;
    }

    /// <inheritdoc/>
    public nint CreateNativeUiWindow(string virtualPath)
    {
        if (_table->UiCreateWindow is null || string.IsNullOrWhiteSpace(virtualPath))
        {
            return nint.Zero;
        }

        byte[] utf8 = ToNullTerminatedUtf8(virtualPath);
        fixed (byte* pPath = utf8)
        {
            void* ctrl = null;
            return _table->UiCreateWindow(pPath, &ctrl) == NmStatus.Ok ? (nint)ctrl : nint.Zero;
        }
    }

    /// <inheritdoc/>
    public nint CreateNativeUiWindowInto(nint parentControl, string virtualPath)
    {
        if (_table->UiCreateWindowInto is null || parentControl == nint.Zero || string.IsNullOrWhiteSpace(virtualPath))
        {
            return nint.Zero;
        }

        byte[] utf8 = ToNullTerminatedUtf8(virtualPath);
        fixed (byte* pPath = utf8)
        {
            void* ctrl = null;
            return _table->UiCreateWindowInto((void*)parentControl, pPath, &ctrl) == NmStatus.Ok ? (nint)ctrl : nint.Zero;
        }
    }

    /// <inheritdoc/>
    public nint FindNativeUiChild(nint parentControl, string dotPath)
    {
        if (_table->UiFindChild is null || parentControl == nint.Zero || string.IsNullOrWhiteSpace(dotPath))
        {
            return nint.Zero;
        }

        byte[] utf8 = ToNullTerminatedUtf8(dotPath);
        fixed (byte* pPath = utf8)
        {
            void* child = null;
            return _table->UiFindChild((void*)parentControl, pPath, &child) == NmStatus.Ok ? (nint)child : nint.Zero;
        }
    }

    /// <inheritdoc/>
    public void SetNativeUiVisible(nint control, bool visible)
    {
        if (_table->UiSetVisible is not null && control != nint.Zero)
        {
            _table->UiSetVisible((void*)control, visible ? 1 : 0);
        }
    }

    /// <inheritdoc/>
    public void SetNativeUiText(nint control, string text, bool isButton)
    {
        if (_table->UiSetText is not null && control != nint.Zero)
        {
            byte[] utf8 = ToNullTerminatedUtf8(text ?? string.Empty);
            fixed (byte* pText = utf8)
            {
                _table->UiSetText((void*)control, pText, isButton ? 1 : 0);
            }
        }
    }

    /// <inheritdoc/>
    public bool TryGetNativeUiRect(nint control, out UiRect rect)
    {
        rect = default;
        if (_table->UiGetRect is null || control == nint.Zero)
        {
            return false;
        }

        float x = 0f, y = 0f, w = 0f, h = 0f;
        if (_table->UiGetRect((void*)control, &x, &y, &w, &h) != NmStatus.Ok)
        {
            return false;
        }

        rect = new UiRect(x, y, w, h);
        return true;
    }

    /// <inheritdoc/>
    public void SetNativeUiPosition(nint control, float x, float y)
    {
        if (_table->UiSetPosition is not null && control != nint.Zero)
        {
            _table->UiSetPosition((void*)control, x, y);
        }
    }

    /// <inheritdoc/>
    public void SetNativeUiSize(nint control, float width, float height)
    {
        if (_table->UiSetSize is not null && control != nint.Zero)
        {
            _table->UiSetSize((void*)control, width, height);
        }
    }

    /// <inheritdoc/>
    public void SetNativeUiEnabled(nint control, bool enabled)
    {
        if (_table->UiSetEnabled is not null && control != nint.Zero)
        {
            _table->UiSetEnabled((void*)control, enabled ? 1 : 0);
        }
    }

    /// <inheritdoc/>
    public bool TryGetNativeUiEnabled(nint control, out bool enabled)
    {
        enabled = false;
        if (_table->UiGetEnabled is null || control == nint.Zero)
        {
            return false;
        }

        int raw = 0;
        if (_table->UiGetEnabled((void*)control, &raw) != NmStatus.Ok)
        {
            return false;
        }

        enabled = raw != 0;
        return true;
    }

    /// <inheritdoc/>
    public bool RemoveNativeUiChild(nint parentControl, nint childControl)
    {
        if (_table->UiRemoveChild is null || parentControl == nint.Zero || childControl == nint.Zero)
        {
            return false;
        }

        return _table->UiRemoveChild((void*)parentControl, (void*)childControl) == NmStatus.Ok;
    }

    /// <inheritdoc/>
    public void DestroyNativeUiControl(nint control)
    {
        if (control == nint.Zero)
        {
            return;
        }

        CleanupUiSlotsForControl(control);
        if (_table->UiDestroyControl is not null)
        {
            _table->UiDestroyControl((void*)control);
        }
    }

    /// <inheritdoc/>
    public string? GetNativeUiTextBoxText(nint textboxControl)
    {
        if (_table->UiTextboxGetText is null || textboxControl == nint.Zero)
        {
            return null;
        }

        nuint length = 0;
        NmStatus st = _table->UiTextboxGetText((void*)textboxControl, null, 0, &length);
        if (st != NmStatus.Ok)
        {
            return null;
        }

        if (length == 0)
        {
            return string.Empty;
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            int cap = checked((int)length + 1);
            byte[] buf = new byte[cap];
            fixed (byte* pBuf = buf)
            {
                nuint actual = 0;
                st = _table->UiTextboxGetText((void*)textboxControl, pBuf, (nuint)cap, &actual);
                if (st == NmStatus.Ok)
                {
                    return Encoding.UTF8.GetString(buf, 0, checked((int)actual));
                }

                if (st != NmStatus.ErrBufferTooSmall || actual == 0)
                {
                    return null;
                }

                length = actual;
            }
        }

        return null;
    }

    /// <inheritdoc/>
    public void SetNativeUiTextBoxText(nint textboxControl, string text)
    {
        if (_table->UiTextboxSetText is null || textboxControl == nint.Zero)
        {
            return;
        }

        byte[] utf8 = ToNullTerminatedUtf8(text ?? string.Empty);
        fixed (byte* pText = utf8)
        {
            _table->UiTextboxSetText((void*)textboxControl, pText);
        }
    }

    /// <inheritdoc/>
    public IDisposable SubscribeNativeUiTextBoxChanged(nint textboxControl, Action onChanged) =>
        SubscribeNativeUiEvent(
            CallbackKind.TextBoxChanged,
            textboxControl,
            onChanged,
            _table->UiTextboxSubscribeChanged);

    /// <inheritdoc/>
    public IDisposable SubscribeNativeUiTextBoxSubmitted(nint textboxControl, Action onSubmitted) =>
        SubscribeNativeUiEvent(
            CallbackKind.TextBoxSubmitted,
            textboxControl,
            onSubmitted,
            _table->UiTextboxSubscribeSubmitted);

    /// <inheritdoc/>
    public bool? GetNativeUiCheckBoxChecked(nint checkboxControl)
    {
        if (_table->UiCheckboxGetChecked is null || checkboxControl == nint.Zero)
        {
            return null;
        }

        int value = 0;
        return _table->UiCheckboxGetChecked((void*)checkboxControl, &value) == NmStatus.Ok ? value != 0 : null;
    }

    /// <inheritdoc/>
    public void SetNativeUiCheckBoxChecked(nint checkboxControl, bool isChecked)
    {
        if (_table->UiCheckboxSetChecked is not null && checkboxControl != nint.Zero)
        {
            _table->UiCheckboxSetChecked((void*)checkboxControl, isChecked ? 1 : 0);
        }
    }

    /// <inheritdoc/>
    public IDisposable SubscribeNativeUiCheckBoxChanged(nint checkboxControl, Action onChanged) =>
        SubscribeNativeUiEvent(
            CallbackKind.CheckBoxChanged,
            checkboxControl,
            onChanged,
            _table->UiCheckboxSubscribeChanged);

    /// <inheritdoc/>
    public IDisposable SubscribeNativeUiClick(nint buttonControl, Action onClicked) =>
        SubscribeNativeUiEvent(
            CallbackKind.UiClick,
            buttonControl,
            onClicked,
            _table->UiSubscribeClick);

    private IDisposable SubscribeNativeUiEvent(
        CallbackKind kind,
        nint control,
        Action callback,
        delegate* unmanaged[Cdecl]<void*, delegate* unmanaged[Cdecl]<void*, void*, void>, void*, ulong*, NmStatus> subscribeFn)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (subscribeFn is null || control == nint.Zero)
        {
            return NoopDisposable.Instance;
        }

        UiEventSlot slot = _uiEventSlots.GetOrAdd((kind, control), k => new UiEventSlot(this, k.Kind));
        lock (slot)
        {
            Volatile.Write(ref slot.Handler, callback);
            if (!slot.NativeSubscribed)
            {
                GCHandle handle = GCHandle.Alloc(slot);
                ulong token = 0;
                NmStatus st = subscribeFn((void*)control, &OnNativeUiEvent, (void*)GCHandle.ToIntPtr(handle), &token);
                if (st != NmStatus.Ok)
                {
                    handle.Free();
                    Interlocked.CompareExchange(ref slot.Handler, null, callback);
                    return NoopDisposable.Instance;
                }

                slot.NativeSubscribed = true;
                slot.ActiveToken = token;
                _pinnedCallbacks[(kind, token)] = handle;
            }
        }

        return new ActionDisposable(() =>
        {
            lock (slot)
            {
                Interlocked.CompareExchange(ref slot.Handler, null, callback);
                if (Volatile.Read(ref slot.Handler) is null && slot.ActiveToken != 0)
                {
                    ulong token = slot.ActiveToken;
                    slot.ActiveToken = 0;
                    slot.NativeSubscribed = false;
                    if (_table->UiUnsubscribe is not null)
                    {
                        _table->UiUnsubscribe(token);
                    }

                    if (_pinnedCallbacks.TryRemove((kind, token), out GCHandle h) && h.IsAllocated)
                    {
                        h.Free();
                    }
                }
            }
        });
    }

    private void CleanupUiSlotsForControl(nint control)
    {
        foreach (CallbackKind kind in new[] { CallbackKind.UiClick, CallbackKind.TextBoxChanged, CallbackKind.TextBoxSubmitted, CallbackKind.CheckBoxChanged })
        {
            if (_uiEventSlots.TryRemove((kind, control), out UiEventSlot? slot))
            {
                lock (slot)
                {
                    Volatile.Write(ref slot.Handler, null);
                    if (slot.ActiveToken != 0)
                    {
                        ulong token = slot.ActiveToken;
                        slot.ActiveToken = 0;
                        slot.NativeSubscribed = false;
                        if (_table->UiUnsubscribe is not null)
                        {
                            _table->UiUnsubscribe(token);
                        }

                        if (_pinnedCallbacks.TryRemove((kind, token), out GCHandle h) && h.IsAllocated)
                        {
                            h.Free();
                        }
                    }
                }
            }
        }
    }

    /// <inheritdoc/>
    public string? GetClipboardText()
    {
        if (_table->ClipboardGetText is null)
        {
            return null;
        }

        nuint length = 0;
        NmStatus st = _table->ClipboardGetText(null, 0, &length);
        if (st != NmStatus.Ok)
        {
            return null;
        }

        if (length == 0)
        {
            return string.Empty;
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            int cap = checked((int)length + 1);
            byte[] buf = new byte[cap];
            fixed (byte* pBuf = buf)
            {
                nuint actual = 0;
                st = _table->ClipboardGetText(pBuf, (nuint)cap, &actual);
                if (st == NmStatus.Ok)
                {
                    return Encoding.UTF8.GetString(buf, 0, checked((int)actual));
                }

                if (st != NmStatus.ErrBufferTooSmall || actual == 0)
                {
                    return null;
                }

                length = actual;
            }
        }

        return null;
    }

    /// <inheritdoc/>
    public void SetClipboardText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_table->ClipboardSetText is null)
        {
            return;
        }

        byte[] utf8 = ToNullTerminatedUtf8(text);
        fixed (byte* pText = utf8)
        {
            NmStatus status = _table->ClipboardSetText(pText);
            EnsureOk(status, "ClipboardSetText");
        }
    }

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
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        Volatile.Write(ref _disposed, true);
        foreach (UiEventSlot slot in _uiEventSlots.Values)
        {
            Volatile.Write(ref slot.Handler, null);
            slot.ActiveToken = 0;
            slot.NativeSubscribed = false;
        }

        _uiEventSlots.Clear();

        foreach (nint ptr in _pendingWorkHandles.Keys.ToArray())
        {
            if (_pendingWorkHandles.TryRemove(ptr, out GCHandle h) && h.IsAllocated)
            {
                h.Free();
            }
        }

        while (_pendingWork.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _pendingWorkCount);
        }

        foreach ((CallbackKind Kind, ulong Token) key in _pinnedCallbacks.Keys.ToArray())
        {
            if (_pinnedCallbacks.TryRemove(key, out GCHandle h) && h.IsAllocated)
            {
                h.Free();
            }
        }
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

    private struct StreamRemainingProbeContext
    {
        public void* StreamHandle;
        public ulong Remaining;
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
    private static long ProbeStreamRemainingTrampoline(void* context) => ProbeStreamRemainingCore(context);

    private static long ProbeStreamRemainingCore(void* context)
    {
        StreamRemainingProbeContext* ctx = (StreamRemainingProbeContext*)context;
        byte* stream = (byte*)ctx->StreamHandle;
        ulong magic = *(ulong*)stream;
        if (magic == SyntheticStreamMagic)
        {
            ulong size = *(ulong*)(stream + 16);
            ulong cursor = *(ulong*)(stream + 24);
            ctx->Remaining = size >= cursor ? size - cursor : 0;
            return 0;
        }

        ulong total = *(ulong*)(stream + 0x270);
        ulong cur = *(ulong*)(stream + 0x278);
        ctx->Remaining = total >= cur ? total - cur : 0;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeFrame(float deltaSeconds, void* userData)
    {
        try
        {
            GCHandle handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is FrameCallbackWrapper wrapper)
            {
                using var _ = wrapper.Bridge.EnterNativeGameThreadScope(captureGameThreadId: true);
                wrapper.Callback(deltaSeconds);
            }
        }
        catch
        {
            // Never let managed exceptions unwind across native game frames!
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeUiEvent(void* control, void* userData)
    {
        try
        {
            GCHandle handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is UiEventSlot slot)
            {
                using var _ = slot.Bridge.EnterNativeGameThreadScope(captureGameThreadId: true);
                Volatile.Read(ref slot.Handler)?.Invoke();
            }
        }
        catch
        {
            // Never unwind managed exceptions into native UI event dispatch.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeMainThreadDrain(void* userData)
    {
        nint ptr = (nint)userData;
        if (ptr == nint.Zero)
        {
            return;
        }

        GCHandle handle = GCHandle.FromIntPtr(ptr);
        PendingWorkItem? item = handle.Target as PendingWorkItem;
        if (item is not null)
        {
            if (item.Bridge._pendingWorkHandles.TryRemove(ptr, out GCHandle removed) && removed.IsAllocated)
            {
                removed.Free();
            }
        }
        else if (handle.IsAllocated)
        {
            handle.Free();
        }

        if (item is null || Volatile.Read(ref item.Bridge._disposed))
        {
            return;
        }

        try
        {
            using var _ = item.Bridge.EnterNativeGameThreadScope(captureGameThreadId: true);
            item.Work();
        }
        catch
        {
            // Exception wall across native boundary.
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
            if (handle.Target is RulesetCallbackWrapper wrapper)
            {
                using var _ = wrapper.Bridge.EnterNativeGameThreadScope(captureGameThreadId: true);
                byte[] copy = new byte[length];
                if (length > 0 && payload is not null)
                {
                    Marshal.Copy((nint)payload, copy, 0, (int)length);
                }

                wrapper.Callback(copy);
            }
        }
        catch
        {
            // Never unwind managed exceptions into native network pump.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnNativeChatRx(byte* senderUtf8, byte* textUtf8, void* userData)
    {
        try
        {
            GCHandle handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is ChatCallbackWrapper wrapper)
            {
                using var _ = wrapper.Bridge.EnterNativeGameThreadScope(captureGameThreadId: true);
                string sender = senderUtf8 is not null ? Marshal.PtrToStringUTF8((nint)senderUtf8) ?? string.Empty : string.Empty;
                string text = textUtf8 is not null ? Marshal.PtrToStringUTF8((nint)textUtf8) ?? string.Empty : string.Empty;
                return wrapper.Callback(sender, text) ? 1 : 0;
            }
        }
        catch
        {
            // Exception wall: fail-open (do not suppress stock chat on managed exception).
        }

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnNativeOpcodeRx(ushort opcode, void* streamHandle, byte* payload, uint length, void* userData)
    {
        try
        {
            GCHandle handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is not OpcodeHandlerWrapper wrapper)
            {
                return 0;
            }

            using var _ = wrapper.Bridge.EnterNativeGameThreadScope(captureGameThreadId: true);

            if (wrapper.StreamHandler is not null && streamHandle is not null)
            {
                byte[] preloaded = [];
                if (payload is not null && length > 0)
                {
                    preloaded = new byte[length];
                    Marshal.Copy((nint)payload, preloaded, 0, (int)length);
                }

                var stream = new NativePacketStream(wrapper.Bridge, (nint)streamHandle);
                return wrapper.StreamHandler(opcode, stream, preloaded) ? 1 : 0;
            }

            if (wrapper.ByteHandler is not null)
            {
                byte[] bytes;
                if (payload is not null)
                {
                    bytes = new byte[length];
                    if (length > 0)
                    {
                        Marshal.Copy((nint)payload, bytes, 0, (int)length);
                    }
                }
                else if (streamHandle is not null && wrapper.Bridge._table->StreamRead is not null)
                {
                    ulong remaining = ReadRemainingStreamBytes(wrapper.Bridge, streamHandle);
                    if (remaining > 1048576ul)
                    {
                        return 0;
                    }

                    bytes = new byte[remaining];
                    if (remaining > 0 && !wrapper.Bridge.StreamRead((nint)streamHandle, bytes))
                    {
                        return 0;
                    }
                }
                else
                {
                    bytes = [];
                }

                wrapper.ByteHandler(opcode, bytes);
                return 1;
            }
        }
        catch
        {
            // Exception wall across native MessagePump.
        }

        return 0;
    }

    private static ulong ReadRemainingStreamBytes(NativeShimBridge bridge, void* streamHandle)
    {
        StreamRemainingProbeContext probe = new() { StreamHandle = streamHandle, Remaining = 0 };
        if (bridge._table->CallGuarded is not null)
        {
            long rc = 0;
            NmExceptionInfo ex = default;
            if (bridge._table->CallGuarded(&ProbeStreamRemainingTrampoline, &probe, &rc, &ex) == NmStatus.Ok)
            {
                return probe.Remaining;
            }

            return 0;
        }

        ProbeStreamRemainingCore(&probe);
        return probe.Remaining;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeHookChain(NmHookContext* nativeCtx, void* userData)
    {
        if (nativeCtx is null)
        {
            return;
        }

        bool prev = s_inNativeGameFrame;
        s_inNativeGameFrame = true;
        try
        {
            GCHandle handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is not HookChainWrapper wrapper)
            {
                return;
            }

            var managedCtx = new HookContext(unchecked((nint)nativeCtx->TargetAddress))
            {
                ReturnValue = nativeCtx->ReturnValue,
                ReturnFloatBits = nativeCtx->ReturnFloat,
                SkipOriginal = nativeCtx->SkipOriginal != 0,
            };
            for (int i = 0; i < 8; i++)
            {
                managedCtx.Args[i] = nativeCtx->Args[i];
            }

            for (int i = 0; i < 4; i++)
            {
                managedCtx.FloatArgs[i] = nativeCtx->FloatArgs[i];
            }

            wrapper.Callback(managedCtx);

            for (int i = 0; i < 8; i++)
            {
                nativeCtx->Args[i] = managedCtx.Args[i];
            }

            for (int i = 0; i < 4; i++)
            {
                nativeCtx->FloatArgs[i] = managedCtx.FloatArgs[i];
            }

            nativeCtx->ReturnValue = managedCtx.ReturnValue;
            nativeCtx->ReturnFloat = managedCtx.ReturnFloatBits;
            nativeCtx->SkipOriginal = managedCtx.SkipOriginal ? (byte)1 : (byte)0;
        }
        catch
        {
            // Exception wall across native hook dispatcher.
        }
        finally
        {
            s_inNativeGameFrame = prev;
        }
    }

    internal static string NormalizeAndValidateVirtualPath(string virtualPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualPath);
        string[] rawSegments = virtualPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (string seg in rawSegments)
        {
            if (seg == "..")
            {
                throw new ArgumentException($"Virtual path '{virtualPath}' contains forbidden '..' traversal segment.", nameof(virtualPath));
            }
        }

        return ClientTextEncoding.NormalizeVirtualPath(virtualPath);
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
