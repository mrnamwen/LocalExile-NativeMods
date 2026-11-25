# Regression & Pitfall Log: `.ui` Parser and Layout Traps

Source: `07-spike-results.md` (live testing on 3.29.3d under Proton) and `02-architecture.md` §5.

## 1. Duplicate Sibling Control IDs Crash the Client

- **Symptom:** Loading a `.ui` file whose top-level or sibling `id` already exists under the same parent control causes the client's `.ui` parser to throw an MSVC C++ exception (`0xE06D7363`). With nothing catching it on the call stack, the client shows an error dialog in a secondary crash process and immediately exits.
- **Root Cause:** Sibling control IDs must be strictly unique in the UI tree, and the game does not install a catch handler around external calls to `Control::Load` (`0x1418b08d0`).
- **Required Mitigations:**
  1. Native shim **must** wrap every `Control::Load` call in a C++/SEH exception wall catching `0xE06D7363` and surfacing the parser error (file, line, column) without terminating the process.
  2. Host **must** allocate a dedicated container `Frame` per mod window and detach/destroy it before reloading.
  3. Waypoint / SDK linter **must** reject duplicate sibling IDs before a `.ui` file reaches the game.

## 2. `Picture` with `margin` Inside `StackLayout` Causes 100% CPU Infinite Spin

- **Symptom:** Placing a `Picture` control that specifies a `margin` property inside a `StackLayout` freezes the main game thread permanently at 100% CPU with no error message or exception.
- **Workaround:** Use a spacer `Frame` inside `StackLayout` instead of setting `margin` on a `Picture` (matches stock GGG layouts).
- **Required Mitigation:** The `.ui` linter (`DualModPackageValidator`) must flag `Picture` controls with `margin` inside `StackLayout` as an error.

## 3. Strict `.ui` Parser Syntax Quirks

- **Leading space before `<DEFINITIONS>`:** Starting a `.ui` file immediately with `<DEFINITIONS>` triggers `Syntax error. You must have a space before <DEFINITIONS>`.
- **Unknown property names:** Produce a misleading `error: ":" expected` parser exception.
- **Encoding:** `.ui`, `.ot`, and `.it` text files expected by the client wide-stream loader are UTF-16LE with BOM (`0xFF 0xFE`) and CRLF (`\r\n`) line endings.
- **Error visibility under crash:** Because fatal errors are displayed by a second process while the first exits, any diagnostics must be flushed synchronously to disk.

## 4. Window Sizing Behavior

- Windows automatically size to their content; editing a root window's explicit size in `.ui` may have no visible effect if content sizing dominates.
- Layout coordinates use a 1600-high virtual screen coordinate space (scaled dynamically to actual resolution, e.g., 0.45× at 720p).
