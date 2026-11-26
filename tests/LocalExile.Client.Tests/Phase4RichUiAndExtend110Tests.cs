using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using LocalExile.Client.Host;
using LocalExile.Client.Host.Loading;
using LocalExile.Client.Host.Native;
using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;
using Xunit;

namespace LocalExile.Client.Tests;

[Collection("NativeEntryPointSerial")]
public sealed class Phase4RichUiAndExtend110Tests
{
    private static HoveredItemInfo CreateSampleItem(
        uint itemId = 1001u,
        string metadataPath = "Metadata/Items/Currency/CurrencyAddModToRare",
        string baseName = "Exalted Orb",
        ItemRarity rarity = ItemRarity.Normal,
        uint itemClassId = 14u) =>
        new(
            EntityHandle: 0x4001,
            ControlHandle: 0x5001,
            ItemId: itemId,
            InventoryId: 1u,
            GridX: 0,
            GridY: 0,
            Width: 1,
            Height: 1,
            MetadataPath: metadataPath,
            BaseName: baseName,
            StackSize: 1u,
            MaxStackSize: 20u,
            Rarity: rarity,
            IsIdentified: true,
            IsCorrupted: false,
            ItemLevel: 86,
            ItemClassId: itemClassId);

    [Fact]
    public void Sdk110Versioning_PreservesProtocol1AndBackwardCompatibility()
    {
        Assert.Equal("1.2.0", SdkVersion.Current);
        Assert.Equal(new Version(1, 2, 0), SdkVersion.Parsed);
        Assert.Equal(1, SdkVersion.ProtocolVersion);
        Assert.Equal(0x00000600u, NativeShimConstants.AbiVersion);
        Assert.Equal(0xA0, IRulesetRegistry.DefaultHostChannelRulesetId);
        Assert.Equal(0xAE, IRulesetRegistry.FirstFreeNativeRulesetId);

        Assert.True(SdkVersion.IsCompatible(">=0.2.0 <2.0.0"));
        Assert.True(SdkVersion.IsCompatible(">=0.3.0 <2.0.0"));
        Assert.True(SdkVersion.IsCompatible(">=0.4.0 <2.0.0"));
        Assert.True(SdkVersion.IsCompatible(">=1.0.0 <2.0.0"));
        Assert.True(SdkVersion.IsCompatible(">=1.1.0 <2.0.0"));
        Assert.True(SdkVersion.IsCompatible(">=1.2.0 <2.0.0"));
        Assert.True(SdkVersion.IsCompatible("^1.0.0"));
        Assert.True(SdkVersion.IsCompatible("^1.1.0"));
        Assert.True(SdkVersion.IsCompatible("^1.2.0"));
        Assert.True(SdkVersion.IsCompatible("~1.2.0"));
        Assert.False(SdkVersion.IsCompatible("~1.0.0"));
        Assert.False(SdkVersion.IsCompatible("~1.1.0"));
        Assert.False(SdkVersion.IsCompatible(">=2.0.0"));
    }

    [Fact]
    public void RichDynamicUiControls_AllKindsCreateInteractAndRoundTripNativeBridge()
    {
        using var bridge = new SimulatedNativeBridge { EnableSimulatedNativeUi = true };
        using var host = new ClientHostRuntime(bridge);

        using IDisposable uiFile = host.FileOverlayService.RegisterVirtualTextFile(
            "Metadata/UI/Mods/Phase4/HostPanel.ui",
            """
            begin Frame
                id = p4_host;
                size = 640, 480;
            end
            """);

        using IUiWindow win = host.Ui.Load("Metadata/UI/Mods/Phase4/HostPanel.ui", "p4_host");
        Assert.NotEqual(nint.Zero, win.NativeHandle);

        // Frame, Label, Button, TextBox, CheckBox, StackLayout
        IUiControl frame = host.Ui.CreateFrame(win, "dyn_frame");
        ILabel label = host.Ui.CreateLabel(frame, "Hello Phase 4", "dyn_label");
        IButton button = host.Ui.CreateButton(frame, "Apply", "dyn_btn");
        IUiControl textBox = host.Ui.CreateControl(frame, UiControlKind.TextBox, "dyn_tb");
        IUiControl checkBox = host.Ui.CreateControl(frame, UiControlKind.CheckBox, "dyn_cb");
        IUiControl stack = host.Ui.CreateControl(frame, UiControlKind.StackLayout, "dyn_stack");

        Assert.Equal("Hello Phase 4", label.Text);
        Assert.Equal("Apply", button.Text);
        Assert.IsAssignableFrom<ITextBox>(textBox);
        Assert.IsAssignableFrom<ICheckBox>(checkBox);
        Assert.Equal("StackLayout", stack.ControlType);

        int clicks = 0;
        button.Clicked += () => clicks++;
        button.Click();
        Assert.Equal(1, clicks);

        // Picture
        IPicture pic = host.Ui.CreatePicture(frame, "Art/2DArt/UIImages/InGame/Icon.dds", "dyn_pic");
        Assert.Equal("Art/2DArt/UIImages/InGame/Icon.dds", pic.TexturePath);
        pic.SetTexture("Art/2DArt/UIImages/InGame/AltIcon.dds", resizeToImage: true);
        Assert.Equal("Art/2DArt/UIImages/InGame/AltIcon.dds", pic.TexturePath);

        // ScrollableFrame
        IScrollableFrame scroll = host.Ui.CreateScrollableFrame(frame, "dyn_scroll");
        Assert.NotNull(scroll.ContentContainer);
        scroll.SetMaxScrollOffset(250f);
        scroll.SetScrollOffset(120f);
        Assert.Equal(250f, scroll.MaxScrollOffset);
        Assert.Equal(120f, scroll.ScrollOffset);
        scroll.SetScrollOffset(999f);
        Assert.Equal(250f, scroll.ScrollOffset);
        scroll.UpdateContentBounds();

        // TabContainer
        ITabContainer tabs = host.Ui.CreateTabContainer(frame, "dyn_tabs");
        List<int> tabEvents = [];
        tabs.ActiveTabChanged += tabEvents.Add;
        IUiControl tab0 = tabs.AddTab("Overview");
        IUiControl tab1 = tabs.AddTab("Affinities");
        Assert.Equal(2, tabs.TabCount);
        Assert.Equal(0, tabs.ActiveTabIndex);
        Assert.True(tab0.Visible);
        Assert.False(tab1.Visible);
        tabs.SelectTab(1);
        Assert.Equal(1, tabs.ActiveTabIndex);
        Assert.Equal([1], tabEvents);
        Assert.False(tab0.Visible);
        Assert.True(tab1.Visible);
        Assert.Throws<ArgumentOutOfRangeException>(() => tabs.SelectTab(5));
        tabs.ClearTabs();
        Assert.Equal(0, tabs.TabCount);
        Assert.Equal(-1, tabs.ActiveTabIndex);

        // ComboBox
        IComboBox combo = host.Ui.CreateComboBox(frame, "dyn_combo");
        List<int> comboEvents = [];
        combo.SelectionChanged += comboEvents.Add;
        Assert.Equal(0, combo.AddItem("Currency"));
        Assert.Equal(1, combo.AddItem("Maps"));
        Assert.Equal(2, combo.ItemCount);
        combo.SelectedIndex = 1;
        Assert.Equal(1, combo.SelectedIndex);
        Assert.Equal("Maps", combo.SelectedText);
        Assert.Equal([1], comboEvents);
        combo.ClearItems();
        Assert.Equal(0, combo.ItemCount);
        Assert.Equal(-1, combo.SelectedIndex);
        Assert.Equal([1, -1], comboEvents);

        // ListBox
        IListBox listBox = host.Ui.CreateListBox(frame, "dyn_list");
        List<int> listEvents = [];
        listBox.SelectionChanged += listEvents.Add;
        ILabel row0 = host.Ui.CreateLabel(frame, "Row 0", "row_0");
        ILabel row1 = host.Ui.CreateLabel(frame, "Row 1", "row_1");
        Assert.True(listBox.InsertItem(0, row0, userData: 0x101u));
        Assert.True(listBox.InsertItem(1, row1, userData: 0x202u));
        Assert.Equal(2, listBox.ItemCount);
        listBox.SelectedIndex = 1;
        Assert.Equal(1, listBox.SelectedIndex);
        Assert.Equal([1], listEvents);
        listBox.ClearSelection();
        Assert.Equal(-1, listBox.SelectedIndex);
        Assert.Equal([1, -1], listEvents);
        listBox.ClearItems();
        Assert.Equal(0, listBox.ItemCount);
    }

    [Fact]
    public void UiItemGrid_SupportsMultiCellOccupancyClicksAndHoverTooltips()
    {
        using var bridge = new SimulatedNativeBridge { EnableSimulatedNativeUi = true };
        using var host = new ClientHostRuntime(bridge);

        using IDisposable uiFile = host.FileOverlayService.RegisterVirtualTextFile(
            "Metadata/UI/Mods/Phase4/GridHost.ui",
            """
            begin Frame
                id = grid_host;
                size = 600, 400;
            end
            """);

        using IUiWindow win = host.Ui.Load("Metadata/UI/Mods/Phase4/GridHost.ui", "grid_host");
        using IUiItemGrid grid = host.Ui.CreateItemGrid(win, new UiItemGridOptions(
            Columns: 4,
            Rows: 3,
            CellSize: 48f,
            CellPadding: 2f,
            CellBackgroundTexture: "Art/2DArt/UIImages/InGame/CellBg.dds",
            ShowTooltipsOnHover: true), id: "idol_grid");

        Assert.Equal(4, grid.Columns);
        Assert.Equal(3, grid.Rows);
        Assert.Equal(48f, grid.CellSize);

        var largeIdol = new UiGridItem(
            ItemId: "idol_2x3",
            Col: 0,
            Row: 0,
            Width: 2,
            Height: 3,
            IconPath: "Art/2DItems/Idols/LargeIdol.dds",
            StackSize: 1,
            Rarity: ItemRarity.Unique,
            Title: "Totemic Idol",
            TooltipLines: [new TooltipLine("+12% Pack Size", "rgb(136, 136, 255)")],
            Tag: "large-idol-tag");

        Assert.True(grid.CanPlaceItem(0, 0, 2, 3));
        Assert.True(grid.PlaceItem(largeIdol));

        // All 6 footprint cells return the placed item.
        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 2; c++)
            {
                Assert.Same(largeIdol, grid.GetItemAt(c, r));
            }
        }

        // Overlapping placement, duplicate ItemId, and out-of-bounds placement are rejected.
        Assert.False(grid.CanPlaceItem(1, 1, 1, 1));
        Assert.False(grid.PlaceItem(new UiGridItem("overlap", Col: 1, Row: 2, Width: 1, Height: 1)));
        Assert.False(grid.PlaceItem(new UiGridItem("idol_2x3", Col: 2, Row: 0, Width: 1, Height: 1)));
        Assert.False(grid.PlaceItem(new UiGridItem("oob", Col: 3, Row: 0, Width: 2, Height: 1)));

        // FindFreeCell locates the first row-major open spot for a 2x2 item -> (2, 0).
        Assert.Equal((2, 0), grid.FindFreeCell(2, 2));

        var stackItem = new UiGridItem(
            ItemId: "scarab_stack",
            Col: 2,
            Row: 0,
            Width: 2,
            Height: 2,
            IconPath: "Art/2DItems/Currency/Scarab.dds",
            StackSize: 15,
            Rarity: ItemRarity.Rare,
            Title: "Ambush Scarab");
        Assert.True(grid.PlaceItem(stackItem));
        Assert.Equal(2, grid.Items.Count);
        Assert.Null(grid.FindFreeCell(2, 2));
        Assert.Equal((2, 2), grid.FindFreeCell(2, 1));

        // Events: left click, right click, ctrl+click, hover + automatic tooltip display.
        List<UiGridCellEventArgs> leftClicks = [];
        List<UiGridCellEventArgs> rightClicks = [];
        List<UiGridCellEventArgs> ctrlClicks = [];
        List<UiGridCellEventArgs> hovers = [];
        grid.CellClicked += leftClicks.Add;
        grid.CellRightClicked += rightClicks.Add;
        grid.CellCtrlClicked += ctrlClicks.Add;
        grid.CellHovered += hovers.Add;

        grid.TriggerCellClick(1, 2, MouseButton.Left, KeyModifiers.None);
        grid.TriggerCellClick(1, 2, MouseButton.Right, KeyModifiers.None);
        grid.TriggerCellClick(2, 1, MouseButton.Left, KeyModifiers.Control);

        Assert.Single(leftClicks);
        Assert.Equal("idol_2x3", leftClicks[0].Item!.ItemId);
        Assert.Single(rightClicks);
        Assert.Equal("idol_2x3", rightClicks[0].Item!.ItemId);
        Assert.Single(ctrlClicks);
        Assert.Equal("scarab_stack", ctrlClicks[0].Item!.ItemId);

        // Hover occupied cell -> shows tooltip; hover empty cell -> hides tooltip!
        grid.TriggerCellHover(0, 0);
        Assert.Single(hovers);
        Assert.True(host.Tooltips.IsCustomTooltipVisible);
        Assert.Equal("Totemic Idol", host.Tooltips.ActiveCustomTooltip!.Title);

        grid.TriggerCellHover(3, 2);
        Assert.False(host.Tooltips.IsCustomTooltipVisible);

        // RemoveItem frees footprint cells and allows new placement.
        Assert.True(grid.RemoveItem("idol_2x3"));
        Assert.Null(grid.GetItemAt(0, 0));
        Assert.Equal((0, 0), grid.FindFreeCell(2, 3));

        grid.Clear();
        Assert.Empty(grid.Items);
    }

    [Fact]
    public void TooltipService_SupportsStockAugmentationWithAltAndCustomAnchoredTooltips()
    {
        using var bridge = new SimulatedNativeBridge { EnableSimulatedNativeUi = true };
        using var host = new ClientHostRuntime(bridge);

        using IDisposable augLow = host.Tooltips.RegisterItemTooltipAugmenter(ctx =>
        [
            TooltipSection.SingleLine($"Priority 10 | Alt={ctx.IsAltHeld}", "rgb(200, 200, 255)", rgbaColor: 0xC8C8FFFFu),
        ], priority: 10);

        using IDisposable augHigh = host.Tooltips.RegisterItemTooltipAugmenter(ctx =>
        [
            TooltipSection.SingleLine($"Priority -5 | {ctx.Item.BaseName}", "rgb(255, 220, 120)", rgbaColor: 0xFFDC78FFu),
        ], priority: -5);

        using IDisposable augFaulty = host.Tooltips.RegisterItemTooltipAugmenter(_ =>
            throw new InvalidOperationException("boom"), priority: 0);

        HoveredItemInfo item = CreateSampleItem();
        IReadOnlyList<TooltipSection> normalSections = bridge.SimulateItemTooltipBuild(item, isAltHeld: false);
        Assert.Equal(2, normalSections.Count);
        Assert.Equal("Priority -5 | Exalted Orb", normalSections[0].Lines[0].Text);
        Assert.Equal("Priority 10 | Alt=False", normalSections[1].Lines[0].Text);

        IReadOnlyList<TooltipSection> altSections = bridge.SimulateItemTooltipBuild(item, isAltHeld: true);
        Assert.Equal(2, altSections.Count);
        Assert.Equal("Priority 10 | Alt=True", altSections[1].Lines[0].Text);

        // Direct ShowTooltip / HideTooltip on NativeMods-owned overlay.
        TooltipContent custom = TooltipContent.FromLines(
            "Custom Mod Header",
            [new TooltipLine("Line 1", "Colours.White"), new TooltipLine("Line 2", "rgb(100, 255, 100)")],
            ItemRarity.Unique);
        host.Tooltips.ShowTooltip(custom, 320f, 180f);
        Assert.True(host.Tooltips.IsCustomTooltipVisible);
        Assert.Equal((320f, 180f), host.Tooltips.CustomTooltipPosition);
        Assert.NotNull(host.Tooltips.OverlayWindow);
        Assert.True(host.Tooltips.OverlayWindow!.Visible);
        Assert.Equal("Custom Mod Header", host.Tooltips.OverlayWindow.Require<ILabel>("tooltip_title").Text);

        host.Tooltips.HideTooltip();
        Assert.False(host.Tooltips.IsCustomTooltipVisible);
        Assert.False(host.Tooltips.OverlayWindow.Visible);

        // AttachTooltip to a control + hover trigger + dispose cleanup.
        using IDisposable panelFile = host.FileOverlayService.RegisterVirtualTextFile(
            "Metadata/UI/Mods/Phase4/TipPanel.ui",
            """
            begin Frame
                id = tip_panel;
                size = 200, 100;
            end
            """);
        using IUiWindow win = host.Ui.Load("Metadata/UI/Mods/Phase4/TipPanel.ui", "tip_panel");
        IButton btn = host.Ui.CreateButton(win, "Hover Me", "hover_btn");
        btn.SetPosition(100f, 50f);
        btn.SetSize(80f, 30f);

        IDisposable attached = host.Tooltips.AttachTooltip(btn, () => TooltipContent.FromLines("Button Help", [new TooltipLine("Click to activate")]));
        host.Tooltips.NotifyControlHovered(btn);
        Assert.True(host.Tooltips.IsCustomTooltipVisible);
        Assert.Equal("Button Help", host.Tooltips.ActiveCustomTooltip!.Title);

        // Disposing the attachment hides the active tooltip automatically.
        attached.Dispose();
        Assert.False(host.Tooltips.IsCustomTooltipVisible);
    }

    [Fact]
    public void StashAffinityService_RoutesArbitraryTabIndicesAndStockBitsetsWithPriority()
    {
        using var bridge = new SimulatedNativeBridge();
        using var host = new ClientHostRuntime(bridge);

        using IDisposable routeLate = host.StashAffinity.RegisterRoute(
            "fallback_route",
            ctx => new StashAffinityRoute(TabIndex: 12, AffinityId: 0, ReplaceStockTargets: false),
            priority: 10);

        using IDisposable routeEarly = host.StashAffinity.RegisterRoute(
            "custom_idol_route",
            ctx => ctx.Item.MetadataPath.Contains("Idol", StringComparison.OrdinalIgnoreCase)
                ? new StashAffinityRoute(TabIndex: 47, AffinityId: 0, ReplaceStockTargets: true)
                : null,
            priority: -10);

        // Duplicate route id rejected.
        Assert.Throws<InvalidOperationException>(() =>
            host.StashAffinity.RegisterRoute("CUSTOM_IDOL_ROUTE", _ => null));

        // Stock affinity rules (0..21 valid; > 21 ignored safely).
        using IDisposable stockRuleA = host.StashAffinity.RegisterStockAffinityRule("currency_bit", _ => 0);
        using IDisposable stockRuleB = host.StashAffinity.RegisterStockAffinityRule("unique_bit", _ => 4);
        using IDisposable stockRuleOutOfRange = host.StashAffinity.RegisterStockAffinityRule("oob_bit", _ => 25);

        var idolCtx = new StashAffinityItemContext(
            CreateSampleItem(metadataPath: "Metadata/Items/Idols/SmallIdol", baseName: "Small Idol"),
            StashKind.Personal);

        IReadOnlyList<StashAffinityRoute> routes = bridge.SimulateStashAffinityRoute(idolCtx);
        Assert.Equal(2, routes.Count);
        Assert.Equal((ushort)47, routes[0].TabIndex);
        Assert.True(routes[0].ReplaceStockTargets);
        Assert.Equal((ushort)12, routes[1].TabIndex);

        uint bitmask = bridge.SimulateStashAffinityBitset(idolCtx);
        Assert.Equal((1u << 0) | (1u << 4), bitmask);

        routeEarly.Dispose();
        IReadOnlyList<StashAffinityRoute> afterDispose = bridge.SimulateStashAffinityRoute(idolCtx);
        Assert.Single(afterDispose);
        Assert.Equal((ushort)12, afterDispose[0].TabIndex);
    }

    [Fact]
    public void NativeExtendRegistries_WireGealRulesetCensusAndCustomUiKindsCleanly()
    {
        using var bridge = new SimulatedNativeBridge { EnableSimulatedNativeUi = true };
        using var host = new ClientHostRuntime(bridge);

        // Verify exact 3.29.3d ruleset slot census: 107 free / 68 occupied across 0x00..0xAE.
        int freeCount = 0;
        for (int i = 0; i <= IRulesetRegistry.MaxRulesetId; i++)
        {
            if (IRulesetRegistry.IsNativeSlotFree((byte)i))
            {
                freeCount++;
            }
        }

        Assert.Equal(107, freeCount);
        Assert.False(IRulesetRegistry.IsNativeSlotFree(0xA0));
        Assert.True(IRulesetRegistry.IsNativeSlotFree(0xAE));
        Assert.True(IRulesetRegistry.IsNativeSlotFree(0xAA));
        Assert.True(IRulesetRegistry.IsNativeSlotFree(0xA9));
        Assert.True(IRulesetRegistry.IsNativeSlotFree(0xA3));

        // 0xA0 (and all stock occupied slots via RegisterNative) MUST be rejected with a clear error pointing to 0xAE!
        InvalidOperationException exA0 = Assert.Throws<InvalidOperationException>(() =>
            host.Rulesets.Register(0xA0, () => new SampleNativeRuleset(0xA0)));
        Assert.Contains("0xA0", exA0.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0xAE", exA0.Message, StringComparison.OrdinalIgnoreCase);

        InvalidOperationException exA1Native = Assert.Throws<InvalidOperationException>(() =>
            host.Rulesets.RegisterNative(0xA1, () => new SampleNativeRuleset(0xA1)));
        Assert.Contains("0xA1", exA1Native.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0xAE", exA1Native.Message, StringComparison.OrdinalIgnoreCase);

        // Legacy 0.2.0..1.0.0 non-0xA0 occupied slots (e.g. 0xA4) fall back to 0x02C9 filter-hook WITHOUT touching creator_table!
        using (IDisposable legacySub = host.Rulesets.Register(0xA4, () => new SampleNativeRuleset(0xA4)))
        {
            Assert.DoesNotContain((byte)0xA4, bridge.RegisteredNativeRulesetIds);
            Assert.True(bridge.SimulateIncomingRulesetPacket(0xA4, new byte[] { 9, 9 }));
        }

        // Registering verified free slot 0xAE succeeds and wires native rulesets.
        SampleNativeRuleset? rs = null;
        using (IDisposable rsReg = host.Rulesets.RegisterNative(0xAE, () => rs = new SampleNativeRuleset(0xAE)))
        {
            Assert.Contains((byte)0xAE, bridge.RegisteredNativeRulesetIds);
            Assert.True(bridge.SimulateIncomingRulesetPacket(0xAE, new byte[] { 1, 2, 3 }));
            Assert.Equal(new byte[] { 1, 2, 3 }, rs!.LastPayload);
        }

        Assert.DoesNotContain((byte)0xAE, bridge.RegisteredNativeRulesetIds);

        // Native GEAL built-ins (both return-value and void).
        int voidCalls = 0;
        using (IDisposable fnReg = host.GealBuiltins.Register("ModAdd", (objId, args) => GealValue.FromInt(args.Int(0) + args.Int(1))))
        using (IDisposable voidReg = host.GealBuiltins.Register("ModPing", (objId, args) => { voidCalls++; }))
        {
            Assert.Contains("ModAdd", bridge.RegisteredNativeGealBuiltins);
            Assert.Contains("ModPing", bridge.RegisteredNativeGealBuiltins);

            GealInvocationResult addRes = bridge.SimulateNativeGealBuiltinCall("ModAdd", 99u, GealValue.FromInt(17), GealValue.FromInt(25));
            Assert.True(addRes.Succeeded);
            Assert.Equal(42, addRes.ReturnValue.IntValue);

            GealInvocationResult voidRes = bridge.SimulateNativeGealBuiltinCall("ModPing", 99u);
            Assert.True(voidRes.Succeeded);
            Assert.Equal(GealValueKind.Null, voidRes.ReturnValue.Kind);
            Assert.Equal(1, voidCalls);
        }

        Assert.DoesNotContain("ModAdd", bridge.RegisteredNativeGealBuiltins);
        Assert.DoesNotContain("ModPing", bridge.RegisteredNativeGealBuiltins);

        // Native custom UI control kind lifecycle (OnCreated -> OnProperty -> OnFrame -> OnDestroyed).
        SampleCustomControl? created = null;
        using (IDisposable kindReg = host.UiControls.Register("RuneDial", () => created = new SampleCustomControl()))
        {
            Assert.Contains("RuneDial", bridge.RegisteredNativeCustomUiControlKinds);
            nint handle = bridge.SimulateNativeCustomControlCreated("RuneDial", "dial_1");
            Assert.NotEqual(nint.Zero, handle);
            Assert.NotNull(created);
            Assert.True(created!.Initialized);
            Assert.Equal(1, host.UiControls.LiveNativeInstanceCount);

            Assert.True(bridge.SimulateNativeCustomControlProperty(handle, "charge", "0.85"));
            Assert.Equal("0.85", created.LastCharge);

            host.TickFrame(0.016f);
            Assert.Equal(1, created.Frames);

            Assert.True(bridge.SimulateNativeCustomControlDestroyed(handle));
            Assert.True(created.Destroyed);
            Assert.Equal(0, host.UiControls.LiveNativeInstanceCount);
        }

        Assert.DoesNotContain("RuneDial", bridge.RegisteredNativeCustomUiControlKinds);
    }

    [Fact]
    public async Task CollectibleAlcMod_Phase4ServicesCleanlyDisposeAndCollectOnUnload()
    {
        using var bridge = new SimulatedNativeBridge { EnableSimulatedNativeUi = true };
        using var host = new ClientHostRuntime(bridge, new ClientHostOptions { MaxModFaults = 2 });

        string tempDir = Path.Combine(Path.GetTempPath(), $"nm-p4-alc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string dllPath = Path.Combine(tempDir, "phase4-alc.Client.dll");
        try
        {
            EmitPhase4CollectibleModAssembly(dllPath, "Phase4CollectibleModAsm");

            var manifest = new ClientModManifest(
                Id: "phase4-alc",
                Name: "Phase 4 ALC Probe",
                Version: "1.1.0",
                SdkVersionRange: ">=1.1.0 <2.0.0",
                Assembly: "phase4-alc.Client.dll");

            LoadedClientMod loaded = host.Loader.LoadMod(manifest, tempDir, dllPath);
            Assert.NotNull(loaded.LoadContext);

            // Verify all Phase 4 registrations from the collectible mod are live!
            Assert.Contains("P4AlcBuiltin", host.GealBuiltins.RegisteredNames);
            Assert.Contains("P4AlcBuiltin", bridge.RegisteredNativeGealBuiltins);
            Assert.Single(bridge.SimulateItemTooltipBuild(CreateSampleItem(), isAltHeld: false));
            Assert.Single(bridge.SimulateStashAffinityRoute(new StashAffinityItemContext(CreateSampleItem())));
            Assert.True(host.Tooltips.IsCustomTooltipVisible);

            // Unload collectible mod -> ALL registrations torn down AND ALC collected!
            ModUnloadResult unload = await host.Loader.UnloadModAsync("phase4-alc");
            Assert.True(unload.WasLoaded);
            Assert.True(unload.UnloadedCleanly);
            Assert.DoesNotContain("P4AlcBuiltin", host.GealBuiltins.RegisteredNames);
            Assert.DoesNotContain("P4AlcBuiltin", bridge.RegisteredNativeGealBuiltins);
            Assert.Empty(bridge.SimulateItemTooltipBuild(CreateSampleItem(), isAltHeld: false));
            Assert.Empty(bridge.SimulateStashAffinityRoute(new StashAffinityItemContext(CreateSampleItem())));
            Assert.False(host.Tooltips.IsCustomTooltipVisible);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ScopedGameServices_Phase4FaultIsolationDisablesFaultyModWithoutAffectingHealthyMod()
    {
        using var bridge = new SimulatedNativeBridge();
        using var host = new ClientHostRuntime(bridge, new ClientHostOptions { MaxModFaults = 2 });

        var good = new Phase4FaultIsolationMod(shouldThrow: false);
        var bad = new Phase4FaultIsolationMod(shouldThrow: true);
        host.Loader.LoadInProcessMod(new ClientModManifest("p4-good", "Good", "1.1.0"), good);
        host.Loader.LoadInProcessMod(new ClientModManifest("p4-bad", "Bad", "1.1.0"), bad);

        HoveredItemInfo item = CreateSampleItem();
        for (int i = 0; i < 3; i++)
        {
            IReadOnlyList<TooltipSection> sections = bridge.SimulateItemTooltipBuild(item, isAltHeld: false);
            Assert.Single(sections);
            Assert.Equal("Healthy Mod Section", sections[0].Lines[0].Text);

            IReadOnlyList<StashAffinityRoute> routes = bridge.SimulateStashAffinityRoute(new StashAffinityItemContext(item));
            Assert.Single(routes);
            Assert.Equal((ushort)33, routes[0].TabIndex);
        }

        Assert.True(host.Loader.GetMod("p4-bad")!.IsDisabled);
        Assert.False(host.Loader.GetMod("p4-good")!.IsDisabled);
    }

    // ── Exact C ABI 0.5.0 Struct Sizes, Field Offsets & Slots 107..138 NativeShimBridge Round-Trip ──

    private static nint s_p4NextCtrl = 0x7000;
    private static string? s_p4LastPicturePath;
    private static float s_p4ScrollOffset;
    private static float s_p4ScrollMax = 400f;
    private static int s_p4TabCount;
    private static int s_p4ActiveTab = -1;
    private static unsafe delegate* unmanaged[Cdecl]<void*, int, void*, void> s_p4TabChangedCb;
    private static unsafe void* s_p4TabChangedUd;
    private static int s_p4ComboCount;
    private static int s_p4ComboSelected = -1;
    private static unsafe delegate* unmanaged[Cdecl]<void*, int, void*, void> s_p4ComboChangedCb;
    private static unsafe void* s_p4ComboChangedUd;
    private static int s_p4ListCount;
    private static int s_p4ListSelected = -1;
    private static ulong s_p4LastListUserData;
    private static unsafe delegate* unmanaged[Cdecl]<void*, int, void*, void> s_p4ListChangedCb;
    private static unsafe void* s_p4ListChangedUd;
    private static int s_p4UnsubscribeCount;
    private static unsafe delegate* unmanaged[Cdecl]<void*, NmItemSnapshot*, int, void*, void> s_p4TooltipCb;
    private static unsafe void* s_p4TooltipUd;
    private static readonly List<(string Text, uint Rgba, int AddSep)> s_p4InjectedTooltipSections = [];
    private static unsafe delegate* unmanaged[Cdecl]<NmItemSnapshot*, int, NmAffinityRouteResult*, void*, int> s_p4AffinityCb;
    private static unsafe void* s_p4AffinityUd;
    private static string? s_p4RegisteredGealName;
    private static unsafe delegate* unmanaged[Cdecl]<uint, byte*, byte*, nuint, nuint*, void*, NmStatus> s_p4GealCb;
    private static unsafe void* s_p4GealUd;
    private static int s_p4GealUnregCount;
    private static uint s_p4RegisteredRulesetId;
    private static NmNativeRulesetCallbacks s_p4RulesetCbs;
    private static unsafe void* s_p4RulesetUd;
    private static int s_p4RulesetUnregCount;
    private static string? s_p4CustomUiKindName;
    private static NmCustomUiControlCallbacks s_p4CustomUiCbs;
    private static unsafe void* s_p4CustomUiUd;
    private static int s_p4CustomUiUnregCount;

    [Fact]
    public unsafe void NativeShimBridge_Abi050StructSizesOffsetsAndAllSlots107To138RoundTrip()
    {
        // Exact C ABI 0.5.0/0.6.0 struct sizes and field offsets matching nativemods_abi.h (x64).
        Assert.Equal(0x00000600u, NativeShimConstants.AbiVersion);
        Assert.Equal(4, sizeof(NmAffinityTarget));
        Assert.Equal(32, sizeof(NmAffinityRouteResult));
        Assert.Equal(64, sizeof(NmNativeRulesetCallbacks));
        Assert.Equal(32, sizeof(NmCustomUiControlCallbacks));
        Assert.Equal(1168, sizeof(NmAbiTable));

        Assert.Equal(16, (int)Marshal.OffsetOf<NmAffinityRouteResult>(nameof(NmAffinityRouteResult.Target0)));
        Assert.Equal(8, (int)Marshal.OffsetOf<NmNativeRulesetCallbacks>(nameof(NmNativeRulesetCallbacks.OnCreated)));
        Assert.Equal(56, (int)Marshal.OffsetOf<NmNativeRulesetCallbacks>(nameof(NmNativeRulesetCallbacks.OnAreaLeft)));
        Assert.Equal(8, (int)Marshal.OffsetOf<NmCustomUiControlCallbacks>(nameof(NmCustomUiControlCallbacks.OnCreated)));
        Assert.Equal(24, (int)Marshal.OffsetOf<NmCustomUiControlCallbacks>(nameof(NmCustomUiControlCallbacks.OnDestroyed)));

        // Reset mock state.
        s_p4NextCtrl = 0x7000;
        s_p4LastPicturePath = null;
        s_p4ScrollOffset = 0f;
        s_p4ScrollMax = 400f;
        s_p4TabCount = 0;
        s_p4ActiveTab = -1;
        s_p4TabChangedCb = null;
        s_p4TabChangedUd = null;
        s_p4ComboCount = 0;
        s_p4ComboSelected = -1;
        s_p4ComboChangedCb = null;
        s_p4ComboChangedUd = null;
        s_p4ListCount = 0;
        s_p4ListSelected = -1;
        s_p4LastListUserData = 0;
        s_p4ListChangedCb = null;
        s_p4ListChangedUd = null;
        s_p4UnsubscribeCount = 0;
        s_p4TooltipCb = null;
        s_p4TooltipUd = null;
        s_p4InjectedTooltipSections.Clear();
        s_p4AffinityCb = null;
        s_p4AffinityUd = null;
        s_p4RegisteredGealName = null;
        s_p4GealCb = null;
        s_p4GealUd = null;
        s_p4GealUnregCount = 0;
        s_p4RegisteredRulesetId = 0;
        s_p4RulesetCbs = default;
        s_p4RulesetUd = null;
        s_p4RulesetUnregCount = 0;
        s_p4CustomUiKindName = null;
        s_p4CustomUiCbs = default;
        s_p4CustomUiUd = null;
        s_p4CustomUiUnregCount = 0;

        NmAbiTable table = default;
        table.StructSize = (uint)sizeof(NmAbiTable);
        table.AbiVersion = NativeShimConstants.AbiVersion;
        table.GetHostInfo = &Mock050GetHostInfo;
        table.UiUnsubscribe = &Mock050UiUnsubscribe;
        table.UiCreateControl = &Mock050UiCreateControl;
        table.UiPictureSetTexture = &Mock050UiPictureSetTexture;
        table.UiScrollableGetScroll = &Mock050UiScrollableGetScroll;
        table.UiScrollableSetScroll = &Mock050UiScrollableSetScroll;
        table.UiScrollableGetContent = &Mock050UiScrollableGetContent;
        table.UiTabContainerGetCount = &Mock050UiTabGetCount;
        table.UiTabContainerGetActive = &Mock050UiTabGetActive;
        table.UiTabContainerSelect = &Mock050UiTabSelect;
        table.UiTabContainerAddTab = &Mock050UiTabAddTab;
        table.UiTabContainerSubscribeChanged = &Mock050UiTabSubscribe;
        table.UiComboBoxGetCount = &Mock050UiComboGetCount;
        table.UiComboBoxGetSelected = &Mock050UiComboGetSelected;
        table.UiComboBoxSetSelected = &Mock050UiComboSetSelected;
        table.UiComboBoxAddItem = &Mock050UiComboAddItem;
        table.UiComboBoxClear = &Mock050UiComboClear;
        table.UiComboBoxSubscribeChanged = &Mock050UiComboSubscribe;
        table.UiListBoxGetCount = &Mock050UiListGetCount;
        table.UiListBoxGetSelected = &Mock050UiListGetSelected;
        table.UiListBoxSetSelected = &Mock050UiListSetSelected;
        table.UiListBoxInsertItem = &Mock050UiListInsertItem;
        table.UiListBoxClear = &Mock050UiListClear;
        table.UiListBoxSubscribeChanged = &Mock050UiListSubscribe;
        table.TooltipSetItemAugmentCallback = &Mock050TooltipSetCallback;
        table.TooltipAddItemSection = &Mock050TooltipAddSection;
        table.AffinitySetRouterCallback = &Mock050AffinitySetCallback;
        table.GealRegisterBuiltin = &Mock050GealRegisterBuiltin;
        table.GealUnregisterBuiltin = &Mock050GealUnregisterBuiltin;
        table.GealInvokeFunction = &Mock050GealInvokeFunction;
        table.RulesetRegisterNative = &Mock050RulesetRegisterNative;
        table.RulesetUnregisterNative = &Mock050RulesetUnregisterNative;
        table.UiRegisterCustomControlKind = &Mock050UiRegisterCustomKind;
        table.UiUnregisterCustomControlKind = &Mock050UiUnregisterCustomKind;

        using var bridge = new NativeShimBridge((nint)(&table));

        // 1. Rich UI Controls slots 107..128
        nint pic = bridge.CreateNativeUiControl(0x1000, UiControlKind.Picture, "pic0");
        Assert.NotEqual(nint.Zero, pic);
        bridge.SetNativeUiPictureTexture(pic, "Art/2DItems/Divination.dds");
        Assert.Equal("Art/2DItems/Divination.dds", s_p4LastPicturePath);

        nint scroll = bridge.CreateNativeUiControl(0x1000, UiControlKind.ScrollableFrame, "scroll0");
        bridge.SetNativeUiScrollOffset(scroll, 75.5f);
        Assert.True(bridge.TryGetNativeUiScrollState(scroll, out float off, out float maxOff));
        Assert.Equal(75.5f, off);
        Assert.Equal(400f, maxOff);

        nint tc = bridge.CreateNativeUiControl(0x1000, UiControlKind.TabContainer, "tc0");
        int observedTab = -1;
        using (IDisposable tabSub = bridge.SubscribeNativeUiTabChanged(tc, idx => observedTab = idx))
        {
            Assert.NotEqual(nint.Zero, bridge.AddNativeUiTab(tc, "Main"));
            Assert.NotEqual(nint.Zero, bridge.AddNativeUiTab(tc, "Extra"));
            bridge.SelectNativeUiTab(tc, 1);
            Assert.True(bridge.TryGetNativeUiTabState(tc, out int cnt, out int act));
            Assert.Equal(2, cnt);
            Assert.Equal(1, act);
            s_p4TabChangedCb((void*)tc, 1, s_p4TabChangedUd);
            Assert.Equal(1, observedTab);
        }

        nint cb = bridge.CreateNativeUiControl(0x1000, UiControlKind.ComboBox, "cb0");
        int observedCombo = -1;
        using (IDisposable comboSub = bridge.SubscribeNativeUiComboBoxSelectionChanged(cb, idx => observedCombo = idx))
        {
            Assert.Equal(0, bridge.AddNativeUiComboBoxItem(cb, "First"));
            Assert.Equal(1, bridge.AddNativeUiComboBoxItem(cb, "Second"));
            Assert.True(bridge.SetNativeUiComboBoxSelectedIndex(cb, 1));
            Assert.True(bridge.TryGetNativeUiComboBoxState(cb, out int cCnt, out int cSel, out _));
            Assert.Equal(2, cCnt);
            Assert.Equal(1, cSel);
            s_p4ComboChangedCb((void*)cb, 1, s_p4ComboChangedUd);
            Assert.Equal(1, observedCombo);
            bridge.ClearNativeUiComboBoxItems(cb);
            Assert.Equal(0, s_p4ComboCount);
        }

        nint lb = bridge.CreateNativeUiControl(0x1000, UiControlKind.ListBox, "lb0");
        int observedList = -1;
        using (IDisposable listSub = bridge.SubscribeNativeUiListBoxSelectionChanged(lb, idx => observedList = idx))
        {
            Assert.True(bridge.InsertNativeUiListBoxItem(lb, 0, pic, userData: 0xCAFEu));
            Assert.Equal(0xCAFEul, s_p4LastListUserData);
            Assert.True(bridge.SetNativeUiListBoxSelectedIndex(lb, 0));
            Assert.True(bridge.TryGetNativeUiListBoxState(lb, out int lCnt, out int lSel));
            Assert.Equal(1, lCnt);
            Assert.Equal(0, lSel);
            s_p4ListChangedCb((void*)lb, 0, s_p4ListChangedUd);
            Assert.Equal(0, observedList);
            bridge.ClearNativeUiListBoxItems(lb);
            Assert.Equal(0, s_p4ListCount);
        }

        Assert.Equal(3, s_p4UnsubscribeCount);

        // 2. Tooltip augmentation slots 129..130
        using (IDisposable tipSub = bridge.RegisterItemTooltipBuildCallback(ctx =>
        [
            new TooltipSection(
            [
                new TooltipLine($"Augmented:{ctx.Item.BaseName}:Alt={ctx.IsAltHeld}", RgbaColor: 0xFFAACCFFu),
                new TooltipLine("Second Line", RgbaColor: 0xFFFFFFFFu),
            ], PrependSeparator: true),
        ]))
        {
            NmItemSnapshot snap = CreateNativeItemSnapshot("Metadata/Items/Rings/Ring1", "Gold Ring");
            s_p4TooltipCb((void*)0x9999, &snap, 1, s_p4TooltipUd);
            Assert.Equal(2, s_p4InjectedTooltipSections.Count);
            Assert.Equal(("Augmented:Gold Ring:Alt=True", 0xFFAACCFFu, 1), s_p4InjectedTooltipSections[0]);
            Assert.Equal(("Second Line", 0xFFFFFFFFu, 0), s_p4InjectedTooltipSections[1]);
        }

        Assert.True(s_p4TooltipCb is null);

        // 3. Stash Affinity Router slot 131 (both stash_kind=-1 bitset and stash_kind=0 override)
        using (IDisposable routeSub = bridge.RegisterStashAffinityRouteCallback(_ => [new StashAffinityRoute(TabIndex: 55, AffinityId: 2, ReplaceStockTargets: true)]))
        using (IDisposable bitSub = bridge.RegisterStashAffinityBitsetCallback(_ => (1u << 3) | (1u << 7)))
        {
            NmItemSnapshot snap = CreateNativeItemSnapshot("Metadata/Items/Idols/TestIdol", "Test Idol");
            NmAffinityRouteResult bitRes = default;
            Assert.Equal(1, s_p4AffinityCb(&snap, -1, &bitRes, s_p4AffinityUd));
            Assert.Equal((1u << 3) | (1u << 7), bitRes.StockAffinityBitmask);

            NmAffinityRouteResult routeRes = default;
            Assert.Equal(1, s_p4AffinityCb(&snap, 0, &routeRes, s_p4AffinityUd));
            Assert.Equal((int)NmAffinityRouteAction.Override, routeRes.Action);
            Assert.Equal(1u, routeRes.TargetCount);
            Assert.Equal((ushort)55, routeRes.GetTarget(0).TabIndex);
            Assert.Equal((byte)2, routeRes.GetTarget(0).AffinityId);
        }

        Assert.True(s_p4AffinityCb is null);

        // 4. Native GEAL built-in + InvokeFunction slots 132..134
        using (IDisposable gealSub = bridge.RegisterNativeGealBuiltin("SumPair", hasReturnValue: true, (entityId, args) =>
            GealValue.FromInt(entityId + args.Int(0) + args.Int(1))))
        {
            Assert.Equal("SumPair", s_p4RegisteredGealName);
            byte[] argsJson = Encoding.UTF8.GetBytes("[10, 20]\0");
            byte[] outBuf = new byte[128];
            nuint outLen = 0;
            fixed (byte* pArgs = argsJson)
            fixed (byte* pOut = outBuf)
            {
                Assert.Equal(NmStatus.Ok, s_p4GealCb(100u, pArgs, pOut, (nuint)outBuf.Length, &outLen, s_p4GealUd));
            }

            Assert.Equal("130", Encoding.UTF8.GetString(outBuf, 0, (int)outLen));
        }

        Assert.Equal(1, s_p4GealUnregCount);

        GealInvocationResult invoked = bridge.InvokeGeal(77u, "CalcScore", isEvent: false, [GealValue.FromInt(5)]);
        Assert.True(invoked.Succeeded);
        Assert.Equal(9001, invoked.ReturnValue.IntValue);

        // 5. Native Ruleset slots 135..136
        Assert.True(bridge.DrivesNativeRulesetLifecycle);
        int rsEntered = 0;
        int rsFrames = 0;
        int rsLeft = 0;
        byte[]? rsPayload = null;
        using (IDisposable rsSub = bridge.RegisterNativeRuleset(
            0xAE,
            _ => rsEntered++,
            p => rsPayload = p.ToArray(),
            _ => rsFrames++,
            () => rsLeft++))
        {
            Assert.Equal(0xAEu, s_p4RegisteredRulesetId);
            s_p4RulesetCbs.OnAreaEntered(0xAEu, 42u, s_p4RulesetUd);
            s_p4RulesetCbs.OnFrame(0xAEu, 0.016f, s_p4RulesetUd);
            byte[] pkt = [0xAA, 0xBB];
            fixed (byte* pPkt = pkt)
            {
                s_p4RulesetCbs.OnRulesetUpdate(0xAEu, pPkt, 2u, s_p4RulesetUd);
            }

            s_p4RulesetCbs.OnAreaLeft(0xAEu, s_p4RulesetUd);
        }

        Assert.Equal(1, rsEntered);
        Assert.Equal(1, rsFrames);
        Assert.Equal(1, rsLeft);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, rsPayload);
        Assert.Equal(1, s_p4RulesetUnregCount);

        // 6. Native Custom UI Control Kind slots 137..138
        nint createdCtrl = nint.Zero;
        string? propPair = null;
        nint destroyedCtrl = nint.Zero;
        using (IDisposable kindSub = bridge.RegisterNativeCustomUiControlKind(
            "AuraGauge",
            h => createdCtrl = h,
            (h, k, v) => propPair = $"{k}={v}",
            h => destroyedCtrl = h))
        {
            Assert.Equal("AuraGauge", s_p4CustomUiKindName);
            byte[] kindBytes = Encoding.UTF8.GetBytes("AuraGauge\0");
            byte[] keyBytes = Encoding.UTF8.GetBytes("fill\0");
            byte[] valBytes = Encoding.UTF8.GetBytes("0.5\0");
            fixed (byte* pKind = kindBytes)
            fixed (byte* pKey = keyBytes)
            fixed (byte* pVal = valBytes)
            {
                s_p4CustomUiCbs.OnCreated(pKind, (void*)0x8888, s_p4CustomUiUd);
                s_p4CustomUiCbs.OnProperty((void*)0x8888, pKey, pVal, s_p4CustomUiUd);
                s_p4CustomUiCbs.OnDestroyed((void*)0x8888, s_p4CustomUiUd);
            }
        }

        Assert.Equal((nint)0x8888, createdCtrl);
        Assert.Equal("fill=0.5", propPair);
        Assert.Equal((nint)0x8888, destroyedCtrl);
        Assert.Equal(1, s_p4CustomUiUnregCount);
    }

    private static unsafe NmItemSnapshot CreateNativeItemSnapshot(string path, string baseName)
    {
        NmItemSnapshot snap = default;
        snap.Present = 1;
        snap.GridW = 1;
        snap.GridH = 1;
        snap.ItemId = 777u;
        snap.StackSize = 1u;
        snap.MaxStackSize = 1u;
        byte[] pathBytes = Encoding.UTF8.GetBytes(path);
        for (int i = 0; i < Math.Min(pathBytes.Length, 255); i++) snap.MetadataPath[i] = pathBytes[i];
        byte[] nameBytes = Encoding.UTF8.GetBytes(baseName);
        for (int i = 0; i < Math.Min(nameBytes.Length, 127); i++) snap.BaseName[i] = nameBytes[i];
        return snap;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock050GetHostInfo(NmHostInfo* outInfo)
    {
        outInfo->StructSize = (uint)sizeof(NmHostInfo);
        outInfo->AbiVersion = NativeShimConstants.AbiVersion;
        outInfo->BuildId = NmBuildId.Build3293d328228;
        outInfo->ImageBase = NativeShimConstants.NominalImageBase;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NmStatus Mock050UiUnsubscribe(ulong token)
    {
        s_p4UnsubscribeCount++;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock050UiCreateControl(int kind, void* parent, byte* idUtf8, void** outCtrl)
    {
        *outCtrl = (void*)(s_p4NextCtrl += 0x10);
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock050UiPictureSetTexture(void* pic, byte* pathUtf8)
    {
        s_p4LastPicturePath = Marshal.PtrToStringUTF8((nint)pathUtf8);
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock050UiScrollableGetScroll(void* ctrl, float* outOffset, float* outMax)
    {
        *outOffset = s_p4ScrollOffset;
        *outMax = s_p4ScrollMax;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock050UiScrollableSetScroll(void* ctrl, float offset)
    {
        s_p4ScrollOffset = offset;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock050UiScrollableGetContent(void* ctrl, void** outContent)
    {
        *outContent = ctrl;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock050UiTabGetCount(void* ctrl, uint* outCount)
    {
        *outCount = (uint)s_p4TabCount;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock050UiTabGetActive(void* ctrl, int* outIdx)
    {
        *outIdx = s_p4ActiveTab;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock050UiTabSelect(void* ctrl, int idx)
    {
        s_p4ActiveTab = idx;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock050UiTabAddTab(void* ctrl, byte* labelUtf8, uint style, int hidden, void** outPage)
    {
        s_p4TabCount++;
        *outPage = (void*)(s_p4NextCtrl += 0x10);
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock050UiTabSubscribe(void* ctrl, delegate* unmanaged[Cdecl]<void*, int, void*, void> cb, void* ud, ulong* outSub)
    {
        s_p4TabChangedCb = cb;
        s_p4TabChangedUd = ud;
        *outSub = 101ul;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock050UiComboGetCount(void* ctrl, uint* outCount)
    {
        *outCount = (uint)s_p4ComboCount;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock050UiComboGetSelected(void* ctrl, int* outIdx)
    {
        *outIdx = s_p4ComboSelected;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock050UiComboSetSelected(void* ctrl, int idx)
    {
        s_p4ComboSelected = idx;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock050UiComboAddItem(void* ctrl, byte* textUtf8, int enabled, uint tag, uint* outIdx)
    {
        *outIdx = (uint)s_p4ComboCount++;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock050UiComboClear(void* ctrl)
    {
        s_p4ComboCount = 0;
        s_p4ComboSelected = -1;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock050UiComboSubscribe(void* ctrl, delegate* unmanaged[Cdecl]<void*, int, void*, void> cb, void* ud, ulong* outSub)
    {
        s_p4ComboChangedCb = cb;
        s_p4ComboChangedUd = ud;
        *outSub = 102ul;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock050UiListGetCount(void* ctrl, uint* outCount)
    {
        *outCount = (uint)s_p4ListCount;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock050UiListGetSelected(void* ctrl, int* outIdx)
    {
        *outIdx = s_p4ListSelected;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock050UiListSetSelected(void* ctrl, int idx)
    {
        s_p4ListSelected = idx;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock050UiListInsertItem(void* ctrl, uint idx, void* child, ulong userData)
    {
        s_p4ListCount++;
        s_p4LastListUserData = userData;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock050UiListClear(void* ctrl)
    {
        s_p4ListCount = 0;
        s_p4ListSelected = -1;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock050UiListSubscribe(void* ctrl, delegate* unmanaged[Cdecl]<void*, int, void*, void> cb, void* ud, ulong* outSub)
    {
        s_p4ListChangedCb = cb;
        s_p4ListChangedUd = ud;
        *outSub = 103ul;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock050TooltipSetCallback(delegate* unmanaged[Cdecl]<void*, NmItemSnapshot*, int, void*, void> cb, void* ud)
    {
        s_p4TooltipCb = cb;
        s_p4TooltipUd = ud;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock050TooltipAddSection(void* popup, byte* textUtf8, uint rgba, int addSep)
    {
        s_p4InjectedTooltipSections.Add((Marshal.PtrToStringUTF8((nint)textUtf8) ?? string.Empty, rgba, addSep));
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock050AffinitySetCallback(delegate* unmanaged[Cdecl]<NmItemSnapshot*, int, NmAffinityRouteResult*, void*, int> cb, void* ud)
    {
        s_p4AffinityCb = cb;
        s_p4AffinityUd = ud;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock050GealRegisterBuiltin(byte* nameUtf8, uint minArgs, uint maxArgs, int hasRet, delegate* unmanaged[Cdecl]<uint, byte*, byte*, nuint, nuint*, void*, NmStatus> cb, void* ud, ulong* outHandle)
    {
        s_p4RegisteredGealName = Marshal.PtrToStringUTF8((nint)nameUtf8);
        s_p4GealCb = cb;
        s_p4GealUd = ud;
        *outHandle = 501ul;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NmStatus Mock050GealUnregisterBuiltin(ulong handle)
    {
        s_p4GealUnregCount++;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock050GealInvokeFunction(uint entityId, byte* fnUtf8, byte* argsJson, byte* outResult, nuint cap, nuint* outLen)
    {
        byte[] ret = Encoding.UTF8.GetBytes("9001");
        ret.CopyTo(new Span<byte>(outResult, ret.Length));
        outResult[ret.Length] = 0;
        *outLen = (nuint)ret.Length;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock050RulesetRegisterNative(uint rulesetId, NmNativeRulesetCallbacks* cbs, void* ud, ulong* outHandle)
    {
        s_p4RegisteredRulesetId = rulesetId;
        s_p4RulesetCbs = *cbs;
        s_p4RulesetUd = ud;
        *outHandle = 601ul;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NmStatus Mock050RulesetUnregisterNative(ulong handle)
    {
        s_p4RulesetUnregCount++;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NmStatus Mock050UiRegisterCustomKind(byte* kindUtf8, NmCustomUiControlCallbacks* cbs, void* ud, ulong* outHandle)
    {
        s_p4CustomUiKindName = Marshal.PtrToStringUTF8((nint)kindUtf8);
        s_p4CustomUiCbs = *cbs;
        s_p4CustomUiUd = ud;
        *outHandle = 701ul;
        return NmStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NmStatus Mock050UiUnregisterCustomKind(ulong handle)
    {
        s_p4CustomUiUnregCount++;
        return NmStatus.Ok;
    }

    private static void EmitPhase4CollectibleModAssembly(string outputPath, string assemblyName)
    {
        var ab = new PersistedAssemblyBuilder(new AssemblyName(assemblyName), typeof(object).Assembly);
        ModuleBuilder mb = ab.DefineDynamicModule(assemblyName);
        TypeBuilder tb = mb.DefineType("Phase4AlcMod", TypeAttributes.Public | TypeAttributes.Class, typeof(object), [typeof(IClientMod)]);
        tb.DefineDefaultConstructor(MethodAttributes.Public);

        // Helpers invoked as static delegates so the collectible ALC owns real delegate targets!
        MethodBuilder augMethod = tb.DefineMethod(
            "Augment",
            MethodAttributes.Private | MethodAttributes.Static,
            typeof(IReadOnlyList<TooltipSection>),
            [typeof(ItemTooltipContext)]);
        ILGenerator augIl = augMethod.GetILGenerator();
        augIl.Emit(OpCodes.Ldc_I4_1);
        augIl.Emit(OpCodes.Newarr, typeof(TooltipSection));
        augIl.Emit(OpCodes.Dup);
        augIl.Emit(OpCodes.Ldc_I4_0);
        augIl.Emit(OpCodes.Ldstr, "ALC Augmented");
        augIl.Emit(OpCodes.Ldnull);
        augIl.Emit(OpCodes.Ldc_I4_1);
        augIl.Emit(OpCodes.Ldc_I4_0);
        augIl.Emit(OpCodes.Call, typeof(TooltipSection).GetMethod(nameof(TooltipSection.SingleLine))!);
        augIl.Emit(OpCodes.Stelem_Ref);
        augIl.Emit(OpCodes.Ret);

        MethodBuilder routeMethod = tb.DefineMethod(
            "Route",
            MethodAttributes.Private | MethodAttributes.Static,
            typeof(StashAffinityRoute),
            [typeof(StashAffinityItemContext)]);
        ILGenerator routeIl = routeMethod.GetILGenerator();
        routeIl.Emit(OpCodes.Ldc_I4, 88);
        routeIl.Emit(OpCodes.Ldc_I4_0);
        routeIl.Emit(OpCodes.Ldc_I4_1);
        routeIl.Emit(OpCodes.Newobj, typeof(StashAffinityRoute).GetConstructor([typeof(ushort), typeof(byte), typeof(bool)])!);
        routeIl.Emit(OpCodes.Ret);

        MethodBuilder gealMethod = tb.DefineMethod(
            "GealFn",
            MethodAttributes.Private | MethodAttributes.Static,
            typeof(GealValue),
            [typeof(uint), typeof(GealArguments)]);
        ILGenerator gealIl = gealMethod.GetILGenerator();
        gealIl.Emit(OpCodes.Ldc_I8, 123L);
        gealIl.Emit(OpCodes.Call, typeof(GealValue).GetMethod(nameof(GealValue.FromInt))!);
        gealIl.Emit(OpCodes.Ret);

        // void Load(IClientContext context)
        MethodBuilder loadMethod = tb.DefineMethod(
            nameof(IClientMod.Load),
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final,
            typeof(void),
            [typeof(IClientContext)]);
        ILGenerator il = loadMethod.GetILGenerator();
        LocalBuilder gameLocal = il.DeclareLocal(typeof(IGameServices));

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, typeof(IClientContext).GetProperty(nameof(IClientContext.Game))!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, gameLocal);

        // game.Tooltips.RegisterItemTooltipAugmenter(Augment, 0);
        il.Emit(OpCodes.Ldloc, gameLocal);
        il.Emit(OpCodes.Callvirt, typeof(IGameServices).GetProperty(nameof(IGameServices.Tooltips))!.GetGetMethod()!);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldftn, augMethod);
        il.Emit(OpCodes.Newobj, typeof(Func<ItemTooltipContext, IReadOnlyList<TooltipSection>>).GetConstructor([typeof(object), typeof(nint)])!);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Callvirt, typeof(ITooltipService).GetMethod(nameof(ITooltipService.RegisterItemTooltipAugmenter))!);
        il.Emit(OpCodes.Pop);

        // game.Tooltips.ShowTooltip(new TooltipContent("ALC Tip", Array.Empty<TooltipSection>()), 10f, 20f);
        il.Emit(OpCodes.Ldloc, gameLocal);
        il.Emit(OpCodes.Callvirt, typeof(IGameServices).GetProperty(nameof(IGameServices.Tooltips))!.GetGetMethod()!);
        il.Emit(OpCodes.Ldstr, "ALC Tip");
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Newarr, typeof(TooltipSection));
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Newobj, typeof(TooltipContent).GetConstructor([typeof(string), typeof(IReadOnlyList<TooltipSection>), typeof(string), typeof(ItemRarity)])!);
        il.Emit(OpCodes.Ldc_R4, 10f);
        il.Emit(OpCodes.Ldc_R4, 20f);
        il.Emit(OpCodes.Callvirt, typeof(ITooltipService).GetMethod(nameof(ITooltipService.ShowTooltip))!);

        // game.StashAffinity.RegisterRoute("alc_route", Route, 0);
        il.Emit(OpCodes.Ldloc, gameLocal);
        il.Emit(OpCodes.Callvirt, typeof(IGameServices).GetProperty(nameof(IGameServices.StashAffinity))!.GetGetMethod()!);
        il.Emit(OpCodes.Ldstr, "alc_route");
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldftn, routeMethod);
        il.Emit(OpCodes.Newobj, typeof(Func<StashAffinityItemContext, StashAffinityRoute?>).GetConstructor([typeof(object), typeof(nint)])!);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Callvirt, typeof(IStashAffinityService).GetMethod(nameof(IStashAffinityService.RegisterRoute))!);
        il.Emit(OpCodes.Pop);

        // game.GealBuiltins.Register("P4AlcBuiltin", GealFn);
        il.Emit(OpCodes.Ldloc, gameLocal);
        il.Emit(OpCodes.Callvirt, typeof(IGameServices).GetProperty(nameof(IGameServices.GealBuiltins))!.GetGetMethod()!);
        il.Emit(OpCodes.Ldstr, "P4AlcBuiltin");
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldftn, gealMethod);
        il.Emit(OpCodes.Newobj, typeof(Func<uint, GealArguments, GealValue>).GetConstructor([typeof(object), typeof(nint)])!);
        il.Emit(OpCodes.Callvirt, typeof(IGealBuiltinRegistry).GetMethod(nameof(IGealBuiltinRegistry.Register), [typeof(string), typeof(Func<uint, GealArguments, GealValue>)])!);
        il.Emit(OpCodes.Pop);

        il.Emit(OpCodes.Ret);

        tb.CreateType();
        ab.Save(outputPath);
    }

    private sealed class SampleNativeRuleset(byte id) : IClientRuleset
    {
        public byte RulesetId => id;
        public string Name => $"Sample_{id:X2}";
        public byte[]? LastPayload { get; private set; }
        public void OnAreaEntered(AreaEventArgs area) { }
        public void OnRulesetUpdate(ReadOnlySpan<byte> payload) => LastPayload = payload.ToArray();
        public void OnFrame(float deltaSeconds) { }
        public void OnAreaLeft() { }
    }

    private sealed class SampleCustomControl : ICustomUiControl
    {
        public string ControlTypeName => "RuneDial";
        public bool Initialized { get; private set; }
        public bool Destroyed { get; private set; }
        public int Frames { get; private set; }
        public string? LastCharge { get; private set; }

        public void OnInitialize(IUiControl hostControl) => Initialized = true;
        public void OnPropertyChanged(string propertyName, string value)
        {
            if (propertyName == "charge") LastCharge = value;
        }
        public void OnFrame(float deltaSeconds) => Frames++;
        public void OnDestroy() => Destroyed = true;
    }

    private sealed class Phase4FaultIsolationMod(bool shouldThrow) : IClientMod
    {
        public void Load(IClientContext context)
        {
            context.Game.Tooltips.RegisterItemTooltipAugmenter(_ =>
            {
                if (shouldThrow) throw new InvalidOperationException("tooltip fault");
                return [TooltipSection.SingleLine("Healthy Mod Section")];
            });

            context.Game.StashAffinity.RegisterRoute(
                shouldThrow ? "bad_route" : "good_route",
                _ =>
                {
                    if (shouldThrow) throw new InvalidOperationException("affinity fault");
                    return new StashAffinityRoute(33);
                });
        }
    }
}
