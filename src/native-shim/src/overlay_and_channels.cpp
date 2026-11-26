#include "overlay_and_channels.hpp"
#include "exception_wall.hpp"

#include <algorithm>
#include <cctype>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <string_view>

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

enum class PoeTextFormat {
    None,
    Utf16LeBomCrlf,
    Utf16LeNoBomCrlf,
};

// Matches ClientTextEncoding in FileOverlayTypes.cs:
//   - 12 BOM + CRLF extensions: .ui .ot .otc .it .itc .ao .aoc .act .epk .ddt .pet .trl
//   -  3 no-BOM + CRLF extensions: .sm .mat .amd
PoeTextFormat classify_poe_utf16_text_extension(const std::string& normalized_path) {
    const size_t dot = normalized_path.rfind('.');
    if (dot == std::string::npos) return PoeTextFormat::None;
    const std::string_view ext(normalized_path.data() + dot, normalized_path.size() - dot);
    if (ext == ".ui" || ext == ".ot" || ext == ".otc" || ext == ".it" || ext == ".itc" ||
        ext == ".ao" || ext == ".aoc" || ext == ".act" || ext == ".epk" || ext == ".ddt" ||
        ext == ".pet" || ext == ".trl") {
        return PoeTextFormat::Utf16LeBomCrlf;
    }
    if (ext == ".sm" || ext == ".mat" || ext == ".amd") {
        return PoeTextFormat::Utf16LeNoBomCrlf;
    }
    return PoeTextFormat::None;
}

bool is_poe_utf16_text_extension(const std::string& normalized_path) {
    return classify_poe_utf16_text_extension(normalized_path) != PoeTextFormat::None;
}

// Detects plain UTF-8/ASCII text overlays that need UTF-16LE + CRLF transcoding
// for PoE 3.29's wide parser while leaving existing UTF-16LE (both BOM FF FE and BOM-less
// .sm/.mat/.amd containing NUL bytes) completely untouched.
bool needs_utf8_to_utf16le_transcode(const std::string& normalized_path, const uint8_t* data, size_t size) {
    if (!data || size == 0) return false;
    if (!is_poe_utf16_text_extension(normalized_path)) return false;
    if (size >= 2 && ((data[0] == 0xFFu && data[1] == 0xFEu) || (data[0] == 0xFEu && data[1] == 0xFFu))) {
        return false;
    }
    if (std::memchr(data, 0, size) != nullptr) {
        return false;
    }
    return true;
}

void transcode_utf8_to_utf16le_crlf(
    const uint8_t*        data,
    size_t                size,
    bool                  include_bom,
    std::vector<uint8_t>& out) {
    out.clear();
    size_t i = 0;
    if (size >= 3 && data[0] == 0xEFu && data[1] == 0xBBu && data[2] == 0xBFu) {
        i = 3;
    }

    out.reserve((include_bom ? 2u : 0u) + (size - i) * 2 + 16);
    if (include_bom) {
        out.push_back(0xFFu);
        out.push_back(0xFEu);
    }

    auto push_u16 = [&out](uint16_t cu) {
        out.push_back(static_cast<uint8_t>(cu & 0xFFu));
        out.push_back(static_cast<uint8_t>((cu >> 8) & 0xFFu));
    };

    while (i < size) {
        uint32_t cp = 0;
        const uint8_t b0 = data[i];
        if (b0 < 0x80u) {
            cp = b0;
            ++i;
        } else if ((b0 & 0xE0u) == 0xC0u && i + 1 < size && (data[i + 1] & 0xC0u) == 0x80u) {
            cp = ((static_cast<uint32_t>(b0 & 0x1Fu)) << 6) | (data[i + 1] & 0x3Fu);
            i += 2;
        } else if ((b0 & 0xF0u) == 0xE0u && i + 2 < size &&
                   (data[i + 1] & 0xC0u) == 0x80u && (data[i + 2] & 0xC0u) == 0x80u) {
            cp = ((static_cast<uint32_t>(b0 & 0x0Fu)) << 12) |
                 ((static_cast<uint32_t>(data[i + 1] & 0x3Fu)) << 6) |
                 (data[i + 2] & 0x3Fu);
            i += 3;
        } else if ((b0 & 0xF8u) == 0xF0u && i + 3 < size &&
                   (data[i + 1] & 0xC0u) == 0x80u && (data[i + 2] & 0xC0u) == 0x80u &&
                   (data[i + 3] & 0xC0u) == 0x80u) {
            cp = ((static_cast<uint32_t>(b0 & 0x07u)) << 18) |
                 ((static_cast<uint32_t>(data[i + 1] & 0x3Fu)) << 12) |
                 ((static_cast<uint32_t>(data[i + 2] & 0x3Fu)) << 6) |
                 (data[i + 3] & 0x3Fu);
            i += 4;
        } else {
            cp = 0xFFFDu;
            ++i;
        }

        if (cp == '\r') {
            if (i < size && data[i] == '\n') {
                ++i;
            }
            push_u16('\r');
            push_u16('\n');
        } else if (cp == '\n') {
            push_u16('\r');
            push_u16('\n');
        } else if (cp <= 0xFFFFu) {
            push_u16(static_cast<uint16_t>(cp));
        } else if (cp <= 0x10FFFFu) {
            cp -= 0x10000u;
            push_u16(static_cast<uint16_t>(0xD800u + (cp >> 10)));
            push_u16(static_cast<uint16_t>(0xDC00u + (cp & 0x3FFu)));
        } else {
            push_u16(0xFFFDu);
        }
    }
}

void maybe_transcode_poe_text_inplace(const std::string& normalized_path, std::vector<uint8_t>& bytes) {
    if (!needs_utf8_to_utf16le_transcode(normalized_path, bytes.data(), bytes.size())) {
        return;
    }
    const bool include_bom =
        classify_poe_utf16_text_extension(normalized_path) == PoeTextFormat::Utf16LeBomCrlf;
    std::vector<uint8_t> transcoded;
    transcode_utf8_to_utf16le_crlf(bytes.data(), bytes.size(), include_bom, transcoded);
    bytes = std::move(transcoded);
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

std::string wide_to_utf8_local(const wchar_t* ptr, size_t len) {
    if (!ptr || len == 0) return {};
    const int needed = WideCharToMultiByte(CP_UTF8, 0, ptr, static_cast<int>(len), nullptr, 0, nullptr, nullptr);
    if (needed <= 0) return {};
    std::string out(static_cast<size_t>(needed), '\0');
    WideCharToMultiByte(CP_UTF8, 0, ptr, static_cast<int>(len), out.data(), needed, nullptr, nullptr);
    return out;
}

void walk_dir_win32(
    const std::string&                                            disk_dir,
    const std::string&                                            rel_prefix,
    std::unordered_map<std::string, OverlayIndex::DiskFileEntry>& out_files) {
    std::string search = disk_dir;
    if (!search.empty() && search.back() != '\\' && search.back() != '/') {
        search.push_back('\\');
    }
    const std::string base_dir = search;
    search.push_back('*');

    WIN32_FIND_DATAW fd{};
    HANDLE h = FindFirstFileW(utf8_to_wide_local(search).c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) {
        return;
    }

    do {
        if (std::wcscmp(fd.cFileName, L".") == 0 || std::wcscmp(fd.cFileName, L"..") == 0) {
            continue;
        }
        const std::string name_u8 = wide_to_utf8_local(fd.cFileName, std::wcslen(fd.cFileName));
        if (name_u8.empty()) {
            continue;
        }
        const std::string child_disk = base_dir + name_u8;
        const std::string child_rel = rel_prefix.empty() ? name_u8 : (rel_prefix + "/" + name_u8);

        if ((fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) {
            if ((fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) == 0) {
                walk_dir_win32(child_disk, child_rel, out_files);
            }
        } else {
            const std::string norm = OverlayAndChannels::normalize_virtual_path(child_rel.c_str());
            if (!is_safe_relative_virtual_path(norm)) {
                continue;
            }
            const uint64_t raw_sz = (static_cast<uint64_t>(fd.nFileSizeHigh) << 32) |
                                    static_cast<uint64_t>(fd.nFileSizeLow);
            OverlayIndex::DiskFileEntry entry{};
            entry.resolved_path = child_disk;
            entry.raw_size = static_cast<size_t>(raw_sz);
            entry.served_size = entry.raw_size;
            entry.is_utf16_text = is_poe_utf16_text_extension(norm);
            out_files[norm] = std::move(entry);
        }
    } while (FindNextFileW(h, &fd));

    FindClose(h);
}
#endif

} // namespace

OverlayAndChannels::ScopedOverlayIndex::~ScopedOverlayIndex() {
    if (owner_) {
        owner_->active_readers_.fetch_sub(1, std::memory_order_seq_cst);
        if (owner_->retired_count_.load(std::memory_order_acquire) > 0) {
            owner_->try_reclaim_retired();
        }
    }
}

OverlayAndChannels::ScopedOverlayIndex OverlayAndChannels::acquire_index() const noexcept {
    active_readers_.fetch_add(1, std::memory_order_seq_cst);
    const OverlayIndex* idx = active_index_.load(std::memory_order_seq_cst);
    return ScopedOverlayIndex(this, idx);
}

size_t OverlayAndChannels::retired_index_count() const noexcept {
    std::lock_guard<std::mutex> lock(mutex_);
    return retired_indices_.size();
}

void OverlayAndChannels::try_reclaim_retired() const {
    std::unique_lock<std::mutex> lock(mutex_, std::try_to_lock);
    if (!lock.owns_lock()) {
        return;
    }
    if (active_readers_.load(std::memory_order_seq_cst) == 0 && !retired_indices_.empty()) {
        for (OverlayIndex* old : retired_indices_) {
            delete old;
        }
        retired_indices_.clear();
        retired_count_.store(0, std::memory_order_release);
    }
}

OverlayAndChannels::~OverlayAndChannels() {
    const OverlayIndex* cur = active_index_.exchange(nullptr, std::memory_order_seq_cst);
    delete cur;
    for (OverlayIndex* old : retired_indices_) {
        delete old;
    }
    retired_indices_.clear();
    retired_count_.store(0, std::memory_order_release);
}

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

uint64_t OverlayAndChannels::hash_virtual_path_ci_utf8(const char* ptr, size_t len) noexcept {
    if (!ptr || len == 0) return 0;
    uint64_t h = 14695981039346656037ULL;
    bool started = false;
    for (size_t i = 0; i < len; ++i) {
        uint8_t c = static_cast<uint8_t>(ptr[i]);
        if (c == '\\') {
            c = '/';
        } else if (c >= 'A' && c <= 'Z') {
            c = static_cast<uint8_t>(c + ('a' - 'A'));
        }
        if (!started) {
            if (c == '/') continue;
            started = true;
        }
        h ^= c;
        h *= 1099511628211ULL;
    }
    if (!started) return 0;
    return (h == 0) ? 1ULL : h;
}

uint64_t OverlayAndChannels::hash_virtual_path_ci_wide(const wchar_t* ptr, size_t len) noexcept {
    if (!ptr || len == 0) return 0;
    uint64_t h = 14695981039346656037ULL;
    bool started = false;
    for (size_t i = 0; i < len; ++i) {
        uint32_t ch = static_cast<uint32_t>(ptr[i]);
        if (ch == L'\\') {
            ch = L'/';
        } else if (ch >= L'A' && ch <= L'Z') {
            ch += static_cast<uint32_t>(L'a' - L'A');
        }
        if (!started) {
            if (ch == L'/') continue;
            started = true;
        }
        if (ch < 0x80u) {
            h ^= static_cast<uint8_t>(ch);
            h *= 1099511628211ULL;
        } else if (ch < 0x800u) {
            h ^= static_cast<uint8_t>(0xC0u | (ch >> 6));
            h *= 1099511628211ULL;
            h ^= static_cast<uint8_t>(0x80u | (ch & 0x3Fu));
            h *= 1099511628211ULL;
        } else {
            h ^= static_cast<uint8_t>(0xE0u | (ch >> 12));
            h *= 1099511628211ULL;
            h ^= static_cast<uint8_t>(0x80u | ((ch >> 6) & 0x3Fu));
            h *= 1099511628211ULL;
            h ^= static_cast<uint8_t>(0x80u | (ch & 0x3Fu));
            h *= 1099511628211ULL;
        }
    }
    if (!started) return 0;
    return (h == 0) ? 1ULL : h;
}

void OverlayAndChannels::index_disk_directory(
    const std::string&                                            root_dir,
    std::unordered_map<std::string, OverlayIndex::DiskFileEntry>& out_files) {
    if (root_dir.empty()) {
        return;
    }

#if defined(_WIN32)
    walk_dir_win32(root_dir, std::string{}, out_files);
#else
    namespace fs = std::filesystem;
    std::error_code ec;
    fs::path root(root_dir);
    if (!fs::exists(root, ec) || !fs::is_directory(root, ec)) {
        return;
    }
    for (fs::recursive_directory_iterator it(root, fs::directory_options::skip_permission_denied, ec), end;
         it != end && !ec; it.increment(ec)) {
        if (!it->is_regular_file(ec)) {
            continue;
        }
        const fs::path rel = fs::relative(it->path(), root, ec);
        if (ec) {
            continue;
        }
        const std::string norm = normalize_virtual_path(rel.generic_string().c_str());
        if (!is_safe_relative_virtual_path(norm)) {
            continue;
        }
        const auto raw_sz = it->file_size(ec);
        if (ec) {
            continue;
        }
        OverlayIndex::DiskFileEntry entry{};
        entry.resolved_path = it->path().string();
        entry.raw_size = static_cast<size_t>(raw_sz);
        entry.served_size = entry.raw_size;
        entry.is_utf16_text = is_poe_utf16_text_extension(norm);
        out_files[norm] = std::move(entry);
    }
#endif

    // Precompute exact served size for PoE UTF-16LE text overlays (.ui/.ot/.ao/.act/.amd/.sm)
    // once at index time so live has_file_overlay / size_query hits never touch disk!
    for (auto& kv : out_files) {
        if (kv.second.is_utf16_text && kv.second.raw_size > 0 && kv.second.raw_size <= 4u * 1024u * 1024u) {
            std::vector<uint8_t> bytes;
            if (read_disk_file(kv.second.resolved_path, bytes)) {
                maybe_transcode_poe_text_inplace(kv.first, bytes);
                kv.second.served_size = bytes.size();
            }
        }
    }
}

void OverlayAndChannels::rebuild_index_locked() {
    auto* next = new OverlayIndex();
    next->generation = ++index_generation_;

    // Walk overlay_dirs_ in registration order so later directories overwrite earlier ones (exact LIFO precedence!).
    for (const auto& dir : overlay_dirs_) {
        index_disk_directory(dir.root_dir, next->disk_files);
    }

    std::vector<uint64_t> all_hashes;
    all_hashes.reserve(next->disk_files.size() + 32);
    for (const auto& kv : next->disk_files) {
        const uint64_t h = hash_virtual_path_ci_utf8(kv.first.data(), kv.first.size());
        if (h != 0) {
            all_hashes.push_back(h);
        }
    }

    bool unfiltered = false;
    for (const auto& cb : overlays_) {
        if (!cb.has_path_filter) {
            unfiltered = true;
        } else {
            for (const auto& path : cb.known_paths) {
                const uint64_t h = hash_virtual_path_ci_utf8(path.data(), path.size());
                if (h != 0) {
                    all_hashes.push_back(h);
                }
            }
        }
    }

    next->has_unfiltered_callbacks = unfiltered;
    if (!all_hashes.empty()) {
        size_t cap = 64;
        while (cap < all_hashes.size() * 4u) {
            cap <<= 1u;
        }
        next->ci_hash_table.assign(cap, 0ull);
        next->ci_hash_mask = static_cast<uint64_t>(cap - 1u);
        for (uint64_t h : all_hashes) {
            size_t slot = static_cast<size_t>(h & next->ci_hash_mask);
            while (next->ci_hash_table[slot] != 0 && next->ci_hash_table[slot] != h) {
                slot = (slot + 1u) & static_cast<size_t>(next->ci_hash_mask);
            }
            next->ci_hash_table[slot] = h;
        }
    }

    next->empty = !next->has_unfiltered_callbacks && next->ci_hash_table.empty();

    const OverlayIndex* prev = active_index_.exchange(next, std::memory_order_seq_cst);
    if (prev != nullptr) {
        retired_indices_.push_back(const_cast<OverlayIndex*>(prev));
    }
    if (active_readers_.load(std::memory_order_seq_cst) == 0) {
        for (OverlayIndex* old : retired_indices_) {
            delete old;
        }
        retired_indices_.clear();
        retired_count_.store(0, std::memory_order_release);
    } else {
        retired_count_.store(static_cast<uint32_t>(retired_indices_.size()), std::memory_order_release);
    }
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
    OverlayEntry entry{};
    entry.token = token;
    entry.cb = cb;
    entry.free_cb = free_cb;
    entry.user_data = user_data;
    entry.has_path_filter = false;
    overlays_.push_back(std::move(entry));
    rebuild_index_locked();
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
    rebuild_index_locked();
    return NM_OK;
}

nm_status_t OverlayAndChannels::set_file_overlay_known_paths(
    uint64_t           token,
    const char* const* paths_utf8,
    size_t             path_count) {
    if (path_count > 0 && paths_utf8 == nullptr) {
        return NM_ERR_INVALID_ARG;
    }
    std::lock_guard<std::mutex> lock(mutex_);
    for (auto& entry : overlays_) {
        if (entry.token == token) {
            entry.has_path_filter = true;
            entry.known_paths.clear();
            entry.known_paths.reserve(path_count);
            for (size_t i = 0; i < path_count; ++i) {
                if (paths_utf8[i] != nullptr && paths_utf8[i][0] != '\0') {
                    const std::string norm = normalize_virtual_path(paths_utf8[i]);
                    if (!norm.empty()) {
                        entry.known_paths.insert(norm);
                    }
                }
            }
            rebuild_index_locked();
            return NM_OK;
        }
    }
    return NM_ERR_INVALID_ARG;
}

nm_status_t OverlayAndChannels::register_file_overlay_dir(const char* root_dir_utf8, uint64_t* out_token) {
    if (root_dir_utf8 == nullptr || root_dir_utf8[0] == '\0' || out_token == nullptr) {
        return NM_ERR_INVALID_ARG;
    }
    std::lock_guard<std::mutex> lock(mutex_);
    const uint64_t token = next_token_++;
    overlay_dirs_.push_back(OverlayDirEntry{token, std::string(root_dir_utf8)});
    rebuild_index_locked();
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
    rebuild_index_locked();
    return NM_OK;
}

nm_status_t OverlayAndChannels::has_file_overlay(const char* virtual_path_utf8, size_t* out_size) {
    if (virtual_path_utf8 == nullptr) {
        return NM_ERR_INVALID_ARG;
    }
    if (out_size) {
        *out_size = 0;
    }

    ScopedOverlayIndex idx_guard = acquire_index();
    const OverlayIndex* idx = idx_guard.get();
    if (!idx || idx->empty) {
        return NM_ERR_SYMBOL_NOT_FOUND;
    }

    if (!idx->has_unfiltered_callbacks) {
        const uint64_t h = hash_virtual_path_ci_utf8(virtual_path_utf8, std::strlen(virtual_path_utf8));
        if (!idx->might_contain_ci_hash(h)) {
            return NM_ERR_SYMBOL_NOT_FOUND;
        }
    }

    const std::string normalized = normalize_virtual_path(virtual_path_utf8);
    if (normalized.empty() || !is_safe_relative_virtual_path(normalized)) {
        return NM_ERR_SYMBOL_NOT_FOUND;
    }

    // Snapshot matching managed / dynamic callbacks only (never copy disk dirs or run stat()).
    struct MatchingCallback {
        nm_file_overlay_callback_t cb;
        nm_file_overlay_free_t     free_cb;
        void*                      user_data;
    };
    std::vector<MatchingCallback> matched_cbs;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        for (auto it = overlays_.rbegin(); it != overlays_.rend(); ++it) {
            if (!it->has_path_filter || it->known_paths.find(normalized) != it->known_paths.end()) {
                matched_cbs.push_back(MatchingCallback{it->cb, it->free_cb, it->user_data});
            }
        }
    }

    // 1. Managed / dynamic callbacks (LIFO).
    for (const auto& cb : matched_cbs) {
        uint8_t* data = nullptr;
        size_t size = 0;
        if (cb.cb(normalized.c_str(), &data, &size, cb.user_data) == NM_OK && data != nullptr) {
            size_t final_size = size;
            if (out_size && needs_utf8_to_utf16le_transcode(normalized, data, size)) {
                const bool include_bom =
                    classify_poe_utf16_text_extension(normalized) == PoeTextFormat::Utf16LeBomCrlf;
                std::vector<uint8_t> transcoded;
                transcode_utf8_to_utf16le_crlf(data, size, include_bom, transcoded);
                final_size = transcoded.size();
            }
            if (cb.free_cb) {
                cb.free_cb(data, cb.user_data);
            }
            if (out_size) {
                *out_size = final_size;
            }
            return NM_OK;
        }
    }

    // 2. Pre-indexed native disk overlay directories (O(1) map lookup, ZERO disk syscalls!).
    const auto disk_it = idx->disk_files.find(normalized);
    if (disk_it != idx->disk_files.end()) {
        if (out_size) {
            *out_size = disk_it->second.served_size;
        }
        return NM_OK;
    }

    return NM_ERR_SYMBOL_NOT_FOUND;
}

bool OverlayAndChannels::resolve_file_overlay_owned(const char* virtual_path_utf8, std::vector<uint8_t>& out_bytes) {
    out_bytes.clear();
    if (virtual_path_utf8 == nullptr) {
        return false;
    }

    ScopedOverlayIndex idx_guard = acquire_index();
    const OverlayIndex* idx = idx_guard.get();
    if (!idx || idx->empty) {
        return false;
    }

    if (!idx->has_unfiltered_callbacks) {
        const uint64_t h = hash_virtual_path_ci_utf8(virtual_path_utf8, std::strlen(virtual_path_utf8));
        if (!idx->might_contain_ci_hash(h)) {
            return false;
        }
    }

    const std::string normalized = normalize_virtual_path(virtual_path_utf8);
    if (normalized.empty() || !is_safe_relative_virtual_path(normalized)) {
        return false;
    }

    struct MatchingCallback {
        nm_file_overlay_callback_t cb;
        nm_file_overlay_free_t     free_cb;
        void*                      user_data;
    };
    std::vector<MatchingCallback> matched_cbs;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        for (auto it = overlays_.rbegin(); it != overlays_.rend(); ++it) {
            if (!it->has_path_filter || it->known_paths.find(normalized) != it->known_paths.end()) {
                matched_cbs.push_back(MatchingCallback{it->cb, it->free_cb, it->user_data});
            }
        }
    }

    // 1. Managed / dynamic callbacks take highest precedence (LIFO).
    for (const auto& cb : matched_cbs) {
        uint8_t* data = nullptr;
        size_t size = 0;
        if (cb.cb(normalized.c_str(), &data, &size, cb.user_data) == NM_OK && data != nullptr) {
            out_bytes.assign(data, data + size);
            if (cb.free_cb) {
                cb.free_cb(data, cb.user_data);
            }
            maybe_transcode_poe_text_inplace(normalized, out_bytes);
            return true;
        }
    }

    // 2. Pre-indexed native disk overlay directories (LIFO precedence already resolved in index).
    const auto disk_it = idx->disk_files.find(normalized);
    if (disk_it != idx->disk_files.end()) {
        if (read_disk_file(disk_it->second.resolved_path, out_bytes)) {
            maybe_transcode_poe_text_inplace(normalized, out_bytes);
            return true;
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

    ScopedOverlayIndex idx_guard = acquire_index();
    const OverlayIndex* idx = idx_guard.get();
    if (!idx || idx->empty) {
        return NM_ERR_SYMBOL_NOT_FOUND;
    }

    if (!idx->has_unfiltered_callbacks) {
        const uint64_t h = hash_virtual_path_ci_utf8(virtual_path_utf8, std::strlen(virtual_path_utf8));
        if (!idx->might_contain_ci_hash(h)) {
            return NM_ERR_SYMBOL_NOT_FOUND;
        }
    }

    const std::string normalized = normalize_virtual_path(virtual_path_utf8);
    if (normalized.empty() || !is_safe_relative_virtual_path(normalized)) {
        return NM_ERR_SYMBOL_NOT_FOUND;
    }

    struct MatchingCallback {
        nm_file_overlay_callback_t cb;
        nm_file_overlay_free_t     free_cb;
        void*                      user_data;
    };
    std::vector<MatchingCallback> matched_cbs;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        for (auto it = overlays_.rbegin(); it != overlays_.rend(); ++it) {
            if (!it->has_path_filter || it->known_paths.find(normalized) != it->known_paths.end()) {
                matched_cbs.push_back(MatchingCallback{it->cb, it->free_cb, it->user_data});
            }
        }
    }

    // Latest registered callback overlay wins (LIFO precedence).
    for (const auto& cb : matched_cbs) {
        uint8_t* data = nullptr;
        size_t size = 0;
        if (cb.cb(normalized.c_str(), &data, &size, cb.user_data) == NM_OK && data != nullptr) {
            if (needs_utf8_to_utf16le_transcode(normalized, data, size)) {
                const bool include_bom =
                    classify_poe_utf16_text_extension(normalized) == PoeTextFormat::Utf16LeBomCrlf;
                std::vector<uint8_t> transcoded;
                transcode_utf8_to_utf16le_crlf(data, size, include_bom, transcoded);
                if (cb.free_cb) {
                    cb.free_cb(data, cb.user_data);
                }
                auto* buf = new uint8_t[std::max<size_t>(1, transcoded.size())];
                if (!transcoded.empty()) {
                    std::memcpy(buf, transcoded.data(), transcoded.size());
                }
                {
                    std::lock_guard<std::mutex> lock(mutex_);
                    owned_buffers_[buf] = OwnedBufferRecord{nullptr, nullptr, true};
                }
                *out_data = buf;
                *out_size = transcoded.size();
                return NM_OK;
            }

            if (cb.free_cb) {
                std::lock_guard<std::mutex> lock(mutex_);
                owned_buffers_[data] = OwnedBufferRecord{cb.free_cb, cb.user_data, false};
            }
            *out_data = data;
            *out_size = size;
            return NM_OK;
        }
    }

    // Fallback to pre-indexed native disk overlay directories (LIFO).
    const auto disk_it = idx->disk_files.find(normalized);
    if (disk_it != idx->disk_files.end()) {
        std::vector<uint8_t> bytes;
        if (read_disk_file(disk_it->second.resolved_path, bytes)) {
            maybe_transcode_poe_text_inplace(normalized, bytes);
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

uint8_t* OverlayAndChannels::allocate_owned_buffer_copy(const uint8_t* data, size_t size) {
    auto* buf = new uint8_t[std::max<size_t>(1, size)];
    if (size > 0 && data != nullptr) {
        std::memcpy(buf, data, size);
    }
    std::lock_guard<std::mutex> lock(mutex_);
    owned_buffers_[buf] = OwnedBufferRecord{nullptr, nullptr, true};
    return buf;
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

bool OverlayAndChannels::has_chat_handlers() {
    std::lock_guard<std::mutex> lock(mutex_);
    return !chat_handlers_.empty();
}

int32_t OverlayAndChannels::dispatch_chat_rx(const char* sender_utf8, const char* text_utf8) {
    if (text_utf8 == nullptr) {
        return 0;
    }
    constexpr size_t kPrefixLen = 5; // "##NM "
    const bool is_nm_prefixed = (std::strncmp(text_utf8, NM_CHAT_FALLBACK_PREFIX, kPrefixLen) == 0);

    std::vector<ChatEntry> snapshot;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        snapshot = chat_handlers_;
    }

    if (snapshot.empty()) {
        return is_nm_prefixed ? 1 : 0;
    }

    struct ChatCtx {
        nm_chat_rx_callback_t cb;
        const char*           sender;
        const char*           text;
        void*                 ud;
    };
    auto trampoline = [](void* raw) -> int64_t {
        auto* c = static_cast<ChatCtx*>(raw);
        return static_cast<int64_t>(c->cb(c->sender ? c->sender : "", c->text, c->ud));
    };

    bool suppress = false;
    for (const auto& entry : snapshot) {
        ChatCtx ctx{entry.cb, sender_utf8, text_utf8, entry.user_data};
        int64_t rc = 0;
        nm_exception_info_t ex{};
        if (invoke_guarded(trampoline, &ctx, &rc, &ex) == NM_OK && rc != 0) {
            suppress = true;
        }
    }
    return suppress ? 1 : 0;
}

nm_status_t OverlayAndChannels::register_opcode_handler_group(
    uint16_t            min_opcode,
    uint16_t            max_opcode,
    nm_opcode_handler_t cb,
    void*               user_data,
    uint64_t*           out_token) {
    if (cb == nullptr || out_token == nullptr || min_opcode > max_opcode) {
        return NM_ERR_INVALID_ARG;
    }
    std::lock_guard<std::mutex> lock(mutex_);
    const uint64_t token = next_token_++;
    opcode_groups_.push_back(OpcodeGroupEntry{token, min_opcode, max_opcode, cb, user_data});
    *out_token = token;
    return NM_OK;
}

nm_status_t OverlayAndChannels::unregister_opcode_handler_group(uint64_t token) {
    std::lock_guard<std::mutex> lock(mutex_);
    auto it = std::remove_if(opcode_groups_.begin(), opcode_groups_.end(),
                             [token](const OpcodeGroupEntry& e) { return e.token == token; });
    if (it == opcode_groups_.end()) {
        return NM_ERR_INVALID_ARG;
    }
    opcode_groups_.erase(it, opcode_groups_.end());
    return NM_OK;
}

bool OverlayAndChannels::has_opcode_handlers() {
    std::lock_guard<std::mutex> lock(mutex_);
    return !opcode_groups_.empty();
}

nm_status_t OverlayAndChannels::dispatch_opcode_rx(
    uint16_t       opcode,
    void*          stream_handle,
    const uint8_t* payload,
    uint32_t       length,
    int32_t*       out_handled) {
    if (out_handled) {
        *out_handled = 0;
    }
    if (length > 0 && payload == nullptr && stream_handle == nullptr) {
        return NM_ERR_INVALID_ARG;
    }

    std::vector<OpcodeGroupEntry> matched;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        for (const auto& g : opcode_groups_) {
            if (opcode >= g.min_opcode && opcode <= g.max_opcode) {
                matched.push_back(g);
            }
        }
    }

    if (matched.empty()) {
        return NM_OK;
    }

    SyntheticPacketStream synth_stream{};
    void* effective_stream = stream_handle;
    if (effective_stream == nullptr) {
        synth_stream.data = payload;
        synth_stream.size = length;
        synth_stream.cursor = 0;
        effective_stream = &synth_stream;
    }

    struct OpcodeCtx {
        nm_opcode_handler_t cb;
        uint16_t            opcode;
        void*               stream;
        const uint8_t*      payload;
        uint32_t            length;
        void*               ud;
    };
    auto trampoline = [](void* raw) -> int64_t {
        auto* c = static_cast<OpcodeCtx*>(raw);
        return static_cast<int64_t>(c->cb(c->opcode, c->stream, c->payload, c->length, c->ud));
    };

    bool handled = false;
    for (const auto& entry : matched) {
        OpcodeCtx ctx{entry.cb, opcode, effective_stream, payload, length, entry.user_data};
        int64_t rc = 0;
        nm_exception_info_t ex{};
        if (invoke_guarded(trampoline, &ctx, &rc, &ex) == NM_OK && rc != 0) {
            handled = true;
        }
    }
    if (out_handled) {
        *out_handled = handled ? 1 : 0;
    }
    return NM_OK;
}

nm_status_t OverlayAndChannels::try_send_raw_packet_override(
    uint16_t       opcode,
    const uint8_t* payload,
    uint32_t       length) {
    nm_packet_tx_override_t tx = nullptr;
    void* ud = nullptr;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        tx = tx_override_;
        ud = tx_override_ud_;
    }
    if (tx) {
        return tx(opcode, 0, payload, length, ud);
    }
    return NM_ERR_NOT_SUPPORTED_ON_PLATFORM;
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
    const OverlayIndex* cur = active_index_.exchange(nullptr, std::memory_order_seq_cst);
    delete cur;
    for (OverlayIndex* old : retired_indices_) {
        delete old;
    }
    retired_indices_.clear();
    retired_count_.store(0, std::memory_order_release);
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
    opcode_groups_.clear();
    tx_override_ = nullptr;
    tx_override_ud_ = nullptr;
}

} // namespace nativemods
