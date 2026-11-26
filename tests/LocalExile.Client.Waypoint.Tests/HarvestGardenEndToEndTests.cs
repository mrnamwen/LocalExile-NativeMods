using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using DualMod.HarvestGarden.Server;
using DualMod.HarvestGarden.Shared;
using LocalExile.Client.Host;
using LocalExile.Client.Host.Native;
using LocalExile.Client.Host.Services;
using LocalExile.Client.Patcher;
using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Addresses;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.Sdk.Protocol;
using LocalExile.Client.ServerBridge;
using Xunit;

namespace LocalExile.Client.Waypoint.Tests;

public sealed class HarvestGardenEndToEndTests
{
    [Theory]
    [InlineData(NetTransportKind.HostRuleset)]
    [InlineData(NetTransportKind.ChatFallback)]
    public async Task FullDualSideHarvestGardenSimulation_InstallLoadSyncUiGealFrameFallbackAndCleanUninstall(NetTransportKind transport)
    {
        string workspace = Path.Combine(Path.GetTempPath(), "nm-garden-e2e-" + Guid.NewGuid().ToString("N"));
        string localExileRoot = Path.Combine(workspace, "LocalExile");
        string stagingPkg = Path.Combine(workspace, "harvest-garden-pkg");
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

            // 2. Stage the real HarvestGarden package including the freshly compiled Server + Client DLLs
            string repoRoot = FindRepoRoot();
            string exampleRoot = Path.Combine(repoRoot, "examples", "DualMod.HarvestGarden");
            CopyDirectory(exampleRoot, stagingPkg);

            string compiledClientDll = FindCompiledAssembly(exampleRoot, "Client", "harvest-garden.Client.dll");
            string compiledServerDll = FindCompiledAssembly(exampleRoot, "Server", "harvest-garden.dll");
            File.Copy(compiledClientDll, Path.Combine(stagingPkg, "harvest-garden.Client.dll"), overwrite: true);
            File.Copy(compiledServerDll, Path.Combine(stagingPkg, "harvest-garden.dll"), overwrite: true);

            // 3. Install via WaypointModInstaller
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
            Assert.True(File.Exists(Path.Combine(deployedClientDir, "harvest-garden.Client.dll")));
            Assert.True(File.Exists(Path.Combine(deployedClientDir, "files", "metadata", "ui", "mods", "garden", "stockpile.ui")));
            Assert.True(File.Exists(Path.Combine(deployedClientDir, "files", "metadata", "miscellaneousobjects", "mods", "garden", "plot.ot")));

            // 4. Boot ClientHostRuntime + dynamically load compiled harvest-garden.Client.dll in a collectible ALC
            using var simBridge = new SimulatedNativeBridge(ClientBuildId.Build3293d);
            using var clientHost = new ClientHostRuntime(simBridge, new ClientHostOptions
            {
                TransportKind = transport,
                HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId,
                ShadowCopyRoot = shadowRoot
            });
            clientHost.Start();

            // 4-12. Load compiled harvest-garden.Client.dll in a collectible ALC and exercise full E2E session
            // inside a NoInlining helper so zero stack references pin ALC types during UnloadModAsync GC check.
            await RunActiveHarvestGardenSessionAsync(simBridge, clientHost, deployedClientDir, transport);

            // 13. Clean unload of collectible ALC + clean Waypoint uninstall
            var unloadResult = await clientHost.Loader.UnloadModAsync(GardenProtocol.ModId);
            Assert.True(unloadResult.WasLoaded);
            Assert.True(unloadResult.UnloadedCleanly);
            Assert.False(clientHost.UiControls.IsRegistered(GardenProtocol.GrowthGaugeControlType));
            Assert.False(clientHost.GealBuiltins.IsRegistered(GardenProtocol.GealRipenBuiltin));
            Assert.False(clientHost.Rulesets.IsRegistered(GardenProtocol.GardenRulesetId));

            var uninstallResult = installer.Uninstall(GardenProtocol.ModId, localExileRoot);
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
    private static async Task RunActiveHarvestGardenSessionAsync(
        SimulatedNativeBridge simBridge,
        ClientHostRuntime clientHost,
        string deployedClientDir,
        NetTransportKind transport)
    {
        var loadedMod = clientHost.Loader.LoadModFromDirectory(deployedClientDir);
        Assert.Equal(GardenProtocol.ModId, loadedMod.Manifest.Id);
        Assert.False(loadedMod.IsDisabled);

        // Confirm all extension points registered by GardenClientMod
        Assert.True(clientHost.UiControls.IsRegistered(GardenProtocol.GrowthGaugeControlType));
        Assert.True(clientHost.GealBuiltins.IsRegistered(GardenProtocol.GealRipenBuiltin));
        Assert.True(clientHost.Rulesets.IsRegistered(GardenProtocol.GardenRulesetId));
        Assert.True(clientHost.Opcodes.IsRegistered(GardenProtocol.OpcodeSyncState));
        Assert.Contains(clientHost.Content.ItemBases, i => i.MetadataPath == "Metadata/Items/Mods/Garden/WildSeed");
        Assert.Contains(clientHost.Content.WorldObjects, w => w.MetadataPath == GardenProtocol.PlotObjectPath);

        // Confirm Plot.ot overlay deployed in UTF-16LE BOM CRLF is readable through FileOverlayService
        string? plotOtText = clientHost.FileOverlayService.ReadAllText(GardenProtocol.PlotObjectPath);
        Assert.NotNull(plotOtText);
        Assert.Contains("GardenRipen", plotOtText);

        // 5. Wire ServerBridgeHost + GardenServerMod over loopback transport
        const string richConnId = "exile-rich-conn";
        const string vanillaConnId = "exile-vanilla-conn";
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

        using var serverMod = new GardenServerMod(serverBridge);
        serverMod.Attach();

        // 6. Client <-> Server handshake
        clientHost.SendHandshake();
        var richSession = serverBridge.For(richConnId);
        Assert.True(richSession.HasNativeHost);
        Assert.True(richSession.HasClientMod(GardenProtocol.ModId));

        // 7. Player runs /garden -> server opens Stockpile.ui on client
        string openReply = await serverMod.HandleGardenCommandAsync(richConnId, "Oshabi", []);
        Assert.Contains("Opened Seed Stockpile window", openReply);

        IUiWindow? window = clientHost.Ui.GetWindow("harvest_garden_stockpile");
        Assert.NotNull(window);
        Assert.True(window!.Visible);
        Assert.Contains("Welcome to the Sacred Grove, Oshabi", window.Require<ILabel>("status_label").Text);

        // Confirm custom GrowthGauge control was instantiated by UiService
        var gaugeNode = Assert.IsType<UiControlNode>(window.Find("growth_gauge"));
        Assert.NotNull(gaugeNode.CustomBehavior);
        Assert.Equal(GardenProtocol.GrowthGaugeControlType, gaugeNode.CustomBehavior!.ControlTypeName);

        // 8. User types seed name, ticks auto-collect, and clicks Plant button -> reaches GardenServerMod -> updates client UI
        var seedBox = window.Require<ITextBox>("seed_box");
        seedBox.Text = "Vivid Watcher Bulb";
        var autoCollect = window.Require<ICheckBox>("auto_collect");
        autoCollect.IsChecked = true;

        window.Require<IButton>("plant_button").Click();

        Assert.Contains("Planted 'Vivid Watcher Bulb' in Plot #1", window.Require<ILabel>("status_label").Text);
        Assert.Equal("0.333", gaugeNode.GetProperty("progress"));

        // 9. Trigger custom GEAL built-in GardenRipen(3) as invoked by Plot.ot -> updates GrowthGauge target
        var ripenResult = clientHost.GealBuiltins.Invoke(GardenProtocol.GealRipenBuiltin, objectId: 0x1042, GealValue.FromInt(3));
        Assert.True(ripenResult.Succeeded);
        Assert.Equal(3, ripenResult.ReturnValue.IntValue);
        Assert.Equal("3", gaugeNode.GetProperty("ripen_stage"));

        // 10. Advance client frames -> ticks GardenClientMod.OnFrame and smoothly interpolates GrowthGauge
        for (int i = 0; i < 5; i++)
            simBridge.SimulateFrame(0.1f);

        Assert.Equal("5", gaugeNode.GetProperty("frame_ticks"));
        Assert.True(float.TryParse(gaugeNode.GetProperty("rendered_progress"), CultureInfo.InvariantCulture, out float rendered) && rendered > 0.5f);

        // 11. Click Harvest button -> server awards Condensed Lifeforce -> client label reflects authoritative total
        window.Require<IButton>("harvest_button").Click();
        Assert.Contains("Condensed Lifeforce: 25 (Harvested: 1)", window.Require<ILabel>("lifeforce_label").Text);
        Assert.Contains("Harvested 1 plot(s) for +25 Condensed Lifeforce", window.Require<ILabel>("status_label").Text);

        // 12. Vanilla client (no NativeMods handshake) gets graceful chat fallback
        string fallbackReply = await serverMod.HandleGardenCommandAsync(vanillaConnId, "VanillaExile", []);
        Assert.Contains("Chat Fallback", fallbackReply);
        Assert.Contains("Install NativeMods via Waypoint", fallbackReply);
        Assert.False(serverBridge.For(vanillaConnId).HasNativeHost);
    }

    [Fact]
    public async Task ClientPatcherBridge_DrivesRealPe32PlusPatcherThroughWaypointExtenderManager()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "nm-patchbridge-test-" + Guid.NewGuid().ToString("N"));
        string localExileRoot = Path.Combine(workspace, "LocalExile");
        string distDir = Path.Combine(workspace, "dist");
        string hostDir = Path.Combine(distDir, "host");
        Directory.CreateDirectory(Path.Combine(localExileRoot, "Client"));
        Directory.CreateDirectory(hostDir);

        try
        {
            byte[] syntheticExe = BuildMinimalSyntheticPe32Plus();
            byte[] stockExe = (byte[])syntheticExe.Clone();
            string stockPath = Path.Combine(localExileRoot, "Client", "Game.exe");
            string patchedPath = Path.Combine(localExileRoot, "Client", "Game.patched.exe");
            File.WriteAllBytes(stockPath, stockExe);
            File.WriteAllBytes(patchedPath, syntheticExe);

            string shimSrc = Path.Combine(distDir, "LocalExile.ClientHost.dll");
            File.WriteAllBytes(shimSrc, "NATIVE_SHIM_PAYLOAD"u8.ToArray());
            File.WriteAllBytes(Path.Combine(hostDir, "LocalExile.Client.Host.dll"), "MANAGED_HOST_PAYLOAD"u8.ToArray());

            var bridge = new ClientPatcherBridge(allowSynthetic: true);
            var manager = new WaypointExtenderManager(bridge);

            // Install extender -> patches real synthetic PE32+ Game.patched.exe
            var install = await manager.InstallAsync(localExileRoot, new ExtenderInstallOptions(
                ExtenderVersion: SdkVersion.Current,
                NativeShimDllSourcePath: shimSrc,
                ManagedHostBundleDir: hostDir,
                Mode: ExtenderInstallMode.ImportTable));

            Assert.True(install.Succeeded, install.Message);
            Assert.True(install.Status.IsInstalled);
            Assert.True(install.Status.IsHealthy);
            Assert.True(install.Status.PeImportVerified);
            Assert.NotEqual(syntheticExe, File.ReadAllBytes(patchedPath));

            // Remove extender -> unpatches Game.patched.exe byte-for-byte back to original synthetic PE32+
            var remove = await manager.RemoveAsync(localExileRoot);
            Assert.True(remove.Succeeded, remove.Message);
            Assert.False(remove.Status.IsInstalled);
            Assert.False(remove.Status.PeImportVerified);
            Assert.Equal(syntheticExe, File.ReadAllBytes(patchedPath));
            Assert.Equal(stockExe, File.ReadAllBytes(stockPath));
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
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
            if (File.Exists(Path.Combine(dir, "NativeMods.slnx")))
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

    private static byte[] BuildMinimalSyntheticPe32Plus()
    {
        const int sectionCount = 2; // .text, .rdata
        const int optionalHeaderSize = 0xF0;
        const int sizeOfHeaders = 0x400;
        const int headerSlack = 136;
        int secTableStart = sizeOfHeaders - headerSlack - sectionCount * Pe32PlusImage.SectionHeaderSize;
        int peOffset = secTableStart - 24 - optionalHeaderSize;

        const uint textRva = 0x1000, textRawOff = 0x400, textRawSize = 0x200;
        const uint rdataRva = 0x2000, rdataRawOff = 0x600, rdataRawSize = 0x400;
        const uint sizeOfImage = 0x3000;
        byte[] img = new byte[rdataRawOff + rdataRawSize];

        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0, 2), Pe32PlusImage.DosMagic);
        BinaryPrimitives.WriteInt32LittleEndian(img.AsSpan(0x3C, 4), peOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(peOffset, 4), Pe32PlusImage.PeSignature);

        int fh = peOffset + 4;
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(fh, 2), Pe32PlusImage.MachineAmd64);
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(fh + 2, 2), sectionCount);
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(fh + 16, 2), optionalHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(fh + 18, 2), 0x0022);

        int opt = peOffset + 24;
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(opt, 2), Pe32PlusImage.Pe32PlusMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(opt + 16, 4), textRva);
        BinaryPrimitives.WriteUInt64LittleEndian(img.AsSpan(opt + 24, 8), 0x140000000UL);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(opt + 32, 4), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(opt + 36, 4), 0x200);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(opt + 56, 4), sizeOfImage);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(opt + 60, 4), sizeOfHeaders);
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(opt + 70, 2), 0x8160);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(opt + 108, 4), 16);

        img[textRawOff] = 0xC3;

        // Single KERNEL32.dll import in .rdata
        int impOff = (int)rdataRawOff;
        int iltOff = impOff + 2 * Pe32PlusImage.ImportDescriptorSize;
        int iatOff = iltOff + 16;
        int hnOff = iatOff + 16;
        byte[] fn = "GetModuleHandleA"u8.ToArray();
        Buffer.BlockCopy(fn, 0, img, hnOff + 2, fn.Length);
        int nameOff = (hnOff + 2 + fn.Length + 8) & ~7;
        byte[] dll = "KERNEL32.dll"u8.ToArray();
        Buffer.BlockCopy(dll, 0, img, nameOff, dll.Length);

        uint iltRva = rdataRva + (uint)(iltOff - rdataRawOff);
        uint iatRva = rdataRva + (uint)(iatOff - rdataRawOff);
        uint hnRva = rdataRva + (uint)(hnOff - rdataRawOff);
        uint nameRva = rdataRva + (uint)(nameOff - rdataRawOff);

        BinaryPrimitives.WriteUInt64LittleEndian(img.AsSpan(iltOff, 8), hnRva);
        BinaryPrimitives.WriteUInt64LittleEndian(img.AsSpan(iatOff, 8), hnRva);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(impOff + 0, 4), iltRva);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(impOff + 12, 4), nameRva);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(impOff + 16, 4), iatRva);

        int ddOff = opt + 112;
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(ddOff + 8, 4), rdataRva);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(ddOff + 12, 4), 2 * Pe32PlusImage.ImportDescriptorSize);

        WriteSec(img, secTableStart, ".text", textRawSize, textRva, textRawSize, textRawOff, 0x60000020u);
        WriteSec(img, secTableStart + 40, ".rdata", rdataRawSize, rdataRva, rdataRawSize, rdataRawOff, 0x40000040u);

        uint checksum = Pe32PlusImage.ComputePeChecksum(img, opt + 64);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(opt + 64, 4), checksum);
        return img;
    }

    private static void WriteSec(byte[] img, int off, string name, uint vSize, uint vAddr, uint rawSize, uint rawOff, uint chars)
    {
        byte[] n = Encoding.ASCII.GetBytes(name);
        Buffer.BlockCopy(n, 0, img, off, Math.Min(8, n.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(off + 8, 4), vSize);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(off + 12, 4), vAddr);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(off + 16, 4), rawSize);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(off + 20, 4), rawOff);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(off + 36, 4), chars);
    }
}
