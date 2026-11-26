#pragma once

#include "nativemods_abi.h"

#include <cstddef>
#include <cstdint>
#include <vector>

namespace nativemods {

class HookEngine;

// Phase 1 (ABI 0.3.0) native services:
//   - Stock-tree UI traversal & layout helpers (ui_get_active_root, children, vtable, tag, id, parent, visible, width/height, checkbox/stack creation, anchor, layout update)
//   - Screens & Tabs inspection + lifecycle event callbacks (InGameUI::Init, InGameState::Leave, LeagueOverlay::Fill, CreateCharacterState::Enter/Leave, TabContainer::SelectTab, MapStashPage)
//   - Low-level suppressible input hook (StateStack::OnWindowMessage) + TextBox::AsTextInput focus check
//   - Hover & Cursor item inspection (UIManager+0x230, InventoryItemView, Cursor::GetHeldItem, item components)
//   - Pre-Game character creation S78 start_state rewrite (LoginClient::WriteCreateCharacter_0x12) & S->C 0x13 result hook
class Phase1Services {
public:
    Phase1Services() = default;
    ~Phase1Services();

    Phase1Services(const Phase1Services&) = delete;
    Phase1Services& operator=(const Phase1Services&) = delete;

    void install_live_hooks(nm_build_id_t build_id, uint64_t image_base, HookEngine& hooks);
    void reset(HookEngine* hooks);

    // Native stock-tree UI traversal & layout helpers (0.3.0)
    nm_status_t ui_get_active_root(void* fallback_captured_root, void** out_root) const;
    nm_status_t ui_get_child_count(void* parent_control, uint32_t* out_count) const;
    nm_status_t ui_get_child_at(void* parent_control, uint32_t index, void** out_child) const;
    nm_status_t ui_get_vtable(void* control, uint64_t* out_vtable) const;
    nm_status_t ui_get_control_tag(void* control, uint16_t* out_tag) const;
    nm_status_t ui_get_control_id(void* control, char* out_utf8, size_t capacity, size_t* out_length) const;
    nm_status_t ui_get_control_parent(void* control, void** out_parent) const;
    nm_status_t ui_is_visible(void* control, int32_t* out_visible) const;
    nm_status_t ui_set_width(void* control, float width) const;
    nm_status_t ui_set_height(void* control, float height) const;
    nm_status_t ui_create_checkbox_child(void* parent_control, const char* caption_utf8, void** out_checkbox) const;
    nm_status_t ui_create_stack_layout(void* parent_control, int32_t vertical, void** out_layout) const;
    nm_status_t ui_layout_update(void* control) const;
    nm_status_t ui_set_anchor(void* control, uint32_t x_anchor, float x_offset, uint32_t y_anchor, float y_offset) const;

    // Screens & Tabs (0.3.0)
    nm_status_t screens_get_state(nm_screens_state_t* out_state) const;
    nm_status_t screens_set_callback(nm_screen_event_callback_t cb, void* user_data);

    // Input interception & text focus (0.3.0)
    nm_status_t input_set_callback(nm_input_callback_t cb, void* user_data);
    nm_status_t input_is_text_focused(int32_t* out_focused) const;

    // Hover & Cursor item inspection (0.3.0)
    nm_status_t hover_get_state(nm_hover_state_t* out_state) const;

    // Pre-Game character creation hooks (0.3.0)
    nm_status_t pregame_set_callbacks(
        nm_pregame_creating_callback_t on_creating,
        nm_pregame_created_callback_t  on_created,
        void*                          user_data);

    // Deterministic dispatch helpers (invoked by live hooks & unit tests)
    void    dispatch_screen_event(int32_t event_kind, void* primary_handle, uint64_t param0);
    int32_t dispatch_input_event(const nm_input_event_t* ev);
    int32_t dispatch_pregame_creating(
        const char* name_utf8,
        const char* league_utf8,
        const char* start_state_utf8,
        char*       out_start_state_utf8,
        size_t      out_capacity);
    void    dispatch_pregame_created(
        const char* name_utf8,
        const char* league_utf8,
        uint16_t    result_code);

private:
    nm_build_id_t                 build_id_ = NM_BUILD_UNKNOWN;
    uint64_t                      image_base_ = 0;
    std::vector<nm_hook_handle_t> installed_handles_;
};

} // namespace nativemods
