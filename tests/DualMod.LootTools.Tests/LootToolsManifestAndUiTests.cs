using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using DualMod.LootTools.Server;
using DualMod.LootTools.Shared;
using LocalExile.Client.Sdk;
using LocalExile.Client.ServerBridge;
using LocalExile.Client.Waypoint;
using Xunit;

namespace DualMod.LootTools.Tests;

public sealed class LootToolsManifestAndUiTests
{
    [Fact]
    public void Manifest_IsValidDualMod_RequiresSdk040_AndHasZeroConflictsWithAllShowcases()
    {
        string repoRoot = FindRepoRoot();
        DualModManifest lootTools = DualModManifest.LoadFromFile(Path.Combine(repoRoot, "examples", "DualMod.LootTools", "mod.json"));
        DualModManifest stashQol = DualModManifest.LoadFromFile(Path.Combine(repoRoot, "examples", "DualMod.StashQol", "mod.json"));
        DualModManifest skipCampaign = DualModManifest.LoadFromFile(Path.Combine(repoRoot, "examples", "DualMod.SkipCampaign", "mod.json"));
        DualModManifest siphon = DualModManifest.LoadFromFile(Path.Combine(repoRoot, "examples", "DualMod.SiphonOrb", "mod.json"));
        DualModManifest maxwell = DualModManifest.LoadFromFile(Path.Combine(repoRoot, "examples", "DualMod.MaxwellPet", "mod.json"));
        DualModManifest garden = DualModManifest.LoadFromFile(Path.Combine(repoRoot, "examples", "DualMod.HarvestGarden", "mod.json"));

        ValidationReport report = DualModPackageValidator.ValidateManifest(lootTools, SemVer.Parse(SdkVersion.Current));
        Assert.True(report.IsValid, string.Join(Environment.NewLine, report.Errors.Select(e => $"{e.Code}: {e.Message}")));
        Assert.Empty(report.Warnings);

        Assert.Equal(LootToolsProtocol.ModId, lootTools.Id);
        Assert.Equal("loottools.dll", lootTools.EffectiveAssembly);
        Assert.NotNull(lootTools.NativeClient);
        Assert.Equal(">=0.4.0 <2.0.0", lootTools.NativeClient!.SdkVersionRange);
        Assert.Equal("loottools.Client.dll", lootTools.NativeClient.Assembly);
        Assert.False(lootTools.NativeClient.RawAccess);
        Assert.Empty(lootTools.NativeClient.Replaces);
        Assert.Contains(lootTools.NativeClient.Overlays, p => p.Equals(LootToolsProtocol.PanelUiPath, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(lootTools.NativeClient.ServerHalf);
        Assert.Equal(LootToolsProtocol.ChannelName, lootTools.NativeClient.ServerHalf!.ChannelName);

        // 0.4.0 minimum range rejects 0.3.0 cleanly with NC002.
        ValidationReport oldSdkReport = DualModPackageValidator.ValidateManifest(lootTools, SemVer.Parse("0.3.0"));
        Assert.False(oldSdkReport.IsValid);
        Assert.Contains(oldSdkReport.Errors, e => e.Code == "NC002");

        // Zero conflicts across all dual-side showcase mods.
        var conflicts = DualModPackageValidator.DetectConflicts([garden, maxwell, siphon, skipCampaign, stashQol, lootTools]);
        Assert.Empty(conflicts);
    }

    [Fact]
    public void PackageDirectory_ValidatesWithZeroErrorsAndZeroWarnings()
    {
        string repoRoot = FindRepoRoot();
        string pkgDir = Path.Combine(repoRoot, "examples", "DualMod.LootTools");
        ValidationReport report = DualModPackageValidator.ValidatePackageDirectory(pkgDir, SemVer.Parse(SdkVersion.Current));
        Assert.True(report.IsValid, string.Join(Environment.NewLine, report.Errors.Select(e => $"{e.Code}: {e.Message}")));
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void SettingsJson_HasValidSchemaAndMatchesServerDefaults()
    {
        string repoRoot = FindRepoRoot();
        string settingsPath = Path.Combine(repoRoot, "examples", "DualMod.LootTools", "settings.json");
        Assert.True(File.Exists(settingsPath));

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
        JsonElement settings = doc.RootElement.GetProperty("settings");

        LootToolsSettings defaults = new();
        Assert.Equal(defaults.MagnetRadius, settings.GetProperty("magnetRadius").GetProperty("default").GetInt32());
        Assert.Equal(defaults.PickupRadius, settings.GetProperty("pickupRadius").GetProperty("default").GetInt32());
        Assert.Equal(defaults.MergeRadius, settings.GetProperty("mergeRadius").GetProperty("default").GetInt32());
        Assert.Equal(defaults.MaxBatchSize, settings.GetProperty("maxBatchSize").GetProperty("default").GetInt32());
        Assert.Equal(defaults.AutoMergeEnabled, settings.GetProperty("autoMergeEnabled").GetProperty("default").GetBoolean());
        Assert.Equal(defaults.AutoMergeIntervalSeconds, settings.GetProperty("autoMergeIntervalSeconds").GetProperty("default").GetDouble(), precision: 2);
        Assert.Equal(defaults.ScopeMergeToFilter, settings.GetProperty("scopeMergeToFilter").GetProperty("default").GetBoolean());
        Assert.Equal(defaults.ZoomOverrideEnabled, settings.GetProperty("zoomOverrideEnabled").GetProperty("default").GetBoolean());
        Assert.Equal(defaults.ZoomOverrideValue, settings.GetProperty("zoomOverrideValue").GetProperty("default").GetSingle(), precision: 2);
    }

    [Fact]
    public void LootToolsPanelUi_Passes329UiLayoutLinterWithZeroDiagnostics()
    {
        string repoRoot = FindRepoRoot();
        string uiPath = Path.Combine(repoRoot, "examples", "DualMod.LootTools", "client", "Metadata", "UI", "Mods", "LootTools", "LootToolsPanel.ui");
        Assert.True(File.Exists(uiPath), $"Missing UI file at {uiPath}");

        string content = File.ReadAllText(uiPath);
        IReadOnlyList<ValidationDiagnostic> diags = UiLayoutLinter.Lint(content, LootToolsProtocol.PanelUiPath);
        Assert.True(diags.Count == 0, string.Join(Environment.NewLine, diags.Select(d => $"{d.Code} L{d.Line}: {d.Message}")));

        Assert.Contains("id = loottools_panel;", content, StringComparison.Ordinal);
        Assert.Contains("id = filter_name_label;", content, StringComparison.Ordinal);
        Assert.Contains("id = visible_items_label;", content, StringComparison.Ordinal);
        Assert.Contains("id = area_status_label;", content, StringComparison.Ordinal);
        Assert.Contains("id = reload_filter_button;", content, StringComparison.Ordinal);
        Assert.Contains("id = magnet_radius_textbox;", content, StringComparison.Ordinal);
        Assert.Contains("id = pickup_radius_textbox;", content, StringComparison.Ordinal);
        Assert.Contains("id = merge_radius_textbox;", content, StringComparison.Ordinal);
        Assert.Contains("id = auto_merge_checkbox;", content, StringComparison.Ordinal);
        Assert.Contains("id = scope_merge_filter_checkbox;", content, StringComparison.Ordinal);
        Assert.Contains("id = zoom_override_checkbox;", content, StringComparison.Ordinal);
        Assert.Contains("id = zoom_value_textbox;", content, StringComparison.Ordinal);
        Assert.Contains("id = apply_settings_button;", content, StringComparison.Ordinal);
        Assert.Contains("id = status_label;", content, StringComparison.Ordinal);
    }

    [Fact]
    public void GatewayEntry_FollowsBlueprintRules_NoTeleportOnWorldItems_NoSendPickupWakeOmission_NoSyncOverAsync()
    {
        string repoRoot = FindRepoRoot();
        string entryPath = Path.Combine(repoRoot, "examples", "DualMod.LootTools", "Server", "LootToolsLocalExileEntry.cs");
        Assert.True(File.Exists(entryPath));

        string source = File.ReadAllText(entryPath);
        Assert.Contains("class LootToolsLocalExileEntry : IMod", source, StringComparison.Ordinal);
        Assert.Contains("LocalExileGatewayHost.Create", source, StringComparison.Ordinal);
        Assert.Contains("GameEvents.AreaTick.Add", source, StringComparison.Ordinal);
        Assert.Contains("Game.SendWorldRemoval", source, StringComparison.Ordinal);
        Assert.Contains("Game.SendGroundItem", source, StringComparison.Ordinal);
        Assert.Contains("Game.PickUpGold", source, StringComparison.Ordinal);
        Assert.Contains("Game.TakeGroundStack", source, StringComparison.Ordinal);
        Assert.Contains("Game.KeepOnGroundIfNotSaved", source, StringComparison.Ordinal);
        Assert.Contains("InstanceItems.InventoryFull()", source, StringComparison.Ordinal);
        Assert.Contains("InstanceItems.InventoryChange", source, StringComparison.Ordinal);
        Assert.Contains("ExpandedInventoryMode == 1", source, StringComparison.Ordinal);
        Assert.Contains("WorldOutput.Has", source, StringComparison.Ordinal);
        Assert.Contains("IsStartWeapon", source, StringComparison.Ordinal);
        Assert.Contains("TutorialObjects.DropKey", source, StringComparison.Ordinal);

        // Forbidden patterns from #62 blueprint:
        Assert.DoesNotContain("InstanceProtocol.Teleport", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Game.SendPickup(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetAwaiter().GetResult()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LootToolsLocalExileEntry_CompilesCleanlyViaRoslynAgainstGatewayAssemblies()
    {
        string? gatewayBin = FindLocalExileGatewayBinDir();
        string? gameDll = FindLocalExileGameDll();
        if (gatewayBin is null || gameDll is null)
        {
            return;
        }

        string repoRoot = FindRepoRoot();
        string entryPath = Path.Combine(repoRoot, "examples", "DualMod.LootTools", "Server", "LootToolsLocalExileEntry.cs");
        string source = File.ReadAllText(entryPath);

        CompileViaRoslynAndAssertSuccess(
            gatewayBin,
            "LootToolsLocalExileEntryProbe",
            source,
            gameDll,
            typeof(SdkVersion).Assembly.Location,
            typeof(ClientBridgeHost).Assembly.Location,
            typeof(LootToolsProtocol).Assembly.Location,
            typeof(LootToolsServerMod).Assembly.Location);
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

    private static void CompileViaRoslynAndAssertSuccess(
        string gatewayBinDir,
        string assemblyName,
        string source,
        params string[] extraReferencePaths)
    {
        var roslynAlc = new AssemblyLoadContext("RoslynEntryProbe-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        try
        {
            Assembly roslynCommon = roslynAlc.LoadFromAssemblyPath(Path.Combine(gatewayBinDir, "Microsoft.CodeAnalysis.dll"));
            Assembly roslynCSharp = roslynAlc.LoadFromAssemblyPath(Path.Combine(gatewayBinDir, "Microsoft.CodeAnalysis.CSharp.dll"));

            Type syntaxTreeType = roslynCSharp.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree")!;
            Type compType = roslynCSharp.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation")!;
            Type compOptsType = roslynCSharp.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions")!;
            Type nullableOptsType = roslynCommon.GetType("Microsoft.CodeAnalysis.NullableContextOptions")!;
            Type outputKindType = roslynCommon.GetType("Microsoft.CodeAnalysis.OutputKind")!;
            Type metaRefType = roslynCommon.GetType("Microsoft.CodeAnalysis.MetadataReference")!;
            Type treeType = roslynCommon.GetType("Microsoft.CodeAnalysis.SyntaxTree")!;

            MethodInfo parseText = syntaxTreeType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == "ParseText" && m.GetParameters()[0].ParameterType == typeof(string));
            object?[] parseArgs = new object?[parseText.GetParameters().Length];
            parseArgs[0] = source;
            object syntaxTree = parseText.Invoke(null, parseArgs)!;

            MethodInfo createFromFile = metaRefType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == "CreateFromFile" && m.GetParameters()[0].ParameterType == typeof(string));

            object CreateMetaRef(string path)
            {
                object?[] args = new object?[createFromFile.GetParameters().Length];
                args[0] = path;
                return createFromFile.Invoke(null, args)!;
            }

            string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            var refPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                typeof(object).Assembly.Location,
                typeof(Task).Assembly.Location,
                typeof(Console).Assembly.Location,
                typeof(System.Collections.Concurrent.ConcurrentDictionary<,>).Assembly.Location,
                typeof(Enumerable).Assembly.Location,
                Path.Combine(runtimeDir, "System.Runtime.dll"),
                Path.Combine(runtimeDir, "System.Collections.dll"),
                Path.Combine(runtimeDir, "netstandard.dll"),
                Path.Combine(gatewayBinDir, "GameServer.Core.dll"),
                Path.Combine(gatewayBinDir, "GameServer.Data.dll"),
                Path.Combine(gatewayBinDir, "GameServer.Modding.dll"),
                Path.Combine(gatewayBinDir, "GameServer.Gateway.dll"),
            };
            foreach (string extra in extraReferencePaths)
            {
                refPaths.Add(extra);
            }

            string[] presentPaths = [.. refPaths.Where(File.Exists)];
            Array metaRefs = Array.CreateInstance(metaRefType, presentPaths.Length);
            for (int i = 0; i < presentPaths.Length; i++)
            {
                metaRefs.SetValue(CreateMetaRef(presentPaths[i]), i);
            }

            Array trees = Array.CreateInstance(treeType, 1);
            trees.SetValue(syntaxTree, 0);

            ConstructorInfo optsCtor = compOptsType.GetConstructors()
                .First(c => c.GetParameters().Length >= 1 && c.GetParameters()[0].ParameterType == outputKindType);
            object?[] optsArgs = new object?[optsCtor.GetParameters().Length];
            optsArgs[0] = Enum.Parse(outputKindType, "DynamicallyLinkedLibrary");
            for (int i = 1; i < optsArgs.Length; i++)
            {
                ParameterInfo param = optsCtor.GetParameters()[i];
                optsArgs[i] = param.ParameterType == nullableOptsType
                    ? Enum.Parse(nullableOptsType, "Enable")
                    : (param.HasDefaultValue ? param.DefaultValue : null);
            }

            object compOpts = optsCtor.Invoke(optsArgs);
            MethodInfo createComp = compType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == "Create" && m.GetParameters().Length == 4);
            object compilation = createComp.Invoke(null, [assemblyName, trees, metaRefs, compOpts])!;

            using var peStream = new MemoryStream();
            MethodInfo emit = compilation.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .First(m => m.Name == "Emit" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(Stream));
            object?[] emitArgs = new object?[emit.GetParameters().Length];
            emitArgs[0] = peStream;
            object emitResult = emit.Invoke(compilation, emitArgs)!;

            bool success = (bool)emitResult.GetType().GetProperty("Success")!.GetValue(emitResult)!;
            string[] diags = [.. ((IEnumerable)emitResult.GetType().GetProperty("Diagnostics")!.GetValue(emitResult)!)
                .Cast<object>()
                .Select(d => d.ToString() ?? string.Empty)];
            Assert.True(success, $"Roslyn compile ({assemblyName}) failed:\n" + string.Join("\n", diags));
        }
        finally
        {
            roslynAlc.Unload();
        }
    }

    internal static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "02-architecture.md")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException("Could not locate NativeMods repository root.");
    }
}
