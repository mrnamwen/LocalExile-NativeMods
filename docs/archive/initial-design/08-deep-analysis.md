# Deep analysis: how far the client can be extended

A second, deeper static pass over the 3.29.3d client, after the spikes ([01](01-findings.md), [07](07-spike-results.md)).
Its purpose: answer, from code rather than hope, every question that decides whether mods can put **native code in
the client** and ship **new content** — the scope the maintainers have since chosen. The design that follows from it
is [09-native-mods.md](09-native-mods.md); this file is the evidence.

> **Corrected by [10-verification.md](10-verification.md).** A third pass re-derived these claims and refuted
> several: route C (§1), the ruleset wire id, table and vtable (§3), the built-in count and `ExecuteGEAL` (§4),
> parts of §5 and §7. Sections below carry a marker where 10 overrides them; where they disagree, 10 wins.

How to read: **[V]** verified statically (Ghidra headless over a persistent analysed project, `objdump`, PE xrefs;
the method is in [§9](#9-how-these-were-found)). **[L]** live-verified earlier. **[D]** stated in LocalExile's docs.
**[I]** inference. **[?]** open. Addresses are 3.29.3d image VAs; code addresses hold for 3.29.3c, data addresses do
not.

## 1. Files: three overlay routes, and the mystery table is the game's own

The whole-file read `0x140e564d0(fileSystem, out, pathHash)` — the one the spikes already swapped buffers on —
consults an optional table at `fileSystem+0xc900` **before** the GGPK/bundle index. That table is now identified
[**V**]:

- It is the game's in-memory parse of **`Bundles2/_.index.bin`**: path → `{bundle file, offset, size}`. Installed at
  startup by `0x1410063f0 → 0x140e54a20` (logs `[BUNDLE] Index not found, bundles will be disabled` when absent —
  a Steam-less install simply runs without it).
- Layout: 0x130 bytes; 8 sorted shard arrays of 24-byte `{u64 pathHash; u32 index; u32 offset; u32 size}` (hash =
  murmur64 of the lower-cased narrow path, **seed `0x1337b33f`**), plus a 0xb0-byte entry vector at `+0xc0`
  (`char* bundleName` at `+0`). Lookup `0x140e4c430` returns `Bundles2/<name>.bundle.bin` + offset + size; the
  reader passes those straight into the bundle slice, per-0x40000-block Oodle decode (`0x142c9d520`) [V].
- **Nothing validates the table's origin.** The game's own installer is the protocol: allocate 0x130 bytes, fill
  it, store the pointer. A host can synthesize a table, extend the game's with its own insert helpers
  (`0x140e49ec0`, `0x140e4a2c0`, `0x140e4a850`), or — on a Steam loose install — edit the on-disk
  `_.index.bin` with no code at all. Constraints: shards stay hash-sorted; the entry must name a real
  `Bundles2/<name>.bundle.bin` on disk; the slice must decode as bundle blocks (Oodle, or a stored/uncompressed
  block) [V].
- **Two dormant hooks the game itself honours** [V]: a variant-tag `std::wstring` at `0x1444a6930` — when set,
  every read/exists first tries `<stem>.<tag><ext>` (never assigned by any game code; GGG's own dev channel), and a
  pair of load-trace function pointers `0x144599980/0x144599988` called before/after every text load, with no
  writer anywhere in `.text`. Both are overlay/lint machinery the host gets for free.

So the file overlay has three routes, all evidence-backed:

| Route | Mechanism | Needs a code hook | Formats | Hot reload |
|---|---|---|---|---|
| A. buffer swap | replace the result of `0x140e564d0` (the spike method, generalised) | yes (one return-path detour) | all (post-decode) | per read |
| B. index table | entries in the `+0xc900` table → mod bundle file on disk | no (data-structure edit, or on-disk index edit) | all (must decode as bundle blocks) | restart or table rebuild |
| C. loose files | **refuted ([10](10-verification.md) §2):** the index always wins; disk serves only paths the index lacks, at `<cwd>/<path>`, and not `.ot`/`.ao`/`.it`/`.header` | no | new paths only | per read |

Coverage [V]: all five read variants (`0x140e557f0/0x140e55aa0/0x140e55f70/0x140e564d0/0x140e56af0`) consult the
table, and their callers include the text stream open `0x1421d5b50` (`.ui`, `.mat`), the `<file>.header` loader
`0x141148850` (`.dds.header`), the object/animated loader `0x141148690`, `data/mat_table.dat` `0x1410f8190`, the
renderer-side loader `0x140fa50b0`, a wide-text-or-throw reader `0x142a26540`, and a string reader with a built-in
`'*'`-content redirect `0x141017e30`. Sound banks resolve on the same layer (`0x1417d19f0` reads the table;
`FMOD::Studio::System::loadBankCustom` is imported) [V].

Caching [V]: file content is never cached — the buffer moves to the caller on every read. Only a hash→path reverse
map (`fs+0xc910`, insert-only: new paths must be registered there, `0x140e40a90/0x140e57490`) and an LRU of
bundle-file handles persist. **Live `.ui` reload (S3c) is feasible; nothing needs invalidating.**

The rest of the file layer [V]: `0x1400fc3f0` opens `Content.ggpk` unless the distribution mode says loose (mode
from `0x140df9010`: module name, `--steam`, the INI); on a GGPK install route B means synthesizing a table or
hooking route A, on Steam either works. The only hash compare anywhere in the read chain is `0x1428f31c0`: a
memcmp of the GGPK root digest against the patch server's welcome. No content digests are checked at read time.

## 2. The message pump: the reserved-id channel is a first-class object, not a hook

The pump `0x1429a77a0(pump, stream)` reads a u16 BE id (a per-pump byte at `pump+0` can switch the whole pump to
1-byte ids; the instance pump is u16 [V]) and offers the message to registered handler groups, front-inserted by
`0x1429a7710`. Three facts settle the mod channel [**V**]:

1. **An id no group accepts is fatal.** The pump formats `Unable to deserialise packet with pid N` and throws
   (`0x140df95a0` + `0x142ce6a40` = `RaiseException(0xE06D7363)`); nothing catches it on this path; the
   connection dies. The 01-findings [?] is closed in the sharpest way: there is no silent-desync path — a
   server-sent reserved id either is claimed and consumed exactly, or it kills the connection. The envelope
   being self-delimiting (`u16 channel, u32 length, payload`) is therefore mandatory, as [02](02-architecture.md)
   §4 guessed.
2. **A host group needs no code hook.** Registration `0x1429a7710(pump, group)` is pure bookkeeping; the pump calls
   the group's vtable. Recipe: a small object whose vtable slot 1 is `host_handle(group, id, stream)` — decline
   ids that are not ours, read the envelope with the client's own reader `0x140df9ed0` (each read advances the
   cursor exactly), return 1. Register on the game thread after `0x14218f630` (world group), once per area
   connection; front insertion puts the host first, ahead of world and instance. The dispatcher-hook plan in
   02-architecture §4 is retired — for an unclaimed id the dispatcher is never reached.
3. **The write side is byte-for-byte the client's own sequence**: acquire the stream SRW lock at `stream+0x248`
   (`+0x242` is the threaded flag, `+0x240` the write-in-progress marker), write `0x140dfa000`, release, flush
   `0x140dfa160` (self-locking — never call it holding the lock). Or simply call `0x1421752b0`, which does all of
   it [V].

The dispatcher itself `0x142150ae0`: early null-message and owner-filter passes, then `id−0x0d > 0x2ee → default`
— ids above 0x2fb are destroyed there, but that case is unreachable in practice because the pump throws first
(unless a group claims the id, which is the plan) [V].

## 3. Rulesets: dat-driven ids, one wire id (0x2ca), compiled-in implementations

> **Mostly wrong in detail; see [10](10-verification.md) §3.** S→C is `0x2c9` (`0x2ca` is C→S only); the creator
> table is the writable array at `0x14475dc70` with 68 entries, not the range below; the vtable has 9 slots;
> traffic for an absent id is dropped silently.

- **Creation**: each implemented ruleset has a creator thunk in a function-pointer table (Harvest's is the entry at
  `0x144831ab8` → `0x141764b60` = alloc 0x188 + ctor `0x14168d750`); the ruleset id space is the rows of the
  **`Rulesets` dat table** (RTTI `RulesetsValues`, `GenerateGameWorld(…, vector<Rulesets>)`) — ids are data, the
  33 `Game::Rulesets::Client*` implementations are code [V]. Rulesets are created during area generation (the
  Harvest ctor already reads the world's string table for `harvest_grid_anchor`); objects can add rulesets per
  their `.ot` key `rulesets_added` [V, string evidence].
- **Wire**: one message id, **0x2ca**, both directions. The writer `0x141fa7c10` writes `u16 0x2ca, u8 ruleset id
  (from ruleset+0x08; Harvest 0x69), compact-int size, raw payload`; ~250 send sites funnel through the sender
  `0x1421a1960(ruleset, event)` [V]. S→C 0x2ca is in the instance factory's accepted set; per-ruleset payloads are
  read by the protected virtual `DeserialiseRulesetUpdate(BufferStreamIn&)`; the failure string is
  "Ruleset protocol violation." [V]. The exact 3.29.3d dispatcher case for 0x2ca needs the byte table dumped
  (3.29.3c's case numbers do not transfer) [?].
- **Driving league UI**: for an id that exists, an S→C ruleset event reaches a ruleset object whose virtuals and
  UI listeners open windows — live-proven once on the real server (`DispatchRulesetEvent(BanditCallVote)` opens
  the vote panel) [D, L]. A ruleset id with no compiled implementation cannot be created and its traffic hits the
  protocol-violation path [I]. The ruleset vtable's 24 slots are mostly unnamed [?]; `FrameMove(float)` and
  `DeserialiseRulesetUpdate` are the two observed virtuals [V].

## 4. GEAL: a full script VM with 1,221 built-ins, and no sandbox watchdog

> **Overstated; see [10](10-verification.md) §4.** 724 of the 1,221 names exist on the client; the other 497 are
> server-only and do not compile. `ExecuteGEAL` is a skill class, not a run-text entry. `While` is capped at
> 1,000 iterations.

- **The client compiles and runs the scripts** of `.ot`/`.ao`/`.it` files: compiler at `0x141df8210` (statements),
  `0x141df8780/0x141df91a0/0x141df9680/0x141dfa050/0x141dfa8c0/0x141dea480` (assignment, local, if, expressions,
  operators, lambdas; precedence table `0x142fc8558`), interpreter `0x141df0d70` [V]. Compilation is lazy; the
  compiled form is a u8 bytecode vector plus u8-indexed tables (constants, a FuncWrapper function table, strings)
  [V].
- **Entry points**: exactly two external — `0x141df8040` (run a whole script; re-entrancy-safe) and `0x141df8160`
  (run at a bytecode offset — the path a `Functions` entry takes) [V]. The server-reachable route is S→C 0x285:
  object header, u16 entry id (a name hash; live-proven by the zoom mod's `camera_zoom` call), u8 argument mask,
  per-argument count/type/value [D, L]. A mod that ships `Functions { my_entry = { … } }` in an `.ot` gets a
  server-callable client function with no new machinery [I].
- **The built-in surface is now measured, not estimated: 1,231 registrations, 1,221 unique names** —
  [samples/geal-builtins.txt](../../../samples/geal-builtins.txt). They are registered at startup by `0x141ece040` (name
  interning into the global list at `0x1445c81a8`) and `0x14006a5f0` (functor binding), so a host can enumerate
  them at runtime by walking that list [V]. Coverage: control flow (`If`, `While`, `ForEach`, `IfState`),
  math/logic (`ATan2`, `Xor`), object control (`Transition`, `PlayAnimation`, `DisableRendering`,
  `WarpToLeagueMechanic`), effects (`PlayEffect`), audio (`PlayNPCAudio`), camera nodes, timers, randomness.
- **Object events**: 21 `on_xxx` event kinds recovered with their dispatch sites (`on_enter`/`on_exit`,
  `on_state*`/`on_timer`, `on_transition_to`, `on_trigger`, `on_gain_`/`on_lose_`, `on_life_*`,
  `on_animation_end`, `on_destroy`, `on_event`, …) [V] — the client-side behaviour hooks a mod's `.ot` can use.
- **Sandbox**: capability-closed — all opcodes read type-tagged values, bounds-checked arrays, name-hashed members;
  no raw-memory, file or network opcodes exist [V]. But there is **no watchdog**: no instruction budget, no
  recursion cap (only an external stop flag). A hostile `.ot` can hard-hang, stack-overflow or OOM the client;
  the worst case is a denial of service, not code execution [V]. The mitigations that follow for server-pushed
  payloads are in [09](09-native-mods.md) §6: lint, compile-time limits (the compiler's own constant/lambda/call
  caps), and a host-side step budget around interpreter entry.
- Limits [V]: s16 jumps (±32 KB per block), ≤255 arguments per call (u8), ≤256 functions per script (u8 index,
  with a 2-byte form), ≤256-string constant pool; locals, recursion and the value stack are unbounded.

## 5. Objects, `.ot`, dat rows, items: the content rulebook

> **Qualified by [10](10-verification.md) §5:** ItemClasses and skill action classes are closed, new stats are
> inert, an unknown object type throws rather than logs.

- **The object-type register is a generated hash map built from 8 dat tables** (MonsterVarieties, Chests, NPCs,
  Projectiles, Characters, Pet + two unidentified ones), filing every row under `MurmurHash2` of its narrow path
  [V]. A S→C 0x2bb whose hash is not in the register logs `Unknown object type serialized by server` and creates
  nothing [D]. **A server-created new object type therefore needs a dat row** — NPCs/Chests/Pet/Projectiles being
  the easy carriers — plus an optional `.ot` overlay for its components; without one, the register's base
  generates a synthetic `extends` type [V, D].
- **Dat rows can be appended**: rows are deserialized fixed-stride structs in one contiguous vector; counts derive
  from the loaded data; lookups bounds-check dynamically; no compiled-in row counts exist in these paths [V].
  Append-only (positional row references exist in packets and cross-table keys; u16 indices cap tables at 65,535
  rows; HASH16 references must not collide) [V, D]. This answers spike S7's desk half: the risk left is
  per-table behaviour, not the loader.
- **Client-side creation is a first-class channel**: `0x141ec47e0(out, type, world, …)` builds the same object as
  the 0x2bb path but takes its id from the world and reads no blob — the predicted-projectile factory. A host can
  create **arbitrary client-only objects of any path-loadable type, no dat row**, invisible to the server [V].
- **The `.ot` grammar is fully recovered** [V]: UTF-16 text, `version = 2|3`, optional `abstract`, repeatable
  `extends` (multiple parents), `Component { key = value; }` sections (override-in-place), `server { }`/`client {
  }` blocks (two passes; `client { }` components land after the blob-deserialized set), `on_xxx = { … }` kept as
  GEAL source spans for compilation, dependency validation against the 213-component table.
- **Effects on demand**: `PlayEffect` takes a direct path with art suffixes or a `MiscAnimated.dat` row name; the
  effect object type is the fixed `Metadata/Effects/*Effect` set; creation is a queued task. Any shipped effect is
  playable from any GEAL or 0x285 call; new ones need a MiscAnimated row append or the suffixed direct form [V].
- **New item bases work**: `Metadata/ItemTypeRegister` is the same hash map over BaseItemTypes HASH32; `.it` files
  merge like `.ot`; leaf rows usually ride their `InheritsFrom` chain. A new BaseItemTypes row is usable
  end-to-end [V, D].

## 6. Frame, threads, ABI, audio, stats

- **The frame point is understood**: `0x141528f40` is the state-stack singleton's vtable slot 2
  (`StateStack::PassA`); `0x141528fbf` — the point the spikes and `client-control.js` use — is the top of its
  weak-ref sweep, after the frame guard, before any state's pass. Once per frame, game thread [V]. Trampolines:
  UI work there; world/entity work at the tail of the in-game tick `0x140d54a00`; **never** the instance tick
  `0x14214f1d0` (network path, instance-only) [V].
- **Threading contract**: the game thread owns frame, UI, audio, stats, the instance tick and socket writes; a
  short-lived connect-job thread is joined by the tick; Job2 job-graph workers and FMOD threads exist. Locks:
  socket SRW at `stream+0x248`, audio SRW at `audio_mgr+0xbb8`, interlocked refcounts, **no locks on stats**.
  A hosted runtime touches game state on the game thread only, never blocks the frame, and writes the socket
  only under `+0x248` [V].
- **Audio by name**: `0x140ffaa90()` → holder (global `0x14474f870`), manager at `holder+8`; build the name
  (`0x140144010`), wrap (`0x1400f0ae0`), submit `0x140ff86e0(mgr, req)` under the audio SRW. The client's own
  item-use chain plays both **FMOD Studio event names** and **.ogg paths** this way; ≤0x200 live channels [V].
- **Stats**: `0x1424d65c0(stats, key, value)` inserts/replaces and synchronously notifies that key's listeners;
  `0x1424d6510` reads with upstream fallback. No validation, no lock, invisible to the server; the next network
  write of the key overwrites. Stat-gated client UI (the Backpack pattern) generalises to any stat id,
  display-only [V].
- **ABI for a hosted runtime** [V]: Win64 x64 throughout. Allocation is the game's own per-thread slab allocator
  `0x142c8cf60(size, align)`; buffers the game frees carry the confirmed 16-byte header `{u64 size; u32 0; u8
  log2(align); u16 tag; u8 0}` (global byte counter `0x1445d36a8`); free is `0x142c8ca30(obj − align)`.
  `0x140144010` is MSVC `std::wstring::assign(dest, src, len)`. The CRT is **statically linked VS2019** — never
  CRT-free game memory; C++ exceptions must be caught around every game call (the spikes' lesson, now grounded:
  MSVC `RaiseException(0xE06D7363)`).

## 7. Integrity and the loader: nothing in the way

- No runtime self-check exists: the patch handshake (`0x1428f31c0`) is the only digest compare; the
  "integrity" strings are LMDB/curl/patcher-dialog artifacts; `IsDebuggerPresent` is imported but never called;
  NVIDIA Streamline signature-checks only its own modules [V]. The empirical soak (20 min, hooked, no reaction)
  stands [L]; a long soak is still owed.
- **S1's desk half is done** [V]: `Game.patched.exe` differs from `Game.exe` in 644 bytes and their **import
  tables are identical** — the patch step has never touched imports, so adding one is a clean, isolated change.
  The PE has 136 bytes of slack after the section table (three more section headers fit); a new import directory
  in a new section fits without growing the headers. `VERSION.dll`, `WINMM.dll`, `dxgi.dll`, `d3d11.dll`,
  `dbghelp.dll` are imported (proxy-DLL fallback candidates); `d3d12.dll` and `vulkan-1.dll` are loaded
  dynamically — all three renderers ship, and a swap-chain overlay remains possible on each (the UI bridge makes
  it unnecessary).
- ~~LocalExile and Waypoint target **net10.0**, so every user machine already has the .NET 10 runtime the host will
  load.~~ **Refuted ([10](10-verification.md) §6):** not inside the Proton prefix, and not with self-contained
  releases.

## 8. What this enables (read this before 09)

1. **The file overlay can be hook-free** (routes B/C) or one trivial hook (route A) — and the game ships dormant
   hook slots it already honours (the variant tag, the load-trace pointers).
2. **The mod channel is an object registration, not a patch** — but it is mandatory: an unclaimed reserved id kills
   the connection, so the host's group is load-bearing from day one.
3. **GEAL is the content language**: 1,221 built-ins, 21 event kinds, server-callable entries via 0x285. Most
   league-mechanic *behaviour* — state machines, transitions, effects, animations, sounds, camera, randomness —
   is authorable as `.ot` text with no native code, sandboxed to a closed opcode set. The missing ceiling from
   06-roadmap ("the removed C++ does not come back") moves: what GEAL cannot express, the native tier (09) can.
4. **Content is append-friendly**: dat rows append cleanly; new item bases need one row; new server-created object
   types need one row in a carrier table; new client-only objects and effects need no row at all.
5. **Rulesets are half-open**: ids are data (rows), implementations are compiled in; a mod can drive any existing
   league's UI through 0x2ca, and a *new* league's mechanics belong to the server plus the client payload, with
   the ruleset class reused only where an existing one fits (Harvest's 0x69 for a garden, for example).

## 9. How these were found

Static only; no client was run for this pass. The binary is `Client/Game.exe` = the archived Steam
`PathOfExileSteam.exe` (SHA-256 `e9ba3d31…f53d`, build 328228). Tools: a persistent analysed Ghidra 12.1.4
project queried headless and read-only (`Decomp.java`, `Callers.java` from LocalExile's `scripts/re/ghidra`);
~200 functions decompiled around the file, object, pump, ruleset, GEAL, frame and ABI subsystems;
`objdump -d -M intel` with RIP-resolution for targeted windows; a stdlib PE xref helper for callers and string
references; the full narrow/UTF-16 string dumps of the image; and LocalExile's own RE docs
(`Agents/docs/re/*`, `Agents/docs/protocol/*`, `schema.md`, the zoom mod) as the entry map — their 3.29.3c code
addresses were confirmed to hold on 3.29.3d. GEAL built-in names were extracted by attributing every call to the
name-intern function `0x141ee8450` inside the startup registrar `0x141ece040` to the `.rdata` UTF-16 string its
registration loads.

### Round-2 targets (what a third pass should decompile or dump)

The highest-value leftovers, all named by this pass: the 3.29.3d dispatcher byte table (`dispatch_ids.py` against
3.29.3d — the 0x2ca case, and the 3.29.3c case numbers do not transfer); the ruleset creation loop (xrefs of the
thunk table `0x144831770..0x144831ab8`) and the ruleset vtable `0x14347b910` slot dump; the world handler's 0x2bb
case (`0x14218ddf0` around `0x14218e5fa`); the `.ot` loader `0x14247b5b0` and body parser `0x14247da10`; the
`ExecuteGEAL` console handler `0x1419896e0`; the bundle-table insert helpers (`0x140e49ec0`, `0x140e4a2c0`,
`0x140e4a850`) and the table ctor `0x140e4b120`; the effect task vtable `0x1435d61d0`; the in-game update body
`0x140d54c50`; a global holding the current instance client (for hook-free group registration). The per-scout
need-lists are preserved with the analysis workspace.
