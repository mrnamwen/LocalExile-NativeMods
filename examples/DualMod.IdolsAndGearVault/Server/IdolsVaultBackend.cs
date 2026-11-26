using System.Collections.Concurrent;
using DualMod.IdolsAndGearVault.Shared;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.Sdk.Protocol;
using LocalExile.Client.ServerBridge;

namespace DualMod.IdolsAndGearVault.Server;

/// <summary>
/// Abstraction bridging <see cref="IdolsVaultServerMod"/> to either live LocalExile Gateway stash inventories
/// (<c>IdolsVaultLocalExileEntry.cs</c>) or deterministic in-memory test harnesses.
/// </summary>
public interface IIdolsVaultBackend
{
    /// <summary>
    /// Reads items currently stored in <paramref name="targetTabIndex"/> that match <paramref name="gearClassFilter"/>.
    /// </summary>
    IReadOnlyList<GearVaultItemRecord> GetVaultItems(
        string connectionId,
        ushort targetTabIndex,
        string gearClassFilter,
        int maxEntries);

    /// <summary>
    /// Emits a compact S→C <c>0x02C9</c> ruleset telemetry packet to <paramref name="rulesetId"/> (verified free slot <c>0xAE</c>).
    /// </summary>
    Task SendBoardRulesetUpdateAsync(
        string connectionId,
        byte rulesetId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Deterministic in-memory backend for automated unit and end-to-end tests of <see cref="IdolsVaultServerMod"/>.
/// </summary>
public sealed class InMemoryIdolsVaultBackend(IGatewayPacketSink? packetSink = null) : IIdolsVaultBackend
{
    private readonly ConcurrentDictionary<string, List<GearVaultItemRecord>> _stashItems = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(string ConnectionId, byte RulesetId, byte[] Payload)> _sentRulesetUpdates = new();

    public IReadOnlyList<(string ConnectionId, byte RulesetId, byte[] Payload)> SentRulesetUpdates => [.. _sentRulesetUpdates];

    public void AddVaultItem(string connectionId, GearVaultItemRecord item)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(item);

        List<GearVaultItemRecord> list = _stashItems.GetOrAdd(connectionId, static _ => []);
        lock (list)
        {
            list.Add(item);
        }
    }

    public void ClearPlayer(string connectionId) => _stashItems.TryRemove(connectionId, out _);

    public IReadOnlyList<GearVaultItemRecord> GetVaultItems(
        string connectionId,
        ushort targetTabIndex,
        string gearClassFilter,
        int maxEntries)
    {
        if (!_stashItems.TryGetValue(connectionId, out List<GearVaultItemRecord>? list))
        {
            return [];
        }

        lock (list)
        {
            return [.. list
                .Where(item =>
                    item.TabIndex == targetTabIndex &&
                    string.Equals(item.GearClass, gearClassFilter, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.ItemLevel)
                .ThenBy(item => item.ItemId)
                .Take(Math.Max(1, maxEntries))];
        }
    }

    public Task SendBoardRulesetUpdateAsync(
        string connectionId,
        byte rulesetId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        _sentRulesetUpdates.Enqueue((connectionId, rulesetId, payload.ToArray()));
        if (packetSink is not null)
        {
            byte[] body = MessageCodec.EncodeRulesetBody(rulesetId, payload.Span);
            return packetSink.SendPacketAsync(connectionId, IOpcodeRegistry.ServerRulesetOpcode, body, cancellationToken);
        }

        return Task.CompletedTask;
    }
}
