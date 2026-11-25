using System.Globalization;
using System.Runtime.CompilerServices;
using DualMod.MaxwellPet.Server;
using DualMod.MaxwellPet.Shared;
using LocalExile.Client.Host;
using LocalExile.Client.Host.Native;
using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Addresses;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.Sdk.Protocol;
using LocalExile.Client.ServerBridge;
using LocalExile.Client.Waypoint;
using Xunit;

namespace LocalExile.Client.MaxwellPet.Tests;

public sealed class MaxwellPetEndToEndTests
{
    [Theory]
    [InlineData(NetTransportKind.HostRuleset)]
    [InlineData(NetTransportKind.ChatFallback)]
    public async Task FullDualSideMaxwellPetSimulation_InstallLoadSummonSpinAudioDanceCosmeticDismissFallbackAndCleanUnload(
        NetTransportKind transport)
    {
        string workspace = Path.Combine(Path.GetTempPath(), "nm-maxwell-e2e-" + Guid.NewGuid().ToString("N"));
        string localExileRoot = Path.Combine(workspace, "LocalExile");
        string stagingPkg = Path.Combine(workspace, "maxwell-pkg");
        string shadowRoot = Path.Combine(workspace, "shadow");

        try
        {
            // 1. Seed a fake LocalExile checkout with stock Client/ files that must remain untouched
            string stockClientDir = Path.Combine(localExileRoot, "Client");
            Directory.CreateDirectory(Path.Combine(stockClientDir, "Bundles2"));
            byte[] stockExe = "STOCK_POE_3_29_3D_EXE"u8.ToArray();
            byte[] stockIndex = "STOCK_POE_INDEX_BIN"u8.ToArray();
            File.WriteAllBytes(Path.Combine(stockClientDir, "Game.exe"), stockExe);
            File.WriteAllBytes(Path.Combine(stockClientDir, "Bundles2", "_.index.bin"), stockIndex);

            // 2. Stage DualMod.MaxwellPet with freshly compiled Server + Client DLLs and synthetic patched .datc64 tables
            string repoRoot = FindRepoRoot();
            string exampleRoot = Path.Combine(repoRoot, "examples", "DualMod.MaxwellPet");
            CopyDirectory(exampleRoot, stagingPkg);

            string compiledClientDll = FindCompiledAssembly(exampleRoot, "Client", "maxwell.Client.dll");
            string compiledServerDll = FindCompiledAssembly(exampleRoot, "Server", "maxwell.dll");
            File.Copy(compiledClientDll, Path.Combine(stagingPkg, "maxwell.Client.dll"), overwrite: true);
            File.Copy(compiledServerDll, Path.Combine(stagingPkg, "maxwell.dll"), overwrite: true);

            // Place synthetic patched .datc64 tables & audio placeholder into staged client/ payload
            // (matches what scripts/build-maxwell-assets.sh produces locally without committing binaries to git).
            string stagedDataDir = Path.Combine(stagingPkg, "client", "Data");
            Directory.CreateDirectory(stagedDataDir);
            byte[] syntheticPetDat = BuildSyntheticDat64(rowCount: 517, rowStride: 16);
            byte[] syntheticMtxTypesDat = BuildSyntheticDat64(rowCount: 8558, rowStride: 16);
            byte[] syntheticMtxGsDat = BuildSyntheticDat64(rowCount: 7850, rowStride: 16);
            File.WriteAllBytes(Path.Combine(stagedDataDir, "Pet.datc64"), syntheticPetDat);
            File.WriteAllBytes(Path.Combine(stagedDataDir, "MtxTypes.datc64"), syntheticMtxTypesDat);
            File.WriteAllBytes(Path.Combine(stagedDataDir, "MtxTypeGameSpecific.datc64"), syntheticMtxGsDat);

            string stagedAudioDir = Path.Combine(stagingPkg, "client", "Audio", "Mods", "Maxwell");
            Directory.CreateDirectory(stagedAudioDir);
            File.WriteAllBytes(Path.Combine(stagedAudioDir, "stock_market.ogg"), "OggS_SYNTHETIC_VORBIS_TEST"u8.ToArray());

            // 3. Validate and install via WaypointModInstaller
            var validation = DualModPackageValidator.ValidatePackageDirectory(stagingPkg, SemVer.Parse(SdkVersion.Current));
            Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors.Select(e => $"{e.Code}: {e.Message}")));

            var installer = new WaypointModInstaller();
            var installReport = installer.Install(stagingPkg, localExileRoot, new InstallOptions
            {
                CurrentSdkVersion = SemVer.Parse(SdkVersion.Current),
                PackClientBundle = true,
                DeployLooseOverlays = true
            });

            Assert.True(installReport.Succeeded, installReport.ToHumanReadableSummary());
            Assert.NotNull(installReport.ClientTargetDir);
            Assert.NotNull(installReport.BundleFile);
            string deployedClientDir = installReport.ClientTargetDir!;
            Assert.True(File.Exists(Path.Combine(deployedClientDir, "maxwell.Client.dll")));
            Assert.True(File.Exists(Path.Combine(deployedClientDir, "files", "metadata", "ui", "mods", "maxwell", "maxwellpanel.ui")));
            Assert.True(File.Exists(Path.Combine(deployedClientDir, "files", "metadata", "pet", "mods", "maxwell", "maxwellpet.ot")));
            Assert.True(File.Exists(Path.Combine(deployedClientDir, "files", "data", "pet.datc64")));

            // 4. Boot ClientHostRuntime + dynamically load maxwell.Client.dll in a collectible ALC
            using var simBridge = new SimulatedNativeBridge(ClientBuildId.Build3293d);
            using var clientHost = new ClientHostRuntime(simBridge, new ClientHostOptions
            {
                TransportKind = transport,
                HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId,
                ShadowCopyRoot = shadowRoot
            });
            clientHost.Start();

            // Run active session in a NoInlining helper so zero stack slots keep the collectible ALC alive.
            await RunActiveMaxwellSessionAsync(simBridge, clientHost, deployedClientDir, transport, syntheticPetDat);

            // 5. Verify collectible ALC clean unload + zero lingering registrations/audio + clean Waypoint uninstall
            var unloadResult = await clientHost.Loader.UnloadModAsync(MaxwellProtocol.ModId);
            Assert.True(unloadResult.WasLoaded);
            Assert.True(unloadResult.UnloadedCleanly);
            Assert.False(clientHost.GealBuiltins.IsRegistered(MaxwellProtocol.GealDanceBuiltin));
            Assert.False(clientHost.Rulesets.IsRegistered(MaxwellProtocol.MaxwellRulesetId));
            Assert.False(clientHost.Opcodes.IsRegistered(MaxwellProtocol.OpcodeStateSync));
            Assert.Equal(0, clientHost.Audio.ActiveChannelCount);
            Assert.Null(clientHost.Ui.GetWindow(MaxwellProtocol.PanelWindowId));

            var uninstallResult = installer.Uninstall(MaxwellProtocol.ModId, localExileRoot);
            Assert.True(uninstallResult.Succeeded);
            Assert.False(Directory.Exists(deployedClientDir));
            Assert.Equal(stockExe, File.ReadAllBytes(Path.Combine(stockClientDir, "Game.exe")));
            Assert.Equal(stockIndex, File.ReadAllBytes(Path.Combine(stockClientDir, "Bundles2", "_.index.bin")));
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task RunActiveMaxwellSessionAsync(
        SimulatedNativeBridge simBridge,
        ClientHostRuntime clientHost,
        string deployedClientDir,
        NetTransportKind transport,
        byte[] expectedPetDat)
    {
        var loadedMod = clientHost.Loader.LoadModFromDirectory(deployedClientDir);
        Assert.Equal(MaxwellProtocol.ModId, loadedMod.Manifest.Id);
        Assert.False(loadedMod.IsDisabled);

        // Verify client mod extension points & append-only content declarations
        Assert.True(clientHost.GealBuiltins.IsRegistered(MaxwellProtocol.GealDanceBuiltin));
        Assert.True(clientHost.Rulesets.IsRegistered(MaxwellProtocol.MaxwellRulesetId));
        Assert.True(clientHost.Opcodes.IsRegistered(MaxwellProtocol.OpcodeStateSync));
        Assert.Contains(clientHost.Content.DatRows, r => r.TableName == "MtxTypes" && r.EntryKey == MaxwellProtocol.MtxTypeId);
        Assert.Contains(clientHost.Content.DatRows, r => r.TableName == "MtxTypeGameSpecific" && r.EntryKey == MaxwellProtocol.MtxTypeId);
        Assert.Contains(clientHost.Content.DatRows, r => r.TableName == "Pet" && r.EntryKey == MaxwellProtocol.PetMetadataPath);
        Assert.Contains(clientHost.Content.WorldObjects, w => w.MetadataPath == MaxwellProtocol.PetMetadataPath && w.CarrierTable == "Pet");

        // Confirm deployed overlays are served cleanly via IFileOverlayService
        string? otText = clientHost.FileOverlayService.ReadAllText(MaxwellProtocol.PetOtPath);
        Assert.NotNull(otText);
        Assert.Contains("extends \"Metadata/Pet/Pet\"", otText, StringComparison.Ordinal);
        Assert.Equal(expectedPetDat, clientHost.FileOverlayService.ReadAllBytes(MaxwellProtocol.PetDatPath));
        Assert.True(clientHost.FileOverlayService.Exists(MaxwellProtocol.MusicOggPath));

        // Wire ServerBridgeHost + MaxwellServerMod + SimulatedMaxwellWorldBackend over loopback
        const string richConnId = "exile-maxwell-rich";
        const string vanillaConnId = "exile-maxwell-vanilla";
        var sink = new LoopbackGatewaySink(simBridge);
        var serverBridge = new ClientBridgeHost(sink, new ClientBridgeOptions
        {
            DefaultTransport = transport,
            HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId
        });

        simBridge.OnOutgoingRulesetPacket = (rulesetId, innerPayload) =>
        {
            byte[] body = MessageCodec.EncodeRulesetBody(rulesetId, innerPayload.Span);
            serverBridge.HandleIncomingPacketAsync(richConnId, IOpcodeRegistry.ClientRulesetOpcode, body).GetAwaiter().GetResult();
        };
        simBridge.OnOutgoingChatLine = line =>
        {
            serverBridge.HandleIncomingChatCommandAsync(richConnId, line).GetAwaiter().GetResult();
        };

        var worldBackend = new SimulatedMaxwellWorldBackend();
        using var serverMod = new MaxwellServerMod(serverBridge, worldBackend);
        serverMod.Attach();

        Assert.NotNull(worldBackend.RegisteredMtx);
        Assert.Equal(MaxwellProtocol.MtxRecordId, worldBackend.RegisteredMtx!.RecordId);
        Assert.Equal(MaxwellProtocol.MtxTypeHash, worldBackend.RegisteredMtx.MtxTypeHash);

        // Handshake
        clientHost.SendHandshake();
        var richSession = serverBridge.For(richConnId);
        Assert.True(richSession.HasNativeHost);
        Assert.True(richSession.HasClientMod(MaxwellProtocol.ModId));

        // Player enters area -> server grants brand-new Maxwell Pet MTX record (8558)
        await serverMod.OnPlayerEnteredAsync(richConnId, "MaxwellOwner");
        Assert.Contains(MaxwellProtocol.MtxRecordId, worldBackend.GetGrantedRecords(richConnId));

        // /maxwell -> opens MaxwellPanel.ui on client
        string openReply = await serverMod.HandleMaxwellCommandAsync(richConnId, "MaxwellOwner", []);
        Assert.Contains("Opened Maxwell control panel", openReply);

        IUiWindow? window = clientHost.Ui.GetWindow(MaxwellProtocol.PanelWindowId);
        Assert.NotNull(window);
        Assert.True(window!.Visible);
        Assert.Equal("Summon Maxwell", window.Require<IButton>("summon_button").Text);
        Assert.Equal(0, clientHost.Audio.ActiveChannelCount);

        // Click Summon Maxwell -> authoritative server spawns PetObject -> starts looping Stock Market audio
        window.Require<IButton>("summon_button").Click();

        Assert.Single(worldBackend.SpawnedPets);
        var spawned = worldBackend.SpawnedPets.Values.Single();
        Assert.Equal(MaxwellProtocol.PetMetadataPath, spawned.PetMetadataPath);
        Assert.Equal(MaxwellProtocol.PetSlot1, spawned.PetSlot);
        Assert.Equal("Dismiss Maxwell", window.Require<IButton>("summon_button").Text);
        Assert.Equal(1, clientHost.Audio.ActiveChannelCount);
        Assert.Equal(MaxwellProtocol.MusicOggPath, window.GetProperty("maxwell_audio_path"));
        Assert.Contains("Music: Looping (75%)", window.Require<ILabel>("telemetry_label").Text);

        // Increase Spin Speed (+30 -> 120) and submit Float Height (22.5)
        window.Require<IButton>("spin_faster_button").Click();
        Assert.Equal("120", window.Require<ITextBox>("spin_box").Text);

        var floatBox = window.Require<ITextBox>("float_box");
        floatBox.Text = "22.5";
        floatBox.Submit();
        Assert.Equal("22.5", floatBox.Text);

        // Advance client frames -> verify smooth spinning yaw and hover offset update
        for (int i = 0; i < 6; i++)
            simBridge.SimulateFrame(0.1f);

        Assert.Equal("6", window.GetProperty("frame_ticks"));
        Assert.True(float.TryParse(window.GetProperty("maxwell_yaw"), CultureInfo.InvariantCulture, out float yaw) && yaw > 60f);
        Assert.True(float.TryParse(window.GetProperty("maxwell_float"), CultureInfo.InvariantCulture, out float hover) && hover > 18f);

        // Trigger custom GEAL MaxwellDance via button click
        window.Require<IButton>("dance_button").Click();
        Assert.Equal("1", window.GetProperty("maxwell_dance_calls"));
        Assert.Contains(simBridge.PlayedEffects, e => e.ObjectId == spawned.EntityId && e.EffectPath == MaxwellProtocol.PetAoPath);

        // Adjust audio volume, mute, and unmute
        window.Require<IButton>("vol_up_button").Click();
        Assert.Equal("Volume: 85%", window.Require<ILabel>("volume_label").Text);

        var muteBox = window.Require<ICheckBox>("music_mute_checkbox");
        muteBox.IsChecked = true;
        Assert.Equal(0, clientHost.Audio.ActiveChannelCount);

        muteBox.IsChecked = false;
        Assert.Equal(1, clientHost.Audio.ActiveChannelCount);

        // Equip Maxwell in Pet Slot 2 (19) via native Cosmetics tab pick (0x01f9)
        var slot2Sync = await serverMod.HandleCosmeticSlotPickedAsync(richConnId, "MaxwellOwner", MaxwellProtocol.PetSlot2, MaxwellProtocol.MtxRecordId);
        Assert.NotNull(slot2Sync);
        Assert.True(slot2Sync!.IsSummoned);
        Assert.Equal(MaxwellProtocol.PetSlot2, slot2Sync.PetSlot);
        Assert.Single(worldBackend.SpawnedPets);
        Assert.Equal(MaxwellProtocol.PetSlot2, worldBackend.SpawnedPets.Values.Single().PetSlot);

        // Click Dismiss Maxwell -> despawns pet entity & stops Stock Market audio
        window.Require<IButton>("summon_button").Click();
        Assert.Empty(worldBackend.SpawnedPets);
        Assert.Equal(0, clientHost.Audio.ActiveChannelCount);
        Assert.Equal("Summon Maxwell", window.Require<IButton>("summon_button").Text);

        // Vanilla client (no NativeMods handshake) degrades gracefully via chat fallback
        string vanillaPanel = await serverMod.HandleMaxwellCommandAsync(vanillaConnId, "VanillaExile", []);
        Assert.Contains("Chat Fallback", vanillaPanel);
        Assert.Contains("Install NativeMods via Waypoint", vanillaPanel);
        Assert.False(serverBridge.For(vanillaConnId).HasNativeHost);

        string vanillaSummon = await serverMod.HandleMaxwellCommandAsync(vanillaConnId, "VanillaExile", ["summon"]);
        Assert.Contains("Summoned Maxwell", vanillaSummon);
        Assert.Contains("Chat Fallback", vanillaSummon);
        Assert.Single(worldBackend.SpawnedPets);

        string vanillaDismiss = await serverMod.HandleMaxwellCommandAsync(vanillaConnId, "VanillaExile", ["dismiss"]);
        Assert.Contains("Dismissed Maxwell", vanillaDismiss);
        Assert.Empty(worldBackend.SpawnedPets);
    }

    [Fact]
    public async Task ServerMod_ChatCommandsClampingAreaReentryAndCosmeticUnequip_BehaveAuthoritatively()
    {
        using var simBridge = new SimulatedNativeBridge(ClientBuildId.Build3293d);
        var sink = new LoopbackGatewaySink(simBridge);
        var bridge = new ClientBridgeHost(sink);
        var backend = new SimulatedMaxwellWorldBackend();
        using var serverMod = new MaxwellServerMod(bridge, backend);
        serverMod.Attach();

        const string conn1 = "conn-area-1";
        const string conn2 = "conn-area-2";
        const string character = "ExileCatLover";

        // Summon in slot 1, adjust spin/float with out-of-range inputs (must clamp)
        string summonReply = await serverMod.HandleMaxwellCommandAsync(conn1, character, ["summon", "1"]);
        Assert.Contains("Pet Slot 1", summonReply);
        uint firstEntity = Assert.Single(backend.SpawnedPets).Key;

        string spinReply = await serverMod.HandleMaxwellCommandAsync(conn1, character, ["spin", "9999"]);
        Assert.Contains("spin=720 deg/s", spinReply);

        string floatReply = await serverMod.HandleMaxwellCommandAsync(conn1, character, ["float", "-25"]);
        Assert.Contains("float=0", floatReply);

        string musicVol = await serverMod.HandleMaxwellCommandAsync(conn1, character, ["music", "vol", "2.5"]);
        Assert.Contains("volume=100%", musicVol);

        string danceReply = await serverMod.HandleMaxwellCommandAsync(conn1, character, ["dance"]);
        Assert.Contains("dance #1", danceReply);

        // Area transition on new connection respawns Maxwell automatically and grants record 8558
        await serverMod.OnPlayerEnteredAsync(conn2, character);
        Assert.Contains(MaxwellProtocol.MtxRecordId, backend.GetGrantedRecords(conn2));
        Assert.Contains(backend.SpawnedPets.Values, p => p.ConnectionId == conn2 && p.EntityId != firstEntity);

        // Unequip via Cosmetics tab (recordId = 0 in slot 18) dismisses Maxwell
        var unequipSync = await serverMod.HandleCosmeticSlotPickedAsync(conn2, character, MaxwellProtocol.PetSlot1, 0);
        Assert.NotNull(unequipSync);
        Assert.False(unequipSync!.IsSummoned);
        Assert.False(unequipSync.MusicPlaying);
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

    private static byte[] BuildSyntheticDat64(int rowCount, int rowStride)
    {
        byte[] data = new byte[4 + rowCount * rowStride + 8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0, 4), rowCount);
        Array.Fill(data, (byte)0xBB, 4 + rowCount * rowStride, 8);
        return data;
    }

    private static string FindCompiledAssembly(string exampleRoot, string subFolder, string fileName)
    {
        foreach (string config in new[] { "Release", "Debug" })
        {
            string candidate = Path.Combine(exampleRoot, subFolder, "bin", config, "net10.0", fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException($"Could not locate compiled '{fileName}' under '{exampleRoot}/{subFolder}/bin/*/net10.0/'.");
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "02-architecture.md")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException("Could not locate NativeMods repository root.");
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(source, file);
            if (rel.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj"))
                continue;
            string target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
