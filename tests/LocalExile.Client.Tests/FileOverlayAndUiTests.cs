using System.Text;
using LocalExile.Client.Host;
using LocalExile.Client.Host.Native;
using LocalExile.Client.Sdk.Engine;
using Xunit;

namespace LocalExile.Client.Tests;

public sealed class FileOverlayAndUiTests
{
    private const string SampleGardenUi = """
        // Sample Garden UI layout
        <DEFINITIONS>

        const $w = 1100;

        begin Frame
            id = garden_window;
            size = $w, 420;
            visible = true;

            begin StackLayout
                id = body;

                begin Label
                    id = status;
                    text = "Initial status";
                    foreground_colour = Colours.White;
                end

                begin TextBox
                    id = seed_name;
                end

                begin CheckBox
                    id = auto_collect;
                    text = "Collect lifeforce automatically";
                end

                begin Frame
                    id = buttons;

                    begin Button
                        id = plant;
                        text = "Plant";
                        size = 180, 44;
                    end

                    begin Button based on plant
                        id = harvest;
                        text = "Harvest";
                    end
                end
            end
        end
        """;

    [Fact]
    public void FileOverlay_PrecedenceUtf16LeEncodingBundleAndInvalidation()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"nm-overlay-test-{Guid.NewGuid():N}");
        string looseDir = Path.Combine(tempDir, "loose");
        Directory.CreateDirectory(Path.Combine(looseDir, "metadata", "ui", "mods", "demo"));

        try
        {
            using var bridge = new SimulatedNativeBridge();
            bridge.AddStockTextFile("Metadata/UI/Mods/Demo/Panel.ui", "stock-content");
            using var host = new ClientHostRuntime(bridge);

            // 1. Stock fallback when no overlay exists.
            Assert.Equal("stock-content", host.FileOverlayService.ReadAllText("Metadata/UI/Mods/Demo/Panel.ui"));

            // 2. Packed bundle overlay (with Oodle stored-block prefix 0xCC 0x06) beats stock.
            byte[] bundlePayload = ClientTextEncoding.EncodeUtf16LeBomCrlf("bundle-content\nline2");
            byte[] storedBlock = [0xCC, 0x06, .. bundlePayload];
            string bundleBin = Path.Combine(tempDir, "demo.bundle.bin");
            string bundleManifest = Path.Combine(tempDir, "manifest.json");
            File.WriteAllBytes(bundleBin, storedBlock);
            File.WriteAllText(bundleManifest, $$"""
                {
                  "files": [
                    { "path": "Metadata/UI/Mods/Demo/Panel.ui", "offset": 0, "size": {{storedBlock.Length}} }
                  ]
                }
                """);
            using IDisposable bundleSub = host.FileOverlayService.RegisterBundle(bundleBin, bundleManifest);
            Assert.Equal("bundle-content\r\nline2", host.FileOverlayService.ReadAllText("Metadata/UI/Mods/Demo/Panel.ui"));

            // 3. Loose directory overlay beats bundle.
            File.WriteAllBytes(
                Path.Combine(looseDir, "metadata", "ui", "mods", "demo", "panel.ui"),
                ClientTextEncoding.EncodeUtf16LeBomCrlf("loose-content"));
            using IDisposable dirSub = host.FileOverlayService.RegisterDirectory(looseDir);
            Assert.Equal("loose-content", host.FileOverlayService.ReadAllText("Metadata/UI/Mods/Demo/Panel.ui"));

            // 4. Virtual text file beats loose directory AND auto-encodes .ui as UTF-16LE BOM CRLF!
            using IDisposable vfSub = host.FileOverlayService.RegisterVirtualTextFile("Metadata/UI/Mods/Demo/Panel.ui", "virtual-line1\nvirtual-line2");
            byte[]? rawBytes = host.FileOverlayService.ReadAllBytes("Metadata/UI/Mods/Demo/Panel.ui");
            Assert.NotNull(rawBytes);
            Assert.Equal(0xFF, rawBytes![0]);
            Assert.Equal(0xFE, rawBytes[1]);
            Assert.Equal("virtual-line1\r\nvirtual-line2", Encoding.Unicode.GetString(rawBytes, 2, rawBytes.Length - 2));

            // 5. Non-client extension (e.g. .json) stays UTF-8.
            using IDisposable jsonSub = host.FileOverlayService.RegisterVirtualTextFile("Mods/Demo/config.json", "{\"ok\":true}");
            byte[]? jsonBytes = host.FileOverlayService.ReadAllBytes("Mods/Demo/config.json");
            Assert.Equal("{\"ok\":true}", Encoding.UTF8.GetString(jsonBytes!));
            Assert.Contains("metadata/ui/mods/demo/panel.ui", bridge.KnownOverlayPaths);
            Assert.Contains("mods/demo/config.json", bridge.KnownOverlayPaths);

            // 6. Disposing virtual file falls back cleanly to loose directory.
            vfSub.Dispose();
            Assert.Equal("loose-content", host.FileOverlayService.ReadAllText("Metadata/UI/Mods/Demo/Panel.ui"));
            jsonSub.Dispose();
            Assert.DoesNotContain("mods/demo/config.json", bridge.KnownOverlayPaths);

            // 7. Invalidation forwards normalized path to native bridge.
            host.FileOverlayService.Invalidate("Metadata\\UI\\Mods\\Demo\\Panel.ui");
            Assert.Contains("metadata/ui/mods/demo/panel.ui", bridge.InvalidatedPaths);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void UiService_LoadsControlsDispatchesEventsReloadsAndRejectsDuplicateSiblingIds()
    {
        using var bridge = new SimulatedNativeBridge();
        using var host = new ClientHostRuntime(bridge);

        string uiPath = "Metadata/UI/Mods/Garden/Stockpile.ui";
        IDisposable overlay = host.FileOverlayService.RegisterVirtualTextFile(uiPath, SampleGardenUi);

        IUiWindow window = host.Ui.Load(uiPath);
        Assert.Equal("garden_window", window.Id);
        Assert.True(window.IsOpen);

        ILabel status = window.Require<ILabel>("body.status");
        ITextBox seedBox = window.Require<ITextBox>("body.seed_name");
        ICheckBox autoCollect = window.Require<ICheckBox>("body.auto_collect");
        IButton plant = window.Require<IButton>("body.buttons.plant");
        IButton harvest = window.Require<IButton>("body.buttons.harvest");

        Assert.Equal("Initial status", status.Text);
        // 'based on plant' inherited size property from plant!
        Assert.Equal("180, 44", harvest.GetProperty("size"));

        int clickCount = 0;
        string? submittedSeed = null;
        bool? checkedState = null;

        plant.Clicked += () => clickCount++;
        seedBox.Submitted += val => submittedSeed = val;
        autoCollect.CheckedChanged += val => checkedState = val;

        seedBox.Text = "Wild Thornfruit";
        seedBox.Submit();
        autoCollect.IsChecked = true;
        plant.Click();

        Assert.Equal(1, clickCount);
        Assert.Equal("Wild Thornfruit", submittedSeed);
        Assert.True(checkedState);

        // Reload picks up updated overlay text.
        overlay.Dispose();
        using IDisposable updatedOverlay = host.FileOverlayService.RegisterVirtualTextFile(
            uiPath,
            SampleGardenUi.Replace("Initial status", "Reloaded status", StringComparison.Ordinal));
        window.Reload();
        Assert.Equal("Reloaded status", window.Require<ILabel>("body.status").Text);

        bool closedFired = false;
        window.Closed += () => closedFired = true;
        window.Close();
        Assert.True(closedFired);
        Assert.False(window.IsOpen);
        Assert.Null(host.Ui.GetWindow("garden_window"));

        // Duplicate sibling IDs must throw FormatException (verified crash hazard from 07-spike-results.md!).
        string dupUi = """
            begin Frame
                id = root_dup;
                begin Label
                    id = same_id;
                end
                begin Label
                    id = same_id;
                end
            end
            """;
        using IDisposable dupOverlay = host.FileOverlayService.RegisterVirtualTextFile("Metadata/UI/Mods/Dup.ui", dupUi);
        Assert.Throws<FormatException>(() => host.Ui.Load("Metadata/UI/Mods/Dup.ui"));
    }

    private const string SampleSiphonPickerUi = """
        // Recombinator-style SiphonPicker.ui test layout
        <DEFINITIONS>

        begin Frame
            id = siphon_picker_window;
            size = 920, 640;
            visible = true;

            begin StackLayout
                id = body;

                begin Label
                    id = title_label;
                    text = "Orb of Siphoning";
                end

                begin Frame
                    id = stored_mod_box;
                    visible = false;

                    begin Label
                        id = stored_mod_label;
                        text = "";
                    end
                end

                begin StackLayout
                    id = affix_list;

                    begin Frame
                        id = affix_row_0;
                        visible = true;

                        begin Button
                            id = affix_btn_0;
                            text = "Select";
                        end

                        begin Label
                            id = affix_label_0;
                            text = "Affix 0";
                        end
                    end

                    begin Frame
                        id = affix_row_1;
                        visible = true;

                        begin Button
                            id = affix_btn_1;
                            text = "Select";
                        end

                        begin Label
                            id = affix_label_1;
                            text = "Affix 1";
                        end
                    end

                    begin Frame
                        id = affix_row_2;
                        visible = true;

                        begin Button
                            id = affix_btn_2;
                            text = "Select";
                        end

                        begin Label
                            id = affix_label_2;
                            text = "Affix 2";
                        end
                    end
                end

                begin Frame
                    id = actions;

                    begin Button
                        id = confirm_button;
                        text = "Confirm";
                    end

                    begin Button
                        id = cancel_button;
                        text = "Cancel";
                    end
                end
            end
        end
        """;

    [Fact]
    public void UiService_NativeBridge_SupportsDynamicRowVisibilityButtonLabelsAndMultiCycleWindowReuse()
    {
        using var bridge = new SimulatedNativeBridge { EnableSimulatedNativeUi = true };
        using var host = new ClientHostRuntime(bridge);

        const string uiPath = "Metadata/UI/Mods/Siphon/SiphonPicker.ui";
        using IDisposable overlay = host.FileOverlayService.RegisterVirtualTextFile(uiPath, SampleSiphonPickerUi);

        // --- Cycle 1: Steal mode on a 2-mod Magic item (rows 0-1 visible, row 2 hidden) ---
        IUiWindow stealWindow = host.Ui.Load(uiPath, "siphon_picker_window");
        SimulatedNativeBridge.SimulatedNativeUiControl? nativeRoot1 = bridge.GetSimulatedNativeUiControl(uiPath);
        Assert.NotNull(nativeRoot1);
        Assert.True(nativeRoot1!.Visible);

        IUiControl row0 = stealWindow.Require<IUiControl>("body.affix_list.affix_row_0");
        IUiControl row1 = stealWindow.Require<IUiControl>("body.affix_list.affix_row_1");
        IUiControl row2 = stealWindow.Require<IUiControl>("body.affix_list.affix_row_2");
        IButton btn0 = stealWindow.Require<IButton>("body.affix_list.affix_row_0.affix_btn_0");
        ILabel lbl0 = stealWindow.Require<ILabel>("body.affix_list.affix_row_0.affix_label_0");
        IButton btn2 = stealWindow.Require<IButton>("body.affix_list.affix_row_2.affix_btn_2");
        IButton confirm1 = stealWindow.Require<IButton>("body.actions.confirm_button");

        // Dynamic Label + Button text updates route with exact isButton flag.
        lbl0.Text = "[Prefix] +78 to Maximum Life";
        btn0.Text = "Selected";
        row2.Visible = false;

        var nativeLbl0 = bridge.GetSimulatedNativeUiControl(uiPath, "affix_label_0")!;
        var nativeBtn0 = bridge.GetSimulatedNativeUiControl(uiPath, "affix_btn_0")!;
        var nativeRow2 = bridge.GetSimulatedNativeUiControl(uiPath, "affix_row_2")!;
        var nativeBtn2 = bridge.GetSimulatedNativeUiControl(uiPath, "affix_btn_2")!;
        var nativeConfirm = bridge.GetSimulatedNativeUiControl(uiPath, "confirm_button")!;

        Assert.Equal("[Prefix] +78 to Maximum Life", nativeLbl0.Text);
        Assert.False(nativeLbl0.LastSetTextIsButton);
        Assert.Equal("Selected", nativeBtn0.Text);
        Assert.True(nativeBtn0.LastSetTextIsButton);
        Assert.False(nativeRow2.Visible);

        // Hidden parent row MUST block clicks on child buttons in both managed & native simulation!
        int cycle1Row0Clicks = 0;
        int cycle1Row2Clicks = 0;
        int cycle1ConfirmClicks = 0;
        btn0.Clicked += () => cycle1Row0Clicks++;
        btn2.Clicked += () => cycle1Row2Clicks++;
        confirm1.Clicked += () => cycle1ConfirmClicks++;

        btn2.Click();
        Assert.False(bridge.SimulateNativeUiClick(nativeBtn2.Handle));
        Assert.Equal(0, cycle1Row2Clicks);

        // Visible row 0 and confirm button fire normally through native click routing.
        Assert.True(bridge.SimulateNativeUiClick(nativeBtn0.Handle));
        Assert.True(bridge.SimulateNativeUiClick(nativeConfirm.Handle));
        Assert.Equal(1, cycle1Row0Clicks);
        Assert.Equal(1, cycle1ConfirmClicks);

        // Also verify SetProperty("text"/ "visible") syncs to native when bound.
        row1.SetProperty("visible", "false");
        Assert.False(bridge.GetSimulatedNativeUiControl(uiPath, "affix_row_1")!.Visible);

        // Close cycle 1 window: hides native root AND releases native click subscriptions immediately.
        stealWindow.Close();
        Assert.False(nativeRoot1.Visible);
        Assert.False(bridge.SimulateNativeUiClick(nativeConfirm.Handle));
        Assert.Equal(1, cycle1ConfirmClicks);

        // --- Cycle 2: Imbue mode reopens the exact same .ui on a 3-mod Rare item ---
        IUiWindow imbueWindow = host.Ui.Load(uiPath, "siphon_picker_window");
        SimulatedNativeBridge.SimulatedNativeUiControl? nativeRoot2 = bridge.GetSimulatedNativeUiControl(uiPath);
        Assert.NotNull(nativeRoot2);
        // Same native window handle reused (zero duplicate-ID reload crash!).
        Assert.Equal(nativeRoot1.Handle, nativeRoot2!.Handle);
        Assert.True(nativeRoot2.Visible);

        // Previously hidden rows & mutated text MUST be reset to parsed .ui defaults on reopen!
        Assert.True(bridge.GetSimulatedNativeUiControl(uiPath, "affix_row_1")!.Visible);
        Assert.True(bridge.GetSimulatedNativeUiControl(uiPath, "affix_row_2")!.Visible);
        Assert.Equal("Affix 0", nativeLbl0.Text);
        Assert.Equal("Select", nativeBtn0.Text);

        // Re-bind cycle 2 handlers: clicking Confirm or Row 2 fires ONLY cycle 2 (ZERO stale cycle 1 fires!).
        int cycle2Row2Clicks = 0;
        int cycle2ConfirmClicks = 0;
        imbueWindow.Require<IButton>("body.affix_list.affix_row_2.affix_btn_2").Clicked += () => cycle2Row2Clicks++;
        imbueWindow.Require<IButton>("body.actions.confirm_button").Clicked += () => cycle2ConfirmClicks++;

        Assert.True(bridge.SimulateNativeUiClick(nativeBtn2.Handle));
        Assert.True(bridge.SimulateNativeUiClick(nativeConfirm.Handle));
        Assert.Equal(0, cycle1Row2Clicks);
        Assert.Equal(1, cycle1ConfirmClicks);
        Assert.Equal(1, cycle2Row2Clicks);
        Assert.Equal(1, cycle2ConfirmClicks);

        // Reload() while open must also cleanly replace native subscriptions without double-firing.
        imbueWindow.Reload();
        int postReloadConfirmClicks = 0;
        imbueWindow.Require<IButton>("body.actions.confirm_button").Clicked += () => postReloadConfirmClicks++;
        Assert.True(bridge.SimulateNativeUiClick(nativeConfirm.Handle));
        Assert.Equal(1, cycle2ConfirmClicks);
        Assert.Equal(1, postReloadConfirmClicks);

        imbueWindow.Close();
        Assert.False(bridge.SimulateNativeUiClick(nativeConfirm.Handle));
        Assert.Equal(1, postReloadConfirmClicks);
    }
}
