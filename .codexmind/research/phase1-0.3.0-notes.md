# Phase 1 (`0.3.0`) Implementation & Verification Notes

**Date:** 2026-11-26  
**Scope:** NativeMods SDK/ABI `0.3.0` — Stock Screens & Pre-Game UI (`IScreensService`, `IPreGameService`), Input & Keybinds (`IInputService`/`IKeybindRegistry`), Hovered/Cursor Items (`IHoverService`), HUD Toasts (`IToastService`), Tier 1 `ServerBridge 0.3.0`, `LocalExile` Core Hooks Part 2a, showcases `DualMod.StashQol` & `DualMod.SkipCampaign`, plus PR #1615 & `Stats.datc64` closed-table closeout.  
**Live Proton Status:** Verified on live isolated `Xvfb` PoE `3.29.3d` (`ea603c1`, `build/phase0-proton-logs/20261126-194103-proxy-3829347/nativemods-shim.log`): Win64 `abi=0x00000300` / `SDK 0.3.0` / `240` symbols / `160` offsets booted CoreCLR in `219.68ms`, **all 11 live Phase 1 hooks armed `OK`**, `0` `[WARN]`, `0` SEH faults across `2400` frames, FMOD PCM `-24.5 dB` mean / `-9.9 dB` max, and exact stock `Game.exe` / `Game.patched.exe` SHA-256 baseline restored (stale `0.2.0` string check in `Phase0ClientMod.cs:81` updated to `SdkVersion.IsCompatible(">=0.2.0 <2.0.0")` for `16/16` `E2E-01`..`E2E-16` pass).

---

## 1. Address Library & Native Shim C ABI `0.3.0` (`data/addresses/`, `src/native-shim/`)

- **Address Library Single Source of Truth (`data/addresses/{3.29.3d,3.29.3c}.json`):**
  - Expanded from `122` symbols / `37` offsets (`0.2.0`) to **`240` symbols**, **`160` offsets**, and **`18` constants** per build (identical symbol sets across `3.29.3d.json` and `3.29.3c.json`).
  - **`3.29.3d.json`:** `240/240` verified; all **`170/170`** non-null `expectedBytes` independently verified directly against `/home/nam/Git/LocalExile/Client/Game.exe` (SHA-256 `e9ba3d31c4d922e1826ef01d02fd22178a001b29e17264b68fc2446efd41f53d`, ImageBase `0x140000000`), and all `kind: "vtable"` entries store `"expectedBytes": null` for `DYNAMIC_BASE` ASLR safety.
  - **`3.29.3c.json`:** `87` verified/shared + `153` `null` (`unverified_3_29_3c`, queued for Phase 5 batch port).
  - `python3 scripts/generate-address-table.py --check` enforces exact JSON ↔ `src/native-shim/src/address_table.cpp` parity.
- **C ABI `0.3.0` (`src/native-shim/include/nativemods_abi.h`, `src/LocalExile.Client.Host/Interop/NativeShimAbi.cs`):**
  - `NM_ABI_VERSION` = `0x00000300u` (`0.3.0`), `nm_abi_table_t` / `NmAbiTable` = **`720` bytes** / **`89` function-pointer slots (`0..88`)** (strictly backward-appended after the 69 `0.2.0` slots `0..68`), **`102`** exports in `src/native-shim/src/LocalExile.ClientHost.def`.
  - Struct binary parity verified across C++ and C#: `nm_screens_state_t` (`280 B`), `nm_input_event_t` (`40 B`), `nm_item_snapshot_t` (`448 B`), `nm_hover_state_t` (`952 B`).
  - Live engine hooks wired for:
    - **Stock-tree UI traversal & pre-game screens:** native `Control` child enumeration (`+0xb8`), `StateStack` (`+0x08/+0x10` authoritative stack), `InGameUI::Init` panel pointers (`+0x628..+0x7c8`), `InGameState::Leave` teardown (`+0x978`), `InstanceClient::GetStashTabs` (`+0xc498/+0xc4b0/+0xc4c8`), `MapStashPage+0x550` `optional<variant<RowRef0,1,2>>` tier decoding (`MapKeyTier1..17`, `-1` for empty/special), `CreateCharacterState` + `LeagueOverlay::Fill` (`0x140cccff0`), and `LoginClient::WriteCreateCharacter_0x12` (`0x142146de0`) / `CreateCharacterState::OnCreateResult` (`0x140cf7fb0`).
    - **Input interception:** WndProc stub + keyboard/mouse hooks with `Handled` suppression (suppressing both button-down and matching button-up), modifier bitmasks, and `TextBox::AsTextInput` (vtable slot 6 `0x14015f2b0`) focus detection.
    - **Hover & Cursor inspection:** `PC_HUD::GetCursor` (`+0x488`) / `Console_HUD::GetCursor` (`+0x7e8`), `Cursor::GetHeldItem` (`+0x4e8`), `ModsComponent::EnsureDeserialised` (`+0x2e2` guard), and `BaseStatic+0x68` inline `RowRef<BaseItemTypes>` (`BaseItemTypesRow+0x00` `Id` & `+0x08` `RowRef<ItemClasses>` → `0x1423bf520`).
- **Verification:** **`24/24`** C++20 shim tests passing on Linux (`ctest`, `-Wall -Wextra -Wpedantic -Werror`) and Win64/Wine (`nativemods_shim_tests.exe` + `r1_wine_smoke.exe`).

---

## 2. Managed SDK & Host `0.3.0` (`src/LocalExile.Client.Sdk/`, `src/LocalExile.Client.Host/`)

- **Versioning:** `Directory.Build.props` bumped to `VersionPrefix = 0.3.0` (`SdkVersion.Current == "0.3.0"`, `>=0.2.0 <2.0.0` manifests remain forward-compatible).
- **New SDK `0.3.0` Surfaces (`IGameServices`):**
  - **`IScreensService` (`ScreenTypes.cs`, `ScreensService.cs`):** Tracks `PreGameScreen` (`Unknown`, `Login`, `SelectCharacter`, `CreateCharacter`, `LeagueOverlay`, `InGame`), `GamePanel` bitmask (`Hud`, `Inventory`, `Stash`, `MapStash`, `BlightStash`, `PassiveTree`, `Vendor`, `AtlasPassiveTree`, `NpcDialogue`, `PlayerTrade`), `ActiveStashTab` (`StashTabInfo` incl. `MapTier`, folder nesting, guild/remove-only flags), and **9 conflict-free managed auto-flow `ScreenSlot`s** (`InventoryToolbar`, `StashHeader`, `StashFooter`, `MapTabBar`, `BlightTabBar`, `PassiveTreeHeader`, `HudTopRight`, `LeagueCardModes`, `CreateCharacterBar`) plus raw `AttachToControl` escape hatch.
    - `ScreensService.SyncSlotAttachments` destroys prior native controls on same-zone panel close or parent switch (`destroyNative: true`) while `InvalidateInGameAttachments` / `DetachSlot(CreateCharacterBar)` avoid double-free (`destroyNative: false`) after engine teardown.
    - `RegisterLeagueCardMode` re-injects mod checkboxes into every `0xdc35` card's mode `StackLayout` on every `LeagueOverlay::Fill`, equalizing widths with HC/SSF/Ruthless, compacting height/spacing inside the `500×905` card budget, and passing `yAnchor: 0xFF`.
  - **`IInputService` & `IKeybindRegistry` (`InputTypes.cs`, `InputService.cs`):** Priority-ordered `OnKey` / `OnMouse` hooks with `Handled` suppression, `IsTextInputFocused` gating, `KeyChord` parser/formatter (supporting keyboard chords and mouse chords like `Ctrl+Shift+RMB`), and centralized `KeybindRegistry` persisted to `Mods/Client/keybinds.json` with live conflict detection (`KeybindConflict`).
  - **`IHoverService` (`HoverTypes.cs`, `HoverService.cs`):** Exposes `HoveredControl`, `HoveredItem` (`HoveredItemInfo`: entity/control handles, item/inventory IDs, grid coords/size, metadata path, base name, stack/max stack, rarity, identified/corrupted, ilvl, item class), `CursorItem` (`CursorItemInfo`: `Empty`/`HoldingItem`/`UsingItem`, source inventory/grid, `ShiftKeep`), immediate `RefreshHoveredItem()`/`RefreshCursorItem()`, and `HoverChanged`.
  - **`IToastService` (`ToastTypes.cs`, `ToastService.cs`):** NativeMods-owned HUD toast queue anchored to `ScreenSlot.HudTopRight` with severity levels (`Info`, `Success`, `Warning`, `Error`), FIFO queuing, configurable `MaxVisibleToasts` / `DefaultDuration`, frame-tick auto-dismiss, and per-mod `Clear()` scoping.
  - **`IPreGameService` (`PreGameTypes.cs`, `PreGameService.cs`):** Exposes `CharacterCreating(CharacterCreateContext)` (`C→S 0x12` `CreateCharacter.S78` structured `|nm:k=v&...` option injection, max `1024` chars / `32` options, leaving stock `BaseStartState` unchanged when empty) and `CharacterCreated(CharacterCreatedEventArgs)` (`S→C 0x13`).
  - **Native Stock-Tree UI Traversal (`UiService.cs`):** `GetRootControl().Find()` queries native stock UI trees (`FindNativeUiChild`) and exposes native child enumeration (`+0xb8`) and handle wrapping.
  - **Isolation & Teardown (`ScopedGameServices`):** All new `0.3.0` services are wrapped in per-mod fault isolation and collectible-ALC disposal tracking.
- **Verification:** `LocalExile.Client.Tests` **`106/106`** passing (including Phase 1 unit tests and 12 `ServerBridge030Tests`).

---

## 3. Tier 1 `ServerBridge 0.3.0` & `LocalExile` Core Hooks Part 2a

- **Tier 1 `ServerBridge 0.3.0` (`src/LocalExile.Client.ServerBridge/ServerBridgeTier1.cs`, `src/LocalExile.Client.Host/Services/ServerBridgeTier1Handler.cs`):**
  - `IClientPhase1Session` lets server-only / Tier 1 Roslyn mods register stock-slot buttons (`OnSlotButtonClickAsync`), display HUD toasts (`ShowToastAsync`), register hotkeys (`RegisterHotkeyAsync` / `OnHotkeyAsync`), and subscribe to screen/hover changes (`OnScreenChangedAsync` / `OnHoverChangedAsync`) with ref-counted multi-mod subscriptions and clean disconnect/dispose teardown.
  - `ReflectionGatewayAdapter` & `LocalExileGatewayHost` add Part 2a reflection wrappers for `GameEvents.CharacterCreated` (`CharacterCreatedContext`) and `GameEvents.QuoteVendor` (`VendorQuoteResult`).
- **`LocalExile` Core Hooks Part 2a (`/home/nam/Git/LocalExile`, branch `feat/nativemods-phase0-core-hooks`, HEAD `b8423dc9c` atop `c5f928486`, unpushed):**
  - `LoginServerSession.ParseStartState` parses `CreateCharacter.S78` `|nm:k=v&...` options and sends `character_created` over a persistent reverse `gateway_attach` NDJSON stream on localhost `InstanceControl` (with ack written and flushed before `_gateway` publication in `InstanceDirectory.cs`) before `S→C 0x13` / `InstanceDirectory.AssignStart`.
  - Fires `GameEvents.CharacterCreated(CharacterCreatedContext)` in Gateway (`StartingAreaId`, `StartingLevel`, and atomic `SuppressStartKit` merge onto existing save in `GatewayControl.cs`).
  - Adds public `IPlayerWorld` progression/account facades (`SetLevelAsync`, `SetQuestFlagsAsync`, `SetLeagueQuestFlagsAsync`, `GrantWaypointsAsync`, `SetRefundPointsAsync`, `AwardAscendancyAsync`, `AllocatePassivesAsync`, `GetLeagueCharacters`, `Gold` / `AddGoldAsync`), `StashRules.AffinitiesOf` / `AccountStash.FindAffinityTab`, `GameEvents.VendorQuote` (`QuoteVendor`), and stash orb-use helpers.
  - Verified independently in Release: `GameServer.Auth` **0w/0e**, `GameServer.Core.Tests` **`550 passed, 0 failed, 1 skipped`**, `GameServer.Gateway.Tests` **`951 passed, 0 failed, 2 skipped`** (incl. `4/4` `GameEventsTests`).

---

## 4. Phase 1 Showcases, Solution Integration & Waypoint Closeout

- **`examples/DualMod.StashQol/` (Wishlist #1–#8 Showcase):**
  - Implements all **8 in-game QoL features**: Deposit All (affinity-aware via `StashRules.AffinitiesOf`/`AccountStash.FindAffinityTab`), Vendor All (tab-scoped `QuoteVendor`), Inventory Sort, Map Tier Upgrade (3× T*n* → T*n+1* via `Maps.MapUpgrade_BaseItemTypesKey`), Blight Oil Upgrade Max, Regex Roller (`RegexRoller.ui` with atomic `CommitRegexRoll` via `items.Pay`/`stash.Pay`/`Change` + `0x00BF` diffs & stash-aware `SimulateCurrencyRoll`), `Ctrl+Shift+RMB` Bulk Stack Use (suppressed mouse chord + `IHoverService`), and PoB Passive Tree Import (`PobImport.ui` + `IClipboardService` + `IToastService`).
  - Verified: **`15/15`** `DualMod.StashQol.Tests` passing (incl. `DualModPackageValidator`, `UiLayoutLinter` 0 diagnostics on both `.ui` files, collectible ALC unload, and live Roslyn compile of `StashQolLocalExileEntry.cs` against `GameServer.Gateway.dll` + `game.dll`).
- **`examples/DualMod.SkipCampaign/` (Wishlist #16 Showcase):**
  - Registers a `Skip Campaign` toggle on `ScreenSlot.LeagueCardModes` (`LeagueOverlay`) and `ScreenSlot.CreateCharacterBar` (`CreateCharacterSkipBar.ui`), injects `skip_campaign=1` into `CreateCharacter.S78` via `IPreGameService.CharacterCreating`, and applies karui shores spawn (`2_11_endgame_town`), level, quest flags (incl. Kitava penalties), waypoints, refund points, and Lab ascendancy via `GameEvents.CharacterCreated` + `IPlayerWorld`.
  - Verified: **`11/11`** `DualMod.SkipCampaign.Tests` passing (incl. `DualModPackageValidator`, `UiLayoutLinter`, collectible ALC unload, and live Roslyn compile of `SkipCampaignLocalExileEntry.cs`).
- **Solution & Waypoint Closeout (`NativeMods.slnx`, `LocalExile.Client.Waypoint`):**
  - `NativeMods.slnx` integrates all 8 new Phase 1 projects (**`33` projects** total across `src/`, `examples/`, `tests/`); `dotnet build` & `dotnet test -c Release` pass **`310/310`** tests across 9 suites (`Client.Tests` 106, `Waypoint.Tests` 69, `AssetPipeline.Tests` 41, `SiphonOrb.Tests` 22, `MaxwellPet.Tests` 19, `Phase0E2E.Tests` 18, `StashQol.Tests` 15, `SkipCampaign.Tests` 11, `Patcher.Tests` 9) with 0 warnings and 0 errors.
  - Incorporated PR #1615 + `re-stats-enum` findings into Waypoint: `AppendableRegistryTables` expanded `15 → 18` (added `Mods`, `ModType`, `BuffDefinitions`), **`Stats` added to `ClosedRegistryTables`** (`NC016` error with dead-slot / `StatsComponent` guidance), and `art/uiimages1.txt` added as a case-insensitive no-BOM exception in `ModBundlePacker`.

---

## 5. Live Isolated `Xvfb` `3.29.3d` Proton Verification (`0.3.0`)

- **Run:** `ea603c1` (`build/phase0-proton-logs/20261126-194103-proxy-3829347/nativemods-shim.log`), Win64 `LocalExile.ClientHost.dll` (`abi=0x00000300`), `LocalExile.Client.Host.dll` (`SDK 0.3.0`), `3.29.3d.json` (`240` symbols / `160` offsets).
- **Boot & Mods:** CoreCLR booted in **`219.68ms`** (`NativeEntryPoint.Initialize` returned `0`); loaded all 4 client mods (`harvest-garden`, `maxwell`, `phase0-e2e`, `siphon`) in collectible ALCs.
- **Live Phase 1 Hooks (11/11 `OK`):** `[PHASE1] Armed live hooks (wndmsg=OK ui_init=OK ig_leave=OK league_fill=OK cc_enter=OK cc_leave=OK tab=OK map_grp=OK map_key=OK c2s_0x12=OK s2c_0x13=OK)`.
- **Stability, Audio & Clean Restore:** **ZERO `[WARN]` lines**, **ZERO SEH faults** (`msvc_cxx_passthrough=0` across `2400` frames), **ZERO crashes/hangs/Hyprland window leaks**, non-silent FMOD PCM capture (`mean_volume=-24.5 dB`, `max_volume=-9.9 dB`), and exact baseline SHA-256 restore verified on both `Game.exe` (`e9ba3d31...`) and `Game.patched.exe` (`ca8c1795...`).
- **`Phase0E2E` `E2E-01` Fix:** `E2E-02`..`E2E-16` passed `15/15` on the initial `0.3.0` run; `E2E-01` failed only on a stale `SdkVersion.Current.StartsWith("0.2.0")` check in `tests/LocalExile.Client.Phase0E2E/Client/Phase0ClientMod.cs:81`, now updated to `SdkVersion.IsCompatible(">=0.2.0 <2.0.0")` for full `16/16` pass on `0.3.0`.
