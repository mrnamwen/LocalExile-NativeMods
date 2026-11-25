#include "overlay_and_channels.hpp"
#include "exception_wall.hpp"

#include <algorithm>
#include <cctype>
#include <cstring>
#include <filesystem>
#include <fstream>

#if defined(_WIN32)
#  define WIN32_LEAN_AND_MEAN
#  include <windows.h>
#endif

namespace nativemods {

namespace {

bool is_safe_relative_virtual_path(const std::string& normalized) {
    if (normalized.empty()) return false;
    if (normalized[0] == '/' || normalized.find(':') != std::string::npos) return false;
    if (normalized.find("..") != std::string::npos) {
        size_t pos = 0;
        while ((pos = normalized.find("..", pos)) != std::string::npos) {
            const bool left_ok = (pos == 0 || normalized[pos - 1] == '/');
            const bool right_ok = (pos + 2 == normalized.size() || normalized[pos + 2] == '/');
            if (left_ok && right_ok) return false;
            pos += 2;
        }
    }
    return true;
}

#if defined(_WIN32)
std::wstring utf8_to_wide_local(const std::string& s) {
    if (s.empty()) return {};
    const int needed = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()), nullptr, 0);
    if (needed <= 0) return {};
    std::wstring out(static_cast<size_t>(needed), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()), out.data(), needed);
    return out;
}
#endif

} // namespace

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

bool OverlayAndChannels::stat_disk_overlay(
    const std::string& root_dir,
    const std::string& normalized_rel,
    size_t&            out_size,
    std::string&       out_resolved_path) {
    out_size = 0;
    out_resolved_path.clear();
    if (root_dir.empty() || !is_safe_relative_virtual_path(normalized_rel)) {
        return false;
    }

#if defined(_WIN32)
    // Windows / Wine / Proton paths are case-insensitive natively.
    std::string combined = root_dir;
    if (!combined.empty() && combined.back() != '\\' && combined.back() != '/') {
        combined.push_back('\\');
    }
    for (char c : normalized_rel) {
        combined.push_back(c == '/' ? '\\' : c);
    }
    const std::wstring wpath = utf8_to_wide_local(combined);
    WIN32_FILE_ATTRIBUTE_DATA fad{};
    if (GetFileAttributesExW(wpath.c_str(), GetFileExInfoStandard, &fad) != 0 &&
        (fad.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0) {
        const uint64_t sz = (static_cast<uint64_t>(fad.nFileSizeHigh) << 32) | static_cast<uint64_t>(fad.nFileSizeLow);
        out_size = static_cast<size_t>(sz);
        out_resolved_path = std::move(combined);
        return true;
    }
    return false;
#else
    namespace fs = std::filesystem;
    std::error_code ec;
    fs::path cur(root_dir);
    if (!fs::exists(cur, ec) || !fs::is_directory(cur, ec)) {
        return false;
    }

    // Fast path: direct relative lookup.
    fs::path direct = cur / normalized_rel;
    if (fs::is_regular_file(direct, ec)) {
        const auto sz = fs::file_size(direct, ec);
        if (!ec) {
            out_size = static_cast<size_t>(sz);
            out_resolved_path = direct.string();
            return true;
        }
    }

    // Case-insensitive component walk for Linux host tests.
    size_t start = 0;
    while (start < normalized_rel.size()) {
        size_t slash = normalized_rel.find('/', start);
        const std::string seg = (slash == std::string::npos)
                                    ? normalized_rel.substr(start)
                                    : normalized_rel.substr(start, slash - start);
        if (seg.empty()) {
            if (slash == std::string::npos) break;
            start = slash + 1;
            continue;
        }

        fs::path next = cur / seg;
        if (fs::exists(next, ec)) {
            cur = std::move(next);
        } else {
            bool matched = false;
            for (const auto& entry : fs::directory_iterator(cur, ec)) {
                if (ec) break;
                std::string name = entry.path().filename().string();
                std::transform(name.begin(), name.end(), name.begin(),
                               [](unsigned char ch) { return static_cast<char>(std::tolower(ch)); });
                if (name == seg) {
                    cur = entry.path();
                    matched = true;
                    break;
                }
            }
            if (!matched) {
                return false;
            }
        }

        if (slash == std::string::npos) break;
        start = slash + 1;
    }

    if (fs::is_regular_file(cur, ec)) {
        const auto sz = fs::file_size(cur, ec);
        if (!ec) {
            out_size = static_cast<size_t>(sz);
            out_resolved_path = cur.string();
            return true;
        }
    }
    return false;
#endif
}

bool OverlayAndChannels::read_disk_file(const std::string& resolved_path, std::vector<uint8_t>& out_bytes) {
    out_bytes.clear();
#if defined(_WIN32)
    const std::wstring wpath = utf8_to_wide_local(resolved_path);
    HANDLE h = CreateFileW(
        wpath.c_str(),
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    if (h == INVALID_HANDLE_VALUE) {
        return false;
    }
    LARGE_INTEGER li{};
    if (!GetFileSizeEx(h, &li) || li.QuadPart < 0) {
        CloseHandle(h);
        return false;
    }
    const size_t total = static_cast<size_t>(li.QuadPart);
    out_bytes.resize(total);
    size_t offset = 0;
    while (offset < total) {
        const DWORD chunk = static_cast<DWORD>(std::min<size_t>(total - offset, 0x40000000u));
        DWORD read_now = 0;
        if (!ReadFile(h, out_bytes.data() + offset, chunk, &read_now, nullptr) || read_now == 0) {
            CloseHandle(h);
            out_bytes.clear();
            return false;
        }
        offset += read_now;
    }
    CloseHandle(h);
    return true;
#else
    std::ifstream ifs(resolved_path, std::ios::binary | std::ios::ate);
    if (!ifs) return false;
    const std::streamoff sz = ifs.tellg();
    if (sz < 0) return false;
    ifs.seekg(0, std::ios::beg);
    out_bytes.resize(static_cast<size_t>(sz));
    if (sz > 0 && !ifs.read(reinterpret_cast<char*>(out_bytes.data()), sz)) {
        out_bytes.clear();
        return false;
    }
    return true;
#endif
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

nm_status_t OverlayAndChannels::register_file_overlay_dir(const char* root_dir_utf8, uint64_t* out_token) {
    if (root_dir_utf8 == nullptr || root_dir_utf8[0] == '\0' || out_token == nullptr) {
        return NM_ERR_INVALID_ARG;
    }
    std::lock_guard<std::mutex> lock(mutex_);
    const uint64_t token = next_token_++;
    overlay_dirs_.push_back(OverlayDirEntry{token, std::string(root_dir_utf8)});
    *out_token = token;
    return NM_OK;
}

nm_status_t OverlayAndChannels::unregister_file_overlay_dir(uint64_t token) {
    std::lock_guard<std::mutex> lock(mutex_);
    auto it = std::remove_if(overlay_dirs_.begin(), overlay_dirs_.end(),
                             [token](const OverlayDirEntry& e) { return e.token == token; });
    if (it == overlay_dirs_.end()) {
        return NM_ERR_INVALID_ARG;
    }
    overlay_dirs_.erase(it, overlay_dirs_.end());
    return NM_OK;
}

nm_status_t OverlayAndChannels::has_file_overlay(const char* virtual_path_utf8, size_t* out_size) {
    if (virtual_path_utf8 == nullptr) {
        return NM_ERR_INVALID_ARG;
    }
    if (out_size) {
        *out_size = 0;
    }

    const std::string normalized = normalize_virtual_path(virtual_path_utf8);
    if (normalized.empty()) {
        return NM_ERR_SYMBOL_NOT_FOUND;
    }

    std::vector<OverlayEntry> cb_snapshot;
    std::vector<OverlayDirEntry> dir_snapshot;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        cb_snapshot = overlays_;
        dir_snapshot = overlay_dirs_;
    }

    // 1. Managed / dynamic callbacks (LIFO).
    for (auto it = cb_snapshot.rbegin(); it != cb_snapshot.rend(); ++it) {
        uint8_t* data = nullptr;
        size_t size = 0;
        if (it->cb(normalized.c_str(), &data, &size, it->user_data) == NM_OK && data != nullptr) {
            if (it->free_cb) {
                it->free_cb(data, it->user_data);
            }
            if (out_size) {
                *out_size = size;
            }
            return NM_OK;
        }
    }

    // 2. Native disk overlay directories (LIFO, fast stat without reading file).
    for (auto it = dir_snapshot.rbegin(); it != dir_snapshot.rend(); ++it) {
        size_t sz = 0;
        std::string resolved;
        if (stat_disk_overlay(it->root_dir, normalized, sz, resolved)) {
            if (out_size) {
                *out_size = sz;
            }
            return NM_OK;
        }
    }

    return NM_ERR_SYMBOL_NOT_FOUND;
}

bool OverlayAndChannels::resolve_file_overlay_owned(const char* virtual_path_utf8, std::vector<uint8_t>& out_bytes) {
    out_bytes.clear();
    if (virtual_path_utf8 == nullptr) {
        return false;
    }

    const std::string normalized = normalize_virtual_path(virtual_path_utf8);
    if (normalized.empty()) {
        return false;
    }

    std::vector<OverlayEntry> cb_snapshot;
    std::vector<OverlayDirEntry> dir_snapshot;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        cb_snapshot = overlays_;
        dir_snapshot = overlay_dirs_;
    }

    // 1. Managed / dynamic callbacks take highest precedence (LIFO).
    for (auto it = cb_snapshot.rbegin(); it != cb_snapshot.rend(); ++it) {
        uint8_t* data = nullptr;
        size_t size = 0;
        if (it->cb(normalized.c_str(), &data, &size, it->user_data) == NM_OK && data != nullptr) {
            out_bytes.assign(data, data + size);
            if (it->free_cb) {
                it->free_cb(data, it->user_data);
            }
            return true;
        }
    }

    // 2. Native disk overlay directories (LIFO — includes early-boot NATIVEMODS_OVERLAY_DIR).
    for (auto it = dir_snapshot.rbegin(); it != dir_snapshot.rend(); ++it) {
        size_t sz = 0;
        std::string resolved;
        if (stat_disk_overlay(it->root_dir, normalized, sz, resolved)) {
            if (read_disk_file(resolved, out_bytes)) {
                return true;
            }
        }
    }

    return false;
}

nm_status_t OverlayAndChannels::resolve_file_overlay(const char* virtual_path_utf8, uint8_t** out_data, size_t* out_size) {
    if (virtual_path_utf8 == nullptr || out_data == nullptr || out_size == nullptr) {
        return NM_ERR_INVALID_ARG;
    }
    *out_data = nullptr;
    *out_size = 0;

    const std::string normalized = normalize_virtual_path(virtual_path_utf8);
    if (normalized.empty()) {
        return NM_ERR_SYMBOL_NOT_FOUND;
    }

    std::vector<OverlayEntry> cb_snapshot;
    std::vector<OverlayDirEntry> dir_snapshot;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        cb_snapshot = overlays_;
        dir_snapshot = overlay_dirs_;
    }

    // Latest registered callback overlay wins (LIFO precedence).
    for (auto it = cb_snapshot.rbegin(); it != cb_snapshot.rend(); ++it) {
        uint8_t* data = nullptr;
        size_t size = 0;
        if (it->cb(normalized.c_str(), &data, &size, it->user_data) == NM_OK && data != nullptr) {
            if (it->free_cb) {
                std::lock_guard<std::mutex> lock(mutex_);
                owned_buffers_[data] = OwnedBufferRecord{it->free_cb, it->user_data, false};
            }
            *out_data = data;
            *out_size = size;
            return NM_OK;
        }
    }

    // Fallback to native disk overlay directories (LIFO).
    for (auto it = dir_snapshot.rbegin(); it != dir_snapshot.rend(); ++it) {
        size_t sz = 0;
        std::string resolved;
        if (stat_disk_overlay(it->root_dir, normalized, sz, resolved)) {
            std::vector<uint8_t> bytes;
            if (read_disk_file(resolved, bytes)) {
                auto* buf = new uint8_t[std::max<size_t>(1, bytes.size())];
                if (!bytes.empty()) {
                    std::memcpy(buf, bytes.data(), bytes.size());
                }
                {
                    std::lock_guard<std::mutex> lock(mutex_);
                    owned_buffers_[buf] = OwnedBufferRecord{nullptr, nullptr, true};
                }
                *out_data = buf;
                *out_size = bytes.size();
                return NM_OK;
            }
        }
    }

    return NM_ERR_SYMBOL_NOT_FOUND;
}

nm_status_t OverlayAndChannels::free_file_overlay_buffer(uint8_t* data) {
    if (data == nullptr) {
        return NM_OK;
    }
    OwnedBufferRecord rec{nullptr, nullptr, false};
    bool found = false;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        auto it = owned_buffers_.find(data);
        if (it != owned_buffers_.end()) {
            rec = it->second;
            owned_buffers_.erase(it);
            found = true;
        }
    }
    if (!found) {
        return NM_OK;
    }
    if (rec.cpp_delete_array) {
        delete[] data;
    } else if (rec.free_cb) {
        rec.free_cb(data, rec.user_data);
    }
    return NM_OK;
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

bool OverlayAndChannels::has_ruleset_handler(uint32_t ruleset_id) {
    std::lock_guard<std::mutex> lock(mutex_);
    for (const auto& entry : rulesets_) {
        if (entry.ruleset_id == ruleset_id) {
            return true;
        }
    }
    return false;
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
    overlay_dirs_.clear();
    for (auto& kv : owned_buffers_) {
        if (kv.second.cpp_delete_array) {
            delete[] kv.first;
        } else if (kv.second.free_cb) {
            kv.second.free_cb(kv.first, kv.second.user_data);
        }
    }
    owned_buffers_.clear();
    rulesets_.clear();
    chat_handlers_.clear();
    tx_override_ = nullptr;
    tx_override_ud_ = nullptr;
}

} // namespace nativemods
