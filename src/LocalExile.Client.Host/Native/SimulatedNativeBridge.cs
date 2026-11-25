using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using LocalExile.Client.Sdk.Addresses;
using LocalExile.Client.Sdk.Engine;

namespace LocalExile.Client.Host.Native;

/// <summary>
/// Full in-memory deterministic 3.29 client simulation implementing <see cref="INativeBridge"/>.
/// Supports address verification, simulated memory allocation, inline/vtable hook tracking,
/// frame ticking, file overlay resolution, GEAL script/event invocation, audio/stats/world state,
/// and bidirectional packet/chat loopback for automated tests without a Windows client process.
/// </summary>
public sealed class SimulatedNativeBridge : INativeBridge
{
    private readonly Dictionary<string, AddressEntry> _symbols = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<ReadOnlySpan<nint>, nint>> _nativeCallStubs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, (string Symbol, nint Detour)> _activeHooks = [];
    private readonly List<Action<float>> _frameCallbacks = [];
    private readonly ConcurrentQueue<Action> _mainThreadQueue = new();
    private readonly List<Func<string, byte[]?>> _overlayResolvers = [];
    private readonly Dictionary<string, byte[]> _stockFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _invalidatedPaths = [];
    private readonly Dictionary<byte, List<Action<ReadOnlyMemory<byte>>>> _rulesetReceivers = [];
    private readonly List<Func<string, string, bool>> _chatInterceptors = [];
    private readonly Dictionary<ushort, List<Action<ReadOnlyMemory<byte>>>> _rawOpcodeReceivers = [];
    private readonly Dictionary<int, (string PathOrEvent, bool IsFmod, float Volume, bool Active)> _audioChannels = [];
    private readonly Dictionary<string, int> _stats = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, WorldObjectInfo> _worldObjects = [];
    private readonly List<(uint ObjectId, string EffectPath)> _playedEffects = [];
    private readonly Dictionary<(uint ObjectId, string EntryName, bool IsEvent), Func<GealArguments, GealValue>> _gealObjectHandlers = [];
    private readonly List<nint> _allocatedBuffers = [];
    private readonly Lock _gate = new();

    private int _gameThreadId;
    private ulong _nextHookHandle = 1;
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
        _gameThreadId = Environment.CurrentManagedThreadId;

        foreach (AddressEntry entry in symbols ?? CreateDefaultSymbols(build))
        {
            _symbols[entry.Name] = entry;
        }
    }

    /// <summary>Binds the current managed thread as the simulated game thread.</summary>
    public void BindCurrentThreadAsGameThread() => _gameThreadId = Environment.CurrentManagedThreadId;

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

    /// <summary>Adds a simulated stock text file (encoded as UTF-16LE BOM CRLF if applicable).</summary>
    public void AddStockTextFile(string virtualPath, string text)
    {
        byte[] bytes = ClientTextEncoding.RequiresUtf16LeBomCrlf(virtualPath)
            ? ClientTextEncoding.EncodeUtf16LeBomCrlf(text)
            : System.Text.Encoding.UTF8.GetBytes(text);
        AddStockFile(virtualPath, bytes);
    }

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
        _gameThreadId = Environment.CurrentManagedThreadId;
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

        foreach (Func<string, string, bool> interceptor in interceptors)
        {
            if (interceptor(sender, text))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Delivers a simulated incoming raw opcode payload.</summary>
    public bool SimulateIncomingRawPacket(ushort opcode, ReadOnlyMemory<byte> payload)
    {
        Action<ReadOnlyMemory<byte>>[] receivers;
        lock (_gate)
        {
            if (!_rawOpcodeReceivers.TryGetValue(opcode, out List<Action<ReadOnlyMemory<byte>>>? list) || list.Count == 0)
            {
                return false;
            }

            receivers = [.. list];
        }

        foreach (Action<ReadOnlyMemory<byte>> rx in receivers)
        {
            rx(payload);
        }

        return true;
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
    public bool RemoveHook(ulong hookHandle)
    {
        lock (_gate)
        {
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
    public void EnqueueGameThread(Action action) => _mainThreadQueue.Enqueue(action);

    /// <inheritdoc/>
    public int DrainGameThreadQueue(float deltaSeconds = 0f, int maxItems = 256)
    {
        int count = 0;
        while (count < maxItems && _mainThreadQueue.TryDequeue(out Action? work))
        {
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
        string normalized = ClientTextEncoding.NormalizeVirtualPath(virtualPath);
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
    public void SendRawPacket(ushort opcode, ReadOnlySpan<byte> payload)
    {
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
    public int PlayAudio(string pathOrEvent, bool isFmodEvent, float volume)
    {
        lock (_gate)
        {
            int handle = _nextAudioChannel++;
            _audioChannels[handle] = (pathOrEvent, isFmodEvent, volume, true);
            return handle;
        }
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

        return
        [
            new("frame.state_stack_pass_a_hook", 0x1528FBFul, "frame", codeStatus, "488b5d284883eb10", SourceDoc: "spikes/agent.js"),
            new("ui.control_load", 0x18B08D0ul, "ui", codeStatus, SourceDoc: "spikes/agent.js"),
            new("ui.control_set_visible", 0x18B0A30ul, "ui", codeStatus, SourceDoc: "spikes/agent.js"),
            new("ui.control_find_child", 0x18B6420ul, "ui", codeStatus, SourceDoc: "spikes/agent.js"),
            new("files.read_whole_file", 0x0E564D0ul, "files", staticCodeStatus, SourceDoc: "08-deep-analysis.md §1"),
            new("files.text_stream_open", 0x21D5B50ul, "files", codeStatus, SourceDoc: "spikes/agent.js"),
            new("files.asset_invalidator", 0x1010560ul, "files", staticCodeStatus, SourceDoc: "10-verification.md §2"),
            new("geal.interpreter", 0x1DF0D70ul, "geal", staticCodeStatus, SourceDoc: "08-deep-analysis.md §4"),
            new("rulesets.create", 0x15A6800ul, "rulesets", staticCodeStatus, SourceDoc: "10-verification.md §3"),
            new("rulesets.s2c_filter", 0x15A6B50ul, "rulesets", staticCodeStatus, SourceDoc: "10-verification.md §3"),
            new("rulesets.c2s_writer", 0x1FA7C10ul, "rulesets", staticCodeStatus, SourceDoc: "10-verification.md §3"),
            new("rulesets.creator_table", is3c ? null : 0x475DC70ul, "rulesets", is3c ? "unverified_3_29_3c" : "static_verified", SourceDoc: "10-verification.md §3", Kind: "data"),
            new("geal.builtin_table", is3c ? null : 0x45C50F8ul, "geal", is3c ? "unverified_3_29_3c" : "static_verified", SourceDoc: "12-ceiling.md §4", Kind: "data"),
            new("allocator.alloc", 0x2C8CF60ul, "allocator", codeStatus, SourceDoc: "08-deep-analysis.md §6"),
            new("allocator.free", 0x2C8CA30ul, "allocator", staticCodeStatus, SourceDoc: "08-deep-analysis.md §6"),
            new("allocator.wstring_assign", 0x0144010ul, "allocator", codeStatus, SourceDoc: "spikes/agent.js"),
            new("runtime.deadlock_detector", 0x0E3ED10ul, "runtime", staticCodeStatus, SourceDoc: "10-verification.md §6"),
            new("runtime.cxx_throw_exception", 0x2CE6A40ul, "runtime", codeStatus, SourceDoc: "spikes/agent.js"),
            new("network.chat_listener", 0x017A270ul, "network", codeStatus, SourceDoc: "spikes/agent.js"),
            new("network.handler_group_register", 0x29A7710ul, "network", staticCodeStatus, SourceDoc: "08-deep-analysis.md §2"),
            new("audio.submit_request", 0x0FF86E0ul, "audio", staticCodeStatus, SourceDoc: "08-deep-analysis.md §6"),
            new("stats.set_value", 0x24D65C0ul, "stats", staticCodeStatus, SourceDoc: "08-deep-analysis.md §6"),
            new("stats.get_value", 0x24D6510ul, "stats", staticCodeStatus, SourceDoc: "08-deep-analysis.md §6"),
            new("world.create_client_object", 0x1EC47E0ul, "world", staticCodeStatus, SourceDoc: "08-deep-analysis.md §5"),
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
