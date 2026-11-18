# Verification: a third pass over the claims in 08 and 09

[08](08-deep-analysis.md) and [09](09-native-mods.md) were written from five scout reports in one sitting. Before
building on them, every load-bearing claim was re-derived from the 3.29.3d binary by independent reviewers who
were asked to refute it, not confirm it. This file is the result. Where it disagrees with 08 or 09, **this file
wins**; the wrong statements in those files now point here.

Tags as before: **[V]** verified statically in this pass, **[L]** live, **[D]** LocalExile's docs, **[I]**
inference, **[?]** open. Nothing was run; every "works" below that is not [L] still owes a live spike
([§7](#7-spikes-still-owed)). Addresses are 3.29.3d image VAs.

## 1. Scorecard

| Area | 08/09 said | Verdict | What is actually true |
|---|---|---|---|
| Index table at `fs+0xc900` | the parsed `_.index.bin`, unvalidated | **confirmed** | as stated; it also exists in GGPK mode |
| Loose files ("route C") | a file under `Bundles2/` wins over the index | **refuted** | the index always wins; disk serves only paths *absent* from it ([§2](#2-files)) |
| "Nothing is cached" | live reload needs no invalidation | **partly** | true of bytes; parsed materials, meshes, textures, UI are cached by path |
| Unclaimed message id | fatal, no silent path | **confirmed** | and an exception inside a handler is converted to the same fatal |
| Ruleset wire id | `0x2ca` both directions | **refuted** | S→C is **`0x2c9`**; `0x2ca` is C→S only ([§3](#3-rulesets-and-the-mod-channel)) |
| Ruleset creator table | `0x144831770..ab8`, 33 creators, 24-slot vtable | **refuted** | a writable array at `0x14475dc70`, 0xaf slots, **68** filled, **9**-slot vtable |
| Unknown ruleset id | "protocol violation" | **refuted** | creation logs and continues; an update for an absent id is silently dropped |
| GEAL built-ins | 1,221 callable | **refuted** | **724** exist on the client; 497 are server-only and fail to compile ([§4](#4-geal)) |
| `ExecuteGEAL` | a run-this-text entry point | **refuted** | a skill class backed by `Data/ExecuteGEAL.dat` (167 rows) |
| GEAL watchdog | none at all | **partly** | `While`/`DoWhile` stop at 1,000 iterations; `Repeat(n)` and recursion are uncapped |
| Dat rows append | append-only, yes | **partly** | the loader accepts them; several tables are bound to compiled enums ([§5](#5-content)) |
| Unknown object type | logged, nothing created | **partly** | it throws; the catch was not traced |
| Patched exe, imports, header slack | 644 bytes, identical imports, 136 bytes slack | **confirmed** | plus: the image is relocatable, so the address table must hold RVAs |
| No integrity check | none | **confirmed** | `IsDebuggerPresent` *is* called (3 sites), none of them anti-tamper |
| .NET 10 in the client | "already on every machine" | **refuted** | not in the Proton prefix, not in self-contained releases ([§6](#6-the-runtime)) |
| Frame point `0x141528fbf` | once per frame [V] | **partly** | the code matches; "once per frame, game thread" is [L], not [V] |

## 2. Files

Every content read does: **index table → the GGPK if one is open → disk** [V]. Disk means `CreateFileW` on the
virtual path as given, relative to the working directory (`<cwd>/Metadata/UI/x.ui`), not under `Bundles2/`.

- **A loose file never overrides a path the index has.** It serves only new paths. This matches the one known
  loose override, `gateway_list.txt`, which is not an indexed path [D].
- **Extension trap:** with an index loaded, the `exists` and `size` queries (`0x140e55340`, `0x140e557f0`) answer
  "absent" *without probing disk* for `.ot`, `.otc`, `.ao`, `.aoc`, `.it`, `.itc` and `.header` paths that the
  index lacks. Loose files of exactly the types a content mod needs are invisible to callers that check first.
- `LooseFilesPatch` only stops the GGPK from opening. It does not change the order.
- **The variant tag** (`0x1444a6930`) is real and unwritten by the game: when set, reads try
  `<stem>.<tag><ext>` first and that probe does fall through to disk. A loose `foo.mod.ui` should beat an indexed
  `foo.ui` [I, untested]; the extension trap still applies.
- **Mod bundles need no compressor.** Every block goes through `OodleLZ_Decompress`, and Oodle's own header
  accepts a stored block: bytes `CC 06` then the raw data, in 0x40000-byte blocks. The encoding round-trips in
  LocalExile's `ooz` [V]; the live client has not been tried.
- An open bundle stays memory-mapped share-read. A mod bundle cannot be rewritten while the client runs; a new
  version needs a new file name.
- **GGPK installs:** the same table is built from the `_.index.bin` inside the pack, and a bundle name the pack
  lacks falls through to disk. No record hash is checked on read; the only compare is the root digest against the
  patch server's welcome, which LocalExile's patch server already answers [V]. No `Content.ggpk` was available to
  test against.
- **A dormant developer hot-reload system ships in the client.** A directory watcher (`0x14100d350`) and a
  per-extension invalidator (`0x141010560`: `.mat`, `.fxgraph`, `.dds`, `.png`, `.tgm`, `.smd`, `.sm`, `.fmt`,
  `.bank`, shaders) with a reload-everything routine (`0x141011270`). It arms only when an engine setting string
  is non-empty, which nothing sets. A host can call the invalidator directly. `.ui`, `.dat` and `.ot` are not in
  its list.

## 3. Rulesets and the mod channel

- **Creation.** `0x1415a6800(client, id)`: if `id < 0xaf` and `table[id]` is set, call the creator, push the
  object on the vector at `client+0xcfd8`. A missing creator logs `Failed to create ruleset N` and carries on.
  The table is filled at static init with `if (slot == 0) slot = creator`, so an entry written first is kept.
- **An area's rulesets come from three places** [V]: a list of ids in the area-entry message, a list world
  generation collects (`rulesets_added` in object types), and the area's own Rulesets row. So the server can ask
  for any id in any area. Which field of the area message is that list was not mapped [?].
- **The object** is 0x18 bytes of base: vtable, u32 id at `+8`, client at `+0x10`. Nine virtual slots: slot 0
  the deleting destructor, slots 1–5 and 7 empty in both the base and Harvest, slot 6 returns a constant, slot 8
  is `DeserialiseRulesetUpdate`.
- **Routing.** S→C `0x2c9` is consumed by a filter (`0x1415a6b50`) before the dispatcher: it finds the ruleset
  whose id matches and calls slot 8 with `{pointer, length, position}`. No match, no effect. Payloads above
  0xffff bytes are the "protocol violation".
- **Consequence: a host ruleset is the better mod channel.** Write a creator into an empty slot of
  `0x14475dc70`; have the server list that id for the area; send mod traffic as `0x2c9`. The game length-delimits
  the payload and hands it to the host's slot 8. No reserved message id, no pump group, and **a client without
  the host just logs one line and drops the traffic** instead of dying, which the reserved-id design could never
  offer. The C→S direction is the client's own ruleset writer (`0x141fa7c10`, id `0x2ca`), which the Gateway's
  `GameEvents.Packet` already hands to a mod. The `0x2c9` reader's exact wire layout still needs reading [?].
- The reserved-id handler group stays possible (a free id is `0x2fc` or above; the 3.29.3d factory accepts ids up
  to `0x2fb`). A host handler must never let an exception escape: the pump turns it into a disconnect.
- Stock league UI finds its ruleset by concrete id (about 250 functions touch the vector). A new id gets nothing
  for free; it is a channel and a lifetime, not a framework.

## 4. GEAL

The registrar interns names with a kind: 227 client, 505 server, 499 shared. Only client and shared names have an
implementation in this binary; a server-only name outside a skipped `server { }` block is the compile error
`Function X does not exist.` The classified table is
[samples/geal-builtins-classified.tsv](samples/geal-builtins-classified.tsv).

| A client script can | A client script cannot |
|---|---|
| play animations, effect packs, attachments, beams, lights | add or remove buffs, change stats, life or damage |
| drive local state machines, timers, delays, counters, lists | spawn monsters, packs, chests or projectiles |
| spawn client objects and effects | drop, grant or identify items; set quest flags |
| play audio and dialogue, fade music, move the camera | open a game panel (every `SendOpen*Screen` is absent) |
| show a message box, a chat line, world text, minimap icons | send anything to the server [V to call depth 6] |
| read stats, buffs, quest flags, the local player, gold | run a source string (no such built-in) |

GGG's own use agrees: across 79,739 object files, 35,750 scripts with a **median of 9 tokens**; 87% sit in
`client { }` blocks; `PlayAnimation` alone is 27% of all calls; zero server-only names appear. In the Harvest,
Expedition, Ritual, Ultimatum and Sanctum folders, 14–18% of files script anything and most of that is animation
and effects. **GEAL on the client is presentation glue driven by replicated state. GGG's league mechanics are
compiled rulesets and components, plus the server.** It is a real scripting engine, and it is the right tool for
how a mechanic looks and feels; it is not where a mechanic's rules can live.

The language, for a tool that generates it: Integer, Float, Bool, String, Location, Function and object values;
every variable is a group (`+=` appends, `v[i]` indexes); `local`, `this.x` (per object), `globals.x` (per
area); `Functions { name = { … } }` called with `Call(name, a, b)`, arguments as `arg1..argN`, result in
`return_value`; lambdas `(x){ … }`; strings interpolate `{ var }`; control flow is built-ins taking code blocks
(`If`, `IfState`, `Repeat`, `ForEach`, `Delay`). Built-in arguments are raw token ranges, so `IfState(x, = 0,
{…})` is legal. Limits as in 08 §4.

`DispatchRulesetEvent` exists as a client built-in (48 shipped calls) and ends in a virtual call on the client
object. Whether it only delivers to a local ruleset or can reach the wire is the one lead on a script-to-server
signal [?].

The 0x285 call is as the zoom mod has it [L]: a u16 folded FNV hash of the entry's name. The scout report's
"byte offset" reading describes the client's internal task, not the wire.

## 5. Content

The loader takes appended rows (count from the file, compiled stride, a marker check). What limits a mod is what
compiled code does with a table:

| Table | Verdict | Why |
|---|---|---|
| BaseItemTypes, MonsterVarieties, NPCs, Chests, Projectiles, MiscObjects, MiscAnimated, HideoutDoodads | appendable | hash- or name-keyed. Existing rows carry hard-coded special cases new rows do not get |
| ItemClasses | **closed** | a generated enum of 103 names in the binary; a 104th row has no enum value |
| Stats | append is safe, new rows are inert | code reads stats by compiled row number; a new stat only reaches generic consumers (display, stat-gated UI) |
| ActiveSkills, GrantedEffects | rows are data, behaviour is not | one C++ action class per ActionTypes hash (2,155 registered); a new skill must reuse an existing class |
| ExecuteGEAL | appendable [I] | the row carries the script a scripted skill runs |
| Mods, BuffDefinitions, WorldAreas, Rulesets | not checked [?] | |

- The object-type register reads 8 tables; the two 08 left unnamed are **MiscObjects** and **HideoutDoodads**.
  MiscObjects is the natural carrier for an arbitrary server-created object.
- A new item base must sit in an existing item class and gets that class's compiled behaviour. An unknown item
  hash yields a null item and leaves its bytes unread.
- Client-side object creation (`0x141ec47e0`, 825 call sites) reads no blob and no register. That it takes any
  path-loadable type is not yet shown.
- **No tool in the tree writes any game format.** Text formats (`.ui`, `.ot`, `.ao`, `.act`, `.epk`, `.mat`,
  `.sm`) need only an encoder. `.dds` plus its `.header` is mechanical. Meshes (`.smd`, `.fmt`, `.tgm`),
  animation (`.ast`) and FMOD banks need writers nobody has; `.ogg` by path needs none.

## 6. The runtime

09 §2 rests on "the .NET 10 runtime is already on every LocalExile machine". It is not where the client can use
it:

- On Linux the client is a Windows process in a Proton prefix (`data/wine-prefix`), which has no `hostfxr` or
  `coreclr`. The host's Linux runtime is unusable from inside it.
- LocalExile's releases are self-contained on both platforms, so a Windows release user has no shared runtime
  either.

Hosting CoreCLR in the client therefore means shipping a private win-x64 runtime with the host, and accepting
what comes with it: a GC and a vectored exception handler inside a process that throws MSVC C++ exceptions as
control flow, under Wine, next to a **DeadlockDetector** (`0x140e3ed10`) that throws when the game thread stalls
past a timeout nobody has recovered. Other tools do host CoreCLR in games under Wine; none of it is tested here.
[11](11-client-sdk.md) §2 ships a private runtime with the extender and makes proving it the first spike (R1).

Also for the shim: the image has `DYNAMIC_BASE`, so addresses are RVAs plus the loaded base; the import
descriptor array must move to the new section; the Authenticode blob sits at the end of the file; the frame
point is skipped while the state stack is empty, so a queue drain needs a fallback.

## 7. Spikes still owed

Nothing in this pass ran. In order of how much design rests on each:

| Spike | Proves | If it fails |
|---|---|---|
| R1 a private .NET runtime started inside the client, on Windows and under Proton, through a play session | client mods in C# with hot reload | NativeAOT client mods, restart to reload |
| H1 host ruleset: creator in a free slot, id listed by the server, `0x2c9` in, `0x2ca` out | the mod channel and its graceful failure | reserved-id group; chat transport (already [L]) |
| F1 stored-block bundle + in-memory index entries, for `.ui`, `.ot`, `.dds`, `.datc64` | packed assets, every format | read-result swap (text is [L]) |
| F2 variant tag with a loose file | hook-free loose overrides | read-result swap |
| D1 appended rows: one item base, one MiscObjects row, one MonsterVarieties row | new content | per-table "not offered" list |
| G1 a payload `.ot` with `client { }` GEAL, driven by state and by 0x285 | script payloads | data-only payloads |
| S1 the import-table loader, on Windows and under Proton | the host loads at all | proxy DLL |
| U1 removing and rebuilding a window; list, table and scroll controls | real UI, live reload | restart to reload |

## 8. How this pass was made

Four reviewers, each given one subsystem's claims and told to refute them from the binary: the persistent Ghidra
project (read-only headless decompiles), `objdump`, PE parsing, the string dumps, and for GEAL a scan of the
shipped script corpus through LocalExile's decoders. The pump and ruleset review was cut short; its unverified
items (the write side, the handler group's calling convention) are unchanged from 08 and marked there. The
`ExecuteGEAL`, area-ruleset and 0x285 points were checked separately by hand.
