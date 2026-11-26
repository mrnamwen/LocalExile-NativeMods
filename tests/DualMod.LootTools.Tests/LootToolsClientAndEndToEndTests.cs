using System.Runtime.CompilerServices;
using DualMod.LootTools.Server;
using DualMod.LootTools.Shared;
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

namespace DualMod.LootTools.Tests;

public sealed class LootToolsClientAndEndToEndTests
{
    [Theory]
    [InlineData(NetTransportKind.HostRuleset)]
    [InlineData(NetTransportKind.ChatFallback)]
    public async Task FullDualSideFlow_FilterAwareMagnet_Merge_AoePickup_Keybinds_FilterReload_AreaLifecycle_CameraZoom_CleanAlcUnload(
        NetTransportKind transport)
    {
        string workspace = Path.Combine(Path.GetTempPath(), "nm-loottools-e2e-" + Guid.NewGuid().ToString("N"));
        string localExileRoot = Path.Combine(workspace, "LocalExile");
        string stagingPkg = Path.Combine(workspace, "loottools-pkg");
        string shadowRoot = Path.Combine(workspace, "shadow");
        string keybindsPath = Path.Combine(workspace, "keybinds.json");

        try
        {
            // 1. Seed fake LocalExile checkout with stock files that must remain untouched.
            string stockClientDir = Path.Combine(localExileRoot, "Client");
            Directory.CreateDirectory(Path.Combine(stockClientDir, "Bundles2"));
            byte[] stockExe = "STOCK_POE_3_29_3D_EXE"u8.ToArray();
            File.WriteAllBytes(Path.Combine(stockClientDir, "Game.exe"), stockExe);

            // 2. Stage DualMod.LootTools with compiled Server + Client DLLs and validate package directory.
            string repoRoot = LootToolsManifestAndUiTests.FindRepoRoot();
            string exampleRoot = Path.Combine(repoRoot, "examples", "DualMod.LootTools");
            CopyDirectory(exampleRoot, stagingPkg);
            File.Copy(FindCompiledAssembly(exampleRoot, "Client", "loottools.Client.dll"), Path.Combine(stagingPkg, "loottools.Client.dll"), overwrite: true);
            File.Copy(FindCompiledAssembly(exampleRoot, "Server", "loottools.dll"), Path.Combine(stagingPkg, "loottools.dll"), overwrite: true);

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

            await RunActiveLootToolsSessionAsync(simBridge, clientHost, deployedClientDir, transport);

            // 4. Verify collectible ALC clean unload + stock camera zoom restored (-1.0f) + zero lingering loottools slots/windows/keybinds + clean uninstall.
            var unloadResult = await clientHost.Loader.UnloadModAsync(LootToolsProtocol.ModId);
            Assert.True(unloadResult.WasLoaded);
            Assert.True(unloadResult.UnloadedCleanly);
            Assert.Equal(-1.0f, simBridge.SimulatedZoomOverride);
            Assert.DoesNotContain(
                clientHost.Screens.GetSlotAttachments(ScreenSlot.HudTopRight),
                a => a.Id.StartsWith("loottools.", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                clientHost.Input.Keybinds.Entries,
                k => k.Id.StartsWith("loottools.", StringComparison.OrdinalIgnoreCase));
            Assert.Null(clientHost.Ui.GetWindow(LootToolsProtocol.PanelWindowId));

            var uninstall = installer.Uninstall(LootToolsProtocol.ModId, localExileRoot);
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
    private static async Task RunActiveLootToolsSessionAsync(
        SimulatedNativeBridge simBridge,
        ClientHostRuntime clientHost,
        string deployedClientDir,
        NetTransportKind transport)
    {
        // Seed initial Phase 2 state (InGame HUD, active filter, valid camera, local player at grid 100,100).
        simBridge.SimulatedInGameUiHandle = 0x5555;
        simBridge.SimulatedActivePanels = GamePanel.Hud;
        simBridge.SimulatedItemFilterState = new ItemFilterState(
            FilterName: "NeverSink_Strict.filter",
            FilterFolder: @"C:\Users\Exile\Documents\My Games\Path of Exile",
            FilterPath: @"C:\Users\Exile\Documents\My Games\Path of Exile\NeverSink_Strict.filter",
            IsLoaded: true,
            HideFilteredGroundItems: true,
            AltHighlightActive: false);
        simBridge.SimulatedCameraState = CameraState.Invalid with
        {
            IsValid = true,
            Zoom = 1.0f,
            ZoomOverride = -1.0f,
            ZoomTarget = 1.0f,
            ViewportWidth = 1920f,
            ViewportHeight = 1080f,
        };
        simBridge.SetSimulatedLocalPlayer(MakeEntity(1u, WorldEntityKind.Player | WorldEntityKind.LocalPlayer | WorldEntityKind.Alive, 100, 100));

        var loadedMod = clientHost.Loader.LoadModFromDirectory(deployedClientDir);
        Assert.Equal(LootToolsProtocol.ModId, loadedMod.Manifest.Id);
        Assert.False(loadedMod.IsDisabled);
        simBridge.SimulateFrame(0.016f);

        // Verify all 5 HudTopRight buttons mounted with non-overlapping vertical stack rects.
        IReadOnlyList<ISlotAttachment> hudSlots = clientHost.Screens.GetSlotAttachments(ScreenSlot.HudTopRight);
        Assert.Equal(5, hudSlots.Count);
        Assert.All(hudSlots, s => Assert.True(s.IsAttached));
        for (int i = 1; i < hudSlots.Count; i++)
        {
            Assert.True(hudSlots[i].ComputedRect.Y >= hudSlots[i - 1].ComputedRect.Y + hudSlots[i - 1].ComputedRect.Height);
        }

        // Wire loopback ClientBridgeHost + LootToolsServerMod.
        const string connId = "exile-loottools-e2e";
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

        var backend = new InMemoryLootToolsBackend();
        using var serverMod = new LootToolsServerMod(serverBridge, backend);
        serverMod.Attach();

        clientHost.SendHandshake();
        IClientSession session = serverBridge.For(connId);
        Assert.True(session.HasNativeHost);
        Assert.True(session.HasClientMod(LootToolsProtocol.ModId));

        // Seed matching server ground items + client world entities (visible + filter-hidden + client-only).
        var player = backend.GetOrCreatePlayer(connId);
        player.Session = new LootSessionState(PlayerX: 100, PlayerY: 100);
        player.NextGroundEntityId = 0x100100u;

        AddDualGroundItem(simBridge, player, 0x100001u, 122, 100, 0x1111u, "Chaos Orb", stackSize: 6, maxStack: 20, isFilterVisible: true);
        AddDualGroundItem(simBridge, player, 0x100002u, 124, 100, 0x1111u, "Chaos Orb", stackSize: 8, maxStack: 20, isFilterVisible: true);
        AddDualGroundItem(simBridge, player, 0x100003u, 118, 100, 0x2222u, "Scroll Fragment (Hidden)", stackSize: 1, maxStack: 40, isFilterVisible: false);
        AddDualGroundItem(simBridge, player, 0x100004u, 115, 100, 0x9999u, "Gold", stackSize: 420, maxStack: 1, isFilterVisible: true, isGold: true);
        AddDualGroundItem(simBridge, player, 0x100005u, 116, 100, 0x3333u, "Diamond Ring", stackSize: 1, maxStack: 1, isFilterVisible: true);
        // Client-only object must NEVER be sent to server.
        simBridge.AddSimulatedEntity(MakeEntity(0x80000001u, WorldEntityKind.WorldItem | WorldEntityKind.ClientOnly, 105, 100, isFilterVisible: true));

        // --- 1. !IsTextInputFocused guard: Alt+M while typing MUST NOT trigger Loot Magnet ---
        simBridge.SimulatedTextInputFocused = true;
        bool handledWhileTyping = simBridge.SimulateKeyEvent('M', KeyModifiers.Alt, isDown: true);
        Assert.False(handledWhileTyping);
        Assert.Empty(player.SentWorldRemovals);

        // --- 2. Filter-Aware Loot Magnet via Alt+M keybind ---
        simBridge.SimulatedTextInputFocused = false;
        bool handledMagnet = simBridge.SimulateKeyEvent('M', KeyModifiers.Alt, isDown: true);
        Assert.True(handledMagnet);

        // Only the 4 filter-visible server items were pulled; filter-hidden 0x100003 stayed at (118, 100)!
        Assert.Equal(4, player.SentWorldRemovals.Count);
        Assert.Contains(0x100001u, player.SentWorldRemovals);
        Assert.Contains(0x100002u, player.SentWorldRemovals);
        Assert.Contains(0x100004u, player.SentWorldRemovals);
        Assert.Contains(0x100005u, player.SentWorldRemovals);
        Assert.DoesNotContain(0x100003u, player.SentWorldRemovals);
        Assert.DoesNotContain(0x80000001u, player.SentWorldRemovals);
        Assert.Equal((118, 100), (player.Ground[0x100003u].GridX, player.Ground[0x100003u].GridY));
        Assert.Equal(0, player.TeleportPacketsSent);
        Assert.Contains(clientHost.Toasts.ActiveToasts, t => t.Title == "Loot Magnet" && t.Severity == ToastSeverity.Success);

        // Sync simulated client world entities to reflect the relocated server ground IDs.
        SyncClientWorldItemsFromServer(simBridge, player, hiddenIds: [0x100003u]);

        // --- 3. Ground Loot Merger via Alt+R keybind ---
        clientHost.Toasts.Clear();
        Assert.True(simBridge.SimulateKeyEvent('R', KeyModifiers.Alt, isDown: true));
        // The two Chaos Orb stacks (6 + 8) collapsed into one 14-stack pile!
        LootGroundItem mergedChaos = player.Ground.Values.Single(g => g.TypeHash == 0x1111u);
        Assert.Equal((ushort)14, mergedChaos.StackSize);
        Assert.Contains(clientHost.Toasts.ActiveToasts, t => t.Title == "Loot Merger" && t.Severity == ToastSeverity.Success);

        // Sync client entities to merged IDs, then trigger AoE Pickup via Alt+G!
        SyncClientWorldItemsFromServer(simBridge, player, hiddenIds: [0x100003u]);

        // --- 4. Filter-Aware AoE Pickup via Alt+G keybind ---
        clientHost.Toasts.Clear();
        Assert.True(simBridge.SimulateKeyEvent('G', KeyModifiers.Alt, isDown: true));
        Assert.Equal(420, player.GoldStash);
        Assert.Equal(2, player.MainInventory.Count);
        Assert.Equal((ushort)14, player.MainInventory.Single(i => i.SourceItem.TypeHash == 0x1111u).StackSize);
        Assert.Contains(player.MainInventory, i => i.SourceItem.TypeHash == 0x3333u);
        // Filter-hidden item 0x100003 was NEVER picked up!
        Assert.True(player.Ground.ContainsKey(0x100003u));
        Assert.Contains(clientHost.Toasts.ActiveToasts, t => t.Title == "AoE Pickup" && t.Message.Contains("+420 gold", StringComparison.Ordinal));

        // --- 5. Open LootToolsPanel.ui + Item Filter Reload + AreaEntered/AreaLeft + Camera Zoom Override ---
        ClickSlotButton(hudSlots, LootToolsProtocol.SlotPanelId);
        IUiWindow? panel = clientHost.Ui.GetWindow(LootToolsProtocol.PanelWindowId);
        Assert.NotNull(panel);
        Assert.Contains("NeverSink_Strict.filter", panel!.Require<ILabel>("filter_name_label").Text);

        // Trigger filter reload via panel button + simulate native reload completion.
        clientHost.Toasts.Clear();
        panel.Require<IButton>("reload_filter_button").Click();
        Assert.Equal(1, simBridge.SimulatedFilterReloadCount);
        simBridge.SimulatedItemFilterState = simBridge.SimulatedItemFilterState with { FilterName = "NeverSink_UberStrict.filter" };
        simBridge.SimulateItemFilterReloaded(true, "Loaded NeverSink_UberStrict.filter.");
        Assert.Contains("NeverSink_UberStrict.filter", panel.Require<ILabel>("filter_name_label").Text);
        Assert.Contains(clientHost.Toasts.ActiveToasts, t => t.Title == "Item Filter" && t.Severity == ToastSeverity.Success);

        // Simulate AreaEntered and AreaLeft lifecycle transitions.
        simBridge.SimulateAreaEntered(new AreaInfo("MapWorldsStrand", "Strand Map", Act: 11, AreaLevel: 83, EffectiveLevel: 83, Hash16: 0x1234, IsTown: false, IsHideout: false, IsGuildHideout: false, ActiveRulesetIds: []));
        Assert.Contains("Strand Map", panel.Require<ILabel>("area_status_label").Text);

        // Enable Auto-Merge checkbox in panel and drop 2 partial stacks -> throttled frame tick auto-merges them!
        AddDualGroundItem(simBridge, player, 0x100201u, 102, 100, 0x4444u, "Orb of Alchemy", stackSize: 3, maxStack: 20, isFilterVisible: true);
        AddDualGroundItem(simBridge, player, 0x100202u, 103, 100, 0x4444u, "Orb of Alchemy", stackSize: 4, maxStack: 20, isFilterVisible: true);
        panel.Require<ICheckBox>("auto_merge_checkbox").IsChecked = true;
        simBridge.SimulateFrame(0.5f); // Below 1.0s interval -> not yet merged
        Assert.Equal(2, player.Ground.Values.Count(g => g.TypeHash == 0x4444u));
        simBridge.SimulateFrame(0.6f); // Crosses 1.0s interval -> auto-merge triggers!
        LootGroundItem mergedAlch = Assert.Single(player.Ground.Values, g => g.TypeHash == 0x4444u);
        Assert.Equal((ushort)7, mergedAlch.StackSize);

        // Leave area resets cooldowns and clears area label.
        simBridge.SimulateAreaLeft("MapWorldsStrand");
        Assert.Equal("Area: None", panel.Require<ILabel>("area_status_label").Text);

        // Enable Camera Zoom Override in panel -> applies 1.65f to ICameraService (restored to -1.0f on unload!).
        panel.Require<ITextBox>("zoom_value_textbox").Text = "1.65";
        panel.Require<ICheckBox>("zoom_override_checkbox").IsChecked = true;
        panel.Require<IButton>("apply_settings_button").Click();
        Assert.Equal(1.65f, simBridge.SimulatedZoomOverride);

        await Task.CompletedTask;
    }

    private static void AddDualGroundItem(
        SimulatedNativeBridge simBridge,
        InMemoryLootToolsBackend.PlayerWorldState player,
        uint entityId,
        int gridX,
        int gridY,
        uint typeHash,
        string baseName,
        ushort stackSize,
        ushort maxStack,
        bool isFilterVisible,
        bool isGold = false)
    {
        player.Ground[entityId] = new LootGroundItem
        {
            EntityId = entityId,
            GridX = gridX,
            GridY = gridY,
            TypeHash = typeHash,
            BaseName = baseName,
            StackSize = stackSize,
            MaxStack = maxStack,
            IsGold = isGold,
            Shared = true,
            LockstepAdmitted = true,
        };
        simBridge.AddSimulatedEntity(MakeEntity(entityId, WorldEntityKind.WorldItem, gridX, gridY, isFilterVisible));
    }

    private static void SyncClientWorldItemsFromServer(
        SimulatedNativeBridge simBridge,
        InMemoryLootToolsBackend.PlayerWorldState player,
        HashSet<uint> hiddenIds)
    {
        WorldEntitySnapshot localPlayer = simBridge.GetLocalPlayerEntity()!;
        simBridge.ClearSimulatedEntities();
        simBridge.SetSimulatedLocalPlayer(localPlayer);
        foreach (LootGroundItem g in player.Ground.Values)
        {
            bool visible = !hiddenIds.Contains(g.EntityId);
            simBridge.AddSimulatedEntity(MakeEntity(g.EntityId, WorldEntityKind.WorldItem, g.GridX, g.GridY, visible));
        }
    }

    private static WorldEntitySnapshot MakeEntity(uint entityId, WorldEntityKind kind, int gridX, int gridY, bool isFilterVisible = false) =>
        new(
            EntityId: entityId,
            EntityHandle: unchecked((nint)(0x6000_0000L + entityId)),
            Kind: kind,
            MetadataPath: (kind & WorldEntityKind.WorldItem) != 0 ? "Metadata/MiscellaneousObjects/WorldItem" : "Metadata/Characters/Dex/Dex",
            GridX: gridX,
            GridY: gridY,
            Position: new WorldPosition3D(
                gridX * LootToolsProtocol.WorldUnitsPerGridCell,
                gridY * LootToolsProtocol.WorldUnitsPerGridCell,
                0f),
            IsFilterVisible: isFilterVisible);

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
