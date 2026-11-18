# How far it goes: from mods to an engine

The aim is a 3.29 client that custom content can treat as an ARPG engine. This file says what is reachable at
each stage, what a new league mechanic takes, and what must be dug out of the game code to get the rest.

The short answer: **a new league mechanic is buildable** with the server, the tooling in
[11](11-client-sdk.md) and data alone, if it is assembled from things the client already does. Everything past
that — new kinds of item, skill, screen and object — is behind compiled registries, and the script extender is
how they open: one registry at a time, each a bounded piece of reverse engineering on a client that will never
change. Nothing found so far is a wall; original art is the one part that needs tooling rather than hooks.

Evidence: [10](10-verification.md). Nothing in a "buildable" column is live-proven beyond the UI and file
spikes.

## 1. Where the limits come from

The client is a renderer with prediction. Three things in it are closed today:

1. **Behaviour classes.** Item classes (an enum of 103), skill action classes (2,155), 213 components, 68
   rulesets, 47 UI controls, about 35 league windows. Data selects among them; data cannot add one. **Client
   code can** — these are the `Extend` entries of [11](11-client-sdk.md) §3.
2. **The script VM's reach.** 724 built-ins, all presentation and local state. **Client code can add
   built-ins.**
3. **Asset formats nobody can write yet.** Meshes, animation, terrain tiles, FMOD banks. Tooling, not hooks.

Everything else — which objects exist, where, in what state; what drops; what a window shows; what a row says
— is data or server traffic, and the server is ours.

## 2. The ladder

| Level | Example | Needs | Standing |
|---|---|---|---|
| 1. Native windows | settings panel, GM tools, build import dialog | extender + `.ui` | live-proven (07) |
| 2. Reskins and text | UI art, item art, strings, sounds | overlay + importer | text live-proven; binary owes F1 |
| 3. New items | currency, fragments, uniques on existing bases | appended rows in existing item classes, server logic | static; owes D1 |
| 4. New world objects | altars, devices, chests, NPCs with dialogue windows | carrier rows, `.ot` with GEAL, shipped art | static; owes D1, G1 |
| 5. New monsters | new varieties from shipped models, skills and effects | MonsterVarieties rows, server AI | static; owes D1 |
| 6. Scripted encounters | bosses with phases, arenas, camera work | server state machine, client GEAL, `ExecuteGEAL` skill rows | static; owes G1 |
| 7. **A league mechanic** | §3 | levels 1–6 and the channel | owes the spikes in 10 §7 |
| 8. Client systems | custom HUD logic, new input, mod-defined built-ins, a mod's own ruleset | client code on the SDK's `Engine` and first `Extend` entries | owes R1, then per-service digs |
| 9. New verbs | new item classes, skill behaviours, components, control types; changed stock windows | the rest of `Extend` | reverse engineering not started (§4) |
| 10. New art | original models, animation, tilesets, sound banks | format writers (§5) | no tooling exists |

Levels 1–7 need no mod code in the client. Level 8 is the extender doing what it is for. Level 9 is where the
client becomes an engine. Level 10 is independent of all of them and can run in parallel.

## 3. A league mechanic, concretely

| Part | GGG builds it as | A mod builds it as | With client code |
|---|---|---|---|
| Rules, rewards, scaling, persistence | server code | a module in `Modules/Custom/`, listed by a league | — |
| Encounter placement | server level generation | the module's hooks and LocalExile's level generation | — |
| Encounter objects | object types + components | carrier rows + `.ot`, existing components | new components |
| How they look and react | GEAL on replicated state | the same; this is what GEAL is for | mod-defined built-ins |
| Monsters | varieties + skills | new varieties over shipped art and skills | new skill behaviours |
| Reward and crafting windows | a C++ window per league | `.ui` windows from stock controls | custom controls: sockets, trees, charts |
| HUD tracker | a C++ HUD element | a `.ui` panel with bindings | anything drawn per frame |
| League items | item bases + components | new bases in existing classes | new item classes |
| Client-side ruleset | a compiled class | the host ruleset as channel and lifetime | the mod's own ruleset class |
| Atlas and passive integration | compiled screens | not offered | hooks into those screens |

With data alone, the mechanics that fit are those whose client half was always mostly presentation: Breach,
Ritual, Harvest, Ultimatum, Expedition-style encounters, Blight-style defence, Essence, Bestiary-style capture,
and new designs of that family. A complete loop is realistic: an object in the world, a fight around it, a
currency that drops, a window to spend it in, a tracker on the HUD, saved progress.

The Harvest 3.11 garden from [06](06-roadmap.md) fits: the grove terrain, plot objects and their scripts still
ship; seeds are new currency bases; planting is clicks on world objects; the Seed Stockpile and crafting are
`.ui` windows. It stays the right first showcase, and each thing it has to fake is a named `Extend` entry.

What a data-only mechanic lacks, and so what the right-hand column is for:

- items that behave in new ways on the client (a seed is a stackable currency with a tooltip, not a new kind of
  thing);
- skills with new mechanics (a new skill borrows an existing action class's prediction and animation);
- bespoke screens (a Delve chart is custom-drawn; the stand-in is buttons and pictures in frames);
- stats the client computes with (new stats display and gate UI; the server sends results for the rest);
- new geometry (arenas are built from shipped tiles — level 10).

## 4. The dig: what to reverse next

Ordered by what each unlocks per unit of effort. "Known" is what the three passes already have.

| # | Target | Known | To find | Unlocks |
|---|---|---|---|---|
| 1 | **Entity and component access** | 213 components with registrars and deserializers [D]; object creation paths; the world's object map at `world+0x8d8` | component lookup on an object, field layouts for Positioned, Life, Render, Animated, StateMachine, Stats, Targetable | `World`: reading and driving anything in an area. Most of level 8 |
| 2 | **GEAL built-in registration** | the `{name, factory}` table at `0x1445c50f8`, filled by `0x141de9780`; factory returns a functor whose slot 2 runs | the functor and argument-range layouts; inserting after startup | scripts calling mod code. Cheap, and makes every `.ot` extensible |
| 3 | **UI control internals** | `Register<T>` for 47 types; control layout offsets from 07 | the control vtable (draw, layout, input), the registration call, removal and destruction | custom controls; U1's open questions |
| 4 | **Rendering hooks** | three renderers ship; the UI draws through one path | the UI draw-list interface, so a control can draw primitives and textures | custom-drawn HUD and screens without touching the 3D pipeline |
| 5 | **Input** | nothing | key and mouse dispatch, the keybinding table | new hotkeys and input modes |
| 6 | **Rulesets in full** | creator array, 9 slots, routing | which slots are frame/area callbacks and who calls them; the area message's id list field | proper mod rulesets |
| 7 | **Skill actions** | the registry (`0x141947300`, FNV-16 of the name), a factory per class, `Data/ExecuteGEAL.dat` | the action base class and its virtuals; how prediction and animation events drive it | new skill behaviours |
| 8 | **Component registration** | registrar `0x140e35f40`, deserializer in slot 17 | a full component vtable; how `.ot` sections bind to it | new replicated object state |
| 9 | **Item classes and item UI** | the enum table at `0x142fe0b80`, item component readers in slot 5 | every site that switches on the class; tooltip construction | new item kinds, custom tooltips |
| 10 | **Stock windows** | class names, about 35 | per window, on demand | changing inventory, league and atlas UI |
| 11 | **Stats with client meaning** | code reads stats by compiled row number | the consumers worth redirecting | stats that change prediction |

Items 1–3 turn the extender from a UI tool into a general one and should come first. LocalExile's own
reverse-engineering docs cover the server-facing side of several of these (components, object types, items),
which is where each dig starts.

Also still open from 10: the handler-group calling convention and socket write path (unverified), the catch for
an unknown object type, the `0x2c9` reader's wire layout, Mods/BuffDefinitions/WorldAreas bindings, the
DeadlockDetector timeout.

## 5. The asset track

| Asset | Formats | Read today | To write |
|---|---|---|---|
| Textures | `.dds` + `.dds.header` | yes | mechanical |
| UI, objects, effects text | `.ui`, `.ot`, `.ao`, `.act`, `.epk`, `.mat`, `.sm` | yes | an encoder |
| Particles, trails | `.pet`, `.trl` | partly | medium |
| Static and skinned meshes | `.fmt`, `.smd`, `.tgm` | yes (layouts measured) | a writer and a glTF importer |
| Animation | `.ast`, `.amd` | mostly (three key tracks unnamed) | a writer; skeleton retargeting |
| Terrain | `.tgm`, `.tdt`, `.tst`, `.gt`, `.arm` | partly | the largest piece; needs the level-generation knowledge LocalExile already has |
| Sound | `.ogg` by path; FMOD `.bank` | `.ogg` yes | banks need a matching FMOD Studio version |
| Tables | `.datc64` | yes | small |

The readers in `scripts/re/formats` are the specification for each writer. New models on existing skeletons
come first: they need a mesh writer and nothing else.

## 6. What could lower the ceiling

- **R1.** A .NET runtime inside the client under Proton is untested. The fallback (NativeAOT, restart to
  reload) keeps the SDK and loses hot reload.
- Appended rows are accepted by the loader, but no table has been tried live.
- Blob-less client objects and new object types rely on components initialising from text alone.
- Each `Extend` entry is unproven until someone registers one. The shapes are known; the details are not.
- Long-session stability with the extender loaded has never been measured.
