using System.Runtime.InteropServices;
using LocalExile.Client.Sdk.Addresses;

namespace LocalExile.Client.Host.Native;

/// <summary>
/// C ABI constants matching <c>src/native-shim/include/nativemods_abi.h</c> (ABI 0.5.0).
/// </summary>
public static class NativeShimConstants
{
    /// <summary>Packed ABI version <c>(0 &lt;&lt; 16) | (5 &lt;&lt; 8) | 0</c>.</summary>
    public const uint AbiVersion = 0x00000500u;

    /// <summary>Highest verified unoccupied native ruleset slot (<c>0xAE</c>; <c>0xA0</c> is occupied on 3.29.3d).</summary>
    public const byte RulesetFirstFreeNativeId = 0xAE;

    /// <summary>Maximum number of custom stash affinity targets carried in <see cref="NmAffinityRouteResult"/>.</summary>
    public const int AffinityMaxTargets = 4;

    /// <summary>Maximum number of active ruleset identifiers carried in <see cref="NmAreaInfo"/>.</summary>
    public const int AreaMaxRulesets = 16;

    /// <summary>First custom opcode above the 3.29.3d stock instance dispatcher bound (<c>0x02FC</c>).</summary>
    public const ushort OpcodeReservedMin = 0x02FC;

    /// <summary>Login TCP C→S Play opcode (<c>0x000E</c>).</summary>
    public const ushort OpcodePlayC2s = 0x000E;

    /// <summary>Login TCP C→S CreateCharacter opcode (<c>0x0012</c>).</summary>
    public const ushort OpcodeCreateCharC2s = 0x0012;

    /// <summary>Login TCP S→C CreateCharacterResult opcode (<c>0x0013</c>).</summary>
    public const ushort OpcodeCreateCharS2c = 0x0013;

    /// <summary>Nominal 3.29 PE32+ image base (ASLR DYNAMIC_BASE relocates at runtime).</summary>
    public const ulong NominalImageBase = 0x140000000ul;

    /// <summary>MSVC C++ SEH exception code caught by <c>nm_call_guarded</c>.</summary>
    public const uint MsvcCxxExceptionCode = 0xE06D7363u;

    /// <summary>Default 16-byte game allocator tag (<c>0x0500</c>).</summary>
    public const ushort DefaultAllocTag = 0x0500;

    /// <summary>Numeric control tag (<c>Control+0x1d8</c>) identifying main <c>LeagueCard</c> controls (<c>0xDC35</c>).</summary>
    public const ushort TagLeagueCard = 0xDC35;

    /// <summary>Numeric control tag (<c>Control+0x1d8</c>) identifying <c>InventoryItemView</c> controls (<c>0xC004</c>).</summary>
    public const ushort TagInvItemView = 0xC004;
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
    ErrAlreadyExists = -16,
    ErrNotFound = -17,
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

/// <summary>Matches <c>nm_pregame_screen_t</c> in <c>nativemods_abi.h</c>.</summary>
public enum NmPreGameScreen : int
{
    Unknown = 0,
    Login = 1,
    SelectCharacter = 2,
    CreateCharacter = 3,
    LeagueOverlay = 4,
    InGame = 5,
}

/// <summary>Matches <c>nm_panel_flags_t</c> in <c>nativemods_abi.h</c>.</summary>
[Flags]
public enum NmPanelFlags : uint
{
    None = 0u,
    Hud = 1u << 0,
    Inventory = 1u << 1,
    Stash = 1u << 2,
    GuildStash = 1u << 3,
    TradeStash = 1u << 4,
    MapStash = 1u << 5,
    BlightStash = 1u << 6,
    PassiveTree = 1u << 7,
    AtlasTree = 1u << 8,
    VendorBuy = 1u << 9,
    VendorSell = 1u << 10,
    NpcDialogue = 1u << 11,
    PlayerTrade = 1u << 12,
    Cosmetics = 1u << 13,
    Minimap = 1u << 14,
    RightSide = 1u << 15,
    LeftSide = 1u << 16,
}

/// <summary>Matches <c>nm_screen_event_kind_t</c> in <c>nativemods_abi.h</c>.</summary>
public enum NmScreenEventKind : int
{
    InGameUiInit = 1,
    InGameLeave = 2,
    LeagueOverlayFilled = 3,
    CreateCharEnter = 4,
    CreateCharLeave = 5,
    StashTabChanged = 6,
    MapSubTabChanged = 7,
}

/// <summary>Matches <c>nm_input_kind_t</c> in <c>nativemods_abi.h</c>.</summary>
public enum NmInputKind : int
{
    KeyDown = 1,
    KeyUp = 2,
    MouseDown = 3,
    MouseUp = 4,
    MouseWheel = 5,
    MouseMove = 6,
}

/// <summary>Matches <c>nm_input_modifiers_t</c> in <c>nativemods_abi.h</c>.</summary>
[Flags]
public enum NmInputModifiers : uint
{
    None = 0u,
    Shift = 1u << 0,
    Ctrl = 1u << 1,
    Alt = 1u << 2,
}

/// <summary>Matches <c>nm_mouse_button_t</c> in <c>nativemods_abi.h</c>.</summary>
public enum NmMouseButton : int
{
    None = 0,
    Left = 1,
    Right = 2,
    Middle = 3,
    X1 = 4,
    X2 = 5,
}

/// <summary>Matches <c>nm_cursor_mode_t</c> in <c>nativemods_abi.h</c>.</summary>
public enum NmCursorMode : int
{
    Empty = 0,
    Holding = 1,
    UseMode = 2,
}

/// <summary>Matches <c>nm_world_entity_kind_t</c> in <c>nativemods_abi.h</c> (ABI 0.4.0).</summary>
[Flags]
public enum NmWorldEntityKind : uint
{
    None = 0u,
    Player = 1u << 0,
    Monster = 1u << 1,
    WorldItem = 1u << 2,
    Chest = 1u << 3,
    Npc = 1u << 4,
    Portal = 1u << 5,
    AreaTransition = 1u << 6,
    MinimapIcon = 1u << 7,
    Targetable = 1u << 8,
    Alive = 1u << 9,
    Opened = 1u << 10,
    LocalPlayer = 1u << 11,
}

/// <summary>Matches <c>nm_area_event_kind_t</c> in <c>nativemods_abi.h</c> (ABI 0.4.0).</summary>
public enum NmAreaEventKind : int
{
    Entered = 1,
    Left = 2,
}

/// <summary>Matches <c>nm_ui_control_kind_t</c> in <c>nativemods_abi.h</c> (ABI 0.5.0).</summary>
public enum NmUiControlKind : int
{
    Frame = 0,
    Label = 1,
    Button = 2,
    TextBox = 3,
    CheckBox = 4,
    Picture = 5,
    StackLayout = 6,
    ScrollableFrame = 7,
    TabContainer = 8,
    ComboBox = 9,
    ListBox = 10,
}

/// <summary>Matches <c>nm_affinity_route_action_t</c> in <c>nativemods_abi.h</c> (ABI 0.5.0).</summary>
public enum NmAffinityRouteAction : int
{
    Stock = 0,
    Override = 1,
    Append = 2,
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

/// <summary>Matches <c>nm_hook_kind_t</c> in <c>nativemods_abi.h</c>.</summary>
public enum NmHookKind : int
{
    InlineDetour = 1,
    VtableSlot = 2,
}

/// <summary>Matches <c>nm_hook_phase_t</c> in <c>nativemods_abi.h</c>.</summary>
public enum NmHookPhase : int
{
    Pre = 0,
    Post = 1,
}

/// <summary>Matches <c>nm_hook_context_t</c> (128 bytes) in <c>nativemods_abi.h</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NmHookContext
{
    public fixed ulong Args[8];
    public fixed ulong FloatArgs[4];
    public ulong ReturnValue;
    public ulong ReturnFloat;
    public ulong TargetAddress;
    public byte SkipOriginal;
    public fixed byte Reserved[7];
}

/// <summary>Matches <c>nm_native_call_desc_t</c> (104 bytes) in <c>nativemods_abi.h</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NmNativeCallDesc
{
    public ulong TargetAddress;
    public uint ArgCount;
    public uint FloatArgMask;
    public byte ReturnIsFloat;
    public fixed byte Reserved[7];
    public fixed ulong Args[8];
    public ulong OutInt;
    public ulong OutFloatBits;
}

/// <summary>Matches <c>nm_screens_state_t</c> (280 bytes on x64) in <c>nativemods_abi.h</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NmScreensState
{
    public uint StructSize;
    public int Screen;
    public uint VisiblePanels;
    public uint StashMode;
    public void* ActiveUiRoot;
    public void* HudRoot;
    public void* InventoryPanel;
    public void* InventoryContent;
    public void* StashPanel;
    public void* ActiveStashPage;
    public void* MapStashPage;
    public void* BlightStashPage;
    public void* PassivePanel;
    public void* PassiveTitleBar;
    public void* VendorPanel;
    public void* LeagueOverlay;
    public void* LeagueCardsFlow;
    public void* CreateCharFrame;
    public int ActiveStashTabIndex;
    public uint ActiveStashTabId;
    public uint ActiveStashTabType;
    public int ActiveMapTier;
    public int ActiveMapSubtab;
    public uint SelectedLeagueIndex;
    public fixed byte ActiveStashTabName[128];

    public readonly string GetActiveStashTabName()
    {
        fixed (byte* p = ActiveStashTabName)
        {
            return Marshal.PtrToStringUTF8((nint)p, 128).TrimEnd('\0');
        }
    }
}

/// <summary>Matches <c>nm_input_event_t</c> (40 bytes on x64) in <c>nativemods_abi.h</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NmInputEvent
{
    public uint StructSize;
    public uint Win32Msg;
    public int Kind;
    public uint VirtualKey;
    public int MouseButton;
    public uint Modifiers;
    public int MouseX;
    public int MouseY;
    public int WheelDelta;
    public byte IsRepeat;
    public byte IsTextInputFocused;
    public fixed byte Reserved[2];
}

/// <summary>Matches <c>nm_item_snapshot_t</c> (448 bytes on x64) in <c>nativemods_abi.h</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NmItemSnapshot
{
    public byte Present;
    public byte GridW;
    public byte GridH;
    public byte Identified;
    public byte Corrupted;
    public byte AccountBound;
    public fixed byte Reserved[2];
    public void* ItemEntity;
    public void* ItemView;
    public uint InventoryId;
    public uint ItemId;
    public int GridX;
    public int GridY;
    public uint StackSize;
    public uint MaxStackSize;
    public uint Rarity;
    public int ItemLevel;
    public uint ItemClassId;
    public uint BaseHash32;
    public fixed byte MetadataPath[256];
    public fixed byte BaseName[128];

    public readonly string GetMetadataPath()
    {
        fixed (byte* p = MetadataPath)
        {
            return Marshal.PtrToStringUTF8((nint)p, 256).TrimEnd('\0');
        }
    }

    public readonly string GetBaseName()
    {
        fixed (byte* p = BaseName)
        {
            return Marshal.PtrToStringUTF8((nint)p, 128).TrimEnd('\0');
        }
    }
}

/// <summary>Matches <c>nm_hover_state_t</c> (952 bytes on x64) in <c>nativemods_abi.h</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NmHoverState
{
    public uint StructSize;
    public int MouseX;
    public int MouseY;
    public int CursorMode;
    public void* HoveredControl;
    public ulong HoveredVtable;
    public void* FocusedControl;
    public uint UseSourceInvId;
    public int UseCellX;
    public int UseCellY;
    public uint Reserved;
    public NmItemSnapshot HoveredItem;
    public NmItemSnapshot CursorItem;
}

/// <summary>Matches <c>nm_world_entity_query_t</c> (160 bytes on x64) in <c>nativemods_abi.h</c> (ABI 0.4.0).</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NmWorldEntityQuery
{
    public uint StructSize;
    public uint RequiredKinds;
    public uint ExcludedKinds;
    public uint MaxResults;
    public float OriginX;
    public float OriginY;
    public float MaxDistance;
    public byte UseOrigin;
    public byte FilterVisibleOnly;
    public byte IncludeWorldItem;
    public byte Reserved;
    public fixed byte MetadataPrefix[128];
}

/// <summary>Matches <c>nm_world_entity_snapshot_t</c> (760 bytes on x64) in <c>nativemods_abi.h</c> (ABI 0.4.0).</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NmWorldEntitySnapshot
{
    public uint StructSize;
    public uint EntityId;
    public void* EntityPtr;
    public uint KindFlags;
    public int GridX;
    public int GridY;
    public float WorldX;
    public float WorldY;
    public float WorldZ;
    public int Hp;
    public int MaxHp;
    public uint Rarity;
    public byte FilterVisible;
    public byte IsAlive;
    public byte IsTargetable;
    public byte IsOpened;
    public fixed byte MetadataPath[256];
    public NmItemSnapshot WorldItem;

    public readonly string GetMetadataPath()
    {
        fixed (byte* p = MetadataPath)
        {
            return Marshal.PtrToStringUTF8((nint)p, 256).TrimEnd('\0');
        }
    }
}

/// <summary>Matches <c>nm_stat_entry_t</c> (8 bytes on x64) in <c>nativemods_abi.h</c> (ABI 0.4.0).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NmStatEntry
{
    public int StatId;
    public int Value;
}

/// <summary>Matches <c>nm_area_info_t</c> (352 bytes on x64) in <c>nativemods_abi.h</c> (ABI 0.4.0).</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NmAreaInfo
{
    public uint StructSize;
    public byte InGame;
    public byte IsTown;
    public byte IsHideout;
    public byte IsGuildHideout;
    public int Act;
    public int AreaLevel;
    public int EffectiveLevel;
    public ushort Hash16;
    public ushort Reserved;
    public uint AreaSeed;
    public uint RulesetCount;
    public fixed uint RulesetIds[16];
    public fixed byte AreaId[128];
    public fixed byte AreaName[128];

    public readonly string GetAreaId()
    {
        fixed (byte* p = AreaId)
        {
            return Marshal.PtrToStringUTF8((nint)p, 128).TrimEnd('\0');
        }
    }

    public readonly string GetAreaName()
    {
        fixed (byte* p = AreaName)
        {
            return Marshal.PtrToStringUTF8((nint)p, 128).TrimEnd('\0');
        }
    }
}

/// <summary>Matches <c>nm_item_filter_state_t</c> (784 bytes on x64) in <c>nativemods_abi.h</c> (ABI 0.4.0).</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NmItemFilterState
{
    public uint StructSize;
    public byte Loaded;
    public byte HideFilteredGroundItems;
    public byte AltHighlightActive;
    public byte Reserved;
    public uint FilterKind;
    public uint RuleCount;
    public fixed byte FilterName[128];
    public fixed byte FilterFolder[256];
    public fixed byte FilterPath[384];

    public readonly string GetFilterName()
    {
        fixed (byte* p = FilterName)
        {
            return Marshal.PtrToStringUTF8((nint)p, 128).TrimEnd('\0');
        }
    }

    public readonly string GetFilterFolder()
    {
        fixed (byte* p = FilterFolder)
        {
            return Marshal.PtrToStringUTF8((nint)p, 256).TrimEnd('\0');
        }
    }

    public readonly string GetFilterPath()
    {
        fixed (byte* p = FilterPath)
        {
            return Marshal.PtrToStringUTF8((nint)p, 384).TrimEnd('\0');
        }
    }
}

/// <summary>Matches <c>nm_item_filter_result_t</c> (72 bytes on x64) in <c>nativemods_abi.h</c> (ABI 0.4.0).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NmItemFilterResult
{
    public uint StructSize;
    public byte Evaluated;
    public byte IsVisible;
    public byte BeamTemporary;
    public byte HasLabelRect;
    public uint StyleFlags;
    public uint FontSize;
    public uint TextRgba;
    public uint BorderRgba;
    public uint BgRgba;
    public int AlertSoundId;
    public uint AlertSoundVolume;
    public int MinimapIconShape;
    public int MinimapIconColor;
    public int MinimapIconSize;
    public int BeamColor;
    public float LabelX;
    public float LabelY;
    public float LabelW;
    public float LabelH;
    public uint Reserved;
}

/// <summary>Matches <c>nm_camera_state_t</c> (120 bytes on x64) in <c>nativemods_abi.h</c> (ABI 0.4.0).</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NmCameraState
{
    public uint StructSize;
    public byte Valid;
    public fixed byte Reserved[3];
    public float PosX;
    public float PosY;
    public float PosZ;
    public float TargetX;
    public float TargetY;
    public float TargetZ;
    public float Zoom;
    public float ZoomOverride;
    public float ZoomTarget;
    public float Fov;
    public uint ViewportW;
    public uint ViewportH;
    public fixed float ViewProj[16];
}

/// <summary>Matches <c>nm_affinity_target_t</c> (4 bytes on x64) in <c>nativemods_abi.h</c> (ABI 0.5.0).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NmAffinityTarget
{
    public ushort TabIndex;
    public byte AffinityId;
    public byte Reserved;
}

/// <summary>Matches <c>nm_affinity_route_result_t</c> (32 bytes on x64) in <c>nativemods_abi.h</c> (ABI 0.5.0).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NmAffinityRouteResult
{
    public uint StructSize;
    public int Action;
    public uint TargetCount;
    public uint StockAffinityBitmask;
    public NmAffinityTarget Target0;
    public NmAffinityTarget Target1;
    public NmAffinityTarget Target2;
    public NmAffinityTarget Target3;

    public void SetTarget(int index, NmAffinityTarget target)
    {
        switch (index)
        {
            case 0: Target0 = target; break;
            case 1: Target1 = target; break;
            case 2: Target2 = target; break;
            case 3: Target3 = target; break;
            default: throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public readonly NmAffinityTarget GetTarget(int index) => index switch
    {
        0 => Target0,
        1 => Target1,
        2 => Target2,
        3 => Target3,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };
}

/// <summary>Matches <c>nm_native_ruleset_callbacks_t</c> (64 bytes on x64) in <c>nativemods_abi.h</c> (ABI 0.5.0).</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NmNativeRulesetCallbacks
{
    public uint StructSize;
    public uint Reserved;
    public delegate* unmanaged[Cdecl]<uint, void*, void*, void> OnCreated;
    public delegate* unmanaged[Cdecl]<uint, uint, void*, void> OnAreaEntered;
    public delegate* unmanaged[Cdecl]<uint, uint, byte*, void*, void> OnScriptEvent;
    public delegate* unmanaged[Cdecl]<uint, float, void*, void> OnFrame;
    public delegate* unmanaged[Cdecl]<uint, float, void*, void> OnTick;
    public delegate* unmanaged[Cdecl]<uint, byte*, uint, void*, void> OnRulesetUpdate;
    public delegate* unmanaged[Cdecl]<uint, void*, void> OnAreaLeft;
}

/// <summary>Matches <c>nm_custom_ui_control_callbacks_t</c> (32 bytes on x64) in <c>nativemods_abi.h</c> (ABI 0.5.0).</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NmCustomUiControlCallbacks
{
    public uint StructSize;
    public uint Reserved;
    public delegate* unmanaged[Cdecl]<byte*, void*, void*, void> OnCreated;
    public delegate* unmanaged[Cdecl]<void*, byte*, byte*, void*, void> OnProperty;
    public delegate* unmanaged[Cdecl]<void*, void*, void> OnDestroyed;
}

/// <summary>
/// Binary layout matching <c>nm_abi_table_t</c> in <c>src/native-shim/include/nativemods_abi.h</c> (ABI 0.5.0, 139 function pointers, 1120 bytes).
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
    public delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<byte*, byte*, void*, int>, void*, ulong*, NmStatus> RegisterChatChannelHandler;
    public delegate* unmanaged[Cdecl]<ulong, NmStatus> UnregisterChatChannelHandler;
    public delegate* unmanaged[Cdecl]<byte*, NmStatus> SendChatLine;

    /* Native disk file overlay directories & buffer release (4) */
    public delegate* unmanaged[Cdecl]<byte*, ulong*, NmStatus> RegisterFileOverlayDir;
    public delegate* unmanaged[Cdecl]<ulong, NmStatus> UnregisterFileOverlayDir;
    public delegate* unmanaged[Cdecl]<byte*, nuint*, NmStatus> HasFileOverlay;
    public delegate* unmanaged[Cdecl]<byte*, NmStatus> FreeFileOverlayBuffer;

    /* Live FMOD 2.03 (.ogg) & engine audio playback (5) */
    public delegate* unmanaged[Cdecl]<byte*, float, int, int*, NmStatus> AudioPlayOgg;
    public delegate* unmanaged[Cdecl]<byte*, float, int*, NmStatus> AudioPlayEvent;
    public delegate* unmanaged[Cdecl]<int, NmStatus> AudioStop;
    public delegate* unmanaged[Cdecl]<int, float, NmStatus> AudioSetVolume;
    public delegate* unmanaged[Cdecl]<int, int*, NmStatus> AudioIsPlaying;

    /* Live native UI bridge (6) */
    public delegate* unmanaged[Cdecl]<void**, NmStatus> UiGetRoot;
    public delegate* unmanaged[Cdecl]<byte*, void**, NmStatus> UiCreateWindow;
    public delegate* unmanaged[Cdecl]<void*, byte*, void**, NmStatus> UiFindChild;
    public delegate* unmanaged[Cdecl]<void*, int, NmStatus> UiSetVisible;
    public delegate* unmanaged[Cdecl]<void*, byte*, int, NmStatus> UiSetText;
    public delegate* unmanaged[Cdecl]<void*, delegate* unmanaged[Cdecl]<void*, void*, void>, void*, ulong*, NmStatus> UiSubscribeClick;

    /* Fast overlay path pre-filter & live connection generation token (2) */
    public delegate* unmanaged[Cdecl]<ulong, byte**, nuint, NmStatus> SetFileOverlayKnownPaths;
    public delegate* unmanaged[Cdecl]<ulong*, NmStatus> GetActiveConnectionToken;

    /* ABI 0.2.0: Multi-mod pre/post hook chains & 8-arg/float guarded native calls (4) */
    public delegate* unmanaged[Cdecl]<ulong, byte*, int, int, delegate* unmanaged[Cdecl]<NmHookContext*, void*, void>, void*, ulong*, NmStatus> InstallInlineChainHook;
    public delegate* unmanaged[Cdecl]<byte*, int, int, delegate* unmanaged[Cdecl]<NmHookContext*, void*, void>, void*, ulong*, NmStatus> InstallInlineChainHookSym;
    public delegate* unmanaged[Cdecl]<void*, uint, int, int, delegate* unmanaged[Cdecl]<NmHookContext*, void*, void>, void*, ulong*, NmStatus> InstallVtableChainHook;
    public delegate* unmanaged[Cdecl]<NmNativeCallDesc*, NmExceptionInfo*, NmStatus> CallNativeGuarded;

    /* ABI 0.2.0: Stock game file reads & hot-reload invalidation (2) */
    public delegate* unmanaged[Cdecl]<byte*, byte**, nuint*, NmStatus> FilesReadStock;
    public delegate* unmanaged[Cdecl]<byte*, NmStatus> FilesInvalidate;

    /* ABI 0.2.0: Raw packets & 0x02FC+ opcode handler group (4) */
    public delegate* unmanaged[Cdecl]<ushort, byte*, uint, NmStatus> SendRawPacket;
    public delegate* unmanaged[Cdecl]<ushort, ushort, delegate* unmanaged[Cdecl]<ushort, void*, byte*, uint, void*, int>, void*, ulong*, NmStatus> RegisterOpcodeHandlerGroup;
    public delegate* unmanaged[Cdecl]<ulong, NmStatus> UnregisterOpcodeHandlerGroup;
    public delegate* unmanaged[Cdecl]<void*, void*, ulong, NmStatus> StreamRead;

    /* ABI 0.2.0: Extended live native UI controls (15) */
    public delegate* unmanaged[Cdecl]<void*, byte*, void**, NmStatus> UiCreateWindowInto;
    public delegate* unmanaged[Cdecl]<void*, float*, float*, float*, float*, NmStatus> UiGetRect;
    public delegate* unmanaged[Cdecl]<void*, float, float, NmStatus> UiSetPosition;
    public delegate* unmanaged[Cdecl]<void*, float, float, NmStatus> UiSetSize;
    public delegate* unmanaged[Cdecl]<void*, int, NmStatus> UiSetEnabled;
    public delegate* unmanaged[Cdecl]<void*, void*, NmStatus> UiRemoveChild;
    public delegate* unmanaged[Cdecl]<void*, NmStatus> UiDestroyControl;
    public delegate* unmanaged[Cdecl]<void*, byte*, nuint, nuint*, NmStatus> UiTextboxGetText;
    public delegate* unmanaged[Cdecl]<void*, byte*, NmStatus> UiTextboxSetText;
    public delegate* unmanaged[Cdecl]<void*, delegate* unmanaged[Cdecl]<void*, void*, void>, void*, ulong*, NmStatus> UiTextboxSubscribeChanged;
    public delegate* unmanaged[Cdecl]<void*, delegate* unmanaged[Cdecl]<void*, void*, void>, void*, ulong*, NmStatus> UiTextboxSubscribeSubmitted;
    public delegate* unmanaged[Cdecl]<void*, int*, NmStatus> UiCheckboxGetChecked;
    public delegate* unmanaged[Cdecl]<void*, int, NmStatus> UiCheckboxSetChecked;
    public delegate* unmanaged[Cdecl]<void*, delegate* unmanaged[Cdecl]<void*, void*, void>, void*, ulong*, NmStatus> UiCheckboxSubscribeChanged;
    public delegate* unmanaged[Cdecl]<ulong, NmStatus> UiUnsubscribe;

    /* ABI 0.2.0: Clipboard (2) */
    public delegate* unmanaged[Cdecl]<byte*, nuint, nuint*, NmStatus> ClipboardGetText;
    public delegate* unmanaged[Cdecl]<byte*, NmStatus> ClipboardSetText;

    /* ABI 0.2.0: Control enabled readback (1) */
    public delegate* unmanaged[Cdecl]<void*, int*, NmStatus> UiGetEnabled;

    /* ABI 0.3.0: Native stock-tree UI traversal & layout helpers (14; slots 69..82) */
    public delegate* unmanaged[Cdecl]<void**, NmStatus> UiGetActiveRoot;
    public delegate* unmanaged[Cdecl]<void*, uint*, NmStatus> UiGetChildCount;
    public delegate* unmanaged[Cdecl]<void*, uint, void**, NmStatus> UiGetChildAt;
    public delegate* unmanaged[Cdecl]<void*, ulong*, NmStatus> UiGetVtable;
    public delegate* unmanaged[Cdecl]<void*, ushort*, NmStatus> UiGetControlTag;
    public delegate* unmanaged[Cdecl]<void*, byte*, nuint, nuint*, NmStatus> UiGetControlId;
    public delegate* unmanaged[Cdecl]<void*, void**, NmStatus> UiGetControlParent;
    public delegate* unmanaged[Cdecl]<void*, int*, NmStatus> UiIsVisible;
    public delegate* unmanaged[Cdecl]<void*, float, NmStatus> UiSetWidth;
    public delegate* unmanaged[Cdecl]<void*, float, NmStatus> UiSetHeight;
    public delegate* unmanaged[Cdecl]<void*, byte*, void**, NmStatus> UiCreateCheckBoxChild;
    public delegate* unmanaged[Cdecl]<void*, int, void**, NmStatus> UiCreateStackLayout;
    public delegate* unmanaged[Cdecl]<void*, NmStatus> UiLayoutUpdate;
    public delegate* unmanaged[Cdecl]<void*, uint, float, uint, float, NmStatus> UiSetAnchor;

    /* ABI 0.3.0: Screens & Tabs (2; slots 83..84) */
    public delegate* unmanaged[Cdecl]<NmScreensState*, NmStatus> ScreensGetState;
    public delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<int, void*, ulong, void*, void>, void*, NmStatus> ScreensSetCallback;

    /* ABI 0.3.0: Input interception & text focus (2; slots 85..86) */
    public delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<NmInputEvent*, void*, int>, void*, NmStatus> InputSetCallback;
    public delegate* unmanaged[Cdecl]<int*, NmStatus> InputIsTextFocused;

    /* ABI 0.3.0: Hover & Cursor item inspection (1; slot 87) */
    public delegate* unmanaged[Cdecl]<NmHoverState*, NmStatus> HoverGetState;

    /* ABI 0.3.0: Pre-Game character creation hooks (1; slot 88) */
    public delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<byte*, byte*, byte*, byte*, nuint, void*, int>, delegate* unmanaged[Cdecl]<byte*, byte*, ushort, void*, void>, void*, NmStatus> PreGameSetCallbacks;

    /* ABI 0.4.0: Live World entities & client objects (5; slots 89..93) */
    public delegate* unmanaged[Cdecl]<NmWorldEntitySnapshot*, NmStatus> WorldGetLocalPlayer;
    public delegate* unmanaged[Cdecl]<NmWorldEntityQuery*, NmWorldEntitySnapshot*, uint, uint*, uint*, NmStatus> WorldListEntities;
    public delegate* unmanaged[Cdecl]<uint, NmWorldEntitySnapshot*, NmStatus> WorldGetEntity;
    public delegate* unmanaged[Cdecl]<byte*, float, float, float, ulong*, NmStatus> WorldCreateClientObject;
    public delegate* unmanaged[Cdecl]<ulong, NmStatus> WorldRemoveClientObject;

    /* ABI 0.4.0: Live Stats reads, local writes & high-ID enumeration (3; slots 94..96) */
    public delegate* unmanaged[Cdecl]<uint, int, int*, NmStatus> StatsReadEntity;
    public delegate* unmanaged[Cdecl]<uint, int, int, NmStatus> StatsSetLocalEntity;
    public delegate* unmanaged[Cdecl]<uint, NmStatEntry*, uint, uint*, uint*, NmStatus> StatsListEntity;

    /* ABI 0.4.0: Live Area lifecycle metadata & hooks (2; slots 97..98) */
    public delegate* unmanaged[Cdecl]<NmAreaInfo*, NmStatus> LifecycleGetAreaInfo;
    public delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<int, NmAreaInfo*, void*, void>, void*, NmStatus> LifecycleSetAreaCallback;

    /* ABI 0.4.0: Item Filter state, cached world-item results, evaluation & reload hook (5; slots 99..103) */
    public delegate* unmanaged[Cdecl]<NmItemFilterState*, NmStatus> FilterGetState;
    public delegate* unmanaged[Cdecl]<NmStatus> FilterReload;
    public delegate* unmanaged[Cdecl]<uint, NmItemFilterResult*, NmStatus> FilterGetWorldItemResult;
    public delegate* unmanaged[Cdecl]<uint, NmItemFilterResult*, NmStatus> FilterEvaluateEntity;
    public delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<int, NmItemFilterState*, void*, void>, void*, NmStatus> FilterSetReloadCallback;

    /* ABI 0.4.0: Camera state, world-to-screen projection & zoom override (3; slots 104..106) */
    public delegate* unmanaged[Cdecl]<NmCameraState*, NmStatus> CameraGetState;
    public delegate* unmanaged[Cdecl]<float, float, float, float*, float*, int*, NmStatus> CameraWorldToScreen;
    public delegate* unmanaged[Cdecl]<float, NmStatus> CameraSetZoomOverride;

    /* ABI 0.5.0: Programmatic & Rich UI Controls (22; slots 107..128) */
    public delegate* unmanaged[Cdecl]<int, void*, byte*, void**, NmStatus> UiCreateControl;
    public delegate* unmanaged[Cdecl]<void*, byte*, NmStatus> UiPictureSetTexture;
    public delegate* unmanaged[Cdecl]<void*, float*, float*, NmStatus> UiScrollableGetScroll;
    public delegate* unmanaged[Cdecl]<void*, float, NmStatus> UiScrollableSetScroll;
    public delegate* unmanaged[Cdecl]<void*, void**, NmStatus> UiScrollableGetContent;
    public delegate* unmanaged[Cdecl]<void*, uint*, NmStatus> UiTabContainerGetCount;
    public delegate* unmanaged[Cdecl]<void*, int*, NmStatus> UiTabContainerGetActive;
    public delegate* unmanaged[Cdecl]<void*, int, NmStatus> UiTabContainerSelect;
    public delegate* unmanaged[Cdecl]<void*, byte*, uint, int, void**, NmStatus> UiTabContainerAddTab;
    public delegate* unmanaged[Cdecl]<void*, delegate* unmanaged[Cdecl]<void*, int, void*, void>, void*, ulong*, NmStatus> UiTabContainerSubscribeChanged;
    public delegate* unmanaged[Cdecl]<void*, uint*, NmStatus> UiComboBoxGetCount;
    public delegate* unmanaged[Cdecl]<void*, int*, NmStatus> UiComboBoxGetSelected;
    public delegate* unmanaged[Cdecl]<void*, int, NmStatus> UiComboBoxSetSelected;
    public delegate* unmanaged[Cdecl]<void*, byte*, int, uint, uint*, NmStatus> UiComboBoxAddItem;
    public delegate* unmanaged[Cdecl]<void*, NmStatus> UiComboBoxClear;
    public delegate* unmanaged[Cdecl]<void*, delegate* unmanaged[Cdecl]<void*, int, void*, void>, void*, ulong*, NmStatus> UiComboBoxSubscribeChanged;
    public delegate* unmanaged[Cdecl]<void*, uint*, NmStatus> UiListBoxGetCount;
    public delegate* unmanaged[Cdecl]<void*, int*, NmStatus> UiListBoxGetSelected;
    public delegate* unmanaged[Cdecl]<void*, int, NmStatus> UiListBoxSetSelected;
    public delegate* unmanaged[Cdecl]<void*, uint, void*, ulong, NmStatus> UiListBoxInsertItem;
    public delegate* unmanaged[Cdecl]<void*, NmStatus> UiListBoxClear;
    public delegate* unmanaged[Cdecl]<void*, delegate* unmanaged[Cdecl]<void*, int, void*, void>, void*, ulong*, NmStatus> UiListBoxSubscribeChanged;

    /* ABI 0.5.0: Stock Item Tooltip Augmentation (2; slots 129..130) */
    public delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<void*, NmItemSnapshot*, int, void*, void>, void*, NmStatus> TooltipSetItemAugmentCallback;
    public delegate* unmanaged[Cdecl]<void*, byte*, uint, int, NmStatus> TooltipAddItemSection;

    /* ABI 0.5.0: Custom Stash Affinity Router (1; slot 131) */
    public delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<NmItemSnapshot*, int, NmAffinityRouteResult*, void*, int>, void*, NmStatus> AffinitySetRouterCallback;

    /* ABI 0.5.0: Native Extend Registries (7; slots 132..138) */
    public delegate* unmanaged[Cdecl]<byte*, uint, uint, int, delegate* unmanaged[Cdecl]<uint, byte*, byte*, nuint, nuint*, void*, NmStatus>, void*, ulong*, NmStatus> GealRegisterBuiltin;
    public delegate* unmanaged[Cdecl]<ulong, NmStatus> GealUnregisterBuiltin;
    public delegate* unmanaged[Cdecl]<uint, byte*, byte*, byte*, nuint, nuint*, NmStatus> GealInvokeFunction;
    public delegate* unmanaged[Cdecl]<uint, NmNativeRulesetCallbacks*, void*, ulong*, NmStatus> RulesetRegisterNative;
    public delegate* unmanaged[Cdecl]<ulong, NmStatus> RulesetUnregisterNative;
    public delegate* unmanaged[Cdecl]<byte*, NmCustomUiControlCallbacks*, void*, ulong*, NmStatus> UiRegisterCustomControlKind;
    public delegate* unmanaged[Cdecl]<ulong, NmStatus> UiUnregisterCustomControlKind;
}
