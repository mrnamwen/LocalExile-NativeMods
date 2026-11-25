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
#define NM_ABI_VERSION_MINOR 1u
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
typedef void        (NM_CALL *nm_chat_rx_callback_t)(
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

/* Inbound dispatch & lifecycle helpers (used by internal hooks and host-side tests). */
NM_API nm_status_t NM_CALL nm_dispatch_ruleset_rx(
    uint32_t       ruleset_id,
    const uint8_t* payload,
    uint32_t       length);
NM_API int32_t     NM_CALL nm_dispatch_chat_rx(
    const char* sender_utf8,
    const char* text_utf8);
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
