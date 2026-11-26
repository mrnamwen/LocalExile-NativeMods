using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
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

public sealed class ServerBridge030Tests
{
    private const string ConnId = "player-tier1-conn";

    private sealed class Tier1Harness : IDisposable
    {
        public string TempDir { get; }
        public SimulatedNativeBridge SimBridge { get; }
        public ClientHostRuntime ClientHost { get; }
        public ClientBridgeHost ServerBridge { get; }
        public IClientSession Session { get; }
        public SimulatedNativeBridge.SimulatedNativeUiControl Hud { get; }
        public SimulatedNativeBridge.SimulatedNativeUiControl Inventory { get; }
        public SimulatedNativeBridge.SimulatedNativeUiControl Stash { get; }
        public SimulatedNativeBridge.SimulatedNativeUiControl MapPage { get; }
        public SimulatedNativeBridge.SimulatedNativeUiControl BlightPage { get; }
        public SimulatedNativeBridge.SimulatedNativeUiControl PassiveTree { get; }

        public Tier1Harness(NetTransportKind transport = NetTransportKind.HostRuleset, bool connectHandshake = true)
        {
            TempDir = Path.Combine(Path.GetTempPath(), $"nativemods-sb030-{Guid.NewGuid():N}");
            Directory.CreateDirectory(TempDir);

            SimBridge = new SimulatedNativeBridge(ClientBuildId.Build3293d)
            {
                EnableSimulatedNativeUi = true,
            };

            ClientHost = new ClientHostRuntime(SimBridge, new ClientHostOptions
            {
                TransportKind = transport,
                HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId,
                KeybindsPath = Path.Combine(TempDir, "keybinds.json"),
            });
            ClientHost.Start();

            Hud = SimBridge.RegisterSimulatedStockControl("InGameUI_1");
            Inventory = SimBridge.RegisterSimulatedStockControl("InventoryPanel_1", parentHandle: Hud.Handle);
            Stash = SimBridge.RegisterSimulatedStockControl("StashPanel_1", parentHandle: Hud.Handle);
            MapPage = SimBridge.RegisterSimulatedStockControl("MapStashPage_1", parentHandle: Stash.Handle);
            BlightPage = SimBridge.RegisterSimulatedStockControl("BlightStashPage_1", parentHandle: Stash.Handle);
            PassiveTree = SimBridge.RegisterSimulatedStockControl("PassiveShellPC_1", parentHandle: Hud.Handle);

            SimBridge.SimulatedPreGameScreen = PreGameScreen.InGame;
            SimBridge.SimulatedInGameUiHandle = Hud.Handle;
            SimBridge.SimulatedActivePanels = GamePanel.Hud | GamePanel.Inventory | GamePanel.Stash | GamePanel.MapStash | GamePanel.BlightStash | GamePanel.PassiveTree;
            SimBridge.SetSimulatedPanelHandle(GamePanel.Hud, Hud.Handle);
            SimBridge.SetSimulatedPanelHandle(GamePanel.Inventory, Inventory.Handle);
            SimBridge.SetSimulatedPanelHandle(GamePanel.Stash, Stash.Handle);
            SimBridge.SetSimulatedPanelHandle(GamePanel.MapStash, MapPage.Handle);
            SimBridge.SetSimulatedPanelHandle(GamePanel.BlightStash, BlightPage.Handle);
            SimBridge.SetSimulatedPanelHandle(GamePanel.PassiveTree, PassiveTree.Handle);
            ClientHost.TickFrame();

            var sink = new LoopbackGatewaySink(SimBridge);
            ServerBridge = new ClientBridgeHost(sink, new ClientBridgeOptions
            {
                DefaultTransport = transport,
                HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId,
            });

            SimBridge.OnOutgoingRulesetPacket = (rulesetId, innerPayload) =>
            {
                byte[] body = MessageCodec.EncodeRulesetBody(rulesetId, innerPayload.Span);
                ServerBridge.HandleIncomingPacketAsync(ConnId, IOpcodeRegistry.ClientRulesetOpcode, body).GetAwaiter().GetResult();
            };
            SimBridge.OnOutgoingChatLine = line =>
            {
                ServerBridge.HandleIncomingChatCommandAsync(ConnId, line).GetAwaiter().GetResult();
            };

            if (connectHandshake)
            {
                ClientHost.SendHandshake();
            }

            Session = ServerBridge.For(ConnId);
        }

        public void Dispose()
        {
            ServerBridge.Dispose();
            ClientHost.Dispose();
            SimBridge.Dispose();
            try
            {
                if (Directory.Exists(TempDir))
                {
                    Directory.Delete(TempDir, recursive: true);
                }
            }
            catch
            {
                // Best-effort temp dir cleanup.
            }
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

    // -------------------------------------------------------------------------
    // 1. Managed ScreenSlot Toolbar Buttons + Click Routing + Removal
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(NetTransportKind.HostRuleset)]
    [InlineData(NetTransportKind.ChatFallback)]
    public async Task Tier1_SlotButtons_RegisterClickUpdateAndRemove_AcrossAllManagedSlots(NetTransportKind transport)
    {
        using var harness = new Tier1Harness(transport);

        Assert.True(harness.Session.HasNativeHost);
        Assert.Contains("tier1.slots", harness.ClientHost.LastServerHandshake!.ServerCapabilities);
        Assert.Contains("tier1.toasts", harness.ClientHost.LastServerHandshake.ServerCapabilities);
        Assert.Contains("tier1.hotkeys", harness.ClientHost.LastServerHandshake.ServerCapabilities);
        Assert.Contains("tier1.queries", harness.ClientHost.LastServerHandshake.ServerCapabilities);

        int depositClicks = 0;
        int vendorClicks = 0;
        int mapClicks = 0;
        int treeClicks = 0;
        int hudClicks = 0;

        // Register via session directly (with separate OnSlotButtonClickAsync)
        ServerSlotButtonHandle depositBtn = await harness.Session.RegisterSlotButtonAsync(
            ScreenSlot.InventoryToolbar,
            "deposit_all",
            "Deposit All",
            new SlotAttachmentOptions(Order: 10, Width: 112f, Height: 34f, Spacing: 6f));
        using IDisposable depositClickSub = await harness.Session.OnSlotButtonClickAsync(
            ScreenSlot.InventoryToolbar,
            "deposit_all",
            async (_, _) =>
            {
                await Task.Yield();
                Interlocked.Increment(ref depositClicks);
            });

        // Register via session.Ui with inline async handler
        ServerSlotButtonHandle vendorBtn = await harness.Session.Ui.RegisterSlotButtonAsync(
            ScreenSlot.StashHeader,
            "vendor_all",
            "Vendor All",
            async _ =>
            {
                await Task.Yield();
                Interlocked.Increment(ref vendorClicks);
            },
            new SlotAttachmentOptions(Order: 20));

        // Additional managed ScreenSlots
        ServerSlotButtonHandle mapBtn = await harness.Session.RegisterSlotButtonAsync(
            ScreenSlot.MapTabBar,
            "roll_maps",
            "Roll Maps",
            _ => Interlocked.Increment(ref mapClicks));
        ServerSlotButtonHandle treeBtn = await harness.Session.RegisterSlotButtonAsync(
            ScreenSlot.PassiveTreeHeader,
            "respec_all",
            "Respec",
            _ => Interlocked.Increment(ref treeClicks));
        ServerSlotButtonHandle hudBtn = await harness.Session.RegisterSlotButtonAsync(
            ScreenSlot.HudTopRight,
            "quick_menu",
            "Menu",
            _ => Interlocked.Increment(ref hudClicks));
        ServerSlotButtonHandle footerBtn = await harness.Session.RegisterSlotButtonAsync(
            ScreenSlot.StashFooter,
            "sort_tab",
            "Sort");
        ServerSlotButtonHandle blightBtn = await harness.Session.RegisterSlotButtonAsync(
            ScreenSlot.BlightTabBar,
            "anoint_all",
            "Anoint");

        harness.ClientHost.TickFrame();

        Assert.Equal(7, harness.ClientHost.Tier1Handler.RegisteredSlotButtonCount);

        ISlotAttachment invAttach = Assert.Single(harness.ClientHost.Screens.GetSlotAttachments(ScreenSlot.InventoryToolbar));
        ISlotAttachment stashAttach = Assert.Single(harness.ClientHost.Screens.GetSlotAttachments(ScreenSlot.StashHeader));
        ISlotAttachment footerAttach = Assert.Single(harness.ClientHost.Screens.GetSlotAttachments(ScreenSlot.StashFooter));
        ISlotAttachment mapAttach = Assert.Single(harness.ClientHost.Screens.GetSlotAttachments(ScreenSlot.MapTabBar));
        ISlotAttachment blightAttach = Assert.Single(harness.ClientHost.Screens.GetSlotAttachments(ScreenSlot.BlightTabBar));
        ISlotAttachment treeAttach = Assert.Single(harness.ClientHost.Screens.GetSlotAttachments(ScreenSlot.PassiveTreeHeader));
        ISlotAttachment hudAttach = Assert.Single(harness.ClientHost.Screens.GetSlotAttachments(ScreenSlot.HudTopRight));

        Assert.True(invAttach.IsAttached);
        Assert.Equal("Deposit All", invAttach.Control!.Require<IButton>("btn").Text);
        Assert.True(stashAttach.IsAttached);
        Assert.True(footerAttach.IsAttached);
        Assert.True(mapAttach.IsAttached);
        Assert.True(blightAttach.IsAttached);
        Assert.True(treeAttach.IsAttached);
        Assert.True(hudAttach.IsAttached);

        // Simulate user clicks on the native client buttons -> verify callbacks route back to server
        invAttach.Control!.Require<IButton>("btn").Click();
        stashAttach.Control!.Require<IButton>("btn").Click();
        mapAttach.Control!.Require<IButton>("btn").Click();
        treeAttach.Control!.Require<IButton>("btn").Click();
        hudAttach.Control!.Require<IButton>("btn").Click();

        await WaitUntilAsync(() => depositClicks == 1 && vendorClicks == 1);
        Assert.Equal(1, depositClicks);
        Assert.Equal(1, vendorClicks);
        Assert.Equal(1, mapClicks);
        Assert.Equal(1, treeClicks);
        Assert.Equal(1, hudClicks);

        // Re-registering same (Slot, Id) replaces caption cleanly without throwing
        await harness.Session.RegisterSlotButtonAsync(ScreenSlot.InventoryToolbar, "deposit_all", "Deposit (Updated)");
        harness.ClientHost.TickFrame();
        ISlotAttachment updatedInv = Assert.Single(harness.ClientHost.Screens.GetSlotAttachments(ScreenSlot.InventoryToolbar));
        Assert.Equal("Deposit (Updated)", updatedInv.Control!.Require<IButton>("btn").Text);
        Assert.Equal(7, harness.ClientHost.Tier1Handler.RegisteredSlotButtonCount);

        // Remove via RemoveSlotButtonAsync and via handle.DisposeAsync()
        await harness.Session.RemoveSlotButtonAsync(ScreenSlot.InventoryToolbar, "deposit_all");
        await vendorBtn.DisposeAsync();
        await mapBtn.RemoveAsync();
        await treeBtn.DisposeAsync();
        await hudBtn.DisposeAsync();
        await footerBtn.DisposeAsync();
        await blightBtn.DisposeAsync();

        Assert.Equal(0, harness.ClientHost.Tier1Handler.RegisteredSlotButtonCount);
        Assert.Empty(harness.ClientHost.Screens.GetSlotAttachments(ScreenSlot.InventoryToolbar));
        Assert.Empty(harness.ClientHost.Screens.GetSlotAttachments(ScreenSlot.StashHeader));
        Assert.False(depositBtn.IsRegistered);
        Assert.False(vendorBtn.IsRegistered);

        // LeagueCardModes is rejected for toolbar buttons
        await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Session.RegisterSlotButtonAsync(ScreenSlot.LeagueCardModes, "bad", "Nope"));
    }

    // -------------------------------------------------------------------------
    // 2. HUD Toast Delivery Round-Trip
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(NetTransportKind.HostRuleset)]
    [InlineData(NetTransportKind.ChatFallback)]
    public async Task Tier1_ShowToast_DeliversToClientToastService_WithSeverityAndDuration(NetTransportKind transport)
    {
        using var harness = new Tier1Harness(transport);

        await harness.Session.ShowToastAsync("Stash QoL", "Deposited 14 items", ToastSeverity.Success, durationMs: 2500);
        await harness.Session.Ui.ShowToastAsync("Warning", "Tab is full", ToastSeverity.Warning, durationMs: 5000);
        await harness.ServerBridge.ShowToastAsync(ConnId, "Error", "Insufficient gold", ToastSeverity.Error, durationMs: 1500);

        IReadOnlyList<ToastMessage> active = harness.ClientHost.Toasts.ActiveToasts;
        Assert.Equal(3, active.Count);

        Assert.Equal("Stash QoL", active[0].Title);
        Assert.Equal("Deposited 14 items", active[0].Message);
        Assert.Equal(ToastSeverity.Success, active[0].Severity);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), active[0].Duration);
        Assert.Equal("server-bridge", active[0].OwnerModId);

        Assert.Equal("Warning", active[1].Title);
        Assert.Equal(ToastSeverity.Warning, active[1].Severity);

        Assert.Equal("Error", active[2].Title);
        Assert.Equal(ToastSeverity.Error, active[2].Severity);

        // Frame ticks auto-expire shorter toasts cleanly
        harness.ClientHost.TickFrame(2.0f);
        Assert.Equal(2, harness.ClientHost.Toasts.ActiveToasts.Count);
        Assert.DoesNotContain(harness.ClientHost.Toasts.ActiveToasts, t => t.Title == "Error");
    }

    // -------------------------------------------------------------------------
    // 3. Centralized Hotkeys + Suppressible Mouse Chords (Ctrl+Shift+RMB)
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(NetTransportKind.HostRuleset)]
    [InlineData(NetTransportKind.ChatFallback)]
    public async Task Tier1_Hotkeys_RegisterSuppressMouseChordRebindTextFocusAndUnregister(NetTransportKind transport)
    {
        using var harness = new Tier1Harness(transport);

        List<ClientHotkeyTriggeredEvent> bulkEvents = [];
        List<ClientHotkeyTriggeredEvent> keyEvents = [];

        // Register suppressible mouse chord Ctrl+Shift+RMB via canonical string
        ServerHotkeyHandle bulkHandle = await harness.Session.RegisterHotkeyAsync(
            "stashqol.bulk_use",
            "Bulk Currency Use",
            "Ctrl+Shift+RMB",
            allowWhenTextInputFocused: false,
            suppressGameInput: true);
        using IDisposable bulkSub = await harness.Session.OnHotkeyAsync(
            "stashqol.bulk_use",
            async (_, evt, _) =>
            {
                await Task.Yield();
                lock (bulkEvents) { bulkEvents.Add(evt); }
            });

        // Register keyboard hotkey Ctrl+F6 with inline handler and allowWhenTextInputFocused = true
        ServerHotkeyHandle keyHandle = await harness.Session.RegisterHotkeyAsync(
            "mod.quick_dump",
            "Quick Dump",
            KeyChord.FromKey(0x75, KeyModifiers.Control), // Ctrl+F6
            async (_, evt, _) =>
            {
                await Task.Yield();
                lock (keyEvents) { keyEvents.Add(evt); }
            },
            allowWhenTextInputFocused: true,
            suppressGameInput: false);

        Assert.Equal(2, harness.ClientHost.Tier1Handler.RegisteredHotkeyCount);
        Assert.Equal(2, harness.ClientHost.Keybinds.Entries.Count);

        // Plain RMB must NOT be suppressed and must NOT trigger bulk_use
        Assert.False(harness.SimBridge.SimulateMouseEvent(MouseButton.Right, InputEventKind.Down, 320, 240, KeyModifiers.None));
        Assert.False(harness.SimBridge.SimulateMouseEvent(MouseButton.Right, InputEventKind.Up, 320, 240, KeyModifiers.None));
        Assert.Empty(bulkEvents);

        // Ctrl+Shift+RMB MUST trigger bulk_use AND suppress BOTH Down and matching Up!
        bool downSuppressed = harness.SimBridge.SimulateMouseEvent(
            MouseButton.Right,
            InputEventKind.Down,
            412,
            288,
            KeyModifiers.Control | KeyModifiers.Shift);
        bool upSuppressed = harness.SimBridge.SimulateMouseEvent(
            MouseButton.Right,
            InputEventKind.Up,
            412,
            288,
            KeyModifiers.Control | KeyModifiers.Shift);

        Assert.True(downSuppressed);
        Assert.True(upSuppressed);
        await WaitUntilAsync(() => { lock (bulkEvents) { return bulkEvents.Count == 1; } });

        ClientHotkeyTriggeredEvent fired = bulkEvents[0];
        Assert.Equal("stashqol.bulk_use", fired.Id);
        Assert.Equal("Ctrl+Shift+RMB", fired.ActiveChord);
        Assert.False(fired.IsTextInputFocused);
        Assert.Equal(412, fired.MouseX);
        Assert.Equal(288, fired.MouseY);

        // While text input is focused, allowWhenTextInputFocused=false skips bulk_use,
        // while allowWhenTextInputFocused=true still fires quick_dump.
        harness.SimBridge.SimulatedTextInputFocused = true;
        Assert.False(harness.SimBridge.SimulateMouseEvent(
            MouseButton.Right,
            InputEventKind.Down,
            100,
            100,
            KeyModifiers.Control | KeyModifiers.Shift));
        Assert.Single(bulkEvents);

        harness.SimBridge.SimulateKeyEvent(0x75, KeyModifiers.Control, isDown: true);
        await WaitUntilAsync(() => { lock (keyEvents) { return keyEvents.Count == 1; } });
        Assert.Equal("Ctrl+F6", keyEvents[0].ActiveChord);
        Assert.True(keyEvents[0].IsTextInputFocused);

        // Centralized rebind on client updates the active chord automatically
        harness.SimBridge.SimulatedTextInputFocused = false;
        Assert.True(harness.ClientHost.Keybinds.Rebind("stashqol.bulk_use", KeyChord.FromMouse(MouseButton.Middle, KeyModifiers.Alt), out _));
        Assert.True(harness.SimBridge.SimulateMouseEvent(MouseButton.Middle, InputEventKind.Down, 50, 60, KeyModifiers.Alt));
        Assert.True(harness.SimBridge.SimulateMouseEvent(MouseButton.Middle, InputEventKind.Up, 50, 60, KeyModifiers.Alt));
        await WaitUntilAsync(() => { lock (bulkEvents) { return bulkEvents.Count == 2; } });
        Assert.Equal("Alt+MMB", bulkEvents[1].ActiveChord);

        // Unregister removes keybinds and stops suppression/triggers
        await harness.Session.UnregisterHotkeyAsync("stashqol.bulk_use");
        await keyHandle.DisposeAsync();

        Assert.Equal(0, harness.ClientHost.Tier1Handler.RegisteredHotkeyCount);
        Assert.Empty(harness.ClientHost.Keybinds.Entries);
        Assert.False(bulkHandle.IsRegistered);
        Assert.False(keyHandle.IsRegistered);
        Assert.False(harness.SimBridge.SimulateMouseEvent(MouseButton.Middle, InputEventKind.Down, 50, 60, KeyModifiers.Alt));
        Assert.Equal(2, bulkEvents.Count);
    }

    // -------------------------------------------------------------------------
    // 4. Screen / Stash Tab / Map Tier & Hover / Cursor Queries + Opt-In Events
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(NetTransportKind.HostRuleset)]
    [InlineData(NetTransportKind.ChatFallback)]
    public async Task Tier1_ScreenAndHoverQueries_AndOptInChangeNotifications_WorkEndToEnd(NetTransportKind transport)
    {
        using var harness = new Tier1Harness(transport);

        // Opt-in subscriptions must start disabled (zero push traffic until subscribed)
        Assert.False(harness.ClientHost.Tier1Handler.IsScreenSubscriptionActive);
        Assert.False(harness.ClientHost.Tier1Handler.IsHoverSubscriptionActive);

        // Set up active map stash tab + hovered item + cursor item on client
        harness.SimBridge.SimulatedActiveStashTab = new StashTabInfo(
            Index: 4,
            Id: 42001u,
            Type: StashTabType.Map,
            Name: "Maps",
            MapTier: 16,
            IsGuild: false,
            IsRemoveOnly: false,
            Color: 0x336699u);
        harness.ClientHost.TickFrame();

        var hoveredControl = harness.SimBridge.RegisterSimulatedStockControl("stash_grid_cell_3_4", parentHandle: harness.Stash.Handle);
        harness.SimBridge.SimulatedHoveredControlHandle = hoveredControl.Handle;
        harness.SimBridge.SimulatedHoveredItem = new HoveredItemInfo(
            EntityHandle: 0x11223344,
            ControlHandle: hoveredControl.Handle,
            ItemId: 7788u,
            InventoryId: 12u,
            GridX: 3,
            GridY: 4,
            Width: 1,
            Height: 1,
            MetadataPath: "Metadata/Items/Maps/MapKeyTier16",
            BaseName: "Strand Map",
            StackSize: 1u,
            MaxStackSize: 1u,
            Rarity: ItemRarity.Rare,
            IsIdentified: true,
            IsCorrupted: false,
            ItemLevel: 83,
            ItemClassId: 32u);
        harness.SimBridge.SimulatedCursorItem = new CursorItemInfo(
            Mode: CursorMode.UsingItem,
            EntityHandle: 0x55667788,
            ItemId: 9900u,
            SourceInventoryId: 1u,
            SourceGridX: 0,
            SourceGridY: 0,
            Width: 1,
            Height: 1,
            MetadataPath: "Metadata/Items/Currency/CurrencyRerollRare",
            BaseName: "Chaos Orb",
            StackSize: 19u,
            MaxStackSize: 20u,
            Rarity: ItemRarity.Normal,
            IsIdentified: true,
            IsCorrupted: false,
            ShiftKeep: true);

        // Query screen state (no tick needed after last state update)
        ClientScreenStateSnapshot screen = await harness.Session.QueryScreenStateAsync();
        Assert.True(screen.IsInGame);
        Assert.True(screen.IsPanelOpen(GamePanel.Stash));
        Assert.True(screen.IsPanelOpen(GamePanel.MapStash));
        Assert.NotNull(screen.ActiveStashTab);
        Assert.Equal(4, screen.ActiveStashTab!.Index);
        Assert.Equal(42001u, screen.ActiveStashTab.Id);
        Assert.Equal(StashTabType.Map, screen.ActiveStashTab.Type);
        Assert.Equal("Maps", screen.ActiveStashTab.Name);
        Assert.Equal(16, screen.ActiveMapTier);

        // Query hover state with refresh=true (even BEFORE TickFrame polled HoverService!)
        ClientHoverStateSnapshot hover = await harness.Session.QueryHoverStateAsync(refresh: true);
        Assert.Equal("stash_grid_cell_3_4", hover.HoveredControlId);
        Assert.Equal(unchecked((ulong)hoveredControl.Handle), hover.HoveredControlHandle);
        Assert.NotNull(hover.HoveredItem);
        Assert.Equal(7788u, hover.HoveredItem!.ItemId);
        Assert.Equal(12u, hover.HoveredItem.InventoryId);
        Assert.Equal("Strand Map", hover.HoveredItem.BaseName);
        Assert.Equal(ItemRarity.Rare, hover.HoveredItem.Rarity);
        Assert.Equal(83, hover.HoveredItem.ItemLevel);
        Assert.Equal(0x11223344ul, hover.HoveredItem.EntityHandle);
        Assert.NotNull(hover.CursorItem);
        Assert.Equal(CursorMode.UsingItem, hover.CursorItem!.Mode);
        Assert.Equal("Chaos Orb", hover.CursorItem.BaseName);
        Assert.True(hover.CursorItem.ShiftKeep);
        Assert.Equal(19u, hover.CursorItem.StackSize);

        // Opt-in screen change notifications
        List<ClientScreenChangedEvent> screenEvents = [];
        IDisposable screenSub = await harness.Session.OnScreenChangedAsync((_, evt) => screenEvents.Add(evt));
        Assert.True(harness.ClientHost.Tier1Handler.IsScreenSubscriptionActive);

        // Change map tier -> pushes StashTabChanged
        harness.SimBridge.SimulatedActiveStashTab = harness.SimBridge.SimulatedActiveStashTab with { MapTier = 17 };
        harness.ClientHost.TickFrame();
        Assert.Contains(screenEvents, e => e.StashTabChanged && e.State.ActiveMapTier == 17);

        // Close PassiveTree panel -> pushes PanelIsOpen == false
        harness.SimBridge.SimulatedActivePanels &= ~GamePanel.PassiveTree;
        harness.ClientHost.TickFrame();
        Assert.Contains(screenEvents, e => e.ChangedPanel == GamePanel.PassiveTree && e.PanelIsOpen == false);

        // Dispose last subscriber -> client automatically unsubscribes!
        screenSub.Dispose();
        Assert.False(harness.ClientHost.Tier1Handler.IsScreenSubscriptionActive);

        int countAfterUnsub = screenEvents.Count;
        harness.SimBridge.SimulatedActiveStashTab = harness.SimBridge.SimulatedActiveStashTab with { MapTier = 15 };
        harness.ClientHost.TickFrame();
        Assert.Equal(countAfterUnsub, screenEvents.Count);

        // Opt-in hover change notifications
        List<ClientHoverStateSnapshot> hoverEvents = [];
        IDisposable hoverSub = await harness.Session.OnHoverChangedAsync((_, snap) => hoverEvents.Add(snap));
        Assert.True(harness.ClientHost.Tier1Handler.IsHoverSubscriptionActive);

        harness.SimBridge.SimulatedHoveredItem = harness.SimBridge.SimulatedHoveredItem with { BaseName = "Crimson Temple Map", ItemId = 8899u };
        harness.ClientHost.TickFrame();
        Assert.Single(hoverEvents);
        Assert.Equal("Crimson Temple Map", hoverEvents[0].HoveredItem!.BaseName);
        Assert.Equal(8899u, hoverEvents[0].HoveredItem!.ItemId);

        hoverSub.Dispose();
        Assert.False(harness.ClientHost.Tier1Handler.IsHoverSubscriptionActive);

        harness.SimBridge.SimulatedHoveredItem = null;
        harness.ClientHost.TickFrame();
        Assert.Single(hoverEvents);
    }

    // -------------------------------------------------------------------------
    // 5. Disconnect & Unload Resource Teardown + Query Timeout / Cancellation
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Tier1_DisconnectAndUnload_TearsDownAllClientAndServerResourcesCleanly()
    {
        using var harness = new Tier1Harness(NetTransportKind.HostRuleset);

        ServerSlotButtonHandle btn = await harness.Session.RegisterSlotButtonAsync(ScreenSlot.InventoryToolbar, "btn1", "Deposit");
        ServerHotkeyHandle hk = await harness.Session.RegisterHotkeyAsync("hk1", "Bulk", "Ctrl+Shift+RMB", suppressGameInput: true);
        await harness.Session.ShowToastAsync("Notice", "Connected", ToastSeverity.Info, 5000);
        using IDisposable scrSub = await harness.Session.OnScreenChangedAsync((_, _) => { });
        using IDisposable hovSub = await harness.Session.OnHoverChangedAsync((_, _) => { });
        harness.ClientHost.TickFrame();

        Assert.Equal(1, harness.ClientHost.Tier1Handler.RegisteredSlotButtonCount);
        Assert.Equal(1, harness.ClientHost.Tier1Handler.RegisteredHotkeyCount);
        Assert.True(harness.ClientHost.Tier1Handler.IsScreenSubscriptionActive);
        Assert.True(harness.ClientHost.Tier1Handler.IsHoverSubscriptionActive);
        Assert.Single(harness.ClientHost.Screens.GetSlotAttachments(ScreenSlot.InventoryToolbar));
        Assert.Single(harness.ClientHost.Keybinds.Entries);
        Assert.Single(harness.ClientHost.Toasts.ActiveToasts);

        // Simulate client disconnect -> Tier1Handler cleans up every single client-side resource!
        harness.ClientHost.NotifyDisconnected("logout");

        Assert.Equal(0, harness.ClientHost.Tier1Handler.RegisteredSlotButtonCount);
        Assert.Equal(0, harness.ClientHost.Tier1Handler.RegisteredHotkeyCount);
        Assert.False(harness.ClientHost.Tier1Handler.IsScreenSubscriptionActive);
        Assert.False(harness.ClientHost.Tier1Handler.IsHoverSubscriptionActive);
        Assert.Empty(harness.ClientHost.Screens.GetSlotAttachments(ScreenSlot.InventoryToolbar));
        Assert.Empty(harness.ClientHost.Keybinds.Entries);
        Assert.Empty(harness.ClientHost.Toasts.ActiveToasts);

        // Reconnect on the same ClientHostRuntime still accepts new Tier 1 commands cleanly
        await harness.Session.RegisterSlotButtonAsync(ScreenSlot.StashHeader, "btn2", "Reconnected");
        Assert.Equal(1, harness.ClientHost.Tier1Handler.RegisteredSlotButtonCount);

        // Server-side session removal cleans up handles & rejects subsequent queries
        Assert.True(await harness.ServerBridge.RemoveSessionAsync(ConnId));
        Assert.False(btn.IsRegistered);
        Assert.False(hk.IsRegistered);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Session.QueryScreenStateAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Session.QueryHoverStateAsync());
    }

    [Fact]
    public async Task Tier1_QueryTimeout_AndSessionCloseCancelInFlightQuery()
    {
        var sink = new BlackHoleGatewaySink();
        using var bridge = new ClientBridgeHost(sink);
        IClientSession session = bridge.For("no-reply");

        await Assert.ThrowsAsync<TimeoutException>(() =>
            session.QueryScreenStateAsync(timeout: TimeSpan.FromMilliseconds(40)));
        await Assert.ThrowsAsync<TimeoutException>(() =>
            session.QueryHoverStateAsync(refresh: true, timeout: TimeSpan.FromMilliseconds(40)));

        Task<ClientScreenStateSnapshot> inFlight = session.QueryScreenStateAsync(timeout: TimeSpan.FromSeconds(10));
        Assert.True(await bridge.RemoveSessionAsync("no-reply"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inFlight);
    }

    [Fact]
    public async Task Tier1_MultiModGatewayBus_CoexistsForSlotButtonsHotkeysAndRefCountedSubscriptions()
    {
        string domainKey = $"Tier1MultiModBus.{Guid.NewGuid():N}";
        using var harness = new Tier1Harness(NetTransportKind.HostRuleset, connectHandshake: false);
        var sinkB = new LoopbackGatewaySink(harness.SimBridge);
        using var bridgeB = new ClientBridgeHost(sinkB, new ClientBridgeOptions
        {
            DefaultTransport = NetTransportKind.HostRuleset,
            HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId,
        });

        using GatewayBridgeRegistration regA = harness.ServerBridge.AttachToGatewayBus("mod.alpha", domainKey: domainKey);
        using GatewayBridgeRegistration regB = bridgeB.AttachToGatewayBus("mod.beta", domainKey: domainKey);

        harness.ClientHost.SendHandshake();
        IClientSession sessionA = harness.ServerBridge.For(ConnId);
        IClientSession sessionB = bridgeB.For(ConnId);

        int alphaScreenEvents = 0;
        int betaScreenEvents = 0;
        int betaHotkeyCount = 0;

        IDisposable subA = await sessionA.OnScreenChangedAsync((_, _) => alphaScreenEvents++);
        IDisposable subB = await sessionB.OnScreenChangedAsync((_, _) => betaScreenEvents++);
        Assert.True(harness.ClientHost.Tier1Handler.IsScreenSubscriptionActive);

        // String-chord inline hotkey registration from Mod B
        await using ServerHotkeyHandle hkB = await sessionB.RegisterHotkeyAsync(
            "beta.chord",
            "Beta Chord",
            "Alt+Shift+RMB",
            (_, _, _) =>
            {
                betaHotkeyCount++;
                return Task.CompletedTask;
            },
            suppressGameInput: true);

        // Unsubscribing Mod B leaves Mod A's ref-counted subscription active!
        subB.Dispose();
        Assert.True(harness.ClientHost.Tier1Handler.IsScreenSubscriptionActive);

        harness.SimBridge.SimulatedActiveStashTab = new StashTabInfo(1, 555u, StashTabType.Currency, "Currency");
        harness.ClientHost.TickFrame();
        Assert.Equal(1, alphaScreenEvents);
        Assert.Equal(0, betaScreenEvents);

        Assert.True(harness.SimBridge.SimulateMouseEvent(MouseButton.Right, InputEventKind.Down, 10, 20, KeyModifiers.Alt | KeyModifiers.Shift));
        Assert.True(harness.SimBridge.SimulateMouseEvent(MouseButton.Right, InputEventKind.Up, 10, 20, KeyModifiers.Alt | KeyModifiers.Shift));
        Assert.Equal(1, betaHotkeyCount);

        // Unsubscribing Mod A brings ref-count to 0 -> detaches client events cleanly
        subA.Dispose();
        Assert.False(harness.ClientHost.Tier1Handler.IsScreenSubscriptionActive);

        GatewayBridgeBus.Clear(domainKey);
    }

    // -------------------------------------------------------------------------
    // 6. LocalExileGatewayHost 0.3.0 Part 2a Wrappers (CharacterCreated & VendorQuote)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task LocalExileGatewayHost_Part2a_OnCharacterCreatedAndQuoteVendor_WorkViaAdapterAndReflection()
    {
        // (a) Verify via custom ILocalExileGatewayAdapter (verifying 0.2.0 FakeGatewayAdapter default interface compat too)
        var adapter = new Part2aFakeGatewayAdapter();
        var modCtx = new FakeModContext("skip.campaign");

        GatewayCharacterCreatedContext? captured = null;
        using (LocalExileGatewayHost host = LocalExileGatewayHost.Create(modCtx, b => b
            .WithAdapter(adapter)
            .WithBus(attach: false)
            .OnCharacterCreated(ctx =>
            {
                captured = ctx;
                if (ctx.TryGetOption("skip_campaign", out string? val) && val == "1")
                {
                    ctx.StartingAreaId = "2_11_endgame_town";
                    ctx.StartingLevel = 68;
                    ctx.SuppressStartKit = true;
                }

                return Task.CompletedTask;
            })))
        {
            var createdCtx = new GatewayCharacterCreatedContext(
                account: "nam",
                characterId: Guid.NewGuid(),
                name: "ZoomRanger",
                className: "Ranger",
                classId: 2,
                league: "Standard",
                hardcore: false,
                ssf: true,
                leagueForm: "SSF Standard",
                creationOptions: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["skip_campaign"] = "1",
                });

            await adapter.RaiseCharacterCreatedAsync(createdCtx);
            Assert.NotNull(captured);
            Assert.Equal("ZoomRanger", captured!.Name);
            Assert.Equal("2_11_endgame_town", createdCtx.StartingAreaId);
            Assert.Equal(68, createdCtx.StartingLevel);
            Assert.True(createdCtx.SuppressStartKit);

            GatewayVendorQuoteResult? quote = host.QuoteVendor(new object(), new object[] { "ItemA", "ItemB" });
            Assert.NotNull(quote);
            Assert.Equal(2, quote!.Rewards.Count);
            Assert.Contains("ChaosRecipe", quote.Recipes);
        }

        Assert.Equal(0, adapter.CharacterCreatedSubscriberCount);

        // (b) Verify ReflectionGatewayAdapter against a dynamically emitted Gateway-shaped assembly
        //     (proves zero compile-time dependency on GameServer.Gateway AND verifies mutable write-back!).
        Assembly dynGateway = EmitPart2aGatewayAssembly();
        Type gameEventsType = dynGateway.GetType("GameEvents", throwOnError: true)!;
        Type charCreatedCtxType = dynGateway.GetType("CharacterCreatedContext", throwOnError: true)!;
        Type invItemType = dynGateway.GetType("InventoryItem", throwOnError: true)!;
        Type playerType = dynGateway.GetType("Player", throwOnError: true)!;

        using LocalExileGatewayHost reflHost = LocalExileGatewayHost.Create(modCtx, b => b
            .WithGatewayAssembly(dynGateway)
            .WithBus(attach: false)
            .UseCoreClientChannel(false)
            .OnCharacterCreated(async ctx =>
            {
                await Task.Yield();
                if (ctx.TryGetOption("skip_campaign", out string? flag) && flag == "true")
                {
                    ctx.StartingAreaId = "2_11_endgame_town";
                    ctx.StartingLevel = 70;
                    ctx.SuppressStartKit = true;
                }
            }));

        object liveCharCtx = Activator.CreateInstance(
            charCreatedCtxType,
            "nam",
            Guid.NewGuid(),
            "SkipWitch",
            3,
            "Standard",
            false,
            false,
            "Standard",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["skip_campaign"] = "true" })!;

        MethodInfo runCharCreated = gameEventsType.GetMethod("RunCharacterCreated", BindingFlags.Public | BindingFlags.Static)!;
        await (Task)runCharCreated.Invoke(null, [liveCharCtx])!;

        Assert.Equal("2_11_endgame_town", charCreatedCtxType.GetProperty("StartingAreaId")!.GetValue(liveCharCtx));
        Assert.Equal(70, charCreatedCtxType.GetProperty("StartingLevel")!.GetValue(liveCharCtx));
        Assert.Equal(true, charCreatedCtxType.GetProperty("SuppressStartKit")!.GetValue(liveCharCtx));

        object livePlayer = Activator.CreateInstance(playerType)!;
        object liveItem = Activator.CreateInstance(invItemType)!;
        GatewayVendorQuoteResult? reflQuote = reflHost.QuoteVendor(livePlayer, new object[] { liveItem });
        Assert.NotNull(reflQuote);
        Assert.Single(reflQuote!.Rewards);
        Assert.Equal(["ScrollFragment"], reflQuote.Recipes);
        Assert.Null(reflQuote.Error);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                return;
            }

            await Task.Delay(5);
        }
    }

    private sealed class BlackHoleGatewaySink : IGatewayPacketSink
    {
        public Task SendPacketAsync(string connectionId, ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SendChatReplyAsync(string connectionId, string text, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeModContext(string id)
    {
        public FakeManifest Manifest { get; } = new(id);
        public List<string> Logs { get; } = [];
        public void Log(string msg) => Logs.Add(msg);
    }

    private sealed record FakeManifest(string Id);

    private sealed class Part2aFakeGatewayAdapter : ILocalExileGatewayAdapter
    {
        private readonly List<Func<GatewayCharacterCreatedContext, Task>> _charCreated = [];

        public int CharacterCreatedSubscriberCount => _charCreated.Count;
        public bool SupportsCoreClientChannel => false;
        public string? TryGetOwnerId(object modContext) => (modContext as FakeModContext)?.Manifest.Id;
        public Action<string>? TryGetLogger(object modContext) => _ => { };
        public string GetConnectionId(object player) => "fake#1";
        public string GetCharacterName(object player) => "FakeChar";
        public bool IsPlayerClosed(object player) => false;
        public object? TryGetClientChannel(object player) => null;
        public Task SendPacketAsync(object player, ushort opcode, ReadOnlyMemory<byte> payload, string? log, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendChatReplyAsync(object player, string text, CancellationToken cancellationToken) => Task.CompletedTask;
        public IDisposable SubscribeRuleset(string ownerId, byte rulesetId, Func<object, string, ReadOnlyMemory<byte>, Task<bool>> onInnerPayload) => new Noop();
        public IDisposable SubscribeClientCommand(string ownerId, Func<object, string, string, Task<bool>> onCommand) => new Noop();
        public IDisposable SubscribePacket(string ownerId, ushort opcode, Func<object, byte[], Task<bool>> onPacket) => new Noop();
        public IDisposable SubscribeCommand(string ownerId, string commandName, string usage, Func<object, string[], Task> onCommand) => new Noop();
        public IDisposable SubscribePlayerEntered(string ownerId, Func<object, Task> onEntered) => new Noop();
        public IDisposable SubscribePlayerLeaving(string ownerId, Func<object, string, Task> onLeaving) => new Noop();
        public IDisposable? SubscribeClientReady(string ownerId, Func<object, Task> onReady) => new Noop();
        public IDisposable? SubscribeClientMessage(string ownerId, Func<GatewayClientMessageContext, Task<bool>> onMessage) => new Noop();
        public IDisposable? SubscribeCosmeticChanged(string ownerId, Func<GatewayCosmeticChangeContext, Task> onCosmeticChanged) => new Noop();

        public IDisposable? SubscribeCharacterCreated(string ownerId, Func<GatewayCharacterCreatedContext, Task> onCharacterCreated)
        {
            _charCreated.Add(onCharacterCreated);
            return new CallbackDisposable(() => _charCreated.Remove(onCharacterCreated));
        }

        public GatewayVendorQuoteResult? TryQuoteVendor(object player, IEnumerable offeredItems) =>
            new(["ChaosOrb#1", "ChaosOrb#2"], ["ChaosRecipe"]);

        public async Task RaiseCharacterCreatedAsync(GatewayCharacterCreatedContext ctx)
        {
            foreach (Func<GatewayCharacterCreatedContext, Task> cb in _charCreated.ToArray())
            {
                await cb(ctx);
            }
        }

        private sealed class Noop : IDisposable { public void Dispose() { } }
        private sealed class CallbackDisposable(Action onDispose) : IDisposable { public void Dispose() => onDispose(); }
    }

    // Builds an isolated in-memory Gateway-shaped assembly exposing global::Program, global::GameEvents
    // (Packet, Command, PlayerEntered, PlayerLeaving, CharacterCreated, RunCharacterCreated, QuoteVendor),
    // global::CharacterCreatedContext, global::VendorQuoteResult, global::Player, global::InventoryItem.
    private static AssemblyBuilder EmitPart2aGatewayAssembly()
    {
        var asmName = new AssemblyName($"FakeGatewayPart2a_{Guid.NewGuid():N}");
        AssemblyBuilder ab = AssemblyBuilder.DefineDynamicAssembly(asmName, AssemblyBuilderAccess.Run);
        ModuleBuilder mb = ab.DefineDynamicModule("MainModule");

        // public sealed class Player {}
        TypeBuilder playerTb = mb.DefineType("Player", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class);
        Type playerType = playerTb.CreateType();

        // public sealed class InventoryItem {}
        TypeBuilder invItemTb = mb.DefineType("InventoryItem", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class);
        Type invItemType = invItemTb.CreateType();

        // public sealed class CharacterCreatedContext { ... }
        TypeBuilder ctxTb = mb.DefineType("CharacterCreatedContext", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class);
        FieldBuilder fAccount = DefineAutoProp(ctxTb, "Account", typeof(string), setter: false);
        FieldBuilder fCharId = DefineAutoProp(ctxTb, "CharacterId", typeof(Guid), setter: false);
        FieldBuilder fName = DefineAutoProp(ctxTb, "Name", typeof(string), setter: false);
        FieldBuilder fClass = DefineAutoProp(ctxTb, "Class", typeof(int), setter: false);
        FieldBuilder fLeague = DefineAutoProp(ctxTb, "League", typeof(string), setter: false);
        FieldBuilder fHc = DefineAutoProp(ctxTb, "Hardcore", typeof(bool), setter: false);
        FieldBuilder fSsf = DefineAutoProp(ctxTb, "Ssf", typeof(bool), setter: false);
        FieldBuilder fForm = DefineAutoProp(ctxTb, "LeagueForm", typeof(string), setter: false);
        FieldBuilder fOpts = DefineAutoProp(ctxTb, "CreationOptions", typeof(IReadOnlyDictionary<string, string>), setter: false);
        DefineAutoProp(ctxTb, "StartingAreaId", typeof(string), setter: true);
        DefineAutoProp(ctxTb, "StartingLevel", typeof(int?), setter: true);
        DefineAutoProp(ctxTb, "SuppressStartKit", typeof(bool), setter: true);

        ConstructorBuilder ctxCtor = ctxTb.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [typeof(string), typeof(Guid), typeof(string), typeof(int), typeof(string), typeof(bool), typeof(bool), typeof(string), typeof(IReadOnlyDictionary<string, string>)]);
        ILGenerator cil = ctxCtor.GetILGenerator();
        cil.Emit(OpCodes.Ldarg_0);
        cil.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        FieldBuilder[] ctorFields = [fAccount, fCharId, fName, fClass, fLeague, fHc, fSsf, fForm, fOpts];
        for (int i = 0; i < ctorFields.Length; i++)
        {
            cil.Emit(OpCodes.Ldarg_0);
            cil.Emit(OpCodes.Ldarg, i + 1);
            cil.Emit(OpCodes.Stfld, ctorFields[i]);
        }
        cil.Emit(OpCodes.Ret);
        Type ctxType = ctxTb.CreateType();

        // public sealed class VendorQuoteResult { Rewards, Recipes, Error }
        TypeBuilder vqTb = mb.DefineType("VendorQuoteResult", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class);
        FieldBuilder fRewards = DefineAutoProp(vqTb, "Rewards", typeof(IReadOnlyList<object>), setter: false);
        FieldBuilder fRecipes = DefineAutoProp(vqTb, "Recipes", typeof(IReadOnlyList<string>), setter: false);
        FieldBuilder fError = DefineAutoProp(vqTb, "Error", typeof(string), setter: false);
        ConstructorBuilder vqCtor = vqTb.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [typeof(IReadOnlyList<object>), typeof(IReadOnlyList<string>), typeof(string)]);
        ILGenerator vqIl = vqCtor.GetILGenerator();
        vqIl.Emit(OpCodes.Ldarg_0);
        vqIl.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        vqIl.Emit(OpCodes.Ldarg_0); vqIl.Emit(OpCodes.Ldarg_1); vqIl.Emit(OpCodes.Stfld, fRewards);
        vqIl.Emit(OpCodes.Ldarg_0); vqIl.Emit(OpCodes.Ldarg_2); vqIl.Emit(OpCodes.Stfld, fRecipes);
        vqIl.Emit(OpCodes.Ldarg_0); vqIl.Emit(OpCodes.Ldarg_3); vqIl.Emit(OpCodes.Stfld, fError);
        vqIl.Emit(OpCodes.Ret);
        Type vqType = vqTb.CreateType();

        // public sealed class Program {}
        TypeBuilder progTb = mb.DefineType("Program", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class);
        progTb.CreateType();

        // public sealed class GameEvent<T> : SimpleGameEvent<T> (via helper generic class defined in this test assembly!)
        Type pktEvtType = typeof(SimpleGameEvent<>).MakeGenericType(typeof(Func<,,>).MakeGenericType(playerType, typeof(byte[]), typeof(Task<bool>)));
        Type enteredEvtType = typeof(SimpleGameEvent<>).MakeGenericType(typeof(Func<,>).MakeGenericType(playerType, typeof(Task)));
        Type leavingEvtType = typeof(SimpleGameEvent<>).MakeGenericType(typeof(Func<,,>).MakeGenericType(playerType, typeof(string), typeof(Task)));
        Type charCreatedDelType = typeof(Func<,>).MakeGenericType(ctxType, typeof(Task));
        Type charCreatedEvtType = typeof(SimpleGameEvent<>).MakeGenericType(charCreatedDelType);

        // public static class GameEvents
        TypeBuilder geTb = mb.DefineType("GameEvents", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        FieldBuilder fEntered = geTb.DefineField("PlayerEntered", enteredEvtType, FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.InitOnly);
        FieldBuilder fLeaving = geTb.DefineField("PlayerLeaving", leavingEvtType, FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.InitOnly);
        FieldBuilder fCharCreated = geTb.DefineField("CharacterCreated", charCreatedEvtType, FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.InitOnly);
        FieldBuilder fPkt = geTb.DefineField("_pkt", pktEvtType, FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly);

        ConstructorBuilder geCctor = geTb.DefineTypeInitializer();
        ILGenerator cctorIl = geCctor.GetILGenerator();
        cctorIl.Emit(OpCodes.Newobj, enteredEvtType.GetConstructor(Type.EmptyTypes)!);
        cctorIl.Emit(OpCodes.Stsfld, fEntered);
        cctorIl.Emit(OpCodes.Newobj, leavingEvtType.GetConstructor(Type.EmptyTypes)!);
        cctorIl.Emit(OpCodes.Stsfld, fLeaving);
        cctorIl.Emit(OpCodes.Newobj, charCreatedEvtType.GetConstructor(Type.EmptyTypes)!);
        cctorIl.Emit(OpCodes.Stsfld, fCharCreated);
        cctorIl.Emit(OpCodes.Newobj, pktEvtType.GetConstructor(Type.EmptyTypes)!);
        cctorIl.Emit(OpCodes.Stsfld, fPkt);
        cctorIl.Emit(OpCodes.Ret);

        // public static object Packet(ushort opcode) => _pkt;
        MethodBuilder pktM = geTb.DefineMethod("Packet", MethodAttributes.Public | MethodAttributes.Static, pktEvtType, [typeof(ushort)]);
        ILGenerator pktIl = pktM.GetILGenerator();
        pktIl.Emit(OpCodes.Ldsfld, fPkt);
        pktIl.Emit(OpCodes.Ret);

        // public static IDisposable Command(string owner, string name, string usage, Func<Player, string[], Task> run)
        Type cmdDelType = typeof(Func<,,>).MakeGenericType(playerType, typeof(string[]), typeof(Task));
        MethodBuilder cmdM = geTb.DefineMethod("Command", MethodAttributes.Public | MethodAttributes.Static, typeof(IDisposable), [typeof(string), typeof(string), typeof(string), cmdDelType]);
        ILGenerator cmdIl = cmdM.GetILGenerator();
        cmdIl.Emit(OpCodes.Newobj, typeof(SimpleNoopDisposable).GetConstructor(Type.EmptyTypes)!);
        cmdIl.Emit(OpCodes.Ret);

        // public static Task RunCharacterCreated(CharacterCreatedContext ctx) => CharacterCreated.InvokeFirst(ctx);
        MethodBuilder runM = geTb.DefineMethod("RunCharacterCreated", MethodAttributes.Public | MethodAttributes.Static, typeof(Task), [ctxType]);
        ILGenerator runIl = runM.GetILGenerator();
        runIl.Emit(OpCodes.Ldsfld, fCharCreated);
        runIl.Emit(OpCodes.Ldarg_0);
        runIl.Emit(OpCodes.Call, typeof(SimpleGameEventRunner).GetMethod(nameof(SimpleGameEventRunner.RunAsync))!.MakeGenericMethod(ctxType));
        runIl.Emit(OpCodes.Ret);

        // public static VendorQuoteResult QuoteVendor(Player player, IReadOnlyList<InventoryItem> offered)
        Type invListType = typeof(IReadOnlyList<>).MakeGenericType(invItemType);
        MethodBuilder qvM = geTb.DefineMethod("QuoteVendor", MethodAttributes.Public | MethodAttributes.Static, vqType, [playerType, invListType]);
        ILGenerator qvIl = qvM.GetILGenerator();
        qvIl.Emit(OpCodes.Ldarg_1);
        qvIl.Emit(OpCodes.Call, typeof(SimpleGameEventRunner).GetMethod(nameof(SimpleGameEventRunner.MakeRewards))!.MakeGenericMethod(invItemType));
        qvIl.Emit(OpCodes.Call, typeof(SimpleGameEventRunner).GetMethod(nameof(SimpleGameEventRunner.MakeRecipes))!);
        qvIl.Emit(OpCodes.Ldnull);
        qvIl.Emit(OpCodes.Newobj, vqCtor);
        qvIl.Emit(OpCodes.Ret);

        geTb.CreateType();
        return ab;
    }

    private static FieldBuilder DefineAutoProp(TypeBuilder tb, string name, Type propType, bool setter)
    {
        FieldBuilder field = tb.DefineField($"_{name}", propType, FieldAttributes.Private);
        PropertyBuilder prop = tb.DefineProperty(name, PropertyAttributes.None, propType, null);

        MethodBuilder getM = tb.DefineMethod($"get_{name}", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, propType, Type.EmptyTypes);
        ILGenerator gil = getM.GetILGenerator();
        gil.Emit(OpCodes.Ldarg_0);
        gil.Emit(OpCodes.Ldfld, field);
        gil.Emit(OpCodes.Ret);
        prop.SetGetMethod(getM);

        if (setter)
        {
            MethodBuilder setM = tb.DefineMethod($"set_{name}", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, null, [propType]);
            ILGenerator sil = setM.GetILGenerator();
            sil.Emit(OpCodes.Ldarg_0);
            sil.Emit(OpCodes.Ldarg_1);
            sil.Emit(OpCodes.Stfld, field);
            sil.Emit(OpCodes.Ret);
            prop.SetSetMethod(setM);
        }

        return field;
    }

    public sealed class SimpleNoopDisposable : IDisposable
    {
        public void Dispose() { }
    }

    public sealed class SimpleGameEvent<T> where T : Delegate
    {
        public List<T> Handlers { get; } = [];

        public IDisposable Add(string owner, T handler)
        {
            Handlers.Add(handler);
            return new RemoveDisposable(() => Handlers.Remove(handler));
        }

        private sealed class RemoveDisposable(Action onDispose) : IDisposable
        {
            public void Dispose() => onDispose();
        }
    }

    public static class SimpleGameEventRunner
    {
        public static async Task RunAsync<TCtx>(SimpleGameEvent<Func<TCtx, Task>> evt, TCtx ctx)
        {
            foreach (Func<TCtx, Task> h in evt.Handlers.ToArray())
            {
                await h(ctx);
            }
        }

        public static IReadOnlyList<object> MakeRewards<TItem>(IReadOnlyList<TItem> offered) =>
            offered.Count > 0 ? ["ScrollFragmentItem"] : [];

        public static IReadOnlyList<string> MakeRecipes() => ["ScrollFragment"];
    }
}
