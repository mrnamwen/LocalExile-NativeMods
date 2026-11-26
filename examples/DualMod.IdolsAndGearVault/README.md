# DualMod.IdolsAndGearVault (`idolsvault`)

Phase 4 flagship showcase dual-side mod targeting **NativeMods SDK `1.1.0`** (`>=1.1.0 <2.0.0`) and **LocalExile Gateway** (zero server modifications required).

## Features

1. **Wishlist #13 — 32-Cell Idol Board + Alt-Tooltip Affix Tags (`Client` + `Server`)**
   - Builds a 6×6 slab-safe `IUiItemGrid` with the 4 corner cells `(0,0)`, `(5,0)`, `(0,5)`, `(5,5)` visually and structurally locked, producing the exact **32 active cells** of PoE 3.29's Idol Board.
   - Supports all 6 PoE 3.29 Idol footprints (`1×1`, `1×2`, `2×1`, `2×2`, `1×3`, `3×1`) with server-authoritative `PlaceIdolRequest` / `RemoveIdolRequest` validation, occupancy feedback (`X / 32 active cells`), and anchored custom hover tooltips on every placed Idol.
   - Registers `ITooltipService.RegisterItemTooltipAugmenter(...)` to inspect hovered stock items (Idols and gear) and cleanly inject Alt-held `[Affix Tags: ...]` and tier summary sections into stock item tooltips.

2. **Wishlist #14 — Custom Gear-Affinity Stash Tab (`ITabContainer` + `IComboBox` + `IListBox` + `IScrollableFrame` + `IStashAffinityService`)**
   - Uses `ITabContainer` (`Idol Board (32 Cells)` and `Gear Vault Affinity` tabs), `IComboBox` (8 gear classes: `Helmets`, `Body Armours`, `Gloves`, `Boots`, `Belts`, `Rings`, `Amulets`, `Weapons` + arbitrary target stash tab indices `0..47`), `IScrollableFrame`, and `IListBox` with attached item tooltips.
   - Registers `IStashAffinityService.RegisterRoute(...)` so Ctrl+Clicking matching gear routes directly to any arbitrary target Gear Vault tab index in `C→S 0x00E1` (bypassing the stock `0..21` affinity cap), plus optional `RegisterStockAffinityRule(...)` mapping into stock `StashTabAffinityId` bits.

3. **Live Native `Extend` Showcase (`IGealBuiltinRegistry` + `IRulesetRegistry` + `IUiControlRegistry`)**
   - Registers custom GEAL built-in `IdolBoardCount` (reporting placed idol count, occupied cells, free cells, or board capacity).
   - Registers a native per-area client ruleset on verified free slot `IRulesetRegistry.FirstFreeNativeRulesetId` (`0xAE`, never `0xA0`!), receiving server-pushed S→C `0x02C9` board telemetry updates.
   - Registers custom `.ui` control kind `"IdolVault.StatusBadge"` with full `OnInitialize` / `OnPropertyChanged` / `OnFrame` / `OnDestroy` lifecycle and verified zero-leak teardown on `_unloading` and collectible `AssemblyLoadContext` unload.
