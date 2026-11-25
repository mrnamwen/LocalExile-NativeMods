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

            // 6. Disposing virtual file falls back cleanly to loose directory.
            vfSub.Dispose();
            Assert.Equal("loose-content", host.FileOverlayService.ReadAllText("Metadata/UI/Mods/Demo/Panel.ui"));

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
}
