#include "hook_engine.hpp"
#include "address_table.hpp"

#include <cstring>

#if defined(_WIN32)
#  define WIN32_LEAN_AND_MEAN
#  include <windows.h>
#else
#  include <sys/mman.h>
#  include <unistd.h>
#endif

namespace nativemods {

namespace {

// Minimal x86-64 instruction-length decoder covering standard Win64 / SysV function prologues
// (prefixes, REX, push/pop, mov/lea/add/sub/cmp/test/xor/and/or, movaps/movups, nop, etc.).
// Returns 0 if the instruction is not recognized so callers can safely fall back.
size_t decode_modrm_length(const uint8_t* p, size_t max_len) {
    if (max_len < 1) return 0;
    const uint8_t modrm = p[0];
    const uint8_t mod = (modrm >> 6) & 3u;
    const uint8_t rm  = modrm & 7u;
    size_t len = 1;
    if (mod == 3u) return len;

    bool has_sib = (rm == 4u);
    uint8_t sib_base = 0;
    if (has_sib) {
        if (len + 1 > max_len) return 0;
        sib_base = p[len] & 7u;
        ++len;
    }

    if (mod == 0u) {
        if (rm == 5u || (has_sib && sib_base == 5u)) {
            len += 4; // disp32 (RIP-rel or SIB base=5)
        }
    } else if (mod == 1u) {
        len += 1; // disp8
    } else if (mod == 2u) {
        len += 4; // disp32
    }
    return (len <= max_len) ? len : 0;
}

size_t decode_x64_insn_length(const uint8_t* p, size_t max_len) {
    if (!p || max_len == 0) return 0;
    size_t i = 0;
    bool has_66 = false;
    bool rex_w = false;

    // Legacy prefixes
    while (i < max_len) {
        const uint8_t b = p[i];
        if (b == 0x66) { has_66 = true; ++i; continue; }
        if (b == 0x67 || b == 0xF0 || b == 0xF2 || b == 0xF3 ||
            b == 0x2E || b == 0x3E || b == 0x26 || b == 0x36 || b == 0x64 || b == 0x65) {
            ++i;
            continue;
        }
        break;
    }

    // Optional REX prefix (0x40..0x4F)
    if (i < max_len && (p[i] & 0xF0u) == 0x40u) {
        rex_w = (p[i] & 0x08u) != 0;
        ++i;
    }
    if (i >= max_len) return 0;

    const uint8_t op = p[i++];

    // Single-byte opcodes without ModRM
    if (op == 0x90 || op == 0xC3 || op == 0xCC || (op >= 0x50 && op <= 0x5F)) {
        return i;
    }

    // 2-byte 0x0F opcodes (common in prologues: movaps/movups/movzx/movsx/nop)
    if (op == 0x0F) {
        if (i >= max_len) return 0;
        const uint8_t op2 = p[i++];
        if (op2 == 0x10 || op2 == 0x11 || op2 == 0x1F || // movups / multi-byte nop
            op2 == 0x28 || op2 == 0x29 ||                 // movaps
            op2 == 0xB6 || op2 == 0xB7 || op2 == 0xBE || op2 == 0xBF) { // movzx / movsx
            const size_t m = decode_modrm_length(p + i, max_len - i);
            return m ? (i + m) : 0;
        }
        return 0;
    }

    // Common ALU / MOV / LEA / TEST / CMP with ModRM and no immediate
    if ((op <= 0x3B && (op & 7u) <= 3u) ||
        op == 0x63 || op == 0x84 || op == 0x85 ||
        (op >= 0x88 && op <= 0x8B) || op == 0x8D) {
        const size_t m = decode_modrm_length(p + i, max_len - i);
        return m ? (i + m) : 0;
    }

    // Immediate ALU group 1 (0x80..0x83)
    if (op == 0x80 || op == 0x83) {
        const size_t m = decode_modrm_length(p + i, max_len - i);
        return (m && i + m + 1 <= max_len) ? (i + m + 1) : 0;
    }
    if (op == 0x81) {
        const size_t m = decode_modrm_length(p + i, max_len - i);
        const size_t imm = has_66 ? 2 : 4;
        return (m && i + m + imm <= max_len) ? (i + m + imm) : 0;
    }

    // MOV r, imm (0xB0..0xBF)
    if (op >= 0xB0 && op <= 0xB7) {
        return (i + 1 <= max_len) ? (i + 1) : 0;
    }
    if (op >= 0xB8 && op <= 0xBF) {
        const size_t imm = rex_w ? 8 : (has_66 ? 2 : 4);
        return (i + imm <= max_len) ? (i + imm) : 0;
    }

    // MOV r/m, imm (0xC6 / 0xC7)
    if (op == 0xC6) {
        const size_t m = decode_modrm_length(p + i, max_len - i);
        return (m && i + m + 1 <= max_len) ? (i + m + 1) : 0;
    }
    if (op == 0xC7) {
        const size_t m = decode_modrm_length(p + i, max_len - i);
        const size_t imm = has_66 ? 2 : 4;
        return (m && i + m + imm <= max_len) ? (i + m + imm) : 0;
    }

    return 0;
}

} // namespace

HookEngine::~HookEngine() {
    remove_all();
}

size_t HookEngine::compute_stolen_length(const uint8_t* code, size_t min_len) {
    if (!code) return min_len;
    constexpr size_t kMaxScan = 32;
    size_t offset = 0;
    while (offset < min_len && offset < kMaxScan) {
        const size_t insn_len = decode_x64_insn_length(code + offset, kMaxScan - offset);
        if (insn_len == 0) {
            return min_len; // Fallback for synthetic/unknown opcodes
        }
        offset += insn_len;
    }
    return (offset >= min_len && offset <= kMaxScan) ? offset : min_len;
}

void* HookEngine::alloc_exec_page(size_t size) {
#if defined(_WIN32)
    return VirtualAlloc(nullptr, size, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
#else
    void* p = mmap(nullptr, size, PROT_READ | PROT_WRITE | PROT_EXEC, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    return (p == MAP_FAILED) ? nullptr : p;
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
    std::memcpy(dst, src, len);
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
    std::memcpy(dst, src, len);
    __builtin___clear_cache(static_cast<char*>(dst), static_cast<char*>(dst) + len);
    return true;
#endif
}

void HookEngine::emit_abs_jmp_x64(uint8_t* dst, uint64_t target) {
    // x86-64: jmp qword ptr [rip + 0] ; <uint64 target>
    // Works across the full 64-bit address space regardless of ASLR/DYNAMIC_BASE distance.
    dst[0] = 0xFF;
    dst[1] = 0x25;
    dst[2] = 0x00;
    dst[3] = 0x00;
    dst[4] = 0x00;
    dst[5] = 0x00;
    std::memcpy(dst + 6, &target, sizeof(target));
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
    if (by_target_.find(target_address) != by_target_.end()) {
        return NM_ERR_HOOK_ALREADY_INSTALLED;
    }

    if (expected_bytes_hex != nullptr && expected_bytes_hex[0] != '\0') {
        const nm_status_t verify_st = verify_memory_bytes(target_address, expected_bytes_hex);
        if (verify_st != NM_OK) {
            return verify_st;
        }
    }

    constexpr size_t kTrampolinePageSize = 4096;
    void* tramp = alloc_exec_page(kTrampolinePageSize);
    if (!tramp) {
        return NM_ERR_ALLOC_FAILED;
    }

    auto* target_ptr = reinterpret_cast<uint8_t*>(static_cast<uintptr_t>(target_address));
    const size_t stolen_len = compute_stolen_length(target_ptr, kAbsJumpSize);

    std::vector<uint8_t> orig(stolen_len);
    std::memcpy(orig.data(), target_ptr, stolen_len);

    // Build trampoline: [stolen_len whole instructions] + [jmp back to target + stolen_len].
    auto* tramp_bytes = static_cast<uint8_t*>(tramp);
    std::memcpy(tramp_bytes, orig.data(), stolen_len);
    emit_abs_jmp_x64(tramp_bytes + stolen_len, target_address + stolen_len);

    // Patch target: 14-byte absolute jump + NOP padding for any remaining stolen bytes.
    std::vector<uint8_t> patch(stolen_len, 0x90u);
    emit_abs_jmp_x64(patch.data(), static_cast<uint64_t>(reinterpret_cast<uintptr_t>(detour_fn)));

    if (!write_protected_memory(target_ptr, patch.data(), stolen_len)) {
        free_exec_page(tramp, kTrampolinePageSize);
        return NM_ERR_ALLOC_FAILED;
    }

    const nm_hook_handle_t handle = next_handle_++;
    HookRecord rec;
    rec.handle = handle;
    rec.kind = NM_HOOK_INLINE_DETOUR;
    rec.target_address = target_address;
    rec.original_bytes = std::move(orig);
    rec.trampoline_page = tramp;
    rec.trampoline_page_size = kTrampolinePageSize;

    hooks_.emplace(handle, std::move(rec));
    by_target_.emplace(target_address, handle);

    if (out_trampoline) {
        *out_trampoline = tramp;
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
    void** slot_ptr = &slots[slot_index];
    const uint64_t target_key = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(slot_ptr));

    std::lock_guard<std::mutex> lock(mutex_);
    if (by_target_.find(target_key) != by_target_.end()) {
        return NM_ERR_HOOK_ALREADY_INSTALLED;
    }

    void* orig_fn = *slot_ptr;
    if (!write_protected_memory(slot_ptr, &detour_fn, sizeof(void*))) {
        return NM_ERR_ALLOC_FAILED;
    }

    const nm_hook_handle_t handle = next_handle_++;
    HookRecord rec;
    rec.handle = handle;
    rec.kind = NM_HOOK_VTABLE_SLOT;
    rec.target_address = target_key;
    rec.original_vtable_fn = orig_fn;

    hooks_.emplace(handle, std::move(rec));
    by_target_.emplace(target_key, handle);

    if (out_original_fn) {
        *out_original_fn = orig_fn;
    }
    *out_handle = handle;
    return NM_OK;
}

nm_status_t HookEngine::remove(nm_hook_handle_t handle) {
    if (handle == NM_HOOK_INVALID) {
        return NM_ERR_INVALID_ARG;
    }

    std::lock_guard<std::mutex> lock(mutex_);
    auto it = hooks_.find(handle);
    if (it == hooks_.end()) {
        return NM_ERR_HOOK_NOT_FOUND;
    }

    HookRecord rec = std::move(it->second);
    hooks_.erase(it);
    by_target_.erase(rec.target_address);

    if (rec.kind == NM_HOOK_INLINE_DETOUR) {
        auto* target_ptr = reinterpret_cast<void*>(static_cast<uintptr_t>(rec.target_address));
        write_protected_memory(target_ptr, rec.original_bytes.data(), rec.original_bytes.size());
        free_exec_page(rec.trampoline_page, rec.trampoline_page_size);
    } else if (rec.kind == NM_HOOK_VTABLE_SLOT) {
        auto* slot_ptr = reinterpret_cast<void**>(static_cast<uintptr_t>(rec.target_address));
        write_protected_memory(slot_ptr, &rec.original_vtable_fn, sizeof(void*));
    }
    return NM_OK;
}

void HookEngine::remove_all() {
    std::vector<nm_hook_handle_t> handles;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        handles.reserve(hooks_.size());
        for (const auto& kv : hooks_) {
            handles.push_back(kv.first);
        }
    }
    for (nm_hook_handle_t h : handles) {
        remove(h);
    }
}

} // namespace nativemods
