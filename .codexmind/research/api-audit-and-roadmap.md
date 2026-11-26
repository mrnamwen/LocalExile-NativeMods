# NativeMods Modding API Audit & Community Roadmap (`0.2.0` → `1.0.0`)

**Date:** 2026-11-26  
**Status:** Phase 0 (`0.2.0`) implemented across Milestones M1–M6 (live `Xvfb` `3.29.3d` Proton verification scheduled in M7); Phases 1–5 planned.  
**Scope Audited:**
- `NativeMods`: `LocalExile.Client.Sdk`, `LocalExile.Client.Host`, `src/native-shim` (C++20 Win64), `LocalExile.Client.ServerBridge`, `LocalExile.Client.AssetPipeline`, `LocalExile.Client.Waypoint`, `data/addresses/{3.29.3d,3.29.3c}.json`, `examples/*`
- `LocalExile` Server (`/home/nam/Git/LocalExile`): `GameServer.Modding`, `GameServer.Gateway`, `Modules/*`, `world.db`, 3.29.3c/d protocol docs
- `LocalExile-Mods`: existing community server mods (`backpack`, `craftableidols`, `petloot`, `pob`, `zoom`, etc.)
- `LEC` (reference only for verified 3.29 client item-filter RE addresses)

---

## 1. Executive Summary

NativeMods already had a **live-proven vertical slice** on real PoE **3.29.3d** under Proton (`0.1.0-poc`), and **Phase 0 (`0.2.0`)** has now resolved the foundational ABI, threading, symbol, bundle, UI/clipboard/file/network wiring, `ServerBridge` ergonomics, and showcase migration gaps.

The pre-Phase-0 audit exposed a **sharp split between what the SDK declared and what was wired in `native-shim`**, plus **three structural bottlenecks** blocking the community wishlist:

1. **Over half the `0.1.0-poc` SDK surface was simulated/managed-only on `3.29.3d`:** `ITextBox`, `ICheckBox`, `IClipboardService`, `IUiControl` geometry/lifecycle, `INetChannel.SendRaw`/`OnRaw`, `IOpcodeRegistry` (`0x02FC+`), `IFileOverlayService.ReadStockGameFile`/`Invalidate` — **now wired in Phase 0 (`0.2.0`)** via C ABI `552 B` / `68` slots (`122` symbols in `3.29.3d.json`). Remaining engine surfaces (`IWorldService`, `IStatsService`, `IGealBuiltinRegistry`/`IScriptService` native VM wiring, `IRulesetRegistry` native creator table, `IGameLifecycle.AreaEntered`/`AreaLeft`, `IContentRegistry.DeclareDatRow`) are scheduled for **Phases 2 and 4**.
2. **Zero stock-UI, input, hover, or item-filter APIs exist yet:** Almost every gameplay screen in PoE 3.29 (Inventory, Stash, Map Stash, Blight Stash, Passive Tree, Vendor, Ground Loot Labels) is a **C++-constructed `UI::Control` tree**, not a `.ui` file. Without **Stock Panel Attachment + Screen State** (Phase 1), **Input/Hotkey Interception** (Phase 1), **Hovered-Item Inspection** (Phase 1), and a **Client Item-Filter Visibility Bridge** (Phase 2), mods cannot add native buttons to Stash/Inventory, cannot detect `CTRL+SHIFT+RMB`, and cannot build filter-aware Loot Magnets (because PoE item filters are **100% client-side**).
3. **No multi-mod `.datc64` merging or row-ID negotiation yet (Phase 3):** Content mods currently ship pre-patched `.datc64` binaries in `replaces` with fixed 3.29.3d row indices (`5461`, `8557`). Composable `.datpatch.json` delta-merging (`DatPatchMerger`) + handshake `content-row-map.json` synchronization will land in **Phase 3**.

> **High-Leverage Insight:** Just **4 foundational API pillars** — (1) Completing core UI (`TextBox`/`CheckBox`/dynamic children/`Clipboard`, **done in Phase 0**), (2) Stock Panel Attachment + Screen/Tab State (Phase 1), (3) Input/Hotkey + Hovered-Item Inspection (Phase 1), and (4) World Entities + Client Item-Filter Bridge (Phase 2) — unlock **~80% of the entire community wishlist**.

---

## 2. Implementation Audit (Pre-Phase-0 Findings & Phase 0 Resolutions)

### 2.1 Client Stack (`LocalExile.Client.Sdk` → `Host` → `native-shim`)

| Surface | Status (`0.2.0`) | What Works | Remaining / Future Phase |
|---|---|---|---|
| **Bootstrap & Hot Reload** | **HARDENED (`0.2.0`)** | Off-thread .NET 10 CoreCLR boot, `AutoLoadClientMods` deferred onto game thread, manifest `Enabled`/`Depends` (Kahn topo-sort)/`PayloadHash` enforced, shadow-copied collectible ALCs, `CollectibleJsonCodec`, `ScopedGameServices` fault isolation + ALC disposal tracking | `WatchDirectoryForHotReload` opt-in / launcher integration |
| **Frame Loop & Scheduler** | **HARDENED (`0.2.0`)** | `StateStack::PassA` (`0x1528f40`) hook, `IGameServices.Frame`, `RunOnGameThread`, `[ThreadStatic] s_inNativeGameFrame` scope across `Frame`, `Drain`, `Ui*`, `RulesetRx`, `ChatRx`, `OpcodeRx`, `EnqueueGameThread` `ErrQueueFull` cleanup | — |
| **File Overlays (`IFileOverlayService`)** | **WIRED (`0.2.0`)** | Route A hooks (`exists`, `size`, `read_range`, `read_whole`, `read_v5`, `files.hash_to_wstring_view` `0x0e40af0`), `ScopedOverlayIndex` RAII reclamation, `ReadStockGameFile`, `Invalidate`/`InvalidateCachedAsset` (`0x1010560`), `NMB1` chunked framing, 12 BOM+CRLF + 3 no-BOM+CRLF (`.sm`, `.mat`, `.amd`) extensions | Direct `fs+0xc900` index mount / `.merged-cache/` auto-mount (Phase 3) |
| **UI (`IUiService` / Controls / Clipboard)** | **CORE WIRED (`0.2.0`)** | `ui_create_window`, `ui_create_window_into`, `ui_get_root`, `ui_find_child`, `ui_set_visible`, `ui_set_text`, `ui_get_rect`, `ui_set_position`, `ui_set_size`, `ui_set_enabled`, `ui_remove_child`, `ui_destroy_control`, `ITextBox` (`+0x338`, `MaxLength`, `TextChanged`, `Submitted`), `ICheckBox` (`+0x4e2`/`+0x418`, `CheckedChanged`), `IUiWindow.{IsOpen, Closed}`, `_subscribedUiEvents` tree/subtree cleanup, `IClipboardService` (`CF_UNICODETEXT`), `MsvcWString` leak fixed | Stock panel attachment (`IScreensService`, Phase 1), `ScrollableFrame`/`TabContainer`/`ComboBox`/`ListBox`/`IUiItemGrid`/`ITooltipService` (Phase 4) |
| **Networking (`INetChannel` / `IChatService`)** | **WIRED (`0.2.0`)** | `HostRuleset` (`0x02C9`/`0x02CA`), `ChatFallback` (`0x0017`/`0x0015`) with `IChatService.RegisterInterceptor` `bool` suppression, `SendRaw`/`OnRaw` (`write_socket_bytes_locked`), `IOpcodeRegistry.{RegisterGroup, RegisterStreamHandler}` via `network.register_handler_group` (`0x29a7710`) for `0x02FC+` | — |
| **Audio (`IAudioService`)** | **HARDENED (`0.2.0`)** | Looping/one-shot `.ogg` via FMOD 2.03 (`PlaySoundFile`, `SetChannelVolume`, `IsChannelPlaying`, `StopChannel`), `PlayEvent` `MsvcWString` leak fixed & fire-and-forget channels reaped/bounded (`64`) | 3D spatial audio |
| **Lifecycle (`IGameLifecycle`)** | **PARTIAL** | `Frame`, `FrameCount`, `IsConnected`, `Connected`/`Disconnected` (connection token), `ScopedGameServices.Lifecycle` fault isolation & disposal tracking | `AreaEntered`, `AreaLeft`, `CurrentAreaId` native hooks (Phase 2) |
| **Rulesets (`IRulesetRegistry`)** | **PARTIAL (`0.2.0` hardened)** | S→C `0x02C9` dispatch + per-frame `OnFrame`, fault isolation & ALC disposal tracking | Native `rulesets.creator_table` (`0x475dc70`) + area enter/leave hooks (Phase 4) |
| **World (`IWorldService`)** | **STUB** | In-memory dict only (`SimulatedNativeBridge`) | Native `world+0x8d8` traversal, entity/component reads, `world.create_client_object` (`0x1ec47e0`), `PlayEffect` (Phase 2) |
| **Stats (`IStatsService`)** | **STUB** | In-memory dict only | Wire `stats.read` (`0x24d6510`) & `stats.set_local` (`0x24d65c0`) (Phase 2) |
| **Scripts / GEAL (`IScriptService`, `IGealBuiltinRegistry`)** | **PARTIAL** | `ValidateScript`, managed `IGealBuiltinRegistry`, `IScriptService.RegisterChatInterceptor` (`0.2.0`) | Native `geal.builtin_factory_table` (`0x45c50f8`) & `InvokeFunction` (Phase 4) |
| **Content (`IContentRegistry`)** | **PARTIAL** | `DeclareOverlay` / `DeclareWorldObject` virtual text files + FNV-1a ID | Runtime `.datpatch.json` delta merger & `DeclareDatRow` wiring (Phase 3) |
| **Raw (`IRawMemory`)** | **UPGRADED (`0.2.0`)** | Code-generated `address_table.cpp` (`122` symbols, `37` offsets, `18` constants), `HookEngine` full x64 decoder + RIP-relative relocation + shared multi-mod `TargetChain` (`PRE`/`POST`, `SkipOriginal`, `>=50 ms` reclaim), `InstallVtableHook`/`vtableSlotIndex`, `CallEx` (8 mixed int/float args inside `nm_call_native_guarded`) | — |
| **Screens / Input / Hover / ItemFilter / Camera** | **NOT BUILT** | — | Phases 1–2 |

### 2.2 `ServerBridge`, `AssetPipeline` & `Waypoint`

- **`LocalExile.Client.ServerBridge` (`0.2.0`):**
  - Added async `On<T>` & `OnClientReady` (`Func<IClientSession, ..., Task>`), channel-less overloads using `DefaultChannel`, `TrySendAsync<T>`, `HostChannelId` (`"__host"`), `IsClientReady` / `HasMod`, `HandlerError` isolation.
  - Added `ServerUiWindowHandle.{IsOpen, Closed, OnClosed}` and `OnButtonClickAsync` / `OnControlEventAsync` + per-session `IClientCommandBatch` (`BeginBatch` / `BatchAsync` / `FlushAsync`).
  - Added `LocalExileGatewayHost.Create` / `Attach` (eliminating ~130 lines of per-mod Gateway sink/bus/packet/command glue, supporting `GameEvents.CosmeticChanged` and `global::Client.OnRuleset` re-framing) and `LocalExileGatewayTransactions` + `Item.CustomName` helpers.
  - Remaining for Phase 3: cross-mod row-index allocator / generic catalog overlay (`content-row-map.json`).
- **`LocalExile.Client.AssetPipeline` (`0.2.0`):**
  - `GlbToPoeMeshConverter` / `PoeAstWriter.BuildAmd` aligned to BOM-less UTF-16LE CRLF for `.sm`/`.mat`/`.amd`.
  - Remaining for Phase 3: schema-driven `.datc64` delta merger (`DatPatchMerger` using `poe-dat-schema`), `.ui`/`.ot`/`.it` patcher, generic static `.fmt` & multi-bone rigged `.ast`.
- **`LocalExile.Client.Waypoint` (`0.2.0`):**
  - Fixed `ModBundlePacker` `NMB1` framing parity with `FileOverlayService.ReadBundleSlice`, fixed `.sm`/`.mat`/`.amd` BOM-less encoding + idempotent `TrimStart('\uFEFF')`, fixed `DualModPackageValidator.AppendableRegistryTables` (all **15** tables), unified `PoeContentHash.MurmurHash2` to `Encoding.UTF8` matching `PoeHashing.Hash32`.
  - Remaining for Phase 3: declarative `datPatches` merging (`Mods/Client/.merged-cache/`) instead of whole-file `.datc64` `replaces`.

---

## 3. Community Mod Wishlist — Feasibility & API Mapping

| # | Community Mod Idea | Server (`LocalExile`) Today | 3.29 Client Blocker? | Required NativeMods APIs | Phase / Difficulty |
|---|---|---|---|---|---|
| 1 | **Deposit All button on Inventory** | **Ready** (`AccountStash` + `StashRules` + `0x00BF`) | No native button (C++ UI) | `IScreensService` + `ScreenSlot.InventoryToolbar` + `INetChannel` | **P1 (Low)** |
| 2 | **Vendor All button on Stashes** | **Ready** (`VendorRecipes` + `VendorsFeature`) | No native button (C++ UI) | `IScreensService` (active tab ID) + `ScreenSlot.StashHeader` | **P1 (Low)** |
| 3 | **Inventory Sort button** | **Ready** (`PlayerInventories` 12×5 + `0x00BF`) | No native button or sort opcode | `IScreensService` + `ScreenSlot.InventoryToolbar` + `IInputService` | **P1 (Low)** |
| 4 | **Upgrade Map Tiers (3× T*n* → T*n+1*)** | **Ready** (`MapUpgrade_BaseItemTypesKey`) | No native button in Map Tab | `IScreensService` (`MapStash` + tier sub-tab) + `ScreenSlot.MapTabBar` | **P1 (Low–Med)** |
| 5 | **Blight Oil Upgrade Max button** | **Ready** (`BlightStashTabLayout` + recipes) | No native button in Blight Tab | `IScreensService` (`BlightStash`) + `ScreenSlot.BlightTabBar` | **P1 (Low)** |
| 6 | **Roll Until Regex Match** | **Doable** (`ItemRolls`, `ItemMoves.UseAcross`) | Live `ITextBox`/`ICheckBox` (**ready in `0.2.0`**); needs screen/hover | **P0 (`ITextBox`/`ICheckBox` done)** + P1 (`IScreensService` + `IHoverService`) | **P0–P1 (Low–Med)** |
| 7 | **CTRL + SHIFT + RMB uses full stack** | **Ready** once triggered | `0x58`/`0x71`/`0x72` carry no bulk flag | **`IInputService`** (suppress `Ctrl+Shift+RMB`) + **`IHoverService`** | **P1 (Medium)** |
| 8 | **Import PoB → Auto-apply passives + toast** | **Mostly ready** (`GameEvents.LevelUp` **added in M4**) | Chat too short (`ITextBox`+`IClipboardService` **ready in `0.2.0`**); needs HUD toast | **P0 (`ITextBox`+`IClipboardService`+`LevelUp` done)** + P1 (`IToastService`) | **P0–P1 (Low)** |
| 9 | **6 Sockets & 6 Links on 4-socket slots** | **ALREADY BUILT IN** (`world.db` `system_settings`) | Works on stock 3.29 client | Pure server config today; optional P5 compact-icon socket layout polish | **Done (Server)** |
| 10 | **Loot Merger + AoE Pickup** | **Ready** for whitelist/radius (`petloot` pattern) | No custom hotkey; item filter is client-side | **`IInputService`** + **`IWorldService`** + **`IItemFilterService`** | **P2 (Medium)** |
| 11 | **Loot Magnet (filter-visible loot)** | Teleporting `WorldItem`s trivial; server lacks filter state | 3.29 evaluates `.filter` locally (`0x140e13a00`/`0x140e15970`/`0x1419369e0`) | **`IWorldService`** + **`IItemFilterService`** + **`IInputService`** | **P2 (Medium)** |
| 12 | **Unique Item Generator** | `unique_items` + `ItemCrafter` + `Item.CustomName` (**added in M4**) | Cannot invent new bases/art/flavour/mods without `.datc64`+`.dds` | **Declarative Multi-Mod `.datc64` Merger** + row-map sync + studio UI | **P3 (Med–High)** |
| 13 | **Idols + Idol Crafting + Affix Tags + Restored League UIs** | `Modules/Legacy/Idols/`, `craftableidols`, `MapOpening` | Unmodifiable flag, Alt-tooltip tags (`Mods.datc64`), 32-cell board & league UIs | **In-place `.datc64` delta patching** + **`IUiItemGrid` + `ITooltipService`** | **P3–P4 (Med–High)** |
| 14 | **Unique-style Gear-Affinity Stash Tabs** | Server can store/route by `ItemClass` | `UniqueStash` hardcodes `UniqueStashLayout`; `StashTabAffinityId` fixed `0..21` (`0x141589b20`) | **Path A (P4):** `IUiItemGrid` + `TabContainer` + affinity hook `0x141589b20` | **P4–P5 (High)** |
| 15 | **1,000,000 Stack Size on Everything** | Server up to `65,535` (`ushort`) today | `Stack::Deserialise` (`0x1423fb5f0`) reads `u16` | **≤65,535 (P3):** `.datpatch.json`; **1M (P5):** hook `0x1423fb5f0` + `u32` sync | **P3 (Low) / P5 (High)** |

---

## 4. Locked-In Architectural Decisions & Phased Execution Plan

### Locked-In Decisions
- **Execution Order:** **Phase 0 → Phase 1 → Phase 2 → Phase 3 → Phase 4 → Phase 5**.
- **Target Client Build:** Build & live-verify on **`3.29.3d`** first; batch-port data/vtable RVAs to **`3.29.3c`** in Phase 5.
- **Plugin & Hook Model:** Managed C# SDK + multi-mod x64 hook chaining (`HookEngine` `TargetChain`).
- **Stock UI Conflict Resolution:** Managed auto-flow toolbar slots (`InventoryToolbar`, `StashHeader`, `StashFooter`, `MapTabBar`, `BlightTabBar`, `PassiveTreeHeader`, `HudTopRight`) + raw `AttachToControl` escape hatch.
- **Input & Keybind Resolution:** Centralized Keybind Registry (`Mods/Client/keybinds.json`) + conflict warnings + low-level suppressible key/mouse hooks.
- **Authoring DX:** Tier 1 (single `.cs` Roslyn server script via `ServerBridge`) and Tier 2 (`<id>.Client.dll` against `LocalExile.Client.Sdk` + local NuGet + `nativemods new`).
- **Multi-Mod `.datc64` Merging:** Shared C# `DatPatchMerger` → `Mods/Client/.merged-cache/`, auto-mounted at `t=0`, with `content-row-map.json` handshake digest check.
- **`LocalExile` PR Strategy:** Grouped local feature branch `feat/nativemods-phase0-core-hooks` (Part 1 landed locally in M4; Part 2 public catalog overlays land in Phase 3, then one combined PR).
- **Versioning:** `0.2.0` in Phase 0; freeze `1.0.0` after Phase 3.

### Phase Progress Tracker

- **Phase 0 (`0.2.0` Foundation Hardening, Core UI & ServerBridge Ergonomics):**
  - [x] **M1 (`e849e26`):** `HookEngine` x64 decoder/relocator/chaining, `address_table.cpp` JSON codegen (`122` symbols, `37` offsets, `18` constants), `NM_ABI_VERSION=0x00000200u` (`552 B`, `68` slots), leak/BOM/chat fixes, live UI/Clipboard/Files/Opcode C ABI wiring (`18/18` shim tests + Wine smoke).
  - [x] **M2 (`3f0ddf3`):** Managed Host & SDK `0.2.0`, `IsOnGameThread` (`s_inNativeGameFrame`), `AutoLoadClientMods` game-thread deferral, manifest `Enabled`/`Depends`/`PayloadHash`, `EnqueueGameThread` overflow fix, `_subscribedUiEvents` cleanup, `ScopedGameServices` isolation + ALC disposal tracking, all `0.2.0` SDK surfaces.
  - [x] **M3 (`d5f136e`):** `NMB1` chunked framing parity, `.sm`/`.mat`/`.amd` BOM-less encoding + idempotent BOM strip, `AppendableRegistryTables` (`15` tables), unified UTF-8 `MurmurHash2`.
  - [x] **M4 (`/home/nam/Git/LocalExile` `feat/nativemods-phase0-core-hooks`):** `Item.CustomName`, `GameEvents.{ClientReady, ClientMessage, LevelUp, CosmeticChanged}`, `Client.For(player)` (`0x02C9`/`0x02CA` + `/nm`) — 3 local commits (`5e72a1b7f`, `759a8ae29`, `9d5300859`).
  - [x] **M5 (`b6a4b2c`):** `ServerBridge 0.2.0` async `On<T>`/`OnClientReady`, `ServerUiWindowHandle.Closed`, `IClientCommandBatch`, `LocalExileGatewayHost`, `LocalExileGatewayTransactions`, `Item.CustomName` helpers.
  - [x] **M6:** `DualMod.SiphonOrb`, `DualMod.MaxwellPet`, `DualMod.HarvestGarden` refactored onto `0.2.0`, `NativeMods.slnx` updated (`21` projects; `Phase0E2E` excluded until M7), `.codexmind/` & `docs/modding-reference.md` updated.
  - [ ] **M7:** Final `Phase0E2E` + live isolated `Xvfb` `3.29.3d` Proton verification + remote push.
- **Phase 1:** `IScreensService` + managed `ScreenSlot` auto-flow toolbars + `IToastService`, `IInputService` + centralized keybinds (`Ctrl+Shift+RMB`), `IHoverService`, Tier 1 `ServerBridge` parity, showcase `DualMod.StashQol`.
- **Phase 2:** Live `IWorldService` (`world+0x8d8`), `IItemFilterService` (grounded on `0x140e13a00`/`0x140e15970`/`0x1419369e0`), `IStatsService`, `IGameLifecycle.AreaEntered`/`AreaLeft`, `ICameraService`, showcase `DualMod.LootTools`.
- **Phase 3:** Schema-driven `.datc64` `DatPatchMerger` (`poe-dat-schema`), `.datpatch.json` (`append` + `modify`), `content-row-map.json` handshake sync, `LocalExile` Core PR Part 2 (public `Item`/`MTX`/`ModPool` overlays), SDK `1.0.0` NuGet + `nativemods new`, multi-mod content pack showcase.
- **Phase 4:** Dynamic UI tree + `ScrollableFrame`/`TabContainer`/`ComboBox`/`ListBox`, `IUiItemGrid` + `ITooltipService`, custom stash affinity router (`0x141589b20`), live `Extend` registries (`IGealBuiltinRegistry`, `IRulesetRegistry`, `IUiControlRegistry`).
- **Phase 5:** `3.29.3c` batch RVA port, `1,000,000` (`u32`) stacks (`Stack::Deserialise` `0x1423fb5f0`), 6-socket compact icon polish, deep engine `Extend` + `.fmt`/multi-bone `.ast`/`.smd`.
