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
struct OverlayIndex {
    struct DiskFileEntry {
        std::string resolved_path;
        size_t      raw_size = 0;
        size_t      served_size = 0;
        bool        is_utf16_text = false;
    };

    uint64_t                                       generation = 0;
    bool                                           empty = true;
    bool                                           has_unfiltered_callbacks = false;
    uint64_t                                       ci_hash_mask = 0;
    std::vector<uint64_t>                          ci_hash_table;
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
    class ScopedOverlayIndex {
    public:
        ScopedOverlayIndex() = default;
        ScopedOverlayIndex(const OverlayAndChannels* owner, const OverlayIndex* idx) noexcept
            : owner_(owner), index_(idx) {}
        ~ScopedOverlayIndex();

        ScopedOverlayIndex(const ScopedOverlayIndex&) = delete;
        ScopedOverlayIndex& operator=(const ScopedOverlayIndex&) = delete;

        ScopedOverlayIndex(ScopedOverlayIndex&& other) noexcept
            : owner_(other.owner_), index_(other.index_) {
            other.owner_ = nullptr;
            other.index_ = nullptr;
        }
        ScopedOverlayIndex& operator=(ScopedOverlayIndex&&) = delete;

        const OverlayIndex* get() const noexcept { return index_; }
        const OverlayIndex* operator->() const noexcept { return index_; }
        explicit operator bool() const noexcept { return index_ != nullptr; }

    private:
        const OverlayAndChannels* owner_ = nullptr;
        const OverlayIndex*       index_ = nullptr;
    };

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

    // Allocates and tracks a caller-freed copy (released via free_file_overlay_buffer).
    uint8_t* allocate_owned_buffer_copy(const uint8_t* data, size_t size);

    // Internal C++ helper: resolves overlay bytes into a caller-owned vector and immediately
    // releases any callback-owned buffer via free_cb so live hooks never leak memory.
    bool resolve_file_overlay_owned(const char* virtual_path_utf8, std::vector<uint8_t>& out_bytes);

    // Wait-free RAII reader pin of the current immutable overlay index snapshot.
    ScopedOverlayIndex acquire_index() const noexcept;

    size_t indexed_disk_file_count() const noexcept {
        ScopedOverlayIndex guard = acquire_index();
        return guard ? guard->disk_files.size() : 0u;
    }

    size_t retired_index_count() const noexcept;

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
    bool        has_chat_handlers();
    int32_t     dispatch_chat_rx(const char* sender_utf8, const char* text_utf8);
    nm_status_t send_chat_line(const char* text_utf8);

    nm_status_t register_opcode_handler_group(
        uint16_t            min_opcode,
        uint16_t            max_opcode,
        nm_opcode_handler_t cb,
        void*               user_data,
        uint64_t*           out_token);
    nm_status_t unregister_opcode_handler_group(uint64_t token);
    bool        has_opcode_handlers();
    nm_status_t dispatch_opcode_rx(
        uint16_t       opcode,
        void*          stream_handle,
        const uint8_t* payload,
        uint32_t       length,
        int32_t*       out_handled);
    nm_status_t try_send_raw_packet_override(
        uint16_t       opcode,
        const uint8_t* payload,
        uint32_t       length);

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
    struct OpcodeGroupEntry {
        uint64_t            token;
        uint16_t            min_opcode;
        uint16_t            max_opcode;
        nm_opcode_handler_t cb;
        void*               user_data;
    };

    static void index_disk_directory(
        const std::string&                                            root_dir,
        std::unordered_map<std::string, OverlayIndex::DiskFileEntry>& out_files);
    static bool read_disk_file(const std::string& resolved_path, std::vector<uint8_t>& out_bytes);

    void rebuild_index_locked();
    void try_reclaim_retired() const;

    mutable std::mutex                              mutex_;
    uint64_t                                        next_token_ = 1;
    uint64_t                                        index_generation_ = 0;
    std::vector<OverlayEntry>                       overlays_;
    std::vector<OverlayDirEntry>                    overlay_dirs_;
    std::atomic<const OverlayIndex*>                active_index_{nullptr};
    mutable std::atomic<uint32_t>                   active_readers_{0};
    mutable std::atomic<uint32_t>                   retired_count_{0};
    mutable std::vector<OverlayIndex*>              retired_indices_;
    std::unordered_map<uint8_t*, OwnedBufferRecord> owned_buffers_;
    std::vector<RulesetEntry>                       rulesets_;
    std::vector<ChatEntry>                          chat_handlers_;
    std::vector<OpcodeGroupEntry>                   opcode_groups_;
    nm_packet_tx_override_t                         tx_override_ = nullptr;
    void*                                           tx_override_ud_ = nullptr;
};

// Synthetic stream structure shared between nm_dispatch_opcode_rx and nm_stream_read.
struct SyntheticPacketStream {
    uint64_t       magic = 0x4E4D53545245414Dull; // "NMSTREAM"
    const uint8_t* data = nullptr;
    uint64_t       size = 0;
    uint64_t       cursor = 0;
};

} // namespace nativemods
