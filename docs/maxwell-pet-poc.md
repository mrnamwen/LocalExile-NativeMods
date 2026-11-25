# Maxwell the Cat Pet MTX — Live 3.29.3d Proton PoC Results

This document records the empirical end-to-end verification of **`DualMod.MaxwellPet`** on the real **Path of Exile 3.29.3d** client (`build 328228`, `Game.patched.exe` SHA-256 `ca8c17956b40a5b43007413695737912d72f5b8319903c6b1bfec6b674b5dcb1`) running under **GE-Proton11-14** (`umu-launcher 1.4.3`, `steamrt4`, `ntsync`) against a live **LocalExile** Gateway server inside an isolated `Xvfb` virtual screen (`1280x720x24`) and an isolated PipeWire null audio sink.

> **Result: 16 / 16 live integration assertions PASSED** (0 crashes, 0 `DeadlockDetector` kills, 0 `msvc_cxx_passthrough` exceptions, 0 Hyprland window leaks, 100% bit-exact restore of `/home/nam/Git/LocalExile/`).

---

## 1. What Was Proven Live In-Game

1. **Append-only `.datc64` Brand-New Pet MTX (`Data/MtxTypes.datc64`, `Data/MtxTypeGameSpecific.datc64`, `Data/Pet.datc64`):**
   - Served through the live C++20 Route A `FileSystem` detours (`exists`, `size`, `read_range`, `read_whole`, `read_v5`) at client startup (`+2218 ms..+2235 ms`), appending `MtxTypes[8557]` (`HASH32=0xA1EB592E`, `ShortId="MaxwellPet"`), `MtxTypeGameSpecific[7849]`, and `Pet[516]` (`HASH32=0x1F3AB11A`, `HASH16=0xAE20`) while keeping all 516 stock pets intact.
2. **Native Cosmetics (`K`) Menu Pet Slot 1 Display & Direct Equip (`C->S 0x01f9`):**
   - Opening the in-game Cosmetics (`K`) tab triggers `S->C 0x01f4` (`7,850` owned records: `7,849` stock + brand-new Maxwell record `8558`).
   - Opening **Pet Slot 1** displays **`Maxwell the Cat Pet`** with its custom 2D icon (`Art/2DItems/Pets/Mods/Maxwell/MaxwellPet.dds`, `156x156` RGBA8) and native PoE tooltip (*"Summons your spinning Maxwell the Cat pet"*) right alongside stock pets (`Mallard Duck Pet`, etc.).
   - Clicking Maxwell directly in the native Cosmetics (`K`) Pet Slot 1 picker sends `C->S 0x01f9 (slot 18, record 8558, hash 0xa1eb592e, option 0)`. LocalExile's `CosmeticsFeature.SpawnPets` spawns the authoritative pet entity (`0x02bb create pet 0x4000006 Metadata/Pet/Mods/Maxwell/MaxwellPet at (203, 264)`), equips Maxwell's custom 2D icon into Pet Slot 1 with the `IN USE — EQUIPPED` badge, and `LocalExilePetMtxRegistrar` pushes `MaxwellStateSync` over `S->C 0x02C9` (`ruleset 0xA0`) to start looping audio and synchronize the client mod automatically.
3. **Custom 3D Mesh, Materials, Textures, and Spinning/Hovering Animation via Live Route A Overlay:**
   - All **18** Maxwell 3D asset files (`MaxwellPet.{ot,ao,act}`, `maxwell.{amd,sm,ast,smd.header,smd}`, `maxwell_dingus.mat`, `maxwell_whiskers.mat`, and all 3 `.dds` + `.dds.header` texture pairs) were served transparently on demand (`+78125 ms..+78256 ms`).
   - Multi-timestamp virtual-screen captures at `0.7 s` intervals (`07-maxwell-spin-0..4.png` and zoomed montage `08-maxwell-spin-strip.png`) visually verify Maxwell the Cat textured in black-and-white tuxedo fur, hovering above the ground, and completing a smooth 360° rotation beside the player in Lioneye's Watch (`1_1_town`).
4. **Custom Native `MaxwellPanel.ui` (Auto-Transcoded UTF-8 -> UTF-16LE, Centered, Live Interactive):**
   - Running `/maxwell` opens `Metadata/UI/Mods/Maxwell/MaxwellPanel.ui` — stored as plain UTF-8 in git, auto-transcoded to UTF-16LE + BOM + CRLF (`11,140` bytes) on the fly by `OverlayAndChannels`, and loaded directly onto the live game UI root (`00000424960f6800`) so `float = centre` centers the ornate window cleanly on screen.
   - The panel opens **already synchronized** with the Cosmetics (`K`) equip (`summoned=True`, `Music: Looping (75%)`, `STOP MUSIC`, `DISMISS MAXWELL`, live 60 Hz `Yaw` and `Float` telemetry).
   - Real mouse clicks (`xdotool`) on the native buttons executed live C# callbacks inside the collectible ALC:
     - Clicking **`MAXWELL DANCE`** invoked the custom GEAL built-in (`GEAL MaxwellDance triggered on object 0x4000006 (boost=2, total=1)`), incrementing `Dances: 1`.
     - Clicking **`+30`** sent `C->S 0x02CA` (`121` payload / `125` wire bytes) -> Gateway replied `S->C 0x02C9` (`309` bytes) -> updated live UI text to `"Updated Maxwell motion: spin=120 deg/s, float=14."` and `Spin: 120 deg/s`.
     - Clicking **`CLOSE`** hid the native window cleanly via `UiSetVisible`.
5. **Looping Custom OGG Audio Through FMOD 2.03 + Isolated PipeWire PCM Verification:**
   - Equipping Maxwell initialized FMOD 2.03 (`output=default`) and started looping `Audio/Mods/Maxwell/stock_market.ogg` (`2,237,309` bytes, 48 kHz stereo Vorbis, `loop=1`, `volume=0.75`, `handle=1`, `fmod_rc=0`).
   - An independent 4-second `ffmpeg -f pulse` recording from the isolated PipeWire null-sink `.monitor` measured **`mean_volume: -23.1 dB, max_volume: -7.9 dB`** (proving real audible PCM output without playing on the user's speakers).

---

## 2. Final Gold-Standard Run Telemetry (`20261125-213526-proxy-515325`)

| Metric | Measured Value |
|---|---|
| Client executable | `Game.patched.exe` (PoE 3.29.3d, build `328228`) |
| Injection mode | `version.dll` proxy (`WINEDLLOVERRIDES="version=n,b"`) |
| Proton / Wine runtime | `GE-Proton11-14` (`umu-launcher 1.4.3`, `steamrt4`, `ntsync`) |
| Virtual display | `Xvfb :96` (`1280x720x24`, Wayland/Hyprland env stripped) |
| Audio isolation | PipeWire `module-null-sink` (`nativemods_maxwell_...`) |
| Hyprland window leaks | **0** (verified pre-launch, `t=5s`, post-boot, in-game, pre-shutdown) |
| Off-thread CoreCLR boot latency | **`210.60 ms`** (`NativeEntryPoint.Initialize` returned `0` in `94.31 ms`) |
| Loaded client mods (collectible ALCs) | `2` (`harvest-garden` v1.0.0, `maxwell` v1.0.0) |
| First `StateStack::PassA` game-thread tick | `+6530.790 ms` (`tid=352`, RVA `0x1528f40`) |
| Handshake (`0x02CA` -> `0x02C9`, ruleset `0xA0`) | Frame `#1452` (`388 B` payload -> `264 B` ack, `HostRuleset`) |
| Cosmetics Pet Slot 1 equip (`C->S 0x01f9`) | `slot 18, record 8558 (hash 0xa1eb592e), option 0` -> entity `0x4000006` |
| FMOD 2.03 OGG playback | `stock_market.ogg` (`2,237,309 B`, `loop=1`, `volume=0.75`, `fmod_rc=0`) |
| Isolated PipeWire PCM capture (4 s) | `mean_volume: -23.1 dB`, `max_volume: -7.9 dB` |
| Total game-thread frames / crashes | `2,700+` frames, **0 crashes**, **0 `msvc_cxx_passthrough`** |
| Post-run LocalExile restore | `Game.exe` & `Game.patched.exe` SHA-256 identical; `Mods/` & `data/characters.db` restored |

### Complete Route A Overlay Serve Log (24 Hits)

| Timestamp | Virtual Path | Bytes | Hook |
|---|---|---|---|
| `+2218.465 ms` | `Data/Pet.datc64` | `122,950` | `read_whole` |
| `+2225.829 ms` | `Data/MTXTypeGameSpecific.datc64` | `1,294,256` | `read_whole` |
| `+2234.860 ms` | `Data/MTXTypes.datc64` | `4,339,732` | `read_whole` |
| `+63403.844 ms` | `Art/2DItems/Pets/Mods/Maxwell/MaxwellPet.dds.header` | `2,008` | `read_whole` |
| `+63407.965 ms` | `Art/2DItems/Pets/Mods/Maxwell/MaxwellPet.dds` | `129,764` | `read_whole` |
| `+78125.560 ms` | `Metadata/Pet/Mods/Maxwell/MaxwellPet.ot` | `444` | `exists` + `read_whole` |
| `+78134.869 ms` | `Metadata/Pet/Mods/Maxwell/MaxwellPet.ao` | `582` | `exists` + `read_whole` |
| `+78136.326 ms` | `Metadata/Pet/Mods/Maxwell/MaxwellPet.act` | `484` | `read_whole` |
| `+78140.248 ms` | `Art/Models/Mods/Maxwell/maxwell.amd` | `272` | `read_whole` |
| `+78144.671 ms` | `Art/Models/Mods/Maxwell/maxwell.sm` | `518` | `read_whole` |
| `+78145.540 ms` | `Art/Models/Mods/Maxwell/maxwell.ast` | `2,125` | `read_whole` |
| `+78146.135 ms` | `Art/Models/Mods/Maxwell/maxwell.smd.header` | `280` | `read_whole` |
| `+78161.722 ms` | `Art/Models/Mods/Maxwell/maxwell.smd` | `15,425` | `read_whole` |
| `+78163.184 ms` | `Art/Models/Mods/Maxwell/textures/maxwell_dingus.mat` | `1,542` | `read_whole` |
| `+78165.406 ms` | `Art/Models/Mods/Maxwell/textures/maxwell_whiskers.mat` | `1,640` | `read_whole` |
| `+78221.816 ms` | `Art/Models/Mods/Maxwell/textures/maxwell_dingus_dxt1.dds.header` | `1,520` | `read_whole` |
| `+78223.612 ms` | `Art/Models/Mods/Maxwell/textures/maxwell_flat_normal.dds.header` | `1,520` | `read_whole` |
| `+78225.272 ms` | `Art/Models/Mods/Maxwell/textures/maxwell_whiskers_rgba.dds.header` | `840` | `read_whole` |
| `+78250.096 ms` | `Art/Models/Mods/Maxwell/textures/maxwell_whiskers_rgba.dds` | `174,912` | `read_whole` |
| `+78250.537 ms` | `Art/Models/Mods/Maxwell/textures/maxwell_flat_normal.dds` | `21,992` | `read_whole` |
| `+78255.945 ms` | `Art/Models/Mods/Maxwell/textures/maxwell_dingus_dxt1.dds` | `174,924` | `read_whole` |
| `+83220.778 ms` | `Metadata/UI/Mods/Maxwell/MaxwellPanel.ui` | `11,140` | `read_whole` (auto-transcoded UTF-8 -> UTF-16LE+BOM+CRLF) |

---

## 3. Live Runtime Root Causes Discovered & Fixed During Testing

Live Proton testing surfaced three real engine behaviors that could not be observed in synthetic unit tests; all three were diagnosed down to exact instructions/structures and fixed permanently upstream:

1. **`.datc64` In-Place `foreignrow[]` Double-Resolution Crash (`Game.patched.exe + 0x129838`):**
   - Cloning row 0 in `Data/Pet.datc64` left `Pet[516].__col7` (offset `36`, `foreignrow[]`) pointing to row 0's variable-section offset `0x3a`. Because PoE 3.29's `.datc64` loader resolves `foreignrow[]` entries **in-place** (`{row_idx, 0}` -> `{row_ptr, table_ptr}`), aliasing `0x3a` caused double-resolution and a NULL `table_ptr` dereference on town entry. Fixed in `MaxwellDatPatcher.cs` (`105ebcf`) by deep-copying `foreignrow[]` arrays into fresh variable-section offsets and setting unique `MtxTypes.__col18` (`ShortId = "MaxwellPet"` at offset `137`). See [`.codexmind/regressions/dat64-foreignrow-aliasing.md`](../.codexmind/regressions/dat64-foreignrow-aliasing.md).
2. **`ui_create_window` Wrapper Frame Clipping & Child Binding Failure:**
   - Creating a `0x0` wrapper `Frame` and calling `Control::Load(frame, path)` caused `float = centre` to center around `x=0` (clipping half the window off the left screen edge) and hid `maxwell_pet_window`'s children one level too deep for `UiService.BindChildrenRecursive`. Fixed in `live_engine_hooks.cpp` (`8a6d9ea`) to call `Control::Load(root, path)` directly on the game UI root (matching `spikes/garden-demo.js:10`) and return the newly appended child `*(new_end - 1)` with `(root, path)` reuse.
3. **UTF-8 Text Overlay Auto-Transcoding for `.ui`/`.ot`/`.ao`/`.act`/`.amd`/`.sm`:**
   - PoE 3.29's wide text parsers throw C++ exception `0xE06D7363` or popup token errors on UTF-8 files lacking a UTF-16LE BOM. Fixed in `overlay_and_channels.cpp` (`8a6d9ea`) to auto-transcode plain UTF-8/ASCII text overlays to UTF-16LE + BOM (`FF FE`) + CRLF consistently across `has_file_overlay` and `resolve_file_overlay*`, while preserving existing UTF-16LE (BOM and BOM-less) files verbatim.

---

## 4. Reproducing the Live Proton Run

```bash
# 1. Build Win64 native shim + managed host bundle
./scripts/build-win64.sh

# 2. Generate PoE 3.29.3d Maxwell 3D mesh/textures/audio + append-only .datc64 tables
./scripts/build-maxwell-assets.sh

# 3. Build DualMod.MaxwellPet (Server + Client)
dotnet build examples/DualMod.MaxwellPet/Server/DualMod.MaxwellPet.Server.csproj -c Release
dotnet build examples/DualMod.MaxwellPet/Client/DualMod.MaxwellPet.Client.csproj -c Release

# 4. Run the fully isolated Xvfb + PipeWire null-sink integration & visual capture harness
./scripts/run-maxwell-proton.sh
```

`scripts/run-maxwell-proton.sh` enforces strict safety invariants on every run:
- Runs exclusively inside an isolated `Xvfb` display (`>= :50`, stripping all Wayland/Hyprland environment variables) and checks `hyprctl clients -j` at five milestones to guarantee **0 window leaks**.
- Launches Path of Exile **only** via `/home/nam/Git/LocalExile/scripts/dev/live.ps1` with server mods staged via `Game_MODS_FOLDER` (never touching `LocalExile/Mods/`).
- Isolates audio to a temporary PipeWire `module-null-sink` (pass `--audible` to route to speakers).
- Snapshots and restores `Client/Game.exe`, `Client/Game.patched.exe` (verified via SHA-256), `Mods/`, and `data/characters.db*` unconditionally in `trap cleanup EXIT INT TERM`.
