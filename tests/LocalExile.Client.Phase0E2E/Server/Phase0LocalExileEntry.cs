// Thin LocalExile Gateway IMod entry point for Phase 0 E2E verification.
// Compiled dynamically by LocalExile's ModHost Roslyn loader against live 0.2.0 Gateway assemblies.
// Uses ZERO private reflection: exercises Client.For(player), GameEvents.{ClientReady,ClientMessage,LevelUp,CosmeticChanged,Packet},
// Item.CustomName, and LocalExileGatewayHost / ServerBridge 0.2.0 directly.

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using DualMod.Phase0E2E.Shared;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Modding;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.ServerBridge;

namespace DualMod.Phase0E2E.Server;

public sealed class Phase0LocalExileEntry : IMod, IDisposable
{
    private readonly ConcurrentDictionary<string, Player> _players = new(StringComparer.Ordinal);
    private ClientBridgeHost? _bridge;
    private GatewayBridgeRegistration? _busRegistration;
    private Phase0ServerMod? _serverMod;
    private IDisposable? _gatewayHostHandle;

    public void Load(IModContext mod)
    {
        string id = mod.Manifest.Id;

        _bridge = new ClientBridgeHost(new LocalExileGatewaySink(_players), new ClientBridgeOptions
        {
            ServerVersion = "LocalExile-3.29.3d",
            HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId
        });

        _serverMod = new Phase0ServerMod(_bridge, mod.Log);
        _serverMod.Attach();

        // Register via LocalExileGatewayHost + shared GatewayBridgeBus (0x02C9/0x02CA + /nm)
        _gatewayHostHandle = LocalExileGatewayHost.Attach(mod, _bridge, out _busRegistration);

        bool clientReadySeen = false;
        bool clientReadyChannelOk = false;
        bool clientMessageSeen = false;
        bool e2e13Emitted = false;

        bool levelUpSeen = false;
        bool cosmeticSeen = false;
        bool e2e14Emitted = false;

        // Ensure _players tracks every player as early as area entry so ServerHandshake / ClientHandshake replies are never dropped.
        GameEvents.PlayerEntered.Add(id, player =>
        {
            var client = Client.For(player);
            _players[client.ConnectionId] = player;
            return Task.CompletedTask;
        });

        // E2E-13: GameEvents.ClientReady + Client.For(player)
        GameEvents.ClientReady.Add(id, async player =>
        {
            var client = Client.For(player);
            _players[client.ConnectionId] = player;
            clientReadySeen = true;
            clientReadyChannelOk = client.Connected;
            mod.Log($"[PHASE0-E2E] GameEvents.ClientReady conn={client.ConnectionId} connected={client.Connected}");

            if (clientMessageSeen && !e2e13Emitted)
            {
                e2e13Emitted = true;
                mod.Log(Phase0Protocol.FormatAssertMarker(
                    "E2E-13",
                    clientReadyChannelOk,
                    $"ClientReady & ClientMessage observed; Client.For(player).Connected={clientReadyChannelOk}"));
            }

            if (client.Connected)
            {
                await _bridge.SendAsync(
                    client.ConnectionId,
                    new Phase0ServerEventNotice("ClientReady", $"Server observed Client.For(player) conn={client.ConnectionId}"));
            }
        });

        // E2E-13: GameEvents.ClientMessage
        GameEvents.ClientMessage.Add(id, async msg =>
        {
            var client = Client.For(msg.Player);
            _players[client.ConnectionId] = msg.Player;
            clientMessageSeen = true;
            mod.Log($"[PHASE0-E2E] GameEvents.ClientMessage player={Program.CharacterName(msg.Player)} text='{msg.Text}' links={msg.Links.Count}");

            if (clientReadySeen && !e2e13Emitted)
            {
                e2e13Emitted = true;
                mod.Log(Phase0Protocol.FormatAssertMarker(
                    "E2E-13",
                    clientReadyChannelOk,
                    $"ClientReady & ClientMessage observed; Client.For(player).Connected={clientReadyChannelOk}"));
            }

            await Task.CompletedTask;
            return false;
        });

        // E2E-14: GameEvents.LevelUp + GameEvents.CosmeticChanged
        GameEvents.LevelUp.Add(id, async (player, prevLevel, newLevel) =>
        {
            levelUpSeen = newLevel > prevLevel;
            mod.Log($"[PHASE0-E2E] GameEvents.LevelUp player={Program.CharacterName(player)} old={prevLevel} new={newLevel}");
            if (levelUpSeen && cosmeticSeen && !e2e14Emitted)
            {
                e2e14Emitted = true;
                mod.Log(Phase0Protocol.FormatAssertMarker(
                    "E2E-14",
                    true,
                    $"LevelUp({prevLevel}->{newLevel}) & CosmeticChanged both observed"));
            }
            await Task.CompletedTask;
        });

        GameEvents.CosmeticChanged.Add(id, async evt =>
        {
            cosmeticSeen = true;
            mod.Log($"[PHASE0-E2E] GameEvents.CosmeticChanged slot={evt.Slot} recordId={evt.RecordId} mtxHash=0x{evt.MtxTypeHash:X8}");
            if (levelUpSeen && cosmeticSeen && !e2e14Emitted)
            {
                e2e14Emitted = true;
                mod.Log(Phase0Protocol.FormatAssertMarker(
                    "E2E-14",
                    true,
                    $"LevelUp & CosmeticChanged(mtx=0x{evt.MtxTypeHash:X8}) both observed"));
            }
            await Task.CompletedTask;
        });

        // E2E-07: Echo C->S 0x02FE raw probe payload back to client S->C 0x02FE
        GameEvents.Packet(Phase0Protocol.RawProbeOpcodeLoopback).Add(id, async (player, bytes) =>
        {
            if (bytes.Length < 2)
            {
                return false;
            }

            var client = Client.For(player);
            _players[client.ConnectionId] = player;
            ReadOnlyMemory<byte> payload = bytes.AsMemory(2);
            mod.Log($"[PHASE0-E2E] Echoing C->S 0x{Phase0Protocol.RawProbeOpcodeLoopback:X4} ({payload.Length} B) back to {client.ConnectionId}");
            await client.SendPacket(
                Phase0Protocol.RawProbeOpcodeLoopback,
                payload,
                log: $"0x{Phase0Protocol.RawProbeOpcodeLoopback:x4} phase0 raw loopback echo");
            return true;
        });

        // E2E-15: Item.CustomName (0x80 custom-name flag) via /phase0 item or Phase0ItemTestRequest
        _bridge.On<Phase0ItemTestRequest>(async (session, req, ct) =>
        {
            var item = new Item(1, ["Mods"], 0)
            {
                Rarity = ItemRarity.Rare,
                Identified = true,
                CustomName = req.RequestedCustomName
            };
            byte[] payloadWithName = item.Payload();
            string observed = item.CustomName ?? string.Empty;
            bool setOk = string.Equals(observed, req.RequestedCustomName, StringComparison.Ordinal)
                && payloadWithName.Length > 10;

            bool clearedOk = true;
            if (req.TestClearAfterVerify)
            {
                Item cleared = item with { CustomName = null };
                byte[] payloadCleared = cleared.Payload();
                clearedOk = cleared.CustomName is null && payloadCleared.Length < payloadWithName.Length;
            }

            bool passed = setOk && clearedOk;
            mod.Log(Phase0Protocol.FormatAssertMarker(
                "E2E-15",
                passed,
                $"set='{observed}' payloadBytes={payloadWithName.Length} cleared={clearedOk}"));

            await _bridge.SendAsync(
                session.ConnectionId,
                new Phase0ItemTestResult(passed, observed, clearedOk, "Item.CustomName 0x80 verified"),
                cancellationToken: ct);
        });

        // /phase0 <run|close|raw|item|levelup|chat|finalize> driver command
        GameEvents.Command(id, "phase0", "/phase0 [run|close|raw|item|levelup|chat|finalize]", async (player, args) =>
        {
            var channel = Client.For(player);
            string connId = channel.ConnectionId;
            _players[connId] = player;
            string suite = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "run";

            switch (suite)
            {
                case "levelup":
                {
                    int prevLevel = Math.Max(1, player.Pools.Level);
                    int nextLevel = prevLevel < CharacterStats.LevelStarts.Count ? prevLevel + 1 : prevLevel;
                    if (nextLevel > prevLevel)
                    {
                        uint targetXp = CharacterStats.LevelStarts[prevLevel];
                        uint gain = targetXp > player.Character.Experience ? targetXp - player.Character.Experience : 1u;
                        var (_, after) = CharacterSaves.AddExperience(
                            Program.CharacterName(player),
                            player.Character.Experience,
                            xp => Progression.AddExperience(xp, gain, CharacterStats.LevelStarts));
                        player.Character.Experience = after;
                        player.Pools = player.Pools with { Level = nextLevel, Experience = after };
                        await Program.Send(
                            player,
                            InstanceEntities.PlayerExperienceUpdate(player.Id, after),
                            $"0x02bc phase0 levelup {prevLevel}->{nextLevel}");
                        await GameEvents.RunLevelUp(player, prevLevel, nextLevel);
                    }
                    else
                    {
                        await GameEvents.RunLevelUp(player, Math.Max(1, prevLevel - 1), prevLevel);
                    }
                    break;
                }

                case "chat":
                    await channel.SendChat($"{Phase0Protocol.SuppressChatPrefix} probe_secret");
                    await channel.SendChat($"{Phase0Protocol.AllowChatPrefix} probe_public");
                    await _serverMod.TriggerSuiteAsync(connId, suite);
                    break;

                case "finalize":
                    if (!e2e13Emitted)
                    {
                        e2e13Emitted = true;
                        mod.Log(Phase0Protocol.FormatAssertMarker(
                            "E2E-13",
                            false,
                            $"Incomplete: clientReadySeen={clientReadySeen} clientReadyChannelOk={clientReadyChannelOk} clientMessageSeen={clientMessageSeen}"));
                    }
                    if (!e2e14Emitted)
                    {
                        e2e14Emitted = true;
                        mod.Log(Phase0Protocol.FormatAssertMarker(
                            "E2E-14",
                            false,
                            $"Incomplete: levelUpSeen={levelUpSeen} cosmeticSeen={cosmeticSeen}"));
                    }
                    await _serverMod.TriggerSuiteAsync(connId, suite);
                    break;

                default:
                    await _serverMod.TriggerSuiteAsync(connId, suite);
                    break;
            }
        });
    }

    public void Dispose()
    {
        _gatewayHostHandle?.Dispose();
        _busRegistration?.Dispose();
        _serverMod?.Dispose();
        if ((object?)_bridge is IDisposable disposableBridge)
        {
            disposableBridge.Dispose();
        }
        _players.Clear();
    }

    private sealed class LocalExileGatewaySink : IGatewayPacketSink
    {
        private readonly ConcurrentDictionary<string, Player> _players;

        public LocalExileGatewaySink(ConcurrentDictionary<string, Player> players) => _players = players;

        public async Task SendPacketAsync(string connectionId, ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            if (_players.TryGetValue(connectionId, out Player? player) && !player.Closed)
            {
                await Client.For(player).SendPacket(opcode, payload, cancellationToken: cancellationToken);
            }
        }

        public async Task SendChatReplyAsync(string connectionId, string text, CancellationToken cancellationToken = default)
        {
            if (_players.TryGetValue(connectionId, out Player? player) && !player.Closed)
            {
                await Client.For(player).SendChat(text, cancellationToken);
            }
        }
    }
}
