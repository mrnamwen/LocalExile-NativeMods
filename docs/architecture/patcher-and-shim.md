# Universal PE32+ Patcher & Native Script Extender Shim

This guide covers the two native-boundary layers of NativeMods:

1. **`src/LocalExile.Client.Patcher/`** — Cross-platform `.NET 10` PE32+ import-table patcher library and CLI.
2. **`src/native-shim/`** — C++20 Win64 script extender (`LocalExile.ClientHost.dll`, optional `version.dll` proxy) and its public C ABI (`include/nativemods_abi.h`).

---

## 1. Universal PE32+ Patcher (`LocalExile.Client.Patcher`)

### CLI Usage

```bash
dotnet run --project src/LocalExile.Client.Patcher -- <command> [options]
```

Exact commands and flags implemented in `Program.cs`:

```text
check <exe> [--addresses <dir>] [--allow-synthetic] [--dll <name>] [--json]
patch <exe> [--output <out>] [--dll <name>] [--anchor <sym>] [--addresses <dir>] [--allow-synthetic] [--backup]
patch --proxy-dll --client-dir <dir> --proxy-source <dll> [--host-source <dll>] [--proxy-name version.dll]
verify <exe> [--dll <name>] [--addresses <dir>]
verify --proxy-dll --client-dir <dir>
unpatch <exe> [--output <out>]
unpatch --proxy-dll --client-dir <dir>
dump-addresses [--build <3.29.3d|3.29.3c>] [--category <cat>] [--addresses <dir>] [--json]
```

| Command | Behavior |
|---|---|
| `check` | Parses PE32+ headers (`Pe32PlusImage`), identifies build via `BuildMatchKind` (`ExactStockSha256`, `LocalExileKeyPatched329` via 6 verified code anchors, or `SyntheticAllowed`), inspects section count, header slack, existing imports, and Authenticode signature (`SecurityDir`). |
| `patch` | Appends a new `.nmimp` section relocating and extending the import descriptor array with `LocalExile.ClientHost.dll` (`LocalExileClientHost_ImportAnchor`), recomputes PE checksum, and embeds an `NMUNPAT1` reversible metadata record. Idempotent if the DLL is already imported. With `--proxy-dll`, deploys `version.dll` + `.nativemods-proxy.json` manifest instead. |
| `verify` | Confirms PE32+ validity, presence of the `LocalExile.ClientHost.dll` import descriptor, and `NMUNPAT1` unpatch metadata (or verifies `.nativemods-proxy.json` SHA-256 hashes in `--proxy-dll` mode). |
| `unpatch` | Reads the embedded `NMUNPAT1` record inside `.nmimp` and restores the executable **byte-for-byte** to its exact original SHA-256 (including any trailing EOF Authenticode signature bytes), requiring no external `.bak` file. |
| `dump-addresses` | Loads `data/addresses/` via `AddressLibraryCatalog` and dumps symbols filtered by `--build` (`3.29.3d` or `3.29.3c`) and optional `--category` (table or `--json`). |

### Library API (`LocalExile.Client.Patcher`)

| Type | Key Members |
|---|---|
| `ClientPatcher` | `CheckBytes`, `PatchBytes`, `PatchFile`, `VerifyBytes`, `UnpatchFile`, `DeployProxyDll`, `VerifyProxyDll`, `RemoveProxyDll`; constants `DefaultHostDllName = "LocalExile.ClientHost.dll"`, `DefaultImportAnchorSymbol = "LocalExileClientHost_ImportAnchor"`, `DefaultProxyDllName = "version.dll"`, `PatchSectionName = ".nmimp"`, `ProxyManifestFileName = ".nativemods-proxy.json"`. |
| `AddressLibraryCatalog` | `LoadFromDirectory`, `LocateDefaultDirectory`, `GetByGameVersion`, `MatchExecutableSha256`, `MatchByVerifiedCodeSignatures`. |
| `Pe32PlusImage` | `Parse`, `ReadImportDescriptors`, `TryRvaToFileOffset`, `HeaderSlackBytes`, `SecurityDirectory`, `Sha256Hex`. |
| Result Records | `CheckResult`, `PatchOptions`, `PatchResult`, `VerifyResult`, `UnpatchResult`, `ProxyDeploymentManifest`. |

### Import-Table Patching vs. `version.dll` Proxy Fallback

| Aspect | Import-Table Patching (Primary) | `version.dll` Proxy Fallback (`--proxy-dll`) |
|---|---|---|
| **How it loads** | Consumes 40 of the verified **136 bytes of PE header slack** (`SecTableEnd = 0x378`, `SizeOfHeaders = 0x400`) to add `.nmimp`, relocates all existing import descriptors, and appends `LocalExile.ClientHost.dll` importing `LocalExileClientHost_ImportAnchor`. | Deploys `version.dll` (built from `src/native-shim/src/version_proxy.cpp`) + `.nativemods-proxy.json` beside `Game.patched.exe` to forward standard Win32 `VERSION.dll` exports and load `LocalExile.ClientHost.dll`. |
| **LocalExile compatibility** | Recognizes both unpatched 3.29 stock builds (`ExactStockSha256`) and `GameServer.Auth`-patched copies (`LocalExileKeyPatched329`). | Zero edits to `Game.patched.exe`. |
| **Authenticode (`SecurityDir`) & `BoundImportDir`** | Captures any trailing EOF Authenticode bytes (`0x2958` B at `0x4bb4c00` on `Game.exe`) into `NMUNPAT1` inside `.nmimp`, clears `SecurityDir`/`BoundImportDir` safely while patched, and restores the exact original bytes & SHA-256 on `unpatch`. | Untouched. |
| **Linux / Proton** | Works out of the box under Wine/Proton with zero environment variables. | Requires `WINEDLLOVERRIDES="version=n,b"` (printed by `patch --proxy-dll`). |

---

## 2. Native Script Extender Shim (`src/native-shim/`)

### Public C ABI (`include/nativemods_abi.h`)

- **ABI Version:** `0.6.0` (`NM_ABI_VERSION = 0x00000600u`), Win64 / `__cdecl` (`NM_CALL`).
- **Import Anchor:** `NM_API void NM_CALL LocalExileClientHost_ImportAnchor(void)` — anchor export referenced by the `.nmimp` import descriptor so the Windows/Wine loader eagerly maps `LocalExile.ClientHost.dll`.
- **Managed Entry Point:** `[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] public static int LocalExile.Client.Host.NativeEntryPoint.Initialize(nint abiTablePtr)` receives a `const nm_abi_table_t*` (`struct_size = 1168`, `abi_version = 0x00000600u`, and **`145` function-pointer slots `0..144`**; **`162` DLL exports** in `src/native-shim/src/LocalExile.ClientHost.def`).
- **Verified C ABI Struct Sizes (64-bit C++ `static_assert` & C# `LayoutKind.Sequential` parity):**
  - `nm_abi_table_t` / `NmAbiTable`: **1168 bytes** (`145` function pointers, strictly backward-appended across `0.2.0` `0..68`, `0.3.0` `69..88`, `0.4.0` `89..106`, `0.5.0` `107..138`, and `0.6.0` `139..144`: `StackSetU32WireEnabled`, `StackSetFormatMillionsEnabled`, `StackSetEntityOverride`, `UiSetCompactSocketsEnabled`, `WorldPlayEffect`, `WorldCreateClientObjectEx`)
  - `nm_host_info_t` / `NmHostInfo`: **120 bytes** · `nm_symbol_info_t`: **48 bytes** · `nm_game_alloc_header_t`: **16 bytes** · `nm_exception_info_t`: **528 bytes**
  - Phase 1 (`0.3.0`) structs: `nm_screens_state_t` (**280 B**), `nm_input_event_t` (**40 B**), `nm_item_snapshot_t` (**448 B**), `nm_hover_state_t` (**952 B**)
  - Phase 2 (`0.4.0`) structs: `nm_world_entity_query_t` (**160 B**), `nm_world_entity_snapshot_t` (**760 B**), `nm_stat_entry_t` (**8 B**), `nm_area_info_t` (**352 B**), `nm_item_filter_state_t` (**784 B**), `nm_item_filter_result_t` (**72 B**), `nm_camera_state_t` (**120 B**)
  - Phase 4 (`0.5.0`) structs & constants: `nm_affinity_target_t` (**4 B**), `nm_affinity_route_result_t` (**32 B**), `nm_native_ruleset_callbacks_t` (**64 B**), `nm_custom_ui_control_callbacks_t` (**32 B**), `NM_RULESET_FIRST_FREE_NATIVE_ID = 0xAEu`, `NM_AFFINITY_MAX_TARGETS = 4u`
- **`HookEngine` & Phase 4/5 Win64 ABI Safety Rules (`35/35` C++20 shim tests):**
  - Shared Pre/Post hook chains (`install_inline_chain`) register DWARF `.eh_frame` / Win64 `RUNTIME_FUNCTION`+`UNWIND_INFO` unwind tables and verify multi-chain `expectedBytes` tails beyond `stolen_len` directly against live unpatched memory.
  - **`> 8`-arg Win64 limit:** `install_inline_chain` forwards at most `8` arguments (`args[0..7]`). Any native target taking `> 8` Win64 arguments — such as 10-arg `world.client_world_init_area` (`0x141653ee0`) — MUST use a typed raw `hooks.install_inline` detour forwarding all parameters (`a0..a9`).
  - **Slab-pool control teardown:** `ui_destroy_control` calls `Control::ReleaseOrDestroy` (`0x18b6810`) for slab-pooled controls (`[control + 0x08] != 0`) instead of `vtbl[8]`.
  - **16-Byte Alignment for `movaps` (`GealBuiltinEntry` & Game-Bound Structs):** `geal.builtin_register_entry` (`0x141de9780`) loads entries via `movaps xmm0, XMMWORD PTR [rdi]`. `GealBuiltinEntry`, `GealRegisterCtx`, `ImageHandle16`, `GealCompiledArgSlot`, and stack `MsvcWString`/`MsvcStdString`/`MsvcStdFunc64` structs passed to game functions MUST carry `alignas(16)`.
  - **Exact 7-Slot MSVC `std::_Func_base` Vtable Layout:** All shim functors (`g_geal_void_impl_vtable[7]`, `g_geal_value_impl_vtable[7]`, `g_custom_ui_impl_vtable[7]`) match VS2019 `0x143031688` (`[0]=_Copy`, `[1]=_Move`, `[2]=_Do_call`, `[3]=_Type`, `[4]=_Delete_this`, `[5]=_Get`, `[6]=~_Func_base(flags)`).
  - **Live Hooks (`38/38` OK on `3.29.3d`):** `[OVERLAY]` 6/6, `[UI]` 1/1, `[NET]` 3/3, `[PHASE1]` 11/11, `[PHASE2]` 4/4, `[PHASE4]` 8/8 (`tab`, `combo`, `listbox`, `tooltip`, `aff_bit`, `aff_tgt`, `ui_prop`, `ui_destroy`), **`[PHASE5]` 5/5** (`stack_deser`, `fmt_count`, `sock_cell`, `sock_rebuild`, `ig_leave`), plus Frame hook.

#### Exported C API Surface

| Group | Functions | Notes |
|---|---|---|
| **Info & Symbols** | `nm_get_abi_table`, `nm_get_host_info`, `nm_lookup_symbol`, `nm_get_symbol_at`, `nm_verify_bytes` | Resolves RVAs against the loaded `image_base` (`DYNAMIC_BASE` safe) and verifies hex byte patterns with `??` wildcards. |
| **Hooks** | `nm_install_inline_hook`, `nm_install_inline_hook_at`, `nm_install_vtable_hook`, `nm_remove_hook` | Byte-verified x64 inline detours + trampolines (`NM_HOOK_INLINE_DETOUR`) and vtable slot swaps (`NM_HOOK_VTABLE_SLOT`). |
| **Frame & Queue** | `nm_register_frame_callback`, `nm_unregister_frame_callback`, `nm_enqueue_main_thread`, `nm_drain_main_thread_queue` | Drains queued work on the game thread at `StateStack::PassA` (`0x1528fbf`) with fallback draining when the state stack is empty. |
| **Exception Wall** | `nm_call_guarded` | Catches Win64 SEH and MSVC C++ exceptions (`NM_MSVC_CXX_EXCEPTION_CODE = 0xE06D7363`), populating `nm_exception_info_t`. |
| **Game Allocator** | `nm_game_alloc`, `nm_game_free` | Uses the client slab allocator (`0x2c8cf60` / `0x2c8ca30`) with packed 16-byte `nm_game_alloc_header_t` (`u64 size; u32 reserved0; u8 log2_align; u16 tag; u8 reserved1`; `NM_DEFAULT_ALLOC_TAG = 0x0500` → `0x00050004`). Never CRT-free game memory. |
| **File Overlay** | `nm_register_file_overlay`, `nm_unregister_file_overlay`, `nm_resolve_file_overlay` | Bridges native read-result swaps to `IFileOverlayService`. |
| **Channels** | `nm_register_ruleset_handler`, `nm_unregister_ruleset_handler`, `nm_send_ruleset_packet`, `nm_register_chat_channel_handler`, `nm_unregister_chat_channel_handler`, `nm_send_chat_line` | Primary HostRuleset (`0x02C9`/`0x02CA`, max `0xFFFF`) and ChatFallback (`0x0017` `"##NM "` / `0x0015`). |
| **Inbound Dispatch & Lifecycle** | `nm_dispatch_ruleset_rx`, `nm_dispatch_chat_rx`, `nm_set_packet_tx_override`, `nm_bootstrap_managed_host`, `nm_initialize_for_build`, `nm_shutdown` | Internal hook dispatchers, host-side test overrides, deferred CoreCLR bootstrapper, and lifecycle control. |

#### Core ABI Enums

- **`nm_build_id_t`**: `NM_BUILD_UNKNOWN = 0`, `NM_BUILD_3_29_3C_327984 = 1`, `NM_BUILD_3_29_3C_328126 = 2`, `NM_BUILD_3_29_3D_328228 = 3`, `NM_BUILD_SYNTHETIC = 0x7FFF`.
- **`nm_status_t`**: `NM_OK = 0`, `NM_ERR_INVALID_ARG = -1`, `NM_ERR_NOT_INITIALIZED = -2`, `NM_ERR_UNSUPPORTED_BUILD = -3`, `NM_ERR_SYMBOL_NOT_FOUND = -4`, `NM_ERR_SYMBOL_UNVERIFIED = -5`, `NM_ERR_BYTE_MISMATCH = -6`, `NM_ERR_HOOK_ALREADY_INSTALLED = -7`, `NM_ERR_HOOK_NOT_FOUND = -8`, `NM_ERR_ALLOC_FAILED = -9`, `NM_ERR_GAME_EXCEPTION = -10`, `NM_ERR_BUFFER_TOO_SMALL = -11`, `NM_ERR_QUEUE_FULL = -12`, `NM_ERR_RUNTIME_BOOT_FAILED = -13`, `NM_ERR_NOT_SUPPORTED_ON_PLATFORM = -14`, `NM_ERR_PAYLOAD_TOO_LARGE = -15`, `NM_ERR_ALREADY_EXISTS = -16`, `NM_ERR_NOT_FOUND = -17`.
- **`nm_host_flags_t`**: `NM_HOST_FLAG_NONE`, `NM_HOST_FLAG_INITIALIZED`, `NM_HOST_FLAG_FRAME_HOOK_ARMED`, `NM_HOST_FLAG_CORECLR_BOOTING`, `NM_HOST_FLAG_CORECLR_BOOTED`, `NM_HOST_FLAG_PROXY_MODE`, `NM_HOST_FLAG_SIMULATED`.
- **`nm_symbol_kind_t`**: `NM_SYM_CODE = 0`, `NM_SYM_DATA = 1`, `NM_SYM_VTABLE = 2`, `NM_SYM_IMPORT_SLOT = 3`, `NM_SYM_GLOBAL_REF = 4`.
- **`nm_symbol_status_t`**: `NM_SYM_STATUS_UNVERIFIED = 0`, `NM_SYM_STATUS_STATIC_VERIFIED = 1`, `NM_SYM_STATUS_LIVE_VERIFIED = 2`, `NM_SYM_STATUS_DOC_VERIFIED = 3`, `NM_SYM_STATUS_CODE_SHARED = 4`.

---

## 3. CoreCLR Boot Sequence & Runtime Mitigations

1. **Minimal `DllMain`:** Never invoke `hostfxr`, allocate managed objects, or perform heavy I/O inside `DllMain` (avoids Windows/Wine loader-lock deadlocks).
2. **Build recognition & expected-byte verification:** Matches the loaded client against 3.29.3c / 3.29.3d; unknown builds leave the client stock.
3. **Private Win64 .NET 10 runtime:** Neither Proton prefixes (`data/wine-prefix`) nor self-contained LocalExile Windows releases include a shared Windows .NET 10 runtime, so `Client/nativemods-host/` ships a private self-contained `win-x64` runtime (`hostfxr.dll`, `coreclr.dll`, `LocalExile.Client.Host.dll`).
4. **`DeadlockDetector` (`0xe3ed10`) mitigation:** `nm_bootstrap_managed_host` initializes `hostfxr`/CoreCLR asynchronously off the game thread, then hands `nm_abi_table_t*` to `LocalExile.Client.Host.NativeEntryPoint.Initialize` on the first frame tick (workstation concurrent GC keeps pauses minimal).
5. **SEH & Wine/Proton safety:**
   - Every native-to-game invocation passes through `nm_call_guarded` so MSVC C++ exceptions (`0xE06D7363`) are caught before the game's unhandled-exception filter.
   - Managed code on the game thread executes strictly inside bounded bridge calls.
   - Because `0x1528fbf` is skipped while the state stack is empty, queued tasks use a fallback drain mechanism during startup/transitions.

---

## 4. Building & Testing the Native Shim (`src/native-shim/CMakeLists.txt`)

### Linux Host-Side Unit Tests

```bash
cmake -S src/native-shim -B build/native-shim -DCMAKE_BUILD_TYPE=Release
cmake --build build/native-shim
ctest --test-dir build/native-shim --output-on-failure
```

### Windows x64 Native MSVC Build (`LocalExile.ClientHost.dll` + `version.dll`)

```bash
cmake -S src/native-shim -B build/native-shim-win64 -G "Visual Studio 17 2022" -A x64
cmake --build build/native-shim-win64 --config Release
```

### Linux → Windows x64 Cross-Compile (requires `mingw-w64-gcc` sysroot)

```bash
cmake -S src/native-shim -B build/native-shim-win64 \
  -DCMAKE_SYSTEM_NAME=Windows \
  -DCMAKE_C_COMPILER=x86_64-w64-mingw32-gcc \
  -DCMAKE_CXX_COMPILER=x86_64-w64-mingw32-g++ \
  -DCMAKE_BUILD_TYPE=Release
cmake --build build/native-shim-win64
```
