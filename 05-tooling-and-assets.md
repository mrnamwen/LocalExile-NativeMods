# Tooling and assets

## The `.ui` designer

A mod author should not have to learn the layout language from 124 undocumented files. Three tools, cheapest first.

**1. Language reference and linter.** Generated, not hand-written:
- the grammar and every property seen, from the 124 stock files (extract with `ggdump`, decode UTF-16);
- the full property list per control type from the executable: each registered control reads its properties by
  name, so the names are strings near each control's parser;
- the named settings (`Fonts.*`, `Colours.*`, `Sizes[…]`) from `uisettings.xml`.

The linter checks a mod's `.ui` file against that: unknown control, unknown property, missing `end`, an image that
is neither in the game nor in the mod's payload. It runs in Waypoint on install and as a command-line tool.

The spikes made the linter a requirement, not a convenience ([results](07-spike-results.md)):

- the game's parser throws on an error, and an uncaught throw closes the client;
- the file must have whitespace or a line before `<DEFINITIONS>`;
- a top-level id must not already exist under the parent;
- an unknown property name is read as a reference to another control and fails with `":" expected`;
- a `Picture` with a `margin` inside a `StackLayout` made the game spin forever with no error. The linter carries a
  list of such known-bad combinations, and it will grow.

The parser's own messages carry file, line and column, so when the host catches one it can show it to the author
as it is.

**2. Live preview in the game.** The host watches `Mods/Client/<id>/files/` and reloads an open window when its
file changes, the same loop mods have on the server today. This is the designer with perfect fidelity, because the
game itself renders it. It depends on spike S3c. A `/ui_preview <file>` dev command (in `gm-commands`, where dev
tools belong) opens any payload file with placeholder data.

**3. A visual editor in Waypoint.** A tree of controls, a property grid from the generated reference, and an
approximate canvas. The canvas does not need to be exact, because the live preview sits beside it. This is the
largest piece and the last.

A starter kit ships with the tools: templates for a framed window, a list with a scroll bar, a tabbed panel, a
confirmation, built with `based on` so mods look like the game by default.

## Importing assets

Ranked by how sure the path is. Every item needs its spike before anyone promises it.

| Asset | Path | Confidence |
|---|---|---|
| **`.ui` layouts** | Text; overlay as-is | **Confirmed** (S3a, S3b) |
| **Strings** | Rows in `ClientStrings` and similar tables, or literal text set by the server | High for server-set text; table rows per S7 |
| **UI images, item art** | PNG → DDS (BC7 or BC3, mips), plus a `.dds.header` if the client streams it, plus the atlas or list entry | Medium (S6) |
| **Table rows** | JSON row patches → merged `.datc64`; the schema is in `data/schema` | Medium, per table (S7) |
| **Video** | `.bk2` (Bink 2), played by `VideoDisplay`. The encoder is proprietary | Works if a file is supplied; no free encoder |
| **Audio** | The client uses FMOD; packed audio is in `Audio.bundle.bin`. Format not studied here | Low; needs research |
| **Object files** (`.ot`, `.ao`, `.it`) | Text with GEAL scripts; LocalExile already decodes them. New objects from existing models and effects | Medium: text is easy, behaviour needs matching server code |
| **Effects** (`.pet`, `.epk`) | Partly decoded (`.pet` version 4 holds JSON) | Low to medium |
| **Models and animation** (`.fmt`, `.smd`, `.tgm`, `.ast`) | Formats are read, not written; an exporter from glTF is a project of its own | Low; last |

**The importer** is one command-line tool (and a Waypoint panel over it): `nativemods import <file> --as <kind>`
converts and writes into a payload folder; `nativemods pack <mod>` validates the payload and builds the release
zip. Table patches are JSON keyed by the schema's column names, so they read like the server's own data.

## Payload rules

- **A mod ships only what its author made.** No file from the game, in any form: LocalExile's rule against
  committing game files applies to mod payloads too. A patch to a GGG table is the changed and added rows only.
- **Restoring old content** (for example 3.11 files for a legacy league) cannot be shipped. The only clean route is
  a tool that builds the payload on the user's machine from a copy they supply.
- **Paths are namespaced:** new files go under `…/mods/<id>/`. Overriding a stock path is allowed but declared in
  the manifest, shown by Waypoint, and resolved by load order when two mods override the same file.
- **Size and safety limits** as for server mods today (Waypoint: 64 MB, 2000 files, no path leaving the folder,
  SHA-256 on releases).

## Manifest

`mod.json` gains one optional block; a mod without it is exactly today's server mod.

```json
{
  "id": "garden",
  "client": {
    "minHost": "1.0",
    "required": false,
    "overrides": ["metadata/ui/ingamestate/escapemenu.ui"]
  }
}
```

`required: false` means the mod works without the host (chat fallback); `true` lets Waypoint and the server tell
the player why the mod is inactive for them.
