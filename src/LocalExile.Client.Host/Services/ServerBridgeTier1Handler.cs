using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Protocol;

namespace LocalExile.Client.Host.Services;

/// <summary>
/// Client-side dispatcher for Tier 1 <c>ServerBridge 0.3.0</c>/<c>0.4.0</c> commands arriving on <see cref="MessageCodec.SystemChannel"/>.
/// Wires server-only single-<c>.cs</c> mods directly to <see cref="ScreensService"/>, <see cref="InputService"/>,
/// <see cref="HoverService"/>, <see cref="ToastService"/>, <see cref="WorldService"/>, <see cref="StatsService"/>,
/// <see cref="GameLifecycle"/>, <see cref="ItemFilterService"/>, and <see cref="CameraService"/> on the game thread,
/// and tears down all server-owned client resources cleanly on session disconnect or host disposal.
/// </summary>
public sealed class ServerBridgeTier1Handler : IDisposable
{
    private const string ServerBridgeModOwner = "server-bridge";

    // Phase 1 (0.3.0)
    private const string MsgRegisterSlotButton = "Tier1RegisterSlotButton";
    private const string MsgRemoveSlotButton = "Tier1RemoveSlotButton";
    private const string MsgSlotButtonClick = "Tier1SlotButtonClick";
    private const string MsgShowToast = "Tier1ShowToast";
    private const string MsgRegisterHotkey = "Tier1RegisterHotkey";
    private const string MsgUnregisterHotkey = "Tier1UnregisterHotkey";
    private const string MsgHotkeyTriggered = "Tier1HotkeyTriggered";
    private const string MsgQueryScreenState = "Tier1QueryScreenState";
    private const string MsgScreenStateResponse = "Tier1ScreenStateResponse";
    private const string MsgSetScreenSubscription = "Tier1SetScreenSubscription";
    private const string MsgScreenStateChanged = "Tier1ScreenStateChanged";
    private const string MsgQueryHoverState = "Tier1QueryHoverState";
    private const string MsgHoverStateResponse = "Tier1HoverStateResponse";
    private const string MsgSetHoverSubscription = "Tier1SetHoverSubscription";
    private const string MsgHoverStateChanged = "Tier1HoverStateChanged";

    // Phase 2 (0.4.0)
    private const string MsgQueryWorldEntities = "Tier1QueryWorldEntities";
    private const string MsgWorldEntitiesResponse = "Tier1WorldEntitiesResponse";
    private const string MsgQueryLocalPlayer = "Tier1QueryLocalPlayer";
    private const string MsgLocalPlayerResponse = "Tier1LocalPlayerResponse";
    private const string MsgQueryEntityStats = "Tier1QueryEntityStats";
    private const string MsgEntityStatsResponse = "Tier1EntityStatsResponse";
    private const string MsgQueryStat = "Tier1QueryStat";
    private const string MsgStatResponse = "Tier1StatResponse";
    private const string MsgQueryAreaInfo = "Tier1QueryAreaInfo";
    private const string MsgAreaInfoResponse = "Tier1AreaInfoResponse";
    private const string MsgSetAreaSubscription = "Tier1SetAreaSubscription";
    private const string MsgAreaChanged = "Tier1AreaChanged";
    private const string MsgQueryItemFilterState = "Tier1QueryItemFilterState";
    private const string MsgItemFilterStateResponse = "Tier1ItemFilterStateResponse";
    private const string MsgReloadItemFilter = "Tier1ReloadItemFilter";
    private const string MsgReloadItemFilterResponse = "Tier1ReloadItemFilterResponse";
    private const string MsgEvaluateWorldItemFilter = "Tier1EvaluateWorldItemFilter";
    private const string MsgEvaluateWorldItemFilterResponse = "Tier1EvaluateWorldItemFilterResponse";
    private const string MsgSetFilterSubscription = "Tier1SetFilterSubscription";
    private const string MsgFilterReloaded = "Tier1FilterReloaded";
    private const string MsgQueryCameraState = "Tier1QueryCameraState";
    private const string MsgCameraStateResponse = "Tier1CameraStateResponse";
    private const string MsgSetCameraZoomOverride = "Tier1SetCameraZoomOverride";

    // Phase 4 + 5 (1.2.0)
    private const string MsgShowTooltip = "Tier1ShowTooltip";
    private const string MsgHideTooltip = "Tier1HideTooltip";
    private const string MsgRegisterItemTooltipRule = "Tier1RegisterItemTooltipRule";
    private const string MsgRemoveItemTooltipRule = "Tier1RemoveItemTooltipRule";
    private const string MsgRegisterStashAffinityRoute = "Tier1RegisterStashAffinityRoute";
    private const string MsgRemoveStashAffinityRoute = "Tier1RemoveStashAffinityRoute";
    private const string MsgSetPictureTexture = "Tier1SetPictureTexture";
    private const string MsgSetComboBoxSelected = "Tier1SetComboBoxSelected";
    private const string MsgSelectTab = "Tier1SelectTab";
    private const string MsgCreateItemGrid = "Tier1CreateItemGrid";
    private const string MsgSetGridItem = "Tier1SetGridItem";
    private const string MsgRemoveGridItem = "Tier1RemoveGridItem";
    private const string MsgClearGrid = "Tier1ClearGrid";
    private const string MsgSetGridClickSubscription = "Tier1SetGridClickSubscription";
    private const string MsgGridCellClick = "Tier1GridCellClick";
    private const string MsgPlayAudioEx = "Tier1PlayAudioEx";
    private const string MsgPlayAudioExResponse = "Tier1PlayAudioExResponse";
    private const string MsgStopAudio = "Tier1StopAudio";
    private const string MsgPlaySoundAtPosition = "Tier1PlaySoundAtPosition";
    private const string MsgSetAudioPosition = "Tier1SetAudioPosition";
    private const string MsgSpawnClientObject = "Tier1SpawnClientObject";
    private const string MsgSpawnClientObjectResponse = "Tier1SpawnClientObjectResponse";
    private const string MsgRemoveClientObject = "Tier1RemoveClientObject";
    private const string MsgSetExtendedU32Stacks = "Tier1SetExtendedU32Stacks";
    private const string MsgSetEntityStackOverride = "Tier1SetEntityStackOverride";
    private const string MsgSetCompactSockets = "Tier1SetCompactSockets";

    private sealed class GridKeyComparer : IEqualityComparer<(string WindowId, string GridPath)>
    {
        public static readonly GridKeyComparer Instance = new();

        public bool Equals((string WindowId, string GridPath) x, (string WindowId, string GridPath) y) =>
            string.Equals(x.WindowId, y.WindowId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.GridPath, y.GridPath, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string WindowId, string GridPath) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.WindowId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.GridPath));
    }

    private sealed class SlotButtonKeyComparer : IEqualityComparer<(ScreenSlot Slot, string Id)>
    {
        public static readonly SlotButtonKeyComparer Instance = new();

        public bool Equals((ScreenSlot Slot, string Id) x, (ScreenSlot Slot, string Id) y) =>
            x.Slot == y.Slot && string.Equals(x.Id, y.Id, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((ScreenSlot Slot, string Id) obj) =>
            HashCode.Combine((int)obj.Slot, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Id));
    }

    private readonly INetChannel _systemChannel;
    private readonly IModScheduler _scheduler;
    private readonly ScreensService _screens;
    private readonly InputService _input;
    private readonly HoverService _hover;
    private readonly ToastService _toasts;
    private readonly WorldService? _world;
    private readonly StatsService? _stats;
    private readonly GameLifecycle? _lifecycle;
    private readonly ItemFilterService? _itemFilter;
    private readonly CameraService? _camera;
    private readonly UiService? _ui;
    private readonly TooltipService? _tooltips;
    private readonly StashAffinityService? _stashAffinity;
    private readonly AudioService? _audio;
    private readonly StackExtensionService? _stackExtension;
    private readonly Action<string, string, string>? _log;
    private readonly Dictionary<(ScreenSlot Slot, string Id), ISlotAttachment> _slotButtons = new(SlotButtonKeyComparer.Instance);
    private readonly Dictionary<string, IDisposable> _hotkeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IDisposable> _tooltipRules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IDisposable> _affinityRoutes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string WindowId, string GridPath), IUiItemGrid> _ownedGrids = new(GridKeyComparer.Instance);
    private readonly Dictionary<(string WindowId, string GridPath), Action> _gridClickSubs = new(GridKeyComparer.Instance);
    private readonly HashSet<int> _ownedAudioChannels = [];
    private readonly HashSet<uint> _ownedWorldObjects = [];
    private readonly List<IDisposable> _channelSubs = [];
    private readonly Lock _gate = new();
    private int _screenSubRefCount;
    private int _hoverSubRefCount;
    private int _areaSubRefCount;
    private int _filterSubRefCount;
    private float? _activeCameraZoomOverride;
    private bool _disposed;

    /// <summary>Number of server-registered declarative item tooltip rules currently active.</summary>
    public int RegisteredTooltipRuleCount
    {
        get { lock (_gate) { return _tooltipRules.Count; } }
    }

    /// <summary>Number of server-registered Ctrl+Click stash affinity routes currently active.</summary>
    public int RegisteredAffinityRouteCount
    {
        get { lock (_gate) { return _affinityRoutes.Count; } }
    }

    /// <summary>Number of server-started audio channels currently tracked for disconnect cleanup.</summary>
    public int OwnedAudioChannelCount
    {
        get { lock (_gate) { return _ownedAudioChannels.Count; } }
    }

    /// <summary>Number of server-spawned client-only world objects currently tracked for disconnect cleanup.</summary>
    public int OwnedWorldObjectCount
    {
        get { lock (_gate) { return _ownedWorldObjects.Count; } }
    }

    /// <summary>Number of server-registered managed <see cref="ScreenSlot"/> toolbar buttons currently active.</summary>
    public int RegisteredSlotButtonCount
    {
        get { lock (_gate) { return _slotButtons.Count; } }
    }

    /// <summary>Number of server-registered centralized keybinds/hotkeys currently active.</summary>
    public int RegisteredHotkeyCount
    {
        get { lock (_gate) { return _hotkeys.Count; } }
    }

    /// <summary>Whether opt-in screen/panel/stash-tab change notifications are currently active.</summary>
    public bool IsScreenSubscriptionActive
    {
        get { lock (_gate) { return _screenSubRefCount > 0; } }
    }

    /// <summary>Whether opt-in hover/cursor change notifications are currently active.</summary>
    public bool IsHoverSubscriptionActive
    {
        get { lock (_gate) { return _hoverSubRefCount > 0; } }
    }

    /// <summary>Whether opt-in area enter/leave notifications are currently active.</summary>
    public bool IsAreaSubscriptionActive
    {
        get { lock (_gate) { return _areaSubRefCount > 0; } }
    }

    /// <summary>Whether opt-in item-filter reload notifications are currently active.</summary>
    public bool IsFilterSubscriptionActive
    {
        get { lock (_gate) { return _filterSubRefCount > 0; } }
    }

    /// <summary>Active server-owned camera zoom override, or <c>null</c> when stock zoom is active.</summary>
    public float? ActiveCameraZoomOverride
    {
        get { lock (_gate) { return _activeCameraZoomOverride; } }
    }

    /// <summary>Creates and wires a new <see cref="ServerBridgeTier1Handler"/> onto <paramref name="systemChannel"/> (0.3.0 Phase 1 subset).</summary>
    public ServerBridgeTier1Handler(
        INetChannel systemChannel,
        IModScheduler scheduler,
        ScreensService screens,
        InputService input,
        HoverService hover,
        ToastService toasts,
        Action<string, string, string>? log = null)
        : this(
            systemChannel,
            scheduler,
            screens,
            input,
            hover,
            toasts,
            world: null,
            stats: null,
            lifecycle: null,
            itemFilter: null,
            camera: null,
            log: log)
    {
    }

    /// <summary>Creates and wires a new <see cref="ServerBridgeTier1Handler"/> onto <paramref name="systemChannel"/> with full 0.4.0 Phase 2 services.</summary>
    public ServerBridgeTier1Handler(
        INetChannel systemChannel,
        IModScheduler scheduler,
        ScreensService screens,
        InputService input,
        HoverService hover,
        ToastService toasts,
        WorldService? world,
        StatsService? stats,
        GameLifecycle? lifecycle,
        ItemFilterService? itemFilter,
        CameraService? camera,
        Action<string, string, string>? log = null)
        : this(
            systemChannel,
            scheduler,
            screens,
            input,
            hover,
            toasts,
            world,
            stats,
            lifecycle,
            itemFilter,
            camera,
            ui: null,
            tooltips: null,
            stashAffinity: null,
            audio: null,
            stackExtension: null,
            log: log)
    {
    }

    /// <summary>Creates and wires a new <see cref="ServerBridgeTier1Handler"/> onto <paramref name="systemChannel"/> with full 1.2.0 Phase 1/2/4/5 services.</summary>
    public ServerBridgeTier1Handler(
        INetChannel systemChannel,
        IModScheduler scheduler,
        ScreensService screens,
        InputService input,
        HoverService hover,
        ToastService toasts,
        WorldService? world,
        StatsService? stats,
        GameLifecycle? lifecycle,
        ItemFilterService? itemFilter,
        CameraService? camera,
        UiService? ui,
        TooltipService? tooltips,
        StashAffinityService? stashAffinity,
        AudioService? audio,
        StackExtensionService? stackExtension,
        Action<string, string, string>? log = null)
    {
        _systemChannel = systemChannel ?? throw new ArgumentNullException(nameof(systemChannel));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _screens = screens ?? throw new ArgumentNullException(nameof(screens));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _hover = hover ?? throw new ArgumentNullException(nameof(hover));
        _toasts = toasts ?? throw new ArgumentNullException(nameof(toasts));
        _world = world;
        _stats = stats;
        _lifecycle = lifecycle;
        _itemFilter = itemFilter;
        _camera = camera;
        _ui = ui;
        _tooltips = tooltips;
        _stashAffinity = stashAffinity;
        _audio = audio;
        _stackExtension = stackExtension;
        _log = log;

        // Phase 1 (0.3.0)
        _channelSubs.Add(_systemChannel.On<RegisterSlotButtonDto>(HandleRegisterSlotButton, MsgRegisterSlotButton));
        _channelSubs.Add(_systemChannel.On<RemoveSlotButtonDto>(HandleRemoveSlotButton, MsgRemoveSlotButton));
        _channelSubs.Add(_systemChannel.On<ShowToastDto>(HandleShowToast, MsgShowToast));
        _channelSubs.Add(_systemChannel.On<RegisterHotkeyDto>(HandleRegisterHotkey, MsgRegisterHotkey));
        _channelSubs.Add(_systemChannel.On<UnregisterHotkeyDto>(HandleUnregisterHotkey, MsgUnregisterHotkey));
        _channelSubs.Add(_systemChannel.On<QueryScreenStateDto>(HandleQueryScreenState, MsgQueryScreenState));
        _channelSubs.Add(_systemChannel.On<SetSubscriptionDto>(HandleSetScreenSubscription, MsgSetScreenSubscription));
        _channelSubs.Add(_systemChannel.On<QueryHoverStateDto>(HandleQueryHoverState, MsgQueryHoverState));
        _channelSubs.Add(_systemChannel.On<SetSubscriptionDto>(HandleSetHoverSubscription, MsgSetHoverSubscription));

        // Phase 2 (0.4.0)
        _channelSubs.Add(_systemChannel.On<QueryWorldEntitiesDto>(HandleQueryWorldEntities, MsgQueryWorldEntities));
        _channelSubs.Add(_systemChannel.On<QueryLocalPlayerDto>(HandleQueryLocalPlayer, MsgQueryLocalPlayer));
        _channelSubs.Add(_systemChannel.On<QueryEntityStatsDto>(HandleQueryEntityStats, MsgQueryEntityStats));
        _channelSubs.Add(_systemChannel.On<QueryStatDto>(HandleQueryStat, MsgQueryStat));
        _channelSubs.Add(_systemChannel.On<QueryAreaInfoDto>(HandleQueryAreaInfo, MsgQueryAreaInfo));
        _channelSubs.Add(_systemChannel.On<SetSubscriptionDto>(HandleSetAreaSubscription, MsgSetAreaSubscription));
        _channelSubs.Add(_systemChannel.On<QueryItemFilterStateDto>(HandleQueryItemFilterState, MsgQueryItemFilterState));
        _channelSubs.Add(_systemChannel.On<ReloadItemFilterDto>(HandleReloadItemFilter, MsgReloadItemFilter));
        _channelSubs.Add(_systemChannel.On<EvaluateWorldItemFilterDto>(HandleEvaluateWorldItemFilter, MsgEvaluateWorldItemFilter));
        _channelSubs.Add(_systemChannel.On<SetSubscriptionDto>(HandleSetFilterSubscription, MsgSetFilterSubscription));
        _channelSubs.Add(_systemChannel.On<QueryCameraStateDto>(HandleQueryCameraState, MsgQueryCameraState));
        _channelSubs.Add(_systemChannel.On<SetCameraZoomOverrideDto>(HandleSetCameraZoomOverride, MsgSetCameraZoomOverride));

        // Phase 4 + 5 (1.2.0)
        _channelSubs.Add(_systemChannel.On<ShowTooltipDto>(HandleShowTooltip, MsgShowTooltip));
        _channelSubs.Add(_systemChannel.On<HideTooltipDto>(HandleHideTooltip, MsgHideTooltip));
        _channelSubs.Add(_systemChannel.On<ItemTooltipRuleDto>(HandleRegisterItemTooltipRule, MsgRegisterItemTooltipRule));
        _channelSubs.Add(_systemChannel.On<RemoveRuleDto>(HandleRemoveItemTooltipRule, MsgRemoveItemTooltipRule));
        _channelSubs.Add(_systemChannel.On<StashAffinityRouteRuleDto>(HandleRegisterStashAffinityRoute, MsgRegisterStashAffinityRoute));
        _channelSubs.Add(_systemChannel.On<RemoveRuleDto>(HandleRemoveStashAffinityRoute, MsgRemoveStashAffinityRoute));
        _channelSubs.Add(_systemChannel.On<SetPictureTextureDto>(HandleSetPictureTexture, MsgSetPictureTexture));
        _channelSubs.Add(_systemChannel.On<SetComboBoxSelectedDto>(HandleSetComboBoxSelected, MsgSetComboBoxSelected));
        _channelSubs.Add(_systemChannel.On<SelectTabDto>(HandleSelectTab, MsgSelectTab));
        _channelSubs.Add(_systemChannel.On<CreateItemGridDto>(HandleCreateItemGrid, MsgCreateItemGrid));
        _channelSubs.Add(_systemChannel.On<SetGridItemDto>(HandleSetGridItem, MsgSetGridItem));
        _channelSubs.Add(_systemChannel.On<RemoveGridItemDto>(HandleRemoveGridItem, MsgRemoveGridItem));
        _channelSubs.Add(_systemChannel.On<ClearGridDto>(HandleClearGrid, MsgClearGrid));
        _channelSubs.Add(_systemChannel.On<SetGridClickSubscriptionDto>(HandleSetGridClickSubscription, MsgSetGridClickSubscription));
        _channelSubs.Add(_systemChannel.On<PlayAudioExDto>(HandlePlayAudioEx, MsgPlayAudioEx));
        _channelSubs.Add(_systemChannel.On<StopAudioDto>(HandleStopAudio, MsgStopAudio));
        _channelSubs.Add(_systemChannel.On<PlaySoundAtPositionDto>(HandlePlaySoundAtPosition, MsgPlaySoundAtPosition));
        _channelSubs.Add(_systemChannel.On<SetAudioPositionDto>(HandleSetAudioPosition, MsgSetAudioPosition));
        _channelSubs.Add(_systemChannel.On<SpawnClientObjectDto>(HandleSpawnClientObject, MsgSpawnClientObject));
        _channelSubs.Add(_systemChannel.On<RemoveClientObjectDto>(HandleRemoveClientObject, MsgRemoveClientObject));
        _channelSubs.Add(_systemChannel.On<SetExtendedU32StacksDto>(HandleSetExtendedU32Stacks, MsgSetExtendedU32Stacks));
        _channelSubs.Add(_systemChannel.On<SetEntityStackOverrideDto>(HandleSetEntityStackOverride, MsgSetEntityStackOverride));
        _channelSubs.Add(_systemChannel.On<SetCompactSocketsDto>(HandleSetCompactSockets, MsgSetCompactSockets));

        if (_ui is not null)
        {
            _ui.WindowTreeInvalidated += HandleWindowTreeInvalidated;
        }
    }

    /// <summary>
    /// Cleans up all server-registered slot buttons, hotkeys, toasts, opt-in screen/hover/area/filter subscriptions,
    /// and server-owned camera zoom overrides when the client disconnects from an instance server.
    /// </summary>
    public void HandleDisconnected()
    {
        RunOnGameThreadGuarded(ClearSessionResourcesCore);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        bool alreadyDisposed;
        lock (_gate)
        {
            alreadyDisposed = _disposed;
            _disposed = true;
        }

        if (alreadyDisposed)
        {
            return;
        }

        if (_ui is not null)
        {
            _ui.WindowTreeInvalidated -= HandleWindowTreeInvalidated;
        }

        foreach (IDisposable sub in _channelSubs)
        {
            try
            {
                sub.Dispose();
            }
            catch
            {
                // Best-effort cleanup on shutdown.
            }
        }

        _channelSubs.Clear();
        ClearSessionResourcesCore();
    }

    // -------------------------------------------------------------------------
    // Phase 1 (0.3.0) Command & Query Handlers
    // -------------------------------------------------------------------------

    private void HandleRegisterSlotButton(RegisterSlotButtonDto cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.Id) || cmd.Slot == ScreenSlot.LeagueCardModes)
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            string trimmedId = cmd.Id.Trim();
            var key = (cmd.Slot, trimmedId);

            ISlotAttachment? existing;
            lock (_gate)
            {
                _slotButtons.Remove(key, out existing);
            }

            existing?.Dispose();

            var options = new SlotAttachmentOptions(
                Order: cmd.Order,
                Width: cmd.Width,
                Height: cmd.Height,
                Spacing: cmd.Spacing,
                Tooltip: cmd.Tooltip);

            try
            {
                ISlotAttachment attachment = _screens.RegisterSlotButton(
                    cmd.Slot,
                    trimmedId,
                    cmd.Text ?? string.Empty,
                    () =>
                    {
                        if (!IsDisposed())
                        {
                            _systemChannel.Send(new SlotButtonClickDto(cmd.Slot, trimmedId), MsgSlotButtonClick);
                        }
                    },
                    options);

                bool disposeImmediately = false;
                lock (_gate)
                {
                    if (_disposed)
                    {
                        disposeImmediately = true;
                    }
                    else
                    {
                        _slotButtons[key] = attachment;
                    }
                }

                if (disposeImmediately)
                {
                    attachment.Dispose();
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke("WARN", ServerBridgeModOwner, $"Failed to register slot button '{cmd.Slot}/{trimmedId}': {ex.Message}");
            }
        });
    }

    private void HandleRemoveSlotButton(RemoveSlotButtonDto cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.Id))
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            ISlotAttachment? existing;
            lock (_gate)
            {
                _slotButtons.Remove((cmd.Slot, cmd.Id.Trim()), out existing);
            }

            existing?.Dispose();
        });
    }

    private void HandleShowToast(ShowToastDto cmd)
    {
        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            TimeSpan? duration = cmd.DurationMs > 0 ? TimeSpan.FromMilliseconds(cmd.DurationMs) : null;
            _toasts.ShowForMod(ServerBridgeModOwner, cmd.Title ?? string.Empty, cmd.Message ?? string.Empty, cmd.Severity, duration);
        });
    }

    private void HandleRegisterHotkey(RegisterHotkeyDto cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.Id) || string.IsNullOrWhiteSpace(cmd.DisplayName))
        {
            return;
        }

        if (!KeyChord.TryParse(cmd.DefaultChord, out KeyChord parsedChord) || parsedChord.IsEmpty)
        {
            _log?.Invoke("WARN", ServerBridgeModOwner, $"Ignored hotkey '{cmd.Id}' with invalid chord '{cmd.DefaultChord}'.");
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            string trimmedId = cmd.Id.Trim();
            IDisposable? existing;
            lock (_gate)
            {
                _hotkeys.Remove(trimmedId, out existing);
            }

            existing?.Dispose();

            try
            {
                IDisposable reg = _input.KeybindRegistry.RegisterForMod(
                    ServerBridgeModOwner,
                    trimmedId,
                    cmd.DisplayName.Trim(),
                    parsedChord,
                    () =>
                    {
                        if (IsDisposed())
                        {
                            return;
                        }

                        KeyChord activeChord = _input.Keybinds.GetBinding(trimmedId) ?? parsedChord;
                        (int mx, int my) = _input.MousePosition;
                        _systemChannel.Send(
                            new HotkeyTriggeredDto(trimmedId, activeChord.ToString(), _input.IsTextInputFocused, mx, my),
                            MsgHotkeyTriggered);
                    },
                    cmd.AllowWhenTextInputFocused,
                    cmd.SuppressGameInput);

                bool disposeImmediately = false;
                lock (_gate)
                {
                    if (_disposed)
                    {
                        disposeImmediately = true;
                    }
                    else
                    {
                        _hotkeys[trimmedId] = reg;
                    }
                }

                if (disposeImmediately)
                {
                    reg.Dispose();
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke("WARN", ServerBridgeModOwner, $"Failed to register hotkey '{trimmedId}': {ex.Message}");
            }
        });
    }

    private void HandleUnregisterHotkey(UnregisterHotkeyDto cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.Id))
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            IDisposable? existing;
            lock (_gate)
            {
                _hotkeys.Remove(cmd.Id.Trim(), out existing);
            }

            existing?.Dispose();
        });
    }

    private void HandleQueryScreenState(QueryScreenStateDto req)
    {
        if (string.IsNullOrWhiteSpace(req.RequestId))
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            ScreenStateSnapshotDto snapshot = CaptureScreenSnapshot();
            _systemChannel.Send(new ScreenStateResponseDto(req.RequestId, snapshot), MsgScreenStateResponse);
        });
    }

    private void HandleSetScreenSubscription(SetSubscriptionDto cmd)
    {
        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            bool attach = false;
            bool detach = false;
            lock (_gate)
            {
                if (cmd.Enabled)
                {
                    _screenSubRefCount++;
                    attach = _screenSubRefCount == 1;
                }
                else if (_screenSubRefCount > 0)
                {
                    _screenSubRefCount--;
                    detach = _screenSubRefCount == 0;
                }
            }

            if (attach)
            {
                _screens.ScreenChanged += OnScreenChanged;
                _screens.PreGameScreenChanged += OnPreGameScreenChanged;
                _screens.ActiveStashTabChanged += OnActiveStashTabChanged;
            }
            else if (detach)
            {
                _screens.ScreenChanged -= OnScreenChanged;
                _screens.PreGameScreenChanged -= OnPreGameScreenChanged;
                _screens.ActiveStashTabChanged -= OnActiveStashTabChanged;
            }
        });
    }

    private void HandleQueryHoverState(QueryHoverStateDto req)
    {
        if (string.IsNullOrWhiteSpace(req.RequestId))
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            if (req.Refresh)
            {
                _hover.RefreshHoveredItem();
            }

            HoverStateSnapshotDto snapshot = CaptureHoverSnapshot(_hover.HoveredControl, _hover.HoveredItem, _hover.CursorItem);
            _systemChannel.Send(new HoverStateResponseDto(req.RequestId, snapshot), MsgHoverStateResponse);
        });
    }

    private void HandleSetHoverSubscription(SetSubscriptionDto cmd)
    {
        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            bool attach = false;
            bool detach = false;
            lock (_gate)
            {
                if (cmd.Enabled)
                {
                    _hoverSubRefCount++;
                    attach = _hoverSubRefCount == 1;
                }
                else if (_hoverSubRefCount > 0)
                {
                    _hoverSubRefCount--;
                    detach = _hoverSubRefCount == 0;
                }
            }

            if (attach)
            {
                _hover.HoverChanged += OnHoverChanged;
            }
            else if (detach)
            {
                _hover.HoverChanged -= OnHoverChanged;
            }
        });
    }

    // -------------------------------------------------------------------------
    // Phase 2 (0.4.0) Command & Query Handlers
    // -------------------------------------------------------------------------

    private void HandleQueryWorldEntities(QueryWorldEntitiesDto req)
    {
        if (string.IsNullOrWhiteSpace(req.RequestId))
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            IReadOnlyList<WorldEntitySnapshot> entities;
            if (req.Query is { RequiredKind: WorldEntityKind.WorldItem, ExcludedKind: WorldEntityKind.None, FilterVisibleOnly: true, MetadataPrefix: null, Origin: null, MaxResults: null } &&
                _itemFilter is not null)
            {
                entities = _itemFilter.GetVisibleGroundItems(req.Query.MaxDistance);
            }
            else if (_world is not null)
            {
                WorldEntityQuery? sdkQuery = req.Query is null
                    ? null
                    : new WorldEntityQuery(
                        RequiredKind: req.Query.RequiredKind,
                        ExcludedKind: req.Query.ExcludedKind,
                        MetadataPrefix: req.Query.MetadataPrefix,
                        MaxDistance: req.Query.MaxDistance,
                        Origin: req.Query.Origin,
                        FilterVisibleOnly: req.Query.FilterVisibleOnly,
                        MaxResults: req.Query.MaxResults);
                entities = _world.GetEntities(sdkQuery);
            }
            else
            {
                entities = [];
            }

            WorldEntitySnapshotDto[] dtos = new WorldEntitySnapshotDto[entities.Count];
            for (int i = 0; i < entities.Count; i++)
            {
                dtos[i] = MapWorldEntity(entities[i]);
            }

            _systemChannel.Send(new WorldEntitiesResponseDto(req.RequestId, dtos), MsgWorldEntitiesResponse);
        });
    }

    private void HandleQueryLocalPlayer(QueryLocalPlayerDto req)
    {
        if (string.IsNullOrWhiteSpace(req.RequestId))
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            WorldEntitySnapshot? player = _world?.GetLocalPlayer();
            WorldEntitySnapshotDto? dto = player is null ? null : MapWorldEntity(player);
            _systemChannel.Send(new LocalPlayerResponseDto(req.RequestId, dto), MsgLocalPlayerResponse);
        });
    }

    private void HandleQueryEntityStats(QueryEntityStatsDto req)
    {
        if (string.IsNullOrWhiteSpace(req.RequestId))
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            IReadOnlyDictionary<int, int> stats = _stats?.GetAllStats(req.EntityId) ?? new Dictionary<int, int>();
            uint resolvedEntityId = req.EntityId == 0 ? (_world?.GetLocalPlayer()?.EntityId ?? 0u) : req.EntityId;
            _systemChannel.Send(new EntityStatsResponseDto(req.RequestId, resolvedEntityId, stats), MsgEntityStatsResponse);
        });
    }

    private void HandleQueryStat(QueryStatDto req)
    {
        if (string.IsNullOrWhiteSpace(req.RequestId))
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            int value = 0;
            bool found = _stats is not null && _stats.TryReadStat(req.EntityId, req.StatId, out value);
            uint resolvedEntityId = req.EntityId == 0 ? (_world?.GetLocalPlayer()?.EntityId ?? 0u) : req.EntityId;
            _systemChannel.Send(new StatResponseDto(req.RequestId, resolvedEntityId, req.StatId, found, found ? value : 0), MsgStatResponse);
        });
    }

    private void HandleQueryAreaInfo(QueryAreaInfoDto req)
    {
        if (string.IsNullOrWhiteSpace(req.RequestId))
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            AreaInfo? area = _lifecycle?.CurrentArea;
            _systemChannel.Send(new AreaInfoResponseDto(req.RequestId, area), MsgAreaInfoResponse);
        });
    }

    private void HandleSetAreaSubscription(SetSubscriptionDto cmd)
    {
        if (_lifecycle is null)
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            bool attach = false;
            bool detach = false;
            lock (_gate)
            {
                if (cmd.Enabled)
                {
                    _areaSubRefCount++;
                    attach = _areaSubRefCount == 1;
                }
                else if (_areaSubRefCount > 0)
                {
                    _areaSubRefCount--;
                    detach = _areaSubRefCount == 0;
                }
            }

            if (attach)
            {
                _lifecycle.AreaEntered += OnAreaEntered;
                _lifecycle.AreaLeft += OnAreaLeft;
            }
            else if (detach)
            {
                _lifecycle.AreaEntered -= OnAreaEntered;
                _lifecycle.AreaLeft -= OnAreaLeft;
            }
        });
    }

    private void HandleQueryItemFilterState(QueryItemFilterStateDto req)
    {
        if (string.IsNullOrWhiteSpace(req.RequestId))
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            ItemFilterState state = _itemFilter?.GetState() ?? ItemFilterState.Empty;
            _systemChannel.Send(new ItemFilterStateResponseDto(req.RequestId, state), MsgItemFilterStateResponse);
        });
    }

    private void HandleReloadItemFilter(ReloadItemFilterDto req)
    {
        if (string.IsNullOrWhiteSpace(req.RequestId))
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            bool dispatched = _itemFilter is not null && _itemFilter.Reload();
            _systemChannel.Send(new ReloadItemFilterResponseDto(req.RequestId, dispatched), MsgReloadItemFilterResponse);
        });
    }

    private void HandleEvaluateWorldItemFilter(EvaluateWorldItemFilterDto req)
    {
        if (string.IsNullOrWhiteSpace(req.RequestId))
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            ItemFilterResult result = ItemFilterResult.Unevaluated;
            if (_itemFilter is not null)
            {
                if (req.LiveEvaluate)
                {
                    result = _itemFilter.EvaluateItem(req.EntityId);
                    if (!result.Evaluated)
                    {
                        result = _itemFilter.GetWorldItemResult(req.EntityId);
                    }
                }
                else
                {
                    result = _itemFilter.GetWorldItemResult(req.EntityId);
                    if (!result.Evaluated)
                    {
                        result = _itemFilter.EvaluateItem(req.EntityId);
                    }
                }
            }

            _systemChannel.Send(new EvaluateWorldItemFilterResponseDto(req.RequestId, result), MsgEvaluateWorldItemFilterResponse);
        });
    }

    private void HandleSetFilterSubscription(SetSubscriptionDto cmd)
    {
        if (_itemFilter is null)
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            bool attach = false;
            bool detach = false;
            lock (_gate)
            {
                if (cmd.Enabled)
                {
                    _filterSubRefCount++;
                    attach = _filterSubRefCount == 1;
                }
                else if (_filterSubRefCount > 0)
                {
                    _filterSubRefCount--;
                    detach = _filterSubRefCount == 0;
                }
            }

            if (attach)
            {
                _itemFilter.FilterReloaded += OnFilterReloaded;
            }
            else if (detach)
            {
                _itemFilter.FilterReloaded -= OnFilterReloaded;
            }
        });
    }

    private void HandleQueryCameraState(QueryCameraStateDto req)
    {
        if (string.IsNullOrWhiteSpace(req.RequestId))
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            CameraState state = _camera?.GetState() ?? CameraState.Invalid;
            _systemChannel.Send(new CameraStateResponseDto(req.RequestId, state), MsgCameraStateResponse);
        });
    }

    private void HandleSetCameraZoomOverride(SetCameraZoomOverrideDto cmd)
    {
        if (_camera is null)
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            if (cmd.ZoomOverride is float z && !float.IsNaN(z) && !float.IsInfinity(z) && z >= 0f)
            {
                bool disposedNow;
                lock (_gate)
                {
                    disposedNow = _disposed;
                    if (!disposedNow)
                    {
                        _activeCameraZoomOverride = z;
                    }
                }

                if (disposedNow)
                {
                    _camera.ClearZoomOverrideForOwner(ServerBridgeModOwner);
                }
                else
                {
                    _camera.SetZoomOverrideForOwner(ServerBridgeModOwner, z);
                }
            }
            else
            {
                lock (_gate)
                {
                    _activeCameraZoomOverride = null;
                }

        _camera.ClearZoomOverrideForOwner(ServerBridgeModOwner);
            }
        });
    }

    // -------------------------------------------------------------------------
    // Phase 4 + 5 (1.2.0) Command & Query Handlers
    // -------------------------------------------------------------------------

    private void HandleShowTooltip(ShowTooltipDto cmd)
    {
        if (_tooltips is null || cmd.Content is null)
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            _tooltips.ShowTooltipForMod(ServerBridgeModOwner, cmd.Content, cmd.ScreenX, cmd.ScreenY);
        });
    }

    private void HandleHideTooltip(HideTooltipDto _)
    {
        if (_tooltips is null)
        {
            return;
        }

        RunOnGameThreadGuarded(() => _tooltips.HideTooltipForMod(ServerBridgeModOwner));
    }

    private void HandleRegisterItemTooltipRule(ItemTooltipRuleDto cmd)
    {
        if (_tooltips is null || string.IsNullOrWhiteSpace(cmd.Id) || cmd.Sections is null || cmd.Sections.Count == 0)
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            string trimmedId = cmd.Id.Trim();
            IDisposable? existing;
            lock (_gate)
            {
                _tooltipRules.Remove(trimmedId, out existing);
            }

            existing?.Dispose();

            IReadOnlyList<TooltipSection> sections = [.. cmd.Sections];
            string? metadataPrefix = string.IsNullOrWhiteSpace(cmd.MetadataPrefix) ? null : cmd.MetadataPrefix.Trim();
            string? baseNameContains = string.IsNullOrWhiteSpace(cmd.BaseNameContains) ? null : cmd.BaseNameContains.Trim();
            uint? itemClassId = cmd.ItemClassId;
            ItemRarity? minRarity = cmd.MinRarity;
            bool? requireAltHeld = cmd.RequireAltHeld;

            IDisposable reg = _tooltips.RegisterItemTooltipAugmenter(
                ctx =>
                {
                    if (requireAltHeld.HasValue && ctx.IsAltHeld != requireAltHeld.Value)
                    {
                        return [];
                    }

                    if (metadataPrefix is not null &&
                        !ctx.Item.MetadataPath.StartsWith(metadataPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return [];
                    }

                    if (baseNameContains is not null &&
                        !ctx.Item.BaseName.Contains(baseNameContains, StringComparison.OrdinalIgnoreCase))
                    {
                        return [];
                    }

                    if (itemClassId.HasValue && ctx.Item.ItemClassId != itemClassId.Value)
                    {
                        return [];
                    }

                    if (minRarity.HasValue && ctx.Item.Rarity < minRarity.Value)
                    {
                        return [];
                    }

                    return sections;
                },
                cmd.Priority);

            bool disposeImmediately = false;
            lock (_gate)
            {
                if (_disposed)
                {
                    disposeImmediately = true;
                }
                else
                {
                    _tooltipRules[trimmedId] = reg;
                }
            }

            if (disposeImmediately)
            {
                reg.Dispose();
            }
        });
    }

    private void HandleRemoveItemTooltipRule(RemoveRuleDto cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.Id))
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            IDisposable? existing;
            lock (_gate)
            {
                _tooltipRules.Remove(cmd.Id.Trim(), out existing);
            }

            existing?.Dispose();
        });
    }

    private void HandleRegisterStashAffinityRoute(StashAffinityRouteRuleDto cmd)
    {
        if (_stashAffinity is null || string.IsNullOrWhiteSpace(cmd.Id))
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            string trimmedId = cmd.Id.Trim();
            IDisposable? existing;
            lock (_gate)
            {
                _affinityRoutes.Remove(trimmedId, out existing);
            }

            existing?.Dispose();

            string? metadataPrefix = string.IsNullOrWhiteSpace(cmd.MetadataPrefix) ? null : cmd.MetadataPrefix.Trim();
            string? baseNameContains = string.IsNullOrWhiteSpace(cmd.BaseNameContains) ? null : cmd.BaseNameContains.Trim();
            uint? itemClassId = cmd.ItemClassId;
            StashKind? stashKind = cmd.StashKind;
            var route = new StashAffinityRoute(cmd.TargetTabIndex, cmd.AffinityId, cmd.ReplaceStockTargets);

            try
            {
                IDisposable reg = _stashAffinity.RegisterRoute(
                    $"{ServerBridgeModOwner}:{trimmedId}",
                    ctx =>
                    {
                        if (stashKind.HasValue && ctx.StashKind != stashKind.Value)
                        {
                            return null;
                        }

                        if (metadataPrefix is not null &&
                            !ctx.Item.MetadataPath.StartsWith(metadataPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            return null;
                        }

                        if (baseNameContains is not null &&
                            !ctx.Item.BaseName.Contains(baseNameContains, StringComparison.OrdinalIgnoreCase))
                        {
                            return null;
                        }

                        if (itemClassId.HasValue && ctx.Item.ItemClassId != itemClassId.Value)
                        {
                            return null;
                        }

                        return route;
                    },
                    cmd.Priority);

                bool disposeImmediately = false;
                lock (_gate)
                {
                    if (_disposed)
                    {
                        disposeImmediately = true;
                    }
                    else
                    {
                        _affinityRoutes[trimmedId] = reg;
                    }
                }

                if (disposeImmediately)
                {
                    reg.Dispose();
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke("WARN", ServerBridgeModOwner, $"Failed to register stash affinity route '{trimmedId}': {ex.Message}");
            }
        });
    }

    private void HandleRemoveStashAffinityRoute(RemoveRuleDto cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.Id))
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            IDisposable? existing;
            lock (_gate)
            {
                _affinityRoutes.Remove(cmd.Id.Trim(), out existing);
            }

            existing?.Dispose();
        });
    }

    private void HandleSetPictureTexture(SetPictureTextureDto cmd)
    {
        if (_ui is null || string.IsNullOrWhiteSpace(cmd.WindowId) || string.IsNullOrWhiteSpace(cmd.ControlPath) || string.IsNullOrWhiteSpace(cmd.TexturePath))
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            if (FindUiControl(cmd.WindowId, cmd.ControlPath) is IPicture picture)
            {
                picture.SetTexture(cmd.TexturePath, cmd.ResizeToImage);
            }
        });
    }

    private void HandleSetComboBoxSelected(SetComboBoxSelectedDto cmd)
    {
        if (_ui is null || string.IsNullOrWhiteSpace(cmd.WindowId) || string.IsNullOrWhiteSpace(cmd.ControlPath))
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            if (FindUiControl(cmd.WindowId, cmd.ControlPath) is IComboBox combo &&
                cmd.SelectedIndex >= -1 &&
                cmd.SelectedIndex < combo.ItemCount)
            {
                combo.SelectedIndex = cmd.SelectedIndex;
            }
        });
    }

    private void HandleSelectTab(SelectTabDto cmd)
    {
        if (_ui is null || string.IsNullOrWhiteSpace(cmd.WindowId) || string.IsNullOrWhiteSpace(cmd.ControlPath))
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            if (FindUiControl(cmd.WindowId, cmd.ControlPath) is ITabContainer tabs &&
                cmd.TabIndex >= 0 &&
                cmd.TabIndex < tabs.TabCount)
            {
                tabs.SelectTab(cmd.TabIndex);
            }
        });
    }

    private void HandleCreateItemGrid(CreateItemGridDto cmd)
    {
        if (_ui is null || string.IsNullOrWhiteSpace(cmd.WindowId) || string.IsNullOrWhiteSpace(cmd.GridId) || cmd.Options is null)
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            IUiControl? parent = FindUiControl(cmd.WindowId, cmd.ParentControlPath);
            if (parent is null)
            {
                return;
            }

            string winId = cmd.WindowId.Trim();
            string gridId = cmd.GridId.Trim();
            var key = (winId, gridId);

            IUiItemGrid? previous;
            Action? previousClickDetach;
            lock (_gate)
            {
                _ownedGrids.Remove(key, out previous);
                _gridClickSubs.Remove(key, out previousClickDetach);
            }

            previousClickDetach?.Invoke();
            previous?.Dispose();

            try
            {
                IUiItemGrid grid = _ui.CreateItemGrid(parent, cmd.Options, gridId);
                bool disposeImmediately = false;
                lock (_gate)
                {
                    if (_disposed)
                    {
                        disposeImmediately = true;
                    }
                    else
                    {
                        _ownedGrids[key] = grid;
                    }
                }

                if (disposeImmediately)
                {
                    grid.Dispose();
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke("WARN", ServerBridgeModOwner, $"Failed to create item grid '{winId}/{gridId}': {ex.Message}");
            }
        });
    }

    private void HandleSetGridItem(SetGridItemDto cmd)
    {
        if (_ui is null || string.IsNullOrWhiteSpace(cmd.WindowId) || string.IsNullOrWhiteSpace(cmd.GridPath) || cmd.Item is null || string.IsNullOrWhiteSpace(cmd.Item.ItemId))
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            IUiItemGrid? grid = ResolveItemGrid(cmd.WindowId, cmd.GridPath);
            if (grid is null)
            {
                return;
            }

            grid.RemoveItem(cmd.Item.ItemId);
            grid.PlaceItem(new UiGridItem(
                ItemId: cmd.Item.ItemId,
                Col: cmd.Item.Col,
                Row: cmd.Item.Row,
                Width: cmd.Item.Width,
                Height: cmd.Item.Height,
                IconPath: cmd.Item.IconPath,
                StackSize: cmd.Item.StackSize,
                Rarity: cmd.Item.Rarity,
                Title: cmd.Item.Title,
                TooltipLines: cmd.Item.TooltipLines));
        });
    }

    private void HandleRemoveGridItem(RemoveGridItemDto cmd)
    {
        if (_ui is null || string.IsNullOrWhiteSpace(cmd.WindowId) || string.IsNullOrWhiteSpace(cmd.GridPath) || string.IsNullOrWhiteSpace(cmd.ItemId))
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            ResolveItemGrid(cmd.WindowId, cmd.GridPath)?.RemoveItem(cmd.ItemId);
        });
    }

    private void HandleClearGrid(ClearGridDto cmd)
    {
        if (_ui is null || string.IsNullOrWhiteSpace(cmd.WindowId) || string.IsNullOrWhiteSpace(cmd.GridPath))
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            ResolveItemGrid(cmd.WindowId, cmd.GridPath)?.Clear();
        });
    }

    private void HandleSetGridClickSubscription(SetGridClickSubscriptionDto cmd)
    {
        if (_ui is null || string.IsNullOrWhiteSpace(cmd.WindowId) || string.IsNullOrWhiteSpace(cmd.GridPath))
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            string winId = cmd.WindowId.Trim();
            string gridPath = cmd.GridPath.Trim();
            var key = (winId, gridPath);

            if (!cmd.Enabled)
            {
                Action? detach;
                lock (_gate)
                {
                    _gridClickSubs.Remove(key, out detach);
                }

                detach?.Invoke();
                return;
            }

            lock (_gate)
            {
                if (_gridClickSubs.ContainsKey(key))
                {
                    return;
                }
            }

            IUiItemGrid? grid = ResolveItemGrid(winId, gridPath);
            if (grid is null)
            {
                return;
            }

            void OnLeftClick(UiGridCellEventArgs args)
            {
                if (!IsDisposed())
                {
                    _systemChannel.Send(new GridCellClickDto(winId, gridPath, args.Col, args.Row, args.Item?.ItemId, MouseButton.Left, args.Modifiers), MsgGridCellClick);
                }
            }

            void OnRightClick(UiGridCellEventArgs args)
            {
                if (!IsDisposed())
                {
                    _systemChannel.Send(new GridCellClickDto(winId, gridPath, args.Col, args.Row, args.Item?.ItemId, MouseButton.Right, args.Modifiers), MsgGridCellClick);
                }
            }

            void OnCtrlClick(UiGridCellEventArgs args)
            {
                if (!IsDisposed())
                {
                    _systemChannel.Send(new GridCellClickDto(winId, gridPath, args.Col, args.Row, args.Item?.ItemId, MouseButton.Left, args.Modifiers | KeyModifiers.Control), MsgGridCellClick);
                }
            }

            grid.CellClicked += OnLeftClick;
            grid.CellRightClicked += OnRightClick;
            grid.CellCtrlClicked += OnCtrlClick;

            Action detachAction = () =>
            {
                grid.CellClicked -= OnLeftClick;
                grid.CellRightClicked -= OnRightClick;
                grid.CellCtrlClicked -= OnCtrlClick;
            };

            bool detachImmediately = false;
            lock (_gate)
            {
                if (_disposed || _gridClickSubs.ContainsKey(key))
                {
                    detachImmediately = true;
                }
                else
                {
                    _gridClickSubs[key] = detachAction;
                }
            }

            if (detachImmediately)
            {
                detachAction();
            }
        });
    }

    private void HandlePlayAudioEx(PlayAudioExDto req)
    {
        if (string.IsNullOrWhiteSpace(req.RequestId))
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            int handle = 0;
            if (_audio is not null && !string.IsNullOrWhiteSpace(req.PathOrEvent))
            {
                handle = req.PathOrEvent.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
                    ? _audio.PlaySoundFile(req.PathOrEvent, req.Volume, req.Loop)
                    : _audio.PlayEvent(req.PathOrEvent, req.Volume);

                if (handle > 0)
                {
                    bool stopImmediately = false;
                    lock (_gate)
                    {
                        if (_disposed)
                        {
                            stopImmediately = true;
                        }
                        else
                        {
                            _ownedAudioChannels.Add(handle);
                        }
                    }

                    if (stopImmediately)
                    {
                        _audio.StopChannel(handle);
                        handle = 0;
                    }
                }
            }

            _systemChannel.Send(new PlayAudioExResponseDto(req.RequestId, handle), MsgPlayAudioExResponse);
        });
    }

    private void HandleStopAudio(StopAudioDto cmd)
    {
        if (_audio is null || cmd.ChannelHandle <= 0)
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            lock (_gate)
            {
                _ownedAudioChannels.Remove(cmd.ChannelHandle);
            }

            _audio.StopChannel(cmd.ChannelHandle);
        });
    }

    private void HandlePlaySoundAtPosition(PlaySoundAtPositionDto req)
    {
        if (string.IsNullOrWhiteSpace(req.RequestId))
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            int handle = 0;
            if (_audio is not null && !string.IsNullOrWhiteSpace(req.PathOrEvent))
            {
                AudioChannelHandle ch = _audio.PlaySoundAtPosition(
                    req.PathOrEvent,
                    req.X,
                    req.Y,
                    req.Z,
                    req.Volume,
                    req.MaxDistance,
                    req.Loop);
                handle = ch.Value;

                if (handle > 0)
                {
                    bool stopImmediately = false;
                    lock (_gate)
                    {
                        if (_disposed)
                        {
                            stopImmediately = true;
                        }
                        else
                        {
                            _ownedAudioChannels.Add(handle);
                        }
                    }

                    if (stopImmediately)
                    {
                        _audio.StopChannel(handle);
                        handle = 0;
                    }
                }
            }

            _systemChannel.Send(new PlayAudioExResponseDto(req.RequestId, handle), MsgPlayAudioExResponse);
        });
    }

    private void HandleSetAudioPosition(SetAudioPositionDto cmd)
    {
        if (_audio is null || cmd.ChannelHandle <= 0)
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            _audio.SetChannelPosition(new AudioChannelHandle(cmd.ChannelHandle), cmd.X, cmd.Y, cmd.Z, cmd.MaxDistance);
        });
    }

    private void HandleSpawnClientObject(SpawnClientObjectDto req)
    {
        if (string.IsNullOrWhiteSpace(req.RequestId))
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            uint objectId = 0;
            if (_world is not null && !string.IsNullOrWhiteSpace(req.MetadataPath))
            {
                objectId = _world.SpawnClientObject(req.MetadataPath, req.X, req.Y, req.Z, req.RotationRadians, req.Scale);
                if (objectId != 0)
                {
                    bool removeImmediately = false;
                    lock (_gate)
                    {
                        if (_disposed)
                        {
                            removeImmediately = true;
                        }
                        else
                        {
                            _ownedWorldObjects.Add(objectId);
                        }
                    }

                    if (removeImmediately)
                    {
                        _world.RemoveClientObject(objectId);
                        objectId = 0;
                    }
                }
            }

            _systemChannel.Send(new SpawnClientObjectResponseDto(req.RequestId, objectId), MsgSpawnClientObjectResponse);
        });
    }

    private void HandleRemoveClientObject(RemoveClientObjectDto cmd)
    {
        if (_world is null || cmd.ObjectId == 0)
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            lock (_gate)
            {
                _ownedWorldObjects.Remove(cmd.ObjectId);
            }

            _world.RemoveClientObject(cmd.ObjectId);
        });
    }

    private void HandleSetExtendedU32Stacks(SetExtendedU32StacksDto cmd)
    {
        if (_stackExtension is null)
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            _stackExtension.SetExtendedU32StacksEnabledForOwner(ServerBridgeModOwner, cmd.Enabled);
            if (cmd.FormatMillions.HasValue)
            {
                _stackExtension.SetFormatMillionsEnabledForOwner(ServerBridgeModOwner, cmd.FormatMillions.Value);
            }
        });
    }

    private void HandleSetEntityStackOverride(SetEntityStackOverrideDto cmd)
    {
        if (_stackExtension is null || cmd.EntityId == 0)
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            _stackExtension.SetEntityStackOverrideForOwner(ServerBridgeModOwner, cmd.EntityId, cmd.StackSize);
        });
    }

    private void HandleSetCompactSockets(SetCompactSocketsDto cmd)
    {
        if (_ui is null)
        {
            return;
        }

        RunOnGameThreadGuarded(() =>
        {
            if (IsDisposed())
            {
                return;
            }

            _ui.SetCompactSocketsEnabledForOwner(ServerBridgeModOwner, cmd.Enabled);
        });
    }

    private IUiControl? FindUiControl(string windowId, string? controlPath)
    {
        if (_ui is null)
        {
            return null;
        }

        IUiWindow? window = !string.IsNullOrWhiteSpace(windowId)
            ? _ui.GetWindow(windowId)
            : _ui.OpenWindows.FirstOrDefault();
        if (window is null)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(controlPath) ? window : window.Find(controlPath);
    }

    private IUiItemGrid? ResolveItemGrid(string windowId, string gridPath)
    {
        var key = (windowId.Trim(), gridPath.Trim());
        lock (_gate)
        {
            if (_ownedGrids.TryGetValue(key, out IUiItemGrid? owned))
            {
                return owned;
            }
        }

        return FindUiControl(windowId, gridPath) as IUiItemGrid;
    }

    private void HandleWindowTreeInvalidated(string windowId)
    {
        if (string.IsNullOrWhiteSpace(windowId))
        {
            return;
        }

        Action[] detaches;
        lock (_gate)
        {
            var clickKeys = _gridClickSubs.Keys
                .Where(k => string.Equals(k.WindowId, windowId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            detaches = new Action[clickKeys.Length];
            for (int i = 0; i < clickKeys.Length; i++)
            {
                if (_gridClickSubs.Remove(clickKeys[i], out Action? d))
                {
                    detaches[i] = d;
                }
            }

            var gridKeys = _ownedGrids.Keys
                .Where(k => string.Equals(k.WindowId, windowId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            foreach (var gk in gridKeys)
            {
                _ownedGrids.Remove(gk);
            }
        }

        foreach (Action? detach in detaches)
        {
            try { detach?.Invoke(); }
            catch { }
        }
    }

    // -------------------------------------------------------------------------
    // Client Event Forwarders (Opt-In Subscriptions)
    // -------------------------------------------------------------------------

    private void OnScreenChanged(ScreenChangedEventArgs args)
    {
        if (IsDisposed())
        {
            return;
        }

        _systemChannel.Send(
            new ScreenChangedDto(CaptureScreenSnapshot(), ChangedPanel: args.Panel, PanelIsOpen: args.IsOpen),
            MsgScreenStateChanged);
    }

    private void OnPreGameScreenChanged(PreGameScreenChangedEventArgs args)
    {
        if (IsDisposed())
        {
            return;
        }

        _systemChannel.Send(
            new ScreenChangedDto(CaptureScreenSnapshot(), PreGameChanged: true),
            MsgScreenStateChanged);
    }

    private void OnActiveStashTabChanged(ActiveStashTabChangedEventArgs args)
    {
        if (IsDisposed())
        {
            return;
        }

        _systemChannel.Send(
            new ScreenChangedDto(CaptureScreenSnapshot(), StashTabChanged: true),
            MsgScreenStateChanged);
    }

    private void OnHoverChanged(HoverChangedEventArgs args)
    {
        if (IsDisposed())
        {
            return;
        }

        HoverStateSnapshotDto snapshot = CaptureHoverSnapshot(args.Control, args.Item, args.CursorItem);
        _systemChannel.Send(snapshot, MsgHoverStateChanged);
    }

    private void OnAreaEntered(AreaEventArgs args)
    {
        if (IsDisposed())
        {
            return;
        }

        AreaInfo? area = args.Area ?? _lifecycle?.CurrentArea;
        IReadOnlyList<uint> rulesets = area?.ActiveRulesetIds is { Count: > 0 } areaRulesets
            ? areaRulesets
            : [.. args.ActiveRulesetIds.Select(static id => (uint)id)];

        _systemChannel.Send(
            new AreaChangedDto(Kind: 0, AreaId: args.AreaId ?? string.Empty, Area: area, Seed: args.Seed, ActiveRulesetIds: rulesets),
            MsgAreaChanged);
    }

    private void OnAreaLeft(string areaId)
    {
        if (IsDisposed())
        {
            return;
        }

        _systemChannel.Send(
            new AreaChangedDto(Kind: 1, AreaId: areaId ?? string.Empty, Area: null, Seed: 0u, ActiveRulesetIds: null),
            MsgAreaChanged);
    }

    private void OnFilterReloaded(ItemFilterReloadedEventArgs args)
    {
        if (IsDisposed())
        {
            return;
        }

        _systemChannel.Send(args, MsgFilterReloaded);
    }

    // -------------------------------------------------------------------------
    // Snapshot Mappers & Cleanup
    // -------------------------------------------------------------------------

    private ScreenStateSnapshotDto CaptureScreenSnapshot() =>
        new(_screens.CurrentPreGameScreen, _screens.ActivePanels, _screens.ActiveStashTab);

    private static HoveredItemSnapshotDto? MapHoveredItem(HoveredItemInfo? item) =>
        item is null
            ? null
            : new HoveredItemSnapshotDto(
                item.ItemId,
                item.InventoryId,
                item.GridX,
                item.GridY,
                item.Width,
                item.Height,
                item.MetadataPath,
                item.BaseName,
                item.StackSize,
                item.MaxStackSize,
                item.Rarity,
                item.IsIdentified,
                item.IsCorrupted,
                item.ItemLevel,
                item.ItemClassId,
                unchecked((ulong)item.EntityHandle),
                unchecked((ulong)item.ControlHandle));

    private static WorldEntitySnapshotDto MapWorldEntity(WorldEntitySnapshot entity) =>
        new(
            entity.EntityId,
            unchecked((ulong)entity.EntityHandle),
            entity.Kind,
            entity.MetadataPath,
            entity.GridX,
            entity.GridY,
            entity.Position,
            entity.Hp,
            entity.MaxHp,
            entity.Rarity,
            entity.IsFilterVisible,
            MapHoveredItem(entity.WorldItem));

    private static HoverStateSnapshotDto CaptureHoverSnapshot(IUiControl? control, HoveredItemInfo? item, CursorItemInfo? cursor)
    {
        HoveredItemSnapshotDto? itemDto = MapHoveredItem(item);

        CursorItemSnapshotDto? cursorDto = cursor is null
            ? null
            : new CursorItemSnapshotDto(
                cursor.Mode,
                cursor.ItemId,
                cursor.SourceInventoryId,
                cursor.SourceGridX,
                cursor.SourceGridY,
                cursor.Width,
                cursor.Height,
                cursor.MetadataPath,
                cursor.BaseName,
                cursor.StackSize,
                cursor.MaxStackSize,
                cursor.Rarity,
                cursor.IsIdentified,
                cursor.IsCorrupted,
                cursor.ShiftKeep,
                unchecked((ulong)cursor.EntityHandle));

        return new HoverStateSnapshotDto(
            control?.Id,
            control is not null ? unchecked((ulong)control.NativeHandle) : 0ul,
            itemDto,
            cursorDto);
    }

    private void ClearSessionResourcesCore()
    {
        ISlotAttachment[] slotButtons;
        IDisposable[] hotkeys;
        IDisposable[] tooltipRules;
        IDisposable[] affinityRoutes;
        Action[] gridClickDetaches;
        IUiItemGrid[] ownedGrids;
        int[] ownedAudioChannels;
        uint[] ownedWorldObjects;
        bool detachScreen;
        bool detachHover;
        bool detachArea;
        bool detachFilter;
        bool clearCameraZoom;
        lock (_gate)
        {
            slotButtons = [.. _slotButtons.Values];
            _slotButtons.Clear();
            hotkeys = [.. _hotkeys.Values];
            _hotkeys.Clear();
            tooltipRules = [.. _tooltipRules.Values];
            _tooltipRules.Clear();
            affinityRoutes = [.. _affinityRoutes.Values];
            _affinityRoutes.Clear();
            gridClickDetaches = [.. _gridClickSubs.Values];
            _gridClickSubs.Clear();
            ownedGrids = [.. _ownedGrids.Values];
            _ownedGrids.Clear();
            ownedAudioChannels = [.. _ownedAudioChannels];
            _ownedAudioChannels.Clear();
            ownedWorldObjects = [.. _ownedWorldObjects];
            _ownedWorldObjects.Clear();
            detachScreen = _screenSubRefCount > 0;
            _screenSubRefCount = 0;
            detachHover = _hoverSubRefCount > 0;
            _hoverSubRefCount = 0;
            detachArea = _areaSubRefCount > 0;
            _areaSubRefCount = 0;
            detachFilter = _filterSubRefCount > 0;
            _filterSubRefCount = 0;
            clearCameraZoom = _activeCameraZoomOverride.HasValue;
            _activeCameraZoomOverride = null;
        }

        if (detachScreen)
        {
            _screens.ScreenChanged -= OnScreenChanged;
            _screens.PreGameScreenChanged -= OnPreGameScreenChanged;
            _screens.ActiveStashTabChanged -= OnActiveStashTabChanged;
        }

        if (detachHover)
        {
            _hover.HoverChanged -= OnHoverChanged;
        }

        if (detachArea && _lifecycle is not null)
        {
            _lifecycle.AreaEntered -= OnAreaEntered;
            _lifecycle.AreaLeft -= OnAreaLeft;
        }

        if (detachFilter && _itemFilter is not null)
        {
            _itemFilter.FilterReloaded -= OnFilterReloaded;
        }

        if (clearCameraZoom && _camera is not null)
        {
            try
            {
                _camera.ClearZoomOverrideForOwner(ServerBridgeModOwner);
            }
            catch
            {
                // Best-effort camera zoom restore on disconnect/dispose.
            }
        }

        foreach (Action detach in gridClickDetaches)
        {
            try { detach(); }
            catch { }
        }

        foreach (IUiItemGrid grid in ownedGrids)
        {
            try { grid.Dispose(); }
            catch { }
        }

        foreach (IDisposable rule in tooltipRules)
        {
            try { rule.Dispose(); }
            catch { }
        }

        try { _tooltips?.HideTooltipForMod(ServerBridgeModOwner); }
        catch { }

        foreach (IDisposable route in affinityRoutes)
        {
            try { route.Dispose(); }
            catch { }
        }

        if (_audio is not null)
        {
            foreach (int ch in ownedAudioChannels)
            {
                try { _audio.StopChannel(ch); }
                catch { }
            }
        }

        if (_world is not null)
        {
            foreach (uint objId in ownedWorldObjects)
            {
                try { _world.RemoveClientObject(objId); }
                catch { }
            }
        }

        try { _stackExtension?.ClearForOwner(ServerBridgeModOwner); }
        catch { }

        try { _ui?.ClearCompactSocketsForOwner(ServerBridgeModOwner); }
        catch { }

        foreach (ISlotAttachment button in slotButtons)
        {
            try
            {
                button.Dispose();
            }
            catch
            {
                // Best-effort cleanup on disconnect/dispose.
            }
        }

        foreach (IDisposable hotkey in hotkeys)
        {
            try
            {
                hotkey.Dispose();
            }
            catch
            {
                // Best-effort cleanup on disconnect/dispose.
            }
        }

        _toasts.ClearForMod(ServerBridgeModOwner);
    }

    private void RunOnGameThreadGuarded(Action action)
    {
        if (_scheduler.IsOnGameThread)
        {
            action();
            return;
        }

        try
        {
            _scheduler.RunOnGameThread(action);
        }
        catch (InvalidOperationException)
        {
            // Fallback if the scheduler queue is already torn down during shutdown.
            action();
        }
    }

    private bool IsDisposed()
    {
        lock (_gate)
        {
            return _disposed;
        }
    }

    // -------------------------------------------------------------------------
    // Wire DTOs (JSON schema matching LocalExile.Client.ServerBridge Tier 1 0.3.0 / 0.4.0)
    // -------------------------------------------------------------------------

    private sealed record RegisterSlotButtonDto(
        ScreenSlot Slot,
        string Id,
        string Text,
        int Order = 0,
        float Width = 0f,
        float Height = 0f,
        float Spacing = 6f,
        string? Tooltip = null);

    private sealed record RemoveSlotButtonDto(ScreenSlot Slot, string Id);

    private sealed record SlotButtonClickDto(ScreenSlot Slot, string Id);

    private sealed record ShowToastDto(
        string Title,
        string Message,
        ToastSeverity Severity = ToastSeverity.Info,
        int DurationMs = 4000);

    private sealed record RegisterHotkeyDto(
        string Id,
        string DisplayName,
        string DefaultChord,
        bool AllowWhenTextInputFocused = false,
        bool SuppressGameInput = false);

    private sealed record UnregisterHotkeyDto(string Id);

    private sealed record HotkeyTriggeredDto(
        string Id,
        string ActiveChord,
        bool IsTextInputFocused = false,
        int MouseX = 0,
        int MouseY = 0);

    private sealed record QueryScreenStateDto(string RequestId);

    private sealed record ScreenStateSnapshotDto(
        PreGameScreen CurrentPreGameScreen,
        GamePanel ActivePanels,
        StashTabInfo? ActiveStashTab = null);

    private sealed record ScreenStateResponseDto(string RequestId, ScreenStateSnapshotDto State);

    private sealed record ScreenChangedDto(
        ScreenStateSnapshotDto State,
        GamePanel? ChangedPanel = null,
        bool? PanelIsOpen = null,
        bool StashTabChanged = false,
        bool PreGameChanged = false);

    private sealed record SetSubscriptionDto(bool Enabled);

    private sealed record QueryHoverStateDto(string RequestId, bool Refresh = true);

    private sealed record HoveredItemSnapshotDto(
        uint ItemId,
        uint InventoryId,
        int GridX,
        int GridY,
        int Width,
        int Height,
        string MetadataPath,
        string BaseName,
        uint StackSize,
        uint MaxStackSize,
        ItemRarity Rarity,
        bool IsIdentified,
        bool IsCorrupted,
        int ItemLevel = 0,
        uint ItemClassId = 0,
        ulong EntityHandle = 0,
        ulong ControlHandle = 0);

    private sealed record CursorItemSnapshotDto(
        CursorMode Mode,
        uint ItemId,
        uint SourceInventoryId,
        int SourceGridX,
        int SourceGridY,
        int Width,
        int Height,
        string MetadataPath,
        string BaseName,
        uint StackSize,
        uint MaxStackSize,
        ItemRarity Rarity,
        bool IsIdentified,
        bool IsCorrupted,
        bool ShiftKeep = false,
        ulong EntityHandle = 0);

    private sealed record HoverStateSnapshotDto(
        string? HoveredControlId = null,
        ulong HoveredControlHandle = 0,
        HoveredItemSnapshotDto? HoveredItem = null,
        CursorItemSnapshotDto? CursorItem = null);

    private sealed record HoverStateResponseDto(string RequestId, HoverStateSnapshotDto State);

    // Phase 2 (0.4.0) Wire DTOs

    private sealed record WorldEntityQueryDto(
        WorldEntityKind RequiredKind = WorldEntityKind.None,
        WorldEntityKind ExcludedKind = WorldEntityKind.None,
        string? MetadataPrefix = null,
        float? MaxDistance = null,
        WorldPosition3D? Origin = null,
        bool FilterVisibleOnly = false,
        int? MaxResults = null);

    private sealed record QueryWorldEntitiesDto(string RequestId, WorldEntityQueryDto? Query = null);

    private sealed record WorldEntitySnapshotDto(
        uint EntityId,
        ulong EntityHandle,
        WorldEntityKind Kind,
        string MetadataPath,
        int GridX,
        int GridY,
        WorldPosition3D Position,
        int Hp = 0,
        int MaxHp = 0,
        ItemRarity Rarity = ItemRarity.Normal,
        bool IsFilterVisible = false,
        HoveredItemSnapshotDto? WorldItem = null);

    private sealed record WorldEntitiesResponseDto(string RequestId, IReadOnlyList<WorldEntitySnapshotDto> Entities);

    private sealed record QueryLocalPlayerDto(string RequestId);

    private sealed record LocalPlayerResponseDto(string RequestId, WorldEntitySnapshotDto? Player);

    private sealed record QueryEntityStatsDto(string RequestId, uint EntityId = 0);

    private sealed record EntityStatsResponseDto(string RequestId, uint EntityId, IReadOnlyDictionary<int, int> Stats);

    private sealed record QueryStatDto(string RequestId, int StatId, uint EntityId = 0);

    private sealed record StatResponseDto(string RequestId, uint EntityId, int StatId, bool Found, int Value);

    private sealed record QueryAreaInfoDto(string RequestId);

    private sealed record AreaInfoResponseDto(string RequestId, AreaInfo? Area);

    private sealed record AreaChangedDto(
        int Kind,
        string AreaId,
        AreaInfo? Area = null,
        uint Seed = 0,
        IReadOnlyList<uint>? ActiveRulesetIds = null);

    private sealed record QueryItemFilterStateDto(string RequestId);

    private sealed record ItemFilterStateResponseDto(string RequestId, ItemFilterState State);

    private sealed record ReloadItemFilterDto(string RequestId);

    private sealed record ReloadItemFilterResponseDto(string RequestId, bool Dispatched);

    private sealed record EvaluateWorldItemFilterDto(string RequestId, uint EntityId, bool LiveEvaluate = false);

    private sealed record EvaluateWorldItemFilterResponseDto(string RequestId, ItemFilterResult Result);

    private sealed record QueryCameraStateDto(string RequestId);

    private sealed record CameraStateResponseDto(string RequestId, CameraState State);

    private sealed record SetCameraZoomOverrideDto(float? ZoomOverride);

    // Phase 4 + 5 (1.2.0) Wire DTOs

    private sealed record ShowTooltipDto(TooltipContent Content, float ScreenX, float ScreenY);

    private sealed record HideTooltipDto();

    private sealed record ItemTooltipRuleDto(
        string Id,
        IReadOnlyList<TooltipSection> Sections,
        string? MetadataPrefix = null,
        string? BaseNameContains = null,
        uint? ItemClassId = null,
        ItemRarity? MinRarity = null,
        bool? RequireAltHeld = null,
        int Priority = 0);

    private sealed record RemoveRuleDto(string Id);

    private sealed record StashAffinityRouteRuleDto(
        string Id,
        ushort TargetTabIndex,
        string? MetadataPrefix = null,
        string? BaseNameContains = null,
        uint? ItemClassId = null,
        StashKind? StashKind = null,
        byte AffinityId = 0,
        bool ReplaceStockTargets = false,
        int Priority = 0);

    private sealed record SetPictureTextureDto(string WindowId, string ControlPath, string TexturePath, bool ResizeToImage = false);

    private sealed record SetComboBoxSelectedDto(string WindowId, string ControlPath, int SelectedIndex);

    private sealed record SelectTabDto(string WindowId, string ControlPath, int TabIndex);

    private sealed record CreateItemGridDto(string WindowId, string ParentControlPath, string GridId, UiItemGridOptions Options);

    private sealed record ServerGridItemDto(
        string ItemId,
        int Col,
        int Row,
        int Width = 1,
        int Height = 1,
        string? IconPath = null,
        int StackSize = 1,
        ItemRarity Rarity = ItemRarity.Normal,
        string? Title = null,
        IReadOnlyList<TooltipLine>? TooltipLines = null);

    private sealed record SetGridItemDto(string WindowId, string GridPath, ServerGridItemDto Item);

    private sealed record RemoveGridItemDto(string WindowId, string GridPath, string ItemId);

    private sealed record ClearGridDto(string WindowId, string GridPath);

    private sealed record SetGridClickSubscriptionDto(string WindowId, string GridPath, bool Enabled);

    private sealed record GridCellClickDto(
        string WindowId,
        string GridPath,
        int Col,
        int Row,
        string? ItemId = null,
        MouseButton Button = MouseButton.Left,
        KeyModifiers Modifiers = KeyModifiers.None);

    private sealed record PlayAudioExDto(string RequestId, string PathOrEvent, float Volume = 1.0f, bool Loop = false);

    private sealed record PlaySoundAtPositionDto(
        string RequestId,
        string PathOrEvent,
        float X,
        float Y,
        float Z,
        float Volume = 1.0f,
        float MaxDistance = 1200f,
        bool Loop = false);

    private sealed record PlayAudioExResponseDto(string RequestId, int ChannelHandle);

    private sealed record StopAudioDto(int ChannelHandle);

    private sealed record SetAudioPositionDto(int ChannelHandle, float X, float Y, float Z, float MaxDistance = 1200f);

    private sealed record SpawnClientObjectDto(
        string RequestId,
        string MetadataPath,
        float X,
        float Y,
        float Z = 0f,
        float RotationRadians = 0f,
        float Scale = 1.0f);

    private sealed record SpawnClientObjectResponseDto(string RequestId, uint ObjectId);

    private sealed record RemoveClientObjectDto(uint ObjectId);

    private sealed record SetExtendedU32StacksDto(bool Enabled, bool? FormatMillions = null);

    private sealed record SetEntityStackOverrideDto(uint EntityId, uint StackSize);

    private sealed record SetCompactSocketsDto(bool Enabled);
}
