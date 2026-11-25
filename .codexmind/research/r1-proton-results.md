# Research Findings: Spike R1 Live Proton CoreCLR Hosting (3.29.3d)

**Status:** **PASSED (2026-11-25)** — Risk R1 (`.NET 10` CoreCLR + `hostfxr` + collectible ALC inside Path of Exile 3.29.3d under Proton) is retired. Full report: `docs/r1-proton-results.md`. Runner: `scripts/run-r1-proton.sh`.

## Verified Empirical Results (`GE-Proton11-14-x86_64`, `umu-run 1.4.3`, `steamrt4`, RTX 3090 on isolated `Xvfb`)

1. **Both injection modes succeed end-to-end on real 3.29.3d `Game.patched.exe`:**
   - **Proxy mode (`version.dll` + `WINEDLLOVERRIDES="version=n,b"`):** CoreCLR booted off-game-thread in **`215.46 ms`**, `JsonAddressLibrary` loaded `3.29.3d.json` (`110` symbols, `29` offsets), `ClientModLoader` loaded `harvest-garden.Client.dll` (`collectibleAlc=True`), and the client survived a 45 s soak (`900+` frames at ~30 FPS, `4.18–5.06 GiB` RSS, `45–46` threads) with 0 crashes and 0 `DeadlockDetector` kills.
   - **Import-table mode (`.nmimp` at RVA `0x4D9A000`, no `version.dll` / no `WINEDLLOVERRIDES`):** Passed LocalExile's `GameServer.Auth verify-patch`, booted CoreCLR off-game-thread in **`556.79 ms`**, loaded `3.29.3d.json` + `harvest-garden.Client.dll`, and survived a 35 s soak (`900+` frames at ~32 FPS) with 0 crashes.
2. **ASLR rebasing confirmed live:** Proton relocated `Game.patched.exe` to `0x6FFFF8A30000` (not `0x140000000`). RVA rebasing worked across all 110 symbols and armed `StateStack::PassA` at `0x6FFFF9F58F40`.
3. **First-chance `0xE06D7363` + CoreCLR VEH coexistence under `GE-Proton11-14`:** Verified via `r1_wine_smoke.exe` under `umu-run` (`248.32 ms` CoreCLR boot): `nm_call_guarded` caught guarded `0xE06D7363` and `0xC0000005` via `First=0` VEH, and unguarded `0xE06D7363` passed cleanly through CoreCLR's `First=1` VEH (`msvc_cxx_passthrough=1`).

## Key Architecture & Gotcha Notes Retained for Future Sessions

- **Hook `0x1528f40` (15-byte prologue), verify `0x1528fbf`:** `StateStack::PassA` (`0x1528f40`) begins with 15 bytes of position-independent saves (`48 89 5c 24 10 | 48 89 6c 24 18 | 48 89 74 24 20`) and takes `(void* rcx, float xmm1, float xmm2)`. Do not use a fixed 14-byte steal (splits byte 14) or mid-function detour at `0x1528fbf` (ends in RIP-relative `je`).
- **Frame thread ID can migrate:** Live heartbeats showed `StateStack::PassA` running primarily on `tid=352`, but occasionally on `tid=472` (frame 120) and `tid=468` (frame 900). Never assume a single immutable OS thread ID in frame callbacks.
- **Virtual screen testing on NVIDIA + Hyprland:** `Xvfb` with `WAYLAND_DISPLAY` and `HYPRLAND_INSTANCE_SIGNATURE` unset runs hardware NVIDIA Vulkan `1280x720` swapchains with zero Hyprland window leaks and no extra compositor packages (`gamescope`/`weston` unnecessary).
- **Proton `steamrt4` container path rule:** Host `/tmp` is NOT visible inside `steamrt4`; stage test bundles/logs under `/home/nam/Git/LocalExile/data/` and clean up in `trap EXIT`.
- **Patcher UX gap noted:** `LocalExile.Client.Patcher patch` needed `--allow-synthetic` for `Game.patched.exe` because LocalExile's login-key patch changes the SHA-256 from stock `Game.exe`.
