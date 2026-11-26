#pragma once

#include "nativemods_abi.h"

#include <cstdint>
#include <string>
#include <vector>

namespace nativemods {

class HookEngine;
class GameAllocator;
class OverlayAndChannels;

// Installs and coordinates all live 3.29.3d Win64 engine hooks and 0.2.0 native bridges.
class LiveEngineHooks {
public:
    LiveEngineHooks() = default;
    ~LiveEngineHooks();

    LiveEngineHooks(const LiveEngineHooks&) = delete;
    LiveEngineHooks& operator=(const LiveEngineHooks&) = delete;

    void register_startup_disk_overlays(
        const std::string&  dll_dir_utf8,
        OverlayAndChannels& overlay);

    void install_live_hooks(
        nm_build_id_t       build_id,
        uint64_t            image_base,
        HookEngine&         hooks,
        GameAllocator&      allocator,
        OverlayAndChannels& overlay);

    void reset(HookEngine* hooks);

    // Live C->S packet transmit helpers (used when no unit-test tx_override is installed).
    nm_status_t send_live_chat_line(const char* text_utf8);
    nm_status_t send_live_ruleset_packet(uint32_t ruleset_id, const uint8_t* payload, uint32_t length);
    nm_status_t send_live_raw_packet(uint16_t opcode, const uint8_t* payload, uint32_t length);

    nm_status_t stream_read(void* stream_handle, void* dst, uint64_t length);

    nm_status_t files_read_stock(
        const char*         virtual_path_utf8,
        OverlayAndChannels& overlay,
        const GameAllocator& allocator,
        uint8_t**           out_data,
        size_t*             out_size);
    nm_status_t files_invalidate(const char* virtual_path_utf8);

    nm_status_t get_active_connection_token(uint64_t* out_token) const;

    // Live native UI helpers (0.1.0 + 0.2.0).
    nm_status_t ui_get_root(void** out_root);
    nm_status_t ui_create_window(const char* virtual_path_utf8, void** out_control);
    nm_status_t ui_create_window_into(void* parent_control, const char* virtual_path_utf8, void** out_control);
    nm_status_t ui_find_child(void* parent_control, const char* dot_path_utf8, void** out_child);
    nm_status_t ui_set_visible(void* control, int32_t visible);
    nm_status_t ui_set_text(void* control, const char* text_utf8, int32_t is_button);
    nm_status_t ui_subscribe_click(
        void*                  button_control,
        nm_ui_event_callback_t cb,
        void*                  user_data,
        uint64_t*              out_token);
    nm_status_t ui_get_rect(void* control, float* out_x, float* out_y, float* out_w, float* out_h);
    nm_status_t ui_set_position(void* control, float x, float y);
    nm_status_t ui_set_size(void* control, float w, float h);
    nm_status_t ui_set_enabled(void* control, int32_t enabled);
    nm_status_t ui_get_enabled(void* control, int32_t* out_enabled);
    nm_status_t ui_remove_child(void* parent_control, void* child_control);
    nm_status_t ui_destroy_control(void* control);
    nm_status_t ui_textbox_get_text(void* textbox_control, char* out_utf8, size_t capacity, size_t* out_length);
    nm_status_t ui_textbox_set_text(void* textbox_control, const char* text_utf8);
    nm_status_t ui_textbox_subscribe_changed(
        void*                  textbox_control,
        nm_ui_event_callback_t cb,
        void*                  user_data,
        uint64_t*              out_token);
    nm_status_t ui_textbox_subscribe_submitted(
        void*                  textbox_control,
        nm_ui_event_callback_t cb,
        void*                  user_data,
        uint64_t*              out_token);
    nm_status_t ui_checkbox_get_checked(void* checkbox_control, int32_t* out_checked);
    nm_status_t ui_checkbox_set_checked(void* checkbox_control, int32_t checked);
    nm_status_t ui_checkbox_subscribe_changed(
        void*                  checkbox_control,
        nm_ui_event_callback_t cb,
        void*                  user_data,
        uint64_t*              out_token);
    nm_status_t ui_unsubscribe(uint64_t token);

    // Clipboard helpers (Win32 CF_UNICODETEXT on Windows, in-memory mutex store on Linux).
    nm_status_t clipboard_get_text(char* out_utf8, size_t capacity, size_t* out_length);
    nm_status_t clipboard_set_text(const char* text_utf8);

    // Wire-format encoders exposed for deterministic cross-platform C++20 unit tests.
    static std::vector<uint8_t> encode_c2s_chat_packet(const char* text_utf8);
    static std::vector<uint8_t> encode_c2s_ruleset_packet(uint32_t ruleset_id, const uint8_t* payload, uint32_t length);
    static std::vector<uint8_t> encode_c2s_raw_packet(uint16_t opcode, const uint8_t* payload, uint32_t length);

private:
    nm_status_t ui_create_window_in_parent(void* parent_control, const char* virtual_path_utf8, void** out_control);

    nm_build_id_t                 build_id_ = NM_BUILD_UNKNOWN;
    uint64_t                      image_base_ = 0;
    HookEngine*                   hooks_ = nullptr;
    std::vector<nm_hook_handle_t> installed_handles_;
};

} // namespace nativemods
