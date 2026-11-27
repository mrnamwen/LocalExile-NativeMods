# Testing, Cross-Compilation & Live 3.29.3d Proton Verification

Verify 95% of your mod deterministically in milliseconds with zero game binary (`SimulatedNativeBridge`, collectible ALC unload checks, live Gateway Roslyn compile tests), cross-compile Win64 binaries with PE import auditing, and run isolated headless `Xvfb` Proton tests against real Path of Exile `3.29.3d`.

[← Documentation Home](../README.md) · [Capabilities & Limits](capabilities-and-limits.md) · [Spike R1 Proton Results](../reports/r1-proton-results.md) · [Maxwell Pet PoC Results](../reports/maxwell-pet-poc.md)

---

## 1. Fast Deterministic Unit & E2E Testing (No Game Binary Needed)

Test manifest validation, `.ui` linting, Waypoint installation, `ClientHostRuntime` + `SimulatedNativeBridge`, dual-transport `ClientBridgeHost`, and **collectible ALC hot-reload reclamation** (`UnloadedCleanly == true`) in xUnit:

```csharp
using LocalExile.Client.Host;
using LocalExile.Client.Host.Interop;
using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Addresses;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Waypoint;

// 1. Validate manifest, namespace rules, .ui layouts, encodings & cross-mod conflicts
ValidationReport report = DualModPackageValidator.ValidatePackageDirectory(packageDir, SemVer.Parse(SdkVersion.Current));
Assert.True(report.IsValid, string.Join('\n', report.Errors));

// 2. Lint individual .ui files directly
IReadOnlyList<ValidationDiagnostic> uiDiags = UiLayoutLinter.Lint(
    File.ReadAllText(uiPath), uiPath, customControls: ["GrowthGauge"]);
Assert.DoesNotContain(uiDiags, d => d.Severity == DiagnosticSeverity.Error);

// 3. Install into a temp LocalExile root with WaypointModInstaller
var installer = new WaypointModInstaller();
InstallAuditReport audit = installer.Install(packageDir, tempLocalExileRoot, new InstallOptions { PackClientBundle = true });
Assert.True(audit.Succeeded);

// 4. Boot ClientHostRuntime over SimulatedNativeBridge & verify collectible ALC unload
var bridge = new SimulatedNativeBridge(ClientBuildId.Build3293d);
using var host = new ClientHostRuntime(bridge, new ClientHostOptions
{
    TransportKind = NetTransportKind.HostRuleset,
    ShadowCopyRoot = Path.Combine(tempRoot, "shadow"),
});
host.Start();
host.Loader.LoadModFromDirectory(Path.Combine(tempLocalExileRoot, "Mods", "Client", "my-feature"));
host.TickFrame(1f / 60f);

// Verify zero ALC pinning (catches System.Text.Json leaks, forgotten TrackDisposable, & leaked Game.Frame delegates!)
ModUnloadResult unload = await host.Loader.UnloadModAsync("my-feature");
Assert.True(unload.UnloadedCleanly);
```

### Live Roslyn Gateway Compilation Tests

To guarantee your `*LocalExileEntry.cs` file compiles cleanly inside LocalExile's live Gateway `ModHost`, reference `Microsoft.CodeAnalysis.CSharp` in your test project and compile the `.cs` entry point against LocalExile's `GameServer.Gateway.dll`, `GameServer.Core.dll`, `GameServer.Modding.dll`, and `game.dll` (see `tests/DualMod.LootTools.Tests/`, `tests/DualMod.StashQol.Tests/`, and `tests/DualMod.SkipCampaign.Tests/`).

### Running the Full Managed, Native & Generator Verification Gates

```bash
# 1. Address Library & API Docs drift checks
python3 scripts/generate-address-table.py --check
python3 scripts/generate-api-docs.py --check

# 2. .NET 10 solution build & all 13 xUnit test suites (44 projects, TreatWarningsAsErrors=true)
dotnet build NativeMods.slnx -c Release
dotnet test NativeMods.slnx -c Release --no-build

# 3. C++20 native shim unit tests (Linux Release, -Wall -Wextra -Wpedantic -Werror)
cmake -S src/native-shim -B build/native-shim -DCMAKE_BUILD_TYPE=Release
cmake --build build/native-shim
ctest --test-dir build/native-shim --output-on-failure

# 4. Optional Linux Debug ASan + UBSan native shim check
cmake -S src/native-shim -B build/native-shim-asan -DCMAKE_BUILD_TYPE=Debug \
  -DCMAKE_CXX_FLAGS="-fsanitize=address,undefined -fno-omit-frame-pointer"
cmake --build build/native-shim-asan
ASAN_OPTIONS=detect_leaks=1 ctest --test-dir build/native-shim-asan --output-on-failure
```

**Verified Phase 5 (`1.2.0`) Baselines:**
- **.NET 10 xUnit:** **`434/434`** passing across all `13` test suites (`44` projects, `0` warnings, `0` errors).
- **C++20 Native Shim:** **`35/35`** passing under `-Wall -Wextra -Wpedantic -Werror` (C ABI `0.6.0`, `1168 B` / `145` slots / `162` exports).
- **Address Library Port Self-Verify:** **`447/447` (100%)** exact RVA & byte parity via `scripts/port-address-library.py --verify-self`.
- **LocalExile Part 2b + 2c (`c0392f414` atop `origin/main` `affb3b104`):** `43/43` `GameServer.Data.Tests`, `552/553` `GameServer.Core.Tests` (1 skip), `1,608/1,608` `GameServer.Gateway.Tests`, plus `76/76` NativeMods live Roslyn compile checks (`0` warnings/errors).

---

## 2. Cross-Compiling Win64 DLLs & Wine Smoke Verification

Build the real Win64 `LocalExile.ClientHost.dll`, `version.dll` proxy, `r1_wine_smoke.exe`, and self-contained `win-x64` `.NET 10` host bundle into `dist/win-x64/`:

```bash
./scripts/build-win64.sh
```

`scripts/build-win64.sh` automatically:
1. Cross-compiles `src/native-shim` via MinGW-w64 (`x86_64-w64-mingw32-g++`).
2. Publishes self-contained `win-x64` `LocalExile.Client.Host` (`hostfxr.dll`, `coreclr.dll`, `clrjit.dll`, etc.).
3. Stages `addresses/{3.29.3d,3.29.3c}.json` and example client mods.
4. Audits PE imports via `x86_64-w64-mingw32-objdump -p`, failing if any non-system MinGW runtime DLL (`libgcc`, `libstdc++`, `libwinpthread`) leaks in.

Run fast Wine smoke checks before touching Proton:

```bash
wine build/native-shim-win64/nativemods_shim_tests.exe
wine dist/win-x64/r1_wine_smoke.exe
```

---

## 3. Live Testing Against Real 3.29.3d Under Proton (`Xvfb`)

All headless Proton test runners enforce strict isolation and zero-mutation guarantees:
- **Isolated `Xvfb` virtual screen (`1280×720×24`):** Unsets `WAYLAND_DISPLAY`, `HYPRLAND_INSTANCE_SIGNATURE`, `XDG_CURRENT_DESKTOP`, `XDG_SESSION_TYPE`, and `PROTON_ENABLE_WAYLAND`, with pre/mid/post `hyprctl clients -j` zero-window-leak assertions.
- **Strictly offline local server:** Launches exclusively via LocalExile's `scripts/dev/live.ps1` (`--patching-server 127.0.0.1:PORT`) — never connects to official GGG servers.
- **Zero-mutation guarantee on `Client/`:** Records `Game.exe` and `Game.patched.exe` SHA-256 before launch, restores everything in `trap EXIT`, and verifies exact SHA-256 match on exit.
- **Isolated PipeWire null sink (`module-null-sink`):** Captures real 48 kHz stereo FMOD 2.03 PCM energy via `parec` without emitting audio to desktop speakers.
- **Container `/tmp` staging rule:** Because `umu-run`'s `steamrt4` (`pressure-vessel`) container does not mount host `/tmp`, always stage temporary test artifacts under `<LocalExileRoot>/data/` (cleaned up in `trap EXIT`).

### Runner Commands

```bash
# 1. CoreCLR & Frame-Hook Soak (proxy mode & PE32+ .nmimp import-patch mode)
./scripts/run-r1-proton.sh --mode proxy --soak-seconds 45
./scripts/run-r1-proton.sh --mode import-patch --soak-seconds 35

# 2. Full Phase 0/1/2/4 E2E Proton Suite (33/33 live hooks + 16/16 E2E checks + FMOD PCM)
./scripts/run-phase0-proton.sh --mode proxy

# 3. Maxwell Pet MTX Headless Proton Verification (Cosmetics K menu + .ui + looping .ogg)
./scripts/run-maxwell-proton.sh

# 4. Interactive Desktop Testing (Hyprland/Wayland/X11, automatic characters.db snapshot & teardown)
./scripts/launch-desktop.sh --mod all      # Maxwell Pet + Orb of Siphoning together
./scripts/launch-desktop.sh --mod siphon   # Single mod (--dry-run validates plan without launching)
./scripts/launch-desktop.sh --mod maxwell
```

### Expected `nativemods-shim.log` Live Markers (`1.2.0`)

Verify these lines in `nativemods-shim.log` after a live Proton run:

```text
[OVERLAY] Armed live FileSystem hooks (exists=OK size=OK read_range=OK read_whole=OK read_v5=OK hash_lookup=OK)
[OVERLAY] Served 'data/pet.datc64' (... bytes via ...)
[UI] Armed Control::Load root observer (OK)
[NET] Armed live channel hooks (chat_rx=OK ruleset_0x2c9=OK msg_pump=OK)
[PHASE1] Armed live hooks (wndmsg=OK ui_init=OK ig_leave=OK league_fill=OK cc_enter=OK cc_leave=OK tab=OK map_grp=OK map_key=OK c2s_0x12=OK s2c_0x13=OK)
[PHASE2] Armed live hooks (init_area=OK area_load_leave=OK ig_leave=OK filter_loaded=OK)
[PHASE4] Armed live hooks (tab=OK combo=OK listbox=OK tooltip=OK aff_bit=OK aff_tgt=OK ui_prop=OK ui_destroy=OK)
[PHASE5] Armed live hooks (stack_deser=OK fmt_count=OK sock_cell=OK sock_rebuild=OK ig_leave=OK)
[MANAGED] [INFO] [host] NativeMods managed host started (SDK 1.2.0, build 3.29.3d, transport HostRuleset).
[AUDIO] FMOD 2.03 initialized (output=default|nosound, sys=0x...)
[AUDIO] Playing OGG 'Audio/Mods/Maxwell/stock_market.ogg' (bytes=..., loop=1, volume=..., handle=..., output=default|nosound, fmod_rc=0)
[UI] Captured native UI root 0x...
[UI] Loaded native window 'Metadata/UI/Mods/Maxwell/MaxwellPanel.ui' (root=0x..., control=0x...)
```

Verified `1.1.0` live Proton baseline (`e05029d`, run `20261126-225934-proxy-397097`): `abi=0x00000500`, `SDK 1.1.0`, `426` symbols / `357` offsets, **`33/33`** live hooks `OK` (`0` `SKIP`), `4/4` client mods loaded with live native `GealRegisterBuiltin` (`GardenRipen`, `MaxwellDance`), **`16/16`** E2E PASS (`sdk=1.1.0`), **`0`** `[WARN]`/`[ERROR]`/SEH faults, FMOD PCM `-24.5 dB` mean / `-9.6 dB` max, `0` Hyprland window leaks, exact stock `Game.exe` / `Game.patched.exe` SHA-256 baseline restored. On `1.2.0` (`abi=0x00000600`), all **`38/38`** live hooks arm `OK` (including `[PHASE5]` 5/5).
