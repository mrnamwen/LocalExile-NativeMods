namespace LocalExile.Client.Sdk.Engine;

/// <summary>
/// Snapshot of the client's active item-filter configuration and runtime highlight state.
/// </summary>
/// <param name="FilterName">Active item filter filename or display name (empty when no filter is selected).</param>
/// <param name="FilterFolder">Folder containing local <c>.filter</c> files (typically <c>Documents\My Games\Path of Exile</c>).</param>
/// <param name="FilterPath">Resolved path or source name of the currently loaded item filter.</param>
/// <param name="IsLoaded"><c>true</c> when an active item filter is loaded and valid.</param>
/// <param name="HideFilteredGroundItems"><c>true</c> when the "Hide Filtered Ground Items" client option is enabled.</param>
/// <param name="AltHighlightActive"><c>true</c> when the player is holding the item-highlight key (Alt) to temporarily reveal hidden ground items.</param>
public sealed record ItemFilterState(
    string FilterName,
    string FilterFolder,
    string FilterPath,
    bool IsLoaded,
    bool HideFilteredGroundItems,
    bool AltHighlightActive)
{
    /// <summary>Default unloaded item-filter state snapshot.</summary>
    public static ItemFilterState Empty { get; } = new(
        FilterName: string.Empty,
        FilterFolder: string.Empty,
        FilterPath: string.Empty,
        IsLoaded: false,
        HideFilteredGroundItems: false,
        AltHighlightActive: false);
}

/// <summary>
/// Result of evaluating an item or world item against the active client item filter (<c>ItemFilterStyle</c>).
/// </summary>
/// <param name="Evaluated"><c>true</c> if a filter style was evaluated or read from the entity's cached style.</param>
/// <param name="IsVisible"><c>true</c> if the item is shown by the active filter (or never-hidden); <c>false</c> if hidden.</param>
/// <param name="FontSize">Configured label font size (0 if unspecified).</param>
/// <param name="TextRgba">Packed RGBA text color (<c>R | (G &lt;&lt; 8) | (B &lt;&lt; 16) | (A &lt;&lt; 24)</c>).</param>
/// <param name="BorderRgba">Packed RGBA border color.</param>
/// <param name="BackgroundRgba">Packed RGBA background color.</param>
/// <param name="AlertSoundId">Alert sound identifier, or <c>-1</c> if none.</param>
/// <param name="MinimapIconShape">Minimap icon shape/index, or <c>-1</c> if none.</param>
/// <param name="MinimapIconColor">Minimap icon color index, or <c>-1</c> if none.</param>
/// <param name="MinimapIconSize">Minimap icon size index, or <c>-1</c> if none.</param>
/// <param name="BeamColor">Drop beam effect color/row index, or <c>-1</c> if none.</param>
/// <param name="BeamTemporary"><c>true</c> if the drop beam is temporary rather than permanent.</param>
/// <param name="LabelRect">Current screen-space label rectangle if a ground label is active; otherwise <c>null</c>.</param>
public sealed record ItemFilterResult(
    bool Evaluated,
    bool IsVisible,
    int FontSize,
    uint TextRgba,
    uint BorderRgba,
    uint BackgroundRgba,
    int AlertSoundId,
    int MinimapIconShape,
    int MinimapIconColor,
    int MinimapIconSize,
    int BeamColor,
    bool BeamTemporary,
    UiRect? LabelRect = null)
{
    /// <summary>Default unevaluated item-filter result.</summary>
    public static ItemFilterResult Unevaluated { get; } = new(
        Evaluated: false,
        IsVisible: false,
        FontSize: 0,
        TextRgba: 0u,
        BorderRgba: 0u,
        BackgroundRgba: 0u,
        AlertSoundId: -1,
        MinimapIconShape: -1,
        MinimapIconColor: -1,
        MinimapIconSize: -1,
        BeamColor: -1,
        BeamTemporary: false,
        LabelRect: null);
}

/// <summary>
/// Event payload delivered on the game thread when the client finishes loading, reloading, or clearing an item filter.
/// </summary>
/// <param name="Succeeded"><c>true</c> if the item filter loaded cleanly without parse errors.</param>
/// <param name="StatusMessage">Optional status or error message reported by the client filter loader.</param>
/// <param name="State">Updated item-filter state snapshot.</param>
public sealed record ItemFilterReloadedEventArgs(
    bool Succeeded,
    string StatusMessage,
    ItemFilterState State);

/// <summary>
/// Exposes the client's active item-filter state, ground-loot visibility and styling, on-demand item evaluation, and reload notifications.
/// </summary>
public interface IItemFilterService
{
    /// <summary>Reads the current active item-filter configuration and highlight state.</summary>
    ItemFilterState GetState();

    /// <summary>Triggers a game-thread reload of the currently selected item filter.</summary>
    /// <returns><c>true</c> if the reload request was dispatched; otherwise <c>false</c>.</returns>
    bool Reload();

    /// <summary>Returns whether the specified ground world-item entity is currently visible under the active item filter.</summary>
    /// <param name="entityId">World entity identifier of the <c>WorldItem</c>.</param>
    bool IsWorldItemVisible(uint entityId);

    /// <summary>Reads the cached filter visibility, styling, and optional ground label rectangle for a world-item entity.</summary>
    /// <param name="entityId">World entity identifier of the <c>WorldItem</c>.</param>
    ItemFilterResult GetWorldItemResult(uint entityId);

    /// <summary>Enumerates all currently filter-visible ground items in the active area, optionally bounded by distance from the local player.</summary>
    /// <param name="maxDistance">Optional maximum 3D distance in world units from the local player.</param>
    IReadOnlyList<WorldEntitySnapshot> GetVisibleGroundItems(float? maxDistance = null);

    /// <summary>Evaluates an item or world-item entity directly against the active item filter on the game thread.</summary>
    /// <param name="entityId">Entity identifier to evaluate.</param>
    ItemFilterResult EvaluateItem(uint entityId);

    /// <summary>Raised on the game thread whenever the client completes an item-filter load, reload, or clear.</summary>
    event Action<ItemFilterReloadedEventArgs>? FilterReloaded;
}
