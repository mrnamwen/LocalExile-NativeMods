# Phase 0 (`0.2.0`) Implementation & Verification Notes

**Date:** 2026-11-26  
**Scope:** NativeMods SDK/ABI `0.2.0` (Milestones M1–M7 COMPLETE + Tier 5 Hardened; clean handoff ready for Phase 1).

---

## 1. Native Shim C ABI `0.2.0` (`src/native-shim/`, M1 `e849e26`)

- **Versioning & Table Layout (`nativemods_abi.h`, `NativeShimAbi.cs`):**
  - `NM_ABI_VERSION` = `0x00000200u` (`0.2.0`); `NM_OPCODE_RESERVED_MIN` = `0x02FCu`.
  - `nm_abi_table_t` / `NmAbiTable`: **`560` bytes**, **`69` function pointers** (`8 + 69 * 8`) — 41 `0.1.0` prefix slots (`0..40`) + 28 `0.2.0` slots (`41..68`, tail-appending `ui_get_enabled`).
  - `nm_hook_context_t` / `NmHookContext` (`128 B`), `nm_native_call_desc_t` / `NmNativeCallDesc` (`104 B`).
  - `LocalExile.ClientHost.def` exports **78** symbols (`LocalExileClientHost_ImportAnchor` + `nm_*`).
- **`HookEngine` (`src/native-shim/src/hook_engine.cpp`, `exception_wall.cpp`):**
  - Replaced subset prologue decoder with full C++20 x64 instruction length decoder (`decode_x64_insn`: legacy/REX/VEX2/VEX3/EVEX, `0F`/`0F 38`/`0F 3A`, ModR/M+SIB+disp, Group 3 `F6`/`F7`, branches) + ±2 GB `alloc_exec_near` + 2-pass `relocate_instructions` (RIP-relative `[rip+disp32]`, `E8`/`E9`/`0F 8x`, short `7x`/`EB`/`E0..E3` promotion).
  - Shared multi-mod `TargetChain` per `(kind, target_address)` (inline & vtable) supporting priority-ordered `PRE`/`POST` (`nm_hook_phase_t`) callbacks, `skip_original`, `lock add`/`lock sub` `active_calls` brackets, and `>= 50 ms` quiescent reclamation.
  - `nm_call_native_guarded` supports up to **8** mixed integer/pointer + float/double (`XMM0..XMM3` + stack) arguments inside `0xE06D7363` SEH/C++ wall.
- **Address Library Single Source of Truth (`data/addresses/*.json`, `scripts/generate-address-table.py`):**
  - `data/addresses/3.29.3d.json`: **`122` symbols**, **`37` offsets**, **`18` constants** (adds `files.hash_to_wstring_view` `0x0e40af0` + 11 newly verified UI/files/runtime/network symbols).
  - `data/addresses/3.29.3c.json`: **`122` symbols**, **`35` `null`/`unverified_3_29_3c`**, **`87` verified/shared**.
  - `scripts/generate-address-table.py --check` generates `src/native-shim/src/address_table.cpp` (`0ull` / `NM_SYM_STATUS_UNVERIFIED` for `null` RVAs); zero hardcoded 3.29 RVAs remain in `client_host_shim.cpp` / `version_proxy.cpp`.
- **Leak, Extension, Chat & `0.2.0` Symbol Wiring:**
  - Fixed `MsvcWString` leaks via `runtime.wstring_destroy` (`0x134500`) in `ui_create_window`/`ui_create_window_in_parent` and `play_event`; FMOD finished non-looping tracks reaped/bounded (`64`).
  - Replaced unbounded `OverlayAndChannels::retired_indices_` with RAII `ScopedOverlayIndex` (`active_readers_` + deferred safe reclamation).
  - Native `classify_poe_utf16_text_extension` matches managed `ClientTextEncoding`: **12 BOM+CRLF** (`.ui`, `.ot`, `.otc`, `.it`, `.itc`, `.ao`, `.aoc`, `.act`, `.epk`, `.ddt`, `.pet`, `.trl`) + **3 no-BOM+CRLF** (`.sm`, `.mat`, `.amd`).
  - `nm_chat_rx_callback_t` returns `int32_t` (non-zero = suppress, `0` = forward, fail-open on exception).
  - Wired slots `41..68`: hook chains, `call_native_guarded`, `files_read_stock`, `files_invalidate` (`0x1010560`), `send_raw_packet`, `register_opcode_handler_group` (`0x29a7710`) / `unregister_opcode_handler_group`, `stream_read`, `ui_create_window_into`, `ui_get_rect`, `ui_set_position`, `ui_set_size`, `ui_set_enabled`, `ui_get_enabled` (`ui.control.flags +0x1e8 & 0x2000`), `ui_remove_child`, `ui_destroy_control`, `ui_textbox_{get_text,set_text,subscribe_changed,subscribe_submitted}`, `ui_checkbox_{get_checked,set_checked,subscribe_changed}`, `ui_unsubscribe`, `clipboard_{get_text,set_text}`.
  - Verified: `18/18` C++20 shim `ctest` + ASan/UBSan + Win64 cross-compile + Wine smoke (`r1_wine_smoke.exe`).

---

## 2. Managed Host & SDK `0.2.0` (`Sdk` & `Host`, M2 `3f0ddf3`)

- **Versioning & Manifest Enforcement (`Directory.Build.props`, `Core.cs`, `ClientModLoader.cs`):**
  - `VersionPrefix = 0.2.0`, `SdkVersion.Current == "0.2.0"`, `SdkVersion.Parsed == (0, 2, 0)`, `ProtocolVersion == 1`.
  - `ClientModManifest.Parse` & `ClientModLoader.LoadModsFromDirectory` enforce `Enabled` (root & `nativeClient`), `Depends` topological Kahn sort + cycle detection (`CyclicDependency`), and `PayloadHash` SHA-256 verification (`payloadHash` / `bundleSha256` / `integrity.packageSha256`).
- **Threading, Cleanup & Fault Isolation:**
  - `NativeEntryPoint.InitializeForNativeHost` defers `AutoLoadClientMods` onto the game thread via `bridge.EnqueueGameThread`.
  - `INativeBridge.IsOnGameThread` uses `[ThreadStatic] s_inNativeGameFrame` across all native-to-managed game-thread callbacks (`Frame`, `Drain`, `Ui*`, `RulesetRx`, `ChatRx`, `OpcodeRx`).
  - `EnqueueGameThread` returns `bool` and cleans `_pendingWork` on `NM_ERR_QUEUE_FULL`.
  - `ClientHostRuntime._subscribedUiEvents` removes stale handlers on `WindowTreeInvalidated` / `ControlSubtreeRemoved`.
  - `ScopedGameServices` wraps `Lifecycle`, `Rulesets`, `Opcodes`, `Chat`, `Scripts`, `Audio`, `World`, etc. in fault isolation + collectible-ALC disposal tracking.
  - `SimulatedNativeBridge` aligns all `122` symbol names with `3.29.3d.json`.
- **New SDK `0.2.0` Surfaces:**
  - `IClipboardService` (`game.Clipboard`), `IChatService` (`game.Chat.RegisterInterceptor` / `SendLine`), `IScriptService.RegisterChatInterceptor`.
  - `UiRect`, `IUiControl.{Rect, Bounds, IsEnabled, SetPosition, SetSize, AddChild, RemoveChild, Destroy}`, `ITextBox.{MaxLength, TextChanged, Submitted, Submit}`, `ICheckBox.{IsChecked, Checked, CheckedChanged}`, `IUiWindow.{IsOpen, Closed}`, `IUiService.{GetRootControl(), CreateWindowInto(...)}`.
  - `IStockFileProvider.ReadStockGameFile`, `FileOverlayServiceExtensions.{ReadStockGameFile, InvalidateCachedAsset}`.
  - `INetChannel.{SendRaw, OnRaw}`, `INativePacketStream`, `IOpcodeRegistry.{RegisterGroup, RegisterStreamHandler}`.
  - `IRawMemory.{InstallHook(..., vtableSlotIndex), InstallVtableHook, InstallHookChain, InstallVtableHookChain, CallEx}`.
- **`ClientModManifest` Integrity (`Core.cs`, `ClientModLoader.cs`):**
  - `PayloadHash` (`payloadHash` / `assemblyPayloadHash` / `assemblySha256`) verifies only the managed client assembly DLL; `BundleSha256` (`bundleSha256` / `integrity.packageSha256`) is tracked separately so dual-side mods declaring both `assembly` and `bundleSha256` without `payloadHash` load cleanly.
- **Resolved M2/M7 Follow-up:**
  - `tests/LocalExile.Client.Tests/AddressLibraryTests.cs` now carries `[Collection("NativeEntryPointSerial")]` matching `Host020AndReliabilityTests`, eliminating parallel race conditions on `NativeEntryPoint.ActiveRuntime`.

---

## 3. Waypoint, `AssetPipeline`, `NMB1` & BOM Fixes (M3 `d5f136e`)

- **`NMB1` Framing:** `ModBundlePacker` (`NMB1` 16-byte header + `[u32 chunkLen][0xCC 0x06][payload]` 256 KiB chunks) and `FileOverlayService.ReadBundleSlice` now use identical chunked framing.
- **12 + 3 Text Encoding Contract:**
  - **12 BOM + CRLF:** `.ui`, `.ot`, `.otc`, `.it`, `.itc`, `.ao`, `.aoc`, `.act`, `.epk`, `.ddt`, `.pet`, `.trl`.
  - **3 No-BOM + CRLF:** `.sm`, `.mat`, `.amd` (`ClientTextEncoding.EncodeUtf16LeNoBomCrlf`, `ClientTextEncoding.EncodeTextForPath`, `ModBundlePacker.EncodeClientTextNoBom`, `PoeAstWriter.BuildAmd`, `GlbToPoeMeshConverter`).
  - `TrimStart('\uFEFF')` makes BOM stripping idempotent.
- **Appendable Tables (`15` tables):** `DualModPackageValidator.AppendableRegistryTables` includes `BaseItemTypes`, `ItemVisualIdentity`, `CurrencyItems`, `MtxTypes`, `MtxTypeGameSpecific`, `Pet`, `MonsterVarieties`, `NPCs`, `Chests`, `Projectiles`, `MiscObjects`, `MiscAnimated`, `HideoutDoodads`, `ExecuteGEAL`, `ClientStrings`.
- **MurmurHash2 Parity:** `PoeContentHash.MurmurHash2(string)` now encodes via `Encoding.UTF8`, matching `PoeHashing.Hash32(string)` on non-ASCII strings.

---

## 4. `LocalExile` Core PR Branch Part 1 & `ServerBridge 0.2.0` (M4 & M5 `b6a4b2c`)

- **`LocalExile` Local Branch (`feat/nativemods-phase0-core-hooks`, unpushed, ready for Part 2):**
  - `5e72a1b7f`: `Item.CustomName` (`MagicProperties` bit `0x80` + `CharacterSaves.SavedItem` round-trip).
  - `759a8ae29`: `GameEvents.{ClientReady, ClientMessage, LevelUp, CosmeticChanged}`.
  - `9d5300859`: `Client.For(player)` (`0x02C9`/`0x02CA` + `/nm`), `Client.OnRuleset`, `Client.OnCommand`.
- **`ServerBridge 0.2.0` (`src/LocalExile.Client.ServerBridge/`):**
  - `ClientBridgeHost`: `HostChannelId` (`"__host"`), `DefaultChannel`, `OnClientReady`, `ClientReadyAsync`, `HandlerError`, `IsClientReady`, `HasMod`, `RemoveSessionAsync`, async & channel-less `On<T>`/`SendAsync<T>`/`TrySendAsync<T>`.
  - `IClientUiSession` / `ServerUiWindowHandle`: `IsOpen`, `Closed` / `OnClosed`, `OnButtonClickAsync` / `OnControlEventAsync`, `IClientCommandBatch` (`BeginBatch`, `BatchAsync`, `FlushAsync`).
  - `LocalExileGatewayHost` (`Create`/`Attach`/`RegisterCommand`/`RegisterPacket`): eliminates per-mod sink/bus/packet/command glue, re-frames `global::Client.OnRuleset`, exposes `OnCosmeticChanged`/`OnClientReady`/`OnClientMessage`.
    - **Name Collision Invariant:** Files importing `LocalExile.Client.ServerBridge` that call LocalExile's core channel directly MUST qualify **`global::Client.For(player)`** to avoid colliding with `LocalExile.Client.ServerBridge.Client`.
  - `LocalExileGatewayTransactions` & `LocalExileItemAffixService`: `LocateCurrencyStack`, `LocateGatewayItem` (`IsWorn` rows `1..11, 23, 54, 78, 102`), `GrantItemsAsync`, `StealAffixOnGatewayItem`, `ReplaceAffixOnGatewayItem`, `SendInventoryDiffsAsync`, `Item.CustomName` helpers.

---

## 5. Showcase Refactor & Solution Integration (M6)

- **`examples/DualMod.SiphonOrb/**` (`0.2.0`):**
  - `mod.json`: `sdkVersionRange` bumped to `">=0.2.0 <2.0.0"`.
  - `SiphonLocalExileEntry.cs`: refactored onto `LocalExileGatewayHost.Create(...)`, `LocalExileGatewayTransactions` (`LocateCurrencyStack`, `LocateGatewayItem`, `GrantItemsAsync`), `StealAffixOnGatewayItem` / `ReplaceAffixOnGatewayItem` (preserving explicit `EmptyOrbDefinition.TypeHash` / `ChargedOrbDefinition.TypeHash` pre-checks and `chargedCustomName: null`), and `global::Client.For(player).SendChat(...)`.
  - `SiphonServerMod.cs`: converted `SelectSiphonAffixRequest` / `CancelSiphonPicker` handlers to async `On<T>`.
  - `SiphonClientMod.cs`: added `IsOnGameThread` / `RunOnGameThread` marshaling, `IUiWindow.IsOpen` / `Closed` reload recovery, `_unloading` cleanup without spurious `CancelSiphonPicker` on unload, preserving `SiphonPicker.ui`, `btn.Enabled = true` on locked rows, and `HideWindow()` `Visible = false`.
- **`examples/DualMod.MaxwellPet/**` (`0.2.0`):**
  - `mod.json`: `sdkVersionRange` bumped to `">=0.2.0 <2.0.0"`.
  - `MaxwellLocalExileEntry.cs`: refactored onto `LocalExileGatewayHost.Create(...)` + event-driven **`OnCosmeticChanged`** (eliminating `GameEvents.AreaTick` polling) + `global::Client.For(player).SendChat(...)`.
  - `MaxwellServerMod.cs`: converted `ToggleMaxwellRequest` / `MaxwellAudioControl` / `OnClientReadyAsync` to async `0.2.0` handlers (and removed `.GetAwaiter().GetResult()` in `OnCosmeticEquipObserved`).
  - `MaxwellClientMod.cs`: added `IsOnGameThread` / `RunOnGameThread` marshaling, `ITextBox.MaxLength = 6` on `spin_box` & `float_box`, `IUiWindow.IsOpen` / `Closed` reload recovery, and `_unloading` cleanup while keeping `MaxwellPanel.ui` + FMOD audio + `MaxwellDance` untouched.
- **`examples/DualMod.HarvestGarden/**`:**
  - `GardenServerMod.cs` converted to async `On<T>`; `GardenClientMod.cs` hardened with `IsOnGameThread` marshaling, `IsOpen` / `Closed`, and `_unloading` guards.
  - `examples/DualMod.HarvestGarden/mod.json` keeps `">=0.1.0-poc <2.0.0"` (already `0.2.0`-compatible) so `tests/LocalExile.Client.Waypoint.Tests/UiLayoutLinterAndValidatorTests.cs:111` (`SemVer.Parse("0.1.0-poc")`) continues passing unmodified.
- **`NativeMods.slnx`:** Integrated `LocalExile.Client.AssetPipeline`, `DualMod.MaxwellPet.*`, `DualMod.SiphonOrb.*`, `DualMod.Phase0E2E.*`, and all 7 test projects (`Tests`, `Patcher.Tests`, `Waypoint.Tests`, `AssetPipeline.Tests`, `MaxwellPet.Tests`, `SiphonOrb.Tests`, `Phase0E2E.Tests` — `22` solution projects total).

---

## 6. Milestone M7: `Phase0E2E`, Proton UI Fixes, Tier 5 Hardening & Phase 1 Handoff

- **Live PoE `3.29.3d` UI & Slab-Pool Fixes (`src/native-shim/src/live_engine_hooks.cpp`, `data/addresses/3.29.3{c,d}.json`):**
  - **`Control::ReleaseOrDestroy` (`0x18b6810`):** Slab-pooled `UI::Control` instances (`[control+0x08] != 0`) MUST be returned via `Control::ReleaseOrDestroy` (`40 56 48 83 EC 20 F6 81 E8 01 00 00 01 48 8B F1`), never `vtbl[8]` deleting destructor (heap-only fallback when `[control+0x08] == 0`).
  - **`Control::SetEnabled` (`0x18b0c70`) & `nm_ui_get_enabled` (slot 68):** Verified bit 13 (`0x2000u`) at `[control+0x1e8]` (`ui.control.flags`) governs control enabled state; added `nm_ui_get_enabled` expanding `nm_abi_table_t` to **`560 B` / `69` slots / `78` `.def` exports / `37` offsets**.
- **Tier 5 Adversarial Hardening (C++20 Shim & Managed Host/Waypoint):**
  - `HookEngine::compute_stolen_length`: stops/rejects (`0` → `NM_ERR_INVALID_ARG`) on function-terminating opcodes (`ret` `C3`/`C2`/`CB`/`CA`, `int3` `CC`, `ud2` `0F 0B`, unconditional `E9`/`EB` `jmp`) before `14` bytes unless an earlier forward conditional branch in the decoded window targets at or past that terminator.
  - `HookEngine::relocate_instructions`: returns `false` when `si.internal_target` does not land on an exact decoded instruction boundary.
  - `HookEngine::build_dispatcher_stub` & `dispatch_chain_body`: emits & registers DWARF `.eh_frame` CIE+FDE (`__register_frame`/`__deregister_frame` on SysV) and Win64 `RUNTIME_FUNCTION`+`UNWIND_INFO` (`RtlAddFunctionTable`/`RtlDeleteFunctionTable` on MinGW/Win64) + `ActiveCallGuard` + `nm_hook_invoke_original` CFI/SEH wrapper so C++ exceptions thrown inside `PRE`, `POST`, or `original_callable` unwind cleanly to outer `catch` blocks and decrement `active_calls` without leaking retired chains.
  - `.sm`/`.mat`/`.amd` BOM hardening: native `try_normalize_poe_text` strips `FF FE`/`FE FF`/`EF BB BF` on `.sm`/`.mat`/`.amd` (and ensures `FF FE` + CRLF on `.ui`/`.ot`/etc.); `DualModPackageValidator` emits `ENC001` error on BOM-contaminated loose `.sm`/`.mat`/`.amd` files (`WaypointModInstaller.Install` allows auto-normalized `ENC001` during `DeployClientFiles`).
  - C ABI network guards: `OverlayAndChannels::register_opcode_handler_group` rejects `min_opcode < 0x02FC` (`NM_ERR_INVALID_ARG`); `nm_send_raw_packet` & `send_live_raw_packet` enforce `1..65536` (`NM_ERR_INVALID_ARG` on 0/null, `NM_ERR_PAYLOAD_TOO_LARGE` on `> 65536`). Managed `SendRawPacket` enforces `<= 65536` while preserving 0-byte `ClientTransports.SendRaw` parity.
  - `live_engine_hooks.cpp`: `reset()` deletes & clears `g_ui_handlers`, modeled struct offsets use `resolve_offset_or(build_id_, ...)`, and `ui_textbox_*` / `ui_checkbox_*` verify `ui.vtable_textbox` / `ui.vtable_checkbox` inside `invoke_guarded`.
  - Managed polish: `ContentRegistry` rejects duplicate `(TableName, EntryKey)` & `MetadataPath`; `UiTextBoxNode.OnNativeTextChanged` pushes `MaxLength`-clamped text back to native cleanly; embedded child `UiWindowNode.Close()` & `FinalizeReplacedWindow` call `Destroy()` (`_destroyed = true`); `NativeEntryPoint` runs `AutoLoadClientMods` under `lock (Gate)`; `DrainGameThreadQueue(maxItems: 0)` returns `0` cleanly on both bridges.
- **Verification Status:** `22/22` C++20 shim tests pass on both Linux (`ctest`) and Win64/Wine (`nativemods_shim_tests.exe` + `r1_wine_smoke.exe`), all `22`-project `NativeMods.slnx` suites (`Tests` x3 + `Patcher`/`Waypoint`/`AssetPipeline`/`MaxwellPet`/`SiphonOrb`/`Phase0E2E`) pass with 0 warnings/errors, Maxwell/Siphon assets build cleanly, and `./scripts/run-phase0-proton.sh --rebuild` exercises `E2E-01`..`E2E-16` + non-silent FMOD PCM + stock SHA-256 baseline restoration on live PoE `3.29.3d`. Ready for Phase 1 session handoff!
