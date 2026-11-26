using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using DualMod.SiphonOrb.Server;
using DualMod.SiphonOrb.Shared;
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

namespace LocalExile.Client.SiphonOrb.Tests;

internal static class RoslynEntryCompileHelper
{
    public static void CompileAndAssertSuccess(
        string gatewayBinDir,
        string assemblyName,
        string source,
        params string[] extraReferencePaths)
    {
        var roslynAlc = new AssemblyLoadContext("RoslynEntryProbe-" + Guid.NewGuid().ToString("N"), isCollectible: true);
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

public sealed class Siphon020LifecycleAndTransactionsTests
{
    private static readonly int[] ModValues55 = [55];
    private static readonly int[] ModValues35 = [35];
    private static readonly byte[] WornTestRows = [54, 78, 102];
    [Fact]
    public async Task SiphonClientMod_020Lifecycle_MarshalsBackgroundPackets_RecoversFromWindowClose_AndUnloadsCleanlyWithoutSpuriousCancel()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "nm-siphon-020-" + Guid.NewGuid().ToString("N"));
        string modDir = Path.Combine(tempRoot, "mod");
        string shadowDir = Path.Combine(tempRoot, "shadow");
        Directory.CreateDirectory(modDir);

        try
        {
            string repoRoot = FindRepoRoot();
            string exampleRoot = Path.Combine(repoRoot, "examples", "DualMod.SiphonOrb");
            File.Copy(Path.Combine(exampleRoot, "mod.json"), Path.Combine(modDir, "mod.json"));
            File.Copy(FindCompiledAssembly(exampleRoot, "Client", "siphon.Client.dll"), Path.Combine(modDir, "siphon.Client.dll"));

            using var simBridge = new SimulatedNativeBridge(ClientBuildId.Build3293d);
            using var clientHost = new ClientHostRuntime(simBridge, new ClientHostOptions
            {
                TransportKind = NetTransportKind.HostRuleset,
                HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId,
                ShadowCopyRoot = shadowDir
            });
            clientHost.Start();

            string uiSource = File.ReadAllText(Path.Combine(exampleRoot, "client", "Metadata", "UI", "Mods", "Siphon", "SiphonPicker.ui"));
            clientHost.FileOverlayService.RegisterVirtualTextFile(SiphonProtocol.PickerUiPath, uiSource);

            var sentCancels = new List<string>();
            var serverBridge = new ClientBridgeHost(new LoopbackSink(simBridge), new ClientBridgeOptions
            {
                DefaultTransport = NetTransportKind.HostRuleset,
                HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId
            });
            serverBridge.On<CancelSiphonPicker>(SiphonProtocol.ChannelName, (_, req) => sentCancels.Add(req.SessionToken));

            simBridge.OnOutgoingRulesetPacket = (rulesetId, inner) =>
            {
                byte[] body = MessageCodec.EncodeRulesetBody(rulesetId, inner.Span);
                serverBridge.HandleIncomingPacketAsync("siphon-020", IOpcodeRegistry.ClientRulesetOpcode, body).GetAwaiter().GetResult();
            };

            await RunLifecycleScenarioAsync(simBridge, clientHost, serverBridge, modDir, sentCancels);
            var unloadResult = await clientHost.Loader.UnloadModAsync(SiphonProtocol.ModId);
            Assert.True(unloadResult.WasLoaded);
            Assert.True(unloadResult.UnloadedCleanly);
            Assert.Null(clientHost.Ui.GetWindow(SiphonProtocol.PickerWindowId));
            // Window.Closed sent cancel on explicit close ("tok-1"), NOT during Unload() ("tok-2").
            Assert.Equal(["tok-1"], sentCancels);
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
        string modDir,
        List<string> sentCancels)
    {
        clientHost.Loader.LoadModFromDirectory(modDir);
        clientHost.SendHandshake();

        // Capture a known game thread via SimulateFrame so background Task.Run has IsOnGameThread == false.
        simBridge.SimulateFrame(1f / 60f);

        var openMsg1 = new OpenSiphonPicker(
            SessionToken: "tok-1",
            Mode: SiphonPickerMode.Steal,
            CurrencyInventoryId: 1,
            CurrencyItemId: 10,
            TargetInventoryId: 1,
            TargetItemId: 20,
            TargetItemName: "Doom Carapace",
            TargetBaseType: "Astral Plate",
            TargetRarity: "Rare",
            StoredAffix: null,
            Affixes:
            [
                new SiphonAffixOption(0, "IncreasedLife10", "Prime", "+99 to maximum Life", true, false, "IncreasedLife", 1, [99], true, null)
            ],
            PromptText: "Select modifier");

        await Task.Run(async () =>
        {
            Assert.False(simBridge.IsOnGameThread);
            await serverBridge.SendAsync("siphon-020", SiphonProtocol.ChannelName, openMsg1);
        });
        simBridge.SimulateFrame(1f / 60f);

        IUiWindow? win1 = clientHost.Ui.GetWindow(SiphonProtocol.PickerWindowId);
        Assert.NotNull(win1);
        Assert.True(win1!.IsOpen);
        Assert.True(win1.Visible);

        // Explicit window.Close() must fire Window.Closed -> send CancelSiphonPicker("tok-1") and drop stale handle.
        win1.Close();
        Assert.False(win1.IsOpen);
        Assert.Equal(["tok-1"], sentCancels);

        // Re-opening after Close() must load a fresh open window without ObjectDisposedException / stale handle.
        var openMsg2 = openMsg1 with { SessionToken = "tok-2" };
        await serverBridge.SendAsync("siphon-020", SiphonProtocol.ChannelName, openMsg2);

        IUiWindow? win2 = clientHost.Ui.GetWindow(SiphonProtocol.PickerWindowId);
        Assert.NotNull(win2);
        Assert.NotSame(win1, win2);
        Assert.True(win2!.IsOpen);
        Assert.True(win2.Visible);
    }

    [Fact]
    public async Task LocalExileGatewayTransactions_WornRows54_78_102_AndCustomNamePreservation_BehaveAccurately()
    {
        string? gatewayBin = FindLocalExileGatewayBinDir();
        if (gatewayBin is null)
            return;

        string dataDir = "/home/nam/Git/LocalExile/data";
        string? prevClientDb = Environment.GetEnvironmentVariable("Game_CLIENT_DB");
        string? prevWorldDb = Environment.GetEnvironmentVariable("Game_WORLD_DB");
        string? prevSqlDir = Environment.GetEnvironmentVariable("Game_SQL_DIR");
        Environment.SetEnvironmentVariable("Game_CLIENT_DB", Path.Combine(dataDir, "client.db"));
        Environment.SetEnvironmentVariable("Game_WORLD_DB", Path.Combine(dataDir, "world.db"));
        Environment.SetEnvironmentVariable("Game_SQL_DIR", "/home/nam/Git/LocalExile/sql");

        var gatewayAlc = new IsolatedGatewayAlc(Path.Combine(gatewayBin, "GameServer.Gateway.dll"));
        try
        {
            Assembly coreAsm = gatewayAlc.LoadFromAssemblyPath(Path.Combine(gatewayBin, "GameServer.Core.dll"));
            Assembly gatewayAsm = gatewayAlc.LoadFromAssemblyPath(Path.Combine(gatewayBin, "GameServer.Gateway.dll"));

            // 1. Verify IsWorn rows 54, 78, 102 and non-worn MainInventory row 0 via real PlayerInventories & ItemCatalog.
            Type programType = gatewayAsm.GetType("Program")!;
            Type statCatalogType = gatewayAsm.GetType("StatCatalog")!;
            Type playerInvType = gatewayAsm.GetType("PlayerInventories")!;
            Type itemCatalogType = gatewayAsm.GetType("ItemCatalog")!;
            Type modCatalogType = gatewayAsm.GetType("ModCatalog")!;
            Type itemType = coreAsm.GetType("GameServer.Core.Protocol.Instance.Item")!;
            Type itemModType = coreAsm.GetType("GameServer.Core.Protocol.Instance.ItemMod")!;
            Type modSectionType = coreAsm.GetType("GameServer.Core.Protocol.Instance.ModSection")!;
            Type rarityType = coreAsm.GetType("GameServer.Core.Protocol.Instance.ItemRarity")!;

            object statCatalog = Activator.CreateInstance(statCatalogType)!;
            if (programType.GetField("Stats", BindingFlags.Public | BindingFlags.Static) is { } statsField)
                statsField.SetValue(null, statCatalog);
            else
                programType.GetProperty("Stats", BindingFlags.Public | BindingFlags.Static)?.SetValue(null, statCatalog);

            gatewayAsm.GetType("ClusterJewels")!.GetMethod("LoadGameData")!.Invoke(null, null);
            ConstructorInfo catalogCtor = itemCatalogType.GetConstructors()[0];
            ParameterInfo[] catalogPs = catalogCtor.GetParameters();
            object?[] catalogArgs = new object?[catalogPs.Length];
            catalogArgs[0] = dataDir;
            for (int i = 1; i < catalogPs.Length; i++)
                catalogArgs[i] = catalogPs[i].HasDefaultValue ? catalogPs[i].DefaultValue : null;
            object itemCatalog = catalogCtor.Invoke(catalogArgs);
            object modCatalog = itemCatalogType.GetProperty("Mods")!.GetValue(itemCatalog)!;

            var registrar = new LocalExileCurrencyRegistrar();
            registrar.Register(SiphonServerMod.EmptyOrbDefinition, _ => Task.FromResult(true));
            registrar.Register(SiphonServerMod.ChargedOrbDefinition, _ => Task.FromResult(true));
            registrar.ApplyToLiveCatalog(itemCatalog);

            MethodInfo byHash = itemCatalogType.GetMethod("ByHash", [typeof(uint)])!;
            object emptyBase = byHash.Invoke(itemCatalog, [SiphonServerMod.EmptyOrbDefinition.TypeHash])!;
            object ringBase = itemCatalogType.GetMethod("Find", [typeof(string)])!.Invoke(itemCatalog, ["Iron Greaves"])
                ?? throw new InvalidOperationException("Iron Greaves not found.");

            object modLife = modCatalogType.GetMethod("ById", [typeof(string)])!.Invoke(modCatalog, ["IncreasedLife7"])!;
            object modCold = modCatalogType.GetMethod("ById", [typeof(string)])!.Invoke(modCatalog, ["ColdResist1"])!;
            object modFire = modCatalogType.GetMethod("ById", [typeof(string)])!.Invoke(modCatalog, ["FireResist1"])!;
            ushort lifeHash = (ushort)modLife.GetType().GetProperty("Hash")!.GetValue(modLife)!;
            ushort coldHash = (ushort)modCold.GetType().GetProperty("Hash")!.GetValue(modCold)!;
            ushort fireHash = (ushort)modFire.GetType().GetProperty("Hash")!.GetValue(modFire)!;

            object inv = Activator.CreateInstance(playerInvType)!;
            MethodInfo addMethod = playerInvType.GetMethod("Add", [typeof(byte), typeof(byte), typeof(byte), itemType])!;
            MethodInfo invIdMethod = playerInvType.GetMethod("InventoryId", [typeof(byte)])!;

            object createGear(string customName, ushort secondModHash)
            {
                object gear = CreateCatalogItem(itemCatalogType, ringBase);
                gear = LocalExileItemAffixService.WithGatewayItemCustomName(gear, customName);
                itemType.GetProperty("Rarity")!.GetSetMethod(true)!.Invoke(gear, [Enum.ToObject(rarityType, 2)]);
                itemType.GetProperty("Identified")!.GetSetMethod(true)!.Invoke(gear, [true]);
                itemType.GetProperty("ItemLevel")!.GetSetMethod(true)!.Invoke(gear, [(byte)84]);

                object explicitSec = Enum.ToObject(modSectionType, (byte)ItemModSectionKind.Explicit);
                object mod1 = itemModType.GetConstructors()[0].Invoke([explicitSec, lifeHash, ModValues55]);
                object mod2 = itemModType.GetConstructors()[0].Invoke([explicitSec, secondModHash, ModValues35]);
                IList modList = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(itemModType))!;
                modList.Add(mod1);
                modList.Add(mod2);
                itemType.GetProperty("Mods")!.GetSetMethod(true)!.Invoke(gear, [modList]);
                return gear;
            }

            // Check worn rows 54, 78, 102
            foreach (byte wornRow in WornTestRows)
            {
                object placed = addMethod.Invoke(inv, [wornRow, (byte)0, (byte)0, createGear("Worn Gear", coldHash)])!;
                uint placedId = (uint)placed.GetType().GetProperty("Id")!.GetValue(placed)!;
                uint invId = Convert.ToUInt32(invIdMethod.Invoke(null, [wornRow]), System.Globalization.CultureInfo.InvariantCulture);
                GatewayItemLocation? loc = LocalExileGatewayTransactions.LocateGatewayItem(inv, null, invId, placedId);
                Assert.NotNull(loc);
                Assert.True(loc!.IsWorn, $"Expected row {wornRow} to report IsWorn == true");
            }

            // Add empty orb + non-worn source & destination gear into MainInventory (row 0)
            object emptyOrb = CreateCatalogItem(itemCatalogType, emptyBase);
            itemType.GetProperty("Identified")!.GetSetMethod(true)!.Invoke(emptyOrb, [true]);
            object placedOrb = addMethod.Invoke(inv, [(byte)0, (byte)0, (byte)0, emptyOrb])!;
            uint orbId = (uint)placedOrb.GetType().GetProperty("Id")!.GetValue(placedOrb)!;

            object placedSource = addMethod.Invoke(inv, [(byte)0, (byte)2, (byte)0, createGear("Siphon Heirloom", coldHash)])!;
            uint sourceId = (uint)placedSource.GetType().GetProperty("Id")!.GetValue(placedSource)!;
            object placedDest = addMethod.Invoke(inv, [(byte)0, (byte)5, (byte)0, createGear("Imbue Vessel", fireHash)])!;
            uint destId = (uint)placedDest.GetType().GetProperty("Id")!.GetValue(placedDest)!;
            uint mainInvId = Convert.ToUInt32(invIdMethod.Invoke(null, [(byte)0]), System.Globalization.CultureInfo.InvariantCulture);

            GatewayItemLocation? bagLoc = LocalExileGatewayTransactions.LocateGatewayItem(inv, null, mainInvId, sourceId);
            Assert.NotNull(bagLoc);
            Assert.False(bagLoc!.IsWorn);

            // Steal affix with chargedCustomName: null -> preserves source's CustomName ("Siphon Heirloom") and leaves Charged Orb CustomName null
            var affixSvc = new LocalExileItemAffixService();
            GatewayStealResult steal = await affixSvc.StealAffixOnGatewayItem(
                player: null,
                itemCatalog: itemCatalog,
                playerInventories: inv,
                accountStash: null,
                currencyInventoryId: mainInvId,
                currencyItemId: orbId,
                targetInventoryId: mainInvId,
                targetItemId: sourceId,
                affixIndex: 0,
                chargedCurrencyDef: SiphonServerMod.ChargedOrbDefinition,
                chargedCustomName: null,
                sendPackets: false);
            Assert.True(steal.Success, steal.Error);

            GatewayItemLocation updatedSourceLoc = LocalExileGatewayTransactions.LocateGatewayItem(inv, null, mainInvId, sourceId)!;
            Assert.Equal("Siphon Heirloom", LocalExileItemAffixService.GetGatewayItemCustomName(updatedSourceLoc.Item));

            GatewayItemLocation chargedLoc = LocalExileGatewayTransactions.LocateGatewayItem(inv, null, mainInvId, steal.CreatedChargedItemId)!;
            Assert.Null(LocalExileItemAffixService.GetGatewayItemCustomName(chargedLoc.Item));

            // Replace affix on destination item -> preserves destination's CustomName ("Imbue Vessel")
            GatewayReplaceResult replace = await affixSvc.ReplaceAffixOnGatewayItem(
                player: null,
                itemCatalog: itemCatalog,
                playerInventories: inv,
                accountStash: null,
                currencyInventoryId: mainInvId,
                currencyItemId: steal.CreatedChargedItemId,
                targetInventoryId: mainInvId,
                targetItemId: destId,
                replaceIndex: 0,
                sendPackets: false);
            Assert.True(replace.Success, replace.Error);

            GatewayItemLocation updatedDestLoc = LocalExileGatewayTransactions.LocateGatewayItem(inv, null, mainInvId, destId)!;
            Assert.Equal("Imbue Vessel", LocalExileItemAffixService.GetGatewayItemCustomName(updatedDestLoc.Item));
        }
        finally
        {
            Environment.SetEnvironmentVariable("Game_CLIENT_DB", prevClientDb);
            Environment.SetEnvironmentVariable("Game_WORLD_DB", prevWorldDb);
            Environment.SetEnvironmentVariable("Game_SQL_DIR", prevSqlDir);
            gatewayAlc.Unload();
        }
    }

    private sealed class IsolatedGatewayAlc(string gatewayDllPath) : AssemblyLoadContext("SiphonGatewayProbe-" + Guid.NewGuid().ToString("N"), isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(gatewayDllPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.Equals(assemblyName.Name, "LocalExile.Client.ServerBridge", StringComparison.OrdinalIgnoreCase))
                return typeof(ClientBridgeHost).Assembly;
            if (string.Equals(assemblyName.Name, "LocalExile.Client.Sdk", StringComparison.OrdinalIgnoreCase))
                return typeof(SdkVersion).Assembly;

            string? path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is not null ? LoadFromAssemblyPath(path) : null;
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is not null ? LoadUnmanagedDllFromPath(path) : nint.Zero;
        }
    }

    private static object CreateCatalogItem(Type itemCatalogType, object baseType)
    {
        MethodInfo createMethod = itemCatalogType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == "Create" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == baseType.GetType());
        ParameterInfo[] ps = createMethod.GetParameters();
        object?[] args = new object?[ps.Length];
        args[0] = baseType;
        for (int i = 1; i < ps.Length; i++)
            args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
        return createMethod.Invoke(null, args)!;
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

    private static string? FindLocalExileGatewayBinDir()
    {
        foreach (string config in new[] { "Release", "Debug" })
        {
            string candidate = Path.Combine("/home/nam/Git/LocalExile/src/GameServer.Gateway/bin", config, "net10.0");
            if (File.Exists(Path.Combine(candidate, "GameServer.Gateway.dll")) &&
                Directory.Exists("/home/nam/Git/LocalExile/data"))
            {
                return candidate;
            }
        }
        return null;
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
