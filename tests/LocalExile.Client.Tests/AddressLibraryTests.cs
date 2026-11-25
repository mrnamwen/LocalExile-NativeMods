using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using LocalExile.Client.Host;
using LocalExile.Client.Host.Addresses;
using LocalExile.Client.Host.Native;
using LocalExile.Client.Sdk.Addresses;
using Xunit;

namespace LocalExile.Client.Tests;

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
        // Exact C ABI struct size parity with src/native-shim/include/nativemods_abi.h (x64).
        Assert.Equal(120, sizeof(NmHostInfo));
        Assert.Equal(48, sizeof(NmSymbolInfo));
        Assert.Equal(16, sizeof(NmGameAllocHeader));
        Assert.Equal(528, sizeof(NmExceptionInfo));
        Assert.Equal(200, sizeof(NmAbiTable));
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

        _lastSentRulesetLen = 0;
        _lastSentChat = null;
        _unregisteredCount = 0;

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
        }
        finally
        {
            NativeEntryPoint.Shutdown();
        }

        Assert.True(_unregisteredCount >= 4);
    }

    private static readonly byte[] MockSymName = AllocatePinnedUtf8("frame.state_stack_pass_a_hook");
    private static readonly byte[] MockSymCat = AllocatePinnedUtf8("frame");
    private static readonly byte[] MockSymBytes = AllocatePinnedUtf8("488b5d284883eb10");
    private static int _lastSentRulesetLen;
    private static string? _lastSentChat;
    private static int _unregisteredCount;

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

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockRegisterChatHandler(delegate* unmanaged[Cdecl]<byte*, byte*, void*, void> cb, void* ud, ulong* outToken)
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
}
