# Regression & Pitfall Log: Runtime, Proton, ASLR, and DeadlockDetector Hazards

Source: `10-verification.md` §6 and `11-client-sdk.md` §2.

## 1. No System .NET 10 Runtime Available Inside Client Process

- **Pitfall:** Early design (`09-native-mods.md`) assumed the user's machine already has a shared .NET 10 runtime usable by `LocalExile.ClientHost.dll`.
- **Why it breaks:**
  - On Linux, Path of Exile runs as a Windows x64 binary inside a Proton/Wine prefix (`data/wine-prefix`), which cannot load a Linux host's .NET runtime and has no Windows `hostfxr`/`coreclr` installed.
  - On Windows, LocalExile distributes self-contained releases, so users may have no shared .NET 10 desktop/runtime installation.
- **Mitigation:** Ship a private self-contained `win-x64` .NET 10 runtime (`hostfxr.dll`, `coreclr.dll`, managed host) alongside `LocalExile.ClientHost.dll` so both native Windows and Proton load identically.

## 2. `DeadlockDetector` (`0x140e3ed10`) Main-Thread Stall Kills

- **Hazard:** The 3.29 client runs a `DeadlockDetector` (`0x140e3ed10`) that throws if the main game thread stalls beyond its timeout threshold (exact timeout not yet recovered).
- **Mitigations:**
  - Never boot CoreCLR synchronously inside `DllMain` (loader-lock deadlock + startup stall) or block the game thread during runtime initialization.
  - Defer CoreCLR bootstrap to a background worker thread and trigger managed initialization safely on the first frame tick once ready.
  - Configure workstation concurrent GC to keep game-thread pause times minimal.

## 3. CoreCLR VEH vs. MSVC C++ Exceptions (`0xE06D7363`) Under Wine/Proton

- **Hazard:** Path of Exile throws MSVC C++ exceptions (`RaiseException(0xE06D7363)`) as normal control flow. CoreCLR registers a Vectored Exception Handler (VEH) that observes every first-chance exception, and GC thread suspension interacts with Wine threading.
- **Mitigations:**
  - Native shim exception walls catch `0xE06D7363` around all game calls and prevent managed exceptions from ever unwinding across native game frames.
  - Keep managed code execution on game threads confined strictly to bounded bridge callbacks.
  - Fallback plan if spike R1 fails under Proton: compile client mods as NativeAOT DLLs against the identical `LocalExile.Client.Sdk` contract (loses hot reload, preserves SDK).

## 4. `DYNAMIC_BASE` (ASLR) Rebasing & Empty State Stack

- **ASLR (`DYNAMIC_BASE`):** The 3.29 PE32+ executable is relocatable. Never hardcode absolute `0x14xxxxxxx` addresses in the shim; store **RVAs** (`VA - 0x140000000`) and compute `moduleBase + rva` at runtime, verifying expected prologue bytes before installing any detour.
- **Empty State Stack:** The frame hook site (`0x141528fbf` in `StateStack::PassA`) is skipped while the state stack is empty (e.g., during early startup or transitions). The main-thread task queue drain requires a fallback trigger (such as a Win32 timer or secondary tick) so queued tasks cannot starve.
- **Proxy DLL under Wine:** If falling back to a `version.dll` proxy instead of import-table patching, Wine ignores local `version.dll` unless `WINEDLLOVERRIDES=version=n,b` is set in the launch environment.
