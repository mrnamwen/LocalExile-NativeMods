using System.Globalization;
using LocalExile.Client.Host.Native;
using LocalExile.Client.Sdk.Engine;

namespace LocalExile.Client.Host.Services;

/// <summary>
/// Pure geometry solver for <see cref="ScreenSlot.LeagueCardModes"/> checkbox injection inside
/// PoE 3.29's <c>500×905</c> <c>LeagueCard</c> layout budget (verified in RE report #9).
/// </summary>
public static class LeagueCardLayoutSolver
{
    /// <summary>Stock league card width in virtual UI units.</summary>
    public const float CardWidth = 500f;

    /// <summary>Stock league card height in virtual UI units.</summary>
    public const float CardHeight = 905f;

    /// <summary>Horizontal card padding (<c>0x1436ae774 = 74.0f</c>).</summary>
    public const float HorizontalPadding = 74f;

    /// <summary>Maximum equalized checkbox width (<c>500 - 74 = 426.0f</c>).</summary>
    public const float MaxCheckboxWidth = CardWidth - HorizontalPadding;

    /// <summary>Bottom Y limit inside the card leaving 20 units bottom margin (<c>905 - 20 = 885.0f</c>).</summary>
    public const float BottomLimitY = CardHeight - 20f;

    /// <summary>Default gap between description label and mode <c>StackLayout</c> (<c>0x1436ae688 = 48.0f</c>).</summary>
    public const float DefaultTopGap = 48f;

    /// <summary>Compacted gap between description label and mode <c>StackLayout</c> when vertical budget is tight.</summary>
    public const float CompactTopGap = 16f;

    /// <summary>Stock checkbox height (<c>"Button"."Size".y = 59.0f</c>).</summary>
    public const float StockCheckboxHeight = 59f;

    /// <summary>Minimum compacted checkbox height.</summary>
    public const float MinCheckboxHeight = 36f;

    /// <summary>Computed compaction and width-equalization result for a single <c>LeagueCard</c>.</summary>
    /// <param name="ModeStackY">Target Y coordinate for the mode <c>StackLayout</c>.</param>
    /// <param name="GapCompacted"><c>true</c> if the description-to-stack gap was tightened from 48 to 16.</param>
    /// <param name="CheckboxHeight">Equalized height applied to every checkbox in the mode stack.</param>
    /// <param name="EqualizedWidth">Equalized width applied to every checkbox in the mode stack (clamped to 426).</param>
    /// <param name="TotalCheckboxes">Total checkbox count (<c>stock + mod</c>).</param>
    public readonly record struct LeagueCardLayoutResult(
        float ModeStackY,
        bool GapCompacted,
        float CheckboxHeight,
        float EqualizedWidth,
        int TotalCheckboxes);

    /// <summary>
    /// Computes the equalized checkbox width, compacted checkbox height, and mode <c>StackLayout</c> Y offset
    /// for a <c>LeagueCard</c> containing stock plus injected mod checkboxes.
    /// </summary>
    public static LeagueCardLayoutResult Solve(
        float descBottomY,
        float currentModeStackY,
        int stockCount,
        int modCount,
        ReadOnlySpan<float> checkboxNaturalWidths)
    {
        int total = Math.Max(0, stockCount) + Math.Max(0, modCount);
        float maxW = 0f;
        foreach (float w in checkboxNaturalWidths)
        {
            if (w > maxW)
            {
                maxW = w;
            }
        }

        float eqWidth = Math.Min(MaxCheckboxWidth, maxW);
        if (total == 0)
        {
            return new LeagueCardLayoutResult(currentModeStackY, false, StockCheckboxHeight, eqWidth, 0);
        }

        float stackY = currentModeStackY;
        float availH = BottomLimitY - stackY;
        bool gapCompacted = false;

        if (total * StockCheckboxHeight > availH)
        {
            float compactY = descBottomY + CompactTopGap;
            if (compactY < stackY)
            {
                stackY = compactY;
                availH = BottomLimitY - stackY;
                gapCompacted = true;
            }
        }

        float cbH = availH > 0f
            ? Math.Min(StockCheckboxHeight, Math.Max(MinCheckboxHeight, MathF.Floor(availH / total)))
            : MinCheckboxHeight;

        return new LeagueCardLayoutResult(stackY, gapCompacted, cbH, eqWidth, total);
    }
}

/// <summary>
/// Deterministic conflict-free auto-flow layout solver for managed <see cref="ScreenSlot"/> toolbars.
/// </summary>
public static class SlotFlowLayoutEngine
{
    /// <summary>Input item descriptor for slot flow calculation.</summary>
    public readonly record struct SlotFlowItem(string Id, float Width, float Height, float Spacing);

    /// <summary>Slot flow bounds and direction parameters.</summary>
    public readonly record struct SlotLayoutSpec(
        float OriginX,
        float OriginY,
        float MaxSpan,
        float DefaultItemWidth,
        float DefaultItemHeight,
        bool IsVertical,
        bool RightToLeft = false);

    /// <summary>Returns the verified 3.29.3d virtual-coordinate layout spec for <paramref name="slot"/>.</summary>
    public static SlotLayoutSpec GetSpec(ScreenSlot slot, bool minimapVisible = false, bool rightSidePanelOpen = false) => slot switch
    {
        ScreenSlot.InventoryToolbar => new SlotLayoutSpec(OriginX: 24f, OriginY: 682f, MaxSpan: 920f, DefaultItemWidth: 112f, DefaultItemHeight: 34f, IsVertical: false),
        ScreenSlot.StashHeader => new SlotLayoutSpec(OriginX: 24f, OriginY: 10f, MaxSpan: 860f, DefaultItemWidth: 116f, DefaultItemHeight: 32f, IsVertical: false),
        ScreenSlot.StashFooter => new SlotLayoutSpec(OriginX: 24f, OriginY: 1300f, MaxSpan: 560f, DefaultItemWidth: 116f, DefaultItemHeight: 32f, IsVertical: false),
        ScreenSlot.MapTabBar => new SlotLayoutSpec(OriginX: 24f, OriginY: 890f, MaxSpan: 840f, DefaultItemWidth: 124f, DefaultItemHeight: 32f, IsVertical: false),
        ScreenSlot.BlightTabBar => new SlotLayoutSpec(OriginX: 24f, OriginY: 977f, MaxSpan: 840f, DefaultItemWidth: 130f, DefaultItemHeight: 32f, IsVertical: false),
        ScreenSlot.PassiveTreeHeader => new SlotLayoutSpec(OriginX: 340f, OriginY: 16f, MaxSpan: 960f, DefaultItemWidth: 130f, DefaultItemHeight: 34f, IsVertical: false),
        ScreenSlot.HudTopRight => new SlotLayoutSpec(
            OriginX: 2544f - (rightSidePanelOpen ? 986f : (minimapVisible ? 420f : 0f)),
            OriginY: 16f,
            MaxSpan: 900f,
            DefaultItemWidth: 320f,
            DefaultItemHeight: 56f,
            IsVertical: true,
            RightToLeft: true),
        ScreenSlot.CreateCharacterBar => new SlotLayoutSpec(OriginX: 24f, OriginY: 12f, MaxSpan: 720f, DefaultItemWidth: 150f, DefaultItemHeight: 36f, IsVertical: false),
        _ => new SlotLayoutSpec(OriginX: 0f, OriginY: 0f, MaxSpan: 426f, DefaultItemWidth: 220f, DefaultItemHeight: 48f, IsVertical: true),
    };

    /// <summary>Computes non-overlapping rectangles for all ordered items in a slot.</summary>
    public static UiRect[] ComputeRects(SlotLayoutSpec spec, ReadOnlySpan<SlotFlowItem> items)
    {
        if (items.Length == 0)
        {
            return [];
        }

        var results = new UiRect[items.Length];
        float cursor = 0f;

        for (int i = 0; i < items.Length; i++)
        {
            float w = items[i].Width > 0f ? items[i].Width : spec.DefaultItemWidth;
            float h = items[i].Height > 0f ? items[i].Height : spec.DefaultItemHeight;
            float gap = Math.Max(0f, items[i].Spacing);

            if (spec.IsVertical)
            {
                float x = spec.RightToLeft ? Math.Max(0f, spec.OriginX - w) : spec.OriginX;
                float y = spec.OriginY + cursor;
                results[i] = new UiRect(x, y, w, h);
                cursor += h + gap;
            }
            else
            {
                float x = spec.RightToLeft ? Math.Max(0f, spec.OriginX - cursor - w) : spec.OriginX + cursor;
                float y = spec.OriginY;
                results[i] = new UiRect(x, y, w, h);
                cursor += w + gap;
            }
        }

        return results;
    }
}

/// <summary>
/// Tracks in-game and pre-game screen transitions, active stash tab metadata, managed conflict-free
/// auto-flow toolbar slots, and <see cref="ScreenSlot.LeagueCardModes"/> checkbox injection.
/// </summary>
public sealed class ScreensService : IScreensService, IDisposable
{
    private sealed class ManagedSlotAttachment : ISlotAttachment
    {
        private readonly ScreensService _owner;
        private readonly Action _onDispose;
        private bool _disposed;

        public ManagedSlotAttachment(
            ScreensService owner,
            ScreenSlot slot,
            string id,
            string virtualUiPath,
            SlotAttachmentOptions options,
            long sequence,
            nint rawParentHandle,
            float rawX,
            float rawY,
            Action? buttonClickCallback,
            IDisposable? generatedOverlay,
            Action onDispose)
        {
            _owner = owner;
            Slot = slot;
            Id = id;
            VirtualUiPath = virtualUiPath;
            Options = options;
            Sequence = sequence;
            RawParentHandle = rawParentHandle;
            RawX = rawX;
            RawY = rawY;
            ButtonClickCallback = buttonClickCallback;
            GeneratedOverlay = generatedOverlay;
            _onDispose = onDispose;
        }

        public ScreenSlot Slot { get; }
        public string Id { get; }
        public string VirtualUiPath { get; }
        public SlotAttachmentOptions Options { get; }
        public long Sequence { get; }
        public nint RawParentHandle { get; }
        public float RawX { get; }
        public float RawY { get; }
        public Action? ButtonClickCallback { get; }
        public IDisposable? GeneratedOverlay { get; }
        public bool IsRawAttachment => RawParentHandle != nint.Zero;
        public int Order => Options.Order;
        public bool IsAttached { get; internal set; }
        public IUiControl? Control { get; internal set; }
        public UiRect ComputedRect { get; internal set; }
        internal nint MountedParentHandle { get; set; }
        internal string? MountedWindowId { get; set; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _onDispose();
            GeneratedOverlay?.Dispose();
        }
    }

    private sealed class ManagedLeagueModeRegistration : ILeagueModeRegistration
    {
        private readonly ScreensService _owner;
        private readonly Dictionary<string, bool> _checkedByLeague = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ICheckBox> _liveCheckBoxes = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<IDisposable> _liveSubscriptions = [];
        private readonly Lock _stateGate = new();
        private bool _lastChecked;
        private bool _disposed;

        public ManagedLeagueModeRegistration(ScreensService owner, LeagueModeCheckboxOptions options, long sequence)
        {
            _owner = owner;
            Options = options;
            Sequence = sequence;
            _lastChecked = options.DefaultChecked;
        }

        public LeagueModeCheckboxOptions Options { get; }
        public long Sequence { get; }
        public string Id => Options.Id;
        public string Caption => Options.Caption;
        public bool IsChecked { get { lock (_stateGate) { return _lastChecked; } } }

        public event Action<string, bool>? CheckedChanged;

        public bool AppliesToLeague(string leagueId) => Options.LeagueFilter is null || Options.LeagueFilter(leagueId);

        public bool GetChecked(string leagueId)
        {
            lock (_stateGate)
            {
                return !string.IsNullOrWhiteSpace(leagueId) && _checkedByLeague.TryGetValue(leagueId, out bool value)
                    ? value
                    : Options.DefaultChecked;
            }
        }

        public void SetChecked(string leagueId, bool isChecked)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(leagueId);
            ICheckBox? live = null;
            bool changed;
            lock (_stateGate)
            {
                bool prev = _checkedByLeague.TryGetValue(leagueId, out bool existing) ? existing : Options.DefaultChecked;
                changed = prev != isChecked || !_checkedByLeague.ContainsKey(leagueId);
                _checkedByLeague[leagueId] = isChecked;
                _lastChecked = isChecked;
                _liveCheckBoxes.TryGetValue(leagueId, out live);
            }

            if (live is not null && live.IsChecked != isChecked)
            {
                live.IsChecked = isChecked;
            }
            else if (changed)
            {
                CheckedChanged?.Invoke(leagueId, isChecked);
            }
        }

        internal void ClearLiveBindings()
        {
            IDisposable[] subs;
            lock (_stateGate)
            {
                subs = [.. _liveSubscriptions];
                _liveSubscriptions.Clear();
                _liveCheckBoxes.Clear();
            }

            foreach (IDisposable sub in subs)
            {
                try { sub.Dispose(); }
                catch { }
            }
        }

        internal void BindLiveCheckBox(string leagueId, ICheckBox checkBox, IDisposable? nativeSub)
        {
            lock (_stateGate)
            {
                _liveCheckBoxes[leagueId] = checkBox;
                if (nativeSub is not null)
                {
                    _liveSubscriptions.Add(nativeSub);
                }
            }

            checkBox.CheckedChanged += isChecked =>
            {
                lock (_stateGate)
                {
                    _checkedByLeague[leagueId] = isChecked;
                    _lastChecked = isChecked;
                }

                CheckedChanged?.Invoke(leagueId, isChecked);
            };
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ClearLiveBindings();
            _owner.UnregisterLeagueMode(this);
        }
    }

    private readonly INativeBridge _bridge;
    private readonly UiService _ui;
    private readonly FileOverlayService _files;
    private readonly Lock _gate = new();
    private readonly List<ManagedSlotAttachment> _slotAttachments = [];
    private readonly List<ManagedLeagueModeRegistration> _leagueModes = [];
    private readonly IDisposable _leagueFillSub;
    private long _nextSequence;
    private PreGameScreen _currentPreGameScreen = PreGameScreen.Unknown;
    private GamePanel _activePanels = GamePanel.None;
    private StashTabInfo? _activeStashTab;
    private nint _lastInGameUiHandle;
    private nint _lastOverlayHandle;
    private bool _lastMinimapVisible;
    private bool _lastRightPanelOpen;
    private IReadOnlyList<LeagueCardInfo> _activeLeagueCards = [];
    private readonly Dictionary<int, LeagueCardLayoutSolver.LeagueCardLayoutResult> _lastLeagueCardLayouts = [];
    private bool _disposed;

    /// <summary>Creates a new <see cref="ScreensService"/>.</summary>
    public ScreensService(INativeBridge bridge, UiService ui, FileOverlayService files)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _leagueFillSub = _bridge.RegisterLeagueOverlayFilledCallback(HandleLeagueOverlayFilled);
    }

    /// <inheritdoc/>
    public PreGameScreen CurrentPreGameScreen => _currentPreGameScreen;

    /// <inheritdoc/>
    public GamePanel ActivePanels => _activePanels;

    /// <inheritdoc/>
    public StashTabInfo? ActiveStashTab => _activeStashTab;

    /// <inheritdoc/>
    public IReadOnlyList<LeagueCardInfo> ActiveLeagueCards => _activeLeagueCards;

    /// <summary>Last computed <see cref="LeagueCardLayoutSolver.LeagueCardLayoutResult"/> per card index (for diagnostics/tests).</summary>
    public IReadOnlyDictionary<int, LeagueCardLayoutSolver.LeagueCardLayoutResult> LastLeagueCardLayouts
    {
        get { lock (_gate) { return new Dictionary<int, LeagueCardLayoutSolver.LeagueCardLayoutResult>(_lastLeagueCardLayouts); } }
    }

    /// <inheritdoc/>
    public event Action<ScreenChangedEventArgs>? ScreenChanged;

    /// <inheritdoc/>
    public event Action<PreGameScreenChangedEventArgs>? PreGameScreenChanged;

    /// <inheritdoc/>
    public event Action<ActiveStashTabChangedEventArgs>? ActiveStashTabChanged;

    /// <inheritdoc/>
    public event Action<LeagueOverlayFilledEventArgs>? LeagueOverlayFilled;

    /// <inheritdoc/>
    public bool IsPanelOpen(GamePanel panel) => panel != GamePanel.None && (_activePanels & panel) == panel;

    /// <inheritdoc/>
    public IUiControl? GetPanelControl(GamePanel panel)
    {
        nint handle = _bridge.GetPanelControlHandle(panel);
        return handle != nint.Zero ? _ui.WrapControl(handle) : null;
    }

    /// <inheritdoc/>
    public ISlotAttachment RegisterSlot(ScreenSlot slot, string id, string virtualUiPath, SlotAttachmentOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualUiPath);

        long seq = Interlocked.Increment(ref _nextSequence);
        SlotAttachmentOptions opts = options ?? new SlotAttachmentOptions();
        ManagedSlotAttachment? attachment = null;
        attachment = new ManagedSlotAttachment(
            this,
            slot,
            id.Trim(),
            virtualUiPath,
            opts,
            seq,
            nint.Zero,
            0f,
            0f,
            buttonClickCallback: null,
            generatedOverlay: null,
            onDispose: () => RemoveSlotAttachment(attachment!));

        lock (_gate)
        {
            if (_slotAttachments.Any(a => !a.IsRawAttachment && a.Slot == slot && string.Equals(a.Id, attachment.Id, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Slot attachment '{attachment.Id}' is already registered in slot '{slot}'.");
            }

            _slotAttachments.Add(attachment);
        }

        SyncSlotAttachments(slot);
        return attachment;
    }

    /// <inheritdoc/>
    public ISlotAttachment RegisterSlotButton(ScreenSlot slot, string id, string text, Action onClick, SlotAttachmentOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(onClick);

        long seq = Interlocked.Increment(ref _nextSequence);
        string safeId = SanitizeControlId(id);
        SlotAttachmentOptions opts = options ?? new SlotAttachmentOptions();
        SlotFlowLayoutEngine.SlotLayoutSpec spec = SlotFlowLayoutEngine.GetSpec(slot);
        float w = opts.Width > 0f ? opts.Width : spec.DefaultItemWidth;
        float h = opts.Height > 0f ? opts.Height : spec.DefaultItemHeight;

        string virtualPath = $"Metadata/UI/Mods/NativeMods/Slots/{slot}_{safeId}_{seq}.ui";
        string escapedText = (text ?? string.Empty).Replace("\"", "\\\"", StringComparison.Ordinal);
        string uiSource = string.Create(
            CultureInfo.InvariantCulture,
            $"begin Frame\n  id = \"slot_{safeId}\"\n  size = {w}, {h}\n  begin Button\n    id = \"btn\"\n    text = \"{escapedText}\"\n    size = {w}, {h}\n  end\nend\n");

        IDisposable overlaySub = _files.RegisterVirtualTextFileForMod("nativemods.slots", virtualPath, uiSource, priority: 100);
        ManagedSlotAttachment? attachment = null;
        attachment = new ManagedSlotAttachment(
            this,
            slot,
            id.Trim(),
            virtualPath,
            opts,
            seq,
            nint.Zero,
            0f,
            0f,
            buttonClickCallback: onClick,
            generatedOverlay: overlaySub,
            onDispose: () => RemoveSlotAttachment(attachment!));

        lock (_gate)
        {
            if (_slotAttachments.Any(a => !a.IsRawAttachment && a.Slot == slot && string.Equals(a.Id, attachment.Id, StringComparison.OrdinalIgnoreCase)))
            {
                overlaySub.Dispose();
                throw new InvalidOperationException($"Slot attachment '{attachment.Id}' is already registered in slot '{slot}'.");
            }

            _slotAttachments.Add(attachment);
        }

        SyncSlotAttachments(slot);
        return attachment;
    }

    /// <inheritdoc/>
    public ILeagueModeRegistration RegisterLeagueCardMode(LeagueModeCheckboxOptions options, Action<string, bool>? onChanged = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Caption);

        long seq = Interlocked.Increment(ref _nextSequence);
        var reg = new ManagedLeagueModeRegistration(this, options, seq);
        if (onChanged is not null)
        {
            reg.CheckedChanged += onChanged;
        }

        lock (_gate)
        {
            if (_leagueModes.Any(m => string.Equals(m.Id, options.Id, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"League card mode checkbox '{options.Id}' is already registered.");
            }

            _leagueModes.Add(reg);
        }

        // If LeagueOverlay is currently open with active cards, inject immediately.
        if (_activeLeagueCards.Count > 0 && _lastOverlayHandle != nint.Zero)
        {
            InjectLeagueCardModes(_activeLeagueCards);
        }

        return reg;
    }

    /// <inheritdoc/>
    public ISlotAttachment AttachToControl(nint parentHandle, string virtualUiPath, string? id = null, float x = 0f, float y = 0f)
    {
        if (parentHandle == nint.Zero)
        {
            throw new ArgumentException("Parent native control handle cannot be zero.", nameof(parentHandle));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(virtualUiPath);

        long seq = Interlocked.Increment(ref _nextSequence);
        string resolvedId = string.IsNullOrWhiteSpace(id) ? $"raw_{seq}" : id.Trim();
        ManagedSlotAttachment? attachment = null;
        attachment = new ManagedSlotAttachment(
            this,
            ScreenSlot.HudTopRight,
            resolvedId,
            virtualUiPath,
            new SlotAttachmentOptions(),
            seq,
            parentHandle,
            x,
            y,
            buttonClickCallback: null,
            generatedOverlay: null,
            onDispose: () => RemoveSlotAttachment(attachment!));

        lock (_gate)
        {
            _slotAttachments.Add(attachment);
        }

        MountAttachment(attachment, parentHandle, new UiRect(x, y, 0f, 0f));
        return attachment;
    }

    /// <inheritdoc/>
    public IReadOnlyList<ISlotAttachment> GetSlotAttachments(ScreenSlot slot)
    {
        lock (_gate)
        {
            return [.. _slotAttachments
                .Where(a => !a.IsRawAttachment && a.Slot == slot)
                .OrderBy(a => a.Order)
                .ThenBy(a => a.Sequence)
                .ThenBy(a => a.Id, StringComparer.OrdinalIgnoreCase)];
        }
    }

    /// <summary>
    /// Polls native screen, panel, and stash tab state on the game thread and synchronizes managed slots.
    /// </summary>
    public void Tick()
    {
        if (_disposed)
        {
            return;
        }

        PreGameScreen nextScreen = _bridge.GetPreGameScreen();
        if (nextScreen != _currentPreGameScreen)
        {
            PreGameScreen prev = _currentPreGameScreen;
            _currentPreGameScreen = nextScreen;

            if (nextScreen != PreGameScreen.LeagueOverlay)
            {
                _activeLeagueCards = [];
                _lastOverlayHandle = nint.Zero;
            }

            if (prev == PreGameScreen.CreateCharacter && nextScreen != PreGameScreen.CreateCharacter)
            {
                DetachSlot(ScreenSlot.CreateCharacterBar);
            }

            PreGameScreenChanged?.Invoke(new PreGameScreenChangedEventArgs(prev, nextScreen));

            if (nextScreen == PreGameScreen.CreateCharacter)
            {
                SyncSlotAttachments(ScreenSlot.CreateCharacterBar);
            }
        }

        // Detect InGameUI pointer rebuild across zone transitions (InGameState::Leave -> Enter).
        nint inGameUi = _bridge.GetInGameUiHandle();
        if (inGameUi != _lastInGameUiHandle)
        {
            _lastInGameUiHandle = inGameUi;
            InvalidateInGameAttachments();
        }

        GamePanel nextPanels = _bridge.GetActivePanels();
        if (nextPanels != _activePanels)
        {
            GamePanel diff = _activePanels ^ nextPanels;
            _activePanels = nextPanels;
            foreach (GamePanel panel in EnumerateIndividualPanels(diff))
            {
                bool isOpen = (_activePanels & panel) == panel;
                IUiControl? ctrl = isOpen ? GetPanelControl(panel) : null;
                ScreenChanged?.Invoke(new ScreenChangedEventArgs(panel, isOpen, ctrl));
            }
        }

        StashTabInfo? nextTab = IsPanelOpen(GamePanel.Stash) ? _bridge.GetActiveStashTab() : null;
        if (!Equals(nextTab, _activeStashTab))
        {
            StashTabInfo? prevTab = _activeStashTab;
            _activeStashTab = nextTab;
            ActiveStashTabChanged?.Invoke(new ActiveStashTabChangedEventArgs(prevTab, nextTab));
        }

        bool minimapVisible = _bridge.IsMinimapVisible();
        bool rightPanelOpen = _bridge.IsRightSidePanelOpen() || IsPanelOpen(GamePanel.Inventory);
        bool hudLayoutChanged = minimapVisible != _lastMinimapVisible || rightPanelOpen != _lastRightPanelOpen;
        _lastMinimapVisible = minimapVisible;
        _lastRightPanelOpen = rightPanelOpen;

        // Sync all managed slots whose target parent handle is active or changed.
        SyncSlotAttachments(ScreenSlot.HudTopRight, forceRelayout: hudLayoutChanged);
        SyncSlotAttachments(ScreenSlot.InventoryToolbar);
        SyncSlotAttachments(ScreenSlot.StashHeader);
        SyncSlotAttachments(ScreenSlot.StashFooter);
        SyncSlotAttachments(ScreenSlot.MapTabBar);
        SyncSlotAttachments(ScreenSlot.BlightTabBar);
        SyncSlotAttachments(ScreenSlot.PassiveTreeHeader);
        if (_currentPreGameScreen == PreGameScreen.CreateCharacter)
        {
            SyncSlotAttachments(ScreenSlot.CreateCharacterBar);
        }
    }

    private void HandleLeagueOverlayFilled(nint overlayHandle, IReadOnlyList<LeagueCardInfo> cards)
    {
        if (_disposed)
        {
            return;
        }

        _lastOverlayHandle = overlayHandle;
        _activeLeagueCards = cards ?? [];

        if (_currentPreGameScreen != PreGameScreen.LeagueOverlay)
        {
            PreGameScreen prev = _currentPreGameScreen;
            _currentPreGameScreen = PreGameScreen.LeagueOverlay;
            PreGameScreenChanged?.Invoke(new PreGameScreenChangedEventArgs(prev, PreGameScreen.LeagueOverlay));
        }

        InjectLeagueCardModes(_activeLeagueCards);
        LeagueOverlayFilled?.Invoke(new LeagueOverlayFilledEventArgs(overlayHandle, _activeLeagueCards));
    }

    private void InjectLeagueCardModes(IReadOnlyList<LeagueCardInfo> cards)
    {
        ManagedLeagueModeRegistration[] registrations;
        lock (_gate)
        {
            registrations = [.. _leagueModes.OrderBy(m => m.Options.Order).ThenBy(m => m.Sequence).ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase)];
            _lastLeagueCardLayouts.Clear();
        }

        foreach (ManagedLeagueModeRegistration reg in registrations)
        {
            reg.ClearLiveBindings();
        }

        foreach (LeagueCardInfo card in cards)
        {
            if (card.ModeStackHandle == nint.Zero)
            {
                continue;
            }

            List<ManagedLeagueModeRegistration> matching = [.. registrations.Where(r => r.AppliesToLeague(card.LeagueId))];
            if (matching.Count == 0)
            {
                continue;
            }

            IReadOnlyList<nint> existingChildren = _bridge.GetNativeUiChildren(card.ModeStackHandle);
            List<nint> allCheckBoxHandles = [.. existingChildren];
            List<float> naturalWidths = [];
            if (card.MaxStockCheckboxWidth > 0f && card.StockCheckboxCount > 0)
            {
                naturalWidths.Add(card.MaxStockCheckboxWidth);
            }

            foreach (nint stockHandle in existingChildren)
            {
                if (_bridge.TryGetNativeUiRect(stockHandle, out UiRect stockRect) && stockRect.Width > 0f)
                {
                    naturalWidths.Add(stockRect.Width);
                }
            }

            foreach (ManagedLeagueModeRegistration reg in matching)
            {
                bool initialChecked = reg.GetChecked(card.LeagueId);
                nint cbHandle = _bridge.CreateNativeUiCheckBoxChild(card.ModeStackHandle, reg.Caption, initialChecked);
                if (cbHandle != nint.Zero)
                {
                    _bridge.SetNativeUiAnchor(cbHandle, xAnchor: 3, xOffset: 0f, yAnchor: 0, yOffset: 0f);
                    _bridge.SetNativeUiAutoWidth(cbHandle, true);
                    _bridge.UpdateNativeUiLayout(cbHandle);

                    float measuredW = reg.Options.NaturalWidth;
                    if (_bridge.TryGetNativeUiRect(cbHandle, out UiRect cbRect) && cbRect.Width > 0f)
                    {
                        measuredW = cbRect.Width;
                    }

                    naturalWidths.Add(measuredW);
                    allCheckBoxHandles.Add(cbHandle);

                    var cbNode = new UiCheckBoxNode($"league_mode_{SanitizeControlId(reg.Id)}_{card.CardIndex}", null)
                    {
                        NativeBridge = _bridge,
                        NativeControlHandle = cbHandle,
                    };
                    cbNode.SetBaseTextSilent(reg.Caption);
                    cbNode.SetProperty("checked", initialChecked ? "true" : "false");
                    IDisposable nativeSub = _bridge.SubscribeNativeUiCheckBoxChanged(cbHandle, cbNode.OnNativeCheckedChanged);
                    reg.BindLiveCheckBox(card.LeagueId, cbNode, nativeSub);
                }
                else
                {
                    naturalWidths.Add(reg.Options.NaturalWidth);
                }
            }

            LeagueCardLayoutSolver.LeagueCardLayoutResult layout = LeagueCardLayoutSolver.Solve(
                card.DescBottomY,
                card.ModeStackY,
                card.StockCheckboxCount,
                matching.Count,
                naturalWidths.ToArray());

            lock (_gate)
            {
                _lastLeagueCardLayouts[card.CardIndex] = layout;
            }

            if (layout.GapCompacted)
            {
                _bridge.SetNativeUiPosition(card.ModeStackHandle, 0f, layout.ModeStackY);
            }

            foreach (nint cbHandle in allCheckBoxHandles)
            {
                _bridge.SetNativeUiAutoWidth(cbHandle, false);
                _bridge.SetNativeUiWidth(cbHandle, layout.EqualizedWidth);
                _bridge.SetNativeUiHeight(cbHandle, layout.CheckboxHeight);
            }

            _bridge.UpdateNativeUiLayout(card.ModeStackHandle);
            if (card.CardHandle != nint.Zero)
            {
                _bridge.UpdateNativeUiLayout(card.CardHandle);
            }
        }
    }

    private void SyncSlotAttachments(ScreenSlot slot, bool forceRelayout = false)
    {
        if (slot == ScreenSlot.LeagueCardModes)
        {
            return;
        }

        ManagedSlotAttachment[] items;
        lock (_gate)
        {
            items = [.. _slotAttachments
                .Where(a => !a.IsRawAttachment && a.Slot == slot)
                .OrderBy(a => a.Order)
                .ThenBy(a => a.Sequence)
                .ThenBy(a => a.Id, StringComparer.OrdinalIgnoreCase)];
        }

        if (items.Length == 0)
        {
            return;
        }

        nint targetParent = ResolveSlotParentHandle(slot);
        if (targetParent == nint.Zero)
        {
            foreach (ManagedSlotAttachment item in items)
            {
                if (item.IsAttached)
                {
                    UnmountAttachment(item, destroyNative: false);
                }
            }

            return;
        }

        SlotFlowLayoutEngine.SlotLayoutSpec spec = SlotFlowLayoutEngine.GetSpec(slot, _lastMinimapVisible, _lastRightPanelOpen);
        var flowItems = new SlotFlowLayoutEngine.SlotFlowItem[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            flowItems[i] = new SlotFlowLayoutEngine.SlotFlowItem(
                items[i].Id,
                items[i].Options.Width,
                items[i].Options.Height,
                items[i].Options.Spacing);
        }

        UiRect[] rects = SlotFlowLayoutEngine.ComputeRects(spec, flowItems);
        for (int i = 0; i < items.Length; i++)
        {
            ManagedSlotAttachment item = items[i];
            UiRect rect = rects[i];

            if (!item.IsAttached || item.MountedParentHandle != targetParent)
            {
                if (item.IsAttached)
                {
                    UnmountAttachment(item, destroyNative: false);
                }

                MountAttachment(item, targetParent, rect);
            }
            else if (forceRelayout || item.ComputedRect != rect)
            {
                item.ComputedRect = rect;
                if (item.Control is not null)
                {
                    item.Control.SetPosition(rect.X, rect.Y);
                    if (rect.Width > 0f && rect.Height > 0f)
                    {
                        item.Control.SetSize(rect.Width, rect.Height);
                    }
                }
            }
        }
    }

    private nint ResolveSlotParentHandle(ScreenSlot slot)
    {
        nint direct = _bridge.GetSlotParentHandle(slot);
        if (direct != nint.Zero)
        {
            return direct;
        }

        return slot switch
        {
            ScreenSlot.InventoryToolbar => IsPanelOpen(GamePanel.Inventory) ? _bridge.GetPanelControlHandle(GamePanel.Inventory) : nint.Zero,
            ScreenSlot.StashHeader or ScreenSlot.StashFooter => IsPanelOpen(GamePanel.Stash) ? _bridge.GetPanelControlHandle(GamePanel.Stash) : nint.Zero,
            ScreenSlot.MapTabBar => IsPanelOpen(GamePanel.MapStash) ? _bridge.GetPanelControlHandle(GamePanel.MapStash) : nint.Zero,
            ScreenSlot.BlightTabBar => IsPanelOpen(GamePanel.BlightStash) ? _bridge.GetPanelControlHandle(GamePanel.BlightStash) : nint.Zero,
            ScreenSlot.PassiveTreeHeader => IsPanelOpen(GamePanel.PassiveTree) ? _bridge.GetPanelControlHandle(GamePanel.PassiveTree) : nint.Zero,
            ScreenSlot.HudTopRight => _bridge.GetInGameUiHandle() is var hud && hud != nint.Zero ? hud : (IsPanelOpen(GamePanel.Hud) ? _bridge.GetPanelControlHandle(GamePanel.Hud) : nint.Zero),
            ScreenSlot.CreateCharacterBar => _currentPreGameScreen == PreGameScreen.CreateCharacter ? _bridge.GetNativeUiRoot() : nint.Zero,
            _ => nint.Zero,
        };
    }

    private void MountAttachment(ManagedSlotAttachment attachment, nint parentHandle, UiRect rect)
    {
        IUiControl? parent = _ui.WrapControl(parentHandle);
        if (parent is null)
        {
            return;
        }

        string winId = $"__slot_{attachment.Slot}_{SanitizeControlId(attachment.Id)}_{attachment.Sequence}";
        IUiWindow win = _ui.CreateWindowInto(parent, attachment.VirtualUiPath, winId);
        win.SetPosition(rect.X, rect.Y);
        if (rect.Width > 0f && rect.Height > 0f)
        {
            win.SetSize(rect.Width, rect.Height);
        }

        if (attachment.ButtonClickCallback is not null && win.Find<IButton>("btn") is { } button)
        {
            button.Clicked += attachment.ButtonClickCallback;
        }

        attachment.Control = win;
        attachment.ComputedRect = rect;
        attachment.MountedParentHandle = parentHandle;
        attachment.MountedWindowId = winId;
        attachment.IsAttached = true;
    }

    private static void UnmountAttachment(ManagedSlotAttachment attachment, bool destroyNative)
    {
        if (attachment.Control is UiWindowNode win)
        {
            if (destroyNative)
            {
                win.Close();
            }
            else
            {
                // Native parent was already torn down by InGameState::Leave — avoid double-freeing stale native pointers.
                win.NativeControlHandle = nint.Zero;
                foreach (UiControlNode child in win.MutableChildren)
                {
                    ClearNativeHandlesRecursive(child);
                }

                win.Close();
            }
        }

        attachment.Control = null;
        attachment.MountedParentHandle = nint.Zero;
        attachment.MountedWindowId = null;
        attachment.IsAttached = false;
    }

    private static void ClearNativeHandlesRecursive(UiControlNode node)
    {
        node.NativeControlHandle = nint.Zero;
        foreach (UiControlNode child in node.MutableChildren)
        {
            ClearNativeHandlesRecursive(child);
        }
    }

    private void DetachSlot(ScreenSlot slot)
    {
        ManagedSlotAttachment[] items;
        lock (_gate)
        {
            items = [.. _slotAttachments.Where(a => !a.IsRawAttachment && a.Slot == slot)];
        }

        foreach (ManagedSlotAttachment item in items)
        {
            if (item.IsAttached)
            {
                UnmountAttachment(item, destroyNative: false);
            }
        }
    }

    private void InvalidateInGameAttachments()
    {
        ManagedSlotAttachment[] items;
        lock (_gate)
        {
            items = [.. _slotAttachments.Where(a => !a.IsRawAttachment && a.Slot is not (ScreenSlot.CreateCharacterBar or ScreenSlot.LeagueCardModes))];
        }

        foreach (ManagedSlotAttachment item in items)
        {
            if (item.IsAttached)
            {
                UnmountAttachment(item, destroyNative: false);
            }
        }
    }

    private void RemoveSlotAttachment(ManagedSlotAttachment attachment)
    {
        bool removed;
        lock (_gate)
        {
            removed = _slotAttachments.Remove(attachment);
        }

        if (attachment.IsAttached)
        {
            UnmountAttachment(attachment, destroyNative: true);
        }

        if (removed && !attachment.IsRawAttachment)
        {
            SyncSlotAttachments(attachment.Slot, forceRelayout: true);
        }
    }

    private void UnregisterLeagueMode(ManagedLeagueModeRegistration registration)
    {
        lock (_gate)
        {
            _leagueModes.Remove(registration);
        }
    }

    private static List<GamePanel> EnumerateIndividualPanels(GamePanel mask)
    {
        ReadOnlySpan<GamePanel> all =
        [
            GamePanel.Hud,
            GamePanel.Inventory,
            GamePanel.Stash,
            GamePanel.MapStash,
            GamePanel.BlightStash,
            GamePanel.PassiveTree,
            GamePanel.Vendor,
            GamePanel.AtlasPassiveTree,
            GamePanel.NpcDialogue,
            GamePanel.PlayerTrade,
        ];

        List<GamePanel> list = [];
        foreach (GamePanel panel in all)
        {
            if ((mask & panel) == panel)
            {
                list.Add(panel);
            }
        }

        return list;
    }

    private static string SanitizeControlId(string raw)
    {
        Span<char> buffer = stackalloc char[Math.Min(raw.Length, 48)];
        int len = 0;
        foreach (char c in raw)
        {
            if (len >= buffer.Length)
            {
                break;
            }

            buffer[len++] = char.IsLetterOrDigit(c) || c == '_' ? c : '_';
        }

        return len > 0 ? new string(buffer[..len]) : "item";
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _leagueFillSub.Dispose();

        ManagedSlotAttachment[] attachments;
        ManagedLeagueModeRegistration[] modes;
        lock (_gate)
        {
            attachments = [.. _slotAttachments];
            _slotAttachments.Clear();
            modes = [.. _leagueModes];
            _leagueModes.Clear();
        }

        foreach (ManagedSlotAttachment a in attachments)
        {
            if (a.IsAttached)
            {
                UnmountAttachment(a, destroyNative: true);
            }

            a.GeneratedOverlay?.Dispose();
        }

        foreach (ManagedLeagueModeRegistration m in modes)
        {
            m.ClearLiveBindings();
        }
    }
}
