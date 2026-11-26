using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DualMod.SiphonOrb.Server;

/// <summary>
/// Drop sources eligible to roll natural Empty Orbs of Siphoning (mirrors LocalExile's <c>DropSourceMask</c>).
/// </summary>
[Flags]
public enum SiphonDropSource
{
    None = 0,
    MonsterKill = 1 << 0,
    Container = 1 << 1,
    Strongbox = 1 << 2,
    BossBonus = 1 << 3,
    MapCurrency = 1 << 4,
    All = MonsterKill | Container | Strongbox | BossBonus | MapCurrency,
}

/// <summary>
/// Monster rarities and boss archetypes eligible to drop Empty Orbs of Siphoning (mirrors LocalExile's <c>DropMonsterFilter</c>).
/// </summary>
[Flags]
public enum SiphonMonsterRarity
{
    None = 0,
    Normal = 1 << 0,
    Magic = 1 << 1,
    Rare = 1 << 2,
    Unique = 1 << 3,
    Boss = 1 << 4,
    Any = Normal | Magic | Rare | Unique | Boss,
}

/// <summary>
/// Configurable natural world currency drop-pool settings for Empty Orbs of Siphoning,
/// bound from <c>examples/DualMod.SiphonOrb/settings.json</c> (and optional user overrides in <c>data/mods/siphon.json</c>).
/// </summary>
public sealed record SiphonDropSettings
{
    public const double DefaultDropWeight = 25.0;
    public const int DefaultMinAreaLevel = 45;
    public const int DefaultMaxAreaLevel = 0;

    public static readonly IReadOnlyList<string> DefaultSources =
    [
        nameof(SiphonDropSource.MonsterKill),
        nameof(SiphonDropSource.Container),
        nameof(SiphonDropSource.Strongbox),
        nameof(SiphonDropSource.BossBonus),
        nameof(SiphonDropSource.MapCurrency),
    ];

    public static readonly IReadOnlyList<string> DefaultMonsterRarity =
    [
        nameof(SiphonMonsterRarity.Normal),
        nameof(SiphonMonsterRarity.Magic),
        nameof(SiphonMonsterRarity.Rare),
        nameof(SiphonMonsterRarity.Unique),
        nameof(SiphonMonsterRarity.Boss),
    ];

    /// <summary>Default drop-pool configuration matching <c>settings.json</c>.</summary>
    public static SiphonDropSettings Default => new();

    /// <summary>When <c>true</c>, registers Empty Orbs of Siphoning into LocalExile's natural currency drop pool.</summary>
    public bool EnableNaturalDrops { get; init; } = true;

    /// <summary>Relative currency pool weight (must be finite and &gt; 0 when <see cref="EnableNaturalDrops"/> is <c>true</c>).</summary>
    public double DropWeight { get; init; } = DefaultDropWeight;

    /// <summary>Minimum area/monster level required for natural drops (inclusive, <c>1..100</c>).</summary>
    public int MinAreaLevel { get; init; } = DefaultMinAreaLevel;

    /// <summary>Optional maximum area/monster level (inclusive; <c>0</c> = unbounded).</summary>
    public int MaxAreaLevel { get; init; } = DefaultMaxAreaLevel;

    /// <summary>Drop source names eligible to roll Empty Orbs of Siphoning.</summary>
    public IReadOnlyList<string> Sources { get; init; } = DefaultSources;

    /// <summary>Monster rarity / boss archetype names eligible to drop Empty Orbs of Siphoning.</summary>
    public IReadOnlyList<string> MonsterRarity { get; init; } = DefaultMonsterRarity;

    /// <summary>Parses <see cref="Sources"/> into a strongly-typed <see cref="SiphonDropSource"/> bitmask.</summary>
    public SiphonDropSource ResolveSources()
    {
        if (Sources is null)
            return SiphonDropSource.None;

        SiphonDropSource mask = SiphonDropSource.None;
        foreach (string raw in Sources)
        {
            if (string.IsNullOrWhiteSpace(raw))
                throw new ArgumentException("Sources cannot contain empty entries.", nameof(Sources));

            string token = raw.Trim();
            if (Enum.TryParse<SiphonDropSource>(token, ignoreCase: true, out SiphonDropSource parsed))
            {
                mask |= parsed;
            }
            else
            {
                throw new ArgumentException($"Unknown drop source '{raw}'. Expected one of: MonsterKill, Container, Strongbox, BossBonus, MapCurrency, All.", nameof(Sources));
            }
        }

        return mask;
    }

    /// <summary>Parses <see cref="MonsterRarity"/> into a strongly-typed <see cref="SiphonMonsterRarity"/> bitmask.</summary>
    public SiphonMonsterRarity ResolveMonsterRarity()
    {
        if (MonsterRarity is null)
            return SiphonMonsterRarity.None;

        SiphonMonsterRarity mask = SiphonMonsterRarity.None;
        foreach (string raw in MonsterRarity)
        {
            if (string.IsNullOrWhiteSpace(raw))
                throw new ArgumentException("MonsterRarity cannot contain empty entries.", nameof(MonsterRarity));

            string token = raw.Trim();
            if (token.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                mask |= SiphonMonsterRarity.Any;
            }
            else if (Enum.TryParse<SiphonMonsterRarity>(token, ignoreCase: true, out SiphonMonsterRarity parsed))
            {
                mask |= parsed;
            }
            else
            {
                throw new ArgumentException($"Unknown monster rarity filter '{raw}'. Expected one of: Normal, Magic, Rare, Unique, Boss, Any.", nameof(MonsterRarity));
            }
        }

        return mask;
    }

    /// <summary>Validates all bounds, filter tokens, and area-level invariants.</summary>
    public void Validate()
    {
        if (!double.IsFinite(DropWeight) || (EnableNaturalDrops && DropWeight <= 0) || (!EnableNaturalDrops && DropWeight < 0))
            throw new ArgumentOutOfRangeException(nameof(DropWeight), $"DropWeight ({DropWeight}) must be finite and positive when natural drops are enabled.");

        if (MinAreaLevel < 1 || MinAreaLevel > 100)
            throw new ArgumentOutOfRangeException(nameof(MinAreaLevel), $"MinAreaLevel ({MinAreaLevel}) must be in [1, 100].");

        if (MaxAreaLevel < 0 || MaxAreaLevel > 100)
            throw new ArgumentOutOfRangeException(nameof(MaxAreaLevel), $"MaxAreaLevel ({MaxAreaLevel}) must be 0 (unbounded) or in [1, 100].");

        if (MaxAreaLevel > 0 && MaxAreaLevel < MinAreaLevel)
            throw new ArgumentOutOfRangeException(nameof(MaxAreaLevel), $"MaxAreaLevel ({MaxAreaLevel}) cannot be less than MinAreaLevel ({MinAreaLevel}).");

        SiphonDropSource sources = ResolveSources();
        SiphonMonsterRarity rarities = ResolveMonsterRarity();

        if (EnableNaturalDrops && sources == SiphonDropSource.None)
            throw new ArgumentException("At least one drop source must be enabled when EnableNaturalDrops is true.", nameof(Sources));

        if (EnableNaturalDrops && rarities == SiphonMonsterRarity.None)
            throw new ArgumentException("At least one monster rarity filter must be enabled when EnableNaturalDrops is true.", nameof(MonsterRarity));
    }

    /// <summary>
    /// Parses a LocalExile <c>settings.json</c> schema document, optionally overlays a flat user overrides JSON object
    /// (such as <c>data/mods/siphon.json</c>), validates the resulting <see cref="SiphonDropSettings"/>, and returns it.
    /// </summary>
    public static SiphonDropSettings ParseSettingsSchema(string settingsJson, string? userOverridesJson = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsJson);

        var docOpts = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        JsonObject root = JsonNode.Parse(settingsJson, documentOptions: docOpts) as JsonObject
            ?? throw new FormatException("settings.json root must be a JSON object.");

        if (root["settings"] is not JsonObject declared)
            throw new FormatException("settings.json must contain a 'settings' object.");

        var effective = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, JsonNode?> kv in declared)
        {
            if (kv.Value is JsonObject decl && decl.TryGetPropertyValue("default", out JsonNode? defaultNode))
            {
                effective[kv.Key] = defaultNode;
            }
        }

        if (!string.IsNullOrWhiteSpace(userOverridesJson))
        {
            JsonObject overrides = JsonNode.Parse(userOverridesJson, documentOptions: docOpts) as JsonObject
                ?? throw new FormatException("User settings override must be a JSON object.");

            foreach (KeyValuePair<string, JsonNode?> kv in overrides)
            {
                effective[kv.Key] = kv.Value;
            }
        }

        var settings = new SiphonDropSettings
        {
            EnableNaturalDrops = ReadBool(effective, "enableNaturalDrops", Default.EnableNaturalDrops),
            DropWeight = ReadDouble(effective, "dropWeight", Default.DropWeight),
            MinAreaLevel = ReadInt(effective, "minAreaLevel", Default.MinAreaLevel),
            MaxAreaLevel = ReadInt(effective, "maxAreaLevel", Default.MaxAreaLevel),
            Sources = ReadStringList(effective, "sources", DefaultSources),
            MonsterRarity = ReadStringList(effective, "monsterRarity", DefaultMonsterRarity),
        };

        settings.Validate();
        return settings;
    }

    /// <summary>Loads and validates <see cref="SiphonDropSettings"/> from a <c>settings.json</c> file on disk.</summary>
    public static SiphonDropSettings LoadFromFile(string settingsPath, string? userOverridesPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        string schemaJson = File.ReadAllText(settingsPath);
        string? overridesJson = !string.IsNullOrWhiteSpace(userOverridesPath) && File.Exists(userOverridesPath)
            ? File.ReadAllText(userOverridesPath)
            : null;
        return ParseSettingsSchema(schemaJson, overridesJson);
    }

    private static bool ReadBool(Dictionary<string, JsonNode?> map, string key, bool fallback)
    {
        if (!map.TryGetValue(key, out JsonNode? node) || node is null)
            return fallback;
        JsonValueKind kind = node.GetValueKind();
        return kind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new FormatException($"Setting '{key}' must be a boolean."),
        };
    }

    private static double ReadDouble(Dictionary<string, JsonNode?> map, string key, double fallback)
    {
        if (!map.TryGetValue(key, out JsonNode? node) || node is null)
            return fallback;
        if (node.GetValueKind() != JsonValueKind.Number)
            throw new FormatException($"Setting '{key}' must be a number.");
        return double.Parse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static int ReadInt(Dictionary<string, JsonNode?> map, string key, int fallback)
    {
        if (!map.TryGetValue(key, out JsonNode? node) || node is null)
            return fallback;
        if (node.GetValueKind() != JsonValueKind.Number)
            throw new FormatException($"Setting '{key}' must be an integer.");
        double num = double.Parse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);
        if (!double.IsFinite(num) || num != Math.Floor(num) || num < int.MinValue || num > int.MaxValue)
            throw new FormatException($"Setting '{key}' must be a whole 32-bit integer.");
        return (int)num;
    }

    private static IReadOnlyList<string> ReadStringList(
        Dictionary<string, JsonNode?> map,
        string key,
        IReadOnlyList<string> fallback)
    {
        if (!map.TryGetValue(key, out JsonNode? node) || node is null)
            return fallback;
        if (node is not JsonArray arr)
            throw new FormatException($"Setting '{key}' must be a JSON array of strings.");

        var list = new List<string>(arr.Count);
        foreach (JsonNode? elem in arr)
        {
            if (elem is null || elem.GetValueKind() != JsonValueKind.String)
                throw new FormatException($"Setting '{key}' must contain only string entries.");
            list.Add(elem.GetValue<string>());
        }

        return list;
    }
}
