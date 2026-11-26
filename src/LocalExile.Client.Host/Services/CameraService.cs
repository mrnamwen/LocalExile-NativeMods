using LocalExile.Client.Host.Native;
using LocalExile.Client.Sdk.Engine;

namespace LocalExile.Client.Host.Services;

/// <summary>
/// Managed <see cref="ICameraService"/> providing live 3.29 camera snapshots, <c>WorldToScreen</c> projection,
/// and per-owner zoom override tracking so unloading a mod cleanly restores stock zoom (<c>-1.0f</c>).
/// </summary>
public sealed class CameraService : ICameraService, IDisposable
{
    /// <summary>Sentinel value written to <c>CameraController + 0x4ac</c> to disable zoom override and restore stock zoom.</summary>
    public const float StockZoomOverride = -1.0f;

    private const string HostOwnerKey = "__host__";

    private readonly INativeBridge _bridge;
    private readonly List<(string OwnerId, float Zoom)> _overrideStack = [];
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>Creates a new <see cref="CameraService"/> wired to <paramref name="bridge"/>.</summary>
    public CameraService(INativeBridge bridge)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    }

    /// <summary>Gets the active zoom override currently pushed to the native bridge (<c>null</c> when stock zoom is active).</summary>
    public float? ActiveZoomOverride
    {
        get
        {
            lock (_gate)
            {
                return _overrideStack.Count > 0 ? _overrideStack[^1].Zoom : null;
            }
        }
    }

    /// <inheritdoc/>
    public CameraState GetState() => _bridge.GetCameraState();

    /// <inheritdoc/>
    public bool TryWorldToScreen(float worldX, float worldY, float worldZ, out float screenX, out float screenY) =>
        _bridge.TryCameraWorldToScreen(worldX, worldY, worldZ, out screenX, out screenY);

    /// <inheritdoc/>
    public bool TryWorldToScreen(WorldPosition3D worldPosition, out float screenX, out float screenY) =>
        TryWorldToScreen(worldPosition.X, worldPosition.Y, worldPosition.Z, out screenX, out screenY);

    /// <inheritdoc/>
    public bool TryWorldToUiPosition(float worldX, float worldY, float worldZ, out float uiX, out float uiY)
    {
        if (!_bridge.TryCameraWorldToScreen(worldX, worldY, worldZ, out float screenX, out float screenY))
        {
            uiX = 0f;
            uiY = 0f;
            return false;
        }

        CameraState state = _bridge.GetCameraState();
        return state.TryScreenToUiPosition(screenX, screenY, out uiX, out uiY);
    }

    /// <inheritdoc/>
    public bool TryWorldToUiPosition(WorldPosition3D worldPosition, out float uiX, out float uiY) =>
        TryWorldToUiPosition(worldPosition.X, worldPosition.Y, worldPosition.Z, out uiX, out uiY);

    /// <inheritdoc/>
    public void SetZoomOverride(float? zoomOverride) => SetZoomOverrideForOwner(HostOwnerKey, zoomOverride);

    internal void SetZoomOverrideForOwner(string ownerId, float? zoomOverride)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        float valueToApply;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _overrideStack.RemoveAll(e => string.Equals(e.OwnerId, ownerId, StringComparison.OrdinalIgnoreCase));
            if (zoomOverride is float z && !float.IsNaN(z) && !float.IsInfinity(z) && z >= 0f)
            {
                _overrideStack.Add((ownerId, z));
            }

            valueToApply = _overrideStack.Count > 0 ? _overrideStack[^1].Zoom : StockZoomOverride;
        }

        _bridge.SetCameraZoomOverride(valueToApply);
    }

    internal void ClearZoomOverrideForOwner(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            return;
        }

        float valueToApply;
        bool hadEntry;
        lock (_gate)
        {
            int removed = _overrideStack.RemoveAll(e => string.Equals(e.OwnerId, ownerId, StringComparison.OrdinalIgnoreCase));
            hadEntry = removed > 0;
            valueToApply = _overrideStack.Count > 0 ? _overrideStack[^1].Zoom : StockZoomOverride;
        }

        if (hadEntry)
        {
            _bridge.SetCameraZoomOverride(valueToApply);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        bool hadAny;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            hadAny = _overrideStack.Count > 0;
            _overrideStack.Clear();
        }

        if (hadAny)
        {
            try
            {
                _bridge.SetCameraZoomOverride(StockZoomOverride);
            }
            catch
            {
                // Ignore teardown errors if the native bridge is already shutting down.
            }
        }
    }
}
