#pragma once

#include "nativemods_abi.h"

#include <cstdint>
#include <deque>
#include <mutex>
#include <vector>

namespace nativemods {

// Thread-safe main-thread work queue & per-frame callback dispatcher.
class FrameQueue {
public:
    static constexpr size_t kMaxPendingWork = 4096;

    nm_status_t register_frame_callback(nm_frame_callback_t cb, void* user_data, uint64_t* out_token);
    nm_status_t unregister_frame_callback(uint64_t token);
    nm_status_t enqueue(nm_work_callback_t cb, void* user_data);

    // Executes up to `max_items` queued work items (0 = unlimited) and invokes registered
    // frame callbacks. Every callback is wrapped inside invoke_guarded so exceptions never
    // unwind across the game's StateStack::PassA frame hook.
    nm_status_t drain(float delta_seconds, uint32_t max_items);

    void clear();

private:
    struct FrameEntry {
        uint64_t            token;
        nm_frame_callback_t cb;
        void*               user_data;
    };
    struct WorkItem {
        nm_work_callback_t cb;
        void*              user_data;
    };

    std::mutex              mutex_;
    uint64_t                next_token_ = 1;
    std::vector<FrameEntry> frame_callbacks_;
    std::deque<WorkItem>    pending_;
};

} // namespace nativemods
