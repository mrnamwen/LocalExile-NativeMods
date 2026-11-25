# Architecture Decisions — NativeMods 3.29 PoC

> Living decision log for the NativeMods 3.29 script extender, Client SDK, Managed Host, Server Bridge, PE32+ Patcher, and Waypoint integration.

## 1. Frozen-Target Scope (3.29.3c & 3.29.3d Only)

- LocalExile targets Path of Exile 3.29.3c and 3.29.3d exclusively.
- Because the client executable never updates again, reverse engineering is amortized once and symbols are stored in static per-build Address Library JSON files (`data/addresses/3.29.3d.json`, `data/addresses/3.29.3c.json`) validated against `data/addresses/address-library.schema.json`.
- Because the binary has `DYNAMIC_BASE` (ASLR), all addresses are stored as **RVAs** relative to image base `0x140000000` and resolved dynamically against the loaded module base with expected-byte checks before detour installation.
- Code RVAs match across 3.29.3c and 3.29.3d; data/global/vtable RVAs do not and are explicitly marked unverified (`rva: null`) on 3.29.3c until verified.

## 2. Three-Layer Client Architecture

1. **Universal PE32+ Patcher (`src/LocalExile.Client.Patcher/`)**:
   - Cross-platform .NET 10 library and CLI.
   - Moves the existing PE import directory into a new section (using the verified 136 bytes of PE header slack) and appends an import descriptor for `LocalExile.ClientHost.dll`, preserving existing imports, data directories, and Authenticode layout with byte-for-byte reversible unpatching.
   - Supports `version.dll` proxy deployment as an explicit fallback (requires `WINEDLLOVERRIDES=version=n,b` under Wine/Proton).
2. **Native Script Extender Shim (`src/native-shim/` → `LocalExile.ClientHost.dll`)**:
   - C++20 Win64 DLL exposing a stable C ABI (`include/nativemods_abi.h`).
   - Owns build recognition, expected-byte verification, inline detour & vtable hooking, SEH/C++ exception wall (`0xE06D7363`), Win64 game-allocator wrapper (`0x2c8cf60` + 16-byte header / `0x2c8ca30`), main-thread frame queue (with empty state-stack fallback), file overlay hook registration, and host-ruleset/channel callbacks.
   - Boots a private self-contained `win-x64` .NET 10 `hostfxr`/CoreCLR runtime off the game thread (deferred until first frame) so `DeadlockDetector` (`0x0e3ed10`) never trips and Proton/self-contained Windows installs require zero system runtime.
3. **Managed Host & Public SDK (`src/LocalExile.Client.Host/`, `src/LocalExile.Client.Sdk/`)**:
   - `LocalExile.Client.Sdk` is the single versioned assembly client mods compile against (zero dependency on Host internals), organized into `Engine`, `Extend`, `Net`, `Raw`, and `IAddressLibrary`.
   - `LocalExile.Client.Host` loads each client mod assembly in a collectible `AssemblyLoadContext` from a shadow-copied file for hot reload, verifies unload via `WeakReference`, enforces `RawAccess` manifest permissions, and isolates per-mod exceptions on frame/callback walls.
   - `INativeBridge` abstracts the native C ABI (`NativeShimBridge`) and provides a deterministic in-memory engine simulation (`SimulatedNativeBridge`) so full host/SDK/dual-mod tests execute cross-platform on Linux CI without a 3.29 client binary.

## 3. Dual-Side Mod Model & Mod Channel

- One mod solution ships `Server/` (`<id>.dll` for LocalExile Gateway), `Client/` (`<id>.Client.dll` against `LocalExile.Client.Sdk`), `Shared/` (typed message records/opcodes), and `client/` assets (`.ui`, `.ot`, `.it`, art, sounds).
- **Server remains authoritative** for game rules; client code owns presentation, native UI, local GEAL built-ins, custom controls, and per-frame client logic.
- **Primary Transport (`HostRuleset`, S→C `0x2c9` / C→S `0x2ca`)**: Registers a creator in an unused slot of the writable ruleset table (`0x475dc70`). Length-delimited up to `0xffff` bytes; unmodded clients log once and silently drop `0x2c9` updates instead of disconnecting.
- **Fallback Transport (`ChatFallback`, S→C `0x17` `##NM ` / C→S `0x15`)**: Live-verified in spike S4; requires zero Gateway protocol changes.
- **Server-Driven UI**: `LocalExile.Client.ServerBridge` enables server-only mods to drive native `.ui` windows via closed `ServerUiCommand` operations and gracefully degrade via `HasClientMod` handshake checks.

## 4. Waypoint Integration & Trust Model

- **Servers never push executable code.** Client mod assemblies are installed explicitly by the player via Waypoint from verified catalogs with SHA-256 digests.
- `mod.json` extends LocalExile-Mods conventions with a `nativeClient` block declaring SDK SemVer range, client assembly, payload directory, stock path `replaces`, `rawAccess` disclosure + justification, custom opcodes, GEAL built-ins, and content registries.
- Stock client archives (`Content.ggpk`, `Bundles2/_.index.bin`) are **never** modified on disk. Loose overlays live in `Mods/Client/<id>/` (auto-encoded to UTF-16LE BOM CRLF for `.ui`/`.ot`/`.it`) and packed releases use stored-block (`CC 06`, `0x40000` chunks) `.bundle.bin` archives mounted in memory.

## 5. Local Build & Test Environment (Verified Milestone 3 Baseline)

- **Solution (`NativeMods.slnx` + `Directory.Build.props`):** `.NET 10` (`net10.0`, `Nullable=enable`, `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-recommended`, XML docs enabled on `LocalExile.Client.Sdk` and `LocalExile.Client.ServerBridge`).
- **Verified .NET Test Suite (`dotnet test NativeMods.slnx -c Release`):** **73/73 passed** (0 warnings, 0 errors):
  - `tests/LocalExile.Client.Tests`: **32/32** (including real `data/addresses/3.29.3d.json` & `3.29.3c.json` integration, C ABI struct size parity `NmHostInfo=120`/`NmSymbolInfo=48`/`NmGameAllocHeader=16`/`NmExceptionInfo=528`/`NmAbiTable=200`, collectible ALC hot-reload with `CollectibleJsonCodec`, fault isolation, `RawAccess` gating, `HostRuleset` + `ChatFallback` loopback).
  - `tests/LocalExile.Client.Waypoint.Tests`: **32/32** (SemVer, manifest & `mods.json` v1 catalog, 47-control `.ui` linter, `NMB1` stored-block bundle packer, `InstallOptions.AllowRawAccess = false` default, `ClientPatcherBridge` PE32+ round-trip, and full `DualMod.HarvestGarden` E2E over both transports with `UnloadedCleanly == true`).
  - `tests/LocalExile.Client.Patcher.Tests`: **9/9** (PE32+ `.nmimp` import patching, `NMUNPAT1` byte-for-byte unpatch including EOF Authenticode, `--proxy-dll`, CLI E2E, Address Library integrity).
- **Verified Native C++20 Shim (`src/native-shim/`):** **8/8 C++20 shim tests passing** via `cmake` + `clang++` (`-Wall -Wextra -Wpedantic -Werror`) + `ctest`; Win64 `LocalExile.ClientHost.dll` & `version.dll` proxy targets guarded under `#ifdef _WIN32` with documented MSVC & MinGW-w64 commands in `CMakeLists.txt`.
