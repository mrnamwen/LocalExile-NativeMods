// LocalExile Gateway IMod entry point for Maxwell the Cat Pet MTX.
// Place alongside maxwell.dll, DualMod.MaxwellPet.Shared.dll, LocalExile.Client.ServerBridge.dll,
// and LocalExile.Client.Sdk.dll inside <LocalExile>/Mods/Binaries/maxwell/.
// LocalExile's ModHost compiles this file against the running Gateway's exact GameServer.* and Game
// assemblies with ZERO edits required to LocalExile core.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DualMod.MaxwellPet.Server;
using DualMod.MaxwellPet.Shared;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.ServerBridge;

public sealed class MaxwellLocalExileEntry : IMod, IDisposable
{
    private readonly ConcurrentDictionary<string, Player> _players = new(StringComparer.Ordinal);
    private ClientBridgeHost? _bridge;
    private MaxwellServerMod? _serverMod;

    public void Load(IModContext mod)
    {
        string id = mod.Manifest.Id;

        var sink = new LocalExileGatewaySink(_players);
        _bridge = new ClientBridgeHost(sink, new ClientBridgeOptions
        {
            ServerVersion = "LocalExile-3.29.3d",
            HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId
        });

        var backend = new LocalExileMaxwellWorldBackend(_players, mod.Log);
        _serverMod = new MaxwellServerMod(_bridge, backend);
        _serverMod.Attach();

        // /maxwell command (works for both NativeMods clients and stock vanilla clients)
        GameEvents.Command(id, "maxwell",
            "/maxwell [panel|summon [1|2]|dismiss|toggle|spin <0..720>|float <0..60>|music <on|off|mute|unmute|vol 0..1>|dance|status]",
            async (player, args) =>
            {
                string connId = ConnectionKey(player);
                _players[connId] = player;
                string character = Program.CharacterName(player);
                string reply = await _serverMod.HandleMaxwellCommandAsync(connId, character, args);
                if (!string.IsNullOrEmpty(reply))
                {
                    await Program.PlayerOf(player).Tell(reply);
                }
            });

        // /nm chat fallback transport (C->S)
        GameEvents.Command(id, "nm", "/nm <native-mods-envelope>", async (player, args) =>
        {
            string connId = ConnectionKey(player);
            _players[connId] = player;
            string line = "/nm " + string.Join(' ', args);
            await _bridge.HandleIncomingChatCommandAsync(connId, line);
        });

        // C->S 0x02ca host ruleset transport (unclaimed by stock modules for ruleset 0xA0)
        GameEvents.Packet(IOpcodeRegistry.ClientRulesetOpcode).Add(id, async (player, bytes) =>
        {
            if (bytes.Length <= 2) return false;
            string connId = ConnectionKey(player);
            _players[connId] = player;
            return await _bridge.HandleIncomingPacketAsync(connId, IOpcodeRegistry.ClientRulesetOpcode, bytes.AsMemory(2));
        });

        // Area entry: grant appended Maxwell Pet MTX record (0x0204) and respawn active pet
        GameEvents.PlayerEntered.Add(id, async player =>
        {
            string connId = ConnectionKey(player);
            _players[connId] = player;
            backend.EnsureCatalogRegistered(MaxwellServerMod.DefaultRegistration);
            await _serverMod.OnPlayerEnteredAsync(connId, Program.CharacterName(player));
        });

        GameEvents.PlayerLeaving.Add(id, (player, reason) =>
        {
            if (player.Closed)
            {
                string connId = ConnectionKey(player);
                _players.TryRemove(connId, out _);
                _bridge.RemoveSession(connId);
            }
            return Task.CompletedTask;
        });

        mod.Log($"Loaded Maxwell the Cat Pet MTX (MtxTypes row {MaxwellProtocol.MtxTypesAppendedRow}, Pet row {MaxwellProtocol.PetAppendedRow}, hash 0x{MaxwellProtocol.MtxTypeHash:X8}).");
    }

    public void Dispose()
    {
        _serverMod?.Dispose();
        _serverMod = null;
        _bridge = null;
        _players.Clear();
    }

    private static string ConnectionKey(Player player) =>
        $"{player.Connection.Remote}#{player.Id}";

    private sealed class LocalExileGatewaySink(ConcurrentDictionary<string, Player> players) : IGatewayPacketSink
    {
        public async Task SendPacketAsync(string connectionId, ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            if (!players.TryGetValue(connectionId, out var player) || player.Closed)
                return;

            byte[] frame = new byte[2 + payload.Length];
            frame[0] = (byte)(opcode >> 8);
            frame[1] = (byte)(opcode & 0xFF);
            payload.Span.CopyTo(frame.AsSpan(2));
            await Program.Send(player, frame, $"0x{opcode:x4} maxwell native-mods ruleset payload ({payload.Length} B)");
        }

        public async Task SendChatReplyAsync(string connectionId, string text, CancellationToken cancellationToken = default)
        {
            if (!players.TryGetValue(connectionId, out var player) || player.Closed)
                return;

            await Program.PlayerOf(player).Tell(text);
        }
    }

    private sealed class LocalExileMaxwellWorldBackend(
        ConcurrentDictionary<string, Player> players,
        Action<string> log) : IMaxwellWorldBackend
    {
        private readonly object _catalogLock = new();
        private bool _catalogPatched;

        public IDisposable RegisterPetMtx(MaxwellMtxRegistration registration)
        {
            EnsureCatalogRegistered(registration);
            return new ActionDisposable(() => UnregisterCatalog(registration));
        }

        public void EnsureCatalogRegistered(MaxwellMtxRegistration reg)
        {
            lock (_catalogLock)
            {
                if (_catalogPatched || Game.Mtx is null || Game.MtxEffects is null)
                    return;

                try
                {
                    // Append Maxwell to Game.Mtx (_owned + _byId) so 0x01f2/0x01f4 and 0x01f9 recognize RecordId 8558.
                    var record = new InstanceMicrotransactions.Record(reg.MtxTypeHash, reg.RecordId);
                    var ownedField = typeof(MtxCatalog).GetField("_owned", BindingFlags.Instance | BindingFlags.NonPublic);
                    var byIdField = typeof(MtxCatalog).GetField("_byId", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (ownedField?.GetValue(Game.Mtx) is List<InstanceMicrotransactions.Record> owned &&
                        byIdField?.GetValue(Game.Mtx) is Dictionary<uint, InstanceMicrotransactions.Record> byId &&
                        !byId.ContainsKey(reg.RecordId))
                    {
                        owned.Add(record);
                        byId[reg.RecordId] = record;
                    }

                    // Register Pet effect in Game.MtxEffects (_data.Effects) so CosmeticsFeature.SpawnPets spawns Metadata/Pet/Mods/Maxwell/MaxwellPet.
                    var dataField = typeof(CosmeticEffects).GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (dataField?.GetValue(Game.MtxEffects) is ValueTuple<Dictionary<uint, CosmeticEffects.Effect>, Dictionary<byte, CosmeticEffects.Slot>> data)
                    {
                        data.Item1[reg.MtxTypeHash] = new CosmeticEffects.Effect(0, 0, [], Pet: reg.PetRowIndex + 1)
                        {
                            PetType = reg.PetMetadataPath,
                            PetComponents = PetObject.BaseComponents,
                            PetStates = null
                        };
                    }

                    _catalogPatched = true;
                    log($"Registered brand-new Maxwell Pet MTX in Game.Mtx (id={reg.RecordId}) and Game.MtxEffects (type='{reg.PetMetadataPath}').");
                }
                catch (Exception ex)
                {
                    log($"Warning: Could not register Maxwell into live MtxCatalog/CosmeticEffects ({ex.Message}); /maxwell direct spawn remains active.");
                }
            }
        }

        private void UnregisterCatalog(MaxwellMtxRegistration reg)
        {
            lock (_catalogLock)
            {
                if (!_catalogPatched)
                    return;

                try
                {
                    var ownedField = typeof(MtxCatalog).GetField("_owned", BindingFlags.Instance | BindingFlags.NonPublic);
                    var byIdField = typeof(MtxCatalog).GetField("_byId", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (ownedField?.GetValue(Game.Mtx) is List<InstanceMicrotransactions.Record> owned)
                        owned.RemoveAll(r => r.Id == reg.RecordId && r.MtxTypeHash == reg.MtxTypeHash);
                    if (byIdField?.GetValue(Game.Mtx) is Dictionary<uint, InstanceMicrotransactions.Record> byId)
                        byId.Remove(reg.RecordId);

                    var dataField = typeof(CosmeticEffects).GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (dataField?.GetValue(Game.MtxEffects) is ValueTuple<Dictionary<uint, CosmeticEffects.Effect>, Dictionary<byte, CosmeticEffects.Slot>> data)
                        data.Item1.Remove(reg.MtxTypeHash);
                }
                catch
                {
                    // Best-effort cleanup on mod unload.
                }
                finally
                {
                    _catalogPatched = false;
                }
            }
        }

        public async Task GrantMtxRecordAsync(string connectionId, MaxwellMtxRegistration registration, CancellationToken cancellationToken = default)
        {
            if (!players.TryGetValue(connectionId, out var player) || player.Closed)
                return;

            var record = new InstanceMicrotransactions.Record(registration.MtxTypeHash, registration.RecordId);
            await Program.Send(player,
                InstanceMicrotransactions.RecordUpsert(record, isStore: false, isGuild: false, justReceived: true),
                $"0x0204 grant Maxwell Pet MTX record {registration.RecordId} (0x{registration.MtxTypeHash:x8})");
        }

        public async Task<uint> SpawnPetEntityAsync(string connectionId, string characterName, string petMetadataPath, byte petSlot, CancellationToken cancellationToken = default)
        {
            if (!players.TryGetValue(connectionId, out var player) || player.Closed)
                return 0;

            double now = Game.AreaTime(player);
            var (px, py) = player.Walk.Position(now);
            int side = petSlot == MaxwellProtocol.PetSlot2 ? -1 : 1;
            var spot = (X: px + side * 6, Y: py + 3);
            if (player.Layout.Walkable is { } walkable && !walkable(spot.X, spot.Y))
                spot = (px, py);

            uint entityId = player.TakeObjectId();
            var pet = new PetObject(spot.X, spot.Y, player.Id);
            uint typeHash = InstanceEntities.ObjectTypeHash(petMetadataPath);
            byte[] createPacket = InstanceEntities.CreateObject(entityId, typeHash, pet.Blob());

            if (!await Game.SendWorldCreate(player, entityId, createPacket,
                $"0x02bb create Maxwell pet 0x{entityId:x} {petMetadataPath} (hash 0x{typeHash:x8}) at ({spot.X}, {spot.Y})"))
            {
                return 0;
            }

            if (player.Closed)
                return 0;

            lock (player.Pets)
            {
                player.Pets[entityId] = new SpawnedPet(player.NewWalk(spot.X, spot.Y, player.Walk.Speed), side);
            }

            await Game.SendWorldRecord(player, entityId, InstanceProtocol.EnterWakeZone(entityId),
                $"0x01a9 Maxwell pet 0x{entityId:x} enters wake zone");

            return entityId;
        }

        public async Task DespawnPetEntityAsync(string connectionId, uint petEntityId, CancellationToken cancellationToken = default)
        {
            if (!players.TryGetValue(connectionId, out var player) || player.Closed || petEntityId == 0)
                return;

            lock (player.Pets)
            {
                player.Pets.Remove(petEntityId);
            }

            await Game.SendWorldRemoval(player, petEntityId,
                $"0x{InstanceProtocol.RemoveObjectId:x4} remove Maxwell pet 0x{petEntityId:x}");
        }

        private sealed class ActionDisposable(Action onDispose) : IDisposable
        {
            private Action? _onDispose = onDispose;
            public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
        }
    }
}
