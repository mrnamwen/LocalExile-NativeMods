#ifndef NATIVEMODS_ABI_H
#define NATIVEMODS_ABI_H

/*
 * NativeMods 3.29 Script Extender — Public C ABI (LocalExile.ClientHost.dll)
 *
 * Shared contract between:
 *   - src/native-shim/              (C++20 Win64 native shim)
 *   - src/LocalExile.Client.Host/   (.NET 10 managed host NativeShimBridge)
 *
 * All addresses in the client are RVAs relative to the loaded module base
 * (DYNAMIC_BASE is enabled on 3.29.3c / 3.29.3d; nominal base 0x140000000).
 * All callbacks and game calls follow the Win64 x64 ABI (unified cdecl).
 */

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#  if defined(NATIVEMODS_SHIM_EXPORTS)
#    define NM_API __declspec(dllexport)
#  elif defined(NATIVEMODS_SHIM_STATIC)
#    define NM_API
#  else
#    define NM_API __declspec(dllimport)
#  endif
#  define NM_CALL __cdecl
#else
#  define NM_API __attribute__((visibility("default")))
#  define NM_CALL
#endif

/* Semantic ABI version encoded as (major << 16) | (minor << 8) | patch. */
#define NM_ABI_VERSION_MAJOR 0u
#define NM_ABI_VERSION_MINOR 4u
#define NM_ABI_VERSION_PATCH 0u
#define NM_ABI_VERSION ((NM_ABI_VERSION_MAJOR << 16) | (NM_ABI_VERSION_MINOR << 8) | NM_ABI_VERSION_PATCH)

/* Ground-truth protocol & runtime constants (verified in 08/10-verification.md & Phase 1 RE). */
#define NM_NOMINAL_IMAGE_BASE        0x140000000ull
#define NM_MSVC_CXX_EXCEPTION_CODE   0xE06D7363u
#define NM_OPCODE_PLAY_C2S           0x000Eu
#define NM_OPCODE_CREATE_CHAR_C2S    0x0012u
#define NM_OPCODE_CREATE_CHAR_S2C    0x0013u
#define NM_OPCODE_RULESET_S2C        0x02C9u
#define NM_OPCODE_RULESET_C2S        0x02CAu
#define NM_OPCODE_CHAT_S2C           0x0017u
#define NM_OPCODE_CHAT_C2S           0x0015u
#define NM_OPCODE_RESERVED_MIN       0x02FCu
#define NM_RULESET_MAX_PAYLOAD       0xFFFFu
#define NM_RULESET_SLOT_COUNT        0xAFu
#define NM_RULESET_VTABLE_SLOTS      9u
#define NM_RULESET_BASE_SIZE         0x18u
#define NM_CHAT_FALLBACK_PREFIX      "##NM "
#define NM_DEFAULT_ALLOC_TAG         0x0500u
#define NM_TAG_LEAGUE_CARD           0xDC35u
#define NM_TAG_INV_ITEM_VIEW         0xC004u

/* Build identifiers recognized by SHA-256 of the unpatched client executable. */
typedef enum nm_build_id_t {
    NM_BUILD_UNKNOWN         = 0,
    NM_BUILD_3_29_3C_327984  = 1, /* SHA-256 cc8aa383...5964c */
    NM_BUILD_3_29_3C_328126  = 2, /* SHA-256 01a22fac...a70cf (3.29.3.5 hotfix) */
    NM_BUILD_3_29_3D_328228  = 3, /* SHA-256 e9ba3d31...1f53d */
    NM_BUILD_SYNTHETIC       = 0x7FFF /* Host-side unit test / simulation mode */
} nm_build_id_t;

/* Return status codes for all C ABI calls. */
typedef enum nm_status_t {
    NM_OK                           =  0,
    NM_ERR_INVALID_ARG              = -1,
    NM_ERR_NOT_INITIALIZED          = -2,
    NM_ERR_UNSUPPORTED_BUILD        = -3,
    NM_ERR_SYMBOL_NOT_FOUND         = -4,
    NM_ERR_SYMBOL_UNVERIFIED        = -5,
    NM_ERR_BYTE_MISMATCH            = -6,
    NM_ERR_HOOK_ALREADY_INSTALLED   = -7,
    NM_ERR_HOOK_NOT_FOUND           = -8,
    NM_ERR_ALLOC_FAILED             = -9,
    NM_ERR_GAME_EXCEPTION           = -10,
    NM_ERR_BUFFER_TOO_SMALL         = -11,
    NM_ERR_QUEUE_FULL               = -12,
    NM_ERR_RUNTIME_BOOT_FAILED      = -13,
    NM_ERR_NOT_SUPPORTED_ON_PLATFORM = -14,
    NM_ERR_PAYLOAD_TOO_LARGE        = -15
} nm_status_t;

/* Host runtime feature / state flags. */
typedef enum nm_host_flags_t {
    NM_HOST_FLAG_NONE             = 0u,
    NM_HOST_FLAG_INITIALIZED      = 1u << 0,
    NM_HOST_FLAG_FRAME_HOOK_ARMED = 1u << 1,
    NM_HOST_FLAG_CORECLR_BOOTING  = 1u << 2,
    NM_HOST_FLAG_CORECLR_BOOTED   = 1u << 3,
    NM_HOST_FLAG_PROXY_MODE       = 1u << 4,
    NM_HOST_FLAG_SIMULATED        = 1u << 5
} nm_host_flags_t;

/* Address library symbol kinds. */
typedef enum nm_symbol_kind_t {
    NM_SYM_CODE        = 0,
    NM_SYM_DATA        = 1,
    NM_SYM_VTABLE      = 2,
    NM_SYM_IMPORT_SLOT = 3,
    NM_SYM_GLOBAL_REF  = 4
} nm_symbol_kind_t;

/* Verification status of an address library entry for the active build. */
typedef enum nm_symbol_status_t {
    NM_SYM_STATUS_UNVERIFIED      = 0, /* e.g. unverified_3_29_3c (rva == 0) */
    NM_SYM_STATUS_STATIC_VERIFIED = 1, /* static_verified */
    NM_SYM_STATUS_LIVE_VERIFIED   = 2, /* live_verified */
    NM_SYM_STATUS_DOC_VERIFIED    = 3, /* doc_verified */
    NM_SYM_STATUS_CODE_SHARED     = 4  /* code_shared_3_29_3c */
} nm_symbol_status_t;

/* Opaque handle for an installed inline detour or vtable hook. */
typedef uint64_t nm_hook_handle_t;
#define NM_HOOK_INVALID ((nm_hook_handle_t)0ull)

typedef enum nm_hook_kind_t {
    NM_HOOK_INLINE_DETOUR = 1,
    NM_HOOK_VTABLE_SLOT   = 2
} nm_hook_kind_t;

typedef enum nm_hook_phase_t {
    NM_HOOK_PHASE_PRE  = 0,
    NM_HOOK_PHASE_POST = 1
} nm_hook_phase_t;

/* Shared register/argument context for multi-mod pre/post hook chains (0.2.0). */
typedef struct nm_hook_context_t {
    uint64_t args[8];         /* arg0..3 from RCX..R9 (Win64) or RDI..RCX (SysV); arg4..7 from stack / SysV R8..R9+stack */
    uint64_t float_args[4];   /* raw 64-bit bits of XMM0..XMM3 */
    uint64_t return_value;    /* RAX (captured after original, editable by pre-skip or post-hook) */
    uint64_t return_float;    /* raw 64-bit bits of XMM0 return */
    uint64_t target_address;  /* hooked target address */
    uint8_t  skip_original;   /* pre-hook sets to non-zero to bypass original invocation */
    uint8_t  reserved[7];
} nm_hook_context_t;

/* Descriptor for up to 8-arg int/ptr/float/double guarded native calls (0.2.0). */
typedef struct nm_native_call_desc_t {
    uint64_t target_address;
    uint32_t arg_count;        /* 0..8 */
    uint32_t float_arg_mask;   /* bit i = 1 if arg i is float/double (bits in args[i]) */
    uint8_t  return_is_float;  /* non-zero to capture XMM0 into out_float_bits */
    uint8_t  reserved[7];
    uint64_t args[8];
    uint64_t out_int;          /* captured RAX */
    uint64_t out_float_bits;   /* captured XMM0 */
} nm_native_call_desc_t;

/* Snapshot of host shim state & detected client build. */
typedef struct nm_host_info_t {
    uint32_t      struct_size;
    uint32_t      abi_version;
    nm_build_id_t build_id;
    uint32_t      flags;
    uint64_t      image_base;
    uint32_t      image_size;
    uint32_t      symbol_count;
    char          build_version[16]; /* e.g. "3.29.3d", "3.29.3c", "synthetic" */
    char          exe_sha256[65];    /* Lowercase hex SHA-256 + NUL */
} nm_host_info_t;

/* Resolved symbol entry returned by nm_lookup_symbol / nm_get_symbol_at. */
typedef struct nm_symbol_info_t {
    const char*        name;
    const char*        category;
    nm_symbol_kind_t   kind;
    nm_symbol_status_t status;
    uint64_t           rva;                /* 0 when unverified */
    uint64_t           runtime_address;    /* image_base + rva, or 0 when unverified */
    const char*        expected_bytes_hex; /* NULL when no prologue signature is verified */
} nm_symbol_info_t;

/*
 * Game allocator 16-byte prefix header (verified in 08-deep-analysis.md §6 & spikes/agent.js).
 * Placed immediately before the returned payload pointer (payload - 16).
 * For alignment=16 (log2=4) and tag=0x0500, the final 32-bit word is 0x00050004.
 */
#if defined(_MSC_VER)
#  pragma pack(push, 1)
#endif
typedef struct
#if !defined(_MSC_VER)
__attribute__((packed))
#endif
nm_game_alloc_header_t {
    uint64_t size;
    uint32_t reserved0;
    uint8_t  log2_align;
    uint16_t tag;
    uint8_t  reserved1;
} nm_game_alloc_header_t;
#if defined(_MSC_VER)
#  pragma pack(pop)
#endif

/* Exception info captured by nm_call_guarded (SEH / MSVC 0xE06D7363 wall). */
typedef struct nm_exception_info_t {
    uint32_t exception_code;
    uint32_t reserved;
    uint64_t exception_address;
    char     message[512];
} nm_exception_info_t;

/* Callback signatures across the native <-> managed boundary. */
typedef void        (NM_CALL *nm_frame_callback_t)(float delta_seconds, void* user_data);
typedef void        (NM_CALL *nm_work_callback_t)(void* user_data);
typedef int64_t     (NM_CALL *nm_guarded_fn_t)(void* context);
typedef nm_status_t (NM_CALL *nm_file_overlay_callback_t)(
    const char* virtual_path_utf8,
    uint8_t**   out_data,
    size_t*     out_size,
    void*       user_data);
typedef void        (NM_CALL *nm_file_overlay_free_t)(uint8_t* data, void* user_data);
typedef void        (NM_CALL *nm_ruleset_rx_callback_t)(
    uint32_t       ruleset_id,
    const uint8_t* payload,
    uint32_t       length,
    void*          user_data);
typedef int32_t     (NM_CALL *nm_chat_rx_callback_t)(
    const char* sender_utf8,
    const char* text_utf8,
    void*       user_data);
typedef nm_status_t (NM_CALL *nm_packet_tx_override_t)(
    uint16_t       opcode,
    uint32_t       ruleset_id,
    const uint8_t* payload,
    uint32_t       length,
    void*          user_data);
typedef void        (NM_CALL *nm_ui_event_callback_t)(
    void* control,
    void* user_data);
typedef void        (NM_CALL *nm_hook_chain_callback_t)(
    nm_hook_context_t* ctx,
    void*              user_data);
typedef int32_t     (NM_CALL *nm_opcode_handler_t)(
    uint16_t       opcode,
    void*          stream_handle,
    const uint8_t* payload,
    uint32_t       length,
    void*          user_data);

/* --- ABI 0.3.0: Screens, Input, Hover/Cursor & Pre-Game Creation types --- */

typedef enum nm_pregame_screen_t {
    NM_SCREEN_UNKNOWN          = 0,
    NM_SCREEN_LOGIN            = 1,
    NM_SCREEN_SELECT_CHARACTER = 2,
    NM_SCREEN_CREATE_CHARACTER = 3,
    NM_SCREEN_LEAGUE_OVERLAY   = 4,
    NM_SCREEN_IN_GAME          = 5
} nm_pregame_screen_t;

typedef enum nm_panel_flags_t {
    NM_PANEL_NONE         = 0u,
    NM_PANEL_HUD          = 1u << 0,
    NM_PANEL_INVENTORY    = 1u << 1,
    NM_PANEL_STASH        = 1u << 2,
    NM_PANEL_GUILD_STASH  = 1u << 3,
    NM_PANEL_TRADE_STASH  = 1u << 4,
    NM_PANEL_MAP_STASH    = 1u << 5,
    NM_PANEL_BLIGHT_STASH = 1u << 6,
    NM_PANEL_PASSIVE_TREE = 1u << 7,
    NM_PANEL_ATLAS_TREE   = 1u << 8,
    NM_PANEL_VENDOR_BUY   = 1u << 9,
    NM_PANEL_VENDOR_SELL  = 1u << 10,
    NM_PANEL_NPC_DIALOGUE = 1u << 11,
    NM_PANEL_PLAYER_TRADE = 1u << 12,
    NM_PANEL_COSMETICS    = 1u << 13,
    NM_PANEL_MINIMAP      = 1u << 14,
    NM_PANEL_RIGHT_SIDE   = 1u << 15,
    NM_PANEL_LEFT_SIDE    = 1u << 16
} nm_panel_flags_t;

typedef enum nm_screen_event_kind_t {
    NM_SCREEN_EVT_INGAME_UI_INIT        = 1,
    NM_SCREEN_EVT_INGAME_LEAVE          = 2,
    NM_SCREEN_EVT_LEAGUE_OVERLAY_FILLED = 3,
    NM_SCREEN_EVT_CREATE_CHAR_ENTER     = 4,
    NM_SCREEN_EVT_CREATE_CHAR_LEAVE     = 5,
    NM_SCREEN_EVT_STASH_TAB_CHANGED     = 6,
    NM_SCREEN_EVT_MAP_SUBTAB_CHANGED    = 7
} nm_screen_event_kind_t;

/* Active pre-game / in-game screen & panel snapshot (280 bytes on x64). */
typedef struct nm_screens_state_t {
    uint32_t struct_size;
    int32_t  screen;                     /* nm_pregame_screen_t */
    uint32_t visible_panels;             /* bitmask of nm_panel_flags_t */
    uint32_t stash_mode;                 /* 0=personal, 1=guild, 2=trade, 0xFFFFFFFF=none */
    void*    active_ui_root;             /* UIManager::root (in-game or pre-game) */
    void*    hud_root;                   /* InGameUI* (InGameState+0x978) */
    void*    inventory_panel;            /* InventoryPanel* (InGameUI+0x628) */
    void*    inventory_content;          /* InventoryPanel+0x518 (auto-hides in Cosmetics) */
    void*    stash_panel;                /* active StashPanel* / GuildStashPanel* */
    void*    active_stash_page;          /* active StashPage* */
    void*    map_stash_page;             /* non-null if active stash page is MapStashPage */
    void*    blight_stash_page;          /* non-null if active stash page is BlightStashPage */
    void*    passive_panel;              /* PassiveShellPC* (InGameUI+0x650) */
    void*    passive_title_bar;          /* PassiveShellPC+0x3e0 */
    void*    vendor_panel;               /* visible VendorBuy/VendorSell/NPCDialogue */
    void*    league_overlay;             /* CreateCharacterState+0x2b8 */
    void*    league_cards_flow;          /* LeagueOverlay+0x3c0 */
    void*    create_char_frame;          /* CreateCharacterState+0x248 */
    int32_t  active_stash_tab_index;     /* server tab index, or -1 if none */
    uint32_t active_stash_tab_id;        /* server tab_id */
    uint32_t active_stash_tab_type;      /* StashType (0/1 Normal/Prem, 3 Currency, 5 Map, 13 Blight, ...) */
    int32_t  active_map_tier;            /* selected map tier/group index, or -1 */
    int32_t  active_map_subtab;          /* inner MapStashPage TabContainer selected index, or -1 */
    uint32_t selected_league_index;      /* CreateCharacterState+0x2c0 (0xFFFFFFFF if none) */
    char     active_stash_tab_name[128]; /* UTF-8 NUL-terminated */
} nm_screens_state_t;

typedef enum nm_input_kind_t {
    NM_INPUT_KEY_DOWN    = 1,
    NM_INPUT_KEY_UP      = 2,
    NM_INPUT_MOUSE_DOWN  = 3,
    NM_INPUT_MOUSE_UP    = 4,
    NM_INPUT_MOUSE_WHEEL = 5,
    NM_INPUT_MOUSE_MOVE  = 6
} nm_input_kind_t;

typedef enum nm_input_modifiers_t {
    NM_MOD_NONE  = 0u,
    NM_MOD_SHIFT = 1u << 0,
    NM_MOD_CTRL  = 1u << 1,
    NM_MOD_ALT   = 1u << 2
} nm_input_modifiers_t;

typedef enum nm_mouse_button_t {
    NM_MOUSE_NONE   = 0,
    NM_MOUSE_LEFT   = 1,
    NM_MOUSE_RIGHT  = 2,
    NM_MOUSE_MIDDLE = 3,
    NM_MOUSE_X1     = 4,
    NM_MOUSE_X2     = 5
} nm_mouse_button_t;

/* Low-level window input event intercepted in StateStack::OnWindowMessage (40 bytes). */
typedef struct nm_input_event_t {
    uint32_t struct_size;
    uint32_t win32_msg;             /* WM_KEYDOWN 0x100, WM_RBUTTONDOWN 0x204, etc. */
    int32_t  kind;                  /* nm_input_kind_t */
    uint32_t virtual_key;           /* Win32 VK_* code */
    int32_t  mouse_button;          /* nm_mouse_button_t */
    uint32_t modifiers;             /* nm_input_modifiers_t bitmask */
    int32_t  mouse_x;               /* client/UI X */
    int32_t  mouse_y;               /* client/UI Y */
    int32_t  wheel_delta;           /* signed wheel delta (multiples of 120) */
    uint8_t  is_repeat;             /* non-zero for auto-repeat WM_KEYDOWN */
    uint8_t  is_text_input_focused; /* non-zero if focused Control::AsTextInput() != nullptr */
    uint8_t  reserved[2];
} nm_input_event_t;

typedef enum nm_cursor_mode_t {
    NM_CURSOR_EMPTY    = 0,
    NM_CURSOR_HOLDING  = 1,
    NM_CURSOR_USE_MODE = 2
} nm_cursor_mode_t;

/* Snapshot of a hovered or cursor-held item entity (448 bytes on x64). */
typedef struct nm_item_snapshot_t {
    uint8_t  present;            /* non-zero if item fields are populated */
    uint8_t  grid_w;             /* BaseStatic+0x10 */
    uint8_t  grid_h;             /* BaseStatic+0x11 */
    uint8_t  identified;         /* ModsComponent+0xb0 */
    uint8_t  corrupted;          /* BaseComponent+0xc7 & 0x01 */
    uint8_t  account_bound;      /* BaseComponent+0xc7 & 0x08 */
    uint8_t  reserved[2];
    void*    item_entity;        /* ItemEntity* */
    void*    item_view;          /* InventoryItemView* (if available) */
    uint32_t inventory_id;       /* owning InventoryGrid+0x5c0 or cursor source inv */
    uint32_t item_id;            /* InventoryItemView+0x678 / slot itemId */
    int32_t  grid_x;             /* InventoryItemSlot+0x08 (or -1) */
    int32_t  grid_y;             /* InventoryItemSlot+0x0c (or -1) */
    uint32_t stack_size;         /* StackComponent+0x18 (default 1) */
    uint32_t max_stack_size;     /* StackComponent::GetMaxStackSize (default 1) */
    uint32_t rarity;             /* 0=Normal, 1=Magic, 2=Rare, 3=Unique */
    int32_t  item_level;         /* ModsComponent+0x2b8 */
    uint32_t item_class_id;      /* BaseItemTypesRef::GetItemClassId */
    uint32_t base_hash32;        /* BaseStatic+0x8c */
    char     metadata_path[256]; /* UTF-8 BaseItemTypesRow+0x00 Id */
    char     base_name[128];     /* UTF-8 BaseStatic+0x48 baseName */
} nm_item_snapshot_t;

/* Combined hover + cursor item snapshot (952 bytes on x64). */
typedef struct nm_hover_state_t {
    uint32_t           struct_size;
    int32_t            mouse_x;
    int32_t            mouse_y;
    int32_t            cursor_mode;        /* nm_cursor_mode_t */
    void*              hovered_control;    /* UIManager+0x230 */
    uint64_t           hovered_vtable;     /* *(uint64_t*)hovered_control */
    void*              focused_control;    /* UIManager+0x208 */
    uint32_t           use_source_inv_id;  /* Cursor+0x52c when mode==2 */
    int32_t            use_cell_x;         /* Cursor+0x530 when mode==2 */
    int32_t            use_cell_y;         /* Cursor+0x534 when mode==2 */
    uint32_t           reserved;
    nm_item_snapshot_t hovered_item;       /* 448 B */
    nm_item_snapshot_t cursor_item;        /* 448 B */
} nm_hover_state_t;

typedef void    (NM_CALL *nm_screen_event_callback_t)(
    int32_t  event_kind,
    void*    primary_handle,
    uint64_t param0,
    void*    user_data);

/* Returns non-zero to suppress the message BEFORE InGameState / UIManager / InventoryGrid. */
typedef int32_t (NM_CALL *nm_input_callback_t)(
    const nm_input_event_t* ev,
    void*                   user_data);

/*
 * Invoked in PRE hook of LoginClient::WriteCreateCharacter_0x12 (0x142146de0).
 * Return non-zero after writing a NUL-terminated UTF-8 string into out_start_state_utf8
 * to override the S78 start_state string; return 0 to leave stock start_state untouched.
 */
typedef int32_t (NM_CALL *nm_pregame_creating_callback_t)(
    const char* name_utf8,
    const char* league_utf8,
    const char* start_state_utf8,
    char*       out_start_state_utf8,
    size_t      out_capacity,
    void*       user_data);

typedef void    (NM_CALL *nm_pregame_created_callback_t)(
    const char* name_utf8,
    const char* league_utf8,
    uint16_t    result_code,
    void*       user_data);

/* --- ABI 0.4.0: World, Stats, Area Lifecycle, Item Filter & Camera types --- */

typedef enum nm_world_entity_kind_t {
    NM_ENTITY_KIND_NONE            = 0u,
    NM_ENTITY_KIND_PLAYER          = 1u << 0,
    NM_ENTITY_KIND_MONSTER         = 1u << 1,
    NM_ENTITY_KIND_WORLD_ITEM      = 1u << 2,
    NM_ENTITY_KIND_CHEST           = 1u << 3,
    NM_ENTITY_KIND_NPC             = 1u << 4,
    NM_ENTITY_KIND_PORTAL          = 1u << 5,
    NM_ENTITY_KIND_AREA_TRANSITION = 1u << 6,
    NM_ENTITY_KIND_MINIMAP_ICON    = 1u << 7,
    NM_ENTITY_KIND_TARGETABLE      = 1u << 8,
    NM_ENTITY_KIND_ALIVE           = 1u << 9,
    NM_ENTITY_KIND_OPENED          = 1u << 10,
    NM_ENTITY_KIND_LOCAL_PLAYER    = 1u << 11
} nm_world_entity_kind_t;

/* Query filter for nm_world_list_entities (160 bytes, zero hidden padding). */
typedef struct nm_world_entity_query_t {
    uint32_t struct_size;
    uint32_t required_kinds;       /* bitmask of nm_world_entity_kind_t (all bits required) */
    uint32_t excluded_kinds;       /* bitmask of nm_world_entity_kind_t (none may match) */
    uint32_t max_results;          /* 0 = unlimited (bounded by out_buf capacity) */
    float    origin_x;             /* world X for max_distance when use_origin != 0 */
    float    origin_y;             /* world Y for max_distance when use_origin != 0 */
    float    max_distance;         /* <= 0.0f disables distance check (world units) */
    uint8_t  use_origin;           /* 0 = distance from local player, non-zero = origin_x/origin_y */
    uint8_t  filter_visible_only;  /* non-zero = require filter_visible != 0 for WorldItem entities */
    uint8_t  include_world_item;   /* non-zero = populate embedded world_item snapshot for WorldItems */
    uint8_t  reserved;
    char     metadata_prefix[128]; /* optional UTF-8 prefix filter (empty = match all) */
} nm_world_entity_query_t;

/* Snapshot of a live ClientEntity in the active ClientWorld (760 bytes on x64). */
typedef struct nm_world_entity_snapshot_t {
    uint32_t           struct_size;
    uint32_t           entity_id;
    void*              entity_ptr;
    uint32_t           kind_flags;          /* bitmask of nm_world_entity_kind_t */
    int32_t            grid_x;
    int32_t            grid_y;
    float              world_x;
    float              world_y;
    float              world_z;
    int32_t            hp;
    int32_t            max_hp;
    uint32_t           rarity;              /* 0=Normal, 1=Magic, 2=Rare, 3=Unique */
    uint8_t            filter_visible;      /* WorldItem::IsFilterVisible (0 if not a WorldItem) */
    uint8_t            is_alive;
    uint8_t            is_targetable;
    uint8_t            is_opened;
    char               metadata_path[256];  /* UTF-8 ObjectTypeSlot+0x08 metadata path */
    nm_item_snapshot_t world_item;          /* 448 B, populated when WorldItem component present */
} nm_world_entity_snapshot_t;

/* Single stat key/value pair from StatsComponent+0xf0 (8 bytes). */
typedef struct nm_stat_entry_t {
    int32_t stat_id;
    int32_t value;
} nm_stat_entry_t;

typedef enum nm_area_event_kind_t {
    NM_AREA_EVT_ENTERED = 1,
    NM_AREA_EVT_LEFT    = 2
} nm_area_event_kind_t;

#define NM_AREA_MAX_RULESETS 16u

/* Active area / WorldAreasRow + ruleset snapshot (352 bytes). */
typedef struct nm_area_info_t {
    uint32_t struct_size;
    uint8_t  in_game;
    uint8_t  is_town;
    uint8_t  is_hideout;
    uint8_t  is_guild_hideout;
    int32_t  act;
    int32_t  area_level;                    /* WorldAreasRow+0x26 base level */
    int32_t  effective_level;               /* ClientWorld+0xd4 effective runtime level */
    uint16_t hash16;                        /* WorldAreasRow+0x2a */
    uint16_t reserved;
    uint32_t area_seed;                     /* ClientWorld+0x8e0 */
    uint32_t ruleset_count;                 /* clamped to NM_AREA_MAX_RULESETS */
    uint32_t ruleset_ids[NM_AREA_MAX_RULESETS];
    char     area_id[128];                  /* WorldAreasRow+0x00 Id (e.g. "1_1_town") */
    char     area_name[128];                /* WorldAreasRow+0x08 Name */
} nm_area_info_t;

/* Loaded client item-filter metadata & visibility toggle snapshot (784 bytes). */
typedef struct nm_item_filter_state_t {
    uint32_t struct_size;
    uint8_t  loaded;
    uint8_t  hide_filtered_ground_items;
    uint8_t  alt_highlight_active;
    uint8_t  reserved;
    uint32_t filter_kind;                   /* 0=None, 1=Normal, 2=Ruthless */
    uint32_t rule_count;
    char     filter_name[128];
    char     filter_folder[256];
    char     filter_path[384];
} nm_item_filter_state_t;

/* Evaluated ItemFilterStyle + ground label rect for a WorldItem or Item entity (72 bytes). */
typedef struct nm_item_filter_result_t {
    uint32_t struct_size;
    uint8_t  evaluated;
    uint8_t  is_visible;
    uint8_t  beam_temporary;
    uint8_t  has_label_rect;
    uint32_t style_flags;                   /* raw flags b0 | (b1 << 8) | (b2 << 16) */
    uint32_t font_size;
    uint32_t text_rgba;                     /* packed 0xRRGGBBAA */
    uint32_t border_rgba;                   /* packed 0xRRGGBBAA */
    uint32_t bg_rgba;                       /* packed 0xRRGGBBAA */
    int32_t  alert_sound_id;                /* -1 = none */
    uint32_t alert_sound_volume;
    int32_t  minimap_icon_shape;            /* -1 = none */
    int32_t  minimap_icon_color;            /* -1 = none */
    int32_t  minimap_icon_size;             /* -1 = none */
    int32_t  beam_color;                    /* DropEffects row index, -1 = none */
    float    label_x;
    float    label_y;
    float    label_w;
    float    label_h;
    uint32_t reserved;
} nm_item_filter_result_t;

/* Active FollowCamera / CameraController snapshot (120 bytes). */
typedef struct nm_camera_state_t {
    uint32_t struct_size;
    uint8_t  valid;
    uint8_t  reserved[3];
    float    pos_x;
    float    pos_y;
    float    pos_z;
    float    target_x;
    float    target_y;
    float    target_z;
    float    zoom;                          /* CameraController+0x4a8 zoom_current */
    float    zoom_override;                 /* CameraController+0x4ac (<0.0f = stock) */
    float    zoom_target;                   /* CameraController+0x4b0 */
    float    fov;                           /* vertical FOV in radians */
    uint32_t viewport_w;
    uint32_t viewport_h;
    float    view_proj[16];                 /* Camera+0x100 row-major DirectX v_row * M */
} nm_camera_state_t;

typedef void (NM_CALL *nm_area_event_callback_t)(
    int32_t               event_kind,
    const nm_area_info_t* area_info,
    void*                 user_data);

typedef void (NM_CALL *nm_filter_reload_callback_t)(
    int32_t                       succeeded,
    const nm_item_filter_state_t* filter_state,
    void*                         user_data);

/*
 * Versioned function-pointer table passed to the managed host entry point:
 *   int LocalExile.Client.Host.NativeEntryPoint.Initialize(IntPtr abiTablePtr)
 */
typedef struct nm_abi_table_t {
    uint32_t struct_size;
    uint32_t abi_version;

    /* Host info & address library (4) */
    nm_status_t (NM_CALL *get_host_info)(nm_host_info_t* out_info);
    nm_status_t (NM_CALL *lookup_symbol)(const char* name, nm_symbol_info_t* out_symbol);
    nm_status_t (NM_CALL *get_symbol_at)(uint32_t index, nm_symbol_info_t* out_symbol);
    nm_status_t (NM_CALL *verify_bytes)(uint64_t address, const char* expected_bytes_hex);

    /* Hook engine (byte-verified inline detour & vtable swap) (4) */
    nm_status_t (NM_CALL *install_inline_hook)(
        const char*       symbol_name,
        void*             detour_fn,
        void**            out_trampoline,
        nm_hook_handle_t* out_handle);
    nm_status_t (NM_CALL *install_inline_hook_at)(
        uint64_t          address,
        const char*       expected_bytes_hex,
        void*             detour_fn,
        void**            out_trampoline,
        nm_hook_handle_t* out_handle);
    nm_status_t (NM_CALL *install_vtable_hook)(
        void*             vtable_address,
        uint32_t          slot_index,
        void*             detour_fn,
        void**            out_original_fn,
        nm_hook_handle_t* out_handle);
    nm_status_t (NM_CALL *remove_hook)(nm_hook_handle_t handle);

    /* Main-thread frame queue (4) */
    nm_status_t (NM_CALL *register_frame_callback)(
        nm_frame_callback_t cb,
        void*               user_data,
        uint64_t*           out_token);
    nm_status_t (NM_CALL *unregister_frame_callback)(uint64_t token);
    nm_status_t (NM_CALL *enqueue_main_thread)(nm_work_callback_t cb, void* user_data);
    nm_status_t (NM_CALL *drain_main_thread_queue)(float delta_seconds, uint32_t max_items);

    /* SEH / C++ exception wall (1) */
    nm_status_t (NM_CALL *call_guarded)(
        nm_guarded_fn_t      fn,
        void*                context,
        int64_t*             out_result,
        nm_exception_info_t* out_exception);

    /* Win64 game allocator ABI wrapper (2) */
    nm_status_t (NM_CALL *game_alloc)(
        size_t   size,
        size_t   alignment,
        uint16_t tag,
        void**   out_ptr);
    nm_status_t (NM_CALL *game_free)(void* ptr, size_t alignment);

    /* File overlay hook registration (3) */
    nm_status_t (NM_CALL *register_file_overlay)(
        nm_file_overlay_callback_t cb,
        nm_file_overlay_free_t     free_cb,
        void*                      user_data,
        uint64_t*                  out_token);
    nm_status_t (NM_CALL *unregister_file_overlay)(uint64_t token);
    nm_status_t (NM_CALL *resolve_file_overlay)(
        const char* virtual_path_utf8,
        uint8_t**   out_data,
        size_t*     out_size);

    /* Host-ruleset (0x2c9/0x2ca) & chat fallback (0x17/0x15) channels (6) */
    nm_status_t (NM_CALL *register_ruleset_handler)(
        uint32_t                 ruleset_id,
        nm_ruleset_rx_callback_t cb,
        void*                    user_data,
        uint64_t*                out_token);
    nm_status_t (NM_CALL *unregister_ruleset_handler)(uint64_t token);
    nm_status_t (NM_CALL *send_ruleset_packet)(
        uint32_t       ruleset_id,
        const uint8_t* payload,
        uint32_t       length);
    nm_status_t (NM_CALL *register_chat_channel_handler)(
        nm_chat_rx_callback_t cb,
        void*                 user_data,
        uint64_t*             out_token);
    nm_status_t (NM_CALL *unregister_chat_channel_handler)(uint64_t token);
    nm_status_t (NM_CALL *send_chat_line)(const char* text_utf8);

    /* Native disk file overlay directories & buffer release (4) */
    nm_status_t (NM_CALL *register_file_overlay_dir)(
        const char* root_dir_utf8,
        uint64_t*   out_token);
    nm_status_t (NM_CALL *unregister_file_overlay_dir)(uint64_t token);
    nm_status_t (NM_CALL *has_file_overlay)(
        const char* virtual_path_utf8,
        size_t*     out_size);
    nm_status_t (NM_CALL *free_file_overlay_buffer)(uint8_t* data);

    /* Live FMOD 2.03 (.ogg) & engine audio playback (5) */
    nm_status_t (NM_CALL *audio_play_ogg)(
        const char* virtual_or_disk_path_utf8,
        float       volume,
        int32_t     loop,
        int32_t*    out_handle);
    nm_status_t (NM_CALL *audio_play_event)(
        const char* fmod_event_utf8,
        float       volume,
        int32_t*    out_handle);
    nm_status_t (NM_CALL *audio_stop)(int32_t channel_handle);
    nm_status_t (NM_CALL *audio_set_volume)(int32_t channel_handle, float volume);
    nm_status_t (NM_CALL *audio_is_playing)(int32_t channel_handle, int32_t* out_playing);

    /* Live native UI bridge (6) */
    nm_status_t (NM_CALL *ui_get_root)(void** out_root);
    nm_status_t (NM_CALL *ui_create_window)(const char* virtual_path_utf8, void** out_control);
    nm_status_t (NM_CALL *ui_find_child)(void* parent_control, const char* dot_path_utf8, void** out_child);
    nm_status_t (NM_CALL *ui_set_visible)(void* control, int32_t visible);
    nm_status_t (NM_CALL *ui_set_text)(void* control, const char* text_utf8, int32_t is_button);
    nm_status_t (NM_CALL *ui_subscribe_click)(
        void*                  button_control,
        nm_ui_event_callback_t cb,
        void*                  user_data,
        uint64_t*              out_token);

    /* Fast overlay path pre-filter & live connection generation token (2) */
    nm_status_t (NM_CALL *set_file_overlay_known_paths)(
        uint64_t           token,
        const char* const* paths_utf8,
        size_t             path_count);
    nm_status_t (NM_CALL *get_active_connection_token)(uint64_t* out_token);

    /* ABI 0.2.0: Multi-mod pre/post hook chains & 8-arg/float guarded native calls (4) */
    nm_status_t (NM_CALL *install_inline_chain_hook)(
        uint64_t                  address,
        const char*               expected_bytes_hex,
        int32_t                   phase,
        int32_t                   priority,
        nm_hook_chain_callback_t  cb,
        void*                     user_data,
        nm_hook_handle_t*         out_handle);
    nm_status_t (NM_CALL *install_inline_chain_hook_sym)(
        const char*               symbol_name,
        int32_t                   phase,
        int32_t                   priority,
        nm_hook_chain_callback_t  cb,
        void*                     user_data,
        nm_hook_handle_t*         out_handle);
    nm_status_t (NM_CALL *install_vtable_chain_hook)(
        void*                     vtable_address,
        uint32_t                  slot_index,
        int32_t                   phase,
        int32_t                   priority,
        nm_hook_chain_callback_t  cb,
        void*                     user_data,
        nm_hook_handle_t*         out_handle);
    nm_status_t (NM_CALL *call_native_guarded)(
        nm_native_call_desc_t* desc,
        nm_exception_info_t*   out_exception);

    /* ABI 0.2.0: Stock game file reads & hot-reload invalidation (2) */
    nm_status_t (NM_CALL *files_read_stock)(
        const char* virtual_path_utf8,
        uint8_t**   out_data,
        size_t*     out_size);
    nm_status_t (NM_CALL *files_invalidate)(const char* virtual_path_utf8);

    /* ABI 0.2.0: Raw packets & 0x02FC+ opcode handler group (4) */
    nm_status_t (NM_CALL *send_raw_packet)(
        uint16_t       opcode,
        const uint8_t* payload,
        uint32_t       length);
    nm_status_t (NM_CALL *register_opcode_handler_group)(
        uint16_t             min_opcode,
        uint16_t             max_opcode,
        nm_opcode_handler_t  cb,
        void*                user_data,
        uint64_t*            out_token);
    nm_status_t (NM_CALL *unregister_opcode_handler_group)(uint64_t token);
    nm_status_t (NM_CALL *stream_read)(
        void*    stream_handle,
        void*    dst,
        uint64_t length);

    /* ABI 0.2.0: Extended live native UI controls (15) */
    nm_status_t (NM_CALL *ui_create_window_into)(
        void*       parent_control,
        const char* virtual_path_utf8,
        void**      out_control);
    nm_status_t (NM_CALL *ui_get_rect)(
        void*  control,
        float* out_x,
        float* out_y,
        float* out_w,
        float* out_h);
    nm_status_t (NM_CALL *ui_set_position)(void* control, float x, float y);
    nm_status_t (NM_CALL *ui_set_size)(void* control, float w, float h);
    nm_status_t (NM_CALL *ui_set_enabled)(void* control, int32_t enabled);
    nm_status_t (NM_CALL *ui_remove_child)(void* parent_control, void* child_control);
    nm_status_t (NM_CALL *ui_destroy_control)(void* control);
    nm_status_t (NM_CALL *ui_textbox_get_text)(
        void*   textbox_control,
        char*   out_utf8,
        size_t  capacity,
        size_t* out_length);
    nm_status_t (NM_CALL *ui_textbox_set_text)(void* textbox_control, const char* text_utf8);
    nm_status_t (NM_CALL *ui_textbox_subscribe_changed)(
        void*                  textbox_control,
        nm_ui_event_callback_t cb,
        void*                  user_data,
        uint64_t*              out_token);
    nm_status_t (NM_CALL *ui_textbox_subscribe_submitted)(
        void*                  textbox_control,
        nm_ui_event_callback_t cb,
        void*                  user_data,
        uint64_t*              out_token);
    nm_status_t (NM_CALL *ui_checkbox_get_checked)(void* checkbox_control, int32_t* out_checked);
    nm_status_t (NM_CALL *ui_checkbox_set_checked)(void* checkbox_control, int32_t checked);
    nm_status_t (NM_CALL *ui_checkbox_subscribe_changed)(
        void*                  checkbox_control,
        nm_ui_event_callback_t cb,
        void*                  user_data,
        uint64_t*              out_token);
    nm_status_t (NM_CALL *ui_unsubscribe)(uint64_t token);

    /* ABI 0.2.0: Clipboard (2) */
    nm_status_t (NM_CALL *clipboard_get_text)(
        char*   out_utf8,
        size_t  capacity,
        size_t* out_length);
    nm_status_t (NM_CALL *clipboard_set_text)(const char* text_utf8);

    /* ABI 0.2.0: Control enabled readback (1) */
    nm_status_t (NM_CALL *ui_get_enabled)(void* control, int32_t* out_enabled);

    /* ABI 0.3.0: Native stock-tree UI traversal & layout helpers (14; slots 69..82) */
    nm_status_t (NM_CALL *ui_get_active_root)(void** out_root);
    nm_status_t (NM_CALL *ui_get_child_count)(void* parent_control, uint32_t* out_count);
    nm_status_t (NM_CALL *ui_get_child_at)(void* parent_control, uint32_t index, void** out_child);
    nm_status_t (NM_CALL *ui_get_vtable)(void* control, uint64_t* out_vtable);
    nm_status_t (NM_CALL *ui_get_control_tag)(void* control, uint16_t* out_tag);
    nm_status_t (NM_CALL *ui_get_control_id)(
        void*   control,
        char*   out_utf8,
        size_t  capacity,
        size_t* out_length);
    nm_status_t (NM_CALL *ui_get_control_parent)(void* control, void** out_parent);
    nm_status_t (NM_CALL *ui_is_visible)(void* control, int32_t* out_visible);
    nm_status_t (NM_CALL *ui_set_width)(void* control, float width);
    nm_status_t (NM_CALL *ui_set_height)(void* control, float height);
    nm_status_t (NM_CALL *ui_create_checkbox_child)(
        void*       parent_control,
        const char* caption_utf8,
        void**      out_checkbox);
    nm_status_t (NM_CALL *ui_create_stack_layout)(
        void*   parent_control,
        int32_t vertical,
        void**  out_layout);
    nm_status_t (NM_CALL *ui_layout_update)(void* control);
    nm_status_t (NM_CALL *ui_set_anchor)(
        void*    control,
        uint32_t x_anchor,
        float    x_offset,
        uint32_t y_anchor,
        float    y_offset);

    /* ABI 0.3.0: Screens & Tabs (2; slots 83..84) */
    nm_status_t (NM_CALL *screens_get_state)(nm_screens_state_t* out_state);
    nm_status_t (NM_CALL *screens_set_callback)(
        nm_screen_event_callback_t cb,
        void*                      user_data);

    /* ABI 0.3.0: Input interception & text focus (2; slots 85..86) */
    nm_status_t (NM_CALL *input_set_callback)(
        nm_input_callback_t cb,
        void*               user_data);
    nm_status_t (NM_CALL *input_is_text_focused)(int32_t* out_focused);

    /* ABI 0.3.0: Hover & Cursor item inspection (1; slot 87) */
    nm_status_t (NM_CALL *hover_get_state)(nm_hover_state_t* out_state);

    /* ABI 0.3.0: Pre-Game character creation hooks (1; slot 88) */
    nm_status_t (NM_CALL *pregame_set_callbacks)(
        nm_pregame_creating_callback_t on_creating,
        nm_pregame_created_callback_t  on_created,
        void*                          user_data);

    /* ABI 0.4.0: World entities & client objects (5; slots 89..93) */
    nm_status_t (NM_CALL *world_get_local_player)(nm_world_entity_snapshot_t* out_entity);
    nm_status_t (NM_CALL *world_list_entities)(
        const nm_world_entity_query_t* query,
        nm_world_entity_snapshot_t*    out_buf,
        uint32_t                       capacity,
        uint32_t*                      out_count,
        uint32_t*                      out_total);
    nm_status_t (NM_CALL *world_get_entity)(
        uint32_t                    entity_id,
        nm_world_entity_snapshot_t* out_entity);
    nm_status_t (NM_CALL *world_create_client_object)(
        const char* metadata_utf8,
        float       x,
        float       y,
        float       z,
        uint64_t*   out_handle);
    nm_status_t (NM_CALL *world_remove_client_object)(uint64_t handle);

    /* ABI 0.4.0: Entity stats (3; slots 94..96) */
    nm_status_t (NM_CALL *stats_read_entity)(
        uint32_t entity_id,
        int32_t  stat_id,
        int32_t* out_value);
    nm_status_t (NM_CALL *stats_set_local_entity)(
        uint32_t entity_id,
        int32_t  stat_id,
        int32_t  value);
    nm_status_t (NM_CALL *stats_list_entity)(
        uint32_t         entity_id,
        nm_stat_entry_t* out_buf,
        uint32_t         capacity,
        uint32_t*        out_count,
        uint32_t*        out_total);

    /* ABI 0.4.0: Area lifecycle (2; slots 97..98) */
    nm_status_t (NM_CALL *lifecycle_get_area_info)(nm_area_info_t* out_info);
    nm_status_t (NM_CALL *lifecycle_set_area_callback)(
        nm_area_event_callback_t cb,
        void*                    user_data);

    /* ABI 0.4.0: Client Item Filter & Ground Loot visibility (5; slots 99..103) */
    nm_status_t (NM_CALL *filter_get_state)(nm_item_filter_state_t* out_state);
    nm_status_t (NM_CALL *filter_reload)(void);
    nm_status_t (NM_CALL *filter_get_world_item_result)(
        uint32_t                 entity_id,
        nm_item_filter_result_t* out_result);
    nm_status_t (NM_CALL *filter_evaluate_entity)(
        uint32_t                 entity_id,
        nm_item_filter_result_t* out_result);
    nm_status_t (NM_CALL *filter_set_reload_callback)(
        nm_filter_reload_callback_t cb,
        void*                       user_data);

    /* ABI 0.4.0: Camera state, WorldToScreen & Zoom Override (3; slots 104..106) */
    nm_status_t (NM_CALL *camera_get_state)(nm_camera_state_t* out_state);
    nm_status_t (NM_CALL *camera_world_to_screen)(
        float    wx,
        float    wy,
        float    wz,
        float*   out_sx,
        float*   out_sy,
        int32_t* out_in_front);
    nm_status_t (NM_CALL *camera_set_zoom_override)(float zoom_override);
} nm_abi_table_t;

/* Exported C ABI functions. */
NM_API nm_status_t NM_CALL nm_get_abi_table(const nm_abi_table_t** out_table);
NM_API nm_status_t NM_CALL nm_get_host_info(nm_host_info_t* out_info);
NM_API nm_status_t NM_CALL nm_lookup_symbol(const char* name, nm_symbol_info_t* out_symbol);
NM_API nm_status_t NM_CALL nm_get_symbol_at(uint32_t index, nm_symbol_info_t* out_symbol);
NM_API nm_status_t NM_CALL nm_verify_bytes(uint64_t address, const char* expected_bytes_hex);

NM_API nm_status_t NM_CALL nm_install_inline_hook(
    const char*       symbol_name,
    void*             detour_fn,
    void**            out_trampoline,
    nm_hook_handle_t* out_handle);
NM_API nm_status_t NM_CALL nm_install_inline_hook_at(
    uint64_t          address,
    const char*       expected_bytes_hex,
    void*             detour_fn,
    void**            out_trampoline,
    nm_hook_handle_t* out_handle);
NM_API nm_status_t NM_CALL nm_install_vtable_hook(
    void*             vtable_address,
    uint32_t          slot_index,
    void*             detour_fn,
    void**            out_original_fn,
    nm_hook_handle_t* out_handle);
NM_API nm_status_t NM_CALL nm_install_inline_chain_hook(
    uint64_t                  address,
    const char*               expected_bytes_hex,
    int32_t                   phase,
    int32_t                   priority,
    nm_hook_chain_callback_t  cb,
    void*                     user_data,
    nm_hook_handle_t*         out_handle);
NM_API nm_status_t NM_CALL nm_install_inline_chain_hook_sym(
    const char*               symbol_name,
    int32_t                   phase,
    int32_t                   priority,
    nm_hook_chain_callback_t  cb,
    void*                     user_data,
    nm_hook_handle_t*         out_handle);
NM_API nm_status_t NM_CALL nm_install_vtable_chain_hook(
    void*                     vtable_address,
    uint32_t                  slot_index,
    int32_t                   phase,
    int32_t                   priority,
    nm_hook_chain_callback_t  cb,
    void*                     user_data,
    nm_hook_handle_t*         out_handle);
NM_API nm_status_t NM_CALL nm_remove_hook(nm_hook_handle_t handle);

NM_API nm_status_t NM_CALL nm_register_frame_callback(
    nm_frame_callback_t cb,
    void*               user_data,
    uint64_t*           out_token);
NM_API nm_status_t NM_CALL nm_unregister_frame_callback(uint64_t token);
NM_API nm_status_t NM_CALL nm_enqueue_main_thread(nm_work_callback_t cb, void* user_data);
NM_API nm_status_t NM_CALL nm_drain_main_thread_queue(float delta_seconds, uint32_t max_items);

NM_API nm_status_t NM_CALL nm_call_guarded(
    nm_guarded_fn_t      fn,
    void*                context,
    int64_t*             out_result,
    nm_exception_info_t* out_exception);
NM_API nm_status_t NM_CALL nm_call_native_guarded(
    nm_native_call_desc_t* desc,
    nm_exception_info_t*   out_exception);

NM_API nm_status_t NM_CALL nm_game_alloc(
    size_t   size,
    size_t   alignment,
    uint16_t tag,
    void**   out_ptr);
NM_API nm_status_t NM_CALL nm_game_free(void* ptr, size_t alignment);

NM_API nm_status_t NM_CALL nm_register_file_overlay(
    nm_file_overlay_callback_t cb,
    nm_file_overlay_free_t     free_cb,
    void*                      user_data,
    uint64_t*                  out_token);
NM_API nm_status_t NM_CALL nm_unregister_file_overlay(uint64_t token);
NM_API nm_status_t NM_CALL nm_resolve_file_overlay(
    const char* virtual_path_utf8,
    uint8_t**   out_data,
    size_t*     out_size);
NM_API nm_status_t NM_CALL nm_register_file_overlay_dir(
    const char* root_dir_utf8,
    uint64_t*   out_token);
NM_API nm_status_t NM_CALL nm_unregister_file_overlay_dir(uint64_t token);
NM_API nm_status_t NM_CALL nm_has_file_overlay(
    const char* virtual_path_utf8,
    size_t*     out_size);
NM_API nm_status_t NM_CALL nm_free_file_overlay_buffer(uint8_t* data);
NM_API nm_status_t NM_CALL nm_set_file_overlay_known_paths(
    uint64_t           token,
    const char* const* paths_utf8,
    size_t             path_count);
NM_API nm_status_t NM_CALL nm_get_active_connection_token(uint64_t* out_token);
NM_API nm_status_t NM_CALL nm_files_read_stock(
    const char* virtual_path_utf8,
    uint8_t**   out_data,
    size_t*     out_size);
NM_API nm_status_t NM_CALL nm_files_invalidate(const char* virtual_path_utf8);

NM_API nm_status_t NM_CALL nm_audio_play_ogg(
    const char* virtual_or_disk_path_utf8,
    float       volume,
    int32_t     loop,
    int32_t*    out_handle);
NM_API nm_status_t NM_CALL nm_audio_play_event(
    const char* fmod_event_utf8,
    float       volume,
    int32_t*    out_handle);
NM_API nm_status_t NM_CALL nm_audio_stop(int32_t channel_handle);
NM_API nm_status_t NM_CALL nm_audio_set_volume(int32_t channel_handle, float volume);
NM_API nm_status_t NM_CALL nm_audio_is_playing(int32_t channel_handle, int32_t* out_playing);

NM_API nm_status_t NM_CALL nm_ui_get_root(void** out_root);
NM_API nm_status_t NM_CALL nm_ui_create_window(const char* virtual_path_utf8, void** out_control);
NM_API nm_status_t NM_CALL nm_ui_find_child(void* parent_control, const char* dot_path_utf8, void** out_child);
NM_API nm_status_t NM_CALL nm_ui_set_visible(void* control, int32_t visible);
NM_API nm_status_t NM_CALL nm_ui_set_text(void* control, const char* text_utf8, int32_t is_button);
NM_API nm_status_t NM_CALL nm_ui_subscribe_click(
    void*                  button_control,
    nm_ui_event_callback_t cb,
    void*                  user_data,
    uint64_t*              out_token);
NM_API nm_status_t NM_CALL nm_ui_create_window_into(
    void*       parent_control,
    const char* virtual_path_utf8,
    void**      out_control);
NM_API nm_status_t NM_CALL nm_ui_get_rect(
    void*  control,
    float* out_x,
    float* out_y,
    float* out_w,
    float* out_h);
NM_API nm_status_t NM_CALL nm_ui_set_position(void* control, float x, float y);
NM_API nm_status_t NM_CALL nm_ui_set_size(void* control, float w, float h);
NM_API nm_status_t NM_CALL nm_ui_set_enabled(void* control, int32_t enabled);
NM_API nm_status_t NM_CALL nm_ui_get_enabled(void* control, int32_t* out_enabled);
NM_API nm_status_t NM_CALL nm_ui_remove_child(void* parent_control, void* child_control);
NM_API nm_status_t NM_CALL nm_ui_destroy_control(void* control);
NM_API nm_status_t NM_CALL nm_ui_textbox_get_text(
    void*   textbox_control,
    char*   out_utf8,
    size_t  capacity,
    size_t* out_length);
NM_API nm_status_t NM_CALL nm_ui_textbox_set_text(void* textbox_control, const char* text_utf8);
NM_API nm_status_t NM_CALL nm_ui_textbox_subscribe_changed(
    void*                  textbox_control,
    nm_ui_event_callback_t cb,
    void*                  user_data,
    uint64_t*              out_token);
NM_API nm_status_t NM_CALL nm_ui_textbox_subscribe_submitted(
    void*                  textbox_control,
    nm_ui_event_callback_t cb,
    void*                  user_data,
    uint64_t*              out_token);
NM_API nm_status_t NM_CALL nm_ui_checkbox_get_checked(void* checkbox_control, int32_t* out_checked);
NM_API nm_status_t NM_CALL nm_ui_checkbox_set_checked(void* checkbox_control, int32_t checked);
NM_API nm_status_t NM_CALL nm_ui_checkbox_subscribe_changed(
    void*                  checkbox_control,
    nm_ui_event_callback_t cb,
    void*                  user_data,
    uint64_t*              out_token);
NM_API nm_status_t NM_CALL nm_ui_unsubscribe(uint64_t token);
NM_API nm_status_t NM_CALL nm_clipboard_get_text(
    char*   out_utf8,
    size_t  capacity,
    size_t* out_length);
NM_API nm_status_t NM_CALL nm_clipboard_set_text(const char* text_utf8);

NM_API nm_status_t NM_CALL nm_register_ruleset_handler(
    uint32_t                 ruleset_id,
    nm_ruleset_rx_callback_t cb,
    void*                    user_data,
    uint64_t*                out_token);
NM_API nm_status_t NM_CALL nm_unregister_ruleset_handler(uint64_t token);
NM_API nm_status_t NM_CALL nm_send_ruleset_packet(
    uint32_t       ruleset_id,
    const uint8_t* payload,
    uint32_t       length);
NM_API nm_status_t NM_CALL nm_register_chat_channel_handler(
    nm_chat_rx_callback_t cb,
    void*                 user_data,
    uint64_t*             out_token);
NM_API nm_status_t NM_CALL nm_unregister_chat_channel_handler(uint64_t token);
NM_API nm_status_t NM_CALL nm_send_chat_line(const char* text_utf8);
NM_API nm_status_t NM_CALL nm_send_raw_packet(
    uint16_t       opcode,
    const uint8_t* payload,
    uint32_t       length);
NM_API nm_status_t NM_CALL nm_register_opcode_handler_group(
    uint16_t             min_opcode,
    uint16_t             max_opcode,
    nm_opcode_handler_t  cb,
    void*                user_data,
    uint64_t*            out_token);
NM_API nm_status_t NM_CALL nm_unregister_opcode_handler_group(uint64_t token);
NM_API nm_status_t NM_CALL nm_stream_read(
    void*    stream_handle,
    void*    dst,
    uint64_t length);

/* Inbound dispatch & lifecycle helpers (used by internal hooks and host-side tests). */
NM_API nm_status_t NM_CALL nm_dispatch_ruleset_rx(
    uint32_t       ruleset_id,
    const uint8_t* payload,
    uint32_t       length);
NM_API int32_t     NM_CALL nm_dispatch_chat_rx(
    const char* sender_utf8,
    const char* text_utf8);
NM_API nm_status_t NM_CALL nm_dispatch_opcode_rx(
    uint16_t       opcode,
    const uint8_t* payload,
    uint32_t       length,
    int32_t*       out_handled);
NM_API nm_status_t NM_CALL nm_set_packet_tx_override(
    nm_packet_tx_override_t tx_fn,
    void*                   user_data);

/* ABI 0.3.0 exported functions: Stock-tree UI traversal, Screens, Input, Hover & Pre-Game. */
NM_API nm_status_t NM_CALL nm_ui_get_active_root(void** out_root);
NM_API nm_status_t NM_CALL nm_ui_get_child_count(void* parent_control, uint32_t* out_count);
NM_API nm_status_t NM_CALL nm_ui_get_child_at(void* parent_control, uint32_t index, void** out_child);
NM_API nm_status_t NM_CALL nm_ui_get_vtable(void* control, uint64_t* out_vtable);
NM_API nm_status_t NM_CALL nm_ui_get_control_tag(void* control, uint16_t* out_tag);
NM_API nm_status_t NM_CALL nm_ui_get_control_id(
    void*   control,
    char*   out_utf8,
    size_t  capacity,
    size_t* out_length);
NM_API nm_status_t NM_CALL nm_ui_get_control_parent(void* control, void** out_parent);
NM_API nm_status_t NM_CALL nm_ui_is_visible(void* control, int32_t* out_visible);
NM_API nm_status_t NM_CALL nm_ui_set_width(void* control, float width);
NM_API nm_status_t NM_CALL nm_ui_set_height(void* control, float height);
NM_API nm_status_t NM_CALL nm_ui_create_checkbox_child(
    void*       parent_control,
    const char* caption_utf8,
    void**      out_checkbox);
NM_API nm_status_t NM_CALL nm_ui_create_stack_layout(
    void*   parent_control,
    int32_t vertical,
    void**  out_layout);
NM_API nm_status_t NM_CALL nm_ui_layout_update(void* control);
NM_API nm_status_t NM_CALL nm_ui_set_anchor(
    void*    control,
    uint32_t x_anchor,
    float    x_offset,
    uint32_t y_anchor,
    float    y_offset);
NM_API nm_status_t NM_CALL nm_screens_get_state(nm_screens_state_t* out_state);
NM_API nm_status_t NM_CALL nm_screens_set_callback(
    nm_screen_event_callback_t cb,
    void*                      user_data);
NM_API nm_status_t NM_CALL nm_input_set_callback(
    nm_input_callback_t cb,
    void*               user_data);
NM_API nm_status_t NM_CALL nm_input_is_text_focused(int32_t* out_focused);
NM_API nm_status_t NM_CALL nm_hover_get_state(nm_hover_state_t* out_state);
NM_API nm_status_t NM_CALL nm_pregame_set_callbacks(
    nm_pregame_creating_callback_t on_creating,
    nm_pregame_created_callback_t  on_created,
    void*                          user_data);

/* ABI 0.3.0 deterministic dispatch helpers (used by internal hooks and host-side tests). */
NM_API void    NM_CALL nm_dispatch_screen_event(int32_t event_kind, void* primary_handle, uint64_t param0);
NM_API int32_t NM_CALL nm_dispatch_input_event(const nm_input_event_t* ev);
NM_API int32_t NM_CALL nm_dispatch_pregame_creating(
    const char* name_utf8,
    const char* league_utf8,
    const char* start_state_utf8,
    char*       out_start_state_utf8,
    size_t      out_capacity);
NM_API void    NM_CALL nm_dispatch_pregame_created(
    const char* name_utf8,
    const char* league_utf8,
    uint16_t    result_code);

/* ABI 0.4.0 exported functions: World, Stats, Area Lifecycle, Item Filter & Camera. */
NM_API nm_status_t NM_CALL nm_world_get_local_player(nm_world_entity_snapshot_t* out_entity);
NM_API nm_status_t NM_CALL nm_world_list_entities(
    const nm_world_entity_query_t* query,
    nm_world_entity_snapshot_t*    out_buf,
    uint32_t                       capacity,
    uint32_t*                      out_count,
    uint32_t*                      out_total);
NM_API nm_status_t NM_CALL nm_world_get_entity(
    uint32_t                    entity_id,
    nm_world_entity_snapshot_t* out_entity);
NM_API nm_status_t NM_CALL nm_world_create_client_object(
    const char* metadata_utf8,
    float       x,
    float       y,
    float       z,
    uint64_t*   out_handle);
NM_API nm_status_t NM_CALL nm_world_remove_client_object(uint64_t handle);

NM_API nm_status_t NM_CALL nm_stats_read_entity(
    uint32_t entity_id,
    int32_t  stat_id,
    int32_t* out_value);
NM_API nm_status_t NM_CALL nm_stats_set_local_entity(
    uint32_t entity_id,
    int32_t  stat_id,
    int32_t  value);
NM_API nm_status_t NM_CALL nm_stats_list_entity(
    uint32_t         entity_id,
    nm_stat_entry_t* out_buf,
    uint32_t         capacity,
    uint32_t*        out_count,
    uint32_t*        out_total);

NM_API nm_status_t NM_CALL nm_lifecycle_get_area_info(nm_area_info_t* out_info);
NM_API nm_status_t NM_CALL nm_lifecycle_set_area_callback(
    nm_area_event_callback_t cb,
    void*                    user_data);

NM_API nm_status_t NM_CALL nm_filter_get_state(nm_item_filter_state_t* out_state);
NM_API nm_status_t NM_CALL nm_filter_reload(void);
NM_API nm_status_t NM_CALL nm_filter_get_world_item_result(
    uint32_t                 entity_id,
    nm_item_filter_result_t* out_result);
NM_API nm_status_t NM_CALL nm_filter_evaluate_entity(
    uint32_t                 entity_id,
    nm_item_filter_result_t* out_result);
NM_API nm_status_t NM_CALL nm_filter_set_reload_callback(
    nm_filter_reload_callback_t cb,
    void*                       user_data);

NM_API nm_status_t NM_CALL nm_camera_get_state(nm_camera_state_t* out_state);
NM_API nm_status_t NM_CALL nm_camera_world_to_screen(
    float    wx,
    float    wy,
    float    wz,
    float*   out_sx,
    float*   out_sy,
    int32_t* out_in_front);
NM_API nm_status_t NM_CALL nm_camera_set_zoom_override(float zoom_override);

/* ABI 0.4.0 deterministic dispatch helpers (used by internal hooks and host-side tests). */
NM_API void NM_CALL nm_dispatch_area_event(int32_t event_kind, const nm_area_info_t* area_info);
NM_API void NM_CALL nm_dispatch_filter_reloaded(int32_t succeeded, const nm_item_filter_state_t* filter_state);

/* Deferred CoreCLR (.NET 10) bootstrapper (off game thread to avoid DeadlockDetector). */
NM_API nm_status_t NM_CALL nm_bootstrap_managed_host(
    const char* hostfxr_path_utf8,
    const char* runtimeconfig_path_utf8,
    const char* host_assembly_path_utf8);

/* Host-side lifecycle (for standalone initialization / Linux unit tests). */
NM_API nm_status_t NM_CALL nm_initialize_for_build(
    nm_build_id_t build_id,
    uint64_t      image_base,
    uint32_t      image_size,
    const char*   exe_sha256_or_null);
NM_API nm_status_t NM_CALL nm_shutdown(void);

/*
 * Dummy anchor export used by the PE32+ import-table patcher so the Windows
 * loader resolves at least one symbol from LocalExile.ClientHost.dll on startup.
 */
NM_API void NM_CALL LocalExileClientHost_ImportAnchor(void);

#ifdef __cplusplus
}
#endif

#endif /* NATIVEMODS_ABI_H */
