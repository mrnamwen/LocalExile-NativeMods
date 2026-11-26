using DualMod.Phase0E2E.Shared;
using LocalExile.Client.ServerBridge;

namespace DualMod.Phase0E2E.Server;

/// <summary>
/// Server half of the Phase 0 E2E probe mod built against <c>LocalExile.Client.ServerBridge</c> 0.2.0.
/// </summary>
public sealed class Phase0ServerMod : IDisposable
{
    private readonly ClientBridgeHost _bridge;
    private readonly Action<string> _log;
    private readonly List<IDisposable> _subscriptions = [];

    public Phase0ServerMod(ClientBridgeHost bridge, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        _bridge = bridge;
        _log = log ?? (_ => { });
    }

    public void Attach()
    {
        _subscriptions.Add(_bridge.On<Phase0Ping>(async (session, ping, ct) =>
        {
            bool ready = _bridge.IsClientReady(session.ConnectionId);
            bool hasPhase0 = _bridge.HasMod(session.ConnectionId, Phase0Protocol.ModId);
            bool hasMaxwell = _bridge.HasMod(session.ConnectionId, "maxwell");
            bool hasSiphon = _bridge.HasMod(session.ConnectionId, "siphon");

            _log(Phase0Protocol.FormatAssertMarker(
                "E2E-12",
                ready && hasPhase0,
                $"conn={session.ConnectionId} ready={ready} phase0={hasPhase0} maxwell={hasMaxwell} siphon={hasSiphon} sdk={ping.ClientSdkVersion}"));

            await _bridge.SendAsync(
                session.ConnectionId,
                new Phase0Pong(
                    CorrelationToken: ping.CorrelationToken,
                    ClientReadyObserved: ready,
                    HasPhase0Mod: hasPhase0,
                    HasMaxwellMod: hasMaxwell,
                    HasSiphonMod: hasSiphon,
                    ServerNote: "Phase0ServerMod 0.2.0 ack"),
                cancellationToken: ct);
        }));

        _subscriptions.Add(_bridge.On<Phase0ProbeAssertionResult>((session, report) =>
        {
            _log(Phase0Protocol.FormatAssertMarker(
                report.AssertionId,
                report.Passed,
                $"[from-client conn={session.ConnectionId}] {report.Detail}"));
        }));

        _subscriptions.Add(_bridge.OnClientReady(async (session, ct) =>
        {
            _log($"[PHASE0-E2E] ServerBridge ClientReady conn={session.ConnectionId} mods={string.Join(',', session.ClientMods)}");
            await Task.CompletedTask;
        }));
    }

    public Task TriggerSuiteAsync(string connectionId, string suite, CancellationToken cancellationToken = default)
    {
        string token = $"{suite}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        return _bridge.SendAsync(
            connectionId,
            new Phase0RunProbesRequest(suite, token),
            cancellationToken: cancellationToken);
    }

    public void Dispose()
    {
        foreach (IDisposable sub in _subscriptions)
        {
            sub.Dispose();
        }
        _subscriptions.Clear();
    }
}
