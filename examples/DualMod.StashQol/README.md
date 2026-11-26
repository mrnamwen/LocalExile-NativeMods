# DualMod.StashQol (`stashqol`)

Phase 1 showcase dual-side mod targeting **NativeMods SDK `0.3.0`** and **LocalExile Core Hooks Part 2a**.

## Features

1. **Deposit All (`ScreenSlot.InventoryToolbar`)** — Routes eligible main-inventory items to matching stash affinity tabs (`AccountStash.FindAffinityTab`) with active-tab fallback, skipping quest items and trade imprints.
2. **Vendor All (`ScreenSlot.StashHeader`)** — Two-step confirmation guard; refuses public/folder/hidden/parent-Map/parent-Unique tabs and items with socketed gems or skin transfers; quotes via `GameEvents.VendorQuote` and commits atomically.
3. **Inventory Sort (`ScreenSlot.InventoryToolbar`)** — Deterministic 12×5 2D bin-pack sort (area → height → width → class → base name → rarity) in one atomic batch.
4. **Map Tier 3→1 Upgrade (`ScreenSlot.MapTabBar`)** — Upgrades group-0 normal maps 3-to-1 via `MapUpgrade_BaseItemTypesKey`, skipping unique/guardian/mirrored/blighted (and corrupted by default).
5. **Blight Oil Upgrade Max (`ScreenSlot.BlightTabBar`)** — Cascades 3-to-1 upgrades across the 13-tier oil chain (`Clear Oil` → `Golden Oil`), ignoring special oils and anoint input slots while respecting the 5,000 stack cap.
6. **Roll Until Regex Match (`RegexRoller.ui`)** — Captures hovered target via `IHoverService`, runs bounded rolls (hard cap + 50 ms regex timeout), respects worn/mirrored/corrupted/Hinekora's Lock/public-tab guards.
7. **`Ctrl+Shift+RMB` Bulk Stack Use (`IInputService` + `IHoverService`)** — Suppresses native PoE right-click before `0x58`/`0x71`/`0x72` fires when not typing in a `TextBox`; supports Stacked Decks (direct to inventory, stops when full), Orbs of Regret/Unmaking (inventory and non-public stash tabs), and targeted orbs (stopping on max quality/sockets/links).
8. **PoB Passive Tree Auto-Apply + HUD Toast (`PobImport.ui` + `IToastService`)** — Pastes PoB codes/XML/node lists via `IClipboardService`, auto-allocates connected frontier nodes on `GameEvents.LevelUp` via `IPlayerWorld.AllocatePassivesAsync` (isolating mastery choices per call), and notifies via HUD toasts.
