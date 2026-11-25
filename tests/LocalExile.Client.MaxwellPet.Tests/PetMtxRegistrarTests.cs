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
            if (File.Exists(Path.Combine(dir, "02-architecture.md")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException("Could not locate NativeMods repository root.");
    }
}
