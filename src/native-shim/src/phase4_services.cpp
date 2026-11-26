#include "phase4_services.hpp"

#include "address_table.hpp"
#include "exception_wall.hpp"
#include "game_allocator.hpp"
#include "hook_engine.hpp"
#include "phase1_services.hpp"
#include "shim_logger.hpp"

#include <algorithm>
#include <array>
#include <atomic>
#include <cctype>
#include <cmath>
#include <cstddef>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cwchar>
#include <mutex>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

#if defined(_WIN32)
#  define WIN32_LEAN_AND_MEAN
#  include <windows.h>
#  ifdef exception_code
#    undef exception_code
#  endif
#endif

namespace nativemods {

// --- ABI 0.5.0 prefix layout static_asserts (zero hidden padding, exact C# LayoutKind.Sequential parity) ---
static_assert(NM_ABI_VERSION >= 0x00000500u, "NM_ABI_VERSION must be >= 0.5.0");
static_assert(sizeof(nm_abi_table_t) >= 1120u, "nm_abi_table_t must preserve 0.5.0 prefix (8 + 139*8)");
static_assert(offsetof(nm_abi_table_t, camera_set_zoom_override) == 8u + 106u * 8u, "ABI 0.4.0 prefix preserved");
static_assert(offsetof(nm_abi_table_t, ui_create_control) == 8u + 107u * 8u, "ABI 0.5.0 starts at slot 107");
static_assert(offsetof(nm_abi_table_t, ui_listbox_subscribe_changed) == 8u + 128u * 8u, "Rich UI ends at slot 128");
static_assert(offsetof(nm_abi_table_t, tooltip_set_item_augment_callback) == 8u + 129u * 8u, "Tooltips at slot 129");
static_assert(offsetof(nm_abi_table_t, tooltip_add_item_section) == 8u + 130u * 8u, "Tooltip add at slot 130");
static_assert(offsetof(nm_abi_table_t, affinity_set_router_callback) == 8u + 131u * 8u, "Affinity at slot 131");
static_assert(offsetof(nm_abi_table_t, geal_register_builtin) == 8u + 132u * 8u, "GEAL builtin at slot 132");
static_assert(offsetof(nm_abi_table_t, geal_invoke_function) == 8u + 134u * 8u, "GEAL invoke at slot 134");
static_assert(offsetof(nm_abi_table_t, ruleset_register_native) == 8u + 135u * 8u, "Ruleset native at slot 135");
static_assert(offsetof(nm_abi_table_t, ui_register_custom_control_kind) == 8u + 137u * 8u, "Custom UI at slot 137");
static_assert(offsetof(nm_abi_table_t, ui_unregister_custom_control_kind) == 8u + 138u * 8u, "ABI 0.5.0 ends at slot 138");
static_assert(sizeof(nm_affinity_target_t) == 4u, "nm_affinity_target_t must be 4 bytes");
static_assert(sizeof(nm_affinity_route_result_t) == 32u, "nm_affinity_route_result_t must be 32 bytes");
static_assert(offsetof(nm_affinity_route_result_t, targets) == 16u, "targets at +16");
static_assert(sizeof(nm_native_ruleset_callbacks_t) == 64u, "nm_native_ruleset_callbacks_t must be 64 bytes");
static_assert(offsetof(nm_native_ruleset_callbacks_t, on_created) == 8u, "on_created at +8");
static_assert(offsetof(nm_native_ruleset_callbacks_t, on_area_left) == 56u, "on_area_left at +56");
static_assert(sizeof(nm_custom_ui_control_callbacks_t) == 32u, "nm_custom_ui_control_callbacks_t must be 32 bytes");
static_assert(offsetof(nm_custom_ui_control_callbacks_t, on_created) == 8u, "on_created at +8");
static_assert(offsetof(nm_custom_ui_control_callbacks_t, on_destroyed) == 24u, "on_destroyed at +24");

namespace {

constexpr uint64_t kPhase4UiTokenTag       = 0x4000000000000000ull;
constexpr size_t   kMaxGealBuiltins        = 128u;
constexpr size_t   kMaxCustomUiKinds       = 64u;
constexpr uint32_t kRulesetSlot6Sentinel   = 0x50u;
constexpr uint32_t kTabEntryStride         = 0x90u;
constexpr uint32_t kComboBoxItemStride     = 0x10u;
constexpr uint32_t kListBoxItemStride      = 0x18u;

// --- Shared global state (atomic fast-paths + mutex-protected registries) ---
std::atomic<nm_build_id_t>  g_p4_build_id{NM_BUILD_UNKNOWN};
std::atomic<uint64_t>       g_p4_image_base{0};
std::atomic<GameAllocator*> g_p4_allocator{nullptr};

std::mutex                    g_p4_cb_mutex;
nm_tooltip_augment_callback_t g_tooltip_cb = nullptr;
void*                         g_tooltip_ud = nullptr;
nm_affinity_route_callback_t  g_affinity_cb = nullptr;
void*                         g_affinity_ud = nullptr;
std::atomic<bool>             g_has_tooltip_cb{false};
std::atomic<bool>             g_has_affinity_cb{false};

// --- Rich UI int-event subscriptions (TabContainer / ComboBox / ListBox) ---
struct UiIntSubEntry {
    uint64_t                   token;
    int32_t                    ctrl_kind;
    void*                      control;
    nm_ui_int_event_callback_t cb;
    void*                      user_data;
};

std::mutex                 g_ui_int_sub_mutex;
std::vector<UiIntSubEntry> g_ui_int_subs;
std::atomic<uint64_t>      g_next_ui_int_token{1};
std::atomic<size_t>        g_ui_int_sub_count{0};

// --- GEAL Builtin Indirection Registry ---
struct GealBuiltinSlot {
    std::atomic<uint64_t>      generation{0};
    uint64_t                   handle = 0;
    std::string                name_utf8;
    std::wstring               name_w;
    uint32_t                   min_args = 0;
    uint32_t                   max_args = 0;
    bool                       has_return = false;
    nm_geal_builtin_callback_t cb = nullptr;
    void*                      user_data = nullptr;
};

std::mutex                                  g_geal_mutex;
std::array<GealBuiltinSlot, kMaxGealBuiltins> g_geal_slots;
std::atomic<uint64_t>                       g_next_geal_handle{1};

// --- Native Ruleset Proxy Registry (0x00..0xAE) ---
struct NativeRulesetSlot {
    std::atomic<uint64_t>         generation{0};
    uint64_t                      handle = 0;
    bool                          installed_in_live_table = false;
    nm_native_ruleset_callbacks_t callbacks{};
    void*                         user_data = nullptr;
};

std::mutex                                          g_ruleset_mutex;
std::array<NativeRulesetSlot, NM_RULESET_SLOT_COUNT> g_ruleset_slots;
std::atomic<uint64_t>                               g_next_ruleset_handle{1};

// --- Custom .ui Control Kind Registry ---
struct CustomUiKindSlot {
    std::atomic<uint64_t>            generation{0};
    uint64_t                         handle = 0;
    std::string                      kind_utf8;
    std::wstring                     kind_w;
    bool                             installed_in_live_map = false;
    nm_custom_ui_control_callbacks_t callbacks{};
    void*                            user_data = nullptr;
};

struct LiveCustomControlRecord {
    size_t   slot_idx;
    uint64_t generation;
};

std::mutex                                         g_custom_ui_mutex;
std::array<CustomUiKindSlot, kMaxCustomUiKinds>    g_custom_ui_slots;
std::unordered_map<void*, LiveCustomControlRecord> g_live_custom_controls;
std::atomic<uint64_t>                              g_next_custom_ui_handle{1};
std::atomic<size_t>                                g_live_custom_control_count{0};

// --- Verified 3.29.3d free-slot table for rulesets.creator_table (107 free / 68 occupied, #128/#132) ---
constexpr bool is_3293d_free_ruleset_slot(uint32_t id) noexcept {
    if (id <= 0x10u) return true;
    if (id >= 0x12u && id <= 0x3Fu) return true;
    if (id >= 0x41u && id <= 0x45u) return true;
    if (id == 0x47u || id == 0x48u) return true;
    if (id >= 0x4Du && id <= 0x50u) return true;
    if (id >= 0x52u && id <= 0x57u) return true;
    if (id == 0x61u || id == 0x62u) return true;
    if (id >= 0x6Cu && id <= 0x6Eu) return true;
    if (id == 0x71u || id == 0x73u || id == 0x79u || id == 0x7Bu) return true;
    if (id >= 0x80u && id <= 0x84u) return true;
    if (id == 0x8Au || id == 0x8Bu) return true;
    if (id == 0x90u) return true;
    if (id >= 0x92u && id <= 0x96u) return true;
    if (id == 0x9Cu) return true;
    if (id == 0xA3u || id == 0xA9u || id == 0xAAu || id == 0xAEu) return true;
    return false;
}

bool ascii_iequals(const std::string& a, const std::string& b) noexcept {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i) {
        if (std::tolower(static_cast<unsigned char>(a[i])) !=
            std::tolower(static_cast<unsigned char>(b[i]))) {
            return false;
        }
    }
    return true;
}

bool ptr_readable_p4(const void* p, size_t len) noexcept {
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
bool read_mem_p4(const void* base, size_t offset, T& out) noexcept {
    if (!base) return false;
    const auto* addr = static_cast<const uint8_t*>(base) + offset;
    if (!ptr_readable_p4(addr, sizeof(T))) return false;
    std::memcpy(&out, addr, sizeof(T));
    return true;
}

std::wstring utf8_to_wide_p4(const char* s) {
    if (!s || s[0] == '\0') return {};
#if defined(_WIN32)
    const int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, nullptr, 0);
    if (n <= 1) return {};
    std::wstring w(static_cast<size_t>(n - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s, -1, w.data(), n);
    return w;
#else
    std::wstring out;
    const auto* bytes = reinterpret_cast<const unsigned char*>(s);
    for (size_t i = 0; bytes[i] != 0;) {
        uint32_t cp = bytes[i];
        if (cp < 0x80u) {
            ++i;
        } else if ((cp & 0xE0u) == 0xC0u && bytes[i + 1]) {
            cp = ((cp & 0x1Fu) << 6) | (bytes[i + 1] & 0x3Fu);
            i += 2;
        } else if ((cp & 0xF0u) == 0xE0u && bytes[i + 1] && bytes[i + 2]) {
            cp = ((cp & 0x0Fu) << 12) | ((bytes[i + 1] & 0x3Fu) << 6) | (bytes[i + 2] & 0x3Fu);
            i += 3;
        } else {
            ++i;
            cp = '?';
        }
        out.push_back(static_cast<wchar_t>(cp));
    }
    return out;
#endif
}

// --- Native ClientRuleset 9-slot C++ Proxy (used on BOTH live Win64 and unit tests) ---
struct ClientRulesetProxy {
    void**   vtable;
    uint32_t ruleset_id;
    uint32_t pad;
    void*    instance_client;
    uint64_t expected_generation;
};
static_assert(offsetof(ClientRulesetProxy, vtable) == 0x00u, "ClientRuleset+0x00 vtable");
static_assert(offsetof(ClientRulesetProxy, ruleset_id) == 0x08u, "ClientRuleset+0x08 id");
static_assert(offsetof(ClientRulesetProxy, instance_client) == 0x10u, "ClientRuleset+0x10 instance_client");

bool snapshot_ruleset_slot(
    uint32_t                       ruleset_id,
    uint64_t                       expected_gen,
    nm_native_ruleset_callbacks_t& out_cbs,
    void*&                         out_ud) {
    if (ruleset_id >= NM_RULESET_SLOT_COUNT) return false;
    auto& slot = g_ruleset_slots[ruleset_id];
    if (slot.generation.load(std::memory_order_acquire) != expected_gen) return false;
    std::lock_guard<std::mutex> lock(g_ruleset_mutex);
    if (slot.handle == 0 || slot.generation.load(std::memory_order_acquire) != expected_gen) return false;
    out_cbs = slot.callbacks;
    out_ud = slot.user_data;
    return true;
}

void* NM_CALL ruleset_proxy_slot0_dtor(ClientRulesetProxy* self, uint32_t flags) {
    if (!self) return nullptr;
    nm_native_ruleset_callbacks_t cbs{};
    void* ud = nullptr;
    if (snapshot_ruleset_slot(self->ruleset_id, self->expected_generation, cbs, ud) && cbs.on_area_left) {
        struct Ctx {
            nm_ruleset_area_left_cb_t cb;
            uint32_t                  id;
            void*                     ud;
        } ctx{cbs.on_area_left, self->ruleset_id, ud};
        auto fn = [](void* raw) -> int64_t {
            auto* c = static_cast<Ctx*>(raw);
            c->cb(c->id, c->ud);
            return 0;
        };
        nm_exception_info_t ex{};
        (void)invoke_guarded(fn, &ctx, nullptr, &ex);
    }
    if ((flags & 1u) != 0u) {
        delete self;
    }
    return self;
}

void NM_CALL ruleset_proxy_slot1_on_created(ClientRulesetProxy* self) {
    if (!self) return;
    nm_native_ruleset_callbacks_t cbs{};
    void* ud = nullptr;
    if (!snapshot_ruleset_slot(self->ruleset_id, self->expected_generation, cbs, ud) || !cbs.on_created) return;
    struct Ctx {
        nm_ruleset_created_cb_t cb;
        uint32_t                id;
        void*                   ic;
        void*                   ud;
    } ctx{cbs.on_created, self->ruleset_id, self->instance_client, ud};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        c->cb(c->id, c->ic, c->ud);
        return 0;
    };
    nm_exception_info_t ex{};
    (void)invoke_guarded(fn, &ctx, nullptr, &ex);
}

void NM_CALL ruleset_proxy_slot2_on_area_entered(ClientRulesetProxy* self, void* local_player) {
    if (!self) return;
    nm_native_ruleset_callbacks_t cbs{};
    void* ud = nullptr;
    if (!snapshot_ruleset_slot(self->ruleset_id, self->expected_generation, cbs, ud) || !cbs.on_area_entered) return;

    uint32_t entity_id = 0;
    if (local_player) {
        (void)read_mem_p4(local_player, 0x88u, entity_id);
    }

    struct Ctx {
        nm_ruleset_area_entered_cb_t cb;
        uint32_t                     id;
        uint32_t                     eid;
        void*                        ud;
    } ctx{cbs.on_area_entered, self->ruleset_id, entity_id, ud};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        c->cb(c->id, c->eid, c->ud);
        return 0;
    };
    nm_exception_info_t ex{};
    (void)invoke_guarded(fn, &ctx, nullptr, &ex);
}

struct MsvcStdString {
    union {
        char  buf[16];
        char* ptr;
    };
    uint64_t size;
    uint64_t cap;
};
static_assert(sizeof(MsvcStdString) == 32u, "MSVC std::string must be 32 bytes");

void NM_CALL ruleset_proxy_slot3_on_script_event(ClientRulesetProxy* self, void* source_entity, const void* event_name_msvc_str) {
    if (!self) return;
    nm_native_ruleset_callbacks_t cbs{};
    void* ud = nullptr;
    if (!snapshot_ruleset_slot(self->ruleset_id, self->expected_generation, cbs, ud) || !cbs.on_script_event) return;

    uint32_t source_id = 0;
    if (source_entity) {
        (void)read_mem_p4(source_entity, 0x88u, source_id);
    }
    std::string ev_name;
    if (event_name_msvc_str && ptr_readable_p4(event_name_msvc_str, sizeof(MsvcStdString))) {
        const auto* ms = static_cast<const MsvcStdString*>(event_name_msvc_str);
        if (ms->size > 0 && ms->size <= 1024u) {
            const char* chars = (ms->cap >= 16u) ? ms->ptr : ms->buf;
            if (chars && ptr_readable_p4(chars, static_cast<size_t>(ms->size))) {
                ev_name.assign(chars, static_cast<size_t>(ms->size));
            }
        }
    }

    struct Ctx {
        nm_ruleset_script_event_cb_t cb;
        uint32_t                     id;
        uint32_t                     src;
        const char*                  name;
        void*                        ud;
    } ctx{cbs.on_script_event, self->ruleset_id, source_id, ev_name.c_str(), ud};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        c->cb(c->id, c->src, c->name, c->ud);
        return 0;
    };
    nm_exception_info_t ex{};
    (void)invoke_guarded(fn, &ctx, nullptr, &ex);
}

void NM_CALL ruleset_proxy_slot4_on_frame(ClientRulesetProxy* self, float delta_seconds) {
    if (!self) return;
    nm_native_ruleset_callbacks_t cbs{};
    void* ud = nullptr;
    if (!snapshot_ruleset_slot(self->ruleset_id, self->expected_generation, cbs, ud) || !cbs.on_frame) return;
    struct Ctx {
        nm_ruleset_frame_cb_t cb;
        uint32_t              id;
        float                 dt;
        void*                 ud;
    } ctx{cbs.on_frame, self->ruleset_id, delta_seconds, ud};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        c->cb(c->id, c->dt, c->ud);
        return 0;
    };
    nm_exception_info_t ex{};
    (void)invoke_guarded(fn, &ctx, nullptr, &ex);
}

void NM_CALL ruleset_proxy_slot5_on_tick(ClientRulesetProxy* self, float fixed_dt) {
    if (!self) return;
    nm_native_ruleset_callbacks_t cbs{};
    void* ud = nullptr;
    if (!snapshot_ruleset_slot(self->ruleset_id, self->expected_generation, cbs, ud) || !cbs.on_tick) return;
    struct Ctx {
        nm_ruleset_tick_cb_t cb;
        uint32_t             id;
        float                dt;
        void*                ud;
    } ctx{cbs.on_tick, self->ruleset_id, fixed_dt, ud};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        c->cb(c->id, c->dt, c->ud);
        return 0;
    };
    nm_exception_info_t ex{};
    (void)invoke_guarded(fn, &ctx, nullptr, &ex);
}

uint32_t NM_CALL ruleset_proxy_slot6_get_sentinel(ClientRulesetProxy* /*self*/) {
    return kRulesetSlot6Sentinel;
}

void NM_CALL ruleset_proxy_slot7_reserved(ClientRulesetProxy* /*self*/) {}

void NM_CALL ruleset_proxy_slot8_deserialise(ClientRulesetProxy* self, const Phase4Services::NativeRulesetBufferView* view) {
    if (!self || !view) return;
    nm_native_ruleset_callbacks_t cbs{};
    void* ud = nullptr;
    if (!snapshot_ruleset_slot(self->ruleset_id, self->expected_generation, cbs, ud) || !cbs.on_ruleset_update) return;

    Phase4Services::NativeRulesetBufferView bv{};
    if (!read_mem_p4(view, 0, bv)) return;
    const uint64_t rem = (bv.position <= bv.length) ? (bv.length - bv.position) : 0ull;
    if (rem > NM_RULESET_MAX_PAYLOAD) return;
    const uint8_t* ptr = (rem > 0 && bv.data) ? (bv.data + bv.position) : nullptr;
    if (rem > 0 && (!ptr || !ptr_readable_p4(ptr, static_cast<size_t>(rem)))) return;

    struct Ctx {
        nm_ruleset_update_cb_t cb;
        uint32_t               id;
        const uint8_t*         data;
        uint32_t               len;
        void*                  ud;
    } ctx{cbs.on_ruleset_update, self->ruleset_id, ptr, static_cast<uint32_t>(rem), ud};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        c->cb(c->id, c->data, c->len, c->ud);
        return 0;
    };
    nm_exception_info_t ex{};
    (void)invoke_guarded(fn, &ctx, nullptr, &ex);
}

void* g_ruleset_proxy_vtable[NM_RULESET_VTABLE_SLOTS] = {
    reinterpret_cast<void*>(&ruleset_proxy_slot0_dtor),
    reinterpret_cast<void*>(&ruleset_proxy_slot1_on_created),
    reinterpret_cast<void*>(&ruleset_proxy_slot2_on_area_entered),
    reinterpret_cast<void*>(&ruleset_proxy_slot3_on_script_event),
    reinterpret_cast<void*>(&ruleset_proxy_slot4_on_frame),
    reinterpret_cast<void*>(&ruleset_proxy_slot5_on_tick),
    reinterpret_cast<void*>(&ruleset_proxy_slot6_get_sentinel),
    reinterpret_cast<void*>(&ruleset_proxy_slot7_reserved),
    reinterpret_cast<void*>(&ruleset_proxy_slot8_deserialise),
};

ClientRulesetProxy* allocate_ruleset_proxy(uint32_t ruleset_id, void* instance_client) {
    if (ruleset_id >= NM_RULESET_SLOT_COUNT) return nullptr;
    const uint64_t gen = g_ruleset_slots[ruleset_id].generation.load(std::memory_order_acquire);
    auto* proxy = new ClientRulesetProxy();
    proxy->vtable = g_ruleset_proxy_vtable;
    proxy->ruleset_id = ruleset_id;
    proxy->pad = 0;
    proxy->instance_client = instance_client;
    proxy->expected_generation = gen;
    return proxy;
}

#if defined(_WIN32)
template <uint32_t RulesetId>
ClientRulesetProxy** NM_CALL ruleset_creator_thunk(ClientRulesetProxy** out_unique_ptr, void* instance_client) {
    if (out_unique_ptr) {
        *out_unique_ptr = allocate_ruleset_proxy(RulesetId, instance_client);
    }
    return out_unique_ptr;
}

using RulesetCreatorThunkFn = ClientRulesetProxy**(NM_CALL*)(ClientRulesetProxy**, void*);

template <size_t... Is>
constexpr std::array<RulesetCreatorThunkFn, sizeof...(Is)> make_ruleset_creator_table(std::index_sequence<Is...>) {
    return {&ruleset_creator_thunk<static_cast<uint32_t>(Is)>...};
}

constexpr auto kRulesetCreatorThunks = make_ruleset_creator_table(std::make_index_sequence<NM_RULESET_SLOT_COUNT>{});
#endif

// --- Guarded callback dispatchers ---
void fire_ui_int_event_guarded(int32_t ctrl_kind, void* control, int32_t value) {
    if (!control || g_ui_int_sub_count.load(std::memory_order_acquire) == 0) return;
    std::vector<UiIntSubEntry> matches;
    {
        std::lock_guard<std::mutex> lock(g_ui_int_sub_mutex);
        for (const auto& s : g_ui_int_subs) {
            if (s.ctrl_kind == ctrl_kind && s.control == control && s.cb != nullptr) {
                matches.push_back(s);
            }
        }
    }
    for (const auto& m : matches) {
        struct Ctx {
            nm_ui_int_event_callback_t cb;
            void*                      ctrl;
            int32_t                    val;
            void*                      ud;
        } ctx{m.cb, control, value, m.user_data};
        auto fn = [](void* raw) -> int64_t {
            auto* c = static_cast<Ctx*>(raw);
            c->cb(c->ctrl, c->val, c->ud);
            return 0;
        };
        nm_exception_info_t ex{};
        (void)invoke_guarded(fn, &ctx, nullptr, &ex);
    }
}

void fire_tooltip_augment_guarded(void* popup_control, const nm_item_snapshot_t* item, int32_t alt_held) {
    if (!popup_control || !item || !g_has_tooltip_cb.load(std::memory_order_acquire)) return;
    nm_tooltip_augment_callback_t cb = nullptr;
    void* ud = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_p4_cb_mutex);
        cb = g_tooltip_cb;
        ud = g_tooltip_ud;
    }
    if (!cb) return;

    struct Ctx {
        nm_tooltip_augment_callback_t cb;
        void*                         popup;
        const nm_item_snapshot_t*     item;
        int32_t                       alt;
        void*                         ud;
    } ctx{cb, popup_control, item, alt_held, ud};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        c->cb(c->popup, c->item, c->alt, c->ud);
        return 0;
    };
    nm_exception_info_t ex{};
    (void)invoke_guarded(fn, &ctx, nullptr, &ex);
}

int32_t fire_affinity_route_guarded(
    const nm_item_snapshot_t*   item,
    int32_t                     stash_kind,
    nm_affinity_route_result_t* out_result) {
    if (!item || !out_result) return 0;
    std::memset(out_result, 0, sizeof(*out_result));
    out_result->struct_size = sizeof(nm_affinity_route_result_t);
    out_result->action = NM_AFFINITY_ACTION_STOCK;

    if (!g_has_affinity_cb.load(std::memory_order_acquire)) return 0;
    nm_affinity_route_callback_t cb = nullptr;
    void* ud = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_p4_cb_mutex);
        cb = g_affinity_cb;
        ud = g_affinity_ud;
    }
    if (!cb) return 0;

    struct Ctx {
        nm_affinity_route_callback_t cb;
        const nm_item_snapshot_t*    item;
        int32_t                      kind;
        nm_affinity_route_result_t*  res;
        void*                        ud;
    } ctx{cb, item, stash_kind, out_result, ud};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        return static_cast<int64_t>(c->cb(c->item, c->kind, c->res, c->ud));
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    if (invoke_guarded(fn, &ctx, &rc, &ex) != NM_OK) {
        return 0;
    }
    if (out_result->target_count > NM_AFFINITY_MAX_TARGETS) {
        out_result->target_count = NM_AFFINITY_MAX_TARGETS;
    }
    return (rc != 0) ? 1 : 0;
}

nm_status_t invoke_geal_slot_guarded(
    size_t       slot_idx,
    uint64_t     expected_gen,
    uint32_t     entity_id,
    const char*  args_json_utf8,
    std::string& out_result) {
    out_result.clear();
    if (slot_idx >= kMaxGealBuiltins) return NM_ERR_NOT_FOUND;

    nm_geal_builtin_callback_t cb = nullptr;
    void* ud = nullptr;
    bool has_return = false;
    {
        auto& slot = g_geal_slots[slot_idx];
        if (slot.generation.load(std::memory_order_acquire) != expected_gen) {
            return NM_OK; // Safely no-op when mod has unloaded!
        }
        std::lock_guard<std::mutex> lock(g_geal_mutex);
        if (slot.handle == 0 || slot.generation.load(std::memory_order_acquire) != expected_gen) {
            return NM_OK; // Safely no-op when mod has unloaded!
        }
        cb = slot.cb;
        ud = slot.user_data;
        has_return = slot.has_return;
    }
    if (!cb) return NM_OK;

    std::string buf(4096u, '\0');
    size_t out_len = 0;
    struct Ctx {
        nm_geal_builtin_callback_t cb;
        uint32_t                   entity_id;
        const char*                args_json;
        char*                      out_buf;
        size_t                     out_cap;
        size_t*                    out_len;
        void*                      ud;
    } ctx{
        cb,
        entity_id,
        args_json_utf8 ? args_json_utf8 : "[]",
        has_return ? buf.data() : nullptr,
        has_return ? buf.size() : 0u,
        &out_len,
        ud,
    };
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        return static_cast<int64_t>(c->cb(c->entity_id, c->args_json, c->out_buf, c->out_cap, c->out_len, c->ud));
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK) return st;
    const auto cb_st = static_cast<nm_status_t>(rc);
    if (cb_st == NM_OK && has_return) {
        if (out_len > 0 && out_len < buf.size()) {
            out_result.assign(buf.data(), out_len);
        } else if (buf[0] != '\0') {
            out_result.assign(buf.c_str());
        }
    }
    return cb_st;
}

// geal.builtin_register_entry (0x141de9780) executes `movaps xmm0, XMMWORD PTR [rdi]`
// at 0x141de9858 and 0x141de9870, requiring `begin` to be 16-byte aligned!
struct alignas(16) GealBuiltinEntry {
    const wchar_t* name;
    void*          factory;
};
static_assert(sizeof(GealBuiltinEntry) == 16u, "GealBuiltinEntry must be 16 bytes");
static_assert(alignof(GealBuiltinEntry) == 16u, "GealBuiltinEntry must be 16-byte aligned for movaps");

struct alignas(16) GealRegisterCtx {
    GealBuiltinEntry entry;
    uint64_t         reg_addr;
};
static_assert(alignof(GealRegisterCtx) == 16u, "GealRegisterCtx must be 16-byte aligned");
static_assert(offsetof(GealRegisterCtx, entry) % 16u == 0u, "GealRegisterCtx::entry must be 16-byte aligned for movaps");

#if defined(_WIN32)

// --- Win32 live helpers ---
struct MsvcWString {
    union {
        wchar_t  buf[8];
        wchar_t* ptr;
    };
    uint64_t size;
    uint64_t cap;
};
static_assert(sizeof(MsvcWString) == 32u, "MSVC std::wstring must be 32 bytes");

struct alignas(16) ImageHandle16 {
    uint64_t vtable;
    void*    img_obj;
};
static_assert(sizeof(ImageHandle16) == 16u, "ImageHandle must be 16 bytes");
static_assert(alignof(ImageHandle16) == 16u, "ImageHandle must be 16-byte aligned");

struct MsvcStdFunc64 {
    uint8_t storage[0x38];
    void*   impl;
};
static_assert(sizeof(MsvcStdFunc64) == 0x40u, "MSVC std::function must be 0x40 bytes");

struct GealFuncWrapper {
    MsvcStdFunc64 fn;
    uint8_t       has_return;
    uint8_t       pad0[7];
    uint32_t      return_type_tag;
    uint32_t      pad1;
};
static_assert(sizeof(GealFuncWrapper) == 0x50u, "GealFuncWrapper must be 0x50 bytes");
static_assert(offsetof(GealFuncWrapper, has_return) == 0x40u, "has_return at +0x40");
static_assert(offsetof(GealFuncWrapper, return_type_tag) == 0x48u, "return_type_tag at +0x48");

struct GealValueRaw {
    uint8_t data[0x20];
    int8_t  tag;
    uint8_t pad[7];
};
static_assert(sizeof(GealValueRaw) == 0x28u, "GealValue must be 0x28 bytes");

struct GealValueVectorRaw {
    GealValueRaw* begin;
    GealValueRaw* end;
    GealValueRaw* cap;
    GealValueRaw  inline0;
};
static_assert(sizeof(GealValueVectorRaw) == 0x40u, "GealValueVector must be 0x40 bytes");

struct GealArgSpan {
    const uint8_t* begin;
    const uint8_t* end;
};

struct MsvcVectorRaw {
    uint8_t* begin;
    uint8_t* end;
    uint8_t* cap;
};

size_t off_or_p4(nm_build_id_t bid, std::string_view name, size_t fallback) noexcept {
    if (bid != NM_BUILD_UNKNOWN && bid != NM_BUILD_SYNTHETIC) {
        if (const EmbeddedOffset* o = find_offset(bid, name)) {
            return o->offset;
        }
    }
    return fallback;
}

uint64_t sym_addr_p4(nm_build_id_t bid, uint64_t base, std::string_view name) noexcept {
    if (bid == NM_BUILD_UNKNOWN || bid == NM_BUILD_SYNTHETIC || base == 0) return 0;
    const EmbeddedSymbol* s = find_symbol(bid, name);
    if (!s || s->rva == 0 || s->status == NM_SYM_STATUS_UNVERIFIED) return 0;
    return base + s->rva;
}

std::string wide_to_utf8_p4(const wchar_t* ptr, size_t len) {
    if (!ptr || len == 0 || len > 8192u) return {};
    const int needed = WideCharToMultiByte(CP_UTF8, 0, ptr, static_cast<int>(len), nullptr, 0, nullptr, nullptr);
    if (needed <= 0) return {};
    std::string out(static_cast<size_t>(needed), '\0');
    WideCharToMultiByte(CP_UTF8, 0, ptr, static_cast<int>(len), out.data(), needed, nullptr, nullptr);
    return out;
}

std::string read_c_wstr_p4(const wchar_t* ptr) {
    if (!ptr || !ptr_readable_p4(ptr, sizeof(wchar_t))) return {};
    size_t len = 0;
    while (len < 1024u && ptr_readable_p4(ptr + len, sizeof(wchar_t)) && ptr[len] != L'\0') {
        ++len;
    }
    return wide_to_utf8_p4(ptr, len);
}

void init_msvc_ws_unguarded(nm_build_id_t bid, uint64_t base, MsvcWString& ws, const std::wstring& src) {
    std::memset(&ws, 0, sizeof(ws));
    ws.cap = 7;
    if (src.empty()) return;
    const uint64_t assign_addr = sym_addr_p4(bid, base, "runtime.wstring_assign");
    if (assign_addr != 0) {
        using AssignFn = void*(NM_CALL*)(void*, const wchar_t*, uint64_t);
        reinterpret_cast<AssignFn>(static_cast<uintptr_t>(assign_addr))(
            &ws, src.c_str(), static_cast<uint64_t>(src.size()));
    } else if (src.size() <= 7u) {
        std::memcpy(ws.buf, src.data(), src.size() * sizeof(wchar_t));
        ws.buf[src.size()] = L'\0';
        ws.size = src.size();
    }
}

void destroy_msvc_ws_unguarded(nm_build_id_t bid, uint64_t base, MsvcWString& ws) {
    const uint64_t dtor = sym_addr_p4(bid, base, "runtime.wstring_destroy");
    if (dtor != 0) {
        using DtorFn = void(NM_CALL*)(void*);
        reinterpret_cast<DtorFn>(static_cast<uintptr_t>(dtor))(&ws);
    }
}

void attach_child_unguarded(nm_build_id_t bid, uint64_t base, void* parent, void* child) {
    if (!parent || !child) return;
    const uint64_t add_addr = sym_addr_p4(bid, base, "ui.add_child");
    if (add_addr == 0) return;
    const size_t children_off = off_or_p4(bid, "ui.control.children_container", 0xB8u);
    const size_t begin_off    = off_or_p4(bid, "ui.control.children_begin", 0xC0u);
    const size_t end_off      = off_or_p4(bid, "ui.control.children_end", 0xC8u);

    uint8_t* begin = nullptr;
    uint8_t* end = nullptr;
    uint64_t count = 0;
    if (read_mem_p4(parent, begin_off, begin) && read_mem_p4(parent, end_off, end) &&
        begin && end && end >= begin) {
        count = static_cast<uint64_t>(end - begin) / sizeof(void*);
    }
    using AddChildFn = void(NM_CALL*)(void*, uint64_t, void*);
    reinterpret_cast<AddChildFn>(static_cast<uintptr_t>(add_addr))(
        static_cast<uint8_t*>(parent) + children_off, count, child);
}

std::string json_escape_str(const std::string& s) {
    std::string out;
    out.reserve(s.size() + 2);
    out.push_back('"');
    for (char ch : s) {
        if (ch == '"' || ch == '\\') {
            out.push_back('\\');
            out.push_back(ch);
        } else if (ch == '\n') {
            out += "\\n";
        } else if (ch == '\r') {
            out += "\\r";
        } else if (ch == '\t') {
            out += "\\t";
        } else {
            out.push_back(ch);
        }
    }
    out.push_back('"');
    return out;
}

std::string encode_geal_value_json(const GealValueRaw& v) {
    switch (v.tag) {
        case 0x08: { // int32 / uint32
            int32_t iv = 0;
            std::memcpy(&iv, v.data, sizeof(iv));
            return std::to_string(iv);
        }
        case 0x09: { // float
            float fv = 0.0f;
            std::memcpy(&fv, v.data, sizeof(fv));
            char buf[64];
            std::snprintf(buf, sizeof(buf), "%.6g", static_cast<double>(fv));
            return std::string(buf);
        }
        case 0x0A: { // bool
            return (v.data[0] != 0) ? "true" : "false";
        }
        case 0x0B: { // MSVC std::wstring
            const auto* ws = reinterpret_cast<const MsvcWString*>(v.data);
            if (ws->size == 0 || ws->size > 4096u) return "\"\"";
            const wchar_t* chars = (ws->cap >= 8u) ? ws->ptr : ws->buf;
            if (!chars || !ptr_readable_p4(chars, static_cast<size_t>(ws->size) * sizeof(wchar_t))) {
                return "\"\"";
            }
            return json_escape_str(wide_to_utf8_p4(chars, static_cast<size_t>(ws->size)));
        }
        default:
            return "null";
    }
}

void write_json_to_geal_vec_unguarded(
    nm_build_id_t       bid,
    uint64_t            base,
    const std::string&  json_str,
    GealValueVectorRaw* out_vec) {
    if (!out_vec) return;
    std::memset(out_vec, 0, sizeof(*out_vec));
    out_vec->begin = &out_vec->inline0;
    out_vec->end = &out_vec->inline0;
    out_vec->cap = &out_vec->inline0 + 1;
    out_vec->inline0.tag = -1;

    // Trim whitespace
    size_t a = 0;
    size_t b = json_str.size();
    while (a < b && std::isspace(static_cast<unsigned char>(json_str[a]))) ++a;
    while (b > a && std::isspace(static_cast<unsigned char>(json_str[b - 1]))) --b;
    if (a == b) return;
    const std::string tok = json_str.substr(a, b - a);
    if (tok == "null") {
        out_vec->inline0.tag = 0x00;
        out_vec->end = &out_vec->inline0 + 1;
        return;
    }
    if (tok == "true" || tok == "false") {
        out_vec->inline0.tag = 0x0A;
        out_vec->inline0.data[0] = (tok == "true") ? 1u : 0u;
        out_vec->end = &out_vec->inline0 + 1;
        return;
    }
    if (tok.size() >= 2 && tok.front() == '"' && tok.back() == '"') {
        const std::wstring w = utf8_to_wide_p4(tok.substr(1, tok.size() - 2).c_str());
        auto* ws = reinterpret_cast<MsvcWString*>(out_vec->inline0.data);
        init_msvc_ws_unguarded(bid, base, *ws, w);
        out_vec->inline0.tag = 0x0B;
        out_vec->end = &out_vec->inline0 + 1;
        return;
    }
    if (tok.find('.') != std::string::npos || tok.find('e') != std::string::npos || tok.find('E') != std::string::npos) {
        char* endp = nullptr;
        const float fv = std::strtof(tok.c_str(), &endp);
        if (endp != tok.c_str()) {
            std::memcpy(out_vec->inline0.data, &fv, sizeof(fv));
            out_vec->inline0.tag = 0x09;
            out_vec->end = &out_vec->inline0 + 1;
            return;
        }
    }
    char* endp = nullptr;
    const long iv = std::strtol(tok.c_str(), &endp, 10);
    if (endp != tok.c_str()) {
        const int32_t i32 = static_cast<int32_t>(iv);
        std::memcpy(out_vec->inline0.data, &i32, sizeof(i32));
        out_vec->inline0.tag = 0x08;
        out_vec->end = &out_vec->inline0 + 1;
    }
}

// --- DLL-owned SBO MSVC std::function _Func_impl for GEAL Builtins (#128) ---
struct alignas(16) GealCompiledArgSlot {
    uint8_t bytes[0x50];
};
static_assert(sizeof(GealCompiledArgSlot) == 0x50u, "GealCompiledArgSlot must be 0x50 bytes");
static_assert(alignof(GealCompiledArgSlot) == 16u, "GealCompiledArgSlot must be 16-byte aligned");

struct GealCompiledArgsHolder {
    std::atomic<uint32_t>            ref_count{1};
    uint32_t                         count = 0;
    std::vector<GealCompiledArgSlot> arg_storage; // count * 0x50 bytes, 16-byte aligned
};

struct GealFuncImpl {
    void**                  vtable;
    uint64_t                slot_index;
    uint64_t                expected_gen;
    GealCompiledArgsHolder* args_holder;
    uint32_t                has_return;
    uint32_t                pad;
};
static_assert(sizeof(GealFuncImpl) <= 0x38u, "GealFuncImpl must fit in MSVC std::function 0x38B SBO storage");

extern void* g_geal_void_impl_vtable[7];
extern void* g_geal_value_impl_vtable[7];

GealFuncImpl* NM_CALL geal_impl_copy(const GealFuncImpl* self, void* dst) {
    auto* out = static_cast<GealFuncImpl*>(dst);
    *out = *self;
    if (out->args_holder) {
        out->args_holder->ref_count.fetch_add(1u, std::memory_order_relaxed);
    }
    return out;
}

GealFuncImpl* NM_CALL geal_impl_move(GealFuncImpl* self, void* dst) {
    auto* out = static_cast<GealFuncImpl*>(dst);
    *out = *self;
    self->args_holder = nullptr;
    return out;
}

std::string eval_geal_args_to_json_unguarded(
    nm_build_id_t           bid,
    uint64_t                base,
    GealCompiledArgsHolder* holder) {
    if (!holder || holder->count == 0) return "[]";
    const uint64_t eval_addr = sym_addr_p4(bid, base, "geal.compiled_arg_eval");
    if (eval_addr == 0) return "[]";
    using EvalFn = void(NM_CALL*)(void*);
    auto eval_fn = reinterpret_cast<EvalFn>(static_cast<uintptr_t>(eval_addr));

    std::string json = "[";
    for (uint32_t i = 0; i < holder->count; ++i) {
        if (i > 0) json.push_back(',');
        uint8_t* arg_mem = holder->arg_storage[i].bytes;
        eval_fn(arg_mem);
        const auto* vec = reinterpret_cast<const GealValueVectorRaw*>(arg_mem + 0x08u);
        if (vec->begin && vec->end && vec->end > vec->begin && ptr_readable_p4(vec->begin, sizeof(GealValueRaw))) {
            json += encode_geal_value_json(*vec->begin);
        } else {
            json += "null";
        }
    }
    json.push_back(']');
    return json;
}

uint32_t extract_geal_entity_id_unguarded(nm_build_id_t bid, uint64_t base, void* target) {
    if (!target) return 0;
    const uint64_t get_ent_addr = sym_addr_p4(bid, base, "geal.exec_ctx_get_entity");
    if (get_ent_addr == 0) return 0;
    using GetEntFn = void*(NM_CALL*)(void*);
    void* ent = reinterpret_cast<GetEntFn>(static_cast<uintptr_t>(get_ent_addr))(target);
    uint32_t eid = 0;
    if (ent) (void)read_mem_p4(ent, 0x88u, eid);
    return eid;
}

void NM_CALL geal_impl_do_call_void(GealFuncImpl* self, void* target, void* /*ctx*/) {
    if (!self) return;
    const nm_build_id_t bid = g_p4_build_id.load(std::memory_order_acquire);
    const uint64_t base = g_p4_image_base.load(std::memory_order_acquire);

    struct EvalCtx {
        nm_build_id_t           bid;
        uint64_t                base;
        void*                   target;
        GealCompiledArgsHolder* holder;
        uint32_t                eid = 0;
        std::string             args_json;
    } ectx{bid, base, target, self->args_holder, 0, "[]"};
    auto eval_fn = [](void* raw) -> int64_t {
        auto* c = static_cast<EvalCtx*>(raw);
        c->eid = extract_geal_entity_id_unguarded(c->bid, c->base, c->target);
        c->args_json = eval_geal_args_to_json_unguarded(c->bid, c->base, c->holder);
        return 0;
    };
    nm_exception_info_t ex{};
    (void)invoke_guarded(eval_fn, &ectx, nullptr, &ex);

    std::string ignored;
    (void)invoke_geal_slot_guarded(
        static_cast<size_t>(self->slot_index), self->expected_gen, ectx.eid, ectx.args_json.c_str(), ignored);
}

GealValueVectorRaw* NM_CALL geal_impl_do_call_value(GealFuncImpl* self, GealValueVectorRaw* out_vec, void* target, void* /*ctx*/) {
    if (out_vec) {
        std::memset(out_vec, 0, sizeof(*out_vec));
        out_vec->begin = &out_vec->inline0;
        out_vec->end = &out_vec->inline0;
        out_vec->cap = &out_vec->inline0 + 1;
        out_vec->inline0.tag = -1;
    }
    if (!self || !out_vec) return out_vec;

    const nm_build_id_t bid = g_p4_build_id.load(std::memory_order_acquire);
    const uint64_t base = g_p4_image_base.load(std::memory_order_acquire);

    struct EvalCtx {
        nm_build_id_t           bid;
        uint64_t                base;
        void*                   target;
        GealCompiledArgsHolder* holder;
        uint32_t                eid = 0;
        std::string             args_json;
    } ectx{bid, base, target, self->args_holder, 0, "[]"};
    auto eval_fn = [](void* raw) -> int64_t {
        auto* c = static_cast<EvalCtx*>(raw);
        c->eid = extract_geal_entity_id_unguarded(c->bid, c->base, c->target);
        c->args_json = eval_geal_args_to_json_unguarded(c->bid, c->base, c->holder);
        return 0;
    };
    nm_exception_info_t ex{};
    (void)invoke_guarded(eval_fn, &ectx, nullptr, &ex);

    std::string ret_json;
    if (invoke_geal_slot_guarded(
            static_cast<size_t>(self->slot_index), self->expected_gen, ectx.eid, ectx.args_json.c_str(), ret_json) == NM_OK &&
        !ret_json.empty()) {
        struct WriteCtx {
            nm_build_id_t       bid;
            uint64_t            base;
            const std::string*  json;
            GealValueVectorRaw* out;
        } wctx{bid, base, &ret_json, out_vec};
        auto wfn = [](void* raw) -> int64_t {
            auto* c = static_cast<WriteCtx*>(raw);
            write_json_to_geal_vec_unguarded(c->bid, c->base, *c->json, c->out);
            return 0;
        };
        (void)invoke_guarded(wfn, &wctx, nullptr, &ex);
    }
    return out_vec;
}

void NM_CALL geal_impl_delete_this(GealFuncImpl* self, uint8_t dealloc) {
    if (!self) return;
    if (self->args_holder) {
        if (self->args_holder->ref_count.fetch_sub(1u, std::memory_order_acq_rel) == 1u) {
            delete self->args_holder;
        }
        self->args_holder = nullptr;
    }
    if (dealloc != 0) {
        std::free(self);
    }
}

const void* NM_CALL geal_impl_type(const GealFuncImpl* /*self*/) { return nullptr; }
const void* NM_CALL geal_impl_get(const GealFuncImpl* self) { return self; }

void* NM_CALL geal_impl_scalar_dtor(GealFuncImpl* self, uint32_t flags) {
    geal_impl_delete_this(self, (flags & 1u) != 0u ? 1u : 0u);
    return self;
}

// Exact 7-slot MSVC std::_Func_base vtable layout verified on Game.exe 3.29.3d (0x143031688):
//   [0] +0x00 _Copy, [1] +0x08 _Move, [2] +0x10 _Do_call, [3] +0x18 _Type,
//   [4] +0x20 _Delete_this, [5] +0x28 _Get, [6] +0x30 ~_Func_base(flags)
void* g_geal_void_impl_vtable[7] = {
    reinterpret_cast<void*>(&geal_impl_copy),
    reinterpret_cast<void*>(&geal_impl_move),
    reinterpret_cast<void*>(&geal_impl_do_call_void),
    reinterpret_cast<void*>(&geal_impl_type),
    reinterpret_cast<void*>(&geal_impl_delete_this),
    reinterpret_cast<void*>(&geal_impl_get),
    reinterpret_cast<void*>(&geal_impl_scalar_dtor),
};

void* g_geal_value_impl_vtable[7] = {
    reinterpret_cast<void*>(&geal_impl_copy),
    reinterpret_cast<void*>(&geal_impl_move),
    reinterpret_cast<void*>(&geal_impl_do_call_value),
    reinterpret_cast<void*>(&geal_impl_type),
    reinterpret_cast<void*>(&geal_impl_delete_this),
    reinterpret_cast<void*>(&geal_impl_get),
    reinterpret_cast<void*>(&geal_impl_scalar_dtor),
};

GealFuncWrapper* build_geal_func_wrapper(size_t slot_idx, GealFuncWrapper* out_sret, const GealArgSpan* args) {
    if (!out_sret) return nullptr;
    std::memset(out_sret, 0, sizeof(*out_sret));

    bool has_return = false;
    uint64_t gen = 0;
    {
        std::lock_guard<std::mutex> lock(g_geal_mutex);
        has_return = g_geal_slots[slot_idx].has_return;
        gen = g_geal_slots[slot_idx].generation.load(std::memory_order_acquire);
    }

    GealCompiledArgsHolder* holder = nullptr;
    const nm_build_id_t bid = g_p4_build_id.load(std::memory_order_acquire);
    const uint64_t base = g_p4_image_base.load(std::memory_order_acquire);
    const uint64_t compile_addr = sym_addr_p4(bid, base, "geal.compiled_arg_compile");
    if (args && args->begin && args->end && args->end > args->begin && compile_addr != 0) {
        const size_t n = static_cast<size_t>(args->end - args->begin) / 16u;
        if (n > 0 && n <= 16u) {
            holder = new GealCompiledArgsHolder();
            holder->count = static_cast<uint32_t>(n);
            holder->arg_storage.assign(n, GealCompiledArgSlot{});
            using CompileArgFn = void*(NM_CALL*)(void*, const void*);
            auto compile_fn = reinterpret_cast<CompileArgFn>(static_cast<uintptr_t>(compile_addr));
            for (size_t i = 0; i < n; ++i) {
                compile_fn(holder->arg_storage[i].bytes, args->begin + i * 16u);
            }
        }
    }

    auto* impl = reinterpret_cast<GealFuncImpl*>(out_sret->fn.storage);
    impl->vtable = has_return ? g_geal_value_impl_vtable : g_geal_void_impl_vtable;
    impl->slot_index = static_cast<uint64_t>(slot_idx);
    impl->expected_gen = gen;
    impl->args_holder = holder;
    impl->has_return = has_return ? 1u : 0u;
    impl->pad = 0;
    out_sret->fn.impl = impl;
    out_sret->has_return = has_return ? 1u : 0u;
    out_sret->return_type_tag = 0x0Du; // Dynamic / Any
    return out_sret;
}

template <size_t Slot>
GealFuncWrapper* NM_CALL geal_builtin_factory_thunk(GealFuncWrapper* out_sret, const GealArgSpan* args) {
    return build_geal_func_wrapper(Slot, out_sret, args);
}

using GealFactoryThunkFn = GealFuncWrapper*(NM_CALL*)(GealFuncWrapper*, const GealArgSpan*);

template <size_t... Is>
constexpr std::array<GealFactoryThunkFn, sizeof...(Is)> make_geal_factory_table(std::index_sequence<Is...>) {
    return {&geal_builtin_factory_thunk<Is>...};
}

constexpr auto kGealFactoryThunks = make_geal_factory_table(std::make_index_sequence<kMaxGealBuiltins>{});

// --- DLL-owned SBO MSVC std::function<Control*()> _Func_impl for Custom UI Control Kinds (#128) ---
struct CustomUiFuncImpl {
    void**   vtable;
    uint64_t slot_index;
    uint64_t expected_gen;
};
static_assert(sizeof(CustomUiFuncImpl) <= 0x38u, "CustomUiFuncImpl must fit in MSVC std::function 0x38B SBO storage");

CustomUiFuncImpl* NM_CALL custom_ui_impl_copy(const CustomUiFuncImpl* self, void* dst) {
    auto* out = static_cast<CustomUiFuncImpl*>(dst);
    *out = *self;
    return out;
}

CustomUiFuncImpl* NM_CALL custom_ui_impl_move(CustomUiFuncImpl* self, void* dst) {
    auto* out = static_cast<CustomUiFuncImpl*>(dst);
    *out = *self;
    return out;
}

void* NM_CALL custom_ui_impl_do_call(CustomUiFuncImpl* self) {
    const nm_build_id_t bid = g_p4_build_id.load(std::memory_order_acquire);
    const uint64_t base = g_p4_image_base.load(std::memory_order_acquire);
    const uint64_t pool_addr = sym_addr_p4(bid, base, "ui.control_pool");
    const uint64_t frame_create_addr = sym_addr_p4(bid, base, "ui.frame_create");
    if (pool_addr == 0 || frame_create_addr == 0) return nullptr;

    using PoolFn = void*(NM_CALL*)();
    using FrameCreateFn = void*(NM_CALL*)(void*);
    void* pool = reinterpret_cast<PoolFn>(static_cast<uintptr_t>(pool_addr))();
    if (!pool) return nullptr;
    void* frame = reinterpret_cast<FrameCreateFn>(static_cast<uintptr_t>(frame_create_addr))(pool);
    if (!frame || !self || self->slot_index >= kMaxCustomUiKinds) return frame;

    std::string kind_name;
    nm_custom_ui_created_cb_t on_created = nullptr;
    void* ud = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_custom_ui_mutex);
        auto& slot = g_custom_ui_slots[self->slot_index];
        if (slot.handle != 0 && slot.generation.load(std::memory_order_acquire) == self->expected_gen) {
            kind_name = slot.kind_utf8;
            on_created = slot.callbacks.on_created;
            ud = slot.user_data;
            g_live_custom_controls[frame] = LiveCustomControlRecord{
                static_cast<size_t>(self->slot_index), self->expected_gen};
            g_live_custom_control_count.store(g_live_custom_controls.size(), std::memory_order_release);
        }
    }

    if (on_created) {
        struct Ctx {
            nm_custom_ui_created_cb_t cb;
            const char*               kind;
            void*                     ctrl;
            void*                     ud;
        } ctx{on_created, kind_name.c_str(), frame, ud};
        auto fn = [](void* raw) -> int64_t {
            auto* c = static_cast<Ctx*>(raw);
            c->cb(c->kind, c->ctrl, c->ud);
            return 0;
        };
        nm_exception_info_t ex{};
        (void)invoke_guarded(fn, &ctx, nullptr, &ex);
    }
    return frame;
}

void NM_CALL custom_ui_impl_delete_this(CustomUiFuncImpl* self, uint8_t dealloc) {
    if (dealloc != 0 && self) {
        std::free(self);
    }
}

const void* NM_CALL custom_ui_impl_type(const CustomUiFuncImpl* /*self*/) { return nullptr; }
const void* NM_CALL custom_ui_impl_get(const CustomUiFuncImpl* self) { return self; }

void* NM_CALL custom_ui_impl_scalar_dtor(CustomUiFuncImpl* self, uint32_t flags) {
    custom_ui_impl_delete_this(self, (flags & 1u) != 0u ? 1u : 0u);
    return self;
}

// Exact 7-slot MSVC std::_Func_base vtable layout verified on Game.exe 3.29.3d (0x143031688):
//   [0] +0x00 _Copy, [1] +0x08 _Move, [2] +0x10 _Do_call, [3] +0x18 _Type,
//   [4] +0x20 _Delete_this, [5] +0x28 _Get, [6] +0x30 ~_Func_base(flags)
void* g_custom_ui_impl_vtable[7] = {
    reinterpret_cast<void*>(&custom_ui_impl_copy),
    reinterpret_cast<void*>(&custom_ui_impl_move),
    reinterpret_cast<void*>(&custom_ui_impl_do_call),
    reinterpret_cast<void*>(&custom_ui_impl_type),
    reinterpret_cast<void*>(&custom_ui_impl_delete_this),
    reinterpret_cast<void*>(&custom_ui_impl_get),
    reinterpret_cast<void*>(&custom_ui_impl_scalar_dtor),
};

// --- Live TargetChain Hooks (ALL verified >= 14B and <= 8 args in #132) ---

void NM_CALL hook_tab_container_select_post(nm_hook_context_t* ctx, void* /*ud*/) {
    if (!ctx || g_ui_int_sub_count.load(std::memory_order_acquire) == 0) return;
    void* tc = reinterpret_cast<void*>(static_cast<uintptr_t>(ctx->args[0]));
    if (!tc) return;
    const nm_build_id_t bid = g_p4_build_id.load(std::memory_order_acquire);
    uint8_t has_val = 0;
    uint64_t idx = 0;
    int32_t active = -1;
    if (read_mem_p4(tc, off_or_p4(bid, "ui.tab_container.active_tab_has_value", 0x328u), has_val) && has_val != 0 &&
        read_mem_p4(tc, off_or_p4(bid, "ui.tab_container.active_tab_index", 0x320u), idx)) {
        active = static_cast<int32_t>(idx);
    }
    fire_ui_int_event_guarded(NM_UI_CTRL_TAB_CONTAINER, tc, active);
}

void NM_CALL hook_combobox_selection_changed_post(nm_hook_context_t* ctx, void* /*ud*/) {
    if (!ctx || g_ui_int_sub_count.load(std::memory_order_acquire) == 0) return;
    void* cb = reinterpret_cast<void*>(static_cast<uintptr_t>(ctx->args[0]));
    if (!cb) return;
    const nm_build_id_t bid = g_p4_build_id.load(std::memory_order_acquire);
    uint8_t has_val = 0;
    uint64_t idx = 0;
    int32_t sel = -1;
    if (read_mem_p4(cb, off_or_p4(bid, "ui.combobox.selected_has_value", 0x4F0u), has_val) && has_val != 0 &&
        read_mem_p4(cb, off_or_p4(bid, "ui.combobox.selected_index", 0x4E8u), idx)) {
        sel = static_cast<int32_t>(idx);
    }
    fire_ui_int_event_guarded(NM_UI_CTRL_COMBOBOX, cb, sel);
}

void NM_CALL hook_listbox_set_selected_post(nm_hook_context_t* ctx, void* /*ud*/) {
    if (!ctx || g_ui_int_sub_count.load(std::memory_order_acquire) == 0) return;
    void* lb = reinterpret_cast<void*>(static_cast<uintptr_t>(ctx->args[0]));
    if (!lb) return;
    const nm_build_id_t bid = g_p4_build_id.load(std::memory_order_acquire);
    uint8_t has_val = 0;
    uint64_t idx = 0;
    int32_t sel = -1;
    if (read_mem_p4(lb, off_or_p4(bid, "ui.control_listbox.selected_has_value", 0x498u), has_val) && has_val != 0 &&
        read_mem_p4(lb, off_or_p4(bid, "ui.control_listbox.selected_index", 0x490u), idx)) {
        sel = static_cast<int32_t>(idx);
    }
    fire_ui_int_event_guarded(NM_UI_CTRL_LISTBOX, lb, sel);
}

void NM_CALL hook_item_popup_build_sections_post(nm_hook_context_t* ctx, void* /*ud*/) {
    if (!ctx || !g_has_tooltip_cb.load(std::memory_order_acquire)) return;
    void* popup = reinterpret_cast<void*>(static_cast<uintptr_t>(ctx->args[0]));
    if (!popup) return;

    const nm_build_id_t bid = g_p4_build_id.load(std::memory_order_acquire);
    const uint64_t base = g_p4_image_base.load(std::memory_order_acquire);

    struct ReadSnapCtx {
        nm_build_id_t      bid;
        uint64_t           base;
        void*              popup;
        nm_item_snapshot_t snap{};
    } rctx{bid, base, popup, {}};

    auto rfn = [](void* raw) -> int64_t {
        auto* c = static_cast<ReadSnapCtx*>(raw);
        void* item_entity = nullptr;
        if (read_mem_p4(c->popup, off_or_p4(c->bid, "ui.item_hover.entity", 0x370u), item_entity) && item_entity) {
            Phase1Services::populate_item_snapshot_unguarded(c->bid, c->base, item_entity, c->snap);
        }
        return 0;
    };
    nm_exception_info_t ex{};
    (void)invoke_guarded(rfn, &rctx, nullptr, &ex);

    const int32_t alt_held = ((GetAsyncKeyState(VK_MENU) & 0x8000) != 0) ? 1 : 0;
    fire_tooltip_augment_guarded(popup, &rctx.snap, alt_held);
}

void NM_CALL hook_compute_affinity_bitset_post(nm_hook_context_t* ctx, void* /*ud*/) {
    if (!ctx || !g_has_affinity_cb.load(std::memory_order_acquire)) return;
    auto* out7b = reinterpret_cast<uint8_t*>(static_cast<uintptr_t>(ctx->args[0]));
    void* item_entity = reinterpret_cast<void*>(static_cast<uintptr_t>(ctx->args[1]));
    if (!out7b || !item_entity) return;

    const nm_build_id_t bid = g_p4_build_id.load(std::memory_order_acquire);
    const uint64_t base = g_p4_image_base.load(std::memory_order_acquire);

    struct ReadSnapCtx {
        nm_build_id_t      bid;
        uint64_t           base;
        void*              entity;
        nm_item_snapshot_t snap{};
    } rctx{bid, base, item_entity, {}};
    auto rfn = [](void* raw) -> int64_t {
        auto* c = static_cast<ReadSnapCtx*>(raw);
        Phase1Services::populate_item_snapshot_unguarded(c->bid, c->base, c->entity, c->snap);
        return 0;
    };
    nm_exception_info_t ex{};
    if (invoke_guarded(rfn, &rctx, nullptr, &ex) != NM_OK) return;

    nm_affinity_route_result_t res{};
    if (fire_affinity_route_guarded(&rctx.snap, -1, &res) != 0 && res.stock_affinity_bitmask != 0u) {
        if (ptr_readable_p4(out7b, 7u)) {
            Phase4Services::apply_affinity_bitset_mask(out7b, res.stock_affinity_bitmask);
        }
    }
}

void NM_CALL hook_stash_get_affinity_targets_post(nm_hook_context_t* ctx, void* /*ud*/) {
    if (!ctx || !g_has_affinity_cb.load(std::memory_order_acquire)) return;
    void* stash_ctrl = reinterpret_cast<void*>(static_cast<uintptr_t>(ctx->args[0]));
    auto* out_vec = reinterpret_cast<MsvcVectorRaw*>(static_cast<uintptr_t>(ctx->args[1]));
    void* item_entity = reinterpret_cast<void*>(static_cast<uintptr_t>(ctx->args[2]));
    if (!stash_ctrl || !out_vec || !item_entity) return;

    const nm_build_id_t bid = g_p4_build_id.load(std::memory_order_acquire);
    const uint64_t base = g_p4_image_base.load(std::memory_order_acquire);

    struct ReadCtx {
        nm_build_id_t      bid;
        uint64_t           base;
        void*              stash;
        void*              entity;
        uint8_t            stash_kind = 0;
        nm_item_snapshot_t snap{};
    } rctx{bid, base, stash_ctrl, item_entity, 0, {}};
    auto rfn = [](void* raw) -> int64_t {
        auto* c = static_cast<ReadCtx*>(raw);
        (void)read_mem_p4(c->stash, off_or_p4(c->bid, "ui.stash_control.stash_kind", 0x580u), c->stash_kind);
        Phase1Services::populate_item_snapshot_unguarded(c->bid, c->base, c->entity, c->snap);
        return 0;
    };
    nm_exception_info_t ex{};
    if (invoke_guarded(rfn, &rctx, nullptr, &ex) != NM_OK) return;

    nm_affinity_route_result_t res{};
    if (fire_affinity_route_guarded(&rctx.snap, static_cast<int32_t>(rctx.stash_kind), &res) == 0 ||
        res.action == NM_AFFINITY_ACTION_STOCK) {
        return;
    }

    GameAllocator* alloc = g_p4_allocator.load(std::memory_order_acquire);
    struct PatchVecCtx {
        MsvcVectorRaw*                    vec;
        const nm_affinity_route_result_t* res;
        GameAllocator*                    alloc;
    } pctx{out_vec, &res, alloc};

    auto pfn = [](void* raw) -> int64_t {
        auto* c = static_cast<PatchVecCtx*>(raw);
        if (!ptr_readable_p4(c->vec, sizeof(MsvcVectorRaw))) return -1;

        std::vector<nm_affinity_target_t> merged;
        if (c->vec->begin && c->vec->end && c->vec->end > c->vec->begin) {
            const size_t existing = static_cast<size_t>(c->vec->end - c->vec->begin) / sizeof(nm_affinity_target_t);
            if (existing <= 64u && ptr_readable_p4(c->vec->begin, existing * sizeof(nm_affinity_target_t))) {
                merged.resize(existing);
                std::memcpy(merged.data(), c->vec->begin, existing * sizeof(nm_affinity_target_t));
            }
        }
        Phase4Services::apply_affinity_targets(merged, *c->res);

        if (merged.empty()) {
            c->vec->end = c->vec->begin;
            return 0;
        }

        const size_t needed_bytes = merged.size() * sizeof(nm_affinity_target_t);
        const size_t current_cap_bytes =
            (c->vec->begin && c->vec->cap && c->vec->cap >= c->vec->begin)
                ? static_cast<size_t>(c->vec->cap - c->vec->begin)
                : 0u;
        if (needed_bytes > current_cap_bytes) {
            if (!c->alloc) return -2;
            void* new_buf = nullptr;
            const size_t alloc_bytes = std::max<size_t>(needed_bytes, 16u);
            if (c->alloc->alloc(alloc_bytes, 16u, NM_DEFAULT_ALLOC_TAG, &new_buf) != NM_OK || !new_buf) {
                return -3;
            }
            if (c->vec->begin) {
                (void)c->alloc->free(c->vec->begin, 16u);
            }
            c->vec->begin = static_cast<uint8_t*>(new_buf);
            c->vec->cap = c->vec->begin + alloc_bytes;
        }
        std::memcpy(c->vec->begin, merged.data(), needed_bytes);
        c->vec->end = c->vec->begin + needed_bytes;
        return 0;
    };
    (void)invoke_guarded(pfn, &pctx, nullptr, &ex);
}

void NM_CALL hook_parser_parse_property_pre(nm_hook_context_t* ctx, void* /*ud*/) {
    if (!ctx || g_live_custom_control_count.load(std::memory_order_acquire) == 0) return;
    void* parser = reinterpret_cast<void*>(static_cast<uintptr_t>(ctx->args[0]));
    void* ctrl   = reinterpret_cast<void*>(static_cast<uintptr_t>(ctx->args[1]));
    if (!parser || !ctrl) return;

    nm_custom_ui_property_cb_t on_prop = nullptr;
    void* ud = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_custom_ui_mutex);
        auto it = g_live_custom_controls.find(ctrl);
        if (it == g_live_custom_controls.end()) return;
        auto& slot = g_custom_ui_slots[it->second.slot_idx];
        if (slot.handle == 0 || slot.generation.load(std::memory_order_acquire) != it->second.generation) return;
        on_prop = slot.callbacks.on_property;
        ud = slot.user_data;
    }

    const nm_build_id_t bid = g_p4_build_id.load(std::memory_order_acquire);
    const uint64_t base = g_p4_image_base.load(std::memory_order_acquire);
    const uint64_t get_tok_addr = sym_addr_p4(bid, base, "ui.parser_get_token");
    const uint64_t scan_tok_addr = sym_addr_p4(bid, base, "ui.scanner_scan_token");
    if (get_tok_addr == 0) return;

    struct ParsePropCtx {
        nm_build_id_t bid;
        void*         parser;
        uint64_t      get_tok_addr;
        uint64_t      scan_tok_addr;
        bool          consumed = false;
        std::string   key;
        std::string   val;
    } pctx{bid, parser, get_tok_addr, scan_tok_addr, false, {}, {}};

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<ParsePropCtx*>(raw);
        const size_t t_off       = off_or_p4(c->bid, "ui.parser.current_token", 0x60u);
        const size_t la_off      = off_or_p4(c->bid, "ui.parser.lookahead_token", 0x68u);
        const size_t scanner_off = off_or_p4(c->bid, "ui.parser.scanner", 0x70u);
        const size_t kind_off    = off_or_p4(c->bid, "ui.parser_token.kind", 0x00u);
        const size_t val_off     = off_or_p4(c->bid, "ui.parser_token.val", 0x18u);
        const size_t next_off    = off_or_p4(c->bid, "ui.parser_token.next", 0x20u);

        void* la = nullptr;
        if (!read_mem_p4(c->parser, la_off, la) || !la) return 0;
        int32_t la_kind = 0;
        if (!read_mem_p4(la, kind_off, la_kind) || la_kind != 1) return 0; // 1 = ident

        // Peek next token; if next == nullptr, scan & link via Coco/R Scanner::ScanToken
        void* next_tok = nullptr;
        (void)read_mem_p4(la, next_off, next_tok);
        if (!next_tok && c->scan_tok_addr != 0) {
            void* scanner = nullptr;
            if (read_mem_p4(c->parser, scanner_off, scanner) && scanner) {
                using ScanFn = void*(NM_CALL*)(void*);
                next_tok = reinterpret_cast<ScanFn>(static_cast<uintptr_t>(c->scan_tok_addr))(scanner);
                if (next_tok && ptr_readable_p4(static_cast<uint8_t*>(la) + next_off, sizeof(void*))) {
                    std::memcpy(static_cast<uint8_t*>(la) + next_off, &next_tok, sizeof(void*));
                }
            }
        }
        if (!next_tok) return 0;
        int32_t next_kind = 0;
        if (!read_mem_p4(next_tok, kind_off, next_kind) || next_kind != 9) return 0; // 9 = '='

        using GetTokFn = void(NM_CALL*)(void*);
        auto get_tok = reinterpret_cast<GetTokFn>(static_cast<uintptr_t>(c->get_tok_addr));

        // Consume ident
        get_tok(c->parser);
        void* t = nullptr;
        if (read_mem_p4(c->parser, t_off, t) && t) {
            const wchar_t* wval = nullptr;
            if (read_mem_p4(t, val_off, wval) && wval) {
                c->key = read_c_wstr_p4(wval);
            }
        }
        // Consume '='
        get_tok(c->parser);

        // Consume value tokens until ';' (kind == 15) or EOF (kind == 0)
        int guard = 0;
        while (guard++ < 32) {
            void* cur_la = nullptr;
            int32_t cur_kind = 0;
            if (!read_mem_p4(c->parser, la_off, cur_la) || !cur_la ||
                !read_mem_p4(cur_la, kind_off, cur_kind)) {
                break;
            }
            if (cur_kind == 0 || cur_kind == 15) {
                if (cur_kind == 15) get_tok(c->parser); // consume ';'
                break;
            }
            get_tok(c->parser);
            void* vt = nullptr;
            if (read_mem_p4(c->parser, t_off, vt) && vt) {
                const wchar_t* wv = nullptr;
                if (read_mem_p4(vt, val_off, wv) && wv) {
                    std::string seg = read_c_wstr_p4(wv);
                    if (seg.size() >= 2 && seg.front() == '"' && seg.back() == '"') {
                        seg = seg.substr(1, seg.size() - 2);
                    }
                    if (!c->val.empty()) c->val.push_back(' ');
                    c->val += seg;
                }
            }
        }
        c->consumed = !c->key.empty();
        return 0;
    };

    nm_exception_info_t ex{};
    if (invoke_guarded(fn, &pctx, nullptr, &ex) == NM_OK && pctx.consumed) {
        ctx->skip_original = 1;
        if (on_prop) {
            struct CbCtx {
                nm_custom_ui_property_cb_t cb;
                void*                      ctrl;
                const char*                key;
                const char*                val;
                void*                      ud;
            } cbctx{on_prop, ctrl, pctx.key.c_str(), pctx.val.c_str(), ud};
            auto cbfn = [](void* raw) -> int64_t {
                auto* c = static_cast<CbCtx*>(raw);
                c->cb(c->ctrl, c->key, c->val, c->ud);
                return 0;
            };
            (void)invoke_guarded(cbfn, &cbctx, nullptr, &ex);
        }
    }
}

void NM_CALL hook_control_release_or_destroy_pre(nm_hook_context_t* ctx, void* /*ud*/) {
    if (!ctx || g_live_custom_control_count.load(std::memory_order_acquire) == 0) return;
    void* ctrl = reinterpret_cast<void*>(static_cast<uintptr_t>(ctx->args[0]));
    if (!ctrl) return;
    (void)Phase4Services::simulate_custom_ui_destroyed(ctrl);
}

#endif // _WIN32

} // namespace

Phase4Services::~Phase4Services() = default;

bool Phase4Services::is_stock_ruleset_slot_occupied(uint32_t ruleset_id) noexcept {
    if (ruleset_id >= NM_RULESET_SLOT_COUNT) return true;
    return !is_3293d_free_ruleset_slot(ruleset_id);
}

void Phase4Services::apply_affinity_bitset_mask(uint8_t out7b[7], uint32_t stock_affinity_bitmask) noexcept {
    if (!out7b) return;
    const uint32_t clamped = stock_affinity_bitmask & 0x003FFFFFu; // bits 0..21 only
    out7b[0] |= static_cast<uint8_t>(clamped & 0xFFu);
    out7b[1] |= static_cast<uint8_t>((clamped >> 8) & 0xFFu);
    out7b[2] |= static_cast<uint8_t>((clamped >> 16) & 0x3Fu);
}

void Phase4Services::apply_affinity_targets(
    std::vector<nm_affinity_target_t>& targets,
    const nm_affinity_route_result_t&  res) {
    if (res.action == NM_AFFINITY_ACTION_STOCK) return;
    const uint32_t count = std::min<uint32_t>(res.target_count, NM_AFFINITY_MAX_TARGETS);
    if (res.action == NM_AFFINITY_ACTION_OVERRIDE) {
        targets.clear();
    }
    for (uint32_t i = 0; i < count; ++i) {
        targets.push_back(res.targets[i]);
    }
}

bool Phase4Services::rb_tree_unlink_node(MsvcRbTreeHeader* tree, MsvcRbNodeHeader* z) noexcept {
    if (!tree || !tree->head || !z || z->isnil != 0) return false;
    MsvcRbNodeHeader* head = tree->head;
    auto is_nil = [](const MsvcRbNodeHeader* n) noexcept -> bool { return !n || n->isnil != 0; };

    MsvcRbNodeHeader* y = z;
    MsvcRbNodeHeader* x = nullptr;
    MsvcRbNodeHeader* x_parent = nullptr;

    if (is_nil(z->left)) {
        x = z->right;
    } else if (is_nil(z->right)) {
        x = z->left;
    } else {
        y = z->right;
        while (!is_nil(y->left)) {
            y = y->left;
        }
        x = y->right;
    }

    if (y != z) {
        z->left->parent = y;
        y->left = z->left;
        if (y != z->right) {
            x_parent = y->parent;
            if (!is_nil(x)) x->parent = y->parent;
            y->parent->left = x;
            y->right = z->right;
            z->right->parent = y;
        } else {
            x_parent = y;
        }
        if (head->parent == z) {
            head->parent = y;
        } else if (z->parent->left == z) {
            z->parent->left = y;
        } else {
            z->parent->right = y;
        }
        y->parent = z->parent;
        std::swap(y->color, z->color);
        y = z; // y now points to node being unlinked
    } else {
        x_parent = y->parent;
        if (!is_nil(x)) x->parent = y->parent;
        if (head->parent == z) {
            head->parent = x;
        } else if (z->parent->left == z) {
            z->parent->left = x;
        } else {
            z->parent->right = x;
        }
        if (head->left == z) {
            if (is_nil(z->right)) {
                head->left = z->parent;
            } else {
                MsvcRbNodeHeader* m = x;
                while (!is_nil(m->left)) m = m->left;
                head->left = m;
            }
        }
        if (head->right == z) {
            if (is_nil(z->left)) {
                head->right = z->parent;
            } else {
                MsvcRbNodeHeader* m = x;
                while (!is_nil(m->right)) m = m->right;
                head->right = m;
            }
        }
    }

    auto rotate_left = [&](MsvcRbNodeHeader* w) noexcept {
        MsvcRbNodeHeader* r = w->right;
        w->right = r->left;
        if (!is_nil(r->left)) r->left->parent = w;
        r->parent = w->parent;
        if (w == head->parent) {
            head->parent = r;
        } else if (w == w->parent->left) {
            w->parent->left = r;
        } else {
            w->parent->right = r;
        }
        r->left = w;
        w->parent = r;
    };

    auto rotate_right = [&](MsvcRbNodeHeader* w) noexcept {
        MsvcRbNodeHeader* l = w->left;
        w->left = l->right;
        if (!is_nil(l->right)) l->right->parent = w;
        l->parent = w->parent;
        if (w == head->parent) {
            head->parent = l;
        } else if (w == w->parent->right) {
            w->parent->right = l;
        } else {
            w->parent->left = l;
        }
        l->right = w;
        w->parent = l;
    };

    // Rebalance if removed node y was black (color == 1).
    if (y->color == 1u) {
        while (x != head->parent && (is_nil(x) || x->color == 1u)) {
            if (x == x_parent->left) {
                MsvcRbNodeHeader* w = x_parent->right;
                if (w->color == 0u) {
                    w->color = 1u;
                    x_parent->color = 0u;
                    rotate_left(x_parent);
                    w = x_parent->right;
                }
                if ((is_nil(w->left) || w->left->color == 1u) &&
                    (is_nil(w->right) || w->right->color == 1u)) {
                    w->color = 0u;
                    x = x_parent;
                    x_parent = x_parent->parent;
                } else {
                    if (is_nil(w->right) || w->right->color == 1u) {
                        if (!is_nil(w->left)) w->left->color = 1u;
                        w->color = 0u;
                        rotate_right(w);
                        w = x_parent->right;
                    }
                    w->color = x_parent->color;
                    x_parent->color = 1u;
                    if (!is_nil(w->right)) w->right->color = 1u;
                    rotate_left(x_parent);
                    break;
                }
            } else {
                MsvcRbNodeHeader* w = x_parent->left;
                if (w->color == 0u) {
                    w->color = 1u;
                    x_parent->color = 0u;
                    rotate_right(x_parent);
                    w = x_parent->left;
                }
                if ((is_nil(w->right) || w->right->color == 1u) &&
                    (is_nil(w->left) || w->left->color == 1u)) {
                    w->color = 0u;
                    x = x_parent;
                    x_parent = x_parent->parent;
                } else {
                    if (is_nil(w->left) || w->left->color == 1u) {
                        if (!is_nil(w->right)) w->right->color = 1u;
                        w->color = 0u;
                        rotate_left(w);
                        w = x_parent->left;
                    }
                    w->color = x_parent->color;
                    x_parent->color = 1u;
                    if (!is_nil(w->left)) w->left->color = 1u;
                    rotate_right(x_parent);
                    break;
                }
            }
        }
        if (!is_nil(x)) x->color = 1u;
    }

    if (tree->size > 0) --tree->size;
    return true;
}

void Phase4Services::install_live_hooks(
    nm_build_id_t  build_id,
    uint64_t       image_base,
    HookEngine&    hooks,
    GameAllocator& allocator) {
    build_id_ = build_id;
    image_base_ = image_base;
    allocator_ = &allocator;
    g_p4_build_id.store(build_id, std::memory_order_release);
    g_p4_image_base.store(image_base, std::memory_order_release);
    g_p4_allocator.store(&allocator, std::memory_order_release);

#if defined(_WIN32)
    if (build_id == NM_BUILD_SYNTHETIC || build_id == NM_BUILD_UNKNOWN || image_base == 0) {
        return;
    }

    auto arm_chain = [&](const char* sym_name, nm_hook_phase_t phase, nm_hook_chain_callback_t cb) -> bool {
        const EmbeddedSymbol* sym = find_symbol(build_id, sym_name);
        if (!sym || sym->rva == 0 || sym->status == NM_SYM_STATUS_UNVERIFIED) return false;
        const uint64_t addr = image_base + sym->rva;
        if (!ptr_readable_p4(reinterpret_cast<const void*>(static_cast<uintptr_t>(addr)), 24u)) return false;

        nm_hook_handle_t handle = NM_HOOK_INVALID;
        const nm_status_t st = hooks.install_inline_chain(addr, sym->expected_bytes_hex, phase, 0, cb, nullptr, &handle);
        if (st == NM_OK && handle != NM_HOOK_INVALID) {
            installed_handles_.push_back(handle);
            return true;
        }
        logger_logf("WARN", "[PHASE4] Failed to arm chain hook '%s' at 0x%llX (st=%d)",
                    sym_name, static_cast<unsigned long long>(addr), static_cast<int>(st));
        return false;
    };

    const bool ok_tab    = arm_chain("ui.tab_container_select",             NM_HOOK_PHASE_POST, &hook_tab_container_select_post);
    const bool ok_combo  = arm_chain("ui.combobox_on_selection_changed",    NM_HOOK_PHASE_POST, &hook_combobox_selection_changed_post);
    const bool ok_lb     = arm_chain("ui.control_listbox_set_selected_index", NM_HOOK_PHASE_POST, &hook_listbox_set_selected_post);
    const bool ok_tip    = arm_chain("ui.item_popup_build_sections",        NM_HOOK_PHASE_POST, &hook_item_popup_build_sections_post);
    const bool ok_affbit = arm_chain("items.compute_affinity_bitset",       NM_HOOK_PHASE_POST, &hook_compute_affinity_bitset_post);
    const bool ok_afftgt = arm_chain("ui.stash_get_affinity_targets",       NM_HOOK_PHASE_POST, &hook_stash_get_affinity_targets_post);
    const bool ok_prop   = arm_chain("ui.parser_parse_property",            NM_HOOK_PHASE_PRE,  &hook_parser_parse_property_pre);
    const bool ok_dest   = arm_chain("ui.control_release_or_destroy",       NM_HOOK_PHASE_PRE,  &hook_control_release_or_destroy_pre);

    logger_logf("INFO",
                "[PHASE4] Armed live hooks (tab=%s combo=%s listbox=%s tooltip=%s aff_bit=%s aff_tgt=%s ui_prop=%s ui_destroy=%s)",
                ok_tab ? "OK" : "SKIP",
                ok_combo ? "OK" : "SKIP",
                ok_lb ? "OK" : "SKIP",
                ok_tip ? "OK" : "SKIP",
                ok_affbit ? "OK" : "SKIP",
                ok_afftgt ? "OK" : "SKIP",
                ok_prop ? "OK" : "SKIP",
                ok_dest ? "OK" : "SKIP");
#else
    (void)hooks;
#endif
}

void Phase4Services::reset(HookEngine* hooks) {
    // Unregister all active native Extend registrations BEFORE clearing build_id_/image_base_
    // so live native tables (geal.builtin_factory_table, rulesets.creator_table, ui.control_factory_map_global)
    // are restored cleanly to stock state.
    {
        std::vector<uint64_t> handles;
        {
            std::lock_guard<std::mutex> lock(g_geal_mutex);
            for (const auto& s : g_geal_slots) {
                if (s.handle != 0) handles.push_back(s.handle);
            }
        }
        for (uint64_t h : handles) (void)geal_unregister_builtin(h);
    }
    {
        std::vector<uint64_t> handles;
        {
            std::lock_guard<std::mutex> lock(g_ruleset_mutex);
            for (const auto& s : g_ruleset_slots) {
                if (s.handle != 0) handles.push_back(s.handle);
            }
        }
        for (uint64_t h : handles) (void)ruleset_unregister_native(h);
    }
    {
        std::vector<uint64_t> handles;
        {
            std::lock_guard<std::mutex> lock(g_custom_ui_mutex);
            for (const auto& s : g_custom_ui_slots) {
                if (s.handle != 0) handles.push_back(s.handle);
            }
        }
        for (uint64_t h : handles) (void)ui_unregister_custom_control_kind(h);
    }

    if (hooks) {
        for (nm_hook_handle_t h : installed_handles_) {
            (void)hooks->remove(h);
        }
    }
    installed_handles_.clear();

    {
        std::lock_guard<std::mutex> lock(g_p4_cb_mutex);
        g_tooltip_cb = nullptr;
        g_tooltip_ud = nullptr;
        g_affinity_cb = nullptr;
        g_affinity_ud = nullptr;
    }
    g_has_tooltip_cb.store(false, std::memory_order_release);
    g_has_affinity_cb.store(false, std::memory_order_release);

    {
        std::lock_guard<std::mutex> lock(g_ui_int_sub_mutex);
        g_ui_int_subs.clear();
        g_ui_int_sub_count.store(0, std::memory_order_release);
    }
    {
        std::lock_guard<std::mutex> lock(g_custom_ui_mutex);
        g_live_custom_controls.clear();
        g_live_custom_control_count.store(0, std::memory_order_release);
    }

    build_id_ = NM_BUILD_UNKNOWN;
    image_base_ = 0;
    allocator_ = nullptr;
    g_p4_build_id.store(NM_BUILD_UNKNOWN, std::memory_order_release);
    g_p4_image_base.store(0, std::memory_order_release);
    g_p4_allocator.store(nullptr, std::memory_order_release);
}

// ============================================================================
// Rich Dynamic UI Controls (0.5.0)
// ============================================================================

nm_status_t Phase4Services::ui_create_control(
    int32_t     kind,
    void*       parent_control,
    const char* id_utf8,
    void**      out_control) const {
    if (!out_control) return NM_ERR_INVALID_ARG;
    *out_control = nullptr;
    if (kind < NM_UI_CTRL_FRAME || kind > NM_UI_CTRL_LISTBOX) return NM_ERR_INVALID_ARG;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;

    struct Ctx {
        nm_build_id_t bid;
        uint64_t      base;
        int32_t       kind;
        void*         parent;
        std::wstring  id_w;
        void*         created = nullptr;
    } ctx{
        build_id_,
        image_base_,
        kind,
        parent_control,
        id_utf8 ? utf8_to_wide_p4(id_utf8) : std::wstring{},
        nullptr,
    };

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        using Factory0Fn = void*(NM_CALL*)(void*);
        using PoolFn     = void*(NM_CALL*)();
        using Create1Fn  = void*(NM_CALL*)(void*);

        auto call_factory = [&](const char* sym_name) -> void* {
            const uint64_t addr = sym_addr_p4(c->bid, c->base, sym_name);
            if (addr == 0) return nullptr;
            return reinterpret_cast<Factory0Fn>(static_cast<uintptr_t>(addr))(nullptr);
        };

        auto call_map_factory = [&](const wchar_t* type_name) -> void* {
            const uint64_t get_map = sym_addr_p4(c->bid, c->base, "ui.control_factory_map_get");
            const uint64_t idx_fn  = sym_addr_p4(c->bid, c->base, "ui.control_factory_map_index");
            if (get_map == 0 || idx_fn == 0) return nullptr;
            void* map = reinterpret_cast<PoolFn>(static_cast<uintptr_t>(get_map))();
            if (!map) return nullptr;
            alignas(16) MsvcWString key{};
            init_msvc_ws_unguarded(c->bid, c->base, key, std::wstring(type_name));
            using MapIdxFn = MsvcStdFunc64*(NM_CALL*)(void*, const void*);
            MsvcStdFunc64* fn_slot = reinterpret_cast<MapIdxFn>(static_cast<uintptr_t>(idx_fn))(map, &key);
            destroy_msvc_ws_unguarded(c->bid, c->base, key);
            if (!fn_slot || !ptr_readable_p4(fn_slot, sizeof(MsvcStdFunc64)) || !fn_slot->impl) return nullptr;
            void** vtbl = nullptr;
            if (!read_mem_p4(fn_slot->impl, 0, vtbl) || !vtbl || !ptr_readable_p4(vtbl, 3 * sizeof(void*)) || !vtbl[2]) {
                return nullptr;
            }
            return reinterpret_cast<Create1Fn>(vtbl[2])(fn_slot->impl);
        };

        bool already_attached = false;
        void* ctrl = nullptr;
        switch (c->kind) {
            case NM_UI_CTRL_FRAME: {
                const uint64_t pool_addr = sym_addr_p4(c->bid, c->base, "ui.control_pool");
                const uint64_t fc_addr   = sym_addr_p4(c->bid, c->base, "ui.frame_create");
                if (pool_addr && fc_addr) {
                    void* pool = reinterpret_cast<PoolFn>(static_cast<uintptr_t>(pool_addr))();
                    if (pool) ctrl = reinterpret_cast<Create1Fn>(static_cast<uintptr_t>(fc_addr))(pool);
                }
                break;
            }
            case NM_UI_CTRL_LABEL:
                ctrl = call_factory("ui.label_factory");
                break;
            case NM_UI_CTRL_BUTTON:
                ctrl = call_factory("ui.button_factory");
                break;
            case NM_UI_CTRL_TEXTBOX:
                ctrl = call_map_factory(L"TextBox");
                break;
            case NM_UI_CTRL_CHECKBOX:
                if (c->parent) {
                    const uint64_t cb_addr = sym_addr_p4(c->bid, c->base, "ui.checkbox_create_child");
                    if (cb_addr != 0) {
                        const size_t ch_off = off_or_p4(c->bid, "ui.control.children_container", 0xB8u);
                        ctrl = reinterpret_cast<Create1Fn>(static_cast<uintptr_t>(cb_addr))(
                            static_cast<uint8_t*>(c->parent) + ch_off);
                        already_attached = (ctrl != nullptr);
                    }
                } else {
                    ctrl = call_map_factory(L"CheckBox");
                }
                break;
            case NM_UI_CTRL_PICTURE:
                ctrl = call_factory("ui.picture_factory");
                break;
            case NM_UI_CTRL_STACK_LAYOUT:
                ctrl = call_factory("ui.stack_layout_factory");
                break;
            case NM_UI_CTRL_SCROLLABLE_FRAME:
                ctrl = call_factory("ui.scrollable_frame_factory");
                break;
            case NM_UI_CTRL_TAB_CONTAINER:
                ctrl = call_factory("ui.tab_container_factory");
                break;
            case NM_UI_CTRL_COMBOBOX:
                ctrl = call_factory("ui.combobox_factory");
                break;
            case NM_UI_CTRL_LISTBOX:
                ctrl = call_factory("ui.control_listbox_factory");
                break;
            default:
                return -1;
        }
        if (!ctrl) return -2;

        if (!c->id_w.empty()) {
            const size_t id_off = off_or_p4(c->bid, "ui.control.id", 0x1B8u);
            const uint64_t assign_addr = sym_addr_p4(c->bid, c->base, "runtime.wstring_assign");
            if (assign_addr != 0 && ptr_readable_p4(static_cast<uint8_t*>(ctrl) + id_off, sizeof(MsvcWString))) {
                using AssignFn = void*(NM_CALL*)(void*, const wchar_t*, uint64_t);
                reinterpret_cast<AssignFn>(static_cast<uintptr_t>(assign_addr))(
                    static_cast<uint8_t*>(ctrl) + id_off, c->id_w.c_str(), static_cast<uint64_t>(c->id_w.size()));
            }
        }

        if (c->parent && !already_attached) {
            attach_child_unguarded(c->bid, c->base, c->parent, ctrl);
        }

        c->created = ctrl;
        return 0;
    };

    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK || rc != 0 || !ctx.created) return (st != NM_OK) ? st : NM_ERR_GAME_EXCEPTION;
    *out_control = ctx.created;
    return NM_OK;
#else
    (void)parent_control;
    (void)id_utf8;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase4Services::ui_picture_set_texture(void* picture_control, const char* texture_path_utf8) const {
    if (!picture_control || !texture_path_utf8 || texture_path_utf8[0] == '\0') return NM_ERR_INVALID_ARG;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const uint64_t from_path_addr = sym_addr_p4(build_id_, image_base_, "ui.image_handle_from_path");
    const uint64_t set_img_addr   = sym_addr_p4(build_id_, image_base_, "ui.picture_set_image");
    const uint64_t rel_addr       = sym_addr_p4(build_id_, image_base_, "ui.image_handle_release");
    const uint64_t img_vt         = sym_addr_p4(build_id_, image_base_, "ui.vtable_image_handle");
    if (from_path_addr == 0 || set_img_addr == 0 || img_vt == 0) return NM_ERR_SYMBOL_NOT_FOUND;

    struct Ctx {
        void*        pic;
        size_t       iface_off;
        uint64_t     from_path;
        uint64_t     set_img;
        uint64_t     rel_img;
        uint64_t     img_vt;
        std::wstring path_w;
    } ctx{
        picture_control,
        off_or_p4(build_id_, "ui.picture.image_iface", 0x220u),
        from_path_addr,
        set_img_addr,
        rel_addr,
        img_vt,
        utf8_to_wide_p4(texture_path_utf8),
    };

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        if (!ptr_readable_p4(static_cast<const uint8_t*>(c->pic) + c->iface_off, sizeof(void*))) return -1;
        ImageHandle16 h{c->img_vt, nullptr};
        using FromPathFn = ImageHandle16*(NM_CALL*)(ImageHandle16*, const wchar_t*);
        using SetImgFn   = void(NM_CALL*)(void*, ImageHandle16*, uint8_t);
        using RelFn      = void(NM_CALL*)(void*);
        reinterpret_cast<FromPathFn>(static_cast<uintptr_t>(c->from_path))(&h, c->path_w.c_str());
        // NOTE #129: Picture::SetImage expects rcx = pic + 0x220 (image interface subobject)!
        reinterpret_cast<SetImgFn>(static_cast<uintptr_t>(c->set_img))(
            static_cast<uint8_t*>(c->pic) + c->iface_off, &h, 0u);
        if (h.img_obj != nullptr && c->rel_img != 0) {
            reinterpret_cast<RelFn>(static_cast<uintptr_t>(c->rel_img))(h.img_obj);
        }
        return 0;
    };

    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    return (st == NM_OK && rc == 0) ? NM_OK : ((st != NM_OK) ? st : NM_ERR_GAME_EXCEPTION);
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase4Services::ui_scrollable_get_scroll(void* ctrl, float* out_offset, float* out_max) const {
    if (!ctrl || !out_offset || !out_max) return NM_ERR_INVALID_ARG;
    *out_offset = 0.0f;
    *out_max = 0.0f;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    struct Ctx {
        nm_build_id_t bid;
        void*         ctrl;
        float         offset = 0.0f;
        float         max_val = 0.0f;
    } ctx{build_id_, ctrl, 0.0f, 0.0f};

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        void* v_sb = nullptr;
        if (!read_mem_p4(c->ctrl, off_or_p4(c->bid, "ui.scrollable_frame.v_scrollbar", 0x2E8u), v_sb)) return -1;
        if (!v_sb) return 0;
        uint64_t val = 0;
        uint64_t mx = 0;
        (void)read_mem_p4(v_sb, off_or_p4(c->bid, "ui.scrollbar.value", 0x368u), val);
        (void)read_mem_p4(v_sb, off_or_p4(c->bid, "ui.scrollbar.max", 0x390u), mx);
        c->offset = static_cast<float>(val);
        c->max_val = static_cast<float>(mx);
        return 0;
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK || rc != 0) return (st != NM_OK) ? st : NM_ERR_GAME_EXCEPTION;
    *out_offset = ctx.offset;
    *out_max = ctx.max_val;
    return NM_OK;
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase4Services::ui_scrollable_set_scroll(void* ctrl, float offset) const {
    if (!ctrl) return NM_ERR_INVALID_ARG;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const uint64_t set_val_addr = sym_addr_p4(build_id_, image_base_, "ui.scrollbar_set_value");
    const uint64_t upd_addr     = sym_addr_p4(build_id_, image_base_, "ui.scrollable_frame_update_layout");
    if (set_val_addr == 0) return NM_ERR_SYMBOL_NOT_FOUND;

    struct Ctx {
        nm_build_id_t bid;
        void*         ctrl;
        uint64_t      set_val;
        uint64_t      upd_layout;
        uint64_t      pos;
    } ctx{
        build_id_,
        ctrl,
        set_val_addr,
        upd_addr,
        (offset > 0.0f) ? static_cast<uint64_t>(offset) : 0ull,
    };
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        void* v_sb = nullptr;
        if (!read_mem_p4(c->ctrl, off_or_p4(c->bid, "ui.scrollable_frame.v_scrollbar", 0x2E8u), v_sb)) return -1;
        if (v_sb) {
            using SetValFn = void(NM_CALL*)(void*, uint64_t);
            reinterpret_cast<SetValFn>(static_cast<uintptr_t>(c->set_val))(v_sb, c->pos);
        }
        if (c->upd_layout != 0) {
            using UpdFn = void(NM_CALL*)(void*);
            reinterpret_cast<UpdFn>(static_cast<uintptr_t>(c->upd_layout))(c->ctrl);
        }
        return 0;
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    return (st == NM_OK && rc == 0) ? NM_OK : ((st != NM_OK) ? st : NM_ERR_GAME_EXCEPTION);
#else
    (void)offset;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase4Services::ui_scrollable_get_content(void* ctrl, void** out_content) const {
    if (!ctrl || !out_content) return NM_ERR_INVALID_ARG;
    // Per #129: ScrollableFrame content children live directly in Control+0xb8 (skipping scrollbars).
    *out_content = ctrl;
    return NM_OK;
}

nm_status_t Phase4Services::ui_tab_container_get_count(void* ctrl, uint32_t* out_count) const {
    if (!ctrl || !out_count) return NM_ERR_INVALID_ARG;
    *out_count = 0;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    struct Ctx {
        nm_build_id_t bid;
        void*         ctrl;
        uint32_t      count = 0;
    } ctx{build_id_, ctrl, 0};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        uint8_t* begin = nullptr;
        uint8_t* end = nullptr;
        if (!read_mem_p4(c->ctrl, off_or_p4(c->bid, "ui.tab_container.tabs_begin", 0x308u), begin) ||
            !read_mem_p4(c->ctrl, off_or_p4(c->bid, "ui.tab_container.tabs_end", 0x310u), end)) {
            return -1;
        }
        if (!begin || !end || end < begin) return 0;
        const size_t n = static_cast<size_t>(end - begin) / kTabEntryStride;
        if (n > 1024u) return -2;
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

nm_status_t Phase4Services::ui_tab_container_get_active(void* ctrl, int32_t* out_index) const {
    if (!ctrl || !out_index) return NM_ERR_INVALID_ARG;
    *out_index = -1;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    struct Ctx {
        nm_build_id_t bid;
        void*         ctrl;
        int32_t       active = -1;
    } ctx{build_id_, ctrl, -1};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        uint8_t has_val = 0;
        uint64_t idx = 0;
        if (!read_mem_p4(c->ctrl, off_or_p4(c->bid, "ui.tab_container.active_tab_has_value", 0x328u), has_val)) {
            return -1;
        }
        if (has_val != 0 && read_mem_p4(c->ctrl, off_or_p4(c->bid, "ui.tab_container.active_tab_index", 0x320u), idx)) {
            c->active = static_cast<int32_t>(idx);
        }
        return 0;
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK || rc != 0) return (st != NM_OK) ? st : NM_ERR_GAME_EXCEPTION;
    *out_index = ctx.active;
    return NM_OK;
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase4Services::ui_tab_container_select(void* ctrl, int32_t index) const {
    if (!ctrl || index < 0) return NM_ERR_INVALID_ARG;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const uint64_t sel_addr = sym_addr_p4(build_id_, image_base_, "ui.tab_container_select");
    if (sel_addr == 0) return NM_ERR_SYMBOL_NOT_FOUND;

    struct Ctx {
        void*    ctrl;
        uint64_t fn_addr;
        uint64_t idx;
    } ctx{ctrl, sel_addr, static_cast<uint64_t>(index)};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        if (!ptr_readable_p4(c->ctrl, 0x330u)) return -1;
        using SelFn = void(NM_CALL*)(void*, uint64_t);
        reinterpret_cast<SelFn>(static_cast<uintptr_t>(c->fn_addr))(c->ctrl, c->idx);
        return 0;
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    return (st == NM_OK && rc == 0) ? NM_OK : ((st != NM_OK) ? st : NM_ERR_GAME_EXCEPTION);
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase4Services::ui_tab_container_add_tab(
    void*       ctrl,
    const char* label_utf8,
    uint32_t    style,
    int32_t     hidden,
    void**      out_page) const {
    if (!ctrl || !label_utf8 || !out_page) return NM_ERR_INVALID_ARG;
    *out_page = nullptr;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const uint64_t add_addr = sym_addr_p4(build_id_, image_base_, "ui.tab_container_add_tab");
    const uint64_t img_vt   = sym_addr_p4(build_id_, image_base_, "ui.vtable_image_handle");
    if (add_addr == 0 || img_vt == 0) return NM_ERR_SYMBOL_NOT_FOUND;

    struct Ctx {
        nm_build_id_t bid;
        uint64_t      base;
        void*         ctrl;
        uint64_t      add_addr;
        uint64_t      img_vt;
        std::wstring  label_w;
        uint32_t      style;
        uint8_t       hidden;
        void*         page = nullptr;
    } ctx{
        build_id_,
        image_base_,
        ctrl,
        add_addr,
        img_vt,
        utf8_to_wide_p4(label_utf8),
        style,
        static_cast<uint8_t>(hidden ? 1 : 0),
        nullptr,
    };

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        if (!ptr_readable_p4(c->ctrl, 0x330u)) return -1;

        alignas(16) MsvcWString label_ws{};
        init_msvc_ws_unguarded(c->bid, c->base, label_ws, c->label_w);

        // CRITICAL #131 6-arg Win64 ABI:
        //   rcx = tc, rdx = &label_ws (destroyed by callee!), r8d = style, r9 = 0 (unused),
        //   [rsp+0x20] = non-null 16B ImageHandle { vtable_image_handle, nullptr }, [rsp+0x28] = hidden
        ImageHandle16 opt_img{c->img_vt, nullptr};
        using AddTabFn = void*(NM_CALL*)(void*, MsvcWString*, uint32_t, uint64_t, ImageHandle16*, uint8_t);
        c->page = reinterpret_cast<AddTabFn>(static_cast<uintptr_t>(c->add_addr))(
            c->ctrl, &label_ws, c->style, 0ull, &opt_img, c->hidden);
        return c->page ? 0 : -2;
    };

    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK || rc != 0 || !ctx.page) return (st != NM_OK) ? st : NM_ERR_GAME_EXCEPTION;
    *out_page = ctx.page;
    return NM_OK;
#else
    (void)style;
    (void)hidden;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase4Services::ui_tab_container_subscribe_changed(
    void*                      ctrl,
    nm_ui_int_event_callback_t cb,
    void*                      user_data,
    uint64_t*                  out_sub) {
    if (!ctrl || !cb || !out_sub) return NM_ERR_INVALID_ARG;
    const uint64_t tok = kPhase4UiTokenTag | g_next_ui_int_token.fetch_add(1u, std::memory_order_relaxed);
    {
        std::lock_guard<std::mutex> lock(g_ui_int_sub_mutex);
        g_ui_int_subs.push_back(UiIntSubEntry{tok, NM_UI_CTRL_TAB_CONTAINER, ctrl, cb, user_data});
        g_ui_int_sub_count.store(g_ui_int_subs.size(), std::memory_order_release);
    }
    *out_sub = tok;
    return NM_OK;
}

nm_status_t Phase4Services::ui_combobox_get_count(void* ctrl, uint32_t* out_count) const {
    if (!ctrl || !out_count) return NM_ERR_INVALID_ARG;
    *out_count = 0;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    struct Ctx {
        nm_build_id_t bid;
        void*         ctrl;
        uint32_t      count = 0;
    } ctx{build_id_, ctrl, 0};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        uint8_t* begin = nullptr;
        uint8_t* end = nullptr;
        if (!read_mem_p4(c->ctrl, off_or_p4(c->bid, "ui.combobox.items_begin", 0x658u), begin) ||
            !read_mem_p4(c->ctrl, off_or_p4(c->bid, "ui.combobox.items_end", 0x660u), end)) {
            return -1;
        }
        if (!begin || !end || end < begin) return 0;
        const size_t n = static_cast<size_t>(end - begin) / kComboBoxItemStride;
        if (n > 8192u) return -2;
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

nm_status_t Phase4Services::ui_combobox_get_selected(void* ctrl, int32_t* out_index) const {
    if (!ctrl || !out_index) return NM_ERR_INVALID_ARG;
    *out_index = -1;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    struct Ctx {
        nm_build_id_t bid;
        void*         ctrl;
        int32_t       sel = -1;
    } ctx{build_id_, ctrl, -1};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        uint8_t has_val = 0;
        uint64_t idx = 0;
        if (!read_mem_p4(c->ctrl, off_or_p4(c->bid, "ui.combobox.selected_has_value", 0x4F0u), has_val)) return -1;
        if (has_val != 0 && read_mem_p4(c->ctrl, off_or_p4(c->bid, "ui.combobox.selected_index", 0x4E8u), idx)) {
            c->sel = static_cast<int32_t>(idx);
        }
        return 0;
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK || rc != 0) return (st != NM_OK) ? st : NM_ERR_GAME_EXCEPTION;
    *out_index = ctx.sel;
    return NM_OK;
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase4Services::ui_combobox_set_selected(void* ctrl, int32_t index) const {
    if (!ctrl || index < 0) return NM_ERR_INVALID_ARG;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const uint64_t fn_addr = sym_addr_p4(build_id_, image_base_, "ui.combobox_set_selected_index");
    if (fn_addr == 0) return NM_ERR_SYMBOL_NOT_FOUND;

    struct Ctx {
        void*    ctrl;
        uint64_t fn_addr;
        uint64_t idx;
    } ctx{ctrl, fn_addr, static_cast<uint64_t>(index)};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        if (!ptr_readable_p4(c->ctrl, 0x510u)) return -1;
        using SetSelFn = uint8_t(NM_CALL*)(void*, uint64_t);
        reinterpret_cast<SetSelFn>(static_cast<uintptr_t>(c->fn_addr))(c->ctrl, c->idx);
        return 0;
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    return (st == NM_OK && rc == 0) ? NM_OK : ((st != NM_OK) ? st : NM_ERR_GAME_EXCEPTION);
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase4Services::ui_combobox_add_item(
    void*       ctrl,
    const char* text_utf8,
    int32_t     enabled,
    uint32_t    tag,
    uint32_t*   out_index) const {
    if (!ctrl || !text_utf8) return NM_ERR_INVALID_ARG;
    if (out_index) *out_index = 0;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const uint64_t add_addr = sym_addr_p4(build_id_, image_base_, "ui.combobox_add_item");
    if (add_addr == 0) return NM_ERR_SYMBOL_NOT_FOUND;

    struct Ctx {
        nm_build_id_t bid;
        uint64_t      base;
        void*         ctrl;
        size_t        iface_off;
        uint64_t      add_addr;
        std::wstring  text_w;
        uint8_t       enabled;
        uint32_t      tag;
        uint32_t      out_idx = 0;
    } ctx{
        build_id_,
        image_base_,
        ctrl,
        off_or_p4(build_id_, "ui.combobox.items_iface", 0x418u),
        add_addr,
        utf8_to_wide_p4(text_utf8),
        static_cast<uint8_t>(enabled ? 1 : 0),
        tag,
        0,
    };

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        if (!ptr_readable_p4(static_cast<const uint8_t*>(c->ctrl) + c->iface_off, sizeof(void*))) return -1;
        alignas(16) MsvcWString ws{};
        init_msvc_ws_unguarded(c->bid, c->base, ws, c->text_w);
        alignas(16) MsvcStdFunc64 empty_cb{};
        std::memset(&empty_cb, 0, sizeof(empty_cb));
        // NOTE #129: ComboBox::AddItem takes rcx = cb + 0x418!
        using AddItemFn = uint64_t(NM_CALL*)(void*, const MsvcWString*, const MsvcStdFunc64*, uint8_t, uint32_t);
        const uint64_t idx = reinterpret_cast<AddItemFn>(static_cast<uintptr_t>(c->add_addr))(
            static_cast<uint8_t*>(c->ctrl) + c->iface_off, &ws, &empty_cb, c->enabled, c->tag);
        destroy_msvc_ws_unguarded(c->bid, c->base, ws);
        c->out_idx = static_cast<uint32_t>(idx);
        return 0;
    };

    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK || rc != 0) return (st != NM_OK) ? st : NM_ERR_GAME_EXCEPTION;
    if (out_index) *out_index = ctx.out_idx;
    return NM_OK;
#else
    (void)enabled;
    (void)tag;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase4Services::ui_combobox_clear(void* ctrl) const {
    if (!ctrl) return NM_ERR_INVALID_ARG;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const uint64_t clr_addr = sym_addr_p4(build_id_, image_base_, "ui.combobox_clear_items");
    if (clr_addr == 0) return NM_ERR_SYMBOL_NOT_FOUND;

    struct Ctx {
        void*    ctrl;
        size_t   iface_off;
        uint64_t clr_addr;
    } ctx{ctrl, off_or_p4(build_id_, "ui.combobox.items_iface", 0x418u), clr_addr};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        if (!ptr_readable_p4(static_cast<const uint8_t*>(c->ctrl) + c->iface_off, sizeof(void*))) return -1;
        // NOTE #129: ComboBox::ClearItems takes rcx = cb + 0x418!
        using ClrFn = void(NM_CALL*)(void*);
        reinterpret_cast<ClrFn>(static_cast<uintptr_t>(c->clr_addr))(static_cast<uint8_t*>(c->ctrl) + c->iface_off);
        return 0;
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    return (st == NM_OK && rc == 0) ? NM_OK : ((st != NM_OK) ? st : NM_ERR_GAME_EXCEPTION);
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase4Services::ui_combobox_subscribe_changed(
    void*                      ctrl,
    nm_ui_int_event_callback_t cb,
    void*                      user_data,
    uint64_t*                  out_sub) {
    if (!ctrl || !cb || !out_sub) return NM_ERR_INVALID_ARG;
    const uint64_t tok = kPhase4UiTokenTag | g_next_ui_int_token.fetch_add(1u, std::memory_order_relaxed);
    {
        std::lock_guard<std::mutex> lock(g_ui_int_sub_mutex);
        g_ui_int_subs.push_back(UiIntSubEntry{tok, NM_UI_CTRL_COMBOBOX, ctrl, cb, user_data});
        g_ui_int_sub_count.store(g_ui_int_subs.size(), std::memory_order_release);
    }
    *out_sub = tok;
    return NM_OK;
}

nm_status_t Phase4Services::ui_listbox_get_count(void* ctrl, uint32_t* out_count) const {
    if (!ctrl || !out_count) return NM_ERR_INVALID_ARG;
    *out_count = 0;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    struct Ctx {
        nm_build_id_t bid;
        void*         ctrl;
        uint32_t      count = 0;
    } ctx{build_id_, ctrl, 0};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        uint8_t* begin = nullptr;
        uint8_t* end = nullptr;
        if (!read_mem_p4(c->ctrl, off_or_p4(c->bid, "ui.control_listbox.items_begin", 0x4A8u), begin) ||
            !read_mem_p4(c->ctrl, off_or_p4(c->bid, "ui.control_listbox.items_end", 0x4B0u), end)) {
            return -1;
        }
        if (!begin || !end || end < begin) return 0;
        const size_t n = static_cast<size_t>(end - begin) / kListBoxItemStride;
        if (n > 8192u) return -2;
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

nm_status_t Phase4Services::ui_listbox_get_selected(void* ctrl, int32_t* out_index) const {
    if (!ctrl || !out_index) return NM_ERR_INVALID_ARG;
    *out_index = -1;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    struct Ctx {
        nm_build_id_t bid;
        void*         ctrl;
        int32_t       sel = -1;
    } ctx{build_id_, ctrl, -1};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        uint8_t has_val = 0;
        uint64_t idx = 0;
        if (!read_mem_p4(c->ctrl, off_or_p4(c->bid, "ui.control_listbox.selected_has_value", 0x498u), has_val)) {
            return -1;
        }
        if (has_val != 0 && read_mem_p4(c->ctrl, off_or_p4(c->bid, "ui.control_listbox.selected_index", 0x490u), idx)) {
            c->sel = static_cast<int32_t>(idx);
        }
        return 0;
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK || rc != 0) return (st != NM_OK) ? st : NM_ERR_GAME_EXCEPTION;
    *out_index = ctx.sel;
    return NM_OK;
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase4Services::ui_listbox_set_selected(void* ctrl, int32_t index) const {
    if (!ctrl) return NM_ERR_INVALID_ARG;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const uint64_t set_addr = sym_addr_p4(build_id_, image_base_, "ui.control_listbox_set_selected_index");
    const uint64_t clr_addr = sym_addr_p4(build_id_, image_base_, "ui.control_listbox_clear_selection");
    if (index < 0 && clr_addr == 0) return NM_ERR_SYMBOL_NOT_FOUND;
    if (index >= 0 && set_addr == 0) return NM_ERR_SYMBOL_NOT_FOUND;

    struct Ctx {
        void*    ctrl;
        uint64_t set_addr;
        uint64_t clr_addr;
        int32_t  index;
    } ctx{ctrl, set_addr, clr_addr, index};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        if (!ptr_readable_p4(c->ctrl, 0x4B8u)) return -1;
        if (c->index < 0) {
            using ClrFn = void(NM_CALL*)(void*);
            reinterpret_cast<ClrFn>(static_cast<uintptr_t>(c->clr_addr))(c->ctrl);
        } else {
            using SetFn = uint8_t(NM_CALL*)(void*, uint64_t);
            reinterpret_cast<SetFn>(static_cast<uintptr_t>(c->set_addr))(c->ctrl, static_cast<uint64_t>(c->index));
        }
        return 0;
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    return (st == NM_OK && rc == 0) ? NM_OK : ((st != NM_OK) ? st : NM_ERR_GAME_EXCEPTION);
#else
    (void)index;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase4Services::ui_listbox_insert_item(
    void*    ctrl,
    uint32_t index,
    void*    child_control,
    uint64_t item_user_data) const {
    if (!ctrl || !child_control) return NM_ERR_INVALID_ARG;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const uint64_t ins_addr = sym_addr_p4(build_id_, image_base_, "ui.control_listbox_insert_item");
    if (ins_addr == 0) return NM_ERR_SYMBOL_NOT_FOUND;

    struct Ctx {
        void*    ctrl;
        uint64_t ins_addr;
        uint64_t index;
        void*    child;
        uint64_t user_data;
    } ctx{ctrl, ins_addr, static_cast<uint64_t>(index), child_control, item_user_data};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        if (!ptr_readable_p4(c->ctrl, 0x4B8u)) return -1;
        // CRITICAL #131 4-arg Win64 ABI: (lb, index, child, userData)
        using InsFn = uint8_t(NM_CALL*)(void*, uint64_t, void*, uint64_t);
        reinterpret_cast<InsFn>(static_cast<uintptr_t>(c->ins_addr))(c->ctrl, c->index, c->child, c->user_data);
        return 0;
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    return (st == NM_OK && rc == 0) ? NM_OK : ((st != NM_OK) ? st : NM_ERR_GAME_EXCEPTION);
#else
    (void)index;
    (void)item_user_data;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase4Services::ui_listbox_clear(void* ctrl) const {
    if (!ctrl) return NM_ERR_INVALID_ARG;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const uint64_t clr_addr = sym_addr_p4(build_id_, image_base_, "ui.control_listbox_clear_items");
    if (clr_addr == 0) return NM_ERR_SYMBOL_NOT_FOUND;

    struct Ctx {
        void*    ctrl;
        uint64_t clr_addr;
    } ctx{ctrl, clr_addr};
    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        if (!ptr_readable_p4(c->ctrl, 0x4B8u)) return -1;
        using ClrFn = void(NM_CALL*)(void*);
        reinterpret_cast<ClrFn>(static_cast<uintptr_t>(c->clr_addr))(c->ctrl);
        return 0;
    };
    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    return (st == NM_OK && rc == 0) ? NM_OK : ((st != NM_OK) ? st : NM_ERR_GAME_EXCEPTION);
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase4Services::ui_listbox_subscribe_changed(
    void*                      ctrl,
    nm_ui_int_event_callback_t cb,
    void*                      user_data,
    uint64_t*                  out_sub) {
    if (!ctrl || !cb || !out_sub) return NM_ERR_INVALID_ARG;
    const uint64_t tok = kPhase4UiTokenTag | g_next_ui_int_token.fetch_add(1u, std::memory_order_relaxed);
    {
        std::lock_guard<std::mutex> lock(g_ui_int_sub_mutex);
        g_ui_int_subs.push_back(UiIntSubEntry{tok, NM_UI_CTRL_LISTBOX, ctrl, cb, user_data});
        g_ui_int_sub_count.store(g_ui_int_subs.size(), std::memory_order_release);
    }
    *out_sub = tok;
    return NM_OK;
}

bool Phase4Services::try_ui_unsubscribe(uint64_t token) {
    if ((token & kPhase4UiTokenTag) == 0) return false;
    std::lock_guard<std::mutex> lock(g_ui_int_sub_mutex);
    const auto it = std::remove_if(g_ui_int_subs.begin(), g_ui_int_subs.end(),
                                   [token](const UiIntSubEntry& e) { return e.token == token; });
    if (it == g_ui_int_subs.end()) return false;
    g_ui_int_subs.erase(it, g_ui_int_subs.end());
    g_ui_int_sub_count.store(g_ui_int_subs.size(), std::memory_order_release);
    return true;
}

// ============================================================================
// Stock Item Tooltip Augmentation (0.5.0)
// ============================================================================

nm_status_t Phase4Services::tooltip_set_item_augment_callback(nm_tooltip_augment_callback_t cb, void* user_data) {
    std::lock_guard<std::mutex> lock(g_p4_cb_mutex);
    g_tooltip_cb = cb;
    g_tooltip_ud = cb ? user_data : nullptr;
    g_has_tooltip_cb.store(cb != nullptr, std::memory_order_release);
    return NM_OK;
}

nm_status_t Phase4Services::tooltip_add_item_section(
    void*       popup_control,
    const char* text_utf8,
    uint32_t    rgba,
    int32_t     add_separator) const {
    if (!popup_control || !text_utf8) return NM_ERR_INVALID_ARG;
#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || image_base_ == 0) return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    const uint64_t lbl_fac  = sym_addr_p4(build_id_, image_base_, "ui.label_factory");
    const uint64_t sep_fn   = sym_addr_p4(build_id_, image_base_, "ui.item_popup_create_separator");
    const uint64_t layout_fn = sym_addr_p4(build_id_, image_base_, "ui.layout_update");
    if (lbl_fac == 0) return NM_ERR_SYMBOL_NOT_FOUND;

    struct Ctx {
        nm_build_id_t bid;
        uint64_t      base;
        void*         popup;
        uint64_t      lbl_fac;
        uint64_t      sep_fn;
        uint64_t      layout_fn;
        std::wstring  text_w;
        uint32_t      rgba;
        int32_t       add_sep;
    } ctx{
        build_id_,
        image_base_,
        popup_control,
        lbl_fac,
        sep_fn,
        layout_fn,
        utf8_to_wide_p4(text_utf8),
        rgba,
        add_separator,
    };

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        void* body = nullptr;
        if (!read_mem_p4(c->popup, off_or_p4(c->bid, "ui.item_popup.body_container", 0x3E8u), body) || !body) {
            body = c->popup;
        }
        if (c->add_sep != 0 && c->sep_fn != 0) {
            using CreateSepFn = void*(NM_CALL*)(uint32_t, uint8_t);
            void* sep = reinterpret_cast<CreateSepFn>(static_cast<uintptr_t>(c->sep_fn))(0u, 0u);
            if (sep) attach_child_unguarded(c->bid, c->base, body, sep);
        }
        using Factory0Fn = void*(NM_CALL*)(void*);
        void* lbl = reinterpret_cast<Factory0Fn>(static_cast<uintptr_t>(c->lbl_fac))(nullptr);
        if (!lbl) return -2;

        // Label text interface at +0x298 (vtbl[2](sub, &ws), destroys ws internally)
        const size_t text_iface_off = off_or_p4(c->bid, "ui.label.text_interface", 0x298u);
        void* sub = static_cast<uint8_t*>(lbl) + text_iface_off;
        void** svt = nullptr;
        if (read_mem_p4(sub, 0, svt) && svt && ptr_readable_p4(svt, 3 * sizeof(void*)) && svt[2]) {
            alignas(16) MsvcWString ws{};
            init_msvc_ws_unguarded(c->bid, c->base, ws, c->text_w);
            using SetTextFn = void(NM_CALL*)(void*, const void*);
            reinterpret_cast<SetTextFn>(svt[2])(sub, &ws);
        }
        (void)c->rgba;
        attach_child_unguarded(c->bid, c->base, body, lbl);

        if (c->layout_fn != 0) {
            const size_t layout_off = off_or_p4(c->bid, "ui.control.layout", 0x220u);
            using LayoutUpdFn = void(NM_CALL*)(void*);
            reinterpret_cast<LayoutUpdFn>(static_cast<uintptr_t>(c->layout_fn))(
                static_cast<uint8_t*>(body) + layout_off);
        }
        return 0;
    };

    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    return (st == NM_OK && rc == 0) ? NM_OK : ((st != NM_OK) ? st : NM_ERR_GAME_EXCEPTION);
#else
    (void)rgba;
    (void)add_separator;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

// ============================================================================
// Custom Stash Affinity Router (0.5.0)
// ============================================================================

nm_status_t Phase4Services::affinity_set_router_callback(nm_affinity_route_callback_t cb, void* user_data) {
    std::lock_guard<std::mutex> lock(g_p4_cb_mutex);
    g_affinity_cb = cb;
    g_affinity_ud = cb ? user_data : nullptr;
    g_has_affinity_cb.store(cb != nullptr, std::memory_order_release);
    return NM_OK;
}

// ============================================================================
// Native Extend Registries (0.5.0)
// ============================================================================

nm_status_t Phase4Services::geal_register_builtin(
    const char*                name_utf8,
    uint32_t                   min_args,
    uint32_t                   max_args,
    int32_t                    has_return,
    nm_geal_builtin_callback_t cb,
    void*                      user_data,
    uint64_t*                  out_handle) {
    if (!name_utf8 || name_utf8[0] == '\0' || !cb || !out_handle || min_args > max_args) {
        return NM_ERR_INVALID_ARG;
    }
    *out_handle = 0;

    const std::string name_str(name_utf8);
    const std::wstring name_w = utf8_to_wide_p4(name_utf8);
    size_t chosen_idx = kMaxGealBuiltins;
    uint64_t handle = 0;

    {
        std::lock_guard<std::mutex> lock(g_geal_mutex);
        for (size_t i = 0; i < kMaxGealBuiltins; ++i) {
            if (g_geal_slots[i].handle != 0 && ascii_iequals(g_geal_slots[i].name_utf8, name_str)) {
                return NM_ERR_ALREADY_EXISTS;
            }
            if (g_geal_slots[i].handle == 0 && chosen_idx == kMaxGealBuiltins) {
                chosen_idx = i;
            }
        }
        if (chosen_idx == kMaxGealBuiltins) {
            return NM_ERR_QUEUE_FULL;
        }
        handle = g_next_geal_handle.fetch_add(1u, std::memory_order_relaxed);
        auto& slot = g_geal_slots[chosen_idx];
        slot.generation.fetch_add(1u, std::memory_order_acq_rel);
        slot.handle = handle;
        slot.name_utf8 = name_str;
        slot.name_w = name_w;
        slot.min_args = min_args;
        slot.max_args = max_args;
        slot.has_return = (has_return != 0);
        slot.cb = cb;
        slot.user_data = user_data;
    }

#if defined(_WIN32)
    if (build_id_ != NM_BUILD_SYNTHETIC && build_id_ != NM_BUILD_UNKNOWN && image_base_ != 0) {
        const uint64_t reg_addr = sym_addr_p4(build_id_, image_base_, "geal.builtin_register_entry");
        if (reg_addr != 0) {
            GealRegisterCtx rctx{
                GealBuiltinEntry{
                    g_geal_slots[chosen_idx].name_w.c_str(),
                    reinterpret_cast<void*>(kGealFactoryThunks[chosen_idx]),
                },
                reg_addr,
            };
            auto fn = [](void* raw) -> int64_t {
                auto* c = static_cast<GealRegisterCtx*>(raw);
                using RegFn = void(NM_CALL*)(const GealBuiltinEntry*, const GealBuiltinEntry*);
                reinterpret_cast<RegFn>(static_cast<uintptr_t>(c->reg_addr))(&c->entry, &c->entry + 1);
                return 0;
            };
            nm_exception_info_t ex{};
            if (invoke_guarded(fn, &rctx, nullptr, &ex) != NM_OK) {
                std::lock_guard<std::mutex> lock(g_geal_mutex);
                auto& slot = g_geal_slots[chosen_idx];
                slot.generation.fetch_add(1u, std::memory_order_acq_rel);
                slot.handle = 0;
                slot.cb = nullptr;
                slot.user_data = nullptr;
                return NM_ERR_GAME_EXCEPTION;
            }
        }
    }
#endif

    *out_handle = handle;
    return NM_OK;
}

nm_status_t Phase4Services::geal_unregister_builtin(uint64_t handle) {
    if (handle == 0) return NM_ERR_INVALID_ARG;

    std::wstring name_w;
    bool found = false;
    {
        std::lock_guard<std::mutex> lock(g_geal_mutex);
        for (size_t i = 0; i < kMaxGealBuiltins; ++i) {
            if (g_geal_slots[i].handle == handle) {
                auto& slot = g_geal_slots[i];
                name_w = slot.name_w;
                // Bump generation FIRST so any in-flight or retained FuncWrapper immediately no-ops!
                slot.generation.fetch_add(1u, std::memory_order_acq_rel);
                slot.handle = 0;
                slot.cb = nullptr;
                slot.user_data = nullptr;
                found = true;
                break;
            }
        }
    }
    if (!found) return NM_ERR_NOT_FOUND;

#if defined(_WIN32)
    if (build_id_ != NM_BUILD_SYNTHETIC && build_id_ != NM_BUILD_UNKNOWN && image_base_ != 0 && !name_w.empty()) {
        const uint64_t table_addr = sym_addr_p4(build_id_, image_base_, "geal.builtin_factory_table");
        if (table_addr != 0) {
            struct EraseCtx {
                uint64_t       table_addr;
                const wchar_t* name_w;
            } ectx{table_addr, name_w.c_str()};
            auto fn = [](void* raw) -> int64_t {
                auto* c = static_cast<EraseCtx*>(raw);
                auto* vec = reinterpret_cast<MsvcVectorRaw*>(static_cast<uintptr_t>(c->table_addr));
                if (!ptr_readable_p4(vec, sizeof(MsvcVectorRaw))) return -1;
                if (!vec->begin || !vec->end || vec->end <= vec->begin) return 0;
                struct GealBuiltinEntryRaw {
                    const wchar_t* name;
                    void*          factory;
                };
                static_assert(sizeof(GealBuiltinEntryRaw) == sizeof(GealBuiltinEntry),
                              "GealBuiltinEntryRaw size mismatch");
                auto* entries = reinterpret_cast<GealBuiltinEntryRaw*>(vec->begin);
                const size_t n = static_cast<size_t>(vec->end - vec->begin) / sizeof(GealBuiltinEntryRaw);
                size_t lo = 0;
                size_t hi = n;
                while (lo < hi) {
                    const size_t mid = lo + (hi - lo) / 2u;
                    const int cmp = entries[mid].name ? std::wcscmp(entries[mid].name, c->name_w) : -1;
                    if (cmp < 0) lo = mid + 1u;
                    else hi = mid;
                }
                if (lo < n && entries[lo].name && std::wcscmp(entries[lo].name, c->name_w) == 0) {
                    const size_t tail = n - lo - 1u;
                    if (tail > 0) {
                        std::memmove(&entries[lo], &entries[lo + 1u], tail * sizeof(GealBuiltinEntryRaw));
                    }
                    vec->end -= sizeof(GealBuiltinEntryRaw);
                }
                return 0;
            };
            nm_exception_info_t ex{};
            (void)invoke_guarded(fn, &ectx, nullptr, &ex);
        }
    }
#endif
    return NM_OK;
}

nm_status_t Phase4Services::geal_invoke_function(
    uint32_t    entity_id,
    const char* script_or_fn_utf8,
    const char* args_json_utf8,
    char*       out_result_utf8,
    size_t      capacity,
    size_t*     out_len) const {
    if (!script_or_fn_utf8 || script_or_fn_utf8[0] == '\0') return NM_ERR_INVALID_ARG;
    if (out_len) *out_len = 0;

    // 1. Check registered custom GEAL builtins first (works identically on Linux/synthetic and Win64!).
    size_t matched_slot = kMaxGealBuiltins;
    uint64_t matched_gen = 0;
    {
        const std::string target(script_or_fn_utf8);
        std::lock_guard<std::mutex> lock(g_geal_mutex);
        for (size_t i = 0; i < kMaxGealBuiltins; ++i) {
            if (g_geal_slots[i].handle != 0 && ascii_iequals(g_geal_slots[i].name_utf8, target)) {
                matched_slot = i;
                matched_gen = g_geal_slots[i].generation.load(std::memory_order_acquire);
                break;
            }
        }
    }
    if (matched_slot < kMaxGealBuiltins) {
        std::string res;
        const nm_status_t st = invoke_geal_slot_guarded(matched_slot, matched_gen, entity_id, args_json_utf8, res);
        if (st != NM_OK) return st;
        if (out_len) *out_len = res.size();
        if (!out_result_utf8 && capacity == 0) return NM_OK;
        if (!out_result_utf8 || capacity < res.size() + 1u) return NM_ERR_BUFFER_TOO_SMALL;
        if (!res.empty()) std::memcpy(out_result_utf8, res.data(), res.size());
        out_result_utf8[res.size()] = '\0';
        return NM_OK;
    }

#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || build_id_ == NM_BUILD_UNKNOWN || image_base_ == 0) {
        return NM_ERR_NOT_FOUND;
    }
    // 2. Live entity Functions { name = { ... } } lookup & invocation (#128).
    const uint64_t get_ic_addr   = sym_addr_p4(build_id_, image_base_, "world.get_instance_client");
    const uint64_t find_ent_addr = sym_addr_p4(build_id_, image_base_, "world.entity_mgr_find_by_id");
    const uint64_t get_ot_addr   = sym_addr_p4(build_id_, image_base_, "items.entity_get_object_type");
    const uint64_t map_lk_addr   = sym_addr_p4(build_id_, image_base_, "components.map_lookup");
    const uint64_t fn_find_addr  = sym_addr_p4(build_id_, image_base_, "geal.functions_find_by_name");
    if (get_ic_addr == 0 || find_ent_addr == 0 || get_ot_addr == 0 || map_lk_addr == 0 || fn_find_addr == 0) {
        return NM_ERR_SYMBOL_NOT_FOUND;
    }

    struct InvokeCtx {
        nm_build_id_t bid;
        uint64_t      base;
        uint32_t      entity_id;
        std::string   fn_name;
        uint64_t      get_ic_addr;
        uint64_t      find_ent_addr;
        uint64_t      get_ot_addr;
        uint64_t      map_lk_addr;
        uint64_t      fn_find_addr;
        std::string   result_json;
    } ictx{
        build_id_, image_base_, entity_id, std::string(script_or_fn_utf8),
        get_ic_addr, find_ent_addr, get_ot_addr, map_lk_addr, fn_find_addr, "null",
    };

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<InvokeCtx*>(raw);
        using GetIcFn = void*(NM_CALL*)();
        void* ic = reinterpret_cast<GetIcFn>(static_cast<uintptr_t>(c->get_ic_addr))();
        if (!ic) return -1;

        void* world = nullptr;
        if (!read_mem_p4(ic, off_or_p4(c->bid, "world.instance_client.client_world", 0xC878u), world) || !world) {
            return -1;
        }
        const size_t em_off = off_or_p4(c->bid, "world.client_world.entity_manager", 0xA18u);
        using FindEntFn = void*(NM_CALL*)(void*, uint32_t);
        void* ent = reinterpret_cast<FindEntFn>(static_cast<uintptr_t>(c->find_ent_addr))(
            static_cast<uint8_t*>(world) + em_off, c->entity_id);
        if (!ent) return -2;

        using GetOtFn = void*(NM_CALL*)(void*);
        void* obj_type = reinterpret_cast<GetOtFn>(static_cast<uintptr_t>(c->get_ot_addr))(
            static_cast<uint8_t*>(ent) + 0x08u);
        if (!obj_type) return -3;

        // Build SSO MSVC std::string("Functions")
        alignas(16) MsvcStdString comp_key{};
        std::memset(&comp_key, 0, sizeof(comp_key));
        std::memcpy(comp_key.buf, "Functions", 9u);
        comp_key.size = 9u;
        comp_key.cap = 15u;

        void* it_node = nullptr;
        using MapLookupFn = void*(NM_CALL*)(void*, void**, const void*);
        reinterpret_cast<MapLookupFn>(static_cast<uintptr_t>(c->map_lk_addr))(
            static_cast<uint8_t*>(obj_type) + 0x28u, &it_node, &comp_key);
        void* map_head = nullptr;
        if (!it_node || !read_mem_p4(static_cast<uint8_t*>(obj_type) + 0x28u, 0, map_head) || it_node == map_head) {
            return -4;
        }
        int32_t comp_idx = -1;
        if (!read_mem_p4(it_node, 0x40u, comp_idx) || comp_idx < 0) return -4;

        void** comp_arr = nullptr;
        if (!read_mem_p4(ent, 0x10u, comp_arr) || !comp_arr ||
            !ptr_readable_p4(comp_arr + comp_idx, sizeof(void*))) {
            return -4;
        }
        void* fn_comp_base = comp_arr[comp_idx];
        if (!fn_comp_base) return -4;
        void* fn_comp = static_cast<uint8_t*>(fn_comp_base) + 0x18u;

        void* fn_table_obj = nullptr;
        if (!read_mem_p4(fn_comp, 0x08u, fn_table_obj) || !fn_table_obj) return -5;

        // Lookup named function entry
        alignas(16) MsvcStdString fn_key{};
        std::memset(&fn_key, 0, sizeof(fn_key));
        if (c->fn_name.size() < 16u) {
            std::memcpy(fn_key.buf, c->fn_name.data(), c->fn_name.size());
            fn_key.size = c->fn_name.size();
            fn_key.cap = 15u;
        } else {
            fn_key.ptr = const_cast<char*>(c->fn_name.c_str());
            fn_key.size = c->fn_name.size();
            fn_key.cap = c->fn_name.size();
        }
        void* fn_it = nullptr;
        void* fn_head = nullptr;
        reinterpret_cast<MapLookupFn>(static_cast<uintptr_t>(c->fn_find_addr))(
            static_cast<uint8_t*>(fn_table_obj) + 0x10u, &fn_it, &fn_key);
        if (!fn_it || !read_mem_p4(static_cast<uint8_t*>(fn_table_obj) + 0x10u, 0, fn_head) || fn_it == fn_head) {
            return -6;
        }

        // Invoke fnComp->vtable[0](fnComp, entry + 0x20, nullptr)
        void** vtbl = nullptr;
        if (!read_mem_p4(fn_comp, 0, vtbl) || !vtbl || !ptr_readable_p4(vtbl, sizeof(void*)) || !vtbl[0]) {
            return -7;
        }
        using FnInvokeFn = void(NM_CALL*)(void*, void*, void*);
        reinterpret_cast<FnInvokeFn>(vtbl[0])(fn_comp, static_cast<uint8_t*>(fn_it) + 0x20u, nullptr);
        c->result_json = "true";
        return 0;
    };

    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ictx, &rc, &ex);
    if (st != NM_OK) return st;
    if (rc != 0) return NM_ERR_NOT_FOUND;

    if (out_len) *out_len = ictx.result_json.size();
    if (!out_result_utf8 && capacity == 0) return NM_OK;
    if (!out_result_utf8 || capacity < ictx.result_json.size() + 1u) return NM_ERR_BUFFER_TOO_SMALL;
    std::memcpy(out_result_utf8, ictx.result_json.data(), ictx.result_json.size());
    out_result_utf8[ictx.result_json.size()] = '\0';
    return NM_OK;
#else
    return NM_ERR_NOT_FOUND;
#endif
}

nm_status_t Phase4Services::ruleset_register_native(
    uint32_t                             ruleset_id,
    const nm_native_ruleset_callbacks_t* callbacks,
    void*                                user_data,
    uint64_t*                            out_handle) {
    if (ruleset_id >= NM_RULESET_SLOT_COUNT || !callbacks || !out_handle) return NM_ERR_INVALID_ARG;
    if (callbacks->struct_size != 0 && callbacks->struct_size != sizeof(nm_native_ruleset_callbacks_t)) {
        return NM_ERR_INVALID_ARG;
    }
    *out_handle = 0;

    // CRITICAL #128/#132: Reject occupied stock slots (including 0xA0!) with NM_ERR_ALREADY_EXISTS!
    if (is_stock_ruleset_slot_occupied(ruleset_id)) {
        return NM_ERR_ALREADY_EXISTS;
    }

    uint64_t handle = 0;
    {
        std::lock_guard<std::mutex> lock(g_ruleset_mutex);
        auto& slot = g_ruleset_slots[ruleset_id];
        if (slot.handle != 0) {
            return NM_ERR_ALREADY_EXISTS;
        }

#if defined(_WIN32)
        // On live Win64 builds, ALSO verify live rulesets.creator_table[ruleset_id] == nullptr!
        if (build_id_ != NM_BUILD_SYNTHETIC && build_id_ != NM_BUILD_UNKNOWN && image_base_ != 0) {
            const uint64_t tbl_addr = sym_addr_p4(build_id_, image_base_, "rulesets.creator_table");
            if (tbl_addr != 0) {
                auto* entry_ptr = reinterpret_cast<void**>(
                    static_cast<uintptr_t>(tbl_addr + static_cast<uint64_t>(ruleset_id) * sizeof(void*)));
                void* existing = nullptr;
                if (!read_mem_p4(entry_ptr, 0, existing)) {
                    return NM_ERR_GAME_EXCEPTION;
                }
                if (existing != nullptr) {
                    return NM_ERR_ALREADY_EXISTS;
                }
                slot.generation.fetch_add(1u, std::memory_order_acq_rel);
                slot.callbacks = *callbacks;
                slot.user_data = user_data;
                handle = g_next_ruleset_handle.fetch_add(1u, std::memory_order_relaxed);
                slot.handle = handle;
                *entry_ptr = reinterpret_cast<void*>(kRulesetCreatorThunks[ruleset_id]);
                slot.installed_in_live_table = true;
                *out_handle = handle;
                return NM_OK;
            }
        }
#endif
        slot.generation.fetch_add(1u, std::memory_order_acq_rel);
        slot.callbacks = *callbacks;
        slot.user_data = user_data;
        handle = g_next_ruleset_handle.fetch_add(1u, std::memory_order_relaxed);
        slot.handle = handle;
        slot.installed_in_live_table = false;
    }

    *out_handle = handle;
    return NM_OK;
}

nm_status_t Phase4Services::ruleset_unregister_native(uint64_t handle) {
    if (handle == 0) return NM_ERR_INVALID_ARG;

    std::lock_guard<std::mutex> lock(g_ruleset_mutex);
    for (uint32_t id = 0; id < NM_RULESET_SLOT_COUNT; ++id) {
        auto& slot = g_ruleset_slots[id];
        if (slot.handle == handle) {
#if defined(_WIN32)
            if (slot.installed_in_live_table && build_id_ != NM_BUILD_SYNTHETIC && image_base_ != 0) {
                const uint64_t tbl_addr = sym_addr_p4(build_id_, image_base_, "rulesets.creator_table");
                if (tbl_addr != 0) {
                    auto* entry_ptr = reinterpret_cast<void**>(
                        static_cast<uintptr_t>(tbl_addr + static_cast<uint64_t>(id) * sizeof(void*)));
                    if (ptr_readable_p4(entry_ptr, sizeof(void*))) {
                        *entry_ptr = nullptr;
                    }
                }
            }
#endif
            // Bump generation FIRST so any live ClientRulesetProxy in client+0xcfd8 safely no-ops!
            slot.generation.fetch_add(1u, std::memory_order_acq_rel);
            slot.handle = 0;
            slot.installed_in_live_table = false;
            std::memset(&slot.callbacks, 0, sizeof(slot.callbacks));
            slot.user_data = nullptr;
            return NM_OK;
        }
    }
    return NM_ERR_NOT_FOUND;
}

nm_status_t Phase4Services::ui_register_custom_control_kind(
    const char*                             kind_name_utf8,
    const nm_custom_ui_control_callbacks_t* callbacks,
    void*                                   user_data,
    uint64_t*                               out_handle) {
    if (!kind_name_utf8 || kind_name_utf8[0] == '\0' || !callbacks || !out_handle) return NM_ERR_INVALID_ARG;
    if (callbacks->struct_size != 0 && callbacks->struct_size != sizeof(nm_custom_ui_control_callbacks_t)) {
        return NM_ERR_INVALID_ARG;
    }
    *out_handle = 0;

    const std::string kind_str(kind_name_utf8);
    const std::wstring kind_w = utf8_to_wide_p4(kind_name_utf8);
    size_t chosen_idx = kMaxCustomUiKinds;
    uint64_t handle = 0;
    uint64_t gen = 0;

    {
        std::lock_guard<std::mutex> lock(g_custom_ui_mutex);
        for (size_t i = 0; i < kMaxCustomUiKinds; ++i) {
            if (g_custom_ui_slots[i].handle != 0 && ascii_iequals(g_custom_ui_slots[i].kind_utf8, kind_str)) {
                return NM_ERR_ALREADY_EXISTS;
            }
            if (g_custom_ui_slots[i].handle == 0 && chosen_idx == kMaxCustomUiKinds) {
                chosen_idx = i;
            }
        }
        if (chosen_idx == kMaxCustomUiKinds) return NM_ERR_QUEUE_FULL;

        auto& slot = g_custom_ui_slots[chosen_idx];
        gen = slot.generation.fetch_add(1u, std::memory_order_acq_rel) + 1u;
        handle = g_next_custom_ui_handle.fetch_add(1u, std::memory_order_relaxed);
        slot.handle = handle;
        slot.kind_utf8 = kind_str;
        slot.kind_w = kind_w;
        slot.callbacks = *callbacks;
        slot.user_data = user_data;
        slot.installed_in_live_map = false;
    }

#if defined(_WIN32)
    if (build_id_ != NM_BUILD_SYNTHETIC && build_id_ != NM_BUILD_UNKNOWN && image_base_ != 0) {
        const uint64_t get_map = sym_addr_p4(build_id_, image_base_, "ui.control_factory_map_get");
        const uint64_t idx_fn  = sym_addr_p4(build_id_, image_base_, "ui.control_factory_map_index");
        if (get_map != 0 && idx_fn != 0) {
            struct RegUiCtx {
                nm_build_id_t bid;
                uint64_t      base;
                uint64_t      get_map;
                uint64_t      idx_fn;
                std::wstring  kind_w;
                size_t        slot_idx;
                uint64_t      gen;
                int32_t       conflict = 0;
            } rctx{build_id_, image_base_, get_map, idx_fn, kind_w, chosen_idx, gen, 0};

            auto fn = [](void* raw) -> int64_t {
                auto* c = static_cast<RegUiCtx*>(raw);
                using GetMapFn = void*(NM_CALL*)();
                using MapIdxFn = MsvcStdFunc64*(NM_CALL*)(void*, const void*);
                void* map = reinterpret_cast<GetMapFn>(static_cast<uintptr_t>(c->get_map))();
                if (!map) return -1;

                alignas(16) MsvcWString key{};
                init_msvc_ws_unguarded(c->bid, c->base, key, c->kind_w);
                MsvcStdFunc64* fn_slot = reinterpret_cast<MapIdxFn>(static_cast<uintptr_t>(c->idx_fn))(map, &key);
                destroy_msvc_ws_unguarded(c->bid, c->base, key);
                if (!fn_slot || !ptr_readable_p4(fn_slot, sizeof(MsvcStdFunc64))) return -2;

                // Refuse to clobber a stock control factory (47 built-ins)!
                if (fn_slot->impl != nullptr) {
                    void** existing_vt = nullptr;
                    if (!read_mem_p4(fn_slot->impl, 0, existing_vt) || existing_vt != g_custom_ui_impl_vtable) {
                        c->conflict = 1;
                        return -3;
                    }
                }
                auto* impl = reinterpret_cast<CustomUiFuncImpl*>(fn_slot->storage);
                impl->vtable = g_custom_ui_impl_vtable;
                impl->slot_index = static_cast<uint64_t>(c->slot_idx);
                impl->expected_gen = c->gen;
                fn_slot->impl = impl;
                return 0;
            };

            int64_t rc = 0;
            nm_exception_info_t ex{};
            const nm_status_t st = invoke_guarded(fn, &rctx, &rc, &ex);
            if (st != NM_OK || rc != 0) {
                std::lock_guard<std::mutex> lock(g_custom_ui_mutex);
                auto& slot = g_custom_ui_slots[chosen_idx];
                slot.generation.fetch_add(1u, std::memory_order_acq_rel);
                slot.handle = 0;
                std::memset(&slot.callbacks, 0, sizeof(slot.callbacks));
                slot.user_data = nullptr;
                return rctx.conflict ? NM_ERR_ALREADY_EXISTS : NM_ERR_GAME_EXCEPTION;
            }
            std::lock_guard<std::mutex> lock(g_custom_ui_mutex);
            g_custom_ui_slots[chosen_idx].installed_in_live_map = true;
        }
    }
#else
    (void)gen;
#endif

    *out_handle = handle;
    return NM_OK;
}

nm_status_t Phase4Services::ui_unregister_custom_control_kind(uint64_t handle) {
    if (handle == 0) return NM_ERR_INVALID_ARG;

    std::wstring kind_w;
    bool was_live = false;
    size_t found_idx = kMaxCustomUiKinds;
    {
        std::lock_guard<std::mutex> lock(g_custom_ui_mutex);
        for (size_t i = 0; i < kMaxCustomUiKinds; ++i) {
            if (g_custom_ui_slots[i].handle == handle) {
                auto& slot = g_custom_ui_slots[i];
                kind_w = slot.kind_w;
                was_live = slot.installed_in_live_map;
                found_idx = i;
                slot.generation.fetch_add(1u, std::memory_order_acq_rel);
                slot.handle = 0;
                slot.installed_in_live_map = false;
                std::memset(&slot.callbacks, 0, sizeof(slot.callbacks));
                slot.user_data = nullptr;
                break;
            }
        }
        if (found_idx == kMaxCustomUiKinds) return NM_ERR_NOT_FOUND;

        for (auto it = g_live_custom_controls.begin(); it != g_live_custom_controls.end();) {
            if (it->second.slot_idx == found_idx) {
                it = g_live_custom_controls.erase(it);
            } else {
                ++it;
            }
        }
        g_live_custom_control_count.store(g_live_custom_controls.size(), std::memory_order_release);
    }

#if defined(_WIN32)
    if (was_live && build_id_ != NM_BUILD_SYNTHETIC && image_base_ != 0 && !kind_w.empty()) {
        const uint64_t get_map = sym_addr_p4(build_id_, image_base_, "ui.control_factory_map_get");
        if (get_map != 0) {
            struct UnlinkCtx {
                nm_build_id_t  bid;
                uint64_t       base;
                uint64_t       get_map;
                const wchar_t* kind_w;
                GameAllocator* alloc;
            } uctx{build_id_, image_base_, get_map, kind_w.c_str(), allocator_};

            auto fn = [](void* raw) -> int64_t {
                auto* c = static_cast<UnlinkCtx*>(raw);
                using GetMapFn = MsvcRbTreeHeader*(NM_CALL*)();
                MsvcRbTreeHeader* tree = reinterpret_cast<GetMapFn>(static_cast<uintptr_t>(c->get_map))();
                if (!tree || !ptr_readable_p4(tree, sizeof(MsvcRbTreeHeader)) || !tree->head) return -1;

                // BST find node matching kind_w (key MsvcWString sits at node + 0x20)
                MsvcRbNodeHeader* cur = tree->head->parent;
                MsvcRbNodeHeader* found = nullptr;
                int guard = 0;
                while (cur && cur->isnil == 0 && guard++ < 256) {
                    const auto* key = reinterpret_cast<const MsvcWString*>(reinterpret_cast<const uint8_t*>(cur) + 0x20u);
                    const wchar_t* chars = (key->cap >= 8u) ? key->ptr : key->buf;
                    const int cmp = chars ? std::wcscmp(c->kind_w, chars) : -1;
                    if (cmp == 0) {
                        found = cur;
                        break;
                    }
                    cur = (cmp < 0) ? cur->left : cur->right;
                }
                if (!found) return 0;

                if (Phase4Services::rb_tree_unlink_node(tree, found)) {
                    auto* key = reinterpret_cast<MsvcWString*>(reinterpret_cast<uint8_t*>(found) + 0x20u);
                    destroy_msvc_ws_unguarded(c->bid, c->base, *key);
                    if (c->alloc) {
                        (void)c->alloc->free(found, 16u);
                    }
                }
                return 0;
            };
            nm_exception_info_t ex{};
            (void)invoke_guarded(fn, &uctx, nullptr, &ex);
        }
    }
#else
    (void)was_live;
#endif
    return NM_OK;
}

// ============================================================================
// Deterministic Dispatch & White-Box Unit-Test Helpers
// ============================================================================

void Phase4Services::dispatch_tooltip_augment(void* popup_control, const nm_item_snapshot_t* item, int32_t alt_held) {
    fire_tooltip_augment_guarded(popup_control, item, alt_held);
}

int32_t Phase4Services::dispatch_affinity_route(
    const nm_item_snapshot_t*   item,
    int32_t                     stash_kind,
    nm_affinity_route_result_t* out_result) {
    return fire_affinity_route_guarded(item, stash_kind, out_result);
}

void Phase4Services::dispatch_ui_int_event(int32_t ctrl_kind, void* control, int32_t value) {
    fire_ui_int_event_guarded(ctrl_kind, control, value);
}

bool Phase4Services::get_geal_builtin_slot_for_handle(uint64_t handle, size_t& out_slot, uint64_t& out_gen) noexcept {
    if (handle == 0) return false;
    std::lock_guard<std::mutex> lock(g_geal_mutex);
    for (size_t i = 0; i < kMaxGealBuiltins; ++i) {
        if (g_geal_slots[i].handle == handle) {
            out_slot = i;
            out_gen = g_geal_slots[i].generation.load(std::memory_order_acquire);
            return true;
        }
    }
    return false;
}

nm_status_t Phase4Services::invoke_geal_indirection_slot(
    size_t       slot_idx,
    uint64_t     expected_gen,
    uint32_t     entity_id,
    const char*  args_json_utf8,
    std::string& out_result) {
    return invoke_geal_slot_guarded(slot_idx, expected_gen, entity_id, args_json_utf8, out_result);
}

void* Phase4Services::create_ruleset_proxy_for_slot(uint32_t ruleset_id, void* instance_client) {
    return allocate_ruleset_proxy(ruleset_id, instance_client);
}

void Phase4Services::simulate_custom_ui_created(const char* kind_name_utf8, void* control) {
    if (!kind_name_utf8 || !control) return;
    const std::string target(kind_name_utf8);
    nm_custom_ui_created_cb_t on_created = nullptr;
    void* ud = nullptr;
    std::string canonical;
    {
        std::lock_guard<std::mutex> lock(g_custom_ui_mutex);
        for (size_t i = 0; i < kMaxCustomUiKinds; ++i) {
            if (g_custom_ui_slots[i].handle != 0 && ascii_iequals(g_custom_ui_slots[i].kind_utf8, target)) {
                const uint64_t gen = g_custom_ui_slots[i].generation.load(std::memory_order_acquire);
                g_live_custom_controls[control] = LiveCustomControlRecord{i, gen};
                g_live_custom_control_count.store(g_live_custom_controls.size(), std::memory_order_release);
                canonical = g_custom_ui_slots[i].kind_utf8;
                on_created = g_custom_ui_slots[i].callbacks.on_created;
                ud = g_custom_ui_slots[i].user_data;
                break;
            }
        }
    }
    if (on_created) {
        on_created(canonical.c_str(), control, ud);
    }
}

bool Phase4Services::simulate_custom_ui_property(void* control, const char* key_utf8, const char* value_utf8) {
    if (!control || !key_utf8 || !value_utf8) return false;
    nm_custom_ui_property_cb_t on_prop = nullptr;
    void* ud = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_custom_ui_mutex);
        auto it = g_live_custom_controls.find(control);
        if (it == g_live_custom_controls.end()) return false;
        auto& slot = g_custom_ui_slots[it->second.slot_idx];
        if (slot.handle == 0 || slot.generation.load(std::memory_order_acquire) != it->second.generation) return false;
        on_prop = slot.callbacks.on_property;
        ud = slot.user_data;
    }
    if (on_prop) {
        on_prop(control, key_utf8, value_utf8, ud);
    }
    return true;
}

bool Phase4Services::simulate_custom_ui_destroyed(void* control) {
    if (!control) return false;
    nm_custom_ui_destroyed_cb_t on_destroyed = nullptr;
    void* ud = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_custom_ui_mutex);
        auto it = g_live_custom_controls.find(control);
        if (it == g_live_custom_controls.end()) return false;
        auto& slot = g_custom_ui_slots[it->second.slot_idx];
        if (slot.handle != 0 && slot.generation.load(std::memory_order_acquire) == it->second.generation) {
            on_destroyed = slot.callbacks.on_destroyed;
            ud = slot.user_data;
        }
        g_live_custom_controls.erase(it);
        g_live_custom_control_count.store(g_live_custom_controls.size(), std::memory_order_release);
    }
    if (on_destroyed) {
        struct Ctx {
            nm_custom_ui_destroyed_cb_t cb;
            void*                       ctrl;
            void*                       ud;
        } ctx{on_destroyed, control, ud};
        auto fn = [](void* raw) -> int64_t {
            auto* c = static_cast<Ctx*>(raw);
            c->cb(c->ctrl, c->ud);
            return 0;
        };
        nm_exception_info_t ex{};
        (void)invoke_guarded(fn, &ctx, nullptr, &ex);
    }
    return true;
}

} // namespace nativemods
