using DualMod.MaxwellPet.Server;
using DualMod.MaxwellPet.Shared;
using LocalExile.Client.Host.Native;
using LocalExile.Client.Host.Services;
using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Addresses;
using LocalExile.Client.ServerBridge;
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
        Assert.DoesNotContain(report.Warnings, w => w.Code == "NC018");
        Assert.Empty(report.Warnings);

        Assert.Equal(MaxwellProtocol.ModId, maxwell.Id);
        Assert.Equal("maxwell.dll", maxwell.EffectiveAssembly);
        Assert.NotNull(maxwell.NativeClient);
        Assert.Equal(">=0.2.0 <2.0.0", maxwell.NativeClient!.SdkVersionRange);
        Assert.Equal("maxwell.Client.dll", maxwell.NativeClient.Assembly);
        Assert.False(maxwell.NativeClient.RawAccess);

        // 0.2.0 minimum range rejects 0.1.0-poc cleanly
        var oldSdkReport = DualModPackageValidator.ValidateManifest(maxwell, SemVer.Parse("0.1.0-poc"));
        Assert.False(oldSdkReport.IsValid);
        Assert.Contains(oldSdkReport.Errors, e => e.Code == "NC002");

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
        Assert.Equal(649897822u, PoeContentHash.MurmurHash2("Metadata/Items/Pets/Kiwi1"));

        // Brand-new Maxwell Pet MTX hashes via ServerBridge PoeContentHash & CustomPetMtxDefinition
        var def = MaxwellServerMod.MaxwellPetDefinition;
        def.Validate();

        Assert.Equal(MaxwellProtocol.MtxTypeHash, def.MtxTypeHash);
        Assert.Equal(MaxwellProtocol.PetObjectTypeHash, def.PetObjectTypeHash);
        Assert.Equal(MaxwellProtocol.PetHash16, def.PetHash16);

        // Verify appended row indices are strictly past 3.29.3d stock counts (never replacing stock rows)
        Assert.Equal(8557, def.MtxTypesRowIndex);
        Assert.Equal(8558u, def.RecordId);
        Assert.Equal(7849, def.MtxTypeGameSpecificRowIndex);
        Assert.Equal(516, def.PetRowIndex);
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
        Assert.Contains("LocalExileGatewayHost.Create", source, StringComparison.Ordinal);
        Assert.Contains("OnCosmeticChanged", source, StringComparison.Ordinal);
        Assert.Contains("RegisterCommand(\"maxwell\"", source, StringComparison.Ordinal);
        Assert.Contains("global::Client.For", source, StringComparison.Ordinal);
        Assert.Contains("LocalExilePetMtxRegistrar", source, StringComparison.Ordinal);
        Assert.Contains("ObserveLiveGatewayPlayer", source, StringComparison.Ordinal);
        Assert.Contains("new PetObject(", source, StringComparison.Ordinal);
        Assert.Contains("Game.SendWorldCreate", source, StringComparison.Ordinal);
        Assert.Contains("Game.SendWorldRemoval", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AreaTick", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetAwaiter().GetResult()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MaxwellLocalExileEntry_CompilesCleanlyViaRoslynAgainstGatewayAssemblies()
    {
        string? gatewayBin = FindLocalExileGatewayBinDir();
        string? gameDll = FindLocalExileGameDll();
        if (gatewayBin is null || gameDll is null)
            return;

        string repoRoot = FindRepoRoot();
        string entryPath = Path.Combine(repoRoot, "examples", "DualMod.MaxwellPet", "Server", "MaxwellLocalExileEntry.cs");
        string source = File.ReadAllText(entryPath);

        MaxwellRoslynEntryCompileHelper.CompileAndAssertSuccess(
            gatewayBin,
            "MaxwellLocalExileEntryProbe",
            source,
            gameDll,
            typeof(SdkVersion).Assembly.Location,
            typeof(ClientBridgeHost).Assembly.Location,
            typeof(MaxwellProtocol).Assembly.Location,
            typeof(MaxwellServerMod).Assembly.Location);
    }

    private static string? FindLocalExileGatewayBinDir()
    {
        foreach (string config in new[] { "Release", "Debug" })
        {
            string candidate = Path.Combine("/home/nam/Git/LocalExile/src/GameServer.Gateway/bin", config, "net10.0");
            if (File.Exists(Path.Combine(candidate, "GameServer.Gateway.dll")) &&
                File.Exists(Path.Combine(candidate, "GameServer.Modding.dll")) &&
                File.Exists(Path.Combine(candidate, "GameServer.Core.dll")))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? FindLocalExileGameDll()
    {
        string[] candidates =
        [
            "/home/nam/Git/LocalExile/tests/GameServer.Gateway.Tests/bin/Release/net10.0/game.dll",
            "/home/nam/Git/LocalExile/Modules/Core/Game/bin/game.dll",
            "/home/nam/Git/LocalExile/tests/GameServer.Gateway.Tests/bin/Debug/net10.0/game.dll",
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "NativeMods.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException("Could not locate NativeMods repository root.");
    }
}
