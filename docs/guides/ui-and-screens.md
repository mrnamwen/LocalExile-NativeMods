# UI, Controls, Stock Screens & HUD Toasts

Author native Path of Exile 3.29 `.ui` windows, bind typed controls, attach conflict-free buttons and checkboxes to stock game screens, traverse native UI trees, and display queued HUD toasts.

[← Documentation Home](../README.md) · [UI & Screens API Reference](../api/sdk-ui-screens.md) · [Capabilities & Limits](capabilities-and-limits.md)

---

## 1. Authoring Valid 3.29.3d `.ui` Layouts

Always lint every `.ui` file with `UiLayoutLinter.Lint(...)` before testing in-game (see [Capabilities & Limits — `.ui` Parser Crash Traps](capabilities-and-limits.md#9-slab-pool-control-destroy-and-ui-parser-crash-traps) for `UI002`, `UI006`, `UI008`, `UI010`, `UI013`, and `UI016`).

```text
// Leading comment required before <DEFINITIONS> (avoids UI002 parser error)
<DEFINITIONS>

const $w = 720;

begin Frame
	id = my_panel_window;
	aspect_mode = scaley;
	size = $w, 420;
	float = centre;
	y = 240;
	visible = true;
	mouse_enabled = true;
	propagate_properties = false;
	background = "Art/2DArt/UIImages/Common/Background1", tile;

	begin StackLayout
		id = body;
		auto_size = true, true;
		margin = 24, 20, 24, 20;
		float = centre;

		begin Label
			id = status_label;
			font = Fonts.NormalSC;
			foreground_colour = Colours.White;
			text = "Ready";
		end

		begin Picture
			id = header_divider;
			float = centre;
			source = "Art/2DArt/UIImages/InGame/Divider";
			size = 520, 8;
		end

		// Use a spacer Frame instead of margin on Picture inside StackLayout (avoids UI008 hang!)
		begin Frame
			id = divider_spacer;
			size = 10, 12;
		end

		begin TextBox
			id = amount_box;
			size = 240, 44;
			font = Fonts[ "TextBox" ].Normal;
			caret_colour = Colours.White;
			text = "10";
		end

		begin CheckBox
			id = auto_check;
			size = 420, 40;
			font = Fonts.NormalSC;
			text_side = right;
			text = "Enable Auto Mode";
		end

		begin Button
			id = apply_button;
			size = 220, 44;
			text = "Apply";
		end
	end
end
```

### Layout & Encoding Checklist

- **Encoding:** UTF-16LE with BOM (`0xFF 0xFE`) + CRLF (`\r\n`).
- **Coordinate Space:** Virtual `1600`-high screen coordinate space (scaled dynamically to actual resolution, e.g., `0.45×` at 720p).
- **Content Sizing:** Windows auto-size to content; explicit root sizes may be overridden by child `StackLayout` bounds.
- **Explicit Unique IDs:** Give every ancestor `Frame`/`StackLayout` and every sibling control an explicit unique `id` (`UI016`).

---

## 2. Loading Windows, Typed Controls & Clipboard (`IUiService`)

```csharp
using LocalExile.Client.Sdk.Engine;

IUiWindow window = context.Game.Ui.Load("Metadata/UI/Mods/Feature/Panel.ui", "my_panel");
window.Closed += () => context.Logger.Info("Window closed.");
context.TrackDisposable(window);

var statusLabel = window.Require<ILabel>("status_label");
var amountBox   = window.Require<ITextBox>("amount_box");
var autoCheck   = window.Require<ICheckBox>("auto_check");
var applyButton = window.Require<IButton>("apply_button");

amountBox.MaxLength = 16; // Live native TextBox (+0x338) with clamp sync
applyButton.Clicked += () =>
{
    statusLabel.Text = $"Applied: {amountBox.Text} (auto={autoCheck.IsChecked})";
    context.Net.Send(new ApplyRequest(amountBox.Text, autoCheck.IsChecked));
};

amountBox.TextChanged += text => statusLabel.SetProperty("draft", text);
amountBox.Submitted += text => statusLabel.Text = $"Submitted: {text}";
autoCheck.CheckedChanged += isChecked => statusLabel.SetProperty("auto_enabled", isChecked.ToString());

// Geometry, enable state (Control::SetEnabled bit 13 0x2000), child attach, and Win32 CF_UNICODETEXT clipboard:
UiRect rect = applyButton.Rect;
applyButton.SetPosition(rect.X, rect.Y + 4f);
applyButton.SetSize(220f, 44f);
applyButton.IsEnabled = true;

IUiControl? uiRoot = context.Game.Ui.GetRootControl();
if (uiRoot is not null)
{
    IUiWindow childWin = context.Game.Ui.CreateWindowInto(uiRoot, "Metadata/UI/Mods/Feature/Badge.ui", "my_badge");
    context.TrackDisposable(childWin);
}

IClipboardService clipboard = context.Game.Clipboard;
clipboard.SetText(amountBox.Text);
string? pasted = clipboard.GetText();
```

### Verified 3.29.3d Dynamic UI Behavior

- **Mixed-case vs. SmallCaps (`Button.Text` vs. `Label.Text`):** `Button::SetText` (`+0x3f8` slot 2) updates the button's internal child `Label` (`+0x408`) and lowercases ASCII for PoE's centered SmallCaps button font. For mixed-case, left-aligned dynamic rows (such as Recombinator-style affix pickers in `DualMod.SiphonOrb`), pair a `Button` (`Select` / `Selected`) with an explicit child `Label` (`Fonts.Normal`, `text_align = MiddleLeft`) inside a named row `Frame`.
- **Dynamic Row Visibility:** Setting `rowFrame.Visible = false` invokes `Control::SetVisible` (`0x1418b0a30`), hiding the row, clearing hover/focus/capture on descendants, collapsing the row inside `StackLayout`, and blocking clicks on hidden child buttons.
- **Window Lifecycle & Clean Reopen:** Guard window access with `_window is { IsOpen: true }` and clear stale references in `_window.Closed`. Closing and reopening a `.ui` window reuses the native handle cleanly (resyncing initial visibility/text and replacing per-button click slots with zero duplicate-ID crashes and zero leaked handlers).
- **Slab-Pool Safety:** Native child destruction uses `Control::ReleaseOrDestroy` (`0x18b6810`) for slab-pooled controls (`[control+0x08] != 0`) and validates `ui.vtable_textbox` / `ui.vtable_checkbox` before typed control calls.

---

## 3. Stock-Tree Traversal & Custom Controls

### Traversing Live Stock UI Trees

`context.Game.Ui.GetRootControl()` returns the native root control wrapper. Calling `.Find("...")` or enumerating `.Children` walks live native child vectors (`Control + 0xb8`) across stock PoE screens (`InGameUI`, `InventoryPanel`, `StashPanel`, `LeagueOverlay`, etc.):

```csharp
IUiControl? root = context.Game.Ui.GetRootControl();
IUiControl? searchBox = root?.Find("stash_search_box");
if (searchBox is not null)
{
    context.Logger.Debug($"Found native control at {searchBox.Rect}");
}
```

### Registering a Custom UI Control (`ICustomUiControl` — Live Native `1.1.0`)

Declare the control name in `mod.json` (`"customControls": ["GrowthGauge"]`), then register it before calling `Ui.Load(...)`. In **`1.1.0`** (C ABI `0.5.0`), `IUiControlRegistry` registers natively into PoE's 47-control `g_uiControlFactoryMap` (`0x14475e990`), intercepts custom `key = value;` properties via `ui.parser_parse_property` (`0x1872270`), drives `OnFrame` on the game thread, and cleans up via `ui.control_release_or_destroy` (`0x18b6810`) before slab teardown (using an exact 7-slot MSVC `std::_Func_base` vtable `[3]=_Type/[4]=_Delete_this/[5]=_Get/[6]=dtor`):

```csharp
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;

public sealed class GrowthGauge : ICustomUiControl
{
    private IUiControl? _host;
    private float _target, _rendered;

    public string ControlTypeName => "GrowthGauge";

    public void OnInitialize(IUiControl hostControl) => _host = hostControl;

    public void OnPropertyChanged(string propertyName, string value)
    {
        if (propertyName == "progress" && float.TryParse(value, out float p))
            _target = Math.Clamp(p, 0f, 1f);
    }

    public void OnFrame(float deltaSeconds)
    {
        _rendered += (_target - _rendered) * Math.Min(1f, deltaSeconds * 6f);
        _host?.SetProperty("rendered_progress", _rendered.ToString("0.00"));
    }

    public void OnDestroy() => _host = null;
}

// Inside IClientMod.Load(IClientContext context):
context.TrackDisposable(context.Game.Ui.RegisterControl<GrowthGauge>("GrowthGauge"));
```

---

## 3.5 Dynamic Rich Controls, `IUiItemGrid`, `ITooltipService` & `IStashAffinityService` (`1.1.0`)

Phase 4 (`SdkVersion 1.1.0`, C ABI `0.5.0`) adds programmatic slab-pooled control construction, composite item grids, stock & custom tooltips, and custom Ctrl+Click stash affinity routing (demonstrated end-to-end in `examples/DualMod.IdolsAndGearVault/`):

### Programmatic Rich Controls (`IUiService.Create*`)

All controls created via `CreateControl` / `CreateFrame` / `CreateLabel` / `CreateButton` / `CreatePicture` / `CreateScrollableFrame` / `CreateTabContainer` / `CreateComboBox` / `CreateListBox` are allocated from PoE's slab pool so `Destroy()` releases them safely through `Control::ReleaseOrDestroy` (`0x18b6810`):

```csharp
ITabContainer tabs = context.Game.Ui.CreateTabContainer(body, "vault_tabs");
IUiControl boardTab = tabs.AddTab("Idol Board (32 Cells)");
IUiControl gearTab  = tabs.AddTab("Gear Vault Affinity");
tabs.ActiveTabChanged += idx => context.Logger.Info($"Active tab: {idx}");

IComboBox classPicker = context.Game.Ui.CreateComboBox(gearTab, "gear_class");
classPicker.AddItem("Helmets");
classPicker.AddItem("Body Armours");
classPicker.SelectionChanged += idx => context.Logger.Info($"Selected class: {classPicker.SelectedText}");

IScrollableFrame scroll = context.Game.Ui.CreateScrollableFrame(gearTab, "rules_scroll");
IListBox list = context.Game.Ui.CreateListBox(scroll.ContentContainer, "rules_list");
ILabel row = context.Game.Ui.CreateLabel(list, "Helmets -> Stash Tab 12", "rule_row_0");
list.AddItem(row, userData: 12);
```

### Composite Item Grids (`IUiItemGrid`) — Wishlist #13

`IUiItemGrid` composes native `Frame`/`Picture`/`Label`/`Button` controls with zero server `InventorySet` coupling, multi-cell `(Width, Height)` footprints (`1×1`, `1×2`, `2×1`, `2×2`, `1×3`, `3×1`), occupancy validation, click/right-click/Ctrl+click/hover events, and automatic hover tooltips:

```csharp
IUiItemGrid grid = context.Game.Ui.CreateItemGrid(
    boardTab,
    new UiItemGridOptions(Columns: 6, Rows: 6, CellSize: 48f, CellPadding: 2f, ShowTooltipsOnHover: true),
    id: "idol_board");
context.TrackDisposable(grid);

// Place a 2x2 Idol at (1, 1) with hover tooltip lines:
grid.PlaceItem(new UiGridItem(
    ItemId: "idol-01",
    Col: 1, Row: 1, Width: 2, Height: 2,
    Rarity: ItemRarity.Rare,
    Title: "Major Idol of Harvesting",
    TooltipLines: [new TooltipLine("+12% chance for Sacred Grove", Color: "rgb(136,136,255)")]));

grid.CellClicked += e => context.Logger.Info($"Clicked ({e.Col},{e.Row}): {e.Item?.Title ?? "empty"}");
```

### Stock Item Tooltip Augmentation & Anchored Tooltips (`ITooltipService`)

`context.Game.Tooltips` hooks `ui.item_popup_build_sections` (`0x16c5740`) to append styled lines/separators to stock hovered item tooltips (including Alt-held Advanced Mod Descriptions) and renders NativeMods-owned custom anchored tooltips (`ShowTooltip` / `HideTooltip` / `AttachTooltip`):

```csharp
context.TrackDisposable(context.Game.Tooltips.RegisterItemTooltipAugmenter(ctx =>
{
    if (!ctx.IsAltHeld)
        return [];

    return [TooltipSection.SingleLine("[Affix Tags: Life, Resistance] · Tier Summary", rgbaColor: 0x88CCFFFFu)];
}));

context.TrackDisposable(context.Game.Tooltips.AttachTooltip(row, () =>
    TooltipContent.FromLines("Gear Vault Route", [new TooltipLine("Ctrl+Click routes Helmets to Tab 12")])));
```

### Custom Ctrl+Click Stash Affinity Routing (`IStashAffinityService`) — Wishlist #14

`context.Game.StashAffinity` hooks `ui.stash_get_affinity_targets` (`0xa83460`) and `items.compute_affinity_bitset` (`0x1589b20`), letting mods route Ctrl+Clicked items directly to **any arbitrary stash tab index** in `C→S 0x00E1` (bypassing the stock `0..21` `StashTabAffinityId` cap) or map custom items into stock affinity bits:

```csharp
context.TrackDisposable(context.Game.StashAffinity.RegisterRoute(
    id: "idolsvault.helmets",
    router: ctx => string.Equals(ctx.Item.ItemClass, "Helmets", StringComparison.OrdinalIgnoreCase)
        ? new StashAffinityRoute(TabIndex: 12, ReplaceStockTargets: true)
        : null,
    priority: 10));
```

### Compact 6-Socket Rendering Polish (`1.2.0`)

Phase 5 (`SdkVersion 1.2.0`, C ABI `0.6.0`) hooks `SocketOverlay::SetCellSide` and `SocketOverlay::Rebuild` so 6-socket 1×2 / 2×2 items render cleanly with compact socket sizing instead of overflowing their item frame (auto-reset on mod unload):

```csharp
context.Game.Ui.SetCompactSocketsEnabled(true);
```

---

## 4. Stock Screens & Conflict-Free `ScreenSlot`s (`IScreensService`)

Multiple mods can attach buttons, panels, and toggles to the same stock PoE screen without overlapping coordinates by using the **9 managed auto-flow `ScreenSlot`s** on `context.Game.Screens`:

| `ScreenSlot` | Target Stock Screen / Anchor | Behavior |
|---|---|---|
| `InventoryToolbar` | Horizontal strip above the 12×5 main inventory grid | Auto-hides when switching to the Cosmetics (`K`) tab |
| `StashHeader` | Top header bar of `StashPanel` / `GuildStashPanel` | Visible whenever stash is open |
| `StashFooter` | Bottom footer bar of `StashPanel` (left of search box) | Visible whenever stash is open |
| `MapTabBar` | Active `MapStashPage` toolbar | Visible when a Map stash tab is selected |
| `BlightTabBar` | Active `BlightStashPage` toolbar | Visible when a Blight stash tab is selected |
| `PassiveTreeHeader` | `PassiveShellPC` header toolbar | Visible while passive tree is open |
| `HudTopRight` | Top-right HUD overlay anchor | Auto-shifts left when right side panel or minimap is visible; also anchors `IToastService` |
| `LeagueCardModes` | Mode checkbox `StackLayout` inside each `0xdc35` `LeagueCard` on `LeagueOverlay` | Auto-reinjected on every `LeagueOverlay::Fill`, equalizing widths with HC/SSF/Ruthless inside the `500×905` card budget |
| `CreateCharacterBar` | Bottom bar on `CreateCharacterState` | Visible when `LeagueOverlay` is closed or skipped |

### Attaching Slot Buttons & Observing Panel / Stash Tab State

```csharp
using LocalExile.Client.Sdk.Engine;

// Attach a conflict-free button to the Inventory toolbar:
context.TrackDisposable(context.Game.Screens.RegisterSlotButton(
    ScreenSlot.InventoryToolbar,
    id: "mymod.deposit_all",
    text: "Deposit All",
    onClick: () => context.Net.Send(new DepositAllRequest()),
    options: new SlotAttachmentOptions(Order: 10, Width: 118f, Height: 30f)));

// Observe stock panel open/close (Inventory, Stash, MapStash, BlightStash, PassiveTree, Vendor, etc.):
context.Game.Screens.ScreenChanged += ev =>
{
    if (ev.Panel == GamePanel.Vendor && ev.IsOpen)
        context.Logger.Info("Vendor window opened.");
};

// Observe active stash tab & Map Stash tier (1–17) changes:
context.Game.Screens.ActiveStashTabChanged += ev =>
{
    if (ev.Current is { Type: StashTabType.Map, MapTier: int tier })
        context.Logger.Info($"Active Map Tab '{ev.Current.Name}' (T{tier}, idx={ev.Current.Index})");
};
```

> **Lifecycle Safety:** `ScreensService.SyncSlotAttachments` destroys old native controls cleanly on same-zone panel close or parent switch (`destroyNative: true`) while avoiding double-free on `InGameState::Leave` / `CreateCharacterState::Leave` (`destroyNative: false`). For raw custom parenting outside the 9 slots, `AttachToControl` provides a managed escape hatch.

---

## 5. Queued HUD Toasts (`IToastService`)

`context.Game.Toasts` renders non-blocking, auto-dismissing notifications anchored to `ScreenSlot.HudTopRight` with severity styling (`Info`, `Success`, `Warning`, `Error`), FIFO queuing, configurable `MaxVisibleToasts` and `DefaultDuration`, and per-mod `Clear()` scoping:

```csharp
using LocalExile.Client.Sdk.Engine;

context.Game.Toasts.Show(
    title: "Deposit Complete",
    body: "Moved 14 items to affinity tabs.",
    severity: ToastSeverity.Success);
```

Tier 1 server-only mods can also emit client HUD toasts with zero client DLL via `await session.Phase1.ShowToastAsync(...)` (see [ServerBridge Guide](server-bridge.md)).

---

## Related API Reference

- [`IUiService`, `IUiWindow`, `IUiControl`, `IButton`, `ILabel`, `ITextBox`, `ICheckBox`, `IPicture`, `IScrollableFrame`, `ITabContainer`, `IComboBox`, `IListBox`, `IClipboardService`](../api/sdk-ui-screens.md)
- [`IScreensService`, `ScreenSlot`, `GamePanel`, `StashTabInfo`, `IToastService`](../api/sdk-ui-screens.md)
- [`IUiItemGrid`, `ITooltipService`, `IStashAffinityService`, `IUiControlRegistry`](../api/sdk-net-files-raw-extend.md)
- [`UiLayoutLinter`](../api/waypoint.md#uilayoutlinter)
