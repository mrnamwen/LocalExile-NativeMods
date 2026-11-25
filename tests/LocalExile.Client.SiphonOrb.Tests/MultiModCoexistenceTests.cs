using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using DualMod.MaxwellPet.Server;
using DualMod.MaxwellPet.Shared;
using DualMod.SiphonOrb.Server;
using DualMod.SiphonOrb.Shared;
using LocalExile.Client.Host;
using LocalExile.Client.Host.Native;
using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Addresses;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.Sdk.Protocol;
using LocalExile.Client.ServerBridge;
using Xunit;

namespace LocalExile.Client.SiphonOrb.Tests;

/// <summary>
/// Regression suite proving multiple LocalExile server mods (<c>maxwell</c> + <c>siphon</c>, or any N mods
/// in separate <see cref="AssemblyLoadContext"/>s) coexist cleanly on <c>0x02CA</c> and <c>/nm</c> without
/// swallowing each other's <see cref="ClientHandshake"/> or channel messages, while emitting exactly ONE
/// deduplicated <see cref="ServerHandshake"/> on the wire.
/// </summary>
public sealed class MultiModCoexistenceTests
{
    [Theory]
    [InlineData(NetTransportKind.HostRuleset, true)]
    [InlineData(NetTransportKind.HostRuleset, false)]
    [InlineData(NetTransportKind.ChatFallback, true)]
    [InlineData(NetTransportKind.ChatFallback, false)]
    public async Task MaxwellAndSiphonCoexistSimultaneously_SingleServerHandshake_ZeroChatFallback_UnloadAndHotReloadSafe(
        NetTransportKind transport,
        bool maxwellLoadedFirst)
    {
        string busKey = $"test.gateway.bus.{Guid.NewGuid():N}";
        string shadowRoot = Path.Combine(Path.GetTempPath(), "nm-multimod-shadow-" + Guid.NewGuid().ToString("N"));
        string repoRoot = FindRepoRoot();

        try
        {
            using var simBridge = new SimulatedNativeBridge(ClientBuildId.Build3293d);
            using var clientHost = new ClientHostRuntime(simBridge, new ClientHostOptions
            {
                TransportKind = transport,
                HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId,
                ShadowCopyRoot = shadowRoot
            });
            clientHost.Start();

            await RunCoexistenceScenarioAsync(simBridge, clientHost, repoRoot, busKey, transport, maxwellLoadedFirst);

            Assert.Equal(0, GatewayBridgeBus.GetParticipantCount(busKey));
        }
        finally
        {
            GatewayBridgeBus.Clear(busKey);
            if (Directory.Exists(shadowRoot))
            {
                Directory.Delete(shadowRoot, recursive: true);
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task RunCoexistenceScenarioAsync(
        SimulatedNativeBridge simBridge,
        ClientHostRuntime clientHost,
        string repoRoot,
        string busKey,
        NetTransportKind transport,
        bool maxwellLoadedFirst)
    {
        // Load BOTH real client mod DLLs (maxwell + siphon) into the single ClientHostRuntime.
        string maxwellExample = Path.Combine(repoRoot, "examples", "DualMod.MaxwellPet");
        string siphonExample = Path.Combine(repoRoot, "examples", "DualMod.SiphonOrb");
        string maxwellStage = Path.Combine(Path.GetTempPath(), "nm-coexist-maxwell-" + Guid.NewGuid().ToString("N"));
        string siphonStage = Path.Combine(Path.GetTempPath(), "nm-coexist-siphon-" + Guid.NewGuid().ToString("N"));

        try
        {
            StageClientMod(maxwellExample, maxwellStage, "maxwell.Client.dll");
            StageClientMod(siphonExample, siphonStage, "siphon.Client.dll");

            var loadedMaxwell = clientHost.Loader.LoadModFromDirectory(maxwellStage);
            var loadedSiphon = clientHost.Loader.LoadModFromDirectory(siphonStage);
            Assert.Equal(MaxwellProtocol.ModId, loadedMaxwell.Manifest.Id);
            Assert.Equal(SiphonProtocol.ModId, loadedSiphon.Manifest.Id);

            // Counting loopback sink shared by both mods' independent ClientBridgeHosts so we can
            // assert EXACTLY ONE ServerHandshake hits the wire.
            var wireCounter = new CountingLoopbackGatewaySink(simBridge);

            // Simulate LocalExile Gateway's exact dispatch semantics:
            //   1. GameEvents.Packet(0x02ca) short-circuits on the FIRST subscriber returning true!
            //   2. GameEvents.Command("nm") uses ChatCommands.TryAdd — ONLY the FIRST mod's /nm runs!
            var gatewayPacketSubscribers = new List<(string Owner, Func<object, byte[], Task<bool>> Handler)>();
            var gatewayNmCommands = new List<(string Owner, Func<object, string[], Task> Handler)>();

            var maxwellGatewayPlayers = new Dictionary<string, object>(StringComparer.Ordinal);
            var siphonGatewayPlayers = new Dictionary<string, object>(StringComparer.Ordinal);

            var maxwellBridge = new ClientBridgeHost(wireCounter, new ClientBridgeOptions
            {
                DefaultTransport = transport,
                HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId,
                RequiredMods = ["maxwell"],
                ServerCapabilities = ["ruleset.0x2c9", "chat.fallback", "maxwell.audio"]
            });
            var siphonBridge = new ClientBridgeHost(wireCounter, new ClientBridgeOptions
            {
                DefaultTransport = transport,
                HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId,
                RequiredMods = ["siphon"],
                ServerCapabilities = ["ruleset.0x2c9", "chat.fallback", "siphon.currency"]
            });

            var maxwellBackend = new SimulatedMaxwellWorldBackend();
            var siphonBackend = new SimulatedSiphonWorldBackend();
            using var maxwellMod = new MaxwellServerMod(maxwellBridge, maxwellBackend);
            using var siphonMod = new SiphonServerMod(siphonBridge, siphonBackend);
            maxwellMod.Attach();
            siphonMod.Attach();

            int maxwellReadyCount = 0;
            int siphonReadyCount = 0;
            maxwellBridge.ClientReady += _ => maxwellReadyCount++;
            siphonBridge.ClientReady += _ => siphonReadyCount++;

            GatewayBridgeRegistration? maxwellReg = null;
            GatewayBridgeRegistration? siphonReg = null;

            const string connId = "127.0.0.1:54321#1";
            const string charName = "DualModExile";
            object fakeGatewayPlayer = new();

            void RegisterMaxwell()
            {
                maxwellReg = maxwellBridge.AttachToGatewayBus(
                    MaxwellProtocol.ModId,
                    onGatewayContext: (cid, character, ctx) =>
                    {
                        if (ctx is not null) maxwellGatewayPlayers[cid] = ctx;
                        if (!string.IsNullOrWhiteSpace(character)) maxwellMod.BindConnection(cid, character);
                    },
                    domainKey: busKey);

                gatewayPacketSubscribers.Add((MaxwellProtocol.ModId, async (player, bytes) =>
                {
                    maxwellGatewayPlayers[connId] = player;
                    maxwellMod.BindConnection(connId, charName);
                    return await maxwellBridge.HandleIncomingPacketAsync(connId, IOpcodeRegistry.ClientRulesetOpcode, bytes.AsMemory(2), charName, player);
                }));

                gatewayNmCommands.Add((MaxwellProtocol.ModId, async (player, args) =>
                {
                    maxwellGatewayPlayers[connId] = player;
                    maxwellMod.BindConnection(connId, charName);
                    await maxwellBridge.HandleIncomingChatCommandAsync(connId, "/nm " + string.Join(' ', args), charName, player);
                }));
            }

            void RegisterSiphon()
            {
                siphonReg = siphonBridge.AttachToGatewayBus(
                    SiphonProtocol.ModId,
                    onGatewayContext: (cid, character, ctx) =>
                    {
                        if (ctx is not null) siphonGatewayPlayers[cid] = ctx;
                        if (!string.IsNullOrWhiteSpace(character)) siphonMod.BindConnection(cid, character);
                    },
                    domainKey: busKey);

                gatewayPacketSubscribers.Add((SiphonProtocol.ModId, async (player, bytes) =>
                {
                    siphonGatewayPlayers[connId] = player;
                    siphonMod.BindConnection(connId, charName);
                    return await siphonBridge.HandleIncomingPacketAsync(connId, IOpcodeRegistry.ClientRulesetOpcode, bytes.AsMemory(2), charName, player);
                }));

                gatewayNmCommands.Add((SiphonProtocol.ModId, async (player, args) =>
                {
                    siphonGatewayPlayers[connId] = player;
                    siphonMod.BindConnection(connId, charName);
                    await siphonBridge.HandleIncomingChatCommandAsync(connId, "/nm " + string.Join(' ', args), charName, player);
                }));
            }

            if (maxwellLoadedFirst)
            {
                RegisterMaxwell();
                RegisterSiphon();
            }
            else
            {
                RegisterSiphon();
                RegisterMaxwell();
            }

            Assert.Equal(2, GatewayBridgeBus.GetParticipantCount(busKey));

            // Wire SimulatedNativeBridge C->S traffic through our LocalExile short-circuiting dispatcher!
            simBridge.OnOutgoingRulesetPacket = (rulesetId, innerPayload) =>
            {
                byte[] fullPacket = MessageCodec.EncodeFullRulesetPacket(IOpcodeRegistry.ClientRulesetOpcode, rulesetId, innerPayload.Span);
                foreach (var (_, handler) in gatewayPacketSubscribers.ToArray())
                {
                    if (handler(fakeGatewayPlayer, fullPacket).GetAwaiter().GetResult())
                    {
                        // LocalExile GameEvents.AnyPacket short-circuits here!
                        break;
                    }
                }
            };

            simBridge.OnOutgoingChatLine = line =>
            {
                if (!line.StartsWith("/nm ", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                string[] args = line[4..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // LocalExile ChatCommands.TryAdd keeps ONLY the first /nm command!
                if (gatewayNmCommands.Count > 0)
                {
                    gatewayNmCommands[0].Handler(fakeGatewayPlayer, args).GetAwaiter().GetResult();
                }
            };

            // 1. Handshake: Client sends ONE ClientHandshake -> BOTH mods get HasNativeHost=true & ClientReady,
            //    BOTH mods bind fakeGatewayPlayer via onGatewayContext, and EXACTLY ONE ServerHandshake hits the wire!
            int clientHandshakeCompletedCount = 0;
            ServerHandshake? receivedServerHandshake = null;
            clientHost.HandshakeCompleted += hs =>
            {
                clientHandshakeCompletedCount++;
                receivedServerHandshake = hs;
            };

            clientHost.SendHandshake();

            Assert.Equal(1, wireCounter.ServerHandshakeCount);
            Assert.Equal(1, clientHandshakeCompletedCount);
            Assert.NotNull(receivedServerHandshake);
            Assert.Contains("maxwell", receivedServerHandshake!.RequiredMods);
            Assert.Contains("siphon", receivedServerHandshake.RequiredMods);
            Assert.Contains("maxwell.audio", receivedServerHandshake.ServerCapabilities);
            Assert.Contains("siphon.currency", receivedServerHandshake.ServerCapabilities);

            Assert.Equal(1, maxwellReadyCount);
            Assert.Equal(1, siphonReadyCount);
            Assert.True(maxwellBridge.For(connId).HasNativeHost);
            Assert.True(siphonBridge.For(connId).HasNativeHost);
            Assert.True(maxwellBridge.For(connId).HasClientMod(MaxwellProtocol.ModId));
            Assert.True(siphonBridge.For(connId).HasClientMod(SiphonProtocol.ModId));
            Assert.Same(fakeGatewayPlayer, maxwellGatewayPlayers[connId]);
            Assert.Same(fakeGatewayPlayer, siphonGatewayPlayers[connId]);

            // 2. Unrelated 0x02CA ruleset (e.g. ruleset 0x69 Harvest) MUST NOT be consumed!
            byte[] unrelatedPacket = MessageCodec.EncodeFullRulesetPacket(IOpcodeRegistry.ClientRulesetOpcode, 0x69, [0x01, 0x02, 0x03]);
            bool unrelatedClaimed = await gatewayPacketSubscribers[0].Handler(fakeGatewayPlayer, unrelatedPacket);
            Assert.False(unrelatedClaimed);

            // 3. Maxwell works simultaneously: /maxwell panel opens native MaxwellPanel.ui and C->S ToggleMaxwellRequest
            //    flows cleanly through the short-circuiting 0x02CA / /nm dispatcher regardless of load order!
            string maxwellPanelReply = await maxwellMod.HandleMaxwellCommandAsync(connId, charName, ["panel"]);
            Assert.DoesNotContain("Chat Fallback", maxwellPanelReply);
            IUiWindow? maxwellPanel = clientHost.Ui.GetWindow(MaxwellProtocol.PanelWindowId);
            Assert.NotNull(maxwellPanel);
            Assert.True(maxwellPanel!.Visible);

            maxwellPanel.Require<IButton>("summon_button").Click();
            Assert.Single(maxwellBackend.SpawnedPets);

            // 4. Siphon works simultaneously RIGHT AFTER login: C->S 0x0071 opens native SiphonPicker.ui with
            //    ZERO /siphon pick chat fallback, and clicking Confirm routes SelectSiphonAffixRequest to siphon!
            await siphonMod.HandleSiphonCommandAsync(connId, charName, ["give", "2"]);
            PortableItem emptyOrb = siphonBackend.GetAllItems(connId).Single(i => i.TypeHash == SiphonProtocol.EmptyOrbTypeHash);
            var rarePlate = new PortableItem(
                ItemId: 901,
                InventoryId: 1,
                TypeHash: 0x10000010u,
                ItemName: "Soul Shell",
                BaseType: "Astral Plate",
                ItemClass: "Body Armour",
                Rarity: ItemRarityKind.Rare,
                ItemLevel: 85,
                Identified: true,
                Corrupted: false,
                Mirrored: false,
                FracturedCount: 0,
                StackSize: 1,
                Mods:
                [
                    new PortableItemMod(ItemModSectionKind.Explicit, "IncreasedLife10", 0x2001, "Prime", "+99 to maximum Life", true, false, "IncreasedLife", 1, [99]),
                    new PortableItemMod(ItemModSectionKind.Explicit, "FireResistance7", 0x2003, "of Tzteosh", "+45% to Fire Resistance", false, true, "FireResistance", 1, [45]),
                ]);
            siphonBackend.AddItem(connId, rarePlate);

            byte[] useEmptyPkt = LocalExileCurrencyRegistrar.EncodeUseItemOn(1, emptyOrb.ItemId, 1, rarePlate.ItemId);
            bool claimedUse = await siphonMod.CurrencyRegistrar.HandleUseItemOnPacketAsync(
                connId, charName, useEmptyPkt, (invId, itemId) => siphonBackend.ResolveTypeHash(connId, invId, itemId));
            Assert.True(claimedUse);

            // ZERO chat fallback! Native SiphonPicker.ui MUST be open!
            Assert.DoesNotContain(siphonBackend.GetChatLog(connId), line => line.Contains("/siphon pick", StringComparison.Ordinal));
            IUiWindow? siphonPicker = clientHost.Ui.GetWindow(SiphonProtocol.PickerWindowId);
            Assert.NotNull(siphonPicker);
            Assert.True(siphonPicker!.Visible);
            Assert.Contains("+99 to maximum Life", siphonPicker.Require<ILabel>("affix_label_0").Text);

            // Select row 0 and Confirm -> C->S SelectSiphonAffixRequest passes through first mod's hook -> fans out to siphon!
            siphonPicker.Require<IButton>("affix_btn_0").Click();
            siphonPicker.Require<IButton>("confirm_button").Click();

            Assert.False(siphonPicker.Visible);
            Assert.Single(siphonBackend.GetAllItems(connId), i => i.TypeHash == SiphonProtocol.ChargedOrbTypeHash);
            Assert.Equal((ushort)1, siphonBackend.GetItem(connId, 1, emptyOrb.ItemId)!.StackSize);

            // 5. Unload first mod (maxwell) -> LocalExile removes maxwell's hooks; siphon seamlessly takes over!
            maxwellReg!.Dispose();
            gatewayPacketSubscribers.RemoveAll(s => s.Owner == MaxwellProtocol.ModId);
            gatewayNmCommands.RemoveAll(s => s.Owner == MaxwellProtocol.ModId);
            Assert.Equal(1, GatewayBridgeBus.GetParticipantCount(busKey));

            // Re-open SiphonPicker and cancel after maxwell unloaded to confirm seamless failover.
            Assert.True(await siphonMod.CurrencyRegistrar.HandleUseItemOnPacketAsync(
                connId, charName, useEmptyPkt, (invId, itemId) => siphonBackend.ResolveTypeHash(connId, invId, itemId)));
            Assert.True(siphonPicker.Visible);
            siphonPicker.Require<IButton>("cancel_button").Click();
            Assert.False(siphonPicker.Visible);

            // 6. Hot-reload siphon mid-session (brand-new ClientBridgeHost without client re-handshaking):
            //    GetOrCreateSession automatically hydrates HasNativeHost from the bus's cached handshake!
            siphonReg!.Dispose();
            var reloadedSiphonBridge = new ClientBridgeHost(wireCounter, new ClientBridgeOptions
            {
                DefaultTransport = transport,
                HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId
            });
            using var reloadedReg = reloadedSiphonBridge.AttachToGatewayBus(SiphonProtocol.ModId, domainKey: busKey);
            IClientSession hydrated = reloadedSiphonBridge.For(connId);
            Assert.True(hydrated.HasNativeHost);
            Assert.True(hydrated.HasClientMod(SiphonProtocol.ModId));
            Assert.Equal(transport, hydrated.ActiveTransport);

            // Disconnect cleans cached handshake so subsequent sessions start fresh.
            reloadedSiphonBridge.RemoveSession(connId);
            Assert.False(reloadedSiphonBridge.For(connId).HasNativeHost);
        }
        finally
        {
            await clientHost.Loader.UnloadModAsync(MaxwellProtocol.ModId);
            await clientHost.Loader.UnloadModAsync(SiphonProtocol.ModId);
            if (Directory.Exists(maxwellStage)) Directory.Delete(maxwellStage, recursive: true);
            if (Directory.Exists(siphonStage)) Directory.Delete(siphonStage, recursive: true);
        }
    }

    [Fact]
    public async Task GatewayBridgeBus_IsolatesParticipantExceptions_AndStillDeliversToHealthyMods()
    {
        string busKey = $"test.gateway.fault.{Guid.NewGuid():N}";
        try
        {
            var sink1 = new RecordingSink();
            var sink2 = new RecordingSink();
            var host1 = new ClientBridgeHost(sink1);
            var host2 = new ClientBridgeHost(sink2);

            var errors = new List<(string Owner, Exception Ex)>();
            host1.ClientReady += _ => throw new InvalidOperationException("Boom in buggy mod ClientReady");

            bool host2Ready = false;
            host2.ClientReady += _ => host2Ready = true;

            using var reg1 = host1.AttachToGatewayBus("buggy-mod", domainKey: busKey, onDispatchError: (o, ex) => errors.Add((o, ex)));
            using var reg2 = host2.AttachToGatewayBus("healthy-mod", domainKey: busKey, onDispatchError: (o, ex) => errors.Add((o, ex)));

            var handshake = new ClientHandshake(
                "1.0.0", SdkVersion.Current, ClientBuildId.Build3293d, "3.29.3d", SdkVersion.ProtocolVersion,
                [new InstalledClientModInfo("siphon", "1.0.0")],
                ["rulesets.0x2c9"]);
            byte[] envBytes = MessageCodec.EncodeEnvelope(MessageCodec.CreateTypedEnvelope(MessageCodec.SystemChannel, handshake, sequence: 1));
            byte[] rulesetBody = MessageCodec.EncodeRulesetBody(IRulesetRegistry.DefaultHostChannelRulesetId, envBytes);

            bool handled = await host1.HandleIncomingPacketAsync("conn-fault", IOpcodeRegistry.ClientRulesetOpcode, rulesetBody);
            Assert.True(handled);

            // Buggy mod threw AFTER sending ServerHandshake -> error captured, healthy mod STILL received handshake,
            // and NO duplicate ServerHandshake was sent!
            Assert.Single(errors);
            Assert.Equal("buggy-mod", errors[0].Owner);
            Assert.True(host2Ready);
            Assert.True(host2.For("conn-fault").HasNativeHost);
            Assert.Single(sink1.Packets);
            Assert.Empty(sink2.Packets);
        }
        finally
        {
            GatewayBridgeBus.Clear(busKey);
        }
    }

    [Fact]
    public async Task GatewayBridgeBus_FansOutAcrossGenuinelySeparateAssemblyLoadContexts_WithSingleServerHandshake()
    {
        // Directly mirrors LocalExile's ModLoadContext architecture: two completely separate ALCs each load
        // their OWN copy of LocalExile.Client.ServerBridge.dll and LocalExile.Client.Sdk.dll so CLR types are
        // NOT identical across ALCs. The BCL-only AppDomain bus must still fan out cleanly with zero InvalidCastException!
        string busKey = $"test.gateway.crossalc.{Guid.NewGuid():N}";
        string assemblyDir = Path.GetDirectoryName(typeof(ClientBridgeHost).Assembly.Location)!;
        string serverBridgePath = Path.Combine(assemblyDir, "LocalExile.Client.ServerBridge.dll");
        string testAsmPath = typeof(MultiModCoexistenceTests).Assembly.Location;

        var alcA = new IsolatedModAlc("mod-maxwell-alc", assemblyDir);
        var alcB = new IsolatedModAlc("mod-siphon-alc", assemblyDir);

        try
        {
            Assembly bridgeAsmA = alcA.LoadFromAssemblyPath(serverBridgePath);
            Assembly bridgeAsmB = alcB.LoadFromAssemblyPath(serverBridgePath);
            Assembly modAsmA = alcA.LoadFromAssemblyPath(testAsmPath);
            Assembly modAsmB = alcB.LoadFromAssemblyPath(testAsmPath);

            // Sanity-check: the two ALCs really loaded DISTINCT ClientBridgeHost CLR types!
            Type hostTypeA = bridgeAsmA.GetType("LocalExile.Client.ServerBridge.ClientBridgeHost", throwOnError: true)!;
            Type hostTypeB = bridgeAsmB.GetType("LocalExile.Client.ServerBridge.ClientBridgeHost", throwOnError: true)!;
            Assert.NotEqual(hostTypeA, hostTypeB);

            // Instantiate each ALC's own RecordingSink (implementing that ALC's IGatewayPacketSink) and ClientBridgeHost.
            object sinkA = Activator.CreateInstance(modAsmA.GetType(typeof(RecordingSink).FullName!, throwOnError: true)!)!;
            object sinkB = Activator.CreateInstance(modAsmB.GetType(typeof(RecordingSink).FullName!, throwOnError: true)!)!;
            object hostA = CreateAlcBridgeHost(bridgeAsmA, sinkA);
            object hostB = CreateAlcBridgeHost(bridgeAsmB, sinkB);

            // Attach both hosts to the SAME AppDomain bus key!
            IDisposable regA = AttachAlcHost(hostA, "maxwell", busKey);
            IDisposable regB = AttachAlcHost(hostB, "siphon", busKey);

            try
            {
                Assert.Equal(2, GatewayBridgeBus.GetParticipantCount(busKey));
                Assert.Equal(["maxwell", "siphon"], GatewayBridgeBus.GetParticipantOwners(busKey));

                // Build a real ClientHandshake 0x02CA payload in the default ALC and feed it ONLY into ALC A!
                var hs = new ClientHandshake(
                    "1.0.0", SdkVersion.Current, ClientBuildId.Build3293d, "3.29.3d", SdkVersion.ProtocolVersion,
                    [new InstalledClientModInfo("maxwell", "1.0.0"), new InstalledClientModInfo("siphon", "1.0.0")],
                    ["rulesets.0x2c9"]);
                byte[] envBytes = MessageCodec.EncodeEnvelope(MessageCodec.CreateTypedEnvelope(MessageCodec.SystemChannel, hs, sequence: 1));
                byte[] body = MessageCodec.EncodeRulesetBody(IRulesetRegistry.DefaultHostChannelRulesetId, envBytes);

                MethodInfo handleMethodA = hostTypeA.GetMethod(
                    "HandleIncomingPacketAsync",
                    [typeof(string), typeof(ushort), typeof(ReadOnlyMemory<byte>), typeof(CancellationToken)])!;
                bool handled = await (Task<bool>)handleMethodA.Invoke(
                    hostA,
                    ["cross-alc-conn", IOpcodeRegistry.ClientRulesetOpcode, (ReadOnlyMemory<byte>)body, CancellationToken.None])!;
                Assert.True(handled);

                // Verify BOTH ALC A AND ALC B now report HasNativeHost == true and HasClientMod(...) == true!
                Assert.True(GetAlcSessionHasNativeHost(hostA, "cross-alc-conn"));
                Assert.True(GetAlcSessionHasNativeHost(hostB, "cross-alc-conn"));
                Assert.True(GetAlcSessionHasClientMod(hostA, "cross-alc-conn", "maxwell"));
                Assert.True(GetAlcSessionHasClientMod(hostB, "cross-alc-conn", "siphon"));

                // Verify EXACTLY ONE ServerHandshake was emitted across both ALCs!
                Assert.Equal(1, GetAlcSinkPacketCount(sinkA));
                Assert.Equal(0, GetAlcSinkPacketCount(sinkB));
            }
            finally
            {
                regA.Dispose();
                regB.Dispose();
            }

            Assert.Equal(0, GatewayBridgeBus.GetParticipantCount(busKey));
        }
        finally
        {
            GatewayBridgeBus.Clear(busKey);
        }
    }

    // Mirrors LocalExile's real ModLoadContext (non-collectible per-mod ALC isolating ServerBridge & Sdk).
    private sealed class IsolatedModAlc(string name, string directory) : AssemblyLoadContext(name, isCollectible: false)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is "LocalExile.Client.ServerBridge" or "LocalExile.Client.Sdk" or "LocalExile.Client.SiphonOrb.Tests")
            {
                string path = Path.Combine(directory, assemblyName.Name + ".dll");
                if (File.Exists(path))
                {
                    return LoadFromAssemblyPath(path);
                }
            }

            return null;
        }
    }

    private static object CreateAlcBridgeHost(Assembly bridgeAsm, object sink)
    {
        Type hostType = bridgeAsm.GetType("LocalExile.Client.ServerBridge.ClientBridgeHost", throwOnError: true)!;
        Type optionsType = bridgeAsm.GetType("LocalExile.Client.ServerBridge.ClientBridgeOptions", throwOnError: true)!;
        object options = Activator.CreateInstance(optionsType)!;
        return Activator.CreateInstance(hostType, sink, options)!;
    }

    private static int GetAlcSinkPacketCount(object sink)
    {
        object packets = sink.GetType().GetProperty("Packets")!.GetValue(sink)!;
        return (int)packets.GetType().GetProperty("Count")!.GetValue(packets)!;
    }

    private static IDisposable AttachAlcHost(object host, string ownerId, string busKey)
    {
        MethodInfo attach = host.GetType().GetMethod("AttachToGatewayBus")!;
        return (IDisposable)attach.Invoke(host, [ownerId, null, busKey, null])!;
    }

    private static bool GetAlcSessionHasNativeHost(object host, string connId)
    {
        object session = host.GetType().GetMethod("For")!.Invoke(host, [connId])!;
        return (bool)session.GetType().GetProperty("HasNativeHost")!.GetValue(session)!;
    }

    private static bool GetAlcSessionHasClientMod(object host, string connId, string modId)
    {
        object session = host.GetType().GetMethod("For")!.Invoke(host, [connId])!;
        return (bool)session.GetType().GetMethod("HasClientMod")!.Invoke(session, [modId, null])!;
    }

    private sealed class RecordingSink : IGatewayPacketSink
    {
        public List<(string ConnectionId, ushort Opcode, byte[] Payload)> Packets { get; } = [];
        public List<(string ConnectionId, string Text)> Chats { get; } = [];

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
    }

    private sealed class CountingLoopbackGatewaySink(SimulatedNativeBridge simBridge) : IGatewayPacketSink
    {
        private int _serverHandshakeCount;

        public int ServerHandshakeCount => Volatile.Read(ref _serverHandshakeCount);

        public Task SendPacketAsync(string connectionId, ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            if (opcode == IOpcodeRegistry.ServerRulesetOpcode &&
                MessageCodec.TryDecodeRulesetBody(payload, out byte rulesetId, out ReadOnlyMemory<byte> inner))
            {
                TryCountServerHandshake(inner.Span);
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
            if (MessageCodec.TryDecodeServerChatEnvelope(text, out ModEnvelope? env) &&
                env is not null &&
                string.Equals(env.Channel, MessageCodec.SystemChannel, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(env.MessageType, nameof(ServerHandshake), StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _serverHandshakeCount);
            }

            simBridge.SimulateIncomingChat("Server", text);
            return Task.CompletedTask;
        }

        private void TryCountServerHandshake(ReadOnlySpan<byte> inner)
        {
            try
            {
                ModEnvelope env = MessageCodec.DecodeEnvelope(inner);
                if (string.Equals(env.Channel, MessageCodec.SystemChannel, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(env.MessageType, nameof(ServerHandshake), StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _serverHandshakeCount);
                }
            }
            catch (InvalidDataException)
            {
            }
        }
    }

    private static void StageClientMod(string exampleRoot, string targetDir, string clientDllName)
    {
        Directory.CreateDirectory(targetDir);
        string manifestSrc = Path.Combine(exampleRoot, "mod.json");
        File.Copy(manifestSrc, Path.Combine(targetDir, "mod.json"), overwrite: true);

        string dllPath = FindCompiledAssembly(exampleRoot, "Client", clientDllName);
        File.Copy(dllPath, Path.Combine(targetDir, clientDllName), overwrite: true);

        // Copy client/ loose files into files/ using lowercased relative paths matching WaypointModInstaller layout.
        string clientAssetsDir = Path.Combine(exampleRoot, "client");
        if (Directory.Exists(clientAssetsDir))
        {
            foreach (string srcFile in Directory.GetFiles(clientAssetsDir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(clientAssetsDir, srcFile).Replace('\\', '/').ToLowerInvariant();
                string dstFile = Path.Combine(targetDir, "files", rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dstFile)!);
                File.Copy(srcFile, dstFile, overwrite: true);
            }
        }
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

    private static string FindCompiledAssembly(string exampleRoot, string subDir, string fileName)
    {
        string binDir = Path.Combine(exampleRoot, subDir, "bin");
        foreach (string config in new[] { "Debug", "Release" })
        {
            string candidate = Path.Combine(binDir, config, "net10.0", fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException($"Could not locate compiled assembly {fileName} under {binDir}.");
    }
}
