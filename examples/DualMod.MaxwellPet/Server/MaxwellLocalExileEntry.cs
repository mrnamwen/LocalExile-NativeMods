// Thin LocalExile Gateway IMod entry point for Maxwell the Cat Pet MTX (SDK 0.2.0 / ServerBridge 0.2.0).
// Delegates Gateway lifecycle + 0x02CA / /nm / GatewayBridgeBus wiring + GameEvents.CosmeticChanged
// to LocalExileGatewayHost, and Pet MTX catalog registration / S->C 0x0204 ownership grant /
// Cosmetics (K) menu equip detection to LocalExilePetMtxRegistrar (event-driven, zero per-tick polling!).

using System;
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
    private LocalExileGatewayHost? _gatewayHost;
    private LocalExilePetMtxRegistrar? _registrar;
    private MaxwellServerMod? _serverMod;
    private Action<string>? _log;

    public void Load(IModContext mod)
    {
        _log = mod.Log;
        _registrar = new LocalExilePetMtxRegistrar();

        _gatewayHost = LocalExileGatewayHost.Create(mod, builder => builder
            .WithBridgeOptions(new ClientBridgeOptions
            {
                ServerVersion = "LocalExile-3.29.3d",
                HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId
            })
            .WithBus(
                attach: true,
                onDispatchError: (owner, ex) => mod.Log($"[gateway-bus] participant '{owner}' failed: {ex.Message}"))
            .OnGatewayContext((connId, character, _) =>
            {
                if (!string.IsNullOrWhiteSpace(character))
                    _serverMod?.BindConnection(connId, character);
            })
            .OnPlayerEntered(async ctx =>
            {
                await SyncGatewayPlayerAsync((Player)ctx.Player, ctx.ConnectionId, ctx.CharacterName, isAreaEntry: true)
                    .ConfigureAwait(false);
            })
            .OnCosmeticChanged(async evt =>
            {
                if (evt.IsSkillCosmetic)
                    return;

                await SyncGatewayPlayerAsync((Player)evt.Player, evt.ConnectionId, evt.CharacterName, isAreaEntry: false)
                    .ConfigureAwait(false);
            })
            .OnPlayerLeaving((ctx, _) =>
            {
                if (((Player)ctx.Player).Closed)
                {
                    _registrar?.ClearConnection(ctx.ConnectionId);
                    _serverMod?.OnPlayerLeft(ctx.ConnectionId);
                }

                return Task.CompletedTask;
            }));

        var backend = new LocalExileMaxwellWorldBackend(_gatewayHost, _registrar, mod.Log);
        _serverMod = new MaxwellServerMod(_gatewayHost.Bridge, backend, _registrar);
        _serverMod.Attach();

        // /maxwell command (works for both NativeMods clients and stock vanilla clients)
        _gatewayHost.RegisterCommand("maxwell",
            "/maxwell [panel|summon [1|2]|dismiss|toggle|spin <0..720>|float <0..60>|music <on|off|mute|unmute|vol 0..1>|dance|status]",
            async (ctx, args) =>
            {
                string reply = await _serverMod.HandleMaxwellCommandAsync(ctx.ConnectionId, ctx.CharacterName, args)
                    .ConfigureAwait(false);
                if (!string.IsNullOrEmpty(reply))
                    await global::Client.For((Player)ctx.Player).SendChat(reply).ConfigureAwait(false);
            });

        mod.Log($"Loaded Maxwell the Cat Pet MTX via LocalExileGatewayHost + OnCosmeticChanged (RecordId={MaxwellServerMod.MaxwellPetDefinition.RecordId}, HASH32=0x{MaxwellServerMod.MaxwellPetDefinition.MtxTypeHash:X8}).");
    }

    public void Dispose()
    {
        _serverMod?.Dispose();
        _serverMod = null;
        _registrar?.Dispose();
        _registrar = null;
        _gatewayHost?.Dispose();
        _gatewayHost = null;
    }

    private async Task SyncGatewayPlayerAsync(Player player, string connId, string character, bool isAreaEntry)
    {
        if (player.Closed || _registrar is null || _serverMod is null || _log is null)
            return;

        _registrar.ApplyToLiveCatalogs(Game.Mtx, Game.MtxEffects, _log);
        if (isAreaEntry)
        {
            await _serverMod.OnPlayerEnteredAsync(connId, character).ConfigureAwait(false);
        }

        _registrar.ObserveLiveGatewayPlayer(connId, character, Game.WornCosmetics(player), player.Pets);
        await _serverMod.FlushPendingCosmeticSyncAsync().ConfigureAwait(false);
        await _serverMod.ReconcileCosmeticAndPetStateAsync(
            connId,
            character,
            _registrar.IsEquipped(connId, MaxwellServerMod.MaxwellPetDefinition.MtxTypeHash),
            SnapshotPetIds(player)).ConfigureAwait(false);
    }

    private static uint[] SnapshotPetIds(Player player)
    {
        lock (player.Pets)
        {
            return player.Pets.Count == 0 ? [] : [.. player.Pets.Keys];
        }
    }

    private sealed class LocalExileMaxwellWorldBackend(
        LocalExileGatewayHost gatewayHost,
        LocalExilePetMtxRegistrar registrar,
        Action<string> log) : IMaxwellWorldBackend
    {
        private bool TryGetActivePlayer(string connectionId, out Player player)
        {
            if (gatewayHost.TryGetPlayer(connectionId, out object? obj) && obj is Player p && !p.Closed)
            {
                player = p;
                return true;
            }

            player = null!;
            return false;
        }

        public IDisposable RegisterPetMtx(CustomPetMtxDefinition pet)
        {
            registrar.ApplyToLiveCatalogs(Game.Mtx, Game.MtxEffects, log);
            return new NoOpDisposable();
        }

        public async Task GrantMtxRecordAsync(string connectionId, CustomPetMtxDefinition pet, CancellationToken cancellationToken = default)
        {
            if (!TryGetActivePlayer(connectionId, out var player))
                return;

            await Program.Send(player,
                LocalExilePetMtxRegistrar.EncodeRecordUpsertPacket(pet, justReceived: true),
                $"0x0204 grant {pet.DisplayName} record {pet.RecordId} (0x{pet.MtxTypeHash:x8})");
        }

        public async Task<uint> SpawnPetEntityAsync(string connectionId, string characterName, string petMetadataPath, byte petSlot, CancellationToken cancellationToken = default)
        {
            if (!TryGetActivePlayer(connectionId, out var player))
                return 0;

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
            if (!TryGetActivePlayer(connectionId, out var player) || petEntityId == 0)
                return;

            bool removed;
            lock (player.Pets)
            {
                removed = player.Pets.Remove(petEntityId);
            }

            if (!removed)
                return;

            await Game.SendWorldRemoval(player, petEntityId,
                $"0x{InstanceProtocol.RemoveObjectId:x4} remove Maxwell pet 0x{petEntityId:x}");
        }

        private sealed class NoOpDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
