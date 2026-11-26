# LocalExile.Client.Phase0E2E

Independent, opaque-box Phase 0 (`0.2.0` SDK & C ABI) verification mod and automated integration test suite for PoE `3.29.3d` + `LocalExile`.

## Decoupled Layout (M1–M6 vs M7)

During parallel implementation of **M1–M6**, `tests/LocalExile.Client.Phase0E2E/` is intentionally **excluded from `NativeMods.slnx`** so in-flight `0.2.0` contract work never blocks `dotnet build NativeMods.slnx` or `dotnet test NativeMods.slnx`:

- `mod.json` — Dual-side manifest (`id: "phase0-e2e"`, `sdkVersionRange: ">=0.2.0 <2.0.0"`, `rawAccess: true`, `depends: ["Game"]`).
- `Shared/` — `DualMod.Phase0E2E.Shared.csproj` (`net10.0`, zero external dependencies, `TreatWarningsAsErrors=true`) & `Phase0Messages.cs` (`RulesetId = 0xA4`, opcodes `1301..1310`, raw `0x02FC..0x02FE`). **Compiles immediately pre-M2.**
- `Client/` — `DualMod.Phase0E2E.Client.csproj` (`AssemblyName=phase0-e2e.Client`) & `Phase0ClientMod.cs` exercising all `0.2.0` client APIs (`E2E-01`..`E2E-16`) and emitting `[PHASE0-E2E] ASSERT <E2E-XX> PASS|FAIL <detail>`. **Targets M2 `0.2.0` SDK contracts (compiles once M2 lands).**
- `Server/` — `DualMod.Phase0E2E.Server.csproj` (`AssemblyName=phase0-e2e`), `Phase0ServerMod.cs` (`ServerBridge` `0.2.0`), and `Phase0LocalExileEntry.cs` (`Compile Remove` + `PreserveNewest` Roslyn entry using `LocalExile` Part 1 `Client.For`, `GameEvents.*`, `Item.CustomName`). **Targets M4+M5 `0.2.0` contracts (compiles once M5 lands).**
- `client/Metadata/UI/Mods/Phase0E2E/` — Valid `3.29.3d` layouts (`Phase0Probe.ui`, `Phase0ChildCard.ui`) + BOM-free `.sm`/`.mat`/`.amd` fixtures.
- `Tests/` — `LocalExile.Client.Phase0E2E.Tests.csproj` & `Phase0E2EIntegrationTests.cs` covering Tier 1–3 automated assertions (`UiLayoutLinter`, `NMB1` round-trip, BOM-free checks, hash SSOT, manifest/ALC/threading, `0.2.0` UI/net/files/raw/ServerBridge). **Compiles once M2+M5 land.**

## Tier 1–4 Coverage Summary

- **Tier 1 & 2 (Unit/Boundary)**: Covered in `Tests/Phase0E2EIntegrationTests.cs`, `NativeMods.slnx` (`Client.Tests`, `Patcher.Tests`, `Waypoint.Tests`), `AssetPipeline.Tests`, `MaxwellPet.Tests`, `SiphonOrb.Tests`, C++ `ctest`, and `GameServer.slnx` tests.
- **Tier 3 (Cross-Feature & Cross-Compile)**: `./scripts/build-win64.sh`, `wine` unit/smoke, asset pipeline overlays, and pairwise checks `P1`..`P14` in `Phase0E2EIntegrationTests.cs`.
- **Tier 4 (Live Isolated `Xvfb` `3.29.3d` Proton)**: `./scripts/run-phase0-proton.sh` co-stages `DualMod.MaxwellPet`, `DualMod.SiphonOrb`, and `DualMod.Phase0E2E` against `LocalExile` `scripts/dev/live.ps1 -NoBuild -NoExtract`, drives `/phase0 run`, `/phase0 raw`, `/phase0 item`, `/phase0 chat`, Cosmetics `K` + `/maxwell`, and `/siphon`, and asserts all 16 live markers `E2E-01`..`E2E-16`.

## Build & Run Instructions (M7)

```bash
# 1. Automated suite (post-M2/M5)
dotnet test tests/LocalExile.Client.Phase0E2E/Tests/LocalExile.Client.Phase0E2E.Tests.csproj -c Release

# 2. Build Client + Server probe binaries for Proton staging
dotnet build tests/LocalExile.Client.Phase0E2E/Client/DualMod.Phase0E2E.Client.csproj -c Release
dotnet build tests/LocalExile.Client.Phase0E2E/Server/DualMod.Phase0E2E.Server.csproj -c Release

# 3. Pre-flight check (safe pre-M6 as well)
./scripts/run-phase0-proton.sh --dry-run

# 4. Full live isolated Xvfb 3.29.3d Proton verification (M7)
./scripts/run-phase0-proton.sh --rebuild
```

## M2 / M5 / M6 Contract Reconciliation Points

1. **Root `"depends": ["Game"]`**: `LocalExile` server `ModHost` requires `"depends": ["Game"]` at root `mod.json` to link `Game.dll` during Roslyn compilation of `Phase0LocalExileEntry.cs` (same as `DualMod.MaxwellPet` and `DualMod.SiphonOrb`). When M2 enforces `Depends` topological sorting in `ClientModLoader`, M2 must either treat `"Game"` as an implicit server-only dependency or resolve `nativeClient.depends` separately.
2. **Exact M2/M5 Naming Choices**: `Phase0ClientMod.cs`, `Phase0ServerMod.cs`, `Phase0LocalExileEntry.cs`, and `Phase0E2EIntegrationTests.cs` bind to the `0.2.0` contracts in `PROJECT.md § Interface Contracts` (including `IUiControl` rect/size/pos/enabled/add/remove/destroy, `IUiService.CreateWindowInto`, `IGameServices.Clipboard`, `ReadStockGameFile`/`InvalidateCachedAsset`, `IRawMemory` vtable `slotIndex` + multi-arg/float `Call`, `LocalExileGatewayHost`, `Client.For`, `GameEvents.*`, `Item.CustomName`). Verify exact method/property names during M7 integration and reconcile if M2 or M5 picked an alias documented in `TEST_INFRA.md § 4.2`.
