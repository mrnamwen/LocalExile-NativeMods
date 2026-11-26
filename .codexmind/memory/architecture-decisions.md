# Architecture Decisions — NativeMods 3.29 (`0.2.0`)

> Living decision log for the NativeMods 3.29 script extender, Client SDK (`0.2.0`), Managed Host, Server Bridge (`0.2.0`), Asset Pipeline, PE32+ Patcher, and Waypoint integration.

## 1. Frozen-Target Scope (3.29.3c & 3.29.3d Only)

- LocalExile targets Path of Exile 3.29.3c and 3.29.3d exclusively.
- Symbols are stored in static per-build Address Library JSON files (`data/addresses/3.29.3d.json`: **`122` symbols**, **`37` offsets**, **`18` constants**; `data/addresses/3.29.3c.json`: **`122` symbols**, **`35` `null` unverified**, **`87` verified/shared**) validated against `data/addresses/address-library.schema.json`.
- `scripts/generate-address-table.py` (`--check` enforced by CMake) code-generates `src/native-shim/src/address_table.cpp` directly from JSON so zero hardcoded RVAs exist in `client_host_shim.cpp` / `version_proxy.cpp` and `SimulatedNativeBridge` aligns all `122` symbol names.
- Because the binary has `DYNAMIC_BASE` (ASLR), all addresses are stored as **RVAs** relative to image base `0x140000000` and resolved dynamically against the loaded module base with expected-byte checks before detour installation.

## 2. Three-Layer Client Architecture

1. **Universal PE32+ Patcher (`src/LocalExile.Client.Patcher/`)**:
   - Cross-platform .NET 10 library and CLI.
   - Moves the existing PE import directory into a new section (using the verified 136 bytes of PE header slack) and appends an import descriptor for `LocalExile.ClientHost.dll`, preserving existing imports, data directories, and Authenticode layout with byte-for-byte reversible unpatching (`NMUNPAT1`).
   - Supports `version.dll` proxy deployment as an explicit fallback (requires `WINEDLLOVERRIDES=version=n,b` under Wine/Proton).
2. **Native Script Extender Shim (`src/native-shim/` → `LocalExile.ClientHost.dll`, C ABI `0.2.0`)**:
   - C++20 Win64 DLL exposing C ABI `0.2.0` (`NM_ABI_VERSION = 0x00000200u`, `NmAbiTable = 552 B` / `68` fn ptrs, `NmHookContext = 128 B`, `NmNativeCallDesc = 104 B`, `77` `.def` exports).
   - `HookEngine`: full C++20 x64 instruction-length decoder + ±2 GB RIP-relative relocator + shared multi-mod `TargetChain` (`PRE`/`POST` phases, `SkipOriginal`, `>= 50 ms` quiescent reclamation) + `nm_call_native_guarded` (up to 8 mixed int/float `XMM0..XMM3` args inside `0xE06D7363` SEH/C++ wall).
   - Owns build recognition, Win64 game-allocator wrapper (`0x2c8cf60` + 16B header / `0x2c8ca30`), `MsvcWString` & `ScopedOverlayIndex` RAII reclamation, main-thread frame queue (with empty state-stack fallback), 15 UTF-16 text extensions (12 BOM + 3 no-BOM `.sm`/`.mat`/`.amd`), live UI (`ITextBox`, `ICheckBox`, geometry/lifecycle, `CreateWindowInto`), `IClipboardService`, `ReadStockGameFile`/`Invalidate`, `SendRaw`, `0x02FC+` opcode groups (`0x29a7710`), and chat suppression `int32_t`.
   - Boots a private self-contained `win-x64` .NET 10 `hostfxr`/CoreCLR runtime off the game thread, deferring `AutoLoadClientMods` onto the game-thread queue.
3. **Managed Host & Public SDK (`src/LocalExile.Client.Host/`, `src/LocalExile.Client.Sdk/`, `0.2.0`)**:
   - `LocalExile.Client.Sdk` (`VersionPrefix=0.2.0`, `SdkVersion.Current == "0.2.0"`) compiles client mods with zero dependency on Host internals (`Engine`, `Extend`, `Net`, `Raw`, `IAddressLibrary`).
   - `ClientModLoader` loads each mod in a shadow-copied collectible `AssemblyLoadContext`, enforces manifest `Enabled`, `Depends` (Kahn topological sort), and `PayloadHash` (SHA-256), tracks `ScopedGameServices` disposal, and isolates per-mod faults on frame/lifecycle/ruleset callbacks.
   - `INativeBridge.IsOnGameThread` uses `[ThreadStatic] s_inNativeGameFrame` across all native-to-managed game-thread callbacks (`Frame`, `Drain`, `Ui*`, `RulesetRx`, `ChatRx`, `OpcodeRx`), and `ClientHostRuntime._subscribedUiEvents` cleans up on window/subtree removal.

## 3. Dual-Side Mod Model & `ServerBridge 0.2.0`

- Showcases (`examples/DualMod.HarvestGarden/`, `examples/DualMod.MaxwellPet/`, `examples/DualMod.SiphonOrb/`) share `Shared/` records, `Server/` (`<id>.dll` + optional `*LocalExileEntry.cs` Roslyn entry), `Client/` (`<id>.Client.dll` against `LocalExile.Client.Sdk`), and `client/` overlays.
- **Primary Transport (`HostRuleset`, S→C `0x2c9` / C→S `0x2ca`)** + **Fallback Transport (`ChatFallback`, S→C `0x17` `##NM ` / C→S `0x15`)**.
- **`ServerBridge 0.2.0` (`src/LocalExile.Client.ServerBridge/`):**
  - `ClientBridgeHost`: async `On<T>` / `OnClientReady`, `HostChannelId`, `IsClientReady`, `HasMod`, `HandlerError` isolation, `ServerUiWindowHandle.Closed` / `OnClosed` + `OnButtonClickAsync` / `OnControlEventAsync`, per-session `IClientCommandBatch` (`BeginBatch`, `BatchAsync`, `FlushAsync`).
  - `LocalExileGatewayHost.Create` / `Attach`: eliminates Gateway sink/bus/packet/command boilerplate and integrates `LocalExile` Part 1 (`feat/nativemods-phase0-core-hooks`: `Client.For(player)`, `GameEvents.{ClientReady, ClientMessage, LevelUp, CosmeticChanged}`, `Item.CustomName`). Note `global::Client.For(player)` qualification when importing `LocalExile.Client.ServerBridge`.
  - `LocalExileGatewayTransactions` & `LocalExileItemAffixService`: reusable `LocateCurrencyStack`, `LocateGatewayItem` (`IsWorn` rows `1..11, 23, 54, 78, 102`), `GrantItemsAsync`, `StealAffixOnGatewayItem`, `ReplaceAffixOnGatewayItem`, `SendInventoryDiffsAsync`, `Item.CustomName`.

## 4. Waypoint & `AssetPipeline` Integration (`0.2.0`)

- **Servers never push executable code.** Waypoint validates `mod.json` (`15` `AppendableRegistryTables`, closed `ItemClasses`), `.ui` layouts, namespace rules, and SHA-256 digests.
- `ModBundlePacker` (`NMB1` chunked framing `[u32 chunkLen][0xCC 0x06][payload]`) matches `FileOverlayService.ReadBundleSlice`.
- Text encodings enforce **12 BOM+CRLF** vs **3 no-BOM+CRLF** (`.sm`, `.mat`, `.amd`) with idempotent `TrimStart('\uFEFF')`, and `PoeContentHash.MurmurHash2` matches `PoeHashing.Hash32` (`Encoding.UTF8`).

## 5. Local Build & Test Environment (Phase 0 `0.2.0` M1–M6 Baseline)

- **Solution (`NativeMods.slnx` + `Directory.Build.props`):** `VersionPrefix=0.2.0`, `.NET 10` (`net10.0`, `Nullable=enable`, `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-recommended`), **`21` projects** (6 `/src/`, 9 `/examples/`, 6 `/tests/`; `tests/LocalExile.Client.Phase0E2E/` kept out of `.slnx` until M7).
- **Verified Managed Suites (`dotnet test NativeMods.slnx -c Release`):** all 6 solution test projects (`Tests`, `Patcher.Tests`, `Waypoint.Tests`, `AssetPipeline.Tests`, `MaxwellPet.Tests`, `SiphonOrb.Tests`) pass with 0 warnings and 0 errors (note: `AddressLibraryTests` in `LocalExile.Client.Tests` lacks `[Collection("NativeEntryPointSerial")]` — documented M2 follow-up).
- **Verified Native C++20 Shim (`src/native-shim/`):** **`18/18`** C++20 shim tests passing (`ctest`, `-Wall -Wextra -Wpedantic -Werror`, ASan/UBSan) + Win64 cross-compile + Wine smoke (`r1_wine_smoke.exe`).
- **Live Proton (`Xvfb` `3.29.3d`):** Scheduled for Milestone M7.
