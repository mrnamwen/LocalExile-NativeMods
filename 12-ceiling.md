# How complex can a mod be?

The short answer: **a new league mechanic is buildable**, with full control of the server, the tooling in
[11](11-bridge.md) and a shim in the client — provided its rules live on the server and it is assembled from
verbs the client already has. What stays out of reach without writing code *into* the client is anything that
needs a new verb: a new kind of item, a new kind of skill, or a screen the stock controls cannot compose. With
tier 4 those fall too, one reverse-engineered feature at a time.

Evidence: [10](10-verification.md). Nothing in the "buildable" column is live-proven beyond the UI and file
spikes; each row names what it rests on.

## 1. Where the limits come from

The client is a renderer with prediction. Three things in it are compiled and closed, and every limit below is
one of them:

1. **Behaviour classes.** Item classes (an enum of 103), skill action classes (2,155, one per ActionType), the
   213 components, 68 rulesets, about 35 league windows. Data selects among them; data cannot add one.
2. **The script VM's reach.** 724 built-ins, all presentation and local state. No authority, no panels, no
   network.
3. **Asset formats nobody can write yet.** Meshes, animation, terrain tiles, FMOD banks.

Everything else — which objects exist, where, in what state; what drops; what a window shows; what a table row
says — is data or server traffic, and the server is ours.

## 2. The ladder

| Level | Example | Needs | Status |
|---|---|---|---|
| 1. Native windows | settings panel, GM tools, build import dialog | shim + `.ui` | live-proven (07) |
| 2. Reskins and text | UI art, item art, strings, sounds by `.ogg` | overlay + importer | text live-proven; binary owes F1 |
| 3. New items | currency, fragments, map-like items, uniques on existing bases | appended rows in **existing item classes**, `.it` chains, server logic | static; owes D1 |
| 4. New world objects | altars, chests, devices, NPCs with dialogue windows | MiscObjects/NPCs/Chests rows, `.ot` with GEAL, shipped art | static; owes D1, G1 |
| 5. New monsters | new varieties from shipped models, skills and effects, recombined and rescaled | MonsterVarieties rows, server AI | static; owes D1 |
| 6. Scripted encounters | bosses with phases, arenas, camera work, telegraphs | server state machine + client GEAL on replicated state + `ExecuteGEAL` skill rows | static; owes G1 |
| 7. **A league mechanic** | §3 | all of the above + the channel | owes every spike in 10 §7 |
| 8. New verbs | a new item class, a new skill behaviour, a custom-drawn screen, stock-window changes | tier 4 code, per feature | reverse engineering not started |
| 9. New art | original models, animation, tilesets | format writers | no tooling exists |

Levels 1–7 need no mod code in the client. Level 8 is possible and bounded by effort, not by a wall: the image
has no integrity checks, and it is frozen, so a hook written once stays written. Level 9 is a tooling project
independent of everything else here.

## 3. The moonshot, concretely

What a league mechanic is made of, and where each part would come from:

| Part | GGG builds it as | A mod builds it as | Gap |
|---|---|---|---|
| Rules, rewards, scaling, persistence | server code | a module in `Modules/Custom/`, listed by a league | none |
| Encounter placement in areas | server level generation | the module's `AreaGenerated` hook and LocalExile's level generation | none |
| Encounter objects | object types + components | MiscObjects/Chests/NPC rows + `.ot`, existing components | components are a fixed set of 213 |
| How they look and react | GEAL on replicated state | the same — this is exactly what GEAL is for | none |
| Monsters | varieties + skills | new varieties over shipped art and existing skills | no new skill behaviours |
| Reward and crafting windows | a C++ window per league | `.ui` windows driven by the server | stock controls only; no item-socket or tree widgets unless a stock control fits |
| HUD tracker | C++ HUD element | a `.ui` panel with bindings | none expected |
| League items | item bases + components | new bases in existing classes | no new item class; behaviour on use is the server's |
| Client-side ruleset | a compiled class | the host ruleset: a channel and a per-area lifetime | stock UI does not know the new id |
| Atlas/passive integration | compiled screens | not offered | tier 4 |

So the mechanics that fit are the ones whose *client* half was always mostly presentation: Breach, Ritual,
Harvest, Ultimatum, Expedition-style encounters, Blight-style defence, Essence, Metamorph-style assembly,
Bestiary-style capture — and new designs of that family. A complete loop is realistic: an object in the world,
a fight around it, a currency that drops, a window to spend it in, a tracker on the HUD, saved progress.

The Harvest 3.11 garden from [06](06-roadmap.md) fits and becomes better than first scored: the grove terrain,
plot objects and their GEAL still ship; seeds are new currency bases; planting is clicks on world objects; the
Seed Stockpile and crafting are `.ui` windows. It stays the right showcase.

What a mod-built mechanic will not have at level 7:

- **Items that behave in new ways on the client.** A seed is a stackable currency with a custom tooltip; it is
  not a socketable, a flask-like or a map-like thing unless it *is* one of those classes.
- **New skill mechanics.** A new skill is an existing action class with new numbers, effects and scripts.
  Prediction and animation follow the borrowed class.
- **Bespoke screens.** A Delve chart or Sanctum floor map is custom-drawn C++. A mod's equivalent is buttons
  and pictures in frames: functional, native-looking, less fluid.
- **New stats with client meaning.** New stats display and gate UI. Anything the client computes from stats
  (movement speed, cast time in prediction) only honours the stats it was compiled with — the server sends the
  results instead.
- **New geometry.** Arenas are built from shipped tiles.

Each of those is a level 8 item, and each has a known shape: a new action class is one registration in a
2,155-entry registry and a class with a vtable; a ruleset is a 9-slot object; a control type is one of 47
registered by name. None has been attempted.

## 4. What tooling the moonshot needs

| Tool | For | Size |
|---|---|---|
| Shim + Bridge runtime | everything | the core of 11 |
| Bundle and table writers | packed assets, appended rows | small; formats are fully read today |
| `.ui` linter and live preview | windows that cannot crash the client | medium |
| `[ClientScript]` → GEAL compiler | scripts without learning GEAL | medium; hand-written GEAL works first |
| Catalog overlays in the Gateway | the server knowing mod content | medium, a LocalExile change |
| Object-type browser | finding shipped art, effects, skills and scripts to build from | small; the decoders exist |
| Mesh/animation/tile writers | level 9 | large, separate |

## 5. Unknowns that could lower the ceiling

- Appended rows are accepted by the loader but no table has been tried live. A table that misbehaves is dropped
  from the offer, not worked around.
- Blob-less client objects and new object types rely on components initialising from text alone.
- Whether a client script can signal the server is open (`DispatchRulesetEvent`). If not, every interaction is a
  click the server already sees or a UI event through the shim, which is enough.
- WorldAreas, Mods and BuffDefinitions were not checked. A mechanic that needs its own areas or its own buffs
  with icons waits on that.
- Long-session stability with the shim loaded has never been measured.
