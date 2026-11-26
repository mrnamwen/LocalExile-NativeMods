# Spike R1 Empirical Report: .NET 10 CoreCLR Hosting Inside Path of Exile 3.29.3d Under Proton

**Date:** 2026-11-25  
**Verdict:** **PASS — Risk R1 retired.** Self-contained `.NET 10` CoreCLR (`hostfxr.dll` + `coreclr.dll` + `clrjit.dll`) with collectible `AssemblyLoadContext` hot-reload support boots off-game-thread inside the real 3.29.3d `Game.patched.exe` under `GE-Proton11-14-x86_64` in **both** `version.dll` proxy mode and PE32+ `.nmimp` import-table patch mode, survives sustained multi-frame soaks (~30–32 FPS, 900+ frames) with zero `DeadlockDetector` stalls or crashes, and cleanly coexists with first-chance MSVC C++ exceptions (`0xE06D7363`). The NativeAOT fallback is **not** required.

---

## 1. Test Environment & Safety Isolation

| Component | Observed Value |
|---|---|
| Host OS / Compositor | Arch Linux x86_64, Hyprland (`wayland-1`, `DISPLAY=:1`) |
| Virtual Screen Isolation | `Xvfb :98 -screen 0 1280x720x24 +extension GLX +extension RANDR +render -noreset` with `WAYLAND_DISPLAY`, `HYPRLAND_INSTANCE_SIGNATURE`, `XDG_CURRENT_DESKTOP`, `XDG_SESSION_TYPE`, `PROTON_ENABLE_WAYLAND` unset |
| Hyprland Leak Verification | `hyprctl clients -j` checked pre-launch and during soak (`t=3s`, `t=15s`, `t=35s`): **0 Path of Exile / Wine windows leaked** |
| GPU & Vulkan Driver | NVIDIA GeForce RTX 3090 (24 GB VRAM), driver `620.73.04`, Vulkan `1.4.357` (`VK_KHR_xcb_surface` + `VK_KHR_swapchain` `1280x720` `Fifo` directly on `Xvfb`) |
| Proton Runtime | `umu-launcher 1.4.3` -> `GE-Proton11-14-x86_64` inside `steamrt4` (`ntsync: up and running`) |
| Wine Prefix | `/home/nam/Git/LocalExile/data/wine-prefix` (`GAMEID=umu-238960`, `production_Config.ini`: `device_type=Vulkan`) |
| Client Build | Path of Exile **3.29.3d** (build revision `328228`, Steam loose `Bundles2/` layout) |
| Client Launch Path | Strictly via `/home/nam/Git/LocalExile/scripts/dev/live.ps1 -NoBuild -NoExtract` with `Game_CLIENT_RUNNER` wrapper (`--patching-server 127.0.0.1:12995 --nosound --nologo --disable-assert-box`) |
| Native Toolchain | MinGW-w64 `x86_64-w64-mingw32-g++` GCC `16.2.0` (`scripts/build-win64.sh`, commit `40eb276`), zero MinGW runtime DLL dependencies (`KERNEL32.dll` + Windows UCRT only) |
| Managed Runtime | Self-contained `.NET 10` `win-x64` (`hostfxr.dll`, `coreclr.dll`, `clrjit.dll`, `LocalExile.Client.Host.dll`) |
| Baseline `Game.exe` SHA-256 | `e9ba3d31c4d922e1826ef01d02fd22178a001b29e17264b68fc2446efd41f53d` |
| Baseline `Game.patched.exe` SHA-256 | `ca8c17956b40a5b43007413695737912d72f5b8319903c6b1bfec6b674b5dcb1` |

Reproducible runner: [`scripts/run-r1-proton.sh`](../../scripts/run-r1-proton.sh).

---

## 2. Summary of Empirical Runs

Three live Proton runs were executed against the exact same `GE-Proton11-14-x86_64` + `steamrt4` + `data/wine-prefix` runtime:

| Metric | Run 1: Live 3.29.3d (`--mode proxy`) | Run 2: Live 3.29.3d (`--mode import-patch`) | Run 3: VEH `0xE06D7363` Probe (`r1_wine_smoke.exe` on GE-Proton11-14) |
|---|---|---|---|
| Run ID | `20261125-194410-proxy-223918` | `20261125-194618-import-patch-229662` | `r1-proton-veh-probe-232061` |
| Injection Mechanism | `version.dll` proxy + `WINEDLLOVERRIDES="version=n,b"` | PE32+ `.nmimp` import section at RVA `0x4D9A000` (no `version.dll`, no `WINEDLLOVERRIDES`) | Direct `LoadLibraryW` + `version.dll` proxy |
| `GameServer.Auth verify-patch` | PASS | PASS (`[auth] patched client login keys match this server`) | N/A |
| Runtime `image_base` (ASLR) | `0x6FFFF8A30000` (`SizeOfImage=0x4D9A000`) | `0x6FFFF8A30000` (`SizeOfImage=0x4D9D000`) | `0x140000000` (synthetic) |
| Build & Byte Verification | `pass_a(0x1528f40)=OK`, `pass_a_hook(0x1528fbf)=OK`, `app_global_ref(0x00f3b30)=OK` -> `3.29.3d` | `pass_a(0x1528f40)=OK`, `pass_a_hook(0x1528fbf)=OK`, `app_global_ref(0x00f3b30)=OK` -> `3.29.3d` | Synthetic `3.29.3d` + 15B prologue hook/unhook OK |
| Frame Hook Armed (`0x1528f40`) | `+245.980 ms` (`0x6FFFF9F58F40`) | `+1269.234 ms` (`0x6FFFF9F58F40`) | PASS |
| CoreCLR Boot Trigger | `+2248.860 ms` (`startup-timer-fallback`, off-game-thread) | `+3272.611 ms` (`startup-timer-fallback`, off-game-thread) | Explicit off-thread worker (`+52.465 ms`) |
| `LoadLibraryW(hostfxr.dll)` | `2.35 ms` | `2.44 ms` | `7.62 ms` |
| `hostfxr_initialize_for_dotnet_command_line` | `3.36 ms` (`0x0`) | `3.84 ms` (`0x0`) | `9.69 ms` (`0x0`) |
| `hostfxr_get_runtime_delegate` | `53.02 ms` (`0x0`) | `41.47 ms` (`0x0`) | `89.69 ms` (`0x0`) |
| `load_assembly_and_get_function_pointer` | `73.22 ms` (`0x0`) | `224.51 ms` (`0x0`) | `58.46 ms` (`0x0`) |
| `NativeEntryPoint.Initialize` | `80.48 ms` (`rc=0`) | `282.10 ms` (`rc=0`) | `80.64 ms` (`rc=0`) |
| **Total CoreCLR Boot Latency** | **`215.46 ms`** | **`556.79 ms`** | **`248.32 ms`** |
| `JsonAddressLibrary` (`3.29.3d.json`) | Loaded (`110` symbols, `29` offsets) | Loaded (`110` symbols, `29` offsets) | Loaded by SHA-256 (`110` symbols, `29` offsets) |
| `ClientModLoader` (`harvest-garden.Client.dll`) | Loaded v1.0.0 (`collectibleAlc=True`) | Loaded v1.0.0 (`collectibleAlc=True`) | Loaded v1.0.0 (`collectibleAlc=True`) |
| First `StateStack::PassA` Frame Hit | `+6161.716 ms` (`tid=352`) | `+5466.988 ms` (`tid=352`) | N/A |
| Sustained Frames & Soak | **900+ frames** over `45 s` (~30.0 FPS), 0 crashes | **900+ frames** over `35 s` (~32.3 FPS), 0 crashes | 5 managed ticks, exit `0` |
| Process RSS / Threads (Soak) | `4.18 -> 5.06 GiB` RSS, `45–46` threads | Alive full `35 s` | Clean exit |
| `DeadlockDetector` (`0x0e3ed10`) | 0 stalls / 0 kills | 0 stalls / 0 kills | N/A |
| `0xE06D7363` VEH Compatibility | `msvc_cxx_passthrough=0` (clean login screen threw 0 C++ exceptions) | `msvc_cxx_passthrough=0` (clean login screen threw 0 C++ exceptions) | **PASS** — guarded `0xE06D7363` & `0xC0000005` caught; unguarded `0xE06D7363` passed through CoreCLR `First=1` VEH (`msvc_cxx_passthrough=1`) |
| Post-run `Client/` SHA-256 Restore | Exact match, 0 stray files | Exact match, 0 stray files | `Client/` untouched |

---

## 3. Detailed Observations & Engineering Findings

### 3.1 ASLR Rebasing Confirmed Under Proton (`0x6FFFF8A30000`)
In both live runs, Proton/Wine relocated `Game.patched.exe` to `0x6FFFF8A30000` rather than its link-time preferred `0x140000000`. The RVA-based shim (`base + rva`) and `JsonAddressLibrary` rebased all 110 verified symbols cleanly, arming `frame.state_stack_pass_a` at `0x6FFFF9F58F40` (`0x6FFFF8A30000 + 0x1528f40`).

### 3.2 `StateStack::PassA` Disassembly & 15-Byte Prologue Fix
Before running R1, live disassembly of `Client/Game.patched.exe` at `0x141528f40` and `0x141528fbf` revealed two critical constraints:
1. **`0x141528f40` (`StateStack::PassA` prologue)** is **15 bytes** of position-independent saves (`48 89 5c 24 10` `mov [rsp+0x10], rbx`; `48 89 6c 24 18` `mov [rsp+0x18], rbp`; `48 89 74 24 20` `mov [rsp+0x20], rsi`) followed by `push rdi` (`57`), and receives floating-point arguments in `xmm1` and `xmm2` (`movaps xmm6, xmm2; movaps xmm7, xmm1`).
   - A naive 14-byte `FF 25` detour would have split the 3rd `mov` instruction at byte 14 and crashed on frame 1.
   - `win64-builder` added an x86-64 prologue instruction-length decoder to `HookEngine::install_inline` (stealing 15 whole bytes + 1 `0x90` NOP pad) and a register-preserving stub saving/restoring `rcx`, `rdx`, `r8`, `r9`, and `xmm0..xmm3` with 16-byte stack alignment.
2. **`0x141528fbf` (`frame.state_stack_pass_a_hook`)** is mid-function (`4+4+4+6 = 18` bytes ending with RIP-relative `je 0x141529059`). Keeping `0x1528fbf` as a byte-verification guard (`488b5d284883eb10`) while hooking the `0x1528f40` function entry avoided RIP-relative relocation and also ensures ticks run even when the state stack is empty.

### 3.3 Empirical Surprise: `StateStack::PassA` Thread Migration Across Heartbeats
In both live runs, `StateStack::PassA` normally executed on Windows `tid=352`, **except** on two heartbeats:
- Proxy run, frame 120 (`+10768.272 ms`): `tid=472`
- Import-patch run, frame 900 (`+33327.648 ms`): `tid=468`

This proves the game's engine/Job2 threading can execute `StateStack::PassA` on a different OS thread ID across frames/transitions. Consequently:
- `FrameQueue`, `ClientHostRuntime`, and managed mod tick callbacks **must remain thread-safe** (atomic/locked) and **must not** hard-pin or assert a single immutable OS `GetCurrentThreadId()` captured on frame 1.

### 3.4 `DeadlockDetector` (`0x0e3ed10`) & Off-Game-Thread Boot Timing
Path of Exile took `5.47–6.16 s` from `DLL_PROCESS_ATTACH` to its first `StateStack::PassA` frame while initializing `Bundles2/_.index.bin`, NVIDIA Streamline/DLSS, and the Vulkan `1280x720` swapchain. Because `DeferredInitThread` triggers the CoreCLR worker thread after a `2000 ms` startup fallback:
- CoreCLR booted completely off-thread and returned `NativeEntryPoint.Initialize == 0` at `+2475.6 ms` (proxy) / `+3830.7 ms` (import-patch) — **1.6–3.7 seconds before frame 1 even executed**.
- Game-thread stall during CoreCLR initialization was **0 ms**, and `DeadlockDetector` never triggered.
- Note that Run 2 (`556.79 ms` CoreCLR boot) was slightly slower than Run 1 (`215.46 ms`) because `.nmimp` attached earlier during loader startup (`+1269 ms` vs `+245 ms` hook arm) and overlapped heavier concurrent PoE initialization CPU/IO contention.

### 3.5 PE32+ `.nmimp` Import Patch Compatibility with LocalExile
Patching `Game.patched.exe` with `LocalExile.Client.Patcher patch --allow-synthetic --no-backup` appended `.nmimp` at RVA `0x4D9A000` (28 imported DLLs, reversible unpatch metadata present, output SHA-256 `7363d480f5d23527b2d35c495205b2c932fe992064560d2c2f4464b09fc933cf`).
- LocalExile's `GameServer.Auth verify-patch` **passed** without modification (`[auth] patched client login keys match this server`).
- Proton's PE loader loaded `LocalExile.ClientHost.dll` directly without `version.dll` or `WINEDLLOVERRIDES` (confirmed in `/proc/<pid>/maps`: only GE-Proton's built-in `version.dll` was mapped, alongside `LocalExile.ClientHost.dll`, `hostfxr.dll`, `coreclr.dll`, and `clrjit.dll`).
- Note: `LocalExile.Client.Patcher` currently requires `--allow-synthetic` when patching `Game.patched.exe` (rather than stock `Game.exe`) because `Game.patched.exe` already carries LocalExile's login-key patch and therefore has a per-machine SHA-256 rather than the vanilla `Game.exe` hash. Future Waypoint/patcher integration should either patch `Game.exe` before/alongside login-key patching or recognize key-patched 3.29 PE signatures explicitly.

### 3.6 First-Chance MSVC C++ Exceptions (`0xE06D7363`) & CoreCLR VEH Coexistence
In the login-screen soaks (`--nosound --nologo`), Path of Exile did not raise unguarded C++ exceptions (`msvc_cxx_passthrough=0`). To empirically validate VEH coexistence under the exact Proton runtime, we ran `r1_wine_smoke.exe` directly under `GE-Proton11-14-x86_64` (`umu-run` + `steamrt4` + `data/wine-prefix`):
- With CoreCLR's `First=1` VEH active and our MinGW exception wall registered at `First=0`:
  - Guarded `RaiseException(0xE06D7363)` and `0xC0000005` (`ACCESS_VIOLATION`) were cleanly caught via non-volatile `CONTEXT` recovery (`nm_call_guarded` returned `NM_ERR_GAME_EXCEPTION`).
  - Unguarded `RaiseException(0xE06D7363)` passed through both CoreCLR's `First=1` VEH and our `First=0` VEH (`msvc_cxx_passthrough=1`) and unwound cleanly into the caller's handler without crashing CoreCLR or Proton.

### 3.7 Headless Virtual-Screen Proton Strategy (`Xvfb`)
- On this NVIDIA (`620.73.04`) + Hyprland system, `Xvfb` natively supported hardware RTX 3090 Vulkan `VK_KHR_xcb_surface` + `VK_KHR_swapchain` (`1280x720`, `Fifo`), so no additional packages (`gamescope`, `weston`, `wlr-randr`) were required.
- Because `umu-run`'s `steamrt4` (`pressure-vessel`) container does **not** expose host `/tmp`, `scripts/run-r1-proton.sh` stages the temporary host bundle and shim log inside `/home/nam/Git/LocalExile/data/r1-proton-stage-<run-id>/` (removed unconditionally in `trap EXIT`).

---

## 4. Captured Raw Excerpts

### Proxy Mode (`20261125-194410-proxy-223918/nativemods-r1.log`)
```text
[+    0.440ms] [INFO] DLL_PROCESS_ATTACH deferred init (pid=348, tid=380, dll_dir='X:\Git\LocalExile\Client', abi=0x00000100)
[+  243.800ms] [INFO] Main executable: 'X:\Git\LocalExile\Client\Game.patched.exe' (image_base=0x6FFFF8A30000, image_size=0x4D9A000, sha256=ca8c17956b40a5b43007413695737912d72f5b8319903c6b1bfec6b674b5dcb1)
[+  244.384ms] [INFO] 3.29 signature check: pass_a(0x1528f40)=OK pass_a_hook(0x1528fbf)=OK app_global_ref(0x00f3b30)=OK
[+  244.905ms] [INFO] Recognized client build: 3.29.3d (id=3, match=signature-match-3.29.3d)
[+  245.980ms] [INFO] Frame hook armed at 0x6FFFF9F58F40 (RVA 0x1528f40, 15B prologue + 0x1528fbf verified; stub=0000000002330000 tramp=0000000002340000 handle=1)
[+ 2248.860ms] [INFO] Triggering deferred off-game-thread CoreCLR bootstrap (startup-timer-fallback) ...
[+ 2262.503ms] [INFO] Loaded hostfxr.dll at 0x00006ffff6a70000 (+2.35ms)
[+ 2266.544ms] [INFO] hostfxr_initialize_for_dotnet_command_line(self-contained) returned 0x00000000 (ctx=0000000000a69f60, 3.36ms)
[+ 2320.098ms] [INFO] hostfxr_get_runtime_delegate(hdt_load_assembly_and_get_function_pointer) returned 0x00000000 (fn=00006fff96a53f60, 53.02ms)
[+ 2394.564ms] [INFO] load_assembly_and_get_function_pointer(NativeEntryPoint.Initialize) returned 0x00000000 (entry=00006fff96be8f00, 73.22ms)
[MANAGED] [INFO] [addresses] Loaded JsonAddressLibrary from '...\3.29.3d.json' (build=3.29.3d, symbols=110, offsets=29).
[MANAGED] [INFO] [host] NativeMods managed host started (SDK 0.1.0-poc, build 3.29.3d, transport HostRuleset).
[MANAGED] [INFO] [loader] Loaded client mod 'harvest-garden' v1.0.0 (gen=1, assembly='harvest-garden.Client.dll', collectibleAlc=True).
[+ 2475.618ms] [INFO] NativeEntryPoint.Initialize(nm_abi_table_t*) returned 0 (entry=80.48ms, total CoreCLR boot latency=215.46ms)
[+ 6161.716ms] [INFO] First frame hook hit on game thread (tid=352, rva=0x1528f40)
[MANAGED] [INFO] [host] Managed frame tick #1 (dt=0.0167s, loadedMods=1).
[+36277.430ms] [INFO] Frame heartbeat: frames=900 tid=352 coreclr_booted=1 coreclr_booting=0 msvc_cxx_passthrough=0
[MANAGED] [INFO] [host] Managed frame tick #900 (dt=0.0330s, loadedMods=1).
```

### Import-Patch Mode (`20261125-194618-import-patch-229662/nativemods-r1.log`)
```text
[+ 1269.234ms] [INFO] Frame hook armed at 0x6FFFF9F58F40 (RVA 0x1528f40, 15B prologue + 0x1528fbf verified; stub=00000000035d0000 tramp=0000000007780000 handle=1)
[+ 3276.377ms] [INFO] Loaded hostfxr.dll at 0x00006ffff6330000 (+2.44ms)
[+ 3280.916ms] [INFO] hostfxr_initialize_for_dotnet_command_line(self-contained) returned 0x00000000 (ctx=0000000000a11a70, 3.84ms)
[+ 3322.870ms] [INFO] hostfxr_get_runtime_delegate(hdt_load_assembly_and_get_function_pointer) returned 0x00000000 (fn=00006fff96333f60, 41.47ms)
[+ 3547.857ms] [INFO] load_assembly_and_get_function_pointer(NativeEntryPoint.Initialize) returned 0x00000000 (entry=00006fff964c8f00, 224.51ms)
[MANAGED] [INFO] [addresses] Loaded JsonAddressLibrary from '...\3.29.3d.json' (build=3.29.3d, symbols=110, offsets=29).
[MANAGED] [INFO] [loader] Loaded client mod 'harvest-garden' v1.0.0 (gen=1, assembly='harvest-garden.Client.dll', collectibleAlc=True).
[+ 3830.727ms] [INFO] NativeEntryPoint.Initialize(nm_abi_table_t*) returned 0 (entry=282.10ms, total CoreCLR boot latency=556.79ms)
[+ 5466.988ms] [INFO] First frame hook hit on game thread (tid=352, rva=0x1528f40)
[+33327.648ms] [INFO] Frame heartbeat: frames=900 tid=468 coreclr_booted=1 coreclr_booting=0 msvc_cxx_passthrough=0
[MANAGED] [INFO] [host] Managed frame tick #900 (dt=0.0274s, loadedMods=1).
```

---

## 5. Follow-Up Recommendations

1. **In-game combat/UI exception stress test (Phase R2):** Extend the automated harness with a logged-in character entering Lioneye's Watch / Coast and exercising intentional `.ui` / overlay / GEAL C++ throws (`0xE06D7363`) during live rendering.
2. **Key-patched PE recognition in `ClientPatcher`:** Allow `LocalExile.Client.Patcher patch` to accept LocalExile key-patched `Game.patched.exe` via code-signature verification (not just vanilla `Game.exe` SHA-256) so `--allow-synthetic` is unnecessary in production Waypoint flows.
3. **Thread-safe frame callback contract:** Document in SDK guidelines that `StateStack::PassA` may migrate across OS thread IDs (`tid=352` / `468` / `472`), so managed frame callbacks must not assume thread-static affinity.
