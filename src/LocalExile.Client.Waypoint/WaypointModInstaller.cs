using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalExile.Client.Waypoint;

/// <summary>
/// Options controlling how <see cref="WaypointModInstaller"/> deploys a dual-side mod.
/// </summary>
public sealed record InstallOptions
{
    /// <summary>Current NativeMods Client SDK version used to check <c>nativeClient.sdkVersionRange</c>.</summary>
    public SemVer? CurrentSdkVersion { get; init; }

    /// <summary>Whether to also pack <c>client/</c> into a deterministic <c>.bundle.bin</c> + <c>manifest.json</c> under <c>Mods/Client/&lt;id&gt;/</c>.</summary>
    public bool PackClientBundle { get; init; } = true;

    /// <summary>Whether to also deploy loose UTF-16LE BOM CRLF text overlays under <c>Mods/Client/&lt;id&gt;/files/</c>.</summary>
    public bool DeployLooseOverlays { get; init; } = true;

    /// <summary>When false, cross-mod conflicts abort installation rather than warning.</summary>
    public bool AllowConflicts { get; init; }

    /// <summary>When false, mods requesting <c>rawAccess: true</c> are blocked until explicitly approved by the player.</summary>
    public bool AllowRawAccess { get; init; } = true;

    /// <summary>Whether to enable the mod in <c>Mods/Binaries/mod-state.txt</c> upon install (null uses manifest default).</summary>
    public bool? EnableOnInstall { get; init; }
}

/// <summary>A single file recorded in the deployment ledger.</summary>
public sealed record DeployedFileRecord(
    [property: JsonPropertyName("relativePath")] string RelativePath,
    [property: JsonPropertyName("area")] string Area,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes);

/// <summary>Deployment record for one installed dual-side mod in <c>Mods/Client/.nativemods-ledger.json</c>.</summary>
public sealed record InstalledDualModRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("installedUtc")] DateTimeOffset InstalledUtc,
    [property: JsonPropertyName("serverDir")] string ServerDir,
    [property: JsonPropertyName("clientDir")] string? ClientDir,
    [property: JsonPropertyName("bundleFile")] string? BundleFile,
    [property: JsonPropertyName("rawAccess")] bool RawAccess,
    [property: JsonPropertyName("rawAccessJustification")] string? RawAccessJustification,
    [property: JsonPropertyName("replaces")] IReadOnlyList<string> Replaces,
    [property: JsonPropertyName("overlays")] IReadOnlyList<string> Overlays,
    [property: JsonPropertyName("customOpcodes")] IReadOnlyList<CustomOpcodeDeclaration> CustomOpcodes,
    [property: JsonPropertyName("gealBuiltins")] IReadOnlyList<string> GealBuiltins,
    [property: JsonPropertyName("customControls")] IReadOnlyList<string> CustomControls,
    [property: JsonPropertyName("contentRegistries")] IReadOnlyList<ContentRegistryDeclaration> ContentRegistries,
    [property: JsonPropertyName("manifest")] DualModManifest Manifest,
    [property: JsonPropertyName("files")] IReadOnlyList<DeployedFileRecord> Files);

/// <summary>Persistent deployment ledger stored at <c>&lt;serverRoot&gt;/Mods/Client/.nativemods-ledger.json</c>.</summary>
public sealed record DeploymentLedger(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("updatedUtc")] DateTimeOffset UpdatedUtc,
    [property: JsonPropertyName("mods")] IReadOnlyList<InstalledDualModRecord> Mods);

/// <summary>
/// Human-readable and machine-readable audit report returned by <see cref="WaypointModInstaller"/>
/// before or after installing a dual-side mod.
/// </summary>
public sealed record InstallAuditReport(
    bool Succeeded,
    bool IsUpgrade,
    string? PreviousVersion,
    string ModId,
    string ModName,
    string Version,
    bool HasNativeClient,
    bool RawAccessRequested,
    string? RawAccessJustification,
    IReadOnlyList<string> ReplacedStockPaths,
    IReadOnlyList<string> AddedOverlayPaths,
    IReadOnlyList<CustomOpcodeDeclaration> CustomOpcodes,
    IReadOnlyList<string> GealBuiltins,
    IReadOnlyList<string> CustomControls,
    IReadOnlyList<ContentRegistryDeclaration> ContentRegistries,
    ValidationReport Validation,
    IReadOnlyList<CrossModConflict> Conflicts,
    IReadOnlyList<DeployedFileRecord> DeployedFiles,
    string? ServerTargetDir,
    string? ClientTargetDir,
    string? BundleFile,
    string? FailureReason = null)
{
    public string ToHumanReadableSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== NativeMods Install Audit: {ModName} ({ModId} v{Version}) ===");
        sb.AppendLine($"Outcome: {(Succeeded ? (IsUpgrade ? $"UPGRADED (from v{PreviousVersion})" : "INSTALLED") : $"BLOCKED - {FailureReason}")}");
        sb.AppendLine($"Native Client Half: {(HasNativeClient ? "Yes" : "No (server-only)")}");
        sb.AppendLine($"Raw Memory/Hook Access: {(RawAccessRequested ? $"YES — {RawAccessJustification}" : "No")}");

        if (ReplacedStockPaths.Count > 0)
        {
            sb.AppendLine($"Replaced Stock Client Paths ({ReplacedStockPaths.Count}):");
            foreach (string path in ReplacedStockPaths)
                sb.AppendLine($"  [OVERRIDE] {path}");
        }
        else
        {
            sb.AppendLine("Replaced Stock Client Paths: None");
        }

        if (AddedOverlayPaths.Count > 0)
            sb.AppendLine($"Added Namespaced Overlays: {AddedOverlayPaths.Count}");
        if (CustomOpcodes.Count > 0)
            sb.AppendLine($"Custom Opcodes: {string.Join(", ", CustomOpcodes.Select(o => $"{o.Name} (0x{o.Id:X4}, {o.Direction})"))}");
        if (GealBuiltins.Count > 0)
            sb.AppendLine($"GEAL Built-ins: {string.Join(", ", GealBuiltins)}");
        if (CustomControls.Count > 0)
            sb.AppendLine($"Custom UI Controls: {string.Join(", ", CustomControls)}");
        if (ContentRegistries.Count > 0)
            sb.AppendLine($"Content Registries: {string.Join(", ", ContentRegistries.Select(r => $"{r.Table}:{r.EntryId} ({r.Mode})"))}");

        if (Conflicts.Count > 0)
        {
            sb.AppendLine($"Cross-Mod Conflicts ({Conflicts.Count}):");
            foreach (var c in Conflicts)
                sb.AppendLine($"  [{c.Code}] {c.Message}");
        }
        else
        {
            sb.AppendLine("Cross-Mod Conflicts: None");
        }

        int errors = Validation.Errors.Count();
        int warnings = Validation.Warnings.Count();
        sb.AppendLine($"Validation: {errors} error(s), {warnings} warning(s)");
        foreach (var diag in Validation.Diagnostics.Where(d => d.Severity != DiagnosticSeverity.Info))
        {
            string loc = diag.FilePath is not null ? $" ({diag.FilePath}{(diag.Line is not null ? $":{diag.Line}" : "")})" : "";
            sb.AppendLine($"  [{diag.Severity}] {diag.Code}{loc}: {diag.Message}");
        }

        if (Succeeded)
        {
            sb.AppendLine($"Deployed Files: {DeployedFiles.Count}");
            if (ServerTargetDir is not null) sb.AppendLine($"  Server Dir: {ServerTargetDir}");
            if (ClientTargetDir is not null) sb.AppendLine($"  Client Dir: {ClientTargetDir}");
            if (BundleFile is not null) sb.AppendLine($"  Packed Bundle: {BundleFile}");
        }

        return sb.ToString();
    }
}

/// <summary>Result of uninstalling a dual-side mod.</summary>
public sealed record UninstallResult(
    bool Succeeded,
    string ModId,
    bool RemovedServerDir,
    bool RemovedClientDir,
    string Message);

/// <summary>
/// Installs, upgrades, audits, and cleanly uninstalls dual-side mods into a LocalExile checkout's
/// <c>Mods/Binaries/&lt;id&gt;/</c> and <c>Mods/Client/&lt;id&gt;/</c> directories.
/// Never touches stock client files (<c>Client/Game.exe</c>, <c>Client/Content.ggpk</c>, <c>Client/Bundles2/</c>).
/// </summary>
public sealed class WaypointModInstaller
{
    public const string LedgerFileName = ".nativemods-ledger.json";
    public const string ModStateFileName = "mod-state.txt";

    public static string GetServerBinariesRoot(string localExileRoot) =>
        Path.Combine(localExileRoot, "Mods", "Binaries");

    public static string GetClientModsRoot(string localExileRoot) =>
        Path.Combine(localExileRoot, "Mods", "Client");

    public static string GetLedgerPath(string localExileRoot) =>
        Path.Combine(GetClientModsRoot(localExileRoot), LedgerFileName);

    /// <summary>Reads the deployment ledger, returning an empty ledger when none exists yet.</summary>
    public DeploymentLedger ReadLedger(string localExileRoot)
    {
        string path = GetLedgerPath(localExileRoot);
        if (!File.Exists(path))
            return new DeploymentLedger(1, DateTimeOffset.UtcNow, []);

        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<DeploymentLedger>(json, DualModManifest.SerializerOptions)
            ?? new DeploymentLedger(1, DateTimeOffset.UtcNow, []);
    }

    /// <summary>
    /// Audits a candidate dual-side mod package directory against the target LocalExile checkout without modifying disk.
    /// </summary>
    public InstallAuditReport AuditPackage(
        string packageDir,
        string localExileRoot,
        InstallOptions? options = null)
    {
        options ??= new InstallOptions();
        var validation = DualModPackageValidator.ValidatePackageDirectory(packageDir, options.CurrentSdkVersion);

        string manifestPath = Path.Combine(packageDir, "mod.json");
        if (!File.Exists(manifestPath))
        {
            return new InstallAuditReport(
                false, false, null, Path.GetFileName(packageDir), Path.GetFileName(packageDir), "0.0.0",
                false, false, null, [], [], [], [], [], [],
                validation, [], [], null, null, null, "Missing mod.json.");
        }

        DualModManifest manifest;
        try
        {
            manifest = DualModManifest.LoadFromFile(manifestPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            return new InstallAuditReport(
                false, false, null, Path.GetFileName(packageDir), Path.GetFileName(packageDir), "0.0.0",
                false, false, null, [], [], [], [], [], [],
                validation, [], [], null, null, null, $"Invalid mod.json: {ex.Message}");
        }

        var ledger = ReadLedger(localExileRoot);
        var existingRecord = ledger.Mods.FirstOrDefault(m => m.Id.Equals(manifest.Id, StringComparison.OrdinalIgnoreCase));
        var otherManifests = ledger.Mods
            .Where(m => !m.Id.Equals(manifest.Id, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Manifest)
            .Append(manifest)
            .ToList();

        var conflicts = DualModPackageValidator.DetectConflicts(otherManifests);
        var nc = manifest.NativeClient;

        string? failureReason = null;
        if (!validation.IsValid)
            failureReason = "Package validation failed with errors.";
        else if (nc is { RawAccess: true } && !options.AllowRawAccess)
            failureReason = "Mod requests RawAccess, which is not approved in InstallOptions.";
        else if (conflicts.Count > 0 && !options.AllowConflicts)
            failureReason = $"Detected {conflicts.Count} cross-mod conflict(s).";

        return new InstallAuditReport(
            Succeeded: failureReason is null,
            IsUpgrade: existingRecord is not null,
            PreviousVersion: existingRecord?.Version,
            ModId: manifest.Id,
            ModName: manifest.DisplayName,
            Version: manifest.Version,
            HasNativeClient: nc is not null,
            RawAccessRequested: nc?.RawAccess ?? false,
            RawAccessJustification: nc?.RawAccessJustification,
            ReplacedStockPaths: nc?.Replaces ?? [],
            AddedOverlayPaths: nc?.Overlays ?? [],
            CustomOpcodes: nc?.CustomOpcodes ?? [],
            GealBuiltins: nc?.GealBuiltins ?? [],
            CustomControls: nc?.CustomControls ?? [],
            ContentRegistries: nc?.ContentRegistries ?? [],
            Validation: validation,
            Conflicts: conflicts,
            DeployedFiles: [],
            ServerTargetDir: Path.Combine(GetServerBinariesRoot(localExileRoot), manifest.Id),
            ClientTargetDir: nc is not null ? Path.Combine(GetClientModsRoot(localExileRoot), manifest.Id) : null,
            BundleFile: null,
            FailureReason: failureReason);
    }

    /// <summary>
    /// Installs or upgrades a dual-side mod from a `.zip` release archive into the LocalExile checkout.
    /// </summary>
    public InstallAuditReport InstallFromZip(
        string zipPath,
        string localExileRoot,
        InstallOptions? options = null)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("Mod archive not found.", zipPath);

        long zipLen = new FileInfo(zipPath).Length;
        if (zipLen > DualModManifest.MaxPackageBytes)
            throw new InvalidDataException("Mod zip exceeds Waypoint's 64 MB limit.");

        string tempStage = Path.Combine(Path.GetTempPath(), "waypoint-nativemods-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempStage);
        try
        {
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var entries = zip.Entries
                    .Select(e => (Entry: e, Name: e.FullName.Replace('\\', '/')))
                    .Where(x => !x.Name.EndsWith('/'))
                    .ToList();

                if (entries.Count == 0)
                    throw new InvalidDataException("Zip archive contains no files.");
                if (entries.Count > DualModManifest.MaxPackageFiles)
                    throw new InvalidDataException($"Zip archive exceeds {DualModManifest.MaxPackageFiles} files.");

                // Strip single top-level wrapper directory if present (matching Waypoint ModRepository.Extract)
                string? singlePrefix = null;
                if (entries.All(x => x.Name.Contains('/')))
                {
                    string candidate = entries[0].Name.Split('/')[0] + "/";
                    if (entries.All(x => x.Name.StartsWith(candidate, StringComparison.Ordinal)))
                        singlePrefix = candidate;
                }

                long totalUncompressed = 0;
                foreach (var (entry, rawName) in entries)
                {
                    string rel = singlePrefix is not null ? rawName[singlePrefix.Length..] : rawName;
                    if (!DualModPackageValidator.IsSafeRelativeVirtualPath(rel))
                        throw new InvalidDataException($"Zip contains unsafe entry path '{rawName}'.");

                    totalUncompressed += entry.Length;
                    if (totalUncompressed > DualModManifest.MaxPackageBytes)
                        throw new InvalidDataException("Extracted mod size exceeds Waypoint's 64 MB limit.");

                    string dest = Path.Combine(tempStage, Path.Combine(rel.Split('/')));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, overwrite: true);
                }
            }

            return Install(tempStage, localExileRoot, options);
        }
        finally
        {
            try { Directory.Delete(tempStage, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Installs or cleanly upgrades a dual-side mod from <paramref name="packageDir"/> into <paramref name="localExileRoot"/>.
    /// </summary>
    public InstallAuditReport Install(
        string packageDir,
        string localExileRoot,
        InstallOptions? options = null)
    {
        options ??= new InstallOptions();
        if (!DualModPackageValidator.IsPhysicalPath(localExileRoot))
            throw new IOException("Redirected LocalExile root paths are not supported.");

        var audit = AuditPackage(packageDir, localExileRoot, options);
        if (!audit.Succeeded)
            return audit;

        var manifest = DualModManifest.LoadFromFile(Path.Combine(packageDir, "mod.json"));
        string serverRoot = GetServerBinariesRoot(localExileRoot);
        string clientRoot = GetClientModsRoot(localExileRoot);
        string serverTarget = Path.Combine(serverRoot, manifest.Id);
        string? clientTarget = manifest.NativeClient is not null ? Path.Combine(clientRoot, manifest.Id) : null;

        EnsureWithinDirectory(serverTarget, serverRoot);
        if (clientTarget is not null)
            EnsureWithinDirectory(clientTarget, clientRoot);

        // Preserve existing user settings.json values during an upgrade if present
        string? preservedSettings = null;
        string existingSettingsPath = Path.Combine(serverTarget, "settings.json");
        if (audit.IsUpgrade && File.Exists(existingSettingsPath))
            preservedSettings = File.ReadAllText(existingSettingsPath);

        Directory.CreateDirectory(serverRoot);
        var deployedFiles = new List<DeployedFileRecord>();

        // 1. Deploy server half cleanly into Mods/Binaries/<id>/
        string serverStaging = Path.Combine(serverRoot, $".staging-{manifest.Id}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(serverStaging);
        try
        {
            DeployServerFiles(packageDir, manifest, serverStaging);

            // Keep existing settings.json on upgrade if package didn't ship a fresh one
            if (preservedSettings is not null && !File.Exists(Path.Combine(serverStaging, "settings.json")))
                File.WriteAllText(Path.Combine(serverStaging, "settings.json"), preservedSettings, new UTF8Encoding(false));

            SwapDirectoryAtomically(serverStaging, serverTarget);

            foreach (string file in Directory.EnumerateFiles(serverTarget, "*", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                string rel = DualModPackageValidator.NormalizeVirtualPath(Path.GetRelativePath(serverTarget, file));
                deployedFiles.Add(new(rel, "server", DualModPackageValidator.ComputeFileSha256(file), new FileInfo(file).Length));
            }
        }
        finally
        {
            if (Directory.Exists(serverStaging))
                try { Directory.Delete(serverStaging, recursive: true); } catch { }
        }

        // 2. Deploy client half cleanly into Mods/Client/<id>/ (never touching Client/ stock files)
        string? deployedBundleFile = null;
        if (manifest.NativeClient is { } nc && clientTarget is not null)
        {
            Directory.CreateDirectory(clientRoot);
            string clientStaging = Path.Combine(clientRoot, $".staging-{manifest.Id}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(clientStaging);
            try
            {
                deployedBundleFile = DeployClientFiles(packageDir, manifest, nc, clientStaging, options);
                SwapDirectoryAtomically(clientStaging, clientTarget);

                foreach (string file in Directory.EnumerateFiles(clientTarget, "*", SearchOption.AllDirectories)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    string rel = DualModPackageValidator.NormalizeVirtualPath(Path.GetRelativePath(clientTarget, file));
                    deployedFiles.Add(new(rel, "client", DualModPackageValidator.ComputeFileSha256(file), new FileInfo(file).Length));
                }
            }
            finally
            {
                if (Directory.Exists(clientStaging))
                    try { Directory.Delete(clientStaging, recursive: true); } catch { }
            }
        }
        else if (audit.IsUpgrade)
        {
            // Upgraded from dual-side to server-only: remove old client folder
            string oldClientDir = Path.Combine(clientRoot, manifest.Id);
            if (Directory.Exists(oldClientDir))
                Directory.Delete(oldClientDir, recursive: true);
        }

        // 3. Update Waypoint-compatible Mods/Binaries/mod-state.txt
        bool enableState = options.EnableOnInstall ?? manifest.Enabled;
        WriteModStateChoice(serverRoot, manifest.Id, enableState);

        // 4. Persist deployment ledger
        var ledger = ReadLedger(localExileRoot);
        var updatedMods = ledger.Mods
            .Where(m => !m.Id.Equals(manifest.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        updatedMods.Add(new InstalledDualModRecord(
            manifest.Id,
            manifest.DisplayName,
            manifest.Version,
            DateTimeOffset.UtcNow,
            DualModPackageValidator.NormalizeVirtualPath(Path.GetRelativePath(localExileRoot, serverTarget)),
            clientTarget is not null ? DualModPackageValidator.NormalizeVirtualPath(Path.GetRelativePath(localExileRoot, clientTarget)) : null,
            deployedBundleFile,
            manifest.NativeClient?.RawAccess ?? false,
            manifest.NativeClient?.RawAccessJustification,
            manifest.NativeClient?.Replaces ?? [],
            manifest.NativeClient?.Overlays ?? [],
            manifest.NativeClient?.CustomOpcodes ?? [],
            manifest.NativeClient?.GealBuiltins ?? [],
            manifest.NativeClient?.CustomControls ?? [],
            manifest.NativeClient?.ContentRegistries ?? [],
            manifest,
            deployedFiles));

        updatedMods = updatedMods.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();
        WriteLedger(localExileRoot, new DeploymentLedger(1, DateTimeOffset.UtcNow, updatedMods));

        return audit with
        {
            Succeeded = true,
            DeployedFiles = deployedFiles,
            BundleFile = deployedBundleFile
        };
    }

    /// <summary>
    /// Cleanly uninstalls a dual-side mod from both <c>Mods/Binaries/&lt;id&gt;/</c> and <c>Mods/Client/&lt;id&gt;/</c>,
    /// removes it from <c>mod-state.txt</c> and the deployment ledger, and never touches stock client files.
    /// </summary>
    public UninstallResult Uninstall(string modId, string localExileRoot)
    {
        if (!DualModManifest.IsValidId(modId))
            return new(false, modId, false, false, $"Invalid mod id '{modId}'.");

        string serverRoot = GetServerBinariesRoot(localExileRoot);
        string clientRoot = GetClientModsRoot(localExileRoot);
        string serverDir = Path.Combine(serverRoot, modId);
        string clientDir = Path.Combine(clientRoot, modId);

        EnsureWithinDirectory(serverDir, serverRoot);
        EnsureWithinDirectory(clientDir, clientRoot);

        bool removedServer = false;
        bool removedClient = false;

        if (Directory.Exists(serverDir))
        {
            Directory.Delete(serverDir, recursive: true);
            removedServer = true;
        }

        if (Directory.Exists(clientDir))
        {
            Directory.Delete(clientDir, recursive: true);
            removedClient = true;
        }

        RemoveModStateChoice(serverRoot, modId);

        var ledger = ReadLedger(localExileRoot);
        bool wasInLedger = ledger.Mods.Any(m => m.Id.Equals(modId, StringComparison.OrdinalIgnoreCase));
        if (wasInLedger)
        {
            var remaining = ledger.Mods
                .Where(m => !m.Id.Equals(modId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            WriteLedger(localExileRoot, new DeploymentLedger(1, DateTimeOffset.UtcNow, remaining));
        }

        if (!removedServer && !removedClient && !wasInLedger)
            return new(false, modId, false, false, $"Mod '{modId}' is not installed.");

        return new(true, modId, removedServer, removedClient, $"Uninstalled '{modId}' cleanly.");
    }

    /// <summary>Reads <c>Mods/Binaries/mod-state.txt</c> using Waypoint's exact format.</summary>
    public static Dictionary<string, bool> ReadModStateChoices(string serverBinariesRoot)
    {
        var choices = new Dictionary<string, bool>(StringComparer.Ordinal);
        string path = Path.Combine(serverBinariesRoot, ModStateFileName);
        if (!File.Exists(path))
            return choices;

        foreach (string line in File.ReadAllLines(path))
        {
            string[] words = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words is [var id, "on" or "off"] && DualModManifest.IsValidId(id))
                choices[id] = words[1] == "on";
        }
        return choices;
    }

    private static void WriteModStateChoice(string serverBinariesRoot, string id, bool enabled)
    {
        Directory.CreateDirectory(serverBinariesRoot);
        string path = Path.Combine(serverBinariesRoot, ModStateFileName);
        var lines = new List<string>();
        if (File.Exists(path))
        {
            foreach (string raw in File.ReadAllLines(path))
            {
                string trimmed = raw.TrimEnd('\r');
                if (trimmed.Length == 0) continue;
                string[] words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words is [var other, "on" or "off"] && other == id) continue;
                lines.Add(trimmed);
            }
        }
        lines.Add(id + (enabled ? " on" : " off"));
        string tmp = Path.Combine(serverBinariesRoot, $".waypoint-choices-{Guid.NewGuid():N}.tmp");
        File.WriteAllLines(tmp, lines, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private static void RemoveModStateChoice(string serverBinariesRoot, string id)
    {
        string path = Path.Combine(serverBinariesRoot, ModStateFileName);
        if (!File.Exists(path)) return;

        var lines = File.ReadAllLines(path)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0 && (l.Split(' ', StringSplitOptions.RemoveEmptyEntries) is not [var other, "on" or "off"] || other != id))
            .ToList();

        string tmp = Path.Combine(serverBinariesRoot, $".waypoint-choices-{Guid.NewGuid():N}.tmp");
        File.WriteAllLines(tmp, lines, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private static void DeployServerFiles(string packageDir, DualModManifest manifest, string serverStaging)
    {
        string payloadDirName = string.IsNullOrWhiteSpace(manifest.NativeClient?.PayloadDir)
            ? "client"
            : manifest.NativeClient.PayloadDir.Replace('\\', '/').Split('/')[0];

        string? clientAsm = manifest.NativeClient?.Assembly;
        string? bundleFile = manifest.NativeClient?.BundleFile;

        foreach (string file in Directory.EnumerateFiles(packageDir, "*", SearchOption.AllDirectories))
        {
            string rel = DualModPackageValidator.NormalizeVirtualPath(Path.GetRelativePath(packageDir, file));
            string[] parts = rel.Split('/');

            // Exclude build artifacts, client-only payload/assembly/bundle, and README.md (per LocalExile-Mods build-release.ps1)
            if (parts.Any(p => p is "bin" or "obj" or ".git" or ".vs"))
                continue;
            if (parts[0].Equals(payloadDirName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (parts[0].Equals("Client", StringComparison.OrdinalIgnoreCase) && parts.Length > 1)
                continue;
            if (rel.Equals("README.md", StringComparison.OrdinalIgnoreCase))
                continue;
            if (clientAsm is not null && rel.Equals(clientAsm, StringComparison.OrdinalIgnoreCase))
                continue;
            if (bundleFile is not null && rel.Equals(bundleFile, StringComparison.OrdinalIgnoreCase))
                continue;

            string dest = Path.Combine(serverStaging, Path.Combine(parts));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private static string? DeployClientFiles(
        string packageDir,
        DualModManifest manifest,
        NativeClientManifest nc,
        string clientStaging,
        InstallOptions options)
    {
        // Copy mod.json into Mods/Client/<id>/ so the managed client host can inspect manifest + RawAccess flags
        File.Copy(Path.Combine(packageDir, "mod.json"), Path.Combine(clientStaging, "mod.json"), overwrite: true);

        // Copy client assembly if shipped in package root (or Client/)
        if (!string.IsNullOrWhiteSpace(nc.Assembly))
        {
            string rootAsm = Path.Combine(packageDir, nc.Assembly);
            string nestedAsm = Path.Combine(packageDir, "Client", nc.Assembly);
            string? foundAsm = File.Exists(rootAsm) ? rootAsm : File.Exists(nestedAsm) ? nestedAsm : null;
            if (foundAsm is not null)
                File.Copy(foundAsm, Path.Combine(clientStaging, nc.Assembly), overwrite: true);
        }

        string payloadDirRel = string.IsNullOrWhiteSpace(nc.PayloadDir) ? "client" : nc.PayloadDir;
        string sourcePayloadDir = Path.Combine(packageDir, Path.Combine(payloadDirRel.Replace('\\', '/').Split('/')));

        string? bundleFileName = null;
        if (Directory.Exists(sourcePayloadDir))
        {
            if (options.DeployLooseOverlays)
            {
                string filesDest = Path.Combine(clientStaging, "files");
                Directory.CreateDirectory(filesDest);

                foreach (string srcFile in Directory.EnumerateFiles(sourcePayloadDir, "*", SearchOption.AllDirectories))
                {
                    string rel = DualModPackageValidator.NormalizeVirtualPath(Path.GetRelativePath(sourcePayloadDir, srcFile))
                        .ToLowerInvariant();
                    string dest = Path.Combine(filesDest, Path.Combine(rel.Split('/')));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

                    // Encode text overlays (.ui, .ot, .it, etc.) to UTF-16LE BOM CRLF as 3.29 expects!
                    if (ModBundlePacker.IsClientTextExtension(rel))
                    {
                        string text = DualModPackageValidator.ReadTextAutoEncoding(srcFile);
                        File.WriteAllBytes(dest, ModBundlePacker.EncodeClientText(text));
                    }
                    else
                    {
                        File.Copy(srcFile, dest, overwrite: true);
                    }
                }
            }

            if (options.PackClientBundle)
            {
                var packed = ModBundlePacker.Pack(manifest.Id, manifest.Version, sourcePayloadDir, clientStaging, encodeTextAsUtf16Le: true);
                bundleFileName = packed.Manifest.BundleFile;
            }
        }
        else if (!string.IsNullOrWhiteSpace(nc.BundleFile))
        {
            string prebuiltBundle = Path.Combine(packageDir, nc.BundleFile);
            if (File.Exists(prebuiltBundle))
            {
                File.Copy(prebuiltBundle, Path.Combine(clientStaging, Path.GetFileName(nc.BundleFile)), overwrite: true);
                bundleFileName = Path.GetFileName(nc.BundleFile);
                string prebuiltManifest = Path.Combine(packageDir, "manifest.json");
                if (File.Exists(prebuiltManifest))
                    File.Copy(prebuiltManifest, Path.Combine(clientStaging, "manifest.json"), overwrite: true);
            }
        }

        return bundleFileName;
    }

    private static void WriteLedger(string localExileRoot, DeploymentLedger ledger)
    {
        string clientRoot = GetClientModsRoot(localExileRoot);
        Directory.CreateDirectory(clientRoot);
        string path = GetLedgerPath(localExileRoot);
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(ledger, DualModManifest.SerializerOptions), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private static void SwapDirectoryAtomically(string stagingDir, string targetDir)
    {
        string? backupDir = null;
        if (Directory.Exists(targetDir))
        {
            backupDir = targetDir + ".old-" + Guid.NewGuid().ToString("N");
            Directory.Move(targetDir, backupDir);
        }

        try
        {
            Directory.Move(stagingDir, targetDir);
        }
        catch
        {
            if (backupDir is not null && Directory.Exists(backupDir) && !Directory.Exists(targetDir))
                Directory.Move(backupDir, targetDir);
            throw;
        }

        if (backupDir is not null && Directory.Exists(backupDir))
            Directory.Delete(backupDir, recursive: true);
    }

    private static void EnsureWithinDirectory(string candidatePath, string rootDir)
    {
        string fullRoot = Path.GetFullPath(rootDir);
        string rootWithSep = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        string fullCandidate = Path.GetFullPath(candidatePath);
        if (!fullCandidate.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Target path '{candidatePath}' escapes '{rootDir}'.");
    }
}
