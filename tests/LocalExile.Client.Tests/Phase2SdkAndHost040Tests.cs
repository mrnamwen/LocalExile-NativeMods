using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using LocalExile.Client.Host;
using LocalExile.Client.Host.Loading;
using LocalExile.Client.Host.Native;
using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;
using Xunit;

namespace LocalExile.Client.Tests;

[Collection("NativeEntryPointSerial")]
public sealed class Phase2SdkAndHost040Tests
{
    [Fact]
    public void SdkVersion_040_MaintainsBackwardCompatibilityWith020And030Ranges()
    {
        Assert.Equal("1.1.0", SdkVersion.Current);
        Assert.Equal(new Version(1, 1, 0), SdkVersion.Parsed);
        Assert.Equal(1, SdkVersion.ProtocolVersion);

        Assert.True(SdkVersion.IsCompatible(">=0.2.0 <2.0.0"));
        Assert.True(SdkVersion.IsCompatible(">=0.3.0 <2.0.0"));
        Assert.True(SdkVersion.IsCompatible(">=0.4.0 <2.0.0"));
        Assert.True(SdkVersion.IsCompatible(">=1.0.0 <2.0.0"));
        Assert.True(SdkVersion.IsCompatible(">=1.1.0 <2.0.0"));
        Assert.True(SdkVersion.IsCompatible("^1.0.0"));
        Assert.True(SdkVersion.IsCompatible("~1.1.0"));
        Assert.False(SdkVersion.IsCompatible(">=0.3.0 <0.4.0"));
        Assert.False(SdkVersion.IsCompatible(">=2.0.0"));
    }

    [Fact]
    public void WorldService_LiveQueriesWorldItemsDistanceAndPreservedClientObjects()
    {
        using var bridge = new SimulatedNativeBridge();
        using var host = new ClientHostRuntime(bridge);

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

        var nearbyMonster = new WorldEntitySnapshot(
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

        var divineItemInfo = new HoveredItemInfo(
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
            StackSize: 1u,
            MaxStackSize: 20u,
            Rarity: ItemRarity.Normal,
            IsIdentified: true,
            IsCorrupted: false,
            ItemLevel: 84);

        var visibleNearDrop = new WorldEntitySnapshot(
            EntityId: 301u,
            EntityHandle: 0x3010,
            Kind: WorldEntityKind.WorldItem | WorldEntityKind.Targetable,
            MetadataPath: "Metadata/MiscellaneousObjects/WorldItem",
            GridX: 11,
            GridY: 10,
            Position: new WorldPosition3D(115f, 100f, 0f),
            IsFilterVisible: true,
            WorldItem: divineItemInfo);

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

        bridge.SetSimulatedLocalPlayer(player);
        bridge.AddSimulatedEntity(nearbyMonster);
        bridge.AddSimulatedEntity(deadMonster);
        bridge.AddSimulatedEntity(visibleNearDrop);
        bridge.AddSimulatedEntity(hiddenNearDrop);
        bridge.AddSimulatedEntity(visibleFarDrop);

        Assert.Equal(100u, host.World.GetLocalPlayer()!.EntityId);
        Assert.Equal(6, host.World.GetEntities().Count);
        Assert.Equal(201u, host.World.GetEntity(201u)!.EntityId);
        Assert.Equal(new WorldPosition3D(120f, 100f, 0f), host.World.GetEntityPosition(201u));

        // RequiredKind + ExcludedKind + MetadataPrefix
        IReadOnlyList<WorldEntitySnapshot> aliveZombies = host.World.GetEntities(new WorldEntityQuery(
            RequiredKind: WorldEntityKind.Monster | WorldEntityKind.Alive,
            MetadataPrefix: "Metadata/Monsters/Zombies"));
        Assert.Single(aliveZombies);
        Assert.Equal(201u, aliveZombies[0].EntityId);

        IReadOnlyList<WorldEntitySnapshot> deadMonsters = host.World.GetEntities(new WorldEntityQuery(
            RequiredKind: WorldEntityKind.Monster,
            ExcludedKind: WorldEntityKind.Alive));
        Assert.Single(deadMonsters);
        Assert.Equal(202u, deadMonsters[0].EntityId);

        // GetWorldItems filterVisibleOnly + maxDistance (measured from local player automatically)
        Assert.Equal(3, host.World.GetWorldItems().Count);
        IReadOnlyList<WorldEntitySnapshot> allVisible = host.World.GetWorldItems(filterVisibleOnly: true);
        Assert.Equal(2, allVisible.Count);
        IReadOnlyList<WorldEntitySnapshot> nearVisible = host.World.GetWorldItems(filterVisibleOnly: true, maxDistance: 50f);
        Assert.Single(nearVisible);
        Assert.Equal(301u, nearVisible[0].EntityId);
        Assert.Equal("Divine Orb", nearVisible[0].WorldItem!.BaseName);

        // MaxResults cap
        Assert.Equal(2, host.World.GetEntities(new WorldEntityQuery(MaxResults: 2)).Count);

        // Preserved 0.1.0-0.3.0 client-object APIs
        uint clientId = host.World.SpawnClientObject("Metadata/Effects/Mods/Beacon", 150f, 200f, 12f);
        Assert.NotNull(host.World.FindObject(clientId));
        Assert.Equal(new WorldPosition3D(150f, 200f, 12f), host.World.GetEntityPosition(clientId));
        host.World.PlayEffect(clientId, "Metadata/Effects/Pulse");
        Assert.Single(bridge.PlayedEffects);
        Assert.True(host.World.RemoveClientObject(clientId));
        Assert.Null(host.World.FindObject(clientId));
    }

    [Fact]
    public void StatsService_ReadsWritesHighIdStatsAndLocalPlayerConvenienceOverloads()
    {
        using var bridge = new SimulatedNativeBridge();
        using var host = new ClientHostRuntime(bridge);

        bridge.SetSimulatedLocalPlayer(new WorldEntitySnapshot(
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

        host.Stats.SetLocalStat(42u, StockLifeStat, 3450);
        host.Stats.SetLocalStat(0u, HighCustomStatA, 999);
        host.Stats.SetLocalStat(42u, HighCustomStatB, -12345);
        host.Stats.SetLocalStat(900u, HighCustomStatA, 77);

        // Local player alias (0u) and explicit id (42u) unify
        Assert.Equal(3450, host.Stats.ReadLocalPlayerStat(StockLifeStat));
        Assert.Equal(999, host.Stats.ReadStat(42u, HighCustomStatA));
        Assert.Equal(-12345, host.Stats.ReadLocalPlayerStat(HighCustomStatB));
        Assert.True(host.Stats.TryReadLocalPlayerStat(HighCustomStatA, out int customVal));
        Assert.Equal(999, customVal);
        Assert.False(host.Stats.TryReadStat(42u, 0x20000, out _));
        Assert.Equal(0, host.Stats.ReadStat(42u, 0x20000));

        // High-ID enumeration
        IReadOnlyDictionary<int, int> playerStats = host.Stats.GetLocalPlayerStats();
        Assert.Equal(3, playerStats.Count);
        Assert.Equal(3450, playerStats[StockLifeStat]);
        Assert.Equal(999, playerStats[HighCustomStatA]);
        Assert.Equal(-12345, playerStats[HighCustomStatB]);

        IReadOnlyDictionary<int, int> otherStats = host.Stats.GetAllStats(900u);
        Assert.Single(otherStats);
        Assert.Equal(77, otherStats[HighCustomStatA]);

        // Preserved 0.1.0-0.3.0 string stat API
        int notified = 0;
        using IDisposable sub = host.Stats.OnStatChanged("legacy_key", v => notified = v);
        host.Stats.SetLocalStat("legacy_key", 55);
        Assert.Equal(55, host.Stats.GetStat("legacy_key"));
        Assert.Equal(55, notified);
    }

    [Fact]
    public void LifecycleService_DrivesAreaInfoAreaEnteredAreaLeftAndRulesetsFromNativeHooks()
    {
        using var bridge = new SimulatedNativeBridge();
        using var host = new ClientHostRuntime(bridge);

        List<AreaEventArgs> entered = [];
        List<string> left = [];
        host.Lifecycle.AreaEntered += entered.Add;
        host.Lifecycle.AreaLeft += left.Add;

        int rulesetEntered = 0;
        int rulesetLeft = 0;
        using IDisposable rsSub = host.Rulesets.Register(0x42, () => new TrackingRuleset(0x42, () => rulesetEntered++, () => rulesetLeft++));

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

        bridge.SimulateAreaEntered(hideout);

        Assert.Single(entered);
        Assert.Equal("HideoutBattleScarred", host.Lifecycle.CurrentAreaId);
        Assert.Equal("HideoutBattleScarred", host.World.CurrentAreaId);
        Assert.Equal(hideout, host.Lifecycle.CurrentArea);
        Assert.Equal(hideout, entered[0].Area);
        Assert.Equal(0x11223344u, entered[0].Seed);
        Assert.Equal([0x42, 0xA0], entered[0].ActiveRulesetIds);
        Assert.Equal(1, rulesetEntered);

        bridge.SimulateAreaLeft("HideoutBattleScarred");

        Assert.Single(left);
        Assert.Equal("HideoutBattleScarred", left[0]);
        Assert.Null(host.Lifecycle.CurrentArea);
        Assert.Null(host.Lifecycle.CurrentAreaId);
        Assert.Equal(1, rulesetLeft);

        // Re-enter town, then disconnect clears CurrentArea cleanly.
        var town = new AreaInfo("1_1_town", "Lioneye's Watch", 1, 1, 1, 0x1001, IsTown: true, IsHideout: false, IsGuildHideout: false, ActiveRulesetIds: []);
        bridge.SimulateAreaEntered(town);
        Assert.True(host.Lifecycle.CurrentArea!.IsTown);
        host.NotifyDisconnected("logout");
        Assert.Null(host.Lifecycle.CurrentArea);
        Assert.Null(host.Lifecycle.CurrentAreaId);
    }

    [Fact]
    public void ItemFilterService_ReadsStateStylesLabelRectReloadEvaluateAndEvents()
    {
        using var bridge = new SimulatedNativeBridge();
        using var host = new ClientHostRuntime(bridge);

        var filterState = new ItemFilterState(
            FilterName: "NeverSinkStrict.filter",
            FilterFolder: @"C:\Users\Exile\Documents\My Games\Path of Exile",
            FilterPath: @"C:\Users\Exile\Documents\My Games\Path of Exile\NeverSinkStrict.filter",
            IsLoaded: true,
            HideFilteredGroundItems: true,
            AltHighlightActive: false);
        bridge.SimulatedItemFilterState = filterState;

        bridge.SetSimulatedLocalPlayer(new WorldEntitySnapshot(
            EntityId: 1u,
            EntityHandle: 0x100,
            Kind: WorldEntityKind.Player | WorldEntityKind.LocalPlayer,
            MetadataPath: "Metadata/Characters/Str/Str",
            GridX: 0,
            GridY: 0,
            Position: new WorldPosition3D(0f, 0f, 0f)));

        bridge.AddSimulatedEntity(new WorldEntitySnapshot(
            EntityId: 501u,
            EntityHandle: 0x501,
            Kind: WorldEntityKind.WorldItem,
            MetadataPath: "Metadata/MiscellaneousObjects/WorldItem",
            GridX: 1,
            GridY: 0,
            Position: new WorldPosition3D(10f, 0f, 0f),
            IsFilterVisible: true));
        bridge.AddSimulatedEntity(new WorldEntitySnapshot(
            EntityId: 502u,
            EntityHandle: 0x502,
            Kind: WorldEntityKind.WorldItem,
            MetadataPath: "Metadata/MiscellaneousObjects/WorldItem",
            GridX: 2,
            GridY: 0,
            Position: new WorldPosition3D(20f, 0f, 0f),
            IsFilterVisible: false));

        var divineResult = new ItemFilterResult(
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
        bridge.SetSimulatedWorldItemFilterResult(501u, divineResult);

        var evaluatedInventoryItem = new ItemFilterResult(
            Evaluated: true,
            IsVisible: true,
            FontSize: 38,
            TextRgba: 0xFFFFD700u,
            BorderRgba: 0xFFFFD700u,
            BackgroundRgba: 0xE0000000u,
            AlertSoundId: 1,
            MinimapIconShape: 2,
            MinimapIconColor: 4,
            MinimapIconSize: 1,
            BeamColor: 4,
            BeamTemporary: true,
            LabelRect: null);
        bridge.SetSimulatedEvaluatedFilterResult(777u, evaluatedInventoryItem);

        Assert.Equal(filterState, host.ItemFilter.GetState());
        Assert.True(host.ItemFilter.IsWorldItemVisible(501u));
        Assert.False(host.ItemFilter.IsWorldItemVisible(502u));
        Assert.Equal(divineResult, host.ItemFilter.GetWorldItemResult(501u));
        Assert.Equal(new UiRect(600f, 320f, 140f, 34f), host.ItemFilter.GetWorldItemResult(501u).LabelRect);
        Assert.Equal(evaluatedInventoryItem, host.ItemFilter.EvaluateItem(777u));

        IReadOnlyList<WorldEntitySnapshot> groundVisible = host.ItemFilter.GetVisibleGroundItems(maxDistance: 50f);
        Assert.Single(groundVisible);
        Assert.Equal(501u, groundVisible[0].EntityId);

        List<ItemFilterReloadedEventArgs> reloads = [];
        host.ItemFilter.FilterReloaded += reloads.Add;
        Assert.True(host.ItemFilter.Reload());
        Assert.Equal(1, bridge.SimulatedFilterReloadCount);

        bridge.SimulateItemFilterReloaded(succeeded: true, "Item Filter loaded successfully.");
        Assert.Single(reloads);
        Assert.True(reloads[0].Succeeded);
        Assert.Equal("NeverSinkStrict.filter", reloads[0].State.FilterName);
    }

    [Fact]
    public async Task CameraService_ProjectsWorldToScreenTracksZoomStackAndRestoresStockOnCollectibleAlcUnload()
    {
        using var bridge = new SimulatedNativeBridge();
        using var host = new ClientHostRuntime(bridge);

        // Simple row-major DirectX view-projection matrix (v_row * M):
        // cx = x * 0.01, cy = y * 0.01, cz = z * 0.01, cw = z * 0.01 + 1.0
        // For (x=50, y=25, z=0): cx=0.5, cy=0.25, cw=1.0 -> sx = 960 * 1.5 = 1440, sy = 540 * 0.75 = 405
        float[] vp =
        [
            0.01f, 0f,    0f,    0f,
            0f,    0.01f, 0f,    0f,
            0f,    0f,    0.01f, 0.01f,
            0f,    0f,    0f,    1f,
        ];
        bridge.SimulatedCameraState = new CameraState(
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

        CameraState state = host.Camera.GetState();
        Assert.True(state.IsValid);
        Assert.Equal(1920f, state.ViewportWidth);
        Assert.Equal(1080f, state.ViewportHeight);
        Assert.Equal(16, state.ViewProjectionMatrix.Count);

        Assert.True(host.Camera.TryWorldToScreen(50f, 25f, 0f, out float sx, out float sy));
        Assert.Equal(1440f, sx, precision: 2);
        Assert.Equal(405f, sy, precision: 2);

        // Point behind camera (cw = -200 * 0.01 + 1 = -1 <= 0) returns false.
        Assert.False(host.Camera.TryWorldToScreen(0f, 0f, -200f, out _, out _));

        // Multi-mod zoom override stack + real collectible ALC unload restoration to -1.0f!
        string tempDir = Path.Combine(Path.GetTempPath(), $"nm-p2-alc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string dllPath = Path.Combine(tempDir, "zoommod.Client.dll");
        try
        {
            EmitPhase2CollectibleModAssembly(dllPath, "Phase2ZoomModAsm", zoomOverride: 1.75f);

            var modA = new ZoomInProcessMod(1.25f);
            host.Loader.LoadInProcessMod(new ClientModManifest("mod-a", "Mod A", "1.0.0"), modA);
            Assert.Equal(1.25f, bridge.SimulatedZoomOverride);

            var manifestB = new ClientModManifest("zoommod", "Zoom Mod", "1.0.0", SdkVersionRange: ">=0.4.0 <2.0.0", Assembly: "zoommod.Client.dll");
            LoadedClientMod loadedB = host.Loader.LoadMod(manifestB, tempDir, dllPath);
            Assert.NotNull(loadedB.LoadContext);
            Assert.Equal(1.75f, bridge.SimulatedZoomOverride);
            Assert.Single(host.World.Objects);

            // Unload collectible mod B -> restores mod A's 1.25f override, cleans spawned client object, collects ALC!
            ModUnloadResult unloadB = await host.Loader.UnloadModAsync("zoommod");
            Assert.True(unloadB.WasLoaded);
            Assert.True(unloadB.UnloadedCleanly);
            Assert.Equal(1.25f, bridge.SimulatedZoomOverride);
            Assert.Empty(host.World.Objects);

            // Unload mod A -> restores stock -1.0f zoom!
            ModUnloadResult unloadA = await host.Loader.UnloadModAsync("mod-a");
            Assert.True(unloadA.WasLoaded);
            Assert.Equal(-1.0f, bridge.SimulatedZoomOverride);
            Assert.Equal(-1.0f, host.Camera.GetState().ZoomOverride);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ScopedGameServices_Phase2FaultIsolationProtectsHealthyModsAndAutoUnsubscribes()
    {
        using var bridge = new SimulatedNativeBridge();
        using var host = new ClientHostRuntime(bridge, new ClientHostOptions { MaxModFaults = 2 });

        var goodMod = new Phase2EventMod(throwOnEvents: false);
        var badMod = new Phase2EventMod(throwOnEvents: true);
        host.Loader.LoadInProcessMod(new ClientModManifest("p2-good", "Good P2 Mod", "1.0.0"), goodMod);
        host.Loader.LoadInProcessMod(new ClientModManifest("p2-bad", "Bad P2 Mod", "1.0.0"), badMod);

        bridge.SimulateItemFilterReloaded(true, "ok");
        Assert.Equal(1, goodMod.FilterReloadCount);
        Assert.False(host.Loader.GetMod("p2-bad")!.IsDisabled);

        // Second fault disables badMod without breaking goodMod!
        bridge.SimulateAreaEntered(new AreaInfo("1_1_1", "The Twilight Strand", 1, 1, 1, 0x0011, false, false, false, []));
        Assert.Equal(1, goodMod.AreaEnteredCount);
        Assert.True(host.Loader.GetMod("p2-bad")!.IsDisabled);

        // Unloading goodMod detaches FilterReloaded & AreaEntered subscriptions.
        await host.Loader.UnloadModAsync("p2-good");
        bridge.SimulateItemFilterReloaded(true, "after-unload");
        bridge.SimulateAreaEntered(new AreaInfo("1_1_town", "Lioneye's Watch", 1, 1, 1, 0x0012, true, false, false, []));
        Assert.Equal(1, goodMod.FilterReloadCount);
        Assert.Equal(1, goodMod.AreaEnteredCount);
    }

    // --- Live NativeShimBridge C ABI 0.4.0 Parity Test (Slots 89..106) ---

    private static readonly byte[] s_p2SymName = AllocatePinnedUtf8("world.client_world_init_area");
    private static readonly byte[] s_p2SymCat = AllocatePinnedUtf8("world");
    private static readonly byte[] s_p2SymBytes = AllocatePinnedUtf8("488bc44c894820535657415441554156");

    private static unsafe delegate* unmanaged[Cdecl]<int, NmAreaInfo*, void*, void> s_areaCb;
    private static unsafe void* s_areaUd;
    private static unsafe delegate* unmanaged[Cdecl]<int, NmItemFilterState*, void*, void> s_filterReloadCb;
    private static unsafe void* s_filterReloadUd;
    private static ulong s_lastRemovedClientHandle;
    private static int s_lastWrittenStatEntityId;
    private static int s_lastWrittenStatId;
    private static int s_lastWrittenStatVal;
    private static int s_filterReloadTriggered;
    private static float s_lastNativeZoomOverride;

    private static byte[] AllocatePinnedUtf8(string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        byte[] pinned = GC.AllocateArray<byte>(utf8.Length + 1, pinned: true);
        utf8.CopyTo(pinned, 0);
        pinned[utf8.Length] = 0;
        return pinned;
    }

    [Fact]
    public unsafe void NativeShimBridge_Abi040StructSizesOffsetsAndAllSlots89To106RoundTrip()
    {
        // Exact C ABI 0.4.0/0.5.0 struct sizes and key offsets matching nativemods_abi.h (x64).
        Assert.Equal(0x00000500u, NativeShimConstants.AbiVersion);
        Assert.Equal(160, sizeof(NmWorldEntityQuery));
        Assert.Equal(760, sizeof(NmWorldEntitySnapshot));
        Assert.Equal(8, sizeof(NmStatEntry));
        Assert.Equal(352, sizeof(NmAreaInfo));
        Assert.Equal(784, sizeof(NmItemFilterState));
        Assert.Equal(72, sizeof(NmItemFilterResult));
        Assert.Equal(120, sizeof(NmCameraState));
        Assert.Equal(1120, sizeof(NmAbiTable));

        Assert.Equal(32, (int)Marshal.OffsetOf<NmWorldEntityQuery>(nameof(NmWorldEntityQuery.MetadataPrefix)));
        Assert.Equal(56, (int)Marshal.OffsetOf<NmWorldEntitySnapshot>(nameof(NmWorldEntitySnapshot.MetadataPath)));
        Assert.Equal(312, (int)Marshal.OffsetOf<NmWorldEntitySnapshot>(nameof(NmWorldEntitySnapshot.WorldItem)));
        Assert.Equal(32, (int)Marshal.OffsetOf<NmAreaInfo>(nameof(NmAreaInfo.RulesetIds)));
        Assert.Equal(96, (int)Marshal.OffsetOf<NmAreaInfo>(nameof(NmAreaInfo.AreaId)));
        Assert.Equal(224, (int)Marshal.OffsetOf<NmAreaInfo>(nameof(NmAreaInfo.AreaName)));
        Assert.Equal(16, (int)Marshal.OffsetOf<NmItemFilterState>(nameof(NmItemFilterState.FilterName)));
        Assert.Equal(144, (int)Marshal.OffsetOf<NmItemFilterState>(nameof(NmItemFilterState.FilterFolder)));
        Assert.Equal(400, (int)Marshal.OffsetOf<NmItemFilterState>(nameof(NmItemFilterState.FilterPath)));
        Assert.Equal(52, (int)Marshal.OffsetOf<NmItemFilterResult>(nameof(NmItemFilterResult.LabelX)));
        Assert.Equal(56, (int)Marshal.OffsetOf<NmCameraState>(nameof(NmCameraState.ViewProj)));

        s_areaCb = null;
        s_areaUd = null;
        s_filterReloadCb = null;
        s_filterReloadUd = null;
        s_lastRemovedClientHandle = 0;
        s_lastWrittenStatEntityId = -1;
        s_lastWrittenStatId = -1;
        s_lastWrittenStatVal = -1;
        s_filterReloadTriggered = 0;
        s_lastNativeZoomOverride = -99f;

        NmAbiTable table = default;
        table.StructSize = (uint)sizeof(NmAbiTable);
        table.AbiVersion = NativeShimConstants.AbiVersion;
        table.GetHostInfo = &Mock040GetHostInfo;
        table.LookupSymbol = &Mock040LookupSymbol;
        table.GetSymbolAt = &Mock040GetSymbolAt;
        table.WorldGetLocalPlayer = &Mock040WorldGetLocalPlayer;
        table.WorldListEntities = &Mock040WorldListEntities;
        table.WorldGetEntity = &Mock040WorldGetEntity;
        table.WorldCreateClientObject = &Mock040WorldCreateClientObject;
        table.WorldRemoveClientObject = &Mock040WorldRemoveClientObject;
        table.StatsReadEntity = &Mock040StatsReadEntity;
        table.StatsSetLocalEntity = &Mock040StatsSetLocalEntity;
        table.StatsListEntity = &Mock040StatsListEntity;
        table.LifecycleGetAreaInfo = &Mock040LifecycleGetAreaInfo;
        table.LifecycleSetAreaCallback = &Mock040LifecycleSetAreaCallback;
        table.FilterGetState = &Mock040FilterGetState;
        table.FilterReload = &Mock040FilterReload;
        table.FilterGetWorldItemResult = &Mock040FilterGetWorldItemResult;
        table.FilterEvaluateEntity = &Mock040FilterEvaluateEntity;
        table.FilterSetReloadCallback = &Mock040FilterSetReloadCallback;
        table.CameraGetState = &Mock040CameraGetState;
        table.CameraWorldToScreen = &Mock040CameraWorldToScreen;
        table.CameraSetZoomOverride = &Mock040CameraSetZoomOverride;

        int bootStatus = NativeEntryPoint.InitializeManaged((nint)(&table));
        try
        {
            Assert.Equal((int)NmStatus.Ok, bootStatus);
            ClientHostRuntime rt = NativeEntryPoint.ActiveRuntime!;

            // Slots 89..93: World
            WorldEntitySnapshot? lp = rt.World.GetLocalPlayer();
            Assert.NotNull(lp);
            Assert.Equal(10u, lp!.EntityId);
            Assert.True(lp.IsPlayer);

            IReadOnlyList<WorldEntitySnapshot> entities = rt.World.GetEntities(new WorldEntityQuery(RequiredKind: WorldEntityKind.WorldItem));
            Assert.Single(entities);
            Assert.Equal(500u, entities[0].EntityId);
            Assert.True(entities[0].IsFilterVisible);
            Assert.Equal("Mirror of Kalandra", entities[0].WorldItem!.BaseName);

            WorldEntitySnapshot? byId = rt.World.GetEntity(500u);
            Assert.NotNull(byId);
            Assert.Equal(500u, byId!.EntityId);

            uint clientObjId = rt.World.SpawnClientObject("Metadata/Effects/Test", 10f, 20f, 30f);
            Assert.Equal(0xC000_0042u, clientObjId);
            Assert.True(rt.World.RemoveClientObject(clientObjId));
            Assert.Equal(0x0000_0001_C000_0042ul, s_lastRemovedClientHandle);

            // Slots 94..96: Stats
            Assert.True(rt.Stats.TryReadLocalPlayerStat(0x10042, out int highStat));
            Assert.Equal(7777, highStat);
            rt.Stats.SetLocalStat(10u, 0x10042, 8888);
            Assert.Equal(10, s_lastWrittenStatEntityId);
            Assert.Equal(0x10042, s_lastWrittenStatId);
            Assert.Equal(8888, s_lastWrittenStatVal);
            IReadOnlyDictionary<int, int> allStats = rt.Stats.GetLocalPlayerStats();
            Assert.Equal(2, allStats.Count);
            Assert.Equal(7777, allStats[0x10042]);

            // Slots 97..98: Area Lifecycle
            AreaInfo? currentArea = rt.Lifecycle.CurrentArea;
            Assert.NotNull(currentArea);
            Assert.Equal("1_1_town", currentArea!.AreaId);
            Assert.Equal("Lioneye's Watch", currentArea.AreaName);
            Assert.True(currentArea.IsTown);
            Assert.Equal([0x10u, 0xA0u], currentArea.ActiveRulesetIds);

            Assert.True(s_areaCb is not null);
            List<AreaEventArgs> areaEnteredEvents = [];
            List<string> areaLeftEvents = [];
            rt.Lifecycle.AreaEntered += areaEnteredEvents.Add;
            rt.Lifecycle.AreaLeft += areaLeftEvents.Add;

            NmAreaInfo nativeArea = default;
            table.LifecycleGetAreaInfo(&nativeArea);
            s_areaCb((int)NmAreaEventKind.Entered, &nativeArea, s_areaUd);
            Assert.Single(areaEnteredEvents);
            Assert.Equal("1_1_town", areaEnteredEvents[0].AreaId);
            Assert.Equal(0xDEADBEEFu, areaEnteredEvents[0].Seed);

            s_areaCb((int)NmAreaEventKind.Left, &nativeArea, s_areaUd);
            Assert.Single(areaLeftEvents);
            Assert.Equal("1_1_town", areaLeftEvents[0]);

            // Slots 99..103: Item Filter
            ItemFilterState fs = rt.ItemFilter.GetState();
            Assert.True(fs.IsLoaded);
            Assert.Equal("Strict.filter", fs.FilterName);
            Assert.True(rt.ItemFilter.Reload());
            Assert.Equal(1, s_filterReloadTriggered);

            Assert.True(rt.ItemFilter.IsWorldItemVisible(500u));
            ItemFilterResult wiRes = rt.ItemFilter.GetWorldItemResult(500u);
            Assert.Equal(45, wiRes.FontSize);
            Assert.Equal(new UiRect(100f, 200f, 120f, 28f), wiRes.LabelRect);

            ItemFilterResult evalRes = rt.ItemFilter.EvaluateItem(500u);
            Assert.True(evalRes.Evaluated);
            Assert.Equal(9, evalRes.AlertSoundId);

            Assert.True(s_filterReloadCb is not null);
            List<ItemFilterReloadedEventArgs> filterReloadEvents = [];
            rt.ItemFilter.FilterReloaded += filterReloadEvents.Add;
            NmItemFilterState nativeFilter = default;
            table.FilterGetState(&nativeFilter);
            s_filterReloadCb(1, &nativeFilter, s_filterReloadUd);
            Assert.Single(filterReloadEvents);
            Assert.True(filterReloadEvents[0].Succeeded);
            Assert.Equal("Strict.filter", filterReloadEvents[0].State.FilterName);

            // Slots 104..106: Camera
            CameraState cam = rt.Camera.GetState();
            Assert.True(cam.IsValid);
            Assert.Equal(2560f, cam.ViewportWidth);
            Assert.Equal(1440f, cam.ViewportHeight);
            Assert.True(rt.Camera.TryWorldToScreen(10f, 20f, 30f, out float sx, out float sy));
            Assert.Equal(1280f, sx);
            Assert.Equal(720f, sy);
            Assert.False(rt.Camera.TryWorldToScreen(0f, 0f, -500f, out _, out _));

            rt.Camera.SetZoomOverride(1.5f);
            Assert.Equal(1.5f, s_lastNativeZoomOverride);
            rt.Camera.SetZoomOverride(null);
            Assert.Equal(-1.0f, s_lastNativeZoomOverride);
        }
        finally
        {
            NativeEntryPoint.Shutdown();
        }

        // Verify clean native callback unhooking on shutdown!
        Assert.True(s_areaCb is null);
        Assert.True(s_filterReloadCb is null);
    }

    // --- Unmanaged Mock Callbacks for C ABI 0.4.0 ---

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock040GetHostInfo(NmHostInfo* outInfo)
    {
        outInfo->AbiVersion = NativeShimConstants.AbiVersion;
        outInfo->BuildId = NmBuildId.Build3293d328228;
        outInfo->Flags = NmHostFlags.Initialized | NmHostFlags.Simulated;
        outInfo->ImageBase = NativeShimConstants.NominalImageBase;
        outInfo->SymbolCount = 1;
        "3.29.3d\0"u8.CopyTo(new Span<byte>(outInfo->BuildVersion, 16));
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock040LookupSymbol(byte* name, NmSymbolInfo* outSym) => Fill040Sym0(outSym);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock040GetSymbolAt(uint index, NmSymbolInfo* outSym) =>
        index == 0 ? Fill040Sym0(outSym) : NmStatus.ErrSymbolNotFound;

    private static unsafe NmStatus Fill040Sym0(NmSymbolInfo* outSym)
    {
        outSym->Name = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(s_p2SymName));
        outSym->Category = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(s_p2SymCat));
        outSym->Kind = NmSymbolKind.Code;
        outSym->Status = NmSymbolStatus.StaticVerified;
        outSym->Rva = 0x1653EE0ul;
        outSym->RuntimeAddress = NativeShimConstants.NominalImageBase + 0x1653EE0ul;
        outSym->ExpectedBytesHex = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(s_p2SymBytes));
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock040WorldGetLocalPlayer(NmWorldEntitySnapshot* outEntity)
    {
        outEntity->StructSize = (uint)sizeof(NmWorldEntitySnapshot);
        outEntity->EntityId = 10u;
        outEntity->EntityPtr = (void*)0x1010;
        outEntity->KindFlags = (uint)(NmWorldEntityKind.Player | NmWorldEntityKind.LocalPlayer | NmWorldEntityKind.Alive);
        outEntity->GridX = 5;
        outEntity->GridY = 6;
        outEntity->WorldX = 50f;
        outEntity->WorldY = 60f;
        outEntity->WorldZ = 0f;
        outEntity->Hp = 3000;
        outEntity->MaxHp = 3000;
        outEntity->IsAlive = 1;
        "Metadata/Characters/Dex/Dex\0"u8.CopyTo(new Span<byte>(outEntity->MetadataPath, 256));
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock040WorldListEntities(NmWorldEntityQuery* query, NmWorldEntitySnapshot* outBuf, uint cap, uint* outCount, uint* outTotal)
    {
        *outTotal = 1u;
        if (cap < 1u || outBuf is null)
        {
            *outCount = 0u;
            return NmStatus.ErrBufferTooSmall;
        }

        FillMockWorldItem500(&outBuf[0]);
        *outCount = 1u;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock040WorldGetEntity(uint entityId, NmWorldEntitySnapshot* outEntity)
    {
        if (entityId != 500u) return NmStatus.ErrSymbolNotFound;
        FillMockWorldItem500(outEntity);
        return NmStatus.Ok;
    }

    private static unsafe void FillMockWorldItem500(NmWorldEntitySnapshot* e)
    {
        e->StructSize = (uint)sizeof(NmWorldEntitySnapshot);
        e->EntityId = 500u;
        e->EntityPtr = (void*)0x5000;
        e->KindFlags = (uint)(NmWorldEntityKind.WorldItem | NmWorldEntityKind.Targetable);
        e->WorldX = 55f;
        e->WorldY = 60f;
        e->FilterVisible = 1;
        e->IsTargetable = 1;
        "Metadata/MiscellaneousObjects/WorldItem\0"u8.CopyTo(new Span<byte>(e->MetadataPath, 256));
        e->WorldItem.Present = 1;
        e->WorldItem.ItemEntity = (void*)0x5050;
        e->WorldItem.ItemId = 999u;
        e->WorldItem.StackSize = 1u;
        e->WorldItem.MaxStackSize = 10u;
        "Metadata/Items/Currency/CurrencyDuplicate\0"u8.CopyTo(new Span<byte>(e->WorldItem.MetadataPath, 256));
        "Mirror of Kalandra\0"u8.CopyTo(new Span<byte>(e->WorldItem.BaseName, 128));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock040WorldCreateClientObject(byte* metadataUtf8, float x, float y, float z, ulong* outHandle)
    {
        *outHandle = 0x0000_0001_C000_0042ul;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NmStatus Mock040WorldRemoveClientObject(ulong handle)
    {
        s_lastRemovedClientHandle = handle;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock040StatsReadEntity(uint entityId, int statId, int* outValue)
    {
        if (statId == 0x10042)
        {
            *outValue = 7777;
            return NmStatus.Ok;
        }

        return NmStatus.ErrSymbolNotFound;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NmStatus Mock040StatsSetLocalEntity(uint entityId, int statId, int value)
    {
        s_lastWrittenStatEntityId = unchecked((int)entityId);
        s_lastWrittenStatId = statId;
        s_lastWrittenStatVal = value;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock040StatsListEntity(uint entityId, NmStatEntry* outBuf, uint cap, uint* outCount, uint* outTotal)
    {
        *outTotal = 2u;
        if (cap < 2u || outBuf is null)
        {
            *outCount = 0u;
            return NmStatus.ErrBufferTooSmall;
        }

        outBuf[0] = new NmStatEntry { StatId = 1, Value = 3000 };
        outBuf[1] = new NmStatEntry { StatId = 0x10042, Value = 7777 };
        *outCount = 2u;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock040LifecycleGetAreaInfo(NmAreaInfo* outInfo)
    {
        outInfo->StructSize = (uint)sizeof(NmAreaInfo);
        outInfo->InGame = 1;
        outInfo->IsTown = 1;
        outInfo->Act = 1;
        outInfo->AreaLevel = 1;
        outInfo->EffectiveLevel = 1;
        outInfo->Hash16 = 0x1234;
        outInfo->AreaSeed = 0xDEADBEEFu;
        outInfo->RulesetCount = 2;
        outInfo->RulesetIds[0] = 0x10u;
        outInfo->RulesetIds[1] = 0xA0u;
        "1_1_town\0"u8.CopyTo(new Span<byte>(outInfo->AreaId, 128));
        "Lioneye's Watch\0"u8.CopyTo(new Span<byte>(outInfo->AreaName, 128));
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock040LifecycleSetAreaCallback(delegate* unmanaged[Cdecl]<int, NmAreaInfo*, void*, void> cb, void* ud)
    {
        s_areaCb = cb;
        s_areaUd = ud;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock040FilterGetState(NmItemFilterState* outState)
    {
        outState->StructSize = (uint)sizeof(NmItemFilterState);
        outState->Loaded = 1;
        outState->HideFilteredGroundItems = 1;
        outState->AltHighlightActive = 0;
        outState->FilterKind = 1;
        outState->RuleCount = 420;
        "Strict.filter\0"u8.CopyTo(new Span<byte>(outState->FilterName, 128));
        "C:\\Users\\Exile\\Documents\\My Games\\Path of Exile\0"u8.CopyTo(new Span<byte>(outState->FilterFolder, 256));
        "C:\\Users\\Exile\\Documents\\My Games\\Path of Exile\\Strict.filter\0"u8.CopyTo(new Span<byte>(outState->FilterPath, 384));
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NmStatus Mock040FilterReload()
    {
        s_filterReloadTriggered++;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock040FilterGetWorldItemResult(uint entityId, NmItemFilterResult* outRes)
    {
        outRes->StructSize = (uint)sizeof(NmItemFilterResult);
        outRes->Evaluated = 1;
        outRes->IsVisible = 1;
        outRes->FontSize = 45;
        outRes->TextRgba = 0xFF0000FFu;
        outRes->BorderRgba = 0xFF0000FFu;
        outRes->BgRgba = 0xFFFFFFFFu;
        outRes->AlertSoundId = 6;
        outRes->MinimapIconShape = 0;
        outRes->MinimapIconColor = 0;
        outRes->MinimapIconSize = 0;
        outRes->BeamColor = 0;
        outRes->HasLabelRect = 1;
        outRes->LabelX = 100f;
        outRes->LabelY = 200f;
        outRes->LabelW = 120f;
        outRes->LabelH = 28f;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock040FilterEvaluateEntity(uint entityId, NmItemFilterResult* outRes)
    {
        outRes->StructSize = (uint)sizeof(NmItemFilterResult);
        outRes->Evaluated = 1;
        outRes->IsVisible = 1;
        outRes->FontSize = 40;
        outRes->AlertSoundId = 9;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock040FilterSetReloadCallback(delegate* unmanaged[Cdecl]<int, NmItemFilterState*, void*, void> cb, void* ud)
    {
        s_filterReloadCb = cb;
        s_filterReloadUd = ud;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock040CameraGetState(NmCameraState* outState)
    {
        outState->StructSize = (uint)sizeof(NmCameraState);
        outState->Valid = 1;
        outState->PosX = -462f;
        outState->PosY = -462f;
        outState->PosZ = -981.75f;
        outState->Zoom = 1.0f;
        outState->ZoomOverride = s_lastNativeZoomOverride < 0f ? -1.0f : s_lastNativeZoomOverride;
        outState->ZoomTarget = 1.0f;
        outState->Fov = 0.785398f;
        outState->ViewportW = 2560;
        outState->ViewportH = 1440;
        outState->ViewProj[0] = 1f;
        outState->ViewProj[5] = 1f;
        outState->ViewProj[10] = 1f;
        outState->ViewProj[15] = 1f;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock040CameraWorldToScreen(float wx, float wy, float wz, float* outSx, float* outSy, int* outInFront)
    {
        if (wz < -100f)
        {
            *outInFront = 0;
            return NmStatus.Ok;
        }

        *outSx = 1280f;
        *outSy = 720f;
        *outInFront = 1;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NmStatus Mock040CameraSetZoomOverride(float zoom)
    {
        s_lastNativeZoomOverride = zoom;
        return NmStatus.Ok;
    }

    // --- Helper Test Types & Dynamic Collectible Mod Emitter ---

    private sealed class TrackingRuleset(byte id, Action onEntered, Action onLeft) : IClientRuleset
    {
        public byte RulesetId => id;
        public string Name => "TrackingRuleset";
        public void OnAreaEntered(AreaEventArgs area) => onEntered();
        public void OnRulesetUpdate(ReadOnlySpan<byte> payload) { }
        public void OnFrame(float deltaSeconds) { }
        public void OnAreaLeft() => onLeft();
    }

    private sealed class ZoomInProcessMod(float zoom) : IClientMod
    {
        public void Load(IClientContext context) => context.Game.Camera.SetZoomOverride(zoom);
    }

    private sealed class Phase2EventMod(bool throwOnEvents) : IClientMod
    {
        public int FilterReloadCount { get; private set; }
        public int AreaEnteredCount { get; private set; }

        public void Load(IClientContext context)
        {
            context.Game.ItemFilter.FilterReloaded += _ =>
            {
                if (throwOnEvents) throw new InvalidOperationException("Boom on FilterReloaded");
                FilterReloadCount++;
            };
            context.Game.Lifecycle.AreaEntered += _ =>
            {
                if (throwOnEvents) throw new InvalidOperationException("Boom on AreaEntered");
                AreaEnteredCount++;
            };
        }
    }

    private static void EmitPhase2CollectibleModAssembly(string outputDllPath, string assemblyName, float zoomOverride)
    {
        var ab = new PersistedAssemblyBuilder(new AssemblyName(assemblyName), typeof(object).Assembly);
        ModuleBuilder mb = ab.DefineDynamicModule(assemblyName);
        TypeBuilder tb = mb.DefineType(
            $"{assemblyName}.Phase2CollectibleMod",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class,
            typeof(object),
            [typeof(IClientMod)]);
        tb.DefineDefaultConstructor(MethodAttributes.Public);

        MethodBuilder onFilterReload = tb.DefineMethod(
            "OnFilterReload",
            MethodAttributes.Private,
            typeof(void),
            [typeof(ItemFilterReloadedEventArgs)]);
        onFilterReload.GetILGenerator().Emit(OpCodes.Ret);

        MethodBuilder onAreaEntered = tb.DefineMethod(
            "OnAreaEntered",
            MethodAttributes.Private,
            typeof(void),
            [typeof(AreaEventArgs)]);
        onAreaEntered.GetILGenerator().Emit(OpCodes.Ret);

        MethodInfo ifaceLoad = typeof(IClientMod).GetMethod(nameof(IClientMod.Load))!;
        MethodBuilder loadMethod = tb.DefineMethod(
            nameof(IClientMod.Load),
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
            typeof(void),
            [typeof(IClientContext)]);

        ILGenerator il = loadMethod.GetILGenerator();
        LocalBuilder gameLocal = il.DeclareLocal(typeof(IGameServices));
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, typeof(IClientContext).GetProperty(nameof(IClientContext.Game))!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, gameLocal);

        // game.Camera.SetZoomOverride(new float?(zoomOverride));
        il.Emit(OpCodes.Ldloc, gameLocal);
        il.Emit(OpCodes.Callvirt, typeof(IGameServices).GetProperty(nameof(IGameServices.Camera))!.GetGetMethod()!);
        il.Emit(OpCodes.Ldc_R4, zoomOverride);
        il.Emit(OpCodes.Newobj, typeof(float?).GetConstructor([typeof(float)])!);
        il.Emit(OpCodes.Callvirt, typeof(ICameraService).GetMethod(nameof(ICameraService.SetZoomOverride))!);

        // game.World.SpawnClientObject("Metadata/Effects/TempMarker", 1f, 2f, 3f);
        il.Emit(OpCodes.Ldloc, gameLocal);
        il.Emit(OpCodes.Callvirt, typeof(IGameServices).GetProperty(nameof(IGameServices.World))!.GetGetMethod()!);
        il.Emit(OpCodes.Ldstr, "Metadata/Effects/TempMarker");
        il.Emit(OpCodes.Ldc_R4, 1f);
        il.Emit(OpCodes.Ldc_R4, 2f);
        il.Emit(OpCodes.Ldc_R4, 3f);
        il.Emit(OpCodes.Callvirt, typeof(IWorldService).GetMethod(nameof(IWorldService.SpawnClientObject))!);
        il.Emit(OpCodes.Pop);

        // game.ItemFilter.FilterReloaded += OnFilterReload;
        il.Emit(OpCodes.Ldloc, gameLocal);
        il.Emit(OpCodes.Callvirt, typeof(IGameServices).GetProperty(nameof(IGameServices.ItemFilter))!.GetGetMethod()!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldftn, onFilterReload);
        il.Emit(OpCodes.Newobj, typeof(Action<ItemFilterReloadedEventArgs>).GetConstructor([typeof(object), typeof(nint)])!);
        il.Emit(OpCodes.Callvirt, typeof(IItemFilterService).GetEvent(nameof(IItemFilterService.FilterReloaded))!.GetAddMethod()!);

        // game.Lifecycle.AreaEntered += OnAreaEntered;
        il.Emit(OpCodes.Ldloc, gameLocal);
        il.Emit(OpCodes.Callvirt, typeof(IGameServices).GetProperty(nameof(IGameServices.Lifecycle))!.GetGetMethod()!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldftn, onAreaEntered);
        il.Emit(OpCodes.Newobj, typeof(Action<AreaEventArgs>).GetConstructor([typeof(object), typeof(nint)])!);
        il.Emit(OpCodes.Callvirt, typeof(IGameLifecycle).GetEvent(nameof(IGameLifecycle.AreaEntered))!.GetAddMethod()!);

        il.Emit(OpCodes.Ret);

        tb.DefineMethodOverride(loadMethod, ifaceLoad);
        tb.CreateType();
        ab.Save(outputDllPath);
    }
}
