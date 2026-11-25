#pragma once

#include "nativemods_abi.h"

#include <cstdint>
#include <string>
#include <vector>

namespace nativemods {

class HookEngine;
class GameAllocator;
class OverlayAndChannels;

// Installs and coordinates all live 3.29.3d Win64 engine hooks:
//   - Route A Universal File Overlay (exists_query, size_query, read_variant_2, read_whole_file, read_variant_5)
//   - UI Root Observer (ui.control_load) & Native UI API (CreateWindow, FindChild, SetVisible, SetText, SubscribeClick)
//   - Mod Channels (network.chat_panel_listener S->C ##NM, rulesets.s2c_0x2c9_filter S->C 0x2c9,
//                   C->S 0x15 chat & 0x2ca ruleset writers under socket+0x248 SRW lock)
class LiveEngineHooks {
public:
    LiveEngineHooks() = default;
    ~LiveEngineHooks();

    LiveEngineHooks(const LiveEngineHooks&) = delete;
    LiveEngineHooks& operator=(const LiveEngineHooks&) = delete;

    // Discovers native disk overlay roots from NATIVEMODS_OVERLAY_DIR and Mods/Client/*/{client,files}
    // before CoreCLR boots so early .datc64/.ot/.ao/.ui reads are intercepted from millisecond 0.
    void register_startup_disk_overlays(
        const std::string&  dll_dir_utf8,
        OverlayAndChannels& overlay);

    // Arms all verified 3.29.3d Win64 native hooks (FileSystem, UI root observer, Chat, Ruleset 0x2c9).
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

    // Live native UI helpers.
    nm_status_t ui_get_root(void** out_root);
    nm_status_t ui_create_window(const char* virtual_path_utf8, void** out_control);
    nm_status_t ui_find_child(void* parent_control, const char* dot_path_utf8, void** out_child);
    nm_status_t ui_set_visible(void* control, int32_t visible);
    nm_status_t ui_set_text(void* control, const char* text_utf8, int32_t is_button);
    nm_status_t ui_subscribe_click(
        void*                  button_control,
        nm_ui_event_callback_t cb,
        void*                  user_data,
        uint64_t*              out_token);

    // Wire-format encoders exposed for deterministic cross-platform C++20 unit tests.
    static std::vector<uint8_t> encode_c2s_chat_packet(const char* text_utf8);
    static std::vector<uint8_t> encode_c2s_ruleset_packet(uint32_t ruleset_id, const uint8_t* payload, uint32_t length);

private:
    nm_build_id_t               build_id_ = NM_BUILD_UNKNOWN;
    uint64_t                    image_base_ = 0;
    std::vector<nm_hook_handle_t> installed_handles_;
};

} // namespace nativemods
