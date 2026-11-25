using System.Text.Json;

namespace LocalExile.Client.Patcher;

public static class Program
{
    private static readonly JsonSerializerOptions JsonPretty = new() { WriteIndented = true };

    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            string cmd = args[0].ToLowerInvariant();
            string[] rest = args[1..];
            return cmd switch
            {
                "check" => RunCheck(rest),
                "patch" => RunPatch(rest),
                "verify" => RunVerify(rest),
                "unpatch" => RunUnpatch(rest),
                "dump-addresses" => RunDumpAddresses(rest),
                _ => UnknownCommand(cmd),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[patcher] error: {ex.Message}");
            return 1;
        }
    }

    private static int RunCheck(string[] args)
    {
        if (!TryGetPositional(args, 0, out string exePath))
            return UsageError("check requires <exePath>");

        bool allowSynthetic = HasFlag(args, "--allow-synthetic");
        bool jsonOut = HasFlag(args, "--json");
        string dllName = GetOption(args, "--dll") ?? ClientPatcher.DefaultHostDllName;
        var catalog = TryLoadCatalog(GetOption(args, "--addresses"));
        var patcher = new ClientPatcher(catalog);

        var result = patcher.CheckBytes(File.ReadAllBytes(exePath), allowSynthetic, dllName);
        if (jsonOut)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, JsonPretty));
        }
        else
        {
            Console.WriteLine($"File:         {exePath}");
            Console.WriteLine($"SHA-256:      {result.Sha256Hex}");
            Console.WriteLine($"Size:         {result.FileSize:N0} bytes");
            Console.WriteLine($"Build Match:  {result.MatchKind} ({result.GameVersion ?? "unknown"})");
            Console.WriteLine($"Sections:     {result.SectionCount} (header slack: {result.HeaderSlackBytes} bytes)");
            Console.WriteLine($"Imports:      {result.ExistingImportCount} DLL(s) (has {dllName}: {result.HasNativeModsImport})");
            Console.WriteLine($"Authenticode: {(result.HasAuthenticodeSignature ? $"present @ 0x{result.AuthenticodeOffset:X} ({result.AuthenticodeSize} B)" : "none")}");
            Console.WriteLine($"Status:       {result.Message}");
        }

        return result.MatchKind == BuildMatchKind.Unknown ? 1 : 0;
    }

    private static int RunPatch(string[] args)
    {
        bool proxyMode = HasFlag(args, "--proxy-dll");
        var catalog = TryLoadCatalog(GetOption(args, "--addresses"));
        var patcher = new ClientPatcher(catalog);

        if (proxyMode)
        {
            string? clientDir = GetOption(args, "--client-dir");
            string? proxySource = GetOption(args, "--proxy-source");
            string? hostSource = GetOption(args, "--host-source");
            string proxyName = GetOption(args, "--proxy-name") ?? ClientPatcher.DefaultProxyDllName;
            if (string.IsNullOrWhiteSpace(clientDir) || string.IsNullOrWhiteSpace(proxySource))
                return UsageError("patch --proxy-dll requires --client-dir <dir> and --proxy-source <path>");

            var manifest = patcher.DeployProxyDll(clientDir, proxySource, hostSource, proxyName);
            Console.WriteLine($"[patcher] Deployed proxy DLL '{manifest.ProxyDllName}' to {clientDir}");
            Console.WriteLine($"[patcher] SHA-256: {manifest.ProxyDllSha256}");
            Console.WriteLine($"[patcher] Proton / Wine hint: {manifest.WineDllOverridesHint}");
            return 0;
        }

        if (!TryGetPositional(args, 0, out string exePath))
            return UsageError("patch requires <exePath>");

        string outputPath = GetOption(args, "--output") ?? exePath;
        var options = new PatchOptions(
            DllName: GetOption(args, "--dll") ?? ClientPatcher.DefaultHostDllName,
            ImportAnchorSymbol: GetOption(args, "--anchor") ?? ClientPatcher.DefaultImportAnchorSymbol,
            AllowSynthetic: HasFlag(args, "--allow-synthetic"),
            CreateBackup: HasFlag(args, "--backup"));

        var res = patcher.PatchFile(exePath, outputPath, options);
        Console.WriteLine($"[patcher] {res.Message}");
        Console.WriteLine($"[patcher] Input  SHA-256: {res.InputSha256}");
        Console.WriteLine($"[patcher] Output SHA-256: {res.OutputSha256}");
        if (res.BackupPath is not null)
            Console.WriteLine($"[patcher] Backup saved:   {res.BackupPath}");
        return 0;
    }

    private static int RunVerify(string[] args)
    {
        var patcher = new ClientPatcher(TryLoadCatalog(GetOption(args, "--addresses")));
        if (HasFlag(args, "--proxy-dll"))
        {
            string? clientDir = GetOption(args, "--client-dir");
            if (string.IsNullOrWhiteSpace(clientDir))
                return UsageError("verify --proxy-dll requires --client-dir <dir>");

            var (ok, msg, _) = patcher.VerifyProxyDll(clientDir);
            Console.WriteLine($"[patcher] {msg}");
            return ok ? 0 : 1;
        }

        if (!TryGetPositional(args, 0, out string exePath))
            return UsageError("verify requires <exePath>");

        string dllName = GetOption(args, "--dll") ?? ClientPatcher.DefaultHostDllName;
        var res = patcher.VerifyBytes(File.ReadAllBytes(exePath), dllName);
        Console.WriteLine($"[patcher] {res.Message}");
        if (res.RecordedOriginalSha256 is not null)
            Console.WriteLine($"[patcher] Recorded original SHA-256: {res.RecordedOriginalSha256}");
        return res.IsValid ? 0 : 1;
    }

    private static int RunUnpatch(string[] args)
    {
        var patcher = new ClientPatcher();
        if (HasFlag(args, "--proxy-dll"))
        {
            string? clientDir = GetOption(args, "--client-dir");
            if (string.IsNullOrWhiteSpace(clientDir))
                return UsageError("unpatch --proxy-dll requires --client-dir <dir>");

            bool removed = patcher.RemoveProxyDll(clientDir);
            Console.WriteLine(removed
                ? $"[patcher] Removed proxy-DLL deployment from {clientDir}."
                : $"[patcher] No proxy-DLL deployment found in {clientDir}.");
            return removed ? 0 : 1;
        }

        if (!TryGetPositional(args, 0, out string exePath))
            return UsageError("unpatch requires <exePath>");

        string outputPath = GetOption(args, "--output") ?? exePath;
        var res = patcher.UnpatchFile(exePath, outputPath);
        Console.WriteLine($"[patcher] {res.Message}");
        return res.ExactByteRestoreVerified ? 0 : 1;
    }

    private static int RunDumpAddresses(string[] args)
    {
        string? addrDir = GetOption(args, "--addresses") ?? AddressLibraryCatalog.LocateDefaultDirectory();
        if (addrDir is null)
            throw new DirectoryNotFoundException("Could not locate data/addresses; specify --addresses <dir>.");

        var catalog = AddressLibraryCatalog.LoadFromDirectory(addrDir);
        string buildVersion = GetOption(args, "--build") ?? "3.29.3d";
        string? category = GetOption(args, "--category");
        bool jsonOut = HasFlag(args, "--json");

        var doc = catalog.GetByGameVersion(buildVersion)
            ?? throw new KeyNotFoundException($"Build '{buildVersion}' not found in address library.");

        var symbols = doc.Symbols
            .Where(s => string.IsNullOrWhiteSpace(category) || string.Equals(s.Category, category, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (jsonOut)
        {
            Console.WriteLine(JsonSerializer.Serialize(symbols, JsonPretty));
            return 0;
        }

        Console.WriteLine($"Address Library: Path of Exile {doc.GameVersion} (ImageBase {doc.ImageBase}, {symbols.Count} symbols)");
        foreach (var b in doc.Builds)
            Console.WriteLine($"  Build {b.BuildNumber} ({b.Label}): SHA-256 {b.ExecutableSha256}");
        Console.WriteLine();
        Console.WriteLine($"{"Symbol",-38} {"Category",-11} {"Kind",-11} {"RVA",-12} {"Status",-20} {"ExpectedBytes"}");
        Console.WriteLine(new string('-', 118));
        foreach (var s in symbols)
        {
            Console.WriteLine(
                $"{s.Name,-38} {s.Category,-11} {s.Kind,-11} {s.Rva ?? "null",-12} {s.Status,-20} {s.ExpectedBytes ?? "null"}");
        }

        return 0;
    }

    private static AddressLibraryCatalog? TryLoadCatalog(string? explicitDir)
    {
        string? dir = explicitDir ?? AddressLibraryCatalog.LocateDefaultDirectory();
        return dir is not null && Directory.Exists(dir) ? AddressLibraryCatalog.LoadFromDirectory(dir) : null;
    }

    private static bool TryGetPositional(string[] args, int index, out string value)
    {
        int seen = 0;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (TakesValue(args[i]))
                    i++;
                continue;
            }

            if (seen == index)
            {
                value = args[i];
                return true;
            }

            seen++;
        }

        value = "";
        return false;
    }

    private static bool TakesValue(string flag) => flag is
        "--output" or "--dll" or "--anchor" or "--addresses" or "--build" or "--category"
        or "--client-dir" or "--proxy-source" or "--host-source" or "--proxy-name";

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static string? GetOption(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    private static int UnknownCommand(string cmd)
    {
        Console.Error.WriteLine($"Unknown command: {cmd}");
        PrintUsage();
        return 2;
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine($"[patcher] {message}");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            LocalExile.Client.Patcher — Universal 3.29 PE32+ Client Patcher & Address Library CLI

            Commands:
              check <exe> [--addresses <dir>] [--allow-synthetic] [--dll <name>] [--json]
              patch <exe> [--output <out>] [--dll <name>] [--addresses <dir>] [--allow-synthetic] [--backup]
              patch --proxy-dll --client-dir <dir> --proxy-source <dll> [--host-source <dll>] [--proxy-name version.dll]
              verify <exe> [--dll <name>] [--addresses <dir>]
              verify --proxy-dll --client-dir <dir>
              unpatch <exe> [--output <out>]
              unpatch --proxy-dll --client-dir <dir>
              dump-addresses [--build <3.29.3d|3.29.3c>] [--category <cat>] [--addresses <dir>] [--json]
            """);
    }
}
