# The Bridge: one mod, written in C#, translated into what the client already runs

This is the plan for scripting the client alongside LocalExile's server mods. It replaces the centre of
[09](09-native-mods.md): instead of putting a .NET runtime inside the game, **a mod stays one C# project in the
Gateway, and a translation layer turns the client half of it into things the client already understands** —
layout files, object scripts, table rows, bundles, and a small set of remote operations run by a native shim.
In-process mod code survives as an optional last tier, for the few things nothing else reaches.

Evidence is in [08](08-deep-analysis.md) as corrected by [10](10-verification.md). Nothing here has run beyond
the spikes in [07](07-spike-results.md); each layer names the spike it waits on.

## 1. Why translate instead of host

| | CoreCLR in the client (09) | The Bridge |
|---|---|---|
| Runtime | a private win-x64 .NET shipped into every install and Proton prefix | none; the shim is a small native DLL |
| Where mod code runs | a second mod host, in the game process | the Gateway's existing host: same loader, hot reload, settings, `/mod` |
| What a joining player needs | the mod's assembly, installed by hand | the shim once, then payloads the server declares |
| What the server can make a client do | whatever the installed assemblies do | a closed, versioned set of operations |
| Risk inside the game process | GC, exception filters, the DeadlockDetector, Wine | function calls on the game thread |
| Per-frame mod code | yes | no — bindings and GEAL instead ([§5](#5-what-runs-every-frame)) |

LocalExile is a local server: the Gateway is a loopback hop away, so "ask the server" costs well under a frame.
The one thing given up is arbitrary code on the frame, and §5 shows how little of a mod needs that.

## 2. The layers

```
 mod author's C# project  (Mods/Source/<id>/ — one project, server and client halves)
        │ build
        ├──────────────► <id>.dll                 server half, loaded by the Gateway as today
        └─ bridge compiler (MSBuild task + Roslyn)
              ├─ UI           C# builders or hand-written .ui      → .ui (UTF-16), linted
              ├─ scripts      [ClientScript] C# methods            → GEAL in .ot / .ao
              ├─ content      registries (items, objects, monsters) → appended .datc64 rows + .ot/.it chains
              │                                                    → the same rows for the server's catalogs
              ├─ assets       .png/.dds, .ogg, text                 → game formats (+ .dds.header)
              └─ package      everything above                     → client/<id>-<hash>.bundle.bin + manifest
                                                                       (stored blocks, no compressor)
 Gateway  ── Bridge runtime (framework, names no mod) ──────────────────────────────────────────────┐
        Client.For(conn): Ui, World, Sound, Stats, Files, Screens, Camera   events: GameEvents.Client*   │
        ▼ operations, batched                                    ▲ events                                │
 ═══════ S→C 0x2c9 (ruleset update) ═════════════════════ C→S 0x2ca ═══════ the game's own connection ═══╡
        ▼                                                        ▲                                       │
 client shim (LocalExile.ClientHost.dll, imported by Game.patched.exe)                                   │
        build table (SHA-256, RVAs, expected bytes) · frame drain · exception walls                      │
        host ruleset in a free creator slot = the channel and the per-area lifetime                      │
        file overlay: index-table entries → mod bundles;  dev: loose folder with live reload             │
        operation interpreter: the closed set in §4, nothing else                                        │
        bindings: control property ← stat / variable, evaluated each frame                               ┘
```

**The shim** is the only code added to the client, and it is generic: it knows operations, not mods. It is the
only part that knows addresses. The client is frozen at 3.29.3c and 3.29.3d, so its table is written twice and
never chases a patch.

**The channel** is a host ruleset ([10](10-verification.md) §3): the server lists a ruleset id for the area,
the shim's creator answers for it, operations arrive through the game's own ruleset update path already
length-delimited, and events leave through the game's own ruleset writer, which `GameEvents.Packet` already
gives a mod. A client without the shim logs one line and plays on with no client features, so a server can
offer client content without requiring it. Fallbacks, in order: the reserved-id handler group (08 §2); chat
lines (live-proven in the spikes, and enough for a first release).

**The handshake** is the first exchange on each area connection: shim version, client build, operation-set
version, and the hashes of installed payloads. The server then knows per player what it may use, and a mod asks
`Client.For(conn).Available` before relying on any of it.

## 3. What the author writes

One project, the conventions of a server mod, and a `client/` folder:

```
Mods/Source/garden/
  Garden.csproj      references GameServer.Modding and LocalExile.Bridge
  GardenMod.cs       the server half (IMod, or IModule for a league's own system)
  GardenClient.cs    client declarations: registries, [ClientScript] methods, UI
  client/            hand-made files, at their game paths: Metadata/UI/Mods/Garden/Stockpile.ui, Art/…
  mod.json           "client": { "payload": true, "operations": ["ui", "world", "sound"] }
```

```csharp
public sealed class GardenMod : IMod
{
    public void Load(IModContext mod)
    {
        string id = mod.Manifest.Id;
        GameEvents.Command(id, "garden", "/garden", async (conn, _) =>
        {
            var client = Client.For(conn);
            if (!client.Has(id)) { await conn.Commands!.Reply("The garden needs the client host."); return; }
            var window = await client.Ui.Open("Metadata/UI/Mods/Garden/Stockpile.ui");
            window.Set("lifeforce_label", Lifeforce(conn));
            window.OnClick("plant_button", () => Plant(conn, window.Text("seed_box")));
        });
        GameEvents.Client.Add(id, "plot_used", (conn, plot) => Harvest(conn, plot.ObjectId));
    }
}

// Content: one declaration feeds the client's tables and the server's catalogs.
[ClientContent]
static class GardenContent
{
    public static readonly ItemBase Seed = Items.Add("Metadata/Items/Mods/Garden/WildSeed",
        inherits: "Metadata/Items/Currency/StackableCurrency", itemClass: ItemClass.StackableCurrency,
        name: "Wild Seed", art: "Art/2DItems/Mods/Garden/WildSeed");

    public static readonly ObjectType Plot = Objects.Add("Metadata/MiscellaneousObjects/Mods/Garden/Plot",
        carrier: Carrier.MiscObjects, extends: "Metadata/MiscellaneousObjects/Harvest/HarvestPlot",
        script: typeof(PlotScript));
}

// Client behaviour: C# that the build turns into GEAL. It never runs as C#.
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

Paths and names in the example are illustrative.

- **The server half is an ordinary mod or module.** A league's mechanic is a module in `Modules/Custom/`, as
  LocalExile's layering already says; its payload rides with the module, and a league that lists the module is
  what makes a client need it.
- **`[ClientScript]` is a subset of C#**, checked at build time by an analyzer. The `Geal.*` and `This` surface
  is generated from the classified built-in table, so only the 724 names the client implements exist; calling a
  server-only built-in is a C# compile error rather than a runtime surprise. The subset is what GEAL has:
  numbers, bools, strings, locations, lists, `if`, bounded loops, lambdas, `this` and area globals. No classes,
  no allocation, no `async`. The compiler emits GEAL text and checks the compiler's own limits (s16 jumps, 256
  constants and 256 functions per script).
- **Hand-written files are always allowed.** The generators are a convenience; a `.ui`, `.ot` or `.epk` dropped
  in `client/` ships as it is, through the same linter.
- **Script mods** (loose `.cs` in `Mods/Binaries/`) get the runtime API and `client/` files. The build-time
  generators need a project.

## 4. The operation set

What the server can ask a client to do. Closed, versioned, declared per mod in `mod.json` so Waypoint can show
it. Handles are numbers the shim validates; no pointer crosses the wire.

| Group | Operations | Rests on |
|---|---|---|
| `ui` | open a `.ui` under a per-mod container, close it, find, set text/visible/enabled/value, fill lists, subscribe to click/change/submit, bind a property | 07 [L]; removal and lists owe spike U1 |
| `world` | create a client-only object, remove it, play an effect at a place or on an object, call a `Functions` entry, set a local object variable | 08 §5, [10](10-verification.md) §5 |
| `sound` | play a Studio event or an `.ogg` by path, stop it | 08 §6 |
| `stats` | set a display-only local stat (stat-gated stock UI) | 08 §6 |
| `screens` | open a stock screen kind, close the open screen | 03 |
| `camera` | zoom, look-at and rotation nodes, custom speed | the client's own nodes |
| `files` | overlay a payload, invalidate a path (textures, meshes, materials) | [10](10-verification.md) §2 |
| `input` | bind a key to an event or to a window toggle | to be analysed [?] |
| `raw` | call, read, write — **development builds only**, never offered to a server | |

Everything the server already does without the shim stays where it is and is wrapped by the same API: creating
objects (0x2bb), calling object functions (0x285), opening screens (0x7b), replicated state. `Client.World`
picks the shim or the stock message, whichever exists for that player.

## 5. What runs every frame

Three mechanisms cover what mods use per-frame code for, without mod code on the frame:

1. **Bindings.** `window.Bind("gauge", "value", Stat.Lifeforce)` or an expression over variables the server
   pushes. The shim evaluates bindings each frame; the server only sends changes.
2. **GEAL.** Timers, delays, state machines, interpolation, attachments and camera work run in the client's own
   VM, on the object they belong to.
3. **The server tick over loopback**, for anything that is logic. It is the authority anyway.

What this does not cover: drawing that the 47 controls cannot compose, new control types, new input modes,
reading game state the shim has no operation for. Those belong to tier 4.

## 6. Assets: packed and loose

| | Loose (development) | Packed (release) |
|---|---|---|
| Where | `Mods/Client/<id>/files/<game path>` | `Mods/Client/<id>/<id>-<hash>.bundle.bin` + `manifest.json` |
| How it is served | the shim swaps the read result for a path it has on disk | the shim adds index-table entries naming the bundle |
| Formats | all (text is live-proven) | all (they are ordinary bundle reads) |
| Reload | on file change: `.ui` rebuilds its window; textures, meshes, materials through the client's own invalidator; `.ot` and tables at the next area or restart | new hash, new file name, at start |
| Needs | the one read hook | spike F1; no hook |

Rules that follow from [10](10-verification.md) §2:

- **The client's own files are never written.** No edit to `Content.ggpk`, `_.index.bin` or any shipped bundle:
  the index entries live in memory, so LocalExile's SHA-256 pins still hold and uninstalling a mod is deleting
  its folder.
- **Why not a repacked GGPK or a rewritten index:** both work in principle — nothing verifies record hashes on
  read, and a mod bundle on disk is found from either layout — but they change a 70 GB install in place, break
  the build pin, and cannot be undone by deleting a folder. They are worth keeping only as a **no-shim mode**
  for data-only mods: Waypoint appends the mod's bundle records to a copy of `_.index.bin` on a Steam-layout
  install, with a recorded original to restore. Proposed as a later option, not the default.
- **Plain loose files** (no shim) work only for paths the index lacks, and not for `.ot`/`.ao`/`.it`/`.header`.
  That is too narrow to build on.
- A payload may **add** paths freely and **replace** stock paths only when its manifest lists them, so Waypoint
  can show what a mod changes and two mods replacing the same file are caught at install.
- New paths live under a `Mods/<id>/` segment (`Metadata/UI/Mods/Garden/…`) so they cannot collide with the
  game's or each other's.

**The importer** turns author formats into game formats: PNG → DDS with its `.header`; UTF-8 text → UTF-16 with
BOM and CRLF; `.ogg` passes through; table patches → appended `.datc64`. Models and animation have no writer
anywhere yet ([12](12-ceiling.md) §4): until one exists, mods reuse and recombine shipped art.

## 7. Content registries

A registry entry is one C# declaration that produces three things: the client rows (appended, never inserted),
the generated `.ot`/`.it` chain, and the same rows for the Gateway's catalogs. The last is a LocalExile change
the mod guide already lists as not built ("a mod replacing rows of the catalogs"); here it is a general
hand-over, *catalog overlays*, that any mod could use.

- Row indices are assigned at install, not at build, because two mods append to the same table. The shim and the
  Gateway agree on the assignment in the handshake; characters save content by id string, never by row number.
- Offered first: item bases (in existing item classes), MiscObjects, MiscAnimated, NPCs, Chests,
  MonsterVarieties, client strings, ExecuteGEAL rows. Not offered: ItemClasses, new ActionTypes; Stats only as
  display-only.

## 8. Trust

- A server can make a client do exactly the operation set, load linted data, and run GEAL. GEAL's reach is its
  724 built-ins; the worst a hostile script does is hang or crash the client. The linter rejects `Repeat` with a
  non-constant count and unbounded self-`Call`, and the build limits apply; a step budget in the shim is the
  backstop if lint proves too weak.
- Payloads are fetched by hash from the server that declared them, capped in size and count, and shown to the
  player before the first load. `.ui` goes through the linter and the known-bad list (07).
- No server ever delivers native code. The shim is installed by the player through Waypoint; tier 4 mods too.
- The shim refuses any executable whose SHA-256 it does not know and leaves that client stock.

## 9. Tier 4: code in the client, when nothing else will do

For custom drawing, new control types, new action or ruleset classes, and hooks into stock windows. Kept
separate so the common path never depends on it:

- A **native plugin interface** on the shim: a C ABI with the same services the operation interpreter uses, plus
  hooks. A plugin is a DLL the player installs. C# authors target it with NativeAOT (no runtime to ship; no
  unload, so restart to reload).
- Hosting CoreCLR with a private runtime stays possible later, behind the same interface, if hot reload of
  in-client code proves worth its weight ([10](10-verification.md) §6).
- Tier 4 is where the build-bound, feature-specific reverse engineering lives. Because the client is frozen,
  that work is done once.

## 10. Build order

| Step | Delivers | Waits on |
|---|---|---|
| 1 | Shim skeleton: loader, build table, frame drain, exception walls; chat transport; `ui` operations; loose overlay | S1, U1 |
| 2 | Bridge runtime in the Gateway: `Client.For`, handshake, `GameEvents.Client*`; `pob` and `gm-commands` ported | step 1 |
| 3 | Host-ruleset channel replaces chat | H1 |
| 4 | Bundle writer, index-table overlay, importer, Waypoint install of `client/` | F1 |
| 5 | Registries and catalog overlays: item bases, MiscObjects, strings | D1 |
| 6 | GEAL: hand-written payloads, linter, then the `[ClientScript]` compiler and generated built-in surface | G1 |
| 7 | Bindings, `world`/`sound`/`camera` operations, live reload through the invalidator | steps 3–4 |
| 8 | The showcase mechanic ([12](12-ceiling.md) §3) | steps 1–7 |
| 9 | Tier 4 plugin interface | a mod that needs it |

Steps 1–2 are the product described in 06 and need no Gateway change beyond the runtime. Each later step is
useful on its own and none is a prerequisite for a mod that does not use it.

## 11. Asked of the main repositories

- **LocalExile:** `src/LocalExile.ClientHost/` (the shim, built in the release workflow) and
  `src/GameServer.Bridge/` (runtime and compiler); `GameServer.Auth patch --client-host` adds the import, opt-in;
  the area message carries the host ruleset id when the player's client has the shim; catalog overlays;
  `GameEvents.Client*`. All general, none names a mod.
- **Waypoint:** install and remove `Mods/Client/<id>/`, the opt-in switch for the shim, "this mod changes these
  files and may do these operations", payload fetch for joined servers.
- **LocalExile-Mods:** `mod.json` gains `client`; `build-index.ps1` checks it; release zips carry the bundle.
