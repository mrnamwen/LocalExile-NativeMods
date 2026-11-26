# Phase 2 (`0.4.0`) Implementation & Verification Notes

**Date:** 2026-11-26  
**Scope:** NativeMods SDK/ABI `0.4.0` — Live World Entities (`IWorldService`), Live Entity/Player Stats (`IStatsService`), Area Enter/Leave Lifecycle (`IGameLifecycle`), Client Item Filter Bridge (`IItemFilterService`), Camera & Zoom Control (`ICameraService`), Tier 1 `ServerBridge 0.4.0` (`IClientPhase2Session`), and the `DualMod.LootTools` flagship showcase (Wishlist #10 Loot Merger + AoE Pickup & #11 Filter-Aware Loot Magnet) with **zero LocalExile server edits required**.  
**Status:** **COMPLETE & Live `3.29.3d` Proton Verified** across Managed (`349/349`), Native C++20 (`28/28` Linux Release + ASan/UBSan + Win64/Wine), Live Roslyn Gateway Compilation, and Live Isolated `Xvfb` `3.29.3d` Proton (`25/25` hooks `OK`, `16/16` `E2E-01`..`E2E-16` PASS). Ready for clean session start on **Phase 3 (Multi-Mod `.datc64` Merger, Dead-Slot Custom Stats, Drop-Pool Overlays & `1.0.0`)**.

---

## 1. Address Library & Native Shim C ABI `0.4.0` (`data/addresses/`, `src/native-shim/`)

- **Address Library Single Source of Truth (`data/addresses/{3.29.3d,3.29.3c}.json`):**
  - Expanded from `240` symbols / `160` offsets (`0.3.0`) to **`319` symbols**, **`300` offsets**, and **`18` constants** per build (exact symbol & offset parity across `3.29.3d.json` and `3.29.3c.json`).
  - **`3.29.3d.json`:** `319/319` verified (`238` non-null `expectedBytes`, all `kind: "vtable"` entries `expectedBytes: null` for `DYNAMIC_BASE` ASLR safety), independently verified against `/home/nam/Git/LocalExile/Client/Game.exe` (SHA-256 `e9ba3d31c4d922e1826ef01d02fd22178a001b29e17264b68fc2446efd41f53d`, ImageBase `0x140000000`) and `HookEngine::compute_stolen_length` + near/far relocation (`0` errors, `0` collisions, `0` RVA overlaps).
  - **`3.29.3c.json`:** `87` verified/shared + `232` `null` (`unverified_3_29_3c`, queued for Phase 5 batch RVA port).
  - **Critical Stale-Offset Fix:** Fixed stale `world.object_map = 0x8d8` → **`0xa18`** (`ClientWorld` vtable[49/50] `0x1416502c0` returns `rcx + 0xa18` `EntityManager`; vtable[51/52] `0x1416502d0` returns `rcx + 0x8e0` `AreaGen`).
  - `python3 scripts/generate-address-table.py --check` enforces exact JSON ↔ `src/native-shim/src/address_table.cpp` parity.
- **C ABI `0.4.0` (`src/native-shim/include/nativemods_abi.h`, `src/native-shim/src/phase2_services.{hpp,cpp}`, `src/LocalExile.Client.Host/Interop/NativeShimAbi.cs`):**
  - `NM_ABI_VERSION` = **`0x00000400u`** (`0.4.0`), `nm_abi_table_t` / `NmAbiTable` = **`864` bytes** / **`107` function-pointer slots (`0..106`)** (strictly backward-appended slots `89..106` after preserved `0.3.0` slots `0..88`), **`122`** exports in `src/native-shim/src/LocalExile.ClientHost.def` (`102` + `20` new exports).
  - Struct binary parity verified with C++ `static_assert` and C# `LayoutKind.Sequential` (zero hidden padding):
    - `nm_world_entity_query_t` (**160 B**)
    - `nm_world_entity_snapshot_t` (**760 B**, embeds 448 B `nm_item_snapshot_t`)
    - `nm_stat_entry_t` (**8 B**)
    - `nm_area_info_t` (**352 B**, up to `16` ruleset IDs)
    - `nm_item_filter_state_t` (**784 B**)
    - `nm_item_filter_result_t` (**72 B**, `0xRRGGBBAA` colors)
    - `nm_camera_state_t` (**120 B**, 16-float row-major `view_proj`)
  - **Live Engine Wiring & Hardening (`f992c0b`, `506561e`):**
    - `world_create_client_object` uses raw mimalloc `allocator.alloc(0x248, 16)` / `allocator.free` matching `ClientEntity` destructor (`0x141558a50` → `mi_free`, zero `+16` slab header shift).
    - `camera_world_to_screen` reads `ui.viewport_offset_x` / `+4` as `int32_t` with `static_cast<float>`, matching `Camera::WorldToScreen` (`v_row * M` via `Camera+0x100`).
    - `world.client_world_init_area` (`0x141653ee0`, 10 Win64 args `a0..a9`) is hooked via a typed 10-arg raw inline detour (`hooks.install_inline` → `detour_client_world_init_area`) rather than the 8-arg `install_inline_chain`, and `HookEngine::get_or_create_inline_chain_locked` verifies multi-chain `expectedBytes` tail bytes beyond `stolen_len` directly against live unpatched memory (`506561e`).
    - All 14 `components.get_*` accessors wired (`Positioned`, `Render`, `WorldItem`, `Targetable`, `Life`, `ObjectMagicProperties`, `Chest`, `Stats`, `Player`, `NPC`, `Portal`, `AreaTransition`, `MinimapIcon`, `Transitionable`) over `EntityManager` dual RB-trees (`+0x10` awake / `+0x20` asleep).
- **Native Verification:** **`28/28`** C++20 shim tests passing across:
  - Linux Release (`-Wall -Wextra -Wpedantic -Werror`, `ctest` 1/1 PASS),
  - Linux Debug ASan + UBSan (`detect_leaks=1`, `ctest` 1/1 PASS),
  - Win64 MinGW-w64 cross-compile (`-Werror`, zero non-system MinGW DLL deps, all 20 new exports verified) + Wine `nativemods_shim_tests.exe` (**`28/28` PASS**).

---

## 2. Managed SDK & Host `0.4.0` (`src/LocalExile.Client.Sdk/`, `src/LocalExile.Client.Host/`)

- **Versioning:** `Directory.Build.props` & `SdkVersion.Current` bumped to **`0.4.0`** (`>=0.2.0 <2.0.0` & `>=0.3.0 <2.0.0` manifests remain forward-compatible).
- **Phase 2 SDK & Host Services (`IGameServices`):**
  - **`IWorldService` (`WorldAudioStatsLifecycle.cs`, `WorldService.cs`):** Live `ClientWorld+0xa18` `EntityManager` queries (`GetLocalPlayer`, `GetEntities(WorldEntityQuery?)`, `GetEntity(uint)`, `GetWorldItems(filterVisibleOnly, maxDistance)`, `GetEntityPosition(uint)`), 14 components mapped into `WorldEntityKind` flags + `WorldEntitySnapshot`, and live `SpawnClientObject` / `RemoveClientObject` backed by `world.create_client_object` (`0x1ec47e0`).
  - **`IStatsService` (`WorldAudioStatsLifecycle.cs`, `StatsService.cs`):** Live `StatsComponent+0xf0` sorted `vector<pair<int32,int32>>` via `ReadStat` / `TryReadStat` / `SetLocalStat` / `GetAllStats` (`entityId == 0` targets local player, plus `ReadLocalPlayerStat` / `TryReadLocalPlayerStat` / `GetLocalPlayerStats`), with full support for unbounded high-ID custom stats (`>= 0x10000`).
  - **`IGameLifecycle` (`WorldAudioStatsLifecycle.cs`, `GameLifecycleService.cs`):** Live `AreaEntered(AreaEventArgs)` (carrying full `AreaInfo`: `AreaId`, `AreaName`, `Act`, `AreaLevel`, `EffectiveLevel`, `Hash16`, `IsTown`, `IsHideout`, `IsGuildHideout`, `ActiveRulesetIds`, `AreaSeed`), `AreaLeft(string)`, and `CurrentArea` (`AreaInfo?`) wired to `ClientWorld::InitArea` / `InGameState::Leave` / `AreaLoadingState`.
  - **`IItemFilterService` (`ItemFilterTypes.cs`, `ItemFilterService.cs`):** Live `Options::Instance` (`0x14475e9f0`), `WorldItem::IsFilterVisible` (`0x15e5ed0`), `WorldItem::EvalFilterStyle` (`0x15e5f10`), `ItemFilter::Match` (`0xe15970`), `Options::Tick` reload trigger (`+0x542/+0x543`), and `ItemFilter::NotifyLoaded` (`0x1935130`) hook exposing `GetState()`, `Reload()`, `IsWorldItemVisible(uint)`, `GetWorldItemResult(uint)`, `GetVisibleGroundItems(float?)`, `EvaluateItem(uint)`, and `FilterReloaded`. Resolves `.filter` files from `Documents\My Games\Path of Exile` with packed `0xRRGGBBAA` style colors.
  - **`ICameraService` (`CameraTypes.cs`, `CameraService.cs`):** Live `FollowCamera` (`+0x100` row-major DirectX `v_row * M` view-projection matrix) + `CameraController` (`+0x4a8` zoom, `+0x4ac` zoom_override, `+0x4b0` zoom_target) exposing `GetState()`, `TryWorldToScreen` (matching `Camera::WorldToScreen` bit-for-bit), and owner-scoped `SetZoomOverride(float?)` stack that automatically restores stock zoom (`-1.0f`) when a mod unloads from its collectible `AssemblyLoadContext`.
  - **Isolation & Teardown (`ScopedGameServices`, `NativeShimBridge`):** Safe `GCHandle` callback pinning, clean native callback teardown on shutdown, and per-mod fault isolation + disposal tracking across all `0.4.0` services.

---

## 3. Tier 1 `ServerBridge 0.4.0` (`src/LocalExile.Client.ServerBridge/`, `src/LocalExile.Client.Host/`)

- **`IClientPhase2Session` (`ServerBridgePhase2.cs` ↔ `ServerBridgeTier1Handler.cs`):**
  - Available on `IClientSession.Phase2` and via `ClientBridgeHost` helpers across both `HostRuleset` (`0x02C9`/`0x02CA`) and `ChatFallback` (`0x0017`/`0x0015`) transports:
    - **World/Loot:** `QueryVisibleGroundItemsAsync(float? maxDistance)`, `QueryWorldEntitiesAsync(ClientWorldEntityQuery?)`, `QueryLocalPlayerAsync()` returning portable JSON-safe `ClientWorldEntitySnapshot` (`ulong` handles).
    - **Stats:** `QueryEntityStatsAsync(uint entityId = 0)` & `QueryStatAsync(int statId, uint entityId = 0)` (local player alias `0`, supports high-ID `>= 0x10000`).
    - **Area:** `QueryAreaInfoAsync()` & ref-counted opt-in `OnAreaChangedAsync` (`ClientAreaChangeKind.Entered`/`Left`, full `AreaInfo`, ruleset IDs, clean disconnect/dispose detach).
    - **Item Filter:** `QueryItemFilterStateAsync()`, `ReloadItemFilterAsync()`, `EvaluateWorldItemFilterAsync(uint entityId, bool liveEvaluate = false)`, & ref-counted opt-in `OnFilterReloadedAsync`.
    - **Camera:** `QueryCameraStateAsync()` & `SetCameraZoomOverrideAsync(float? zoomOverride)` backed by `CameraService.SetZoomOverrideForOwner("server-bridge", ...)` with automatic disconnect/dispose restore that preserves client-mod overrides.
  - Advertises `tier1.world`, `tier1.stats`, `tier1.area`, `tier1.filter`, `tier1.camera` capabilities alongside `0.2.0`/`0.3.0`; uses a per-session `RequestId` nonce to prevent multi-mod `GatewayBridgeBus` collisions; preserves the 7-argument `ServerBridgeTier1Handler` constructor for backward compatibility.
- **Wave 3 Integration Cleanups (`861a419`):**
  - Fixed `ItemFilterTypes.cs` `TextRgba`/`BorderRgba`/`BackgroundRgba` doc comments to match `0xRRGGBBAA` packing.
  - Updated `Phase0E2EIntegrationTests.SdkAndAbi_AreBumpedTo020` to assert `0.4.0` / `0x00000400u` while preserving `>=0.2.0 <2.0.0` compatibility checks.
- **Verification:** `LocalExile.Client.Tests` **`131/131`** passing (including 15 new `ServerBridge040Tests` cases over both `HostRuleset` and `ChatFallback`).

---

## 4. `DualMod.LootTools` Showcase & Solution Integration (`examples/DualMod.LootTools/`, `tests/DualMod.LootTools.Tests/`, `NativeMods.slnx`)

- **Zero LocalExile Server Edits Required:** Verified against `/home/nam/Git/LocalExile` (`feat/nativemods-phase0-core-hooks` `b8423dc9c`) — every required ground-item (`InstanceGround`), inventory (`PlayerInventories`), world-sync (`Game.SendWorldRemoval`/`Game.SendGroundItem`/`Game.PickUpGold`/`Game.TakeGroundStack`), `GameEvents.AreaTick`, and ruleset-RPC API is already `public`.
- **Showcase Features (`examples/DualMod.LootTools/`, SDK `>=0.4.0 <2.0.0`):**
  - **Filter-Aware Loot Magnet (`Alt+M` / `ScreenSlot.HudTopRight`):** Queries `game.World.GetWorldItems(filterVisibleOnly: true, maxDistance: ...)`, sends only client-visible `uint[]` entity IDs; server validates active session, allocation/visibility (`origin.Shared || ReferenceEquals(origin.By, player)`), lockstep admission (`!LockstepClient || WorldOutput.Has(id)`), clamped radius (`<= 80`), batch cap (`<= 64`), and skips quest & tracked tutorial/start-weapon drops (`IsStartWeapon` + `TutorialObjects.DropKey`). Relocates cleanly via atomic `Ground.Take` → `Ground.Add(..., justDropped: false)` + `Game.SendWorldRemoval` (`0x02BD`) + `Game.SendGroundItem(..., justDropped: false)` (`0x02BB` + `0x01A9`) — **never `0x01B7` Teleport** — guaranteeing zero ghost labels and zero server desync.
  - **AoE Pickup (`Alt+G` / `ScreenSlot.HudTopRight`):** Picks up gold via `Game.PickUpGold`, stackables via `Game.TakeGroundStack` (emitting `Game.SendGroundItem` for any `Rest` so `wake: true` is preserved), and non-stackables into `MainInventory` + `ExpandedInventory` (`ExpandedInventoryMode == 1`) with `KeepOnGroundIfNotSaved` and coalesced `0x00BF`; emits `0x0117 InventoryFull` at most once per trigger and reports summary via `IToastService`.
  - **Atomic Ground Loot Merger (`Alt+R` + throttled `GameEvents.AreaTick` auto-merge toggle):** Strict `StackIdentityKey` + `ComputeNonPlainPayloadHash`, `MaxStack`/gold (`65535`) caps, atomic claim-all-first `Ground.Take` (zero dupes under concurrent pickup), and clean `SendWorldRemoval` + `SendGroundItem(justDropped: false)` label refresh.
  - **Item Filter Reload & Camera Zoom Override (`Alt+F`, `Alt+L` `LootToolsPanel.ui`):** `IItemFilterService.Reload` + `FilterReloaded` + `AreaEntered`/`AreaLeft` lifecycle resets, `!IsTextInputFocused` keybind guards, and `ICameraService.SetZoomOverride` cleanly restored on collectible ALC unload.
- **Showcase Verification:** **`14/14`** `DualMod.LootTools.Tests` passing, including `DualModPackageValidator` (0 errors/warnings/cross-mod conflicts across all 6 showcases), `UiLayoutLinter` (0 diagnostics on `LootToolsPanel.ui`), dual-transport E2E + collectible ALC unload camera restore, and live Roslyn compilation of `LootToolsLocalExileEntry.cs` against on-disk `/home/nam/Git/LocalExile` `GameServer.Gateway.dll` + `game.dll`.
- **Full Solution Closeout (`NativeMods.slnx`):**
  - **`37` projects** (6 `/src/`, 18 `/examples/`, 13 `/tests/` — **`10` test suites** + 3 `Phase0E2E` helper projects).
  - `python3 scripts/generate-address-table.py --check` PASS (`319` symbols / `300` offsets in both `3.29.3d` and `3.29.3c`).
  - `dotnet build NativeMods.slnx -c Release` (**0 warnings, 0 errors**) & `dotnet test NativeMods.slnx -c Release --no-build` (**`349/349`** PASS across all 10 suites: `Client.Tests` 131, `Waypoint.Tests` 69, `AssetPipeline.Tests` 41, `SiphonOrb.Tests` 22, `MaxwellPet.Tests` 19, `Phase0E2E.Tests` 18, `StashQol.Tests` 15, `LootTools.Tests` 14, `SkipCampaign.Tests` 11, `Patcher.Tests` 9).

---

## 5. Live Isolated `Xvfb` `3.29.3d` Proton Verification (`0.4.0`)

- **Run:** `dbe7a13` (`build/phase0-proton-logs/20261126-210436-proxy-4060031/nativemods-shim.log`), Win64 `LocalExile.ClientHost.dll` (`abi=0x00000400`), `LocalExile.Client.Host.dll` (`SDK 0.4.0`), `3.29.3d.json` (`319` symbols / `300` offsets), zero non-system MinGW DLL imports.
- **Boot & World Entry:** CoreCLR booted in **`252.60ms`** (`NativeEntryPoint.Initialize` returned `0`); loaded all 4 client mods (`harvest-garden`, `maxwell`, `phase0-e2e`, `siphon`) in collectible ALCs; clean world entry into `1_1_town` (`[world t=1s]`), survived full run + 10s soak (**`2,700+`** live frames).
- **All Live Hooks Armed `OK` (`25/25` total, `0` `SKIP`):**
  - `[OVERLAY]` 6/6 (`exists=OK size=OK read_range=OK read_whole=OK read_v5=OK hash_lookup=OK`), `[UI]` `Control::Load` `OK`, `[NET]` 3/3 (`chat_rx=OK ruleset_0x2c9=OK msg_pump=OK`), `PassA` frame hook `OK` (`handle=25`).
  - `[PHASE1]` **11/11 `OK`:** `wndmsg=OK ui_init=OK ig_leave=OK league_fill=OK cc_enter=OK cc_leave=OK tab=OK map_grp=OK map_key=OK c2s_0x12=OK s2c_0x13=OK`.
  - `[PHASE2]` **4/4 `OK`:** `init_area=OK area_load_leave=OK ig_leave=OK filter_loaded=OK`.
- **E2E, Audio, Stability & Clean Restore:**
  - **ALL `16/16` (`E2E-01`..`E2E-16`) PASS** (`E2E-01` reports `sdk=0.4.0`).
  - **ZERO `[WARN]` / ZERO `[ERROR]` / ZERO SEH faults** (`msvc_cxx_passthrough=0`), `0` Hyprland window leaks across all 5 checkpoints, `0` stray processes.
  - Non-silent FMOD PCM capture (`mean_volume=-24.4 dB`, `max_volume=-10.2 dB`), MaxwellPet + SiphonOrb live overlays & GEAL dance verified.
  - Exact baseline SHA-256 restore verified on `/home/nam/Git/LocalExile/Client/Game.exe` (`e9ba3d31...f53d`) and `Game.patched.exe` (`ca8c1795...dcb1`), zero stray DLLs/logs.
