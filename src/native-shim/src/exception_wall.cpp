#include "exception_wall.hpp"
#include "shim_logger.hpp"

#include <atomic>
#include <cstddef>
#include <cstdio>
#include <cstring>
#include <type_traits>

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

void nm_win64_call_8args(nm_native_call_desc_t* desc);

static_assert(offsetof(nm_native_call_desc_t, target_address) == 0x00, "offset mismatch");
static_assert(offsetof(nm_native_call_desc_t, args) == 0x18, "offset mismatch");
static_assert(offsetof(nm_native_call_desc_t, out_int) == 0x58, "offset mismatch");
static_assert(offsetof(nm_native_call_desc_t, out_float_bits) == 0x60, "offset mismatch");

__asm__(
    ".text\n"
    ".globl nm_win64_call_8args\n"
    ".def nm_win64_call_8args; .scl 2; .type 32; .endef\n"
    ".seh_proc nm_win64_call_8args\n"
    "nm_win64_call_8args:\n"
    "    pushq %rbx\n"
    "    .seh_pushreg %rbx\n"
    "    subq $0x40, %rsp\n"
    "    .seh_stackalloc 0x40\n"
    "    .seh_endprologue\n"
    "    movq %rcx, %rbx\n"
    "    movq 0x38(%rbx), %rax\n"
    "    movq %rax, 0x20(%rsp)\n"
    "    movq 0x40(%rbx), %rax\n"
    "    movq %rax, 0x28(%rsp)\n"
    "    movq 0x48(%rbx), %rax\n"
    "    movq %rax, 0x30(%rsp)\n"
    "    movq 0x50(%rbx), %rax\n"
    "    movq %rax, 0x38(%rsp)\n"
    "    movq 0x18(%rbx), %rcx\n"
    "    movq %rcx, %xmm0\n"
    "    movq 0x20(%rbx), %rdx\n"
    "    movq %rdx, %xmm1\n"
    "    movq 0x28(%rbx), %r8\n"
    "    movq %r8,  %xmm2\n"
    "    movq 0x30(%rbx), %r9\n"
    "    movq %r9,  %xmm3\n"
    "    movq 0x00(%rbx), %rax\n"
    "    callq *%rax\n"
    "    movq %rax, 0x58(%rbx)\n"
    "    movq %xmm0, 0x60(%rbx)\n"
    "    addq $0x40, %rsp\n"
    "    popq %rbx\n"
    "    retq\n"
    ".seh_endproc\n"
);

} // extern "C"
#elif !defined(_WIN32) && defined(__x86_64__)
extern "C" {

struct SysvCallFrame {
    uint64_t target;      // 0x00
    uint64_t gpr[6];      // 0x08..0x30
    uint64_t xmm[8];      // 0x38..0x70
    uint64_t stack[8];    // 0x78..0xB0
    uint64_t stack_count; // 0xB8
    uint64_t out_int;     // 0xC0
    uint64_t out_float;   // 0xC8
};

static_assert(offsetof(SysvCallFrame, target) == 0x00, "offset mismatch");
static_assert(offsetof(SysvCallFrame, gpr) == 0x08, "offset mismatch");
static_assert(offsetof(SysvCallFrame, xmm) == 0x38, "offset mismatch");
static_assert(offsetof(SysvCallFrame, stack) == 0x78, "offset mismatch");
static_assert(offsetof(SysvCallFrame, stack_count) == 0xB8, "offset mismatch");
static_assert(offsetof(SysvCallFrame, out_int) == 0xC0, "offset mismatch");
static_assert(offsetof(SysvCallFrame, out_float) == 0xC8, "offset mismatch");

void nm_sysv_invoke_frame(SysvCallFrame* f);

__asm__(
    ".text\n"
    ".globl nm_sysv_invoke_frame\n"
    ".type nm_sysv_invoke_frame, @function\n"
    "nm_sysv_invoke_frame:\n"
    "    .cfi_startproc\n"
    "    pushq %rbp\n"
    "    .cfi_def_cfa_offset 16\n"
    "    .cfi_offset %rbp, -16\n"
    "    movq %rsp, %rbp\n"
    "    .cfi_def_cfa_register %rbp\n"
    "    pushq %rbx\n"
    "    .cfi_offset %rbx, -24\n"
    "    subq $8, %rsp\n"
    "    movq %rdi, %rbx\n"
    "    movq 0xB8(%rbx), %rcx\n"
    "    testq %rcx, %rcx\n"
    "    jz .Lsysv_regs\n"
    "    testq $1, %rcx\n"
    "    jz .Lsysv_push_loop\n"
    "    subq $8, %rsp\n"
    ".Lsysv_push_loop:\n"
    "    decq %rcx\n"
    "    movq 0x78(%rbx,%rcx,8), %rax\n"
    "    pushq %rax\n"
    "    testq %rcx, %rcx\n"
    "    jnz .Lsysv_push_loop\n"
    ".Lsysv_regs:\n"
    "    movq 0x38(%rbx), %xmm0\n"
    "    movq 0x40(%rbx), %xmm1\n"
    "    movq 0x48(%rbx), %xmm2\n"
    "    movq 0x50(%rbx), %xmm3\n"
    "    movq 0x58(%rbx), %xmm4\n"
    "    movq 0x60(%rbx), %xmm5\n"
    "    movq 0x68(%rbx), %xmm6\n"
    "    movq 0x70(%rbx), %xmm7\n"
    "    movq 0x08(%rbx), %rdi\n"
    "    movq 0x10(%rbx), %rsi\n"
    "    movq 0x18(%rbx), %rdx\n"
    "    movq 0x20(%rbx), %rcx\n"
    "    movq 0x28(%rbx), %r8\n"
    "    movq 0x30(%rbx), %r9\n"
    "    movl $8, %eax\n"
    "    movq 0x00(%rbx), %r10\n"
    "    callq *%r10\n"
    "    movq %rax, 0xC0(%rbx)\n"
    "    movq %xmm0, 0xC8(%rbx)\n"
    "    leaq -8(%rbp), %rsp\n"
    "    popq %rbx\n"
    "    popq %rbp\n"
    "    .cfi_def_cfa %rsp, 8\n"
    "    retq\n"
    "    .cfi_endproc\n"
    ".size nm_sysv_invoke_frame, .-nm_sysv_invoke_frame\n"
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
        } else if (code == EXCEPTION_ACCESS_VIOLATION || code == EXCEPTION_ILLEGAL_INSTRUCTION || code == EXCEPTION_STACK_OVERFLOW) {
            static std::atomic<int> diag_count{0};
            if (diag_count.fetch_add(1, std::memory_order_relaxed) < 5) {
                const uint64_t mod_base = reinterpret_cast<uint64_t>(GetModuleHandleW(nullptr));
                const uint64_t rip = ep->ContextRecord->Rip;
                const uint64_t fault_addr = (ep->ExceptionRecord->NumberParameters >= 2)
                                                ? static_cast<uint64_t>(ep->ExceptionRecord->ExceptionInformation[1])
                                                : 0ull;
                logger_logf("ERROR",
                            "[VEH-DIAG] Native exception outside guard: code=0x%08lX rip=0x%llX (exe_rva=0x%llX) fault_addr=0x%llX rcx=0x%llX rdx=0x%llX",
                            static_cast<unsigned long>(code),
                            static_cast<unsigned long long>(rip),
                            static_cast<unsigned long long>(rip >= mod_base ? (rip - mod_base) : 0ull),
                            static_cast<unsigned long long>(fault_addr),
                            static_cast<unsigned long long>(ep->ContextRecord->Rcx),
                            static_cast<unsigned long long>(ep->ContextRecord->Rdx));
            }
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

#if defined(_WIN32) && defined(_MSC_VER) && defined(_M_X64)
namespace {
template <uint32_t Mask, size_t I>
using Win64ArgT = std::conditional_t<((Mask >> I) & 1u) != 0, double, uint64_t>;

template <uint32_t Mask, size_t I>
Win64ArgT<Mask, I> win64_extract_arg(const uint64_t* args) noexcept {
    Win64ArgT<Mask, I> v{};
    std::memcpy(&v, &args[I], sizeof(v));
    return v;
}

template <uint32_t Mask>
void msvc_win64_call_8args_masked(nm_native_call_desc_t* d) {
    if (d->return_is_float) {
        using Fn = double (*)(
            Win64ArgT<Mask, 0>, Win64ArgT<Mask, 1>, Win64ArgT<Mask, 2>, Win64ArgT<Mask, 3>,
            uint64_t, uint64_t, uint64_t, uint64_t);
        auto fn = reinterpret_cast<Fn>(static_cast<uintptr_t>(d->target_address));
        const double r = fn(
            win64_extract_arg<Mask, 0>(d->args),
            win64_extract_arg<Mask, 1>(d->args),
            win64_extract_arg<Mask, 2>(d->args),
            win64_extract_arg<Mask, 3>(d->args),
            d->args[4], d->args[5], d->args[6], d->args[7]);
        std::memcpy(&d->out_float_bits, &r, sizeof(r));
        d->out_int = d->out_float_bits;
    } else {
        using Fn = uint64_t (*)(
            Win64ArgT<Mask, 0>, Win64ArgT<Mask, 1>, Win64ArgT<Mask, 2>, Win64ArgT<Mask, 3>,
            uint64_t, uint64_t, uint64_t, uint64_t);
        auto fn = reinterpret_cast<Fn>(static_cast<uintptr_t>(d->target_address));
        d->out_int = fn(
            win64_extract_arg<Mask, 0>(d->args),
            win64_extract_arg<Mask, 1>(d->args),
            win64_extract_arg<Mask, 2>(d->args),
            win64_extract_arg<Mask, 3>(d->args),
            d->args[4], d->args[5], d->args[6], d->args[7]);
        d->out_float_bits = 0;
    }
}

void msvc_win64_call_8args(nm_native_call_desc_t* d) {
    switch (d->float_arg_mask & 0xFu) {
        case 0x0u: msvc_win64_call_8args_masked<0x0u>(d); break;
        case 0x1u: msvc_win64_call_8args_masked<0x1u>(d); break;
        case 0x2u: msvc_win64_call_8args_masked<0x2u>(d); break;
        case 0x3u: msvc_win64_call_8args_masked<0x3u>(d); break;
        case 0x4u: msvc_win64_call_8args_masked<0x4u>(d); break;
        case 0x5u: msvc_win64_call_8args_masked<0x5u>(d); break;
        case 0x6u: msvc_win64_call_8args_masked<0x6u>(d); break;
        case 0x7u: msvc_win64_call_8args_masked<0x7u>(d); break;
        case 0x8u: msvc_win64_call_8args_masked<0x8u>(d); break;
        case 0x9u: msvc_win64_call_8args_masked<0x9u>(d); break;
        case 0xAu: msvc_win64_call_8args_masked<0xAu>(d); break;
        case 0xBu: msvc_win64_call_8args_masked<0xBu>(d); break;
        case 0xCu: msvc_win64_call_8args_masked<0xCu>(d); break;
        case 0xDu: msvc_win64_call_8args_masked<0xDu>(d); break;
        case 0xEu: msvc_win64_call_8args_masked<0xEu>(d); break;
        default:   msvc_win64_call_8args_masked<0xFu>(d); break;
    }
}
} // namespace
#endif

nm_status_t invoke_native_guarded(
    nm_native_call_desc_t* desc,
    nm_exception_info_t*   out_exception) {
    if (desc == nullptr || desc->target_address == 0 || desc->arg_count > 8u) {
        return NM_ERR_INVALID_ARG;
    }
    desc->out_int = 0;
    desc->out_float_bits = 0;

#if !(defined(_WIN32) && !defined(_MSC_VER) && defined(__x86_64__)) && \
    !(defined(_WIN32) && defined(_MSC_VER) && defined(_M_X64)) && \
    !(!defined(_WIN32) && defined(__x86_64__))
    (void)out_exception;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#else
    auto trampoline = [](void* raw) -> int64_t {
        auto* d = static_cast<nm_native_call_desc_t*>(raw);
#if defined(_WIN32) && !defined(_MSC_VER) && defined(__x86_64__)
        nm_win64_call_8args(d);
        return static_cast<int64_t>(d->out_int);
#elif defined(_WIN32) && defined(_MSC_VER) && defined(_M_X64)
        msvc_win64_call_8args(d);
        return static_cast<int64_t>(d->out_int);
#elif !defined(_WIN32) && defined(__x86_64__)
        SysvCallFrame frame{};
        frame.target = d->target_address;
        uint32_t g_idx = 0;
        uint32_t f_idx = 0;
        uint32_t s_idx = 0;
        for (uint32_t i = 0; i < d->arg_count; ++i) {
            const bool is_flt = ((d->float_arg_mask >> i) & 1u) != 0;
            if (is_flt) {
                if (f_idx < 8u) {
                    frame.xmm[f_idx++] = d->args[i];
                } else {
                    frame.stack[s_idx++] = d->args[i];
                }
            } else {
                if (g_idx < 6u) {
                    frame.gpr[g_idx++] = d->args[i];
                } else {
                    frame.stack[s_idx++] = d->args[i];
                }
            }
        }
        frame.stack_count = s_idx;
        nm_sysv_invoke_frame(&frame);
        d->out_int = frame.out_int;
        d->out_float_bits = frame.out_float;
        return static_cast<int64_t>(d->out_int);
#endif
    };

    int64_t ignored = 0;
    return invoke_guarded(trampoline, desc, &ignored, out_exception);
#endif
}

} // namespace nativemods
