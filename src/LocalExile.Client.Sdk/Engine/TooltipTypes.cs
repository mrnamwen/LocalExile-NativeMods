namespace LocalExile.Client.Sdk.Engine;

/// <summary>
/// Single styled line inside a stock item tooltip augmentation or custom mod tooltip.
/// </summary>
/// <param name="Text">UTF-8 display text for the line.</param>
/// <param name="Color">Optional <c>.ui</c> color expression (for example <c>Colours.White</c> or <c>rgb(136, 136, 255)</c>).</param>
/// <param name="Centered">Whether the line is horizontally centered.</param>
/// <param name="RgbaColor">Optional packed <c>0xRRGGBBAA</c> color used by native tooltip label injection (<c>0</c> = default stock color).</param>
public sealed record TooltipLine(
    string Text,
    string? Color = null,
    bool Centered = false,
    uint RgbaColor = 0);

/// <summary>
/// Logical section of lines appended to a stock item tooltip or displayed inside a custom tooltip.
/// </summary>
/// <param name="Lines">Ordered text lines rendered inside this section.</param>
/// <param name="PrependSeparator">Whether a native item-popup separator line is inserted before this section.</param>
public sealed record TooltipSection(
    IReadOnlyList<TooltipLine> Lines,
    bool PrependSeparator = true)
{
    /// <summary>Creates a <see cref="TooltipSection"/> from a single line of text.</summary>
    public static TooltipSection SingleLine(string text, string? color = null, bool prependSeparator = true, uint rgbaColor = 0) =>
        new([new TooltipLine(text, color, Centered: false, RgbaColor: rgbaColor)], prependSeparator);
}

/// <summary>
/// Complete custom tooltip payload displayed by <see cref="ITooltipService.ShowTooltip"/> or <see cref="ITooltipService.AttachTooltip"/>.
/// </summary>
/// <param name="Title">Header title line displayed at the top of the tooltip.</param>
/// <param name="Sections">Ordered body sections rendered beneath the title.</param>
/// <param name="TitleColor">Optional explicit color expression for the header title.</param>
/// <param name="Rarity">Item rarity tier controlling default header color when <paramref name="TitleColor"/> is omitted.</param>
public sealed record TooltipContent(
    string Title,
    IReadOnlyList<TooltipSection> Sections,
    string? TitleColor = null,
    ItemRarity Rarity = ItemRarity.Normal)
{
    /// <summary>Creates a <see cref="TooltipContent"/> with a single body section built from <paramref name="lines"/>.</summary>
    public static TooltipContent FromLines(
        string title,
        IEnumerable<TooltipLine> lines,
        ItemRarity rarity = ItemRarity.Normal,
        string? titleColor = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        TooltipLine[] lineArray = [.. lines];
        IReadOnlyList<TooltipSection> sections = lineArray.Length > 0
            ? [new TooltipSection(lineArray, PrependSeparator: true)]
            : [];
        return new TooltipContent(title ?? string.Empty, sections, titleColor, rarity);
    }
}

/// <summary>
/// Context passed to registered stock item tooltip augmenters when the client builds an <c>ItemPopup</c>
/// (<c>ui.item_popup_build_sections</c>), including when the player toggles Alt for Advanced Mod Descriptions.
/// </summary>
/// <param name="Item">Snapshot of the hovered item whose tooltip is being built.</param>
/// <param name="IsAltHeld"><c>true</c> when Alt (Advanced Mod Descriptions) is currently held.</param>
/// <param name="PopupHandle">Native <c>ItemPopup*</c> pointer, or <see cref="nint.Zero"/> when simulated.</param>
/// <param name="FrameStyle">Native item-popup frame style parameter passed to <c>BuildSections</c>.</param>
/// <param name="IsCompact">Whether the popup is building in compact mode.</param>
public sealed record ItemTooltipContext(
    HoveredItemInfo Item,
    bool IsAltHeld,
    nint PopupHandle = 0,
    uint FrameStyle = 0,
    bool IsCompact = false);

/// <summary>
/// Stock item tooltip augmentation (<c>ui.item_popup_build_sections</c>) and NativeMods-owned
/// custom anchored tooltip overlay service (<c>game.Tooltips</c>).
/// </summary>
public interface ITooltipService
{
    /// <summary>Whether the NativeMods custom anchored tooltip overlay is currently visible.</summary>
    bool IsCustomTooltipVisible { get; }

    /// <summary>Currently displayed custom tooltip payload, or <c>null</c> when hidden.</summary>
    TooltipContent? ActiveCustomTooltip { get; }

    /// <summary>Current screen anchor coordinates of the custom tooltip overlay.</summary>
    (float X, float Y) CustomTooltipPosition { get; }

    /// <summary>
    /// Registers a stock item tooltip augmenter invoked on the game thread whenever <c>ItemPopup::BuildSections</c>
    /// builds or rebuilds a hovered item tooltip (including Alt-held Advanced Mod Descriptions rebuilds).
    /// </summary>
    /// <param name="augmenter">Callback returning additional sections to append (or empty/null for no changes).</param>
    /// <param name="priority">Execution priority (lower values run first).</param>
    /// <returns>Disposable handle that unregisters the augmenter.</returns>
    IDisposable RegisterItemTooltipAugmenter(Func<ItemTooltipContext, IReadOnlyList<TooltipSection>> augmenter, int priority = 0);

    /// <summary>
    /// Displays a custom anchored tooltip at screen coordinates <c>(screenX, screenY)</c>.
    /// </summary>
    /// <param name="content">Tooltip title and sections to render.</param>
    /// <param name="screenX">Screen X pixel coordinate.</param>
    /// <param name="screenY">Screen Y pixel coordinate.</param>
    void ShowTooltip(TooltipContent content, float screenX, float screenY);

    /// <summary>Hides the currently visible custom anchored tooltip overlay.</summary>
    void HideTooltip();

    /// <summary>
    /// Binds a dynamic tooltip provider to <paramref name="control"/> so hovering it automatically shows
    /// the returned <see cref="TooltipContent"/> and leaving hides it.
    /// </summary>
    /// <param name="control">Target UI control.</param>
    /// <param name="contentProvider">Callback invoked on hover; returning <c>null</c> keeps the tooltip hidden.</param>
    /// <returns>Disposable handle that detaches the tooltip binding.</returns>
    IDisposable AttachTooltip(IUiControl control, Func<TooltipContent?> contentProvider);
}
