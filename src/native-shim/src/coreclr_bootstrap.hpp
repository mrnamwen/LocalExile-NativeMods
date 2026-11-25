#pragma once

#include "nativemods_abi.h"

#include <atomic>
#include <string>

namespace nativemods {

// Deferred .NET 10 hostfxr/CoreCLR bootstrapper.
// Designed to boot off the game thread (triggered on the first frame / timer fallback)
// so Path of Exile's DeadlockDetector (0x140e3ed10) is never tripped during CLR startup.
class CoreClrBootstrapper {
public:
    nm_status_t bootstrap_async_or_sync(
        const char*           hostfxr_path_utf8,
        const char*           runtimeconfig_path_utf8,
        const char*           host_assembly_path_utf8,
        const nm_abi_table_t* abi_table);

    bool is_booting() const noexcept { return booting_.load(std::memory_order_acquire); }
    bool is_booted() const noexcept { return booted_.load(std::memory_order_acquire); }
    void reset() noexcept {
        booting_.store(false, std::memory_order_release);
        booted_.store(false, std::memory_order_release);
    }

private:
    std::atomic<bool> booting_{false};
    std::atomic<bool> booted_{false};
};

} // namespace nativemods
