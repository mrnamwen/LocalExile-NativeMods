using System.Runtime.CompilerServices;
using DualMod.SkipCampaign.Server;
using DualMod.SkipCampaign.Shared;
using LocalExile.Client.Host;
using LocalExile.Client.Host.Loading;
using LocalExile.Client.Host.Native;
using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Addresses;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.Sdk.Protocol;
using LocalExile.Client.ServerBridge;
using Xunit;

namespace DualMod.SkipCampaign.Tests;

public sealed class SkipCampaignClientAndEndToEndTests
{
    [Fact]
    public async Task ClientMod_LeagueOverlayFillInjection_CreateCharacterBarSync_S78Mutation_ToggleResetAndPreserve_AndCleanUnload()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "nm-skip-client-" + Guid.NewGuid().ToString("N"));
        string shadowRoot = Path.Combine(workspace, "shadow");
        try
        {
            string modDir = TestPaths.StageClientModDirectory(workspace);

            using var bridge = new SimulatedNativeBridge(ClientBuildId.Build3293d) { EnableSimulatedNativeUi = true };
            using var host = new ClientHostRuntime(bridge, new ClientHostOptions
            {
                TransportKind = NetTransportKind.HostRuleset,
                HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId,
                ShadowCopyRoot = shadowRoot,
            });
            host.Start();

            RunClientPreGameVerification(bridge, host, modDir);

            // Verify collectible ALC unload + zero lingering registrations or S78 mutations.
            ModUnloadResult unload = await host.Loader.UnloadModAsync(SkipCampaignProtocol.ModId);
            Assert.True(unload.WasLoaded);
            Assert.True(unload.UnloadedCleanly);
            Assert.Empty(host.Screens.GetSlotAttachments(ScreenSlot.CreateCharacterBar));
            Assert.Equal("DexDefault", bridge.SimulateCharacterCreating("AfterUnload", "Standard", "DexDefault"));
        }
        finally
        {
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunClientPreGameVerification(
        SimulatedNativeBridge bridge,
        ClientHostRuntime host,
        string modDir)
    {
        LoadedClientMod loaded = host.Loader.LoadModFromDirectory(modDir);
        Assert.Equal(SkipCampaignProtocol.ModId, loaded.Manifest.Id);
        Assert.False(loaded.IsDisabled);

        // 1. LeagueOverlay::Fill #1 injects "Skip Campaign" checkbox into both Standard and Hardcore 0xdc35 cards.
        var stdCard = bridge.RegisterSimulatedStockControl("Standard", tag: NativeShimConstants.TagLeagueCard, rect: new UiRect(0, 0, 500, 905));
        var stdStack = bridge.RegisterSimulatedStockControl("std_mode_stack", parentHandle: stdCard.Handle, rect: new UiRect(0, 628, 300, 177));
        bridge.RegisterSimulatedStockControl("hc", parentHandle: stdStack.Handle, rect: new UiRect(0, 0, 210, 59));
        bridge.RegisterSimulatedStockControl("ssf", parentHandle: stdStack.Handle, rect: new UiRect(0, 59, 230, 59));

        var hcCard = bridge.RegisterSimulatedStockControl("Hardcore", tag: NativeShimConstants.TagLeagueCard, rect: new UiRect(520, 0, 500, 905));
        var hcStack = bridge.RegisterSimulatedStockControl("hc_mode_stack", parentHandle: hcCard.Handle, rect: new UiRect(0, 628, 300, 118));
        bridge.RegisterSimulatedStockControl("ssf_hc", parentHandle: hcStack.Handle, rect: new UiRect(0, 0, 220, 59));

        bridge.SimulateLeagueOverlayFilled(0x8800,
        [
            new LeagueCardInfo(0, "Standard", stdCard.Handle, stdStack.Handle, 2, 580f, 628f, 230f),
            new LeagueCardInfo(1, "Hardcore", hcCard.Handle, hcStack.Handle, 1, 580f, 628f, 220f),
        ]);

        IReadOnlyList<nint> stdChildren = bridge.GetNativeUiChildren(stdStack.Handle);
        IReadOnlyList<nint> hcChildren = bridge.GetNativeUiChildren(hcStack.Handle);
        Assert.Equal(3, stdChildren.Count); // 2 stock + Skip Campaign
        Assert.Equal(2, hcChildren.Count);  // 1 stock + Skip Campaign

        nint stdSkipCb = stdChildren[2];
        var stdSkipSim = bridge.GetSimulatedNativeUiControlByHandle(stdSkipCb)!;
        Assert.Equal(SkipCampaignProtocol.CheckboxCaption, stdSkipSim.Text);
        Assert.False(stdSkipSim.IsChecked);

        // Unchecked -> stock S78 untouched!
        Assert.Equal("DexDefault", bridge.SimulateCharacterCreating("RangerOne", "Standard", "DexDefault"));

        // 2. Player checks "Skip Campaign" on Standard card -> re-injected as checked on Fill #2!
        Assert.True(bridge.SimulateNativeUiCheckBoxChanged(stdSkipCb, true));

        var stdCard2 = bridge.RegisterSimulatedStockControl("Standard", tag: NativeShimConstants.TagLeagueCard, rect: new UiRect(0, 0, 500, 905));
        var stdStack2 = bridge.RegisterSimulatedStockControl("std_mode_stack_2", parentHandle: stdCard2.Handle, rect: new UiRect(0, 628, 300, 177));
        bridge.SimulateLeagueOverlayFilled(0x8800,
        [
            new LeagueCardInfo(0, "Standard", stdCard2.Handle, stdStack2.Handle, 0, 580f, 628f, 0f),
        ]);
        nint reinjectedCb = Assert.Single(bridge.GetNativeUiChildren(stdStack2.Handle));
        Assert.True(bridge.GetSimulatedNativeUiControlByHandle(reinjectedCb)!.IsChecked);

        // 3. Transition to CreateCharacter screen -> CreateCharacterBar mirror mounts & reflects checked=true!
        var createBarParent = bridge.RegisterSimulatedStockControl("create_character_bottom_flow");
        bridge.SetSimulatedSlotParentHandle(ScreenSlot.CreateCharacterBar, createBarParent.Handle);
        bridge.SimulatedPreGameScreen = PreGameScreen.CreateCharacter;
        host.TickFrame();
        host.TickFrame();

        ISlotAttachment barAttachment = Assert.Single(host.Screens.GetSlotAttachments(ScreenSlot.CreateCharacterBar));
        Assert.True(barAttachment.IsAttached);
        Assert.NotNull(barAttachment.Control);
        ICheckBox mirrorCb = barAttachment.Control!.Require<ICheckBox>(SkipCampaignProtocol.CreateCharacterCheckBoxId);
        ILabel mirrorStatus = barAttachment.Control.Require<ILabel>(SkipCampaignProtocol.CreateCharacterStatusLabelId);
        Assert.True(mirrorCb.IsChecked);
        Assert.Equal("Endgame Start", mirrorStatus.Text);

        // 4. Outgoing C->S 0x12 appends "|nm:skip_campaign=1" for Standard and SSF Standard, NOT for Hardcore!
        Assert.Equal("DexDefault|nm:skip_campaign=1", bridge.SimulateCharacterCreating("RangerOne", "Standard", "DexDefault"));
        Assert.Equal("DexDefault|nm:skip_campaign=1", bridge.SimulateCharacterCreating("RangerSsf", "SSF Standard", "DexDefault"));
        Assert.Equal("StrDefault", bridge.SimulateCharacterCreating("MarauderHc", "Hardcore", "StrDefault"));

        // 5. Name-taken failure (ResultCode != 0) PRESERVES toggle so user doesn't have to re-check!
        bridge.SimulateCharacterCreated("RangerOne", "Standard", resultCode: 5);
        Assert.True(mirrorCb.IsChecked);
        Assert.Equal("DexDefault|nm:skip_campaign=1", bridge.SimulateCharacterCreating("RangerTwo", "Standard", "DexDefault"));

        // 6. Creation success (ResultCode == 0) RESETS toggle cleanly on both CreateCharacterBar and LeagueCardModes!
        bridge.SimulateCharacterCreated("RangerTwo", "Standard", resultCode: 0);
        Assert.False(mirrorCb.IsChecked);
        Assert.Equal("Normal Start", mirrorStatus.Text);
        Assert.Equal("DexDefault", bridge.SimulateCharacterCreating("RangerThree", "Standard", "DexDefault"));

        // 7. Toggling mirror directly on CreateCharacterBar (when LeagueOverlay is bypassed) enables S78 option!
        mirrorCb.IsChecked = true;
        Assert.Equal("Endgame Start", mirrorStatus.Text);
        Assert.Equal("IntDefault|nm:skip_campaign=1", bridge.SimulateCharacterCreating("WitchMirror", "Standard", "IntDefault"));

        // Re-opening LeagueOverlay reflects the mirror toggle back on the Standard card!
        var stdCard3 = bridge.RegisterSimulatedStockControl("Standard", tag: NativeShimConstants.TagLeagueCard, rect: new UiRect(0, 0, 500, 905));
        var stdStack3 = bridge.RegisterSimulatedStockControl("std_mode_stack_3", parentHandle: stdCard3.Handle, rect: new UiRect(0, 628, 300, 177));
        bridge.SimulateLeagueOverlayFilled(0x8800,
        [
            new LeagueCardInfo(0, "Standard", stdCard3.Handle, stdStack3.Handle, 0, 580f, 628f, 0f),
        ]);
        nint syncedCardCb = Assert.Single(bridge.GetNativeUiChildren(stdStack3.Handle));
        Assert.True(bridge.GetSimulatedNativeUiControlByHandle(syncedCardCb)!.IsChecked);
    }

    [Theory]
    [InlineData(NetTransportKind.HostRuleset)]
    [InlineData(NetTransportKind.ChatFallback)]
    public async Task EndToEnd_ServerNoticeDeliveredViaServerBridge_ShowsHudToastOnGameThread(NetTransportKind transport)
    {
        string workspace = Path.Combine(Path.GetTempPath(), "nm-skip-e2e-" + Guid.NewGuid().ToString("N"));
        string shadowRoot = Path.Combine(workspace, "shadow");
        try
        {
            string modDir = TestPaths.StageClientModDirectory(workspace);

            using var simBridge = new SimulatedNativeBridge(ClientBuildId.Build3293d) { EnableSimulatedNativeUi = true };
            using var clientHost = new ClientHostRuntime(simBridge, new ClientHostOptions
            {
                TransportKind = transport,
                HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId,
                ShadowCopyRoot = shadowRoot,
            });
            clientHost.Start();

            await RunDualSideToastFlowAsync(simBridge, clientHost, modDir, transport);

            ModUnloadResult unload = await clientHost.Loader.UnloadModAsync(SkipCampaignProtocol.ModId);
            Assert.True(unload.WasLoaded);
            Assert.True(unload.UnloadedCleanly);
            Assert.Empty(clientHost.Toasts.ActiveToasts);
        }
        finally
        {
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task RunDualSideToastFlowAsync(
        SimulatedNativeBridge simBridge,
        ClientHostRuntime clientHost,
        string modDir,
        NetTransportKind transport)
    {
        clientHost.Loader.LoadModFromDirectory(modDir);

        var sink = new LoopbackSink(simBridge, transport);
        using var serverBridge = new ClientBridgeHost(sink, new ClientBridgeOptions
        {
            DefaultChannel = SkipCampaignProtocol.ChannelName,
            HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId,
            DefaultTransport = transport,
        });

        // Wire client -> server loopback so ClientHandshake completes over the chosen transport.
        if (transport == NetTransportKind.HostRuleset)
        {
            simBridge.OnOutgoingRulesetPacket = (rulesetId, payload) =>
            {
                byte[] framed = MessageCodec.EncodeRulesetBody(rulesetId, payload.Span);
                serverBridge.HandleIncomingPacketAsync("e2e-conn", IOpcodeRegistry.ClientRulesetOpcode, framed, "SkipHero").GetAwaiter().GetResult();
            };
        }
        else
        {
            simBridge.OnOutgoingChatLine = line =>
            {
                serverBridge.HandleIncomingChatCommandAsync("e2e-conn", line, "SkipHero").GetAwaiter().GetResult();
            };
        }

        // Complete client handshake (simulates arriving in Karui Shores).
        clientHost.NotifyConnected();
        clientHost.SendHandshake();
        Assert.True(serverBridge.IsClientReady("e2e-conn"));
        clientHost.TickFrame();

        // Simulate C->S 0x12 with Skip Campaign toggled via CreateCharacterBar (skipping LeagueOverlay completely).
        var createBarParent = simBridge.RegisterSimulatedStockControl("create_character_bottom_flow");
        simBridge.SetSimulatedSlotParentHandle(ScreenSlot.CreateCharacterBar, createBarParent.Handle);
        simBridge.SimulatedPreGameScreen = PreGameScreen.CreateCharacter;
        clientHost.TickFrame();
        clientHost.TickFrame();

        ISlotAttachment bar = Assert.Single(clientHost.Screens.GetSlotAttachments(ScreenSlot.CreateCharacterBar));
        bar.Control!.Require<ICheckBox>(SkipCampaignProtocol.CreateCharacterCheckBoxId).IsChecked = true;

        string wireS78 = simBridge.SimulateCharacterCreating("SkipHero", "Standard", "DexDefault");
        Assert.Equal("DexDefault|nm:skip_campaign=1", wireS78);
        Assert.True(CharacterCreateContext.TryParseStartState(wireS78, out string baseState, out var parsedOptions));
        Assert.Equal("DexDefault", baseState);

        // Server evaluates CharacterCreated -> approves Karui Shores Lv.68 + SuppressStartKit.
        var handler = new SkipCampaignHandler(
            SkipCampaignSettings.Default with { RequireLeagueCompletion = false },
            new EmptyEligibilitySource());
        using var serverMod = new SkipCampaignServerMod(serverBridge, handler);
        serverMod.Attach();

        Guid heroId = Guid.NewGuid();
        var effect = serverMod.HandleCharacterCreated(new SkipCampaignCharacterCreatedInput(
            "exile", heroId, "SkipHero", "Standard", false, false, parsedOptions));
        Assert.True(effect.Applied);
        Assert.Equal("2_11_endgame_town", effect.StartingAreaId);
        Assert.Equal(68, effect.StartingLevel);
        Assert.True(effect.SuppressStartKit);

        // Client receives S->C 0x13 success and transitions in-game.
        simBridge.SimulateCharacterCreated("SkipHero", "Standard", 0);
        simBridge.SimulatedPreGameScreen = PreGameScreen.InGame;
        clientHost.TickFrame();

        // Server runs OnPlayerEnteredAsync from a background thread to verify game-thread marshaling!
        var world = new MinimalWorld(heroId, "SkipHero", "Standard");
        SkipCampaignFirstEnterResult entered = await Task.Run(() => serverMod.OnPlayerEnteredAsync("e2e-conn", world));
        Assert.True(entered.Applied);

        // Drain game thread queue on next frame -> HUD toast must be visible!
        clientHost.TickFrame();

        ToastMessage toast = Assert.Single(clientHost.Toasts.ActiveToasts);
        Assert.Equal("Campaign Skipped", toast.Title);
        Assert.Equal(ToastSeverity.Success, toast.Severity);
        Assert.Contains("Lv.68", toast.Message, StringComparison.Ordinal);
        Assert.Contains("Karui Shores", toast.Message, StringComparison.Ordinal);
        Assert.Contains("50,000 gold", toast.Message, StringComparison.Ordinal);
    }

    private sealed class LoopbackSink(
        SimulatedNativeBridge simBridge,
        NetTransportKind transport) : IGatewayPacketSink
    {
        public Task SendPacketAsync(string connectionId, ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            if (transport == NetTransportKind.HostRuleset &&
                opcode == IOpcodeRegistry.ServerRulesetOpcode &&
                MessageCodec.TryDecodeRulesetBody(payload, out byte rulesetId, out ReadOnlyMemory<byte> inner))
            {
                simBridge.SimulateIncomingRulesetPacket(rulesetId, inner);
            }

            return Task.CompletedTask;
        }

        public Task SendChatReplyAsync(string connectionId, string text, CancellationToken cancellationToken = default)
        {
            if (transport == NetTransportKind.ChatFallback)
            {
                simBridge.SimulateIncomingChat("Server", text);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class EmptyEligibilitySource : ISkipCampaignEligibilitySource
    {
        public IReadOnlyList<SkipCampaignLeagueCharacterSnapshot> GetLeagueCharacters(string account, string league, bool includeDead = false) => [];
    }

    private sealed class MinimalWorld(Guid id, string name, string league) : ISkipCampaignPlayerWorld
    {
        private readonly Dictionary<string, string> _data = new(StringComparer.Ordinal);
        public Guid CharacterId { get; } = id;
        public string CharacterName { get; } = name;
        public string League { get; } = league;
        public uint Gold { get; private set; }
        public ushort RefundPoints { get; private set; }
        public bool HasQuestFlag(string flag, bool earnedOnly = false) => false;
        public Task SetQuestFlagsAsync(IEnumerable<string> flags, bool on = true, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> GrantWaypointsAsync(IEnumerable<string> areaIds, string? reason = null, CancellationToken cancellationToken = default) => Task.FromResult(areaIds.Count());
        public Task<ushort> SetRefundPointsAsync(ushort points, CancellationToken cancellationToken = default) { RefundPoints = points; return Task.FromResult(points); }
        public Task<int> AwardAscendancyAsync(IEnumerable<string> awardKeys, CancellationToken cancellationToken = default) => Task.FromResult(awardKeys.Count() * 2);
        public Task<uint> AddGoldAsync(long delta, string? reason = null, CancellationToken cancellationToken = default) { Gold = (uint)Math.Clamp((long)Gold + delta, 0, uint.MaxValue); return Task.FromResult(Gold); }
        public string? GetCharacterData(string key) => _data.TryGetValue(key, out string? v) ? v : null;
        public bool ChangeCharacterData(string key, Func<string?, string?> change)
        {
            string? next = change(GetCharacterData(key));
            if (next is null) return false;
            _data[key] = next;
            return true;
        }
    }
}
