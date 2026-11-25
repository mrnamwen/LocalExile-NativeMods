#pragma once

#include "nativemods_abi.h"

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <mutex>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace nativemods {

// Wait-free immutable snapshot of all indexed disk overlays and callback path filters.
// Readers load OverlayAndChannels::get_active_index() with memory_order_acquire and never lock.
struct OverlayIndex {
    struct DiskFileEntry {
        std::string resolved_path;
        size_t      raw_size = 0;
        size_t      served_size = 0;
        bool        is_utf16_text = false;
    };

    uint64_t                                      generation = 0;
    bool                                          empty = true;
    bool                                          has_unfiltered_callbacks = false;
    uint64_t                                      ci_hash_mask = 0;
    std::vector<uint64_t>                         ci_hash_table;
    std::unordered_map<std::string, DiskFileEntry> disk_files;

    bool might_contain_ci_hash(uint64_t h) const noexcept {
        if (ci_hash_table.empty() || h == 0) {
            return false;
        }
        size_t slot = static_cast<size_t>(h & ci_hash_mask);
        for (;;) {
            const uint64_t cur = ci_hash_table[slot];
            if (cur == h) {
                return true;
            }
            if (cur == 0) {
                return false;
            }
            slot = (slot + 1u) & static_cast<size_t>(ci_hash_mask);
        }
    }
};

class OverlayAndChannels {
public:
    OverlayAndChannels() = default;
    ~OverlayAndChannels();

    OverlayAndChannels(const OverlayAndChannels&) = delete;
    OverlayAndChannels& operator=(const OverlayAndChannels&) = delete;

    nm_status_t register_file_overlay(
        nm_file_overlay_callback_t cb,
        nm_file_overlay_free_t     free_cb,
        void*                      user_data,
        uint64_t*                  out_token);
    nm_status_t unregister_file_overlay(uint64_t token);
    nm_status_t set_file_overlay_known_paths(
        uint64_t           token,
        const char* const* paths_utf8,
        size_t             path_count);
    nm_status_t resolve_file_overlay(const char* virtual_path_utf8, uint8_t** out_data, size_t* out_size);

    nm_status_t register_file_overlay_dir(const char* root_dir_utf8, uint64_t* out_token);
    nm_status_t unregister_file_overlay_dir(uint64_t token);
    nm_status_t has_file_overlay(const char* virtual_path_utf8, size_t* out_size);
    nm_status_t free_file_overlay_buffer(uint8_t* data);

    // Internal C++ helper: resolves overlay bytes into a caller-owned vector and immediately
    // releases any callback-owned buffer via free_cb so live hooks never leak memory.
    bool resolve_file_overlay_owned(const char* virtual_path_utf8, std::vector<uint8_t>& out_bytes);

    // Wait-free read of the current immutable overlay index snapshot (never blocks or allocates).
    const OverlayIndex* get_active_index() const noexcept {
        return active_index_.load(std::memory_order_acquire);
    }

    size_t indexed_disk_file_count() const noexcept {
        const OverlayIndex* idx = get_active_index();
        return idx ? idx->disk_files.size() : 0u;
    }

    nm_status_t register_ruleset_handler(
        uint32_t                 ruleset_id,
        nm_ruleset_rx_callback_t cb,
        void*                    user_data,
        uint64_t*                out_token);
    nm_status_t unregister_ruleset_handler(uint64_t token);
    bool        has_ruleset_handler(uint32_t ruleset_id);
    nm_status_t dispatch_ruleset_rx(uint32_t ruleset_id, const uint8_t* payload, uint32_t length);
    nm_status_t send_ruleset_packet(uint32_t ruleset_id, const uint8_t* payload, uint32_t length);

    nm_status_t register_chat_handler(nm_chat_rx_callback_t cb, void* user_data, uint64_t* out_token);
    nm_status_t unregister_chat_handler(uint64_t token);
    int32_t     dispatch_chat_rx(const char* sender_utf8, const char* text_utf8);
    nm_status_t send_chat_line(const char* text_utf8);

    nm_status_t set_tx_override(nm_packet_tx_override_t tx_fn, void* user_data);
    void        clear();

    static std::string normalize_virtual_path(const char* path);
    static uint64_t    hash_virtual_path_ci_utf8(const char* ptr, size_t len) noexcept;
    static uint64_t    hash_virtual_path_ci_wide(const wchar_t* ptr, size_t len) noexcept;

private:
    struct OverlayEntry {
        uint64_t                        token;
        nm_file_overlay_callback_t      cb;
        nm_file_overlay_free_t          free_cb;
        void*                           user_data;
        bool                            has_path_filter = false;
        std::unordered_set<std::string> known_paths;
    };
    struct OverlayDirEntry {
        uint64_t    token;
        std::string root_dir;
    };
    struct OwnedBufferRecord {
        nm_file_overlay_free_t free_cb;
        void*                  user_data;
        bool                   cpp_delete_array;
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

    static void index_disk_directory(
        const std::string&                                    root_dir,
        std::unordered_map<std::string, OverlayIndex::DiskFileEntry>& out_files);
    static bool read_disk_file(const std::string& resolved_path, std::vector<uint8_t>& out_bytes);

    void rebuild_index_locked();

    std::mutex                                      mutex_;
    uint64_t                                        next_token_ = 1;
    uint64_t                                        index_generation_ = 0;
    std::vector<OverlayEntry>                       overlays_;
    std::vector<OverlayDirEntry>                    overlay_dirs_;
    std::atomic<const OverlayIndex*>                active_index_{nullptr};
    std::vector<OverlayIndex*>                      retired_indices_;
    std::unordered_map<uint8_t*, OwnedBufferRecord> owned_buffers_;
    std::vector<RulesetEntry>                       rulesets_;
    std::vector<ChatEntry>                          chat_handlers_;
    nm_packet_tx_override_t                         tx_override_ = nullptr;
    void*                                           tx_override_ud_ = nullptr;
};

} // namespace nativemods
