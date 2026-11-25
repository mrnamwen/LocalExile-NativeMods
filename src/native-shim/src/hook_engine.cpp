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
    std::vector<uint8_t> orig(kAbsJumpSize);
    std::memcpy(orig.data(), target_ptr, kAbsJumpSize);

    // Build trampoline: [stolen kAbsJumpSize bytes] + [jmp back to target + kAbsJumpSize].
    // Callers hooking sites with RIP-relative instructions inside the first 14 bytes must use
    // instruction-boundary-aligned prologues (or a dedicated relocator); our tests and standard
    // Win64 prologues use position-independent stack/register saves.
    auto* tramp_bytes = static_cast<uint8_t*>(tramp);
    std::memcpy(tramp_bytes, orig.data(), kAbsJumpSize);
    emit_abs_jmp_x64(tramp_bytes + kAbsJumpSize, target_address + kAbsJumpSize);

    uint8_t patch[kAbsJumpSize];
    emit_abs_jmp_x64(patch, static_cast<uint64_t>(reinterpret_cast<uintptr_t>(detour_fn)));

    if (!write_protected_memory(target_ptr, patch, kAbsJumpSize)) {
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
