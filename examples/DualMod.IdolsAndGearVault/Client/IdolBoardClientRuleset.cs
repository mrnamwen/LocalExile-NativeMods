using DualMod.IdolsAndGearVault.Shared;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;

namespace DualMod.IdolsAndGearVault.Client;

/// <summary>
/// Live native per-area client ruleset registered into verified free slot
/// <see cref="IRulesetRegistry.FirstFreeNativeRulesetId"/> (<c>0xAE</c>, never <c>0xA0</c>).
/// Receives compact binary S→C <c>0x02C9</c> Idol Board telemetry payloads (<c>[idolCount, occupiedCells]</c>).
/// </summary>
public sealed class IdolBoardClientRuleset : IClientRuleset
{
    private readonly Action<int, int>? _onTelemetryReceived;

    public IdolBoardClientRuleset()
        : this(null)
    {
    }

    public IdolBoardClientRuleset(Action<int, int>? onTelemetryReceived)
    {
        _onTelemetryReceived = onTelemetryReceived;
    }

    public byte RulesetId => IRulesetRegistry.FirstFreeNativeRulesetId;
    public string Name => "IdolsVault.BoardRuleset";

    public string? CurrentAreaId { get; private set; }
    public int AreaEnteredCount { get; private set; }
    public int AreaLeftCount { get; private set; }
    public int FrameCount { get; private set; }
    public int UpdateCount { get; private set; }
    public byte[] LastPayload { get; private set; } = [];
    public int LastReportedIdolCount { get; private set; }
    public int LastReportedOccupiedCells { get; private set; }

    public void OnAreaEntered(AreaEventArgs area)
    {
        CurrentAreaId = area.AreaId;
        AreaEnteredCount++;
    }

    public void OnRulesetUpdate(ReadOnlySpan<byte> payload)
    {
        UpdateCount++;
        LastPayload = payload.ToArray();
        if (payload.Length >= 2)
        {
            LastReportedIdolCount = payload[0];
            LastReportedOccupiedCells = Math.Min(IdolBoardGeometry.ActiveCellCount, (int)payload[1]);
            _onTelemetryReceived?.Invoke(LastReportedIdolCount, LastReportedOccupiedCells);
        }
    }

    public void OnFrame(float deltaSeconds)
    {
        if (deltaSeconds > 0f)
        {
            FrameCount++;
        }
    }

    public void OnAreaLeft()
    {
        CurrentAreaId = null;
        AreaLeftCount++;
    }
}
