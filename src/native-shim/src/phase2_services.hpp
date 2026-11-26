#pragma once

#include "nativemods_abi.h"

#include <cstddef>
#include <cstdint>
#include <vector>

namespace nativemods {

class GameAllocator;
class HookEngine;

// Phase 2 (ABI 0.4.0) native services:
//   - World entity inspection & client-only object spawning (ClientWorld+0xa18 EntityManager dual RB-trees, 14 components.get_* getters)
//   - Entity stats read/write/enumeration (StatsComponent+0xf0 sorted vector, high-ID >= 0x10000 support)
//   - Area lifecycle info & AreaEntered / AreaLeft hooks (ClientWorld::InitArea, AreaLoadingState::Leave, InGameState::Leave, WorldAreasRow + rulesets)
//   - Client Item Filter state, reload hook (ItemFilterManager::NotifyLoaded), WorldItem style + ground label rect, guarded ItemFilter::Match
//   - Camera state, bit-exact DirectX v_row * M WorldToScreen, and guarded zoom override with automatic stock restore on reset()
class Phase2Services {
public:
    Phase2Services() = default;
    ~Phase2Services();

    Phase2Services(const Phase2Services&) = delete;
    Phase2Services& operator=(const Phase2Services&) = delete;

    void install_live_hooks(nm_build_id_t build_id, uint64_t image_base, HookEngine& hooks, GameAllocator& allocator);
    void reset(HookEngine* hooks);

    // World (0.4.0)
    nm_status_t world_get_local_player(nm_world_entity_snapshot_t* out_entity) const;
    nm_status_t world_list_entities(
        const nm_world_entity_query_t* query,
        nm_world_entity_snapshot_t*    out_buf,
        uint32_t                       capacity,
        uint32_t*                      out_count,
        uint32_t*                      out_total) const;
    nm_status_t world_get_entity(
        uint32_t                    entity_id,
        nm_world_entity_snapshot_t* out_entity) const;
    nm_status_t world_create_client_object(
        const char* metadata_utf8,
        float       x,
        float       y,
        float       z,
        uint64_t*   out_handle);
    nm_status_t world_remove_client_object(uint64_t handle);

    // Stats (0.4.0)
    nm_status_t stats_read_entity(uint32_t entity_id, int32_t stat_id, int32_t* out_value) const;
    nm_status_t stats_set_local_entity(uint32_t entity_id, int32_t stat_id, int32_t value) const;
    nm_status_t stats_list_entity(
        uint32_t         entity_id,
        nm_stat_entry_t* out_buf,
        uint32_t         capacity,
        uint32_t*        out_count,
        uint32_t*        out_total) const;

    // Area Lifecycle (0.4.0)
    nm_status_t lifecycle_get_area_info(nm_area_info_t* out_info) const;
    nm_status_t lifecycle_set_area_callback(nm_area_event_callback_t cb, void* user_data);

    // Item Filter (0.4.0)
    nm_status_t filter_get_state(nm_item_filter_state_t* out_state) const;
    nm_status_t filter_reload() const;
    nm_status_t filter_get_world_item_result(uint32_t entity_id, nm_item_filter_result_t* out_result) const;
    nm_status_t filter_evaluate_entity(uint32_t entity_id, nm_item_filter_result_t* out_result) const;
    nm_status_t filter_set_reload_callback(nm_filter_reload_callback_t cb, void* user_data);

    // Camera (0.4.0)
    nm_status_t camera_get_state(nm_camera_state_t* out_state) const;
    nm_status_t camera_world_to_screen(
        float    wx,
        float    wy,
        float    wz,
        float*   out_sx,
        float*   out_sy,
        int32_t* out_in_front) const;
    nm_status_t camera_set_zoom_override(float zoom_override);

    // Deterministic dispatch helpers (invoked by live hooks & unit tests)
    void dispatch_area_event(int32_t event_kind, const nm_area_info_t* area_info);
    void dispatch_filter_reloaded(int32_t succeeded, const nm_item_filter_state_t* filter_state);

    // Pure layout/math helpers exposed for deterministic cross-platform C++20 unit tests.
    static size_t traverse_rb_tree_entities(
        const void*         map_head_ptr,
        std::vector<void*>& out_entities,
        size_t              max_nodes = 16384u) noexcept;

    static void* find_entity_in_rb_tree(
        const void* map_head_ptr,
        uint32_t    entity_id) noexcept;

    static nm_status_t read_stats_vector(
        const void*      stats_comp,
        size_t           begin_offset,
        size_t           end_offset,
        nm_stat_entry_t* out_buf,
        uint32_t         capacity,
        uint32_t*        out_count,
        uint32_t*        out_total) noexcept;

    static bool read_area_info_from_structs(
        const void*     client_world,
        const void*     instance_client,
        nm_area_info_t& out) noexcept;

    static bool extract_item_filter_style(
        const void*              style_ptr,
        int32_t                  visibility_override, // -1 = derive from !(b0 & 1), 0 = hidden, 1 = visible
        nm_item_filter_result_t& out) noexcept;

    static float compute_vertical_fov(float tan_half_fov, float fov_scale) noexcept;

    static bool project_world_to_screen(
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
        int32_t*    out_in_front) noexcept;

private:
    nm_build_id_t                 build_id_ = NM_BUILD_UNKNOWN;
    uint64_t                      image_base_ = 0;
    GameAllocator*                allocator_ = nullptr;
    bool                          zoom_override_modified_ = false;
    nm_hook_handle_t              init_area_hook_handle_ = NM_HOOK_INVALID;
    void*                         init_area_trampoline_ = nullptr;
    std::vector<nm_hook_handle_t> installed_handles_;
};

} // namespace nativemods
