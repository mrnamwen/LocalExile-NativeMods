using System.Globalization;
using System.Text;
using LocalExile.Client.Sdk.Engine;

namespace LocalExile.Client.Host.Services;

/// <summary>
/// NativeMods-owned HUD toast notification overlay anchored to <see cref="ScreenSlot.HudTopRight"/>.
/// Supports queued stacking toasts, severity styling, game-thread auto-dismissal, and clean teardown.
/// </summary>
public sealed class ToastService : IToastService, IDisposable
{
    /// <summary>Virtual path of the NativeMods-owned toast overlay <c>.ui</c> layout.</summary>
    public const string OverlayVirtualPath = "Metadata/UI/Mods/NativeMods/ToastOverlay.ui";

    private const int MaxSupportedSlots = 8;

    private sealed class ActiveToastState
    {
        public required ulong Id { get; init; }
        public required string Title { get; init; }
        public required string Message { get; init; }
        public required ToastSeverity Severity { get; init; }
        public required TimeSpan Duration { get; init; }
        public required string OwnerModId { get; init; }
        public float RemainingSeconds { get; set; }

        public ToastMessage ToSnapshot() =>
            new(Id, Title, Message, Severity, Duration, RemainingSeconds, OwnerModId);
    }

    private readonly UiService _ui;
    private readonly FileOverlayService _files;
    private readonly ScreensService _screens;
    private readonly Lock _gate = new();
    private readonly List<ActiveToastState> _active = [];
    private readonly Queue<ActiveToastState> _queue = new();
    private readonly IDisposable _overlayRegistration;
    private ISlotAttachment? _slotAttachment;
    private IUiWindow? _fallbackWindow;
    private long _nextToastId;
    private int _maxVisibleToasts = 5;
    private TimeSpan _defaultDuration = TimeSpan.FromSeconds(4);
    private bool _disposed;

    /// <summary>Creates a new <see cref="ToastService"/>.</summary>
    public ToastService(UiService ui, FileOverlayService files, ScreensService screens)
    {
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _screens = screens ?? throw new ArgumentNullException(nameof(screens));
        _overlayRegistration = _files.RegisterVirtualTextFileForMod("nativemods.toasts", OverlayVirtualPath, BuildToastOverlayUi(), priority: 100);
    }

    /// <inheritdoc/>
    public IReadOnlyList<ToastMessage> ActiveToasts
    {
        get
        {
            lock (_gate)
            {
                return [.. _active.Select(t => t.ToSnapshot())];
            }
        }
    }

    /// <inheritdoc/>
    public int QueuedCount
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count;
            }
        }
    }

    /// <inheritdoc/>
    public int MaxVisibleToasts
    {
        get => Volatile.Read(ref _maxVisibleToasts);
        set
        {
            int clamped = Math.Clamp(value, 1, MaxSupportedSlots);
            Volatile.Write(ref _maxVisibleToasts, clamped);
            lock (_gate)
            {
                PromoteQueuedLocked();
            }

            SyncOverlayVisuals();
        }
    }

    /// <inheritdoc/>
    public TimeSpan DefaultDuration
    {
        get { lock (_gate) { return _defaultDuration; } }
        set
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Default toast duration must be positive.");
            }

            lock (_gate)
            {
                _defaultDuration = value;
            }
        }
    }

    /// <inheritdoc/>
    public ulong Show(string title, string message, ToastSeverity severity = ToastSeverity.Info, TimeSpan? duration = null) =>
        ShowForMod("host", title, message, severity, duration);

    internal ulong ShowForMod(string ownerModId, string title, string message, ToastSeverity severity = ToastSeverity.Info, TimeSpan? duration = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        TimeSpan resolvedDuration = duration.HasValue && duration.Value > TimeSpan.Zero ? duration.Value : DefaultDuration;
        ulong id = (ulong)Interlocked.Increment(ref _nextToastId);

        var toast = new ActiveToastState
        {
            Id = id,
            Title = title ?? string.Empty,
            Message = message ?? string.Empty,
            Severity = severity,
            Duration = resolvedDuration,
            RemainingSeconds = (float)resolvedDuration.TotalSeconds,
            OwnerModId = string.IsNullOrWhiteSpace(ownerModId) ? "host" : ownerModId,
        };

        lock (_gate)
        {
            if (_active.Count < Volatile.Read(ref _maxVisibleToasts))
            {
                _active.Add(toast);
            }
            else
            {
                _queue.Enqueue(toast);
            }
        }

        EnsureOverlayMounted();
        SyncOverlayVisuals();
        return id;
    }

    /// <inheritdoc/>
    public bool Dismiss(ulong toastId)
    {
        bool removed = false;
        lock (_gate)
        {
            int idx = _active.FindIndex(t => t.Id == toastId);
            if (idx >= 0)
            {
                _active.RemoveAt(idx);
                removed = true;
                PromoteQueuedLocked();
            }
            else if (_queue.Count > 0)
            {
                int count = _queue.Count;
                for (int i = 0; i < count; i++)
                {
                    ActiveToastState item = _queue.Dequeue();
                    if (item.Id == toastId)
                    {
                        removed = true;
                    }
                    else
                    {
                        _queue.Enqueue(item);
                    }
                }
            }
        }

        if (removed)
        {
            SyncOverlayVisuals();
        }

        return removed;
    }

    /// <inheritdoc/>
    public void Clear()
    {
        lock (_gate)
        {
            _active.Clear();
            _queue.Clear();
        }

        SyncOverlayVisuals();
    }

    /// <summary>Dismisses all active and queued toasts created by <paramref name="ownerModId"/>.</summary>
    public void ClearForMod(string ownerModId)
    {
        if (string.IsNullOrWhiteSpace(ownerModId))
        {
            return;
        }

        bool changed = false;
        lock (_gate)
        {
            if (_active.RemoveAll(t => string.Equals(t.OwnerModId, ownerModId, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                changed = true;
            }

            if (_queue.Count > 0)
            {
                int count = _queue.Count;
                for (int i = 0; i < count; i++)
                {
                    ActiveToastState item = _queue.Dequeue();
                    if (string.Equals(item.OwnerModId, ownerModId, StringComparison.OrdinalIgnoreCase))
                    {
                        changed = true;
                    }
                    else
                    {
                        _queue.Enqueue(item);
                    }
                }
            }

            if (changed)
            {
                PromoteQueuedLocked();
            }
        }

        if (changed)
        {
            SyncOverlayVisuals();
        }
    }

    /// <summary>Tears down active toasts and detaches the overlay on server disconnect.</summary>
    public void HandleDisconnected()
    {
        Clear();
        TeardownOverlay();
    }

    /// <summary>Advances toast countdown timers on the game thread and promotes queued toasts as active ones expire.</summary>
    public void Tick(float deltaSeconds)
    {
        if (_disposed || deltaSeconds <= 0f)
        {
            return;
        }

        bool changed = false;
        lock (_gate)
        {
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                _active[i].RemainingSeconds -= deltaSeconds;
                if (_active[i].RemainingSeconds <= 0f)
                {
                    _active.RemoveAt(i);
                    changed = true;
                }
            }

            if (changed)
            {
                PromoteQueuedLocked();
            }
        }

        if (changed)
        {
            SyncOverlayVisuals();
        }
    }

    /// <summary>Gets the active overlay UI control (for diagnostics and tests).</summary>
    public IUiControl? OverlayControl => _slotAttachment?.Control ?? _fallbackWindow;

    private void PromoteQueuedLocked()
    {
        int max = Volatile.Read(ref _maxVisibleToasts);
        while (_active.Count < max && _queue.Count > 0)
        {
            _active.Add(_queue.Dequeue());
        }
    }

    private void EnsureOverlayMounted()
    {
        if (_disposed)
        {
            return;
        }

        if (_slotAttachment is null)
        {
            _slotAttachment = _screens.RegisterSlot(
                ScreenSlot.HudTopRight,
                "nativemods_toasts",
                OverlayVirtualPath,
                new SlotAttachmentOptions(Order: -100, Width: 360f, Height: 440f, Spacing: 8f));
        }

        if (_slotAttachment.Control is null && (_fallbackWindow is null || !_fallbackWindow.IsOpen))
        {
            _fallbackWindow = _ui.Load(OverlayVirtualPath, "nativemods_toasts_overlay");
        }
        else if (_slotAttachment.Control is not null && _fallbackWindow is { IsOpen: true })
        {
            _fallbackWindow.Close();
            _fallbackWindow = null;
        }
    }

    private void SyncOverlayVisuals()
    {
        if (_disposed)
        {
            return;
        }

        IUiControl? root = OverlayControl;
        if (root is null)
        {
            return;
        }

        ToastMessage[] snapshot = ActiveToasts.ToArray();
        root.Visible = snapshot.Length > 0;

        for (int i = 0; i < MaxSupportedSlots; i++)
        {
            IUiControl? card = root.Find($"toast_{i}");
            if (card is null)
            {
                continue;
            }

            if (i < snapshot.Length)
            {
                ToastMessage toast = snapshot[i];
                card.Visible = true;
                card.SetPosition(0f, i * 68f);
                if (card.Find<ILabel>("title") is { } titleLabel)
                {
                    titleLabel.Text = toast.Title;
                    titleLabel.ForegroundColor = GetSeverityColor(toast.Severity);
                }

                if (card.Find<ILabel>("body") is { } bodyLabel)
                {
                    bodyLabel.Text = toast.Message;
                }
            }
            else
            {
                card.Visible = false;
            }
        }
    }

    private void TeardownOverlay()
    {
        _slotAttachment?.Dispose();
        _slotAttachment = null;
        if (_fallbackWindow is { IsOpen: true })
        {
            _fallbackWindow.Close();
        }

        _fallbackWindow = null;
    }

    private static string GetSeverityColor(ToastSeverity severity) => severity switch
    {
        ToastSeverity.Success => "rgb(130, 230, 140)",
        ToastSeverity.Warning => "rgb(255, 200, 90)",
        ToastSeverity.Error => "rgb(245, 110, 110)",
        _ => "Colours.White",
    };

    private static string BuildToastOverlayUi()
    {
        var sb = new StringBuilder();
        sb.AppendLine("begin Frame");
        sb.AppendLine("  id = \"nativemods_toasts_root\"");
        sb.AppendLine("  size = 360, 544");
        sb.AppendLine("  visible = false");
        for (int i = 0; i < MaxSupportedSlots; i++)
        {
            sb.AppendLine("  begin Frame");
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    id = \"toast_{i}\""));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    location = 0, {i * 68}"));
            sb.AppendLine("    size = 360, 62");
            sb.AppendLine("    visible = false");
            sb.AppendLine("    begin Label");
            sb.AppendLine("      id = \"title\"");
            sb.AppendLine("      location = 10, 6");
            sb.AppendLine("      size = 340, 22");
            sb.AppendLine("      text = \"\"");
            sb.AppendLine("    end");
            sb.AppendLine("    begin Label");
            sb.AppendLine("      id = \"body\"");
            sb.AppendLine("      location = 10, 30");
            sb.AppendLine("      size = 340, 26");
            sb.AppendLine("      text = \"\"");
            sb.AppendLine("    end");
            sb.AppendLine("  end");
        }

        sb.AppendLine("end");
        return sb.ToString();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Clear();
        TeardownOverlay();
        _overlayRegistration.Dispose();
    }
}
