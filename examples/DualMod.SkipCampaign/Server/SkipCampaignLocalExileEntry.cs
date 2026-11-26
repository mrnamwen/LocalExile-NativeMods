// Thin LocalExile Gateway IMod entry point for DualMod.SkipCampaign (SDK 0.3.0 / ServerBridge).
// Wires GameEvents.CharacterCreated, CharacterSaves.GetLeagueCharacters, LocalExileGatewayHost
// (PlayerEntered / PlayerLeaving / ServerBridge), and public IPlayerWorld progression facades.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DualMod.SkipCampaign.Server;
using DualMod.SkipCampaign.Shared;
using GameServer.Modding;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.ServerBridge;

public sealed class SkipCampaignLocalExileEntry : IMod, IDisposable
{
    private LocalExileGatewayHost? _gatewayHost;
    private SkipCampaignServerMod? _serverMod;
    private IDisposable? _characterCreatedSub;

    public void Load(IModContext mod)
    {
        SkipCampaignSettings settings = LoadSettings(mod);

        _gatewayHost = LocalExileGatewayHost.Create(mod, builder => builder
            .WithBridgeOptions(new ClientBridgeOptions
            {
                ServerVersion = "LocalExile-3.29.3d",
                HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId,
                DefaultChannel = SkipCampaignProtocol.ChannelName,
            })
            .WithBus(
                attach: true,
                onDispatchError: (owner, ex) => mod.Log($"[gateway-bus] participant '{owner}' failed: {ex.Message}"))
            .OnPlayerEntered(async ctx =>
            {
                var player = (Player)ctx.Player;
                if (_serverMod is null)
                {
                    return;
                }

                var adapter = new LocalExilePlayerWorldAdapter(player, Program.WorldOf(player));
                SkipCampaignFirstEnterResult result = await _serverMod.OnPlayerEnteredAsync(ctx.ConnectionId, adapter).ConfigureAwait(false);
                if (result.Applied && result.Notice is { } notice && !_gatewayHost!.Bridge.IsClientReady(ctx.ConnectionId))
                {
                    // Graceful chat summary for vanilla clients without the NativeMods client extender.
                    await global::Client.For(player).SendChat(
                        $"[Skip Campaign] Lv.{notice.StartingLevel} in {notice.StartingAreaId}: {notice.WaypointsGranted} waypoints, {notice.RefundPoints} refund pts, {notice.AscendancyPointsEarned} ascendancy pts, {notice.GoldGranted:N0} gold.").ConfigureAwait(false);
                }
            })
            .OnPlayerLeaving((ctx, _) =>
            {
                if (((Player)ctx.Player).Closed)
                {
                    _serverMod?.OnPlayerLeft(ctx.ConnectionId);
                }

                return Task.CompletedTask;
            }));

        var handler = new SkipCampaignHandler(settings, new LocalExileEligibilitySource(), mod.Log);
        _serverMod = new SkipCampaignServerMod(_gatewayHost.Bridge, handler);
        _serverMod.Attach();

        _characterCreatedSub = GameEvents.CharacterCreated.Add(mod.Manifest.Id, ctx =>
        {
            if (_serverMod is null)
            {
                return Task.CompletedTask;
            }

            SkipCampaignCharacterCreatedEffect effect = _serverMod.HandleCharacterCreated(
                new SkipCampaignCharacterCreatedInput(
                    Account: ctx.Account,
                    CharacterId: ctx.CharacterId,
                    CharacterName: ctx.Name,
                    League: ctx.League,
                    Hardcore: ctx.Hardcore,
                    Ssf: ctx.Ssf,
                    CreationOptions: ctx.CreationOptions));

            if (effect.Applied)
            {
                ctx.StartingAreaId = effect.StartingAreaId;
                ctx.StartingLevel = effect.StartingLevel;
                ctx.SuppressStartKit = effect.SuppressStartKit;
            }

            return Task.CompletedTask;
        });

        mod.Log($"Loaded Skip Campaign (Enabled={settings.Enabled}, RequireLeagueCompletion={settings.RequireLeagueCompletion}, Start={settings.StartingAreaId} Lv.{settings.StartingLevel}).");
    }

    public void Dispose()
    {
        _characterCreatedSub?.Dispose();
        _characterCreatedSub = null;
        _serverMod?.Dispose();
        _serverMod = null;
        _gatewayHost?.Dispose();
        _gatewayHost = null;
    }

    private static SkipCampaignSettings LoadSettings(IModContext mod)
    {
        try
        {
            SkipCampaignSettings? loaded = mod.Settings<SkipCampaignSettings>();
            return loaded ?? SkipCampaignSettings.Default;
        }
        catch (Exception ex)
        {
            mod.Log($"[skip-campaign] Using default settings ({ex.Message}).");
            return SkipCampaignSettings.Default;
        }
    }

    private sealed class LocalExileEligibilitySource : ISkipCampaignEligibilitySource
    {
        public IReadOnlyList<SkipCampaignLeagueCharacterSnapshot> GetLeagueCharacters(
            string account,
            string league,
            bool includeDead = false)
        {
            return [.. CharacterSaves.GetLeagueCharacters(account, league, includeDead).Select(c =>
                new SkipCampaignLeagueCharacterSnapshot(
                    Id: c.Id,
                    Name: c.Name,
                    Level: c.Level,
                    League: c.League,
                    AreaId: c.AreaId,
                    LastTown: c.LastTown,
                    HardcoreDead: c.HardcoreDead,
                    PermanentlyDead: c.PermanentlyDead))];
        }
    }

    private sealed class LocalExilePlayerWorldAdapter(Player player, IPlayerWorld world) : ISkipCampaignPlayerWorld
    {
        public Guid CharacterId => player.Character.Id ?? Guid.Empty;
        public string CharacterName => player.Character.Name ?? string.Empty;
        public string League => player.Character.League ?? string.Empty;
        public uint Gold => world.Gold;
        public ushort RefundPoints => world.RefundPoints;

        public bool HasQuestFlag(string flag, bool earnedOnly = false) =>
            world.HasQuestFlag(flag, earnedOnly);

        public Task SetQuestFlagsAsync(IEnumerable<string> flags, bool on = true, CancellationToken cancellationToken = default) =>
            world.SetQuestFlagsAsync(flags, on);

        public Task<int> GrantWaypointsAsync(IEnumerable<string> areaIds, string? reason = null, CancellationToken cancellationToken = default) =>
            world.GrantWaypointsAsync(areaIds, reason);

        public Task<ushort> SetRefundPointsAsync(ushort points, CancellationToken cancellationToken = default) =>
            world.SetRefundPointsAsync(points);

        public Task<int> AwardAscendancyAsync(IEnumerable<string> awardKeys, CancellationToken cancellationToken = default) =>
            world.AwardAscendancyAsync(awardKeys);

        public Task<uint> AddGoldAsync(long delta, string? reason = null, CancellationToken cancellationToken = default) =>
            world.AddGoldAsync(delta, reason);

        public string? GetCharacterData(string key) =>
            world.CharacterData.Get(key);

        public bool ChangeCharacterData(string key, Func<string?, string?> change) =>
            world.CharacterData.Change(key, change);
    }
}
