using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LocalExile.Client.Host;
using LocalExile.Client.Host.Loading;
using LocalExile.Client.Host.Native;
using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Engine;
using Xunit;

namespace LocalExile.Client.Tests;

[Collection("NativeEntryPointSerial")]
public sealed class Phase5AndLeagueGaps120Tests
{
    private static int s_abi060U32WireEnabled;
    private static int s_abi060FormatMillionsEnabled;
    private static uint s_abi060LastOverrideEntityId;
    private static uint s_abi060LastOverrideStackSize;
    private static int s_abi060CompactSocketsEnabled;
    private static uint s_abi060LastEffectEntityId;
    private static string? s_abi060LastEffectPath;
    private static string? s_abi060LastObjectMeta;
    private static float s_abi060LastObjX;
    private static float s_abi060LastObjY;
    private static float s_abi060LastObjZ;
    private static float s_abi060LastObjRot;
    private static float s_abi060LastObjScale;
    private static ulong s_abi060NextObjHandle;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockGetHostInfo060(NmHostInfo* outInfo)
    {
        *outInfo = default;
        outInfo->StructSize = (uint)sizeof(NmHostInfo);
        outInfo->AbiVersion = NativeShimConstants.AbiVersion;
        outInfo->BuildId = NmBuildId.Build3293d328228;
        outInfo->ImageBase = NativeShimConstants.NominalImageBase;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NmStatus MockStackSetU32Wire(int enabled)
    {
        s_abi060U32WireEnabled = enabled;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NmStatus MockStackSetFormatMillions(int enabled)
    {
        s_abi060FormatMillionsEnabled = enabled;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NmStatus MockStackSetEntityOverride(uint entityId, uint stackSize)
    {
        if (entityId == 0)
        {
            return NmStatus.ErrNotFound;
        }

        s_abi060LastOverrideEntityId = entityId;
        s_abi060LastOverrideStackSize = stackSize;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NmStatus MockUiSetCompactSockets(int enabled)
    {
        s_abi060CompactSocketsEnabled = enabled;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockWorldPlayEffect(uint targetEntityId, byte* effectPathUtf8)
    {
        s_abi060LastEffectEntityId = targetEntityId;
        s_abi060LastEffectPath = effectPathUtf8 is not null ? Marshal.PtrToStringUTF8((nint)effectPathUtf8) : null;
        if (s_abi060LastEffectPath is not null)
        {
            if (s_abi060LastEffectPath.EndsWith(".ao", StringComparison.OrdinalIgnoreCase) ||
                s_abi060LastEffectPath.EndsWith(".epk", StringComparison.OrdinalIgnoreCase))
            {
                return NmStatus.ErrInvalidArg;
            }

            if (s_abi060LastEffectPath.Contains("Missing", StringComparison.OrdinalIgnoreCase))
            {
                return NmStatus.ErrNotFound;
            }
        }

        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockWorldCreateClientObject(
        byte* metadataUtf8,
        float x,
        float y,
        float z,
        ulong* outHandle)
    {
        s_abi060LastObjectMeta = metadataUtf8 is not null ? Marshal.PtrToStringUTF8((nint)metadataUtf8) : null;
        if (s_abi060LastObjectMeta is not null)
        {
            if (s_abi060LastObjectMeta.EndsWith(".ao", StringComparison.OrdinalIgnoreCase) ||
                s_abi060LastObjectMeta.EndsWith(".epk", StringComparison.OrdinalIgnoreCase))
            {
                *outHandle = 0;
                return NmStatus.ErrInvalidArg;
            }

            if (s_abi060LastObjectMeta.Contains("Missing", StringComparison.OrdinalIgnoreCase))
            {
                *outHandle = 0;
                return NmStatus.ErrNotFound;
            }
        }

        s_abi060LastObjX = x;
        s_abi060LastObjY = y;
        s_abi060LastObjZ = z;
        *outHandle = ++s_abi060NextObjHandle;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus MockWorldCreateClientObjectEx(
        byte* metadataUtf8,
        float x,
        float y,
        float z,
        float rotationRad,
        float scale,
        ulong* outHandle)
    {
        s_abi060LastObjectMeta = metadataUtf8 is not null ? Marshal.PtrToStringUTF8((nint)metadataUtf8) : null;
        if (s_abi060LastObjectMeta is not null)
        {
            if (s_abi060LastObjectMeta.EndsWith(".ao", StringComparison.OrdinalIgnoreCase) ||
                s_abi060LastObjectMeta.EndsWith(".epk", StringComparison.OrdinalIgnoreCase))
            {
                *outHandle = 0;
                return NmStatus.ErrInvalidArg;
            }

            if (s_abi060LastObjectMeta.Contains("Missing", StringComparison.OrdinalIgnoreCase))
            {
                *outHandle = 0;
                return NmStatus.ErrNotFound;
            }
        }

        s_abi060LastObjX = x;
        s_abi060LastObjY = y;
        s_abi060LastObjZ = z;
        s_abi060LastObjRot = rotationRad;
        s_abi060LastObjScale = scale;
        *outHandle = ++s_abi060NextObjHandle;
        return NmStatus.Ok;
    }

    [Fact]
    public unsafe void Abi060_StructSizeOffsetsAndAll6Phase5SlotsRoundTripThroughNativeShimBridge()
    {
        Assert.Equal("1.2.0", SdkVersion.Current);
        Assert.Equal(new Version(1, 2, 0), SdkVersion.Parsed);
        Assert.Equal(0x00000600u, NativeShimConstants.AbiVersion);
        Assert.Equal(1168, sizeof(NmAbiTable));

        Assert.Equal(1120, (int)Marshal.OffsetOf<NmAbiTable>(nameof(NmAbiTable.StackSetU32WireEnabled)));
        Assert.Equal(1128, (int)Marshal.OffsetOf<NmAbiTable>(nameof(NmAbiTable.StackSetFormatMillionsEnabled)));
        Assert.Equal(1136, (int)Marshal.OffsetOf<NmAbiTable>(nameof(NmAbiTable.StackSetEntityOverride)));
        Assert.Equal(1144, (int)Marshal.OffsetOf<NmAbiTable>(nameof(NmAbiTable.UiSetCompactSocketsEnabled)));
        Assert.Equal(1152, (int)Marshal.OffsetOf<NmAbiTable>(nameof(NmAbiTable.WorldPlayEffect)));
        Assert.Equal(1160, (int)Marshal.OffsetOf<NmAbiTable>(nameof(NmAbiTable.WorldCreateClientObjectEx)));

        s_abi060U32WireEnabled = 0;
        s_abi060FormatMillionsEnabled = 0;
        s_abi060LastOverrideEntityId = 0;
        s_abi060LastOverrideStackSize = 0;
        s_abi060CompactSocketsEnabled = 0;
        s_abi060LastEffectEntityId = 0;
        s_abi060LastEffectPath = null;
        s_abi060LastObjectMeta = null;
        s_abi060NextObjHandle = 0x4000_0100ul;

        NmAbiTable table = default;
        table.StructSize = (uint)sizeof(NmAbiTable);
        table.AbiVersion = NativeShimConstants.AbiVersion;
        table.GetHostInfo = &MockGetHostInfo060;
        table.StackSetU32WireEnabled = &MockStackSetU32Wire;
        table.StackSetFormatMillionsEnabled = &MockStackSetFormatMillions;
        table.StackSetEntityOverride = &MockStackSetEntityOverride;
        table.UiSetCompactSocketsEnabled = &MockUiSetCompactSockets;
        table.WorldPlayEffect = &MockWorldPlayEffect;
        table.WorldCreateClientObjectEx = &MockWorldCreateClientObjectEx;

        using var bridge = new NativeShimBridge((nint)(&table));

        bridge.SetExtendedU32StacksEnabled(true);
        Assert.Equal(1, s_abi060U32WireEnabled);

        bridge.SetFormatMillionsEnabled(true);
        Assert.Equal(1, s_abi060FormatMillionsEnabled);

        Assert.True(bridge.SetEntityStackOverride(42u, 1_250_000u));
        Assert.Equal(42u, s_abi060LastOverrideEntityId);
        Assert.Equal(1_250_000u, s_abi060LastOverrideStackSize);
        Assert.False(bridge.SetEntityStackOverride(0u, 999u));

        bridge.SetCompactSocketsEnabled(true);
        Assert.Equal(1, s_abi060CompactSocketsEnabled);

        bridge.PlayEffect(777u, "Metadata/Effects/Spells/breach/breach_ring");
        Assert.Equal(777u, s_abi060LastEffectEntityId);
        Assert.Equal("Metadata/Effects/Spells/breach/breach_ring", s_abi060LastEffectPath);

        uint objId = bridge.SpawnClientObject("Metadata/MiscellaneousObjects/Mods/League/Monolith", 120f, 340f, 15f, 1.5707964f, 1.35f);
        Assert.Equal(0x4000_0101u, objId);
        Assert.Equal("Metadata/MiscellaneousObjects/Mods/League/Monolith", s_abi060LastObjectMeta);
        Assert.Equal(120f, s_abi060LastObjX, precision: 3);
        Assert.Equal(340f, s_abi060LastObjY, precision: 3);
        Assert.Equal(15f, s_abi060LastObjZ, precision: 3);
        Assert.Equal(1.5707964f, s_abi060LastObjRot, precision: 4);
        Assert.Equal(1.35f, s_abi060LastObjScale, precision: 3);
    }

    [Fact]
    public unsafe void NativeShimBridge_WorldPlayEffectAndSpawnClientObject_RejectAoAndMissingOtCleanly()
    {
        s_abi060LastEffectEntityId = 0;
        s_abi060LastEffectPath = null;
        s_abi060LastObjectMeta = null;
        s_abi060NextObjHandle = 0x5000_0000ul;

        NmAbiTable table = default;
        table.StructSize = (uint)sizeof(NmAbiTable);
        table.AbiVersion = NativeShimConstants.AbiVersion;
        table.GetHostInfo = &MockGetHostInfo060;
        table.WorldCreateClientObject = &MockWorldCreateClientObject;
        table.WorldPlayEffect = &MockWorldPlayEffect;
        table.WorldCreateClientObjectEx = &MockWorldCreateClientObjectEx;

        using var bridge = new NativeShimBridge((nint)(&table));

        // Non-.ot asset paths (.ao / .epk) rejected with ArgumentException cleanly
        var playAoEx = Assert.Throws<ArgumentException>(() =>
            bridge.PlayEffect(67108870u, "Metadata/Pet/Mods/Maxwell/MaxwellPet.ao"));
        Assert.Contains(".ot", playAoEx.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Throws<ArgumentException>(() =>
            bridge.PlayEffect(67108870u, "Metadata/Effects/Spells/breach/breach_ring.epk"));
        Assert.Throws<ArgumentException>(() =>
            bridge.SpawnClientObject("Metadata/Pet/Mods/Maxwell/MaxwellPet.ao", 1f, 2f, 3f));
        Assert.Throws<ArgumentException>(() =>
            bridge.SpawnClientObject("Metadata/Pet/Mods/Maxwell/MaxwellPet.ao", 1f, 2f, 3f, 0f, 1f));

        // Missing .ot paths rejected with FileNotFoundException cleanly
        Assert.Throws<FileNotFoundException>(() =>
            bridge.PlayEffect(67108870u, "Metadata/Effects/MissingEffect"));
        Assert.Throws<FileNotFoundException>(() =>
            bridge.SpawnClientObject("Metadata/Effects/MissingObject", 1f, 2f, 3f));
        Assert.Throws<FileNotFoundException>(() =>
            bridge.SpawnClientObject("Metadata/Effects/MissingObject", 1f, 2f, 3f, 0f, 1f));

        // Failed spawns MUST NOT leave phantom objects tracked in GetWorldObjects()
        Assert.Empty(bridge.GetWorldObjects());
    }

    [Fact]
    public void NativeEntryPoint_ResolveModsDirectory_SupportsWaypointSiblingAndNestedHostLayouts()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"nm-mods-probe-{Guid.NewGuid():N}");
        try
        {
            string waypointRootMods = Path.Combine(tempRoot, "Mods", "Client");
            string clientDir = Path.Combine(tempRoot, "Client");
            string nestedHostDir = Path.Combine(clientDir, "nativemods-host");
            Directory.CreateDirectory(waypointRootMods);
            Directory.CreateDirectory(nestedHostDir);

            // 1. Waypoint layout: Client/nativemods-host/ -> ../../Mods/Client
            string fromNested = NativeEntryPoint.ResolveModsDirectory(nestedHostDir, envModsDir: string.Empty);
            Assert.Equal(Path.GetFullPath(waypointRootMods), Path.GetFullPath(fromNested));

            // 2. Client/ layout -> ../Mods/Client
            string fromClient = NativeEntryPoint.ResolveModsDirectory(clientDir, envModsDir: string.Empty);
            Assert.Equal(Path.GetFullPath(waypointRootMods), Path.GetFullPath(fromClient));

            // 3. Explicit env override always wins
            string explicitDir = Path.Combine(tempRoot, "CustomMods");
            string fromEnv = NativeEntryPoint.ResolveModsDirectory(nestedHostDir, envModsDir: explicitDir);
            Assert.Equal(explicitDir, fromEnv);

            // 4. Direct <hostDir>/Mods/Client takes precedence over parent fallback when present
            string directMods = Path.Combine(nestedHostDir, "Mods", "Client");
            Directory.CreateDirectory(directMods);
            string fromDirect = NativeEntryPoint.ResolveModsDirectory(nestedHostDir, envModsDir: string.Empty);
            Assert.Equal(Path.GetFullPath(directMods), Path.GetFullPath(fromDirect));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StackExtensionAndCompactSockets_AreTrackedPerModAndAutoDisabledOnUnloadAndDisconnect()
    {
        using var bridge = new SimulatedNativeBridge();
        using var runtime = new ClientHostRuntime(bridge);
        runtime.Start();

        Assert.False(runtime.StackExtension.IsExtendedU32StacksEnabled);
        Assert.False(runtime.StackExtension.IsFormatMillionsEnabled);
        Assert.False(runtime.Ui.IsCompactSocketsEnabled);

        var modA = new Phase5ToggleMod(enableU32: true, enableMillions: true, enableCompactSockets: true, overrideEntityId: 500u, overrideStackSize: 250_000u);
        var modB = new Phase5ToggleMod(enableU32: true, enableMillions: false, enableCompactSockets: true, overrideEntityId: 501u, overrideStackSize: 1_000_000u);

        runtime.Loader.LoadInProcessMod(new ClientModManifest("mod-a", "Mod A", "1.2.0", SdkVersionRange: ">=1.2.0 <2.0.0"), modA);
        runtime.Loader.LoadInProcessMod(new ClientModManifest("mod-b", "Mod B", "1.2.0", SdkVersionRange: ">=1.2.0 <2.0.0"), modB);

        Assert.True(runtime.StackExtension.IsExtendedU32StacksEnabled);
        Assert.True(runtime.StackExtension.IsFormatMillionsEnabled);
        Assert.True(runtime.Ui.IsCompactSocketsEnabled);
        Assert.True(bridge.SimulatedExtendedU32StacksEnabled);
        Assert.True(bridge.SimulatedFormatMillionsEnabled);
        Assert.True(bridge.SimulatedCompactSocketsEnabled);
        Assert.Equal(250_000u, bridge.SimulatedEntityStackOverrides[500u]);
        Assert.Equal(1_000_000u, bridge.SimulatedEntityStackOverrides[501u]);

        // Unload modA: modB still holds U32 + compact sockets, while FormatMillions drops to false!
        await runtime.Loader.UnloadModAsync("mod-a");
        Assert.True(runtime.StackExtension.IsExtendedU32StacksEnabled);
        Assert.False(runtime.StackExtension.IsFormatMillionsEnabled);
        Assert.True(runtime.Ui.IsCompactSocketsEnabled);
        Assert.True(bridge.SimulatedExtendedU32StacksEnabled);
        Assert.False(bridge.SimulatedFormatMillionsEnabled);
        Assert.True(bridge.SimulatedCompactSocketsEnabled);
        Assert.False(runtime.StackExtension.ActiveEntityOverrides.ContainsKey(500u));
        Assert.True(runtime.StackExtension.ActiveEntityOverrides.ContainsKey(501u));

        // Unload modB: all Phase 5 toggles auto-disable cleanly!
        await runtime.Loader.UnloadModAsync("mod-b");
        Assert.False(runtime.StackExtension.IsExtendedU32StacksEnabled);
        Assert.False(runtime.Ui.IsCompactSocketsEnabled);
        Assert.False(bridge.SimulatedExtendedU32StacksEnabled);
        Assert.False(bridge.SimulatedCompactSocketsEnabled);

        // Re-enable and verify disconnect resets session-scoped StackExtension flags!
        runtime.StackExtension.SetExtendedU32StacksEnabled(true);
        runtime.StackExtension.SetFormatMillionsEnabled(true);
        runtime.StackExtension.SetEntityStackOverride(600u, 999_999u);
        Assert.True(bridge.SimulatedExtendedU32StacksEnabled);
        Assert.True(bridge.SimulatedFormatMillionsEnabled);

        runtime.NotifyDisconnected("test-disconnect");
        Assert.False(runtime.StackExtension.IsExtendedU32StacksEnabled);
        Assert.False(runtime.StackExtension.IsFormatMillionsEnabled);
        Assert.False(bridge.SimulatedExtendedU32StacksEnabled);
        Assert.False(bridge.SimulatedFormatMillionsEnabled);
        Assert.Empty(runtime.StackExtension.ActiveEntityOverrides);
    }

    [Fact]
    public async Task SpatialAudio_ComputesLiveCameraAttenuationUpdatesOnFrameAndCleansUpOnUnload()
    {
        using var bridge = new SimulatedNativeBridge();
        using var runtime = new ClientHostRuntime(bridge);
        runtime.Start();

        bridge.SimulatedCameraState = CreateCameraState(targetX: 0f, targetY: 0f, targetZ: 0f, viewportW: 1920f, viewportH: 1080f);

        // Play at distance 500 with maxDistance 1000 and base volume 0.8 => attenuated = 0.8 * (1 - 0.5) = 0.4
        AudioChannelHandle ch = runtime.Audio.PlaySoundAtPosition("Audio/Mods/League/monolith_hum.ogg", 500f, 0f, 0f, volume: 0.8f, maxDistance: 1000f, loop: true);
        Assert.True(ch.IsValid);
        Assert.True(bridge.TryGetSimulatedAudioChannel(ch, out string path, out bool isFmod, out float vol, out bool loop, out bool active));
        Assert.Equal("Audio/Mods/League/monolith_hum.ogg", path);
        Assert.False(isFmod);
        Assert.True(loop);
        Assert.True(active);
        Assert.Equal(0.4f, vol, precision: 3);

        // Move camera target closer to (250, 0, 0) => distance 250 => gain 0.75 => volume 0.6 on next frame!
        bridge.SimulatedCameraState = CreateCameraState(targetX: 250f, targetY: 0f, targetZ: 0f, viewportW: 1920f, viewportH: 1080f);
        runtime.TickFrame();
        Assert.True(bridge.TryGetSimulatedAudioChannel(ch, out _, out _, out vol, out _, out _));
        Assert.Equal(0.6f, vol, precision: 3);

        // Move sound beyond maxDistance => clamped to 0.0f
        runtime.Audio.SetChannelPosition(ch, 2000f, 0f, 0f, maxDistance: 1000f);
        Assert.True(bridge.TryGetSimulatedAudioChannel(ch, out _, out _, out vol, out _, out _));
        Assert.Equal(0f, vol, precision: 3);

        // Move sound right onto listener => full base volume 0.8f
        runtime.Audio.SetChannelPosition(ch, 250f, 0f, 0f, maxDistance: 1000f);
        Assert.True(bridge.TryGetSimulatedAudioChannel(ch, out _, out _, out vol, out _, out _));
        Assert.Equal(0.8f, vol, precision: 3);

        // Adjust base volume while spatial -> scales live attenuated volume
        runtime.Audio.SetChannelVolume(ch, 0.5f);
        Assert.True(bridge.TryGetSimulatedAudioChannel(ch, out _, out _, out vol, out _, out _));
        Assert.Equal(0.5f, vol, precision: 3);

        runtime.Audio.StopChannel(ch);
        Assert.Equal(0, runtime.Audio.SpatialChannelCount);

        // Verify mod-owned spatial audio + transformed client object auto-clean on unload!
        var leagueMod = new SpatialAndWorldMod();
        runtime.Loader.LoadInProcessMod(new ClientModManifest("league-mod", "League Mod", "1.2.0", SdkVersionRange: ">=1.2.0 <2.0.0"), leagueMod);
        Assert.True(leagueMod.SpatialHandle.IsValid);
        Assert.True(bridge.IsAudioPlaying(leagueMod.SpatialHandle));
        Assert.NotEqual(0u, leagueMod.SpawnedObjectId);
        Assert.Equal((1.25f, 2.0f), bridge.SimulatedClientObjectTransforms[leagueMod.SpawnedObjectId]);
        Assert.Contains(bridge.PlayedEffects, e => e.ObjectId == leagueMod.SpawnedObjectId && e.EffectPath == "Metadata/Effects/League/Pulse");

        await runtime.Loader.UnloadModAsync("league-mod");
        Assert.False(bridge.IsAudioPlaying(leagueMod.SpatialHandle));
        Assert.Null(runtime.World.FindObject(leagueMod.SpawnedObjectId));
        Assert.Equal(0, runtime.Audio.SpatialChannelCount);
    }

    [Fact]
    public void CameraService_TryWorldToUiPosition_ConvertsViewportPixelsInto1600VirtualUiSpace()
    {
        using var bridge = new SimulatedNativeBridge();
        using var runtime = new ClientHostRuntime(bridge);

        // 1920x1080 viewport: (50, 25, 0) projects to screen (1440, 405).
        // Virtual UI scale = 1600 / 1080 => uiX = 1440 * (1600 / 1080) = 2133.3333, uiY = 405 * (1600 / 1080) = 600.0!
        bridge.SimulatedCameraState = CreateCameraState(0f, 0f, 0f, 1920f, 1080f);
        Assert.True(runtime.Camera.TryWorldToUiPosition(50f, 25f, 0f, out float uiX, out float uiY));
        Assert.Equal(1440f * (1600f / 1080f), uiX, precision: 2);
        Assert.Equal(600f, uiY, precision: 2);

        // 2560x1600 native 1:1 UI height: center (0, 0, 0) -> (1280, 800)
        bridge.SimulatedCameraState = CreateCameraState(0f, 0f, 0f, 2560f, 1600f);
        Assert.True(runtime.Camera.TryWorldToUiPosition(new WorldPosition3D(0f, 0f, 0f), out uiX, out uiY));
        Assert.Equal(1280f, uiX, precision: 2);
        Assert.Equal(800f, uiY, precision: 2);

        // 3840x2160 (4K) 16:9: center (0, 0, 0) -> screen (1920, 1080) -> UI (1422.222, 800.0)
        bridge.SimulatedCameraState = CreateCameraState(0f, 0f, 0f, 3840f, 2160f);
        Assert.True(runtime.Camera.TryWorldToUiPosition(0f, 0f, 0f, out uiX, out uiY));
        Assert.Equal(1920f * (1600f / 2160f), uiX, precision: 2);
        Assert.Equal(800f, uiY, precision: 2);

        // Behind camera -> false
        Assert.False(runtime.Camera.TryWorldToUiPosition(0f, 0f, -200f, out uiX, out uiY));
        Assert.Equal(0f, uiX);
        Assert.Equal(0f, uiY);

        // Invalid camera -> false
        bridge.SimulatedCameraState = CameraState.Invalid;
        Assert.False(runtime.Camera.TryWorldToUiPosition(0f, 0f, 0f, out _, out _));
    }

    private static CameraState CreateCameraState(float targetX, float targetY, float targetZ, float viewportW, float viewportH)
    {
        float[] vp =
        [
            0.01f, 0f,    0f,    0f,
            0f,    0.01f, 0f,    0f,
            0f,    0f,    0.01f, 0.01f,
            0f,    0f,    0f,    1f,
        ];
        return new CameraState(
            IsValid: true,
            Position: new WorldPosition3D(targetX - 462f, targetY - 462f, targetZ - 981.75f),
            Target: new WorldPosition3D(targetX, targetY, targetZ),
            Zoom: 1.0f,
            ZoomOverride: -1.0f,
            ZoomTarget: 1.0f,
            Fov: MathF.PI / 4f,
            ViewportWidth: viewportW,
            ViewportHeight: viewportH,
            ViewProjectionMatrix: vp);
    }

    private sealed class Phase5ToggleMod(
        bool enableU32,
        bool enableMillions,
        bool enableCompactSockets,
        uint overrideEntityId,
        uint overrideStackSize) : IClientMod
    {
        public void Load(IClientContext context)
        {
            if (enableU32)
            {
                context.Game.StackExtension.SetExtendedU32StacksEnabled(true);
            }

            if (enableMillions)
            {
                context.Game.StackExtension.SetFormatMillionsEnabled(true);
            }

            if (overrideEntityId != 0)
            {
                context.Game.StackExtension.SetEntityStackOverride(overrideEntityId, overrideStackSize);
            }

            if (enableCompactSockets)
            {
                context.Game.Ui.SetCompactSocketsEnabled(true);
            }
        }
    }

    private sealed class SpatialAndWorldMod : IClientMod
    {
        public AudioChannelHandle SpatialHandle { get; private set; }
        public uint SpawnedObjectId { get; private set; }

        public void Load(IClientContext context)
        {
            SpatialHandle = context.Game.Audio.PlaySoundAtPosition("event:/League/EncounterHum", 100f, 0f, 0f, volume: 0.9f, maxDistance: 1200f, loop: true);
            SpawnedObjectId = context.Game.World.SpawnClientObject("Metadata/MiscellaneousObjects/Mods/League/Altar", 100f, 0f, 0f, rotationRadians: 1.25f, scale: 2.0f);
            context.Game.World.PlayEffect(SpawnedObjectId, "Metadata/Effects/League/Pulse");
        }
    }
}
