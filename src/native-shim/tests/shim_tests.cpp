#include "nativemods_abi.h"
#include "address_table.hpp"
#include "exception_wall.hpp"
#include "game_allocator.hpp"
#include "hook_engine.hpp"
#include "live_engine_hooks.hpp"
#include "overlay_and_channels.hpp"
#include "phase1_services.hpp"
#include "phase2_services.hpp"
#include "shim_logger.hpp"

#include <atomic>
#include <cassert>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

#if !defined(_WIN32)
#  include <sys/mman.h>
#else
#  define WIN32_LEAN_AND_MEAN
#  include <windows.h>
#  ifdef exception_code
#    undef exception_code
#  endif
#endif

namespace {

int g_tests_passed = 0;

#define CHECK(cond)                                                                                   \
    do {                                                                                              \
        if (!(cond)) {                                                                                \
            std::fprintf(stderr, "FAIL at %s:%d: %s\n", __FILE__, __LINE__, #cond);                   \
            return 1;                                                                                 \
        }                                                                                             \
    } while (0)

int test_abi_table_and_build_recognition() {
    const nm_abi_table_t* table = nullptr;
    CHECK(nm_get_abi_table(&table) == NM_OK);
    CHECK(table != nullptr);
    CHECK(table->struct_size == sizeof(nm_abi_table_t));
    CHECK(sizeof(nm_abi_table_t) == 864u);
    CHECK(sizeof(nm_screens_state_t) == 280u);
    CHECK(sizeof(nm_input_event_t) == 40u);
    CHECK(sizeof(nm_item_snapshot_t) == 448u);
    CHECK(sizeof(nm_hover_state_t) == 952u);
    CHECK(sizeof(nm_world_entity_query_t) == 160u);
    CHECK(sizeof(nm_world_entity_snapshot_t) == 760u);
    CHECK(sizeof(nm_stat_entry_t) == 8u);
    CHECK(sizeof(nm_area_info_t) == 352u);
    CHECK(sizeof(nm_item_filter_state_t) == 784u);
    CHECK(sizeof(nm_item_filter_result_t) == 72u);
    CHECK(sizeof(nm_camera_state_t) == 120u);
    CHECK(table->abi_version == NM_ABI_VERSION);
    CHECK(NM_ABI_VERSION == 0x00000400u);
    CHECK(table->get_host_info != nullptr);
    CHECK(table->lookup_symbol != nullptr);
    CHECK(table->install_inline_hook != nullptr);
    CHECK(table->install_vtable_hook != nullptr);
    CHECK(table->install_inline_chain_hook != nullptr);
    CHECK(table->install_inline_chain_hook_sym != nullptr);
    CHECK(table->install_vtable_chain_hook != nullptr);
    CHECK(table->call_guarded != nullptr);
    CHECK(table->call_native_guarded != nullptr);
    CHECK(table->game_alloc != nullptr);
    CHECK(table->send_ruleset_packet != nullptr);
    CHECK(table->files_read_stock != nullptr);
    CHECK(table->files_invalidate != nullptr);
    CHECK(table->send_raw_packet != nullptr);
    CHECK(table->register_opcode_handler_group != nullptr);
    CHECK(table->unregister_opcode_handler_group != nullptr);
    CHECK(table->stream_read != nullptr);
    CHECK(table->ui_create_window_into != nullptr);
    CHECK(table->ui_get_rect != nullptr);
    CHECK(table->ui_set_position != nullptr);
    CHECK(table->ui_set_size != nullptr);
    CHECK(table->ui_set_enabled != nullptr);
    CHECK(table->ui_remove_child != nullptr);
    CHECK(table->ui_destroy_control != nullptr);
    CHECK(table->ui_textbox_get_text != nullptr);
    CHECK(table->ui_textbox_set_text != nullptr);
    CHECK(table->ui_textbox_subscribe_changed != nullptr);
    CHECK(table->ui_textbox_subscribe_submitted != nullptr);
    CHECK(table->ui_checkbox_get_checked != nullptr);
    CHECK(table->ui_checkbox_set_checked != nullptr);
    CHECK(table->ui_checkbox_subscribe_changed != nullptr);
    CHECK(table->ui_unsubscribe != nullptr);
    CHECK(table->clipboard_get_text != nullptr);
    CHECK(table->clipboard_set_text != nullptr);
    CHECK(table->ui_get_enabled != nullptr);
    CHECK(table->ui_get_active_root == &nm_ui_get_active_root);
    CHECK(table->ui_get_child_count == &nm_ui_get_child_count);
    CHECK(table->ui_get_child_at == &nm_ui_get_child_at);
    CHECK(table->ui_get_vtable == &nm_ui_get_vtable);
    CHECK(table->ui_get_control_tag == &nm_ui_get_control_tag);
    CHECK(table->ui_get_control_id == &nm_ui_get_control_id);
    CHECK(table->ui_get_control_parent == &nm_ui_get_control_parent);
    CHECK(table->ui_is_visible == &nm_ui_is_visible);
    CHECK(table->ui_set_width == &nm_ui_set_width);
    CHECK(table->ui_set_height == &nm_ui_set_height);
    CHECK(table->ui_create_checkbox_child == &nm_ui_create_checkbox_child);
    CHECK(table->ui_create_stack_layout == &nm_ui_create_stack_layout);
    CHECK(table->ui_layout_update == &nm_ui_layout_update);
    CHECK(table->ui_set_anchor == &nm_ui_set_anchor);
    CHECK(table->screens_get_state == &nm_screens_get_state);
    CHECK(table->screens_set_callback == &nm_screens_set_callback);
    CHECK(table->input_set_callback == &nm_input_set_callback);
    CHECK(table->input_is_text_focused == &nm_input_is_text_focused);
    CHECK(table->hover_get_state == &nm_hover_get_state);
    CHECK(table->pregame_set_callbacks == &nm_pregame_set_callbacks);
    CHECK(table->world_get_local_player == &nm_world_get_local_player);
    CHECK(table->world_list_entities == &nm_world_list_entities);
    CHECK(table->world_get_entity == &nm_world_get_entity);
    CHECK(table->world_create_client_object == &nm_world_create_client_object);
    CHECK(table->world_remove_client_object == &nm_world_remove_client_object);
    CHECK(table->stats_read_entity == &nm_stats_read_entity);
    CHECK(table->stats_set_local_entity == &nm_stats_set_local_entity);
    CHECK(table->stats_list_entity == &nm_stats_list_entity);
    CHECK(table->lifecycle_get_area_info == &nm_lifecycle_get_area_info);
    CHECK(table->lifecycle_set_area_callback == &nm_lifecycle_set_area_callback);
    CHECK(table->filter_get_state == &nm_filter_get_state);
    CHECK(table->filter_reload == &nm_filter_reload);
    CHECK(table->filter_get_world_item_result == &nm_filter_get_world_item_result);
    CHECK(table->filter_evaluate_entity == &nm_filter_evaluate_entity);
    CHECK(table->filter_set_reload_callback == &nm_filter_set_reload_callback);
    CHECK(table->camera_get_state == &nm_camera_get_state);
    CHECK(table->camera_world_to_screen == &nm_camera_world_to_screen);
    CHECK(table->camera_set_zoom_override == &nm_camera_set_zoom_override);

    // SHA-256 build recognition matches LocalExile ClientBuilds.cs verbatim
    CHECK(nativemods::identify_build_by_sha256("e9ba3d31c4d922e1826ef01d02fd22178a001b29e17264b68fc2446efd41f53d")
          == NM_BUILD_3_29_3D_328228);
    CHECK(nativemods::identify_build_by_sha256("cc8aa38345be2a837575215d2ce142ebecf7b007345843265b335ebb8ac5964c")
          == NM_BUILD_3_29_3C_327984);
    CHECK(nativemods::identify_build_by_sha256("01a22facf36886b39bca74793c0d8c7295b1495f1625b4ebc7b23c2de32a70cf")
          == NM_BUILD_3_29_3C_328126);
    CHECK(nativemods::identify_build_by_sha256("0000000000000000000000000000000000000000000000000000000000000000")
          == NM_BUILD_UNKNOWN);

    // Refuses NM_BUILD_UNKNOWN
    CHECK(nm_initialize_for_build(NM_BUILD_UNKNOWN, 0x140000000ull, 0x4d9a000u, nullptr) == NM_ERR_UNSUPPORTED_BUILD);

    ++g_tests_passed;
    return 0;
}

int test_address_table_3293d_and_3293c() {
    // 3.29.3d with rebased module (DYNAMIC_BASE / ASLR)
    constexpr uint64_t kRebased = 0x7ff610000000ull;
    CHECK(nm_initialize_for_build(
              NM_BUILD_3_29_3D_328228,
              kRebased,
              0x4d9a000u,
              "e9ba3d31c4d922e1826ef01d02fd22178a001b29e17264b68fc2446efd41f53d") == NM_OK);

    nm_host_info_t host{};
    CHECK(nm_get_host_info(&host) == NM_OK);
    CHECK(host.build_id == NM_BUILD_3_29_3D_328228);
    CHECK(std::strcmp(host.build_version, "3.29.3d") == 0);
    CHECK(host.image_base == kRebased);
    CHECK(host.symbol_count == 319u);

    nm_symbol_info_t sym{};
    CHECK(nm_lookup_symbol("frame.state_stack_pass_a_hook", &sym) == NM_OK);
    CHECK(sym.rva == 0x1528fbfull);
    CHECK(sym.runtime_address == kRebased + 0x1528fbfull);
    CHECK(std::strcmp(sym.expected_bytes_hex, "488b5d284883eb10") == 0);

    CHECK(nm_lookup_symbol("files.hash_to_wstring_view", &sym) == NM_OK);
    CHECK(sym.rva == 0x0e40af0ull);
    CHECK(nm_lookup_symbol("files.hash_and_register_path", &sym) == NM_OK);
    CHECK(sym.rva == 0x0e40a90ull);
    CHECK(nm_lookup_symbol("runtime.wstring_destroy", &sym) == NM_OK);
    CHECK(sym.rva == 0x0134500ull);
    CHECK(nm_lookup_symbol("ui.control_set_position", &sym) == NM_OK);
    CHECK(sym.rva == 0x18b1080ull);
    CHECK(nm_lookup_symbol("ui.control_set_size", &sym) == NM_OK);
    CHECK(sym.rva == 0x1906a30ull);
    CHECK(nm_lookup_symbol("ui.control_get_abs_pos", &sym) == NM_OK);
    CHECK(sym.rva == 0x18b1250ull);
    CHECK(nm_lookup_symbol("ui.control_detach", &sym) == NM_OK);
    CHECK(sym.rva == 0x18b5a00ull);
    CHECK(nm_lookup_symbol("ui.children_remove_at", &sym) == NM_OK);
    CHECK(sym.rva == 0x18b6720ull);
    CHECK(nm_lookup_symbol("ui.event_list_get_ctrl", &sym) == NM_OK);
    CHECK(sym.rva == 0x055ae30ull);
    CHECK(nm_lookup_symbol("files.filesystem_holder_get", &sym) == NM_OK);
    CHECK(sym.rva == 0x0e571d0ull);
    CHECK(nm_lookup_symbol("files.hot_reload_singleton", &sym) == NM_OK);
    CHECK(sym.rva == 0x10062a0ull);
    CHECK(nm_lookup_symbol("network.stream_reader", &sym) == NM_OK);
    CHECK(sym.rva == 0x0df9ed0ull);

    CHECK(nm_lookup_symbol("rulesets.creator_table", &sym) == NM_OK);
    CHECK(sym.rva == 0x475dc70ull);
    CHECK(sym.runtime_address == kRebased + 0x475dc70ull);

    CHECK(nm_lookup_symbol("network.hnc_mask", &sym) == NM_OK);
    CHECK(sym.rva == 0x2feaeb0ull);

    // Phase 1 verified symbols on 3.29.3d (deduplicated + ASLR-safe vtables)
    CHECK(nm_lookup_symbol("frame.state_stack_global", &sym) == NM_OK);
    CHECK(sym.rva == 0x45d6fc0ull && sym.kind == NM_SYM_DATA && sym.expected_bytes_hex == nullptr);
    CHECK(nm_lookup_symbol("frame.state_stack_get", &sym) == NM_OK);
    CHECK(sym.rva == 0x00f2890ull && sym.kind == NM_SYM_CODE);
    CHECK(nm_lookup_symbol("frame.state_stack_on_wndmsg", &sym) == NM_OK);
    CHECK(sym.rva == 0x1529910ull && sym.kind == NM_SYM_CODE);
    CHECK(nm_lookup_symbol("frame.vtable_create_char_state", &sym) == NM_OK);
    CHECK(sym.rva == 0x31f28e0ull && sym.kind == NM_SYM_VTABLE && sym.expected_bytes_hex == nullptr);
    CHECK(nm_lookup_symbol("network.login_write_create_char_0x12", &sym) == NM_OK);
    CHECK(sym.rva == 0x2146de0ull && sym.kind == NM_SYM_CODE);
    CHECK(nm_lookup_symbol("frame.create_char_result_handler", &sym) == NM_OK);
    CHECK(sym.rva == 0x0cf7fb0ull && sym.kind == NM_SYM_CODE);
    CHECK(nm_lookup_symbol("ui.league_overlay_fill", &sym) == NM_OK);
    CHECK(sym.rva == 0x0cccff0ull && sym.kind == NM_SYM_CODE);
    CHECK(nm_lookup_symbol("ui.vtable_league_card", &sym) == NM_OK);
    CHECK(sym.rva == 0x31eccb0ull && sym.kind == NM_SYM_VTABLE && sym.expected_bytes_hex == nullptr);
    CHECK(nm_lookup_symbol("ui.vtable_pc_hud", &sym) == NM_OK);
    CHECK(sym.rva == 0x31fbf20ull && sym.kind == NM_SYM_VTABLE && sym.expected_bytes_hex == nullptr);
    CHECK(nm_lookup_symbol("ui.vtable_control", &sym) == NM_OK);
    CHECK(sym.rva == 0x34a8f28ull && sym.kind == NM_SYM_VTABLE && sym.expected_bytes_hex == nullptr);
    CHECK(nm_lookup_symbol("ui.ingame_ui_init", &sym) == NM_OK);
    CHECK(sym.rva == 0x0dad910ull && sym.kind == NM_SYM_CODE);
    CHECK(nm_lookup_symbol("items.cursor_get_held_item", &sym) == NM_OK);
    CHECK(sym.rva == 0x16fcbc0ull && sym.kind == NM_SYM_CODE);

    // Phase 2 verified symbols on 3.29.3d (World, Stats, Lifecycle, ItemFilter, Camera)
    CHECK(nm_lookup_symbol("world.get_instance_client", &sym) == NM_OK);
    CHECK(sym.rva == 0x00f3740ull && sym.kind == NM_SYM_CODE);
    CHECK(nm_lookup_symbol("world.entity_mgr_find_by_id", &sym) == NM_OK);
    CHECK(sym.rva == 0x1ec5880ull && sym.kind == NM_SYM_CODE);
    CHECK(nm_lookup_symbol("world.client_world_init_area", &sym) == NM_OK);
    CHECK(sym.rva == 0x1653ee0ull && sym.kind == NM_SYM_CODE);
    CHECK(nm_lookup_symbol("components.get_positioned", &sym) == NM_OK);
    CHECK(sym.rva == 0x020f350ull && sym.kind == NM_SYM_CODE);
    CHECK(nm_lookup_symbol("components.get_world_item", &sym) == NM_OK);
    CHECK(sym.rva == 0x0141a20ull && sym.kind == NM_SYM_CODE);
    CHECK(nm_lookup_symbol("components.get_stats", &sym) == NM_OK);
    CHECK(sym.rva == 0x013e140ull && sym.kind == NM_SYM_CODE);
    CHECK(nm_lookup_symbol("stats.notify_listeners", &sym) == NM_OK);
    CHECK(sym.rva == 0x24d67d0ull && sym.kind == NM_SYM_CODE);
    CHECK(nm_lookup_symbol("frame.area_loading_leave", &sym) == NM_OK);
    CHECK(sym.rva == 0x152b710ull && sym.kind == NM_SYM_CODE);
    CHECK(nm_lookup_symbol("items.options_singleton_get", &sym) == NM_OK);
    CHECK(sym.rva == 0x190abe0ull && sym.kind == NM_SYM_CODE);
    CHECK(nm_lookup_symbol("items.options_singleton_global", &sym) == NM_OK);
    CHECK(sym.rva == 0x475e9f0ull && sym.kind == NM_SYM_DATA && sym.expected_bytes_hex == nullptr);
    CHECK(nm_lookup_symbol("items.filter_match", &sym) == NM_OK);
    CHECK(sym.rva == 0x0e15970ull && sym.kind == NM_SYM_CODE);
    CHECK(nm_lookup_symbol("items.filter_notify_loaded", &sym) == NM_OK);
    CHECK(sym.rva == 0x1935130ull && sym.kind == NM_SYM_CODE);
    CHECK(nm_lookup_symbol("items.filter_style_dtor_custom_sounds", &sym) == NM_OK);
    CHECK(sym.rva == 0x0134470ull && sym.kind == NM_SYM_CODE);
    CHECK(nm_lookup_symbol("items.world_item_is_visible", &sym) == NM_OK);
    CHECK(sym.rva == 0x15e5ed0ull && sym.kind == NM_SYM_CODE);
    CHECK(nm_lookup_symbol("ui.vtable_world_label_mgr", &sym) == NM_OK);
    CHECK(sym.rva == 0x31e7f30ull && sym.kind == NM_SYM_VTABLE && sym.expected_bytes_hex == nullptr);
    CHECK(nm_lookup_symbol("world.camera_world_to_screen", &sym) == NM_OK);
    CHECK(sym.rva == 0x0fd3b40ull && sym.kind == NM_SYM_CODE);
    CHECK(nm_lookup_symbol("world.vtable_camera_follow", &sym) == NM_OK);
    CHECK(sym.rva == 0x3221068ull && sym.kind == NM_SYM_VTABLE && sym.expected_bytes_hex == nullptr);

    // Every vtable symbol in 3.29.3d MUST have expected_bytes_hex == nullptr (ASLR safety).
    for (uint32_t i = 0; i < host.symbol_count; ++i) {
        nm_symbol_info_t s{};
        CHECK(nm_get_symbol_at(i, &s) == NM_OK);
        if (s.kind == NM_SYM_VTABLE) {
            CHECK(s.expected_bytes_hex == nullptr);
        }
    }

    CHECK(nm_lookup_symbol("nonexistent.symbol", &sym) == NM_ERR_SYMBOL_NOT_FOUND);

    const nativemods::EmbeddedOffset* off_flags_3d =
        nativemods::find_offset(NM_BUILD_3_29_3D_328228, "ui.control.flags");
    CHECK(off_flags_3d != nullptr);
    CHECK(off_flags_3d->offset == 0x1e8u);
    CHECK(nativemods::find_offset(NM_BUILD_3_29_3D_328228, "frame.state_stack.active_begin")->offset == 0x08u);
    CHECK(nativemods::find_offset(NM_BUILD_3_29_3D_328228, "ui.league_overlay.cards_flow")->offset == 0x3c0u);
    CHECK(nativemods::find_offset(NM_BUILD_3_29_3D_328228, "ui.ingame_ui.inventory_panel")->offset == 0x628u);
    CHECK(nativemods::find_offset(NM_BUILD_3_29_3D_328228, "items.item_view.entity")->offset == 0x420u);
    // Phase 2 offsets & stale world.object_map fix (0x8d8 -> 0xa18)
    CHECK(nativemods::find_offset(NM_BUILD_3_29_3D_328228, "world.object_map")->offset == 0xa18u);
    CHECK(nativemods::find_offset(NM_BUILD_3_29_3D_328228, "world.client_world.entity_manager")->offset == 0xa18u);
    CHECK(nativemods::find_offset(NM_BUILD_3_29_3D_328228, "world.client_world.local_player")->offset == 0x970u);
    CHECK(nativemods::find_offset(NM_BUILD_3_29_3D_328228, "stats.comp.entries_begin")->offset == 0xf0u);
    CHECK(nativemods::find_offset(NM_BUILD_3_29_3D_328228, "frame.ingame_state.camera_ctrl")->offset == 0x270u);
    CHECK(nativemods::find_offset(NM_BUILD_3_29_3D_328228, "world.camera.view_proj")->offset == 0x100u);
    CHECK(nativemods::find_offset(NM_BUILD_3_29_3D_328228, "items.world_item.filter_style")->offset == 0x70u);
    CHECK(nativemods::find_offset(NM_BUILD_3_29_3D_328228, "nonexistent.offset") == nullptr);

    // 3.29.3c: code RVAs resolve, HNC mask resolves to 0x2feaed0, unverified data returns NM_ERR_SYMBOL_UNVERIFIED
    CHECK(nm_initialize_for_build(
              NM_BUILD_3_29_3C_328126,
              kRebased,
              0x4d9a000u,
              "01a22facf36886b39bca74793c0d8c7295b1495f1625b4ebc7b23c2de32a70cf") == NM_OK);

    CHECK(nm_get_host_info(&host) == NM_OK);
    CHECK(host.symbol_count == 319u);
    CHECK(nm_lookup_symbol("items.filter_notify_loaded", &sym) == NM_ERR_SYMBOL_UNVERIFIED);
    CHECK(sym.rva == 0ull && sym.status == NM_SYM_STATUS_UNVERIFIED);
    CHECK(nm_lookup_symbol("ui.vtable_world_label_mgr", &sym) == NM_ERR_SYMBOL_UNVERIFIED);
    CHECK(sym.rva == 0ull && sym.status == NM_SYM_STATUS_UNVERIFIED);
    CHECK(nm_lookup_symbol("network.login_write_create_char_0x12", &sym) == NM_ERR_SYMBOL_UNVERIFIED);
    CHECK(sym.rva == 0ull && sym.status == NM_SYM_STATUS_UNVERIFIED);
    CHECK(nm_lookup_symbol("ui.vtable_pc_hud", &sym) == NM_ERR_SYMBOL_UNVERIFIED);
    CHECK(sym.rva == 0ull && sym.status == NM_SYM_STATUS_UNVERIFIED);

    CHECK(nm_lookup_symbol("frame.state_stack_pass_a_hook", &sym) == NM_OK);
    CHECK(sym.rva == 0x1528fbfull);
    CHECK(sym.status == NM_SYM_STATUS_CODE_SHARED);

    CHECK(nm_lookup_symbol("files.hash_to_wstring_view", &sym) == NM_OK);
    CHECK(sym.rva == 0x0e40af0ull);
    CHECK(sym.status == NM_SYM_STATUS_CODE_SHARED);

    CHECK(nm_lookup_symbol("runtime.wstring_destroy", &sym) == NM_ERR_SYMBOL_UNVERIFIED);
    CHECK(sym.rva == 0ull);
    CHECK(sym.status == NM_SYM_STATUS_UNVERIFIED);

    CHECK(nm_lookup_symbol("network.hnc_mask", &sym) == NM_OK);
    CHECK(sym.rva == 0x2feaed0ull);

    CHECK(nm_lookup_symbol("rulesets.creator_table", &sym) == NM_ERR_SYMBOL_UNVERIFIED);
    CHECK(sym.rva == 0ull);
    CHECK(sym.runtime_address == 0ull);
    CHECK(sym.status == NM_SYM_STATUS_UNVERIFIED);

    const nativemods::EmbeddedOffset* off_flags_3c =
        nativemods::find_offset(NM_BUILD_3_29_3C_328126, "ui.control.flags");
    CHECK(off_flags_3c != nullptr);
    CHECK(off_flags_3c->offset == 0x1e8u);

    CHECK(nm_shutdown() == NM_OK);

    // Synthetic mode: ui_get_enabled returns INVALID_ARG / NOT_SUPPORTED_ON_PLATFORM cleanly
    CHECK(nm_initialize_for_build(NM_BUILD_SYNTHETIC, 0x10000000ull, 0x10000u, nullptr) == NM_OK);
    {
        uint8_t dummy_ctrl[64] = {};
        int32_t enabled = 123;
        CHECK(nm_ui_get_enabled(nullptr, &enabled) == NM_ERR_INVALID_ARG);
        CHECK(nm_ui_get_enabled(dummy_ctrl, nullptr) == NM_ERR_INVALID_ARG);
        CHECK(nm_ui_get_enabled(dummy_ctrl, &enabled) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);
        CHECK(enabled == 0);
    }
    CHECK(nm_shutdown() == NM_OK);
    ++g_tests_passed;
    return 0;
}

int test_byte_verification_wildcards() {
    const uint8_t sample[] = {
        0x48, 0x8b, 0x05, 0xb1, 0x34, 0x4e, 0x04, 0x48, 0x89, 0x07, 0x48, 0x8b, 0x05
    };
    const uint64_t addr = reinterpret_cast<uint64_t>(sample);

    CHECK(nm_verify_bytes(addr, "488b05????????488907488b05") == NM_OK);
    CHECK(nm_verify_bytes(addr, "488b05b1344e04488907488b05") == NM_OK);
    CHECK(nm_verify_bytes(addr, "488b05????????488907488b06") == NM_ERR_BYTE_MISMATCH);
    CHECK(nm_verify_bytes(addr, "488b0") == NM_ERR_INVALID_ARG); // odd length

    ++g_tests_passed;
    return 0;
}

// Globals for inline & vtable detour tests
using TestAddFn = int(*)(int);
#if !defined(_WIN32) && (defined(__x86_64__) || defined(_M_X64))
TestAddFn g_inline_trampoline = nullptr;
TestAddFn g_inline_trampoline2 = nullptr;
int g_detour_hits = 0;
int g_detour_hits2 = 0;

int inline_detour_fn(int x) {
    ++g_detour_hits;
    return g_inline_trampoline(x) + 100;
}

int inline_detour_fn2(int x) {
    ++g_detour_hits2;
    return g_inline_trampoline2(x) + 1000;
}
#endif

int vtable_orig_fn(int x) { return x * 2; }
TestAddFn g_vtable_orig = nullptr;
TestAddFn g_vtable_orig2 = nullptr;
int vtable_detour_fn(int x) { return g_vtable_orig(x) + 7; }
int vtable_detour_fn2(int x) { return g_vtable_orig2(x) + 50; }

int test_inline_and_vtable_hooks() {
    CHECK(nm_initialize_for_build(NM_BUILD_SYNTHETIC, 0x10000000ull, 0x10000u, nullptr) == NM_OK);

#if !defined(_WIN32) && (defined(__x86_64__) || defined(_M_X64))
    // Synthesize a clean position-independent x86-64 function in RWX memory:
    //   14x NOP (0x90)             -- cleanly stolen by 14-byte abs jmp patch
    //   8D 47 05                   -- lea eax, [rdi + 5]  (SysV x64 arg0=edi)
    //   C3                         -- ret
    void* code_page = mmap(nullptr, 4096, PROT_READ | PROT_WRITE | PROT_EXEC, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    CHECK(code_page != MAP_FAILED);
    auto* code = static_cast<uint8_t*>(code_page);
    std::memset(code, 0x90, 16);
    code[16] = 0x8D;
    code[17] = 0x47;
    code[18] = 0x05;
    code[19] = 0xC3;

    auto target_fn = reinterpret_cast<TestAddFn>(code_page);
    CHECK(target_fn(10) == 15);

    // Wrong expected bytes MUST fail without touching target memory
    void* tramp_raw = nullptr;
    nm_hook_handle_t handle = NM_HOOK_INVALID;
    CHECK(nm_install_inline_hook_at(
              reinterpret_cast<uint64_t>(code_page),
              "deadbeef",
              reinterpret_cast<void*>(&inline_detour_fn),
              &tramp_raw,
              &handle) == NM_ERR_BYTE_MISMATCH);
    CHECK(target_fn(10) == 15);

    // Matching expected bytes installs detour + trampoline cleanly
    g_detour_hits = 0;
    CHECK(nm_install_inline_hook_at(
              reinterpret_cast<uint64_t>(code_page),
              "9090909090909090",
              reinterpret_cast<void*>(&inline_detour_fn),
              &tramp_raw,
              &handle) == NM_OK);
    CHECK(handle != NM_HOOK_INVALID);
    CHECK(tramp_raw != nullptr);
    g_inline_trampoline = reinterpret_cast<TestAddFn>(tramp_raw);

    // Multi-mod raw detour chaining at same address MUST compose cleanly and unhook independently!
    void* tramp_raw2 = nullptr;
    nm_hook_handle_t handle2 = NM_HOOK_INVALID;
    g_detour_hits2 = 0;
    CHECK(nm_install_inline_hook_at(
              reinterpret_cast<uint64_t>(code_page),
              "9090909090909090",
              reinterpret_cast<void*>(&inline_detour_fn2),
              &tramp_raw2,
              &handle2) == NM_OK);
    CHECK(handle2 != NM_HOOK_INVALID && handle2 != handle && tramp_raw2 != nullptr);
    g_inline_trampoline2 = reinterpret_cast<TestAddFn>(tramp_raw2);

    // Call target: runs detour2 -> detour1 -> trampoline -> original -> +1100
    CHECK(target_fn(10) == 1115);
    CHECK(g_detour_hits == 1);
    CHECK(g_detour_hits2 == 1);

    // Remove first detour independently: detour2 still runs!
    CHECK(nm_remove_hook(handle) == NM_OK);
    CHECK(nm_remove_hook(handle) == NM_ERR_HOOK_NOT_FOUND);
    CHECK(target_fn(10) == 1015);
    CHECK(g_detour_hits == 1);
    CHECK(g_detour_hits2 == 2);

    // Remove second detour: restores original 14 bytes and original behavior
    CHECK(nm_remove_hook(handle2) == NM_OK);
    CHECK(target_fn(10) == 15);
    CHECK(code[0] == 0x90 && code[13] == 0x90);

    munmap(code_page, 4096);
#endif

    // Vtable hook + multi-mod vtable raw chaining test
    void* vtable[4] = {
        nullptr,
        reinterpret_cast<void*>(&vtable_orig_fn),
        nullptr,
        nullptr,
    };
    void* orig_slot = nullptr;
    nm_hook_handle_t vt_handle = NM_HOOK_INVALID;
    CHECK(nm_install_vtable_hook(vtable, 1, reinterpret_cast<void*>(&vtable_detour_fn), &orig_slot, &vt_handle) == NM_OK);
    g_vtable_orig = reinterpret_cast<TestAddFn>(orig_slot);
    CHECK(reinterpret_cast<TestAddFn>(vtable[1])(10) == 27);

    void* orig_slot2 = nullptr;
    nm_hook_handle_t vt_handle2 = NM_HOOK_INVALID;
    CHECK(nm_install_vtable_hook(vtable, 1, reinterpret_cast<void*>(&vtable_detour_fn2), &orig_slot2, &vt_handle2) == NM_OK);
    g_vtable_orig2 = reinterpret_cast<TestAddFn>(orig_slot2);
    CHECK(reinterpret_cast<TestAddFn>(vtable[1])(10) == 77);

    // Remove first vtable hook independently: second keeps running
    CHECK(nm_remove_hook(vt_handle) == NM_OK);
    CHECK(reinterpret_cast<TestAddFn>(vtable[1])(10) == 70);

    CHECK(nm_remove_hook(vt_handle2) == NM_OK);
    CHECK(vtable[1] == reinterpret_cast<void*>(&vtable_orig_fn));
    CHECK(reinterpret_cast<TestAddFn>(vtable[1])(10) == 20);

    CHECK(nm_shutdown() == NM_OK);
    ++g_tests_passed;
    return 0;
}

int test_exception_wall() {
    int64_t result = 0;
    nm_exception_info_t ex{};

    // Happy path
    auto ok_fn = [](void* ctx) -> int64_t {
        return *static_cast<int64_t*>(ctx) * 3;
    };
    int64_t arg = 14;
    CHECK(nm_call_guarded(ok_fn, &arg, &result, &ex) == NM_OK);
    CHECK(result == 42);
    CHECK(ex.exception_code == 0);

    // Simulated MSVC 0xE06D7363 exception wall isolation
    auto throw_msvc = [](void*) -> int64_t {
        throw nativemods::SimulatedMsvcException("Metadata/UI/Bad.ui(12,4): duplicate id 'root'");
    };
    CHECK(nm_call_guarded(throw_msvc, nullptr, &result, &ex) == NM_ERR_GAME_EXCEPTION);
    CHECK(ex.exception_code == NM_MSVC_CXX_EXCEPTION_CODE);
    CHECK(std::strstr(ex.message, "duplicate id") != nullptr);

    // Standard C++ exception isolation
    auto throw_std = [](void*) -> int64_t {
        throw std::runtime_error("boom");
    };
    CHECK(nm_call_guarded(throw_std, nullptr, &result, &ex) == NM_ERR_GAME_EXCEPTION);
    CHECK(ex.exception_code == NM_MSVC_CXX_EXCEPTION_CODE);
    CHECK(std::strstr(ex.message, "boom") != nullptr);

    ++g_tests_passed;
    return 0;
}

int test_game_allocator_header_abi() {
    CHECK(nm_initialize_for_build(NM_BUILD_SYNTHETIC, 0x10000000ull, 0x10000u, nullptr) == NM_OK);

    void* ptr = nullptr;
    constexpr size_t kPayloadSize = 128;
    CHECK(nm_game_alloc(kPayloadSize, 16, NM_DEFAULT_ALLOC_TAG, &ptr) == NM_OK);
    CHECK(ptr != nullptr);
    CHECK((reinterpret_cast<uintptr_t>(ptr) % 16) == 0);

    // Verify exact 16-byte header matching 08-deep-analysis.md §6 and spikes/agent.js#L159
    const auto* hdr = reinterpret_cast<const nm_game_alloc_header_t*>(static_cast<const uint8_t*>(ptr) - 16);
    CHECK(hdr->size == kPayloadSize);
    CHECK(hdr->reserved0 == 0);
    CHECK(hdr->log2_align == 4); // log2(16)
    CHECK(hdr->tag == 0x0500);
    CHECK(hdr->reserved1 == 0);

    // Exact 32-bit word at payload - 4 must be 0x00050004 (matching spikes/agent.js)
    uint32_t meta_word = 0;
    std::memcpy(&meta_word, static_cast<const uint8_t*>(ptr) - 4, sizeof(meta_word));
    CHECK(meta_word == 0x00050004u);

    std::memset(ptr, 0xAB, kPayloadSize);
    CHECK(nm_game_free(ptr, 16) == NM_OK);

    // Invalid alignments must fail cleanly
    CHECK(nm_game_alloc(64, 8, 0, &ptr) == NM_ERR_INVALID_ARG);
    CHECK(nm_game_alloc(64, 24, 0, &ptr) == NM_ERR_INVALID_ARG);

    CHECK(nm_shutdown() == NM_OK);
    ++g_tests_passed;
    return 0;
}

int test_frame_queue_concurrency_and_isolation() {
    CHECK(nm_initialize_for_build(NM_BUILD_SYNTHETIC, 0x10000000ull, 0x10000u, nullptr) == NM_OK);

    std::atomic<int> work_count{0};
    auto inc_work = [](void* ud) {
        static_cast<std::atomic<int>*>(ud)->fetch_add(1, std::memory_order_relaxed);
    };
    auto throwing_work = [](void*) {
        throw std::runtime_error("faulty mod callback");
    };

    // Enqueue concurrently from 4 worker threads
    std::vector<std::thread> threads;
    for (int t = 0; t < 4; ++t) {
        threads.emplace_back([&]() {
            for (int i = 0; i < 50; ++i) {
                (void)nm_enqueue_main_thread(inc_work, &work_count);
            }
        });
    }
    for (auto& th : threads) th.join();
    CHECK(nm_enqueue_main_thread(throwing_work, nullptr) == NM_OK);

    float observed_dt = 0.0f;
    uint64_t frame_tok = 0;
    auto on_frame = [](float dt, void* ud) {
        *static_cast<float*>(ud) += dt;
    };
    CHECK(nm_register_frame_callback(on_frame, &observed_dt, &frame_tok) == NM_OK);

    // Drain in two batches; throwing work must not stop frame callback
    CHECK(nm_drain_main_thread_queue(0.016f, 100) == NM_OK);
    CHECK(work_count.load() == 100);
    CHECK(nm_drain_main_thread_queue(0.016f, 0) == NM_OK);
    CHECK(work_count.load() == 200);
    CHECK(std::fabs(observed_dt - 0.032f) < 1e-5f);

    CHECK(nm_unregister_frame_callback(frame_tok) == NM_OK);
    CHECK(nm_shutdown() == NM_OK);
    ++g_tests_passed;
    return 0;
}

int test_file_overlay_and_channels() {
    CHECK(nm_initialize_for_build(NM_BUILD_SYNTHETIC, 0x10000000ull, 0x10000u, nullptr) == NM_OK);

    // File overlay registration + path normalization (UTF-16LE BOM payload stays zero-copy,
    // plain UTF-8 .ui payload auto-transcodes to UTF-16LE+BOM+CRLF with matching has_file_overlay size).
    static uint8_t kUiPayload[] = {0xFF, 0xFE, 'U', 0x00, 'I', 0x00, '!', 0x00};
    static uint8_t kUtf8UiPayload[] = {'b', 'e', 'g', 'i', 'n', '\n', 'e', 'n', 'd', '\n'};
    static uint8_t kBomlessUtf16Sm[] = {'v', 0x00, 'e', 0x00, 'r', 0x00};
    uint64_t ov_tok = 0;
    auto ov_cb = [](const char* path, uint8_t** out_data, size_t* out_size, void*) -> nm_status_t {
        if (std::strcmp(path, "metadata/ui/mods/garden/stockpile.ui") == 0) {
            *out_data = kUiPayload;
            *out_size = sizeof(kUiPayload);
            return NM_OK;
        }
        if (std::strcmp(path, "metadata/ui/mods/maxwell/maxwellpanel.ui") == 0) {
            *out_data = kUtf8UiPayload;
            *out_size = sizeof(kUtf8UiPayload);
            return NM_OK;
        }
        if (std::strcmp(path, "art/models/mods/maxwell/maxwell.sm") == 0) {
            *out_data = kBomlessUtf16Sm;
            *out_size = sizeof(kBomlessUtf16Sm);
            return NM_OK;
        }
        return NM_ERR_SYMBOL_NOT_FOUND;
    };
    CHECK(nm_register_file_overlay(ov_cb, nullptr, nullptr, &ov_tok) == NM_OK);

    uint8_t* data = nullptr;
    size_t sz = 0;
    CHECK(nm_resolve_file_overlay("Metadata\\UI\\Mods\\Garden\\Stockpile.ui", &data, &sz) == NM_OK);
    CHECK(data == kUiPayload && sz == sizeof(kUiPayload));

    // UTF-8 .ui -> UTF-16LE + BOM + CRLF ("begin\r\nend\r\n" = 2 + 12*2 = 26 bytes)
    size_t transcoded_has_sz = 0;
    CHECK(nm_has_file_overlay("Metadata/UI/Mods/Maxwell/MaxwellPanel.ui", &transcoded_has_sz) == NM_OK);
    CHECK(transcoded_has_sz == 26u);
    CHECK(nm_resolve_file_overlay("Metadata\\UI\\Mods\\Maxwell\\MaxwellPanel.ui", &data, &sz) == NM_OK);
    CHECK(sz == transcoded_has_sz && data != nullptr && data[0] == 0xFFu && data[1] == 0xFEu);
    CHECK(data[2] == 'b' && data[3] == 0x00u && data[12] == '\r' && data[13] == 0x00u && data[14] == '\n' && data[15] == 0x00u);
    CHECK(nm_free_file_overlay_buffer(data) == NM_OK);

    // BOM-less UTF-16LE .sm (already contains NUL bytes) must pass through untouched!
    CHECK(nm_resolve_file_overlay("Art/Models/Mods/Maxwell/maxwell.sm", &data, &sz) == NM_OK);
    CHECK(data == kBomlessUtf16Sm && sz == sizeof(kBomlessUtf16Sm));

    CHECK(nm_resolve_file_overlay("Metadata/UI/Missing.ui", &data, &sz) == NM_ERR_SYMBOL_NOT_FOUND);
    CHECK(nm_unregister_file_overlay(ov_tok) == NM_OK);

    // Host-ruleset channel (0x2c9 RX, 0x2ca TX)
    struct TxCapture { uint16_t opcode = 0; uint32_t ruleset = 0; std::vector<uint8_t> bytes; } tx_cap;
    auto tx_fn = [](uint16_t opcode, uint32_t ruleset_id, const uint8_t* payload, uint32_t len, void* ud) -> nm_status_t {
        auto* c = static_cast<TxCapture*>(ud);
        c->opcode = opcode;
        c->ruleset = ruleset_id;
        c->bytes.assign(payload, payload + len);
        return NM_OK;
    };
    CHECK(nm_set_packet_tx_override(tx_fn, &tx_cap) == NM_OK);

    std::vector<uint8_t> rx_bytes;
    uint64_t rs_tok = 0;
    auto rs_cb = [](uint32_t id, const uint8_t* p, uint32_t len, void* ud) {
        if (id == 0xA0u) static_cast<std::vector<uint8_t>*>(ud)->assign(p, p + len);
    };
    CHECK(nm_register_ruleset_handler(0xA0u, rs_cb, &rx_bytes, &rs_tok) == NM_OK);

    // Reject out-of-range ruleset id (>= 0xAF) and payload (> 0xFFFF)
    uint64_t bad_tok = 0;
    CHECK(nm_register_ruleset_handler(NM_RULESET_SLOT_COUNT, rs_cb, &rx_bytes, &bad_tok) == NM_ERR_INVALID_ARG);
    const uint8_t msg[] = {0xDE, 0xAD, 0xBE, 0xEF};
    CHECK(nm_send_ruleset_packet(0xA0u, msg, NM_RULESET_MAX_PAYLOAD + 1u) == NM_ERR_PAYLOAD_TOO_LARGE);

    // Valid ruleset RX and TX
    CHECK(nm_dispatch_ruleset_rx(0xA0u, msg, sizeof(msg)) == NM_OK);
    CHECK(rx_bytes.size() == 4 && rx_bytes[0] == 0xDE && rx_bytes[3] == 0xEF);
    // Unregistered ruleset id silently drops (10-verification.md §3)
    CHECK(nm_dispatch_ruleset_rx(0x99u, msg, sizeof(msg)) == NM_OK);

    CHECK(nm_send_ruleset_packet(0xA0u, msg, sizeof(msg)) == NM_OK);
    CHECK(tx_cap.opcode == NM_OPCODE_RULESET_C2S);
    CHECK(tx_cap.ruleset == 0xA0u);
    CHECK(tx_cap.bytes.size() == 4);

    // Chat fallback & suppression return value:
    // Callback returns 1 to suppress from game chat panel, 0 to pass through.
    std::string chat_captured;
    uint64_t chat_tok = 0;
    auto chat_cb = [](const char*, const char* text, void* ud) -> int32_t {
        *static_cast<std::string*>(ud) = text;
        if (std::strncmp(text, "##NM ", 5) == 0 || std::strcmp(text, "/suppress_me") == 0) {
            return 1;
        }
        return 0;
    };
    CHECK(nm_register_chat_channel_handler(chat_cb, &chat_captured, &chat_tok) == NM_OK);
    CHECK(nm_dispatch_chat_rx("System", "Hello Wraeclast") == 0);
    CHECK(chat_captured == "Hello Wraeclast");
    CHECK(nm_dispatch_chat_rx("System", "/suppress_me") == 1);
    CHECK(chat_captured == "/suppress_me");
    CHECK(nm_dispatch_chat_rx("System", "##NM open garden") == 1);
    CHECK(chat_captured == "##NM open garden");

    CHECK(nm_send_chat_line("/nm plant WildSeed") == NM_OK);
    CHECK(tx_cap.opcode == NM_OPCODE_CHAT_C2S);

    // Raw packet TX & 0x02FC+ opcode handler group + nm_stream_read
    const uint8_t raw_tx[] = {0x11, 0x22, 0x33};
    CHECK(nm_send_raw_packet(0x02FDu, raw_tx, sizeof(raw_tx)) == NM_OK);
    CHECK(tx_cap.opcode == 0x02FDu);
    CHECK(tx_cap.bytes == std::vector<uint8_t>({0x11, 0x22, 0x33}));

    struct OpcodeCapture {
        uint16_t             opcode = 0;
        std::vector<uint8_t> streamed;
        std::vector<uint8_t> direct;
    } op_cap;
    auto op_cb = [](uint16_t opcode, void* stream, const uint8_t* payload, uint32_t len, void* ud) -> int32_t {
        auto* c = static_cast<OpcodeCapture*>(ud);
        c->opcode = opcode;
        c->direct.assign(payload, payload + len);
        c->streamed.resize(len);
        if (len > 0 && nm_stream_read(stream, c->streamed.data(), len) != NM_OK) {
            return 0;
        }
        return 1;
    };
    uint64_t op_tok = 0;
    CHECK(nm_register_opcode_handler_group(0x02FCu, 0x0310u, op_cb, &op_cap, &op_tok) == NM_OK);
    const uint8_t op_rx[] = {0xCA, 0xFE, 0xBA, 0xBE};
    int32_t op_handled = 0;
    CHECK(nm_dispatch_opcode_rx(0x0300u, op_rx, sizeof(op_rx), &op_handled) == NM_OK);
    CHECK(op_handled == 1 && op_cap.opcode == 0x0300u);
    CHECK(op_cap.direct == std::vector<uint8_t>({0xCA, 0xFE, 0xBA, 0xBE}));
    CHECK(op_cap.streamed == std::vector<uint8_t>({0xCA, 0xFE, 0xBA, 0xBE}));
    op_handled = 1;
    CHECK(nm_dispatch_opcode_rx(0x0200u, op_rx, sizeof(op_rx), &op_handled) == NM_OK);
    CHECK(op_handled == 0);
    CHECK(nm_unregister_opcode_handler_group(op_tok) == NM_OK);

    // Non-Windows bootstrapper cleanly reports platform unsupported rather than crashing
#if !defined(_WIN32)
    CHECK(nm_bootstrap_managed_host("hostfxr.dll", "host.runtimeconfig.json", "LocalExile.Client.Host.dll")
          == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);
#endif

    CHECK(nm_unregister_ruleset_handler(rs_tok) == NM_OK);
    CHECK(nm_unregister_chat_channel_handler(chat_tok) == NM_OK);
    CHECK(nm_shutdown() == NM_OK);
    ++g_tests_passed;
    return 0;
}

int test_prologue_length_and_sha256() {
    // Verify whole-instruction stolen lengths (>= 14B, zero split instructions) on ALL 9 real 3.29.3d hook prologues!
    // 1. frame.state_stack_pass_a (0x1528f40): 5 + 5 + 5 = 15 bytes
    const uint8_t pass_a[] = {
        0x48, 0x89, 0x5C, 0x24, 0x10, 0x48, 0x89, 0x6C, 0x24, 0x18,
        0x48, 0x89, 0x74, 0x24, 0x20, 0x57, 0x48, 0x83, 0xEC, 0x50,
    };
    CHECK(nativemods::HookEngine::compute_stolen_length(pass_a, 14) == 15);

    // 2. files.exists_query (0x0e55340): 5 + 1 + 7 + 3 = 16 bytes
    const uint8_t fs_exists[] = {
        0x48, 0x89, 0x5C, 0x24, 0x08, 0x57, 0x48, 0x81, 0xEC, 0xA0, 0x00, 0x00, 0x00,
        0x48, 0x8B, 0xF9, 0x0F, 0x57, 0xC0,
    };
    CHECK(nativemods::HookEngine::compute_stolen_length(fs_exists, 14) == 16);

    // 3. files.size_query (0x0e557f0): 5 + 5 + 1 + 1 + 2 = 14 bytes
    const uint8_t fs_size[] = {
        0x48, 0x89, 0x5C, 0x24, 0x10, 0x48, 0x89, 0x74, 0x24, 0x18,
        0x55, 0x57, 0x41, 0x56, 0x48, 0x8D, 0x6C, 0x24, 0xB9,
    };
    CHECK(nativemods::HookEngine::compute_stolen_length(fs_size, 14) == 14);

    // 4. files.read_variant_2 (0x0e55aa0): 5 + 5 + 1 + 1 + 2 = 14 bytes
    const uint8_t fs_range[] = {
        0x48, 0x89, 0x5C, 0x24, 0x10, 0x48, 0x89, 0x74, 0x24, 0x18,
        0x55, 0x57, 0x41, 0x54, 0x41, 0x56, 0x41, 0x57,
    };
    CHECK(nativemods::HookEngine::compute_stolen_length(fs_range, 14) == 14);

    // 5. files.read_whole_file (0x0e564d0) & read_variant_5 (0x0e56af0): 5 + 5 + 1 + 1 + 1 + 2 = 15 bytes
    const uint8_t fs_whole[] = {
        0x48, 0x89, 0x5C, 0x24, 0x18, 0x48, 0x89, 0x54, 0x24, 0x10,
        0x55, 0x56, 0x57, 0x41, 0x56, 0x41, 0x57,
    };
    CHECK(nativemods::HookEngine::compute_stolen_length(fs_whole, 14) == 15);

    // 6. ui.control_load (0x18b08d0): 5 + 5 + 1 + 8 = 19 bytes
    const uint8_t ui_load[] = {
        0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x7C, 0x24, 0x10,
        0x55, 0x48, 0x8D, 0xAC, 0x24, 0xB0, 0xFE, 0xFF, 0xFF, 0x48, 0x81, 0xEC, 0x50, 0x02, 0x00, 0x00,
    };
    CHECK(nativemods::HookEngine::compute_stolen_length(ui_load, 14) == 19);

    // 7. network.chat_panel_listener (0x017a270): 5 + 5 + 1 + 1 + 2 = 14 bytes
    const uint8_t chat_rx[] = {
        0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x74, 0x24, 0x18,
        0x55, 0x57, 0x41, 0x56, 0x48, 0x8D, 0x6C, 0x24, 0xB0,
    };
    CHECK(nativemods::HookEngine::compute_stolen_length(chat_rx, 14) == 14);

    // 8. rulesets.s2c_0x2c9_filter (0x15a6b50): 5 + 1 + 1 + 1 + 2 + 2 + 2 = 14 bytes (stops cleanly before 41 57 push r15)
    const uint8_t rs_filter[] = {
        0x48, 0x89, 0x5C, 0x24, 0x08, 0x55, 0x56, 0x57,
        0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57,
        0x48, 0x8D, 0x6C, 0x24, 0xD9,
    };
    CHECK(nativemods::HookEngine::compute_stolen_length(rs_filter, 14) == 14);

    // Self-contained SHA-256 test vectors (NIST FIPS 180-4)
    CHECK(nativemods::sha256_hex_bytes(nullptr, 0) ==
          "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    const uint8_t abc[] = {'a', 'b', 'c'};
    CHECK(nativemods::sha256_hex_bytes(abc, 3) ==
          "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");

    ++g_tests_passed;
    return 0;
}

int test_disk_overlays_audio_and_wire_encoders() {
    CHECK(nm_initialize_for_build(NM_BUILD_SYNTHETIC, 0x10000000ull, 0x10000u, nullptr) == NM_OK);

    // Create temp overlay directory with binary (.datc64) and text (.ot) files
    namespace fs = std::filesystem;
    const fs::path tmp_root = fs::temp_directory_path() / "nativemods_shim_overlay_test";
    std::error_code ec;
    fs::remove_all(tmp_root, ec);
    fs::create_directories(tmp_root / "Data", ec);
    fs::create_directories(tmp_root / "Metadata" / "Pet" / "Mods" / "Maxwell", ec);

    const std::vector<uint8_t> dat_bytes = {0x04, 0x02, 0x00, 0x00, 0xBB, 0xBB, 0xBB, 0xBB};
    {
        std::ofstream ofs(tmp_root / "Data" / "Pet.datc64", std::ios::binary);
        ofs.write(reinterpret_cast<const char*>(dat_bytes.data()), static_cast<std::streamsize>(dat_bytes.size()));
    }
    const std::string ot_text = "version 2\r\nextends \"Metadata/Pet/Pet\"\r\n";
    {
        std::ofstream ofs(tmp_root / "Metadata" / "Pet" / "Mods" / "Maxwell" / "MaxwellPet.ot", std::ios::binary);
        ofs.write(ot_text.data(), static_cast<std::streamsize>(ot_text.size()));
    }

    uint64_t dir_tok = 0;
    CHECK(nm_register_file_overlay_dir(tmp_root.string().c_str(), &dir_tok) == NM_OK);
    CHECK(dir_tok != 0);

    size_t sz = 0;
    CHECK(nm_has_file_overlay("data\\pet.datc64", &sz) == NM_OK);
    CHECK(sz == dat_bytes.size());
    CHECK(nm_has_file_overlay("Metadata/Pet/Mods/Maxwell/MaxwellPet.ot", &sz) == NM_OK);
    // Plain UTF-8 .ot on disk auto-transcodes to UTF-16LE + BOM + CRLF (2 + len*2 bytes).
    CHECK(sz == 2u + ot_text.size() * 2u);
    CHECK(nm_has_file_overlay("Data/DoesNotExist.datc64", &sz) == NM_ERR_SYMBOL_NOT_FOUND);
    // Reject path traversal
    CHECK(nm_has_file_overlay("../etc/passwd", &sz) == NM_ERR_SYMBOL_NOT_FOUND);

    uint8_t* resolved = nullptr;
    size_t resolved_sz = 0;
    CHECK(nm_resolve_file_overlay("DATA/PET.DATC64", &resolved, &resolved_sz) == NM_OK);
    CHECK(resolved != nullptr && resolved_sz == dat_bytes.size());
    CHECK(std::memcmp(resolved, dat_bytes.data(), dat_bytes.size()) == 0);
    CHECK(nm_free_file_overlay_buffer(resolved) == NM_OK);

    CHECK(nm_resolve_file_overlay("Metadata/Pet/Mods/Maxwell/MaxwellPet.ot", &resolved, &resolved_sz) == NM_OK);
    CHECK(resolved != nullptr && resolved_sz == 2u + ot_text.size() * 2u);
    CHECK(resolved[0] == 0xFFu && resolved[1] == 0xFEu && resolved[2] == 'v' && resolved[3] == 0x00u);
    CHECK(nm_free_file_overlay_buffer(resolved) == NM_OK);

    CHECK(nm_unregister_file_overlay_dir(dir_tok) == NM_OK);
    CHECK(nm_has_file_overlay("Data/Pet.datc64", &sz) == NM_ERR_SYMBOL_NOT_FOUND);
    fs::remove_all(tmp_root, ec);

    // Live audio C ABI lifecycle (looping OGG + volume + is_playing + stop + event)
    int32_t handle = 0;
    CHECK(nm_audio_play_ogg("Audio/Mods/Maxwell/stock_market.ogg", 0.75f, 1, &handle) == NM_OK);
    CHECK(handle > 0);
    int32_t playing = 0;
    CHECK(nm_audio_is_playing(handle, &playing) == NM_OK);
    CHECK(playing == 1);
    CHECK(nm_audio_set_volume(handle, 0.4f) == NM_OK);
    CHECK(nm_audio_stop(handle) == NM_OK);
    CHECK(nm_audio_is_playing(handle, &playing) == NM_OK);
    CHECK(playing == 0);

    int32_t evt_handle = 0;
    CHECK(nm_audio_play_event("event:/Mods/Maxwell/Summon", 0.9f, &evt_handle) == NM_OK);
    CHECK(evt_handle > 0);
    CHECK(nm_audio_stop(evt_handle) == NM_OK);

    // Verify exact PoE 3.29 C->S wire encoders (0x0015 chat & 0x02CA ruleset compact-int)
    const auto chat_wire = nativemods::LiveEngineHooks::encode_c2s_chat_packet("/maxwell");
    CHECK(chat_wire.size() == 4 + 8 + 1);
    CHECK(chat_wire[0] == 0x00 && chat_wire[1] == 0x15);
    CHECK(chat_wire[2] == 0x00 && chat_wire[3] == 0x08);
    CHECK(chat_wire[4] == '/' && chat_wire[12] == 0x00);

    const std::vector<uint8_t> small_payload = {0x01, 0x02, 0x03};
    const auto rs_small = nativemods::LiveEngineHooks::encode_c2s_ruleset_packet(0x9E, small_payload.data(), 3);
    CHECK(rs_small.size() == 7);
    CHECK(rs_small[0] == 0x02 && rs_small[1] == 0xCA && rs_small[2] == 0x9E && rs_small[3] == 0x03);

    const std::vector<uint8_t> med_payload(200, 0xAA);
    const auto rs_med = nativemods::LiveEngineHooks::encode_c2s_ruleset_packet(0xA2, med_payload.data(), 200);
    CHECK(rs_med.size() == 5 + 200);
    CHECK(rs_med[0] == 0x02 && rs_med[1] == 0xCA && rs_med[2] == 0xA2);
    CHECK(rs_med[3] == 0x80 && rs_med[4] == 200);

    const std::vector<uint8_t> big_payload(0x4005, 0x55);
    const auto rs_big = nativemods::LiveEngineHooks::encode_c2s_ruleset_packet(0xA2, big_payload.data(), 0x4005);
    CHECK(rs_big.size() == 6 + 0x4005);
    CHECK(rs_big[3] == 0xC0 && rs_big[4] == 0x40 && rs_big[5] == 0x05);

    CHECK(nm_shutdown() == NM_OK);
    ++g_tests_passed;
    return 0;
}

int test_overlay_hash_prefilter_and_connection_token() {
    // 1. Verify exact UTF-8 <-> UTF-16LE case-insensitive slash-normalized hash parity
    const char* u8_a = "Data/MTXTypes.datc64";
    const char* u8_b = "/data\\mtxtypes.datc64";
    const wchar_t* w_a = L"Data\\MTXTypes.datc64";
    const wchar_t* w_b = L"/DATA/MtxTypes.datc64";
    const uint64_t h_u8_a = nativemods::OverlayAndChannels::hash_virtual_path_ci_utf8(u8_a, std::strlen(u8_a));
    const uint64_t h_u8_b = nativemods::OverlayAndChannels::hash_virtual_path_ci_utf8(u8_b, std::strlen(u8_b));
    const uint64_t h_w_a  = nativemods::OverlayAndChannels::hash_virtual_path_ci_wide(w_a, std::wcslen(w_a));
    const uint64_t h_w_b  = nativemods::OverlayAndChannels::hash_virtual_path_ci_wide(w_b, std::wcslen(w_b));
    CHECK(h_u8_a != 0);
    CHECK(h_u8_a == h_u8_b);
    CHECK(h_u8_a == h_w_a);
    CHECK(h_u8_a == h_w_b);

    // 2. Verify nm_set_file_overlay_known_paths prevents callback invocations for all non-matching stock paths!
    CHECK(nm_initialize_for_build(NM_BUILD_SYNTHETIC, 0x10000000ull, 0x10000u, nullptr) == NM_OK);

    static uint8_t kPanelBytes[] = {0xFF, 0xFE, 'O', 0x00, 'K', 0x00};
    struct FilterProbe {
        int calls = 0;
    } probe;

    auto filtered_cb = [](const char* path, uint8_t** out_data, size_t* out_size, void* ud) -> nm_status_t {
        auto* p = static_cast<FilterProbe*>(ud);
        ++p->calls;
        if (std::strcmp(path, "metadata/ui/mods/maxwell/maxwellpanel.ui") == 0) {
            *out_data = kPanelBytes;
            *out_size = sizeof(kPanelBytes);
            return NM_OK;
        }
        return NM_ERR_SYMBOL_NOT_FOUND;
    };

    uint64_t tok = 0;
    CHECK(nm_register_file_overlay(filtered_cb, nullptr, &probe, &tok) == NM_OK);
    const char* known[] = {"Metadata\\UI\\Mods\\Maxwell\\MaxwellPanel.ui"};
    CHECK(nm_set_file_overlay_known_paths(tok, known, 1) == NM_OK);

    // Query 2000 stock game paths -> callback MUST NOT be invoked even once!
    size_t sz = 0;
    for (int i = 0; i < 2000; ++i) {
        std::string stock = "Art/Textures/Stock_" + std::to_string(i) + ".dds";
        CHECK(nm_has_file_overlay(stock.c_str(), &sz) == NM_ERR_SYMBOL_NOT_FOUND);
    }
    CHECK(probe.calls == 0);

    // Query the registered overlay path (with different casing/slashes) -> callback IS invoked!
    CHECK(nm_has_file_overlay("METADATA/ui/mods/maxwell/MAXWELLPANEL.UI", &sz) == NM_OK);
    CHECK(sz == sizeof(kPanelBytes));
    CHECK(probe.calls == 1);

    // Empty known path list -> callback bypassed completely!
    CHECK(nm_set_file_overlay_known_paths(tok, nullptr, 0) == NM_OK);
    CHECK(nm_has_file_overlay("Metadata/UI/Mods/Maxwell/MaxwellPanel.ui", &sz) == NM_ERR_SYMBOL_NOT_FOUND);
    CHECK(probe.calls == 1);

    // 3. Verify nm_get_active_connection_token returns 0 cleanly on synthetic build
    uint64_t conn_tok = 0xDEADBEEFull;
    CHECK(nm_get_active_connection_token(&conn_tok) == NM_OK);
    CHECK(conn_tok == 0ull);

    // 4. Verify native UI ABI argument validation & safe non-live status codes
    void* ui_ptr = nullptr;
    uint64_t click_tok = 0;
    auto dummy_click = [](void*, void*) {};
    CHECK(nm_ui_get_root(nullptr) == NM_ERR_INVALID_ARG);
    CHECK(nm_ui_create_window(nullptr, &ui_ptr) == NM_ERR_INVALID_ARG);
    CHECK(nm_ui_create_window("Metadata/UI/Mods/Siphon/SiphonPicker.ui", &ui_ptr) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);
    CHECK(nm_ui_find_child(nullptr, "affix_row_0", &ui_ptr) == NM_ERR_INVALID_ARG);
    CHECK(nm_ui_set_visible(nullptr, 0) == NM_ERR_INVALID_ARG);
    CHECK(nm_ui_set_text(nullptr, "test", 1) == NM_ERR_INVALID_ARG);
    CHECK(nm_ui_subscribe_click(nullptr, dummy_click, nullptr, &click_tok) == NM_ERR_INVALID_ARG);
    CHECK(nm_ui_subscribe_click(reinterpret_cast<void*>(0x1234), dummy_click, nullptr, &click_tok)
          == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);

    CHECK(nm_unregister_file_overlay(tok) == NM_OK);
    CHECK(nm_shutdown() == NM_OK);
    ++g_tests_passed;
    return 0;
}

int test_x64_decoder_and_rip_relocation() {
    using nativemods::HookEngine;

    // 1. Verify full x64 instruction length decoder coverage
    const uint8_t rex_mov[]   = {0x48, 0x89, 0x5C, 0x24, 0x10};                 // 5B
    const uint8_t vex2_zero[] = {0xC5, 0xF8, 0x77};                             // vzeroupper (3B)
    const uint8_t vex2_mov[]  = {0xC5, 0xF8, 0x28, 0xC1};                       // vmovaps xmm0, xmm1 (4B)
    const uint8_t vex3_op[]   = {0xC4, 0xE2, 0x7D, 0x18, 0x05, 0, 0, 0, 0};     // vbroadcastss ymm0, [rip+0] (9B)
    const uint8_t vex3_3a[]   = {0xC4, 0xE3, 0x79, 0x0F, 0xC1, 0x04};           // vpalignr (6B)
    const uint8_t evex_mov[]  = {0x62, 0xF1, 0x7C, 0x48, 0x28, 0xC1};           // vmovaps zmm0, zmm1 (6B)
    const uint8_t op_0f38[]   = {0x66, 0x0F, 0x38, 0x00, 0xC1};                 // pshufb (5B)
    const uint8_t op_0f3a[]   = {0x66, 0x0F, 0x3A, 0x0F, 0xC1, 0x04};           // palignr imm8 (6B)
    const uint8_t f6_test[]   = {0xF6, 0x05, 0x00, 0x00, 0x00, 0x00, 0x01};     // test byte [rip+0], 1 (7B)
    const uint8_t f6_not[]    = {0xF6, 0xD0};                                   // not al (2B)
    const uint8_t f7_test[]   = {0x48, 0xF7, 0xC1, 0x01, 0x00, 0x00, 0x00};     // test rcx, 1 (7B)
    const uint8_t f7_neg[]    = {0x48, 0xF7, 0xD8};                             // neg rax (3B)
    const uint8_t call_rel[]  = {0xE8, 0x10, 0x00, 0x00, 0x00};                 // call +16 (5B)
    const uint8_t jcc_near[]  = {0x0F, 0x84, 0x20, 0x00, 0x00, 0x00};           // je near (6B)
    const uint8_t jcc_short[] = {0x75, 0x05};                                   // jne short (2B)
    const uint8_t jmp_short[] = {0xEB, 0xFE};                                   // jmp short (2B)
    const uint8_t mov_imm64[] = {0x48, 0xB8, 1, 2, 3, 4, 5, 6, 7, 8};           // movabs rax, imm64 (10B)
    const uint8_t invalid64[] = {0x06, 0x90, 0x90};                             // push es (invalid in x64)

    CHECK(HookEngine::decode_x64_insn(rex_mov, sizeof(rex_mov)).length == 5);
    CHECK(HookEngine::decode_x64_insn(vex2_zero, sizeof(vex2_zero)).length == 3);
    CHECK(HookEngine::decode_x64_insn(vex2_mov, sizeof(vex2_mov)).length == 4);
    const auto d_vex3 = HookEngine::decode_x64_insn(vex3_op, sizeof(vex3_op));
    CHECK(d_vex3.length == 9 && d_vex3.is_rip_relative && d_vex3.disp_offset == 5);
    CHECK(HookEngine::decode_x64_insn(vex3_3a, sizeof(vex3_3a)).length == 6);
    CHECK(HookEngine::decode_x64_insn(evex_mov, sizeof(evex_mov)).length == 6);
    CHECK(HookEngine::decode_x64_insn(op_0f38, sizeof(op_0f38)).length == 5);
    CHECK(HookEngine::decode_x64_insn(op_0f3a, sizeof(op_0f3a)).length == 6);
    const auto d_f6 = HookEngine::decode_x64_insn(f6_test, sizeof(f6_test));
    CHECK(d_f6.length == 7 && d_f6.is_rip_relative && d_f6.disp_offset == 2);
    CHECK(HookEngine::decode_x64_insn(f6_not, sizeof(f6_not)).length == 2);
    CHECK(HookEngine::decode_x64_insn(f7_test, sizeof(f7_test)).length == 7);
    CHECK(HookEngine::decode_x64_insn(f7_neg, sizeof(f7_neg)).length == 3);
    CHECK(HookEngine::decode_x64_insn(call_rel, sizeof(call_rel)).is_rel_branch);
    CHECK(HookEngine::decode_x64_insn(jcc_near, sizeof(jcc_near)).length == 6);
    CHECK(HookEngine::decode_x64_insn(jcc_short, sizeof(jcc_short)).length == 2);
    CHECK(HookEngine::decode_x64_insn(jmp_short, sizeof(jmp_short)).length == 2);
    CHECK(HookEngine::decode_x64_insn(mov_imm64, sizeof(mov_imm64)).length == 10);
    CHECK(HookEngine::decode_x64_insn(invalid64, sizeof(invalid64)).length == 0);
    // compute_stolen_length MUST return 0 on unknown/invalid opcodes rather than splitting!
    CHECK(HookEngine::compute_stolen_length(invalid64, 14) == 0);

    // 2. Verify RIP-relative relocation unit logic (disp32, E8, E9, 0F 8x, short 7x/EB promotion)
    const uint8_t sample_stolen[] = {
        0x8B, 0x05, 0x10, 0x00, 0x00, 0x00, // mov eax, [rip+0x10] -> target = orig + 6 + 0x10 = orig + 0x16
        0x74, 0x03,                         // je +3 (internal -> off 8+3=11)
        0x83, 0xC0, 0x01,                   // add eax, 1 (3B -> off 8..11)
        0xEB, 0x20,                         // jmp short +0x20 -> target = orig + 0x0D + 0x20 = orig + 0x2D
        0xE8, 0x40, 0x00, 0x00, 0x00,       // call +0x40 -> target = orig + 0x12 + 0x40 = orig + 0x52
    };
    const uint64_t orig_base = 0x100000000ull;
    const uint64_t tramp_base = 0x100020000ull;
    std::vector<uint8_t> relocated;
    CHECK(HookEngine::relocate_instructions(orig_base, sample_stolen, sizeof(sample_stolen), tramp_base, relocated));
    // Expected layout:
    //   0..5:   8B 05 <disp32 = (orig_base+0x16) - (tramp_base+6)> (6B)
    //   6..11:  0F 84 <rel32 = +3> (promoted je skipping 3B add eax, 1!) (6B)
    //   12..14: 83 C0 01 (3B)
    //   15..19: E9 <rel32 = (orig_base+0x2D) - (tramp_base+20)> (promoted jmp!) (5B)
    //   20..24: E8 <rel32 = (orig_base+0x52) - (tramp_base+25)> (5B)
    //   25..38: FF 25 00 00 00 00 <u64 (orig_base+18)> (14B)
    CHECK(relocated.size() == 39);
    int32_t d_rip = 0, d_je = 0, d_jmp = 0, d_call = 0;
    std::memcpy(&d_rip, &relocated[2], 4);
    std::memcpy(&d_je, &relocated[8], 4);
    std::memcpy(&d_jmp, &relocated[16], 4);
    std::memcpy(&d_call, &relocated[21], 4);
    CHECK(static_cast<int64_t>(tramp_base + 6) + d_rip == static_cast<int64_t>(orig_base + 0x16));
    CHECK(relocated[6] == 0x0Fu && relocated[7] == 0x84u && d_je == 3);
    CHECK(relocated[15] == 0xE9u && static_cast<int64_t>(tramp_base + 20) + d_jmp == static_cast<int64_t>(orig_base + 0x2D));
    CHECK(relocated[20] == 0xE8u && static_cast<int64_t>(tramp_base + 25) + d_call == static_cast<int64_t>(orig_base + 0x52));

    // 3. Live execution of a hooked function whose prologue contains [rip+disp32] + short branch + call!
#if !defined(_WIN32) && (defined(__x86_64__) || defined(_M_X64))
    CHECK(nm_initialize_for_build(NM_BUILD_SYNTHETIC, 0x10000000ull, 0x10000u, nullptr) == NM_OK);
    void* exec_page = HookEngine::alloc_exec_page(4096);
    CHECK(exec_page != nullptr);
    auto* mem = static_cast<uint8_t*>(exec_page);
    // Data word at mem + 0x200 = 100
    const int32_t data_val = 100;
    std::memcpy(mem + 0x200, &data_val, 4);
    // Helper at mem + 0x100: eax = edi * 2; ret
    mem[0x100] = 0x8D; mem[0x101] = 0x04; mem[0x102] = 0x3F; // lea eax, [rdi+rdi]
    mem[0x103] = 0xC3;
    // Target function at mem + 0x00:
    //   0:  8B 05 <0x200 - 6>  (mov eax, [rip + data_val])  (6B)
    //   6:  85 FF              (test edi, edi)              (2B)
    //   8:  74 05              (je +5 -> +15)               (2B)
    //   10: E8 <0x100 - 15>    (call helper)                (5B -> 15B stolen!)
    //   15: 03 C7              (add eax, edi)               (2B)
    //   17: C3                 (ret)                        (1B)
    const int32_t disp_data = 0x200 - 6;
    const int32_t disp_call = 0x100 - 15;
    mem[0] = 0x8B; mem[1] = 0x05; std::memcpy(mem + 2, &disp_data, 4);
    mem[6] = 0x85; mem[7] = 0xFF;
    mem[8] = 0x74; mem[9] = 0x05;
    mem[10] = 0xE8; std::memcpy(mem + 11, &disp_call, 4);
    mem[15] = 0x03; mem[16] = 0xC7;
    mem[17] = 0xC3;

    using Fn = int(*)(int);
    auto fn = reinterpret_cast<Fn>(mem);
    CHECK(fn(0) == 100); // skips call helper -> 100 + 0
    CHECK(fn(7) == 21);  // calls helper (14) + 7 = 21

    nm_hook_handle_t h = NM_HOOK_INVALID;
    int pre_seen = 0;
    auto pre_cb = [](nm_hook_context_t* ctx, void* ud) {
        ++(*static_cast<int*>(ud));
        if (ctx->args[0] == 5) ctx->args[0] = 7;
    };
    CHECK(nm_install_inline_chain_hook(reinterpret_cast<uint64_t>(mem), nullptr, NM_HOOK_PHASE_PRE, 0, pre_cb, &pre_seen, &h) == NM_OK);
    CHECK(fn(0) == 100);
    CHECK(fn(5) == 21);
    CHECK(pre_seen == 2);
    CHECK(nm_remove_hook(h) == NM_OK);
    CHECK(fn(5) == 15);
    HookEngine::free_exec_page(exec_page, 4096);
    CHECK(nm_shutdown() == NM_OK);
#elif defined(_WIN32) && (defined(__x86_64__) || defined(_M_X64))
    CHECK(nm_initialize_for_build(NM_BUILD_SYNTHETIC, 0x10000000ull, 0x10000u, nullptr) == NM_OK);
    void* exec_page = HookEngine::alloc_exec_page(4096);
    CHECK(exec_page != nullptr);
    auto* mem = static_cast<uint8_t*>(exec_page);
    const int32_t data_val = 100;
    std::memcpy(mem + 0x200, &data_val, 4);
    // Helper at mem + 0x100: eax = ecx * 2; ret
    mem[0x100] = 0x8D; mem[0x101] = 0x04; mem[0x102] = 0x09; // lea eax, [rcx+rcx]
    mem[0x103] = 0xC3;
    // Target function (Win64 arg0 = ecx):
    //   0:  8B 05 <0x200 - 6>  (mov eax, [rip + data_val])  (6B)
    //   6:  85 C9              (test ecx, ecx)              (2B)
    //   8:  74 05              (je +5 -> +15)               (2B)
    //   10: E8 <0x100 - 15>    (call helper)                (5B -> 15B stolen!)
    //   15: 03 C1              (add eax, ecx)               (2B)
    //   17: C3                 (ret)                        (1B)
    const int32_t disp_data = 0x200 - 6;
    const int32_t disp_call = 0x100 - 15;
    mem[0] = 0x8B; mem[1] = 0x05; std::memcpy(mem + 2, &disp_data, 4);
    mem[6] = 0x85; mem[7] = 0xC9;
    mem[8] = 0x74; mem[9] = 0x05;
    mem[10] = 0xE8; std::memcpy(mem + 11, &disp_call, 4);
    mem[15] = 0x03; mem[16] = 0xC1;
    mem[17] = 0xC3;

    using Fn = int(NM_CALL*)(int);
    auto fn = reinterpret_cast<Fn>(mem);
    CHECK(fn(0) == 100);
    CHECK(fn(7) == 21);

    nm_hook_handle_t h = NM_HOOK_INVALID;
    int pre_seen = 0;
    auto pre_cb = [](nm_hook_context_t* ctx, void* ud) {
        ++(*static_cast<int*>(ud));
        if (ctx->args[0] == 5) ctx->args[0] = 7;
    };
    CHECK(nm_install_inline_chain_hook(reinterpret_cast<uint64_t>(mem), nullptr, NM_HOOK_PHASE_PRE, 0, pre_cb, &pre_seen, &h) == NM_OK);
    CHECK(fn(0) == 100);
    CHECK(fn(5) == 21);
    CHECK(pre_seen == 2);
    CHECK(nm_remove_hook(h) == NM_OK);
    CHECK(fn(5) == 15);
    HookEngine::free_exec_page(exec_page, 4096);
    CHECK(nm_shutdown() == NM_OK);
#endif

    ++g_tests_passed;
    return 0;
}

namespace {
#if defined(__GNUC__) || defined(__clang__)
#  define NM_NOINLINE __attribute__((noinline))
#else
#  define NM_NOINLINE __declspec(noinline)
#endif

NM_NOINLINE int64_t NM_CALL chain_target_fn(int64_t a, int64_t b, int64_t c, int64_t d, int64_t e, int64_t f, int64_t g, int64_t h) {
    return a + b + c + d + e + f + g + h;
}

NM_NOINLINE double NM_CALL chain_vtable_float_target(int64_t a, double b) {
    return static_cast<double>(a) + b;
}
} // namespace

int test_multimod_pre_post_hook_chains() {
    CHECK(nm_initialize_for_build(NM_BUILD_SYNTHETIC, 0x10000000ull, 0x10000u, nullptr) == NM_OK);

    // 1. Inline pre/post chain: priority ordering, arg rewrite, return mutation, skip_original, independent unhook
    using Chain8Fn = int64_t(NM_CALL*)(int64_t, int64_t, int64_t, int64_t, int64_t, int64_t, int64_t, int64_t);
    volatile Chain8Fn call_target = &chain_target_fn;
    std::vector<int> trace;
    auto pre_low = [](nm_hook_context_t* ctx, void* ud) {
        static_cast<std::vector<int>*>(ud)->push_back(1);
        ctx->args[0] += 10;
    };
    auto pre_high = [](nm_hook_context_t* ctx, void* ud) {
        static_cast<std::vector<int>*>(ud)->push_back(2);
        if (ctx->args[7] == 999) {
            ctx->return_value = 7777;
            ctx->skip_original = 1;
        }
    };
    auto post_one = [](nm_hook_context_t* ctx, void* ud) {
        static_cast<std::vector<int>*>(ud)->push_back(3);
        ctx->return_value += 100;
    };
    auto post_two = [](nm_hook_context_t* ctx, void* ud) {
        static_cast<std::vector<int>*>(ud)->push_back(4);
        ctx->return_value *= 2;
    };

    nm_hook_handle_t h_pre_low = 0, h_pre_high = 0, h_post_1 = 0, h_post_2 = 0;
    const uint64_t target_addr = reinterpret_cast<uint64_t>(&chain_target_fn);
    CHECK(nm_install_inline_chain_hook(target_addr, nullptr, NM_HOOK_PHASE_PRE, 10, pre_high, &trace, &h_pre_high) == NM_OK);
    CHECK(nm_install_inline_chain_hook(target_addr, nullptr, NM_HOOK_PHASE_PRE, -5, pre_low, &trace, &h_pre_low) == NM_OK);
    CHECK(nm_install_inline_chain_hook(target_addr, nullptr, NM_HOOK_PHASE_POST, 1, post_one, &trace, &h_post_1) == NM_OK);
    CHECK(nm_install_inline_chain_hook(target_addr, nullptr, NM_HOOK_PHASE_POST, 2, post_two, &trace, &h_post_2) == NM_OK);

    // Normal call: args[0] += 10 -> sum = (1+10)+2+3+4+5+6+7+8 = 46; post_1 (+100) = 146; post_2 (*2) = 292
    trace.clear();
    CHECK(call_target(1, 2, 3, 4, 5, 6, 7, 8) == 292);
    CHECK((trace == std::vector<int>{1, 2, 3, 4}));

    // Skip original: return = 7777 -> post_1 (+100) = 7877 -> post_2 (*2) = 15754
    trace.clear();
    CHECK(call_target(1, 2, 3, 4, 5, 6, 7, 999) == 15754);
    CHECK((trace == std::vector<int>{1, 2, 3, 4}));

    // Independent unhooking: remove post_one & pre_low; remaining pre_high + post_two stay active!
    CHECK(nm_remove_hook(h_post_1) == NM_OK);
    CHECK(nm_remove_hook(h_pre_low) == NM_OK);
    trace.clear();
    CHECK(call_target(1, 2, 3, 4, 5, 6, 7, 8) == 72); // 36 * 2
    CHECK((trace == std::vector<int>{2, 4}));

    CHECK(nm_remove_hook(h_pre_high) == NM_OK);
    CHECK(nm_remove_hook(h_post_2) == NM_OK);
    CHECK(call_target(1, 2, 3, 4, 5, 6, 7, 8) == 36);

    // 2. Vtable pre/post chain with float return value mutation
    void* vt[2] = {nullptr, reinterpret_cast<void*>(&chain_vtable_float_target)};
    nm_hook_handle_t hv_pre = 0, hv_post = 0;
    auto vt_pre = [](nm_hook_context_t* ctx, void*) {
        ctx->args[0] += 5;
    };
    auto vt_post = [](nm_hook_context_t* ctx, void*) {
        double d = 0.0;
        std::memcpy(&d, &ctx->return_float, sizeof(d));
        d += 0.5;
        std::memcpy(&ctx->return_float, &d, sizeof(d));
    };
    CHECK(nm_install_vtable_chain_hook(vt, 1, NM_HOOK_PHASE_PRE, 0, vt_pre, nullptr, &hv_pre) == NM_OK);
    CHECK(nm_install_vtable_chain_hook(vt, 1, NM_HOOK_PHASE_POST, 0, vt_post, nullptr, &hv_post) == NM_OK);
    using VtFloatFn = double(NM_CALL*)(int64_t, double);
    const double r = reinterpret_cast<VtFloatFn>(vt[1])(10, 2.25);
    CHECK(std::fabs(r - 17.75) < 1e-9);
    CHECK(nm_remove_hook(hv_pre) == NM_OK);
    CHECK(nm_remove_hook(hv_post) == NM_OK);
    CHECK(vt[1] == reinterpret_cast<void*>(&chain_vtable_float_target));

    CHECK(nm_shutdown() == NM_OK);
    ++g_tests_passed;
    return 0;
}

namespace {
NM_NOINLINE double NM_CALL mix_8args_double(int64_t a0, float f1, double d2, int64_t a3, float f4, double d5, int64_t a6, int64_t a7) {
    return static_cast<double>(a0 + a3 + a6 + a7) + static_cast<double>(f1) + d2 + static_cast<double>(f4) + d5;
}

NM_NOINLINE int64_t NM_CALL throw_8args(int64_t, int64_t, int64_t, int64_t, int64_t, int64_t, int64_t, int64_t) {
    throw nativemods::SimulatedMsvcException("8-arg fault");
}

#if defined(_WIN32)
NM_NOINLINE int64_t NM_CALL raise_seh_8args(int64_t, int64_t, int64_t, int64_t, int64_t, int64_t, int64_t, int64_t) {
    RaiseException(NM_MSVC_CXX_EXCEPTION_CODE, 0, 0, nullptr);
    return 0;
}
#endif
} // namespace

int test_guarded_8arg_int_float_calls() {
    nm_native_call_desc_t desc{};
    desc.target_address = reinterpret_cast<uint64_t>(&mix_8args_double);
    desc.arg_count = 8;
    desc.float_arg_mask = (1u << 1) | (1u << 2) | (1u << 4) | (1u << 5);
    desc.return_is_float = 1;

    float f1 = 1.5f, f4 = 2.5f;
    double d2 = 3.25, d5 = 4.75;
    uint32_t f1_bits = 0, f4_bits = 0;
    std::memcpy(&f1_bits, &f1, 4);
    std::memcpy(&f4_bits, &f4, 4);

    desc.args[0] = 10;
    desc.args[1] = f1_bits;
    std::memcpy(&desc.args[2], &d2, 8);
    desc.args[3] = 20;
    desc.args[4] = f4_bits;
    std::memcpy(&desc.args[5], &d5, 8);
    desc.args[6] = 30;
    desc.args[7] = 40;

    nm_exception_info_t ex{};
    CHECK(nm_call_native_guarded(&desc, &ex) == NM_OK);
    double out_d = 0.0;
    std::memcpy(&out_d, &desc.out_float_bits, 8);
    CHECK(std::fabs(out_d - 112.0) < 1e-9);

    // Fault isolation on 8-arg call
    nm_native_call_desc_t bad{};
    bad.target_address = reinterpret_cast<uint64_t>(&throw_8args);
    bad.arg_count = 8;
    CHECK(nm_call_native_guarded(&bad, &ex) == NM_ERR_GAME_EXCEPTION);
    CHECK(ex.exception_code == NM_MSVC_CXX_EXCEPTION_CODE);

#if defined(_WIN32)
    nm_native_call_desc_t bad_seh{};
    bad_seh.target_address = reinterpret_cast<uint64_t>(&raise_seh_8args);
    bad_seh.arg_count = 8;
    CHECK(nm_call_native_guarded(&bad_seh, &ex) == NM_ERR_GAME_EXCEPTION);
    CHECK(ex.exception_code == NM_MSVC_CXX_EXCEPTION_CODE);
#endif

    ++g_tests_passed;
    return 0;
}

int test_utf16_extensions_nobom_and_overlay_reclaim() {
    CHECK(nm_initialize_for_build(NM_BUILD_SYNTHETIC, 0x10000000ull, 0x10000u, nullptr) == NM_OK);

    // Verify all 15 ClientTextEncoding extensions: .otc/.it/.trl -> BOM+CRLF, .sm/.mat/.amd -> NO BOM+CRLF!
    static uint8_t kUtf8Sample[] = {'a', '\n', 'b', '\n'};
    uint64_t tok = 0;
    auto cb = [](const char*, uint8_t** out_data, size_t* out_size, void*) -> nm_status_t {
        *out_data = kUtf8Sample;
        *out_size = sizeof(kUtf8Sample);
        return NM_OK;
    };
    CHECK(nm_register_file_overlay(cb, nullptr, nullptr, &tok) == NM_OK);

    const char* bom_exts[] = {"a.ui", "a.ot", "a.otc", "a.it", "a.itc", "a.ao", "a.aoc", "a.act", "a.epk", "a.ddt", "a.pet", "a.trl"};
    for (const char* p : bom_exts) {
        uint8_t* data = nullptr;
        size_t sz = 0;
        CHECK(nm_resolve_file_overlay(p, &data, &sz) == NM_OK);
        // 2 (BOM) + 6 code units ("a\r\nb\r\n") * 2 = 14 bytes
        CHECK(sz == 14u && data[0] == 0xFFu && data[1] == 0xFEu && data[2] == 'a' && data[3] == 0x00u);
        CHECK(nm_free_file_overlay_buffer(data) == NM_OK);
    }

    const char* nobom_exts[] = {"a.sm", "a.mat", "a.amd"};
    for (const char* p : nobom_exts) {
        uint8_t* data = nullptr;
        size_t sz = 0;
        CHECK(nm_resolve_file_overlay(p, &data, &sz) == NM_OK);
        // NO BOM: 6 code units ("a\r\nb\r\n") * 2 = 12 bytes, starts directly with 'a', 0x00
        CHECK(sz == 12u && data[0] == 'a' && data[1] == 0x00u && data[2] == '\r' && data[3] == 0x00u);
        CHECK(nm_free_file_overlay_buffer(data) == NM_OK);
    }
    CHECK(nm_unregister_file_overlay(tok) == NM_OK);

    // Verify OverlayAndChannels ScopedOverlayIndex safely defers & reclaims retired_indices_
    nativemods::OverlayAndChannels ov;
    uint64_t t1 = 0;
    CHECK(ov.register_file_overlay(cb, nullptr, nullptr, &t1) == NM_OK);
    CHECK(ov.retired_index_count() == 0);
    {
        auto pinned = ov.acquire_index();
        CHECK(static_cast<bool>(pinned));
        for (int i = 0; i < 10; ++i) {
            const char* p = "Data/Test.datc64";
            CHECK(ov.set_file_overlay_known_paths(t1, &p, 1) == NM_OK);
        }
        CHECK(ov.retired_index_count() == 10u);
    }
    CHECK(ov.retired_index_count() == 0u);

    // Clipboard round-trip (mutex-backed stub on Linux, CF_UNICODETEXT on Windows)
    CHECK(nm_clipboard_set_text("LocalExile-0.2.0") == NM_OK);
    size_t clip_len = 0;
    CHECK(nm_clipboard_get_text(nullptr, 0, &clip_len) == NM_OK);
    CHECK(clip_len == std::strlen("LocalExile-0.2.0"));
    char small_buf[4] = {};
    CHECK(nm_clipboard_get_text(small_buf, sizeof(small_buf), &clip_len) == NM_ERR_BUFFER_TOO_SMALL);
    char full_buf[64] = {};
    CHECK(nm_clipboard_get_text(full_buf, sizeof(full_buf), &clip_len) == NM_OK);
    CHECK(std::strcmp(full_buf, "LocalExile-0.2.0") == 0);

    CHECK(nm_shutdown() == NM_OK);
    ++g_tests_passed;
    return 0;
}

namespace {
nm_hook_handle_t   g_self_inline_handle = NM_HOOK_INVALID;
TestAddFn          g_self_inline_tramp = nullptr;
std::atomic<int>   g_self_inline_hits{0};
nm_status_t        g_self_inline_remove_st = NM_ERR_INVALID_ARG;

int self_removing_inline_detour(int x) {
    g_self_inline_hits.fetch_add(1, std::memory_order_relaxed);
    const nm_hook_handle_t h = g_self_inline_handle;
    g_self_inline_handle = NM_HOOK_INVALID;
    g_self_inline_remove_st = nm_remove_hook(h);
    return g_self_inline_tramp(x) + 100;
}

nm_hook_handle_t   g_self_vt_handle = NM_HOOK_INVALID;
TestAddFn          g_self_vt_orig = nullptr;
std::atomic<int>   g_self_vt_hits{0};
nm_status_t        g_self_vt_remove_st = NM_ERR_INVALID_ARG;

int self_removing_vtable_detour(int x) {
    g_self_vt_hits.fetch_add(1, std::memory_order_relaxed);
    const nm_hook_handle_t h = g_self_vt_handle;
    g_self_vt_handle = NM_HOOK_INVALID;
    g_self_vt_remove_st = nm_remove_hook(h);
    return g_self_vt_orig(x) + 50;
}

NM_NOINLINE int64_t NM_CALL vt_stress_target(int64_t x) {
    return x * 3 + 1;
}
} // namespace

int test_relocate_internal_jump_over_2gb() {
    using nativemods::HookEngine;

    // Stolen bytes containing an internal `je` jumping past a 5B external `jmp` (which
    // expands to 14B when tramp_base is >2GB from orig_base), plus a trailing external `jmp short`.
    const uint8_t stolen[] = {
        0x85, 0xFF,                         // 0:  test edi, edi (2B)
        0x74, 0x05,                         // 2:  je +5 -> orig + 9 (2B, internal!)
        0xE9, 0x40, 0x00, 0x00, 0x00,       // 4:  jmp +0x40 -> orig + 0x49 (5B, external -> 14B)
        0x83, 0xC7, 0x01,                   // 9:  add edi, 1 (3B, internal target!)
        0xEB, 0x20,                         // 12: jmp short +0x20 -> orig + 0x34 (2B, external -> 14B)
    };
    const uint64_t orig_base  = 0x100000000ull;
    const uint64_t tramp_base = 0x300000000ull; // +8GB (> 2GB!)

    std::vector<uint8_t> out;
    CHECK(HookEngine::relocate_instructions(orig_base, stolen, sizeof(stolen), tramp_base, out));
    // Expected layout:
    //   0..1:   85 FF (2B)
    //   2..7:   0F 84 <rel32 = 14> (6B) -> target = tramp_base + 8 + 14 = tramp_base + 22
    //   8..21:  FF 25 00 00 00 00 <orig_base + 0x49> (14B)
    //   22..24: 83 C7 01 (3B)  <-- exact internal target!
    //   25..38: FF 25 00 00 00 00 <orig_base + 0x34> (14B)
    //   39..52: FF 25 00 00 00 00 <orig_base + 14> (14B)
    CHECK(out.size() == 53u);
    CHECK(out[2] == 0x0Fu && out[3] == 0x84u);
    int32_t je_disp = 0;
    std::memcpy(&je_disp, &out[4], 4);
    CHECK(je_disp == 14);
    CHECK(out[22] == 0x83u && out[23] == 0xC7u && out[24] == 0x01u);

    // Also verify internal backward `jmp short` + internal `jrcxz` over >2GB
    const uint8_t stolen_loop[] = {
        0x83, 0xC7, 0x01,                   // 0: add edi, 1 (3B, internal target)
        0xE3, 0x05,                         // 3: jrcxz +5 -> orig + 10 (2B, internal!)
        0xE9, 0x50, 0x00, 0x00, 0x00,       // 5: jmp +0x50 -> external (5B -> 14B)
        0xEB, 0xF4,                         // 10: jmp short -12 -> orig + 0 (2B, internal backward!)
        0x90, 0x90,                         // 12: nop; nop (2B)
    };
    out.clear();
    CHECK(HookEngine::relocate_instructions(orig_base, stolen_loop, sizeof(stolen_loop), tramp_base, out));
    // Expected layout:
    //   0..2:   83 C7 01 (3B)
    //   3..11:  E3 02 EB 05 E9 <rel32 = 14> (9B) -> target = tramp_base + 12 + 14 = tramp_base + 26
    //   12..25: FF 25 ... (14B)
    //   26..30: E9 <rel32 = -31> (5B) -> target = tramp_base + 31 - 31 = tramp_base + 0
    //   31..32: 90 90 (2B)
    //   33..46: FF 25 ... (14B)
    CHECK(out.size() == 47u);
    CHECK(out[3] == 0xE3u && out[7] == 0xE9u);
    int32_t jrcxz_disp = 0;
    std::memcpy(&jrcxz_disp, &out[8], 4);
    CHECK(jrcxz_disp == 14);
    CHECK(out[26] == 0xE9u);
    int32_t back_disp = 0;
    std::memcpy(&back_disp, &out[27], 4);
    CHECK(back_disp == -31);

    ++g_tests_passed;
    return 0;
}

int test_raw_detour_self_removal() {
    using nativemods::HookEngine;
    CHECK(nm_initialize_for_build(NM_BUILD_SYNTHETIC, 0x10000000ull, 0x10000u, nullptr) == NM_OK);

    // 1. Inline raw detour calling nm_remove_hook(self) BEFORE calling out_trampoline
    void* code_page = HookEngine::alloc_exec_page(4096);
    CHECK(code_page != nullptr);
    auto* code = static_cast<uint8_t*>(code_page);
    std::memset(code, 0x90, 16);
#if defined(_WIN32)
    // lea eax, [rcx + 10] ; ret
    code[16] = 0x8D; code[17] = 0x41; code[18] = 0x0A; code[19] = 0xC3;
#else
    // lea eax, [rdi + 10] ; ret
    code[16] = 0x8D; code[17] = 0x47; code[18] = 0x0A; code[19] = 0xC3;
#endif
#if defined(_WIN32)
    FlushInstructionCache(GetCurrentProcess(), code_page, 4096);
#else
    __builtin___clear_cache(static_cast<char*>(code_page), static_cast<char*>(code_page) + 4096);
#endif

    auto target_fn = reinterpret_cast<TestAddFn>(code_page);
    CHECK(target_fn(5) == 15);

    g_self_inline_hits.store(0, std::memory_order_relaxed);
    g_self_inline_remove_st = NM_ERR_INVALID_ARG;
    g_self_inline_handle = NM_HOOK_INVALID;
    void* tramp = nullptr;
    CHECK(nm_install_inline_hook_at(
              reinterpret_cast<uint64_t>(code_page),
              "9090909090909090",
              reinterpret_cast<void*>(&self_removing_inline_detour),
              &tramp,
              &g_self_inline_handle) == NM_OK);
    CHECK(tramp != nullptr && g_self_inline_handle != NM_HOOK_INVALID);
    g_self_inline_tramp = reinterpret_cast<TestAddFn>(tramp);

    // Detour removes itself, then invokes next_stub (must NOT UAF/SEGV!)
    CHECK(target_fn(5) == 115);
    CHECK(g_self_inline_remove_st == NM_OK);
    CHECK(g_self_inline_hits.load(std::memory_order_relaxed) == 1);
    // Subsequent call runs restored original directly
    CHECK(target_fn(5) == 15);
    CHECK(g_self_inline_hits.load(std::memory_order_relaxed) == 1);

    HookEngine::free_exec_page(code_page, 4096);

    // 2. Vtable raw detour calling nm_remove_hook(self) BEFORE calling out_original_fn
    void* vtable[2] = {nullptr, reinterpret_cast<void*>(&vtable_orig_fn)};
    g_self_vt_hits.store(0, std::memory_order_relaxed);
    g_self_vt_remove_st = NM_ERR_INVALID_ARG;
    g_self_vt_handle = NM_HOOK_INVALID;
    void* vt_orig = nullptr;
    CHECK(nm_install_vtable_hook(
              vtable,
              1u,
              reinterpret_cast<void*>(&self_removing_vtable_detour),
              &vt_orig,
              &g_self_vt_handle) == NM_OK);
    CHECK(vt_orig != nullptr && g_self_vt_handle != NM_HOOK_INVALID);
    g_self_vt_orig = reinterpret_cast<TestAddFn>(vt_orig);

    CHECK(reinterpret_cast<TestAddFn>(vtable[1])(10) == 70);
    CHECK(g_self_vt_remove_st == NM_OK);
    CHECK(g_self_vt_hits.load(std::memory_order_relaxed) == 1);
    CHECK(vtable[1] == reinterpret_cast<void*>(&vtable_orig_fn));
    CHECK(reinterpret_cast<TestAddFn>(vtable[1])(10) == 20);
    CHECK(g_self_vt_hits.load(std::memory_order_relaxed) == 1);

    CHECK(nm_shutdown() == NM_OK);
    ++g_tests_passed;
    return 0;
}

int test_concurrent_vtable_chain_install_remove_stress() {
    CHECK(nm_initialize_for_build(NM_BUILD_SYNTHETIC, 0x10000000ull, 0x10000u, nullptr) == NM_OK);

    void* vt[2] = {nullptr, reinterpret_cast<void*>(&vt_stress_target)};
    using StressFn = int64_t(NM_CALL*)(int64_t);

    std::atomic<bool> stop{false};
    std::atomic<uint64_t> bad_results{0};
    std::atomic<uint64_t> total_calls{0};

    std::vector<std::thread> callers;
    for (int t = 0; t < 3; ++t) {
        callers.emplace_back([&]() {
            while (!stop.load(std::memory_order_acquire)) {
                StressFn fn = __atomic_load_n(reinterpret_cast<StressFn*>(&vt[1]), __ATOMIC_ACQUIRE);
                const int64_t r = fn(7);
                if (r != 22 && r != 52 && r != 122 && r != 152) {
                    bad_results.fetch_add(1, std::memory_order_relaxed);
                }
                total_calls.fetch_add(1, std::memory_order_relaxed);
            }
        });
    }

    auto pre_cb = [](nm_hook_context_t* ctx, void*) {
        ctx->args[0] += 10;
    };
    auto post_cb = [](nm_hook_context_t* ctx, void*) {
        ctx->return_value += 100;
    };

    for (int iter = 0; iter < 250; ++iter) {
        nm_hook_handle_t h_pre = NM_HOOK_INVALID;
        nm_hook_handle_t h_post = NM_HOOK_INVALID;
        CHECK(nm_install_vtable_chain_hook(vt, 1u, NM_HOOK_PHASE_PRE, 0, pre_cb, nullptr, &h_pre) == NM_OK);
        CHECK(nm_install_vtable_chain_hook(vt, 1u, NM_HOOK_PHASE_POST, 0, post_cb, nullptr, &h_post) == NM_OK);
        if ((iter % 8) == 0) {
            std::this_thread::sleep_for(std::chrono::milliseconds(2));
        } else {
            std::this_thread::yield();
        }
        if ((iter & 1) == 0) {
            CHECK(nm_remove_hook(h_pre) == NM_OK);
            CHECK(nm_remove_hook(h_post) == NM_OK);
        } else {
            CHECK(nm_remove_hook(h_post) == NM_OK);
            CHECK(nm_remove_hook(h_pre) == NM_OK);
        }
    }

    stop.store(true, std::memory_order_release);
    for (auto& th : callers) {
        th.join();
    }

    CHECK(bad_results.load() == 0);
    CHECK(total_calls.load() > 0);
    CHECK(vt[1] == reinterpret_cast<void*>(&vt_stress_target));
    CHECK(nm_shutdown() == NM_OK);
    ++g_tests_passed;
    return 0;
}

namespace {
NM_NOINLINE int64_t NM_CALL unwind_chain_target(int64_t x) {
    volatile int64_t a = x * 3 + 7;
    volatile int64_t b = a ^ 0x12345LL;
    if (x < 0) {
        throw std::runtime_error("chain-target-throw");
    }
    return a + b;
}
} // namespace

int test_short_function_and_midinsn_relocate_rejection() {
    using nativemods::HookEngine;
    nativemods::HookEngine engine;

    // 1. Short <14B leaf function followed by int3 + adjacent FuncB must be rejected without touching FuncB
    void* exec_page = HookEngine::alloc_exec_page(4096);
    CHECK(exec_page != nullptr);
    auto* mem = static_cast<uint8_t*>(exec_page);
    std::memset(mem, 0x90, 256);
    // FuncA at +0: mov eax, edi (2B); ret (1B); int3 (1B)
    mem[0] = 0x89; mem[1] = 0xF8; mem[2] = 0xC3;
    mem[3] = 0xCC;
    // FuncB at +4: mov eax, 42 (5B); ret (1B) -> starts well inside 14-byte steal window!
    const uint8_t funcb_orig[6] = {0xB8, 0x2A, 0x00, 0x00, 0x00, 0xC3};
    std::memcpy(mem + 4, funcb_orig, sizeof(funcb_orig));

    CHECK(HookEngine::compute_stolen_length(mem, 14) == 0);
    auto dummy_detour = +[](int x) -> int { return x + 1; };
    void* orig_ptr = nullptr;
    nm_hook_handle_t bad_h = NM_HOOK_INVALID;
    CHECK(engine.install_inline(
              reinterpret_cast<uint64_t>(mem),
              nullptr,
              reinterpret_cast<void*>(dummy_detour),
              &orig_ptr,
              &bad_h) == NM_ERR_INVALID_ARG);
    CHECK(bad_h == NM_HOOK_INVALID);
    CHECK(std::memcmp(mem + 4, funcb_orig, sizeof(funcb_orig)) == 0);
    using LeafFn = int(*)();
    CHECK(reinterpret_cast<LeafFn>(mem + 4)() == 42);

    // 2. Unconditional JMP thunk (<14B) must also be rejected
    const uint8_t jmp_thunk[16] = {0xE9, 0x10, 0x00, 0x00, 0x00, 0xCC, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90};
    CHECK(HookEngine::compute_stolen_length(jmp_thunk, 14) == 0);

    // 3. Conditional branch jumping past an earlier ret allows continuing past ret cleanly
    const uint8_t cond_skip_ret[16] = {
        0x85, 0xFF,                         // test edi, edi (2B -> 2)
        0x75, 0x03,                         // jnz +3 -> off 7 (2B -> 4)
        0xB0, 0x01,                         // mov al, 1 (2B -> 6)
        0xC3,                               // ret (1B -> 7; jnz target == 7 so allowed!)
        0xB8, 0x2A, 0x00, 0x00, 0x00,       // mov eax, 42 (5B -> 12)
        0x90, 0x90, 0x90, 0xC3,             // nops + ret (-> 16)
    };
    CHECK(HookEngine::compute_stolen_length(cond_skip_ret, 14) >= 14);

    // 4. Mid-instruction internal jump in relocate_instructions must return false
    const uint8_t mid_insn_jmp[9] = {
        0xEB, 0x01,                         // jmp +1 -> off 3 (lands INSIDE 48 89 E5 at 2..5!)
        0x48, 0x89, 0xE5,                   // mov rbp, rsp (off 2..5)
        0x48, 0x83, 0xEC, 0x20,             // sub rsp, 0x20 (off 5..9)
    };
    std::vector<uint8_t> relocated;
    CHECK(!HookEngine::relocate_instructions(0x100000000ull, mid_insn_jmp, sizeof(mid_insn_jmp), 0x100020000ull, relocated));

    HookEngine::free_exec_page(exec_page, 4096);
    ++g_tests_passed;
    return 0;
}

int test_chained_hook_exception_unwind_and_active_calls() {
    nativemods::HookEngine engine;
    const uint64_t target_u64 = reinterpret_cast<uint64_t>(&unwind_chain_target);

    // Check inline chain unwind when target, PRE, and POST callbacks throw C++ exceptions
    auto pre_cb = [](nm_hook_context_t* ctx, void*) {
        if (ctx->args[0] == 111) {
            throw std::runtime_error("pre-throw");
        }
        if (ctx->args[0] == 222) {
            ctx->args[0] = static_cast<uint64_t>(-1LL); // causes target to throw
        }
    };
    auto post_cb = [](nm_hook_context_t* ctx, void*) {
        if (ctx->args[0] == 333) {
            throw std::runtime_error("post-throw");
        }
        ctx->return_value += 1;
    };

    nm_hook_handle_t h_pre = NM_HOOK_INVALID;
    nm_hook_handle_t h_post = NM_HOOK_INVALID;
    CHECK(engine.install_inline_chain(target_u64, nullptr, NM_HOOK_PHASE_PRE, 0, pre_cb, nullptr, &h_pre) == NM_OK);
    CHECK(engine.install_inline_chain(target_u64, nullptr, NM_HOOK_PHASE_POST, 0, post_cb, nullptr, &h_post) == NM_OK);

    // Normal execution
    const int64_t ok_res = unwind_chain_target(5);
    CHECK(ok_res != 0);
    CHECK(engine.chain_active_calls(NM_HOOK_INLINE_DETOUR, target_u64) == 0u);

    // 1. Exception in PRE callback
    bool caught_pre = false;
    try {
        (void)unwind_chain_target(111);
    } catch (const std::runtime_error& e) {
        caught_pre = (std::string(e.what()) == "pre-throw");
    }
    CHECK(caught_pre);
    CHECK(engine.chain_active_calls(NM_HOOK_INLINE_DETOUR, target_u64) == 0u);

    // 2. Exception in original_callable target
    bool caught_target = false;
    try {
        (void)unwind_chain_target(222);
    } catch (const std::runtime_error& e) {
        caught_target = (std::string(e.what()) == "chain-target-throw");
    }
    CHECK(caught_target);
    CHECK(engine.chain_active_calls(NM_HOOK_INLINE_DETOUR, target_u64) == 0u);

    // 3. Exception in POST callback
    bool caught_post = false;
    try {
        (void)unwind_chain_target(333);
    } catch (const std::runtime_error& e) {
        caught_post = (std::string(e.what()) == "post-throw");
    }
    CHECK(caught_post);
    CHECK(engine.chain_active_calls(NM_HOOK_INLINE_DETOUR, target_u64) == 0u);

    // 4. Clean retirement after exception unwind (no leaked active_calls)
    CHECK(engine.remove(h_pre) == NM_OK);
    CHECK(engine.remove(h_post) == NM_OK);
    engine.sweep_retired_now();
    CHECK(engine.retired_chain_count() == 0u);

    // 5. Same check for vtable chain
    void* vt[2] = {nullptr, reinterpret_cast<void*>(&unwind_chain_target)};
    nm_hook_handle_t hv = NM_HOOK_INVALID;
    CHECK(engine.install_vtable_chain(vt, 1u, NM_HOOK_PHASE_PRE, 0, pre_cb, nullptr, &hv) == NM_OK);
    bool caught_vt = false;
    try {
        using Fn = int64_t(NM_CALL*)(int64_t);
        (void)reinterpret_cast<Fn>(vt[1])(222);
    } catch (const std::runtime_error& e) {
        caught_vt = (std::string(e.what()) == "chain-target-throw");
    }
    CHECK(caught_vt);
    CHECK(engine.chain_active_calls(NM_HOOK_VTABLE_SLOT, reinterpret_cast<uint64_t>(&vt[1])) == 0u);
    CHECK(engine.remove(hv) == NM_OK);
    engine.sweep_retired_now();
    CHECK(engine.retired_chain_count() == 0u);

    ++g_tests_passed;
    return 0;
}

int test_poe_bom_stripping_and_net_bounds() {
    CHECK(nm_initialize_for_build(NM_BUILD_SYNTHETIC, 0x10000000ull, 0x10000u, nullptr) == NM_OK);

    // 1. BOM handling for .sm/.mat/.amd (strip BOM) and .ui/.ot (ensure FF FE BOM + CRLF)
    struct SampleCase {
        const char* path;
        std::vector<uint8_t> input;
        std::vector<uint8_t> expected;
    };
    const std::vector<SampleCase> cases = {
        // UTF-16LE BOM supplied for .sm -> BOM stripped, LF -> CRLF
        {"mods/test/a.sm", {0xFF, 0xFE, 'a', 0x00, '\n', 0x00, 'b', 0x00}, {'a', 0x00, '\r', 0x00, '\n', 0x00, 'b', 0x00}},
        // UTF-16BE BOM supplied for .mat -> transcoded to LE without BOM + CRLF
        {"mods/test/a.mat", {0xFE, 0xFF, 0x00, 'a', 0x00, '\n', 0x00, 'b'}, {'a', 0x00, '\r', 0x00, '\n', 0x00, 'b', 0x00}},
        // UTF-8 BOM supplied for .amd -> BOM stripped, transcoded to LE without BOM + CRLF
        {"mods/test/a.amd", {0xEF, 0xBB, 0xBF, 'a', '\n', 'b'}, {'a', 0x00, '\r', 0x00, '\n', 0x00, 'b', 0x00}},
        // BOM-less UTF-16LE supplied for .ui -> gets FF FE BOM + CRLF
        {"mods/test/a.ui", {'a', 0x00, '\n', 0x00, 'b', 0x00}, {0xFF, 0xFE, 'a', 0x00, '\r', 0x00, '\n', 0x00, 'b', 0x00}},
        // UTF-16BE supplied for .ot -> transcoded to UTF-16LE WITH FF FE BOM + CRLF
        {"mods/test/a.ot", {0xFE, 0xFF, 0x00, 'a', 0x00, '\n', 0x00, 'b'}, {0xFF, 0xFE, 'a', 0x00, '\r', 0x00, '\n', 0x00, 'b', 0x00}},
    };

    for (const auto& sc : cases) {
        uint64_t tok = 0;
        const auto* vec_ptr = &sc.input;
        auto cb = [](const char*, uint8_t** out_data, size_t* out_size, void* ud) -> nm_status_t {
            const auto* v = static_cast<const std::vector<uint8_t>*>(ud);
            *out_data = const_cast<uint8_t*>(v->data());
            *out_size = v->size();
            return NM_OK;
        };
        CHECK(nm_register_file_overlay(cb, nullptr, const_cast<std::vector<uint8_t>*>(vec_ptr), &tok) == NM_OK);
        uint8_t* resolved = nullptr;
        size_t resolved_len = 0;
        CHECK(nm_resolve_file_overlay(sc.path, &resolved, &resolved_len) == NM_OK);
        CHECK(resolved_len == sc.expected.size());
        CHECK(std::memcmp(resolved, sc.expected.data(), resolved_len) == 0);
        CHECK(nm_free_file_overlay_buffer(resolved) == NM_OK);
        CHECK(nm_unregister_file_overlay(tok) == NM_OK);
    }

    // 2. Opcode handler group min_opcode < 0x02FC rejected
    uint64_t grp_tok = 0;
    auto raw_cb = [](uint16_t, void*, const uint8_t*, uint32_t, void*) -> int { return 0; };
    CHECK(nm_register_opcode_handler_group(0x02FBu, 0x02FEu, raw_cb, nullptr, &grp_tok) == NM_ERR_INVALID_ARG);
    CHECK(nm_register_opcode_handler_group(0x0000u, 0x02FCu, raw_cb, nullptr, &grp_tok) == NM_ERR_INVALID_ARG);
    CHECK(nm_register_opcode_handler_group(0x02FCu, 0x02FFu, raw_cb, nullptr, &grp_tok) == NM_OK);
    CHECK(nm_unregister_opcode_handler_group(grp_tok) == NM_OK);

    // 3. nm_send_raw_packet length bounds (0 / nullptr -> NM_ERR_INVALID_ARG, > 65536 -> NM_ERR_PAYLOAD_TOO_LARGE)
    uint8_t sample_pkt[4] = {1, 2, 3, 4};
    CHECK(nm_send_raw_packet(0x02FCu, sample_pkt, 0u) == NM_ERR_INVALID_ARG);
    CHECK(nm_send_raw_packet(0x02FCu, nullptr, 4u) == NM_ERR_INVALID_ARG);
    std::vector<uint8_t> oversized(65537u, 0xAAu);
    CHECK(nm_send_raw_packet(0x02FCu, oversized.data(), static_cast<uint32_t>(oversized.size())) == NM_ERR_PAYLOAD_TOO_LARGE);

    CHECK(nm_shutdown() == NM_OK);
    ++g_tests_passed;
    return 0;
}

int test_live_ui_vtable_mismatch_guards() {
#if defined(_WIN32)
    nativemods::HookEngine hooks;
    nativemods::GameAllocator alloc;
    nativemods::OverlayAndChannels ov;
    nativemods::LiveEngineHooks live;
    live.install_live_hooks(NM_BUILD_3_29_3D_328228, 0x140000000ull, hooks, alloc, ov);

    struct alignas(16) FakeControl {
        void**  vtbl;
        uint8_t body[2048];
    };
    void* dummy_vtable[160]{};
    FakeControl fake{};
    fake.vtbl = dummy_vtable;
    std::memset(fake.body, 0, sizeof(fake.body));

    char buf[64]{};
    size_t out_len = 0;
    int32_t checked = 0;
    uint64_t tok = 0;
    auto dummy_cb = [](void*, void*) {};

    CHECK(live.ui_textbox_get_text(&fake, buf, sizeof(buf), &out_len) == NM_ERR_INVALID_ARG);
    CHECK(live.ui_textbox_set_text(&fake, "hello") == NM_ERR_INVALID_ARG);
    CHECK(live.ui_textbox_subscribe_changed(&fake, dummy_cb, nullptr, &tok) == NM_ERR_INVALID_ARG);
    CHECK(live.ui_textbox_subscribe_submitted(&fake, dummy_cb, nullptr, &tok) == NM_ERR_INVALID_ARG);
    CHECK(live.ui_checkbox_get_checked(&fake, &checked) == NM_ERR_INVALID_ARG);
    CHECK(live.ui_checkbox_set_checked(&fake, 1) == NM_ERR_INVALID_ARG);
    CHECK(live.ui_checkbox_subscribe_changed(&fake, dummy_cb, nullptr, &tok) == NM_ERR_INVALID_ARG);

    live.reset(&hooks);
#endif
    ++g_tests_passed;
    return 0;
}

int test_phase1_prologues_hookengine_safety() {
    // Verify HookEngine::compute_stolen_length >= 14 on all 11 Phase 1 live 3.29.3d hook prologues.
    const char* kPhase1Hooks[] = {
        "frame.state_stack_on_wndmsg",
        "ui.ingame_ui_init",
        "frame.ingame_state_leave",
        "ui.league_overlay_fill",
        "frame.create_char_enter",
        "frame.create_char_leave",
        "ui.tab_container_select",
        "ui.map_stash_select_group",
        "ui.map_stash_select_key",
        "network.login_write_create_char_0x12",
        "frame.create_char_result_handler",
    };

    auto hex_nibble = [](char c) -> int {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    };

    for (const char* name : kPhase1Hooks) {
        const nativemods::EmbeddedSymbol* sym = nativemods::find_symbol(NM_BUILD_3_29_3D_328228, name);
        CHECK(sym != nullptr && sym->expected_bytes_hex != nullptr);
        const size_t hex_len = std::strlen(sym->expected_bytes_hex);
        CHECK(hex_len == 32u); // 16 verified bytes
        uint8_t buf[32];
        std::memset(buf, 0x90, sizeof(buf));
        for (size_t i = 0; i < 16u; ++i) {
            const int hi = hex_nibble(sym->expected_bytes_hex[i * 2]);
            const int lo = hex_nibble(sym->expected_bytes_hex[i * 2 + 1]);
            CHECK(hi >= 0 && lo >= 0);
            buf[i] = static_cast<uint8_t>((hi << 4) | lo);
        }
        const size_t stolen = nativemods::HookEngine::compute_stolen_length(buf, 14);
        CHECK(stolen >= 14u && stolen <= 16u);
    }

    ++g_tests_passed;
    return 0;
}

int test_phase1_input_screens_hover_and_pregame_callbacks() {
    CHECK(nm_initialize_for_build(NM_BUILD_SYNTHETIC, 0x10000000ull, 0x10000u, nullptr) == NM_OK);

    // 1. Input interception & suppression (Ctrl+Shift+RMB suppressed; plain RMB passes through; exceptions isolated)
    struct InputCapture {
        int      calls = 0;
        uint32_t last_mods = 0;
        uint8_t  last_text_focused = 0;
    } icap{};

    auto input_cb = [](const nm_input_event_t* ev, void* ud) -> int32_t {
        auto* c = static_cast<InputCapture*>(ud);
        ++c->calls;
        c->last_mods = ev->modifiers;
        c->last_text_focused = ev->is_text_input_focused;
        if (ev->virtual_key == 0xFFu) {
            throw std::runtime_error("faulty input handler");
        }
        if (ev->kind == NM_INPUT_MOUSE_DOWN &&
            ev->mouse_button == NM_MOUSE_RIGHT &&
            (ev->modifiers & (NM_MOD_CTRL | NM_MOD_SHIFT)) == (NM_MOD_CTRL | NM_MOD_SHIFT)) {
            return 1; // suppress before InGameState / UIManager / InventoryGrid
        }
        return 0;
    };

    CHECK(nm_input_set_callback(input_cb, &icap) == NM_OK);

    nm_input_event_t ev{};
    ev.struct_size = sizeof(ev);
    ev.win32_msg = 0x0204u; // WM_RBUTTONDOWN
    ev.kind = NM_INPUT_MOUSE_DOWN;
    ev.mouse_button = NM_MOUSE_RIGHT;
    ev.modifiers = NM_MOD_CTRL | NM_MOD_SHIFT;
    ev.mouse_x = 640;
    ev.mouse_y = 360;
    ev.is_text_input_focused = 0;
    CHECK(nm_dispatch_input_event(&ev) == 1);
    CHECK(icap.calls == 1 && icap.last_mods == (NM_MOD_CTRL | NM_MOD_SHIFT));

    ev.modifiers = NM_MOD_NONE;
    ev.is_text_input_focused = 1;
    CHECK(nm_dispatch_input_event(&ev) == 0);
    CHECK(icap.calls == 2 && icap.last_text_focused == 1);

    // Throwing callback must be isolated and return 0 (never suppress or crash)
    ev.virtual_key = 0xFFu;
    CHECK(nm_dispatch_input_event(&ev) == 0);
    CHECK(icap.calls == 3);

    CHECK(nm_input_set_callback(nullptr, nullptr) == NM_OK);
    ev.modifiers = NM_MOD_CTRL | NM_MOD_SHIFT;
    ev.virtual_key = 0;
    CHECK(nm_dispatch_input_event(&ev) == 0);
    CHECK(icap.calls == 3);

    // 2. Pre-Game S78 rewrite (CharacterCreating) & result callback (CharacterCreated)
    struct PreGameCapture {
        std::string creating_name;
        std::string creating_league;
        std::string creating_state;
        std::string created_name;
        std::string created_league;
        uint16_t    created_result = 0xFFFFu;
        bool        skip_campaign = true;
    } pgcap{};

    auto creating_cb = [](const char* name, const char* league, const char* start_state,
                          char* out_buf, size_t out_cap, void* ud) -> int32_t {
        auto* c = static_cast<PreGameCapture*>(ud);
        c->creating_name = name;
        c->creating_league = league;
        c->creating_state = start_state;
        if (std::strcmp(name, "ThrowExile") == 0) {
            throw std::runtime_error("faulty pregame callback");
        }
        if (!c->skip_campaign) return 0;
        const std::string rewritten = std::string(start_state) + "|nm:skip_campaign=1";
        if (rewritten.size() + 1 > out_cap) return 0;
        std::memcpy(out_buf, rewritten.c_str(), rewritten.size() + 1);
        return 1;
    };
    auto created_cb = [](const char* name, const char* league, uint16_t result_code, void* ud) {
        auto* c = static_cast<PreGameCapture*>(ud);
        c->created_name = name;
        c->created_league = league;
        c->created_result = result_code;
    };

    CHECK(nm_pregame_set_callbacks(creating_cb, created_cb, &pgcap) == NM_OK);

    char out_s78[256] = {};
    CHECK(nm_dispatch_pregame_creating("RangerTest", "Standard", "DexDefault", out_s78, sizeof(out_s78)) == 1);
    CHECK(pgcap.creating_name == "RangerTest" && pgcap.creating_league == "Standard" && pgcap.creating_state == "DexDefault");
    CHECK(std::strcmp(out_s78, "DexDefault|nm:skip_campaign=1") == 0);

    pgcap.skip_campaign = false;
    std::memset(out_s78, 0xCC, sizeof(out_s78));
    CHECK(nm_dispatch_pregame_creating("RangerTest", "Standard", "DexDefault", out_s78, sizeof(out_s78)) == 0);
    CHECK(out_s78[0] == '\0');

    // Exception isolation
    pgcap.skip_campaign = true;
    CHECK(nm_dispatch_pregame_creating("ThrowExile", "Standard", "DexDefault", out_s78, sizeof(out_s78)) == 0);
    CHECK(out_s78[0] == '\0');

    nm_dispatch_pregame_created("RangerTest", "Standard", 0u);
    CHECK(pgcap.created_name == "RangerTest" && pgcap.created_league == "Standard" && pgcap.created_result == 0u);
    nm_dispatch_pregame_created("RangerTest", "Standard", 3u);
    CHECK(pgcap.created_result == 3u);

    // 3. Screen lifecycle callback
    struct ScreenEvtCapture {
        int      count = 0;
        int32_t  last_kind = 0;
        void*    last_handle = nullptr;
        uint64_t last_param = 0;
    } scap{};
    auto screen_cb = [](int32_t kind, void* handle, uint64_t param0, void* ud) {
        auto* c = static_cast<ScreenEvtCapture*>(ud);
        ++c->count;
        c->last_kind = kind;
        c->last_handle = handle;
        c->last_param = param0;
    };
    CHECK(nm_screens_set_callback(screen_cb, &scap) == NM_OK);
    void* fake_overlay = reinterpret_cast<void*>(0x12345678ull);
    nm_dispatch_screen_event(NM_SCREEN_EVT_LEAGUE_OVERLAY_FILLED, fake_overlay, 2ull);
    CHECK(scap.count == 1 && scap.last_kind == NM_SCREEN_EVT_LEAGUE_OVERLAY_FILLED &&
          scap.last_handle == fake_overlay && scap.last_param == 2ull);

    // 4. Null-arg validation & synthetic defaults for screens/hover/input/ui traversal APIs
    nm_screens_state_t sstate{};
    CHECK(nm_screens_get_state(nullptr) == NM_ERR_INVALID_ARG);
    CHECK(nm_screens_get_state(&sstate) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);
    CHECK(sstate.struct_size == sizeof(nm_screens_state_t));
    CHECK(sstate.screen == NM_SCREEN_UNKNOWN && sstate.active_stash_tab_index == -1 && sstate.stash_mode == 0xFFFFFFFFu);

    nm_hover_state_t hstate{};
    CHECK(nm_hover_get_state(nullptr) == NM_ERR_INVALID_ARG);
    CHECK(nm_hover_get_state(&hstate) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);
    CHECK(hstate.struct_size == sizeof(nm_hover_state_t));
    CHECK(hstate.cursor_mode == NM_CURSOR_EMPTY && hstate.hovered_item.present == 0 && hstate.hovered_item.grid_x == -1);

    int32_t focused = 99;
    CHECK(nm_input_is_text_focused(nullptr) == NM_ERR_INVALID_ARG);
    CHECK(nm_input_is_text_focused(&focused) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);
    CHECK(focused == 0);

    uint8_t dummy[64] = {};
    void* out_ptr = nullptr;
    uint32_t out_u32 = 0;
    uint64_t out_u64 = 0;
    uint16_t out_u16 = 0;
    int32_t out_i32 = 0;
    size_t out_sz = 0;
    char id_buf[32] = {};
    CHECK(nm_ui_get_active_root(nullptr) == NM_ERR_INVALID_ARG);
    CHECK(nm_ui_get_active_root(&out_ptr) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);
    CHECK(nm_ui_get_child_count(nullptr, &out_u32) == NM_ERR_INVALID_ARG);
    CHECK(nm_ui_get_child_count(dummy, &out_u32) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);
    CHECK(nm_ui_get_child_at(nullptr, 0, &out_ptr) == NM_ERR_INVALID_ARG);
    CHECK(nm_ui_get_child_at(dummy, 0, &out_ptr) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);
    CHECK(nm_ui_get_vtable(nullptr, &out_u64) == NM_ERR_INVALID_ARG);
    CHECK(nm_ui_get_vtable(dummy, &out_u64) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);
    CHECK(nm_ui_get_control_tag(nullptr, &out_u16) == NM_ERR_INVALID_ARG);
    CHECK(nm_ui_get_control_tag(dummy, &out_u16) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);
    CHECK(nm_ui_get_control_id(nullptr, id_buf, sizeof(id_buf), &out_sz) == NM_ERR_INVALID_ARG);
    CHECK(nm_ui_get_control_id(dummy, id_buf, sizeof(id_buf), &out_sz) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);
    CHECK(nm_ui_get_control_parent(nullptr, &out_ptr) == NM_ERR_INVALID_ARG);
    CHECK(nm_ui_get_control_parent(dummy, &out_ptr) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);
    CHECK(nm_ui_is_visible(nullptr, &out_i32) == NM_ERR_INVALID_ARG);
    CHECK(nm_ui_is_visible(dummy, &out_i32) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);
    CHECK(nm_ui_set_width(nullptr, 100.0f) == NM_ERR_INVALID_ARG);
    CHECK(nm_ui_set_width(dummy, 100.0f) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);
    CHECK(nm_ui_set_height(nullptr, 40.0f) == NM_ERR_INVALID_ARG);
    CHECK(nm_ui_set_height(dummy, 40.0f) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);
    CHECK(nm_ui_create_checkbox_child(nullptr, "Skip", &out_ptr) == NM_ERR_INVALID_ARG);
    CHECK(nm_ui_create_checkbox_child(dummy, "Skip", &out_ptr) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);
    CHECK(nm_ui_create_stack_layout(dummy, 1, nullptr) == NM_ERR_INVALID_ARG);
    CHECK(nm_ui_create_stack_layout(dummy, 1, &out_ptr) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);
    CHECK(nm_ui_layout_update(nullptr) == NM_ERR_INVALID_ARG);
    CHECK(nm_ui_layout_update(dummy) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);
    CHECK(nm_ui_set_anchor(nullptr, 3, 16.0f, 0, 20.0f) == NM_ERR_INVALID_ARG);
    CHECK(nm_ui_set_anchor(dummy, 3, 16.0f, 0, 20.0f) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);

    // 5. Win32 in-memory stock UI control traversal unit test (runs under Wine / Windows)
#if defined(_WIN32)
    {
        nativemods::HookEngine hooks;
        nativemods::Phase1Services p1;
        p1.install_live_hooks(NM_BUILD_3_29_3D_328228, 0x140000000ull, hooks);

        struct alignas(16) FakeCtrl {
            uint8_t bytes[0x300];
        };
        FakeCtrl parent{};
        FakeCtrl child0{};
        FakeCtrl child1{};
        std::memset(&parent, 0, sizeof(parent));
        std::memset(&child0, 0, sizeof(child0));
        std::memset(&child1, 0, sizeof(child1));

        void* children_arr[2] = {&child0, &child1};
        void* const* begin_ptr = children_arr;
        void* const* end_ptr = children_arr + 2;
        std::memcpy(parent.bytes + 0xC0u, &begin_ptr, sizeof(begin_ptr));
        std::memcpy(parent.bytes + 0xC8u, &end_ptr, sizeof(end_ptr));

        const uint64_t fake_vt = 0x1431eccb0ull;
        const uint16_t fake_tag = NM_TAG_LEAGUE_CARD;
        void* parent_ptr = &parent;
        std::memcpy(child0.bytes + 0x00u, &fake_vt, sizeof(fake_vt));
        std::memcpy(child0.bytes + 0x1D8u, &fake_tag, sizeof(fake_tag));
        std::memcpy(child0.bytes + 0x1E0u, &parent_ptr, sizeof(parent_ptr));

        // Write SSO MSVC std::wstring "card0" at child0 + 0x1B8
        const wchar_t sso_id[] = L"card0";
        const uint64_t sso_len = 5;
        const uint64_t sso_cap = 7;
        std::memcpy(child0.bytes + 0x1B8u, sso_id, sizeof(sso_id));
        std::memcpy(child0.bytes + 0x1B8u + 16u, &sso_len, sizeof(sso_len));
        std::memcpy(child0.bytes + 0x1B8u + 24u, &sso_cap, sizeof(sso_cap));

        uint32_t cnt = 0;
        CHECK(p1.ui_get_child_count(&parent, &cnt) == NM_OK && cnt == 2u);
        void* c0 = nullptr;
        void* c1 = nullptr;
        CHECK(p1.ui_get_child_at(&parent, 0, &c0) == NM_OK && c0 == &child0);
        CHECK(p1.ui_get_child_at(&parent, 1, &c1) == NM_OK && c1 == &child1);
        CHECK(p1.ui_get_child_at(&parent, 2, &c0) == NM_ERR_INVALID_ARG);

        uint64_t vt = 0;
        uint16_t tag = 0;
        void* par = nullptr;
        CHECK(p1.ui_get_vtable(&child0, &vt) == NM_OK && vt == fake_vt);
        CHECK(p1.ui_get_control_tag(&child0, &tag) == NM_OK && tag == NM_TAG_LEAGUE_CARD);
        CHECK(p1.ui_get_control_parent(&child0, &par) == NM_OK && par == &parent);

        char id_out[32] = {};
        size_t id_len = 0;
        CHECK(p1.ui_get_control_id(&child0, id_out, sizeof(id_out), &id_len) == NM_OK);
        CHECK(id_len == 5u && std::strcmp(id_out, "card0") == 0);

        p1.reset(&hooks);
    }
#endif

    // 6. Verify 3.29.3d BaseStatic+0x68 inline RowRef<BaseItemTypes> & MapStashPage+0x550 selected_group layouts (#23)
    {
        struct FakeItemClassRow {
            const wchar_t* class_id;
        } item_class_row{L"Currency"};

        struct FakeBaseItemTypesRow {
            const wchar_t* id;               // +0x00: BaseItemTypes.Id
            void*          item_classes_row; // +0x08: ItemClassesRow*
            void*          item_classes_dat; // +0x10: ItemClassesDat*
        } base_row{
            L"Metadata/Items/Currency/CurrencyRerollRare",
            &item_class_row,
            reinterpret_cast<void*>(0xDEAD0001ull),
        };

        alignas(16) uint8_t fake_base_static[0x98] = {};
        void* base_row_ptr = &base_row;
        void* base_dat_ptr = reinterpret_cast<void*>(0xDEAD0002ull);
        std::memcpy(fake_base_static + 0x68u, &base_row_ptr, sizeof(base_row_ptr));
        std::memcpy(fake_base_static + 0x70u, &base_dat_ptr, sizeof(base_dat_ptr));

        const wchar_t* extracted_meta = nullptr;
        const void* extracted_cls_ref = nullptr;
        CHECK(nativemods::Phase1Services::extract_base_item_type_row(
                  fake_base_static, 0x68u, &extracted_meta, &extracted_cls_ref));
        // Must read BaseItemTypes.Id (NOT ItemClasses.Id) and point to BaseItemTypesRow+0x08!
        CHECK(extracted_meta == base_row.id);
        CHECK(std::wcscmp(extracted_meta, L"Metadata/Items/Currency/CurrencyRerollRare") == 0);
        CHECK(extracted_cls_ref == reinterpret_cast<const uint8_t*>(&base_row) + 0x08u);

        // MapStashPage+0x550 std::optional<std::variant<RowRef0, RowRef1, RowRef2>>
        alignas(16) uint8_t fake_map_page[0x600] = {};
        // Empty optional (has_value == 0) -> -1
        CHECK(nativemods::Phase1Services::read_map_stash_selected_tier(fake_map_page, 0x550u) == -1);

        struct FakeMapKeyRow {
            const wchar_t* id;
        } map_t1{L"Metadata/Items/Maps/MapKeyTier1"},
          map_t16{L"Metadata/Items/Maps/MapKeyTier16"},
          map_t17{L"Metadata/Items/Maps/MapKeyTier17"},
          map_special{L"Metadata/Items/Maps/MapKeyUnique"};

        auto set_selected_group = [&](const void* row, int8_t var_idx, uint8_t has_val) {
            std::memset(fake_map_page + 0x550u, 0, 0x20u);
            std::memcpy(fake_map_page + 0x550u, &row, sizeof(row));
            std::memcpy(fake_map_page + 0x560u, &var_idx, sizeof(var_idx));
            std::memcpy(fake_map_page + 0x568u, &has_val, sizeof(has_val));
        };

        set_selected_group(&map_t1, 0, 1);
        CHECK(nativemods::Phase1Services::read_map_stash_selected_tier(fake_map_page, 0x550u) == 1);
        set_selected_group(&map_t16, 0, 1);
        CHECK(nativemods::Phase1Services::read_map_stash_selected_tier(fake_map_page, 0x550u) == 16);
        set_selected_group(&map_t17, 0, 1);
        CHECK(nativemods::Phase1Services::read_map_stash_selected_tier(fake_map_page, 0x550u) == 17);
        // Special group (variant_index != 0) or non-tier ID -> -1
        set_selected_group(&map_t16, 1, 1);
        CHECK(nativemods::Phase1Services::read_map_stash_selected_tier(fake_map_page, 0x550u) == -1);
        set_selected_group(&map_special, 0, 1);
        CHECK(nativemods::Phase1Services::read_map_stash_selected_tier(fake_map_page, 0x550u) == -1);

        CHECK(nativemods::Phase1Services::parse_map_key_tier("Metadata/Items/Maps/MapKeyTier14") == 14);
        CHECK(nativemods::Phase1Services::parse_map_key_tier("Metadata/Items/Maps/MapKeyTier0") == -1);
        CHECK(nativemods::Phase1Services::parse_map_key_tier("Metadata/Items/Maps/MapKeyTier16_extra") == -1);
        CHECK(nativemods::Phase1Services::parse_map_key_tier(nullptr) == -1);
    }

    // 7. Shutdown clears all Phase 1 callbacks cleanly
    CHECK(nm_shutdown() == NM_OK);
    CHECK(nm_dispatch_input_event(&ev) == 0);
    CHECK(nm_dispatch_pregame_creating("A", "B", "C", out_s78, sizeof(out_s78)) == 0);

    ++g_tests_passed;
    return 0;
}

int test_phase2_prologues_hookengine_safety() {
    const char* kPhase2Hooks[] = {
        "world.client_world_init_area",
        "frame.area_loading_enter",
        "frame.area_loading_leave",
        "frame.ingame_on_instance_details",
        "world.entity_mgr_insert",
        "world.entity_mgr_remove",
        "world.entity_mark_for_removal",
        "items.filter_match",
        "items.filter_poll_load",
        "items.filter_select_apply",
        "items.filter_notify_loaded",
        "items.filter_style_dtor_custom_sounds",
        "items.world_item_eval_filter",
        "items.world_item_is_visible",
        "ui.world_item_label_update",
        "world.camera_ctrl_update",
        "world.camera_ctrl_mouse_wheel",
        "world.camera_world_to_screen",
    };

    auto hex_nibble = [](char c) -> int {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    };

    for (const char* name : kPhase2Hooks) {
        const nativemods::EmbeddedSymbol* sym = nativemods::find_symbol(NM_BUILD_3_29_3D_328228, name);
        CHECK(sym != nullptr && sym->expected_bytes_hex != nullptr);
        CHECK(std::strlen(sym->expected_bytes_hex) == 32u);
        uint8_t buf[32];
        std::memset(buf, 0x90, sizeof(buf));
        for (size_t i = 0; i < 16u; ++i) {
            const int hi = hex_nibble(sym->expected_bytes_hex[i * 2]);
            const int lo = hex_nibble(sym->expected_bytes_hex[i * 2 + 1]);
            CHECK(hi >= 0 && lo >= 0);
            buf[i] = static_cast<uint8_t>((hi << 4) | lo);
        }
        const size_t stolen = nativemods::HookEngine::compute_stolen_length(buf, 14);
        CHECK(stolen >= 14u && stolen <= 24u);
    }

    ++g_tests_passed;
    return 0;
}

int test_phase2_world_rbtree_stats_area_filter_camera() {
    CHECK(nm_initialize_for_build(NM_BUILD_SYNTHETIC, 0x10000000ull, 0x10000u, nullptr) == NM_OK);

    // 1. World EntityManager MSVC std::map<uint32_t, Entity*> RB-tree traversal & lookup
    {
        struct FakeRbNode {
            FakeRbNode* left;      // +0x00
            FakeRbNode* parent;    // +0x08 (root in header sentinel)
            FakeRbNode* right;     // +0x10
            uint8_t     color;     // +0x18
            uint8_t     is_nil;    // +0x19
            uint8_t     pad[6];    // +0x1a..0x1f
            uint32_t    entity_id; // +0x20
            uint32_t    pad2;      // +0x24
            void*       entity;    // +0x28
        };
        static_assert(sizeof(FakeRbNode) == 0x30u);

        int e10 = 10, e20 = 20, e30 = 30, e40 = 40, e50 = 50;
        FakeRbNode head{};
        FakeRbNode n10{}, n20{}, n30{}, n40{}, n50{};
        head.left = &n10;
        head.parent = &n30; // root
        head.right = &n50;
        head.color = 1;
        head.is_nil = 1;

        auto init_node = [&](FakeRbNode& n, FakeRbNode* l, FakeRbNode* p, FakeRbNode* r, uint32_t id, void* ent) {
            n.left = l ? l : &head;
            n.parent = p ? p : &head;
            n.right = r ? r : &head;
            n.color = 0;
            n.is_nil = 0;
            n.entity_id = id;
            n.entity = ent;
        };
        init_node(n30, &n20, &head, &n40, 30u, &e30);
        init_node(n20, &n10, &n30, &head, 20u, &e20);
        init_node(n10, &head, &n20, &head, 10u, &e10);
        init_node(n40, &head, &n30, &n50, 40u, &e40);
        init_node(n50, &head, &n40, &head, 50u, &e50);

        std::vector<void*> collected;
        const size_t visited = nativemods::Phase2Services::traverse_rb_tree_entities(&head, collected);
        CHECK(visited == 5u && collected.size() == 5u);
        CHECK(nativemods::Phase2Services::find_entity_in_rb_tree(&head, 10u) == &e10);
        CHECK(nativemods::Phase2Services::find_entity_in_rb_tree(&head, 30u) == &e30);
        CHECK(nativemods::Phase2Services::find_entity_in_rb_tree(&head, 50u) == &e50);
        CHECK(nativemods::Phase2Services::find_entity_in_rb_tree(&head, 99u) == nullptr);

        // Max-nodes bound prevents runaway traversal
        std::vector<void*> capped;
        CHECK(nativemods::Phase2Services::traverse_rb_tree_entities(&head, capped, 2u) == 2u);
        CHECK(capped.size() == 2u);
    }

    // 2. StatsComponent +0xf0 sorted vector enumeration (including high-ID >= 0x10000 custom stats)
    {
        nm_stat_entry_t entries[4] = {
            {1, 120},
            {42, 75},
            {0x10000, 999},
            {0x12345, -50},
        };
        alignas(16) uint8_t fake_stats[0x120] = {};
        const nm_stat_entry_t* begin_p = entries;
        const nm_stat_entry_t* end_p = entries + 4;
        std::memcpy(fake_stats + 0xF0u, &begin_p, sizeof(begin_p));
        std::memcpy(fake_stats + 0xF8u, &end_p, sizeof(end_p));

        uint32_t cnt = 0, total = 0;
        // Sizing query
        CHECK(nativemods::Phase2Services::read_stats_vector(fake_stats, 0xF0u, 0xF8u, nullptr, 0u, &cnt, &total) == NM_OK);
        CHECK(cnt == 0u && total == 4u);

        // Partial copy
        nm_stat_entry_t partial[2] = {};
        CHECK(nativemods::Phase2Services::read_stats_vector(fake_stats, 0xF0u, 0xF8u, partial, 2u, &cnt, &total) == NM_OK);
        CHECK(cnt == 2u && total == 4u && partial[0].stat_id == 1 && partial[1].stat_id == 42);

        // Full copy with high-ID custom stats
        nm_stat_entry_t full[8] = {};
        CHECK(nativemods::Phase2Services::read_stats_vector(fake_stats, 0xF0u, 0xF8u, full, 8u, &cnt, &total) == NM_OK);
        CHECK(cnt == 4u && total == 4u);
        CHECK(full[2].stat_id == 0x10000 && full[2].value == 999);
        CHECK(full[3].stat_id == 0x12345 && full[3].value == -50);
    }

    // 3. AreaInfo reading from ClientWorld + WorldAreasRow + InstanceClient rulesets vector
    {
        alignas(16) uint8_t fake_row[0x160] = {};
        const wchar_t* area_id_w = L"1_1_town";
        const wchar_t* area_name_w = L"Lioneye's Watch";
        const int32_t act = 1;
        const uint8_t is_town = 1;
        const int32_t base_lvl = 1;
        const uint16_t hash16 = 0x1234u;
        const uint8_t is_guild_ho = 0;
        const uint8_t is_ho = 0;
        std::memcpy(fake_row + 0x00u, &area_id_w, sizeof(area_id_w));
        std::memcpy(fake_row + 0x08u, &area_name_w, sizeof(area_name_w));
        std::memcpy(fake_row + 0x10u, &act, sizeof(act));
        std::memcpy(fake_row + 0x14u, &is_town, sizeof(is_town));
        std::memcpy(fake_row + 0x26u, &base_lvl, sizeof(base_lvl));
        std::memcpy(fake_row + 0x2Au, &hash16, sizeof(hash16));
        std::memcpy(fake_row + 0x139u, &is_guild_ho, sizeof(is_guild_ho));
        std::memcpy(fake_row + 0x14Eu, &is_ho, sizeof(is_ho));

        std::vector<uint8_t> fake_world(0xA00u, 0);
        const void* row_ptr = fake_row;
        const uint8_t eff_lvl = 68u;
        const uint32_t seed = 0xDEADBEEFu;
        std::memcpy(fake_world.data() + 0xB0u, &row_ptr, sizeof(row_ptr));
        std::memcpy(fake_world.data() + 0xD4u, &eff_lvl, sizeof(eff_lvl));
        std::memcpy(fake_world.data() + 0x8E0u, &seed, sizeof(seed));

        struct FakeRuleset {
            void*    vtable;
            uint32_t id;
            uint32_t pad;
        } rs0{nullptr, 100u, 0}, rs1{nullptr, 101u, 0}, rs2{nullptr, 200u, 0};
        void* rs_ptrs[3] = {&rs0, &rs1, &rs2};
        void* const* rs_begin = rs_ptrs;
        void* const* rs_end = rs_ptrs + 3;

        std::vector<uint8_t> fake_ic(0xD000u, 0);
        std::memcpy(fake_ic.data() + 0xCFD8u, &rs_begin, sizeof(rs_begin));
        std::memcpy(fake_ic.data() + 0xCFE0u, &rs_end, sizeof(rs_end));

        nm_area_info_t info{};
        CHECK(nativemods::Phase2Services::read_area_info_from_structs(fake_world.data(), fake_ic.data(), info));
        CHECK(info.struct_size == sizeof(nm_area_info_t));
        CHECK(info.in_game == 1u);
        CHECK(std::strcmp(info.area_id, "1_1_town") == 0);
        CHECK(std::strcmp(info.area_name, "Lioneye's Watch") == 0);
        CHECK(info.act == 1 && info.area_level == 1 && info.effective_level == 68);
        CHECK(info.hash16 == 0x1234u && info.area_seed == 0xDEADBEEFu);
        CHECK(info.is_town == 1u && info.is_hideout == 0u && info.is_guild_hideout == 0u);
        CHECK(info.ruleset_count == 3u && info.ruleset_ids[0] == 100u && info.ruleset_ids[1] == 101u && info.ruleset_ids[2] == 200u);
    }

    // 4. ItemFilterStyle extraction (Show, Hide, colors 0xRRGGBBAA, sound, icon, temp/perm beam)
    {
        alignas(8) uint8_t raw_style[0x40] = {};
        // Show + TextColor + BorderColor + BgColor
        raw_style[0] = 0x04u | 0x08u | 0x10u;
        // FontSize + AlertSound
        raw_style[1] = 0x01u | 0x02u;
        // MinimapIcon + PlayEffect (without EffectPermanent -> temporary beam)
        raw_style[2] = 0x01u | 0x02u;
        // Text RGBA (255, 0, 128, 255)
        raw_style[3] = 255; raw_style[4] = 0; raw_style[5] = 128; raw_style[6] = 255;
        // Border RGBA (10, 20, 30, 200)
        raw_style[7] = 10; raw_style[8] = 20; raw_style[9] = 30; raw_style[10] = 200;
        // Bg RGBA (0, 0, 0, 240)
        raw_style[11] = 0; raw_style[12] = 0; raw_style[13] = 0; raw_style[14] = 240;
        const int32_t sound_id = 6;
        const uint32_t sound_vol = 300;
        const uint32_t font_sz = 45;
        const int32_t icon_id = 17;
        const int32_t beam_id = 3;
        std::memcpy(raw_style + 0x10u, &sound_id, 4);
        std::memcpy(raw_style + 0x14u, &sound_vol, 4);
        std::memcpy(raw_style + 0x18u, &font_sz, 4);
        std::memcpy(raw_style + 0x1Cu, &icon_id, 4);
        std::memcpy(raw_style + 0x20u, &beam_id, 4);

        nm_item_filter_result_t res{};
        CHECK(nativemods::Phase2Services::extract_item_filter_style(raw_style, -1, res));
        CHECK(res.evaluated == 1u && res.is_visible == 1u);
        CHECK(res.text_rgba == 0xFF0080FFu);
        CHECK(res.border_rgba == 0x0A141EC8u);
        CHECK(res.bg_rgba == 0x000000F0u);
        CHECK(res.font_size == 45u);
        CHECK(res.alert_sound_id == 6 && res.alert_sound_volume == 300u);
        CHECK(res.minimap_icon_shape == 17);
        CHECK(res.beam_color == 3 && res.beam_temporary == 1u);

        // Hide rule + permanent beam
        raw_style[0] |= 0x01u;
        raw_style[2] |= 0x04u;
        CHECK(nativemods::Phase2Services::extract_item_filter_style(raw_style, -1, res));
        CHECK(res.is_visible == 0u && res.beam_temporary == 0u);

        // Visibility override (e.g. WorldItem::never_hidden lifts Hide)
        CHECK(nativemods::Phase2Services::extract_item_filter_style(raw_style, 1, res));
        CHECK(res.is_visible == 1u);
    }

    // 5. Camera row-major DirectX v_row * M WorldToScreen & FOV math
    {
        const float fov = nativemods::Phase2Services::compute_vertical_fov(0.41421357f, 0.5f);
        CHECK(std::fabs(fov - 0.78539816f) < 1e-4f); // 45 degrees == pi/4

        // Row-major matrix with translation in row 3 (M[12..15]):
        //   clip = [x, y, z, 1] * M
        //   cx = 0.5*x + 0.25, cy = 0.5*y - 0.25, cw = 1.0*z + 1.0
        const float mat[16] = {
            0.5f,  0.0f, 0.0f, 0.0f,
            0.0f,  0.5f, 0.0f, 0.0f,
            0.0f,  0.0f, 1.0f, 1.0f,
            0.25f, -0.25f, 0.0f, 1.0f,
        };
        float sx = 0.0f, sy = 0.0f;
        int32_t in_front = 0;

        // World (0, 0, 0) -> cx=0.25, cy=-0.25, cw=1.0 -> sx=960*1.25=1200, sy=540*1.25=675
        CHECK(nativemods::Phase2Services::project_world_to_screen(mat, 1920u, 1080u, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, &sx, &sy, &in_front));
        CHECK(in_front == 1 && std::fabs(sx - 1200.0f) < 1e-3f && std::fabs(sy - 675.0f) < 1e-3f);

        // Ultrawide viewport offset (120, 20) subtracted
        CHECK(nativemods::Phase2Services::project_world_to_screen(mat, 1920u, 1080u, 120.0f, 20.0f, 0.0f, 0.0f, 0.0f, &sx, &sy, &in_front));
        CHECK(in_front == 1 && std::fabs(sx - 1080.0f) < 1e-3f && std::fabs(sy - 655.0f) < 1e-3f);

        // Perspective divide: world (1.5, 2.5, 1.0) -> cx=1.0, cy=1.0, cw=2.0 -> ndc=(0.5, 0.5) -> (1440, 270)
        CHECK(nativemods::Phase2Services::project_world_to_screen(mat, 1920u, 1080u, 0.0f, 0.0f, 1.5f, 2.5f, 1.0f, &sx, &sy, &in_front));
        CHECK(in_front == 1 && std::fabs(sx - 1440.0f) < 1e-3f && std::fabs(sy - 270.0f) < 1e-3f);

        // Behind camera (z = -2.0 -> cw = -1.0 <= 1e-6f)
        CHECK(!nativemods::Phase2Services::project_world_to_screen(mat, 1920u, 1080u, 0.0f, 0.0f, 0.0f, 0.0f, -2.0f, &sx, &sy, &in_front));
        CHECK(in_front == 0);
    }

    // 6. AreaEntered / AreaLeft & FilterReloaded callbacks + exception isolation
    {
        struct AreaCap {
            int         calls = 0;
            int32_t     last_kind = 0;
            std::string last_id;
        } acap{};
        auto area_cb = [](int32_t kind, const nm_area_info_t* info, void* ud) {
            auto* c = static_cast<AreaCap*>(ud);
            ++c->calls;
            c->last_kind = kind;
            c->last_id = info->area_id;
            if (kind == 99) throw std::runtime_error("faulty area cb");
        };
        CHECK(nm_lifecycle_set_area_callback(area_cb, &acap) == NM_OK);

        nm_area_info_t ainfo{};
        ainfo.struct_size = sizeof(ainfo);
        ainfo.in_game = 1;
        std::snprintf(ainfo.area_id, sizeof(ainfo.area_id), "1_1_1");
        nm_dispatch_area_event(NM_AREA_EVT_ENTERED, &ainfo);
        CHECK(acap.calls == 1 && acap.last_kind == NM_AREA_EVT_ENTERED && acap.last_id == "1_1_1");
        nm_dispatch_area_event(NM_AREA_EVT_LEFT, &ainfo);
        CHECK(acap.calls == 2 && acap.last_kind == NM_AREA_EVT_LEFT);
        nm_dispatch_area_event(99, &ainfo); // exception isolated
        CHECK(acap.calls == 3);
        CHECK(nm_lifecycle_set_area_callback(nullptr, nullptr) == NM_OK);
        nm_dispatch_area_event(NM_AREA_EVT_ENTERED, &ainfo);
        CHECK(acap.calls == 3);

        struct FilterCap {
            int         calls = 0;
            int32_t     last_ok = -1;
            std::string last_name;
        } fcap{};
        auto flt_cb = [](int32_t ok, const nm_item_filter_state_t* st, void* ud) {
            auto* c = static_cast<FilterCap*>(ud);
            ++c->calls;
            c->last_ok = ok;
            c->last_name = st->filter_name;
            if (ok == 99) throw std::runtime_error("faulty filter cb");
        };
        CHECK(nm_filter_set_reload_callback(flt_cb, &fcap) == NM_OK);

        nm_item_filter_state_t fst{};
        fst.struct_size = sizeof(fst);
        fst.loaded = 1;
        std::snprintf(fst.filter_name, sizeof(fst.filter_name), "NeverSink.filter");
        nm_dispatch_filter_reloaded(1, &fst);
        CHECK(fcap.calls == 1 && fcap.last_ok == 1 && fcap.last_name == "NeverSink.filter");
        nm_dispatch_filter_reloaded(0, &fst);
        CHECK(fcap.calls == 2 && fcap.last_ok == 0);
        nm_dispatch_filter_reloaded(99, &fst); // exception isolated
        CHECK(fcap.calls == 3);
    }

    // 7. Null-arg / NaN validation & synthetic platform checks for all 18 ABI 0.4.0 entry points
    {
        nm_world_entity_snapshot_t ent{};
        uint32_t cnt = 0, total = 0;
        uint64_t handle = 0;
        int32_t val = 0;
        nm_area_info_t ainfo{};
        nm_item_filter_state_t fst{};
        nm_item_filter_result_t fres{};
        nm_camera_state_t cam{};
        float sx = 0.0f, sy = 0.0f;
        int32_t front = 0;

        CHECK(nm_world_get_local_player(nullptr) == NM_ERR_INVALID_ARG);
        CHECK(nm_world_get_local_player(&ent) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);
        CHECK(nm_world_list_entities(nullptr, nullptr, 0, nullptr, &total) == NM_ERR_INVALID_ARG);
        CHECK(nm_world_list_entities(nullptr, nullptr, 1, &cnt, &total) == NM_ERR_INVALID_ARG);
        CHECK(nm_world_list_entities(nullptr, nullptr, 0, &cnt, &total) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);
        CHECK(nm_world_get_entity(1, nullptr) == NM_ERR_INVALID_ARG);
        CHECK(nm_world_get_entity(1, &ent) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);
        CHECK(nm_world_create_client_object(nullptr, 0, 0, 0, &handle) == NM_ERR_INVALID_ARG);
        CHECK(nm_world_create_client_object("Metadata/Effects/Effect", 1, 2, 3, &handle) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);
        CHECK(nm_world_remove_client_object(0) == NM_ERR_INVALID_ARG);
        CHECK(nm_world_remove_client_object(1) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);

        CHECK(nm_stats_read_entity(0, 1, nullptr) == NM_ERR_INVALID_ARG);
        CHECK(nm_stats_read_entity(0, 1, &val) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);
        CHECK(nm_stats_set_local_entity(0, 1, 42) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);
        CHECK(nm_stats_list_entity(0, nullptr, 0, nullptr, &total) == NM_ERR_INVALID_ARG);
        CHECK(nm_stats_list_entity(0, nullptr, 0, &cnt, &total) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);

        CHECK(nm_lifecycle_get_area_info(nullptr) == NM_ERR_INVALID_ARG);
        CHECK(nm_lifecycle_get_area_info(&ainfo) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);

        CHECK(nm_filter_get_state(nullptr) == NM_ERR_INVALID_ARG);
        CHECK(nm_filter_get_state(&fst) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);
        CHECK(nm_filter_reload() == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);
        CHECK(nm_filter_get_world_item_result(1, nullptr) == NM_ERR_INVALID_ARG);
        CHECK(nm_filter_get_world_item_result(1, &fres) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);
        CHECK(nm_filter_evaluate_entity(1, nullptr) == NM_ERR_INVALID_ARG);
        CHECK(nm_filter_evaluate_entity(1, &fres) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);

        CHECK(nm_camera_get_state(nullptr) == NM_ERR_INVALID_ARG);
        CHECK(nm_camera_get_state(&cam) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);
        CHECK(nm_camera_world_to_screen(0, 0, 0, nullptr, &sy, &front) == NM_ERR_INVALID_ARG);
        CHECK(nm_camera_world_to_screen(0, 0, 0, &sx, &sy, &front) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);
        CHECK(nm_camera_set_zoom_override(NAN) == NM_ERR_INVALID_ARG);
        CHECK(nm_camera_set_zoom_override(INFINITY) == NM_ERR_INVALID_ARG);
        CHECK(nm_camera_set_zoom_override(1.5f) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);
    }

    CHECK(nm_shutdown() == NM_OK);
    ++g_tests_passed;
    return 0;
}

namespace {
using TenArgTestFn = uint64_t(NM_CALL*)(
    uint64_t a0, uint64_t a1, uint64_t a2, uint64_t a3,
    uint64_t a4, uint64_t a5, uint64_t a6, uint64_t a7,
    uint64_t a8, uint64_t a9);

std::atomic<TenArgTestFn> g_ten_arg_trampoline{nullptr};
uint64_t                  g_ten_arg_orig_seen[10] = {};
uint64_t                  g_ten_arg_detour_seen[10] = {};
std::atomic<int>          g_ten_arg_detour_calls{0};
std::atomic<bool>         g_ten_arg_post_guarded_ran{false};
thread_local bool         t_ten_arg_in_detour = false;

NM_NOINLINE uint64_t NM_CALL ten_arg_target_fn(
    uint64_t a0, uint64_t a1, uint64_t a2, uint64_t a3,
    uint64_t a4, uint64_t a5, uint64_t a6, uint64_t a7,
    uint64_t a8, uint64_t a9) {
    g_ten_arg_orig_seen[0] = a0;
    g_ten_arg_orig_seen[1] = a1;
    g_ten_arg_orig_seen[2] = a2;
    g_ten_arg_orig_seen[3] = a3;
    g_ten_arg_orig_seen[4] = a4;
    g_ten_arg_orig_seen[5] = a5;
    g_ten_arg_orig_seen[6] = a6;
    g_ten_arg_orig_seen[7] = a7;
    g_ten_arg_orig_seen[8] = a8;
    g_ten_arg_orig_seen[9] = a9;
    return a0 * 1ull + a1 * 3ull + a2 * 7ull + a3 * 11ull +
           a4 * 13ull + a5 * 17ull + a6 * 19ull + a7 * 23ull +
           a8 * 29ull + a9 * 31ull;
}

uint64_t NM_CALL ten_arg_detour_fn(
    uint64_t a0, uint64_t a1, uint64_t a2, uint64_t a3,
    uint64_t a4, uint64_t a5, uint64_t a6, uint64_t a7,
    uint64_t a8, uint64_t a9) {
    g_ten_arg_detour_seen[0] = a0;
    g_ten_arg_detour_seen[1] = a1;
    g_ten_arg_detour_seen[2] = a2;
    g_ten_arg_detour_seen[3] = a3;
    g_ten_arg_detour_seen[4] = a4;
    g_ten_arg_detour_seen[5] = a5;
    g_ten_arg_detour_seen[6] = a6;
    g_ten_arg_detour_seen[7] = a7;
    g_ten_arg_detour_seen[8] = a8;
    g_ten_arg_detour_seen[9] = a9;
    g_ten_arg_detour_calls.fetch_add(1, std::memory_order_relaxed);

    const auto orig = g_ten_arg_trampoline.load(std::memory_order_acquire);
    const uint64_t ret = orig ? orig(a0, a1, a2, a3, a4, a5, a6, a7, a8, a9) : 0ull;

    if (!t_ten_arg_in_detour) {
        struct Guard {
            Guard() noexcept { t_ten_arg_in_detour = true; }
            ~Guard() noexcept { t_ten_arg_in_detour = false; }
        } guard;
        auto post_fn = [](void*) -> int64_t {
            g_ten_arg_post_guarded_ran.store(true, std::memory_order_release);
            return 0;
        };
        nm_exception_info_t ex{};
        (void)nativemods::invoke_guarded(post_fn, nullptr, nullptr, &ex);
    }

    return ret;
}

using TailCheckFn = int(NM_CALL*)(int);
std::atomic<TailCheckFn> g_tail_check_tramp{nullptr};
std::atomic<int>         g_tail_check_raw_hits{0};

int NM_CALL tail_check_raw_detour(int x) {
    g_tail_check_raw_hits.fetch_add(1, std::memory_order_relaxed);
    const auto orig = g_tail_check_tramp.load(std::memory_order_acquire);
    return orig ? (orig(x) + 1000) : 0;
}
} // namespace

int test_multichain_expected_bytes_longer_than_stolen() {
    using nativemods::HookEngine;
    CHECK(nm_initialize_for_build(NM_BUILD_SYNTHETIC, 0x10000000ull, 0x10000u, nullptr) == NM_OK);

    // Synthesize a function whose exact stolen_len is 15 bytes (12x NOP + 3B `mov rax, rax`),
    // followed by 4 live unpatched tail bytes (`lea eax, [arg0 + 7] ; ret`), mirroring
    // frame.ingame_state_leave (15B stolen prologue, 16B+ expectedBytes).
    void* code_page = HookEngine::alloc_exec_page(4096);
    CHECK(code_page != nullptr);
    auto* code = static_cast<uint8_t*>(code_page);
    std::memset(code, 0x90, 12);
    code[12] = 0x48u; // mov rax, rax (3 bytes -> total stolen_len == 15)
    code[13] = 0x89u;
    code[14] = 0xC0u;
#if defined(_WIN32)
    // lea eax, [rcx + 7] ; ret
    code[15] = 0x8Du; code[16] = 0x41u; code[17] = 0x07u; code[18] = 0xC3u;
    const char* kFullHex         = "9090909090909090909090904889c08d4107c3";
    const char* kWildcardCross   = "9090909090909090909090904889????4107c3";
    const char* kBadPrefixByte0  = "9190909090909090909090904889c08d4107c3";
    const char* kBadPrefixByte14 = "9090909090909090909090904889c18d4107c3";
    const char* kBadTailByte15   = "9090909090909090909090904889c08e4107c3";
    const char* kBadTailByte18   = "9090909090909090909090904889c08d4107c2";
    const char* kBadHexTail      = "9090909090909090909090904889c08d4107zz";
    FlushInstructionCache(GetCurrentProcess(), code_page, 4096);
#else
    // lea eax, [rdi + 7] ; ret
    code[15] = 0x8Du; code[16] = 0x47u; code[17] = 0x07u; code[18] = 0xC3u;
    const char* kFullHex         = "9090909090909090909090904889c08d4707c3";
    const char* kWildcardCross   = "9090909090909090909090904889????4707c3";
    const char* kBadPrefixByte0  = "9190909090909090909090904889c08d4707c3";
    const char* kBadPrefixByte14 = "9090909090909090909090904889c18d4707c3";
    const char* kBadTailByte15   = "9090909090909090909090904889c08e4707c3";
    const char* kBadTailByte18   = "9090909090909090909090904889c08d4707c2";
    const char* kBadHexTail      = "9090909090909090909090904889c08d4707zz";
    __builtin___clear_cache(static_cast<char*>(code_page), static_cast<char*>(code_page) + 4096);
#endif

    CHECK(HookEngine::compute_stolen_length(code, HookEngine::kAbsJumpSize) == 15u);
    auto target_fn = reinterpret_cast<TailCheckFn>(code_page);
    CHECK(target_fn(10) == 17);

    const uint64_t target_addr = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(code_page));
    auto pre_cb = [](nm_hook_context_t* ctx, void*) { ctx->args[0] += 3; };
    auto post_cb1 = [](nm_hook_context_t* ctx, void*) { ctx->return_value += 100; };
    auto post_cb2 = [](nm_hook_context_t* ctx, void*) { ctx->return_value += 20; };

    nm_hook_handle_t h1 = NM_HOOK_INVALID;
    nm_hook_handle_t h2 = NM_HOOK_INVALID;
    nm_hook_handle_t h3 = NM_HOOK_INVALID;
    nm_hook_handle_t h_bad = NM_HOOK_INVALID;
    nm_hook_handle_t h_raw = NM_HOOK_INVALID;

    // 1st chain hook attaches cleanly with 19-byte expected_hex (> 15B stolen_len)
    CHECK(nm_install_inline_chain_hook(target_addr, kFullHex, NM_HOOK_PHASE_PRE, 0, pre_cb, nullptr, &h1) == NM_OK);
    CHECK(h1 != NM_HOOK_INVALID);

    // 2nd chain hook on already-chained target with same 19-byte expected_hex MUST succeed!
    CHECK(nm_install_inline_chain_hook(target_addr, kFullHex, NM_HOOK_PHASE_POST, 0, post_cb1, nullptr, &h2) == NM_OK);
    CHECK(h2 != NM_HOOK_INVALID);

    // 3rd chain hook with wildcards straddling the stolen/tail boundary (bytes 14 & 15) MUST succeed!
    CHECK(nm_install_inline_chain_hook(target_addr, kWildcardCross, NM_HOOK_PHASE_POST, 1, post_cb2, nullptr, &h3) == NM_OK);
    CHECK(h3 != NM_HOOK_INVALID);

    // Real mismatches in either saved prefix [0..14] or live unpatched tail [15..18] MUST be rejected!
    CHECK(nm_install_inline_chain_hook(target_addr, kBadPrefixByte0, NM_HOOK_PHASE_POST, 2, post_cb2, nullptr, &h_bad) == NM_ERR_BYTE_MISMATCH);
    CHECK(nm_install_inline_chain_hook(target_addr, kBadPrefixByte14, NM_HOOK_PHASE_POST, 2, post_cb2, nullptr, &h_bad) == NM_ERR_BYTE_MISMATCH);
    CHECK(nm_install_inline_chain_hook(target_addr, kBadTailByte15, NM_HOOK_PHASE_POST, 2, post_cb2, nullptr, &h_bad) == NM_ERR_BYTE_MISMATCH);
    CHECK(nm_install_inline_chain_hook(target_addr, kBadTailByte18, NM_HOOK_PHASE_POST, 2, post_cb2, nullptr, &h_bad) == NM_ERR_BYTE_MISMATCH);
    // Malformed hex on already-chained target MUST return NM_ERR_INVALID_ARG
    CHECK(nm_install_inline_chain_hook(target_addr, "909", NM_HOOK_PHASE_POST, 2, post_cb2, nullptr, &h_bad) == NM_ERR_INVALID_ARG);
    CHECK(nm_install_inline_chain_hook(target_addr, kBadHexTail, NM_HOOK_PHASE_POST, 2, post_cb2, nullptr, &h_bad) == NM_ERR_INVALID_ARG);

    // Raw inline detour attaching to the same already-chained target with 19-byte expected_hex MUST also succeed
    void* raw_tramp = nullptr;
    g_tail_check_raw_hits.store(0, std::memory_order_relaxed);
    CHECK(nm_install_inline_hook_at(target_addr, kFullHex, reinterpret_cast<void*>(&tail_check_raw_detour), &raw_tramp, &h_raw) == NM_OK);
    CHECK(raw_tramp != nullptr && h_raw != NM_HOOK_INVALID);
    g_tail_check_tramp.store(reinterpret_cast<TailCheckFn>(raw_tramp), std::memory_order_release);

    // End-to-end call: ((10 + 3) + 7) + 100 + 20 + 1000 = 1140
    CHECK(target_fn(10) == 1140);
    CHECK(g_tail_check_raw_hits.load(std::memory_order_relaxed) == 1);

    CHECK(nm_remove_hook(h_raw) == NM_OK);
    CHECK(nm_remove_hook(h3) == NM_OK);
    CHECK(nm_remove_hook(h2) == NM_OK);
    CHECK(nm_remove_hook(h1) == NM_OK);
    g_tail_check_tramp.store(nullptr, std::memory_order_release);
    CHECK(target_fn(10) == 17);

    CHECK(nm_shutdown() == NM_OK);
    HookEngine::free_exec_page(code_page, 4096);
    ++g_tests_passed;
    return 0;
}

int test_ten_arg_raw_inline_detour() {
    CHECK(nm_initialize_for_build(NM_BUILD_SYNTHETIC, 0x10000000ull, 0x10000u, nullptr) == NM_OK);

    const uint64_t args[10] = {
        0x1000000000000001ull,
        0x2000000000000002ull,
        0x3000000000000003ull,
        0x4000000000000004ull,
        0x0000000500000005ull,
        0x0000000600000006ull,
        0x0000000700000007ull,
        0x0000000800000008ull,
        0x0000000900000009ull,
        0x0000000A0000000Aull,
    };
    const uint64_t expected_ret =
        args[0] * 1ull + args[1] * 3ull + args[2] * 7ull + args[3] * 11ull +
        args[4] * 13ull + args[5] * 17ull + args[6] * 19ull + args[7] * 23ull +
        args[8] * 29ull + args[9] * 31ull;

    volatile TenArgTestFn call_fn = &ten_arg_target_fn;
    std::memset(g_ten_arg_orig_seen, 0, sizeof(g_ten_arg_orig_seen));
    std::memset(g_ten_arg_detour_seen, 0, sizeof(g_ten_arg_detour_seen));
    g_ten_arg_detour_calls.store(0, std::memory_order_relaxed);
    g_ten_arg_post_guarded_ran.store(false, std::memory_order_relaxed);
    g_ten_arg_trampoline.store(nullptr, std::memory_order_release);

    // Baseline unhooked call
    CHECK(call_fn(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9]) == expected_ret);
    for (int i = 0; i < 10; ++i) {
        CHECK(g_ten_arg_orig_seen[i] == args[i]);
    }

    // Install typed 10-arg raw inline detour
    void* tramp = nullptr;
    nm_hook_handle_t handle = NM_HOOK_INVALID;
    CHECK(nm_install_inline_hook_at(
              static_cast<uint64_t>(reinterpret_cast<uintptr_t>(&ten_arg_target_fn)),
              nullptr,
              reinterpret_cast<void*>(&ten_arg_detour_fn),
              &tramp,
              &handle) == NM_OK);
    CHECK(tramp != nullptr && handle != NM_HOOK_INVALID);
    g_ten_arg_trampoline.store(reinterpret_cast<TenArgTestFn>(tramp), std::memory_order_release);

    std::memset(g_ten_arg_orig_seen, 0, sizeof(g_ten_arg_orig_seen));
    std::memset(g_ten_arg_detour_seen, 0, sizeof(g_ten_arg_detour_seen));

    // Hooked call MUST forward all 10 arguments (including stack args a4..a9) and preserve 64-bit return value!
    const uint64_t hooked_ret = call_fn(
        args[0], args[1], args[2], args[3], args[4],
        args[5], args[6], args[7], args[8], args[9]);
    CHECK(hooked_ret == expected_ret);
    CHECK(g_ten_arg_detour_calls.load(std::memory_order_relaxed) == 1);
    CHECK(g_ten_arg_post_guarded_ran.load(std::memory_order_acquire));
    for (int i = 0; i < 10; ++i) {
        CHECK(g_ten_arg_detour_seen[i] == args[i]);
        CHECK(g_ten_arg_orig_seen[i] == args[i]);
    }

    // Uninstall restores original cleanly
    CHECK(nm_remove_hook(handle) == NM_OK);
    g_ten_arg_trampoline.store(nullptr, std::memory_order_release);
    std::memset(g_ten_arg_orig_seen, 0, sizeof(g_ten_arg_orig_seen));
    CHECK(call_fn(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9]) == expected_ret);
    CHECK(g_ten_arg_detour_calls.load(std::memory_order_relaxed) == 1);
    for (int i = 0; i < 10; ++i) {
        CHECK(g_ten_arg_orig_seen[i] == args[i]);
    }

    CHECK(nm_shutdown() == NM_OK);
    ++g_tests_passed;
    return 0;
}

} // namespace

int main() {
    if (test_abi_table_and_build_recognition() != 0) return 1;
    if (test_address_table_3293d_and_3293c() != 0) return 1;
    if (test_byte_verification_wildcards() != 0) return 1;
    if (test_inline_and_vtable_hooks() != 0) return 1;
    if (test_exception_wall() != 0) return 1;
    if (test_game_allocator_header_abi() != 0) return 1;
    if (test_frame_queue_concurrency_and_isolation() != 0) return 1;
    if (test_file_overlay_and_channels() != 0) return 1;
    if (test_prologue_length_and_sha256() != 0) return 1;
    if (test_disk_overlays_audio_and_wire_encoders() != 0) return 1;
    if (test_overlay_hash_prefilter_and_connection_token() != 0) return 1;
    if (test_x64_decoder_and_rip_relocation() != 0) return 1;
    if (test_multimod_pre_post_hook_chains() != 0) return 1;
    if (test_guarded_8arg_int_float_calls() != 0) return 1;
    if (test_utf16_extensions_nobom_and_overlay_reclaim() != 0) return 1;
    if (test_relocate_internal_jump_over_2gb() != 0) return 1;
    if (test_raw_detour_self_removal() != 0) return 1;
    if (test_concurrent_vtable_chain_install_remove_stress() != 0) return 1;
    if (test_short_function_and_midinsn_relocate_rejection() != 0) return 1;
    if (test_chained_hook_exception_unwind_and_active_calls() != 0) return 1;
    if (test_poe_bom_stripping_and_net_bounds() != 0) return 1;
    if (test_live_ui_vtable_mismatch_guards() != 0) return 1;
    if (test_phase1_prologues_hookengine_safety() != 0) return 1;
    if (test_phase1_input_screens_hover_and_pregame_callbacks() != 0) return 1;
    if (test_phase2_prologues_hookengine_safety() != 0) return 1;
    if (test_phase2_world_rbtree_stats_area_filter_camera() != 0) return 1;
    if (test_multichain_expected_bytes_longer_than_stolen() != 0) return 1;
    if (test_ten_arg_raw_inline_detour() != 0) return 1;

    std::printf("ALL %d NATIVEMODS C++20 SHIM TESTS PASSED!\n", g_tests_passed);
    return 0;
}
