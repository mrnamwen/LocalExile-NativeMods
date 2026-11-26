namespace LocalExile.Client.Sdk.Engine;

/// <summary>
/// Configuration options for creating a slab-safe composite <see cref="IUiItemGrid"/>.
/// </summary>
/// <param name="Columns">Number of horizontal grid cells (&gt; 0).</param>
/// <param name="Rows">Number of vertical grid cells (&gt; 0).</param>
/// <param name="CellSize">Pixel width and height of a single 1x1 cell (&gt; 0).</param>
/// <param name="CellPadding">Optional pixel gap between adjacent cells (&gt;= 0).</param>
/// <param name="CellBackgroundTexture">Optional virtual texture path applied to each cell background.</param>
/// <param name="ShowTooltipsOnHover">When <c>true</c>, hovering an occupied cell automatically displays its tooltip via <see cref="ITooltipService"/>.</param>
public sealed record UiItemGridOptions(
    int Columns,
    int Rows,
    float CellSize = 48f,
    float CellPadding = 2f,
    string? CellBackgroundTexture = null,
    bool ShowTooltipsOnHover = true);

/// <summary>
/// Item descriptor placed inside an <see cref="IUiItemGrid"/> supporting multi-cell <c>(Width, Height)</c> footprints.
/// </summary>
/// <param name="ItemId">Unique identifier for this item within the grid.</param>
/// <param name="Col">Zero-based top-left column coordinate.</param>
/// <param name="Row">Zero-based top-left row coordinate.</param>
/// <param name="Width">Horizontal footprint in grid cells (&gt;= 1).</param>
/// <param name="Height">Vertical footprint in grid cells (&gt;= 1).</param>
/// <param name="IconPath">Optional virtual texture path for the item's icon (<c>IPicture</c>).</param>
/// <param name="StackSize">Displayed stack count (shown when &gt; 1).</param>
/// <param name="Rarity">Item rarity tier controlling border/tooltip header styling.</param>
/// <param name="Title">Optional display title shown in hover tooltips.</param>
/// <param name="TooltipLines">Optional custom tooltip lines displayed on hover.</param>
/// <param name="Tag">Optional mod-defined state payload associated with this item.</param>
public sealed record UiGridItem(
    string ItemId,
    int Col,
    int Row,
    int Width = 1,
    int Height = 1,
    string? IconPath = null,
    int StackSize = 1,
    ItemRarity Rarity = ItemRarity.Normal,
    string? Title = null,
    IReadOnlyList<TooltipLine>? TooltipLines = null,
    object? Tag = null);

/// <summary>
/// Event payload raised when a cell in an <see cref="IUiItemGrid"/> is clicked or hovered.
/// </summary>
/// <param name="Col">Zero-based cell column coordinate.</param>
/// <param name="Row">Zero-based cell row coordinate.</param>
/// <param name="Item">Item occupying <c>(Col, Row)</c>, or <c>null</c> if the cell is empty.</param>
/// <param name="Modifiers">Keyboard modifiers active during the interaction.</param>
public sealed record UiGridCellEventArgs(
    int Col,
    int Row,
    UiGridItem? Item,
    KeyModifiers Modifiers = KeyModifiers.None);

/// <summary>
/// Clean, slab-safe composite item grid built atop native <c>Frame</c>/<c>Picture</c>/<c>Label</c>/<c>Button</c>
/// controls and <see cref="ITooltipService"/> with zero server <c>InventorySet</c> coupling.
/// </summary>
public interface IUiItemGrid : IUiControl, IDisposable
{
    /// <summary>Number of horizontal columns in this grid.</summary>
    int Columns { get; }

    /// <summary>Number of vertical rows in this grid.</summary>
    int Rows { get; }

    /// <summary>Pixel size of a single 1x1 grid cell.</summary>
    float CellSize { get; }

    /// <summary>Snapshot of all items currently placed in this grid.</summary>
    IReadOnlyCollection<UiGridItem> Items { get; }

    /// <summary>
    /// Returns <c>true</c> if an item of size <c>(width, height)</c> fits within bounds at <c>(col, row)</c>
    /// without overlapping any existing item (optionally ignoring <paramref name="ignoreItemId"/>).
    /// </summary>
    bool CanPlaceItem(int col, int row, int width = 1, int height = 1, string? ignoreItemId = null);

    /// <summary>
    /// Places <paramref name="item"/> into the grid after validating bounds, unique <see cref="UiGridItem.ItemId"/>,
    /// and non-overlapping cell occupancy.
    /// </summary>
    /// <param name="item">Item descriptor to place.</param>
    /// <returns><c>true</c> if placed; <c>false</c> if out of bounds, overlapping, or duplicate id.</returns>
    bool PlaceItem(UiGridItem item);

    /// <summary>Removes the item with <paramref name="itemId"/> and destroys its visual controls.</summary>
    /// <returns><c>true</c> if the item existed and was removed.</returns>
    bool RemoveItem(string itemId);

    /// <summary>Removes all items from the grid and destroys their visual controls.</summary>
    void Clear();

    /// <summary>Returns the item occupying <c>(col, row)</c> (including multi-cell footprints), or <c>null</c> if empty or out of bounds.</summary>
    UiGridItem? GetItemAt(int col, int row);

    /// <summary>Returns the placed item matching <paramref name="itemId"/>, or <c>null</c> if absent.</summary>
    UiGridItem? GetItemById(string itemId);

    /// <summary>Finds the first top-left <c>(Col, Row)</c> coordinate in row-major order that can fit <c>(width, height)</c>, or <c>null</c> if full.</summary>
    (int Col, int Row)? FindFreeCell(int width = 1, int height = 1);

    /// <summary>Raised when a grid cell is left-clicked (without Ctrl).</summary>
    event Action<UiGridCellEventArgs>? CellClicked;

    /// <summary>Raised when a grid cell is right-clicked.</summary>
    event Action<UiGridCellEventArgs>? CellRightClicked;

    /// <summary>Raised when a grid cell is Ctrl+left-clicked.</summary>
    event Action<UiGridCellEventArgs>? CellCtrlClicked;

    /// <summary>Raised when the pointer hovers a grid cell.</summary>
    event Action<UiGridCellEventArgs>? CellHovered;

    /// <summary>Programmatically dispatches a cell click event (primarily for deterministic testing and automation).</summary>
    void TriggerCellClick(int col, int row, MouseButton button = MouseButton.Left, KeyModifiers modifiers = KeyModifiers.None);

    /// <summary>Programmatically dispatches a cell hover event and updates the hover tooltip if enabled.</summary>
    void TriggerCellHover(int col, int row);
}
