using DualMod.SiphonOrb.Server;
using DualMod.SiphonOrb.Shared;
using LocalExile.Client.Sdk;
using LocalExile.Client.ServerBridge;
using LocalExile.Client.Waypoint;
using Xunit;

namespace LocalExile.Client.SiphonOrb.Tests;

public sealed class SiphonManifestAndUiTests
{
    [Fact]
    public void Manifest_IsValidDualMod_AndHasZeroConflictsWithHarvestGardenAndMaxwell()
    {
        string repoRoot = FindRepoRoot();
        var siphon = DualModManifest.LoadFromFile(Path.Combine(repoRoot, "examples", "DualMod.SiphonOrb", "mod.json"));
        var maxwell = DualModManifest.LoadFromFile(Path.Combine(repoRoot, "examples", "DualMod.MaxwellPet", "mod.json"));
        var garden = DualModManifest.LoadFromFile(Path.Combine(repoRoot, "examples", "DualMod.HarvestGarden", "mod.json"));

        var report = DualModPackageValidator.ValidateManifest(siphon, SemVer.Parse(SdkVersion.Current));
        Assert.True(report.IsValid, string.Join(Environment.NewLine, report.Errors.Select(e => $"{e.Code}: {e.Message}")));
        Assert.DoesNotContain(report.Warnings, w => w.Code == "NC018");
        Assert.Empty(report.Warnings);

        Assert.Equal(SiphonProtocol.ModId, siphon.Id);
        Assert.Equal("siphon.dll", siphon.EffectiveAssembly);
        Assert.NotNull(siphon.NativeClient);
        Assert.Equal(">=0.2.0 <2.0.0", siphon.NativeClient!.SdkVersionRange);
        Assert.Equal("siphon.Client.dll", siphon.NativeClient.Assembly);
        Assert.False(siphon.NativeClient.RawAccess);

        // 0.2.0 minimum range rejects 0.1.0-poc cleanly
        var oldSdkReport = DualModPackageValidator.ValidateManifest(siphon, SemVer.Parse("0.1.0-poc"));
        Assert.False(oldSdkReport.IsValid);
        Assert.Contains(oldSdkReport.Errors, e => e.Code == "NC002");

        Assert.Contains(siphon.NativeClient.Replaces, p => p.Equals(SiphonProtocol.BaseItemTypesDatPath, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(siphon.NativeClient.Replaces, p => p.Equals(SiphonProtocol.ItemVisualIdentityDatPath, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(siphon.NativeClient.Replaces, p => p.Equals(SiphonProtocol.CurrencyItemsDatPath, StringComparison.OrdinalIgnoreCase));

        Assert.All(siphon.NativeClient.ContentRegistries, reg => Assert.Equal("append", reg.Mode));
        Assert.Equal(6, siphon.NativeClient.ContentRegistries.Count);
        Assert.Equal(4, siphon.NativeClient.CustomOpcodes.Count);
        Assert.NotNull(siphon.NativeClient.ServerHalf);
        Assert.True(siphon.NativeClient.ServerHalf!.SupportsGracefulFallback);

        // Zero conflicts across all three dual-side mods!
        var conflicts = DualModPackageValidator.DetectConflicts([garden, maxwell, siphon]);
        Assert.Empty(conflicts);
    }

    [Fact]
    public void ProtocolHashesAndRows_MatchVerifiedPoE3293dInvariants()
    {
        // Regression proof against stock 3.29.3d Orb of Annulment (BaseItemTypes row 230)
        Assert.Equal(3081331240u, PoeContentHash.MurmurHash2("Metadata/Items/Currency/CurrencyRemoveMod"));

        var empty = SiphonServerMod.EmptyOrbDefinition;
        var charged = SiphonServerMod.ChargedOrbDefinition;
        empty.Validate();
        charged.Validate();

        Assert.Equal(SiphonProtocol.EmptyOrbTypeHash, empty.TypeHash);
        Assert.Equal(SiphonProtocol.ChargedOrbTypeHash, charged.TypeHash);
        Assert.NotEqual(empty.TypeHash, charged.TypeHash);
        Assert.NotEqual(empty.VisualIdentityHash16, charged.VisualIdentityHash16);

        Assert.Equal(5461, empty.BaseItemTypesRowIndex);
        Assert.Equal(5462, charged.BaseItemTypesRowIndex);
        Assert.Equal(15516, empty.ItemVisualIdentityRowIndex);
        Assert.Equal(15517, charged.ItemVisualIdentityRowIndex);
        Assert.Equal(1962, empty.CurrencyItemsRowIndex);
        Assert.Equal(1963, charged.CurrencyItemsRowIndex);

        Assert.Equal(["Base", "Usable", "Stack"], empty.Components);
        Assert.Equal(["Base", "Usable", "Stack", "LocalStats", "Mods"], charged.Components);
        Assert.Equal(20, empty.MaxStackSize);
        Assert.Equal(1, charged.MaxStackSize);
        Assert.True(charged.SupportsStoredMods);
    }

    [Fact]
    public void SiphonPickerUi_Passes329UiLayoutLinterWithZeroErrorsAndZeroWarnings()
    {
        string repoRoot = FindRepoRoot();
        string uiPath = Path.Combine(repoRoot, "examples", "DualMod.SiphonOrb", "client", "Metadata", "UI", "Mods", "Siphon", "SiphonPicker.ui");
        Assert.True(File.Exists(uiPath), $"Missing UI file at {uiPath}");

        string uiContent = File.ReadAllText(uiPath);
        var diagnostics = UiLayoutLinter.Lint(uiContent, SiphonProtocol.PickerUiPath);
        Assert.Empty(diagnostics);

        // Verify all 6 affix rows + buttons + labels + stored mod preview + confirm/cancel exist.
        Assert.Contains("id = siphon_picker_window;", uiContent, StringComparison.Ordinal);
        Assert.Contains("id = stored_mod_box;", uiContent, StringComparison.Ordinal);
        Assert.Contains("id = confirm_button;", uiContent, StringComparison.Ordinal);
        Assert.Contains("id = cancel_button;", uiContent, StringComparison.Ordinal);
        for (int i = 0; i < SiphonProtocol.MaxPickerAffixRows; i++)
        {
            Assert.Contains($"id = affix_row_{i};", uiContent, StringComparison.Ordinal);
            Assert.Contains($"id = affix_btn_{i};", uiContent, StringComparison.Ordinal);
            Assert.Contains($"id = affix_label_{i};", uiContent, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ItemMetadataScriptsAndGatewayEntry_ExistAndFollow329Conventions()
    {
        string repoRoot = FindRepoRoot();
        string emptyIt = File.ReadAllText(Path.Combine(repoRoot, "examples", "DualMod.SiphonOrb", "client", "Metadata", "Items", "Currency", "Mods", "Siphon", "SiphonOrbEmpty.it"));
        string chargedIt = File.ReadAllText(Path.Combine(repoRoot, "examples", "DualMod.SiphonOrb", "client", "Metadata", "Items", "Currency", "Mods", "Siphon", "SiphonOrbCharged.it"));
        string entrySource = File.ReadAllText(Path.Combine(repoRoot, "examples", "DualMod.SiphonOrb", "Server", "SiphonLocalExileEntry.cs"));

        Assert.Contains("version 3", emptyIt, StringComparison.Ordinal);
        Assert.Contains("extends \"Metadata/Items/Currency/StackableCurrency\"", emptyIt, StringComparison.Ordinal);
        Assert.DoesNotContain("Mods", emptyIt, StringComparison.Ordinal);

        Assert.Contains("version 3", chargedIt, StringComparison.Ordinal);
        Assert.Contains("extends \"Metadata/Items/Currency/StackableCurrency\"", chargedIt, StringComparison.Ordinal);
        Assert.Contains("LocalStats", chargedIt, StringComparison.Ordinal);
        Assert.Contains("Mods", chargedIt, StringComparison.Ordinal);
        Assert.Contains("enable_rarity = \"normal\"", chargedIt, StringComparison.Ordinal);

        Assert.Contains("class SiphonLocalExileEntry : IMod", entrySource, StringComparison.Ordinal);
        Assert.Contains("LocalExileGatewayHost.Create", entrySource, StringComparison.Ordinal);
        Assert.Contains("LocalExileCurrencyRegistrar", entrySource, StringComparison.Ordinal);
        Assert.Contains("LocalExileItemAffixService", entrySource, StringComparison.Ordinal);
        Assert.Contains("LocalExileGatewayTransactions", entrySource, StringComparison.Ordinal);
        Assert.Contains("global::Client.For", entrySource, StringComparison.Ordinal);
        Assert.Contains("InstanceItems.UseItemOnId", entrySource, StringComparison.Ordinal);
        Assert.Contains("InstanceItems.UseItemTypeOnId", entrySource, StringComparison.Ordinal);
        Assert.DoesNotContain("GetAwaiter().GetResult()", entrySource, StringComparison.Ordinal);
    }

    [Fact]
    public void SiphonLocalExileEntry_CompilesCleanlyViaRoslynAgainstGatewayAssemblies()
    {
        string? gatewayBin = FindLocalExileGatewayBinDir();
        string? gameDll = FindLocalExileGameDll();
        if (gatewayBin is null || gameDll is null)
            return;

        string repoRoot = FindRepoRoot();
        string entryPath = Path.Combine(repoRoot, "examples", "DualMod.SiphonOrb", "Server", "SiphonLocalExileEntry.cs");
        string source = File.ReadAllText(entryPath);

        RoslynEntryCompileHelper.CompileAndAssertSuccess(
            gatewayBin,
            "SiphonLocalExileEntryProbe",
            source,
            gameDll,
            typeof(SdkVersion).Assembly.Location,
            typeof(ClientBridgeHost).Assembly.Location,
            typeof(SiphonProtocol).Assembly.Location,
            typeof(SiphonServerMod).Assembly.Location);
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
            if (File.Exists(Path.Combine(dir, "02-architecture.md")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException("Could not locate NativeMods repository root.");
    }
}
