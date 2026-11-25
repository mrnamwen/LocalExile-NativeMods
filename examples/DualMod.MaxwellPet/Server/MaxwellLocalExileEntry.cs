// Thin LocalExile Gateway IMod entry point for Maxwell the Cat Pet MTX.
// Delegates Pet MTX catalog registration, S->C 0x0204 ownership grant encoding, and real-time
// Cosmetics (K) menu equip/unequip detection to LocalExilePetMtxRegistrar in LocalExile.Client.ServerBridge
// so ZERO edits to LocalExile core and ZERO duplicated catalog reflection are needed.

using System;
using System.Collections.Concurrent;
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
    private GatewayBridgeRegistration? _busRegistration;
    private LocalExilePetMtxRegistrar? _registrar;
    private MaxwellServerMod? _serverMod;

    public void Load(IModContext mod)
    {
        string id = mod.Manifest.Id;

        _bridge = new ClientBridgeHost(new LocalExileGatewaySink(_players), new ClientBridgeOptions
        {
            ServerVersion = "LocalExile-3.29.3d",
            HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId
        });

        _registrar = new LocalExilePetMtxRegistrar();
        var backend = new LocalExileMaxwellWorldBackend(_players, _registrar, mod.Log);
        _serverMod = new MaxwellServerMod(_bridge, backend, _registrar);
        _serverMod.Attach();

        // Register on the shared cross-ALC AppDomain bus so multiple NativeMods server mods
        // coexist cleanly on 0x02CA and /nm with a single deduplicated ServerHandshake.
        _busRegistration = _bridge.AttachToGatewayBus(
            id,
            onGatewayContext: (connId, character, gatewayPlayer) =>
            {
                if (gatewayPlayer is Player p)
                    _players[connId] = p;
                if (!string.IsNullOrWhiteSpace(character))
                    _serverMod.BindConnection(connId, character);
            },
            onDispatchError: (owner, ex) => mod.Log($"[gateway-bus] participant '{owner}' failed: {ex.Message}"));

        // /maxwell command (works for both NativeMods clients and stock vanilla clients)
        GameEvents.Command(id, "maxwell",
            "/maxwell [panel|summon [1|2]|dismiss|toggle|spin <0..720>|float <0..60>|music <on|off|mute|unmute|vol 0..1>|dance|status]",
            async (player, args) =>
            {
                string connId = ConnectionKey(player);
                _players[connId] = player;
                string reply = await _serverMod.HandleMaxwellCommandAsync(connId, Program.CharacterName(player), args);
                if (!string.IsNullOrEmpty(reply))
                    await Program.PlayerOf(player).Tell(reply);
            });

        // /nm chat fallback transport (C->S, fanned out across all NativeMods server mods via GatewayBridgeBus)
        GameEvents.Command(id, "nm", "/nm <native-mods-envelope>", async (player, args) =>
        {
            string connId = ConnectionKey(player);
            string character = Program.CharacterName(player);
            _players[connId] = player;
            _serverMod.BindConnection(connId, character);
            await _bridge.HandleIncomingChatCommandAsync(connId, "/nm " + string.Join(' ', args), character, player);
        });

        // C->S 0x02ca host ruleset transport (unclaimed by stock modules for ruleset 0xA0;
        // fanned out to every attached NativeMods server mod via GatewayBridgeBus)
        GameEvents.Packet(IOpcodeRegistry.ClientRulesetOpcode).Add(id, async (player, bytes) =>
        {
            if (bytes.Length <= 2) return false;
            string connId = ConnectionKey(player);
            string character = Program.CharacterName(player);
            _players[connId] = player;
            _serverMod.BindConnection(connId, character);
            return await _bridge.HandleIncomingPacketAsync(connId, IOpcodeRegistry.ClientRulesetOpcode, bytes.AsMemory(2), character, player);
        });

        // Area entry: ensure live MtxCatalog/CosmeticEffects include Maxwell, grant 0x0204 ownership,
        // and sync if the player already has Maxwell equipped (or unequipped) in the Cosmetics (K) menu.
        GameEvents.PlayerEntered.Add(id, async player =>
        {
            string connId = ConnectionKey(player);
            string character = Program.CharacterName(player);
            _players[connId] = player;

            _registrar.ApplyToLiveCatalogs(Game.Mtx, Game.MtxEffects, mod.Log);
            await _serverMod.OnPlayerEnteredAsync(connId, character);
            _registrar.ObserveLiveGatewayPlayer(connId, character, Game.WornCosmetics(player), player.Pets);
            await _serverMod.ReconcileCosmeticAndPetStateAsync(
                connId,
                character,
                _registrar.IsEquipped(connId, MaxwellServerMod.MaxwellPetDefinition.MtxTypeHash),
                SnapshotPetIds(player));
        });

        // AreaTick: detect Cosmetics (K) menu equip/unequip transitions in real time so clicking
        // "Maxwell the Cat Pet" in Pet Slot 1 (18) or 2 (19) starts/stops Stock Market music and updates MaxwellPanel.ui!
        GameEvents.AreaTick.Add(id, GameEvents.Always(async (player, _) =>
        {
            if (player.Closed || player.LeavingArea) return;
            string connId = ConnectionKey(player);
            string character = Program.CharacterName(player);
            _players[connId] = player;
            _registrar.ApplyToLiveCatalogs(Game.Mtx, Game.MtxEffects, mod.Log);
            _registrar.ObserveLiveGatewayPlayer(connId, character, Game.WornCosmetics(player), player.Pets);
            await _serverMod.ReconcileCosmeticAndPetStateAsync(
                connId,
                character,
                _registrar.IsEquipped(connId, MaxwellServerMod.MaxwellPetDefinition.MtxTypeHash),
                SnapshotPetIds(player));
        }));

        GameEvents.PlayerLeaving.Add(id, (player, reason) =>
        {
            if (player.Closed)
            {
                string connId = ConnectionKey(player);
                _players.TryRemove(connId, out _);
                _registrar.ClearConnection(connId);
                _serverMod.OnPlayerLeft(connId);
                _bridge.RemoveSession(connId);
            }
            return Task.CompletedTask;
        });

        mod.Log($"Loaded Maxwell the Cat Pet MTX via LocalExilePetMtxRegistrar (RecordId={MaxwellServerMod.MaxwellPetDefinition.RecordId}, HASH32=0x{MaxwellServerMod.MaxwellPetDefinition.MtxTypeHash:X8}).");
    }

    public void Dispose()
    {
        _busRegistration?.Dispose();
        _busRegistration = null;
        _serverMod?.Dispose();
        _serverMod = null;
        _registrar?.Dispose();
        _registrar = null;
        _bridge = null;
        _players.Clear();
    }

    private static string ConnectionKey(Player player) => $"{player.Connection.Remote}#{player.Id}";

    private static uint[] SnapshotPetIds(Player player)
    {
        lock (player.Pets)
        {
            return player.Pets.Count == 0 ? [] : [.. player.Pets.Keys];
        }
    }

    private sealed class LocalExileGatewaySink(ConcurrentDictionary<string, Player> players) : IGatewayPacketSink
    {
        public async Task SendPacketAsync(string connectionId, ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            if (!players.TryGetValue(connectionId, out var player) || player.Closed) return;
            byte[] frame = new byte[2 + payload.Length];
            frame[0] = (byte)(opcode >> 8);
            frame[1] = (byte)(opcode & 0xFF);
            payload.Span.CopyTo(frame.AsSpan(2));
            await Program.Send(player, frame, $"0x{opcode:x4} maxwell native-mods ruleset payload ({payload.Length} B)");
        }

        public async Task SendChatReplyAsync(string connectionId, string text, CancellationToken cancellationToken = default)
        {
            if (!players.TryGetValue(connectionId, out var player) || player.Closed) return;
            await Program.PlayerOf(player).Tell(text);
        }
    }

    private sealed class LocalExileMaxwellWorldBackend(
        ConcurrentDictionary<string, Player> players,
        LocalExilePetMtxRegistrar registrar,
        Action<string> log) : IMaxwellWorldBackend
    {
        public IDisposable RegisterPetMtx(CustomPetMtxDefinition pet)
        {
            registrar.ApplyToLiveCatalogs(Game.Mtx, Game.MtxEffects, log);
            return new NoOpDisposable();
        }

        public async Task GrantMtxRecordAsync(string connectionId, CustomPetMtxDefinition pet, CancellationToken cancellationToken = default)
        {
            if (!players.TryGetValue(connectionId, out var player) || player.Closed) return;
            await Program.Send(player,
                LocalExilePetMtxRegistrar.EncodeRecordUpsertPacket(pet, justReceived: true),
                $"0x0204 grant {pet.DisplayName} record {pet.RecordId} (0x{pet.MtxTypeHash:x8})");
        }

        public async Task<uint> SpawnPetEntityAsync(string connectionId, string characterName, string petMetadataPath, byte petSlot, CancellationToken cancellationToken = default)
        {
            if (!players.TryGetValue(connectionId, out var player) || player.Closed) return 0;

            double now = Game.AreaTime(player);
            var (px, py) = player.Walk.Position(now);
            int side = petSlot == CustomPetMtxDefinition.PetSlot2 ? -1 : 1;
            var spot = (X: px + side * 6, Y: py + 3);
            if (player.Layout.Walkable is { } walkable && !walkable(spot.X, spot.Y))
                spot = (px, py);

            uint entityId = player.TakeObjectId();
            uint typeHash = PoeContentHash.MurmurHash2(petMetadataPath);
            byte[] createPacket = InstanceEntities.CreateObject(entityId, typeHash, new PetObject(spot.X, spot.Y, player.Id).Blob());

            if (!await Game.SendWorldCreate(player, entityId, createPacket,
                $"0x02bb create Maxwell pet 0x{entityId:x} {petMetadataPath} (hash 0x{typeHash:x8}) at ({spot.X}, {spot.Y})"))
            {
                return 0;
            }

            if (player.Closed) return 0;

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
            if (!players.TryGetValue(connectionId, out var player) || player.Closed || petEntityId == 0) return;

            bool removed;
            lock (player.Pets)
            {
                removed = player.Pets.Remove(petEntityId);
            }

            if (!removed) return;

            await Game.SendWorldRemoval(player, petEntityId,
                $"0x{InstanceProtocol.RemoveObjectId:x4} remove Maxwell pet 0x{petEntityId:x}");
        }

        private sealed class NoOpDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
