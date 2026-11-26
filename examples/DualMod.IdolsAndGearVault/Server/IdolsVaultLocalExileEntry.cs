#nullable enable
// Ready-to-drop LocalExile Gateway IMod entry point for DualMod.IdolsAndGearVault (SDK 1.1.0).
// Requires ZERO LocalExile server modifications — uses verified public Gateway APIs via LocalExileGatewayHost:
// - LocalExileGatewayHost.Create(...) for 0x02C9/0x02CA + chat fallback transport and player lifecycle tracking
// - Game.PersonalStashOf(player) + Game.ItemsFor(player) to inspect Gear Vault stash tab items
// - IdolsVaultServerMod for authoritative 32-cell Idol Board placement/removal and 0xAE ruleset telemetry.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DualMod.IdolsAndGearVault.Server;
using DualMod.IdolsAndGearVault.Shared;
using GameServer.Core;
using GameServer.Modding;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.Sdk.Protocol;
using LocalExile.Client.ServerBridge;

public sealed class IdolsVaultLocalExileEntry : IMod, IDisposable
{
    private LocalExileGatewayHost? _gatewayHost;
    private LocalExileIdolsVaultBackend? _backend;
    private IdolsVaultServerMod? _serverMod;

    public void Load(IModContext mod)
    {
        IdolsVaultSettings settings = LoadSettings(mod);

        _gatewayHost = LocalExileGatewayHost.Create(mod, builder => builder
            .WithBridgeOptions(new ClientBridgeOptions
            {
                ServerVersion = "LocalExile-3.29.3d",
                HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId,
                DefaultChannel = IdolsVaultProtocol.ChannelName,
            })
            .WithBus(
                attach: true,
                onDispatchError: (owner, ex) => mod.Log($"[idolsvault] gateway bus '{owner}' failed: {ex.Message}"))
            .OnPlayerLeaving((ctx, _) =>
            {
                if (((Player)ctx.Player).Closed)
                {
                    _serverMod?.OnPlayerLeft(ctx.ConnectionId);
                }

                return Task.CompletedTask;
            }));

        _backend = new LocalExileIdolsVaultBackend(_gatewayHost);
        _serverMod = new IdolsVaultServerMod(_gatewayHost.Bridge, _backend, settings);
        _serverMod.Attach();

        mod.Log("Loaded DualMod.IdolsAndGearVault via LocalExileGatewayHost (SDK 1.1.0, zero server edits).");
    }

    public void Dispose()
    {
        _serverMod?.Dispose();
        _serverMod = null;
        _gatewayHost?.Dispose();
        _gatewayHost = null;
    }

    private static IdolsVaultSettings LoadSettings(IModContext mod)
    {
        try
        {
            return mod.Settings<IdolsVaultSettings>();
        }
        catch
        {
            return new IdolsVaultSettings();
        }
    }

    private sealed class LocalExileIdolsVaultBackend(LocalExileGatewayHost gatewayHost) : IIdolsVaultBackend
    {
        private readonly LocalExileGatewaySink _packetSink = new(id => gatewayHost.TryGetPlayer(id, out object? p) ? p : null);

        public Task SendBoardRulesetUpdateAsync(
            string connectionId,
            byte rulesetId,
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default)
        {
            byte[] body = MessageCodec.EncodeRulesetBody(rulesetId, payload.Span);
            return _packetSink.SendPacketAsync(connectionId, IOpcodeRegistry.ServerRulesetOpcode, body, cancellationToken);
        }

        public IReadOnlyList<GearVaultItemRecord> GetVaultItems(
            string connectionId,
            ushort targetTabIndex,
            string gearClassFilter,
            int maxEntries)
        {
            if (!gatewayHost.TryGetPlayer(connectionId, out object? obj) || obj is not Player p || p.Closed)
            {
                return [];
            }

            var stash = Game.PersonalStashOf(p);
            if (targetTabIndex >= stash.Count)
            {
                return [];
            }

            var catalog = Game.ItemsFor(p);
            var tabItems = stash.Inventory(targetTabIndex).Items;
            List<GearVaultItemRecord> matches = [];

            foreach (var invItem in tabItems)
            {
                ItemCatalog.BaseType? baseType = catalog.ByHash(invItem.Item.TypeHash);
                string metadataPath = baseType?.Id ?? string.Empty;
                string baseName = baseType?.Name ?? "Unknown Item";
                string? gearClass = GearVaultClasses.Classify(metadataPath, baseName);

                if (gearClass is null || !string.Equals(gearClass, gearClassFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string displayName = $"{invItem.Item.Rarity} {baseName}";
                matches.Add(new GearVaultItemRecord(
                    ItemId: invItem.Id,
                    Name: displayName,
                    BaseName: baseName,
                    MetadataPath: metadataPath,
                    GearClass: gearClass,
                    ItemLevel: invItem.Item.ItemLevel,
                    Rarity: (int)invItem.Item.Rarity,
                    TabIndex: targetTabIndex,
                    AffixTags: BuildDefaultTags(gearClass, invItem.Item.Corrupted)));

                if (matches.Count >= Math.Max(1, maxEntries))
                {
                    break;
                }
            }

            return matches;
        }

        private static string[] BuildDefaultTags(string gearClass, bool corrupted)
        {
            List<string> tags = gearClass switch
            {
                GearVaultClasses.Weapons => ["Attack", "Critical", "Weapons"],
                GearVaultClasses.Rings or GearVaultClasses.Amulets or GearVaultClasses.Belts => ["Life", "Resistance", gearClass],
                _ => ["Defence", "Life", gearClass.Replace(" ", string.Empty, StringComparison.Ordinal)],
            };
            if (corrupted)
            {
                tags.Add("Corrupted");
            }

            return [.. tags];
        }
    }
}
