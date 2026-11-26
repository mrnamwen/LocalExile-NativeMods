#include "phase1_services.hpp"

#include "address_table.hpp"
#include "exception_wall.hpp"
#include "hook_engine.hpp"
#include "shim_logger.hpp"

#include <algorithm>
#include <atomic>
#include <cstring>
#include <cwchar>
#include <mutex>
#include <string>

#if defined(_WIN32)
#  define WIN32_LEAN_AND_MEAN
#  include <windows.h>
#  ifdef exception_code
#    undef exception_code
#  endif
#endif

namespace nativemods {

namespace {

// Callback slots (protected by mutex for atomic (fn, ud) pairs, snapshotted before invocation).
std::mutex                     g_p1_cb_mutex;
nm_screen_event_callback_t     g_screen_cb = nullptr;
void*                          g_screen_ud = nullptr;
nm_input_callback_t            g_input_cb = nullptr;
void*                          g_input_ud = nullptr;
nm_pregame_creating_callback_t g_pregame_creating_cb = nullptr;
nm_pregame_created_callback_t  g_pregame_created_cb = nullptr;
void*                          g_pregame_ud = nullptr;

// Fast atomic presence flags so hot hooks (StateStack::OnWindowMessage) exit in 1 instruction when idle.
std::atomic<bool>              g_has_screen_cb{false};
std::atomic<bool>              g_has_input_cb{false};
std::atomic<bool>              g_has_pregame_creating_cb{false};
std::atomic<bool>              g_has_pregame_created_cb{false};

std::atomic<nm_build_id_t>     g_p1_build_id{NM_BUILD_UNKNOWN};
std::atomic<uint64_t>          g_p1_image_base{0};

// Last character creation attempt cached for S->C 0x13 fallback if CreateCharacterState controls cleared.
std::mutex                     g_last_create_mutex;
std::string                    g_last_create_name;
std::string                    g_last_create_league;

void fire_screen_event_guarded(int32_t event_kind, void* primary_handle, uint64_t param0) {
    if (!g_has_screen_cb.load(std::memory_order_acquire)) return;
    nm_screen_event_callback_t cb = nullptr;
    void* ud = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_p1_cb_mutex);
        cb = g_screen_cb;
        ud = g_screen_ud;
    }
    if (!cb) return;

    struct Ctx {
        nm_screen_event_callback_t cb;
        int32_t                    kind;
        void*                      handle;
        uint64_t                   param0;
        void*                      ud;
    } ctx{cb, event_kind, primary_handle, param0, ud};

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        c->cb(c->kind, c->handle, c->param0, c->ud);
        return 0;
    };
    nm_exception_info_t ex{};
    (void)invoke_guarded(fn, &ctx, nullptr, &ex);
}

int32_t fire_input_event_guarded(const nm_input_event_t* ev) {
    if (!ev || !g_has_input_cb.load(std::memory_order_acquire)) return 0;
    nm_input_callback_t cb = nullptr;
    void* ud = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_p1_cb_mutex);
        cb = g_input_cb;
        ud = g_input_ud;
    }
    if (!cb) return 0;

    struct Ctx {
        nm_input_callback_t     cb;
        const nm_input_event_t* ev;
        void*                   ud;
    } ctx{cb, ev, ud};

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        return static_cast<int64_t>(c->cb(c->ev, c->ud));
    };
    int64_t res = 0;
    nm_exception_info_t ex{};
    if (invoke_guarded(fn, &ctx, &res, &ex) != NM_OK) {
        return 0;
    }
    return (res != 0) ? 1 : 0;
}

int32_t fire_pregame_creating_guarded(
    const char* name_utf8,
    const char* league_utf8,
    const char* start_state_utf8,
    char*       out_start_state_utf8,
    size_t      out_capacity) {
    if (!out_start_state_utf8 || out_capacity == 0) return 0;
    out_start_state_utf8[0] = '\0';

    {
        std::lock_guard<std::mutex> lock(g_last_create_mutex);
        g_last_create_name = name_utf8 ? name_utf8 : "";
        g_last_create_league = league_utf8 ? league_utf8 : "";
    }

    if (!g_has_pregame_creating_cb.load(std::memory_order_acquire)) return 0;
    nm_pregame_creating_callback_t cb = nullptr;
    void* ud = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_p1_cb_mutex);
        cb = g_pregame_creating_cb;
        ud = g_pregame_ud;
    }
    if (!cb) return 0;

    struct Ctx {
        nm_pregame_creating_callback_t cb;
        const char*                    name;
        const char*                    league;
        const char*                    start_state;
        char*                          out_buf;
        size_t                         out_cap;
        void*                          ud;
    } ctx{
        cb,
        name_utf8 ? name_utf8 : "",
        league_utf8 ? league_utf8 : "",
        start_state_utf8 ? start_state_utf8 : "",
        out_start_state_utf8,
        out_capacity,
        ud,
    };

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        return static_cast<int64_t>(c->cb(c->name, c->league, c->start_state, c->out_buf, c->out_cap, c->ud));
    };
    int64_t res = 0;
    nm_exception_info_t ex{};
    if (invoke_guarded(fn, &ctx, &res, &ex) != NM_OK) {
        out_start_state_utf8[0] = '\0';
        return 0;
    }
    out_start_state_utf8[out_capacity - 1] = '\0';
    return (res != 0 && out_start_state_utf8[0] != '\0') ? 1 : 0;
}

void fire_pregame_created_guarded(const char* name_utf8, const char* league_utf8, uint16_t result_code) {
    if (!g_has_pregame_created_cb.load(std::memory_order_acquire)) return;
    nm_pregame_created_callback_t cb = nullptr;
    void* ud = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_p1_cb_mutex);
        cb = g_pregame_created_cb;
        ud = g_pregame_ud;
    }
    if (!cb) return;

    struct Ctx {
        nm_pregame_created_callback_t cb;
        const char*                   name;
        const char*                   league;
        uint16_t                      result_code;
        void*                         ud;
    } ctx{
        cb,
        name_utf8 ? name_utf8 : "",
        league_utf8 ? league_utf8 : "",
        result_code,
        ud,
    };

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        c->cb(c->name, c->league, c->result_code, c->ud);
        return 0;
    };
    nm_exception_info_t ex{};
    (void)invoke_guarded(fn, &ctx, nullptr, &ex);
}

#if defined(_WIN32)

void copy_utf8_to_fixed(char* dst, size_t cap, const std::string& src) {
    if (!dst || cap == 0) return;
    const size_t n = std::min(src.size(), cap - 1);
    if (n > 0) {
        std::memcpy(dst, src.data(), n);
    }
    dst[n] = '\0';
}

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

struct CursorItemRef {
    uint32_t inv_mgr_kind;
    uint32_t inv_id;
    uint32_t item_id;
    uint32_t pad;
    void*    item_entity;
};
static_assert(sizeof(CursorItemRef) == 24, "CursorItemRef must be 24 bytes");

size_t off_or(nm_build_id_t bid, std::string_view name, size_t fallback) noexcept {
    if (bid != NM_BUILD_UNKNOWN && bid != NM_BUILD_SYNTHETIC) {
        if (const EmbeddedOffset* o = find_offset(bid, name)) {
            return o->offset;
        }
    }
    return fallback;
}

uint64_t sym_addr(nm_build_id_t bid, uint64_t base, std::string_view name) noexcept {
    if (bid == NM_BUILD_UNKNOWN || bid == NM_BUILD_SYNTHETIC || base == 0) return 0;
    const EmbeddedSymbol* s = find_symbol(bid, name);
    if (!s || s->rva == 0 || s->status == NM_SYM_STATUS_UNVERIFIED) return 0;
    return base + s->rva;
}

bool ptr_readable(const void* p, size_t len) {
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

template <typename T>
bool read_val(const void* base, size_t offset, T& out) {
    const auto* addr = static_cast<const uint8_t*>(base) + offset;
    if (!ptr_readable(addr, sizeof(T))) return false;
    std::memcpy(&out, addr, sizeof(T));
    return true;
}

std::string wide_to_utf8_p1(const wchar_t* ptr, size_t len) {
    if (!ptr || len == 0 || len > 8192) return {};
    const int needed = WideCharToMultiByte(CP_UTF8, 0, ptr, static_cast<int>(len), nullptr, 0, nullptr, nullptr);
    if (needed <= 0) return {};
    std::string out(static_cast<size_t>(needed), '\0');
    WideCharToMultiByte(CP_UTF8, 0, ptr, static_cast<int>(len), out.data(), needed, nullptr, nullptr);
    return out;
}

std::wstring utf8_to_wide_p1(const char* s) {
    if (!s || s[0] == '\0') return {};
    const int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, nullptr, 0);
    if (n <= 1) return {};
    std::wstring w(static_cast<size_t>(n - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s, -1, w.data(), n);
    return w;
}

std::string read_msvc_ws_utf8(const void* addr) {
    if (!ptr_readable(addr, sizeof(MsvcWString))) return {};
    const auto* ws = static_cast<const MsvcWString*>(addr);
    if (ws->size == 0 || ws->size > 4096) return {};
    const wchar_t* chars = (ws->cap >= 8) ? ws->ptr : ws->buf;
    if (!chars || !ptr_readable(chars, static_cast<size_t>(ws->size) * sizeof(wchar_t))) return {};
    return wide_to_utf8_p1(chars, static_cast<size_t>(ws->size));
}

std::string read_wsv_utf8(const WStringView* wsv) {
    if (!wsv || !ptr_readable(wsv, sizeof(WStringView))) return {};
    if (!wsv->ptr || wsv->len == 0 || wsv->len > 4096) return {};
    if (!ptr_readable(wsv->ptr, static_cast<size_t>(wsv->len) * sizeof(wchar_t))) return {};
    return wide_to_utf8_p1(wsv->ptr, static_cast<size_t>(wsv->len));
}

std::string read_c_wstr_utf8(const wchar_t* ptr) {
    if (!ptr || !ptr_readable(ptr, sizeof(wchar_t))) return {};
    size_t len = 0;
    while (len < 512 && ptr_readable(ptr + len, sizeof(wchar_t)) && ptr[len] != L'\0') {
        ++len;
    }
    return wide_to_utf8_p1(ptr, len);
}

void* resolve_state_stack_unguarded(nm_build_id_t bid, uint64_t base) {
    const uint64_t g_addr = sym_addr(bid, base, "frame.state_stack_global");
    if (g_addr == 0) return nullptr;
    void* ss = nullptr;
    if (!read_val(reinterpret_cast<const void*>(static_cast<uintptr_t>(g_addr)), 0, ss)) {
        return nullptr;
    }
    return ss;
}

bool control_is_visible_unguarded(nm_build_id_t bid, uint64_t base, void* ctrl) {
    if (!ctrl || !ptr_readable(ctrl, 0x1F0)) return false;
    const uint64_t fn_addr = sym_addr(bid, base, "ui.control_is_visible");
    if (fn_addr != 0) {
        using IsVisFn = uint8_t(NM_CALL*)(void*);
        return reinterpret_cast<IsVisFn>(static_cast<uintptr_t>(fn_addr))(ctrl) != 0;
    }
    uint32_t flags = 0;
    if (!read_val(ctrl, off_or(bid, "ui.control.flags", 0x1E8u), flags)) return false;
    return (flags & 0x800u) != 0u;
}

bool control_flag_visible_unguarded(nm_build_id_t bid, void* ctrl) {
    if (!ctrl) return false;
    uint32_t flags = 0;
    if (!read_val(ctrl, off_or(bid, "ui.control.flags", 0x1E8u), flags)) return false;
    return (flags & 0x800u) != 0u;
}

struct ActiveStateInfo {
    nm_pregame_screen_t screen = NM_SCREEN_UNKNOWN;
    void*               state_stack = nullptr;
    void*               active_state = nullptr;
    void*               ingame_state = nullptr;
    void*               pregame_state = nullptr;
    void*               create_char_state = nullptr;
    void*               ui_manager = nullptr;
    void*               ui_root = nullptr;
};

ActiveStateInfo query_active_state_unguarded(nm_build_id_t bid, uint64_t base) {
    ActiveStateInfo out{};
    void* ss = resolve_state_stack_unguarded(bid, base);
    if (!ss) return out;
    out.state_stack = ss;

    const uint64_t vt_ingame      = sym_addr(bid, base, "frame.vtable_ingame_state");
    const uint64_t vt_pregame     = sym_addr(bid, base, "frame.vtable_pregame_state");
    const uint64_t vt_login       = sym_addr(bid, base, "frame.vtable_login_state");
    const uint64_t vt_select_char = sym_addr(bid, base, "frame.vtable_select_char_state");
    const uint64_t vt_create_char = sym_addr(bid, base, "frame.vtable_create_char_state");
    const uint64_t vt_modal       = sym_addr(bid, base, "frame.vtable_modal_state");
    const uint64_t vt_waiting     = sym_addr(bid, base, "frame.vtable_waiting_state");
    const uint64_t vt_escape      = sym_addr(bid, base, "frame.vtable_escape_state");
    const uint64_t vt_delete_char = sym_addr(bid, base, "frame.vtable_delete_char_state");

    const size_t active_begin_off = off_or(bid, "frame.state_stack.active_begin", 0x08u);
    const size_t active_end_off   = off_or(bid, "frame.state_stack.active_end", 0x10u);
    const size_t state_id_off     = off_or(bid, "frame.state.state_id", 0x209u);
    const size_t igs_uimgr_off    = off_or(bid, "frame.ingame_state.ui_manager", 0x3A8u);
    const size_t pgs_uimgr_off    = off_or(bid, "frame.pregame_state.ui_manager", 0x240u);
    const size_t uimgr_root_off   = off_or(bid, "ui.manager.root", 0x1F8u);
    const size_t ccs_overlay_off  = off_or(bid, "frame.create_char_state.league_overlay", 0x2B8u);
    const size_t ss_pgs_off       = off_or(bid, "frame.state_stack.pregame_state", 0xB8u);
    const size_t pregame_root_off = off_or(bid, "frame.state.pregame_ui_root", 0x218u);
    const size_t ctrl_uimgr_off   = off_or(bid, "ui.control.ui_manager", 0x148u);

    uint8_t* vec_begin = nullptr;
    uint8_t* vec_end = nullptr;
    if (!read_val(ss, active_begin_off, vec_begin) || !read_val(ss, active_end_off, vec_end) ||
        !vec_begin || !vec_end || vec_end < vec_begin) {
        return out;
    }

    const size_t byte_len = static_cast<size_t>(vec_end - vec_begin);
    const size_t count = byte_len / 0x10u;
    if (count == 0 || count > 64 || !ptr_readable(vec_begin, count * 0x10u)) {
        return out;
    }

    // Walk authoritative active stack top-down.
    for (size_t idx = count; idx > 0; --idx) {
        void* st = nullptr;
        if (!read_val(vec_begin + (idx - 1) * 0x10u, 0, st) || !st) continue;
        uint64_t vt = 0;
        if (!read_val(st, 0, vt)) continue;
        uint8_t sid = 0xFFu;
        (void)read_val(st, state_id_off, sid);

        // Skip transient overlays when determining underlying screen.
        if ((vt_modal && vt == vt_modal) || sid == 11u ||
            (vt_waiting && vt == vt_waiting) || sid == 2u ||
            (vt_escape && vt == vt_escape) || sid == 3u ||
            (vt_delete_char && vt == vt_delete_char) || sid == 10u) {
            continue;
        }

        if ((vt_ingame && vt == vt_ingame) || sid == 4u) {
            out.screen = NM_SCREEN_IN_GAME;
            out.active_state = st;
            out.ingame_state = st;
            out.ui_manager = static_cast<uint8_t*>(st) + igs_uimgr_off;
            (void)read_val(out.ui_manager, uimgr_root_off, out.ui_root);
            break;
        }
        if ((vt_create_char && vt == vt_create_char) || sid == 8u) {
            out.active_state = st;
            out.create_char_state = st;
            void* overlay = nullptr;
            (void)read_val(st, ccs_overlay_off, overlay);
            out.screen = (overlay && control_flag_visible_unguarded(bid, overlay))
                             ? NM_SCREEN_LEAGUE_OVERLAY
                             : NM_SCREEN_CREATE_CHARACTER;
            void* pgs = nullptr;
            if (read_val(ss, ss_pgs_off, pgs) && pgs) {
                out.pregame_state = pgs;
                out.ui_manager = static_cast<uint8_t*>(pgs) + pgs_uimgr_off;
                (void)read_val(out.ui_manager, uimgr_root_off, out.ui_root);
            }
            if (!out.ui_root) {
                (void)read_val(st, pregame_root_off, out.ui_root);
                if (out.ui_root && !out.ui_manager) {
                    (void)read_val(out.ui_root, ctrl_uimgr_off, out.ui_manager);
                }
            }
            break;
        }
        if ((vt_select_char && vt == vt_select_char) || sid == 9u) {
            out.screen = NM_SCREEN_SELECT_CHARACTER;
            out.active_state = st;
            void* pgs = nullptr;
            if (read_val(ss, ss_pgs_off, pgs) && pgs) {
                out.pregame_state = pgs;
                out.ui_manager = static_cast<uint8_t*>(pgs) + pgs_uimgr_off;
                (void)read_val(out.ui_manager, uimgr_root_off, out.ui_root);
            }
            if (!out.ui_root) {
                (void)read_val(st, pregame_root_off, out.ui_root);
                if (out.ui_root && !out.ui_manager) {
                    (void)read_val(out.ui_root, ctrl_uimgr_off, out.ui_manager);
                }
            }
            break;
        }
        if ((vt_login && vt == vt_login) || sid == 6u) {
            out.screen = NM_SCREEN_LOGIN;
            out.active_state = st;
            void* pgs = nullptr;
            if (read_val(ss, ss_pgs_off, pgs) && pgs) {
                out.pregame_state = pgs;
                out.ui_manager = static_cast<uint8_t*>(pgs) + pgs_uimgr_off;
                (void)read_val(out.ui_manager, uimgr_root_off, out.ui_root);
            }
            if (!out.ui_root) {
                (void)read_val(st, pregame_root_off, out.ui_root);
                if (out.ui_root && !out.ui_manager) {
                    (void)read_val(out.ui_root, ctrl_uimgr_off, out.ui_manager);
                }
            }
            break;
        }
        if ((vt_pregame && vt == vt_pregame) || sid == 7u) {
            out.pregame_state = st;
            out.ui_manager = static_cast<uint8_t*>(st) + pgs_uimgr_off;
            (void)read_val(out.ui_manager, uimgr_root_off, out.ui_root);
        }
    }

    return out;
}

bool is_text_input_focused_unguarded(nm_build_id_t bid, uint64_t base) {
    ActiveStateInfo st = query_active_state_unguarded(bid, base);
    if (!st.ui_manager) return false;
    void* focused = nullptr;
    if (!read_val(st.ui_manager, off_or(bid, "ui.manager.focused", 0x208u), focused) || !focused) {
        return false;
    }
    void** vtbl = nullptr;
    if (!read_val(focused, 0, vtbl) || !vtbl || !ptr_readable(vtbl, 7 * sizeof(void*)) || !vtbl[6]) {
        return false;
    }
    // Control::AsTextInput at vtable slot 6 (+0x30): returns non-null this+0x6b8 on TextBox & subclasses.
    using AsTextInputFn = void*(NM_CALL*)(void*);
    return reinterpret_cast<AsTextInputFn>(vtbl[6])(focused) != nullptr;
}

// RB-tree lookup in Inventory+0x188 (std::map<uint32_t, InventoryItemSlot*>)
bool lookup_item_grid_slot_unguarded(nm_build_id_t bid, void* inventory, uint32_t item_id, int32_t& out_x, int32_t& out_y) {
    out_x = -1;
    out_y = -1;
    if (!inventory) return false;
    const size_t map_off = off_or(bid, "items.inventory.slot_map", 0x188u);
    void* head = nullptr;
    if (!read_val(inventory, map_off, head) || !head) return false;

    void* node = nullptr;
    if (!read_val(head, 0x08u, node) || !node) return false; // head->parent == root

    for (int step = 0; step < 64 && node != nullptr && node != head; ++step) {
        uint8_t is_nil = 1;
        if (!read_val(node, 0x19u, is_nil) || is_nil != 0) break;

        uint32_t key = 0;
        if (!read_val(node, 0x20u, key)) break;
        if (item_id == key) {
            void* slot = nullptr;
            if (read_val(node, 0x28u, slot) && slot) {
                int32_t gx = -1;
                int32_t gy = -1;
                if (read_val(slot, 0x08u, gx) && read_val(slot, 0x0Cu, gy)) {
                    out_x = gx;
                    out_y = gy;
                    return true;
                }
            }
            return false;
        }
        void* next = nullptr;
        if (!read_val(node, (item_id < key) ? 0x00u : 0x10u, next)) break;
        node = next;
    }
    return false;
}

void populate_item_snapshot_unguarded(
    nm_build_id_t       bid,
    uint64_t            base,
    void*               item_entity,
    nm_item_snapshot_t& out) {
    if (!item_entity || !ptr_readable(item_entity, 0x10)) return;
    out.present = 1;
    out.item_entity = item_entity;
    out.stack_size = 1;
    out.max_stack_size = 1;

    using GetCompFn = void*(NM_CALL*)(void*);
    const uint64_t fn_base  = sym_addr(bid, base, "items.get_base_component");
    const uint64_t fn_stack = sym_addr(bid, base, "items.get_stack_component");
    const uint64_t fn_mods  = sym_addr(bid, base, "items.get_mods_component");

    if (fn_base != 0) {
        void* base_comp = reinterpret_cast<GetCompFn>(static_cast<uintptr_t>(fn_base))(item_entity);
        if (base_comp && ptr_readable(base_comp, 0xD0)) {
            uint8_t flags = 0;
            if (read_val(base_comp, off_or(bid, "items.base_comp.flags", 0xC7u), flags)) {
                out.corrupted = (flags & 0x01u) ? 1 : 0;
                out.account_bound = (flags & 0x08u) ? 1 : 0;
            }
            void* bs = nullptr;
            if (read_val(base_comp, off_or(bid, "items.base_comp.static_data", 0x10u), bs) && bs && ptr_readable(bs, 0x98)) {
                (void)read_val(bs, off_or(bid, "items.base_static.width", 0x10u), out.grid_w);
                (void)read_val(bs, off_or(bid, "items.base_static.height", 0x11u), out.grid_h);
                (void)read_val(bs, off_or(bid, "items.base_static.base_hash32", 0x8Cu), out.base_hash32);

                const std::string bname = read_msvc_ws_utf8(
                    static_cast<const uint8_t*>(bs) + off_or(bid, "items.base_static.base_name", 0x48u));
                copy_utf8_to_fixed(out.base_name, sizeof(out.base_name), bname);

                const size_t row_ref_off = off_or(bid, "items.base_static.row_ref", 0x68u);
                const wchar_t* meta_w = nullptr;
                const void* item_classes_ref = nullptr;
                if (Phase1Services::extract_base_item_type_row(bs, row_ref_off, &meta_w, &item_classes_ref)) {
                    if (meta_w) {
                        const std::string meta = read_c_wstr_utf8(meta_w);
                        copy_utf8_to_fixed(out.metadata_path, sizeof(out.metadata_path), meta);
                    }
                    const uint64_t fn_cls = sym_addr(bid, base, "items.base_get_item_class");
                    if (fn_cls != 0 && item_classes_ref != nullptr) {
                        using GetClsFn = uint32_t(NM_CALL*)(const void*);
                        out.item_class_id = reinterpret_cast<GetClsFn>(static_cast<uintptr_t>(fn_cls))(item_classes_ref);
                    }
                }
            }
        }
    }

    if (fn_stack != 0) {
        void* stack_comp = reinterpret_cast<GetCompFn>(static_cast<uintptr_t>(fn_stack))(item_entity);
        if (stack_comp && ptr_readable(stack_comp, 0x20)) {
            uint32_t sz = 1;
            if (read_val(stack_comp, off_or(bid, "items.stack_comp.stack_size", 0x18u), sz)) {
                out.stack_size = sz;
            }
            const uint64_t fn_max = sym_addr(bid, base, "items.stack_get_max_size");
            if (fn_max != 0) {
                using GetMaxFn = uint32_t(NM_CALL*)(void*, uint32_t);
                out.max_stack_size = reinterpret_cast<GetMaxFn>(static_cast<uintptr_t>(fn_max))(stack_comp, 0x47u);
            }
        }
    }

    if (fn_mods != 0) {
        void* mods_comp = reinterpret_cast<GetCompFn>(static_cast<uintptr_t>(fn_mods))(item_entity);
        if (mods_comp && ptr_readable(mods_comp, 0x2F0)) {
            uint8_t deser = 1;
            if (read_val(mods_comp, off_or(bid, "items.mods_comp.deserialised", 0x2E2u), deser) && deser == 0) {
                const uint64_t fn_ensure = sym_addr(bid, base, "items.mods_ensure_deserialised");
                if (fn_ensure != 0) {
                    using EnsureFn = void(NM_CALL*)(void*);
                    reinterpret_cast<EnsureFn>(static_cast<uintptr_t>(fn_ensure))(mods_comp);
                }
            }
            uint8_t ident = 0;
            if (read_val(mods_comp, off_or(bid, "items.mods_comp.identified", 0xB0u), ident)) {
                out.identified = ident ? 1 : 0;
            }
            (void)read_val(mods_comp, off_or(bid, "items.mods_comp.rarity", 0xB4u), out.rarity);
            (void)read_val(mods_comp, off_or(bid, "items.mods_comp.item_level", 0x2B8u), out.item_level);
        }
    }
}

// --- Live HookEngine Chain Callbacks ---

void NM_CALL hook_state_stack_on_wndmsg_pre(nm_hook_context_t* ctx, void* /*ud*/) {
    if (!ctx || !g_has_input_cb.load(std::memory_order_acquire)) return;

    const uint32_t msg = static_cast<uint32_t>(ctx->args[2] & 0xFFFFFFFFull);
    const uint64_t wparam = ctx->args[3];
    const uint64_t lparam = ctx->args[4];

    nm_input_event_t ev{};
    ev.struct_size = sizeof(ev);
    ev.win32_msg = msg;

    bool is_mouse_msg = false;
    switch (msg) {
        case WM_KEYDOWN:
        case WM_SYSKEYDOWN:
            ev.kind = NM_INPUT_KEY_DOWN;
            ev.virtual_key = static_cast<uint32_t>(wparam & 0xFFFFu);
            ev.is_repeat = ((lparam & (1ull << 30)) != 0ull) ? 1 : 0;
            break;
        case WM_KEYUP:
        case WM_SYSKEYUP:
            ev.kind = NM_INPUT_KEY_UP;
            ev.virtual_key = static_cast<uint32_t>(wparam & 0xFFFFu);
            break;
        case WM_LBUTTONDOWN:
            ev.kind = NM_INPUT_MOUSE_DOWN;
            ev.mouse_button = NM_MOUSE_LEFT;
            is_mouse_msg = true;
            break;
        case WM_LBUTTONUP:
            ev.kind = NM_INPUT_MOUSE_UP;
            ev.mouse_button = NM_MOUSE_LEFT;
            is_mouse_msg = true;
            break;
        case WM_RBUTTONDOWN:
            ev.kind = NM_INPUT_MOUSE_DOWN;
            ev.mouse_button = NM_MOUSE_RIGHT;
            is_mouse_msg = true;
            break;
        case WM_RBUTTONUP:
            ev.kind = NM_INPUT_MOUSE_UP;
            ev.mouse_button = NM_MOUSE_RIGHT;
            is_mouse_msg = true;
            break;
        case WM_MBUTTONDOWN:
            ev.kind = NM_INPUT_MOUSE_DOWN;
            ev.mouse_button = NM_MOUSE_MIDDLE;
            is_mouse_msg = true;
            break;
        case WM_MBUTTONUP:
            ev.kind = NM_INPUT_MOUSE_UP;
            ev.mouse_button = NM_MOUSE_MIDDLE;
            is_mouse_msg = true;
            break;
        case WM_XBUTTONDOWN:
            ev.kind = NM_INPUT_MOUSE_DOWN;
            ev.mouse_button = (((wparam >> 16) & 0xFFFFu) == 1u) ? NM_MOUSE_X1 : NM_MOUSE_X2;
            is_mouse_msg = true;
            break;
        case WM_XBUTTONUP:
            ev.kind = NM_INPUT_MOUSE_UP;
            ev.mouse_button = (((wparam >> 16) & 0xFFFFu) == 1u) ? NM_MOUSE_X1 : NM_MOUSE_X2;
            is_mouse_msg = true;
            break;
        case WM_MOUSEWHEEL:
            ev.kind = NM_INPUT_MOUSE_WHEEL;
            ev.wheel_delta = static_cast<int32_t>(static_cast<int16_t>((wparam >> 16) & 0xFFFFu));
            is_mouse_msg = true;
            break;
        case WM_MOUSEMOVE:
            ev.kind = NM_INPUT_MOUSE_MOVE;
            is_mouse_msg = true;
            break;
        default:
            return;
    }

    uint32_t mods = NM_MOD_NONE;
    if ((GetKeyState(VK_SHIFT) & 0x8000) != 0)   mods |= NM_MOD_SHIFT;
    if ((GetKeyState(VK_CONTROL) & 0x8000) != 0) mods |= NM_MOD_CTRL;
    if ((GetKeyState(VK_MENU) & 0x8000) != 0)    mods |= NM_MOD_ALT;
    ev.modifiers = mods;

    if (is_mouse_msg) {
        ev.mouse_x = static_cast<int32_t>(static_cast<int16_t>(lparam & 0xFFFFu));
        ev.mouse_y = static_cast<int32_t>(static_cast<int16_t>((lparam >> 16) & 0xFFFFu));
    }

    const nm_build_id_t bid = g_p1_build_id.load(std::memory_order_acquire);
    const uint64_t base = g_p1_image_base.load(std::memory_order_acquire);
    struct FocusCtx {
        nm_build_id_t bid;
        uint64_t      base;
        bool          need_xy;
        int32_t       mx;
        int32_t       my;
        uint8_t       focused;
    } fctx{bid, base, !is_mouse_msg, ev.mouse_x, ev.mouse_y, 0};

    auto focus_fn = [](void* raw) -> int64_t {
        auto* c = static_cast<FocusCtx*>(raw);
        ActiveStateInfo st = query_active_state_unguarded(c->bid, c->base);
        if (st.ui_manager) {
            if (c->need_xy) {
                (void)read_val(st.ui_manager, off_or(c->bid, "ui.manager.mouse_x", 0x298u), c->mx);
                (void)read_val(st.ui_manager, off_or(c->bid, "ui.manager.mouse_y", 0x29Cu), c->my);
            }
            void* foc = nullptr;
            if (read_val(st.ui_manager, off_or(c->bid, "ui.manager.focused", 0x208u), foc) && foc) {
                void** vtbl = nullptr;
                if (read_val(foc, 0, vtbl) && vtbl && ptr_readable(vtbl, 7 * sizeof(void*)) && vtbl[6]) {
                    using AsTextInputFn = void*(NM_CALL*)(void*);
                    c->focused = (reinterpret_cast<AsTextInputFn>(vtbl[6])(foc) != nullptr) ? 1 : 0;
                }
            }
        }
        return 0;
    };
    nm_exception_info_t ex{};
    if (invoke_guarded(focus_fn, &fctx, nullptr, &ex) == NM_OK) {
        ev.mouse_x = fctx.mx;
        ev.mouse_y = fctx.my;
        ev.is_text_input_focused = fctx.focused;
    }

    if (fire_input_event_guarded(&ev) != 0) {
        ctx->skip_original = 1;
        ctx->return_value = 1;
    }
}

void NM_CALL hook_ingame_ui_init_post(nm_hook_context_t* ctx, void* /*ud*/) {
    if (!ctx) return;
    fire_screen_event_guarded(NM_SCREEN_EVT_INGAME_UI_INIT, reinterpret_cast<void*>(static_cast<uintptr_t>(ctx->args[0])), 0);
}

void NM_CALL hook_ingame_state_leave_pre(nm_hook_context_t* ctx, void* /*ud*/) {
    if (!ctx) return;
    fire_screen_event_guarded(NM_SCREEN_EVT_INGAME_LEAVE, reinterpret_cast<void*>(static_cast<uintptr_t>(ctx->args[0])), 0);
}

void NM_CALL hook_league_overlay_fill_post(nm_hook_context_t* ctx, void* /*ud*/) {
    if (!ctx) return;
    fire_screen_event_guarded(
        NM_SCREEN_EVT_LEAGUE_OVERLAY_FILLED,
        reinterpret_cast<void*>(static_cast<uintptr_t>(ctx->args[0])),
        ctx->args[2]);
}

void NM_CALL hook_create_char_enter_post(nm_hook_context_t* ctx, void* /*ud*/) {
    if (!ctx) return;
    fire_screen_event_guarded(NM_SCREEN_EVT_CREATE_CHAR_ENTER, reinterpret_cast<void*>(static_cast<uintptr_t>(ctx->args[0])), 0);
}

void NM_CALL hook_create_char_leave_pre(nm_hook_context_t* ctx, void* /*ud*/) {
    if (!ctx) return;
    fire_screen_event_guarded(NM_SCREEN_EVT_CREATE_CHAR_LEAVE, reinterpret_cast<void*>(static_cast<uintptr_t>(ctx->args[0])), 0);
}

void NM_CALL hook_tab_container_select_post(nm_hook_context_t* ctx, void* /*ud*/) {
    if (!ctx) return;
    fire_screen_event_guarded(
        NM_SCREEN_EVT_STASH_TAB_CHANGED,
        reinterpret_cast<void*>(static_cast<uintptr_t>(ctx->args[0])),
        ctx->args[1]);
}

void NM_CALL hook_map_stash_subtab_post(nm_hook_context_t* ctx, void* /*ud*/) {
    if (!ctx) return;
    fire_screen_event_guarded(
        NM_SCREEN_EVT_MAP_SUBTAB_CHANGED,
        reinterpret_cast<void*>(static_cast<uintptr_t>(ctx->args[0])),
        0);
}

// Thread-local storage for zero-leak S78 WStringView override during LoginClient::WriteCreateCharacter_0x12.
thread_local std::wstring t_override_start_state_w;
thread_local WStringView  t_override_start_state_view{nullptr, 0};

void NM_CALL hook_write_create_char_0x12_pre(nm_hook_context_t* ctx, void* /*ud*/) {
    if (!ctx) return;

    struct ReadArgsCtx {
        const WStringView* name_wsv;
        const WStringView* league_wsv;
        const WStringView* state_wsv;
        std::string        name;
        std::string        league;
        std::string        start_state;
    } rctx{
        reinterpret_cast<const WStringView*>(static_cast<uintptr_t>(ctx->args[1])),
        reinterpret_cast<const WStringView*>(static_cast<uintptr_t>(ctx->args[2])),
        reinterpret_cast<const WStringView*>(static_cast<uintptr_t>(ctx->args[5])),
        {},
        {},
        {},
    };

    auto read_fn = [](void* raw) -> int64_t {
        auto* c = static_cast<ReadArgsCtx*>(raw);
        c->name = read_wsv_utf8(c->name_wsv);
        c->league = read_wsv_utf8(c->league_wsv);
        c->start_state = read_wsv_utf8(c->state_wsv);
        return 0;
    };
    nm_exception_info_t ex{};
    if (invoke_guarded(read_fn, &rctx, nullptr, &ex) != NM_OK) {
        return;
    }

    char rewritten_utf8[4096] = {};
    if (fire_pregame_creating_guarded(
            rctx.name.c_str(),
            rctx.league.c_str(),
            rctx.start_state.c_str(),
            rewritten_utf8,
            sizeof(rewritten_utf8)) != 0) {
        t_override_start_state_w = utf8_to_wide_p1(rewritten_utf8);
        if (t_override_start_state_w.size() > 4096) {
            t_override_start_state_w.resize(4096);
        }
        if (!t_override_start_state_w.empty()) {
            t_override_start_state_view.ptr = t_override_start_state_w.c_str();
            t_override_start_state_view.len = static_cast<uint64_t>(t_override_start_state_w.size());
            ctx->args[5] = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(&t_override_start_state_view));
            logger_logf("INFO", "[PREGAME] Rewrote C->S 0x12 start_state ('%s' -> '%s')",
                        rctx.start_state.c_str(), rewritten_utf8);
        }
    }
}

void NM_CALL hook_create_char_result_0x13_pre(nm_hook_context_t* ctx, void* /*ud*/) {
    if (!ctx || !g_has_pregame_created_cb.load(std::memory_order_acquire)) return;

    const uint16_t result_code = static_cast<uint16_t>(ctx->args[1] & 0xFFFFu);
    std::string name;
    std::string league;

    const nm_build_id_t bid = g_p1_build_id.load(std::memory_order_acquire);
    void* state_ptr = reinterpret_cast<void*>(static_cast<uintptr_t>(ctx->args[0]));
    if (state_ptr) {
        struct ReadResCtx {
            nm_build_id_t bid;
            void*         state;
            std::string   name;
        } rctx{bid, state_ptr, {}};

        auto fn = [](void* raw) -> int64_t {
            auto* c = static_cast<ReadResCtx*>(raw);
            void* name_tb = nullptr;
            if (read_val(c->state, off_or(c->bid, "frame.create_char_state.name_textbox", 0x250u), name_tb) && name_tb) {
                c->name = read_msvc_ws_utf8(
                    static_cast<const uint8_t*>(name_tb) + off_or(c->bid, "ui.textbox.content", 0x338u));
            }
            return 0;
        };
        nm_exception_info_t ex{};
        (void)invoke_guarded(fn, &rctx, nullptr, &ex);
        name = std::move(rctx.name);
    }

    {
        std::lock_guard<std::mutex> lock(g_last_create_mutex);
        if (name.empty()) name = g_last_create_name;
        league = g_last_create_league;
    }

    fire_pregame_created_guarded(name.c_str(), league.c_str(), result_code);
}

#endif // _WIN32

} // namespace

Phase1Services::~Phase1Services() = default;

void Phase1Services::install_live_hooks(nm_build_id_t build_id, uint64_t image_base, HookEngine& hooks) {
    build_id_ = build_id;
    image_base_ = image_base;
    g_p1_build_id.store(build_id, std::memory_order_release);
    g_p1_image_base.store(image_base, std::memory_order_release);

#if defined(_WIN32)
    if (build_id == NM_BUILD_SYNTHETIC || build_id == NM_BUILD_UNKNOWN || image_base == 0) {
        return;
    }

    auto arm_chain = [&](const char* sym_name, nm_hook_phase_t phase, nm_hook_chain_callback_t cb) -> bool {
        const EmbeddedSymbol* sym = find_symbol(build_id, sym_name);
        if (!sym || sym->rva == 0 || sym->status == NM_SYM_STATUS_UNVERIFIED) return false;
        const uint64_t addr = image_base + sym->rva;
        if (!ptr_readable(reinterpret_cast<const void*>(static_cast<uintptr_t>(addr)), 24)) return false;

        nm_hook_handle_t handle = NM_HOOK_INVALID;
        const nm_status_t st = hooks.install_inline_chain(addr, sym->expected_bytes_hex, phase, 0, cb, nullptr, &handle);
        if (st == NM_OK && handle != NM_HOOK_INVALID) {
            installed_handles_.push_back(handle);
            return true;
        }
        logger_logf("WARN", "[PHASE1] Failed to arm chain hook '%s' at 0x%llX (st=%d)",
                    sym_name, static_cast<unsigned long long>(addr), static_cast<int>(st));
        return false;
    };

    const bool ok_wndmsg  = arm_chain("frame.state_stack_on_wndmsg",      NM_HOOK_PHASE_PRE,  &hook_state_stack_on_wndmsg_pre);
    const bool ok_ui_init = arm_chain("ui.ingame_ui_init",                NM_HOOK_PHASE_POST, &hook_ingame_ui_init_post);
    const bool ok_leave   = arm_chain("frame.ingame_state_leave",         NM_HOOK_PHASE_PRE,  &hook_ingame_state_leave_pre);
    const bool ok_lo_fill = arm_chain("ui.league_overlay_fill",           NM_HOOK_PHASE_POST, &hook_league_overlay_fill_post);
    const bool ok_cc_ent  = arm_chain("frame.create_char_enter",          NM_HOOK_PHASE_POST, &hook_create_char_enter_post);
    const bool ok_cc_lv   = arm_chain("frame.create_char_leave",          NM_HOOK_PHASE_PRE,  &hook_create_char_leave_pre);
    const bool ok_tab_sel = arm_chain("ui.tab_container_select",          NM_HOOK_PHASE_POST, &hook_tab_container_select_post);
    const bool ok_map_grp = arm_chain("ui.map_stash_select_group",        NM_HOOK_PHASE_POST, &hook_map_stash_subtab_post);
    const bool ok_map_key = arm_chain("ui.map_stash_select_key",          NM_HOOK_PHASE_POST, &hook_map_stash_subtab_post);
    const bool ok_c12     = arm_chain("network.login_write_create_char_0x12", NM_HOOK_PHASE_PRE, &hook_write_create_char_0x12_pre);
    const bool ok_c13     = arm_chain("frame.create_char_result_handler", NM_HOOK_PHASE_PRE,  &hook_create_char_result_0x13_pre);

    logger_logf("INFO",
                "[PHASE1] Armed live hooks (wndmsg=%s ui_init=%s ig_leave=%s league_fill=%s cc_enter=%s cc_leave=%s tab=%s map_grp=%s map_key=%s c2s_0x12=%s s2c_0x13=%s)",
                ok_wndmsg ? "OK" : "SKIP",
                ok_ui_init ? "OK" : "SKIP",
                ok_leave ? "OK" : "SKIP",
                ok_lo_fill ? "OK" : "SKIP",
                ok_cc_ent ? "OK" : "SKIP",
                ok_cc_lv ? "OK" : "SKIP",
                ok_tab_sel ? "OK" : "SKIP",
                ok_map_grp ? "OK" : "SKIP",
                ok_map_key ? "OK" : "SKIP",
                ok_c12 ? "OK" : "SKIP",
                ok_c13 ? "OK" : "SKIP");
#else
    (void)hooks;
#endif
}

void Phase1Services::reset(HookEngine* hooks) {
    if (hooks) {
        for (nm_hook_handle_t h : installed_handles_) {
            (void)hooks->remove(h);
        }
    }
    installed_handles_.clear();
    build_id_ = NM_BUILD_UNKNOWN;
    image_base_ = 0;
    g_p1_build_id.store(NM_BUILD_UNKNOWN, std::memory_order_release);
    g_p1_image_base.store(0, std::memory_order_release);

    {
        std::lock_guard<std::mutex> lock(g_p1_cb_mutex);
        g_screen_cb = nullptr;
        g_screen_ud = nullptr;
        g_input_cb = nullptr;
        g_input_ud = nullptr;
        g_pregame_creating_cb = nullptr;
        g_pregame_created_cb = nullptr;
        g_pregame_ud = nullptr;
    }
    g_has_screen_cb.store(false, std::memory_order_release);
    g_has_input_cb.store(false, std::memory_order_release);
    g_has_pregame_creating_cb.store(false, std::memory_order_release);
    g_has_pregame_created_cb.store(false, std::memory_order_release);

    {
        std::lock_guard<std::mutex> lock(g_last_create_mutex);
        g_last_create_name.clear();
        g_last_create_league.clear();
    }
}

// --- Native Stock-Tree UI Traversal & Layout Helpers (0.3.0) ---

nm_status_t Phase1Services::ui_get_active_root(void* fallback_captured_root, void** out_root) const {
    if (!out_root) return NM_ERR_INVALID_ARG;
    *out_root = nullptr;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) {
        return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    }
    struct RootCtx {
        nm_build_id_t bid;
        uint64_t      base;
        void*         root = nullptr;
    } ctx{build_id_, image_base_, nullptr};

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<RootCtx*>(raw);
        ActiveStateInfo st = query_active_state_unguarded(c->bid, c->base);
        c->root = st.ui_root;
        return 0;
    };
    nm_exception_info_t ex{};
    if (invoke_guarded(fn, &ctx, nullptr, &ex) == NM_OK && ctx.root != nullptr) {
        *out_root = ctx.root;
        return NM_OK;
    }
    if (fallback_captured_root != nullptr) {
        *out_root = fallback_captured_root;
        return NM_OK;
    }
    return NM_ERR_NOT_INITIALIZED;
#else
    (void)fallback_captured_root;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase1Services::ui_get_child_count(void* parent_control, uint32_t* out_count) const {
    if (!parent_control || !out_count) return NM_ERR_INVALID_ARG;
    *out_count = 0;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    struct Ctx {
        void*    parent;
        size_t   begin_off;
        size_t   end_off;
        uint32_t count = 0;
    } ctx{
        parent_control,
        off_or(build_id_, "ui.control.children_begin", 0xC0u),
        off_or(build_id_, "ui.control.children_end", 0xC8u),
        0,
    };
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        void* const* begin = nullptr;
        void* const* end = nullptr;
        if (!read_val(c->parent, c->begin_off, begin) || !read_val(c->parent, c->end_off, end)) return -1;
        if (!begin || !end || end < begin) return 0;
        const size_t n = static_cast<size_t>(end - begin);
        if (n > 4096) return -2;
        c->count = static_cast<uint32_t>(n);
        return 0;
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK || rc != 0) return (st != NM_OK) ? st : NM_ERR_GAME_EXCEPTION;
    *out_count = ctx.count;
    return NM_OK;
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase1Services::ui_get_child_at(void* parent_control, uint32_t index, void** out_child) const {
    if (!parent_control || !out_child) return NM_ERR_INVALID_ARG;
    *out_child = nullptr;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    struct Ctx {
        void*    parent;
        size_t   begin_off;
        size_t   end_off;
        uint32_t index;
        void*    child = nullptr;
    } ctx{
        parent_control,
        off_or(build_id_, "ui.control.children_begin", 0xC0u),
        off_or(build_id_, "ui.control.children_end", 0xC8u),
        index,
        nullptr,
    };
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        void* const* begin = nullptr;
        void* const* end = nullptr;
        if (!read_val(c->parent, c->begin_off, begin) || !read_val(c->parent, c->end_off, end)) return -1;
        if (!begin || !end || end < begin) return -3;
        const size_t n = static_cast<size_t>(end - begin);
        if (c->index >= n || n > 4096) return -3;
        if (!ptr_readable(begin + c->index, sizeof(void*))) return -1;
        c->child = begin[c->index];
        return 0;
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK) return st;
    if (rc == -3) return NM_ERR_INVALID_ARG;
    if (rc != 0) return NM_ERR_GAME_EXCEPTION;
    *out_child = ctx.child;
    return NM_OK;
#else
    (void)index;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase1Services::ui_get_vtable(void* control, uint64_t* out_vtable) const {
    if (!control || !out_vtable) return NM_ERR_INVALID_ARG;
    *out_vtable = 0;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    struct Ctx { void* ctrl; uint64_t vt = 0; } ctx{control, 0};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        return read_val(c->ctrl, 0, c->vt) ? 0 : -1;
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK || rc != 0) return (st != NM_OK) ? st : NM_ERR_GAME_EXCEPTION;
    *out_vtable = ctx.vt;
    return NM_OK;
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase1Services::ui_get_control_tag(void* control, uint16_t* out_tag) const {
    if (!control || !out_tag) return NM_ERR_INVALID_ARG;
    *out_tag = 0;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    struct Ctx { void* ctrl; size_t tag_off; uint16_t tag = 0; } ctx{
        control, off_or(build_id_, "ui.control.tag", 0x1D8u), 0};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        return read_val(c->ctrl, c->tag_off, c->tag) ? 0 : -1;
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK || rc != 0) return (st != NM_OK) ? st : NM_ERR_GAME_EXCEPTION;
    *out_tag = ctx.tag;
    return NM_OK;
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase1Services::ui_get_control_id(void* control, char* out_utf8, size_t capacity, size_t* out_length) const {
    if (!control) return NM_ERR_INVALID_ARG;
    if (out_length) *out_length = 0;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    struct Ctx {
        void*       ctrl;
        size_t      id_off;
        std::string id;
    } ctx{control, off_or(build_id_, "ui.control.id", 0x1B8u), {}};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        if (!ptr_readable(static_cast<const uint8_t*>(c->ctrl) + c->id_off, sizeof(MsvcWString))) return -1;
        c->id = read_msvc_ws_utf8(static_cast<const uint8_t*>(c->ctrl) + c->id_off);
        return 0;
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK || rc != 0) return (st != NM_OK) ? st : NM_ERR_GAME_EXCEPTION;

    if (out_length) *out_length = ctx.id.size();
    if (!out_utf8 && capacity == 0) return NM_OK;
    if (!out_utf8 || capacity < ctx.id.size() + 1) return NM_ERR_BUFFER_TOO_SMALL;
    if (!ctx.id.empty()) std::memcpy(out_utf8, ctx.id.data(), ctx.id.size());
    out_utf8[ctx.id.size()] = '\0';
    return NM_OK;
#else
    (void)out_utf8;
    (void)capacity;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase1Services::ui_get_control_parent(void* control, void** out_parent) const {
    if (!control || !out_parent) return NM_ERR_INVALID_ARG;
    *out_parent = nullptr;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    struct Ctx { void* ctrl; size_t parent_off; void* parent = nullptr; } ctx{
        control, off_or(build_id_, "ui.control.parent", 0x1E0u), nullptr};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        return read_val(c->ctrl, c->parent_off, c->parent) ? 0 : -1;
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK || rc != 0) return (st != NM_OK) ? st : NM_ERR_GAME_EXCEPTION;
    *out_parent = ctx.parent;
    return NM_OK;
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase1Services::ui_is_visible(void* control, int32_t* out_visible) const {
    if (!control || !out_visible) return NM_ERR_INVALID_ARG;
    *out_visible = 0;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    struct Ctx { nm_build_id_t bid; uint64_t base; void* ctrl; int32_t vis = 0; } ctx{
        build_id_, image_base_, control, 0};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        if (!ptr_readable(c->ctrl, 0x1F0)) return -1;
        c->vis = control_is_visible_unguarded(c->bid, c->base, c->ctrl) ? 1 : 0;
        return 0;
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK || rc != 0) return (st != NM_OK) ? st : NM_ERR_GAME_EXCEPTION;
    *out_visible = ctx.vis;
    return NM_OK;
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase1Services::ui_set_width(void* control, float width) const {
    if (!control) return NM_ERR_INVALID_ARG;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const uint64_t fn_set_w = sym_addr(build_id_, image_base_, "ui.control_set_width");
    if (fn_set_w == 0) return NM_ERR_SYMBOL_NOT_FOUND;

    struct Ctx {
        void*    ctrl;
        uint64_t set_w_addr;
        uint64_t cb_vt;
        size_t   layout_off;
        float    width;
    } ctx{
        control,
        fn_set_w,
        sym_addr(build_id_, image_base_, "ui.vtable_checkbox"),
        off_or(build_id_, "ui.control.layout", 0x220u),
        width,
    };
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        if (!ptr_readable(c->ctrl, 0x280)) return -1;
        uint64_t vt = 0;
        if (c->cb_vt != 0 && read_val(c->ctrl, 0, vt) && vt == c->cb_vt) {
            void* layout = static_cast<uint8_t*>(c->ctrl) + c->layout_off;
            void** lvt = nullptr;
            if (read_val(layout, 0, lvt) && lvt && ptr_readable(lvt, 3 * sizeof(void*)) && lvt[2]) {
                using SetAutoWidthFn = void(NM_CALL*)(void*, uint8_t);
                reinterpret_cast<SetAutoWidthFn>(lvt[2])(layout, 0);
            }
        }
        using SetWidthFn = void(NM_CALL*)(void*, float);
        reinterpret_cast<SetWidthFn>(static_cast<uintptr_t>(c->set_w_addr))(c->ctrl, c->width);
        return 0;
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    return (st == NM_OK && rc == 0) ? NM_OK : NM_ERR_GAME_EXCEPTION;
#else
    (void)width;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase1Services::ui_set_height(void* control, float height) const {
    if (!control) return NM_ERR_INVALID_ARG;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const uint64_t fn_set_h = sym_addr(build_id_, image_base_, "ui.control_set_height");
    if (fn_set_h == 0) return NM_ERR_SYMBOL_NOT_FOUND;

    struct Ctx { void* ctrl; uint64_t set_h_addr; float height; } ctx{control, fn_set_h, height};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        if (!ptr_readable(c->ctrl, 0x280)) return -1;
        using SetHeightFn = void(NM_CALL*)(void*, float);
        reinterpret_cast<SetHeightFn>(static_cast<uintptr_t>(c->set_h_addr))(c->ctrl, c->height);
        return 0;
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    return (st == NM_OK && rc == 0) ? NM_OK : NM_ERR_GAME_EXCEPTION;
#else
    (void)height;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase1Services::ui_create_checkbox_child(
    void*       parent_control,
    const char* caption_utf8,
    void**      out_checkbox) const {
    if (!parent_control || !out_checkbox) return NM_ERR_INVALID_ARG;
    *out_checkbox = nullptr;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const uint64_t fn_create = sym_addr(build_id_, image_base_, "ui.checkbox_create_child");
    const uint64_t fn_assign = sym_addr(build_id_, image_base_, "runtime.wstring_assign");
    const uint64_t fn_layout = sym_addr(build_id_, image_base_, "ui.layout_update");
    if (fn_create == 0 || fn_assign == 0) return NM_ERR_SYMBOL_NOT_FOUND;

    struct Ctx {
        void*        parent;
        uint64_t     create_addr;
        uint64_t     assign_addr;
        uint64_t     layout_addr;
        size_t       children_off;
        size_t       x_anchor_off;
        size_t       x_offset_off;
        size_t       caption_off;
        size_t       layout_off;
        std::wstring caption;
        void*        created = nullptr;
    } ctx{
        parent_control,
        fn_create,
        fn_assign,
        fn_layout,
        off_or(build_id_, "ui.control.children_container", 0xB8u),
        off_or(build_id_, "ui.control.x_anchor", 0x158u),
        off_or(build_id_, "ui.control.x_offset", 0x15Cu),
        off_or(build_id_, "ui.button.caption_interface", 0x3F8u),
        off_or(build_id_, "ui.control.layout", 0x220u),
        caption_utf8 ? utf8_to_wide_p1(caption_utf8) : std::wstring{},
        nullptr,
    };

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        if (!ptr_readable(static_cast<const uint8_t*>(c->parent) + c->children_off, 0x18)) return -1;
        using CreateCbFn = void*(NM_CALL*)(void*);
        void* cb = reinterpret_cast<CreateCbFn>(static_cast<uintptr_t>(c->create_addr))(
            static_cast<uint8_t*>(c->parent) + c->children_off);
        if (!cb || !ptr_readable(cb, 0x4E8)) return -2;

        // Match LeagueCard stock checkbox alignment (HAlign=3 right, offset 0.0f).
        auto* cb_bytes = static_cast<uint8_t*>(cb);
        cb_bytes[c->x_anchor_off] = 3u;
        const float zero = 0.0f;
        std::memcpy(cb_bytes + c->x_offset_off, &zero, sizeof(float));

        if (!c->caption.empty()) {
            void* sub = cb_bytes + c->caption_off;
            void** svt = nullptr;
            if (read_val(sub, 0, svt) && svt && ptr_readable(svt, 3 * sizeof(void*)) && svt[2]) {
                using AssignFn  = void*(NM_CALL*)(void*, const wchar_t*, uint64_t);
                using SetTextFn = void(NM_CALL*)(void*, const void*);
                MsvcWString ws{};
                std::memset(&ws, 0, sizeof(ws));
                ws.cap = 7;
                reinterpret_cast<AssignFn>(static_cast<uintptr_t>(c->assign_addr))(
                    &ws, c->caption.c_str(), static_cast<uint64_t>(c->caption.size()));
                // Takes std::wstring by value and frees internally (0x1418f4c70).
                reinterpret_cast<SetTextFn>(svt[2])(sub, &ws);
            }
        }

        // Enable auto-width & run layout update so natural width is immediately measurable via ui_get_rect.
        void* layout = cb_bytes + c->layout_off;
        void** lvt = nullptr;
        if (read_val(layout, 0, lvt) && lvt && ptr_readable(lvt, 9 * sizeof(void*))) {
            if (lvt[3]) {
                using SetAutoSizeFn = void(NM_CALL*)(void*, uint8_t, uint8_t);
                reinterpret_cast<SetAutoSizeFn>(lvt[3])(layout, 1, 0);
            }
            if (c->layout_addr != 0) {
                using LayoutUpdateFn = void(NM_CALL*)(void*);
                reinterpret_cast<LayoutUpdateFn>(static_cast<uintptr_t>(c->layout_addr))(layout);
            } else if (lvt[8]) {
                using LayoutUpdateFn = void(NM_CALL*)(void*);
                reinterpret_cast<LayoutUpdateFn>(lvt[8])(layout);
            }
        }

        c->created = cb;
        return 0;
    };

    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK || rc != 0 || !ctx.created) return (st != NM_OK) ? st : NM_ERR_GAME_EXCEPTION;
    *out_checkbox = ctx.created;
    return NM_OK;
#else
    (void)caption_utf8;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase1Services::ui_create_stack_layout(void* parent_control, int32_t vertical, void** out_layout) const {
    if (!out_layout) return NM_ERR_INVALID_ARG;
    *out_layout = nullptr;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const uint64_t fn_pool   = sym_addr(build_id_, image_base_, "ui.control_pool");
    const uint64_t fn_create = sym_addr(build_id_, image_base_, "ui.stack_layout_create");
    const uint64_t fn_add    = sym_addr(build_id_, image_base_, "ui.add_child");
    if (fn_pool == 0 || fn_create == 0 || (parent_control && fn_add == 0)) return NM_ERR_SYMBOL_NOT_FOUND;

    struct Ctx {
        void*    parent;
        uint64_t pool_addr;
        uint64_t create_addr;
        uint64_t add_addr;
        size_t   children_off;
        size_t   begin_off;
        size_t   end_off;
        size_t   layout_off;
        uint8_t  vertical;
        void*    created = nullptr;
    } ctx{
        parent_control,
        fn_pool,
        fn_create,
        fn_add,
        off_or(build_id_, "ui.control.children_container", 0xB8u),
        off_or(build_id_, "ui.control.children_begin", 0xC0u),
        off_or(build_id_, "ui.control.children_end", 0xC8u),
        off_or(build_id_, "ui.control.layout", 0x220u),
        static_cast<uint8_t>(vertical ? 1 : 0),
        nullptr,
    };

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        using PoolFn   = void*(NM_CALL*)();
        using CreateFn = void*(NM_CALL*)(void*);
        void* pool = reinterpret_cast<PoolFn>(static_cast<uintptr_t>(c->pool_addr))();
        if (!pool) return -1;
        void* sl = reinterpret_cast<CreateFn>(static_cast<uintptr_t>(c->create_addr))(pool);
        if (!sl || !ptr_readable(sl, 0x2B8)) return -2;

        void* layout = static_cast<uint8_t*>(sl) + c->layout_off;
        void** lvt = nullptr;
        if (read_val(layout, 0, lvt) && lvt && ptr_readable(lvt, 3 * sizeof(void*)) && lvt[2]) {
            using SetVertFn = void(NM_CALL*)(void*, uint8_t);
            reinterpret_cast<SetVertFn>(lvt[2])(layout, c->vertical);
        }

        if (c->parent) {
            void* const* begin = nullptr;
            void* const* end = nullptr;
            if (!read_val(c->parent, c->begin_off, begin) || !read_val(c->parent, c->end_off, end)) return -3;
            const uint64_t idx = (begin && end && end >= begin) ? static_cast<uint64_t>(end - begin) : 0ull;
            using AddChildFn = void(NM_CALL*)(void*, uint64_t, void*);
            reinterpret_cast<AddChildFn>(static_cast<uintptr_t>(c->add_addr))(
                static_cast<uint8_t*>(c->parent) + c->children_off, idx, sl);
        }

        c->created = sl;
        return 0;
    };

    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK || rc != 0 || !ctx.created) return (st != NM_OK) ? st : NM_ERR_GAME_EXCEPTION;
    *out_layout = ctx.created;
    return NM_OK;
#else
    (void)parent_control;
    (void)vertical;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase1Services::ui_layout_update(void* control) const {
    if (!control) return NM_ERR_INVALID_ARG;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const uint64_t fn_upd = sym_addr(build_id_, image_base_, "ui.layout_update");
    if (fn_upd == 0) return NM_ERR_SYMBOL_NOT_FOUND;

    struct Ctx {
        void*    ctrl;
        uint64_t upd_addr;
        uint64_t sl_vt;
        size_t   layout_off;
    } ctx{
        control,
        fn_upd,
        sym_addr(build_id_, image_base_, "ui.vtable_stack_layout"),
        off_or(build_id_, "ui.control.layout", 0x220u),
    };
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        if (!ptr_readable(c->ctrl, c->layout_off + sizeof(void*))) return -1;
        void* layout = static_cast<uint8_t*>(c->ctrl) + c->layout_off;
        uint64_t vt = 0;
        if (c->sl_vt != 0 && read_val(c->ctrl, 0, vt) && vt == c->sl_vt) {
            void** lvt = nullptr;
            if (read_val(layout, 0, lvt) && lvt && ptr_readable(lvt, 16 * sizeof(void*)) && lvt[15]) {
                using RelayoutFn = void(NM_CALL*)(void*);
                reinterpret_cast<RelayoutFn>(lvt[15])(layout);
            }
        }
        using UpdateFn = void(NM_CALL*)(void*);
        reinterpret_cast<UpdateFn>(static_cast<uintptr_t>(c->upd_addr))(layout);
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

nm_status_t Phase1Services::ui_set_anchor(
    void*    control,
    uint32_t x_anchor,
    float    x_offset,
    uint32_t y_anchor,
    float    y_offset) const {
    if (!control) return NM_ERR_INVALID_ARG;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const uint64_t fn_upd = sym_addr(build_id_, image_base_, "ui.layout_update");

    struct Ctx {
        void*    ctrl;
        uint64_t upd_addr;
        size_t   x_anc_off;
        size_t   x_off_off;
        size_t   y_anc_off;
        size_t   y_off_off;
        size_t   layout_off;
        uint32_t x_anchor;
        float    x_offset;
        uint32_t y_anchor;
        float    y_offset;
    } ctx{
        control,
        fn_upd,
        off_or(build_id_, "ui.control.x_anchor", 0x158u),
        off_or(build_id_, "ui.control.x_offset", 0x15Cu),
        off_or(build_id_, "ui.control.y_anchor", 0x160u),
        off_or(build_id_, "ui.control.y_offset", 0x164u),
        off_or(build_id_, "ui.control.layout", 0x220u),
        x_anchor,
        x_offset,
        y_anchor,
        y_offset,
    };

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        if (!ptr_readable(c->ctrl, c->layout_off + sizeof(void*))) return -1;
        auto* bytes = static_cast<uint8_t*>(c->ctrl);
        if (c->x_anchor <= 0xFEu) {
            bytes[c->x_anc_off] = static_cast<uint8_t>(c->x_anchor);
            std::memcpy(bytes + c->x_off_off, &c->x_offset, sizeof(float));
        }
        if (c->y_anchor <= 0xFEu) {
            bytes[c->y_anc_off] = static_cast<uint8_t>(c->y_anchor);
            std::memcpy(bytes + c->y_off_off, &c->y_offset, sizeof(float));
        }
        if (c->upd_addr != 0) {
            using UpdateFn = void(NM_CALL*)(void*);
            reinterpret_cast<UpdateFn>(static_cast<uintptr_t>(c->upd_addr))(bytes + c->layout_off);
        }
        return 0;
    };

    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    return (st == NM_OK && rc == 0) ? NM_OK : NM_ERR_GAME_EXCEPTION;
#else
    (void)x_anchor;
    (void)x_offset;
    (void)y_anchor;
    (void)y_offset;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

// --- Screens & Tabs (0.3.0) ---

nm_status_t Phase1Services::screens_get_state(nm_screens_state_t* out_state) const {
    if (!out_state) return NM_ERR_INVALID_ARG;
    std::memset(out_state, 0, sizeof(*out_state));
    out_state->struct_size = sizeof(*out_state);
    out_state->screen = NM_SCREEN_UNKNOWN;
    out_state->stash_mode = 0xFFFFFFFFu;
    out_state->active_stash_tab_index = -1;
    out_state->active_map_tier = -1;
    out_state->active_map_subtab = -1;
    out_state->selected_league_index = 0xFFFFFFFFu;

#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;

    struct Ctx {
        nm_build_id_t       bid;
        uint64_t            base;
        nm_screens_state_t* out;
    } ctx{build_id_, image_base_, out_state};

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        const nm_build_id_t bid = c->bid;
        const uint64_t base = c->base;
        auto* out = c->out;

        ActiveStateInfo st = query_active_state_unguarded(bid, base);
        out->screen = static_cast<int32_t>(st.screen);
        out->active_ui_root = st.ui_root;

        if (st.create_char_state) {
            (void)read_val(st.create_char_state, off_or(bid, "frame.create_char_state.create_frame", 0x248u), out->create_char_frame);
            (void)read_val(st.create_char_state, off_or(bid, "frame.create_char_state.league_overlay", 0x2B8u), out->league_overlay);
            (void)read_val(st.create_char_state, off_or(bid, "frame.create_char_state.selected_league_index", 0x2C0u), out->selected_league_index);
            if (out->league_overlay) {
                (void)read_val(out->league_overlay, off_or(bid, "ui.league_overlay.cards_flow", 0x3C0u), out->league_cards_flow);
            }
        }

        if (!st.ingame_state) {
            return 0;
        }

        void* hud = nullptr;
        if (!read_val(st.ingame_state, off_or(bid, "frame.ingame_state.ingame_ui", 0x978u), hud) || !hud) {
            return 0;
        }
        out->hud_root = hud;
        if (control_is_visible_unguarded(bid, base, hud)) {
            out->visible_panels |= NM_PANEL_HUD;
        }

        void* inv = nullptr;
        if (read_val(hud, off_or(bid, "ui.ingame_ui.inventory_panel", 0x628u), inv) && inv) {
            out->inventory_panel = inv;
            void* inv_content = nullptr;
            void* cos_content = nullptr;
            (void)read_val(inv, off_or(bid, "ui.inventory_panel.inventory_tab_container", 0x518u), inv_content);
            (void)read_val(inv, off_or(bid, "ui.inventory_panel.cosmetics_tab_container", 0x520u), cos_content);
            out->inventory_content = inv_content;
            if (control_is_visible_unguarded(bid, base, inv)) {
                out->visible_panels |= NM_PANEL_INVENTORY;
                if (cos_content && control_flag_visible_unguarded(bid, cos_content)) {
                    out->visible_panels |= NM_PANEL_COSMETICS;
                }
            }
        }

        void* passive = nullptr;
        if (read_val(hud, off_or(bid, "ui.ingame_ui.passive_panel", 0x650u), passive) && passive) {
            out->passive_panel = passive;
            (void)read_val(passive, off_or(bid, "ui.passive_shell.title_bar", 0x3E0u), out->passive_title_bar);
            if (control_is_visible_unguarded(bid, base, passive)) {
                out->visible_panels |= NM_PANEL_PASSIVE_TREE;
            }
        }

        void* atlas = nullptr;
        if (read_val(hud, off_or(bid, "ui.ingame_ui.atlas_passive_panel", 0x668u), atlas) && atlas &&
            control_is_visible_unguarded(bid, base, atlas)) {
            out->visible_panels |= NM_PANEL_ATLAS_TREE;
        }

        void* map_view = nullptr;
        if (read_val(hud, off_or(bid, "ui.ingame_ui.map_view", 0x6E0u), map_view) && map_view &&
            control_is_visible_unguarded(bid, base, map_view)) {
            out->visible_panels |= NM_PANEL_MINIMAP;
        }

        void* left_side = nullptr;
        if (read_val(hud, off_or(bid, "ui.ingame_ui.active_left_panel", 0x608u), left_side) && left_side &&
            control_is_visible_unguarded(bid, base, left_side)) {
            out->visible_panels |= NM_PANEL_LEFT_SIDE;
        }
        void* right_side = nullptr;
        if (read_val(hud, off_or(bid, "ui.ingame_ui.active_right_panel", 0x610u), right_side) && right_side &&
            control_is_visible_unguarded(bid, base, right_side)) {
            out->visible_panels |= NM_PANEL_RIGHT_SIDE;
        }

        void* vbuy = nullptr;
        void* vsell = nullptr;
        void* npc = nullptr;
        void* ptrade = nullptr;
        if (read_val(hud, off_or(bid, "ui.ingame_ui.vendor_buy", 0x7B8u), vbuy) && vbuy &&
            control_is_visible_unguarded(bid, base, vbuy)) {
            out->visible_panels |= NM_PANEL_VENDOR_BUY;
            if (!out->vendor_panel) out->vendor_panel = vbuy;
        }
        if (read_val(hud, off_or(bid, "ui.ingame_ui.vendor_sell", 0x7C8u), vsell) && vsell &&
            control_is_visible_unguarded(bid, base, vsell)) {
            out->visible_panels |= NM_PANEL_VENDOR_SELL;
            if (!out->vendor_panel) out->vendor_panel = vsell;
        }
        if (read_val(hud, off_or(bid, "ui.ingame_ui.npc_dialogue", 0x790u), npc) && npc &&
            control_is_visible_unguarded(bid, base, npc)) {
            out->visible_panels |= NM_PANEL_NPC_DIALOGUE;
            if (!out->vendor_panel) out->vendor_panel = npc;
        }
        if (read_val(hud, off_or(bid, "ui.ingame_ui.player_trade", 0x7C0u), ptrade) && ptrade &&
            control_is_visible_unguarded(bid, base, ptrade)) {
            out->visible_panels |= NM_PANEL_PLAYER_TRADE;
        }

        // Check personal (0), guild (1), trade (2) stash panels.
        void* p_stash = nullptr;
        void* g_stash = nullptr;
        void* t_stash = nullptr;
        (void)read_val(hud, off_or(bid, "ui.ingame_ui.stash_panel", 0x630u), p_stash);
        (void)read_val(hud, off_or(bid, "ui.ingame_ui.guild_stash_panel", 0x638u), g_stash);
        (void)read_val(hud, off_or(bid, "ui.ingame_ui.trade_stash_panel", 0x640u), t_stash);

        void* active_stash = nullptr;
        if (p_stash && control_is_visible_unguarded(bid, base, p_stash)) {
            out->visible_panels |= NM_PANEL_STASH;
            active_stash = p_stash;
            out->stash_mode = 0u;
        } else if (g_stash && control_is_visible_unguarded(bid, base, g_stash)) {
            out->visible_panels |= NM_PANEL_GUILD_STASH;
            active_stash = g_stash;
            out->stash_mode = 1u;
        } else if (t_stash && control_is_visible_unguarded(bid, base, t_stash)) {
            out->visible_panels |= NM_PANEL_TRADE_STASH;
            active_stash = t_stash;
            out->stash_mode = 2u;
        } else if (p_stash) {
            out->stash_panel = p_stash;
        }

        if (active_stash) {
            out->stash_panel = active_stash;
            void* stash_ctrl = nullptr;
            if (read_val(active_stash, off_or(bid, "ui.stash_panel.stash_control", 0x3A8u), stash_ctrl) && stash_ctrl) {
                uint8_t mode_byte = 0;
                if (read_val(stash_ctrl, off_or(bid, "ui.stash_control.mode", 0x580u), mode_byte)) {
                    out->stash_mode = mode_byte;
                }
                void* tab_cont = nullptr;
                if (read_val(stash_ctrl, off_or(bid, "ui.stash_control.tab_container", 0x3A8u), tab_cont) && tab_cont) {
                    uint8_t has_sel = 0;
                    uint64_t sel_idx = 0;
                    if (read_val(tab_cont, off_or(bid, "ui.tab_container.has_selection", 0x328u), has_sel) && has_sel != 0 &&
                        read_val(tab_cont, off_or(bid, "ui.tab_container.selected_index", 0x320u), sel_idx)) {
                        const uint64_t fn_find = sym_addr(bid, base, "ui.stash_find_view_by_ui_index");
                        if (fn_find != 0) {
                            using FindViewFn = void*(NM_CALL*)(void*, uint64_t);
                            void* mgr = static_cast<uint8_t*>(stash_ctrl) + off_or(bid, "ui.stash_control.view_manager", 0x3E8u);
                            void* view = reinterpret_cast<FindViewFn>(static_cast<uintptr_t>(fn_find))(mgr, sel_idx);

                            // Resolve folder child tab if selected view is a folder (view_flags & 0x02).
                            if (view) {
                                uint8_t vflags = 0;
                                if (read_val(view, off_or(bid, "ui.stash_tab_view.view_flags", 0x38u), vflags) && (vflags & 0x02u) != 0) {
                                    void* const* cbegin = nullptr;
                                    void* const* cend = nullptr;
                                    if (read_val(view, off_or(bid, "ui.stash_tab_view.child_views_begin", 0x160u), cbegin) &&
                                        read_val(view, off_or(bid, "ui.stash_tab_view.child_views_end", 0x168u), cend) &&
                                        cbegin && cend && cend > cbegin) {
                                        const size_t child_cnt = static_cast<size_t>(cend - cbegin);
                                        if (child_cnt <= 256 && ptr_readable(cbegin, child_cnt * sizeof(void*)) && cbegin[0]) {
                                            void* sub_bar = nullptr;
                                            if (read_val(cbegin[0], off_or(bid, "ui.stash_tab_view.owner_tab_bar", 0x48u), sub_bar) && sub_bar) {
                                                uint8_t sub_has = 0;
                                                uint64_t sub_idx = 0;
                                                if (read_val(sub_bar, off_or(bid, "ui.tab_container.has_selection", 0x328u), sub_has) &&
                                                    sub_has != 0 &&
                                                    read_val(sub_bar, off_or(bid, "ui.tab_container.selected_index", 0x320u), sub_idx) &&
                                                    sub_idx < child_cnt && cbegin[sub_idx]) {
                                                    view = cbegin[sub_idx];
                                                }
                                            }
                                        }
                                    }
                                }

                                uint16_t srv_idx = 0;
                                if (read_val(view, off_or(bid, "ui.stash_tab_view.server_tab_index", 0x08u), srv_idx)) {
                                    out->active_stash_tab_index = static_cast<int32_t>(srv_idx);
                                }
                                void* page = nullptr;
                                if (read_val(view, off_or(bid, "ui.stash_tab_view.page_control", 0x58u), page) && page) {
                                    out->active_stash_page = page;
                                    uint64_t pvt = 0;
                                    const uint64_t vt_map = sym_addr(bid, base, "ui.vtable_map_stash_page");
                                    const uint64_t vt_blight = sym_addr(bid, base, "ui.vtable_blight_stash_page");
                                    if (read_val(page, 0, pvt)) {
                                        if (vt_map != 0 && pvt == vt_map) {
                                            out->map_stash_page = page;
                                            out->visible_panels |= NM_PANEL_MAP_STASH;
                                            const size_t sg_off = off_or(bid, "ui.map_stash_page.selected_group", 0x550u);
                                            out->active_map_tier = Phase1Services::read_map_stash_selected_tier(page, sg_off);
                                            void* inner_tc = nullptr;
                                            if (read_val(page, off_or(bid, "ui.map_stash_page.inner_tab_container", 0x530u), inner_tc) && inner_tc) {
                                                uint8_t i_has = 0;
                                                uint64_t i_sel = 0;
                                                if (read_val(inner_tc, off_or(bid, "ui.tab_container.has_selection", 0x328u), i_has) &&
                                                    i_has != 0 &&
                                                    read_val(inner_tc, off_or(bid, "ui.tab_container.selected_index", 0x320u), i_sel)) {
                                                    out->active_map_subtab = static_cast<int32_t>(i_sel);
                                                }
                                            }
                                        } else if (vt_blight != 0 && pvt == vt_blight) {
                                            out->blight_stash_page = page;
                                            out->visible_panels |= NM_PANEL_BLIGHT_STASH;
                                        }
                                    }
                                }

                                // Read server StashTabInfo metadata from InstanceClient.
                                void* inst = nullptr;
                                if (out->active_stash_tab_index >= 0 &&
                                    read_val(stash_ctrl, off_or(bid, "ui.stash_control.instance_client", 0x3E0u), inst) && inst) {
                                    size_t vec_off = off_or(bid, "ui.instance_client.personal_stash_tabs", 0xC498u);
                                    if (out->stash_mode == 1u) vec_off = off_or(bid, "ui.instance_client.guild_stash_tabs", 0xC4B0u);
                                    else if (out->stash_mode == 2u) vec_off = off_or(bid, "ui.instance_client.trade_stash_tabs", 0xC4C8u);

                                    uint8_t* tbegin = nullptr;
                                    uint8_t* tend = nullptr;
                                    if (read_val(inst, vec_off, tbegin) && read_val(inst, vec_off + 8, tend) &&
                                        tbegin && tend && tend > tbegin) {
                                        constexpr size_t kTabStride = 0x68u;
                                        const size_t tcount = static_cast<size_t>(tend - tbegin) / kTabStride;
                                        const size_t tidx = static_cast<size_t>(out->active_stash_tab_index);
                                        if (tidx < tcount) {
                                            const uint8_t* info = tbegin + tidx * kTabStride;
                                            (void)read_val(info, off_or(bid, "ui.stash_tab_info.tab_id", 0x28u), out->active_stash_tab_id);
                                            (void)read_val(info, off_or(bid, "ui.stash_tab_info.stash_type", 0x34u), out->active_stash_tab_type);
                                            const std::string tname = read_msvc_ws_utf8(
                                                info + off_or(bid, "ui.stash_tab_info.name", 0x08u));
                                            copy_utf8_to_fixed(out->active_stash_tab_name, sizeof(out->active_stash_tab_name), tname);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        return 0;
    };

    nm_exception_info_t ex{};
    return invoke_guarded(fn, &ctx, nullptr, &ex);
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase1Services::screens_set_callback(nm_screen_event_callback_t cb, void* user_data) {
    {
        std::lock_guard<std::mutex> lock(g_p1_cb_mutex);
        g_screen_cb = cb;
        g_screen_ud = cb ? user_data : nullptr;
    }
    g_has_screen_cb.store(cb != nullptr, std::memory_order_release);
    return NM_OK;
}

// --- Input Interception & Text Focus (0.3.0) ---

nm_status_t Phase1Services::input_set_callback(nm_input_callback_t cb, void* user_data) {
    {
        std::lock_guard<std::mutex> lock(g_p1_cb_mutex);
        g_input_cb = cb;
        g_input_ud = cb ? user_data : nullptr;
    }
    g_has_input_cb.store(cb != nullptr, std::memory_order_release);
    return NM_OK;
}

nm_status_t Phase1Services::input_is_text_focused(int32_t* out_focused) const {
    if (!out_focused) return NM_ERR_INVALID_ARG;
    *out_focused = 0;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    struct Ctx { nm_build_id_t bid; uint64_t base; int32_t focused = 0; } ctx{build_id_, image_base_, 0};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        c->focused = is_text_input_focused_unguarded(c->bid, c->base) ? 1 : 0;
        return 0;
    };
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, nullptr, &ex);
    if (st != NM_OK) return st;
    *out_focused = ctx.focused;
    return NM_OK;
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

// --- Hover & Cursor Item Inspection (0.3.0) ---

nm_status_t Phase1Services::hover_get_state(nm_hover_state_t* out_state) const {
    if (!out_state) return NM_ERR_INVALID_ARG;
    std::memset(out_state, 0, sizeof(*out_state));
    out_state->struct_size = sizeof(*out_state);
    out_state->cursor_mode = NM_CURSOR_EMPTY;
    out_state->use_cell_x = -1;
    out_state->use_cell_y = -1;
    out_state->hovered_item.grid_x = -1;
    out_state->hovered_item.grid_y = -1;
    out_state->cursor_item.grid_x = -1;
    out_state->cursor_item.grid_y = -1;

#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;

    struct Ctx {
        nm_build_id_t     bid;
        uint64_t          base;
        nm_hover_state_t* out;
    } ctx{build_id_, image_base_, out_state};

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        const nm_build_id_t bid = c->bid;
        const uint64_t base = c->base;
        auto* out = c->out;

        ActiveStateInfo st = query_active_state_unguarded(bid, base);
        if (!st.ui_manager) return 0;

        (void)read_val(st.ui_manager, off_or(bid, "ui.manager.mouse_x", 0x298u), out->mouse_x);
        (void)read_val(st.ui_manager, off_or(bid, "ui.manager.mouse_y", 0x29Cu), out->mouse_y);
        (void)read_val(st.ui_manager, off_or(bid, "ui.manager.hovered", 0x230u), out->hovered_control);
        (void)read_val(st.ui_manager, off_or(bid, "ui.manager.focused", 0x208u), out->focused_control);

        if (out->hovered_control) {
            (void)read_val(out->hovered_control, 0, out->hovered_vtable);

            // Walk parent chain to find enclosing InventoryItemView (tag 0xc004 or vtable match).
            const size_t tag_off = off_or(bid, "ui.control.tag", 0x1D8u);
            const size_t parent_off = off_or(bid, "ui.control.parent", 0x1E0u);
            const uint64_t vt_item_view = sym_addr(bid, base, "items.vtable_inv_item_view");
            void* cur = out->hovered_control;
            void* item_view = nullptr;
            for (int hop = 0; hop < 16 && cur != nullptr; ++hop) {
                uint16_t tag = 0;
                uint64_t vt = 0;
                if ((read_val(cur, tag_off, tag) && tag == NM_TAG_INV_ITEM_VIEW) ||
                    (vt_item_view != 0 && read_val(cur, 0, vt) && vt == vt_item_view)) {
                    item_view = cur;
                    break;
                }
                void* next = nullptr;
                if (!read_val(cur, parent_off, next)) break;
                cur = next;
            }

            if (item_view) {
                out->hovered_item.item_view = item_view;
                void* item_ent = nullptr;
                void* inv = nullptr;
                (void)read_val(item_view, off_or(bid, "items.item_view.entity", 0x420u), item_ent);
                (void)read_val(item_view, off_or(bid, "items.item_view.inventory", 0x670u), inv);
                (void)read_val(item_view, off_or(bid, "items.item_view.item_id", 0x678u), out->hovered_item.item_id);

                void* grid = nullptr;
                if (read_val(item_view, parent_off, grid) && grid) {
                    (void)read_val(grid, off_or(bid, "items.inv_grid.inventory_id", 0x5C0u), out->hovered_item.inventory_id);
                    if (!inv) {
                        (void)read_val(grid, off_or(bid, "items.inv_grid.inventory", 0x5C8u), inv);
                    }
                }
                if (inv) {
                    (void)lookup_item_grid_slot_unguarded(
                        bid, inv, out->hovered_item.item_id, out->hovered_item.grid_x, out->hovered_item.grid_y);
                }
                if (item_ent) {
                    populate_item_snapshot_unguarded(bid, base, item_ent, out->hovered_item);
                }
            }
        }

        // Cursor-held / use-mode item state (InGame only).
        if (st.ingame_state) {
            void* hud = nullptr;
            if (read_val(st.ingame_state, off_or(bid, "frame.ingame_state.ingame_ui", 0x978u), hud) && hud) {
                void** hvt = nullptr;
                if (read_val(hud, 0, hvt) && hvt && ptr_readable(hvt, 355 * sizeof(void*)) && hvt[354]) {
                    using GetCursorFn = void*(NM_CALL*)(void*);
                    void* cursor = reinterpret_cast<GetCursorFn>(hvt[354])(hud);
                    if (cursor && ptr_readable(cursor, 0x5A0)) {
                        uint8_t mode = 0;
                        (void)read_val(cursor, off_or(bid, "items.cursor.mode", 0x588u), mode);
                        out->cursor_mode = static_cast<int32_t>(mode);

                        void* cur_view = nullptr;
                        (void)read_val(cursor, off_or(bid, "items.cursor.item_view", 0x598u), cur_view);
                        out->cursor_item.item_view = cur_view;

                        if (mode == NM_CURSOR_HOLDING) {
                            const uint64_t fn_held = sym_addr(bid, base, "items.cursor_get_held_item");
                            if (fn_held != 0) {
                                using GetHeldFn = CursorItemRef*(NM_CALL*)(void**, CursorItemRef*);
                                CursorItemRef ref{};
                                void* cursor_ptr = cursor;
                                reinterpret_cast<GetHeldFn>(static_cast<uintptr_t>(fn_held))(&cursor_ptr, &ref);
                                out->cursor_item.inventory_id = ref.inv_id;
                                out->cursor_item.item_id = ref.item_id;
                                if (ref.item_entity) {
                                    populate_item_snapshot_unguarded(bid, base, ref.item_entity, out->cursor_item);
                                }
                            } else if (cur_view) {
                                void* item_ent = nullptr;
                                if (read_val(cur_view, off_or(bid, "items.item_view.entity", 0x420u), item_ent) && item_ent) {
                                    populate_item_snapshot_unguarded(bid, base, item_ent, out->cursor_item);
                                }
                            }
                        } else if (mode == NM_CURSOR_USE_MODE) {
                            (void)read_val(cursor, off_or(bid, "items.cursor.use_source_inv_id", 0x52Cu), out->use_source_inv_id);
                            uint32_t ux = 0;
                            uint32_t uy = 0;
                            if (read_val(cursor, off_or(bid, "items.cursor.use_cell_x", 0x530u), ux)) {
                                out->use_cell_x = static_cast<int32_t>(ux);
                            }
                            if (read_val(cursor, off_or(bid, "items.cursor.use_cell_y", 0x534u), uy)) {
                                out->use_cell_y = static_cast<int32_t>(uy);
                            }
                            if (cur_view) {
                                void* item_ent = nullptr;
                                if (read_val(cur_view, off_or(bid, "items.item_view.entity", 0x420u), item_ent) && item_ent) {
                                    out->cursor_item.inventory_id = out->use_source_inv_id;
                                    out->cursor_item.grid_x = out->use_cell_x;
                                    out->cursor_item.grid_y = out->use_cell_y;
                                    populate_item_snapshot_unguarded(bid, base, item_ent, out->cursor_item);
                                }
                            }
                        }
                    }
                }
            }
        }
        return 0;
    };

    nm_exception_info_t ex{};
    return invoke_guarded(fn, &ctx, nullptr, &ex);
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

// --- Pre-Game Character Creation Hooks (0.3.0) ---

nm_status_t Phase1Services::pregame_set_callbacks(
    nm_pregame_creating_callback_t on_creating,
    nm_pregame_created_callback_t  on_created,
    void*                          user_data) {
    {
        std::lock_guard<std::mutex> lock(g_p1_cb_mutex);
        g_pregame_creating_cb = on_creating;
        g_pregame_created_cb = on_created;
        g_pregame_ud = (on_creating || on_created) ? user_data : nullptr;
    }
    g_has_pregame_creating_cb.store(on_creating != nullptr, std::memory_order_release);
    g_has_pregame_created_cb.store(on_created != nullptr, std::memory_order_release);
    return NM_OK;
}

// --- Deterministic Dispatch Helpers ---

void Phase1Services::dispatch_screen_event(int32_t event_kind, void* primary_handle, uint64_t param0) {
    fire_screen_event_guarded(event_kind, primary_handle, param0);
}

int32_t Phase1Services::dispatch_input_event(const nm_input_event_t* ev) {
    return fire_input_event_guarded(ev);
}

int32_t Phase1Services::dispatch_pregame_creating(
    const char* name_utf8,
    const char* league_utf8,
    const char* start_state_utf8,
    char*       out_start_state_utf8,
    size_t      out_capacity) {
    return fire_pregame_creating_guarded(name_utf8, league_utf8, start_state_utf8, out_start_state_utf8, out_capacity);
}

void Phase1Services::dispatch_pregame_created(const char* name_utf8, const char* league_utf8, uint16_t result_code) {
    fire_pregame_created_guarded(name_utf8, league_utf8, result_code);
}

int32_t Phase1Services::parse_map_key_tier(const char* metadata_id_utf8) noexcept {
    if (!metadata_id_utf8) return -1;
    static constexpr const char kMarker[] = "MapKeyTier";
    static constexpr size_t kMarkerLen = sizeof(kMarker) - 1;
    const char* found = std::strstr(metadata_id_utf8, kMarker);
    if (!found) return -1;
    const char* p = found + kMarkerLen;
    if (*p < '0' || *p > '9') return -1;
    int32_t val = 0;
    while (*p >= '0' && *p <= '9') {
        val = val * 10 + (*p - '0');
        if (val > 99) return -1;
        ++p;
    }
    if (*p != '\0') return -1;
    return (val > 0) ? val : -1;
}

bool Phase1Services::extract_base_item_type_row(
    const void*     base_static,
    size_t          row_ref_offset,
    const wchar_t** out_metadata_id_w,
    const void**    out_item_classes_row_ref) noexcept {
    if (out_metadata_id_w) *out_metadata_id_w = nullptr;
    if (out_item_classes_row_ref) *out_item_classes_row_ref = nullptr;
    if (!base_static) return false;

    // BaseStatic + row_ref_offset (0x68) is an inline RowRef<BaseItemTypes>:
    //   +0x00: BaseItemTypesRow* base_row
    //   +0x08: BaseItemTypesDat*
    // Inside BaseItemTypesRow:
    //   +0x00: const wchar_t* Id ("Metadata/Items/...")
    //   +0x08: ItemClassesRow* (inline RowRef<ItemClasses> {ItemClassesRow*, ItemClassesDat*})
    const void* base_row = nullptr;
#if defined(_WIN32)
    if (!read_val(base_static, row_ref_offset, base_row) || !base_row || !ptr_readable(base_row, 0x18u)) {
        return false;
    }
    const wchar_t* meta_w = nullptr;
    if (read_val(base_row, 0x00u, meta_w) && out_metadata_id_w) {
        *out_metadata_id_w = meta_w;
    }
#else
    std::memcpy(&base_row, static_cast<const uint8_t*>(base_static) + row_ref_offset, sizeof(base_row));
    if (!base_row) return false;
    const wchar_t* meta_w = nullptr;
    std::memcpy(&meta_w, static_cast<const uint8_t*>(base_row) + 0x00u, sizeof(meta_w));
    if (out_metadata_id_w) *out_metadata_id_w = meta_w;
#endif
    if (out_item_classes_row_ref) {
        *out_item_classes_row_ref = static_cast<const uint8_t*>(base_row) + 0x08u;
    }
    return true;
}

int32_t Phase1Services::read_map_stash_selected_tier(
    const void* map_stash_page,
    size_t      selected_group_offset) noexcept {
    if (!map_stash_page) return -1;

    // MapStashPage + 0x550 is std::optional<std::variant<RowRef0, RowRef1, RowRef2>>:
    //   +0x00: void* row_ptr
    //   +0x08: void* dat_ptr
    //   +0x10: int8_t variant_index (0 = BaseItemTypesRow* "Metadata/Items/Maps/MapKeyTier1..17")
    //   +0x18: uint8_t has_value
    uint8_t has_val = 0;
    int8_t var_idx = -1;
    const void* row_ptr = nullptr;
    const wchar_t* id_w = nullptr;

#if defined(_WIN32)
    if (!read_val(map_stash_page, selected_group_offset + 0x18u, has_val) || has_val == 0) return -1;
    if (!read_val(map_stash_page, selected_group_offset + 0x10u, var_idx) || var_idx != 0) return -1;
    if (!read_val(map_stash_page, selected_group_offset + 0x00u, row_ptr) || !row_ptr) return -1;
    if (!read_val(row_ptr, 0x00u, id_w) || !id_w) return -1;
    const std::string id_u8 = read_c_wstr_utf8(id_w);
    return parse_map_key_tier(id_u8.c_str());
#else
    const auto* base = static_cast<const uint8_t*>(map_stash_page) + selected_group_offset;
    std::memcpy(&has_val, base + 0x18u, sizeof(has_val));
    if (has_val == 0) return -1;
    std::memcpy(&var_idx, base + 0x10u, sizeof(var_idx));
    if (var_idx != 0) return -1;
    std::memcpy(&row_ptr, base + 0x00u, sizeof(row_ptr));
    if (!row_ptr) return -1;
    std::memcpy(&id_w, static_cast<const uint8_t*>(row_ptr) + 0x00u, sizeof(id_w));
    if (!id_w) return -1;
    char ascii[256] = {};
    size_t n = 0;
    while (n + 1 < sizeof(ascii) && id_w[n] != L'\0') {
        const wchar_t ch = id_w[n];
        ascii[n++] = (ch >= 0 && ch < 128) ? static_cast<char>(ch) : '?';
    }
    ascii[n] = '\0';
    return parse_map_key_tier(ascii);
#endif
}

} // namespace nativemods
