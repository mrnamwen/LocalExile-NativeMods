# Phase 0 (`0.2.0`) Implementation & Verification Notes

**Date:** 2026-11-26  
**Scope:** NativeMods SDK/ABI `0.2.0` (Milestones M1–M6 complete; M7 live `Xvfb` `3.29.3d` Proton run pending).

---

## 1. Native Shim C ABI `0.2.0` (`src/native-shim/`, M1 `e849e26`)

- **Versioning & Table Layout (`nativemods_abi.h`, `NativeShimAbi.cs`):**
  - `NM_ABI_VERSION` = `0x00000200u` (`0.2.0`); `NM_OPCODE_RESERVED_MIN` = `0x02FCu`.
  - `nm_abi_table_t` / `NmAbiTable`: **`552` bytes**, **`68` function pointers** (`8 + 68 * 8`) — 41 `0.1.0` prefix slots (`0..40`) + 27 `0.2.0` slots (`41..67`).
  - `nm_hook_context_t` / `NmHookContext` (`128 B`), `nm_native_call_desc_t` / `NmNativeCallDesc` (`104 B`).
  - `LocalExile.ClientHost.def` exports **77** symbols (`LocalExileClientHost_ImportAnchor` + `nm_*`).
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
  - Wired slots `41..67`: hook chains, `call_native_guarded`, `files_read_stock`, `files_invalidate` (`0x1010560`), `send_raw_packet`, `register_opcode_handler_group` (`0x29a7710`) / `unregister_opcode_handler_group`, `stream_read`, `ui_create_window_into`, `ui_get_rect`, `ui_set_position`, `ui_set_size`, `ui_set_enabled`, `ui_remove_child`, `ui_destroy_control`, `ui_textbox_{get_text,set_text,subscribe_changed,subscribe_submitted}`, `ui_checkbox_{get_checked,set_checked,subscribe_changed}`, `ui_unsubscribe`, `clipboard_{get_text,set_text}`.
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
- **Known M2 Test Follow-up:**
  - `tests/LocalExile.Client.Tests/AddressLibraryTests.cs` mutates `NativeEntryPoint.InitializeManaged` / `Shutdown()` without `[Collection("NativeEntryPointSerial")]`, which can intermittently race when xUnit runs `AddressLibraryTests` in parallel with `Host020AndReliabilityTests` (annotated with `[Collection("NativeEntryPointSerial")]`).

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
- **`NativeMods.slnx`:** Integrated `LocalExile.Client.AssetPipeline`, `DualMod.MaxwellPet.*`, `DualMod.SiphonOrb.*`, and `AssetPipeline.Tests`, `MaxwellPet.Tests`, `SiphonOrb.Tests` (`21` projects total; `LocalExile.Client.Phase0E2E` kept out until M7).
- **Live Verification Status:** M7 will execute `LocalExile.Client.Phase0E2E` and the live isolated `Xvfb` `3.29.3d` Proton verification covering `SiphonOrb`, `MaxwellPet`, and all new `0.2.0` native client capabilities.
