namespace LocalExile.Client.Sdk.Engine;

/// <summary>
/// Path of Exile item rarity tier (<c>ModsComponent + 0xb4</c>).
/// </summary>
public enum ItemRarity
{
    /// <summary>Normal (white) item.</summary>
    Normal = 0,

    /// <summary>Magic (blue) item.</summary>
    Magic = 1,

    /// <summary>Rare (yellow) item.</summary>
    Rare = 2,

    /// <summary>Unique (brown/orange) item.</summary>
    Unique = 3,

    /// <summary>Unknown or uninitialized rarity.</summary>
    Unknown = 255,
}

/// <summary>
/// Active mode of PoE's in-game cursor (<c>Cursor + 0x588</c>).
/// </summary>
public enum CursorMode
{
    /// <summary>Cursor is empty (not holding or using an item).</summary>
    Empty = 0,

    /// <summary>Cursor is holding an item picked up from an inventory or stash.</summary>
    HoldingItem = 1,

    /// <summary>Cursor is in currency/scroll use mode (for example Chaos/Alteration rolling).</summary>
    UsingItem = 2,
}

/// <summary>
/// Snapshot of the inventory/stash item currently under the mouse cursor.
/// </summary>
/// <param name="EntityHandle">Native <c>ItemEntity*</c> pointer.</param>
/// <param name="ControlHandle">Native <c>InventoryItemView*</c> control pointer.</param>
/// <param name="ItemId">Server/client item identifier within its inventory.</param>
/// <param name="InventoryId">Owning inventory identifier (<c>InventoryGrid + 0x5c0</c>).</param>
/// <param name="GridX">Top-left X cell coordinate inside the inventory grid.</param>
/// <param name="GridY">Top-left Y cell coordinate inside the inventory grid.</param>
/// <param name="Width">Item width in grid cells.</param>
/// <param name="Height">Item height in grid cells.</param>
/// <param name="MetadataPath"><c>BaseItemTypes</c> metadata path (for example <c>Metadata/Items/Currency/CurrencyRerollRare</c>).</param>
/// <param name="BaseName">Localized base item display name.</param>
/// <param name="StackSize">Current stack count (1 for non-stackable items).</param>
/// <param name="MaxStackSize">Maximum stack size (1 for non-stackable items).</param>
/// <param name="Rarity">Item rarity tier.</param>
/// <param name="IsIdentified"><c>true</c> if the item is identified.</param>
/// <param name="IsCorrupted"><c>true</c> if the item is corrupted.</param>
/// <param name="ItemLevel">Item level (<c>ModsComponent + 0x2b8</c>), or <c>0</c> if unavailable.</param>
/// <param name="ItemClassId">1-based <c>ItemClasses</c> identifier, or <c>0</c> if unavailable.</param>
public sealed record HoveredItemInfo(
    nint EntityHandle,
    nint ControlHandle,
    uint ItemId,
    uint InventoryId,
    int GridX,
    int GridY,
    int Width,
    int Height,
    string MetadataPath,
    string BaseName,
    uint StackSize,
    uint MaxStackSize,
    ItemRarity Rarity,
    bool IsIdentified,
    bool IsCorrupted,
    int ItemLevel = 0,
    uint ItemClassId = 0);

/// <summary>
/// Snapshot of the item currently held on the cursor or active in currency use mode.
/// </summary>
/// <param name="Mode">Active cursor mode (<see cref="CursorMode.HoldingItem"/> or <see cref="CursorMode.UsingItem"/>).</param>
/// <param name="EntityHandle">Native <c>ItemEntity*</c> pointer (may be <see cref="nint.Zero"/> if unavailable).</param>
/// <param name="ItemId">Item identifier in the cursor or source inventory.</param>
/// <param name="SourceInventoryId">Source inventory identifier for held or currency-use items.</param>
/// <param name="SourceGridX">Source X grid cell when in <see cref="CursorMode.UsingItem"/> mode.</param>
/// <param name="SourceGridY">Source Y grid cell when in <see cref="CursorMode.UsingItem"/> mode.</param>
/// <param name="Width">Item width in grid cells.</param>
/// <param name="Height">Item height in grid cells.</param>
/// <param name="MetadataPath"><c>BaseItemTypes</c> metadata path.</param>
/// <param name="BaseName">Localized base item display name.</param>
/// <param name="StackSize">Current stack size.</param>
/// <param name="MaxStackSize">Maximum stack size.</param>
/// <param name="Rarity">Item rarity tier.</param>
/// <param name="IsIdentified"><c>true</c> if identified.</param>
/// <param name="IsCorrupted"><c>true</c> if corrupted.</param>
/// <param name="ShiftKeep"><c>true</c> if Shift is keeping currency use mode active.</param>
public sealed record CursorItemInfo(
    CursorMode Mode,
    nint EntityHandle,
    uint ItemId,
    uint SourceInventoryId,
    int SourceGridX,
    int SourceGridY,
    int Width,
    int Height,
    string MetadataPath,
    string BaseName,
    uint StackSize,
    uint MaxStackSize,
    ItemRarity Rarity,
    bool IsIdentified,
    bool IsCorrupted,
    bool ShiftKeep = false);

/// <summary>
/// Event payload raised when the hovered UI control, hovered item, or cursor item changes.
/// </summary>
/// <param name="Control">Currently hovered <see cref="IUiControl"/>, or <c>null</c>.</param>
/// <param name="Item">Currently hovered inventory/stash item, or <c>null</c>.</param>
/// <param name="CursorItem">Item currently held or active on the cursor, or <c>null</c>.</param>
public sealed record HoverChangedEventArgs(
    IUiControl? Control,
    HoveredItemInfo? Item,
    CursorItemInfo? CursorItem);

/// <summary>
/// Inspects the currently hovered UI control, hovered inventory/stash item, and cursor-attached item.
/// </summary>
public interface IHoverService
{
    /// <summary>Most recently observed hovered UI control, or <c>null</c>.</summary>
    IUiControl? HoveredControl { get; }

    /// <summary>Most recently observed hovered inventory/stash item, or <c>null</c>.</summary>
    HoveredItemInfo? HoveredItem { get; }

    /// <summary>Most recently observed item held on the cursor or active in currency use mode, or <c>null</c>.</summary>
    CursorItemInfo? CursorItem { get; }

    /// <summary>Forces an immediate native poll of the hovered item (for example inside a low-level input handler).</summary>
    HoveredItemInfo? RefreshHoveredItem();

    /// <summary>Forces an immediate native poll of the cursor item.</summary>
    CursorItemInfo? RefreshCursorItem();

    /// <summary>Raised on the game thread when the hovered control, hovered item, or cursor item changes.</summary>
    event Action<HoverChangedEventArgs>? HoverChanged;
}
