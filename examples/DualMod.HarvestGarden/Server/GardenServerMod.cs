using System.Collections.Concurrent;
using DualMod.HarvestGarden.Shared;
using LocalExile.Client.ServerBridge;

namespace DualMod.HarvestGarden.Server;

/// <summary>
/// Server half of the Harvest Garden dual-side mod.
/// Maintains authoritative per-character Sacred Grove state (planted seeds, growth stages, lifeforce),
/// handles the <c>/garden</c> chat command with graceful text fallback when the player lacks NativeMods,
/// and synchronizes typed state over <see cref="ClientBridgeHost"/> when the player has the client mod.
/// </summary>
public sealed class GardenServerMod : IDisposable
{
    private const int DefaultMaxPlots = 6;
    private const int DefaultLifeforcePerSeed = 25;
    private const int MaxGrowthStage = 3;

    private readonly ClientBridgeHost _bridge;
    private readonly ConcurrentDictionary<string, CharacterGardenState> _gardens = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _connectionCharacters = new(StringComparer.Ordinal);
    private readonly List<IDisposable> _subscriptions = [];

    public GardenServerMod(ClientBridgeHost bridge)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    }

    /// <summary>
    /// Attaches channel message handlers to <see cref="ClientBridgeHost"/>.
    /// In a LocalExile Gateway deployment, <c>IMod.Load(IModContext)</c> calls <see cref="Attach"/>
    /// alongside registering <c>GameEvents.Command(id, "garden", ...)</c>.
    /// </summary>
    public void Attach()
    {
        if (_subscriptions.Count > 0)
            return;

        _subscriptions.Add(_bridge.On<PlantSeedRequest>(GardenProtocol.ChannelName, (session, req) =>
        {
            if (!session.HasClientMod(GardenProtocol.ModId))
                return;

            string character = _connectionCharacters.GetValueOrDefault(session.ConnectionId, session.ConnectionId);
            var snapshot = PlantSeed(character, req.SeedName);
            session.SendAsync(GardenProtocol.ChannelName, snapshot).GetAwaiter().GetResult();
        }));

        _subscriptions.Add(_bridge.On<HarvestPlotRequest>(GardenProtocol.ChannelName, (session, req) =>
        {
            if (!session.HasClientMod(GardenProtocol.ModId))
                return;

            string character = _connectionCharacters.GetValueOrDefault(session.ConnectionId, session.ConnectionId);
            var snapshot = HarvestPlots(character, req.PlotIndex);
            session.SendAsync(GardenProtocol.ChannelName, snapshot).GetAwaiter().GetResult();
        }));
    }

    public void Detach()
    {
        foreach (var sub in _subscriptions)
            sub.Dispose();
        _subscriptions.Clear();
    }

    public void Dispose() => Detach();

    /// <summary>
    /// Handles the <c>/garden [plant &lt;seed&gt; | harvest]</c> command.
    /// Returns a chat reply line for players without NativeMods, or pushes rich UI sync to NativeMods clients.
    /// </summary>
    public async Task<string> HandleGardenCommandAsync(
        string connectionId,
        string characterName,
        string[] args,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(characterName);
        args ??= [];

        _connectionCharacters[connectionId] = characterName;

        var session = _bridge.For(connectionId);
        bool hasRichClient = session.HasNativeHost && session.HasClientMod(GardenProtocol.ModId);

        if (args.Length > 0 && args[0].Equals("plant", StringComparison.OrdinalIgnoreCase))
        {
            string seedName = args.Length > 1 ? string.Join(' ', args[1..]) : "Wild Thornfruit";
            var snapshot = PlantSeed(characterName, seedName);
            if (hasRichClient)
                await session.SendAsync(GardenProtocol.ChannelName, snapshot, cancellationToken: cancellationToken).ConfigureAwait(false);
            return snapshot.StatusMessage;
        }

        if (args.Length > 0 && args[0].Equals("harvest", StringComparison.OrdinalIgnoreCase))
        {
            var snapshot = HarvestPlots(characterName, -1);
            if (hasRichClient)
                await session.SendAsync(GardenProtocol.ChannelName, snapshot, cancellationToken: cancellationToken).ConfigureAwait(false);
            return snapshot.StatusMessage;
        }

        var current = BuildSnapshot(characterName, $"Welcome to the Sacred Grove, {characterName}.");
        if (hasRichClient)
        {
            await session.SendAsync(
                GardenProtocol.ChannelName,
                new GardenOpenWindowCommand(characterName, current),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return $"[Harvest Garden] Opened Seed Stockpile window ({current.Plots.Count} active plot(s), {current.Lifeforce} lifeforce).";
        }

        // Graceful degradation for stock 3.29 clients without the NativeMods extender
        string plotSummary = current.Plots.Count == 0
            ? "No seeds planted."
            : string.Join(", ", current.Plots.Select(p => $"#{p.PlotIndex}:{p.SeedName}({p.GrowthStage}/{p.MaxStage})"));

        return $"[Harvest Garden — Chat Fallback] Lifeforce: {current.Lifeforce} | {plotSummary} | " +
               "Use '/garden plant <seed>' or '/garden harvest'. Install NativeMods via Waypoint for the native UI.";
    }

    /// <summary>Authoritative server operation: plants a seed and advances existing plots by 1 growth stage.</summary>
    public GardenStateSnapshot PlantSeed(string characterName, string seedName)
    {
        string normalizedSeed = string.IsNullOrWhiteSpace(seedName) ? "Wild Thornfruit" : seedName.Trim();
        var state = _gardens.GetOrAdd(characterName, _ => new CharacterGardenState());

        lock (state)
        {
            foreach (var existing in state.Plots)
            {
                if (existing.GrowthStage < MaxGrowthStage)
                    existing.GrowthStage++;
            }

            if (state.Plots.Count >= DefaultMaxPlots)
            {
                return BuildSnapshotLocked(characterName, state,
                    $"All {DefaultMaxPlots} Sacred Grove plots are full. Harvest ripe plots before planting '{normalizedSeed}'.");
            }

            int nextIndex = state.Plots.Count;
            state.Plots.Add(new MutablePlot(nextIndex, normalizedSeed, growthStage: 1));
            return BuildSnapshotLocked(characterName, state,
                $"Planted '{normalizedSeed}' in Plot #{nextIndex + 1}. ({state.Plots.Count}/{DefaultMaxPlots} plots active)");
        }
    }

    /// <summary>Authoritative server operation: harvests plots and awards Condensed Lifeforce.</summary>
    public GardenStateSnapshot HarvestPlots(string characterName, int plotIndex = -1)
    {
        var state = _gardens.GetOrAdd(characterName, _ => new CharacterGardenState());
        lock (state)
        {
            if (state.Plots.Count == 0)
                return BuildSnapshotLocked(characterName, state, "No planted seeds to harvest yet.");

            List<MutablePlot> harvested;
            if (plotIndex >= 0 && plotIndex < state.Plots.Count)
            {
                harvested = [state.Plots[plotIndex]];
                state.Plots.RemoveAt(plotIndex);
            }
            else
            {
                harvested = [.. state.Plots];
                state.Plots.Clear();
            }

            for (int i = 0; i < state.Plots.Count; i++)
                state.Plots[i].PlotIndex = i;

            int gained = harvested.Sum(p => DefaultLifeforcePerSeed * Math.Max(1, p.GrowthStage));
            state.Lifeforce += gained;
            state.TotalHarvested += harvested.Count;

            return BuildSnapshotLocked(characterName, state,
                $"Harvested {harvested.Count} plot(s) for +{gained} Condensed Lifeforce (total: {state.Lifeforce}).");
        }
    }

    public GardenStateSnapshot BuildSnapshot(string characterName, string statusMessage)
    {
        var state = _gardens.GetOrAdd(characterName, _ => new CharacterGardenState());
        lock (state)
        {
            return BuildSnapshotLocked(characterName, state, statusMessage);
        }
    }

    private static GardenStateSnapshot BuildSnapshotLocked(string characterName, CharacterGardenState state, string status)
    {
        var plots = state.Plots
            .Select(p => new GardenPlotState(p.PlotIndex, p.SeedName, p.GrowthStage, MaxGrowthStage, p.GrowthStage >= MaxGrowthStage))
            .ToArray();

        float avgProgress = plots.Length == 0
            ? 0f
            : plots.Average(p => (float)p.GrowthStage / p.MaxStage);

        return new GardenStateSnapshot(
            characterName,
            state.Lifeforce,
            state.TotalHarvested,
            avgProgress,
            status,
            plots);
    }

    private sealed class CharacterGardenState
    {
        public int Lifeforce { get; set; }
        public int TotalHarvested { get; set; }
        public List<MutablePlot> Plots { get; } = [];
    }

    private sealed class MutablePlot(int plotIndex, string seedName, int growthStage)
    {
        public int PlotIndex { get; set; } = plotIndex;
        public string SeedName { get; } = seedName;
        public int GrowthStage { get; set; } = growthStage;
    }
}
