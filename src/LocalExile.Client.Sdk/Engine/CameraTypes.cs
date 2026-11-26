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

    /// <summary>Path of Exile's canonical virtual UI coordinate space height (1600 virtual units high).</summary>
    public const float VirtualUiHeight = 1600.0f;

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

    /// <summary>
    /// Converts a viewport pixel coordinate into Path of Exile's 1600-high virtual UI coordinate space
    /// used by <see cref="IUiControl.SetPosition"/>.
    /// </summary>
    public bool TryScreenToUiPosition(float screenX, float screenY, out float uiX, out float uiY)
    {
        if (ViewportHeight <= 0f || float.IsNaN(ViewportHeight) || float.IsInfinity(ViewportHeight))
        {
            uiX = 0f;
            uiY = 0f;
            return false;
        }

        float scale = VirtualUiHeight / ViewportHeight;
        uiX = screenX * scale;
        uiY = screenY * scale;
        return true;
    }

    /// <summary>
    /// Projects a 3D world point directly into Path of Exile's 1600-high virtual UI coordinate space
    /// so 3D encounter markers align accurately with <see cref="IUiControl.SetPosition"/>.
    /// </summary>
    public bool TryProjectWorldToUi(float worldX, float worldY, float worldZ, out float uiX, out float uiY)
    {
        if (!TryProjectWorldToScreen(worldX, worldY, worldZ, out float screenX, out float screenY))
        {
            uiX = 0f;
            uiY = 0f;
            return false;
        }

        return TryScreenToUiPosition(screenX, screenY, out uiX, out uiY);
    }
}

/// <summary>
/// Exposes read-only camera state, world-to-screen projection, world-to-UI coordinate conversion, and per-mod zoom override control.
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
    /// Projects a 3D world-space coordinate into Path of Exile's 1600-high virtual UI coordinate space
    /// so 3D encounter markers align pixel-accurately with <see cref="IUiControl.SetPosition"/>.
    /// </summary>
    /// <param name="worldX">World X coordinate.</param>
    /// <param name="worldY">World Y coordinate.</param>
    /// <param name="worldZ">World Z coordinate.</param>
    /// <param name="uiX">Projected X coordinate in virtual UI units (scaled by <c>1600 / ViewportHeight</c>).</param>
    /// <param name="uiY">Projected Y coordinate in virtual UI units (<c>[0, 1600]</c> when on-screen).</param>
    /// <returns><c>true</c> if the point lies in front of an active camera with a valid viewport; otherwise <c>false</c>.</returns>
    bool TryWorldToUiPosition(float worldX, float worldY, float worldZ, out float uiX, out float uiY)
    {
        if (!TryWorldToScreen(worldX, worldY, worldZ, out float screenX, out float screenY))
        {
            uiX = 0f;
            uiY = 0f;
            return false;
        }

        CameraState state = GetState();
        return state.TryScreenToUiPosition(screenX, screenY, out uiX, out uiY);
    }

    /// <summary>Projects a <see cref="WorldPosition3D"/> into Path of Exile's 1600-high virtual UI coordinate space.</summary>
    bool TryWorldToUiPosition(WorldPosition3D worldPosition, out float uiX, out float uiY) =>
        TryWorldToUiPosition(worldPosition.X, worldPosition.Y, worldPosition.Z, out uiX, out uiY);

    /// <summary>
    /// Sets or clears a camera zoom override (<c>CameraController + 0x4ac</c>).
    /// Passing <c>null</c> (or a negative value) clears this mod's override and restores stock zoom (<c>-1.0f</c>).
    /// Per-mod overrides are automatically cleared when the owning mod unloads.
    /// </summary>
    /// <param name="zoomOverride">Desired zoom override value, or <c>null</c> to restore stock zoom.</param>
    void SetZoomOverride(float? zoomOverride);
}
