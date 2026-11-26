using System.Runtime.CompilerServices;
using DualMod.StashQol.Server;
using DualMod.StashQol.Shared;
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

namespace DualMod.StashQol.Tests;

public sealed class StashQolClientAndEndToEndTests
{
    [Theory]
    [InlineData(NetTransportKind.HostRuleset)]
    [InlineData(NetTransportKind.ChatFallback)]
    public async Task FullDualSideFlow_ToolbarButtons_CtrlShiftRmb_RegexRoller_PobClipboardAndLevelUpToast_CleanUnload(
        NetTransportKind transport)
    {
        string workspace = Path.Combine(Path.GetTempPath(), "nm-stashqol-e2e-" + Guid.NewGuid().ToString("N"));
        string localExileRoot = Path.Combine(workspace, "LocalExile");
        string stagingPkg = Path.Combine(workspace, "stashqol-pkg");
        string shadowRoot = Path.Combine(workspace, "shadow");
        string keybindsPath = Path.Combine(workspace, "keybinds.json");

        try
        {
            // 1. Seed fake LocalExile checkout with stock files that must remain untouched.
            string stockClientDir = Path.Combine(localExileRoot, "Client");
            Directory.CreateDirectory(Path.Combine(stockClientDir, "Bundles2"));
            byte[] stockExe = "STOCK_POE_3_29_3D_EXE"u8.ToArray();
            File.WriteAllBytes(Path.Combine(stockClientDir, "Game.exe"), stockExe);

            // 2. Stage DualMod.StashQol with compiled Server + Client DLLs and validate package directory.
            string repoRoot = StashQolManifestAndUiTests.FindRepoRoot();
            string exampleRoot = Path.Combine(repoRoot, "examples", "DualMod.StashQol");
            CopyDirectory(exampleRoot, stagingPkg);
            File.Copy(FindCompiledAssembly(exampleRoot, "Client", "stashqol.Client.dll"), Path.Combine(stagingPkg, "stashqol.Client.dll"), overwrite: true);
            File.Copy(FindCompiledAssembly(exampleRoot, "Server", "stashqol.dll"), Path.Combine(stagingPkg, "stashqol.dll"), overwrite: true);

            ValidationReport validation = DualModPackageValidator.ValidatePackageDirectory(stagingPkg, SemVer.Parse(SdkVersion.Current));
            Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors.Select(e => $"{e.Code}: {e.Message}")));

            var installer = new WaypointModInstaller();
            var installReport = installer.Install(stagingPkg, localExileRoot, new InstallOptions
            {
                CurrentSdkVersion = SemVer.Parse(SdkVersion.Current),
                PackClientBundle = true,
                DeployLooseOverlays = true,
            });
            Assert.True(installReport.Succeeded, installReport.ToHumanReadableSummary());
            string deployedClientDir = installReport.ClientTargetDir!;

            // 3. Boot ClientHostRuntime with SimulatedNativeBridge (Build 3.29.3d) + Simulated Native UI.
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

            await RunActiveStashQolSessionAsync(simBridge, clientHost, deployedClientDir, transport);

            // 4. Verify collectible ALC clean unload + zero lingering slots/windows/keybinds + clean uninstall.
            var unloadResult = await clientHost.Loader.UnloadModAsync(StashQolProtocol.ModId);
            Assert.True(unloadResult.WasLoaded);
            Assert.True(unloadResult.UnloadedCleanly);
            Assert.Empty(clientHost.Screens.GetSlotAttachments(ScreenSlot.InventoryToolbar));
            Assert.Empty(clientHost.Screens.GetSlotAttachments(ScreenSlot.StashHeader));
            Assert.Empty(clientHost.Screens.GetSlotAttachments(ScreenSlot.MapTabBar));
            Assert.Empty(clientHost.Screens.GetSlotAttachments(ScreenSlot.BlightTabBar));
            Assert.Null(clientHost.Ui.GetWindow(StashQolProtocol.RegexRollerWindowId));
            Assert.Null(clientHost.Ui.GetWindow(StashQolProtocol.PobImportWindowId));

            var uninstall = installer.Uninstall(StashQolProtocol.ModId, localExileRoot);
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
    private static async Task RunActiveStashQolSessionAsync(
        SimulatedNativeBridge simBridge,
        ClientHostRuntime clientHost,
        string deployedClientDir,
        NetTransportKind transport)
    {
        var loadedMod = clientHost.Loader.LoadModFromDirectory(deployedClientDir);
        Assert.Equal(StashQolProtocol.ModId, loadedMod.Manifest.Id);
        Assert.False(loadedMod.IsDisabled);

        // Register simulated stock panel handles and open Inventory + Stash + Map + Blight panels.
        var invPanel = simBridge.RegisterSimulatedStockControl("InventoryPanel");
        var stashPanel = simBridge.RegisterSimulatedStockControl("StashPanel");
        var mapPanel = simBridge.RegisterSimulatedStockControl("MapStashPanel");
        var blightPanel = simBridge.RegisterSimulatedStockControl("BlightStashPanel");
        simBridge.SetSimulatedPanelHandle(GamePanel.Inventory, invPanel.Handle);
        simBridge.SetSimulatedPanelHandle(GamePanel.Stash, stashPanel.Handle);
        simBridge.SetSimulatedPanelHandle(GamePanel.MapStash, mapPanel.Handle);
        simBridge.SetSimulatedPanelHandle(GamePanel.BlightStash, blightPanel.Handle);
        simBridge.SimulatedInGameUiHandle = 0x5555;
        simBridge.SimulatedActivePanels = GamePanel.Inventory | GamePanel.Stash | GamePanel.MapStash | GamePanel.BlightStash;
        simBridge.SimulatedActiveStashTab = new StashTabInfo(Index: 0, Id: 100u, Type: StashTabType.Normal, Name: "Dump");
        simBridge.SimulateFrame(0.016f);

        // Verify all 7 managed toolbar buttons mounted with non-overlapping rects!
        IReadOnlyList<ISlotAttachment> invSlots = clientHost.Screens.GetSlotAttachments(ScreenSlot.InventoryToolbar);
        IReadOnlyList<ISlotAttachment> stashSlots = clientHost.Screens.GetSlotAttachments(ScreenSlot.StashHeader);
        IReadOnlyList<ISlotAttachment> mapSlots = clientHost.Screens.GetSlotAttachments(ScreenSlot.MapTabBar);
        IReadOnlyList<ISlotAttachment> blightSlots = clientHost.Screens.GetSlotAttachments(ScreenSlot.BlightTabBar);
        Assert.Equal(4, invSlots.Count);
        Assert.Single(stashSlots);
        Assert.Single(mapSlots);
        Assert.Single(blightSlots);
        Assert.All(invSlots, s => Assert.True(s.IsAttached));
        for (int i = 1; i < invSlots.Count; i++)
        {
            Assert.True(invSlots[i].ComputedRect.X >= invSlots[i - 1].ComputedRect.X + invSlots[i - 1].ComputedRect.Width);
        }

        // Wire loopback ClientBridgeHost + StashQolServerMod.
        const string connId = "exile-stashqol-e2e";
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

        var backend = new InMemoryStashQolWorldBackend();
        using var serverMod = new StashQolServerMod(serverBridge, backend);
        serverMod.Attach();

        clientHost.SendHandshake();
        IClientSession session = serverBridge.For(connId);
        Assert.True(session.HasNativeHost);
        Assert.True(session.HasClientMod(StashQolProtocol.ModId));

        // Seed player state covering all 8 QoL features.
        var player = backend.GetOrCreatePlayer(connId);
        player.StashTabs.AddRange(
        [
            new QolStashTab(0, "Dump", QolStashTabKind.Normal),
            new QolStashTab(1, "Currency", QolStashTabKind.Currency, AffinityMask: 1u << 1),
            new QolStashTab(2, "Maps", QolStashTabKind.Map, IsSubTab: false),
            new QolStashTab(3, "T1", QolStashTabKind.Map, IsSubTab: true, ParentTabIndex: 2, MapGroup: 0, MapTier: 1),
            new QolStashTab(4, "T2", QolStashTabKind.Map, IsSubTab: true, ParentTabIndex: 2, MapGroup: 0, MapTier: 2),
            new QolStashTab(5, "Blight", QolStashTabKind.Blight),
        ]);
        player.CurrencyPool[StashQolProtocol.ChaosOrbMetadata] = 25;
        player.MainInventory.AddRange(
        [
            new QolItemRecord { Id = 1, MetadataPath = StashQolProtocol.ChaosOrbMetadata, BaseName = "Chaos Orb", ItemClass = "StackableCurrency", StackSize = 5, MaxStackSize = 20, Affinities = [1], X = 10, Y = 4 },
            new QolItemRecord { Id = 2, MetadataPath = "Metadata/Items/Armours/BodyArmours/BodyStr1", BaseName = "Astral Plate", ItemClass = "Body Armour", Width = 2, Height = 3, X = 8, Y = 1, Rarity = QolItemRarity.Rare },
            new QolItemRecord { Id = 3, MetadataPath = StashQolProtocol.StackedDeckMetadata, BaseName = "Stacked Deck", ItemClass = "StackableCurrency", StackSize = 4, MaxStackSize = 20, X = 5, Y = 2 },
        ]);
        player.TabItems[0] =
        [
            new QolItemRecord { Id = 40, BaseName = "Iron Ring", ItemClass = "Ring", Rarity = QolItemRarity.Rare },
        ];
        player.TabItems[3] =
        [
            new QolItemRecord { Id = 50, MetadataPath = "Metadata/Items/Maps/MapKeyStrand", BaseName = "Strand Map", MapTier = 1, ItemLevel = 70, MapUpgradeTargetMetadata = "Metadata/Items/Maps/MapKeyDunes", MapUpgradeTargetName = "Dunes Map" },
            new QolItemRecord { Id = 51, MetadataPath = "Metadata/Items/Maps/MapKeyStrand", BaseName = "Strand Map", MapTier = 1, ItemLevel = 69, MapUpgradeTargetMetadata = "Metadata/Items/Maps/MapKeyDunes", MapUpgradeTargetName = "Dunes Map" },
            new QolItemRecord { Id = 52, MetadataPath = "Metadata/Items/Maps/MapKeyStrand", BaseName = "Strand Map", MapTier = 1, ItemLevel = 71, MapUpgradeTargetMetadata = "Metadata/Items/Maps/MapKeyDunes", MapUpgradeTargetName = "Dunes Map" },
        ];
        player.TabItems[5] =
        [
            new QolItemRecord { Id = 60, BaseName = "Clear Oil", StackSize = 9, MaxStackSize = 5000 },
        ];
        player.PassiveTree = new QolPassiveTreeState(
            ClassStartNode: 1,
            AllocatedNodes: new HashSet<ushort>(),
            Adjacency: new Dictionary<ushort, IReadOnlyList<ushort>>
            {
                [1] = [10],
                [10] = [1, 20],
                [20] = [10],
            },
            UnspentPoints: 1);

        // --- Feature 3: Inventory Sort toolbar button ---
        ClickSlotButton(invSlots, StashQolProtocol.SlotSortInventoryId);
        Assert.Equal((byte)0, player.MainInventory.Single(i => i.Id == 2).X);
        Assert.Contains(clientHost.Toasts.ActiveToasts, t => t.Title == "Inventory Sort");

        // --- Feature 7: Ctrl+Shift+RMB suppressible mouse chord on Stacked Deck ---
        simBridge.SimulatedHoveredItem = new HoveredItemInfo(
            EntityHandle: 0x1001,
            ControlHandle: 0x2001,
            ItemId: 3,
            InventoryId: 1,
            GridX: 0,
            GridY: 0,
            Width: 1,
            Height: 1,
            MetadataPath: StashQolProtocol.StackedDeckMetadata,
            BaseName: "Stacked Deck",
            StackSize: 4,
            MaxStackSize: 20,
            Rarity: ItemRarity.Normal,
            IsIdentified: true,
            IsCorrupted: false);

        // While text input is focused, Ctrl+Shift+RMB MUST pass through unsuppressed!
        simBridge.SimulatedTextInputFocused = true;
        bool suppressedWhileTyping = simBridge.SimulateMouseEvent(MouseButton.Right, InputEventKind.Down, 100, 100, KeyModifiers.Control | KeyModifiers.Shift);
        Assert.False(suppressedWhileTyping);
        Assert.Equal((ushort)4, player.MainInventory.Single(i => i.Id == 3).StackSize);

        // When not typing, Ctrl+Shift+RMB suppresses PoE RMB and bulk-opens Stacked Decks!
        simBridge.SimulatedTextInputFocused = false;
        bool suppressed = simBridge.SimulateMouseEvent(MouseButton.Right, InputEventKind.Down, 100, 100, KeyModifiers.Control | KeyModifiers.Shift);
        Assert.True(suppressed);
        Assert.DoesNotContain(player.MainInventory, i => i.Id == 3);
        Assert.Contains(clientHost.Toasts.ActiveToasts, t => t.Title == "Bulk Stack Use" && t.Message.Contains("Opened 4 Stacked Deck", StringComparison.Ordinal));

        // --- Feature 6: Regex Roller UI dialog ---
        simBridge.SimulatedHoveredItem = new HoveredItemInfo(
            EntityHandle: 0x1002,
            ControlHandle: 0x2002,
            ItemId: 2,
            InventoryId: 1,
            GridX: 0,
            GridY: 0,
            Width: 2,
            Height: 3,
            MetadataPath: "Metadata/Items/Armours/BodyArmours/BodyStr1",
            BaseName: "Astral Plate",
            StackSize: 1,
            MaxStackSize: 1,
            Rarity: ItemRarity.Rare,
            IsIdentified: true,
            IsCorrupted: false);
        ClickSlotButton(invSlots, StashQolProtocol.SlotRegexRollerId);
        IUiWindow? regexWin = clientHost.Ui.GetWindow(StashQolProtocol.RegexRollerWindowId);
        Assert.NotNull(regexWin);
        Assert.Contains("Astral Plate", regexWin!.Require<ILabel>("target_preview_label").Text);

        // Invalid regex rejected locally with error toast without spending orbs.
        regexWin.Require<ITextBox>("pattern_textbox").Text = "[bad(";
        regexWin.Require<IButton>("roll_button").Click();
        Assert.Equal(25, player.CurrencyPool[StashQolProtocol.ChaosOrbMetadata]);
        Assert.Contains(clientHost.Toasts.ActiveToasts, t => t.Severity == ToastSeverity.Error && t.Message.Contains("Invalid regex", StringComparison.Ordinal));

        // Valid regex rolls until match and updates dialog status + toast.
        regexWin.Require<ITextBox>("pattern_textbox").Text = @"\+60 to maximum Life";
        regexWin.Require<ITextBox>("max_rolls_textbox").Text = "20";
        regexWin.Require<IButton>("roll_button").Click();
        Assert.Equal(21, player.CurrencyPool[StashQolProtocol.ChaosOrbMetadata]); // 20 + 4*10 = 60 -> 4 rolls!
        Assert.Contains("Matched regex", regexWin.Require<ILabel>("status_label").Text);
        regexWin.Require<IButton>("close_button").Click();

        // --- Feature 1: Deposit All toolbar button ---
        ClickSlotButton(invSlots, StashQolProtocol.SlotDepositAllId);
        Assert.Empty(player.MainInventory);
        Assert.Contains(backend.GetStashTabItems(connId, 1), i => i.Id == 1); // Chaos -> Currency affinity tab
        Assert.Contains(backend.GetStashTabItems(connId, 0), i => i.Id == 2); // Plate -> Dump fallback tab

        // --- Feature 2: Vendor All 2-step confirmation + tab switch reset ---
        clientHost.Toasts.Clear();
        ClickSlotButton(stashSlots, StashQolProtocol.SlotVendorAllId);
        Assert.Contains(clientHost.Toasts.ActiveToasts, t => t.Title == "Confirm Vendor All");
        Assert.Contains(backend.GetStashTabItems(connId, 0), i => i.Id == 40); // Not sold yet!

        // Switching tabs resets pending confirmation; switching back requires 2 clicks again.
        simBridge.SimulatedActiveStashTab = new StashTabInfo(Index: 1, Id: 101u, Type: StashTabType.Currency, Name: "Currency");
        simBridge.SimulateFrame(0.016f);
        simBridge.SimulatedActiveStashTab = new StashTabInfo(Index: 0, Id: 100u, Type: StashTabType.Normal, Name: "Dump");
        simBridge.SimulateFrame(0.016f);
        ClickSlotButton(stashSlots, StashQolProtocol.SlotVendorAllId); // First click after switch -> re-arms confirm
        Assert.Contains(backend.GetStashTabItems(connId, 0), i => i.Id == 40);
        ClickSlotButton(stashSlots, StashQolProtocol.SlotVendorAllId); // Second click -> commits sale!
        Assert.Empty(backend.GetStashTabItems(connId, 0));
        Assert.Contains(clientHost.Toasts.ActiveToasts, t => t.Title == "Vendor All" && t.Severity == ToastSeverity.Success);

        // --- Feature 4: Map Tier 3->1 Upgrade button ---
        clientHost.Toasts.Clear();
        simBridge.SimulatedActiveStashTab = new StashTabInfo(Index: 2, Id: 102u, Type: StashTabType.Map, Name: "Maps", MapTier: 1);
        simBridge.SimulateFrame(0.016f);
        ClickSlotButton(mapSlots, StashQolProtocol.SlotUpgradeMapsId);
        Assert.Single(backend.GetStashTabItems(connId, 4)); // T2 Dunes Map created!
        Assert.Contains(clientHost.Toasts.ActiveToasts, t => t.Title == "Map Upgrade 3->1");

        // --- Feature 5: Blight Oil Upgrade Max button ---
        simBridge.SimulatedActiveStashTab = new StashTabInfo(Index: 5, Id: 105u, Type: StashTabType.Blight, Name: "Blight");
        simBridge.SimulateFrame(0.016f);
        ClickSlotButton(blightSlots, StashQolProtocol.SlotUpgradeOilsId);
        Assert.Contains(backend.GetStashTabItems(connId, 5), i => i.BaseName == "Amber Oil" && i.StackSize == 1);
        Assert.Contains(clientHost.Toasts.ActiveToasts, t => t.Title == "Blight Oils");

        // --- Feature 7b: Ctrl+Shift+RMB targeted orb with active CursorItemInfo ---
        player.MainInventory.Add(new QolItemRecord
        {
            Id = 70,
            MetadataPath = "Metadata/Items/Armours/BodyArmours/BodyStr1",
            BaseName = "Vaal Regalia",
            Sockets = 6,
            MaxSockets = 6,
            MaxLinkGroup = 4,
        });
        player.TabItems[1] =
        [
            new QolItemRecord
            {
                Id = 71,
                MetadataPath = "Metadata/Items/Currency/CurrencyRerollSocketLinks",
                BaseName = "Orb of Fusing",
                StackSize = 10,
                MaxStackSize = 20,
            },
        ];
        simBridge.SimulatedHoveredItem = new HoveredItemInfo(0x1003, 0x2003, 70, 1, 0, 0, 2, 3, "Metadata/Items/Armours/BodyArmours/BodyStr1", "Vaal Regalia", 1, 1, ItemRarity.Rare, true, false);
        simBridge.SimulatedCursorItem = new CursorItemInfo(CursorMode.UsingItem, 0x1004, 71, 0x801, 0, 0, 1, 1, "Metadata/Items/Currency/CurrencyRerollSocketLinks", "Orb of Fusing", 10, 20, ItemRarity.Normal, true, false);
        Assert.True(simBridge.SimulateMouseEvent(MouseButton.Right, InputEventKind.Down, 120, 120, KeyModifiers.Control | KeyModifiers.Shift));
        Assert.Equal((byte)6, player.MainInventory.Single(i => i.Id == 70).MaxLinkGroup);
        Assert.Equal((ushort)8, player.TabItems[1].Single(i => i.Id == 71).StackSize);
        simBridge.SimulatedCursorItem = null;

        // --- Feature 8: PoB Import UI + Clipboard Paste + Auto-Apply on LevelUp + HUD Toast ---
        clientHost.Toasts.Clear();
        ClickSlotButton(invSlots, StashQolProtocol.SlotPobImportId);
        IUiWindow? pobWin = clientHost.Ui.GetWindow(StashQolProtocol.PobImportWindowId);
        Assert.NotNull(pobWin);
        simBridge.SetClipboardText("10, 20");
        pobWin!.Require<IButton>("paste_clipboard_button").Click();
        Assert.Equal("10, 20", pobWin.Require<ITextBox>("pob_code_textbox").Text);
        pobWin.Require<IButton>("import_button").Click();
        Assert.Contains((ushort)10, player.PassiveTree!.AllocatedNodes);
        Assert.DoesNotContain((ushort)20, player.PassiveTree.AllocatedNodes);

        // Level up grants +1 passive point -> server auto-allocates node 20 & sends LevelUp HUD toast!
        player.PassiveTree = player.PassiveTree with { UnspentPoints = 1 };
        await serverMod.OnLevelUpAsync(connId, previousLevel: 1, newLevel: 2);
        Assert.Contains((ushort)20, player.PassiveTree.AllocatedNodes);
        Assert.Contains(clientHost.Toasts.ActiveToasts, t => t.Title == "Level 2 Auto-Passives" && t.Severity == ToastSeverity.Success);
        pobWin.Require<IButton>("close_button").Click();
    }

    private static void ClickSlotButton(IReadOnlyList<ISlotAttachment> attachments, string id)
    {
        ISlotAttachment attachment = attachments.Single(a => a.Id == id);
        Assert.True(attachment.IsAttached, $"Slot attachment '{id}' is not mounted.");
        IUiWindow window = Assert.IsAssignableFrom<IUiWindow>(attachment.Control);
        window.Require<IButton>("btn").Click();
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
