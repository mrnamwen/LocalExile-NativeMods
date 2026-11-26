using LocalExile.Client.Host.Native;
using LocalExile.Client.Sdk.Engine;

namespace LocalExile.Client.Host.Services;

/// <summary>
/// Manages custom Ctrl+Click stash affinity routes (<c>ui.stash_get_affinity_targets</c> <c>0xa83460</c>)
/// and stock affinity bitset extensions (<c>items.compute_affinity_bitset</c> <c>0x1589b20</c>).
/// </summary>
public sealed class StashAffinityService : IStashAffinityService, IDisposable
{
    private sealed record RouteEntry(string Id, long Sequence, int Priority, Func<StashAffinityItemContext, StashAffinityRoute?> Router);
    private sealed record StockRuleEntry(string Id, long Sequence, int Priority, Func<StashAffinityItemContext, byte?> Selector);

    private readonly INativeBridge _bridge;
    private readonly Lock _gate = new();
    private readonly List<RouteEntry> _routes = [];
    private readonly List<StockRuleEntry> _stockRules = [];
    private readonly IDisposable _nativeRouteSub;
    private readonly IDisposable _nativeBitsetSub;
    private long _nextSequence;
    private bool _disposed;

    /// <summary>Creates a new <see cref="StashAffinityService"/>.</summary>
    public StashAffinityService(INativeBridge bridge)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _nativeRouteSub = _bridge.RegisterStashAffinityRouteCallback(EvaluateRoutes);
        _nativeBitsetSub = _bridge.RegisterStashAffinityBitsetCallback(EvaluateStockAffinityMask);
    }

    /// <summary>Returns the count of currently registered custom stash routes.</summary>
    public int RouteCount
    {
        get { lock (_gate) { return _routes.Count; } }
    }

    /// <summary>Returns the count of currently registered stock affinity rules.</summary>
    public int StockRuleCount
    {
        get { lock (_gate) { return _stockRules.Count; } }
    }

    /// <inheritdoc/>
    public IDisposable RegisterRoute(string id, Func<StashAffinityItemContext, StashAffinityRoute?> router, int priority = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(router);

        string trimmedId = id.Trim();
        long seq = Interlocked.Increment(ref _nextSequence);
        var entry = new RouteEntry(trimmedId, seq, priority, router);
        lock (_gate)
        {
            if (_routes.Any(r => string.Equals(r.Id, trimmedId, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Stash affinity route '{trimmedId}' is already registered.");
            }

            _routes.Add(entry);
        }

        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                _routes.RemoveAll(r => r.Sequence == seq);
            }
        });
    }

    /// <inheritdoc/>
    public IDisposable RegisterStockAffinityRule(string id, Func<StashAffinityItemContext, byte?> affinitySelector, int priority = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(affinitySelector);

        string trimmedId = id.Trim();
        long seq = Interlocked.Increment(ref _nextSequence);
        var entry = new StockRuleEntry(trimmedId, seq, priority, affinitySelector);
        lock (_gate)
        {
            if (_stockRules.Any(r => string.Equals(r.Id, trimmedId, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Stock stash affinity rule '{trimmedId}' is already registered.");
            }

            _stockRules.Add(entry);
        }

        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                _stockRules.RemoveAll(r => r.Sequence == seq);
            }
        });
    }

    /// <inheritdoc/>
    public IReadOnlyList<StashAffinityRoute> EvaluateRoutes(StashAffinityItemContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_disposed)
        {
            return [];
        }

        RouteEntry[] snapshot;
        lock (_gate)
        {
            if (_routes.Count == 0)
            {
                return [];
            }

            snapshot = [.. _routes.OrderBy(r => r.Priority).ThenBy(r => r.Sequence)];
        }

        var results = new List<StashAffinityRoute>();
        foreach (RouteEntry entry in snapshot)
        {
            try
            {
                StashAffinityRoute? route = entry.Router(context);
                if (route is not null)
                {
                    results.Add(route);
                }
            }
            catch
            {
                // Fail-open so a faulting route predicate never breaks stock Ctrl+Click stash moves.
            }
        }

        return results;
    }

    /// <inheritdoc/>
    public uint EvaluateStockAffinityMask(StashAffinityItemContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_disposed)
        {
            return 0u;
        }

        StockRuleEntry[] snapshot;
        lock (_gate)
        {
            if (_stockRules.Count == 0)
            {
                return 0u;
            }

            snapshot = [.. _stockRules.OrderBy(r => r.Priority).ThenBy(r => r.Sequence)];
        }

        uint mask = 0u;
        foreach (StockRuleEntry entry in snapshot)
        {
            try
            {
                byte? bit = entry.Selector(context);
                if (bit.HasValue && bit.Value <= IStashAffinityService.MaxStockAffinityId)
                {
                    mask |= 1u << bit.Value;
                }
            }
            catch
            {
                // Fail-open on faulting affinity selector.
            }
        }

        return mask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _nativeRouteSub.Dispose();
        _nativeBitsetSub.Dispose();

        lock (_gate)
        {
            _routes.Clear();
            _stockRules.Clear();
        }
    }
}
