using LocalExile.Client.Host.Native;
using LocalExile.Client.Sdk.Engine;

namespace LocalExile.Client.Host.Services;

/// <summary>
/// Managed <see cref="IItemFilterService"/> bridging active filter metadata, cached world-item visibility/style,
/// on-demand <c>ItemFilter::Match</c> evaluation, and native <c>ItemFilterManager::NotifyLoaded</c> reload events.
/// </summary>
public sealed class ItemFilterService : IItemFilterService, IDisposable
{
    private readonly INativeBridge _bridge;
    private readonly WorldService _world;
    private readonly IDisposable _reloadSub;
    private readonly Action<string, string, string>? _log;
    private bool _disposed;

    /// <summary>Creates a new <see cref="ItemFilterService"/> wired to <paramref name="bridge"/> and <paramref name="world"/>.</summary>
    public ItemFilterService(INativeBridge bridge, WorldService world, Action<string, string, string>? log = null)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _log = log;
        _reloadSub = _bridge.RegisterItemFilterReloadCallback(HandleNativeFilterReloaded);
    }

    /// <inheritdoc/>
    public event Action<ItemFilterReloadedEventArgs>? FilterReloaded;

    /// <inheritdoc/>
    public ItemFilterState GetState() => _bridge.GetItemFilterState();

    /// <inheritdoc/>
    public bool Reload() => _bridge.ReloadItemFilter();

    /// <inheritdoc/>
    public bool IsWorldItemVisible(uint entityId)
    {
        ItemFilterResult result = _bridge.GetWorldItemFilterResult(entityId);
        return result.Evaluated && result.IsVisible;
    }

    /// <inheritdoc/>
    public ItemFilterResult GetWorldItemResult(uint entityId) => _bridge.GetWorldItemFilterResult(entityId);

    /// <inheritdoc/>
    public IReadOnlyList<WorldEntitySnapshot> GetVisibleGroundItems(float? maxDistance = null) =>
        _world.GetWorldItems(filterVisibleOnly: true, maxDistance: maxDistance);

    /// <inheritdoc/>
    public ItemFilterResult EvaluateItem(uint entityId) => _bridge.EvaluateItemFilter(entityId);

    private void HandleNativeFilterReloaded(bool succeeded, string statusMessage, ItemFilterState? providedState)
    {
        if (_disposed)
        {
            return;
        }

        Action<ItemFilterReloadedEventArgs>? handlers = FilterReloaded;
        if (handlers is null)
        {
            return;
        }

        ItemFilterState state = providedState ?? _bridge.GetItemFilterState();
        var args = new ItemFilterReloadedEventArgs(succeeded, statusMessage ?? string.Empty, state);
        foreach (Delegate d in handlers.GetInvocationList())
        {
            try
            {
                ((Action<ItemFilterReloadedEventArgs>)d)(args);
            }
            catch (Exception ex)
            {
                _log?.Invoke("ERROR", "itemfilter", $"FilterReloaded handler failed: {ex.Message}");
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _reloadSub.Dispose();
    }
}
