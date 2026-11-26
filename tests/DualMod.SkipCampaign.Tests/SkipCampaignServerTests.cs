using DualMod.SkipCampaign.Server;
using DualMod.SkipCampaign.Shared;
using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Addresses;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.Sdk.Protocol;
using LocalExile.Client.ServerBridge;
using Xunit;

namespace DualMod.SkipCampaign.Tests;

public sealed class SkipCampaignServerTests
{
    private sealed class FakeEligibilitySource : ISkipCampaignEligibilitySource
    {
        private readonly List<(string Account, SkipCampaignLeagueCharacterSnapshot Char)> _owned = [];
        public List<(string Account, string League, bool IncludeDead)> Queries { get; } = [];

        public void AddOwned(string account, SkipCampaignLeagueCharacterSnapshot snapshot) =>
            _owned.Add((account, snapshot));

        public IReadOnlyList<SkipCampaignLeagueCharacterSnapshot> GetLeagueCharacters(
            string account,
            string league,
            bool includeDead = false)
        {
            Queries.Add((account, league, includeDead));
            return [.. _owned
                .Where(e =>
                    string.Equals(e.Account, account, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.Char.League, league, StringComparison.OrdinalIgnoreCase) &&
                    (includeDead || (!e.Char.HardcoreDead && !e.Char.PermanentlyDead)))
                .Select(e => e.Char)];
        }
    }

    private sealed class FakePlayerWorld(Guid characterId, string characterName, string league) : ISkipCampaignPlayerWorld
    {
        private readonly HashSet<string> _questFlags = new(StringComparer.Ordinal);
        private readonly HashSet<string> _unearnedQuestFlags = new(StringComparer.Ordinal);
        private readonly HashSet<string> _waypoints = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _ascendancyAwards = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _characterData = new(StringComparer.Ordinal);

        public Guid CharacterId { get; } = characterId;
        public string CharacterName { get; } = characterName;
        public string League { get; } = league;
        public uint Gold { get; private set; }
        public ushort RefundPoints { get; private set; }

        public int SetQuestFlagsCalls { get; private set; }
        public int GrantWaypointsCalls { get; private set; }
        public int SetRefundPointsCalls { get; private set; }
        public int AwardAscendancyCalls { get; private set; }
        public int AddGoldCalls { get; private set; }
        public HashSet<string> QuestFlags => _questFlags;
        public HashSet<string> UnearnedQuestFlags => _unearnedQuestFlags;
        public HashSet<string> Waypoints => _waypoints;
        public HashSet<string> AscendancyAwards => _ascendancyAwards;

        public void SeedUnearnedFlag(string flag)
        {
            _questFlags.Add(flag);
            _unearnedQuestFlags.Add(flag);
        }

        public bool HasQuestFlag(string flag, bool earnedOnly = false) =>
            _questFlags.Contains(flag) && (!earnedOnly || !_unearnedQuestFlags.Contains(flag));

        public Task SetQuestFlagsAsync(IEnumerable<string> flags, bool on = true, CancellationToken cancellationToken = default)
        {
            SetQuestFlagsCalls++;
            foreach (string flag in flags)
            {
                if (on)
                {
                    _questFlags.Add(flag);
                    _unearnedQuestFlags.Remove(flag);
                }
                else
                {
                    _questFlags.Remove(flag);
                    _unearnedQuestFlags.Remove(flag);
                }
            }

            return Task.CompletedTask;
        }

        public Task<int> GrantWaypointsAsync(IEnumerable<string> areaIds, string? reason = null, CancellationToken cancellationToken = default)
        {
            GrantWaypointsCalls++;
            int added = 0;
            foreach (string area in areaIds)
            {
                if (_waypoints.Add(area))
                {
                    added++;
                }
            }

            return Task.FromResult(added);
        }

        public Task<ushort> SetRefundPointsAsync(ushort points, CancellationToken cancellationToken = default)
        {
            SetRefundPointsCalls++;
            RefundPoints = points;
            return Task.FromResult(RefundPoints);
        }

        public Task<int> AwardAscendancyAsync(IEnumerable<string> awardKeys, CancellationToken cancellationToken = default)
        {
            AwardAscendancyCalls++;
            foreach (string key in awardKeys)
            {
                _ascendancyAwards.Add(key);
            }

            return Task.FromResult(_ascendancyAwards.Count * 2);
        }

        public Task<uint> AddGoldAsync(long delta, string? reason = null, CancellationToken cancellationToken = default)
        {
            AddGoldCalls++;
            long next = Math.Clamp((long)Gold + delta, 0, uint.MaxValue);
            Gold = (uint)next;
            return Task.FromResult(Gold);
        }

        public string? GetCharacterData(string key) =>
            _characterData.TryGetValue(key, out string? value) ? value : null;

        public bool ChangeCharacterData(string key, Func<string?, string?> change)
        {
            string? current = GetCharacterData(key);
            string? next = change(current);
            if (next is null)
            {
                return false;
            }

            _characterData[key] = next;
            return true;
        }
    }

    [Fact]
    public void CharacterCreated_RespectsOptInOptOutAndEnabledSetting()
    {
        var eligibility = new FakeEligibilitySource();
        var settings = SkipCampaignSettings.Default with { RequireLeagueCompletion = false };
        var handler = new SkipCampaignHandler(settings, eligibility);

        // Opt-out (no option) -> no overrides, no pending grant.
        Guid id1 = Guid.NewGuid();
        var optOut = handler.OnCharacterCreated(new SkipCampaignCharacterCreatedInput(
            "exile", id1, "NormalChar", "Standard", false, false, new Dictionary<string, string>()));
        Assert.False(optOut.Applied);
        Assert.Null(optOut.StartingAreaId);
        Assert.Null(optOut.StartingLevel);
        Assert.False(optOut.SuppressStartKit);
        Assert.False(handler.HasPendingGrant(id1, "NormalChar"));

        // Explicit skip_campaign=0 -> opt-out.
        Guid id2 = Guid.NewGuid();
        var explicitOff = handler.OnCharacterCreated(new SkipCampaignCharacterCreatedInput(
            "exile", id2, "OffChar", "Standard", false, false, new Dictionary<string, string> { ["skip_campaign"] = "0" }));
        Assert.False(explicitOff.Applied);
        Assert.False(handler.HasPendingGrant(id2, "OffChar"));

        // Opt-in skip_campaign=1 -> Karui Shores Lv.68 + SuppressStartKit=true.
        Guid id3 = Guid.NewGuid();
        var optIn = handler.OnCharacterCreated(new SkipCampaignCharacterCreatedInput(
            "exile", id3, "SkipChar", "Standard", false, false, new Dictionary<string, string> { ["skip_campaign"] = "1" }));
        Assert.True(optIn.Applied);
        Assert.Equal("2_11_endgame_town", optIn.StartingAreaId);
        Assert.Equal(68, optIn.StartingLevel);
        Assert.True(optIn.SuppressStartKit);
        Assert.True(handler.HasPendingGrant(id3, "SkipChar"));

        // Master disabled switch blocks even when skip_campaign=1.
        var disabledHandler = new SkipCampaignHandler(settings with { Enabled = false }, eligibility);
        var blocked = disabledHandler.OnCharacterCreated(new SkipCampaignCharacterCreatedInput(
            "exile", Guid.NewGuid(), "DisabledChar", "Standard", false, false, new Dictionary<string, string> { ["skip_campaign"] = "1" }));
        Assert.False(blocked.Applied);
        Assert.NotNull(blocked.DenialReason);
    }

    [Fact]
    public void CharacterCreated_EnforcesStrictLeagueSsfAndHardcoreDeathIsolation()
    {
        var eligibility = new FakeEligibilitySource();
        // Account has a Lv.95 character in Standard and a DEAD Lv.85 character in Hardcore.
        eligibility.AddOwned("exile", new SkipCampaignLeagueCharacterSnapshot(
            Guid.NewGuid(), "StdMain", Level: 95, League: "Standard", AreaId: "2_11_endgame_town", LastTown: "2_11_endgame_town"));
        eligibility.AddOwned("exile", new SkipCampaignLeagueCharacterSnapshot(
            Guid.NewGuid(), "HcFallen", Level: 85, League: "Hardcore", AreaId: "2_11_endgame_town", LastTown: "2_11_endgame_town", HardcoreDead: true, PermanentlyDead: true));
        eligibility.AddOwned("exile", new SkipCampaignLeagueCharacterSnapshot(
            Guid.NewGuid(), "SsfLow", Level: 42, League: "SSF Standard", AreaId: "1_5_town", LastTown: "1_5_town"));

        var handler = new SkipCampaignHandler(SkipCampaignSettings.Default with { RequireLeagueCompletion = true }, eligibility);
        var optIn = new Dictionary<string, string> { [SkipCampaignProtocol.OptionKey] = SkipCampaignProtocol.OptionEnabledValue };

        // 1. Standard -> APPROVED because StdMain (Lv.95, alive) is in Standard.
        Guid stdAltId = Guid.NewGuid();
        var stdAlt = handler.OnCharacterCreated(new SkipCampaignCharacterCreatedInput("exile", stdAltId, "StdAlt", "Standard", false, false, optIn));
        Assert.True(stdAlt.Applied);
        Assert.Equal("2_11_endgame_town", stdAlt.StartingAreaId);
        Assert.Equal(68, stdAlt.StartingLevel);
        Assert.True(stdAlt.SuppressStartKit);
        Assert.Contains(eligibility.Queries, q => q.League == "Standard" && q.IncludeDead == false);

        // 2. SSF Standard -> DENIED! Standard Lv.95 must NOT unlock SSF Standard, and SsfLow is only Lv.42 in Act 5.
        var ssfAlt = handler.OnCharacterCreated(new SkipCampaignCharacterCreatedInput("exile", Guid.NewGuid(), "SsfAlt", "SSF Standard", false, true, optIn));
        Assert.False(ssfAlt.Applied);
        Assert.Contains("SSF Standard", ssfAlt.DenialReason);

        // 3. Hardcore -> DENIED! Dead HC character must NOT satisfy living league completion!
        var hcAlt = handler.OnCharacterCreated(new SkipCampaignCharacterCreatedInput("exile", Guid.NewGuid(), "HcAlt", "Hardcore", true, false, optIn));
        Assert.False(hcAlt.Applied);
        Assert.Contains("Hardcore", hcAlt.DenialReason);

        // 4. Add a living Karui Shores character in Hardcore (even below Lv.68, e.g. Lv.65 who killed Kitava) -> APPROVED!
        eligibility.AddOwned("exile", new SkipCampaignLeagueCharacterSnapshot(
            Guid.NewGuid(), "HcSurvivor", Level: 65, League: "Hardcore", AreaId: "2_11_endgame_town", LastTown: "2_11_endgame_town"));
        var hcAlt2 = handler.OnCharacterCreated(new SkipCampaignCharacterCreatedInput("exile", Guid.NewGuid(), "HcAlt2", "Hardcore", true, false, optIn));
        Assert.True(hcAlt2.Applied);

        // 5. Brand-new character already saved in DB cannot satisfy its own check!
        Guid selfId = Guid.NewGuid();
        eligibility.AddOwned("newcomer", new SkipCampaignLeagueCharacterSnapshot(
            selfId, "FreshHero", Level: 68, League: "Standard", AreaId: "2_11_endgame_town", LastTown: "2_11_endgame_town"));
        var selfCheck = handler.OnCharacterCreated(new SkipCampaignCharacterCreatedInput("newcomer", selfId, "FreshHero", "Standard", false, false, optIn));
        Assert.False(selfCheck.Applied);
    }

    [Fact]
    public async Task FirstEnter_AppliesProgressionStripsUnearnedFlagsAndIsStrictlyIdempotentAcrossRelogsAndRestarts()
    {
        var eligibility = new FakeEligibilitySource();
        var settings = SkipCampaignSettings.Default with { RequireLeagueCompletion = false };
        var handler = new SkipCampaignHandler(settings, eligibility);

        Guid charId = Guid.NewGuid();
        var created = handler.OnCharacterCreated(new SkipCampaignCharacterCreatedInput(
            "exile", charId, "EndgameRanger", "Standard", false, false,
            new Dictionary<string, string> { ["skip_campaign"] = "1" }));
        Assert.True(created.Applied);

        var world = new FakePlayerWorld(charId, "EndgameRanger", "Standard");
        // Simulate stock LocalExile login seeding EndGameAvailable as unearned until SetQuestFlagsAsync strips it.
        world.SeedUnearnedFlag("EndGameAvailable");
        Assert.False(world.HasQuestFlag("EndGameAvailable", earnedOnly: true));

        // First enter applies all progression exactly once.
        SkipCampaignFirstEnterResult first = await handler.OnFirstEnterAsync(world);
        Assert.True(first.Applied);
        Assert.False(first.AlreadyFinalized);
        Assert.NotNull(first.Notice);

        Assert.True(world.HasQuestFlag("a5q6-CompletedBoss", earnedOnly: true));
        Assert.True(world.HasQuestFlag("EndGameAvailable", earnedOnly: true));
        Assert.True(world.HasQuestFlag("CanChangePantheon", earnedOnly: true));
        Assert.DoesNotContain("EndGameAvailable", world.UnearnedQuestFlags);

        Assert.Equal(SkipCampaignProtocol.DefaultCampaignWaypoints.Count, world.Waypoints.Count);
        Assert.Contains("1_1_town", world.Waypoints);
        Assert.Contains("2_11_endgame_town", world.Waypoints);
        Assert.Equal((ushort)10, world.RefundPoints);
        Assert.Equal(["cruel", "merciless", "normal"], world.AscendancyAwards.Order().ToArray());
        Assert.Equal(50_000u, world.Gold);
        Assert.Equal("1", world.GetCharacterData(SkipCampaignProtocol.FinalizedCharacterDataKey));
        Assert.False(handler.HasPendingGrant(charId, "EndgameRanger"));

        Assert.Equal(68, first.Notice!.StartingLevel);
        Assert.Equal("2_11_endgame_town", first.Notice.StartingAreaId);
        Assert.Equal(SkipCampaignProtocol.DefaultCampaignWaypoints.Count, first.Notice.WaypointsGranted);
        Assert.Equal(6, first.Notice.AscendancyPointsEarned);
        Assert.Equal(50_000, first.Notice.GoldGranted);
        Assert.Equal(50_000u, first.Notice.TotalGold);

        // Relog (second OnFirstEnterAsync on same handler) -> strictly no-op!
        SkipCampaignFirstEnterResult relog = await handler.OnFirstEnterAsync(world);
        Assert.False(relog.Applied);
        Assert.True(relog.AlreadyFinalized);
        Assert.Null(relog.Notice);
        Assert.Equal(1, world.AddGoldCalls);
        Assert.Equal(50_000u, world.Gold);

        // Restart resilience: even if a fresh handler received a duplicate pending grant, persisted CharacterData blocks re-grant!
        var restartedHandler = new SkipCampaignHandler(settings, eligibility);
        restartedHandler.OnCharacterCreated(new SkipCampaignCharacterCreatedInput(
            "exile", charId, "EndgameRanger", "Standard", false, false,
            new Dictionary<string, string> { ["skip_campaign"] = "1" }));
        SkipCampaignFirstEnterResult afterRestart = await restartedHandler.OnFirstEnterAsync(world);
        Assert.False(afterRestart.Applied);
        Assert.True(afterRestart.AlreadyFinalized);
        Assert.Equal(1, world.AddGoldCalls);
        Assert.Equal(1, world.SetQuestFlagsCalls);
    }

    [Fact]
    public async Task FirstEnter_SkipsAscendancyWhenGrantAscendancyTrialsIsFalse()
    {
        var eligibility = new FakeEligibilitySource();
        var settings = SkipCampaignSettings.Default with
        {
            RequireLeagueCompletion = false,
            GrantAscendancyTrials = false,
            StartingGold = 12_500,
            RefundPoints = 5,
        };
        var handler = new SkipCampaignHandler(settings, eligibility);
        Guid charId = Guid.NewGuid();
        handler.OnCharacterCreated(new SkipCampaignCharacterCreatedInput(
            "exile", charId, "NoLabHero", "Standard", false, false,
            new Dictionary<string, string> { ["skip_campaign"] = "1" }));

        var world = new FakePlayerWorld(charId, "NoLabHero", "Standard");
        SkipCampaignFirstEnterResult res = await handler.OnFirstEnterAsync(world);

        Assert.True(res.Applied);
        Assert.Equal(0, world.AwardAscendancyCalls);
        Assert.Empty(world.AscendancyAwards);
        Assert.Equal(0, res.Notice!.AscendancyPointsEarned);
        Assert.Equal((ushort)5, world.RefundPoints);
        Assert.Equal(12_500u, world.Gold);
    }

    [Fact]
    public async Task ServerMod_QueuesNoticeUntilClientReadyAndDeliversOverServerBridge()
    {
        var sentPackets = new List<(string ConnId, ushort Opcode, byte[] Payload)>();
        var sink = new DelegateSink(
            (conn, op, payload) => { sentPackets.Add((conn, op, payload.ToArray())); return Task.CompletedTask; },
            (_, _) => Task.CompletedTask);

        using var bridge = new ClientBridgeHost(sink, new ClientBridgeOptions
        {
            DefaultChannel = SkipCampaignProtocol.ChannelName,
            HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId,
        });
        var handler = new SkipCampaignHandler(SkipCampaignSettings.Default with { RequireLeagueCompletion = false }, new FakeEligibilitySource());
        using var serverMod = new SkipCampaignServerMod(bridge, handler);
        serverMod.Attach();

        Guid charId = Guid.NewGuid();
        serverMod.HandleCharacterCreated(new SkipCampaignCharacterCreatedInput(
            "exile", charId, "ToastHero", "Standard", false, false,
            new Dictionary<string, string> { ["skip_campaign"] = "1" }));

        var world = new FakePlayerWorld(charId, "ToastHero", "Standard");

        // Player enters BEFORE ClientHello handshake completes -> notice is queued, not dropped!
        SkipCampaignFirstEnterResult entered = await serverMod.OnPlayerEnteredAsync("conn-1", world);
        Assert.True(entered.Applied);
        Assert.Empty(sentPackets);

        // NativeMods client finishes ClientHandshake -> ServerHandshake + queued SkipCampaignAppliedNotice delivered!
        var hello = new ClientHandshake(
            ExtenderVersion: SdkVersion.Current,
            SdkVersion: SdkVersion.Current,
            Build: ClientBuildId.Build3293d,
            BuildVersion: "3.29.3d",
            ProtocolVersion: SdkVersion.ProtocolVersion,
            InstalledMods: [new InstalledClientModInfo(SkipCampaignProtocol.ModId, "1.0.0")],
            Capabilities: ["ruleset.0x2c9"]);
        ModEnvelope helloEnv = MessageCodec.CreateTypedEnvelope(ClientBridgeHost.HostChannelId, hello, sequence: 1);
        byte[] rulesetBody = MessageCodec.EncodeRulesetBody(IRulesetRegistry.DefaultHostChannelRulesetId, MessageCodec.EncodeEnvelope(helloEnv));
        bool handled = await bridge.HandleIncomingPacketAsync("conn-1", IOpcodeRegistry.ClientRulesetOpcode, rulesetBody, "ToastHero");
        Assert.True(handled);

        // Expect ServerHandshake + SkipCampaignAppliedNotice envelopes on 0x02C9.
        Assert.True(sentPackets.Count >= 2);
        var notices = sentPackets
            .Select(p =>
            {
                Assert.True(MessageCodec.TryDecodeRulesetBody(p.Payload, out _, out var inner));
                return MessageCodec.DecodeEnvelope(inner.Span);
            })
            .Where(env => env.Channel == SkipCampaignProtocol.ChannelName)
            .Select(MessageCodec.DeserializePayload<SkipCampaignAppliedNotice>)
            .ToList();

        SkipCampaignAppliedNotice delivered = Assert.Single(notices);
        Assert.Equal("ToastHero", delivered.CharacterName);
        Assert.Equal(68, delivered.StartingLevel);
        Assert.Equal("2_11_endgame_town", delivered.StartingAreaId);
        Assert.Equal(50_000, delivered.GoldGranted);
    }

    private sealed class DelegateSink(
        Func<string, ushort, ReadOnlyMemory<byte>, Task> onPacket,
        Func<string, string, Task> onChat) : IGatewayPacketSink
    {
        public Task SendPacketAsync(string connectionId, ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
            onPacket(connectionId, opcode, payload);

        public Task SendChatReplyAsync(string connectionId, string text, CancellationToken cancellationToken = default) =>
            onChat(connectionId, text);
    }
}
