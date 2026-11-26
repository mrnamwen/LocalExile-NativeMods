using System.Buffers.Binary;
using System.Runtime.CompilerServices;
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
using LocalExile.Client.Waypoint;
using Xunit;

namespace LocalExile.Client.SiphonOrb.Tests;

public sealed class SiphonOrbEndToEndTests
{
    [Theory]
    [InlineData(NetTransportKind.HostRuleset)]
    [InlineData(NetTransportKind.ChatFallback)]
    public async Task FullDualSide5StepFlow_InstallLoadCancelStealStoreImbueConsumeVanillaFallbackAndCleanUnload(
        NetTransportKind transport)
    {
        string workspace = Path.Combine(Path.GetTempPath(), "nm-siphon-e2e-" + Guid.NewGuid().ToString("N"));
        string localExileRoot = Path.Combine(workspace, "LocalExile");
        string stagingPkg = Path.Combine(workspace, "siphon-pkg");
        string shadowRoot = Path.Combine(workspace, "shadow");

        try
        {
            // 1. Seed fake LocalExile checkout with stock files that must never be mutated
            string stockClientDir = Path.Combine(localExileRoot, "Client");
            Directory.CreateDirectory(Path.Combine(stockClientDir, "Bundles2"));
            byte[] stockExe = "STOCK_POE_3_29_3D_EXE"u8.ToArray();
            byte[] stockIndex = "STOCK_POE_INDEX_BIN"u8.ToArray();
            File.WriteAllBytes(Path.Combine(stockClientDir, "Game.exe"), stockExe);
            File.WriteAllBytes(Path.Combine(stockClientDir, "Bundles2", "_.index.bin"), stockIndex);

            // 2. Stage DualMod.SiphonOrb with compiled Server + Client DLLs and synthetic patched .datc64 overlays
            string repoRoot = FindRepoRoot();
            string exampleRoot = Path.Combine(repoRoot, "examples", "DualMod.SiphonOrb");
            CopyDirectory(exampleRoot, stagingPkg);

            File.Copy(FindCompiledAssembly(exampleRoot, "Client", "siphon.Client.dll"), Path.Combine(stagingPkg, "siphon.Client.dll"), overwrite: true);
            File.Copy(FindCompiledAssembly(exampleRoot, "Server", "siphon.dll"), Path.Combine(stagingPkg, "siphon.dll"), overwrite: true);

            string stagedDataDir = Path.Combine(stagingPkg, "client", "Data");
            Directory.CreateDirectory(stagedDataDir);
            byte[] syntheticBaseItems = BuildSyntheticDat64(rowCount: 5463, rowStride: 16);
            byte[] syntheticVisuals = BuildSyntheticDat64(rowCount: 15518, rowStride: 16);
            byte[] syntheticCurrencies = BuildSyntheticDat64(rowCount: 1964, rowStride: 16);
            File.WriteAllBytes(Path.Combine(stagedDataDir, "BaseItemTypes.datc64"), syntheticBaseItems);
            File.WriteAllBytes(Path.Combine(stagedDataDir, "ItemVisualIdentity.datc64"), syntheticVisuals);
            File.WriteAllBytes(Path.Combine(stagedDataDir, "CurrencyItems.datc64"), syntheticCurrencies);

            // 3. Validate package and install via WaypointModInstaller
            var validation = DualModPackageValidator.ValidatePackageDirectory(stagingPkg, SemVer.Parse(SdkVersion.Current));
            Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors.Select(e => $"{e.Code}: {e.Message}")));

            var installer = new WaypointModInstaller();
            var installReport = installer.Install(stagingPkg, localExileRoot, new InstallOptions
            {
                CurrentSdkVersion = SemVer.Parse(SdkVersion.Current),
                PackClientBundle = true,
                DeployLooseOverlays = true
            });
            Assert.True(installReport.Succeeded, installReport.ToHumanReadableSummary());
            string deployedClientDir = installReport.ClientTargetDir!;
            Assert.True(File.Exists(Path.Combine(deployedClientDir, "siphon.Client.dll")));
            Assert.True(File.Exists(Path.Combine(deployedClientDir, "files", "metadata", "ui", "mods", "siphon", "siphonpicker.ui")));

            // 4. Boot ClientHostRuntime and load siphon.Client.dll in a collectible ALC
            using var simBridge = new SimulatedNativeBridge(ClientBuildId.Build3293d);
            using var clientHost = new ClientHostRuntime(simBridge, new ClientHostOptions
            {
                TransportKind = transport,
                HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId,
                ShadowCopyRoot = shadowRoot
            });
            clientHost.Start();

            await RunActiveSiphonSessionAsync(simBridge, clientHost, deployedClientDir, transport, syntheticBaseItems);

            // 5. Verify collectible ALC clean unload + zero lingering registrations + clean uninstall
            var unloadResult = await clientHost.Loader.UnloadModAsync(SiphonProtocol.ModId);
            Assert.True(unloadResult.WasLoaded);
            Assert.True(unloadResult.UnloadedCleanly);
            Assert.False(clientHost.Rulesets.IsRegistered(SiphonProtocol.SiphonRulesetId));
            Assert.False(clientHost.Opcodes.IsRegistered(SiphonProtocol.OpcodeOpenPicker));
            Assert.Null(clientHost.Ui.GetWindow(SiphonProtocol.PickerWindowId));

            var uninstall = installer.Uninstall(SiphonProtocol.ModId, localExileRoot);
            Assert.True(uninstall.Succeeded);
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
    private static async Task RunActiveSiphonSessionAsync(
        SimulatedNativeBridge simBridge,
        ClientHostRuntime clientHost,
        string deployedClientDir,
        NetTransportKind transport,
        byte[] expectedBaseItemsDat)
    {
        var loadedMod = clientHost.Loader.LoadModFromDirectory(deployedClientDir);
        Assert.Equal(SiphonProtocol.ModId, loadedMod.Manifest.Id);
        Assert.False(loadedMod.IsDisabled);

        Assert.True(clientHost.Rulesets.IsRegistered(SiphonProtocol.SiphonRulesetId));
        Assert.True(clientHost.Opcodes.IsRegistered(SiphonProtocol.OpcodeOpenPicker));
        Assert.Contains(clientHost.Content.DatRows, r => r.TableName == "BaseItemTypes" && r.EntryKey == SiphonProtocol.EmptyOrbBaseId);
        Assert.Contains(clientHost.Content.DatRows, r => r.TableName == "BaseItemTypes" && r.EntryKey == SiphonProtocol.ChargedOrbBaseId);
        Assert.Equal(expectedBaseItemsDat, clientHost.FileOverlayService.ReadAllBytes(SiphonProtocol.BaseItemTypesDatPath));

        const string richConnId = "exile-siphon-rich";
        const string vanillaConnId = "exile-siphon-vanilla";
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

        var backend = new SimulatedSiphonWorldBackend();
        using var serverMod = new SiphonServerMod(serverBridge, backend);
        serverMod.Attach();

        Assert.Equal(2, backend.RegisteredCurrencies.Count);

        // Handshake
        clientHost.SendHandshake();
        var richSession = serverBridge.For(richConnId);
        Assert.True(richSession.HasNativeHost);
        Assert.True(richSession.HasClientMod(SiphonProtocol.ModId));

        // Seed 2 rare gear items into rich player's inventory
        var sourceAstralPlate = new PortableItem(
            ItemId: 501,
            InventoryId: 1,
            TypeHash: 0x10000001u,
            ItemName: "Doom Carapace",
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
                new PortableItemMod(ItemModSectionKind.Implicit, "AllResImplicit1", 0x1001, "Implicit", "+12% to all Elemental Resistances", false, false, "ImplicitRes", 1, [12]),
                new PortableItemMod(ItemModSectionKind.Explicit, "IncreasedLife10", 0x2001, "Prime", "+97 to maximum Life", true, false, "IncreasedLife", 1, [97]),
                new PortableItemMod(ItemModSectionKind.Explicit, "LocalArmourPercent7", 0x2002, "Unassailable", "98% increased Armour", true, false, "ArmourPct", 2, [98]),
                new PortableItemMod(ItemModSectionKind.Explicit, "FireResistance7", 0x2003, "of Tzteosh", "+44% to Fire Resistance", false, true, "FireResistance", 1, [44]),
                new PortableItemMod(ItemModSectionKind.Explicit, "ChaosResistance5", 0x2004, "of Bameth", "+31% to Chaos Resistance", false, true, "ChaosResistance", 2, [31]),
            ]);

        var destVaalRegalia = new PortableItem(
            ItemId: 502,
            InventoryId: 1,
            TypeHash: 0x10000002u,
            ItemName: "Bramble Shroud",
            BaseType: "Vaal Regalia",
            ItemClass: "Body Armour",
            Rarity: ItemRarityKind.Rare,
            ItemLevel: 86,
            Identified: true,
            Corrupted: false,
            Mirrored: false,
            FracturedCount: 1, // First explicit (index 0) is fractured!
            StackSize: 1,
            Mods:
            [
                new PortableItemMod(ItemModSectionKind.Explicit, "LocalEsPercent8", 0x3001, "Unfaltering", "105% increased Energy Shield", true, false, "EsPct", 1, [105]),
                new PortableItemMod(ItemModSectionKind.Explicit, "IncreasedLife3", 0x3002, "Hale", "+28 to maximum Life", true, false, "IncreasedLife", 8, [28]),
                new PortableItemMod(ItemModSectionKind.Explicit, "LocalFlatEs6", 0x3003, "Resplendent", "+86 to maximum Energy Shield", true, false, "FlatEs", 2, [86]),
                new PortableItemMod(ItemModSectionKind.Explicit, "ColdResistance6", 0x3004, "of Haast", "+41% to Cold Resistance", false, true, "ColdResistance", 2, [41]),
                new PortableItemMod(ItemModSectionKind.Explicit, "Intelligence6", 0x3005, "of the Genius", "+48 to Intelligence", false, true, "Intelligence", 2, [48]),
            ]);

        backend.AddItem(richConnId, sourceAstralPlate);
        backend.AddItem(richConnId, destVaalRegalia);

        // STEP 1: /siphon give 3 -> grants 3 empty Orbs of Siphoning
        string giveReply = await serverMod.HandleSiphonCommandAsync(richConnId, "SiphonHero", ["give", "3"]);
        Assert.Contains("Granted 3x Orb of Siphoning", giveReply);

        PortableItem emptyOrbStack = backend.GetAllItems(richConnId).Single(i => i.TypeHash == SiphonProtocol.EmptyOrbTypeHash);
        Assert.Equal((ushort)3, emptyOrbStack.StackSize);

        // STEP 2a: Right-click Empty Orb -> Left-click Doom Carapace (C->S 0x0071) -> opens Steal picker
        byte[] useEmptyPkt = LocalExileCurrencyRegistrar.EncodeUseItemOn(1, emptyOrbStack.ItemId, 1, sourceAstralPlate.ItemId);
        bool claimedSteal = await serverMod.CurrencyRegistrar.HandleUseItemOnPacketAsync(
            richConnId, "SiphonHero", useEmptyPkt, (invId, itemId) => backend.ResolveTypeHash(richConnId, invId, itemId));
        Assert.True(claimedSteal);

        IUiWindow? picker = clientHost.Ui.GetWindow(SiphonProtocol.PickerWindowId);
        Assert.NotNull(picker);
        Assert.True(picker!.Visible);
        Assert.Equal("Steal", picker.GetProperty("siphon_mode"));
        Assert.False(picker.Find<IUiControl>("stored_mod_box")!.Visible);

        // Rows 0..3 visible (4 affixes), rows 4..5 hidden!
        for (int i = 0; i < 4; i++)
            Assert.True(picker.Find<IUiControl>($"affix_row_{i}")!.Visible);
        Assert.False(picker.Find<IUiControl>("affix_row_4")!.Visible);
        Assert.False(picker.Find<IUiControl>("affix_row_5")!.Visible);
        Assert.Contains("+97 to maximum Life", picker.Require<ILabel>("affix_label_0").Text);

        // Cancel test: clicking Cancel closes picker and consumes ZERO orbs!
        picker.Require<IButton>("cancel_button").Click();
        Assert.False(picker.Visible);
        Assert.False(serverMod.HasPendingSession(richConnId));
        Assert.Equal((ushort)3, backend.GetItem(richConnId, 1, emptyOrbStack.ItemId)!.StackSize);

        // STEP 2b & STEP 3: Reopen Steal picker, select row 0 (+97 Life), click Confirm Siphon
        Assert.True(await serverMod.CurrencyRegistrar.HandleUseItemOnPacketAsync(
            richConnId, "SiphonHero", useEmptyPkt, (invId, itemId) => backend.ResolveTypeHash(richConnId, invId, itemId)));
        Assert.True(picker.Visible);

        picker.Require<IButton>("affix_btn_0").Click();
        Assert.Equal("[SELECTED]", picker.Require<IButton>("affix_btn_0").Text);
        picker.Require<IButton>("confirm_button").Click();

        // Picker closes, Empty Orb stack drops 3 -> 2, source item loses +97 Life (3 explicits left), Charged Orb created!
        Assert.False(picker.Visible);
        Assert.Equal((ushort)2, backend.GetItem(richConnId, 1, emptyOrbStack.ItemId)!.StackSize);
        PortableItem updatedSource = backend.GetItem(richConnId, 1, sourceAstralPlate.ItemId)!;
        Assert.Equal(3, LocalExileItemAffixService.Inspect(updatedSource).Affixes.Count);
        Assert.DoesNotContain(updatedSource.Mods, m => m.ModId == "IncreasedLife10");

        PortableItem chargedOrb = backend.GetAllItems(richConnId).Single(i => i.TypeHash == SiphonProtocol.ChargedOrbTypeHash);
        Assert.Equal((ushort)1, chargedOrb.StackSize);
        Assert.Equal(ItemRarityKind.Normal, chargedOrb.Rarity);
        StoredAffixPayload? storedOnOrb = LocalExileItemAffixService.ReadStoredAffix(chargedOrb);
        Assert.NotNull(storedOnOrb);
        Assert.Equal("IncreasedLife10", storedOnOrb!.ModId);
        Assert.Equal([97], storedOnOrb.RolledValues);

        // STEP 4: Right-click Charged Orb -> Left-click Bramble Shroud (C->S 0x0071) -> opens Imbue picker
        byte[] useChargedPkt = LocalExileCurrencyRegistrar.EncodeUseItemOn(1, chargedOrb.ItemId, 1, destVaalRegalia.ItemId);
        bool claimedImbue = await serverMod.CurrencyRegistrar.HandleUseItemOnPacketAsync(
            richConnId, "SiphonHero", useChargedPkt, (invId, itemId) => backend.ResolveTypeHash(richConnId, invId, itemId));
        Assert.True(claimedImbue);

        Assert.True(picker.Visible);
        Assert.Equal("Imbue", picker.GetProperty("siphon_mode"));
        Assert.True(picker.Find<IUiControl>("stored_mod_box")!.Visible);
        Assert.Contains("+97 to maximum Life", picker.Require<ILabel>("stored_mod_label").Text);

        // Verify row 0 is LOCKED (fractured), row 1 (+28 Life) is selectable (same-group upgrade!), rows 2/3/4 locked (duplicate Life group)!
        Assert.Equal("Locked", picker.Require<IButton>("affix_btn_0").Text);
        Assert.Equal("[SELECTED]", picker.Require<IButton>("affix_btn_1").Text); // Auto-selected first selectable row!
        Assert.Equal("Locked", picker.Require<IButton>("affix_btn_2").Text);
        Assert.Equal("Locked", picker.Require<IButton>("affix_btn_3").Text);
        Assert.Equal("Locked", picker.Require<IButton>("affix_btn_4").Text);
        Assert.False(picker.Find<IUiControl>("affix_row_5")!.Visible);

        // STEP 5: Click Confirm Imbue -> replaces +28 Life with exact +97 Life & consumes Charged Orb!
        picker.Require<IButton>("confirm_button").Click();
        Assert.False(picker.Visible);
        Assert.Null(backend.GetItem(richConnId, 1, chargedOrb.ItemId)); // Charged Orb consumed!

        PortableItem updatedDest = backend.GetItem(richConnId, 1, destVaalRegalia.ItemId)!;
        ItemAffixView destView = LocalExileItemAffixService.Inspect(updatedDest);
        Assert.Equal(5, destView.Affixes.Count);
        Assert.Equal("LocalEsPercent8", destView.Affixes[0].ModId);
        Assert.Equal("IncreasedLife10", destView.Affixes[1].ModId);
        Assert.Equal([97], destView.Affixes[1].RolledValues); // Exact rolled value preserved!

        // VANILLA CLIENT FALLBACK E2E (no NativeMods client connected on vanillaConnId)
        await serverMod.HandleSiphonCommandAsync(vanillaConnId, "VanillaExile", ["give", "2"]);
        PortableItem vanillaOrb = backend.GetAllItems(vanillaConnId).Single(i => i.TypeHash == SiphonProtocol.EmptyOrbTypeHash);
        backend.AddItem(vanillaConnId, sourceAstralPlate with { ItemId = 701 });
        backend.AddItem(vanillaConnId, destVaalRegalia with { ItemId = 702 });

        byte[] vanillaStealPkt = LocalExileCurrencyRegistrar.EncodeUseItemOn(1, vanillaOrb.ItemId, 1, 701);
        Assert.True(await serverMod.CurrencyRegistrar.HandleUseItemOnPacketAsync(
            vanillaConnId, "VanillaExile", vanillaStealPkt, (invId, itemId) => backend.ResolveTypeHash(vanillaConnId, invId, itemId)));
        Assert.Contains(backend.GetChatLog(vanillaConnId), line => line.Contains("/siphon pick", StringComparison.Ordinal));

        string vanillaPickSteal = await serverMod.HandleSiphonCommandAsync(vanillaConnId, "VanillaExile", ["pick", "1"]);
        Assert.Contains("Siphoned", vanillaPickSteal);

        PortableItem vanillaCharged = backend.GetAllItems(vanillaConnId).Single(i => i.TypeHash == SiphonProtocol.ChargedOrbTypeHash);
        byte[] vanillaImbuePkt = LocalExileCurrencyRegistrar.EncodeUseItemOn(1, vanillaCharged.ItemId, 1, 702);
        Assert.True(await serverMod.CurrencyRegistrar.HandleUseItemOnPacketAsync(
            vanillaConnId, "VanillaExile", vanillaImbuePkt, (invId, itemId) => backend.ResolveTypeHash(vanillaConnId, invId, itemId)));

        string vanillaPickImbue = await serverMod.HandleSiphonCommandAsync(vanillaConnId, "VanillaExile", ["pick", "2"]);
        Assert.Contains("Imbued", vanillaPickImbue);
        Assert.Null(backend.GetItem(vanillaConnId, 1, vanillaCharged.ItemId));
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

    private static byte[] BuildSyntheticDat64(int rowCount, int rowStride)
    {
        byte[] data = new byte[4 + rowCount * rowStride + 8];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0, 4), rowCount);
        data.AsSpan(4 + rowCount * rowStride, 8).Fill(0xBB);
        return data;
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

        throw new FileNotFoundException($"Could not locate compiled assembly {fileName} under {binDir}.");
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (string file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string dir in Directory.GetDirectories(sourceDir))
        {
            string name = Path.GetFileName(dir);
            if (name is "bin" or "obj" or "Shared" or "Server" or "Client" or "assets-src")
                continue;
            CopyDirectory(dir, Path.Combine(targetDir, name));
        }
    }
}
