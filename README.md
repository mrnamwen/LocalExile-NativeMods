# NativeMods

A proposal for client-side modding in [LocalExile](https://github.com/LocalExile/LocalExile): one trusted component
loaded into the Path of Exile client, driven by the server mods that already exist and installed by Waypoint, so
that a mod can have its own native windows, its own files and its own messages.

**This is a concept with a working proof, published for discussion.** It is not part of LocalExile, LocalExile-Mods
or Waypoint, nothing here has been reviewed by their maintainers, and nothing in it is ready to install. Opinions,
objections and better ideas are the point: please open an issue or a discussion.

![A window defined by a mod's text file, opened in game by a server mod](screenshots/s4-button-reaches-server-mod.png)

*A window the game never shipped: its layout is a text file in a mod folder, the game's own UI system draws it, a
LocalExile server mod opens it with `/garden`, and the Plant button reports back to that mod.*

## What was shown

On the 3.29.3d client, with experimental test code ([results](07-spike-results.md)):

- the game's own UI system builds a window from a `.ui` layout file when asked from outside;
- that file can come from a folder on disk, including a file the game never had;
- clicks, typed text and check boxes reach outside code, which can change labels in reply;
- a normal LocalExile script mod on the server can open the window and receive its button presses, with no change
  to the Gateway;
- the window survives an area change.

## What was not shown

- Loading the component the way the design proposes (the test used a Linux-only preload rig).
- Anything on 3.29.3c, or on Windows.
- New images, new table rows, league ruleset messages, a dedicated message id, removing or reloading a window.
- Any long-run stability.

## The idea

The server already decides everything in this game; the client is a renderer with a fixed set of screens, tables
and league classes. Today a mod can only reuse what the client happens to still contain (the Backpack mod turns on a
leftover inventory grid with one stat), and everything else goes through chat commands. NativeMods adds a **client
host** that gives server mods three things they lack: their own files in the client's file system, their own
messages on the game connection, and control of the client's own UI system. On top of that sits a Forge-style API:
registries for content, events on both sides, and typed access to the systems GGG built.

Mods stay server-side C#. A mod author writes C# and ships data; no mod ships native code.

**Target clients: 3.29.3c and 3.29.3d only**, the builds LocalExile supports.

## Documents

| File | What it holds |
|---|---|
| [01-findings.md](01-findings.md) | What was found in the client and the repositories, and how each fact was checked |
| [02-architecture.md](02-architecture.md) | The client host, loader, file overlay, mod channel, UI bridge, and what would change in each repository |
| [03-api.md](03-api.md) | The modding API: registries, events, networking, UI, GGG's systems, with a Forge comparison |
| [04-spikes.md](04-spikes.md) | The experiments that prove or kill the design, with pass/fail criteria |
| [05-tooling-and-assets.md](05-tooling-and-assets.md) | A `.ui` designer, asset import, packaging |
| [06-roadmap.md](06-roadmap.md) | Phases, the Harvest 3.11 garden as a showcase, risks, open decisions |
| [07-spike-results.md](07-spike-results.md) | What was run, with screenshots, failures and what they taught |
| [spikes/](spikes/) | The test code |
| [samples/](samples/) | Evidence lists and a small PE helper |

## Questions for the community

1. **Is a client component acceptable at all?** LocalExile has kept the client untouched apart from the login keys.
   This would add code inside it, opt-in.
2. **Server-driven or client scripts?** The proposal keeps all mod logic on the server and makes the client a
   remote. Is that enough for the mods you want to write?
3. **How should it load?** An entry in the patched executable's import table, a proxy DLL, or something else
   ([architecture](02-architecture.md) §1).
4. **What would you build first?** The list in [the roadmap](06-roadmap.md) is a guess.
5. **Multiplayer:** should a server be able to require client payloads from players who join it?

## Ground rules

- No game files are in this repository, and none would ever be in a mod: a mod ships only what its author made.
- This is for LocalExile, a local server. It has no use against, and no support for, the official service.
- Addresses in these documents are the 3.29.3d client's; code addresses are expected to match 3.29.3c, data
  addresses are not.

## Licence

GPL-2.0, the same as LocalExile; `spikes/spikehost.cpp` is derived from its walk-map recorder.

Path of Exile is a trademark of Grinding Gear Games. This project is not affiliated with or endorsed by them.
