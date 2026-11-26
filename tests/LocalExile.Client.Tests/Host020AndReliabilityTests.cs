using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalExile.Client.Host;
using LocalExile.Client.Host.Loading;
using LocalExile.Client.Host.Native;
using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.Sdk.Protocol;
using LocalExile.Client.Sdk.Raw;
using Xunit;

namespace LocalExile.Client.Tests;

[Collection("NativeEntryPointSerial")]
public sealed class Host020AndReliabilityTests
{
    private static readonly Lock s_mockGate = new();
    private static readonly byte[] s_symName = AllocatePinnedUtf8("frame.state_stack_pass_a_hook");
    private static readonly byte[] s_symCat = AllocatePinnedUtf8("frame");
    private static readonly byte[] s_symBytes = AllocatePinnedUtf8("488b5d284883eb10");
    private static unsafe delegate* unmanaged[Cdecl]<void*, void> s_lastQueuedFn;
    private static unsafe void* s_lastQueuedUd;
    private static int s_enqueueCallCount;
    private static bool s_forceQueueFull;
    private static readonly List<ulong> s_unsubscribedTokens = [];
    private static readonly List<nint> s_freedBuffers = [];
    private static unsafe delegate* unmanaged[Cdecl]<byte*, byte*, void*, int> s_chatCallback;
    private static unsafe void* s_chatUserData;
    private static unsafe delegate* unmanaged[Cdecl]<ushort, void*, byte*, uint, void*, int> s_opcodeCallback;
    private static unsafe void* s_opcodeUserData;
    private static unsafe delegate* unmanaged[Cdecl]<NmHookContext*, void*, void> s_hookChainCallback;
    private static unsafe void* s_hookChainUserData;
    private static unsafe delegate* unmanaged[Cdecl]<byte*, byte**, nuint*, void*, NmStatus> s_overlayCallback;
    private static unsafe void* s_overlayUserData;
    private static ulong s_nextToken = 100;
    private static nint s_lastAllocatedStockBuffer;
    private static readonly byte[] s_stockBytes = "stock_payload_v2"u8.ToArray();
    private static readonly byte[] s_streamFallbackBytes = [0xAA, 0xBB, 0xCC, 0xDD];
    private static string s_clipboardMock = string.Empty;
    private static bool s_nativeEnabledMock = true;
    private static bool s_ignoreSetEnabled;
    private static bool s_failGetEnabled;

    private static byte[] AllocatePinnedUtf8(string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        byte[] pinned = GC.AllocateArray<byte>(utf8.Length + 1, pinned: true);
        utf8.CopyTo(pinned, 0);
        pinned[utf8.Length] = 0;
        return pinned;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockGetHostInfo(NmHostInfo* outInfo)
    {
        outInfo->AbiVersion = NativeShimConstants.AbiVersion;
        outInfo->BuildId = NmBuildId.Build3293d328228;
        outInfo->Flags = NmHostFlags.Initialized | NmHostFlags.Simulated;
        outInfo->ImageBase = NativeShimConstants.NominalImageBase;
        outInfo->ImageSize = 79_394_136;
        outInfo->SymbolCount = 1;
        ReadOnlySpan<byte> ver = "3.29.3d\0"u8;
        ver.CopyTo(new Span<byte>(outInfo->BuildVersion, 16));
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockLookupSymbol(byte* name, NmSymbolInfo* outSym)
    {
        string? requested = Marshal.PtrToStringUTF8((nint)name);
        if (!string.Equals(requested, "frame.state_stack_pass_a_hook", StringComparison.OrdinalIgnoreCase))
        {
            return NmStatus.ErrSymbolNotFound;
        }

        return FillSymbol0(outSym);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockGetSymbolAt(uint index, NmSymbolInfo* outSym) =>
        index == 0 ? FillSymbol0(outSym) : NmStatus.ErrSymbolNotFound;

    private static unsafe NmStatus FillSymbol0(NmSymbolInfo* outSym)
    {
        outSym->Name = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(s_symName));
        outSym->Category = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(s_symCat));
        outSym->Kind = NmSymbolKind.Code;
        outSym->Status = NmSymbolStatus.LiveVerified;
        outSym->Rva = 0x1528FBFul;
        outSym->RuntimeAddress = NativeShimConstants.NominalImageBase + 0x1528FBFul;
        outSym->ExpectedBytesHex = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(s_symBytes));
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockEnqueueMainThread(delegate* unmanaged[Cdecl]<void*, void> fn, void* ud)
    {
        lock (s_mockGate)
        {
            s_enqueueCallCount++;
            if (s_forceQueueFull)
            {
                return NmStatus.ErrQueueFull;
            }

            s_lastQueuedFn = fn;
            s_lastQueuedUd = ud;
            return NmStatus.Ok;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockRegisterFrameCallback(delegate* unmanaged[Cdecl]<float, void*, void> cb, void* ud, ulong* outToken)
    {
        *outToken = Interlocked.Increment(ref s_nextToken);
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NmStatus MockUnregisterToken(ulong token) => NmStatus.Ok;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockRegisterFileOverlay(
        delegate* unmanaged[Cdecl]<byte*, byte**, nuint*, void*, NmStatus> cb,
        delegate* unmanaged[Cdecl]<byte*, void*, void> freeCb,
        void* ud,
        ulong* outToken)
    {
        s_overlayCallback = cb;
        s_overlayUserData = ud;
        *outToken = Interlocked.Increment(ref s_nextToken);
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockRegisterRulesetHandler(uint id, delegate* unmanaged[Cdecl]<uint, byte*, uint, void*, void> cb, void* ud, ulong* outToken)
    {
        *outToken = Interlocked.Increment(ref s_nextToken);
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockSendRulesetPacket(uint id, byte* data, uint len) => NmStatus.Ok;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockRegisterChatHandler(delegate* unmanaged[Cdecl]<byte*, byte*, void*, int> cb, void* ud, ulong* outToken)
    {
        s_chatCallback = cb;
        s_chatUserData = ud;
        *outToken = Interlocked.Increment(ref s_nextToken);
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockSendChatLine(byte* text) => NmStatus.Ok;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockFreeFileOverlayBuffer(byte* ptr)
    {
        lock (s_mockGate)
        {
            s_freedBuffers.Add((nint)ptr);
        }

        if (ptr is not null)
        {
            NativeMemory.Free(ptr);
        }

        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockFilesReadStock(byte* virtualPathUtf8, byte** outData, nuint* outLength)
    {
        if (outData is null || outLength is null)
        {
            return NmStatus.ErrInvalidArg;
        }

        byte* buf = (byte*)NativeMemory.Alloc((nuint)s_stockBytes.Length);
        s_stockBytes.CopyTo(new Span<byte>(buf, s_stockBytes.Length));
        s_lastAllocatedStockBuffer = (nint)buf;
        *outData = buf;
        *outLength = (nuint)s_stockBytes.Length;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockFilesInvalidate(byte* virtualPathUtf8) => NmStatus.Ok;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockRegisterOpcodeGroup(ushort minOp, ushort maxOp, delegate* unmanaged[Cdecl]<ushort, void*, byte*, uint, void*, int> cb, void* ud, ulong* outToken)
    {
        s_opcodeCallback = cb;
        s_opcodeUserData = ud;
        *outToken = Interlocked.Increment(ref s_nextToken);
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockStreamRead(void* streamHandle, void* dst, ulong count)
    {
        if (streamHandle is null || dst is null)
        {
            return NmStatus.ErrInvalidArg;
        }

        int len = Math.Min((int)count, s_streamFallbackBytes.Length);
        s_streamFallbackBytes.AsSpan(0, len).CopyTo(new Span<byte>(dst, len));
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockSendRawPacket(ushort opcode, byte* data, uint len) => NmStatus.Ok;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockInstallInlineChainHook(ulong target, byte* expectedBytes, int phase, int priority, delegate* unmanaged[Cdecl]<NmHookContext*, void*, void> cb, void* ud, ulong* outToken)
    {
        s_hookChainCallback = cb;
        s_hookChainUserData = ud;
        *outToken = Interlocked.Increment(ref s_nextToken);
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockClipboardGetText(byte* outBuf, nuint bufLen, nuint* outRequiredLen)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(s_clipboardMock);
        if (outRequiredLen is not null)
        {
            *outRequiredLen = (nuint)utf8.Length;
        }

        if (outBuf is null && bufLen == 0)
        {
            return NmStatus.Ok;
        }

        if (outBuf is not null && bufLen > 0)
        {
            if (bufLen <= (nuint)utf8.Length)
            {
                return NmStatus.ErrBufferTooSmall;
            }

            utf8.CopyTo(new Span<byte>(outBuf, utf8.Length));
            outBuf[utf8.Length] = 0;
        }

        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockClipboardSetText(byte* textUtf8)
    {
        s_clipboardMock = Marshal.PtrToStringUTF8((nint)textUtf8) ?? string.Empty;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockUiCreateWindow(byte* path, void** outControl)
    {
        *outControl = (void*)0x2100;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockUiFindChild(void* parent, byte* dotPath, void** outChild)
    {
        *outChild = (void*)0x2101;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockUiSetEnabled(void* control, int enabled)
    {
        if (!s_ignoreSetEnabled)
        {
            s_nativeEnabledMock = enabled != 0;
        }

        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockUiGetEnabled(void* control, int* outEnabled)
    {
        if (s_failGetEnabled)
        {
            return NmStatus.ErrGameException;
        }

        *outEnabled = s_nativeEnabledMock ? 1 : 0;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockUiSubscribeClick(void* ctrl, delegate* unmanaged[Cdecl]<void*, void*, void> cb, void* ud, ulong* outToken)
    {
        *outToken = Interlocked.Increment(ref s_nextToken);
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NmStatus MockUiUnsubscribe(ulong token)
    {
        lock (s_mockGate)
        {
            s_unsubscribedTokens.Add(token);
        }

        return NmStatus.Ok;
    }

    private static unsafe NmAbiTable CreateMockAbiTable()
    {
        ResetMockStatics();
        NmAbiTable table = default;
        table.StructSize = (uint)sizeof(NmAbiTable);
        table.AbiVersion = NativeShimConstants.AbiVersion;
        table.GetHostInfo = &MockGetHostInfo;
        table.LookupSymbol = &MockLookupSymbol;
        table.GetSymbolAt = &MockGetSymbolAt;
        table.EnqueueMainThread = &MockEnqueueMainThread;
        table.RegisterFrameCallback = &MockRegisterFrameCallback;
        table.UnregisterFrameCallback = &MockUnregisterToken;
        table.RegisterFileOverlay = &MockRegisterFileOverlay;
        table.UnregisterFileOverlay = &MockUnregisterToken;
        table.RegisterRulesetHandler = &MockRegisterRulesetHandler;
        table.UnregisterRulesetHandler = &MockUnregisterToken;
        table.SendRulesetPacket = &MockSendRulesetPacket;
        table.RegisterChatChannelHandler = &MockRegisterChatHandler;
        table.UnregisterChatChannelHandler = &MockUnregisterToken;
        table.SendChatLine = &MockSendChatLine;
        table.FreeFileOverlayBuffer = &MockFreeFileOverlayBuffer;
        table.UiSubscribeClick = &MockUiSubscribeClick;
        table.InstallInlineChainHook = &MockInstallInlineChainHook;
        table.RemoveHook = &MockUnregisterToken;
        table.FilesReadStock = &MockFilesReadStock;
        table.FilesInvalidate = &MockFilesInvalidate;
        table.SendRawPacket = &MockSendRawPacket;
        table.RegisterOpcodeHandlerGroup = &MockRegisterOpcodeGroup;
        table.UnregisterOpcodeHandlerGroup = &MockUnregisterToken;
        table.StreamRead = &MockStreamRead;
        table.UiUnsubscribe = &MockUiUnsubscribe;
        table.ClipboardGetText = &MockClipboardGetText;
        table.ClipboardSetText = &MockClipboardSetText;
        table.UiGetEnabled = &MockUiGetEnabled;
        return table;
    }

    private static unsafe void ResetMockStatics()
    {
        lock (s_mockGate)
        {
            s_lastQueuedFn = null;
            s_lastQueuedUd = null;
            s_enqueueCallCount = 0;
            s_forceQueueFull = false;
            s_unsubscribedTokens.Clear();
            s_freedBuffers.Clear();
            s_chatCallback = null;
            s_chatUserData = null;
            s_opcodeCallback = null;
            s_opcodeUserData = null;
            s_hookChainCallback = null;
            s_hookChainUserData = null;
            s_overlayCallback = null;
            s_overlayUserData = null;
            s_lastAllocatedStockBuffer = nint.Zero;
            s_clipboardMock = string.Empty;
            s_nativeEnabledMock = true;
            s_ignoreSetEnabled = false;
            s_failGetEnabled = false;
        }
    }

    [Fact]
    public unsafe void IsOnGameThread_CapturedOnFirstGameThreadCallback_NotCtorOrHookChainOrFileOverlay()
    {
        NmAbiTable table = CreateMockAbiTable();
        using var bridge = new NativeShimBridge((nint)(&table));

        // Never true on bootstrap/ctor thread before any game-thread callback executes.
        Assert.False(bridge.IsOnGameThread);

        // Install a hook chain and invoke OnNativeHookChain on a non-game worker thread:
        // IsOnGameThread must be true INSIDE the hook callback (via ThreadStatic flag), false after,
        // and must NOT capture that worker thread as _gameThreadId.
        bool seenInHook = false;
        ulong hookHandle = bridge.InstallInlineHookChainAt(0x1234, null, HookPhase.Pre, 0, _ =>
        {
            seenInHook = bridge.IsOnGameThread;
        });

        try
        {
            Thread workerThread = new(() =>
            {
                Assert.False(bridge.IsOnGameThread);
                NmHookContext ctx = default;
                s_hookChainCallback(&ctx, s_hookChainUserData);
                Assert.False(bridge.IsOnGameThread);
            });
            workerThread.Start();
            workerThread.Join();
            Assert.True(seenInHook);
        }
        finally
        {
            bridge.RemoveHook(hookHandle);
        }

        // OnNativeFileOverlay must NEVER set IsOnGameThread.
        bool seenInOverlay = true;
        using IDisposable overlaySub = bridge.RegisterFileOverlayResolver(_ =>
        {
            seenInOverlay = bridge.IsOnGameThread;
            return null;
        });
        byte[] pathUtf8 = "art/test.txt\0"u8.ToArray();
        fixed (byte* pPath = pathUtf8)
        {
            byte* outBuf = null;
            nuint outLen = 0;
            s_overlayCallback(pPath, &outBuf, &outLen, s_overlayUserData);
        }
        Assert.False(seenInOverlay);

        // Enqueue work and invoke OnNativeMainThreadDrain from a dedicated game thread.
        bool seenInWork = false;
        Assert.True(bridge.EnqueueGameThread(() =>
        {
            seenInWork = bridge.IsOnGameThread;
            throw new InvalidOperationException("intentional fault to verify try/finally restoration");
        }));

        bool stillTrueOnGameThreadAfterDrain = false;
        Thread gameThread = new(() =>
        {
            Assert.False(bridge.IsOnGameThread);
            s_lastQueuedFn(s_lastQueuedUd);
            stillTrueOnGameThreadAfterDrain = bridge.IsOnGameThread;
        });
        gameThread.Start();
        gameThread.Join();

        Assert.True(seenInWork);
        Assert.True(stillTrueOnGameThreadAfterDrain);
        Assert.False(bridge.IsOnGameThread); // Current test thread is NOT the game thread.

        // Also verify SimulatedNativeBridge ClearGameThreadBinding behavior.
        using var sim = new SimulatedNativeBridge();
        sim.ClearGameThreadBinding();
        Assert.False(sim.IsOnGameThread);
        bool inSimHook = false;
        sim.InstallInlineHookChain("frame.state_stack_pass_a_hook", HookPhase.Pre, 0, _ => inSimHook = sim.IsOnGameThread);
        sim.ResolveSymbol("frame.state_stack_pass_a_hook", out _, out nint hookTarget);
        sim.SimulateHookChainCall(hookTarget, new HookContext(hookTarget));
        Assert.True(inSimHook);
        Assert.False(sim.IsOnGameThread); // Hook did NOT capture _gameThreadId.

        bool inSimFrame = false;
        sim.RegisterFrameCallback(_ => inSimFrame = sim.IsOnGameThread);
        sim.SimulateFrame();
        Assert.True(inSimFrame);
        Assert.True(sim.IsOnGameThread);
    }

    [Fact]
    public unsafe void AutoLoadClientMods_IsDeferredToGameThread_AndSkipsCleanlyIfDisposedBeforeDrain()
    {
        string tempRoot = CreateTempDirectory();
        string modsDir = Path.Combine(tempRoot, "mods");
        string modDir = Path.Combine(modsDir, "deferred-mod");
        Directory.CreateDirectory(modDir);

        string? prevModsDir = Environment.GetEnvironmentVariable("NATIVEMODS_MODS_DIR");
        string? prevLogPath = Environment.GetEnvironmentVariable("NATIVEMODS_MANAGED_LOG_PATH");
        try
        {
            string dllPath = Path.Combine(modDir, "deferred.Client.dll");
            EmitMinimalModDll(dllPath, "DeferredTestMod", statKey: "deferred_loaded", statValue: 1);
            File.WriteAllText(Path.Combine(modDir, "mod.json"), """
                {
                  "id": "deferred-mod",
                  "name": "Deferred Mod",
                  "version": "1.0.0",
                  "nativeClient": {
                    "sdkVersionRange": ">=0.2.0 <2.0.0",
                    "assembly": "deferred.Client.dll"
                  }
                }
                """);

            Environment.SetEnvironmentVariable("NATIVEMODS_MODS_DIR", modsDir);
            Environment.SetEnvironmentVariable("NATIVEMODS_MANAGED_LOG_PATH", Path.Combine(tempRoot, "managed.log"));

            delegate* unmanaged[Cdecl]<nint, int> initFn = &NativeEntryPoint.Initialize;
            NmAbiTable table = CreateMockAbiTable();
            int initStatus = initFn((nint)(&table));
            Assert.Equal((int)NmStatus.Ok, initStatus);
            Assert.NotNull(NativeEntryPoint.ActiveRuntime);
            // Deferred: must NOT load synchronously during Initialize().
            Assert.Empty(NativeEntryPoint.ActiveRuntime!.Loader.LoadedMods);
            Assert.Equal(1, s_enqueueCallCount);

            // Drain on game thread -> loads cleanly inside game-thread context.
            s_lastQueuedFn(s_lastQueuedUd);
            Assert.Single(NativeEntryPoint.ActiveRuntime.Loader.LoadedMods);
            Assert.Equal(1, NativeEntryPoint.ActiveRuntime.Bridge.ReadStat("deferred_loaded", 0));

            NativeEntryPoint.Shutdown();

            // Second run: Shutdown BEFORE draining -> draining orphaned callback must be a clean no-op.
            NmAbiTable table2 = CreateMockAbiTable();
            Assert.Equal((int)NmStatus.Ok, initFn((nint)(&table2)));
            delegate* unmanaged[Cdecl]<void*, void> fn = s_lastQueuedFn;
            void* ud = s_lastQueuedUd;
            NativeEntryPoint.Shutdown();
            Assert.Null(NativeEntryPoint.ActiveRuntime);

            fn(ud); // Must NOT throw ObjectDisposedException or load into disposed runtime.
        }
        finally
        {
            NativeEntryPoint.Shutdown();
            Environment.SetEnvironmentVariable("NATIVEMODS_MODS_DIR", prevModsDir);
            Environment.SetEnvironmentVariable("NATIVEMODS_MANAGED_LOG_PATH", prevLogPath);
            CleanupDirectory(tempRoot);
        }
    }

    [Fact]
    public void ClientModManifest_And_LoadModsFromDirectory_EnforcesEnabledDependsCyclesAndPayloadHash()
    {
        ClientModManifest dualWithGame = ClientModManifest.Parse("""
            {
              "id": "dual-mod",
              "name": "Dual",
              "version": "1.0.0",
              "depends": ["Game", "SharedLib"],
              "nativeClient": {
                "sdkVersionRange": ">=0.2.0 <2.0.0",
                "assembly": "dual.dll"
              }
            }
            """);
        Assert.Equal(["SharedLib"], dualWithGame.EffectiveDepends);
        Assert.True(dualWithGame.Enabled);

        ClientModManifest disabledByClientSection = ClientModManifest.Parse("""
            {
              "id": "disabled-client",
              "name": "Disabled Client",
              "version": "1.0.0",
              "enabled": true,
              "nativeClient": {
                "enabled": false,
                "sdkVersionRange": ">=0.2.0 <2.0.0"
              }
            }
            """);
        Assert.False(disabledByClientSection.Enabled);

        string tempRoot = CreateTempDirectory();
        try
        {
            string modsDir = Path.Combine(tempRoot, "mods");
            Directory.CreateDirectory(modsDir);

            // Valid chain: alpha <- beta <- gamma + "Game" dependency ignored.
            WriteEmittedMod(modsDir, "alpha", "AlphaMod", "load_seq_alpha", 1, depends: ["Game"]);
            WriteEmittedMod(modsDir, "beta", "BetaMod", "load_seq_beta", 2, depends: ["alpha"]);
            WriteEmittedMod(modsDir, "gamma", "GammaMod", "load_seq_gamma", 3, depends: ["beta", "alpha"]);

            // Disabled mod (skipped without ALC)
            WriteEmittedMod(modsDir, "disabled-mod", "DisabledMod", "disabled_ran", 99, enabled: false);

            // Cycle pair: cyc1 <-> cyc2
            WriteEmittedMod(modsDir, "cyc1", "Cyc1", "cyc1_ran", 1, depends: ["cyc2"]);
            WriteEmittedMod(modsDir, "cyc2", "Cyc2", "cyc2_ran", 1, depends: ["cyc1"]);

            // Tampered hash mod (valid 64 hex, mismatched) must reject BEFORE .cctor executes.
            string markerFile = Path.Combine(tempRoot, "cctor_executed.txt");
            WriteEmittedMod(
                modsDir,
                "tampered",
                "TamperedMod",
                "tampered_ran",
                1,
                overrideHash: new string('a', 64),
                cctorMarkerPath: markerFile);

            using var bridge = new SimulatedNativeBridge();
            using var host = new ClientHostRuntime(bridge);
            host.Start();

            IReadOnlyList<ModLoadDiagnostic> diagnostics = host.Loader.LoadModsFromDirectory(modsDir);
            Assert.False(File.Exists(markerFile));

            Dictionary<string, ModLoadDiagnostic> byId = diagnostics.ToDictionary(d => d.ModId, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(ModLoadStatus.Loaded, byId["alpha"].Status);
            Assert.Equal(ModLoadStatus.Loaded, byId["beta"].Status);
            Assert.Equal(ModLoadStatus.Loaded, byId["gamma"].Status);
            Assert.Equal(ModLoadStatus.Disabled, byId["disabled-mod"].Status);
            Assert.Equal(ModLoadStatus.CyclicDependency, byId["cyc1"].Status);
            Assert.Equal(ModLoadStatus.CyclicDependency, byId["cyc2"].Status);
            Assert.Equal(ModLoadStatus.LoadFailed, byId["tampered"].Status);

            List<string> loadedOrder = host.Loader.LoadedMods.Select(m => m.Manifest.Id).ToList();
            Assert.Equal(["alpha", "beta", "gamma"], loadedOrder);

            Assert.Throws<InvalidOperationException>(() => host.Loader.LoadModFromDirectory(Path.Combine(modsDir, "disabled-mod")));
        }
        finally
        {
            CleanupDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task EnqueueGameThread_QueueFullAt4096_HandlesCleanlyWithoutLeakedHandlesOrHangingScheduler()
    {
        using var sim = new SimulatedNativeBridge();
        int executed = 0;
        for (int i = 0; i < 4096; i++)
        {
            Assert.True(sim.EnqueueGameThread(() => executed++));
        }

        Assert.False(sim.EnqueueGameThread(() => executed++));
        sim.DrainGameThreadQueue(0.016f, 4096);
        Assert.Equal(4096, executed);
        int gameThreadId = 0;
        Assert.True(sim.EnqueueGameThread(() =>
        {
            executed++;
            gameThreadId = Environment.CurrentManagedThreadId;
        }));
        sim.DrainGameThreadQueue(0.016f, 4096);
        Assert.NotEqual(0, gameThreadId);

        VerifyNativeQueueFullBehavior();

        using var host = new ClientHostRuntime(sim);
        host.Start();
        for (int i = 0; i < 4096; i++)
        {
            Assert.True(sim.EnqueueGameThread(() => { }));
        }

        var offThreadDone = new TaskCompletionSource<(InvalidOperationException SyncEx, InvalidOperationException AsyncEx)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                Assert.NotEqual(gameThreadId, Environment.CurrentManagedThreadId);
                InvalidOperationException syncEx = Assert.Throws<InvalidOperationException>(
                    () => host.Scheduler.RunOnGameThread(() => { }));
                InvalidOperationException asyncEx = Assert.ThrowsAsync<InvalidOperationException>(
                    () => host.Scheduler.RunOnGameThreadAsync(() => 42)).GetAwaiter().GetResult();
                offThreadDone.SetResult((syncEx, asyncEx));
            }
            catch (Exception ex)
            {
                offThreadDone.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "QueueFullOffGameThread"
        };
        worker.Start();
        (InvalidOperationException syncEx, InvalidOperationException asyncEx) = await offThreadDone.Task;
        worker.Join();

        Assert.Contains("full", syncEx.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("full", asyncEx.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static unsafe void VerifyNativeQueueFullBehavior()
    {
        NmAbiTable table = CreateMockAbiTable();
        using var nativeBridge = new NativeShimBridge((nint)(&table));
        s_forceQueueFull = true;
        Assert.False(nativeBridge.EnqueueGameThread(() => { }));
        Assert.Equal(0, nativeBridge.PendingWorkHandleCount);

        s_forceQueueFull = false;
        int nativeRan = 0;
        Assert.True(nativeBridge.EnqueueGameThread(() => nativeRan++));
        Assert.Equal(1, nativeBridge.PendingWorkHandleCount);
        s_lastQueuedFn(s_lastQueuedUd);
        Assert.Equal(1, nativeRan);
        Assert.Equal(0, nativeBridge.PendingWorkHandleCount);
    }

    [Fact]
    public void SubscribedUiEvents_CleanupOnCloseReloadReplaceRemoveDestroy_And_LiveControlsWork()
    {
        using var bridge = new SimulatedNativeBridge { EnableSimulatedNativeUi = true };
        using var host = new ClientHostRuntime(bridge);
        host.Start();

        bridge.AddStockTextFile("Art/UI/mod.ui", """
            begin Window
              id = "root_win"
              begin TextBox
                id = "SearchBox"
              end
              begin CheckBox
                id = "AutoLoot"
              end
              begin Button
                id = "Go"
              end
              begin Button
                id = "ChildBtn"
              end
            end
            """);
        IUiWindow win1 = host.Ui.Load("Art/UI/mod.ui");
        IUiControl? root = host.Ui.GetRootControl();
        Assert.NotNull(root);
        IUiWindow win2 = host.Ui.CreateWindowInto(root!, "Art/UI/mod.ui", "nested_win");
        Assert.Single(root!.Children);
        Assert.Same(win2, root.Children[0]);

        ITextBox box = win1.Require<ITextBox>("SearchBox");
        box.MaxLength = 4;
        string? changedText = null;
        string? submittedText = null;
        box.TextChanged += t => changedText = t;
        box.Submitted += t => submittedText = t;
        box.Text = "ab\U0001F600cd";
        Assert.True(box.Text.Length <= 4);
        Assert.Equal(box.Text, changedText);

        var simBox = bridge.GetSimulatedNativeUiControl("Art/UI/mod.ui", "SearchBox")!;
        Assert.True(bridge.SimulateNativeUiTextBoxChanged(simBox.Handle, "xyz"));
        Assert.Equal("xyz", box.Text);
        Assert.True(bridge.SimulateNativeUiTextBoxSubmitted(simBox.Handle));
        Assert.Equal("xyz", submittedText);

        ICheckBox chk = win1.Require<ICheckBox>("AutoLoot");
        bool? checkedVal = null;
        chk.CheckedChanged += v => checkedVal = v;
        chk.IsChecked = true;
        Assert.True(chk.Checked);
        Assert.True(checkedVal);
        var simChk = bridge.GetSimulatedNativeUiControl("Art/UI/mod.ui", "AutoLoot")!;
        Assert.True(bridge.SimulateNativeUiCheckBoxChanged(simChk.Handle, false));
        Assert.False(chk.IsChecked);
        Assert.False(checkedVal);

        IButton btn = win1.Require<IButton>("Go");
        btn.SetPosition(12, 34);
        btn.SetSize(120, 40);
        Assert.Equal(new UiRect(12, 34, 120, 40), btn.Rect);
        Assert.Equal(btn.Rect, btn.Bounds);
        btn.Enabled = false;
        Assert.False(btn.IsEnabled);
        var simBtn = bridge.GetSimulatedNativeUiControl("Art/UI/mod.ui", "Go")!;
        Assert.False(simBtn.Enabled);
        simBtn.Enabled = true;
        Assert.True(btn.IsEnabled);

        host.HandleServerUiCommand(new ServerUiCommand(ServerUiCommandKind.SubscribeEvent, "nested_win", ControlPath: "Go", EventName: "clicked"));
        host.HandleServerUiCommand(new ServerUiCommand(ServerUiCommandKind.SubscribeEvent, "NESTED_WIN", ControlPath: "go", EventName: "CLICKED"));
        Assert.Equal(1, host.SubscribedUiEventCount);

        IButton win2Btn = win2.Require<IButton>("Go");
        bool reentrantRan = false;
        win2Btn.Clicked += () =>
        {
            reentrantRan = true;
            win2.Close();
        };
        win2Btn.Click();
        Assert.True(reentrantRan);
        Assert.False(win2.IsOpen);
        Assert.Equal(0, host.SubscribedUiEventCount);

        IUiWindow replacement = host.Ui.Load("Art/UI/mod.ui");
        Assert.False(win1.IsOpen);
        Assert.True(replacement.IsOpen);

        IButton repChild = replacement.Require<IButton>("ChildBtn");
        host.HandleServerUiCommand(new ServerUiCommand(ServerUiCommandKind.SubscribeEvent, replacement.Id, ControlPath: "ChildBtn", EventName: "Clicked"));
        Assert.Equal(1, host.SubscribedUiEventCount);
        Assert.True(replacement.RemoveChild(repChild));
        Assert.Equal(0, host.SubscribedUiEventCount);
        repChild.Destroy();
        Assert.Throws<ObjectDisposedException>(() => _ = repChild.Text);
    }

    [Fact]
    public unsafe void UiControlNode_RectAndIsEnabled_QueryNativeStateWithoutMaskingDeadOrNoopBridge()
    {
        NmAbiTable table = CreateMockAbiTable();
        table.UiCreateWindow = &MockUiCreateWindow;
        table.UiFindChild = &MockUiFindChild;
        table.UiSetEnabled = &MockUiSetEnabled;
        table.UiGetEnabled = &MockUiGetEnabled;

        using var bridge = new NativeShimBridge((nint)(&table));
        using var host = new ClientHostRuntime(bridge);
        host.Start();

        using var uiOverlay = host.FileOverlayService.RegisterVirtualTextFile(
            "Art/UI/native_guard.ui",
            """
            begin Frame
              id = "root_frame"
              begin Button
                id = "Go"
                text = "Go"
              end
            end
            """);

        IUiWindow win = host.Ui.Load("Art/UI/native_guard.ui");
        IButton btn = win.Require<IButton>("Go");

        // 1. IsEnabled reads back native state via TryGetNativeUiEnabled.
        Assert.True(btn.IsEnabled);
        btn.IsEnabled = false;
        Assert.False(btn.IsEnabled);
        Assert.False(s_nativeEnabledMock);

        // 2. If SetNativeUiEnabled is a no-op, IsEnabled must reflect true native state instead of stale _enabled.
        btn.IsEnabled = true;
        Assert.True(btn.IsEnabled);
        s_ignoreSetEnabled = true;
        btn.IsEnabled = false;
        Assert.True(btn.IsEnabled);

        // 3. If TryGetNativeUiEnabled fails, IsEnabled must return false instead of masking via stale _enabled.
        s_failGetEnabled = true;
        Assert.False(btn.IsEnabled);

        // 4. If TryGetNativeUiRect fails (UiGetRect == null) on a live-handled control, Rect must return default.
        btn.SetPosition(12, 34);
        btn.SetSize(120, 40);
        Assert.Equal(default, btn.Rect);

        // 5. When NativeControlHandle == 0 (simulated native UI disabled), managed fallback still works.
        using var simOffBridge = new SimulatedNativeBridge { EnableSimulatedNativeUi = false };
        using var simOffHost = new ClientHostRuntime(simOffBridge);
        simOffHost.Start();
        simOffBridge.AddStockTextFile("Art/UI/managed_fallback.ui", """
            begin Frame
              id = "root_frame"
              begin Button
                id = "Go"
              end
            end
            """);
        IUiWindow simWin = simOffHost.Ui.Load("Art/UI/managed_fallback.ui");
        IButton simBtn = simWin.Require<IButton>("Go");
        simBtn.SetPosition(12, 34);
        simBtn.SetSize(120, 40);
        Assert.Equal(new UiRect(12, 34, 120, 40), simBtn.Rect);
        simBtn.IsEnabled = false;
        Assert.False(simBtn.IsEnabled);
    }

    [Fact]
    public void CreateWindowInto_ParentReloadCloseAndDirectChildClose_CleansChildWindowsSubscriptionsAndParentAttachment()
    {
        using var bridge = new SimulatedNativeBridge { EnableSimulatedNativeUi = true };
        using var host = new ClientHostRuntime(bridge);
        host.Start();

        bridge.AddStockTextFile("Art/UI/p_parent.ui", """
            begin Window
              id = "p_parent"
              begin Frame
                id = "Slot"
              end
            end
            """);
        bridge.AddStockTextFile("Art/UI/p_child.ui", """
            begin Window
              id = "p_child"
              begin Frame
                id = "SubSlot"
              end
              begin Button
                id = "Btn"
              end
            end
            """);

        // 1. Parent Reload cleans nested CreateWindowInto child + grandchild windows and their UI subscriptions.
        IUiWindow parent = host.Ui.Load("Art/UI/p_parent.ui", "parent_win");
        IUiWindow childOnReload = host.Ui.CreateWindowInto(parent, "Art/UI/p_child.ui", "child_reload");
        IUiWindow grandChild = host.Ui.CreateWindowInto(childOnReload.Require<IUiControl>("SubSlot"), "Art/UI/p_child.ui", "grandchild_reload");

        bool childClosed = false;
        bool grandChildClosed = false;
        childOnReload.Closed += () => childClosed = true;
        grandChild.Closed += () => grandChildClosed = true;

        host.HandleServerUiCommand(new ServerUiCommand(ServerUiCommandKind.SubscribeEvent, "child_reload", ControlPath: "Btn", EventName: "Clicked"));
        host.HandleServerUiCommand(new ServerUiCommand(ServerUiCommandKind.SubscribeEvent, "grandchild_reload", ControlPath: "Btn", EventName: "Clicked"));
        Assert.Equal(2, host.SubscribedUiEventCount);

        parent.Reload();

        Assert.True(childClosed);
        Assert.True(grandChildClosed);
        Assert.Null(host.Ui.GetWindow("child_reload"));
        Assert.Null(host.Ui.GetWindow("grandchild_reload"));
        Assert.False(childOnReload.IsOpen);
        Assert.False(grandChild.IsOpen);
        Assert.Equal(0, host.SubscribedUiEventCount);
        Assert.Throws<ObjectDisposedException>(() => _ = childOnReload.Text);
        Assert.Throws<ObjectDisposedException>(() => _ = grandChild.Text);
        Assert.DoesNotContain(childOnReload, parent.Children);

        // 2. Parent Close cleans nested CreateWindowInto child window, subscriptions, and detaches from parent.Children.
        IUiWindow childOnClose = host.Ui.CreateWindowInto(parent.Require<IUiControl>("Slot"), "Art/UI/p_child.ui", "child_close");
        bool childOnCloseClosed = false;
        childOnClose.Closed += () => childOnCloseClosed = true;
        host.HandleServerUiCommand(new ServerUiCommand(ServerUiCommandKind.SubscribeEvent, "child_close", ControlPath: "Btn", EventName: "Clicked"));
        Assert.Equal(1, host.SubscribedUiEventCount);

        parent.Close();

        Assert.True(childOnCloseClosed);
        Assert.Null(host.Ui.GetWindow("child_close"));
        Assert.False(childOnClose.IsOpen);
        Assert.Equal(0, host.SubscribedUiEventCount);
        Assert.DoesNotContain(childOnClose, parent.Require<IUiControl>("Slot").Children);

        // 3. Direct Close() on a CreateWindowInto child detaches cleanly from parent.Children and clears subscriptions.
        IUiWindow parent2 = host.Ui.Load("Art/UI/p_parent.ui", "parent_win_2");
        IUiWindow directChild = host.Ui.CreateWindowInto(parent2, "Art/UI/p_child.ui", "direct_child");
        host.HandleServerUiCommand(new ServerUiCommand(ServerUiCommandKind.SubscribeEvent, "direct_child", ControlPath: "Btn", EventName: "Clicked"));
        Assert.Contains(directChild, parent2.Children);
        Assert.Equal(1, host.SubscribedUiEventCount);

        directChild.Close();

        Assert.False(directChild.IsOpen);
        Assert.Null(host.Ui.GetWindow("direct_child"));
        Assert.Equal(0, host.SubscribedUiEventCount);
        Assert.DoesNotContain(directChild, parent2.Children);
    }

    private sealed class ActionOnDestroyCustomControl(string typeName, Action onDestroy) : ICustomUiControl
    {
        public int InitCount { get; private set; }
        public int DestroyCount { get; private set; }
        public string ControlTypeName => typeName;
        public void OnInitialize(IUiControl hostControl) => InitCount++;
        public void OnPropertyChanged(string propertyName, string value) { }
        public void OnFrame(float deltaSeconds) { }
        public void OnDestroy()
        {
            DestroyCount++;
            onDestroy();
        }
    }

    [Fact]
    public void ReloadInternal_WhenCustomControlOnDestroyClosesOrDestroysWindow_DoesNotRebuildOrReinitialize()
    {
        using var bridge = new SimulatedNativeBridge { EnableSimulatedNativeUi = true };
        using var host = new ClientHostRuntime(bridge);
        host.Start();

        bridge.AddStockTextFile("Art/UI/p412f.ui", """
            begin Window
              id = "p412f_win"
              begin CloseOnDestroyCtrl
                id = "Custom"
              end
              begin Button
                id = "Btn"
              end
            end
            """);

        IUiWindow? activeWindow = null;
        ActionOnDestroyCustomControl? ctrlClose = null;
        using (host.Ui.RegisterControl("CloseOnDestroyCtrl", () =>
               {
                   ctrlClose = new ActionOnDestroyCustomControl("CloseOnDestroyCtrl", () => activeWindow?.Close());
                   return ctrlClose;
               }))
        {
            activeWindow = host.Ui.Load("Art/UI/p412f.ui", "close_on_destroy");
            host.HandleServerUiCommand(new ServerUiCommand(ServerUiCommandKind.SubscribeEvent, "close_on_destroy", ControlPath: "Btn", EventName: "Clicked"));
            Assert.NotNull(ctrlClose);
            Assert.Equal(1, ctrlClose!.InitCount);
            Assert.Equal(1, host.SubscribedUiEventCount);

            activeWindow.Reload();

            Assert.False(activeWindow.IsOpen);
            Assert.Empty(activeWindow.Children);
            Assert.Equal(1, ctrlClose.InitCount);
            Assert.Equal(1, ctrlClose.DestroyCount);
            Assert.Null(host.Ui.GetWindow("close_on_destroy"));
            Assert.Equal(0, host.SubscribedUiEventCount);
        }

        IUiWindow? destroyedWindow = null;
        ActionOnDestroyCustomControl? ctrlDestroy = null;
        using (host.Ui.RegisterControl("CloseOnDestroyCtrl", () =>
               {
                   ctrlDestroy = new ActionOnDestroyCustomControl("CloseOnDestroyCtrl", () => destroyedWindow?.Destroy());
                   return ctrlDestroy;
               }))
        {
            destroyedWindow = host.Ui.Load("Art/UI/p412f.ui", "destroy_on_destroy");
            host.HandleServerUiCommand(new ServerUiCommand(ServerUiCommandKind.SubscribeEvent, "destroy_on_destroy", ControlPath: "Btn", EventName: "Clicked"));
            Assert.NotNull(ctrlDestroy);
            Assert.Equal(1, ctrlDestroy!.InitCount);

            destroyedWindow.Reload();

            Assert.False(destroyedWindow.IsOpen);
            Assert.Equal(1, ctrlDestroy.InitCount);
            Assert.Equal(1, ctrlDestroy.DestroyCount);
            Assert.Null(host.Ui.GetWindow("destroy_on_destroy"));
            Assert.Equal(0, host.SubscribedUiEventCount);
            Assert.Throws<ObjectDisposedException>(() => _ = destroyedWindow.Children);
        }
    }

    [Fact]
    public void CreateWindowInto_DestroyAndRemoveControlInsideChildWindow_CleansChildSubscriptionsWithoutBreakingParentCascade()
    {
        using var bridge = new SimulatedNativeBridge { EnableSimulatedNativeUi = true };
        using var host = new ClientHostRuntime(bridge);
        host.Start();

        bridge.AddStockTextFile("Art/UI/p412g_parent.ui", """
            begin Window
              id = "parent_id"
              begin Button
                id = "ParentBtn"
              end
              begin Frame
                id = "Slot"
              end
            end
            """);
        bridge.AddStockTextFile("Art/UI/p412g_child.ui", """
            begin Window
              id = "child_template"
              begin Button
                id = "ChildBtn"
              end
              begin Button
                id = "RemoveTargetBtn"
              end
              begin Frame
                id = "SubPanel"
                begin Button
                  id = "DeepBtn"
                end
              end
            end
            """);

        IUiWindow parent = host.Ui.Load("Art/UI/p412g_parent.ui", "parent_id");
        IUiControl slot = parent.Require<IUiControl>("Slot");
        IUiWindow child = host.Ui.CreateWindowInto(slot, "Art/UI/p412g_child.ui", "child_id");

        IButton childBtn = child.Require<IButton>("ChildBtn");
        IButton removeTargetBtn = child.Require<IButton>("RemoveTargetBtn");
        IUiControl subPanel = child.Require<IUiControl>("SubPanel");
        IButton deepBtn = child.Require<IButton>("SubPanel.DeepBtn");

        Assert.Equal("child_id", child.Path);
        Assert.Equal("ChildBtn", childBtn.Path);
        Assert.Equal("RemoveTargetBtn", removeTargetBtn.Path);
        Assert.Equal("SubPanel.DeepBtn", deepBtn.Path);

        host.HandleServerUiCommand(new ServerUiCommand(ServerUiCommandKind.SubscribeEvent, "parent_id", ControlPath: "ParentBtn", EventName: "Clicked"));
        host.HandleServerUiCommand(new ServerUiCommand(ServerUiCommandKind.SubscribeEvent, "child_id", ControlPath: "ChildBtn", EventName: "Clicked"));
        host.HandleServerUiCommand(new ServerUiCommand(ServerUiCommandKind.SubscribeEvent, "child_id", ControlPath: "RemoveTargetBtn", EventName: "Clicked"));
        host.HandleServerUiCommand(new ServerUiCommand(ServerUiCommandKind.SubscribeEvent, "child_id", ControlPath: "SubPanel.DeepBtn", EventName: "Clicked"));
        Assert.Equal(4, host.SubscribedUiEventCount);

        // 1. Destroying ChildBtn inside embedded child window cleans ("child_id", "ChildBtn", "Clicked") only.
        childBtn.Destroy();
        Assert.Equal(3, host.SubscribedUiEventCount);
        Assert.Null(child.Find("ChildBtn"));
        Assert.Throws<ObjectDisposedException>(() => _ = childBtn.Text);

        // 2. Removing RemoveTargetBtn via child.RemoveChild cleans ("child_id", "RemoveTargetBtn", "Clicked") only.
        Assert.True(child.RemoveChild(removeTargetBtn));
        Assert.Equal(2, host.SubscribedUiEventCount);
        Assert.Null(child.Find("RemoveTargetBtn"));

        // 3. Destroying SubPanel subtree inside embedded child window cleans ("child_id", "SubPanel.DeepBtn", "Clicked"), preserving parent sub.
        subPanel.Destroy();
        Assert.Equal(1, host.SubscribedUiEventCount);
        Assert.Null(child.Find("SubPanel.DeepBtn"));

        // 4. Parent Reload & Close still cascade to remaining embedded CreateWindowInto child windows.
        IUiWindow childCascade = host.Ui.CreateWindowInto(slot, "Art/UI/p412g_child.ui", "child_cascade");
        host.HandleServerUiCommand(new ServerUiCommand(ServerUiCommandKind.SubscribeEvent, "child_cascade", ControlPath: "ChildBtn", EventName: "Clicked"));
        Assert.Equal(2, host.SubscribedUiEventCount);

        parent.Reload();
        Assert.True(parent.IsOpen);
        Assert.False(child.IsOpen);
        Assert.False(childCascade.IsOpen);
        Assert.Null(host.Ui.GetWindow("child_cascade"));
        Assert.Equal(0, host.SubscribedUiEventCount);

        host.HandleServerUiCommand(new ServerUiCommand(ServerUiCommandKind.SubscribeEvent, "parent_id", ControlPath: "ParentBtn", EventName: "Clicked"));
        IUiWindow childOnClose = host.Ui.CreateWindowInto(parent.Require<IUiControl>("Slot"), "Art/UI/p412g_child.ui", "child_close");
        host.HandleServerUiCommand(new ServerUiCommand(ServerUiCommandKind.SubscribeEvent, "child_close", ControlPath: "ChildBtn", EventName: "Clicked"));
        Assert.Equal(2, host.SubscribedUiEventCount);

        parent.Close();
        Assert.False(parent.IsOpen);
        Assert.False(childOnClose.IsOpen);
        Assert.Null(host.Ui.GetWindow("child_close"));
        Assert.Equal(0, host.SubscribedUiEventCount);
    }

    [Fact]
    public void SimulatedNativeBridge_SimulateIncomingChat_FailsOpenWhenUnscopedInterceptorThrows()
    {
        using var sim = new SimulatedNativeBridge();
        using var host = new ClientHostRuntime(sim);
        host.Start();

        using IDisposable throwing = host.Chat.RegisterInterceptor((_, _) => throw new InvalidOperationException("unscoped boom"));
        Assert.False(sim.SimulateIncomingChat("System", "hello"));

        string? observedText = null;
        using IDisposable observer = host.Chat.RegisterInterceptor((_, text) =>
        {
            observedText = text;
            return false;
        });

        Assert.False(sim.SimulateIncomingChat("Global", "pass through"));
        Assert.Equal("pass through", observedText);

        using IDisposable suppressor = host.Chat.RegisterInterceptor((_, text) => text.Contains("block", StringComparison.Ordinal));
        Assert.True(sim.SimulateIncomingChat("Global", "please block me"));
        Assert.False(sim.SimulateIncomingChat("Global", "allow me"));
        Assert.Equal("allow me", observedText);
    }

    [Fact]
    public unsafe void NativeUiUnsubscribe_CalledOnDispose_ForNativeShimBridge()
    {
        NmAbiTable table = CreateMockAbiTable();
        using var bridge = new NativeShimBridge((nint)(&table));
        IDisposable sub = bridge.SubscribeNativeUiClick(0x2000, () => { });
        Assert.Empty(s_unsubscribedTokens);
        sub.Dispose();
        Assert.Single(s_unsubscribedTokens);
    }

    [Fact]
    public unsafe void Clipboard_Chat_NetRaw_OpcodeStreamFallback_FileOverlay_And_RawHookChains_WorkAndIsolateFaults()
    {
        NmAbiTable table = CreateMockAbiTable();
        using var nativeBridge = new NativeShimBridge((nint)(&table));
        using var nativeHost = new ClientHostRuntime(nativeBridge);
        nativeHost.Start();

        nativeHost.Clipboard.SetText("Path of Exile UTF-8 ✓");
        Assert.Equal("Path of Exile UTF-8 ✓", nativeHost.Clipboard.GetText());

        Assert.Throws<ArgumentException>(() => nativeHost.FileOverlayService.ReadStockGameFile("../secret.txt"));
        byte[]? readBack = nativeHost.FileOverlayService.ReadStockGameFile("Metadata/test.txt");
        Assert.Equal(s_stockBytes, readBack);
        Assert.Contains(s_lastAllocatedStockBuffer, s_freedBuffers);

        // Chat interceptor return int (1 suppressed, 0 pass) + fail-open on exception
        nativeHost.Scripts.RegisterChatInterceptor((_, _) => throw new InvalidOperationException("boom"));
        byte[] sender = "Global\0"u8.ToArray();
        byte[] msg = "hello\0"u8.ToArray();
        fixed (byte* pSender = sender)
        fixed (byte* pMsg = msg)
        {
            Assert.Equal(0, s_chatCallback(pSender, pMsg, s_chatUserData));
            using IDisposable suppress = nativeHost.Scripts.RegisterChatInterceptor((_, text) => text.Contains("hello", StringComparison.Ordinal));
            Assert.Equal(1, s_chatCallback(pSender, pMsg, s_chatUserData));
        }

        // Opcode fallback stream read (payload == null, streamHandle points to fake BufferStreamIn with +0x270=cur, +0x278=end)
        byte[]? fallbackRead = null;
        using IDisposable groupSub = nativeHost.Opcodes.RegisterGroup(0x0400, 0x0405, (op, payload) =>
        {
            if (op == 0x0402)
            {
                fallbackRead = payload.ToArray();
            }
        });
        void* fakeStream = NativeMemory.AllocZeroed(0x280);
        try
        {
            *(ulong*)((byte*)fakeStream + 0x270) = (ulong)s_streamFallbackBytes.Length;
            *(ulong*)((byte*)fakeStream + 0x278) = 0UL;
            int handled = s_opcodeCallback(0x0402, fakeStream, null, 0, s_opcodeUserData);
            Assert.Equal(1, handled);
            Assert.Equal(s_streamFallbackBytes, fallbackRead);
        }
        finally
        {
            NativeMemory.Free(fakeStream);
        }

        using var sim = new SimulatedNativeBridge();
        using var simHost = new ClientHostRuntime(sim);
        simHost.Start();

        sim.AddStockTextFile("Art/Models/box.sm", "version 1\n");
        byte[]? smBytes = simHost.FileOverlayService.ReadStockGameFile("Art/Models/box.sm");
        Assert.NotNull(smBytes);
        Assert.False(smBytes!.Length >= 2 && smBytes[0] == 0xFF && smBytes[1] == 0xFE);

        Assert.Throws<ArgumentOutOfRangeException>(() => simHost.Opcodes.RegisterHandler(0x02FB, _ => { }));
        Assert.Throws<ArgumentOutOfRangeException>(() => simHost.Opcodes.RegisterGroup(0x0200, 0x02FF, (_, _) => { }));

        ushort seenOpcode = 0;
        byte[] seenBytes = [];
        simHost.Opcodes.RegisterGroup(0x0300, 0x0305, (op, data) =>
        {
            seenOpcode = op;
            seenBytes = data.ToArray();
        });
        sim.SimulateIncomingRawPacket(0x0302, new byte[] { 1, 2, 3 });
        Assert.Equal((ushort)0x0302, seenOpcode);
        Assert.Equal([1, 2, 3], seenBytes);

        // Per-subscriber fault isolation in GameLifecycle
        int secondSubscriberTicks = 0;
        simHost.Lifecycle.Frame += _ => throw new InvalidOperationException("first frame sub failed");
        simHost.Lifecycle.Frame += _ => secondSubscriberTicks++;
        sim.SimulateFrame();
        Assert.Equal(1, secondSubscriberTicks);

        // ClientNetChannel.On<T> isolates malformed JSON inside faultGuard
        INetChannel badChan = simHost.Router.CreateChannel("mod-a");
        bool badCalled = false;
        badChan.On<Dictionary<string, int>>(_ => badCalled = true, "bad_json");
        sim.SimulateIncomingRulesetPacket(201, "{\"Channel\":\"mod-a\",\"MessageType\":\"bad_json\",\"PayloadJson\":\"{NOT_JSON\"}"u8.ToArray());
        Assert.False(badCalled);

        // RawMemory hook chains + CallEx
        List<int> order = [];
        sim.ResolveSymbol("frame.state_stack_pass_a_hook", out _, out nint hookAddr);
        IDisposable hPost = simHost.RawMemory.InstallHookChain("frame.state_stack_pass_a_hook", HookPhase.Post, _ => order.Add(30), priority: 0);
        IDisposable hPreLow = simHost.RawMemory.InstallHookChain("frame.state_stack_pass_a_hook", HookPhase.Pre, ctx =>
        {
            order.Add(20);
            ctx.SkipOriginal = true;
            ctx.ReturnValue = 777;
        }, priority: 20);
        IDisposable hPreHigh = simHost.RawMemory.InstallHookChain("frame.state_stack_pass_a_hook", HookPhase.Pre, _ => order.Add(5), priority: 5);

        bool origCalled = false;
        HookContext res = sim.SimulateHookChainCall(hookAddr, new HookContext(hookAddr), _ => { origCalled = true; return 111; });
        Assert.Equal([5, 20, 30], order);
        Assert.False(origCalled);
        Assert.Equal(777UL, res.ReturnValue);

        hPreLow.Dispose();
        order.Clear();
        HookContext res2 = sim.SimulateHookChainCall(hookAddr, new HookContext(hookAddr), _ => { origCalled = true; return 111; });
        Assert.Equal([5, 30], order);
        Assert.True(origCalled);
        Assert.Equal(111UL, res2.ReturnValue);
        hPost.Dispose();
        hPreHigh.Dispose();

        sim.RegisterNativeExStub(hookAddr, (args, returnIsFloat) =>
        {
            ulong intRes = returnIsFloat ? 0UL : (ulong)args.Length + args[7].RawBits;
            double floatRes = returnIsFloat
                ? BitConverter.UInt64BitsToDouble(args[0].RawBits) + BitConverter.UInt64BitsToDouble(args[1].RawBits)
                : 0.0;
            return new NativeCallResult(intRes, BitConverter.DoubleToUInt64Bits(floatRes));
        });
        NativeCallArgument[] exArgs =
        [
            NativeCallArgument.FromDouble(1.5),
            NativeCallArgument.FromDouble(2.5),
            NativeCallArgument.FromInt(3),
            NativeCallArgument.FromInt(4),
            NativeCallArgument.FromInt(5),
            NativeCallArgument.FromInt(6),
            NativeCallArgument.FromInt(7),
            NativeCallArgument.FromInt(10),
        ];
        Assert.Equal(18UL, simHost.RawMemory.CallEx(hookAddr, exArgs).RawInt);
        Assert.Equal(4.0, simHost.RawMemory.CallEx(hookAddr, exArgs, returnIsFloat: true).AsDouble, precision: 5);
    }

    [Fact]
    public async Task ModUnload_DetachesLifecycleRulesetsAudioWorldChatOpcodes_And_CollectsAlc()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string modDir = Path.Combine(tempRoot, "subscriber-mod");
            Directory.CreateDirectory(modDir);
            string dllPath = Path.Combine(modDir, "subscriber.Client.dll");
            EmitSubscriberLifecycleModDll(dllPath);
            File.WriteAllText(Path.Combine(modDir, "mod.json"), """
                {
                  "id": "subscriber-mod",
                  "name": "Subscriber Mod",
                  "version": "1.0.0",
                  "nativeClient": {
                    "sdkVersionRange": ">=0.2.0 <2.0.0",
                    "assembly": "subscriber.Client.dll"
                  }
                }
                """);

            using var bridge = new SimulatedNativeBridge();
            using var host = new ClientHostRuntime(bridge);
            host.Start();

            WeakReference alcWeakRef = LoadAndVerifySubscriberMod(host, modDir, bridge);

            ModUnloadResult unload = await host.Loader.UnloadModAsync("subscriber-mod");
            Assert.True(unload.UnloadedCleanly);
            await AssertCollectedAsync(alcWeakRef);

            bridge.SimulateFrame();
            Assert.False(bridge.SimulateIncomingChat("Local", "test"));
            Assert.Equal(0, bridge.GetWorldObjects().Count(w => w.IsClientOnly));
            Assert.False(bridge.IsAudioPlaying(1));
        }
        finally
        {
            CleanupDirectory(tempRoot);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadAndVerifySubscriberMod(ClientHostRuntime host, string modDir, SimulatedNativeBridge bridge)
    {
        LoadedClientMod mod = host.Loader.LoadModFromDirectory(modDir);
        Assert.NotNull(mod.LoadContext);
        bridge.SimulateFrame();
        Assert.Equal(1, bridge.ReadStat("sub_frame_count", 0));
        return new WeakReference(mod.LoadContext!, trackResurrection: true);
    }

    private static async Task AssertCollectedAsync(WeakReference weakRef)
    {
        for (int i = 0; i < 20 && weakRef.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Yield();
        }

        Assert.False(weakRef.IsAlive);
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"nm-m2-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CleanupDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static void WriteEmittedMod(
        string modsRoot,
        string id,
        string typeName,
        string statKey,
        int statValue,
        string[]? depends = null,
        bool enabled = true,
        string? overrideHash = null,
        string? cctorMarkerPath = null)
    {
        string modDir = Path.Combine(modsRoot, id);
        Directory.CreateDirectory(modDir);
        string dllFile = $"{id}.Client.dll";
        string dllPath = Path.Combine(modDir, dllFile);
        EmitMinimalModDll(dllPath, typeName, statKey, statValue, cctorMarkerPath);

        string actualHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(dllPath)));
        string payloadHash = overrideHash ?? actualHash;

        var manifestObj = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["name"] = id,
            ["version"] = "1.0.0",
            ["enabled"] = enabled,
            ["depends"] = depends ?? [],
            ["nativeClient"] = new Dictionary<string, object?>
            {
                ["sdkVersionRange"] = ">=0.2.0 <2.0.0",
                ["assembly"] = dllFile,
                ["payloadHash"] = payloadHash,
            },
        };
        File.WriteAllText(Path.Combine(modDir, "mod.json"), JsonSerializer.Serialize(manifestObj));
    }

    private static void EmitMinimalModDll(string outputDllPath, string typeName, string statKey, int statValue, string? cctorMarkerPath = null)
    {
        var ab = new PersistedAssemblyBuilder(new AssemblyName($"{typeName}Asm"), typeof(object).Assembly);
        ModuleBuilder mb = ab.DefineDynamicModule($"{typeName}Module");
        TypeBuilder tb = mb.DefineType(
            $"DynamicMods.{typeName}",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed,
            typeof(object),
            [typeof(IClientMod)]);

        if (!string.IsNullOrWhiteSpace(cctorMarkerPath))
        {
            ConstructorBuilder cctor = tb.DefineConstructor(
                MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                CallingConventions.Standard,
                Type.EmptyTypes);
            ILGenerator cil = cctor.GetILGenerator();
            cil.Emit(OpCodes.Ldstr, cctorMarkerPath);
            cil.Emit(OpCodes.Ldstr, "executed");
            cil.Emit(OpCodes.Call, typeof(File).GetMethod(nameof(File.WriteAllText), [typeof(string), typeof(string)])!);
            cil.Emit(OpCodes.Ret);
        }

        MethodInfo ifaceLoad = typeof(IClientMod).GetMethod(nameof(IClientMod.Load))!;
        MethodBuilder loadMethod = tb.DefineMethod(
            nameof(IClientMod.Load),
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
            typeof(void),
            [typeof(IClientContext)]);

        ILGenerator il = loadMethod.GetILGenerator();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, typeof(IClientContext).GetProperty(nameof(IClientContext.Game))!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, typeof(IGameServices).GetProperty(nameof(IGameServices.Stats))!.GetGetMethod()!);
        il.Emit(OpCodes.Ldstr, statKey);
        il.Emit(OpCodes.Ldc_I4, statValue);
        il.Emit(OpCodes.Callvirt, typeof(IStatsService).GetMethod(nameof(IStatsService.SetLocalStat), [typeof(string), typeof(int)])!);
        il.Emit(OpCodes.Ret);

        tb.DefineMethodOverride(loadMethod, ifaceLoad);
        tb.CreateType();
        ab.Save(outputDllPath);
    }

    private static void EmitSubscriberLifecycleModDll(string outputDllPath)
    {
        var ab = new PersistedAssemblyBuilder(new AssemblyName("SubscriberModAsm"), typeof(object).Assembly);
        ModuleBuilder mb = ab.DefineDynamicModule("SubscriberModModule");
        TypeBuilder tb = mb.DefineType(
            "DynamicMods.SubscriberMod",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed,
            typeof(object),
            [typeof(IClientMod)]);

        FieldBuilder statsField = tb.DefineField("_stats", typeof(IStatsService), FieldAttributes.Private);

        MethodBuilder onFrame = tb.DefineMethod(
            "OnFrame",
            MethodAttributes.Private | MethodAttributes.HideBySig,
            typeof(void),
            [typeof(float)]);
        ILGenerator fil = onFrame.GetILGenerator();
        fil.Emit(OpCodes.Ldarg_0);
        fil.Emit(OpCodes.Ldfld, statsField);
        fil.Emit(OpCodes.Ldstr, "sub_frame_count");
        fil.Emit(OpCodes.Ldc_I4_1);
        fil.Emit(OpCodes.Callvirt, typeof(IStatsService).GetMethod(nameof(IStatsService.SetLocalStat), [typeof(string), typeof(int)])!);
        fil.Emit(OpCodes.Ret);

        MethodInfo ifaceLoad = typeof(IClientMod).GetMethod(nameof(IClientMod.Load))!;
        MethodBuilder loadMethod = tb.DefineMethod(
            nameof(IClientMod.Load),
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
            typeof(void),
            [typeof(IClientContext)]);

        ILGenerator il = loadMethod.GetILGenerator();
        LocalBuilder gameLocal = il.DeclareLocal(typeof(IGameServices));
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, typeof(IClientContext).GetProperty(nameof(IClientContext.Game))!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, gameLocal);

        // _stats = context.Game.Stats;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, gameLocal);
        il.Emit(OpCodes.Callvirt, typeof(IGameServices).GetProperty(nameof(IGameServices.Stats))!.GetGetMethod()!);
        il.Emit(OpCodes.Stfld, statsField);

        // game.Lifecycle.Frame += OnFrame;
        il.Emit(OpCodes.Ldloc, gameLocal);
        il.Emit(OpCodes.Callvirt, typeof(IGameServices).GetProperty(nameof(IGameServices.Lifecycle))!.GetGetMethod()!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldftn, onFrame);
        il.Emit(OpCodes.Newobj, typeof(Action<float>).GetConstructor([typeof(object), typeof(nint)])!);
        il.Emit(OpCodes.Callvirt, typeof(IGameLifecycle).GetEvent(nameof(IGameLifecycle.Frame))!.GetAddMethod()!);

        // game.Audio.PlaySoundFile("Audio/test.ogg", 0.5f, true);
        il.Emit(OpCodes.Ldloc, gameLocal);
        il.Emit(OpCodes.Callvirt, typeof(IGameServices).GetProperty(nameof(IGameServices.Audio))!.GetGetMethod()!);
        il.Emit(OpCodes.Ldstr, "Audio/test.ogg");
        il.Emit(OpCodes.Ldc_R4, 0.5f);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, typeof(IAudioService).GetMethod(nameof(IAudioService.PlaySoundFile), [typeof(string), typeof(float), typeof(bool)])!);
        il.Emit(OpCodes.Pop);

        // game.World.SpawnClientObject("Metadata/Item.ot", 1f, 2f, 3f);
        il.Emit(OpCodes.Ldloc, gameLocal);
        il.Emit(OpCodes.Callvirt, typeof(IGameServices).GetProperty(nameof(IGameServices.World))!.GetGetMethod()!);
        il.Emit(OpCodes.Ldstr, "Metadata/Item.ot");
        il.Emit(OpCodes.Ldc_R4, 1f);
        il.Emit(OpCodes.Ldc_R4, 2f);
        il.Emit(OpCodes.Ldc_R4, 3f);
        il.Emit(OpCodes.Callvirt, typeof(IWorldService).GetMethod(nameof(IWorldService.SpawnClientObject))!);
        il.Emit(OpCodes.Pop);

        il.Emit(OpCodes.Ret);

        tb.DefineMethodOverride(loadMethod, ifaceLoad);
        tb.CreateType();
        ab.Save(outputDllPath);
    }

    [Fact]
    public void Bridge_DrainGameThreadQueueZeroMaxItems_AndSendRawPayloadOver64K_Guards()
    {
        using var bridge = new SimulatedNativeBridge();
        int ran = 0;
        Assert.True(bridge.EnqueueGameThread(() => ran++));

        Assert.Throws<ArgumentOutOfRangeException>(() => bridge.DrainGameThreadQueue(0.016f, -1));
        Assert.Equal(0, bridge.DrainGameThreadQueue(0.016f, 0));
        Assert.Equal(0, ran);
        Assert.Equal(1, bridge.DrainGameThreadQueue(0.016f, 1));
        Assert.Equal(1, ran);

        byte[] oversized = new byte[65537];
        Assert.Throws<ArgumentOutOfRangeException>(() => bridge.SendRawPacket(0x02FC, oversized));
    }
}
