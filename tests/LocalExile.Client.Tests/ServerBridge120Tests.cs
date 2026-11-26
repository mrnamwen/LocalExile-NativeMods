using LocalExile.Client.Host;
using LocalExile.Client.Host.Native;
using LocalExile.Client.Sdk.Addresses;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.Sdk.Protocol;
using LocalExile.Client.ServerBridge;
using Xunit;

namespace LocalExile.Client.Tests;

public sealed class ServerBridge120Tests
{
    private const string ConnId = "player-tier1-120-conn";

    private sealed class Phase4Harness : IDisposable
    {
        public string TempDir { get; }
        public SimulatedNativeBridge SimBridge { get; }
        public ClientHostRuntime ClientHost { get; }
        public ClientBridgeHost ServerBridge { get; }
        public IClientSession Session { get; }

        public Phase4Harness(NetTransportKind transport = NetTransportKind.HostRuleset)
        {
            TempDir = Path.Combine(Path.GetTempPath(), $"nativemods-sb120-{Guid.NewGuid():N}");
            Directory.CreateDirectory(TempDir);

            SimBridge = new SimulatedNativeBridge(ClientBuildId.Build3293d)
            {
                EnableSimulatedNativeUi = true,
            };

            ClientHost = new ClientHostRuntime(SimBridge, new ClientHostOptions
            {
                TransportKind = transport,
                HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId,
                KeybindsPath = Path.Combine(TempDir, "keybinds.json"),
            });
            ClientHost.Start();

            var sink = new LoopbackGatewaySink(SimBridge);
            ServerBridge = new ClientBridgeHost(sink, new ClientBridgeOptions
            {
                DefaultTransport = transport,
                HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId,
            });

            SimBridge.OnOutgoingRulesetPacket = (rulesetId, innerPayload) =>
            {
                byte[] body = MessageCodec.EncodeRulesetBody(rulesetId, innerPayload.Span);
                ServerBridge.HandleIncomingPacketAsync(ConnId, IOpcodeRegistry.ClientRulesetOpcode, body).GetAwaiter().GetResult();
            };
            SimBridge.OnOutgoingChatLine = line =>
            {
                ServerBridge.HandleIncomingChatCommandAsync(ConnId, line).GetAwaiter().GetResult();
            };

            ClientHost.SendHandshake();
            Session = ServerBridge.For(ConnId);
        }

        public void Dispose()
        {
            ServerBridge.Dispose();
            ClientHost.Dispose();
            SimBridge.Dispose();
            try
            {
                if (Directory.Exists(TempDir))
                {
                    Directory.Delete(TempDir, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private sealed class LoopbackGatewaySink(SimulatedNativeBridge simBridge) : IGatewayPacketSink
    {
        public Task SendPacketAsync(string connectionId, ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            if (opcode == IOpcodeRegistry.ServerRulesetOpcode &&
                MessageCodec.TryDecodeRulesetBody(payload, out byte rulesetId, out ReadOnlyMemory<byte> inner))
            {
                simBridge.SimulateIncomingRulesetPacket(rulesetId, inner);
            }
            else
            {
                simBridge.SimulateIncomingRawPacket(opcode, payload);
            }

            return Task.CompletedTask;
        }

        public Task SendChatReplyAsync(string connectionId, string text, CancellationToken cancellationToken = default)
        {
            simBridge.SimulateIncomingChat("System", text);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void Handshake_AdvertisesPhase4And5ServerAndClientCapabilities()
    {
        using var harness = new Phase4Harness();

        ServerHandshake? serverHs = harness.ClientHost.LastServerHandshake;
        Assert.NotNull(serverHs);
        Assert.Contains("tier1.tooltips", serverHs!.ServerCapabilities);
        Assert.Contains("tier1.affinity", serverHs.ServerCapabilities);
        Assert.Contains("tier1.rich-ui", serverHs.ServerCapabilities);
        Assert.Contains("tier1.audio-ex", serverHs.ServerCapabilities);
        Assert.Contains("tier1.stacks-u32", serverHs.ServerCapabilities);

        ClientHandshake? clientHs = harness.Session.Handshake;
        Assert.NotNull(clientHs);
        Assert.Contains("stacks.u32", clientHs!.Capabilities);
        Assert.Contains("ui.compact-sockets", clientHs.Capabilities);
        Assert.Contains("audio.spatial", clientHs.Capabilities);
        Assert.Contains("world.play-effect", clientHs.Capabilities);
    }

    [Theory]
    [InlineData(NetTransportKind.HostRuleset)]
    [InlineData(NetTransportKind.ChatFallback)]
    public async Task TooltipsAndStashAffinity_RoundTripDeclarativeRulesAndCustomOverlay(NetTransportKind transport)
    {
        using var harness = new Phase4Harness(transport);

        // 1. Custom anchored tooltip show/hide
        var content = TooltipContent.FromLines("League Altar", [new TooltipLine("+40% Pack Size", "rgb(136, 136, 255)")], ItemRarity.Unique);
        await harness.Session.ShowTooltipAsync(content, 420f, 310f);
        Assert.True(harness.ClientHost.Tooltips.IsCustomTooltipVisible);
        Assert.Equal("League Altar", harness.ClientHost.Tooltips.ActiveCustomTooltip?.Title);
        Assert.Equal((420f, 310f), harness.ClientHost.Tooltips.CustomTooltipPosition);

        await harness.Session.HideTooltipAsync();
        Assert.False(harness.ClientHost.Tooltips.IsCustomTooltipVisible);

        // 2. Declarative stock item tooltip rule (metadata prefix + Alt-held filter)
        var rule = new ServerItemTooltipRule(
            Id: "idol-price",
            Sections: [TooltipSection.SingleLine("League Value: 120 Chaos", "rgb(255, 215, 0)")],
            MetadataPrefix: "Metadata/Items/Mods/Idols/",
            RequireAltHeld: true,
            Priority: 5);

        await using ServerItemTooltipRuleHandle ruleHandle = await harness.Session.RegisterItemTooltipRuleAsync(rule);
        Assert.Equal(1, harness.ClientHost.Tier1Handler.RegisteredTooltipRuleCount);

        var matchingItem = CreateItem("Metadata/Items/Mods/Idols/SolarIdol", "Solar Idol", itemClassId: 52u);
        var otherItem = CreateItem("Metadata/Items/Currency/CurrencyRerollRare", "Chaos Orb", itemClassId: 14u);

        // Alt not held -> no match
        Assert.Empty(harness.ClientHost.Tooltips.EvaluateItemTooltipAugmenters(new ItemTooltipContext(matchingItem, IsAltHeld: false)));
        // Alt held + matching prefix -> appended!
        IReadOnlyList<TooltipSection> augmented = harness.ClientHost.Tooltips.EvaluateItemTooltipAugmenters(new ItemTooltipContext(matchingItem, IsAltHeld: true));
        Assert.Single(augmented);
        Assert.Equal("League Value: 120 Chaos", augmented[0].Lines[0].Text);
        // Non-matching prefix -> empty
        Assert.Empty(harness.ClientHost.Tooltips.EvaluateItemTooltipAugmenters(new ItemTooltipContext(otherItem, IsAltHeld: true)));

        // Remove rule via handle
        await ruleHandle.DisposeAsync();
        Assert.Equal(0, harness.ClientHost.Tier1Handler.RegisteredTooltipRuleCount);
        Assert.Empty(harness.ClientHost.Tooltips.EvaluateItemTooltipAugmenters(new ItemTooltipContext(matchingItem, IsAltHeld: true)));

        // 3. Declarative Stash Affinity route
        await using ServerStashAffinityRouteHandle affinityHandle = await harness.Session.RegisterStashAffinityRouteAsync(
            id: "route-idols",
            targetTabIndex: 7,
            metadataPrefix: "Metadata/Items/Mods/Idols/",
            stashKind: StashKind.Personal,
            replaceStockTargets: true);

        Assert.Equal(1, harness.ClientHost.Tier1Handler.RegisteredAffinityRouteCount);
        IReadOnlyList<StashAffinityRoute> personalRoutes = harness.ClientHost.StashAffinity.EvaluateRoutes(
            new StashAffinityItemContext(matchingItem, StashKind.Personal));
        Assert.Single(personalRoutes);
        Assert.Equal((ushort)7, personalRoutes[0].TabIndex);
        Assert.True(personalRoutes[0].ReplaceStockTargets);

        // Guild stash doesn't match Personal-only filter
        Assert.Empty(harness.ClientHost.StashAffinity.EvaluateRoutes(new StashAffinityItemContext(matchingItem, StashKind.Guild)));

        await affinityHandle.DisposeAsync();
        Assert.Equal(0, harness.ClientHost.Tier1Handler.RegisteredAffinityRouteCount);
        Assert.Empty(harness.ClientHost.StashAffinity.EvaluateRoutes(new StashAffinityItemContext(matchingItem, StashKind.Personal)));
    }

    [Fact]
    public async Task RichUiAndItemGrid_PictureComboBoxTabAndGridCrudWithCellClickRouting()
    {
        using var harness = new Phase4Harness();

        using IDisposable uiOverlay = harness.ClientHost.FileOverlayService.RegisterVirtualTextFile(
            "Metadata/UI/Mods/League/Vault.ui",
            """
            begin Frame
                id = league_vault;
                size = 700, 520;
                begin Picture
                    id = banner_pic;
                    size = 128, 64;
                end
                begin ComboBox
                    id = tier_combo;
                    size = 180, 28;
                end
                begin TabContainer
                    id = main_tabs;
                    size = 660, 420;
                end
                begin Frame
                    id = grid_host;
                    size = 300, 300;
                end
            end
            """);

        ServerUiWindowHandle winHandle = await harness.Session.Ui.OpenAsync("Metadata/UI/Mods/League/Vault.ui", "league_vault");
        IUiWindow? clientWin = harness.ClientHost.Ui.GetWindow("league_vault");
        Assert.NotNull(clientWin);

        // Populate combo & tabs on client control
        IComboBox combo = clientWin!.Require<IComboBox>("tier_combo");
        combo.AddItem("Tier 1");
        combo.AddItem("Tier 2");
        combo.AddItem("Tier 3");

        ITabContainer tabs = clientWin.Require<ITabContainer>("main_tabs");
        tabs.AddTab("Vault");
        tabs.AddTab("Craft");

        // Server drives Picture, ComboBox, TabContainer!
        await harness.Session.SetPictureTextureAsync("league_vault", "banner_pic", "Art/2DArt/UIImages/Mods/League/Banner.dds");
        await harness.Session.SetComboBoxSelectedAsync("league_vault", "tier_combo", 2);
        await harness.Session.SelectTabAsync("league_vault", "main_tabs", 1);

        Assert.Equal("Art/2DArt/UIImages/Mods/League/Banner.dds", clientWin.Require<IPicture>("banner_pic").TexturePath);
        Assert.Equal(2, combo.SelectedIndex);
        Assert.Equal(1, tabs.ActiveTabIndex);

        // Server creates ItemGrid, places/updates/removes/clears items, and receives cell clicks!
        await harness.Session.CreateItemGridAsync("league_vault", "grid_host", "vault_grid", new UiItemGridOptions(4, 4, CellSize: 48f));
        IUiItemGrid grid = clientWin.Require<IUiItemGrid>("grid_host.vault_grid");
        Assert.Equal(4, grid.Columns);
        Assert.Equal(4, grid.Rows);

        var clickedEvents = new List<ClientGridCellClickedEvent>();
        using IDisposable clickSub = await harness.Session.OnGridCellClickAsync("league_vault", "vault_grid", (_, evt) => clickedEvents.Add(evt));

        await harness.Session.SetGridItemAsync("league_vault", "vault_grid", new ServerGridItem("idol-1", Col: 1, Row: 2, Width: 1, Height: 2, StackSize: 1, Rarity: ItemRarity.Unique, Title: "Sun Idol"));
        Assert.Equal("idol-1", grid.GetItemAt(1, 2)?.ItemId);
        Assert.Equal("idol-1", grid.GetItemAt(1, 3)?.ItemId);

        // Trigger left, right, and Ctrl clicks on client grid -> routed back to server!
        grid.TriggerCellClick(1, 2, MouseButton.Left, KeyModifiers.None);
        grid.TriggerCellClick(1, 3, MouseButton.Right, KeyModifiers.None);
        grid.TriggerCellClick(1, 2, MouseButton.Left, KeyModifiers.Control);

        Assert.Equal(3, clickedEvents.Count);
        Assert.Equal(("idol-1", MouseButton.Left, KeyModifiers.None), (clickedEvents[0].ItemId, clickedEvents[0].Button, clickedEvents[0].Modifiers));
        Assert.Equal(("idol-1", MouseButton.Right, KeyModifiers.None), (clickedEvents[1].ItemId, clickedEvents[1].Button, clickedEvents[1].Modifiers));
        Assert.Equal(("idol-1", MouseButton.Left, KeyModifiers.Control), (clickedEvents[2].ItemId, clickedEvents[2].Button, clickedEvents[2].Modifiers));

        // Update/move existing item via SetGridItemAsync (upsert!)
        await harness.Session.SetGridItemAsync("league_vault", "vault_grid", new ServerGridItem("idol-1", Col: 0, Row: 0, StackSize: 5));
        Assert.Null(grid.GetItemAt(1, 2));
        Assert.Equal(5, grid.GetItemAt(0, 0)?.StackSize);

        await harness.Session.RemoveGridItemAsync("league_vault", "vault_grid", "idol-1");
        Assert.Empty(grid.Items);

        await harness.Session.SetGridItemAsync("league_vault", "vault_grid", new ServerGridItem("idol-2", Col: 2, Row: 2));
        Assert.Single(grid.Items);
        await harness.Session.ClearGridAsync("league_vault", "vault_grid");
        Assert.Empty(grid.Items);

        await winHandle.CloseAsync();
    }

    [Fact]
    public async Task AudioWorldAndPhase5Toggles_WorkAndTearDownCompletelyOnDisconnect()
    {
        using var harness = new Phase4Harness();

        harness.SimBridge.SimulatedCameraState = new CameraState(
            IsValid: true,
            Position: new WorldPosition3D(-462f, -462f, -981.75f),
            Target: new WorldPosition3D(0f, 0f, 0f),
            Zoom: 1.0f,
            ZoomOverride: -1.0f,
            ZoomTarget: 1.0f,
            Fov: MathF.PI / 4f,
            ViewportWidth: 1920f,
            ViewportHeight: 1080f,
            ViewProjectionMatrix: [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]);

        // 1. PlayAudioExAsync (2D looping .ogg) + StopAudioAsync
        AudioChannelHandle bgm = await harness.Session.PlayAudioExAsync("Audio/Mods/League/theme.ogg", volume: 0.65f, loop: true);
        Assert.True(bgm.IsValid);
        Assert.True(harness.SimBridge.TryGetSimulatedAudioChannel(bgm, out string bgmPath, out bool bgmFmod, out float bgmVol, out bool bgmLoop, out bool bgmActive));
        Assert.Equal("Audio/Mods/League/theme.ogg", bgmPath);
        Assert.False(bgmFmod);
        Assert.Equal(0.65f, bgmVol, precision: 3);
        Assert.True(bgmLoop);
        Assert.True(bgmActive);

        // 2. PlaySoundAtPositionAsync + SetAudioPositionAsync
        AudioChannelHandle spatial = await harness.Session.PlaySoundAtPositionAsync("event:/League/PortalLoop", 600f, 0f, 0f, volume: 1.0f, maxDistance: 1200f, loop: true);
        Assert.True(spatial.IsValid);
        Assert.True(harness.SimBridge.TryGetSimulatedAudioChannel(spatial, out _, out bool spatialFmod, out float spatialVol, out _, out bool spatialActive));
        Assert.True(spatialFmod);
        Assert.True(spatialActive);
        Assert.Equal(0.5f, spatialVol, precision: 3);

        await harness.Session.SetAudioPositionAsync(spatial, 300f, 0f, 0f, maxDistance: 1200f);
        Assert.True(harness.SimBridge.TryGetSimulatedAudioChannel(spatial, out _, out _, out spatialVol, out _, out _));
        Assert.Equal(0.75f, spatialVol, precision: 3);

        // 3. SpawnClientObjectAsync + PlayEffectAsync
        uint objId = await harness.Session.SpawnClientObjectAsync("Metadata/MiscellaneousObjects/Mods/League/Beacon", 10f, 20f, 30f, rotationRadians: 0.5f, scale: 1.5f);
        Assert.NotEqual(0u, objId);
        Assert.NotNull(harness.ClientHost.World.FindObject(objId));
        Assert.Equal((0.5f, 1.5f), harness.SimBridge.SimulatedClientObjectTransforms[objId]);

        await harness.Session.Ui.PlayEffectAsync(objId, "Metadata/Effects/League/BeaconBeam.epk");
        Assert.Contains(harness.SimBridge.PlayedEffects, e => e.ObjectId == objId && e.EffectPath == "Metadata/Effects/League/BeaconBeam.epk");

        // 4. Phase 5 toggles + tooltip + affinity + stack override
        await harness.Session.SetExtendedU32StacksAsync(enabled: true, formatMillions: true);
        await harness.Session.SetEntityStackOverrideAsync(9001u, 1_500_000u);
        await harness.Session.SetCompactSocketsAsync(enabled: true);
        await harness.Session.ShowTooltipAsync(TooltipContent.FromLines("Active", [new TooltipLine("Live")]), 100f, 100f);
        await harness.Session.RegisterItemTooltipRuleAsync(new ServerItemTooltipRule("dc-rule", [TooltipSection.SingleLine("Test")]));
        await harness.Session.RegisterStashAffinityRouteAsync("dc-route", targetTabIndex: 3);

        Assert.True(harness.ClientHost.StackExtension.IsExtendedU32StacksEnabled);
        Assert.True(harness.ClientHost.StackExtension.IsFormatMillionsEnabled);
        Assert.True(harness.ClientHost.Ui.IsCompactSocketsEnabled);
        Assert.Equal(1_500_000u, harness.SimBridge.SimulatedEntityStackOverrides[9001u]);
        Assert.Equal(2, harness.ClientHost.Tier1Handler.OwnedAudioChannelCount);
        Assert.Equal(1, harness.ClientHost.Tier1Handler.OwnedWorldObjectCount);

        // Stop one audio channel explicitly
        await harness.Session.StopAudioAsync(bgm);
        Assert.False(harness.SimBridge.IsAudioPlaying(bgm));
        Assert.Equal(1, harness.ClientHost.Tier1Handler.OwnedAudioChannelCount);

        // 5. Simulate disconnect -> EVERYTHING server-owned must be cleaned up!
        harness.ClientHost.NotifyDisconnected("session-ended");

        Assert.False(harness.SimBridge.IsAudioPlaying(spatial));
        Assert.Null(harness.ClientHost.World.FindObject(objId));
        Assert.False(harness.ClientHost.StackExtension.IsExtendedU32StacksEnabled);
        Assert.False(harness.ClientHost.StackExtension.IsFormatMillionsEnabled);
        Assert.False(harness.SimBridge.SimulatedExtendedU32StacksEnabled);
        Assert.False(harness.SimBridge.SimulatedFormatMillionsEnabled);
        Assert.False(harness.ClientHost.Ui.IsCompactSocketsEnabled);
        Assert.False(harness.SimBridge.SimulatedCompactSocketsEnabled);
        Assert.False(harness.ClientHost.Tooltips.IsCustomTooltipVisible);
        Assert.Equal(0, harness.ClientHost.Tier1Handler.RegisteredTooltipRuleCount);
        Assert.Equal(0, harness.ClientHost.Tier1Handler.RegisteredAffinityRouteCount);
        Assert.Equal(0, harness.ClientHost.Tier1Handler.OwnedAudioChannelCount);
        Assert.Equal(0, harness.ClientHost.Tier1Handler.OwnedWorldObjectCount);
    }

    private static HoveredItemInfo CreateItem(string metadataPath, string baseName, uint itemClassId) =>
        new(
            EntityHandle: 0x2001,
            ControlHandle: 0x3001,
            ItemId: 42u,
            InventoryId: 1u,
            GridX: 0,
            GridY: 0,
            Width: 1,
            Height: 1,
            MetadataPath: metadataPath,
            BaseName: baseName,
            StackSize: 1u,
            MaxStackSize: 20u,
            Rarity: ItemRarity.Unique,
            IsIdentified: true,
            IsCorrupted: false,
            ItemLevel: 84,
            ItemClassId: itemClassId);
}
