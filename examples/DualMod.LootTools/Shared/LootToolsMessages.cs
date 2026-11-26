namespace DualMod.LootTools.Shared;

/// <summary>
/// Shared constants, keybind/slot identifiers, and server-clamped limits for <c>DualMod.LootTools</c>.
/// </summary>
public static class LootToolsProtocol
{
    public const string ModId = "loottools";
    public const string ChannelName = "loottools";

    public const string PanelUiPath = "Metadata/UI/Mods/LootTools/LootToolsPanel.ui";
    public const string PanelWindowId = "loottools_panel";

    public const string MagnetKeybindId = "loottools.magnet";
    public const string AoePickupKeybindId = "loottools.aoe_pickup";
    public const string LootMergeKeybindId = "loottools.merge";
    public const string FilterReloadKeybindId = "loottools.filter_reload";
    public const string TogglePanelKeybindId = "loottools.toggle_panel";

    public const string SlotMagnetId = "loottools.magnet";
    public const string SlotAoePickupId = "loottools.aoe_pickup";
    public const string SlotMergeId = "loottools.merge";
    public const string SlotFilterReloadId = "loottools.filter_reload";
    public const string SlotPanelId = "loottools.panel";

    public const int MinRadiusCells = 5;
    public const int MaxRadiusCells = 80;
    public const int DefaultMagnetRadiusCells = 60;
    public const int DefaultPickupRadiusCells = 50;
    public const int DefaultMergeRadiusCells = 60;
    public const int MaxBatchEntityCount = 64;

    public const float MinAutoMergeIntervalSeconds = 0.5f;
    public const float MaxAutoMergeIntervalSeconds = 10.0f;
    public const float DefaultAutoMergeIntervalSeconds = 1.0f;

    public const float MinZoomOverride = 0.25f;
    public const float MaxZoomOverride = 3.0f;
    public const float DefaultZoomOverride = 1.35f;

    /// <summary>Path of Exile 3.29 conversion factor from grid cells to 3D world units.</summary>
    public const float WorldUnitsPerGridCell = 10.869565f;

    public const ushort GoldMaxGroundStack = ushort.MaxValue;
}

/// <summary>
/// Client-to-server request to pull filter-visible ground items near the player.
/// Client NEVER invents items — only sends <see cref="EntityIds"/> that passed PoE's active local item filter.
/// </summary>
public sealed record LootMagnetRequest(
    uint[] EntityIds,
    int RadiusCells = LootToolsProtocol.DefaultMagnetRadiusCells);

/// <summary>
/// Client-to-server request to pick up filter-visible ground items within pickup radius.
/// </summary>
public sealed record AoePickupRequest(
    uint[] EntityIds,
    int RadiusCells = LootToolsProtocol.DefaultPickupRadiusCells);

/// <summary>
/// Client-to-server request to merge compatible partial ground stacks.
/// When <see cref="EntityIds"/> is <c>null</c> or empty, the server merges all eligible nearby stackables within <see cref="RadiusCells"/>.
/// </summary>
public sealed record LootMergeRequest(
    uint[]? EntityIds = null,
    int RadiusCells = LootToolsProtocol.DefaultMergeRadiusCells,
    bool IsAutoMerge = false);

/// <summary>
/// Client-to-server toggle synchronizing server-side auto-merge area tick preference.
/// </summary>
public sealed record LootToolsSetAutoMergeRequest(
    bool Enabled,
    int RadiusCells = LootToolsProtocol.DefaultMergeRadiusCells);

/// <summary>
/// Server-to-client execution summary displayed via <c>IToastService</c> and <c>LootToolsPanel.ui</c>.
/// </summary>
public sealed record LootToolsToastNotification(
    string Feature,
    string Title,
    string Message,
    string Severity = "Info",
    int AffectedCount = 0,
    int GoldPicked = 0,
    int InventoryFullSkipped = 0,
    int MergedPilesRemoved = 0,
    int MergedPilesCreated = 0);
