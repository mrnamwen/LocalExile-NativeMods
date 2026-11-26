using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Protocol;

namespace LocalExile.Client.Host.Services;

/// <summary>
/// Client-side dispatcher for Tier 1 <c>ServerBridge 0.3.0</c> commands arriving on <see cref="MessageCodec.SystemChannel"/>.
/// Wires server-only single-<c>.cs</c> mods directly to <see cref="ScreensService"/>, <see cref="InputService"/>,
/// <see cref="HoverService"/>, and <see cref="ToastService"/> on the game thread, and tears down all server-owned
/// client resources cleanly on session disconnect or host disposal.
/// </summary>
public sealed class ServerBridgeTier1Handler : IDisposable
{
    private const string ServerBridgeModOwner = "server-bridge";

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
    private readonly Action<string, string, string>? _log;
    private readonly Dictionary<(ScreenSlot Slot, string Id), ISlotAttachment> _slotButtons = new(SlotButtonKeyComparer.Instance);
    private readonly Dictionary<string, IDisposable> _hotkeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDisposable> _channelSubs = [];
    private readonly Lock _gate = new();
    private int _screenSubRefCount;
    private int _hoverSubRefCount;
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

    /// <summary>Creates and wires a new <see cref="ServerBridgeTier1Handler"/> onto <paramref name="systemChannel"/>.</summary>
    public ServerBridgeTier1Handler(
        INetChannel systemChannel,
        IModScheduler scheduler,
        ScreensService screens,
        InputService input,
        HoverService hover,
        ToastService toasts,
        Action<string, string, string>? log = null)
    {
        _systemChannel = systemChannel ?? throw new ArgumentNullException(nameof(systemChannel));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _screens = screens ?? throw new ArgumentNullException(nameof(screens));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _hover = hover ?? throw new ArgumentNullException(nameof(hover));
        _toasts = toasts ?? throw new ArgumentNullException(nameof(toasts));
        _log = log;

        _channelSubs.Add(_systemChannel.On<RegisterSlotButtonDto>(HandleRegisterSlotButton, MsgRegisterSlotButton));
        _channelSubs.Add(_systemChannel.On<RemoveSlotButtonDto>(HandleRemoveSlotButton, MsgRemoveSlotButton));
        _channelSubs.Add(_systemChannel.On<ShowToastDto>(HandleShowToast, MsgShowToast));
        _channelSubs.Add(_systemChannel.On<RegisterHotkeyDto>(HandleRegisterHotkey, MsgRegisterHotkey));
        _channelSubs.Add(_systemChannel.On<UnregisterHotkeyDto>(HandleUnregisterHotkey, MsgUnregisterHotkey));
        _channelSubs.Add(_systemChannel.On<QueryScreenStateDto>(HandleQueryScreenState, MsgQueryScreenState));
        _channelSubs.Add(_systemChannel.On<SetSubscriptionDto>(HandleSetScreenSubscription, MsgSetScreenSubscription));
        _channelSubs.Add(_systemChannel.On<QueryHoverStateDto>(HandleQueryHoverState, MsgQueryHoverState));
        _channelSubs.Add(_systemChannel.On<SetSubscriptionDto>(HandleSetHoverSubscription, MsgSetHoverSubscription));
    }

    /// <summary>
    /// Cleans up all server-registered slot buttons, hotkeys, toasts, and opt-in screen/hover subscriptions
    /// when the client disconnects from an instance server.
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

    private ScreenStateSnapshotDto CaptureScreenSnapshot() =>
        new(_screens.CurrentPreGameScreen, _screens.ActivePanels, _screens.ActiveStashTab);

    private static HoverStateSnapshotDto CaptureHoverSnapshot(IUiControl? control, HoveredItemInfo? item, CursorItemInfo? cursor)
    {
        HoveredItemSnapshotDto? itemDto = item is null
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
    // Wire DTOs (JSON schema matching LocalExile.Client.ServerBridge Tier 1)
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
}
