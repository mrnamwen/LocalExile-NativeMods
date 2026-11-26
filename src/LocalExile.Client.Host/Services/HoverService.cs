using LocalExile.Client.Host.Native;
using LocalExile.Client.Sdk.Engine;

namespace LocalExile.Client.Host.Services;

/// <summary>
/// Tracks the hovered native UI control, hovered inventory/stash item, and cursor-held/use-mode item.
/// </summary>
public sealed class HoverService : IHoverService
{
    private readonly INativeBridge _bridge;
    private readonly UiService _ui;
    private nint _lastControlHandle;
    private IUiControl? _hoveredControl;
    private HoveredItemInfo? _hoveredItem;
    private CursorItemInfo? _cursorItem;

    /// <summary>Creates a new <see cref="HoverService"/>.</summary>
    public HoverService(INativeBridge bridge, UiService ui)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
    }

    /// <inheritdoc/>
    public IUiControl? HoveredControl => _hoveredControl;

    /// <inheritdoc/>
    public HoveredItemInfo? HoveredItem => _hoveredItem;

    /// <inheritdoc/>
    public CursorItemInfo? CursorItem => _cursorItem;

    /// <inheritdoc/>
    public event Action<HoverChangedEventArgs>? HoverChanged;

    /// <inheritdoc/>
    public HoveredItemInfo? RefreshHoveredItem()
    {
        PollAndNotify();
        return _hoveredItem;
    }

    /// <inheritdoc/>
    public CursorItemInfo? RefreshCursorItem()
    {
        PollAndNotify();
        return _cursorItem;
    }

    /// <summary>Polls native hover &amp; cursor state on the game thread and raises <see cref="HoverChanged"/> if anything changed.</summary>
    public void Tick() => PollAndNotify();

    private void PollAndNotify()
    {
        nint nextControlHandle = _bridge.GetHoveredControlHandle();
        HoveredItemInfo? nextItem = _bridge.GetHoveredItem();
        CursorItemInfo? nextCursor = _bridge.GetCursorItem();

        bool controlChanged = nextControlHandle != _lastControlHandle;
        bool itemChanged = !Equals(nextItem, _hoveredItem);
        bool cursorChanged = !Equals(nextCursor, _cursorItem);

        if (!controlChanged && !itemChanged && !cursorChanged)
        {
            return;
        }

        if (controlChanged)
        {
            _lastControlHandle = nextControlHandle;
            _hoveredControl = nextControlHandle != nint.Zero ? _ui.WrapControl(nextControlHandle) : null;
        }

        _hoveredItem = nextItem;
        _cursorItem = nextCursor;

        HoverChanged?.Invoke(new HoverChangedEventArgs(_hoveredControl, _hoveredItem, _cursorItem));
    }
}
