# World Entities, Item Filter, Live Stats, Area Lifecycle & Camera

Query live entities in Path of Exile 3.29's `EntityManager`, read and reload the active client item filter, inspect live entity/player stats (including high-ID custom stats `>= 0x10000`), subscribe to rich area transitions, project world-to-screen coordinates, and apply collectible-ALC-safe camera zoom overrides.

[← Documentation Home](../README.md) · [World, Filter, Stats & Camera API Reference](../api/sdk-world-filter-stats-camera.md) · [Capabilities & Limits](capabilities-and-limits.md)

---

## 1. Live World Entities & Ground Loot (`IWorldService`)

`context.Game.World` reads live entities directly from PoE 3.29's `ClientWorld + 0xa18` `EntityManager` dual RB-trees (`+0x10` awake / `+0x20` asleep) across **14 wired components**: `Positioned`, `Render`, `WorldItem`, `Targetable`, `Life`, `ObjectMagicProperties`, `Chest`, `Stats`, `Player`, `NPC`, `Portal`, `AreaTransition`, `MinimapIcon`, and `Transitionable`.

```csharp
using LocalExile.Client.Sdk.Engine;

// 1. Local player snapshot
WorldEntitySnapshot? player = context.Game.World.GetLocalPlayer();

// 2. Filter-visible ground loot within 800 world units
IReadOnlyList<WorldEntitySnapshot> visibleLoot = context.Game.World.GetWorldItems(
    filterVisibleOnly: true,
    maxDistance: 800f);

// 3. Structured query for alive nearby monsters
IReadOnlyList<WorldEntitySnapshot> nearbyAliveMonsters = context.Game.World.GetEntities(
    new WorldEntityQuery(
        RequiredKind: WorldEntityKind.Monster | WorldEntityKind.Alive,
        MaxDistance: 600f,
        MaxResults: 32));

// 4. Lookup single entity or 3D position
WorldEntitySnapshot? boss = context.Game.World.GetEntity(entityId);
WorldPosition3D? pos = context.Game.World.GetEntityPosition(entityId);

// 5. Client-only visual object (raw mimalloc ClientEntity 0x248, EntityId >= 0x40000000, invisible to server)
uint markerId = context.Game.World.SpawnClientObject(
    "Metadata/MiscellaneousObjects/Mods/MyMod/Marker.ot", 120f, 240f, 0f);
// 1.2.0 extended spawn with explicit rotation (radians) and uniform scale:
uint scaledMarkerId = context.Game.World.CreateClientObjectEx(
    "Metadata/MiscellaneousObjects/Mods/MyMod/Altar.ot", 140f, 240f, 0f, rotationRad: 1.57f, scale: 1.25f);
// 1.2.0 live native entity VFX playback:
context.Game.World.PlayEffect(markerId, "Metadata/Effects/Mods/MyMod/Pulse.ao");
context.Game.World.RemoveClientObject(markerId);
context.Game.World.RemoveClientObject(scaledMarkerId);
```

---

## 2. Client Item Filter Bridge (`IItemFilterService`)

`context.Game.ItemFilter` integrates with live `Options::Instance` (`0x14475e9f0`), `WorldItem::IsFilterVisible` (`0x15e5ed0`), `WorldItem::EvalFilterStyle` (`0x15e5f10`), `ItemFilter::Match` (`0xe15970`), `Options::Tick` reload trigger (`+0x542/+0x543`), and `ItemFilter::NotifyLoaded` (`0x1935130`).

> **Item Filter Directory Rule:** Path of Exile 3.29 **always** enumerates `.filter` files from `Documents\My Games\Path of Exile`, ignoring `Client\config.json` subfolder redirection.
> **Color Packing:** `ItemFilterResult.TextRgba`, `BorderRgba`, and `BackgroundRgba` use packed **`0xRRGGBBAA`**.

```csharp
using LocalExile.Client.Sdk.Engine;

ItemFilterState filterState = context.Game.ItemFilter.GetState();
context.Logger.Info($"Active filter: {filterState.FilterName} (loaded={filterState.IsLoaded})");

context.Game.ItemFilter.FilterReloaded += ev =>
{
    context.Game.Toasts.Show(
        "Item Filter",
        $"{ev.State.FilterName} (loaded={ev.Succeeded})",
        ev.Succeeded ? ToastSeverity.Success : ToastSeverity.Warning);
};

// Check filter visibility & rendered style for a ground item:
if (visibleLoot.Count > 0)
{
    uint lootId = visibleLoot[0].EntityId;
    bool shown = context.Game.ItemFilter.IsWorldItemVisible(lootId);
    ItemFilterResult style = context.Game.ItemFilter.GetWorldItemResult(lootId);
    // Live ItemFilter::Match evaluation on demand:
    ItemFilterResult evaluated = context.Game.ItemFilter.EvaluateItem(lootId);
}

// Trigger game-thread reload (Options::Tick -> SelectApply -> NotifyLoaded):
context.Game.ItemFilter.Reload();
```

---

## 3. Live Entity & Player Stats (`IStatsService`)

`context.Game.Stats` reads live `StatsComponent + 0xf0` sorted `vector<pair<int32, int32>>` tables for any entity (`entityId == 0` targets the local player), including **unbounded high-ID custom stats (`>= 0x10000`)** carried over `StatsComponent` (`0x1b3` varint).

```csharp
using LocalExile.Client.Sdk.Engine;

// Local player convenience helpers (entityId == 0):
int maxLife = context.Game.Stats.ReadLocalPlayerStat(statId: 1);
IReadOnlyDictionary<int, int> allPlayerStats = context.Game.Stats.GetLocalPlayerStats();

// High-ID custom stats (>= 0x10000 — bypasses closed 23,362-row Stats.datc64 ceiling!):
if (context.Game.Stats.TryReadStat(entityId: 0u, statId: 0x10001, out int customVal))
{
    context.Logger.Debug($"Custom stat 0x10001 = {customVal}");
}

// Display-only local stat write (invisible to server, overwritten on next server sync):
context.Game.Stats.SetLocalStat(entityId: 0u, statId: 0x10001, value: 42);
```

---

## 4. Live Area Enter / Leave Lifecycle (`IGameLifecycle`)

Wired live to `ClientWorld::InitArea` (10-arg raw inline detour `0x141653ee0`), `InGameState::Leave`, and `AreaLoadingState`:

```csharp
using LocalExile.Client.Sdk.Engine;

context.Game.Lifecycle.AreaEntered += ev =>
{
    AreaInfo? area = ev.Area ?? context.Game.Lifecycle.CurrentArea;
    context.Logger.Info(
        $"Entered {ev.AreaId} ({area?.AreaName}, Act {area?.Act}, L{area?.EffectiveLevel}, " +
        $"town={area?.IsTown}, hideout={area?.IsHideout}, seed={area?.AreaSeed})");
};

context.Game.Lifecycle.AreaLeft += prevAreaId =>
{
    context.Logger.Info($"Left area {prevAreaId}");
};
```

---

## 5. Camera Projection & Collectible-ALC-Safe Zoom Override (`ICameraService`)

`context.Game.Camera` reads `FollowCamera + 0x100` (16-float row-major DirectX view-projection matrix, `v_row * M`, matching `Camera::WorldToScreen` bit-for-bit with `int32_t` viewport offsets) and `CameraController` (`+0x4a8` zoom, `+0x4ac` zoom override, `+0x4b0` zoom target):

```csharp
using LocalExile.Client.Sdk.Engine;

CameraState cam = context.Game.Camera.GetState();
if (player is not null && context.Game.Camera.TryWorldToScreen(player.Position, out float sx, out float sy))
{
    context.Logger.Debug($"Player screen pos: ({sx:0.0}, {sy:0.0}), zoom={cam.Zoom:0.00}");
}

// 1.2.0: project directly into PoE's 1600-high virtual UI coordinate space (for IUiControl.SetPosition):
if (player is not null && context.Game.Camera.TryWorldToUiPosition(player.Position, out float uiX, out float uiY))
{
    markerLabel.SetPosition(uiX, uiY);
}

// Per-mod owner-scoped zoom override stack:
// Automatically restored to stock zoom (-1.0f) on SetZoomOverride(null) OR collectible ALC unload!
context.Game.Camera.SetZoomOverride(1.35f);
```

---

## 6. Blueprint: `DualMod.LootTools` (Zero LocalExile Server Edits)

`examples/DualMod.LootTools/` demonstrates how Phase 2 (`0.4.0`) client services combine with LocalExile's existing public server APIs (`InstanceGround`, `PlayerInventories`, `Game.SendWorldRemoval`, `Game.SendGroundItem`, `Game.PickUpGold`, `Game.TakeGroundStack`, `GameEvents.AreaTick`) with **zero edits to LocalExile core**:

1. **Filter-Aware Loot Magnet (`Alt+M` / `ScreenSlot.HudTopRight`):**
   - Client queries `context.Game.World.GetWorldItems(filterVisibleOnly: true, maxDistance: ...)` and sends only filter-visible `uint[]` entity IDs.
   - Server validates active session, item allocation (`origin.Shared || ReferenceEquals(origin.By, player)`), lockstep admission (`!LockstepClient || WorldOutput.Has(id)`), clamped radius (`<= 80`), batch cap (`<= 64`), and skips quest & start-weapon drops (`IsStartWeapon` + `TutorialObjects.DropKey`).
2. **Zero-Ghost-Label Relocation Invariant (CRITICAL):**
   - **NEVER** send `InstanceProtocol.Teleport` (`0x01B7`) for a `WorldItem` — `InstanceGround` does not update `_cells`, breaking pickup reach and leaving permanent ghost labels.
   - **ALWAYS** relocate atomically via:
     `Ground.Take(id, item)` → `Ground.Add(relocated, origin.By, origin.Shared, justDropped: false)` → `Game.SendWorldRemoval` (`0x02BD`) → `Game.SendGroundItem(..., justDropped: false)` (`0x02BB` + `0x01A9`).
3. **AoE Pickup (`Alt+G`):**
   - Picks up gold via `Game.PickUpGold`, stackables via `Game.TakeGroundStack` (emitting `Game.SendGroundItem` for any `Rest` so `wake: true` is preserved), and non-stackables into `MainInventory` + `ExpandedInventory` (`ExpandedInventoryMode == 1`) with `KeepOnGroundIfNotSaved` and coalesced `0x00BF`; emits `0x0117 InventoryFull` at most once per trigger and reports a summary via `IToastService`.
4. **Atomic Ground Loot Merger (`Alt+R` + throttled `GameEvents.AreaTick` auto-merge):**
   - Groups compatible stacks via strict `StackIdentityKey` + `ComputeNonPlainPayloadHash`, respects `MaxStack` / gold (`65535`) caps, claims all source stacks first via `Ground.Take` (zero dupes under concurrent pickup), and refreshes labels cleanly via `SendWorldRemoval` + `SendGroundItem(justDropped: false)`.

---

## Tier 1 Server-Only Parity (`0.4.0` & `1.2.0`)

Single-`.cs` Roslyn server mods can query all of these Phase 2/4/5 services without shipping a client DLL via `session.Phase2` (`IClientPhase2Session`) and `session.Phase4` (`IClientPhase4Session`) — see [ServerBridge Guide](server-bridge.md#4-tier-1-server-only-parity-030-040-and-120).

---

## Related API Reference

- [`IWorldService`, `WorldEntitySnapshot`, `WorldEntityQuery`, `WorldEntityKind`, `WorldPosition3D`](../api/sdk-world-filter-stats-camera.md)
- [`IItemFilterService`, `ItemFilterState`, `ItemFilterResult`](../api/sdk-world-filter-stats-camera.md)
- [`IStatsService`, `IGameLifecycle`, `AreaInfo`, `AreaEventArgs`](../api/sdk-world-filter-stats-camera.md)
- [`ICameraService`, `CameraState`](../api/sdk-world-filter-stats-camera.md)
- [`IClientPhase2Session`](../api/server-bridge.md#iclientphase2session)
