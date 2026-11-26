using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using LocalExile.Client.Host;
using LocalExile.Client.Host.Native;
using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Addresses;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.Sdk.Protocol;
using LocalExile.Client.ServerBridge;
using Xunit;

namespace LocalExile.Client.Tests;

public sealed class ServerBridge020Tests
{
    private sealed record ProbeReq(string Action, int Count);
    private sealed record ProbeResp(string Status, int Value);

    private static readonly CustomCurrencyItemDefinition TestEmptyOrb = new()
    {
        BaseItemTypeId = "Metadata/Items/Currency/CurrencyTestEmptyOrb",
        DisplayName = "Orb of Siphoning",
        VisualIdentityId = "TestEmptyOrbVisual",
        VisualIdentityHash16 = 0x7A11,
        BaseItemTypesRowIndex = 5560,
        ItemVisualIdentityRowIndex = 15600,
        CurrencyItemsRowIndex = 2060,
        MaxStackSize = 20,
        SupportsStoredMods = false,
        Description = "Test empty orb",
    };

    private static readonly CustomCurrencyItemDefinition TestChargedOrb = new()
    {
        BaseItemTypeId = "Metadata/Items/Currency/CurrencyTestChargedOrb",
        DisplayName = "Charged Orb of Siphoning",
        VisualIdentityId = "TestChargedOrbVisual",
        VisualIdentityHash16 = 0x7A12,
        BaseItemTypesRowIndex = 5561,
        ItemVisualIdentityRowIndex = 15601,
        CurrencyItemsRowIndex = 2061,
        MaxStackSize = 1,
        SupportsStoredMods = true,
        Description = "Test charged orb",
    };

    private static string? FindLocalExileRepoRoot()
    {
        string candidate = "/home/nam/Git/LocalExile";
        if (File.Exists(Path.Combine(candidate, "src", "GameServer.Gateway", "bin", "Release", "net10.0", "GameServer.Gateway.dll")) &&
            Directory.Exists(Path.Combine(candidate, "data")))
        {
            return candidate;
        }

        return null;
    }

    private static byte[] MakeClientHandshakeEnvelope(string[]? mods = null)
    {
        var installed = (mods ?? ["siphon", "maxwell"])
            .Select(m => new InstalledClientModInfo(m, "0.2.0", $"hash-{m}"))
            .ToArray();
        var hs = new ClientHandshake(
            ExtenderVersion: SdkVersion.Current,
            SdkVersion: SdkVersion.Current,
            Build: ClientBuildId.Build3293d,
            BuildVersion: "3.29.3d",
            ProtocolVersion: SdkVersion.ProtocolVersion,
            InstalledMods: installed,
            Capabilities: ["ruleset.0x2c9"]);
        ModEnvelope env = MessageCodec.CreateTypedEnvelope(MessageCodec.SystemChannel, hs, sequence: 1);
        return MessageCodec.EncodeRulesetBody(IRulesetRegistry.DefaultHostChannelRulesetId, MessageCodec.EncodeEnvelope(env));
    }

    private static byte[] MakeClientEnvelopePacket<T>(string channel, T payload, byte rulesetId = IRulesetRegistry.DefaultHostChannelRulesetId)
    {
        ModEnvelope env = MessageCodec.CreateTypedEnvelope(channel, payload, sequence: 2);
        return MessageCodec.EncodeRulesetBody(rulesetId, MessageCodec.EncodeEnvelope(env));
    }

    // -------------------------------------------------------------------------
    // 1. Async Handlers, Overloads, Fault Isolation, Deadlock Avoidance, Dispose
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AsyncAndSyncOverloads_OnAndClientReady_WorkWithoutAmbiguityAndSupportReadinessQueries()
    {
        var sink = new RecordingBatchSink();
        using var bridge = new ClientBridgeHost(sink, new ClientBridgeOptions
        {
            DefaultChannel = "siphon",
            HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId,
        });

        int syncReady = 0;
        int asyncReady1 = 0;
        int asyncReady2 = 0;
        int eventReadyAsync = 0;
        using IDisposable r1 = bridge.OnClientReady(_ => syncReady++);
        using IDisposable r2 = bridge.OnClientReady(async _ => { await Task.Yield(); asyncReady1++; });
        using IDisposable r3 = bridge.OnClientReady(async (_, ct) => { await Task.Delay(1, ct); asyncReady2++; });
        bridge.ClientReadyAsync += async (_, ct) => { await Task.Yield(); eventReadyAsync++; };

        int channelSync = 0;
        int channelAsync1 = 0;
        int channelAsync2 = 0;
        int wildcardAsync = 0;
        using IDisposable h1 = bridge.On<ProbeReq>("siphon", (_, req) => channelSync += req.Count);
        using IDisposable h2 = bridge.On<ProbeReq>("siphon", async (_, req) => { await Task.Yield(); channelAsync1 += req.Count; });
        using IDisposable h3 = bridge.On<ProbeReq>("siphon", async (session, req, ct) =>
        {
            await Task.Yield();
            channelAsync2 += req.Count;
            await session.SendAsync(new ProbeResp("ok", req.Count * 10), ct);
        });
        using IDisposable h4 = bridge.On<ProbeReq>(async (_, req, ct) =>
        {
            await Task.Yield();
            wildcardAsync += req.Count;
        });

        Assert.False(bridge.IsClientReady("c1"));
        Assert.False(bridge.HasMod("c1", "siphon"));
        Assert.False(await bridge.TrySendAsync("missing-conn", new ProbeResp("ignored", 0)));

        Assert.True(await bridge.HandleIncomingPacketAsync("c1", IOpcodeRegistry.ClientRulesetOpcode, MakeClientHandshakeEnvelope(["siphon", "maxwell"])));
        Assert.True(bridge.IsClientReady("c1"));
        Assert.True(bridge.HasMod("c1", "siphon"));
        Assert.True(bridge.HasMod("c1", "siphon", "hash-siphon"));
        Assert.False(bridge.HasMod("c1", "siphon", "wrong-hash"));
        Assert.False(bridge.HasMod("c1", "nonexistent"));
        Assert.Equal(1, syncReady);
        Assert.Equal(1, asyncReady1);
        Assert.Equal(1, asyncReady2);
        Assert.Equal(1, eventReadyAsync);

        IClientSession s1 = bridge.For("c1");
        Assert.Equal(["siphon", "maxwell"], s1.ClientMods);

        int sessionAsyncCount = 0;
        using IDisposable sh1 = s1.On<ProbeReq>("siphon", async (_, req, _) => { await Task.Yield(); sessionAsyncCount += req.Count; });

        Assert.True(await bridge.HandleIncomingPacketAsync("c1", IOpcodeRegistry.ClientRulesetOpcode, MakeClientEnvelopePacket("siphon", new ProbeReq("ping", 3))));
        Assert.Equal(3, channelSync);
        Assert.Equal(3, channelAsync1);
        Assert.Equal(3, channelAsync2);
        Assert.Equal(3, wildcardAsync);
        Assert.Equal(3, sessionAsyncCount);

        // Verify TrySendAsync succeeds on ready session & channel-less SendAsync used DefaultChannel ("siphon")
        Assert.True(await bridge.TrySendAsync("c1", new ProbeResp("second", 42)));
        Assert.True(sink.Packets.Count >= 3); // ServerHandshake + 2 ProbeResp
        ModEnvelope decodedResp = DecodeSentRulesetEnvelope(sink.Packets[^1].Payload);
        Assert.Equal("siphon", decodedResp.Channel);
        Assert.Equal(nameof(ProbeResp), decodedResp.MessageType);

        // Disposing subscriptions stops further invocations cleanly
        h1.Dispose();
        h2.Dispose();
        h3.Dispose();
        h4.Dispose();
        sh1.Dispose();
        Assert.True(await bridge.HandleIncomingPacketAsync("c1", IOpcodeRegistry.ClientRulesetOpcode, MakeClientEnvelopePacket("siphon", new ProbeReq("ping", 5))));
        Assert.Equal(3, channelSync);
        Assert.Equal(3, wildcardAsync);
    }

    [Fact]
    public async Task HandlerFaultIsolation_IsolatesExceptionsAcrossAllHandlerTypes_AndEmitsDiagnostics()
    {
        var sink = new RecordingBatchSink();
        using var bridge = new ClientBridgeHost(sink);
        var errors = new List<(string Source, Exception Ex)>();
        bridge.HandlerError += (src, ex) => errors.Add((src, ex));

        int siblingReady = 0;
        bridge.ClientReady += _ => throw new InvalidOperationException("sync ready boom");
        bridge.ClientReadyAsync += (_, _) => throw new InvalidOperationException("async event ready boom");
        using var r1 = bridge.OnClientReady(async (_, _) => { await Task.Yield(); throw new InvalidOperationException("on ready boom"); });
        using var r2 = bridge.OnClientReady(_ => siblingReady++);

        int siblingMsg = 0;
        using var m1 = bridge.On<ProbeReq>("ch", (_, _) => throw new InvalidOperationException("global msg boom"));
        using var m2 = bridge.On<ProbeReq>("ch", (_, req) => siblingMsg += req.Count);

        IClientSession session = bridge.For("c-err");
        int siblingSessionMsg = 0;
        using var sm1 = session.On<ProbeReq>("ch", async (_, _, _) => { await Task.Yield(); throw new InvalidOperationException("session msg boom"); });
        using var sm2 = session.On<ProbeReq>("ch", (_, req) => siblingSessionMsg += req.Count);

        int siblingUi = 0;
        ServerUiWindowHandle win = await session.Ui.OpenAsync("Metadata/UI/Test.ui", "win_err");
        using var u1 = win.OnButtonClick("ok_btn", async _ => { await Task.Yield(); throw new InvalidOperationException("ui boom"); });
        using var u2 = win.OnButtonClick("ok_btn", _ => siblingUi++);

        await bridge.HandleIncomingPacketAsync("c-err", IOpcodeRegistry.ClientRulesetOpcode, MakeClientHandshakeEnvelope());
        await bridge.HandleIncomingPacketAsync("c-err", IOpcodeRegistry.ClientRulesetOpcode, MakeClientEnvelopePacket("ch", new ProbeReq("go", 7)));
        await bridge.HandleIncomingEnvelopeAsync("c-err", MessageCodec.CreateTypedEnvelope(
            MessageCodec.SystemChannel,
            new ClientUiEventNotification("win_err", "ok_btn", "Clicked")));

        Assert.Equal(1, siblingReady);
        Assert.Equal(7, siblingMsg);
        Assert.Equal(7, siblingSessionMsg);
        Assert.Equal(1, siblingUi);
        Assert.Equal(6, errors.Count);
    }

    [Fact]
    public async Task OnButtonClick_And_BatchDispose_DoNotDeadlockOnSingleThreadedSynchronizationContext()
    {
        using var singleThreadCtx = new SingleThreadQueueSyncContext();
        await singleThreadCtx.RunAsync(async () =>
        {
            var yieldingSink = new AsyncYieldingSink();
            using var bridge = new ClientBridgeHost(yieldingSink);
            IClientSession session = bridge.For("sync-ctx");
            ServerUiWindowHandle win = await session.Ui.OpenAsync("Metadata/UI/Test.ui", "w1");

            // Synchronous OnButtonClick used to call .GetAwaiter().GetResult() and deadlock on a single-threaded SynchronizationContext!
            using IDisposable clickSub = win.OnButtonClick("btn", _ => { });
            using IDisposable asyncSub = await win.OnButtonClickAsync("btn2", async (_, _) => await Task.Yield());

            // Synchronous Batch.Dispose() must also never block/deadlock
            using (IClientCommandBatch batch = win.BeginBatch())
            {
                await win.SetTextAsync("lbl", "no deadlock");
            }
        }).WaitAsync(TimeSpan.FromSeconds(5));
    }

    // -------------------------------------------------------------------------
    // 2. ServerUiWindowHandle.Closed Across All 4 Close Paths + Auto-Cleanup
    // -------------------------------------------------------------------------

    [Fact]
    public async Task WindowClosed_FiresStrictlyOnceAcrossAllPaths_AndCleansUpSubscriptions()
    {
        var sink = new RecordingBatchSink();
        using var bridge = new ClientBridgeHost(sink);
        IClientSession session = bridge.For("c-win");

        // Path 1: Explicit CloseAsync()
        ServerUiWindowHandle w1 = await session.Ui.OpenAsync("Metadata/UI/W1.ui", "w1");
        int w1ClosedCount = 0;
        int w1OnClosedAsync = 0;
        int w1ClickCount = 0;
        w1.Closed += () => w1ClosedCount++;
        using var _ = w1.OnClosed(async () => { await Task.Yield(); w1OnClosedAsync++; });
        w1.OnButtonClick("btn", _ => w1ClickCount++);
        Assert.True(w1.IsOpen);

        await w1.CloseAsync();
        await w1.CloseAsync(); // Second close must NOT double-fire
        Assert.False(w1.IsOpen);
        Assert.Equal(1, w1ClosedCount);
        Assert.Equal(1, w1OnClosedAsync);

        // Bound subscription was auto-disposed on close
        await bridge.HandleIncomingEnvelopeAsync("c-win", MessageCodec.CreateTypedEnvelope(
            MessageCodec.SystemChannel,
            new ClientUiEventNotification("w1", "btn", "Clicked")));
        Assert.Equal(0, w1ClickCount);

        // Path 2: Replacement OpenAsync with same windowId
        ServerUiWindowHandle w2First = await session.Ui.OpenAsync("Metadata/UI/W2.ui", "w2");
        int w2Closed = 0;
        w2First.Closed += () => w2Closed++;
        ServerUiWindowHandle w2Second = await session.Ui.OpenAsync("Metadata/UI/W2.ui", "w2");
        Assert.False(w2First.IsOpen);
        Assert.True(w2Second.IsOpen);
        Assert.Equal(1, w2Closed);

        // Path 3: Client-side ClientUiEventNotification("Closed")
        int w2SecondClosed = 0;
        w2Second.Closed += () => w2SecondClosed++;
        await bridge.HandleIncomingEnvelopeAsync("c-win", MessageCodec.CreateTypedEnvelope(
            MessageCodec.SystemChannel,
            new ClientUiEventNotification("w2", string.Empty, "Closed")));
        Assert.False(w2Second.IsOpen);
        Assert.Equal(1, w2SecondClosed);
        await w2Second.CloseAsync();
        Assert.Equal(1, w2SecondClosed);

        // Path 4a: RemoveSessionAsync
        ServerUiWindowHandle w3 = await session.Ui.OpenAsync("Metadata/UI/W3.ui", "w3");
        int w3Closed = 0;
        w3.Closed += () => w3Closed++;
        Assert.True(await bridge.RemoveSessionAsync("c-win"));
        Assert.False(w3.IsOpen);
        Assert.Equal(1, w3Closed);

        // Path 4b: Host Dispose()
        IClientSession s2 = bridge.For("c-win-2");
        ServerUiWindowHandle w4 = await s2.Ui.OpenAsync("Metadata/UI/W4.ui", "w4");
        int w4Closed = 0;
        w4.Closed += () => w4Closed++;
        bridge.Dispose();
        Assert.False(w4.IsOpen);
        Assert.Equal(1, w4Closed);
    }

    [Fact]
    public async Task HeldSession_And_ClosedWindow_AfterRemoveSessionAndDispose_NeverLeakOrphanWindowsOrEmitWirePackets()
    {
        var sink = new RecordingBatchSink();
        using var bridge = new ClientBridgeHost(sink);

        // 1. Handshake & held IClientSession across RemoveSessionAsync
        await bridge.HandleIncomingPacketAsync("orphan-1", IOpcodeRegistry.ClientRulesetOpcode, MakeClientHandshakeEnvelope());
        IClientSession heldSession = bridge.For("orphan-1");
        ServerUiWindowHandle preOpen = await heldSession.Ui.OpenAsync("Metadata/UI/Pre.ui", "pre_win");
        int preClosedCount = 0;
        int preOnClosedCount = 0;
        preOpen.Closed += () => preClosedCount++;
        using var preOnClosedSub = preOpen.OnClosed(() => preOnClosedCount++);

        // Also verify closed window on an active session does NOT emit SubscribeEvent
        ServerUiWindowHandle singleClosed = await heldSession.Ui.OpenAsync("Metadata/UI/Single.ui", "single_win");
        await singleClosed.CloseAsync();
        Assert.False(singleClosed.IsOpen);

        sink.Clear();
        using (singleClosed.OnButtonClick("btn1", _ => { }))
        using (await singleClosed.OnButtonClickAsync("btn2", _ => { }))
        using (singleClosed.OnControlEvent("tb1", "TextChanged", (_, _) => { }))
        using (await singleClosed.OnControlEventAsync("tb2", "Submitted", (_, _) => { }))
        using (singleClosed.OnClosed(() => { }))
        {
            Assert.Empty(sink.Packets);
            Assert.Empty(sink.Batches);
        }

        Assert.True(await bridge.RemoveSessionAsync("orphan-1"));
        Assert.False(preOpen.IsOpen);
        Assert.Equal(1, preClosedCount);
        Assert.Equal(1, preOnClosedCount);

        sink.Clear();

        // Calling OpenAsync on a held disconnected IClientSession must return a closed handle and emit 0 packets
        ServerUiWindowHandle orphan = await heldSession.Ui.OpenAsync("Metadata/UI/Orphan.ui", "orphan_win");
        Assert.False(orphan.IsOpen);
        Assert.Empty(sink.Packets);
        Assert.Empty(sink.Batches);

        // Subscriptions, sends, and batches on held closed session / orphan handle must emit 0 wire traffic
        using (orphan.OnButtonClick("btn", _ => { }))
        using (await orphan.OnButtonClickAsync("btn", _ => { }))
        using (orphan.OnControlEvent("tb", "TextChanged", (_, _) => { }))
        using (await orphan.OnControlEventAsync("tb", "Submitted", (_, _) => { }))
        using (orphan.OnClosed(() => { }))
        using (heldSession.Ui.OnButtonClick("orphan_win", "btn", _ => { }))
        using (await heldSession.Ui.OnControlEventAsync("orphan_win", "tb", "TextChanged", (_, _) => { }))
        using (IClientCommandBatch closedBatch = heldSession.BeginBatch())
        {
            await heldSession.SendAsync("siphon", new ProbeResp("ignored", 1));
            await heldSession.Ui.SetTextAsync("orphan_win", "lbl", "ignored");
            await closedBatch.FlushAsync();
            Assert.Equal(0, closedBatch.Count);
        }

        await heldSession.FlushAsync();
        Assert.False(await bridge.TrySendAsync("orphan-1", "siphon", new ProbeResp("try-ignored", 2)));
        Assert.Empty(sink.Packets);
        Assert.Empty(sink.Batches);

        // 2. Held IClientSession across ClientBridgeHost.Dispose()
        await bridge.HandleIncomingPacketAsync("orphan-2", IOpcodeRegistry.ClientRulesetOpcode, MakeClientHandshakeEnvelope());
        IClientSession heldSession2 = bridge.For("orphan-2");
        ServerUiWindowHandle preOpen2 = await heldSession2.Ui.OpenAsync("Metadata/UI/Pre2.ui", "pre_win_2");
        int preClosed2 = 0;
        preOpen2.Closed += () => preClosed2++;

        bridge.Dispose();
        Assert.False(preOpen2.IsOpen);
        Assert.Equal(1, preClosed2);

        sink.Clear();
        ServerUiWindowHandle orphan2 = await heldSession2.Ui.OpenAsync("Metadata/UI/Orphan2.ui", "orphan_win_2");
        Assert.False(orphan2.IsOpen);
        Assert.False(await bridge.TrySendAsync("orphan-2", "siphon", new ProbeResp("disposed-ignored", 3)));
        await heldSession2.SendAsync("siphon", new ProbeResp("disposed-ignored-2", 4));
        Assert.Empty(sink.Packets);
        Assert.Empty(sink.Batches);
    }

    // -------------------------------------------------------------------------
    // 3. Command & UI Batching + Safe Coalescing + Nested Discard + Races
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CommandAndUiBatching_CoalescesSafelyWithinSegments_SupportsNestingAndDiscard()
    {
        var sink = new RecordingBatchSink();
        using var bridge = new ClientBridgeHost(sink);
        IClientSession session = bridge.For("c-batch");

        ServerUiWindowHandle win = await session.Ui.OpenAsync("Metadata/UI/Panel.ui", "panel");
        sink.Clear();

        await win.BatchAsync(async (w, ct) =>
        {
            await w.SetTextAsync("lbl", "step 1", ct);
            await w.SetVisibleAsync("btn", false, ct);
            await w.SetTextAsync("lbl", "step 2", ct); // supersedes "step 1"
            await w.SetVisibleAsync("btn", true, ct);  // supersedes false
            await w.SetPropertyAsync("lbl", "color", "red", ct);
            await w.SetPropertyAsync("lbl", "color", "gold", ct); // supersedes "red"

            // Nested batch throwing must discard ONLY inner commands, leaving outer commands intact
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await session.BatchAsync(async s =>
                {
                    await s.Ui.SetTextAsync("panel", "lbl", "SHOULD_BE_DISCARDED");
                    throw new InvalidOperationException("abort inner");
                }, ct);
            });

            // Boundary: ReloadWindow prevents coalescing across it
            await w.ReloadAsync(ct);
            await w.SetTextAsync("lbl", "after reload", ct);
            await session.SendAsync("mod", new ProbeResp("mid", 99), cancellationToken: ct);
            await w.SetTextAsync("lbl", "after msg", ct);
        });

        Assert.Single(sink.Batches);
        IReadOnlyList<GatewayBatchItem> items = sink.Batches[0];
        var commands = items.Select(i => DecodeSentRulesetEnvelope(i.Payload)).ToList();

        Assert.Equal(7, commands.Count);
        Assert.Equal(ServerUiCommandKind.SetText, MessageCodec.DeserializePayload<ServerUiCommand>(commands[0]).Kind);
        Assert.Equal("step 2", MessageCodec.DeserializePayload<ServerUiCommand>(commands[0]).Value);
        Assert.Equal(true, MessageCodec.DeserializePayload<ServerUiCommand>(commands[1]).BoolValue);
        Assert.Equal("gold", MessageCodec.DeserializePayload<ServerUiCommand>(commands[2]).Value);
        Assert.Equal(ServerUiCommandKind.ReloadWindow, MessageCodec.DeserializePayload<ServerUiCommand>(commands[3]).Kind);
        Assert.Equal("after reload", MessageCodec.DeserializePayload<ServerUiCommand>(commands[4]).Value);
        Assert.Equal(nameof(ProbeResp), commands[5].MessageType);
        Assert.Equal("after msg", MessageCodec.DeserializePayload<ServerUiCommand>(commands[6]).Value);
    }

    [Fact]
    public async Task ConcurrentSendsDuringDisconnect_DoNotThrowInvalidOperationException()
    {
        var sink = new RecordingBatchSink();
        using var bridge = new ClientBridgeHost(sink);
        await bridge.HandleIncomingPacketAsync("race-conn", IOpcodeRegistry.ClientRulesetOpcode, MakeClientHandshakeEnvelope());

        Task[] senders = Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
        {
            await bridge.SendAsync("race-conn", "siphon", new ProbeResp("race", i));
            await bridge.TrySendAsync("race-conn", "siphon", new ProbeResp("try-race", i));
        })).ToArray();

        Task remover = Task.Run(async () =>
        {
            await Task.Yield();
            await bridge.RemoveSessionAsync("race-conn");
        });

        await Task.WhenAll([.. senders, remover]);
    }

    // -------------------------------------------------------------------------
    // 4. LocalExileGatewayHost, 0x02CA Re-Framing, Multi-Mod Bus, GatewaySink
    // -------------------------------------------------------------------------

    [Fact]
    public async Task LocalExileGatewayHost_CreateAndAttach_Reframes0x02CA_SupportsMultiModBusAndLifecycle()
    {
        string domainKey = $"test-bus-{Guid.NewGuid():N}";
        var fakeAdapter = new FakeGatewayAdapter();
        var player1 = new FakeGatewayPlayer("10.0.0.1:1234", 7, "ExileOne");

        var modCtxA = new FakeModContext("mod-alpha");
        var modCtxB = new FakeModContext("mod-beta");

        var enteredEvents = new List<string>();
        var leavingEvents = new List<(string ConnId, string Reason)>();
        var readyEvents = new List<string>();
        var chatMessages = new List<string>();
        var cosmetics = new List<uint>();

        using LocalExileGatewayHost hostA = LocalExileGatewayHost.Create(modCtxA, b => b
            .WithAdapter(fakeAdapter)
            .WithBus(attach: true, domainKey: domainKey)
            .OnPlayerEntered(ctx => { enteredEvents.Add(ctx.ConnectionId); return Task.CompletedTask; })
            .OnPlayerLeaving((ctx, reason) => { leavingEvents.Add((ctx.ConnectionId, reason)); return Task.CompletedTask; })
            .OnClientReady(ctx => { readyEvents.Add(ctx.CharacterName); return Task.CompletedTask; })
            .OnClientMessage(msg => { chatMessages.Add(msg.Text); return Task.FromResult(false); })
            .OnCosmeticChanged(evt => { cosmetics.Add(evt.MtxTypeHash); return Task.CompletedTask; }));

        var bridgeB = new ClientBridgeHost(new LocalExileGatewaySink(conn => conn == "10.0.0.1:1234#7" ? player1 : null, fakeAdapter),
            new ClientBridgeOptions { HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId });
        using IDisposable attachB = LocalExileGatewayHost.Attach(modCtxB, bridgeB, out GatewayBridgeRegistration regB, new LocalExileGatewayHostOptions
        {
            Adapter = fakeAdapter,
            BusDomainKey = domainKey,
            UseCoreClientChannel = false, // Exercise GameEvents.Packet(0x02CA) fallback alongside hostA's Client.OnRuleset!
        });

        Assert.Equal(2, GatewayBridgeBus.GetParticipantCount(domainKey));
        Assert.NotNull(regB);

        int aReceived = 0;
        int bReceived = 0;
        using var subA = hostA.Bridge.On<ProbeReq>((_, req) => aReceived += req.Count);
        using var subB = bridgeB.On<ProbeReq>((_, req) => bReceived += req.Count);

        await fakeAdapter.FirePlayerEnteredAsync(player1);
        Assert.True(hostA.TryGetPlayer("10.0.0.1:1234#7", out object? resolvedPlayer));
        Assert.Same(player1, resolvedPlayer);

        // Send unframed ClientHandshake inner payload via global::Client.OnRuleset path (hostA) -> re-framed & fanned out to BOTH A and B!
        ModEnvelope hsEnv = MessageCodec.CreateTypedEnvelope(MessageCodec.SystemChannel, new ClientHandshake(
            SdkVersion.Current, SdkVersion.Current, ClientBuildId.Build3293d, "3.29.3d", SdkVersion.ProtocolVersion,
            [new InstalledClientModInfo("mod-alpha", "0.2.0")], ["ruleset.0x2c9"]), sequence: 1);
        Assert.True(await fakeAdapter.SimulateCoreClientRulesetAsync(player1, IRulesetRegistry.DefaultHostChannelRulesetId, MessageCodec.EncodeEnvelope(hsEnv)));

        Assert.True(hostA.Bridge.IsClientReady("10.0.0.1:1234#7"));
        Assert.True(bridgeB.IsClientReady("10.0.0.1:1234#7"));
        // Deduplicated ServerHandshake: only 1 outbound 0x02C9 packet sent!
        Assert.Single(fakeAdapter.SentPackets);

        // Send ProbeReq via GameEvents.Packet(0x02CA) fallback (with 2-byte opcode prefix) -> fans out to BOTH A and B!
        byte[] framedBody = MakeClientEnvelopePacket("mod-alpha", new ProbeReq("ping", 4));
        byte[] full02ca = new byte[2 + framedBody.Length];
        BinaryPrimitives.WriteUInt16BigEndian(full02ca, IOpcodeRegistry.ClientRulesetOpcode);
        framedBody.CopyTo(full02ca, 2);
        Assert.True(await fakeAdapter.SimulatePacketAsync(player1, IOpcodeRegistry.ClientRulesetOpcode, full02ca));

        Assert.Equal(4, aReceived);
        Assert.Equal(4, bReceived);

        // Lifecycle & chat/cosmetic forwarding
        await fakeAdapter.FireClientReadyAsync(player1);
        await fakeAdapter.FireClientMessageAsync(player1, "hello world", [], isCommand: false);
        await fakeAdapter.FireCosmeticChangedAsync(player1, 18, 99001, 0xABCD1234u);
        Assert.Equal(["ExileOne"], readyEvents);
        Assert.Equal(["hello world"], chatMessages);
        Assert.Equal([0xABCD1234u], cosmetics);

        // LocalExileGatewaySink silently no-ops when player is closed or missing
        await hostA.Sink.SendPacketAsync("unknown-conn", 0x02C9, new byte[] { 1, 2 });
        player1.Closed = true;
        await hostA.Sink.SendChatReplyAsync("10.0.0.1:1234#7", "ignored");
        await fakeAdapter.FirePlayerLeavingAsync(player1, "disconnect");
        Assert.False(hostA.Bridge.IsClientReady("10.0.0.1:1234#7"));
        Assert.False(hostA.TryGetPlayer("10.0.0.1:1234#7", out _));
        Assert.Single(leavingEvents);

        attachB.Dispose();
        hostA.Dispose();
        Assert.Equal(0, GatewayBridgeBus.GetParticipantCount(domainKey));
        GatewayBridgeBus.Clear(domainKey);
    }

    // -------------------------------------------------------------------------
    // 5. PortableItem.CustomName & GroupInventoryDiffs Pure Tests
    // -------------------------------------------------------------------------

    [Fact]
    public void PortableItem_CustomName_InspectAndReplaceAffix_SetNormalizeAndClearCleanly()
    {
        var mod1 = new PortableItemMod(ItemModSectionKind.Explicit, "IncreasedLife8", 101, "Vigorous", "+84 to maximum Life", true, false, "Life", 8, [84]);
        var mod2 = new PortableItemMod(ItemModSectionKind.Explicit, "ColdResistance4", 102, "of the Yeti", "+36% to Cold Resistance", false, true, "ColdRes", 4, [36]);
        var replacement = new StoredAffixPayload("FireResistance5", [42], "+42% to Fire Resistance", false, "FireRes", 103, "of the Volcano", true, 5);

        var plate = new PortableItem(
            ItemId: 10,
            InventoryId: 1,
            TypeHash: 0x11223344,
            ItemName: "Doom Hide",
            BaseType: "Astral Plate",
            ItemClass: "Body Armour",
            Rarity: ItemRarityKind.Rare,
            ItemLevel: 86,
            Identified: true,
            Corrupted: false,
            Mirrored: false,
            FracturedCount: 0,
            StackSize: 1,
            Mods: [mod1, mod2],
            CustomName: "Siphoned Plate");

        Assert.Equal("Siphoned Plate", LocalExileItemAffixService.Inspect(plate).ItemName);
        Assert.Equal("Astral Plate", LocalExileItemAffixService.Inspect(plate with { Identified = false, ItemName = "Astral Plate" }).ItemName);

        AffixReplaceResult setRes = LocalExileItemAffixService.ReplaceAffix(plate, 1, replacement, customName: "★ Soul Plate ★");
        Assert.True(setRes.Success);
        Assert.Equal("★ Soul Plate ★", setRes.UpdatedTargetItem!.CustomName);
        Assert.Equal("★ Soul Plate ★", setRes.UpdatedTargetView!.ItemName);

        AffixReplaceResult whitespaceRes = LocalExileItemAffixService.ReplaceAffix(setRes.UpdatedTargetItem, 1, replacement, customName: "   ");
        Assert.True(whitespaceRes.Success);
        Assert.Null(whitespaceRes.UpdatedTargetItem!.CustomName);

        AffixReplaceResult clearRes = LocalExileItemAffixService.ReplaceAffix(setRes.UpdatedTargetItem, 1, replacement, customName: null, clearCustomName: true);
        Assert.True(clearRes.Success);
        Assert.Null(clearRes.UpdatedTargetItem!.CustomName);
    }

    [Fact]
    public void GroupInventoryDiffs_MergesSameContainer_AndSplitsCrossContainer()
    {
        var changes = new[]
        {
            new GatewayInventoryChange(null, 0, [10u], ["item10"]),
            new GatewayInventoryChange(null, 0, [11u], ["item11", "item12"]),
            new GatewayInventoryChange(2, 26, [200u], ["stashItem"]),
        };

        IReadOnlyList<GatewayInventoryDiffBucket> buckets = LocalExileGatewayTransactions.GroupInventoryDiffs(changes);
        Assert.Equal(2, buckets.Count);

        Assert.Null(buckets[0].Tab);
        Assert.Equal(0, buckets[0].Row);
        Assert.Equal([10u, 11u], buckets[0].RemovedIds);
        Assert.Equal(3, buckets[0].AddedItems.Count);

        Assert.Equal(2, buckets[1].Tab);
        Assert.Equal(26, buckets[1].Row);
        Assert.Equal([200u], buckets[1].RemovedIds);
        Assert.Single(buckets[1].AddedItems);
    }

    [Fact]
    public async Task IsWornRow_SecondaryEquipmentSlots54_78_102_AreRejectedWithoutMutation()
    {
        var affixService = new LocalExileItemAffixService();
        var fakeCatalog = new FakeGatewayItemCatalog(TestEmptyOrb);
        var fakeInv = new FakeGatewayPlayerInventories();

        fakeInv.Add(0, 100u, new FakeGatewayItem(TestEmptyOrb.TypeHash, stackSize: 3, identified: true));
        uint[] secondaryWornRows = [54, 78, 102];
        for (int i = 0; i < secondaryWornRows.Length; i++)
        {
            byte row = (byte)secondaryWornRows[i];
            uint itemId = 200u + (uint)i;
            fakeInv.Add(row, itemId, new FakeGatewayItem(0x11223344u, stackSize: 1, identified: true));

            GatewayItemLocation? loc = LocalExileGatewayTransactions.LocateGatewayItem(fakeInv, null, (uint)row + 1, itemId);
            Assert.NotNull(loc);
            Assert.True(loc!.IsWorn, $"Expected row {row} to be marked as worn.");

            GatewayStealResult stealRes = await affixService.StealAffixOnGatewayItem(
                player: null,
                fakeCatalog,
                fakeInv,
                accountStash: null,
                currencyInventoryId: 1u,
                currencyItemId: 100u,
                targetInventoryId: (uint)row + 1,
                targetItemId: itemId,
                affixIndex: 0,
                chargedCurrencyDef: TestChargedOrb,
                sendPackets: false);
            Assert.False(stealRes.Success);
            Assert.Contains("Unequip", stealRes.Error, StringComparison.OrdinalIgnoreCase);

            GatewayReplaceResult replaceRes = await affixService.ReplaceAffixOnGatewayItem(
                player: null,
                fakeCatalog,
                fakeInv,
                accountStash: null,
                currencyInventoryId: 1u,
                currencyItemId: 100u,
                targetInventoryId: (uint)row + 1,
                targetItemId: itemId,
                replaceIndex: 0,
                sendPackets: false);
            Assert.False(replaceRes.Success);
            Assert.Contains("Unequip", replaceRes.Error, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal((ushort)3, fakeInv.GetItem(100u)!.StackSize);
    }

    // -------------------------------------------------------------------------
    // 6. Isolated Real LocalExile M4 Part 1 Assembly Compatibility & Live Tx Tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task LiveM4GatewayAssemblies_ItemCustomName_GatewayHost_And_ItemTransactions_WorkEndToEnd()
    {
        string? repoRoot = FindLocalExileRepoRoot();
        if (repoRoot is null)
        {
            return; // Gracefully skipped only if external LocalExile repo is absent on machine.
        }

        string gatewayBinDir = Path.Combine(repoRoot, "src", "GameServer.Gateway", "bin", "Release", "net10.0");
        string gatewayDll = Path.Combine(gatewayBinDir, "GameServer.Gateway.dll");
        string gameDll = Path.Combine(repoRoot, "Modules", "Core", "Game", "bin", "game.dll");
        string dataDir = Path.Combine(repoRoot, "data");
        string tempDbDir = Path.Combine(AppContext.BaseDirectory, "sb020-m4-tmp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDbDir);
        string? prevClientDb = Environment.GetEnvironmentVariable("Game_CLIENT_DB");
        string? prevWorldDb = Environment.GetEnvironmentVariable("Game_WORLD_DB");
        string? prevSqlDir = Environment.GetEnvironmentVariable("Game_SQL_DIR");
        Environment.SetEnvironmentVariable("Game_CLIENT_DB", Path.Combine(dataDir, "client.db"));
        Environment.SetEnvironmentVariable("Game_WORLD_DB", Path.Combine(dataDir, "world.db"));
        Environment.SetEnvironmentVariable("Game_SQL_DIR", Path.Combine(repoRoot, "sql"));

        var alc = new IsolatedGatewayLoadContext(gatewayDll);
        try
        {
            Assembly coreAsm = alc.LoadFromAssemblyPath(Path.Combine(gatewayBinDir, "GameServer.Core.dll"));
            Assembly moddingAsm = alc.LoadFromAssemblyPath(Path.Combine(gatewayBinDir, "GameServer.Modding.dll"));
            Assembly gatewayAsm = alc.LoadFromAssemblyPath(gatewayDll);
            if (File.Exists(gameDll))
            {
                alc.LoadFromAssemblyPath(gameDll);
            }

            Type itemCatalogType = gatewayAsm.GetType("ItemCatalog")!;
            Type modCatalogType = gatewayAsm.GetType("ModCatalog")!;
            Type playerInvType = gatewayAsm.GetType("PlayerInventories")!;
            Type itemType = coreAsm.GetType("GameServer.Core.Protocol.Instance.Item")!;
            Type itemModType = coreAsm.GetType("GameServer.Core.Protocol.Instance.ItemMod")!;
            Type modSectionType = coreAsm.GetType("GameServer.Core.Protocol.Instance.ModSection")!;
            Type itemRarityType = coreAsm.GetType("GameServer.Core.Protocol.Instance.ItemRarity")!;
            Type savesType = gatewayAsm.GetType("Saves")!;
            Type accountStashType = gatewayAsm.GetType("AccountStash")!;
            Type clientType = gatewayAsm.GetType("Client")!;
            Type playerType = gatewayAsm.GetType("Player")!;
            Type areaLayoutType = gatewayAsm.GetType("AreaLayout")!;
            Type connectionCiphersType = coreAsm.GetType("GameServer.Core.Protocol.Login.ConnectionCiphers")!;

            // 1. Initialize Program.Stats + ClusterJewels and construct real ItemCatalog & ModCatalog from /home/nam/Git/LocalExile/data
            Type programType = gatewayAsm.GetType("Program")!;
            Type statCatalogType = gatewayAsm.GetType("StatCatalog")!;
            object statCatalog = Activator.CreateInstance(statCatalogType)!;
            if (programType.GetField("Stats", BindingFlags.Public | BindingFlags.Static) is { } statsField)
                statsField.SetValue(null, statCatalog);
            else
                programType.GetProperty("Stats", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, statCatalog);
            gatewayAsm.GetType("ClusterJewels")!.GetMethod("LoadGameData")!.Invoke(null, null);
            ConstructorInfo catalogCtor = itemCatalogType.GetConstructors()[0];
            ParameterInfo[] catalogParams = catalogCtor.GetParameters();
            object?[] catalogArgs = new object?[catalogParams.Length];
            catalogArgs[0] = dataDir;
            for (int i = 1; i < catalogParams.Length; i++)
            {
                catalogArgs[i] = catalogParams[i].HasDefaultValue ? catalogParams[i].DefaultValue : null;
            }
            object itemCatalog = catalogCtor.Invoke(catalogArgs);
            object modCatalog = itemCatalogType.GetProperty("Mods")!.GetValue(itemCatalog)!;
            programType.GetField("Items", BindingFlags.Public | BindingFlags.Static)?.SetValue(null, itemCatalog);
            FieldInfo? rollsField = programType.GetField("Rolls", BindingFlags.Public | BindingFlags.Static);
            object? itemRolls = rollsField is not null ? CreateWithDefaults(rollsField.FieldType, itemCatalog, dataDir) : null;
            rollsField?.SetValue(null, itemRolls);
            FieldInfo? benchField = programType.GetField("Bench", BindingFlags.Public | BindingFlags.Static);
            object? bench = benchField is not null ? CreateWithDefaults(benchField.FieldType, itemCatalog, itemRolls) : null;
            benchField?.SetValue(null, bench);
            gatewayAsm.GetType("Quests")!.GetMethod("LoadGameData")!.Invoke(null, null);
            var registrar = new LocalExileCurrencyRegistrar();
            registrar.Register(TestEmptyOrb);
            registrar.Register(TestChargedOrb);
            registrar.ApplyToLiveCatalog(itemCatalog);

            // 2. Verify Item.CustomName (0x80 wire bit + RequiredLevel preservation)
            object plateBase = itemCatalogType.GetMethod("Find", [typeof(string)])!.Invoke(itemCatalog, ["Iron Greaves"])
                ?? throw new InvalidOperationException("Iron Greaves not found in ItemCatalog.");
            uint plateHash = (uint)plateBase.GetType().GetProperty("Hash")!.GetValue(plateBase)!;
            object modLife = modCatalogType.GetMethod("ById", [typeof(string)])!.Invoke(modCatalog, ["IncreasedLife7"])
                ?? throw new InvalidOperationException("IncreasedLife7 not found in ModCatalog.");
            object modCold = modCatalogType.GetMethod("ById", [typeof(string)])!.Invoke(modCatalog, ["ColdResist1"])
                ?? throw new InvalidOperationException("ColdResist1 not found in ModCatalog.");
            object modFire = modCatalogType.GetMethod("ById", [typeof(string)])!.Invoke(modCatalog, ["FireResist1"])
                ?? throw new InvalidOperationException("FireResist1 not found in ModCatalog.");
            ushort lifeHash = (ushort)modLife.GetType().GetProperty("Hash")!.GetValue(modLife)!;
            ushort coldHash = (ushort)modCold.GetType().GetProperty("Hash")!.GetValue(modCold)!;
            ushort fireHash = (ushort)modFire.GetType().GetProperty("Hash")!.GetValue(modFire)!;

            object explicitSec = Enum.ToObject(modSectionType, (byte)5);
            object realPlate = CreateGatewayItem(itemCatalogType, plateBase, itemType, itemRarityType, itemModType,
                rarity: 2, itemLevel: 86, identified: true, requiredLevel: 62,
                mods:
                [
                    Activator.CreateInstance(itemModType, [explicitSec, lifeHash, (IReadOnlyList<int>)[84]])!,
                    Activator.CreateInstance(itemModType, [explicitSec, coldHash, (IReadOnlyList<int>)[36]])!,
                ]);

            byte[] payloadBefore = (byte[])itemType.GetMethod("Payload")!.Invoke(realPlate, null)!;
            object namedPlate = LocalExileItemAffixService.WithGatewayItemCustomName(realPlate, "★ Soul Plate ★");
            Assert.Equal("★ Soul Plate ★", LocalExileItemAffixService.GetGatewayItemCustomName(namedPlate));
            Assert.Equal(62, itemType.GetProperty("RequiredLevel")!.GetValue(namedPlate)); // Preserved when Mods unchanged!
            byte[] payloadNamed = (byte[])itemType.GetMethod("Payload")!.Invoke(namedPlate, null)!;
            Assert.True(payloadNamed.Length > payloadBefore.Length);

            object clearedPlate = LocalExileItemAffixService.WithGatewayItemCustomName(namedPlate, "   ");
            Assert.Null(LocalExileItemAffixService.GetGatewayItemCustomName(clearedPlate));
            byte[] payloadCleared = (byte[])itemType.GetMethod("Payload")!.Invoke(clearedPlate, null)!;
            Assert.Equal(payloadBefore.Length, payloadCleared.Length);

            var affixService = new LocalExileItemAffixService();
            ItemAffixView namedView = affixService.InspectGatewayItem(1, 1, namedPlate, itemCatalog);
            Assert.Equal("★ Soul Plate ★", namedView.ItemName);

            // 3. Open isolated temporary Saves SQLite DB for AccountStash + PlayerInventories tests
            string dbPath = Path.Combine(tempDbDir, "chars.db");
            savesType.GetMethod("Open", [typeof(string)])!.Invoke(null, [dbPath]);
            object inv = Activator.CreateInstance(playerInvType)!;
            object stash = accountStashType.GetMethod("For")!.Invoke(null, ["sb020-acc-" + Guid.NewGuid().ToString("N"), itemCatalog])!;

            // 4. LocateCurrencyStack: main inventory priority + stash non-public vs public tab
            object shapeDelegate = BuildGatewayShapeDelegate(playerInvType, itemCatalogType, itemCatalog, itemType);
            object emptyOrbStack = LocalExileCurrencyRegistrar.CreateLiveItem(itemCatalog, TestEmptyOrb, stackSize: 3)!;

            // Put one stack in tab 0 (non-public), one stack in tab 1 (mark public -> skipped unless opted in)
            MethodInfo stashAddStacked = accountStashType.GetMethod("AddStacked")!;
            stashAddStacked.Invoke(stash, [0, LocalExileCurrencyRegistrar.CreateLiveItem(itemCatalog, TestEmptyOrb, 2)!, shapeDelegate, null]);

            (uint InventoryId, uint ItemId)? stashHit = LocalExileGatewayTransactions.LocateCurrencyStack(inv, stash, TestEmptyOrb.TypeHash);
            Assert.NotNull(stashHit);
            Assert.Equal(0x800u, stashHit!.Value.InventoryId);

            // Grant 2 Empty Orbs to MainInventory -> MainInventory takes priority over stash!
            var sentFrames = new List<(byte[] Frame, string? Label)>();
            Task CaptureSend(object p, byte[] frame, string? label)
            {
                sentFrames.Add((frame, label));
                return Task.CompletedTask;
            }

            object fakePlayer = CreateRealGatewayPlayer(gatewayAsm, coreAsm, out List<byte[]> playerOutbox);
            GatewayGrantResult grantRes = await LocalExileGatewayTransactions.GrantItemsAsync(
                fakePlayer, itemCatalog, inv, TestEmptyOrb, count: 2, customName: "Fresh Orb", requireFullFit: true,
                sendPacket: CaptureSend);
            Assert.True(grantRes.Success);
            Assert.Equal(2, grantRes.GrantedCount);
            Assert.Single(sentFrames);
            Assert.Equal(0x00BF, BinaryPrimitives.ReadUInt16BigEndian(sentFrames[0].Frame));

            // Multi-chunk GrantItemsAsync (count > maxStack) where chunk 1 creates a new stack (Added)
            // and chunk 2 merges into that newly created stack (Changed) must NOT place the newly created stack ID into 0x00BF RemovedIds.
            object inv2 = Activator.CreateInstance(playerInvType)!;
            object? preStack = LocalExileCurrencyRegistrar.CreateLiveItem(itemCatalog, TestEmptyOrb, stackSize: 15);
            Assert.NotNull(preStack);
            object preInvItem = playerInvType.GetMethod("Add", [typeof(byte), typeof(byte), typeof(byte), itemType])!
                .Invoke(inv2, [(byte)0, (byte)2, (byte)0, preStack!])!;
            uint preExistingStackId = (uint)preInvItem.GetType().GetProperty("Id")!.GetValue(preInvItem)!;

            sentFrames.Clear();
            GatewayGrantResult multiChunkGrant = await LocalExileGatewayTransactions.GrantItemsAsync(
                fakePlayer, itemCatalog, inv2, TestEmptyOrb, count: 25, requireFullFit: true, sendPacket: CaptureSend);
            Assert.True(multiChunkGrant.Success, multiChunkGrant.Message);
            Assert.Equal(25, multiChunkGrant.GrantedCount);
            byte[] multiChunk00bf = Assert.Single(sentFrames).Frame;
            Assert.Equal(0x00BF, BinaryPrimitives.ReadUInt16BigEndian(multiChunk00bf));
            uint[] decodedRemovedIds = Decode00BfRemovedIds(multiChunk00bf);
            Assert.Equal([preExistingStackId], decodedRemovedIds);
            Assert.Equal(2, multiChunkGrant.GrantedItemIds.Count);
            sentFrames.Clear();

            (uint InventoryId, uint ItemId)? mainHit = LocalExileGatewayTransactions.LocateCurrencyStack(inv, stash, TestEmptyOrb.TypeHash);
            Assert.NotNull(mainHit);
            Assert.Equal(1u, mainHit!.Value.InventoryId);

            // 5. Add rare target plate + worn helmet + invalid targets into PlayerInventories
            MethodInfo invAdd = playerInvType.GetMethod("Add", [typeof(byte), typeof(byte), typeof(byte), itemType])!;
            object rareInvItem = invAdd.Invoke(inv, [(byte)0, (byte)4, (byte)0, namedPlate])!;
            uint rareId = (uint)rareInvItem.GetType().GetProperty("Id")!.GetValue(rareInvItem)!;

            object wornPlate = invAdd.Invoke(inv, [(byte)1, (byte)0, (byte)0, realPlate])!;
            uint wornId = (uint)wornPlate.GetType().GetProperty("Id")!.GetValue(wornPlate)!;

            GatewayItemLocation? wornLoc = LocalExileGatewayTransactions.LocateGatewayItem(inv, stash, 2u, wornId);
            Assert.NotNull(wornLoc);
            Assert.True(wornLoc!.IsWorn);

            // Invalid steal targets must fail with ZERO mutation!
            GatewayStealResult wornFail = await affixService.StealAffixOnGatewayItem(
                fakePlayer, itemCatalog, inv, stash, mainHit.Value.InventoryId, mainHit.Value.ItemId, 2u, wornId, 0, TestChargedOrb, sendPacket: CaptureSend);
            Assert.False(wornFail.Success);
            Assert.Equal(2, GetGatewayItemStack(inv, 0, mainHit.Value.ItemId)); // Stack untouched!

            object corruptedPlate = CloneWithProp(realPlate, "Corrupted", true);
            object corruptedInv = invAdd.Invoke(inv, [(byte)0, (byte)6, (byte)0, corruptedPlate])!;
            uint corruptedId = (uint)corruptedInv.GetType().GetProperty("Id")!.GetValue(corruptedInv)!;
            GatewayStealResult corruptedFail = await affixService.StealAffixOnGatewayItem(
                fakePlayer, itemCatalog, inv, stash, mainHit.Value.InventoryId, mainHit.Value.ItemId, 1u, corruptedId, 0, TestChargedOrb, sendPacket: CaptureSend);
            Assert.False(corruptedFail.Success);

            // Valid StealAffixAsync: steals index 1 (ColdResist1) from rareId using mainHit (stack 2 -> 1), creates Charged Orb
            sentFrames.Clear();
            GatewayStealResult stealOk = await affixService.StealAffixOnGatewayItem(
                fakePlayer, itemCatalog, inv, stash,
                mainHit.Value.InventoryId, mainHit.Value.ItemId,
                1u, rareId,
                affixIndex: 1,
                chargedCurrencyDef: TestChargedOrb,
                chargedCustomName: "Frozen Charge",
                sendPacket: CaptureSend);
            Assert.True(stealOk.Success, stealOk.Error);
            Assert.Equal("ColdResist1", stealOk.StolenAffix!.ModId);
            Assert.Equal([36], stealOk.StolenAffix.RolledValues);
            Assert.Single(stealOk.UpdatedSourceView!.Affixes);
            Assert.Equal(1, GetGatewayItemStack(inv, 0, mainHit.Value.ItemId)); // Decremented 2 -> 1
            // RequiredLevel invalidated on updated source item because Mods changed
            object updatedSourceItem = LocalExileGatewayTransactions.LocateGatewayItem(inv, stash, 1u, rareId)!.Item;
            Assert.Null(itemType.GetProperty("RequiredLevel")!.GetValue(updatedSourceItem));
            // 0x00BF + 0x0263 packets sent
            Assert.Contains(sentFrames, f => BinaryPrimitives.ReadUInt16BigEndian(f.Frame) == 0x00BF);
            Assert.Contains(sentFrames, f => BinaryPrimitives.ReadUInt16BigEndian(f.Frame) == 0x0263);

            // Additional Tier 2 invalid targets must also fail with zero mutation:
            object mirroredPlate = CloneWithProp(realPlate, "Mirrored", true);
            object mirroredInv = invAdd.Invoke(inv, [(byte)0, (byte)6, (byte)3, mirroredPlate])!;
            uint mirroredId = (uint)mirroredInv.GetType().GetProperty("Id")!.GetValue(mirroredInv)!;
            Assert.False((await affixService.StealAffixOnGatewayItem(
                fakePlayer, itemCatalog, inv, stash, mainHit.Value.InventoryId, mainHit.Value.ItemId, 1u, mirroredId, 0, TestChargedOrb, sendPackets: false)).Success);

            object unidPlate = CloneWithProp(realPlate, "Identified", false);
            object unidInv = invAdd.Invoke(inv, [(byte)0, (byte)8, (byte)3, unidPlate])!;
            uint unidId = (uint)unidInv.GetType().GetProperty("Id")!.GetValue(unidInv)!;
            Assert.False((await affixService.StealAffixOnGatewayItem(
                fakePlayer, itemCatalog, inv, stash, mainHit.Value.InventoryId, mainHit.Value.ItemId, 1u, unidId, 0, TestChargedOrb, sendPackets: false)).Success);

            object normalZeroAffix = CreateGatewayItem(itemCatalogType, plateBase, itemType, itemRarityType, itemModType,
                rarity: 0, itemLevel: 86, identified: true, requiredLevel: 62, mods: []);
            object normalInv = invAdd.Invoke(inv, [(byte)0, (byte)10, (byte)0, normalZeroAffix])!;
            uint normalId = (uint)normalInv.GetType().GetProperty("Id")!.GetValue(normalInv)!;
            Assert.False((await affixService.StealAffixOnGatewayItem(
                fakePlayer, itemCatalog, inv, stash, mainHit.Value.InventoryId, mainHit.Value.ItemId, 1u, normalId, 0, TestChargedOrb, sendPackets: false)).Success);
            Assert.Equal(1, GetGatewayItemStack(inv, 0, mainHit.Value.ItemId)); // Still 1!

            // 6. Test ReplaceAffixAsync: create destination rare with 1 mod, replace with stolen Charged Orb + CustomName
            object destPlate = CreateGatewayItem(itemCatalogType, plateBase, itemType, itemRarityType, itemModType,
                rarity: 2, itemLevel: 86, identified: true, requiredLevel: 62,
                mods: [Activator.CreateInstance(itemModType, [explicitSec, fireHash, (IReadOnlyList<int>)[40]])!]);
            object destInv = invAdd.Invoke(inv, [(byte)0, (byte)8, (byte)0, destPlate])!;
            uint destId = (uint)destInv.GetType().GetProperty("Id")!.GetValue(destInv)!;

            // Duplicate modId rejection before commit (stealOk carries ColdResist1; plate already having ColdResist1 must fail cleanly)
            object coldDupPlate = CreateGatewayItem(itemCatalogType, plateBase, itemType, itemRarityType, itemModType,
                rarity: 2, itemLevel: 86, identified: true, requiredLevel: 62,
                mods:
                [
                    Activator.CreateInstance(itemModType, [explicitSec, lifeHash, (IReadOnlyList<int>)[75]])!,
                    Activator.CreateInstance(itemModType, [explicitSec, coldHash, (IReadOnlyList<int>)[33]])!,
                ]);
            object coldDupInv = invAdd.Invoke(inv, [(byte)0, (byte)10, (byte)6, coldDupPlate])!;
            uint coldDupId = (uint)coldDupInv.GetType().GetProperty("Id")!.GetValue(coldDupInv)!;
            GatewayReplaceResult dupFail = await affixService.ReplaceAffixOnGatewayItem(
                fakePlayer, itemCatalog, inv, stash, 1u, stealOk.CreatedChargedItemId, 1u, coldDupId, replaceIndex: 0, sendPackets: false);
            Assert.False(dupFail.Success);
            Assert.NotNull(LocalExileGatewayTransactions.LocateGatewayItem(inv, stash, 1u, stealOk.CreatedChargedItemId)); // Charged orb NOT consumed!

            // Duplicate group/modId rejection on fractured target before commit
            object fracturedDest = CloneWithProp(destPlate, "FracturedCount", (byte)1);
            object fracturedInv = invAdd.Invoke(inv, [(byte)0, (byte)10, (byte)3, fracturedDest])!;
            uint fracturedId = (uint)fracturedInv.GetType().GetProperty("Id")!.GetValue(fracturedInv)!;
            GatewayReplaceResult fracturedFail = await affixService.ReplaceAffixOnGatewayItem(
                fakePlayer, itemCatalog, inv, stash, 1u, stealOk.CreatedChargedItemId, 1u, fracturedId, replaceIndex: 0, sendPackets: false);
            Assert.False(fracturedFail.Success);
            Assert.NotNull(LocalExileGatewayTransactions.LocateGatewayItem(inv, stash, 1u, stealOk.CreatedChargedItemId)); // Charged orb NOT consumed!

            sentFrames.Clear();
            GatewayReplaceResult replaceOk = await affixService.ReplaceAffixOnGatewayItem(
                fakePlayer, itemCatalog, inv, stash,
                1u, stealOk.CreatedChargedItemId,
                1u, destId,
                replaceIndex: 0,
                customName: "Imbued Plate",
                sendPacket: CaptureSend);
            Assert.True(replaceOk.Success, replaceOk.Error);
            Assert.Equal("ColdResist1", replaceOk.AppliedAffix!.ModId);
            Assert.Equal("Imbued Plate", replaceOk.UpdatedTargetView!.ItemName);
            // Charged orb (stack 1) was fully consumed
            Assert.Null(LocalExileGatewayTransactions.LocateGatewayItem(inv, stash, 1u, stealOk.CreatedChargedItemId));

            // 7. Test LocalExileGatewayHost + ReflectionGatewayAdapter against REAL global::Client & GameEvents!
            string domainKey = $"m4-real-bus-{Guid.NewGuid():N}";
            var fakeModCtx = new FakeModContext("m4-probe-mod");
            using LocalExileGatewayHost realHost = LocalExileGatewayHost.Create(fakeModCtx, b => b.WithBus(attach: true, domainKey: domainKey));
            int realReceived = 0;
            using var pingSub = realHost.Bridge.On<ProbeReq>((_, req) => realReceived += req.Count);

            byte[] realInnerHs = MessageCodec.EncodeEnvelope(MessageCodec.CreateTypedEnvelope(
                MessageCodec.SystemChannel,
                new ClientHandshake(SdkVersion.Current, SdkVersion.Current, ClientBuildId.Build3293d, "3.29.3d", SdkVersion.ProtocolVersion,
                    [new InstalledClientModInfo("m4-probe-mod", "0.2.0")], ["ruleset.0x2c9"]),
                sequence: 1));
            byte[] wire02caHs = Build02CAWirePacket(coreAsm, IRulesetRegistry.DefaultHostChannelRulesetId, realInnerHs);
            bool hsDispatched = await (Task<bool>)clientType.GetMethod("DispatchRulesetAsync")!.Invoke(null, [fakePlayer, (ReadOnlyMemory<byte>)wire02caHs])!;
            Assert.True(hsDispatched);
            string realConnKey = LocalExileGatewayHost.ConnectionKey(fakePlayer);
            Assert.True(realHost.Bridge.IsClientReady(realConnKey));

            byte[] realInnerReq = MessageCodec.EncodeEnvelope(MessageCodec.CreateTypedEnvelope("m4-probe-mod", new ProbeReq("real-m4", 9), sequence: 2));
            byte[] wire02caReq = Build02CAWirePacket(coreAsm, IRulesetRegistry.DefaultHostChannelRulesetId, realInnerReq);
            bool reqDispatched = await (Task<bool>)clientType.GetMethod("DispatchRulesetAsync")!.Invoke(null, [fakePlayer, (ReadOnlyMemory<byte>)wire02caReq])!;
            Assert.True(reqDispatched);
            Assert.Equal(9, realReceived);

            playerOutbox.Clear();
            await realHost.Bridge.SendAsync(realConnKey, "m4-probe-mod", new ProbeResp("outbound-ok", 99));
            byte[] outboundFrame = Assert.Single(playerOutbox);
            Assert.Equal(0x02C9, BinaryPrimitives.ReadUInt16BigEndian(outboundFrame));
            ModEnvelope outEnv = DecodeSentRulesetEnvelope(outboundFrame.AsMemory(2));
            Assert.Equal("m4-probe-mod", outEnv.Channel);
            Assert.Equal(new ProbeResp("outbound-ok", 99), MessageCodec.DeserializePayload<ProbeResp>(outEnv));

            // 8. Roslyn compilation smoke check against M4 GameServer.*.dll + LocalExile.Client.ServerBridge.dll
            string roslynCSharpDll = Path.Combine(gatewayBinDir, "Microsoft.CodeAnalysis.CSharp.dll");
            string roslynCommonDll = Path.Combine(gatewayBinDir, "Microsoft.CodeAnalysis.dll");
            if (File.Exists(roslynCSharpDll) && File.Exists(roslynCommonDll))
            {
                Assembly roslynCommonAsm = alc.LoadFromAssemblyPath(roslynCommonDll);
                Assembly roslynCSharpAsm = alc.LoadFromAssemblyPath(roslynCSharpDll);
                VerifyRoslynCompileAgainstGatewayAssemblies(roslynCommonAsm, roslynCSharpAsm, coreAsm, moddingAsm, gatewayAsm);
            }

            realHost.Dispose();
            GatewayBridgeBus.Clear(domainKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Game_CLIENT_DB", prevClientDb);
            Environment.SetEnvironmentVariable("Game_WORLD_DB", prevWorldDb);
            Environment.SetEnvironmentVariable("Game_SQL_DIR", prevSqlDir);
            alc.Unload();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            if (Directory.Exists(tempDbDir))
            {
                try { Directory.Delete(tempDbDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void ReflectionGatewayAdapter_SequentialCollectibleALCs_ResolveWithoutCrossAlcRetention()
    {
        string? repoRoot = FindLocalExileRepoRoot();
        if (repoRoot is null)
        {
            return;
        }

        string gatewayBinDir = Path.Combine(repoRoot, "src", "GameServer.Gateway", "bin", "Release", "net10.0");
        string gatewayDll = Path.Combine(gatewayBinDir, "GameServer.Gateway.dll");

        WeakReference firstAlcRef = RunInCollectibleGatewayAlc(gatewayBinDir, gatewayDll, "alc-1");
        for (int i = 0; i < 10 && firstAlcRef.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(firstAlcRef.IsAlive);

        WeakReference secondAlcRef = RunInCollectibleGatewayAlc(gatewayBinDir, gatewayDll, "alc-2");
        for (int i = 0; i < 10 && secondAlcRef.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(secondAlcRef.IsAlive);
    }

    [Fact]
    public async Task LocalExileGatewayHost_BidirectionalCrossAlc_BindsToModContextAlcEvenWhenNewerAlcLoaded()
    {
        string? repoRoot = FindLocalExileRepoRoot();
        if (repoRoot is null)
        {
            return;
        }

        string gatewayBinDir = Path.Combine(repoRoot, "src", "GameServer.Gateway", "bin", "Release", "net10.0");
        string gatewayDll = Path.Combine(gatewayBinDir, "GameServer.Gateway.dll");
        string dataDir = Path.Combine(repoRoot, "data");

        var alc1 = new IsolatedGatewayLoadContext(gatewayDll);
        var alc2 = new IsolatedGatewayLoadContext(gatewayDll);
        string tempModDir = Path.Combine(AppContext.BaseDirectory, "sb020-bidir-tmp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempModDir);

        string? prevClientDb = Environment.GetEnvironmentVariable("Game_CLIENT_DB");
        string? prevWorldDb = Environment.GetEnvironmentVariable("Game_WORLD_DB");
        string? prevSqlDir = Environment.GetEnvironmentVariable("Game_SQL_DIR");
        Environment.SetEnvironmentVariable("Game_CLIENT_DB", Path.Combine(dataDir, "client.db"));
        Environment.SetEnvironmentVariable("Game_WORLD_DB", Path.Combine(dataDir, "world.db"));
        Environment.SetEnvironmentVariable("Game_SQL_DIR", Path.Combine(repoRoot, "sql"));

        string domainKey1 = $"bidir-alc1-{Guid.NewGuid():N}";
        string domainKey1Attach = $"bidir-alc1-attach-{Guid.NewGuid():N}";
        string domainKey2 = $"bidir-alc2-{Guid.NewGuid():N}";

        try
        {
            var rt1 = InitializeGatewayAlcRuntime(alc1, gatewayBinDir, gatewayDll, string.Empty, loadGameInParentAlc: false, dataDir, Path.Combine(tempModDir, "chars-alc1.db"), "bidir1");
            var rt2 = InitializeGatewayAlcRuntime(alc2, gatewayBinDir, gatewayDll, string.Empty, loadGameInParentAlc: false, dataDir, Path.Combine(tempModDir, "chars-alc2.db"), "bidir2");
            Assembly core1 = rt1.CoreAsm;
            Assembly modding1 = alc1.Assemblies.First(a => string.Equals(a.GetName().Name, "GameServer.Modding", StringComparison.OrdinalIgnoreCase));
            Assembly gateway1 = rt1.GatewayAsm;
            Assembly core2 = rt2.CoreAsm;
            Assembly modding2 = alc2.Assemblies.First(a => string.Equals(a.GetName().Name, "GameServer.Modding", StringComparison.OrdinalIgnoreCase));
            Assembly gateway2 = rt2.GatewayAsm;

            object player1 = rt1.Player;
            object player2 = rt2.Player;

            object modCtx1 = CreateRealModHostContext(gateway1, modding1, tempModDir, "m4-probe-mod");
            object modCtx2 = CreateRealModHostContext(gateway2, modding2, tempModDir, "m4-probe-mod");

            // Create host1 for ALC-1 AFTER ALC-2 is already loaded at the end of AppDomain.CurrentDomain.GetAssemblies().
            using LocalExileGatewayHost host1 = LocalExileGatewayHost.Create(modCtx1, b => b.WithBus(attach: true, domainKey: domainKey1));
            using LocalExileGatewayHost host2 = LocalExileGatewayHost.Create(modCtx2, b => b.WithBus(attach: true, domainKey: domainKey2));

            int hitOn1 = 0;
            int hitOn2 = 0;
            using var sub1 = host1.Bridge.On<ProbeReq>((_, req) => hitOn1 += req.Count);
            using var sub2 = host2.Bridge.On<ProbeReq>((_, req) => hitOn2 += req.Count);

            byte[] hsEnvelope = MessageCodec.EncodeEnvelope(MessageCodec.CreateTypedEnvelope(
                MessageCodec.SystemChannel,
                new ClientHandshake(SdkVersion.Current, SdkVersion.Current, ClientBuildId.Build3293d, "3.29.3d", SdkVersion.ProtocolVersion,
                    [new InstalledClientModInfo("m4-probe-mod", "0.2.0")], ["ruleset.0x2c9"]),
                sequence: 1));
            byte[] req1Envelope = MessageCodec.EncodeEnvelope(MessageCodec.CreateTypedEnvelope("m4-probe-mod", new ProbeReq("alc1", 7), sequence: 2));
            byte[] req2Envelope = MessageCodec.EncodeEnvelope(MessageCodec.CreateTypedEnvelope("m4-probe-mod", new ProbeReq("alc2", 11), sequence: 2));

            MethodInfo dispatch1 = gateway1.GetType("Client")!.GetMethod("DispatchRulesetAsync")!;
            MethodInfo dispatch2 = gateway2.GetType("Client")!.GetMethod("DispatchRulesetAsync")!;

            bool hs1Ok = await (Task<bool>)dispatch1.Invoke(null, [player1, (ReadOnlyMemory<byte>)Build02CAWirePacket(core1, IRulesetRegistry.DefaultHostChannelRulesetId, hsEnvelope)])!;
            bool req1Ok = await (Task<bool>)dispatch1.Invoke(null, [player1, (ReadOnlyMemory<byte>)Build02CAWirePacket(core1, IRulesetRegistry.DefaultHostChannelRulesetId, req1Envelope)])!;
            Assert.True(hs1Ok);
            Assert.True(req1Ok);
            Assert.Equal(7, hitOn1);
            Assert.Equal(0, hitOn2);

            bool hs2Ok = await (Task<bool>)dispatch2.Invoke(null, [player2, (ReadOnlyMemory<byte>)Build02CAWirePacket(core2, IRulesetRegistry.DefaultHostChannelRulesetId, hsEnvelope)])!;
            bool req2Ok = await (Task<bool>)dispatch2.Invoke(null, [player2, (ReadOnlyMemory<byte>)Build02CAWirePacket(core2, IRulesetRegistry.DefaultHostChannelRulesetId, req2Envelope)])!;
            Assert.True(hs2Ok);
            Assert.True(req2Ok);
            Assert.Equal(11, hitOn2);
            Assert.Equal(7, hitOn1);

            // Also verify LocalExileGatewayHost.Attach(modCtx1, ...) and WithGatewayAssembly(gateway1) bind to ALC-1 while ALC-2 is loaded.
            host1.Dispose();
            GatewayBridgeBus.Clear(domainKey1);

            var attachedSink = new RecordingBatchSink();
            using var attachedBridge = new ClientBridgeHost(attachedSink, new ClientBridgeOptions { DefaultChannel = "m4-probe-mod" });
            int hitAttached = 0;
            using var attachedSub = attachedBridge.On<ProbeReq>((_, req) => hitAttached += req.Count);
            using IDisposable attachedHost = LocalExileGatewayHost.Attach(modCtx1, attachedBridge, new LocalExileGatewayHostOptions { BusDomainKey = domainKey1Attach });

            bool req1AttachedOk = await (Task<bool>)dispatch1.Invoke(null, [player1, (ReadOnlyMemory<byte>)Build02CAWirePacket(core1, IRulesetRegistry.DefaultHostChannelRulesetId, req1Envelope)])!;
            Assert.True(req1AttachedOk);
            Assert.Equal(7, hitAttached);

            attachedHost.Dispose();
            GatewayBridgeBus.Clear(domainKey1Attach);

            var standaloneCtx = new FakeModContext("m4-probe-mod");
            using LocalExileGatewayHost hintedHost = LocalExileGatewayHost.Create(
                standaloneCtx,
                b => b.WithGatewayAssembly(gateway1).WithBus(attach: true, domainKey: domainKey1));
            int hitHinted = 0;
            using var hintedSub = hintedHost.Bridge.On<ProbeReq>((_, req) => hitHinted += req.Count);

            bool req1HintedOk = await (Task<bool>)dispatch1.Invoke(null, [player1, (ReadOnlyMemory<byte>)Build02CAWirePacket(core1, IRulesetRegistry.DefaultHostChannelRulesetId, req1Envelope)])!;
            Assert.True(req1HintedOk);
            Assert.Equal(7, hitHinted);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Game_CLIENT_DB", prevClientDb);
            Environment.SetEnvironmentVariable("Game_WORLD_DB", prevWorldDb);
            Environment.SetEnvironmentVariable("Game_SQL_DIR", prevSqlDir);
            GatewayBridgeBus.Clear(domainKey1);
            GatewayBridgeBus.Clear(domainKey1Attach);
            GatewayBridgeBus.Clear(domainKey2);
            alc2.Unload();
            alc1.Unload();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            if (Directory.Exists(tempModDir))
            {
                try { Directory.Delete(tempModDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task GatewayItemTransactions_CrossAlc_GrantItemsAndSendInventoryDiffs_ResolveCorrectCoreAndGameTypes()
    {
        string? repoRoot = FindLocalExileRepoRoot();
        if (repoRoot is null)
        {
            return;
        }

        string gatewayBinDir = Path.Combine(repoRoot, "src", "GameServer.Gateway", "bin", "Release", "net10.0");
        string gatewayDll = Path.Combine(gatewayBinDir, "GameServer.Gateway.dll");
        string gameDll = Path.Combine(repoRoot, "Modules", "Core", "Game", "bin", "game.dll");
        if (!File.Exists(gameDll))
        {
            return;
        }

        string dataDir = Path.Combine(repoRoot, "data");
        string tempDbDir = Path.Combine(AppContext.BaseDirectory, "sb020-crossalc-tmp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDbDir);
        string? prevClientDb = Environment.GetEnvironmentVariable("Game_CLIENT_DB");
        string? prevWorldDb = Environment.GetEnvironmentVariable("Game_WORLD_DB");
        string? prevSqlDir = Environment.GetEnvironmentVariable("Game_SQL_DIR");
        Environment.SetEnvironmentVariable("Game_CLIENT_DB", Path.Combine(dataDir, "client.db"));
        Environment.SetEnvironmentVariable("Game_WORLD_DB", Path.Combine(dataDir, "world.db"));
        Environment.SetEnvironmentVariable("Game_SQL_DIR", Path.Combine(repoRoot, "sql"));

        var alc1 = new IsolatedGatewayLoadContext(gatewayDll);
        var alc2 = new IsolatedGatewayLoadContext(gatewayDll);
        ChildGameLoadContext? child1b = null;
        ChildGameLoadContext? child2a = null;
        ChildGameLoadContext? child2b = null;

        try
        {
            // Keep ALC 1 (with game.dll in ALC 1) loaded in AppDomain.CurrentDomain while executing in ALC 2
            // (with game.dll in a child ModLoadContext-style ALC, mirroring live LocalExile hot-reload).
            var rt1 = InitializeGatewayAlcRuntime(alc1, gatewayBinDir, gatewayDll, gameDll, loadGameInParentAlc: true, dataDir, Path.Combine(tempDbDir, "chars-alc1.db"), "crossalc1");
            var rt2 = InitializeGatewayAlcRuntime(alc2, gatewayBinDir, gatewayDll, gameDll, loadGameInParentAlc: false, dataDir, Path.Combine(tempDbDir, "chars-alc2.db"), "crossalc2");
            child2a = new ChildGameLoadContext(alc2);
            Assembly game2a = child2a.LoadFromAssemblyPath(gameDll);
            Assert.NotNull(game2a.GetType("Game"));

            // 1. Execute GrantItemsAsync and SendInventoryDiffsAsync in ALC 2 while ALC 1 remains loaded.
            await VerifyCrossAlcGrantAndDiffsAsync(rt2, tabIndex: 1, labelPrefix: "alc2");

            // 2. Simulate ModLoadContext hot-reload of game.dll on ALC 2 (new child ALC loaded while child2a stays in memory).
            child2b = new ChildGameLoadContext(alc2);
            Assembly game2b = child2b.LoadFromAssemblyPath(gameDll);
            Assert.NotNull(game2b.GetType("Game"));
            Assert.NotSame(game2a, game2b);

            object tab2Orb = LocalExileCurrencyRegistrar.CreateLiveItem(rt2.ItemCatalog, TestEmptyOrb, stackSize: 2)!;
            rt2.AccountStashType.GetMethod("AddStacked")!.Invoke(rt2.Stash, [2, tab2Orb, rt2.ShapeDelegate, null]);
            object tab2InvObj = rt2.AccountStashType.GetMethod("Inventory", [typeof(int)])!.Invoke(rt2.Stash, [2])!;
            object tab2Item = ((System.Collections.IEnumerable)tab2InvObj.GetType().GetField("Item2")!.GetValue(tab2InvObj)!).Cast<object>().First();

            rt2.Outbox.Clear();
            await LocalExileGatewayTransactions.SendInventoryDiffsAsync(
                rt2.Player,
                rt2.PlayerInventories,
                rt2.Stash,
                [new GatewayInventoryChange(2, 26, [], [tab2Item])],
                label: "hot-reload-game-dll",
                sendPacket: null);

            ushort[] reloadOpcodes = rt2.Outbox.Select(f => BinaryPrimitives.ReadUInt16BigEndian(f)).ToArray();
            Assert.Contains((ushort)0x00C0, reloadOpcodes);
            Assert.Contains((ushort)0x00C8, reloadOpcodes);
            Assert.Contains((ushort)0x00BF, reloadOpcodes);
            Assert.Contains(2, GetStashTabsSent(rt2.Player));

            // 3. Execute in ALC 1 while ALC 2 + child2b remain loaded at the end of GetAssemblies()
            // (verifies same-ALC / hintAssembly preference prevents ALC 1 from calling ALC 2 / child2b).
            await VerifyCrossAlcGrantAndDiffsAsync(rt1, tabIndex: 1, labelPrefix: "alc1");

            // 4. ChildOverParentGame: parent ALC 1 already has game.dll v1 loaded directly in alc1;
            // loading a newer Game v2 in a child ALC (ChildGameLoadContext(alc1)) must override parent v1,
            // while still rejecting alc2/child2b's incompatible Game types.
            string roslynCSharpDll = Path.Combine(gatewayBinDir, "Microsoft.CodeAnalysis.CSharp.dll");
            string roslynCommonDll = Path.Combine(gatewayBinDir, "Microsoft.CodeAnalysis.dll");
            if (File.Exists(roslynCSharpDll) && File.Exists(roslynCommonDll))
            {
                Assembly roslynCommonAsm = alc1.LoadFromAssemblyPath(roslynCommonDll);
                Assembly roslynCSharpAsm = alc1.LoadFromAssemblyPath(roslynCSharpDll);
                const string gameV2Source = """
                    using System;
                    using System.Collections.Generic;
                    using System.Threading.Tasks;
                    using GameServer.Core.Protocol.Instance;

                    public static class Game
                    {
                        public static int TabCalls;
                        public static int EquipCalls;

                        public static Task SendTabChange(Player player, AccountStash stash, int tab, IReadOnlyList<uint> gone, IReadOnlyList<InventoryItem> now, string? label)
                        {
                            TabCalls++;
                            return Client.For(player).SendPacket(0x00BF, new byte[] { (byte)tab }, label ?? "v2-tab");
                        }

                        public static Task SendEquipment(Player player, PlayerInventories items)
                        {
                            EquipCalls++;
                            return Client.For(player).SendPacket(0x00C8, Array.Empty<byte>(), "v2-equip");
                        }
                    }
                    """;

                byte[] gameV2Pe = CompileRoslynAssemblyBytes(
                    roslynCommonAsm,
                    roslynCSharpAsm,
                    "ChildOverParentGameV2",
                    gameV2Source,
                    rt1.CoreAsm.Location,
                    rt1.GatewayAsm.Location);

                child1b = new ChildGameLoadContext(alc1);
                using var gameV2Stream = new MemoryStream(gameV2Pe);
                Assembly game1ChildV2 = child1b.LoadFromStream(gameV2Stream);
                Type game1ChildV2Type = game1ChildV2.GetType("Game")!;

                object tab1InvObj = rt1.AccountStashType.GetMethod("Inventory", [typeof(int)])!.Invoke(rt1.Stash, [1])!;
                object tab1Item = ((System.Collections.IEnumerable)tab1InvObj.GetType().GetField("Item2")!.GetValue(tab1InvObj)!).Cast<object>().First();
                rt1.Outbox.Clear();

                await LocalExileGatewayTransactions.SendInventoryDiffsAsync(
                    rt1.Player,
                    rt1.PlayerInventories,
                    rt1.Stash,
                    [new GatewayInventoryChange(1, 26, [], [tab1Item])],
                    label: "child-over-parent-v2",
                    sendPacket: null);

                Assert.Equal(1, (int)game1ChildV2Type.GetField("TabCalls")!.GetValue(null)!);
                Assert.Single(rt1.Outbox);
                Assert.Equal(0x00BF, BinaryPrimitives.ReadUInt16BigEndian(rt1.Outbox[0]));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("Game_CLIENT_DB", prevClientDb);
            Environment.SetEnvironmentVariable("Game_WORLD_DB", prevWorldDb);
            Environment.SetEnvironmentVariable("Game_SQL_DIR", prevSqlDir);
            child1b?.Unload();
            child2b?.Unload();
            child2a?.Unload();
            alc2.Unload();
            alc1.Unload();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            if (Directory.Exists(tempDbDir))
            {
                try { Directory.Delete(tempDbDir, recursive: true); } catch { }
            }
        }
    }

    private sealed record GatewayAlcRuntime(
        Assembly CoreAsm,
        Assembly GatewayAsm,
        Type AccountStashType,
        object ItemCatalog,
        object PlayerInventories,
        object Stash,
        Delegate ShapeDelegate,
        object Player,
        List<byte[]> Outbox);

    private static GatewayAlcRuntime InitializeGatewayAlcRuntime(
        IsolatedGatewayLoadContext alc,
        string gatewayBinDir,
        string gatewayDll,
        string gameDll,
        bool loadGameInParentAlc,
        string dataDir,
        string dbPath,
        string accountTag)
    {
        Assembly coreAsm = alc.LoadFromAssemblyPath(Path.Combine(gatewayBinDir, "GameServer.Core.dll"));
        alc.LoadFromAssemblyPath(Path.Combine(gatewayBinDir, "GameServer.Modding.dll"));
        Assembly gatewayAsm = alc.LoadFromAssemblyPath(gatewayDll);
        if (loadGameInParentAlc)
        {
            alc.LoadFromAssemblyPath(gameDll);
        }

        Type programType = gatewayAsm.GetType("Program")!;
        Type statCatalogType = gatewayAsm.GetType("StatCatalog")!;
        Type skillCatalogType = gatewayAsm.GetType("SkillCatalog")!;
        Type itemCatalogType = gatewayAsm.GetType("ItemCatalog")!;
        Type playerInvType = gatewayAsm.GetType("PlayerInventories")!;
        Type savesType = gatewayAsm.GetType("Saves")!;
        Type accountStashType = gatewayAsm.GetType("AccountStash")!;
        Type itemType = coreAsm.GetType("GameServer.Core.Protocol.Instance.Item")!;

        object statCatalog = Activator.CreateInstance(statCatalogType)!;
        if (programType.GetField("Stats", BindingFlags.Public | BindingFlags.Static) is { } statsField)
            statsField.SetValue(null, statCatalog);
        else
            programType.GetProperty("Stats", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, statCatalog);

        object skillCatalog = CreateWithDefaults(skillCatalogType);
        if (programType.GetField("Skills", BindingFlags.Public | BindingFlags.Static) is { } skillsField)
            skillsField.SetValue(null, skillCatalog);
        else
            programType.GetProperty("Skills", BindingFlags.Public | BindingFlags.Static)?.SetValue(null, skillCatalog);

        gatewayAsm.GetType("ClusterJewels")!.GetMethod("LoadGameData")!.Invoke(null, null);
        ConstructorInfo catalogCtor = itemCatalogType.GetConstructors()[0];
        ParameterInfo[] catalogParams = catalogCtor.GetParameters();
        object?[] catalogArgs = new object?[catalogParams.Length];
        catalogArgs[0] = dataDir;
        for (int i = 1; i < catalogParams.Length; i++)
        {
            catalogArgs[i] = catalogParams[i].HasDefaultValue ? catalogParams[i].DefaultValue : null;
        }

        object itemCatalog = catalogCtor.Invoke(catalogArgs);
        programType.GetField("Items", BindingFlags.Public | BindingFlags.Static)?.SetValue(null, itemCatalog);
        FieldInfo? rollsField = programType.GetField("Rolls", BindingFlags.Public | BindingFlags.Static);
        object? itemRolls = rollsField is not null ? CreateWithDefaults(rollsField.FieldType, itemCatalog, dataDir) : null;
        rollsField?.SetValue(null, itemRolls);
        FieldInfo? benchField = programType.GetField("Bench", BindingFlags.Public | BindingFlags.Static);
        object? bench = benchField is not null ? CreateWithDefaults(benchField.FieldType, itemCatalog, itemRolls) : null;
        benchField?.SetValue(null, bench);
        gatewayAsm.GetType("Quests")!.GetMethod("LoadGameData")!.Invoke(null, null);

        var registrar = new LocalExileCurrencyRegistrar();
        registrar.Register(TestEmptyOrb);
        registrar.ApplyToLiveCatalog(itemCatalog);

        savesType.GetMethod("Open", [typeof(string)])!.Invoke(null, [dbPath]);
        object inv = Activator.CreateInstance(playerInvType)!;
        object stash = accountStashType.GetMethod("For")!.Invoke(null, [$"sb020-{accountTag}-{Guid.NewGuid():N}", itemCatalog])!;
        Delegate shapeDelegate = BuildGatewayShapeDelegate(playerInvType, itemCatalogType, itemCatalog, itemType);
        object player = CreateRealGatewayPlayer(gatewayAsm, coreAsm, out List<byte[]> outbox);

        Type outgoingType = typeof(Action<,>).MakeGenericType(player.GetType(), typeof(byte[]));
        var pParam = System.Linq.Expressions.Expression.Parameter(player.GetType(), "p");
        var bParam = System.Linq.Expressions.Expression.Parameter(typeof(byte[]), "bytes");
        Action<object, byte[]> captureOutgoing = (p, bytes) =>
        {
            if (ReferenceEquals(p, player))
            {
                outbox.Add(bytes.ToArray());
            }
        };
        Delegate outgoingDel = System.Linq.Expressions.Expression.Lambda(
            outgoingType,
            System.Linq.Expressions.Expression.Call(
                System.Linq.Expressions.Expression.Constant(captureOutgoing),
                captureOutgoing.GetType().GetMethod("Invoke")!,
                System.Linq.Expressions.Expression.Convert(pParam, typeof(object)),
                bParam),
            pParam,
            bParam).Compile();
        programType.GetField("Outgoing", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, outgoingDel);

        MethodInfo driveByServer = programType.GetMethod("DriveByServer", BindingFlags.Public | BindingFlags.Static)!;
        object?[] driveArgs = new object?[driveByServer.GetParameters().Length];
        driveArgs[0] = player;
        driveArgs[1] = accountTag;
        for (int i = 2; i < driveArgs.Length; i++)
        {
            driveArgs[i] = driveByServer.GetParameters()[i].HasDefaultValue ? driveByServer.GetParameters()[i].DefaultValue : null;
        }
        driveByServer.Invoke(null, driveArgs);

        return new GatewayAlcRuntime(coreAsm, gatewayAsm, accountStashType, itemCatalog, inv, stash, shapeDelegate, player, outbox);
    }

    private static async Task VerifyCrossAlcGrantAndDiffsAsync(GatewayAlcRuntime rt, int tabIndex, string labelPrefix)
    {
        rt.Outbox.Clear();
        GatewayGrantResult grantRes = await LocalExileGatewayTransactions.GrantItemsAsync(
            rt.Player,
            rt.ItemCatalog,
            rt.PlayerInventories,
            TestEmptyOrb,
            count: 2,
            customName: $"{labelPrefix} Orb",
            requireFullFit: true,
            sendPacket: null);

        Assert.True(grantRes.Success, grantRes.Message);
        Assert.Equal(2, grantRes.GrantedCount);
        byte[] grantFrame = Assert.Single(rt.Outbox);
        Assert.Equal(0x00BF, BinaryPrimitives.ReadUInt16BigEndian(grantFrame));

        object stashOrb = LocalExileCurrencyRegistrar.CreateLiveItem(rt.ItemCatalog, TestEmptyOrb, stackSize: 3)!;
        rt.AccountStashType.GetMethod("AddStacked")!.Invoke(rt.Stash, [tabIndex, stashOrb, rt.ShapeDelegate, null]);
        object tabInvObj = rt.AccountStashType.GetMethod("Inventory", [typeof(int)])!.Invoke(rt.Stash, [tabIndex])!;
        object stashInvItem = ((System.Collections.IEnumerable)tabInvObj.GetType().GetField("Item2")!.GetValue(tabInvObj)!).Cast<object>().First();
        object mainInvItem = ((System.Collections.IEnumerable)rt.PlayerInventories.GetType().GetMethod("Items", [typeof(byte)])!.Invoke(rt.PlayerInventories, [(byte)0])!)
            .Cast<object>().First();

        rt.Outbox.Clear();
        await LocalExileGatewayTransactions.SendInventoryDiffsAsync(
            rt.Player,
            rt.PlayerInventories,
            rt.Stash,
            [
                new GatewayInventoryChange(tabIndex, 26, [], [stashInvItem]),
                new GatewayInventoryChange(null, 0, [], [mainInvItem]),
            ],
            label: $"{labelPrefix}-diffs",
            sendPacket: null);

        ushort[] opcodes = rt.Outbox.Select(f => BinaryPrimitives.ReadUInt16BigEndian(f)).ToArray();
        Assert.Contains((ushort)0x00C0, opcodes);
        Assert.Contains((ushort)0x00C8, opcodes);
        Assert.Equal(2, opcodes.Count(op => op == 0x00BF));
        Assert.Contains(tabIndex, GetStashTabsSent(rt.Player));
    }

    private static HashSet<int> GetStashTabsSent(object player)
    {
        object conn = player.GetType().GetProperty("Connection", BindingFlags.Public | BindingFlags.Instance)!.GetValue(player)!;
        return (HashSet<int>)conn.GetType().GetField("StashTabsSent", BindingFlags.Public | BindingFlags.Instance)!.GetValue(conn)!;
    }

    private sealed class ChildGameLoadContext(AssemblyLoadContext parentAlc) : AssemblyLoadContext("ChildGameHotReload", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name?.StartsWith("GameServer.", StringComparison.Ordinal) == true)
            {
                return parentAlc.Assemblies.FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
            }

            if (string.Equals(assemblyName.Name, "LocalExile.Client.ServerBridge", StringComparison.OrdinalIgnoreCase))
            {
                return typeof(ClientBridgeHost).Assembly;
            }

            if (string.Equals(assemblyName.Name, "LocalExile.Client.Sdk", StringComparison.OrdinalIgnoreCase))
            {
                return typeof(SdkVersion).Assembly;
            }

            return null;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RunInCollectibleGatewayAlc(string gatewayBinDir, string gatewayDll, string ownerTag)
    {
        var alc = new IsolatedGatewayLoadContext(gatewayDll);
        var weakRef = new WeakReference(alc, trackResurrection: true);
        try
        {
            alc.LoadFromAssemblyPath(Path.Combine(gatewayBinDir, "GameServer.Core.dll"));
            alc.LoadFromAssemblyPath(Path.Combine(gatewayBinDir, "GameServer.Modding.dll"));
            Assembly gatewayAsm = alc.LoadFromAssemblyPath(gatewayDll);

            string domainKey = $"alc-seq-{ownerTag}-{Guid.NewGuid():N}";
            var modCtx = new FakeModContext($"mod-{ownerTag}");
            using LocalExileGatewayHost host = LocalExileGatewayHost.Create(modCtx, b => b.WithBus(attach: true, domainKey: domainKey));
            Assert.NotNull(gatewayAsm.GetType("Client"));
            host.Dispose();
            GatewayBridgeBus.Clear(domainKey);
        }
        finally
        {
            alc.Unload();
        }

        return weakRef;
    }

    private static uint[] Decode00BfRemovedIds(byte[] frame)
    {
        // S->C 0x00BF: u16 opcode, u8 manager, u32 invId, u32 rev, u32 removedCount, u32[] removedIds
        uint count = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(11, 4));
        var ids = new uint[count];
        for (int i = 0; i < count; i++)
        {
            ids[i] = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(15 + (i * 4), 4));
        }

        return ids;
    }

    // -------------------------------------------------------------------------
    // Helpers & Fakes
    // -------------------------------------------------------------------------

    private static ModEnvelope DecodeSentRulesetEnvelope(ReadOnlyMemory<byte> rulesetBody)
    {
        Assert.True(MessageCodec.TryDecodeRulesetBody(rulesetBody, out _, out ReadOnlyMemory<byte> inner));
        return MessageCodec.DecodeEnvelope(inner.Span);
    }

    private static object CreateWithDefaults(Type type, params object?[] prefixArgs)
    {
        ConstructorInfo ctor = type.GetConstructors()[0];
        ParameterInfo[] ps = ctor.GetParameters();
        object?[] args = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            if (i < prefixArgs.Length)
                args[i] = prefixArgs[i];
            else
                args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
        }
        return ctor.Invoke(args);
    }

    private static byte[] Build02CAWirePacket(Assembly coreAsm, byte rulesetId, byte[] innerPayload) =>
        MessageCodec.EncodeFullRulesetPacket(IOpcodeRegistry.ClientRulesetOpcode, rulesetId, innerPayload);

    private static object CreateGatewayItem(
        Type itemCatalogType,
        object baseType,
        Type itemType,
        Type rarityType,
        Type itemModType,
        byte rarity,
        byte itemLevel,
        bool identified,
        int requiredLevel,
        object[] mods)
    {
        MethodInfo createMethod = itemCatalogType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == "Create" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == baseType.GetType());
        ParameterInfo[] ps = createMethod.GetParameters();
        object?[] args = new object?[ps.Length];
        args[0] = baseType;
        for (int i = 1; i < ps.Length; i++)
        {
            args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
        }

        object item = createMethod.Invoke(null, args)!;
        itemType.GetProperty("Rarity")!.GetSetMethod(true)!.Invoke(item, [Enum.ToObject(rarityType, rarity)]);
        itemType.GetProperty("ItemLevel")!.GetSetMethod(true)!.Invoke(item, [itemLevel]);
        itemType.GetProperty("Identified")!.GetSetMethod(true)!.Invoke(item, [identified]);

        var typedMods = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(itemModType))!;
        foreach (object m in mods)
        {
            typedMods.Add(m);
        }

        itemType.GetProperty("Mods")!.GetSetMethod(true)!.Invoke(item, [typedMods]);
        itemType.GetProperty("RequiredLevel")!.GetSetMethod(true)!.Invoke(item, [requiredLevel]);
        return item;
    }

    private static object CloneWithProp(object item, string propName, object? val)
    {
        object cloned = item.GetType().GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(item, null)!;
        item.GetType().GetProperty(propName)!.GetSetMethod(true)!.Invoke(cloned, [val]);
        return cloned;
    }

    private static ushort GetGatewayItemStack(object inv, byte row, uint itemId)
    {
        var items = (System.Collections.IEnumerable)inv.GetType().GetMethod("Items", [typeof(byte)])!.Invoke(inv, [row])!;
        foreach (object? it in items)
        {
            if (it is not null && (uint)it.GetType().GetProperty("Id")!.GetValue(it)! == itemId)
            {
                object inner = it.GetType().GetProperty("Item")!.GetValue(it)!;
                return (ushort)inner.GetType().GetProperty("StackSize")!.GetValue(inner)!;
            }
        }

        return 0;
    }

    private static Delegate BuildGatewayShapeDelegate(Type playerInvType, Type itemCatalogType, object itemCatalog, Type itemType)
    {
        Type shapeType = playerInvType.GetNestedType("Shape", BindingFlags.Public)!;
        Type nullableShapeType = typeof(Nullable<>).MakeGenericType(shapeType);
        Type delegateType = typeof(Func<,>).MakeGenericType(itemType, nullableShapeType);
        MethodInfo byHash = itemCatalogType.GetMethod("ByHash", [typeof(uint)])!;
        ConstructorInfo ctor = shapeType.GetConstructors()[0];

        Func<object, object?> resolver = item =>
        {
            uint hash = (uint)itemType.GetProperty("TypeHash")!.GetValue(item)!;
            object? bt = byHash.Invoke(itemCatalog, [hash]);
            if (bt is null) return null;
            byte w = (byte)bt.GetType().GetProperty("Width")!.GetValue(bt)!;
            byte h = (byte)bt.GetType().GetProperty("Height")!.GetValue(bt)!;
            string cls = (string)bt.GetType().GetProperty("Class")!.GetValue(bt)!;
            ushort maxStack = Math.Max((ushort)1, (ushort)bt.GetType().GetProperty("MaxStack")!.GetValue(bt)!);
            return ctor.Invoke([w, h, cls, maxStack, null, false]);
        };

        var p = System.Linq.Expressions.Expression.Parameter(itemType, "i");
        var tmp = System.Linq.Expressions.Expression.Variable(typeof(object), "s");
        var call = System.Linq.Expressions.Expression.Call(
            System.Linq.Expressions.Expression.Constant(resolver),
            resolver.GetType().GetMethod("Invoke")!,
            System.Linq.Expressions.Expression.Convert(p, typeof(object)));
        var body = System.Linq.Expressions.Expression.Condition(
            System.Linq.Expressions.Expression.Equal(tmp, System.Linq.Expressions.Expression.Constant(null)),
            System.Linq.Expressions.Expression.Default(nullableShapeType),
            System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.Unbox(tmp, shapeType), nullableShapeType));
        return System.Linq.Expressions.Expression.Lambda(delegateType,
            System.Linq.Expressions.Expression.Block([tmp], System.Linq.Expressions.Expression.Assign(tmp, call), body), p).Compile();
    }

    private static void VerifyRoslynCompileAgainstGatewayAssemblies(
        Assembly roslynCommonAsm,
        Assembly roslynCSharpAsm,
        Assembly coreAsm,
        Assembly moddingAsm,
        Assembly gatewayAsm)
    {
        const string source = """
            using System;
            using System.Threading.Tasks;
            using GameServer.Modding;
            using LocalExile.Client.Sdk;
            using LocalExile.Client.ServerBridge;

            public static class ProbeModEntry
            {
                public static void Init(IModContext context)
                {
                    using var host = LocalExileGatewayHost.Create(context, b => b.WithBus(attach: true));
                    host.Bridge.OnClientReady(async session =>
                    {
                        var win = await session.Ui.OpenAsync("probe/hud.json");
                        win.Closed += () => { };
                    });

                    var svc = new LocalExileItemAffixService();
                    _ = new PortableItem(1u, 1u, 1u, "Test", "Belt", "Leather Belt", ItemRarityKind.Rare, 80, true, false, false, 0, 1, [], CustomName: "Imbued Belt");
                    _ = GameEvents.PlayerEntered.Add(context.Manifest.Id, player =>
                    {
                        var ch = global::Client.For(player);
                        return ch.SendChat("ready");
                    });
                }
            }
            """;

        _ = CompileRoslynAssemblyBytes(
            roslynCommonAsm,
            roslynCSharpAsm,
            "M5ProbeMod",
            source,
            typeof(SdkVersion).Assembly.Location,
            typeof(ClientBridgeHost).Assembly.Location,
            coreAsm.Location,
            moddingAsm.Location,
            gatewayAsm.Location);
    }

    private static byte[] CompileRoslynAssemblyBytes(
        Assembly roslynCommonAsm,
        Assembly roslynCSharpAsm,
        string assemblyName,
        string source,
        params string[] extraReferencePaths)
    {
        Type syntaxTreeType = roslynCSharpAsm.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree")!;
        Type compType = roslynCSharpAsm.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation")!;
        Type compOptsType = roslynCSharpAsm.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions")!;
        Type outputKindType = roslynCommonAsm.GetType("Microsoft.CodeAnalysis.OutputKind")!;
        Type metaRefType = roslynCommonAsm.GetType("Microsoft.CodeAnalysis.MetadataReference")!;
        Type treeType = roslynCommonAsm.GetType("Microsoft.CodeAnalysis.SyntaxTree")!;

        MethodInfo parseText = syntaxTreeType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == "ParseText" && m.GetParameters()[0].ParameterType == typeof(string));
        object?[] parseArgs = new object?[parseText.GetParameters().Length];
        parseArgs[0] = source;
        object syntaxTree = parseText.Invoke(null, parseArgs)!;

        MethodInfo createFromFile = metaRefType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == "CreateFromFile" && m.GetParameters()[0].ParameterType == typeof(string));
        object CreateMetaRef(string path)
        {
            object?[] args = new object?[createFromFile.GetParameters().Length];
            args[0] = path;
            return createFromFile.Invoke(null, args)!;
        }

        string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var refPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            typeof(object).Assembly.Location,
            typeof(Task).Assembly.Location,
            typeof(Console).Assembly.Location,
            typeof(System.Collections.Concurrent.ConcurrentDictionary<,>).Assembly.Location,
            typeof(System.Linq.Enumerable).Assembly.Location,
            Path.Combine(runtimeDir, "System.Runtime.dll"),
            Path.Combine(runtimeDir, "System.Collections.dll"),
            Path.Combine(runtimeDir, "netstandard.dll"),
        };
        foreach (string extra in extraReferencePaths)
        {
            refPaths.Add(extra);
        }

        Array metaRefs = Array.CreateInstance(metaRefType, refPaths.Count);
        int idx = 0;
        foreach (string path in refPaths)
        {
            if (File.Exists(path))
            {
                metaRefs.SetValue(CreateMetaRef(path), idx++);
            }
        }
        if (idx < metaRefs.Length)
        {
            Array trimmed = Array.CreateInstance(metaRefType, idx);
            Array.Copy(metaRefs, trimmed, idx);
            metaRefs = trimmed;
        }

        Array trees = Array.CreateInstance(treeType, 1);
        trees.SetValue(syntaxTree, 0);

        ConstructorInfo optsCtor = compOptsType.GetConstructors().First(c => c.GetParameters().Length >= 1 && c.GetParameters()[0].ParameterType == outputKindType);
        object?[] optsArgs = new object?[optsCtor.GetParameters().Length];
        optsArgs[0] = Enum.Parse(outputKindType, "DynamicallyLinkedLibrary");
        for (int i = 1; i < optsArgs.Length; i++)
        {
            optsArgs[i] = optsCtor.GetParameters()[i].HasDefaultValue ? optsCtor.GetParameters()[i].DefaultValue : null;
        }
        object compOpts = optsCtor.Invoke(optsArgs);

        MethodInfo createComp = compType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == "Create" && m.GetParameters().Length == 4);
        object compilation = createComp.Invoke(null, [assemblyName, trees, metaRefs, compOpts])!;

        using var peStream = new MemoryStream();
        MethodInfo emit = compilation.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .First(m => m.Name == "Emit" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(Stream));
        object?[] emitArgs = new object?[emit.GetParameters().Length];
        emitArgs[0] = peStream;
        object emitResult = emit.Invoke(compilation, emitArgs)!;

        bool success = (bool)emitResult.GetType().GetProperty("Success")!.GetValue(emitResult)!;
        var diags = ((System.Collections.IEnumerable)emitResult.GetType().GetProperty("Diagnostics")!.GetValue(emitResult)!)
            .Cast<object>()
            .Select(d => d.ToString())
            .ToArray();
        Assert.True(success, $"Roslyn compile ({assemblyName}) failed:\n" + string.Join("\n", diags));
        return peStream.ToArray();
    }

    private static object CreateRealModHostContext(Assembly gatewayAsm, Assembly moddingAsm, string folder, string modId)
    {
        Type modHostType = gatewayAsm.GetType("ModHost")!;
        Type loadedModType = modHostType.GetNestedType("LoadedMod", BindingFlags.NonPublic)!;
        Type contextType = modHostType.GetNestedType("Context", BindingFlags.NonPublic)!;
        Type modManifestType = moddingAsm.GetType("GameServer.Modding.ModManifest")!;
        Type modKindType = moddingAsm.GetType("GameServer.Modding.ModKind")!;

        object modHost = Activator.CreateInstance(modHostType, [folder, new Action<string>(_ => { }), false, false])!;
        object manifest = Activator.CreateInstance(
            modManifestType,
            [modId, "0.2.0", Enum.Parse(modKindType, "Feature"), Array.Empty<string>(), $"{modId}.dll", true])!;
        object loadedMod = Activator.CreateInstance(loadedModType, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, [manifest, folder, null], null)!;
        return Activator.CreateInstance(contextType, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, [modHost, loadedMod], null)!;
    }

    private static object CreateRealGatewayPlayer(Assembly gatewayAsm, Assembly coreAsm, out List<byte[]> outbox)
    {
        outbox = [];
        Type playerType = gatewayAsm.GetType("Player")!;
        Type areaLayoutType = gatewayAsm.GetType("AreaLayout")!;
        Type leagueRuntimeType = gatewayAsm.GetType("LeagueRuntime")!;
        Type leagueType = coreAsm.GetType("GameServer.Core.Accounts.League")!;

        ConstructorInfo layoutCtor = areaLayoutType.GetConstructors()[0];
        ParameterInfo[] lps = layoutCtor.GetParameters();
        object?[] largs = new object?[lps.Length];
        largs[0] = Convert.ChangeType(1, lps[0].ParameterType, System.Globalization.CultureInfo.InvariantCulture);
        largs[1] = "1_1_1";
        largs[2] = Convert.ChangeType(100, lps[2].ParameterType, System.Globalization.CultureInfo.InvariantCulture);
        largs[3] = Convert.ChangeType(100, lps[3].ParameterType, System.Globalization.CultureInfo.InvariantCulture);
        largs[4] = Convert.ChangeType(50, lps[4].ParameterType, System.Globalization.CultureInfo.InvariantCulture);
        largs[5] = Convert.ChangeType(50, lps[5].ParameterType, System.Globalization.CultureInfo.InvariantCulture);
        largs[6] = new Func<int, int, bool>((_, _) => true);
        largs[7] = lps[7].ParameterType.IsArray
            ? Array.CreateInstance(lps[7].ParameterType.GetElementType()!, 0)
            : Array.Empty<byte>();
        for (int i = 8; i < lps.Length; i++)
        {
            largs[i] = lps[i].HasDefaultValue ? lps[i].DefaultValue : null;
        }

        object layout = layoutCtor.Invoke(largs);
        object player = Activator.CreateInstance(playerType, [layout, 1u, 1u])!;

        PropertyInfo instProp = playerType.GetProperty("Instance")!;
        instProp.SetValue(player, Activator.CreateInstance(instProp.PropertyType)!);

        object character = playerType.GetProperty("Character")!.GetValue(player)!;
        character.GetType().GetProperty("Name")!.SetValue(character, "RealM4Tester");

        object league = CreateWithDefaults(leagueType, "m4-test", "Standard", false, false);
        var modsSet = new HashSet<string>(["m4-probe-mod"], StringComparer.OrdinalIgnoreCase);
        object leagueRuntime = CreateWithDefaults(leagueRuntimeType, league, modsSet);
        SetMemberValue(player, "LeagueModule", leagueRuntime);

        object conn = playerType.GetProperty("Connection")!.GetValue(player)!;
        PropertyInfo sessionProp = conn.GetType().GetProperty("Session", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        object session = Activator.CreateInstance(sessionProp.PropertyType, ["127.0.0.1:41234", null!, null!])!;
        sessionProp.SetValue(conn, session);
        SetMemberValue(conn, "PlayerSent", true);
        List<byte[]> captured = outbox;
        conn.GetType().GetField("Outbox", BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(conn, new Func<byte[], string?, Task>((bytes, _) => { captured.Add(bytes); return Task.CompletedTask; }));
        return player;
    }

    private static void SetMemberValue(object target, string memberName, object? value)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        Type t = target.GetType();
        if (t.GetProperty(memberName, Flags) is { } prop)
        {
            prop.SetValue(target, value);
            return;
        }

        if (t.GetField(memberName, Flags) is { } field)
        {
            field.SetValue(target, value);
            return;
        }

        throw new MissingMemberException(t.FullName, memberName);
    }

    private sealed class IsolatedGatewayLoadContext(string mainAssemblyPath) : AssemblyLoadContext("M4IsolatedTest", isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.Equals(assemblyName.Name, "LocalExile.Client.ServerBridge", StringComparison.OrdinalIgnoreCase))
            {
                return typeof(ClientBridgeHost).Assembly;
            }

            if (string.Equals(assemblyName.Name, "LocalExile.Client.Sdk", StringComparison.OrdinalIgnoreCase))
            {
                return typeof(SdkVersion).Assembly;
            }

            string? resolved = _resolver.ResolveAssemblyToPath(assemblyName);
            return resolved is not null ? LoadFromAssemblyPath(resolved) : null;
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is not null ? LoadUnmanagedDllFromPath(path) : 0;
        }
    }

    private sealed class RecordingBatchSink : IBatchingGatewayPacketSink
    {
        public List<(string ConnectionId, ushort Opcode, byte[] Payload)> Packets { get; } = [];
        public List<(string ConnectionId, string Text)> Chats { get; } = [];
        public List<IReadOnlyList<GatewayBatchItem>> Batches { get; } = [];

        public void Clear()
        {
            Packets.Clear();
            Chats.Clear();
            Batches.Clear();
        }

        public Task SendPacketAsync(string connectionId, ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            Packets.Add((connectionId, opcode, payload.ToArray()));
            return Task.CompletedTask;
        }

        public Task SendChatReplyAsync(string connectionId, string text, CancellationToken cancellationToken = default)
        {
            Chats.Add((connectionId, text));
            return Task.CompletedTask;
        }

        public Task SendBatchAsync(string connectionId, IReadOnlyList<GatewayBatchItem> items, CancellationToken cancellationToken = default)
        {
            Batches.Add(items);
            foreach (GatewayBatchItem item in items)
            {
                if (item.IsChatReply)
                {
                    Chats.Add((connectionId, item.ChatText ?? string.Empty));
                }
                else
                {
                    Packets.Add((connectionId, item.Opcode, item.Payload.ToArray()));
                }
            }

            return Task.CompletedTask;
        }
    }

    private sealed class AsyncYieldingSink : IGatewayPacketSink
    {
        public async Task SendPacketAsync(string connectionId, ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
            await Task.Yield();

        public async Task SendChatReplyAsync(string connectionId, string text, CancellationToken cancellationToken = default) =>
            await Task.Yield();
    }

    private sealed class SingleThreadQueueSyncContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];
        private readonly Thread _thread;

        public SingleThreadQueueSyncContext()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var (cb, state) in _queue.GetConsumingEnumerable())
                {
                    cb(state);
                }
            })
            {
                IsBackground = true,
            };
            _thread.Start();
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            if (_queue.IsAddingCompleted)
            {
                return;
            }

            try
            {
                _queue.Add((d, state));
            }
            catch (InvalidOperationException)
            {
                // Ignore late async continuations posted after test teardown.
            }
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (Environment.CurrentManagedThreadId == _thread.ManagedThreadId)
            {
                d(state);
                return;
            }

            using var done = new ManualResetEventSlim();
            Exception? error = null;
            _queue.Add((_ =>
            {
                try { d(state); }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            }, null));
            done.Wait();
            if (error is not null) throw error;
        }

        public Task RunAsync(Func<Task> action)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async _ =>
            {
                try
                {
                    await action();
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }, null);
            return tcs.Task;
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            if (Environment.CurrentManagedThreadId != _thread.ManagedThreadId && _thread.Join(TimeSpan.FromSeconds(5)))
            {
                _queue.Dispose();
            }
        }
    }

    private sealed class FakeModContext(string id)
    {
        public FakeManifest Manifest { get; } = new(id);
        public List<string> Logs { get; } = [];
        public void Log(string message) => Logs.Add(message);
    }

    private sealed record FakeManifest(string Id);

    private sealed class FakeGatewayPlayer(string remote, uint id, string charName)
    {
        public string Remote { get; } = remote;
        public uint Id { get; } = id;
        public string CharacterName { get; } = charName;
        public bool Closed { get; set; }
    }

    private sealed class FakeGatewayAdapter : ILocalExileGatewayAdapter
    {
        private readonly List<(byte RulesetId, Func<object, string, ReadOnlyMemory<byte>, Task<bool>> Handler)> _rulesetHandlers = [];
        private readonly List<Func<object, string, string, Task<bool>>> _clientCmdHandlers = [];
        private readonly List<(ushort Opcode, Func<object, byte[], Task<bool>> Handler)> _packetHandlers = [];
        private readonly List<Func<object, Task>> _enteredHandlers = [];
        private readonly List<Func<object, string, Task>> _leavingHandlers = [];
        private readonly List<Func<object, Task>> _readyHandlers = [];
        private readonly List<Func<GatewayClientMessageContext, Task<bool>>> _msgHandlers = [];
        private readonly List<Func<GatewayCosmeticChangeContext, Task>> _cosmeticHandlers = [];

        public List<(ushort Opcode, byte[] Payload)> SentPackets { get; } = [];
        public List<string> SentChats { get; } = [];
        public bool SupportsCoreClientChannel => true;

        public string? TryGetOwnerId(object modContext) => (modContext as FakeModContext)?.Manifest.Id;
        public Action<string>? TryGetLogger(object modContext) => modContext is FakeModContext fake ? fake.Log : null;
        public string GetConnectionId(object player) { var p = (FakeGatewayPlayer)player; return $"{p.Remote}#{p.Id}"; }
        public string GetCharacterName(object player) => ((FakeGatewayPlayer)player).CharacterName;
        public bool IsPlayerClosed(object player) => ((FakeGatewayPlayer)player).Closed;
        public object? TryGetClientChannel(object player) => null;

        public Task SendPacketAsync(object player, ushort opcode, ReadOnlyMemory<byte> payload, string? log, CancellationToken cancellationToken)
        {
            SentPackets.Add((opcode, payload.ToArray()));
            return Task.CompletedTask;
        }

        public Task SendChatReplyAsync(object player, string text, CancellationToken cancellationToken)
        {
            SentChats.Add(text);
            return Task.CompletedTask;
        }

        public IDisposable SubscribeRuleset(string ownerId, byte rulesetId, Func<object, string, ReadOnlyMemory<byte>, Task<bool>> onInnerPayload)
        {
            var entry = (rulesetId, onInnerPayload);
            _rulesetHandlers.Add(entry);
            return new RemoveDisposable(() => _rulesetHandlers.Remove(entry));
        }

        public IDisposable SubscribeClientCommand(string ownerId, Func<object, string, string, Task<bool>> onCommand)
        {
            _clientCmdHandlers.Add(onCommand);
            return new RemoveDisposable(() => _clientCmdHandlers.Remove(onCommand));
        }

        public IDisposable SubscribePacket(string ownerId, ushort opcode, Func<object, byte[], Task<bool>> onPacket)
        {
            var entry = (opcode, onPacket);
            _packetHandlers.Add(entry);
            return new RemoveDisposable(() => _packetHandlers.Remove(entry));
        }

        public IDisposable SubscribeCommand(string ownerId, string commandName, string usage, Func<object, string[], Task> onCommand) =>
            new RemoveDisposable(() => { });

        public IDisposable SubscribePlayerEntered(string ownerId, Func<object, Task> onEntered)
        {
            _enteredHandlers.Add(onEntered);
            return new RemoveDisposable(() => _enteredHandlers.Remove(onEntered));
        }

        public IDisposable SubscribePlayerLeaving(string ownerId, Func<object, string, Task> onLeaving)
        {
            _leavingHandlers.Add(onLeaving);
            return new RemoveDisposable(() => _leavingHandlers.Remove(onLeaving));
        }

        public IDisposable? SubscribeClientReady(string ownerId, Func<object, Task> onReady)
        {
            _readyHandlers.Add(onReady);
            return new RemoveDisposable(() => _readyHandlers.Remove(onReady));
        }

        public IDisposable? SubscribeClientMessage(string ownerId, Func<GatewayClientMessageContext, Task<bool>> onMessage)
        {
            _msgHandlers.Add(onMessage);
            return new RemoveDisposable(() => _msgHandlers.Remove(onMessage));
        }

        public IDisposable? SubscribeCosmeticChanged(string ownerId, Func<GatewayCosmeticChangeContext, Task> onCosmeticChanged)
        {
            _cosmeticHandlers.Add(onCosmeticChanged);
            return new RemoveDisposable(() => _cosmeticHandlers.Remove(onCosmeticChanged));
        }

        public async Task<bool> SimulateCoreClientRulesetAsync(FakeGatewayPlayer player, byte rulesetId, ReadOnlyMemory<byte> innerPayload)
        {
            foreach (var (rid, handler) in _rulesetHandlers.ToArray())
            {
                if (rid == rulesetId && await handler(player, GetConnectionId(player), innerPayload))
                    return true;
            }
            return false;
        }

        public async Task<bool> SimulatePacketAsync(FakeGatewayPlayer player, ushort opcode, byte[] bytes)
        {
            foreach (var (op, handler) in _packetHandlers.ToArray())
            {
                if (op == opcode && await handler(player, bytes))
                    return true;
            }
            return false;
        }

        public async Task FirePlayerEnteredAsync(FakeGatewayPlayer player)
        {
            foreach (var h in _enteredHandlers.ToArray()) await h(player);
        }

        public async Task FirePlayerLeavingAsync(FakeGatewayPlayer player, string reason)
        {
            foreach (var h in _leavingHandlers.ToArray()) await h(player, reason);
        }

        public async Task FireClientReadyAsync(FakeGatewayPlayer player)
        {
            foreach (var h in _readyHandlers.ToArray()) await h(player);
        }

        public async Task FireClientMessageAsync(FakeGatewayPlayer player, string text, IReadOnlyList<uint> links, bool isCommand)
        {
            var ctx = new GatewayClientMessageContext(GetConnectionId(player), GetCharacterName(player), player, text, links, isCommand);
            foreach (var h in _msgHandlers.ToArray()) await h(ctx);
        }

        public async Task FireCosmeticChangedAsync(FakeGatewayPlayer player, byte slot, uint recordId, uint mtxHash)
        {
            var ctx = new GatewayCosmeticChangeContext(GetConnectionId(player), GetCharacterName(player), player, slot, recordId, mtxHash);
            foreach (var h in _cosmeticHandlers.ToArray()) await h(ctx);
        }

        private sealed class RemoveDisposable(Action remove) : IDisposable
        {
            private Action? _remove = remove;
            public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
        }
    }

    private sealed class FakeGatewayItemCatalog(CustomCurrencyItemDefinition def)
    {
        private readonly FakeGatewayBaseType _baseType = new(def.TypeHash, def.DisplayName, def.MaxStackSize);

        public FakeGatewayBaseType? ByHash(uint hash) => hash == def.TypeHash ? _baseType : null;
        public static FakeGatewayItem Create(FakeGatewayBaseType baseType) => new(baseType.Hash, stackSize: 1, identified: false);
    }

    private sealed class FakeGatewayBaseType(uint hash, string name, ushort maxStack)
    {
        public uint Hash { get; } = hash;
        public string Name { get; } = name;
        public ushort MaxStack { get; } = maxStack;
        public byte Width { get; } = 1;
        public byte Height { get; } = 1;
        public string Class { get; } = "StackableCurrency";
    }

    private sealed class FakeGatewayItem(uint typeHash, ushort stackSize, bool identified)
    {
        public uint TypeHash { get; } = typeHash;
        public ushort StackSize { get; set; } = stackSize;
        public bool Identified { get; set; } = identified;
    }

    private sealed class FakeGatewayInventoryItem(uint id, byte x, byte y, FakeGatewayItem item)
    {
        public uint Id { get; } = id;
        public byte X { get; } = x;
        public byte Y { get; } = y;
        public FakeGatewayItem Item { get; } = item;
    }

    private sealed class FakeGatewayPlayerInventories
    {
        private readonly Dictionary<byte, List<FakeGatewayInventoryItem>> _rows = [];
        private readonly Dictionary<uint, byte> _rowOf = [];

        public void Add(byte row, uint id, FakeGatewayItem item)
        {
            if (!_rows.TryGetValue(row, out List<FakeGatewayInventoryItem>? list))
            {
                list = [];
                _rows[row] = list;
            }

            list.Add(new FakeGatewayInventoryItem(id, 0, 0, item));
            _rowOf[id] = row;
        }

        public byte? RowOf(uint itemId) => _rowOf.TryGetValue(itemId, out byte row) ? row : null;

        public List<FakeGatewayInventoryItem> Items(byte row) =>
            _rows.TryGetValue(row, out List<FakeGatewayInventoryItem>? list) ? list : [];

        public static uint InventoryId(byte row) => (uint)row + 1;

        public FakeGatewayItem? GetItem(uint itemId)
        {
            if (!_rowOf.TryGetValue(itemId, out byte row))
            {
                return null;
            }

            return _rows[row].FirstOrDefault(x => x.Id == itemId)?.Item;
        }
    }
}
