#include "nativemods_abi.h"

#include "address_table.hpp"
#include "coreclr_bootstrap.hpp"
#include "exception_wall.hpp"
#include "frame_queue.hpp"
#include "game_allocator.hpp"
#include "hook_engine.hpp"
#include "live_audio.hpp"
#include "live_engine_hooks.hpp"
#include "overlay_and_channels.hpp"
#include "shim_logger.hpp"

#include <atomic>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>

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
    LiveAudioManager    audio;
    LiveEngineHooks     live_hooks;
    CoreClrBootstrapper clr;

    nm_hook_handle_t    frame_hook_handle = NM_HOOK_INVALID;
    void*               frame_stub_page = nullptr;
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
    &nm_register_file_overlay_dir,
    &nm_unregister_file_overlay_dir,
    &nm_has_file_overlay,
    &nm_free_file_overlay_buffer,
    &nm_audio_play_ogg,
    &nm_audio_play_event,
    &nm_audio_stop,
    &nm_audio_set_volume,
    &nm_audio_is_playing,
    &nm_ui_get_root,
    &nm_ui_create_window,
    &nm_ui_find_child,
    &nm_ui_set_visible,
    &nm_ui_set_text,
    &nm_ui_subscribe_click,
    &nm_set_file_overlay_known_paths,
    &nm_get_active_connection_token,
    &nm_install_inline_chain_hook,
    &nm_install_inline_chain_hook_sym,
    &nm_install_vtable_chain_hook,
    &nm_call_native_guarded,
    &nm_files_read_stock,
    &nm_files_invalidate,
    &nm_send_raw_packet,
    &nm_register_opcode_handler_group,
    &nm_unregister_opcode_handler_group,
    &nm_stream_read,
    &nm_ui_create_window_into,
    &nm_ui_get_rect,
    &nm_ui_set_position,
    &nm_ui_set_size,
    &nm_ui_set_enabled,
    &nm_ui_remove_child,
    &nm_ui_destroy_control,
    &nm_ui_textbox_get_text,
    &nm_ui_textbox_set_text,
    &nm_ui_textbox_subscribe_changed,
    &nm_ui_textbox_subscribe_submitted,
    &nm_ui_checkbox_get_checked,
    &nm_ui_checkbox_set_checked,
    &nm_ui_checkbox_subscribe_changed,
    &nm_ui_unsubscribe,
    &nm_clipboard_get_text,
    &nm_clipboard_set_text,
};

#if defined(_WIN32)
wchar_t               g_dll_dir_w[MAX_PATH] = {};
std::atomic<void*>    g_frame_trampoline{nullptr};
std::atomic<uint64_t> g_live_frame_count{0};
std::atomic<bool>     g_auto_clr_triggered{false};
LARGE_INTEGER         g_qpc_freq{};
LARGE_INTEGER         g_last_frame_qpc{};

std::string wide_to_utf8(const wchar_t* wide) {
    if (!wide || wide[0] == L'\0') return {};
    const int needed = WideCharToMultiByte(CP_UTF8, 0, wide, -1, nullptr, 0, nullptr, nullptr);
    if (needed <= 1) return {};
    std::string out(static_cast<size_t>(needed - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, wide, -1, out.data(), needed, nullptr, nullptr);
    return out;
}

bool file_exists_utf8(const std::string& path_utf8) {
    if (path_utf8.empty()) return false;
    const int needed = MultiByteToWideChar(CP_UTF8, 0, path_utf8.c_str(), -1, nullptr, 0);
    if (needed <= 1) return false;
    std::wstring w(static_cast<size_t>(needed - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, path_utf8.c_str(), -1, w.data(), needed);
    const DWORD attrs = GetFileAttributesW(w.c_str());
    return attrs != INVALID_FILE_ATTRIBUTES && (attrs & FILE_ATTRIBUTE_DIRECTORY) == 0;
}

std::string get_env_utf8(const wchar_t* name) {
    wchar_t buf[1024] = {};
    const DWORD n = GetEnvironmentVariableW(name, buf, 1024);
    if (n == 0 || n >= 1024) return {};
    return wide_to_utf8(buf);
}

bool is_memory_readable(uint64_t address, size_t len) {
    if (address == 0 || len == 0) return false;
    MEMORY_BASIC_INFORMATION mbi{};
    if (VirtualQuery(reinterpret_cast<LPCVOID>(static_cast<uintptr_t>(address)), &mbi, sizeof(mbi)) == 0) {
        return false;
    }
    if (mbi.State != MEM_COMMIT) return false;
    const DWORD prot = mbi.Protect & 0xFFu;
    if (prot == PAGE_NOACCESS || (mbi.Protect & PAGE_GUARD) != 0) return false;
    const uint64_t region_end = reinterpret_cast<uint64_t>(mbi.BaseAddress) + static_cast<uint64_t>(mbi.RegionSize);
    return (address + len) <= region_end;
}

void trigger_auto_coreclr_bootstrap(const char* trigger_reason) {
    if (g_auto_clr_triggered.exchange(true, std::memory_order_acq_rel)) {
        return;
    }

    const std::string dll_dir = wide_to_utf8(g_dll_dir_w);
    const std::string env_host_dir = get_env_utf8(L"NATIVEMODS_HOST_DIR");

    std::string candidates[3];
    size_t cand_count = 0;
    if (!env_host_dir.empty()) {
        candidates[cand_count++] = env_host_dir;
    }
    if (!dll_dir.empty()) {
        candidates[cand_count++] = dll_dir;
        candidates[cand_count++] = dll_dir + "\\host";
    }

    for (size_t i = 0; i < cand_count; ++i) {
        const std::string& dir = candidates[i];
        const std::string fxr = dir + "\\hostfxr.dll";
        const std::string cfg = dir + "\\LocalExile.Client.Host.runtimeconfig.json";
        const std::string asm_dll = dir + "\\LocalExile.Client.Host.dll";
        if (file_exists_utf8(fxr) && file_exists_utf8(cfg) && file_exists_utf8(asm_dll)) {
            logger_logf("INFO", "Triggering deferred off-game-thread CoreCLR bootstrap (%s) from '%s'",
                        trigger_reason, dir.c_str());
            const nm_status_t st = nm_bootstrap_managed_host(fxr.c_str(), cfg.c_str(), asm_dll.c_str());
            if (st != NM_OK) {
                logger_logf("ERROR", "nm_bootstrap_managed_host failed with status %d", static_cast<int>(st));
            }
            return;
        }
    }

    logger_logf("WARN", "CoreCLR auto-bootstrap skipped (%s): hostfxr.dll + LocalExile.Client.Host.runtimeconfig.json + LocalExile.Client.Host.dll not found beside DLL",
                trigger_reason);
}

// Invoked on the game thread by our register-preserving frame.state_stack_pass_a stub.
// Returns the trampoline address in RAX so the stub can tail-jump directly to the original function.
extern "C" void* NM_CALL on_live_game_frame_tick() {
    const uint64_t frame_num = g_live_frame_count.fetch_add(1, std::memory_order_relaxed) + 1;

    float dt = 1.0f / 60.0f;
    LARGE_INTEGER now{};
    QueryPerformanceCounter(&now);
    if (g_qpc_freq.QuadPart > 0 && g_last_frame_qpc.QuadPart > 0) {
        const double sec = static_cast<double>(now.QuadPart - g_last_frame_qpc.QuadPart) /
                           static_cast<double>(g_qpc_freq.QuadPart);
        if (sec >= 0.0001 && sec <= 0.25) {
            dt = static_cast<float>(sec);
        }
    }
    g_last_frame_qpc = now;

    if (frame_num == 1) {
        logger_logf("INFO", "First frame hook hit on game thread (tid=%lu)",
                    static_cast<unsigned long>(GetCurrentThreadId()));
        trigger_auto_coreclr_bootstrap("first-frame");
    } else if (frame_num == 10 || frame_num == 60 || frame_num == 120 || (frame_num % 300) == 0) {
        auto& s = state();
        logger_logf("INFO", "Frame heartbeat: frames=%llu tid=%lu coreclr_booted=%d coreclr_booting=%d msvc_cxx_passthrough=%llu",
                    static_cast<unsigned long long>(frame_num),
                    static_cast<unsigned long>(GetCurrentThreadId()),
                    s.clr.is_booted() ? 1 : 0,
                    s.clr.is_booting() ? 1 : 0,
                    static_cast<unsigned long long>(get_msvc_cxx_passthrough_count()));
    }

    (void)nm_drain_main_thread_queue(dt, 64);
    state().audio.tick_update();

    void* tramp = g_frame_trampoline.load(std::memory_order_acquire);
    while (!tramp) {
        YieldProcessor();
        tramp = g_frame_trampoline.load(std::memory_order_acquire);
    }
    return tramp;
}

// Builds a register-preserving x86-64 entry stub for frame.state_stack_pass_a.
// Preserves rcx, rdx, r8, r9, xmm0..xmm3 and Win64 16-byte stack alignment across on_live_game_frame_tick(),
// then jumps to the returned trampoline address in RAX.
void* build_pass_a_frame_stub() {
    void* page = HookEngine::alloc_exec_page(4096);
    if (!page) return nullptr;
    auto* p = static_cast<uint8_t*>(page);
    size_t i = 0;

    // push rcx ; push rdx ; push r8 ; push r9
    p[i++] = 0x51;
    p[i++] = 0x52;
    p[i++] = 0x41; p[i++] = 0x50;
    p[i++] = 0x41; p[i++] = 0x51;

    // sub rsp, 0x68  (rsp % 16 == 0, 0x20 shadow + 0x40 xmm0..xmm3 + 0x08 align)
    p[i++] = 0x48; p[i++] = 0x83; p[i++] = 0xEC; p[i++] = 0x68;

    // movaps [rsp+0x20..0x50], xmm0..xmm3
    p[i++] = 0x0F; p[i++] = 0x29; p[i++] = 0x44; p[i++] = 0x24; p[i++] = 0x20;
    p[i++] = 0x0F; p[i++] = 0x29; p[i++] = 0x4C; p[i++] = 0x24; p[i++] = 0x30;
    p[i++] = 0x0F; p[i++] = 0x29; p[i++] = 0x54; p[i++] = 0x24; p[i++] = 0x40;
    p[i++] = 0x0F; p[i++] = 0x29; p[i++] = 0x5C; p[i++] = 0x24; p[i++] = 0x50;

    // movabs rax, &on_live_game_frame_tick ; call rax
    const uint64_t cb_addr = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(&on_live_game_frame_tick));
    p[i++] = 0x48; p[i++] = 0xB8;
    std::memcpy(p + i, &cb_addr, sizeof(cb_addr));
    i += sizeof(cb_addr);
    p[i++] = 0xFF; p[i++] = 0xD0;

    // movaps xmm0..xmm3, [rsp+0x20..0x50]  (does not touch RAX)
    p[i++] = 0x0F; p[i++] = 0x28; p[i++] = 0x44; p[i++] = 0x24; p[i++] = 0x20;
    p[i++] = 0x0F; p[i++] = 0x28; p[i++] = 0x4C; p[i++] = 0x24; p[i++] = 0x30;
    p[i++] = 0x0F; p[i++] = 0x28; p[i++] = 0x54; p[i++] = 0x24; p[i++] = 0x40;
    p[i++] = 0x0F; p[i++] = 0x28; p[i++] = 0x5C; p[i++] = 0x24; p[i++] = 0x50;

    // add rsp, 0x68 ; pop r9 ; pop r8 ; pop rdx ; pop rcx
    p[i++] = 0x48; p[i++] = 0x83; p[i++] = 0xC4; p[i++] = 0x68;
    p[i++] = 0x41; p[i++] = 0x59;
    p[i++] = 0x41; p[i++] = 0x58;
    p[i++] = 0x5A;
    p[i++] = 0x59;

    // jmp rax (tail-jump to trampoline returned by on_live_game_frame_tick)
    p[i++] = 0xFF; p[i++] = 0xE0;

    FlushInstructionCache(GetCurrentProcess(), page, i);
    return page;
}
#endif

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

    s.live_hooks.reset(&s.hooks);
    s.hooks.remove_all();
    s.frame_hook_handle = NM_HOOK_INVALID;
    if (s.frame_stub_page) {
        HookEngine::free_exec_page(s.frame_stub_page, 4096);
        s.frame_stub_page = nullptr;
    }
#if defined(_WIN32)
    g_frame_trampoline.store(nullptr, std::memory_order_release);
    g_live_frame_count.store(0, std::memory_order_relaxed);
    g_auto_clr_triggered.store(false, std::memory_order_release);
#endif
    s.frame_queue.clear();
    s.audio.reset();
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
    ensure_exception_wall_installed();
    const std::string dll_dir = wide_to_utf8(g_dll_dir_w);
    s.audio.configure(dll_dir, image_base, build_id, &s.channels);
    if (build_id != NM_BUILD_SYNTHETIC) {
        const EmbeddedSymbol* alloc_sym = find_symbol(build_id, "allocator.alloc");
        const EmbeddedSymbol* free_sym  = find_symbol(build_id, "allocator.free");
        const EmbeddedSymbol* ctr_sym   = find_symbol(build_id, "allocator.global_byte_counter");
        if (alloc_sym && free_sym && alloc_sym->rva != 0 && free_sym->rva != 0) {
            const uint64_t alloc_addr = image_base + alloc_sym->rva;
            const uint64_t free_addr  = image_base + free_sym->rva;
            if (is_memory_readable(alloc_addr, 16) && is_memory_readable(free_addr, 16)) {
                const bool alloc_ok = !alloc_sym->expected_bytes_hex ||
                                      verify_memory_bytes(alloc_addr, alloc_sym->expected_bytes_hex) == NM_OK;
                const bool free_ok  = !free_sym->expected_bytes_hex ||
                                      verify_memory_bytes(free_addr, free_sym->expected_bytes_hex) == NM_OK;
                if (alloc_ok && free_ok) {
                    volatile int64_t* byte_ctr = nullptr;
                    if (ctr_sym && ctr_sym->rva != 0 && is_memory_readable(image_base + ctr_sym->rva, 8)) {
                        byte_ctr = reinterpret_cast<volatile int64_t*>(static_cast<uintptr_t>(image_base + ctr_sym->rva));
                    }
                    s.allocator.configure(
                        reinterpret_cast<GameAllocator::RawAllocFn>(static_cast<uintptr_t>(alloc_addr)),
                        reinterpret_cast<GameAllocator::RawFreeFn>(static_cast<uintptr_t>(free_addr)),
                        byte_ctr);
                }
            }
        }
    }
#else
    s.audio.configure(std::string{}, image_base, build_id, &s.channels);
#endif

    s.initialized = true;
    logger_logf("INFO", "nm_initialize_for_build: build=%s (%d) image_base=0x%llX image_size=0x%X sha256=%s",
                build_version_string(build_id),
                static_cast<int>(build_id),
                static_cast<unsigned long long>(image_base),
                image_size,
                s.exe_sha256[0] ? s.exe_sha256 : "<none>");
    return NM_OK;
}

NM_API nm_status_t NM_CALL nm_shutdown(void) {
    auto& s = state();
    std::lock_guard<std::mutex> lock(s.init_mutex);
    s.live_hooks.reset(&s.hooks);
    s.hooks.remove_all();
    s.frame_hook_handle = NM_HOOK_INVALID;
    if (s.frame_stub_page) {
        HookEngine::free_exec_page(s.frame_stub_page, 4096);
        s.frame_stub_page = nullptr;
    }
#if defined(_WIN32)
    g_frame_trampoline.store(nullptr, std::memory_order_release);
#endif
    s.frame_queue.clear();
    s.audio.reset();
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

NM_API nm_status_t NM_CALL nm_install_inline_chain_hook(
    uint64_t                 address,
    const char*              expected_bytes_hex,
    int32_t                  phase,
    int32_t                  priority,
    nm_hook_chain_callback_t cb,
    void*                    user_data,
    nm_hook_handle_t*        out_handle) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().hooks.install_inline_chain(
        address, expected_bytes_hex, static_cast<nm_hook_phase_t>(phase), priority, cb, user_data, out_handle);
}

NM_API nm_status_t NM_CALL nm_install_inline_chain_hook_sym(
    const char*              symbol_name,
    int32_t                  phase,
    int32_t                  priority,
    nm_hook_chain_callback_t cb,
    void*                    user_data,
    nm_hook_handle_t*        out_handle) {
    if (!symbol_name || !cb || !out_handle) return NM_ERR_INVALID_ARG;
    nm_symbol_info_t info{};
    const nm_status_t lookup_st = nm_lookup_symbol(symbol_name, &info);
    if (lookup_st != NM_OK) {
        return lookup_st;
    }
    return state().hooks.install_inline_chain(
        info.runtime_address, info.expected_bytes_hex, static_cast<nm_hook_phase_t>(phase), priority, cb, user_data, out_handle);
}

NM_API nm_status_t NM_CALL nm_install_vtable_chain_hook(
    void*                    vtable_address,
    uint32_t                 slot_index,
    int32_t                  phase,
    int32_t                  priority,
    nm_hook_chain_callback_t cb,
    void*                    user_data,
    nm_hook_handle_t*        out_handle) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().hooks.install_vtable_chain(
        vtable_address, slot_index, static_cast<nm_hook_phase_t>(phase), priority, cb, user_data, out_handle);
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

NM_API nm_status_t NM_CALL nm_call_native_guarded(
    nm_native_call_desc_t* desc,
    nm_exception_info_t*   out_exception) {
    return invoke_native_guarded(desc, out_exception);
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

NM_API nm_status_t NM_CALL nm_register_file_overlay_dir(const char* root_dir_utf8, uint64_t* out_token) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().channels.register_file_overlay_dir(root_dir_utf8, out_token);
}

NM_API nm_status_t NM_CALL nm_unregister_file_overlay_dir(uint64_t token) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().channels.unregister_file_overlay_dir(token);
}

NM_API nm_status_t NM_CALL nm_has_file_overlay(const char* virtual_path_utf8, size_t* out_size) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().channels.has_file_overlay(virtual_path_utf8, out_size);
}

NM_API nm_status_t NM_CALL nm_free_file_overlay_buffer(uint8_t* data) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().channels.free_file_overlay_buffer(data);
}

NM_API nm_status_t NM_CALL nm_set_file_overlay_known_paths(
    uint64_t           token,
    const char* const* paths_utf8,
    size_t             path_count) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().channels.set_file_overlay_known_paths(token, paths_utf8, path_count);
}

NM_API nm_status_t NM_CALL nm_get_active_connection_token(uint64_t* out_token) {
    if (!out_token) return NM_ERR_INVALID_ARG;
    *out_token = 0;
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().live_hooks.get_active_connection_token(out_token);
}

NM_API nm_status_t NM_CALL nm_files_read_stock(const char* virtual_path_utf8, uint8_t** out_data, size_t* out_size) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    auto& s = state();
    return s.live_hooks.files_read_stock(virtual_path_utf8, s.channels, s.allocator, out_data, out_size);
}

NM_API nm_status_t NM_CALL nm_files_invalidate(const char* virtual_path_utf8) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().live_hooks.files_invalidate(virtual_path_utf8);
}

NM_API nm_status_t NM_CALL nm_audio_play_ogg(
    const char* virtual_or_disk_path_utf8,
    float       volume,
    int32_t     loop,
    int32_t*    out_handle) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().audio.play_ogg(virtual_or_disk_path_utf8, volume, loop, out_handle);
}

NM_API nm_status_t NM_CALL nm_audio_play_event(
    const char* fmod_event_utf8,
    float       volume,
    int32_t*    out_handle) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().audio.play_event(fmod_event_utf8, volume, out_handle);
}

NM_API nm_status_t NM_CALL nm_audio_stop(int32_t channel_handle) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().audio.stop(channel_handle);
}

NM_API nm_status_t NM_CALL nm_audio_set_volume(int32_t channel_handle, float volume) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().audio.set_volume(channel_handle, volume);
}

NM_API nm_status_t NM_CALL nm_audio_is_playing(int32_t channel_handle, int32_t* out_playing) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().audio.is_playing(channel_handle, out_playing);
}

NM_API nm_status_t NM_CALL nm_ui_get_root(void** out_root) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().live_hooks.ui_get_root(out_root);
}

NM_API nm_status_t NM_CALL nm_ui_create_window(const char* virtual_path_utf8, void** out_control) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().live_hooks.ui_create_window(virtual_path_utf8, out_control);
}

NM_API nm_status_t NM_CALL nm_ui_find_child(void* parent_control, const char* dot_path_utf8, void** out_child) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().live_hooks.ui_find_child(parent_control, dot_path_utf8, out_child);
}

NM_API nm_status_t NM_CALL nm_ui_set_visible(void* control, int32_t visible) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().live_hooks.ui_set_visible(control, visible);
}

NM_API nm_status_t NM_CALL nm_ui_set_text(void* control, const char* text_utf8, int32_t is_button) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().live_hooks.ui_set_text(control, text_utf8, is_button);
}

NM_API nm_status_t NM_CALL nm_ui_subscribe_click(
    void*                  button_control,
    nm_ui_event_callback_t cb,
    void*                  user_data,
    uint64_t*              out_token) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().live_hooks.ui_subscribe_click(button_control, cb, user_data, out_token);
}

NM_API nm_status_t NM_CALL nm_ui_create_window_into(void* parent_control, const char* virtual_path_utf8, void** out_control) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().live_hooks.ui_create_window_into(parent_control, virtual_path_utf8, out_control);
}

NM_API nm_status_t NM_CALL nm_ui_get_rect(void* control, float* out_x, float* out_y, float* out_w, float* out_h) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().live_hooks.ui_get_rect(control, out_x, out_y, out_w, out_h);
}

NM_API nm_status_t NM_CALL nm_ui_set_position(void* control, float x, float y) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().live_hooks.ui_set_position(control, x, y);
}

NM_API nm_status_t NM_CALL nm_ui_set_size(void* control, float w, float h) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().live_hooks.ui_set_size(control, w, h);
}

NM_API nm_status_t NM_CALL nm_ui_set_enabled(void* control, int32_t enabled) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().live_hooks.ui_set_enabled(control, enabled);
}

NM_API nm_status_t NM_CALL nm_ui_remove_child(void* parent_control, void* child_control) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().live_hooks.ui_remove_child(parent_control, child_control);
}

NM_API nm_status_t NM_CALL nm_ui_destroy_control(void* control) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().live_hooks.ui_destroy_control(control);
}

NM_API nm_status_t NM_CALL nm_ui_textbox_get_text(void* textbox_control, char* out_utf8, size_t capacity, size_t* out_length) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().live_hooks.ui_textbox_get_text(textbox_control, out_utf8, capacity, out_length);
}

NM_API nm_status_t NM_CALL nm_ui_textbox_set_text(void* textbox_control, const char* text_utf8) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().live_hooks.ui_textbox_set_text(textbox_control, text_utf8);
}

NM_API nm_status_t NM_CALL nm_ui_textbox_subscribe_changed(
    void*                  textbox_control,
    nm_ui_event_callback_t cb,
    void*                  user_data,
    uint64_t*              out_token) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().live_hooks.ui_textbox_subscribe_changed(textbox_control, cb, user_data, out_token);
}

NM_API nm_status_t NM_CALL nm_ui_textbox_subscribe_submitted(
    void*                  textbox_control,
    nm_ui_event_callback_t cb,
    void*                  user_data,
    uint64_t*              out_token) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().live_hooks.ui_textbox_subscribe_submitted(textbox_control, cb, user_data, out_token);
}

NM_API nm_status_t NM_CALL nm_ui_checkbox_get_checked(void* checkbox_control, int32_t* out_checked) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().live_hooks.ui_checkbox_get_checked(checkbox_control, out_checked);
}

NM_API nm_status_t NM_CALL nm_ui_checkbox_set_checked(void* checkbox_control, int32_t checked) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().live_hooks.ui_checkbox_set_checked(checkbox_control, checked);
}

NM_API nm_status_t NM_CALL nm_ui_checkbox_subscribe_changed(
    void*                  checkbox_control,
    nm_ui_event_callback_t cb,
    void*                  user_data,
    uint64_t*              out_token) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().live_hooks.ui_checkbox_subscribe_changed(checkbox_control, cb, user_data, out_token);
}

NM_API nm_status_t NM_CALL nm_ui_unsubscribe(uint64_t token) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().live_hooks.ui_unsubscribe(token);
}

NM_API nm_status_t NM_CALL nm_clipboard_get_text(char* out_utf8, size_t capacity, size_t* out_length) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().live_hooks.clipboard_get_text(out_utf8, capacity, out_length);
}

NM_API nm_status_t NM_CALL nm_clipboard_set_text(const char* text_utf8) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().live_hooks.clipboard_set_text(text_utf8);
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
    const nm_status_t st = state().channels.send_ruleset_packet(ruleset_id, payload, length);
    if (st == NM_ERR_NOT_SUPPORTED_ON_PLATFORM) {
        return state().live_hooks.send_live_ruleset_packet(ruleset_id, payload, length);
    }
    return st;
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
    const nm_status_t st = state().channels.send_chat_line(text_utf8);
    if (st == NM_ERR_NOT_SUPPORTED_ON_PLATFORM) {
        return state().live_hooks.send_live_chat_line(text_utf8);
    }
    return st;
}

NM_API nm_status_t NM_CALL nm_send_raw_packet(uint16_t opcode, const uint8_t* payload, uint32_t length) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    if (length > 1048576u) return NM_ERR_PAYLOAD_TOO_LARGE;
    if (length > 0 && payload == nullptr) return NM_ERR_INVALID_ARG;
    const nm_status_t st = state().channels.try_send_raw_packet_override(opcode, payload, length);
    if (st == NM_ERR_NOT_SUPPORTED_ON_PLATFORM) {
        return state().live_hooks.send_live_raw_packet(opcode, payload, length);
    }
    return st;
}

NM_API nm_status_t NM_CALL nm_register_opcode_handler_group(
    uint16_t            min_opcode,
    uint16_t            max_opcode,
    nm_opcode_handler_t cb,
    void*               user_data,
    uint64_t*           out_token) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().channels.register_opcode_handler_group(min_opcode, max_opcode, cb, user_data, out_token);
}

NM_API nm_status_t NM_CALL nm_unregister_opcode_handler_group(uint64_t token) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().channels.unregister_opcode_handler_group(token);
}

NM_API nm_status_t NM_CALL nm_stream_read(void* stream_handle, void* dst, uint64_t length) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().live_hooks.stream_read(stream_handle, dst, length);
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

NM_API nm_status_t NM_CALL nm_dispatch_opcode_rx(
    uint16_t       opcode,
    const uint8_t* payload,
    uint32_t       length,
    int32_t*       out_handled) {
    if (!state().initialized) return NM_ERR_NOT_INITIALIZED;
    return state().channels.dispatch_opcode_rx(opcode, nullptr, payload, length, out_handled);
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
    const std::string dll_dir = wide_to_utf8(g_dll_dir_w);
    logger_init(dll_dir);
    ensure_exception_wall_installed();
    QueryPerformanceFrequency(&g_qpc_freq);

    logger_logf("INFO", "DLL_PROCESS_ATTACH deferred init (pid=%lu, tid=%lu, dll_dir='%s', abi=0x%08X)",
                static_cast<unsigned long>(GetCurrentProcessId()),
                static_cast<unsigned long>(GetCurrentThreadId()),
                dll_dir.c_str(),
                NM_ABI_VERSION);

    if (get_env_utf8(L"NATIVEMODS_DISABLE_AUTO_INIT") == "1") {
        logger_logf("INFO", "NATIVEMODS_DISABLE_AUTO_INIT=1; skipping automatic build detection");
        return 0;
    }

    // Inspect main executable module (supports DYNAMIC_BASE / ASLR).
    HMODULE main_mod = GetModuleHandleW(nullptr);
    if (!main_mod) {
        logger_logf("ERROR", "GetModuleHandleW(nullptr) returned null");
        return 0;
    }

    wchar_t exe_path_w[MAX_PATH * 2] = {};
    const DWORD exe_path_len = GetModuleFileNameW(main_mod, exe_path_w, MAX_PATH * 2);
    const std::string exe_path_utf8 = (exe_path_len > 0) ? wide_to_utf8(exe_path_w) : std::string("<unknown>");

    const auto base = reinterpret_cast<uint64_t>(main_mod);
    auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(main_mod);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) {
        logger_logf("ERROR", "Main module at 0x%llX has invalid DOS signature", static_cast<unsigned long long>(base));
        return 0;
    }
    auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + static_cast<uint64_t>(dos->e_lfanew));
    if (nt->Signature != IMAGE_NT_SIGNATURE) {
        logger_logf("ERROR", "Main module at 0x%llX has invalid NT signature", static_cast<unsigned long long>(base));
        return 0;
    }
    const uint32_t image_size = nt->OptionalHeader.SizeOfImage;

    std::string exe_sha256;
    if (exe_path_len > 0 && sha256_hex_file_wide(exe_path_w, exe_sha256)) {
        logger_logf("INFO", "Main executable: '%s' (image_base=0x%llX, image_size=0x%X, sha256=%s)",
                    exe_path_utf8.c_str(),
                    static_cast<unsigned long long>(base),
                    image_size,
                    exe_sha256.c_str());
    } else {
        logger_logf("WARN", "Main executable: '%s' (image_base=0x%llX, image_size=0x%X, sha256=<unavailable>)",
                    exe_path_utf8.c_str(),
                    static_cast<unsigned long long>(base),
                    image_size);
    }

    nm_build_id_t detected_build = identify_build_by_sha256(exe_sha256);
    const char* match_source = (detected_build != NM_BUILD_UNKNOWN) ? "exact-sha256" : "none";

    // Check in-memory 3.29 code signatures from address table (handles key-patched and .nmimp builds).
    const nm_build_id_t probe_build = (detected_build != NM_BUILD_UNKNOWN) ? detected_build : NM_BUILD_3_29_3D_328228;
    const EmbeddedSymbol* pass_a_sym      = find_symbol(probe_build, "frame.state_stack_pass_a");
    const EmbeddedSymbol* pass_a_hook_sym = find_symbol(probe_build, "frame.state_stack_pass_a_hook");
    const EmbeddedSymbol* app_global_sym  = find_symbol(probe_build, "network.app_global_ref");

    auto check_sym = [&](const EmbeddedSymbol* sym) -> bool {
        if (!sym || sym->rva == 0 || !sym->expected_bytes_hex) return false;
        const size_t sig_bytes = std::strlen(sym->expected_bytes_hex) / 2u;
        if (sig_bytes == 0 || sym->rva + sig_bytes > image_size) return false;
        const uint64_t addr = base + sym->rva;
        if (!is_memory_readable(addr, sig_bytes)) return false;
        return verify_memory_bytes(addr, sym->expected_bytes_hex) == NM_OK;
    };

    const bool pass_a_ok      = check_sym(pass_a_sym);
    const bool pass_a_hook_ok = check_sym(pass_a_hook_sym);
    const bool app_global_ok  = check_sym(app_global_sym);

    if (pass_a_sym && pass_a_hook_sym && app_global_sym) {
        logger_logf("INFO", "3.29 signature check: pass_a(0x%llX)=%s pass_a_hook(0x%llX)=%s app_global_ref(0x%llX)=%s",
                    static_cast<unsigned long long>(pass_a_sym->rva),
                    pass_a_ok ? "OK" : "MISMATCH",
                    static_cast<unsigned long long>(pass_a_hook_sym->rva),
                    pass_a_hook_ok ? "OK" : "MISMATCH",
                    static_cast<unsigned long long>(app_global_sym->rva),
                    app_global_ok ? "OK" : "MISMATCH");

        if (detected_build == NM_BUILD_UNKNOWN && pass_a_ok && pass_a_hook_ok && app_global_ok) {
            detected_build = NM_BUILD_3_29_3D_328228;
            match_source = "signature-match-3.29.3d";
        }
    }

    if (detected_build == NM_BUILD_UNKNOWN) {
        logger_logf("INFO", "Host executable is not a recognized 3.29.3c/3.29.3d game client; leaving process stock (manual nm_initialize_for_build available)");
        return 0;
    }

    logger_logf("INFO", "Recognized client build: %s (id=%d, match=%s)",
                build_version_string(detected_build), static_cast<int>(detected_build), match_source);

    const nm_status_t init_st = nm_initialize_for_build(
        detected_build,
        base,
        image_size,
        exe_sha256.empty() ? nullptr : exe_sha256.c_str());
    if (init_st != NM_OK) {
        logger_logf("ERROR", "nm_initialize_for_build failed (%d)", static_cast<int>(init_st));
        return 0;
    }

    // Register startup disk overlay directories (NATIVEMODS_OVERLAY_DIR + Mods/Client/*/{client,files})
    // and immediately arm all live FileSystem, UI-root, Chat, and Ruleset 0x2c9 hooks BEFORE CoreCLR
    // boots so early .datc64/.ot/.ao/.ui reads are intercepted from millisecond 0!
    {
        auto& s = state();
        s.live_hooks.register_startup_disk_overlays(dll_dir, s.channels);
        s.live_hooks.install_live_hooks(detected_build, base, s.hooks, s.allocator, s.channels);
    }

    // Install register-preserving prologue hook at frame.state_stack_pass_a
    // after confirming both pass_a and pass_a_hook match.
    const EmbeddedSymbol* active_pass_a_sym = find_symbol(detected_build, "frame.state_stack_pass_a");
    if (pass_a_ok && pass_a_hook_ok && active_pass_a_sym && active_pass_a_sym->rva != 0) {
        void* stub = build_pass_a_frame_stub();
        if (!stub) {
            logger_logf("ERROR", "Failed to allocate executable frame hook stub page");
        } else {
            void* trampoline = nullptr;
            nm_hook_handle_t handle = NM_HOOK_INVALID;
            const uint64_t pass_a_addr = base + active_pass_a_sym->rva;
            const nm_status_t hook_st = nm_install_inline_hook(
                "frame.state_stack_pass_a",
                stub,
                &trampoline,
                &handle);
            if (hook_st == NM_OK && trampoline != nullptr) {
                g_frame_trampoline.store(trampoline, std::memory_order_release);
                auto& s = state();
                std::lock_guard<std::mutex> lock(s.init_mutex);
                s.frame_hook_handle = handle;
                s.frame_stub_page = stub;
                s.flags |= NM_HOST_FLAG_FRAME_HOOK_ARMED;
                logger_logf("INFO", "Frame hook armed at 0x%llX (RVA 0x%llX; stub=%p tramp=%p handle=%llu)",
                            static_cast<unsigned long long>(pass_a_addr),
                            static_cast<unsigned long long>(active_pass_a_sym->rva),
                            stub,
                            trampoline,
                            static_cast<unsigned long long>(handle));
            } else {
                HookEngine::free_exec_page(stub, 4096);
                logger_logf("ERROR", "Failed to install frame hook at 0x%llX (status=%d)",
                            static_cast<unsigned long long>(pass_a_addr),
                            static_cast<int>(hook_st));
            }
        }
    }

    // Fallback: if StateStack::PassA hasn't fired within 2000ms (e.g. early splash), boot CoreCLR anyway off-thread.
    for (int i = 0; i < 40; ++i) {
        if (g_auto_clr_triggered.load(std::memory_order_acquire)) {
            break;
        }
        Sleep(50);
    }
    if (!g_auto_clr_triggered.load(std::memory_order_acquire)) {
        trigger_auto_coreclr_bootstrap("startup-timer-fallback");
    }

    return 0;
}

} // namespace

BOOL WINAPI DllMain(HINSTANCE hinst, DWORD reason, LPVOID reserved) {
    if (reason == DLL_PROCESS_ATTACH) {
        logger_mark_attach_time();
        DisableThreadLibraryCalls(hinst);
        const DWORD len = GetModuleFileNameW(hinst, g_dll_dir_w, MAX_PATH);
        if (len > 0 && len < MAX_PATH) {
            for (DWORD i = len; i > 0; --i) {
                if (g_dll_dir_w[i - 1] == L'\\' || g_dll_dir_w[i - 1] == L'/') {
                    g_dll_dir_w[i - 1] = L'\0';
                    break;
                }
            }
        }
        // Never run CoreCLR, SHA-256 I/O, or hook installation under the Windows loader lock!
        HANDLE th = CreateThread(nullptr, 0, DeferredInitThread, nullptr, 0, nullptr);
        if (th) CloseHandle(th);
    } else if (reason == DLL_PROCESS_DETACH) {
        logger_logf("INFO", "DLL_PROCESS_DETACH (process_exit=%d, live_frames=%llu, msvc_cxx_passthrough=%llu, coreclr_booted=%d)",
                    reserved != nullptr ? 1 : 0,
                    static_cast<unsigned long long>(g_live_frame_count.load(std::memory_order_relaxed)),
                    static_cast<unsigned long long>(get_msvc_cxx_passthrough_count()),
                    state().clr.is_booted() ? 1 : 0);
        if (reserved == nullptr) {
            nm_shutdown();
        }
        logger_close();
    }
    return TRUE;
}
#endif
