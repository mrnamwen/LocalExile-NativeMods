using LocalExile.Client.Waypoint;
using Xunit;

namespace LocalExile.Client.Waypoint.Tests;

public sealed class UiLayoutLinterAndValidatorTests
{
    [Fact]
    public void UiLinter_CatchesLeadingDefinitionsWithoutWhitespace()
    {
        const string ui = """
<DEFINITIONS>
begin Frame
    id = test_frame;
    size = 100, 100;
end
""";
        var diags = UiLayoutLinter.Lint(ui, "test.ui");
        Assert.Contains(diags, d => d.Severity == DiagnosticSeverity.Error && d.Code == "UI002");
    }

    [Fact]
    public void UiLinter_CatchesDuplicateSiblingIds()
    {
        const string ui = """
// Valid header comment
<DEFINITIONS>
begin Frame
    id = root_window;
    begin Button
        id = action_btn;
        text = "First";
    end
    begin Button
        id = action_btn;
        text = "Second";
    end
end
""";
        var diags = UiLayoutLinter.Lint(ui, "dup.ui");
        Assert.Contains(diags, d => d.Severity == DiagnosticSeverity.Error && d.Code == "UI016");
    }

    [Fact]
    public void UiLinter_CatchesPictureWithMarginInsideStackLayoutInfiniteSpinHazard()
    {
        // Verified client hang from 07-spike-results.md
        const string ui = """
// Hang hazard test
<DEFINITIONS>
begin Frame
    id = win;
    begin StackLayout
        id = body;
        begin Picture
            id = bad_divider;
            source = "Art/2DArt/UIImages/InGame/Divider";
            margin = 0, 0, 0, 12;
        end
    end
end
""";
        var diags = UiLayoutLinter.Lint(ui, "hang.ui");
        Assert.Contains(diags, d => d.Severity == DiagnosticSeverity.Error && d.Code == "UI008");
    }

    [Fact]
    public void UiLinter_CatchesUnknownControlUnknownPropertyAndUnbalancedEnd()
    {
        const string ui = """
// Broken layout
<DEFINITIONS>
begin Frame
    id = win;
    nonexistent_prop = 42;
    begin FakeControlType
        id = child;
    end
""";
        var diags = UiLayoutLinter.Lint(ui, "broken.ui");
        Assert.Contains(diags, d => d.Severity == DiagnosticSeverity.Error && d.Code == "UI013"); // unknown prop
        Assert.Contains(diags, d => d.Severity == DiagnosticSeverity.Error && d.Code == "UI006"); // unknown control
        Assert.Contains(diags, d => d.Severity == DiagnosticSeverity.Error && d.Code == "UI007"); // unclosed Frame
    }

    [Fact]
    public void UiLinter_AcceptsCustomDeclaredControlAndVerifiedSpikeLayout()
    {
        string repoRoot = FindRepoRoot();
        string spikeUi = Path.Combine(repoRoot, "spikes", "overlay-src", "metadata", "ui", "mods", "spike", "garden.ui");
        Assert.True(File.Exists(spikeUi), $"Expected spike UI at {spikeUi}");

        var spikeDiags = UiLayoutLinter.Lint(File.ReadAllText(spikeUi), spikeUi);
        Assert.DoesNotContain(spikeDiags, d => d.Severity == DiagnosticSeverity.Error);

        string harvestUi = Path.Combine(repoRoot, "examples", "DualMod.HarvestGarden", "client", "Metadata", "UI", "Mods", "Garden", "Stockpile.ui");
        Assert.True(File.Exists(harvestUi), $"Expected HarvestGarden Stockpile.ui at {harvestUi}");

        // Without declaring GrowthGauge it must error; WITH declaring GrowthGauge it must pass cleanly
        var withoutCustom = UiLayoutLinter.Lint(File.ReadAllText(harvestUi), harvestUi);
        Assert.Contains(withoutCustom, d => d.Severity == DiagnosticSeverity.Error && d.Code == "UI006");

        var withCustom = UiLayoutLinter.Lint(File.ReadAllText(harvestUi), harvestUi, customControls: ["GrowthGauge"]);
        Assert.DoesNotContain(withCustom, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ValidatePackageDirectory_ValidatesRealHarvestGardenExampleCleanly()
    {
        string harvestDir = Path.Combine(FindRepoRoot(), "examples", "DualMod.HarvestGarden");
        var report = DualModPackageValidator.ValidatePackageDirectory(harvestDir, SemVer.Parse("0.1.0-poc"));

        Assert.True(report.IsValid, string.Join(Environment.NewLine, report.Errors.Select(e => $"{e.Code}: {e.Message}")));
    }

    [Fact]
    public void ValidatePackageDirectory_EnforcesNamespaceAndDeclaredReplacesRules()
    {
        string temp = Path.Combine(Path.GetTempPath(), "nm-val-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            // Package places a file in stock path Metadata/UI/InGameState/EscapeMenu.ui WITHOUT declaring it in replaces,
            // AND declares a replace that does not exist on disk!
            var manifest = new DualModManifest
            {
                Id = "sneaky-mod",
                Name = "Sneaky Mod",
                Version = "1.0.0",
                CompatibleClientVersions = ["0.5.2-beta"],
                NativeClient = new NativeClientManifest
                {
                    SdkVersionRange = ">=0.1.0-poc <2.0.0",
                    PayloadDir = "client",
                    Replaces = ["Metadata/UI/InGameState/ConfirmationWindow.ui"]
                }
            };
            File.WriteAllText(Path.Combine(temp, "mod.json"), manifest.ToJson());
            string stockOverride = Path.Combine(temp, "client", "Metadata", "UI", "InGameState", "EscapeMenu.ui");
            Directory.CreateDirectory(Path.GetDirectoryName(stockOverride)!);
            File.WriteAllText(stockOverride, "// override\n<DEFINITIONS>\nbegin Frame\n id = f;\nend\n");

            var report = DualModPackageValidator.ValidatePackageDirectory(temp, SemVer.Parse("0.1.0-poc"));

            Assert.False(report.IsValid);
            Assert.Contains(report.Errors, d => d.Code == "PAY003"); // undeclared stock path
            Assert.Contains(report.Errors, d => d.Code == "PAY005"); // declared replace missing on disk
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void ValidatePackageDirectory_VerifiesSha256IntegrityAndUtf16LeUiFiles()
    {
        string temp = Path.Combine(Path.GetTempPath(), "nm-int-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string uiRel = "client/Metadata/UI/Mods/Integrity/Panel.ui";
            string uiFull = Path.Combine(temp, "client", "Metadata", "UI", "Mods", "Integrity", "Panel.ui");
            Directory.CreateDirectory(Path.GetDirectoryName(uiFull)!);
            File.WriteAllBytes(uiFull, ModBundlePacker.EncodeClientText("// utf16\n<DEFINITIONS>\nbegin Frame\n\tid = ok_panel;\nend\n"));

            string asmPath = Path.Combine(temp, "integrity.Client.dll");
            File.WriteAllBytes(asmPath, "CLIENT_DLL_BYTES"u8.ToArray());

            var manifest = new DualModManifest
            {
                Id = "integrity",
                Name = "Integrity Test",
                Version = "1.0.0",
                CompatibleClientVersions = ["0.5.2-beta"],
                NativeClient = new NativeClientManifest
                {
                    SdkVersionRange = ">=0.1.0-poc <2.0.0",
                    Assembly = "integrity.Client.dll",
                    AssemblySha256 = new string('0', 64), // deliberate mismatch
                    PayloadDir = "client",
                    Overlays = ["Metadata/UI/Mods/Integrity/Panel.ui", "Metadata/UI/Mods/Integrity/Missing.ui"],
                    Integrity = new PackageIntegrity(Files: new Dictionary<string, string>
                    {
                        [uiRel] = new string('a', 64) // deliberate mismatch
                    })
                }
            };
            File.WriteAllText(Path.Combine(temp, "mod.json"), manifest.ToJson());

            var report = DualModPackageValidator.ValidatePackageDirectory(temp, SemVer.Parse("0.1.0-poc"));

            Assert.False(report.IsValid);
            Assert.Contains(report.Errors, d => d.Code == "INT001"); // assembly SHA-256 mismatch
            Assert.Contains(report.Errors, d => d.Code == "INT005"); // file SHA-256 mismatch
            Assert.Contains(report.Errors, d => d.Code == "PAY006"); // missing declared overlay
            // UTF-16LE UI file itself must lint with zero UI errors
            Assert.DoesNotContain(report.Errors, d => d.Code.StartsWith("UI", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "02-architecture.md")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("Could not locate NativeMods repository root.");
    }
}
