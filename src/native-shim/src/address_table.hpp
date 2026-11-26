#pragma once

#include "nativemods_abi.h"

#include <cstddef>
#include <cstdint>
#include <string_view>

namespace nativemods {

struct EmbeddedSymbol {
    const char*        name;
    const char*        category;
    nm_symbol_kind_t   kind;
    nm_symbol_status_t status;
    uint64_t           rva;
    const char*        expected_bytes_hex;
};

struct EmbeddedOffset {
    const char* name;
    size_t      offset;
};

struct BuildDescriptor {
    nm_build_id_t build_id;
    const char*   version;
    const char*   sha256;
    uint32_t      build_number;
};

// Known 3.29.3c / 3.29.3d unpatched executable builds (from LocalExile ClientBuilds.cs).
nm_build_id_t identify_build_by_sha256(std::string_view sha256_hex);
const char*   build_version_string(nm_build_id_t build_id);

// Address table accessors for the active build.
const EmbeddedSymbol* get_symbol_table(nm_build_id_t build_id, uint32_t& out_count);
const EmbeddedSymbol* find_symbol(nm_build_id_t build_id, std::string_view name);
const EmbeddedOffset* get_offset_table(nm_build_id_t build_id, uint32_t& out_count);
const EmbeddedOffset* find_offset(nm_build_id_t build_id, std::string_view name);

// Verifies bytes in memory at `address` against a hex pattern (supports '??' wildcards).
nm_status_t verify_memory_bytes(uint64_t address, const char* expected_bytes_hex);

} // namespace nativemods
