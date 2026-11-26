namespace LocalExile.Client.Sdk.Engine;

/// <summary>
/// Immutable snapshot of the active 3.29 <c>FollowCamera</c> and <c>CameraController</c> state.
/// </summary>
/// <param name="IsValid"><c>true</c> when an active in-game camera is available.</param>
/// <param name="Position">Camera eye position in 3D world space.</param>
/// <param name="Target">Camera look-at target position in 3D world space.</param>
/// <param name="Zoom">Current smoothed zoom factor (<c>CameraController + 0x4a8</c>).</param>
/// <param name="ZoomOverride">Active zoom override (<c>CameraController + 0x4ac</c>); negative (<c>-1.0f</c>) when stock zoom is active.</param>
/// <param name="ZoomTarget">Current zoom target (<c>CameraController + 0x4b0</c>).</param>
/// <param name="Fov">Vertical field of view in radians.</param>
/// <param name="ViewportWidth">Render viewport width in pixels.</param>
/// <param name="ViewportHeight">Render viewport height in pixels.</param>
/// <param name="ViewProjectionMatrix">16-element row-major DirectX view-projection matrix (<c>Camera + 0x100</c>, <c>v_row * M</c>).</param>
public sealed record CameraState(
    bool IsValid,
    WorldPosition3D Position,
    WorldPosition3D Target,
    float Zoom,
    float ZoomOverride,
    float ZoomTarget,
    float Fov,
    float ViewportWidth,
    float ViewportHeight,
    IReadOnlyList<float> ViewProjectionMatrix)
{
    private static readonly float[] IdentityMatrix =
    [
        1f, 0f, 0f, 0f,
        0f, 1f, 0f, 0f,
        0f, 0f, 1f, 0f,
        0f, 0f, 0f, 1f,
    ];

    /// <summary>Default invalid camera snapshot returned when outside an area.</summary>
    public static CameraState Invalid { get; } = new(
        IsValid: false,
        Position: default,
        Target: default,
        Zoom: 0f,
        ZoomOverride: -1.0f,
        ZoomTarget: 1.0f,
        Fov: 0f,
        ViewportWidth: 0f,
        ViewportHeight: 0f,
        ViewProjectionMatrix: IdentityMatrix);

    /// <summary>
    /// Projects a 3D world point into 2D screen pixel coordinates using the row-major <see cref="ViewProjectionMatrix"/>
    /// matching PoE's native <c>Camera::WorldToScreen</c> (<c>v_row * M</c>).
    /// </summary>
    /// <param name="worldX">World X coordinate.</param>
    /// <param name="worldY">World Y coordinate.</param>
    /// <param name="worldZ">World Z coordinate.</param>
    /// <param name="screenX">Output screen X coordinate in pixels.</param>
    /// <param name="screenY">Output screen Y coordinate in pixels.</param>
    /// <returns><c>true</c> if the camera is valid and the point lies in front of the camera (<c>w &gt; 1e-6f</c>); otherwise <c>false</c>.</returns>
    public bool TryProjectWorldToScreen(float worldX, float worldY, float worldZ, out float screenX, out float screenY)
    {
        if (!IsValid || ViewProjectionMatrix is null || ViewProjectionMatrix.Count < 16 || ViewportWidth <= 0f || ViewportHeight <= 0f)
        {
            screenX = 0f;
            screenY = 0f;
            return false;
        }

        IReadOnlyList<float> m = ViewProjectionMatrix;
        float cx = (worldX * m[0]) + (worldY * m[4]) + (worldZ * m[8]) + m[12];
        float cy = (worldX * m[1]) + (worldY * m[5]) + (worldZ * m[9]) + m[13];
        float cw = (worldX * m[3]) + (worldY * m[7]) + (worldZ * m[11]) + m[15];

        if (cw <= 1e-6f)
        {
            screenX = 0f;
            screenY = 0f;
            return false;
        }

        float invW = 1.0f / cw;
        screenX = (ViewportWidth * 0.5f) * ((cx * invW) + 1.0f);
        screenY = (ViewportHeight * 0.5f) * (1.0f - (cy * invW));
        return true;
    }
}

/// <summary>
/// Exposes read-only camera state, world-to-screen projection, and per-mod zoom override control.
/// </summary>
public interface ICameraService
{
    /// <summary>Reads the current live in-game camera state snapshot.</summary>
    CameraState GetState();

    /// <summary>Projects a 3D world-space coordinate into 2D screen pixel coordinates.</summary>
    /// <param name="worldX">World X coordinate.</param>
    /// <param name="worldY">World Y coordinate.</param>
    /// <param name="worldZ">World Z coordinate.</param>
    /// <param name="screenX">Projected screen X coordinate in pixels.</param>
    /// <param name="screenY">Projected screen Y coordinate in pixels.</param>
    /// <returns><c>true</c> if the point lies in front of an active camera; otherwise <c>false</c>.</returns>
    bool TryWorldToScreen(float worldX, float worldY, float worldZ, out float screenX, out float screenY);

    /// <summary>Projects a <see cref="WorldPosition3D"/> into 2D screen pixel coordinates.</summary>
    bool TryWorldToScreen(WorldPosition3D worldPosition, out float screenX, out float screenY) =>
        TryWorldToScreen(worldPosition.X, worldPosition.Y, worldPosition.Z, out screenX, out screenY);

    /// <summary>
    /// Sets or clears a camera zoom override (<c>CameraController + 0x4ac</c>).
    /// Passing <c>null</c> (or a negative value) clears this mod's override and restores stock zoom (<c>-1.0f</c>).
    /// Per-mod overrides are automatically cleared when the owning mod unloads.
    /// </summary>
    /// <param name="zoomOverride">Desired zoom override value, or <c>null</c> to restore stock zoom.</param>
    void SetZoomOverride(float? zoomOverride);
}
