using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using DualMod.StashQol.Server;
using DualMod.StashQol.Shared;
using LocalExile.Client.Sdk;
using LocalExile.Client.ServerBridge;
using LocalExile.Client.Waypoint;
using Xunit;

namespace DualMod.StashQol.Tests;

public sealed class StashQolManifestAndUiTests
{
    private static readonly JsonSerializerOptions CaseInsensitiveJson = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void Manifest_IsValidDualMod_RequiresSdk030_AndHasZeroConflictsWithExistingShowcases()
    {
        string repoRoot = FindRepoRoot();
        DualModManifest stashQol = DualModManifest.LoadFromFile(Path.Combine(repoRoot, "examples", "DualMod.StashQol", "mod.json"));
        DualModManifest siphon = DualModManifest.LoadFromFile(Path.Combine(repoRoot, "examples", "DualMod.SiphonOrb", "mod.json"));
        DualModManifest maxwell = DualModManifest.LoadFromFile(Path.Combine(repoRoot, "examples", "DualMod.MaxwellPet", "mod.json"));
        DualModManifest garden = DualModManifest.LoadFromFile(Path.Combine(repoRoot, "examples", "DualMod.HarvestGarden", "mod.json"));

        ValidationReport report = DualModPackageValidator.ValidateManifest(stashQol, SemVer.Parse(SdkVersion.Current));
        Assert.True(report.IsValid, string.Join(Environment.NewLine, report.Errors.Select(e => $"{e.Code}: {e.Message}")));
        Assert.Empty(report.Warnings);

        Assert.Equal(StashQolProtocol.ModId, stashQol.Id);
        Assert.Equal("stashqol.dll", stashQol.EffectiveAssembly);
        Assert.NotNull(stashQol.NativeClient);
        Assert.Equal(">=0.3.0 <2.0.0", stashQol.NativeClient!.SdkVersionRange);
        Assert.Equal("stashqol.Client.dll", stashQol.NativeClient.Assembly);
        Assert.False(stashQol.NativeClient.RawAccess);
        Assert.Empty(stashQol.NativeClient.Replaces);
        Assert.Contains(stashQol.NativeClient.Overlays, p => p.Equals(StashQolProtocol.RegexRollerUiPath, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(stashQol.NativeClient.Overlays, p => p.Equals(StashQolProtocol.PobImportUiPath, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(stashQol.NativeClient.ServerHalf);
        Assert.Equal(StashQolProtocol.ChannelName, stashQol.NativeClient.ServerHalf!.ChannelName);

        // 0.3.0 minimum range rejects 0.2.0 cleanly with NC002.
        ValidationReport oldSdkReport = DualModPackageValidator.ValidateManifest(stashQol, SemVer.Parse("0.2.0"));
        Assert.False(oldSdkReport.IsValid);
        Assert.Contains(oldSdkReport.Errors, e => e.Code == "NC002");

        // Zero conflicts across all dual-side showcase mods.
        var conflicts = DualModPackageValidator.DetectConflicts([garden, maxwell, siphon, stashQol]);
        Assert.Empty(conflicts);
    }

    [Fact]
    public void SettingsJson_ParsesCleanlyIntoStashQolSettings()
    {
        string repoRoot = FindRepoRoot();
        string settingsPath = Path.Combine(repoRoot, "examples", "DualMod.StashQol", "settings.json");
        Assert.True(File.Exists(settingsPath));

        string json = File.ReadAllText(settingsPath);
        StashQolSettings? parsed = JsonSerializer.Deserialize<StashQolSettings>(json, CaseInsensitiveJson);
        Assert.NotNull(parsed);
        Assert.InRange(parsed!.MaxRegexRolls, 1, StashQolProtocol.MaxRegexRollsHardCap);
        Assert.True(parsed.VendorAllRequiresConfirmation);
        Assert.InRange(parsed.BulkUseMaxActions, 1, StashQolProtocol.MaxBulkUseHardCap);
        Assert.True(parsed.PobAutoApplyOnLevelUp);
        Assert.False(parsed.UpgradeCorruptedMaps);
    }

    [Fact]
    public void RegexRollerAndPobImportUi_Pass329UiLayoutLinterWithZeroDiagnostics()
    {
        string repoRoot = FindRepoRoot();
        string regexPath = Path.Combine(repoRoot, "examples", "DualMod.StashQol", "client", "Metadata", "UI", "Mods", "StashQol", "RegexRoller.ui");
        string pobPath = Path.Combine(repoRoot, "examples", "DualMod.StashQol", "client", "Metadata", "UI", "Mods", "StashQol", "PobImport.ui");

        Assert.True(File.Exists(regexPath), $"Missing UI file at {regexPath}");
        Assert.True(File.Exists(pobPath), $"Missing UI file at {pobPath}");

        string regexUi = File.ReadAllText(regexPath);
        var regexDiags = UiLayoutLinter.Lint(regexUi, StashQolProtocol.RegexRollerUiPath);
        Assert.True(regexDiags.Count == 0, string.Join(Environment.NewLine, regexDiags.Select(d => $"{d.Code} L{d.Line}: {d.Message}")));

        Assert.Contains("id = stashqol_regex_roller;", regexUi, StringComparison.Ordinal);
        Assert.Contains("id = target_preview_label;", regexUi, StringComparison.Ordinal);
        Assert.Contains("id = pattern_textbox;", regexUi, StringComparison.Ordinal);
        Assert.Contains("id = max_rolls_textbox;", regexUi, StringComparison.Ordinal);
        Assert.Contains("id = case_insensitive_checkbox;", regexUi, StringComparison.Ordinal);
        Assert.Contains("id = match_all_lines_checkbox;", regexUi, StringComparison.Ordinal);
        Assert.Contains("id = roll_button;", regexUi, StringComparison.Ordinal);
        Assert.Contains("id = status_label;", regexUi, StringComparison.Ordinal);

        string pobUi = File.ReadAllText(pobPath);
        var pobDiags = UiLayoutLinter.Lint(pobUi, StashQolProtocol.PobImportUiPath);
        Assert.True(pobDiags.Count == 0, string.Join(Environment.NewLine, pobDiags.Select(d => $"{d.Code} L{d.Line}: {d.Message}")));

        Assert.Contains("id = stashqol_pob_import;", pobUi, StringComparison.Ordinal);
        Assert.Contains("id = paste_clipboard_button;", pobUi, StringComparison.Ordinal);
        Assert.Contains("id = pob_code_textbox;", pobUi, StringComparison.Ordinal);
        Assert.Contains("id = auto_apply_checkbox;", pobUi, StringComparison.Ordinal);
        Assert.Contains("id = import_button;", pobUi, StringComparison.Ordinal);
        Assert.Contains("id = clear_plan_button;", pobUi, StringComparison.Ordinal);
        Assert.Contains("id = status_label;", pobUi, StringComparison.Ordinal);
    }

    [Fact]
    public void GatewayEntry_WiresLocalExilePart2aApisWithoutSyncOverAsync()
    {
        string repoRoot = FindRepoRoot();
        string entryPath = Path.Combine(repoRoot, "examples", "DualMod.StashQol", "Server", "StashQolLocalExileEntry.cs");
        Assert.True(File.Exists(entryPath));

        string source = File.ReadAllText(entryPath);
        Assert.Contains("class StashQolLocalExileEntry : IMod", source, StringComparison.Ordinal);
        Assert.Contains("LocalExileGatewayHost.Create", source, StringComparison.Ordinal);
        Assert.Contains("FindAffinityTab", source, StringComparison.Ordinal);
        Assert.Contains("GameEvents.QuoteVendor", source, StringComparison.Ordinal);
        Assert.Contains("GameEvents.LevelUp.Add", source, StringComparison.Ordinal);
        Assert.Contains("AllocatePassivesAsync", source, StringComparison.Ordinal);
        Assert.Contains("Saves.Atomically", source, StringComparison.Ordinal);
        Assert.Contains("InstanceItems.InventoryChange", source, StringComparison.Ordinal);
        Assert.Contains("Game.SendTabChange", source, StringComparison.Ordinal);
        Assert.Contains("MapUpgrade_BaseItemTypesKey", source, StringComparison.Ordinal);
        Assert.Contains("items.Pay(PlayerInventories.MainInventory", source, StringComparison.Ordinal);
        Assert.Contains("stash.Pay(orbType.Hash", source, StringComparison.Ordinal);
        Assert.Contains("stash.Inventory(sourceTabIndex).Items", source, StringComparison.Ordinal);
        Assert.Contains("items.FreeCell(PlayerInventories.MainInventory", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetAwaiter().GetResult()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void StashQolLocalExileEntry_CompilesCleanlyViaRoslynAgainstGatewayAssemblies()
    {
        string? gatewayBin = FindLocalExileGatewayBinDir();
        string? gameDll = FindLocalExileGameDll();
        if (gatewayBin is null || gameDll is null)
        {
            return;
        }

        string repoRoot = FindRepoRoot();
        string entryPath = Path.Combine(repoRoot, "examples", "DualMod.StashQol", "Server", "StashQolLocalExileEntry.cs");
        string source = File.ReadAllText(entryPath);

        CompileViaRoslynAndAssertSuccess(
            gatewayBin,
            "StashQolLocalExileEntryProbe",
            source,
            gameDll,
            typeof(SdkVersion).Assembly.Location,
            typeof(ClientBridgeHost).Assembly.Location,
            typeof(StashQolProtocol).Assembly.Location,
            typeof(StashQolServerMod).Assembly.Location);
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
            Type nullableOptsType = roslynCSharp.GetType("Microsoft.CodeAnalysis.NullableContextOptions")!;
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
            if (File.Exists(Path.Combine(dir, "NativeMods.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException("Could not locate NativeMods repository root.");
    }
}
