using LocalExile.Client.Waypoint;
using Xunit;

namespace LocalExile.Client.Waypoint.Tests;

public sealed class DualModManifestAndSemVerTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.0", true)]
    [InlineData("0.1.0-poc", ">=0.1.0-poc <2.0.0", true)]
    [InlineData("0.1.0", ">=0.1.0-poc <2.0.0", true)]
    [InlineData("1.5.2", ">=0.1.0-poc <2.0.0", true)]
    [InlineData("2.0.0", ">=0.1.0-poc <2.0.0", false)]
    [InlineData("0.0.9", ">=0.1.0-poc <2.0.0", false)]
    [InlineData("1.4.0", "^1.2.0", true)]
    [InlineData("2.0.0", "^1.2.0", false)]
    [InlineData("1.2.9", "~1.2.0", true)]
    [InlineData("1.3.0", "~1.2.0", false)]
    [InlineData("0.2.0", "^0.1.0 || ^0.2.0", true)]
    public void SemVerRange_EvaluatesCompatibilityAccurately(string version, string rangeExpr, bool expected)
    {
        var ver = SemVer.Parse(version);
        var range = SemVerRange.Parse(rangeExpr);

        Assert.Equal(expected, range.IsSatisfiedBy(ver));
    }

    [Fact]
    public void DualModManifest_ParsesStandardLocalExileModJsonWithoutNativeClient()
    {
        // Mirrors real LocalExile-Mods/mods/pob/mod.json
        const string json = """
        {
          "id": "pob",
          "name": "POB",
          "description": "Preview and import a character build through a configured external PoB converter.",
          "author": "nikolos89",
          "version": "1.0.4",
          "compatibleClientVersions": ["0.4.3-beta", "0.5.2-beta"],
          "kind": "feature",
          "depends": [],
          "assembly": "pob.dll",
          "enabled": true
        }
        """;

        var manifest = DualModManifest.Parse(json);

        Assert.Equal("pob", manifest.Id);
        Assert.Equal("POB", manifest.DisplayName);
        Assert.Equal("1.0.4", manifest.Version);
        Assert.Equal("pob.dll", manifest.EffectiveAssembly);
        Assert.False(manifest.HasNativeClient);

        var report = DualModPackageValidator.ValidateManifest(manifest);
        Assert.True(report.IsValid);
    }

    [Fact]
    public void DualModManifest_RoundTripsNativeClientFieldsAndWaypointCatalogIndex()
    {
        var manifest = new DualModManifest
        {
            Id = "harvest-garden",
            Name = "Harvest Garden",
            Description = "Sacred Grove dual-side mod.",
            Author = "LocalExile Community",
            Version = "1.0.0",
            CompatibleClientVersions = ["0.5.2-beta"],
            Kind = "feature",
            Depends = ["Game"],
            NativeClient = new NativeClientManifest
            {
                Required = false,
                SdkVersionRange = ">=0.1.0-poc <2.0.0",
                Assembly = "harvest-garden.Client.dll",
                PayloadDir = "client",
                Overlays = ["Metadata/UI/Mods/Garden/Stockpile.ui"],
                Replaces = [],
                RawAccess = false,
                CustomOpcodes = [new(1001, "GardenPlantSeed", OpcodeDirection.ClientToServer)],
                GealBuiltins = ["GardenRipen"],
                CustomControls = ["GrowthGauge"],
                ContentRegistries = [new("BaseItemTypes", "Metadata/Items/Mods/Garden/WildSeed", "append")],
                ServerHalf = new("harvest-garden", "1.0.0", true, "harvest-garden")
            }
        };

        string json = manifest.ToJson();
        var reparsed = DualModManifest.Parse(json);

        Assert.True(reparsed.HasNativeClient);
        Assert.Equal("harvest-garden.Client.dll", reparsed.NativeClient!.Assembly);
        Assert.Single(reparsed.NativeClient.CustomOpcodes);
        Assert.Equal((ushort)1001, reparsed.NativeClient.CustomOpcodes[0].Id);
        Assert.Single(reparsed.NativeClient.GealBuiltins);
        Assert.Equal("GardenRipen", reparsed.NativeClient.GealBuiltins[0]);
        Assert.Equal("GrowthGauge", reparsed.NativeClient.CustomControls[0]);

        // Verify LocalExile-Mods mods.json v1 index interoperability
        var index = new WaypointCatalogIndex
        {
            Version = 1,
            Mods = [WaypointCatalogModEntry.FromManifest(reparsed, isProject: true, hasSettings: true)]
        };

        var reparsedIndex = WaypointCatalogIndex.Parse(index.ToJson());
        Assert.Equal(1, reparsedIndex.Version);
        Assert.Single(reparsedIndex.Mods);
        Assert.Equal("mods/harvest-garden", reparsedIndex.Mods[0].Path);
        Assert.NotNull(reparsedIndex.Mods[0].NativeClient);
        Assert.True(reparsedIndex.Mods[0].NativeClient!.HasClientAssembly);
    }

    [Fact]
    public void ValidateManifest_RejectsRawAccessWithoutJustificationAndClosedRegistryTable()
    {
        var manifest = new DualModManifest
        {
            Id = "bad-mod",
            Version = "1.0.0",
            CompatibleClientVersions = ["0.5.2-beta"],
            Depends = ["bad-mod"], // self dependency
            NativeClient = new NativeClientManifest
            {
                SdkVersionRange = ">=2.0.0 <3.0.0",
                RawAccess = true,
                RawAccessJustification = null,
                ContentRegistries = [new("ItemClasses", "CustomClass", "append")]
            }
        };

        var report = DualModPackageValidator.ValidateManifest(manifest, SemVer.Parse("0.1.0-poc"));

        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, d => d.Code == "MAN009"); // self-dependency
        Assert.Contains(report.Errors, d => d.Code == "NC002");  // SDK version incompatible
        Assert.Contains(report.Errors, d => d.Code == "NC008");  // rawAccess missing justification
        Assert.Contains(report.Errors, d => d.Code == "NC016");  // ItemClasses is closed
    }

    [Fact]
    public void DetectConflicts_FindsOverlappingReplacesOpcodesGealControlsAndRegistries()
    {
        var modA = new DualModManifest
        {
            Id = "mod-a",
            Version = "1.0.0",
            NativeClient = new NativeClientManifest
            {
                Replaces = ["Metadata/UI/InGameState/EscapeMenu.ui"],
                CustomOpcodes = [new(1001, "AlphaOp")],
                GealBuiltins = ["SharedBuiltin"],
                CustomControls = ["SharedGauge"],
                ContentRegistries = [new("BaseItemTypes", "Metadata/Items/Mods/Shared/Orb", "append")]
            }
        };

        var modB = new DualModManifest
        {
            Id = "mod-b",
            Version = "1.0.0",
            NativeClient = new NativeClientManifest
            {
                Replaces = ["metadata/ui/ingamestate/escapemenu.ui"], // case-insensitive collision
                CustomOpcodes = [new(1001, "BetaOp")],
                GealBuiltins = ["sharedbuiltin"],
                CustomControls = ["sharedgauge"],
                ContentRegistries = [new("BaseItemTypes", "Metadata/Items/Mods/Shared/Orb", "append")]
            }
        };

        var conflicts = DualModPackageValidator.DetectConflicts([modA, modB]);

        Assert.Equal(5, conflicts.Count);
        Assert.Contains(conflicts, c => c.Code == "CNF002" && c.Kind == "FilePath");
        Assert.Contains(conflicts, c => c.Code == "CNF003" && c.Kind == "CustomOpcode");
        Assert.Contains(conflicts, c => c.Code == "CNF004" && c.Kind == "GealBuiltin");
        Assert.Contains(conflicts, c => c.Code == "CNF005" && c.Kind == "CustomControl");
        Assert.Contains(conflicts, c => c.Code == "CNF006" && c.Kind == "ContentRegistry");
    }
}
