namespace DualMod.HarvestGarden.Shared;

/// <summary>
/// Shared constants and custom opcode identifiers compiled into both Server and Client halves.
/// </summary>
public static class GardenProtocol
{
    public const string ModId = "harvest-garden";
    public const string ChannelName = "harvest-garden";

    public const string StockpileUiPath = "Metadata/UI/Mods/Garden/Stockpile.ui";
    public const string PlotObjectPath = "Metadata/MiscellaneousObjects/Mods/Garden/Plot.ot";

    public const string GealRipenBuiltin = "GardenRipen";
    public const string GrowthGaugeControlType = "GrowthGauge";

    /// <summary>Custom opcode IDs matching <c>mod.json</c> <c>nativeClient.customOpcodes</c>.</summary>
    public const ushort OpcodePlantSeed = 1001;
    public const ushort OpcodeHarvestPlot = 1002;
    public const ushort OpcodeSyncState = 1003;
    public const ushort OpcodeOpenWindow = 1004;

    /// <summary>Host-channel custom ruleset ID for the Sacred Grove per-area mechanic.</summary>
    public const uint GardenRulesetId = 0xA1;
}

/// <summary>Client -> Server: player requests planting a named seed into the Sacred Grove.</summary>
public sealed record PlantSeedRequest(
    string SeedName,
    bool AutoCollectLifeforce);

/// <summary>Client -> Server: player requests harvesting ripe plots (plotIndex = -1 harvests all ripe plots).</summary>
public sealed record HarvestPlotRequest(int PlotIndex = -1);

/// <summary>Server -> Client: authoritative state of a single garden plot.</summary>
public sealed record GardenPlotState(
    int PlotIndex,
    string SeedName,
    int GrowthStage,
    int MaxStage,
    bool IsRipe);

/// <summary>Server -> Client: authoritative snapshot of the player's Sacred Grove.</summary>
public sealed record GardenStateSnapshot(
    string CharacterName,
    int Lifeforce,
    int TotalHarvested,
    float AverageGrowthProgress,
    string StatusMessage,
    IReadOnlyList<GardenPlotState> Plots);

/// <summary>Server -> Client: instructs the client mod to open/focus the Seed Stockpile window.</summary>
public sealed record GardenOpenWindowCommand(
    string CharacterName,
    GardenStateSnapshot InitialState);
