#pragma once

#include <cstddef>
#include <cstdint>
#include <string>

namespace nativemods {

// Records the attach timestamp (safe to call from DllMain under loader lock).
void logger_mark_attach_time() noexcept;

// Elapsed milliseconds since logger_mark_attach_time() (or first log call).
double logger_elapsed_ms() noexcept;

// Initializes the file logger (uses NATIVEMODS_LOG_PATH if set, otherwise <dir_utf8>/nativemods-shim.log).
// Safe to call outside DllMain; idempotent once opened unless force_reopen is true.
void logger_init(const std::string& default_dir_utf8, bool force_reopen = false);

// Appends one timestamped line "[+<ms>ms] [<level>] <msg>\n" and flushes immediately.
void logger_logf(const char* level, const char* fmt, ...);

// Flushes and closes the active log handle.
void logger_close();

// Self-contained FIPS 180-4 SHA-256 helpers (zero external crypto DLL dependencies).
std::string sha256_hex_bytes(const uint8_t* data, size_t len);
bool sha256_hex_file_utf8(const char* path_utf8, std::string& out_hex);
#if defined(_WIN32)
bool sha256_hex_file_wide(const wchar_t* path_wide, std::string& out_hex);
#endif

} // namespace nativemods
