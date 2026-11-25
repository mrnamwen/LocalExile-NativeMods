// Thin LocalExile Gateway IMod entry point for Orb of Siphoning.
// Delegates custom currency catalog registration & C->S 0x0071/0x0072 interception to
// LocalExileCurrencyRegistrar and affix inspection/steal/imbue rules to LocalExileItemAffixService
// in LocalExile.Client.ServerBridge so ZERO edits to LocalExile core are required.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    private readonly ConcurrentDictionary<string, Player> _players = new(StringComparer.Ordinal);
    private ClientBridgeHost? _bridge;
    private LocalExileCurrencyRegistrar? _registrar;
    private SiphonServerMod? _serverMod;
    private StatDescriptions? _statDescriptions;

    public void Load(IModContext mod)
    {
        string id = mod.Manifest.Id;

        _bridge = new ClientBridgeHost(new LocalExileGatewaySink(_players), new ClientBridgeOptions
        {
            ServerVersion = "LocalExile-3.29.3d",
            HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId
        });

        _statDescriptions = TryLoadStatDescriptions(mod.Log);
        _registrar = new LocalExileCurrencyRegistrar();
        var affixService = new LocalExileItemAffixService(_statDescriptions);
        var backend = new LocalExileSiphonWorldBackend(_players, _registrar, affixService, mod.Log);
        _serverMod = new SiphonServerMod(_bridge, backend, _registrar);
        _serverMod.Attach();

        // Register both custom currencies immediately at mod load (before listener.Start) so
        // CharacterSaves.FromSaved recognizes saved Empty & Charged Orbs on relog/restart.
        _registrar.ApplyToLiveCatalog(Program.Items, mod.Log);

        // /siphon command (grant empty orbs, help, and vanilla-client /siphon pick|cancel fallback)
        GameEvents.Command(id, "siphon",
            "/siphon [give [1..20]|pick <1..N>|cancel|help]",
            async (player, args) =>
            {
                string connId = ConnectionKey(player);
                _players[connId] = player;
                _registrar.ApplyToLiveCatalog(Game.ItemsFor(player), mod.Log);
                string reply = await _serverMod.HandleSiphonCommandAsync(connId, Program.CharacterName(player), args);
                if (!string.IsNullOrEmpty(reply))
                    await Program.PlayerOf(player).Tell(reply);
            });

        // /nm chat fallback transport (C->S)
        GameEvents.Command(id, "nm", "/nm <native-mods-envelope>", async (player, args) =>
        {
            string connId = ConnectionKey(player);
            _players[connId] = player;
            _serverMod.BindConnection(connId, Program.CharacterName(player));
            await _bridge.HandleIncomingChatCommandAsync(connId, "/nm " + string.Join(' ', args));
        });

        // C->S 0x02CA host ruleset transport (unclaimed by stock modules for ruleset 0xA0)
        GameEvents.Packet(IOpcodeRegistry.ClientRulesetOpcode).Add(id, async (player, bytes) =>
        {
            if (bytes.Length <= 2) return false;
            string connId = ConnectionKey(player);
            _players[connId] = player;
            _serverMod.BindConnection(connId, Program.CharacterName(player));
            return await _bridge.HandleIncomingPacketAsync(connId, IOpcodeRegistry.ClientRulesetOpcode, bytes.AsMemory(2));
        });

        // C->S 0x0071 (UseItemOn): claim ONLY our registered custom currency TypeHashes; return false for all stock currencies.
        GameEvents.Packet(InstanceItems.UseItemOnId).Add(id, async (player, bytes) =>
        {
            string connId = ConnectionKey(player);
            string character = Program.CharacterName(player);
            _players[connId] = player;
            _registrar.ApplyToLiveCatalog(Game.ItemsFor(player), mod.Log);

            var inv = Game.ItemsOf(player);
            var stash = Game.StashOf(player);
            return await _registrar.HandleUseItemOnPacketAsync(
                connId,
                character,
                bytes,
                (invId, itemId) => Game.Locate(inv, stash, invId, itemId).Item?.Item.TypeHash,
                gatewayPlayer: player);
        });

        // C->S 0x0072 (UseItemTypeOn, Shift-repeat): claim ONLY our registered custom currency TypeHashes.
        GameEvents.Packet(InstanceItems.UseItemTypeOnId).Add(id, async (player, bytes) =>
        {
            string connId = ConnectionKey(player);
            string character = Program.CharacterName(player);
            _players[connId] = player;
            _registrar.ApplyToLiveCatalog(Game.ItemsFor(player), mod.Log);

            var inv = Game.ItemsOf(player);
            var stash = Game.StashOf(player);
            return await _registrar.HandleUseItemTypeOnPacketAsync(
                connId,
                character,
                bytes,
                typeHash => LocateCurrencyStack(inv, stash, typeHash),
                gatewayPlayer: player);
        });

        GameEvents.PlayerEntered.Add(id, player =>
        {
            string connId = ConnectionKey(player);
            _players[connId] = player;
            _registrar.ApplyToLiveCatalog(Program.Items, mod.Log);
            _registrar.ApplyToLiveCatalog(Game.ItemsFor(player), mod.Log);
            _serverMod.BindConnection(connId, Program.CharacterName(player));
            return Task.CompletedTask;
        });

        GameEvents.PlayerLeaving.Add(id, (player, reason) =>
        {
            if (player.Closed)
            {
                string connId = ConnectionKey(player);
                _players.TryRemove(connId, out _);
                _serverMod.OnPlayerLeft(connId);
                _bridge.RemoveSession(connId);
            }
            return Task.CompletedTask;
        });

        mod.Log($"Loaded Orb of Siphoning via LocalExileCurrencyRegistrar (Empty=0x{SiphonServerMod.EmptyOrbDefinition.TypeHash:X8}, Charged=0x{SiphonServerMod.ChargedOrbDefinition.TypeHash:X8}).");
    }

    public void Dispose()
    {
        _serverMod?.Dispose();
        _serverMod = null;
        _registrar?.Dispose();
        _registrar = null;
        _bridge = null;
        _players.Clear();
    }

    private static string ConnectionKey(Player player) => $"{player.Connection.Remote}#{player.Id}";

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

    private static (uint InventoryId, uint ItemId)? LocateCurrencyStack(PlayerInventories items, AccountStash stash, uint typeHash)
    {
        InventoryItem? mainHit = items.Items(PlayerInventories.MainInventory).FirstOrDefault(i => i.Item.TypeHash == typeHash);
        if (mainHit is not null)
            return (PlayerInventories.InventoryId(PlayerInventories.MainInventory), mainHit.Id);

        for (int tab = 0; tab < stash.Count; tab++)
        {
            if (stash.IsPublic(tab))
                continue;
            InventoryItem? stashHit = stash.Inventory(tab).Items.FirstOrDefault(i => i.Item.TypeHash == typeHash);
            if (stashHit is not null)
                return (stash.InventoryOf(tab), stashHit.Id);
        }

        return null;
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
            await Program.Send(player, frame, $"0x{opcode:x4} siphon native-mods ruleset payload ({payload.Length} B)");
        }

        public async Task SendChatReplyAsync(string connectionId, string text, CancellationToken cancellationToken = default)
        {
            if (!players.TryGetValue(connectionId, out var player) || player.Closed) return;
            await Program.PlayerOf(player).Tell(text);
        }
    }

    private sealed class LocalExileSiphonWorldBackend(
        ConcurrentDictionary<string, Player> players,
        LocalExileCurrencyRegistrar registrar,
        LocalExileItemAffixService affixService,
        Action<string> log) : ISiphonWorldBackend
    {
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
            if (!players.TryGetValue(connectionId, out var player) || player.Closed)
                return (false, "Player connection is not active.", 0);

            var catalog = Game.ItemsFor(player);
            registrar.ApplyToLiveCatalog(catalog, log);
            var def = SiphonServerMod.EmptyOrbDefinition;

            if (catalog.ByHash(def.TypeHash) is not { } baseType)
                return (false, "Orb of Siphoning is not registered in the live item catalog.", 0);

            ushort clamped = Math.Clamp(count, (ushort)1, def.MaxStackSize);
            Item orbItem = ItemCatalog.Create(baseType) with { StackSize = clamped, Identified = true };
            var inv = Game.ItemsOf(player);
            var stacked = inv.AddStacked(PlayerInventories.MainInventory, orbItem, Game.ShapeOf);
            if (!stacked.Any)
                return (false, "No free space in your main inventory for Orb of Siphoning.", 0);

            List<uint> removed = [.. stacked.Changed.Select(i => i.Id)];
            List<InventoryItem> added = [.. stacked.Changed, .. stacked.Added];
            uint invId = PlayerInventories.InventoryId(PlayerInventories.MainInventory);
            await Program.Send(player,
                InstanceItems.InventoryChange(invId, inv.NextRevision(PlayerInventories.MainInventory), removed, added),
                $"0x00bf grant {clamped}x {def.DisplayName}");

            uint firstId = added.Count > 0 ? added[0].Id : 0;
            int granted = clamped - stacked.Left;
            return (true, $"Granted {granted}x {def.DisplayName} to your inventory.", firstId);
        }

        public Task<ItemAffixView?> InspectItemAsync(
            string connectionId,
            uint inventoryId,
            uint itemId,
            CancellationToken cancellationToken = default)
        {
            if (!players.TryGetValue(connectionId, out var player) || player.Closed)
                return Task.FromResult<ItemAffixView?>(null);

            var (_, located) = Game.Locate(Game.ItemsOf(player), Game.StashOf(player), inventoryId, itemId);
            if (located is null)
                return Task.FromResult<ItemAffixView?>(null);

            var catalog = Game.ItemsFor(player);
            return Task.FromResult<ItemAffixView?>(affixService.InspectGatewayItem(inventoryId, itemId, located.Item, catalog));
        }

        public bool IsItemEquipped(string connectionId, uint inventoryId, uint itemId)
        {
            if (!players.TryGetValue(connectionId, out var player) || player.Closed)
                return false;

            var (spot, _) = Game.Locate(Game.ItemsOf(player), Game.StashOf(player), inventoryId, itemId);
            return Game.Worn(spot);
        }

        public Task<StoredAffixPayload?> ReadStoredAffixAsync(
            string connectionId,
            uint currencyInventoryId,
            uint currencyItemId,
            CancellationToken cancellationToken = default)
        {
            if (!players.TryGetValue(connectionId, out var player) || player.Closed)
                return Task.FromResult<StoredAffixPayload?>(null);

            var (_, located) = Game.Locate(Game.ItemsOf(player), Game.StashOf(player), currencyInventoryId, currencyItemId);
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
            if (!players.TryGetValue(connectionId, out var player) || player.Closed)
                return new SiphonStealCommitResult(false, "Player connection is no longer active.");

            var catalog = Game.ItemsFor(player);
            registrar.ApplyToLiveCatalog(catalog, log);
            var items = Game.ItemsOf(player);
            var stash = Game.StashOf(player);

            var (usedSpot, used) = Game.Locate(items, stash, currencyInventoryId, currencyItemId);
            var (targetSpot, target) = Game.Locate(items, stash, targetInventoryId, targetItemId);
            if (used is null || used.Item.TypeHash != SiphonServerMod.EmptyOrbDefinition.TypeHash)
                return new SiphonStealCommitResult(false, "Orb of Siphoning is no longer in your inventory.");
            if (target is null)
                return new SiphonStealCommitResult(false, "Target item is no longer in your inventory.");
            if (Game.Worn(targetSpot))
                return new SiphonStealCommitResult(false, "Unequip the target item before siphoning.");

            ItemAffixView view = affixService.InspectGatewayItem(targetInventoryId, targetItemId, target.Item, catalog);
            if (LocalExileItemAffixService.ValidateStealTarget(view) is { } targetErr)
                return new SiphonStealCommitResult(false, targetErr);
            if (affixIndex < 0 || affixIndex >= view.Affixes.Count)
                return new SiphonStealCommitResult(false, "Selected modifier index is out of range.");

            ItemAffixSnapshot chosen = view.Affixes[affixIndex];
            if (chosen.IsFractured)
                return new SiphonStealCommitResult(false, "Fractured modifiers cannot be siphoned.");

            if (catalog.ByHash(SiphonServerMod.ChargedOrbDefinition.TypeHash) is not { } chargedBase)
                return new SiphonStealCommitResult(false, "Charged Orb of Siphoning is not registered in the live item catalog.");

            // Check that a free cell will be available in MainInventory for the newly created Charged Orb.
            bool orbVacatesMainCell = usedSpot.Tab is null && usedSpot.Row == PlayerInventories.MainInventory && used.Item.StackSize <= 1;
            if (!orbVacatesMainCell && items.FreeCell(PlayerInventories.MainInventory, 1, 1, Game.ShapeOf) is null)
                return new SiphonStealCommitResult(false, "Need at least 1 free inventory cell for the Charged Orb of Siphoning.");

            // Build updated source item (removing the exact explicit mod at affixIndex) and the Charged Orb.
            var updatedMods = new List<ItemMod>(target.Item.Mods.Count - 1);
            ItemMod? stolenRaw = null;
            int seenExplicit = 0;
            foreach (ItemMod m in target.Item.Mods)
            {
                if (m.Section == ModSection.Explicit)
                {
                    if (seenExplicit == affixIndex)
                    {
                        stolenRaw = m;
                        seenExplicit++;
                        continue;
                    }
                    seenExplicit++;
                }
                updatedMods.Add(m);
            }

            if (stolenRaw is null)
                return new SiphonStealCommitResult(false, "Could not locate selected explicit modifier on source item.");

            Item updatedSource = target.Item with { Mods = updatedMods };
            Item chargedOrbItem = ItemCatalog.Create(chargedBase) with
            {
                Rarity = ItemRarity.Normal,
                Identified = true,
                ItemLevel = 1,
                RequiredLevel = 0,
                StackSize = 1,
                Mods = [new ItemMod(ModSection.Enchant, stolenRaw.Hash, [.. stolenRaw.Values])]
            };

            // Atomically consume 1 Empty Orb, update source item, and place Charged Orb into MainInventory.
            InventoryItem? usedLeft = null;
            InventoryItem? changedTarget = null;
            InventoryItem? addedCharged = null;
            bool committed = Saves.Atomically(() =>
            {
                if (ItemMoves.Use(items, stash, usedSpot, targetSpot, updatedSource, held: null) is not var (left, changed, _))
                    return false;

                if (items.FreeCell(PlayerInventories.MainInventory, 1, 1, Game.ShapeOf) is not { } cell)
                    throw new InvalidOperationException("No free cell for Charged Orb of Siphoning.");

                usedLeft = left;
                changedTarget = changed;
                addedCharged = items.Add(PlayerInventories.MainInventory, cell.X, cell.Y, chargedOrbItem);
                return true;
            });

            if (!committed || changedTarget is null || addedCharged is null)
                return new SiphonStealCommitResult(false, "Failed to commit siphon transaction (item moved or inventory full).");

            await SendInventoryDiffsAsync(player, items, stash, usedSpot, usedLeft, targetSpot, changedTarget, addedCharged, "siphon steal");
            await Program.Send(player,
                InstanceItems.ItemUsed(used.Item.TypeHash, currencyInventoryId, currencyItemId),
                "0x0263 Orb of Siphoning used");

            var stolenPayload = new StoredAffixPayload(
                ModId: chosen.ModId,
                RolledValues: [.. chosen.RolledValues],
                DisplayText: chosen.StatText,
                IsPrefix: chosen.IsPrefix,
                Group: chosen.Group,
                ModHash16: stolenRaw.Hash,
                DisplayName: chosen.DisplayName,
                IsSuffix: chosen.IsSuffix,
                Tier: chosen.Tier,
                IsCrafted: chosen.IsCrafted);

            return new SiphonStealCommitResult(
                Success: true,
                Error: null,
                StolenAffix: stolenPayload,
                CreatedChargedItemId: addedCharged.Id,
                UpdatedSourceView: affixService.InspectGatewayItem(targetInventoryId, targetItemId, changedTarget.Item, catalog));
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
            if (!players.TryGetValue(connectionId, out var player) || player.Closed)
                return new SiphonImbueCommitResult(false, "Player connection is no longer active.");

            var catalog = Game.ItemsFor(player);
            var items = Game.ItemsOf(player);
            var stash = Game.StashOf(player);

            var (usedSpot, used) = Game.Locate(items, stash, currencyInventoryId, currencyItemId);
            var (targetSpot, target) = Game.Locate(items, stash, targetInventoryId, targetItemId);
            if (used is null || used.Item.TypeHash != SiphonServerMod.ChargedOrbDefinition.TypeHash)
                return new SiphonImbueCommitResult(false, "Charged Orb of Siphoning is no longer in your inventory.");
            if (target is null)
                return new SiphonImbueCommitResult(false, "Destination item is no longer in your inventory.");
            if (Game.Worn(targetSpot))
                return new SiphonImbueCommitResult(false, "Unequip the destination item before imbuing.");

            StoredAffixPayload? stored = affixService.ReadStoredAffixFromGatewayItem(used.Item, catalog);
            if (stored is null)
                return new SiphonImbueCommitResult(false, "Charged Orb of Siphoning has no stored modifier.");

            ItemAffixView view = affixService.InspectGatewayItem(targetInventoryId, targetItemId, target.Item, catalog);
            (bool allowed, string? reason) = LocalExileItemAffixService.CanReplaceAffix(view, replaceIndex, stored);
            if (!allowed)
                return new SiphonImbueCommitResult(false, reason ?? "Selected modifier cannot be replaced.");

            ItemAffixSnapshot replacedSnapshot = view.Affixes[replaceIndex];
            var replacementMod = new ItemMod(ModSection.Explicit, stored.ModHash16, [.. stored.RolledValues]);

            var updatedMods = new List<ItemMod>(target.Item.Mods.Count);
            int seenExplicit = 0;
            bool replaced = false;
            foreach (ItemMod m in target.Item.Mods)
            {
                if (m.Section == ModSection.Explicit)
                {
                    if (seenExplicit == replaceIndex)
                    {
                        updatedMods.Add(replacementMod);
                        replaced = true;
                        seenExplicit++;
                        continue;
                    }
                    seenExplicit++;
                }
                updatedMods.Add(m);
            }

            if (!replaced)
                return new SiphonImbueCommitResult(false, "Could not locate target explicit modifier.");

            Item updatedTarget = target.Item with { Mods = updatedMods };
            if (ItemMoves.Use(items, stash, usedSpot, targetSpot, updatedTarget, held: null) is not var (usedLeft, changedTarget, _) || changedTarget is null)
                return new SiphonImbueCommitResult(false, "Failed to commit imbue transaction.");

            await SendInventoryDiffsAsync(player, items, stash, usedSpot, usedLeft, targetSpot, changedTarget, extraMainItem: null, "siphon imbue");
            await Program.Send(player,
                InstanceItems.ItemUsed(used.Item.TypeHash, currencyInventoryId, currencyItemId),
                "0x0263 Charged Orb of Siphoning used");

            return new SiphonImbueCommitResult(
                Success: true,
                Error: null,
                ReplacedAffix: replacedSnapshot,
                AppliedAffix: stored,
                UpdatedTargetView: affixService.InspectGatewayItem(targetInventoryId, targetItemId, changedTarget.Item, catalog));
        }

        public async Task TellPlayerAsync(string connectionId, string message, CancellationToken cancellationToken = default)
        {
            if (!players.TryGetValue(connectionId, out var player) || player.Closed) return;
            await Program.PlayerOf(player).Tell(message);
        }

        private static async Task SendInventoryDiffsAsync(
            Player player,
            PlayerInventories items,
            AccountStash stash,
            ItemMoves.Spot usedSpot,
            InventoryItem? usedLeft,
            ItemMoves.Spot targetSpot,
            InventoryItem changedTarget,
            InventoryItem? extraMainItem,
            string label)
        {
            // Group removals/additions by (Tab, Row) so each modified inventory/tab gets one clean S->C 0x00bf.
            var buckets = new Dictionary<(int? Tab, byte Row), (List<uint> Gone, List<InventoryItem> Now)>();
            static (List<uint> Gone, List<InventoryItem> Now) Bucket(
                Dictionary<(int? Tab, byte Row), (List<uint> Gone, List<InventoryItem> Now)> map,
                int? tab,
                byte row)
            {
                var key = (tab, row);
                if (!map.TryGetValue(key, out var entry))
                    map[key] = entry = ([], []);
                return entry;
            }

            var usedBucket = Bucket(buckets, usedSpot.Tab, usedSpot.Row);
            usedBucket.Gone.Add(usedSpot.Id);
            if (usedLeft is not null)
                usedBucket.Now.Add(usedLeft);

            var targetBucket = Bucket(buckets, targetSpot.Tab, targetSpot.Row);
            targetBucket.Gone.Add(targetSpot.Id);
            targetBucket.Now.Add(changedTarget);

            if (extraMainItem is not null)
            {
                var mainBucket = Bucket(buckets, null, PlayerInventories.MainInventory);
                mainBucket.Now.Add(extraMainItem);
            }

            foreach (var ((tab, row), (gone, now)) in buckets)
            {
                if (tab is int t)
                {
                    await Game.SendTabChange(player, stash, t, gone, now, $"0x00bf stash tab {t}: {label}");
                }
                else
                {
                    uint invId = PlayerInventories.InventoryId(row);
                    await Program.Send(player,
                        InstanceItems.InventoryChange(invId, items.NextRevision(row), gone, now),
                        $"0x00bf inventory {invId}: {label}");
                }
            }
        }

        private sealed class NoOpDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
