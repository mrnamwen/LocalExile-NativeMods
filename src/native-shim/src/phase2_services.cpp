#include "phase2_services.hpp"

#include "address_table.hpp"
#include "exception_wall.hpp"
#include "game_allocator.hpp"
#include "hook_engine.hpp"
#include "phase1_services.hpp"
#include "shim_logger.hpp"

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstddef>
#include <cstring>
#include <cwchar>
#include <mutex>
#include <string>
#include <unordered_set>

#if defined(_WIN32)
#  define WIN32_LEAN_AND_MEAN
#  include <windows.h>
#  ifdef exception_code
#    undef exception_code
#  endif
#endif

namespace nativemods {

// --- ABI 0.4.0 layout static_asserts (zero hidden padding, exact C# LayoutKind.Sequential parity) ---
static_assert(NM_ABI_VERSION == 0x00000400u, "NM_ABI_VERSION must be 0.4.0");
static_assert(sizeof(nm_abi_table_t) == 864u, "nm_abi_table_t must be 864 bytes (8 + 107*8)");
static_assert(offsetof(nm_abi_table_t, pregame_set_callbacks) == 8u + 88u * 8u, "ABI 0.3.0 prefix preserved");
static_assert(offsetof(nm_abi_table_t, world_get_local_player) == 8u + 89u * 8u, "ABI 0.4.0 starts at slot 89");
static_assert(offsetof(nm_abi_table_t, camera_set_zoom_override) == 8u + 106u * 8u, "ABI 0.4.0 ends at slot 106");
static_assert(sizeof(nm_world_entity_query_t) == 160u, "nm_world_entity_query_t must be 160 bytes");
static_assert(sizeof(nm_world_entity_snapshot_t) == 760u, "nm_world_entity_snapshot_t must be 760 bytes");
static_assert(offsetof(nm_world_entity_snapshot_t, entity_ptr) == 8u, "entity_ptr at +8");
static_assert(offsetof(nm_world_entity_snapshot_t, metadata_path) == 56u, "metadata_path at +56");
static_assert(offsetof(nm_world_entity_snapshot_t, world_item) == 312u, "world_item at +312");
static_assert(sizeof(nm_stat_entry_t) == 8u, "nm_stat_entry_t must be 8 bytes");
static_assert(sizeof(nm_area_info_t) == 352u, "nm_area_info_t must be 352 bytes");
static_assert(offsetof(nm_area_info_t, ruleset_ids) == 32u, "ruleset_ids at +32");
static_assert(offsetof(nm_area_info_t, area_id) == 96u, "area_id at +96");
static_assert(offsetof(nm_area_info_t, area_name) == 224u, "area_name at +224");
static_assert(sizeof(nm_item_filter_state_t) == 784u, "nm_item_filter_state_t must be 784 bytes");
static_assert(offsetof(nm_item_filter_state_t, filter_name) == 16u, "filter_name at +16");
static_assert(offsetof(nm_item_filter_state_t, filter_folder) == 144u, "filter_folder at +144");
static_assert(offsetof(nm_item_filter_state_t, filter_path) == 400u, "filter_path at +400");
static_assert(sizeof(nm_item_filter_result_t) == 72u, "nm_item_filter_result_t must be 72 bytes");
static_assert(offsetof(nm_item_filter_result_t, label_x) == 52u, "label_x at +52");
static_assert(sizeof(nm_camera_state_t) == 120u, "nm_camera_state_t must be 120 bytes");
static_assert(offsetof(nm_camera_state_t, view_proj) == 56u, "view_proj at +56");

namespace {

std::mutex                  g_p2_cb_mutex;
nm_area_event_callback_t    g_area_cb = nullptr;
void*                       g_area_ud = nullptr;
nm_filter_reload_callback_t g_filter_reload_cb = nullptr;
void*                       g_filter_reload_ud = nullptr;

std::atomic<bool>           g_has_area_cb{false};
std::atomic<bool>           g_has_filter_reload_cb{false};
std::atomic<nm_build_id_t>  g_p2_build_id{NM_BUILD_UNKNOWN};
std::atomic<uint64_t>       g_p2_image_base{0};

// Track pending area entry between ClientWorld::InitArea and AreaLoadingState::Leave so
// we emit NM_AREA_EVT_ENTERED cleanly once gameplay/entities/rulesets are active.
std::atomic<bool>           g_area_enter_pending{false};

void copy_utf8_fixed(char* dst, size_t cap, const std::string& src) noexcept {
    if (!dst || cap == 0) return;
    const size_t n = std::min(src.size(), cap - 1);
    if (n > 0) {
        std::memcpy(dst, src.data(), n);
    }
    dst[n] = '\0';
}

void fire_area_event_guarded(int32_t event_kind, const nm_area_info_t* area_info) {
    if (!area_info || !g_has_area_cb.load(std::memory_order_acquire)) return;
    nm_area_event_callback_t cb = nullptr;
    void* ud = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_p2_cb_mutex);
        cb = g_area_cb;
        ud = g_area_ud;
    }
    if (!cb) return;

    struct Ctx {
        nm_area_event_callback_t cb;
        int32_t                  kind;
        const nm_area_info_t*    info;
        void*                    ud;
    } ctx{cb, event_kind, area_info, ud};

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        c->cb(c->kind, c->info, c->ud);
        return 0;
    };
    nm_exception_info_t ex{};
    (void)invoke_guarded(fn, &ctx, nullptr, &ex);
}

void fire_filter_reloaded_guarded(int32_t succeeded, const nm_item_filter_state_t* filter_state) {
    if (!filter_state || !g_has_filter_reload_cb.load(std::memory_order_acquire)) return;
    nm_filter_reload_callback_t cb = nullptr;
    void* ud = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_p2_cb_mutex);
        cb = g_filter_reload_cb;
        ud = g_filter_reload_ud;
    }
    if (!cb) return;

    struct Ctx {
        nm_filter_reload_callback_t   cb;
        int32_t                       ok;
        const nm_item_filter_state_t* state;
        void*                         ud;
    } ctx{cb, succeeded, filter_state, ud};

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        c->cb(c->ok, c->state, c->ud);
        return 0;
    };
    nm_exception_info_t ex{};
    (void)invoke_guarded(fn, &ctx, nullptr, &ex);
}

bool ptr_readable_safe(const void* p, size_t len) noexcept {
    if (!p || len == 0) return false;
#if defined(_WIN32)
    MEMORY_BASIC_INFORMATION mbi{};
    if (VirtualQuery(p, &mbi, sizeof(mbi)) == 0) return false;
    if (mbi.State != MEM_COMMIT) return false;
    const DWORD prot = mbi.Protect & 0xFFu;
    if (prot == PAGE_NOACCESS || (mbi.Protect & PAGE_GUARD) != 0) return false;
    const uintptr_t start = reinterpret_cast<uintptr_t>(p);
    const uintptr_t end = reinterpret_cast<uintptr_t>(mbi.BaseAddress) + mbi.RegionSize;
    return (start + len) <= end;
#else
    return true;
#endif
}

template <typename T>
bool read_mem(const void* base, size_t offset, T& out) noexcept {
    if (!base) return false;
    const auto* addr = static_cast<const uint8_t*>(base) + offset;
    if (!ptr_readable_safe(addr, sizeof(T))) return false;
    std::memcpy(&out, addr, sizeof(T));
    return true;
}

std::string wide_chars_to_utf8(const wchar_t* ptr, size_t len) {
    if (!ptr || len == 0 || len > 8192) return {};
#if defined(_WIN32)
    const int needed = WideCharToMultiByte(CP_UTF8, 0, ptr, static_cast<int>(len), nullptr, 0, nullptr, nullptr);
    if (needed <= 0) return {};
    std::string out(static_cast<size_t>(needed), '\0');
    WideCharToMultiByte(CP_UTF8, 0, ptr, static_cast<int>(len), out.data(), needed, nullptr, nullptr);
    return out;
#else
    std::string out;
    out.reserve(len);
    for (size_t i = 0; i < len; ++i) {
        const uint32_t cp = static_cast<uint32_t>(ptr[i]);
        if (cp < 0x80u) {
            out.push_back(static_cast<char>(cp));
        } else if (cp < 0x800u) {
            out.push_back(static_cast<char>(0xC0u | (cp >> 6)));
            out.push_back(static_cast<char>(0x80u | (cp & 0x3Fu)));
        } else {
            out.push_back(static_cast<char>(0xE0u | ((cp >> 12) & 0x0Fu)));
            out.push_back(static_cast<char>(0x80u | ((cp >> 6) & 0x3Fu)));
            out.push_back(static_cast<char>(0x80u | (cp & 0x3Fu)));
        }
    }
    return out;
#endif
}

std::string read_c_wstr_safe(const wchar_t* ptr) {
    if (!ptr || !ptr_readable_safe(ptr, sizeof(wchar_t))) return {};
    size_t len = 0;
    while (len < 512u && ptr_readable_safe(ptr + len, sizeof(wchar_t)) && ptr[len] != L'\0') {
        ++len;
    }
    return wide_chars_to_utf8(ptr, len);
}

uint32_t pack_rgba_u32(const uint8_t rgba[4]) noexcept {
    return (static_cast<uint32_t>(rgba[0]) << 24) |
           (static_cast<uint32_t>(rgba[1]) << 16) |
           (static_cast<uint32_t>(rgba[2]) << 8)  |
           static_cast<uint32_t>(rgba[3]);
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
static_assert(sizeof(MsvcWString) == 32u, "MSVC std::wstring must be 32 bytes");

struct alignas(8) RawItemFilterStyle {
    uint8_t bytes[0x40];
};
static_assert(sizeof(RawItemFilterStyle) == 0x40u, "ItemFilterStyle must be 0x40 bytes");

struct HighlightState {
    uint32_t local_player_alloc_id;
    uint8_t  always_highlight;
    uint8_t  highlight_key_held;
    uint8_t  key_pickup_enabled;
    uint8_t  key_pickup_held;
    uint32_t pad;
};
static_assert(sizeof(HighlightState) == 12u, "HighlightState must be 12 bytes");

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

std::wstring utf8_to_wide_p2(const char* s) {
    if (!s || s[0] == '\0') return {};
    const int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, nullptr, 0);
    if (n <= 1) return {};
    std::wstring w(static_cast<size_t>(n - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s, -1, w.data(), n);
    return w;
}

std::string read_msvc_ws_safe(const void* addr) {
    if (!ptr_readable_safe(addr, sizeof(MsvcWString))) return {};
    const auto* ws = static_cast<const MsvcWString*>(addr);
    if (ws->size == 0 || ws->size > 4096) return {};
    const wchar_t* chars = (ws->cap >= 8) ? ws->ptr : ws->buf;
    if (!chars || !ptr_readable_safe(chars, static_cast<size_t>(ws->size) * sizeof(wchar_t))) return {};
    return wide_chars_to_utf8(chars, static_cast<size_t>(ws->size));
}

void destroy_msvc_ws_unguarded(nm_build_id_t bid, uint64_t base, MsvcWString& ws) {
    const uint64_t dtor = sym_addr(bid, base, "runtime.wstring_destroy");
    if (dtor != 0) {
        using DtorFn = void(NM_CALL*)(void*);
        reinterpret_cast<DtorFn>(static_cast<uintptr_t>(dtor))(&ws);
    }
}

struct LiveInGamePointers {
    void* ingame_state = nullptr;
    void* client_world = nullptr;
    void* instance_client = nullptr;
    void* ingame_ui = nullptr;
    void* camera_ctrl = nullptr;
};

LiveInGamePointers resolve_ingame_pointers_unguarded(nm_build_id_t bid, uint64_t base) {
    LiveInGamePointers out{};

    const uint64_t ss_global = sym_addr(bid, base, "frame.state_stack_global");
    if (ss_global != 0) {
        void* ss = nullptr;
        if (read_mem(reinterpret_cast<const void*>(static_cast<uintptr_t>(ss_global)), 0, ss) && ss) {
            const uint64_t vt_ingame = sym_addr(bid, base, "frame.vtable_ingame_state");
            const size_t begin_off = off_or(bid, "frame.state_stack.active_begin", 0x08u);
            const size_t end_off   = off_or(bid, "frame.state_stack.active_end", 0x10u);
            const size_t sid_off   = off_or(bid, "frame.state.state_id", 0x209u);

            uint8_t* vec_begin = nullptr;
            uint8_t* vec_end = nullptr;
            if (read_mem(ss, begin_off, vec_begin) && read_mem(ss, end_off, vec_end) &&
                vec_begin && vec_end && vec_end >= vec_begin) {
                const size_t count = static_cast<size_t>(vec_end - vec_begin) / 0x10u;
                if (count > 0 && count <= 64 && ptr_readable_safe(vec_begin, count * 0x10u)) {
                    for (size_t i = count; i > 0; --i) {
                        void* st = nullptr;
                        if (!read_mem(vec_begin + (i - 1) * 0x10u, 0, st) || !st) continue;
                        uint64_t vt = 0;
                        uint8_t sid = 0xFFu;
                        (void)read_mem(st, 0, vt);
                        (void)read_mem(st, sid_off, sid);
                        if ((vt_ingame != 0 && vt == vt_ingame) || sid == 4u) {
                            out.ingame_state = st;
                            break;
                        }
                    }
                }
            }
        }
    }

    if (out.ingame_state) {
        (void)read_mem(out.ingame_state, off_or(bid, "frame.ingame_state.world", 0x218u), out.client_world);
        (void)read_mem(out.ingame_state, off_or(bid, "frame.ingame_state.ingame_ui", 0x978u), out.ingame_ui);
        (void)read_mem(out.ingame_state, off_or(bid, "frame.ingame_state.camera_ctrl", 0x270u), out.camera_ctrl);
    }

    if (out.client_world) {
        (void)read_mem(out.client_world, off_or(bid, "world.client_world.instance_client", 0x968u), out.instance_client);
    }

    if (!out.instance_client) {
        const uint64_t fn_ic = sym_addr(bid, base, "world.get_instance_client");
        if (fn_ic != 0) {
            using GetIcFn = void*(NM_CALL*)();
            out.instance_client = reinterpret_cast<GetIcFn>(static_cast<uintptr_t>(fn_ic))();
        }
    }

    if (!out.client_world && out.instance_client) {
        (void)read_mem(out.instance_client, off_or(bid, "network.instance_client.world", 0xC278u), out.client_world);
    }

    return out;
}

void* resolve_entity_by_id_unguarded(nm_build_id_t bid, void* client_world, uint32_t entity_id) {
    if (!client_world) return nullptr;
    if (entity_id == 0u) {
        void* lp = nullptr;
        (void)read_mem(client_world, off_or(bid, "world.client_world.local_player", 0x970u), lp);
        return lp;
    }
    const size_t em_off = off_or(bid, "world.client_world.entity_manager", 0xA18u);
    const auto* em = static_cast<const uint8_t*>(client_world) + em_off;
    const size_t awake_off  = off_or(bid, "world.entity_mgr.awake_map", 0x10u);
    const size_t asleep_off = off_or(bid, "world.entity_mgr.asleep_map", 0x20u);

    void* awake_head = nullptr;
    if (read_mem(em, awake_off, awake_head) && awake_head) {
        if (void* ent = Phase2Services::find_entity_in_rb_tree(awake_head, entity_id)) {
            return ent;
        }
    }
    void* asleep_head = nullptr;
    if (read_mem(em, asleep_off, asleep_head) && asleep_head) {
        if (void* ent = Phase2Services::find_entity_in_rb_tree(asleep_head, entity_id)) {
            return ent;
        }
    }
    return nullptr;
}

void* call_get_component_unguarded(nm_build_id_t bid, uint64_t base, const char* sym_name, void* entity) {
    if (!entity) return nullptr;
    const uint64_t fn_addr = sym_addr(bid, base, sym_name);
    if (fn_addr == 0) return nullptr;
    using GetCompFn = void*(NM_CALL*)(void*);
    return reinterpret_cast<GetCompFn>(static_cast<uintptr_t>(fn_addr))(entity);
}

void populate_entity_snapshot_unguarded(
    nm_build_id_t               bid,
    uint64_t                    base,
    void*                       entity,
    void*                       local_player,
    bool                        include_world_item,
    nm_world_entity_snapshot_t& out) {
    std::memset(&out, 0, sizeof(out));
    out.struct_size = sizeof(out);
    out.world_item.grid_x = -1;
    out.world_item.grid_y = -1;
    if (!entity || !ptr_readable_safe(entity, 0xA0)) return;

    out.entity_ptr = entity;
    (void)read_mem(entity, off_or(bid, "world.entity.id", 0x88u), out.entity_id);

    // Metadata path from ObjectTypeSlot (+0x08) -> std::wstring at +0x08
    void* ot_slot = nullptr;
    if (read_mem(entity, off_or(bid, "world.entity.object_type_slot", 0x08u), ot_slot) && ot_slot) {
        const std::string meta = read_msvc_ws_safe(static_cast<const uint8_t*>(ot_slot) + 0x08u);
        copy_utf8_fixed(out.metadata_path, sizeof(out.metadata_path), meta);
    }

    // Positioned component (cached fast path at Entity+0x98, fallback components.get_positioned)
    void* pos = nullptr;
    (void)read_mem(entity, off_or(bid, "world.entity.positioned", 0x98u), pos);
    if (!pos) {
        pos = call_get_component_unguarded(bid, base, "components.get_positioned", entity);
    }
    if (pos && ptr_readable_safe(pos, 0x2C0u)) {
        (void)read_mem(pos, off_or(bid, "components.positioned.grid_x", 0x294u), out.grid_x);
        (void)read_mem(pos, off_or(bid, "components.positioned.grid_y", 0x298u), out.grid_y);
        (void)read_mem(pos, off_or(bid, "components.positioned.world_x", 0x2B8u), out.world_x);
        (void)read_mem(pos, off_or(bid, "components.positioned.world_y", 0x2BCu), out.world_y);
        int32_t grid_z = 0x7FFFFFFF;
        (void)read_mem(pos, off_or(bid, "components.positioned.grid_z", 0x290u), grid_z);
        if (grid_z != 0x7FFFFFFF) {
            (void)read_mem(pos, off_or(bid, "components.positioned.world_z", 0x2B4u), out.world_z);
        } else {
            const uint64_t fn_z = sym_addr(bid, base, "world.positioned_get_world_z");
            if (fn_z != 0) {
                using GetZFn = float(NM_CALL*)(void*);
                out.world_z = reinterpret_cast<GetZFn>(static_cast<uintptr_t>(fn_z))(pos);
            } else {
                (void)read_mem(pos, off_or(bid, "components.positioned.world_z", 0x2B4u), out.world_z);
            }
        }
    }

    uint32_t kinds = NM_ENTITY_KIND_NONE;
    if (entity == local_player && local_player != nullptr) {
        kinds |= NM_ENTITY_KIND_LOCAL_PLAYER | NM_ENTITY_KIND_PLAYER;
    }

    if (call_get_component_unguarded(bid, base, "components.get_player", entity) != nullptr) {
        kinds |= NM_ENTITY_KIND_PLAYER;
    }
    if (void* wi = call_get_component_unguarded(bid, base, "components.get_world_item", entity)) {
        kinds |= NM_ENTITY_KIND_WORLD_ITEM;
        const uint64_t fn_vis = sym_addr(bid, base, "items.world_item_is_visible");
        if (fn_vis != 0) {
            using IsVisFn = uint8_t(NM_CALL*)(void*);
            out.filter_visible = reinterpret_cast<IsVisFn>(static_cast<uintptr_t>(fn_vis))(wi) ? 1u : 0u;
        } else {
            uint8_t style_valid = 0;
            uint8_t flags0 = 0;
            if (read_mem(wi, off_or(bid, "items.world_item.filter_style_valid", 0xB0u), style_valid) && style_valid != 0 &&
                read_mem(wi, off_or(bid, "items.world_item.filter_style", 0x70u), flags0)) {
                out.filter_visible = ((flags0 & 0x01u) == 0u) ? 1u : 0u;
            } else {
                out.filter_visible = 1u;
            }
        }
        if (include_world_item) {
            void* inner_item = nullptr;
            if (read_mem(wi, off_or(bid, "items.world_item.item_entity", 0x28u), inner_item) && inner_item) {
                Phase1Services::populate_item_snapshot_unguarded(bid, base, inner_item, out.world_item);
            }
        }
    }
    if (void* chest = call_get_component_unguarded(bid, base, "components.get_chest", entity)) {
        kinds |= NM_ENTITY_KIND_CHEST;
        uint8_t opened = 0;
        if (read_mem(chest, off_or(bid, "components.chest.is_opened", 0x168u), opened) && opened != 0) {
            out.is_opened = 1u;
            kinds |= NM_ENTITY_KIND_OPENED;
        }
    }
    if (call_get_component_unguarded(bid, base, "components.get_npc", entity) != nullptr) {
        kinds |= NM_ENTITY_KIND_NPC;
    }
    if (call_get_component_unguarded(bid, base, "components.get_portal", entity) != nullptr) {
        kinds |= NM_ENTITY_KIND_PORTAL;
    }
    if (call_get_component_unguarded(bid, base, "components.get_area_transition", entity) != nullptr) {
        kinds |= NM_ENTITY_KIND_AREA_TRANSITION;
    }
    if (call_get_component_unguarded(bid, base, "components.get_minimap_icon", entity) != nullptr) {
        kinds |= NM_ENTITY_KIND_MINIMAP_ICON;
    }
    if (void* targ = call_get_component_unguarded(bid, base, "components.get_targetable", entity)) {
        uint8_t is_t = 0;
        uint8_t hidden = 0;
        (void)read_mem(targ, off_or(bid, "components.targetable.is_targetable", 0x50u), is_t);
        (void)read_mem(targ, off_or(bid, "components.targetable.hidden_from_player", 0x5Au), hidden);
        if (is_t != 0 && hidden == 0) {
            out.is_targetable = 1u;
            kinds |= NM_ENTITY_KIND_TARGETABLE;
        }
    }
    if (void* life = call_get_component_unguarded(bid, base, "components.get_life", entity)) {
        (void)read_mem(life, off_or(bid, "components.life.max_hp", 0x1A4u), out.max_hp);
        (void)read_mem(life, off_or(bid, "components.life.cur_hp", 0x1A8u), out.hp);
        if (out.hp > 0) {
            out.is_alive = 1u;
            kinds |= NM_ENTITY_KIND_ALIVE;
        }
        const bool non_monster_kind =
            (kinds & (NM_ENTITY_KIND_PLAYER | NM_ENTITY_KIND_NPC | NM_ENTITY_KIND_WORLD_ITEM |
                      NM_ENTITY_KIND_CHEST | NM_ENTITY_KIND_PORTAL | NM_ENTITY_KIND_AREA_TRANSITION)) != 0u;
        if (!non_monster_kind) {
            kinds |= NM_ENTITY_KIND_MONSTER;
        }
    } else if (std::strstr(out.metadata_path, "Metadata/Monsters/") != nullptr) {
        kinds |= NM_ENTITY_KIND_MONSTER;
    }
    if (void* omp = call_get_component_unguarded(bid, base, "components.get_magic_properties", entity)) {
        int32_t rar = 0;
        if (read_mem(omp, off_or(bid, "components.magic_props.rarity", 0x144u), rar) && rar >= 0) {
            out.rarity = static_cast<uint32_t>(rar);
        }
    }

    out.kind_flags = kinds;
}

void* resolve_options_singleton_unguarded(nm_build_id_t bid, uint64_t base) {
    const uint64_t fn_opt = sym_addr(bid, base, "items.options_singleton_get");
    if (fn_opt != 0) {
        using OptFn = void*(NM_CALL*)();
        return reinterpret_cast<OptFn>(static_cast<uintptr_t>(fn_opt))();
    }
    const uint64_t g_opt = sym_addr(bid, base, "items.options_singleton_global");
    return (g_opt != 0) ? reinterpret_cast<void*>(static_cast<uintptr_t>(g_opt)) : nullptr;
}

void populate_filter_state_unguarded(nm_build_id_t bid, uint64_t base, nm_item_filter_state_t& out) {
    std::memset(&out, 0, sizeof(out));
    out.struct_size = sizeof(out);

    void* opt = resolve_options_singleton_unguarded(bid, base);
    if (!opt || !ptr_readable_safe(opt, 0x560u)) return;

    uint32_t filter_kind = 0;
    void* filter_ptr = nullptr;
    uint8_t filter_valid = 0;
    (void)read_mem(opt, off_or(bid, "items.options.filter_kind", 0x510u), filter_kind);
    (void)read_mem(opt, off_or(bid, "items.options.active_filter_ptr", 0x518u), filter_ptr);
    (void)read_mem(opt, off_or(bid, "items.options.filter_valid", 0x545u), filter_valid);

    out.filter_kind = filter_kind;
    out.loaded = (filter_valid != 0 && filter_kind != 0 && filter_ptr != nullptr) ? 1u : 0u;

    if (out.loaded && ptr_readable_safe(filter_ptr, 0x40u)) {
        uint8_t* r_begin = nullptr;
        uint8_t* r_end = nullptr;
        if (read_mem(filter_ptr, off_or(bid, "items.filter.rules_begin", 0x08u), r_begin) &&
            read_mem(filter_ptr, off_or(bid, "items.filter.rules_end", 0x10u), r_end) &&
            r_begin && r_end && r_end >= r_begin) {
            out.rule_count = static_cast<uint32_t>(static_cast<size_t>(r_end - r_begin) / 0x58u);
        }
        const std::string src = read_msvc_ws_safe(
            static_cast<const uint8_t*>(filter_ptr) + off_or(bid, "items.filter.source_name", 0x20u));
        copy_utf8_fixed(out.filter_path, sizeof(out.filter_path), src);
    }

    const uint64_t fn_hide = sym_addr(bid, base, "items.filter_get_hide_filtered");
    if (fn_hide != 0) {
        using HideFn = uint8_t(NM_CALL*)(void*);
        out.hide_filtered_ground_items = reinterpret_cast<HideFn>(static_cast<uintptr_t>(fn_hide))(opt) ? 1u : 0u;
    }

    const uint64_t fn_folder = sym_addr(bid, base, "items.filter_get_folder");
    if (fn_folder != 0) {
        MsvcWString ws{};
        using FolderFn = MsvcWString*(NM_CALL*)(MsvcWString*);
        reinterpret_cast<FolderFn>(static_cast<uintptr_t>(fn_folder))(&ws);
        copy_utf8_fixed(out.filter_folder, sizeof(out.filter_folder), read_msvc_ws_safe(&ws));
        destroy_msvc_ws_unguarded(bid, base, ws);
    }

    const uint64_t fn_name = sym_addr(bid, base, "items.filter_get_setting_name");
    if (fn_name != 0) {
        MsvcWString ws{};
        using NameFn = MsvcWString*(NM_CALL*)(void*, MsvcWString*, uint8_t);
        reinterpret_cast<NameFn>(static_cast<uintptr_t>(fn_name))(opt, &ws, (filter_kind == 2u) ? 1u : 0u);
        copy_utf8_fixed(out.filter_name, sizeof(out.filter_name), read_msvc_ws_safe(&ws));
        destroy_msvc_ws_unguarded(bid, base, ws);
    }

    if (out.filter_path[0] == '\0' && out.filter_folder[0] != '\0' && out.filter_name[0] != '\0') {
        const std::string joined = std::string(out.filter_folder) + "\\" + std::string(out.filter_name);
        copy_utf8_fixed(out.filter_path, sizeof(out.filter_path), joined);
    }

    // Query Alt-highlight state via WorldLabelManager::GetHighlightState
    LiveInGamePointers ig = resolve_ingame_pointers_unguarded(bid, base);
    if (ig.ingame_ui) {
        void* wlm = nullptr;
        if (read_mem(ig.ingame_ui, off_or(bid, "ui.ingame_ui.world_label_mgr", 0x6E8u), wlm) && wlm) {
            const uint64_t fn_hl = sym_addr(bid, base, "ui.world_label_mgr_highlight");
            if (fn_hl != 0) {
                HighlightState hs{};
                using HlFn = HighlightState*(NM_CALL*)(void*, HighlightState*);
                reinterpret_cast<HlFn>(static_cast<uintptr_t>(fn_hl))(wlm, &hs);
                out.alt_highlight_active = (hs.highlight_key_held != 0 || hs.always_highlight != 0) ? 1u : 0u;
            }
        }
    }
}

bool find_ground_label_rect_unguarded(
    nm_build_id_t bid,
    uint64_t      base,
    void*         ingame_ui,
    void*         target_entity,
    float&        out_x,
    float&        out_y,
    float&        out_w,
    float&        out_h) {
    if (!ingame_ui || !target_entity) return false;
    void* wlm = nullptr;
    if (!read_mem(ingame_ui, off_or(bid, "ui.ingame_ui.world_label_mgr", 0x6E8u), wlm) || !wlm) return false;

    const size_t map_off = off_or(bid, "ui.world_label_mgr.entity_to_label_map", 0x520u);
    void* sentinel = nullptr;
    uint64_t map_size = 0;
    if (!read_mem(wlm, map_off + 0x08u, sentinel) || !sentinel) return false;
    if (!read_mem(wlm, map_off + 0x10u, map_size) || map_size == 0 || map_size > 8192u) return false;

    // Doubly-linked list node: { +0x00 next, +0x08 prev, +0x10 key Entity*, +0x18 val Control* }
    void* cur = nullptr;
    if (!read_mem(sentinel, 0x00u, cur) || !cur) return false;
    for (uint64_t step = 0; step < map_size && cur && cur != sentinel; ++step) {
        void* ent = nullptr;
        void* label = nullptr;
        if (!read_mem(cur, 0x10u, ent) || !read_mem(cur, 0x18u, label)) break;
        if (ent == target_entity && label && ptr_readable_safe(label, 0x270u)) {
            float pos[2] = {0.0f, 0.0f};
            const uint64_t fn_pos = sym_addr(bid, base, "ui.control_get_abs_pos");
            if (fn_pos != 0) {
                using AbsPosFn = void*(NM_CALL*)(void*, float*);
                reinterpret_cast<AbsPosFn>(static_cast<uintptr_t>(fn_pos))(label, pos);
            }
            float w = 0.0f;
            float h = 0.0f;
            (void)read_mem(label, 0x268u, w);
            (void)read_mem(label, 0x26Cu, h);
            out_x = pos[0];
            out_y = pos[1];
            out_w = w;
            out_h = h;
            return true;
        }
        void* next = nullptr;
        if (!read_mem(cur, 0x00u, next)) break;
        cur = next;
    }
    return false;
}

// --- Live HookEngine Chain Callbacks ---

void NM_CALL hook_client_world_init_area_post(nm_hook_context_t* /*ctx*/, void* /*ud*/) {
    g_area_enter_pending.store(true, std::memory_order_release);
}

void NM_CALL hook_area_loading_leave_post(nm_hook_context_t* /*ctx*/, void* /*ud*/) {
    g_area_enter_pending.store(false, std::memory_order_release);
    if (!g_has_area_cb.load(std::memory_order_acquire)) return;

    const nm_build_id_t bid = g_p2_build_id.load(std::memory_order_acquire);
    const uint64_t base = g_p2_image_base.load(std::memory_order_acquire);
    if (bid == NM_BUILD_UNKNOWN || bid == NM_BUILD_SYNTHETIC || base == 0) return;

    struct Ctx {
        nm_build_id_t  bid;
        uint64_t       base;
        nm_area_info_t info{};
    } c{bid, base, {}};
    c.info.struct_size = sizeof(c.info);

    auto fn = [](void* raw) -> int64_t {
        auto* p = static_cast<Ctx*>(raw);
        LiveInGamePointers ig = resolve_ingame_pointers_unguarded(p->bid, p->base);
        (void)Phase2Services::read_area_info_from_structs(ig.client_world, ig.instance_client, p->info);
        return 0;
    };
    nm_exception_info_t ex{};
    if (invoke_guarded(fn, &c, nullptr, &ex) == NM_OK && c.info.in_game != 0) {
        fire_area_event_guarded(NM_AREA_EVT_ENTERED, &c.info);
    }
}

void NM_CALL hook_ingame_state_leave_pre_area(nm_hook_context_t* ctx, void* /*ud*/) {
    g_area_enter_pending.store(false, std::memory_order_release);
    if (!ctx || !g_has_area_cb.load(std::memory_order_acquire)) return;

    const nm_build_id_t bid = g_p2_build_id.load(std::memory_order_acquire);
    const uint64_t base = g_p2_image_base.load(std::memory_order_acquire);
    if (bid == NM_BUILD_UNKNOWN || bid == NM_BUILD_SYNTHETIC || base == 0) return;

    struct Ctx {
        nm_build_id_t  bid;
        uint64_t       base;
        void*          igs;
        nm_area_info_t info{};
    } c{bid, base, reinterpret_cast<void*>(static_cast<uintptr_t>(ctx->args[0])), {}};
    c.info.struct_size = sizeof(c.info);

    auto fn = [](void* raw) -> int64_t {
        auto* p = static_cast<Ctx*>(raw);
        void* world = nullptr;
        void* ic = nullptr;
        if (p->igs) {
            (void)read_mem(p->igs, off_or(p->bid, "frame.ingame_state.world", 0x218u), world);
        }
        if (world) {
            (void)read_mem(world, off_or(p->bid, "world.client_world.instance_client", 0x968u), ic);
        }
        (void)Phase2Services::read_area_info_from_structs(world, ic, p->info);
        return 0;
    };
    nm_exception_info_t ex{};
    if (invoke_guarded(fn, &c, nullptr, &ex) == NM_OK) {
        fire_area_event_guarded(NM_AREA_EVT_LEFT, &c.info);
    }
}

void NM_CALL hook_filter_notify_loaded_post(nm_hook_context_t* ctx, void* /*ud*/) {
    if (!ctx || !g_has_filter_reload_cb.load(std::memory_order_acquire)) return;

    const nm_build_id_t bid = g_p2_build_id.load(std::memory_order_acquire);
    const uint64_t base = g_p2_image_base.load(std::memory_order_acquire);
    if (bid == NM_BUILD_UNKNOWN || bid == NM_BUILD_SYNTHETIC || base == 0) return;

    const int32_t ok = ((ctx->args[3] & 0xFFu) != 0u) ? 1 : 0;

    struct Ctx {
        nm_build_id_t          bid;
        uint64_t               base;
        nm_item_filter_state_t state{};
    } c{bid, base, {}};
    c.state.struct_size = sizeof(c.state);

    auto fn = [](void* raw) -> int64_t {
        auto* p = static_cast<Ctx*>(raw);
        populate_filter_state_unguarded(p->bid, p->base, p->state);
        return 0;
    };
    nm_exception_info_t ex{};
    (void)invoke_guarded(fn, &c, nullptr, &ex);
    fire_filter_reloaded_guarded(ok, &c.state);
}

#endif // _WIN32

} // namespace

Phase2Services::~Phase2Services() = default;

void Phase2Services::install_live_hooks(
    nm_build_id_t  build_id,
    uint64_t       image_base,
    HookEngine&    hooks,
    GameAllocator& allocator) {
    build_id_ = build_id;
    image_base_ = image_base;
    allocator_ = &allocator;
    g_p2_build_id.store(build_id, std::memory_order_release);
    g_p2_image_base.store(image_base, std::memory_order_release);

#if defined(_WIN32)
    if (build_id == NM_BUILD_SYNTHETIC || build_id == NM_BUILD_UNKNOWN || image_base == 0) {
        return;
    }

    auto arm_chain = [&](const char* sym_name, nm_hook_phase_t phase, nm_hook_chain_callback_t cb) -> bool {
        const EmbeddedSymbol* sym = find_symbol(build_id, sym_name);
        if (!sym || sym->rva == 0 || sym->status == NM_SYM_STATUS_UNVERIFIED) return false;
        const uint64_t addr = image_base + sym->rva;
        if (!ptr_readable_safe(reinterpret_cast<const void*>(static_cast<uintptr_t>(addr)), 24)) return false;

        nm_hook_handle_t handle = NM_HOOK_INVALID;
        const nm_status_t st = hooks.install_inline_chain(addr, sym->expected_bytes_hex, phase, 0, cb, nullptr, &handle);
        if (st == NM_OK && handle != NM_HOOK_INVALID) {
            installed_handles_.push_back(handle);
            return true;
        }
        logger_logf("WARN", "[PHASE2] Failed to arm chain hook '%s' at 0x%llX (st=%d)",
                    sym_name, static_cast<unsigned long long>(addr), static_cast<int>(st));
        return false;
    };

    const bool ok_init_area = arm_chain("world.client_world_init_area", NM_HOOK_PHASE_POST, &hook_client_world_init_area_post);
    const bool ok_load_lv   = arm_chain("frame.area_loading_leave",     NM_HOOK_PHASE_POST, &hook_area_loading_leave_post);
    const bool ok_ig_leave  = arm_chain("frame.ingame_state_leave",     NM_HOOK_PHASE_PRE,  &hook_ingame_state_leave_pre_area);
    const bool ok_flt_load  = arm_chain("items.filter_notify_loaded",   NM_HOOK_PHASE_POST, &hook_filter_notify_loaded_post);

    logger_logf("INFO",
                "[PHASE2] Armed live hooks (init_area=%s area_load_leave=%s ig_leave=%s filter_loaded=%s)",
                ok_init_area ? "OK" : "SKIP",
                ok_load_lv   ? "OK" : "SKIP",
                ok_ig_leave  ? "OK" : "SKIP",
                ok_flt_load  ? "OK" : "SKIP");
#else
    (void)hooks;
#endif
}

void Phase2Services::reset(HookEngine* hooks) {
    // Restore stock camera zoom override (-1.0f) if modified during this session.
    if (zoom_override_modified_) {
        (void)camera_set_zoom_override(-1.0f);
        zoom_override_modified_ = false;
    }

    if (hooks) {
        for (nm_hook_handle_t h : installed_handles_) {
            (void)hooks->remove(h);
        }
    }
    installed_handles_.clear();
    build_id_ = NM_BUILD_UNKNOWN;
    image_base_ = 0;
    allocator_ = nullptr;
    g_p2_build_id.store(NM_BUILD_UNKNOWN, std::memory_order_release);
    g_p2_image_base.store(0, std::memory_order_release);
    g_area_enter_pending.store(false, std::memory_order_release);

    {
        std::lock_guard<std::mutex> lock(g_p2_cb_mutex);
        g_area_cb = nullptr;
        g_area_ud = nullptr;
        g_filter_reload_cb = nullptr;
        g_filter_reload_ud = nullptr;
    }
    g_has_area_cb.store(false, std::memory_order_release);
    g_has_filter_reload_cb.store(false, std::memory_order_release);
}

// --- World (0.4.0) ---

nm_status_t Phase2Services::world_get_local_player(nm_world_entity_snapshot_t* out_entity) const {
    if (!out_entity) return NM_ERR_INVALID_ARG;
    std::memset(out_entity, 0, sizeof(*out_entity));
    out_entity->struct_size = sizeof(*out_entity);
    out_entity->world_item.grid_x = -1;
    out_entity->world_item.grid_y = -1;

#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;

    struct Ctx {
        nm_build_id_t               bid;
        uint64_t                    base;
        nm_world_entity_snapshot_t* out;
    } ctx{build_id_, image_base_, out_entity};

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        LiveInGamePointers ig = resolve_ingame_pointers_unguarded(c->bid, c->base);
        if (!ig.client_world) return -1;
        void* lp = resolve_entity_by_id_unguarded(c->bid, ig.client_world, 0u);
        if (!lp) return -1;
        populate_entity_snapshot_unguarded(c->bid, c->base, lp, lp, false, *c->out);
        return 0;
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK) return st;
    return (rc == 0) ? NM_OK : NM_ERR_NOT_INITIALIZED;
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase2Services::world_get_entity(uint32_t entity_id, nm_world_entity_snapshot_t* out_entity) const {
    if (!out_entity) return NM_ERR_INVALID_ARG;
    std::memset(out_entity, 0, sizeof(*out_entity));
    out_entity->struct_size = sizeof(*out_entity);
    out_entity->world_item.grid_x = -1;
    out_entity->world_item.grid_y = -1;

#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;

    struct Ctx {
        nm_build_id_t               bid;
        uint64_t                    base;
        uint32_t                    id;
        nm_world_entity_snapshot_t* out;
    } ctx{build_id_, image_base_, entity_id, out_entity};

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        LiveInGamePointers ig = resolve_ingame_pointers_unguarded(c->bid, c->base);
        if (!ig.client_world) return -1;
        void* lp = resolve_entity_by_id_unguarded(c->bid, ig.client_world, 0u);
        void* ent = resolve_entity_by_id_unguarded(c->bid, ig.client_world, c->id);
        if (!ent) return -2;
        uint8_t flags = 0;
        if (read_mem(ent, off_or(c->bid, "world.entity.flags", 0x8Cu), flags) && (flags & 0x03u) != 0u) {
            return -2;
        }
        populate_entity_snapshot_unguarded(c->bid, c->base, ent, lp, true, *c->out);
        return 0;
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK) return st;
    if (rc == -1) return NM_ERR_NOT_INITIALIZED;
    if (rc == -2) return NM_ERR_SYMBOL_NOT_FOUND;
    return NM_OK;
#else
    (void)entity_id;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase2Services::world_list_entities(
    const nm_world_entity_query_t* query,
    nm_world_entity_snapshot_t*    out_buf,
    uint32_t                       capacity,
    uint32_t*                      out_count,
    uint32_t*                      out_total) const {
    if (!out_count || !out_total) return NM_ERR_INVALID_ARG;
    if (capacity > 0 && !out_buf) return NM_ERR_INVALID_ARG;
    *out_count = 0;
    *out_total = 0;

#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;

    nm_world_entity_query_t q{};
    if (query) {
        q = *query;
        q.metadata_prefix[sizeof(q.metadata_prefix) - 1] = '\0';
    } else {
        q.struct_size = sizeof(q);
        q.include_world_item = 1;
    }

    struct Ctx {
        nm_build_id_t               bid;
        uint64_t                    base;
        nm_world_entity_query_t     q;
        nm_world_entity_snapshot_t* out_buf;
        uint32_t                    capacity;
        uint32_t                    count = 0;
        uint32_t                    total = 0;
    } ctx{build_id_, image_base_, q, out_buf, capacity, 0, 0};

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        LiveInGamePointers ig = resolve_ingame_pointers_unguarded(c->bid, c->base);
        if (!ig.client_world) return -1;

        void* lp = resolve_entity_by_id_unguarded(c->bid, ig.client_world, 0u);
        float ref_x = c->q.origin_x;
        float ref_y = c->q.origin_y;
        bool has_origin = (c->q.use_origin != 0);
        if (!has_origin && c->q.max_distance > 0.0f && lp) {
            void* lp_pos = nullptr;
            (void)read_mem(lp, off_or(c->bid, "world.entity.positioned", 0x98u), lp_pos);
            if (lp_pos) {
                (void)read_mem(lp_pos, off_or(c->bid, "components.positioned.world_x", 0x2B8u), ref_x);
                (void)read_mem(lp_pos, off_or(c->bid, "components.positioned.world_y", 0x2BCu), ref_y);
                has_origin = true;
            }
        }
        const float max_dist_sq = (c->q.max_distance > 0.0f) ? (c->q.max_distance * c->q.max_distance) : 0.0f;
        const size_t prefix_len = std::strlen(c->q.metadata_prefix);

        const size_t em_off     = off_or(c->bid, "world.client_world.entity_manager", 0xA18u);
        const auto* em          = static_cast<const uint8_t*>(ig.client_world) + em_off;
        const size_t awake_off  = off_or(c->bid, "world.entity_mgr.awake_map", 0x10u);
        const size_t asleep_off = off_or(c->bid, "world.entity_mgr.asleep_map", 0x20u);

        std::vector<void*> raw_entities;
        raw_entities.reserve(256u);
        void* awake_head = nullptr;
        if (read_mem(em, awake_off, awake_head) && awake_head) {
            (void)Phase2Services::traverse_rb_tree_entities(awake_head, raw_entities);
        }
        void* asleep_head = nullptr;
        if (read_mem(em, asleep_off, asleep_head) && asleep_head) {
            (void)Phase2Services::traverse_rb_tree_entities(asleep_head, raw_entities);
        }

        const size_t flags_off = off_or(c->bid, "world.entity.flags", 0x8Cu);
        std::unordered_set<void*> seen;
        seen.reserve(raw_entities.size());

        const uint32_t limit = (c->q.max_results > 0u && c->q.max_results < c->capacity)
                                   ? c->q.max_results
                                   : c->capacity;

        for (void* ent : raw_entities) {
            if (!ent || !seen.insert(ent).second) continue;
            uint8_t flags = 0;
            if (read_mem(ent, flags_off, flags) && (flags & 0x03u) != 0u) continue;

            nm_world_entity_snapshot_t snap{};
            populate_entity_snapshot_unguarded(
                c->bid, c->base, ent, lp, c->q.include_world_item != 0, snap);

            if ((snap.kind_flags & c->q.required_kinds) != c->q.required_kinds) continue;
            if ((snap.kind_flags & c->q.excluded_kinds) != 0u) continue;
            if (c->q.filter_visible_only != 0 &&
                (snap.kind_flags & NM_ENTITY_KIND_WORLD_ITEM) != 0u &&
                snap.filter_visible == 0) {
                continue;
            }
            if (prefix_len > 0 && std::strncmp(snap.metadata_path, c->q.metadata_prefix, prefix_len) != 0) {
                continue;
            }
            if (max_dist_sq > 0.0f && has_origin) {
                const float dx = snap.world_x - ref_x;
                const float dy = snap.world_y - ref_y;
                if ((dx * dx + dy * dy) > max_dist_sq) continue;
            }

            ++c->total;
            if (c->out_buf && c->count < limit) {
                c->out_buf[c->count++] = snap;
            }
        }
        return 0;
    };

    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK) return st;
    if (rc != 0) return NM_ERR_NOT_INITIALIZED;
    *out_count = ctx.count;
    *out_total = ctx.total;
    return NM_OK;
#else
    (void)query;
    (void)out_buf;
    (void)capacity;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase2Services::world_create_client_object(
    const char* metadata_utf8,
    float       x,
    float       y,
    float       z,
    uint64_t*   out_handle) {
    if (!metadata_utf8 || metadata_utf8[0] == '\0' || !out_handle) return NM_ERR_INVALID_ARG;
    *out_handle = 0;

#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0 || !allocator_) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;

    const std::wstring meta_w = utf8_to_wide_p2(metadata_utf8);
    if (meta_w.empty()) return NM_ERR_INVALID_ARG;

    struct Ctx {
        nm_build_id_t  bid;
        uint64_t       base;
        GameAllocator* alloc;
        const wchar_t* meta_ptr;
        uint64_t       meta_len;
        float          x;
        float          y;
        float          z;
        void*          created = nullptr;
    } ctx{build_id_, image_base_, allocator_, meta_w.c_str(), static_cast<uint64_t>(meta_w.size()), x, y, z, nullptr};

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        LiveInGamePointers ig = resolve_ingame_pointers_unguarded(c->bid, c->base);
        if (!ig.client_world) return -1;

        const uint64_t fn_rm_inst = sym_addr(c->bid, c->base, "world.resource_mgr_instance");
        const uint64_t fn_rm_get  = sym_addr(c->bid, c->base, "world.resource_mgr_get_ot");
        const uint64_t fn_ctor    = sym_addr(c->bid, c->base, "world.create_client_object");
        if (fn_rm_inst == 0 || fn_rm_get == 0 || fn_ctor == 0) return -2;

        using RmInstFn = void*(NM_CALL*)();
        void* rm = reinterpret_cast<RmInstFn>(static_cast<uintptr_t>(fn_rm_inst))();
        if (!rm) return -1;

        WStringView wsv{c->meta_ptr, c->meta_len};
        using RmGetOtFn = void*(NM_CALL*)(void*, const WStringView*, const WStringView*);
        void* ot_slot = reinterpret_cast<RmGetOtFn>(static_cast<uintptr_t>(fn_rm_get))(rm, &wsv, &wsv);
        if (!ot_slot) return -3;

        void* mem = nullptr;
        if (c->alloc->alloc(0x248u, 16u, NM_DEFAULT_ALLOC_TAG, &mem) != NM_OK || !mem) return -4;
        std::memset(mem, 0, 0x248u);

        void* slot_handle = ot_slot;
        using CreateObjFn = void*(NM_CALL*)(void*, void**, void*, uint32_t, uint8_t);
        void* ent = reinterpret_cast<CreateObjFn>(static_cast<uintptr_t>(fn_ctor))(
            mem, &slot_handle, ig.client_world, 0u, 1u);
        if (!ent) {
            (void)c->alloc->free(mem, 16u);
            return -5;
        }

        void* pos = nullptr;
        (void)read_mem(ent, off_or(c->bid, "world.entity.positioned", 0x98u), pos);
        if (!pos) {
            pos = call_get_component_unguarded(c->bid, c->base, "components.get_positioned", ent);
        }
        if (pos && ptr_readable_safe(pos, 0x2C0u)) {
            constexpr float kGridScale = 250.0f / 23.0f;
            const int32_t gx = static_cast<int32_t>(std::lround(c->x / kGridScale));
            const int32_t gy = static_cast<int32_t>(std::lround(c->y / kGridScale));
            std::memcpy(static_cast<uint8_t*>(pos) + off_or(c->bid, "components.positioned.grid_x", 0x294u), &gx, sizeof(gx));
            std::memcpy(static_cast<uint8_t*>(pos) + off_or(c->bid, "components.positioned.grid_y", 0x298u), &gy, sizeof(gy));
            std::memcpy(static_cast<uint8_t*>(pos) + off_or(c->bid, "components.positioned.world_x", 0x2B8u), &c->x, sizeof(c->x));
            std::memcpy(static_cast<uint8_t*>(pos) + off_or(c->bid, "components.positioned.world_y", 0x2BCu), &c->y, sizeof(c->y));
            std::memcpy(static_cast<uint8_t*>(pos) + off_or(c->bid, "components.positioned.world_z", 0x2B4u), &c->z, sizeof(c->z));
        }

        c->created = ent;
        return 0;
    };

    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK) return st;
    if (rc == -1) return NM_ERR_NOT_INITIALIZED;
    if (rc == -2 || rc == -3) return NM_ERR_SYMBOL_NOT_FOUND;
    if (rc == -4) return NM_ERR_ALLOC_FAILED;
    if (rc != 0 || !ctx.created) return NM_ERR_GAME_EXCEPTION;
    *out_handle = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(ctx.created));
    return NM_OK;
#else
    (void)x;
    (void)y;
    (void)z;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase2Services::world_remove_client_object(uint64_t handle) {
    if (handle == 0) return NM_ERR_INVALID_ARG;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;

    struct Ctx {
        nm_build_id_t bid;
        uint64_t      base;
        void*         ent;
    } ctx{build_id_, image_base_, reinterpret_cast<void*>(static_cast<uintptr_t>(handle))};

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        if (!ptr_readable_safe(c->ent, 0x90u)) return -1;
        const uint64_t fn_rm = sym_addr(c->bid, c->base, "world.entity_mark_for_removal");
        if (fn_rm == 0) return -2;
        using MarkRmFn = void(NM_CALL*)(void*);
        reinterpret_cast<MarkRmFn>(static_cast<uintptr_t>(fn_rm))(c->ent);
        return 0;
    };

    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK) return st;
    if (rc == -2) return NM_ERR_SYMBOL_NOT_FOUND;
    return (rc == 0) ? NM_OK : NM_ERR_INVALID_ARG;
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

// --- Stats (0.4.0) ---

nm_status_t Phase2Services::stats_read_entity(uint32_t entity_id, int32_t stat_id, int32_t* out_value) const {
    if (!out_value) return NM_ERR_INVALID_ARG;
    *out_value = 0;

#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;

    struct Ctx {
        nm_build_id_t bid;
        uint64_t      base;
        uint32_t      entity_id;
        int32_t       stat_id;
        int32_t       value = 0;
    } ctx{build_id_, image_base_, entity_id, stat_id, 0};

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        LiveInGamePointers ig = resolve_ingame_pointers_unguarded(c->bid, c->base);
        if (!ig.client_world) return -1;
        void* ent = resolve_entity_by_id_unguarded(c->bid, ig.client_world, c->entity_id);
        if (!ent) return -2;
        void* stats = call_get_component_unguarded(c->bid, c->base, "components.get_stats", ent);
        if (!stats) return -2;
        const uint64_t fn_read = sym_addr(c->bid, c->base, "stats.read");
        if (fn_read == 0) return -3;
        using ReadStatFn = int32_t(NM_CALL*)(void*, uint32_t);
        c->value = reinterpret_cast<ReadStatFn>(static_cast<uintptr_t>(fn_read))(
            stats, static_cast<uint32_t>(c->stat_id));
        return 0;
    };

    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK) return st;
    if (rc == -1) return NM_ERR_NOT_INITIALIZED;
    if (rc == -2 || rc == -3) return NM_ERR_SYMBOL_NOT_FOUND;
    *out_value = ctx.value;
    return NM_OK;
#else
    (void)entity_id;
    (void)stat_id;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase2Services::stats_set_local_entity(uint32_t entity_id, int32_t stat_id, int32_t value) const {
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;

    struct Ctx {
        nm_build_id_t bid;
        uint64_t      base;
        uint32_t      entity_id;
        int32_t       stat_id;
        int32_t       value;
    } ctx{build_id_, image_base_, entity_id, stat_id, value};

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        LiveInGamePointers ig = resolve_ingame_pointers_unguarded(c->bid, c->base);
        if (!ig.client_world) return -1;
        void* ent = resolve_entity_by_id_unguarded(c->bid, ig.client_world, c->entity_id);
        if (!ent) return -2;
        void* stats = call_get_component_unguarded(c->bid, c->base, "components.get_stats", ent);
        if (!stats) return -2;
        const uint64_t fn_set = sym_addr(c->bid, c->base, "stats.set_local");
        if (fn_set == 0) return -3;
        using SetStatFn = void(NM_CALL*)(void*, uint32_t, int32_t);
        reinterpret_cast<SetStatFn>(static_cast<uintptr_t>(fn_set))(
            stats, static_cast<uint32_t>(c->stat_id), c->value);
        return 0;
    };

    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK) return st;
    if (rc == -1) return NM_ERR_NOT_INITIALIZED;
    if (rc == -2 || rc == -3) return NM_ERR_SYMBOL_NOT_FOUND;
    return NM_OK;
#else
    (void)entity_id;
    (void)stat_id;
    (void)value;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase2Services::stats_list_entity(
    uint32_t         entity_id,
    nm_stat_entry_t* out_buf,
    uint32_t         capacity,
    uint32_t*        out_count,
    uint32_t*        out_total) const {
    if (!out_count || !out_total) return NM_ERR_INVALID_ARG;
    if (capacity > 0 && !out_buf) return NM_ERR_INVALID_ARG;
    *out_count = 0;
    *out_total = 0;

#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;

    struct Ctx {
        nm_build_id_t    bid;
        uint64_t         base;
        uint32_t         entity_id;
        nm_stat_entry_t* out_buf;
        uint32_t         capacity;
        uint32_t         count = 0;
        uint32_t         total = 0;
    } ctx{build_id_, image_base_, entity_id, out_buf, capacity, 0, 0};

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        LiveInGamePointers ig = resolve_ingame_pointers_unguarded(c->bid, c->base);
        if (!ig.client_world) return -1;
        void* ent = resolve_entity_by_id_unguarded(c->bid, ig.client_world, c->entity_id);
        if (!ent) return -2;
        void* stats = call_get_component_unguarded(c->bid, c->base, "components.get_stats", ent);
        if (!stats) return -2;
        const size_t begin_off = off_or(c->bid, "stats.comp.entries_begin", 0xF0u);
        const size_t end_off   = off_or(c->bid, "stats.comp.entries_end", 0xF8u);
        const nm_status_t st = Phase2Services::read_stats_vector(
            stats, begin_off, end_off, c->out_buf, c->capacity, &c->count, &c->total);
        return (st == NM_OK) ? 0 : -3;
    };

    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK) return st;
    if (rc == -1) return NM_ERR_NOT_INITIALIZED;
    if (rc == -2) return NM_ERR_SYMBOL_NOT_FOUND;
    if (rc != 0) return NM_ERR_GAME_EXCEPTION;
    *out_count = ctx.count;
    *out_total = ctx.total;
    return NM_OK;
#else
    (void)entity_id;
    (void)out_buf;
    (void)capacity;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

// --- Area Lifecycle (0.4.0) ---

nm_status_t Phase2Services::lifecycle_get_area_info(nm_area_info_t* out_info) const {
    if (!out_info) return NM_ERR_INVALID_ARG;
    std::memset(out_info, 0, sizeof(*out_info));
    out_info->struct_size = sizeof(*out_info);

#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;

    struct Ctx {
        nm_build_id_t   bid;
        uint64_t        base;
        nm_area_info_t* out;
    } ctx{build_id_, image_base_, out_info};

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        LiveInGamePointers ig = resolve_ingame_pointers_unguarded(c->bid, c->base);
        (void)Phase2Services::read_area_info_from_structs(ig.client_world, ig.instance_client, *c->out);
        return 0;
    };

    nm_exception_info_t ex{};
    return invoke_guarded(fn, &ctx, nullptr, &ex);
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase2Services::lifecycle_set_area_callback(nm_area_event_callback_t cb, void* user_data) {
    {
        std::lock_guard<std::mutex> lock(g_p2_cb_mutex);
        g_area_cb = cb;
        g_area_ud = cb ? user_data : nullptr;
    }
    g_has_area_cb.store(cb != nullptr, std::memory_order_release);
    return NM_OK;
}

// --- Item Filter (0.4.0) ---

nm_status_t Phase2Services::filter_get_state(nm_item_filter_state_t* out_state) const {
    if (!out_state) return NM_ERR_INVALID_ARG;
    std::memset(out_state, 0, sizeof(*out_state));
    out_state->struct_size = sizeof(*out_state);

#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;

    struct Ctx {
        nm_build_id_t           bid;
        uint64_t                base;
        nm_item_filter_state_t* out;
    } ctx{build_id_, image_base_, out_state};

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        populate_filter_state_unguarded(c->bid, c->base, *c->out);
        return 0;
    };
    nm_exception_info_t ex{};
    return invoke_guarded(fn, &ctx, nullptr, &ex);
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase2Services::filter_reload() const {
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;

    struct Ctx {
        nm_build_id_t bid;
        uint64_t      base;
    } ctx{build_id_, image_base_};

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        void* opt = resolve_options_singleton_unguarded(c->bid, c->base);
        if (!opt || !ptr_readable_safe(opt, 0x560u)) return -1;
        auto* bytes = static_cast<uint8_t*>(opt);
        bytes[off_or(c->bid, "items.options.reload_show_msg", 0x543u)] = 0u;
        bytes[off_or(c->bid, "items.options.reload_pending", 0x542u)] = 1u;
        return 0;
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK) return st;
    return (rc == 0) ? NM_OK : NM_ERR_NOT_INITIALIZED;
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase2Services::filter_get_world_item_result(
    uint32_t                 entity_id,
    nm_item_filter_result_t* out_result) const {
    if (!out_result) return NM_ERR_INVALID_ARG;
    std::memset(out_result, 0, sizeof(*out_result));
    out_result->struct_size = sizeof(*out_result);
    out_result->alert_sound_id = -1;
    out_result->minimap_icon_shape = -1;
    out_result->minimap_icon_color = -1;
    out_result->minimap_icon_size = -1;
    out_result->beam_color = -1;

#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;

    struct Ctx {
        nm_build_id_t            bid;
        uint64_t                 base;
        uint32_t                 entity_id;
        nm_item_filter_result_t* out;
    } ctx{build_id_, image_base_, entity_id, out_result};

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        LiveInGamePointers ig = resolve_ingame_pointers_unguarded(c->bid, c->base);
        if (!ig.client_world) return -1;
        void* ent = resolve_entity_by_id_unguarded(c->bid, ig.client_world, c->entity_id);
        if (!ent) return -2;
        void* wi = call_get_component_unguarded(c->bid, c->base, "components.get_world_item", ent);
        if (!wi || !ptr_readable_safe(wi, 0xB8u)) return -2;

        uint8_t style_valid = 0;
        (void)read_mem(wi, off_or(c->bid, "items.world_item.filter_style_valid", 0xB0u), style_valid);
        if (style_valid == 0) {
            const uint64_t fn_eval = sym_addr(c->bid, c->base, "items.world_item_eval_filter");
            if (fn_eval != 0) {
                using EvalFn = const void*(NM_CALL*)(void*);
                (void)reinterpret_cast<EvalFn>(static_cast<uintptr_t>(fn_eval))(wi);
                (void)read_mem(wi, off_or(c->bid, "items.world_item.filter_style_valid", 0xB0u), style_valid);
            }
        }

        int32_t vis_override = -1;
        const uint64_t fn_vis = sym_addr(c->bid, c->base, "items.world_item_is_visible");
        if (fn_vis != 0) {
            using IsVisFn = uint8_t(NM_CALL*)(void*);
            vis_override = reinterpret_cast<IsVisFn>(static_cast<uintptr_t>(fn_vis))(wi) ? 1 : 0;
        }

        if (style_valid != 0) {
            const void* style_ptr = static_cast<const uint8_t*>(wi) + off_or(c->bid, "items.world_item.filter_style", 0x70u);
            (void)Phase2Services::extract_item_filter_style(style_ptr, vis_override, *c->out);
        } else {
            c->out->evaluated = 0;
            c->out->is_visible = (vis_override == 0) ? 0u : 1u;
        }

        float lx = 0.0f, ly = 0.0f, lw = 0.0f, lh = 0.0f;
        if (find_ground_label_rect_unguarded(c->bid, c->base, ig.ingame_ui, ent, lx, ly, lw, lh)) {
            c->out->has_label_rect = 1u;
            c->out->label_x = lx;
            c->out->label_y = ly;
            c->out->label_w = lw;
            c->out->label_h = lh;
        }
        return 0;
    };

    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK) return st;
    if (rc == -1) return NM_ERR_NOT_INITIALIZED;
    if (rc == -2) return NM_ERR_SYMBOL_NOT_FOUND;
    return NM_OK;
#else
    (void)entity_id;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase2Services::filter_evaluate_entity(
    uint32_t                 entity_id,
    nm_item_filter_result_t* out_result) const {
    if (!out_result) return NM_ERR_INVALID_ARG;
    std::memset(out_result, 0, sizeof(*out_result));
    out_result->struct_size = sizeof(*out_result);
    out_result->alert_sound_id = -1;
    out_result->minimap_icon_shape = -1;
    out_result->minimap_icon_color = -1;
    out_result->minimap_icon_size = -1;
    out_result->beam_color = -1;

#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;

    struct Ctx {
        nm_build_id_t            bid;
        uint64_t                 base;
        uint32_t                 entity_id;
        nm_item_filter_result_t* out;
    } ctx{build_id_, image_base_, entity_id, out_result};

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        LiveInGamePointers ig = resolve_ingame_pointers_unguarded(c->bid, c->base);
        if (!ig.client_world) return -1;
        void* ent = resolve_entity_by_id_unguarded(c->bid, ig.client_world, c->entity_id);
        if (!ent) return -2;

        void* item_entity = ent;
        if (void* wi = call_get_component_unguarded(c->bid, c->base, "components.get_world_item", ent)) {
            void* inner = nullptr;
            if (read_mem(wi, off_or(c->bid, "items.world_item.item_entity", 0x28u), inner) && inner) {
                item_entity = inner;
            }
        }

        void* opt = resolve_options_singleton_unguarded(c->bid, c->base);
        if (!opt || !ptr_readable_safe(opt, 0x560u)) return -1;

        uint32_t filter_kind = 0;
        void* filter_ptr = nullptr;
        void* ctrl_ptr = nullptr;
        uint8_t filter_valid = 0;
        (void)read_mem(opt, off_or(c->bid, "items.options.filter_kind", 0x510u), filter_kind);
        (void)read_mem(opt, off_or(c->bid, "items.options.active_filter_ptr", 0x518u), filter_ptr);
        (void)read_mem(opt, off_or(c->bid, "items.options.active_filter_ctrl", 0x520u), ctrl_ptr);
        (void)read_mem(opt, off_or(c->bid, "items.options.filter_valid", 0x545u), filter_valid);
        if (filter_valid == 0 || filter_kind == 0 || !filter_ptr) {
            c->out->evaluated = 0;
            c->out->is_visible = 1;
            return 0;
        }

        const uint64_t fn_match = sym_addr(c->bid, c->base, "items.filter_match");
        const uint64_t fn_dtor  = sym_addr(c->bid, c->base, "items.filter_style_dtor_custom_sounds");
        if (fn_match == 0) return -3;

        // Retain local shared_ptr strong ref on the game thread while evaluating.
        volatile LONG* strong_ref = nullptr;
        if (ctrl_ptr && ptr_readable_safe(ctrl_ptr, 0x10u)) {
            strong_ref = reinterpret_cast<volatile LONG*>(static_cast<uint8_t*>(ctrl_ptr) + 0x08u);
            InterlockedIncrement(strong_ref);
        }

        RawItemFilterStyle local_style{};
        using MatchFn = void*(NM_CALL*)(const void*, void*, void*);
        reinterpret_cast<MatchFn>(static_cast<uintptr_t>(fn_match))(filter_ptr, &local_style, item_entity);

        (void)Phase2Services::extract_item_filter_style(&local_style, -1, *c->out);

        // Destroy custom_sounds vector at local_style+0x28 via game allocator dtor (zero leaks!).
        if (fn_dtor != 0) {
            using VecDtorFn = void(NM_CALL*)(void*);
            reinterpret_cast<VecDtorFn>(static_cast<uintptr_t>(fn_dtor))(local_style.bytes + 0x28u);
        }

        if (strong_ref) {
            InterlockedDecrement(strong_ref);
        }
        return 0;
    };

    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK) return st;
    if (rc == -1) return NM_ERR_NOT_INITIALIZED;
    if (rc == -2 || rc == -3) return NM_ERR_SYMBOL_NOT_FOUND;
    return NM_OK;
#else
    (void)entity_id;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase2Services::filter_set_reload_callback(nm_filter_reload_callback_t cb, void* user_data) {
    {
        std::lock_guard<std::mutex> lock(g_p2_cb_mutex);
        g_filter_reload_cb = cb;
        g_filter_reload_ud = cb ? user_data : nullptr;
    }
    g_has_filter_reload_cb.store(cb != nullptr, std::memory_order_release);
    return NM_OK;
}

// --- Camera (0.4.0) ---

nm_status_t Phase2Services::camera_get_state(nm_camera_state_t* out_state) const {
    if (!out_state) return NM_ERR_INVALID_ARG;
    std::memset(out_state, 0, sizeof(*out_state));
    out_state->struct_size = sizeof(*out_state);
    out_state->zoom_override = -1.0f;

#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;

    struct Ctx {
        nm_build_id_t      bid;
        uint64_t           base;
        nm_camera_state_t* out;
    } ctx{build_id_, image_base_, out_state};

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        LiveInGamePointers ig = resolve_ingame_pointers_unguarded(c->bid, c->base);
        if (!ig.camera_ctrl || !ptr_readable_safe(ig.camera_ctrl, 0x4C0u)) return 0;

        const uint8_t* ctrl = static_cast<const uint8_t*>(ig.camera_ctrl);
        const uint8_t* cam  = ctrl + off_or(c->bid, "world.camera_ctrl.camera", 0xA8u);
        if (!ptr_readable_safe(cam, 0x3D0u)) return 0;

        float eye[3] = {};
        float look[3] = {};
        (void)read_mem(cam, off_or(c->bid, "world.camera.eye_pos", 0x378u), eye);
        (void)read_mem(cam, off_or(c->bid, "world.camera.look_at", 0x384u), look);
        c->out->pos_x = eye[0];
        c->out->pos_y = eye[1];
        c->out->pos_z = eye[2];
        c->out->target_x = look[0];
        c->out->target_y = look[1];
        c->out->target_z = look[2];

        (void)read_mem(ctrl, off_or(c->bid, "world.camera_ctrl.zoom_current", 0x4A8u), c->out->zoom);
        (void)read_mem(ctrl, off_or(c->bid, "world.camera_ctrl.zoom_override", 0x4ACu), c->out->zoom_override);
        (void)read_mem(ctrl, off_or(c->bid, "world.camera_ctrl.zoom_target", 0x4B0u), c->out->zoom_target);

        float tan_hfov = 0.41421357f;
        float fov_scale = 0.5f;
        (void)read_mem(cam, off_or(c->bid, "world.camera.tan_half_fov", 0x268u), tan_hfov);
        (void)read_mem(cam, off_or(c->bid, "world.camera.fov_scale", 0x26Cu), fov_scale);
        c->out->fov = Phase2Services::compute_vertical_fov(tan_hfov, fov_scale);

        (void)read_mem(cam, off_or(c->bid, "world.camera.viewport_w", 0x270u), c->out->viewport_w);
        (void)read_mem(cam, off_or(c->bid, "world.camera.viewport_h", 0x274u), c->out->viewport_h);
        if (c->out->viewport_w == 0 || c->out->viewport_h == 0) {
            (void)read_mem(ctrl, off_or(c->bid, "world.camera_ctrl.viewport_w", 0x498u), c->out->viewport_w);
            (void)read_mem(ctrl, off_or(c->bid, "world.camera_ctrl.viewport_h", 0x49Cu), c->out->viewport_h);
        }

        (void)read_mem(cam, off_or(c->bid, "world.camera.view_proj", 0x100u), c->out->view_proj);
        c->out->valid = (c->out->viewport_w > 0 && c->out->viewport_h > 0) ? 1u : 0u;
        return 0;
    };

    nm_exception_info_t ex{};
    return invoke_guarded(fn, &ctx, nullptr, &ex);
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase2Services::camera_world_to_screen(
    float    wx,
    float    wy,
    float    wz,
    float*   out_sx,
    float*   out_sy,
    int32_t* out_in_front) const {
    if (!out_sx || !out_sy || !out_in_front) return NM_ERR_INVALID_ARG;
    *out_sx = 0.0f;
    *out_sy = 0.0f;
    *out_in_front = 0;

#if defined(_WIN32)
    nm_camera_state_t cam{};
    const nm_status_t st = camera_get_state(&cam);
    if (st != NM_OK) return st;
    if (cam.valid == 0) return NM_ERR_NOT_INITIALIZED;

    float off_x = 0.0f;
    float off_y = 0.0f;
    const uint64_t vp_off_addr = sym_addr(build_id_, image_base_, "ui.viewport_offset_x");
    if (vp_off_addr != 0) {
        const void* p = reinterpret_cast<const void*>(static_cast<uintptr_t>(vp_off_addr));
        (void)read_mem(p, 0x0u, off_x);
        (void)read_mem(p, 0x4u, off_y);
    }

    (void)project_world_to_screen(
        cam.view_proj, cam.viewport_w, cam.viewport_h, off_x, off_y, wx, wy, wz, out_sx, out_sy, out_in_front);
    return NM_OK;
#else
    (void)wx;
    (void)wy;
    (void)wz;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase2Services::camera_set_zoom_override(float zoom_override) {
    if (std::isnan(zoom_override) || std::isinf(zoom_override)) return NM_ERR_INVALID_ARG;

#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;

    const float value_to_write = (zoom_override < 0.0f)
                                     ? -1.0f
                                     : std::clamp(zoom_override, 0.05f, 3.0f);

    struct Ctx {
        nm_build_id_t bid;
        uint64_t      base;
        float         val;
    } ctx{build_id_, image_base_, value_to_write};

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        LiveInGamePointers ig = resolve_ingame_pointers_unguarded(c->bid, c->base);
        if (!ig.camera_ctrl || !ptr_readable_safe(ig.camera_ctrl, 0x4C0u)) return -1;
        auto* dst = static_cast<uint8_t*>(ig.camera_ctrl) + off_or(c->bid, "world.camera_ctrl.zoom_override", 0x4ACu);
        std::memcpy(dst, &c->val, sizeof(c->val));
        return 0;
    };

    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK) return st;
    if (rc != 0) return NM_ERR_NOT_INITIALIZED;
    zoom_override_modified_ = (value_to_write >= 0.0f);
    return NM_OK;
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

// --- Deterministic Dispatch Helpers ---

void Phase2Services::dispatch_area_event(int32_t event_kind, const nm_area_info_t* area_info) {
    fire_area_event_guarded(event_kind, area_info);
}

void Phase2Services::dispatch_filter_reloaded(int32_t succeeded, const nm_item_filter_state_t* filter_state) {
    fire_filter_reloaded_guarded(succeeded, filter_state);
}

// --- Pure Layout & Math Helpers (cross-platform deterministic unit testing) ---

size_t Phase2Services::traverse_rb_tree_entities(
    const void*         map_head_ptr,
    std::vector<void*>& out_entities,
    size_t              max_nodes) noexcept {
    if (!map_head_ptr) return 0;
    const void* root = nullptr;
    if (!read_mem(map_head_ptr, 0x08u, root) || !root || root == map_head_ptr) return 0;

    std::vector<const void*> stack;
    stack.reserve(64u);
    stack.push_back(root);

    size_t visited = 0;
    while (!stack.empty() && visited < max_nodes) {
        const void* node = stack.back();
        stack.pop_back();
        if (!node || node == map_head_ptr) continue;

        uint8_t is_nil = 1;
        if (!read_mem(node, 0x19u, is_nil) || is_nil != 0) continue;

        ++visited;
        void* entity_ptr = nullptr;
        if (read_mem(node, 0x28u, entity_ptr) && entity_ptr) {
            out_entities.push_back(entity_ptr);
        }

        const void* left = nullptr;
        const void* right = nullptr;
        if (read_mem(node, 0x10u, right) && right && right != map_head_ptr) {
            stack.push_back(right);
        }
        if (read_mem(node, 0x00u, left) && left && left != map_head_ptr) {
            stack.push_back(left);
        }
    }
    return visited;
}

void* Phase2Services::find_entity_in_rb_tree(const void* map_head_ptr, uint32_t entity_id) noexcept {
    if (!map_head_ptr) return nullptr;
    const void* node = nullptr;
    if (!read_mem(map_head_ptr, 0x08u, node) || !node) return nullptr;

    for (int step = 0; step < 64 && node && node != map_head_ptr; ++step) {
        uint8_t is_nil = 1;
        if (!read_mem(node, 0x19u, is_nil) || is_nil != 0) break;

        uint32_t key = 0;
        if (!read_mem(node, 0x20u, key)) break;
        if (entity_id == key) {
            void* ent = nullptr;
            return read_mem(node, 0x28u, ent) ? ent : nullptr;
        }
        const void* next = nullptr;
        if (!read_mem(node, (entity_id < key) ? 0x00u : 0x10u, next)) break;
        node = next;
    }
    return nullptr;
}

nm_status_t Phase2Services::read_stats_vector(
    const void*      stats_comp,
    size_t           begin_offset,
    size_t           end_offset,
    nm_stat_entry_t* out_buf,
    uint32_t         capacity,
    uint32_t*        out_count,
    uint32_t*        out_total) noexcept {
    if (!stats_comp || !out_count || !out_total) return NM_ERR_INVALID_ARG;
    if (capacity > 0 && !out_buf) return NM_ERR_INVALID_ARG;
    *out_count = 0;
    *out_total = 0;

    const uint8_t* vec_begin = nullptr;
    const uint8_t* vec_end = nullptr;
    if (!read_mem(stats_comp, begin_offset, vec_begin) || !read_mem(stats_comp, end_offset, vec_end)) {
        return NM_ERR_GAME_EXCEPTION;
    }
    if (!vec_begin || !vec_end || vec_end < vec_begin) {
        return NM_OK;
    }

    const size_t byte_len = static_cast<size_t>(vec_end - vec_begin);
    if ((byte_len % sizeof(nm_stat_entry_t)) != 0u) return NM_ERR_GAME_EXCEPTION;
    const size_t total = byte_len / sizeof(nm_stat_entry_t);
    if (total > 65536u) return NM_ERR_GAME_EXCEPTION;
    if (total > 0 && !ptr_readable_safe(vec_begin, byte_len)) return NM_ERR_GAME_EXCEPTION;

    *out_total = static_cast<uint32_t>(total);
    const uint32_t to_copy = static_cast<uint32_t>(std::min<size_t>(total, capacity));
    if (to_copy > 0 && out_buf) {
        std::memcpy(out_buf, vec_begin, static_cast<size_t>(to_copy) * sizeof(nm_stat_entry_t));
    }
    *out_count = to_copy;
    return NM_OK;
}

bool Phase2Services::read_area_info_from_structs(
    const void*     client_world,
    const void*     instance_client,
    nm_area_info_t& out) noexcept {
    std::memset(&out, 0, sizeof(out));
    out.struct_size = sizeof(out);
    if (!client_world) return false;

    const void* area_row = nullptr;
    if (!read_mem(client_world, 0xB0u, area_row) || !area_row) {
        return false;
    }

    out.in_game = 1u;

    uint8_t eff_lvl = 0;
    if (read_mem(client_world, 0xD4u, eff_lvl)) {
        out.effective_level = static_cast<int32_t>(eff_lvl);
    }
    (void)read_mem(client_world, 0x8E0u, out.area_seed);

    const wchar_t* id_w = nullptr;
    const wchar_t* name_w = nullptr;
    if (read_mem(area_row, 0x00u, id_w) && id_w) {
        copy_utf8_fixed(out.area_id, sizeof(out.area_id), read_c_wstr_safe(id_w));
    }
    if (read_mem(area_row, 0x08u, name_w) && name_w) {
        copy_utf8_fixed(out.area_name, sizeof(out.area_name), read_c_wstr_safe(name_w));
    }
    (void)read_mem(area_row, 0x10u, out.act);
    (void)read_mem(area_row, 0x14u, out.is_town);
    (void)read_mem(area_row, 0x26u, out.area_level);
    (void)read_mem(area_row, 0x2Au, out.hash16);
    (void)read_mem(area_row, 0x139u, out.is_guild_hideout);
    (void)read_mem(area_row, 0x14Eu, out.is_hideout);

    if (instance_client) {
        const void* const* rs_begin = nullptr;
        const void* const* rs_end = nullptr;
        if (read_mem(instance_client, 0xCFD8u, rs_begin) &&
            read_mem(instance_client, 0xCFE0u, rs_end) &&
            rs_begin && rs_end && rs_end >= rs_begin) {
            const size_t n = static_cast<size_t>(rs_end - rs_begin);
            if (n <= 256u && ptr_readable_safe(rs_begin, n * sizeof(void*))) {
                uint32_t written = 0;
                for (size_t i = 0; i < n && written < NM_AREA_MAX_RULESETS; ++i) {
                    const void* rs = rs_begin[i];
                    if (!rs) continue;
                    uint32_t rid = 0;
                    if (read_mem(rs, 0x08u, rid)) {
                        out.ruleset_ids[written++] = rid;
                    }
                }
                out.ruleset_count = written;
            }
        }
    }

    return true;
}

bool Phase2Services::extract_item_filter_style(
    const void*              style_ptr,
    int32_t                  visibility_override,
    nm_item_filter_result_t& out) noexcept {
    std::memset(&out, 0, sizeof(out));
    out.struct_size = sizeof(out);
    out.alert_sound_id = -1;
    out.minimap_icon_shape = -1;
    out.minimap_icon_color = -1;
    out.minimap_icon_size = -1;
    out.beam_color = -1;

    if (!style_ptr || !ptr_readable_safe(style_ptr, 0x40u)) return false;

    uint8_t flags[3] = {};
    uint8_t text_rgba[4] = {};
    uint8_t border_rgba[4] = {};
    uint8_t bg_rgba[4] = {};
    int32_t sound_id = -1;
    uint32_t sound_vol = 0;
    uint32_t font_size = 0;
    int32_t minimap_icon = -1;
    int32_t play_effect = -1;

    if (!read_mem(style_ptr, 0x00u, flags) ||
        !read_mem(style_ptr, 0x03u, text_rgba) ||
        !read_mem(style_ptr, 0x07u, border_rgba) ||
        !read_mem(style_ptr, 0x0Bu, bg_rgba) ||
        !read_mem(style_ptr, 0x10u, sound_id) ||
        !read_mem(style_ptr, 0x14u, sound_vol) ||
        !read_mem(style_ptr, 0x18u, font_size) ||
        !read_mem(style_ptr, 0x1Cu, minimap_icon) ||
        !read_mem(style_ptr, 0x20u, play_effect)) {
        return false;
    }

    out.evaluated = 1u;
    out.style_flags = static_cast<uint32_t>(flags[0]) |
                      (static_cast<uint32_t>(flags[1]) << 8) |
                      (static_cast<uint32_t>(flags[2]) << 16);

    if (visibility_override >= 0) {
        out.is_visible = (visibility_override != 0) ? 1u : 0u;
    } else {
        out.is_visible = ((flags[0] & 0x01u) == 0u) ? 1u : 0u;
    }

    if ((flags[0] & 0x04u) != 0u) out.text_rgba = pack_rgba_u32(text_rgba);
    if ((flags[0] & 0x08u) != 0u) out.border_rgba = pack_rgba_u32(border_rgba);
    if ((flags[0] & 0x10u) != 0u) out.bg_rgba = pack_rgba_u32(bg_rgba);
    if ((flags[1] & 0x01u) != 0u) out.font_size = font_size;

    if ((flags[1] & (0x02u | 0x04u)) != 0u) {
        out.alert_sound_id = sound_id;
        out.alert_sound_volume = sound_vol;
    }
    if ((flags[2] & 0x01u) != 0u && minimap_icon >= 0) {
        out.minimap_icon_shape = minimap_icon;
    }
    if ((flags[2] & 0x02u) != 0u && play_effect >= 0) {
        out.beam_color = play_effect;
        out.beam_temporary = ((flags[2] & 0x04u) == 0u) ? 1u : 0u;
    }

    return true;
}

float Phase2Services::compute_vertical_fov(float tan_half_fov, float fov_scale) noexcept {
    if (tan_half_fov <= 0.0f || fov_scale <= 1e-6f) {
        return 0.78539816339f; // default pi/4 = 45 degrees
    }
    return 2.0f * std::atan((0.5f / fov_scale) * tan_half_fov);
}

bool Phase2Services::project_world_to_screen(
    const float view_proj[16],
    uint32_t    viewport_w,
    uint32_t    viewport_h,
    float       offset_x,
    float       offset_y,
    float       wx,
    float       wy,
    float       wz,
    float*      out_sx,
    float*      out_sy,
    int32_t*    out_in_front) noexcept {
    if (!view_proj || !out_sx || !out_sy || !out_in_front || viewport_w == 0 || viewport_h == 0) {
        if (out_in_front) *out_in_front = 0;
        return false;
    }

    // Exact DirectX row-major v_row * M multiplication matching Camera::WorldToScreen (0x140fd3b40).
    const float cx = wx * view_proj[0] + wy * view_proj[4] + wz * view_proj[8]  + view_proj[12];
    const float cy = wx * view_proj[1] + wy * view_proj[5] + wz * view_proj[9]  + view_proj[13];
    const float cw = wx * view_proj[3] + wy * view_proj[7] + wz * view_proj[11] + view_proj[15];

    if (cw <= 1e-6f) {
        *out_in_front = 0;
        *out_sx = 0.0f;
        *out_sy = 0.0f;
        return false;
    }

    *out_in_front = 1;
    *out_sx = (static_cast<float>(viewport_w) * 0.5f) * (cx / cw + 1.0f) - offset_x;
    *out_sy = (static_cast<float>(viewport_h) * 0.5f) * (1.0f - cy / cw) - offset_y;
    return true;
}

} // namespace nativemods
