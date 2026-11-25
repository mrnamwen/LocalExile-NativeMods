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

    internal nint NativeControlHandle { get; set; }
    internal Native.INativeBridge? NativeBridge { get; set; }

    /// <summary>Creates a new control node.</summary>
    public UiControlNode(string id, string controlType, UiControlNode? parent, ICustomUiControl? customBehavior = null)
    {
        Id = id;
        ControlType = controlType;
        Parent = parent;
        _customBehavior = customBehavior;
    }

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
            while (cur is not null && cur.Parent is not null)
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
        get => _visible;
        set
        {
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
        get => _text;
        set
        {
            string normalized = value ?? string.Empty;
            if (string.Equals(_text, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _text = normalized;
            _properties["text"] = _text;
            if (NativeControlHandle != nint.Zero && NativeBridge is not null)
            {
                bool isButton = string.Equals(ControlType, "Button", StringComparison.OrdinalIgnoreCase);
                if (isButton || string.Equals(ControlType, "Label", StringComparison.OrdinalIgnoreCase))
                {
                    NativeBridge.SetNativeUiText(NativeControlHandle, _text, isButton);
                }
            }

            _customBehavior?.OnPropertyChanged("text", _text);
        }
    }

    /// <summary>Typed parent node.</summary>
    public UiControlNode? Parent { get; internal set; }

    IUiControl? IUiControl.Parent => Parent;

    /// <inheritdoc/>
    public IReadOnlyList<IUiControl> Children => _children;

    internal List<UiControlNode> MutableChildren => _children;

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> Properties => _properties;

    /// <inheritdoc/>
    public string? GetProperty(string name) =>
        _properties.TryGetValue(name, out string? value) ? value : null;

    /// <inheritdoc/>
    public void SetProperty(string name, string value)
    {
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

        _customBehavior?.OnPropertyChanged(name, value);
    }

    internal bool IsEffectivelyVisible()
    {
        UiControlNode? cur = this;
        while (cur is not null)
        {
            if (!cur.Visible)
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
        child.Parent = this;
        _children.Add(child);
    }

    internal void InitializeCustomRecursive()
    {
        _customBehavior?.OnInitialize(this);
        foreach (UiControlNode child in _children)
        {
            child.InitializeCustomRecursive();
        }
    }

    internal void TickCustomRecursive(float deltaSeconds)
    {
        _customBehavior?.OnFrame(deltaSeconds);
        foreach (UiControlNode child in _children)
        {
            child.TickCustomRecursive(deltaSeconds);
        }
    }

    internal void DestroyCustomRecursive()
    {
        foreach (UiControlNode child in _children)
        {
            child.DestroyCustomRecursive();
        }

        _customBehavior?.OnDestroy();
    }

    /// <inheritdoc/>
    public IUiControl? Find(string idPath)
    {
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
public sealed class UiButtonNode(string id, UiControlNode? parent) : UiControlNode(id, "Button", parent), IButton
{
    /// <inheritdoc/>
    public bool Enabled { get; set; } = true;

    /// <inheritdoc/>
    public event Action? Clicked;

    /// <inheritdoc/>
    public void Click()
    {
        if (Enabled && IsEffectivelyVisible())
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
    /// <inheritdoc/>
    public int MaxLength { get; set; }

    /// <inheritdoc/>
    public event Action<string>? TextChanged;

    /// <inheritdoc/>
    public event Action<string>? Submitted;

    /// <inheritdoc/>
    public override string Text
    {
        get => base.Text;
        set
        {
            string next = value ?? string.Empty;
            if (MaxLength > 0 && next.Length > MaxLength)
            {
                next = next[..MaxLength];
            }

            if (!string.Equals(base.Text, next, StringComparison.Ordinal))
            {
                base.Text = next;
                TextChanged?.Invoke(next);
            }
        }
    }

    /// <inheritdoc/>
    public void Submit() => Submitted?.Invoke(Text);
}

/// <summary>Managed <see cref="ICheckBox"/> implementation.</summary>
public sealed class UiCheckBoxNode(string id, UiControlNode? parent) : UiControlNode(id, "CheckBox", parent), ICheckBox
{
    private bool _isChecked;

    /// <inheritdoc/>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked != value)
            {
                _isChecked = value;
                SetProperty("checked", value ? "true" : "false");
                CheckedChanged?.Invoke(value);
            }
        }
    }

    /// <inheritdoc/>
    public event Action<bool>? CheckedChanged;
}

/// <summary>Managed <see cref="IUiWindow"/> implementation.</summary>
public sealed class UiWindowNode : UiControlNode, IUiWindow
{
    private readonly Action<UiWindowNode> _reloadAction;
    private readonly Action<UiWindowNode> _closeAction;
    private readonly List<IDisposable> _nativeSubscriptions = [];

    internal UiWindowNode(string windowId, string sourcePath, Action<UiWindowNode> reloadAction, Action<UiWindowNode> closeAction)
        : base(windowId, "Frame", null)
    {
        SourcePath = sourcePath;
        _reloadAction = reloadAction;
        _closeAction = closeAction;
    }

    internal void TrackNativeSubscription(IDisposable subscription)
    {
        _nativeSubscriptions.Add(subscription);
    }

    internal void ReleaseNativeSubscriptions()
    {
        foreach (IDisposable sub in _nativeSubscriptions)
        {
            sub.Dispose();
        }

        _nativeSubscriptions.Clear();
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
        Closed?.Invoke();
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

        var window = new UiWindowNode(resolvedId, virtualPath, ReloadInternal, CloseInternal);
        AttachParsedTree(window, parsedRoot);

        lock (_gate)
        {
            if (_windows.TryGetValue(resolvedId, out UiWindowNode? existing))
            {
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

        window.ReleaseNativeSubscriptions();
        window.DestroyCustomRecursive();
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
                    if (child.Properties.ContainsKey("text") || !string.IsNullOrEmpty(child.Text))
                    {
                        _bridge.SetNativeUiText(nativeChild, child.Text, isButton: true);
                    }

                    window.TrackNativeSubscription(_bridge.SubscribeNativeUiClick(nativeChild, button.Click));
                }
                else if (child is UiLabelNode && (child.Properties.ContainsKey("text") || !string.IsNullOrEmpty(child.Text)))
                {
                    _bridge.SetNativeUiText(nativeChild, child.Text, isButton: false);
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
        if (string.Equals(typeName, "Button", StringComparison.OrdinalIgnoreCase))
        {
            return new UiButtonNode(string.Empty, parent);
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
