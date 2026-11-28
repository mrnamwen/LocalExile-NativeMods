#pragma once
// Cached "is this memory readable" check for the services that read game state every frame.
//
// VirtualQuery is a system call. Asking it before every field read (hundreds per frame while an item is hovered)
// cost Windows players most of their frame rate. Game objects live in a few large committed regions, so each thread
// keeps the last regions it was told are readable and answers from them until the system tick moves on (about 16 ms,
// so at most one frame). A region freed within that window is the same race the uncached check already had between
// VirtualQuery and the read itself.

#include <cstddef>
#include <cstdint>

#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#endif

namespace nativemods {

inline bool ptr_readable_cached(const void* p, size_t len) noexcept {
    if (!p || len == 0) return false;
#if defined(_WIN32)
    struct Region {
        uintptr_t lo;
        uintptr_t hi;
        uint64_t  tick;
    };
    constexpr size_t kRegions = 16;
    thread_local Region regions[kRegions] = {};
    thread_local size_t next = 0;

    const uintptr_t start = reinterpret_cast<uintptr_t>(p);
    const uintptr_t end = start + len;
    if (end < start) return false;
    const uint64_t now = GetTickCount64();
    for (size_t i = 0; i < kRegions; ++i) {
        const Region& r = regions[i];
        if (r.tick == now && start >= r.lo && end <= r.hi) return true;
    }

    MEMORY_BASIC_INFORMATION mbi{};
    if (VirtualQuery(p, &mbi, sizeof(mbi)) == 0) return false;
    if (mbi.State != MEM_COMMIT) return false;
    const DWORD prot = mbi.Protect & 0xFFu;
    if (prot == PAGE_NOACCESS || (mbi.Protect & PAGE_GUARD) != 0) return false;
    const uintptr_t lo = reinterpret_cast<uintptr_t>(mbi.BaseAddress);
    const uintptr_t hi = lo + mbi.RegionSize;
    regions[next] = Region{lo, hi, now};
    next = (next + 1) % kRegions;
    return end <= hi;
#else
    (void)p;
    (void)len;
    return true;
#endif
}

} // namespace nativemods
