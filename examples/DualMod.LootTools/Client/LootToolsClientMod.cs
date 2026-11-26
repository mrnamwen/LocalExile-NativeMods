using System.Globalization;
using System.Text.Json;
using DualMod.LootTools.Shared;
using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Engine;

namespace DualMod.LootTools.Client;

/// <summary>
/// Client half of <c>DualMod.LootTools</c> targeting NativeMods SDK <c>0.4.0</c>.
/// Implements filter-aware Loot Magnet (<c>Alt+M</c>), AoE Pickup (<c>Alt+G</c>),
/// Ground Loot Merger (<c>Alt+R</c> + optional auto-merge toggle), quick Item Filter reload (<c>Alt+F</c>),
/// and configurable Camera Zoom Override (<see cref="ICameraService.SetZoomOverride"/>) with automatic restore on unload.
/// </summary>
public sealed class LootToolsClientMod : IClientMod
{
    private IClientContext? _context;
    private IUiWindow? _panelWindow;
    private bool _unloading;

    private int _magnetRadiusCells = LootToolsProtocol.DefaultMagnetRadiusCells;
    private int _pickupRadiusCells = LootToolsProtocol.DefaultPickupRadiusCells;
    private int _mergeRadiusCells = LootToolsProtocol.DefaultMergeRadiusCells;
    private bool _autoMergeEnabled;
    private float _autoMergeIntervalSeconds = LootToolsProtocol.DefaultAutoMergeIntervalSeconds;
    private bool _scopeMergeToFilter;
    private bool _zoomOverrideEnabled;
    private float _zoomOverrideValue = LootToolsProtocol.DefaultZoomOverride;

    private float _autoMergeElapsed;
    private float _panelRefreshElapsed;
    private string _activeAreaId = string.Empty;
    private string _activeFilterName = "None";
    private int _lastVisibleItemCount;
    private int _areaResetCount;
    private int _filterReloadNotificationCount;
    private LootToolsToastNotification? _lastToast;

    public IUiWindow? PanelWindow => _panelWindow;
    public int MagnetRadiusCells => _magnetRadiusCells;
    public int PickupRadiusCells => _pickupRadiusCells;
    public int MergeRadiusCells => _mergeRadiusCells;
    public bool AutoMergeEnabled => _autoMergeEnabled;
    public bool ScopeMergeToFilter => _scopeMergeToFilter;
    public bool ZoomOverrideEnabled => _zoomOverrideEnabled;
    public float ZoomOverrideValue => _zoomOverrideValue;
    public string ActiveAreaId => _activeAreaId;
    public string ActiveFilterName => _activeFilterName;
    public int LastVisibleItemCount => _lastVisibleItemCount;
    public int AreaResetCount => _areaResetCount;
    public int FilterReloadNotificationCount => _filterReloadNotificationCount;
    public LootToolsToastNotification? LastToast => _lastToast;

    public void Load(IClientContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _unloading = false;
        _autoMergeElapsed = 0f;
        _panelRefreshElapsed = 0f;
        IGameServices game = context.Game;

        LoadInitialSettings(context.ModDirectory);
        EnsureVirtualUiFallback(context, game);

        // 1. Register compact HUD toolbar controls in conflict-free ScreenSlot.HudTopRight.
        context.TrackDisposable(game.Screens.RegisterSlotButton(
            ScreenSlot.HudTopRight,
            LootToolsProtocol.SlotMagnetId,
            "Magnet",
            TriggerLootMagnet,
            new SlotAttachmentOptions(Order: 10, Width: 76f, Height: 26f, Tooltip: "Pull filter-visible ground items nearby (Alt+M).")));

        context.TrackDisposable(game.Screens.RegisterSlotButton(
            ScreenSlot.HudTopRight,
            LootToolsProtocol.SlotAoePickupId,
            "Pickup",
            TriggerAoePickup,
            new SlotAttachmentOptions(Order: 20, Width: 74f, Height: 26f, Tooltip: "Pick up filter-visible ground items in range (Alt+G).")));

        context.TrackDisposable(game.Screens.RegisterSlotButton(
            ScreenSlot.HudTopRight,
            LootToolsProtocol.SlotMergeId,
            "Merge",
            TriggerLootMerge,
            new SlotAttachmentOptions(Order: 30, Width: 70f, Height: 26f, Tooltip: "Merge nearby partial ground stacks (Alt+R).")));

        context.TrackDisposable(game.Screens.RegisterSlotButton(
            ScreenSlot.HudTopRight,
            LootToolsProtocol.SlotFilterReloadId,
            "Filter",
            TriggerFilterReload,
            new SlotAttachmentOptions(Order: 40, Width: 68f, Height: 26f, Tooltip: "Reload active local .filter file (Alt+F).")));

        context.TrackDisposable(game.Screens.RegisterSlotButton(
            ScreenSlot.HudTopRight,
            LootToolsProtocol.SlotPanelId,
            "Loot Tools",
            TogglePanel,
            new SlotAttachmentOptions(Order: 50, Width: 92f, Height: 26f, Tooltip: "Open or close the Loot Tools settings & status panel (Alt+L).")));

        // 2. Register centralized keybinds respecting !IsTextInputFocused.
        context.TrackDisposable(game.Input.Keybinds.Register(
            LootToolsProtocol.MagnetKeybindId,
            "Loot Magnet (Filter-Aware)",
            KeyChord.FromKey('M', KeyModifiers.Alt),
            OnMagnetKeybind,
            allowWhenTextInputFocused: false,
            suppressGameInput: true));

        context.TrackDisposable(game.Input.Keybinds.Register(
            LootToolsProtocol.AoePickupKeybindId,
            "AoE Pickup (Filter-Aware)",
            KeyChord.FromKey('G', KeyModifiers.Alt),
            OnAoePickupKeybind,
            allowWhenTextInputFocused: false,
            suppressGameInput: true));

        context.TrackDisposable(game.Input.Keybinds.Register(
            LootToolsProtocol.LootMergeKeybindId,
            "Merge Ground Stacks",
            KeyChord.FromKey('R', KeyModifiers.Alt),
            OnMergeKeybind,
            allowWhenTextInputFocused: false,
            suppressGameInput: true));

        context.TrackDisposable(game.Input.Keybinds.Register(
            LootToolsProtocol.FilterReloadKeybindId,
            "Reload Item Filter",
            KeyChord.FromKey('F', KeyModifiers.Alt),
            OnFilterReloadKeybind,
            allowWhenTextInputFocused: false,
            suppressGameInput: true));

        context.TrackDisposable(game.Input.Keybinds.Register(
            LootToolsProtocol.TogglePanelKeybindId,
            "Toggle Loot Tools Panel",
            KeyChord.FromKey('L', KeyModifiers.Alt),
            OnTogglePanelKeybind,
            allowWhenTextInputFocused: false,
            suppressGameInput: true));

        // 3. Subscribe to Phase 2 ItemFilter, Lifecycle, Frame, and server bridge notifications.
        game.ItemFilter.FilterReloaded += OnItemFilterReloaded;
        game.Lifecycle.AreaEntered += OnAreaEntered;
        game.Lifecycle.AreaLeft += OnAreaLeft;
        game.Lifecycle.Disconnected += OnDisconnected;
        game.Lifecycle.Frame += OnFrame;

        context.TrackDisposable(context.Net.On<LootToolsToastNotification>(
            notification => DispatchOnGameThread(() => HandleServerToast(notification))));

        // Seed initial state from live Phase 2 services.
        RefreshFilterAndAreaSnapshot(game);
        if (_zoomOverrideEnabled)
        {
            game.Camera.SetZoomOverride(_zoomOverrideValue);
        }

        context.Logger.Info("DualMod.LootTools client initialized (SDK 0.4.0).");
    }

    public void Unload()
    {
        _unloading = true;
        if (_context is { } ctx)
        {
            ctx.Game.ItemFilter.FilterReloaded -= OnItemFilterReloaded;
            ctx.Game.Lifecycle.AreaEntered -= OnAreaEntered;
            ctx.Game.Lifecycle.AreaLeft -= OnAreaLeft;
            ctx.Game.Lifecycle.Disconnected -= OnDisconnected;
            ctx.Game.Lifecycle.Frame -= OnFrame;

            // Always restore stock camera zoom cleanly on unload.
            try
            {
                ctx.Game.Camera.SetZoomOverride(null);
            }
            catch
            {
                // Ignore bridge teardown errors during shutdown.
            }
        }

        ClosePanel();
        _autoMergeElapsed = 0f;
        _panelRefreshElapsed = 0f;
        _context = null;
    }

    public void TogglePanel()
    {
        if (_unloading || _context is null)
        {
            return;
        }

        DispatchOnGameThread(() =>
        {
            if (_unloading)
            {
                return;
            }

            if (_panelWindow is { IsOpen: true })
            {
                ClosePanel();
            }
            else
            {
                OpenPanel();
            }
        });
    }

    public void OpenPanel()
    {
        if (_unloading || _context is not { } ctx)
        {
            return;
        }

        DispatchOnGameThread(() =>
        {
            if (_unloading || _context is null)
            {
                return;
            }

            if (_panelWindow is { IsOpen: true })
            {
                _panelWindow.Visible = true;
                SyncPanelControlsFromState();
                return;
            }

            IUiWindow window = ctx.Game.Ui.Load(LootToolsProtocol.PanelUiPath, LootToolsProtocol.PanelWindowId);
            _panelWindow = window;
            window.Closed += () =>
            {
                if (ReferenceEquals(_panelWindow, window))
                {
                    _panelWindow = null;
                }
            };

            if (window.Find<IButton>("close_button") is { } closeBtn)
            {
                closeBtn.Clicked += ClosePanel;
            }

            if (window.Find<IButton>("close_footer_button") is { } closeFooterBtn)
            {
                closeFooterBtn.Clicked += ClosePanel;
            }

            if (window.Find<IButton>("reload_filter_button") is { } reloadBtn)
            {
                reloadBtn.Clicked += TriggerFilterReload;
            }

            if (window.Find<IButton>("magnet_now_button") is { } magnetBtn)
            {
                magnetBtn.Clicked += TriggerLootMagnet;
            }

            if (window.Find<IButton>("pickup_now_button") is { } pickupBtn)
            {
                pickupBtn.Clicked += TriggerAoePickup;
            }

            if (window.Find<IButton>("merge_now_button") is { } mergeBtn)
            {
                mergeBtn.Clicked += TriggerLootMerge;
            }

            if (window.Find<IButton>("apply_settings_button") is { } applyBtn)
            {
                applyBtn.Clicked += ApplySettingsFromPanel;
            }

            if (window.Find<ICheckBox>("auto_merge_checkbox") is { } autoMergeBox)
            {
                autoMergeBox.CheckedChanged += OnAutoMergeCheckBoxChanged;
            }

            if (window.Find<ICheckBox>("scope_merge_filter_checkbox") is { } scopeFilterBox)
            {
                scopeFilterBox.CheckedChanged += isChecked => _scopeMergeToFilter = isChecked;
            }

            if (window.Find<ICheckBox>("zoom_override_checkbox") is { } zoomBox)
            {
                zoomBox.CheckedChanged += OnZoomOverrideCheckBoxChanged;
            }

            if (window.Find<ITextBox>("zoom_value_textbox") is { } zoomText)
            {
                zoomText.Submitted += _ => ApplySettingsFromPanel();
            }

            SyncPanelControlsFromState();
        });
    }

    public void ClosePanel()
    {
        IUiWindow? window = _panelWindow;
        _panelWindow = null;
        if (window is { IsOpen: true })
        {
            window.Close();
        }
    }

    public void TriggerLootMagnet()
    {
        if (_unloading || _context is not { } ctx)
        {
            return;
        }

        DispatchOnGameThread(() =>
        {
            if (_unloading || _context is null)
            {
                return;
            }

            uint[] entityIds = QueryFilterVisibleGroundItemIds(ctx.Game, _magnetRadiusCells);
            _lastVisibleItemCount = entityIds.Length;
            UpdateVisibleCountLabel();

            if (entityIds.Length == 0)
            {
                ctx.Game.Toasts.Show("Loot Magnet", "No filter-visible ground items in range.", ToastSeverity.Info);
                SetPanelStatus("No filter-visible ground items within magnet radius.");
                return;
            }

            ctx.Net.Send(new LootMagnetRequest(entityIds, _magnetRadiusCells));
        });
    }

    public void TriggerAoePickup()
    {
        if (_unloading || _context is not { } ctx)
        {
            return;
        }

        DispatchOnGameThread(() =>
        {
            if (_unloading || _context is null)
            {
                return;
            }

            uint[] entityIds = QueryFilterVisibleGroundItemIds(ctx.Game, _pickupRadiusCells);
            _lastVisibleItemCount = entityIds.Length;
            UpdateVisibleCountLabel();

            if (entityIds.Length == 0)
            {
                ctx.Game.Toasts.Show("AoE Pickup", "No filter-visible ground items in pickup range.", ToastSeverity.Info);
                SetPanelStatus("No filter-visible ground items within pickup radius.");
                return;
            }

            ctx.Net.Send(new AoePickupRequest(entityIds, _pickupRadiusCells));
        });
    }

    public void TriggerLootMerge() => TriggerLootMergeInternal(isAutoMerge: false);

    public void TriggerFilterReload()
    {
        if (_unloading || _context is not { } ctx)
        {
            return;
        }

        DispatchOnGameThread(() =>
        {
            if (_unloading || _context is null)
            {
                return;
            }

            bool dispatched = ctx.Game.ItemFilter.Reload();
            if (!dispatched)
            {
                ctx.Game.Toasts.Show("Item Filter", "Could not dispatch item filter reload.", ToastSeverity.Warning);
                SetPanelStatus("Item filter reload unavailable.");
            }
            else
            {
                SetPanelStatus("Reloading active item filter...");
            }
        });
    }

    public void SetZoomOverride(bool enabled, float? zoomValue = null)
    {
        if (zoomValue is float z && !float.IsNaN(z) && !float.IsInfinity(z))
        {
            _zoomOverrideValue = Math.Clamp(z, LootToolsProtocol.MinZoomOverride, LootToolsProtocol.MaxZoomOverride);
        }

        _zoomOverrideEnabled = enabled;
        if (_unloading || _context is not { } ctx)
        {
            return;
        }

        DispatchOnGameThread(() =>
        {
            if (_unloading || _context is null)
            {
                return;
            }

            ctx.Game.Camera.SetZoomOverride(_zoomOverrideEnabled ? _zoomOverrideValue : null);
            string msg = _zoomOverrideEnabled
                ? $"Camera zoom override set to {_zoomOverrideValue.ToString("0.00", CultureInfo.InvariantCulture)}x."
                : "Camera zoom restored to stock.";
            SetPanelStatus(msg);
            ctx.Game.Toasts.Show("Camera Zoom", msg, ToastSeverity.Info);
        });
    }

    public void SetAutoMerge(bool enabled)
    {
        _autoMergeEnabled = enabled;
        _autoMergeElapsed = 0f;
        if (_unloading || _context is not { } ctx)
        {
            return;
        }

        DispatchOnGameThread(() =>
        {
            if (_unloading || _context is null)
            {
                return;
            }

            ctx.Net.Send(new LootToolsSetAutoMergeRequest(_autoMergeEnabled, _mergeRadiusCells));
            SetPanelStatus(_autoMergeEnabled ? "Auto-merge enabled." : "Auto-merge disabled.");
        });
    }

    private void OnMagnetKeybind()
    {
        if (_unloading || _context is not { } ctx || ctx.Game.Input.IsTextInputFocused)
        {
            return;
        }

        TriggerLootMagnet();
    }

    private void OnAoePickupKeybind()
    {
        if (_unloading || _context is not { } ctx || ctx.Game.Input.IsTextInputFocused)
        {
            return;
        }

        TriggerAoePickup();
    }

    private void OnMergeKeybind()
    {
        if (_unloading || _context is not { } ctx || ctx.Game.Input.IsTextInputFocused)
        {
            return;
        }

        TriggerLootMerge();
    }

    private void OnFilterReloadKeybind()
    {
        if (_unloading || _context is not { } ctx || ctx.Game.Input.IsTextInputFocused)
        {
            return;
        }

        TriggerFilterReload();
    }

    private void OnTogglePanelKeybind()
    {
        if (_unloading || _context is not { } ctx || ctx.Game.Input.IsTextInputFocused)
        {
            return;
        }

        TogglePanel();
    }

    private void TriggerLootMergeInternal(bool isAutoMerge)
    {
        if (_unloading || _context is not { } ctx)
        {
            return;
        }

        DispatchOnGameThread(() =>
        {
            if (_unloading || _context is null)
            {
                return;
            }

            uint[]? scopedIds = null;
            if (!isAutoMerge && _scopeMergeToFilter)
            {
                scopedIds = QueryFilterVisibleGroundItemIds(ctx.Game, _mergeRadiusCells);
                if (scopedIds.Length < 2)
                {
                    ctx.Game.Toasts.Show("Loot Merger", "Need at least 2 filter-visible ground items in range to merge.", ToastSeverity.Info);
                    return;
                }
            }

            ctx.Net.Send(new LootMergeRequest(scopedIds, _mergeRadiusCells, IsAutoMerge: isAutoMerge));
        });
    }

    private void ApplySettingsFromPanel()
    {
        if (_unloading || _context is not { } ctx || _panelWindow is not { IsOpen: true } window)
        {
            return;
        }

        _magnetRadiusCells = ParseClampedInt(window.Find<ITextBox>("magnet_radius_textbox")?.Text, _magnetRadiusCells, LootToolsProtocol.MinRadiusCells, LootToolsProtocol.MaxRadiusCells);
        _pickupRadiusCells = ParseClampedInt(window.Find<ITextBox>("pickup_radius_textbox")?.Text, _pickupRadiusCells, LootToolsProtocol.MinRadiusCells, LootToolsProtocol.MaxRadiusCells);
        _mergeRadiusCells = ParseClampedInt(window.Find<ITextBox>("merge_radius_textbox")?.Text, _mergeRadiusCells, LootToolsProtocol.MinRadiusCells, LootToolsProtocol.MaxRadiusCells);
        _autoMergeEnabled = window.Find<ICheckBox>("auto_merge_checkbox")?.IsChecked ?? _autoMergeEnabled;
        _scopeMergeToFilter = window.Find<ICheckBox>("scope_merge_filter_checkbox")?.IsChecked ?? _scopeMergeToFilter;
        _zoomOverrideEnabled = window.Find<ICheckBox>("zoom_override_checkbox")?.IsChecked ?? _zoomOverrideEnabled;
        _zoomOverrideValue = ParseClampedFloat(window.Find<ITextBox>("zoom_value_textbox")?.Text, _zoomOverrideValue, LootToolsProtocol.MinZoomOverride, LootToolsProtocol.MaxZoomOverride);

        ctx.Game.Camera.SetZoomOverride(_zoomOverrideEnabled ? _zoomOverrideValue : null);
        ctx.Net.Send(new LootToolsSetAutoMergeRequest(_autoMergeEnabled, _mergeRadiusCells));

        SyncPanelControlsFromState();
        string summary = $"Applied: Magnet {_magnetRadiusCells}, Pickup {_pickupRadiusCells}, Merge {_mergeRadiusCells}"
            + (_zoomOverrideEnabled ? $", Zoom {_zoomOverrideValue.ToString("0.00", CultureInfo.InvariantCulture)}x" : ", Zoom stock")
            + ".";
        SetPanelStatus(summary);
        ctx.Game.Toasts.Show("Loot Tools", summary, ToastSeverity.Success);
    }

    private void OnAutoMergeCheckBoxChanged(bool isChecked)
    {
        if (_unloading || _context is not { } ctx)
        {
            return;
        }

        _autoMergeEnabled = isChecked;
        _autoMergeElapsed = 0f;
        ctx.Net.Send(new LootToolsSetAutoMergeRequest(_autoMergeEnabled, _mergeRadiusCells));
        SetPanelStatus(_autoMergeEnabled ? "Auto-merge enabled." : "Auto-merge disabled.");
    }

    private void OnZoomOverrideCheckBoxChanged(bool isChecked)
    {
        if (_unloading || _context is not { } ctx)
        {
            return;
        }

        if (_panelWindow is { IsOpen: true } window)
        {
            _zoomOverrideValue = ParseClampedFloat(
                window.Find<ITextBox>("zoom_value_textbox")?.Text,
                _zoomOverrideValue,
                LootToolsProtocol.MinZoomOverride,
                LootToolsProtocol.MaxZoomOverride);
        }

        _zoomOverrideEnabled = isChecked;
        ctx.Game.Camera.SetZoomOverride(_zoomOverrideEnabled ? _zoomOverrideValue : null);
        SetPanelStatus(_zoomOverrideEnabled
            ? $"Camera zoom override active ({_zoomOverrideValue.ToString("0.00", CultureInfo.InvariantCulture)}x)."
            : "Camera zoom restored to stock.");
    }

    private void OnItemFilterReloaded(ItemFilterReloadedEventArgs args)
    {
        DispatchOnGameThread(() =>
        {
            if (_unloading || _context is not { } ctx)
            {
                return;
            }

            _filterReloadNotificationCount++;
            _activeFilterName = FormatFilterName(args.State);
            _lastVisibleItemCount = ctx.Game.World.GetWorldItems(filterVisibleOnly: true, maxDistance: _magnetRadiusCells * LootToolsProtocol.WorldUnitsPerGridCell).Count;

            SyncPanelStatusLabels();
            string message = string.IsNullOrWhiteSpace(args.StatusMessage)
                ? (args.Succeeded ? $"Loaded filter '{_activeFilterName}'." : "Item filter reload failed.")
                : args.StatusMessage;
            SetPanelStatus(message);
            ctx.Game.Toasts.Show(
                "Item Filter",
                message,
                args.Succeeded ? ToastSeverity.Success : ToastSeverity.Error);
        });
    }

    private void OnAreaEntered(AreaEventArgs args)
    {
        DispatchOnGameThread(() =>
        {
            if (_unloading || _context is not { } ctx)
            {
                return;
            }

            _areaResetCount++;
            _autoMergeElapsed = 0f;
            _panelRefreshElapsed = 0f;
            _activeAreaId = args.Area?.AreaName is { Length: > 0 } name ? $"{name} ({args.AreaId})" : args.AreaId;
            RefreshFilterAndAreaSnapshot(ctx.Game);

            if (_zoomOverrideEnabled)
            {
                ctx.Game.Camera.SetZoomOverride(_zoomOverrideValue);
            }

            SyncPanelStatusLabels();
            SetPanelStatus($"Entered {_activeAreaId}. Ready.");
        });
    }

    private void OnAreaLeft(string areaId)
    {
        _ = areaId;
        DispatchOnGameThread(() =>
        {
            if (_unloading)
            {
                return;
            }

            _areaResetCount++;
            _autoMergeElapsed = 0f;
            _panelRefreshElapsed = 0f;
            _activeAreaId = string.Empty;
            _lastVisibleItemCount = 0;
            SyncPanelStatusLabels();
            SetPanelStatus("Left area. Cooldowns reset.");
        });
    }

    private void OnDisconnected(string? _)
    {
        DispatchOnGameThread(() =>
        {
            if (_unloading)
            {
                return;
            }

            _autoMergeElapsed = 0f;
            _panelRefreshElapsed = 0f;
            _activeAreaId = string.Empty;
            _lastVisibleItemCount = 0;
            ClosePanel();
        });
    }

    private void OnFrame(float deltaSeconds)
    {
        if (_unloading || _context is not { } ctx || deltaSeconds <= 0f)
        {
            return;
        }

        if (_panelWindow is { IsOpen: true })
        {
            _panelRefreshElapsed += deltaSeconds;
            if (_panelRefreshElapsed >= 0.25f)
            {
                _panelRefreshElapsed = 0f;
                RefreshFilterAndAreaSnapshot(ctx.Game);
                SyncPanelStatusLabels();
            }
        }

        if (_autoMergeEnabled)
        {
            AreaInfo? area = ctx.Game.Lifecycle.CurrentArea;
            if (area is { IsTown: true } or { IsHideout: true } or { IsGuildHideout: true })
            {
                return;
            }

            _autoMergeElapsed += deltaSeconds;
            if (_autoMergeElapsed >= _autoMergeIntervalSeconds)
            {
                _autoMergeElapsed = 0f;
                float worldRadius = _mergeRadiusCells * LootToolsProtocol.WorldUnitsPerGridCell;
                IReadOnlyList<WorldEntitySnapshot> nearbyItems = ctx.Game.World.GetWorldItems(filterVisibleOnly: false, maxDistance: worldRadius);
                if (nearbyItems.Count >= 2)
                {
                    TriggerLootMergeInternal(isAutoMerge: true);
                }
            }
        }
    }

    private void HandleServerToast(LootToolsToastNotification notification)
    {
        if (_unloading || _context is not { } ctx)
        {
            return;
        }

        _lastToast = notification;
        ToastSeverity severity = ParseSeverity(notification.Severity);
        ctx.Game.Toasts.Show(notification.Title, notification.Message, severity);
        SetPanelStatus(notification.Message);
    }

    private static uint[] QueryFilterVisibleGroundItemIds(IGameServices game, int radiusCells)
    {
        int clampedRadius = Math.Clamp(radiusCells, LootToolsProtocol.MinRadiusCells, LootToolsProtocol.MaxRadiusCells);
        float maxWorldDistance = clampedRadius * LootToolsProtocol.WorldUnitsPerGridCell;

        // Backed by game.ItemFilter: NEVER invents items, ONLY collects server WorldItem IDs passing local .filter.
        IReadOnlyList<WorldEntitySnapshot> visible = game.World.GetWorldItems(
            filterVisibleOnly: true,
            maxDistance: maxWorldDistance);

        if (visible.Count == 0)
        {
            return [];
        }

        WorldEntitySnapshot? localPlayer = game.World.GetLocalPlayer();
        IEnumerable<WorldEntitySnapshot> candidates = visible
            .Where(e => e.IsWorldItem && !e.IsClientOnly && e.IsFilterVisible);

        candidates = localPlayer is not null
            ? candidates.OrderBy(e => e.Position.Distance2DTo(localPlayer.Position)).ThenBy(e => e.EntityId)
            : candidates.OrderBy(e => e.EntityId);

        return [.. candidates
            .Select(e => e.EntityId)
            .Distinct()
            .Take(LootToolsProtocol.MaxBatchEntityCount)];
    }

    private void RefreshFilterAndAreaSnapshot(IGameServices game)
    {
        ItemFilterState filterState = game.ItemFilter.GetState();
        _activeFilterName = FormatFilterName(filterState);

        AreaInfo? area = game.Lifecycle.CurrentArea;
        if (area is not null)
        {
            _activeAreaId = string.IsNullOrWhiteSpace(area.AreaName) ? area.AreaId : $"{area.AreaName} ({area.AreaId})";
        }
        else if (!string.IsNullOrWhiteSpace(game.Lifecycle.CurrentAreaId))
        {
            _activeAreaId = game.Lifecycle.CurrentAreaId!;
        }

        float maxWorldDist = _magnetRadiusCells * LootToolsProtocol.WorldUnitsPerGridCell;
        _lastVisibleItemCount = game.World.GetWorldItems(filterVisibleOnly: true, maxDistance: maxWorldDist).Count;
    }

    private void SyncPanelControlsFromState()
    {
        if (_panelWindow is not { IsOpen: true } window)
        {
            return;
        }

        if (_context is { } ctx)
        {
            RefreshFilterAndAreaSnapshot(ctx.Game);
        }

        if (window.Find<ITextBox>("magnet_radius_textbox") is { } magnetBox)
        {
            magnetBox.Text = _magnetRadiusCells.ToString(CultureInfo.InvariantCulture);
        }

        if (window.Find<ITextBox>("pickup_radius_textbox") is { } pickupBox)
        {
            pickupBox.Text = _pickupRadiusCells.ToString(CultureInfo.InvariantCulture);
        }

        if (window.Find<ITextBox>("merge_radius_textbox") is { } mergeBox)
        {
            mergeBox.Text = _mergeRadiusCells.ToString(CultureInfo.InvariantCulture);
        }

        if (window.Find<ICheckBox>("auto_merge_checkbox") is { } autoMergeBox)
        {
            autoMergeBox.IsChecked = _autoMergeEnabled;
        }

        if (window.Find<ICheckBox>("scope_merge_filter_checkbox") is { } scopeBox)
        {
            scopeBox.IsChecked = _scopeMergeToFilter;
        }

        if (window.Find<ICheckBox>("zoom_override_checkbox") is { } zoomBox)
        {
            zoomBox.IsChecked = _zoomOverrideEnabled;
        }

        if (window.Find<ITextBox>("zoom_value_textbox") is { } zoomText)
        {
            zoomText.Text = _zoomOverrideValue.ToString("0.00", CultureInfo.InvariantCulture);
        }

        SyncPanelStatusLabels();
    }

    private void SyncPanelStatusLabels()
    {
        if (_panelWindow is not { IsOpen: true } window)
        {
            return;
        }

        if (window.Find<ILabel>("filter_name_label") is { } filterLabel)
        {
            filterLabel.Text = $"Filter: {_activeFilterName}";
        }

        UpdateVisibleCountLabel();

        if (window.Find<ILabel>("area_status_label") is { } areaLabel)
        {
            areaLabel.Text = string.IsNullOrWhiteSpace(_activeAreaId) ? "Area: None" : $"Area: {_activeAreaId}";
        }
    }

    private void UpdateVisibleCountLabel()
    {
        if (_panelWindow is { IsOpen: true } window && window.Find<ILabel>("visible_items_label") is { } label)
        {
            label.Text = $"Visible Ground Items: {_lastVisibleItemCount}";
        }
    }

    private void SetPanelStatus(string message)
    {
        if (_panelWindow is { IsOpen: true } window && window.Find<ILabel>("status_label") is { } label)
        {
            label.Text = message;
        }
    }

    private void DispatchOnGameThread(Action action)
    {
        if (_unloading || _context is not { } ctx)
        {
            return;
        }

        if (ctx.Scheduler.IsOnGameThread)
        {
            action();
        }
        else
        {
            ctx.Scheduler.RunOnGameThread(() =>
            {
                if (!_unloading)
                {
                    action();
                }
            });
        }
    }

    private void LoadInitialSettings(string modDirectory)
    {
        try
        {
            string settingsPath = Path.Combine(modDirectory, "settings.json");
            if (!File.Exists(settingsPath))
            {
                return;
            }

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
            JsonElement root = doc.RootElement;
            JsonElement section = root.TryGetProperty("settings", out JsonElement s) && s.ValueKind == JsonValueKind.Object ? s : root;

            if (TryReadSettingInt(section, "magnetRadius", out int mag))
            {
                _magnetRadiusCells = Math.Clamp(mag, LootToolsProtocol.MinRadiusCells, LootToolsProtocol.MaxRadiusCells);
            }

            if (TryReadSettingInt(section, "pickupRadius", out int pick))
            {
                _pickupRadiusCells = Math.Clamp(pick, LootToolsProtocol.MinRadiusCells, LootToolsProtocol.MaxRadiusCells);
            }

            if (TryReadSettingInt(section, "mergeRadius", out int merge))
            {
                _mergeRadiusCells = Math.Clamp(merge, LootToolsProtocol.MinRadiusCells, LootToolsProtocol.MaxRadiusCells);
            }

            if (TryReadSettingBool(section, "autoMergeEnabled", out bool autoMerge))
            {
                _autoMergeEnabled = autoMerge;
            }

            if (TryReadSettingFloat(section, "autoMergeIntervalSeconds", out float interval))
            {
                _autoMergeIntervalSeconds = Math.Clamp(interval, LootToolsProtocol.MinAutoMergeIntervalSeconds, LootToolsProtocol.MaxAutoMergeIntervalSeconds);
            }

            if (TryReadSettingBool(section, "scopeMergeToFilter", out bool scopeFilter))
            {
                _scopeMergeToFilter = scopeFilter;
            }

            if (TryReadSettingBool(section, "zoomOverrideEnabled", out bool zoomEnabled))
            {
                _zoomOverrideEnabled = zoomEnabled;
            }

            if (TryReadSettingFloat(section, "zoomOverrideValue", out float zoomVal))
            {
                _zoomOverrideValue = Math.Clamp(zoomVal, LootToolsProtocol.MinZoomOverride, LootToolsProtocol.MaxZoomOverride);
            }
        }
        catch
        {
            // Keep safe defaults if settings.json is absent or malformed.
        }
    }

    private static bool TryReadSettingInt(JsonElement section, string key, out int value)
    {
        value = 0;
        if (!TryGetSettingValueElement(section, key, out JsonElement elem))
        {
            return false;
        }

        if (elem.ValueKind == JsonValueKind.Number && elem.TryGetInt32(out value))
        {
            return true;
        }

        return false;
    }

    private static bool TryReadSettingFloat(JsonElement section, string key, out float value)
    {
        value = 0f;
        if (!TryGetSettingValueElement(section, key, out JsonElement elem))
        {
            return false;
        }

        if (elem.ValueKind == JsonValueKind.Number && elem.TryGetSingle(out value))
        {
            return true;
        }

        return false;
    }

    private static bool TryReadSettingBool(JsonElement section, string key, out bool value)
    {
        value = false;
        if (!TryGetSettingValueElement(section, key, out JsonElement elem))
        {
            return false;
        }

        if (elem.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = elem.GetBoolean();
            return true;
        }

        return false;
    }

    private static bool TryGetSettingValueElement(JsonElement section, string key, out JsonElement valueElem)
    {
        foreach (JsonProperty prop in section.EnumerateObject())
        {
            if (!string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (prop.Value.ValueKind == JsonValueKind.Object &&
                prop.Value.TryGetProperty("default", out JsonElement def))
            {
                valueElem = def;
                return true;
            }

            valueElem = prop.Value;
            return true;
        }

        valueElem = default;
        return false;
    }

    private static string FormatFilterName(ItemFilterState state)
    {
        if (!state.IsLoaded)
        {
            return "None";
        }

        if (!string.IsNullOrWhiteSpace(state.FilterName))
        {
            return state.FilterName;
        }

        if (!string.IsNullOrWhiteSpace(state.FilterPath))
        {
            return Path.GetFileName(state.FilterPath);
        }

        return "Default";
    }

    private static int ParseClampedInt(string? text, int fallback, int min, int max) =>
        !string.IsNullOrWhiteSpace(text) && int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? Math.Clamp(parsed, min, max)
            : fallback;

    private static float ParseClampedFloat(string? text, float fallback, float min, float max) =>
        !string.IsNullOrWhiteSpace(text) && float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) && float.IsFinite(parsed)
            ? Math.Clamp(parsed, min, max)
            : fallback;

    private static ToastSeverity ParseSeverity(string severity) => severity.ToUpperInvariant() switch
    {
        "SUCCESS" => ToastSeverity.Success,
        "WARNING" or "WARN" => ToastSeverity.Warning,
        "ERROR" => ToastSeverity.Error,
        _ => ToastSeverity.Info,
    };

    private static void EnsureVirtualUiFallback(IClientContext context, IGameServices game)
    {
        if (!game.Files.Exists(LootToolsProtocol.PanelUiPath))
        {
            context.TrackDisposable(game.Files.RegisterVirtualTextFile(
                LootToolsProtocol.PanelUiPath,
                FallbackPanelUi,
                priority: 50));
        }
    }

    private const string FallbackPanelUi = """
// Fallback Loot Tools UI
<DEFINITIONS>
begin Frame
	id = loottools_panel;
	size = 560, 540;
	begin Label
		id = filter_name_label;
		text = "Filter: None";
	end
	begin Label
		id = visible_items_label;
		text = "Visible Ground Items: 0";
	end
	begin Label
		id = area_status_label;
		text = "Area: None";
	end
	begin BasicButton
		id = reload_filter_button;
		text = "Reload Filter";
	end
	begin TextBox
		id = magnet_radius_textbox;
		text = "60";
	end
	begin TextBox
		id = pickup_radius_textbox;
		text = "50";
	end
	begin TextBox
		id = merge_radius_textbox;
		text = "60";
	end
	begin CheckBox
		id = auto_merge_checkbox;
		checked = false;
	end
	begin CheckBox
		id = scope_merge_filter_checkbox;
		checked = false;
	end
	begin CheckBox
		id = zoom_override_checkbox;
		checked = false;
	end
	begin TextBox
		id = zoom_value_textbox;
		text = "1.35";
	end
	begin BasicButton
		id = magnet_now_button;
		text = "Magnet (Alt+M)";
	end
	begin BasicButton
		id = pickup_now_button;
		text = "Pickup (Alt+G)";
	end
	begin BasicButton
		id = merge_now_button;
		text = "Merge (Alt+R)";
	end
	begin BasicButton
		id = apply_settings_button;
		text = "Apply Settings";
	end
	begin BasicButton
		id = close_footer_button;
		text = "Close";
	end
	begin BasicButton
		id = close_button;
		text = "X";
	end
	begin Label
		id = status_label;
		text = "Ready.";
	end
end
""";
}
