#pragma once

#include "nativemods_abi.h"

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace nativemods {

class GameAllocator;
class HookEngine;
class Phase4Services;

// Phase 5 (ABI 0.6.0) native services:
//   - Opt-in U32 stack wire sentinel (0xFFFF + LE u32 inside Stack::Deserialise 0x23fb5f0, auto-reset on reset()/disconnect)
//   - Opt-in "1M" / "1.2M" stack count formatter hook (ui.format_stack_count 0x16e2200)
//   - Sideband entity Stack+0x18 uint32_t override helper (stack_set_entity_override)
//   - 6-socket compact icon polish on SocketOverlay (SetCellSide 0x16dee60 + Rebuild 0x16df7a0)
//   - Live world VFX playback (world_play_effect) & extended client object spawning with rotation/scale (world_create_client_object_ex)
class Phase5Services {
public:
    Phase5Services() = default;
    ~Phase5Services();

    Phase5Services(const Phase5Services&) = delete;
    Phase5Services& operator=(const Phase5Services&) = delete;

    void install_live_hooks(
        nm_build_id_t   build_id,
        uint64_t        image_base,
        HookEngine&     hooks,
        GameAllocator&  allocator,
        Phase4Services& phase4);
    void reset(HookEngine* hooks);

    // ABI 0.6.0 service entry points (slots 139..144)
    nm_status_t stack_set_u32_wire_enabled(int32_t enabled);
    nm_status_t stack_set_format_millions_enabled(int32_t enabled);
    nm_status_t stack_set_entity_override(uint32_t entity_id, uint32_t stack_size) const;
    nm_status_t ui_set_compact_sockets_enabled(int32_t enabled);
    nm_status_t world_play_effect(uint32_t target_entity_id, const char* effect_path_utf8);
    nm_status_t world_create_client_object_ex(
        const char* metadata_utf8,
        float       x,
        float       y,
        float       z,
        float       rotation_rad,
        float       scale,
        uint64_t*   out_handle);

    // State query helpers for deterministic unit tests
    bool is_u32_wire_enabled() const noexcept;
    bool is_format_millions_enabled() const noexcept;
    bool is_compact_sockets_enabled() const noexcept;

    // --- Pure layout/wire/math helpers exposed for deterministic cross-platform C++20 unit tests ---

    struct StackWireStream {
        const uint8_t* data;
        uint64_t       size;
        uint64_t       pos;
    };

    static bool try_deserialise_u32_stack_wire(
        bool             wire_enabled,
        void*            stack_comp,
        size_t           stack_size_offset,
        StackWireStream* stream) noexcept;

    static bool format_stack_count_millions(uint32_t count, std::string& out_utf8);

    static bool apply_stack_override_to_component(
        void*    stack_comp,
        size_t   stack_size_offset,
        uint32_t stack_size) noexcept;

    struct CompactSocketLayout {
        uint32_t cols = 1;
        uint32_t rows = 1;
        float    scale = 1.0f;
        bool     compacted = false;
    };

    static CompactSocketLayout compute_compact_socket_layout(
        uint8_t  item_w,
        uint8_t  item_h,
        uint32_t socket_count) noexcept;

    static float    unpack_xmm_f32(uint64_t raw_bits) noexcept;
    static uint64_t pack_xmm_f32(float value) noexcept;

    static bool apply_socket_overlay_rebuild_pre(
        bool      compact_enabled,
        uint8_t   item_w,
        uint8_t   item_h,
        uint32_t  socket_count,
        float     raw_cell_side,
        uint32_t& overlay_cols,
        float&    overlay_cell_side) noexcept;

    static bool apply_socket_overlay_set_cell_side_pre(
        bool      compact_enabled,
        uint8_t   item_w,
        uint8_t   item_h,
        uint32_t  socket_count,
        float     raw_cell_side,
        uint32_t& overlay_cols,
        uint64_t& xmm1_bits) noexcept;

    static bool apply_client_object_transform(
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
        float  scale) noexcept;

    static std::string normalize_effect_path(const char* raw_path_utf8);
    static bool        looks_like_metadata_path(const std::string& normalized_path) noexcept;

private:
    nm_build_id_t                 build_id_ = NM_BUILD_UNKNOWN;
    uint64_t                      image_base_ = 0;
    GameAllocator*                allocator_ = nullptr;
    Phase4Services*               phase4_ = nullptr;
    std::vector<nm_hook_handle_t> installed_handles_;
};

} // namespace nativemods
