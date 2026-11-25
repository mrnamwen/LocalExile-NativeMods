#pragma once

#include "nativemods_abi.h"

#include <cstddef>
#include <cstdint>
#include <mutex>
#include <unordered_map>
#include <vector>

namespace nativemods {

// Byte-verified inline x64 detour & vtable slot hook manager.
class HookEngine {
public:
    HookEngine() = default;
    ~HookEngine();

    HookEngine(const HookEngine&) = delete;
    HookEngine& operator=(const HookEngine&) = delete;

    nm_status_t install_inline(
        uint64_t          target_address,
        const char*       expected_bytes_hex,
        void*             detour_fn,
        void**            out_trampoline,
        nm_hook_handle_t* out_handle);

    nm_status_t install_vtable(
        void*             vtable_address,
        uint32_t          slot_index,
        void*             detour_fn,
        void**            out_original_fn,
        nm_hook_handle_t* out_handle);

    nm_status_t remove(nm_hook_handle_t handle);
    void        remove_all();

    static constexpr size_t kAbsJumpSize = 14; // FF 25 00 00 00 00 <u64 target>

    // Computes an instruction-boundary-aligned stolen byte count >= min_len for standard
    // x86-64 function prologues (returns min_len if an unrecognized opcode is encountered).
    static size_t compute_stolen_length(const uint8_t* code, size_t min_len = kAbsJumpSize);

    // Allocates/frees an executable page (also used by Win64 register-preserving frame stub).
    static void* alloc_exec_page(size_t size);
    static void  free_exec_page(void* ptr, size_t size);
    static void  emit_abs_jmp_x64(uint8_t* dst, uint64_t target);

private:
    struct HookRecord {
        nm_hook_handle_t     handle = NM_HOOK_INVALID;
        nm_hook_kind_t       kind = NM_HOOK_INLINE_DETOUR;
        uint64_t             target_address = 0;
        std::vector<uint8_t> original_bytes;
        void*                trampoline_page = nullptr;
        size_t               trampoline_page_size = 0;
        void*                original_vtable_fn = nullptr;
    };

    static bool write_protected_memory(void* dst, const void* src, size_t len);

    std::mutex                                  mutex_;
    nm_hook_handle_t                            next_handle_ = 1;
    std::unordered_map<nm_hook_handle_t, HookRecord> hooks_;
    std::unordered_map<uint64_t, nm_hook_handle_t>   by_target_;
};

} // namespace nativemods
