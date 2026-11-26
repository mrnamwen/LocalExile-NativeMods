using LocalExile.Client.Host.Native;
using LocalExile.Client.Sdk.Engine;

namespace LocalExile.Client.Host.Services;

/// <summary>
/// Managed <see cref="IStackExtensionService"/> controlling Phase 5 (<c>1.2.0</c>) extended 32-bit stack
/// deserialization, millions stack formatting, and sideband per-entity stack overrides with per-owner tracking
/// so mod unload and server disconnect cleanly restore stock behavior.
/// </summary>
public sealed class StackExtensionService : IStackExtensionService, IDisposable
{
    private const string HostOwnerKey = "__host__";

    private readonly INativeBridge _bridge;
    private readonly HashSet<string> _u32StackOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _formatMillionsOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<uint>> _overrideOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, uint> _entityOverrides = [];
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>Creates a new <see cref="StackExtensionService"/> wired to <paramref name="bridge"/>.</summary>
    public StackExtensionService(INativeBridge bridge)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    }

    /// <inheritdoc/>
    public bool IsExtendedU32StacksEnabled
    {
        get
        {
            lock (_gate)
            {
                return _u32StackOwners.Count > 0;
            }
        }
    }

    /// <inheritdoc/>
    public bool IsFormatMillionsEnabled
    {
        get
        {
            lock (_gate)
            {
                return _formatMillionsOwners.Count > 0;
            }
        }
    }

    /// <summary>Snapshot of active sideband per-entity stack size overrides.</summary>
    public IReadOnlyDictionary<uint, uint> ActiveEntityOverrides
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<uint, uint>(_entityOverrides);
            }
        }
    }

    /// <inheritdoc/>
    public void SetExtendedU32StacksEnabled(bool enabled) =>
        SetExtendedU32StacksEnabledForOwner(HostOwnerKey, enabled);

    /// <inheritdoc/>
    public void SetFormatMillionsEnabled(bool enabled) =>
        SetFormatMillionsEnabledForOwner(HostOwnerKey, enabled);

    /// <inheritdoc/>
    public bool SetEntityStackOverride(uint entityId, uint stackSize) =>
        SetEntityStackOverrideForOwner(HostOwnerKey, entityId, stackSize);

    internal void SetExtendedU32StacksEnabledForOwner(string ownerId, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        bool nextState;
        bool shouldPush;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            bool prevState = _u32StackOwners.Count > 0;
            if (enabled)
            {
                _u32StackOwners.Add(ownerId);
            }
            else
            {
                _u32StackOwners.Remove(ownerId);
            }

            nextState = _u32StackOwners.Count > 0;
            shouldPush = prevState != nextState || enabled;
        }

        if (shouldPush)
        {
            _bridge.SetExtendedU32StacksEnabled(nextState);
        }
    }

    internal void SetFormatMillionsEnabledForOwner(string ownerId, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        bool nextState;
        bool shouldPush;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            bool prevState = _formatMillionsOwners.Count > 0;
            if (enabled)
            {
                _formatMillionsOwners.Add(ownerId);
            }
            else
            {
                _formatMillionsOwners.Remove(ownerId);
            }

            nextState = _formatMillionsOwners.Count > 0;
            shouldPush = prevState != nextState || enabled;
        }

        if (shouldPush)
        {
            _bridge.SetFormatMillionsEnabled(nextState);
        }
    }

    internal bool SetEntityStackOverrideForOwner(string ownerId, uint entityId, uint stackSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (entityId == 0)
        {
            return false;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }
        }

        bool applied = _bridge.SetEntityStackOverride(entityId, stackSize);
        if (applied)
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    _entityOverrides[entityId] = stackSize;
                    if (!_overrideOwners.TryGetValue(ownerId, out HashSet<uint>? owned))
                    {
                        owned = [];
                        _overrideOwners[ownerId] = owned;
                    }

                    owned.Add(entityId);
                }
            }
        }

        return applied;
    }

    internal void ClearForOwner(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            return;
        }

        bool pushU32 = false;
        bool nextU32 = false;
        bool pushMillions = false;
        bool nextMillions = false;

        lock (_gate)
        {
            bool prevU32 = _u32StackOwners.Count > 0;
            if (_u32StackOwners.Remove(ownerId))
            {
                nextU32 = _u32StackOwners.Count > 0;
                pushU32 = prevU32 != nextU32;
            }

            bool prevMillions = _formatMillionsOwners.Count > 0;
            if (_formatMillionsOwners.Remove(ownerId))
            {
                nextMillions = _formatMillionsOwners.Count > 0;
                pushMillions = prevMillions != nextMillions;
            }

            if (_overrideOwners.Remove(ownerId, out HashSet<uint>? ownedEntities))
            {
                foreach (uint entityId in ownedEntities)
                {
                    if (!_overrideOwners.Values.Any(set => set.Contains(entityId)))
                    {
                        _entityOverrides.Remove(entityId);
                    }
                }
            }
        }

        if (pushU32)
        {
            TryPushU32(nextU32);
        }

        if (pushMillions)
        {
            TryPushMillions(nextMillions);
        }
    }

    /// <summary>
    /// Resets session-scoped extended <c>u32</c> stack flags and sideband overrides on disconnect
    /// so reconnecting to a vanilla server never leaves extended wire decoding active.
    /// </summary>
    public void HandleDisconnected()
    {
        bool hadU32;
        bool hadMillions;
        lock (_gate)
        {
            hadU32 = _u32StackOwners.Count > 0;
            hadMillions = _formatMillionsOwners.Count > 0;
            _u32StackOwners.Clear();
            _formatMillionsOwners.Clear();
            _overrideOwners.Clear();
            _entityOverrides.Clear();
        }

        if (hadU32)
        {
            TryPushU32(false);
        }

        if (hadMillions)
        {
            TryPushMillions(false);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        bool hadU32;
        bool hadMillions;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            hadU32 = _u32StackOwners.Count > 0;
            hadMillions = _formatMillionsOwners.Count > 0;
            _u32StackOwners.Clear();
            _formatMillionsOwners.Clear();
            _overrideOwners.Clear();
            _entityOverrides.Clear();
        }

        if (hadU32)
        {
            TryPushU32(false);
        }

        if (hadMillions)
        {
            TryPushMillions(false);
        }
    }

    private void TryPushU32(bool enabled)
    {
        try
        {
            _bridge.SetExtendedU32StacksEnabled(enabled);
        }
        catch
        {
            // Ignore teardown errors if the native bridge is already shutting down.
        }
    }

    private void TryPushMillions(bool enabled)
    {
        try
        {
            _bridge.SetFormatMillionsEnabled(enabled);
        }
        catch
        {
            // Ignore teardown errors if the native bridge is already shutting down.
        }
    }
}
