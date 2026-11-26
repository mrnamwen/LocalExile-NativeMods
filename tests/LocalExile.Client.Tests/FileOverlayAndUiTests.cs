using System.Buffers.Binary;
using System.Security.Cryptography;
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

    [Fact]
    public void UiService_BasicButton_BindsNativeClicksWithoutCallingSetNativeUiText()
    {
        const string recombCardUi = """
            // Recombinator item-card BasicButton hit-test & captionless verification
            <DEFINITIONS>

            begin Frame
                id = recomb_card_win;
                size = 960, 520;
                visible = true;

                begin BasicButton
                    id = close_button;
                    location = 900, 10;
                    size = 41, 41;
                    mouse_enabled = true;
                end

                begin Frame
                    id = affix_row_0;
                    visible = true;

                    begin BasicButton
                        id = affix_btn_0;
                        size = 872, 52;
                        mouse_enabled = true;

                        begin Picture
                            id = affix_sel_bg_0;
                            source = "Art/2DArt/UIImages/InGame/CraftingWindow/MasterCraftingLesserRecipeSelected";
                            mouse_enabled = false;
                            visible = false;
                        end

                        begin Label
                            id = affix_label_0;
                            text = "+97 to maximum Life";
                            mouse_enabled = false;
                        end
                    end
                end

                begin Button
                    id = confirm_button;
                    text = "Siphon Modifier";
                end
            end
            """;

        using var bridge = new SimulatedNativeBridge { EnableSimulatedNativeUi = true };
        using var host = new ClientHostRuntime(bridge);
        const string uiPath = "Metadata/UI/Mods/Siphon/RecombCardTest.ui";
        using IDisposable overlay = host.FileOverlayService.RegisterVirtualTextFile(uiPath, recombCardUi);

        IUiWindow win = host.Ui.Load(uiPath, "recomb_card_win");
        IButton closeBtn = win.Require<IButton>("close_button");
        IButton rowBtn = win.Require<IButton>("affix_row_0.affix_btn_0");
        ILabel rowLabel = win.Require<ILabel>("affix_row_0.affix_btn_0.affix_label_0");
        IUiControl selBg = win.Require<IUiControl>("affix_row_0.affix_btn_0.affix_sel_bg_0");
        IButton confirmBtn = win.Require<IButton>("confirm_button");

        Assert.Equal("BasicButton", rowBtn.ControlType);
        Assert.Equal("BasicButton", closeBtn.ControlType);
        Assert.Equal("Button", confirmBtn.ControlType);
        Assert.False(selBg.Visible);

        var nativeRowBtn = bridge.GetSimulatedNativeUiControl(uiPath, "affix_btn_0")!;
        var nativeCloseBtn = bridge.GetSimulatedNativeUiControl(uiPath, "close_button")!;
        var nativeConfirmBtn = bridge.GetSimulatedNativeUiControl(uiPath, "confirm_button")!;
        var nativeSelBg = bridge.GetSimulatedNativeUiControl(uiPath, "affix_sel_bg_0")!;
        var nativeRowLbl = bridge.GetSimulatedNativeUiControl(uiPath, "affix_label_0")!;

        Assert.False(nativeSelBg.Visible);
        Assert.Equal(0, nativeRowBtn.SetTextCallCount);
        Assert.Equal(0, nativeCloseBtn.SetTextCallCount);
        Assert.Equal(1, nativeConfirmBtn.SetTextCallCount);
        Assert.Equal(1, nativeRowLbl.SetTextCallCount);

        // Updating managed Text on BasicButton must NOT call native SetNativeUiText (no inner label at +0x408).
        rowBtn.Text = "[SELECTED]";
        Assert.Equal("[SELECTED]", rowBtn.Text);
        Assert.Equal(0, nativeRowBtn.SetTextCallCount);

        // Native click routing still fires cleanly on BasicButton (+0x2A0 event list).
        int rowClicks = 0;
        int closeClicks = 0;
        rowBtn.Clicked += () =>
        {
            rowClicks++;
            selBg.Visible = true;
        };
        closeBtn.Clicked += () => closeClicks++;

        Assert.True(bridge.SimulateNativeUiClick(nativeRowBtn.Handle));
        Assert.True(bridge.SimulateNativeUiClick(nativeCloseBtn.Handle));
        Assert.Equal(1, rowClicks);
        Assert.Equal(1, closeClicks);
        Assert.True(nativeSelBg.Visible);

        // Hiding parent row blocks clicks on BasicButton just like standard Button.
        win.Require<IUiControl>("affix_row_0").Visible = false;
        Assert.False(bridge.SimulateNativeUiClick(nativeRowBtn.Handle));
        Assert.Equal(1, rowClicks);

        win.Close();
    }

    [Fact]
    public void FileOverlay_ReadsNmb1SingleAndMultiChunkBundlesAndRejectsCorruption()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"nm-nmb1-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            const int blockSize = 0x40000;

            // 1. 0-byte entry
            byte[] emptyPayload = [];
            // 2. Single-chunk entry whose length is 1740 (0x06CC LE = CC 06 00 00, proving chunkLen != raw marker)
            byte[] edgeLenPayload = Encoding.ASCII.GetBytes(new string('X', 1740));
            // 3. Multi-chunk binary entry (> 0x40000 bytes)
            byte[] multiChunkPayload = new byte[blockSize + 12345];
            new Random(99).NextBytes(multiChunkPayload);

            static byte[] EncodeNmb1Blocks(byte[] payload, int blkSize, out int blockCount)
            {
                using var ms = new MemoryStream();
                Span<byte> lenBuf = stackalloc byte[4];
                if (payload.Length == 0)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(lenBuf, 0);
                    ms.Write(lenBuf);
                    ms.Write([0xCC, 0x06]);
                    blockCount = 1;
                    return ms.ToArray();
                }

                blockCount = 0;
                int pos = 0;
                while (pos < payload.Length)
                {
                    int chunkLen = Math.Min(blkSize, payload.Length - pos);
                    BinaryPrimitives.WriteInt32LittleEndian(lenBuf, chunkLen);
                    ms.Write(lenBuf);
                    ms.Write([0xCC, 0x06]);
                    ms.Write(payload, pos, chunkLen);
                    pos += chunkLen;
                    blockCount++;
                }

                return ms.ToArray();
            }

            byte[] emptyBlocks = EncodeNmb1Blocks(emptyPayload, blockSize, out int emptyCount);
            byte[] edgeBlocks = EncodeNmb1Blocks(edgeLenPayload, blockSize, out int edgeCount);
            byte[] multiBlocks = EncodeNmb1Blocks(multiChunkPayload, blockSize, out int multiCount);

            using var bundleMs = new MemoryStream();
            Span<byte> header = stackalloc byte[16];
            "NMB1"u8.CopyTo(header[..4]);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..8], 1);
            BinaryPrimitives.WriteInt32LittleEndian(header[8..12], 3);
            BinaryPrimitives.WriteInt32LittleEndian(header[12..16], blockSize);
            bundleMs.Write(header);

            long offEmpty = bundleMs.Position;
            bundleMs.Write(emptyBlocks);
            long offEdge = bundleMs.Position;
            bundleMs.Write(edgeBlocks);
            long offMulti = bundleMs.Position;
            bundleMs.Write(multiBlocks);

            string bundlePath = Path.Combine(tempDir, "nmb1.bundle.bin");
            string manifestPath = Path.Combine(tempDir, "manifest.json");
            File.WriteAllBytes(bundlePath, bundleMs.ToArray());

            static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
            File.WriteAllText(manifestPath, $$"""
                {
                  "formatVersion": 1,
                  "blockSize": {{blockSize}},
                  "files": [
                    {
                      "virtualPath": "mods/demo/empty.bin",
                      "offset": {{offEmpty}},
                      "uncompressedSize": 0,
                      "storedSize": {{emptyBlocks.Length}},
                      "blockCount": {{emptyCount}},
                      "sha256": "{{Sha(emptyPayload)}}"
                    },
                    {
                      "virtualPath": "mods/demo/edge1740.txt",
                      "offset": {{offEdge}},
                      "uncompressedSize": {{edgeLenPayload.Length}},
                      "storedSize": {{edgeBlocks.Length}},
                      "blockCount": {{edgeCount}},
                      "sha256": "{{Sha(edgeLenPayload)}}"
                    },
                    {
                      "virtualPath": "art/2dart/mods/demo/atlas.dds",
                      "offset": {{offMulti}},
                      "uncompressedSize": {{multiChunkPayload.Length}},
                      "storedSize": {{multiBlocks.Length}},
                      "blockCount": {{multiCount}},
                      "sha256": "{{Sha(multiChunkPayload)}}"
                    }
                  ]
                }
                """);

            using var bridge = new SimulatedNativeBridge();
            using var host = new ClientHostRuntime(bridge);
            using IDisposable sub = host.FileOverlayService.RegisterBundle(bundlePath, manifestPath);

            Assert.Equal(emptyPayload, host.FileOverlayService.ReadAllBytes("mods/demo/empty.bin"));
            Assert.Equal(edgeLenPayload, host.FileOverlayService.ReadAllBytes("mods/demo/edge1740.txt"));
            Assert.Equal(new string('X', 1740), host.FileOverlayService.ReadAllText("mods/demo/edge1740.txt"));
            Assert.Equal(multiChunkPayload, host.FileOverlayService.ReadAllBytes("art/2dart/mods/demo/atlas.dds"));

            // Corrupt stored block marker must throw InvalidDataException
            byte[] badMarkerBundle = File.ReadAllBytes(bundlePath);
            badMarkerBundle[offEdge + 4] = 0xAA;
            string badMarkerPath = Path.Combine(tempDir, "bad-marker.bundle.bin");
            File.WriteAllBytes(badMarkerPath, badMarkerBundle);
            using (host.FileOverlayService.RegisterBundle(badMarkerPath, manifestPath, priority: 10))
            {
                Assert.Throws<InvalidDataException>(() => host.FileOverlayService.ReadAllBytes("mods/demo/edge1740.txt"));
            }

            // Corrupt NMB1 header magic must throw InvalidDataException
            byte[] badMagicBundle = File.ReadAllBytes(bundlePath);
            badMagicBundle[0] = (byte)'X';
            string badMagicPath = Path.Combine(tempDir, "bad-magic.bundle.bin");
            File.WriteAllBytes(badMagicPath, badMagicBundle);
            using (host.FileOverlayService.RegisterBundle(badMagicPath, manifestPath, priority: 10))
            {
                Assert.Throws<InvalidDataException>(() => host.FileOverlayService.ReadAllBytes("mods/demo/edge1740.txt"));
            }

            // Truncated bundle file must throw InvalidDataException
            byte[] truncatedBundle = File.ReadAllBytes(bundlePath)[..^100];
            string truncatedPath = Path.Combine(tempDir, "truncated.bundle.bin");
            File.WriteAllBytes(truncatedPath, truncatedBundle);
            using (host.FileOverlayService.RegisterBundle(truncatedPath, manifestPath, priority: 10))
            {
                Assert.Throws<InvalidDataException>(() => host.FileOverlayService.ReadAllBytes("art/2dart/mods/demo/atlas.dds"));
            }

            // Adversarial NMB1: headerBlockSize == int.MaxValue and chunkLen == int.MaxValue must throw InvalidDataException (no signed int overflow)
            using var overflowMs = new MemoryStream();
            Span<byte> overflowHdr = stackalloc byte[16];
            "NMB1"u8.CopyTo(overflowHdr[..4]);
            BinaryPrimitives.WriteInt32LittleEndian(overflowHdr[4..8], 1);
            BinaryPrimitives.WriteInt32LittleEndian(overflowHdr[8..12], 1);
            BinaryPrimitives.WriteInt32LittleEndian(overflowHdr[12..16], int.MaxValue);
            overflowMs.Write(overflowHdr);
            Span<byte> overflowBlkHdr = stackalloc byte[6];
            BinaryPrimitives.WriteInt32LittleEndian(overflowBlkHdr[..4], int.MaxValue);
            overflowBlkHdr[4] = 0xCC;
            overflowBlkHdr[5] = 0x06;
            overflowMs.Write(overflowBlkHdr);
            overflowMs.Write([0x41, 0x42, 0x43, 0x44]);

            string overflowBundlePath = Path.Combine(tempDir, "overflow.bundle.bin");
            string overflowManifestPath = Path.Combine(tempDir, "overflow.manifest.json");
            File.WriteAllBytes(overflowBundlePath, overflowMs.ToArray());
            File.WriteAllText(overflowManifestPath, $$"""
                {
                  "formatVersion": 1,
                  "blockSize": {{int.MaxValue}},
                  "files": [
                    {
                      "virtualPath": "mods/demo/overflow.bin",
                      "offset": 16,
                      "storedSize": 10
                    }
                  ]
                }
                """);
            using (host.FileOverlayService.RegisterBundle(overflowBundlePath, overflowManifestPath, priority: 20))
            {
                Assert.Throws<InvalidDataException>(() => host.FileOverlayService.ReadAllBytes("mods/demo/overflow.bin"));
            }

            // Adversarial NMB1: storedSize == 0 with omitted blockCount/uncompressedSize must throw InvalidDataException
            string zeroStoredManifestPath = Path.Combine(tempDir, "zero-stored.manifest.json");
            File.WriteAllText(zeroStoredManifestPath, $$"""
                {
                  "formatVersion": 1,
                  "blockSize": {{blockSize}},
                  "files": [
                    {
                      "virtualPath": "mods/demo/zero-stored.bin",
                      "offset": 16,
                      "storedSize": 0
                    }
                  ]
                }
                """);
            using (host.FileOverlayService.RegisterBundle(bundlePath, zeroStoredManifestPath, priority: 20))
            {
                Assert.Throws<InvalidDataException>(() => host.FileOverlayService.ReadAllBytes("mods/demo/zero-stored.bin"));
            }
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ClientTextEncoding_EncodesSmMatAmdWithoutBomAndPreservesBomOnUiOt()
    {
        foreach (string bomPath in new[]
        {
            "a.ui", "a.ot", "a.otc", "a.it", "a.itc", "a.ao", "a.aoc", "a.act", "a.epk", "a.ddt", "a.pet", "a.trl",
        })
        {
            Assert.True(ClientTextEncoding.RequiresUtf16LeBomCrlf(bomPath), bomPath);
            Assert.False(ClientTextEncoding.RequiresUtf16LeNoBomCrlf(bomPath), bomPath);
        }

        foreach (string noBomPath in new[] { "a.sm", "a.mat", "a.amd", "ART/MODELS/TEST.SM" })
        {
            Assert.False(ClientTextEncoding.RequiresUtf16LeBomCrlf(noBomPath), noBomPath);
            Assert.True(ClientTextEncoding.RequiresUtf16LeNoBomCrlf(noBomPath), noBomPath);
        }

        Assert.False(ClientTextEncoding.RequiresUtf16LeBomCrlf("config.json"));
        Assert.False(ClientTextEncoding.RequiresUtf16LeNoBomCrlf("config.json"));

        // Idempotent BOM stripping on both encoders
        byte[] bomEncoded = ClientTextEncoding.EncodeUtf16LeBomCrlf("\uFEFFline1\nline2\r\nline3\rline4");
        Assert.Equal(0xFF, bomEncoded[0]);
        Assert.Equal(0xFE, bomEncoded[1]);
        Assert.Equal("line1\r\nline2\r\nline3\r\nline4", Encoding.Unicode.GetString(bomEncoded, 2, bomEncoded.Length - 2));

        byte[] noBomEncoded = ClientTextEncoding.EncodeUtf16LeNoBomCrlf("\uFEFFline1\nline2\r\nline3\rline4");
        Assert.False(noBomEncoded[0] == 0xFF && noBomEncoded[1] == 0xFE);
        Assert.Equal("line1\r\nline2\r\nline3\r\nline4", Encoding.Unicode.GetString(noBomEncoded));

        // DecodeText handles FF FE, FE FF, EF BB BF, and BOM-less UTF-16LE/BE (even with leading U+0400)
        const string nonAsciiStart = "\u0400version 6\r\n";
        byte[] bomlessLe = new UnicodeEncoding(bigEndian: false, byteOrderMark: false).GetBytes(nonAsciiStart);
        byte[] bomlessBe = new UnicodeEncoding(bigEndian: true, byteOrderMark: false).GetBytes(nonAsciiStart);
        byte[] withBeBom = [0xFE, 0xFF, .. bomlessBe];
        byte[] withUtf8Bom = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("utf8-bom")];

        Assert.Equal(nonAsciiStart, ClientTextEncoding.DecodeText(bomlessLe));
        Assert.Equal(nonAsciiStart, ClientTextEncoding.DecodeText(bomlessBe));
        Assert.Equal(nonAsciiStart, ClientTextEncoding.DecodeText(withBeBom));
        Assert.Equal("utf8-bom", ClientTextEncoding.DecodeText(withUtf8Bom));

        // Virtual file registration emits BOM-less UTF-16LE CRLF for .sm/.mat/.amd and BOM for .ot
        using var bridge = new SimulatedNativeBridge();
        using var host = new ClientHostRuntime(bridge);

        using var smSub = host.FileOverlayService.RegisterVirtualTextFile("Art/Models/Mods/Cat/cat.sm", "version 6\nBoneGroups 0\n");
        using var matSub = host.FileOverlayService.RegisterVirtualTextFile("Art/Models/Mods/Cat/cat.mat", "{\"version\":4}");
        using var amdSub = host.FileOverlayService.RegisterVirtualTextFile("Art/Models/Mods/Cat/cat.amd", "version 5\nBoneGroups 0\n");
        using var otSub = host.FileOverlayService.RegisterVirtualTextFile("Metadata/Pet/Mods/Cat/Cat.ot", "version 3\nextends \"Metadata/Pet/Pet\"\n");

        foreach (string p in new[]
        {
            "Art/Models/Mods/Cat/cat.sm",
            "Art/Models/Mods/Cat/cat.mat",
            "Art/Models/Mods/Cat/cat.amd",
        })
        {
            byte[]? raw = host.FileOverlayService.ReadAllBytes(p);
            Assert.NotNull(raw);
            Assert.False(raw![0] == 0xFF && raw[1] == 0xFE, $"Unexpected BOM on {p}");
            Assert.Equal(0, raw.Length & 1);
            string? text = host.FileOverlayService.ReadAllText(p);
            Assert.NotNull(text);
            Assert.DoesNotContain('\0', text!);
        }

        Assert.Equal("version 6\r\nBoneGroups 0\r\n", host.FileOverlayService.ReadAllText("Art/Models/Mods/Cat/cat.sm"));
        Assert.Equal("{\"version\":4}", host.FileOverlayService.ReadAllText("Art/Models/Mods/Cat/cat.mat"));
        Assert.Equal("version 5\r\nBoneGroups 0\r\n", host.FileOverlayService.ReadAllText("Art/Models/Mods/Cat/cat.amd"));

        byte[]? otRaw = host.FileOverlayService.ReadAllBytes("Metadata/Pet/Mods/Cat/Cat.ot");
        Assert.NotNull(otRaw);
        Assert.Equal(0xFF, otRaw![0]);
        Assert.Equal(0xFE, otRaw[1]);
        Assert.Equal("version 3\r\nextends \"Metadata/Pet/Pet\"\r\n", host.FileOverlayService.ReadAllText("Metadata/Pet/Mods/Cat/Cat.ot"));

        // Double-BOM stripping across EncodeUtf16LeNoBomCrlf, EncodeUtf16LeBomCrlf, and RegisterVirtualTextFile
        const string doubleBomInput = "\uFEFF\uFEFFhello";
        byte[] doubleBomNoBom = ClientTextEncoding.EncodeUtf16LeNoBomCrlf(doubleBomInput);
        Assert.False(doubleBomNoBom[0] == 0xFF && doubleBomNoBom[1] == 0xFE);
        Assert.Equal("hello", ClientTextEncoding.DecodeText(doubleBomNoBom));

        byte[] doubleBomWithBom = ClientTextEncoding.EncodeUtf16LeBomCrlf(doubleBomInput);
        Assert.Equal(0xFF, doubleBomWithBom[0]);
        Assert.Equal(0xFE, doubleBomWithBom[1]);
        Assert.Equal("hello", ClientTextEncoding.DecodeText(doubleBomWithBom));

        foreach (string p in new[]
        {
            "Art/Models/Mods/Cat/doublebom.sm",
            "Art/Models/Mods/Cat/doublebom.mat",
            "Art/Models/Mods/Cat/doublebom.amd",
        })
        {
            using var sub = host.FileOverlayService.RegisterVirtualTextFile(p, doubleBomInput);
            byte[]? raw = host.FileOverlayService.ReadAllBytes(p);
            Assert.NotNull(raw);
            Assert.False(raw![0] == 0xFF && raw[1] == 0xFE, $"Unexpected BOM on {p}");
            Assert.Equal("hello", host.FileOverlayService.ReadAllText(p));
        }

        // Standalone / predominant BOM-less UTF-16LE with 0x00 low bytes (U+0400) vs genuine UTF-16BE
        Encoding utf16LeNoBomEnc = new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
        Encoding utf16BeNoBomEnc = new UnicodeEncoding(bigEndian: true, byteOrderMark: false);
        Assert.Equal("\u0400", ClientTextEncoding.DecodeText(utf16LeNoBomEnc.GetBytes("\u0400")));
        Assert.Equal("\u0400\u0400a", ClientTextEncoding.DecodeText(utf16LeNoBomEnc.GetBytes("\u0400\u0400a")));
        Assert.Equal("hello\r\n", ClientTextEncoding.DecodeText(utf16BeNoBomEnc.GetBytes("hello\r\n")));
    }

    [Fact]
    public void FileOverlay_RejectsInvalidNmb1UncompressedSizeBeforeAllocation()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "nm-nmb1-uncompressed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            const int blockSize = 0x40000;
            using var bundleMs = new MemoryStream();
            Span<byte> hdr = stackalloc byte[16];
            "NMB1"u8.CopyTo(hdr[..4]);
            BinaryPrimitives.WriteInt32LittleEndian(hdr[4..8], 1);
            BinaryPrimitives.WriteInt32LittleEndian(hdr[8..12], 1);
            BinaryPrimitives.WriteInt32LittleEndian(hdr[12..16], blockSize);
            bundleMs.Write(hdr);

            Span<byte> blkHdr = stackalloc byte[6];
            BinaryPrimitives.WriteInt32LittleEndian(blkHdr[..4], 4);
            blkHdr[4] = 0xCC;
            blkHdr[5] = 0x06;
            bundleMs.Write(blkHdr);
            bundleMs.Write([0x41, 0x42, 0x43, 0x44]);

            string bundlePath = Path.Combine(tempDir, "slice.bundle.bin");
            File.WriteAllBytes(bundlePath, bundleMs.ToArray());

            using var bridge = new SimulatedNativeBridge();
            using var host = new ClientHostRuntime(bridge);

            foreach ((string name, int uncompressedSize) in new[] { ("intmax", int.MaxValue), ("negone", -1) })
            {
                string manifestPath = Path.Combine(tempDir, $"{name}.manifest.json");
                File.WriteAllText(manifestPath, $$"""
                    {
                      "formatVersion": 1,
                      "blockSize": {{blockSize}},
                      "files": [
                        {
                          "virtualPath": "mods/demo/{{name}}.bin",
                          "offset": 16,
                          "uncompressedSize": {{uncompressedSize}},
                          "storedSize": 10,
                          "blockCount": 1
                        }
                      ]
                    }
                    """);
                using (host.FileOverlayService.RegisterBundle(bundlePath, manifestPath, priority: 10))
                {
                    Assert.Throws<InvalidDataException>(() => host.FileOverlayService.ReadAllBytes($"mods/demo/{name}.bin"));
                }
            }
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
