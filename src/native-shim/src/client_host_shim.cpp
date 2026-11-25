#include "nativemods_abi.h"

#include "address_table.hpp"
#include "coreclr_bootstrap.hpp"
#include "exception_wall.hpp"
#include "frame_queue.hpp"
#include "game_allocator.hpp"
#include "hook_engine.hpp"
#include "overlay_and_channels.hpp"

#include <atomic>
#include <cstdio>
#include <cstring>
#include <mutex>

#if defined(_WIN32)
#  define WIN32_LEAN_AND_MEAN
#  include <windows.h>
#endif

namespace nativemods {

namespace {

struct ShimState {
    std::mutex          init_mutex;
    bool                initialized = false;
    nm_build_id_t       build_id = NM_BUILD_UNKNOWN;
    uint32_t            flags = NM_HOST_FLAG_NONE;
    uint64_t            image_base = 0;
    uint32_t            image_size = 0;
    char                exe_sha256[65] = {};

    HookEngine          hooks;
    GameAllocator       allocator;
    FrameQueue          frame_queue;
    OverlayAndChannels  channels;
    CoreClrBootstrapper clr;
};

ShimState& state() {
    static ShimState instance;
    return instance;
}

const nm_abi_table_t kAbiTable = {
    sizeof(nm_abi_table_t),
    NM_ABI_VERSION,
    &nm_get_host_info,
    &nm_lookup_symbol,
    &nm_get_symbol_at,
    &nm_verify_bytes,
    &nm_install_inline_hook,
    &nm_install_inline_hook_at,
    &nm_install_vtable_hook,
    &nm_remove_hook,
    &nm_register_frame_callback,
    &nm_unregister_frame_callback,
    &nm_enqueue_main_thread,
    &nm_drain_main_thread_queue,
    &nm_call_guarded,
    &nm_game_alloc,
    &nm_game_free,
    &nm_register_file_overlay,
    &nm_unregister_file_overlay,
    &nm_resolve_file_overlay,
    &nm_register_ruleset_handler,
    &nm_unregister_ruleset_handler,
    &nm_send_ruleset_packet,
    &nm_register_chat_channel_handler,
    &nm_unregister_chat_channel_handler,
    &nm_send_chat_line,
};

} // namespace

} // namespace nativemods

using namespace nativemods;

extern "C" {

NM_API nm_status_t NM_CALL nm_get_abi_table(const nm_abi_table_t** out_table) {
    if (!out_table) return NM_ERR_INVALID_ARG;
    *out_table = &kAbiTable;
    return NM_OK;
}

NM_API nm_status_t NM_CALL nm_initialize_for_build(
    nm_build_id_t build_id,
    uint64_t      image_base,
    uint32_t      image_size,
    const char*   exe_sha256_or_null) {
    if (build_id == NM_BUILD_UNKNOWN) {
        return NM_ERR_UNSUPPORTED_BUILD;
    }
    if (image_base == 0) {
        return NM_ERR_INVALID_ARG;
    }

    auto& s = state();
    std::lock_guard<std::mutex> lock(s.init_mutex);

    s.hooks.remove_all();
    s.frame_queue.clear();
    s.channels.clear();
    s.allocator.reset();
    s.clr.reset();

    s.build_id = build_id;
    s.image_base = image_base;
    s.image_size = image_size;
    s.flags = NM_HOST_FLAG_INITIALIZED;
    if (build_id == NM_BUILD_SYNTHETIC) {
        s.flags |= NM_HOST_FLAG_SIMULATED;
    }

    std::memset(s.exe_sha256, 0, sizeof(s.exe_sha256));
    if (exe_sha256_or_null) {
        std::snprintf(s.exe_sha256, sizeof(s.exe_sha256), "%s", exe_sha256_or_null);
    }

    // Wire game allocator function pointers ONLY on real Win64 game builds (not synthetic tests).
#if defined(_WIN32)
    if (build_id != NM_BUILD_SYNTHETIC) {
        const EmbeddedSymbol* alloc_sym = find_symbol(build_id, "allocator.alloc");
        const EmbeddedSymbol* free_sym  = find_symbol(build_id, "allocator.free");
        if (alloc_sym && free_sym && alloc_sym->rva != 0 && free_sym->rva != 0) {
            const uint64_t alloc_addr = image_base + alloc_sym->rva;
            const uint64_t free_addr  = image_base + free_sym->rva;
            const bool alloc_ok = !alloc_sym->expected_bytes_hex ||
                                  verify_memory_bytes(alloc_addr, alloc_sym->expected_bytes_hex) == NM_OK;
            const bool free_ok  = !free_sym->expected_bytes_hex ||
                                  verify_memory_bytes(free_addr, free_sym->expected_bytes_hex) == NM_OK;
            if (alloc_ok && free_ok) {
                s.allocator.configure(
                    reinterpret_cast<GameAllocator::RawAllocFn>(static_cast<uintptr_t>(alloc_addr)),
                    reinterpret_cast<GameAllocator::RawFreeFn>(static_cast<uintptr_t>(free_addr)));
            }
        }
    }
#endif

    s.initialized = true;
    return NM_OK;
}

NM_API nm_status_t NM_CALL nm_shutdown(void) {
    auto& s = state();
    std::lock_guard<std::mutex> lock(s.init_mutex);
    s.hooks.remove_all();
    s.frame_queue.clear();
    s.channels.clear();
    s.allocator.reset();
    s.clr.reset();
    s.initialized = false;
    s.build_id = NM_BUILD_UNKNOWN;
    s.flags = NM_HOST_FLAG_NONE;
    s.image_base = 0;
    s.image_size = 0;
    std::memset(s.exe_sha256, 0, sizeof(s.exe_sha256));
    return NM_OK;
}

NM_API nm_status_t NM_CALL nm_get_host_info(nm_host_info_t* out_info) {
    if (!out_info) return NM_ERR_INVALID_ARG;
    auto& s = state();
    std::lock_guard<std::mutex> lock(s.init_mutex);
    if (!s.initialized) return NM_ERR_NOT_INITIALIZED;

    uint32_t sym_count = 0;
    (void)get_symbol_table(s.build_id, sym_count);

    std::memset(out_info, 0, sizeof(*out_info));
    out_info->struct_size = sizeof(nm_host_info_t);
    out_info->abi_version = NM_ABI_VERSION;
    out_info->build_id = s.build_id;
    out_info->flags = s.flags;
    if (s.clr.is_booting()) out_info->flags |= NM_HOST_FLAG_CORECLR_BOOTING;
    if (s.clr.is_booted())  out_info->flags |= NM_HOST_FLAG_CORECLR_BOOTED;
    out_info->image_base = s.image_base;
    out_info->image_size = s.image_size;
    out_info->symbol_count = sym_count;
    std::snprintf(out_info->build_version, sizeof(out_info->build_version), "%s", build_version_string(s.build_id));
    std::snprintf(out_info->exe_sha256, sizeof(out_info->exe_sha256), "%s", s.exe_sha256);
    return NM_OK;
}

NM_API nm_status_t NM_CALL nm_lookup_symbol(const char* name, nm_symbol_info_t* out_symbol) {
    if (!name || !out_symbol) return NM_ERR_INVALID_ARG;
    auto& s = state();
    if (!s.initialized) return NM_ERR_NOT_INITIALIZED;

    const EmbeddedSymbol* sym = find_symbol(s.build_id, name);
    if (!sym) return NM_ERR_SYMBOL_NOT_FOUND;

    out_symbol->name = sym->name;
    out_symbol->category = sym->category;
    out_symbol->kind = sym->kind;
    out_symbol->status = sym->status;
    out_symbol->rva = sym->rva;
    out_symbol->runtime_address = (sym->status == NM_SYM_STATUS_UNVERIFIED || sym->rva == 0)
                                      ? 0ull
                                      : (s.image_base + sym->rva);
    out_symbol->expected_bytes_hex = sym->expected_bytes_hex;

    if (sym->status == NM_SYM_STATUS_UNVERIFIED || sym->rva == 0) {
        return NM_ERR_SYMBOL_UNVERIFIED;
    }
    return NM_OK;
}

NM_API nm_status_t NM_CALL nm_get_symbol_at(uint32_t index, nm_symbol_info_t* out_symbol) {
    if (!out_symbol) return NM_ERR_INVALID_ARG;
    auto& s = state();
    if (!s.initialized) return NM_ERR_NOT_INITIALIZED;

    uint32_t count = 0;
    const EmbeddedSymbol* table = get_symbol_table(s.build_id, count);
    if (!table || index >= count) return NM_ERR_SYMBOL_NOT_FOUND;

    const EmbeddedSymbol& sym = table[index];
    out_symbol->name = sym.name;
    out_symbol->category = sym.category;
    out_symbol->kind = sym.kind;
    out_symbol->status = sym.status;
    out_symbol->rva = sym.rva;
    out_symbol->runtime_address = (sym.status == NM_SYM_STATUS_UNVERIFIED || sym.rva == 0)
                                      ? 0ull
                                      : (s.image_base + sym.rva);
    out_symbol->expected_bytes_hex = sym.expected_bytes_hex;
    return NM_OK;
}

NM_API nm_status_t NM_CALL nm_verify_bytes(uint64_t address, const char* expected_bytes_hex) {
    return verify_memory_bytes(address, expected_bytes_hex);
}

NM_API nm_status_t NM_CALL nm_install_inline_hook(
    const char*       symbol_name,
    void*             detour_fn,
    void**            out_trampoline,
    nm_hook_handle_t* out_handle) {
    if (!symbol_name || !detour_fn || !out_handle) return NM_ERR_INVALID_ARG;
    nm_symbol_info_t info{};
    const nm_status_t lookup_st = nm_lookup_symbol(symbol_name, &info);
    if (lookup_st != NM_OK) {
        return lookup_st;
    }
    return state().hooks.install_inline(
        info.runtime_address, info.expected_bytes_hex, detour_fn, out_trampoline, out_handle);
}

NM_API nm_status_t NM_CALL nm_install_inline_hook_at(
    uint64_t          address,
    const char*       expected_bytes_hex,
    void*             detour_fn,
    void**            out_trampoline,
    nm_hook_handle_t* out_handle) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().hooks.install_inline(address, expected_bytes_hex, detour_fn, out_trampoline, out_handle);
}

NM_API nm_status_t NM_CALL nm_install_vtable_hook(
    void*             vtable_address,
    uint32_t          slot_index,
    void*             detour_fn,
    void**            out_original_fn,
    nm_hook_handle_t* out_handle) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().hooks.install_vtable(vtable_address, slot_index, detour_fn, out_original_fn, out_handle);
}

NM_API nm_status_t NM_CALL nm_remove_hook(nm_hook_handle_t handle) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().hooks.remove(handle);
}

NM_API nm_status_t NM_CALL nm_register_frame_callback(
    nm_frame_callback_t cb,
    void*               user_data,
    uint64_t*           out_token) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().frame_queue.register_frame_callback(cb, user_data, out_token);
}

NM_API nm_status_t NM_CALL nm_unregister_frame_callback(uint64_t token) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().frame_queue.unregister_frame_callback(token);
}

NM_API nm_status_t NM_CALL nm_enqueue_main_thread(nm_work_callback_t cb, void* user_data) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().frame_queue.enqueue(cb, user_data);
}

NM_API nm_status_t NM_CALL nm_drain_main_thread_queue(float delta_seconds, uint32_t max_items) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().frame_queue.drain(delta_seconds, max_items);
}

NM_API nm_status_t NM_CALL nm_call_guarded(
    nm_guarded_fn_t      fn,
    void*                context,
    int64_t*             out_result,
    nm_exception_info_t* out_exception) {
    return invoke_guarded(fn, context, out_result, out_exception);
}

NM_API nm_status_t NM_CALL nm_game_alloc(size_t size, size_t alignment, uint16_t tag, void** out_ptr) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().allocator.alloc(size, alignment, tag, out_ptr);
}

NM_API nm_status_t NM_CALL nm_game_free(void* ptr, size_t alignment) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().allocator.free(ptr, alignment);
}

NM_API nm_status_t NM_CALL nm_register_file_overlay(
    nm_file_overlay_callback_t cb,
    nm_file_overlay_free_t     free_cb,
    void*                      user_data,
    uint64_t*                  out_token) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().channels.register_file_overlay(cb, free_cb, user_data, out_token);
}

NM_API nm_status_t NM_CALL nm_unregister_file_overlay(uint64_t token) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().channels.unregister_file_overlay(token);
}

NM_API nm_status_t NM_CALL nm_resolve_file_overlay(
    const char* virtual_path_utf8,
    uint8_t**   out_data,
    size_t*     out_size) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().channels.resolve_file_overlay(virtual_path_utf8, out_data, out_size);
}

NM_API nm_status_t NM_CALL nm_register_ruleset_handler(
    uint32_t                 ruleset_id,
    nm_ruleset_rx_callback_t cb,
    void*                    user_data,
    uint64_t*                out_token) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().channels.register_ruleset_handler(ruleset_id, cb, user_data, out_token);
}

NM_API nm_status_t NM_CALL nm_unregister_ruleset_handler(uint64_t token) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().channels.unregister_ruleset_handler(token);
}

NM_API nm_status_t NM_CALL nm_send_ruleset_packet(
    uint32_t       ruleset_id,
    const uint8_t* payload,
    uint32_t       length) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().channels.send_ruleset_packet(ruleset_id, payload, length);
}

NM_API nm_status_t NM_CALL nm_register_chat_channel_handler(
    nm_chat_rx_callback_t cb,
    void*                 user_data,
    uint64_t*             out_token) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().channels.register_chat_handler(cb, user_data, out_token);
}

NM_API nm_status_t NM_CALL nm_unregister_chat_channel_handler(uint64_t token) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().channels.unregister_chat_handler(token);
}

NM_API nm_status_t NM_CALL nm_send_chat_line(const char* text_utf8) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().channels.send_chat_line(text_utf8);
}

NM_API nm_status_t NM_CALL nm_dispatch_ruleset_rx(
    uint32_t       ruleset_id,
    const uint8_t* payload,
    uint32_t       length) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().channels.dispatch_ruleset_rx(ruleset_id, payload, length);
}

NM_API int32_t NM_CALL nm_dispatch_chat_rx(const char* sender_utf8, const char* text_utf8) {
    if (!state().initialized) return 0;
    return state().channels.dispatch_chat_rx(sender_utf8, text_utf8);
}

NM_API nm_status_t NM_CALL nm_set_packet_tx_override(nm_packet_tx_override_t tx_fn, void* user_data) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().channels.set_tx_override(tx_fn, user_data);
}

NM_API nm_status_t NM_CALL nm_bootstrap_managed_host(
    const char* hostfxr_path_utf8,
    const char* runtimeconfig_path_utf8,
    const char* host_assembly_path_utf8) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().clr.bootstrap_async_or_sync(
        hostfxr_path_utf8, runtimeconfig_path_utf8, host_assembly_path_utf8, &kAbiTable);
}

NM_API void NM_CALL LocalExileClientHost_ImportAnchor(void) {
    // Intentionally empty anchor export referenced by PE32+ import-table patcher (.nmimp).
}

} // extern "C"

#if defined(_WIN32)
namespace {

DWORD WINAPI DeferredInitThread(LPVOID) {
    // Compute base of main game module (supports DYNAMIC_BASE / ASLR).
    HMODULE main_mod = GetModuleHandleW(nullptr);
    if (!main_mod) return 0;

    const auto base = reinterpret_cast<uint64_t>(main_mod);
    auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(main_mod);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return 0;
    auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return 0;

    // Verify 3.29 frame hook + appGlobal signature before touching any game code.
    const bool frame_ok = verify_memory_bytes(base + 0x1528fbfull, "488b5d284883eb10") == NM_OK;
    const bool app_ok   = verify_memory_bytes(base + 0x00f3b30ull, "488b05????????488907488b05") == NM_OK;
    if (!frame_ok || !app_ok) {
        // Unknown build: leave client completely stock.
        return 0;
    }

    // Initialize shim for 3.29.3d (or 3.29.3c if matched by external config).
    nm_initialize_for_build(NM_BUILD_3_29_3D_328228, base, nt->OptionalHeader.SizeOfImage, nullptr);
    return 0;
}

} // namespace

BOOL WINAPI DllMain(HINSTANCE hinst, DWORD reason, LPVOID reserved) {
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hinst);
        // Never run CoreCLR or heavy init under the Windows loader lock!
        HANDLE th = CreateThread(nullptr, 0, DeferredInitThread, nullptr, 0, nullptr);
        if (th) CloseHandle(th);
    } else if (reason == DLL_PROCESS_DETACH) {
        nm_shutdown();
    }
    return TRUE;
}
#endif
