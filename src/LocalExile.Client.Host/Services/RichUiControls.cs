using System.Globalization;
using LocalExile.Client.Host.Native;
using LocalExile.Client.Sdk.Engine;

namespace LocalExile.Client.Host.Services;

/// <summary>Managed <see cref="IPicture"/> implementation backed by slab-pooled native <c>UI::Controls::Picture</c>.</summary>
public sealed class UiPictureNode(string id, UiControlNode? parent) : UiControlNode(id, "Picture", parent), IPicture
{
    private string? _texturePath;

    /// <inheritdoc/>
    public string? TexturePath => _texturePath ?? GetProperty("texture");

    /// <inheritdoc/>
    public void SetTexture(string texturePath, bool resizeToImage = false)
    {
        ThrowIfDestroyed();
        ArgumentException.ThrowIfNullOrWhiteSpace(texturePath);
        _texturePath = texturePath.Trim();
        SetProperty("texture", _texturePath);
        if (NativeControlHandle != nint.Zero && NativeBridge is not null)
        {
            NativeBridge.SetNativeUiPictureTexture(NativeControlHandle, _texturePath, resizeToImage);
        }
    }
}

/// <summary>Managed <see cref="IScrollableFrame"/> implementation backed by slab-pooled native <c>UI::Controls::ScrollableFrame</c>.</summary>
public sealed class UiScrollableFrameNode : UiControlNode, IScrollableFrame
{
    private float _scrollOffset;
    private float _maxScrollOffset;

    /// <summary>Creates a new <see cref="UiScrollableFrameNode"/>.</summary>
    public UiScrollableFrameNode(string id, UiControlNode? parent)
        : base(id, "ScrollableFrame", parent)
    {
    }

    /// <inheritdoc/>
    public float ScrollOffset
    {
        get
        {
            ThrowIfDestroyed();
            if (NativeControlHandle != nint.Zero && NativeBridge is not null &&
                NativeBridge.TryGetNativeUiScrollState(NativeControlHandle, out float nativeOffset, out float nativeMax))
            {
                _scrollOffset = nativeOffset;
                _maxScrollOffset = nativeMax;
            }

            return _scrollOffset;
        }
    }

    /// <inheritdoc/>
    public float MaxScrollOffset
    {
        get
        {
            ThrowIfDestroyed();
            if (NativeControlHandle != nint.Zero && NativeBridge is not null &&
                NativeBridge.TryGetNativeUiScrollState(NativeControlHandle, out float nativeOffset, out float nativeMax))
            {
                _scrollOffset = nativeOffset;
                _maxScrollOffset = nativeMax;
            }

            return _maxScrollOffset;
        }
    }

    /// <inheritdoc/>
    /// <remarks>On 3.29.3d, <c>ScrollableFrame</c> content children live directly in <c>Control+0xb8</c>.</remarks>
    public IUiControl ContentContainer => this;

    /// <inheritdoc/>
    public void SetScrollOffset(float offset)
    {
        ThrowIfDestroyed();
        float max = Math.Max(0f, _maxScrollOffset);
        float clamped = float.IsNaN(offset) ? 0f : Math.Clamp(offset, 0f, max > 0f ? max : Math.Max(0f, offset));
        _scrollOffset = clamped;
        SetProperty("scroll_offset", clamped.ToString(CultureInfo.InvariantCulture));
        if (NativeControlHandle != nint.Zero && NativeBridge is not null)
        {
            NativeBridge.SetNativeUiScrollOffset(NativeControlHandle, clamped);
        }
    }

    /// <inheritdoc/>
    public void SetMaxScrollOffset(float maxScrollOffset)
    {
        ThrowIfDestroyed();
        float clampedMax = Math.Max(0f, float.IsNaN(maxScrollOffset) ? 0f : maxScrollOffset);
        _maxScrollOffset = clampedMax;
        if (_scrollOffset > _maxScrollOffset)
        {
            _scrollOffset = _maxScrollOffset;
        }

        SetProperty("max_scroll_offset", clampedMax.ToString(CultureInfo.InvariantCulture));
        if (NativeControlHandle != nint.Zero && NativeBridge is not null)
        {
            NativeBridge.SetNativeUiScrollRange(NativeControlHandle, 0f, clampedMax);
        }
    }

    /// <inheritdoc/>
    public void UpdateContentBounds()
    {
        ThrowIfDestroyed();
        if (NativeControlHandle != nint.Zero && NativeBridge is not null)
        {
            NativeBridge.UpdateNativeUiScrollContentBounds(NativeControlHandle);
        }
        else
        {
            UpdateLayout();
        }
    }
}

/// <summary>Managed <see cref="ITabContainer"/> implementation backed by slab-pooled native <c>UI::Controls::TabContainer</c>.</summary>
public sealed class UiTabContainerNode(string id, UiControlNode? parent) : UiControlNode(id, "TabContainer", parent), ITabContainer
{
    private readonly List<(string Title, UiControlNode Content)> _tabs = [];
    private int _activeTabIndex = -1;
    private bool _suppressNativeChanged;
    private IDisposable? _nativeSub;

    /// <inheritdoc/>
    public int TabCount
    {
        get
        {
            ThrowIfDestroyed();
            if (NativeControlHandle != nint.Zero && NativeBridge is not null &&
                NativeBridge.TryGetNativeUiTabState(NativeControlHandle, out int nativeCount, out _) &&
                nativeCount >= _tabs.Count)
            {
                return nativeCount;
            }

            return _tabs.Count;
        }
    }

    /// <inheritdoc/>
    public int ActiveTabIndex
    {
        get
        {
            ThrowIfDestroyed();
            if (NativeControlHandle != nint.Zero && NativeBridge is not null &&
                NativeBridge.TryGetNativeUiTabState(NativeControlHandle, out _, out int nativeActive))
            {
                _activeTabIndex = nativeActive;
            }

            return _activeTabIndex;
        }
    }

    /// <inheritdoc/>
    public event Action<int>? ActiveTabChanged;

    internal void BindNativeSubscription()
    {
        _nativeSub?.Dispose();
        _nativeSub = null;
        if (NativeControlHandle != nint.Zero && NativeBridge is not null)
        {
            _nativeSub = NativeBridge.SubscribeNativeUiTabChanged(NativeControlHandle, OnNativeActiveTabChanged);
        }
    }

    /// <inheritdoc/>
    public void SelectTab(int index)
    {
        ThrowIfDestroyed();
        int count = TabCount;
        if (index < 0 || index >= count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), $"Tab index {index} is out of range [0, {count}).");
        }

        if (_activeTabIndex == index)
        {
            return;
        }

        _activeTabIndex = index;
        SyncManagedTabVisibility();
        if (NativeControlHandle != nint.Zero && NativeBridge is not null)
        {
            _suppressNativeChanged = true;
            try
            {
                NativeBridge.SelectNativeUiTab(NativeControlHandle, index);
            }
            finally
            {
                _suppressNativeChanged = false;
            }
        }

        ActiveTabChanged?.Invoke(index);
    }

    /// <inheritdoc/>
    public IUiControl AddTab(string title, string? iconPath = null, bool hidden = false)
    {
        ThrowIfDestroyed();
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        int idx = _tabs.Count;
        var content = new UiControlNode($"tab_{idx}", "Frame", this)
        {
            NativeBridge = NativeBridge,
            OwningUiService = OwningUiService,
        };

        if (NativeControlHandle != nint.Zero && NativeBridge is not null)
        {
            nint nativeContent = NativeBridge.AddNativeUiTab(NativeControlHandle, title, iconPath, hidden);
            if (nativeContent != nint.Zero)
            {
                content.NativeControlHandle = nativeContent;
            }
        }

        AddChild(content);
        _tabs.Add((title, content));

        if (_activeTabIndex < 0 && !hidden)
        {
            _activeTabIndex = idx;
        }

        SyncManagedTabVisibility();
        return content;
    }

    /// <inheritdoc/>
    public void ClearTabs()
    {
        ThrowIfDestroyed();
        foreach ((string _, UiControlNode content) in _tabs.ToArray())
        {
            if (!content.IsDestroyed)
            {
                RemoveChild(content);
                content.Destroy();
            }
        }

        _tabs.Clear();
        _activeTabIndex = -1;

        if (NativeControlHandle != nint.Zero && NativeBridge is not null)
        {
            NativeBridge.ClearNativeUiTabs(NativeControlHandle);
        }
    }

    internal void OnNativeActiveTabChanged(int newIndex)
    {
        if (_suppressNativeChanged || IsDestroyed)
        {
            return;
        }

        if (newIndex != _activeTabIndex)
        {
            _activeTabIndex = newIndex;
            SyncManagedTabVisibility();
            ActiveTabChanged?.Invoke(newIndex);
        }
    }

    private void SyncManagedTabVisibility()
    {
        for (int i = 0; i < _tabs.Count; i++)
        {
            UiControlNode content = _tabs[i].Content;
            if (!content.IsDestroyed)
            {
                content.Visible = i == _activeTabIndex;
            }
        }
    }

    protected override void OnBeforeDestroy()
    {
        _nativeSub?.Dispose();
        _nativeSub = null;
        _tabs.Clear();
    }
}

/// <summary>Managed <see cref="IComboBox"/> implementation backed by slab-pooled native <c>UI::Controls::ComboBox</c>.</summary>
public sealed class UiComboBoxNode(string id, UiControlNode? parent) : UiControlNode(id, "ComboBox", parent), IComboBox
{
    private readonly List<string> _items = [];
    private int _selectedIndex = -1;
    private bool _suppressNativeChanged;
    private IDisposable? _nativeSub;

    /// <inheritdoc/>
    public int ItemCount
    {
        get
        {
            ThrowIfDestroyed();
            if (NativeControlHandle != nint.Zero && NativeBridge is not null &&
                NativeBridge.TryGetNativeUiComboBoxState(NativeControlHandle, out int nativeCount, out _, out _) &&
                nativeCount >= _items.Count)
            {
                return nativeCount;
            }

            return _items.Count;
        }
    }

    /// <inheritdoc/>
    public int SelectedIndex
    {
        get
        {
            ThrowIfDestroyed();
            if (NativeControlHandle != nint.Zero && NativeBridge is not null &&
                NativeBridge.TryGetNativeUiComboBoxState(NativeControlHandle, out _, out int nativeSel, out _))
            {
                _selectedIndex = nativeSel;
            }

            return _selectedIndex;
        }
        set
        {
            ThrowIfDestroyed();
            int count = ItemCount;
            if (value < -1 || value >= count)
            {
                throw new ArgumentOutOfRangeException(nameof(value), $"ComboBox selection index {value} is out of range [-1, {count}).");
            }

            if (_selectedIndex == value)
            {
                return;
            }

            _selectedIndex = value;
            if (NativeControlHandle != nint.Zero && NativeBridge is not null && value >= 0)
            {
                _suppressNativeChanged = true;
                try
                {
                    NativeBridge.SetNativeUiComboBoxSelectedIndex(NativeControlHandle, value);
                }
                finally
                {
                    _suppressNativeChanged = false;
                }
            }

            SelectionChanged?.Invoke(value);
        }
    }

    /// <inheritdoc/>
    public string? SelectedText
    {
        get
        {
            ThrowIfDestroyed();
            if (NativeControlHandle != nint.Zero && NativeBridge is not null &&
                NativeBridge.TryGetNativeUiComboBoxState(NativeControlHandle, out _, out int nativeSel, out string? nativeText))
            {
                _selectedIndex = nativeSel;
                if (nativeText is not null)
                {
                    return nativeText;
                }
            }

            return _selectedIndex >= 0 && _selectedIndex < _items.Count ? _items[_selectedIndex] : null;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> Items
    {
        get
        {
            ThrowIfDestroyed();
            return [.. _items];
        }
    }

    /// <inheritdoc/>
    public event Action<int>? SelectionChanged;

    internal void BindNativeSubscription()
    {
        _nativeSub?.Dispose();
        _nativeSub = null;
        if (NativeControlHandle != nint.Zero && NativeBridge is not null)
        {
            _nativeSub = NativeBridge.SubscribeNativeUiComboBoxSelectionChanged(NativeControlHandle, OnNativeSelectionChanged);
        }
    }

    /// <inheritdoc/>
    public int AddItem(string text, bool enabled = true, uint tag = 0)
    {
        ThrowIfDestroyed();
        ArgumentNullException.ThrowIfNull(text);

        int idx = _items.Count;
        if (NativeControlHandle != nint.Zero && NativeBridge is not null)
        {
            int nativeIdx = NativeBridge.AddNativeUiComboBoxItem(NativeControlHandle, text, enabled, tag);
            if (nativeIdx >= 0)
            {
                idx = nativeIdx;
            }
        }

        _items.Add(text);
        return idx;
    }

    /// <inheritdoc/>
    public void ClearItems()
    {
        ThrowIfDestroyed();
        bool hadSelection = _selectedIndex >= 0;
        _items.Clear();
        _selectedIndex = -1;

        if (NativeControlHandle != nint.Zero && NativeBridge is not null)
        {
            NativeBridge.ClearNativeUiComboBoxItems(NativeControlHandle);
        }

        if (hadSelection)
        {
            SelectionChanged?.Invoke(-1);
        }
    }

    internal void OnNativeSelectionChanged(int newIndex)
    {
        if (_suppressNativeChanged || IsDestroyed)
        {
            return;
        }

        if (newIndex != _selectedIndex)
        {
            _selectedIndex = newIndex;
            SelectionChanged?.Invoke(newIndex);
        }
    }

    protected override void OnBeforeDestroy()
    {
        _nativeSub?.Dispose();
        _nativeSub = null;
        _items.Clear();
    }
}

/// <summary>Managed <see cref="IListBox"/> implementation backed by slab-pooled native <c>UI::Controls::ControlListBox</c>.</summary>
public sealed class UiListBoxNode(string id, UiControlNode? parent) : UiControlNode(id, "ControlListBox", parent), IListBox
{
    private readonly List<UiControlNode> _items = [];
    private int _selectedIndex = -1;
    private bool _suppressNativeChanged;
    private IDisposable? _nativeSub;

    /// <inheritdoc/>
    public int ItemCount
    {
        get
        {
            ThrowIfDestroyed();
            if (NativeControlHandle != nint.Zero && NativeBridge is not null &&
                NativeBridge.TryGetNativeUiListBoxState(NativeControlHandle, out int nativeCount, out _) &&
                nativeCount >= _items.Count)
            {
                return nativeCount;
            }

            return _items.Count;
        }
    }

    /// <inheritdoc/>
    public int SelectedIndex
    {
        get
        {
            ThrowIfDestroyed();
            if (NativeControlHandle != nint.Zero && NativeBridge is not null &&
                NativeBridge.TryGetNativeUiListBoxState(NativeControlHandle, out _, out int nativeSel))
            {
                _selectedIndex = nativeSel;
            }

            return _selectedIndex;
        }
        set
        {
            ThrowIfDestroyed();
            if (value == -1)
            {
                ClearSelection();
                return;
            }

            int count = ItemCount;
            if (value < 0 || value >= count)
            {
                throw new ArgumentOutOfRangeException(nameof(value), $"ListBox selection index {value} is out of range [-1, {count}).");
            }

            if (_selectedIndex == value)
            {
                return;
            }

            _selectedIndex = value;
            if (NativeControlHandle != nint.Zero && NativeBridge is not null)
            {
                _suppressNativeChanged = true;
                try
                {
                    NativeBridge.SetNativeUiListBoxSelectedIndex(NativeControlHandle, value);
                }
                finally
                {
                    _suppressNativeChanged = false;
                }
            }

            SelectionChanged?.Invoke(value);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<IUiControl> Items
    {
        get
        {
            ThrowIfDestroyed();
            return [.. _items];
        }
    }

    /// <inheritdoc/>
    public event Action<int>? SelectionChanged;

    internal void BindNativeSubscription()
    {
        _nativeSub?.Dispose();
        _nativeSub = null;
        if (NativeControlHandle != nint.Zero && NativeBridge is not null)
        {
            _nativeSub = NativeBridge.SubscribeNativeUiListBoxSelectionChanged(NativeControlHandle, OnNativeSelectionChanged);
        }
    }

    /// <inheritdoc/>
    public bool InsertItem(int index, IUiControl child, ulong userData = 0)
    {
        ThrowIfDestroyed();
        ArgumentNullException.ThrowIfNull(child);
        if (child is not UiControlNode childNode)
        {
            throw new ArgumentException("ListBox child control must be created by UiService.", nameof(child));
        }

        ObjectDisposedException.ThrowIf(childNode.IsDestroyed, childNode);
        int clampedIndex = Math.Clamp(index, 0, _items.Count);

        if (NativeControlHandle != nint.Zero && childNode.NativeControlHandle != nint.Zero && NativeBridge is not null)
        {
            if (!NativeBridge.InsertNativeUiListBoxItem(NativeControlHandle, clampedIndex, childNode.NativeControlHandle, userData))
            {
                return false;
            }
        }

        if (childNode.Parent != this)
        {
            AddChild(childNode);
        }

        _items.Insert(clampedIndex, childNode);
        if (_selectedIndex >= clampedIndex)
        {
            _selectedIndex++;
        }

        return true;
    }

    /// <inheritdoc/>
    public void ClearItems()
    {
        ThrowIfDestroyed();
        bool hadSelection = _selectedIndex >= 0;
        foreach (UiControlNode item in _items.ToArray())
        {
            if (!item.IsDestroyed)
            {
                RemoveChild(item);
                item.Destroy();
            }
        }

        _items.Clear();
        _selectedIndex = -1;

        if (NativeControlHandle != nint.Zero && NativeBridge is not null)
        {
            NativeBridge.ClearNativeUiListBoxItems(NativeControlHandle);
        }

        if (hadSelection)
        {
            SelectionChanged?.Invoke(-1);
        }
    }

    /// <inheritdoc/>
    public void ClearSelection()
    {
        ThrowIfDestroyed();
        if (_selectedIndex == -1)
        {
            return;
        }

        _selectedIndex = -1;
        if (NativeControlHandle != nint.Zero && NativeBridge is not null)
        {
            _suppressNativeChanged = true;
            try
            {
                NativeBridge.ClearNativeUiListBoxSelection(NativeControlHandle);
            }
            finally
            {
                _suppressNativeChanged = false;
            }
        }

        SelectionChanged?.Invoke(-1);
    }

    internal void OnNativeSelectionChanged(int newIndex)
    {
        if (_suppressNativeChanged || IsDestroyed)
        {
            return;
        }

        if (newIndex != _selectedIndex)
        {
            _selectedIndex = newIndex;
            SelectionChanged?.Invoke(newIndex);
        }
    }

    protected override void OnBeforeDestroy()
    {
        _nativeSub?.Dispose();
        _nativeSub = null;
        _items.Clear();
    }
}

/// <summary>
/// Slab-safe composite <see cref="IUiItemGrid"/> built atop native <c>Frame</c>/<c>Picture</c>/<c>Label</c>/<c>Button</c>
/// controls and <see cref="ITooltipService"/> with zero server <c>InventorySet</c> coupling.
/// </summary>
public sealed class UiItemGridControl : UiControlNode, IUiItemGrid
{
    private sealed class PlacedGridEntry
    {
        public required UiGridItem Item { get; init; }
        public required UiControlNode Container { get; init; }
        public IPicture? Icon { get; init; }
        public ILabel? StackLabel { get; init; }
        public IButton? HitButton { get; init; }
        public IDisposable? TooltipBinding { get; init; }
    }

    private readonly UiService _ui;
    private readonly TooltipService? _tooltips;
    private readonly InputService? _input;
    private readonly UiItemGridOptions _options;
    private readonly UiGridItem?[,] _occupancy;
    private readonly Dictionary<string, PlacedGridEntry> _entries = new(StringComparer.Ordinal);
    private readonly List<UiControlNode> _cellBackdrops = [];
    private readonly Lock _gate = new();
    private IDisposable? _mouseSub;
    private string? _hoveredTooltipItemId;
    private bool _disposed;

    internal UiItemGridControl(
        string id,
        UiControlNode parent,
        UiService ui,
        TooltipService? tooltips,
        InputService? input,
        UiItemGridOptions options)
        : base(id, "Frame", parent)
    {
        if (options.Columns <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Grid columns must be positive.");
        }

        if (options.Rows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Grid rows must be positive.");
        }

        if (options.CellSize <= 0f || float.IsNaN(options.CellSize))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Grid cell size must be positive.");
        }

        if (options.CellPadding < 0f || float.IsNaN(options.CellPadding))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Grid cell padding must be non-negative.");
        }

        _ui = ui;
        _tooltips = tooltips;
        _input = input;
        _options = options;
        _occupancy = new UiGridItem?[options.Columns, options.Rows];
    }

    internal void InitializeGridVisuals()
    {
        float totalW = (_options.Columns * _options.CellSize) + (Math.Max(0, _options.Columns - 1) * _options.CellPadding);
        float totalH = (_options.Rows * _options.CellSize) + (Math.Max(0, _options.Rows - 1) * _options.CellPadding);
        SetSize(totalW, totalH);

        for (int row = 0; row < _options.Rows; row++)
        {
            for (int col = 0; col < _options.Columns; col++)
            {
                float x = col * (_options.CellSize + _options.CellPadding);
                float y = row * (_options.CellSize + _options.CellPadding);
                var cellFrame = (UiControlNode)_ui.CreateControl(this, UiControlKind.Frame, $"cell_{col}_{row}");
                cellFrame.SetPosition(x, y);
                cellFrame.SetSize(_options.CellSize, _options.CellSize);

                if (!string.IsNullOrWhiteSpace(_options.CellBackgroundTexture))
                {
                    IPicture bg = _ui.CreatePicture(cellFrame, _options.CellBackgroundTexture, $"cell_bg_{col}_{row}");
                    bg.SetPosition(0f, 0f);
                    bg.SetSize(_options.CellSize, _options.CellSize);
                }

                int capturedCol = col;
                int capturedRow = row;
                IButton cellBtn = _ui.CreateButton(cellFrame, string.Empty, $"cell_btn_{col}_{row}");
                cellBtn.SetPosition(0f, 0f);
                cellBtn.SetSize(_options.CellSize, _options.CellSize);
                cellBtn.Clicked += () =>
                {
                    KeyModifiers mods = _input?.ActiveModifiers ?? KeyModifiers.None;
                    TriggerCellClick(capturedCol, capturedRow, MouseButton.Left, mods);
                };

                _cellBackdrops.Add(cellFrame);
            }
        }

        if (_input is not null)
        {
            _mouseSub = _input.OnMouse(HandleMouseEvent, priority: 50, allowWhenTextInputFocused: true);
        }
    }

    /// <inheritdoc/>
    public int Columns => _options.Columns;

    /// <inheritdoc/>
    public int Rows => _options.Rows;

    /// <inheritdoc/>
    public float CellSize => _options.CellSize;

    /// <inheritdoc/>
    public IReadOnlyCollection<UiGridItem> Items
    {
        get
        {
            ThrowIfDestroyed();
            lock (_gate)
            {
                return [.. _entries.Values.Select(e => e.Item)];
            }
        }
    }

    /// <inheritdoc/>
    public event Action<UiGridCellEventArgs>? CellClicked;

    /// <inheritdoc/>
    public event Action<UiGridCellEventArgs>? CellRightClicked;

    /// <inheritdoc/>
    public event Action<UiGridCellEventArgs>? CellCtrlClicked;

    /// <inheritdoc/>
    public event Action<UiGridCellEventArgs>? CellHovered;

    /// <inheritdoc/>
    public bool CanPlaceItem(int col, int row, int width = 1, int height = 1, string? ignoreItemId = null)
    {
        ThrowIfDestroyed();
        if (width < 1 || height < 1 || col < 0 || row < 0 || col + width > _options.Columns || row + height > _options.Rows)
        {
            return false;
        }

        lock (_gate)
        {
            for (int r = row; r < row + height; r++)
            {
                for (int c = col; c < col + width; c++)
                {
                    UiGridItem? existing = _occupancy[c, r];
                    if (existing is not null &&
                        (ignoreItemId is null || !string.Equals(existing.ItemId, ignoreItemId, StringComparison.Ordinal)))
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }

    /// <inheritdoc/>
    public bool PlaceItem(UiGridItem item)
    {
        ThrowIfDestroyed();
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.ItemId))
        {
            throw new ArgumentException("Grid item ItemId cannot be null or whitespace.", nameof(item));
        }

        lock (_gate)
        {
            if (_entries.ContainsKey(item.ItemId))
            {
                return false;
            }

            if (item.Width < 1 || item.Height < 1 ||
                item.Col < 0 || item.Row < 0 ||
                item.Col + item.Width > _options.Columns ||
                item.Row + item.Height > _options.Rows)
            {
                return false;
            }

            for (int r = item.Row; r < item.Row + item.Height; r++)
            {
                for (int c = item.Col; c < item.Col + item.Width; c++)
                {
                    if (_occupancy[c, r] is not null)
                    {
                        return false;
                    }
                }
            }

            float x = item.Col * (_options.CellSize + _options.CellPadding);
            float y = item.Row * (_options.CellSize + _options.CellPadding);
            float w = (item.Width * _options.CellSize) + (Math.Max(0, item.Width - 1) * _options.CellPadding);
            float h = (item.Height * _options.CellSize) + (Math.Max(0, item.Height - 1) * _options.CellPadding);

            var container = (UiControlNode)_ui.CreateControl(this, UiControlKind.Frame, $"item_{item.ItemId}");
            container.SetPosition(x, y);
            container.SetSize(w, h);
            container.SetProperty("rarity", item.Rarity.ToString());

            IPicture? icon = null;
            if (!string.IsNullOrWhiteSpace(item.IconPath))
            {
                icon = _ui.CreatePicture(container, item.IconPath, $"item_icon_{item.ItemId}");
                icon.SetPosition(0f, 0f);
                icon.SetSize(w, h);
            }

            ILabel? stackLabel = null;
            if (item.StackSize > 1)
            {
                stackLabel = _ui.CreateLabel(container, item.StackSize.ToString(CultureInfo.InvariantCulture), $"item_stack_{item.ItemId}");
                stackLabel.SetPosition(2f, 2f);
                stackLabel.SetSize(w - 4f, 18f);
            }

            IButton hitBtn = _ui.CreateButton(container, string.Empty, $"item_hit_{item.ItemId}");
            hitBtn.SetPosition(0f, 0f);
            hitBtn.SetSize(w, h);
            int itemCol = item.Col;
            int itemRow = item.Row;
            hitBtn.Clicked += () =>
            {
                KeyModifiers mods = _input?.ActiveModifiers ?? KeyModifiers.None;
                TriggerCellClick(itemCol, itemRow, MouseButton.Left, mods);
            };

            IDisposable? tooltipBinding = null;
            if (_options.ShowTooltipsOnHover && _tooltips is not null)
            {
                tooltipBinding = _tooltips.AttachTooltip(hitBtn, () => BuildTooltipContent(item));
            }

            for (int r = item.Row; r < item.Row + item.Height; r++)
            {
                for (int c = item.Col; c < item.Col + item.Width; c++)
                {
                    _occupancy[c, r] = item;
                }
            }

            _entries[item.ItemId] = new PlacedGridEntry
            {
                Item = item,
                Container = container,
                Icon = icon,
                StackLabel = stackLabel,
                HitButton = hitBtn,
                TooltipBinding = tooltipBinding,
            };

            return true;
        }
    }

    /// <inheritdoc/>
    public bool RemoveItem(string itemId)
    {
        ThrowIfDestroyed();
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return false;
        }

        PlacedGridEntry? removed;
        bool hideTooltip = false;
        lock (_gate)
        {
            if (!_entries.Remove(itemId, out removed))
            {
                return false;
            }

            UiGridItem item = removed.Item;
            for (int r = item.Row; r < item.Row + item.Height; r++)
            {
                for (int c = item.Col; c < item.Col + item.Width; c++)
                {
                    if (ReferenceEquals(_occupancy[c, r], item))
                    {
                        _occupancy[c, r] = null;
                    }
                }
            }

            if (string.Equals(_hoveredTooltipItemId, itemId, StringComparison.Ordinal))
            {
                _hoveredTooltipItemId = null;
                hideTooltip = true;
            }
        }

        removed.TooltipBinding?.Dispose();
        if (!removed.Container.IsDestroyed)
        {
            RemoveChild(removed.Container);
            removed.Container.Destroy();
        }

        if (hideTooltip)
        {
            _tooltips?.HideTooltip();
        }

        return true;
    }

    /// <inheritdoc/>
    public void Clear()
    {
        ThrowIfDestroyed();
        PlacedGridEntry[] snapshot;
        bool hideTooltip;
        lock (_gate)
        {
            snapshot = [.. _entries.Values];
            _entries.Clear();
            Array.Clear(_occupancy);
            hideTooltip = _hoveredTooltipItemId is not null;
            _hoveredTooltipItemId = null;
        }

        foreach (PlacedGridEntry entry in snapshot)
        {
            entry.TooltipBinding?.Dispose();
            if (!entry.Container.IsDestroyed)
            {
                RemoveChild(entry.Container);
                entry.Container.Destroy();
            }
        }

        if (hideTooltip)
        {
            _tooltips?.HideTooltip();
        }
    }

    /// <inheritdoc/>
    public UiGridItem? GetItemAt(int col, int row)
    {
        ThrowIfDestroyed();
        if (col < 0 || row < 0 || col >= _options.Columns || row >= _options.Rows)
        {
            return null;
        }

        lock (_gate)
        {
            return _occupancy[col, row];
        }
    }

    /// <inheritdoc/>
    public UiGridItem? GetItemById(string itemId)
    {
        ThrowIfDestroyed();
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return null;
        }

        lock (_gate)
        {
            return _entries.TryGetValue(itemId, out PlacedGridEntry? entry) ? entry.Item : null;
        }
    }

    /// <inheritdoc/>
    public (int Col, int Row)? FindFreeCell(int width = 1, int height = 1)
    {
        ThrowIfDestroyed();
        if (width < 1 || height < 1 || width > _options.Columns || height > _options.Rows)
        {
            return null;
        }

        lock (_gate)
        {
            for (int row = 0; row <= _options.Rows - height; row++)
            {
                for (int col = 0; col <= _options.Columns - width; col++)
                {
                    bool free = true;
                    for (int r = row; r < row + height && free; r++)
                    {
                        for (int c = col; c < col + width; c++)
                        {
                            if (_occupancy[c, r] is not null)
                            {
                                free = false;
                                break;
                            }
                        }
                    }

                    if (free)
                    {
                        return (col, row);
                    }
                }
            }

            return null;
        }
    }

    /// <inheritdoc/>
    public void TriggerCellClick(int col, int row, MouseButton button = MouseButton.Left, KeyModifiers modifiers = KeyModifiers.None)
    {
        ThrowIfDestroyed();
        if (col < 0 || row < 0 || col >= _options.Columns || row >= _options.Rows)
        {
            throw new ArgumentOutOfRangeException(nameof(col), $"Grid cell ({col}, {row}) is outside ({_options.Columns}x{_options.Rows}).");
        }

        UiGridItem? item = GetItemAt(col, row);
        var args = new UiGridCellEventArgs(col, row, item, modifiers);
        if (button == MouseButton.Right)
        {
            CellRightClicked?.Invoke(args);
        }
        else if (button == MouseButton.Left && (modifiers & KeyModifiers.Control) != 0)
        {
            CellCtrlClicked?.Invoke(args);
        }
        else
        {
            CellClicked?.Invoke(args);
        }
    }

    /// <inheritdoc/>
    public void TriggerCellHover(int col, int row)
    {
        ThrowIfDestroyed();
        if (col < 0 || row < 0 || col >= _options.Columns || row >= _options.Rows)
        {
            throw new ArgumentOutOfRangeException(nameof(col), $"Grid cell ({col}, {row}) is outside ({_options.Columns}x{_options.Rows}).");
        }

        UiGridItem? item = GetItemAt(col, row);
        KeyModifiers mods = _input?.ActiveModifiers ?? KeyModifiers.None;
        CellHovered?.Invoke(new UiGridCellEventArgs(col, row, item, mods));

        if (_options.ShowTooltipsOnHover && _tooltips is not null)
        {
            if (item is not null && BuildTooltipContent(item) is { } tooltip)
            {
                UiRect bounds = Rect;
                float cellStep = _options.CellSize + _options.CellPadding;
                float tipX = bounds.X + ((col + item.Width) * cellStep) + 6f;
                float tipY = bounds.Y + (row * cellStep);
                lock (_gate)
                {
                    _hoveredTooltipItemId = item.ItemId;
                }

                _tooltips.ShowTooltip(tooltip, tipX, tipY);
            }
            else
            {
                bool wasGridTooltip;
                lock (_gate)
                {
                    wasGridTooltip = _hoveredTooltipItemId is not null;
                    _hoveredTooltipItemId = null;
                }

                if (wasGridTooltip)
                {
                    _tooltips.HideTooltip();
                }
            }
        }
    }

    private void HandleMouseEvent(MouseEventArgs args)
    {
        if (_disposed || IsDestroyed || !IsEffectivelyVisible())
        {
            return;
        }

        if (args.Kind != InputEventKind.Down || args.Button != MouseButton.Right)
        {
            return;
        }

        UiRect bounds = Rect;
        if (bounds.Width <= 0f || bounds.Height <= 0f)
        {
            return;
        }

        float relX = args.X - bounds.X;
        float relY = args.Y - bounds.Y;
        if (relX < 0f || relY < 0f || relX >= bounds.Width || relY >= bounds.Height)
        {
            return;
        }

        float step = _options.CellSize + _options.CellPadding;
        int col = Math.Clamp((int)(relX / step), 0, _options.Columns - 1);
        int row = Math.Clamp((int)(relY / step), 0, _options.Rows - 1);
        TriggerCellClick(col, row, MouseButton.Right, args.Modifiers);
    }

    private static TooltipContent? BuildTooltipContent(UiGridItem item)
    {
        string title = !string.IsNullOrWhiteSpace(item.Title) ? item.Title : item.ItemId;
        IReadOnlyList<TooltipLine> lines = item.TooltipLines ?? [];
        if (string.IsNullOrWhiteSpace(title) && lines.Count == 0)
        {
            return null;
        }

        return TooltipContent.FromLines(title, lines, item.Rarity);
    }

    protected override void OnBeforeDestroy()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mouseSub?.Dispose();
        _mouseSub = null;

        PlacedGridEntry[] snapshot;
        bool hideTooltip;
        lock (_gate)
        {
            snapshot = [.. _entries.Values];
            _entries.Clear();
            Array.Clear(_occupancy);
            hideTooltip = _hoveredTooltipItemId is not null;
            _hoveredTooltipItemId = null;
        }

        foreach (PlacedGridEntry entry in snapshot)
        {
            entry.TooltipBinding?.Dispose();
        }

        _cellBackdrops.Clear();
        if (hideTooltip)
        {
            _tooltips?.HideTooltip();
        }
    }

    /// <inheritdoc/>
    public void Dispose() => Destroy();
}
