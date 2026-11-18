# The native mod runtime and the full API

This is the design the maintainers asked for after the first pass: a comprehensive modding API that can put **native
code in the client**, authored in **C#**, with custom content and custom leagues as the endgame. It supersedes two
first-pass positions — "no mod ships native code" and "client scripts, deliberately later" — and grounds every
mechanism in the second static pass, [08-deep-analysis.md](08-deep-analysis.md). The 02/03 documents remain the
phase-1 record; where they disagree with this file, this file wins for phase 2.

> **Superseded in part.** [10-verification.md](10-verification.md) refuted two things this design leans on: that
> a .NET runtime is available to the client process (§2), and that GEAL offers 1,221 built-ins and a run-text
> entry (§1, §3). [11-bridge.md](11-bridge.md) is the current plan: mod code stays in the Gateway and is
> translated for the client; code inside the client becomes an optional last tier. The tier model, the service
> list and the trust rules below carry over; the runtime in §2 does not.

The honest ceiling in [06](06-roadmap.md) also moves. "The removed C++ does not come back" is still true of the
game's own code — but a mod can now bring its own: behaviour GEAL cannot express becomes a native client mod, and
the gap between "functional equivalent" and "restored league" is exactly what that tier closes.

## 1. The three-tier extension model

| Tier | What ships | Who can install it | What it can do |
|---|---|---|---|
| **1. Data payloads** | `.ui`, images, strings, dat row patches, sounds | the server declares them; Waypoint or the host installs them; joining players can auto-fetch them (linted) | new windows, reskins, new item bases, new table rows, sound |
| **2. GEAL scripts** | `.ot`/`.ao`/`.it` text with `on_xxx` handlers and `Functions` entries | as tier 1 (they ride in payloads) | client-side behaviour: state machines, transitions, effects, animations, timers, camera, randomness — 1,221 built-ins, 21 event kinds, callable from the server via 0x285 |
| **3. Native client mods** | C# assemblies (net10.0) | **the user, via Waypoint, opt-in** — never auto-fetched | everything else: per-frame logic, custom HUD, new input, deep hooks, anything the host exposes |

Tiers 1–2 make most content possible with no native code at all; tier 3 exists because some things need to run
every frame or reach where neither data nor GEAL goes. A league typically uses all three: server module
(authoritative rules, in the Gateway) + payload (art, windows, object types, scripts) + optional native client mod
(the parts that must think every frame).

The division of authority does not change: the server decides; the client renders and predicts. Tier 3 runs on the
player's own machine, installed by the player — it is trusted exactly as much as a server mod is today (a C#
assembly the user opted into, running in a LocalExile process).

## 2. The runtime

```
Game.patched.exe ──import──▶ ClientHost.Native (small C++ shim)
                               │  build table (SHA-256 + expected bytes), refuses unknown builds
                               │  trampoline at the frame point 0x141528fbf (queue drain, SEH walls)
                               │  reserved-id handler group (object, no code hook)
                               │  boots .NET 10 in-process (hostfxr → coreclr) at first frame
                               ▼
                             ClientHost (net10.0, the managed host)
                               │  services: Files, Channel, UI, Sound, Stats, World, Screens, Geal, Raw
                               │  mod loader: one collectible AssemblyLoadContext per client mod
                               │  hot reload by file watch, exactly like the Gateway's Mods/Binaries
                               ▼
                             client mods (C#, one folder each: mod.json + <id>.dll + client/ payload)
```

**Loading.** Same decision as phase 1, now de-risked statically ([08](08-deep-analysis.md) §7): an import-table
entry `LocalExile.ClientHost.dll` appended by the patch step (the PE has the header slack; the current patch's 644
changed bytes touch nothing the loader reads; import tables of both exes are identical). Fallback: `version.dll`
proxy plus `WINEDLLOVERRIDES` under Wine. The shim does nothing until the frame point fires, so Windows' loader
and Wine's map it before the engine starts, and every later step runs on the game thread.

**Why an in-process .NET 10 runtime instead of NativeAOT-compiled mods:** hot reload. Server mods reload in ~0.4 s
by watching files; client mods get the same loop (collectible `AssemblyLoadContext`, path-backed load from a
private copy — the Gateway's proven recipe — with the same unload-verification discipline). The runtime is
already on every LocalExile machine (LocalExile and Waypoint are net10.0). NativeAOT stays available later as a
*release packaging* of the same mod sources for authors who want a single dependency-free DLL — the SDK is a plain
managed library, so both targets compile it — but it cannot unload, so it is not the development story.

**Build binding.** The shim carries the address table per build (3.29.3c, 3.29.3d), each entry with expected
bytes; unknown SHA-256 → load nothing, log, leave the client stock ([08] §6 lists the data addresses that move
between builds). No signature scanner to maintain while the build set is two.

**Threading contract** (from [08] §6): all game-object, UI, audio, stats and file-table operations happen on the
game thread, inside the frame, via `Game.RunOnGameThread`; the host never blocks the frame; socket writes go
through the client's own sender `0x1421752b0` or under the stream SRW at `+0x248`; the connect-job thread is
nobody's business but the client's. Mod code may use its own threads for computation, but every bridge call is
marshalled.

**Exception walls.** Every bridge into the client is wrapped: MSVC C++ throws (`0xE06D7363`) are caught at the
shim boundary and reported (the `.ui` parser's file/line/column errors become mod-author feedback), and managed
exceptions never propagate into native frames. A mod that throws on the frame is disabled, not fatal — the rule
the Gateway already applies to its mods.

**ABI.** Win64 x64 throughout. The interop layer implements, in `unsafe` C#: the game allocator
(`0x142c8cf60(size, align)` with the 16-byte header protocol — buffers the game frees *must* carry it),
`std::wstring` construction (`0x140144010`), and function-pointer calls into the address table. No CRT frees of
game memory; the client's CRT is static VS2019. Details and every signature are in [08] §6.

## 3. The host services (client-side C#)

Each service is a thin, typed wrapper over one analyzed subsystem. All calls marshal to the game thread
automatically; handles are numbers the host validates; a mod never holds a raw game pointer.

| Service | What it gives | Rests on ([08] §) |
|---|---|---|
| `Client.Files` | overlay a payload folder (or single buffers): route B (index-table entries + mod bundle) on any layout, route A (read-result swap) as the universal fallback; hot reload for text | 1 |
| `Client.Channel` | named channels on the reserved message id: `Send`, `On<T>`; envelope `u16 id, u16 channel, u32 length, payload`; the host's handler group claims the id (recipe in 08 §2) | 2 |
| `Client.UI` | open/find/set/subscribe on `.ui` windows — the spike-proven operations, now with exception walls and the linter as the gate | 07 |
| `Client.Sound` | `Play(name)` for any FMOD Studio event or shipped `.ogg`; the client's own submission chain | 6 |
| `Client.Stats` | display-only local stats (`Set(key, value)`), stat-gated UI without server round-trips; the server can overwrite | 6 |
| `Client.World` | client-only objects (`Create(typePath)` — no dat row), effects (`PlayEffect(path-or-row)`), object function calls, camera control through the client's own paths | 5 |
| `Client.Screens` | open a built-in screen kind (what S→C 0x7b does), plus a typed enum of the 0x73 kinds | 03-api |
| `Client.Geal` | compile and run object scripts on demand; call a `Functions` entry by name (the 0x285 wire form, usable client-side too); enumerate the 1,221 built-ins for tooling | 4 |
| `Client.Rulesets` | typed helpers for the 33 existing ruleset ids: send/receive 0x2ca traffic for an id the area created; per-league helpers grow here | 3 |
| `Client.Raw` | `Call(address, args)`, memory read/write, hook install — development surface, build-checked, `gm-commands`-gated | 0 |

Plus the lifecycle events a mod subscribes to: `Game.OnFrame` (the frame point), `Game.OnAreaEnter/Leave`,
`Game.OnConnectionUp/Down`, `Game.OnChatCommand`-equivalents, and the channel events. All of it is what 03-api
called Layer 0–1, now running inside the client; 03-api's server-side shape (`ClientUi.For(mod)` etc.) survives
unchanged and gains the backing it was missing.

## 4. Native client mods: the authoring experience

Same conventions as a server mod — deliberately, so the ecosystem transfers skills:

```
Mods/ClientBinaries/<id>/
  mod.json     { "id": "<id>", "version": "1.0.0", "depends": [], "assembly": "<id>.dll", "client": true }
  <id>.dll     the mod (net10.0, references LocalExile.ClientHost.Sdk)
  files/       its payload: .ui, images, .ot, table patches (installed into the overlay)
  settings.json
```

```csharp
public sealed class GardenHud : IClientMod
{
    public void Load(IClientContext ctx)
    {
        var ui = ctx.Ui;
        ctx.Channel.On<PlotClicked>(ctx.Manifest.Id, msg => ctx.Game.RunOnGameThread(() => ui.Set("lifeforce_label", msg.Lifeforce)));

        // a native HUD element the .ui system cannot express: drawn every frame by the mod
        ctx.Game.OnFrame += (dt) => Overlay.DrawGrowthGauge(dt);
    }
}
```

- Waypoint installs/uninstalls; the host hot-reloads on file change; `/mod_reload` gets a client twin. Errors in
  chat, as on the server.
- A client mod is **required to declare** the payload paths it overlays and the screens/rulesets it touches, in
  `mod.json` — the same declaration discipline as phase 1 payloads, so Waypoint can show exactly what a mod changes.
- The server side of the same mod (its Gateway half) stays a normal server mod. One repository can ship both
  halves plus the payload; Waypoint already installs server mods and now gets a `client/` folder to deploy.

## 5. Custom leagues: the endgame, concretely

A custom league is three cooperating pieces, all native to LocalExile:

1. **Server module** (C#, existing machinery): the rules, the encounters, level-generation hooks, drops —
   authoritative, hot-reloaded, invisible to the client question.
2. **Client payload** (tier 1+2): new item bases as BaseItemTypes row appends + `.it` chains; new world objects
   as NPCs/Chests/Pet/Projectiles carrier rows + `.ot` overlays (with `client { }` components, GEAL handlers,
   `Functions` entries); new effects as MiscAnimated rows or suffixed paths; new windows as `.ui`; sounds by name
   ([08] §5 answers every "does the client accept a new row?" with "append-only, yes").
3. **Optional native client mod** (tier 3): what neither can express — per-frame HUD, custom input modes, new UI
   behaviours on top of the 47 controls, high-rate overlays.

The server drives the client through channels that all exist today: 0x2bb creates the objects (types resolved
through dat rows the payload added), 0x285 calls their script functions, 0x7b opens screens, 0x2ca rides the
ruleset path for any id the area created, and the reserved-id channel carries mod traffic. **Rulesets are the one
place the client's own league framework stays closed**: ids are dat rows, implementations are compiled in
([08] §3) — a new league does not get a 34th ruleset class, and does not need one; it gets its mechanics from the
server and its feel from the payload. Reusing an existing id (Harvest's 0x69 for a garden rework) is available
when its window/UI machinery fits.

**The Harvest 3.11 garden, re-scored under this design** (updating [06](06-roadmap.md)):

| 3.11 piece | Under phase 1 | Under phase 2 |
|---|---|---|
| Grove terrain, monsters, effects | reused | reused |
| Seeds, pylons, collectors | new item bases (rows), behaviour on the server | same, plus `.it` overlays and GEAL `on_xxx` interactions client-side |
| Seed Stockpile window | mod `.ui` window | same, plus a native mod if it must update every frame |
| Planting and pipe linking | clicks through a mod window | world-object clicks (InteractionAction), beams via `PlayEffect`, linking logic server-side |
| Collect and craft | mod window + server data | same |
| Lifeforce display | chat/label | `Client.Stats.Set` → the game's own stat-gated UI, or the native HUD |

## 6. Trust and safety

- **The server never ships native code.** A joining player auto-fetches payloads only (data + GEAL), linted by
  the same rules as phase 1 ([05](05-tooling-and-assets.md)): linter, known-bad-combination list, size and count
  limits. The handshake declares payload hashes; a server that lies gets refused by the hash check.
- **GEAL payloads get an extra gate**, because [08] §4 found no watchdog in the VM: the host compiles server-
  pushed scripts itself first (compiler limits are real: constant/lambda/call caps), and the interpreter entry
  gets a host-installed step budget — a script that exceeds it is dropped with a report, not a hang. DoS is the
  worst case by construction (closed opcode set, no raw memory/file/net); the budget closes the hang.
- **Native client mods are user-installed** (Waypoint, opt-in, SHA-256 from the catalog) — the same trust as
  today's server mods. A server can *require* a payload, and can *suggest* a client mod by id; installing code
  stays the player's click.
- **The host itself stays generic**: it names no mod, league or feature; per-build address tables are data; and
  `Client.Raw` is dev-gated, not mod-facing.

## 7. Risks and open work

| Item | Status | If it bites |
|---|---|---|
| Loader (S1) import entry | desk work done ([08] §7); live run still owed | proxy DLL fallback |
| Reserved-id group | design closed statically; needs one live run | chat transport (S4-proven) covers v1 |
| 3.29.3d dispatch byte table | not yet dumped (0x2ca case; 3.29.3c numbers do not transfer) | dump is mechanical (`dispatch_ids.py` on 3.29.3d) |
| Ruleset creation loop + vtable slots | not yet decompiled | affects only reuse of existing ids' helpers |
| Route B bundle writer | needs a bundle/index writer with valid Oodle-or-stored blocks | route A buffer swap covers all formats; B is an optimization |
| GEAL watchdog | design here; needs implementation + soak | lint-only first (compile caps), budget later |
| Long integrity soak | owed since the spikes | the 20-min result and the static closure in 08 §7 stand meanwhile |
| Hot unload of native mods | collectible ALC on paper; needs the Gateway's unload-verification discipline ported | restart-to-reload first, then unload |
| 3.29.3c data addresses | every entry needs the c-build value | `compare_builds.py --data-map` already exists |

## 8. Roadmap delta

Phases 1–2 of [06](06-roadmap.md) stand as written (host MVP, UI bridge), with two amendments: the file overlay's
route B is now *described by the game's own table protocol* rather than hoped for, and the v2 channel's receive
side is the handler group, not a dispatcher hook. Phase 4 (content) is de-risked by the S7 answer and gains the
registry rulebook ([08] §5). The new phase, **5b. Native runtime**, slots after phase 2: shim + runtime + services
+ hot reload, `pob` and `gm-commands` ported as the first native mods, then the GEAL budget, then the designer's
live preview on top of hot reload. The showcase (the garden) keeps its place at the end, now with the native tier
available for whatever the payload cannot express.

## 9. What would be asked of the main repositories

Beyond phase 1's list (02-architecture §7): `src/LocalExile.ClientHost/` grows the managed host, the SDK project
and the shim (one C++ file set, built in the release workflow only); `GameServer.Auth patch --client-host` gains
the import append; Waypoint gains the client-mod browser row and the `client/` deploy; `AGENTS.md` gains the
native-mod rule (opt-in, declared, Waypoint-installed). The Gateway-side surface (envelope reader,
`GameEvents.Client*`) is unchanged from phase 1.
