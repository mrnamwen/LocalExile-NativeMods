#include "exception_wall.hpp"

#include <atomic>
#include <cstdio>
#include <cstring>

#if defined(_WIN32)
#  define WIN32_LEAN_AND_MEAN
#  include <windows.h>
#  ifdef exception_code
#    undef exception_code
#  endif
#endif

#if defined(_WIN32) && !defined(_MSC_VER) && defined(__x86_64__)
extern "C" {

struct alignas(16) NmWin64GuardRegs {
    uint64_t rbx;          // 0x00
    uint64_t rbp;          // 0x08
    uint64_t rsi;          // 0x10
    uint64_t rdi;          // 0x18
    uint64_t r12;          // 0x20
    uint64_t r13;          // 0x28
    uint64_t r14;          // 0x30
    uint64_t r15;          // 0x38
    uint64_t rsp;          // 0x40
    uint64_t rip;          // 0x48
    uint8_t  xmm[10 * 16]; // 0x50..0xEF (xmm6..xmm15)
};

using NmWin64GuardCallback = int32_t (*)(void* ctx);

int32_t nm_win64_veh_call(NmWin64GuardRegs* regs, NmWin64GuardCallback cb, void* ctx);

__asm__(
    ".text\n"
    ".globl nm_win64_veh_call\n"
    ".def nm_win64_veh_call; .scl 2; .type 32; .endef\n"
    ".seh_proc nm_win64_veh_call\n"
    "nm_win64_veh_call:\n"
    "    subq $40, %rsp\n"
    "    .seh_stackalloc 40\n"
    "    .seh_endprologue\n"
    "    movq %rbx, 0x00(%rcx)\n"
    "    movq %rbp, 0x08(%rcx)\n"
    "    movq %rsi, 0x10(%rcx)\n"
    "    movq %rdi, 0x18(%rcx)\n"
    "    movq %r12, 0x20(%rcx)\n"
    "    movq %r13, 0x28(%rcx)\n"
    "    movq %r14, 0x30(%rcx)\n"
    "    movq %r15, 0x38(%rcx)\n"
    "    movq %rsp, 0x40(%rcx)\n"
    "    leaq .Lnm_veh_recover(%rip), %rax\n"
    "    movq %rax, 0x48(%rcx)\n"
    "    movdqu %xmm6,  0x50(%rcx)\n"
    "    movdqu %xmm7,  0x60(%rcx)\n"
    "    movdqu %xmm8,  0x70(%rcx)\n"
    "    movdqu %xmm9,  0x80(%rcx)\n"
    "    movdqu %xmm10, 0x90(%rcx)\n"
    "    movdqu %xmm11, 0xA0(%rcx)\n"
    "    movdqu %xmm12, 0xB0(%rcx)\n"
    "    movdqu %xmm13, 0xC0(%rcx)\n"
    "    movdqu %xmm14, 0xD0(%rcx)\n"
    "    movdqu %xmm15, 0xE0(%rcx)\n"
    "    movq %rdx, %rax\n"
    "    movq %r8,  %rcx\n"
    "    callq *%rax\n"
    "    addq $40, %rsp\n"
    "    retq\n"
    ".Lnm_veh_recover:\n"
    "    movl $1, %eax\n"
    "    addq $40, %rsp\n"
    "    retq\n"
    ".seh_endproc\n"
);

} // extern "C"
#endif

namespace nativemods {

namespace {

std::atomic<uint64_t> g_msvc_cxx_passthrough_count{0};

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
#elif defined(_WIN32) && defined(__x86_64__)

struct VehGuardFrame {
    NmWin64GuardRegs regs{};
    VehGuardFrame*   prev = nullptr;
    uint32_t         caught_code = 0;
    uint64_t         caught_address = 0;
    bool             triggered = false;
};

thread_local VehGuardFrame* tls_guard_top = nullptr;

bool should_catch_in_guard(DWORD code) noexcept {
    // Always let CLR managed exceptions, GCC C++ SEH exceptions, thread-naming,
    // OutputDebugString, and debugger notifications pass through untouched.
    if (code == 0xE0434352u || // CoreCLR managed exception
        code == 0xE0434F4Du || // Legacy CLR exception
        code == 0x20474343u || // MinGW GCC SEH C++ exception (caught by C++ try/catch below)
        code == 0x406D1388u || // MSVC SetThreadName
        code == 0x40010006u || // DBG_PRINTEXCEPTION_C (OutputDebugStringA)
        code == 0x4001000Au || // DBG_PRINTEXCEPTION_WIDE_C (OutputDebugStringW)
        code == EXCEPTION_BREAKPOINT ||
        code == EXCEPTION_SINGLE_STEP) {
        return false;
    }

    switch (code) {
        case NM_MSVC_CXX_EXCEPTION_CODE:
        case EXCEPTION_ACCESS_VIOLATION:
        case EXCEPTION_IN_PAGE_ERROR:
        case EXCEPTION_ILLEGAL_INSTRUCTION:
        case EXCEPTION_PRIV_INSTRUCTION:
        case EXCEPTION_INT_DIVIDE_BY_ZERO:
        case EXCEPTION_INT_OVERFLOW:
        case EXCEPTION_ARRAY_BOUNDS_EXCEEDED:
        case EXCEPTION_DATATYPE_MISALIGNMENT:
        case EXCEPTION_STACK_OVERFLOW:
        case EXCEPTION_FLT_DENORMAL_OPERAND:
        case EXCEPTION_FLT_DIVIDE_BY_ZERO:
        case EXCEPTION_FLT_INEXACT_RESULT:
        case EXCEPTION_FLT_INVALID_OPERATION:
        case EXCEPTION_FLT_OVERFLOW:
        case EXCEPTION_FLT_STACK_CHECK:
        case EXCEPTION_FLT_UNDERFLOW:
            return true;
        default:
            return false;
    }
}

LONG CALLBACK GuardVehHandler(PEXCEPTION_POINTERS ep) {
    if (!ep || !ep->ExceptionRecord || !ep->ContextRecord) {
        return EXCEPTION_CONTINUE_SEARCH;
    }

    const DWORD code = ep->ExceptionRecord->ExceptionCode;
    VehGuardFrame* frame = tls_guard_top;

    // Outside nm_call_guarded: never interfere with game C++ exceptions or CoreCLR!
    if (frame == nullptr) {
        if (code == NM_MSVC_CXX_EXCEPTION_CODE) {
            g_msvc_cxx_passthrough_count.fetch_add(1, std::memory_order_relaxed);
        }
        return EXCEPTION_CONTINUE_SEARCH;
    }

    if (!should_catch_in_guard(code)) {
        return EXCEPTION_CONTINUE_SEARCH;
    }

    frame->caught_code = static_cast<uint32_t>(code);
    frame->caught_address = reinterpret_cast<uint64_t>(ep->ExceptionRecord->ExceptionAddress);
    frame->triggered = true;

    // Restore saved non-volatile context and resume cleanly at .Lnm_veh_recover
    // without calling RtlUnwindEx/longjmp from inside the VEH.
    CONTEXT* ctx = ep->ContextRecord;
    ctx->Rbx = frame->regs.rbx;
    ctx->Rbp = frame->regs.rbp;
    ctx->Rsi = frame->regs.rsi;
    ctx->Rdi = frame->regs.rdi;
    ctx->R12 = frame->regs.r12;
    ctx->R13 = frame->regs.r13;
    ctx->R14 = frame->regs.r14;
    ctx->R15 = frame->regs.r15;
    ctx->Rsp = frame->regs.rsp;
    ctx->Rip = frame->regs.rip;
    ctx->Rax = 1;
    std::memcpy(&ctx->Xmm6, frame->regs.xmm, sizeof(frame->regs.xmm));

    return EXCEPTION_CONTINUE_EXECUTION;
}

std::atomic<void*> g_veh_handle{nullptr};

void install_veh_once() noexcept {
    if (g_veh_handle.load(std::memory_order_acquire) != nullptr) {
        return;
    }
    // Register with First=0 so CoreCLR's First=1 VEH handles JIT/GC faults first,
    // while native MSVC C++ exceptions (0xE06D7363) and native AVs inside guarded
    // calls reach our handler cleanly.
    PVOID handle = AddVectoredExceptionHandler(0, &GuardVehHandler);
    void* expected = nullptr;
    if (!g_veh_handle.compare_exchange_strong(expected, handle, std::memory_order_acq_rel)) {
        if (handle) {
            RemoveVectoredExceptionHandler(handle);
        }
    }
}

struct GuardCallCtx {
    nm_guarded_fn_t      fn;
    void*                user_ctx;
    int64_t*             out_result;
    nm_exception_info_t* out_exception;
};

int32_t run_guarded_cpp_inner(void* raw) {
    auto* c = static_cast<GuardCallCtx*>(raw);
    try {
        const int64_t res = c->fn(c->user_ctx);
        if (c->out_result) {
            *c->out_result = res;
        }
        return 0;
    } catch (const SimulatedMsvcException& ex) {
        fill_exception(c->out_exception, ex.code(), 0, ex.what());
        return 2;
    } catch (const std::exception& ex) {
        fill_exception(c->out_exception, NM_MSVC_CXX_EXCEPTION_CODE, 0, ex.what());
        return 2;
    } catch (...) {
        fill_exception(c->out_exception, NM_MSVC_CXX_EXCEPTION_CODE, 0, "Caught unknown C++ exception across native boundary");
        return 2;
    }
}

nm_status_t invoke_mingw_veh_wall(
    nm_guarded_fn_t      fn,
    void*                context,
    int64_t*             out_result,
    nm_exception_info_t* out_exception) {
    install_veh_once();

    VehGuardFrame frame{};
    frame.prev = tls_guard_top;
    tls_guard_top = &frame;

    GuardCallCtx call_ctx{fn, context, out_result, out_exception};
    const int32_t rc = nm_win64_veh_call(&frame.regs, &run_guarded_cpp_inner, &call_ctx);

    tls_guard_top = frame.prev;

    if (rc == 0) {
        return NM_OK;
    }
    if (rc == 2) {
        return NM_ERR_GAME_EXCEPTION;
    }

    char buf[160];
    if (frame.caught_code == NM_MSVC_CXX_EXCEPTION_CODE) {
        std::snprintf(buf, sizeof(buf), "Caught game MSVC C++ exception (0x%08X) via Win64 VEH", frame.caught_code);
    } else if (frame.caught_code == EXCEPTION_ACCESS_VIOLATION) {
        std::snprintf(buf, sizeof(buf), "Caught Win64 ACCESS_VIOLATION (0x%08X) via VEH", frame.caught_code);
    } else {
        std::snprintf(buf, sizeof(buf), "Caught Win64 hardware/SEH exception (0x%08X) via VEH", frame.caught_code);
    }
    fill_exception(out_exception, frame.caught_code, frame.caught_address, buf);
    return NM_ERR_GAME_EXCEPTION;
}
#endif

} // namespace

void ensure_exception_wall_installed() noexcept {
#if defined(_WIN32) && !defined(_MSC_VER) && defined(__x86_64__)
    install_veh_once();
#endif
}

uint64_t get_msvc_cxx_passthrough_count() noexcept {
    return g_msvc_cxx_passthrough_count.load(std::memory_order_relaxed);
}

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
#elif defined(_WIN32) && defined(__x86_64__)
    return invoke_mingw_veh_wall(fn, context, out_result, out_exception);
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
