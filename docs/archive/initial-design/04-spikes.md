# Spikes

Small experiments, in the order that removes the most risk first. Each has a question, a method, and what counts as
a pass.

**Status (2026-11-18):** S2, S3a, S3b, S5 passed and S4 passed in a reduced form (chat as the transport), all on
3.29.3d under Proton. S1, S3c, S6, S7 and S8 have not been run. What happened is in
[the results](07-spike-results.md); this file is the plan as written beforehand, with a status line added to each.

## How to run them

- **No C++ yet.** Every spike is a Frida script. On Windows it loads through the existing suspended-spawn path
  (`scripts/dev/nullifier.py`) or by attach (`scripts/re/frida/run.py`). On this Linux host the route is Frida Gum
  loaded with `LD_PRELOAD` (`scripts/walkmaps/proton-recorder.cpp`).
- **Where the scripts live.** The Proton client cannot see `/tmp`; agent files must be under a checkout. Use a
  disposable worktree of LocalExile, not the main checkout, and copy nothing back.
- **Client rules still apply:** start only through `live.ps1`, one client at a time on this host.
- **Game thread only.** Run every call at the frame point `client-control.js` uses (`0x141528fbf`, after the
  state-stack guard). Calling UI code from Frida's own thread will corrupt state and look like a failed spike.
- **Ghidra:** an analysed project of the client, headless and read-only (LocalExile's `scripts/re/ghidra`).

## S0. Is anything checked? (the premise)

*Status: nothing seen in a short session; no long soak.*

**Question:** does the client verify its own code or data after start?

**Why it is probably fine:** it runs today from a rewritten executable with Frida hooks installed, and the server
makes every decision, so the client has little to protect. The one compare found is the patch step, already
answered.

**Method:** nothing new. S2 to S4 each change code or data in memory; a crash or disconnect that follows a hook
with a delay, not the hooked call, is the signal to look for. Also leave an idle hooked client in town for 30
minutes.

**Pass:** no delayed failure across the other spikes.

## S1. Loading through an import

*Status: not run.*

**Question:** does a DLL named in `Game.patched.exe`'s import table load before the engine starts, on Windows and
under Proton?

**Desk work:** read the PE's section table for free space after the last section header and decide where the new
import directory goes (a new section is the standard approach); check that nothing reads the executable's own
bytes at run time in a way a new section breaks (the HNC mask is read from `Game.exe` by the server, not by the
client).

**Method:** patch a scratch copy of the patched exe in a worktree's own `Client` folder (a copy, never the shared
client), with a 20-line test DLL whose `DllMain` writes a file. Start through `live.ps1`.

**Pass:** the file appears and the client reaches the login screen, on both platforms.
**Fallback:** `version.dll` proxy plus `WINEDLLOVERRIDES`.

## S2. A window from a stock `.ui` file (go / no-go for native UI)

*Status: passed (2b, the game's own `IConfirmationWindow::Show`, was skipped: 2c made it unnecessary).*

**Question:** can outside code create a control, load a `.ui` file into it, and have the game draw and lay it out?

**Desk work (Ghidra):**
1. Decompile `0x1418b08d0` and its callees (`0x14187ee70`, `0x14187bcb0`, `0x14187b9e0`, `0x14187bf70`,
   `0x14187f000`, `0x1418b6280`, `0x1418bbd60`) to see parse → build → attach.
2. Decompile two callers end to end: `0x1416fb440` (Credits: the smallest) and `0x14156b5d0`
   (ConfirmationWindow: creation, loading, child lookup by id, handler attachment all in one function).
3. From those: the `Frame` constructor and allocation size, how a child is added to a parent, the find-by-id
   function, the in-game state's root control.
4. Find the registry behind `Detail::Register<T>`: the map from name to factory, so a control can be created by
   name without knowing its constructor.

**Method, cheapest first:**
- **2a.** Hook the ConfirmationWindow call site and change the path argument to another stock file
  (`…/Tutorials/Example_Tutorial.ui`). Trigger a confirmation (destroy an item). Proves the loader is not tied to
  its caller.
- **2b.** Call the client's own `IConfirmationWindow::Show` with custom title and text. A useful product on its
  own: native yes/no dialogs for mods.
- **2c.** Create a `Frame` through the registry, attach it to the in-game root, load `Example_Tutorial.ui`.

**Pass:** 2c draws, scales with the window, takes mouse input, and closing it leaves the game stable through an
area change.
**If only 2a/2b pass:** native UI is limited to re-skinning existing windows and dialogs; evaluate the swap-chain
overlay for everything else.

## S3. A file from disk

*Status: 3a and 3b passed for text files; 3c not run.*

**Question:** can the client be given a file that differs from, or is absent from, the bundle index?

**Desk work:** decompile `0x1421d2bf0` and its callers on the `.ui` path (it is reached from `0x1418b08d0`); find
where a path becomes (bundle, offset, size) and where the bytes are read and decompressed; decide the hook point.
Read `GameServer.Data/Ggpk/GameIndex.cs` for the index layout if route B is needed.

**Method:**
- **3a.** Serve a changed copy of `ConfirmationWindow.ui` (different title colour). Trigger a confirmation.
- **3b.** Serve a `.ui` at a path that is not in the index, and load it with S2c.
- **3c.** Change it on disk and reload without restarting the client (is anything cached by path?).

**Pass:** 3a and 3b. 3c decides whether live preview in the designer is free or needs a cache flush.
**Fallback:** route B in [architecture](02-architecture.md) §3: a merged index and a mod bundle in a cache folder,
with `CreateFileW` redirected.

## S4. The mod channel

*Status: passed with chat lines as the transport. The reserved id below is not tested.*

**Question:** can a reserved message id travel both ways without disturbing the stream?

**Desk work:** decompile the reader that feeds `0x142150ae0`: where the u16 id is read, what happens for
`id ≥ 0x2fc`, and where a hook can read `length` bytes from the stream object. Find the Gateway's read loop for an
unknown C→S id.

**Method:** Gateway side, a scratch branch that writes the envelope after area entry and logs the reserved C→S id
(a script mod with `GameEvents.Packet` can do the logging if the Gateway can be told the length). Client side, a
hook that consumes the envelope and answers through the locked writer.

**Pass:** 10,000 round trips during normal play (movement, combat, an area change) with no desynchronisation.

## S5. Events and properties

*Status: passed for button clicks, label text, visibility and reading a text box.*

**Question:** can outside code react to a click and change a control?

**Desk work:** from S2's decompile of ConfirmationWindow: how `action_button`'s handler is attached (the
`std::function` layout, the event list on a control), the `Label` text setter, the visibility setter.

**Method:** with the S2c window: subscribe to a button, set a label's text from the handler, read a `TextBox`.

**Pass:** click → handler → label changes, with no leak or crash on close.

## S6. A new image

*Status: not run.*

**Question:** can a `.ui` file show an image the game never had?

**Desk work:** how `"Art/2DArt/UIImages/Common/Background1"` resolves: through an atlas list (the name has no
extension, which suggests a lookup table) or directly to a `.dds`; whether a `.dds.header` is required; which DDS
formats the UI accepts.

**Method:** overlay one new image and whatever list names it; reference it from the S3b file.

**Pass:** it draws at the right size and survives a resolution change.

## S7. A new table row

*Status: not run.*

**Question:** does the client accept a row it never had?

**Method:** in increasing risk: a changed existing `ClientStrings` text; a new `BaseItemTypes` row that copies a
currency item with a new name and id; the server grants it.

**Pass:** the item shows in the inventory with its name and art, stacks, drops, and survives a relog.
**Expect:** some tables to fail because code indexes them by a compiled-in enum. Record which.

## S8. A ruleset message

*Status: not run.*

**Question:** can the host observe and send ruleset messages generically?

**Desk work:** name the 24 slots of a ruleset vtable from two rulesets (Harvest `0x14347b910`, Expedition); find
where the client creates rulesets for an area and where an incoming ruleset message is routed.

**Method:** log every ruleset message in an area that has one today; replay one.

**Pass:** a table of (ruleset id, message, direction, layout) for one league, produced by the hook alone.

## Order and stopping points

S1 and S2 first, in parallel: they decide the loader and whether "native UI" is real. S3 and S4 next: together with
S2 they are the whole MVP. S5 to S8 widen it. If S2c and the overlay fallback both fail, the project shrinks to
dialogs, files and messages, which is still more than mods have today.
