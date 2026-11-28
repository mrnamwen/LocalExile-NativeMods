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

    // Frames seen by Tick, and the frame the hover state was last read on: the state is read at most once a frame, and
    // only when something asks for it (a HoverChanged subscriber, or a read of the properties below). Reading it walks
    // the hovered control's parents and copies the hovered item, which every frame cost Windows players frame rate.
    private long _frame;
    private long _polledFrame = -1;

    /// <inheritdoc/>
    public IUiControl? HoveredControl { get { EnsureFresh(); return _hoveredControl; } }

    /// <inheritdoc/>
    public HoveredItemInfo? HoveredItem { get { EnsureFresh(); return _hoveredItem; } }

    /// <inheritdoc/>
    public CursorItemInfo? CursorItem { get { EnsureFresh(); return _cursorItem; } }

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

    /// <summary>Called once a frame on the game thread: polls the hover &amp; cursor state when something listens for
    /// <see cref="HoverChanged"/>, and raises it if anything changed.</summary>
    public void Tick()
    {
        _frame++;
        if (HoverChanged is not null)
        {
            PollAndNotify();
        }
    }

    private void EnsureFresh()
    {
        if (_polledFrame != _frame)
        {
            PollAndNotify();
        }
    }

    private void PollAndNotify()
    {
        _polledFrame = _frame;
        (nint nextControlHandle, HoveredItemInfo? nextItem, CursorItemInfo? nextCursor) = _bridge.GetHoverSnapshot();

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
