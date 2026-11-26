# Waypoint Integration & Dual-Side Mod Packaging

`src/LocalExile.Client.Waypoint/` provides the manifest schema, validator, `.ui` linter, stored-block bundle packer, installer, and extender lifecycle manager designed to integrate directly into **Waypoint** and align with **LocalExile-Mods**.

---

## 1. Alignment with LocalExile-Mods & Waypoint

Existing `LocalExile-Mods` / Waypoint conventions are preserved 100%:

- **`mod.json` base fields:** `id` (`^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$`, canonical lowercase `^[a-z0-9][a-z0-9_.-]{0,127}$` for catalog entries), `name`, `description`, `author`, `version` (`X.Y.Z`), `compatibleClientVersions` (e.g., `["0.5.2-beta"]`), `kind` (`"feature" | "league" | "script"`), `depends` (mod or module IDs such as `"Game"`), optional `assembly` (default `<id>.dll`), optional `enabled` (default `true`).
- **`mods.json` (v1) catalog index:** Modeled by `WaypointCatalogIndex` and `WaypointCatalogModEntry` (`FromManifest`), optionally including a `WaypointCatalogClientSummary` (`required`, `sdkVersionRange`, `rawAccess`, `replacesCount`, `hasClientAssembly`).
- **Safety limits (`DualModManifest`):** `MaxManifestBytes = 128 KB`, `MaxPackageBytes = 64 MB`, `MaxPackageFiles = 2000`, no symlinks/reparse points, zip-slip rejection.
- **No stock archive mutation:** Waypoint **never** writes to `Client/Game.exe`, `Client/Content.ggpk`, or `Client/Bundles2/`.
- **Backward compatibility:** A `mod.json` without a `nativeClient` block behaves identically to a standard server-only LocalExile mod.

---

## 2. Dual-Side `mod.json` Manifest (`nativeClient` Block)

Actual example from `examples/DualMod.HarvestGarden/mod.json`:

```json
{
  "id": "harvest-garden",
  "name": "Harvest Garden",
  "description": "Functional 3.11-inspired Sacred Grove seed stockpile, custom GrowthGauge UI, GEAL plot ripening, and server-authoritative lifeforce.",
  "author": "LocalExile Community",
  "version": "1.0.0",
  "compatibleClientVersions": ["0.5.2-beta"],
  "kind": "feature",
  "depends": ["Game"],
  "assembly": "harvest-garden.dll",
  "enabled": true,
  "nativeClient": {
    "required": false,
    "sdkVersionRange": ">=0.1.0-poc <2.0.0",
    "supportedBuilds": ["3.29.3d", "3.29.3c"],
    "assembly": "harvest-garden.Client.dll",
    "payloadDir": "client",
    "overlays": [
      "Metadata/UI/Mods/Garden/Stockpile.ui",
      "Metadata/MiscellaneousObjects/Mods/Garden/Plot.ot"
    ],
    "replaces": [],
    "rawAccess": false,
    "customOpcodes": [
      { "id": 1001, "name": "GardenPlantSeed", "direction": "ClientToServer" },
      { "id": 1002, "name": "GardenHarvestPlot", "direction": "ClientToServer" },
      { "id": 1003, "name": "GardenSyncState", "direction": "ServerToClient" },
      { "id": 1004, "name": "GardenOpenWindow", "direction": "ServerToClient" }
    ],
    "gealBuiltins": ["GardenRipen"],
    "customControls": ["GrowthGauge"],
    "contentRegistries": [
      { "table": "BaseItemTypes", "entryId": "Metadata/Items/Mods/Garden/WildSeed", "mode": "append" },
      { "table": "MiscObjects", "entryId": "Metadata/MiscellaneousObjects/Mods/Garden/Plot", "mode": "append" }
    ],
    "serverHalf": {
      "id": "harvest-garden",
      "minVersion": "1.0.0",
      "supportsGracefulFallback": true,
      "channelName": "harvest-garden"
    }
  }
}
```

### `NativeClientManifest` Field Reference

| Field | C# Type | Default / Notes |
|---|---|---|
| `required` | `bool` | `false` — allows graceful fallback (e.g., chat commands) when client mod is absent. |
| `sdkVersionRange` | `string` | `">=0.1.0-poc <2.0.0"` — parsed by `SemVerRange`. |
| `supportedBuilds` | `IReadOnlyList<string>` | `["3.29.3d", "3.29.3c"]` — restricted to 3.29.3c / 3.29.3d. |
| `assembly` / `assemblySha256` | `string?` | Single `.dll` filename (no path separators) and optional 64-hex SHA-256. |
| `payloadDir` | `string` | `"client"` — relative folder containing loose overlays. |
| `bundleFile` / `bundleSha256` | `string?` | Optional prebuilt `.bundle.bin` filename and SHA-256. |
| `overlays` | `IReadOnlyList<string>` | Declared namespaced paths (`Mods/<id>/` or hyphen suffix, e.g., `Mods/Garden/`). |
| `datPatches` | `IReadOnlyList<string>` | Relative paths under `payloadDir` to declarative `.datpatch.json` files (`DAT001`–`DAT009`, `NC022`–`NC023`, `PAY007`, cross-mod `CNF007`–`CNF009`). Merged atomically into `Mods/Client/.merged-cache/` + `content-row-map.json` on install/uninstall, with `DeploymentLedger.MergedCacheDigest` persisted. |
| `replaces` | `IReadOnlyList<string>` | Stock game paths explicitly overridden by this mod. Any payload file outside `Mods/<namespace>/` (other than declared `datPatches`) **must** appear here (`PAY003`). |
| `rawAccess` / `rawAccessJustification` | `bool` / `string?` | If `rawAccess` is `true`, `rawAccessJustification` is mandatory (`NC008`) and surfaced as a warning (`NC009`) + audit disclosure. |
| `customOpcodes` | `IReadOnlyList<CustomOpcodeDeclaration>` | `{ id: ushort, name: string, direction: OpcodeDirection }` (`ClientToServer`, `ServerToClient`, `Bidirectional`). ID `0` is rejected (`NC012`); cross-mod duplicates trigger `CNF003`. |
| `gealBuiltins` | `IReadOnlyList<string>` | Custom GEAL built-in names (`CNF004` on cross-mod collision). |
| `customControls` | `IReadOnlyList<string>` | Custom `.ui` control type names (`CNF005` on cross-mod collision; whitelisted during `.ui` linting). |
| `contentRegistries` | `IReadOnlyList<ContentRegistryDeclaration>` | `{ table, entryId, mode = "append" }`. Accepts **18 verified appendable tables**; closed tables (`ItemClasses` and `Stats` — fixed `23,362` / `0x5b42` rows) fail with `NC016`; non-`"append"` fails with `NC019`. |
| `serverHalf` | `ServerHalfMetadata?` | `{ id, minVersion, supportsGracefulFallback, channelName }`. |
| `integrity` | `PackageIntegrity?` | `{ packageSha256, files: Dictionary<string, string> }` verified on package validation (`INT001`–`INT005`). |

---

## 3. `LocalExile.Client.Waypoint` API

| Type | Key Members | Responsibility |
|---|---|---|
| `DualModManifest` | `Parse`, `LoadFromFile`, `ToJson`, `IsValidId`, `IsCanonicalCatalogId`, `IsValidSha256`, `IsSafeAssemblyFileName` | Strongly typed `mod.json` + `NativeClientManifest` model. |
| `SemVer` / `SemVerRange` | `TryParse`, `IsSatisfiedBy` | Semantic version parsing and range matching (`>=`, `<`, `^`, `~`, etc.). |
| `UiLayoutLinter` | `KnownControlTypes` (47 verified 3.29.3d controls, qualified & short names), `KnownProperties`, `Lint(content, filePath?, customControls?, knownModAssets?)` | Static `.ui` linter emitting `ValidationDiagnostic` (`UI001`–`UI017`). |
| `DualModPackageValidator` | `ValidateManifest`, `ValidatePackageDirectory`, `DetectConflicts`, `AppendableRegistryTables`, `ClosedRegistryTables`, `IsModNamespacedPath`, `ComputeFileSha256` | Full manifest, package, integrity, namespace, `.ui`, and cross-mod conflict validation (`MAN*`, `NC*`, `PKG*`, `INT*`, `PAY*`, `CNF001`–`CNF006`). |
| `ModBundlePacker` | `Pack`, `Verify`, `ReadEntry`, `Unpack`, `LoadManifest`, `EncodeClientText`, `IsClientTextExtension` | Deterministic `NMB1` stored-block `.bundle.bin` writer/verifier/reader. |
| `WaypointModInstaller` | `AuditPackage`, `Install`, `InstallFromZip`, `Uninstall`, static `ReadLedger`, static `ReadModStateChoices` | Atomic staging install/upgrade/uninstall across `Mods/Binaries/<id>/` and `Mods/Client/<id>/`, preserving `settings.json` on upgrade and updating `mod-state.txt` + `.nativemods-ledger.json`. `InstallOptions.AllowRawAccess` defaults to `false` (explicit opt-in). |
| `InstallAuditReport` | `Succeeded`, `IsUpgrade`, `RawAccessRequested`, `ReplacedStockPaths`, `Conflicts`, `Validation`, `DeployedFiles`, `ToHumanReadableSummary()` | Pre- and post-install security & conflict summary for Waypoint UI. |
| `WaypointExtenderManager` | `GetStatusAsync`, `InstallAsync`, `RemoveAsync`, static `ReadState` over `IPePatcherBridge` | Manages `Client/LocalExile.ClientHost.dll`, `Client/nativemods-host/`, optional `Client/version.dll`, `Mods/Client/.extender-state.json`, and PE patching (`ExtenderInstallMode.ImportTable` or `ExtenderInstallMode.ProxyVersionDll`). |
| `ClientPatcherBridge` | Concrete `IPePatcherBridge` implementation (`CheckAsync`, `PatchAsync`, `UnpatchAsync`, `VerifyAsync`) | Adapts `LocalExile.Client.Patcher.ClientPatcher` directly for `WaypointExtenderManager`. |

### `.ui` Linter Rules (`UiLayoutLinter`)

Prevents known 3.29.3d crashes and infinite layout loops discovered in [`07-spike-results.md`](../archive/initial-design/07-spike-results.md):

| Code | Rule |
|---|---|
| `UI002` | Rejects files starting directly with `<DEFINITIONS>` without leading whitespace, comment, or blank line. |
| `UI006` | Rejects unknown control types (accepts the 47 verified 3.29.3d controls plus `nativeClient.customControls`). |
| `UI007` | Rejects unbalanced `begin` / `end` blocks. |
| `UI008` | **Rejects `Picture` with `margin` directly inside a `StackLayout`** (known 100% CPU hang; use a spacer `Frame`). |
| `UI010` / `UI013` | Rejects invalid property assignments and unknown properties on stock controls (which throw `error: ":" expected` and exit the client). |
| `UI016` | **Rejects duplicate sibling control IDs** (throws uncaught MSVC `0xE06D7363` parser exception and closes the client). |
| `UI017` | Warns if a `mods/` image path referenced by `source`/`image` is missing from the mod payload. |

---

## 4. Security & Trust Model

| Component | Installed By | Trust Level & Safeguards |
|---|---|---|
| **Script Extender (`LocalExile.ClientHost.dll`)** | Player (opt-in via Waypoint) | First-party LocalExile code; build-verified against 3.29.3c/d SHA-256. |
| **Client Mod Assembly (`<id>.Client.dll`)** | Player (explicit click in Waypoint) | Native trust (same as a server mod). **Servers NEVER push executable code to clients.** Verified by SHA-256 (missing files with declared `assemblySha256`/`bundleSha256` fail with `INT001`/`INT002`); `rawAccess` requires `rawAccessJustification` (`NC008`) and is blocked by default (`InstallOptions.AllowRawAccess = false`) until explicitly approved by the player. |
| **Data Payloads & GEAL Scripts** | Player / Server-verified hash | Data-only (`.ui`, `.ot`, art, append-only rows). Linted before load; GEAL is capability-closed (no file/memory/network opcodes). |
| **Server-Driven UI Commands** | None required beyond extender | Closed, versioned operation set (`ServerUiCommand`); no arbitrary code execution. |

---

## 5. Bundle Format & Installation Layout

### Packed Release Bundle (`ModBundlePacker`)

- **Binary container (`<modId>-<shortHash12>.bundle.bin`):**
  - 16-byte header: `Magic` (`"NMB1"`), `FormatVersion` (`int32 = 1`), `EntryCount` (`int32`), `StoredBlockSize` (`int32 = 0x40000`).
  - Per-file payload split into `0x40000`-byte (256 KiB) chunks, each written as `[u32 chunkRawLen][0xCC 0x06][raw bytes...]` — compatible with Path of Exile's `OodleLZ_Decompress` (`0x142c9d520`) stored-block marker without requiring an Oodle compressor.
  - Client text extensions are encoded according to the verified **12 BOM+CRLF vs. 3 + 1 No-BOM** contract: `.ui`, `.ot`, `.otc`, `.it`, `.itc`, `.ao`, `.aoc`, `.act`, `.epk`, `.ddt`, `.pet`, `.trl` use **UTF-16LE with BOM (`FF FE`) + CRLF** (`ModBundlePacker.EncodeClientText`); `.sm`, `.mat`, `.amd`, and `art/uiimages1.txt` (case-insensitive) use **UTF-16LE without BOM + CRLF** (`ModBundlePacker.EncodeClientTextNoBom`; stray BOMs in loose `.sm`/`.mat`/`.amd` fail validation with `ENC001`).
- **Companion `manifest.json` (`ModBundleManifest`):** Records `formatVersion`, `modId`, `modVersion`, `bundleFile`, `bundleSha256`, `blockSize`, and `files[]` (`virtualPath`, `offset`, `uncompressedSize`, `storedSize`, `blockCount`, `sha256`, `encoding`).

### On-Disk Installation Layout

```text
<LocalExileRoot>/
├─ Client/
│  ├─ Game.exe                         # NEVER modified
│  ├─ Game.patched.exe                 # Optional +1 import for LocalExile.ClientHost.dll
│  ├─ LocalExile.ClientHost.dll        # Native shim (deployed by WaypointExtenderManager)
│  ├─ version.dll                      # Only present in ExtenderInstallMode.ProxyVersionDll
│  └─ nativemods-host/                 # Private self-contained win-x64 .NET 10 runtime + Host
│     └─ addresses/                    # Optional copied Address Library JSONs
├─ Mods/
│  ├─ Binaries/
│  │  ├─ mod-state.txt                 # Existing Waypoint enable state (`<id> on|off`)
│  │  └─ <id>/
│  │     ├─ mod.json
│  │     ├─ settings.json              # Preserved across upgrades
│  │     └─ <id>.dll                   # Server half
│  └─ Client/
│     ├─ .extender-state.json          # ExtenderStateRecord (WaypointExtenderManager)
│     ├─ .nativemods-ledger.json       # DeploymentLedger (WaypointModInstaller)
│     └─ <id>/
│        ├─ mod.json
│        ├─ <id>.Client.dll            # Client half (shadow-copied into collectible ALC)
│        ├─ <id>-<hash12>.bundle.bin   # Packed NMB1 stored-block bundle (PackClientBundle)
│        ├─ manifest.json              # ModBundleManifest
│        └─ files/                     # Loose dev overlays (UTF-16LE BOM CRLF for text)
│           └─ metadata/ui/mods/<id>/...
```
