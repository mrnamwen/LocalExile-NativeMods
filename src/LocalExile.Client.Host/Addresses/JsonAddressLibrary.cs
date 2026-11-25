using System.Globalization;
using System.Text.Json;
using LocalExile.Client.Host.Native;
using LocalExile.Client.Sdk.Addresses;

namespace LocalExile.Client.Host.Addresses;

/// <summary>
/// Struct field offset entry parsed from <c>data/addresses/*.json</c>.
/// </summary>
public sealed record StructOffsetEntry(
    string Name,
    string StructName,
    ulong Offset,
    string TypeName,
    string Status,
    string? Notes = null,
    string? Source = null);

/// <summary>
/// Recognized client executable build metadata from <c>data/addresses/*.json</c>.
/// </summary>
public sealed record KnownBuildRecord(
    int BuildNumber,
    string Label,
    string ExecutableSha256,
    long ExecutableSize,
    string? SteamIndexSha256 = null,
    string Source = "");

/// <summary>
/// Address library implementation loading <c>data/addresses/3.29.3d.json</c> / <c>3.29.3c.json</c>
/// conforming to <c>data/addresses/address-library.schema.json</c> (schemaVersion 1).
/// </summary>
public sealed class JsonAddressLibrary : IAddressLibrary
{
    private readonly Dictionary<string, AddressEntry> _entries;
    private readonly Dictionary<string, StructOffsetEntry> _offsets;
    private readonly Dictionary<string, string> _constants;
    private readonly List<KnownBuildRecord> _builds;

    /// <inheritdoc/>
    public ClientBuildId Build { get; }

    /// <inheritdoc/>
    public string BuildVersion { get; }

    /// <inheritdoc/>
    public string? ExecutableSha256 { get; }

    /// <inheritdoc/>
    public nint ImageBase { get; }

    /// <inheritdoc/>
    public IReadOnlyCollection<AddressEntry> Entries => _entries.Values;

    /// <summary>Struct field offsets declared in the database.</summary>
    public IReadOnlyCollection<StructOffsetEntry> Offsets => _offsets.Values;

    /// <summary>Protocol and engine constants declared in the database.</summary>
    public IReadOnlyDictionary<string, string> Constants => _constants;

    /// <summary>Known executable builds covered by this database.</summary>
    public IReadOnlyList<KnownBuildRecord> KnownBuilds => _builds;

    /// <summary>Creates an address library directly from in-memory entries.</summary>
    public JsonAddressLibrary(
        ClientBuildId build,
        string buildVersion,
        nint imageBase,
        IEnumerable<AddressEntry> entries,
        string? executableSha256 = null,
        IEnumerable<StructOffsetEntry>? offsets = null,
        IDictionary<string, string>? constants = null,
        IEnumerable<KnownBuildRecord>? knownBuilds = null)
    {
        Build = build;
        BuildVersion = buildVersion;
        ImageBase = imageBase;
        ExecutableSha256 = executableSha256;
        _entries = new Dictionary<string, AddressEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (AddressEntry entry in entries)
        {
            _entries[entry.Name] = entry;
        }

        _offsets = new Dictionary<string, StructOffsetEntry>(StringComparer.OrdinalIgnoreCase);
        if (offsets is not null)
        {
            foreach (StructOffsetEntry off in offsets)
            {
                _offsets[off.Name] = off;
            }
        }

        _constants = constants is not null
            ? new Dictionary<string, string>(constants, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _builds = knownBuilds is not null ? [.. knownBuilds] : [];
    }

    /// <summary>Builds a <see cref="JsonAddressLibrary"/> directly from an active <see cref="INativeBridge"/>.</summary>
    public static JsonAddressLibrary FromBridge(INativeBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        return new JsonAddressLibrary(
            bridge.DetectedBuild,
            bridge.BuildVersion,
            bridge.ImageBase,
            bridge.EnumerateSymbols(),
            bridge.ExecutableSha256);
    }

    /// <summary>Loads an address library from a JSON file conforming to <c>address-library.schema.json</c>.</summary>
    public static JsonAddressLibrary LoadFromFile(string jsonPath, nint? runtimeImageBase = null, string? executableSha256 = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonPath);
        return Parse(File.ReadAllText(jsonPath), runtimeImageBase, executableSha256);
    }

    /// <summary>
    /// Scans a directory containing <c>3.29.3d.json</c> / <c>3.29.3c.json</c> and loads the database
    /// whose <c>builds[].executableSha256</c> matches <paramref name="executableSha256"/>.
    /// </summary>
    public static JsonAddressLibrary? TryLoadForExecutableSha256(string directoryPath, string executableSha256, nint? runtimeImageBase = null)
    {
        if (!Directory.Exists(directoryPath) || string.IsNullOrWhiteSpace(executableSha256))
        {
            return null;
        }

        string targetSha = executableSha256.Trim().ToLowerInvariant();
        foreach (string file in Directory.GetFiles(directoryPath, "*.json"))
        {
            if (file.EndsWith(".schema.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                JsonAddressLibrary candidate = LoadFromFile(file, runtimeImageBase, targetSha);
                if (candidate.MatchesExecutableSha256(targetSha))
                {
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                // Skip non-library JSON files in the directory.
            }
        }

        return null;
    }

    /// <summary>Parses a JSON address library document conforming to <c>address-library.schema.json</c>.</summary>
    public static JsonAddressLibrary Parse(string json, nint? runtimeImageBase = null, string? executableSha256 = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        JsonElement root = doc.RootElement;
        if (root.TryGetProperty("schemaVersion", out JsonElement sv) && sv.ValueKind == JsonValueKind.Number && sv.GetInt32() != 1)
        {
            throw new FormatException($"Unsupported address library schemaVersion {sv.GetInt32()}; expected 1.");
        }

        string gameVersion = root.TryGetProperty("gameVersion", out JsonElement gv) && gv.ValueKind == JsonValueKind.String
            ? gv.GetString() ?? "unknown"
            : "unknown";

        ClientBuildId buildId = gameVersion switch
        {
            "3.29.3d" => ClientBuildId.Build3293d,
            "3.29.3c" => ClientBuildId.Build3293c,
            _ => ClientBuildId.Unknown,
        };

        ulong nominalBase = NativeShimConstants.NominalImageBase;
        if (root.TryGetProperty("imageBase", out JsonElement ib) && ib.ValueKind == JsonValueKind.String)
        {
            ParseHexUInt64(ib.GetString(), out nominalBase);
        }

        nint effectiveBase = runtimeImageBase ?? unchecked((nint)nominalBase);

        var builds = new List<KnownBuildRecord>();
        if (root.TryGetProperty("builds", out JsonElement buildsElem) && buildsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement b in buildsElem.EnumerateArray())
            {
                int buildNum = b.TryGetProperty("buildNumber", out JsonElement bn) && bn.ValueKind == JsonValueKind.Number ? bn.GetInt32() : 0;
                string label = GetOptString(b, "label") ?? gameVersion;
                string sha = GetOptString(b, "executableSha256") ?? string.Empty;
                long size = b.TryGetProperty("executableSize", out JsonElement sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0;
                string? steamSha = GetOptString(b, "steamIndexSha256");
                string source = GetOptString(b, "source") ?? string.Empty;
                builds.Add(new KnownBuildRecord(buildNum, label, sha, size, steamSha, source));
            }
        }

        var entries = new List<AddressEntry>();
        if (root.TryGetProperty("symbols", out JsonElement symsElem) && symsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement s in symsElem.EnumerateArray())
            {
                string name = GetOptString(s, "name")
                    ?? throw new FormatException("Address library symbol is missing required 'name'.");
                string category = GetOptString(s, "category") ?? "unknown";
                string kind = GetOptString(s, "kind") ?? "code";
                string status = GetOptString(s, "status") ?? "unverified";
                string? expectedBytes = GetOptString(s, "expectedBytes");
                string? source = GetOptString(s, "source");
                string? signature = GetOptString(s, "signature");

                ulong? rva = null;
                if (s.TryGetProperty("rva", out JsonElement rvaElem))
                {
                    if (rvaElem.ValueKind == JsonValueKind.String && ParseHexUInt64(rvaElem.GetString(), out ulong parsedHex))
                    {
                        rva = parsedHex;
                    }
                    else if (rvaElem.ValueKind == JsonValueKind.Number)
                    {
                        rva = rvaElem.GetUInt64();
                    }
                }

                entries.Add(new AddressEntry(name, rva, category, status, expectedBytes, SourceDoc: source, Signature: signature, Kind: kind));
            }
        }

        var offsets = new List<StructOffsetEntry>();
        if (root.TryGetProperty("offsets", out JsonElement offElem) && offElem.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement o in offElem.EnumerateArray())
            {
                string name = GetOptString(o, "name") ?? string.Empty;
                string structName = GetOptString(o, "struct") ?? string.Empty;
                ulong offset = 0;
                if (o.TryGetProperty("offset", out JsonElement ov) && ov.ValueKind == JsonValueKind.String)
                {
                    ParseHexUInt64(ov.GetString(), out offset);
                }

                string type = GetOptString(o, "type") ?? string.Empty;
                string status = GetOptString(o, "status") ?? "unverified";
                offsets.Add(new StructOffsetEntry(name, structName, offset, type, status, GetOptString(o, "notes"), GetOptString(o, "source")));
            }
        }

        var constants = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("constants", out JsonElement constElem) && constElem.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty prop in constElem.EnumerateObject())
            {
                constants[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? string.Empty
                    : prop.Value.GetRawText();
            }
        }

        string? effectiveSha = executableSha256 ?? builds.FirstOrDefault()?.ExecutableSha256;
        return new JsonAddressLibrary(buildId, gameVersion, effectiveBase, entries, effectiveSha, offsets, constants, builds);
    }

    /// <summary>Returns <c>true</c> if any build record in this library matches <paramref name="sha256"/>.</summary>
    public bool MatchesExecutableSha256(string sha256)
    {
        if (string.IsNullOrWhiteSpace(sha256))
        {
            return false;
        }

        return _builds.Any(b => string.Equals(b.ExecutableSha256, sha256.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Attempts to retrieve a struct field offset entry by canonical name.</summary>
    public bool TryGetOffset(string offsetName, out StructOffsetEntry? offset) =>
        _offsets.TryGetValue(offsetName, out offset);

    /// <inheritdoc/>
    public bool TryGetEntry(string symbolName, out AddressEntry? entry) =>
        _entries.TryGetValue(symbolName, out entry);

    /// <inheritdoc/>
    public bool TryResolveRva(string symbolName, out ulong rva)
    {
        rva = 0;
        if (!_entries.TryGetValue(symbolName, out AddressEntry? entry) || !entry.IsVerified || !entry.Rva.HasValue)
        {
            return false;
        }

        rva = entry.Rva.Value;
        return true;
    }

    /// <inheritdoc/>
    public bool TryResolveAddress(string symbolName, out nint address)
    {
        address = nint.Zero;
        if (!TryResolveRva(symbolName, out ulong rva))
        {
            return false;
        }

        address = unchecked((nint)((ulong)ImageBase + rva));
        return true;
    }

    /// <inheritdoc/>
    public nint ResolveAddress(string symbolName)
    {
        if (!_entries.TryGetValue(symbolName, out AddressEntry? entry))
        {
            throw new KeyNotFoundException($"Address library symbol '{symbolName}' does not exist for build {BuildVersion}.");
        }

        if (!entry.IsVerified || !entry.Rva.HasValue)
        {
            throw new InvalidOperationException(
                $"Address library symbol '{symbolName}' is not verified on build {BuildVersion} (status='{entry.Status}').");
        }

        return unchecked((nint)((ulong)ImageBase + entry.Rva.Value));
    }

    private static string? GetOptString(JsonElement elem, string prop) =>
        elem.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool ParseHexUInt64(string? text, out ulong value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string s = text.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            s = s[2..];
        }

        return ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}
