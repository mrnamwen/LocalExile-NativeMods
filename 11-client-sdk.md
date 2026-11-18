# The script extender and the client SDK

The goal, as the maintainers and the community have set it: **make the 3.29 client completely moddable** — an
interface C# mods compile client code against, their own assets, their own content — so that a frozen copy of
Path of Exile becomes an ARPG engine for custom content. An invasive script extender is in scope.

LocalExile targets 3.29.3c and 3.29.3d only, and that is what makes this reasonable. The client never changes
again, so reverse engineering is done once and a hook written once stays written; and a build that can no
longer be played officially is no part of GGG's live game or its monetisation.

This file is the plan. It keeps the tier model of [09](09-native-mods.md), repairs what
[10](10-verification.md) refuted, and adds the translation layer that lets most of a mod be written without
touching an address. Evidence: [08](08-deep-analysis.md) as corrected by 10. Beyond the spikes in
[07](07-spike-results.md) nothing here has run; each part names the spike it waits on.

## 1. The shape

```
 one mod project (C#)                                  what ships
 ├─ Server/   IMod / IModule, GameEvents          →  <id>.dll           loaded by the Gateway, as today
 ├─ Client/   IClientMod, against the Client SDK  →  <id>.Client.dll    loaded inside the game
 ├─ Shared/   message records, constants          →  compiled into both
 └─ client/   .ui, art, sounds, scripts, tables   →  <id>-<hash>.bundle.bin + manifest

 Game.patched.exe ──import──▶ LocalExile.ClientHost.dll            the script extender
                                │ native shim: build table, hooks, exception walls, file overlay, channel
                                │ boots a private .NET runtime shipped beside it
                                ▼
                              LocalExile.Client.Host (managed)       loads client mods, hot reload
                                ▼
                              LocalExile.Client.Sdk                  the interface mods compile against
                                ├─ Engine      typed wrappers over the game's systems
                                ├─ Extend      register new things: built-ins, controls, rulesets, actions…
                                ├─ Net         typed channel to the mod's server half
                                └─ Raw         addresses, memory, hooks — for what has no wrapper yet
```

Three ways to put something in the client, used together:

| Path | The author writes | Runs as | For |
|---|---|---|---|
| **Data** | files and registry declarations | game formats the client loads | windows, art, sound, items, objects, monsters |
| **Scripts** | GEAL, or C# the build translates to GEAL | the client's own VM, on an object | how things look and react: animation, effects, timers, camera |
| **Client code** | C# against the SDK | a mod assembly inside the game | everything else: new systems, new UI behaviour, per-frame logic, hooks, extension points |

The server stays the authority for the game's rules. Client code decides how the client behaves; it cannot make
the server believe anything.

## 2. The script extender

**The native shim** is small and generic. It:

- loads through an import added to `Game.patched.exe` by the patch step (S1; the header slack and untouched
  import table are confirmed), with a proxy DLL as the fallback;
- recognises the build by SHA-256 and refuses any other, leaving that client stock;
- owns the address table — **RVAs** plus the loaded base, since the image can be rebased — with expected bytes
  for every hooked site, one table per build;
- provides the hook engine (inline detours, vtable-slot swaps, and the hook-free registrations 08 found: the
  index table, the ruleset creator array, handler groups);
- wraps every call into the game in an exception wall (the game throws MSVC C++ exceptions as control flow) and
  every call out of it so nothing managed unwinds through game frames;
- runs queued work on the game thread at the frame point, with a fallback while the state stack is empty.

**The runtime.** [10](10-verification.md) §6 showed there is no .NET runtime the client can borrow: none in the
Proton prefix, none with LocalExile's self-contained releases. So the extender **ships its own**: the managed
host is published self-contained for win-x64, and the shim starts it from the folder beside it. LocalExile's
releases already ship a runtime this way, so it is the same choice made once more. The same files serve Windows
and Proton, because the client is a Windows process on both.

Why not NativeAOT plugins instead: no unload, so no hot reload, and hot reload is the development loop
LocalExile's mods already have. NativeAOT stays available for a mod that wants a single native DLL.

What this must survive, in the order to test it (spike **R1**, the first thing to run):

| Hazard | Why it matters | Plan |
|---|---|---|
| Runtime start under Proton | never tried here | start on first frame, not at load; measure |
| The DeadlockDetector (`0x140e3ed10`) | throws when the game thread stalls past an unknown timeout | recover the timeout; boot the runtime off the game thread; keep GC pauses short (workstation, concurrent) |
| First-chance exceptions | the runtime's vectored handler sees every game C++ throw | confirm it passes them through untouched; measure cost |
| GC thread suspension under Wine | suspends threads running managed code | game threads run managed code only inside bridge calls; soak test |
| Unhandled-exception filter | the game installs its own | the wall catches before it; never rely on the filter |

If R1 fails on Proton in a way that cannot be fixed, the fallback is NativeAOT client mods with
restart-to-reload. The SDK is the same either way, so mods do not change.

**The managed host** loads each client mod in its own collectible load context from a private copy, watches the
folder, and reloads on change — the Gateway's recipe and its unload checks, ported. A mod that throws on the
frame is disabled and reported in chat; the game carries on.

## 3. The client SDK

One assembly, `LocalExile.Client.Sdk`, versioned, the only thing a client mod references. Mods never see an
address: the SDK is where reverse engineering becomes API, and a wrapper exists only once its functions are
understood on both builds. Until then the same thing is reachable through `Raw`, which is how the SDK grows.

```csharp
public sealed class GardenClient : IClientMod
{
    public void Load(IClientContext mod)
    {
        var game = mod.Game;

        // Engine: typed access to what the client already has.
        var window = game.Ui.Load("Metadata/UI/Mods/Garden/Stockpile.ui");
        window.Find<Button>("plant_button").Clicked += () =>
            mod.Net.Send(new PlantSeed(window.Find<TextBox>("seed_box").Text));
        mod.Net.On<Lifeforce>(m => window.Find<Label>("lifeforce_label").Text = m.Amount.ToString());

        // Per-frame logic, on the game thread.
        game.Frame += dt => _gauge.Advance(dt);

        // Extend: add to the game what it did not ship with.
        game.Scripts.Register("GardenRipen", (self, args) => Ripen(self, args.Int(0)));   // a new GEAL built-in
        game.Ui.RegisterControl<GrowthGauge>("GrowthGauge");                               // usable from any .ui
        game.Rulesets.Register<GardenRuleset>();                                           // a per-area ruleset
    }
}
```

**Engine** — wrappers over systems that exist. The state of each is what 07, 08 and 10 established:

| Service | Gives | Standing |
|---|---|---|
| `Ui` | load `.ui`, find, typed controls, events, visibility | live-proven for frames, labels, buttons, text boxes, check boxes; removal, lists, tables, scroll owe U1 |
| `Files` | overlay folders and bundles, read any game file, invalidate cached assets | text overlay live-proven; binary owes F1; the client's own invalidator found |
| `Net` | typed messages to the server half | chat transport live-proven; host ruleset owes H1 |
| `Audio` | play a Studio event or `.ogg` by name | functions identified |
| `Stats` | read stats; set display-only local stats | functions identified |
| `World` | objects in the area, their components, positions; create client-only objects; play effects | creation and effects identified; component access is the next dig |
| `Scripts` | run an object's compiled events and `Functions` entries; enumerate built-ins | entry points identified |
| `Camera`, `Input`, `Screens` | zoom/look-at nodes; key and mouse events; stock screens | camera through the game's nodes; input not yet analysed |
| `Frame`, `Area`, `Connection` | lifecycle events | frame point live-proven |

**Extend** — registering new things with the game's own registries. Each is a piece of reverse engineering with
a known shape; none has been attempted. This is the road from "mod" to "engine", and
[12](12-ceiling.md) §4 orders the work:

| Extension | What the game has | What registering one takes | Unlocks |
|---|---|---|---|
| GEAL built-ins | a sorted `{name, factory}` table the compiler searches | insert entries; a factory that parses arguments and returns the functor | scripts calling mod code, and through it the server |
| Rulesets | a creator array indexed by id, 9-slot objects | write a creator into a free slot | a per-area mechanic object with its own traffic |
| UI controls | 47 types registered by name | a `Register<T>` equivalent and a control vtable | custom-drawn and custom-behaving controls in any `.ui` |
| Skill actions | 2,155 action classes by name hash | a registry entry and an action class | skills that behave in new ways |
| Components | 213 types with registrars and deserializers | a registrar entry and a component class | new kinds of object state, replicated |
| Item classes | a compiled enum of 103 | hooks where the enum is switched on | items that behave in new ways |
| Stock windows | about 35 C++ windows | hooks per window | changing built-in league and inventory UI |

**Raw** — `Call`, `Read`, `Write`, `Hook`, by named address from the build table. It is the working surface for
whoever is extending the SDK, and available to any client mod: a client mod is code the player chose to
install, exactly as a server mod is. A mod that uses it declares so in `mod.json`.

**Compatibility.** The SDK has a version; a mod names the range it works with, as server mods name LocalExile
versions. Because the client is frozen, an SDK API never breaks for a reason outside the project.

## 4. Client and server halves

- **One project, two assemblies.** `Shared/` holds the message records both sides compile; `mod.Net.Send(x)` on
  either side arrives as `On<T>` on the other. The server half is an ordinary mod — or, for a league's own
  system, a module in `Modules/Custom/`, as LocalExile's layering already says.
- **The channel** is a host ruleset ([10](10-verification.md) §3): the server lists a ruleset id for the area,
  the extender's creator answers for it, S→C traffic arrives through the game's own ruleset update (`0x2c9`)
  already length-delimited, and C→S leaves through the game's own ruleset writer (`0x2ca`), which
  `GameEvents.Packet` already hands a mod. A client without the extender logs one line and plays on. Fallbacks:
  the reserved-id handler group; chat lines.
- **Handshake** on each area connection: extender version, SDK version, client build, installed client mods and
  payload hashes. The server half asks `Client.For(conn).Has(id)` and degrades when a player lacks something.
- **Server-driven operations.** A server mod with no client assembly can still open windows, set labels, play
  sounds and effects and bind control properties to values: the host exposes a closed, versioned operation set
  over the same channel (`Client.For(conn).Ui.Open(…)`). This is the phase-1 API of [03](03-api.md), and the
  right tool for small mods and for content a server offers to players who installed nothing but the extender.

## 5. Scripts: C# to GEAL

GEAL runs on objects, in the client's own VM, with 724 built-ins — and with `Scripts.Register`, with whatever a
client mod adds. Authors can write it by hand in `.ot` files, or write a subset of C# that the build translates:

```csharp
[ClientScript]
static class PlotScript
{
    [OnState("growth")]                       // the server sets the replicated state; the client reacts
    static void Grew(This plot, int stage)
    {
        plot.PlayAnimation("grow" + stage);
        if (stage == 3) { plot.AddEffectPack(Fx.Ripe); plot.ShowMinimapIcon(); }
        Geal.Delay(0.5f, () => plot.PlayAudio("Harvest/PlotReady"));
    }
}
```

- The `Geal.*` and `This` surface is generated from the classified built-in table
  ([samples](samples/geal-builtins-classified.tsv)), so only names the client implements exist; a server-only
  built-in is a C# compile error.
- The subset is what GEAL has: numbers, bools, strings, locations, lists, `if`, bounded loops, lambdas, object
  and area variables. The compiler checks the VM's limits (s16 jumps, 256 constants and functions per script).
- Scripts need no client assembly and no trust beyond data, which is why they exist beside client code: a
  server can deliver them.

## 6. Assets

| | Loose (development) | Packed (release) |
|---|---|---|
| Where | `Mods/Client/<id>/files/<game path>` | `Mods/Client/<id>/<id>-<hash>.bundle.bin` + `manifest.json` |
| Served by | swapping the read result for a path found on disk | index-table entries naming the bundle |
| Formats | all (text live-proven) | all (ordinary bundle reads; stored blocks, no compressor) |
| Reload | on change: `.ui` rebuilds its window; textures, meshes, materials through the client's invalidator; `.ot` and tables at the next area or restart | new hash, new file, at start |

- **The client's own files are never written.** Index entries live in memory; LocalExile's SHA-256 pins hold;
  uninstalling is deleting a folder.
- **A repacked GGPK or rewritten `_.index.bin` is not needed and not recommended.** Both would work — nothing
  checks record hashes on read — but they alter a 70 GB install in place and break the build pin. With an
  extender in the client there is no case they serve better.
- **Plain loose files with no extender** only serve paths the index lacks, and not `.ot`/`.ao`/`.it`/`.header`
  ([10](10-verification.md) §2): too narrow to build on.
- A payload adds paths freely, under a `Mods/<id>/` segment, and replaces stock paths only when its manifest
  lists them, so Waypoint can show what changes and catch two mods replacing one file.

**The asset pipeline** is its own track ([12](12-ceiling.md) §5): PNG → DDS with `.header`, text encoding and
`.ogg` are mechanical; meshes, animation, terrain tiles and FMOD banks need writers nobody has yet. Until then
mods reuse and recombine shipped art; after, they bring their own.

## 7. Content registries

One C# declaration produces the client rows (appended, never inserted), the generated `.ot`/`.it` chain, and
the same rows for the Gateway's catalogs — *catalog overlays*, a general LocalExile change the mod guide already
lists as not built.

```csharp
[ClientContent]
static class GardenContent
{
    public static readonly ItemBase Seed = Items.Add("Metadata/Items/Mods/Garden/WildSeed",
        inherits: "Metadata/Items/Currency/StackableCurrency", itemClass: ItemClass.StackableCurrency,
        name: "Wild Seed", art: "Art/2DItems/Mods/Garden/WildSeed");

    public static readonly ObjectType Plot = Objects.Add("Metadata/MiscellaneousObjects/Mods/Garden/Plot",
        carrier: Carrier.MiscObjects, script: typeof(PlotScript));
}
```

Paths and names in the examples are illustrative.

- Row numbers are assigned at install, since mods append to the same tables; the extender and the Gateway agree
  on them in the handshake; saves store content by id string.
- Offered first: item bases, MiscObjects, MiscAnimated, NPCs, Chests, MonsterVarieties, client strings,
  ExecuteGEAL rows. Tables bound to compiled code (ItemClasses, action types, stats with client meaning) open
  up as the matching `Extend` entry lands.

## 8. Trust

| Thing | Who installs it | Trust |
|---|---|---|
| The extender | the player, through Waypoint, opt-in | LocalExile's own code |
| Client mod assemblies | the player, through Waypoint, with a hash from the catalog | native trust, as a server mod has today |
| Payloads and scripts | fetched from the server that declares them, by hash, shown first | data: linted, capped; a script's worst case is a hang or crash |
| Server-driven operations | nothing to install | a closed set |

**A server never delivers code.** It can require payloads and name the client mods it needs; installing code
stays the player's click. Built-ins and operations that client mods add are available to a server only on
clients where the player installed that mod.

## 9. Build order

| Step | Delivers | Waits on |
|---|---|---|
| 1 | Extender: loader, build table, hook engine, walls, frame queue | S1 |
| 2 | Runtime inside the client, managed host, hot reload, `Raw` | **R1** |
| 3 | SDK v0: `Ui`, `Files` (loose overlay), `Frame`, `Net` over chat; first client mods | U1 |
| 4 | Gateway side: `Client.For`, handshake, shared messages, server-driven operations; `pob` and `gm-commands` ported | step 3 |
| 5 | Host-ruleset channel; `Rulesets.Register` falls out of it | H1 |
| 6 | Bundle writer, index overlay, importer, Waypoint install of client mods and payloads | F1 |
| 7 | Registries and catalog overlays | D1 |
| 8 | GEAL: linter, `Scripts.Register`, the `[ClientScript]` compiler | G1 |
| 9 | `World`, `Audio`, `Stats`, `Camera`, `Input` as each is dug out | [12](12-ceiling.md) §4 |
| 10 | `Extend`: controls, actions, components, item classes | [12](12-ceiling.md) §4 |
| 11 | Asset writers | [12](12-ceiling.md) §5 |

Steps 1–2 decide whether the plan stands as written, so R1 runs before anything is built on it. From step 3 on,
every step ships something a mod can use.

## 10. Asked of the main repositories

- **LocalExile:** `src/LocalExile.ClientHost/` (shim, managed host, SDK; the shim and the private runtime built
  in the release workflow); `GameServer.Auth patch --client-host` adds the import, opt-in; the area message
  carries the host ruleset id for clients that have the extender; `Client.For`, `GameEvents.Client*`; catalog
  overlays. All general, none names a mod.
- **Waypoint:** install and remove the extender, client mods and payloads; show what a mod replaces and whether
  it uses `Raw`; fetch payloads for joined servers.
- **LocalExile-Mods:** `mod.json` gains the client half and its SDK range; `build-index.ps1` checks it; release
  zips carry `<id>.Client.dll` and the bundle.
