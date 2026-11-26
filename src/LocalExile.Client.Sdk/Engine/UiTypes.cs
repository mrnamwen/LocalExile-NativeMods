using LocalExile.Client.Sdk.Extend;

namespace LocalExile.Client.Sdk.Engine;

/// <summary>
/// Screen-space rectangle describing a UI control's position and dimensions in 3.29 UI coordinates.
/// </summary>
/// <param name="X">Absolute or layout X coordinate.</param>
/// <param name="Y">Absolute or layout Y coordinate.</param>
/// <param name="Width">Control width.</param>
/// <param name="Height">Control height.</param>
public readonly record struct UiRect(float X, float Y, float Width, float Height);

/// <summary>
/// Provides UTF-8/UTF-16 clipboard access through the Win32 clipboard (or simulated host buffer).
/// </summary>
public interface IClipboardService
{
    /// <summary>Reads the current text on the clipboard, or <c>null</c> if unavailable.</summary>
    string? GetText();

    /// <summary>Writes <paramref name="text"/> onto the clipboard.</summary>
    /// <param name="text">Text to place on the clipboard.</param>
    void SetText(string text);
}

/// <summary>
/// Base abstraction for a node in the client's native <c>.ui</c> control tree.
/// </summary>
public interface IUiControl
{
    /// <summary>The control's <c>id</c> attribute (may be empty for anonymous layout controls).</summary>
    string Id { get; }

    /// <summary>The control type name (for example <c>Frame</c>, <c>Label</c>, <c>Button</c>, <c>TextBox</c>, <c>CheckBox</c>).</summary>
    string ControlType { get; }

    /// <summary>The dotted id path of this control relative to its owning window.</summary>
    string Path { get; }

    /// <summary>Gets or sets whether the control is visible.</summary>
    bool Visible { get; set; }

    /// <summary>Gets or sets the control's text or caption.</summary>
    string Text { get; set; }

    /// <summary>Gets the control's current position and size.</summary>
    UiRect Rect { get; }

    /// <summary>Alias for <see cref="Rect"/>.</summary>
    UiRect Bounds => Rect;

    /// <summary>Gets or sets whether the control is enabled for interaction.</summary>
    bool IsEnabled { get; set; }

    /// <summary>Updates the control's relative position.</summary>
    void SetPosition(float x, float y);

    /// <summary>Updates the control's dimensions.</summary>
    void SetSize(float width, float height);

    /// <summary>Attaches a child control under this control.</summary>
    void AddChild(IUiControl child);

    /// <summary>Detaches a child control from this control.</summary>
    bool RemoveChild(IUiControl child);

    /// <summary>Destroys this control, detaching it from its parent and releasing native resources.</summary>
    void Destroy();

    /// <summary>The parent control, or <c>null</c> for a window root.</summary>
    IUiControl? Parent { get; }

    /// <summary>Ordered child controls directly under this control.</summary>
    IReadOnlyList<IUiControl> Children { get; }

    /// <summary>Key-value properties parsed from the <c>.ui</c> layout or set dynamically at runtime.</summary>
    IReadOnlyDictionary<string, string> Properties { get; }

    /// <summary>Reads a raw property value by name, or <c>null</c> if unset.</summary>
    string? GetProperty(string name);

    /// <summary>Sets a raw property value by name.</summary>
    void SetProperty(string name, string value);

    /// <summary>Finds a descendant control by dotted id path (for example <c>body.buttons.plant</c>) or direct id.</summary>
    IUiControl? Find(string idPath);

    /// <summary>Finds a descendant control by dotted id path and casts it to <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">Expected control interface.</typeparam>
    /// <param name="idPath">Dotted id path or descendant id.</param>
    T? Find<T>(string idPath) where T : class, IUiControl;

    /// <summary>Finds a descendant control by dotted id path or throws <see cref="KeyNotFoundException"/> if absent or wrong type.</summary>
    /// <typeparam name="T">Expected control interface.</typeparam>
    /// <param name="idPath">Dotted id path or descendant id.</param>
    T Require<T>(string idPath) where T : class, IUiControl;
}

/// <summary>
/// Interactive push button control (<c>Button</c> in <c>.ui</c>).
/// </summary>
public interface IButton : IUiControl
{
    /// <summary>Gets or sets whether the button responds to clicks.</summary>
    bool Enabled { get; set; }

    /// <summary>Raised on the game thread when the player clicks the button.</summary>
    event Action? Clicked;

    /// <summary>Programmatically triggers the button's click event if enabled and visible.</summary>
    void Click();
}

/// <summary>
/// Text display control (<c>Label</c> in <c>.ui</c>).
/// </summary>
public interface ILabel : IUiControl
{
    /// <summary>Gets or sets the label's foreground color expression (for example <c>Colours.White</c> or <c>rgb(164, 221, 124)</c>).</summary>
    string? ForegroundColor { get; set; }
}

/// <summary>
/// Editable text input control (<c>TextBox</c> in <c>.ui</c>).
/// </summary>
public interface ITextBox : IUiControl
{
    /// <summary>Gets or sets the maximum character length allowed, or 0 for unlimited.</summary>
    int MaxLength { get; set; }

    /// <summary>Raised when the text box content changes.</summary>
    event Action<string>? TextChanged;

    /// <summary>Raised when the user presses Enter inside the text box.</summary>
    event Action<string>? Submitted;

    /// <summary>Programmatically raises <see cref="Submitted"/> with the current text.</summary>
    void Submit();
}

/// <summary>
/// Toggleable check box control (<c>CheckBox</c> in <c>.ui</c>).
/// </summary>
public interface ICheckBox : IUiControl
{
    /// <summary>Gets or sets whether the check box is checked.</summary>
    bool IsChecked { get; set; }

    /// <summary>Alias for <see cref="IsChecked"/>.</summary>
    bool Checked
    {
        get => IsChecked;
        set => IsChecked = value;
    }

    /// <summary>Raised when <see cref="IsChecked"/> changes.</summary>
    event Action<bool>? CheckedChanged;
}

/// <summary>
/// A top-level mod window loaded from a <c>.ui</c> layout file.
/// </summary>
public interface IUiWindow : IUiControl, IDisposable
{
    /// <summary>The virtual game path from which this window was loaded.</summary>
    string SourcePath { get; }

    /// <summary>Gets whether the window is currently open and attached to the UI tree.</summary>
    bool IsOpen { get; }

    /// <summary>Raised when the window is closed.</summary>
    event Action? Closed;

    /// <summary>Reloads this window's control tree from its <see cref="SourcePath"/> overlay.</summary>
    void Reload();

    /// <summary>Closes and detaches this window from the UI tree.</summary>
    void Close();
}

/// <summary>
/// High-level service for loading <c>.ui</c> layouts, managing mod windows, and registering custom UI control types.
/// </summary>
public interface IUiService
{
    /// <summary>Currently open mod windows.</summary>
    IReadOnlyCollection<IUiWindow> OpenWindows { get; }

    /// <summary>Returns the live native UI root control, or <c>null</c> if unavailable.</summary>
    IUiControl? GetRootControl();

    /// <summary>Loads a <c>.ui</c> file through the file overlay and attaches a new isolated window to the active UI root.</summary>
    /// <param name="virtualPath">Virtual path to the <c>.ui</c> layout (for example <c>Metadata/UI/Mods/Garden/Stockpile.ui</c>).</param>
    /// <param name="windowId">Optional explicit window identifier; defaults to the root control id or filename.</param>
    /// <returns>The loaded window handle.</returns>
    IUiWindow Load(string virtualPath, string? windowId = null);

    /// <summary>Loads a <c>.ui</c> layout and attaches it directly as a child of <paramref name="parent"/>.</summary>
    /// <param name="parent">Parent control receiving the loaded child window.</param>
    /// <param name="virtualPath">Virtual path to the <c>.ui</c> layout.</param>
    /// <param name="windowId">Optional explicit window identifier.</param>
    /// <returns>The loaded child window handle.</returns>
    IUiWindow CreateWindowInto(IUiControl parent, string virtualPath, string? windowId = null);

    /// <summary>Retrieves an open window by its identifier, or <c>null</c> if not open.</summary>
    IUiWindow? GetWindow(string windowId);

    /// <summary>Closes an open window by its identifier.</summary>
    /// <returns><c>true</c> if the window existed and was closed.</returns>
    bool CloseWindow(string windowId);

    /// <summary>Reloads an open window from disk/overlay by its identifier.</summary>
    void ReloadWindow(string windowId);

    /// <summary>Registers a custom UI control type usable in <c>.ui</c> layouts.</summary>
    /// <typeparam name="TControl">Custom control implementation with a parameterless constructor.</typeparam>
    /// <param name="controlTypeName">Type name referenced by <c>begin &lt;TypeName&gt;</c> in <c>.ui</c> files.</param>
    IDisposable RegisterControl<TControl>(string controlTypeName) where TControl : class, ICustomUiControl, new();

    /// <summary>Registers a custom UI control factory usable in <c>.ui</c> layouts.</summary>
    /// <param name="controlTypeName">Type name referenced by <c>begin &lt;TypeName&gt;</c> in <c>.ui</c> files.</param>
    /// <param name="factory">Factory producing a new control instance.</param>
    IDisposable RegisterControl(string controlTypeName, Func<ICustomUiControl> factory);
}
