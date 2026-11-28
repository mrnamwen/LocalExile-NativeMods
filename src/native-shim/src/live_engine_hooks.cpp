#include "live_engine_hooks.hpp"

#include "address_table.hpp"
#include "exception_wall.hpp"
#include "game_allocator.hpp"
#include "hook_engine.hpp"
#include "overlay_and_channels.hpp"
#include "shim_logger.hpp"

#include <algorithm>
#include <atomic>
#include <cstring>
#include <cwchar>
#include <mutex>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#if defined(_WIN32)
#  define WIN32_LEAN_AND_MEAN
#  include <windows.h>
#  ifdef exception_code
#    undef exception_code
#  endif
#endif

namespace nativemods {

namespace {

// Encodes UTF-8 string into PoE 3.29 C->S 0x0015 chat wire format (spikes/agent.js#L234-L239).
std::vector<uint8_t> encode_poe_chat_wire(const char* text_utf8) {
    std::vector<uint32_t> codepoints;
    if (text_utf8) {
        const auto* p = reinterpret_cast<const uint8_t*>(text_utf8);
        while (*p != 0) {
            uint32_t cp = 0;
            if ((*p & 0x80u) == 0) {
                cp = *p++;
            } else if ((*p & 0xE0u) == 0xC0u && p[1] != 0) {
                cp = ((p[0] & 0x1Fu) << 6) | (p[1] & 0x3Fu);
                p += 2;
            } else if ((*p & 0xF0u) == 0xE0u && p[1] != 0 && p[2] != 0) {
                cp = ((p[0] & 0x0Fu) << 12) | ((p[1] & 0x3Fu) << 6) | (p[2] & 0x3Fu);
                p += 3;
            } else if ((*p & 0xF8u) == 0xF0u && p[1] != 0 && p[2] != 0 && p[3] != 0) {
                cp = ((p[0] & 0x07u) << 18) | ((p[1] & 0x3Fu) << 12) | ((p[2] & 0x3Fu) << 6) | (p[3] & 0x3Fu);
                p += 4;
            } else {
                cp = *p++;
            }
            codepoints.push_back(cp);
        }
    }

    const uint16_t char_count = static_cast<uint16_t>(std::min<size_t>(codepoints.size(), 0xFFFFu));
    std::vector<uint8_t> out;
    out.reserve(5 + char_count * 2);
    out.push_back(0x00u);
    out.push_back(0x15u);
    out.push_back(static_cast<uint8_t>((char_count >> 8) & 0xFFu));
    out.push_back(static_cast<uint8_t>(char_count & 0xFFu));
    for (size_t i = 0; i < char_count; ++i) {
        const uint32_t cp = codepoints[i];
        if (cp < 0x80u) {
            out.push_back(static_cast<uint8_t>(cp));
        } else {
            out.push_back(0x80u);
            out.push_back(static_cast<uint8_t>(cp & 0xFFu));
            out.push_back(static_cast<uint8_t>((cp >> 8) & 0xFFu));
        }
    }
    out.push_back(0x00u);
    return out;
}

// Encodes C->S 0x02CA ruleset packet matching Game.exe 0x141fa7c10 + CompactIntWriter 0x14089f790.
std::vector<uint8_t> encode_poe_ruleset_wire(uint32_t ruleset_id, const uint8_t* payload, uint32_t length) {
    std::vector<uint8_t> out;
    out.reserve(6 + length);
    // u16 BE opcode 0x02CA
    out.push_back(0x02u);
    out.push_back(0xCAu);
    // u8 ruleset_id
    out.push_back(static_cast<uint8_t>(ruleset_id & 0xFFu));
    // Compact-int length (exact encoding from 0x14089f790)
    if (length <= 0x7Fu) {
        out.push_back(static_cast<uint8_t>(length));
    } else if (length <= 0x3FFFu) {
        out.push_back(static_cast<uint8_t>(0x80u | ((length >> 8) & 0x3Fu)));
        out.push_back(static_cast<uint8_t>(length & 0xFFu));
    } else {
        out.push_back(static_cast<uint8_t>(0xC0u | ((length >> 16) & 0x1Fu)));
        out.push_back(static_cast<uint8_t>((length >> 8) & 0xFFu));
        out.push_back(static_cast<uint8_t>(length & 0xFFu));
    }
    if (length > 0 && payload != nullptr) {
        out.insert(out.end(), payload, payload + length);
    }
    return out;
}

std::vector<uint8_t> encode_poe_raw_wire(uint16_t opcode, const uint8_t* payload, uint32_t length) {
    std::vector<uint8_t> out;
    out.reserve(2 + length);
    out.push_back(static_cast<uint8_t>((opcode >> 8) & 0xFFu));
    out.push_back(static_cast<uint8_t>(opcode & 0xFFu));
    if (length > 0 && payload != nullptr) {
        out.insert(out.end(), payload, payload + length);
    }
    return out;
}

#if defined(_WIN32)

struct WStringView {
    const wchar_t* ptr;
    uint64_t       len;
};

struct MsvcWString {
    union {
        wchar_t  buf[8];
        wchar_t* ptr;
    };
    uint64_t size;
    uint64_t cap;
};
static_assert(sizeof(MsvcWString) == 32, "MSVC std::wstring must be 32 bytes");

// Verified exact layout of FileSystem::ReadWholeFile / ReadVariant5 output struct (0x140e568f4..0x140e56a79).
struct FsReadResult {
    uint8_t  ok;
    uint8_t  pad[7];
    uint8_t* begin;
    uint8_t* end;
    uint8_t* cap;
    // Empty MSVC std::string SSO (32 bytes)
    char     err_buf[16];
    uint64_t err_len;
    uint64_t err_cap;
};
static_assert(sizeof(FsReadResult) == 0x40, "FsReadResult must be 0x40 bytes");

using HashToWStringViewFn = WStringView*(NM_CALL*)(const uint64_t* path_hash, WStringView* out_view);
using FsExistsFn          = uint8_t(NM_CALL*)(void* fs, const WStringView* path);
using FsSizeFn            = uint64_t(NM_CALL*)(void* fs, const uint64_t* path_hash);
using FsReadRangeFn       = uint64_t(NM_CALL*)(void* fs, const uint64_t* path_hash, uint64_t offset, uint64_t count, void* dst_buf);
using FsReadWholeFn       = FsReadResult*(NM_CALL*)(void* fs, FsReadResult* out, const uint64_t* path_hash);
using UiControlLoadFn     = void(NM_CALL*)(void* control, const void* wstring_path);
using ChatListenerFn      = void(NM_CALL*)(void* panel, void* message);
using RulesetS2cFilterFn  = uint8_t(NM_CALL*)(void* client, void** msg_ptr_ptr);
using MessagePumpFn       = void(NM_CALL*)(void* pump, void* stream);
using RegisterGroupFn     = void(NM_CALL*)(void* pump, void* handler_group);
using TbTextChangedFn     = void(NM_CALL*)(void* textbox);
using TbKeyDownFn         = void(NM_CALL*)(void* textbox, void* input_event);

// Active global pointers shared by Win64 detours (set on install, cleared on reset).
std::atomic<OverlayAndChannels*> g_live_overlay{nullptr};
std::atomic<GameAllocator*>      g_live_alloc{nullptr};
std::atomic<HashToWStringViewFn> g_hash_to_wv{nullptr};
std::atomic<FsExistsFn>          g_orig_fs_exists{nullptr};
std::atomic<FsSizeFn>            g_orig_fs_size{nullptr};
std::atomic<FsReadRangeFn>       g_orig_fs_read_range{nullptr};
std::atomic<FsReadWholeFn>       g_orig_fs_read_whole{nullptr};
std::atomic<FsReadWholeFn>       g_orig_fs_read_v5{nullptr};
std::atomic<UiControlLoadFn>     g_orig_ui_control_load{nullptr};
std::atomic<ChatListenerFn>      g_orig_chat_listener{nullptr};
std::atomic<RulesetS2cFilterFn>  g_orig_ruleset_s2c{nullptr};
std::atomic<MessagePumpFn>       g_orig_message_pump{nullptr};
std::atomic<RegisterGroupFn>     g_register_handler_group{nullptr};
std::atomic<TbTextChangedFn>     g_orig_tb_text_changed{nullptr};
std::atomic<TbKeyDownFn>         g_orig_tb_key_down{nullptr};
std::atomic<void*>               g_last_registered_pump{nullptr};
std::mutex                       g_pumps_mutex;
std::unordered_set<void*>        g_registered_pumps;
std::atomic<void*>               g_captured_ui_root{nullptr};
std::atomic<bool>                g_trace_files{false};
std::atomic<uint64_t>            g_cached_app_global_addr{0};
std::atomic<nm_build_id_t>       g_live_build_id{NM_BUILD_UNKNOWN};

size_t resolve_offset_or(nm_build_id_t build_id, std::string_view name, size_t fallback) noexcept {
    if (build_id != NM_BUILD_UNKNOWN && build_id != NM_BUILD_SYNTHETIC) {
        if (const EmbeddedOffset* off = find_offset(build_id, name)) {
            return off->offset;
        }
    }
    return fallback;
}

uint64_t resolve_vtable_addr(nm_build_id_t build_id, uint64_t image_base, std::string_view sym_name) noexcept {
    if (build_id == NM_BUILD_UNKNOWN || build_id == NM_BUILD_SYNTHETIC || image_base == 0) {
        return 0ull;
    }
    const EmbeddedSymbol* sym = find_symbol(build_id, sym_name);
    if (!sym || sym->rva == 0) {
        return 0ull;
    }
    return image_base + sym->rva;
}

constexpr size_t                 kFsMissCacheSize = 16384u;
std::atomic<uint64_t>            g_fs_miss_cache[kFsMissCacheSize];

std::mutex                       g_hit_log_mutex;
std::unordered_set<std::string>  g_logged_overlay_hits;

inline uint64_t fs_miss_tag(uint64_t path_hash, uint64_t generation) noexcept {
    uint64_t x = path_hash ^ (generation * 0x9E3779B97F4A7C15ULL);
    x ^= x >> 30;
    x *= 0xBF58476D1CE4E5B9ULL;
    x ^= x >> 27;
    x *= 0x94D049BB133111EBULL;
    x ^= x >> 31;
    return (x == 0) ? 1ULL : x;
}

inline size_t fs_miss_slot(uint64_t path_hash) noexcept {
    const uint64_t mixed = (path_hash ^ (path_hash >> 33)) * 0xFF51AFD7ED558CCDULL;
    return static_cast<size_t>(mixed & (kFsMissCacheSize - 1u));
}

thread_local bool                t_in_fs_hook = false;

struct ScopedFsReentrancyGuard {
    bool entered = false;
    ScopedFsReentrancyGuard() {
        if (!t_in_fs_hook) {
            t_in_fs_hook = true;
            entered = true;
        }
    }
    ~ScopedFsReentrancyGuard() {
        if (entered) {
            t_in_fs_hook = false;
        }
    }
};

bool is_ptr_readable(const void* p, size_t len) {
    if (!p || len == 0) return false;
    MEMORY_BASIC_INFORMATION mbi{};
    if (VirtualQuery(p, &mbi, sizeof(mbi)) == 0) return false;
    if (mbi.State != MEM_COMMIT) return false;
    const DWORD prot = mbi.Protect & 0xFFu;
    if (prot == PAGE_NOACCESS || (mbi.Protect & PAGE_GUARD) != 0) return false;
    const uintptr_t start = reinterpret_cast<uintptr_t>(p);
    const uintptr_t end = reinterpret_cast<uintptr_t>(mbi.BaseAddress) + mbi.RegionSize;
    return (start + len) <= end;
}

nm_status_t verify_control_vtable_guarded(void* control, uint64_t expected_vt) {
    if (!control) return NM_ERR_INVALID_ARG;
    if (expected_vt == 0) return NM_OK;
    struct VtCtx { void* ctrl; uint64_t expected_vt; } ctx{control, expected_vt};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<VtCtx*>(raw);
        if (!is_ptr_readable(c->ctrl, sizeof(void*))) return -1;
        const uint64_t actual = reinterpret_cast<uint64_t>(*reinterpret_cast<void* const*>(c->ctrl));
        return (actual == c->expected_vt) ? 0 : -10;
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK) return st;
    if (rc == -10) return NM_ERR_INVALID_ARG;
    return (rc == 0) ? NM_OK : NM_ERR_GAME_EXCEPTION;
}

std::string wview_to_utf8(const wchar_t* ptr, size_t len) {
    if (!ptr || len == 0 || len > 4096) return {};
    const int needed = WideCharToMultiByte(CP_UTF8, 0, ptr, static_cast<int>(len), nullptr, 0, nullptr, nullptr);
    if (needed <= 0) return {};
    std::string out(static_cast<size_t>(needed), '\0');
    WideCharToMultiByte(CP_UTF8, 0, ptr, static_cast<int>(len), out.data(), needed, nullptr, nullptr);
    return out;
}

std::wstring utf8_to_wide_hooks(const char* s) {
    if (!s || s[0] == '\0') return {};
    const int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, nullptr, 0);
    if (n <= 1) return {};
    std::wstring w(static_cast<size_t>(n - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s, -1, w.data(), n);
    return w;
}

std::string read_msvc_wstring_utf8(const void* addr) {
    if (!is_ptr_readable(addr, sizeof(MsvcWString))) return {};
    const auto* ws = static_cast<const MsvcWString*>(addr);
    if (ws->size == 0 || ws->size > 8192) return {};
    const wchar_t* chars = (ws->cap >= 8) ? ws->ptr : ws->buf;
    if (!chars || !is_ptr_readable(chars, static_cast<size_t>(ws->size) * sizeof(wchar_t))) return {};
    return wview_to_utf8(chars, static_cast<size_t>(ws->size));
}

bool resolve_hash_to_utf8(const uint64_t* path_hash, std::string& out_path) {
    out_path.clear();
    auto fn = g_hash_to_wv.load(std::memory_order_acquire);
    if (!fn || !path_hash) return false;
    WStringView wv{nullptr, 0};
    WStringView* res = fn(path_hash, &wv);
    if (!res || !res->ptr || res->len == 0 || res->len > 4096) return false;
    out_path = wview_to_utf8(res->ptr, static_cast<size_t>(res->len));
    return !out_path.empty();
}

void log_overlay_hit_once(const char* hook_name, const std::string& path, size_t byte_count) {
    std::string key = std::string(hook_name) + ":" + OverlayAndChannels::normalize_virtual_path(path.c_str());
    bool should_log = false;
    {
        std::lock_guard<std::mutex> lock(g_hit_log_mutex);
        if (g_logged_overlay_hits.size() < 4096 && g_logged_overlay_hits.insert(key).second) {
            should_log = true;
        }
    }
    if (should_log) {
        logger_logf("INFO", "[OVERLAY] Served '%s' (%zu bytes via %s)", path.c_str(), byte_count, hook_name);
    }
}

void maybe_trace_miss(const char* hook_name, const std::string& path) {
    if (!g_trace_files.load(std::memory_order_relaxed)) return;
    const std::string norm = OverlayAndChannels::normalize_virtual_path(path.c_str());
    if (norm.find("mods/") != std::string::npos || norm.find("maxwell") != std::string::npos ||
        norm.find("siphon") != std::string::npos || norm.find(".dat") != std::string::npos) {
        logger_logf("INFO", "[OVERLAY-TRACE] miss '%s' (%s)", norm.c_str(), hook_name);
    }
}

// Fast-path filter for path_hash-keyed FileSystem hooks.
// Returns true ONLY when the path is a candidate overlay (or an unfiltered C callback is registered),
// populating out_utf8. 99.99% of stock game files return false with zero locks, zero heap
// allocations, zero Win32 UTF-8 conversions, zero P/Invoke, and zero disk stat() calls!
bool resolve_hash_if_overlay_candidate(
    const char*         hook_name,
    const OverlayIndex* idx,
    const uint64_t*     path_hash,
    std::string&        out_utf8) {
    out_utf8.clear();
    if (!idx || idx->empty || !path_hash) {
        return false;
    }

    const bool tracing = g_trace_files.load(std::memory_order_relaxed);
    if (!idx->has_unfiltered_callbacks) {
        const uint64_t ph = *path_hash;
        const uint64_t tag = fs_miss_tag(ph, idx->generation);
        const size_t slot = fs_miss_slot(ph);
        if (!tracing && g_fs_miss_cache[slot].load(std::memory_order_relaxed) == tag) {
            return false;
        }

        auto fn = g_hash_to_wv.load(std::memory_order_acquire);
        if (!fn) return false;
        WStringView wv{nullptr, 0};
        WStringView* res = fn(path_hash, &wv);
        if (!res || !res->ptr || res->len == 0 || res->len > 4096) {
            return false;
        }

        const uint64_t ci_h = OverlayAndChannels::hash_virtual_path_ci_wide(
            res->ptr, static_cast<size_t>(res->len));
        if (!idx->might_contain_ci_hash(ci_h)) {
            g_fs_miss_cache[slot].store(tag, std::memory_order_relaxed);
            if (tracing) {
                const std::string utf8 = wview_to_utf8(res->ptr, static_cast<size_t>(res->len));
                maybe_trace_miss(hook_name, utf8);
            }
            return false;
        }

        out_utf8 = wview_to_utf8(res->ptr, static_cast<size_t>(res->len));
        return !out_utf8.empty();
    }

    return resolve_hash_to_utf8(path_hash, out_utf8);
}

// --- Route A FileSystem Detours ---

uint8_t NM_CALL detour_fs_exists(void* fs, const WStringView* path) {
    auto orig = g_orig_fs_exists.load(std::memory_order_acquire);
    auto* ov = g_live_overlay.load(std::memory_order_acquire);
    if (ov && path && path->ptr && path->len > 0 && path->len < 4096) {
        auto idx_guard = ov->acquire_index();
        const OverlayIndex* idx = idx_guard.get();
        if (idx && !idx->empty) {
            const bool tracing = g_trace_files.load(std::memory_order_relaxed);
            bool candidate = idx->has_unfiltered_callbacks;
            if (!candidate) {
                const uint64_t ci_h = OverlayAndChannels::hash_virtual_path_ci_wide(
                    path->ptr, static_cast<size_t>(path->len));
                candidate = idx->might_contain_ci_hash(ci_h);
            }
            if (candidate || tracing) {
                ScopedFsReentrancyGuard guard;
                if (guard.entered) {
                    const std::string utf8 = wview_to_utf8(path->ptr, static_cast<size_t>(path->len));
                    if (candidate && !utf8.empty()) {
                        size_t sz = 0;
                        if (ov->has_file_overlay(utf8.c_str(), &sz) == NM_OK) {
                            log_overlay_hit_once("exists", utf8, sz);
                            return 1;
                        }
                    }
                    if (tracing) {
                        maybe_trace_miss("exists", utf8);
                    }
                }
            }
        }
    }
    return orig ? orig(fs, path) : 0;
}

uint64_t NM_CALL detour_fs_size(void* fs, const uint64_t* path_hash) {
    auto orig = g_orig_fs_size.load(std::memory_order_acquire);
    auto* ov = g_live_overlay.load(std::memory_order_acquire);
    if (ov && path_hash) {
        auto idx_guard = ov->acquire_index();
        const OverlayIndex* idx = idx_guard.get();
        if (idx && !idx->empty) {
            ScopedFsReentrancyGuard guard;
            if (guard.entered) {
                std::string utf8;
                if (resolve_hash_if_overlay_candidate("size", idx, path_hash, utf8)) {
                    size_t sz = 0;
                    if (ov->has_file_overlay(utf8.c_str(), &sz) == NM_OK) {
                        log_overlay_hit_once("size", utf8, sz);
                        return static_cast<uint64_t>(sz);
                    }
                    maybe_trace_miss("size", utf8);
                }
            }
        }
    }
    return orig ? orig(fs, path_hash) : 0;
}

uint64_t NM_CALL detour_fs_read_range(
    void*           fs,
    const uint64_t* path_hash,
    uint64_t        offset,
    uint64_t        count,
    void*           dst_buf) {
    auto orig = g_orig_fs_read_range.load(std::memory_order_acquire);
    auto* ov = g_live_overlay.load(std::memory_order_acquire);
    if (ov && path_hash) {
        auto idx_guard = ov->acquire_index();
        const OverlayIndex* idx = idx_guard.get();
        if (idx && !idx->empty) {
            ScopedFsReentrancyGuard guard;
            if (guard.entered) {
                std::string utf8;
                if (resolve_hash_if_overlay_candidate("read_range", idx, path_hash, utf8)) {
                    std::vector<uint8_t> bytes;
                    if (ov->resolve_file_overlay_owned(utf8.c_str(), bytes)) {
                        log_overlay_hit_once("read_range", utf8, bytes.size());
                        if (!dst_buf || count == 0 || offset >= bytes.size()) {
                            return 0;
                        }
                        const uint64_t avail = static_cast<uint64_t>(bytes.size()) - offset;
                        const uint64_t to_copy = std::min<uint64_t>(count, avail);
                        std::memcpy(dst_buf, bytes.data() + offset, static_cast<size_t>(to_copy));
                        return to_copy;
                    }
                    maybe_trace_miss("read_range", utf8);
                }
            }
        }
    }
    return orig ? orig(fs, path_hash, offset, count, dst_buf) : 0;
}

FsReadResult* serve_whole_file_common(
    const char*     hook_name,
    FsReadWholeFn   orig,
    void*           fs,
    FsReadResult*   out,
    const uint64_t* path_hash) {
    auto* ov = g_live_overlay.load(std::memory_order_acquire);
    auto* alloc = g_live_alloc.load(std::memory_order_acquire);
    if (ov && alloc && out && path_hash) {
        auto idx_guard = ov->acquire_index();
        const OverlayIndex* idx = idx_guard.get();
        if (idx && !idx->empty) {
            ScopedFsReentrancyGuard guard;
            if (guard.entered) {
                std::string utf8;
                if (resolve_hash_if_overlay_candidate(hook_name, idx, path_hash, utf8)) {
                    std::vector<uint8_t> bytes;
                    if (ov->resolve_file_overlay_owned(utf8.c_str(), bytes)) {
                        void* game_buf = nullptr;
                        if (alloc->alloc(bytes.size(), 16, NM_DEFAULT_ALLOC_TAG, &game_buf) == NM_OK && game_buf) {
                            if (!bytes.empty()) {
                                std::memcpy(game_buf, bytes.data(), bytes.size());
                            }
                            std::memset(out, 0, sizeof(*out));
                            out->ok = 1;
                            out->begin = static_cast<uint8_t*>(game_buf);
                            out->end = out->begin + bytes.size();
                            out->cap = out->end;
                            out->err_len = 0;
                            out->err_cap = 15; // Valid empty MSVC std::string SSO
                            log_overlay_hit_once(hook_name, utf8, bytes.size());
                            return out;
                        }
                        logger_logf("ERROR", "[OVERLAY] GameAllocator::alloc(%zu) failed for '%s'", bytes.size(), utf8.c_str());
                    } else {
                        maybe_trace_miss(hook_name, utf8);
                    }
                }
            }
        }
    }
    return orig ? orig(fs, out, path_hash) : out;
}

FsReadResult* NM_CALL detour_fs_read_whole(void* fs, FsReadResult* out, const uint64_t* path_hash) {
    return serve_whole_file_common("read_whole", g_orig_fs_read_whole.load(std::memory_order_acquire), fs, out, path_hash);
}

FsReadResult* NM_CALL detour_fs_read_v5(void* fs, FsReadResult* out, const uint64_t* path_hash) {
    return serve_whole_file_common("read_v5", g_orig_fs_read_v5.load(std::memory_order_acquire), fs, out, path_hash);
}

// --- UI Control::Load Root Observer Detour ---

void NM_CALL detour_ui_control_load(void* control, const void* wstring_path) {
    if (control) {
        const size_t parent_off = resolve_offset_or(
            g_live_build_id.load(std::memory_order_acquire), "ui.control.parent", 0x1E0u);
        void* cur = control;
        for (int hops = 0; hops < 64; ++hops) {
            if (!is_ptr_readable(static_cast<const uint8_t*>(cur) + parent_off, sizeof(void*))) {
                break;
            }
            void* parent = *reinterpret_cast<void* const*>(static_cast<const uint8_t*>(cur) + parent_off);
            if (!parent) {
                void* prev = g_captured_ui_root.exchange(cur, std::memory_order_acq_rel);
                if (prev != cur) {
                    logger_logf("INFO", "[UI] Captured native UI root %p", cur);
                }
                break;
            }
            cur = parent;
        }
    }
    auto orig = g_orig_ui_control_load.load(std::memory_order_acquire);
    if (orig) {
        orig(control, wstring_path);
    }
}

// --- Chat S->C 0x17 Interceptor Detour ---

void NM_CALL detour_chat_panel_listener(void* panel, void* message) {
    auto* ov = g_live_overlay.load(std::memory_order_acquire);
    if (ov && message) {
        const nm_build_id_t bid = g_live_build_id.load(std::memory_order_acquire);
        const size_t sender_off = resolve_offset_or(bid, "network.chat_message.sender", 0x00u);
        const size_t text_off = resolve_offset_or(bid, "network.chat_message.text", 0x20u);
        const std::string text = read_msvc_wstring_utf8(static_cast<const uint8_t*>(message) + text_off);
        const bool is_nm = text.size() >= 5 && std::strncmp(text.c_str(), NM_CHAT_FALLBACK_PREFIX, 5) == 0;
        if (is_nm || ov->has_chat_handlers()) {
            const std::string sender = read_msvc_wstring_utf8(static_cast<const uint8_t*>(message) + sender_off);
            if (ov->dispatch_chat_rx(sender.c_str(), text.c_str()) != 0) {
                logger_logf("INFO", "[NET] Intercepted S->C chat message (len=%zu, nm_prefix=%d)", text.size(), is_nm ? 1 : 0);
                return;
            }
        }
    }
    auto orig = g_orig_chat_listener.load(std::memory_order_acquire);
    if (orig) {
        orig(panel, message);
    }
}

// --- Network 0x02FC+ HandlerGroup & MessagePump Detour ---

void* NM_CALL nm_handler_group_dtor(void* self, uint64_t /*flags*/) { return self; }

uint8_t NM_CALL nm_handler_group_on_message(void* /*self*/, uint16_t opcode, void* stream) {
    if (opcode < NM_OPCODE_RESERVED_MIN) {
        return 0;
    }
    auto* ov = g_live_overlay.load(std::memory_order_acquire);
    if (!ov || !ov->has_opcode_handlers()) {
        return 0;
    }
    int32_t handled = 0;
    (void)ov->dispatch_opcode_rx(opcode, stream, nullptr, 0, &handled);
    return handled ? 1 : 0;
}

void* g_nm_handler_group_vtable[2] = {
    reinterpret_cast<void*>(&nm_handler_group_dtor),
    reinterpret_cast<void*>(&nm_handler_group_on_message),
};

struct NmMessageHandlerGroup {
    void** vtable;
} g_nm_handler_group{g_nm_handler_group_vtable};

void NM_CALL detour_network_message_pump(void* pump, void* stream) {
    if (pump && g_last_registered_pump.load(std::memory_order_acquire) != pump) {
        auto reg_fn = g_register_handler_group.load(std::memory_order_acquire);
        if (reg_fn) {
            bool need_register = false;
            {
                std::lock_guard<std::mutex> lock(g_pumps_mutex);
                if (g_registered_pumps.insert(pump).second) {
                    need_register = true;
                }
            }
            if (need_register) {
                reg_fn(pump, &g_nm_handler_group);
                logger_logf("INFO", "[NET] Registered 0x02FC+ HandlerGroup on MessagePump %p", pump);
            }
            g_last_registered_pump.store(pump, std::memory_order_release);
        }
    }
    auto orig = g_orig_message_pump.load(std::memory_order_acquire);
    if (orig) {
        orig(pump, stream);
    }
}

// --- Ruleset S->C 0x2c9 Filter Detour ---

uint8_t NM_CALL detour_ruleset_s2c_filter(void* client, void** msg_ptr_ptr) {
    auto* ov = g_live_overlay.load(std::memory_order_acquire);
    if (ov && msg_ptr_ptr && is_ptr_readable(msg_ptr_ptr, sizeof(void*))) {
        const auto* msg = static_cast<const uint8_t*>(*msg_ptr_ptr);
        if (msg && is_ptr_readable(msg, 0x98)) {
            const uint16_t opcode = *reinterpret_cast<const uint16_t*>(msg + 0x08);
            if (opcode == NM_OPCODE_RULESET_S2C) {
                const uint32_t ruleset_id = *reinterpret_cast<const uint32_t*>(msg + 0x90);
                if (ov->has_ruleset_handler(ruleset_id)) {
                    const auto* begin = *reinterpret_cast<const uint8_t* const*>(msg + 0x10);
                    const auto* end   = *reinterpret_cast<const uint8_t* const*>(msg + 0x18);
                    const uint32_t len = (begin && end && end >= begin)
                                             ? static_cast<uint32_t>(end - begin)
                                             : 0u;
                    logger_logf("INFO", "[NET] Dispatched S->C 0x2c9 ruleset=0x%02X (%u bytes)", ruleset_id, len);
                    (void)ov->dispatch_ruleset_rx(ruleset_id, begin, len);
                    return 1;
                }
            }
        }
    }
    auto orig = g_orig_ruleset_s2c.load(std::memory_order_acquire);
    return orig ? orig(client, msg_ptr_ptr) : 0;
}

// --- Native UI Click Handler Vtable (matches spikes/agent.js#L110-L136) ---

struct UiClickHandlerRecord {
    uint64_t               token;
    void*                  control;
    nm_ui_event_callback_t cb;
    void*                  user_data;
};

struct UiWindowCacheKey {
    void*       parent;
    std::string path;
    bool operator==(const UiWindowCacheKey& o) const {
        return parent == o.parent && path == o.path;
    }
};
struct UiWindowCacheKeyHash {
    size_t operator()(const UiWindowCacheKey& k) const {
        const size_t h1 = std::hash<void*>{}(k.parent);
        const size_t h2 = std::hash<std::string>{}(k.path);
        return h1 ^ (h2 + 0x9e3779b97f4a7c15ull + (h1 << 6) + (h1 >> 2));
    }
};

std::mutex                                                      g_ui_handlers_mutex;
uint64_t                                                        g_ui_next_token = 1;
std::vector<UiClickHandlerRecord*>                              g_ui_handlers;
std::unordered_map<uint64_t, UiClickHandlerRecord*>             g_ui_token_map;
std::unordered_map<void*, UiClickHandlerRecord*>                g_ui_button_slots;
std::unordered_map<void*, UiClickHandlerRecord*>                g_ui_checkbox_slots;
std::unordered_map<void*, UiClickHandlerRecord*>                g_ui_tb_changed_slots;
std::unordered_map<void*, UiClickHandlerRecord*>                g_ui_tb_submitted_slots;
nm_hook_handle_t                                                g_tb_changed_vt_hook = NM_HOOK_INVALID;
nm_hook_handle_t                                                g_tb_submitted_vt_hook = NM_HOOK_INVALID;
std::mutex                                                      g_ui_windows_mutex;
void*                                                           g_ui_windows_root = nullptr;
std::unordered_map<UiWindowCacheKey, void*, UiWindowCacheKeyHash> g_ui_loaded_windows;

void NM_CALL detour_tb_text_changed(void* textbox) {
    auto orig = g_orig_tb_text_changed.load(std::memory_order_acquire);
    if (orig) {
        orig(textbox);
    }
    if (!textbox) return;
    nm_ui_event_callback_t cb = nullptr;
    void* ud = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_ui_handlers_mutex);
        auto it = g_ui_tb_changed_slots.find(textbox);
        if (it != g_ui_tb_changed_slots.end() && it->second != nullptr) {
            cb = it->second->cb;
            ud = it->second->user_data;
        }
    }
    if (cb) {
        struct Ctx { void* ctrl; nm_ui_event_callback_t cb; void* ud; } ctx{textbox, cb, ud};
        auto fn = [](void* raw) -> int64_t {
            auto* c = static_cast<Ctx*>(raw);
            c->cb(c->ctrl, c->ud);
            return 0;
        };
        nm_exception_info_t ex{};
        (void)invoke_guarded(fn, &ctx, nullptr, &ex);
    }
}

void NM_CALL detour_tb_key_down(void* textbox, void* input_event) {
    auto orig = g_orig_tb_key_down.load(std::memory_order_acquire);
    if (orig) {
        orig(textbox, input_event);
    }
    if (!textbox || !input_event || !is_ptr_readable(input_event, 8)) return;
    const auto* ev_bytes = static_cast<const uint8_t*>(input_event);
    const uint8_t event_kind = ev_bytes[0];
    uint32_t vk = 0;
    std::memcpy(&vk, ev_bytes + 4, sizeof(vk));
    if (event_kind != 0 || vk != 0x0Du) { // VK_RETURN key down
        return;
    }

    nm_ui_event_callback_t cb = nullptr;
    void* ud = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_ui_handlers_mutex);
        auto it = g_ui_tb_submitted_slots.find(textbox);
        if (it != g_ui_tb_submitted_slots.end() && it->second != nullptr) {
            cb = it->second->cb;
            ud = it->second->user_data;
        }
    }
    if (cb) {
        struct Ctx { void* ctrl; nm_ui_event_callback_t cb; void* ud; } ctx{textbox, cb, ud};
        auto fn = [](void* raw) -> int64_t {
            auto* c = static_cast<Ctx*>(raw);
            c->cb(c->ctrl, c->ud);
            return 0;
        };
        nm_exception_info_t ex{};
        (void)invoke_guarded(fn, &ctx, nullptr, &ex);
    }
}

void invalidate_control_cache_and_handlers(void* control) {
    if (!control) return;
    {
        std::lock_guard<std::mutex> lock(g_ui_windows_mutex);
        for (auto it = g_ui_loaded_windows.begin(); it != g_ui_loaded_windows.end();) {
            if (it->second == control || it->first.parent == control) {
                it = g_ui_loaded_windows.erase(it);
            } else {
                ++it;
            }
        }
    }
    {
        std::lock_guard<std::mutex> lock(g_ui_handlers_mutex);
        auto clear_slot = [&](std::unordered_map<void*, UiClickHandlerRecord*>& m) {
            auto it = m.find(control);
            if (it != m.end()) {
                if (it->second) {
                    it->second->cb = nullptr;
                    it->second->user_data = nullptr;
                }
                m.erase(it);
            }
        };
        clear_slot(g_ui_button_slots);
        clear_slot(g_ui_checkbox_slots);
        clear_slot(g_ui_tb_changed_slots);
        clear_slot(g_ui_tb_submitted_slots);
    }
}

void* NM_CALL ui_event_destroy(void* self, uint64_t /*flags*/) { return self; }
uint64_t NM_CALL ui_event_size(void* /*self*/) { return 0x10ull; }
uint64_t NM_CALL ui_event_kind(void* /*self*/) { return 2ull; }
void NM_CALL ui_event_invoke(void* entry_slot) {
    if (!entry_slot) return;
    auto* rec = *reinterpret_cast<UiClickHandlerRecord**>(static_cast<uint8_t*>(entry_slot) + 8);
    if (!rec) return;

    struct InvokeCtx {
        void*                  control;
        nm_ui_event_callback_t cb;
        void*                  user_data;
    } ctx{nullptr, nullptr, nullptr};

    {
        std::lock_guard<std::mutex> lock(g_ui_handlers_mutex);
        ctx.control   = rec->control;
        ctx.cb        = rec->cb;
        ctx.user_data = rec->user_data;
    }
    if (!ctx.cb) return;

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<InvokeCtx*>(raw);
        c->cb(c->control, c->user_data);
        return 0;
    };
    nm_exception_info_t ex{};
    (void)invoke_guarded(fn, &ctx, nullptr, &ex);
}

void* g_ui_event_vtable[4] = {
    reinterpret_cast<void*>(&ui_event_destroy),
    reinterpret_cast<void*>(&ui_event_size),
    reinterpret_cast<void*>(&ui_event_kind),
    reinterpret_cast<void*>(&ui_event_invoke),
};

bool is_control_in_parent_children(nm_build_id_t build_id, void* parent, void* target) {
    if (!parent || !target) return false;
    const size_t begin_off = resolve_offset_or(build_id, "ui.control.children_begin", 0xC0u);
    const size_t end_off = resolve_offset_or(build_id, "ui.control.children_end", 0xC8u);
    auto* parent_bytes = static_cast<const uint8_t*>(parent);
    if (!is_ptr_readable(parent_bytes + begin_off, sizeof(void*)) ||
        !is_ptr_readable(parent_bytes + end_off, sizeof(void*))) {
        return false;
    }
    auto* begin = *reinterpret_cast<void* const* const*>(parent_bytes + begin_off);
    auto* end   = *reinterpret_cast<void* const* const*>(parent_bytes + end_off);
    if (!begin || !end || end < begin) return false;
    const size_t count = static_cast<size_t>(end - begin);
    if (count == 0 || count > 2048 || !is_ptr_readable(begin, count * sizeof(void*))) return false;
    for (size_t i = 0; i < count; ++i) {
        if (begin[i] == target) {
            return true;
        }
    }
    return false;
}

bool control_id_equals(nm_build_id_t build_id, const void* control, const wchar_t* target, size_t target_len) {
    if (!control || !target || target_len == 0) return false;
    const size_t id_off = resolve_offset_or(build_id, "ui.control.id", 0x1B8u);
    const auto* ws_addr = static_cast<const uint8_t*>(control) + id_off;
    if (!is_ptr_readable(ws_addr, sizeof(MsvcWString))) return false;
    const auto* ws = reinterpret_cast<const MsvcWString*>(ws_addr);
    if (ws->size != target_len || ws->size > 4096) return false;
    const wchar_t* chars = (ws->cap >= 8) ? ws->ptr : ws->buf;
    if (!chars || !is_ptr_readable(chars, target_len * sizeof(wchar_t))) return false;
    return std::wmemcmp(chars, target, target_len) == 0;
}

void* find_child_recursive_by_segment(nm_build_id_t build_id, void* parent, const wchar_t* seg, size_t seg_len, int depth) {
    if (!parent || !seg || seg_len == 0 || depth > 16) return nullptr;
    const size_t begin_off = resolve_offset_or(build_id, "ui.control.children_begin", 0xC0u);
    const size_t end_off = resolve_offset_or(build_id, "ui.control.children_end", 0xC8u);
    const auto* parent_bytes = static_cast<const uint8_t*>(parent);
    if (!is_ptr_readable(parent_bytes + begin_off, sizeof(void*)) ||
        !is_ptr_readable(parent_bytes + end_off, sizeof(void*))) {
        return nullptr;
    }
    auto* begin = *reinterpret_cast<void* const* const*>(parent_bytes + begin_off);
    auto* end   = *reinterpret_cast<void* const* const*>(parent_bytes + end_off);
    if (!begin || !end || end < begin) return nullptr;
    const size_t count = static_cast<size_t>(end - begin);
    if (count == 0 || count > 512 || !is_ptr_readable(begin, count * sizeof(void*))) return nullptr;

    // Pass 1: direct children first (matches native Control::Find priority).
    for (size_t i = 0; i < count; ++i) {
        void* child = begin[i];
        if (child && control_id_equals(build_id, child, seg, seg_len)) {
            return child;
        }
    }
    // Pass 2: descend through unnamed or intermediate containers.
    for (size_t i = 0; i < count; ++i) {
        void* child = begin[i];
        if (child) {
            if (void* nested = find_child_recursive_by_segment(build_id, child, seg, seg_len, depth + 1)) {
                return nested;
            }
        }
    }
    return nullptr;
}

void* find_child_fallback_dot_path(nm_build_id_t build_id, void* parent, const std::wstring& dot_path) {
    if (!parent || dot_path.empty()) return nullptr;
    void* current = parent;
    size_t pos = 0;
    while (pos < dot_path.size() && current != nullptr) {
        const size_t dot = dot_path.find(L'.', pos);
        const size_t seg_len = (dot == std::wstring::npos) ? (dot_path.size() - pos) : (dot - pos);
        if (seg_len > 0) {
            current = find_child_recursive_by_segment(build_id, current, dot_path.c_str() + pos, seg_len, 0);
        }
        if (dot == std::wstring::npos) break;
        pos = dot + 1;
    }
    return current;
}

// Resolves and caches the live App global pointer address via network.app_global_ref (0x00f3b30).
uint64_t resolve_app_global_addr(nm_build_id_t build_id, uint64_t image_base) {
    uint64_t cached = g_cached_app_global_addr.load(std::memory_order_acquire);
    if (cached != 0) {
        return cached;
    }
    if (build_id == NM_BUILD_SYNTHETIC || build_id == NM_BUILD_UNKNOWN || image_base == 0) {
        return 0;
    }
    const EmbeddedSymbol* ref_sym = find_symbol(build_id, "network.app_global_ref");
    if (!ref_sym || ref_sym->rva == 0) return 0;
    const uint64_t ref_addr = image_base + ref_sym->rva;
    if (!is_ptr_readable(reinterpret_cast<const void*>(static_cast<uintptr_t>(ref_addr)), 13)) return 0;
    if (verify_memory_bytes(ref_addr, "488b05????????488907488b05") != NM_OK) return 0;

    int32_t disp32 = 0;
    std::memcpy(&disp32, reinterpret_cast<const uint8_t*>(static_cast<uintptr_t>(ref_addr)) + 3, sizeof(disp32));
    const uint64_t app_global_addr = static_cast<uint64_t>(static_cast<int64_t>(ref_addr + 7) + disp32);
    if (!is_ptr_readable(reinterpret_cast<const void*>(static_cast<uintptr_t>(app_global_addr)), sizeof(void*))) return 0;

    g_cached_app_global_addr.store(app_global_addr, std::memory_order_release);
    return app_global_addr;
}

// Resolves the live game InstanceClient & SocketStream pointers via network.app_global_ref (0x00f3b30).
bool resolve_live_connection_ptrs(nm_build_id_t build_id, uint64_t image_base, void** out_instance, void** out_sock) {
    if (out_instance) *out_instance = nullptr;
    if (out_sock) *out_sock = nullptr;

    const uint64_t app_global_addr = resolve_app_global_addr(build_id, image_base);
    if (app_global_addr == 0) return false;
    if (!is_ptr_readable(reinterpret_cast<const void*>(static_cast<uintptr_t>(app_global_addr)), sizeof(void*))) return false;

    const size_t instance_off = resolve_offset_or(build_id, "network.app.instance_client", 0x348u);
    const size_t sock_off = resolve_offset_or(build_id, "network.instance_client.socket", 0xC288u);
    void* app = *reinterpret_cast<void* const*>(static_cast<uintptr_t>(app_global_addr));
    if (!app || !is_ptr_readable(static_cast<const uint8_t*>(app) + instance_off, sizeof(void*))) return false;

    void* instance = *reinterpret_cast<void* const*>(static_cast<const uint8_t*>(app) + instance_off);
    if (!instance || !is_ptr_readable(static_cast<const uint8_t*>(instance) + sock_off, sizeof(void*))) return false;

    void* sock = *reinterpret_cast<void* const*>(static_cast<const uint8_t*>(instance) + sock_off);
    if (!sock || !is_ptr_readable(static_cast<const uint8_t*>(sock) + 0x250, 8)) return false;

    if (out_instance) *out_instance = instance;
    if (out_sock) *out_sock = sock;
    return true;
}

void* resolve_live_game_socket(nm_build_id_t build_id, uint64_t image_base) {
    void* sock = nullptr;
    (void)resolve_live_connection_ptrs(build_id, image_base, nullptr, &sock);
    return sock;
}

nm_status_t write_socket_bytes_locked(
    nm_build_id_t  build_id,
    uint64_t       image_base,
    const uint8_t* data,
    size_t         len) {
    if (!data || len == 0) return NM_ERR_INVALID_ARG;
    void* sock = resolve_live_game_socket(build_id, image_base);
    if (!sock) {
        return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    }

    const EmbeddedSymbol* write_sym = find_symbol(build_id, "network.socket_write");
    const EmbeddedSymbol* flush_sym = find_symbol(build_id, "network.socket_flush");
    if (!write_sym || !flush_sym || write_sym->rva == 0 || flush_sym->rva == 0) {
        return NM_ERR_SYMBOL_NOT_FOUND;
    }

    struct TxCtx {
        void*          sock;
        const uint8_t* data;
        size_t         len;
        uint64_t       write_addr;
        uint64_t       flush_addr;
        size_t         write_in_prog_off;
        size_t         threaded_off;
        size_t         srw_lock_off;
    } ctx{
        sock,
        data,
        len,
        image_base + write_sym->rva,
        image_base + flush_sym->rva,
        resolve_offset_or(build_id, "network.socket.write_in_progress", 0x240u),
        resolve_offset_or(build_id, "network.socket.threaded_flag", 0x242u),
        resolve_offset_or(build_id, "network.socket.srw_lock", 0x248u),
    };

    auto tx_fn = [](void* raw) -> int64_t {
        auto* c = static_cast<TxCtx*>(raw);
        using SocketWriteFn = void*(NM_CALL*)(void*, const void*, uint64_t);
        using SocketFlushFn = void(NM_CALL*)(void*, uint32_t, uint8_t);
        auto sock_write = reinterpret_cast<SocketWriteFn>(static_cast<uintptr_t>(c->write_addr));
        auto sock_flush = reinterpret_cast<SocketFlushFn>(static_cast<uintptr_t>(c->flush_addr));

        auto* sbytes = static_cast<uint8_t*>(c->sock);
        const bool threaded = (sbytes[c->threaded_off] != 0);
        auto* srw = reinterpret_cast<PSRWLOCK>(sbytes + c->srw_lock_off);

        sbytes[c->write_in_prog_off] = 1;
        if (threaded) AcquireSRWLockExclusive(srw);
        sock_write(c->sock, c->data, static_cast<uint64_t>(c->len));
        if (threaded) ReleaseSRWLockExclusive(srw);
        sbytes[c->write_in_prog_off] = 0;

        // SocketStream::Flush is self-locking — NEVER call while holding socket+0x248 SRW lock!
        sock_flush(c->sock, 0, 0);
        return 0;
    };

    nm_exception_info_t ex{};
    return invoke_guarded(tx_fn, &ctx, nullptr, &ex);
}

#endif // _WIN32

} // namespace

LiveEngineHooks::~LiveEngineHooks() = default;

std::vector<uint8_t> LiveEngineHooks::encode_c2s_chat_packet(const char* text_utf8) {
    return encode_poe_chat_wire(text_utf8);
}

std::vector<uint8_t> LiveEngineHooks::encode_c2s_ruleset_packet(uint32_t ruleset_id, const uint8_t* payload, uint32_t length) {
    return encode_poe_ruleset_wire(ruleset_id, payload, length);
}

std::vector<uint8_t> LiveEngineHooks::encode_c2s_raw_packet(uint16_t opcode, const uint8_t* payload, uint32_t length) {
    return encode_poe_raw_wire(opcode, payload, length);
}

void LiveEngineHooks::register_startup_disk_overlays(
    const std::string&  dll_dir_utf8,
    OverlayAndChannels& overlay) {
#if defined(_WIN32)
    auto get_env_str = [](const wchar_t* name) -> std::string {
        wchar_t buf[2048] = {};
        const DWORD n = GetEnvironmentVariableW(name, buf, 2048);
        if (n == 0 || n >= 2048) return {};
        return wview_to_utf8(buf, n);
    };

    if (get_env_str(L"NATIVEMODS_TRACE_FILES") == "1") {
        g_trace_files.store(true, std::memory_order_relaxed);
    }

    auto dir_exists = [](const std::string& path) -> bool {
        const std::wstring w = utf8_to_wide_hooks(path.c_str());
        if (w.empty()) return false;
        const DWORD a = GetFileAttributesW(w.c_str());
        return a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_DIRECTORY) != 0;
    };

    // 1. Auto-discover Mods/Client/<mod>/{client,files} first (lower priority than explicit NATIVEMODS_OVERLAY_DIR).
    std::string mods_dir = get_env_str(L"NATIVEMODS_MODS_DIR");
    if (mods_dir.empty() && !dll_dir_utf8.empty()) {
        const std::string local_mods = dll_dir_utf8 + "\\Mods\\Client";
        const std::string parent_mods = dll_dir_utf8 + "\\..\\Mods\\Client";
        if (dir_exists(local_mods)) {
            mods_dir = local_mods;
        } else if (dir_exists(parent_mods)) {
            mods_dir = parent_mods;
        }
    }
    if (!mods_dir.empty() && dir_exists(mods_dir)) {
        const std::wstring pattern = utf8_to_wide_hooks((mods_dir + "\\*").c_str());
        WIN32_FIND_DATAW fd{};
        HANDLE h = FindFirstFileW(pattern.c_str(), &fd);
        if (h != INVALID_HANDLE_VALUE) {
            do {
                if ((fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0 &&
                    std::wcscmp(fd.cFileName, L".") != 0 &&
                    std::wcscmp(fd.cFileName, L"..") != 0) {
                    const std::string mod_sub = mods_dir + "\\" + wview_to_utf8(fd.cFileName, std::wcslen(fd.cFileName));
                    const char* subnames[2] = {"files", "client"};
                    for (const char* sub : subnames) {
                        const std::string candidate = mod_sub + "\\" + sub;
                        if (dir_exists(candidate)) {
                            uint64_t tok = 0;
                            if (overlay.register_file_overlay_dir(candidate.c_str(), &tok) == NM_OK) {
                                logger_logf("INFO", "[OVERLAY] Registered startup mod overlay dir '%s' (token=%llu)",
                                            candidate.c_str(), static_cast<unsigned long long>(tok));
                            }
                        }
                    }
                }
            } while (FindNextFileW(h, &fd));
            FindClose(h);
        }
    }

    // 2. Explicit NATIVEMODS_OVERLAY_DIR (semicolon-separated, registered last = highest disk LIFO priority!).
    const std::string env_ov = get_env_str(L"NATIVEMODS_OVERLAY_DIR");
    if (!env_ov.empty()) {
        size_t pos = 0;
        while (pos < env_ov.size()) {
            size_t sep = env_ov.find(';', pos);
            std::string entry = (sep == std::string::npos) ? env_ov.substr(pos) : env_ov.substr(pos, sep - pos);
            while (!entry.empty() && (entry.front() == ' ' || entry.front() == '\t')) entry.erase(entry.begin());
            while (!entry.empty() && (entry.back() == ' ' || entry.back() == '\t')) entry.pop_back();
            if (!entry.empty() && dir_exists(entry)) {
                uint64_t tok = 0;
                if (overlay.register_file_overlay_dir(entry.c_str(), &tok) == NM_OK) {
                    logger_logf("INFO", "[OVERLAY] Registered NATIVEMODS_OVERLAY_DIR '%s' (token=%llu)",
                                entry.c_str(), static_cast<unsigned long long>(tok));
                }
            }
            if (sep == std::string::npos) break;
            pos = sep + 1;
        }
    }
    logger_logf("INFO", "[OVERLAY] Pre-indexed %zu startup disk overlay file(s) for lock-free O(1) FileSystem filtering",
                overlay.indexed_disk_file_count());
#else
    (void)dll_dir_utf8;
    (void)overlay;
#endif
}

void LiveEngineHooks::install_live_hooks(
    nm_build_id_t       build_id,
    uint64_t            image_base,
    HookEngine&         hooks,
    GameAllocator&      allocator,
    OverlayAndChannels& overlay) {
    build_id_ = build_id;
    image_base_ = image_base;
    hooks_ = &hooks;

#if defined(_WIN32)
    if (build_id == NM_BUILD_SYNTHETIC || build_id == NM_BUILD_UNKNOWN || image_base == 0) {
        return;
    }

    g_live_build_id.store(build_id, std::memory_order_release);
    g_live_overlay.store(&overlay, std::memory_order_release);
    g_live_alloc.store(&allocator, std::memory_order_release);

    // Resolve HashToWstringView (0x0e40af0) for pathHash -> wstring_view lookup in FileSystem hooks.
    const EmbeddedSymbol* hash_wv_sym = find_symbol(build_id, "files.hash_to_wstring_view");
    if (hash_wv_sym && hash_wv_sym->rva != 0) {
        const uint64_t addr = image_base + hash_wv_sym->rva;
        if (is_ptr_readable(reinterpret_cast<const void*>(static_cast<uintptr_t>(addr)), 16) &&
            (!hash_wv_sym->expected_bytes_hex || verify_memory_bytes(addr, hash_wv_sym->expected_bytes_hex) == NM_OK)) {
            g_hash_to_wv.store(reinterpret_cast<HashToWStringViewFn>(static_cast<uintptr_t>(addr)), std::memory_order_release);
        }
    }

    auto arm_hook = [&](const char* sym_name, void* detour, auto& orig_atomic) -> bool {
        const EmbeddedSymbol* sym = find_symbol(build_id, sym_name);
        if (!sym || sym->rva == 0) return false;
        const uint64_t addr = image_base + sym->rva;
        if (!is_ptr_readable(reinterpret_cast<const void*>(static_cast<uintptr_t>(addr)), 24)) return false;

        void* tramp = nullptr;
        nm_hook_handle_t handle = NM_HOOK_INVALID;
        const nm_status_t st = hooks.install_inline(addr, sym->expected_bytes_hex, detour, &tramp, &handle);
        if (st == NM_OK && tramp != nullptr && handle != NM_HOOK_INVALID) {
            using FnT = std::remove_reference_t<decltype(orig_atomic.load())>;
            orig_atomic.store(reinterpret_cast<FnT>(tramp), std::memory_order_release);
            installed_handles_.push_back(handle);
            return true;
        }
        logger_logf("WARN", "[HOOKS] Failed to arm '%s' at 0x%llX (st=%d)",
                    sym_name, static_cast<unsigned long long>(addr), static_cast<int>(st));
        return false;
    };

    // 1. Universal FileSystem Overlay hooks (Route A)
    const bool ok_exists = arm_hook("files.exists_query",    reinterpret_cast<void*>(&detour_fs_exists),     g_orig_fs_exists);
    const bool ok_size   = arm_hook("files.size_query",      reinterpret_cast<void*>(&detour_fs_size),       g_orig_fs_size);
    const bool ok_range  = arm_hook("files.read_variant_2",  reinterpret_cast<void*>(&detour_fs_read_range), g_orig_fs_read_range);
    const bool ok_whole  = arm_hook("files.read_whole_file", reinterpret_cast<void*>(&detour_fs_read_whole), g_orig_fs_read_whole);
    const bool ok_v5     = arm_hook("files.read_variant_5",  reinterpret_cast<void*>(&detour_fs_read_v5),    g_orig_fs_read_v5);
    logger_logf("INFO", "[OVERLAY] Armed live FileSystem hooks (exists=%s size=%s read_range=%s read_whole=%s read_v5=%s hash_lookup=%s)",
                ok_exists ? "OK" : "SKIP",
                ok_size   ? "OK" : "SKIP",
                ok_range  ? "OK" : "SKIP",
                ok_whole  ? "OK" : "SKIP",
                ok_v5     ? "OK" : "SKIP",
                g_hash_to_wv.load() ? "OK" : "MISS");

    // 2. UI root observer hook
    const bool ok_ui = arm_hook("ui.control_load", reinterpret_cast<void*>(&detour_ui_control_load), g_orig_ui_control_load);
    logger_logf("INFO", "[UI] Armed Control::Load root observer (%s)", ok_ui ? "OK" : "SKIP");

    // 3. Mod channel hooks (Chat S->C 0x17 + Ruleset S->C 0x2c9 + MessagePump 0x02FC+)
    const bool ok_chat = arm_hook("network.chat_panel_listener", reinterpret_cast<void*>(&detour_chat_panel_listener), g_orig_chat_listener);
    const bool ok_rs   = arm_hook("rulesets.s2c_0x2c9_filter",   reinterpret_cast<void*>(&detour_ruleset_s2c_filter),  g_orig_ruleset_s2c);
    const EmbeddedSymbol* reg_grp_sym = find_symbol(build_id, "network.register_handler_group");
    if (reg_grp_sym && reg_grp_sym->rva != 0 &&
        is_ptr_readable(reinterpret_cast<const void*>(static_cast<uintptr_t>(image_base + reg_grp_sym->rva)), 16)) {
        g_register_handler_group.store(
            reinterpret_cast<RegisterGroupFn>(static_cast<uintptr_t>(image_base + reg_grp_sym->rva)),
            std::memory_order_release);
    }
    const bool ok_pump = arm_hook("network.message_pump", reinterpret_cast<void*>(&detour_network_message_pump), g_orig_message_pump);
    logger_logf("INFO", "[NET] Armed live channel hooks (chat_rx=%s ruleset_0x2c9=%s msg_pump=%s)",
                ok_chat ? "OK" : "SKIP",
                ok_rs   ? "OK" : "SKIP",
                ok_pump ? "OK" : "SKIP");
#else
    (void)hooks;
    (void)allocator;
    (void)overlay;
#endif
}

void LiveEngineHooks::reset(HookEngine* hooks) {
    if (hooks) {
        for (nm_hook_handle_t h : installed_handles_) {
            (void)hooks->remove(h);
        }
#if defined(_WIN32)
        if (g_tb_changed_vt_hook != NM_HOOK_INVALID) {
            (void)hooks->remove(g_tb_changed_vt_hook);
            g_tb_changed_vt_hook = NM_HOOK_INVALID;
        }
        if (g_tb_submitted_vt_hook != NM_HOOK_INVALID) {
            (void)hooks->remove(g_tb_submitted_vt_hook);
            g_tb_submitted_vt_hook = NM_HOOK_INVALID;
        }
#endif
    }
    installed_handles_.clear();
    hooks_ = nullptr;
    build_id_ = NM_BUILD_UNKNOWN;
    image_base_ = 0;

#if defined(_WIN32)
    g_live_build_id.store(NM_BUILD_UNKNOWN, std::memory_order_release);
    g_live_overlay.store(nullptr, std::memory_order_release);
    g_live_alloc.store(nullptr, std::memory_order_release);
    g_hash_to_wv.store(nullptr, std::memory_order_release);
    g_orig_fs_exists.store(nullptr, std::memory_order_release);
    g_orig_fs_size.store(nullptr, std::memory_order_release);
    g_orig_fs_read_range.store(nullptr, std::memory_order_release);
    g_orig_fs_read_whole.store(nullptr, std::memory_order_release);
    g_orig_fs_read_v5.store(nullptr, std::memory_order_release);
    g_orig_ui_control_load.store(nullptr, std::memory_order_release);
    g_orig_chat_listener.store(nullptr, std::memory_order_release);
    g_orig_ruleset_s2c.store(nullptr, std::memory_order_release);
    g_orig_message_pump.store(nullptr, std::memory_order_release);
    g_register_handler_group.store(nullptr, std::memory_order_release);
    g_orig_tb_text_changed.store(nullptr, std::memory_order_release);
    g_orig_tb_key_down.store(nullptr, std::memory_order_release);
    g_last_registered_pump.store(nullptr, std::memory_order_release);
    {
        std::lock_guard<std::mutex> lock(g_pumps_mutex);
        g_registered_pumps.clear();
    }
    g_captured_ui_root.store(nullptr, std::memory_order_release);
    g_cached_app_global_addr.store(0, std::memory_order_release);
    for (auto& slot : g_fs_miss_cache) {
        slot.store(0, std::memory_order_relaxed);
    }
    {
        std::lock_guard<std::mutex> lock(g_hit_log_mutex);
        g_logged_overlay_hits.clear();
    }
    {
        std::lock_guard<std::mutex> lock(g_ui_windows_mutex);
        g_ui_windows_root = nullptr;
        g_ui_loaded_windows.clear();
    }
    {
        std::lock_guard<std::mutex> lock(g_ui_handlers_mutex);
        for (UiClickHandlerRecord* rec : g_ui_handlers) {
            if (rec) {
                rec->cb = nullptr;
                rec->user_data = nullptr;
                delete rec;
            }
        }
        g_ui_handlers.clear();
        g_ui_token_map.clear();
        g_ui_button_slots.clear();
        g_ui_checkbox_slots.clear();
        g_ui_tb_changed_slots.clear();
        g_ui_tb_submitted_slots.clear();
    }
#endif
}

nm_status_t LiveEngineHooks::send_live_chat_line(const char* text_utf8) {
    if (!text_utf8 || text_utf8[0] == '\0') return NM_ERR_INVALID_ARG;
#if defined(_WIN32)
    const std::vector<uint8_t> wire = encode_poe_chat_wire(text_utf8);
    const nm_status_t st = write_socket_bytes_locked(build_id_, image_base_, wire.data(), wire.size());
    if (st == NM_OK) {
        logger_logf("INFO", "[NET] Sent C->S 0x15 chat line (%zu wire bytes)", wire.size());
    }
    return st;
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t LiveEngineHooks::send_live_ruleset_packet(uint32_t ruleset_id, const uint8_t* payload, uint32_t length) {
    if (ruleset_id >= NM_RULESET_SLOT_COUNT) return NM_ERR_INVALID_ARG;
    if (length > NM_RULESET_MAX_PAYLOAD) return NM_ERR_PAYLOAD_TOO_LARGE;
    if (length > 0 && payload == nullptr) return NM_ERR_INVALID_ARG;
#if defined(_WIN32)
    const std::vector<uint8_t> wire = encode_poe_ruleset_wire(ruleset_id, payload, length);
    const nm_status_t st = write_socket_bytes_locked(build_id_, image_base_, wire.data(), wire.size());
    if (st == NM_OK) {
        logger_logf("INFO", "[NET] Sent C->S 0x2ca ruleset=0x%02X (%u payload / %zu wire bytes)",
                    ruleset_id, length, wire.size());
    }
    return st;
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t LiveEngineHooks::send_live_raw_packet(uint16_t opcode, const uint8_t* payload, uint32_t length) {
    if (length == 0 || payload == nullptr) return NM_ERR_INVALID_ARG;
    if (length > 65536u) return NM_ERR_PAYLOAD_TOO_LARGE;
#if defined(_WIN32)
    const std::vector<uint8_t> wire = encode_poe_raw_wire(opcode, payload, length);
    const nm_status_t st = write_socket_bytes_locked(build_id_, image_base_, wire.data(), wire.size());
    if (st == NM_OK) {
        logger_logf("INFO", "[NET] Sent C->S raw opcode=0x%04X (%u payload / %zu wire bytes)",
                    opcode, length, wire.size());
    }
    return st;
#else
    (void)opcode;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t LiveEngineHooks::stream_read(void* stream_handle, void* dst, uint64_t length) {
    if (!stream_handle || (length > 0 && !dst)) return NM_ERR_INVALID_ARG;
    if (length == 0) return NM_OK;

    // Check synthetic in-memory stream first (used by nm_dispatch_opcode_rx & tests).
    uint64_t maybe_magic = 0;
    std::memcpy(&maybe_magic, stream_handle, sizeof(maybe_magic));
    if (maybe_magic == 0x4E4D53545245414Dull) {
        auto* ss = static_cast<SyntheticPacketStream*>(stream_handle);
        if (ss->cursor > ss->size || length > (ss->size - ss->cursor)) {
            return NM_ERR_BUFFER_TOO_SMALL;
        }
        if (ss->data == nullptr) return NM_ERR_INVALID_ARG;
        std::memcpy(dst, ss->data + ss->cursor, static_cast<size_t>(length));
        ss->cursor += length;
        return NM_OK;
    }

#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const EmbeddedSymbol* sym_read = find_symbol(build_id_, "network.stream_reader");
    if (!sym_read || sym_read->rva == 0) return NM_ERR_SYMBOL_NOT_FOUND;

    struct StreamCtx {
        void*    stream;
        void*    dst;
        uint64_t len;
        uint64_t fn_addr;
    } ctx{stream_handle, dst, length, image_base_ + sym_read->rva};

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<StreamCtx*>(raw);
        if (!is_ptr_readable(static_cast<const uint8_t*>(c->stream) + 0x252, 1)) return -1;
        using StreamReadFn = void*(NM_CALL*)(void*, void*, uint64_t);
        reinterpret_cast<StreamReadFn>(static_cast<uintptr_t>(c->fn_addr))(c->stream, c->dst, c->len);
        const uint8_t err = *reinterpret_cast<const uint8_t*>(static_cast<const uint8_t*>(c->stream) + 0x252);
        return (err == 0) ? 0 : -2;
    };

    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK) return st;
    if (rc == -2) return NM_ERR_BUFFER_TOO_SMALL;
    return (rc == 0) ? NM_OK : NM_ERR_GAME_EXCEPTION;
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t LiveEngineHooks::files_read_stock(
    const char*          virtual_path_utf8,
    OverlayAndChannels&  overlay,
    const GameAllocator& allocator,
    uint8_t**            out_data,
    size_t*              out_size) {
    if (!virtual_path_utf8 || virtual_path_utf8[0] == '\0' || !out_data || !out_size) {
        return NM_ERR_INVALID_ARG;
    }
    *out_data = nullptr;
    *out_size = 0;

#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const EmbeddedSymbol* sym_hash_reg = find_symbol(build_id_, "files.hash_and_register_path");
    const EmbeddedSymbol* sym_holder   = find_symbol(build_id_, "files.filesystem_holder_get");
    const EmbeddedSymbol* sym_read     = find_symbol(build_id_, "files.read_whole_file");
    const EmbeddedSymbol* sym_str_dtor = find_symbol(build_id_, "runtime.string_destroy");
    if (!sym_hash_reg || !sym_holder || !sym_read ||
        sym_hash_reg->rva == 0 || sym_holder->rva == 0 || sym_read->rva == 0) {
        return NM_ERR_SYMBOL_NOT_FOUND;
    }

    const std::string norm = OverlayAndChannels::normalize_virtual_path(virtual_path_utf8);
    if (norm.empty()) return NM_ERR_INVALID_ARG;

    auto orig_read = g_orig_fs_read_whole.load(std::memory_order_acquire);
    const uint64_t read_addr = orig_read
                                   ? static_cast<uint64_t>(reinterpret_cast<uintptr_t>(orig_read))
                                   : (image_base_ + sym_read->rva);

    struct ReadStockCtx {
        uint64_t             hash_reg_addr;
        uint64_t             holder_addr;
        uint64_t             read_addr;
        uint64_t             str_dtor_addr;
        std::wstring         wide_path;
        OverlayAndChannels*  overlay;
        const GameAllocator* allocator;
        uint8_t*             out_buf = nullptr;
        size_t               out_len = 0;
    } ctx{
        image_base_ + sym_hash_reg->rva,
        image_base_ + sym_holder->rva,
        read_addr,
        (sym_str_dtor && sym_str_dtor->rva) ? (image_base_ + sym_str_dtor->rva) : 0ull,
        utf8_to_wide_hooks(norm.c_str()),
        &overlay,
        &allocator,
        nullptr,
        0,
    };

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<ReadStockCtx*>(raw);
        ScopedFsReentrancyGuard guard;

        using HashRegFn   = uint64_t*(NM_CALL*)(uint64_t*, const WStringView*);
        using HolderGetFn = void*(NM_CALL*)();
        using StrDtorFn   = void(NM_CALL*)(void*);

        auto hash_reg   = reinterpret_cast<HashRegFn>(static_cast<uintptr_t>(c->hash_reg_addr));
        auto holder_get = reinterpret_cast<HolderGetFn>(static_cast<uintptr_t>(c->holder_addr));
        auto read_whole = reinterpret_cast<FsReadWholeFn>(static_cast<uintptr_t>(c->read_addr));
        auto str_dtor   = c->str_dtor_addr
                              ? reinterpret_cast<StrDtorFn>(static_cast<uintptr_t>(c->str_dtor_addr))
                              : nullptr;

        WStringView wv{c->wide_path.c_str(), static_cast<uint64_t>(c->wide_path.size())};
        uint64_t path_hash = 0;
        hash_reg(&path_hash, &wv);
        if (path_hash == 0) return -1;

        void* holder = holder_get();
        if (!holder || !is_ptr_readable(static_cast<const uint8_t*>(holder) + 0x08, sizeof(void*))) return -2;
        void* fs = *reinterpret_cast<void* const*>(static_cast<const uint8_t*>(holder) + 0x08);
        if (!fs) return -3;

        FsReadResult res{};
        std::memset(&res, 0, sizeof(res));
        res.err_cap = 15;
        read_whole(fs, &res, &path_hash);

        if (res.ok != 0 && res.begin != nullptr && res.end >= res.begin) {
            const size_t len = static_cast<size_t>(res.end - res.begin);
            c->out_buf = c->overlay->allocate_owned_buffer_copy(res.begin, len);
            c->out_len = len;

            size_t align = 16;
            if (is_ptr_readable(res.begin - sizeof(nm_game_alloc_header_t), sizeof(nm_game_alloc_header_t))) {
                const auto* hdr = reinterpret_cast<const nm_game_alloc_header_t*>(
                    res.begin - sizeof(nm_game_alloc_header_t));
                if (hdr->log2_align >= 4 && hdr->log2_align <= 12) {
                    align = size_t{1} << hdr->log2_align;
                }
            }
            (void)c->allocator->free(res.begin, align);
            if (str_dtor && res.err_cap > 15) {
                str_dtor(res.err_buf);
            }
            return 0;
        }

        if (str_dtor && res.err_cap > 15) {
            str_dtor(res.err_buf);
        }
        return -4;
    };

    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK) return st;
    if (rc == -4) return NM_ERR_SYMBOL_NOT_FOUND;
    if (rc != 0 || !ctx.out_buf) return NM_ERR_GAME_EXCEPTION;
    *out_data = ctx.out_buf;
    *out_size = ctx.out_len;
    return NM_OK;
#else
    (void)overlay;
    (void)allocator;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t LiveEngineHooks::files_invalidate(const char* virtual_path_utf8) {
    if (!virtual_path_utf8 || virtual_path_utf8[0] == '\0') return NM_ERR_INVALID_ARG;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const EmbeddedSymbol* sym_mgr = find_symbol(build_id_, "files.hot_reload_singleton");
    const EmbeddedSymbol* sym_inv = find_symbol(build_id_, "files.hot_reload_invalidate_ext");
    if (!sym_mgr || !sym_inv || sym_mgr->rva == 0 || sym_inv->rva == 0) {
        return NM_ERR_SYMBOL_NOT_FOUND;
    }

    const std::string norm = OverlayAndChannels::normalize_virtual_path(virtual_path_utf8);
    struct InvCtx {
        uint64_t     mgr_addr;
        uint64_t     inv_addr;
        std::wstring wide_path;
    } ctx{
        image_base_ + sym_mgr->rva,
        image_base_ + sym_inv->rva,
        utf8_to_wide_hooks(norm.c_str()),
    };

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<InvCtx*>(raw);
        using GetMgrFn = void*(NM_CALL*)();
        using InvalFn  = void(NM_CALL*)(void*, const WStringView*);
        void* mgr = reinterpret_cast<GetMgrFn>(static_cast<uintptr_t>(c->mgr_addr))();
        if (!mgr) return -1;
        WStringView wv{c->wide_path.c_str(), static_cast<uint64_t>(c->wide_path.size())};
        reinterpret_cast<InvalFn>(static_cast<uintptr_t>(c->inv_addr))(mgr, &wv);
        return 0;
    };

    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK) return st;
    return (rc == 0) ? NM_OK : NM_ERR_GAME_EXCEPTION;
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t LiveEngineHooks::get_active_connection_token(uint64_t* out_token) const {
    if (!out_token) return NM_ERR_INVALID_ARG;
    *out_token = 0;
#if defined(_WIN32)
    void* instance = nullptr;
    void* sock = nullptr;
    if (!resolve_live_connection_ptrs(build_id_, image_base_, &instance, &sock) || !instance || !sock) {
        return NM_OK;
    }

    // Mix InstanceClient pointer, SocketStream pointer, and the per-connect deadline timestamp
    // at instance+0xC400 (set at Game.exe 0x14214ebf4 on every instance server connect) so every
    // zone transition / reconnect produces a unique non-zero token even if heap addresses are reused.
    uint64_t connect_stamp = 0;
    if (is_ptr_readable(static_cast<const uint8_t*>(instance) + 0xC400, sizeof(uint64_t))) {
        connect_stamp = *reinterpret_cast<const uint64_t*>(static_cast<const uint8_t*>(instance) + 0xC400);
    }
    uint64_t tok = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(sock));
    tok ^= static_cast<uint64_t>(reinterpret_cast<uintptr_t>(instance)) * 0x9E3779B97F4A7C15ULL;
    tok ^= connect_stamp * 0xBF58476D1CE4E5B9ULL;
    tok ^= tok >> 33;
    tok *= 0xFF51AFD7ED558CCDULL;
    tok ^= tok >> 33;
    *out_token = (tok == 0) ? 1ULL : tok;
    return NM_OK;
#else
    return NM_OK;
#endif
}

nm_status_t LiveEngineHooks::ui_get_root(void** out_root) {
    if (!out_root) return NM_ERR_INVALID_ARG;
    *out_root = nullptr;
#if defined(_WIN32)
    *out_root = g_captured_ui_root.load(std::memory_order_acquire);
    return (*out_root != nullptr) ? NM_OK : NM_ERR_NOT_INITIALIZED;
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t LiveEngineHooks::ui_create_window_in_parent(void* parent_control, const char* virtual_path_utf8, void** out_control) {
    if (!parent_control || !virtual_path_utf8 || virtual_path_utf8[0] == '\0' || !out_control) return NM_ERR_INVALID_ARG;
    *out_control = nullptr;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) {
        return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    }

    void* root = g_captured_ui_root.load(std::memory_order_acquire);
    const std::string normalized_key = OverlayAndChannels::normalize_virtual_path(virtual_path_utf8);
    const UiWindowCacheKey cache_key{parent_control, normalized_key};
    {
        std::lock_guard<std::mutex> lock(g_ui_windows_mutex);
        if (root != nullptr && g_ui_windows_root != root) {
            // A new UI root (an area change, the game rebuilding its screens): windows loaded under the old root are
            // reloaded fresh. Click and change handlers stay: each control's own are dropped when the game destroys
            // it (the ui_destroy hook). Dropping all of them here silenced every live button whenever any window was
            // made after the root changed (a toast after an area change stopped all of a mod's buttons).
            g_ui_windows_root = root;
            g_ui_loaded_windows.clear();
        } else {
            auto it = g_ui_loaded_windows.find(cache_key);
            if (it != g_ui_loaded_windows.end() && it->second != nullptr) {
                if (is_control_in_parent_children(build_id_, parent_control, it->second) && is_ptr_readable(it->second, 0x200)) {
                    *out_control = it->second;
                    (void)ui_set_visible(it->second, 1);
                    logger_logf("INFO", "[UI] Reused existing native window '%s' (parent=%p, control=%p)",
                                virtual_path_utf8, parent_control, it->second);
                    return NM_OK;
                }
                logger_logf("INFO", "[UI] Cached native window '%s' (%p) no longer attached to parent %p; reloading fresh",
                            virtual_path_utf8, it->second, parent_control);
                g_ui_loaded_windows.erase(it);
            }
        }
    }

    const EmbeddedSymbol* sym_load    = find_symbol(build_id_, "ui.control_load");
    const EmbeddedSymbol* sym_assign  = find_symbol(build_id_, "runtime.wstring_assign");
    const EmbeddedSymbol* sym_destroy = find_symbol(build_id_, "runtime.wstring_destroy");
    if (!sym_load || !sym_assign) {
        return NM_ERR_SYMBOL_NOT_FOUND;
    }

    struct CreateWinCtx {
        void*        parent;
        uint64_t     load_addr;
        uint64_t     assign_addr;
        uint64_t     destroy_addr;
        size_t       children_begin_off;
        size_t       children_end_off;
        std::wstring wide_path;
        void*        created = nullptr;
    } ctx{
        parent_control,
        image_base_ + sym_load->rva,
        image_base_ + sym_assign->rva,
        (sym_destroy && sym_destroy->rva) ? (image_base_ + sym_destroy->rva) : 0ull,
        resolve_offset_or(build_id_, "ui.control.children_begin", 0xC0u),
        resolve_offset_or(build_id_, "ui.control.children_end", 0xC8u),
        utf8_to_wide_hooks(virtual_path_utf8),
        nullptr,
    };

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<CreateWinCtx*>(raw);
        if (!is_ptr_readable(static_cast<const uint8_t*>(c->parent) + c->children_begin_off, sizeof(void*)) ||
            !is_ptr_readable(static_cast<const uint8_t*>(c->parent) + c->children_end_off, sizeof(void*))) {
            return -1;
        }
        using LoadFn    = void(NM_CALL*)(void*, const void*);
        using AssignFn  = void*(NM_CALL*)(void*, const wchar_t*, uint64_t);
        using DestroyFn = void(NM_CALL*)(void*);

        auto load_fn    = reinterpret_cast<LoadFn>(static_cast<uintptr_t>(c->load_addr));
        auto assign_fn  = reinterpret_cast<AssignFn>(static_cast<uintptr_t>(c->assign_addr));
        auto destroy_fn = c->destroy_addr
                              ? reinterpret_cast<DestroyFn>(static_cast<uintptr_t>(c->destroy_addr))
                              : nullptr;

        auto* parent_bytes = static_cast<uint8_t*>(c->parent);
        auto* old_begin = *reinterpret_cast<uint8_t**>(parent_bytes + c->children_begin_off);
        auto* old_end   = *reinterpret_cast<uint8_t**>(parent_bytes + c->children_end_off);
        const size_t old_count = (old_end >= old_begin) ? static_cast<size_t>(old_end - old_begin) / sizeof(void*) : 0u;

        MsvcWString ws{};
        std::memset(&ws, 0, sizeof(ws));
        ws.cap = 7;
        assign_fn(&ws, c->wide_path.c_str(), static_cast<uint64_t>(c->wide_path.size()));
        load_fn(c->parent, &ws);
        if (destroy_fn) {
            destroy_fn(&ws);
        }

        auto* new_begin = *reinterpret_cast<uint8_t**>(parent_bytes + c->children_begin_off);
        auto* new_end   = *reinterpret_cast<uint8_t**>(parent_bytes + c->children_end_off);
        const size_t new_count = (new_end >= new_begin) ? static_cast<size_t>(new_end - new_begin) / sizeof(void*) : 0u;
        if (new_count > old_count && new_end != nullptr) {
            c->created = *(reinterpret_cast<void**>(new_end) - 1);
            return 0;
        }
        return -2;
    };

    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK || rc != 0 || !ctx.created) {
        logger_logf("ERROR", "[UI] ui_create_window('%s') failed (st=%d, rc=%lld, ex=0x%08X addr=0x%llX '%s')",
                    virtual_path_utf8, static_cast<int>(st), static_cast<long long>(rc), ex.exception_code,
                    static_cast<unsigned long long>(ex.exception_address), ex.message);
        return (st != NM_OK) ? st : NM_ERR_GAME_EXCEPTION;
    }

    {
        std::lock_guard<std::mutex> lock(g_ui_windows_mutex);
        if (root != nullptr) g_ui_windows_root = root;
        g_ui_loaded_windows[cache_key] = ctx.created;
    }

    *out_control = ctx.created;
    logger_logf("INFO", "[UI] Loaded native window '%s' onto parent=%p (control=%p)", virtual_path_utf8, parent_control, ctx.created);
    return NM_OK;
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t LiveEngineHooks::ui_create_window(const char* virtual_path_utf8, void** out_control) {
    if (!virtual_path_utf8 || virtual_path_utf8[0] == '\0' || !out_control) return NM_ERR_INVALID_ARG;
    *out_control = nullptr;
#if defined(_WIN32)
    void* root = g_captured_ui_root.load(std::memory_order_acquire);
    if (!root || build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) {
        return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    }
    return ui_create_window_in_parent(root, virtual_path_utf8, out_control);
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t LiveEngineHooks::ui_create_window_into(void* parent_control, const char* virtual_path_utf8, void** out_control) {
    return ui_create_window_in_parent(parent_control, virtual_path_utf8, out_control);
}

nm_status_t LiveEngineHooks::ui_find_child(void* parent_control, const char* dot_path_utf8, void** out_child) {
    if (!parent_control || !dot_path_utf8 || dot_path_utf8[0] == '\0' || !out_child) return NM_ERR_INVALID_ARG;
    *out_child = nullptr;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const EmbeddedSymbol* sym_find = find_symbol(build_id_, "ui.find_child");
    if (!sym_find || sym_find->rva == 0) return NM_ERR_SYMBOL_NOT_FOUND;

    struct FindCtx {
        nm_build_id_t build_id;
        void*         parent;
        uint64_t      find_addr;
        size_t        children_off;
        std::wstring  wide_path;
        void*         found = nullptr;
    } ctx{
        build_id_,
        parent_control,
        image_base_ + sym_find->rva,
        resolve_offset_or(build_id_, "ui.control.children_container", 0xB8u),
        utf8_to_wide_hooks(dot_path_utf8),
        nullptr,
    };

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<FindCtx*>(raw);
        if (!is_ptr_readable(static_cast<const uint8_t*>(c->parent) + c->children_off, 0x18)) return -1;
        using FindFn = void*(NM_CALL*)(void*, const WStringView*);
        auto find_fn = reinterpret_cast<FindFn>(static_cast<uintptr_t>(c->find_addr));
        WStringView wv{c->wide_path.c_str(), static_cast<uint64_t>(c->wide_path.size())};
        c->found = find_fn(static_cast<uint8_t*>(c->parent) + c->children_off, &wv);
        if (!c->found) {
            c->found = find_child_fallback_dot_path(c->build_id, c->parent, c->wide_path);
        }
        return 0;
    };

    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, nullptr, &ex);
    if (st != NM_OK) return st;
    *out_child = ctx.found;
    return ctx.found ? NM_OK : NM_ERR_SYMBOL_NOT_FOUND;
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t LiveEngineHooks::ui_set_visible(void* control, int32_t visible) {
    if (!control) return NM_ERR_INVALID_ARG;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const EmbeddedSymbol* sym_vis = find_symbol(build_id_, "ui.set_visible");
    if (!sym_vis || sym_vis->rva == 0) return NM_ERR_SYMBOL_NOT_FOUND;

    struct VisCtx {
        void*    control;
        uint8_t  vis;
        uint64_t fn_addr;
    } ctx{control, static_cast<uint8_t>(visible ? 1 : 0), image_base_ + sym_vis->rva};

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<VisCtx*>(raw);
        if (!is_ptr_readable(c->control, 0x1F0)) return -1;
        using SetVisFn = void(NM_CALL*)(void*, uint8_t);
        reinterpret_cast<SetVisFn>(static_cast<uintptr_t>(c->fn_addr))(c->control, c->vis);
        return 0;
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    return (st == NM_OK && rc == 0) ? NM_OK : NM_ERR_GAME_EXCEPTION;
#else
    (void)visible;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t LiveEngineHooks::ui_set_text(void* control, const char* text_utf8, int32_t is_button) {
    if (!control || !text_utf8) return NM_ERR_INVALID_ARG;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const EmbeddedSymbol* sym_assign = find_symbol(build_id_, "runtime.wstring_assign");
    if (!sym_assign || sym_assign->rva == 0) return NM_ERR_SYMBOL_NOT_FOUND;

    const EmbeddedSymbol* sym_btn_vt = find_symbol(build_id_, "ui.vtable_button");
    const EmbeddedSymbol* sym_lbl_vt = find_symbol(build_id_, "ui.vtable_label");
    const uint64_t btn_vt_addr = (sym_btn_vt && sym_btn_vt->rva != 0) ? (image_base_ + sym_btn_vt->rva) : 0ull;
    const uint64_t lbl_vt_addr = (sym_lbl_vt && sym_lbl_vt->rva != 0) ? (image_base_ + sym_lbl_vt->rva) : 0ull;

    struct TextCtx {
        void*        control;
        size_t       iface_offset;
        size_t       btn_iface_off;
        size_t       lbl_iface_off;
        uint64_t     btn_vt_addr;
        uint64_t     lbl_vt_addr;
        uint64_t     assign_addr;
        std::wstring wide_text;
    } ctx{
        control,
        is_button
            ? resolve_offset_or(build_id_, "ui.button.caption_interface", 0x3F8u)
            : resolve_offset_or(build_id_, "ui.label.text_interface", 0x298u),
        resolve_offset_or(build_id_, "ui.button.caption_interface", 0x3F8u),
        resolve_offset_or(build_id_, "ui.label.text_interface", 0x298u),
        btn_vt_addr,
        lbl_vt_addr,
        image_base_ + sym_assign->rva,
        utf8_to_wide_hooks(text_utf8),
    };

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<TextCtx*>(raw);
        if (!is_ptr_readable(c->control, sizeof(void*))) return -1;

        // Auto-verify Button (+0x3F8, internal child Label at +0x408) vs Label (+0x298)
        // against the real 3.29.3d primary vtable so callers can never hit a wrong subobject offset.
        const uint64_t primary_vt = reinterpret_cast<uint64_t>(*reinterpret_cast<void* const*>(c->control));
        size_t offset = c->iface_offset;
        bool target_is_button = (offset == c->btn_iface_off);
        if (c->btn_vt_addr != 0 && primary_vt == c->btn_vt_addr) {
            offset = c->btn_iface_off;
            target_is_button = true;
        } else if (c->lbl_vt_addr != 0 && primary_vt == c->lbl_vt_addr) {
            offset = c->lbl_iface_off;
            target_is_button = false;
        }

        if (target_is_button) {
            if (!is_ptr_readable(static_cast<const uint8_t*>(c->control) + c->btn_iface_off + 0x10, sizeof(void*))) return -2;
            void* inner_label = *reinterpret_cast<void* const*>(static_cast<const uint8_t*>(c->control) + c->btn_iface_off + 0x10);
            if (!inner_label) return -3;
        }

        void* sub = static_cast<uint8_t*>(c->control) + offset;
        if (!is_ptr_readable(sub, sizeof(void*))) return -4;
        void** vtbl = *reinterpret_cast<void***>(sub);
        if (!vtbl || !is_ptr_readable(vtbl, 3 * sizeof(void*)) || !vtbl[2]) return -5;

        using AssignFn  = void*(NM_CALL*)(void*, const wchar_t*, uint64_t);
        using SetTextFn = void(NM_CALL*)(void*, const void*);
        auto assign_fn = reinterpret_cast<AssignFn>(static_cast<uintptr_t>(c->assign_addr));
        auto set_text  = reinterpret_cast<SetTextFn>(vtbl[2]);

        // Both Button::SetText (0x1418f4c70) and Label::SetText (0x1418c2c10) take std::wstring
        // by value and destroy the passed buffer via 0x140134500 on return.
        MsvcWString ws{};
        std::memset(&ws, 0, sizeof(ws));
        ws.cap = 7;
        assign_fn(&ws, c->wide_text.c_str(), static_cast<uint64_t>(c->wide_text.size()));
        set_text(sub, &ws);
        return 0;
    };

    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK || rc != 0) {
        logger_logf("WARN", "[UI] ui_set_text(ctrl=%p, is_button=%d) failed (st=%d, rc=%lld, ex=0x%08X)",
                    control, is_button, static_cast<int>(st), static_cast<long long>(rc), ex.exception_code);
        return NM_ERR_GAME_EXCEPTION;
    }
    return NM_OK;
#else
    (void)is_button;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t LiveEngineHooks::ui_subscribe_click(
    void*                  button_control,
    nm_ui_event_callback_t cb,
    void*                  user_data,
    uint64_t*              out_token) {
    if (!button_control || !cb || !out_token) return NM_ERR_INVALID_ARG;
    *out_token = 0;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const EmbeddedSymbol* sym_get  = find_symbol(build_id_, "ui.event_list_get");
    const EmbeddedSymbol* sym_grow = find_symbol(build_id_, "ui.event_list_grow");
    if (!sym_get || !sym_grow || sym_get->rva == 0 || sym_grow->rva == 0) return NM_ERR_SYMBOL_NOT_FOUND;

    // Reuse existing handler slot for this native Button control so closing/reopening
    // a window never appends duplicate entries into PoE's button+0x2a0 event list!
    {
        std::lock_guard<std::mutex> lock(g_ui_handlers_mutex);
        auto it = g_ui_button_slots.find(button_control);
        if (it != g_ui_button_slots.end() && it->second != nullptr) {
            UiClickHandlerRecord* existing = it->second;
            g_ui_token_map.erase(existing->token);
            existing->token = g_ui_next_token++;
            existing->cb = cb;
            existing->user_data = user_data;
            g_ui_token_map[existing->token] = existing;
            *out_token = existing->token;
            return NM_OK;
        }
    }

    auto* rec = new UiClickHandlerRecord{};
    {
        std::lock_guard<std::mutex> lock(g_ui_handlers_mutex);
        rec->token = g_ui_next_token++;
        rec->control = button_control;
        rec->cb = cb;
        rec->user_data = user_data;
        g_ui_handlers.push_back(rec);
    }

    struct SubCtx {
        void*                 button;
        UiClickHandlerRecord* rec;
        uint64_t              get_addr;
        uint64_t              grow_addr;
        size_t                clicked_event_off;
    } ctx{
        button_control,
        rec,
        image_base_ + sym_get->rva,
        image_base_ + sym_grow->rva,
        resolve_offset_or(build_id_, "ui.button.clicked_event", 0x2A0u),
    };

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<SubCtx*>(raw);
        if (!is_ptr_readable(static_cast<const uint8_t*>(c->button) + c->clicked_event_off, sizeof(void*))) return -2;
        using EventGetFn  = void*(NM_CALL*)(void*, void*, uint8_t);
        using EventGrowFn = void(NM_CALL*)(void*);
        using StepFn      = uint64_t(NM_CALL*)(void*);

        auto event_get  = reinterpret_cast<EventGetFn>(static_cast<uintptr_t>(c->get_addr));
        auto event_grow = reinterpret_cast<EventGrowFn>(static_cast<uintptr_t>(c->grow_addr));

        void* shared[2] = {nullptr, nullptr};
        event_get(static_cast<uint8_t*>(c->button) + c->clicked_event_off, shared, 1);
        auto* list = static_cast<uint8_t*>(shared[0]);
        if (!list) return -1;

        auto* start = *reinterpret_cast<uint8_t**>(list + 8);
        auto* p = start;
        for (int n = 0; n < 64; ++n) {
            void* vt = *reinterpret_cast<void**>(p);
            if (!vt) break;
            auto step_fn = reinterpret_cast<StepFn>(reinterpret_cast<void**>(vt)[1]);
            p += step_fn(p);
        }

        const uint64_t cap = (start == (list + 0x10))
                                 ? 0x68ull
                                 : *reinterpret_cast<uint64_t*>(list + 0x10);
        if (static_cast<int64_t>(cap) - static_cast<int64_t>(p - start) < 0x18) {
            event_grow(list);
            auto* moved = *reinterpret_cast<uint8_t**>(list + 8);
            p = moved + (p - start);
            start = moved;
        }

        *reinterpret_cast<void**>(p)      = static_cast<void*>(g_ui_event_vtable);
        *reinterpret_cast<void**>(p + 8)  = static_cast<void*>(c->rec);
        *reinterpret_cast<void**>(p + 16) = nullptr;
        return 0;
    };

    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK || rc != 0) {
        std::lock_guard<std::mutex> lock(g_ui_handlers_mutex);
        rec->cb = nullptr;
        rec->user_data = nullptr;
        return NM_ERR_GAME_EXCEPTION;
    }

    {
        std::lock_guard<std::mutex> lock(g_ui_handlers_mutex);
        g_ui_button_slots[button_control] = rec;
        g_ui_token_map[rec->token] = rec;
    }

    *out_token = rec->token;
    return NM_OK;
#else
    (void)user_data;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t LiveEngineHooks::ui_get_rect(void* control, float* out_x, float* out_y, float* out_w, float* out_h) {
    if (!control || !out_x || !out_y || !out_w || !out_h) return NM_ERR_INVALID_ARG;
    *out_x = *out_y = *out_w = *out_h = 0.0f;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const EmbeddedSymbol* sym_pos = find_symbol(build_id_, "ui.control_get_abs_pos");
    if (!sym_pos || sym_pos->rva == 0) return NM_ERR_SYMBOL_NOT_FOUND;

    const size_t w_off = resolve_offset_or(build_id_, "ui.control.width", 0x268u);
    const size_t h_off = resolve_offset_or(build_id_, "ui.control.height", 0x26Cu);
    struct RectCtx {
        void*    control;
        uint64_t pos_addr;
        size_t   w_off;
        size_t   h_off;
        float    x = 0.0f;
        float    y = 0.0f;
        float    w = 0.0f;
        float    h = 0.0f;
    } ctx{control, image_base_ + sym_pos->rva, w_off, h_off};

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<RectCtx*>(raw);
        const size_t min_bytes = (c->h_off > c->w_off ? c->h_off : c->w_off) + sizeof(float);
        if (!is_ptr_readable(c->control, min_bytes)) return -1;
        using GetAbsPosFn = float*(NM_CALL*)(void*, float*);
        float xy[2] = {0.0f, 0.0f};
        reinterpret_cast<GetAbsPosFn>(static_cast<uintptr_t>(c->pos_addr))(c->control, xy);
        c->x = xy[0];
        c->y = xy[1];
        std::memcpy(&c->w, static_cast<const uint8_t*>(c->control) + c->w_off, sizeof(float));
        std::memcpy(&c->h, static_cast<const uint8_t*>(c->control) + c->h_off, sizeof(float));
        return 0;
    };

    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK || rc != 0) return (st != NM_OK) ? st : NM_ERR_GAME_EXCEPTION;
    *out_x = ctx.x;
    *out_y = ctx.y;
    *out_w = ctx.w;
    *out_h = ctx.h;
    return NM_OK;
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t LiveEngineHooks::ui_set_position(void* control, float x, float y) {
    if (!control) return NM_ERR_INVALID_ARG;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const EmbeddedSymbol* sym = find_symbol(build_id_, "ui.control_set_position");
    if (!sym || sym->rva == 0) return NM_ERR_SYMBOL_NOT_FOUND;

    struct PosCtx { void* control; uint64_t addr; float x; float y; } ctx{control, image_base_ + sym->rva, x, y};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<PosCtx*>(raw);
        if (!is_ptr_readable(c->control, 0x1E8)) return -1;
        using SetPosFn = void(NM_CALL*)(void*, float, float);
        reinterpret_cast<SetPosFn>(static_cast<uintptr_t>(c->addr))(c->control, c->x, c->y);
        return 0;
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    return (st == NM_OK && rc == 0) ? NM_OK : NM_ERR_GAME_EXCEPTION;
#else
    (void)x;
    (void)y;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t LiveEngineHooks::ui_set_size(void* control, float w, float h) {
    if (!control) return NM_ERR_INVALID_ARG;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const EmbeddedSymbol* sym = find_symbol(build_id_, "ui.control_set_size");
    if (!sym || sym->rva == 0) return NM_ERR_SYMBOL_NOT_FOUND;

    struct SizeCtx { void* control; uint64_t addr; float w; float h; } ctx{control, image_base_ + sym->rva, w, h};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<SizeCtx*>(raw);
        if (!is_ptr_readable(c->control, 0x280)) return -1;
        using SetSizeFn = void(NM_CALL*)(void*, float, float);
        reinterpret_cast<SetSizeFn>(static_cast<uintptr_t>(c->addr))(c->control, c->w, c->h);
        return 0;
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    return (st == NM_OK && rc == 0) ? NM_OK : NM_ERR_GAME_EXCEPTION;
#else
    (void)w;
    (void)h;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t LiveEngineHooks::ui_set_enabled(void* control, int32_t enabled) {
    if (!control) return NM_ERR_INVALID_ARG;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;

    struct EnableCtx { void* control; uint8_t en; } ctx{control, static_cast<uint8_t>(enabled ? 1 : 0)};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<EnableCtx*>(raw);
        if (!is_ptr_readable(c->control, sizeof(void*))) return -1;
        void** vtbl = *reinterpret_cast<void***>(c->control);
        if (!vtbl || !is_ptr_readable(vtbl, 11 * sizeof(void*)) || !vtbl[10]) return -2;
        using SetEnabledFn = void(NM_CALL*)(void*, uint8_t);
        reinterpret_cast<SetEnabledFn>(vtbl[10])(c->control, c->en);
        return 0;
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    return (st == NM_OK && rc == 0) ? NM_OK : NM_ERR_GAME_EXCEPTION;
#else
    (void)enabled;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t LiveEngineHooks::ui_get_enabled(void* control, int32_t* out_enabled) {
    if (!control || !out_enabled) return NM_ERR_INVALID_ARG;
    *out_enabled = 0;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const EmbeddedOffset* flags_off = find_offset(build_id_, "ui.control.flags");
    if (!flags_off) return NM_ERR_SYMBOL_NOT_FOUND;

    struct GetEnabledCtx {
        void*   control;
        size_t  flags_offset;
        int32_t enabled = 0;
    } ctx{control, flags_off->offset, 0};

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<GetEnabledCtx*>(raw);
        const uint8_t* ptr = static_cast<const uint8_t*>(c->control) + c->flags_offset;
        if (!is_ptr_readable(ptr, sizeof(uint32_t))) return -1;
        uint32_t flags = 0;
        std::memcpy(&flags, ptr, sizeof(uint32_t));
        c->enabled = ((flags & 0x2000u) != 0u) ? 1 : 0;
        return 0;
    };

    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK || rc != 0) return (st != NM_OK) ? st : NM_ERR_GAME_EXCEPTION;
    *out_enabled = ctx.enabled;
    return NM_OK;
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t LiveEngineHooks::ui_remove_child(void* parent_control, void* child_control) {
    if (!parent_control || !child_control) return NM_ERR_INVALID_ARG;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const EmbeddedSymbol* sym = find_symbol(build_id_, "ui.control_detach");
    if (!sym || sym->rva == 0) return NM_ERR_SYMBOL_NOT_FOUND;

    invalidate_control_cache_and_handlers(child_control);

    const size_t parent_off = resolve_offset_or(build_id_, "ui.control.parent", 0x1E0u);
    struct DetachCtx { void* parent; void* child; uint64_t addr; size_t parent_off; } ctx{
        parent_control, child_control, image_base_ + sym->rva, parent_off};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<DetachCtx*>(raw);
        if (!is_ptr_readable(static_cast<const uint8_t*>(c->child) + c->parent_off, sizeof(void*))) return -1;
        void* cur_parent = *reinterpret_cast<void* const*>(static_cast<const uint8_t*>(c->child) + c->parent_off);
        if (cur_parent != c->parent) return -2;
        using DetachFn = void(NM_CALL*)(void*);
        reinterpret_cast<DetachFn>(static_cast<uintptr_t>(c->addr))(c->child);
        return 0;
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK) return st;
    if (rc == -2) return NM_ERR_INVALID_ARG;
    return (rc == 0) ? NM_OK : NM_ERR_GAME_EXCEPTION;
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t LiveEngineHooks::ui_destroy_control(void* control) {
    if (!control) return NM_ERR_INVALID_ARG;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    if (control == g_captured_ui_root.load(std::memory_order_acquire)) {
        return NM_ERR_INVALID_ARG;
    }

    const EmbeddedSymbol* sym_detach = find_symbol(build_id_, "ui.control_detach");
    const EmbeddedSymbol* sym_erase  = find_symbol(build_id_, "ui.children_remove_at");
    const uint64_t detach_addr = (sym_detach && sym_detach->rva != 0) ? (image_base_ + sym_detach->rva) : 0ull;

    uint64_t release_or_destroy_addr = 0ull;
    if (sym_erase && sym_erase->rva != 0) {
        static constexpr uint8_t kReleaseSig[16] = {
            0x40, 0x56, 0x48, 0x83, 0xEC, 0x20, 0xF6, 0x81,
            0xE8, 0x01, 0x00, 0x00, 0x01, 0x48, 0x8B, 0xF1,
        };
        const uint8_t* scan_base = reinterpret_cast<const uint8_t*>(static_cast<uintptr_t>(image_base_ + sym_erase->rva));
        if (is_ptr_readable(scan_base, 0x300)) {
            for (size_t off = 0x80; off + sizeof(kReleaseSig) <= 0x300; ++off) {
                if (std::memcmp(scan_base + off, kReleaseSig, sizeof(kReleaseSig)) == 0) {
                    release_or_destroy_addr = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(scan_base + off));
                    break;
                }
            }
        }
    }

    invalidate_control_cache_and_handlers(control);

    const size_t parent_off = resolve_offset_or(build_id_, "ui.control.parent", 0x1E0u);
    struct DestroyCtx { void* control; uint64_t detach_addr; uint64_t release_addr; size_t parent_off; } ctx{
        control, detach_addr, release_or_destroy_addr, parent_off};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<DestroyCtx*>(raw);
        if (!is_ptr_readable(c->control, c->parent_off + sizeof(void*))) return -1;
        void* parent = *reinterpret_cast<void* const*>(static_cast<const uint8_t*>(c->control) + c->parent_off);
        if (parent != nullptr && c->detach_addr != 0) {
            using DetachFn = void(NM_CALL*)(void*);
            reinterpret_cast<DetachFn>(static_cast<uintptr_t>(c->detach_addr))(c->control);
        }
        if (c->release_addr != 0) {
            using ReleaseOrDestroyFn = void(NM_CALL*)(void*);
            reinterpret_cast<ReleaseOrDestroyFn>(static_cast<uintptr_t>(c->release_addr))(c->control);
            return 0;
        }
        // Fallback only when control is heap-owned (pool_size at +0x08 == 0); slab-pooled controls
        // MUST go through Control::ReleaseOrDestroy so their slab bucket receives them instead of free().
        const uint64_t pool_size = *reinterpret_cast<const uint64_t*>(static_cast<const uint8_t*>(c->control) + 0x08);
        if (pool_size != 0) return -3;
        void** vtbl = *reinterpret_cast<void***>(c->control);
        if (!vtbl || !is_ptr_readable(vtbl, 9 * sizeof(void*)) || !vtbl[8]) return -2;
        using DeletingDtorFn = void*(NM_CALL*)(void*, uint32_t);
        reinterpret_cast<DeletingDtorFn>(vtbl[8])(c->control, 1u);
        return 0;
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    return (st == NM_OK && rc == 0) ? NM_OK : NM_ERR_GAME_EXCEPTION;
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t LiveEngineHooks::ui_textbox_get_text(void* textbox_control, char* out_utf8, size_t capacity, size_t* out_length) {
    if (!textbox_control) return NM_ERR_INVALID_ARG;
    if (out_length) *out_length = 0;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;

    const uint64_t expected_vt = resolve_vtable_addr(build_id_, image_base_, "ui.vtable_textbox");
    const size_t content_off = resolve_offset_or(build_id_, "ui.textbox.content", 0x338u);
    struct GetTextCtx {
        void*       ctrl;
        uint64_t    expected_vt;
        size_t      content_off;
        std::string text;
    } ctx{textbox_control, expected_vt, content_off, {}};

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<GetTextCtx*>(raw);
        if (!is_ptr_readable(c->ctrl, sizeof(void*))) return -1;
        if (c->expected_vt != 0) {
            const uint64_t actual_vt = reinterpret_cast<uint64_t>(*reinterpret_cast<void* const*>(c->ctrl));
            if (actual_vt != c->expected_vt) return -10;
        }
        if (!is_ptr_readable(static_cast<const uint8_t*>(c->ctrl) + c->content_off, sizeof(MsvcWString))) return -1;
        c->text = read_msvc_wstring_utf8(static_cast<const uint8_t*>(c->ctrl) + c->content_off);
        return 0;
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK) return st;
    if (rc == -10) return NM_ERR_INVALID_ARG;
    if (rc != 0) return NM_ERR_GAME_EXCEPTION;

    if (out_length) *out_length = ctx.text.size();
    if (!out_utf8 && capacity == 0) return NM_OK;
    if (!out_utf8 || capacity < ctx.text.size() + 1) return NM_ERR_BUFFER_TOO_SMALL;
    if (!ctx.text.empty()) std::memcpy(out_utf8, ctx.text.data(), ctx.text.size());
    out_utf8[ctx.text.size()] = '\0';
    return NM_OK;
#else
    (void)out_utf8;
    (void)capacity;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t LiveEngineHooks::ui_textbox_set_text(void* textbox_control, const char* text_utf8) {
    if (!textbox_control || !text_utf8) return NM_ERR_INVALID_ARG;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const EmbeddedSymbol* sym_assign = find_symbol(build_id_, "runtime.wstring_assign");
    if (!sym_assign || sym_assign->rva == 0) return NM_ERR_SYMBOL_NOT_FOUND;

    const uint64_t expected_vt = resolve_vtable_addr(build_id_, image_base_, "ui.vtable_textbox");
    const size_t ti_off = resolve_offset_or(build_id_, "ui.textbox.text_interface", 0x298u);
    struct SetTextCtx {
        void*        ctrl;
        uint64_t     expected_vt;
        size_t       ti_off;
        uint64_t     assign_addr;
        std::wstring wide;
    } ctx{textbox_control, expected_vt, ti_off, image_base_ + sym_assign->rva, utf8_to_wide_hooks(text_utf8)};

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<SetTextCtx*>(raw);
        if (!is_ptr_readable(c->ctrl, sizeof(void*))) return -1;
        if (c->expected_vt != 0) {
            const uint64_t actual_vt = reinterpret_cast<uint64_t>(*reinterpret_cast<void* const*>(c->ctrl));
            if (actual_vt != c->expected_vt) return -10;
        }
        void* sub = static_cast<uint8_t*>(c->ctrl) + c->ti_off;
        if (!is_ptr_readable(sub, sizeof(void*))) return -1;
        void** vtbl = *reinterpret_cast<void***>(sub);
        if (!vtbl || !is_ptr_readable(vtbl, 3 * sizeof(void*)) || !vtbl[2]) return -2;

        using AssignFn  = void*(NM_CALL*)(void*, const wchar_t*, uint64_t);
        using SetTextFn = void(NM_CALL*)(void*, void*);
        auto assign_fn = reinterpret_cast<AssignFn>(static_cast<uintptr_t>(c->assign_addr));
        auto set_text  = reinterpret_cast<SetTextFn>(vtbl[2]);

        MsvcWString ws{};
        std::memset(&ws, 0, sizeof(ws));
        ws.cap = 7;
        assign_fn(&ws, c->wide.c_str(), static_cast<uint64_t>(c->wide.size()));
        // 0x14188f880 moves & destroys ws internally — do NOT call wstring_destroy!
        set_text(sub, &ws);
        return 0;
    };

    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK) return st;
    if (rc == -10) return NM_ERR_INVALID_ARG;
    return (rc == 0) ? NM_OK : NM_ERR_GAME_EXCEPTION;
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t LiveEngineHooks::ui_textbox_subscribe_changed(
    void*                  textbox_control,
    nm_ui_event_callback_t cb,
    void*                  user_data,
    uint64_t*              out_token) {
    if (!textbox_control || !cb || !out_token) return NM_ERR_INVALID_ARG;
    *out_token = 0;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0 || !hooks_) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const EmbeddedSymbol* sym_vt = find_symbol(build_id_, "ui.vtable_textbox");
    if (!sym_vt || sym_vt->rva == 0) return NM_ERR_SYMBOL_NOT_FOUND;
    const nm_status_t vtst = verify_control_vtable_guarded(textbox_control, image_base_ + sym_vt->rva);
    if (vtst != NM_OK) return vtst;

    {
        std::lock_guard<std::mutex> lock(g_ui_handlers_mutex);
        if (g_tb_changed_vt_hook == NM_HOOK_INVALID) {
            void* orig = nullptr;
            const nm_status_t hst = hooks_->install_vtable(
                reinterpret_cast<void*>(static_cast<uintptr_t>(image_base_ + sym_vt->rva)),
                129u,
                reinterpret_cast<void*>(&detour_tb_text_changed),
                &orig,
                &g_tb_changed_vt_hook);
            if (hst != NM_OK) return hst;
            g_orig_tb_text_changed.store(reinterpret_cast<TbTextChangedFn>(orig), std::memory_order_release);
        }

        auto it = g_ui_tb_changed_slots.find(textbox_control);
        if (it != g_ui_tb_changed_slots.end() && it->second != nullptr) {
            g_ui_token_map.erase(it->second->token);
            it->second->token = g_ui_next_token++;
            it->second->cb = cb;
            it->second->user_data = user_data;
            g_ui_token_map[it->second->token] = it->second;
            *out_token = it->second->token;
            return NM_OK;
        }
        auto* rec = new UiClickHandlerRecord{g_ui_next_token++, textbox_control, cb, user_data};
        g_ui_handlers.push_back(rec);
        g_ui_tb_changed_slots[textbox_control] = rec;
        g_ui_token_map[rec->token] = rec;
        *out_token = rec->token;
    }
    return NM_OK;
#else
    (void)user_data;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t LiveEngineHooks::ui_textbox_subscribe_submitted(
    void*                  textbox_control,
    nm_ui_event_callback_t cb,
    void*                  user_data,
    uint64_t*              out_token) {
    if (!textbox_control || !cb || !out_token) return NM_ERR_INVALID_ARG;
    *out_token = 0;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0 || !hooks_) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const EmbeddedSymbol* sym_vt = find_symbol(build_id_, "ui.vtable_textbox");
    if (!sym_vt || sym_vt->rva == 0) return NM_ERR_SYMBOL_NOT_FOUND;
    const nm_status_t vtst = verify_control_vtable_guarded(textbox_control, image_base_ + sym_vt->rva);
    if (vtst != NM_OK) return vtst;

    {
        std::lock_guard<std::mutex> lock(g_ui_handlers_mutex);
        if (g_tb_submitted_vt_hook == NM_HOOK_INVALID) {
            void* orig = nullptr;
            const nm_status_t hst = hooks_->install_vtable(
                reinterpret_cast<void*>(static_cast<uintptr_t>(image_base_ + sym_vt->rva)),
                72u,
                reinterpret_cast<void*>(&detour_tb_key_down),
                &orig,
                &g_tb_submitted_vt_hook);
            if (hst != NM_OK) return hst;
            g_orig_tb_key_down.store(reinterpret_cast<TbKeyDownFn>(orig), std::memory_order_release);
        }

        auto it = g_ui_tb_submitted_slots.find(textbox_control);
        if (it != g_ui_tb_submitted_slots.end() && it->second != nullptr) {
            g_ui_token_map.erase(it->second->token);
            it->second->token = g_ui_next_token++;
            it->second->cb = cb;
            it->second->user_data = user_data;
            g_ui_token_map[it->second->token] = it->second;
            *out_token = it->second->token;
            return NM_OK;
        }
        auto* rec = new UiClickHandlerRecord{g_ui_next_token++, textbox_control, cb, user_data};
        g_ui_handlers.push_back(rec);
        g_ui_tb_submitted_slots[textbox_control] = rec;
        g_ui_token_map[rec->token] = rec;
        *out_token = rec->token;
    }
    return NM_OK;
#else
    (void)user_data;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t LiveEngineHooks::ui_checkbox_get_checked(void* checkbox_control, int32_t* out_checked) {
    if (!checkbox_control || !out_checked) return NM_ERR_INVALID_ARG;
    *out_checked = 0;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const uint64_t expected_vt = resolve_vtable_addr(build_id_, image_base_, "ui.vtable_checkbox");
    const size_t checked_off = resolve_offset_or(build_id_, "ui.checkbox.checked", 0x4E2u);
    struct GetCtx { void* ctrl; uint64_t expected_vt; size_t checked_off; int32_t checked = 0; } ctx{
        checkbox_control, expected_vt, checked_off, 0};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<GetCtx*>(raw);
        if (!is_ptr_readable(c->ctrl, sizeof(void*))) return -1;
        if (c->expected_vt != 0) {
            const uint64_t actual_vt = reinterpret_cast<uint64_t>(*reinterpret_cast<void* const*>(c->ctrl));
            if (actual_vt != c->expected_vt) return -10;
        }
        if (!is_ptr_readable(static_cast<const uint8_t*>(c->ctrl) + c->checked_off, 1)) return -1;
        c->checked = (*reinterpret_cast<const uint8_t*>(static_cast<const uint8_t*>(c->ctrl) + c->checked_off) != 0) ? 1 : 0;
        return 0;
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK) return st;
    if (rc == -10) return NM_ERR_INVALID_ARG;
    if (rc != 0) return NM_ERR_GAME_EXCEPTION;
    *out_checked = ctx.checked;
    return NM_OK;
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t LiveEngineHooks::ui_checkbox_set_checked(void* checkbox_control, int32_t checked) {
    if (!checkbox_control) return NM_ERR_INVALID_ARG;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const uint64_t expected_vt = resolve_vtable_addr(build_id_, image_base_, "ui.vtable_checkbox");
    struct SetCtx { void* ctrl; uint64_t expected_vt; uint8_t val; } ctx{
        checkbox_control, expected_vt, static_cast<uint8_t>(checked ? 1 : 0)};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<SetCtx*>(raw);
        if (!is_ptr_readable(c->ctrl, sizeof(void*))) return -1;
        void** vtbl = *reinterpret_cast<void***>(c->ctrl);
        if (c->expected_vt != 0 && reinterpret_cast<uint64_t>(vtbl) != c->expected_vt) return -10;
        if (!vtbl || !is_ptr_readable(vtbl, 116 * sizeof(void*)) || !vtbl[115]) return -2;
        using SetCheckedFn = void(NM_CALL*)(void*, uint8_t, uint8_t);
        reinterpret_cast<SetCheckedFn>(vtbl[115])(c->ctrl, c->val, 1);
        return 0;
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK) return st;
    if (rc == -10) return NM_ERR_INVALID_ARG;
    return (rc == 0) ? NM_OK : NM_ERR_GAME_EXCEPTION;
#else
    (void)checked;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t LiveEngineHooks::ui_checkbox_subscribe_changed(
    void*                  checkbox_control,
    nm_ui_event_callback_t cb,
    void*                  user_data,
    uint64_t*              out_token) {
    if (!checkbox_control || !cb || !out_token) return NM_ERR_INVALID_ARG;
    *out_token = 0;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const uint64_t expected_vt = resolve_vtable_addr(build_id_, image_base_, "ui.vtable_checkbox");
    const nm_status_t vtst = verify_control_vtable_guarded(checkbox_control, expected_vt);
    if (vtst != NM_OK) return vtst;
    const EmbeddedSymbol* sym_get  = find_symbol(build_id_, "ui.event_list_get");
    const EmbeddedSymbol* sym_grow = find_symbol(build_id_, "ui.event_list_grow");
    if (!sym_get || !sym_grow || sym_get->rva == 0 || sym_grow->rva == 0) return NM_ERR_SYMBOL_NOT_FOUND;

    {
        std::lock_guard<std::mutex> lock(g_ui_handlers_mutex);
        auto it = g_ui_checkbox_slots.find(checkbox_control);
        if (it != g_ui_checkbox_slots.end() && it->second != nullptr) {
            g_ui_token_map.erase(it->second->token);
            it->second->token = g_ui_next_token++;
            it->second->cb = cb;
            it->second->user_data = user_data;
            g_ui_token_map[it->second->token] = it->second;
            *out_token = it->second->token;
            return NM_OK;
        }
    }

    auto* rec = new UiClickHandlerRecord{};
    {
        std::lock_guard<std::mutex> lock(g_ui_handlers_mutex);
        rec->token = g_ui_next_token++;
        rec->control = checkbox_control;
        rec->cb = cb;
        rec->user_data = user_data;
        g_ui_handlers.push_back(rec);
    }

    const size_t event_key_off = resolve_offset_or(build_id_, "ui.checkbox.event_key", 0x418u);
    struct SubCtx {
        void*                 checkbox;
        UiClickHandlerRecord* rec;
        uint64_t              get_addr;
        uint64_t              grow_addr;
        size_t                event_key_off;
    } ctx{checkbox_control, rec, image_base_ + sym_get->rva, image_base_ + sym_grow->rva, event_key_off};

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<SubCtx*>(raw);
        if (!is_ptr_readable(static_cast<const uint8_t*>(c->checkbox) + c->event_key_off, sizeof(void*))) return -2;
        using EventGetFn  = void*(NM_CALL*)(void*, void*, uint8_t);
        using EventGrowFn = void(NM_CALL*)(void*);
        using StepFn      = uint64_t(NM_CALL*)(void*);

        auto event_get  = reinterpret_cast<EventGetFn>(static_cast<uintptr_t>(c->get_addr));
        auto event_grow = reinterpret_cast<EventGrowFn>(static_cast<uintptr_t>(c->grow_addr));

        void* shared[2] = {nullptr, nullptr};
        event_get(static_cast<uint8_t*>(c->checkbox) + c->event_key_off, shared, 1);
        auto* list = static_cast<uint8_t*>(shared[0]);
        if (!list) return -1;

        auto* start = *reinterpret_cast<uint8_t**>(list + 8);
        auto* p = start;
        for (int n = 0; n < 64; ++n) {
            void* vt = *reinterpret_cast<void**>(p);
            if (!vt) break;
            auto step_fn = reinterpret_cast<StepFn>(reinterpret_cast<void**>(vt)[1]);
            p += step_fn(p);
        }

        const uint64_t cap = (start == (list + 0x10))
                                 ? 0x68ull
                                 : *reinterpret_cast<uint64_t*>(list + 0x10);
        if (static_cast<int64_t>(cap) - static_cast<int64_t>(p - start) < 0x18) {
            event_grow(list);
            auto* moved = *reinterpret_cast<uint8_t**>(list + 8);
            p = moved + (p - start);
            start = moved;
        }

        *reinterpret_cast<void**>(p)      = static_cast<void*>(g_ui_event_vtable);
        *reinterpret_cast<void**>(p + 8)  = static_cast<void*>(c->rec);
        *reinterpret_cast<void**>(p + 16) = nullptr;
        return 0;
    };

    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK || rc != 0) {
        std::lock_guard<std::mutex> lock(g_ui_handlers_mutex);
        rec->cb = nullptr;
        rec->user_data = nullptr;
        return NM_ERR_GAME_EXCEPTION;
    }

    {
        std::lock_guard<std::mutex> lock(g_ui_handlers_mutex);
        g_ui_checkbox_slots[checkbox_control] = rec;
        g_ui_token_map[rec->token] = rec;
    }

    *out_token = rec->token;
    return NM_OK;
#else
    (void)user_data;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t LiveEngineHooks::ui_unsubscribe(uint64_t token) {
    if (token == 0) return NM_ERR_INVALID_ARG;
#if defined(_WIN32)
    std::lock_guard<std::mutex> lock(g_ui_handlers_mutex);
    auto it = g_ui_token_map.find(token);
    if (it == g_ui_token_map.end() || it->second == nullptr) {
        return NM_ERR_INVALID_ARG;
    }
    UiClickHandlerRecord* rec = it->second;
    if (rec->token == token) {
        rec->cb = nullptr;
        rec->user_data = nullptr;
    }
    g_ui_token_map.erase(it);
    return NM_OK;
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

#if !defined(_WIN32)
namespace {
std::mutex  g_stub_clipboard_mutex;
std::string g_stub_clipboard_utf8;
} // namespace
#endif

nm_status_t LiveEngineHooks::clipboard_get_text(char* out_utf8, size_t capacity, size_t* out_length) {
    if (out_length) *out_length = 0;
#if defined(_WIN32)
    std::string utf8;
    if (OpenClipboard(nullptr)) {
        HANDLE h = GetClipboardData(CF_UNICODETEXT);
        if (h) {
            const auto* w = static_cast<const wchar_t*>(GlobalLock(h));
            if (w) {
                utf8 = wview_to_utf8(w, std::wcslen(w));
                GlobalUnlock(h);
            }
        }
        CloseClipboard();
    }
    if (out_length) *out_length = utf8.size();
    if (!out_utf8 && capacity == 0) return NM_OK;
    if (!out_utf8 || capacity < utf8.size() + 1) return NM_ERR_BUFFER_TOO_SMALL;
    if (!utf8.empty()) std::memcpy(out_utf8, utf8.data(), utf8.size());
    out_utf8[utf8.size()] = '\0';
    return NM_OK;
#else
    std::lock_guard<std::mutex> lock(g_stub_clipboard_mutex);
    if (out_length) *out_length = g_stub_clipboard_utf8.size();
    if (!out_utf8 && capacity == 0) return NM_OK;
    if (!out_utf8 || capacity < g_stub_clipboard_utf8.size() + 1) return NM_ERR_BUFFER_TOO_SMALL;
    if (!g_stub_clipboard_utf8.empty()) {
        std::memcpy(out_utf8, g_stub_clipboard_utf8.data(), g_stub_clipboard_utf8.size());
    }
    out_utf8[g_stub_clipboard_utf8.size()] = '\0';
    return NM_OK;
#endif
}

nm_status_t LiveEngineHooks::clipboard_set_text(const char* text_utf8) {
    if (!text_utf8) return NM_ERR_INVALID_ARG;
#if defined(_WIN32)
    const int needed = MultiByteToWideChar(CP_UTF8, 0, text_utf8, -1, nullptr, 0);
    if (needed <= 0) return NM_ERR_INVALID_ARG;
    const size_t bytes = static_cast<size_t>(needed) * sizeof(wchar_t);
    HGLOBAL hmem = GlobalAlloc(GMEM_MOVEABLE, bytes);
    if (!hmem) return NM_ERR_ALLOC_FAILED;
    auto* dst = static_cast<wchar_t*>(GlobalLock(hmem));
    if (!dst) {
        GlobalFree(hmem);
        return NM_ERR_ALLOC_FAILED;
    }
    MultiByteToWideChar(CP_UTF8, 0, text_utf8, -1, dst, needed);
    GlobalUnlock(hmem);

    if (!OpenClipboard(nullptr)) {
        GlobalFree(hmem);
        return NM_ERR_GAME_EXCEPTION;
    }
    EmptyClipboard();
    if (!SetClipboardData(CF_UNICODETEXT, hmem)) {
        CloseClipboard();
        GlobalFree(hmem);
        return NM_ERR_GAME_EXCEPTION;
    }
    CloseClipboard();
    return NM_OK;
#else
    std::lock_guard<std::mutex> lock(g_stub_clipboard_mutex);
    g_stub_clipboard_utf8 = text_utf8;
    return NM_OK;
#endif
}

} // namespace nativemods
