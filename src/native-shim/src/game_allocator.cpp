#include "game_allocator.hpp"

#include <cstdlib>
#include <cstring>

#if defined(_WIN32)
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

} // namespace

void GameAllocator::configure(RawAllocFn alloc_fn, RawFreeFn free_fn) {
    alloc_fn_ = alloc_fn;
    free_fn_ = free_fn;
}

void GameAllocator::reset() {
    alloc_fn_ = nullptr;
    free_fn_ = nullptr;
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
    // Matches 08-deep-analysis.md §6 and spikes/agent.js#L158-L159:
    //   p = alloc(size + align, align) + align
    //   header @ p - 16: { u64 size, u32 0, u8 log2(align), u16 tag, u8 0 }
    auto* payload = static_cast<uint8_t*>(raw) + alignment;
    auto* header = reinterpret_cast<nm_game_alloc_header_t*>(payload - sizeof(nm_game_alloc_header_t));
    std::memset(header, 0, sizeof(*header));
    header->size = static_cast<uint64_t>(size);
    header->reserved0 = 0;
    header->log2_align = log2_align;
    header->tag = (tag == 0) ? NM_DEFAULT_ALLOC_TAG : tag;
    header->reserved1 = 0;

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

    const RawFreeFn fn = free_fn_ ? free_fn_ : &host_fallback_free;
    void* raw = static_cast<uint8_t*>(ptr) - alignment;
    fn(raw);
    return NM_OK;
}

} // namespace nativemods
