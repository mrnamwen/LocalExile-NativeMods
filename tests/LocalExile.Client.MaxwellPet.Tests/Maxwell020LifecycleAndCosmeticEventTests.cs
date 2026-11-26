using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using DualMod.MaxwellPet.Server;
using DualMod.MaxwellPet.Shared;
using LocalExile.Client.Host;
using LocalExile.Client.Host.Loading;
using LocalExile.Client.Host.Native;
using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Addresses;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.Sdk.Protocol;
using LocalExile.Client.ServerBridge;
using Xunit;

namespace LocalExile.Client.MaxwellPet.Tests;

internal static class MaxwellRoslynEntryCompileHelper
{
    public static void CompileAndAssertSuccess(
        string gatewayBinDir,
        string assemblyName,
        string source,
        params string[] extraReferencePaths)
    {
        var roslynAlc = new AssemblyLoadContext("MaxwellRoslynProbe-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        try
        {
            Assembly roslynCommon = roslynAlc.LoadFromAssemblyPath(Path.Combine(gatewayBinDir, "Microsoft.CodeAnalysis.dll"));
            Assembly roslynCSharp = roslynAlc.LoadFromAssemblyPath(Path.Combine(gatewayBinDir, "Microsoft.CodeAnalysis.CSharp.dll"));

            Type syntaxTreeType = roslynCSharp.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree")!;
            Type compType = roslynCSharp.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation")!;
            Type compOptsType = roslynCSharp.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions")!;
            Type nullableOptsType = roslynCSharp.GetType("Microsoft.CodeAnalysis.NullableContextOptions")!;
            Type outputKindType = roslynCommon.GetType("Microsoft.CodeAnalysis.OutputKind")!;
            Type metaRefType = roslynCommon.GetType("Microsoft.CodeAnalysis.MetadataReference")!;
            Type treeType = roslynCommon.GetType("Microsoft.CodeAnalysis.SyntaxTree")!;

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
                typeof(Enumerable).Assembly.Location,
                Path.Combine(runtimeDir, "System.Runtime.dll"),
                Path.Combine(runtimeDir, "System.Collections.dll"),
                Path.Combine(runtimeDir, "netstandard.dll"),
                Path.Combine(gatewayBinDir, "GameServer.Core.dll"),
                Path.Combine(gatewayBinDir, "GameServer.Data.dll"),
                Path.Combine(gatewayBinDir, "GameServer.Modding.dll"),
                Path.Combine(gatewayBinDir, "GameServer.Gateway.dll"),
            };
            foreach (string extra in extraReferencePaths)
            {
                refPaths.Add(extra);
            }

            string[] presentPaths = refPaths.Where(File.Exists).ToArray();
            Array metaRefs = Array.CreateInstance(metaRefType, presentPaths.Length);
            for (int i = 0; i < presentPaths.Length; i++)
            {
                metaRefs.SetValue(CreateMetaRef(presentPaths[i]), i);
            }

            Array trees = Array.CreateInstance(treeType, 1);
            trees.SetValue(syntaxTree, 0);

            ConstructorInfo optsCtor = compOptsType.GetConstructors()
                .First(c => c.GetParameters().Length >= 1 && c.GetParameters()[0].ParameterType == outputKindType);
            object?[] optsArgs = new object?[optsCtor.GetParameters().Length];
            optsArgs[0] = Enum.Parse(outputKindType, "DynamicallyLinkedLibrary");
            for (int i = 1; i < optsArgs.Length; i++)
            {
                ParameterInfo param = optsCtor.GetParameters()[i];
                if (param.ParameterType == nullableOptsType)
                    optsArgs[i] = Enum.Parse(nullableOptsType, "Enable");
                else
                    optsArgs[i] = param.HasDefaultValue ? param.DefaultValue : null;
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
            string[] diags = ((IEnumerable)emitResult.GetType().GetProperty("Diagnostics")!.GetValue(emitResult)!)
                .Cast<object>()
                .Select(d => d.ToString() ?? string.Empty)
                .ToArray();
            Assert.True(success, $"Roslyn compile ({assemblyName}) failed:\n" + string.Join("\n", diags));
        }
        finally
        {
            roslynAlc.Unload();
        }
    }
}

public sealed class Maxwell020LifecycleAndCosmeticEventTests
{
    [Fact]
    public async Task OnCosmeticChanged_ReconcilesPetAndMusicStateWithoutAreaTickPolling()
    {
        var adapter = new FakeGatewayAdapter();
        var registrar = new LocalExilePetMtxRegistrar();
        var backend = new SimulatedMaxwellWorldBackend();
        MaxwellServerMod? serverMod = null;
        using var gatewayHost = LocalExileGatewayHost.Create(
            new FakeModContext("maxwell"),
            builder => builder
                .WithAdapter(adapter)
                .WithBus(attach: false)
                .OnCosmeticChanged(async evt =>
                {
                    if (evt.IsSkillCosmetic)
                        return;

                    var player = (FakeGatewayPlayer)evt.Player;
                    registrar.ObserveWornCosmetics(evt.ConnectionId, evt.CharacterName, player.Worn, player.Pets);
                    await serverMod!.FlushPendingCosmeticSyncAsync();
                    await serverMod.ReconcileCosmeticAndPetStateAsync(
                        evt.ConnectionId,
                        evt.CharacterName,
                        registrar.IsEquipped(evt.ConnectionId, MaxwellServerMod.MaxwellPetDefinition.MtxTypeHash),
                        [.. player.Pets.Select(p => p.EntityId)]);
                }));

        serverMod = new MaxwellServerMod(gatewayHost.Bridge, backend, registrar);
        using var ownedServerMod = serverMod;
        serverMod.Attach();

        var player = new FakeGatewayPlayer("127.0.0.1:50001#1", "CatLover");
        adapter.LastPlayer = player;

        // 1. Skill cosmetic change (IsSkillCosmetic == true) is ignored cleanly.
        player.Worn.Add(new WornCosmeticSlot(CustomPetMtxDefinition.PetSlot1, MaxwellProtocol.MtxRecordId, MaxwellProtocol.MtxTypeHash));
        player.Pets.Add(new SpawnedPetSnapshot(0x8801u, 1));
        await adapter.RaiseCosmeticChangedAsync(new GatewayCosmeticChangeContext(
            player.ConnectionId, player.CharacterName, player,
            Slot: 0, RecordId: 123, MtxTypeHash: 0x1111, Option: 0, GemHash: 0x2222, IsSkillCosmetic: true));
        Assert.False(registrar.IsEquipped(player.ConnectionId, MaxwellServerMod.MaxwellPetDefinition.MtxTypeHash));

        // 2. Non-skill Pet slot cosmetic change triggers event-driven equip without AreaTick!
        await adapter.RaiseCosmeticChangedAsync(new GatewayCosmeticChangeContext(
            player.ConnectionId, player.CharacterName, player,
            Slot: CustomPetMtxDefinition.PetSlot1, RecordId: MaxwellProtocol.MtxRecordId, MtxTypeHash: MaxwellProtocol.MtxTypeHash));
        await serverMod.FlushPendingCosmeticSyncAsync();

        Assert.True(registrar.IsEquipped(player.ConnectionId, MaxwellServerMod.MaxwellPetDefinition.MtxTypeHash));
        var equipSync = serverMod.BuildSync(player.CharacterName, "");
        Assert.True(equipSync.IsSummoned);
        Assert.True(equipSync.MusicPlaying);
        Assert.Equal(0x8801u, equipSync.PetEntityId);

        // 3. Unequip via CosmeticChanged triggers clean dismiss + music stop without AreaTick!
        player.Worn.Clear();
        player.Pets.Clear();
        await adapter.RaiseCosmeticChangedAsync(new GatewayCosmeticChangeContext(
            player.ConnectionId, player.CharacterName, player,
            Slot: CustomPetMtxDefinition.PetSlot1, RecordId: 0, MtxTypeHash: 0));
        await serverMod.FlushPendingCosmeticSyncAsync();

        Assert.False(registrar.IsEquipped(player.ConnectionId, MaxwellServerMod.MaxwellPetDefinition.MtxTypeHash));
        var unequipSync = serverMod.BuildSync(player.CharacterName, "");
        Assert.False(unequipSync.IsSummoned);
        Assert.False(unequipSync.MusicPlaying);
        Assert.Equal(0u, unequipSync.PetEntityId);
    }

    [Fact]
    public async Task MaxwellClientMod_020Lifecycle_SetsTextBoxMaxLength6_MarshalsBackgroundPackets_RecoversFromWindowClose_AndUnloadsCleanly()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "nm-maxwell-020-" + Guid.NewGuid().ToString("N"));
        string modDir = Path.Combine(tempRoot, "mod");
        string shadowDir = Path.Combine(tempRoot, "shadow");
        Directory.CreateDirectory(modDir);

        try
        {
            string repoRoot = FindRepoRoot();
            string exampleRoot = Path.Combine(repoRoot, "examples", "DualMod.MaxwellPet");
            File.Copy(Path.Combine(exampleRoot, "mod.json"), Path.Combine(modDir, "mod.json"));
            File.Copy(FindCompiledAssembly(exampleRoot, "Client", "maxwell.Client.dll"), Path.Combine(modDir, "maxwell.Client.dll"));

            using var simBridge = new SimulatedNativeBridge(ClientBuildId.Build3293d);
            using var clientHost = new ClientHostRuntime(simBridge, new ClientHostOptions
            {
                TransportKind = NetTransportKind.HostRuleset,
                HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId,
                ShadowCopyRoot = shadowDir
            });
            clientHost.Start();

            string uiSource = File.ReadAllText(Path.Combine(exampleRoot, "client", "Metadata", "UI", "Mods", "Maxwell", "MaxwellPanel.ui"));
            clientHost.FileOverlayService.RegisterVirtualTextFile(MaxwellProtocol.PanelUiPath, uiSource);
            clientHost.FileOverlayService.RegisterVirtualFile(MaxwellProtocol.MusicOggPath, "OggS_TEST"u8.ToArray());

            var serverBridge = new ClientBridgeHost(new LoopbackSink(simBridge), new ClientBridgeOptions
            {
                DefaultTransport = NetTransportKind.HostRuleset,
                HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId
            });
            simBridge.OnOutgoingRulesetPacket = (rulesetId, inner) =>
            {
                byte[] body = MessageCodec.EncodeRulesetBody(rulesetId, inner.Span);
                serverBridge.HandleIncomingPacketAsync("maxwell-020", IOpcodeRegistry.ClientRulesetOpcode, body).GetAwaiter().GetResult();
            };

            await RunLifecycleScenarioAsync(simBridge, clientHost, serverBridge, modDir);
            var unloadResult = await clientHost.Loader.UnloadModAsync(MaxwellProtocol.ModId);
            Assert.True(unloadResult.WasLoaded);
            Assert.True(unloadResult.UnloadedCleanly);
            Assert.Equal(0, clientHost.Audio.ActiveChannelCount);
            Assert.Null(clientHost.Ui.GetWindow(MaxwellProtocol.PanelWindowId));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task RunLifecycleScenarioAsync(
        SimulatedNativeBridge simBridge,
        ClientHostRuntime clientHost,
        ClientBridgeHost serverBridge,
        string modDir)
    {
        clientHost.Loader.LoadModFromDirectory(modDir);
        clientHost.SendHandshake();
        simBridge.SimulateFrame(1f / 60f);

        var openPanel = new OpenMaxwellPanel(
            "CatLover",
            new MaxwellStateSync(
                CharacterName: "CatLover",
                IsSummoned: true,
                PetEntityId: 0x1234,
                PetSlot: CustomPetMtxDefinition.PetSlot1,
                SpinSpeedDegPerSec: 180f,
                FloatHeight: 15f,
                MusicPlaying: true,
                MusicMuted: false,
                MusicVolume: 0.75f,
                DanceCount: 0,
                StatusMessage: "Ready"));

        // Deliver OpenMaxwellPanel from a background thread (IsOnGameThread == false) then drain frame.
        await Task.Run(async () =>
        {
            Assert.False(simBridge.IsOnGameThread);
            await serverBridge.SendAsync("maxwell-020", MaxwellProtocol.ChannelName, openPanel);
        });
        simBridge.SimulateFrame(1f / 60f);

        IUiWindow? win1 = clientHost.Ui.GetWindow(MaxwellProtocol.PanelWindowId);
        Assert.NotNull(win1);
        Assert.True(win1!.IsOpen);
        Assert.True(win1.Visible);
        Assert.Equal(6, win1.Require<ITextBox>("spin_box").MaxLength);
        Assert.Equal(6, win1.Require<ITextBox>("float_box").MaxLength);
        Assert.Equal(1, clientHost.Audio.ActiveChannelCount);

        // Close window -> Window.Closed clears stale handle without throwing on next sync/re-open.
        win1.Close();
        Assert.False(win1.IsOpen);

        await serverBridge.SendAsync("maxwell-020", MaxwellProtocol.ChannelName, openPanel with
        {
            InitialState = openPanel.InitialState with { SpinSpeedDegPerSec = 240f }
        });

        IUiWindow? win2 = clientHost.Ui.GetWindow(MaxwellProtocol.PanelWindowId);
        Assert.NotNull(win2);
        Assert.NotSame(win1, win2);
        Assert.True(win2!.IsOpen);
        Assert.Equal("240", win2.Require<ITextBox>("spin_box").Text);
        Assert.Equal(6, win2.Require<ITextBox>("spin_box").MaxLength);
        Assert.Equal(6, win2.Require<ITextBox>("float_box").MaxLength);
    }

    private sealed class FakeModContext(string id)
    {
        public FakeManifest Manifest { get; } = new(id);
        public Action<string> Log { get; } = _ => { };
        public sealed record FakeManifest(string Id);
    }

    private sealed class FakeGatewayPlayer(string connectionId, string characterName)
    {
        public string ConnectionId { get; } = connectionId;
        public string CharacterName { get; } = characterName;
        public List<WornCosmeticSlot> Worn { get; } = [];
        public List<SpawnedPetSnapshot> Pets { get; } = [];
    }

    private sealed class FakeGatewayAdapter : ILocalExileGatewayAdapter
    {
        private Func<GatewayCosmeticChangeContext, Task>? _onCosmeticChanged;
        public object? LastPlayer { get; set; }

        public string? TryGetOwnerId(object modContext) => ((FakeModContext)modContext).Manifest.Id;
        public Action<string>? TryGetLogger(object modContext) => ((FakeModContext)modContext).Log;
        public string GetConnectionId(object player) => ((FakeGatewayPlayer)player).ConnectionId;
        public string GetCharacterName(object player) => ((FakeGatewayPlayer)player).CharacterName;
        public bool IsPlayerClosed(object player) => false;
        public bool SupportsCoreClientChannel => false;
        public object? TryGetClientChannel(object player) => null;

        public Task SendPacketAsync(object player, ushort opcode, ReadOnlyMemory<byte> payload, string? log, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendChatReplyAsync(object player, string text, CancellationToken cancellationToken) => Task.CompletedTask;
        public IDisposable SubscribeRuleset(string ownerId, byte rulesetId, Func<object, string, ReadOnlyMemory<byte>, Task<bool>> onInnerPayload) => NoOp.Instance;
        public IDisposable SubscribeClientCommand(string ownerId, Func<object, string, string, Task<bool>> onCommand) => NoOp.Instance;
        public IDisposable SubscribePacket(string ownerId, ushort opcode, Func<object, byte[], Task<bool>> onPacket) => NoOp.Instance;
        public IDisposable SubscribeCommand(string ownerId, string commandName, string usage, Func<object, string[], Task> onCommand) => NoOp.Instance;
        public IDisposable SubscribePlayerEntered(string ownerId, Func<object, Task> onEntered) => NoOp.Instance;
        public IDisposable SubscribePlayerLeaving(string ownerId, Func<object, string, Task> onLeaving) => NoOp.Instance;
        public IDisposable? SubscribeClientReady(string ownerId, Func<object, Task> onReady) => NoOp.Instance;
        public IDisposable? SubscribeClientMessage(string ownerId, Func<GatewayClientMessageContext, Task<bool>> onMessage) => NoOp.Instance;

        public IDisposable? SubscribeCosmeticChanged(string ownerId, Func<GatewayCosmeticChangeContext, Task> onCosmeticChanged)
        {
            _onCosmeticChanged = onCosmeticChanged;
            return NoOp.Instance;
        }

        public Task RaiseCosmeticChangedAsync(GatewayCosmeticChangeContext evt) =>
            _onCosmeticChanged is null ? Task.CompletedTask : _onCosmeticChanged(evt);

        private sealed class NoOp : IDisposable
        {
            public static readonly NoOp Instance = new();
            public void Dispose() { }
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

        public Task SendChatReplyAsync(string connectionId, string text, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

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

    private static string FindCompiledAssembly(string exampleRoot, string subDir, string fileName)
    {
        string binDir = Path.Combine(exampleRoot, subDir, "bin");
        foreach (string config in new[] { "Release", "Debug" })
        {
            string candidate = Path.Combine(binDir, config, "net10.0", fileName);
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException($"Could not locate {fileName} under {binDir}");
    }
}
