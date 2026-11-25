using System.Reflection;
using System.Reflection.Emit;
using LocalExile.Client.Host;
using LocalExile.Client.Host.Loading;
using LocalExile.Client.Host.Native;
using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Raw;
using Xunit;

namespace LocalExile.Client.Tests;

public sealed class ModLoaderAndIsolationTests
{
    [Fact]
    public async Task CollectibleAlc_ShadowCopyHotReloadAndWeakRefUnloadVerification()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"nm-mod-hotreload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string dllPath = Path.Combine(tempDir, "sample.Client.dll");
        string manifestPath = Path.Combine(tempDir, "mod.json");

        try
        {
            File.WriteAllText(manifestPath, """
                {
                  "id": "sample",
                  "name": "Sample Mod",
                  "version": "1.0.0",
                  "nativeClient": {
                    "sdkVersionRange": ">=0.1.0-poc <0.2.0",
                    "assembly": "sample.Client.dll",
                    "rawAccess": false
                  }
                }
                """);

            // Emit v1 mod DLL on disk setting stat "sample_version" = 1 on Load().
            EmitDynamicClientModAssembly(dllPath, "SampleModV1", statKey: "sample_version", statValue: 1);

            using var bridge = new SimulatedNativeBridge();
            using var host = new ClientHostRuntime(bridge);
            host.Start();

            LoadedClientMod loadedV1 = host.Loader.LoadModFromDirectory(tempDir);
            Assert.Equal(1, loadedV1.Generation);
            Assert.Equal(1, host.Stats.GetStat("sample_version"));

            // Because the loader uses shadow-copy, overwriting the original DLL on disk MUST succeed while loaded!
            EmitDynamicClientModAssembly(dllPath, "SampleModV2", statKey: "sample_version", statValue: 2);

            (ModUnloadResult unloadV1, LoadedClientMod loadedV2) = await host.Loader.ReloadModAsync("sample");
            Assert.True(unloadV1.WasLoaded);
            Assert.True(unloadV1.UnloadedCleanly);
            Assert.Equal(2, loadedV2.Generation);
            Assert.Equal(2, host.Stats.GetStat("sample_version"));

            // Final unload verifies clean GC collection of the v2 collectible AssemblyLoadContext.
            ModUnloadResult unloadV2 = await host.Loader.UnloadModAsync("sample");
            Assert.True(unloadV2.WasLoaded);
            Assert.True(unloadV2.UnloadedCleanly);
            Assert.Null(host.Loader.GetMod("sample"));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CollectibleAlc_TypedNetSendAndReceiveWithModDefinedMessage_UnloadsCleanly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"nm-mod-json-unload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string dllPath = Path.Combine(tempDir, "netmod.Client.dll");
        string manifestPath = Path.Combine(tempDir, "mod.json");

        try
        {
            File.WriteAllText(manifestPath, """
                {
                  "id": "netmod",
                  "name": "Networked Collectible Mod",
                  "version": "1.0.0",
                  "nativeClient": {
                    "sdkVersionRange": ">=0.1.0-poc <0.2.0",
                    "assembly": "netmod.Client.dll"
                  }
                }
                """);

            // Emit a collectible mod DLL that defines its own message type ModPing { int Value; }
            // and both sends (Serialize) and receives (Deserialize) it through context.Net!
            EmitNetworkedClientModAssembly(dllPath, "NetModAsm");

            using var bridge = new SimulatedNativeBridge();
            byte[]? capturedWireEnvelope = null;
            bridge.OnOutgoingRulesetPacket = (_, payload) => capturedWireEnvelope = payload.ToArray();

            using var host = new ClientHostRuntime(bridge);
            host.Start();

            LoadedClientMod loaded = host.Loader.LoadModFromDirectory(tempDir);
            Assert.Equal("netmod", loaded.Manifest.Id);
            Assert.NotNull(capturedWireEnvelope);

            // Echo the exact encoded ModPing envelope back into the client host so DeserializePayload<ModPing> runs!
            Assert.True(bridge.SimulateIncomingRulesetPacket(Sdk.Extend.IRulesetRegistry.DefaultHostChannelRulesetId, capturedWireEnvelope!));
            Assert.Equal(77, host.Stats.GetStat("netmod_received"));

            // Unloading MUST collect the mod's AssemblyLoadContext (JsonSerializerOptions must NOT pin ModPing!).
            ModUnloadResult unload = await host.Loader.UnloadModAsync("netmod");
            Assert.True(unload.WasLoaded);
            Assert.True(unload.UnloadedCleanly);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RawAccessPermission_BlocksUnauthorizedModAndAllowsAuthorizedMod()
    {
        using var bridge = new SimulatedNativeBridge();
        nint mem = bridge.AllocateSimulatedMemory(64);
        bridge.RegisterNativeStub("frame.state_stack_pass_a_hook", args => args.Length >= 2 ? args[0] + args[1] : 0);

        using var host = new ClientHostRuntime(bridge);
        host.Start();

        var deniedManifest = new ClientModManifest("no-raw", "No Raw", "1.0.0", RawAccess: false);
        var allowedManifest = new ClientModManifest("with-raw", "With Raw", "1.0.0", RawAccess: true, RawAccessJustification: "Unit test raw read/write");

        Assert.Throws<RawAccessDeniedException>(() =>
            host.Loader.LoadInProcessMod(deniedManifest, new RawProbingMod(mem)));

        var rawMod = new RawProbingMod(mem);
        host.Loader.LoadInProcessMod(allowedManifest, rawMod);

        Assert.Equal(0x12345678, rawMod.ReadBackValue);
        Assert.Equal((nint)30, rawMod.NativeCallResult);
        Assert.Equal(1, bridge.ActiveHookCount);

        // Unloading the mod disposes its tracked hook automatically.
        await host.Loader.UnloadModAsync("with-raw");
        Assert.Equal(0, bridge.ActiveHookCount);
    }

    [Fact]
    public void SdkVersionRange_RejectsIncompatibleClientMod()
    {
        using var bridge = new SimulatedNativeBridge();
        using var host = new ClientHostRuntime(bridge);

        var incompatible = new ClientModManifest("future-mod", "Future", "1.0.0", SdkVersionRange: ">=2.0.0");
        Assert.Throws<InvalidOperationException>(() =>
            host.Loader.LoadInProcessMod(incompatible, new HealthyCountingMod()));
    }

    [Fact]
    public void FaultIsolationWall_DisablesRepeatedlyThrowingModWhileHealthyModKeepsTicking()
    {
        using var bridge = new SimulatedNativeBridge();
        using var host = new ClientHostRuntime(bridge, new ClientHostOptions { MaxModFaults = 3 });
        host.Start();

        var healthy = new HealthyCountingMod();
        var faulty = new AlwaysThrowingMod();
        var faultsObserved = new List<(string ModId, bool Disabled)>();
        host.Loader.ModFaulted += (id, _, disabled) => faultsObserved.Add((id, disabled));

        host.Loader.LoadInProcessMod(new ClientModManifest("healthy", "Healthy", "1.0.0"), healthy);
        host.Loader.LoadInProcessMod(new ClientModManifest("faulty", "Faulty", "1.0.0"), faulty);

        for (int i = 0; i < 6; i++)
        {
            host.TickFrame(0.016f);
        }

        // Healthy mod ran all 6 frames uninterrupted.
        Assert.Equal(6, healthy.FrameTicks);

        // Faulty mod was caught 3 times and disabled on the 3rd fault; frames 4-6 skipped it completely.
        LoadedClientMod faultyState = host.Loader.GetMod("faulty")!;
        Assert.True(faultyState.IsDisabled);
        Assert.Equal(3, faultyState.FaultCount);
        Assert.Equal(3, faultsObserved.Count);
        Assert.False(faultsObserved[0].Disabled);
        Assert.False(faultsObserved[1].Disabled);
        Assert.True(faultsObserved[2].Disabled);
        Assert.True(faulty.TrackedResourceDisposed);
    }

    private static void EmitDynamicClientModAssembly(string outputDllPath, string assemblyName, string statKey, int statValue)
    {
        var ab = new PersistedAssemblyBuilder(new AssemblyName(assemblyName), typeof(object).Assembly);
        ModuleBuilder mb = ab.DefineDynamicModule(assemblyName);
        TypeBuilder tb = mb.DefineType(
            $"{assemblyName}.DynamicMod",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class,
            typeof(object),
            [typeof(IClientMod)]);

        ConstructorBuilder ctor = tb.DefineDefaultConstructor(MethodAttributes.Public);

        MethodInfo loadInterfaceMethod = typeof(IClientMod).GetMethod(nameof(IClientMod.Load))!;
        MethodBuilder loadMethod = tb.DefineMethod(
            nameof(IClientMod.Load),
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
            typeof(void),
            [typeof(IClientContext)]);

        ILGenerator il = loadMethod.GetILGenerator();
        // context.Game.Stats.SetLocalStat(statKey, statValue);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, typeof(IClientContext).GetProperty(nameof(IClientContext.Game))!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, typeof(IGameServices).GetProperty(nameof(IGameServices.Stats))!.GetGetMethod()!);
        il.Emit(OpCodes.Ldstr, statKey);
        il.Emit(OpCodes.Ldc_I4, statValue);
        il.Emit(OpCodes.Callvirt, typeof(IStatsService).GetMethod(nameof(IStatsService.SetLocalStat))!);
        il.Emit(OpCodes.Ret);

        tb.DefineMethodOverride(loadMethod, loadInterfaceMethod);
        tb.CreateType();
        ab.Save(outputDllPath);
    }

    private static void EmitNetworkedClientModAssembly(string outputDllPath, string assemblyName)
    {
        var ab = new PersistedAssemblyBuilder(new AssemblyName(assemblyName), typeof(object).Assembly);
        ModuleBuilder mb = ab.DefineDynamicModule(assemblyName);

        // Define public sealed class ModPing { public int Value { get; set; } } inside the collectible assembly.
        TypeBuilder msgTb = mb.DefineType($"{assemblyName}.ModPing", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class);
        msgTb.DefineDefaultConstructor(MethodAttributes.Public);
        FieldBuilder valField = msgTb.DefineField("_value", typeof(int), FieldAttributes.Private);
        PropertyBuilder valProp = msgTb.DefineProperty("Value", PropertyAttributes.None, typeof(int), null);
        MethodBuilder getVal = msgTb.DefineMethod("get_Value", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, typeof(int), Type.EmptyTypes);
        ILGenerator getIl = getVal.GetILGenerator();
        getIl.Emit(OpCodes.Ldarg_0);
        getIl.Emit(OpCodes.Ldfld, valField);
        getIl.Emit(OpCodes.Ret);
        MethodBuilder setVal = msgTb.DefineMethod("set_Value", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, typeof(void), [typeof(int)]);
        ILGenerator setIl = setVal.GetILGenerator();
        setIl.Emit(OpCodes.Ldarg_0);
        setIl.Emit(OpCodes.Ldarg_1);
        setIl.Emit(OpCodes.Stfld, valField);
        setIl.Emit(OpCodes.Ret);
        valProp.SetGetMethod(getVal);
        valProp.SetSetMethod(setVal);
        Type msgType = msgTb.CreateType();

        // Define public sealed class NetworkedMod : IClientMod
        TypeBuilder modTb = mb.DefineType(
            $"{assemblyName}.NetworkedMod",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class,
            typeof(object),
            [typeof(IClientMod)]);
        modTb.DefineDefaultConstructor(MethodAttributes.Public);
        FieldBuilder statsField = modTb.DefineField("_stats", typeof(IStatsService), FieldAttributes.Private);

        // private void OnPing(ModPing msg) => _stats.SetLocalStat("netmod_received", msg.Value);
        MethodBuilder onPingMethod = modTb.DefineMethod("OnPing", MethodAttributes.Private, typeof(void), [msgType]);
        ILGenerator onPingIl = onPingMethod.GetILGenerator();
        onPingIl.Emit(OpCodes.Ldarg_0);
        onPingIl.Emit(OpCodes.Ldfld, statsField);
        onPingIl.Emit(OpCodes.Ldstr, "netmod_received");
        onPingIl.Emit(OpCodes.Ldarg_1);
        onPingIl.Emit(OpCodes.Callvirt, getVal);
        onPingIl.Emit(OpCodes.Callvirt, typeof(IStatsService).GetMethod(nameof(IStatsService.SetLocalStat))!);
        onPingIl.Emit(OpCodes.Ret);

        MethodInfo loadInterfaceMethod = typeof(IClientMod).GetMethod(nameof(IClientMod.Load))!;
        MethodBuilder loadMethod = modTb.DefineMethod(
            nameof(IClientMod.Load),
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
            typeof(void),
            [typeof(IClientContext)]);

        ILGenerator il = loadMethod.GetILGenerator();
        // _stats = context.Game.Stats;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, typeof(IClientContext).GetProperty(nameof(IClientContext.Game))!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, typeof(IGameServices).GetProperty(nameof(IGameServices.Stats))!.GetGetMethod()!);
        il.Emit(OpCodes.Stfld, statsField);

        // context.Net.On<ModPing>(new Action<ModPing>(this.OnPing), null);
        Type actionOfMsg = typeof(Action<>).MakeGenericType(msgType);
        ConstructorInfo openActionCtor = typeof(Action<>).GetConstructor([typeof(object), typeof(nint)])!;
        ConstructorInfo actionCtor = TypeBuilder.GetConstructor(actionOfMsg, openActionCtor);
        MethodInfo onGeneric = typeof(INetChannel).GetMethod(nameof(INetChannel.On))!.MakeGenericMethod(msgType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, typeof(IClientContext).GetProperty(nameof(IClientContext.Net))!.GetGetMethod()!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldftn, onPingMethod);
        il.Emit(OpCodes.Newobj, actionCtor);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Callvirt, onGeneric);
        il.Emit(OpCodes.Pop);

        // var ping = new ModPing { Value = 77 }; context.Net.Send<ModPing>(ping, null);
        MethodInfo sendGeneric = typeof(INetChannel).GetMethod(nameof(INetChannel.Send))!.MakeGenericMethod(msgType);
        LocalBuilder pingLocal = il.DeclareLocal(msgType);
        il.Emit(OpCodes.Newobj, msgType.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, pingLocal);
        il.Emit(OpCodes.Ldloc, pingLocal);
        il.Emit(OpCodes.Ldc_I4, 77);
        il.Emit(OpCodes.Callvirt, setVal);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, typeof(IClientContext).GetProperty(nameof(IClientContext.Net))!.GetGetMethod()!);
        il.Emit(OpCodes.Ldloc, pingLocal);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Callvirt, sendGeneric);
        il.Emit(OpCodes.Ret);

        modTb.DefineMethodOverride(loadMethod, loadInterfaceMethod);
        modTb.CreateType();
        ab.Save(outputDllPath);
    }

    private sealed class HealthyCountingMod : IClientMod
    {
        public int FrameTicks { get; private set; }
        public void Load(IClientContext context) => context.Game.Frame += _ => FrameTicks++;
    }

    private sealed class AlwaysThrowingMod : IClientMod
    {
        public bool TrackedResourceDisposed { get; private set; }

        public void Load(IClientContext context)
        {
            context.TrackDisposable(new ActionCallback(() => TrackedResourceDisposed = true));
            context.Game.Frame += _ => throw new InvalidOperationException("Simulated mod crash on frame");
        }
    }

    private sealed class RawProbingMod(nint buffer) : IClientMod
    {
        public int ReadBackValue { get; private set; }
        public nint NativeCallResult { get; private set; }

        public void Load(IClientContext context)
        {
            IRawMemory raw = context.Raw;
            _ = raw.GetAddress("frame.state_stack_pass_a_hook");
            raw.Write(buffer, 0x12345678);
            ReadBackValue = raw.Read<int>(buffer);
            IDisposable hook = raw.InstallHook("frame.state_stack_pass_a_hook", 0x9999, out _);
            context.TrackDisposable(hook);
            NativeCallResult = raw.Call("frame.state_stack_pass_a_hook", 12, 18);
        }
    }

    private sealed class ActionCallback(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
