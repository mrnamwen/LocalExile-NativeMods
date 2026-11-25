#include "overlay_and_channels.hpp"
#include "exception_wall.hpp"

#include <algorithm>
#include <cctype>
#include <cstring>

namespace nativemods {

std::string OverlayAndChannels::normalize_virtual_path(const char* path) {
    if (!path) return {};
    std::string out;
    out.reserve(std::strlen(path));
    for (const unsigned char* p = reinterpret_cast<const unsigned char*>(path); *p != 0; ++p) {
        char c = (*p == '\\') ? '/' : static_cast<char>(std::tolower(*p));
        if (c == '/' && out.empty()) {
            continue;
        }
        out.push_back(c);
    }
    return out;
}

nm_status_t OverlayAndChannels::register_file_overlay(
    nm_file_overlay_callback_t cb,
    nm_file_overlay_free_t     free_cb,
    void*                      user_data,
    uint64_t*                  out_token) {
    if (cb == nullptr || out_token == nullptr) {
        return NM_ERR_INVALID_ARG;
    }
    std::lock_guard<std::mutex> lock(mutex_);
    const uint64_t token = next_token_++;
    overlays_.push_back(OverlayEntry{token, cb, free_cb, user_data});
    *out_token = token;
    return NM_OK;
}

nm_status_t OverlayAndChannels::unregister_file_overlay(uint64_t token) {
    std::lock_guard<std::mutex> lock(mutex_);
    auto it = std::remove_if(overlays_.begin(), overlays_.end(),
                             [token](const OverlayEntry& e) { return e.token == token; });
    if (it == overlays_.end()) {
        return NM_ERR_INVALID_ARG;
    }
    overlays_.erase(it, overlays_.end());
    return NM_OK;
}

nm_status_t OverlayAndChannels::resolve_file_overlay(const char* virtual_path_utf8, uint8_t** out_data, size_t* out_size) {
    if (virtual_path_utf8 == nullptr || out_data == nullptr || out_size == nullptr) {
        return NM_ERR_INVALID_ARG;
    }
    *out_data = nullptr;
    *out_size = 0;

    const std::string normalized = normalize_virtual_path(virtual_path_utf8);
    std::vector<OverlayEntry> snapshot;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        snapshot = overlays_;
    }

    // Latest registered overlay wins (LIFO precedence).
    for (auto it = snapshot.rbegin(); it != snapshot.rend(); ++it) {
        uint8_t* data = nullptr;
        size_t size = 0;
        if (it->cb(normalized.c_str(), &data, &size, it->user_data) == NM_OK && data != nullptr) {
            *out_data = data;
            *out_size = size;
            return NM_OK;
        }
    }
    return NM_ERR_SYMBOL_NOT_FOUND;
}

nm_status_t OverlayAndChannels::register_ruleset_handler(
    uint32_t                 ruleset_id,
    nm_ruleset_rx_callback_t cb,
    void*                    user_data,
    uint64_t*                out_token) {
    if (cb == nullptr || out_token == nullptr || ruleset_id >= NM_RULESET_SLOT_COUNT) {
        return NM_ERR_INVALID_ARG;
    }
    std::lock_guard<std::mutex> lock(mutex_);
    const uint64_t token = next_token_++;
    rulesets_.push_back(RulesetEntry{token, ruleset_id, cb, user_data});
    *out_token = token;
    return NM_OK;
}

nm_status_t OverlayAndChannels::unregister_ruleset_handler(uint64_t token) {
    std::lock_guard<std::mutex> lock(mutex_);
    auto it = std::remove_if(rulesets_.begin(), rulesets_.end(),
                             [token](const RulesetEntry& e) { return e.token == token; });
    if (it == rulesets_.end()) {
        return NM_ERR_INVALID_ARG;
    }
    rulesets_.erase(it, rulesets_.end());
    return NM_OK;
}

nm_status_t OverlayAndChannels::dispatch_ruleset_rx(uint32_t ruleset_id, const uint8_t* payload, uint32_t length) {
    if (ruleset_id >= NM_RULESET_SLOT_COUNT) {
        return NM_ERR_INVALID_ARG;
    }
    if (length > NM_RULESET_MAX_PAYLOAD) {
        return NM_ERR_PAYLOAD_TOO_LARGE;
    }
    if (length > 0 && payload == nullptr) {
        return NM_ERR_INVALID_ARG;
    }

    std::vector<RulesetEntry> matched;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        for (const auto& entry : rulesets_) {
            if (entry.ruleset_id == ruleset_id) {
                matched.push_back(entry);
            }
        }
    }

    // Per 10-verification.md §3: an update for an unregistered ruleset id is silently dropped.
    struct RulesetCtx {
        nm_ruleset_rx_callback_t cb;
        uint32_t                 id;
        const uint8_t*           payload;
        uint32_t                 length;
        void*                    ud;
    };
    auto trampoline = [](void* raw) -> int64_t {
        auto* c = static_cast<RulesetCtx*>(raw);
        c->cb(c->id, c->payload, c->length, c->ud);
        return 0;
    };

    for (const auto& entry : matched) {
        RulesetCtx ctx{entry.cb, ruleset_id, payload, length, entry.user_data};
        nm_exception_info_t ex{};
        (void)invoke_guarded(trampoline, &ctx, nullptr, &ex);
    }
    return NM_OK;
}

nm_status_t OverlayAndChannels::send_ruleset_packet(uint32_t ruleset_id, const uint8_t* payload, uint32_t length) {
    if (ruleset_id >= NM_RULESET_SLOT_COUNT) {
        return NM_ERR_INVALID_ARG;
    }
    if (length > NM_RULESET_MAX_PAYLOAD) {
        return NM_ERR_PAYLOAD_TOO_LARGE;
    }
    if (length > 0 && payload == nullptr) {
        return NM_ERR_INVALID_ARG;
    }

    nm_packet_tx_override_t tx = nullptr;
    void* ud = nullptr;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        tx = tx_override_;
        ud = tx_override_ud_;
    }

    if (tx) {
        return tx(NM_OPCODE_RULESET_C2S, ruleset_id, payload, length, ud);
    }
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
}

nm_status_t OverlayAndChannels::register_chat_handler(nm_chat_rx_callback_t cb, void* user_data, uint64_t* out_token) {
    if (cb == nullptr || out_token == nullptr) {
        return NM_ERR_INVALID_ARG;
    }
    std::lock_guard<std::mutex> lock(mutex_);
    const uint64_t token = next_token_++;
    chat_handlers_.push_back(ChatEntry{token, cb, user_data});
    *out_token = token;
    return NM_OK;
}

nm_status_t OverlayAndChannels::unregister_chat_handler(uint64_t token) {
    std::lock_guard<std::mutex> lock(mutex_);
    auto it = std::remove_if(chat_handlers_.begin(), chat_handlers_.end(),
                             [token](const ChatEntry& e) { return e.token == token; });
    if (it == chat_handlers_.end()) {
        return NM_ERR_INVALID_ARG;
    }
    chat_handlers_.erase(it, chat_handlers_.end());
    return NM_OK;
}

int32_t OverlayAndChannels::dispatch_chat_rx(const char* sender_utf8, const char* text_utf8) {
    if (text_utf8 == nullptr) {
        return 0;
    }
    constexpr size_t kPrefixLen = 5; // "##NM "
    if (std::strncmp(text_utf8, NM_CHAT_FALLBACK_PREFIX, kPrefixLen) != 0) {
        return 0; // Normal game chat; pass through to original ChatPanel::OnChatMessage.
    }

    std::vector<ChatEntry> snapshot;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        snapshot = chat_handlers_;
    }

    struct ChatCtx {
        nm_chat_rx_callback_t cb;
        const char*           sender;
        const char*           text;
        void*                 ud;
    };
    auto trampoline = [](void* raw) -> int64_t {
        auto* c = static_cast<ChatCtx*>(raw);
        c->cb(c->sender ? c->sender : "", c->text, c->ud);
        return 0;
    };

    for (const auto& entry : snapshot) {
        ChatCtx ctx{entry.cb, sender_utf8, text_utf8, entry.user_data};
        nm_exception_info_t ex{};
        (void)invoke_guarded(trampoline, &ctx, nullptr, &ex);
    }
    return 1; // Intercepted; suppress from game chat panel.
}

nm_status_t OverlayAndChannels::send_chat_line(const char* text_utf8) {
    if (text_utf8 == nullptr || text_utf8[0] == '\0') {
        return NM_ERR_INVALID_ARG;
    }
    nm_packet_tx_override_t tx = nullptr;
    void* ud = nullptr;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        tx = tx_override_;
        ud = tx_override_ud_;
    }

    if (tx) {
        const auto* bytes = reinterpret_cast<const uint8_t*>(text_utf8);
        const uint32_t len = static_cast<uint32_t>(std::strlen(text_utf8));
        return tx(NM_OPCODE_CHAT_C2S, 0, bytes, len, ud);
    }
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
}

nm_status_t OverlayAndChannels::set_tx_override(nm_packet_tx_override_t tx_fn, void* user_data) {
    std::lock_guard<std::mutex> lock(mutex_);
    tx_override_ = tx_fn;
    tx_override_ud_ = user_data;
    return NM_OK;
}

void OverlayAndChannels::clear() {
    std::lock_guard<std::mutex> lock(mutex_);
    overlays_.clear();
    rulesets_.clear();
    chat_handlers_.clear();
    tx_override_ = nullptr;
    tx_override_ud_ = nullptr;
}

} // namespace nativemods
