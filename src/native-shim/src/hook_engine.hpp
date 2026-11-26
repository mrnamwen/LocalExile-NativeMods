#pragma once

#include "nativemods_abi.h"

#include <atomic>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <mutex>
#include <unordered_map>
#include <vector>

namespace nativemods {

// Decoded x86-64 instruction metadata for length decoding & RIP-relative relocation.
struct DecodedX64Insn {
    uint8_t length = 0;           // 1..15 bytes, or 0 on invalid/truncated instruction
    bool    has_modrm = false;
    uint8_t modrm_offset = 0;
    uint8_t disp_offset = 0;      // offset of disp32 (RIP-rel) or rel8/rel32 (branch)
    uint8_t disp_size = 0;        // 1 or 4 when is_rip_relative or is_rel_branch
    bool    is_rip_relative = false;
    bool    is_rel_branch = false;
    bool    is_0f = false;
    uint8_t opcode_byte1 = 0;
    uint8_t opcode_byte2 = 0;
};

// Byte-verified inline x64 detour, vtable slot hook, and multi-mod pre/post chain manager.
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

    nm_status_t install_inline_chain(
        uint64_t                 target_address,
        const char*              expected_bytes_hex,
        nm_hook_phase_t          phase,
        int32_t                  priority,
        nm_hook_chain_callback_t cb,
        void*                    user_data,
        nm_hook_handle_t*        out_handle);

    nm_status_t install_vtable_chain(
        void*                    vtable_address,
        uint32_t                 slot_index,
        nm_hook_phase_t          phase,
        int32_t                  priority,
        nm_hook_chain_callback_t cb,
        void*                    user_data,
        nm_hook_handle_t*        out_handle);

    nm_status_t remove(nm_hook_handle_t handle);
    void        remove_all();

    static constexpr size_t kAbsJumpSize = 14; // FF 25 00 00 00 00 <u64 target>

    // Full C++20 x86-64 instruction decoder & length calculator (returns length=0 on invalid/truncated).
    static DecodedX64Insn decode_x64_insn(const uint8_t* code, size_t max_len);

    // Computes whole-instruction stolen length >= min_len. Returns 0 if an invalid instruction is hit!
    static size_t compute_stolen_length(const uint8_t* code, size_t min_len = kAbsJumpSize);

    // Relocates stolen x64 instructions from `orig_base` to `tramp_base` + appends tail jump back.
    static bool relocate_instructions(
        uint64_t              orig_base,
        const uint8_t*        orig_code,
        size_t                stolen_len,
        uint64_t              tramp_base,
        std::vector<uint8_t>& out_bytes);

    static void* alloc_exec_page(size_t size);
    static void* alloc_exec_near(uint64_t target_address, size_t size);
    static void  free_exec_page(void* ptr, size_t size);
    static void  emit_abs_jmp_x64(uint8_t* dst, uint64_t target);

private:
    struct ChainNode {
        nm_hook_handle_t         handle = NM_HOOK_INVALID;
        int32_t                  priority = 0;
        nm_hook_chain_callback_t cb = nullptr;
        void*                    user_data = nullptr;
    };

    struct RawDetourNode {
        nm_hook_handle_t handle = NM_HOOK_INVALID;
        void*            detour_fn = nullptr;
        void*            next_stub = nullptr;      // 16B: FF 25 02 00 00 00 CC CC <u64 next_target>
        size_t           next_stub_size = 0;
    };

    struct TargetChain {
        nm_hook_kind_t        kind = NM_HOOK_INLINE_DETOUR;
        uint64_t              target_address = 0;
        std::vector<uint8_t>  original_bytes;
        void*                 trampoline_page = nullptr;
        size_t                trampoline_page_size = 0;
        void*                 original_vtable_fn = nullptr;
        void*                 original_callable = nullptr;

        // 16B indirect entry stub for inline hooks: FF 25 00 00 00 00 <u64 entry_target>
        void*                 head_stub_page = nullptr;
        size_t                head_stub_size = 0;

        void*                 dispatcher_page = nullptr;
        size_t                dispatcher_size = 0;

        std::vector<RawDetourNode> raw_detours; // Ordered from head (back) to tail (front)
        std::vector<ChainNode>     pre_hooks;
        std::vector<ChainNode>     post_hooks;

        std::mutex                 chain_mutex;
        std::atomic<uint32_t>      active_calls{0};
    };

    struct HandleLocation {
        nm_hook_kind_t kind = NM_HOOK_INLINE_DETOUR;
        uint64_t       target_address = 0;
        enum SubKind { RawDetour, PreHook, PostHook } sub_kind = RawDetour;
    };

    struct RetiredChain {
        TargetChain*                          chain = nullptr;
        std::chrono::steady_clock::time_point retired_at{};
    };

    struct RetiredStub {
        void*                                 ptr = nullptr;
        size_t                                size = 0;
        std::chrono::steady_clock::time_point retired_at{};
    };

    static uint64_t make_chain_key(nm_hook_kind_t kind, uint64_t target_address);
    static bool     write_protected_memory(void* dst, const void* src, size_t len);
    static void*    build_dispatcher_stub(TargetChain* chain, size_t& out_size);
    static void     dispatch_pre(TargetChain* chain, nm_hook_context_t* ctx);
    static void     dispatch_post(TargetChain* chain, nm_hook_context_t* ctx);
    static void     emit_aligned_indirect_jmp_x64(uint8_t* dst, uint64_t target);
    static void     update_indirect_slot(void* stub16, uint64_t target);

    TargetChain* get_or_create_inline_chain_locked(
        uint64_t    target_address,
        const char* expected_bytes_hex,
        nm_status_t& out_status);
    TargetChain* get_or_create_vtable_chain_locked(
        void*       vtable_address,
        uint32_t    slot_index,
        nm_status_t& out_status);
    void relink_chain_locked(TargetChain* chain);
    void sweep_retired_chains_locked(bool force_quiescent = false);
    void sweep_retired_stubs_locked(bool force_quiescent = false);
    void free_chain_pages(TargetChain* chain);

    std::mutex                                         mutex_;
    nm_hook_handle_t                                   next_handle_ = 1;
    std::unordered_map<uint64_t, TargetChain*>         chains_; // key = target_address ^ kind tag
    std::unordered_map<nm_hook_handle_t, HandleLocation> handles_;
    std::vector<RetiredChain>                          retired_chains_;
    std::vector<RetiredStub>                           retired_stubs_;
};

} // namespace nativemods
