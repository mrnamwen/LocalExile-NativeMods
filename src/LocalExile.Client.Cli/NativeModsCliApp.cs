using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalExile.Client.AssetPipeline.Dat64;
using LocalExile.Client.Sdk;
using LocalExile.Client.Waypoint;

namespace LocalExile.Client.Cli;

/// <summary>
/// Main entry point and command dispatcher for the <c>nativemods</c> developer CLI.
/// Supports <c>new</c> (<c>tier1</c>, <c>dual</c>, <c>content</c>), <c>validate</c>, and <c>merge</c>.
/// </summary>
public static class NativeModsCliApp
{
    public const int ExitSuccess = 0;
    public const int ExitFailure = 1;
    public const int ExitUsageError = 2;

    private static readonly JsonSerializerOptions JsonOutputOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage(stdout);
            return ExitSuccess;
        }

        if (args[0] is "-v" or "--version" or "version")
        {
            stdout.WriteLine($"nativemods {SdkVersion.Current}");
            return ExitSuccess;
        }

        string command = args[0].ToLowerInvariant();
        string[] rest = args[1..];

        try
        {
            return command switch
            {
                "new" => RunNew(rest, stdout, stderr),
                "validate" => RunValidate(rest, stdout, stderr),
                "merge" => RunMerge(rest, stdout, stderr),
                _ => UnknownCommand(args[0], stderr),
            };
        }
        catch (CliUsageException ex)
        {
            stderr.WriteLine($"Error: {ex.Message}");
            return ExitUsageError;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException or JsonException)
        {
            stderr.WriteLine($"Error: {ex.Message}");
            return ExitFailure;
        }
    }

    private static int RunNew(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintNewUsage(stdout);
            return ExitSuccess;
        }

        if (args.Length < 2)
        {
            throw new CliUsageException("Usage: nativemods new <tier1|dual|content> <ModId> [--out <dir>] [--name <displayName>] [--author <author>] [--force]");
        }

        ModTemplateKind template = args[0].ToLowerInvariant() switch
        {
            "tier1" => ModTemplateKind.Tier1,
            "dual" => ModTemplateKind.Dual,
            "content" => ModTemplateKind.Content,
            _ => throw new CliUsageException($"Unknown template '{args[0]}'. Expected one of: tier1, dual, content."),
        };

        string rawModId = args[1];
        string? outDir = null;
        string? displayName = null;
        string? author = null;
        bool force = false;

        for (int i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out":
                case "-o":
                    outDir = RequireValue(args, ref i, args[i]);
                    break;
                case "--name":
                    displayName = RequireValue(args, ref i, args[i]);
                    break;
                case "--author":
                    author = RequireValue(args, ref i, args[i]);
                    break;
                case "--force":
                case "-f":
                    force = true;
                    break;
                default:
                    throw new CliUsageException($"Unrecognized option '{args[i]}' for 'nativemods new'.");
            }
        }

        string canonicalId = ModScaffolder.NormalizeCanonicalId(rawModId);
        string targetDir = string.IsNullOrWhiteSpace(outDir)
            ? Path.Combine(Directory.GetCurrentDirectory(), string.IsNullOrWhiteSpace(canonicalId) ? rawModId : canonicalId)
            : outDir;

        ScaffoldResult result = ModScaffolder.Scaffold(template, rawModId, targetDir, displayName, author, force);

        stdout.WriteLine($"Scaffolded {result.Template.ToString().ToLowerInvariant()} mod '{result.ModId}' in '{result.OutputDirectory}':");
        foreach (string file in result.CreatedFiles)
        {
            stdout.WriteLine($"  + {file}");
        }

        return ExitSuccess;
    }

    private static int RunValidate(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintValidateUsage(stdout);
            return ExitSuccess;
        }

        string? modDir = null;
        var againstDirs = new List<string>();
        string? schemaPath = null;
        string? sdkVersionArg = null;
        bool jsonOutput = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "--against" or "-a")
            {
                string raw = RequireValue(args, ref i, arg);
                foreach (string item in raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    againstDirs.Add(item);
                }
            }
            else if (arg is "--schema")
            {
                schemaPath = RequireValue(args, ref i, arg);
            }
            else if (arg is "--sdk-version")
            {
                sdkVersionArg = RequireValue(args, ref i, arg);
            }
            else if (arg is "--json")
            {
                jsonOutput = true;
            }
            else if (arg.StartsWith('-'))
            {
                throw new CliUsageException($"Unrecognized option '{arg}' for 'nativemods validate'.");
            }
            else if (modDir is null)
            {
                modDir = arg;
            }
            else
            {
                throw new CliUsageException($"Unexpected positional argument '{arg}' for 'nativemods validate'.");
            }
        }

        if (string.IsNullOrWhiteSpace(modDir))
        {
            throw new CliUsageException("Usage: nativemods validate <modDir> [--against <otherModDir>...] [--schema <schema.json>] [--sdk-version <ver>] [--json]");
        }

        SemVer currentSdk = SemVer.Parse(SdkVersion.Current);
        if (!string.IsNullOrWhiteSpace(sdkVersionArg))
        {
            if (!SemVer.TryParse(sdkVersionArg, out currentSdk))
            {
                throw new CliUsageException($"Invalid --sdk-version '{sdkVersionArg}'. Expected SemVer such as '0.4.0' or '1.0.0'.");
            }
        }

        Dat64SchemaRegistry schema = string.IsNullOrWhiteSpace(schemaPath)
            ? Dat64SchemaRegistry.LoadDefault329()
            : Dat64SchemaRegistry.LoadFromFile(schemaPath);

        string fullModDir = Path.GetFullPath(modDir);

        // 1. Package & manifest & payload .ui validation via DualModPackageValidator.
        ValidationReport pkgReport = DualModPackageValidator.ValidatePackageDirectory(fullModDir, currentSdk);
        var allDiagnostics = new List<ValidationDiagnostic>(pkgReport.Diagnostics);
        var allConflicts = new List<CrossModConflict>();

        // 2. Lint any stray .ui files outside payloadDir that DualModPackageValidator did not inspect.
        if (Directory.Exists(fullModDir))
        {
            DualModManifest? primaryManifest = TryLoadManifest(fullModDir);
            string payloadSubDir = primaryManifest?.NativeClient?.PayloadDir is { Length: > 0 } pd ? pd : "client";
            string payloadFullDir = Path.GetFullPath(Path.Combine(fullModDir, payloadSubDir));

            foreach (string uiFile in EnumerateProjectFiles(fullModDir, "*.ui"))
            {
                string fullUi = Path.GetFullPath(uiFile);
                bool insidePayload = fullUi.StartsWith(payloadFullDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                if (insidePayload && primaryManifest?.HasNativeClient == true)
                {
                    continue;
                }

                string relPath = Path.GetRelativePath(fullModDir, fullUi).Replace('\\', '/');
                string content = DualModPackageValidator.ReadTextAutoEncoding(fullUi);
                allDiagnostics.AddRange(UiLayoutLinter.Lint(content, relPath, primaryManifest?.NativeClient?.CustomControls));
            }
        }

        // 3. Validate .datpatch.json files (and cross-mod conflicts against --against dirs).
        List<string> primaryPatchFiles = Directory.Exists(fullModDir)
            ? EnumerateProjectFiles(fullModDir, "*.datpatch.json")
            : [];
        var companionPatchFiles = new List<string>();
        var manifestsForConflictCheck = new List<DualModManifest>();

        if (Directory.Exists(fullModDir) && TryLoadManifest(fullModDir) is { } mainManifest)
        {
            manifestsForConflictCheck.Add(mainManifest);
        }

        foreach (string otherDir in againstDirs)
        {
            string fullOther = Path.GetFullPath(otherDir);
            if (!Directory.Exists(fullOther))
            {
                allDiagnostics.Add(new(DiagnosticSeverity.Error, "CLI001", $"Companion --against directory not found: '{otherDir}'.", otherDir));
                continue;
            }

            if (TryLoadManifest(fullOther) is { } otherManifest)
            {
                manifestsForConflictCheck.Add(otherManifest);
            }
            else
            {
                allDiagnostics.Add(new(DiagnosticSeverity.Error, "CLI002", $"Companion --against directory '{otherDir}' is missing a valid mod.json.", otherDir));
            }

            companionPatchFiles.AddRange(EnumerateProjectFiles(fullOther, "*.datpatch.json"));
        }

        if (manifestsForConflictCheck.Count > 1)
        {
            allConflicts.AddRange(DualModPackageValidator.DetectConflicts(manifestsForConflictCheck));
        }

        if (primaryPatchFiles.Count > 0 || companionPatchFiles.Count > 0)
        {
            DatPatchValidationResult patchResult = DatPatchValidator.ValidateFiles(primaryPatchFiles, companionPatchFiles, schema);
            allDiagnostics.AddRange(patchResult.Diagnostics);
            allConflicts.AddRange(patchResult.Conflicts);
        }

        int errorCount = allDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        int warningCount = allDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
        bool isValid = errorCount == 0 && allConflicts.Count == 0;

        if (jsonOutput)
        {
            var payload = new
            {
                Valid = isValid,
                ModDirectory = fullModDir,
                ErrorCount = errorCount,
                WarningCount = warningCount,
                ConflictCount = allConflicts.Count,
                PatchFileCount = primaryPatchFiles.Count,
                Diagnostics = allDiagnostics,
                Conflicts = allConflicts,
            };
            stdout.WriteLine(JsonSerializer.Serialize(payload, JsonOutputOptions));
        }
        else
        {
            foreach (ValidationDiagnostic diag in allDiagnostics)
            {
                string loc = diag.FilePath is not null
                    ? (diag.Line.HasValue ? $" ({diag.FilePath}:{diag.Line.Value})" : $" ({diag.FilePath})")
                    : string.Empty;
                TextWriter target = diag.Severity == DiagnosticSeverity.Error ? stderr : stdout;
                target.WriteLine($"[{diag.Severity.ToString().ToUpperInvariant()}] {diag.Code}{loc}: {diag.Message}");
            }

            foreach (CrossModConflict conflict in allConflicts)
            {
                stderr.WriteLine($"[CONFLICT] {conflict.Code} ({conflict.Kind} '{conflict.Resource}', {conflict.ModA} vs {conflict.ModB}): {conflict.Message}");
            }

            string status = isValid ? "VALID" : "FAILED";
            stdout.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Validation {status}: {errorCount} error(s), {warningCount} warning(s), {allConflicts.Count} conflict(s), {primaryPatchFiles.Count} .datpatch.json file(s) checked."));
        }

        return isValid ? ExitSuccess : ExitFailure;
    }

    private static int RunMerge(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintMergeUsage(stdout);
            return ExitSuccess;
        }

        string? stockDir = null;
        string? outDir = null;
        string? schemaPath = null;
        bool jsonOutput = false;
        var inputs = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "--stock" or "--client")
            {
                stockDir = RequireValue(args, ref i, arg);
            }
            else if (arg is "--out" or "-o")
            {
                outDir = RequireValue(args, ref i, arg);
            }
            else if (arg is "--schema")
            {
                schemaPath = RequireValue(args, ref i, arg);
            }
            else if (arg is "--patches")
            {
                string raw = RequireValue(args, ref i, arg);
                foreach (string item in raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    inputs.Add(item);
                }
            }
            else if (arg is "--json")
            {
                jsonOutput = true;
            }
            else if (arg.StartsWith('-'))
            {
                throw new CliUsageException($"Unrecognized option '{arg}' for 'nativemods merge'.");
            }
            else
            {
                inputs.Add(arg);
            }
        }

        if (string.IsNullOrWhiteSpace(stockDir))
        {
            throw new CliUsageException("Missing required '--stock <stockDir>' option.");
        }

        if (string.IsNullOrWhiteSpace(outDir))
        {
            throw new CliUsageException("Missing required '--out <mergedCacheDir>' option.");
        }

        if (inputs.Count == 0)
        {
            throw new CliUsageException("At least one <patchOrModDir> argument is required.");
        }

        List<string> patchFiles = ResolvePatchInputPaths(inputs);
        if (patchFiles.Count == 0)
        {
            throw new InvalidOperationException("No *.datpatch.json files were found in the supplied input path(s).");
        }

        List<DatPatchDocument> docs = patchFiles.Select(DatPatchDocument.LoadFromFile).ToList();
        Dat64SchemaRegistry schema = string.IsNullOrWhiteSpace(schemaPath)
            ? Dat64SchemaRegistry.LoadDefault329()
            : Dat64SchemaRegistry.LoadFromFile(schemaPath);

        string resolvedStockDir = ResolveStockRootDirectory(stockDir);
        using var source = new DirectoryDat64Source(resolvedStockDir);
        DatPatchMergeResult result = DatPatchMerger.Merge(source, docs, schema, outDir);

        if (jsonOutput)
        {
            var payload = new
            {
                OutputDirectory = Path.GetFullPath(outDir),
                Digest = result.RowMap.Digest,
                Mods = result.RowMap.Mods,
                Tables = result.RowMap.Tables.ToDictionary(
                    kv => kv.Key,
                    kv => new
                    {
                        kv.Value.StockRowCount,
                        kv.Value.MergedRowCount,
                        EntryCount = kv.Value.Entries.Count,
                        kv.Value.TableSha256,
                    }),
                GeneratedOverlays = result.RowMap.GeneratedOverlays,
            };
            stdout.WriteLine(JsonSerializer.Serialize(payload, JsonOutputOptions));
        }
        else
        {
            stdout.WriteLine($"Merged {docs.Count} mod patch(es) into '{Path.GetFullPath(outDir)}':");
            stdout.WriteLine($"  Mods:   {string.Join(", ", result.RowMap.Mods)}");
            stdout.WriteLine($"  Digest: {result.RowMap.Digest}");
            foreach (var (tableName, tbl) in result.RowMap.Tables)
            {
                stdout.WriteLine($"  Table   Data/{tableName}.datc64: {tbl.StockRowCount} -> {tbl.MergedRowCount} rows ({tbl.Entries.Count} mapped, sha256={tbl.TableSha256[..12]}...)");
            }

            foreach (GeneratedOverlayEntry ov in result.RowMap.GeneratedOverlays)
            {
                stdout.WriteLine($"  Overlay {ov.VirtualPath} (sha256={ov.Sha256[..12]}...)");
            }

            stdout.WriteLine($"  RowMap  {Path.Combine(Path.GetFullPath(outDir), "content-row-map.json")}");
        }

        _ = stderr;
        return ExitSuccess;
    }

    /// <summary>
    /// Normalizes a <c>--stock</c> directory so callers can pass either a PoE client root containing
    /// <c>Bundles2/_.index.bin</c>, a <c>Bundles2/</c> directory directly containing <c>_.index.bin</c>,
    /// or a directory of loose <c>Data/*.datc64</c> / <c>*.datc64</c> files.
    /// </summary>
    public static string ResolveStockRootDirectory(string stockDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stockDir);
        string full = Path.GetFullPath(stockDir);
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"Stock directory not found: '{stockDir}'.");
        }

        if (File.Exists(Path.Combine(full, "_.index.bin")) &&
            string.Equals(Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), "Bundles2", StringComparison.OrdinalIgnoreCase))
        {
            string? parent = Path.GetDirectoryName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrEmpty(parent))
            {
                return parent;
            }
        }

        return full;
    }

    private static List<string> ResolvePatchInputPaths(IReadOnlyList<string> inputs)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();

        foreach (string input in inputs)
        {
            if (File.Exists(input))
            {
                string full = Path.GetFullPath(input);
                if (seen.Add(full))
                {
                    files.Add(full);
                }
            }
            else if (Directory.Exists(input))
            {
                foreach (string patchFile in EnumerateProjectFiles(input, "*.datpatch.json"))
                {
                    string full = Path.GetFullPath(patchFile);
                    if (seen.Add(full))
                    {
                        files.Add(full);
                    }
                }
            }
            else
            {
                throw new FileNotFoundException($"Patch file or mod directory not found: '{input}'.", input);
            }
        }

        return files;
    }

    private static List<string> EnumerateProjectFiles(string rootDir, string searchPattern)
    {
        var results = new List<string>();
        foreach (string file in Directory.EnumerateFiles(rootDir, searchPattern, SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(rootDir, file);
            string[] segments = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(s => s is "bin" or "obj" or ".git" or ".vs" or ".merged-cache"))
            {
                continue;
            }

            results.Add(file);
        }

        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    private static DualModManifest? TryLoadManifest(string modDir)
    {
        string path = Path.Combine(modDir, "mod.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return DualModManifest.LoadFromFile(path);
        }
        catch
        {
            return null;
        }
    }

    private static string RequireValue(string[] args, ref int index, string flagName)
    {
        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            throw new CliUsageException($"Option '{flagName}' requires a value.");
        }

        index++;
        return args[index];
    }

    private static int UnknownCommand(string cmd, TextWriter stderr)
    {
        stderr.WriteLine($"Unknown command '{cmd}'. Run 'nativemods --help' for usage.");
        return ExitUsageError;
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine($"nativemods {SdkVersion.Current} — LocalExile NativeMods Developer CLI");
        writer.WriteLine();
        writer.WriteLine("Commands:");
        writer.WriteLine("  nativemods new tier1 <ModId> [--out <dir>] [--name <title>] [--author <name>] [--force]");
        writer.WriteLine("  nativemods new dual <ModId> [--out <dir>] [--name <title>] [--author <name>] [--force]");
        writer.WriteLine("  nativemods new content <ModId> [--out <dir>] [--name <title>] [--author <name>] [--force]");
        writer.WriteLine("  nativemods validate <modDir> [--against <otherModDir>...] [--schema <schema.json>] [--sdk-version <ver>] [--json]");
        writer.WriteLine("  nativemods merge --stock <stockDir> --out <mergedCacheDir> [--schema <schema.json>] [--json] <patchOrModDir>...");
    }

    private static void PrintNewUsage(TextWriter writer)
    {
        writer.WriteLine("Usage: nativemods new <tier1|dual|content> <ModId> [--out <dir>] [--name <title>] [--author <name>] [--force]");
    }

    private static void PrintValidateUsage(TextWriter writer)
    {
        writer.WriteLine("Usage: nativemods validate <modDir> [--against <otherModDir>...] [--schema <schema.json>] [--sdk-version <ver>] [--json]");
    }

    private static void PrintMergeUsage(TextWriter writer)
    {
        writer.WriteLine("Usage: nativemods merge --stock <stockDir> --out <mergedCacheDir> [--schema <schema.json>] [--json] <patchOrModDir>...");
    }

    private sealed class CliUsageException(string message) : Exception(message);
}
