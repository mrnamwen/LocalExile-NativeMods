# Path of Exile 3.29 Client Verified Findings

Distilled from `07-spike-results.md`, `08-deep-analysis.md`, `10-verification.md`, `11-client-sdk.md`, `12-ceiling.md`, and `spikes/agent.js`. Where `08` and `10` disagree, **`10-verification.md` is authoritative**.

> **Target builds:** 3.29.3c and 3.29.3d only.
> **Image base:** `0x140000000` (all VAs below are 3.29.3d image VAs; compute `RVA = VA - 0x140000000` because the binary has `DYNAMIC_BASE` / ASLR enabled).
> **Cross-build rule:** Code addresses match between 3.29.3c and 3.29.3d; data addresses (globals, vtables, tables) do **not** transfer automatically and must be verified per build.

---

## 1. Binary & PE32+ Loader Properties

- **3.29.3d reference binary:** Archived Steam `PathOfExileSteam.exe` (build 328228, SHA-256 prefix `e9ba3d31...f53d`).
- **LocalExile patching compatibility:** `Game.patched.exe` differs from `Game.exe` by 644 bytes and leaves the import table completely untouched.
- **PE Header Slack:** 136 bytes of slack after the section table (fits 3 additional 40-byte section headers without growing `SizeOfHeaders`).
- **Import Table Injection:** Move the existing import descriptor array into a new section and append a descriptor for `LocalExile.ClientHost.dll`. Note that an Authenticode signature blob sits at the end of the original file.
- **Proxy DLL Fallback:** `VERSION.dll`, `WINMM.dll`, `dxgi.dll`, `d3d11.dll`, `dbghelp.dll` are statically imported (`d3d12.dll` and `vulkan-1.dll` are dynamically loaded). Under Wine/Proton, `version.dll` proxy loading requires `WINEDLLOVERRIDES=version=n,b`.
- **Anti-Tamper / Integrity:** No runtime self-check exists. `0x1428f31c0` compares only the GGPK root digest against the patch server welcome. `IsDebuggerPresent` is called at 3 non-anti-tamper sites. NVIDIA Streamline checks only its own modules. 20-minute live hooked soak showed zero reactions.

---

## 2. Win64 ABI, Memory Allocator, Exceptions & Threading

- **Calling Convention:** Win64 x64 throughout; statically linked VS2019 CRT. Never pass game-allocated memory to host CRT `free`/`delete`.
- **Game Slab Allocator:**
  - `alloc`: `0x142c8cf60(size, align)` — buffers the game frees must carry a 16-byte header immediately before the returned payload pointer: `{ u64 size; u32 0; u8 log2(align); u16 tag; u8 0 }` (spike writes `{ u32 size; u32 0; u32 0; u32 (tag<<8)|log2(align) }`, e.g., `0x50004` for align 16, tag 0x500; global byte counter at `0x1445d36a8`).
  - `free`: `0x142c8ca30(obj - align)`.
- **Strings:** MSVC `std::wstring::assign(dest, src, len)` at `0x140144010`. Layout: SSO buffer/pointer at `+0`, `size` (`u64`) at `+0x10`, `capacity` (`u64`) at `+0x18` (heap pointer used when `capacity >= 8`).
- **C++ Exceptions:** The game throws MSVC C++ exceptions (`_CxxThrowException` at `0x142ce6a40`, SEH code `0xE06D7363`) as normal control flow (e.g., `.ui` parse errors, missing files, unclaimed packets). Every native-to-game call **must** sit inside an exception wall, and no managed exception may unwind through game stack frames.
- **Frame & Tick Sites:**
  - `0x141528f40`: `StateStack::PassA` (vtable slot 2).
  - `0x141528fbf`: Live-verified frame hook site (expected bytes `48 8b 5d 28 48 83 eb 10`). Skipped while the state stack is empty — queue drain needs a fallback.
  - `0x140d54a00`: Tail of in-game tick (world/entity work; body at `0x140d54c50`).
  - `0x14214f1d0`: Instance tick (network path, instance-only — **do not** use for general frame work).
- **Threading & Locks:**
  - Main game thread owns frame, UI, audio, stats, instance tick, and socket writes.
  - Socket SRW lock: `stream+0x248` (`+0x242` threaded flag, `+0x240` write-in-progress marker).
  - Audio SRW lock: `audio_mgr+0xbb8`.
  - Stats: **no lock** — game thread only.
  - **DeadlockDetector** (`0x140e3ed10`): throws if the game thread stalls past an unrecovered timeout. Boot CoreCLR off the game thread / defer initialization and keep GC pauses short (workstation concurrent GC).

---

## 3. UI System (Live-Verified on 3.29.3d)

| Symbol / Offset | 3.29.3d VA / Offset | Notes |
|---|---|---|
| Control allocator singleton (`pool`) | `0x1418b6d20` | `void* pool()` |
| Frame constructor (`newFrame`) | `0x14014b0e0` | `Frame* create(pool)` |
| Add child (`addChild`) | `0x1418b5b10` | `void add(control+0xb8, size_t index, Control*)` |
| Load `.ui` (`uiLoad`) | `0x1418b08d0` | `void Control::Load(this, const std::wstring& path)` |
| Find child (`findChild`) | `0x1418b6420` | `Control* find(control+0xb8, wstring_view* "a.b.c")` |
| Set visible (`setVisible`) | `0x1418b0a30` | `void Control::SetVisible(this, bool)` |
| Event list lookup (`eventList`) | `0x14019b2b0` | `shared_ptr<List>* get(control+offset, shared_ptr* out, bool create)` |
| Event list grow (`eventGrow`) | `0x140152430` | `void List::grow(this)` |
| Control `id` (`std::wstring`) | `+0x1b8` | Sibling IDs must be unique |
| Control `parent` | `+0x1e0` | `Control*` |
| Control `children` container | `+0xb8` | Pointer array `begin` at `+0xc0`, `end` at `+0xc8` |
| Control `rect` | `+0x110..+0x11c` | 4 floats (1600-high virtual screen units) |
| Control `flags` | `+0x1e8` | `u32`; bit `0x800` tracks visibility |
| Label text interface | `+0x298` | Vtable slot 2 (`+0x10`): `SetText(const std::wstring&)` |
| Button caption interface | `+0x3f8` | Same text interface, slot 2 `SetText` |
| Button clicked event | `+0x2a0` | Handler records `{vtable, context, 0}` |
| TextBox content (`std::wstring`) | `+0x338` | Current text |
| Known Vtables (3.29.3d only) | Frame `0x143033598`, Label `0x1434ae300`, Button `0x1434b0e88`, TextBox `0x1430321a0`, CheckBox `0x1434afe98`, StackLayout `0x143051f80`, Picture `0x1430418b0` | 47 registered UI control types total |

---

## 4. File System & Overlay Routes

Read precedence is strictly: **in-memory index table (`fs+0xc900`) → open GGPK → disk (`<cwd>/<virtualPath>`)**.

- **Route A (Buffer Swap — Live-Verified for Text):**
  - Wide text stream open: `0x1421d5b50(streambuf, &path, keepBom, &path)` (used by `.ui`, `.mat`).
  - Whole-file read variants: `0x140e557f0`, `0x140e55aa0`, `0x140e55f70`, `0x140e564d0`, `0x140e56af0` (consult `fs+0xc900`).
  - File-not-found throw site: `0x1421d2ebe` (inside `0x1421d2bf0`).
  - Note: File bytes are not cached, but parsed materials, meshes, textures, and UI are cached by path.
- **Route B (In-Memory Bundle Index Table at `fs+0xc900`):**
  - Parsed `Bundles2/_.index.bin` (exists in both loose and GGPK installs); 0x130 bytes, 8 hash-sorted shard arrays of 24-byte `{u64 pathHash; u32 index; u32 offset; u32 size}` (Murmur64 of lower-cased narrow path, seed `0x1337b33f`).
  - Lookup: `0x140e4c430`; insert helpers: `0x140e49ec0`, `0x140e4a2c0`, `0x140e4a850`; installer: `0x1410063f0 → 0x140e54a20`; reverse hash→path map at `fs+0xc910` (`0x140e40a90` / `0x140e57490`).
  - Mod bundles need no Oodle compressor: `OodleLZ_Decompress` (`0x142c9d520`) accepts stored blocks prefixed with `CC 06` in `0x40000`-byte chunks. Open bundles stay memory-mapped share-read (updates need a new filename/hash).
- **Dormant Developer Hooks:**
  - Variant tag `std::wstring` at `0x1444a6930` (tries `<stem>.<tag><ext>` before base path).
  - Load-trace callbacks at `0x144599980` / `0x144599988`.
  - Dormant hot-reload watcher (`0x14100d350`), per-extension invalidator (`0x141010560` for `.mat`, `.fxgraph`, `.dds`, `.png`, `.tgm`, `.smd`, `.sm`, `.fmt`, `.bank`, shaders — not `.ui`/`.dat`/`.ot`), reload-all (`0x141011270`).

---

## 5. Networking, Rulesets & Mod Channels

- **Message Pump & Handler Groups:**
  - Pump: `0x1429a77a0(pump, stream)` (reads u16 BE packet ID; dispatcher `0x142150ae0` accepts up to `0x2fb`, so reserved IDs are `>= 0x2fc`).
  - Front-insert handler group: `0x1429a7710(pump, group)` (register after world group `0x14218f630`).
  - Reader: `0x140df9ed0`. Writer: `0x140dfa000`, Flush: `0x140dfa160` (self-locking — never call while holding SRW lock `+0x248`), combined helper `0x1421752b0`.
  - **Fatal rule:** Any unclaimed packet ID or escaped handler exception triggers `RaiseException(0xE06D7363)` (`Unable to deserialise packet with pid N`) and kills the connection.
- **Host Ruleset Channel (Preferred Channel per `10-verification.md` §3):**
  - **S→C `0x2c9`**: consumed by filter `0x1415a6b50` before dispatcher; routes by ruleset ID to vtable slot 8 (`DeserialiseRulesetUpdate`) with `{pointer, length, position}`. Max payload `0xffff` bytes ("Ruleset protocol violation" if exceeded). **Missing ruleset ID is silently dropped** (and missing creator at area entry only logs `Failed to create ruleset N`) — giving graceful degradation for unmodded clients!
  - **C→S `0x2ca`**: written by `0x141fa7c10` (`u16 0x2ca, u8 rulesetId, compact-int size, raw payload`), sender `0x1421a1960`.
  - **Creator array**: writable table at `0x14475dc70` (`0xaf` slots, 68 filled; creation at `0x1415a6800`, stored in vector at `client+0xcfd8`). Base ruleset object is `0x18` bytes (`vtable`, `u32 id` at `+0x08`, `client*` at `+0x10`) with a 9-slot vtable.
- **Chat Fallback Transport (Live-Verified in Spike S4):**
  - S→C `0x17` lines prefixed with `##NM ` intercepted by replacing chat listener `0x14017a270(panel, message)` (`sender` wstring at `+0`, `text` wstring at `+0x20`).
  - C→S `0x15` chat commands written via `0x140dfa000` + `0x140dfa160` under `socket+0x248` (`appGlobal` resolved via RIP-relative pattern at `0x1400f3b30`: `488b05????????488907488b05`, `instance = app+0x348`, `socket = instance+0xc288`).

---

## 6. GEAL Script VM, Content Tables, Audio, Stats & World

- **GEAL VM:**
  - Compiler: `0x141df8210`; interpreter: `0x141df0d70`; external entry points: `0x141df8040` (full script), `0x141df8160` (bytecode offset). Server invocation via S→C `0x285` (u16 folded FNV hash of `Functions` entry name).
  - Built-ins: **724** callable on client (227 client + 499 shared, minus overlap; 497 server-only names fail to compile with `Function X does not exist`). Startup interning at `0x141ece040` (global list `0x1445c81a8`), `{name, factory}` table at `0x1445c50f8` filled by `0x141de9780`.
  - Limits: `While`/`DoWhile` capped at 1,000 iterations; `Repeat(n)` and recursion uncapped; s16 jumps (±32 KB); ≤255 args; ≤256 constants/functions per script.
- **Content Tables & Objects:**
  - Appendable: `BaseItemTypes`, `MonsterVarieties`, `NPCs`, `Chests`, `Projectiles`, `MiscObjects`, `MiscAnimated`, `HideoutDoodads`, `ExecuteGEAL` (skill class table, 167 rows). Append-only (u16 indices cap at 65,535 rows).
  - Closed / Restricted: `ItemClasses` (compiled enum of 103 at `0x142fe0b80`), `ActiveSkills` / `ActionTypes` (2,155 registered C++ action classes via `0x141947300`), `Stats` (appendable but new rows are inert to compiled consumers), 213 components (registrar `0x140e35f40`).
  - Client-only object factory: `0x141ec47e0`; world object map at `world+0x8d8`.
- **Audio:** Holder singleton `0x140ffaa90()` (global `0x14474f870`), manager at `holder+8`, wrapper `0x1400f0ae0`, submit `0x140ff86e0(mgr, req)` under SRW lock `audio_mgr+0xbb8` (supports FMOD Studio event names and `.ogg` paths, ≤0x200 channels).
- **Stats:** Write `0x1424d65c0(stats, key, value)` (synchronous listener notify, display-only, overwritten by next server update), read `0x1424d6510`.

---

## 7. Phase 0 (`0.2.0`) Newly Wired `3.29.3d.json` Symbols (`122` Symbols, `37` Offsets, `18` Constants)

In addition to the baseline table above, `data/addresses/3.29.3d.json` and C ABI `0.2.0` (`address_table.cpp` generated via `scripts/generate-address-table.py`) now include and wire:
- **UI Controls & Geometry:** `TextBox` (`+0x338`, changed slot 129 `0x1894e30`, submitted slot 72 `0x18926f0`), `CheckBox` (`+0x4e2`/`+0x418`, changed slot 115 `0x18f5710`), `ui.control_set_position` (`0x18b1080`), `ui.control_set_size` (`0x1906a30`), `ui.control_get_abs_pos` (`0x18b1250`), `ui.control_detach` (`0x18b5a00`), `ui.children_remove_at`, `ui.event_list_get_ctrl`, `Control::Destroy` (vtable slot 8).
- **Files, Runtime & Network:** `files.hash_to_wstring_view` (`0x0e40af0`), `files.hash_and_register_path`, `files.filesystem_holder_get`, `files.hot_reload_singleton`, `files.hot_reload_invalidate_ext` (`0x1010560`), `runtime.wstring_destroy` (`0x134500`), `runtime.string_destroy`, `network.stream_reader` (`0xdf9ed0`), `network.register_handler_group` (`0x29a7710`).
