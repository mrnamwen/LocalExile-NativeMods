using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using DualMod.MaxwellPet.Server;
using DualMod.MaxwellPet.Shared;
using LocalExile.Client.Host;
using LocalExile.Client.Host.Native;
using LocalExile.Client.Sdk.Addresses;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.Sdk.Protocol;
using LocalExile.Client.ServerBridge;
using Xunit;

namespace LocalExile.Client.MaxwellPet.Tests;

public sealed class PetMtxRegistrarTests
{
    [Fact]
    public void PoeContentHash_HandlesShortLongAndSpanOverloadsDeterministically()
    {
        Assert.Equal(649897822u, PoeContentHash.MurmurHash2("Metadata/Items/Pets/Kiwi1"));
        Assert.Equal(0xA1EB592Eu, PoeContentHash.MurmurHash2("Metadata/Items/Pets/Mods/MaxwellPet"));
        Assert.Equal(0x1F3AB11Au, PoeContentHash.MurmurHash2("Metadata/Pet/Mods/Maxwell/MaxwellPet"));
        Assert.Equal(0xAE20, PoeContentHash.FoldHash16(0x1F3AB11Au));

        string longPath = "Metadata/Pet/Mods/Maxwell/" + new string('A', 300);
        byte[] ascii = System.Text.Encoding.ASCII.GetBytes(longPath);
        Assert.Equal(PoeContentHash.MurmurHash2(ascii), PoeContentHash.MurmurHash2(longPath));
    }

    [Fact]
    public void CustomPetMtxDefinition_RejectsReplacingStockRowsAndInvalidPetPaths()
    {
        var baseDef = MaxwellServerMod.MaxwellPetDefinition;
        baseDef.Validate();

        Assert.Throws<ArgumentOutOfRangeException>(() => (baseDef with { MtxTypesRowIndex = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (baseDef with { MtxTypeGameSpecificRowIndex = 4712 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (baseDef with { PetRowIndex = 515 }).Validate());
        Assert.Throws<ArgumentException>(() => (baseDef with { PetMetadataPath = "Metadata/Pets/Mods/Maxwell/MaxwellPet" }).Validate());
        Assert.Throws<ArgumentException>(() => (baseDef with { PetMetadataPath = "Metadata/Pet/Mods/Maxwell/MaxwellPet.ot" }).Validate());
    }

    [Fact]
    public void LocalExilePetMtxRegistrar_AppendsToLiveCatalogsIdempotentlyAndCleansUpOnDispose()
    {
        var fakeMtx = new FakeMtxCatalog();
        var fakeEffects = new FakeCosmeticEffects();
        var logs = new List<string>();

        var registrar = new LocalExilePetMtxRegistrar();
        registrar.Register(MaxwellServerMod.MaxwellPetDefinition);

        // First apply inserts Maxwell without touching the stock Kiwi entry
        int first = registrar.ApplyToLiveCatalogs(fakeMtx, fakeEffects, logs.Add);
        Assert.Equal(1, first);
        Assert.Equal(2, fakeMtx.OwnedCount);
        Assert.True(fakeMtx.ContainsRecord(5731u)); // Stock Kiwi intact
        Assert.True(fakeMtx.ContainsRecord(MaxwellProtocol.MtxRecordId));
        Assert.True(fakeEffects.ContainsEffect(MaxwellProtocol.MtxTypeHash));
        Assert.Equal(MaxwellProtocol.PetMetadataPath, fakeEffects.GetPetType(MaxwellProtocol.MtxTypeHash));
        Assert.Single(logs);

        // Second apply is idempotent (no duplicates!)
        int second = registrar.ApplyToLiveCatalogs(fakeMtx, fakeEffects, logs.Add);
        Assert.Equal(0, second);
        Assert.Equal(2, fakeMtx.OwnedCount);

        // Dispose removes only our appended Maxwell entry; stock Kiwi remains untouched
        registrar.Dispose();
        Assert.Equal(1, fakeMtx.OwnedCount);
        Assert.True(fakeMtx.ContainsRecord(5731u));
        Assert.False(fakeMtx.ContainsRecord(MaxwellProtocol.MtxRecordId));
        Assert.False(fakeEffects.ContainsEffect(MaxwellProtocol.MtxTypeHash));
    }

    [Fact]
    public void EncodeRecordUpsertPacket_MatchesExact29BytePoE329WireFormat()
    {
        byte[] packet = LocalExilePetMtxRegistrar.EncodeRecordUpsertPacket(MaxwellServerMod.MaxwellPetDefinition, justReceived: true);

        Assert.Equal(29, packet.Length);
        Assert.Equal((ushort)0x0204, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(0, 2)));
        Assert.Equal(0, packet[2]); // isStore
        Assert.Equal(0, packet[3]); // isGuild
        Assert.Equal(0, packet[4]); // Flags8
        Assert.Equal(MaxwellProtocol.MtxTypeHash, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(5, 4)));
        Assert.Equal(MaxwellProtocol.MtxRecordId, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(9, 4)));
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(13, 8)));
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(21, 2))); // Name WStr("")
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(23, 2))); // Description WStr("")
        Assert.Equal(0, packet[25]); // Flags9
        Assert.Equal(0, packet[26]); // no variation list
        Assert.Equal(0, packet[27]); // no variant
        Assert.Equal(1, packet[28]); // justReceived
    }

    [Fact]
    public async Task CosmeticsMenuEquipAndUnequip_ObservedViaRegistrar_SyncsClientAudioAndUiWithoutDoubleSpawning()
    {
        using var simBridge = new SimulatedNativeBridge(ClientBuildId.Build3293d);
        using var clientHost = new ClientHostRuntime(simBridge, new ClientHostOptions
        {
            TransportKind = NetTransportKind.HostRuleset,
            HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId
        });
        clientHost.Start();

        // Register MaxwellPanel.ui overlay + load client mod directly for focused Cosmetics K-menu integration test
        string repoRoot = FindRepoRoot();
        string uiPath = Path.Combine(repoRoot, "examples", "DualMod.MaxwellPet", "client", "Metadata", "UI", "Mods", "Maxwell", "MaxwellPanel.ui");
        clientHost.FileOverlayService.RegisterVirtualTextFile(MaxwellProtocol.PanelUiPath, File.ReadAllText(uiPath));

        string clientDir = Path.Combine(repoRoot, "examples", "DualMod.MaxwellPet", "Client", "bin", "Release", "net10.0");
        string clientDll = Path.Combine(clientDir, "maxwell.Client.dll");
        if (!File.Exists(clientDll))
            clientDll = Path.Combine(repoRoot, "examples", "DualMod.MaxwellPet", "Client", "bin", "Debug", "net10.0", "maxwell.Client.dll");

        string tempModDir = Path.Combine(Path.GetTempPath(), "nm-maxwell-kmenu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempModDir);
        try
        {
            File.Copy(Path.Combine(repoRoot, "examples", "DualMod.MaxwellPet", "mod.json"), Path.Combine(tempModDir, "mod.json"));
            File.Copy(clientDll, Path.Combine(tempModDir, "maxwell.Client.dll"));

            clientHost.Loader.LoadModFromDirectory(tempModDir);

            const string connId = "kmenu-conn";
            const string character = "CosmeticsExile";

            var serverBridge = new ClientBridgeHost(new LoopbackSink(simBridge), new ClientBridgeOptions
            {
                DefaultTransport = NetTransportKind.HostRuleset,
                HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId
            });
            simBridge.OnOutgoingRulesetPacket = (rulesetId, inner) =>
            {
                byte[] body = MessageCodec.EncodeRulesetBody(rulesetId, inner.Span);
                serverBridge.HandleIncomingPacketAsync(connId, IOpcodeRegistry.ClientRulesetOpcode, body).GetAwaiter().GetResult();
            };

            var worldBackend = new SimulatedMaxwellWorldBackend();
            using var registrar = new LocalExilePetMtxRegistrar();
            using var serverMod = new MaxwellServerMod(serverBridge, worldBackend, registrar);
            serverMod.Attach();

            clientHost.SendHandshake();
            await serverMod.HandleMaxwellCommandAsync(connId, character, []);

            IUiWindow? panel = clientHost.Ui.GetWindow(MaxwellProtocol.PanelWindowId);
            Assert.NotNull(panel);
            Assert.Equal(0, clientHost.Audio.ActiveChannelCount);

            // Simulate stock LocalExile Cosmetics (K) menu equipping Maxwell in Pet Slot 1 (18),
            // where CosmeticsFeature.SpawnPets spawned entity 0x7777 on side +1.
            var fakeWorn = new List<FakeEquipped>
            {
                new(CustomPetMtxDefinition.PetSlot1, MaxwellProtocol.MtxRecordId, MaxwellProtocol.MtxTypeHash)
            };
            var fakePets = new Dictionary<uint, FakeSpawnedPet>
            {
                [0x7777u] = new(Side: 1)
            };

            var equipEvents = registrar.ObserveLiveGatewayPlayer(connId, character, fakeWorn, fakePets);
            var equipEvt = Assert.Single(equipEvents);
            Assert.True(equipEvt.IsEquipped);
            Assert.Equal(CustomPetMtxDefinition.PetSlot1, equipEvt.PetSlot);
            Assert.Equal(0x7777u, equipEvt.PetEntityId);

            // Must NOT double-spawn through worldBackend (CosmeticsFeature owns the entity),
            // MUST start looping Stock Market audio and update MaxwellPanel.ui!
            Assert.Empty(worldBackend.SpawnedPets);
            Assert.Equal(1, clientHost.Audio.ActiveChannelCount);
            Assert.Equal("Dismiss Maxwell", panel!.Require<IButton>("summon_button").Text);
            Assert.Contains("Cosmetics Pet Slot 1", panel.Require<ILabel>("status_label").Text);

            // Subsequent ticks with unchanged state emit ZERO duplicate events
            Assert.Empty(registrar.ObserveLiveGatewayPlayer(connId, character, fakeWorn, fakePets));

            // Player replaces Maxwell with stock Kiwi (record 5731) in K menu -> Maxwell unequipped -> music stops!
            var kiwiWorn = new List<FakeEquipped>
            {
                new(CustomPetMtxDefinition.PetSlot1, 5731u, 649897822u)
            };
            var unequipEvents = registrar.ObserveLiveGatewayPlayer(connId, character, kiwiWorn, fakePets);
            var unequipEvt = Assert.Single(unequipEvents);
            Assert.False(unequipEvt.IsEquipped);
            Assert.Equal(0u, unequipEvt.PetEntityId);
            Assert.Equal(0, clientHost.Audio.ActiveChannelCount);
            Assert.Equal("Summon Maxwell", panel.Require<IButton>("summon_button").Text);

            // Summoning via /maxwell while K menu has Kiwi must NOT be dismissed by subsequent AreaTick observations
            await serverMod.HandleMaxwellCommandAsync(connId, character, ["summon"]);
            Assert.Single(worldBackend.SpawnedPets);
            Assert.Equal(1, clientHost.Audio.ActiveChannelCount);
            Assert.Empty(registrar.ObserveLiveGatewayPlayer(connId, character, kiwiWorn, fakePets));
            Assert.Equal(1, clientHost.Audio.ActiveChannelCount);
        }
        finally
        {
            await clientHost.Loader.UnloadModAsync(MaxwellProtocol.ModId);
            if (Directory.Exists(tempModDir))
                Directory.Delete(tempModDir, recursive: true);
        }
    }

    [Fact]
    public async Task CosmeticsEquip_ZoneChangeReHandshake_AndCosmeticsUnequip_StaysNativeNoDuplicateAudioAndStopsCleanly()
    {
        using var simBridge = new SimulatedNativeBridge(ClientBuildId.Build3293d);
        using var clientHost = new ClientHostRuntime(simBridge, new ClientHostOptions
        {
            TransportKind = NetTransportKind.HostRuleset,
            HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId
        });
        clientHost.Start();

        string tempModDir = StageClientModForTest(clientHost);
        try
        {
            clientHost.Loader.LoadModFromDirectory(tempModDir);

            const string lioneyeConn = "127.0.0.1:41001#1";
            const string coastConn = "127.0.0.1:41002#1";
            const string character = "ZoneTraveler";
            string activeConnId = lioneyeConn;

            var serverBridge = new ClientBridgeHost(new LoopbackSink(simBridge), new ClientBridgeOptions
            {
                DefaultTransport = NetTransportKind.HostRuleset,
                HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId
            });
            var worldBackend = new SimulatedMaxwellWorldBackend();
            using var registrar = new LocalExilePetMtxRegistrar();
            using var serverMod = new MaxwellServerMod(serverBridge, worldBackend, registrar);
            serverMod.Attach();

            simBridge.OnOutgoingRulesetPacket = (rulesetId, inner) =>
            {
                serverMod.BindConnection(activeConnId, character);
                byte[] body = MessageCodec.EncodeRulesetBody(rulesetId, inner.Span);
                serverBridge.HandleIncomingPacketAsync(activeConnId, IOpcodeRegistry.ClientRulesetOpcode, body).GetAwaiter().GetResult();
            };

            // 1. Lioneye's Watch: enter area + handshake + equip Maxwell in Cosmetics (K) Pet Slot 2
            await serverMod.OnPlayerEnteredAsync(lioneyeConn, character);
            clientHost.SendHandshake();
            Assert.True(serverBridge.For(lioneyeConn).HasNativeHost);

            var wornSlot2 = new List<FakeEquipped>
            {
                new(CustomPetMtxDefinition.PetSlot2, MaxwellProtocol.MtxRecordId, MaxwellProtocol.MtxTypeHash)
            };
            var petsLioneye = new Dictionary<uint, FakeSpawnedPet>
            {
                [0x4000031u] = new(Side: -1)
            };

            registrar.ObserveLiveGatewayPlayer(lioneyeConn, character, wornSlot2, petsLioneye);
            Assert.Equal(1, clientHost.Audio.ActiveChannelCount);
            Assert.True(simBridge.TryGetSimulatedAudioChannel(1, out string path, out bool isFmod, out float vol, out bool loop, out bool active));
            Assert.Equal(MaxwellProtocol.MusicOggPath, path);
            Assert.False(isFmod);
            Assert.True(loop);
            Assert.True(active);
            Assert.Equal(MaxwellProtocol.DefaultMusicVolume, vol, precision: 2);

            // Repeat sync / entity respawn in same area must NOT spawn a duplicate overlapping audio handle
            var petsLioneyeRespawn = new Dictionary<uint, FakeSpawnedPet>
            {
                [0x4000032u] = new(Side: -1)
            };
            registrar.ObserveLiveGatewayPlayer(lioneyeConn, character, wornSlot2, petsLioneyeRespawn);
            Assert.Equal(1, clientHost.Audio.ActiveChannelCount);
            Assert.False(simBridge.TryGetSimulatedAudioChannel(2, out _, out _, out _, out _, out _));

            // 2. Zone change: Lioneye's Watch -> The Coast (new TCP connection coastConn)
            registrar.ClearConnection(lioneyeConn);
            serverMod.OnPlayerLeft(lioneyeConn);
            serverBridge.RemoveSession(lioneyeConn);
            activeConnId = coastConn;

            // PlayerEntered runs on coastConn BEFORE client re-handshake arrives
            var petsCoast = new Dictionary<uint, FakeSpawnedPet>
            {
                [0x4000099u] = new(Side: -1)
            };
            await serverMod.OnPlayerEnteredAsync(coastConn, character);
            registrar.ObserveLiveGatewayPlayer(coastConn, character, wornSlot2, petsCoast);
            Assert.False(serverBridge.For(coastConn).HasNativeHost);

            // Client detects new instance connection and re-sends ClientHandshake -> ClientReady pushes sync
            clientHost.SendHandshake();
            Assert.True(serverBridge.For(coastConn).HasNativeHost);
            Assert.True(serverBridge.For(coastConn).HasClientMod(MaxwellProtocol.ModId));

            // /maxwell in The Coast MUST use native panel (NO Chat Fallback!)
            string coastPanelReply = await serverMod.HandleMaxwellCommandAsync(coastConn, character, []);
            Assert.Contains("Opened Maxwell control panel", coastPanelReply);
            Assert.DoesNotContain("Chat Fallback", coastPanelReply);

            // Still strictly ONE active audio handle across the zone change (zero overlapping channels!)
            Assert.Equal(1, clientHost.Audio.ActiveChannelCount);
            Assert.True(simBridge.IsAudioPlaying(1));
            Assert.False(simBridge.TryGetSimulatedAudioChannel(2, out _, out _, out _, out _, out _));

            // 3. Unequip Maxwell in Cosmetics (K) in The Coast -> MUST clean up Maxwell AND stop Stock Market audio!
            var emptyWorn = new List<FakeEquipped>();
            var emptyPets = new Dictionary<uint, FakeSpawnedPet>();
            var unequipEvents = registrar.ObserveLiveGatewayPlayer(coastConn, character, emptyWorn, emptyPets);

            var unequipEvt = Assert.Single(unequipEvents);
            Assert.False(unequipEvt.IsEquipped);
            Assert.Equal(0u, unequipEvt.PetEntityId);

            var finalSync = serverMod.BuildSync(character, "verify");
            Assert.False(finalSync.IsSummoned);
            Assert.False(finalSync.MusicPlaying);
            Assert.Equal(0u, finalSync.PetEntityId);
            Assert.Empty(worldBackend.SpawnedPets);
            Assert.Equal(0, clientHost.Audio.ActiveChannelCount);
            Assert.False(simBridge.IsAudioPlaying(1));

            IUiWindow? panel = clientHost.Ui.GetWindow(MaxwellProtocol.PanelWindowId);
            Assert.NotNull(panel);
            Assert.Equal("Summon Maxwell", panel!.Require<IButton>("summon_button").Text);
            Assert.Equal("Play Music", panel.Require<IButton>("music_play_button").Text);
        }
        finally
        {
            await clientHost.Loader.UnloadModAsync(MaxwellProtocol.ModId);
            if (Directory.Exists(tempModDir))
                Directory.Delete(tempModDir, recursive: true);
        }
    }

    [Fact]
    public async Task ZoneChangeUnequipBeforeHandshake_AndDisconnectCleanup_AlwaysStopsStockMarketAudio()
    {
        using var simBridge = new SimulatedNativeBridge(ClientBuildId.Build3293d);
        using var clientHost = new ClientHostRuntime(simBridge, new ClientHostOptions
        {
            TransportKind = NetTransportKind.HostRuleset,
            HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId
        });
        clientHost.Start();

        string tempModDir = StageClientModForTest(clientHost);
        try
        {
            clientHost.Loader.LoadModFromDirectory(tempModDir);

            const string connA = "127.0.0.1:42001#1";
            const string connB = "127.0.0.1:42002#1";
            const string connC = "127.0.0.1:42003#1";
            const string character = "AudioCleanupExile";
            string activeConnId = connA;

            var serverBridge = new ClientBridgeHost(new LoopbackSink(simBridge), new ClientBridgeOptions
            {
                DefaultTransport = NetTransportKind.HostRuleset,
                HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId
            });
            var worldBackend = new SimulatedMaxwellWorldBackend();
            using var registrar = new LocalExilePetMtxRegistrar();
            using var serverMod = new MaxwellServerMod(serverBridge, worldBackend, registrar);
            serverMod.Attach();

            simBridge.OnOutgoingRulesetPacket = (rulesetId, inner) =>
            {
                serverMod.BindConnection(activeConnId, character);
                byte[] body = MessageCodec.EncodeRulesetBody(rulesetId, inner.Span);
                serverBridge.HandleIncomingPacketAsync(activeConnId, IOpcodeRegistry.ClientRulesetOpcode, body).GetAwaiter().GetResult();
            };

            // Equip Maxwell on connA -> music playing on handle 1
            await serverMod.OnPlayerEnteredAsync(connA, character);
            clientHost.SendHandshake();
            var wornSlot1 = new List<FakeEquipped>
            {
                new(CustomPetMtxDefinition.PetSlot1, MaxwellProtocol.MtxRecordId, MaxwellProtocol.MtxTypeHash)
            };
            var petsA = new Dictionary<uint, FakeSpawnedPet> { [0x6001u] = new(Side: 1) };
            registrar.ObserveLiveGatewayPlayer(connA, character, wornSlot1, petsA);
            Assert.Equal(1, clientHost.Audio.ActiveChannelCount);
            Assert.True(simBridge.IsAudioPlaying(1));

            // Zone to connB where Maxwell is NO LONGER worn (reconciled BEFORE client re-handshake arrives!)
            registrar.ClearConnection(connA);
            serverMod.OnPlayerLeft(connA);
            serverBridge.RemoveSession(connA);
            activeConnId = connB;

            await serverMod.OnPlayerEnteredAsync(connB, character);
            registrar.ObserveLiveGatewayPlayer(connB, character, new List<FakeEquipped>(), new Dictionary<uint, FakeSpawnedPet>());
            await serverMod.ReconcileCosmeticAndPetStateAsync(
                connB,
                character,
                registrar.IsEquipped(connB, MaxwellServerMod.MaxwellPetDefinition.MtxTypeHash),
                []);

            // Re-handshake arrives on connB -> ClientReady pushes dismissed state -> stops handle 1 immediately!
            clientHost.SendHandshake();
            Assert.Equal(0, clientHost.Audio.ActiveChannelCount);
            Assert.False(simBridge.IsAudioPlaying(1));

            // Re-equip Maxwell on connB -> starts handle 2; then full client disconnect stops handle 2 immediately!
            var petsB = new Dictionary<uint, FakeSpawnedPet> { [0x6002u] = new(Side: 1) };
            registrar.ObserveLiveGatewayPlayer(connB, character, wornSlot1, petsB);
            Assert.Equal(1, clientHost.Audio.ActiveChannelCount);
            Assert.True(simBridge.IsAudioPlaying(2));

            clientHost.NotifyDisconnected("returned to character select");
            Assert.Equal(0, clientHost.Audio.ActiveChannelCount);
            Assert.False(simBridge.IsAudioPlaying(2));

            // Reconnect on connC while Maxwell is still equipped -> handshake arrives BEFORE PlayerEntered -> resumes cleanly!
            registrar.ClearConnection(connB);
            serverMod.OnPlayerLeft(connB);
            serverBridge.RemoveSession(connB);
            activeConnId = connC;

            clientHost.SendHandshake();
            await serverMod.OnPlayerEnteredAsync(connC, character);
            registrar.ObserveLiveGatewayPlayer(connC, character, wornSlot1, new Dictionary<uint, FakeSpawnedPet> { [0x6003u] = new(Side: 1) });
            Assert.Equal(1, clientHost.Audio.ActiveChannelCount);
            Assert.True(simBridge.IsAudioPlaying(3));
        }
        finally
        {
            await clientHost.Loader.UnloadModAsync(MaxwellProtocol.ModId);
            if (Directory.Exists(tempModDir))
                Directory.Delete(tempModDir, recursive: true);
        }
    }

    [Fact]
    public async Task CommandDismissAndCosmeticsSlotClear_NeverResurrectMaxwellOrLeakAudio()
    {
        using var simBridge = new SimulatedNativeBridge(ClientBuildId.Build3293d);
        using var clientHost = new ClientHostRuntime(simBridge, new ClientHostOptions
        {
            TransportKind = NetTransportKind.HostRuleset,
            HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId
        });
        clientHost.Start();

        string tempModDir = StageClientModForTest(clientHost);
        try
        {
            clientHost.Loader.LoadModFromDirectory(tempModDir);

            const string connId = "mixed-mode-conn";
            const string character = "MixedModeExile";

            var serverBridge = new ClientBridgeHost(new LoopbackSink(simBridge), new ClientBridgeOptions
            {
                DefaultTransport = NetTransportKind.HostRuleset,
                HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId
            });
            var worldBackend = new SimulatedMaxwellWorldBackend();
            using var registrar = new LocalExilePetMtxRegistrar();
            using var serverMod = new MaxwellServerMod(serverBridge, worldBackend, registrar);
            serverMod.Attach();

            simBridge.OnOutgoingRulesetPacket = (rulesetId, inner) =>
            {
                serverMod.BindConnection(connId, character);
                byte[] body = MessageCodec.EncodeRulesetBody(rulesetId, inner.Span);
                serverBridge.HandleIncomingPacketAsync(connId, IOpcodeRegistry.ClientRulesetOpcode, body).GetAwaiter().GetResult();
            };

            await serverMod.OnPlayerEnteredAsync(connId, character);
            clientHost.SendHandshake();

            // 1. Equip Maxwell in Cosmetics (K), then run /maxwell dismiss while Cosmetics slot is still worn.
            //    Subsequent AreaTick observations must NOT falsely re-equip Maxwell or restart audio!
            var wornSlot1 = new List<FakeEquipped>
            {
                new(CustomPetMtxDefinition.PetSlot1, MaxwellProtocol.MtxRecordId, MaxwellProtocol.MtxTypeHash)
            };
            registrar.ObserveLiveGatewayPlayer(connId, character, wornSlot1, new Dictionary<uint, FakeSpawnedPet> { [0x7001u] = new(Side: 1) });
            Assert.Equal(1, clientHost.Audio.ActiveChannelCount);

            await serverMod.HandleMaxwellCommandAsync(connId, character, ["dismiss"]);
            Assert.Equal(0, clientHost.Audio.ActiveChannelCount);

            // AreaTick sees wornSlot1 still set, but player.Pets no longer has 0x7001 -> must NOT re-trigger equip!
            var tickEvents = registrar.ObserveLiveGatewayPlayer(connId, character, wornSlot1, new Dictionary<uint, FakeSpawnedPet>());
            Assert.Empty(tickEvents);
            Assert.Equal(0, clientHost.Audio.ActiveChannelCount);

            // Unequipping in Cosmetics (K) afterwards still transitions cleanly to unequipped
            var afterUnequip = registrar.ObserveLiveGatewayPlayer(connId, character, new List<FakeEquipped>(), new Dictionary<uint, FakeSpawnedPet>());
            Assert.False(Assert.Single(afterUnequip).IsEquipped);
            Assert.Equal(0, clientHost.Audio.ActiveChannelCount);

            // 2. Summon via /maxwell summon, then user clears Pet Slot in Cosmetics (K) (which removes all player.Pets).
            //    ReconcileCosmeticAndPetStateAsync must detect the removed entity and stop Stock Market audio!
            await serverMod.HandleMaxwellCommandAsync(connId, character, ["summon"]);
            Assert.Single(worldBackend.SpawnedPets);
            Assert.Equal(1, clientHost.Audio.ActiveChannelCount);

            var reconciled = await serverMod.ReconcileCosmeticAndPetStateAsync(
                connId,
                character,
                isCosmeticEquipped: false,
                livePetEntityIds: []);
            Assert.NotNull(reconciled);
            Assert.False(reconciled!.IsSummoned);
            Assert.False(reconciled.MusicPlaying);
            Assert.Empty(worldBackend.SpawnedPets);
            Assert.Equal(0, clientHost.Audio.ActiveChannelCount);
        }
        finally
        {
            await clientHost.Loader.UnloadModAsync(MaxwellProtocol.ModId);
            if (Directory.Exists(tempModDir))
                Directory.Delete(tempModDir, recursive: true);
        }
    }

    private static string StageClientModForTest(ClientHostRuntime clientHost)
    {
        string repoRoot = FindRepoRoot();
        string uiPath = Path.Combine(repoRoot, "examples", "DualMod.MaxwellPet", "client", "Metadata", "UI", "Mods", "Maxwell", "MaxwellPanel.ui");
        clientHost.FileOverlayService.RegisterVirtualTextFile(MaxwellProtocol.PanelUiPath, File.ReadAllText(uiPath));

        string clientDir = Path.Combine(repoRoot, "examples", "DualMod.MaxwellPet", "Client", "bin", "Release", "net10.0");
        string clientDll = Path.Combine(clientDir, "maxwell.Client.dll");
        if (!File.Exists(clientDll))
            clientDll = Path.Combine(repoRoot, "examples", "DualMod.MaxwellPet", "Client", "bin", "Debug", "net10.0", "maxwell.Client.dll");

        string tempModDir = Path.Combine(Path.GetTempPath(), "nm-maxwell-reg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempModDir);
        File.Copy(Path.Combine(repoRoot, "examples", "DualMod.MaxwellPet", "mod.json"), Path.Combine(tempModDir, "mod.json"));
        File.Copy(clientDll, Path.Combine(tempModDir, "maxwell.Client.dll"));
        return tempModDir;
    }

    private sealed class LoopbackSink(SimulatedNativeBridge simBridge) : IGatewayPacketSink
    {
        public Task SendPacketAsync(string connectionId, ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            if (opcode == IOpcodeRegistry.ServerRulesetOpcode &&
                MessageCodec.TryDecodeRulesetBody(payload, out byte rulesetId, out ReadOnlyMemory<byte> inner))
            {
                simBridge.SimulateIncomingRulesetPacket(rulesetId, inner);
            }
            return Task.CompletedTask;
        }

        public Task SendChatReplyAsync(string connectionId, string text, CancellationToken cancellationToken = default)
        {
            simBridge.SimulateIncomingChat("Server", text);
            return Task.CompletedTask;
        }
    }

    // Mirrors LocalExile's MtxCatalog, CosmeticEffects, Equipped, and SpawnedPet internal layout
    private sealed record FakeRecord(uint MtxTypeHash, uint Id, string Name = "", string Description = "", ulong Qword = 0, byte Flags8 = 0, byte Flags9 = 0);

    private sealed class FakeMtxCatalog
    {
        private readonly List<FakeRecord> _owned = [new FakeRecord(649897822u, 5731u)];
        private readonly Dictionary<uint, FakeRecord> _byId = new() { [5731u] = new FakeRecord(649897822u, 5731u) };

        public int OwnedCount => _owned.Count;
        public bool ContainsRecord(uint id) => _byId.ContainsKey(id) && _owned.Any(r => r.Id == id);
    }

    private sealed record FakeEffect(ushort Visual, ushort WeaponEffect, IReadOnlyList<(uint Stat, int Value)> Stats, int Pet = 0)
    {
        public string? PetType { get; init; }
        public IReadOnlyList<string>? PetComponents { get; init; }
        public object? PetStates { get; init; }
    }

    private sealed record FakeSlot(string Name, byte? ModelSlot);

    private sealed class FakeCosmeticEffects
    {
        [SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Mirrors LocalExile's CosmeticEffects._data field inspected by LocalExilePetMtxRegistrar.")]
        private (Dictionary<uint, FakeEffect> Effects, Dictionary<byte, FakeSlot> Slots) _data =
            (new Dictionary<uint, FakeEffect> { [649897822u] = new(0, 0, [], 1) { PetType = "Metadata/Pet/Kiwi/Basic" } },
             new Dictionary<byte, FakeSlot> { [18] = new("Pet", null), [19] = new("Pet", null) });

        public bool ContainsEffect(uint hash) => _data.Effects.ContainsKey(hash);
        public string? GetPetType(uint hash) => _data.Effects.TryGetValue(hash, out var fx) ? fx.PetType : null;
    }

    private sealed record FakeEquipped(byte Slot, uint Id, uint MtxTypeHash);
    private sealed record FakeSpawnedPet(int Side);

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "NativeMods.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException("Could not locate NativeMods repository root.");
    }
}
