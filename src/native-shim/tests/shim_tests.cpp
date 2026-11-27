#include "nativemods_abi.h"
#include "address_table.hpp"
#include "exception_wall.hpp"
#include "game_allocator.hpp"
#include "hook_engine.hpp"
#include "live_engine_hooks.hpp"
#include "overlay_and_channels.hpp"
#include "phase1_services.hpp"
#include "phase2_services.hpp"
#include "phase4_services.hpp"
#include "phase5_services.hpp"
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
    CHECK(sizeof(nm_abi_table_t) == 1168u);
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
    CHECK(sizeof(nm_affinity_target_t) == 4u);
    CHECK(sizeof(nm_affinity_route_result_t) == 32u);
    CHECK(sizeof(nm_native_ruleset_callbacks_t) == 64u);
    CHECK(sizeof(nm_custom_ui_control_callbacks_t) == 32u);
    CHECK(table->abi_version == NM_ABI_VERSION);
    CHECK(NM_ABI_VERSION == 0x00000600u);
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
    CHECK(table->ui_create_control == &nm_ui_create_control);
    CHECK(table->ui_picture_set_texture == &nm_ui_picture_set_texture);
    CHECK(table->ui_scrollable_get_scroll == &nm_ui_scrollable_get_scroll);
    CHECK(table->ui_scrollable_set_scroll == &nm_ui_scrollable_set_scroll);
    CHECK(table->ui_scrollable_get_content == &nm_ui_scrollable_get_content);
    CHECK(table->ui_tab_container_get_count == &nm_ui_tab_container_get_count);
    CHECK(table->ui_tab_container_get_active == &nm_ui_tab_container_get_active);
    CHECK(table->ui_tab_container_select == &nm_ui_tab_container_select);
    CHECK(table->ui_tab_container_add_tab == &nm_ui_tab_container_add_tab);
    CHECK(table->ui_tab_container_subscribe_changed == &nm_ui_tab_container_subscribe_changed);
    CHECK(table->ui_combobox_get_count == &nm_ui_combobox_get_count);
    CHECK(table->ui_combobox_get_selected == &nm_ui_combobox_get_selected);
    CHECK(table->ui_combobox_set_selected == &nm_ui_combobox_set_selected);
    CHECK(table->ui_combobox_add_item == &nm_ui_combobox_add_item);
    CHECK(table->ui_combobox_clear == &nm_ui_combobox_clear);
    CHECK(table->ui_combobox_subscribe_changed == &nm_ui_combobox_subscribe_changed);
    CHECK(table->ui_listbox_get_count == &nm_ui_listbox_get_count);
    CHECK(table->ui_listbox_get_selected == &nm_ui_listbox_get_selected);
    CHECK(table->ui_listbox_set_selected == &nm_ui_listbox_set_selected);
    CHECK(table->ui_listbox_insert_item == &nm_ui_listbox_insert_item);
    CHECK(table->ui_listbox_clear == &nm_ui_listbox_clear);
    CHECK(table->ui_listbox_subscribe_changed == &nm_ui_listbox_subscribe_changed);
    CHECK(table->tooltip_set_item_augment_callback == &nm_tooltip_set_item_augment_callback);
    CHECK(table->tooltip_add_item_section == &nm_tooltip_add_item_section);
    CHECK(table->affinity_set_router_callback == &nm_affinity_set_router_callback);
    CHECK(table->geal_register_builtin == &nm_geal_register_builtin);
    CHECK(table->geal_unregister_builtin == &nm_geal_unregister_builtin);
    CHECK(table->geal_invoke_function == &nm_geal_invoke_function);
    CHECK(table->ruleset_register_native == &nm_ruleset_register_native);
    CHECK(table->ruleset_unregister_native == &nm_ruleset_unregister_native);
    CHECK(table->ui_register_custom_control_kind == &nm_ui_register_custom_control_kind);
    CHECK(table->ui_unregister_custom_control_kind == &nm_ui_unregister_custom_control_kind);
    CHECK(table->stack_set_u32_wire_enabled == &nm_stack_set_u32_wire_enabled);
    CHECK(table->stack_set_format_millions_enabled == &nm_stack_set_format_millions_enabled);
    CHECK(table->stack_set_entity_override == &nm_stack_set_entity_override);
    CHECK(table->ui_set_compact_sockets_enabled == &nm_ui_set_compact_sockets_enabled);
    CHECK(table->world_play_effect == &nm_world_play_effect);
    CHECK(table->world_create_client_object_ex == &nm_world_create_client_object_ex);

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
    CHECK(host.symbol_count == 447u);

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

    // Phase 4 verified symbols on 3.29.3d (Rich UI, Tooltips, Stash Affinity, GEAL, Rulesets, Custom UI)
    CHECK(nm_lookup_symbol("ui.tab_container_add_tab", &sym) == NM_OK);
    CHECK(sym.rva == 0x18de750ull && sym.kind == NM_SYM_CODE);
    CHECK(nm_lookup_symbol("ui.control_listbox_insert_item", &sym) == NM_OK);
    CHECK(sym.rva == 0x1904470ull && sym.kind == NM_SYM_CODE);
    CHECK(nm_lookup_symbol("ui.item_popup_build_sections", &sym) == NM_OK);
    CHECK(sym.rva == 0x16c5740ull && sym.kind == NM_SYM_CODE);
    CHECK(nm_lookup_symbol("ui.stash_get_affinity_targets", &sym) == NM_OK);
    CHECK(sym.rva == 0x0a83460ull && sym.kind == NM_SYM_CODE);
    CHECK(nm_lookup_symbol("items.compute_affinity_bitset", &sym) == NM_OK);
    CHECK(sym.rva == 0x1589b20ull && sym.kind == NM_SYM_CODE);
    CHECK(nm_lookup_symbol("network.vtable_packet_move_affinity_0xe1", &sym) == NM_OK);
    CHECK(sym.rva == 0x35caa80ull && sym.kind == NM_SYM_VTABLE && sym.expected_bytes_hex == nullptr);
    CHECK(nm_lookup_symbol("geal.arg_name_hash_table", &sym) == NM_OK);
    CHECK(sym.rva == 0x2fca088ull && sym.kind == NM_SYM_DATA && sym.expected_bytes_hex == nullptr);
    CHECK(nm_lookup_symbol("rulesets.default_slot6_sentinel", &sym) == NM_OK);
    CHECK(sym.rva == 0x135d510ull && sym.kind == NM_SYM_CODE);
    CHECK(nm_lookup_symbol("ui.control_factory_map_global", &sym) == NM_OK);
    CHECK(sym.rva == 0x475e990ull && sym.kind == NM_SYM_DATA && sym.expected_bytes_hex == nullptr);
    CHECK(nm_lookup_symbol("ui.parser_parse_property", &sym) == NM_OK);
    CHECK(sym.rva == 0x1872270ull && sym.kind == NM_SYM_CODE);
    CHECK(nm_lookup_symbol("ui.control_release_or_destroy", &sym) == NM_OK);
    CHECK(sym.rva == 0x18b6810ull && sym.kind == NM_SYM_CODE);

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
    CHECK(host.symbol_count == 447u);
    CHECK(nm_lookup_symbol("ui.item_popup_build_sections", &sym) == NM_ERR_SYMBOL_UNVERIFIED);
    CHECK(sym.rva == 0ull && sym.status == NM_SYM_STATUS_UNVERIFIED);
    CHECK(nm_lookup_symbol("ui.control_factory_map_global", &sym) == NM_ERR_SYMBOL_UNVERIFIED);
    CHECK(sym.rva == 0ull && sym.status == NM_SYM_STATUS_UNVERIFIED);
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

int test_phase4_prologues_hookengine_safety() {
    using nativemods::EmbeddedSymbol;
    using nativemods::HookEngine;
    using nativemods::find_symbol;

    const char* hooked_syms[] = {
        "ui.tab_container_select",
        "ui.combobox_on_selection_changed",
        "ui.control_listbox_set_selected_index",
        "ui.item_popup_build_sections",
        "items.compute_affinity_bitset",
        "ui.stash_get_affinity_targets",
        "ui.parser_parse_property",
        "ui.control_release_or_destroy",
    };

    for (const char* name : hooked_syms) {
        const EmbeddedSymbol* sym = find_symbol(NM_BUILD_3_29_3D_328228, name);
        CHECK(sym != nullptr);
        CHECK(sym->rva != 0);
        CHECK(sym->expected_bytes_hex != nullptr);
        const size_t hex_len = std::strlen(sym->expected_bytes_hex);
        CHECK(hex_len == 32u); // 16B verified prologue
        uint8_t buf[64] = {};
        std::memset(buf, 0x90, sizeof(buf)); // pad tail with NOPs for kMaxScan=64 safety under ASan
        for (size_t i = 0; i < 16u; ++i) {
            auto nib = [](char c) -> uint8_t {
                if (c >= '0' && c <= '9') return static_cast<uint8_t>(c - '0');
                if (c >= 'a' && c <= 'f') return static_cast<uint8_t>(c - 'a' + 10);
                return static_cast<uint8_t>(c - 'A' + 10);
            };
            buf[i] = static_cast<uint8_t>((nib(sym->expected_bytes_hex[i * 2]) << 4) |
                                          nib(sym->expected_bytes_hex[i * 2 + 1]));
        }
        const size_t stolen = HookEngine::compute_stolen_length(buf, 14u);
        CHECK(stolen >= 14u && stolen <= 16u);
    }

    ++g_tests_passed;
    return 0;
}

int test_phase4_rich_ui_tooltips_and_stash_affinity() {
    CHECK(nm_initialize_for_build(NM_BUILD_SYNTHETIC, 0x10000000ull, 0x10000u, nullptr) == NM_OK);

    // 1. Rich UI argument validation & synthetic platform checks
    void* ctrl = nullptr;
    CHECK(nm_ui_create_control(-1, nullptr, "bad", &ctrl) == NM_ERR_INVALID_ARG);
    CHECK(nm_ui_create_control(99, nullptr, "bad", &ctrl) == NM_ERR_INVALID_ARG);
    CHECK(nm_ui_create_control(NM_UI_CTRL_FRAME, nullptr, "f", nullptr) == NM_ERR_INVALID_ARG);
    CHECK(nm_ui_create_control(NM_UI_CTRL_TAB_CONTAINER, nullptr, "tabs", &ctrl) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);

    uint8_t fake_scroll[64] = {};
    void* scroll_content = nullptr;
    CHECK(nm_ui_scrollable_get_content(fake_scroll, &scroll_content) == NM_OK);
    CHECK(scroll_content == fake_scroll);

    // TabContainer / ComboBox / ListBox subscriptions + nm_ui_unsubscribe
    uint8_t fake_tab[32] = {};
    uint8_t fake_combo[32] = {};
    uint8_t fake_lb[32] = {};
    struct UiEvtState {
        void*   last_ctrl = nullptr;
        int32_t last_val = -999;
        int     count = 0;
    } ui_st{};
    auto on_ui_int = [](void* c, int32_t val, void* ud) {
        auto* s = static_cast<UiEvtState*>(ud);
        s->last_ctrl = c;
        s->last_val = val;
        ++s->count;
    };
    uint64_t tab_sub = 0, combo_sub = 0, lb_sub = 0;
    CHECK(nm_ui_tab_container_subscribe_changed(fake_tab, on_ui_int, &ui_st, &tab_sub) == NM_OK);
    CHECK(nm_ui_combobox_subscribe_changed(fake_combo, on_ui_int, &ui_st, &combo_sub) == NM_OK);
    CHECK(nm_ui_listbox_subscribe_changed(fake_lb, on_ui_int, &ui_st, &lb_sub) == NM_OK);
    CHECK(tab_sub != 0 && combo_sub != 0 && lb_sub != 0);

    nativemods::Phase4Services helper;
    helper.dispatch_ui_int_event(NM_UI_CTRL_TAB_CONTAINER, fake_tab, 2);
    CHECK(ui_st.count == 1 && ui_st.last_ctrl == fake_tab && ui_st.last_val == 2);
    helper.dispatch_ui_int_event(NM_UI_CTRL_COMBOBOX, fake_combo, 5);
    CHECK(ui_st.count == 2 && ui_st.last_ctrl == fake_combo && ui_st.last_val == 5);
    helper.dispatch_ui_int_event(NM_UI_CTRL_LISTBOX, fake_lb, 7);
    CHECK(ui_st.count == 3 && ui_st.last_ctrl == fake_lb && ui_st.last_val == 7);

    // Unified nm_ui_unsubscribe removes Phase 4 rich UI subscriptions cleanly
    CHECK(nm_ui_unsubscribe(tab_sub) == NM_OK);
    CHECK(nm_ui_unsubscribe(combo_sub) == NM_OK);
    CHECK(nm_ui_unsubscribe(lb_sub) == NM_OK);
    helper.dispatch_ui_int_event(NM_UI_CTRL_TAB_CONTAINER, fake_tab, 3);
    CHECK(ui_st.count == 3);

    // 2. Tooltip augmentation plumbing (normal hover & Alt-held rebuild)
    struct TipState {
        void*       popup = nullptr;
        std::string item_path;
        int32_t     alt_held = -1;
        int         calls = 0;
    } tip_st{};
    auto on_tip = [](void* popup, const nm_item_snapshot_t* item, int32_t alt_held, void* ud) {
        auto* s = static_cast<TipState*>(ud);
        s->popup = popup;
        s->item_path = item ? item->metadata_path : "";
        s->alt_held = alt_held;
        ++s->calls;
    };
    CHECK(nm_tooltip_set_item_augment_callback(on_tip, &tip_st) == NM_OK);
    nm_item_snapshot_t snap{};
    snap.present = 1;
    std::snprintf(snap.metadata_path, sizeof(snap.metadata_path), "%s", "Metadata/Items/Currency/CurrencyRerollRare");
    uint8_t fake_popup[32] = {};
    nm_dispatch_tooltip_augment(fake_popup, &snap, 0);
    CHECK(tip_st.calls == 1 && tip_st.popup == fake_popup && tip_st.alt_held == 0);
    CHECK(tip_st.item_path == "Metadata/Items/Currency/CurrencyRerollRare");
    nm_dispatch_tooltip_augment(fake_popup, &snap, 1);
    CHECK(tip_st.calls == 2 && tip_st.alt_held == 1);
    CHECK(nm_tooltip_set_item_augment_callback(nullptr, nullptr) == NM_OK);
    nm_dispatch_tooltip_augment(fake_popup, &snap, 1);
    CHECK(tip_st.calls == 2);

    // 3. Stash affinity router (bitset bits 0..21 clamping + OVERRIDE / APPEND targets)
    auto on_aff = [](const nm_item_snapshot_t* /*item*/, int32_t stash_kind, nm_affinity_route_result_t* out, void* /*ud*/) -> int32_t {
        if (stash_kind == -1) {
            out->stock_affinity_bitmask = (1u << 4) | (1u << 21) | (1u << 28); // bit 28 must be masked out!
            return 1;
        }
        out->action = (stash_kind == 0) ? NM_AFFINITY_ACTION_OVERRIDE : NM_AFFINITY_ACTION_APPEND;
        out->target_count = 2;
        out->targets[0] = nm_affinity_target_t{42u, 0xFEu, 0u};
        out->targets[1] = nm_affinity_target_t{99u, 0xFFu, 0u};
        return 1;
    };
    CHECK(nm_affinity_set_router_callback(on_aff, nullptr) == NM_OK);

    nm_affinity_route_result_t bit_res{};
    CHECK(nm_dispatch_affinity_route(&snap, -1, &bit_res) == 1);
    uint8_t bitset7[7] = {0x01u, 0x00u, 0xC0u, 0x55u, 0xAAu, 0x11u, 0x22u};
    nativemods::Phase4Services::apply_affinity_bitset_mask(bitset7, bit_res.stock_affinity_bitmask);
    CHECK(bitset7[0] == (0x01u | 0x10u)); // bit 0 preserved + bit 4 set
    CHECK(bitset7[1] == 0x00u);
    CHECK(bitset7[2] == (0xC0u | 0x20u)); // upper 2 bits preserved + bit 21 set
    CHECK(bitset7[3] == 0x55u && bitset7[6] == 0x22u); // locker bytes untouched!

    nm_affinity_route_result_t over_res{};
    CHECK(nm_dispatch_affinity_route(&snap, 0, &over_res) == 1);
    std::vector<nm_affinity_target_t> targets = {{1u, 3u, 0u}};
    nativemods::Phase4Services::apply_affinity_targets(targets, over_res);
    CHECK(targets.size() == 2u && targets[0].tab_index == 42u && targets[1].tab_index == 99u);

    nm_affinity_route_result_t app_res{};
    CHECK(nm_dispatch_affinity_route(&snap, 1, &app_res) == 1);
    nativemods::Phase4Services::apply_affinity_targets(targets, app_res);
    CHECK(targets.size() == 4u && targets[2].tab_index == 42u && targets[3].tab_index == 99u);

    CHECK(nm_affinity_set_router_callback(nullptr, nullptr) == NM_OK);
    CHECK(nm_shutdown() == NM_OK);
    ++g_tests_passed;
    return 0;
}

int test_phase4_geal_rulesets_and_custom_ui_registries() {
    using nativemods::Phase4Services;
    CHECK(nm_initialize_for_build(NM_BUILD_SYNTHETIC, 0x10000000ull, 0x10000u, nullptr) == NM_OK);

    // 1. GEAL Builtin registration, invocation, duplicate rejection, and safe post-unload indirection no-op
    struct GealState {
        int         calls = 0;
        uint32_t    last_eid = 0;
        std::string last_args;
    } geal_st{};
    auto geal_cb = [](uint32_t eid, const char* args_json, char* out, size_t cap, size_t* out_len, void* ud) -> nm_status_t {
        auto* s = static_cast<GealState*>(ud);
        ++s->calls;
        s->last_eid = eid;
        s->last_args = args_json ? args_json : "";
        const char* reply = "42";
        const size_t n = std::strlen(reply);
        if (out_len) *out_len = n;
        if (out && cap > n) {
            std::memcpy(out, reply, n + 1);
        }
        return NM_OK;
    };

    uint64_t g_handle = 0;
    CHECK(nm_geal_register_builtin("MyCustomFn", 0, 4, 1, geal_cb, &geal_st, &g_handle) == NM_OK);
    CHECK(g_handle != 0);
    uint64_t g_dup = 0;
    CHECK(nm_geal_register_builtin("mycustomfn", 0, 4, 1, geal_cb, &geal_st, &g_dup) == NM_ERR_ALREADY_EXISTS);

    size_t g_slot = 0;
    uint64_t g_gen = 0;
    CHECK(Phase4Services::get_geal_builtin_slot_for_handle(g_handle, g_slot, g_gen));

    char res_buf[64] = {};
    size_t res_len = 0;
    CHECK(nm_geal_invoke_function(777u, "MyCustomFn", "[10, \"hi\"]", res_buf, sizeof(res_buf), &res_len) == NM_OK);
    CHECK(res_len == 2u && std::strcmp(res_buf, "42") == 0);
    CHECK(geal_st.calls == 1 && geal_st.last_eid == 777u && geal_st.last_args == "[10, \"hi\"]");
    // Buffer-too-small check
    char tiny[2] = {};
    CHECK(nm_geal_invoke_function(777u, "MyCustomFn", "[]", tiny, sizeof(tiny), &res_len) == NM_ERR_BUFFER_TOO_SMALL);
    CHECK(res_len == 2u);

    // Unregister and verify retained FuncWrapper indirection slot safely no-ops without calling managed cb!
    CHECK(nm_geal_unregister_builtin(g_handle) == NM_OK);
    CHECK(nm_geal_unregister_builtin(g_handle) == NM_ERR_NOT_FOUND);
    const int calls_before_stale = geal_st.calls;
    std::string stale_out = "untouched";
    CHECK(Phase4Services::invoke_geal_indirection_slot(g_slot, g_gen, 999u, "[]", stale_out) == NM_OK);
    CHECK(geal_st.calls == calls_before_stale);
    CHECK(stale_out.empty());

    // 2. Native Ruleset free-slot census (68 occupied, 107 free; 0xA0 OCCUPIED, 0xAE FREE) & 9-slot proxy dispatch
    uint32_t free_count = 0;
    uint32_t occupied_count = 0;
    for (uint32_t id = 0; id < NM_RULESET_SLOT_COUNT; ++id) {
        if (Phase4Services::is_stock_ruleset_slot_occupied(id)) ++occupied_count;
        else ++free_count;
    }
    CHECK(occupied_count == 68u && free_count == 107u);
    CHECK(Phase4Services::is_stock_ruleset_slot_occupied(0xA0u));
    CHECK(!Phase4Services::is_stock_ruleset_slot_occupied(NM_RULESET_FIRST_FREE_NATIVE_ID));

    struct RsState {
        int      created = 0;
        uint32_t entered_eid = 0;
        std::string script_ev;
        float    frame_dt = 0.0f;
        float    tick_dt = 0.0f;
        std::vector<uint8_t> last_payload;
        int      left = 0;
    } rs_st{};
    nm_native_ruleset_callbacks_t rs_cbs{};
    rs_cbs.struct_size = sizeof(rs_cbs);
    rs_cbs.on_created = [](uint32_t, void*, void* ud) { ++static_cast<RsState*>(ud)->created; };
    rs_cbs.on_area_entered = [](uint32_t, uint32_t eid, void* ud) { static_cast<RsState*>(ud)->entered_eid = eid; };
    rs_cbs.on_script_event = [](uint32_t, uint32_t, const char* name, void* ud) { static_cast<RsState*>(ud)->script_ev = name ? name : ""; };
    rs_cbs.on_frame = [](uint32_t, float dt, void* ud) { static_cast<RsState*>(ud)->frame_dt = dt; };
    rs_cbs.on_tick = [](uint32_t, float dt, void* ud) { static_cast<RsState*>(ud)->tick_dt = dt; };
    rs_cbs.on_ruleset_update = [](uint32_t, const uint8_t* p, uint32_t len, void* ud) {
        auto* s = static_cast<RsState*>(ud);
        s->last_payload.assign(p, p + len);
    };
    rs_cbs.on_area_left = [](uint32_t, void* ud) { ++static_cast<RsState*>(ud)->left; };

    uint64_t rs_bad = 0;
    // CRITICAL #128/#132: 0xA0 is occupied on 3.29.3d and MUST be rejected with NM_ERR_ALREADY_EXISTS!
    CHECK(nm_ruleset_register_native(0xA0u, &rs_cbs, &rs_st, &rs_bad) == NM_ERR_ALREADY_EXISTS);
    CHECK(nm_ruleset_register_native(NM_RULESET_SLOT_COUNT, &rs_cbs, &rs_st, &rs_bad) == NM_ERR_INVALID_ARG);

    uint64_t rs_handle = 0;
    CHECK(nm_ruleset_register_native(NM_RULESET_FIRST_FREE_NATIVE_ID, &rs_cbs, &rs_st, &rs_handle) == NM_OK);
    CHECK(rs_handle != 0);
    CHECK(nm_ruleset_register_native(NM_RULESET_FIRST_FREE_NATIVE_ID, &rs_cbs, &rs_st, &rs_bad) == NM_ERR_ALREADY_EXISTS);

    // Exercise ALL 9 ClientRuleset vtable slots on a proxy instance
    uint8_t fake_ic[16] = {};
    void* proxy = Phase4Services::create_ruleset_proxy_for_slot(NM_RULESET_FIRST_FREE_NATIVE_ID, fake_ic);
    CHECK(proxy != nullptr);
    void** vtbl = *reinterpret_cast<void***>(proxy);
    CHECK(vtbl != nullptr);

    using Slot0DtorFn = void*(NM_CALL*)(void*, uint32_t);
    using Slot1Fn     = void(NM_CALL*)(void*);
    using Slot2Fn     = void(NM_CALL*)(void*, void*);
    using Slot3Fn     = void(NM_CALL*)(void*, void*, const void*);
    using Slot4Fn     = void(NM_CALL*)(void*, float);
    using Slot5Fn     = void(NM_CALL*)(void*, float);
    using Slot6Fn     = uint32_t(NM_CALL*)(void*);
    using Slot7Fn     = void(NM_CALL*)(void*);
    using Slot8Fn     = void(NM_CALL*)(void*, const Phase4Services::NativeRulesetBufferView*);

    reinterpret_cast<Slot1Fn>(vtbl[1])(proxy);
    CHECK(rs_st.created == 1);

    uint8_t fake_player[0x90] = {};
    const uint32_t player_id = 0x1234u;
    std::memcpy(fake_player + 0x88u, &player_id, sizeof(player_id));
    reinterpret_cast<Slot2Fn>(vtbl[2])(proxy, fake_player);
    CHECK(rs_st.entered_eid == 0x1234u);

    struct FakeMsvcStr { char buf[16]; uint64_t sz; uint64_t cap; } ev_str{};
    std::memcpy(ev_str.buf, "HarvestReady", 12);
    ev_str.sz = 12;
    ev_str.cap = 15;
    reinterpret_cast<Slot3Fn>(vtbl[3])(proxy, fake_player, &ev_str);
    CHECK(rs_st.script_ev == "HarvestReady");

    reinterpret_cast<Slot4Fn>(vtbl[4])(proxy, 0.016f);
    CHECK(std::fabs(rs_st.frame_dt - 0.016f) < 1e-6f);
    reinterpret_cast<Slot5Fn>(vtbl[5])(proxy, 0.033f);
    CHECK(std::fabs(rs_st.tick_dt - 0.033f) < 1e-6f);
    CHECK(reinterpret_cast<Slot6Fn>(vtbl[6])(proxy) == 0x50u); // MUST return 0x50!
    reinterpret_cast<Slot7Fn>(vtbl[7])(proxy);

    const uint8_t pkt_bytes[5] = {0xAA, 0xBB, 0xCC, 0xDD, 0xEE};
    Phase4Services::NativeRulesetBufferView bv{pkt_bytes, sizeof(pkt_bytes), 1u};
    reinterpret_cast<Slot8Fn>(vtbl[8])(proxy, &bv);
    CHECK(rs_st.last_payload.size() == 4u && rs_st.last_payload[0] == 0xBB && rs_st.last_payload[3] == 0xEE);

    // Unregister while proxy is still alive: subsequent OnFrame & dtor MUST safely no-op without calling mod!
    CHECK(nm_ruleset_unregister_native(rs_handle) == NM_OK);
    rs_st.frame_dt = 0.0f;
    reinterpret_cast<Slot4Fn>(vtbl[4])(proxy, 0.5f);
    CHECK(rs_st.frame_dt == 0.0f);
    reinterpret_cast<Slot0DtorFn>(vtbl[0])(proxy, 1u); // deletes proxy cleanly (ASan verified!)
    CHECK(rs_st.left == 0);

    // 3. Custom UI Control Kind lifecycle & MSVC RB-tree unlink invariants
    struct CustomUiState {
        std::string created_kind;
        std::string prop_k;
        std::string prop_v;
        int         destroyed = 0;
    } cui_st{};
    nm_custom_ui_control_callbacks_t cui_cbs{};
    cui_cbs.struct_size = sizeof(cui_cbs);
    cui_cbs.on_created = [](const char* kind, void*, void* ud) { static_cast<CustomUiState*>(ud)->created_kind = kind ? kind : ""; };
    cui_cbs.on_property = [](void*, const char* k, const char* v, void* ud) {
        auto* s = static_cast<CustomUiState*>(ud);
        s->prop_k = k ? k : "";
        s->prop_v = v ? v : "";
    };
    cui_cbs.on_destroyed = [](void*, void* ud) { ++static_cast<CustomUiState*>(ud)->destroyed; };

    uint64_t cui_handle = 0;
    CHECK(nm_ui_register_custom_control_kind("GrowthGauge", &cui_cbs, &cui_st, &cui_handle) == NM_OK);
    uint64_t cui_dup = 0;
    CHECK(nm_ui_register_custom_control_kind("growthgauge", &cui_cbs, &cui_st, &cui_dup) == NM_ERR_ALREADY_EXISTS);

    uint8_t fake_frame[32] = {};
    Phase4Services::simulate_custom_ui_created("GrowthGauge", fake_frame);
    CHECK(cui_st.created_kind == "GrowthGauge");
    CHECK(Phase4Services::simulate_custom_ui_property(fake_frame, "fill_ratio", "0.75"));
    CHECK(cui_st.prop_k == "fill_ratio" && cui_st.prop_v == "0.75");
    CHECK(Phase4Services::simulate_custom_ui_destroyed(fake_frame));
    CHECK(cui_st.destroyed == 1);
    CHECK(!Phase4Services::simulate_custom_ui_property(fake_frame, "fill_ratio", "0.99"));
    CHECK(nm_ui_unregister_custom_control_kind(cui_handle) == NM_OK);
    CHECK(nm_ui_unregister_custom_control_kind(cui_handle) == NM_ERR_NOT_FOUND);

    // MSVC RB-tree unlink test (5-node valid RB tree -> unlink root, leaf, interior -> size==0)
    Phase4Services::MsvcRbNodeHeader head{};
    head.color = 1u;
    head.isnil = 1u;
    Phase4Services::MsvcRbNodeHeader n1{}, n2{}, n3{}, n4{}, n5{};
    /*        n3 (B)
     *       /      \
     *     n2 (B)   n4 (B)
     *     /          \
     *   n1 (R)       n5 (R)
     */
    auto init_node = [&](Phase4Services::MsvcRbNodeHeader& n, Phase4Services::MsvcRbNodeHeader* p,
                         Phase4Services::MsvcRbNodeHeader* l, Phase4Services::MsvcRbNodeHeader* r, uint8_t color) {
        n.parent = p; n.left = l; n.right = r; n.color = color; n.isnil = 0;
    };
    init_node(n3, &head, &n2, &n4, 1u);
    init_node(n2, &n3, &n1, &head, 1u);
    init_node(n1, &n2, &head, &head, 0u);
    init_node(n4, &n3, &head, &n5, 1u);
    init_node(n5, &n4, &head, &head, 0u);
    head.parent = &n3;
    head.left = &n1;
    head.right = &n5;
    Phase4Services::MsvcRbTreeHeader tree{&head, 5u};

    CHECK(Phase4Services::rb_tree_unlink_node(&tree, &n3));
    CHECK(tree.size == 4u && head.left == &n1 && head.right == &n5 && head.parent->color == 1u);
    CHECK(Phase4Services::rb_tree_unlink_node(&tree, &n1));
    CHECK(tree.size == 3u && head.left == &n2);
    CHECK(Phase4Services::rb_tree_unlink_node(&tree, &n5));
    CHECK(tree.size == 2u && head.right == &n4);
    CHECK(Phase4Services::rb_tree_unlink_node(&tree, &n2));
    CHECK(tree.size == 1u && head.parent == &n4 && head.left == &n4 && head.right == &n4);
    CHECK(Phase4Services::rb_tree_unlink_node(&tree, &n4));
    CHECK(tree.size == 0u && head.parent == &head && head.left == &head && head.right == &head);

    CHECK(nm_shutdown() == NM_OK);
    ++g_tests_passed;
    return 0;
}

struct alignas(16) MockGealBuiltinEntry {
    const wchar_t* name;
    void*          factory;
};
static_assert(sizeof(MockGealBuiltinEntry) == 16u, "MockGealBuiltinEntry must be 16 bytes");
static_assert(alignof(MockGealBuiltinEntry) == 16u, "MockGealBuiltinEntry must be 16-byte aligned");

struct MockGealRegCapture {
    int            calls = 0;
    uintptr_t      last_begin = 0;
    uintptr_t      last_end = 0;
    bool           all_16b_aligned = true;
    std::wstring   last_name;
    void*          last_factory = nullptr;
};

MockGealRegCapture g_mock_geal_reg_cap{};

void NM_CALL mock_geal_builtin_register_entry(const void* begin, const void* end) {
    ++g_mock_geal_reg_cap.calls;
    const uintptr_t b = reinterpret_cast<uintptr_t>(begin);
    const uintptr_t e = reinterpret_cast<uintptr_t>(end);
    g_mock_geal_reg_cap.last_begin = b;
    g_mock_geal_reg_cap.last_end = e;
    if ((b % 16u) != 0u) {
        g_mock_geal_reg_cap.all_16b_aligned = false;
    }

#if (defined(__x86_64__) || defined(_M_X64)) && (defined(__GNUC__) || defined(__clang__))
    // Mirror Game.exe 3.29.3d geal.builtin_register_entry (0x141de9858 / 0x141de9870):
    // `movaps xmm0, XMMWORD PTR [rdi]` traps #GP (0xC0000005 / SIGSEGV) unless begin % 16 == 0!
    __asm__ volatile("movaps (%0), %%xmm0" : : "r"(begin) : "xmm0");
#endif

    if (begin && (e - b) == sizeof(MockGealBuiltinEntry)) {
        const auto* entry = static_cast<const MockGealBuiltinEntry*>(begin);
        g_mock_geal_reg_cap.last_name = entry->name ? std::wstring(entry->name) : std::wstring{};
        g_mock_geal_reg_cap.last_factory = entry->factory;
    }
}

int test_phase4_geal_builtin_register_alignment() {
    using nativemods::EmbeddedSymbol;
    using nativemods::GameAllocator;
    using nativemods::HookEngine;
    using nativemods::Phase4Services;
    using nativemods::find_symbol;

    g_mock_geal_reg_cap = MockGealRegCapture{};

#if defined(_WIN32)
    CHECK(nm_initialize_for_build(NM_BUILD_SYNTHETIC, 0x10000000ull, 0x10000u, nullptr) == NM_OK);

    const EmbeddedSymbol* reg_sym = find_symbol(NM_BUILD_3_29_3D_328228, "geal.builtin_register_entry");
    CHECK(reg_sym != nullptr && reg_sym->rva != 0);

    // Place a 12-byte x64 absolute jump (`mov rax, &mock_geal_builtin_register_entry; jmp rax`)
    // in an isolated executable page so `fake_base + reg_sym->rva == stub_page`, while all other
    // 3.29.3d RVAs (hooks + geal.builtin_factory_table) resolve to unmapped memory safely skipped.
    void* stub_page = HookEngine::alloc_exec_page(4096);
    CHECK(stub_page != nullptr);
    auto* stub = static_cast<uint8_t*>(stub_page);
    const uint64_t mock_addr = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(&mock_geal_builtin_register_entry));
    stub[0] = 0x48u; // mov rax, imm64
    stub[1] = 0xB8u;
    std::memcpy(stub + 2, &mock_addr, sizeof(mock_addr));
    stub[10] = 0xFFu; // jmp rax
    stub[11] = 0xE0u;
    FlushInstructionCache(GetCurrentProcess(), stub_page, 4096);

    const uint64_t stub_addr = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(stub_page));
    const uint64_t fake_base = stub_addr - reg_sym->rva;
    CHECK(fake_base != 0);

    Phase4Services svc;
    HookEngine hooks;
    GameAllocator alloc;
    svc.install_live_hooks(NM_BUILD_3_29_3D_328228, fake_base, hooks, alloc);

    auto dummy_cb = [](uint32_t, const char*, char*, size_t, size_t*, void*) -> nm_status_t {
        return NM_OK;
    };

    uint64_t h1 = 0;
    CHECK(svc.geal_register_builtin("GardenRipen", 0, 2, 0, dummy_cb, nullptr, &h1) == NM_OK);
    CHECK(h1 != 0);
    CHECK(g_mock_geal_reg_cap.calls == 1);
    CHECK(g_mock_geal_reg_cap.all_16b_aligned);
    CHECK((g_mock_geal_reg_cap.last_begin % 16u) == 0u);
    CHECK((g_mock_geal_reg_cap.last_end - g_mock_geal_reg_cap.last_begin) == 16u);
    CHECK(g_mock_geal_reg_cap.last_name == L"GardenRipen");
    CHECK(g_mock_geal_reg_cap.last_factory != nullptr);

    // Register a second builtin from a slightly shifted stack frame to stress stack alignment
    auto reg_shifted = [&](uint64_t* out_h) -> nm_status_t {
        volatile uint8_t pad[24] = {};
        pad[0] = 1u;
        (void)pad[0];
        return svc.geal_register_builtin("MaxwellDance", 0, 1, 0, dummy_cb, nullptr, out_h);
    };
    uint64_t h2 = 0;
    CHECK(reg_shifted(&h2) == NM_OK);
    CHECK(h2 != 0);
    CHECK(g_mock_geal_reg_cap.calls == 2);
    CHECK(g_mock_geal_reg_cap.all_16b_aligned);
    CHECK((g_mock_geal_reg_cap.last_begin % 16u) == 0u);
    CHECK((g_mock_geal_reg_cap.last_end - g_mock_geal_reg_cap.last_begin) == 16u);
    CHECK(g_mock_geal_reg_cap.last_name == L"MaxwellDance");
    CHECK(g_mock_geal_reg_cap.last_factory != nullptr);

    CHECK(svc.geal_unregister_builtin(h2) == NM_OK);
    CHECK(svc.geal_unregister_builtin(h1) == NM_OK);
    svc.reset(&hooks);
    HookEngine::free_exec_page(stub_page, 4096);
    CHECK(nm_shutdown() == NM_OK);
#else
    (void)find_symbol;
    struct alignas(16) RegCtx {
        MockGealBuiltinEntry entry;
        uint64_t             reg_addr;
    };
    static_assert(alignof(RegCtx) == 16u, "RegCtx must be 16-byte aligned");
    static_assert(offsetof(RegCtx, entry) % 16u == 0u, "RegCtx::entry must be 16-byte aligned");
    int dummy_factory = 0;
    RegCtx rctx{
        MockGealBuiltinEntry{L"GardenRipen", &dummy_factory},
        static_cast<uint64_t>(reinterpret_cast<uintptr_t>(&mock_geal_builtin_register_entry)),
    };
    using RegFn = void(NM_CALL*)(const void*, const void*);
    reinterpret_cast<RegFn>(static_cast<uintptr_t>(rctx.reg_addr))(&rctx.entry, &rctx.entry + 1);
    CHECK(g_mock_geal_reg_cap.calls == 1);
    CHECK(g_mock_geal_reg_cap.all_16b_aligned);
    CHECK((g_mock_geal_reg_cap.last_begin % 16u) == 0u);
    CHECK((g_mock_geal_reg_cap.last_end - g_mock_geal_reg_cap.last_begin) == 16u);
    CHECK(g_mock_geal_reg_cap.last_name == L"GardenRipen");
    CHECK(g_mock_geal_reg_cap.last_factory == &dummy_factory);
#endif

    ++g_tests_passed;
    return 0;
}

int test_phase5_prologues_and_address_table() {
    using nativemods::HookEngine;

    constexpr uint64_t kRebased = 0x7ff650000000ull;
    CHECK(nm_initialize_for_build(
              NM_BUILD_3_29_3D_328228,
              kRebased,
              0x4d9a000u,
              "e9ba3d31c4d922e1826ef01d02fd22178a001b29e17264b68fc2446efd41f53d") == NM_OK);

    nm_host_info_t host{};
    CHECK(nm_get_host_info(&host) == NM_OK);
    CHECK(host.symbol_count == 447u);

    uint32_t off_count = 0;
    const nativemods::EmbeddedOffset* offs = nativemods::get_offset_table(NM_BUILD_3_29_3D_328228, off_count);
    CHECK(offs != nullptr);
    CHECK(off_count == 373u);

    // Verify all 16 new Phase 5 offsets resolve to exact verified values.
    CHECK(nativemods::find_offset(NM_BUILD_3_29_3D_328228, "items.stack.size")->offset == 0x18u);
    CHECK(nativemods::find_offset(NM_BUILD_3_29_3D_328228, "items.stack.static_ptr")->offset == 0x10u);
    CHECK(nativemods::find_offset(NM_BUILD_3_29_3D_328228, "items.stack_static.tab_max")->offset == 0x20u);
    CHECK(nativemods::find_offset(NM_BUILD_3_29_3D_328228, "items.stack_static.inv13_max")->offset == 0x24u);
    CHECK(nativemods::find_offset(NM_BUILD_3_29_3D_328228, "items.stack_static.inv_max")->offset == 0x28u);
    CHECK(nativemods::find_offset(NM_BUILD_3_29_3D_328228, "ui.inv_item_view.cell_side")->offset == 0x588u);
    CHECK(nativemods::find_offset(NM_BUILD_3_29_3D_328228, "ui.inv_item_view.socket_overlay")->offset == 0x578u);
    CHECK(nativemods::find_offset(NM_BUILD_3_29_3D_328228, "ui.item_view.cell_side")->offset == 0x35cu);
    CHECK(nativemods::find_offset(NM_BUILD_3_29_3D_328228, "ui.item_view.width")->offset == 0x360u);
    CHECK(nativemods::find_offset(NM_BUILD_3_29_3D_328228, "ui.item_view.height")->offset == 0x364u);
    CHECK(nativemods::find_offset(NM_BUILD_3_29_3D_328228, "ui.item_view.socket_overlay")->offset == 0x370u);
    CHECK(nativemods::find_offset(NM_BUILD_3_29_3D_328228, "ui.socket_overlay.cols")->offset == 0x420u);
    CHECK(nativemods::find_offset(NM_BUILD_3_29_3D_328228, "ui.socket_overlay.cell_side")->offset == 0x424u);
    CHECK(nativemods::find_offset(NM_BUILD_3_29_3D_328228, "ui.socket_overlay.entity")->offset == 0x430u);
    CHECK(nativemods::find_offset(NM_BUILD_3_29_3D_328228, "ui.socket_overlay.sockets_comp")->offset == 0x438u);
    CHECK(nativemods::find_offset(NM_BUILD_3_29_3D_328228, "ui.socket_element.cell_side")->offset == 0x280u);

    // Every vtable and data symbol across all 447 symbols MUST have expected_bytes_hex == nullptr.
    for (uint32_t i = 0; i < host.symbol_count; ++i) {
        nm_symbol_info_t s{};
        CHECK(nm_get_symbol_at(i, &s) == NM_OK);
        if (s.kind == NM_SYM_VTABLE || s.kind == NM_SYM_DATA) {
            CHECK(s.expected_bytes_hex == nullptr);
        }
    }

    // Verify all 4 new Phase 5 vtables resolve cleanly with expected_bytes_hex == nullptr.
    const std::pair<const char*, uint64_t> p5_vtables[] = {
        {"items.vtable_stack",       0x363f638ull},
        {"ui.vtable_socket_overlay", 0x3483208ull},
        {"ui.vtable_socket_element", 0x3482068ull},
        {"ui.vtable_item_view",      0x3485388ull},
    };
    for (const auto& [name, rva] : p5_vtables) {
        nm_symbol_info_t s{};
        CHECK(nm_lookup_symbol(name, &s) == NM_OK);
        CHECK(s.kind == NM_SYM_VTABLE);
        CHECK(s.rva == rva);
        CHECK(s.expected_bytes_hex == nullptr);
    }

    // Verify key Phase 5 TargetChain-hooked prologues decode cleanly >= 14B via HookEngine::compute_stolen_length.
    auto hex_to_bytes = [](const char* hex) -> std::vector<uint8_t> {
        std::vector<uint8_t> out;
        const size_t n = std::strlen(hex) / 2u;
        out.reserve(n);
        for (size_t i = 0; i < n; ++i) {
            auto nib = [](char c) -> uint8_t {
                if (c >= '0' && c <= '9') return static_cast<uint8_t>(c - '0');
                if (c >= 'a' && c <= 'f') return static_cast<uint8_t>(c - 'a' + 10);
                return static_cast<uint8_t>(c - 'A' + 10);
            };
            out.push_back(static_cast<uint8_t>((nib(hex[i * 2]) << 4) | nib(hex[i * 2 + 1])));
        }
        return out;
    };

    const std::pair<const char*, size_t> p5_hook_prologues[] = {
        {"items.stack_deserialise",         15u},
        {"items.stack_serialise",           14u},
        {"items.item_deserialise",          14u},
        {"items.item_deserialise_framed",   15u},
        {"ui.format_stack_count",           15u},
        {"ui.split_window_show",            15u},
        {"network.write_split_stack_0x7c",  16u},
        {"items.sockets_get_count",         16u},
        {"ui.socket_overlay_ctor",          15u},
        {"ui.socket_overlay_set_cell_side", 14u},
        {"ui.socket_overlay_rebuild",       14u},
        {"ui.socket_overlay_layout",        15u},
        {"ui.socket_overlay_hit_test",      15u},
        {"ui.socket_element_draw",          15u},
    };
    for (const auto& [sym_name, expected_stolen] : p5_hook_prologues) {
        nm_symbol_info_t s{};
        CHECK(nm_lookup_symbol(sym_name, &s) == NM_OK);
        CHECK(s.kind == NM_SYM_CODE);
        CHECK(s.expected_bytes_hex != nullptr);
        std::vector<uint8_t> bytes = hex_to_bytes(s.expected_bytes_hex);
        // Pad tail with safe NOPs beyond 16B signature window if needed for full insn decode
        bytes.resize(32u, 0x90u);
        const size_t stolen = HookEngine::compute_stolen_length(bytes.data(), HookEngine::kAbsJumpSize);
        CHECK(stolen == expected_stolen);
    }

    // Also confirm 3.29.3c mirrors all 447 symbols & 373 offsets with Phase 5 marked unverified_3_29_3c.
    CHECK(nm_initialize_for_build(
              NM_BUILD_3_29_3C_328126,
              kRebased,
              0x4d9a000u,
              "01a22facf36886b39bca74793c0d8c7295b1495f1625b4ebc7b23c2de32a70cf") == NM_OK);
    CHECK(nm_get_host_info(&host) == NM_OK);
    CHECK(host.symbol_count == 447u);
    nm_symbol_info_t sym3c{};
    CHECK(nm_lookup_symbol("items.stack_deserialise", &sym3c) == NM_ERR_SYMBOL_UNVERIFIED);
    CHECK(sym3c.rva == 0ull && sym3c.status == NM_SYM_STATUS_UNVERIFIED);
    CHECK(nm_lookup_symbol("ui.vtable_socket_overlay", &sym3c) == NM_ERR_SYMBOL_UNVERIFIED);
    CHECK(sym3c.rva == 0ull && sym3c.expected_bytes_hex == nullptr);

    CHECK(nm_shutdown() == NM_OK);
    ++g_tests_passed;
    return 0;
}

int test_phase5_u32_stack_wire_format_and_override() {
    using nativemods::Phase5Services;

    CHECK(nm_initialize_for_build(NM_BUILD_SYNTHETIC, 0x140000000ull, 0x1000000u, nullptr) == NM_OK);

    // Toggle flags via C ABI and verify reset on nm_shutdown / re-init
    CHECK(nm_stack_set_u32_wire_enabled(1) == NM_OK);
    CHECK(nm_stack_set_format_millions_enabled(1) == NM_OK);
    CHECK(nm_ui_set_compact_sockets_enabled(1) == NM_OK);
    CHECK(nm_shutdown() == NM_OK);
    CHECK(nm_stack_set_u32_wire_enabled(1) == NM_ERR_NOT_INITIALIZED);

    CHECK(nm_initialize_for_build(NM_BUILD_SYNTHETIC, 0x140000000ull, 0x1000000u, nullptr) == NM_OK);

    // 1. Stack::Deserialise 0xFFFF + LE u32 wire sentinel tests
    alignas(8) uint8_t fake_stack[0x20] = {};
    constexpr size_t kSizeOff = 0x18u;

    // 1a. Disabled mode: vanilla passthrough even if stream starts with 0xFFFF + u32(1000000)
    {
        const uint8_t wire[6] = {0xFFu, 0xFFu, 0x40u, 0x42u, 0x0Fu, 0x00u}; // 0xFFFF + LE 1,000,000
        Phase5Services::StackWireStream st{wire, sizeof(wire), 0u};
        uint32_t initial = 7u;
        std::memcpy(fake_stack + kSizeOff, &initial, sizeof(initial));
        CHECK(!Phase5Services::try_deserialise_u32_stack_wire(false, fake_stack, kSizeOff, &st));
        CHECK(st.pos == 0u);
        uint32_t after = 0u;
        std::memcpy(&after, fake_stack + kSizeOff, sizeof(after));
        CHECK(after == 7u);
    }

    // 1b. Enabled mode on normal vanilla value (<= 65534): returns false for vanilla passthrough
    {
        const uint8_t wire[2] = {0xFEu, 0xFFu}; // 65534
        Phase5Services::StackWireStream st{wire, sizeof(wire), 0u};
        CHECK(!Phase5Services::try_deserialise_u32_stack_wire(true, fake_stack, kSizeOff, &st));
        CHECK(st.pos == 0u);
    }

    // 1c. Enabled mode on 0xFFFF + LE u32(65535) (boundary value!)
    {
        const uint8_t wire[6] = {0xFFu, 0xFFu, 0xFFu, 0xFFu, 0x00u, 0x00u};
        Phase5Services::StackWireStream st{wire, sizeof(wire), 0u};
        CHECK(Phase5Services::try_deserialise_u32_stack_wire(true, fake_stack, kSizeOff, &st));
        CHECK(st.pos == 6u);
        uint32_t val = 0u;
        std::memcpy(&val, fake_stack + kSizeOff, sizeof(val));
        CHECK(val == 65535u);
    }

    // 1d. Enabled mode on 0xFFFF + LE u32(1,000,000) at non-zero offset with trailing bytes
    {
        const uint8_t wire[10] = {0xAAu, 0xBBu, 0xFFu, 0xFFu, 0x40u, 0x42u, 0x0Fu, 0x00u, 0xCCu, 0xDDu};
        Phase5Services::StackWireStream st{wire, sizeof(wire), 2u};
        CHECK(Phase5Services::try_deserialise_u32_stack_wire(true, fake_stack, kSizeOff, &st));
        CHECK(st.pos == 8u);
        uint32_t val = 0u;
        std::memcpy(&val, fake_stack + kSizeOff, sizeof(val));
        CHECK(val == 1000000u);
    }

    // 1e. Short-buffer guard: 0xFFFF with fewer than 6 bytes remaining MUST NOT read OOB or advance pos!
    for (uint64_t trunc_len = 2u; trunc_len < 6u; ++trunc_len) {
        const uint8_t wire[5] = {0xFFu, 0xFFu, 0x40u, 0x42u, 0x0Fu};
        Phase5Services::StackWireStream st{wire, trunc_len, 0u};
        uint32_t before = 123u;
        std::memcpy(fake_stack + kSizeOff, &before, sizeof(before));
        CHECK(!Phase5Services::try_deserialise_u32_stack_wire(true, fake_stack, kSizeOff, &st));
        CHECK(st.pos == 0u);
        uint32_t after = 0u;
        std::memcpy(&after, fake_stack + kSizeOff, sizeof(after));
        CHECK(after == 123u);
    }

    // 1f. pos > size guard
    {
        const uint8_t wire[6] = {0xFFu, 0xFFu, 0x01u, 0x00u, 0x00u, 0x00u};
        Phase5Services::StackWireStream st{wire, 4u, 6u};
        CHECK(!Phase5Services::try_deserialise_u32_stack_wire(true, fake_stack, kSizeOff, &st));
    }

    // 2. ui.format_stack_count millions formatting
    std::string fmt;
    CHECK(!Phase5Services::format_stack_count_millions(0u, fmt));
    CHECK(!Phase5Services::format_stack_count_millions(9999u, fmt));
    CHECK(!Phase5Services::format_stack_count_millions(999999u, fmt));
    CHECK(Phase5Services::format_stack_count_millions(1000000u, fmt) && fmt == "1M");
    CHECK(Phase5Services::format_stack_count_millions(1099999u, fmt) && fmt == "1M");
    CHECK(Phase5Services::format_stack_count_millions(1200000u, fmt) && fmt == "1.2M");
    CHECK(Phase5Services::format_stack_count_millions(1299999u, fmt) && fmt == "1.2M");
    CHECK(Phase5Services::format_stack_count_millions(12345678u, fmt) && fmt == "12.3M");
    CHECK(Phase5Services::format_stack_count_millions(100000000u, fmt) && fmt == "100M");
    CHECK(Phase5Services::format_stack_count_millions(0xFFFFFFFFu, fmt) && fmt == "4294.9M" && fmt.size() <= 7u);

    // 3. Sideband Stack+0x18 override
    CHECK(Phase5Services::apply_stack_override_to_component(fake_stack, kSizeOff, 1000000u));
    uint32_t overridden = 0u;
    std::memcpy(&overridden, fake_stack + kSizeOff, sizeof(overridden));
    CHECK(overridden == 1000000u);
    CHECK(!Phase5Services::apply_stack_override_to_component(nullptr, kSizeOff, 100u));
    CHECK(!Phase5Services::apply_stack_override_to_component(fake_stack, kSizeOff, 0u));
    CHECK(nm_stack_set_entity_override(0u, 100u) == NM_ERR_INVALID_ARG);
    CHECK(nm_stack_set_entity_override(42u, 0u) == NM_ERR_INVALID_ARG);
    CHECK(nm_stack_set_entity_override(42u, 1000000u) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);

    CHECK(nm_shutdown() == NM_OK);
    ++g_tests_passed;
    return 0;
}

int test_phase5_compact_sockets_and_world_vfx() {
    using nativemods::Phase5Services;

    // 1. Compact socket layout solver (vanilla no-op vs 6-socket on 2x2 / 1x3 / 1x1)
    {
        const auto stock_2x2 = Phase5Services::compute_compact_socket_layout(2, 2, 4);
        CHECK(!stock_2x2.compacted && stock_2x2.cols == 2u && std::fabs(stock_2x2.scale - 1.0f) < 1e-5f);

        const auto stock_2x3 = Phase5Services::compute_compact_socket_layout(2, 3, 6);
        CHECK(!stock_2x3.compacted && stock_2x3.cols == 2u && std::fabs(stock_2x3.scale - 1.0f) < 1e-5f);

        const auto compact_2x2_6 = Phase5Services::compute_compact_socket_layout(2, 2, 6);
        CHECK(compact_2x2_6.compacted && compact_2x2_6.cols == 3u && compact_2x2_6.rows == 2u);
        CHECK(std::fabs(compact_2x2_6.scale - (2.0f / 3.0f)) < 1e-5f);

        const auto compact_2x2_5 = Phase5Services::compute_compact_socket_layout(2, 2, 5);
        CHECK(compact_2x2_5.compacted && compact_2x2_5.cols == 3u && compact_2x2_5.rows == 2u);
        CHECK(std::fabs(compact_2x2_5.scale - (2.0f / 3.0f)) < 1e-5f);

        const auto compact_1x3_6 = Phase5Services::compute_compact_socket_layout(1, 3, 6);
        CHECK(compact_1x3_6.compacted && compact_1x3_6.cols == 2u && compact_1x3_6.rows == 3u);
        CHECK(std::fabs(compact_1x3_6.scale - 0.5f) < 1e-5f);

        const auto compact_1x3_4 = Phase5Services::compute_compact_socket_layout(1, 3, 4);
        CHECK(compact_1x3_4.compacted && compact_1x3_4.cols == 1u && compact_1x3_4.rows == 4u);
        CHECK(std::fabs(compact_1x3_4.scale - 0.75f) < 1e-5f);
    }

    // 2. XMM1 float packing (#163 note #1: low 32 bits IEEE-754 float, NEVER double cast!)
    {
        const uint64_t packed = Phase5Services::pack_xmm_f32(52.0f);
        CHECK((packed >> 32) == 0ull);
        CHECK(static_cast<uint32_t>(packed & 0xFFFFFFFFull) == 0x42500000u);
        CHECK(std::fabs(Phase5Services::unpack_xmm_f32(packed) - 52.0f) < 1e-5f);
    }

    // 3. Anti-double-scaling on repeated Rebuild + SetCellSide + full restore on n <= w*h or disable (#163 note #2)
    {
        uint32_t cols = 2u;
        float cell_side = 78.0f;

        // First Rebuild on 2x2 item with 6 sockets -> cols=3, cell_side=52.0f
        CHECK(Phase5Services::apply_socket_overlay_rebuild_pre(true, 2, 2, 6, 78.0f, cols, cell_side));
        CHECK(cols == 3u);
        CHECK(std::fabs(cell_side - 52.0f) < 1e-4f);

        // Repeated Rebuild MUST NOT double-scale (remains 52.0f, not 34.67f!)
        CHECK(Phase5Services::apply_socket_overlay_rebuild_pre(true, 2, 2, 6, 78.0f, cols, cell_side));
        CHECK(cols == 3u);
        CHECK(std::fabs(cell_side - 52.0f) < 1e-4f);

        // SetCellSide packs 52.0f into low 32 bits of xmm1_bits
        uint64_t xmm1 = Phase5Services::pack_xmm_f32(78.0f);
        CHECK(Phase5Services::apply_socket_overlay_set_cell_side_pre(true, 2, 2, 6, 78.0f, cols, xmm1));
        CHECK(cols == 3u);
        CHECK((xmm1 >> 32) == 0ull);
        CHECK(std::fabs(Phase5Services::unpack_xmm_f32(xmm1) - 52.0f) < 1e-4f);

        // Restore when item changes to n <= item_w * item_h (4 sockets on 2x2)
        CHECK(!Phase5Services::apply_socket_overlay_rebuild_pre(true, 2, 2, 4, 78.0f, cols, cell_side));
        CHECK(cols == 2u);
        CHECK(std::fabs(cell_side - 78.0f) < 1e-4f);

        // Compact again, then restore when compact_enabled is toggled OFF
        CHECK(Phase5Services::apply_socket_overlay_rebuild_pre(true, 1, 3, 6, 78.0f, cols, cell_side));
        CHECK(cols == 2u && std::fabs(cell_side - 39.0f) < 1e-4f);
        CHECK(!Phase5Services::apply_socket_overlay_set_cell_side_pre(false, 1, 3, 6, 78.0f, cols, xmm1));
        CHECK(cols == 1u);
        CHECK(std::fabs(Phase5Services::unpack_xmm_f32(xmm1) - 78.0f) < 1e-4f);
    }

    // 4. world_play_effect & world_create_client_object_ex (validation + transform + path normalization)
    {
        CHECK(nm_initialize_for_build(NM_BUILD_SYNTHETIC, 0x140000000ull, 0x1000000u, nullptr) == NM_OK);

        CHECK(nm_world_play_effect(0u, nullptr) == NM_ERR_INVALID_ARG);
        CHECK(nm_world_play_effect(0u, "") == NM_ERR_INVALID_ARG);
        CHECK(nm_world_play_effect(0u, "   ") == NM_ERR_INVALID_ARG);
        CHECK(nm_world_play_effect(0u, "Metadata/Effects/Spells/fireball/fireball.ot") == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);

        uint64_t handle = 123u;
        CHECK(nm_world_create_client_object_ex(nullptr, 1.0f, 2.0f, 3.0f, 0.5f, 1.5f, &handle) == NM_ERR_INVALID_ARG);
        CHECK(nm_world_create_client_object_ex("", 1.0f, 2.0f, 3.0f, 0.5f, 1.5f, &handle) == NM_ERR_INVALID_ARG);
        CHECK(nm_world_create_client_object_ex("Metadata/Test.ot", 1.0f, 2.0f, 3.0f, 0.5f, -1.0f, &handle) == NM_ERR_INVALID_ARG);
        CHECK(nm_world_create_client_object_ex("Metadata/Test.ot", 1.0f, 2.0f, 3.0f, 0.5f, 1.5f, nullptr) == NM_ERR_INVALID_ARG);
        CHECK(nm_world_create_client_object_ex("Metadata/Test.ot", 100.0f, 200.0f, 10.0f, 1.57f, 2.0f, &handle) == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);
        CHECK(handle == 0u);

        CHECK(Phase5Services::normalize_effect_path("  \\Metadata\\Effects\\Test.ot  ") == "Metadata/Effects/Test.ot");
        CHECK(Phase5Services::looks_like_metadata_path("Metadata/Effects/Test.ot"));
        CHECK(!Phase5Services::looks_like_metadata_path("PlayBurstEffect"));

        // Verify Positioned + Render transform math (rotation at +0x29C, scale on Render bounds +0x12C)
        alignas(16) uint8_t fake_pos[0x2C0] = {};
        alignas(16) uint8_t fake_render[0x140] = {};
        const float init_bounds[3] = {10.0f, 20.0f, 30.0f};
        std::memcpy(fake_render + 0x12Cu, init_bounds, sizeof(init_bounds));

        CHECK(Phase5Services::apply_client_object_transform(
            fake_pos, 0x294u, 0x298u, 0x29Cu, 0x2B8u, 0x2BCu, 0x2B4u,
            fake_render, 0x120u, 0x12Cu,
            250.0f, 500.0f, 12.5f, 1.5707963f, 2.0f));

        int32_t gx = 0, gy = 0;
        float rot = 0.0f, wx = 0.0f, wy = 0.0f, wz = 0.0f;
        float rpos[3] = {}, rbounds[3] = {};
        std::memcpy(&gx, fake_pos + 0x294u, sizeof(gx));
        std::memcpy(&gy, fake_pos + 0x298u, sizeof(gy));
        std::memcpy(&rot, fake_pos + 0x29Cu, sizeof(rot));
        std::memcpy(&wx, fake_pos + 0x2B8u, sizeof(wx));
        std::memcpy(&wy, fake_pos + 0x2BCu, sizeof(wy));
        std::memcpy(&wz, fake_pos + 0x2B4u, sizeof(wz));
        std::memcpy(rpos, fake_render + 0x120u, sizeof(rpos));
        std::memcpy(rbounds, fake_render + 0x12Cu, sizeof(rbounds));

        CHECK(gx == 23 && gy == 46);
        CHECK(std::fabs(rot - 1.5707963f) < 1e-5f);
        CHECK(std::fabs(wx - 250.0f) < 1e-4f && std::fabs(wy - 500.0f) < 1e-4f && std::fabs(wz - 12.5f) < 1e-4f);
        CHECK(std::fabs(rpos[0] - 250.0f) < 1e-4f && std::fabs(rpos[1] - 500.0f) < 1e-4f && std::fabs(rpos[2] - 12.5f) < 1e-4f);
        CHECK(std::fabs(rbounds[0] - 20.0f) < 1e-4f && std::fabs(rbounds[1] - 40.0f) < 1e-4f && std::fabs(rbounds[2] - 60.0f) < 1e-4f);

        CHECK(nm_shutdown() == NM_OK);
    }

    ++g_tests_passed;
    return 0;
}

int test_object_type_path_and_slot_guards() {
    using nativemods::Phase2Services;

    // 1. Canonicalization & strict non-.ot rejection
    {
        std::string key, ot_file;
        CHECK(Phase2Services::canonicalize_object_type_path("Metadata/Pet/Mods/Maxwell/MaxwellPet", key, ot_file));
        CHECK(key == "Metadata/Pet/Mods/Maxwell/MaxwellPet");
        CHECK(ot_file == "Metadata/Pet/Mods/Maxwell/MaxwellPet.ot");

        CHECK(Phase2Services::canonicalize_object_type_path("  \\Metadata\\Pet\\Mods\\Maxwell\\MaxwellPet.ot  ", key, ot_file));
        CHECK(key == "Metadata/Pet/Mods/Maxwell/MaxwellPet");
        CHECK(ot_file == "Metadata/Pet/Mods/Maxwell/MaxwellPet.ot");

        CHECK(Phase2Services::canonicalize_object_type_path("///Metadata/Effects/Spells/fireball/fireball.OT", key, ot_file));
        CHECK(key == "Metadata/Effects/Spells/fireball/fireball");
        CHECK(ot_file == "Metadata/Effects/Spells/fireball/fireball.ot");

        // Dots in directory segments are allowed as long as the last segment is extensionless or ".ot".
        CHECK(Phase2Services::canonicalize_object_type_path("Metadata/Mods/v1.2/CustomEffect.Ot", key, ot_file));
        CHECK(key == "Metadata/Mods/v1.2/CustomEffect");
        CHECK(ot_file == "Metadata/Mods/v1.2/CustomEffect.ot");

        // All non-.ot extensions and malformed paths MUST be rejected!
        const char* bad_paths[] = {
            nullptr,
            "",
            "   ",
            "/",
            "\\",
            "Metadata/Pet/Mods/Maxwell/",
            "Metadata//Pet/MaxwellPet",
            "Metadata/../Pet/MaxwellPet",
            "Metadata/./Pet/MaxwellPet",
            ".ot",
            "Metadata/.ot",
            "Metadata/Pet/Mods/Maxwell/MaxwellPet.",
            "Metadata/Pet/Mods/Maxwell/MaxwellPet.ao",
            "Metadata/Pet/Mods/Maxwell/MaxwellPet.AO",
            "Metadata/Pet/Mods/Maxwell/MaxwellPet.ao.ot",
            "Metadata/Effects/Spells/breach/breach_ring.epk",
            "Metadata/Pet/Mods/Maxwell/MaxwellPet.pet",
            "Metadata/Pet/Mods/Maxwell/MaxwellPet.act",
            "Art/Textures/Maxwell.dds",
            "Art/Models/Maxwell.fmt",
            "Art/Models/Maxwell.smd",
            "Art/Models/Maxwell.ast",
            "Art/Models/Maxwell.mat",
            "Art/Models/Maxwell.sm",
            "Art/Models/Maxwell.amd",
            "Audio/Mods/Maxwell/stock_market.ogg",
            "Audio/Mods/Maxwell/click.wav",
            "Art/UI/MaxwellPanel.ui",
            "Data/Pet.datc64",
        };
        for (const char* bad : bad_paths) {
            CHECK(!Phase2Services::canonicalize_object_type_path(bad, key, ot_file));
            CHECK(key.empty() && ot_file.empty());
        }
    }

    // 2. C ABI entry points reject non-.ot paths with NM_ERR_INVALID_ARG up front
    {
        CHECK(nm_initialize_for_build(NM_BUILD_SYNTHETIC, 0x140000000ull, 0x1000000u, nullptr) == NM_OK);

        uint64_t handle = 0xDEADBEEFull;
        const char* bad_assets[] = {
            "Metadata/Pet/Mods/Maxwell/MaxwellPet.ao",
            "Metadata/Effects/Spells/breach/breach_ring.epk",
            "Metadata/Pet/Mods/Maxwell/MaxwellPet.pet",
            "Metadata/Pet/Mods/Maxwell/MaxwellPet.act",
            "Art/Textures/Icon.dds",
            "Audio/Mods/Maxwell/stock_market.ogg",
        };
        for (const char* bad : bad_assets) {
            CHECK(nm_world_create_client_object(bad, 1.0f, 2.0f, 3.0f, &handle) == NM_ERR_INVALID_ARG);
            CHECK(handle == 0u);
            handle = 0xDEADBEEFull;
            CHECK(nm_world_create_client_object_ex(bad, 1.0f, 2.0f, 3.0f, 0.0f, 1.0f, &handle) == NM_ERR_INVALID_ARG);
            CHECK(handle == 0u);
            CHECK(nm_world_play_effect(67108870u, bad) == NM_ERR_INVALID_ARG);
        }

        // Bare GEAL function name (no '/' or '.') still passes pre-validation and reaches platform check.
        CHECK(nm_world_play_effect(67108870u, "PlayBurstEffect") == NM_ERR_NOT_SUPPORTED_ON_PLATFORM);

        // 3. ObjectTypeSlot validation (guards against 0-component / abstract / failed slots before EntityManager)
        CHECK(!Phase2Services::validate_object_type_slot(nullptr));

        alignas(8) uint8_t fake_slot[0x50] = {};
        alignas(8) uint8_t fake_ot_data[0xA0] = {};
        void* fake_factories[2] = {reinterpret_cast<void*>(0x1000), reinterpret_cast<void*>(0x2000)};

        // load_state == 0 (unloaded lazy slot after existence verified) -> allowed
        CHECK(Phase2Services::validate_object_type_slot(fake_slot));

        // load_state == 3 (failed load w/ stored exception) -> rejected
        uint32_t state = 3u;
        std::memcpy(fake_slot + 0x40u, &state, sizeof(state));
        CHECK(!Phase2Services::validate_object_type_slot(fake_slot));

        // load_state == 2 with null ot_data -> rejected
        state = 2u;
        std::memcpy(fake_slot + 0x40u, &state, sizeof(state));
        CHECK(!Phase2Services::validate_object_type_slot(fake_slot));

        // load_state == 2 with valid ot_data pointer but 0 components (begin == end) -> rejected!
        void* ot_data_ptr = fake_ot_data;
        std::memcpy(fake_slot + 0x28u, &ot_data_ptr, sizeof(ot_data_ptr));
        uintptr_t comp_begin = reinterpret_cast<uintptr_t>(&fake_factories[0]);
        uintptr_t comp_end = comp_begin; // 0 components
        std::memcpy(fake_ot_data + 0x10u, &comp_begin, sizeof(comp_begin));
        std::memcpy(fake_ot_data + 0x18u, &comp_end, sizeof(comp_end));
        CHECK(!Phase2Services::validate_object_type_slot(fake_slot));

        // load_state == 2 with 2 components & is_abstract == 0 -> accepted!
        comp_end = reinterpret_cast<uintptr_t>(&fake_factories[2]);
        std::memcpy(fake_ot_data + 0x18u, &comp_end, sizeof(comp_end));
        CHECK(Phase2Services::validate_object_type_slot(fake_slot));

        // load_state == 2 with 2 components & is_abstract == 1 -> rejected!
        uint8_t is_abstract = 1u;
        std::memcpy(fake_ot_data + 0x88u, &is_abstract, sizeof(is_abstract));
        CHECK(!Phase2Services::validate_object_type_slot(fake_slot));
        is_abstract = 0u;
        std::memcpy(fake_ot_data + 0x88u, &is_abstract, sizeof(is_abstract));

        // 4. Full pre-flight + spawn pipeline: proves .ao/.epk -> NM_ERR_INVALID_ARG (0 game calls),
        //    missing .ot -> NM_ERR_NOT_FOUND (0 GetObjectType/create calls), empty slot -> NM_ERR_INVALID_ARG
        //    (0 create calls), and existing .ot -> canonical extensionless key + clean spawn!
        uint64_t ov_tok = 0;
        const char* kOverlayPath = "Metadata/Pet/Mods/Maxwell/MaxwellPet.ot";
        auto ov_cb = [](const char* path, uint8_t** out_data, size_t* out_size, void*) -> nm_status_t {
            if (!path || (std::strcmp(path, "metadata/pet/mods/maxwell/maxwellpet.ot") != 0 &&
                          std::strcmp(path, "Metadata/Pet/Mods/Maxwell/MaxwellPet.ot") != 0)) {
                return NM_ERR_NOT_FOUND;
            }
            static const char kBody[] = "version 2\nextends \"Metadata/Pet/Pet\"\n";
            auto* copy = new uint8_t[sizeof(kBody) - 1u];
            std::memcpy(copy, kBody, sizeof(kBody) - 1u);
            *out_data = copy;
            *out_size = sizeof(kBody) - 1u;
            return NM_OK;
        };
        auto ov_free = [](uint8_t* p, void*) { delete[] p; };
        CHECK(nm_register_file_overlay(ov_cb, ov_free, nullptr, &ov_tok) == NM_OK);
        CHECK(nm_set_file_overlay_known_paths(ov_tok, &kOverlayPath, 1u) == NM_OK);

        struct MockSpawnState {
            int         overlay_calls = 0;
            int         fs_calls = 0;
            int         get_ot_calls = 0;
            int         create_calls = 0;
            bool        fs_has_file = false;
            void*       slot_to_return = nullptr;
            void*       ent_to_return = nullptr;
            std::string last_ot_file;
            std::string last_canonical_key;
        } mock{};
        uint8_t dummy_ent[16] = {};
        mock.slot_to_return = fake_slot;
        mock.ent_to_return = dummy_ent;

        Phase2Services::ClientObjectSpawnOps ops{};
        ops.has_overlay_file = [](const std::string& ot_file, void* ud) -> bool {
            auto* m = static_cast<MockSpawnState*>(ud);
            ++m->overlay_calls;
            m->last_ot_file = ot_file;
            size_t sz = 0;
            return nm_has_file_overlay(ot_file.c_str(), &sz) == NM_OK;
        };
        ops.fs_exists = [](const std::string& ot_file, void* ud) -> bool {
            auto* m = static_cast<MockSpawnState*>(ud);
            ++m->fs_calls;
            m->last_ot_file = ot_file;
            return m->fs_has_file;
        };
        ops.get_object_type = [](const std::string& key, void* ud) -> void* {
            auto* m = static_cast<MockSpawnState*>(ud);
            ++m->get_ot_calls;
            m->last_canonical_key = key;
            return m->slot_to_return;
        };
        ops.create_client_object = [](void* /*slot*/, void* ud) -> void* {
            auto* m = static_cast<MockSpawnState*>(ud);
            ++m->create_calls;
            return m->ent_to_return;
        };
        ops.user_data = &mock;

        void* spawned = nullptr;

        // 4a. Non-.ot (.ao / .epk) rejected with NM_ERR_INVALID_ARG; NEVER touches overlay/fs/GetObjectType/create!
        CHECK(Phase2Services::spawn_client_object_guarded("Metadata/Pet/Mods/Maxwell/MaxwellPet.ao", ops, &spawned) == NM_ERR_INVALID_ARG);
        CHECK(Phase2Services::spawn_client_object_guarded("Metadata/Effects/Spells/breach/breach_ring.epk", ops, &spawned) == NM_ERR_INVALID_ARG);
        CHECK(spawned == nullptr);
        CHECK(mock.overlay_calls == 0 && mock.fs_calls == 0 && mock.get_ot_calls == 0 && mock.create_calls == 0);

        // 4b. Missing .ot returns NM_ERR_NOT_FOUND; NEVER touches GetObjectType or world.create_client_object!
        CHECK(Phase2Services::spawn_client_object_guarded("Metadata/Effects/DoesNotExist.ot", ops, &spawned) == NM_ERR_NOT_FOUND);
        CHECK(spawned == nullptr);
        CHECK(mock.overlay_calls == 1 && mock.fs_calls == 1 && mock.last_ot_file == "Metadata/Effects/DoesNotExist.ot");
        CHECK(mock.get_ot_calls == 0 && mock.create_calls == 0);

        // 4c. Existing overlay .ot with 0-component slot rejected BEFORE create_client_object!
        comp_end = comp_begin;
        std::memcpy(fake_ot_data + 0x18u, &comp_end, sizeof(comp_end));
        CHECK(Phase2Services::spawn_client_object_guarded("\\Metadata\\Pet\\Mods\\Maxwell\\MaxwellPet.ot", ops, &spawned) == NM_ERR_INVALID_ARG);
        CHECK(spawned == nullptr);
        CHECK(mock.get_ot_calls == 1 && mock.last_canonical_key == "Metadata/Pet/Mods/Maxwell/MaxwellPet");
        CHECK(mock.create_calls == 0);

        // 4d. Existing overlay .ot with valid loaded ObjectTypeSlot spawns cleanly with extensionless key!
        comp_end = reinterpret_cast<uintptr_t>(&fake_factories[2]);
        std::memcpy(fake_ot_data + 0x18u, &comp_end, sizeof(comp_end));
        CHECK(Phase2Services::spawn_client_object_guarded("\\Metadata\\Pet\\Mods\\Maxwell\\MaxwellPet.OT", ops, &spawned) == NM_OK);
        CHECK(spawned == dummy_ent);
        CHECK(mock.get_ot_calls == 2 && mock.last_canonical_key == "Metadata/Pet/Mods/Maxwell/MaxwellPet");
        CHECK(mock.create_calls == 1);

        CHECK(nm_unregister_file_overlay(ov_tok) == NM_OK);
        CHECK(nm_shutdown() == NM_OK);
    }

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
    if (test_phase4_prologues_hookengine_safety() != 0) return 1;
    if (test_phase4_rich_ui_tooltips_and_stash_affinity() != 0) return 1;
    if (test_phase4_geal_rulesets_and_custom_ui_registries() != 0) return 1;
    if (test_phase4_geal_builtin_register_alignment() != 0) return 1;
    if (test_phase5_prologues_and_address_table() != 0) return 1;
    if (test_phase5_u32_stack_wire_format_and_override() != 0) return 1;
    if (test_phase5_compact_sockets_and_world_vfx() != 0) return 1;
    if (test_object_type_path_and_slot_guards() != 0) return 1;

    std::printf("ALL %d NATIVEMODS C++20 SHIM TESTS PASSED!\n", g_tests_passed);
    return 0;
}
