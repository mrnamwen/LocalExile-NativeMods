#include "exception_wall.hpp"

#include <cstdio>
#include <cstring>

#if defined(_WIN32)
#  define WIN32_LEAN_AND_MEAN
#  include <windows.h>
#endif

namespace nativemods {

namespace {

void fill_exception(nm_exception_info_t* out, uint32_t code, uint64_t addr, const char* msg) {
    if (!out) return;
    std::memset(out, 0, sizeof(*out));
    out->exception_code = code;
    out->exception_address = addr;
    if (msg) {
        std::snprintf(out->message, sizeof(out->message), "%s", msg);
    }
}

#if defined(_WIN32) && defined(_MSC_VER)
nm_status_t invoke_seh_wall(
    nm_guarded_fn_t      fn,
    void*                context,
    int64_t*             out_result,
    nm_exception_info_t* out_exception) {
    DWORD captured_code = 0;
    uint64_t captured_addr = 0;
    __try {
        const int64_t res = fn(context);
        if (out_result) {
            *out_result = res;
        }
        return NM_OK;
    } __except (
        captured_code = GetExceptionCode(),
        captured_addr = reinterpret_cast<uint64_t>(GetExceptionInformation()->ExceptionRecord->ExceptionAddress),
        EXCEPTION_EXECUTE_HANDLER) {
        char buf[128];
        if (captured_code == NM_MSVC_CXX_EXCEPTION_CODE) {
            std::snprintf(buf, sizeof(buf), "Caught game MSVC C++ exception (0x%08lX)", static_cast<unsigned long>(captured_code));
        } else {
            std::snprintf(buf, sizeof(buf), "Caught Win32 SEH exception (0x%08lX)", static_cast<unsigned long>(captured_code));
        }
        fill_exception(out_exception, static_cast<uint32_t>(captured_code), captured_addr, buf);
        return NM_ERR_GAME_EXCEPTION;
    }
}
#endif

} // namespace

nm_status_t invoke_guarded(
    nm_guarded_fn_t      fn,
    void*                context,
    int64_t*             out_result,
    nm_exception_info_t* out_exception) {
    if (fn == nullptr) {
        return NM_ERR_INVALID_ARG;
    }
    if (out_exception) {
        std::memset(out_exception, 0, sizeof(*out_exception));
    }

#if defined(_WIN32) && defined(_MSC_VER)
    return invoke_seh_wall(fn, context, out_result, out_exception);
#else
    try {
        const int64_t res = fn(context);
        if (out_result) {
            *out_result = res;
        }
        return NM_OK;
    } catch (const SimulatedMsvcException& ex) {
        fill_exception(out_exception, ex.code(), 0, ex.what());
        return NM_ERR_GAME_EXCEPTION;
    } catch (const std::exception& ex) {
        fill_exception(out_exception, NM_MSVC_CXX_EXCEPTION_CODE, 0, ex.what());
        return NM_ERR_GAME_EXCEPTION;
    } catch (...) {
        fill_exception(out_exception, NM_MSVC_CXX_EXCEPTION_CODE, 0, "Caught unknown C++ exception across native boundary");
        return NM_ERR_GAME_EXCEPTION;
    }
#endif
}

} // namespace nativemods
