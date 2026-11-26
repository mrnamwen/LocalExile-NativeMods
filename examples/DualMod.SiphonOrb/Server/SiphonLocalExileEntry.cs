// Thin LocalExile Gateway IMod entry point for Orb of Siphoning (SDK 0.2.0 / ServerBridge 0.2.0).
// Delegates Gateway lifecycle + 0x02CA / /nm / GatewayBridgeBus wiring to LocalExileGatewayHost,
// custom currency catalog registration & C->S 0x0071/0x0072 interception to LocalExileCurrencyRegistrar,
// and live Gateway inventory/affix transactions to LocalExileItemAffixService + LocalExileGatewayTransactions.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DualMod.SiphonOrb.Server;
using DualMod.SiphonOrb.Shared;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.ServerBridge;

public sealed class SiphonLocalExileEntry : IMod, IDisposable
{
    private LocalExileGatewayHost? _gatewayHost;
    private LocalExileCurrencyRegistrar? _registrar;
    private SiphonServerMod? _serverMod;
    private StatDescriptions? _statDescriptions;

    public void Load(IModContext mod)
    {
        _statDescriptions = TryLoadStatDescriptions(mod.Log);
        _registrar = new LocalExileCurrencyRegistrar();

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
            .OnPlayerEntered(ctx =>
            {
                var player = (Player)ctx.Player;
                _registrar?.ApplyToLiveCatalog(Program.Items, mod.Log);
                _registrar?.ApplyToLiveCatalog(Game.ItemsFor(player), mod.Log);
                _serverMod?.BindConnection(ctx.ConnectionId, ctx.CharacterName);
                return Task.CompletedTask;
            })
            .OnPlayerLeaving((ctx, _) =>
            {
                if (((Player)ctx.Player).Closed)
                    _serverMod?.OnPlayerLeft(ctx.ConnectionId);
                return Task.CompletedTask;
            }));

        var affixService = new LocalExileItemAffixService(_statDescriptions, Program.Rolls);
        var backend = new LocalExileSiphonWorldBackend(_gatewayHost, _registrar, affixService, mod.Log);
        _serverMod = new SiphonServerMod(_gatewayHost.Bridge, backend, _registrar);
        _serverMod.Attach();

        // Register both custom currencies immediately at mod load (before listener.Start) so
        // CharacterSaves.FromSaved recognizes saved Empty & Charged Orbs on relog/restart.
        _registrar.ApplyToLiveCatalog(Program.Items, mod.Log);

        // /siphon command (grant empty orbs, help, and vanilla-client /siphon pick|cancel fallback)
        _gatewayHost.RegisterCommand(
            "siphon",
            "/siphon [give [1..20]|pick <1..N>|cancel|help]",
            async (ctx, args) =>
            {
                var player = (Player)ctx.Player;
                _registrar.ApplyToLiveCatalog(Game.ItemsFor(player), mod.Log);
                string reply = await _serverMod.HandleSiphonCommandAsync(ctx.ConnectionId, ctx.CharacterName, args);
                if (!string.IsNullOrEmpty(reply))
                    await global::Client.For(player).SendChat(reply);
            });

        // C->S 0x0071 (UseItemOn): claim ONLY our registered custom currency TypeHashes; return false for all stock currencies.
        _gatewayHost.RegisterPacket(InstanceItems.UseItemOnId, async (ctx, bytes) =>
        {
            var player = (Player)ctx.Player;
            _registrar.ApplyToLiveCatalog(Game.ItemsFor(player), mod.Log);

            var inv = Game.ItemsOf(player);
            var stash = Game.StashOf(player);
            return await _registrar.HandleUseItemOnPacketAsync(
                ctx.ConnectionId,
                ctx.CharacterName,
                bytes,
                (invId, itemId) => LocalExileGatewayTransactions.LocateGatewayItem(inv, stash, invId, itemId)?.Item is Item item
                    ? item.TypeHash
                    : null,
                gatewayPlayer: player);
        });

        // C->S 0x0072 (UseItemTypeOn, Shift-repeat): claim ONLY our registered custom currency TypeHashes.
        _gatewayHost.RegisterPacket(InstanceItems.UseItemTypeOnId, async (ctx, bytes) =>
        {
            var player = (Player)ctx.Player;
            _registrar.ApplyToLiveCatalog(Game.ItemsFor(player), mod.Log);

            var inv = Game.ItemsOf(player);
            var stash = Game.StashOf(player);
            return await _registrar.HandleUseItemTypeOnPacketAsync(
                ctx.ConnectionId,
                ctx.CharacterName,
                bytes,
                typeHash => LocalExileGatewayTransactions.LocateCurrencyStack(inv, stash, typeHash),
                gatewayPlayer: player);
        });

        mod.Log($"Loaded Orb of Siphoning via LocalExileGatewayHost + LocalExileCurrencyRegistrar (Empty=0x{SiphonServerMod.EmptyOrbDefinition.TypeHash:X8}, Charged=0x{SiphonServerMod.ChargedOrbDefinition.TypeHash:X8}).");
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

    private static StatDescriptions? TryLoadStatDescriptions(Action<string> log)
    {
        try
        {
            return StatDescriptions.Load();
        }
        catch (Exception ex)
        {
            log($"[siphon] StatDescriptions unavailable, falling back to humanized stat names: {ex.Message}");
            return null;
        }
    }

    private sealed class LocalExileSiphonWorldBackend(
        LocalExileGatewayHost gatewayHost,
        LocalExileCurrencyRegistrar registrar,
        LocalExileItemAffixService affixService,
        Action<string> log) : ISiphonWorldBackend
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

        public IDisposable RegisterCurrency(CustomCurrencyItemDefinition definition)
        {
            registrar.ApplyToLiveCatalog(Program.Items, log);
            return new NoOpDisposable();
        }

        public async Task<(bool Success, string Message, uint GrantedItemId)> GrantEmptyOrbsAsync(
            string connectionId,
            string characterName,
            ushort count,
            CancellationToken cancellationToken = default)
        {
            if (!TryGetActivePlayer(connectionId, out var player))
                return (false, "Player connection is not active.", 0);

            var catalog = Game.ItemsFor(player);
            registrar.ApplyToLiveCatalog(catalog, log);
            var def = SiphonServerMod.EmptyOrbDefinition;
            ushort clamped = Math.Clamp(count, (ushort)1, def.MaxStackSize);

            GatewayGrantResult result = await LocalExileGatewayTransactions.GrantItemsAsync(
                player,
                catalog,
                Game.ItemsOf(player),
                def,
                clamped,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!result.Success)
                return (false, result.Message, 0);

            uint firstId = result.GrantedItemIds.Count > 0 ? result.GrantedItemIds[0] : 0;
            return (true, result.Message, firstId);
        }

        public Task<ItemAffixView?> InspectItemAsync(
            string connectionId,
            uint inventoryId,
            uint itemId,
            CancellationToken cancellationToken = default)
        {
            if (!TryGetActivePlayer(connectionId, out var player))
                return Task.FromResult<ItemAffixView?>(null);

            GatewayItemLocation? located = LocalExileGatewayTransactions.LocateGatewayItem(
                Game.ItemsOf(player),
                Game.StashOf(player),
                inventoryId,
                itemId);
            if (located is null)
                return Task.FromResult<ItemAffixView?>(null);

            var catalog = Game.ItemsFor(player);
            return Task.FromResult<ItemAffixView?>(affixService.InspectGatewayItem(
                inventoryId,
                itemId,
                located.Item,
                catalog,
                itemRolls: Game.RollsFor(player)));
        }

        public bool IsItemEquipped(string connectionId, uint inventoryId, uint itemId)
        {
            if (!TryGetActivePlayer(connectionId, out var player))
                return false;

            return LocalExileGatewayTransactions.LocateGatewayItem(
                Game.ItemsOf(player),
                Game.StashOf(player),
                inventoryId,
                itemId)?.IsWorn ?? false;
        }

        public Task<StoredAffixPayload?> ReadStoredAffixAsync(
            string connectionId,
            uint currencyInventoryId,
            uint currencyItemId,
            CancellationToken cancellationToken = default)
        {
            if (!TryGetActivePlayer(connectionId, out var player))
                return Task.FromResult<StoredAffixPayload?>(null);

            GatewayItemLocation? located = LocalExileGatewayTransactions.LocateGatewayItem(
                Game.ItemsOf(player),
                Game.StashOf(player),
                currencyInventoryId,
                currencyItemId);
            if (located is null)
                return Task.FromResult<StoredAffixPayload?>(null);

            return Task.FromResult(affixService.ReadStoredAffixFromGatewayItem(located.Item, Game.ItemsFor(player)));
        }

        public async Task<SiphonStealCommitResult> CommitStealAsync(
            string connectionId,
            string characterName,
            uint currencyInventoryId,
            uint currencyItemId,
            uint targetInventoryId,
            uint targetItemId,
            int affixIndex,
            CancellationToken cancellationToken = default)
        {
            if (!TryGetActivePlayer(connectionId, out var player))
                return new SiphonStealCommitResult(false, "Player connection is no longer active.");

            var catalog = Game.ItemsFor(player);
            registrar.ApplyToLiveCatalog(catalog, log);
            var items = Game.ItemsOf(player);
            var stash = Game.StashOf(player);

            GatewayItemLocation? usedLoc = LocalExileGatewayTransactions.LocateGatewayItem(
                items,
                stash,
                currencyInventoryId,
                currencyItemId);
            if (usedLoc?.Item is not Item usedItem || usedItem.TypeHash != SiphonServerMod.EmptyOrbDefinition.TypeHash)
                return new SiphonStealCommitResult(false, "Orb of Siphoning is no longer in your inventory.");

            GatewayStealResult result = await affixService.StealAffixOnGatewayItem(
                player,
                catalog,
                items,
                stash,
                currencyInventoryId,
                currencyItemId,
                targetInventoryId,
                targetItemId,
                affixIndex,
                SiphonServerMod.ChargedOrbDefinition,
                itemRolls: Game.RollsFor(player),
                chargedCustomName: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return new SiphonStealCommitResult(
                Success: result.Success,
                Error: result.Error,
                StolenAffix: result.StolenAffix,
                CreatedChargedItemId: result.CreatedChargedItemId,
                UpdatedSourceView: result.UpdatedSourceView);
        }

        public async Task<SiphonImbueCommitResult> CommitImbueAsync(
            string connectionId,
            string characterName,
            uint currencyInventoryId,
            uint currencyItemId,
            uint targetInventoryId,
            uint targetItemId,
            int replaceIndex,
            CancellationToken cancellationToken = default)
        {
            if (!TryGetActivePlayer(connectionId, out var player))
                return new SiphonImbueCommitResult(false, "Player connection is no longer active.");

            var catalog = Game.ItemsFor(player);
            var items = Game.ItemsOf(player);
            var stash = Game.StashOf(player);

            GatewayItemLocation? usedLoc = LocalExileGatewayTransactions.LocateGatewayItem(
                items,
                stash,
                currencyInventoryId,
                currencyItemId);
            if (usedLoc?.Item is not Item usedItem || usedItem.TypeHash != SiphonServerMod.ChargedOrbDefinition.TypeHash)
                return new SiphonImbueCommitResult(false, "Charged Orb of Siphoning is no longer in your inventory.");

            GatewayReplaceResult result = await affixService.ReplaceAffixOnGatewayItem(
                player,
                catalog,
                items,
                stash,
                currencyInventoryId,
                currencyItemId,
                targetInventoryId,
                targetItemId,
                replaceIndex,
                itemRolls: Game.RollsFor(player),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return new SiphonImbueCommitResult(
                Success: result.Success,
                Error: result.Error,
                ReplacedAffix: result.ReplacedAffix,
                AppliedAffix: result.AppliedAffix,
                UpdatedTargetView: result.UpdatedTargetView);
        }

        public async Task TellPlayerAsync(string connectionId, string message, CancellationToken cancellationToken = default)
        {
            if (!TryGetActivePlayer(connectionId, out var player))
                return;

            await global::Client.For(player).SendChat(message, cancellationToken).ConfigureAwait(false);
        }

        private sealed class NoOpDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
