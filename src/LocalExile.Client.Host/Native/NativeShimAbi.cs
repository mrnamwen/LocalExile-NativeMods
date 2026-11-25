using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LocalExile.Client.Sdk.Addresses;

namespace LocalExile.Client.Host.Native;

/// <summary>
/// C ABI constants matching <c>src/native-shim/include/nativemods_abi.h</c> (ABI 0.1.0).
/// </summary>
public static class NativeShimConstants
{
    /// <summary>Packed ABI version <c>(0 &lt;&lt; 16) | (1 &lt;&lt; 8) | 0</c>.</summary>
    public const uint AbiVersion = 0x00000100u;

    /// <summary>Nominal 3.29 PE32+ image base (ASLR DYNAMIC_BASE relocates at runtime).</summary>
    public const ulong NominalImageBase = 0x140000000ul;

    /// <summary>MSVC C++ SEH exception code caught by <c>nm_call_guarded</c>.</summary>
    public const uint MsvcCxxExceptionCode = 0xE06D7363u;

    /// <summary>Default 16-byte game allocator tag (<c>0x0500</c>).</summary>
    public const ushort DefaultAllocTag = 0x0500;
}

/// <summary>Matches <c>nm_build_id_t</c> in <c>nativemods_abi.h</c>.</summary>
public enum NmBuildId : int
{
    Unknown = 0,
    Build3293c327984 = 1,
    Build3293c328126 = 2,
    Build3293d328228 = 3,
    Synthetic = 0x7FFF,
}

/// <summary>Matches <c>nm_status_t</c> in <c>nativemods_abi.h</c>.</summary>
public enum NmStatus : int
{
    Ok = 0,
    ErrInvalidArg = -1,
    ErrNotInitialized = -2,
    ErrUnsupportedBuild = -3,
    ErrSymbolNotFound = -4,
    ErrSymbolUnverified = -5,
    ErrByteMismatch = -6,
    ErrHookAlreadyInstalled = -7,
    ErrHookNotFound = -8,
    ErrAllocFailed = -9,
    ErrGameException = -10,
    ErrBufferTooSmall = -11,
    ErrQueueFull = -12,
    ErrRuntimeBootFailed = -13,
    ErrNotSupportedOnPlatform = -14,
    ErrPayloadTooLarge = -15,
}

/// <summary>Matches <c>nm_host_flags_t</c> in <c>nativemods_abi.h</c>.</summary>
[Flags]
public enum NmHostFlags : uint
{
    None = 0u,
    Initialized = 1u << 0,
    FrameHookArmed = 1u << 1,
    CoreClrBooting = 1u << 2,
    CoreClrBooted = 1u << 3,
    ProxyMode = 1u << 4,
    Simulated = 1u << 5,
}

/// <summary>Matches <c>nm_symbol_kind_t</c> in <c>nativemods_abi.h</c>.</summary>
public enum NmSymbolKind : int
{
    Code = 0,
    Data = 1,
    Vtable = 2,
    ImportSlot = 3,
    GlobalRef = 4,
}

/// <summary>Matches <c>nm_symbol_status_t</c> in <c>nativemods_abi.h</c>.</summary>
public enum NmSymbolStatus : int
{
    Unverified = 0,
    StaticVerified = 1,
    LiveVerified = 2,
    DocVerified = 3,
    CodeShared = 4,
}

/// <summary>Matches <c>nm_host_info_t</c> in <c>nativemods_abi.h</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NmHostInfo
{
    public uint StructSize;
    public uint AbiVersion;
    public NmBuildId BuildId;
    public NmHostFlags Flags;
    public ulong ImageBase;
    public uint ImageSize;
    public uint SymbolCount;
    public fixed byte BuildVersion[16];
    public fixed byte ExeSha256[65];

    public readonly string GetBuildVersion()
    {
        fixed (byte* p = BuildVersion)
        {
            return Marshal.PtrToStringUTF8((nint)p, 16).TrimEnd('\0');
        }
    }

    public readonly string GetExeSha256()
    {
        fixed (byte* p = ExeSha256)
        {
            return Marshal.PtrToStringUTF8((nint)p, 65).TrimEnd('\0');
        }
    }

    public readonly ClientBuildId ToClientBuildId() => BuildId switch
    {
        NmBuildId.Build3293c327984 or NmBuildId.Build3293c328126 => ClientBuildId.Build3293c,
        NmBuildId.Build3293d328228 => ClientBuildId.Build3293d,
        _ => ClientBuildId.Unknown,
    };
}

/// <summary>Matches <c>nm_symbol_info_t</c> in <c>nativemods_abi.h</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NmSymbolInfo
{
    public byte* Name;
    public byte* Category;
    public NmSymbolKind Kind;
    public NmSymbolStatus Status;
    public ulong Rva;
    public ulong RuntimeAddress;
    public byte* ExpectedBytesHex;

    public readonly AddressEntry ToAddressEntry()
    {
        string name = Name is not null ? Marshal.PtrToStringUTF8((nint)Name) ?? string.Empty : string.Empty;
        string category = Category is not null ? Marshal.PtrToStringUTF8((nint)Category) ?? string.Empty : string.Empty;
        string? expectedHex = ExpectedBytesHex is not null ? Marshal.PtrToStringUTF8((nint)ExpectedBytesHex) : null;
        string kind = Kind switch
        {
            NmSymbolKind.Code => "code",
            NmSymbolKind.Data => "data",
            NmSymbolKind.Vtable => "vtable",
            NmSymbolKind.ImportSlot => "import_slot",
            NmSymbolKind.GlobalRef => "global_ref",
            _ => "code",
        };
        string status = Status switch
        {
            NmSymbolStatus.LiveVerified => "live_verified",
            NmSymbolStatus.StaticVerified => "static_verified",
            NmSymbolStatus.DocVerified => "doc_verified",
            NmSymbolStatus.CodeShared => "code_shared_3_29_3c",
            _ => "unverified",
        };
        ulong? rva = Status == NmSymbolStatus.Unverified && Rva == 0 ? null : Rva;
        return new AddressEntry(name, rva, category, status, expectedHex, Kind: kind);
    }
}

/// <summary>Matches packed 16-byte <c>nm_game_alloc_header_t</c> in <c>nativemods_abi.h</c>.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct NmGameAllocHeader
{
    public ulong Size;
    public uint Reserved0;
    public byte Log2Align;
    public ushort Tag;
    public byte Reserved1;
}

/// <summary>Matches <c>nm_exception_info_t</c> in <c>nativemods_abi.h</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NmExceptionInfo
{
    public uint ExceptionCode;
    public uint Reserved;
    public ulong ExceptionAddress;
    public fixed byte Message[512];

    public readonly string GetMessage()
    {
        fixed (byte* p = Message)
        {
            return Marshal.PtrToStringUTF8((nint)p, 512).TrimEnd('\0');
        }
    }
}

/// <summary>
/// Binary layout matching <c>nm_abi_table_t</c> in <c>src/native-shim/include/nativemods_abi.h</c> (ABI 0.1.0, 24 function pointers).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NmAbiTable
{
    public uint StructSize;
    public uint AbiVersion;

    /* Host info & address library (4) */
    public delegate* unmanaged[Cdecl]<NmHostInfo*, NmStatus> GetHostInfo;
    public delegate* unmanaged[Cdecl]<byte*, NmSymbolInfo*, NmStatus> LookupSymbol;
    public delegate* unmanaged[Cdecl]<uint, NmSymbolInfo*, NmStatus> GetSymbolAt;
    public delegate* unmanaged[Cdecl]<ulong, byte*, NmStatus> VerifyBytes;

    /* Hook engine (4) */
    public delegate* unmanaged[Cdecl]<byte*, void*, void**, ulong*, NmStatus> InstallInlineHook;
    public delegate* unmanaged[Cdecl]<ulong, byte*, void*, void**, ulong*, NmStatus> InstallInlineHookAt;
    public delegate* unmanaged[Cdecl]<void*, uint, void*, void**, ulong*, NmStatus> InstallVtableHook;
    public delegate* unmanaged[Cdecl]<ulong, NmStatus> RemoveHook;

    /* Main-thread frame queue (4) */
    public delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<float, void*, void>, void*, ulong*, NmStatus> RegisterFrameCallback;
    public delegate* unmanaged[Cdecl]<ulong, NmStatus> UnregisterFrameCallback;
    public delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<void*, void>, void*, NmStatus> EnqueueMainThread;
    public delegate* unmanaged[Cdecl]<float, uint, NmStatus> DrainMainThreadQueue;

    /* SEH / C++ exception wall (1) */
    public delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<void*, long>, void*, long*, NmExceptionInfo*, NmStatus> CallGuarded;

    /* Win64 game allocator ABI wrapper (2) */
    public delegate* unmanaged[Cdecl]<nuint, nuint, ushort, void**, NmStatus> GameAlloc;
    public delegate* unmanaged[Cdecl]<void*, nuint, NmStatus> GameFree;

    /* File overlay hook registration (3) */
    public delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<byte*, byte**, nuint*, void*, NmStatus>, delegate* unmanaged[Cdecl]<byte*, void*, void>, void*, ulong*, NmStatus> RegisterFileOverlay;
    public delegate* unmanaged[Cdecl]<ulong, NmStatus> UnregisterFileOverlay;
    public delegate* unmanaged[Cdecl]<byte*, byte**, nuint*, NmStatus> ResolveFileOverlay;

    /* Host-ruleset & chat fallback channels (6) */
    public delegate* unmanaged[Cdecl]<uint, delegate* unmanaged[Cdecl]<uint, byte*, uint, void*, void>, void*, ulong*, NmStatus> RegisterRulesetHandler;
    public delegate* unmanaged[Cdecl]<ulong, NmStatus> UnregisterRulesetHandler;
    public delegate* unmanaged[Cdecl]<uint, byte*, uint, NmStatus> SendRulesetPacket;
    public delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<byte*, byte*, void*, void>, void*, ulong*, NmStatus> RegisterChatChannelHandler;
    public delegate* unmanaged[Cdecl]<ulong, NmStatus> UnregisterChatChannelHandler;
    public delegate* unmanaged[Cdecl]<byte*, NmStatus> SendChatLine;
}

/// <summary>
/// Managed entry point invoked by the native shim's CoreCLR bootstrapper (<c>nm_bootstrap_managed_host</c>).
/// Matches the contract documented in <c>nativemods_abi.h</c>:
/// <c>int LocalExile.Client.Host.NativeEntryPoint.Initialize(IntPtr abiTablePtr)</c>.
/// </summary>
public static class NativeEntryPoint
{
    private static readonly Lock Gate = new();

    /// <summary>Gets the active managed runtime booted via <see cref="Initialize"/>, if any.</summary>
    public static ClientHostRuntime? ActiveRuntime { get; private set; }

    /// <summary>
    /// Native bootstrap entry point invoked by <c>LocalExile.ClientHost.dll</c>.
    /// </summary>
    /// <param name="abiTablePtr">Pointer to a valid <c>nm_abi_table_t</c> structure.</param>
    /// <returns><c>0</c> (<see cref="NmStatus.Ok"/>) on success, or a negative <see cref="NmStatus"/> error code.</returns>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int Initialize(nint abiTablePtr)
    {
        return InitializeManaged(abiTablePtr);
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
}
