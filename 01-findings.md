# Findings

How to read this: **[L]** confirmed in the running client by the spikes ([results](07-spike-results.md)). **[V]** verified statically on 2026-11-18 (disassembly with `objdump`, RTTI strings, the
bundle index through `scripts/re/ggdump`, or the source). **[D]** stated in LocalExile's docs, not re-checked.
**[R]** reported by an earlier investigation of the Harvest garden (from its Ghidra output), not re-checked here. **[?]** unknown.

Addresses are image virtual addresses (base `0x140000000`) read from `Client/Game.exe`, which is the 3.29.3d
Steam executable. LocalExile's rule is that code addresses are the same in 3.29.3c and 3.29.3d while data addresses
(globals, vtables, strings) differ; every address below must be confirmed on 3.29.3c before use.

## 1. The client has its own UI language

- **124 `.ui` files** are in the bundle index under `metadata/ui/` [V]. List: [samples/ui-paths.txt](samples/ui-paths.txt).
  They cover the login and character screens, loading screens, the escape menu, the confirmation window, the HUD,
  the character panel and about 70 tutorial pages. 16 `uisettings*.xml` files sit beside them.
- **The format is UTF-16 text** [V]. What the extracted files show:

  ```
  include "Metadata/UI/InGameState/TutorialPanel/TutorialHelper.ui"
  <DEFINITIONS>
  const $panelWidth = 2085;

  begin Frame
      id = confirmation_window;
      size = 700, 284;
      float = centre;
      background = "Art/2DArt/UIImages/Common/Background1", tile;

      begin StackLayout
          id = contents_layout;
          begin Label
              id = title_label;
              font = Fonts.Normal;
              foreground_colour = rgb( 225, 225, 205 );
          end
          begin Button
              id = action_button;
              height = Sizes[ "Button" ].Size.Height;
          end
          begin Button based on action_button
              id = cancel_button;
          end
      end
  end
  ```

  It has includes, constants, arithmetic, templates (`based on`), named settings (`Fonts.Normal`, `Colours.White`,
  `Sizes["Button"]`, from `uisettings.xml`), and references to another control's property
  (`size = …, main_layout.tutorial_list_frame:height + 50;`).
- **Controls are created by name** [V]. The executable has RTTI for
  `UI::Controls::Detail::Register<T>(std::wstring&&)` for 47 control types:
  [samples/registered-controls.txt](samples/registered-controls.txt). The general ones are `Frame`, `Label`,
  `ScalableTextLabel`, `Button`, `BasicButton`, `GreenButton`, `ImageButton`, `IconLabelButton`, `TextBox`,
  `CheckBox`, `ComboBox`, `ProgressBar`, `Picture`, `VideoDisplay`, `StackLayout`, `FlowLayout`, `ScrollableFrame`,
  `CollapsableFrame`, `TabContainer`, `Table`, `ControlListBox`, `ContextMenu`, `Popup`. Game ones include
  `SkillIcon`, `BuffDisplay`, `FlaskBar`, `Orb`, `MapView`, `ExperienceBar`, `MinionDisplay`.
- **One function loads a `.ui` file into an existing control** [V, L]:
  `0x1418b08d0(Control* this, const std::wstring& path)`, 0x15b bytes, **30 call sites**. Checked call sites build a
  `std::wstring` with `0x140144010(wstring*, const wchar_t*, size_t)` and call it with the control in `rcx`:

  | Caller | Path |
  |---|---|
  | `0x14156b781` (in `0x14156b5d0`) | `Metadata/UI/InGameState/ConfirmationWindow.ui` |
  | `0x140a902ec` (in `0x140a90270`) | `…/TutorialPanel/TutorialPanel.ui`, or the `_Console` one when byte `0x1445998ce` is 2 |
  | `0x1416fb5fb` (in `0x1416fb440`) | `Metadata/UI/Credits.ui`, loaded into the child at `this+0x298` |
  | `0x140276e54` (in `0x140276b70`) | `Metadata/UI/InGameState/EscapeMenu.ui` |

- **Most league windows are not `.ui` files** [V]. `HarvestSiloWindow`, `NPCWindow`, `DelveMapWindow`,
  `SanctumMapWindow`, `UnifiedCraftingBenchWindow` and about 30 more exist only as C++ classes. A mod builds new
  windows from the standard controls; changing those built-in windows means hooking their C++.
- **Creating, attaching, finding, setting text and handling clicks all work from outside** [L]. The functions and
  field offsets are in [the results](07-spike-results.md) ("What was learned about the UI system").
- **The parser reports errors with file, line and column, by throwing** [L]. Nothing in the game catches them on
  this path: an uncaught one shows a message box and closes the client.
- **[?]** How a control is removed and freed, the other events besides a button's click, and the properties of the
  list, table, scroll and tab controls.

## 2. League mechanics are "rulesets"

- The executable has **33 `Game::Rulesets::Client*` classes** [V]:
  [samples/client-rulesets.txt](samples/client-rulesets.txt). Expedition, Legion, Ultimatum, Sanctum, Sentinel,
  Archnemesis, Heist contract, Lake (Kalandra), Necropolis graveyard, Village (Kingsmarch), Mercenary, Azmeri
  (Affliction), Ancestral trial, Maven fights and more.
- A ruleset is a class with a vtable; the Harvest one is `0x14347b910` with 24 slots, constructor `0x14168d750`,
  ruleset id `0x69` [R].
- Rulesets share one message path: sender `0x1421a1960`, writer `0x141fa7c10`, which writes a u16 message id, a u8
  ruleset id, a size and a payload [R]. The NPC script action `DispatchRulesetEvent` exists and is not implemented
  server-side [D, `Agents/docs/protocol/17-npcs.md`].
- This is the closest thing GGG has to a league-mechanic interface, and the natural thing for a modding API to
  expose. **[?]** The base class's 24 slots are not named, and how the client picks a ruleset for an area is not
  documented.

## 3. Other extension points the client already has

| Point | What it is | Source |
|---|---|---|
| Components | 213 named component types, registered through `0x140e35f40`, each with a deserializer in slot 17 | [D] `Agents/docs/re/components.md` |
| UI screens | S→C `0x7b` "open a UI": u8 kind plus four u32; a switch over 0x73 kinds | [D] `protocol/16-waypoints.md` |
| Object scripts | GEAL bytecode in `.ot`/`.ao`/`.it` files (`on_xxx = { … }`), compiled by the client; S→C `0x285` calls a named function on an object | [D] `re/tooling.md` §10, the zoom mod |
| Stats | A stat can switch client UI on (the Backpack mod: `primalist_ascendancy_expanded_main_inventory`) | [D] `protocol/05-inventories-and-ui.md` |
| Tables | `.datc64` files; the server reads the same rows from `client.db` | [D] |

## 4. Messages

- Instance messages are a big-endian u16 id followed by fields, **with no length prefix** [D].
- **Chat already works as a two-way channel** [L]: a chat line from the server (S→C `0x17`, listener
  `0x14017a270(panel, message)`, text `std::wstring` at `message+0x20`) can be taken before it is shown, and a chat
  command (C→S `0x15`) can be sent with the client's own writer. A script mod used both with no Gateway change.
- The dispatcher is `0x142150ae0(this+8, stream, msg)`: `switch(byte_table_0x14215b764[id − 0x0d])` for
  `0x0d ≤ id < 0x2fc`. **Ids that hit the default case do nothing** [D]. **[?]** Whether an id at or above `0x2fc`
  is ignored, or its unread bytes desynchronise the stream; a hook must consume the message before the dispatcher.
- Writing on the socket from outside is solved: `client-control.js` writes with the client's own buffered writer
  (`0x140dfa000` / `0x140dfa160`) under the socket's lock, as `0x1421752b0` does. Without the lock the cipher
  desynchronised twice in about six campaign runs [D].
- Server side, `GameEvents.Packet(opcode)` already hands a C→S message to a mod before the Gateway's handler [D].

## 5. Files

- The client reads either `Content.ggpk` or a Steam install's loose `Bundles2/` (`_.index.bin` plus
  `*.bundle.bin`), chosen at `0x1400fc3f0`; `LooseFilesPatch` makes a standalone-mode client read the loose layout [V].
- The index holds about 1.25 million paths [V].
- The client's file lookup is `0x1421d2bf0`, which throws through `0x1421d2ebe` for a missing path
  (`trace-missing-files.js`) [D].
- **Text files are read through one stream open**, `0x1421d5b50(streambuf, &path, keepBom, &path)`, which calls the
  whole-file read `0x140e564d0(fileSystem, &result, &key)` and keeps the buffer at `streambuf+0x88` [V]. Replacing
  that buffer after the call serves other content, for a path the game has or one it does not [L]. `.ui` and `.mat`
  files were seen going through it; binary formats use other callers of the same read and were not tested.
- The whole-file read first consults an optional table at `fileSystem+0xc900` before the bundle index [V].
  **[?]** What that table is; it may be an override mechanism the game already has.
- `CreateFileW` can be hooked in this client: `nullifier.js` does it for tracing [D].
- LocalExile can read every format it needs (`GameFileSystem`, `GameIndex`, `BundleFile`, Oodle through `ooz`) and
  writes none: there is no bundle or index writer in the repo [V].
- One loose override already works: `Bundles2/gateway_list.txt` wins over the packed one [D].
- `<file>.header` files repeat what the client needs before streaming a file; `.dds.header` holds the DDS header and
  the smallest mips [D]. An imported texture probably needs one.

## 6. Integrity

- The client is already run from a rewritten executable (`Game.patched.exe`: replaced login keys, and two changed
  branches for loose files) [V], and with Frida hooks installed at spawn and at attach [D]. Neither is detected.
- The only hash compare found is the patch step: a local directory hash against the patch server's welcome
  (`0x1428f31c0`), which LocalExile's patch server and `LooseFilesPatch` already answer [V].
- LocalExile itself pins builds by SHA-256 of the unpatched executable and, for Steam installs, of `_.index.bin`
  (`ClientBuilds.cs`). That is the server's check, not the client's, and an in-memory overlay does not change
  either file.
- Swapping a text file's content in memory, adding controls and handlers, and replacing a function drew no
  reaction in about twenty minutes of live testing [L]. **[?]** Binary files, and a long session.

## 7. Launching

- `live.ps1` and `LocalExile.exe` start `Client/Game.patched.exe --patching-server 127.0.0.1:<port>`; on Linux
  through `umu-run` (or `Game_CLIENT_RUNNER`) under Proton [V].
- Waypoint never starts the client and has no injection of any kind; its Frida console bridge is Windows-only
  ("does not work under Proton yet") [V].
- `nullifier.py` spawns the client suspended and loads a Frida agent before the engine starts (Windows) [D].
  `scripts/walkmaps/proton-recorder.cpp` loads Frida Gum into the Proton client with `LD_PRELOAD` (Linux) [D].
- The executable imports, among others, `VERSION.dll`, `WINMM.dll`, `dxgi.dll`, `d3d11.dll`, `dbghelp.dll` [V].

## 8. What stopped the Harvest 3.11 garden

- Still in the 3.29 client: about 11,400 files with "harvest" in the path (monsters, effects, terrain, currency
  art) [V], the tables `HarvestSeeds`, `HarvestInfrastructure`, `HarvestSeedItems`, `HarvestCraftOptions` and
  others [V], the world objects under `metadata/miscellaneousobjects/harvest/` [R], `HarvestSiloWindow` [V].
- Gone [R]: the seed and infrastructure item bases and their item component code, the Seed Stockpile UI kind, the
  pipe-linking UI, the collect button, the 3.11 craft list. Two C→S ids (0x27a, 0x2ca) are unidentified.
- That investigation's conclusion was "not possible: the item component code is gone, and LocalExile has no client-mod
  path". The second half is what this project removes. The first half stays true and shapes the showcase
  ([roadmap](06-roadmap.md)).

## 9. How the static checks were made

The first pass used no Ghidra and no running client. `samples/pex.py` parses the PE with the standard
library: `xref <string>` lists RIP-relative references to a string with the containing function from `.pdata`;
`callers <va>` lists direct calls. Disassembly was `objdump -d -M intel --start-address … Client/Game.exe`. The
bundle index was listed with the built `scripts/re/ggdump` (`list <regex>`), which opens the client read-only.
