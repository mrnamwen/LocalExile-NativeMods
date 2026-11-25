#pragma once

#include "nativemods_abi.h"

#include <cstdint>
#include <mutex>
#include <string>
#include <vector>

namespace nativemods {

class OverlayAndChannels {
public:
    nm_status_t register_file_overlay(
        nm_file_overlay_callback_t cb,
        nm_file_overlay_free_t     free_cb,
        void*                      user_data,
        uint64_t*                  out_token);
    nm_status_t unregister_file_overlay(uint64_t token);
    nm_status_t resolve_file_overlay(const char* virtual_path_utf8, uint8_t** out_data, size_t* out_size);

    nm_status_t register_ruleset_handler(
        uint32_t                 ruleset_id,
        nm_ruleset_rx_callback_t cb,
        void*                    user_data,
        uint64_t*                out_token);
    nm_status_t unregister_ruleset_handler(uint64_t token);
    nm_status_t dispatch_ruleset_rx(uint32_t ruleset_id, const uint8_t* payload, uint32_t length);
    nm_status_t send_ruleset_packet(uint32_t ruleset_id, const uint8_t* payload, uint32_t length);

    nm_status_t register_chat_handler(nm_chat_rx_callback_t cb, void* user_data, uint64_t* out_token);
    nm_status_t unregister_chat_handler(uint64_t token);
    int32_t     dispatch_chat_rx(const char* sender_utf8, const char* text_utf8);
    nm_status_t send_chat_line(const char* text_utf8);

    nm_status_t set_tx_override(nm_packet_tx_override_t tx_fn, void* user_data);
    void        clear();

    static std::string normalize_virtual_path(const char* path);

private:
    struct OverlayEntry {
        uint64_t                   token;
        nm_file_overlay_callback_t cb;
        nm_file_overlay_free_t     free_cb;
        void*                      user_data;
    };
    struct RulesetEntry {
        uint64_t                 token;
        uint32_t                 ruleset_id;
        nm_ruleset_rx_callback_t cb;
        void*                    user_data;
    };
    struct ChatEntry {
        uint64_t              token;
        nm_chat_rx_callback_t cb;
        void*                 user_data;
    };

    std::mutex                mutex_;
    uint64_t                  next_token_ = 1;
    std::vector<OverlayEntry> overlays_;
    std::vector<RulesetEntry> rulesets_;
    std::vector<ChatEntry>    chat_handlers_;
    nm_packet_tx_override_t   tx_override_ = nullptr;
    void*                     tx_override_ud_ = nullptr;
};

} // namespace nativemods
