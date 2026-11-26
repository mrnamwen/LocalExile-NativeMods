using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LocalExile.Client.Sdk.Addresses;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.Sdk.Raw;

namespace LocalExile.Client.Host.Native;

/// <summary>
/// Full in-memory deterministic 3.29 client simulation implementing <see cref="INativeBridge"/>.
/// Supports address verification, simulated memory allocation, inline/vtable hook tracking,
/// frame ticking, file overlay resolution, GEAL script/event invocation, audio/stats/world state,
/// and bidirectional packet/chat loopback for automated tests without a Windows client process.
/// </summary>
public sealed class SimulatedNativeBridge : INativeBridge
{
    private const int MaxQueueCapacity = 4096;

    private sealed record SimulatedChainHookEntry(
        ulong Handle,
        string TargetKey,
        HookPhase Phase,
        int Priority,
        Action<HookContext> Callback);

    private sealed record SimulatedGroupHandlerEntry(
        ushort MinOpcode,
        ushort MaxOpcode,
        Action<ushort, ReadOnlyMemory<byte>>? ByteHandler,
        Func<ushort, INativePacketStream, ReadOnlyMemory<byte>, bool>? StreamHandler);

    private sealed class SimulatedPacketStream(ReadOnlyMemory<byte> payload) : INativePacketStream
    {
        private int _cursor;

        public bool TryRead(Span<byte> destination)
        {
            if (_cursor + destination.Length > payload.Length)
            {
                return false;
            }

            payload.Span.Slice(_cursor, destination.Length).CopyTo(destination);
            _cursor += destination.Length;
            return true;
        }

        public byte[] ReadBytes(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            byte[] buf = new byte[count];
            if (!TryRead(buf))
            {
                throw new EndOfStreamException($"Failed to read {count} byte(s) from simulated packet stream.");
            }

            return buf;
        }

        public T Read<T>() where T : unmanaged
        {
            Span<byte> buf = stackalloc byte[Unsafe.SizeOf<T>()];
            if (!TryRead(buf))
            {
                throw new EndOfStreamException($"Failed to read {typeof(T).Name} from simulated packet stream.");
            }

            return MemoryMarshal.Read<T>(buf);
        }
    }

    private readonly struct GameThreadScope(bool prev) : IDisposable
    {
        public void Dispose() => s_inNativeGameFrame = prev;
    }

    [ThreadStatic]
    private static bool s_inNativeGameFrame;

    private readonly Dictionary<string, AddressEntry> _symbols = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<ReadOnlySpan<nint>, nint>> _nativeCallStubs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<nint, Func<ReadOnlySpan<NativeCallArgument>, bool, NativeCallResult>> _nativeExCallStubs = [];
    private readonly Dictionary<ulong, (string Symbol, nint Detour)> _activeHooks = [];
    private readonly List<SimulatedChainHookEntry> _chainHooks = [];
    private readonly List<Action<float>> _frameCallbacks = [];
    private readonly ConcurrentQueue<Action> _mainThreadQueue = new();
    private int _queuedCount;
    private readonly List<Func<string, byte[]?>> _overlayResolvers = [];
    private readonly Dictionary<string, byte[]> _stockFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _invalidatedPaths = [];
    private HashSet<string> _knownOverlayPaths = new(StringComparer.Ordinal);
    private readonly Dictionary<byte, List<Action<ReadOnlyMemory<byte>>>> _rulesetReceivers = [];
    private readonly List<Func<string, string, bool>> _chatInterceptors = [];
    private readonly Dictionary<ushort, List<Action<ReadOnlyMemory<byte>>>> _rawOpcodeReceivers = [];
    private readonly List<SimulatedGroupHandlerEntry> _rawOpcodeGroups = [];
    private readonly Dictionary<int, (string PathOrEvent, bool IsFmod, float Volume, bool Loop, bool Active)> _audioChannels = [];
    private readonly Dictionary<string, int> _stats = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, WorldObjectInfo> _worldObjects = [];
    private readonly List<(uint ObjectId, string EffectPath)> _playedEffects = [];
    private readonly Dictionary<(uint ObjectId, string EntryName, bool IsEvent), Func<GealArguments, GealValue>> _gealObjectHandlers = [];
    private readonly List<nint> _allocatedBuffers = [];
    private readonly Dictionary<string, nint> _nativeUiWindowsByPath = new(StringComparer.Ordinal);
    private readonly Dictionary<(nint Parent, string Key), nint> _nativeUiChildren = [];
    private readonly Dictionary<nint, SimulatedNativeUiControl> _nativeUiControls = [];
    private readonly Dictionary<GamePanel, nint> _simulatedPanelHandles = [];
    private readonly Dictionary<ScreenSlot, nint> _simulatedSlotParentHandles = [];
    private readonly List<Action<nint, IReadOnlyList<LeagueCardInfo>>> _leagueFillCallbacks = [];
    private readonly List<Func<KeyEventArgs, bool>> _keyCallbacks = [];
    private readonly List<Func<MouseEventArgs, bool>> _mouseCallbacks = [];
    private readonly List<Func<string, string, string, string?>> _charCreatingCallbacks = [];
    private readonly List<Action<string, string, ushort>> _charCreatedCallbacks = [];
    private readonly nint _simulatedUiRootHandle = 0x0F00;
    private readonly Lock _gate = new();

    private PreGameScreen _simulatedPreGameScreen = PreGameScreen.Unknown;
    private nint _simulatedInGameUiHandle;
    private GamePanel _simulatedActivePanels = GamePanel.None;
    private StashTabInfo? _simulatedActiveStashTab;
    private bool _simulatedMinimapVisible;
    private bool _simulatedRightSidePanelOpen;
    private bool _simulatedTextInputFocused;
    private nint _simulatedHoveredControlHandle;
    private HoveredItemInfo? _simulatedHoveredItem;
    private CursorItemInfo? _simulatedCursorItem;
    private string? _clipboardText;
    private int _gameThreadId;
    private ulong _nextHookHandle = 1;
    private int _nextAudioChannel = 1;
    private uint _nextClientObjectId = 0x8000_0001u;
    private long _nextNativeUiHandle = 0x1000;
    private bool _disposed;

    /// <summary>Snapshot of simulated native 3.29 UI control state (when <see cref="EnableSimulatedNativeUi"/> is <c>true</c>).</summary>
    public sealed class SimulatedNativeUiControl
    {
        public required nint Handle { get; init; }
        public nint ParentHandle { get; set; }
        public required string Key { get; init; }
        public ulong VtableAddress { get; set; }
        public uint Tag { get; set; }
        public bool IsStockControl { get; set; }
        public bool AutoWidth { get; set; }
        public byte XAnchor { get; set; }
        public float XAnchorOffset { get; set; }
        public byte YAnchor { get; set; }
        public float YAnchorOffset { get; set; }
        public int LayoutUpdateCount { get; set; }
        public bool Visible { get; set; } = true;
        public bool Enabled { get; set; } = true;
        public UiRect Rect { get; set; }
        public string Text { get; set; } = string.Empty;
        public bool IsChecked { get; set; }
        public bool? LastSetTextIsButton { get; set; }
        public int SetTextCallCount { get; set; }
        public int SetVisibleCallCount { get; set; }
        public int NativeSubscribeCount { get; set; }
        public Action? ActiveClickHandler { get; set; }
        public Action? ActiveTextChangedHandler { get; set; }
        public Action? ActiveSubmittedHandler { get; set; }
        public Action? ActiveCheckedChangedHandler { get; set; }
        public bool Destroyed { get; set; }
    }

    /// <summary>
    /// Enables deterministic simulation of the native 3.29 UI window/child/visibility/text/click bridge
    /// (window caching by normalized path, per-button click slot replacement, ancestor visibility checks).
    /// </summary>
    public bool EnableSimulatedNativeUi { get; set; }

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

    private GameThreadScope EnterSimulatedGameThreadScope(bool captureGameThreadId)
    {
        if (captureGameThreadId && Volatile.Read(ref _gameThreadId) == 0)
        {
            Interlocked.CompareExchange(ref _gameThreadId, Environment.CurrentManagedThreadId, 0);
        }

        bool prev = s_inNativeGameFrame;
        s_inNativeGameFrame = true;
        return new GameThreadScope(prev);
    }

    /// <summary>Callback invoked whenever the client sends a C→S <c>0x2ca</c> ruleset packet.</summary>
    public Action<byte, ReadOnlyMemory<byte>>? OnOutgoingRulesetPacket { get; set; }

    /// <summary>Callback invoked whenever the client sends a C→S <c>0x15</c> chat command line.</summary>
    public Action<string>? OnOutgoingChatLine { get; set; }

    /// <summary>Callback invoked whenever the client sends a raw packet.</summary>
    public Action<ushort, ReadOnlyMemory<byte>>? OnOutgoingRawPacket { get; set; }

    /// <summary>Recorded virtual paths passed to <see cref="InvalidateCachedAsset"/>.</summary>
    public IReadOnlyList<string> InvalidatedPaths
    {
        get { lock (_gate) { return [.. _invalidatedPaths]; } }
    }

    /// <summary>Recorded effects played via <see cref="PlayEffect"/>.</summary>
    public IReadOnlyList<(uint ObjectId, string EffectPath)> PlayedEffects
    {
        get { lock (_gate) { return [.. _playedEffects]; } }
    }

    /// <summary>Recorded outgoing chat lines sent by the client.</summary>
    public List<string> SentChatLines { get; } = [];

    /// <summary>Enables simulated instance-connection token tracking in <see cref="ClientHostRuntime"/>.</summary>
    public bool SimulateConnectionLifecycle { get; set; }

    /// <summary>Active simulated instance connection token (<c>0</c> = disconnected).</summary>
    public ulong ActiveConnectionToken { get; set; }

    /// <inheritdoc/>
    public bool SupportsConnectionToken => SimulateConnectionLifecycle || ActiveConnectionToken != 0;

    /// <inheritdoc/>
    public ulong GetActiveConnectionToken() => ActiveConnectionToken;

    /// <summary>Latest normalized overlay paths pushed by <see cref="Services.FileOverlayService"/>.</summary>
    public IReadOnlyCollection<string> KnownOverlayPaths
    {
        get { lock (_gate) { return [.. _knownOverlayPaths]; } }
    }

    /// <inheritdoc/>
    public void SetFileOverlayKnownPaths(IReadOnlyCollection<string> normalizedPaths)
    {
        ArgumentNullException.ThrowIfNull(normalizedPaths);
        lock (_gate)
        {
            _knownOverlayPaths = new HashSet<string>(normalizedPaths, StringComparer.Ordinal);
        }
    }

    /// <summary>Creates a simulated 3.29 client bridge with verified default 3.29.3d symbols.</summary>
    public SimulatedNativeBridge(
        ClientBuildId build = ClientBuildId.Build3293d,
        string? buildVersion = null,
        string? executableSha256 = null,
        nint? imageBase = null,
        IEnumerable<AddressEntry>? symbols = null)
    {
        DetectedBuild = build;
        BuildVersion = buildVersion ?? build switch
        {
            ClientBuildId.Build3293d => "3.29.3d",
            ClientBuildId.Build3293c => "3.29.3c",
            _ => "unknown",
        };
        ExecutableSha256 = executableSha256 ?? (build == ClientBuildId.Build3293d
            ? "e9ba3d31c4d922e1826ef01d02fd22178a001b29e17264b68fc2446efd41f53d"
            : build == ClientBuildId.Build3293c
                ? "cc8aa38345be2a837575215d2ce142ebecf7b007345843265b335ebb8ac5964c"
                : null);
        ImageBase = imageBase ?? unchecked((nint)NativeShimConstants.NominalImageBase);
        Volatile.Write(ref _gameThreadId, Environment.CurrentManagedThreadId);
        _nativeUiControls[_simulatedUiRootHandle] = new SimulatedNativeUiControl
        {
            Handle = _simulatedUiRootHandle,
            ParentHandle = nint.Zero,
            Key = "__root__",
            Visible = true,
        };

        foreach (AddressEntry entry in symbols ?? CreateDefaultSymbols(build))
        {
            _symbols[entry.Name] = entry;
        }
    }

    /// <summary>Returns all registered symbol names in the simulated address table.</summary>
    public IReadOnlyCollection<string> GetRegisteredSymbolNames()
    {
        lock (_gate)
        {
            return [.. _symbols.Keys];
        }
    }

    /// <summary>Binds the current managed thread as the simulated game thread.</summary>
    public void BindCurrentThreadAsGameThread() => Volatile.Write(ref _gameThreadId, Environment.CurrentManagedThreadId);

    /// <summary>Clears the captured simulated game thread ID (for testing off-thread bootstrap behavior).</summary>
    public void ClearGameThreadBinding() => Volatile.Write(ref _gameThreadId, 0);

    /// <summary>Adds or replaces a symbol entry in the simulated address table.</summary>
    public void RegisterSymbol(AddressEntry entry)
    {
        lock (_gate)
        {
            _symbols[entry.Name] = entry;
        }
    }

    /// <summary>Registers a stub handler invoked when <see cref="InvokeNative"/> is called for <paramref name="symbolName"/>.</summary>
    public void RegisterNativeStub(string symbolName, Func<ReadOnlySpan<nint>, nint> stub)
    {
        lock (_gate)
        {
            _nativeCallStubs[symbolName] = stub;
        }
    }

    /// <summary>Registers a stub handler invoked when <see cref="InvokeNativeEx"/> is called for <paramref name="targetAddress"/>.</summary>
    public void RegisterNativeExStub(nint targetAddress, Func<ReadOnlySpan<NativeCallArgument>, bool, NativeCallResult> stub)
    {
        lock (_gate)
        {
            _nativeExCallStubs[targetAddress] = stub;
        }
    }

    /// <summary>Allocates a real unmanaged memory buffer tracked and freed on disposal (for safe <c>IRawMemory</c> tests).</summary>
    public unsafe nint AllocateSimulatedMemory(int byteCount)
    {
        void* ptr = NativeMemory.AllocZeroed((nuint)Math.Max(1, byteCount));
        nint addr = (nint)ptr;
        lock (_gate)
        {
            _allocatedBuffers.Add(addr);
        }

        return addr;
    }

    /// <summary>Adds a simulated stock archive file served when no overlay matches.</summary>
    public void AddStockFile(string virtualPath, ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            _stockFiles[ClientTextEncoding.NormalizeVirtualPath(virtualPath)] = bytes.ToArray();
        }
    }

    /// <summary>Adds a simulated stock text file (encoded per PoE 3.29 client rules: UTF-16LE BOM/no-BOM CRLF or UTF-8).</summary>
    public void AddStockTextFile(string virtualPath, string text) =>
        AddStockFile(virtualPath, ClientTextEncoding.EncodeTextForPath(virtualPath, text));

    /// <summary>Adds a simulated server-replicated world object.</summary>
    public void AddServerWorldObject(WorldObjectInfo obj)
    {
        lock (_gate)
        {
            _worldObjects[obj.ObjectId] = obj;
        }
    }

    /// <summary>Registers a simulated GEAL <c>Functions</c> entry or <c>on_xxx</c> event handler on an object.</summary>
    public void RegisterObjectGealHandler(uint objectId, string entryName, bool isEvent, Func<GealArguments, GealValue> handler)
    {
        lock (_gate)
        {
            _gealObjectHandlers[(objectId, entryName.ToLowerInvariant(), isEvent)] = handler;
        }
    }

    /// <summary>Resolves a virtual path through registered overlays first, then simulated stock files.</summary>
    public byte[]? ResolveFileThroughOverlays(string virtualPath)
    {
        string normalized = ClientTextEncoding.NormalizeVirtualPath(virtualPath);
        Func<string, byte[]?>[] resolvers;
        lock (_gate)
        {
            resolvers = [.. _overlayResolvers];
        }

        foreach (Func<string, byte[]?> resolver in resolvers)
        {
            byte[]? result = resolver(normalized);
            if (result is not null)
            {
                return result;
            }
        }

        return ReadStockGameFile(normalized);
    }

    /// <summary>Advances the simulated client by one frame, draining queued work and invoking frame callbacks.</summary>
    public void SimulateFrame(float deltaSeconds = 1f / 60f)
    {
        using var _ = EnterSimulatedGameThreadScope(captureGameThreadId: true);
        DrainGameThreadQueue(deltaSeconds);

        Action<float>[] callbacks;
        lock (_gate)
        {
            callbacks = [.. _frameCallbacks];
        }

        foreach (Action<float> cb in callbacks)
        {
            cb(deltaSeconds);
        }
    }

    /// <summary>Delivers a simulated S→C <c>0x2c9</c> ruleset payload into registered receivers.</summary>
    public bool SimulateIncomingRulesetPacket(byte rulesetId, ReadOnlyMemory<byte> payload)
    {
        Action<ReadOnlyMemory<byte>>[] receivers;
        lock (_gate)
        {
            if (!_rulesetReceivers.TryGetValue(rulesetId, out List<Action<ReadOnlyMemory<byte>>>? list) || list.Count == 0)
            {
                return false;
            }

            receivers = [.. list];
        }

        using var _ = EnterSimulatedGameThreadScope(captureGameThreadId: true);
        foreach (Action<ReadOnlyMemory<byte>> rx in receivers)
        {
            rx(payload);
        }

        return true;
    }

    /// <summary>Delivers a simulated S→C <c>0x17</c> chat line to registered interceptors. Returns <c>true</c> if suppressed.</summary>
    public bool SimulateIncomingChat(string sender, string text)
    {
        Func<string, string, bool>[] interceptors;
        lock (_gate)
        {
            interceptors = [.. _chatInterceptors];
        }

        bool suppress = false;
        using var _ = EnterSimulatedGameThreadScope(captureGameThreadId: true);
        foreach (Func<string, string, bool> interceptor in interceptors)
        {
            try
            {
                if (interceptor(sender, text))
                {
                    suppress = true;
                }
            }
            catch (Exception ex)
            {
                // Exception wall: fail-open (do not suppress stock chat or abort subsequent interceptors).
                System.Diagnostics.Trace.TraceWarning($"[SimulatedNativeBridge] Chat interceptor faulted and failed open: {ex.Message}");
            }
        }

        return suppress;
    }

    /// <summary>Delivers a simulated incoming raw opcode payload.</summary>
    public bool SimulateIncomingRawPacket(ushort opcode, ReadOnlyMemory<byte> payload)
    {
        Action<ReadOnlyMemory<byte>>[] receivers;
        SimulatedGroupHandlerEntry[] groups;
        lock (_gate)
        {
            receivers = _rawOpcodeReceivers.TryGetValue(opcode, out List<Action<ReadOnlyMemory<byte>>>? list) && list.Count > 0
                ? [.. list]
                : [];
            groups = [.. _rawOpcodeGroups.Where(g => opcode >= g.MinOpcode && opcode <= g.MaxOpcode)];
        }

        if (receivers.Length == 0 && groups.Length == 0)
        {
            return false;
        }

        using var _ = EnterSimulatedGameThreadScope(captureGameThreadId: true);
        foreach (Action<ReadOnlyMemory<byte>> rx in receivers)
        {
            rx(payload);
        }

        bool anyHandled = receivers.Length > 0;
        foreach (SimulatedGroupHandlerEntry g in groups)
        {
            if (g.StreamHandler is not null)
            {
                if (g.StreamHandler(opcode, new SimulatedPacketStream(payload), payload))
                {
                    anyHandled = true;
                }
            }
            else if (g.ByteHandler is not null)
            {
                g.ByteHandler(opcode, payload);
                anyHandled = true;
            }
        }

        return anyHandled;
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<AddressEntry> EnumerateSymbols()
    {
        lock (_gate)
        {
            return [.. _symbols.Values];
        }
    }

    /// <inheritdoc/>
    public bool ResolveSymbol(string name, out ulong rva, out nint address)
    {
        rva = 0;
        address = nint.Zero;
        lock (_gate)
        {
            if (!_symbols.TryGetValue(name, out AddressEntry? entry) || !entry.IsVerified || !entry.Rva.HasValue)
            {
                return false;
            }

            rva = entry.Rva.Value;
            address = unchecked((nint)((ulong)ImageBase + rva));
            return true;
        }
    }

    /// <inheritdoc/>
    public bool VerifyExpectedBytes(nint address, string expectedBytesHex)
    {
        if (string.IsNullOrWhiteSpace(expectedBytesHex))
        {
            return false;
        }

        string cleaned = expectedBytesHex.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (cleaned.Length == 0 || (cleaned.Length & 1) != 0)
        {
            return false;
        }

        // If the address points into one of our real simulated allocations, compare actual bytes.
        lock (_gate)
        {
            foreach (AddressEntry sym in _symbols.Values)
            {
                if (sym.IsVerified && sym.Rva.HasValue && unchecked((nint)((ulong)ImageBase + sym.Rva.Value)) == address)
                {
                    if (sym.ExpectedBytes is null)
                    {
                        return false;
                    }

                    return MatchesWildcardHex(sym.ExpectedBytes, cleaned);
                }
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public ulong InstallInlineHook(string symbolName, nint detourTarget, out nint trampoline)
    {
        if (!ResolveSymbol(symbolName, out _, out nint addr))
        {
            throw new KeyNotFoundException($"Cannot install hook: symbol '{symbolName}' is missing or unverified.");
        }

        lock (_gate)
        {
            ulong handle = _nextHookHandle++;
            _activeHooks[handle] = (symbolName, detourTarget);
            trampoline = addr;
            return handle;
        }
    }

    /// <inheritdoc/>
    public ulong InstallVtableHook(nint vtableAddress, uint slotIndex, nint detourTarget, out nint originalSlot)
    {
        lock (_gate)
        {
            ulong handle = _nextHookHandle++;
            _activeHooks[handle] = ($"vtable:0x{(ulong)vtableAddress:X}[{slotIndex}]", detourTarget);
            originalSlot = vtableAddress + (nint)(slotIndex * 8);
            return handle;
        }
    }

    /// <inheritdoc/>
    public ulong InstallInlineHookChain(string symbolName, HookPhase phase, int priority, Action<HookContext> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (!ResolveSymbol(symbolName, out _, out nint addr))
        {
            throw new KeyNotFoundException($"Cannot install hook chain: symbol '{symbolName}' is missing or unverified.");
        }

        lock (_gate)
        {
            ulong handle = _nextHookHandle++;
            string targetKey = $"addr:0x{(ulong)addr:X}";
            _activeHooks[handle] = ($"chain:{symbolName}", addr);
            _chainHooks.Add(new SimulatedChainHookEntry(handle, targetKey, phase, priority, callback));
            return handle;
        }
    }

    /// <inheritdoc/>
    public ulong InstallInlineHookChainAt(nint address, string? expectedBytesHex, HookPhase phase, int priority, Action<HookContext> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (address == nint.Zero)
        {
            throw new ArgumentException("Cannot install hook chain at null address.", nameof(address));
        }

        if (!string.IsNullOrWhiteSpace(expectedBytesHex) && !VerifyExpectedBytes(address, expectedBytesHex))
        {
            throw new InvalidOperationException($"Byte verification failed at 0x{(ulong)address:X}.");
        }

        lock (_gate)
        {
            ulong handle = _nextHookHandle++;
            string targetKey = $"addr:0x{(ulong)address:X}";
            _activeHooks[handle] = ($"chain:{targetKey}", address);
            _chainHooks.Add(new SimulatedChainHookEntry(handle, targetKey, phase, priority, callback));
            return handle;
        }
    }

    /// <inheritdoc/>
    public ulong InstallVtableHookChain(nint vtableAddress, uint slotIndex, HookPhase phase, int priority, Action<HookContext> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (vtableAddress == nint.Zero)
        {
            throw new ArgumentException("Cannot install vtable hook chain at null vtable.", nameof(vtableAddress));
        }

        lock (_gate)
        {
            ulong handle = _nextHookHandle++;
            string targetKey = $"vtable:0x{(ulong)vtableAddress:X}[{slotIndex}]";
            _activeHooks[handle] = ($"chain:{targetKey}", vtableAddress + (nint)(slotIndex * 8));
            _chainHooks.Add(new SimulatedChainHookEntry(handle, targetKey, phase, priority, callback));
            return handle;
        }
    }

    /// <summary>Executes simulated pre/post hook chain callbacks for <paramref name="targetAddress"/> in deterministic priority order.</summary>
    public HookContext SimulateHookChainCall(nint targetAddress, HookContext context, Func<HookContext, ulong>? originalFunc = null)
    {
        return SimulateHookChainCallByKey($"addr:0x{(ulong)targetAddress:X}", context, originalFunc);
    }

    /// <summary>Executes simulated pre/post vtable hook chain callbacks in deterministic priority order.</summary>
    public HookContext SimulateVtableHookChainCall(nint vtableAddress, uint slotIndex, HookContext context, Func<HookContext, ulong>? originalFunc = null)
    {
        return SimulateHookChainCallByKey($"vtable:0x{(ulong)vtableAddress:X}[{slotIndex}]", context, originalFunc);
    }

    private HookContext SimulateHookChainCallByKey(string targetKey, HookContext context, Func<HookContext, ulong>? originalFunc)
    {
        SimulatedChainHookEntry[] preHooks;
        SimulatedChainHookEntry[] postHooks;
        lock (_gate)
        {
            preHooks =
            [
                .. _chainHooks
                    .Where(h => h.TargetKey == targetKey && h.Phase == HookPhase.Pre)
                    .OrderBy(h => h.Priority)
                    .ThenBy(h => h.Handle),
            ];
            postHooks =
            [
                .. _chainHooks
                    .Where(h => h.TargetKey == targetKey && h.Phase == HookPhase.Post)
                    .OrderBy(h => h.Priority)
                    .ThenBy(h => h.Handle),
            ];
        }

        using var _ = EnterSimulatedGameThreadScope(captureGameThreadId: false);
        foreach (SimulatedChainHookEntry entry in preHooks)
        {
            entry.Callback(context);
        }

        if (!context.SkipOriginal && originalFunc is not null)
        {
            context.ReturnValue = originalFunc(context);
        }

        foreach (SimulatedChainHookEntry entry in postHooks)
        {
            entry.Callback(context);
        }

        return context;
    }

    /// <inheritdoc/>
    public bool RemoveHook(ulong hookHandle)
    {
        lock (_gate)
        {
            _chainHooks.RemoveAll(h => h.Handle == hookHandle);
            return _activeHooks.Remove(hookHandle);
        }
    }

    /// <summary>Gets the count of currently active simulated hooks.</summary>
    public int ActiveHookCount
    {
        get { lock (_gate) { return _activeHooks.Count; } }
    }

    /// <inheritdoc/>
    public unsafe void ReadMemory(nint address, Span<byte> destination)
    {
        if (address == nint.Zero)
        {
            throw new ArgumentException("Cannot read from null address.", nameof(address));
        }

        new ReadOnlySpan<byte>((void*)address, destination.Length).CopyTo(destination);
    }

    /// <inheritdoc/>
    public unsafe void WriteMemory(nint address, ReadOnlySpan<byte> data)
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
        Func<ReadOnlySpan<nint>, nint>? stub;
        lock (_gate)
        {
            _nativeCallStubs.TryGetValue(symbolName, out stub);
        }

        if (stub is not null)
        {
            return stub(args);
        }

        if (!ResolveSymbol(symbolName, out _, out _))
        {
            throw new KeyNotFoundException($"Verified symbol '{symbolName}' not found.");
        }

        return nint.Zero;
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

        Func<ReadOnlySpan<NativeCallArgument>, bool, NativeCallResult>? stub;
        lock (_gate)
        {
            _nativeExCallStubs.TryGetValue(targetAddress, out stub);
        }

        if (stub is not null)
        {
            return stub(args, returnIsFloat);
        }

        return default;
    }

    /// <inheritdoc/>
    public IDisposable RegisterFrameCallback(Action<float> onFrame)
    {
        lock (_gate)
        {
            _frameCallbacks.Add(onFrame);
        }

        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                _frameCallbacks.Remove(onFrame);
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

        while (true)
        {
            int current = Volatile.Read(ref _queuedCount);
            if (current >= MaxQueueCapacity)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _queuedCount, current + 1, current) == current)
            {
                break;
            }
        }

        _mainThreadQueue.Enqueue(action);
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

        int count = 0;
        using var _ = EnterSimulatedGameThreadScope(captureGameThreadId: true);
        while (count < maxItems && _mainThreadQueue.TryDequeue(out Action? work))
        {
            Interlocked.Decrement(ref _queuedCount);
            work();
            count++;
        }

        return count;
    }

    /// <inheritdoc/>
    public IDisposable RegisterFileOverlayResolver(Func<string, byte[]?> resolver)
    {
        lock (_gate)
        {
            _overlayResolvers.Add(resolver);
        }

        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                _overlayResolvers.Remove(resolver);
            }
        });
    }

    /// <inheritdoc/>
    public byte[]? ReadStockGameFile(string virtualPath)
    {
        string normalized = NativeShimBridge.NormalizeAndValidateVirtualPath(virtualPath);
        lock (_gate)
        {
            return _stockFiles.TryGetValue(normalized, out byte[]? data) ? (byte[])data.Clone() : null;
        }
    }

    /// <inheritdoc/>
    public void InvalidateCachedAsset(string virtualPath)
    {
        string normalized = ClientTextEncoding.NormalizeVirtualPath(virtualPath);
        lock (_gate)
        {
            _invalidatedPaths.Add(normalized);
        }
    }

    /// <inheritdoc/>
    public IDisposable RegisterRulesetReceiver(byte rulesetId, Action<ReadOnlyMemory<byte>> onPayload)
    {
        lock (_gate)
        {
            if (!_rulesetReceivers.TryGetValue(rulesetId, out List<Action<ReadOnlyMemory<byte>>>? list))
            {
                list = [];
                _rulesetReceivers[rulesetId] = list;
            }

            list.Add(onPayload);
        }

        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                if (_rulesetReceivers.TryGetValue(rulesetId, out List<Action<ReadOnlyMemory<byte>>>? list))
                {
                    list.Remove(onPayload);
                }
            }
        });
    }

    /// <inheritdoc/>
    public void SendRulesetPacket(byte rulesetId, ReadOnlySpan<byte> payload)
    {
        byte[] copy = payload.ToArray();
        OnOutgoingRulesetPacket?.Invoke(rulesetId, copy);
    }

    /// <inheritdoc/>
    public IDisposable RegisterChatInterceptor(Func<string, string, bool> onIncomingChat)
    {
        lock (_gate)
        {
            _chatInterceptors.Add(onIncomingChat);
        }

        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                _chatInterceptors.Remove(onIncomingChat);
            }
        });
    }

    /// <inheritdoc/>
    public void SendChatLine(string text)
    {
        lock (_gate)
        {
            SentChatLines.Add(text);
        }

        OnOutgoingChatLine?.Invoke(text);
    }

    /// <inheritdoc/>
    public IDisposable RegisterRawOpcodeReceiver(ushort opcode, Action<ReadOnlyMemory<byte>> onPayload)
    {
        lock (_gate)
        {
            if (!_rawOpcodeReceivers.TryGetValue(opcode, out List<Action<ReadOnlyMemory<byte>>>? list))
            {
                list = [];
                _rawOpcodeReceivers[opcode] = list;
            }

            list.Add(onPayload);
        }

        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                if (_rawOpcodeReceivers.TryGetValue(opcode, out List<Action<ReadOnlyMemory<byte>>>? list))
                {
                    list.Remove(onPayload);
                }
            }
        });
    }

    /// <inheritdoc/>
    public IDisposable RegisterRawOpcodeGroup(ushort minOpcode, ushort maxOpcode, Action<ushort, ReadOnlyMemory<byte>> onPayload)
    {
        ArgumentNullException.ThrowIfNull(onPayload);
        var entry = new SimulatedGroupHandlerEntry(minOpcode, maxOpcode, onPayload, null);
        lock (_gate)
        {
            _rawOpcodeGroups.Add(entry);
        }

        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                _rawOpcodeGroups.Remove(entry);
            }
        });
    }

    /// <inheritdoc/>
    public IDisposable RegisterRawOpcodeStreamHandler(ushort minOpcode, ushort maxOpcode, Func<ushort, INativePacketStream, ReadOnlyMemory<byte>, bool> onStream)
    {
        ArgumentNullException.ThrowIfNull(onStream);
        var entry = new SimulatedGroupHandlerEntry(minOpcode, maxOpcode, null, onStream);
        lock (_gate)
        {
            _rawOpcodeGroups.Add(entry);
        }

        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                _rawOpcodeGroups.Remove(entry);
            }
        });
    }

    /// <inheritdoc/>
    public bool StreamRead(nint streamHandle, Span<byte> destination) => false;

    /// <inheritdoc/>
    public void SendRawPacket(ushort opcode, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 65536)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), payload.Length, "Raw packet payload exceeds 65536 bytes.");
        }

        OnOutgoingRawPacket?.Invoke(opcode, payload.ToArray());
    }

    /// <inheritdoc/>
    public GealInvocationResult InvokeGeal(uint objectId, string entryName, bool isEvent, ReadOnlySpan<GealValue> args)
    {
        Func<GealArguments, GealValue>? handler;
        lock (_gate)
        {
            _gealObjectHandlers.TryGetValue((objectId, entryName.ToLowerInvariant(), isEvent), out handler);
        }

        if (handler is null)
        {
            return new GealInvocationResult(false, GealValue.Null, $"No GEAL {(isEvent ? "event" : "function")} '{entryName}' bound on object 0x{objectId:X}.");
        }

        GealValue ret = handler(new GealArguments(args.ToArray()));
        return new GealInvocationResult(true, ret);
    }

    /// <inheritdoc/>
    public int PlayAudio(string pathOrEvent, bool isFmodEvent, float volume) =>
        PlayAudio(pathOrEvent, isFmodEvent, volume, loop: false);

    /// <inheritdoc/>
    public int PlayAudio(string pathOrEvent, bool isFmodEvent, float volume, bool loop)
    {
        lock (_gate)
        {
            int handle = _nextAudioChannel++;
            _audioChannels[handle] = (pathOrEvent, isFmodEvent, volume, loop, true);
            return handle;
        }
    }

    /// <inheritdoc/>
    public void SetAudioVolume(int channelHandle, float volume)
    {
        lock (_gate)
        {
            if (_audioChannels.TryGetValue(channelHandle, out var entry))
            {
                _audioChannels[channelHandle] = entry with { Volume = Math.Clamp(volume, 0f, 1f) };
            }
        }
    }

    /// <inheritdoc/>
    public bool IsAudioPlaying(int channelHandle)
    {
        lock (_gate)
        {
            return _audioChannels.TryGetValue(channelHandle, out var entry) && entry.Active;
        }
    }

    /// <summary>Gets recorded audio channel metadata for test assertions.</summary>
    public bool TryGetSimulatedAudioChannel(int channelHandle, out string pathOrEvent, out bool isFmod, out float volume, out bool loop, out bool active)
    {
        lock (_gate)
        {
            if (_audioChannels.TryGetValue(channelHandle, out var entry))
            {
                pathOrEvent = entry.PathOrEvent;
                isFmod = entry.IsFmod;
                volume = entry.Volume;
                loop = entry.Loop;
                active = entry.Active;
                return true;
            }
        }

        pathOrEvent = string.Empty;
        isFmod = false;
        volume = 0f;
        loop = false;
        active = false;
        return false;
    }

    /// <inheritdoc/>
    public void StopAudio(int channelHandle)
    {
        lock (_gate)
        {
            if (_audioChannels.TryGetValue(channelHandle, out var entry))
            {
                _audioChannels[channelHandle] = entry with { Active = false };
            }
        }
    }

    /// <inheritdoc/>
    public nint GetNativeUiRoot() => EnableSimulatedNativeUi ? _simulatedUiRootHandle : nint.Zero;

    /// <inheritdoc/>
    public nint CreateNativeUiWindow(string virtualPath)
    {
        if (!EnableSimulatedNativeUi || string.IsNullOrWhiteSpace(virtualPath))
        {
            return nint.Zero;
        }

        string norm = ClientTextEncoding.NormalizeVirtualPath(virtualPath);
        lock (_gate)
        {
            if (_nativeUiWindowsByPath.TryGetValue(norm, out nint existing))
            {
                if (_nativeUiControls.TryGetValue(existing, out SimulatedNativeUiControl? ctrl) && !ctrl.Destroyed)
                {
                    ctrl.Visible = true;
                    ctrl.SetVisibleCallCount++;
                    return existing;
                }
            }

            nint handle = (nint)Interlocked.Increment(ref _nextNativeUiHandle);
            _nativeUiWindowsByPath[norm] = handle;
            _nativeUiControls[handle] = new SimulatedNativeUiControl
            {
                Handle = handle,
                ParentHandle = nint.Zero,
                Key = norm,
                Visible = true,
            };
            return handle;
        }
    }

    /// <inheritdoc/>
    public nint CreateNativeUiWindowInto(nint parentControl, string virtualPath)
    {
        if (!EnableSimulatedNativeUi || parentControl == nint.Zero || string.IsNullOrWhiteSpace(virtualPath))
        {
            return nint.Zero;
        }

        string norm = ClientTextEncoding.NormalizeVirtualPath(virtualPath);
        lock (_gate)
        {
            if (!_nativeUiControls.ContainsKey(parentControl))
            {
                return nint.Zero;
            }

            nint handle = (nint)Interlocked.Increment(ref _nextNativeUiHandle);
            _nativeUiControls[handle] = new SimulatedNativeUiControl
            {
                Handle = handle,
                ParentHandle = parentControl,
                Key = norm,
                Visible = true,
            };
            return handle;
        }
    }

    /// <inheritdoc/>
    public nint FindNativeUiChild(nint parentControl, string dotPath)
    {
        if (!EnableSimulatedNativeUi || parentControl == nint.Zero || string.IsNullOrWhiteSpace(dotPath))
        {
            return nint.Zero;
        }

        lock (_gate)
        {
            if (!_nativeUiControls.ContainsKey(parentControl))
            {
                return nint.Zero;
            }

            var key = (parentControl, dotPath.Trim());
            if (_nativeUiChildren.TryGetValue(key, out nint existing) &&
                _nativeUiControls.TryGetValue(existing, out SimulatedNativeUiControl? existingCtrl) &&
                !existingCtrl.Destroyed)
            {
                return existing;
            }

            // Walk dotted segments across existing native stock children first.
            string[] segments = key.Item2.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length > 0)
            {
                nint cur = parentControl;
                bool matchedAll = true;
                foreach (string seg in segments)
                {
                    SimulatedNativeUiControl? nextCtrl = _nativeUiControls.Values
                        .FirstOrDefault(c => !c.Destroyed && c.ParentHandle == cur && string.Equals(c.Key, seg, StringComparison.OrdinalIgnoreCase));
                    if (nextCtrl is null)
                    {
                        matchedAll = false;
                        break;
                    }

                    cur = nextCtrl.Handle;
                }

                if (matchedAll)
                {
                    return cur;
                }
            }

            // Stock controls (and root) do not hallucinate missing children.
            if (parentControl == _simulatedUiRootHandle ||
                (_nativeUiControls.TryGetValue(parentControl, out SimulatedNativeUiControl? parentCtrl) && parentCtrl.IsStockControl))
            {
                return nint.Zero;
            }

            nint handle = (nint)Interlocked.Increment(ref _nextNativeUiHandle);
            _nativeUiChildren[key] = handle;
            _nativeUiControls[handle] = new SimulatedNativeUiControl
            {
                Handle = handle,
                ParentHandle = parentControl,
                Key = key.Item2,
                Visible = true,
            };
            return handle;
        }
    }

    /// <inheritdoc/>
    public void SetNativeUiVisible(nint control, bool visible)
    {
        if (!EnableSimulatedNativeUi || control == nint.Zero)
        {
            return;
        }

        lock (_gate)
        {
            if (_nativeUiControls.TryGetValue(control, out SimulatedNativeUiControl? ctrl))
            {
                ctrl.Visible = visible;
                ctrl.SetVisibleCallCount++;
            }
        }
    }

    /// <inheritdoc/>
    public void SetNativeUiText(nint control, string text, bool isButton)
    {
        if (!EnableSimulatedNativeUi || control == nint.Zero)
        {
            return;
        }

        lock (_gate)
        {
            if (_nativeUiControls.TryGetValue(control, out SimulatedNativeUiControl? ctrl))
            {
                ctrl.Text = text ?? string.Empty;
                ctrl.LastSetTextIsButton = isButton;
                ctrl.SetTextCallCount++;
            }
        }
    }

    /// <inheritdoc/>
    public bool TryGetNativeUiRect(nint control, out UiRect rect)
    {
        rect = default;
        if (!EnableSimulatedNativeUi || control == nint.Zero)
        {
            return false;
        }

        lock (_gate)
        {
            if (_nativeUiControls.TryGetValue(control, out SimulatedNativeUiControl? ctrl) && !ctrl.Destroyed)
            {
                rect = ctrl.Rect;
                return true;
            }

            return false;
        }
    }

    /// <inheritdoc/>
    public void SetNativeUiPosition(nint control, float x, float y)
    {
        if (!EnableSimulatedNativeUi || control == nint.Zero)
        {
            return;
        }

        lock (_gate)
        {
            if (_nativeUiControls.TryGetValue(control, out SimulatedNativeUiControl? ctrl) && !ctrl.Destroyed)
            {
                ctrl.Rect = ctrl.Rect with { X = x, Y = y };
            }
        }
    }

    /// <inheritdoc/>
    public void SetNativeUiSize(nint control, float width, float height)
    {
        if (!EnableSimulatedNativeUi || control == nint.Zero)
        {
            return;
        }

        lock (_gate)
        {
            if (_nativeUiControls.TryGetValue(control, out SimulatedNativeUiControl? ctrl) && !ctrl.Destroyed)
            {
                ctrl.Rect = ctrl.Rect with { Width = width, Height = height };
            }
        }
    }

    /// <inheritdoc/>
    public void SetNativeUiEnabled(nint control, bool enabled)
    {
        if (!EnableSimulatedNativeUi || control == nint.Zero)
        {
            return;
        }

        lock (_gate)
        {
            if (_nativeUiControls.TryGetValue(control, out SimulatedNativeUiControl? ctrl) && !ctrl.Destroyed)
            {
                ctrl.Enabled = enabled;
            }
        }
    }

    /// <inheritdoc/>
    public bool TryGetNativeUiEnabled(nint control, out bool enabled)
    {
        enabled = false;
        if (!EnableSimulatedNativeUi || control == nint.Zero)
        {
            return false;
        }

        lock (_gate)
        {
            if (_nativeUiControls.TryGetValue(control, out SimulatedNativeUiControl? ctrl) && !ctrl.Destroyed)
            {
                enabled = ctrl.Enabled;
                return true;
            }

            return false;
        }
    }

    /// <inheritdoc/>
    public bool RemoveNativeUiChild(nint parentControl, nint childControl)
    {
        if (!EnableSimulatedNativeUi || parentControl == nint.Zero || childControl == nint.Zero)
        {
            return false;
        }

        lock (_gate)
        {
            if (_nativeUiControls.TryGetValue(childControl, out SimulatedNativeUiControl? ctrl) && ctrl.ParentHandle == parentControl)
            {
                ctrl.ParentHandle = nint.Zero;
                return true;
            }

            return false;
        }
    }

    /// <inheritdoc/>
    public void DestroyNativeUiControl(nint control)
    {
        if (!EnableSimulatedNativeUi || control == nint.Zero)
        {
            return;
        }

        lock (_gate)
        {
            if (_nativeUiControls.TryGetValue(control, out SimulatedNativeUiControl? ctrl))
            {
                ctrl.Destroyed = true;
                ctrl.Visible = false;
                ctrl.ParentHandle = nint.Zero;
                ctrl.ActiveClickHandler = null;
                ctrl.ActiveTextChangedHandler = null;
                ctrl.ActiveSubmittedHandler = null;
                ctrl.ActiveCheckedChangedHandler = null;
                _nativeUiControls.Remove(control);

                foreach (string k in _nativeUiWindowsByPath.Where(kv => kv.Value == control).Select(kv => kv.Key).ToArray())
                {
                    _nativeUiWindowsByPath.Remove(k);
                }

                foreach ((nint Parent, string Key) ck in _nativeUiChildren.Where(kv => kv.Value == control).Select(kv => kv.Key).ToArray())
                {
                    _nativeUiChildren.Remove(ck);
                }
            }
        }
    }

    /// <inheritdoc/>
    public string? GetNativeUiTextBoxText(nint textboxControl)
    {
        if (!EnableSimulatedNativeUi || textboxControl == nint.Zero)
        {
            return null;
        }

        lock (_gate)
        {
            return _nativeUiControls.TryGetValue(textboxControl, out SimulatedNativeUiControl? ctrl) && !ctrl.Destroyed
                ? ctrl.Text
                : null;
        }
    }

    /// <inheritdoc/>
    public void SetNativeUiTextBoxText(nint textboxControl, string text)
    {
        if (!EnableSimulatedNativeUi || textboxControl == nint.Zero)
        {
            return;
        }

        Action? changed = null;
        lock (_gate)
        {
            if (_nativeUiControls.TryGetValue(textboxControl, out SimulatedNativeUiControl? ctrl) && !ctrl.Destroyed)
            {
                string next = text ?? string.Empty;
                if (!string.Equals(ctrl.Text, next, StringComparison.Ordinal))
                {
                    ctrl.Text = next;
                    changed = ctrl.ActiveTextChangedHandler;
                }
            }
        }

        changed?.Invoke();
    }

    /// <inheritdoc/>
    public IDisposable SubscribeNativeUiTextBoxChanged(nint textboxControl, Action onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        if (!EnableSimulatedNativeUi || textboxControl == nint.Zero)
        {
            return NoopDisposable.Instance;
        }

        lock (_gate)
        {
            if (!_nativeUiControls.TryGetValue(textboxControl, out SimulatedNativeUiControl? ctrl))
            {
                return NoopDisposable.Instance;
            }

            ctrl.NativeSubscribeCount++;
            ctrl.ActiveTextChangedHandler = onChanged;
        }

        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                if (_nativeUiControls.TryGetValue(textboxControl, out SimulatedNativeUiControl? ctrl) &&
                    ReferenceEquals(ctrl.ActiveTextChangedHandler, onChanged))
                {
                    ctrl.ActiveTextChangedHandler = null;
                }
            }
        });
    }

    /// <inheritdoc/>
    public IDisposable SubscribeNativeUiTextBoxSubmitted(nint textboxControl, Action onSubmitted)
    {
        ArgumentNullException.ThrowIfNull(onSubmitted);
        if (!EnableSimulatedNativeUi || textboxControl == nint.Zero)
        {
            return NoopDisposable.Instance;
        }

        lock (_gate)
        {
            if (!_nativeUiControls.TryGetValue(textboxControl, out SimulatedNativeUiControl? ctrl))
            {
                return NoopDisposable.Instance;
            }

            ctrl.NativeSubscribeCount++;
            ctrl.ActiveSubmittedHandler = onSubmitted;
        }

        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                if (_nativeUiControls.TryGetValue(textboxControl, out SimulatedNativeUiControl? ctrl) &&
                    ReferenceEquals(ctrl.ActiveSubmittedHandler, onSubmitted))
                {
                    ctrl.ActiveSubmittedHandler = null;
                }
            }
        });
    }

    /// <inheritdoc/>
    public bool? GetNativeUiCheckBoxChecked(nint checkboxControl)
    {
        if (!EnableSimulatedNativeUi || checkboxControl == nint.Zero)
        {
            return null;
        }

        lock (_gate)
        {
            return _nativeUiControls.TryGetValue(checkboxControl, out SimulatedNativeUiControl? ctrl) && !ctrl.Destroyed
                ? ctrl.IsChecked
                : null;
        }
    }

    /// <inheritdoc/>
    public void SetNativeUiCheckBoxChecked(nint checkboxControl, bool isChecked)
    {
        if (!EnableSimulatedNativeUi || checkboxControl == nint.Zero)
        {
            return;
        }

        Action? changed = null;
        lock (_gate)
        {
            if (_nativeUiControls.TryGetValue(checkboxControl, out SimulatedNativeUiControl? ctrl) && !ctrl.Destroyed)
            {
                if (ctrl.IsChecked != isChecked)
                {
                    ctrl.IsChecked = isChecked;
                    changed = ctrl.ActiveCheckedChangedHandler;
                }
            }
        }

        changed?.Invoke();
    }

    /// <inheritdoc/>
    public IDisposable SubscribeNativeUiCheckBoxChanged(nint checkboxControl, Action onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        if (!EnableSimulatedNativeUi || checkboxControl == nint.Zero)
        {
            return NoopDisposable.Instance;
        }

        lock (_gate)
        {
            if (!_nativeUiControls.TryGetValue(checkboxControl, out SimulatedNativeUiControl? ctrl))
            {
                return NoopDisposable.Instance;
            }

            ctrl.NativeSubscribeCount++;
            ctrl.ActiveCheckedChangedHandler = onChanged;
        }

        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                if (_nativeUiControls.TryGetValue(checkboxControl, out SimulatedNativeUiControl? ctrl) &&
                    ReferenceEquals(ctrl.ActiveCheckedChangedHandler, onChanged))
                {
                    ctrl.ActiveCheckedChangedHandler = null;
                }
            }
        });
    }

    /// <inheritdoc/>
    public string? GetClipboardText()
    {
        lock (_gate)
        {
            return _clipboardText;
        }
    }

    /// <inheritdoc/>
    public void SetClipboardText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            _clipboardText = text;
        }
    }

    /// <inheritdoc/>
    public IDisposable SubscribeNativeUiClick(nint buttonControl, Action onClicked)
    {
        ArgumentNullException.ThrowIfNull(onClicked);
        if (!EnableSimulatedNativeUi || buttonControl == nint.Zero)
        {
            return NoopDisposable.Instance;
        }

        lock (_gate)
        {
            if (!_nativeUiControls.TryGetValue(buttonControl, out SimulatedNativeUiControl? ctrl))
            {
                return NoopDisposable.Instance;
            }

            ctrl.NativeSubscribeCount++;
            ctrl.ActiveClickHandler = onClicked;
        }

        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                if (_nativeUiControls.TryGetValue(buttonControl, out SimulatedNativeUiControl? ctrl) &&
                    ReferenceEquals(ctrl.ActiveClickHandler, onClicked))
                {
                    ctrl.ActiveClickHandler = null;
                }
            }
        });
    }

    /// <summary>Registers a simulated stock native UI control under <paramref name="parentHandle"/> (defaults to UI root).</summary>
    public SimulatedNativeUiControl RegisterSimulatedStockControl(
        string id,
        nint parentHandle = 0,
        ulong vtableAddress = 0,
        uint tag = 0,
        UiRect rect = default,
        bool visible = true)
    {
        lock (_gate)
        {
            nint parent = parentHandle != nint.Zero ? parentHandle : _simulatedUiRootHandle;
            nint handle = (nint)Interlocked.Increment(ref _nextNativeUiHandle);
            var ctrl = new SimulatedNativeUiControl
            {
                Handle = handle,
                ParentHandle = parent,
                Key = id ?? string.Empty,
                VtableAddress = vtableAddress,
                Tag = tag,
                Rect = rect,
                Visible = visible,
                IsStockControl = true,
            };
            _nativeUiControls[handle] = ctrl;
            return ctrl;
        }
    }

    /// <summary>Gets a simulated native UI control directly by handle, or <c>null</c>.</summary>
    public SimulatedNativeUiControl? GetSimulatedNativeUiControlByHandle(nint handle)
    {
        lock (_gate)
        {
            return _nativeUiControls.TryGetValue(handle, out SimulatedNativeUiControl? ctrl) ? ctrl : null;
        }
    }

    /// <inheritdoc/>
    public bool TryGetNativeUiVisible(nint control, out bool visible)
    {
        visible = false;
        if (!EnableSimulatedNativeUi || control == nint.Zero)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_nativeUiControls.TryGetValue(control, out SimulatedNativeUiControl? ctrl) || ctrl.Destroyed)
            {
                return false;
            }

            nint cur = control;
            while (cur != nint.Zero && _nativeUiControls.TryGetValue(cur, out SimulatedNativeUiControl? node))
            {
                if (node.Destroyed || !node.Visible)
                {
                    visible = false;
                    return true;
                }

                cur = node.ParentHandle;
            }

            visible = true;
            return true;
        }
    }

    /// <inheritdoc/>
    public string? GetNativeUiId(nint control)
    {
        if (!EnableSimulatedNativeUi || control == nint.Zero)
        {
            return null;
        }

        lock (_gate)
        {
            return _nativeUiControls.TryGetValue(control, out SimulatedNativeUiControl? ctrl) && !ctrl.Destroyed
                ? ctrl.Key
                : null;
        }
    }

    /// <inheritdoc/>
    public ulong GetNativeUiVtable(nint control)
    {
        if (!EnableSimulatedNativeUi || control == nint.Zero)
        {
            return 0;
        }

        lock (_gate)
        {
            return _nativeUiControls.TryGetValue(control, out SimulatedNativeUiControl? ctrl) && !ctrl.Destroyed
                ? ctrl.VtableAddress
                : 0;
        }
    }

    /// <inheritdoc/>
    public uint GetNativeUiTag(nint control)
    {
        if (!EnableSimulatedNativeUi || control == nint.Zero)
        {
            return 0;
        }

        lock (_gate)
        {
            return _nativeUiControls.TryGetValue(control, out SimulatedNativeUiControl? ctrl) && !ctrl.Destroyed
                ? ctrl.Tag
                : 0;
        }
    }

    /// <inheritdoc/>
    public nint GetNativeUiParent(nint control)
    {
        if (!EnableSimulatedNativeUi || control == nint.Zero)
        {
            return nint.Zero;
        }

        lock (_gate)
        {
            return _nativeUiControls.TryGetValue(control, out SimulatedNativeUiControl? ctrl) && !ctrl.Destroyed
                ? ctrl.ParentHandle
                : nint.Zero;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<nint> GetNativeUiChildren(nint control)
    {
        if (!EnableSimulatedNativeUi || control == nint.Zero)
        {
            return [];
        }

        lock (_gate)
        {
            return [.. _nativeUiControls.Values
                .Where(c => !c.Destroyed && c.ParentHandle == control)
                .OrderBy(c => c.Handle)
                .Select(c => c.Handle)];
        }
    }

    /// <inheritdoc/>
    public void SetNativeUiWidth(nint control, float width)
    {
        if (!EnableSimulatedNativeUi || control == nint.Zero)
        {
            return;
        }

        lock (_gate)
        {
            if (_nativeUiControls.TryGetValue(control, out SimulatedNativeUiControl? ctrl) && !ctrl.Destroyed)
            {
                ctrl.Rect = ctrl.Rect with { Width = width };
            }
        }
    }

    /// <inheritdoc/>
    public void SetNativeUiHeight(nint control, float height)
    {
        if (!EnableSimulatedNativeUi || control == nint.Zero)
        {
            return;
        }

        lock (_gate)
        {
            if (_nativeUiControls.TryGetValue(control, out SimulatedNativeUiControl? ctrl) && !ctrl.Destroyed)
            {
                ctrl.Rect = ctrl.Rect with { Height = height };
            }
        }
    }

    /// <inheritdoc/>
    public void SetNativeUiAnchor(nint control, byte xAnchor, float xOffset, byte yAnchor, float yOffset)
    {
        if (!EnableSimulatedNativeUi || control == nint.Zero)
        {
            return;
        }

        lock (_gate)
        {
            if (_nativeUiControls.TryGetValue(control, out SimulatedNativeUiControl? ctrl) && !ctrl.Destroyed)
            {
                ctrl.XAnchor = xAnchor;
                ctrl.XAnchorOffset = xOffset;
                ctrl.YAnchor = yAnchor;
                ctrl.YAnchorOffset = yOffset;
            }
        }
    }

    /// <inheritdoc/>
    public void SetNativeUiAutoWidth(nint control, bool enabled)
    {
        if (!EnableSimulatedNativeUi || control == nint.Zero)
        {
            return;
        }

        lock (_gate)
        {
            if (_nativeUiControls.TryGetValue(control, out SimulatedNativeUiControl? ctrl) && !ctrl.Destroyed)
            {
                ctrl.AutoWidth = enabled;
            }
        }
    }

    /// <inheritdoc/>
    public void UpdateNativeUiLayout(nint control)
    {
        if (!EnableSimulatedNativeUi || control == nint.Zero)
        {
            return;
        }

        lock (_gate)
        {
            if (_nativeUiControls.TryGetValue(control, out SimulatedNativeUiControl? ctrl) && !ctrl.Destroyed)
            {
                ctrl.LayoutUpdateCount++;
            }
        }
    }

    /// <inheritdoc/>
    public nint CreateNativeUiCheckBoxChild(nint parentControl, string caption, bool initialChecked)
    {
        if (!EnableSimulatedNativeUi || parentControl == nint.Zero)
        {
            return nint.Zero;
        }

        lock (_gate)
        {
            if (!_nativeUiControls.ContainsKey(parentControl))
            {
                return nint.Zero;
            }

            nint handle = (nint)Interlocked.Increment(ref _nextNativeUiHandle);
            _nativeUiControls[handle] = new SimulatedNativeUiControl
            {
                Handle = handle,
                ParentHandle = parentControl,
                Key = $"cb_{handle}",
                Text = caption ?? string.Empty,
                IsChecked = initialChecked,
                Visible = true,
                Rect = new UiRect(0f, 0f, Math.Max(140f, (caption?.Length ?? 0) * 11f + 44f), 59f),
                IsStockControl = true,
            };
            return handle;
        }
    }

    /// <summary>Gets or sets the simulated active <see cref="PreGameScreen"/>.</summary>
    public PreGameScreen SimulatedPreGameScreen
    {
        get { lock (_gate) { return _simulatedPreGameScreen; } }
        set { lock (_gate) { _simulatedPreGameScreen = value; } }
    }

    /// <summary>Gets or sets the simulated <c>InGameUI*</c> pointer (change to simulate zone transition rebuild).</summary>
    public nint SimulatedInGameUiHandle
    {
        get { lock (_gate) { return _simulatedInGameUiHandle; } }
        set { lock (_gate) { _simulatedInGameUiHandle = value; } }
    }

    /// <summary>Gets or sets the simulated visible in-game panels bitmask.</summary>
    public GamePanel SimulatedActivePanels
    {
        get { lock (_gate) { return _simulatedActivePanels; } }
        set { lock (_gate) { _simulatedActivePanels = value; } }
    }

    /// <summary>Gets or sets the simulated active stash tab metadata.</summary>
    public StashTabInfo? SimulatedActiveStashTab
    {
        get { lock (_gate) { return _simulatedActiveStashTab; } }
        set { lock (_gate) { _simulatedActiveStashTab = value; } }
    }

    /// <summary>Gets or sets whether the simulated corner minimap is visible.</summary>
    public bool SimulatedMinimapVisible
    {
        get { lock (_gate) { return _simulatedMinimapVisible; } }
        set { lock (_gate) { _simulatedMinimapVisible = value; } }
    }

    /// <summary>Gets or sets whether a simulated right side panel is open.</summary>
    public bool SimulatedRightSidePanelOpen
    {
        get { lock (_gate) { return _simulatedRightSidePanelOpen; } }
        set { lock (_gate) { _simulatedRightSidePanelOpen = value; } }
    }

    /// <summary>Gets or sets whether a simulated PoE <c>TextBox</c>/chat input has keyboard focus.</summary>
    public bool SimulatedTextInputFocused
    {
        get { lock (_gate) { return _simulatedTextInputFocused; } }
        set { lock (_gate) { _simulatedTextInputFocused = value; } }
    }

    /// <summary>Gets or sets the simulated hovered control handle.</summary>
    public nint SimulatedHoveredControlHandle
    {
        get { lock (_gate) { return _simulatedHoveredControlHandle; } }
        set { lock (_gate) { _simulatedHoveredControlHandle = value; } }
    }

    /// <summary>Gets or sets the simulated hovered inventory/stash item.</summary>
    public HoveredItemInfo? SimulatedHoveredItem
    {
        get { lock (_gate) { return _simulatedHoveredItem; } }
        set { lock (_gate) { _simulatedHoveredItem = value; } }
    }

    /// <summary>Gets or sets the simulated cursor-held or use-mode item.</summary>
    public CursorItemInfo? SimulatedCursorItem
    {
        get { lock (_gate) { return _simulatedCursorItem; } }
        set { lock (_gate) { _simulatedCursorItem = value; } }
    }

    /// <summary>Associates a simulated stock panel with a control handle.</summary>
    public void SetSimulatedPanelHandle(GamePanel panel, nint handle)
    {
        lock (_gate)
        {
            if (handle == nint.Zero)
            {
                _simulatedPanelHandles.Remove(panel);
            }
            else
            {
                _simulatedPanelHandles[panel] = handle;
            }
        }
    }

    /// <summary>Associates a managed <see cref="ScreenSlot"/> with a direct simulated parent handle.</summary>
    public void SetSimulatedSlotParentHandle(ScreenSlot slot, nint handle)
    {
        lock (_gate)
        {
            if (handle == nint.Zero)
            {
                _simulatedSlotParentHandles.Remove(slot);
            }
            else
            {
                _simulatedSlotParentHandles[slot] = handle;
            }
        }
    }

    /// <inheritdoc/>
    public PreGameScreen GetPreGameScreen() { lock (_gate) { return _simulatedPreGameScreen; } }

    /// <inheritdoc/>
    public nint GetInGameUiHandle() { lock (_gate) { return _simulatedInGameUiHandle; } }

    /// <inheritdoc/>
    public GamePanel GetActivePanels() { lock (_gate) { return _simulatedActivePanels; } }

    /// <inheritdoc/>
    public nint GetPanelControlHandle(GamePanel panel)
    {
        lock (_gate)
        {
            return _simulatedPanelHandles.TryGetValue(panel, out nint handle) ? handle : nint.Zero;
        }
    }

    /// <inheritdoc/>
    public nint GetSlotParentHandle(ScreenSlot slot)
    {
        lock (_gate)
        {
            return _simulatedSlotParentHandles.TryGetValue(slot, out nint handle) ? handle : nint.Zero;
        }
    }

    /// <inheritdoc/>
    public StashTabInfo? GetActiveStashTab() { lock (_gate) { return _simulatedActiveStashTab; } }

    /// <inheritdoc/>
    public bool IsMinimapVisible() { lock (_gate) { return _simulatedMinimapVisible; } }

    /// <inheritdoc/>
    public bool IsRightSidePanelOpen() { lock (_gate) { return _simulatedRightSidePanelOpen; } }

    /// <inheritdoc/>
    public IDisposable RegisterLeagueOverlayFilledCallback(Action<nint, IReadOnlyList<LeagueCardInfo>> onFilled)
    {
        ArgumentNullException.ThrowIfNull(onFilled);
        lock (_gate) { _leagueFillCallbacks.Add(onFilled); }
        return new ActionDisposable(() => { lock (_gate) { _leagueFillCallbacks.Remove(onFilled); } });
    }

    /// <summary>Simulates a native <c>LeagueOverlay::Fill</c> pass firing registered callbacks on the game thread.</summary>
    public void SimulateLeagueOverlayFilled(nint overlayHandle, IReadOnlyList<LeagueCardInfo> cards)
    {
        Action<nint, IReadOnlyList<LeagueCardInfo>>[] callbacks;
        lock (_gate)
        {
            _simulatedPreGameScreen = PreGameScreen.LeagueOverlay;
            callbacks = [.. _leagueFillCallbacks];
        }

        using var _ = EnterSimulatedGameThreadScope(captureGameThreadId: true);
        foreach (Action<nint, IReadOnlyList<LeagueCardInfo>> cb in callbacks)
        {
            cb(overlayHandle, cards);
        }
    }

    /// <inheritdoc/>
    public bool IsTextInputFocused() { lock (_gate) { return _simulatedTextInputFocused; } }

    /// <inheritdoc/>
    public IDisposable RegisterKeyCallback(Func<KeyEventArgs, bool> onKey)
    {
        ArgumentNullException.ThrowIfNull(onKey);
        lock (_gate) { _keyCallbacks.Add(onKey); }
        return new ActionDisposable(() => { lock (_gate) { _keyCallbacks.Remove(onKey); } });
    }

    /// <inheritdoc/>
    public IDisposable RegisterMouseCallback(Func<MouseEventArgs, bool> onMouse)
    {
        ArgumentNullException.ThrowIfNull(onMouse);
        lock (_gate) { _mouseCallbacks.Add(onMouse); }
        return new ActionDisposable(() => { lock (_gate) { _mouseCallbacks.Remove(onMouse); } });
    }

    /// <summary>Simulates a native key event through <c>StateStack::OnWindowMessage</c>. Returns <c>true</c> if suppressed.</summary>
    public bool SimulateKeyEvent(int virtualKey, KeyModifiers modifiers, bool isDown, bool isRepeat = false)
    {
        Func<KeyEventArgs, bool>[] handlers;
        bool textFocused;
        lock (_gate)
        {
            handlers = [.. _keyCallbacks];
            textFocused = _simulatedTextInputFocused;
        }

        var args = new KeyEventArgs(virtualKey, modifiers, isDown, isRepeat, textFocused);
        using var _ = EnterSimulatedGameThreadScope(captureGameThreadId: true);
        foreach (Func<KeyEventArgs, bool> h in handlers)
        {
            if (h(args))
            {
                args.Handled = true;
            }
        }

        return args.Handled;
    }

    /// <summary>Simulates a native mouse event through <c>StateStack::OnWindowMessage</c>. Returns <c>true</c> if suppressed.</summary>
    public bool SimulateMouseEvent(
        MouseButton button,
        InputEventKind kind,
        int x,
        int y,
        KeyModifiers modifiers = KeyModifiers.None,
        int wheelDelta = 0)
    {
        Func<MouseEventArgs, bool>[] handlers;
        bool textFocused;
        lock (_gate)
        {
            handlers = [.. _mouseCallbacks];
            textFocused = _simulatedTextInputFocused;
        }

        var args = new MouseEventArgs(button, kind, x, y, modifiers, wheelDelta, textFocused);
        using var _ = EnterSimulatedGameThreadScope(captureGameThreadId: true);
        foreach (Func<MouseEventArgs, bool> h in handlers)
        {
            if (h(args))
            {
                args.Handled = true;
            }
        }

        return args.Handled;
    }

    /// <inheritdoc/>
    public nint GetHoveredControlHandle() { lock (_gate) { return _simulatedHoveredControlHandle; } }

    /// <inheritdoc/>
    public HoveredItemInfo? GetHoveredItem() { lock (_gate) { return _simulatedHoveredItem; } }

    /// <inheritdoc/>
    public CursorItemInfo? GetCursorItem() { lock (_gate) { return _simulatedCursorItem; } }

    /// <inheritdoc/>
    public IDisposable RegisterCharacterCreatingCallback(Func<string, string, string, string?> onCreating)
    {
        ArgumentNullException.ThrowIfNull(onCreating);
        lock (_gate) { _charCreatingCallbacks.Add(onCreating); }
        return new ActionDisposable(() => { lock (_gate) { _charCreatingCallbacks.Remove(onCreating); } });
    }

    /// <inheritdoc/>
    public IDisposable RegisterCharacterCreatedCallback(Action<string, string, ushort> onCreated)
    {
        ArgumentNullException.ThrowIfNull(onCreated);
        lock (_gate) { _charCreatedCallbacks.Add(onCreated); }
        return new ActionDisposable(() => { lock (_gate) { _charCreatedCallbacks.Remove(onCreated); } });
    }

    /// <summary>Simulates an outgoing <c>C→S 0x12</c> (<c>WriteCreateCharacter_0x12</c>) and returns the final wire <c>S78</c> string.</summary>
    public string SimulateCharacterCreating(string name, string league, string baseStartState)
    {
        Func<string, string, string, string?>[] handlers;
        lock (_gate)
        {
            handlers = [.. _charCreatingCallbacks];
        }

        string current = baseStartState;
        using var _ = EnterSimulatedGameThreadScope(captureGameThreadId: true);
        foreach (Func<string, string, string, string?> h in handlers)
        {
            string? next = h(name, league, current);
            if (!string.IsNullOrEmpty(next))
            {
                current = next;
            }
        }

        return current;
    }

    /// <summary>Simulates an incoming <c>S→C 0x13</c> (<c>CreateCharacterState::OnCreateResult</c>).</summary>
    public void SimulateCharacterCreated(string name, string league, ushort resultCode)
    {
        Action<string, string, ushort>[] handlers;
        lock (_gate)
        {
            handlers = [.. _charCreatedCallbacks];
        }

        using var _ = EnterSimulatedGameThreadScope(captureGameThreadId: true);
        foreach (Action<string, string, ushort> h in handlers)
        {
            h(name, league, resultCode);
        }
    }

    /// <summary>Gets the simulated native UI control state for a control id under the given window path, or <c>null</c>.</summary>
    public SimulatedNativeUiControl? GetSimulatedNativeUiControl(string virtualPath, string? childId = null)
    {
        string norm = ClientTextEncoding.NormalizeVirtualPath(virtualPath);
        lock (_gate)
        {
            if (!_nativeUiWindowsByPath.TryGetValue(norm, out nint root))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(childId))
            {
                return _nativeUiControls.TryGetValue(root, out var rootCtrl) ? rootCtrl : null;
            }

            foreach (SimulatedNativeUiControl ctrl in _nativeUiControls.Values)
            {
                if (string.Equals(ctrl.Key, childId, StringComparison.OrdinalIgnoreCase) &&
                    IsDescendantOf(ctrl.Handle, root))
                {
                    return ctrl;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Simulates a native PoE 3.29 mouse click on a native button control.
    /// Respects native ancestor visibility (<c>Control::SetVisible</c> clears hit-testing on hidden subtrees).
    /// Returns <c>true</c> if a live click handler was invoked.
    /// </summary>
    public bool SimulateNativeUiClick(nint buttonHandle)
    {
        Action? handler = null;
        lock (_gate)
        {
            if (!_nativeUiControls.TryGetValue(buttonHandle, out SimulatedNativeUiControl? ctrl) ||
                ctrl.Destroyed ||
                !ctrl.Enabled ||
                ctrl.ActiveClickHandler is null)
            {
                return false;
            }

            // Native Control::SetVisible blocks clicks on hidden controls and all descendants.
            nint cur = buttonHandle;
            while (cur != nint.Zero && _nativeUiControls.TryGetValue(cur, out SimulatedNativeUiControl? node))
            {
                if (!node.Visible)
                {
                    return false;
                }

                cur = node.ParentHandle;
            }

            handler = ctrl.ActiveClickHandler;
        }

        using var _ = EnterSimulatedGameThreadScope(captureGameThreadId: true);
        handler.Invoke();
        return true;
    }

    /// <summary>Simulates native text editing inside a <c>TextBox</c>, firing <c>TextChanged</c>.</summary>
    public bool SimulateNativeUiTextBoxChanged(nint textboxHandle, string newText)
    {
        Action? handler;
        lock (_gate)
        {
            if (!_nativeUiControls.TryGetValue(textboxHandle, out SimulatedNativeUiControl? ctrl) || ctrl.Destroyed)
            {
                return false;
            }

            ctrl.Text = newText ?? string.Empty;
            handler = ctrl.ActiveTextChangedHandler;
        }

        using var _ = EnterSimulatedGameThreadScope(captureGameThreadId: true);
        handler?.Invoke();
        return handler is not null;
    }

    /// <summary>Simulates pressing Enter inside a <c>TextBox</c>, firing <c>Submitted</c>.</summary>
    public bool SimulateNativeUiTextBoxSubmitted(nint textboxHandle)
    {
        Action? handler;
        lock (_gate)
        {
            if (!_nativeUiControls.TryGetValue(textboxHandle, out SimulatedNativeUiControl? ctrl) || ctrl.Destroyed)
            {
                return false;
            }

            handler = ctrl.ActiveSubmittedHandler;
        }

        using var _ = EnterSimulatedGameThreadScope(captureGameThreadId: true);
        handler?.Invoke();
        return handler is not null;
    }

    /// <summary>Simulates toggling a <c>CheckBox</c> natively, firing <c>CheckedChanged</c>.</summary>
    public bool SimulateNativeUiCheckBoxChanged(nint checkboxHandle, bool isChecked)
    {
        Action? handler;
        lock (_gate)
        {
            if (!_nativeUiControls.TryGetValue(checkboxHandle, out SimulatedNativeUiControl? ctrl) || ctrl.Destroyed)
            {
                return false;
            }

            ctrl.IsChecked = isChecked;
            handler = ctrl.ActiveCheckedChangedHandler;
        }

        using var _ = EnterSimulatedGameThreadScope(captureGameThreadId: true);
        handler?.Invoke();
        return handler is not null;
    }

    private bool IsDescendantOf(nint handle, nint ancestor)
    {
        nint cur = handle;
        while (cur != nint.Zero)
        {
            if (cur == ancestor)
            {
                return true;
            }

            if (!_nativeUiControls.TryGetValue(cur, out SimulatedNativeUiControl? node))
            {
                break;
            }

            cur = node.ParentHandle;
        }

        return false;
    }

    /// <inheritdoc/>
    public int ReadStat(string statKey, int defaultValue)
    {
        lock (_gate)
        {
            return _stats.TryGetValue(statKey, out int val) ? val : defaultValue;
        }
    }

    /// <inheritdoc/>
    public void WriteLocalStat(string statKey, int value)
    {
        lock (_gate)
        {
            _stats[statKey] = value;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<WorldObjectInfo> GetWorldObjects()
    {
        lock (_gate)
        {
            return [.. _worldObjects.Values];
        }
    }

    /// <inheritdoc/>
    public uint SpawnClientObject(string metadataPath, float x, float y, float z)
    {
        lock (_gate)
        {
            uint id = _nextClientObjectId++;
            _worldObjects[id] = new WorldObjectInfo(id, metadataPath, x, y, z, true, new Dictionary<string, string>());
            return id;
        }
    }

    /// <inheritdoc/>
    public bool RemoveClientObject(uint objectId)
    {
        lock (_gate)
        {
            return _worldObjects.TryGetValue(objectId, out WorldObjectInfo? info) && info.IsClientOnly && _worldObjects.Remove(objectId);
        }
    }

    /// <inheritdoc/>
    public void PlayEffect(uint targetObjectId, string effectPath)
    {
        lock (_gate)
        {
            _playedEffects.Add((targetObjectId, effectPath));
        }
    }

    /// <inheritdoc/>
    public unsafe void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_gate)
        {
            foreach (nint ptr in _allocatedBuffers)
            {
                if (ptr != nint.Zero)
                {
                    NativeMemory.Free((void*)ptr);
                }
            }

            _allocatedBuffers.Clear();
        }
    }

    private static bool MatchesWildcardHex(string actualHex, string expectedHex)
    {
        string a = actualHex.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        string e = expectedHex.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        if (a.Length < e.Length)
        {
            return false;
        }

        for (int i = 0; i < e.Length; i += 2)
        {
            if (e[i] == '?' && e[i + 1] == '?')
            {
                continue;
            }

            if (a[i] != e[i] || a[i + 1] != e[i + 1])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Built-in ground-truth verified 3.29 symbol subset from <c>08-deep-analysis.md</c>, <c>10-verification.md</c>, and <c>spikes/agent.js</c>.
    /// Data symbols are explicitly left unverified (<c>null</c> RVA) on 3.29.3c.
    /// </summary>
    public static IReadOnlyList<AddressEntry> CreateDefaultSymbols(ClientBuildId build)
    {
        bool is3c = build == ClientBuildId.Build3293c;
        string codeStatus = is3c ? "code_shared_3_29_3c" : "live_verified";
        string staticCodeStatus = is3c ? "code_shared_3_29_3c" : "static_verified";
        string status3dOnly = is3c ? "unverified_3_29_3c" : "static_verified";
        string live3dOnly = is3c ? "unverified_3_29_3c" : "live_verified";

        return
        [
            new("frame.state_stack_pass_a_hook", 0x1528FBFul, "frame", codeStatus, "488b5d284883eb10", SourceDoc: "spikes/agent.js"),
            new("ui.control_load", 0x18B08D0ul, "ui", codeStatus, "48895c240848897c241055488dac24b0feffff", SourceDoc: "spikes/agent.js"),
            new("ui.set_visible", 0x18B0A30ul, "ui", codeStatus, "40534883ec20488b", SourceDoc: "spikes/agent.js"),
            new("ui.find_child", 0x18B6420ul, "ui", codeStatus, "48896c2418488974", SourceDoc: "spikes/agent.js"),
            new("ui.vtable_frame", is3c ? null : 0x3033598ul, "ui", live3dOnly, SourceDoc: "07-spike-results.md", Kind: "vtable"),
            new("ui.vtable_label", is3c ? null : 0x34AE300ul, "ui", live3dOnly, SourceDoc: "07-spike-results.md", Kind: "vtable"),
            new("ui.vtable_button", is3c ? null : 0x34B0E88ul, "ui", live3dOnly, SourceDoc: "07-spike-results.md", Kind: "vtable"),
            new("ui.vtable_textbox", is3c ? null : 0x30321A0ul, "ui", live3dOnly, SourceDoc: "07-spike-results.md", Kind: "vtable"),
            new("ui.vtable_checkbox", is3c ? null : 0x34AFE98ul, "ui", live3dOnly, SourceDoc: "07-spike-results.md", Kind: "vtable"),
            new("ui.vtable_stack_layout", is3c ? null : 0x3051F80ul, "ui", live3dOnly, SourceDoc: "07-spike-results.md", Kind: "vtable"),
            new("ui.vtable_picture", is3c ? null : 0x30418B0ul, "ui", live3dOnly, SourceDoc: "07-spike-results.md", Kind: "vtable"),
            new("ui.control_set_position", is3c ? null : 0x18B1080ul, "ui", status3dOnly, "48895c240848896c2410488974241857", SourceDoc: "Game.exe 3.29.3d"),
            new("ui.control_set_size", is3c ? null : 0x1906A30ul, "ui", status3dOnly, "4883ec380f2e89680200004c8bc97509", SourceDoc: "Game.exe 3.29.3d"),
            new("ui.control_get_abs_pos", is3c ? null : 0x18B1250ul, "ui", status3dOnly, "405356574883ec20488bb9e001000048", SourceDoc: "Game.exe 3.29.3d"),
            new("ui.control_detach", is3c ? null : 0x18B5A00ul, "ui", status3dOnly, "4883ec384885c90f84cf0000004c8b89", SourceDoc: "Game.exe 3.29.3d"),
            new("ui.children_remove_at", is3c ? null : 0x18B6720ul, "ui", status3dOnly, "48895c240848896c2410488974241857", SourceDoc: "Game.exe 3.29.3d"),
            new("ui.event_list_get_ctrl", is3c ? null : 0x055AE30ul, "ui", status3dOnly, "48895c24184889742420488954241055", SourceDoc: "Game.exe 3.29.3d"),
            new("files.read_whole_file", 0x0E564D0ul, "files", staticCodeStatus, "48895c241848895424105556574156", SourceDoc: "08-deep-analysis.md §1"),
            new("files.text_stream_open", 0x21D5B50ul, "files", codeStatus, "48895c241048896c", SourceDoc: "spikes/agent.js"),
            new("files.hot_reload_invalidate_ext", 0x1010560ul, "files", staticCodeStatus, "488bc44889581048", SourceDoc: "10-verification.md §2"),
            new("files.hash_to_wstring_view", 0x0E40AF0ul, "files", staticCodeStatus, "48895c24184889742420574883ec30", SourceDoc: "Game.exe 3.29.3d"),
            new("files.hash_and_register_path", is3c ? null : 0x0E40A90ul, "files", status3dOnly, "48895c2408574883ec3048837a0800", SourceDoc: "Game.exe 3.29.3d"),
            new("files.filesystem_holder_get", is3c ? null : 0x0E571D0ul, "files", status3dOnly, "48895c2410574883ec3033db65488b04", SourceDoc: "Game.exe 3.29.3d"),
            new("files.hot_reload_singleton", is3c ? null : 0x10062A0ul, "files", status3dOnly, "4883ec2865488b042558000000b91000", SourceDoc: "Game.exe 3.29.3d"),
            new("geal.interpreter", 0x1DF0D70ul, "geal", staticCodeStatus, "488bc44c8948204c", SourceDoc: "08-deep-analysis.md §4"),
            new("rulesets.create", 0x15A6800ul, "rulesets", staticCodeStatus, "48895c2408555657", SourceDoc: "10-verification.md §3"),
            new("rulesets.s2c_0x2c9_filter", 0x15A6B50ul, "rulesets", staticCodeStatus, "48895c24085556574154415541564157", SourceDoc: "10-verification.md §3"),
            new("rulesets.c2s_0x2ca_writer", 0x1FA7C10ul, "rulesets", staticCodeStatus, "48895c2410574883", SourceDoc: "10-verification.md §3"),
            new("rulesets.creator_table", is3c ? null : 0x475DC70ul, "rulesets", status3dOnly, SourceDoc: "10-verification.md §3", Kind: "data"),
            new("geal.builtin_factory_table", is3c ? null : 0x45C50F8ul, "geal", status3dOnly, SourceDoc: "12-ceiling.md §4", Kind: "data"),
            new("allocator.alloc", 0x2C8CF60ul, "allocator", codeStatus, "48895c2408488974", SourceDoc: "08-deep-analysis.md §6"),
            new("allocator.free", 0x2C8CA30ul, "allocator", staticCodeStatus, "4c8bd14c8d49ff49", SourceDoc: "08-deep-analysis.md §6"),
            new("runtime.wstring_assign", 0x0144010ul, "runtime", codeStatus, "48895c241048896c", SourceDoc: "spikes/agent.js"),
            new("runtime.wstring_destroy", is3c ? null : 0x0134500ul, "runtime", status3dOnly, "40534883ec20488b5118488bd94883fa", SourceDoc: "Game.exe 3.29.3d"),
            new("runtime.string_destroy", is3c ? null : 0x0134810ul, "runtime", status3dOnly, "40534883ec20488b5118488bd94883fa", SourceDoc: "Game.exe 3.29.3d"),
            new("runtime.deadlock_detector", 0x0E3ED10ul, "runtime", staticCodeStatus, "4055535657415441", SourceDoc: "10-verification.md §6"),
            new("runtime.cxx_throw_exception", 0x2CE6A40ul, "runtime", codeStatus, "48895c2418488974", SourceDoc: "spikes/agent.js"),
            new("network.chat_panel_listener", 0x017A270ul, "network", codeStatus, "48895c2408488974241855574156", SourceDoc: "spikes/agent.js"),
            new("network.register_handler_group", 0x29A7710ul, "network", staticCodeStatus, "48895c2410574883", SourceDoc: "08-deep-analysis.md §2"),
            new("network.stream_reader", is3c ? null : 0x0DF9ED0ul, "network", status3dOnly, "48895c240848896c2410488974241857", SourceDoc: "Game.exe 3.29.3d"),
            new("audio.submit_request", 0x0FF86E0ul, "audio", staticCodeStatus, "48895c2410555657", SourceDoc: "08-deep-analysis.md §6"),
            new("stats.set_local", 0x24D65C0ul, "stats", staticCodeStatus, "48895c241848896c", SourceDoc: "08-deep-analysis.md §6"),
            new("stats.read", 0x24D6510ul, "stats", staticCodeStatus, "4c8b99f800000044", SourceDoc: "08-deep-analysis.md §6"),
            new("world.create_client_object", 0x1EC47E0ul, "world", staticCodeStatus, "48895c241848894c", SourceDoc: "08-deep-analysis.md §5"),
        ];
    }

    internal static bool TryParseHexUInt64(string? hex, out ulong value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        string trimmed = hex.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }

        return ulong.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}
