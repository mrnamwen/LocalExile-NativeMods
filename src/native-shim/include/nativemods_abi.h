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
#define NM_ABI_VERSION_MINOR 2u
#define NM_ABI_VERSION_PATCH 0u
#define NM_ABI_VERSION ((NM_ABI_VERSION_MAJOR << 16) | (NM_ABI_VERSION_MINOR << 8) | NM_ABI_VERSION_PATCH)

/* Ground-truth protocol & runtime constants (verified in 08/10-verification.md). */
#define NM_NOMINAL_IMAGE_BASE        0x140000000ull
#define NM_MSVC_CXX_EXCEPTION_CODE   0xE06D7363u
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
