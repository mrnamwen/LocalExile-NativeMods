using System.Globalization;
using System.Text;
using LocalExile.Client.Host.Native;
using LocalExile.Client.Sdk.Engine;

namespace LocalExile.Client.Host.Services;

/// <summary>
/// Manages stock item tooltip augmentation (<c>ui.item_popup_build_sections</c>) and a NativeMods-owned
/// custom anchored tooltip overlay for mod controls and <see cref="IUiItemGrid"/>.
/// </summary>
public sealed class TooltipService : ITooltipService, IDisposable
{
    /// <summary>Virtual path of the NativeMods-owned custom tooltip overlay <c>.ui</c> layout.</summary>
    public const string OverlayVirtualPath = "Metadata/UI/Mods/NativeMods/CustomTooltipOverlay.ui";

    private const int MaxOverlayLines = 24;

    private sealed record AugmenterEntry(long Id, int Priority, Func<ItemTooltipContext, IReadOnlyList<TooltipSection>> Augmenter);
    private sealed record AttachedTooltipEntry(long Id, IUiControl Control, Func<TooltipContent?> ContentProvider, string OwnerModId);

    private readonly INativeBridge _bridge;
    private readonly UiService _ui;
    private readonly FileOverlayService _files;
    private readonly HoverService _hover;
    private readonly Lock _gate = new();
    private readonly List<AugmenterEntry> _augmenters = [];
    private readonly List<AttachedTooltipEntry> _attachedTooltips = [];
    private readonly IDisposable _overlayRegistration;
    private readonly IDisposable _nativeTooltipSub;
    private IUiWindow? _overlayWindow;
    private TooltipContent? _activeCustomTooltip;
    private (float X, float Y) _customTooltipPos;
    private string? _activeTooltipOwnerModId;
    private long _activeAttachedEntryId;
    private long _nextId;
    private bool _disposed;

    /// <summary>Creates a new <see cref="TooltipService"/>.</summary>
    public TooltipService(INativeBridge bridge, UiService ui, FileOverlayService files, HoverService hover)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _hover = hover ?? throw new ArgumentNullException(nameof(hover));

        _overlayRegistration = _files.RegisterVirtualTextFileForMod("nativemods.tooltips", OverlayVirtualPath, BuildTooltipOverlayUi(), priority: 100);
        _nativeTooltipSub = _bridge.RegisterItemTooltipBuildCallback(EvaluateItemTooltipAugmenters);
        _hover.HoverChanged += HandleHoverChanged;
    }

    /// <inheritdoc/>
    public bool IsCustomTooltipVisible
    {
        get
        {
            lock (_gate)
            {
                return _activeCustomTooltip is not null;
            }
        }
    }

    /// <inheritdoc/>
    public TooltipContent? ActiveCustomTooltip
    {
        get
        {
            lock (_gate)
            {
                return _activeCustomTooltip;
            }
        }
    }

    /// <inheritdoc/>
    public (float X, float Y) CustomTooltipPosition
    {
        get
        {
            lock (_gate)
            {
                return _customTooltipPos;
            }
        }
    }

    /// <summary>Gets the active custom tooltip overlay window (for diagnostics and tests).</summary>
    public IUiWindow? OverlayWindow => _overlayWindow;

    /// <inheritdoc/>
    public IDisposable RegisterItemTooltipAugmenter(Func<ItemTooltipContext, IReadOnlyList<TooltipSection>> augmenter, int priority = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(augmenter);

        long id = Interlocked.Increment(ref _nextId);
        var entry = new AugmenterEntry(id, priority, augmenter);
        lock (_gate)
        {
            _augmenters.Add(entry);
        }

        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                _augmenters.RemoveAll(e => e.Id == id);
            }
        });
    }

    /// <summary>Evaluates all registered stock item tooltip augmenters in priority order.</summary>
    public IReadOnlyList<TooltipSection> EvaluateItemTooltipAugmenters(ItemTooltipContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_disposed)
        {
            return [];
        }

        AugmenterEntry[] snapshot;
        lock (_gate)
        {
            if (_augmenters.Count == 0)
            {
                return [];
            }

            snapshot = [.. _augmenters.OrderBy(e => e.Priority).ThenBy(e => e.Id)];
        }

        var combined = new List<TooltipSection>();
        foreach (AugmenterEntry entry in snapshot)
        {
            try
            {
                IReadOnlyList<TooltipSection>? sections = entry.Augmenter(context);
                if (sections is not null)
                {
                    foreach (TooltipSection section in sections)
                    {
                        if (section is not null && section.Lines is { Count: > 0 })
                        {
                            combined.Add(section);
                        }
                    }
                }
            }
            catch
            {
                // Fail-open so a faulting mod augmenter never breaks stock item tooltips.
            }
        }

        return combined;
    }

    /// <inheritdoc/>
    public void ShowTooltip(TooltipContent content, float screenX, float screenY) =>
        ShowTooltipForMod("host", content, screenX, screenY, attachedEntryId: 0);

    internal void ShowTooltipForMod(string ownerModId, TooltipContent content, float screenX, float screenY, long attachedEntryId = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(content);

        lock (_gate)
        {
            _activeCustomTooltip = content;
            _customTooltipPos = (screenX, screenY);
            _activeTooltipOwnerModId = ownerModId;
            _activeAttachedEntryId = attachedEntryId;
        }

        EnsureOverlayMounted();
        SyncOverlayVisuals(content, screenX, screenY);
    }

    /// <inheritdoc/>
    public void HideTooltip()
    {
        lock (_gate)
        {
            _activeCustomTooltip = null;
            _activeTooltipOwnerModId = null;
            _activeAttachedEntryId = 0;
        }

        if (_overlayWindow is { IsOpen: true })
        {
            _overlayWindow.Visible = false;
        }
    }

    internal void HideTooltipForMod(string ownerModId)
    {
        bool shouldHide;
        lock (_gate)
        {
            shouldHide = _activeCustomTooltip is not null &&
                         string.Equals(_activeTooltipOwnerModId, ownerModId, StringComparison.OrdinalIgnoreCase);
        }

        if (shouldHide)
        {
            HideTooltip();
        }
    }

    /// <inheritdoc/>
    public IDisposable AttachTooltip(IUiControl control, Func<TooltipContent?> contentProvider) =>
        AttachTooltipForMod("host", control, contentProvider);

    internal IDisposable AttachTooltipForMod(string ownerModId, IUiControl control, Func<TooltipContent?> contentProvider)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(contentProvider);

        long id = Interlocked.Increment(ref _nextId);
        var entry = new AttachedTooltipEntry(id, control, contentProvider, ownerModId);
        lock (_gate)
        {
            _attachedTooltips.Add(entry);
        }

        return new ActionDisposable(() =>
        {
            bool hideActive;
            lock (_gate)
            {
                _attachedTooltips.RemoveAll(e => e.Id == id);
                hideActive = _activeAttachedEntryId == id;
            }

            if (hideActive)
            {
                HideTooltip();
            }
        });
    }

    /// <summary>
    /// Updates attached control tooltip display for a hovered control (invoked automatically by
    /// <see cref="HoverService.HoverChanged"/> and callable directly for managed controls / tests).
    /// </summary>
    public void NotifyControlHovered(IUiControl? hoveredControl, float? anchorX = null, float? anchorY = null)
    {
        if (_disposed)
        {
            return;
        }

        AttachedTooltipEntry? matched = null;
        if (hoveredControl is not null)
        {
            lock (_gate)
            {
                for (int i = _attachedTooltips.Count - 1; i >= 0; i--)
                {
                    AttachedTooltipEntry entry = _attachedTooltips[i];
                    if (MatchesControl(entry.Control, hoveredControl))
                    {
                        matched = entry;
                        break;
                    }
                }
            }
        }

        if (matched is null)
        {
            bool wasAttached;
            lock (_gate)
            {
                wasAttached = _activeAttachedEntryId != 0;
            }

            if (wasAttached)
            {
                HideTooltip();
            }

            return;
        }

        TooltipContent? content = null;
        try
        {
            content = matched.ContentProvider();
        }
        catch
        {
            content = null;
        }

        if (content is null)
        {
            HideTooltip();
            return;
        }

        float x = anchorX ?? (matched.Control.Rect.X + matched.Control.Rect.Width + 8f);
        float y = anchorY ?? matched.Control.Rect.Y;
        ShowTooltipForMod(matched.OwnerModId, content, x, y, matched.Id);
    }

    /// <summary>Hides and tears down the tooltip overlay on server disconnect.</summary>
    public void HandleDisconnected()
    {
        HideTooltip();
        TeardownOverlay();
    }

    private void HandleHoverChanged(HoverChangedEventArgs args) => NotifyControlHovered(args.Control);

    private static bool MatchesControl(IUiControl registered, IUiControl hovered)
    {
        if (ReferenceEquals(registered, hovered))
        {
            return true;
        }

        if (registered.NativeHandle != nint.Zero && registered.NativeHandle == hovered.NativeHandle)
        {
            return true;
        }

        return false;
    }

    private void EnsureOverlayMounted()
    {
        if (_disposed)
        {
            return;
        }

        if (_overlayWindow is null || !_overlayWindow.IsOpen)
        {
            _overlayWindow = _ui.Load(OverlayVirtualPath, "nativemods_custom_tooltip");
        }
    }

    private void SyncOverlayVisuals(TooltipContent content, float screenX, float screenY)
    {
        if (_overlayWindow is not { IsOpen: true })
        {
            return;
        }

        _overlayWindow.SetPosition(screenX, screenY);
        _overlayWindow.Visible = true;

        if (_overlayWindow.Find<ILabel>("tooltip_title") is { } titleLabel)
        {
            titleLabel.Text = content.Title;
            titleLabel.ForegroundColor = content.TitleColor ?? GetRarityColor(content.Rarity);
        }

        var flattened = new List<(string Text, string Color)>();
        foreach (TooltipSection section in content.Sections)
        {
            if (section.PrependSeparator && flattened.Count > 0 && flattened.Count < MaxOverlayLines)
            {
                flattened.Add(("----------------", "rgb(120, 110, 95)"));
            }

            foreach (TooltipLine line in section.Lines)
            {
                if (flattened.Count >= MaxOverlayLines)
                {
                    break;
                }

                flattened.Add((line.Text, line.Color ?? "Colours.White"));
            }
        }

        for (int i = 0; i < MaxOverlayLines; i++)
        {
            if (_overlayWindow.Find<ILabel>($"line_{i}") is { } lineLabel)
            {
                if (i < flattened.Count)
                {
                    lineLabel.Visible = true;
                    lineLabel.Text = flattened[i].Text;
                    lineLabel.ForegroundColor = flattened[i].Color;
                }
                else
                {
                    lineLabel.Visible = false;
                    lineLabel.Text = string.Empty;
                }
            }
        }

        float height = 40f + (flattened.Count * 20f);
        _overlayWindow.SetSize(340f, Math.Max(48f, height));
    }

    private void TeardownOverlay()
    {
        if (_overlayWindow is { IsOpen: true })
        {
            _overlayWindow.Close();
        }

        _overlayWindow = null;
    }

    private static string GetRarityColor(ItemRarity rarity) => rarity switch
    {
        ItemRarity.Magic => "rgb(136, 136, 255)",
        ItemRarity.Rare => "rgb(255, 255, 119)",
        ItemRarity.Unique => "rgb(175, 96, 37)",
        _ => "Colours.White",
    };

    private static string BuildTooltipOverlayUi()
    {
        var sb = new StringBuilder();
        sb.AppendLine("begin Frame");
        sb.AppendLine("  id = \"nativemods_custom_tooltip\"");
        sb.AppendLine("  size = 340, 120");
        sb.AppendLine("  visible = false");
        sb.AppendLine("  begin Label");
        sb.AppendLine("    id = \"tooltip_title\"");
        sb.AppendLine("    location = 10, 8");
        sb.AppendLine("    size = 320, 24");
        sb.AppendLine("    text = \"\"");
        sb.AppendLine("  end");
        for (int i = 0; i < MaxOverlayLines; i++)
        {
            int y = 36 + (i * 20);
            sb.AppendLine("  begin Label");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    id = \"line_{i}\"");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    location = 10, {y}");
            sb.AppendLine("    size = 320, 18");
            sb.AppendLine("    visible = false");
            sb.AppendLine("    text = \"\"");
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
        _hover.HoverChanged -= HandleHoverChanged;
        _nativeTooltipSub.Dispose();
        TeardownOverlay();
        _overlayRegistration.Dispose();

        lock (_gate)
        {
            _augmenters.Clear();
            _attachedTooltips.Clear();
            _activeCustomTooltip = null;
            _activeTooltipOwnerModId = null;
            _activeAttachedEntryId = 0;
        }
    }
}
