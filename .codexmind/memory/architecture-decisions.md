# Architecture Decisions — NativeMods 3.29 (`0.3.0`)

> Living decision log for the NativeMods 3.29 script extender, Client SDK (`0.3.0`), Managed Host, Server Bridge (`0.3.0`), Asset Pipeline, PE32+ Patcher, and Waypoint integration.

## 1. Frozen-Target Scope (3.29.3c & 3.29.3d Only)

- LocalExile targets Path of Exile 3.29.3c and 3.29.3d exclusively.
- Symbols are stored in static per-build Address Library JSON files (`data/addresses/3.29.3d.json`: **`240` symbols**, **`160` offsets**, **`18` constants** — `240/240` verified, `170/170` non-null `expectedBytes`; `data/addresses/3.29.3c.json`: **`240` symbols**, **`153` `null` unverified**, **`87` verified/shared**) validated against `data/addresses/address-library.schema.json`. All `kind: "vtable"` entries store `"expectedBytes": null` for `DYNAMIC_BASE` ASLR safety.
- `scripts/generate-address-table.py` (`--check` enforced by CMake) code-generates `src/native-shim/src/address_table.cpp` directly from JSON so zero hardcoded RVAs exist in `client_host_shim.cpp` / `version_proxy.cpp` and `SimulatedNativeBridge` aligns all `240` symbol names.
- Because the binary has `DYNAMIC_BASE` (ASLR), all addresses are stored as **RVAs** relative to image base `0x140000000` and resolved dynamically against the loaded module base with expected-byte checks before detour installation.

## 2. Three-Layer Client Architecture

1. **Universal PE32+ Patcher (`src/LocalExile.Client.Patcher/`)**:
   - Cross-platform .NET 10 library and CLI.
   - Moves the existing PE import directory into a new section (using the verified 136 bytes of PE header slack) and appends an import descriptor for `LocalExile.ClientHost.dll`, preserving existing imports, data directories, and Authenticode layout with byte-for-byte reversible unpatching (`NMUNPAT1`).
   - Supports `version.dll` proxy deployment as an explicit fallback (requires `WINEDLLOVERRIDES=version=n,b` under Wine/Proton).
2. **Native Script Extender Shim (`src/native-shim/` → `LocalExile.ClientHost.dll`, C ABI `0.3.0`)**:
   - C++20 Win64 DLL exposing C ABI `0.3.0` (`NM_ABI_VERSION = 0x00000300u`, `NmAbiTable = 720 B` / `89` fn ptrs `0..88`, `NmHookContext = 128 B`, `NmNativeCallDesc = 104 B`, `nm_screens_state_t = 280 B`, `nm_input_event_t = 40 B`, `nm_item_snapshot_t = 448 B`, `nm_hover_state_t = 952 B`, **`102`** `.def` exports).
   - `HookEngine`: full C++20 x64 instruction-length decoder + ±2 GB RIP-relative relocator + shared multi-mod `TargetChain` (`PRE`/`POST`, `SkipOriginal`, `>= 50 ms` quiescent reclamation, DWARF `.eh_frame` & Win64 `RUNTIME_FUNCTION`+`UNWIND_INFO` exception unwind + RAII `ActiveCallGuard`) + `nm_call_native_guarded` (up to 8 mixed int/float args inside `0xE06D7363` SEH/C++ wall).
   - Owns build recognition, Win64 game-allocator wrapper (`0x2c8cf60` + 16B header / `0x2c8ca30`), `MsvcWString` & `ScopedOverlayIndex` RAII reclamation, main-thread frame queue, 15 UTF-16 text extensions (12 BOM + 3 no-BOM `.sm`/`.mat`/`.amd`), live UI (`ITextBox`/`ICheckBox`, `Control::SetEnabled`, `Control::ReleaseOrDestroy` `0x18b6810`, geometry/lifecycle, `CreateWindowInto`, native stock-tree `Find` & child enumeration `+0xb8`), stock/pre-game screen & stash-tab hooks (`StateStack`, `InGameUI`, `MapStashPage+0x550`, `LeagueOverlay::Fill`, `LoginClient::WriteCreateCharacter_0x12`), suppressible keyboard/mouse hooks + `TextBox::AsTextInput` focus checks, hovered/cursor item inspection (`BaseStatic+0x68` inline `RowRef<BaseItemTypes>`), `IClipboardService`, `ReadStockGameFile`/`Invalidate`, `SendRaw` (`1..65536`), `0x02FC+` opcode groups (`0x29a7710`), and chat suppression.
   - Boots a private self-contained `win-x64` .NET 10 `hostfxr`/CoreCLR runtime off the game thread, deferring `AutoLoadClientMods` onto the game-thread queue (guarded under `NativeEntryPoint.Gate`).
3. **Managed Host & Public SDK (`src/LocalExile.Client.Host/`, `src/LocalExile.Client.Sdk/`, `0.3.0`)**:
   - `LocalExile.Client.Sdk` (`VersionPrefix=0.3.0`, `SdkVersion.Current == "0.3.0"`) compiles client mods with zero dependency on Host internals (`Engine` incl. `IScreensService`, `IInputService`/`IKeybindRegistry`, `IHoverService`, `IToastService`, `IPreGameService`; `Extend`, `Net`, `Raw`, `IAddressLibrary`).
   - `ClientModLoader` loads each mod in a shadow-copied collectible `AssemblyLoadContext`, enforces manifest `Enabled`, `Depends` (Kahn topological sort), and `PayloadHash` (assembly SHA-256 separate from `BundleSha256`), rejects duplicate `ContentRegistry` registrations, tracks `ScopedGameServices` disposal across all `0.3.0` services, and isolates per-mod faults.
   - `INativeBridge.IsOnGameThread` uses `[ThreadStatic] s_inNativeGameFrame` across all native-to-managed game-thread callbacks, and `ClientHostRuntime._subscribedUiEvents` + `ScreensService.SyncSlotAttachments` clean up native controls without leaking or double-freeing across panel switches and zone teardown.

## 3. Dual-Side Mod Model & `ServerBridge 0.3.0`

- Showcases (`examples/DualMod.HarvestGarden/`, `examples/DualMod.MaxwellPet/`, `examples/DualMod.SiphonOrb/`, `examples/DualMod.StashQol/`, `examples/DualMod.SkipCampaign/`) share `Shared/` records, `Server/` (`<id>.dll` + optional `*LocalExileEntry.cs` Roslyn entry), `Client/` (`<id>.Client.dll` against `LocalExile.Client.Sdk`), and `client/` overlays.
- **Primary Transport (`HostRuleset`, S→C `0x2c9` / C→S `0x2ca`)** + **Fallback Transport (`ChatFallback`, S→C `0x17` `##NM ` / C→S `0x15`)**, plus **Pre-Game Login TCP option injection** (`C→S 0x12` `CreateCharacter.S78` `|nm:k=v&...`).
- **`ServerBridge 0.3.0` (`src/LocalExile.Client.ServerBridge/`):**
  - `ClientBridgeHost`: async `On<T>` / `OnClientReady`, `HostChannelId`, `IsClientReady`, `HasMod`, `HandlerError` isolation, `ServerUiWindowHandle`, `IClientCommandBatch`.
  - **Tier 1 `0.3.0` (`ServerBridgeTier1.cs` ↔ `ServerBridgeTier1Handler.cs`):** `IClientPhase1Session` (`OnSlotButtonClickAsync`, `ShowToastAsync`, `RegisterHotkeyAsync`/`OnHotkeyAsync`, `OnScreenChangedAsync`, `OnHoverChangedAsync`) with ref-counted subscriptions and automatic teardown.
  - `LocalExileGatewayHost.Create` / `Attach`: eliminates Gateway boilerplate and integrates `LocalExile` Core Hooks Part 1 + Part 2a (`feat/nativemods-phase0-core-hooks` `b8423dc9c`: `Client.For(player)`, `GameEvents.{ClientReady, ClientMessage, LevelUp, CosmeticChanged, CharacterCreated, VendorQuote}`, `IPlayerWorld` progression/account facades, `StashRules.AffinitiesOf`/`AccountStash.FindAffinityTab`, `Item.CustomName`). Qualify `global::Client.For(player)` when importing `LocalExile.Client.ServerBridge`.
  - **Server Mod Persistence Rule:** Never write runtime files inside `Mods/Binaries/<id>/` (triggers `ModHost` auto-reload loop); persist via `AccountData`/`CharacterData` or `data/mods/`. `Mods/Client/` is outside `ModHost`'s watch root.

## 4. Waypoint & `AssetPipeline` Integration (`0.3.0`)

- **Servers never push executable code.** Waypoint validates `mod.json`, `.ui` layouts, namespace rules, and SHA-256 digests.
- **Registry Table Policy (`DualModPackageValidator`):**
  - **`18` `AppendableRegistryTables`:** `BaseItemTypes`, `ItemVisualIdentity`, `CurrencyItems`, `MtxTypes`, `MtxTypeGameSpecific`, `Pet`, `MonsterVarieties`, `NPCs`, `Chests`, `Projectiles`, `MiscObjects`, `MiscAnimated`, `HideoutDoodads`, `ExecuteGEAL`, `ClientStrings`, `Mods`, `ModType`, `BuffDefinitions`.
  - **`2` `ClosedRegistryTables` (`NC016` error):** `ItemClasses` (103-entry compiled enum) and **`Stats`** (`23,362` / `0x5b42` fixed rows — `0x5b43` is compiled `StatId::INVALID` + 4 OOB tables; custom stats use Phase 3 dead-slot allocation keeping stock `Id` + Route A `Metadata/StatDescriptions/*.txt` overlays, or unbounded high-ID `StatsComponent` `0x1b3` stats).
- `ModBundlePacker` (`NMB1` chunked framing `[u32 chunkLen][0xCC 0x06][payload]`) enforces **12 BOM+CRLF**, **3 no-BOM+CRLF** (`.sm`, `.mat`, `.amd`), plus the case-insensitive **`art/uiimages1.txt` no-BOM** exception, and `PoeContentHash.MurmurHash2` matches `PoeHashing.Hash32` (`Encoding.UTF8`).

## 5. Local Build & Test Environment (Phase 1 `0.3.0` Complete — Phase 2 Ready)

- **Solution (`NativeMods.slnx` + `Directory.Build.props`):** `VersionPrefix=0.3.0`, `.NET 10` (`net10.0`, `Nullable=enable`, `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-recommended`), **`33` projects** (6 `/src/`, 18 `/examples/`, 9 `/tests/`).
- **Verified Managed Suites (`dotnet test NativeMods.slnx -c Release`):** **`310/310`** passing across all 9 test projects (`Client.Tests` 106, `Waypoint.Tests` 69, `AssetPipeline.Tests` 41, `SiphonOrb.Tests` 22, `MaxwellPet.Tests` 19, `Phase0E2E.Tests` 18, `StashQol.Tests` 15, `SkipCampaign.Tests` 11, `Patcher.Tests` 9) with 0 warnings and 0 errors.
- **Verified Native C++20 Shim (`src/native-shim/`):** **`24/24`** C++20 shim tests passing on Linux (`ctest`, `-Wall -Wextra -Wpedantic -Werror`) and Win64/Wine (`nativemods_shim_tests.exe` + `r1_wine_smoke.exe`).
- **Verified `LocalExile` Core Hooks Part 1 + Part 2a (`feat/nativemods-phase0-core-hooks` HEAD `b8423dc9c`, unpushed):** `GameServer.Auth` 0w/0e, `GameServer.Core.Tests` **`550/0/1`**, `GameServer.Gateway.Tests` **`951/0/2`**.
- **Live Proton (`Xvfb` `3.29.3d`):** Phase 1 (`0.3.0`) live Proton verified (`abi=0x00000300`, `240` symbols / `160` offsets, CoreCLR `219.68ms`, all 11 Phase 1 hooks armed `OK`, `0` `[WARN]`, `0` SEH faults across `2400` frames, FMOD PCM `-24.5 dB / -9.9 dB`, stock SHA-256 baseline restored).
