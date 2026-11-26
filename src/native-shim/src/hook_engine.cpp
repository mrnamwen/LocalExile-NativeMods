#include "hook_engine.hpp"

#include "address_table.hpp"
#include "exception_wall.hpp"

#include <algorithm>
#include <cstddef>
#include <cstdio>
#include <cstring>
#include <limits>
#include <string>

#if defined(_WIN32)
#  define WIN32_LEAN_AND_MEAN
#  include <windows.h>
#else
#  include <fstream>
#  include <sys/mman.h>
#  include <unistd.h>
extern "C" void __register_frame(const void* begin);
extern "C" void __deregister_frame(const void* begin);
#endif

#if defined(_WIN32) && !defined(_MSC_VER) && defined(__x86_64__)
extern "C" {
void nm_hook_invoke_original(void* orig_fn, nm_hook_context_t* ctx);
__asm__(
    ".text\n"
    ".globl nm_hook_invoke_original\n"
    ".def nm_hook_invoke_original; .scl 2; .type 32; .endef\n"
    ".seh_proc nm_hook_invoke_original\n"
    "nm_hook_invoke_original:\n"
    "    pushq %rbx\n"
    "    .seh_pushreg %rbx\n"
    "    subq $0x40, %rsp\n"
    "    .seh_stackalloc 0x40\n"
    "    .seh_endprologue\n"
    "    movq %rdx, %rbx\n"
    "    movq %rcx, %r10\n"
    "    movq 0x20(%rbx), %rax\n"
    "    movq %rax, 0x20(%rsp)\n"
    "    movq 0x28(%rbx), %rax\n"
    "    movq %rax, 0x28(%rsp)\n"
    "    movq 0x30(%rbx), %rax\n"
    "    movq %rax, 0x30(%rsp)\n"
    "    movq 0x38(%rbx), %rax\n"
    "    movq %rax, 0x38(%rsp)\n"
    "    movq 0x40(%rbx), %xmm0\n"
    "    movq 0x48(%rbx), %xmm1\n"
    "    movq 0x50(%rbx), %xmm2\n"
    "    movq 0x58(%rbx), %xmm3\n"
    "    movq 0x00(%rbx), %rcx\n"
    "    movq 0x08(%rbx), %rdx\n"
    "    movq 0x10(%rbx), %r8\n"
    "    movq 0x18(%rbx), %r9\n"
    "    callq *%r10\n"
    "    movq %rax, 0x60(%rbx)\n"
    "    movq %xmm0, 0x68(%rbx)\n"
    "    addq $0x40, %rsp\n"
    "    popq %rbx\n"
    "    retq\n"
    ".seh_endproc\n"
);
} // extern "C"
#elif !defined(_WIN32) && defined(__x86_64__)
extern "C" {
void nm_hook_invoke_original(void* orig_fn, nm_hook_context_t* ctx);
__asm__(
    ".text\n"
    ".globl nm_hook_invoke_original\n"
    ".type nm_hook_invoke_original, @function\n"
    "nm_hook_invoke_original:\n"
    "    .cfi_startproc\n"
    "    pushq %rbp\n"
    "    .cfi_def_cfa_offset 16\n"
    "    .cfi_offset %rbp, -16\n"
    "    movq %rsp, %rbp\n"
    "    .cfi_def_cfa_register %rbp\n"
    "    pushq %rbx\n"
    "    .cfi_offset %rbx, -24\n"
    "    subq $8, %rsp\n"
    "    movq %rsi, %rbx\n"
    "    movq %rdi, %r10\n"
    "    pushq 0x38(%rbx)\n"
    "    pushq 0x30(%rbx)\n"
    "    movq 0x40(%rbx), %xmm0\n"
    "    movq 0x48(%rbx), %xmm1\n"
    "    movq 0x50(%rbx), %xmm2\n"
    "    movq 0x58(%rbx), %xmm3\n"
    "    movq 0x00(%rbx), %rdi\n"
    "    movq 0x08(%rbx), %rsi\n"
    "    movq 0x10(%rbx), %rdx\n"
    "    movq 0x18(%rbx), %rcx\n"
    "    movq 0x20(%rbx), %r8\n"
    "    movq 0x28(%rbx), %r9\n"
    "    movl $4, %eax\n"
    "    callq *%r10\n"
    "    movq %rax, 0x60(%rbx)\n"
    "    movq %xmm0, 0x68(%rbx)\n"
    "    leaq -8(%rbp), %rsp\n"
    "    popq %rbx\n"
    "    popq %rbp\n"
    "    .cfi_def_cfa %rsp, 8\n"
    "    retq\n"
    "    .cfi_endproc\n"
    ".size nm_hook_invoke_original, .-nm_hook_invoke_original\n"
);
} // extern "C"
#endif

namespace nativemods {

namespace {

static_assert(offsetof(nm_hook_context_t, args) == 0x00, "nm_hook_context_t::args offset");
static_assert(offsetof(nm_hook_context_t, float_args) == 0x40, "nm_hook_context_t::float_args offset");
static_assert(offsetof(nm_hook_context_t, return_value) == 0x60, "nm_hook_context_t::return_value offset");
static_assert(offsetof(nm_hook_context_t, return_float) == 0x68, "nm_hook_context_t::return_float offset");
static_assert(offsetof(nm_hook_context_t, target_address) == 0x70, "nm_hook_context_t::target_address offset");
static_assert(offsetof(nm_hook_context_t, skip_original) == 0x78, "nm_hook_context_t::skip_original offset");
static_assert(sizeof(nm_hook_context_t) == 0x80, "nm_hook_context_t size");

constexpr uint64_t kMaxNearDelta = 0x7FFF0000ull;

bool is_legacy_prefix(uint8_t b) noexcept {
    switch (b) {
        case 0x26:
        case 0x2E:
        case 0x36:
        case 0x3E:
        case 0x64:
        case 0x65:
        case 0x66:
        case 0x67:
        case 0xF0:
        case 0xF2:
        case 0xF3:
            return true;
        default:
            return false;
    }
}

bool decode_modrm(
    const uint8_t* p,
    size_t         max_len,
    size_t         base_offset,
    DecodedX64Insn& out,
    size_t&        out_modrm_len,
    uint8_t&       out_reg) {
    if (max_len < 1) return false;
    const uint8_t modrm = p[0];
    const uint8_t mod = (modrm >> 6) & 3u;
    const uint8_t reg = (modrm >> 3) & 7u;
    const uint8_t rm  = modrm & 7u;

    out.has_modrm = true;
    out.modrm_offset = static_cast<uint8_t>(base_offset);
    out_reg = reg;

    size_t len = 1;
    if (mod == 3u) {
        out_modrm_len = len;
        return true;
    }

    bool has_sib = (rm == 4u);
    uint8_t sib_base = 0;
    if (has_sib) {
        if (len + 1 > max_len) return false;
        sib_base = p[len] & 7u;
        ++len;
    }

    if (mod == 0u) {
        if (rm == 5u) {
            if (len + 4 > max_len) return false;
            out.is_rip_relative = true;
            out.disp_offset = static_cast<uint8_t>(base_offset + len);
            out.disp_size = 4;
            len += 4;
        } else if (has_sib && sib_base == 5u) {
            if (len + 4 > max_len) return false;
            len += 4;
        }
    } else if (mod == 1u) {
        if (len + 1 > max_len) return false;
        len += 1;
    } else if (mod == 2u) {
        if (len + 4 > max_len) return false;
        len += 4;
    }

    out_modrm_len = len;
    return true;
}

void emit_u32(std::vector<uint8_t>& out, uint32_t v) {
    const size_t pos = out.size();
    out.resize(pos + 4);
    std::memcpy(out.data() + pos, &v, 4);
}

void emit_u64(std::vector<uint8_t>& out, uint64_t v) {
    const size_t pos = out.size();
    out.resize(pos + 8);
    std::memcpy(out.data() + pos, &v, 8);
}

void emit_call_abs64(std::vector<uint8_t>& out, uint64_t target) {
    // call qword ptr [rip+2] ; jmp short +8 ; <u64 target>
    out.push_back(0xFFu);
    out.push_back(0x15u);
    out.push_back(0x02u);
    out.push_back(0x00u);
    out.push_back(0x00u);
    out.push_back(0x00u);
    out.push_back(0xEBu);
    out.push_back(0x08u);
    emit_u64(out, target);
}

void emit_jmp_abs64(std::vector<uint8_t>& out, uint64_t target) {
    out.push_back(0xFFu);
    out.push_back(0x25u);
    out.push_back(0x00u);
    out.push_back(0x00u);
    out.push_back(0x00u);
    out.push_back(0x00u);
    emit_u64(out, target);
}

bool fits_i32(int64_t v) noexcept {
    return v >= std::numeric_limits<int32_t>::min() && v <= std::numeric_limits<int32_t>::max();
}

bool fits_i8(int64_t v) noexcept {
    return v >= std::numeric_limits<int8_t>::min() && v <= std::numeric_limits<int8_t>::max();
}

} // namespace

DecodedX64Insn HookEngine::decode_x64_insn(const uint8_t* p, size_t max_len) {
    DecodedX64Insn insn{};
    if (!p || max_len == 0) return insn;

    const size_t limit = std::min<size_t>(max_len, 15u);
    size_t i = 0;
    bool has_66 = false;
    bool has_67 = false;
    bool rex_w = false;

    // 1. Legacy prefixes
    while (i < limit && is_legacy_prefix(p[i])) {
        if (p[i] == 0x66u) has_66 = true;
        if (p[i] == 0x67u) has_67 = true;
        ++i;
    }
    if (i >= limit) return DecodedX64Insn{};

    // 2. VEX2 (0xC5), VEX3 (0xC4), EVEX (0x62), or REX (0x40..0x4F)
    const uint8_t b = p[i];
    if (b == 0xC5u) {
        // VEX2: C5 [RvvvvLpp] <opcode> ...
        if (i + 2 >= limit) return DecodedX64Insn{};
        i += 2;
        const uint8_t op = p[i++];
        insn.is_0f = true;
        insn.opcode_byte1 = 0x0Fu;
        insn.opcode_byte2 = op;
        if (op == 0x77u) {
            // vzeroupper / vzeroall: no ModR/M
            insn.length = static_cast<uint8_t>(i);
            return insn;
        }
        size_t m = 0;
        uint8_t reg = 0;
        if (!decode_modrm(p + i, limit - i, i, insn, m, reg)) return DecodedX64Insn{};
        i += m;
        const size_t imm = (op == 0xC2u || op == 0xC4u || op == 0xC5u || op == 0xC6u ||
                            (op >= 0x70u && op <= 0x73u)) ? 1u : 0u;
        if (i + imm > limit) return DecodedX64Insn{};
        i += imm;
        insn.length = static_cast<uint8_t>(i);
        return insn;
    }

    if (b == 0xC4u) {
        // VEX3: C4 [RXBmmmmm] [WvvvvLpp] <opcode> ...
        if (i + 3 >= limit) return DecodedX64Insn{};
        const uint8_t mmmmm = p[i + 1] & 0x1Fu;
        const uint8_t ppw = p[i + 2];
        (void)ppw;
        if (mmmmm < 1u || mmmmm > 3u) return DecodedX64Insn{};
        i += 3;
        const uint8_t op = p[i++];
        insn.is_0f = true;
        insn.opcode_byte1 = 0x0Fu;
        insn.opcode_byte2 = op;
        if (mmmmm == 1u && op == 0x77u) {
            insn.length = static_cast<uint8_t>(i);
            return insn;
        }
        size_t m = 0;
        uint8_t reg = 0;
        if (!decode_modrm(p + i, limit - i, i, insn, m, reg)) return DecodedX64Insn{};
        i += m;
        size_t imm = 0;
        if (mmmmm == 3u) {
            imm = 1u; // 0F 3A map always has imm8
        } else if (mmmmm == 1u) {
            if (op == 0xC2u || op == 0xC4u || op == 0xC5u || op == 0xC6u || (op >= 0x70u && op <= 0x73u)) {
                imm = 1u;
            }
        }
        if (i + imm > limit) return DecodedX64Insn{};
        i += imm;
        insn.length = static_cast<uint8_t>(i);
        return insn;
    }

    if (b == 0x62u) {
        // EVEX: 62 [P0] [P1] [P2] <opcode> ModRM ...
        if (i + 4 >= limit) return DecodedX64Insn{};
        const uint8_t p0 = p[i + 1];
        const uint8_t p1 = p[i + 2];
        const uint8_t map = p0 & 0x07u;
        if ((p1 & 0x04u) == 0u || map < 1u || map > 3u) return DecodedX64Insn{};
        i += 4;
        const uint8_t op = p[i++];
        insn.is_0f = true;
        insn.opcode_byte1 = 0x0Fu;
        insn.opcode_byte2 = op;
        size_t m = 0;
        uint8_t reg = 0;
        if (!decode_modrm(p + i, limit - i, i, insn, m, reg)) return DecodedX64Insn{};
        i += m;
        size_t imm = 0;
        if (map == 3u) {
            imm = 1u;
        } else if (map == 1u) {
            if (op == 0xC2u || op == 0xC4u || op == 0xC5u || op == 0xC6u || (op >= 0x70u && op <= 0x73u)) {
                imm = 1u;
            }
        }
        if (i + imm > limit) return DecodedX64Insn{};
        i += imm;
        insn.length = static_cast<uint8_t>(i);
        return insn;
    }

    if ((b & 0xF0u) == 0x40u) {
        rex_w = (b & 0x08u) != 0;
        ++i;
        if (i >= limit) return DecodedX64Insn{};
    }

    // 3. Primary opcode
    const uint8_t op = p[i++];
    insn.opcode_byte1 = op;

    // 3a. 2-byte (0x0F) and 3-byte (0F 38 / 0F 3A) opcodes
    if (op == 0x0Fu) {
        if (i >= limit) return DecodedX64Insn{};
        const uint8_t op2 = p[i++];
        insn.is_0f = true;
        insn.opcode_byte2 = op2;

        if (op2 >= 0x80u && op2 <= 0x8Fu) {
            // Near conditional branch jcc rel32
            if (i + 4 > limit) return DecodedX64Insn{};
            insn.is_rel_branch = true;
            insn.disp_offset = static_cast<uint8_t>(i);
            insn.disp_size = 4;
            i += 4;
            insn.length = static_cast<uint8_t>(i);
            return insn;
        }

        if (op2 == 0x38u) {
            if (i >= limit) return DecodedX64Insn{};
            ++i; // 3rd opcode byte
            size_t m = 0;
            uint8_t reg = 0;
            if (!decode_modrm(p + i, limit - i, i, insn, m, reg)) return DecodedX64Insn{};
            i += m;
            insn.length = static_cast<uint8_t>(i);
            return insn;
        }

        if (op2 == 0x3Au) {
            if (i >= limit) return DecodedX64Insn{};
            ++i; // 3rd opcode byte
            size_t m = 0;
            uint8_t reg = 0;
            if (!decode_modrm(p + i, limit - i, i, insn, m, reg)) return DecodedX64Insn{};
            i += m;
            if (i + 1 > limit) return DecodedX64Insn{};
            i += 1;
            insn.length = static_cast<uint8_t>(i);
            return insn;
        }

        // No-ModR/M 0x0F opcodes
        if (op2 == 0x05u || op2 == 0x06u || op2 == 0x07u || op2 == 0x08u || op2 == 0x09u ||
            op2 == 0x0Bu || (op2 >= 0x30u && op2 <= 0x35u) || op2 == 0x77u ||
            op2 == 0xA0u || op2 == 0xA1u || op2 == 0xA2u || op2 == 0xA8u || op2 == 0xA9u ||
            op2 == 0xAAu || (op2 >= 0xC8u && op2 <= 0xCFu)) {
            insn.length = static_cast<uint8_t>(i);
            return insn;
        }

        // ModR/M + imm8 0x0F opcodes
        if ((op2 >= 0x70u && op2 <= 0x73u) || op2 == 0xA4u || op2 == 0xACu ||
            op2 == 0xBAu || op2 == 0xC2u || (op2 >= 0xC4u && op2 <= 0xC6u)) {
            size_t m = 0;
            uint8_t reg = 0;
            if (!decode_modrm(p + i, limit - i, i, insn, m, reg)) return DecodedX64Insn{};
            i += m;
            if (i + 1 > limit) return DecodedX64Insn{};
            i += 1;
            insn.length = static_cast<uint8_t>(i);
            return insn;
        }

        // ModR/M + 0 imm 0x0F opcodes
        if (op2 <= 0x03u || op2 == 0x0Du || (op2 >= 0x10u && op2 <= 0x1Fu) ||
            (op2 >= 0x20u && op2 <= 0x23u) || (op2 >= 0x28u && op2 <= 0x2Fu) ||
            (op2 >= 0x40u && op2 <= 0x6Fu) || (op2 >= 0x74u && op2 <= 0x76u) ||
            op2 == 0x7Eu || op2 == 0x7Fu || (op2 >= 0x90u && op2 <= 0x9Fu) ||
            op2 == 0xA3u || op2 == 0xA5u || op2 == 0xABu || op2 == 0xADu ||
            op2 == 0xAEu || op2 == 0xAFu || (op2 >= 0xB0u && op2 <= 0xBFu) ||
            op2 == 0xC0u || op2 == 0xC1u || op2 == 0xC3u || op2 == 0xC7u ||
            op2 >= 0xD0u) {
            size_t m = 0;
            uint8_t reg = 0;
            if (!decode_modrm(p + i, limit - i, i, insn, m, reg)) return DecodedX64Insn{};
            i += m;
            insn.length = static_cast<uint8_t>(i);
            return insn;
        }

        return DecodedX64Insn{};
    }

    // 3b. 1-byte opcodes invalid in 64-bit long mode
    switch (op) {
        case 0x06: case 0x07: case 0x0E: case 0x16: case 0x17: case 0x1E: case 0x1F:
        case 0x27: case 0x2F: case 0x37: case 0x3F: case 0x60: case 0x61: case 0x82:
        case 0x9A: case 0xCE: case 0xD4: case 0xD5: case 0xD6: case 0xEA:
            return DecodedX64Insn{};
        default:
            break;
    }

    // 3c. Single-byte opcodes without ModR/M & 0 immediate
    if ((op >= 0x50u && op <= 0x5Fu) || (op >= 0x6Cu && op <= 0x6Fu) ||
        (op >= 0x90u && op <= 0x99u) || (op >= 0x9Bu && op <= 0x9Fu) ||
        (op >= 0xA4u && op <= 0xA7u) || (op >= 0xAAu && op <= 0xAFu) ||
        op == 0xC3u || op == 0xC9u || op == 0xCBu || op == 0xCCu || op == 0xCFu ||
        op == 0xD7u || (op >= 0xECu && op <= 0xEFu) || op == 0xF1u ||
        op == 0xF4u || op == 0xF5u || op >= 0xF8u) {
        if (op == 0xF6u || op == 0xF7u || op == 0xFEu || op == 0xFFu) {
            // Fall through to ModR/M handlers below
        } else {
            insn.length = static_cast<uint8_t>(i);
            return insn;
        }
    }

    // 3d. Short branches (rel8)
    if ((op >= 0x70u && op <= 0x7Fu) || (op >= 0xE0u && op <= 0xE3u) || op == 0xEBu) {
        if (i + 1 > limit) return DecodedX64Insn{};
        insn.is_rel_branch = true;
        insn.disp_offset = static_cast<uint8_t>(i);
        insn.disp_size = 1;
        i += 1;
        insn.length = static_cast<uint8_t>(i);
        return insn;
    }

    // 3e. Near branches E8/E9 (rel32 in 64-bit mode)
    if (op == 0xE8u || op == 0xE9u) {
        if (i + 4 > limit) return DecodedX64Insn{};
        insn.is_rel_branch = true;
        insn.disp_offset = static_cast<uint8_t>(i);
        insn.disp_size = 4;
        i += 4;
        insn.length = static_cast<uint8_t>(i);
        return insn;
    }

    // 3f. No-ModR/M opcodes with imm8
    if ((op <= 0x3Cu && (op & 7u) == 4u) || op == 0x6Au || op == 0xA8u ||
        (op >= 0xB0u && op <= 0xB7u) || op == 0xCDu || (op >= 0xE4u && op <= 0xE7u)) {
        if (i + 1 > limit) return DecodedX64Insn{};
        i += 1;
        insn.length = static_cast<uint8_t>(i);
        return insn;
    }

    // 3g. No-ModR/M opcodes with imm16 / imm32
    if ((op <= 0x3Du && (op & 7u) == 5u) || op == 0x68u || op == 0xA9u) {
        const size_t imm = (has_66 && !rex_w) ? 2u : 4u;
        if (i + imm > limit) return DecodedX64Insn{};
        i += imm;
        insn.length = static_cast<uint8_t>(i);
        return insn;
    }

    // 3h. MOV r64/32/16, imm (B8..BF)
    if (op >= 0xB8u && op <= 0xBFu) {
        const size_t imm = rex_w ? 8u : (has_66 ? 2u : 4u);
        if (i + imm > limit) return DecodedX64Insn{};
        i += imm;
        insn.length = static_cast<uint8_t>(i);
        return insn;
    }

    // 3i. moffs A0..A3
    if (op >= 0xA0u && op <= 0xA3u) {
        const size_t moffs = has_67 ? 4u : 8u;
        if (i + moffs > limit) return DecodedX64Insn{};
        i += moffs;
        insn.length = static_cast<uint8_t>(i);
        return insn;
    }

    // 3j. RET imm16 (C2/CA), ENTER imm16,imm8 (C8)
    if (op == 0xC2u || op == 0xCAu) {
        if (i + 2 > limit) return DecodedX64Insn{};
        i += 2;
        insn.length = static_cast<uint8_t>(i);
        return insn;
    }
    if (op == 0xC8u) {
        if (i + 3 > limit) return DecodedX64Insn{};
        i += 3;
        insn.length = static_cast<uint8_t>(i);
        return insn;
    }

    // 3k. ModR/M + 0 imm
    if ((op <= 0x3Bu && (op & 7u) <= 3u) || op == 0x63u ||
        (op >= 0x84u && op <= 0x8Eu) || (op >= 0xD0u && op <= 0xD3u) ||
        (op >= 0xD8u && op <= 0xDFu) || op == 0xFEu || op == 0xFFu) {
        size_t m = 0;
        uint8_t reg = 0;
        if (!decode_modrm(p + i, limit - i, i, insn, m, reg)) return DecodedX64Insn{};
        i += m;
        insn.length = static_cast<uint8_t>(i);
        return insn;
    }

    if (op == 0x8Fu) {
        size_t m = 0;
        uint8_t reg = 0;
        if (!decode_modrm(p + i, limit - i, i, insn, m, reg) || reg != 0u) return DecodedX64Insn{};
        i += m;
        insn.length = static_cast<uint8_t>(i);
        return insn;
    }

    // 3l. ModR/M + imm8
    if (op == 0x6Bu || op == 0x80u || op == 0x83u || op == 0xC0u || op == 0xC1u || op == 0xC6u) {
        size_t m = 0;
        uint8_t reg = 0;
        if (!decode_modrm(p + i, limit - i, i, insn, m, reg)) return DecodedX64Insn{};
        i += m;
        if (i + 1 > limit) return DecodedX64Insn{};
        i += 1;
        insn.length = static_cast<uint8_t>(i);
        return insn;
    }

    // 3m. ModR/M + imm16/32
    if (op == 0x69u || op == 0x81u || op == 0xC7u) {
        size_t m = 0;
        uint8_t reg = 0;
        if (!decode_modrm(p + i, limit - i, i, insn, m, reg)) return DecodedX64Insn{};
        i += m;
        const size_t imm = (has_66 && !rex_w) ? 2u : 4u;
        if (i + imm > limit) return DecodedX64Insn{};
        i += imm;
        insn.length = static_cast<uint8_t>(i);
        return insn;
    }

    // 3n. Group 3 (F6/F7): /0 and /1 (TEST) carry imm8 / imm16/32; /2../7 have no imm.
    if (op == 0xF6u || op == 0xF7u) {
        size_t m = 0;
        uint8_t reg = 0;
        if (!decode_modrm(p + i, limit - i, i, insn, m, reg)) return DecodedX64Insn{};
        i += m;
        if (reg <= 1u) {
            const size_t imm = (op == 0xF6u) ? 1u : ((has_66 && !rex_w) ? 2u : 4u);
            if (i + imm > limit) return DecodedX64Insn{};
            i += imm;
        }
        insn.length = static_cast<uint8_t>(i);
        return insn;
    }

    return DecodedX64Insn{};
}

size_t HookEngine::compute_stolen_length(const uint8_t* code, size_t min_len) {
    if (!code || min_len == 0) return 0;
    constexpr size_t kMaxScan = 64;
    size_t offset = 0;
    size_t max_forward_branch_target = 0;
    while (offset < min_len && offset < kMaxScan) {
        const DecodedX64Insn insn = decode_x64_insn(code + offset, kMaxScan - offset);
        if (insn.length == 0) {
            return 0; // Fail safely instead of splitting an unknown/invalid instruction
        }
        const size_t next_offset = offset + insn.length;

        // Track forward conditional branches so an early-return pattern like:
        //   test rcx, rcx ; jnz +3 ; xor eax, eax ; ret ; <rest of prologue>
        // can continue decoding past the guarded ret.
        const bool is_cond_branch =
            (!insn.is_0f && ((insn.opcode_byte1 >= 0x70u && insn.opcode_byte1 <= 0x7Fu) ||
                             (insn.opcode_byte1 >= 0xE0u && insn.opcode_byte1 <= 0xE3u))) ||
            (insn.is_0f && insn.opcode_byte2 >= 0x80u && insn.opcode_byte2 <= 0x8Fu);
        if (is_cond_branch) {
            int64_t rel = 0;
            if (insn.disp_size == 1) {
                rel = static_cast<int8_t>(code[offset + insn.disp_offset]);
            } else if (insn.disp_size == 4) {
                int32_t r32 = 0;
                std::memcpy(&r32, code + offset + insn.disp_offset, 4);
                rel = r32;
            }
            const int64_t target = static_cast<int64_t>(next_offset) + rel;
            if (target > static_cast<int64_t>(next_offset) && target <= static_cast<int64_t>(kMaxScan)) {
                max_forward_branch_target = std::max(max_forward_branch_target, static_cast<size_t>(target));
            }
        }

        // Stop/reject if a function-terminating instruction is hit before min_len
        // unless an earlier conditional/forward branch targets at or past it.
        const uint8_t op = insn.opcode_byte1;
        const bool is_int3 = (!insn.is_0f && op == 0xCCu);
        const bool is_ret = (!insn.is_0f && (op == 0xC3u || op == 0xC2u || op == 0xCBu || op == 0xCAu));
        const bool is_ud2 = (insn.is_0f && insn.opcode_byte2 == 0x0Bu);
        const bool is_uncond_jmp =
            (!insn.is_0f && (op == 0xE9u || op == 0xEBu ||
                             (op == 0xFFu && insn.has_modrm &&
                              ((code[offset + insn.modrm_offset] >> 3) & 7u) == 4u)));

        if (is_int3) {
            if (max_forward_branch_target < next_offset) {
                return 0;
            }
        } else if ((is_ret || is_ud2 || is_uncond_jmp) && next_offset < min_len) {
            if (max_forward_branch_target < next_offset) {
                return 0;
            }
        }

        offset = next_offset;
    }
    return (offset >= min_len && offset <= kMaxScan) ? offset : 0;
}

bool HookEngine::relocate_instructions(
    uint64_t              orig_base,
    const uint8_t*        orig_code,
    size_t                stolen_len,
    uint64_t              tramp_base,
    std::vector<uint8_t>& out_bytes) {
    out_bytes.clear();
    if (!orig_code || stolen_len == 0) return false;

    struct StolenInsn {
        size_t         orig_off = 0;
        DecodedX64Insn dec;
        uint64_t       abs_target = 0;
        bool           internal_target = false;
        size_t         tramp_off = 0;
    };

    std::vector<StolenInsn> insns;
    size_t off = 0;
    while (off < stolen_len) {
        DecodedX64Insn dec = decode_x64_insn(orig_code + off, stolen_len - off);
        if (dec.length == 0) {
            return false;
        }
        StolenInsn si;
        si.orig_off = off;
        si.dec = dec;
        if (dec.is_rel_branch) {
            int64_t rel = 0;
            if (dec.disp_size == 1) {
                rel = static_cast<int8_t>(orig_code[off + dec.disp_offset]);
            } else {
                int32_t r32 = 0;
                std::memcpy(&r32, orig_code + off + dec.disp_offset, 4);
                rel = r32;
            }
            const uint64_t orig_next = orig_base + off + dec.length;
            si.abs_target = static_cast<uint64_t>(static_cast<int64_t>(orig_next) + rel);
            si.internal_target = (si.abs_target >= orig_base && si.abs_target < orig_base + stolen_len);
        }
        insns.push_back(si);
        off += dec.length;
    }
    if (off != stolen_len) {
        return false;
    }

    auto find_internal_target = [&](const StolenInsn& si) -> const StolenInsn* {
        if (si.internal_target) {
            const size_t target_orig_off = static_cast<size_t>(si.abs_target - orig_base);
            for (const auto& candidate : insns) {
                if (candidate.orig_off == target_orig_off) {
                    return &candidate;
                }
            }
        }
        return nullptr;
    };

    // Pass 1: compute initial relocated offsets (using near encoding when in +-2GB or internal, else far expansion).
    auto encode_all = [&](bool write_output) -> bool {
        if (write_output) {
            out_bytes.clear();
        }
        size_t cursor = 0;
        for (size_t idx = 0; idx < insns.size(); ++idx) {
            auto& si = insns[idx];
            if (!write_output) {
                si.tramp_off = cursor;
            }
            const uint64_t orig_ip = orig_base + si.orig_off;
            const uint64_t tramp_ip = tramp_base + si.tramp_off;
            const uint8_t* src = orig_code + si.orig_off;

            if (si.dec.is_rip_relative) {
                int32_t orig_disp = 0;
                std::memcpy(&orig_disp, src + si.dec.disp_offset, 4);
                const uint64_t abs_addr = static_cast<uint64_t>(
                    static_cast<int64_t>(orig_ip + si.dec.length) + orig_disp);
                const int64_t new_disp = static_cast<int64_t>(abs_addr) -
                                         static_cast<int64_t>(tramp_ip + si.dec.length);
                if (!fits_i32(new_disp)) {
                    return false;
                }
                if (write_output) {
                    const size_t base = out_bytes.size();
                    out_bytes.insert(out_bytes.end(), src, src + si.dec.length);
                    const int32_t d32 = static_cast<int32_t>(new_disp);
                    std::memcpy(out_bytes.data() + base + si.dec.disp_offset, &d32, 4);
                }
                cursor += si.dec.length;
                continue;
            }

            if (!si.dec.is_rel_branch) {
                if (write_output) {
                    out_bytes.insert(out_bytes.end(), src, src + si.dec.length);
                }
                cursor += si.dec.length;
                continue;
            }

            const StolenInsn* internal_insn = find_internal_target(si);
            if (si.internal_target && internal_insn == nullptr) {
                return false;
            }
            const bool force_near = (!write_output && internal_insn != nullptr);
            const uint64_t target = write_output
                ? (internal_insn ? (tramp_base + internal_insn->tramp_off) : si.abs_target)
                : si.abs_target;
            const uint8_t op = si.dec.opcode_byte1;

            if (op == 0xE8u) {
                const int64_t rel32 = static_cast<int64_t>(target) - static_cast<int64_t>(tramp_ip + 5);
                if (force_near || fits_i32(rel32)) {
                    if (write_output) {
                        out_bytes.push_back(0xE8u);
                        emit_u32(out_bytes, static_cast<uint32_t>(static_cast<int32_t>(rel32)));
                    }
                    cursor += 5;
                } else {
                    if (write_output) {
                        emit_call_abs64(out_bytes, target);
                    }
                    cursor += 16;
                }
            } else if (op == 0xE9u || op == 0xEBu) {
                const int64_t rel32 = static_cast<int64_t>(target) - static_cast<int64_t>(tramp_ip + 5);
                if (force_near || fits_i32(rel32)) {
                    if (write_output) {
                        out_bytes.push_back(0xE9u);
                        emit_u32(out_bytes, static_cast<uint32_t>(static_cast<int32_t>(rel32)));
                    }
                    cursor += 5;
                } else {
                    if (write_output) {
                        emit_jmp_abs64(out_bytes, target);
                    }
                    cursor += 14;
                }
            } else if ((op >= 0x70u && op <= 0x7Fu) || (si.dec.is_0f && si.dec.opcode_byte2 >= 0x80u && si.dec.opcode_byte2 <= 0x8Fu)) {
                const uint8_t cc = si.dec.is_0f ? (si.dec.opcode_byte2 & 0x0Fu) : (op & 0x0Fu);
                const int64_t rel32 = static_cast<int64_t>(target) - static_cast<int64_t>(tramp_ip + 6);
                if (force_near || fits_i32(rel32)) {
                    if (write_output) {
                        out_bytes.push_back(0x0Fu);
                        out_bytes.push_back(static_cast<uint8_t>(0x80u | cc));
                        emit_u32(out_bytes, static_cast<uint32_t>(static_cast<int32_t>(rel32)));
                    }
                    cursor += 6;
                } else {
                    if (write_output) {
                        // j!cc short +14 ; jmp qword [rip+0] ; <u64 target>
                        out_bytes.push_back(static_cast<uint8_t>(0x70u | (cc ^ 1u)));
                        out_bytes.push_back(14u);
                        emit_jmp_abs64(out_bytes, target);
                    }
                    cursor += 16;
                }
            } else if (op >= 0xE0u && op <= 0xE3u) {
                const int64_t rel8 = static_cast<int64_t>(target) - static_cast<int64_t>(tramp_ip + 2);
                const int64_t rel32 = static_cast<int64_t>(target) - static_cast<int64_t>(tramp_ip + 9);
                if (fits_i8(rel8) && !internal_insn && !si.internal_target) {
                    if (write_output) {
                        out_bytes.push_back(op);
                        out_bytes.push_back(static_cast<uint8_t>(static_cast<int8_t>(rel8)));
                    }
                    cursor += 2;
                } else if (force_near || fits_i32(rel32)) {
                    if (write_output) {
                        out_bytes.push_back(op);
                        out_bytes.push_back(0x02u);
                        out_bytes.push_back(0xEBu);
                        out_bytes.push_back(0x05u);
                        out_bytes.push_back(0xE9u);
                        emit_u32(out_bytes, static_cast<uint32_t>(static_cast<int32_t>(rel32)));
                    }
                    cursor += 9;
                } else {
                    if (write_output) {
                        out_bytes.push_back(op);
                        out_bytes.push_back(0x02u);
                        out_bytes.push_back(0xEBu);
                        out_bytes.push_back(0x0Eu);
                        emit_jmp_abs64(out_bytes, target);
                    }
                    cursor += 18;
                }
            } else {
                return false;
            }
        }
        return true;
    };

    if (!encode_all(false)) {
        return false;
    }
    if (!encode_all(true)) {
        return false;
    }

    // Trailing jump back to orig_base + stolen_len
    emit_jmp_abs64(out_bytes, orig_base + stolen_len);
    return true;
}

HookEngine::~HookEngine() {
    remove_all();
}

void* HookEngine::alloc_exec_page(size_t size) {
#if defined(_WIN32)
    return VirtualAlloc(nullptr, size, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
#else
    void* p = mmap(nullptr, size, PROT_READ | PROT_WRITE | PROT_EXEC, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    return (p == MAP_FAILED) ? nullptr : p;
#endif
}

void* HookEngine::alloc_exec_near(uint64_t target_address, size_t size) {
    if (target_address == 0 || size == 0) {
        return alloc_exec_page(size);
    }

#if defined(_WIN32)
    SYSTEM_INFO si{};
    GetSystemInfo(&si);
    const uint64_t gran = si.dwAllocationGranularity ? si.dwAllocationGranularity : 65536ull;
    const uint64_t min_user = reinterpret_cast<uint64_t>(si.lpMinimumApplicationAddress);
    const uint64_t max_user = reinterpret_cast<uint64_t>(si.lpMaximumApplicationAddress);

    const uint64_t lo = (target_address > kMaxNearDelta + min_user) ? (target_address - kMaxNearDelta) : min_user;
    const uint64_t hi = (target_address + kMaxNearDelta < max_user) ? (target_address + kMaxNearDelta) : max_user;

    auto try_range = [&](uint64_t start_addr, uint64_t end_addr) -> void* {
        uint64_t cur = (start_addr + gran - 1) & ~(gran - 1);
        while (cur < end_addr) {
            MEMORY_BASIC_INFORMATION mbi{};
            if (VirtualQuery(reinterpret_cast<LPCVOID>(static_cast<uintptr_t>(cur)), &mbi, sizeof(mbi)) == 0) {
                break;
            }
            const uint64_t reg_base = reinterpret_cast<uint64_t>(mbi.BaseAddress);
            const uint64_t reg_end = reg_base + static_cast<uint64_t>(mbi.RegionSize);
            if (mbi.State == MEM_FREE) {
                const uint64_t aligned = (reg_base + gran - 1) & ~(gran - 1);
                if (aligned >= lo && aligned + size <= reg_end && aligned + size <= hi) {
                    void* p = VirtualAlloc(
                        reinterpret_cast<LPVOID>(static_cast<uintptr_t>(aligned)),
                        size,
                        MEM_COMMIT | MEM_RESERVE,
                        PAGE_EXECUTE_READWRITE);
                    if (p) return p;
                }
            }
            if (reg_end <= cur) break;
            cur = (reg_end + gran - 1) & ~(gran - 1);
        }
        return nullptr;
    };

    if (void* p = try_range(target_address, hi)) return p;
    if (void* p = try_range(lo, target_address)) return p;
    return alloc_exec_page(size);
#else
    const long ps_raw = sysconf(_SC_PAGESIZE);
    const uint64_t ps = (ps_raw > 0) ? static_cast<uint64_t>(ps_raw) : 4096ull;
    const uint64_t lo = (target_address > kMaxNearDelta + ps) ? (target_address - kMaxNearDelta) : ps;
    const uint64_t hi = target_address + kMaxNearDelta;

    auto is_near = [&](void* p) -> bool {
        if (!p || p == MAP_FAILED) return false;
        const uint64_t addr = reinterpret_cast<uint64_t>(p);
        const uint64_t diff = (addr >= target_address) ? (addr - target_address) : (target_address - addr);
        return diff <= kMaxNearDelta;
    };

    // Fast attempt with mmap hint just above target_address
    const uint64_t hint_addr = (target_address + ps) & ~(ps - 1);
    void* hinted = mmap(
        reinterpret_cast<void*>(static_cast<uintptr_t>(hint_addr)),
        size,
        PROT_READ | PROT_WRITE | PROT_EXEC,
        MAP_PRIVATE | MAP_ANONYMOUS,
        -1,
        0);
    if (is_near(hinted)) {
        return hinted;
    }
    if (hinted && hinted != MAP_FAILED) {
        munmap(hinted, size);
    }

    // Parse /proc/self/maps for an unmapped gap within +-2GB
    std::ifstream maps("/proc/self/maps");
    if (maps) {
        std::vector<std::pair<uint64_t, uint64_t>> regions;
        std::string line;
        while (std::getline(maps, line)) {
            uint64_t s = 0, e = 0;
            if (std::sscanf(line.c_str(), "%llx-%llx",
                            reinterpret_cast<unsigned long long*>(&s),
                            reinterpret_cast<unsigned long long*>(&e)) == 2) {
                regions.emplace_back(s, e);
            }
        }
        std::sort(regions.begin(), regions.end());

        uint64_t prev_end = lo & ~(ps - 1);
        for (const auto& r : regions) {
            if (r.first > prev_end) {
                const uint64_t gap_start = std::max(prev_end, lo & ~(ps - 1));
                const uint64_t gap_end = std::min(r.first, hi);
                if (gap_end > gap_start && gap_end - gap_start >= size) {
#ifdef MAP_FIXED_NOREPLACE
                    constexpr int kFlags = MAP_PRIVATE | MAP_ANONYMOUS | MAP_FIXED_NOREPLACE;
#else
                    constexpr int kFlags = MAP_PRIVATE | MAP_ANONYMOUS;
#endif
                    void* p = mmap(
                        reinterpret_cast<void*>(static_cast<uintptr_t>(gap_start)),
                        size,
                        PROT_READ | PROT_WRITE | PROT_EXEC,
                        kFlags,
                        -1,
                        0);
                    if (is_near(p)) {
                        return p;
                    }
                    if (p && p != MAP_FAILED) {
                        munmap(p, size);
                    }
                }
            }
            if (r.second > prev_end) {
                prev_end = (r.second + ps - 1) & ~(ps - 1);
            }
        }
    }

    return alloc_exec_page(size);
#endif
}

void HookEngine::free_exec_page(void* ptr, size_t size) {
    if (!ptr) return;
#if defined(_WIN32)
    (void)size;
    VirtualFree(ptr, 0, MEM_RELEASE);
#else
    munmap(ptr, size);
#endif
}

bool HookEngine::write_protected_memory(void* dst, const void* src, size_t len) {
#if defined(_WIN32)
    DWORD old_protect = 0;
    if (!VirtualProtect(dst, len, PAGE_EXECUTE_READWRITE, &old_protect)) {
        return false;
    }
    if (len == sizeof(uint64_t) && (reinterpret_cast<uintptr_t>(dst) & 7u) == 0u) {
        uint64_t v = 0;
        std::memcpy(&v, src, sizeof(v));
        __atomic_store_n(static_cast<uint64_t*>(dst), v, __ATOMIC_RELEASE);
    } else {
        std::memcpy(dst, src, len);
    }
    FlushInstructionCache(GetCurrentProcess(), dst, len);
    DWORD ignored = 0;
    VirtualProtect(dst, len, old_protect, &ignored);
    return true;
#else
    const long page_size = sysconf(_SC_PAGESIZE);
    if (page_size <= 0) return false;
    const uintptr_t start = reinterpret_cast<uintptr_t>(dst) & ~static_cast<uintptr_t>(page_size - 1);
    const uintptr_t end = reinterpret_cast<uintptr_t>(dst) + len;
    const size_t span = static_cast<size_t>(end - start);
    if (mprotect(reinterpret_cast<void*>(start), span, PROT_READ | PROT_WRITE | PROT_EXEC) != 0) {
        return false;
    }
    if (len == sizeof(uint64_t) && (reinterpret_cast<uintptr_t>(dst) & 7u) == 0u) {
        uint64_t v = 0;
        std::memcpy(&v, src, sizeof(v));
        __atomic_store_n(static_cast<uint64_t*>(dst), v, __ATOMIC_RELEASE);
    } else {
        std::memcpy(dst, src, len);
    }
    __builtin___clear_cache(static_cast<char*>(dst), static_cast<char*>(dst) + len);
    return true;
#endif
}

void HookEngine::emit_abs_jmp_x64(uint8_t* dst, uint64_t target) {
    dst[0] = 0xFF;
    dst[1] = 0x25;
    dst[2] = 0x00;
    dst[3] = 0x00;
    dst[4] = 0x00;
    dst[5] = 0x00;
    std::memcpy(dst + 6, &target, sizeof(target));
}

void HookEngine::emit_aligned_indirect_jmp_x64(uint8_t* dst, uint64_t target) {
    // jmp qword ptr [rip+2] ; int3 ; int3 ; <aligned u64 target at +8>
    dst[0] = 0xFF;
    dst[1] = 0x25;
    dst[2] = 0x02;
    dst[3] = 0x00;
    dst[4] = 0x00;
    dst[5] = 0x00;
    dst[6] = 0xCC;
    dst[7] = 0xCC;
    std::memcpy(dst + 8, &target, sizeof(target));
}

void HookEngine::update_indirect_slot(void* stub16, uint64_t target) {
    if (!stub16) return;
    auto* bytes = static_cast<uint8_t*>(stub16);
    __atomic_store_n(reinterpret_cast<uint64_t*>(bytes + 8), target, __ATOMIC_RELEASE);
}

uint64_t HookEngine::make_chain_key(nm_hook_kind_t kind, uint64_t target_address) {
    return target_address ^ ((kind == NM_HOOK_VTABLE_SLOT) ? 0x9E3779B97F4A7C15ull : 0ull);
}

void HookEngine::dispatch_pre(TargetChain* chain, nm_hook_context_t* ctx) {
    if (!chain || !ctx) return;

    std::vector<ChainNode> snapshot;
    {
        std::lock_guard<std::mutex> lock(chain->chain_mutex);
        snapshot = chain->pre_hooks;
    }

    for (const auto& node : snapshot) {
        if (!node.cb) continue;
        node.cb(ctx, node.user_data);
    }
}

void HookEngine::dispatch_post(TargetChain* chain, nm_hook_context_t* ctx) {
    if (!chain || !ctx) return;

    std::vector<ChainNode> snapshot;
    {
        std::lock_guard<std::mutex> lock(chain->chain_mutex);
        snapshot = chain->post_hooks;
    }

    for (const auto& node : snapshot) {
        if (!node.cb) continue;
        node.cb(ctx, node.user_data);
    }
}

void HookEngine::dispatch_chain_body(TargetChain* chain, nm_hook_context_t* ctx) {
    struct ActiveCallGuard {
        TargetChain* c;
        ~ActiveCallGuard() {
            if (c) {
                c->active_calls.fetch_sub(1, std::memory_order_acq_rel);
            }
        }
    } guard{chain};

    if (!chain || !ctx) {
        return;
    }

    dispatch_pre(chain, ctx);
    if (!ctx->skip_original && chain->original_callable != nullptr) {
#if (defined(_WIN32) && !defined(_MSC_VER) && defined(__x86_64__)) || (!defined(_WIN32) && defined(__x86_64__))
        nm_hook_invoke_original(chain->original_callable, ctx);
#elif defined(_WIN32) && defined(_MSC_VER)
        using OrigFn = uint64_t(NM_CALL*)(uint64_t, uint64_t, uint64_t, uint64_t, uint64_t, uint64_t, uint64_t, uint64_t);
        auto fn = reinterpret_cast<OrigFn>(chain->original_callable);
        ctx->return_value = fn(
            ctx->args[0], ctx->args[1], ctx->args[2], ctx->args[3],
            ctx->args[4], ctx->args[5], ctx->args[6], ctx->args[7]);
#endif
    }
    dispatch_post(chain, ctx);
}

void* HookEngine::build_dispatcher_stub(TargetChain* chain, size_t& out_size) {
    out_size = 4096;
    void* page = alloc_exec_page(out_size);
    if (!page) return nullptr;

    static_assert(sizeof(std::atomic<uint32_t>) == sizeof(uint32_t), "atomic<uint32_t> must be 4 bytes");
    static_assert(std::atomic<uint32_t>::is_always_lock_free, "atomic<uint32_t> must be lock-free");

    std::vector<uint8_t> code;
    code.reserve(512);

    const uint64_t chain_ptr = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(chain));
    const ptrdiff_t active_calls_diff =
        reinterpret_cast<const uint8_t*>(&chain->active_calls) - reinterpret_cast<const uint8_t*>(chain);
    const int32_t active_calls_off = static_cast<int32_t>(active_calls_diff);
    const uint64_t body_addr = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(&HookEngine::dispatch_chain_body));

    auto push_bytes = [&](std::initializer_list<uint8_t> bs) {
        code.insert(code.end(), bs.begin(), bs.end());
    };
    auto emit_mov_rax_u64 = [&](uint64_t v) {
        code.push_back(0x48u); code.push_back(0xB8u);
        emit_u64(code, v);
    };
    auto emit_mov_rbx_u64 = [&](uint64_t v) {
        code.push_back(0x48u); code.push_back(0xBBu);
        emit_u64(code, v);
    };

#if defined(_WIN32)
    static_assert(sizeof(Win64RuntimeFunction) == sizeof(RUNTIME_FUNCTION), "RUNTIME_FUNCTION size mismatch");
    // Win64 ABI dispatcher stub (Prolog = 8 bytes: push rbx ; sub rsp, 0xB0)
    // Stack layout inside stub:
    //   [rsp+0x00..0x1F]: 32B shadow space for dispatch_chain_body
    //   [rsp+0x20..0x9F]: nm_hook_context_t ctx (0x80 bytes)
    //   [rsp+0xE0..0xF8]: caller stack args 4..7 (0xB0 + 0x08 rbx + 0x08 ret + 0x20 shadow)
    push_bytes({0x53});
    push_bytes({0x48, 0x81, 0xEC, 0xB0, 0x00, 0x00, 0x00});

    // mov rbx, chain_ptr ; lock add dword ptr [rbx + active_calls_off], 1
    emit_mov_rbx_u64(chain_ptr);
    push_bytes({0xF0, 0x83, 0x83});
    emit_u32(code, static_cast<uint32_t>(active_calls_off));
    code.push_back(0x01u);

    // Save rcx, rdx, r8, r9 -> ctx.args[0..3] ([rsp+0x20..0x38])
    push_bytes({0x48, 0x89, 0x4C, 0x24, 0x20});
    push_bytes({0x48, 0x89, 0x54, 0x24, 0x28});
    push_bytes({0x4C, 0x89, 0x44, 0x24, 0x30});
    push_bytes({0x4C, 0x89, 0x4C, 0x24, 0x38});

    // Copy caller stack args 4..7 ([rsp+0xE0..0xF8]) -> ctx.args[4..7] ([rsp+0x40..0x58])
    push_bytes({0x48, 0x8B, 0x84, 0x24, 0xE0, 0x00, 0x00, 0x00, 0x48, 0x89, 0x44, 0x24, 0x40});
    push_bytes({0x48, 0x8B, 0x84, 0x24, 0xE8, 0x00, 0x00, 0x00, 0x48, 0x89, 0x44, 0x24, 0x48});
    push_bytes({0x48, 0x8B, 0x84, 0x24, 0xF0, 0x00, 0x00, 0x00, 0x48, 0x89, 0x44, 0x24, 0x50});
    push_bytes({0x48, 0x8B, 0x84, 0x24, 0xF8, 0x00, 0x00, 0x00, 0x48, 0x89, 0x44, 0x24, 0x58});

    // Save xmm0..xmm3 -> ctx.float_args[0..3] ([rsp+0x60..0x78])
    push_bytes({0x66, 0x0F, 0xD6, 0x44, 0x24, 0x60});
    push_bytes({0x66, 0x0F, 0xD6, 0x4C, 0x24, 0x68});
    push_bytes({0x66, 0x0F, 0xD6, 0x54, 0x24, 0x70});
    push_bytes({0x66, 0x0F, 0xD6, 0x5C, 0x24, 0x78});

    // Zero return_value (0x80), return_float (0x88), skip_original+reserved (0x98)
    push_bytes({0x33, 0xC0});
    push_bytes({0x48, 0x89, 0x84, 0x24, 0x80, 0x00, 0x00, 0x00});
    push_bytes({0x48, 0x89, 0x84, 0x24, 0x88, 0x00, 0x00, 0x00});
    push_bytes({0x48, 0x89, 0x84, 0x24, 0x98, 0x00, 0x00, 0x00});

    // ctx.target_address (0x90)
    emit_mov_rax_u64(chain->target_address);
    push_bytes({0x48, 0x89, 0x84, 0x24, 0x90, 0x00, 0x00, 0x00});

    // dispatch_chain_body(rcx=chain, rdx=&ctx) — ActiveCallGuard decrements active_calls on return or unwind
    push_bytes({0x48, 0x89, 0xD9});             // mov rcx, rbx
    push_bytes({0x48, 0x8D, 0x54, 0x24, 0x20}); // lea rdx, [rsp+0x20]
    emit_mov_rax_u64(body_addr);
    push_bytes({0xFF, 0xD0});                   // call rax

    // Restore RAX, XMM0
    push_bytes({0x48, 0x8B, 0x84, 0x24, 0x80, 0x00, 0x00, 0x00});
    push_bytes({0xF3, 0x0F, 0x7E, 0x84, 0x24, 0x88, 0x00, 0x00, 0x00});

    push_bytes({0x48, 0x81, 0xC4, 0xB0, 0x00, 0x00, 0x00});
    push_bytes({0x5B, 0xC3});

    std::memcpy(page, code.data(), code.size());

    // Emit Win64 UNWIND_INFO at offset 0x200 on the same executable page and register RUNTIME_FUNCTION
    constexpr uint32_t kUnwindOff = 0x200u;
    auto* uw = static_cast<uint8_t*>(page) + kUnwindOff;
    uw[0] = 0x01u; // Version 1, Flags 0
    uw[1] = 0x08u; // SizeOfProlog = 8 (1B push rbx + 7B sub rsp, 0xB0)
    uw[2] = 0x03u; // CountOfCodes = 3
    uw[3] = 0x00u; // FrameRegister = 0, FrameOffset = 0
    const uint16_t codes[4] = {
        0x0108u, // offset 8: UWOP_ALLOC_LARGE (op=1, info=0)
        0x0016u, // 0xB0 / 8 = 22 (0x16)
        0x3001u, // offset 1: UWOP_PUSH_NONVOL (op=0, reg=3 RBX)
        0x0000u, // alignment padding
    };
    std::memcpy(uw + 4, codes, sizeof(codes));

    chain->win64_rt_fn.begin_address = 0u;
    chain->win64_rt_fn.end_address = static_cast<uint32_t>(code.size());
    chain->win64_rt_fn.unwind_data = kUnwindOff;
    if (RtlAddFunctionTable(
            reinterpret_cast<PRUNTIME_FUNCTION>(&chain->win64_rt_fn),
            1u,
            reinterpret_cast<DWORD64>(page))) {
        chain->win64_rt_registered = true;
    }
    FlushInstructionCache(GetCurrentProcess(), page, code.size());
#else
    // SysV x64 ABI dispatcher stub (Prolog = 8 bytes: push rbx ; sub rsp, 0x90)
    // Stack layout inside stub:
    //   [rsp+0x00..0x7F]: nm_hook_context_t ctx (0x80 bytes)
    //   [rsp+0xA0..0xA8]: caller stack args 6..7 (0x90 + 0x08 rbx + 0x08 ret)
    push_bytes({0x53});
    push_bytes({0x48, 0x81, 0xEC, 0x90, 0x00, 0x00, 0x00});

    // mov rbx, chain_ptr ; lock add dword ptr [rbx + active_calls_off], 1
    emit_mov_rbx_u64(chain_ptr);
    push_bytes({0xF0, 0x83, 0x83});
    emit_u32(code, static_cast<uint32_t>(active_calls_off));
    code.push_back(0x01u);

    // Save rdi, rsi, rdx, rcx, r8, r9 -> ctx.args[0..5] ([rsp+0x00..0x28])
    push_bytes({0x48, 0x89, 0x3C, 0x24});
    push_bytes({0x48, 0x89, 0x74, 0x24, 0x08});
    push_bytes({0x48, 0x89, 0x54, 0x24, 0x10});
    push_bytes({0x48, 0x89, 0x4C, 0x24, 0x18});
    push_bytes({0x4C, 0x89, 0x44, 0x24, 0x20});
    push_bytes({0x4C, 0x89, 0x4C, 0x24, 0x28});

    // Copy caller stack args 6..7 ([rsp+0xA0..0xA8]) -> ctx.args[6..7] ([rsp+0x30..0x38])
    push_bytes({0x48, 0x8B, 0x84, 0x24, 0xA0, 0x00, 0x00, 0x00, 0x48, 0x89, 0x44, 0x24, 0x30});
    push_bytes({0x48, 0x8B, 0x84, 0x24, 0xA8, 0x00, 0x00, 0x00, 0x48, 0x89, 0x44, 0x24, 0x38});

    // Save xmm0..xmm3 -> ctx.float_args[0..3] ([rsp+0x40..0x58])
    push_bytes({0x66, 0x0F, 0xD6, 0x44, 0x24, 0x40});
    push_bytes({0x66, 0x0F, 0xD6, 0x4C, 0x24, 0x48});
    push_bytes({0x66, 0x0F, 0xD6, 0x54, 0x24, 0x50});
    push_bytes({0x66, 0x0F, 0xD6, 0x5C, 0x24, 0x58});

    // Zero return_value (0x60), return_float (0x68), skip_original+reserved (0x78)
    push_bytes({0x33, 0xC0});
    push_bytes({0x48, 0x89, 0x44, 0x24, 0x60});
    push_bytes({0x48, 0x89, 0x44, 0x24, 0x68});
    push_bytes({0x48, 0x89, 0x44, 0x24, 0x78});

    // ctx.target_address (0x70)
    emit_mov_rax_u64(chain->target_address);
    push_bytes({0x48, 0x89, 0x44, 0x24, 0x70});

    // dispatch_chain_body(rdi=chain, rsi=&ctx) — ActiveCallGuard decrements active_calls on return or unwind
    push_bytes({0x48, 0x89, 0xDF}); // mov rdi, rbx
    push_bytes({0x48, 0x89, 0xE6}); // mov rsi, rsp
    emit_mov_rax_u64(body_addr);
    push_bytes({0xFF, 0xD0});

    // Restore RAX, XMM0
    push_bytes({0x48, 0x8B, 0x44, 0x24, 0x60});
    push_bytes({0xF3, 0x0F, 0x7E, 0x44, 0x24, 0x68});

    push_bytes({0x48, 0x81, 0xC4, 0x90, 0x00, 0x00, 0x00});
    push_bytes({0x5B, 0xC3});

    std::memcpy(page, code.data(), code.size());

    // Emit DWARF .eh_frame CIE + FDE + 0 terminator at offset 0x200 on the same page
    constexpr size_t kEhOff = 0x200u;
    uint8_t* eh = static_cast<uint8_t*>(page) + kEhOff;
    size_t ep = 0;

    // CIE
    const size_t cie_start = ep;
    ep += 4; // length placeholder
    const uint32_t cie_id = 0;
    std::memcpy(eh + ep, &cie_id, 4); ep += 4;
    eh[ep++] = 1u;                                     // version
    eh[ep++] = 'z'; eh[ep++] = 'R'; eh[ep++] = 0u;     // augmentation "zR"
    eh[ep++] = 1u;                                     // code_alignment_factor = 1
    eh[ep++] = 0x78u;                                  // data_alignment_factor = -8 (SLEB128)
    eh[ep++] = 16u;                                    // return_address_register = RIP (16)
    eh[ep++] = 1u;                                     // augmentation_data_len = 1
    eh[ep++] = 0x00u;                                  // DW_EH_PE_absptr
    eh[ep++] = 0x0Cu; eh[ep++] = 7u; eh[ep++] = 8u;    // DW_CFA_def_cfa rsp, 8
    eh[ep++] = 0x90u; eh[ep++] = 1u;                   // DW_CFA_offset r16, 1 (-8)
    while ((ep - cie_start) % 8u != 0u) eh[ep++] = 0x00u;
    const uint32_t cie_len = static_cast<uint32_t>(ep - cie_start - 4u);
    std::memcpy(eh + cie_start, &cie_len, 4);

    // FDE
    const size_t fde_start = ep;
    ep += 4; // length placeholder
    const uint32_t cie_ptr = static_cast<uint32_t>(ep - cie_start);
    std::memcpy(eh + ep, &cie_ptr, 4); ep += 4;
    const uint64_t pc_begin = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(page));
    const uint64_t pc_range = static_cast<uint64_t>(code.size());
    std::memcpy(eh + ep, &pc_begin, 8); ep += 8;
    std::memcpy(eh + ep, &pc_range, 8); ep += 8;
    eh[ep++] = 0u;                                     // augmentation_data_len = 0
    eh[ep++] = 0x41u;                                  // DW_CFA_advance_loc 1 (past push rbx)
    eh[ep++] = 0x0Eu; eh[ep++] = 16u;                  // DW_CFA_def_cfa_offset 16
    eh[ep++] = 0x83u; eh[ep++] = 2u;                   // DW_CFA_offset r3 (rbx), 2 (-16)
    eh[ep++] = 0x47u;                                  // DW_CFA_advance_loc 7 (past sub rsp, 0x90)
    eh[ep++] = 0x0Eu; eh[ep++] = 0xA0u; eh[ep++] = 0x01u; // DW_CFA_def_cfa_offset 0xA0 (160)
    while ((ep - fde_start) % 8u != 0u) eh[ep++] = 0x00u;
    const uint32_t fde_len = static_cast<uint32_t>(ep - fde_start - 4u);
    std::memcpy(eh + fde_start, &fde_len, 4);

    const uint32_t terminator = 0u;
    std::memcpy(eh + ep, &terminator, 4);

    chain->eh_frame_fde = eh + fde_start;
    __register_frame(chain->eh_frame_fde);
    __builtin___clear_cache(static_cast<char*>(page), static_cast<char*>(page) + code.size());
#endif
    return page;
}

void HookEngine::sweep_retired_chains_locked(bool force_quiescent) {
    const auto now = std::chrono::steady_clock::now();
    constexpr auto kGracePeriod = std::chrono::milliseconds(50);
    auto it = retired_chains_.begin();
    while (it != retired_chains_.end()) {
        TargetChain* c = it->chain;
        const bool grace_elapsed = force_quiescent || ((now - it->retired_at) >= kGracePeriod);
        if (grace_elapsed && c->active_calls.load(std::memory_order_acquire) == 0) {
            free_chain_pages(c);
            delete c;
            it = retired_chains_.erase(it);
        } else {
            ++it;
        }
    }
}

void HookEngine::sweep_retired_stubs_locked(bool force_quiescent) {
    const auto now = std::chrono::steady_clock::now();
    constexpr auto kGracePeriod = std::chrono::milliseconds(50);
    auto it = retired_stubs_.begin();
    while (it != retired_stubs_.end()) {
        const bool grace_elapsed = force_quiescent || ((now - it->retired_at) >= kGracePeriod);
        if (grace_elapsed) {
            if (it->ptr) {
                free_exec_page(it->ptr, it->size);
            }
            it = retired_stubs_.erase(it);
        } else {
            ++it;
        }
    }
}

void HookEngine::free_chain_pages(TargetChain* chain) {
    if (!chain) return;
    for (auto& rd : chain->raw_detours) {
        if (rd.next_stub) {
            free_exec_page(rd.next_stub, rd.next_stub_size);
            rd.next_stub = nullptr;
        }
    }
#if defined(_WIN32)
    if (chain->win64_rt_registered) {
        RtlDeleteFunctionTable(reinterpret_cast<PRUNTIME_FUNCTION>(&chain->win64_rt_fn));
        chain->win64_rt_registered = false;
    }
#else
    if (chain->eh_frame_fde) {
        __deregister_frame(chain->eh_frame_fde);
        chain->eh_frame_fde = nullptr;
    }
#endif
    if (chain->dispatcher_page) {
        free_exec_page(chain->dispatcher_page, chain->dispatcher_size);
        chain->dispatcher_page = nullptr;
    }
    if (chain->head_stub_page) {
        free_exec_page(chain->head_stub_page, chain->head_stub_size);
        chain->head_stub_page = nullptr;
    }
    if (chain->trampoline_page) {
        free_exec_page(chain->trampoline_page, chain->trampoline_page_size);
        chain->trampoline_page = nullptr;
    }
}

HookEngine::TargetChain* HookEngine::get_or_create_inline_chain_locked(
    uint64_t    target_address,
    const char* expected_bytes_hex,
    nm_status_t& out_status) {
    out_status = NM_OK;
    const uint64_t key = make_chain_key(NM_HOOK_INLINE_DETOUR, target_address);
    auto it = chains_.find(key);
    if (it != chains_.end()) {
        if (expected_bytes_hex != nullptr && expected_bytes_hex[0] != '\0') {
            // Verify bytes [0 .. min(N, S)-1] against saved original_bytes (since the
            // stolen prologue at target_address is already patched) and any tail bytes
            // [S .. N-1] directly against live unpatched memory at target_address + S.
            const size_t hex_len = std::strlen(expected_bytes_hex);
            if (hex_len == 0 || (hex_len % 2) != 0) {
                out_status = NM_ERR_INVALID_ARG;
                return nullptr;
            }
            const size_t expected_len = hex_len / 2;
            const size_t stolen_len = it->second->original_bytes.size();
            const size_t prefix_len = std::min(expected_len, stolen_len);

            if (prefix_len > 0) {
                std::string prefix_hex;
                const char* prefix_pattern = expected_bytes_hex;
                if (expected_len > stolen_len) {
                    prefix_hex.assign(expected_bytes_hex, prefix_len * 2);
                    prefix_pattern = prefix_hex.c_str();
                }
                const nm_status_t prefix_st = verify_memory_bytes(
                    static_cast<uint64_t>(reinterpret_cast<uintptr_t>(it->second->original_bytes.data())),
                    prefix_pattern);
                if (prefix_st != NM_OK) {
                    out_status = prefix_st;
                    return nullptr;
                }
            }

            if (expected_len > stolen_len) {
                const nm_status_t tail_st = verify_memory_bytes(
                    target_address + stolen_len,
                    expected_bytes_hex + (stolen_len * 2));
                if (tail_st != NM_OK) {
                    out_status = tail_st;
                    return nullptr;
                }
            }
        }
        return it->second;
    }

    if (expected_bytes_hex != nullptr && expected_bytes_hex[0] != '\0') {
        const nm_status_t verify_st = verify_memory_bytes(target_address, expected_bytes_hex);
        if (verify_st != NM_OK) {
            out_status = verify_st;
            return nullptr;
        }
    }

    auto* target_ptr = reinterpret_cast<uint8_t*>(static_cast<uintptr_t>(target_address));
    const size_t stolen_len = compute_stolen_length(target_ptr, kAbsJumpSize);
    if (stolen_len < kAbsJumpSize) {
        out_status = NM_ERR_INVALID_ARG;
        return nullptr;
    }

    constexpr size_t kTrampPageSize = 4096;
    void* tramp = alloc_exec_near(target_address, kTrampPageSize);
    if (!tramp) {
        out_status = NM_ERR_ALLOC_FAILED;
        return nullptr;
    }

    std::vector<uint8_t> orig(stolen_len);
    std::memcpy(orig.data(), target_ptr, stolen_len);

    std::vector<uint8_t> relocated;
    if (!relocate_instructions(
            target_address,
            orig.data(),
            stolen_len,
            static_cast<uint64_t>(reinterpret_cast<uintptr_t>(tramp)),
            relocated) ||
        relocated.size() > kTrampPageSize) {
        free_exec_page(tramp, kTrampPageSize);
        out_status = NM_ERR_ALLOC_FAILED;
        return nullptr;
    }

    std::memcpy(tramp, relocated.data(), relocated.size());
#if defined(_WIN32)
    FlushInstructionCache(GetCurrentProcess(), tramp, relocated.size());
#else
    __builtin___clear_cache(static_cast<char*>(tramp), static_cast<char*>(tramp) + relocated.size());
#endif

    constexpr size_t kHeadStubSize = 4096;
    void* head_stub = alloc_exec_page(kHeadStubSize);
    if (!head_stub) {
        free_exec_page(tramp, kTrampPageSize);
        out_status = NM_ERR_ALLOC_FAILED;
        return nullptr;
    }
    emit_aligned_indirect_jmp_x64(
        static_cast<uint8_t*>(head_stub),
        static_cast<uint64_t>(reinterpret_cast<uintptr_t>(tramp)));

    auto* chain = new TargetChain();
    chain->kind = NM_HOOK_INLINE_DETOUR;
    chain->target_address = target_address;
    chain->original_bytes = std::move(orig);
    chain->trampoline_page = tramp;
    chain->trampoline_page_size = kTrampPageSize;
    chain->original_callable = tramp;
    chain->head_stub_page = head_stub;
    chain->head_stub_size = kHeadStubSize;

    // Patch target: 14-byte abs jump to head_stub + NOP padding
    std::vector<uint8_t> patch(stolen_len, 0x90u);
    emit_abs_jmp_x64(patch.data(), static_cast<uint64_t>(reinterpret_cast<uintptr_t>(head_stub)));
    if (!write_protected_memory(target_ptr, patch.data(), stolen_len)) {
        free_exec_page(head_stub, kHeadStubSize);
        free_exec_page(tramp, kTrampPageSize);
        delete chain;
        out_status = NM_ERR_ALLOC_FAILED;
        return nullptr;
    }

    chains_.emplace(key, chain);
    return chain;
}

HookEngine::TargetChain* HookEngine::get_or_create_vtable_chain_locked(
    void*       vtable_address,
    uint32_t    slot_index,
    nm_status_t& out_status) {
    out_status = NM_OK;
    auto** slots = static_cast<void**>(vtable_address);
    void** slot_ptr = &slots[slot_index];
    const uint64_t target_key = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(slot_ptr));
    const uint64_t key = make_chain_key(NM_HOOK_VTABLE_SLOT, target_key);

    auto it = chains_.find(key);
    if (it != chains_.end()) {
        return it->second;
    }

    constexpr size_t kHeadStubSize = 4096;
    void* head_stub = alloc_exec_page(kHeadStubSize);
    if (!head_stub) {
        out_status = NM_ERR_ALLOC_FAILED;
        return nullptr;
    }

    void* orig_fn = *slot_ptr;
    emit_aligned_indirect_jmp_x64(
        static_cast<uint8_t*>(head_stub),
        static_cast<uint64_t>(reinterpret_cast<uintptr_t>(orig_fn)));

    if (!write_protected_memory(slot_ptr, &head_stub, sizeof(void*))) {
        free_exec_page(head_stub, kHeadStubSize);
        out_status = NM_ERR_ALLOC_FAILED;
        return nullptr;
    }

    auto* chain = new TargetChain();
    chain->kind = NM_HOOK_VTABLE_SLOT;
    chain->target_address = target_key;
    chain->original_vtable_fn = orig_fn;
    chain->original_callable = orig_fn;
    chain->head_stub_page = head_stub;
    chain->head_stub_size = kHeadStubSize;

    chains_.emplace(key, chain);
    return chain;
}

void HookEngine::relink_chain_locked(TargetChain* chain) {
    if (!chain) return;

    bool has_chain_callbacks = false;
    {
        std::lock_guard<std::mutex> clock(chain->chain_mutex);
        has_chain_callbacks = !chain->pre_hooks.empty() || !chain->post_hooks.empty();
    }

    uint64_t tail_target = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(chain->original_callable));
    if (has_chain_callbacks) {
        if (!chain->dispatcher_page) {
            chain->dispatcher_page = build_dispatcher_stub(chain, chain->dispatcher_size);
        }
        if (chain->dispatcher_page) {
            tail_target = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(chain->dispatcher_page));
        }
    }

    // Link raw detours in reverse installation order: head -> latest -> ... -> oldest -> tail_target
    uint64_t next_target = tail_target;
    for (size_t i = 0; i < chain->raw_detours.size(); ++i) {
        auto& node = chain->raw_detours[i];
        update_indirect_slot(node.next_stub, next_target);
        next_target = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(node.detour_fn));
    }

    update_indirect_slot(chain->head_stub_page, next_target);
}

nm_status_t HookEngine::install_inline(
    uint64_t          target_address,
    const char*       expected_bytes_hex,
    void*             detour_fn,
    void**            out_trampoline,
    nm_hook_handle_t* out_handle) {
    if (target_address == 0 || detour_fn == nullptr || out_handle == nullptr) {
        return NM_ERR_INVALID_ARG;
    }

    std::lock_guard<std::mutex> lock(mutex_);
    sweep_retired_chains_locked(false);
    sweep_retired_stubs_locked(false);

    nm_status_t st = NM_OK;
    TargetChain* chain = get_or_create_inline_chain_locked(target_address, expected_bytes_hex, st);
    if (!chain) {
        return st;
    }

    constexpr size_t kNextStubSize = 4096;
    void* next_stub = alloc_exec_page(kNextStubSize);
    if (!next_stub) {
        if (chain->raw_detours.empty() && chain->pre_hooks.empty() && chain->post_hooks.empty()) {
            auto* target_ptr = reinterpret_cast<void*>(static_cast<uintptr_t>(chain->target_address));
            write_protected_memory(target_ptr, chain->original_bytes.data(), chain->original_bytes.size());
            chains_.erase(make_chain_key(NM_HOOK_INLINE_DETOUR, target_address));
            retired_chains_.push_back(RetiredChain{chain, std::chrono::steady_clock::now()});
        }
        return NM_ERR_ALLOC_FAILED;
    }
    emit_aligned_indirect_jmp_x64(
        static_cast<uint8_t*>(next_stub),
        static_cast<uint64_t>(reinterpret_cast<uintptr_t>(chain->original_callable)));

    const nm_hook_handle_t handle = next_handle_++;
    RawDetourNode node;
    node.handle = handle;
    node.detour_fn = detour_fn;
    node.next_stub = next_stub;
    node.next_stub_size = kNextStubSize;
    chain->raw_detours.push_back(node);

    relink_chain_locked(chain);
    handles_.emplace(handle, HandleLocation{NM_HOOK_INLINE_DETOUR, target_address, HandleLocation::RawDetour});

    if (out_trampoline) {
        *out_trampoline = next_stub;
    }
    *out_handle = handle;
    return NM_OK;
}

nm_status_t HookEngine::install_vtable(
    void*             vtable_address,
    uint32_t          slot_index,
    void*             detour_fn,
    void**            out_original_fn,
    nm_hook_handle_t* out_handle) {
    if (vtable_address == nullptr || detour_fn == nullptr || out_handle == nullptr) {
        return NM_ERR_INVALID_ARG;
    }

    auto** slots = static_cast<void**>(vtable_address);
    const uint64_t target_key = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(&slots[slot_index]));

    std::lock_guard<std::mutex> lock(mutex_);
    sweep_retired_chains_locked(false);
    sweep_retired_stubs_locked(false);

    nm_status_t st = NM_OK;
    TargetChain* chain = get_or_create_vtable_chain_locked(vtable_address, slot_index, st);
    if (!chain) {
        return st;
    }

    constexpr size_t kNextStubSize = 4096;
    void* next_stub = alloc_exec_page(kNextStubSize);
    if (!next_stub) {
        if (chain->raw_detours.empty() && chain->pre_hooks.empty() && chain->post_hooks.empty()) {
            auto* slot_ptr = reinterpret_cast<void**>(static_cast<uintptr_t>(chain->target_address));
            write_protected_memory(slot_ptr, &chain->original_vtable_fn, sizeof(void*));
            chains_.erase(make_chain_key(NM_HOOK_VTABLE_SLOT, target_key));
            retired_chains_.push_back(RetiredChain{chain, std::chrono::steady_clock::now()});
        }
        return NM_ERR_ALLOC_FAILED;
    }
    emit_aligned_indirect_jmp_x64(
        static_cast<uint8_t*>(next_stub),
        static_cast<uint64_t>(reinterpret_cast<uintptr_t>(chain->original_callable)));

    const nm_hook_handle_t handle = next_handle_++;
    RawDetourNode node;
    node.handle = handle;
    node.detour_fn = detour_fn;
    node.next_stub = next_stub;
    node.next_stub_size = kNextStubSize;
    chain->raw_detours.push_back(node);

    relink_chain_locked(chain);
    handles_.emplace(handle, HandleLocation{NM_HOOK_VTABLE_SLOT, target_key, HandleLocation::RawDetour});

    if (out_original_fn) {
        *out_original_fn = next_stub;
    }
    *out_handle = handle;
    return NM_OK;
}

nm_status_t HookEngine::install_inline_chain(
    uint64_t                 target_address,
    const char*              expected_bytes_hex,
    nm_hook_phase_t          phase,
    int32_t                  priority,
    nm_hook_chain_callback_t cb,
    void*                    user_data,
    nm_hook_handle_t*        out_handle) {
    if (target_address == 0 || cb == nullptr || out_handle == nullptr ||
        (phase != NM_HOOK_PHASE_PRE && phase != NM_HOOK_PHASE_POST)) {
        return NM_ERR_INVALID_ARG;
    }

    std::lock_guard<std::mutex> lock(mutex_);
    sweep_retired_chains_locked(false);
    sweep_retired_stubs_locked(false);

    nm_status_t st = NM_OK;
    TargetChain* chain = get_or_create_inline_chain_locked(target_address, expected_bytes_hex, st);
    if (!chain) {
        return st;
    }

    const nm_hook_handle_t handle = next_handle_++;
    ChainNode node{handle, priority, cb, user_data};
    {
        std::lock_guard<std::mutex> clock(chain->chain_mutex);
        auto& vec = (phase == NM_HOOK_PHASE_PRE) ? chain->pre_hooks : chain->post_hooks;
        vec.push_back(node);
        std::sort(vec.begin(), vec.end(), [](const ChainNode& a, const ChainNode& b) {
            if (a.priority != b.priority) return a.priority < b.priority;
            return a.handle < b.handle;
        });
    }

    relink_chain_locked(chain);
    if (!chain->dispatcher_page) {
        // Dispatcher alloc failed; roll back node
        {
            std::lock_guard<std::mutex> clock(chain->chain_mutex);
            auto& vec = (phase == NM_HOOK_PHASE_PRE) ? chain->pre_hooks : chain->post_hooks;
            vec.erase(std::remove_if(vec.begin(), vec.end(),
                                     [handle](const ChainNode& n) { return n.handle == handle; }),
                      vec.end());
        }
        if (chain->raw_detours.empty() && chain->pre_hooks.empty() && chain->post_hooks.empty()) {
            auto* target_ptr = reinterpret_cast<void*>(static_cast<uintptr_t>(chain->target_address));
            write_protected_memory(target_ptr, chain->original_bytes.data(), chain->original_bytes.size());
            chains_.erase(make_chain_key(NM_HOOK_INLINE_DETOUR, target_address));
            retired_chains_.push_back(RetiredChain{chain, std::chrono::steady_clock::now()});
        }
        return NM_ERR_ALLOC_FAILED;
    }

    handles_.emplace(
        handle,
        HandleLocation{
            NM_HOOK_INLINE_DETOUR,
            target_address,
            (phase == NM_HOOK_PHASE_PRE) ? HandleLocation::PreHook : HandleLocation::PostHook,
        });
    *out_handle = handle;
    return NM_OK;
}

nm_status_t HookEngine::install_vtable_chain(
    void*                    vtable_address,
    uint32_t                 slot_index,
    nm_hook_phase_t          phase,
    int32_t                  priority,
    nm_hook_chain_callback_t cb,
    void*                    user_data,
    nm_hook_handle_t*        out_handle) {
    if (vtable_address == nullptr || cb == nullptr || out_handle == nullptr ||
        (phase != NM_HOOK_PHASE_PRE && phase != NM_HOOK_PHASE_POST)) {
        return NM_ERR_INVALID_ARG;
    }

    auto** slots = static_cast<void**>(vtable_address);
    const uint64_t target_key = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(&slots[slot_index]));

    std::lock_guard<std::mutex> lock(mutex_);
    sweep_retired_chains_locked(false);
    sweep_retired_stubs_locked(false);

    nm_status_t st = NM_OK;
    TargetChain* chain = get_or_create_vtable_chain_locked(vtable_address, slot_index, st);
    if (!chain) {
        return st;
    }

    const nm_hook_handle_t handle = next_handle_++;
    ChainNode node{handle, priority, cb, user_data};
    {
        std::lock_guard<std::mutex> clock(chain->chain_mutex);
        auto& vec = (phase == NM_HOOK_PHASE_PRE) ? chain->pre_hooks : chain->post_hooks;
        vec.push_back(node);
        std::sort(vec.begin(), vec.end(), [](const ChainNode& a, const ChainNode& b) {
            if (a.priority != b.priority) return a.priority < b.priority;
            return a.handle < b.handle;
        });
    }

    relink_chain_locked(chain);
    if (!chain->dispatcher_page) {
        {
            std::lock_guard<std::mutex> clock(chain->chain_mutex);
            auto& vec = (phase == NM_HOOK_PHASE_PRE) ? chain->pre_hooks : chain->post_hooks;
            vec.erase(std::remove_if(vec.begin(), vec.end(),
                                     [handle](const ChainNode& n) { return n.handle == handle; }),
                      vec.end());
        }
        if (chain->raw_detours.empty() && chain->pre_hooks.empty() && chain->post_hooks.empty()) {
            auto* slot_ptr = reinterpret_cast<void**>(static_cast<uintptr_t>(chain->target_address));
            write_protected_memory(slot_ptr, &chain->original_vtable_fn, sizeof(void*));
            chains_.erase(make_chain_key(NM_HOOK_VTABLE_SLOT, target_key));
            retired_chains_.push_back(RetiredChain{chain, std::chrono::steady_clock::now()});
        }
        return NM_ERR_ALLOC_FAILED;
    }

    handles_.emplace(
        handle,
        HandleLocation{
            NM_HOOK_VTABLE_SLOT,
            target_key,
            (phase == NM_HOOK_PHASE_PRE) ? HandleLocation::PreHook : HandleLocation::PostHook,
        });
    *out_handle = handle;
    return NM_OK;
}

nm_status_t HookEngine::remove(nm_hook_handle_t handle) {
    if (handle == NM_HOOK_INVALID) {
        return NM_ERR_INVALID_ARG;
    }

    std::lock_guard<std::mutex> lock(mutex_);
    auto hit = handles_.find(handle);
    if (hit == handles_.end()) {
        return NM_ERR_HOOK_NOT_FOUND;
    }

    const HandleLocation loc = hit->second;
    handles_.erase(hit);

    const uint64_t key = make_chain_key(loc.kind, loc.target_address);
    auto cit = chains_.find(key);
    if (cit == chains_.end()) {
        return NM_ERR_HOOK_NOT_FOUND;
    }

    TargetChain* chain = cit->second;
    if (loc.sub_kind == HandleLocation::RawDetour) {
        for (auto it = chain->raw_detours.begin(); it != chain->raw_detours.end(); ++it) {
            if (it->handle == handle) {
                if (it->next_stub) {
                    retired_stubs_.push_back(RetiredStub{
                        it->next_stub,
                        it->next_stub_size,
                        std::chrono::steady_clock::now(),
                    });
                    it->next_stub = nullptr;
                    it->next_stub_size = 0;
                }
                chain->raw_detours.erase(it);
                break;
            }
        }
    } else {
        std::lock_guard<std::mutex> clock(chain->chain_mutex);
        auto& vec = (loc.sub_kind == HandleLocation::PreHook) ? chain->pre_hooks : chain->post_hooks;
        vec.erase(std::remove_if(vec.begin(), vec.end(),
                                 [handle](const ChainNode& n) { return n.handle == handle; }),
                  vec.end());
    }

    bool completely_empty = chain->raw_detours.empty();
    if (completely_empty) {
        std::lock_guard<std::mutex> clock(chain->chain_mutex);
        completely_empty = chain->pre_hooks.empty() && chain->post_hooks.empty();
    }

    if (!completely_empty) {
        relink_chain_locked(chain);
        sweep_retired_chains_locked(false);
        sweep_retired_stubs_locked(false);
        return NM_OK;
    }

    // Restore original target bytes / vtable slot
    if (chain->kind == NM_HOOK_INLINE_DETOUR) {
        auto* target_ptr = reinterpret_cast<void*>(static_cast<uintptr_t>(chain->target_address));
        write_protected_memory(target_ptr, chain->original_bytes.data(), chain->original_bytes.size());
    } else {
        auto* slot_ptr = reinterpret_cast<void**>(static_cast<uintptr_t>(chain->target_address));
        write_protected_memory(slot_ptr, &chain->original_vtable_fn, sizeof(void*));
    }

    // Never immediately free_chain_pages/delete chain: an in-flight thread may have
    // loaded head_stub or vtable[slot] right before entering dispatcher_page's lock add,
    // or just exited lock sub before ret.
    chains_.erase(cit);
    retired_chains_.push_back(RetiredChain{chain, std::chrono::steady_clock::now()});

    sweep_retired_chains_locked(false);
    sweep_retired_stubs_locked(false);
    return NM_OK;
}

void HookEngine::remove_all() {
    std::vector<nm_hook_handle_t> all;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        all.reserve(handles_.size());
        for (const auto& kv : handles_) {
            all.push_back(kv.first);
        }
    }
    for (nm_hook_handle_t h : all) {
        (void)remove(h);
    }
    std::lock_guard<std::mutex> lock(mutex_);
    sweep_retired_chains_locked(true);
    sweep_retired_stubs_locked(true);
}

uint32_t HookEngine::chain_active_calls(nm_hook_kind_t kind, uint64_t target_address) const {
    std::lock_guard<std::mutex> lock(mutex_);
    const uint64_t key = make_chain_key(kind, target_address);
    auto it = chains_.find(key);
    if (it != chains_.end() && it->second != nullptr) {
        return it->second->active_calls.load(std::memory_order_acquire);
    }
    return 0;
}

size_t HookEngine::retired_chain_count() const {
    std::lock_guard<std::mutex> lock(mutex_);
    return retired_chains_.size();
}

void HookEngine::sweep_retired_now(bool force_quiescent) {
    std::lock_guard<std::mutex> lock(mutex_);
    sweep_retired_chains_locked(force_quiescent);
    sweep_retired_stubs_locked(force_quiescent);
}

} // namespace nativemods
