#pragma once

#include "nativemods_abi.h"

#include <cstddef>
#include <cstdint>

namespace nativemods {

// Wraps Path of Exile's per-thread slab allocator (0x142c8cf60 / 0x142c8ca30)
// using the verified 16-byte header placed immediately before the payload pointer
// and matching global byte-counter accounting at allocator.global_byte_counter (0x45d36a8).
// Falls back to host aligned_alloc/free when running outside a live Win64 game process.
class GameAllocator {
public:
    using RawAllocFn = void*(NM_CALL*)(uint64_t size, uint64_t alignment);
    using RawFreeFn  = void(NM_CALL*)(void* block_minus_align);

    void configure(RawAllocFn alloc_fn, RawFreeFn free_fn, volatile int64_t* global_byte_counter = nullptr);
    void reset();

    bool is_live_game_allocator() const { return alloc_fn_ != nullptr && free_fn_ != nullptr; }

    nm_status_t alloc(size_t size, size_t alignment, uint16_t tag, void** out_ptr) const;
    nm_status_t free(void* ptr, size_t alignment) const;

    static bool is_valid_alignment(size_t alignment, uint8_t& out_log2);

private:
    RawAllocFn        alloc_fn_ = nullptr;
    RawFreeFn         free_fn_  = nullptr;
    volatile int64_t* global_byte_counter_ = nullptr;
};

} // namespace nativemods
