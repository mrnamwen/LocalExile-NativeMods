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
    private readonly Action<string, string, string>? _log;
    private readonly Dictionary<(ScreenSlot Slot, string Id), ISlotAttachment> _slotButtons = new(SlotButtonKeyComparer.Instance);
    private readonly Dictionary<string, IDisposable> _hotkeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDisposable> _channelSubs = [];
    private readonly Lock _gate = new();
    private int _screenSubRefCount;
    private int _hoverSubRefCount;
    private int _areaSubRefCount;
    private int _filterSubRefCount;
    private float? _activeCameraZoomOverride;
    private bool _disposed;

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
}
