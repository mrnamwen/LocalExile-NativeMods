using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using LocalExile.Client.Host;
using LocalExile.Client.Host.Addresses;
using LocalExile.Client.Host.Native;
using LocalExile.Client.Sdk.Addresses;
using Xunit;

namespace LocalExile.Client.Tests;

[Collection("NativeEntryPointSerial")]
public sealed class AddressLibraryTests
{
    private const string Sample3293dJson = """
        {
          "schemaVersion": 1,
          "gameVersion": "3.29.3d",
          "imageBase": "0x140000000",
          "builds": [
            {
              "buildNumber": 328228,
              "label": "3.29.3d Steam",
              "executableSha256": "e9ba3d31c4d922e1826ef01d02fd22178a001b29e17264b68fc2446efd41f53d",
              "executableSize": 79394136,
              "source": "ClientBuilds.cs"
            }
          ],
          "symbols": [
            {
              "name": "frame.state_stack_pass_a_hook",
              "category": "frame",
              "kind": "code",
              "rva": "0x1528fbf",
              "va": "0x141528fbf",
              "expectedBytes": "488b5d284883eb10",
              "status": "live_verified",
              "callingConvention": "win64",
              "source": "spikes/agent.js"
            },
            {
              "name": "rulesets.creator_table",
              "category": "rulesets",
              "kind": "data",
              "rva": "0x475dc70",
              "va": "0x14475dc70",
              "expectedBytes": null,
              "status": "static_verified",
              "source": "10-verification.md §3"
            }
          ],
          "offsets": [
            {
              "name": "control.id",
              "struct": "UiControl",
              "offset": "0x1b8",
              "type": "std::wstring",
              "status": "live_verified",
              "source": "07-spike-results.md"
            }
          ],
          "constants": {
            "rulesetS2cOpcode": "0x2c9",
            "rulesetC2sOpcode": "0x2ca",
            "rulesetMaxPayload": 65535
          }
        }
        """;

    private const string Sample3293cJson = """
        {
          "schemaVersion": 1,
          "gameVersion": "3.29.3c",
          "imageBase": "0x140000000",
          "builds": [
            {
              "buildNumber": 327984,
              "label": "3.29.3c",
              "executableSha256": "cc8aa38345be2a837575215d2ce142ebecf7b007345843265b335ebb8ac5964c",
              "executableSize": 79390000,
              "source": "ClientBuilds.cs"
            }
          ],
          "symbols": [
            {
              "name": "frame.state_stack_pass_a_hook",
              "category": "frame",
              "kind": "code",
              "rva": "0x1528fbf",
              "va": "0x141528fbf",
              "expectedBytes": "488b5d284883eb10",
              "status": "code_shared_3_29_3c",
              "source": "08-deep-analysis.md"
            },
            {
              "name": "rulesets.creator_table",
              "category": "rulesets",
              "kind": "data",
              "rva": null,
              "va": null,
              "expectedBytes": null,
              "status": "unverified_3_29_3c",
              "source": "10-verification.md §3"
            }
          ],
          "offsets": [],
          "constants": {
            "rulesetS2cOpcode": "0x2c9"
          }
        }
        """;

    [Fact]
    public void Parse3293d_ResolvesRebasedAddressesOffsetsAndConstants()
    {
        nint rebased = unchecked((nint)0x7FF7_1000_0000L);
        JsonAddressLibrary lib = JsonAddressLibrary.Parse(Sample3293dJson, runtimeImageBase: rebased);

        Assert.Equal(ClientBuildId.Build3293d, lib.Build);
        Assert.Equal("3.29.3d", lib.BuildVersion);
        Assert.True(lib.MatchesExecutableSha256("e9ba3d31c4d922e1826ef01d02fd22178a001b29e17264b68fc2446efd41f53d"));
        Assert.True(lib.TryResolveRva("frame.state_stack_pass_a_hook", out ulong rva));
        Assert.Equal(0x1528FBFul, rva);
        Assert.Equal(rebased + (nint)0x1528FBF, lib.ResolveAddress("frame.state_stack_pass_a_hook"));

        Assert.True(lib.TryGetOffset("control.id", out StructOffsetEntry? off));
        Assert.Equal(0x1B8ul, off!.Offset);
        Assert.Equal("0x2c9", lib.Constants["rulesetS2cOpcode"]);
        Assert.Equal("65535", lib.Constants["rulesetMaxPayload"]);
    }

    [Fact]
    public void Parse3293c_RejectsUnverifiedDataSymbolAndMissingSymbol()
    {
        JsonAddressLibrary lib = JsonAddressLibrary.Parse(Sample3293cJson);

        Assert.Equal(ClientBuildId.Build3293c, lib.Build);
        Assert.True(lib.TryResolveRva("frame.state_stack_pass_a_hook", out ulong codeRva));
        Assert.Equal(0x1528FBFul, codeRva);

        // Data symbol unverified on 3.29.3c must never resolve!
        Assert.True(lib.TryGetEntry("rulesets.creator_table", out AddressEntry? unverified));
        Assert.False(unverified!.IsVerified);
        Assert.False(lib.TryResolveRva("rulesets.creator_table", out _));
        Assert.False(lib.TryResolveAddress("rulesets.creator_table", out _));
        Assert.Throws<InvalidOperationException>(() => lib.ResolveAddress("rulesets.creator_table"));

        // Completely missing symbol throws KeyNotFoundException.
        Assert.Throws<KeyNotFoundException>(() => lib.ResolveAddress("nonexistent.symbol"));
    }

    [Fact]
    public void TryLoadForExecutableSha256_SelectsMatchingBuildDatabase()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"nm-addr-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "3.29.3d.json"), Sample3293dJson);
            File.WriteAllText(Path.Combine(tempDir, "3.29.3c.json"), Sample3293cJson);
            File.WriteAllText(Path.Combine(tempDir, "address-library.schema.json"), "{}");

            JsonAddressLibrary? matched = JsonAddressLibrary.TryLoadForExecutableSha256(
                tempDir,
                "cc8aa38345be2a837575215d2ce142ebecf7b007345843265b335ebb8ac5964c");

            Assert.NotNull(matched);
            Assert.Equal(ClientBuildId.Build3293c, matched!.Build);
            Assert.Null(JsonAddressLibrary.TryLoadForExecutableSha256(tempDir, "0000000000000000000000000000000000000000000000000000000000000000"));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public unsafe void NativeShimBridge_BindsToUnmanagedAbiTableAndNativeEntryPoint()
    {
        // Exact C ABI 0.3.0 struct size parity with src/native-shim/include/nativemods_abi.h (x64).
        Assert.Equal(120, sizeof(NmHostInfo));
        Assert.Equal(48, sizeof(NmSymbolInfo));
        Assert.Equal(16, sizeof(NmGameAllocHeader));
        Assert.Equal(528, sizeof(NmExceptionInfo));
        Assert.Equal(128, sizeof(NmHookContext));
        Assert.Equal(104, sizeof(NmNativeCallDesc));
        Assert.Equal(280, sizeof(NmScreensState));
        Assert.Equal(40, sizeof(NmInputEvent));
        Assert.Equal(448, sizeof(NmItemSnapshot));
        Assert.Equal(952, sizeof(NmHoverState));
        Assert.Equal(720, sizeof(NmAbiTable));
        Assert.Equal(0x00000300u, NativeShimConstants.AbiVersion);
        Assert.Equal("LocalExile.Client.Host.NativeEntryPoint", typeof(NativeEntryPoint).FullName);

        NmAbiTable table = default;
        table.StructSize = (uint)sizeof(NmAbiTable);
        table.AbiVersion = NativeShimConstants.AbiVersion;
        table.GetHostInfo = &MockGetHostInfo;
        table.LookupSymbol = &MockLookupSymbol;
        table.GetSymbolAt = &MockGetSymbolAt;
        table.VerifyBytes = &MockVerifyBytes;
        table.InstallInlineHook = &MockInstallInlineHook;
        table.RemoveHook = &MockRemoveHook;
        table.RegisterFrameCallback = &MockRegisterFrameCallback;
        table.UnregisterFrameCallback = &MockUnregisterFrameCallback;
        table.RegisterFileOverlay = &MockRegisterFileOverlay;
        table.UnregisterFileOverlay = &MockUnregisterFileOverlay;
        table.RegisterRulesetHandler = &MockRegisterRulesetHandler;
        table.UnregisterRulesetHandler = &MockUnregisterRulesetHandler;
        table.RegisterChatChannelHandler = &MockRegisterChatHandler;
        table.UnregisterChatChannelHandler = &MockUnregisterChatHandler;
        table.SendRulesetPacket = &MockSendRulesetPacket;
        table.SendChatLine = &MockSendChatLine;
        table.AudioPlayOgg = &MockAudioPlayOgg;
        table.AudioSetVolume = &MockAudioSetVolume;
        table.AudioIsPlaying = &MockAudioIsPlaying;
        table.AudioStop = &MockAudioStop;
        table.UiCreateWindow = &MockUiCreateWindow;
        table.UiFindChild = &MockUiFindChild;
        table.UiSetVisible = &MockUiSetVisible;
        table.UiSetText = &MockUiSetText;
        table.UiSubscribeClick = &MockUiSubscribeClick;
        table.UiGetEnabled = &MockUiGetEnabled;

        _lastSentRulesetLen = 0;
        _lastSentChat = null;
        _unregisteredCount = 0;
        _lastAudioPath = null;
        _lastAudioLoop = 0;
        _lastAudioVolume = 0f;
        _mockAudioActive = false;
        _lastNativeUiText = null;
        _lastNativeUiVisible = -1;
        _mockClickCb = null;
        _mockClickUd = null;

        int status = NativeEntryPoint.InitializeManaged((nint)(&table));
        try
        {
            Assert.Equal((int)NmStatus.Ok, status);
            ClientHostRuntime? rt = NativeEntryPoint.ActiveRuntime;
            Assert.NotNull(rt);
            Assert.Equal(ClientBuildId.Build3293d, rt!.Addresses.Build);
            Assert.Equal("3.29.3d", rt.Addresses.BuildVersion);
            Assert.True(rt.Addresses.TryResolveRva("frame.state_stack_pass_a_hook", out ulong rva));
            Assert.Equal(0x1528FBFul, rva);
            Assert.True(rt.Bridge.VerifyExpectedBytes(rt.Addresses.ResolveAddress("frame.state_stack_pass_a_hook"), "488b5d284883eb10"));

            // Registering Frame, FileOverlay, Ruleset, and Chat callbacks that all receive token=1 from separate
            // C++ counters must not collide in _pinnedCallbacks or leak GCHandles.
            using IDisposable fSub = rt.Bridge.RegisterFrameCallback(_ => { });
            using IDisposable oSub = rt.Bridge.RegisterFileOverlayResolver(_ => null);
            using IDisposable rSub = rt.Bridge.RegisterRulesetReceiver(0xA1, _ => { });
            using IDisposable cSub = rt.Bridge.RegisterChatInterceptor((_, _) => false);

            ulong hook = rt.Bridge.InstallInlineHook("frame.state_stack_pass_a_hook", 0x1234, out nint tramp);
            Assert.NotEqual(0ul, hook);
            Assert.NotEqual(nint.Zero, tramp);
            Assert.True(rt.Bridge.RemoveHook(hook));

            rt.Bridge.SendRulesetPacket(0xA0, [1, 2, 3, 4]);
            Assert.Equal(4, _lastSentRulesetLen);

            rt.Bridge.SendChatLine("/nm ping");
            Assert.Equal("/nm ping", _lastSentChat);

            // Live looping OGG audio through NmAbiTable
            int audioHandle = rt.Audio.PlaySoundFile("Audio/Mods/Maxwell/stock_market.ogg", 0.65f, loop: true);
            Assert.Equal(77, audioHandle);
            Assert.Equal("Audio/Mods/Maxwell/stock_market.ogg", _lastAudioPath);
            Assert.Equal(1, _lastAudioLoop);
            Assert.True(rt.Audio.IsChannelPlaying(audioHandle));
            rt.Audio.SetChannelVolume(audioHandle, 0.35f);
            Assert.Equal(0.35f, _lastAudioVolume, precision: 3);
            rt.Audio.StopChannel(audioHandle);
            Assert.False(rt.Audio.IsChannelPlaying(audioHandle));

            // Live native UI window creation, child binding, SetText/SetVisible sync, and native click callback
            using var uiOverlay = rt.FileOverlayService.RegisterVirtualTextFile(
                "Metadata/UI/Mods/Test/Panel.ui",
                """
                begin Frame
                    id = "panel"
                    begin Label
                        id = "status"
                        text = "Initial"
                    end
                    begin Button
                        id = "btn"
                        text = "Press"
                    end
                end
                """);
            using var win = rt.Ui.Load("Metadata/UI/Mods/Test/Panel.ui");
            var label = win.Require<LocalExile.Client.Sdk.Engine.ILabel>("status");
            var btn = win.Require<LocalExile.Client.Sdk.Engine.IButton>("btn");
            bool clicked = false;
            btn.Clicked += () => clicked = true;

            label.Text = "Maxwell Spinning!";
            Assert.Equal("Maxwell Spinning!", _lastNativeUiText);
            int callsAfterChange = _nativeUiSetTextCalls;
            label.Text = "Maxwell Spinning!"; // unchanged text must not churn native SetText
            Assert.Equal(callsAfterChange, _nativeUiSetTextCalls);
            win.Visible = false;
            Assert.Equal(0, _lastNativeUiVisible);
            win.Visible = true;
            Assert.Equal(1, _lastNativeUiVisible);

            // Invoke native click callback -> triggers managed IButton.Clicked!
            Assert.True(_mockClickCb is not null);
            _mockClickCb((void*)0x2002, _mockClickUd);
            Assert.True(clicked);

            // Auto-handshake: frames 1..29 do not send; frame 30 sends ClientHandshake automatically.
            _lastSentRulesetLen = 0;
            for (int f = 0; f < 29; f++)
            {
                rt.TickFrame(1f / 60f);
            }
            Assert.Equal(0, _lastSentRulesetLen);

            rt.TickFrame(1f / 60f); // frame #30
            Assert.True(_lastSentRulesetLen > 0);
        }
        finally
        {
            NativeEntryPoint.Shutdown();
        }

        Assert.True(_unregisteredCount >= 4);
    }

    [Fact]
    public void ClientHostRuntime_ReHandshakesOnZoneChangeAndRaisesDisconnectedOnLogout()
    {
        using var bridge = new SimulatedNativeBridge { SimulateConnectionLifecycle = true };
        var serverHandshakesSent = 0;
        var clientHandshakesSent = 0;
        var connectedCount = 0;
        var disconnectedCount = 0;

        using var host = new ClientHostRuntime(bridge, new ClientHostOptions
        {
            DisconnectDebounce = TimeSpan.FromSeconds(5),
        });
        host.Lifecycle.Connected += () => connectedCount++;
        host.Lifecycle.Disconnected += _ => disconnectedCount++;

        bridge.OnOutgoingRulesetPacket = (_, payload) =>
        {
            var env = LocalExile.Client.Sdk.Protocol.MessageCodec.DecodeEnvelope(payload.Span);
            if (env.MessageType == nameof(LocalExile.Client.Sdk.Protocol.ClientHandshake))
            {
                clientHandshakesSent++;
                serverHandshakesSent++;
                var reply = new LocalExile.Client.Sdk.Protocol.ServerHandshake(
                    ServerVersion: "LocalExile-3.29.3d",
                    BridgeVersion: LocalExile.Client.Sdk.SdkVersion.Current,
                    ProtocolVersion: LocalExile.Client.Sdk.SdkVersion.ProtocolVersion,
                    HostRulesetId: LocalExile.Client.Sdk.Extend.IRulesetRegistry.DefaultHostChannelRulesetId,
                    PreferredTransport: LocalExile.Client.Sdk.Engine.NetTransportKind.HostRuleset,
                    RequiredMods: [],
                    ServerCapabilities: ["rulesets.0x2c9"]);
                byte[] wire = LocalExile.Client.Sdk.Protocol.MessageCodec.EncodeEnvelope(
                    LocalExile.Client.Sdk.Protocol.MessageCodec.CreateTypedEnvelope(
                        LocalExile.Client.Sdk.Protocol.MessageCodec.SystemChannel, reply));
                bridge.SimulateIncomingRulesetPacket(
                    LocalExile.Client.Sdk.Extend.IRulesetRegistry.DefaultHostChannelRulesetId, wire);
            }
        };

        // 1. Login screen (token=0): no handshake sent, not connected.
        host.TickFrame();
        Assert.Equal(0, clientHandshakesSent);
        Assert.Null(host.LastServerHandshake);

        // 2. Enter Lioneye's Watch (token=0x1001): connects & sends ClientHandshake immediately.
        bridge.ActiveConnectionToken = 0x1001ul;
        host.TickFrame();
        Assert.Equal(1, connectedCount);
        Assert.Equal(1, clientHandshakesSent);
        Assert.NotNull(host.LastServerHandshake);

        // Subsequent frames on the same socket do NOT spam ClientHandshake.
        host.TickFrame();
        host.TickFrame();
        Assert.Equal(1, clientHandshakesSent);

        // 3. Zone transition Lioneye's Watch -> The Coast (token=0x1002):
        // resets LastServerHandshake and immediately re-sends ClientHandshake without firing Disconnected!
        bridge.ActiveConnectionToken = 0x1002ul;
        host.TickFrame();
        Assert.Equal(0, disconnectedCount);
        Assert.Equal(2, clientHandshakesSent);
        Assert.Equal(2, serverHandshakesSent);
        Assert.NotNull(host.LastServerHandshake);

        // 4. Brief socket handoff (0x1002 -> 0 for 1 frame -> 0x1003):
        // does NOT fire Disconnected within debounce window, still re-handshakes cleanly on 0x1003!
        bridge.ActiveConnectionToken = 0ul;
        host.TickFrame();
        Assert.Null(host.LastServerHandshake);
        Assert.Equal(0, disconnectedCount);

        bridge.ActiveConnectionToken = 0x1003ul;
        host.TickFrame();
        Assert.Equal(0, disconnectedCount);
        Assert.Equal(3, clientHandshakesSent);
        Assert.NotNull(host.LastServerHandshake);

        // 5. Full disconnect with zero debounce raises Lifecycle.Disconnected and clears handshake.
        using var bridge2 = new SimulatedNativeBridge { SimulateConnectionLifecycle = true };
        var disc2 = 0;
        using var host2 = new ClientHostRuntime(bridge2, new ClientHostOptions
        {
            DisconnectDebounce = TimeSpan.Zero,
        });
        host2.Lifecycle.Disconnected += _ => disc2++;
        bridge2.ActiveConnectionToken = 0x2001ul;
        host2.TickFrame();
        Assert.True(host2.Lifecycle.IsConnected);

        bridge2.ActiveConnectionToken = 0ul;
        host2.TickFrame();
        Assert.Equal(1, disc2);
        Assert.False(host2.Lifecycle.IsConnected);
        Assert.Null(host2.LastServerHandshake);
    }

    private static readonly byte[] MockSymName = AllocatePinnedUtf8("frame.state_stack_pass_a_hook");
    private static readonly byte[] MockSymCat = AllocatePinnedUtf8("frame");
    private static readonly byte[] MockSymBytes = AllocatePinnedUtf8("488b5d284883eb10");
    private static int _lastSentRulesetLen;
    private static string? _lastSentChat;
    private static int _unregisteredCount;
    private static string? _lastAudioPath;
    private static int _lastAudioLoop;
    private static float _lastAudioVolume;
    private static bool _mockAudioActive;
    private static string? _lastNativeUiText;
    private static int _nativeUiSetTextCalls;
    private static int _lastNativeUiVisible;
    private static unsafe delegate* unmanaged[Cdecl]<void*, void*, void> _mockClickCb;
    private static unsafe void* _mockClickUd;

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
        // Arrays are allocated on the Pinned Object Heap (GC.AllocateArray pinned:true), so these pointers never move.
        outSym->Name = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(MockSymName));
        outSym->Category = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(MockSymCat));
        outSym->Kind = NmSymbolKind.Code;
        outSym->Status = NmSymbolStatus.LiveVerified;
        outSym->Rva = 0x1528FBFul;
        outSym->RuntimeAddress = NativeShimConstants.NominalImageBase + 0x1528FBFul;
        outSym->ExpectedBytesHex = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(MockSymBytes));
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockRegisterFrameCallback(delegate* unmanaged[Cdecl]<float, void*, void> cb, void* ud, ulong* outToken)
    {
        *outToken = 1;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NmStatus MockUnregisterFrameCallback(ulong token) { Interlocked.Increment(ref _unregisteredCount); return NmStatus.Ok; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockRegisterFileOverlay(delegate* unmanaged[Cdecl]<byte*, byte**, nuint*, void*, NmStatus> cb, delegate* unmanaged[Cdecl]<byte*, void*, void> freeCb, void* ud, ulong* outToken)
    {
        *outToken = 1;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NmStatus MockUnregisterFileOverlay(ulong token) { Interlocked.Increment(ref _unregisteredCount); return NmStatus.Ok; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockRegisterRulesetHandler(uint rulesetId, delegate* unmanaged[Cdecl]<uint, byte*, uint, void*, void> cb, void* ud, ulong* outToken)
    {
        *outToken = 1;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NmStatus MockUnregisterRulesetHandler(ulong token) { Interlocked.Increment(ref _unregisteredCount); return NmStatus.Ok; }

    [Fact]
    public unsafe void NativeShimBridge_RejectsLegacy010_020_Future040AndTruncatedAbiTables()
    {
        NmAbiTable legacy010 = default;
        legacy010.StructSize = (uint)sizeof(NmAbiTable);
        legacy010.AbiVersion = 0x00000100u;
        nint legacy010Ptr = (nint)(&legacy010);
        Assert.Equal((int)NmStatus.ErrInvalidArg, NativeEntryPoint.InitializeManaged(legacy010Ptr));
        Assert.Throws<InvalidOperationException>(() => new NativeShimBridge(legacy010Ptr));

        NmAbiTable legacy020 = default;
        legacy020.StructSize = (uint)sizeof(NmAbiTable);
        legacy020.AbiVersion = 0x00000200u;
        nint legacy020Ptr = (nint)(&legacy020);
        Assert.Equal((int)NmStatus.ErrInvalidArg, NativeEntryPoint.InitializeManaged(legacy020Ptr));
        Assert.Throws<InvalidOperationException>(() => new NativeShimBridge(legacy020Ptr));

        NmAbiTable future = default;
        future.StructSize = (uint)sizeof(NmAbiTable);
        future.AbiVersion = 0x00000400u;
        nint futurePtr = (nint)(&future);
        Assert.Equal((int)NmStatus.ErrInvalidArg, NativeEntryPoint.InitializeManaged(futurePtr));
        Assert.Throws<InvalidOperationException>(() => new NativeShimBridge(futurePtr));

        NmAbiTable truncated = default;
        truncated.StructSize = 560u;
        truncated.AbiVersion = NativeShimConstants.AbiVersion;
        nint truncatedPtr = (nint)(&truncated);
        Assert.Equal((int)NmStatus.ErrInvalidArg, NativeEntryPoint.InitializeManaged(truncatedPtr));
        Assert.Throws<InvalidOperationException>(() => new NativeShimBridge(truncatedPtr));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockRegisterChatHandler(delegate* unmanaged[Cdecl]<byte*, byte*, void*, int> cb, void* ud, ulong* outToken)
    {
        *outToken = 1;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NmStatus MockUnregisterChatHandler(ulong token) { Interlocked.Increment(ref _unregisteredCount); return NmStatus.Ok; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockVerifyBytes(ulong address, byte* expectedHex)
    {
        string? hex = Marshal.PtrToStringUTF8((nint)expectedHex);
        return address == NativeShimConstants.NominalImageBase + 0x1528FBFul
               && string.Equals(hex, "488b5d284883eb10", StringComparison.OrdinalIgnoreCase)
            ? NmStatus.Ok
            : NmStatus.ErrByteMismatch;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockInstallInlineHook(byte* symbolName, void* detourFn, void** outTrampoline, ulong* outHandle)
    {
        *outTrampoline = (void*)(NativeShimConstants.NominalImageBase + 0x1528FBFul);
        *outHandle = 42;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NmStatus MockRemoveHook(ulong handle) => handle == 42 ? NmStatus.Ok : NmStatus.ErrHookNotFound;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockSendRulesetPacket(uint rulesetId, byte* payload, uint length)
    {
        _lastSentRulesetLen = (int)length;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockSendChatLine(byte* textUtf8)
    {
        _lastSentChat = Marshal.PtrToStringUTF8((nint)textUtf8);
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockAudioPlayOgg(byte* pathUtf8, float volume, int loop, int* outHandle)
    {
        _lastAudioPath = Marshal.PtrToStringUTF8((nint)pathUtf8);
        _lastAudioVolume = volume;
        _lastAudioLoop = loop;
        _mockAudioActive = true;
        *outHandle = 77;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NmStatus MockAudioSetVolume(int handle, float volume)
    {
        _lastAudioVolume = volume;
        return handle == 77 ? NmStatus.Ok : NmStatus.ErrInvalidArg;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockAudioIsPlaying(int handle, int* outPlaying)
    {
        *outPlaying = (handle == 77 && _mockAudioActive) ? 1 : 0;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NmStatus MockAudioStop(int handle)
    {
        if (handle == 77)
        {
            _mockAudioActive = false;
        }

        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockUiCreateWindow(byte* pathUtf8, void** outControl)
    {
        *outControl = (void*)0x1000;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockUiFindChild(void* parent, byte* dotPathUtf8, void** outChild)
    {
        string? id = Marshal.PtrToStringUTF8((nint)dotPathUtf8);
        *outChild = id switch
        {
            "status" => (void*)0x2001,
            "btn" => (void*)0x2002,
            _ => null,
        };
        return *outChild is not null ? NmStatus.Ok : NmStatus.ErrSymbolNotFound;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockUiSetVisible(void* control, int visible)
    {
        _lastNativeUiVisible = visible;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockUiSetText(void* control, byte* textUtf8, int isButton)
    {
        Interlocked.Increment(ref _nativeUiSetTextCalls);
        _lastNativeUiText = Marshal.PtrToStringUTF8((nint)textUtf8);
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockUiSubscribeClick(
        void* button,
        delegate* unmanaged[Cdecl]<void*, void*, void> cb,
        void* ud,
        ulong* outToken)
    {
        _mockClickCb = cb;
        _mockClickUd = ud;
        *outToken = 1;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockUiGetEnabled(void* control, int* outEnabled)
    {
        *outEnabled = 1;
        return NmStatus.Ok;
    }
}
