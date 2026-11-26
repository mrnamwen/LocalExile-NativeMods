# Input, Keybinds, Hover & Pre-Game

Intercept and suppress keyboard/mouse chords, register player-remappable keybinds persisted in `Mods/Client/keybinds.json`, inspect hovered and cursor-held items, and inject custom character-creation options into `LeagueOverlay` and `C→S 0x12`.

[← Documentation Home](../README.md) · [Input, Hover & Pre-Game API Reference](../api/sdk-input-hover-pregame.md) · [UI & Screens Guide](ui-and-screens.md)

---

## 1. Suppressible Input & Keybind Registry (`IInputService` / `IKeybindRegistry`)

`context.Game.Input` hooks PoE's Win32 message dispatch (`WndProc` + keyboard/mouse handlers) and exposes:

- **Priority-ordered low-level hooks:** `OnKey` / `OnMouse` handlers that can set `args.Handled = true` to suppress both button-down and the matching button-up event from reaching Path of Exile.
- **Text-input focus guard:** `IsTextInputFocused` inspects PoE's live focus control via `TextBox::AsTextInput` (vtable slot 6 `0x14015f2b0`) so hotkeys never fire while the player is typing in chat, stash search, or a mod `TextBox`.
- **Centralized `KeybindRegistry` (`Mods/Client/keybinds.json`):** Supports both keyboard chords (`Alt+M`, `Ctrl+Shift+F`) and mouse chords (`Ctrl+Shift+RMB`), persists user overrides to `Mods/Client/keybinds.json` (outside `Mods/Binaries`, so zero `ModHost` reload hazard), and exposes live `KeybindConflict` warnings when two mods claim the same chord.

### Pattern: `Ctrl+Shift+RMB` Bulk Currency Use (With Input Suppression)

This is the exact pattern used in `examples/DualMod.StashQol/`:

```csharp
using LocalExile.Client.Sdk.Engine;

context.TrackDisposable(context.Game.Input.Keybinds.Register(
    id: "mymod.bulk_use",
    displayName: "Bulk Use Currency Stack",
    defaultChord: KeyChord.FromMouse(MouseButton.Right, KeyModifiers.Control | KeyModifiers.Shift),
    onTriggered: () =>
    {
        if (context.Game.Input.IsTextInputFocused)
            return;

        HoveredItemInfo? hovered = context.Game.Hover.RefreshHoveredItem();
        if (hovered is { StackSize: > 1 })
        {
            context.Net.Send(new BulkUseRequest(hovered.InventoryId, hovered.ItemId));
        }
    },
    suppressGameInput: true));
```

### Pattern: Low-Level `OnKey` / `OnMouse` Interception

```csharp
context.TrackDisposable(context.Game.Input.OnKey(args =>
{
    if (context.Game.Input.IsTextInputFocused)
        return;

    if (args.Kind == InputEventKind.KeyDown && args.VirtualKey == 0x74) // F5
    {
        args.Handled = true; // Suppress stock F5 behavior
        context.Game.ItemFilter.Reload();
    }
}, priority: 10));
```

---

## 2. Hovered & Cursor Item Inspection (`IHoverService`)

`context.Game.Hover` reads live `PC_HUD::GetCursor` (`+0x488`) / `Console_HUD::GetCursor` (`+0x7e8`), `Cursor::GetHeldItem` (`+0x4e8`), `ModsComponent::EnsureDeserialised` (`+0x2e2` guard), and `BaseStatic+0x68` (`BaseItemTypes` / `ItemClasses`) to expose:

- **`HoveredControl`** (`IUiControl?`): Currently hovered native UI control.
- **`HoveredItem`** (`HoveredItemInfo?`): Live snapshot containing native entity/control handles, server `InventoryId` & `ItemId`, grid coordinates `(GridX, GridY)` and dimensions `(Width, Height)`, `MetadataPath`, `BaseName`, `StackSize`, `MaxStackSize`, `Rarity`, `IsIdentified`, `IsCorrupted`, `ItemLevel`, and `ItemClass`.
- **`CursorItem`** (`CursorItemInfo?`): Live cursor state (`CursorMode.Empty`, `HoldingItem`, `UsingItem`), source inventory/grid, and `ShiftKeep`.
- **On-demand refresh & reactive events:** Call `RefreshHoveredItem()` / `RefreshCursorItem()` right inside a click/keybind callback for zero-latency reads, or subscribe to `HoverChanged`.

```csharp
using LocalExile.Client.Sdk.Engine;

context.Game.Hover.HoverChanged += ev =>
{
    if (ev.HoveredItem is { } item)
    {
        context.Logger.Debug(
            $"Hovering {item.BaseName} ({item.MetadataPath}) " +
            $"inv={item.InventoryId} id={item.ItemId} stack={item.StackSize}/{item.MaxStackSize} ilvl={item.ItemLevel}");
    }
};

CursorItemInfo? cursor = context.Game.Hover.RefreshCursorItem();
if (cursor is { Mode: CursorMode.UsingItem, Item: { } currency })
{
    context.Logger.Info($"Applying currency {currency.BaseName} (shiftKeep={cursor.ShiftKeep})");
}
```

---

## 3. Pre-Game Character Creation (`IPreGameService` + `LeagueCardModes`)

NativeMods lets mods inject checkboxes onto Path of Exile's pre-game **League Selection Overlay (`LeagueOverlay`)** and **Character Creation Bar (`CreateCharacterState`)**, attach structured key-value options onto the outgoing **`C→S 0x12` (`CreateCharacter.S78`)** wire packet, and react on the LocalExile Gateway via **`GameEvents.CharacterCreated`** before the character enters its first area!

### How `CreateCharacter.S78` Option Injection Works

1. On the client, `IScreensService.RegisterLeagueCardMode(...)` injects a checkbox into every `0xdc35` `LeagueCard`'s mode `StackLayout` on every `LeagueOverlay::Fill`, automatically equalizing checkbox widths with HC/SSF/Ruthless inside the `500×905` card layout budget.
2. When the player confirms character creation, `IPreGameService.CharacterCreating` fires on the game thread right before `LoginClient::WriteCreateCharacter_0x12` (`0x142146de0`) serializes the packet.
3. Any options added via `createCtx.SetOption(key, value)` are encoded as `|nm:k1=v1&k2=v2` appended to `CreateCharacter.S78` (max `1024` chars, max `32` options; stock `BaseStartState` is left completely untouched when no mod adds options).
4. On LocalExile (`GameServer.Auth` → `InstanceControl` reverse `gateway_attach` stream), `LoginServerSession.ParseStartState` parses the `|nm:...` options and fires **`GameEvents.CharacterCreated(CharacterCreatedContext)`** in Gateway *before* `S→C 0x13` and `InstanceDirectory.AssignStart`.

### Client Side (`examples/DualMod.SkipCampaign/` Pattern)

```csharp
using LocalExile.Client.Sdk.Engine;

// 1. Register a checkbox on every LeagueOverlay card (auto-reinjected on every LeagueOverlay::Fill)
ILeagueModeRegistration skipMode = context.Game.Screens.RegisterLeagueCardMode(
    new LeagueModeCheckboxOptions(
        Id: "skip_campaign",
        Caption: "Skip Campaign",
        DefaultChecked: false));
context.TrackDisposable(skipMode);

// 2. Optionally attach a companion toggle bar on ScreenSlot.CreateCharacterBar
//    for players who close/skip LeagueOverlay.

// 3. Inject option into C->S 0x12 CreateCharacter.S78:
context.Game.PreGame.CharacterCreating += createCtx =>
{
    if (skipMode.GetChecked(createCtx.League))
    {
        createCtx.SetOption("skip_campaign", "1");
    }
};

// 4. Observe S->C 0x13 create result on the client:
context.Game.PreGame.CharacterCreated += result =>
{
    context.Logger.Info($"CreateCharacter result: succeeded={result.Succeeded} name={result.CharacterName}");
};
```

### Server Side (`LocalExileGatewayHost` + `GameEvents.CharacterCreated`)

```csharp
using LocalExile.Client.ServerBridge;

gatewayHost = LocalExileGatewayHost.Create(modContext, builder => builder
    .OnCharacterCreated(async ctx =>
    {
        if (!ctx.Options.TryGetValue("skip_campaign", out string? val) || val != "1")
            return;

        // Configure endgame spawn, level, quests, waypoints, refund points & Lab ascendancy
        // via LocalExile Part 2a public IPlayerWorld / CharacterCreatedContext APIs:
        ctx.StartingAreaId = "2_11_endgame_town";
        ctx.StartingLevel = 68;
        ctx.SuppressStartKit = true;
        await ApplyCampaignSkipProgressionAsync(ctx);
    }));
```

---

## Tier 1 Server-Only Parity (`0.3.0`)

Single-`.cs` Roslyn server mods can register hotkeys and subscribe to screen/hover changes without shipping a client DLL using `session.Phase1`:
- `RegisterHotkeyAsync` / `OnHotkeyAsync`
- `OnSlotButtonClickAsync`
- `OnScreenChangedAsync` / `OnHoverChangedAsync`
- `ShowToastAsync`

See [ServerBridge Guide](server-bridge.md) and [`IClientPhase1Session`](../api/server-bridge.md#iclientphase1session).

---

## Related API Reference

- [`IInputService`, `IKeybindRegistry`, `KeyChord`, `KeyModifiers`, `MouseButton`](../api/sdk-input-hover-pregame.md)
- [`IHoverService`, `HoveredItemInfo`, `CursorItemInfo`, `CursorMode`](../api/sdk-input-hover-pregame.md)
- [`IPreGameService`, `CharacterCreateContext`, `CharacterCreatedEventArgs`](../api/sdk-input-hover-pregame.md)
- [`ILeagueModeRegistration`, `LeagueModeCheckboxOptions`](../api/sdk-ui-screens.md)
