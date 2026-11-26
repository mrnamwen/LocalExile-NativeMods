# Roadmap

## Phases

| Phase | Delivers | Depends on |
|---|---|---|
| **0. Spikes** | Answers to S0–S5 ([spikes](04-spikes.md)); a go/no-go on native UI | **done for S2–S5: go** ([results](07-spike-results.md)); S1 open |
| **1. Host MVP** | The DLL, the loader step, the address table for both builds, file overlay, mod channel, handshake | S1, S3, S4 |
| **2. UI bridge and server API** | `ClientUi`, confirmation dialogs, `ClientReady`; one existing mod ported as proof | S2, S5 |
| **3. Waypoint** | Client payload install, the opt-in switch, "needs the client host", the linter | phase 1 |
| **4. Content** | Images, strings, the first registries (item bases), the importer | S6, S7 |
| **5. Systems** | Ruleset bridge and typed helpers, league by league; screen and input events | S8 |
| **6. Designer** | Live preview, then the visual editor | S3c |
| **7. Showcase** | The Harvest garden mod | phases 2, 4 |
| Later | Client scripts, custom control types, models and audio import | their own reviews |

Phases 1 and 2 are the product. After them a mod can open its own native window, which is the thing no amount of
server work can do today.

## First ports

Small mods that show the value immediately, each a few lines once `ClientUi` exists:

- **pob:** its Y/N chat confirmation becomes a native dialog.
- **gm-commands:** an item-spawn and teleport panel instead of typed commands.
- **Mod settings in game:** Waypoint's settings form, drawn from the same `settings.json`, as a native window.

## Showcase: the Harvest 3.11 garden

What blocked it ([findings](01-findings.md) §8) and what each piece becomes:

| 3.11 piece | In 3.29 | With NativeMods |
|---|---|---|
| Grove terrain, monsters, effects, world objects | present | reused as they are |
| Seeds, pylons, collectors as items | bases and item component code gone | new item bases through the registry, as plain stackable items; what made them seeds lives on the server |
| Seed Stockpile window | UI kind gone | a mod `.ui` window |
| Planting and pipe linking | UI gone | placement through a mod window or through world-object clicks (`ObjectUse`); pipes drawn with the existing beam effect object |
| Collect and craft | button strings unreferenced; `HarvestSiloWindow` present | a mod window; the 3.11 craft list as server data |
| Craft list | only the 3.19+ rows | server data plus client strings |

So the garden is buildable as a **functional equivalent with native-looking UI**, not a byte-faithful restoration:
the removed C++ does not come back. That is the honest ceiling of this design and worth saying before anyone
starts.

## Risks

| Risk | Likelihood | If it happens |
|---|---|---|
| ~~The `.ui` loader needs state only its C++ owners set up~~ | did not happen | The loader works on a control created from outside |
| A layout crashes or freezes the client | **seen** | Catch parser exceptions in the host; lint before loading; keep a list of known-bad combinations |
| Binary files cannot be served the way text files are | medium | Merged index and `CreateFileW` redirect (route B); Steam layout only at first |
| A table rejects new rows | high for some tables | That registry is not offered; document which |
| Import-table loading fails under Proton | low | Proxy DLL plus `WINEDLLOVERRIDES` |
| Hidden integrity check | low | None seen in the spikes; a long soak is still owed |
| 3.29.3c differs where 3.29.3d was read | low for code, certain for data | The per-build table; every entry checked on both |
| The host becomes a second place for game logic | medium, over time | Hold the rule: the host is generic and names no feature |
| Joining players lack payloads | certain | Handshake, chat fallback, Waypoint fetch |

## Open decisions (maintainer's)

1. **The loader.** Adding an import in `GameServer.Auth patch` changes what the patched client is. It writes no
   archive, but it sits next to the rules "start the client only through `live.ps1`" and "do not add automatic
   client-archive writes to standard startup", and `AGENTS.md` is a protected file. Proposed: opt-in
   (`--client-host` / a `localexile.conf` setting), off by default until phase 2 ships.
2. **Where the host's source lives.** In LocalExile (it must match the Gateway's envelope and the build table) or
   in its own repository under the organisation. Proposed: in LocalExile, as framework.
3. **C++ in a C# project.** The host needs a native toolchain in CI and in releases. Proposed: build it in the
   release workflow only and ship the binary; contributors who do not touch it never build it.
4. **Tests.** The rule is Gateway tests only, at most two per PR. The envelope reader fits that; the host itself
   can only be verified with the real client, which matches "verify with the real client and the logs".
5. **Multiplayer scope.** Whether payload distribution to joining players is in scope at all, or client mods are
   declared single-machine first.

## What would be asked of the main repositories, eventually

Nothing yet. Because chat works as a first channel, the first version of the host needs **no change to the Gateway
at all**: a mod talks to it with `Reply` and `GameEvents.Command`. The first real request would be the loader
(decision 1 above), and later the envelope reader and `GameEvents.ClientMessage`, small and general, reviewable on
their own.
