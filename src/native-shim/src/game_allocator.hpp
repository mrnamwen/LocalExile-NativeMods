#pragma once

#include "nativemods_abi.h"

#include <cstddef>
#include <cstdint>

namespace nativemods {

static_assert(sizeof(nm_game_alloc_header_t) == 16, "Game allocator header must be exactly 16 bytes");

class GameAllocator {
public:
    using RawAllocFn = void*(NM_CALL*)(uint64_t size, uint64_t alignment);
    using RawFreeFn  = void(NM_CALL*)(void* block_minus_align);

    void configure(RawAllocFn alloc_fn, RawFreeFn free_fn);
    void reset();

    nm_status_t alloc(size_t size, size_t alignment, uint16_t tag, void** out_ptr) const;
    nm_status_t free(void* ptr, size_t alignment) const;

private:
    static bool is_valid_alignment(size_t alignment, uint8_t& out_log2);

    RawAllocFn alloc_fn_ = nullptr;
    RawFreeFn  free_fn_ = nullptr;
};

} // namespace nativemods
