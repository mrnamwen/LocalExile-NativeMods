#include "game_allocator.hpp"

#include <cstdlib>
#include <cstring>

#if defined(_WIN32)
#  define WIN32_LEAN_AND_MEAN
#  include <windows.h>
#  include <malloc.h>
#endif

namespace nativemods {

namespace {

void* NM_CALL host_fallback_alloc(uint64_t size, uint64_t alignment) {
    const size_t align = static_cast<size_t>(alignment < 16 ? 16 : alignment);
    const size_t sz = static_cast<size_t>(size);
#if defined(_WIN32)
    return _aligned_malloc(sz, align);
#else
    void* ptr = nullptr;
    if (posix_memalign(&ptr, align, sz) != 0) {
        return nullptr;
    }
    return ptr;
#endif
}

void NM_CALL host_fallback_free(void* ptr) {
    if (!ptr) return;
#if defined(_WIN32)
    _aligned_free(ptr);
#else
    std::free(ptr);
#endif
}

void add_global_bytes(volatile int64_t* counter, int64_t delta) {
    if (!counter) return;
#if defined(_WIN32)
    InterlockedExchangeAdd64(reinterpret_cast<volatile LONG64*>(counter), static_cast<LONG64>(delta));
#else
    __atomic_fetch_add(counter, delta, __ATOMIC_RELAXED);
#endif
}

} // namespace

void GameAllocator::configure(RawAllocFn alloc_fn, RawFreeFn free_fn, volatile int64_t* global_byte_counter) {
    alloc_fn_ = alloc_fn;
    free_fn_ = free_fn;
    global_byte_counter_ = global_byte_counter;
}

void GameAllocator::reset() {
    alloc_fn_ = nullptr;
    free_fn_ = nullptr;
    global_byte_counter_ = nullptr;
}

bool GameAllocator::is_valid_alignment(size_t alignment, uint8_t& out_log2) {
    if (alignment < 16 || (alignment & (alignment - 1)) != 0 || alignment > 4096) {
        return false;
    }
    uint8_t lg = 0;
    size_t v = alignment;
    while (v > 1) {
        v >>= 1;
        ++lg;
    }
    out_log2 = lg;
    return true;
}

nm_status_t GameAllocator::alloc(size_t size, size_t alignment, uint16_t tag, void** out_ptr) const {
    if (out_ptr == nullptr) {
        return NM_ERR_INVALID_ARG;
    }
    *out_ptr = nullptr;

    uint8_t log2_align = 0;
    if (!is_valid_alignment(alignment, log2_align)) {
        return NM_ERR_INVALID_ARG;
    }

    const RawAllocFn fn = alloc_fn_ ? alloc_fn_ : &host_fallback_alloc;
    const size_t total = size + alignment;
    void* raw = fn(static_cast<uint64_t>(total), static_cast<uint64_t>(alignment));
    if (!raw) {
        return NM_ERR_ALLOC_FAILED;
    }

    // Payload starts at raw + alignment; 16-byte header sits immediately before payload (payload - 16).
    // Matches 08-deep-analysis.md §6, spikes/agent.js#L158-L159, and Game.exe 0x140e56948..0x140e56978:
    //   p = alloc(size + align, align) + align
    //   header @ p - 16: { u64 size, u32 0, u8 log2(align), u16 tag, u8 0 }
    //   lock add [g_allocatedBytes], +(size + align)
    auto* payload = static_cast<uint8_t*>(raw) + alignment;
    auto* header = reinterpret_cast<nm_game_alloc_header_t*>(payload - sizeof(nm_game_alloc_header_t));
    std::memset(header, 0, sizeof(*header));
    header->size = static_cast<uint64_t>(size);
    header->reserved0 = 0;
    header->log2_align = log2_align;
    header->tag = (tag == 0) ? NM_DEFAULT_ALLOC_TAG : tag;
    header->reserved1 = 0;

    if (alloc_fn_ != nullptr) {
        add_global_bytes(global_byte_counter_, static_cast<int64_t>(total));
    }

    *out_ptr = payload;
    return NM_OK;
}

nm_status_t GameAllocator::free(void* ptr, size_t alignment) const {
    if (ptr == nullptr) {
        return NM_OK;
    }

    uint8_t log2_align = 0;
    if (!is_valid_alignment(alignment, log2_align)) {
        return NM_ERR_INVALID_ARG;
    }

    if (free_fn_ != nullptr && global_byte_counter_ != nullptr) {
        const auto* header = reinterpret_cast<const nm_game_alloc_header_t*>(
            static_cast<const uint8_t*>(ptr) - sizeof(nm_game_alloc_header_t));
        const int64_t total = static_cast<int64_t>(header->size + alignment);
        add_global_bytes(global_byte_counter_, -total);
    }

    const RawFreeFn fn = free_fn_ ? free_fn_ : &host_fallback_free;
    void* raw = static_cast<uint8_t*>(ptr) - alignment;
    fn(raw);
    return NM_OK;
}

} // namespace nativemods
