# DualMod.LootTools (`loottools`)

Phase 2 flagship showcase dual-side mod targeting **NativeMods SDK `0.4.0`** and **LocalExile Gateway** (zero server modifications required).

## Features

1. **Filter-Aware Loot Magnet (`Alt+M` / `ScreenSlot.HudTopRight`)**
   - Queries `game.World.GetWorldItems(filterVisibleOnly: true, maxDistance: ...)` backed by PoE's real local `.filter` (`IItemFilterService`).
   - Sends only `uint[]` entity IDs that passed the local item filter to the server (never invents items).
   - Server validates active session state, allocation/visibility (`origin.Shared || ReferenceEquals(origin.By, player)`), lockstep admission (`!LockstepClient || WorldOutput.Has(id)`), clamped radius (`<= 80` cells), batch cap (`<= 64`), and skips quest items and tracked tutorial/start-weapon drops.
   - Relocates each item cleanly to a walkable cell near the player via atomic `Ground.Take` -> `Ground.Add(..., justDropped: false)` + `Game.SendWorldRemoval` (`0x02BD`) + `Game.SendGroundItem(..., justDropped: false)` (`0x02BB` + `0x01A9`) — **never** `0x01B7` Teleport — leaving zero ghost labels and zero server desync.
2. **AoE Pickup (`Alt+G` / `ScreenSlot.HudTopRight`)**
   - Queries filter-visible ground items within pickup radius and sends `AoePickupRequest(EntityIds)` to the server.
   - Picks up gold via `Game.PickUpGold`, stackables via `Game.TakeGroundStack` (syncing any `Rest` via `Game.SendGroundItem` so `wake: true` is preserved), and non-stackables into `MainInventory` (or `ExpandedInventory` when `player.ExpandedInventoryMode == 1`) with `KeepOnGroundIfNotSaved` and coalesced `0x00BF`.
   - Continues checking smaller/stackable candidates if a larger item cannot fit, emits `InstanceItems.InventoryFull()` (`0x0117`) at most once per trigger, and displays picked/gold/full summary via `IToastService`.
3. **Atomic Ground Loot Merger (`Alt+R` / `ScreenSlot.HudTopRight` + Auto-Merge toggle)**
   - Groups compatible partial stacks (`StackSize < MaxStack`, `MaxStack > 1` or gold) by strict item identity (`TypeHash` + equal non-stack payload, skipping quest/unstackable/tracked drops) where merged pile count strictly shrinks.
   - Atomically `Ground.Take`s all claimed piles before summing so concurrent pickups never duplicate items, emits `ceil(total / MaxStack)` piles with `SendWorldRemoval` + `SendGroundItem(..., justDropped: false)`, and supports optional throttled auto-merge via `GameEvents.AreaTick`.
4. **Item Filter Reload & Status (`Alt+F` / `LootToolsPanel.ui`)**
   - Triggers `game.ItemFilter.Reload()`, listens to `game.ItemFilter.FilterReloaded` and `game.Lifecycle.AreaEntered`/`AreaLeft`, and displays active filter filename + visible ground item count in `LootToolsPanel.ui`.
5. **Camera Zoom Override (`LootToolsPanel.ui`)**
   - Optional configurable camera zoom override via `game.Camera.SetZoomOverride(...)`, automatically restored to stock (`null` / `-1.0f`) when toggled off or when the mod unloads cleanly from its collectible `AssemblyLoadContext`.
