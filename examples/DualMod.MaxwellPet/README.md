# Maxwell the Cat Pet MTX (Dual-Side NativeMods Mod)

A complete Waypoint-installable dual-side LocalExile mod that adds **Maxwell the Cat** as a **brand-new Pet MTX** (appending rows to `MtxTypes.datc64`, `MtxTypeGameSpecific.datc64`, and `Pet.datc64` — never replacing any existing stock pet!), complete with a native 3.29.3d pet control panel (`MaxwellPanel.ui`), smooth spinning/floating animation, custom GEAL built-in (`MaxwellDance`), and looping Weebl's *Stock Market* audio (`Audio/Mods/Maxwell/stock_market.ogg`).

> **Repo hygiene:** No stock Path of Exile client files or third-party media (`.glb`, `.opus`, `.ogg`, `.dds`, `.smd`, `.datc64`) are committed to git. Generate the local asset payload on your machine using `scripts/build-maxwell-assets.sh`.

## Appended 3.29.3d Pet MTX Contract

Stock 3.29.3d table row counts (`LocalExile/data/client.db`):

| Table | Stock Rows | Appended Maxwell Row | Key Fields |
|---|---|---|---|
| `Data/MtxTypes.datc64` | `8557` (`0..8556`) | **`8557`** (`Record.Id = 8558`) | `Id = "Metadata/Items/Pets/Mods/MaxwellPet"`, `HASH32 = 0xA1EB592E` (`2716555566`), `Name = "Maxwell the Cat Pet"` |
| `Data/MtxTypeGameSpecific.datc64` | `7849` (`0..7848`) | **`7849`** | `Type = 8557`, `HASH32 = 0xA1EB592E`, `Category = 14`, `__col4 = [15]`, `__col6 = [18, 19]`, `DDSFile = "Art/2DItems/Pets/Mods/Maxwell/MaxwellPet.dds"` |
| `Data/Pet.datc64` | `516` (`0..515`) | **`516`** | `Id = "Metadata/Pet/Mods/Maxwell/MaxwellPet"`, `MtxTypeGameSpecificKey = 7849`, `HASH32 = 0x1F3AB11A` (`523940122`), `HASH16 = 0xAE20` (`44576`) |

`Metadata/Pet/Mods/Maxwell/MaxwellPet.ot` extends `"Metadata/Pet/Pet"` with **no extra serialized components**, so `PetObject.BaseComponents` (`Positioned, Stats, Pathfinding, Buffs, Life, Animated, Actor, PetAi`) matches `S->C 0x02bb` verbatim.

## Components

- **Shared (`Shared/MaxwellMessages.cs`)**: Typed message contracts (`ToggleMaxwellRequest`, `MaxwellStateSync`, `MaxwellAudioControl`, `OpenMaxwellPanel`) and shared protocol/table constants.
- **Server (`Server/MaxwellServerMod.cs` + `Server/MaxwellLocalExileEntry.cs`)**:
  - Uses `PoeContentHash`, `CustomPetMtxDefinition`, and `LocalExilePetMtxRegistrar` from `LocalExile.Client.ServerBridge` to register the brand-new Maxwell Pet MTX entry (`Record.Id = 8558`, `HASH32 = 0xA1EB592E`) into `Game.Mtx` & `Game.MtxEffects`, send `S->C 0x0204` ownership grants, and detect real-time **in-game Cosmetics (`K`) menu** Pet Slot 1/2 (`18`/`19`) equip/unequip transitions with zero changes to LocalExile core.
  - Handles `/maxwell` chat commands AND native Cosmetics (`K`) equip/unequip (which automatically spawns/despawns Maxwell AND starts/stops looping Stock Market audio + updates `MaxwellPanel.ui`!).
  - Syncs typed state & audio control over `LocalExile.Client.ServerBridge` (`HostRuleset 0x02c9/0x02ca` or `ChatFallback ##NM / /nm`), with graceful chat-only fallback for vanilla clients.
- **Client (`Client/MaxwellClientMod.cs`)**:
  - Implements `IClientMod` (loaded in a collectible `AssemblyLoadContext` with clean unload).
  - Registers file overlays, append-only content declarations, custom ruleset `0xA2`, custom opcode `1103`, and GEAL built-in `MaxwellDance`.
  - Loads `Metadata/UI/Mods/Maxwell/MaxwellPanel.ui` (Summon/Dismiss, Spin Speed, Float Height, Dance, and Stock Market Music Play/Mute/Volume controls).
  - Plays and loops `Audio/Mods/Maxwell/stock_market.ogg` via `IAudioService` whenever Maxwell is summoned and unmuted.
  - Drives per-frame spinning yaw and hover height updates on `IGameLifecycle.Frame`.
- **Client UI & Metadata (`client/`)**:
  - `Metadata/UI/Mods/Maxwell/MaxwellPanel.ui` (linted 3.29.3d `.ui` layout using 100% stock registered controls).
  - `Metadata/Pet/Mods/Maxwell/MaxwellPet.ot` (pet object type definition).

## One-Command Live Desktop Launch

Launch the real 3.29.3d Path of Exile client + LocalExile Gateway with NativeMods and Maxwell right on your live desktop (full resolution, real audio, automatic build if missing, and automatic clean teardown when PoE closes or on `Ctrl+C`):

```bash
./scripts/launch-desktop.sh
```

- Opens on your native Hyprland/Wayland/X11 session and default audio sink so you can equip **Maxwell the Cat Pet** via Cosmetics (`K` -> Pet Slot 1), open `/maxwell panel`, and hear Weebl's *Stock Market*.
- By default, `LocalExile/data/characters.db*` is snapshotted and restored on exit so custom Pet MTX slots never leak into vanilla LocalExile saves (pass `--keep-save` to persist changes).
- Useful flags: `--mode import-patch`, `--rebuild`, `--trace-files`, `--localexile-dir <path>`, `--dry-run`.

## Building Maxwell Assets & Installing via Waypoint

1. **Build the native Win64 shim & managed projects:**
   ```bash
   dotnet build NativeMods.slnx -c Release
   ./scripts/build-win64.sh
   ```
2. **Generate Maxwell's 3.29.3d assets & patched `.datc64` tables locally into `dist/maxwell-assets/`:**
   ```bash
   ./scripts/build-maxwell-assets.sh
   ```
   This converts Maxwell's `.glb` + textures and *Stock Market* `.opus` into native 3.29.3d assets (`Data/MtxTypes.datc64`, `Data/MtxTypeGameSpecific.datc64`, `Data/Pet.datc64`, `Art/Models/Mods/Maxwell/*`, `Art/2DItems/Pets/Mods/Maxwell/MaxwellPet.dds`, `Metadata/Pet/Mods/Maxwell/MaxwellPet.ao`, `Audio/Mods/Maxwell/stock_market.ogg`).
3. **Stage and install via Waypoint (`WaypointModInstaller`):**
   - Copy `examples/DualMod.MaxwellPet/` + compiled `maxwell.dll`, `maxwell.Client.dll`, and `dist/maxwell-assets/*` under `client/` into a staging folder.
   - Install with `WaypointModInstaller.Install(stagingDir, localExileRoot)` — deploys server binaries (`Mods/Binaries/maxwell/`, including `MaxwellLocalExileEntry.cs`) and client payload/bundle (`Mods/Client/maxwell/`) without ever mutating stock `Client/Game.exe` or `Bundles2/`.

## In-Game Commands

- `/maxwell` or `/maxwell panel` — Opens `MaxwellPanel.ui` (or prints chat status on vanilla clients).
- `/maxwell summon [1|2]` — Summons Maxwell in Pet Slot 1 (18) or Pet Slot 2 (19) and starts looping *Stock Market* music.
- `/maxwell dismiss` — Dismisses Maxwell and stops music.
- `/maxwell toggle` — Toggles summon state.
- `/maxwell spin <0..720>` — Sets spin speed in degrees per second.
- `/maxwell float <0..60>` — Sets hover height above ground.
- `/maxwell music <on|off|mute|unmute|vol 0..1>` — Controls *Stock Market* audio playback, mute, and volume.
- `/maxwell dance` — Triggers a Maxwell dance boost.
- `/maxwell status` — Prints current pet state in chat.
