// Thin LocalExile Gateway IMod entry point for Orb of Siphoning (SDK 1.0.0 / LocalExile Core Hooks Part 2b).
// Delegates Gateway lifecycle + 0x02CA / /nm / GatewayBridgeBus wiring to LocalExileGatewayHost,
// custom currency runtime overlays & natural world drop-pool registration to LocalExile Part 2b public APIs
// (ItemCatalog.RegisterOverlay + DropDataProvider.Default.RegisterOverlay — ZERO SQLite mutation, ZERO reflection),
// C->S 0x0071/0x0072 interception to LocalExileCurrencyRegistrar, and live Gateway inventory/affix transactions to
// LocalExileItemAffixService + LocalExileGatewayTransactions.

#nullable enable

using System;
using System.Collections.Generic;
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
    private readonly List<IDisposable> _overlayHandles = [];
    private LocalExileGatewayHost? _gatewayHost;
    private LocalExileCurrencyRegistrar? _registrar;
    private SiphonServerMod? _serverMod;
    private StatDescriptions? _statDescriptions;

    public void Load(IModContext mod)
    {
        ArgumentNullException.ThrowIfNull(mod);

        string owner = mod.Manifest.Id;
        SiphonDropSettings dropSettings = LoadDropSettings(mod);

        _statDescriptions = TryLoadStatDescriptions(mod.Log);
        _registrar = new LocalExileCurrencyRegistrar();

        // Register Empty & Charged Orb of Siphoning via LocalExile Part 2b public ItemCatalog runtime overlays
        // (visible to every ItemCatalog instance — Standard, HardMode, CharacterSaves — with zero SQLite mutation).
        _overlayHandles.Add(RegisterCatalogOverlays(owner, mod.Log));

        // Register Empty Orb of Siphoning into LocalExile's natural currency drop pool (preserved across /reload,
        // automatically cleaned up on mod unload via GameEventOwners).
        if (RegisterNaturalDropOverlay(owner, dropSettings, mod.Log) is { } dropHandle)
            _overlayHandles.Add(dropHandle);

        _gatewayHost = LocalExileGatewayHost.Create(mod, builder => builder
            .WithBridgeOptions(new ClientBridgeOptions
            {
                ServerVersion = "LocalExile-3.29.3d",
                HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId
            })
            .WithBus(
                attach: true,
                onDispatchError: (busOwner, ex) => mod.Log($"[gateway-bus] participant '{busOwner}' failed: {ex.Message}"))
            .OnGatewayContext((connId, character, _) =>
            {
                if (!string.IsNullOrWhiteSpace(character))
                    _serverMod?.BindConnection(connId, character);
            })
            .OnPlayerEntered(ctx =>
            {
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
        var backend = new LocalExileSiphonWorldBackend(_gatewayHost, affixService);
        _serverMod = new SiphonServerMod(_gatewayHost.Bridge, backend, _registrar);
        _serverMod.Attach();

        // /siphon command (GM grant empty orbs, help, and vanilla-client /siphon pick|cancel fallback)
        _gatewayHost.RegisterCommand(
            "siphon",
            "/siphon [give [1..20]|pick <1..N>|cancel|help]",
            async (ctx, args) =>
            {
                var player = (Player)ctx.Player;
                string reply = await _serverMod.HandleSiphonCommandAsync(ctx.ConnectionId, ctx.CharacterName, args);
                if (!string.IsNullOrEmpty(reply))
                    await global::Client.For(player).SendChat(reply);
            });

        // C->S 0x0071 (UseItemOn): claim ONLY our registered custom currency TypeHashes; return false for all stock currencies.
        _gatewayHost.RegisterPacket(InstanceItems.UseItemOnId, async (ctx, bytes) =>
        {
            var player = (Player)ctx.Player;
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
            var inv = Game.ItemsOf(player);
            var stash = Game.StashOf(player);
            return await _registrar.HandleUseItemTypeOnPacketAsync(
                ctx.ConnectionId,
                ctx.CharacterName,
                bytes,
                typeHash => LocalExileGatewayTransactions.LocateCurrencyStack(inv, stash, typeHash),
                gatewayPlayer: player);
        });

        mod.Log($"Loaded Orb of Siphoning via LocalExileGatewayHost + Part 2b Overlays (Empty=0x{SiphonServerMod.EmptyOrbDefinition.TypeHash:X8}, Charged=0x{SiphonServerMod.ChargedOrbDefinition.TypeHash:X8}, NaturalDrops={dropSettings.EnableNaturalDrops}).");
    }

    public void Dispose()
    {
        _serverMod?.Dispose();
        _serverMod = null;
        _registrar?.Dispose();
        _registrar = null;
        _gatewayHost?.Dispose();
        _gatewayHost = null;

        for (int i = _overlayHandles.Count - 1; i >= 0; i--)
            _overlayHandles[i].Dispose();
        _overlayHandles.Clear();
    }

    /// <summary>
    /// Registers <see cref="SiphonServerMod.EmptyOrbDefinition"/> and <see cref="SiphonServerMod.ChargedOrbDefinition"/>
    /// into LocalExile's shared <see cref="ItemCatalog"/> runtime overlay store (if not already present), bound to
    /// <see cref="GameEventOwners"/> under <paramref name="owner"/>.
    /// </summary>
    public static IDisposable RegisterCatalogOverlays(string owner, Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        var handles = new List<IDisposable>(2);

        try
        {
            RegisterCurrencyOverlayIfAbsent(owner, SiphonServerMod.EmptyOrbDefinition, handles, log);
            RegisterCurrencyOverlayIfAbsent(owner, SiphonServerMod.ChargedOrbDefinition, handles, log);
            return new CompositeDisposable(handles);
        }
        catch
        {
            for (int i = handles.Count - 1; i >= 0; i--)
                handles[i].Dispose();
            throw;
        }
    }

    /// <summary>
    /// Registers the Empty Orb of Siphoning into <see cref="DropDataProvider.Default"/>'s runtime
    /// <see cref="DropPoolOverlays"/> (<see cref="DropPoolCategory.Currency"/>) according to <paramref name="settings"/>,
    /// owned by <see cref="GameEventOwners"/> under <paramref name="owner"/> so <c>/reload</c> preserves it and mod
    /// unload cleanly removes it. Returns <c>null</c> when <see cref="SiphonDropSettings.EnableNaturalDrops"/> is <c>false</c>.
    /// </summary>
    public static IDisposable? RegisterNaturalDropOverlay(
        string owner,
        SiphonDropSettings settings,
        Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        if (!settings.EnableNaturalDrops)
        {
            log?.Invoke("[siphon] Natural world currency drops disabled by settings.json (use '/siphon give' to grant orbs).");
            return null;
        }

        DropSourceMask sourceMask = ToDropSourceMask(settings.ResolveSources());
        DropMonsterFilter monsterFilter = ToDropMonsterFilter(settings.ResolveMonsterRarity());
        var entry = new DropPoolEntry(
            DropPoolCategory.Currency,
            SiphonServerMod.EmptyOrbDefinition.BaseItemTypeId,
            settings.DropWeight)
        {
            MinAreaLevel = settings.MinAreaLevel,
            MaxAreaLevel = settings.MaxAreaLevel,
            Sources = sourceMask,
            Monsters = monsterFilter,
        };

        IDisposable handle = DropDataProvider.Default.RegisterOverlay(owner, entry);
        log?.Invoke(
            $"[siphon] Registered natural Currency drop-pool overlay for '{entry.ItemId}' (HASH32=0x{SiphonServerMod.EmptyOrbDefinition.TypeHash:X8}, Weight={entry.Weight}, AreaLevel={entry.MinAreaLevel}..{(entry.MaxAreaLevel > 0 ? entry.MaxAreaLevel.ToString() : "∞")}, Sources={entry.Sources}, Monsters={entry.Monsters}).");
        return handle;
    }

    private static void RegisterCurrencyOverlayIfAbsent(
        string owner,
        CustomCurrencyItemDefinition def,
        List<IDisposable> handles,
        Action<string>? log)
    {
        def.Validate();

        ItemCatalog? items = Program.Items;
        if (items is not null && (items.ById(def.BaseItemTypeId) is not null || items.ByHash(def.TypeHash) is not null))
            return;

        var baseType = new ItemCatalog.BaseType(
            Row: -1,
            Id: def.BaseItemTypeId,
            Name: def.DisplayName,
            Hash: def.TypeHash,
            Width: def.Width,
            Height: def.Height,
            VisualIdentity: def.VisualIdentityHash16,
            Components: def.Components,
            MaxCharges: 0,
            Class: def.ItemClassName,
            Stance: 0xFF,
            DropLevel: def.DropLevel,
            MaxStack: def.MaxStackSize,
            FullStackRow: -1)
        {
            GroundSize = 1,
            ModDomain = def.ModDomain,
            ClassRow = def.ItemClassRow,
            Unmodifiable = false,
            CanBeFractured = false,
            CanBeMirrored = false,
            CanBeCorrupted = false,
        };

        var options = new ItemCatalog.BaseTypeOverlayOptions(
            VisualIdentityId: def.VisualIdentityId,
            CurrencyAction: def.CurrencyAction);

        handles.Add(ItemCatalog.RegisterOverlay(owner, baseType, options));
        log?.Invoke(
            $"[siphon] Registered ItemCatalog overlay '{def.DisplayName}' (Id={def.BaseItemTypeId}, HASH32=0x{def.TypeHash:X8}, VisualHASH16=0x{def.VisualIdentityHash16:X4}, Stack={def.MaxStackSize}, StoredMods={def.SupportsStoredMods}).");
    }

    private static SiphonDropSettings LoadDropSettings(IModContext mod)
    {
        try
        {
            SiphonDropSettings bound = mod.Settings<SiphonDropSettings>();
            bound.Validate();
            return bound;
        }
        catch (Exception ex)
        {
            mod.Log($"[siphon] Failed to bind drop settings from settings.json, using defaults: {ex.Message}");
            return SiphonDropSettings.Default;
        }
    }

    private static DropSourceMask ToDropSourceMask(SiphonDropSource sources)
    {
        DropSourceMask mask = DropSourceMask.None;
        if ((sources & SiphonDropSource.MonsterKill) != 0) mask |= DropSourceMask.MonsterKill;
        if ((sources & SiphonDropSource.Container) != 0) mask |= DropSourceMask.Container;
        if ((sources & SiphonDropSource.Strongbox) != 0) mask |= DropSourceMask.Strongbox;
        if ((sources & SiphonDropSource.BossBonus) != 0) mask |= DropSourceMask.BossBonus;
        if ((sources & SiphonDropSource.MapCurrency) != 0) mask |= DropSourceMask.MapCurrency;
        return mask;
    }

    private static DropMonsterFilter ToDropMonsterFilter(SiphonMonsterRarity rarity)
    {
        DropMonsterFilter filter = DropMonsterFilter.None;
        if ((rarity & SiphonMonsterRarity.Normal) != 0) filter |= DropMonsterFilter.Normal;
        if ((rarity & SiphonMonsterRarity.Magic) != 0) filter |= DropMonsterFilter.Magic;
        if ((rarity & SiphonMonsterRarity.Rare) != 0) filter |= DropMonsterFilter.Rare;
        if ((rarity & SiphonMonsterRarity.Unique) != 0) filter |= DropMonsterFilter.Unique;
        if ((rarity & SiphonMonsterRarity.Boss) != 0) filter |= DropMonsterFilter.Boss;
        return filter;
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

    private sealed class CompositeDisposable(IReadOnlyList<IDisposable> handles) : IDisposable
    {
        private IReadOnlyList<IDisposable>? _handles = handles;

        public void Dispose()
        {
            IReadOnlyList<IDisposable>? toDispose = Interlocked.Exchange(ref _handles, null);
            if (toDispose is null)
                return;

            for (int i = toDispose.Count - 1; i >= 0; i--)
                toDispose[i].Dispose();
        }
    }

    private sealed class LocalExileSiphonWorldBackend(
        LocalExileGatewayHost gatewayHost,
        LocalExileItemAffixService affixService) : ISiphonWorldBackend
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

        public IDisposable RegisterCurrency(CustomCurrencyItemDefinition definition) =>
            new NoOpDisposable(); // Registered at load via ItemCatalog.RegisterOverlay (shared across all ItemCatalogs).

        public async Task<(bool Success, string Message, uint GrantedItemId)> GrantEmptyOrbsAsync(
            string connectionId,
            string characterName,
            ushort count,
            CancellationToken cancellationToken = default)
        {
            if (!TryGetActivePlayer(connectionId, out var player))
                return (false, "Player connection is not active.", 0);

            var catalog = Game.ItemsFor(player);
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
