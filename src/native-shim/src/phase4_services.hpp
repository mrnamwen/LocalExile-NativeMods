#pragma once

#include "nativemods_abi.h"

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace nativemods {

class GameAllocator;
class HookEngine;

// Phase 4 (ABI 0.5.0) native services:
//   - Programmatic & Rich UI Controls (Frame/Label/Button/TextBox/CheckBox/Picture/StackLayout/ScrollableFrame/TabContainer/ComboBox/ListBox)
//   - Stock Item Tooltip Augmentation (ui.item_popup_build_sections post-hook + tooltip_add_item_section)
//   - Custom Stash Affinity Router (ui.stash_get_affinity_targets & items.compute_affinity_bitset post-hooks)
//   - Native Extend Registries:
//       * IGealBuiltinRegistry (hook-free geal.builtin_register_entry + DLL-owned indirection _Func_impl for unload safety)
//       * IScriptService.InvokeFunction (custom builtin & entity FunctionsComponent invocation)
//       * IRulesetRegistry (hook-free rulesets.creator_table[id] free-slot validation + 9-slot ClientRuleset proxy)
//       * IUiControlRegistry (ui.control_factory_map_global + parser_parse_property + control_release_or_destroy hooks)
class Phase4Services {
public:
    Phase4Services() = default;
    ~Phase4Services();

    Phase4Services(const Phase4Services&) = delete;
    Phase4Services& operator=(const Phase4Services&) = delete;

    void install_live_hooks(nm_build_id_t build_id, uint64_t image_base, HookEngine& hooks, GameAllocator& allocator);
    void reset(HookEngine* hooks);

    // Rich UI Controls (0.5.0)
    nm_status_t ui_create_control(int32_t kind, void* parent_control, const char* id_utf8, void** out_control) const;
    nm_status_t ui_picture_set_texture(void* picture_control, const char* texture_path_utf8) const;
    nm_status_t ui_scrollable_get_scroll(void* ctrl, float* out_offset, float* out_max) const;
    nm_status_t ui_scrollable_set_scroll(void* ctrl, float offset) const;
    nm_status_t ui_scrollable_get_content(void* ctrl, void** out_content) const;
    nm_status_t ui_tab_container_get_count(void* ctrl, uint32_t* out_count) const;
    nm_status_t ui_tab_container_get_active(void* ctrl, int32_t* out_index) const;
    nm_status_t ui_tab_container_select(void* ctrl, int32_t index) const;
    nm_status_t ui_tab_container_add_tab(void* ctrl, const char* label_utf8, uint32_t style, int32_t hidden, void** out_page) const;
    nm_status_t ui_tab_container_subscribe_changed(void* ctrl, nm_ui_int_event_callback_t cb, void* user_data, uint64_t* out_sub);
    nm_status_t ui_combobox_get_count(void* ctrl, uint32_t* out_count) const;
    nm_status_t ui_combobox_get_selected(void* ctrl, int32_t* out_index) const;
    nm_status_t ui_combobox_set_selected(void* ctrl, int32_t index) const;
    nm_status_t ui_combobox_add_item(void* ctrl, const char* text_utf8, int32_t enabled, uint32_t tag, uint32_t* out_index) const;
    nm_status_t ui_combobox_clear(void* ctrl) const;
    nm_status_t ui_combobox_subscribe_changed(void* ctrl, nm_ui_int_event_callback_t cb, void* user_data, uint64_t* out_sub);
    nm_status_t ui_listbox_get_count(void* ctrl, uint32_t* out_count) const;
    nm_status_t ui_listbox_get_selected(void* ctrl, int32_t* out_index) const;
    nm_status_t ui_listbox_set_selected(void* ctrl, int32_t index) const;
    nm_status_t ui_listbox_insert_item(void* ctrl, uint32_t index, void* child_control, uint64_t item_user_data) const;
    nm_status_t ui_listbox_clear(void* ctrl) const;
    nm_status_t ui_listbox_subscribe_changed(void* ctrl, nm_ui_int_event_callback_t cb, void* user_data, uint64_t* out_sub);
    bool        try_ui_unsubscribe(uint64_t token);

    // Stock Item Tooltip Augmentation (0.5.0)
    nm_status_t tooltip_set_item_augment_callback(nm_tooltip_augment_callback_t cb, void* user_data);
    nm_status_t tooltip_add_item_section(void* popup_control, const char* text_utf8, uint32_t rgba, int32_t add_separator) const;

    // Custom Stash Affinity Router (0.5.0)
    nm_status_t affinity_set_router_callback(nm_affinity_route_callback_t cb, void* user_data);

    // Native Extend Registries (0.5.0)
    nm_status_t geal_register_builtin(
        const char*                name_utf8,
        uint32_t                   min_args,
        uint32_t                   max_args,
        int32_t                    has_return,
        nm_geal_builtin_callback_t cb,
        void*                      user_data,
        uint64_t*                  out_handle);
    nm_status_t geal_unregister_builtin(uint64_t handle);
    nm_status_t geal_invoke_function(
        uint32_t    entity_id,
        const char* script_or_fn_utf8,
        const char* args_json_utf8,
        char*       out_result_utf8,
        size_t      capacity,
        size_t*     out_len) const;

    nm_status_t ruleset_register_native(
        uint32_t                             ruleset_id,
        const nm_native_ruleset_callbacks_t* callbacks,
        void*                                user_data,
        uint64_t*                            out_handle);
    nm_status_t ruleset_unregister_native(uint64_t handle);

    nm_status_t ui_register_custom_control_kind(
        const char*                             kind_name_utf8,
        const nm_custom_ui_control_callbacks_t* callbacks,
        void*                                   user_data,
        uint64_t*                               out_handle);
    nm_status_t ui_unregister_custom_control_kind(uint64_t handle);

    // Deterministic dispatch helpers (invoked by live hooks & unit tests)
    void    dispatch_tooltip_augment(void* popup_control, const nm_item_snapshot_t* item, int32_t alt_held);
    int32_t dispatch_affinity_route(const nm_item_snapshot_t* item, int32_t stash_kind, nm_affinity_route_result_t* out_result);
    void    dispatch_ui_int_event(int32_t ctrl_kind, void* control, int32_t value);

    // Pure helpers exposed for deterministic cross-platform C++20 unit tests.
    static bool is_stock_ruleset_slot_occupied(uint32_t ruleset_id) noexcept;
    static void apply_affinity_bitset_mask(uint8_t out7b[7], uint32_t stock_affinity_bitmask) noexcept;
    static void apply_affinity_targets(
        std::vector<nm_affinity_target_t>& targets,
        const nm_affinity_route_result_t&  res);

    // White-box proxy/indirection test helpers (deterministic across Linux & Win64).
    static bool get_geal_builtin_slot_for_handle(uint64_t handle, size_t& out_slot, uint64_t& out_gen) noexcept;
    static nm_status_t invoke_geal_indirection_slot(
        size_t       slot_idx,
        uint64_t     expected_gen,
        uint32_t     entity_id,
        const char*  args_json_utf8,
        std::string& out_result);

    struct NativeRulesetBufferView {
        const uint8_t* data;
        uint64_t       length;
        uint64_t       position;
    };
    static void* create_ruleset_proxy_for_slot(uint32_t ruleset_id, void* instance_client);
    static void  simulate_custom_ui_created(const char* kind_name_utf8, void* control);
    static bool  simulate_custom_ui_property(void* control, const char* key_utf8, const char* value_utf8);
    static bool  simulate_custom_ui_destroyed(void* control);

    // Pure MSVC std::map RB-tree unlink helper (used for clean g_uiControlFactoryMap teardown).
    struct MsvcRbNodeHeader {
        MsvcRbNodeHeader* left;
        MsvcRbNodeHeader* parent;
        MsvcRbNodeHeader* right;
        uint8_t           color; // 0 = red, 1 = black
        uint8_t           isnil; // 1 for _Myhead sentinel, 0 for real nodes
        uint8_t           pad[6];
    };
    struct MsvcRbTreeHeader {
        MsvcRbNodeHeader* head;
        uint64_t          size;
    };
    static bool rb_tree_unlink_node(MsvcRbTreeHeader* tree, MsvcRbNodeHeader* z) noexcept;

private:
    nm_build_id_t                 build_id_ = NM_BUILD_UNKNOWN;
    uint64_t                      image_base_ = 0;
    GameAllocator*                allocator_ = nullptr;
    std::vector<nm_hook_handle_t> installed_handles_;
};

} // namespace nativemods
