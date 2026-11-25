#include "coreclr_bootstrap.hpp"
#include "shim_logger.hpp"

#include <cstring>
#include <memory>

#if defined(_WIN32)
#  define WIN32_LEAN_AND_MEAN
#  include <windows.h>
#  include <string>
#endif

namespace nativemods {

#if defined(_WIN32)
namespace {

using hostfxr_handle = void*;
enum hostfxr_delegate_type : int32_t {
    hdt_load_assembly_and_get_function_pointer = 5,
};

struct hostfxr_initialize_parameters {
    size_t         size;
    const wchar_t* host_path;
    const wchar_t* dotnet_root;
};

using hostfxr_initialize_for_runtime_config_fn = int32_t(__cdecl*)(
    const wchar_t*                       runtime_config_path,
    const hostfxr_initialize_parameters* parameters,
    hostfxr_handle*                      host_context_handle);

using hostfxr_initialize_for_dotnet_command_line_fn = int32_t(__cdecl*)(
    int                                  argc,
    const wchar_t**                      argv,
    const hostfxr_initialize_parameters* parameters,
    hostfxr_handle*                      host_context_handle);

using hostfxr_get_runtime_delegate_fn = int32_t(__cdecl*)(
    hostfxr_handle        host_context_handle,
    hostfxr_delegate_type type,
    void**                delegate);

using hostfxr_close_fn = int32_t(__cdecl*)(hostfxr_handle host_context_handle);

using load_assembly_and_get_function_pointer_fn = int32_t(__stdcall*)(
    const wchar_t* assembly_path,
    const wchar_t* type_name,
    const wchar_t* method_name,
    const wchar_t* delegate_type_name,
    void*          reserved,
    void**         delegate);

using managed_entry_fn = int32_t(__cdecl*)(const nm_abi_table_t* abi_table);

// UNMANAGEDCALLERSONLY_METHOD sentinel from nethost/coreclr_delegates.h
inline const wchar_t* kUnmanagedCallersOnly = reinterpret_cast<const wchar_t*>(-1);

std::wstring utf8_to_wide(const char* utf8) {
    if (!utf8 || utf8[0] == '\0') return {};
    const int needed = MultiByteToWideChar(CP_UTF8, 0, utf8, -1, nullptr, 0);
    if (needed <= 1) return {};
    std::wstring out(static_cast<size_t>(needed - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, utf8, -1, out.data(), needed);
    return out;
}

struct BootstrapWorkerArgs {
    CoreClrBootstrapper*  self;
    std::atomic<bool>*    booting_flag;
    std::atomic<bool>*    booted_flag;
    std::string           hostfxr_utf8;
    std::string           config_utf8;
    std::string           asm_utf8;
    const nm_abi_table_t* abi_table;
};

DWORD WINAPI CoreClrBootstrapThreadProc(LPVOID param) {
    std::unique_ptr<BootstrapWorkerArgs> args(static_cast<BootstrapWorkerArgs*>(param));
    const double t_start = logger_elapsed_ms();
    const DWORD tid = GetCurrentThreadId();

    logger_logf("INFO", "CoreCLR worker thread started (tid=%lu): hostfxr='%s', config='%s', assembly='%s'",
                static_cast<unsigned long>(tid),
                args->hostfxr_utf8.c_str(),
                args->config_utf8.c_str(),
                args->asm_utf8.c_str());

    const std::wstring w_hostfxr = utf8_to_wide(args->hostfxr_utf8.c_str());
    const std::wstring w_config  = utf8_to_wide(args->config_utf8.c_str());
    const std::wstring w_asm     = utf8_to_wide(args->asm_utf8.c_str());

    HMODULE fxr = LoadLibraryW(w_hostfxr.c_str());
    if (!fxr) {
        logger_logf("ERROR", "LoadLibraryW(hostfxr.dll) failed (GetLastError=%lu)",
                    static_cast<unsigned long>(GetLastError()));
        args->booting_flag->store(false, std::memory_order_release);
        return 1;
    }
    logger_logf("INFO", "Loaded hostfxr.dll at 0x%p (+%.2fms)",
                reinterpret_cast<void*>(fxr), logger_elapsed_ms() - t_start);

    auto init_fn = reinterpret_cast<hostfxr_initialize_for_runtime_config_fn>(
        reinterpret_cast<uintptr_t>(GetProcAddress(fxr, "hostfxr_initialize_for_runtime_config")));
    auto init_cli_fn = reinterpret_cast<hostfxr_initialize_for_dotnet_command_line_fn>(
        reinterpret_cast<uintptr_t>(GetProcAddress(fxr, "hostfxr_initialize_for_dotnet_command_line")));
    auto get_delegate_fn = reinterpret_cast<hostfxr_get_runtime_delegate_fn>(
        reinterpret_cast<uintptr_t>(GetProcAddress(fxr, "hostfxr_get_runtime_delegate")));
    auto close_fn = reinterpret_cast<hostfxr_close_fn>(
        reinterpret_cast<uintptr_t>(GetProcAddress(fxr, "hostfxr_close")));

    if (!init_fn || !get_delegate_fn || !close_fn) {
        logger_logf("ERROR", "Missing required hostfxr exports (init=%p get_delegate=%p close=%p)",
                    reinterpret_cast<void*>(init_fn),
                    reinterpret_cast<void*>(get_delegate_fn),
                    reinterpret_cast<void*>(close_fn));
        args->booting_flag->store(false, std::memory_order_release);
        return 2;
    }

    // Derive dotnet_root from hostfxr.dll's directory so self-contained flat bundles resolve locally.
    std::wstring w_dotnet_root = w_hostfxr;
    const size_t slash = w_dotnet_root.find_last_of(L"\\/");
    if (slash != std::wstring::npos) {
        w_dotnet_root.resize(slash);
    }
    hostfxr_initialize_parameters init_params{};
    init_params.size = sizeof(init_params);
    init_params.host_path = w_asm.c_str();
    init_params.dotnet_root = w_dotnet_root.empty() ? nullptr : w_dotnet_root.c_str();

    // Detect whether runtimeconfig.json declares a self-contained flat bundle ("includedFrameworks")
    // vs a framework-dependent component ("framework"). Self-contained bundles use
    // hostfxr_initialize_for_dotnet_command_line cleanly without emitting 0x80008093 stderr noise.
    bool is_self_contained = false;
    {
        HANDLE cfg_h = CreateFileW(w_config.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                   nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (cfg_h != INVALID_HANDLE_VALUE) {
            char cfg_buf[4096] = {};
            DWORD cfg_read = 0;
            if (ReadFile(cfg_h, cfg_buf, sizeof(cfg_buf) - 1, &cfg_read, nullptr) && cfg_read > 0) {
                cfg_buf[cfg_read] = '\0';
                is_self_contained = (std::strstr(cfg_buf, "\"includedFrameworks\"") != nullptr);
            }
            CloseHandle(cfg_h);
        }
    }

    hostfxr_handle ctx = nullptr;
    int32_t rc = -1;
    if (is_self_contained && init_cli_fn != nullptr) {
        const double t_cli = logger_elapsed_ms();
        const wchar_t* cli_argv[1] = { w_asm.c_str() };
        rc = init_cli_fn(1, cli_argv, &init_params, &ctx);
        logger_logf("INFO", "hostfxr_initialize_for_dotnet_command_line(self-contained) returned 0x%08X (ctx=%p, %.2fms)",
                    static_cast<uint32_t>(rc), ctx, logger_elapsed_ms() - t_cli);
    } else {
        const double t_init = logger_elapsed_ms();
        rc = init_fn(w_config.c_str(), &init_params, &ctx);
        logger_logf("INFO", "hostfxr_initialize_for_runtime_config returned 0x%08X (ctx=%p, %.2fms)",
                    static_cast<uint32_t>(rc), ctx, logger_elapsed_ms() - t_init);
        if ((rc == static_cast<int32_t>(0x80008093u) || rc < 0 || !ctx) && init_cli_fn != nullptr) {
            const double t_cli = logger_elapsed_ms();
            const wchar_t* cli_argv[1] = { w_asm.c_str() };
            rc = init_cli_fn(1, cli_argv, &init_params, &ctx);
            logger_logf("INFO", "hostfxr_initialize_for_dotnet_command_line(fallback) returned 0x%08X (ctx=%p, %.2fms)",
                        static_cast<uint32_t>(rc), ctx, logger_elapsed_ms() - t_cli);
        }
    }

    if (rc < 0 || !ctx) {
        logger_logf("ERROR", "hostfxr initialization failed with 0x%08X", static_cast<uint32_t>(rc));
        args->booting_flag->store(false, std::memory_order_release);
        return 3;
    }

    const double t_dg = logger_elapsed_ms();
    void* load_asm_raw = nullptr;
    const int32_t dg_rc = get_delegate_fn(ctx, hdt_load_assembly_and_get_function_pointer, &load_asm_raw);
    close_fn(ctx);
    logger_logf("INFO", "hostfxr_get_runtime_delegate(hdt_load_assembly_and_get_function_pointer) returned 0x%08X (fn=%p, %.2fms)",
                static_cast<uint32_t>(dg_rc), load_asm_raw, logger_elapsed_ms() - t_dg);
    if (dg_rc != 0 || !load_asm_raw) {
        logger_logf("ERROR", "hostfxr_get_runtime_delegate failed with 0x%08X", static_cast<uint32_t>(dg_rc));
        args->booting_flag->store(false, std::memory_order_release);
        return 4;
    }

    const double t_load = logger_elapsed_ms();
    auto load_asm = reinterpret_cast<load_assembly_and_get_function_pointer_fn>(load_asm_raw);
    void* entry_raw = nullptr;
    const int32_t ep_rc = load_asm(
        w_asm.c_str(),
        L"LocalExile.Client.Host.NativeEntryPoint, LocalExile.Client.Host",
        L"Initialize",
        kUnmanagedCallersOnly,
        nullptr,
        &entry_raw);

    logger_logf("INFO", "load_assembly_and_get_function_pointer(NativeEntryPoint.Initialize) returned 0x%08X (entry=%p, %.2fms)",
                static_cast<uint32_t>(ep_rc), entry_raw, logger_elapsed_ms() - t_load);
    if (ep_rc != 0 || !entry_raw) {
        logger_logf("ERROR", "Failed to resolve NativeEntryPoint.Initialize (0x%08X)", static_cast<uint32_t>(ep_rc));
        args->booting_flag->store(false, std::memory_order_release);
        return 5;
    }

    const double t_entry = logger_elapsed_ms();
    auto entry = reinterpret_cast<managed_entry_fn>(entry_raw);
    const int32_t init_status = entry(args->abi_table);
    const double total_ms = logger_elapsed_ms() - t_start;
    logger_logf(init_status == 0 ? "INFO" : "ERROR",
                "NativeEntryPoint.Initialize(nm_abi_table_t*) returned %d (entry=%.2fms, total CoreCLR boot latency=%.2fms)",
                init_status, logger_elapsed_ms() - t_entry, total_ms);

    if (init_status == 0) {
        args->booted_flag->store(true, std::memory_order_release);
    }
    args->booting_flag->store(false, std::memory_order_release);
    return 0;
}

} // namespace
#endif

nm_status_t CoreClrBootstrapper::bootstrap_async_or_sync(
    const char*           hostfxr_path_utf8,
    const char*           runtimeconfig_path_utf8,
    const char*           host_assembly_path_utf8,
    const nm_abi_table_t* abi_table) {
    if (!hostfxr_path_utf8 || !runtimeconfig_path_utf8 || !host_assembly_path_utf8 || !abi_table) {
        return NM_ERR_INVALID_ARG;
    }
    if (hostfxr_path_utf8[0] == '\0' || runtimeconfig_path_utf8[0] == '\0' || host_assembly_path_utf8[0] == '\0') {
        return NM_ERR_INVALID_ARG;
    }
    if (booted_.load(std::memory_order_acquire)) {
        return NM_OK;
    }

#if !defined(_WIN32)
    (void)abi_table;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#else
    bool expected = false;
    if (!booting_.compare_exchange_strong(expected, true, std::memory_order_acq_rel)) {
        return NM_OK; // Boot already in flight on worker thread.
    }

    auto* worker_args = new BootstrapWorkerArgs{
        this,
        &booting_,
        &booted_,
        hostfxr_path_utf8,
        runtimeconfig_path_utf8,
        host_assembly_path_utf8,
        abi_table,
    };

    // Boot CoreCLR on a dedicated native Win32 worker thread so the game thread never stalls
    // inside hostfxr / coreclr initialization (DeadlockDetector @ 0x140e3ed10).
    HANDLE th = CreateThread(nullptr, 0, CoreClrBootstrapThreadProc, worker_args, 0, nullptr);
    if (!th) {
        logger_logf("ERROR", "CreateThread for CoreCLR bootstrap failed (GetLastError=%lu)",
                    static_cast<unsigned long>(GetLastError()));
        delete worker_args;
        booting_.store(false, std::memory_order_release);
        return NM_ERR_RUNTIME_BOOT_FAILED;
    }
    CloseHandle(th);
    return NM_OK;
#endif
}

} // namespace nativemods
