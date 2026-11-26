using LocalExile.Client.Host;
using LocalExile.Client.Host.Native;
using LocalExile.Client.Host.Services;
using LocalExile.Client.Sdk.Addresses;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.Sdk.Protocol;
using LocalExile.Client.ServerBridge;
using Xunit;

namespace LocalExile.Client.Tests;

public sealed class ServerBridge040Tests
{
    private const string ConnId = "player-tier1-040-conn";

    private sealed class Phase2Harness : IDisposable
    {
        public string TempDir { get; }
        public SimulatedNativeBridge SimBridge { get; }
        public ClientHostRuntime ClientHost { get; }
        public ClientBridgeHost ServerBridge { get; }
        public IClientSession Session { get; }

        public Phase2Harness(NetTransportKind transport = NetTransportKind.HostRuleset, bool connectHandshake = true)
        {
            TempDir = Path.Combine(Path.GetTempPath(), $"nativemods-sb040-{Guid.NewGuid():N}");
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

            if (connectHandshake)
            {
                ClientHost.SendHandshake();
            }

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
                // Best-effort temp dir cleanup.
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
            simBridge.SimulateIncomingChat("Server", text);
            return Task.CompletedTask;
        }
    }

    private sealed class BlackHoleGatewaySink : IGatewayPacketSink
    {
        public Task SendPacketAsync(string connectionId, ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SendChatReplyAsync(string connectionId, string text, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // 1. Handshake Capabilities (0.2.0 / 0.3.0 preserved + 0.4.0 added)
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(NetTransportKind.HostRuleset)]
    [InlineData(NetTransportKind.ChatFallback)]
    public void Tier1_040_HandshakeAdvertisesPhase2Capabilities_AndPreservesLegacyCapabilities(NetTransportKind transport)
    {
        using var harness = new Phase2Harness(transport);

        Assert.True(harness.Session.HasNativeHost);
        Assert.NotNull(harness.ClientHost.LastServerHandshake);
        IReadOnlyList<string> serverCaps = harness.ClientHost.LastServerHandshake!.ServerCapabilities;

        // Legacy 0.2.0 / 0.3.0 capabilities preserved
        Assert.Contains("ruleset.0x2c9", serverCaps);
        Assert.Contains("chat.fallback", serverCaps);
        Assert.Contains("ui.server-driven", serverCaps);
        Assert.Contains("tier1.slots", serverCaps);
        Assert.Contains("tier1.toasts", serverCaps);
        Assert.Contains("tier1.hotkeys", serverCaps);
        Assert.Contains("tier1.queries", serverCaps);

        // New 0.4.0 Phase 2 capabilities advertised
        Assert.Contains("tier1.world", serverCaps);
        Assert.Contains("tier1.stats", serverCaps);
        Assert.Contains("tier1.area", serverCaps);
        Assert.Contains("tier1.filter", serverCaps);
        Assert.Contains("tier1.camera", serverCaps);

        // Phase2 facade exposed directly on IClientSession
        Assert.NotNull(harness.Session.Phase2);
    }

    // -------------------------------------------------------------------------
    // 2. World Entities, Visible Ground Items & Local Player Queries
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(NetTransportKind.HostRuleset)]
    [InlineData(NetTransportKind.ChatFallback)]
    public async Task Tier1_040_WorldQueries_LocalPlayerEntitiesAndVisibleGroundItems_RoundTrip(NetTransportKind transport)
    {
        using var harness = new Phase2Harness(transport);

        // Before entering an area, local player is null and entities are empty
        Assert.Null(await harness.Session.QueryLocalPlayerAsync());
        Assert.Empty(await harness.Session.QueryWorldEntitiesAsync());

        var player = new WorldEntitySnapshot(
            EntityId: 100u,
            EntityHandle: 0x1000,
            Kind: WorldEntityKind.Player | WorldEntityKind.LocalPlayer | WorldEntityKind.Alive,
            MetadataPath: "Metadata/Characters/Dex/Dex",
            GridX: 10,
            GridY: 10,
            Position: new WorldPosition3D(100f, 100f, 0f),
            Hp: 2500,
            MaxHp: 2500);

        var aliveMonster = new WorldEntitySnapshot(
            EntityId: 201u,
            EntityHandle: 0x2001,
            Kind: WorldEntityKind.Monster | WorldEntityKind.Alive | WorldEntityKind.Targetable,
            MetadataPath: "Metadata/Monsters/Zombies/ZombieStandard",
            GridX: 12,
            GridY: 10,
            Position: new WorldPosition3D(120f, 100f, 0f),
            Hp: 450,
            MaxHp: 500,
            Rarity: ItemRarity.Rare);

        var deadMonster = new WorldEntitySnapshot(
            EntityId: 202u,
            EntityHandle: 0x2002,
            Kind: WorldEntityKind.Monster,
            MetadataPath: "Metadata/Monsters/Zombies/ZombieStandard",
            GridX: 13,
            GridY: 10,
            Position: new WorldPosition3D(130f, 100f, 0f),
            Hp: 0,
            MaxHp: 500);

        var divineInfo = new HoveredItemInfo(
            EntityHandle: 0x3001,
            ControlHandle: nint.Zero,
            ItemId: 501u,
            InventoryId: 0u,
            GridX: 0,
            GridY: 0,
            Width: 1,
            Height: 1,
            MetadataPath: "Metadata/Items/Currency/CurrencyModValues",
            BaseName: "Divine Orb",
            StackSize: 2u,
            MaxStackSize: 20u,
            Rarity: ItemRarity.Normal,
            IsIdentified: true,
            IsCorrupted: false,
            ItemLevel: 84,
            ItemClassId: 5u);

        var visibleNearDrop = new WorldEntitySnapshot(
            EntityId: 301u,
            EntityHandle: 0x3010,
            Kind: WorldEntityKind.WorldItem | WorldEntityKind.Targetable,
            MetadataPath: "Metadata/MiscellaneousObjects/WorldItem",
            GridX: 11,
            GridY: 10,
            Position: new WorldPosition3D(115f, 100f, 0f),
            IsFilterVisible: true,
            WorldItem: divineInfo);

        var hiddenNearDrop = new WorldEntitySnapshot(
            EntityId: 302u,
            EntityHandle: 0x3020,
            Kind: WorldEntityKind.WorldItem | WorldEntityKind.Targetable,
            MetadataPath: "Metadata/MiscellaneousObjects/WorldItem",
            GridX: 11,
            GridY: 11,
            Position: new WorldPosition3D(110f, 110f, 0f),
            IsFilterVisible: false);

        var visibleFarDrop = new WorldEntitySnapshot(
            EntityId: 303u,
            EntityHandle: 0x3030,
            Kind: WorldEntityKind.WorldItem | WorldEntityKind.Targetable,
            MetadataPath: "Metadata/MiscellaneousObjects/WorldItem",
            GridX: 50,
            GridY: 50,
            Position: new WorldPosition3D(500f, 500f, 0f),
            IsFilterVisible: true);

        harness.SimBridge.SetSimulatedLocalPlayer(player);
        harness.SimBridge.AddSimulatedEntity(aliveMonster);
        harness.SimBridge.AddSimulatedEntity(deadMonster);
        harness.SimBridge.AddSimulatedEntity(visibleNearDrop);
        harness.SimBridge.AddSimulatedEntity(hiddenNearDrop);
        harness.SimBridge.AddSimulatedEntity(visibleFarDrop);

        // QueryLocalPlayerAsync (both on session and via ClientBridgeHost)
        ClientWorldEntitySnapshot? localPlayer = await harness.ServerBridge.QueryLocalPlayerAsync(ConnId);
        Assert.NotNull(localPlayer);
        Assert.Equal(100u, localPlayer!.EntityId);
        Assert.Equal(0x1000ul, localPlayer.EntityHandle);
        Assert.True(localPlayer.IsPlayer);
        Assert.True(localPlayer.IsAlive);
        Assert.Equal(2500, localPlayer.Hp);
        Assert.Equal(new WorldPosition3D(100f, 100f, 0f), localPlayer.Position);

        // QueryVisibleGroundItemsAsync (all visible vs maxDistance bounded)
        IReadOnlyList<ClientWorldEntitySnapshot> allVisibleLoot = await harness.Session.QueryVisibleGroundItemsAsync();
        Assert.Equal(2, allVisibleLoot.Count);
        Assert.All(allVisibleLoot, e => Assert.True(e.IsWorldItem && e.IsFilterVisible));

        IReadOnlyList<ClientWorldEntitySnapshot> nearVisibleLoot = await harness.ServerBridge.QueryVisibleGroundItemsAsync(ConnId, maxDistance: 50f);
        ClientWorldEntitySnapshot nearItem = Assert.Single(nearVisibleLoot);
        Assert.Equal(301u, nearItem.EntityId);
        Assert.NotNull(nearItem.WorldItem);
        Assert.Equal("Divine Orb", nearItem.WorldItem!.BaseName);
        Assert.Equal(2u, nearItem.WorldItem.StackSize);
        Assert.Equal(84, nearItem.WorldItem.ItemLevel);
        Assert.Equal(0x3001ul, nearItem.WorldItem.EntityHandle);

        // QueryWorldEntitiesAsync with full filters
        IReadOnlyList<ClientWorldEntitySnapshot> allEntities = await harness.Session.QueryWorldEntitiesAsync();
        Assert.Equal(6, allEntities.Count);

        IReadOnlyList<ClientWorldEntitySnapshot> aliveZombies = await harness.Session.QueryWorldEntitiesAsync(
            new ClientWorldEntityQuery(
                RequiredKind: WorldEntityKind.Monster | WorldEntityKind.Alive,
                MetadataPrefix: "Metadata/Monsters/Zombies"));
        ClientWorldEntitySnapshot zombie = Assert.Single(aliveZombies);
        Assert.Equal(201u, zombie.EntityId);
        Assert.Equal(ItemRarity.Rare, zombie.Rarity);
        Assert.Equal(450, zombie.Hp);

        IReadOnlyList<ClientWorldEntitySnapshot> deadMonsters = await harness.Session.QueryWorldEntitiesAsync(
            new ClientWorldEntityQuery(
                RequiredKind: WorldEntityKind.Monster,
                ExcludedKind: WorldEntityKind.Alive));
        Assert.Single(deadMonsters);
        Assert.Equal(202u, deadMonsters[0].EntityId);

        IReadOnlyList<ClientWorldEntitySnapshot> capped = await harness.Session.QueryWorldEntitiesAsync(
            new ClientWorldEntityQuery(MaxResults: 2));
        Assert.Equal(2, capped.Count);

        // Validation guards
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => harness.Session.QueryVisibleGroundItemsAsync(maxDistance: -1f));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => harness.Session.QueryVisibleGroundItemsAsync(maxDistance: float.NaN));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => harness.Session.QueryWorldEntitiesAsync(new ClientWorldEntityQuery(MaxResults: -5)));
    }

    // -------------------------------------------------------------------------
    // 3. Entity & Local Player Stats (including High-ID >= 0x10000)
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(NetTransportKind.HostRuleset)]
    [InlineData(NetTransportKind.ChatFallback)]
    public async Task Tier1_040_StatsQueries_SupportLocalPlayerEntityAndHighIdCustomStats(NetTransportKind transport)
    {
        using var harness = new Phase2Harness(transport);

        harness.SimBridge.SetSimulatedLocalPlayer(new WorldEntitySnapshot(
            EntityId: 42u,
            EntityHandle: 0x4200,
            Kind: WorldEntityKind.Player | WorldEntityKind.LocalPlayer,
            MetadataPath: "Metadata/Characters/Int/Int",
            GridX: 0,
            GridY: 0,
            Position: default));

        const int StockLifeStat = 1;
        const int HighCustomStatA = 0x10001;
        const int HighCustomStatB = 0x1BEEF;

        harness.ClientHost.Stats.SetLocalStat(42u, StockLifeStat, 3450);
        harness.ClientHost.Stats.SetLocalStat(0u, HighCustomStatA, 999);
        harness.ClientHost.Stats.SetLocalStat(42u, HighCustomStatB, -12345);
        harness.ClientHost.Stats.SetLocalStat(900u, HighCustomStatA, 77);

        // QueryEntityStatsAsync(0) resolves local player entity id & returns all stats including >= 0x10000
        ClientEntityStatsSnapshot localStats = await harness.Session.QueryEntityStatsAsync();
        Assert.Equal(42u, localStats.EntityId);
        Assert.Equal(3, localStats.Stats.Count);
        Assert.Equal(3450, localStats.GetValueOrDefault(StockLifeStat));
        Assert.Equal(999, localStats.GetValueOrDefault(HighCustomStatA));
        Assert.Equal(-12345, localStats.GetValueOrDefault(HighCustomStatB));
        Assert.True(localStats.TryGetValue(HighCustomStatA, out int customVal));
        Assert.Equal(999, customVal);
        Assert.False(localStats.TryGetValue(0x20000, out _));

        // Explicit entity query
        ClientEntityStatsSnapshot monsterStats = await harness.ServerBridge.QueryEntityStatsAsync(ConnId, entityId: 900u);
        Assert.Equal(900u, monsterStats.EntityId);
        Assert.Single(monsterStats.Stats);
        Assert.Equal(77, monsterStats.GetValueOrDefault(HighCustomStatA));

        // Single stat query (found stock, found high-ID, missing)
        ClientStatValueSnapshot lifeStat = await harness.Session.QueryStatAsync(StockLifeStat);
        Assert.Equal(42u, lifeStat.EntityId);
        Assert.Equal(StockLifeStat, lifeStat.StatId);
        Assert.True(lifeStat.Found);
        Assert.Equal(3450, lifeStat.Value);

        ClientStatValueSnapshot highStat = await harness.ServerBridge.QueryStatAsync(ConnId, HighCustomStatB, entityId: 0u);
        Assert.Equal(42u, highStat.EntityId);
        Assert.True(highStat.Found);
        Assert.Equal(-12345, highStat.Value);

        ClientStatValueSnapshot missingStat = await harness.Session.QueryStatAsync(0x20000, entityId: 42u);
        Assert.Equal(42u, missingStat.EntityId);
        Assert.False(missingStat.Found);
        Assert.Equal(0, missingStat.Value);
    }

    // -------------------------------------------------------------------------
    // 4. Area Info + Opt-In OnAreaChangedAsync
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(NetTransportKind.HostRuleset)]
    [InlineData(NetTransportKind.ChatFallback)]
    public async Task Tier1_040_AreaInfoAndOptInAreaChangedNotifications_RoundTrip(NetTransportKind transport)
    {
        using var harness = new Phase2Harness(transport);

        Assert.False(harness.ClientHost.Tier1Handler.IsAreaSubscriptionActive);
        Assert.Null(await harness.Session.QueryAreaInfoAsync());

        var hideout = new AreaInfo(
            AreaId: "HideoutBattleScarred",
            AreaName: "Battle-scarred Hideout",
            Act: 11,
            AreaLevel: 60,
            EffectiveLevel: 60,
            Hash16: 0x3A2F,
            IsTown: false,
            IsHideout: true,
            IsGuildHideout: false,
            ActiveRulesetIds: [0x42u, 0xA0u],
            AreaSeed: 0x11223344u);

        // Subscribe to opt-in area changes
        List<ClientAreaChangedEvent> areaEvents = [];
        IDisposable areaSub = await harness.Session.OnAreaChangedAsync(async (_, evt, _) =>
        {
            await Task.Yield();
            lock (areaEvents) { areaEvents.Add(evt); }
        });
        Assert.True(harness.ClientHost.Tier1Handler.IsAreaSubscriptionActive);

        // Enter area -> pushes Entered event + QueryAreaInfoAsync returns full metadata
        harness.SimBridge.SimulateAreaEntered(hideout);
        await WaitUntilAsync(() => { lock (areaEvents) { return areaEvents.Count == 1; } });

        Assert.Equal(ClientAreaChangeKind.Entered, areaEvents[0].Kind);
        Assert.Equal("HideoutBattleScarred", areaEvents[0].AreaId);
        Assert.NotNull(areaEvents[0].Area);
        Assert.Equal(hideout.AreaName, areaEvents[0].Area!.AreaName);
        Assert.True(areaEvents[0].Area!.IsHideout);
        Assert.Equal(0x11223344u, areaEvents[0].Seed);
        Assert.Equal<uint>([0x42u, 0xA0u], areaEvents[0].ActiveRulesetIds!);

        AreaInfo? queriedArea = await harness.ServerBridge.QueryAreaInfoAsync(ConnId);
        Assert.NotNull(queriedArea);
        Assert.Equal(hideout.AreaId, queriedArea!.AreaId);
        Assert.Equal(hideout.AreaName, queriedArea.AreaName);
        Assert.Equal(hideout.Act, queriedArea.Act);
        Assert.Equal(hideout.EffectiveLevel, queriedArea.EffectiveLevel);
        Assert.Equal(hideout.Hash16, queriedArea.Hash16);
        Assert.Equal(hideout.AreaSeed, queriedArea.AreaSeed);
        Assert.Equal<uint>([0x42u, 0xA0u], queriedArea.ActiveRulesetIds);

        // Leave area -> pushes Left event + QueryAreaInfoAsync returns null
        harness.SimBridge.SimulateAreaLeft("HideoutBattleScarred");
        await WaitUntilAsync(() => { lock (areaEvents) { return areaEvents.Count == 2; } });
        Assert.Equal(ClientAreaChangeKind.Left, areaEvents[1].Kind);
        Assert.Equal("HideoutBattleScarred", areaEvents[1].AreaId);
        Assert.Null(areaEvents[1].Area);
        Assert.Null(await harness.Session.QueryAreaInfoAsync());

        // Dispose subscriber -> detaches client lifecycle events cleanly
        areaSub.Dispose();
        Assert.False(harness.ClientHost.Tier1Handler.IsAreaSubscriptionActive);

        var town = new AreaInfo("1_1_town", "Lioneye's Watch", 1, 1, 1, 0x1001, IsTown: true, IsHideout: false, IsGuildHideout: false, ActiveRulesetIds: []);
        harness.SimBridge.SimulateAreaEntered(town);
        Assert.Equal(2, areaEvents.Count);
    }

    // -------------------------------------------------------------------------
    // 5. Item Filter State, Reload, Evaluate & Opt-In OnFilterReloadedAsync
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(NetTransportKind.HostRuleset)]
    [InlineData(NetTransportKind.ChatFallback)]
    public async Task Tier1_040_ItemFilterStateReloadEvaluateAndReloadEvents_RoundTrip(NetTransportKind transport)
    {
        using var harness = new Phase2Harness(transport);

        Assert.False(harness.ClientHost.Tier1Handler.IsFilterSubscriptionActive);

        var filterState = new ItemFilterState(
            FilterName: "NeverSinkUberStrict.filter",
            FilterFolder: @"C:\Users\Exile\Documents\My Games\Path of Exile",
            FilterPath: @"C:\Users\Exile\Documents\My Games\Path of Exile\NeverSinkUberStrict.filter",
            IsLoaded: true,
            HideFilteredGroundItems: true,
            AltHighlightActive: false);
        harness.SimBridge.SimulatedItemFilterState = filterState;

        var cachedResult = new ItemFilterResult(
            Evaluated: true,
            IsVisible: true,
            FontSize: 45,
            TextRgba: 0xFF0000FFu,
            BorderRgba: 0xFF0000FFu,
            BackgroundRgba: 0xFFFFFFFFu,
            AlertSoundId: 6,
            MinimapIconShape: 0,
            MinimapIconColor: 0,
            MinimapIconSize: 0,
            BeamColor: 0,
            BeamTemporary: false,
            LabelRect: new UiRect(600f, 320f, 140f, 34f));
        harness.SimBridge.SetSimulatedWorldItemFilterResult(501u, cachedResult);

        var evaluatedOnlyResult = new ItemFilterResult(
            Evaluated: true,
            IsVisible: true,
            FontSize: 38,
            TextRgba: 0xFFD700FFu,
            BorderRgba: 0xFFD700FFu,
            BackgroundRgba: 0x000000E0u,
            AlertSoundId: 1,
            MinimapIconShape: 2,
            MinimapIconColor: 4,
            MinimapIconSize: 1,
            BeamColor: 4,
            BeamTemporary: true,
            LabelRect: null);
        harness.SimBridge.SetSimulatedEvaluatedFilterResult(777u, evaluatedOnlyResult);

        // QueryItemFilterStateAsync
        ItemFilterState queriedState = await harness.ServerBridge.QueryItemFilterStateAsync(ConnId);
        Assert.Equal(filterState, queriedState);

        // EvaluateWorldItemFilterAsync (cached ground item with LabelRect + fallback live evaluation)
        ItemFilterResult groundEval = await harness.Session.EvaluateWorldItemFilterAsync(501u);
        Assert.Equal(cachedResult, groundEval);
        Assert.Equal(new UiRect(600f, 320f, 140f, 34f), groundEval.LabelRect);

        ItemFilterResult nonCachedEval = await harness.ServerBridge.EvaluateWorldItemFilterAsync(ConnId, 777u);
        Assert.Equal(evaluatedOnlyResult, nonCachedEval);

        // Live evaluate prefers EvaluateItem when explicitly requested
        var liveOverride = cachedResult with { FontSize = 42, LabelRect = null };
        harness.SimBridge.SetSimulatedEvaluatedFilterResult(501u, liveOverride);
        ItemFilterResult liveEval = await harness.Session.EvaluateWorldItemFilterAsync(501u, liveEvaluate: true);
        Assert.Equal(42, liveEval.FontSize);
        Assert.Null(liveEval.LabelRect);

        // Unknown entity returns Unevaluated
        ItemFilterResult missingEval = await harness.Session.EvaluateWorldItemFilterAsync(9999u);
        Assert.False(missingEval.Evaluated);

        // Opt-in OnFilterReloadedAsync + ReloadItemFilterAsync
        List<ItemFilterReloadedEventArgs> reloads = [];
        IDisposable filterSub = await harness.Session.OnFilterReloadedAsync((_, evt) =>
        {
            lock (reloads) { reloads.Add(evt); }
        });
        Assert.True(harness.ClientHost.Tier1Handler.IsFilterSubscriptionActive);

        bool dispatched = await harness.ServerBridge.ReloadItemFilterAsync(ConnId);
        Assert.True(dispatched);
        Assert.Equal(1, harness.SimBridge.SimulatedFilterReloadCount);

        harness.SimBridge.SimulateItemFilterReloaded(succeeded: true, "Filter loaded cleanly.");
        await WaitUntilAsync(() => { lock (reloads) { return reloads.Count == 1; } });
        Assert.True(reloads[0].Succeeded);
        Assert.Equal("Filter loaded cleanly.", reloads[0].StatusMessage);
        Assert.Equal(filterState, reloads[0].State);

        // Dispose subscriber -> detaches cleanly
        filterSub.Dispose();
        Assert.False(harness.ClientHost.Tier1Handler.IsFilterSubscriptionActive);

        harness.SimBridge.SimulateItemFilterReloaded(succeeded: false, "Parse error.");
        Assert.Single(reloads);
    }

    // -------------------------------------------------------------------------
    // 6. Camera State, Zoom Override & Disconnect/Dispose Auto-Restore
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(NetTransportKind.HostRuleset)]
    [InlineData(NetTransportKind.ChatFallback)]
    public async Task Tier1_040_CameraStateAndZoomOverride_WithDisconnectAndModStackRestore(NetTransportKind transport)
    {
        using var harness = new Phase2Harness(transport);

        float[] vp =
        [
            0.01f, 0f,    0f,    0f,
            0f,    0.01f, 0f,    0f,
            0f,    0f,    0.01f, 0.01f,
            0f,    0f,    0f,    1f,
        ];
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
            ViewProjectionMatrix: vp);

        CameraState cam = await harness.ServerBridge.QueryCameraStateAsync(ConnId);
        Assert.True(cam.IsValid);
        Assert.Equal(1920f, cam.ViewportWidth);
        Assert.Equal(1080f, cam.ViewportHeight);
        Assert.Equal(16, cam.ViewProjectionMatrix.Count);
        Assert.True(cam.TryProjectWorldToScreen(50f, 25f, 0f, out float sx, out float sy));
        Assert.Equal(1440f, sx, precision: 2);
        Assert.Equal(405f, sy, precision: 2);

        // Set zoom override from server -> reflected on CameraService & SimulatedNativeBridge
        await harness.Session.SetCameraZoomOverrideAsync(2.25f);
        Assert.Equal(2.25f, harness.ClientHost.Tier1Handler.ActiveCameraZoomOverride);
        Assert.Equal(2.25f, harness.ClientHost.Camera.ActiveZoomOverride);
        Assert.Equal(2.25f, harness.SimBridge.SimulatedCameraState.ZoomOverride);

        // QueryCameraStateAsync reflects updated ZoomOverride
        CameraState zoomedCam = await harness.Session.QueryCameraStateAsync();
        Assert.Equal(2.25f, zoomedCam.ZoomOverride);

        // Clear via null restores stock (-1.0f)
        await harness.ServerBridge.SetCameraZoomOverrideAsync(ConnId, null);
        Assert.Null(harness.ClientHost.Tier1Handler.ActiveCameraZoomOverride);
        Assert.Null(harness.ClientHost.Camera.ActiveZoomOverride);
        Assert.Equal(CameraService.StockZoomOverride, harness.SimBridge.SimulatedCameraState.ZoomOverride);

        // Invalid floats rejected
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => harness.Session.SetCameraZoomOverrideAsync(float.NaN));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => harness.Session.SetCameraZoomOverrideAsync(float.PositiveInfinity));

        // Coexistence: client mod sets zoom override, server overrides on top, disconnect restores client mod's override!
        harness.ClientHost.Camera.SetZoomOverrideForOwner("client.mod.zoom", 1.5f);
        await harness.Session.SetCameraZoomOverrideAsync(3.0f);
        Assert.Equal(3.0f, harness.SimBridge.SimulatedCameraState.ZoomOverride);

        using IDisposable areaSub = await harness.Session.OnAreaChangedAsync((_, _) => { });
        using IDisposable filterSub = await harness.Session.OnFilterReloadedAsync((_, _) => { });
        Assert.True(harness.ClientHost.Tier1Handler.IsAreaSubscriptionActive);
        Assert.True(harness.ClientHost.Tier1Handler.IsFilterSubscriptionActive);

        // Disconnect clears server-owned camera zoom + area/filter subscriptions while preserving client mod override
        harness.ClientHost.NotifyDisconnected("logout");
        Assert.Null(harness.ClientHost.Tier1Handler.ActiveCameraZoomOverride);
        Assert.Equal(1.5f, harness.ClientHost.Camera.ActiveZoomOverride);
        Assert.Equal(1.5f, harness.SimBridge.SimulatedCameraState.ZoomOverride);
        Assert.False(harness.ClientHost.Tier1Handler.IsAreaSubscriptionActive);
        Assert.False(harness.ClientHost.Tier1Handler.IsFilterSubscriptionActive);

        // Clear client mod override, set server override again, then Dispose Tier1Handler -> restores stock (-1.0f)
        harness.ClientHost.Camera.ClearZoomOverrideForOwner("client.mod.zoom");
        await harness.Session.SetCameraZoomOverrideAsync(2.75f);
        Assert.Equal(2.75f, harness.SimBridge.SimulatedCameraState.ZoomOverride);

        harness.ClientHost.Tier1Handler.Dispose();
        Assert.Null(harness.ClientHost.Camera.ActiveZoomOverride);
        Assert.Equal(CameraService.StockZoomOverride, harness.SimBridge.SimulatedCameraState.ZoomOverride);
    }

    // -------------------------------------------------------------------------
    // 7. Multi-Mod GatewayBridgeBus Ref-Counted Subscriptions & RequestId Isolation
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Tier1_040_MultiModGatewayBus_CoexistsForRefCountedAreaAndFilterSubscriptions_AndConcurrentQueries()
    {
        string domainKey = $"Tier1Phase2Bus.{Guid.NewGuid():N}";
        using var harness = new Phase2Harness(NetTransportKind.HostRuleset, connectHandshake: false);
        var sinkB = new LoopbackGatewaySink(harness.SimBridge);
        using var bridgeB = new ClientBridgeHost(sinkB, new ClientBridgeOptions
        {
            DefaultTransport = NetTransportKind.HostRuleset,
            HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId,
        });

        using GatewayBridgeRegistration regA = harness.ServerBridge.AttachToGatewayBus("mod.alpha", domainKey: domainKey);
        using GatewayBridgeRegistration regB = bridgeB.AttachToGatewayBus("mod.beta", domainKey: domainKey);

        harness.ClientHost.SendHandshake();
        IClientSession sessionA = harness.ServerBridge.For(ConnId);
        IClientSession sessionB = bridgeB.For(ConnId);

        int alphaAreas = 0;
        int betaAreas = 0;
        int alphaFilters = 0;
        int betaFilters = 0;

        IDisposable areaSubA = await sessionA.OnAreaChangedAsync((_, _) => Interlocked.Increment(ref alphaAreas));
        IDisposable areaSubB = await sessionB.OnAreaChangedAsync((_, _) => Interlocked.Increment(ref betaAreas));
        IDisposable filterSubA = await sessionA.OnFilterReloadedAsync((_, _) => Interlocked.Increment(ref alphaFilters));
        IDisposable filterSubB = await sessionB.OnFilterReloadedAsync((_, _) => Interlocked.Increment(ref betaFilters));

        Assert.True(harness.ClientHost.Tier1Handler.IsAreaSubscriptionActive);
        Assert.True(harness.ClientHost.Tier1Handler.IsFilterSubscriptionActive);

        // Concurrent queries from both mods on the same connection never collide on RequestId
        var town = new AreaInfo("1_1_town", "Lioneye's Watch", 1, 1, 1, 0x1001, IsTown: true, IsHideout: false, IsGuildHideout: false, ActiveRulesetIds: []);
        harness.SimBridge.SimulateAreaEntered(town);
        harness.SimBridge.SimulateItemFilterReloaded(true, "Reload 1");

        await WaitUntilAsync(() => alphaAreas == 1 && betaAreas == 1 && alphaFilters == 1 && betaFilters == 1);

        Task<AreaInfo?> qA = sessionA.QueryAreaInfoAsync();
        Task<AreaInfo?> qB = sessionB.QueryAreaInfoAsync();
        AreaInfo?[] results = await Task.WhenAll(qA, qB);
        Assert.Equal("1_1_town", results[0]!.AreaId);
        Assert.Equal("1_1_town", results[1]!.AreaId);

        // Unsubscribing Mod B leaves Mod A's ref-counted subscriptions active
        areaSubB.Dispose();
        filterSubB.Dispose();
        Assert.True(harness.ClientHost.Tier1Handler.IsAreaSubscriptionActive);
        Assert.True(harness.ClientHost.Tier1Handler.IsFilterSubscriptionActive);

        harness.SimBridge.SimulateAreaLeft("1_1_town");
        harness.SimBridge.SimulateItemFilterReloaded(true, "Reload 2");
        await WaitUntilAsync(() => alphaAreas == 2 && alphaFilters == 2);
        Assert.Equal(2, alphaAreas);
        Assert.Equal(1, betaAreas);
        Assert.Equal(2, alphaFilters);
        Assert.Equal(1, betaFilters);

        // Unsubscribing Mod A brings ref-counts to 0 -> detaches cleanly
        areaSubA.Dispose();
        filterSubA.Dispose();
        Assert.False(harness.ClientHost.Tier1Handler.IsAreaSubscriptionActive);
        Assert.False(harness.ClientHost.Tier1Handler.IsFilterSubscriptionActive);

        GatewayBridgeBus.Clear(domainKey);
    }

    // -------------------------------------------------------------------------
    // 8. Timeouts, Cancellation, Closed Session & Legacy 7-Arg Handler Compat
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Tier1_040_TimeoutsCancellationAndSessionClose_AreHandledCleanly()
    {
        var sink = new BlackHoleGatewaySink();
        using var bridge = new ClientBridgeHost(sink);
        IClientSession session = bridge.For("no-reply-040");

        TimeSpan shortTimeout = TimeSpan.FromMilliseconds(35);
        await Assert.ThrowsAsync<TimeoutException>(() => session.QueryVisibleGroundItemsAsync(timeout: shortTimeout));
        await Assert.ThrowsAsync<TimeoutException>(() => session.QueryWorldEntitiesAsync(timeout: shortTimeout));
        await Assert.ThrowsAsync<TimeoutException>(() => session.QueryLocalPlayerAsync(timeout: shortTimeout));
        await Assert.ThrowsAsync<TimeoutException>(() => session.QueryEntityStatsAsync(timeout: shortTimeout));
        await Assert.ThrowsAsync<TimeoutException>(() => session.QueryStatAsync(1, timeout: shortTimeout));
        await Assert.ThrowsAsync<TimeoutException>(() => session.QueryAreaInfoAsync(timeout: shortTimeout));
        await Assert.ThrowsAsync<TimeoutException>(() => session.QueryItemFilterStateAsync(timeout: shortTimeout));
        await Assert.ThrowsAsync<TimeoutException>(() => session.ReloadItemFilterAsync(timeout: shortTimeout));
        await Assert.ThrowsAsync<TimeoutException>(() => session.EvaluateWorldItemFilterAsync(1u, timeout: shortTimeout));
        await Assert.ThrowsAsync<TimeoutException>(() => session.QueryCameraStateAsync(timeout: shortTimeout));

        // Non-positive timeout rejected
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.QueryCameraStateAsync(timeout: TimeSpan.Zero));

        // Explicit cancellation cancels in-flight query
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.QueryAreaInfoAsync(timeout: TimeSpan.FromSeconds(10), cancellationToken: cts.Token));

        // Removing session cancels all in-flight Phase 2 queries and rejects subsequent calls
        Task<IReadOnlyList<ClientWorldEntitySnapshot>> inFlightWorld = session.QueryVisibleGroundItemsAsync(timeout: TimeSpan.FromSeconds(10));
        Task<ClientEntityStatsSnapshot> inFlightStats = session.QueryEntityStatsAsync(timeout: TimeSpan.FromSeconds(10));
        Task<CameraState> inFlightCamera = session.QueryCameraStateAsync(timeout: TimeSpan.FromSeconds(10));

        Assert.True(await bridge.RemoveSessionAsync("no-reply-040"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inFlightWorld);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inFlightStats);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inFlightCamera);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.QueryVisibleGroundItemsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.QueryAreaInfoAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.QueryCameraStateAsync());
    }

    [Fact]
    public async Task Tier1_040_Legacy7ArgHandlerCtor_GracefullyReturnsDefaultPhase2Responses()
    {
        using var sim = new SimulatedNativeBridge(ClientBuildId.Build3293d);
        using var host = new ClientHostRuntime(sim);
        host.Start();

        // Replace default handler with legacy 7-arg constructor (null Phase 2 services) to verify backward compatibility
        host.Tier1Handler.Dispose();
        INetChannel sysChannel = host.Router.CreateChannel(MessageCodec.SystemChannel);
        using var legacyHandler = new ServerBridgeTier1Handler(
            sysChannel,
            host.Scheduler,
            host.Screens,
            host.Input,
            host.Hover,
            host.Toasts);

        var sink = new LoopbackGatewaySink(sim);
        using var serverBridge = new ClientBridgeHost(sink);
        sim.OnOutgoingRulesetPacket = (rulesetId, innerPayload) =>
        {
            byte[] body = MessageCodec.EncodeRulesetBody(rulesetId, innerPayload.Span);
            serverBridge.HandleIncomingPacketAsync(ConnId, IOpcodeRegistry.ClientRulesetOpcode, body).GetAwaiter().GetResult();
        };

        IClientSession session = serverBridge.For(ConnId);
        Assert.Empty(await session.QueryVisibleGroundItemsAsync());
        Assert.Null(await session.QueryLocalPlayerAsync());
        Assert.Empty((await session.QueryEntityStatsAsync()).Stats);
        Assert.False((await session.QueryStatAsync(1)).Found);
        Assert.Null(await session.QueryAreaInfoAsync());
        Assert.False((await session.QueryItemFilterStateAsync()).IsLoaded);
        Assert.False(await session.ReloadItemFilterAsync());
        Assert.False((await session.EvaluateWorldItemFilterAsync(1u)).Evaluated);
        Assert.False((await session.QueryCameraStateAsync()).IsValid);
        await session.SetCameraZoomOverrideAsync(2.0f);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                return;
            }

            await Task.Delay(5);
        }
    }
}
