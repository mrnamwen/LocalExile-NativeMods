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
std::atomic<void*>               g_captured_ui_root{nullptr};
std::atomic<bool>                g_trace_files{false};
std::atomic<uint64_t>            g_cached_app_global_addr{0};

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
        const OverlayIndex* idx = ov->get_active_index();
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
        const OverlayIndex* idx = ov->get_active_index();
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
        const OverlayIndex* idx = ov->get_active_index();
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
        const OverlayIndex* idx = ov->get_active_index();
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
        void* cur = control;
        for (int hops = 0; hops < 64; ++hops) {
            if (!is_ptr_readable(static_cast<const uint8_t*>(cur) + 0x1E0, sizeof(void*))) {
                break;
            }
            void* parent = *reinterpret_cast<void* const*>(static_cast<const uint8_t*>(cur) + 0x1E0);
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
        const std::string text = read_msvc_wstring_utf8(static_cast<const uint8_t*>(message) + 0x20);
        if (text.size() >= 5 && std::strncmp(text.c_str(), NM_CHAT_FALLBACK_PREFIX, 5) == 0) {
            const std::string sender = read_msvc_wstring_utf8(message);
            if (ov->dispatch_chat_rx(sender.c_str(), text.c_str()) != 0) {
                logger_logf("INFO", "[NET] Intercepted S->C ##NM chat channel message (len=%zu)", text.size());
                return;
            }
        }
    }
    auto orig = g_orig_chat_listener.load(std::memory_order_acquire);
    if (orig) {
        orig(panel, message);
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

std::mutex                                      g_ui_handlers_mutex;
uint64_t                                        g_ui_next_token = 1;
std::vector<UiClickHandlerRecord*>              g_ui_handlers;
std::unordered_map<void*, UiClickHandlerRecord*> g_ui_button_slots;
std::mutex                                      g_ui_windows_mutex;
void*                                           g_ui_windows_root = nullptr;
std::unordered_map<std::string, void*>          g_ui_loaded_windows;

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

bool is_control_in_parent_children(void* parent, void* target) {
    if (!parent || !target) return false;
    auto* parent_bytes = static_cast<const uint8_t*>(parent);
    if (!is_ptr_readable(parent_bytes + 0xC0, 2 * sizeof(void*))) return false;
    auto* begin = *reinterpret_cast<void* const* const*>(parent_bytes + 0xC0);
    auto* end   = *reinterpret_cast<void* const* const*>(parent_bytes + 0xC8);
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

bool control_id_equals(const void* control, const wchar_t* target, size_t target_len) {
    if (!control || !target || target_len == 0) return false;
    const auto* ws_addr = static_cast<const uint8_t*>(control) + 0x1B8;
    if (!is_ptr_readable(ws_addr, sizeof(MsvcWString))) return false;
    const auto* ws = reinterpret_cast<const MsvcWString*>(ws_addr);
    if (ws->size != target_len || ws->size > 4096) return false;
    const wchar_t* chars = (ws->cap >= 8) ? ws->ptr : ws->buf;
    if (!chars || !is_ptr_readable(chars, target_len * sizeof(wchar_t))) return false;
    return std::wmemcmp(chars, target, target_len) == 0;
}

void* find_child_recursive_by_segment(void* parent, const wchar_t* seg, size_t seg_len, int depth) {
    if (!parent || !seg || seg_len == 0 || depth > 16) return nullptr;
    const auto* parent_bytes = static_cast<const uint8_t*>(parent);
    if (!is_ptr_readable(parent_bytes + 0xC0, 2 * sizeof(void*))) return nullptr;
    auto* begin = *reinterpret_cast<void* const* const*>(parent_bytes + 0xC0);
    auto* end   = *reinterpret_cast<void* const* const*>(parent_bytes + 0xC8);
    if (!begin || !end || end < begin) return nullptr;
    const size_t count = static_cast<size_t>(end - begin);
    if (count == 0 || count > 512 || !is_ptr_readable(begin, count * sizeof(void*))) return nullptr;

    // Pass 1: direct children first (matches native Control::Find priority).
    for (size_t i = 0; i < count; ++i) {
        void* child = begin[i];
        if (child && control_id_equals(child, seg, seg_len)) {
            return child;
        }
    }
    // Pass 2: descend through unnamed or intermediate containers.
    for (size_t i = 0; i < count; ++i) {
        void* child = begin[i];
        if (child) {
            if (void* nested = find_child_recursive_by_segment(child, seg, seg_len, depth + 1)) {
                return nested;
            }
        }
    }
    return nullptr;
}

void* find_child_fallback_dot_path(void* parent, const std::wstring& dot_path) {
    if (!parent || dot_path.empty()) return nullptr;
    void* current = parent;
    size_t pos = 0;
    while (pos < dot_path.size() && current != nullptr) {
        const size_t dot = dot_path.find(L'.', pos);
        const size_t seg_len = (dot == std::wstring::npos) ? (dot_path.size() - pos) : (dot - pos);
        if (seg_len > 0) {
            current = find_child_recursive_by_segment(current, dot_path.c_str() + pos, seg_len, 0);
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

    void* app = *reinterpret_cast<void* const*>(static_cast<uintptr_t>(app_global_addr));
    if (!app || !is_ptr_readable(static_cast<const uint8_t*>(app) + 0x348, sizeof(void*))) return false;

    void* instance = *reinterpret_cast<void* const*>(static_cast<const uint8_t*>(app) + 0x348);
    if (!instance || !is_ptr_readable(static_cast<const uint8_t*>(instance) + 0xC288, sizeof(void*))) return false;

    void* sock = *reinterpret_cast<void* const*>(static_cast<const uint8_t*>(instance) + 0xC288);
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
    } ctx{sock, data, len, image_base + write_sym->rva, image_base + flush_sym->rva};

    auto tx_fn = [](void* raw) -> int64_t {
        auto* c = static_cast<TxCtx*>(raw);
        using SocketWriteFn = void*(NM_CALL*)(void*, const void*, uint64_t);
        using SocketFlushFn = void(NM_CALL*)(void*, uint32_t, uint8_t);
        auto sock_write = reinterpret_cast<SocketWriteFn>(static_cast<uintptr_t>(c->write_addr));
        auto sock_flush = reinterpret_cast<SocketFlushFn>(static_cast<uintptr_t>(c->flush_addr));

        auto* sbytes = static_cast<uint8_t*>(c->sock);
        const bool threaded = (sbytes[0x242] != 0);
        auto* srw = reinterpret_cast<PSRWLOCK>(sbytes + 0x248);

        sbytes[0x240] = 1;
        if (threaded) AcquireSRWLockExclusive(srw);
        sock_write(c->sock, c->data, static_cast<uint64_t>(c->len));
        if (threaded) ReleaseSRWLockExclusive(srw);
        sbytes[0x240] = 0;

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
        mods_dir = dll_dir_utf8 + "\\Mods\\Client";
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

#if defined(_WIN32)
    if (build_id == NM_BUILD_SYNTHETIC || build_id == NM_BUILD_UNKNOWN || image_base == 0) {
        return;
    }

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

    // 3. Mod channel hooks (Chat S->C 0x17 + Ruleset S->C 0x2c9)
    const bool ok_chat = arm_hook("network.chat_panel_listener", reinterpret_cast<void*>(&detour_chat_panel_listener), g_orig_chat_listener);
    const bool ok_rs   = arm_hook("rulesets.s2c_0x2c9_filter",   reinterpret_cast<void*>(&detour_ruleset_s2c_filter),  g_orig_ruleset_s2c);
    logger_logf("INFO", "[NET] Armed live channel hooks (chat_rx=%s ruleset_0x2c9=%s)",
                ok_chat ? "OK" : "SKIP",
                ok_rs   ? "OK" : "SKIP");
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
    }
    installed_handles_.clear();
    build_id_ = NM_BUILD_UNKNOWN;
    image_base_ = 0;

#if defined(_WIN32)
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
            }
        }
        g_ui_button_slots.clear();
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

nm_status_t LiveEngineHooks::ui_create_window(const char* virtual_path_utf8, void** out_control) {
    if (!virtual_path_utf8 || virtual_path_utf8[0] == '\0' || !out_control) return NM_ERR_INVALID_ARG;
    *out_control = nullptr;
#if defined(_WIN32)
    void* root = g_captured_ui_root.load(std::memory_order_acquire);
    if (!root || build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) {
        return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    }

    // Reuse an already-loaded native window for the same (root, path) so re-opening
    // never triggers PoE's fatal duplicate-sibling-id exception (matches spikes/garden-demo.js).
    const std::string normalized_key = OverlayAndChannels::normalize_virtual_path(virtual_path_utf8);
    {
        std::lock_guard<std::mutex> lock(g_ui_windows_mutex);
        if (g_ui_windows_root != root) {
            g_ui_windows_root = root;
            g_ui_loaded_windows.clear();
            std::lock_guard<std::mutex> hlock(g_ui_handlers_mutex);
            for (UiClickHandlerRecord* rec : g_ui_handlers) {
                if (rec) {
                    rec->cb = nullptr;
                    rec->user_data = nullptr;
                }
            }
            g_ui_button_slots.clear();
        } else {
            auto it = g_ui_loaded_windows.find(normalized_key);
            if (it != g_ui_loaded_windows.end() && it->second != nullptr) {
                if (is_control_in_parent_children(root, it->second) && is_ptr_readable(it->second, 0x200)) {
                    *out_control = it->second;
                    (void)ui_set_visible(it->second, 1);
                    logger_logf("INFO", "[UI] Reused existing native window '%s' (root=%p, control=%p)",
                                virtual_path_utf8, root, it->second);
                    return NM_OK;
                }
                logger_logf("INFO", "[UI] Cached native window '%s' (%p) no longer attached to root %p; reloading fresh",
                            virtual_path_utf8, it->second, root);
                g_ui_loaded_windows.erase(it);
            }
        }
    }

    const EmbeddedSymbol* sym_load   = find_symbol(build_id_, "ui.control_load");
    const EmbeddedSymbol* sym_assign = find_symbol(build_id_, "runtime.wstring_assign");
    if (!sym_load || !sym_assign) {
        return NM_ERR_SYMBOL_NOT_FOUND;
    }

    struct CreateWinCtx {
        void*        root;
        uint64_t     load_addr;
        uint64_t     assign_addr;
        std::wstring wide_path;
        void*        created = nullptr;
    } ctx{
        root,
        image_base_ + sym_load->rva,
        image_base_ + sym_assign->rva,
        utf8_to_wide_hooks(virtual_path_utf8),
        nullptr,
    };

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<CreateWinCtx*>(raw);
        using LoadFn   = void(NM_CALL*)(void*, const void*);
        using AssignFn = void*(NM_CALL*)(void*, const wchar_t*, uint64_t);

        auto load_fn   = reinterpret_cast<LoadFn>(static_cast<uintptr_t>(c->load_addr));
        auto assign_fn = reinterpret_cast<AssignFn>(static_cast<uintptr_t>(c->assign_addr));

        // Snapshot root's child vector before Control::Load(root, &ws) so we return the
        // actual loaded top-level control (matches spikes/garden-demo.js:10).
        auto* root_bytes = static_cast<uint8_t*>(c->root);
        auto* old_begin = *reinterpret_cast<uint8_t**>(root_bytes + 0xC0);
        auto* old_end   = *reinterpret_cast<uint8_t**>(root_bytes + 0xC8);
        const size_t old_count = (old_end >= old_begin) ? static_cast<size_t>(old_end - old_begin) / sizeof(void*) : 0u;

        MsvcWString ws{};
        std::memset(&ws, 0, sizeof(ws));
        ws.cap = 7;
        assign_fn(&ws, c->wide_path.c_str(), static_cast<uint64_t>(c->wide_path.size()));
        load_fn(c->root, &ws);

        auto* new_begin = *reinterpret_cast<uint8_t**>(root_bytes + 0xC0);
        auto* new_end   = *reinterpret_cast<uint8_t**>(root_bytes + 0xC8);
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
        logger_logf("ERROR", "[UI] ui_create_window('%s') failed (st=%d, rc=%lld, ex=0x%08X '%s')",
                    virtual_path_utf8, static_cast<int>(st), static_cast<long long>(rc), ex.exception_code, ex.message);
        return (st != NM_OK) ? st : NM_ERR_GAME_EXCEPTION;
    }

    {
        std::lock_guard<std::mutex> lock(g_ui_windows_mutex);
        g_ui_windows_root = root;
        g_ui_loaded_windows[normalized_key] = ctx.created;
    }

    *out_control = ctx.created;
    logger_logf("INFO", "[UI] Loaded native window '%s' directly onto root (root=%p, control=%p)", virtual_path_utf8, root, ctx.created);
    return NM_OK;
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t LiveEngineHooks::ui_find_child(void* parent_control, const char* dot_path_utf8, void** out_child) {
    if (!parent_control || !dot_path_utf8 || dot_path_utf8[0] == '\0' || !out_child) return NM_ERR_INVALID_ARG;
    *out_child = nullptr;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const EmbeddedSymbol* sym_find = find_symbol(build_id_, "ui.find_child");
    if (!sym_find || sym_find->rva == 0) return NM_ERR_SYMBOL_NOT_FOUND;

    struct FindCtx {
        void*        parent;
        uint64_t     find_addr;
        std::wstring wide_path;
        void*        found = nullptr;
    } ctx{parent_control, image_base_ + sym_find->rva, utf8_to_wide_hooks(dot_path_utf8), nullptr};

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<FindCtx*>(raw);
        if (!is_ptr_readable(static_cast<const uint8_t*>(c->parent) + 0xB8, 0x18)) return -1;
        using FindFn = void*(NM_CALL*)(void*, const WStringView*);
        auto find_fn = reinterpret_cast<FindFn>(static_cast<uintptr_t>(c->find_addr));
        WStringView wv{c->wide_path.c_str(), static_cast<uint64_t>(c->wide_path.size())};
        c->found = find_fn(static_cast<uint8_t*>(c->parent) + 0xB8, &wv);
        if (!c->found) {
            c->found = find_child_fallback_dot_path(c->parent, c->wide_path);
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
        uint64_t     btn_vt_addr;
        uint64_t     lbl_vt_addr;
        uint64_t     assign_addr;
        std::wstring wide_text;
    } ctx{
        control,
        is_button ? 0x3F8u : 0x298u,
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
        bool target_is_button = (offset == 0x3F8u);
        if (c->btn_vt_addr != 0 && primary_vt == c->btn_vt_addr) {
            offset = 0x3F8u;
            target_is_button = true;
        } else if (c->lbl_vt_addr != 0 && primary_vt == c->lbl_vt_addr) {
            offset = 0x298u;
            target_is_button = false;
        }

        if (target_is_button) {
            if (!is_ptr_readable(static_cast<const uint8_t*>(c->control) + 0x408, sizeof(void*))) return -2;
            void* inner_label = *reinterpret_cast<void* const*>(static_cast<const uint8_t*>(c->control) + 0x408);
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
            existing->token = g_ui_next_token++;
            existing->cb = cb;
            existing->user_data = user_data;
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
    } ctx{button_control, rec, image_base_ + sym_get->rva, image_base_ + sym_grow->rva};

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<SubCtx*>(raw);
        if (!is_ptr_readable(static_cast<const uint8_t*>(c->button) + 0x2A0, sizeof(void*))) return -2;
        using EventGetFn  = void*(NM_CALL*)(void*, void*, uint8_t);
        using EventGrowFn = void(NM_CALL*)(void*);
        using StepFn      = uint64_t(NM_CALL*)(void*);

        auto event_get  = reinterpret_cast<EventGetFn>(static_cast<uintptr_t>(c->get_addr));
        auto event_grow = reinterpret_cast<EventGrowFn>(static_cast<uintptr_t>(c->grow_addr));

        void* shared[2] = {nullptr, nullptr};
        event_get(static_cast<uint8_t*>(c->button) + 0x2A0, shared, 1);
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
    }

    *out_token = rec->token;
    return NM_OK;
#else
    (void)user_data;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

} // namespace nativemods
