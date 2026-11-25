using DualMod.MaxwellPet.Server;
using DualMod.MaxwellPet.Shared;
using LocalExile.Client.Host.Native;
using LocalExile.Client.Host.Services;
using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Addresses;
using LocalExile.Client.Waypoint;
using Xunit;

namespace LocalExile.Client.MaxwellPet.Tests;

public sealed class MaxwellPetManifestAndProtocolTests
{
    [Fact]
    public void Manifest_IsValidDualModDeclaringBrandNewAppendedPetMtx_AndHasZeroConflictsWithHarvestGarden()
    {
        string repoRoot = FindRepoRoot();
        string maxwellManifestPath = Path.Combine(repoRoot, "examples", "DualMod.MaxwellPet", "mod.json");
        string gardenManifestPath = Path.Combine(repoRoot, "examples", "DualMod.HarvestGarden", "mod.json");

        var maxwell = DualModManifest.LoadFromFile(maxwellManifestPath);
        var garden = DualModManifest.LoadFromFile(gardenManifestPath);

        var report = DualModPackageValidator.ValidateManifest(maxwell, SemVer.Parse(SdkVersion.Current));
        Assert.True(report.IsValid, string.Join(Environment.NewLine, report.Errors.Select(e => $"{e.Code}: {e.Message}")));

        Assert.Equal(MaxwellProtocol.ModId, maxwell.Id);
        Assert.Equal("maxwell.dll", maxwell.EffectiveAssembly);
        Assert.NotNull(maxwell.NativeClient);
        Assert.Equal("maxwell.Client.dll", maxwell.NativeClient!.Assembly);
        Assert.False(maxwell.NativeClient.RawAccess);

        // Appended brand-new Pet MTX rows (never replacing any existing stock pet .ot/.ao)
        Assert.Contains(maxwell.NativeClient.Replaces, p => p.Equals(MaxwellProtocol.MtxTypesDatPath, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(maxwell.NativeClient.Replaces, p => p.Equals(MaxwellProtocol.MtxTypeGameSpecificDatPath, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(maxwell.NativeClient.Replaces, p => p.Equals(MaxwellProtocol.PetDatPath, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(maxwell.NativeClient.Replaces, p => p.Contains("Kiwi", StringComparison.OrdinalIgnoreCase));

        Assert.All(maxwell.NativeClient.ContentRegistries, reg => Assert.Equal("append", reg.Mode));
        Assert.Contains(maxwell.NativeClient.ContentRegistries, r => r.Table == "MtxTypes" && r.EntryId == MaxwellProtocol.MtxTypeId);
        Assert.Contains(maxwell.NativeClient.ContentRegistries, r => r.Table == "MtxTypeGameSpecific" && r.EntryId == MaxwellProtocol.MtxTypeId);
        Assert.Contains(maxwell.NativeClient.ContentRegistries, r => r.Table == "Pet" && r.EntryId == MaxwellProtocol.PetMetadataPath);

        Assert.Contains(MaxwellProtocol.GealDanceBuiltin, maxwell.NativeClient.GealBuiltins);
        Assert.Equal(4, maxwell.NativeClient.CustomOpcodes.Count);
        Assert.NotNull(maxwell.NativeClient.ServerHalf);
        Assert.True(maxwell.NativeClient.ServerHalf!.SupportsGracefulFallback);

        // Must coexist cleanly with DualMod.HarvestGarden with zero cross-mod collisions
        var conflicts = DualModPackageValidator.DetectConflicts([garden, maxwell]);
        Assert.Empty(conflicts);
    }

    [Fact]
    public void ProtocolHashes_MatchVerifiedPoE329MurmurHash2AndFoldedHash16()
    {
        // Regression proof against stock 3.29.3d Kiwi1 row (client.db MtxTypes row 5730)
        Assert.Equal(649897822u, MaxwellProtocol.ComputeObjectTypeHash("Metadata/Items/Pets/Kiwi1"));

        // Brand-new Maxwell Pet MTX hashes
        Assert.Equal(MaxwellProtocol.MtxTypeHash, MaxwellProtocol.ComputeObjectTypeHash(MaxwellProtocol.MtxTypeId));
        Assert.Equal(MaxwellProtocol.PetObjectTypeHash, MaxwellProtocol.ComputeObjectTypeHash(MaxwellProtocol.PetMetadataPath));
        Assert.Equal(MaxwellProtocol.PetHash16, MaxwellProtocol.FoldHash16(MaxwellProtocol.PetObjectTypeHash));

        // Verify appended row indices are strictly past 3.29.3d stock counts (never replacing stock rows)
        Assert.Equal(8557, MaxwellServerMod.DefaultRegistration.MtxTypesRowIndex);
        Assert.Equal(8558u, MaxwellServerMod.DefaultRegistration.RecordId);
        Assert.Equal(7849, MaxwellServerMod.DefaultRegistration.MtxTypeGameSpecificRowIndex);
        Assert.Equal(516, MaxwellServerMod.DefaultRegistration.PetRowIndex);
    }

    [Fact]
    public void MaxwellPanelUi_Passes329UiLayoutLinterWithZeroErrorsAndZeroWarnings()
    {
        string repoRoot = FindRepoRoot();
        string uiPath = Path.Combine(
            repoRoot,
            "examples",
            "DualMod.MaxwellPet",
            "client",
            "Metadata",
            "UI",
            "Mods",
            "Maxwell",
            "MaxwellPanel.ui");

        Assert.True(File.Exists(uiPath), $"Missing UI file at {uiPath}");
        string uiContent = File.ReadAllText(uiPath);

        var diagnostics = UiLayoutLinter.Lint(uiContent, MaxwellProtocol.PanelUiPath);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void MaxwellPetOt_ExtendsBasePetWithNoExtraSerializedComponents_AndPassesGealValidation()
    {
        string repoRoot = FindRepoRoot();
        string otPath = Path.Combine(
            repoRoot,
            "examples",
            "DualMod.MaxwellPet",
            "client",
            "Metadata",
            "Pet",
            "Mods",
            "Maxwell",
            "MaxwellPet.ot");

        Assert.True(File.Exists(otPath), $"Missing Pet .ot file at {otPath}");
        string otContent = File.ReadAllText(otPath);

        Assert.Contains("extends \"Metadata/Pet/Pet\"", otContent, StringComparison.Ordinal);
        Assert.Contains(MaxwellProtocol.PetAoPath, otContent, StringComparison.Ordinal);
        Assert.DoesNotContain("StateMachine", otContent, StringComparison.Ordinal);
        Assert.Contains(MaxwellProtocol.GealDanceBuiltin, otContent, StringComparison.Ordinal);

        using var bridge = new SimulatedNativeBridge(ClientBuildId.Build3293d);
        var gealRegistry = new GealBuiltinRegistry();
        var scriptService = new ScriptService(bridge, gealRegistry);
        var diagnostics = scriptService.ValidateScript(otContent);
        Assert.DoesNotContain(diagnostics, d => d.IsError);
    }

    [Fact]
    public void LocalExileGatewayEntryPoint_ExistsAndTargetsStockLocalExileModdingApis()
    {
        string repoRoot = FindRepoRoot();
        string entryPath = Path.Combine(repoRoot, "examples", "DualMod.MaxwellPet", "Server", "MaxwellLocalExileEntry.cs");
        Assert.True(File.Exists(entryPath));

        string source = File.ReadAllText(entryPath);
        Assert.Contains("class MaxwellLocalExileEntry : IMod", source, StringComparison.Ordinal);
        Assert.Contains("GameEvents.Command", source, StringComparison.Ordinal);
        Assert.Contains("new PetObject(", source, StringComparison.Ordinal);
        Assert.Contains("InstanceMicrotransactions.RecordUpsert", source, StringComparison.Ordinal);
        Assert.Contains("Game.SendWorldCreate", source, StringComparison.Ordinal);
        Assert.Contains("Game.SendWorldRemoval", source, StringComparison.Ordinal);
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
