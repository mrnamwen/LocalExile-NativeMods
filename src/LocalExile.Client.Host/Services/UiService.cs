using System.Globalization;
using System.Text.RegularExpressions;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;

namespace LocalExile.Client.Host.Services;

/// <summary>
/// Registry for custom UI control factories usable from <c>.ui</c> layouts.
/// </summary>
public sealed class UiControlRegistry : IUiControlRegistry
{
    private readonly Dictionary<string, Func<ICustomUiControl>> _factories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <inheritdoc/>
    public IReadOnlyCollection<string> RegisteredControlTypes
    {
        get { lock (_gate) { return [.. _factories.Keys]; } }
    }

    /// <inheritdoc/>
    public IDisposable Register<TControl>(string controlTypeName) where TControl : class, ICustomUiControl, new() =>
        Register(controlTypeName, static () => new TControl());

    /// <inheritdoc/>
    public IDisposable Register(string controlTypeName, Func<ICustomUiControl> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controlTypeName);
        ArgumentNullException.ThrowIfNull(factory);
        lock (_gate)
        {
            if (_factories.ContainsKey(controlTypeName))
            {
                throw new InvalidOperationException($"Custom UI control type '{controlTypeName}' is already registered.");
            }

            _factories[controlTypeName] = factory;
        }

        return new Native.ActionDisposable(() =>
        {
            lock (_gate)
            {
                _factories.Remove(controlTypeName);
            }
        });
    }

    /// <inheritdoc/>
    public bool IsRegistered(string controlTypeName)
    {
        lock (_gate)
        {
            return _factories.ContainsKey(controlTypeName);
        }
    }

    /// <inheritdoc/>
    public ICustomUiControl? CreateInstance(string controlTypeName)
    {
        Func<ICustomUiControl>? factory;
        lock (_gate)
        {
            _factories.TryGetValue(controlTypeName, out factory);
        }

        return factory?.Invoke();
    }
}

/// <summary>Base managed representation of a control inside a loaded <c>.ui</c> tree.</summary>
public class UiControlNode : IUiControl
{
    private readonly List<UiControlNode> _children = [];
    private readonly Dictionary<string, string> _properties = new(StringComparer.OrdinalIgnoreCase);
    private readonly ICustomUiControl? _customBehavior;
    private string _text = string.Empty;
    private bool _visible = true;
    private bool _enabled = true;
    private UiRect _rect;
    private bool _destroyed;
    private bool _customDestroyed;

    internal nint NativeControlHandle { get; set; }
    internal Native.INativeBridge? NativeBridge { get; set; }
    internal UiWindowNode? OwningWindow { get; set; }
    internal bool IsDestroyed => _destroyed;

    /// <summary>Creates a new control node.</summary>
    public UiControlNode(string id, string controlType, UiControlNode? parent, ICustomUiControl? customBehavior = null)
    {
        Id = id;
        ControlType = controlType;
        Parent = parent;
        _customBehavior = customBehavior;
    }

    protected void ThrowIfDestroyed() => ObjectDisposedException.ThrowIf(_destroyed, this);

    /// <inheritdoc/>
    public string Id { get; internal set; }

    /// <inheritdoc/>
    public string ControlType { get; }

    /// <summary>The attached custom control behavior instance, if this node was instantiated from <see cref="UiControlRegistry"/>.</summary>
    public ICustomUiControl? CustomBehavior => _customBehavior;

    /// <inheritdoc/>
    public string Path
    {
        get
        {
            var segments = new Stack<string>();
            UiControlNode? cur = this;
            while (cur is not null && cur.Parent is not null && cur is not UiWindowNode)
            {
                if (!string.IsNullOrEmpty(cur.Id))
                {
                    segments.Push(cur.Id);
                }

                cur = cur.Parent;
            }

            return segments.Count > 0 ? string.Join('.', segments) : Id;
        }
    }

    /// <inheritdoc/>
    public bool Visible
    {
        get
        {
            ThrowIfDestroyed();
            return _visible;
        }
        set
        {
            ThrowIfDestroyed();
            _visible = value;
            _properties["visible"] = value ? "true" : "false";
            if (NativeControlHandle != nint.Zero && NativeBridge is not null)
            {
                NativeBridge.SetNativeUiVisible(NativeControlHandle, value);
            }

            _customBehavior?.OnPropertyChanged("visible", _properties["visible"]);
        }
    }

    /// <inheritdoc/>
    public virtual string Text
    {
        get
        {
            ThrowIfDestroyed();
            return _text;
        }
        set
        {
            ThrowIfDestroyed();
            string normalized = value ?? string.Empty;
            if (string.Equals(_text, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _text = normalized;
            _properties["text"] = _text;
            if (NativeControlHandle != nint.Zero && NativeBridge is not null)
            {
                bool isCaptionButton = SupportsNativeButtonCaption(ControlType);
                if (isCaptionButton || string.Equals(ControlType, "Label", StringComparison.OrdinalIgnoreCase))
                {
                    NativeBridge.SetNativeUiText(NativeControlHandle, _text, isCaptionButton);
                }
            }

            _customBehavior?.OnPropertyChanged("text", _text);
        }
    }

    internal string RawBaseText => _text;

    internal void SetBaseTextSilent(string text)
    {
        _text = text;
        _properties["text"] = text;
    }

    /// <inheritdoc/>
    public UiRect Rect
    {
        get
        {
            ThrowIfDestroyed();
            if (NativeControlHandle != nint.Zero && NativeBridge is not null && NativeBridge.TryGetNativeUiRect(NativeControlHandle, out UiRect nativeRect))
            {
                _rect = nativeRect;
            }

            return _rect;
        }
    }

    /// <inheritdoc/>
    public bool IsEnabled
    {
        get
        {
            ThrowIfDestroyed();
            return _enabled;
        }
        set
        {
            ThrowIfDestroyed();
            _enabled = value;
            _properties["enabled"] = value ? "true" : "false";
            if (NativeControlHandle != nint.Zero && NativeBridge is not null)
            {
                NativeBridge.SetNativeUiEnabled(NativeControlHandle, value);
            }

            _customBehavior?.OnPropertyChanged("enabled", _properties["enabled"]);
        }
    }

    /// <inheritdoc/>
    public void SetPosition(float x, float y)
    {
        ThrowIfDestroyed();
        _rect = _rect with { X = x, Y = y };
        _properties["location"] = string.Create(CultureInfo.InvariantCulture, $"{x}, {y}");
        if (NativeControlHandle != nint.Zero && NativeBridge is not null)
        {
            NativeBridge.SetNativeUiPosition(NativeControlHandle, x, y);
        }
    }

    /// <inheritdoc/>
    public void SetSize(float width, float height)
    {
        ThrowIfDestroyed();
        _rect = _rect with { Width = width, Height = height };
        _properties["size"] = string.Create(CultureInfo.InvariantCulture, $"{width}, {height}");
        if (NativeControlHandle != nint.Zero && NativeBridge is not null)
        {
            NativeBridge.SetNativeUiSize(NativeControlHandle, width, height);
        }
    }

    /// <summary>Typed parent node.</summary>
    public UiControlNode? Parent { get; internal set; }

    IUiControl? IUiControl.Parent => Parent;

    /// <inheritdoc/>
    public IReadOnlyList<IUiControl> Children
    {
        get
        {
            ThrowIfDestroyed();
            return _children;
        }
    }

    internal List<UiControlNode> MutableChildren => _children;

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> Properties => _properties;

    /// <inheritdoc/>
    public string? GetProperty(string name)
    {
        ThrowIfDestroyed();
        return _properties.TryGetValue(name, out string? value) ? value : null;
    }

    /// <inheritdoc/>
    public void SetProperty(string name, string value)
    {
        ThrowIfDestroyed();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (NativeControlHandle != nint.Zero && NativeBridge is not null)
        {
            if (string.Equals(name, "text", StringComparison.OrdinalIgnoreCase))
            {
                Text = value;
                return;
            }

            if (string.Equals(name, "visible", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out bool visibleVal))
            {
                Visible = visibleVal;
                return;
            }

            if (string.Equals(name, "enabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out bool enabledVal))
            {
                IsEnabled = enabledVal;
                return;
            }
        }

        _properties[name] = value;
        if (string.Equals(name, "text", StringComparison.OrdinalIgnoreCase))
        {
            _text = value;
        }
        else if (string.Equals(name, "visible", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out bool v))
        {
            _visible = v;
        }
        else if (string.Equals(name, "enabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out bool en))
        {
            _enabled = en;
        }
        else if (string.Equals(name, "location", StringComparison.OrdinalIgnoreCase) && TryParseFloatPair(value, out float lx, out float ly))
        {
            _rect = _rect with { X = lx, Y = ly };
        }
        else if (string.Equals(name, "size", StringComparison.OrdinalIgnoreCase) && TryParseFloatPair(value, out float sw, out float sh))
        {
            _rect = _rect with { Width = sw, Height = sh };
        }

        _customBehavior?.OnPropertyChanged(name, value);
    }

    private static bool TryParseFloatPair(string raw, out float a, out float b)
    {
        a = 0f;
        b = 0f;
        string[] parts = raw.Split(',', StringSplitOptions.TrimEntries);
        return parts.Length == 2
            && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out a)
            && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out b);
    }

    internal static bool SupportsNativeButtonCaption(string controlType) =>
        string.Equals(controlType, "Button", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(controlType, "GreenButton", StringComparison.OrdinalIgnoreCase);

    internal static bool IsClickableButtonControl(string controlType) =>
        SupportsNativeButtonCaption(controlType) ||
        string.Equals(controlType, "BasicButton", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(controlType, "ImageButton", StringComparison.OrdinalIgnoreCase);

    internal bool IsEffectivelyVisible()
    {
        UiControlNode? cur = this;
        while (cur is not null)
        {
            if (cur._destroyed || !cur._visible)
            {
                return false;
            }

            if (cur is UiWindowNode win && !win.IsOpen)
            {
                return false;
            }

            cur = cur.Parent;
        }

        return true;
    }

    internal void AddChild(UiControlNode child)
    {
        ThrowIfDestroyed();
        child.Parent = this;
        UiWindowNode? owner = (this as UiWindowNode) ?? OwningWindow;
        if (owner is not null)
        {
            AssignOwningWindowRecursive(child, owner);
        }

        _children.Add(child);
    }

    private static void AssignOwningWindowRecursive(UiControlNode node, UiWindowNode owner)
    {
        if (node is UiWindowNode)
        {
            return;
        }

        node.OwningWindow = owner;
        foreach (UiControlNode child in node._children)
        {
            AssignOwningWindowRecursive(child, owner);
        }
    }

    /// <inheritdoc/>
    public void AddChild(IUiControl child)
    {
        ArgumentNullException.ThrowIfNull(child);
        ThrowIfDestroyed();
        if (child is not UiControlNode node)
        {
            throw new ArgumentException("Child control must be created by UiService.", nameof(child));
        }

        node.ThrowIfDestroyed();
        node.Parent?.RemoveChild(node);
        AddChild(node);
    }

    /// <inheritdoc/>
    public bool RemoveChild(IUiControl child)
    {
        ArgumentNullException.ThrowIfNull(child);
        ThrowIfDestroyed();
        if (child is not UiControlNode node || !_children.Remove(node))
        {
            return false;
        }

        string removedPath = node.Path;
        if (NativeControlHandle != nint.Zero && node.NativeControlHandle != nint.Zero && NativeBridge is not null)
        {
            NativeBridge.RemoveNativeUiChild(NativeControlHandle, node.NativeControlHandle);
        }

        node.Parent = null;
        UiWindowNode? owner = (this as UiWindowNode) ?? OwningWindow;
        owner?.NotifyControlSubtreeRemoved(removedPath);
        return true;
    }

    /// <inheritdoc/>
    public void Destroy()
    {
        if (_destroyed)
        {
            return;
        }

        UiWindowNode? owner = (this as UiWindowNode) ?? OwningWindow;
        string? subtreePath = Parent is not null ? Path : null;

        if (Parent is not null)
        {
            if (!Parent.IsDestroyed)
            {
                Parent.RemoveChild(this);
            }
            else
            {
                Parent = null;
            }
        }

        if (this is UiWindowNode win)
        {
            win.MarkClosedOnDestroy();
        }

        DestroyCustomRecursive();

        foreach (UiControlNode child in _children.ToArray())
        {
            child.Destroy();
        }

        _children.Clear();

        if (NativeControlHandle != nint.Zero && NativeBridge is not null)
        {
            NativeBridge.DestroyNativeUiControl(NativeControlHandle);
            NativeControlHandle = nint.Zero;
        }

        _destroyed = true;
        if (owner is not null && !string.IsNullOrEmpty(subtreePath) && owner != this)
        {
            owner.NotifyControlSubtreeRemoved(subtreePath);
        }
    }

    internal void DestroyNativeHandlesRecursive()
    {
        foreach (UiControlNode child in _children.ToArray())
        {
            child.DestroyNativeHandlesRecursive();
        }

        if (NativeControlHandle != nint.Zero && NativeBridge is not null)
        {
            NativeBridge.DestroyNativeUiControl(NativeControlHandle);
            NativeControlHandle = nint.Zero;
        }
    }

    internal void InitializeCustomRecursive()
    {
        _customBehavior?.OnInitialize(this);
        foreach (UiControlNode child in _children.ToArray())
        {
            child.InitializeCustomRecursive();
        }
    }

    internal void TickCustomRecursive(float deltaSeconds)
    {
        _customBehavior?.OnFrame(deltaSeconds);
        foreach (UiControlNode child in _children.ToArray())
        {
            child.TickCustomRecursive(deltaSeconds);
        }
    }

    internal void DestroyCustomRecursive()
    {
        foreach (UiControlNode child in _children.ToArray())
        {
            child.DestroyCustomRecursive();
        }

        if (!_customDestroyed)
        {
            _customDestroyed = true;
            _customBehavior?.OnDestroy();
        }
    }

    /// <inheritdoc/>
    public IUiControl? Find(string idPath)
    {
        ThrowIfDestroyed();
        if (string.IsNullOrWhiteSpace(idPath))
        {
            return null;
        }

        string[] segments = idPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 1)
        {
            return FindBySingleId(this, segments[0]);
        }

        // First try strict dotted traversal from this node (or matching root).
        UiControlNode? current = this;
        int startIndex = 0;
        if (string.Equals(current.Id, segments[0], StringComparison.OrdinalIgnoreCase))
        {
            startIndex = 1;
        }

        for (int i = startIndex; i < segments.Length; i++)
        {
            UiControlNode? next = FindBySingleId(current, segments[i]);
            if (next is null)
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    private static UiControlNode? FindBySingleId(UiControlNode root, string targetId)
    {
        foreach (UiControlNode child in root._children)
        {
            if (string.Equals(child.Id, targetId, StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }
        }

        foreach (UiControlNode child in root._children)
        {
            UiControlNode? nested = FindBySingleId(child, targetId);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    /// <inheritdoc/>
    public T? Find<T>(string idPath) where T : class, IUiControl => Find(idPath) as T;

    /// <inheritdoc/>
    public T Require<T>(string idPath) where T : class, IUiControl =>
        Find<T>(idPath) ?? throw new KeyNotFoundException($"UI control '{idPath}' of type {typeof(T).Name} was not found under '{Id}'.");
}

/// <summary>Managed <see cref="IButton"/> implementation.</summary>
public sealed class UiButtonNode(string id, UiControlNode? parent, string controlType = "Button")
    : UiControlNode(id, controlType, parent), IButton
{
    /// <inheritdoc/>
    public bool Enabled
    {
        get => IsEnabled;
        set => IsEnabled = value;
    }

    /// <inheritdoc/>
    public event Action? Clicked;

    /// <inheritdoc/>
    public void Click()
    {
        ThrowIfDestroyed();
        if (IsEnabled && IsEffectivelyVisible())
        {
            Clicked?.Invoke();
        }
    }
}

/// <summary>Managed <see cref="ILabel"/> implementation.</summary>
public sealed class UiLabelNode(string id, UiControlNode? parent) : UiControlNode(id, "Label", parent), ILabel
{
    /// <inheritdoc/>
    public string? ForegroundColor
    {
        get => GetProperty("foreground_colour");
        set
        {
            if (value is not null)
            {
                SetProperty("foreground_colour", value);
            }
        }
    }
}

/// <summary>Managed <see cref="ITextBox"/> implementation.</summary>
public sealed class UiTextBoxNode(string id, UiControlNode? parent) : UiControlNode(id, "TextBox", parent), ITextBox
{
    private bool _suppressNativeTextChanged;

    /// <inheritdoc/>
    public int MaxLength { get; set; }

    /// <inheritdoc/>
    public event Action<string>? TextChanged;

    /// <inheritdoc/>
    public event Action<string>? Submitted;

    /// <inheritdoc/>
    public override string Text
    {
        get
        {
            ThrowIfDestroyed();
            if (NativeControlHandle != nint.Zero && NativeBridge is not null)
            {
                string? nativeText = NativeBridge.GetNativeUiTextBoxText(NativeControlHandle);
                if (nativeText is not null)
                {
                    string clamped = ClampText(nativeText);
                    SetBaseTextSilent(clamped);
                    return clamped;
                }
            }

            return base.Text;
        }
        set
        {
            ThrowIfDestroyed();
            string next = ClampText(value ?? string.Empty);
            string current = RawBaseText;
            if (string.Equals(current, next, StringComparison.Ordinal))
            {
                return;
            }

            SetBaseTextSilent(next);
            if (NativeControlHandle != nint.Zero && NativeBridge is not null)
            {
                _suppressNativeTextChanged = true;
                try
                {
                    NativeBridge.SetNativeUiTextBoxText(NativeControlHandle, next);
                }
                finally
                {
                    _suppressNativeTextChanged = false;
                }
            }

            TextChanged?.Invoke(next);
        }
    }

    /// <inheritdoc/>
    public void Submit()
    {
        ThrowIfDestroyed();
        Submitted?.Invoke(Text);
    }

    internal void OnNativeTextChanged()
    {
        if (_suppressNativeTextChanged || IsDestroyed)
        {
            return;
        }

        string? nativeText = NativeControlHandle != nint.Zero && NativeBridge is not null
            ? NativeBridge.GetNativeUiTextBoxText(NativeControlHandle)
            : null;
        if (nativeText is null)
        {
            return;
        }

        string next = ClampText(nativeText);
        if (!string.Equals(RawBaseText, next, StringComparison.Ordinal))
        {
            SetBaseTextSilent(next);
            TextChanged?.Invoke(next);
        }
    }

    internal void OnNativeSubmitted()
    {
        if (IsDestroyed)
        {
            return;
        }

        Submitted?.Invoke(Text);
    }

    private string ClampText(string text)
    {
        if (MaxLength > 0 && text.Length > MaxLength)
        {
            int len = MaxLength;
            if (len > 0 && char.IsHighSurrogate(text[len - 1]))
            {
                len--;
            }

            return text[..len];
        }

        return text;
    }
}

/// <summary>Managed <see cref="ICheckBox"/> implementation.</summary>
public sealed class UiCheckBoxNode(string id, UiControlNode? parent) : UiControlNode(id, "CheckBox", parent), ICheckBox
{
    private bool _isChecked;
    private bool _suppressNativeCheckedChanged;

    /// <inheritdoc/>
    public bool IsChecked
    {
        get
        {
            ThrowIfDestroyed();
            if (NativeControlHandle != nint.Zero && NativeBridge is not null)
            {
                bool? nativeChecked = NativeBridge.GetNativeUiCheckBoxChecked(NativeControlHandle);
                if (nativeChecked.HasValue)
                {
                    _isChecked = nativeChecked.Value;
                }
            }

            return _isChecked;
        }
        set
        {
            ThrowIfDestroyed();
            if (_isChecked != value)
            {
                _isChecked = value;
                SetProperty("checked", value ? "true" : "false");
                if (NativeControlHandle != nint.Zero && NativeBridge is not null)
                {
                    _suppressNativeCheckedChanged = true;
                    try
                    {
                        NativeBridge.SetNativeUiCheckBoxChecked(NativeControlHandle, value);
                    }
                    finally
                    {
                        _suppressNativeCheckedChanged = false;
                    }
                }

                CheckedChanged?.Invoke(value);
            }
        }
    }

    /// <inheritdoc/>
    public event Action<bool>? CheckedChanged;

    internal void OnNativeCheckedChanged()
    {
        if (_suppressNativeCheckedChanged || IsDestroyed)
        {
            return;
        }

        if (NativeControlHandle != nint.Zero && NativeBridge is not null)
        {
            bool? nativeChecked = NativeBridge.GetNativeUiCheckBoxChecked(NativeControlHandle);
            if (nativeChecked.HasValue && nativeChecked.Value != _isChecked)
            {
                _isChecked = nativeChecked.Value;
                SetProperty("checked", _isChecked ? "true" : "false");
                CheckedChanged?.Invoke(_isChecked);
            }
        }
    }
}

/// <summary>Managed <see cref="IUiWindow"/> implementation.</summary>
public sealed class UiWindowNode : UiControlNode, IUiWindow
{
    private readonly Action<UiWindowNode> _reloadAction;
    private readonly Action<UiWindowNode> _closeAction;
    private readonly Action<UiWindowNode, string>? _subtreeRemovedAction;
    private readonly List<IDisposable> _nativeSubscriptions = [];

    internal UiWindowNode(
        string windowId,
        string sourcePath,
        Action<UiWindowNode> reloadAction,
        Action<UiWindowNode> closeAction,
        Action<UiWindowNode, string>? subtreeRemovedAction = null,
        bool isEmbeddedChildWindow = false)
        : base(windowId, "Frame", null)
    {
        SourcePath = sourcePath;
        _reloadAction = reloadAction;
        _closeAction = closeAction;
        _subtreeRemovedAction = subtreeRemovedAction;
        IsEmbeddedChildWindow = isEmbeddedChildWindow;
    }

    internal bool IsEmbeddedChildWindow { get; }

    internal void TrackNativeSubscription(IDisposable subscription)
    {
        _nativeSubscriptions.Add(subscription);
    }

    internal void ReleaseNativeSubscriptions()
    {
        IDisposable[] snapshot = [.. _nativeSubscriptions];
        _nativeSubscriptions.Clear();
        foreach (IDisposable sub in snapshot)
        {
            sub.Dispose();
        }
    }

    internal void NotifyControlSubtreeRemoved(string controlPath) =>
        _subtreeRemovedAction?.Invoke(this, controlPath);

    internal void RaiseClosed() => Closed?.Invoke();

    internal void MarkClosedOnDestroy()
    {
        bool wasOpen = IsOpen;
        IsOpen = false;
        ReleaseNativeSubscriptions();
        if (wasOpen)
        {
            _closeAction(this);
            Closed?.Invoke();
        }
    }

    /// <inheritdoc/>
    public string SourcePath { get; }

    /// <inheritdoc/>
    public bool IsOpen { get; internal set; } = true;

    /// <inheritdoc/>
    public event Action? Closed;

    /// <inheritdoc/>
    public void Reload()
    {
        ThrowIfDestroyed();
        if (!IsOpen)
        {
            throw new InvalidOperationException($"Cannot reload closed UI window '{Id}'.");
        }

        _reloadAction(this);
    }

    /// <inheritdoc/>
    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        IsOpen = false;
        ReleaseNativeSubscriptions();
        DestroyCustomRecursive();
        _closeAction(this);
        try
        {
            Closed?.Invoke();
        }
        finally
        {
            if (IsEmbeddedChildWindow && !IsDestroyed)
            {
                DestroyNativeHandlesRecursive();
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose() => Close();
}

/// <summary>
/// Loads, parses, tracks, reloads, and closes <c>.ui</c> windows backed by <see cref="IFileOverlayService"/>
/// and <see cref="UiControlRegistry"/>.
/// </summary>
public sealed class UiService : IUiService
{
    private static readonly Regex ConstRegex = new(@"^const\s+(\$[A-Za-z0-9_]+)\s*=\s*(.+?)\s*;?\s*$", RegexOptions.Compiled);
    private static readonly Regex BeginRegex = new(@"^begin\s+([A-Za-z0-9_]+)(?:\s+based\s+on\s+([A-Za-z0-9_]+))?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PropRegex = new(@"^([A-Za-z0-9_]+)\s*=\s*(.+?)\s*;?\s*$", RegexOptions.Compiled);

    private readonly IFileOverlayService _files;
    private readonly UiControlRegistry _controlRegistry;
    private readonly Native.INativeBridge? _bridge;
    private readonly Dictionary<string, UiWindowNode> _windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    internal event Action<string>? WindowTreeInvalidated;
    internal event Action<string, string>? ControlSubtreeRemoved;

    /// <summary>Creates a new <see cref="UiService"/>.</summary>
    public UiService(IFileOverlayService files, UiControlRegistry controlRegistry, Native.INativeBridge? bridge = null)
    {
        _files = files;
        _controlRegistry = controlRegistry;
        _bridge = bridge;
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<IUiWindow> OpenWindows
    {
        get { lock (_gate) { return [.. _windows.Values]; } }
    }

    /// <inheritdoc/>
    public IUiControl? GetRootControl()
    {
        if (_bridge is null)
        {
            return null;
        }

        nint root = _bridge.GetNativeUiRoot();
        if (root == nint.Zero)
        {
            return null;
        }

        return new UiControlNode("__root__", "Frame", null)
        {
            NativeBridge = _bridge,
            NativeControlHandle = root,
        };
    }

    /// <inheritdoc/>
    public IUiWindow Load(string virtualPath, string? windowId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualPath);
        string source = _files.ReadAllText(virtualPath)
            ?? throw new FileNotFoundException($"UI layout '{virtualPath}' was not found in any overlay or stock archive.", virtualPath);

        UiControlNode parsedRoot = ParseUiDocument(source, virtualPath);
        string resolvedId = !string.IsNullOrWhiteSpace(windowId)
            ? windowId.Trim()
            : !string.IsNullOrEmpty(parsedRoot.Id)
                ? parsedRoot.Id
                : Path.GetFileNameWithoutExtension(virtualPath);

        var window = new UiWindowNode(resolvedId, virtualPath, ReloadInternal, CloseInternal, OnControlSubtreeRemoved);
        AttachParsedTree(window, parsedRoot);

        UiWindowNode? replaced = null;
        lock (_gate)
        {
            if (_windows.TryGetValue(resolvedId, out UiWindowNode? existing))
            {
                replaced = existing;
                existing.IsOpen = false;
                existing.ReleaseNativeSubscriptions();
                if (existing.NativeControlHandle != nint.Zero && _bridge is not null)
                {
                    _bridge.SetNativeUiVisible(existing.NativeControlHandle, false);
                }

                existing.DestroyCustomRecursive();
            }

            BindNativeControlsIfLive(window, virtualPath);
            _windows[resolvedId] = window;
        }

        if (replaced is not null)
        {
            FinalizeReplacedWindow(replaced, resolvedId);
        }

        window.InitializeCustomRecursive();
        return window;
    }

    /// <inheritdoc/>
    public IUiWindow CreateWindowInto(IUiControl parent, string virtualPath, string? windowId = null)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualPath);
        if (parent is not UiControlNode parentNode)
        {
            throw new ArgumentException("Parent control must be created by UiService.", nameof(parent));
        }

        ObjectDisposedException.ThrowIf(parentNode.IsDestroyed, parentNode);

        string source = _files.ReadAllText(virtualPath)
            ?? throw new FileNotFoundException($"UI layout '{virtualPath}' was not found in any overlay or stock archive.", virtualPath);

        UiControlNode parsedRoot = ParseUiDocument(source, virtualPath);
        string resolvedId = !string.IsNullOrWhiteSpace(windowId)
            ? windowId.Trim()
            : !string.IsNullOrEmpty(parsedRoot.Id)
                ? parsedRoot.Id
                : Path.GetFileNameWithoutExtension(virtualPath);

        var window = new UiWindowNode(resolvedId, virtualPath, ReloadInternal, CloseInternal, OnControlSubtreeRemoved, isEmbeddedChildWindow: true);
        AttachParsedTree(window, parsedRoot);
        parentNode.AddChild(window);

        UiWindowNode? replaced = null;
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(windowId) && _windows.TryGetValue(resolvedId, out UiWindowNode? existing))
            {
                replaced = existing;
                existing.IsOpen = false;
                existing.ReleaseNativeSubscriptions();
                if (existing.NativeControlHandle != nint.Zero && _bridge is not null)
                {
                    _bridge.SetNativeUiVisible(existing.NativeControlHandle, false);
                }

                existing.DestroyCustomRecursive();
            }

            if (_bridge is not null && parentNode.NativeControlHandle != nint.Zero)
            {
                nint nativeChildWindow = _bridge.CreateNativeUiWindowInto(parentNode.NativeControlHandle, virtualPath);
                if (nativeChildWindow != nint.Zero)
                {
                    window.NativeBridge = _bridge;
                    window.NativeControlHandle = nativeChildWindow;
                    _bridge.SetNativeUiVisible(nativeChildWindow, window.Visible);
                    BindChildrenRecursive(window, nativeChildWindow, window);
                }
            }

            if (!string.IsNullOrWhiteSpace(windowId))
            {
                _windows[resolvedId] = window;
            }
        }

        if (replaced is not null)
        {
            FinalizeReplacedWindow(replaced, resolvedId);
        }

        window.InitializeCustomRecursive();
        return window;
    }

    /// <inheritdoc/>
    public IUiWindow? GetWindow(string windowId)
    {
        if (string.IsNullOrWhiteSpace(windowId))
        {
            return null;
        }

        lock (_gate)
        {
            return _windows.TryGetValue(windowId, out UiWindowNode? w) ? w : null;
        }
    }

    /// <inheritdoc/>
    public bool CloseWindow(string windowId)
    {
        UiWindowNode? window;
        lock (_gate)
        {
            _windows.TryGetValue(windowId, out window);
        }

        if (window is null)
        {
            return false;
        }

        window.Close();
        return true;
    }

    /// <inheritdoc/>
    public void ReloadWindow(string windowId)
    {
        UiWindowNode? window = GetWindow(windowId) as UiWindowNode
            ?? throw new KeyNotFoundException($"UI window '{windowId}' is not open.");
        window.Reload();
    }

    /// <inheritdoc/>
    public IDisposable RegisterControl<TControl>(string controlTypeName) where TControl : class, ICustomUiControl, new() =>
        _controlRegistry.Register<TControl>(controlTypeName);

    /// <inheritdoc/>
    public IDisposable RegisterControl(string controlTypeName, Func<ICustomUiControl> factory) =>
        _controlRegistry.Register(controlTypeName, factory);

    /// <summary>Advances per-frame callbacks on all custom UI controls in open windows.</summary>
    public void TickCustomControls(float deltaSeconds)
    {
        UiWindowNode[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _windows.Values];
        }

        foreach (UiWindowNode win in snapshot)
        {
            if (win.IsOpen)
            {
                win.TickCustomRecursive(deltaSeconds);
            }
        }
    }

    private void ReloadInternal(UiWindowNode window)
    {
        string source = _files.ReadAllText(window.SourcePath)
            ?? throw new FileNotFoundException($"UI layout '{window.SourcePath}' could not be reloaded.", window.SourcePath);

        WindowTreeInvalidated?.Invoke(window.Id);
        window.ReleaseNativeSubscriptions();
        DestroyNestedChildWindows(window);
        if (!window.IsOpen || window.IsDestroyed)
        {
            return;
        }

        window.DestroyCustomRecursive();
        if (!window.IsOpen || window.IsDestroyed)
        {
            window.MutableChildren.Clear();
            return;
        }

        window.MutableChildren.Clear();
        UiControlNode parsedRoot = ParseUiDocument(source, window.SourcePath);
        AttachParsedTree(window, parsedRoot);
        if (window.NativeControlHandle != nint.Zero && _bridge is not null)
        {
            _bridge.SetNativeUiVisible(window.NativeControlHandle, window.Visible);
            BindChildrenRecursive(window, window.NativeControlHandle, window);
        }

        window.InitializeCustomRecursive();
    }

    private void CloseInternal(UiWindowNode window)
    {
        window.ReleaseNativeSubscriptions();
        DestroyNestedChildWindows(window);
        DetachFromParentIfAttached(window);

        if (window.NativeControlHandle != nint.Zero && _bridge is not null)
        {
            _bridge.SetNativeUiVisible(window.NativeControlHandle, false);
        }

        lock (_gate)
        {
            if (_windows.TryGetValue(window.Id, out UiWindowNode? existing) && ReferenceEquals(existing, window))
            {
                _windows.Remove(window.Id);
            }
        }

        WindowTreeInvalidated?.Invoke(window.Id);
    }

    private void FinalizeReplacedWindow(UiWindowNode replaced, string resolvedId)
    {
        DestroyNestedChildWindows(replaced);
        DetachFromParentIfAttached(replaced);
        WindowTreeInvalidated?.Invoke(resolvedId);
        try
        {
            replaced.RaiseClosed();
        }
        finally
        {
            if (replaced.IsEmbeddedChildWindow && !replaced.IsDestroyed)
            {
                replaced.DestroyNativeHandlesRecursive();
            }
        }
    }

    private static void DetachFromParentIfAttached(UiWindowNode window)
    {
        if (window.Parent is UiControlNode parent)
        {
            if (!parent.IsDestroyed)
            {
                parent.RemoveChild(window);
            }
            else
            {
                window.Parent = null;
            }
        }
    }

    private static void DestroyNestedChildWindows(UiControlNode root)
    {
        List<UiWindowNode> nestedWindows = [];
        CollectNestedChildWindowsPostOrder(root, nestedWindows);
        foreach (UiWindowNode nested in nestedWindows)
        {
            nested.Destroy();
        }
    }

    private static void CollectNestedChildWindowsPostOrder(UiControlNode root, List<UiWindowNode> output)
    {
        foreach (UiControlNode child in root.MutableChildren.ToArray())
        {
            CollectNestedChildWindowsPostOrder(child, output);
            if (child is UiWindowNode childWindow)
            {
                output.Add(childWindow);
            }
        }
    }

    private void OnControlSubtreeRemoved(UiWindowNode window, string controlPath)
    {
        if (!string.IsNullOrWhiteSpace(controlPath))
        {
            ControlSubtreeRemoved?.Invoke(window.Id, controlPath);
        }
    }

    private void BindNativeControlsIfLive(UiWindowNode window, string virtualPath)
    {
        if (_bridge is null)
        {
            return;
        }

        nint nativeRoot = _bridge.CreateNativeUiWindow(virtualPath);
        if (nativeRoot == nint.Zero)
        {
            return;
        }

        window.NativeBridge = _bridge;
        window.NativeControlHandle = nativeRoot;
        _bridge.SetNativeUiVisible(nativeRoot, window.Visible);
        BindChildrenRecursive(window, nativeRoot, window);
    }

    private void BindChildrenRecursive(UiWindowNode window, nint nativeParent, UiControlNode parentNode)
    {
        if (_bridge is null)
        {
            return;
        }

        foreach (UiControlNode child in parentNode.MutableChildren)
        {
            nint nativeChild = nint.Zero;
            if (!string.IsNullOrWhiteSpace(child.Id))
            {
                nativeChild = _bridge.FindNativeUiChild(nativeParent, child.Id);
                if (nativeChild == nint.Zero && window.NativeControlHandle != nint.Zero && !string.IsNullOrWhiteSpace(child.Path))
                {
                    nativeChild = _bridge.FindNativeUiChild(window.NativeControlHandle, child.Path);
                }
            }

            if (nativeChild != nint.Zero)
            {
                child.NativeBridge = _bridge;
                child.NativeControlHandle = nativeChild;

                // Synchronize parsed initial visibility & text so reused native windows reset any
                // hidden rows or stale captions from previous open/close cycles.
                _bridge.SetNativeUiVisible(nativeChild, child.Visible);
                if (child is UiButtonNode button)
                {
                    if (UiControlNode.SupportsNativeButtonCaption(child.ControlType) &&
                        (child.Properties.ContainsKey("text") || !string.IsNullOrEmpty(child.Text)))
                    {
                        _bridge.SetNativeUiText(nativeChild, child.Text, isButton: true);
                    }

                    window.TrackNativeSubscription(_bridge.SubscribeNativeUiClick(nativeChild, button.Click));
                }
                else if (child is UiLabelNode && (child.Properties.ContainsKey("text") || !string.IsNullOrEmpty(child.Text)))
                {
                    _bridge.SetNativeUiText(nativeChild, child.Text, isButton: false);
                }
                else if (child is UiTextBoxNode textBox)
                {
                    if (child.Properties.ContainsKey("text") || !string.IsNullOrEmpty(textBox.RawBaseText))
                    {
                        _bridge.SetNativeUiTextBoxText(nativeChild, textBox.RawBaseText);
                    }

                    window.TrackNativeSubscription(_bridge.SubscribeNativeUiTextBoxChanged(nativeChild, textBox.OnNativeTextChanged));
                    window.TrackNativeSubscription(_bridge.SubscribeNativeUiTextBoxSubmitted(nativeChild, textBox.OnNativeSubmitted));
                }
                else if (child is UiCheckBoxNode checkBox)
                {
                    if (child.Properties.TryGetValue("checked", out string? checkedProp) && bool.TryParse(checkedProp, out bool initialChecked))
                    {
                        _bridge.SetNativeUiCheckBoxChecked(nativeChild, initialChecked);
                    }

                    window.TrackNativeSubscription(_bridge.SubscribeNativeUiCheckBoxChanged(nativeChild, checkBox.OnNativeCheckedChanged));
                }

                BindChildrenRecursive(window, nativeChild, child);
            }
            else
            {
                BindChildrenRecursive(window, nativeParent, child);
            }
        }
    }

    private static void AttachParsedTree(UiWindowNode window, UiControlNode parsedRoot)
    {
        foreach (KeyValuePair<string, string> prop in parsedRoot.Properties)
        {
            window.SetProperty(prop.Key, prop.Value);
        }

        foreach (UiControlNode child in parsedRoot.MutableChildren)
        {
            window.AddChild(child);
        }
    }

    private UiControlNode ParseUiDocument(string source, string virtualPath)
    {
        var constants = new Dictionary<string, string>(StringComparer.Ordinal);
        var templates = new Dictionary<string, UiControlNode>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<UiControlNode>();
        UiControlNode? root = null;

        string[] lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
        {
            string raw = lines[lineIdx];
            int comment = FindLineComment(raw);
            string line = (comment >= 0 ? raw[..comment] : raw).Trim();
            if (line.Length == 0 || string.Equals(line, "<DEFINITIONS>", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Match constMatch = ConstRegex.Match(line);
            if (constMatch.Success)
            {
                constants[constMatch.Groups[1].Value] = constMatch.Groups[2].Value.Trim();
                continue;
            }

            Match beginMatch = BeginRegex.Match(line);
            if (beginMatch.Success)
            {
                string typeName = beginMatch.Groups[1].Value;
                string? basedOn = beginMatch.Groups[2].Success ? beginMatch.Groups[2].Value : null;
                UiControlNode? parent = stack.Count > 0 ? stack.Peek() : null;
                UiControlNode node = InstantiateControl(typeName, parent);

                if (basedOn is not null && templates.TryGetValue(basedOn, out UiControlNode? template))
                {
                    foreach (KeyValuePair<string, string> kv in template.Properties)
                    {
                        if (!string.Equals(kv.Key, "id", StringComparison.OrdinalIgnoreCase))
                        {
                            node.SetProperty(kv.Key, kv.Value);
                        }
                    }
                }

                if (parent is not null)
                {
                    parent.AddChild(node);
                }
                else
                {
                    root ??= node;
                }

                stack.Push(node);
                continue;
            }

            if (string.Equals(line, "end", StringComparison.OrdinalIgnoreCase))
            {
                if (stack.Count == 0)
                {
                    throw new FormatException($"{virtualPath}({lineIdx + 1}): unexpected 'end' with no matching 'begin'.");
                }

                UiControlNode completed = stack.Pop();
                if (!string.IsNullOrEmpty(completed.Id))
                {
                    // Guard against duplicate sibling IDs (verified fatal in 07-spike-results.md!).
                    if (completed.Parent is not null)
                    {
                        int dupCount = completed.Parent.MutableChildren.Count(c => string.Equals(c.Id, completed.Id, StringComparison.OrdinalIgnoreCase));
                        if (dupCount > 1)
                        {
                            throw new FormatException($"{virtualPath}({lineIdx + 1}): duplicate sibling control id '{completed.Id}'.");
                        }
                    }

                    templates[completed.Id] = completed;
                }

                continue;
            }

            if (stack.Count > 0)
            {
                Match propMatch = PropRegex.Match(line);
                if (propMatch.Success)
                {
                    string key = propMatch.Groups[1].Value;
                    string val = UnquoteAndSubstitute(propMatch.Groups[2].Value, constants);
                    UiControlNode current = stack.Peek();
                    if (string.Equals(key, "id", StringComparison.OrdinalIgnoreCase))
                    {
                        current.Id = val;
                    }
                    else
                    {
                        current.SetProperty(key, val);
                    }
                }
            }
        }

        if (stack.Count > 0)
        {
            throw new FormatException($"{virtualPath}: unclosed 'begin {stack.Peek().ControlType}' block.");
        }

        return root ?? throw new FormatException($"{virtualPath}: no root control definition found.");
    }

    private UiControlNode InstantiateControl(string typeName, UiControlNode? parent)
    {
        if (UiControlNode.IsClickableButtonControl(typeName))
        {
            return new UiButtonNode(string.Empty, parent, typeName);
        }

        if (string.Equals(typeName, "Label", StringComparison.OrdinalIgnoreCase))
        {
            return new UiLabelNode(string.Empty, parent);
        }

        if (string.Equals(typeName, "TextBox", StringComparison.OrdinalIgnoreCase))
        {
            return new UiTextBoxNode(string.Empty, parent);
        }

        if (string.Equals(typeName, "CheckBox", StringComparison.OrdinalIgnoreCase))
        {
            return new UiCheckBoxNode(string.Empty, parent);
        }

        ICustomUiControl? custom = _controlRegistry.CreateInstance(typeName);
        return new UiControlNode(string.Empty, typeName, parent, custom);
    }

    private static int FindLineComment(string line)
    {
        bool inQuotes = false;
        for (int i = 0; i < line.Length - 1; i++)
        {
            if (line[i] == '"' && (i == 0 || line[i - 1] != '\\'))
            {
                inQuotes = !inQuotes;
            }
            else if (!inQuotes && line[i] == '/' && line[i + 1] == '/')
            {
                return i;
            }
        }

        return -1;
    }

    private static string UnquoteAndSubstitute(string rawValue, Dictionary<string, string> constants)
    {
        string trimmed = rawValue.Trim().TrimEnd(';').Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            return trimmed[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal);
        }

        foreach (KeyValuePair<string, string> kv in constants)
        {
            if (trimmed.Contains(kv.Key, StringComparison.Ordinal))
            {
                trimmed = trimmed.Replace(kv.Key, kv.Value, StringComparison.Ordinal);
            }
        }

        return trimmed;
    }
}
