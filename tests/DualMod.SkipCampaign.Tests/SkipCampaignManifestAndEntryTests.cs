using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using DualMod.SkipCampaign.Server;
using DualMod.SkipCampaign.Shared;
using LocalExile.Client.Sdk;
using LocalExile.Client.ServerBridge;
using LocalExile.Client.Waypoint;
using Xunit;

namespace DualMod.SkipCampaign.Tests;

public sealed class SkipCampaignManifestAndEntryTests
{
    [Fact]
    public void ManifestAndPackageDirectory_ValidateCleanlyAgainstSdk030_AndRejectSdk020_WithZeroConflicts()
    {
        string repoRoot = TestPaths.FindRepoRoot();
        string packageDir = Path.Combine(repoRoot, "examples", "DualMod.SkipCampaign");

        DualModManifest manifest = DualModManifest.LoadFromFile(Path.Combine(packageDir, "mod.json"));
        ValidationReport manifestReport = DualModPackageValidator.ValidateManifest(manifest, SemVer.Parse(SdkVersion.Current));
        Assert.True(manifestReport.IsValid, FormatDiagnostics(manifestReport));
        Assert.Empty(manifestReport.Warnings);

        ValidationReport dirReport = DualModPackageValidator.ValidatePackageDirectory(packageDir, SemVer.Parse(SdkVersion.Current));
        Assert.True(dirReport.IsValid, FormatDiagnostics(dirReport));
        Assert.Empty(dirReport.Warnings);

        Assert.Equal(SkipCampaignProtocol.ModId, manifest.Id);
        Assert.Equal("skip-campaign.dll", manifest.EffectiveAssembly);
        Assert.NotNull(manifest.NativeClient);
        Assert.Equal(">=0.3.0 <2.0.0", manifest.NativeClient!.SdkVersionRange);
        Assert.Equal("skip-campaign.Client.dll", manifest.NativeClient.Assembly);
        Assert.False(manifest.NativeClient.RawAccess);
        Assert.Contains(manifest.NativeClient.Overlays, p => p.Equals(SkipCampaignProtocol.CreateCharacterBarUiPath, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(manifest.NativeClient.ServerHalf);
        Assert.True(manifest.NativeClient.ServerHalf!.SupportsGracefulFallback);

        // Minimum range >=0.3.0 rejects 0.2.0 cleanly.
        ValidationReport oldSdk = DualModPackageValidator.ValidateManifest(manifest, SemVer.Parse("0.2.0"));
        Assert.False(oldSdk.IsValid);
        Assert.Contains(oldSdk.Errors, e => e.Code == "NC002");

        // Zero conflicts with existing dual-side showcases.
        var siphon = DualModManifest.LoadFromFile(Path.Combine(repoRoot, "examples", "DualMod.SiphonOrb", "mod.json"));
        var maxwell = DualModManifest.LoadFromFile(Path.Combine(repoRoot, "examples", "DualMod.MaxwellPet", "mod.json"));
        var garden = DualModManifest.LoadFromFile(Path.Combine(repoRoot, "examples", "DualMod.HarvestGarden", "mod.json"));
        Assert.Empty(DualModPackageValidator.DetectConflicts([garden, maxwell, siphon, manifest]));
    }

    [Fact]
    public void CreateCharacterSkipBarUi_Passes329UiLayoutLinterWithZeroDiagnostics()
    {
        string repoRoot = TestPaths.FindRepoRoot();
        string uiPath = Path.Combine(
            repoRoot,
            "examples",
            "DualMod.SkipCampaign",
            "client",
            "Metadata",
            "UI",
            "Mods",
            "skip-campaign",
            "CreateCharacterSkipBar.ui");
        Assert.True(File.Exists(uiPath), $"Missing UI file at {uiPath}");

        string content = File.ReadAllText(uiPath);
        IReadOnlyList<ValidationDiagnostic> diagnostics = UiLayoutLinter.Lint(content, SkipCampaignProtocol.CreateCharacterBarUiPath);
        Assert.Empty(diagnostics);

        Assert.Contains($"id = {SkipCampaignProtocol.CreateCharacterBarSlotId};", content, StringComparison.Ordinal);
        Assert.Contains($"id = {SkipCampaignProtocol.CreateCharacterCheckBoxId};", content, StringComparison.Ordinal);
        Assert.Contains($"id = {SkipCampaignProtocol.CreateCharacterStatusLabelId};", content, StringComparison.Ordinal);
    }

    [Fact]
    public void SkipCampaignLocalExileEntry_FollowsConventions_AndCompilesViaRoslynWhenGatewayBinariesPresent()
    {
        string repoRoot = TestPaths.FindRepoRoot();
        string entryPath = Path.Combine(repoRoot, "examples", "DualMod.SkipCampaign", "Server", "SkipCampaignLocalExileEntry.cs");
        string source = File.ReadAllText(entryPath);

        Assert.Contains("class SkipCampaignLocalExileEntry : IMod, IDisposable", source, StringComparison.Ordinal);
        Assert.Contains("GameEvents.CharacterCreated.Add", source, StringComparison.Ordinal);
        Assert.Contains("CharacterSaves.GetLeagueCharacters", source, StringComparison.Ordinal);
        Assert.Contains("LocalExileGatewayHost.Create", source, StringComparison.Ordinal);
        Assert.Contains("Program.WorldOf(player)", source, StringComparison.Ordinal);
        Assert.Contains("SetQuestFlagsAsync", source, StringComparison.Ordinal);
        Assert.Contains("GrantWaypointsAsync", source, StringComparison.Ordinal);
        Assert.Contains("SetRefundPointsAsync", source, StringComparison.Ordinal);
        Assert.Contains("AwardAscendancyAsync", source, StringComparison.Ordinal);
        Assert.Contains("AddGoldAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetAwaiter().GetResult()", source, StringComparison.Ordinal);

        string? gatewayBin = FindLocalExileGatewayBinDir();
        if (gatewayBin is null)
        {
            return;
        }

        CompileViaRoslyn(
            gatewayBin,
            "SkipCampaignLocalExileEntryProbe",
            source,
            typeof(SdkVersion).Assembly.Location,
            typeof(ClientBridgeHost).Assembly.Location,
            typeof(SkipCampaignProtocol).Assembly.Location,
            typeof(SkipCampaignHandler).Assembly.Location);
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

    private static void CompileViaRoslyn(
        string gatewayBinDir,
        string assemblyName,
        string source,
        params string[] extraReferencePaths)
    {
        var roslynAlc = new AssemblyLoadContext("SkipCampaignRoslynProbe-" + Guid.NewGuid().ToString("N"), isCollectible: true);
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

            string[] presentPaths = refPaths.Where(File.Exists).ToArray();
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
            string[] diags = ((IEnumerable)emitResult.GetType().GetProperty("Diagnostics")!.GetValue(emitResult)!)
                .Cast<object>()
                .Select(d => d.ToString() ?? string.Empty)
                .ToArray();
            Assert.True(success, $"Roslyn compile ({assemblyName}) failed:\n" + string.Join("\n", diags));
        }
        finally
        {
            roslynAlc.Unload();
        }
    }

    private static string FormatDiagnostics(ValidationReport report) =>
        string.Join(Environment.NewLine, report.Diagnostics.Select(d => $"{d.Severity} {d.Code}: {d.Message}"));
}

internal static class TestPaths
{
    public static string FindRepoRoot()
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

    public static string FindClientAssemblyPath()
    {
        string root = FindRepoRoot();
        string clientDir = Path.Combine(root, "examples", "DualMod.SkipCampaign", "Client", "bin");
        foreach (string config in new[] { "Release", "Debug" })
        {
            string candidate = Path.Combine(clientDir, config, "net10.0", "skip-campaign.Client.dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not locate compiled skip-campaign.Client.dll under '{clientDir}'.");
    }

    public static string StageClientModDirectory(string tempRoot)
    {
        string repoRoot = FindRepoRoot();
        string exampleRoot = Path.Combine(repoRoot, "examples", "DualMod.SkipCampaign");
        string modDir = Path.Combine(tempRoot, SkipCampaignProtocol.ModId);
        Directory.CreateDirectory(modDir);

        File.Copy(Path.Combine(exampleRoot, "mod.json"), Path.Combine(modDir, "mod.json"), overwrite: true);
        File.Copy(FindClientAssemblyPath(), Path.Combine(modDir, "skip-campaign.Client.dll"), overwrite: true);

        string srcClientPayload = Path.Combine(exampleRoot, "client");
        if (Directory.Exists(srcClientPayload))
        {
            foreach (string file in Directory.EnumerateFiles(srcClientPayload, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(srcClientPayload, file);
                string dst = Path.Combine(modDir, "client", rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(file, dst, overwrite: true);
            }
        }

        return modDir;
    }
}
