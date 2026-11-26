// Win64 Wine/Windows end-to-end smoke test for NativeMods Script Extender (Spike R1).
// Verifies:
//   1. version.dll proxy loads cleanly and forwards GetFileVersionInfoSizeW to System32\version.dll.
//   2. LocalExile.ClientHost.dll loads cleanly, exports LocalExileClientHost_ImportAnchor & nm_* ABI.
//   3. Win64 MinGW VEH exception wall isolates MSVC C++ (0xE06D7363) & ACCESS_VIOLATION (0xC0000005),
//      while unguarded 0xE06D7363 exceptions pass straight through (EXCEPTION_CONTINUE_SEARCH).
//   4. 15-byte StateStack::PassA prologue inline detour + trampoline + unhook works on Win64.
//   5. Off-game-thread hostfxr.dll -> .NET 10 CoreCLR -> LocalExile.Client.Host.NativeEntryPoint.Initialize
//      boots ClientHostRuntime, loads 3.29.3d.json (122 symbols) & harvest-garden.Client.dll in a
//      collectible ALC, runs 5 frame ticks, verifies diagnostics, and exits 0.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include "nativemods_abi.h"

#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>

namespace {

#define SMOKE_CHECK(cond)                                                                             \
    do {                                                                                              \
        if (!(cond)) {                                                                                \
            std::fprintf(stderr, "[r1_wine_smoke] FAIL at %s:%d: %s\n", __FILE__, __LINE__, #cond);   \
            std::fflush(stderr);                                                                      \
            return 1;                                                                                 \
        }                                                                                             \
    } while (0)

std::string wide_to_utf8(const std::wstring& w) {
    if (w.empty()) return {};
    const int needed = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (needed <= 1) return {};
    std::string out(static_cast<size_t>(needed - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), -1, out.data(), needed, nullptr, nullptr);
    return out;
}

std::wstring get_exe_dir() {
    wchar_t buf[MAX_PATH * 2] = {};
    const DWORD n = GetModuleFileNameW(nullptr, buf, MAX_PATH * 2);
    std::wstring s(buf, n);
    const size_t slash = s.find_last_of(L"\\/");
    if (slash != std::wstring::npos) {
        s.resize(slash);
    }
    return s;
}

// --- VEH passthrough test harness ---
std::atomic<bool> g_passthrough_observed{false};
std::atomic<bool> g_passthrough_armed{false};
uint64_t g_passthrough_resume_rip = 0;
uint64_t g_passthrough_resume_rsp = 0;

LONG CALLBACK PassthroughTestVeh(PEXCEPTION_POINTERS ep) {
    if (!g_passthrough_armed.load(std::memory_order_acquire)) {
        return EXCEPTION_CONTINUE_SEARCH;
    }
    if (ep && ep->ExceptionRecord &&
        ep->ExceptionRecord->ExceptionCode == NM_MSVC_CXX_EXCEPTION_CODE) {
        g_passthrough_observed.store(true, std::memory_order_release);
        g_passthrough_armed.store(false, std::memory_order_release);
        ep->ContextRecord->Rip = g_passthrough_resume_rip;
        ep->ContextRecord->Rsp = g_passthrough_resume_rsp;
        return EXCEPTION_CONTINUE_EXECUTION;
    }
    return EXCEPTION_CONTINUE_SEARCH;
}

extern "C" void smoke_raise_unguarded_msvc_cxx(void) {
    // Save recovery point in assembly so our test VEH can resume after confirming
    // LocalExile.ClientHost.dll's VEH returned EXCEPTION_CONTINUE_SEARCH.
    RaiseException(NM_MSVC_CXX_EXCEPTION_CODE, 0, 0, nullptr);
}

// --- 15-byte StateStack::PassA prologue inline hook test ---
using SynthPassAFn = int64_t(NM_CALL*)(int64_t a, float b, float c);
SynthPassAFn g_pass_a_trampoline = nullptr;
int g_pass_a_detour_hits = 0;

int64_t NM_CALL synth_pass_a_detour(int64_t a, float b, float c) {
    ++g_pass_a_detour_hits;
    return g_pass_a_trampoline(a, b, c) + 1000;
}

} // namespace

int main() {
    const std::wstring dir_w = get_exe_dir();
    const std::string dir_u8 = wide_to_utf8(dir_w);

    const std::wstring log_path_w = dir_w + L"\\r1-wine-smoke.log";
    DeleteFileW(log_path_w.c_str());

    // Configure environment for managed host discovery & logging.
    SetEnvironmentVariableW(L"NATIVEMODS_DISABLE_AUTO_INIT", L"1");
    SetEnvironmentVariableW(L"NATIVEMODS_LOG_PATH", log_path_w.c_str());
    SetEnvironmentVariableW(L"NATIVEMODS_LOG_CONSOLE", L"1");
    SetEnvironmentVariableW(L"NATIVEMODS_ADDRESSES_DIR", (dir_w + L"\\addresses").c_str());
    SetEnvironmentVariableW(L"NATIVEMODS_MODS_DIR", (dir_w + L"\\Mods\\Client").c_str());

    std::printf("[r1_wine_smoke] Staging directory: %s\n", dir_u8.c_str());
    std::fflush(stdout);

#define SMOKE_PROC(mod, name, fn_type) \
    reinterpret_cast<fn_type>(reinterpret_cast<uintptr_t>(GetProcAddress((mod), (name))))

    // 1. Verify version.dll proxy loads cleanly and forwards to System32\version.dll.
    const std::wstring version_path_w = dir_w + L"\\version.dll";
    HMODULE version_mod = LoadLibraryW(version_path_w.c_str());
    SMOKE_CHECK(version_mod != nullptr);
    using GetVerSizeWFn = DWORD(WINAPI*)(LPCWSTR, LPDWORD);
    auto get_ver_size = SMOKE_PROC(version_mod, "GetFileVersionInfoSizeW", GetVerSizeWFn);
    SMOKE_CHECK(get_ver_size != nullptr);
    DWORD ver_handle = 0;
    const DWORD ver_size = get_ver_size(L"C:\\Windows\\System32\\kernel32.dll", &ver_handle);
    std::printf("[r1_wine_smoke] version.dll proxy GetFileVersionInfoSizeW(kernel32.dll) = %lu bytes\n",
                static_cast<unsigned long>(ver_size));
    SMOKE_CHECK(ver_size > 0);

    // 2. Load LocalExile.ClientHost.dll & verify anchor + ABI exports.
    const std::wstring shim_path_w = dir_w + L"\\LocalExile.ClientHost.dll";
    HMODULE shim_mod = LoadLibraryW(shim_path_w.c_str());
    SMOKE_CHECK(shim_mod != nullptr);

    auto import_anchor = SMOKE_PROC(shim_mod, "LocalExileClientHost_ImportAnchor", void(NM_CALL*)());
    SMOKE_CHECK(import_anchor != nullptr);
    import_anchor();

    auto p_get_abi = SMOKE_PROC(shim_mod, "nm_get_abi_table", nm_status_t(NM_CALL*)(const nm_abi_table_t**));
    auto p_init = SMOKE_PROC(shim_mod, "nm_initialize_for_build", nm_status_t(NM_CALL*)(nm_build_id_t, uint64_t, uint32_t, const char*));
    auto p_get_info = SMOKE_PROC(shim_mod, "nm_get_host_info", nm_status_t(NM_CALL*)(nm_host_info_t*));
    auto p_bootstrap = SMOKE_PROC(shim_mod, "nm_bootstrap_managed_host", nm_status_t(NM_CALL*)(const char*, const char*, const char*));
    auto p_shutdown = SMOKE_PROC(shim_mod, "nm_shutdown", nm_status_t(NM_CALL*)());
    SMOKE_CHECK(p_get_abi && p_init && p_get_info && p_bootstrap && p_shutdown);

    const nm_abi_table_t* abi = nullptr;
    SMOKE_CHECK(p_get_abi(&abi) == NM_OK);
    SMOKE_CHECK(abi != nullptr);
    SMOKE_CHECK(abi->struct_size == sizeof(nm_abi_table_t));
    SMOKE_CHECK(abi->abi_version == NM_ABI_VERSION);
    SMOKE_CHECK(abi->register_file_overlay_dir != nullptr);
    SMOKE_CHECK(abi->has_file_overlay != nullptr);
    SMOKE_CHECK(abi->free_file_overlay_buffer != nullptr);
    SMOKE_CHECK(abi->audio_play_ogg != nullptr);
    SMOKE_CHECK(abi->audio_stop != nullptr);
    SMOKE_CHECK(abi->ui_create_window != nullptr);
    SMOKE_CHECK(abi->ui_subscribe_click != nullptr);
    SMOKE_CHECK(abi->set_file_overlay_known_paths != nullptr);
    SMOKE_CHECK(abi->get_active_connection_token != nullptr);
    SMOKE_CHECK(abi->ui_get_active_root != nullptr);
    SMOKE_CHECK(abi->screens_get_state != nullptr);
    SMOKE_CHECK(abi->input_set_callback != nullptr);
    SMOKE_CHECK(abi->hover_get_state != nullptr);
    SMOKE_CHECK(abi->pregame_set_callbacks != nullptr);

    constexpr const char* kSha3293d = "e9ba3d31c4d922e1826ef01d02fd22178a001b29e17264b68fc2446efd41f53d";
    SMOKE_CHECK(p_init(NM_BUILD_3_29_3D_328228, NM_NOMINAL_IMAGE_BASE, 0x4d9a000u, kSha3293d) == NM_OK);

    nm_host_info_t info{};
    SMOKE_CHECK(p_get_info(&info) == NM_OK);
    SMOKE_CHECK(info.build_id == NM_BUILD_3_29_3D_328228);
    SMOKE_CHECK(std::strcmp(info.build_version, "3.29.3d") == 0);
    SMOKE_CHECK(info.symbol_count >= 52);

    nm_symbol_info_t frame_sym{};
    SMOKE_CHECK(abi->lookup_symbol("frame.state_stack_pass_a_hook", &frame_sym) == NM_OK);
    SMOKE_CHECK(frame_sym.rva == 0x1528fbfull);
    std::printf("[r1_wine_smoke] ABI verified: version=0x%08X build=%s symbols=%u frame_hook_rva=0x%llX\n",
                abi->abi_version, info.build_version, info.symbol_count,
                static_cast<unsigned long long>(frame_sym.rva));

    // 3. Test Win64 MinGW VEH exception wall (happy path, 0xE06D7363, ACCESS_VIOLATION, unguarded passthrough).
    int64_t guarded_res = 0;
    nm_exception_info_t ex{};
    auto ok_fn = [](void* ctx) -> int64_t { return *static_cast<int64_t*>(ctx) * 6; };
    int64_t seven = 7;
    SMOKE_CHECK(abi->call_guarded(ok_fn, &seven, &guarded_res, &ex) == NM_OK);
    SMOKE_CHECK(guarded_res == 42);

    auto msvc_throw_fn = [](void*) -> int64_t {
        RaiseException(NM_MSVC_CXX_EXCEPTION_CODE, 0, 0, nullptr);
        return 0;
    };
    SMOKE_CHECK(abi->call_guarded(msvc_throw_fn, nullptr, &guarded_res, &ex) == NM_ERR_GAME_EXCEPTION);
    SMOKE_CHECK(ex.exception_code == NM_MSVC_CXX_EXCEPTION_CODE);
    std::printf("[r1_wine_smoke] VEH wall caught 0x%08X: %s\n", ex.exception_code, ex.message);

    auto av_fn = [](void*) -> int64_t {
        volatile int* null_ptr = nullptr;
        *null_ptr = 123;
        return 0;
    };
    SMOKE_CHECK(abi->call_guarded(av_fn, nullptr, &guarded_res, &ex) == NM_ERR_GAME_EXCEPTION);
    SMOKE_CHECK(ex.exception_code == EXCEPTION_ACCESS_VIOLATION);
    std::printf("[r1_wine_smoke] VEH wall caught AV 0x%08X: %s\n", ex.exception_code, ex.message);

    // Verify unguarded 0xE06D7363 passes straight through shim's VEH to our downstream handler.
    PVOID test_veh = AddVectoredExceptionHandler(0, &PassthroughTestVeh);
    SMOKE_CHECK(test_veh != nullptr);
    g_passthrough_observed.store(false, std::memory_order_release);
    g_passthrough_armed.store(true, std::memory_order_release);
#if defined(__GNUC__) && defined(__x86_64__)
    __asm__ volatile (
        "leaq 1f(%%rip), %%rax\n"
        "movq %%rax, %0\n"
        "movq %%rsp, %1\n"
        "subq $32, %%rsp\n"
        "call smoke_raise_unguarded_msvc_cxx\n"
        "addq $32, %%rsp\n"
        "1:\n"
        : "=m"(g_passthrough_resume_rip), "=m"(g_passthrough_resume_rsp)
        :
        : "rax", "rcx", "rdx", "r8", "r9", "r10", "r11", "memory", "cc"
    );
#endif
    RemoveVectoredExceptionHandler(test_veh);
    SMOKE_CHECK(g_passthrough_observed.load(std::memory_order_acquire));
    std::printf("[r1_wine_smoke] Unguarded 0xE06D7363 passthrough verified cleanly!\n");

    // 4. Test 15-byte StateStack::PassA prologue inline hook under Wine.
    void* synth_page = VirtualAlloc(nullptr, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    SMOKE_CHECK(synth_page != nullptr);
    auto* code = static_cast<uint8_t*>(synth_page);
    // Exact 15-byte StateStack::PassA prologue + "lea rax, [rcx + 5]; ret"
    const uint8_t synth_fn_bytes[] = {
        0x48, 0x89, 0x5C, 0x24, 0x10, // mov [rsp+0x10], rbx (5B)
        0x48, 0x89, 0x6C, 0x24, 0x18, // mov [rsp+0x18], rbp (5B)
        0x48, 0x89, 0x74, 0x24, 0x20, // mov [rsp+0x20], rsi (5B) -> 15B total!
        0x48, 0x8D, 0x41, 0x05,       // lea rax, [rcx + 5]
        0xC3,                         // ret
    };
    std::memcpy(code, synth_fn_bytes, sizeof(synth_fn_bytes));
    FlushInstructionCache(GetCurrentProcess(), code, sizeof(synth_fn_bytes));

    auto synth_fn = reinterpret_cast<SynthPassAFn>(synth_page);
    SMOKE_CHECK(synth_fn(10, 1.0f, 2.0f) == 15);

    void* tramp = nullptr;
    nm_hook_handle_t hook_handle = NM_HOOK_INVALID;
    SMOKE_CHECK(abi->install_inline_hook_at(
                    reinterpret_cast<uint64_t>(synth_page),
                    "48895c241048896c24184889742420",
                    reinterpret_cast<void*>(&synth_pass_a_detour),
                    &tramp,
                    &hook_handle) == NM_OK);
    SMOKE_CHECK(tramp != nullptr && hook_handle != NM_HOOK_INVALID);
    g_pass_a_trampoline = reinterpret_cast<SynthPassAFn>(tramp);
    g_pass_a_detour_hits = 0;
    SMOKE_CHECK(synth_fn(10, 1.0f, 2.0f) == 1015);
    SMOKE_CHECK(g_pass_a_detour_hits == 1);
    SMOKE_CHECK(abi->remove_hook(hook_handle) == NM_OK);
    SMOKE_CHECK(synth_fn(10, 1.0f, 2.0f) == 15);
    VirtualFree(synth_page, 0, MEM_RELEASE);
    std::printf("[r1_wine_smoke] 15-byte StateStack::PassA prologue inline hook + trampoline + unhook verified!\n");

    // 5. Trigger off-game-thread .NET 10 CoreCLR bootstrap via hostfxr.dll.
    const std::string hostfxr_path = dir_u8 + "\\hostfxr.dll";
    const std::string config_path  = dir_u8 + "\\LocalExile.Client.Host.runtimeconfig.json";
    const std::string asm_path     = dir_u8 + "\\LocalExile.Client.Host.dll";

    LARGE_INTEGER qpc_freq{}, t0{}, t1{};
    QueryPerformanceFrequency(&qpc_freq);
    QueryPerformanceCounter(&t0);

    SMOKE_CHECK(p_bootstrap(hostfxr_path.c_str(), config_path.c_str(), asm_path.c_str()) == NM_OK);

    bool booted = false;
    for (int i = 0; i < 600; ++i) { // up to 30 seconds
        SMOKE_CHECK(p_get_info(&info) == NM_OK);
        if ((info.flags & NM_HOST_FLAG_CORECLR_BOOTED) != 0) {
            booted = true;
            break;
        }
        if ((info.flags & NM_HOST_FLAG_CORECLR_BOOTING) == 0 && i > 2) {
            break;
        }
        Sleep(50);
    }
    QueryPerformanceCounter(&t1);
    const double boot_ms = static_cast<double>(t1.QuadPart - t0.QuadPart) * 1000.0 /
                           static_cast<double>(qpc_freq.QuadPart);
    std::printf("[r1_wine_smoke] CoreCLR bootstrap finished: booted=%d elapsed=%.2fms flags=0x%X\n",
                booted ? 1 : 0, boot_ms, info.flags);
    std::fflush(stdout);
    SMOKE_CHECK(booted);

    // 6. Run 5 frame ticks through native FrameQueue -> NativeShimBridge -> ClientHostRuntime -> GardenClientMod!
    int native_frame_ticks = 0;
    uint64_t frame_token = 0;
    auto on_native_frame = [](float, void* ud) {
        ++(*static_cast<int*>(ud));
    };
    SMOKE_CHECK(abi->register_frame_callback(on_native_frame, &native_frame_ticks, &frame_token) == NM_OK);
    for (int f = 0; f < 5; ++f) {
        SMOKE_CHECK(abi->drain_main_thread_queue(1.0f / 60.0f, 64) == NM_OK);
    }
    SMOKE_CHECK(native_frame_ticks == 5);
    SMOKE_CHECK(abi->unregister_frame_callback(frame_token) == NM_OK);

    // 6b. Verify live file overlay dir + looping audio C ABI calls under Wine!
    uint64_t ov_dir_tok = 0;
    const std::string garden_files_dir = dir_u8 + "\\Mods\\Client\\harvest-garden\\client";
    SMOKE_CHECK(abi->register_file_overlay_dir(garden_files_dir.c_str(), &ov_dir_tok) == NM_OK);
    size_t ui_size = 0;
    SMOKE_CHECK(abi->has_file_overlay("Metadata/UI/Mods/Garden/Stockpile.ui", &ui_size) == NM_OK);
    SMOKE_CHECK(ui_size > 0);
    uint8_t* ui_buf = nullptr;
    size_t ui_read_sz = 0;
    SMOKE_CHECK(abi->resolve_file_overlay("Metadata\\UI\\Mods\\Garden\\Stockpile.ui", &ui_buf, &ui_read_sz) == NM_OK);
    SMOKE_CHECK(ui_buf != nullptr && ui_read_sz == ui_size);
    SMOKE_CHECK(abi->free_file_overlay_buffer(ui_buf) == NM_OK);
    SMOKE_CHECK(abi->unregister_file_overlay_dir(ov_dir_tok) == NM_OK);

    int32_t audio_handle = 0;
    int32_t audio_playing = 0;
    SMOKE_CHECK(abi->audio_play_ogg("Audio/Mods/Maxwell/stock_market.ogg", 0.5f, 1, &audio_handle) == NM_OK);
    SMOKE_CHECK(audio_handle > 0);
    SMOKE_CHECK(abi->audio_is_playing(audio_handle, &audio_playing) == NM_OK);
    SMOKE_CHECK(audio_playing == 1);
    SMOKE_CHECK(abi->audio_set_volume(audio_handle, 0.25f) == NM_OK);
    SMOKE_CHECK(abi->audio_stop(audio_handle) == NM_OK);
    uint64_t conn_tok = 0xDEADBEEFull;
    SMOKE_CHECK(abi->get_active_connection_token(&conn_tok) == NM_OK);
    SMOKE_CHECK(conn_tok == 0ull);
    std::printf("[r1_wine_smoke] Live file overlay dir (%zu bytes), connection token API & looping audio C ABI verified!\n", ui_size);

    // 7. Verify shared diagnostic log confirms CoreCLR + JsonAddressLibrary (122 symbols) +
    //    ClientModLoader (harvest-garden.Client.dll in collectible ALC) + 5 managed frame ticks!
    std::string log_contents;
    {
        HANDLE lh = CreateFileW(log_path_w.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        SMOKE_CHECK(lh != INVALID_HANDLE_VALUE);
        char chunk[4096];
        DWORD nread = 0;
        while (ReadFile(lh, chunk, sizeof(chunk), &nread, nullptr) && nread > 0) {
            log_contents.append(chunk, nread);
        }
        CloseHandle(lh);
    }
    SMOKE_CHECK(log_contents.find("NativeEntryPoint.Initialize(nm_abi_table_t*) returned 0") != std::string::npos);
    SMOKE_CHECK(log_contents.find("Loaded JsonAddressLibrary") != std::string::npos);
    SMOKE_CHECK(log_contents.find("symbols=240") != std::string::npos);
    SMOKE_CHECK(log_contents.find("Loaded client mod 'harvest-garden'") != std::string::npos);
    SMOKE_CHECK(log_contents.find("collectibleAlc=True") != std::string::npos);
    SMOKE_CHECK(log_contents.find("Managed frame tick #1") != std::string::npos);
    SMOKE_CHECK(log_contents.find("Managed frame tick #5") != std::string::npos);

    // 8. Clean native shutdown.
    SMOKE_CHECK(p_shutdown() == NM_OK);

    std::printf("[r1_wine_smoke] PASS: Win64 DLL -> hostfxr -> .NET 10 CoreCLR -> NativeEntryPoint -> harvest-garden.Client.dll -> 5 ticks OK!\n");
    std::fflush(stdout);
    return 0;
}
