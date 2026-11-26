using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.Json;
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

[Collection("NativeEntryPointSerial")]
public sealed class Phase3ContentAndRowMap100Tests
{
    private const string SampleDigestA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SampleDigestB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void Sdk100Freeze_PreservesProtocol1Abi040AndBackwardCompatRanges()
    {
        Assert.Equal("1.1.0", SdkVersion.Current);
        Assert.Equal(new Version(1, 1, 0), SdkVersion.Parsed);
        Assert.Equal(1, SdkVersion.ProtocolVersion);
        Assert.Equal(0x00000500u, NativeShimConstants.AbiVersion);

        Assert.True(SdkVersion.IsCompatible(">=0.2.0 <2.0.0"));
        Assert.True(SdkVersion.IsCompatible(">=0.3.0 <2.0.0"));
        Assert.True(SdkVersion.IsCompatible(">=0.4.0 <2.0.0"));
        Assert.True(SdkVersion.IsCompatible(">=1.0.0 <2.0.0"));
        Assert.True(SdkVersion.IsCompatible(">=1.1.0 <2.0.0"));
        Assert.True(SdkVersion.IsCompatible("^1.0.0"));
        Assert.True(SdkVersion.IsCompatible("~1.1.0"));
        Assert.False(SdkVersion.IsCompatible("<1.0.0"));
        Assert.False(SdkVersion.IsCompatible(">=2.0.0"));
        Assert.False(SdkVersion.IsCompatible("=0.1.0-poc"));
    }

    [Fact]
    public async Task HostT0AutoMount_MountsMergedCacheBeforeModLoadsAndExposesContentRowMap()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"nativemods-t0-cache-{Guid.NewGuid():N}");
        string modsRoot = Path.Combine(tempRoot, "Mods", "Client");
        string mergedCache = Path.Combine(modsRoot, ".merged-cache");
        Directory.CreateDirectory(Path.Combine(mergedCache, "Data"));
        Directory.CreateDirectory(Path.Combine(mergedCache, "Metadata", "StatDescriptions"));

        try
        {
            byte[] fakeBitDat = [0x01, 0x00, 0x00, 0x00, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB];
            byte[] fakeStatDesc = [0xFF, 0xFE, (byte)'d', 0x00, (byte)'e', 0x00, (byte)'s', 0x00, (byte)'c', 0x00];
            File.WriteAllBytes(Path.Combine(mergedCache, "Data", "BaseItemTypes.datc64"), fakeBitDat);
            File.WriteAllBytes(Path.Combine(mergedCache, "Metadata", "StatDescriptions", "alpha_custom_stats.txt"), fakeStatDesc);

            var rowMap = new ContentRowMap
            {
                Digest = SampleDigestA,
                Mods = ["mod-alpha", "mod-beta"],
                Tables = new Dictionary<string, ContentTableMap>
                {
                    ["BaseItemTypes"] = new ContentTableMap
                    {
                        StockRowCount = 1000,
                        MergedRowCount = 1002,
                        TableSha256 = "1111111111111111111111111111111111111111111111111111111111111111",
                        Entries = new Dictionary<string, ContentRowEntry>
                        {
                            ["alpha_orb"] = new ContentRowEntry { ModId = "mod-alpha", RowIndex = 1000, Hash32 = 0x12345678u },
                            ["beta_shard"] = new ContentRowEntry { ModId = "mod-beta", RowIndex = 1001, Hash32 = 0x87654321u },
                        },
                    },
                    ["Stats"] = new ContentTableMap
                    {
                        StockRowCount = 23362,
                        MergedRowCount = 23362,
                        TableSha256 = "2222222222222222222222222222222222222222222222222222222222222222",
                        Entries = new Dictionary<string, ContentRowEntry>
                        {
                            ["alpha_power"] = new ContentRowEntry { ModId = "mod-alpha", RowIndex = 99, StockId = "old_do_not_use_99" },
                        },
                    },
                },
                GeneratedOverlays =
                [
                    new ContentGeneratedOverlay
                    {
                        VirtualPath = "Metadata/StatDescriptions/alpha_custom_stats.txt",
                        Sha256 = "3333333333333333333333333333333333333333333333333333333333333333",
                    },
                ],
            };
            File.WriteAllText(Path.Combine(mergedCache, ContentRowMap.FileName), rowMap.ToJson(), new UTF8Encoding(false));

            using var bridge = new SimulatedNativeBridge();
            using var runtime = new ClientHostRuntime(bridge, new ClientHostOptions
            {
                ModsRootDirectory = modsRoot,
            });

            // Auto-mounted at t=0 inside constructor BEFORE Start() or any mod loads!
            Assert.NotNull(runtime.MergedCacheMount);
            Assert.True(runtime.MergedCacheMount!.Mounted);
            Assert.Equal(SampleDigestA, runtime.Content.ContentRowMapDigest);
            Assert.NotNull(runtime.Content.RowMap);

            // Overlay bytes are live immediately via FileOverlayService and native known paths.
            Assert.Equal(fakeBitDat, runtime.FileOverlayService.ReadAllBytes("Data/BaseItemTypes.datc64"));
            Assert.Equal(fakeStatDesc, runtime.FileOverlayService.ReadAllBytes("Metadata/StatDescriptions/alpha_custom_stats.txt"));
            // content-row-map.json itself is metadata and must NOT pollute game file overlays.
            Assert.False(runtime.FileOverlayService.Exists("content-row-map.json"));

            // Case-insensitive resolution via IContentRegistry.TryResolveRow.
            Assert.True(runtime.Content.TryResolveRow("baseitemtypes", "ALPHA_ORB", out ContentRowEntry? alphaOrb));
            Assert.Equal(1000, alphaOrb!.RowIndex);
            Assert.Equal(0x12345678u, alphaOrb.Hash32);
            Assert.Equal("mod-alpha", alphaOrb.ModId);

            Assert.True(runtime.Content.TryResolveRow("Stats", "alpha_power", out ContentRowEntry? alphaStat));
            Assert.Equal(99, alphaStat!.RowIndex);
            Assert.Equal("old_do_not_use_99", alphaStat.StockId);

            // Client mod sees RowMap, ContentRowMapDigest, TryResolveRow, and live DeclareDatRow tracking.
            var probeMod = new ContentProbeClientMod();
            var manifest = new ClientModManifest("probe-mod", "Probe", "1.0.0", SdkVersionRange: ">=1.0.0 <2.0.0");
            runtime.Loader.LoadInProcessMod(manifest, probeMod);

            Assert.Equal(SampleDigestA, probeMod.SeenDigest);
            Assert.Equal(1000, probeMod.ResolvedAlphaOrbRow);
            Assert.True(runtime.Content.TryGetDeclaredDatRow("BaseItemTypes", "live_runtime_row", out DatRowDeclaration? liveRow));
            Assert.NotNull(liveRow);

            // Unload disposes tracked DeclareDatRow cleanly.
            var unloadResult = await runtime.Loader.UnloadModAsync("probe-mod");
            Assert.True(unloadResult.WasLoaded);
            Assert.False(runtime.Content.TryGetDeclaredDatRow("BaseItemTypes", "live_runtime_row", out _));
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
    public async Task HandshakeContentRowMapDigest_DetectsMatchMismatchAndMissingClientDigest()
    {
        var sink = new RecordingSink();
        var serverBridge = new ClientBridgeHost(sink, new ClientBridgeOptions
        {
            ExpectedContentRowMapDigest = SampleDigestA,
        });

        var mismatches = new List<ContentRowMapDigestMismatchEventArgs>();
        serverBridge.ContentRowMapDigestMismatch += e => mismatches.Add(e);

        // 1. Matching digest -> IsContentRowMapDigestMatched == true, zero mismatch events.
        var matchingHello = new ClientHandshake(
            ExtenderVersion: SdkVersion.Current,
            SdkVersion: SdkVersion.Current,
            Build: ClientBuildId.Build3293d,
            BuildVersion: "3.29.3d",
            ProtocolVersion: SdkVersion.ProtocolVersion,
            InstalledMods: [new InstalledClientModInfo("mod-alpha", "1.0.0")],
            Capabilities: ["content.row-map"],
            ContentRowMapDigest: SampleDigestA);

        await serverBridge.HandleIncomingEnvelopeAsync(
            "conn-match",
            MessageCodec.CreateTypedEnvelope(MessageCodec.SystemChannel, matchingHello));

        IClientSession matchSession = serverBridge.For("conn-match");
        Assert.Equal(SampleDigestA, matchSession.ClientContentRowMapDigest);
        Assert.Equal(SampleDigestA, matchSession.ExpectedContentRowMapDigest);
        Assert.True(matchSession.IsContentRowMapDigestMatched);
        Assert.Empty(mismatches);

        // Verify ServerHandshake reply also advertises ExpectedContentRowMapDigest + content.row-map capability.
        Assert.Single(sink.SentPackets);
        Assert.True(MessageCodec.TryDecodeRulesetBody(sink.SentPackets[0].Payload, out _, out ReadOnlyMemory<byte> inner));
        ModEnvelope replyEnv = MessageCodec.DecodeEnvelope(inner.Span);
        ServerHandshake reply = MessageCodec.DeserializePayload<ServerHandshake>(replyEnv);
        Assert.Equal(SampleDigestA, reply.ExpectedContentRowMapDigest);
        Assert.Contains("content.row-map", reply.ServerCapabilities);

        // 2. Mismatched digest -> IsContentRowMapDigestMatched == false, mismatch event fires!
        var mismatchedHello = matchingHello with { ContentRowMapDigest = SampleDigestB };
        await serverBridge.HandleIncomingEnvelopeAsync(
            "conn-mismatch",
            MessageCodec.CreateTypedEnvelope(MessageCodec.SystemChannel, mismatchedHello));

        IClientSession mismatchSession = serverBridge.For("conn-mismatch");
        Assert.Equal(SampleDigestB, mismatchSession.ClientContentRowMapDigest);
        Assert.False(mismatchSession.IsContentRowMapDigestMatched);
        Assert.Single(mismatches);
        Assert.Equal("conn-mismatch", mismatches[0].ConnectionId);
        Assert.Equal(SampleDigestB, mismatches[0].ClientDigest);
        Assert.Equal(SampleDigestA, mismatches[0].ExpectedDigest);

        // 3. Missing client digest -> IsContentRowMapDigestMatched == false, mismatch event fires!
        var missingDigestHello = matchingHello with { ContentRowMapDigest = null };
        await serverBridge.HandleIncomingEnvelopeAsync(
            "conn-missing",
            MessageCodec.CreateTypedEnvelope(MessageCodec.SystemChannel, missingDigestHello));

        IClientSession missingSession = serverBridge.For("conn-missing");
        Assert.Null(missingSession.ClientContentRowMapDigest);
        Assert.False(missingSession.IsContentRowMapDigestMatched);
        Assert.Equal(2, mismatches.Count);
        Assert.Null(mismatches[1].ClientDigest);
        Assert.Equal(SampleDigestA, mismatches[1].ExpectedDigest);
    }

    [Fact]
    public async Task ClientHostAndServerContentRowMap_EndToEndHandshakeAdvertisesMountedDigest()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"nativemods-e2e-rowmap-{Guid.NewGuid():N}");
        string mergedCache = Path.Combine(tempRoot, "Mods", "Client", ".merged-cache");
        Directory.CreateDirectory(mergedCache);

        try
        {
            var rowMap = new ContentRowMap
            {
                Digest = SampleDigestA,
                Mods = ["siphon"],
                Tables = new Dictionary<string, ContentTableMap>
                {
                    ["BaseItemTypes"] = new ContentTableMap
                    {
                        StockRowCount = 500,
                        MergedRowCount = 501,
                        TableSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                        Entries = new Dictionary<string, ContentRowEntry>
                        {
                            ["siphon_orb"] = new ContentRowEntry { ModId = "siphon", RowIndex = 500, Hash32 = 0xAABBCCDDu, Hash16 = 0x1234 },
                        },
                    },
                    ["Stats"] = new ContentTableMap
                    {
                        StockRowCount = 23362,
                        MergedRowCount = 23362,
                        TableSha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                        Entries = new Dictionary<string, ContentRowEntry>
                        {
                            ["siphon_power"] = new ContentRowEntry { ModId = "siphon", RowIndex = 99, StockId = "old_do_not_use_99" },
                        },
                    },
                },
            };
            File.WriteAllText(Path.Combine(mergedCache, ContentRowMap.FileName), rowMap.ToJson(), new UTF8Encoding(false));

            // Server side loads from LocalExile root and binds expected digest onto ClientBridgeHost.
            ServerContentRowMap? serverRowMap = ServerContentRowMap.TryLoadFromLocalExileRoot(tempRoot);
            Assert.NotNull(serverRowMap);
            Assert.Equal(500, serverRowMap!.RequireRowIndex("BaseItemTypes", "siphon_orb"));
            Assert.Equal(0xAABBCCDDu, serverRowMap.RequireHash32("BaseItemTypes", "siphon_orb"));
            Assert.Equal((ushort)0x1234, serverRowMap.RequireHash16("BaseItemTypes", "siphon_orb"));
            Assert.Equal("old_do_not_use_99", serverRowMap.RequireStockId("Stats", "siphon_power"));
            Assert.Throws<KeyNotFoundException>(() => serverRowMap.RequireRow("BaseItemTypes", "missing"));

            var sink = new RecordingSink();
            using var serverBridge = new ClientBridgeHost(sink);
            Assert.NotNull(ServerContentRowMap.TryLoadAndBind(serverBridge, mergedCache));
            Assert.Equal(SampleDigestA, serverBridge.ExpectedContentRowMapDigest);

            // Client host auto-mounts .merged-cache at t=0 and sends ClientHandshake carrying ContentRowMapDigest.
            using var sim = new SimulatedNativeBridge();
            var sentRulesetPackets = new List<(byte RulesetId, byte[] InnerPayload)>();
            sim.OnOutgoingRulesetPacket = (rulesetId, innerPayload) => sentRulesetPackets.Add((rulesetId, innerPayload.ToArray()));
            using var clientHost = new ClientHostRuntime(sim, new ClientHostOptions
            {
                MergedCacheDirectory = mergedCache,
            });
            ClientHandshake sent = clientHost.SendHandshake();
            Assert.Equal(SampleDigestA, sent.ContentRowMapDigest);
            Assert.Contains("content.row-map", sent.Capabilities);

            // Route raw C->S 0x02CA ruleset packet from client into ServerBridge.
            Assert.Single(sentRulesetPackets);
            (byte rulesetId, byte[] innerPayload) = sentRulesetPackets[0];
            byte[] rulesetBody = MessageCodec.EncodeRulesetBody(rulesetId, innerPayload);
            bool handled = await serverBridge.HandleIncomingPacketAsync("conn-1", IOpcodeRegistry.ClientRulesetOpcode, rulesetBody);
            Assert.True(handled);

            IClientSession session = serverBridge.For("conn-1");
            Assert.True(session.HasNativeHost);
            Assert.Equal(SampleDigestA, session.ClientContentRowMapDigest);
            Assert.True(session.IsContentRowMapDigestMatched);
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
    public void LocalExileGatewayHost_Part2bReflectionHelpers_RegisterAndDisposeCatalogEntriesWithoutHardDependency()
    {
        // Build a lightweight synthetic Gateway assembly with Part 2b ItemCatalog, UniqueCatalog, and DropPoolRegistry.
        Assembly fakeGatewayAsm = BuildSyntheticPart2bGatewayAssembly();
        var modContext = new FakeModContext("custom-content-mod");

        using var host = LocalExileGatewayHost.Create(modContext, b => b
            .WithGatewayAssembly(fakeGatewayAsm)
            .UseCoreClientChannel(false)
            .WithBus(attach: false));

        Assert.True(host.SupportsCatalogRegistration);

        // Omitting RowIndex defaults to -1 (triggers Part 2b synthetic row allocation, never overwriting row 0),
        // and omitting Components defaults to ["Base", "Stack"] when StackSize > 1 so BaseType.Supported == true!
        IDisposable? baseSub = host.RegisterCustomBaseItem(new GatewayCustomBaseItemRegistration(
            MetadataPath: "Metadata/Items/Currency/Mods/Custom/TestOrb",
            Name: "Orb of Reflection",
            ItemClass: "StackableCurrency",
            StackSize: 20));
        Assert.NotNull(baseSub);
        Assert.Equal(-1, ReadStaticInt(fakeGatewayAsm, "ItemCatalog", "LastRow"));
        Assert.Equal("Base,Stack", ReadStaticString(fakeGatewayAsm, "ItemCatalog", "LastComponents"));

        IDisposable? nonStackSub = host.RegisterCustomBaseItem(new GatewayCustomBaseItemRegistration(
            MetadataPath: "Metadata/Items/Rings/Mods/Custom/TestRing",
            Name: "Reflection Ring",
            ItemClass: "Ring",
            StackSize: 1,
            RowIndex: 4201));
        Assert.NotNull(nonStackSub);
        Assert.Equal(4201, ReadStaticInt(fakeGatewayAsm, "ItemCatalog", "LastRow"));
        Assert.Equal("Base", ReadStaticString(fakeGatewayAsm, "ItemCatalog", "LastComponents"));

        IDisposable? uniqueSub = host.RegisterCustomUnique(new GatewayCustomUniqueRegistration(
            UniqueId: "custom_unique_ring",
            Name: "Band of Harmony",
            BaseItemMetadataPath: "Metadata/Items/Rings/Ring1",
            DropLevel: 24,
            Weight: 50,
            ExplicitModIds: ["CustomMod1"]));
        Assert.NotNull(uniqueSub);

        IDisposable? dropSub = host.RegisterDropPoolEntry(new GatewayDropPoolEntryRegistration(
            PoolId: "global",
            EntryKey: "test_orb_drop",
            ItemMetadataPath: "Metadata/Items/Currency/Mods/Custom/TestOrb",
            Weight: 25,
            MinAreaLevel: 10));
        Assert.NotNull(dropSub);

        Assert.Equal(2, ReadStaticInt(fakeGatewayAsm, "ItemCatalog", "ActiveCount"));
        Assert.Equal(1, ReadStaticInt(fakeGatewayAsm, "UniqueCatalog", "ActiveCount"));
        Assert.Equal(1, ReadStaticInt(fakeGatewayAsm, "DropPoolOverlays", "ActiveCount"));

        host.Dispose();

        // All tracked catalog registrations were disposed cleanly when the Gateway host unloaded.
        Assert.Equal(0, ReadStaticInt(fakeGatewayAsm, "ItemCatalog", "ActiveCount"));
        Assert.Equal(0, ReadStaticInt(fakeGatewayAsm, "UniqueCatalog", "ActiveCount"));
        Assert.Equal(0, ReadStaticInt(fakeGatewayAsm, "DropPoolOverlays", "ActiveCount"));
    }

    [Fact]
    public void PackSdkNugetScript_ProducesValid100NupkgsWithXmlDocsInTempDir()
    {
        string repoRoot = FindRepoRoot();
        string scriptPath = Path.Combine(repoRoot, "scripts", "pack-sdk-nuget.sh");
        Assert.True(File.Exists(scriptPath), $"Missing NuGet pack script at '{scriptPath}'.");

        string tempOut = Path.Combine(Path.GetTempPath(), $"nativemods-nuget-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempOut);
        try
        {
            var psi = new ProcessStartInfo("bash", $"\"{scriptPath}\" -o \"{tempOut}\" -c Release")
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using Process proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start scripts/pack-sdk-nuget.sh.");
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            Assert.True(proc.WaitForExit(120_000), "pack-sdk-nuget.sh timed out.");
            Assert.True(proc.ExitCode == 0, $"pack-sdk-nuget.sh failed (exit {proc.ExitCode}):\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");

            string sdkNupkg = Path.Combine(tempOut, $"LocalExile.Client.Sdk.{SdkVersion.Current}.nupkg");
            string bridgeNupkg = Path.Combine(tempOut, $"LocalExile.Client.ServerBridge.{SdkVersion.Current}.nupkg");
            Assert.True(File.Exists(sdkNupkg), $"Missing expected SDK nupkg: {sdkNupkg}");
            Assert.True(File.Exists(bridgeNupkg), $"Missing expected ServerBridge nupkg: {bridgeNupkg}");

            VerifyNupkgContents(sdkNupkg, "LocalExile.Client.Sdk", SdkVersion.Current, "LocalExile.Client.Sdk.xml");
            VerifyNupkgContents(bridgeNupkg, "LocalExile.Client.ServerBridge", SdkVersion.Current, "LocalExile.Client.ServerBridge.xml");
        }
        finally
        {
            if (Directory.Exists(tempOut))
            {
                Directory.Delete(tempOut, recursive: true);
            }
        }
    }

    private static void VerifyNupkgContents(string nupkgPath, string expectedId, string expectedVersion, string expectedXmlDoc)
    {
        using ZipArchive zip = ZipFile.OpenRead(nupkgPath);
        string[] entryNames = [.. zip.Entries.Select(e => e.FullName)];

        Assert.Contains(entryNames, n => n.EndsWith(expectedXmlDoc, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entryNames, n => n.EndsWith($"{expectedId}.dll", StringComparison.OrdinalIgnoreCase));

        ZipArchiveEntry? nuspecEntry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(nuspecEntry);
        using var reader = new StreamReader(nuspecEntry!.Open(), Encoding.UTF8);
        string nuspec = reader.ReadToEnd();
        Assert.Contains($"<id>{expectedId}</id>", nuspec, StringComparison.Ordinal);
        Assert.Contains($"<version>{expectedVersion}</version>", nuspec, StringComparison.Ordinal);
        Assert.Contains("<license type=\"expression\">GPL-3.0-or-later</license>", nuspec, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "NativeMods.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir) ?? string.Empty;
        }

        throw new DirectoryNotFoundException("Could not locate NativeMods.slnx from AppContext.BaseDirectory.");
    }

    private static int ReadStaticInt(Assembly asm, string typeName, string fieldName)
    {
        Type t = asm.GetType(typeName, throwOnError: true)!;
        FieldInfo f = t.GetField(fieldName, BindingFlags.Public | BindingFlags.Static)!;
        return (int)f.GetValue(null)!;
    }

    private static string? ReadStaticString(Assembly asm, string typeName, string fieldName)
    {
        Type t = asm.GetType(typeName, throwOnError: true)!;
        FieldInfo f = t.GetField(fieldName, BindingFlags.Public | BindingFlags.Static)!;
        return f.GetValue(null) as string;
    }

    private static AssemblyBuilder BuildSyntheticPart2bGatewayAssembly()
    {
        var asmName = new AssemblyName($"SyntheticPart2bGateway_{Guid.NewGuid():N}");
        AssemblyBuilder ab = AssemblyBuilder.DefineDynamicAssembly(asmName, AssemblyBuilderAccess.RunAndCollect);
        ModuleBuilder mb = ab.DefineDynamicModule("Main");

        // Disposable token that decrements a static ActiveCount field via Action.
        TypeBuilder tokenType = mb.DefineType("RegToken", TypeAttributes.Public | TypeAttributes.Sealed, typeof(object), [typeof(IDisposable)]);
        FieldBuilder onDisposeField = tokenType.DefineField("_onDispose", typeof(Action), FieldAttributes.Private);
        ConstructorBuilder tokenCtor = tokenType.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [typeof(Action)]);
        ILGenerator ctorIl = tokenCtor.GetILGenerator();
        ctorIl.Emit(OpCodes.Ldarg_0);
        ctorIl.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        ctorIl.Emit(OpCodes.Ldarg_0);
        ctorIl.Emit(OpCodes.Ldarg_1);
        ctorIl.Emit(OpCodes.Stfld, onDisposeField);
        ctorIl.Emit(OpCodes.Ret);

        MethodBuilder disposeMethod = tokenType.DefineMethod("Dispose", MethodAttributes.Public | MethodAttributes.Virtual, typeof(void), Type.EmptyTypes);
        ILGenerator dispIl = disposeMethod.GetILGenerator();
        dispIl.Emit(OpCodes.Ldarg_0);
        dispIl.Emit(OpCodes.Ldfld, onDisposeField);
        dispIl.Emit(OpCodes.Callvirt, typeof(Action).GetMethod("Invoke")!);
        dispIl.Emit(OpCodes.Ret);

        MethodBuilder noopMethod = tokenType.DefineMethod("NoOp", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes);
        noopMethod.GetILGenerator().Emit(OpCodes.Ret);

        Type builtTokenType = tokenType.CreateType()!;

        // Separate single-Add event stub types matching LocalExile's GameEvent<T> (avoids AmbiguousMatchException).
        Type packetEventType = DefineSingleAddEventType(mb, "PacketGameEvent", typeof(Func<object, byte[], Task<bool>>), builtTokenType);
        Type enteredEventType = DefineSingleAddEventType(mb, "PlayerEnteredGameEvent", typeof(Func<object, Task>), builtTokenType);
        Type leavingEventType = DefineSingleAddEventType(mb, "PlayerLeavingGameEvent", typeof(Func<object, string, Task>), builtTokenType);

        // GameEvents static class (minimum required by ReflectionGatewayAdapter)
        TypeBuilder gameEvents = mb.DefineType("GameEvents", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        gameEvents.DefineField("PlayerEntered", enteredEventType, FieldAttributes.Public | FieldAttributes.Static);
        gameEvents.DefineField("PlayerLeaving", leavingEventType, FieldAttributes.Public | FieldAttributes.Static);
        MethodBuilder pktMethod = gameEvents.DefineMethod("Packet", MethodAttributes.Public | MethodAttributes.Static, packetEventType, [typeof(ushort)]);
        ILGenerator pktIl = pktMethod.GetILGenerator();
        pktIl.Emit(OpCodes.Newobj, packetEventType.GetConstructor(Type.EmptyTypes)!);
        pktIl.Emit(OpCodes.Ret);
        MethodBuilder cmdMethod = gameEvents.DefineMethod("Command", MethodAttributes.Public | MethodAttributes.Static, typeof(IDisposable), [typeof(string), typeof(string), typeof(string), typeof(Func<object, string[], Task>)]);
        EmitReturnNoOpDisposable(cmdMethod.GetILGenerator(), builtTokenType);
        Type builtGameEvents = gameEvents.CreateType()!;
        builtGameEvents.GetField("PlayerEntered")!.SetValue(null, Activator.CreateInstance(enteredEventType));
        builtGameEvents.GetField("PlayerLeaving")!.SetValue(null, Activator.CreateInstance(leavingEventType));

        // Program static class
        TypeBuilder program = mb.DefineType("Program", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        MethodBuilder sendMethod = program.DefineMethod("Send", MethodAttributes.Public | MethodAttributes.Static, typeof(Task), [typeof(object), typeof(byte[]), typeof(string)]);
        ILGenerator sendIl = sendMethod.GetILGenerator();
        sendIl.Emit(OpCodes.Call, typeof(Task).GetProperty("CompletedTask")!.GetGetMethod()!);
        sendIl.Emit(OpCodes.Ret);
        program.CreateType();

        // Part 2b ItemCatalog.RegisterOverlay (capturing row & components), UniqueCatalog.RegisterOverlay, and DropPoolOverlays.Register matching LocalExile a4c7cafa5!
        DefineItemCatalogType(mb, builtTokenType);
        DefineCatalogType(mb, "UniqueCatalog", "RegisterOverlay", [typeof(string), typeof(string), typeof(string), typeof(double)], ["owner", "name", "baseTypeId", "dropWeight"], builtTokenType);
        DefineCatalogType(mb, "DropPoolOverlays", "Register", [typeof(string), typeof(string), typeof(string), typeof(double)], ["owner", "category", "itemId", "weight"], builtTokenType);

        return ab;
    }

    private static void DefineItemCatalogType(ModuleBuilder mb, Type tokenType)
    {
        TypeBuilder tb = mb.DefineType("ItemCatalog", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        FieldBuilder activeCount = tb.DefineField("ActiveCount", typeof(int), FieldAttributes.Public | FieldAttributes.Static);
        FieldBuilder lastRow = tb.DefineField("LastRow", typeof(int), FieldAttributes.Public | FieldAttributes.Static);
        FieldBuilder lastComponents = tb.DefineField("LastComponents", typeof(string), FieldAttributes.Public | FieldAttributes.Static);

        MethodBuilder decMethod = tb.DefineMethod("Decrement", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes);
        ILGenerator decIl = decMethod.GetILGenerator();
        decIl.Emit(OpCodes.Ldsfld, activeCount);
        decIl.Emit(OpCodes.Ldc_I4_1);
        decIl.Emit(OpCodes.Sub);
        decIl.Emit(OpCodes.Stsfld, activeCount);
        decIl.Emit(OpCodes.Ret);

        MethodBuilder reg = tb.DefineMethod(
            "RegisterOverlay",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(IDisposable),
            [typeof(string), typeof(int), typeof(string), typeof(string), typeof(string), typeof(IReadOnlyList<string>)]);
        reg.DefineParameter(1, ParameterAttributes.None, "owner");
        reg.DefineParameter(2, ParameterAttributes.None, "row");
        reg.DefineParameter(3, ParameterAttributes.None, "id");
        reg.DefineParameter(4, ParameterAttributes.None, "name");
        reg.DefineParameter(5, ParameterAttributes.None, "class");
        reg.DefineParameter(6, ParameterAttributes.None, "components");

        MethodInfo stringJoin = typeof(string).GetMethod("Join", [typeof(string), typeof(IEnumerable<string>)])!;

        ILGenerator il = reg.GetILGenerator();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stsfld, lastRow);
        il.Emit(OpCodes.Ldstr, ",");
        il.Emit(OpCodes.Ldarg_S, (byte)5);
        il.Emit(OpCodes.Call, stringJoin);
        il.Emit(OpCodes.Stsfld, lastComponents);
        il.Emit(OpCodes.Ldsfld, activeCount);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stsfld, activeCount);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldftn, decMethod);
        il.Emit(OpCodes.Newobj, typeof(Action).GetConstructor([typeof(object), typeof(nint)])!);
        il.Emit(OpCodes.Newobj, tokenType.GetConstructor([typeof(Action)])!);
        il.Emit(OpCodes.Ret);

        tb.CreateType();
    }

    private static Type DefineSingleAddEventType(ModuleBuilder mb, string typeName, Type delegateType, Type tokenType)
    {
        TypeBuilder tb = mb.DefineType(typeName, TypeAttributes.Public | TypeAttributes.Sealed);
        MethodBuilder addMethod = tb.DefineMethod("Add", MethodAttributes.Public, typeof(IDisposable), [typeof(string), delegateType]);
        EmitReturnNoOpDisposable(addMethod.GetILGenerator(), tokenType);
        return tb.CreateType()!;
    }

    private static void DefineCatalogType(
        ModuleBuilder mb,
        string typeName,
        string methodName,
        Type[] paramTypes,
        string[] paramNames,
        Type tokenType)
    {
        TypeBuilder tb = mb.DefineType(typeName, TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        FieldBuilder activeCount = tb.DefineField("ActiveCount", typeof(int), FieldAttributes.Public | FieldAttributes.Static);

        MethodBuilder decMethod = tb.DefineMethod("Decrement", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes);
        ILGenerator decIl = decMethod.GetILGenerator();
        decIl.Emit(OpCodes.Ldsfld, activeCount);
        decIl.Emit(OpCodes.Ldc_I4_1);
        decIl.Emit(OpCodes.Sub);
        decIl.Emit(OpCodes.Stsfld, activeCount);
        decIl.Emit(OpCodes.Ret);

        MethodBuilder reg = tb.DefineMethod(methodName, MethodAttributes.Public | MethodAttributes.Static, typeof(IDisposable), paramTypes);
        for (int i = 0; i < paramNames.Length; i++)
        {
            reg.DefineParameter(i + 1, ParameterAttributes.None, paramNames[i]);
        }

        ILGenerator il = reg.GetILGenerator();
        il.Emit(OpCodes.Ldsfld, activeCount);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stsfld, activeCount);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldftn, decMethod);
        il.Emit(OpCodes.Newobj, typeof(Action).GetConstructor([typeof(object), typeof(nint)])!);
        il.Emit(OpCodes.Newobj, tokenType.GetConstructor([typeof(Action)])!);
        il.Emit(OpCodes.Ret);

        tb.CreateType();
    }

    private static void EmitReturnNoOpDisposable(ILGenerator il, Type tokenType)
    {
        MethodInfo noop = tokenType.GetMethod("NoOp", BindingFlags.Public | BindingFlags.Static)!;
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldftn, noop);
        il.Emit(OpCodes.Newobj, typeof(Action).GetConstructor([typeof(object), typeof(nint)])!);
        il.Emit(OpCodes.Newobj, tokenType.GetConstructor([typeof(Action)])!);
        il.Emit(OpCodes.Ret);
    }

    private sealed class FakeModContext(string id)
    {
        public List<string> Logs { get; } = [];
        public FakeManifest Manifest { get; } = new(id);
        public void Log(string message) => Logs.Add(message);
    }

    private sealed record FakeManifest(string Id);

    private sealed class ContentProbeClientMod : IClientMod
    {
        public string? SeenDigest { get; private set; }
        public int ResolvedAlphaOrbRow { get; private set; } = -1;

        public void Load(IClientContext context)
        {
            SeenDigest = context.Game.Content.ContentRowMapDigest;
            if (context.Game.Content.TryResolveRow("BaseItemTypes", "alpha_orb", out ContentRowEntry? entry))
            {
                ResolvedAlphaOrbRow = entry.RowIndex;
            }

            context.Game.Content.DeclareDatRow(new DatRowDeclaration(
                "BaseItemTypes",
                "live_runtime_row",
                new Dictionary<string, object?> { ["Name"] = "Live Test" }));
        }
    }

    private sealed class RecordingSink : IGatewayPacketSink
    {
        public List<(string ConnectionId, ushort Opcode, byte[] Payload)> SentPackets { get; } = [];
        public List<(string ConnectionId, string Text)> SentChats { get; } = [];

        public Task SendPacketAsync(string connectionId, ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            SentPackets.Add((connectionId, opcode, payload.ToArray()));
            return Task.CompletedTask;
        }

        public Task SendChatReplyAsync(string connectionId, string text, CancellationToken cancellationToken = default)
        {
            SentChats.Add((connectionId, text));
            return Task.CompletedTask;
        }
    }
}
