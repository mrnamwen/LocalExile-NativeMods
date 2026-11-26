namespace LocalExile.Client.Sdk.Engine;

/// <summary>
/// Built-in 3.29 native UI control kinds supported for programmatic creation via <see cref="IUiService.CreateControl"/>.
/// All controls are allocated through the engine's slab-pooled factories so <see cref="IUiControl.Destroy"/>
/// releases them safely through <c>Control::ReleaseOrDestroy</c>.
/// </summary>
public enum UiControlKind
{
    /// <summary>Generic container frame (<c>UI::Controls::Frame</c>).</summary>
    Frame = 0,

    /// <summary>Text display label (<c>UI::Controls::Label</c>).</summary>
    Label = 1,

    /// <summary>Interactive push button (<c>UI::Controls::Button</c>).</summary>
    Button = 2,

    /// <summary>Editable text box (<c>UI::Controls::TextBox</c>).</summary>
    TextBox = 3,

    /// <summary>Toggleable check box (<c>UI::Controls::CheckBox</c>).</summary>
    CheckBox = 4,

    /// <summary>Texture/image display control (<c>UI::Controls::Picture</c>).</summary>
    Picture = 5,

    /// <summary>Linear vertical/horizontal stack layout (<c>Layout::StackLayout</c>).</summary>
    StackLayout = 6,

    /// <summary>Scrollable viewport container (<c>UI::Controls::ScrollableFrame</c>).</summary>
    ScrollableFrame = 7,

    /// <summary>Multi-tab container (<c>UI::Controls::TabContainer</c>).</summary>
    TabContainer = 8,

    /// <summary>Dropdown selection combo box (<c>UI::Controls::ComboBox</c>).</summary>
    ComboBox = 9,

    /// <summary>Scrollable selectable control list (<c>UI::Controls::ControlListBox</c>).</summary>
    ListBox = 10,
}

/// <summary>
/// Native image/texture display control (<c>Picture</c> in <c>.ui</c>).
/// Supports loading any stock or Route-A overlaid <c>.dds</c> / <c>Art/2DArt/UIImages/...</c> texture path.
/// </summary>
public interface IPicture : IUiControl
{
    /// <summary>Most recently assigned texture path, or <c>null</c> if none is set.</summary>
    string? TexturePath { get; }

    /// <summary>
    /// Binds a stock or overlaid virtual texture path to this picture control.
    /// </summary>
    /// <param name="texturePath">Virtual texture path (for example <c>Art/2DArt/UIImages/InGame/Icon.dds</c>).</param>
    /// <param name="resizeToImage">When <c>true</c>, resizes the control to match the loaded texture's native dimensions.</param>
    void SetTexture(string texturePath, bool resizeToImage = false);
}

/// <summary>
/// Scrollable viewport frame (<c>ScrollableFrame</c> in <c>.ui</c>).
/// </summary>
public interface IScrollableFrame : IUiControl
{
    /// <summary>Current vertical scroll offset.</summary>
    float ScrollOffset { get; }

    /// <summary>Maximum vertical scroll offset.</summary>
    float MaxScrollOffset { get; }

    /// <summary>Container control that holds scrollable child controls.</summary>
    IUiControl ContentContainer { get; }

    /// <summary>Sets the current vertical scroll offset (clamped to <c>[0, MaxScrollOffset]</c>).</summary>
    /// <param name="offset">Desired vertical scroll offset.</param>
    void SetScrollOffset(float offset);

    /// <summary>Updates the scrollable range and refreshes native scrollbar thumb bounds.</summary>
    /// <param name="maxScrollOffset">Maximum scroll offset (&gt;= 0).</param>
    void SetMaxScrollOffset(float maxScrollOffset) { }

    /// <summary>Recomputes native content bounds and scrollbar layout.</summary>
    void UpdateContentBounds() => UpdateLayout();
}

/// <summary>
/// Multi-tab container control (<c>TabContainer</c> in <c>.ui</c>).
/// </summary>
public interface ITabContainer : IUiControl
{
    /// <summary>Total number of tabs currently in this container.</summary>
    int TabCount { get; }

    /// <summary>Zero-based index of the currently active tab, or <c>-1</c> if no tab is active.</summary>
    int ActiveTabIndex { get; }

    /// <summary>Selects the tab at <paramref name="index"/> and raises <see cref="ActiveTabChanged"/> if changed.</summary>
    /// <param name="index">Zero-based tab index.</param>
    void SelectTab(int index);

    /// <summary>
    /// Appends a new tab with the given caption and optional icon, returning the tab's content container control.
    /// </summary>
    /// <param name="title">Display caption for the tab header.</param>
    /// <param name="iconPath">Optional virtual texture path for the tab header icon.</param>
    /// <param name="hidden">Whether the newly added tab starts hidden.</param>
    /// <returns>The content container <see cref="IUiControl"/> for the new tab.</returns>
    IUiControl AddTab(string title, string? iconPath = null, bool hidden = false);

    /// <summary>Removes all tabs from this container.</summary>
    void ClearTabs();

    /// <summary>Raised on the game thread when the active tab index changes.</summary>
    event Action<int>? ActiveTabChanged;
}

/// <summary>
/// Dropdown selection combo box control (<c>ComboBox</c> in <c>.ui</c>).
/// </summary>
public interface IComboBox : IUiControl
{
    /// <summary>Number of items currently in the dropdown.</summary>
    int ItemCount { get; }

    /// <summary>Gets or sets the zero-based selected item index, or <c>-1</c> when nothing is selected.</summary>
    int SelectedIndex { get; set; }

    /// <summary>Gets the text of the currently selected item, or <c>null</c> if none is selected.</summary>
    string? SelectedText { get; }

    /// <summary>Ordered item display strings currently populated in the combo box.</summary>
    IReadOnlyList<string> Items { get; }

    /// <summary>
    /// Appends a selectable item to the dropdown and returns its zero-based index.
    /// </summary>
    /// <param name="text">Item display text.</param>
    /// <param name="enabled">Whether the item is selectable.</param>
    /// <param name="tag">Optional user-defined 32-bit tag.</param>
    /// <returns>Zero-based index of the added item.</returns>
    int AddItem(string text, bool enabled = true, uint tag = 0);

    /// <summary>Removes all items and resets <see cref="SelectedIndex"/> to <c>-1</c>.</summary>
    void ClearItems();

    /// <summary>Raised on the game thread when <see cref="SelectedIndex"/> changes.</summary>
    event Action<int>? SelectionChanged;
}

/// <summary>
/// Scrollable selectable list of child controls (<c>ControlListBox</c> in <c>.ui</c>).
/// </summary>
public interface IListBox : IUiControl
{
    /// <summary>Number of items currently in the list box.</summary>
    int ItemCount { get; }

    /// <summary>Gets or sets the zero-based selected item index, or <c>-1</c> when no item is selected.</summary>
    int SelectedIndex { get; set; }

    /// <summary>Ordered child item controls currently in the list box.</summary>
    IReadOnlyList<IUiControl> Items { get; }

    /// <summary>
    /// Inserts <paramref name="child"/> at <paramref name="index"/> (clamped to <see cref="ItemCount"/> for append).
    /// </summary>
    /// <param name="index">Zero-based insertion index.</param>
    /// <param name="child">Child control representing the list row.</param>
    /// <param name="userData">Optional 64-bit user data stored alongside the native list entry.</param>
    /// <returns><c>true</c> if the item was inserted.</returns>
    bool InsertItem(int index, IUiControl child, ulong userData = 0);

    /// <summary>Appends <paramref name="child"/> to the end of the list box.</summary>
    bool AddItem(IUiControl child, ulong userData = 0) => InsertItem(ItemCount, child, userData);

    /// <summary>Removes all items and clears the selection.</summary>
    void ClearItems();

    /// <summary>Clears the current selection, setting <see cref="SelectedIndex"/> to <c>-1</c>.</summary>
    void ClearSelection();

    /// <summary>Raised on the game thread when <see cref="SelectedIndex"/> changes.</summary>
    event Action<int>? SelectionChanged;
}
