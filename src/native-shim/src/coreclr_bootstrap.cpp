#include "coreclr_bootstrap.hpp"

#include <cstring>

#if defined(_WIN32)
#  define WIN32_LEAN_AND_MEAN
#  include <windows.h>
#  include <string>
#  include <thread>
#endif

namespace nativemods {

#if defined(_WIN32)
namespace {

using hostfxr_handle = void*;
enum hostfxr_delegate_type : int32_t {
    hdt_load_assembly_and_get_function_pointer = 5,
};

using hostfxr_initialize_for_runtime_config_fn = int32_t(__cdecl*)(
    const wchar_t* runtime_config_path,
    const void*    parameters,
    hostfxr_handle* host_context_handle);

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

using managed_entry_fn = int32_t(__stdcall*)(const nm_abi_table_t* abi_table);

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

    const std::wstring w_hostfxr = utf8_to_wide(hostfxr_path_utf8);
    const std::wstring w_config  = utf8_to_wide(runtimeconfig_path_utf8);
    const std::wstring w_asm     = utf8_to_wide(host_assembly_path_utf8);

    // Boot CoreCLR on a dedicated worker thread so the game thread never stalls
    // inside hostfxr / coreclr initialization (DeadlockDetector @ 0x140e3ed10).
    std::thread([this, w_hostfxr, w_config, w_asm, abi_table]() {
        HMODULE fxr = LoadLibraryW(w_hostfxr.c_str());
        if (!fxr) {
            booting_.store(false, std::memory_order_release);
            return;
        }

        auto init_fn = reinterpret_cast<hostfxr_initialize_for_runtime_config_fn>(
            GetProcAddress(fxr, "hostfxr_initialize_for_runtime_config"));
        auto get_delegate_fn = reinterpret_cast<hostfxr_get_runtime_delegate_fn>(
            GetProcAddress(fxr, "hostfxr_get_runtime_delegate"));
        auto close_fn = reinterpret_cast<hostfxr_close_fn>(
            GetProcAddress(fxr, "hostfxr_close"));

        if (!init_fn || !get_delegate_fn || !close_fn) {
            booting_.store(false, std::memory_order_release);
            return;
        }

        hostfxr_handle ctx = nullptr;
        const int32_t rc = init_fn(w_config.c_str(), nullptr, &ctx);
        if (rc < 0 || !ctx) {
            booting_.store(false, std::memory_order_release);
            return;
        }

        void* load_asm_raw = nullptr;
        const int32_t dg_rc = get_delegate_fn(ctx, hdt_load_assembly_and_get_function_pointer, &load_asm_raw);
        close_fn(ctx);
        if (dg_rc != 0 || !load_asm_raw) {
            booting_.store(false, std::memory_order_release);
            return;
        }

        auto load_asm = reinterpret_cast<load_assembly_and_get_function_pointer_fn>(load_asm_raw);
        void* entry_raw = nullptr;
        const int32_t ep_rc = load_asm(
            w_asm.c_str(),
            L"LocalExile.Client.Host.NativeEntryPoint, LocalExile.Client.Host",
            L"Initialize",
            kUnmanagedCallersOnly,
            nullptr,
            &entry_raw);

        if (ep_rc != 0 || !entry_raw) {
            booting_.store(false, std::memory_order_release);
            return;
        }

        auto entry = reinterpret_cast<managed_entry_fn>(entry_raw);
        if (entry(abi_table) == 0) {
            booted_.store(true, std::memory_order_release);
        }
        booting_.store(false, std::memory_order_release);
    }).detach();

    return NM_OK;
#endif
}

} // namespace nativemods
