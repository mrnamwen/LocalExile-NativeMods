#include "nativemods_abi.h"
#include "address_table.hpp"
#include "exception_wall.hpp"

#include <atomic>
#include <cassert>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

#if !defined(_WIN32)
#  include <sys/mman.h>
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
    CHECK(table->abi_version == NM_ABI_VERSION);
    CHECK(table->get_host_info != nullptr);
    CHECK(table->lookup_symbol != nullptr);
    CHECK(table->install_inline_hook != nullptr);
    CHECK(table->install_vtable_hook != nullptr);
    CHECK(table->call_guarded != nullptr);
    CHECK(table->game_alloc != nullptr);
    CHECK(table->send_ruleset_packet != nullptr);

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
    CHECK(host.symbol_count >= 50);

    nm_symbol_info_t sym{};
    CHECK(nm_lookup_symbol("frame.state_stack_pass_a_hook", &sym) == NM_OK);
    CHECK(sym.rva == 0x1528fbfull);
    CHECK(sym.runtime_address == kRebased + 0x1528fbfull);
    CHECK(std::strcmp(sym.expected_bytes_hex, "488b5d284883eb10") == 0);

    CHECK(nm_lookup_symbol("rulesets.creator_table", &sym) == NM_OK);
    CHECK(sym.rva == 0x475dc70ull);
    CHECK(sym.runtime_address == kRebased + 0x475dc70ull);

    CHECK(nm_lookup_symbol("network.hnc_mask", &sym) == NM_OK);
    CHECK(sym.rva == 0x2feaeb0ull);

    CHECK(nm_lookup_symbol("nonexistent.symbol", &sym) == NM_ERR_SYMBOL_NOT_FOUND);

    // 3.29.3c: code RVAs resolve, HNC mask resolves to 0x2feaed0, unverified data returns NM_ERR_SYMBOL_UNVERIFIED
    CHECK(nm_initialize_for_build(
              NM_BUILD_3_29_3C_328126,
              kRebased,
              0x4d9a000u,
              "01a22facf36886b39bca74793c0d8c7295b1495f1625b4ebc7b23c2de32a70cf") == NM_OK);

    CHECK(nm_lookup_symbol("frame.state_stack_pass_a_hook", &sym) == NM_OK);
    CHECK(sym.rva == 0x1528fbfull);
    CHECK(sym.status == NM_SYM_STATUS_CODE_SHARED);

    CHECK(nm_lookup_symbol("network.hnc_mask", &sym) == NM_OK);
    CHECK(sym.rva == 0x2feaed0ull);

    CHECK(nm_lookup_symbol("rulesets.creator_table", &sym) == NM_ERR_SYMBOL_UNVERIFIED);
    CHECK(sym.rva == 0ull);
    CHECK(sym.runtime_address == 0ull);
    CHECK(sym.status == NM_SYM_STATUS_UNVERIFIED);

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
TestAddFn g_inline_trampoline = nullptr;
int g_detour_hits = 0;

int inline_detour_fn(int x) {
    ++g_detour_hits;
    return g_inline_trampoline(x) + 100;
}

int vtable_orig_fn(int x) { return x * 2; }
TestAddFn g_vtable_orig = nullptr;
int vtable_detour_fn(int x) { return g_vtable_orig(x) + 7; }

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

    // Duplicate hook at same address MUST be rejected
    nm_hook_handle_t dup = NM_HOOK_INVALID;
    CHECK(nm_install_inline_hook_at(
              reinterpret_cast<uint64_t>(code_page),
              nullptr,
              reinterpret_cast<void*>(&inline_detour_fn),
              nullptr,
              &dup) == NM_ERR_HOOK_ALREADY_INSTALLED);

    // Call target: runs detour -> trampoline -> original -> +100
    CHECK(target_fn(10) == 115);
    CHECK(g_detour_hits == 1);

    // Remove hook: restores original 14 bytes and original behavior
    CHECK(nm_remove_hook(handle) == NM_OK);
    CHECK(nm_remove_hook(handle) == NM_ERR_HOOK_NOT_FOUND);
    CHECK(target_fn(10) == 15);
    CHECK(code[0] == 0x90 && code[13] == 0x90);

    munmap(code_page, 4096);
#endif

    // Vtable hook test
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

    CHECK(nm_remove_hook(vt_handle) == NM_OK);
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

    // File overlay registration + path normalization
    static uint8_t kUiPayload[] = {'U', 'I', '!'};
    uint64_t ov_tok = 0;
    auto ov_cb = [](const char* path, uint8_t** out_data, size_t* out_size, void*) -> nm_status_t {
        if (std::strcmp(path, "metadata/ui/mods/garden/stockpile.ui") == 0) {
            *out_data = kUiPayload;
            *out_size = sizeof(kUiPayload);
            return NM_OK;
        }
        return NM_ERR_SYMBOL_NOT_FOUND;
    };
    CHECK(nm_register_file_overlay(ov_cb, nullptr, nullptr, &ov_tok) == NM_OK);

    uint8_t* data = nullptr;
    size_t sz = 0;
    CHECK(nm_resolve_file_overlay("Metadata\\UI\\Mods\\Garden\\Stockpile.ui", &data, &sz) == NM_OK);
    CHECK(data == kUiPayload && sz == sizeof(kUiPayload));
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

    // Chat fallback (S4: ##NM prefix intercepted, normal chat passed through)
    std::string chat_captured;
    uint64_t chat_tok = 0;
    auto chat_cb = [](const char*, const char* text, void* ud) {
        *static_cast<std::string*>(ud) = text;
    };
    CHECK(nm_register_chat_channel_handler(chat_cb, &chat_captured, &chat_tok) == NM_OK);
    CHECK(nm_dispatch_chat_rx("System", "Hello Wraeclast") == 0);
    CHECK(chat_captured.empty());
    CHECK(nm_dispatch_chat_rx("System", "##NM open garden") == 1);
    CHECK(chat_captured == "##NM open garden");

    CHECK(nm_send_chat_line("/nm plant WildSeed") == NM_OK);
    CHECK(tx_cap.opcode == NM_OPCODE_CHAT_C2S);

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

    std::printf("ALL %d NATIVEMODS C++20 SHIM TESTS PASSED!\n", g_tests_passed);
    return 0;
}
