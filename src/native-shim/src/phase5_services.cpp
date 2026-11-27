#include "phase5_services.hpp"

#include "address_table.hpp"
#include "exception_wall.hpp"
#include "game_allocator.hpp"
#include "hook_engine.hpp"
#include "phase2_services.hpp"
#include "phase4_services.hpp"
#include "shim_logger.hpp"

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstddef>
#include <cstdio>
#include <cstring>
#include <deque>
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

// --- ABI 0.6.0 exact layout static_asserts (strict 0.5.0 prefix + 6 Phase 5 tail slots) ---
static_assert(NM_ABI_VERSION == 0x00000600u, "NM_ABI_VERSION must be 0.6.0");
static_assert(sizeof(nm_abi_table_t) == 1168u, "nm_abi_table_t must be 1168 bytes (8 + 145*8)");
static_assert(offsetof(nm_abi_table_t, ui_unregister_custom_control_kind) == 8u + 138u * 8u, "ABI 0.5.0 prefix preserved");
static_assert(offsetof(nm_abi_table_t, stack_set_u32_wire_enabled) == 8u + 139u * 8u, "ABI 0.6.0 starts at slot 139");
static_assert(offsetof(nm_abi_table_t, stack_set_format_millions_enabled) == 8u + 140u * 8u, "Slot 140 format millions");
static_assert(offsetof(nm_abi_table_t, stack_set_entity_override) == 8u + 141u * 8u, "Slot 141 stack entity override");
static_assert(offsetof(nm_abi_table_t, ui_set_compact_sockets_enabled) == 8u + 142u * 8u, "Slot 142 compact sockets");
static_assert(offsetof(nm_abi_table_t, world_play_effect) == 8u + 143u * 8u, "Slot 143 world play effect");
static_assert(offsetof(nm_abi_table_t, world_create_client_object_ex) == 8u + 144u * 8u, "ABI 0.6.0 ends at slot 144");

namespace {

std::atomic<nm_build_id_t>  g_p5_build_id{NM_BUILD_UNKNOWN};
std::atomic<uint64_t>       g_p5_image_base{0};
std::atomic<GameAllocator*> g_p5_allocator{nullptr};

std::atomic<bool> g_u32_stack_wire_enabled{false};
std::atomic<bool> g_format_millions_enabled{false};
std::atomic<bool> g_compact_sockets_enabled{false};
std::atomic<bool> g_compact_sockets_ever_enabled{false};

constexpr size_t kMaxTrackedEphemeralEffects = 64u;
std::mutex           g_effect_handles_mutex;
std::deque<uint64_t> g_ephemeral_effect_handles;

bool ptr_readable_p5(const void* p, size_t len) noexcept {
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
bool read_mem_p5(const void* base, size_t offset, T& out) noexcept {
    if (!base) return false;
    const auto* addr = static_cast<const uint8_t*>(base) + offset;
    if (!ptr_readable_p5(addr, sizeof(T))) return false;
    std::memcpy(&out, addr, sizeof(T));
    return true;
}

#if defined(_WIN32)

struct WStringViewP5 {
    const wchar_t* ptr;
    uint64_t       len;
};

struct MsvcWStringP5 {
    union {
        wchar_t  buf[8];
        wchar_t* ptr;
    };
    uint64_t size;
    uint64_t cap;
};
static_assert(sizeof(MsvcWStringP5) == 32u, "MSVC std::wstring must be 32 bytes");

size_t off_or_p5(nm_build_id_t bid, std::string_view name, size_t fallback) noexcept {
    if (bid != NM_BUILD_UNKNOWN && bid != NM_BUILD_SYNTHETIC) {
        if (const EmbeddedOffset* o = find_offset(bid, name)) {
            return o->offset;
        }
    }
    return fallback;
}

uint64_t sym_addr_p5(nm_build_id_t bid, uint64_t base, std::string_view name) noexcept {
    if (bid == NM_BUILD_UNKNOWN || bid == NM_BUILD_SYNTHETIC || base == 0) return 0;
    const EmbeddedSymbol* s = find_symbol(bid, name);
    if (!s || s->rva == 0 || s->status == NM_SYM_STATUS_UNVERIFIED) return 0;
    return base + s->rva;
}

std::wstring utf8_to_wide_p5(const char* s) {
    if (!s || s[0] == '\0') return {};
    const int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, nullptr, 0);
    if (n <= 1) return {};
    std::wstring w(static_cast<size_t>(n - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s, -1, w.data(), n);
    return w;
}

void* resolve_client_world_unguarded(nm_build_id_t bid, uint64_t base) {
    void* ingame_state = nullptr;
    const uint64_t ss_global = sym_addr_p5(bid, base, "frame.state_stack_global");
    if (ss_global != 0) {
        void* ss = nullptr;
        if (read_mem_p5(reinterpret_cast<const void*>(static_cast<uintptr_t>(ss_global)), 0, ss) && ss) {
            const uint64_t vt_ingame = sym_addr_p5(bid, base, "frame.vtable_ingame_state");
            const size_t begin_off = off_or_p5(bid, "frame.state_stack.active_begin", 0x08u);
            const size_t end_off   = off_or_p5(bid, "frame.state_stack.active_end", 0x10u);
            const size_t sid_off   = off_or_p5(bid, "frame.state.state_id", 0x209u);

            uint8_t* vec_begin = nullptr;
            uint8_t* vec_end = nullptr;
            if (read_mem_p5(ss, begin_off, vec_begin) && read_mem_p5(ss, end_off, vec_end) &&
                vec_begin && vec_end && vec_end >= vec_begin) {
                const size_t count = static_cast<size_t>(vec_end - vec_begin) / 0x10u;
                if (count > 0 && count <= 64 && ptr_readable_p5(vec_begin, count * 0x10u)) {
                    for (size_t i = count; i > 0; --i) {
                        void* st = nullptr;
                        if (!read_mem_p5(vec_begin + (i - 1) * 0x10u, 0, st) || !st) continue;
                        uint64_t vt = 0;
                        uint8_t sid = 0xFFu;
                        (void)read_mem_p5(st, 0, vt);
                        (void)read_mem_p5(st, sid_off, sid);
                        if ((vt_ingame != 0 && vt == vt_ingame) || sid == 4u) {
                            ingame_state = st;
                            break;
                        }
                    }
                }
            }
        }
    }

    void* world = nullptr;
    if (ingame_state) {
        (void)read_mem_p5(ingame_state, off_or_p5(bid, "frame.ingame_state.world", 0x218u), world);
    }
    if (!world) {
        const uint64_t fn_ic = sym_addr_p5(bid, base, "world.get_instance_client");
        if (fn_ic != 0) {
            using GetIcFn = void*(NM_CALL*)();
            void* ic = reinterpret_cast<GetIcFn>(static_cast<uintptr_t>(fn_ic))();
            if (ic) {
                (void)read_mem_p5(ic, off_or_p5(bid, "network.instance_client.world", 0xC278u), world);
            }
        }
    }
    return world;
}

void* resolve_entity_unguarded(nm_build_id_t bid, void* client_world, uint32_t entity_id) {
    if (!client_world) return nullptr;
    if (entity_id == 0u) {
        void* lp = nullptr;
        (void)read_mem_p5(client_world, off_or_p5(bid, "world.client_world.local_player", 0x970u), lp);
        return lp;
    }
    const size_t em_off     = off_or_p5(bid, "world.client_world.entity_manager", 0xA18u);
    const auto*  em         = static_cast<const uint8_t*>(client_world) + em_off;
    const size_t awake_off  = off_or_p5(bid, "world.entity_mgr.awake_map", 0x10u);
    const size_t asleep_off = off_or_p5(bid, "world.entity_mgr.asleep_map", 0x20u);

    void* awake_head = nullptr;
    if (read_mem_p5(em, awake_off, awake_head) && awake_head) {
        if (void* ent = Phase2Services::find_entity_in_rb_tree(awake_head, entity_id)) {
            return ent;
        }
    }
    void* asleep_head = nullptr;
    if (read_mem_p5(em, asleep_off, asleep_head) && asleep_head) {
        if (void* ent = Phase2Services::find_entity_in_rb_tree(asleep_head, entity_id)) {
            return ent;
        }
    }
    return nullptr;
}

void* call_get_component_p5_unguarded(nm_build_id_t bid, uint64_t base, const char* sym_name, void* entity) {
    if (!entity) return nullptr;
    const uint64_t fn_addr = sym_addr_p5(bid, base, sym_name);
    if (fn_addr == 0) return nullptr;
    using GetCompFn = void*(NM_CALL*)(void*);
    return reinterpret_cast<GetCompFn>(static_cast<uintptr_t>(fn_addr))(entity);
}

// Reads item_w, item_h, socket_count, and unscaled raw_cell_side from a live SocketOverlay*.
// Returns false if entity/sockets are not yet bound (e.g. ItemView calling SetCellSide before binding +0x430/+0x438).
bool inspect_socket_overlay_unguarded(
    nm_build_id_t bid,
    uint64_t      base,
    void*         overlay,
    uint8_t&      out_item_w,
    uint8_t&      out_item_h,
    uint32_t&     out_socket_count,
    float&        out_raw_cell_side) {
    out_item_w = 0;
    out_item_h = 0;
    out_socket_count = 0;
    out_raw_cell_side = 0.0f;

    if (!overlay || !ptr_readable_p5(overlay, 0x440u)) return false;

    void* entity = nullptr;
    void* sockets_comp = nullptr;
    if (!read_mem_p5(overlay, off_or_p5(bid, "ui.socket_overlay.entity", 0x430u), entity) || !entity) return false;
    if (!read_mem_p5(overlay, off_or_p5(bid, "ui.socket_overlay.sockets_comp", 0x438u), sockets_comp) || !sockets_comp) return false;

    void* base_comp = call_get_component_p5_unguarded(bid, base, "items.get_base_component", entity);
    if (!base_comp) return false;
    void* base_static = nullptr;
    if (!read_mem_p5(base_comp, off_or_p5(bid, "items.base_comp.static_data", 0x10u), base_static) || !base_static) return false;

    uint8_t w = 0;
    uint8_t h = 0;
    if (!read_mem_p5(base_static, off_or_p5(bid, "items.base_static.width", 0x10u), w)) return false;
    if (!read_mem_p5(base_static, off_or_p5(bid, "items.base_static.height", 0x11u), h)) return false;
    if (w == 0 || h == 0) return false;

    const uint64_t fn_count = sym_addr_p5(bid, base, "items.sockets_get_count");
    if (fn_count == 0) return false;
    using GetSocketCountFn = uint32_t(NM_CALL*)(const void*);
    const uint32_t n = reinterpret_cast<GetSocketCountFn>(static_cast<uintptr_t>(fn_count))(sockets_comp);

    // Anti-double-scaling: read unscaled cell_side from parent InventoryItemView (+0x588) or ItemView (+0x35c).
    void* parent = nullptr;
    if (read_mem_p5(overlay, off_or_p5(bid, "ui.control.parent", 0x1E0u), parent) && parent) {
        uint64_t parent_vt = 0;
        if (read_mem_p5(parent, 0, parent_vt) && parent_vt != 0) {
            const uint64_t vt_inv_view  = sym_addr_p5(bid, base, "items.vtable_inv_item_view");
            const uint64_t vt_item_view = sym_addr_p5(bid, base, "ui.vtable_item_view");
            if (vt_inv_view != 0 && parent_vt == vt_inv_view) {
                (void)read_mem_p5(parent, off_or_p5(bid, "ui.inv_item_view.cell_side", 0x588u), out_raw_cell_side);
            } else if (vt_item_view != 0 && parent_vt == vt_item_view) {
                (void)read_mem_p5(parent, off_or_p5(bid, "ui.item_view.cell_side", 0x35Cu), out_raw_cell_side);
            }
        }
    }

    out_item_w = w;
    out_item_h = h;
    out_socket_count = n;
    return true;
}

// --- Live TargetChain Hooks ---

void NM_CALL hook_stack_deserialise_pre(nm_hook_context_t* ctx, void* /*ud*/) {
    if (!ctx || !g_u32_stack_wire_enabled.load(std::memory_order_acquire)) return;

    const nm_build_id_t bid = g_p5_build_id.load(std::memory_order_acquire);
    if (bid == NM_BUILD_UNKNOWN || bid == NM_BUILD_SYNTHETIC) return;

    struct Ctx {
        nm_build_id_t bid;
        void*         stack_comp;
        void*         stream;
        bool          handled = false;
    } c{
        bid,
        reinterpret_cast<void*>(static_cast<uintptr_t>(ctx->args[0])),
        reinterpret_cast<void*>(static_cast<uintptr_t>(ctx->args[1])),
        false,
    };

    auto fn = [](void* raw) -> int64_t {
        auto* p = static_cast<Ctx*>(raw);
        if (!p->stack_comp || !p->stream) return 0;
        const size_t size_off = off_or_p5(p->bid, "items.stack.size", 0x18u);
        if (!ptr_readable_p5(p->stack_comp, size_off + sizeof(uint32_t))) return 0;
        if (!ptr_readable_p5(p->stream, sizeof(Phase5Services::StackWireStream))) return 0;

        auto* s = static_cast<Phase5Services::StackWireStream*>(p->stream);
        if (!s->data || s->pos > s->size) return 0;
        const uint64_t rem = s->size - s->pos;
        if (rem < 2u || !ptr_readable_p5(s->data + s->pos, static_cast<size_t>(std::min<uint64_t>(rem, 6u)))) {
            return 0;
        }
        p->handled = Phase5Services::try_deserialise_u32_stack_wire(true, p->stack_comp, size_off, s);
        return 0;
    };

    nm_exception_info_t ex{};
    if (invoke_guarded(fn, &c, nullptr, &ex) == NM_OK && c.handled) {
        ctx->skip_original = 1u;
    }
}

void NM_CALL hook_format_stack_count_pre(nm_hook_context_t* ctx, void* /*ud*/) {
    if (!ctx || !g_format_millions_enabled.load(std::memory_order_acquire)) return;

    const uint32_t count = static_cast<uint32_t>(ctx->args[1] & 0xFFFFFFFFull);
    if (count < 1000000u) return;

    std::string formatted;
    if (!Phase5Services::format_stack_count_millions(count, formatted)) return;
    if (formatted.empty() || formatted.size() > 7u) return;

    struct Ctx {
        void*       out_ws;
        const char* ascii;
        size_t      len;
        bool        ok = false;
    } c{
        reinterpret_cast<void*>(static_cast<uintptr_t>(ctx->args[0])),
        formatted.c_str(),
        formatted.size(),
        false,
    };

    auto fn = [](void* raw) -> int64_t {
        auto* p = static_cast<Ctx*>(raw);
        if (!p->out_ws || !ptr_readable_p5(p->out_ws, sizeof(MsvcWStringP5))) return 0;
        // Max formatted uint32_t ("4294.9M") is 7 ASCII chars, which ALWAYS fits in MSVC std::wstring SSO (cap=7).
        auto* ws = static_cast<MsvcWStringP5*>(p->out_ws);
        std::memset(ws, 0, sizeof(*ws));
        for (size_t i = 0; i < p->len; ++i) {
            ws->buf[i] = static_cast<wchar_t>(static_cast<unsigned char>(p->ascii[i]));
        }
        ws->buf[p->len] = L'\0';
        ws->size = static_cast<uint64_t>(p->len);
        ws->cap = 7ull;
        p->ok = true;
        return 0;
    };

    nm_exception_info_t ex{};
    if (invoke_guarded(fn, &c, nullptr, &ex) == NM_OK && c.ok) {
        ctx->return_value = ctx->args[0];
        ctx->skip_original = 1u;
    }
}

void NM_CALL hook_socket_overlay_set_cell_side_pre(nm_hook_context_t* ctx, void* /*ud*/) {
    if (!ctx) return;
    const bool enabled = g_compact_sockets_enabled.load(std::memory_order_acquire);
    if (!enabled && !g_compact_sockets_ever_enabled.load(std::memory_order_acquire)) return;

    const nm_build_id_t bid = g_p5_build_id.load(std::memory_order_acquire);
    const uint64_t base = g_p5_image_base.load(std::memory_order_acquire);
    if (bid == NM_BUILD_UNKNOWN || bid == NM_BUILD_SYNTHETIC || base == 0) return;

    struct Ctx {
        nm_build_id_t bid;
        uint64_t      base;
        void*         overlay;
        bool          enabled;
        uint64_t      xmm1_bits;
    } c{
        bid,
        base,
        reinterpret_cast<void*>(static_cast<uintptr_t>(ctx->args[0])),
        enabled,
        ctx->float_args[1],
    };

    auto fn = [](void* raw) -> int64_t {
        auto* p = static_cast<Ctx*>(raw);
        uint8_t item_w = 0;
        uint8_t item_h = 0;
        uint32_t socket_count = 0;
        float raw_cell = 0.0f;
        if (!inspect_socket_overlay_unguarded(p->bid, p->base, p->overlay, item_w, item_h, socket_count, raw_cell)) {
            return 0;
        }
        const size_t cols_off = off_or_p5(p->bid, "ui.socket_overlay.cols", 0x420u);
        uint32_t cols = item_w;
        (void)read_mem_p5(p->overlay, cols_off, cols);

        const uint32_t old_cols = cols;
        (void)Phase5Services::apply_socket_overlay_set_cell_side_pre(
            p->enabled, item_w, item_h, socket_count, raw_cell, cols, p->xmm1_bits);

        if (cols != old_cols) {
            std::memcpy(static_cast<uint8_t*>(p->overlay) + cols_off, &cols, sizeof(cols));
        }
        return 0;
    };

    nm_exception_info_t ex{};
    if (invoke_guarded(fn, &c, nullptr, &ex) == NM_OK) {
        ctx->float_args[1] = c.xmm1_bits;
    }
}

void NM_CALL hook_socket_overlay_rebuild_pre(nm_hook_context_t* ctx, void* /*ud*/) {
    if (!ctx) return;
    const bool enabled = g_compact_sockets_enabled.load(std::memory_order_acquire);
    if (!enabled && !g_compact_sockets_ever_enabled.load(std::memory_order_acquire)) return;

    const nm_build_id_t bid = g_p5_build_id.load(std::memory_order_acquire);
    const uint64_t base = g_p5_image_base.load(std::memory_order_acquire);
    if (bid == NM_BUILD_UNKNOWN || bid == NM_BUILD_SYNTHETIC || base == 0) return;

    struct Ctx {
        nm_build_id_t bid;
        uint64_t      base;
        void*         overlay;
        bool          enabled;
    } c{
        bid,
        base,
        reinterpret_cast<void*>(static_cast<uintptr_t>(ctx->args[0])),
        enabled,
    };

    auto fn = [](void* raw) -> int64_t {
        auto* p = static_cast<Ctx*>(raw);
        uint8_t item_w = 0;
        uint8_t item_h = 0;
        uint32_t socket_count = 0;
        float raw_cell = 0.0f;
        if (!inspect_socket_overlay_unguarded(p->bid, p->base, p->overlay, item_w, item_h, socket_count, raw_cell)) {
            return 0;
        }
        if (raw_cell <= 0.0f) return 0;

        const size_t cols_off = off_or_p5(p->bid, "ui.socket_overlay.cols", 0x420u);
        const size_t cell_off = off_or_p5(p->bid, "ui.socket_overlay.cell_side", 0x424u);
        uint32_t cols = item_w;
        float cell_side = raw_cell;
        (void)read_mem_p5(p->overlay, cols_off, cols);
        (void)read_mem_p5(p->overlay, cell_off, cell_side);

        const uint32_t old_cols = cols;
        const float old_cell = cell_side;
        (void)Phase5Services::apply_socket_overlay_rebuild_pre(
            p->enabled, item_w, item_h, socket_count, raw_cell, cols, cell_side);

        if (cols != old_cols) {
            std::memcpy(static_cast<uint8_t*>(p->overlay) + cols_off, &cols, sizeof(cols));
        }
        if (std::fabs(cell_side - old_cell) > 1e-4f) {
            std::memcpy(static_cast<uint8_t*>(p->overlay) + cell_off, &cell_side, sizeof(cell_side));
        }
        return 0;
    };

    nm_exception_info_t ex{};
    (void)invoke_guarded(fn, &c, nullptr, &ex);
}

void NM_CALL hook_ingame_state_leave_reset_p5(nm_hook_context_t* /*ctx*/, void* /*ud*/) {
    // Auto-reset opt-in u32 stack wire sentinel on disconnect / leaving in-game state
    // so subsequent sessions default cleanly to vanilla u16 wire format until handshake opt-in.
    g_u32_stack_wire_enabled.store(false, std::memory_order_release);
    std::lock_guard<std::mutex> lock(g_effect_handles_mutex);
    g_ephemeral_effect_handles.clear();
}

#endif // _WIN32

} // namespace

Phase5Services::~Phase5Services() = default;

void Phase5Services::install_live_hooks(
    nm_build_id_t   build_id,
    uint64_t        image_base,
    HookEngine&     hooks,
    GameAllocator&  allocator,
    Phase4Services& phase4) {
    build_id_ = build_id;
    image_base_ = image_base;
    allocator_ = &allocator;
    phase4_ = &phase4;
    g_p5_build_id.store(build_id, std::memory_order_release);
    g_p5_image_base.store(image_base, std::memory_order_release);
    g_p5_allocator.store(&allocator, std::memory_order_release);

#if defined(_WIN32)
    if (build_id == NM_BUILD_SYNTHETIC || build_id == NM_BUILD_UNKNOWN || image_base == 0) {
        return;
    }

    auto arm_chain = [&](const char* sym_name, nm_hook_phase_t phase, nm_hook_chain_callback_t cb) -> bool {
        const EmbeddedSymbol* sym = find_symbol(build_id, sym_name);
        if (!sym || sym->rva == 0 || sym->status == NM_SYM_STATUS_UNVERIFIED) return false;
        const uint64_t addr = image_base + sym->rva;
        if (!ptr_readable_p5(reinterpret_cast<const void*>(static_cast<uintptr_t>(addr)), 24)) return false;

        nm_hook_handle_t handle = NM_HOOK_INVALID;
        const nm_status_t st = hooks.install_inline_chain(addr, sym->expected_bytes_hex, phase, 0, cb, nullptr, &handle);
        if (st == NM_OK && handle != NM_HOOK_INVALID) {
            installed_handles_.push_back(handle);
            return true;
        }
        logger_logf("WARN", "[PHASE5] Failed to arm chain hook '%s' at 0x%llX (st=%d)",
                    sym_name, static_cast<unsigned long long>(addr), static_cast<int>(st));
        return false;
    };

    const bool ok_stack_deser = arm_chain("items.stack_deserialise",         NM_HOOK_PHASE_PRE, &hook_stack_deserialise_pre);
    const bool ok_fmt_count   = arm_chain("ui.format_stack_count",           NM_HOOK_PHASE_PRE, &hook_format_stack_count_pre);
    const bool ok_sock_cell   = arm_chain("ui.socket_overlay_set_cell_side", NM_HOOK_PHASE_PRE, &hook_socket_overlay_set_cell_side_pre);
    const bool ok_sock_rebd   = arm_chain("ui.socket_overlay_rebuild",       NM_HOOK_PHASE_PRE, &hook_socket_overlay_rebuild_pre);
    const bool ok_ig_leave    = arm_chain("frame.ingame_state_leave",        NM_HOOK_PHASE_PRE, &hook_ingame_state_leave_reset_p5);

    logger_logf("INFO",
                "[PHASE5] Armed live hooks (stack_deser=%s fmt_count=%s sock_cell=%s sock_rebuild=%s ig_leave=%s)",
                ok_stack_deser ? "OK" : "SKIP",
                ok_fmt_count   ? "OK" : "SKIP",
                ok_sock_cell   ? "OK" : "SKIP",
                ok_sock_rebd   ? "OK" : "SKIP",
                ok_ig_leave    ? "OK" : "SKIP");
#else
    (void)hooks;
#endif
}

void Phase5Services::reset(HookEngine* hooks) {
    g_u32_stack_wire_enabled.store(false, std::memory_order_release);
    g_format_millions_enabled.store(false, std::memory_order_release);
    g_compact_sockets_enabled.store(false, std::memory_order_release);
    g_compact_sockets_ever_enabled.store(false, std::memory_order_release);

    {
        std::lock_guard<std::mutex> lock(g_effect_handles_mutex);
        g_ephemeral_effect_handles.clear();
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
    phase4_ = nullptr;
    g_p5_build_id.store(NM_BUILD_UNKNOWN, std::memory_order_release);
    g_p5_image_base.store(0, std::memory_order_release);
    g_p5_allocator.store(nullptr, std::memory_order_release);
}

// ============================================================================
// ABI 0.6.0 Service Entry Points (slots 139..144)
// ============================================================================

nm_status_t Phase5Services::stack_set_u32_wire_enabled(int32_t enabled) {
    g_u32_stack_wire_enabled.store(enabled != 0, std::memory_order_release);
    return NM_OK;
}

nm_status_t Phase5Services::stack_set_format_millions_enabled(int32_t enabled) {
    g_format_millions_enabled.store(enabled != 0, std::memory_order_release);
    return NM_OK;
}

nm_status_t Phase5Services::stack_set_entity_override(uint32_t entity_id, uint32_t stack_size) const {
    if (entity_id == 0u || stack_size == 0u) return NM_ERR_INVALID_ARG;

#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || build_id_ == NM_BUILD_UNKNOWN || image_base_ == 0) {
        return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    }

    struct Ctx {
        nm_build_id_t bid;
        uint64_t      base;
        uint32_t      entity_id;
        uint32_t      stack_size;
    } ctx{build_id_, image_base_, entity_id, stack_size};

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        void* world = resolve_client_world_unguarded(c->bid, c->base);
        if (!world) return -1;

        void* ent = resolve_entity_unguarded(c->bid, world, c->entity_id);
        if (!ent) return -2;

        void* stack_comp = call_get_component_p5_unguarded(c->bid, c->base, "items.get_stack_component", ent);
        if (!stack_comp) {
            // Also support WorldItem ground entities by dereferencing WorldItem+0x28 ItemEntity*.
            void* wi = call_get_component_p5_unguarded(c->bid, c->base, "components.get_world_item", ent);
            if (wi) {
                void* inner_item = nullptr;
                if (read_mem_p5(wi, off_or_p5(c->bid, "items.world_item.item_entity", 0x28u), inner_item) && inner_item) {
                    stack_comp = call_get_component_p5_unguarded(c->bid, c->base, "items.get_stack_component", inner_item);
                }
            }
        }
        if (!stack_comp) return -3;

        const size_t size_off = off_or_p5(c->bid, "items.stack.size", 0x18u);
        if (!ptr_readable_p5(stack_comp, size_off + sizeof(uint32_t))) return -4;
        return Phase5Services::apply_stack_override_to_component(stack_comp, size_off, c->stack_size) ? 0 : -4;
    };

    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK) return st;
    if (rc == -1) return NM_ERR_NOT_INITIALIZED;
    if (rc == -2 || rc == -3) return NM_ERR_NOT_FOUND;
    if (rc != 0) return NM_ERR_GAME_EXCEPTION;
    return NM_OK;
#else
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase5Services::ui_set_compact_sockets_enabled(int32_t enabled) {
    const bool flag = (enabled != 0);
    if (flag) {
        g_compact_sockets_ever_enabled.store(true, std::memory_order_release);
    }
    g_compact_sockets_enabled.store(flag, std::memory_order_release);
    return NM_OK;
}

nm_status_t Phase5Services::world_create_client_object_ex(
    const char* metadata_utf8,
    float       x,
    float       y,
    float       z,
    float       rotation_rad,
    float       scale,
    uint64_t*   out_handle) {
    if (!metadata_utf8 || metadata_utf8[0] == '\0' || !out_handle) return NM_ERR_INVALID_ARG;
    if (!std::isfinite(x) || !std::isfinite(y) || !std::isfinite(z) ||
        !std::isfinite(rotation_rad) || !std::isfinite(scale) || scale < 0.0f) {
        return NM_ERR_INVALID_ARG;
    }
    *out_handle = 0;

    std::string canonical_key;
    std::string ot_file_path;
    if (!Phase2Services::canonicalize_object_type_path(metadata_utf8, canonical_key, ot_file_path)) {
        return NM_ERR_INVALID_ARG;
    }

    const float effective_scale = (scale > 0.0f) ? scale : 1.0f;

#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || build_id_ == NM_BUILD_UNKNOWN || image_base_ == 0 || !allocator_) {
        return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    }

    const std::wstring meta_w = utf8_to_wide_p5(canonical_key.c_str());
    const std::wstring ot_file_w = utf8_to_wide_p5(ot_file_path.c_str());
    if (meta_w.empty() || ot_file_w.empty()) return NM_ERR_INVALID_ARG;

    // Pre-check virtual FileOverlay outside invoke_guarded (avoids C++ mutex inside VEH/SEH frame).
    size_t overlay_sz = 0;
    const bool in_overlay = (nm_has_file_overlay(ot_file_path.c_str(), &overlay_sz) == NM_OK);

    struct Ctx {
        nm_build_id_t  bid;
        uint64_t       base;
        const wchar_t* meta_ptr;
        uint64_t       meta_len;
        const wchar_t* ot_file_ptr;
        uint64_t       ot_file_len;
        bool           in_overlay;
        float          x;
        float          y;
        float          z;
        float          rotation_rad;
        float          scale;
        void*          created = nullptr;
        nm_status_t    status = NM_ERR_GAME_EXCEPTION;
    } ctx{
        build_id_, image_base_,
        meta_w.c_str(), static_cast<uint64_t>(meta_w.size()),
        ot_file_w.c_str(), static_cast<uint64_t>(ot_file_w.size()),
        in_overlay,
        x, y, z, rotation_rad, effective_scale, nullptr, NM_ERR_GAME_EXCEPTION,
    };

    auto fn = [](void* raw) -> int64_t {
        auto* c = static_cast<Ctx*>(raw);
        void* world = resolve_client_world_unguarded(c->bid, c->base);
        if (!world) {
            c->status = NM_ERR_NOT_INITIALIZED;
            return -1;
        }

        const uint64_t fn_rm_inst   = sym_addr_p5(c->bid, c->base, "world.resource_mgr_instance");
        const uint64_t fn_rm_get    = sym_addr_p5(c->bid, c->base, "world.resource_mgr_get_ot");
        const uint64_t fn_ctor      = sym_addr_p5(c->bid, c->base, "world.create_client_object");
        const uint64_t fn_alloc     = sym_addr_p5(c->bid, c->base, "allocator.alloc");
        const uint64_t fn_free      = sym_addr_p5(c->bid, c->base, "allocator.free");
        const uint64_t fn_fs_holder = sym_addr_p5(c->bid, c->base, "files.filesystem_holder_get");
        const uint64_t fn_fs_exists = sym_addr_p5(c->bid, c->base, "files.exists_query");
        if (fn_rm_inst == 0 || fn_rm_get == 0 || fn_ctor == 0 || fn_alloc == 0) {
            c->status = NM_ERR_SYMBOL_NOT_FOUND;
            return -2;
        }

        // Verify "<canonical>.ot" exists BEFORE touching ResourceManager::GetObjectType so a missing
        // metadata file never allocates an empty 0-component ObjectTypeSlot or poisons EntityManager.
        if (!c->in_overlay) {
            if (fn_fs_holder == 0 || fn_fs_exists == 0) {
                c->status = NM_ERR_SYMBOL_NOT_FOUND;
                return -2;
            }
            using HolderGetFn = void*(NM_CALL*)();
            void* holder = reinterpret_cast<HolderGetFn>(static_cast<uintptr_t>(fn_fs_holder))();
            void* fs = nullptr;
            if (!holder || !read_mem_p5(holder, 0x08u, fs) || !fs) {
                c->status = NM_ERR_NOT_INITIALIZED;
                return -1;
            }
            WStringViewP5 ot_wsv{c->ot_file_ptr, c->ot_file_len};
            using FsExistsFn = uint8_t(NM_CALL*)(void*, const WStringViewP5*);
            const uint8_t exists = reinterpret_cast<FsExistsFn>(static_cast<uintptr_t>(fn_fs_exists))(fs, &ot_wsv);
            if (exists == 0u) {
                c->status = NM_ERR_NOT_FOUND;
                return -3;
            }
        }

        using RmInstFn = void*(NM_CALL*)();
        void* rm = reinterpret_cast<RmInstFn>(static_cast<uintptr_t>(fn_rm_inst))();
        if (!rm) {
            c->status = NM_ERR_NOT_INITIALIZED;
            return -1;
        }

        WStringViewP5 wsv{c->meta_ptr, c->meta_len};
        using RmGetOtFn = void*(NM_CALL*)(void*, const WStringViewP5*, const WStringViewP5*);
        void* ot_slot = reinterpret_cast<RmGetOtFn>(static_cast<uintptr_t>(fn_rm_get))(rm, &wsv, &wsv);
        if (!ot_slot) {
            c->status = NM_ERR_NOT_FOUND;
            return -3;
        }
        if (!Phase2Services::validate_object_type_slot(ot_slot)) {
            c->status = NM_ERR_INVALID_ARG;
            return -6;
        }

        auto raw_alloc = reinterpret_cast<GameAllocator::RawAllocFn>(static_cast<uintptr_t>(fn_alloc));
        void* mem = raw_alloc(0x248u, 16u);
        if (!mem) {
            c->status = NM_ERR_ALLOC_FAILED;
            return -4;
        }
        std::memset(mem, 0, 0x248u);

        void* slot_handle = ot_slot;
        using CreateObjFn = void*(NM_CALL*)(void*, void**, void*, uint32_t, uint8_t);
        void* ent = reinterpret_cast<CreateObjFn>(static_cast<uintptr_t>(fn_ctor))(
            mem, &slot_handle, world, 0u, 1u);
        if (!ent) {
            if (fn_free != 0) {
                auto raw_free = reinterpret_cast<GameAllocator::RawFreeFn>(static_cast<uintptr_t>(fn_free));
                raw_free(mem);
            }
            c->status = NM_ERR_GAME_EXCEPTION;
            return -5;
        }

        void* pos = nullptr;
        (void)read_mem_p5(ent, off_or_p5(c->bid, "world.entity.positioned", 0x98u), pos);
        if (!pos) {
            pos = call_get_component_p5_unguarded(c->bid, c->base, "components.get_positioned", ent);
        }
        void* render = call_get_component_p5_unguarded(c->bid, c->base, "components.get_render", ent);
        if (pos && !ptr_readable_p5(pos, 0x2C0u)) pos = nullptr;
        if (render && !ptr_readable_p5(render, 0x140u)) render = nullptr;

        (void)Phase5Services::apply_client_object_transform(
            pos,
            off_or_p5(c->bid, "components.positioned.grid_x", 0x294u),
            off_or_p5(c->bid, "components.positioned.grid_y", 0x298u),
            off_or_p5(c->bid, "components.positioned.rotation", 0x29Cu),
            off_or_p5(c->bid, "components.positioned.world_x", 0x2B8u),
            off_or_p5(c->bid, "components.positioned.world_y", 0x2BCu),
            off_or_p5(c->bid, "components.positioned.world_z", 0x2B4u),
            render,
            off_or_p5(c->bid, "components.render.world_pos", 0x120u),
            off_or_p5(c->bid, "components.render.bounds", 0x12Cu),
            c->x, c->y, c->z, c->rotation_rad, c->scale);

        c->created = ent;
        c->status = NM_OK;
        return 0;
    };

    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(fn, &ctx, &rc, &ex);
    if (st != NM_OK) return st;
    if (rc != 0) return ctx.status;
    if (!ctx.created) return NM_ERR_GAME_EXCEPTION;
    *out_handle = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(ctx.created));
    return NM_OK;
#else
    (void)effective_scale;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

nm_status_t Phase5Services::world_play_effect(uint32_t target_entity_id, const char* effect_path_utf8) {
    if (!effect_path_utf8 || effect_path_utf8[0] == '\0') return NM_ERR_INVALID_ARG;

    const std::string normalized = normalize_effect_path(effect_path_utf8);
    if (normalized.empty()) return NM_ERR_INVALID_ARG;

    // Validate metadata-like paths up front so non-.ot asset paths (.ao, .epk, .pet, .ogg, etc.)
    // are rejected with NM_ERR_INVALID_ARG before ever touching ResourceManager or EntityManager.
    const bool is_metadata_path = looks_like_metadata_path(normalized);
    std::string canonical_key;
    std::string ot_file_path;
    if (is_metadata_path &&
        !Phase2Services::canonicalize_object_type_path(effect_path_utf8, canonical_key, ot_file_path)) {
        return NM_ERR_INVALID_ARG;
    }

#if defined(_WIN32)
    if (build_id_ == NM_BUILD_SYNTHETIC || build_id_ == NM_BUILD_UNKNOWN || image_base_ == 0) {
        return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
    }

    // Resolve target entity (0 => local player) and its live world coordinates inside invoke_guarded.
    struct TargetCtx {
        nm_build_id_t bid;
        uint64_t      base;
        uint32_t      req_entity_id;
        uint32_t      resolved_entity_id = 0;
        float         wx = 0.0f;
        float         wy = 0.0f;
        float         wz = 0.0f;
        float         rot = 0.0f;
    } tctx{build_id_, image_base_, target_entity_id, 0, 0.0f, 0.0f, 0.0f, 0.0f};

    auto resolve_fn = [](void* raw) -> int64_t {
        auto* c = static_cast<TargetCtx*>(raw);
        void* world = resolve_client_world_unguarded(c->bid, c->base);
        if (!world) return -1;

        void* ent = resolve_entity_unguarded(c->bid, world, c->req_entity_id);
        if (!ent) return -2;

        (void)read_mem_p5(ent, off_or_p5(c->bid, "world.entity.id", 0x88u), c->resolved_entity_id);

        void* pos = nullptr;
        (void)read_mem_p5(ent, off_or_p5(c->bid, "world.entity.positioned", 0x98u), pos);
        if (!pos) {
            pos = call_get_component_p5_unguarded(c->bid, c->base, "components.get_positioned", ent);
        }
        if (!pos || !ptr_readable_p5(pos, 0x2C0u)) return -3;

        (void)read_mem_p5(pos, off_or_p5(c->bid, "components.positioned.world_x", 0x2B8u), c->wx);
        (void)read_mem_p5(pos, off_or_p5(c->bid, "components.positioned.world_y", 0x2BCu), c->wy);
        (void)read_mem_p5(pos, off_or_p5(c->bid, "components.positioned.rotation", 0x29Cu), c->rot);

        int32_t grid_z = 0x7FFFFFFF;
        (void)read_mem_p5(pos, off_or_p5(c->bid, "components.positioned.grid_z", 0x290u), grid_z);
        if (grid_z != 0x7FFFFFFF) {
            (void)read_mem_p5(pos, off_or_p5(c->bid, "components.positioned.world_z", 0x2B4u), c->wz);
        } else {
            const uint64_t fn_z = sym_addr_p5(c->bid, c->base, "world.positioned_get_world_z");
            if (fn_z != 0) {
                using GetZFn = float(NM_CALL*)(void*);
                c->wz = reinterpret_cast<GetZFn>(static_cast<uintptr_t>(fn_z))(pos);
            } else {
                (void)read_mem_p5(pos, off_or_p5(c->bid, "components.positioned.world_z", 0x2B4u), c->wz);
            }
        }
        return 0;
    };

    int64_t rc = 0;
    nm_exception_info_t ex{};
    const nm_status_t st = invoke_guarded(resolve_fn, &tctx, &rc, &ex);
    if (st != NM_OK) return st;
    if (rc == -1) return NM_ERR_NOT_INITIALIZED;
    if (rc == -2 || rc == -3) return NM_ERR_NOT_FOUND;

    // 1. If the caller supplied a bare GEAL entity function name (no path separators / dots), try GEAL invocation first.
    if (!is_metadata_path && phase4_ != nullptr && tctx.resolved_entity_id != 0u) {
        size_t out_len = 0;
        const nm_status_t geal_st = phase4_->geal_invoke_function(
            tctx.resolved_entity_id, normalized.c_str(), "[]", nullptr, 0, &out_len);
        if (geal_st == NM_OK) {
            return NM_OK;
        }
    }

    // 2. Spawn client-side VFX object at the target entity's world coordinates (using canonical extensionless .ot key).
    uint64_t spawned = 0;
    const char* spawn_path = is_metadata_path ? canonical_key.c_str() : normalized.c_str();
    const nm_status_t spawn_st = world_create_client_object_ex(
        spawn_path, tctx.wx, tctx.wy, tctx.wz, tctx.rot, 1.0f, &spawned);
    if (spawn_st != NM_OK) {
        return spawn_st;
    }

    // Bound ephemeral effect objects so repeated plays never leak unbounded entities.
    uint64_t evicted = 0;
    {
        std::lock_guard<std::mutex> lock(g_effect_handles_mutex);
        g_ephemeral_effect_handles.push_back(spawned);
        if (g_ephemeral_effect_handles.size() > kMaxTrackedEphemeralEffects) {
            evicted = g_ephemeral_effect_handles.front();
            g_ephemeral_effect_handles.pop_front();
        }
    }
    if (evicted != 0) {
        struct EvictCtx {
            nm_build_id_t bid;
            uint64_t      base;
            void*         ent;
        } ectx{build_id_, image_base_, reinterpret_cast<void*>(static_cast<uintptr_t>(evicted))};
        auto evict_fn = [](void* raw) -> int64_t {
            auto* c = static_cast<EvictCtx*>(raw);
            if (!ptr_readable_p5(c->ent, 0x90u)) return 0;
            const uint64_t fn_rm = sym_addr_p5(c->bid, c->base, "world.entity_mark_for_removal");
            if (fn_rm != 0) {
                using MarkRmFn = void(NM_CALL*)(void*);
                reinterpret_cast<MarkRmFn>(static_cast<uintptr_t>(fn_rm))(c->ent);
            }
            return 0;
        };
        nm_exception_info_t ev_ex{};
        (void)invoke_guarded(evict_fn, &ectx, nullptr, &ev_ex);
    }
    return NM_OK;
#else
    (void)target_entity_id;
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
#endif
}

bool Phase5Services::is_u32_wire_enabled() const noexcept {
    return g_u32_stack_wire_enabled.load(std::memory_order_acquire);
}

bool Phase5Services::is_format_millions_enabled() const noexcept {
    return g_format_millions_enabled.load(std::memory_order_acquire);
}

bool Phase5Services::is_compact_sockets_enabled() const noexcept {
    return g_compact_sockets_enabled.load(std::memory_order_acquire);
}

// ============================================================================
// Pure Deterministic Cross-Platform Helpers
// ============================================================================

bool Phase5Services::try_deserialise_u32_stack_wire(
    bool             wire_enabled,
    void*            stack_comp,
    size_t           stack_size_offset,
    StackWireStream* stream) noexcept {
    if (!wire_enabled || !stack_comp || !stream || !stream->data) return false;
    if (stream->pos > stream->size) return false;

    const uint64_t remaining = stream->size - stream->pos;
    if (remaining < 2u) return false;

    uint16_t sentinel = 0;
    std::memcpy(&sentinel, stream->data + stream->pos, sizeof(sentinel));
    if (sentinel != 0xFFFFu) {
        return false; // Standard <= 65534 vanilla wire path
    }

    // Short-buffer guard: 0xFFFF sentinel requires 4 trailing LE u32 bytes (6 bytes total).
    if (remaining < 6u) {
        return false;
    }

    uint32_t value = 0;
    std::memcpy(&value, stream->data + stream->pos + 2u, sizeof(value));
    std::memcpy(static_cast<uint8_t*>(stack_comp) + stack_size_offset, &value, sizeof(value));
    stream->pos += 6u;
    return true;
}

bool Phase5Services::format_stack_count_millions(uint32_t count, std::string& out_utf8) {
    if (count < 1000000u) return false;
    const uint32_t tenths = count / 100000u; // floor(count / 1e6 * 10)
    const uint32_t whole = tenths / 10u;
    const uint32_t frac = tenths % 10u;

    char buf[16] = {};
    if (frac == 0u) {
        std::snprintf(buf, sizeof(buf), "%uM", whole);
    } else {
        std::snprintf(buf, sizeof(buf), "%u.%uM", whole, frac);
    }
    out_utf8.assign(buf);
    return true;
}

bool Phase5Services::apply_stack_override_to_component(
    void*    stack_comp,
    size_t   stack_size_offset,
    uint32_t stack_size) noexcept {
    if (!stack_comp || stack_size == 0u) return false;
    std::memcpy(static_cast<uint8_t*>(stack_comp) + stack_size_offset, &stack_size, sizeof(stack_size));
    return true;
}

Phase5Services::CompactSocketLayout Phase5Services::compute_compact_socket_layout(
    uint8_t  item_w,
    uint8_t  item_h,
    uint32_t socket_count) noexcept {
    CompactSocketLayout out{};
    if (item_w == 0 || item_h == 0) {
        out.cols = 1;
        out.rows = 1;
        out.scale = 1.0f;
        out.compacted = false;
        return out;
    }

    const uint32_t capacity = static_cast<uint32_t>(item_w) * static_cast<uint32_t>(item_h);
    if (socket_count <= capacity) {
        out.cols = static_cast<uint32_t>(item_w);
        out.rows = static_cast<uint32_t>(item_h);
        out.scale = 1.0f;
        out.compacted = false;
        return out;
    }

    uint32_t best_c = 1;
    uint32_t best_r = socket_count;
    float best_scale = -1.0f;
    const uint32_t max_c = std::min<uint32_t>(socket_count, 6u);
    for (uint32_t c = 1; c <= max_c; ++c) {
        const uint32_t r = (socket_count + c - 1u) / c;
        const float sx = static_cast<float>(item_w) / static_cast<float>(c);
        const float sy = static_cast<float>(item_h) / static_cast<float>(r);
        const float scale = (sx < sy) ? sx : sy;
        // Maximize icon scale; on tie prefer larger c (e.g. 2x2 w/ 5-6 -> c=3,r=2; 1x3 w/ 6 -> c=2,r=3).
        if (scale > best_scale + 1e-6f || (std::fabs(scale - best_scale) <= 1e-6f && c > best_c)) {
            best_scale = scale;
            best_c = c;
            best_r = r;
        }
    }

    out.cols = best_c;
    out.rows = best_r;
    out.scale = best_scale;
    out.compacted = true;
    return out;
}

float Phase5Services::unpack_xmm_f32(uint64_t raw_bits) noexcept {
    const uint32_t lo = static_cast<uint32_t>(raw_bits & 0xFFFFFFFFull);
    float value = 0.0f;
    std::memcpy(&value, &lo, sizeof(value));
    return value;
}

uint64_t Phase5Services::pack_xmm_f32(float value) noexcept {
    uint32_t lo = 0;
    std::memcpy(&lo, &value, sizeof(lo));
    return static_cast<uint64_t>(lo);
}

bool Phase5Services::apply_socket_overlay_rebuild_pre(
    bool      compact_enabled,
    uint8_t   item_w,
    uint8_t   item_h,
    uint32_t  socket_count,
    float     raw_cell_side,
    uint32_t& overlay_cols,
    float&    overlay_cell_side) noexcept {
    if (item_w == 0 || item_h == 0 || raw_cell_side <= 0.0f) return false;

    const CompactSocketLayout layout = compute_compact_socket_layout(item_w, item_h, socket_count);
    if (!compact_enabled || !layout.compacted) {
        // Restore stock cols & unscaled cell_side if previously compacted (#163 note #2).
        overlay_cols = static_cast<uint32_t>(item_w);
        overlay_cell_side = raw_cell_side;
        return false;
    }

    overlay_cols = layout.cols;
    overlay_cell_side = raw_cell_side * layout.scale;
    return true;
}

bool Phase5Services::apply_socket_overlay_set_cell_side_pre(
    bool      compact_enabled,
    uint8_t   item_w,
    uint8_t   item_h,
    uint32_t  socket_count,
    float     raw_cell_side,
    uint32_t& overlay_cols,
    uint64_t& xmm1_bits) noexcept {
    if (item_w == 0 || item_h == 0) return false;

    const float base_cell = (raw_cell_side > 0.0f) ? raw_cell_side : unpack_xmm_f32(xmm1_bits);
    if (base_cell <= 0.0f || !std::isfinite(base_cell)) return false;

    const CompactSocketLayout layout = compute_compact_socket_layout(item_w, item_h, socket_count);
    if (!compact_enabled || !layout.compacted) {
        // Restore stock cols & pass unscaled float packed in low 32 bits of xmm1 (#163 notes #1 & #2).
        overlay_cols = static_cast<uint32_t>(item_w);
        xmm1_bits = pack_xmm_f32(base_cell);
        return false;
    }

    overlay_cols = layout.cols;
    xmm1_bits = pack_xmm_f32(base_cell * layout.scale);
    return true;
}

bool Phase5Services::apply_client_object_transform(
    void*  positioned_comp,
    size_t grid_x_off,
    size_t grid_y_off,
    size_t rot_off,
    size_t world_x_off,
    size_t world_y_off,
    size_t world_z_off,
    void*  render_comp,
    size_t render_pos_off,
    size_t render_bounds_off,
    float  x,
    float  y,
    float  z,
    float  rotation_rad,
    float  scale) noexcept {
    bool wrote_any = false;
    if (positioned_comp) {
        constexpr float kGridScale = 250.0f / 23.0f;
        const int32_t gx = static_cast<int32_t>(std::lround(x / kGridScale));
        const int32_t gy = static_cast<int32_t>(std::lround(y / kGridScale));
        auto* p = static_cast<uint8_t*>(positioned_comp);
        std::memcpy(p + grid_x_off,  &gx, sizeof(gx));
        std::memcpy(p + grid_y_off,  &gy, sizeof(gy));
        std::memcpy(p + rot_off,     &rotation_rad, sizeof(rotation_rad));
        std::memcpy(p + world_x_off, &x, sizeof(x));
        std::memcpy(p + world_y_off, &y, sizeof(y));
        std::memcpy(p + world_z_off, &z, sizeof(z));
        wrote_any = true;
    }
    if (render_comp) {
        auto* r = static_cast<uint8_t*>(render_comp);
        const float pos3[3] = {x, y, z};
        std::memcpy(r + render_pos_off, pos3, sizeof(pos3));
        if (scale > 0.0f && std::fabs(scale - 1.0f) > 1e-6f) {
            float bounds3[3] = {0.0f, 0.0f, 0.0f};
            std::memcpy(bounds3, r + render_bounds_off, sizeof(bounds3));
            bounds3[0] *= scale;
            bounds3[1] *= scale;
            bounds3[2] *= scale;
            std::memcpy(r + render_bounds_off, bounds3, sizeof(bounds3));
        }
        wrote_any = true;
    }
    return wrote_any;
}

std::string Phase5Services::normalize_effect_path(const char* raw_path_utf8) {
    if (!raw_path_utf8) return {};
    std::string out(raw_path_utf8);
    // Trim leading/trailing ASCII whitespace
    size_t start = 0;
    while (start < out.size() && (out[start] == ' ' || out[start] == '\t' || out[start] == '\r' || out[start] == '\n')) {
        ++start;
    }
    size_t end = out.size();
    while (end > start && (out[end - 1] == ' ' || out[end - 1] == '\t' || out[end - 1] == '\r' || out[end - 1] == '\n')) {
        --end;
    }
    out = out.substr(start, end - start);
    for (char& ch : out) {
        if (ch == '\\') ch = '/';
    }
    while (!out.empty() && out.front() == '/') {
        out.erase(out.begin());
    }
    return out;
}

bool Phase5Services::looks_like_metadata_path(const std::string& normalized_path) noexcept {
    return normalized_path.find('/') != std::string::npos || normalized_path.find('.') != std::string::npos;
}

} // namespace nativemods
