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
  - **Appendable (18 live-verified tables):** `BaseItemTypes`, `ItemVisualIdentity`, `CurrencyItems`, `MtxTypes`, `MtxTypeGameSpecific`, `Pet`, `MonsterVarieties`, `NPCs`, `Chests`, `Projectiles`, `MiscObjects`, `MiscAnimated`, `HideoutDoodads`, `ExecuteGEAL` (skill class table, 167 rows), `ClientStrings`, `Mods`, `ModType`, `BuffDefinitions`. Append-only (u16 indices cap at 65,535 rows); `Mods`/`WorldAreas`/`ItemVisualIdentity` need unique `HASH16`, `BaseItemTypes`/`Words` use `HASH32 = MurmurHash2-32(seed=0, UTF-8 Id)`.
  - **Closed / Restricted:**
    - `ItemClasses` (compiled enum of 103 at `0x142fe0b80`), `ActiveSkills` / `ActionTypes` (2,155 registered C++ action classes via `0x141947300`), 213 components (registrar `0x140e35f40`).
    - **`Stats` (`Stats.datc64`) — CLOSED (`23,362` / `0x5b42` fixed rows, stride `0x69` / `105 B`):**
      - `Stats::InitializeStatGraph` (`0x1428eed00`, single xref to `"Stats enum is out of sync..."` at `0x143648bd0`, called from `StatCalc::StatCalc` `0x142502340`) checks exact byte length `(end - begin) - 0x256e12 < 0x69` (`0x256e12 = 23362 * 105`) — row count equality ONLY, not `Id` names.
      - Even if `0x1428eed00` were bypassed, appending row `23362` yields 1-based `StatId 23363 == 0x5b43`, colliding with compiled `StatId::INVALID` (`RowRef<Stats>::ToStatId` `0x142296a20` + 35+ inlined sites) AND causing **unchecked heap OOB writes/reads** across 4 fixed-size tables: `StatDescriptionList::m_byStatId` (`0x14242eaf0` / `0x1424301b0`, `0x5b43` ptrs), `StatValues` (`0x142503e12`, inline `int32_t[0x5b43]` + `uint32_t[731]`), CRT pre-`main` `g_statInfoTable`/`g_statTopoOrder`/`g_statTopoInv` (`0x1400919d0`/`0xe8820`/`0xe8870`), and `StatCalc::m_hasStatBitset` (`0x142502340`/`0x2502d90`/`0x2503f40`, `0x5b43` bits).
      - **Safe custom-stat paths:** (1) In-place non-ID column edits + repurposing **30 verified dead stock slots** (`old_do_not_use_*` / `currently_unused_*`: rows `99, 101..103, 379, 380, 565, 804, 805, 826, 827, 1155, 1156, 1657, 1767, 1913, 1914, 2139, 2174..2177, 2182, 2193, 2254, 2427, 2434, 2622, 2744, 14334` — 0 refs in `Mods.sql`, `LocalExile`, and `Game.exe` virtual stat graph `0x14251a1e0`/`0x14251a310`; **never** reuse row `464` `dummy_stat_display_nothing`, 375 stock refs), keeping stock `Stats[row].Id` intact for `LocalExile` `Stat.Checksum` while writing templates in Route A `Metadata/StatDescriptions/*.txt` (`Stats.dat+0x20` FNV-1a lookup at `0x14222eb40`); (2) unbounded high-ID `StatId >= 0x10000` in `StatsComponent+0xf0` (sorted `vector<pair<int32,int32>>`) & `S→C 0x1b3` (`0x141fe7b70`, compact varint `u32` key `0x14089dc40` + signed varint `i32` value `0x141faa790`).
  - Client-only object factory: `0x141ec47e0` (`ClientEntity` allocated via raw mimalloc `0x248` align `16`, zero `+16` slab header); `EntityManager` sits at **`ClientWorld+0xa18`** (superseding earlier stale `world+0x8d8` note; see §9).
- **Audio:** Holder singleton `0x140ffaa90()` (global `0x14474f870`), manager at `holder+8`, wrapper `0x1400f0ae0`, submit `0x140ff86e0(mgr, req)` under SRW lock `audio_mgr+0xbb8` (supports FMOD Studio event names and `.ogg` paths, ≤0x200 channels).
- **Stats:** Write `0x1424d65c0(stats, key, value)` (`stats.set_local`, synchronous listener notify, display-only, overwritten by next server update), read `0x1424d6510` (`stats.read`). Both operate on unbounded `StatsComponent+0xf0` (no `0x5b43` cap).

---

## 7. Phase 0 (`0.2.0`) Newly Wired `3.29.3d.json` Symbols (`122` Symbols, `37` Offsets, `18` Constants)

In addition to the baseline table above, `data/addresses/3.29.3d.json` and C ABI `0.2.0` (`address_table.cpp` generated via `scripts/generate-address-table.py`) now include and wire:
- **UI Controls & Geometry:** `TextBox` (`ui.vtable_textbox` `0x30321a0`, content `+0x338`, text_interface `+0x298`, changed slot 129 `0x1894e30`, submitted slot 72 `0x18926f0`), `CheckBox` (`ui.vtable_checkbox` `0x34afe98`, checked `+0x4e2`, event_key `+0x418`, changed slot 115 `0x18f5710`), `Control::SetEnabled` (`0x18b0c70` / `[control+0x1e8]` bit 13 `0x2000` enabled), `Control::ReleaseOrDestroy` (`0x18b6810` slab-pool teardown when `[control+0x08] != 0`), `ui.control_set_position` (`0x18b1080`), `ui.control_set_size` (`0x1906a30`), `ui.control_get_abs_pos` (`0x18b1250`), `ui.control_detach` (`0x18b5a00`), `ui.children_remove_at`, `ui.event_list_get_ctrl`, `Control::Destroy` (vtable slot 8, heap-only fallback).
- **Files, Runtime & Network:** `files.hash_to_wstring_view` (`0x0e40af0`), `files.hash_and_register_path`, `files.filesystem_holder_get`, `files.hot_reload_singleton`, `files.hot_reload_invalidate_ext` (`0x1010560`), `runtime.wstring_destroy` (`0x134500`), `runtime.string_destroy`, `network.stream_reader` (`0xdf9ed0`), `network.register_handler_group` (`0x29a7710`).

---

## 8. Phase 1 (`0.3.0`) Verified `3.29.3d` Symbols, Vtables & Runtime Struct Findings (`240` Symbols, `160` Offsets, `18` Constants)

All Phase 1 code, data, and vtable symbols (from RE streams `#9`/`#10`/`#11`, independently verified in `#12` against `3.29.3d` `Game.exe` SHA-256 `e9ba3d31...`) are integrated into `data/addresses/3.29.3d.json` (`240/240` verified, `170/170` non-null `expectedBytes`, all `kind: "vtable"` entries `expectedBytes: null` for ASLR safety) and mirrored in `3.29.3c.json` (`87` verified/shared, `153` `null` queued for Phase 5):

- **StateStack & Pre-Game Screens (`CreateCharacterState` / `LeagueOverlay`):**
  - `StateStack::Get` (`0x1400f2890`), `frame.state_stack_global` (`0x45d6fc0`), vtables (`frame.vtable_state_stack` `0x30275d8`, `frame.vtable_ingame_state` `0x31f66b0`, `frame.vtable_pregame_state` `0x31f31c0`). `StateStack::Push` (`0x1415284c0`) confirms **`+0x08/+0x10`** is the immediately-updated authoritative active stack, whereas `+0x20/+0x28` is the deferred `PassA` snapshot — always read `+0x08/+0x10` for instant screen queries.
  - `CreateCharacterState`: Enter `0x140cf4ad0` (loads `Metadata/UI/PreGameState/CreateCharacterScreen.ui` into `S+0x218`, binds `create_character_frame` `+0x248`, `name_textbox` `+0x250`, `league_label` `+0x270`, `select_league_button` `+0x298`, `create_button` `+0x2a8`, lazy `LeagueOverlay` `+0x2b8`), Leave `0x140cf57c0`.
  - Send create `0x140cf5e60` → `LoginClient::WriteCreateCharacter_0x12` (`0x142146de0`, 6-arg Win64 ABI: `args[5]` at `[entry_rsp+0x30]`, non-owning `WStringView*` copied via `0x140130410` and freed via `0x140134500`, wire `C→S 0x12`: `u16 0x0012`, `wstr name`, `wstr league`, `u32 0`, `u32 0`, `wstr start_state`, `bytes[32] password_sha256`). Result handler `S→C 0x13` at `0x140cf7fb0(S, u16 result)`.
  - `LeagueOverlay`: 100% C++-constructed (size `0x3d8`, ctor `0x140ccc160`, `cards_flow` `+0x3c0`); `LeagueOverlay::Fill` (`0x140cccff0`) clears and reconstructs all `0xdc35` (`u16` tag at `+0x1d8`) league cards (`500 × 905`, ctor `0x140cc9370`) on every open/refresh, building HC (`+0x2d8`), SSF (`+0x2e0`), and Ruthless (`+0x2e8`) checkboxes via `0x140ccb3f0` (`CheckBox::CreateChild` `0x4e8`, `StackLayout::Create` `0x2b8`, `SetWidth`/`SetHeight` vtable slots 17/18, float constants `74.0f` `0x1436ae774` / `48.0f` `0x1436ae688`).
- **In-Game Panels, Stash Tabs & Control Geometry:**
  - `ui.vtable_pc_hud` (`0x31fbf20`, slots `[354]/[365]`), `InGameState::GetInGameUI` (`[rcx+0x978]`), `InGameState::Leave` (`0x140d544da` clears `+0x978` + `UIManager::Reset`), `InGameState::CreateGameUI` (`0x1445998ce` + `+0x5a0`).
  - `InGameUI::Init` (`0x140dad910..0x140db5f82`) exact panel stores verified at `+0x628`, `+0x630`, `+0x638`, `+0x640`, `+0x650`, `+0x668`, `+0x6e0`, `+0x768`, `+0x790`, `+0x7b8`, `+0x7c0`, `+0x7c8`; `Control::IsVisible` checks `+0x1e8 >> 11 & 1` + `+0x148`.
  - `InstanceClient::GetStashTabs` (`+0xc498/+0xc4b0/+0xc4c8`); `MapStashPage+0x550` holds `optional<variant<RowRef0,1,2>>` (`MapKeyTier1..17`, `-1` for empty/special).
  - **Control Rect Clarification:** `UI::Control + 0x110..0x12f` is an SBO buffer (not screen rect); use `ui.control_get_abs_pos` (`0x18b1250`) + `+0x268/+0x26c` (`width`/`height`) for exact screen rectangles.
- **Input Interception & Hovered/Cursor Items:**
  - `ui.wndproc_stub` (RIP-rel load of `0x1445d8890`, cleanly relocatable) + keyboard/mouse dispatch hooks (`>=14B` prologues verified).
  - `TextBox::AsTextInput` (`0x14015f2b0`, returns `this+0x6b8`) at vtable slot 6 on `TextBox` + subclasses vs `0x1400ecff0` (`nullptr`) on non-text controls — exact ground truth for `IInputService.IsTextInputFocused`.
  - `PC_HUD::GetCursor` (`[rcx+0x488]`), `Console_HUD::GetCursor` (`[rcx+0x7e8]`), `Cursor::GetHeldItem` (`rcx = Cursor**`, `[cursor+0x4e8]`, slot 12 `+0x30`, `InventorySet::Lookup` on `inst+0xc890`), `ModsComponent::EnsureDeserialised` (`+0x2e2` guard), and `BaseStatic+0x68` inline `RowRef<BaseItemTypes>` (`BaseItemTypesRow+0x00` `Id` & `+0x08` `RowRef<ItemClasses>` passed to `0x1423bf520`).

---

## 9. Phase 2 (`0.4.0`) Verified `3.29.3d` World, EntityManager, Components, Stats, Filter & Camera Findings (`319` Symbols, `300` Offsets, `18` Constants)

All Phase 2 code, data, and vtable symbols (`79` new symbols: `78` from `#63`/`#64` + `items.filter_style_dtor_custom_sounds` `0x134470`) are integrated into `data/addresses/3.29.3d.json` (`319/319` verified, `238` non-null `expectedBytes`, all vtables `expectedBytes: null` for ASLR safety, `0` RVA overlaps) and mirrored in `3.29.3c.json` (`87` verified/shared, `232` `null` queued for Phase 5):

- **ClientWorld, EntityManager (`+0xa18`) & Area Lifecycle:**
  - **Corrected `world.object_map` Offset (`0x8d8` → `0xa18`):** `ClientWorld` (`vtable 0x347a100`) vtable[49/50] (`0x1416502c0`) returns `rcx + 0xa18` (`EntityManager`, `vtable 0x35c13b0`), while vtable[51/52] (`0x1416502d0`) returns `rcx + 0x8e0` (`AreaGen` / `area_seed`). Key `ClientWorld` fields: `area_row +0xb0`, `area_level +0xd4`, `tile_hash +0x114`, `area_seed +0x8e0`, `instance_client +0x968`, `local_player +0x970`, `entity_manager +0xa18`.
  - **`EntityManager` & `ClientEntity`:** Dual red-black trees at `+0x10` (`awake_map`) and `+0x20` (`asleep_map`); `EntityManager::FindEntityById` (`0x1ec5880`, Win64 leaf), `Insert` (`0x1ec6010`), `Remove` (`0x1ec6a70`), `AllocId` (`0x1ec7af0`), `world.create_client_object` (`0x1ec47e0`), `Entity::PostInit` (`0x1ec4cd0`), `Entity::MarkForRemoval` (`0x1ec4f30`). `ClientEntity` (`vtable 0x3460bc0`, size `0x248`) destructor (`0x141558a50`) calls raw `mi_free`, so `world_create_client_object` must allocate via raw mimalloc `allocator.alloc(0x248, 16)` (no `+16` slab header). `Entity` fields: `object_type_slot +0x08`, `components_begin/end +0x10/+0x18`, `world +0x78`, `id +0x88`, `flags +0x8c`, `positioned +0x98`.
  - **Area Lifecycle & `WorldAreasRow`:** `ClientWorld::InitArea` (`0x1653ee0`), `BaseWorld::InitArea` (`0x2189aa0`), `AreaLoadingState::Enter`/`Leave` (`0x152b060`/`0x152b710`, `vtable 0x345f5d8`), `InGameState::Leave` (`0xd544d0`). `WorldAreasRow`: `id +0x00`, `name +0x08`, `act +0x10`, `is_town +0x14`, `area_level +0x26`, `hash16 +0x2a`, `is_guild_hideout +0x139`, `is_hideout +0x14e`.
- **14 Verified Component Accessors & StatsComponent:**
  - `components.map_lookup` (`0x157210`) + all 14 accessors verified by `.rdata` literal: `Positioned` (`0x20f350`: `grid_z/x/y +0x290/+0x294/+0x298`, `world_z/x/y +0x2b4/+0x2b8/+0x2bc`, `GetWorldZ 0x1e38b40`), `Render` (`0x13fdc0`: `world_pos +0x120`, `name +0x148`), `WorldItem` (`0x141a20`), `Targetable` (`0x1420f0`: `+0x50/+0x52/+0x5a`), `Life` (`0x13e0d0`: `max_hp +0x1a4`, `cur_hp +0x1a8`, `mana +0x1f4/+0x1f8`, `es +0x23c/+0x240`), `ObjectMagicProperties` (`0x13dcb0`: `rarity +0x144`), `Chest` (`0x149cd0`: `is_opened/destroyed/locked/strongbox +0x168..+0x16b`), `Stats` (`0x13e140`), `Player` (`0x13f510`), `NPC` (`0x13dd90`), `Portal` (`0x218fbf0`), `AreaTransition` (`0x13de00`), `MinimapIcon` (`0x16751b0`), `Transitionable` (`0x149c60`).
  - **`StatsComponent`:** `parent_fallback +0xe0`, sorted `vector<pair<int32,int32>>` at `+0xf0/+0xf8/+0x100`; `stats.read` (`0x24d6510`), `stats.set_local` (`0x24d65c0`), `stats.notify_listeners` (`0x24d67d0`). Unbounded `int32` stat keys (supports high-ID custom stats `>= 0x10000`).
- **Client Item Filter (`Options`, `WorldItem`, `ItemFilter`):**
  - `Options::Instance` (`0x190abe0`, `g_options 0x475e9f0`, `vtable 0x34b50f8`): `filter_kind +0x510`, `active_filter_ptr +0x518`, `active_filter_ctrl +0x520`, `reload_pending +0x542`, `reload_show_msg +0x543`, `filter_valid +0x545`, `filter_mgr +0x548`. Setting `+0x542`/`+0x543` triggers `Options::Tick` → `ItemFilterManager::SelectApply` (`0x1930b80`) → `WorkerLoad` (`0x19369e0`) → `PollLoad` (`0x19331e0`) → `NotifyLoaded` (`0x1935130`). Folder resolution (`0x190d390`) always points to `Documents\My Games\Path of Exile`.
  - `WorldItem` (`vtable 0x34700a0`): `item_entity +0x28`, `allocation_id +0x40`, `dropped_fresh +0x50`, `never_hidden +0x54`, cached `ItemFilterStyle +0x70`, `filter_style_valid +0xb0`; `WorldItem::IsFilterVisible` (`0x15e5ed0`), `WorldItem::EvalFilterStyle` (`0x15e5f10`).
  - `ItemFilter`: `Parse` (`0xe13a00`), `LoadFile` (`0xe130d0`), `Match` (`0xe15970`), `DefaultStyle` (`0xe158b0`), `MergeStyle` (`0xe12f70`), `custom_sounds` dtor (`0x134470`). `ItemFilterStyle`: `flags +0x00`, `text/border/bg RGBA +0x03/+0x07/+0x0b`, `sound_id/vol +0x10/+0x14`, `font_size +0x18`, `minimap_icon +0x1c`, `play_effect +0x20`, `custom_sounds +0x28`.
- **CameraController & FollowCamera:**
  - `InGameState+0x270` holds `CameraController` (`ctor 0x10f9c0`, `update 0x1103b0`, `mouse_wheel 0x117c30`): `world +0xa0`, `camera +0xa8`, `viewport_w/h +0x498/+0x49c`, `zoom_current +0x4a8`, **`zoom_override +0x4ac` (`-1.0f` = stock)**, `zoom_target +0x4b0` (`1.0f` stock).
  - `FollowCamera` (`vtable 0x3221068`, `base vtable 0x3220fe0`): row-major DirectX `view_proj +0x100` (`v_row * M`, confirmed via `Mat4x4::MulRowVec4` `0xf00a0` / `0x1400f0250`), `tan_half_fov +0x268`, `viewport_w/h +0x270/+0x274`, `eye_pos +0x378`, `look_at +0x384`. `Camera::WorldToScreen` (`0xfd3b40`) reads `ui.viewport_offset_x` (`0x45998f0`) / `+4` as **`int32_t`** pixels.
