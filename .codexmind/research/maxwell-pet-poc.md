# Maxwell the Cat Pet MTX — Live 3.29.3d Proton Verification Summary

- **Date / Run ID:** `20261125-213526-proxy-515325` (Run 15, clean `main` `64bd640`, zero ad-hoc patches)
- **Target:** Path of Exile `3.29.3d` (`build 328228`, `Game.patched.exe` SHA-256 `ca8c17956b40a5b43007413695737912d72f5b8319903c6b1bfec6b674b5dcb1`) + LocalExile Gateway under `GE-Proton11-14` (`umu-launcher 1.4.3`, `steamrt4`, `ntsync`) inside isolated `Xvfb :96` (`1280x720x24`) + isolated PipeWire `module-null-sink`.
- **Harness:** `scripts/run-maxwell-proton.sh` (launches exclusively via `LocalExile/scripts/dev/live.ps1`, stages server mods via `Game_MODS_FOLDER`, backs up & restores `data/characters.db*`, verifies `Game.exe`/`Game.patched.exe` SHA-256 and `Mods/` integrity on exit, checks `hyprctl clients -j` for 0 leaks).

## Key Empirical Results (16 / 16 PASS)
- **Off-thread CoreCLR boot:** `210.60 ms` (`NativeEntryPoint.Initialize == 0` in `94.31 ms`), loaded 2 client mods (`harvest-garden`, `maxwell`) in collectible ALCs.
- **Append-only `.datc64` tables served via Route A:** `Data/Pet.datc64` (`122,950 B`, row `516`), `Data/MTXTypeGameSpecific.datc64` (`1,294,256 B`, row `7849`), `Data/MTXTypes.datc64` (`4,339,732 B`, row `8557`). Note client requests uppercase `MTX` paths; Route A normalizes case-insensitively.
- **Native Cosmetics (`K`) Menu Pet Slot 1 flow:**
  - Opening `K` triggers `C->S 0x01f2 (Records)` -> `S->C 0x01f4 (7850 owned)`. First-time tile population takes ~9–12 s under `Xvfb`; client loads `Art/2DItems/Pets/Mods/Maxwell/MaxwellPet.dds` (`129,764 B`) + `.dds.header` (`2,008 B`).
  - Pet Slot 1 is at `(920, 385)` at `1280x720` (Pet Slot 2 at `(920, 450)`). In the 3-column alphabetical Pet picker, `Maxwell the Cat Pet` sits at row `106/172` (`~62%` down the scrollbar).
  - Hovering displays native tooltip `"Maxwell the Cat Pet / Pet / Summons your spinning Maxwell the Cat pet"` alongside stock pets.
  - Clicking Maxwell sends `C->S 0x01f9 (slot 18, record 8558, hash 0xa1eb592e, option 0)` -> `CosmeticsFeature.SpawnPets` creates `0x4000006` (`Metadata/Pet/Mods/Maxwell/MaxwellPet`) -> `LocalExilePetMtxRegistrar` pushes `MaxwellStateSync` over `0x02C9` (`ruleset 0xA0`) -> FMOD 2.03 automatically starts looping `stock_market.ogg` and Pet Slot 1 displays Maxwell's custom 2D icon (`IN USE — EQUIPPED`).
- **3D model & animation:** All 18 Maxwell 3D assets (`MaxwellPet.{ot,ao,act}`, `maxwell.{amd,sm,ast,smd.header,smd}`, both `.mat` files, all 3 `.dds`/`.dds.header` pairs) served cleanly; 5-frame burst (`0.7 s` spacing) visually confirms tuxedo-cat texturing, hovering, and full 360° rotation in Lioneye's Watch.
- **Native `MaxwellPanel.ui`:** Plain UTF-8 `.ui` auto-transcoded to UTF-16LE+BOM+CRLF (`11,140 B`) by `OverlayAndChannels`, loaded directly onto root `00000424960f6800`, dead-center, already synced to Cosmetics equip (`summoned=True`, `Music: Looping (75%)`, live 60 Hz `Yaw`/`Float`). Clicking `MAXWELL DANCE` fired `GEAL MaxwellDance triggered on object 0x4000006 (boost=2, total=1)`; clicking `+30` round-tripped `0x02CA` -> `0x02C9` (`Spin: 120 deg/s`); clicking `CLOSE` hid the window cleanly.
- **Audio:** FMOD 2.03 (`output=default, fmod_rc=0, bytes=2237309, loop=1, volume=0.75`); isolated PipeWire null-sink 4 s capture measured `mean_volume: -23.1 dB, max_volume: -7.9 dB`.
- **Stability:** `2,700+` frames, 0 crashes, 0 `DeadlockDetector` kills, 0 `msvc_cxx_passthrough`, 0 Hyprland leaks.

## Live Issues Diagnosed & Resolved During Harness Iteration
1. `Data/Pet.datc64` `foreignrow[]` aliasing crash (`0x140129838`) -> see `regressions/dat64-foreignrow-aliasing.md` (fixed in `105ebcf`).
2. `ui_create_window` `0x0` wrapper frame clipping & broken child binding -> fixed in `8a6d9ea` (`Control::Load(root, path)` directly onto root, returning `*(new_end - 1)`).
3. UTF-8 `.ui`/`.ot`/`.ao`/`.act`/`.amd`/`.sm` overlay auto-transcoding to UTF-16LE+BOM+CRLF -> fixed in `8a6d9ea`.
4. `LocalExile` `data/characters.db` saves Cosmetics (`K`) picks -> `run-maxwell-proton.sh` snapshots and restores `data/characters.db*` in `trap cleanup` so `/home/nam/Git/LocalExile/` remains 100% unmodified.
