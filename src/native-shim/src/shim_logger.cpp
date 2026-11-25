#include "shim_logger.hpp"

#include <array>
#include <atomic>
#include <chrono>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <mutex>

#if defined(_WIN32)
#  define WIN32_LEAN_AND_MEAN
#  include <windows.h>
#endif

namespace nativemods {

namespace {

// Self-contained FIPS 180-4 SHA-256 implementation.
class Sha256 {
public:
    Sha256() { reset(); }

    void reset() {
        h_[0] = 0x6a09e667u;
        h_[1] = 0xbb67ae85u;
        h_[2] = 0x3c6ef372u;
        h_[3] = 0xa54ff53au;
        h_[4] = 0x510e527fu;
        h_[5] = 0x9b05688cu;
        h_[6] = 0x1f83d9abu;
        h_[7] = 0x5be0cd19u;
        total_bits_ = 0;
        buf_len_ = 0;
    }

    void update(const uint8_t* data, size_t len) {
        if (!data || len == 0) return;
        total_bits_ += static_cast<uint64_t>(len) * 8ull;
        size_t i = 0;
        if (buf_len_ > 0) {
            while (i < len && buf_len_ < 64) {
                buf_[buf_len_++] = data[i++];
            }
            if (buf_len_ == 64) {
                transform(buf_);
                buf_len_ = 0;
            }
        }
        while (i + 64 <= len) {
            transform(data + i);
            i += 64;
        }
        while (i < len) {
            buf_[buf_len_++] = data[i++];
        }
    }

    std::string finish_hex() {
        buf_[buf_len_++] = 0x80u;
        if (buf_len_ > 56) {
            while (buf_len_ < 64) buf_[buf_len_++] = 0;
            transform(buf_);
            buf_len_ = 0;
        }
        while (buf_len_ < 56) buf_[buf_len_++] = 0;
        for (int i = 7; i >= 0; --i) {
            buf_[buf_len_++] = static_cast<uint8_t>((total_bits_ >> (i * 8)) & 0xFFu);
        }
        transform(buf_);

        static constexpr char kHex[] = "0123456789abcdef";
        std::string out(64, '0');
        for (size_t i = 0; i < 8; ++i) {
            const uint32_t w = h_[i];
            out[i * 8 + 0] = kHex[(w >> 28) & 0xFu];
            out[i * 8 + 1] = kHex[(w >> 24) & 0xFu];
            out[i * 8 + 2] = kHex[(w >> 20) & 0xFu];
            out[i * 8 + 3] = kHex[(w >> 16) & 0xFu];
            out[i * 8 + 4] = kHex[(w >> 12) & 0xFu];
            out[i * 8 + 5] = kHex[(w >>  8) & 0xFu];
            out[i * 8 + 6] = kHex[(w >>  4) & 0xFu];
            out[i * 8 + 7] = kHex[(w >>  0) & 0xFu];
        }
        return out;
    }

private:
    static constexpr uint32_t rotr(uint32_t x, uint32_t n) noexcept {
        return (x >> n) | (x << (32u - n));
    }

    void transform(const uint8_t block[64]) {
        static constexpr uint32_t k[64] = {
            0x428a2f98u, 0x71374491u, 0xb5c0fbcfu, 0xe9b5dba5u, 0x3956c25bu, 0x59f111f1u, 0x923f82a4u, 0xab1c5ed5u,
            0xd807aa98u, 0x12835b01u, 0x243185beu, 0x550c7dc3u, 0x72be5d74u, 0x80deb1feu, 0x9bdc06a7u, 0xc19bf174u,
            0xe49b69c1u, 0xefbe4786u, 0x0fc19dc6u, 0x240ca1ccu, 0x2de92c6fu, 0x4a7484aau, 0x5cb0a9dcu, 0x76f988dau,
            0x983e5152u, 0xa831c66du, 0xb00327c8u, 0xbf597fc7u, 0xc6e00bf3u, 0xd5a79147u, 0x06ca6351u, 0x14292967u,
            0x27b70a85u, 0x2e1b2138u, 0x4d2c6dfcu, 0x53380d13u, 0x650a7354u, 0x766a0abbu, 0x81c2c92eu, 0x92722c85u,
            0xa2bfe8a1u, 0xa81a664bu, 0xc24b8b70u, 0xc76c51a3u, 0xd192e819u, 0xd6990624u, 0xf40e3585u, 0x106aa070u,
            0x19a4c116u, 0x1e376c08u, 0x2748774cu, 0x34b0bcb5u, 0x391c0cb3u, 0x4ed8aa4au, 0x5b9cca4fu, 0x682e6ff3u,
            0x748f82eeu, 0x78a5636fu, 0x84c87814u, 0x8cc70208u, 0x90befffau, 0xa4506cebu, 0xbef9a3f7u, 0xc67178f2u,
        };

        uint32_t w[64];
        for (int i = 0; i < 16; ++i) {
            w[i] = (static_cast<uint32_t>(block[i * 4 + 0]) << 24) |
                   (static_cast<uint32_t>(block[i * 4 + 1]) << 16) |
                   (static_cast<uint32_t>(block[i * 4 + 2]) << 8) |
                   (static_cast<uint32_t>(block[i * 4 + 3]));
        }
        for (int i = 16; i < 64; ++i) {
            const uint32_t s0 = rotr(w[i - 15], 7) ^ rotr(w[i - 15], 18) ^ (w[i - 15] >> 3);
            const uint32_t s1 = rotr(w[i - 2], 17) ^ rotr(w[i - 2], 19) ^ (w[i - 2] >> 10);
            w[i] = w[i - 16] + s0 + w[i - 7] + s1;
        }

        uint32_t a = h_[0], b = h_[1], c = h_[2], d = h_[3];
        uint32_t e = h_[4], f = h_[5], g = h_[6], h = h_[7];

        for (int i = 0; i < 64; ++i) {
            const uint32_t S1 = rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25);
            const uint32_t ch = (e & f) ^ ((~e) & g);
            const uint32_t temp1 = h + S1 + ch + k[i] + w[i];
            const uint32_t S0 = rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22);
            const uint32_t maj = (a & b) ^ (a & c) ^ (b & c);
            const uint32_t temp2 = S0 + maj;

            h = g;
            g = f;
            f = e;
            e = d + temp1;
            d = c;
            c = b;
            b = a;
            a = temp1 + temp2;
        }

        h_[0] += a; h_[1] += b; h_[2] += c; h_[3] += d;
        h_[4] += e; h_[5] += f; h_[6] += g; h_[7] += h;
    }

    uint32_t h_[8]{};
    uint64_t total_bits_ = 0;
    uint8_t  buf_[64]{};
    size_t   buf_len_ = 0;
};

struct LoggerState {
    std::mutex mutex;
#if defined(_WIN32)
    HANDLE     file_handle = INVALID_HANDLE_VALUE;
    LARGE_INTEGER qpc_freq{};
    LARGE_INTEGER qpc_start{};
    std::atomic<bool> start_set{false};
#else
    FILE*      file_ptr = nullptr;
    std::chrono::steady_clock::time_point start_time{};
    std::atomic<bool> start_set{false};
#endif
};

LoggerState& log_state() {
    static LoggerState instance;
    return instance;
}

#if defined(_WIN32)
std::wstring utf8_to_wide_log(const std::string& utf8) {
    if (utf8.empty()) return {};
    const int needed = MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), -1, nullptr, 0);
    if (needed <= 1) return {};
    std::wstring out(static_cast<size_t>(needed - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), -1, out.data(), needed);
    return out;
}
#endif

} // namespace

void logger_mark_attach_time() noexcept {
    auto& st = log_state();
#if defined(_WIN32)
    QueryPerformanceFrequency(&st.qpc_freq);
    QueryPerformanceCounter(&st.qpc_start);
    st.start_set.store(true, std::memory_order_release);
#else
    st.start_time = std::chrono::steady_clock::now();
    st.start_set.store(true, std::memory_order_release);
#endif
}

double logger_elapsed_ms() noexcept {
    auto& st = log_state();
    if (!st.start_set.load(std::memory_order_acquire)) {
        logger_mark_attach_time();
        return 0.0;
    }
#if defined(_WIN32)
    LARGE_INTEGER now{};
    QueryPerformanceCounter(&now);
    if (st.qpc_freq.QuadPart <= 0) return 0.0;
    return static_cast<double>(now.QuadPart - st.qpc_start.QuadPart) * 1000.0 /
           static_cast<double>(st.qpc_freq.QuadPart);
#else
    const auto now = std::chrono::steady_clock::now();
    return std::chrono::duration<double, std::milli>(now - st.start_time).count();
#endif
}

void logger_init(const std::string& default_dir_utf8, bool force_reopen) {
    auto& st = log_state();
    std::lock_guard<std::mutex> lock(st.mutex);

#if defined(_WIN32)
    if (st.file_handle != INVALID_HANDLE_VALUE) {
        if (!force_reopen) return;
        CloseHandle(st.file_handle);
        st.file_handle = INVALID_HANDLE_VALUE;
    }

    std::wstring target_path;
    wchar_t env_buf[1024] = {};
    const DWORD env_len = GetEnvironmentVariableW(L"NATIVEMODS_LOG_PATH", env_buf, 1024);
    if (env_len > 0 && env_len < 1024) {
        target_path.assign(env_buf, env_len);
    } else if (!default_dir_utf8.empty()) {
        target_path = utf8_to_wide_log(default_dir_utf8);
        if (!target_path.empty() && target_path.back() != L'\\' && target_path.back() != L'/') {
            target_path.push_back(L'\\');
        }
        target_path += L"nativemods-shim.log";
    } else {
        target_path = L"nativemods-shim.log";
    }

    st.file_handle = CreateFileW(
        target_path.c_str(),
        FILE_APPEND_DATA,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr,
        OPEN_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
#else
    if (st.file_ptr != nullptr) {
        if (!force_reopen) return;
        std::fclose(st.file_ptr);
        st.file_ptr = nullptr;
    }
    const char* env = std::getenv("NATIVEMODS_LOG_PATH");
    if (env && env[0] != '\0') {
        st.file_ptr = std::fopen(env, "a");
    } else if (!default_dir_utf8.empty()) {
        std::string p = default_dir_utf8;
        if (p.back() != '/') p.push_back('/');
        p += "nativemods-shim.log";
        st.file_ptr = std::fopen(p.c_str(), "a");
    }
#endif
}

void logger_logf(const char* level, const char* fmt, ...) {
    char msg[2048];
    va_list args;
    va_start(args, fmt);
    std::vsnprintf(msg, sizeof(msg), fmt, args);
    va_end(args);

    const double ms = logger_elapsed_ms();
    char line[2200];
    const int n = std::snprintf(line, sizeof(line), "[+%9.3fms] [%s] %s\n", ms, level ? level : "INFO", msg);
    if (n <= 0) return;

    auto& st = log_state();
    std::lock_guard<std::mutex> lock(st.mutex);
#if defined(_WIN32)
    if (st.file_handle != INVALID_HANDLE_VALUE) {
        DWORD written = 0;
        const DWORD len = static_cast<DWORD>((static_cast<size_t>(n) < sizeof(line)) ? static_cast<size_t>(n) : (sizeof(line) - 1));
        WriteFile(st.file_handle, line, len, &written, nullptr);
        FlushFileBuffers(st.file_handle);
    }
#else
    if (st.file_ptr != nullptr) {
        std::fwrite(line, 1, static_cast<size_t>(n), st.file_ptr);
        std::fflush(st.file_ptr);
    }
#endif
}

void logger_close() {
    auto& st = log_state();
    std::lock_guard<std::mutex> lock(st.mutex);
#if defined(_WIN32)
    if (st.file_handle != INVALID_HANDLE_VALUE) {
        FlushFileBuffers(st.file_handle);
        CloseHandle(st.file_handle);
        st.file_handle = INVALID_HANDLE_VALUE;
    }
#else
    if (st.file_ptr != nullptr) {
        std::fflush(st.file_ptr);
        std::fclose(st.file_ptr);
        st.file_ptr = nullptr;
    }
#endif
}

std::string sha256_hex_bytes(const uint8_t* data, size_t len) {
    Sha256 hasher;
    hasher.update(data, len);
    return hasher.finish_hex();
}

bool sha256_hex_file_utf8(const char* path_utf8, std::string& out_hex) {
    if (!path_utf8 || path_utf8[0] == '\0') return false;
#if defined(_WIN32)
    const std::wstring wide = utf8_to_wide_log(path_utf8);
    return sha256_hex_file_wide(wide.c_str(), out_hex);
#else
    FILE* f = std::fopen(path_utf8, "rb");
    if (!f) return false;
    Sha256 hasher;
    uint8_t buf[65536];
    for (;;) {
        const size_t n = std::fread(buf, 1, sizeof(buf), f);
        if (n > 0) hasher.update(buf, n);
        if (n < sizeof(buf)) break;
    }
    const bool err = std::ferror(f) != 0;
    std::fclose(f);
    if (err) return false;
    out_hex = hasher.finish_hex();
    return true;
#endif
}

#if defined(_WIN32)
bool sha256_hex_file_wide(const wchar_t* path_wide, std::string& out_hex) {
    if (!path_wide || path_wide[0] == L'\0') return false;
    HANDLE h = CreateFileW(
        path_wide,
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN,
        nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;

    Sha256 hasher;
    uint8_t buf[65536];
    DWORD read_bytes = 0;
    bool ok = true;
    while (ReadFile(h, buf, sizeof(buf), &read_bytes, nullptr)) {
        if (read_bytes == 0) break;
        hasher.update(buf, read_bytes);
    }
    if (GetLastError() != ERROR_SUCCESS && read_bytes != 0) {
        ok = false;
    }
    CloseHandle(h);
    if (!ok) return false;
    out_hex = hasher.finish_hex();
    return true;
}
#endif

} // namespace nativemods
