#include "frame_queue.hpp"
#include "exception_wall.hpp"

#include <algorithm>

namespace nativemods {

namespace {

struct WorkDispatchCtx {
    nm_work_callback_t cb;
    void*              user_data;
};

int64_t NM_CALL run_work_trampoline(void* raw) {
    auto* ctx = static_cast<WorkDispatchCtx*>(raw);
    ctx->cb(ctx->user_data);
    return 0;
}

struct FrameDispatchCtx {
    nm_frame_callback_t cb;
    float               dt;
    void*               user_data;
};

int64_t NM_CALL run_frame_trampoline(void* raw) {
    auto* ctx = static_cast<FrameDispatchCtx*>(raw);
    ctx->cb(ctx->dt, ctx->user_data);
    return 0;
}

} // namespace

nm_status_t FrameQueue::register_frame_callback(nm_frame_callback_t cb, void* user_data, uint64_t* out_token) {
    if (cb == nullptr || out_token == nullptr) {
        return NM_ERR_INVALID_ARG;
    }
    std::lock_guard<std::mutex> lock(mutex_);
    const uint64_t token = next_token_++;
    frame_callbacks_.push_back(FrameEntry{token, cb, user_data});
    *out_token = token;
    return NM_OK;
}

nm_status_t FrameQueue::unregister_frame_callback(uint64_t token) {
    std::lock_guard<std::mutex> lock(mutex_);
    auto it = std::remove_if(frame_callbacks_.begin(), frame_callbacks_.end(),
                             [token](const FrameEntry& e) { return e.token == token; });
    if (it == frame_callbacks_.end()) {
        return NM_ERR_INVALID_ARG;
    }
    frame_callbacks_.erase(it, frame_callbacks_.end());
    return NM_OK;
}

nm_status_t FrameQueue::enqueue(nm_work_callback_t cb, void* user_data) {
    if (cb == nullptr) {
        return NM_ERR_INVALID_ARG;
    }
    std::lock_guard<std::mutex> lock(mutex_);
    if (pending_.size() >= kMaxPendingWork) {
        return NM_ERR_QUEUE_FULL;
    }
    pending_.push_back(WorkItem{cb, user_data});
    return NM_OK;
}

nm_status_t FrameQueue::drain(float delta_seconds, uint32_t max_items) {
    std::vector<WorkItem> batch;
    std::vector<FrameEntry> frame_snapshot;

    {
        std::lock_guard<std::mutex> lock(mutex_);
        const size_t limit = (max_items == 0) ? pending_.size() : std::min<size_t>(max_items, pending_.size());
        batch.reserve(limit);
        for (size_t i = 0; i < limit; ++i) {
            batch.push_back(pending_.front());
            pending_.pop_front();
        }
        frame_snapshot = frame_callbacks_;
    }

    for (const auto& item : batch) {
        WorkDispatchCtx ctx{item.cb, item.user_data};
        nm_exception_info_t ex{};
        (void)invoke_guarded(&run_work_trampoline, &ctx, nullptr, &ex);
    }

    for (const auto& entry : frame_snapshot) {
        FrameDispatchCtx ctx{entry.cb, delta_seconds, entry.user_data};
        nm_exception_info_t ex{};
        (void)invoke_guarded(&run_frame_trampoline, &ctx, nullptr, &ex);
    }

    return NM_OK;
}

void FrameQueue::clear() {
    std::lock_guard<std::mutex> lock(mutex_);
    pending_.clear();
    frame_callbacks_.clear();
}

} // namespace nativemods
