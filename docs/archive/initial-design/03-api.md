# The modding API

The goal: everything GGG built for league mechanics and game systems, reachable and extendable from a mod, the way
Forge did it for Minecraft. This is the target shape, not a specification; names are placeholders.

## Forge, mapped onto this game

| Forge | Here | State |
|---|---|---|
| Mod loader, lifecycle, dependencies | `ModHost`, `mod.json`, `IMod.Load`, hot reload | exists |
| Event bus | `GameEvents` (server) | exists; grows `Client*` entries |
| Sided code (client / server) | Server C# only; the client side is the generic host, driven by messages | new |
| Networking (`SimpleChannel`) | The mod channel: a named channel per mod | new |
| Registries (blocks, items, entities) | Content registries over the game's tables: items, mods, stats, monsters, areas, UI screens | new |
| Resource packs | The file overlay: `.ui`, images, strings, audio | new |
| Data packs | Table row patches, shared by client and server | new |
| Capabilities | Components (213 types) and stats | exists server-side; read access client-side later |
| GUI / screens | `.ui` files plus the UI bridge | new |
| Config | `settings.json`, Waypoint's form | exists |
| Game rules / dimensions | Rulesets (33 client classes) and leagues | partly; see below |

The big difference from Forge: Minecraft mods run Java inside the client. Here the client is closed C++, so the API
is **server-side, with the client as a remote**. That is a limit (no per-frame client logic in a mod) and a gift
(one language, one process, hot reload, nothing untrusted in the client).

## Layers

**Layer 0: raw.** For people doing reverse engineering. Send a raw message, call an object function, open a UI
kind, read a client value. `gm-commands` already offers some of this as chat commands (`/raw`, `/ui`).

**Layer 1: systems.** Typed wrappers over what GGG built. This is the "expose every interface" layer.

**Layer 2: content.** Registries and builders: "add an item base", "add a window", "add an encounter".

A mod uses the highest layer that does the job.

## Layer 1: the systems

| System | What the API gives | What it rests on |
|---|---|---|
| **UI** | `ClientUi.Open`, controls by id, events | `.ui` loader, control registry |
| **Screens** | `Screens.Open(kind, …)` with named kinds | S→C `0x7b`, 0x73 kinds |
| **Rulesets** | `Rulesets.Get(id)`: send a ruleset message, receive the client's, per ruleset typed helpers (`Expedition.PlaceExplosive`, …) | ruleset sender `0x1421a1960`, the 33 classes |
| **Objects** | create, remove, call a function, set a state-machine state, attach an effect | existing messages, `0x285` |
| **Components** | the server's component model; later, client reads | `components.md` |
| **Stats** | add and read stats; stat-gated client features listed by name | `GameEvents.Stats` |
| **Items** | build, roll, grant; item display in a mod window later | Game module |
| **Areas** | generate, modify, add encounters | levelgen, `AreaGenerated` |
| **Camera, sound, effects** | zoom, shake, play a sound or video | object functions; `VideoDisplay` |
| **Chat and commands** | exists | `GameEvents.Command` |

For each ruleset the work is the same: name its messages in both directions, write the typed helper, and add the
`GameEvents` entries a module needs. A module in `Modules/Core` or `Legacy` replicates GGG's behaviour with it; a
mod changes or reuses it. The API is one, for both.

## Layer 2: registries

A registry entry is a row a mod declares once and both sides receive:

```csharp
public void Load(IModContext mod)
{
    var items = ClientRegistries.For(mod).Items;
    items.Add("WildSeedT1", new ItemBase
    {
        Name = "Wild Seed", InheritsFrom = "Metadata/Items/Currency/StackableCurrency",
        Art = "art/2ditems/mods/garden/wildseed.dds", StackSize = 20, Width = 1, Height = 1,
    });
}
```

- The server adds the row to its own data (the rule today: server-owned data goes in `world.db`).
- The host merges the same row into the client's table when the table loads.
- Ids and hashes are assigned deterministically from the mod id and the entry name, so they survive reloads and
  agree on both sides without a sync step.
- A registry is only offered for a table once someone has shown the client accepts a new row in it. Tables differ:
  some are indexed by row number, some by hash, some are baked into code as enums (`ClientStrings` values appear as
  enum constants in RTTI). Each table is its own small investigation ([spikes](04-spikes.md), S7).

Likely order: client strings → UI images → item bases (reusing existing item classes and components) → mods and
stats (a new stat needs a description entry) → monster varieties (reusing existing models) → world areas.

**What a registry cannot do:** give a row behaviour the executable has no code for. A new item base can use any
component the client still registers; it cannot bring back the 3.11 `HarvestSeed` item component.

## Networking

```csharp
var channel = ClientChannel.For(mod, "garden");
channel.On<PlotClicked>((conn, msg) => …);        // C→S, from a UI event or a host bridge
channel.Send(conn, new PlotState(…));             // S→C, to a .ui binding or a host bridge
```

Mods rarely need it directly: `ClientUi` is built on it.

## UI

```csharp
var ui = ClientUi.For(mod);

GameEvents.Command(id, "garden", "/garden", (conn, args) =>
{
    if (!conn.Client.HasHost) { conn.Commands!.Reply("Install the client host to use the garden window."); return; }

    var window = ui.Open(conn, "garden.ui");                    // Mods/Client/<id>/files/metadata/ui/mods/<id>/garden.ui
    window["title_label"].Text = "Sacred Grove";
    window["lifeforce_bar"].Progress = 0.4;
    window["plant_button"].Clicked += () => Plant(conn, window);
    window["seed_list"].Items = seeds.Select(s => new Row(s.Icon, s.Name, s.Count));
    window.Closed += () => …;
});
```

- Properties set in one handler go out as one batch.
- A window belongs to a connection and closes with it; an area change is a new connection, so the mod reopens what
  should persist (a helper can do that).
- Events arrive on the connection's task, like every other `GameEvents` subscriber.
- **Bindings** (later): `text = {lifeforce}` in the `.ui` file, with the mod pushing a model object. Avoids one
  message per label.

## Events

New `GameEvents` entries, general, naming no mod:

| Event | When |
|---|---|
| `ClientReady` | the handshake finished; the capability set is known |
| `ClientMessage(channel)` | an envelope on a channel |
| `UiEvent` | a subscribed control event (normally consumed through `ClientUi`) |
| `RulesetMessage(rulesetId)` | the client sent a ruleset message |
| `ClientScreenOpened` / `Closed` | the player opened or closed a built-in panel (hooked client-side) |
| `ClientInput` (opt-in, per key) | a bound key was pressed; lets a mod have a hotkey without chat |

## Client scripts (deliberately later)

Some things cannot be server-driven: drawing every frame, reacting to hover within a frame, custom controls. For
those the host could run a small sandboxed script (Lua or a restricted JavaScript) shipped in the payload, with
access only to the UI bridge and its own channel. This is the one place untrusted code would enter the client, so
it comes last and behind its own review.

## Compatibility promises

- The API is versioned with the host; a mod states the minimum host version, and Waypoint checks it.
- Client builds are 3.29.3c and 3.29.3d; a mod never mentions an address.
- A mod that needs the framework to hand it something new still goes through the existing rule: a general hook in
  its own LocalExile pull request.
