#include "address_table.hpp"

#include <algorithm>
#include <array>
#include <cctype>
#include <cstring>

namespace nativemods {

namespace {

constexpr std::array<BuildDescriptor, 3> kKnownBuilds = {{
    {NM_BUILD_3_29_3C_327984, "3.29.3c", "cc8aa38345be2a837575215d2ce142ebecf7b007345843265b335ebb8ac5964c", 327984},
    {NM_BUILD_3_29_3C_328126, "3.29.3c", "01a22facf36886b39bca74793c0d8c7295b1495f1625b4ebc7b23c2de32a70cf", 328126},
    {NM_BUILD_3_29_3D_328228, "3.29.3d", "e9ba3d31c4d922e1826ef01d02fd22178a001b29e17264b68fc2446efd41f53d", 328228},
}};

// Core verified 3.29.3d symbols (1:1 verbatim with data/addresses/3.29.3d.json and Game.exe).
constexpr std::array<EmbeddedSymbol, 52> kSymbols3293d = {{
    // Frame
    {"frame.state_stack_pass_a",        "frame",      NM_SYM_CODE,        NM_SYM_STATUS_STATIC_VERIFIED, 0x1528f40ull, "48895c241048896c"},
    {"frame.state_stack_pass_a_hook",   "frame",      NM_SYM_CODE,        NM_SYM_STATUS_LIVE_VERIFIED,   0x1528fbfull, "488b5d284883eb10"},
    {"frame.ingame_tick",               "frame",      NM_SYM_CODE,        NM_SYM_STATUS_STATIC_VERIFIED, 0x0d54a00ull, "40534883ec400fb6"},
    {"frame.instance_tick",             "frame",      NM_SYM_CODE,        NM_SYM_STATUS_STATIC_VERIFIED, 0x214f1d0ull, "488bc44889581848"},
    // UI
    {"ui.control_load",                 "ui",         NM_SYM_CODE,        NM_SYM_STATUS_LIVE_VERIFIED,   0x18b08d0ull, "48895c240848897c"},
    {"ui.control_pool",                 "ui",         NM_SYM_CODE,        NM_SYM_STATUS_LIVE_VERIFIED,   0x18b6d20ull, "48895c2410488974"},
    {"ui.frame_create",                 "ui",         NM_SYM_CODE,        NM_SYM_STATUS_LIVE_VERIFIED,   0x014b0e0ull, "40534883ec20488b"},
    {"ui.add_child",                    "ui",         NM_SYM_CODE,        NM_SYM_STATUS_LIVE_VERIFIED,   0x18b5b10ull, "4c89442418535748"},
    {"ui.find_child",                   "ui",         NM_SYM_CODE,        NM_SYM_STATUS_LIVE_VERIFIED,   0x18b6420ull, "48896c2418488974"},
    {"ui.set_visible",                  "ui",         NM_SYM_CODE,        NM_SYM_STATUS_LIVE_VERIFIED,   0x18b0a30ull, "40534883ec20488b"},
    {"ui.event_list_get",               "ui",         NM_SYM_CODE,        NM_SYM_STATUS_LIVE_VERIFIED,   0x019b2b0ull, "48895c2418488974"},
    {"ui.event_list_grow",              "ui",         NM_SYM_CODE,        NM_SYM_STATUS_LIVE_VERIFIED,   0x0152430ull, "4056574883ec4848"},
    {"ui.vtable_frame",                 "ui",         NM_SYM_VTABLE,      NM_SYM_STATUS_LIVE_VERIFIED,   0x3033598ull, nullptr},
    {"ui.vtable_label",                 "ui",         NM_SYM_VTABLE,      NM_SYM_STATUS_LIVE_VERIFIED,   0x34ae300ull, nullptr},
    {"ui.vtable_button",                "ui",         NM_SYM_VTABLE,      NM_SYM_STATUS_LIVE_VERIFIED,   0x34b0e88ull, nullptr},
    {"ui.vtable_textbox",               "ui",         NM_SYM_VTABLE,      NM_SYM_STATUS_LIVE_VERIFIED,   0x30321a0ull, nullptr},
    {"ui.vtable_checkbox",              "ui",         NM_SYM_VTABLE,      NM_SYM_STATUS_LIVE_VERIFIED,   0x34afe98ull, nullptr},
    // Files
    {"files.read_whole_file",           "files",      NM_SYM_CODE,        NM_SYM_STATUS_STATIC_VERIFIED, 0x0e564d0ull, "48895c2418488954"},
    {"files.text_stream_open",          "files",      NM_SYM_CODE,        NM_SYM_STATUS_LIVE_VERIFIED,   0x21d5b50ull, "48895c241048896c"},
    {"files.bundle_index_lookup",       "files",      NM_SYM_CODE,        NM_SYM_STATUS_STATIC_VERIFIED, 0x0e4c430ull, "48895c241048896c"},
    {"files.oodle_decompress",          "files",      NM_SYM_CODE,        NM_SYM_STATUS_STATIC_VERIFIED, 0x2c9d520ull, "4055535657415441"},
    {"files.variant_tag_wstring",       "files",      NM_SYM_DATA,        NM_SYM_STATUS_STATIC_VERIFIED, 0x44a6930ull, nullptr},
    {"files.load_trace_before",         "files",      NM_SYM_DATA,        NM_SYM_STATUS_STATIC_VERIFIED, 0x4599980ull, nullptr},
    {"files.load_trace_after",          "files",      NM_SYM_DATA,        NM_SYM_STATUS_STATIC_VERIFIED, 0x4599988ull, nullptr},
    {"files.hot_reload_invalidate_ext", "files",      NM_SYM_CODE,        NM_SYM_STATUS_STATIC_VERIFIED, 0x1010560ull, "488bc44889581048"},
    // GEAL
    {"geal.compiler_statements",        "geal",       NM_SYM_CODE,        NM_SYM_STATUS_STATIC_VERIFIED, 0x1df8210ull, "48895c240848897c"},
    {"geal.interpreter",                "geal",       NM_SYM_CODE,        NM_SYM_STATUS_STATIC_VERIFIED, 0x1df0d70ull, "488bc44c8948204c"},
    {"geal.run_script",                 "geal",       NM_SYM_CODE,        NM_SYM_STATUS_STATIC_VERIFIED, 0x1df8040ull, "4c894c24204c8944"},
    {"geal.run_at_offset",              "geal",       NM_SYM_CODE,        NM_SYM_STATUS_STATIC_VERIFIED, 0x1df8160ull, "48895c2408574883"},
    {"geal.builtin_register_entry",     "geal",       NM_SYM_CODE,        NM_SYM_STATUS_STATIC_VERIFIED, 0x1de9780ull, "483bca0f845b0100"},
    {"geal.builtin_name_list",          "geal",       NM_SYM_DATA,        NM_SYM_STATUS_STATIC_VERIFIED, 0x45c81a8ull, nullptr},
    {"geal.builtin_factory_table",      "geal",       NM_SYM_DATA,        NM_SYM_STATUS_STATIC_VERIFIED, 0x45c50f8ull, nullptr},
    // Rulesets
    {"rulesets.creator_table",          "rulesets",   NM_SYM_DATA,        NM_SYM_STATUS_STATIC_VERIFIED, 0x475dc70ull, nullptr},
    {"rulesets.create",                 "rulesets",   NM_SYM_CODE,        NM_SYM_STATUS_STATIC_VERIFIED, 0x15a6800ull, "48895c2408555657"},
    {"rulesets.s2c_0x2c9_filter",       "rulesets",   NM_SYM_CODE,        NM_SYM_STATUS_STATIC_VERIFIED, 0x15a6b50ull, "48895c2408555657"},
    {"rulesets.c2s_0x2ca_writer",       "rulesets",   NM_SYM_CODE,        NM_SYM_STATUS_STATIC_VERIFIED, 0x1fa7c10ull, "48895c2410574883"},
    // Network
    {"network.message_pump",            "network",    NM_SYM_CODE,        NM_SYM_STATUS_STATIC_VERIFIED, 0x29a77a0ull, "488bc44889501048"},
    {"network.register_handler_group",  "network",    NM_SYM_CODE,        NM_SYM_STATUS_STATIC_VERIFIED, 0x29a7710ull, "48895c2410574883"},
    {"network.socket_write",            "network",    NM_SYM_CODE,        NM_SYM_STATUS_LIVE_VERIFIED,   0x0dfa000ull, "48895c240848896c"},
    {"network.socket_flush",            "network",    NM_SYM_CODE,        NM_SYM_STATUS_LIVE_VERIFIED,   0x0dfa160ull, "48895c2410488974"},
    {"network.chat_panel_listener",     "network",    NM_SYM_CODE,        NM_SYM_STATUS_LIVE_VERIFIED,   0x017a270ull, "48895c2408488974"},
    {"network.app_global_ref",          "network",    NM_SYM_GLOBAL_REF,  NM_SYM_STATUS_LIVE_VERIFIED,   0x00f3b30ull, "488b05????????488907488b05"},
    {"network.hnc_mask",                "network",    NM_SYM_DATA,        NM_SYM_STATUS_DOC_VERIFIED,    0x2feaeb0ull, nullptr},
    // Allocator & runtime
    {"allocator.alloc",                 "allocator",  NM_SYM_CODE,        NM_SYM_STATUS_LIVE_VERIFIED,   0x2c8cf60ull, "48895c2408488974"},
    {"allocator.free",                  "allocator",  NM_SYM_CODE,        NM_SYM_STATUS_STATIC_VERIFIED, 0x2c8ca30ull, "4c8bd14c8d49ff49"},
    {"runtime.wstring_assign",          "runtime",    NM_SYM_CODE,        NM_SYM_STATUS_LIVE_VERIFIED,   0x0144010ull, "48895c241048896c"},
    {"runtime.cxx_throw_exception",     "runtime",    NM_SYM_CODE,        NM_SYM_STATUS_LIVE_VERIFIED,   0x2ce6a40ull, "48895c2418488974"},
    {"runtime.deadlock_detector",       "runtime",    NM_SYM_CODE,        NM_SYM_STATUS_STATIC_VERIFIED, 0x0e3ed10ull, "4055535657415441"},
    // Audio, Stats, World
    {"audio.submit_request",            "audio",      NM_SYM_CODE,        NM_SYM_STATUS_STATIC_VERIFIED, 0x0ff86e0ull, "48895c2410555657"},
    {"stats.set_local",                 "stats",      NM_SYM_CODE,        NM_SYM_STATUS_STATIC_VERIFIED, 0x24d65c0ull, "48895c241848896c"},
    {"stats.read",                      "stats",      NM_SYM_CODE,        NM_SYM_STATUS_STATIC_VERIFIED, 0x24d6510ull, "4c8b99f800000044"},
    {"world.create_client_object",      "world",      NM_SYM_CODE,        NM_SYM_STATUS_STATIC_VERIFIED, 0x1ec47e0ull, "48895c241848894c"},
}};

// 3.29.3c table: shared code RVAs hold; data/vtable/IAT symbols explicitly unverified (rva=0)
// except network.hnc_mask which is verified at 0x2feaed0 in LocalExile ClientBuilds.cs.
constexpr std::array<EmbeddedSymbol, 52> kSymbols3293c = {{
    {"frame.state_stack_pass_a",        "frame",      NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x1528f40ull, nullptr},
    {"frame.state_stack_pass_a_hook",   "frame",      NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x1528fbfull, "488b5d284883eb10"},
    {"frame.ingame_tick",               "frame",      NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x0d54a00ull, nullptr},
    {"frame.instance_tick",             "frame",      NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x214f1d0ull, nullptr},
    {"ui.control_load",                 "ui",         NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x18b08d0ull, nullptr},
    {"ui.control_pool",                 "ui",         NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x18b6d20ull, nullptr},
    {"ui.frame_create",                 "ui",         NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x014b0e0ull, nullptr},
    {"ui.add_child",                    "ui",         NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x18b5b10ull, nullptr},
    {"ui.find_child",                   "ui",         NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x18b6420ull, nullptr},
    {"ui.set_visible",                  "ui",         NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x18b0a30ull, nullptr},
    {"ui.event_list_get",               "ui",         NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x019b2b0ull, nullptr},
    {"ui.event_list_grow",              "ui",         NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x0152430ull, nullptr},
    {"ui.vtable_frame",                 "ui",         NM_SYM_VTABLE,      NM_SYM_STATUS_UNVERIFIED,      0ull,         nullptr},
    {"ui.vtable_label",                 "ui",         NM_SYM_VTABLE,      NM_SYM_STATUS_UNVERIFIED,      0ull,         nullptr},
    {"ui.vtable_button",                "ui",         NM_SYM_VTABLE,      NM_SYM_STATUS_UNVERIFIED,      0ull,         nullptr},
    {"ui.vtable_textbox",               "ui",         NM_SYM_VTABLE,      NM_SYM_STATUS_UNVERIFIED,      0ull,         nullptr},
    {"ui.vtable_checkbox",              "ui",         NM_SYM_VTABLE,      NM_SYM_STATUS_UNVERIFIED,      0ull,         nullptr},
    {"files.read_whole_file",           "files",      NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x0e564d0ull, nullptr},
    {"files.text_stream_open",          "files",      NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x21d5b50ull, nullptr},
    {"files.bundle_index_lookup",       "files",      NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x0e4c430ull, nullptr},
    {"files.oodle_decompress",          "files",      NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x2c9d520ull, nullptr},
    {"files.variant_tag_wstring",       "files",      NM_SYM_DATA,        NM_SYM_STATUS_UNVERIFIED,      0ull,         nullptr},
    {"files.load_trace_before",         "files",      NM_SYM_DATA,        NM_SYM_STATUS_UNVERIFIED,      0ull,         nullptr},
    {"files.load_trace_after",          "files",      NM_SYM_DATA,        NM_SYM_STATUS_UNVERIFIED,      0ull,         nullptr},
    {"files.hot_reload_invalidate_ext", "files",      NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x1010560ull, nullptr},
    {"geal.compiler_statements",        "geal",       NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x1df8210ull, nullptr},
    {"geal.interpreter",                "geal",       NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x1df0d70ull, nullptr},
    {"geal.run_script",                 "geal",       NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x1df8040ull, nullptr},
    {"geal.run_at_offset",              "geal",       NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x1df8160ull, nullptr},
    {"geal.builtin_register_entry",     "geal",       NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x1de9780ull, nullptr},
    {"geal.builtin_name_list",          "geal",       NM_SYM_DATA,        NM_SYM_STATUS_UNVERIFIED,      0ull,         nullptr},
    {"geal.builtin_factory_table",      "geal",       NM_SYM_DATA,        NM_SYM_STATUS_UNVERIFIED,      0ull,         nullptr},
    {"rulesets.creator_table",          "rulesets",   NM_SYM_DATA,        NM_SYM_STATUS_UNVERIFIED,      0ull,         nullptr},
    {"rulesets.create",                 "rulesets",   NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x15a6800ull, nullptr},
    {"rulesets.s2c_0x2c9_filter",       "rulesets",   NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x15a6b50ull, nullptr},
    {"rulesets.c2s_0x2ca_writer",       "rulesets",   NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x1fa7c10ull, nullptr},
    {"network.message_pump",            "network",    NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x29a77a0ull, nullptr},
    {"network.register_handler_group",  "network",    NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x29a7710ull, nullptr},
    {"network.socket_write",            "network",    NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x0dfa000ull, nullptr},
    {"network.socket_flush",            "network",    NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x0dfa160ull, nullptr},
    {"network.chat_panel_listener",     "network",    NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x017a270ull, nullptr},
    {"network.app_global_ref",          "network",    NM_SYM_GLOBAL_REF,  NM_SYM_STATUS_CODE_SHARED,     0x00f3b30ull, "488b05????????488907488b05"},
    {"network.hnc_mask",                "network",    NM_SYM_DATA,        NM_SYM_STATUS_DOC_VERIFIED,    0x2feaed0ull, nullptr},
    {"allocator.alloc",                 "allocator",  NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x2c8cf60ull, nullptr},
    {"allocator.free",                  "allocator",  NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x2c8ca30ull, nullptr},
    {"runtime.wstring_assign",          "runtime",    NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x0144010ull, nullptr},
    {"runtime.cxx_throw_exception",     "runtime",    NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x2ce6a40ull, nullptr},
    {"runtime.deadlock_detector",       "runtime",    NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x0e3ed10ull, nullptr},
    {"audio.submit_request",            "audio",      NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x0ff86e0ull, nullptr},
    {"stats.set_local",                 "stats",      NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x24d65c0ull, nullptr},
    {"stats.read",                      "stats",      NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x24d6510ull, nullptr},
    {"world.create_client_object",      "world",      NM_SYM_CODE,        NM_SYM_STATUS_CODE_SHARED,     0x1ec47e0ull, nullptr},
}};

int hex_nibble(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

} // namespace

nm_build_id_t identify_build_by_sha256(std::string_view sha256_hex) {
    if (sha256_hex.size() != 64) {
        return NM_BUILD_UNKNOWN;
    }
    for (const auto& b : kKnownBuilds) {
        bool eq = true;
        for (size_t i = 0; i < 64; ++i) {
            if (std::tolower(static_cast<unsigned char>(sha256_hex[i])) != b.sha256[i]) {
                eq = false;
                break;
            }
        }
        if (eq) {
            return b.build_id;
        }
    }
    return NM_BUILD_UNKNOWN;
}

const char* build_version_string(nm_build_id_t build_id) {
    switch (build_id) {
        case NM_BUILD_3_29_3C_327984:
        case NM_BUILD_3_29_3C_328126:
            return "3.29.3c";
        case NM_BUILD_3_29_3D_328228:
            return "3.29.3d";
        case NM_BUILD_SYNTHETIC:
            return "synthetic";
        default:
            return "unknown";
    }
}

const EmbeddedSymbol* get_symbol_table(nm_build_id_t build_id, uint32_t& out_count) {
    switch (build_id) {
        case NM_BUILD_3_29_3D_328228:
        case NM_BUILD_SYNTHETIC:
            out_count = static_cast<uint32_t>(kSymbols3293d.size());
            return kSymbols3293d.data();
        case NM_BUILD_3_29_3C_327984:
        case NM_BUILD_3_29_3C_328126:
            out_count = static_cast<uint32_t>(kSymbols3293c.size());
            return kSymbols3293c.data();
        default:
            out_count = 0;
            return nullptr;
    }
}

const EmbeddedSymbol* find_symbol(nm_build_id_t build_id, std::string_view name) {
    uint32_t count = 0;
    const EmbeddedSymbol* table = get_symbol_table(build_id, count);
    if (!table) {
        return nullptr;
    }
    for (uint32_t i = 0; i < count; ++i) {
        if (name == table[i].name) {
            return &table[i];
        }
    }
    return nullptr;
}

nm_status_t verify_memory_bytes(uint64_t address, const char* expected_bytes_hex) {
    if (address == 0 || expected_bytes_hex == nullptr) {
        return NM_ERR_INVALID_ARG;
    }
    const size_t hex_len = std::strlen(expected_bytes_hex);
    if (hex_len == 0 || (hex_len % 2) != 0) {
        return NM_ERR_INVALID_ARG;
    }

    const auto* mem = reinterpret_cast<const uint8_t*>(static_cast<uintptr_t>(address));
    const size_t byte_len = hex_len / 2;
    for (size_t i = 0; i < byte_len; ++i) {
        const char hi = expected_bytes_hex[i * 2];
        const char lo = expected_bytes_hex[i * 2 + 1];
        if (hi == '?' && lo == '?') {
            continue;
        }
        const int hi_v = hex_nibble(hi);
        const int lo_v = hex_nibble(lo);
        if (hi_v < 0 || lo_v < 0) {
            return NM_ERR_INVALID_ARG;
        }
        const uint8_t expected = static_cast<uint8_t>((hi_v << 4) | lo_v);
        if (mem[i] != expected) {
            return NM_ERR_BYTE_MISMATCH;
        }
    }
    return NM_OK;
}

} // namespace nativemods
