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
        // Build a genuine unmanaged nm_abi_table_t matching nativemods_abi.h and verify NativeEntryPoint + NativeShimBridge!
        Assert.Equal(16, sizeof(NmGameAllocHeader));

        NmAbiTable table = default;
        table.StructSize = (uint)sizeof(NmAbiTable);
        table.AbiVersion = NativeShimConstants.AbiVersion;
        table.GetHostInfo = &MockGetHostInfo;
        table.LookupSymbol = &MockLookupSymbol;
        table.GetSymbolAt = &MockGetSymbolAt;
        table.VerifyBytes = &MockVerifyBytes;
        table.InstallInlineHook = &MockInstallInlineHook;
        table.RemoveHook = &MockRemoveHook;
        table.SendRulesetPacket = &MockSendRulesetPacket;
        table.SendChatLine = &MockSendChatLine;

        _lastSentRulesetLen = 0;
        _lastSentChat = null;

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
    }

    private static readonly byte[] MockSymName = "frame.state_stack_pass_a_hook\0"u8.ToArray();
    private static readonly byte[] MockSymCat = "frame\0"u8.ToArray();
    private static readonly byte[] MockSymBytes = "488b5d284883eb10\0"u8.ToArray();
    private static int _lastSentRulesetLen;
    private static string? _lastSentChat;

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
        fixed (byte* pName = MockSymName)
        fixed (byte* pCat = MockSymCat)
        fixed (byte* pBytes = MockSymBytes)
        {
            outSym->Name = pName;
            outSym->Category = pCat;
            outSym->Kind = NmSymbolKind.Code;
            outSym->Status = NmSymbolStatus.LiveVerified;
            outSym->Rva = 0x1528FBFul;
            outSym->RuntimeAddress = NativeShimConstants.NominalImageBase + 0x1528FBFul;
            outSym->ExpectedBytesHex = pBytes;
        }

        return NmStatus.Ok;
    }

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
