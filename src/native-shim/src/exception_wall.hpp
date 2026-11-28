#pragma once

#include "nativemods_abi.h"

#include <cstdint>
#include <exception>
#include <string>

namespace nativemods {

// Simulated MSVC C++ exception (0xE06D7363) used in Linux host-side unit tests
// to prove the exception wall isolates game throws identically to Windows SEH.
class SimulatedMsvcException : public std::exception {
public:
    explicit SimulatedMsvcException(std::string msg, uint32_t code = NM_MSVC_CXX_EXCEPTION_CODE)
        : code_(code), message_(std::move(msg)) {}

    uint32_t    code() const noexcept { return code_; }
    const char* what() const noexcept override { return message_.c_str(); }

private:
    uint32_t    code_;
    std::string message_;
};

// Ensures the Win64 VEH exception wall is registered (safe to call outside DllMain; idempotent).
void ensure_exception_wall_installed() noexcept;

// Returns count of unguarded first-chance MSVC C++ exceptions (0xE06D7363) observed and
// passed straight through (EXCEPTION_CONTINUE_SEARCH) to the game / CoreCLR.
uint64_t get_msvc_cxx_passthrough_count() noexcept;
// Faults inside guarded calls left to .NET because they happened in managed or runtime code.
uint64_t get_managed_fault_passthrough_count() noexcept;

nm_status_t invoke_guarded(
    nm_guarded_fn_t      fn,
    void*                context,
    int64_t*             out_result,
    nm_exception_info_t* out_exception);

nm_status_t invoke_native_guarded(
    nm_native_call_desc_t* desc,
    nm_exception_info_t*   out_exception);

} // namespace nativemods
