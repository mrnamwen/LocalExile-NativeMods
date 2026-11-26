using System.Runtime.CompilerServices;
using DualMod.IdolsAndGearVault.Server;
using DualMod.IdolsAndGearVault.Shared;
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

namespace DualMod.IdolsAndGearVault.Tests;

public sealed class IdolsVaultClientAndEndToEndTests
{
    [Theory]
    [InlineData(NetTransportKind.HostRuleset)]
    [InlineData(NetTransportKind.ChatFallback)]
    public async Task FullPhase4Showcase_IdolBoard_AltTooltips_GearAffinity_RichControls_LiveExtend_AndCleanAlcUnload(
        NetTransportKind transport)
    {
        string workspace = Path.Combine(Path.GetTempPath(), "nm-idolsvault-e2e-" + Guid.NewGuid().ToString("N"));
        string localExileRoot = Path.Combine(workspace, "LocalExile");
        string stagingPkg = Path.Combine(workspace, "idolsvault-pkg");
        string shadowRoot = Path.Combine(workspace, "shadow");
        string keybindsPath = Path.Combine(workspace, "keybinds.json");

        try
        {
            // 1. Seed fake LocalExile checkout with stock files that must remain untouched.
            string stockClientDir = Path.Combine(localExileRoot, "Client");
            Directory.CreateDirectory(Path.Combine(stockClientDir, "Bundles2"));
            byte[] stockExe = "STOCK_POE_3_29_3D_EXE"u8.ToArray();
            File.WriteAllBytes(Path.Combine(stockClientDir, "Game.exe"), stockExe);

            // 2. Stage DualMod.IdolsAndGearVault with compiled Server + Client DLLs and validate package directory.
            string repoRoot = IdolsVaultManifestAndUiTests.FindRepoRoot();
            string exampleRoot = Path.Combine(repoRoot, "examples", "DualMod.IdolsAndGearVault");
            CopyDirectory(exampleRoot, stagingPkg);
            File.Copy(FindCompiledAssembly(exampleRoot, "Client", "idolsvault.Client.dll"), Path.Combine(stagingPkg, "idolsvault.Client.dll"), overwrite: true);
            File.Copy(FindCompiledAssembly(exampleRoot, "Server", "idolsvault.dll"), Path.Combine(stagingPkg, "idolsvault.dll"), overwrite: true);

            ValidationReport validation = DualModPackageValidator.ValidatePackageDirectory(stagingPkg, SemVer.Parse(SdkVersion.Current));
            Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors.Select(e => $"{e.Code}: {e.Message}")));
            Assert.Empty(validation.Warnings);

            var installer = new WaypointModInstaller();
            var installReport = installer.Install(stagingPkg, localExileRoot, new InstallOptions
            {
                CurrentSdkVersion = SemVer.Parse(SdkVersion.Current),
                PackClientBundle = true,
                DeployLooseOverlays = true,
            });
            Assert.True(installReport.Succeeded, installReport.ToHumanReadableSummary());
            string deployedClientDir = installReport.ClientTargetDir!;

            // 3. Boot ClientHostRuntime with SimulatedNativeBridge (3.29.3d) + Simulated Native UI.
            using var simBridge = new SimulatedNativeBridge(ClientBuildId.Build3293d)
            {
                EnableSimulatedNativeUi = true,
            };
            using var clientHost = new ClientHostRuntime(simBridge, new ClientHostOptions
            {
                TransportKind = transport,
                HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId,
                ShadowCopyRoot = shadowRoot,
                KeybindsPath = keybindsPath,
            });
            clientHost.Start();

            await RunActiveShowcaseSessionAsync(simBridge, clientHost, deployedClientDir, transport);

            // 4. Verify collectible ALC clean unload + COMPLETE teardown of every Phase 4 & Extend registration!
            var unloadResult = await clientHost.Loader.UnloadModAsync(IdolsVaultProtocol.ModId);
            Assert.True(unloadResult.WasLoaded);
            Assert.True(unloadResult.UnloadedCleanly);

            // Zero lingering tooltips or stock tooltip augmenters.
            Assert.False(clientHost.Tooltips.IsCustomTooltipVisible);
            HoveredItemInfo probeIdol = CreateHoveredItem(
                metadataPath: "Metadata/Items/Idols/TotemicIdol",
                baseName: "Totemic Idol",
                width: 1,
                height: 3,
                rarity: ItemRarity.Rare,
                itemLevel: 86);
            Assert.Empty(simBridge.SimulateItemTooltipBuild(probeIdol, isAltHeld: true));

            // Zero lingering stash affinity routes or stock affinity bit rules.
            HoveredItemInfo probeBody = CreateHoveredItem(
                metadataPath: "Metadata/Items/Armours/BodyArmours/BodyStr1",
                baseName: "Astral Plate",
                rarity: ItemRarity.Rare,
                itemLevel: 86);
            Assert.Empty(simBridge.SimulateStashAffinityRoute(new StashAffinityItemContext(probeBody, StashKind.Personal)));
            Assert.Equal(0u, simBridge.SimulateStashAffinityBitset(new StashAffinityItemContext(probeBody, StashKind.Personal)));

            // Zero lingering Extend registrations (GEAL, 0xAE ruleset, custom UI control kind).
            Assert.DoesNotContain(IdolsVaultProtocol.GealBuiltinIdolBoardCount, clientHost.GealBuiltins.RegisteredNames);
            Assert.DoesNotContain(IdolsVaultProtocol.GealBuiltinIdolBoardCount, simBridge.RegisteredNativeGealBuiltins);
            Assert.DoesNotContain(IRulesetRegistry.FirstFreeNativeRulesetId, clientHost.Rulesets.RegisteredIds);
            Assert.DoesNotContain(IRulesetRegistry.FirstFreeNativeRulesetId, simBridge.RegisteredNativeRulesetIds);
            Assert.DoesNotContain(IdolsVaultProtocol.CustomControlStatusBadge, clientHost.UiControls.RegisteredControlTypes);
            Assert.DoesNotContain(IdolsVaultProtocol.CustomControlStatusBadge, simBridge.RegisteredNativeCustomUiControlKinds);

            // Zero lingering slots, keybinds, or open panel window.
            Assert.DoesNotContain(
                clientHost.Screens.GetSlotAttachments(ScreenSlot.HudTopRight),
                a => a.Id.StartsWith("idolsvault.", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                clientHost.Screens.GetSlotAttachments(ScreenSlot.StashHeader),
                a => a.Id.StartsWith("idolsvault.", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                clientHost.Input.Keybinds.Entries,
                k => k.Id.StartsWith("idolsvault.", StringComparison.OrdinalIgnoreCase));
            Assert.Null(clientHost.Ui.GetWindow(IdolsVaultProtocol.PanelWindowId));

            // 5. Clean uninstall leaves stock Game.exe untouched.
            var uninstall = installer.Uninstall(IdolsVaultProtocol.ModId, localExileRoot);
            Assert.True(uninstall.Succeeded);
            Assert.Equal(stockExe, File.ReadAllBytes(Path.Combine(stockClientDir, "Game.exe")));
        }
        finally
        {
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task RunActiveShowcaseSessionAsync(
        SimulatedNativeBridge simBridge,
        ClientHostRuntime clientHost,
        string deployedClientDir,
        NetTransportKind transport)
    {
        simBridge.SimulatedInGameUiHandle = 0x5555;
        simBridge.SimulatedActivePanels = GamePanel.Hud | GamePanel.Stash;

        var loadedMod = clientHost.Loader.LoadModFromDirectory(deployedClientDir);
        Assert.Equal(IdolsVaultProtocol.ModId, loadedMod.Manifest.Id);
        Assert.False(loadedMod.IsDisabled);
        simBridge.SimulateFrame(0.016f);

        // Wire loopback ClientBridgeHost + IdolsVaultServerMod.
        const string connId = "exile-idolsvault-e2e";
        var sink = new LoopbackGatewaySink(simBridge);
        var serverBridge = new ClientBridgeHost(sink, new ClientBridgeOptions
        {
            DefaultTransport = transport,
            HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId,
        });
        simBridge.OnOutgoingRulesetPacket = (rulesetId, innerPayload) =>
        {
            byte[] body = MessageCodec.EncodeRulesetBody(rulesetId, innerPayload.Span);
            serverBridge.HandleIncomingPacketAsync(connId, IOpcodeRegistry.ClientRulesetOpcode, body).GetAwaiter().GetResult();
        };
        simBridge.OnOutgoingChatLine = line =>
        {
            serverBridge.HandleIncomingChatCommandAsync(connId, line).GetAwaiter().GetResult();
        };

        var backend = new InMemoryIdolsVaultBackend(sink);
        backend.AddVaultItem(connId, new GearVaultItemRecord(201, "Vortex Crown", "Royal Burgonet", "Metadata/Items/Armours/Helmets/HelmetStr1", GearVaultClasses.Helmets, 86, 2, 4, ["Defence", "Life"]));
        // Add 10 Body Armours in Tab 37 to verify IScrollableFrame non-zero MaxScrollOffset + scrolling!
        for (uint i = 0; i < 10; i++)
        {
            backend.AddVaultItem(connId, new GearVaultItemRecord(
                ItemId: 202 + i,
                Name: i == 0 ? "Titan Carapace" : $"Vault Plate #{i + 1}",
                BaseName: i % 2 == 0 ? "Astral Plate" : "Vaal Regalia",
                MetadataPath: i % 2 == 0 ? "Metadata/Items/Armours/BodyArmours/BodyStr1" : "Metadata/Items/Armours/BodyArmours/BodyInt1",
                GearClass: GearVaultClasses.BodyArmours,
                ItemLevel: (int)(86 - (i % 4)),
                Rarity: i == 2 ? 3 : 2,
                TabIndex: 37,
                AffixTags: ["Defence", "Life", "Resistance"]));
        }

        using var serverMod = new IdolsVaultServerMod(serverBridge, backend);
        serverMod.Attach();

        clientHost.SendHandshake();
        IClientSession session = serverBridge.For(connId);
        Assert.True(session.HasNativeHost);
        Assert.True(session.HasClientMod(IdolsVaultProtocol.ModId));

        // ── A. Live Native Extend Registries Verified Immediately After Load ──
        Assert.Contains(IdolsVaultProtocol.GealBuiltinIdolBoardCount, simBridge.RegisteredNativeGealBuiltins);
        Assert.Contains(IRulesetRegistry.FirstFreeNativeRulesetId, simBridge.RegisteredNativeRulesetIds);
        Assert.DoesNotContain(IRulesetRegistry.DefaultHostChannelRulesetId, simBridge.RegisteredNativeRulesetIds);
        Assert.Contains(IdolsVaultProtocol.CustomControlStatusBadge, simBridge.RegisteredNativeCustomUiControlKinds);

        GealInvocationResult initialCount = simBridge.SimulateNativeGealBuiltinCall(IdolsVaultProtocol.GealBuiltinIdolBoardCount, 1u);
        Assert.True(initialCount.Succeeded);
        Assert.Equal(0, initialCount.ReturnValue.IntValue);
        GealInvocationResult capacityRes = simBridge.SimulateNativeGealBuiltinCall(IdolsVaultProtocol.GealBuiltinIdolBoardCount, 1u, GealValue.FromString("capacity"));
        Assert.Equal(32, capacityRes.ReturnValue.IntValue);

        // Verify custom .ui control kind ("IdolVault.StatusBadge") native lifecycle.
        nint badgeHandle = simBridge.SimulateNativeCustomControlCreated(IdolsVaultProtocol.CustomControlStatusBadge, "badge_1");
        Assert.NotEqual(nint.Zero, badgeHandle);
        Assert.Equal(1, clientHost.UiControls.LiveNativeInstanceCount);
        Assert.True(simBridge.SimulateNativeCustomControlProperty(badgeHandle, "badge_text", "12 / 32 Cells"));
        clientHost.TickFrame(0.016f);
        Assert.True(simBridge.SimulateNativeCustomControlDestroyed(badgeHandle));
        Assert.Equal(0, clientHost.UiControls.LiveNativeInstanceCount);

        // ── B. Open Panel via Alt+I Keybind (with !IsTextInputFocused guard) ──
        simBridge.SimulatedTextInputFocused = true;
        Assert.False(simBridge.SimulateKeyEvent('I', KeyModifiers.Alt, isDown: true));
        Assert.Null(clientHost.Ui.GetWindow(IdolsVaultProtocol.PanelWindowId));

        simBridge.SimulatedTextInputFocused = false;
        Assert.True(simBridge.SimulateKeyEvent('I', KeyModifiers.Alt, isDown: true));

        IUiWindow? panel = clientHost.Ui.GetWindow(IdolsVaultProtocol.PanelWindowId);
        Assert.NotNull(panel);

        ITabContainer tabs = panel!.Require<ITabContainer>("idolsvault_tabs");
        Assert.Equal(2, tabs.TabCount);
        Assert.Equal(0, tabs.ActiveTabIndex);

        // ── C. Wishlist #13: 32-Cell 6x6 Idol Board (4 Blocked Corners + Multi-Size + Hover Tooltips) ──
        IUiItemGrid grid = panel.Require<IUiItemGrid>("idol_board_grid");
        Assert.Equal(6, grid.Columns);
        Assert.Equal(6, grid.Rows);
        foreach (var (c, r) in new[] { (0, 0), (5, 0), (0, 5), (5, 5) })
        {
            IUiControl? cornerCell = grid.Find($"cell_{c}_{r}");
            Assert.NotNull(cornerCell);
            Assert.Equal("true", cornerCell!.GetProperty("blocked"));
            Assert.False(cornerCell.Require<IButton>($"cell_btn_{c}_{r}").Enabled);
        }

        // Clicking blocked corner (0,0) does NOT place an Idol and reports locked corner feedback.
        grid.TriggerCellClick(0, 0, MouseButton.Left, KeyModifiers.None);
        Assert.Empty(grid.Items);
        Assert.Contains("locked", panel.Require<ILabel>("status_label").Text, StringComparison.OrdinalIgnoreCase);

        // Place Minor (1x1) at (1,0), Totemic (1x3) at (0,1), and Conqueror (2x2) at (1,1) via preset combo + grid clicks!
        IComboBox presetCombo = panel.Require<IComboBox>("idol_preset_combo");
        Assert.Equal(6, presetCombo.ItemCount);

        presetCombo.SelectedIndex = 0; // Minor 1x1
        grid.TriggerCellClick(1, 0, MouseButton.Left, KeyModifiers.None);
        Assert.Single(grid.Items);

        presetCombo.SelectedIndex = 4; // Totemic 1x3
        grid.TriggerCellClick(0, 1, MouseButton.Left, KeyModifiers.None);
        Assert.Equal(2, grid.Items.Count);

        presetCombo.SelectedIndex = 3; // Conqueror 2x2
        grid.TriggerCellClick(1, 1, MouseButton.Left, KeyModifiers.None);
        Assert.Equal(3, grid.Items.Count);
        Assert.Equal("Conqueror Idol of Dominance", grid.GetItemAt(2, 2)!.Title);

        // Live GEAL builtin & 0xAE native ruleset telemetry both reflect 3 idols / 8 occupied cells!
        Assert.Equal(3, simBridge.SimulateNativeGealBuiltinCall(IdolsVaultProtocol.GealBuiltinIdolBoardCount, 1u).ReturnValue.IntValue);
        Assert.Equal(8, simBridge.SimulateNativeGealBuiltinCall(IdolsVaultProtocol.GealBuiltinIdolBoardCount, 1u, GealValue.FromString("cells")).ReturnValue.IntValue);
        Assert.Equal(24, simBridge.SimulateNativeGealBuiltinCall(IdolsVaultProtocol.GealBuiltinIdolBoardCount, 1u, GealValue.FromString("free")).ReturnValue.IntValue);
        Assert.Contains("8 / 32 active cells", panel.Require<ILabel>("board_occupancy_label").Text, StringComparison.Ordinal);
        Assert.Contains("sync=3/8", panel.Require<ILabel>("extend_summary_label").Text, StringComparison.Ordinal);

        // Hovering a placed Idol shows its custom anchored tooltip; hovering an empty cell hides it.
        grid.TriggerCellHover(1, 1);
        Assert.True(clientHost.Tooltips.IsCustomTooltipVisible);
        Assert.Equal("Conqueror Idol of Dominance", clientHost.Tooltips.ActiveCustomTooltip!.Title);
        Assert.Contains(
            clientHost.Tooltips.ActiveCustomTooltip.Sections.SelectMany(s => s.Lines),
            line => line.Text.Contains("[Affix Tags:", StringComparison.Ordinal));

        grid.TriggerCellHover(4, 4);
        Assert.False(clientHost.Tooltips.IsCustomTooltipVisible);

        // Right-clicking placed 1x1 Idol at (1,0) removes it via server RPC.
        grid.TriggerCellClick(1, 0, MouseButton.Right, KeyModifiers.None);
        Assert.Equal(2, grid.Items.Count);
        Assert.Null(grid.GetItemAt(1, 0));
        Assert.Equal(2, simBridge.SimulateNativeGealBuiltinCall(IdolsVaultProtocol.GealBuiltinIdolBoardCount, 1u).ReturnValue.IntValue);
        Assert.Equal(7, simBridge.SimulateNativeGealBuiltinCall(IdolsVaultProtocol.GealBuiltinIdolBoardCount, 1u, GealValue.FromString("cells")).ReturnValue.IntValue);

        // ── D. Wishlist #13: Alt-Held Stock Item Tooltip Augmentation ──
        HoveredItemInfo stockIdol = CreateHoveredItem(
            metadataPath: "Metadata/Items/Idols/TotemicIdol",
            baseName: "Totemic Idol",
            width: 1,
            height: 3,
            rarity: ItemRarity.Rare,
            itemLevel: 86);
        Assert.Empty(simBridge.SimulateItemTooltipBuild(stockIdol, isAltHeld: false));
        IReadOnlyList<TooltipSection> idolAltSections = simBridge.SimulateItemTooltipBuild(stockIdol, isAltHeld: true);
        TooltipSection idolSection = Assert.Single(idolAltSections);
        Assert.True(idolSection.PrependSeparator);
        Assert.Contains("[Affix Tags: Attack, Life, Idol", idolSection.Lines[0].Text, StringComparison.Ordinal);
        Assert.Contains("Tier Summary:", idolSection.Lines[1].Text, StringComparison.Ordinal);

        HoveredItemInfo rareBody = CreateHoveredItem(
            metadataPath: "Metadata/Items/Armours/BodyArmours/BodyStr1",
            baseName: "Astral Plate",
            rarity: ItemRarity.Rare,
            itemLevel: 86);
        IReadOnlyList<TooltipSection> bodyAltSections = simBridge.SimulateItemTooltipBuild(rareBody, isAltHeld: true);
        TooltipSection bodySection = Assert.Single(bodyAltSections);
        Assert.Contains("[Affix Tags: Defence, Life, Resistance, BodyArmours]", bodySection.Lines[0].Text, StringComparison.Ordinal);

        // ── E. Wishlist #14: Custom Gear-Affinity Stash Tab + Rich Controls ──
        tabs.SelectTab(1);
        Assert.Equal(1, tabs.ActiveTabIndex);

        IComboBox classCombo = panel.Require<IComboBox>("gear_class_combo");
        IComboBox tabCombo = panel.Require<IComboBox>("vault_tab_combo");
        IScrollableFrame scrollFrame = panel.Require<IScrollableFrame>("vault_scroll_frame");
        IListBox vaultList = panel.Require<IListBox>("vault_items_listbox");

        Assert.Equal(8, classCombo.ItemCount);
        Assert.Equal(IdolsVaultProtocol.MaxTabChoicesInCombo, tabCombo.ItemCount);

        // Switch Gear Vault affinity to Body Armours -> Stash Tab 37 (arbitrary tab beyond stock 0..21 cap!).
        classCombo.SelectedIndex = 1; // Body Armours
        tabCombo.SelectedIndex = 37;  // Stash Tab 37

        // Vault ListBox now displays all 10 Body Armours from Tab 37 with scrollable viewport overflow!
        Assert.Equal(10, vaultList.ItemCount);
        Assert.True(scrollFrame.MaxScrollOffset > 0f);
        scrollFrame.SetScrollOffset(32f);
        Assert.Equal(32f, scrollFrame.ScrollOffset);
        vaultList.SelectedIndex = 0;
        Assert.Equal(0, vaultList.SelectedIndex);
        Assert.Contains("Titan Carapace", panel.Require<ILabel>("vault_selected_label").Text, StringComparison.Ordinal);
        scrollFrame.UpdateContentBounds();

        // Exercise native 0xAE ruleset area lifecycle callbacks cleanly.
        simBridge.SimulateAreaEntered(new AreaInfo("MapWorldsCrimsonTemple", "Crimson Temple Map", Act: 11, AreaLevel: 83, EffectiveLevel: 83, Hash16: 0x4321, IsTown: false, IsHideout: false, IsGuildHideout: false, ActiveRulesetIds: [IRulesetRegistry.FirstFreeNativeRulesetId]));
        simBridge.SimulateFrame(0.016f);
        simBridge.SimulateAreaLeft("MapWorldsCrimsonTemple");

        // Verify IStashAffinityService priority ordering: add a fallback route at priority +10 and confirm our route (-5) wins first!
        using (clientHost.StashAffinity.RegisterRoute(
            "test.fallback_route",
            _ => new StashAffinityRoute(TabIndex: 9, AffinityId: 0, ReplaceStockTargets: false),
            priority: 10))
        {
            IReadOnlyList<StashAffinityRoute> bodyRoutes = simBridge.SimulateStashAffinityRoute(
                new StashAffinityItemContext(rareBody, StashKind.Personal));
            Assert.Equal(2, bodyRoutes.Count);
            Assert.Equal((ushort)37, bodyRoutes[0].TabIndex);
            Assert.True(bodyRoutes[0].ReplaceStockTargets);
            Assert.Equal((ushort)9, bodyRoutes[1].TabIndex);

            // Non-matching item (Helmet) does NOT hit the Body Armours Gear Vault route (only fallback Tab 9).
            HoveredItemInfo helmet = CreateHoveredItem(
                metadataPath: "Metadata/Items/Armours/Helmets/HelmetStr1",
                baseName: "Royal Burgonet",
                rarity: ItemRarity.Rare,
                itemLevel: 85);
            IReadOnlyList<StashAffinityRoute> helmetRoutes = simBridge.SimulateStashAffinityRoute(
                new StashAffinityItemContext(helmet, StashKind.Personal));
            StashAffinityRoute onlyFallback = Assert.Single(helmetRoutes);
            Assert.Equal((ushort)9, onlyFallback.TabIndex);
        }

        // Verify optional RegisterStockAffinityRule bitset mapping (bit 4 enabled -> toggle off -> 0).
        Assert.Equal(1u << 4, simBridge.SimulateStashAffinityBitset(new StashAffinityItemContext(rareBody, StashKind.Personal)));
        ICheckBox stockCheck = panel.Require<ICheckBox>("stock_affinity_checkbox");
        stockCheck.IsChecked = false;
        Assert.Equal(0u, simBridge.SimulateStashAffinityBitset(new StashAffinityItemContext(rareBody, StashKind.Personal)));

        await Task.CompletedTask;
    }

    private static HoveredItemInfo CreateHoveredItem(
        string metadataPath,
        string baseName,
        int width = 2,
        int height = 3,
        ItemRarity rarity = ItemRarity.Rare,
        int itemLevel = 86,
        uint itemClassId = 0) =>
        new(
            EntityHandle: 0x4100,
            ControlHandle: 0x5100,
            ItemId: 9001u,
            InventoryId: 1u,
            GridX: 0,
            GridY: 0,
            Width: width,
            Height: height,
            MetadataPath: metadataPath,
            BaseName: baseName,
            StackSize: 1u,
            MaxStackSize: 1u,
            Rarity: rarity,
            IsIdentified: true,
            IsCorrupted: false,
            ItemLevel: itemLevel,
            ItemClassId: itemClassId);

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

    private static string FindCompiledAssembly(string exampleRoot, string subDir, string fileName)
    {
        string binDir = Path.Combine(exampleRoot, subDir, "bin");
        foreach (string config in new[] { "Release", "Debug" })
        {
            string candidate = Path.Combine(binDir, config, "net10.0", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
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
            if (name is "bin" or "obj" or "Shared" or "Server" or "Client")
            {
                continue;
            }

            CopyDirectory(dir, Path.Combine(targetDir, name));
        }
    }
}
