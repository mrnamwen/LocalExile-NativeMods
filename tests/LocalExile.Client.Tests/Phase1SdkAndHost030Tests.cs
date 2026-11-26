using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using LocalExile.Client.Host;
using LocalExile.Client.Host.Loading;
using LocalExile.Client.Host.Native;
using LocalExile.Client.Host.Services;
using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Engine;
using Xunit;

namespace LocalExile.Client.Tests;

[Collection("NativeEntryPointSerial")]
public sealed class Phase1SdkAndHost030Tests
{
    [Fact]
    public void SdkVersion_Is040WithProtocolVersion1AndBackwardCompatibleRanges()
    {
        Assert.Equal("1.0.0", SdkVersion.Current);
        Assert.Equal(new Version(1, 0, 0), SdkVersion.Parsed);
        Assert.Equal(1, SdkVersion.ProtocolVersion);
        Assert.True(SdkVersion.IsCompatible(">=0.2.0 <2.0.0"));
        Assert.True(SdkVersion.IsCompatible(">=0.3.0 <2.0.0"));
        Assert.True(SdkVersion.IsCompatible(">=0.4.0 <2.0.0"));
        Assert.True(SdkVersion.IsCompatible(">=1.0.0 <2.0.0"));
        Assert.True(SdkVersion.IsCompatible("^1.0.0"));
        Assert.False(SdkVersion.IsCompatible("<1.0.0"));
    }

    [Fact]
    public void StockTreeUiTraversal_SupportsRootFindNativeChildrenAndWrapControl()
    {
        using var bridge = new SimulatedNativeBridge { EnableSimulatedNativeUi = true };
        using var host = new ClientHostRuntime(bridge);

        var frame = bridge.RegisterSimulatedStockControl("create_character_frame", vtableAddress: 0x143033598ul, tag: 0x1234, rect: new UiRect(0, 0, 1200, 800));
        var bottomFlow = bridge.RegisterSimulatedStockControl("create_character_bottom_flow", parentHandle: frame.Handle, vtableAddress: 0x143051F80ul, tag: 0x5678, rect: new UiRect(10, 700, 900, 80));
        var leagueLabel = bridge.RegisterSimulatedStockControl("league_label", parentHandle: bottomFlow.Handle, vtableAddress: 0x1434AE300ul, rect: new UiRect(20, 710, 200, 30));

        IUiControl? root = host.Ui.GetRootControl();
        Assert.NotNull(root);

        IUiControl? foundNested = root!.Find("create_character_frame.create_character_bottom_flow.league_label");
        Assert.NotNull(foundNested);
        Assert.Equal(leagueLabel.Handle, foundNested!.NativeHandle);
        Assert.Equal("league_label", foundNested.Id);
        Assert.Equal(0x1434AE300ul, foundNested.VtableAddress);
        Assert.Equal(bottomFlow.Handle, foundNested.Parent!.NativeHandle);

        // Non-existent path on stock tree returns null rather than hallucinating controls.
        Assert.Null(root.Find("create_character_frame.nonexistent_child"));

        // Direct WrapControl + GetChildren + layout setters.
        IUiControl? wrappedFrame = host.Ui.WrapControl(frame.Handle);
        Assert.NotNull(wrappedFrame);
        Assert.Equal(0x1234u, wrappedFrame!.Tag);
        IReadOnlyList<IUiControl> frameChildren = wrappedFrame.GetChildren();
        Assert.Single(frameChildren);
        Assert.Equal(bottomFlow.Handle, frameChildren[0].NativeHandle);

        wrappedFrame.SetWidth(426f);
        wrappedFrame.SetHeight(48f);
        wrappedFrame.SetAnchor(3, 16f, 0, 12f);
        wrappedFrame.SetAutoWidth(false);
        wrappedFrame.UpdateLayout();

        Assert.Equal(426f, frame.Rect.Width);
        Assert.Equal(48f, frame.Rect.Height);
        Assert.Equal((byte)3, frame.XAnchor);
        Assert.Equal(16f, frame.XAnchorOffset);
        Assert.False(frame.AutoWidth);
        Assert.Equal(1, frame.LayoutUpdateCount);
        Assert.Null(host.Ui.WrapControl(nint.Zero));
    }

    [Fact]
    public void ScreensService_TracksPreGameInGamePanelsStashTabsAutoFlowSlotsAndZoneReattach()
    {
        using var bridge = new SimulatedNativeBridge { EnableSimulatedNativeUi = true };
        using var host = new ClientHostRuntime(bridge);

        var hud1 = bridge.RegisterSimulatedStockControl("InGameUI_1");
        var inv1 = bridge.RegisterSimulatedStockControl("InventoryPanel_1", parentHandle: hud1.Handle);
        var stash1 = bridge.RegisterSimulatedStockControl("StashPanel_1", parentHandle: hud1.Handle);
        var mapPage = bridge.RegisterSimulatedStockControl("MapStashPage_1", parentHandle: stash1.Handle);

        List<PreGameScreenChangedEventArgs> preGameEvents = [];
        List<ScreenChangedEventArgs> screenEvents = [];
        List<ActiveStashTabChangedEventArgs> stashEvents = [];
        host.Screens.PreGameScreenChanged += preGameEvents.Add;
        host.Screens.ScreenChanged += screenEvents.Add;
        host.Screens.ActiveStashTabChanged += stashEvents.Add;

        // Pre-game transition Login -> SelectCharacter -> CreateCharacter -> InGame
        bridge.SimulatedPreGameScreen = PreGameScreen.Login;
        host.TickFrame();
        bridge.SimulatedPreGameScreen = PreGameScreen.SelectCharacter;
        host.TickFrame();
        bridge.SimulatedPreGameScreen = PreGameScreen.InGame;
        bridge.SimulatedInGameUiHandle = hud1.Handle;
        bridge.SimulatedActivePanels = GamePanel.Hud | GamePanel.Inventory | GamePanel.Stash | GamePanel.MapStash;
        bridge.SetSimulatedPanelHandle(GamePanel.Hud, hud1.Handle);
        bridge.SetSimulatedPanelHandle(GamePanel.Inventory, inv1.Handle);
        bridge.SetSimulatedPanelHandle(GamePanel.Stash, stash1.Handle);
        bridge.SetSimulatedPanelHandle(GamePanel.MapStash, mapPage.Handle);
        bridge.SimulatedActiveStashTab = new StashTabInfo(2, 9001u, StashTabType.Map, "Maps", MapTier: 14);

        host.TickFrame();

        Assert.Equal(PreGameScreen.InGame, host.Screens.CurrentPreGameScreen);
        Assert.Equal(3, preGameEvents.Count);
        Assert.True(host.Screens.IsPanelOpen(GamePanel.Inventory));
        Assert.True(host.Screens.IsPanelOpen(GamePanel.MapStash));
        Assert.Equal(14, host.Screens.ActiveStashTab!.MapTier);
        Assert.Single(stashEvents);
        Assert.Contains(screenEvents, e => e.Panel == GamePanel.Stash && e.IsOpen);

        // Register two buttons in InventoryToolbar out-of-order to verify deterministic non-overlapping flow.
        int depositClicks = 0;
        int sortClicks = 0;
        using ISlotAttachment sortBtn = host.Screens.RegisterSlotButton(
            ScreenSlot.InventoryToolbar,
            "sort",
            "Sort",
            () => sortClicks++,
            new SlotAttachmentOptions(Order: 20, Width: 90f, Height: 34f, Spacing: 8f));
        using ISlotAttachment depositBtn = host.Screens.RegisterSlotButton(
            ScreenSlot.InventoryToolbar,
            "deposit_all",
            "Deposit All",
            () => depositClicks++,
            new SlotAttachmentOptions(Order: 10, Width: 110f, Height: 34f, Spacing: 8f));

        host.TickFrame();

        Assert.True(depositBtn.IsAttached);
        Assert.True(sortBtn.IsAttached);
        Assert.Equal(24f, depositBtn.ComputedRect.X);
        Assert.Equal(24f + 110f + 8f, sortBtn.ComputedRect.X);

        depositBtn.Control!.Require<IButton>("btn").Click();
        sortBtn.Control!.Require<IButton>("btn").Click();
        Assert.Equal(1, depositClicks);
        Assert.Equal(1, sortClicks);

        // Simulate zone transition: InGameUI* rebuilt with brand-new handles -> slots auto-reattach!
        var hud2 = bridge.RegisterSimulatedStockControl("InGameUI_2");
        var inv2 = bridge.RegisterSimulatedStockControl("InventoryPanel_2", parentHandle: hud2.Handle);
        nint oldDepositHandle = depositBtn.Control.NativeHandle;
        bridge.SimulatedInGameUiHandle = hud2.Handle;
        bridge.SetSimulatedPanelHandle(GamePanel.Hud, hud2.Handle);
        bridge.SetSimulatedPanelHandle(GamePanel.Inventory, inv2.Handle);

        host.TickFrame();

        Assert.True(depositBtn.IsAttached);
        Assert.NotEqual(oldDepositHandle, depositBtn.Control!.NativeHandle);

        // Map tier change raises ActiveStashTabChanged.
        bridge.SimulatedActiveStashTab = new StashTabInfo(2, 9001u, StashTabType.Map, "Maps", MapTier: 16);
        host.TickFrame();
        Assert.Equal(2, stashEvents.Count);
        Assert.Equal(16, stashEvents[^1].Current!.MapTier);

        // Raw AttachToControl escape hatch works cleanly.
        using var rawUi = host.FileOverlayService.RegisterVirtualTextFile(
            "Metadata/UI/Mods/Test/RawBadge.ui",
            "begin Frame\n  id = \"badge\"\n  begin Label\n    id = \"lbl\"\n    text = \"Raw\"\n  end\nend\n");
        using ISlotAttachment rawAttach = host.Screens.AttachToControl(stash1.Handle, "Metadata/UI/Mods/Test/RawBadge.ui", "raw_badge", 15f, 25f);
        Assert.True(rawAttach.IsAttached);
        Assert.Equal("Raw", rawAttach.Control!.Require<ILabel>("lbl").Text);
    }

    [Fact]
    public void ScreensService_SameZonePanelToggleAndParentSwitchDestroysNativeControl_ZoneChangeDoesNotDoubleFree()
    {
        using var bridge = new SimulatedNativeBridge { EnableSimulatedNativeUi = true };
        using var host = new ClientHostRuntime(bridge);

        var hud1 = bridge.RegisterSimulatedStockControl("InGameUI_1");
        var inv1 = bridge.RegisterSimulatedStockControl("InventoryPanel_1", parentHandle: hud1.Handle);
        var personalStash1 = bridge.RegisterSimulatedStockControl("PersonalStash_1", parentHandle: hud1.Handle);
        var guildStash1 = bridge.RegisterSimulatedStockControl("GuildStash_1", parentHandle: hud1.Handle);

        bridge.SimulatedPreGameScreen = PreGameScreen.InGame;
        bridge.SimulatedInGameUiHandle = hud1.Handle;
        bridge.SimulatedActivePanels = GamePanel.Hud | GamePanel.Inventory | GamePanel.Stash;
        bridge.SetSimulatedPanelHandle(GamePanel.Hud, hud1.Handle);
        bridge.SetSimulatedPanelHandle(GamePanel.Inventory, inv1.Handle);
        bridge.SetSimulatedPanelHandle(GamePanel.Stash, personalStash1.Handle);
        host.TickFrame();

        using ISlotAttachment invBtn = host.Screens.RegisterSlotButton(ScreenSlot.InventoryToolbar, "inv_btn", "Deposit", () => { });
        using ISlotAttachment stashBtn = host.Screens.RegisterSlotButton(ScreenSlot.StashHeader, "stash_btn", "Vendor All", () => { });
        host.TickFrame();

        Assert.True(invBtn.IsAttached);
        Assert.True(stashBtn.IsAttached);
        nint firstInvHandle = invBtn.Control!.NativeHandle;
        nint firstStashHandle = stashBtn.Control!.NativeHandle;
        Assert.Single(bridge.GetNativeUiChildren(inv1.Handle));
        Assert.Single(bridge.GetNativeUiChildren(personalStash1.Handle));

        // (a) Closing Inventory within the SAME InGameUI MUST destroy the mounted native control (no leak).
        bridge.SimulatedActivePanels = GamePanel.Hud | GamePanel.Stash;
        host.TickFrame();

        Assert.False(invBtn.IsAttached);
        Assert.Contains(firstInvHandle, bridge.DestroyedNativeUiHandles);
        Assert.Empty(bridge.GetNativeUiChildren(inv1.Handle));

        // Re-opening Inventory in the same InGameUI mounts a fresh single frame (no duplicate).
        bridge.SimulatedActivePanels = GamePanel.Hud | GamePanel.Inventory | GamePanel.Stash;
        host.TickFrame();

        Assert.True(invBtn.IsAttached);
        nint secondInvHandle = invBtn.Control!.NativeHandle;
        Assert.NotEqual(firstInvHandle, secondInvHandle);
        Assert.Single(bridge.GetNativeUiChildren(inv1.Handle));

        // (b) Switching Stash parent (Personal -> Guild) in the SAME InGameUI MUST destroy prior native control.
        bridge.SetSimulatedPanelHandle(GamePanel.Stash, guildStash1.Handle);
        host.TickFrame();

        Assert.True(stashBtn.IsAttached);
        nint guildStashBtnHandle = stashBtn.Control!.NativeHandle;
        Assert.NotEqual(firstStashHandle, guildStashBtnHandle);
        Assert.Contains(firstStashHandle, bridge.DestroyedNativeUiHandles);
        Assert.Empty(bridge.GetNativeUiChildren(personalStash1.Handle));
        Assert.Single(bridge.GetNativeUiChildren(guildStash1.Handle));

        // (c) Zone transition: InGameUI handle changes -> prior tree already freed by InGameState::Leave,
        // so UnmountAttachment MUST use destroyNative: false (NO double-free on secondInvHandle or guildStashBtnHandle).
        var hud2 = bridge.RegisterSimulatedStockControl("InGameUI_2");
        var inv2 = bridge.RegisterSimulatedStockControl("InventoryPanel_2", parentHandle: hud2.Handle);
        var personalStash2 = bridge.RegisterSimulatedStockControl("PersonalStash_2", parentHandle: hud2.Handle);
        bridge.SimulatedInGameUiHandle = hud2.Handle;
        bridge.SetSimulatedPanelHandle(GamePanel.Hud, hud2.Handle);
        bridge.SetSimulatedPanelHandle(GamePanel.Inventory, inv2.Handle);
        bridge.SetSimulatedPanelHandle(GamePanel.Stash, personalStash2.Handle);
        host.TickFrame();

        Assert.True(invBtn.IsAttached);
        Assert.True(stashBtn.IsAttached);
        Assert.DoesNotContain(secondInvHandle, bridge.DestroyedNativeUiHandles);
        Assert.DoesNotContain(guildStashBtnHandle, bridge.DestroyedNativeUiHandles);
    }

    [Fact]
    public void LeagueCardLayoutSolver_And_LeagueCardModes_EqualizeCompactAndReinjectAcrossFills()
    {
        // Unit verify exact #9 compaction math inside 500x905 card.
        var fitsNormal = LeagueCardLayoutSolver.Solve(descBottomY: 580f, currentModeStackY: 628f, stockCount: 3, modCount: 1, [210f, 260f]);
        Assert.False(fitsNormal.GapCompacted);
        Assert.Equal(59f, fitsNormal.CheckboxHeight);
        Assert.Equal(260f, fitsNormal.EqualizedWidth);

        var compactsGapAndHeight = LeagueCardLayoutSolver.Solve(descBottomY: 580f, currentModeStackY: 628f, stockCount: 3, modCount: 3, [220f, 510f]);
        Assert.True(compactsGapAndHeight.GapCompacted);
        Assert.Equal(580f + 16f, compactsGapAndHeight.ModeStackY);
        Assert.Equal(48f, compactsGapAndHeight.CheckboxHeight); // floor((885 - 596) / 6) = 48
        Assert.Equal(426f, compactsGapAndHeight.EqualizedWidth); // clamped to 500 - 74 = 426

        var clampsMinHeight = LeagueCardLayoutSolver.Solve(descBottomY: 620f, currentModeStackY: 668f, stockCount: 3, modCount: 8, [180f]);
        Assert.Equal(36f, clampsMinHeight.CheckboxHeight);

        // Full LeagueOverlay::Fill re-injection flow with state persistence across rebuilds.
        using var bridge = new SimulatedNativeBridge { EnableSimulatedNativeUi = true };
        using var host = new ClientHostRuntime(bridge);

        List<(string League, bool Checked)> toggles = [];
        using ILeagueModeRegistration skipCampaign = host.Screens.RegisterLeagueCardMode(
            new LeagueModeCheckboxOptions("skip_campaign", "Skip Campaign", DefaultChecked: false, Order: 10, NaturalWidth: 250f),
            (league, isChecked) => toggles.Add((league, isChecked)));

        int fillEventCount = 0;
        host.Screens.LeagueOverlayFilled += _ => fillEventCount++;

        // Fill #1: builds card with 3 stock checkboxes + mode StackLayout.
        var card1 = bridge.RegisterSimulatedStockControl("Standard", tag: NativeShimConstants.TagLeagueCard, rect: new UiRect(0, 0, 500, 905));
        var stack1 = bridge.RegisterSimulatedStockControl("mode_stack", parentHandle: card1.Handle, rect: new UiRect(0, 628, 300, 177));
        var hc = bridge.RegisterSimulatedStockControl("hc", parentHandle: stack1.Handle, rect: new UiRect(0, 0, 210, 59));
        var ssf = bridge.RegisterSimulatedStockControl("ssf", parentHandle: stack1.Handle, rect: new UiRect(0, 59, 230, 59));
        var ruthless = bridge.RegisterSimulatedStockControl("ruthless", parentHandle: stack1.Handle, rect: new UiRect(0, 118, 220, 59));

        bridge.SimulateLeagueOverlayFilled(0x8800, [new LeagueCardInfo(0, "Standard", card1.Handle, stack1.Handle, 3, 580f, 628f, 230f)]);

        Assert.Equal(1, fillEventCount);
        IReadOnlyList<nint> stackChildren = bridge.GetNativeUiChildren(stack1.Handle);
        Assert.Equal(4, stackChildren.Count); // 3 stock + 1 injected mod checkbox

        nint modCbHandle = stackChildren[3];
        var modCbSim = bridge.GetSimulatedNativeUiControlByHandle(modCbHandle)!;
        Assert.Equal(modCbSim.Rect.Width, hc.Rect.Width);
        Assert.Equal(modCbSim.Rect.Width, ssf.Rect.Width);
        Assert.Equal(modCbSim.Rect.Width, ruthless.Rect.Width);
        Assert.False(hc.AutoWidth);
        Assert.False(modCbSim.AutoWidth);
        Assert.Equal((byte)3, modCbSim.XAnchor);
        Assert.Equal((byte)0xFF, modCbSim.YAnchor);

        // Player checks "Skip Campaign" on Standard card.
        Assert.True(bridge.SimulateNativeUiCheckBoxChanged(modCbHandle, true));
        Assert.True(skipCampaign.GetChecked("Standard"));
        Assert.Single(toggles);
        Assert.Equal(("Standard", true), toggles[0]);

        // Fill #2: LeagueOverlay::Fill destroys old cards and builds fresh ones -> mod checkbox re-injected WITH checked=true!
        var card2 = bridge.RegisterSimulatedStockControl("Standard", tag: NativeShimConstants.TagLeagueCard, rect: new UiRect(0, 0, 500, 905));
        var stack2 = bridge.RegisterSimulatedStockControl("mode_stack_2", parentHandle: card2.Handle, rect: new UiRect(0, 628, 300, 177));
        bridge.SimulateLeagueOverlayFilled(0x8800, [new LeagueCardInfo(0, "Standard", card2.Handle, stack2.Handle, 0, 580f, 628f, 0f)]);

        Assert.Equal(2, fillEventCount);
        IReadOnlyList<nint> stack2Children = bridge.GetNativeUiChildren(stack2.Handle);
        Assert.Single(stack2Children);
        Assert.True(bridge.GetSimulatedNativeUiControlByHandle(stack2Children[0])!.IsChecked);
    }

    [Fact]
    public void InputService_And_KeybindRegistry_SuppressCtrlShiftRmbRespectTextFocusAndPersistJson()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"nm-kb-test-{Guid.NewGuid():N}");
        string keybindsPath = Path.Combine(tempDir, "keybinds.json");
        try
        {
            using var bridge = new SimulatedNativeBridge();
            using var host = new ClientHostRuntime(bridge, new ClientHostOptions { KeybindsPath = keybindsPath });

            // Chord parsing & formatting round-trip.
            Assert.True(KeyChord.TryParse("Ctrl+Shift+RMB", out KeyChord parsedMouse));
            Assert.Equal(KeyChord.FromMouse(MouseButton.Right, KeyModifiers.Control | KeyModifiers.Shift), parsedMouse);
            Assert.Equal("Ctrl+Shift+RMB", parsedMouse.ToString());
            Assert.True(KeyChord.TryParse("Alt+F6", out KeyChord parsedKey));
            Assert.Equal("Alt+F6", parsedKey.ToString());

            // Ctrl+Shift+RMB handler suppresses BOTH Down AND matching Up.
            int bulkTriggers = 0;
            using IDisposable mouseSub = host.Input.OnMouse(args =>
            {
                if (args.Kind == InputEventKind.Down &&
                    args.Button == MouseButton.Right &&
                    args.Modifiers == (KeyModifiers.Control | KeyModifiers.Shift))
                {
                    bulkTriggers++;
                    args.Handled = true;
                }
            });

            Assert.True(bridge.SimulateMouseEvent(MouseButton.Right, InputEventKind.Down, 500, 400, KeyModifiers.Control | KeyModifiers.Shift));
            Assert.Equal(1, bulkTriggers);
            Assert.True(bridge.SimulateMouseEvent(MouseButton.Right, InputEventKind.Up, 500, 400, KeyModifiers.Control | KeyModifiers.Shift));
            // Subsequent unrelated RMB click is NOT suppressed.
            Assert.False(bridge.SimulateMouseEvent(MouseButton.Right, InputEventKind.Down, 500, 400, KeyModifiers.None));
            Assert.False(bridge.SimulateMouseEvent(MouseButton.Right, InputEventKind.Up, 500, 400, KeyModifiers.None));

            // Keybind registration, text-input focus gating, conflict detection, rebinding & persistence.
            int kb1Fired = 0;
            int kb2Fired = 0;
            List<KeybindConflict> conflicts = [];
            host.Input.Keybinds.ConflictDetected += conflicts.Add;

            using IDisposable kb1 = host.Input.Keybinds.Register("stashqol.deposit", "Deposit All", KeyChord.FromKey('D', KeyModifiers.Control), () => kb1Fired++, suppressGameInput: true);
            using IDisposable kb2 = host.Input.Keybinds.Register("other.action", "Other Action", KeyChord.FromKey('D', KeyModifiers.Control), () => kb2Fired++);

            Assert.Single(conflicts);
            Assert.Single(host.Input.Keybinds.Conflicts);

            // Text input focused -> default keybinds and key handlers are skipped!
            bridge.SimulatedTextInputFocused = true;
            Assert.True(host.Input.IsTextInputFocused);
            Assert.False(bridge.SimulateKeyEvent('D', KeyModifiers.Control, isDown: true));
            Assert.Equal(0, kb1Fired);

            // Opt-in handler still runs when text input is focused.
            int optInHits = 0;
            using IDisposable optInSub = host.Input.OnKey(_ => optInHits++, allowWhenTextInputFocused: true);
            bridge.SimulateKeyEvent('D', KeyModifiers.Control, isDown: true);
            Assert.Equal(1, optInHits);

            // Text input unfocused -> Ctrl+D fires and suppresses game input.
            bridge.SimulatedTextInputFocused = false;
            Assert.True(bridge.SimulateKeyEvent('D', KeyModifiers.Control, isDown: true));
            Assert.Equal(1, kb1Fired);
            Assert.Equal(1, kb2Fired);

            // Rebind kb2 to Alt+F6 -> resolves conflict and writes keybinds.json!
            Assert.True(host.Input.Keybinds.Rebind("other.action", parsedKey, out IReadOnlyList<KeybindConflict> afterRebind));
            Assert.Empty(afterRebind);
            Assert.Empty(host.Input.Keybinds.Conflicts);
            Assert.True(File.Exists(keybindsPath));

            // New registry loads persisted override from disk.
            var reloaded = new KeybindRegistry(keybindsPath);
            Assert.Equal(parsedKey, reloaded.GetBinding("other.action"));
            Assert.True(host.Input.Keybinds.ResetBinding("other.action"));
            Assert.Equal(KeyChord.FromKey('D', KeyModifiers.Control), host.Input.Keybinds.GetBinding("other.action"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void HoverService_InspectsHoveredControlHoveredItemCursorItemAndRaisesEvents()
    {
        using var bridge = new SimulatedNativeBridge { EnableSimulatedNativeUi = true };
        using var host = new ClientHostRuntime(bridge);

        var itemView = bridge.RegisterSimulatedStockControl("item_view_1", vtableAddress: 0x143487270ul, tag: NativeShimConstants.TagInvItemView);
        var hoveredItem = new HoveredItemInfo(
            EntityHandle: 0x5500,
            ControlHandle: itemView.Handle,
            ItemId: 42,
            InventoryId: 1,
            GridX: 3,
            GridY: 2,
            Width: 1,
            Height: 1,
            MetadataPath: "Metadata/Items/Currency/CurrencyRerollRare",
            BaseName: "Chaos Orb",
            StackSize: 17,
            MaxStackSize: 20,
            Rarity: ItemRarity.Normal,
            IsIdentified: true,
            IsCorrupted: false,
            ItemLevel: 84);
        var cursorItem = new CursorItemInfo(
            Mode: CursorMode.UsingItem,
            EntityHandle: 0x5600,
            ItemId: 99,
            SourceInventoryId: 1,
            SourceGridX: 0,
            SourceGridY: 0,
            Width: 1,
            Height: 1,
            MetadataPath: "Metadata/Items/Currency/CurrencyRerollMagic",
            BaseName: "Orb of Alteration",
            StackSize: 20,
            MaxStackSize: 20,
            Rarity: ItemRarity.Normal,
            IsIdentified: true,
            IsCorrupted: false,
            ShiftKeep: true);

        List<HoverChangedEventArgs> events = [];
        host.Hover.HoverChanged += events.Add;

        bridge.SimulatedHoveredControlHandle = itemView.Handle;
        bridge.SimulatedHoveredItem = hoveredItem;
        bridge.SimulatedCursorItem = cursorItem;

        host.TickFrame();

        Assert.Single(events);
        Assert.Equal(itemView.Handle, host.Hover.HoveredControl!.NativeHandle);
        Assert.Equal(NativeShimConstants.TagInvItemView, host.Hover.HoveredControl.Tag);
        Assert.Equal("Chaos Orb", host.Hover.HoveredItem!.BaseName);
        Assert.Equal(17u, host.Hover.HoveredItem.StackSize);
        Assert.Equal(CursorMode.UsingItem, host.Hover.CursorItem!.Mode);

        // Immediate RefreshHoveredItem updates synchronously before next TickFrame.
        bridge.SimulatedHoveredItem = null;
        Assert.Null(host.Hover.RefreshHoveredItem());
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void ToastService_QueuesStacksAutoDismissesAndTearsDownOnDisconnect()
    {
        using var bridge = new SimulatedNativeBridge { EnableSimulatedNativeUi = true };
        using var host = new ClientHostRuntime(bridge);

        host.Toasts.MaxVisibleToasts = 2;
        ulong id1 = host.Toasts.Show("PoB Import", "Applied 3 passive points", ToastSeverity.Success, TimeSpan.FromSeconds(1.0));
        ulong id2 = host.Toasts.Show("Stash QoL", "Deposited 14 items", ToastSeverity.Info, TimeSpan.FromSeconds(2.0));
        ulong id3 = host.Toasts.Show("Warning", "Stash tab full", ToastSeverity.Warning, TimeSpan.FromSeconds(3.0));

        Assert.Equal(2, host.Toasts.ActiveToasts.Count);
        Assert.Equal(1, host.Toasts.QueuedCount);
        Assert.NotNull(host.Toasts.OverlayControl);
        Assert.True(host.Toasts.OverlayControl!.Visible);
        Assert.Equal("PoB Import", host.Toasts.OverlayControl.Require<ILabel>("toast_0.title").Text);

        // Tick 1.1s -> id1 expires, id3 promoted from queue!
        host.TickFrame(1.1f);
        Assert.Equal(2, host.Toasts.ActiveToasts.Count);
        Assert.Equal(0, host.Toasts.QueuedCount);
        Assert.Equal(id2, host.Toasts.ActiveToasts[0].Id);
        Assert.Equal(id3, host.Toasts.ActiveToasts[1].Id);

        // Manual dismiss works.
        Assert.True(host.Toasts.Dismiss(id2));
        Assert.Single(host.Toasts.ActiveToasts);
        Assert.Equal(id3, host.Toasts.ActiveToasts[0].Id);
        Assert.False(host.Toasts.Dismiss(id1));

        // Disconnect clears all toasts and tears down overlay cleanly.
        host.NotifyDisconnected("logout");
        Assert.Empty(host.Toasts.ActiveToasts);
        Assert.Null(host.Toasts.OverlayControl);
    }

    [Fact]
    public void PreGameService_FormatsParsesAndValidatesStartStateOptionsAndCreatedResult()
    {
        using var bridge = new SimulatedNativeBridge();
        using var host = new ClientHostRuntime(bridge);

        // No options -> stock S78 untouched.
        Assert.Equal("DexDefault", bridge.SimulateCharacterCreating("RangerOne", "Standard", "DexDefault"));

        host.PreGame.CharacterCreating += ctx =>
        {
            ctx.SetOption("skip_campaign", "1");
            ctx.SetOption("target_town", "2_11_endgame_town");
        };

        string wireS78 = bridge.SimulateCharacterCreating("RangerOne", "Standard", "DexDefault");
        Assert.Equal("DexDefault|nm:skip_campaign=1&target_town=2_11_endgame_town", wireS78);

        Assert.True(CharacterCreateContext.TryParseStartState(wireS78, out string parsedBase, out var parsedOpts));
        Assert.Equal("DexDefault", parsedBase);
        Assert.Equal("1", parsedOpts["skip_campaign"]);
        Assert.Equal("2_11_endgame_town", parsedOpts["target_town"]);

        // Validation guards: invalid keys, control chars, >32 options, >1024-char option tail.
        var badCtx = new CharacterCreateContext("Test", "Standard", "StrDefault");
        Assert.Throws<ArgumentException>(() => badCtx.SetOption("bad|key", "1"));
        Assert.Throws<ArgumentException>(() => badCtx.SetOption("good_key", "bad\nval"));
        Assert.Throws<InvalidOperationException>(() => badCtx.SetOption("huge", new string('x', 1100)));
        for (int i = 0; i < CharacterCreateContext.MaxOptionCount; i++)
        {
            badCtx.SetOption($"k{i}", "1");
        }
        Assert.Throws<InvalidOperationException>(() => badCtx.SetOption("k32", "1"));

        CharacterCreatedEventArgs? created = null;
        host.PreGame.CharacterCreated += e => created = e;
        bridge.SimulateCharacterCreated("RangerOne", "Standard", 0);
        Assert.NotNull(created);
        Assert.True(created!.Succeeded);
        Assert.Equal("RangerOne", created.Name);

        bridge.SimulateCharacterCreated("RangerOne", "Standard", 5);
        Assert.False(created.Succeeded);
        Assert.Equal((ushort)5, created.ResultCode);
    }

    [Fact]
    public async Task ScopedGameServices_Phase1FaultIsolationAndAutoCleanupOnModUnload()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"nm-scoped-p1-{Guid.NewGuid():N}");
        try
        {
            using var bridge = new SimulatedNativeBridge { EnableSimulatedNativeUi = true };
            using var host = new ClientHostRuntime(bridge, new ClientHostOptions
            {
                KeybindsPath = Path.Combine(tempDir, "keybinds.json"),
                MaxModFaults = 2,
            });

            var hud = bridge.RegisterSimulatedStockControl("InGameUI");
            var inv = bridge.RegisterSimulatedStockControl("InventoryPanel", parentHandle: hud.Handle);
            bridge.SimulatedPreGameScreen = PreGameScreen.InGame;
            bridge.SimulatedInGameUiHandle = hud.Handle;
            bridge.SimulatedActivePanels = GamePanel.Hud | GamePanel.Inventory;
            bridge.SetSimulatedPanelHandle(GamePanel.Hud, hud.Handle);
            bridge.SetSimulatedPanelHandle(GamePanel.Inventory, inv.Handle);
            host.TickFrame();

            var modA = new Phase1TestMod();
            var faultyMod = new FaultingPhase1Mod();
            host.Loader.LoadInProcessMod(new ClientModManifest("phase1-good", "Good Mod", "1.0.0"), modA);
            host.Loader.LoadInProcessMod(new ClientModManifest("phase1-bad", "Faulting Mod", "1.0.0"), faultyMod);

            Assert.Single(host.Screens.GetSlotAttachments(ScreenSlot.InventoryToolbar));
            Assert.Single(host.Input.Keybinds.Entries);
            Assert.Single(host.Toasts.ActiveToasts);

            // Faulting mod throws on input without breaking healthy mod or suppressing input.
            Assert.False(bridge.SimulateKeyEvent('K', KeyModifiers.Control, isDown: true));
            Assert.Equal(1, modA.KeyHits);
            Assert.False(bridge.SimulateKeyEvent('K', KeyModifiers.Control, isDown: true)); // 2nd fault -> disables faultyMod!
            Assert.True(host.Loader.GetMod("phase1-bad")!.IsDisabled);
            Assert.Equal(2, modA.KeyHits);

            // PreGame hook still works for healthy mod.
            Assert.Equal("IntDefault|nm:from_good=1", bridge.SimulateCharacterCreating("WitchOne", "Standard", "IntDefault"));

            // Unload healthy mod -> EVERY Phase 1 resource auto-disposed!
            ModUnloadResult unload = await host.Loader.UnloadModAsync("phase1-good");
            Assert.True(unload.WasLoaded);
            Assert.Empty(host.Screens.GetSlotAttachments(ScreenSlot.InventoryToolbar));
            Assert.Empty(host.Input.Keybinds.Entries);
            Assert.Empty(host.Toasts.ActiveToasts);
            Assert.Equal("IntDefault", bridge.SimulateCharacterCreating("WitchOne", "Standard", "IntDefault"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    // --- Live NativeShimBridge C ABI 0.3.0 Parity Test (Slots 69..88) ---

    private static readonly byte[] s_abiSymName = AllocatePinnedUtf8("frame.state_stack_pass_a_hook");
    private static readonly byte[] s_abiSymCat = AllocatePinnedUtf8("frame");
    private static readonly byte[] s_abiSymBytes = AllocatePinnedUtf8("488b5d284883eb10");
    private static unsafe delegate* unmanaged[Cdecl]<int, void*, ulong, void*, void> s_screenCb;
    private static unsafe void* s_screenUd;
    private static unsafe delegate* unmanaged[Cdecl]<NmInputEvent*, void*, int> s_inputCb;
    private static unsafe void* s_inputUd;
    private static unsafe delegate* unmanaged[Cdecl]<byte*, byte*, byte*, byte*, nuint, void*, int> s_creatingCb;
    private static unsafe delegate* unmanaged[Cdecl]<byte*, byte*, ushort, void*, void> s_createdCb;
    private static unsafe void* s_preGameUd;
    private static float s_lastWidthSet;
    private static float s_lastHeightSet;
    private static int s_layoutUpdatedCount;

    private static byte[] AllocatePinnedUtf8(string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        byte[] pinned = GC.AllocateArray<byte>(utf8.Length + 1, pinned: true);
        utf8.CopyTo(pinned, 0);
        pinned[utf8.Length] = 0;
        return pinned;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock030GetHostInfo(NmHostInfo* outInfo)
    {
        outInfo->AbiVersion = NativeShimConstants.AbiVersion;
        outInfo->BuildId = NmBuildId.Build3293d328228;
        outInfo->Flags = NmHostFlags.Initialized | NmHostFlags.Simulated;
        outInfo->ImageBase = NativeShimConstants.NominalImageBase;
        outInfo->SymbolCount = 1;
        "3.29.3d\0"u8.CopyTo(new Span<byte>(outInfo->BuildVersion, 16));
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock030LookupSymbol(byte* name, NmSymbolInfo* outSym) => Fill030Symbol0(outSym);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock030GetSymbolAt(uint index, NmSymbolInfo* outSym) =>
        index == 0 ? Fill030Symbol0(outSym) : NmStatus.ErrSymbolNotFound;

    private static unsafe NmStatus Fill030Symbol0(NmSymbolInfo* outSym)
    {
        outSym->Name = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(s_abiSymName));
        outSym->Category = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(s_abiSymCat));
        outSym->Kind = NmSymbolKind.Code;
        outSym->Status = NmSymbolStatus.LiveVerified;
        outSym->Rva = 0x1528FBFul;
        outSym->RuntimeAddress = NativeShimConstants.NominalImageBase + 0x1528FBFul;
        outSym->ExpectedBytesHex = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(s_abiSymBytes));
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock030UiGetActiveRoot(void** outRoot) { *outRoot = (void*)0xA000; return NmStatus.Ok; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock030UiGetChildCount(void* parent, uint* outCount)
    {
        *outCount = (nint)parent switch
        {
            0xA000 => 1u,
            0xC000 => 1u, // cards flow -> 1 card
            0xC100 => 5u, // card -> [0..3, modeStack]
            _ => 0u,
        };
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock030UiGetChildAt(void* parent, uint index, void** outChild)
    {
        *outChild = ((nint)parent, index) switch
        {
            (0xA000, 0) => (void*)0xA001,
            (0xC000, 0) => (void*)0xC100,
            (0xC100, 4) => (void*)0xC200,
            (0xC100, _) => (void*)(0xC110 + (nint)index),
            _ => null,
        };
        return *outChild is not null ? NmStatus.Ok : NmStatus.ErrInvalidArg;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock030UiGetVtable(void* ctrl, ulong* outVt) { *outVt = 0x1431ECCB0ul; return NmStatus.Ok; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock030UiGetTag(void* ctrl, ushort* outTag)
    {
        *outTag = (nint)ctrl == 0xC100 ? NativeShimConstants.TagLeagueCard : (ushort)0;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock030UiGetId(void* ctrl, byte* outBuf, nuint cap, nuint* outLen)
    {
        byte[] bytes = (nint)ctrl == 0xC100 ? "Standard"u8.ToArray() : "stock_ctrl"u8.ToArray();
        if (outLen is not null) *outLen = (nuint)bytes.Length;
        if (outBuf is null || cap == 0) return NmStatus.Ok;
        if (cap <= (nuint)bytes.Length) return NmStatus.ErrBufferTooSmall;
        bytes.CopyTo(new Span<byte>(outBuf, bytes.Length));
        outBuf[bytes.Length] = 0;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock030UiGetParent(void* ctrl, void** outParent) { *outParent = (void*)0xA000; return NmStatus.Ok; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock030UiIsVisible(void* ctrl, int* outVis) { *outVis = 1; return NmStatus.Ok; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock030UiSetWidth(void* ctrl, float w) { s_lastWidthSet = w; return NmStatus.Ok; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock030UiSetHeight(void* ctrl, float h) { s_lastHeightSet = h; return NmStatus.Ok; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock030UiCreateCheckBoxChild(void* parent, byte* caption, void** outCb) { *outCb = (void*)0xC250; return NmStatus.Ok; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock030UiLayoutUpdate(void* ctrl) { s_layoutUpdatedCount++; return NmStatus.Ok; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock030ScreensGetState(NmScreensState* s)
    {
        s->Screen = (int)NmPreGameScreen.InGame;
        s->VisiblePanels = (uint)(NmPanelFlags.Hud | NmPanelFlags.Stash | NmPanelFlags.MapStash | NmPanelFlags.Minimap);
        s->StashMode = 0;
        s->ActiveUiRoot = (void*)0xA000;
        s->HudRoot = (void*)0xB000;
        s->StashPanel = (void*)0xB100;
        s->MapStashPage = (void*)0xB200;
        s->LeagueOverlay = (void*)0xBF00;
        s->LeagueCardsFlow = (void*)0xC000;
        s->ActiveStashTabIndex = 4;
        s->ActiveStashTabId = 777u;
        s->ActiveStashTabType = (uint)StashTabType.Map;
        s->ActiveMapTier = 16;
        "Endgame Maps\0"u8.CopyTo(new Span<byte>(s->ActiveStashTabName, 128));
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock030ScreensSetCb(delegate* unmanaged[Cdecl]<int, void*, ulong, void*, void> cb, void* ud)
    {
        s_screenCb = cb;
        s_screenUd = ud;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock030InputSetCb(delegate* unmanaged[Cdecl]<NmInputEvent*, void*, int> cb, void* ud)
    {
        s_inputCb = cb;
        s_inputUd = ud;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock030InputIsTextFocused(int* outFocused) { *outFocused = 1; return NmStatus.Ok; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock030HoverGetState(NmHoverState* s)
    {
        s->MouseX = 640;
        s->MouseY = 360;
        s->CursorMode = (int)NmCursorMode.UseMode;
        s->HoveredControl = (void*)0xD000;
        s->UseSourceInvId = 1;
        s->UseCellX = 2;
        s->UseCellY = 3;
        s->HoveredItem.Present = 1;
        s->HoveredItem.ItemEntity = (void*)0xE000;
        s->HoveredItem.ItemView = (void*)0xD000;
        s->HoveredItem.InventoryId = 1;
        s->HoveredItem.ItemId = 55;
        s->HoveredItem.GridX = 4;
        s->HoveredItem.GridY = 1;
        s->HoveredItem.GridW = 1;
        s->HoveredItem.GridH = 1;
        s->HoveredItem.StackSize = 10;
        s->HoveredItem.MaxStackSize = 20;
        s->HoveredItem.Rarity = 0;
        s->HoveredItem.Identified = 1;
        "Metadata/Items/Currency/CurrencyUpgradeToRare\0"u8.CopyTo(new Span<byte>(s->HoveredItem.MetadataPath, 256));
        "Orb of Alchemy\0"u8.CopyTo(new Span<byte>(s->HoveredItem.BaseName, 128));
        s->CursorItem.Present = 1;
        s->CursorItem.ItemEntity = (void*)0xE100;
        s->CursorItem.StackSize = 5;
        s->CursorItem.MaxStackSize = 20;
        "Orb of Scouring\0"u8.CopyTo(new Span<byte>(s->CursorItem.BaseName, 128));
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock030PreGameSetCbs(
        delegate* unmanaged[Cdecl]<byte*, byte*, byte*, byte*, nuint, void*, int> onCreating,
        delegate* unmanaged[Cdecl]<byte*, byte*, ushort, void*, void> onCreated,
        void* ud)
    {
        s_creatingCb = onCreating;
        s_createdCb = onCreated;
        s_preGameUd = ud;
        return NmStatus.Ok;
    }

    [Fact]
    public unsafe void NativeShimBridge_WiresAllPhase1Abi030Slots69Through88()
    {
        s_screenCb = null;
        s_inputCb = null;
        s_creatingCb = null;
        s_createdCb = null;
        s_lastWidthSet = 0f;
        s_lastHeightSet = 0f;
        s_layoutUpdatedCount = 0;

        NmAbiTable table = default;
        table.StructSize = (uint)sizeof(NmAbiTable);
        table.AbiVersion = NativeShimConstants.AbiVersion;
        table.GetHostInfo = &Mock030GetHostInfo;
        table.LookupSymbol = &Mock030LookupSymbol;
        table.GetSymbolAt = &Mock030GetSymbolAt;
        table.UiGetActiveRoot = &Mock030UiGetActiveRoot;
        table.UiGetChildCount = &Mock030UiGetChildCount;
        table.UiGetChildAt = &Mock030UiGetChildAt;
        table.UiGetVtable = &Mock030UiGetVtable;
        table.UiGetControlTag = &Mock030UiGetTag;
        table.UiGetControlId = &Mock030UiGetId;
        table.UiGetControlParent = &Mock030UiGetParent;
        table.UiIsVisible = &Mock030UiIsVisible;
        table.UiSetWidth = &Mock030UiSetWidth;
        table.UiSetHeight = &Mock030UiSetHeight;
        table.UiCreateCheckBoxChild = &Mock030UiCreateCheckBoxChild;
        table.UiLayoutUpdate = &Mock030UiLayoutUpdate;
        table.ScreensGetState = &Mock030ScreensGetState;
        table.ScreensSetCallback = &Mock030ScreensSetCb;
        table.InputSetCallback = &Mock030InputSetCb;
        table.InputIsTextFocused = &Mock030InputIsTextFocused;
        table.HoverGetState = &Mock030HoverGetState;
        table.PreGameSetCallbacks = &Mock030PreGameSetCbs;

        using var bridge = new NativeShimBridge((nint)(&table));
        Assert.Equal((nint)0xA000, bridge.GetNativeUiRoot());
        Assert.Single(bridge.GetNativeUiChildren(0xA000));
        Assert.Equal(0x1431ECCB0ul, bridge.GetNativeUiVtable(0xC100));
        Assert.Equal(NativeShimConstants.TagLeagueCard, bridge.GetNativeUiTag(0xC100));
        Assert.Equal("Standard", bridge.GetNativeUiId(0xC100));
        Assert.Equal((nint)0xA000, bridge.GetNativeUiParent(0xA001));
        Assert.True(bridge.TryGetNativeUiVisible(0xA001, out bool vis) && vis);
        bridge.SetNativeUiWidth(0xA001, 250f);
        bridge.SetNativeUiHeight(0xA001, 44f);
        bridge.UpdateNativeUiLayout(0xA001);
        Assert.Equal(250f, s_lastWidthSet);
        Assert.Equal(44f, s_lastHeightSet);
        Assert.Equal(1, s_layoutUpdatedCount);
        Assert.Equal((nint)0xC250, bridge.CreateNativeUiCheckBoxChild(0xC200, "Skip Campaign", false));

        // Screens + Stash + Hover + TextFocus
        Assert.Equal(PreGameScreen.InGame, bridge.GetPreGameScreen());
        Assert.True(bridge.GetActivePanels().HasFlag(GamePanel.MapStash));
        Assert.True(bridge.IsMinimapVisible());
        StashTabInfo? tab = bridge.GetActiveStashTab();
        Assert.NotNull(tab);
        Assert.Equal("Endgame Maps", tab!.Name);
        Assert.Equal(16, tab.MapTier);
        Assert.True(bridge.IsTextInputFocused());
        HoveredItemInfo? hover = bridge.GetHoveredItem();
        Assert.NotNull(hover);
        Assert.Equal("Orb of Alchemy", hover!.BaseName);
        CursorItemInfo? cursor = bridge.GetCursorItem();
        Assert.NotNull(cursor);
        Assert.Equal(CursorMode.UsingItem, cursor!.Mode);
        Assert.Equal(2, cursor.SourceGridX);

        // Callbacks: LeagueOverlayFilled, Input suppression, PreGame S78 override & Created
        int filledCards = 0;
        using IDisposable screenSub = bridge.RegisterLeagueOverlayFilledCallback((_, cards) => filledCards = cards.Count);
        Assert.True(s_screenCb is not null);
        s_screenCb((int)NmScreenEventKind.LeagueOverlayFilled, (void*)0xBF00, 0, s_screenUd);
        Assert.Equal(1, filledCards);

        using IDisposable mouseSub = bridge.RegisterMouseCallback(ev => ev.Button == MouseButton.Right && ev.Modifiers == (KeyModifiers.Control | KeyModifiers.Shift));
        Assert.True(s_inputCb is not null);
        NmInputEvent rmbEv = new()
        {
            StructSize = (uint)sizeof(NmInputEvent),
            Kind = (int)NmInputKind.MouseDown,
            MouseButton = (int)NmMouseButton.Right,
            Modifiers = (uint)(NmInputModifiers.Ctrl | NmInputModifiers.Shift),
        };
        Assert.Equal(1, s_inputCb(&rmbEv, s_inputUd));

        ushort observedResult = 99;
        using IDisposable creatingSub = bridge.RegisterCharacterCreatingCallback((_, _, s78) => $"{s78}|nm:skip_campaign=1");
        using IDisposable createdSub = bridge.RegisterCharacterCreatedCallback((_, _, code) => observedResult = code);
        Assert.True(s_creatingCb is not null && s_createdCb is not null);

        byte[] nameBuf = "Exile\0"u8.ToArray();
        byte[] leagueBuf = "Standard\0"u8.ToArray();
        byte[] startBuf = "DexDefault\0"u8.ToArray();
        byte[] outBuf = new byte[256];
        fixed (byte* pName = nameBuf)
        fixed (byte* pLeague = leagueBuf)
        fixed (byte* pStart = startBuf)
        fixed (byte* pOut = outBuf)
        {
            int overridden = s_creatingCb(pName, pLeague, pStart, pOut, (nuint)outBuf.Length, s_preGameUd);
            Assert.Equal(1, overridden);
            Assert.Equal("DexDefault|nm:skip_campaign=1", Marshal.PtrToStringUTF8((nint)pOut));
            s_createdCb(pName, pLeague, 0, s_preGameUd);
            Assert.Equal((ushort)0, observedResult);
        }
    }

    private sealed class Phase1TestMod : IClientMod
    {
        public int KeyHits { get; private set; }

        public void Load(IClientContext context)
        {
            _ = context.Game.Screens.RegisterSlotButton(ScreenSlot.InventoryToolbar, "mod_btn", "Good", () => { });
            _ = context.Game.Input.OnKey(_ => KeyHits++);
            _ = context.Game.Input.Keybinds.Register("good.hotkey", "Good Key", KeyChord.FromKey('J', KeyModifiers.Alt), () => { });
            _ = context.Game.Toasts.Show("Good", "Loaded");
            context.Game.PreGame.CharacterCreating += ctx => ctx.SetOption("from_good", "1");
        }
    }

    private sealed class FaultingPhase1Mod : IClientMod
    {
        public void Load(IClientContext context)
        {
            _ = context.Game.Input.OnKey(_ => throw new InvalidOperationException("intentional fault"));
        }
    }
}
