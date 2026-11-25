using LocalExile.Client.Host;
using LocalExile.Client.Host.Native;
using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Addresses;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.Sdk.Protocol;
using LocalExile.Client.ServerBridge;
using ClientFacade = LocalExile.Client.ServerBridge.Client;
using Xunit;

namespace LocalExile.Client.Tests;

public sealed class EndToEndBridgeTests
{
    private sealed record PlantSeedRequest(string SeedName, int PlotIndex);
    private sealed record LifeforceUpdated(int TotalLifeforce, string LastPlanted);

    [Theory]
    [InlineData(NetTransportKind.HostRuleset)]
    [InlineData(NetTransportKind.ChatFallback)]
    public async Task FullBidirectionalHandshakeTypedMessagesAndServerDrivenUi_OverBothTransports(NetTransportKind transport)
    {
        const string connId = "player-conn-1";
        using var simBridge = new SimulatedNativeBridge(ClientBuildId.Build3293d);
        using var clientHost = new ClientHostRuntime(simBridge, new ClientHostOptions
        {
            TransportKind = transport,
            HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId,
        });
        clientHost.Start();

        // Register a .ui layout in the client overlay for server-driven UI testing.
        clientHost.FileOverlayService.RegisterVirtualTextFile("Metadata/UI/Mods/Garden/Stockpile.ui", """
            begin Frame
                id = stockpile;
                begin Label
                    id = status;
                    text = "Waiting...";
                end
                begin Button
                    id = plant_btn;
                    text = "Plant Seed";
                end
            end
            """);

        // Load a client mod onto the host so it shows up in ClientHandshake.InstalledMods.
        LifeforceUpdated? clientReceivedLifeforce = null;
        INetChannel? modNet = null;
        clientHost.Loader.LoadInProcessMod(
            new ClientModManifest("garden", "Harvest Garden", "1.0.0", PayloadHash: "abc123sha"),
            new TestGardenClientMod(ch =>
            {
                modNet = ch;
                ch.On<LifeforceUpdated>(msg => clientReceivedLifeforce = msg);
            }));

        // Wire ServerBridge <-> SimulatedNativeBridge loopback sink.
        var sink = new LoopbackGatewaySink(simBridge);
        var serverBridge = new ClientBridgeHost(sink, new ClientBridgeOptions
        {
            DefaultTransport = transport,
            HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId,
        });
        ClientFacade.Initialize(serverBridge);

        // Wire client-to-server transport delivery into ServerBridge.
        simBridge.OnOutgoingRulesetPacket = (rulesetId, innerPayload) =>
        {
            byte[] body = MessageCodec.EncodeRulesetBody(rulesetId, innerPayload.Span);
            serverBridge.HandleIncomingPacketAsync(connId, IOpcodeRegistry.ClientRulesetOpcode, body).GetAwaiter().GetResult();
        };
        simBridge.OnOutgoingChatLine = line =>
        {
            serverBridge.HandleIncomingChatCommandAsync(connId, line).GetAwaiter().GetResult();
        };

        try
        {
            // 1. Handshake negotiation (Client -> Server -> Client).
            IClientSession? readySession = null;
            serverBridge.ClientReady += s => readySession = s;

            ServerHandshake? serverAck = null;
            clientHost.HandshakeCompleted += hs => serverAck = hs;

            clientHost.SendHandshake();

            Assert.NotNull(readySession);
            Assert.NotNull(serverAck);
            Assert.Same(readySession, ClientFacade.For(connId));
            Assert.True(readySession!.HasNativeHost);
            Assert.Equal(transport, readySession.ActiveTransport);
            Assert.Equal(ClientBuildId.Build3293d, readySession.ClientBuild);
            Assert.Equal(SdkVersion.Current, readySession.ClientSdkVersion);
            Assert.True(readySession.HasClientMod("garden"));
            Assert.True(readySession.HasClientMod("garden", "abc123sha"));
            Assert.False(readySession.HasClientMod("garden", "wrong-hash"));
            Assert.False(readySession.HasClientMod("other-mod"));

            // 2. Typed message round-trip (Client -> Server -> Client).
            PlantSeedRequest? serverReceived = null;
            using IDisposable srvSub = serverBridge.On<PlantSeedRequest>("garden", (session, req) =>
            {
                serverReceived = req;
                session.SendAsync("garden", new LifeforceUpdated(125, req.SeedName)).GetAwaiter().GetResult();
            });

            modNet!.Send(new PlantSeedRequest("Wild Thornfruit", 4));

            Assert.NotNull(serverReceived);
            Assert.Equal("Wild Thornfruit", serverReceived!.SeedName);
            Assert.Equal(4, serverReceived.PlotIndex);
            Assert.NotNull(clientReceivedLifeforce);
            Assert.Equal(125, clientReceivedLifeforce!.TotalLifeforce);
            Assert.Equal("Wild Thornfruit", clientReceivedLifeforce.LastPlanted);

            // 3. Server-driven UI round-trip (Open -> SetText -> OnButtonClick -> Click -> Server updates Label).
            ServerUiWindowHandle winHandle = await readySession.Ui.OpenAsync("Metadata/UI/Mods/Garden/Stockpile.ui", "stockpile");
            await winHandle.SetTextAsync("status", "Ready to plant!");

            IUiWindow? clientWin = clientHost.Ui.GetWindow("stockpile");
            Assert.NotNull(clientWin);
            Assert.Equal("Ready to plant!", clientWin!.Require<ILabel>("status").Text);

            int serverClickCount = 0;
            using IDisposable clickSub = winHandle.OnButtonClick("plant_btn", session =>
            {
                serverClickCount++;
                session.Ui.SetTextAsync("stockpile", "status", $"Planted #{serverClickCount}!").GetAwaiter().GetResult();
            });

            // Simulate user clicking the native button on the client.
            clientWin.Require<IButton>("plant_btn").Click();

            Assert.Equal(1, serverClickCount);
            Assert.Equal("Planted #1!", clientWin.Require<ILabel>("status").Text);

            // Close window from server.
            await winHandle.CloseAsync();
            Assert.Null(clientHost.Ui.GetWindow("stockpile"));
        }
        finally
        {
            ClientFacade.Reset();
        }
    }

    [Fact]
    public async Task ServerBridge_IgnoresUnrelatedOpcodesForeignRulesetsAndEnforcesMaxRulesetPayload()
    {
        using var simBridge = new SimulatedNativeBridge();
        var sink = new LoopbackGatewaySink(simBridge);
        var serverBridge = new ClientBridgeHost(sink);

        // Wrong opcode returns false so Gateway continues stock dispatch.
        Assert.False(await serverBridge.HandleIncomingPacketAsync("c1", 0x0015, new byte[] { 1, 2, 3 }));

        // Ruleset 0x69 (Harvest) is not NativeMods host ruleset (0xA0) -> returns false.
        byte[] harvestBody = MessageCodec.EncodeRulesetBody(0x69, new byte[] { 1, 2, 3 });
        Assert.False(await serverBridge.HandleIncomingPacketAsync("c1", IOpcodeRegistry.ClientRulesetOpcode, harvestBody));

        // Normal chat command not starting with '/nm env ' returns false.
        Assert.False(await serverBridge.HandleIncomingChatCommandAsync("c1", "/zoom 2.0"));

        // Ruleset payload > 0xFFFF must be rejected by codec (3.29 client protocol violation limit).
        Assert.Throws<ArgumentException>(() => MessageCodec.EncodeRulesetBody(0xA0, new byte[MessageCodec.MaxRulesetPayloadBytes + 1]));

        // Stock client with no handshake degrades gracefully.
        IClientSession stockClient = serverBridge.For("stock-player");
        Assert.False(stockClient.HasNativeHost);
        Assert.False(stockClient.HasClientMod("garden"));
    }

    private sealed class LoopbackGatewaySink(SimulatedNativeBridge simBridge) : IGatewayPacketSink
    {
        public Task SendPacketAsync(string connectionId, ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            if (opcode == IOpcodeRegistry.ServerRulesetOpcode
                && MessageCodec.TryDecodeRulesetBody(payload, out byte rulesetId, out ReadOnlyMemory<byte> inner))
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

    private sealed class TestGardenClientMod(Action<INetChannel> onLoad) : IClientMod
    {
        public void Load(IClientContext context) => onLoad(context.Net);
    }
}
