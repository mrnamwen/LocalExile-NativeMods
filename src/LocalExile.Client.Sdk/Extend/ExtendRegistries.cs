using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalExile.Client.Sdk.Engine;

namespace LocalExile.Client.Sdk.Extend;

/// <summary>
/// Registers custom GEAL built-in functions callable from <c>.ot</c> client scripts.
/// </summary>
public interface IGealBuiltinRegistry
{
    /// <summary>Names of all custom GEAL built-ins registered by client mods.</summary>
    IReadOnlyCollection<string> RegisteredNames { get; }

    /// <summary>Registers a custom GEAL built-in returning a <see cref="GealValue"/>.</summary>
    /// <param name="name">Built-in function name (case-insensitive unique).</param>
    /// <param name="handler">Callback receiving the invoking object id and positional arguments.</param>
    IDisposable Register(string name, Func<uint, GealArguments, GealValue> handler);

    /// <summary>Registers a custom void GEAL built-in.</summary>
    /// <param name="name">Built-in function name (case-insensitive unique).</param>
    /// <param name="handler">Callback receiving the invoking object id and positional arguments.</param>
    IDisposable Register(string name, Action<uint, GealArguments> handler);

    /// <summary>Returns <c>true</c> if a custom built-in with <paramref name="name"/> is registered.</summary>
    bool IsRegistered(string name);

    /// <summary>Invokes a registered custom GEAL built-in by name.</summary>
    GealInvocationResult Invoke(string name, uint objectId, params GealValue[] arguments);
}

/// <summary>
/// Contract implemented by custom UI controls registered via <see cref="IUiControlRegistry"/> and instantiated from <c>.ui</c> files.
/// </summary>
public interface ICustomUiControl
{
    /// <summary>The control type name used in <c>.ui</c> files (for example <c>GrowthGauge</c>).</summary>
    string ControlTypeName { get; }

    /// <summary>Called when the control is attached to a loaded <c>.ui</c> tree.</summary>
    /// <param name="hostControl">The backing <see cref="IUiControl"/> node in the UI tree.</param>
    void OnInitialize(IUiControl hostControl);

    /// <summary>Called whenever a property on the backing control is modified.</summary>
    void OnPropertyChanged(string propertyName, string value);

    /// <summary>Called once per game frame while the owning window is open.</summary>
    void OnFrame(float deltaSeconds);

    /// <summary>Called when the owning window closes or reloads.</summary>
    void OnDestroy();
}

/// <summary>
/// Registers custom UI control factories usable from <c>.ui</c> layouts alongside the client's 47 stock controls.
/// </summary>
public interface IUiControlRegistry
{
    /// <summary>Type names of all custom UI controls currently registered.</summary>
    IReadOnlyCollection<string> RegisteredControlTypes { get; }

    /// <summary>Registers a custom control type with a parameterless constructor.</summary>
    IDisposable Register<TControl>(string controlTypeName) where TControl : class, ICustomUiControl, new();

    /// <summary>Registers a custom control factory delegate.</summary>
    IDisposable Register(string controlTypeName, Func<ICustomUiControl> factory);

    /// <summary>Returns <c>true</c> if <paramref name="controlTypeName"/> is registered.</summary>
    bool IsRegistered(string controlTypeName);

    /// <summary>Instantiates a custom control by type name, or returns <c>null</c> if not registered.</summary>
    ICustomUiControl? CreateInstance(string controlTypeName);
}

/// <summary>
/// Custom per-area client ruleset backed by an unused slot in the 3.29 ruleset creator array (<c>0x00..0xAE</c>).
/// Receives S→C <c>0x2c9</c> updates for its <see cref="RulesetId"/>.
/// </summary>
public interface IClientRuleset
{
    /// <summary>Slot index in the client ruleset creator array (<c>0x00..0xAE</c>).</summary>
    byte RulesetId { get; }

    /// <summary>Human-readable ruleset name.</summary>
    string Name { get; }

    /// <summary>Called when the ruleset is instantiated for a newly entered area.</summary>
    void OnAreaEntered(AreaEventArgs area);

    /// <summary>Called when an S→C <c>0x2c9</c> ruleset update arrives for this <see cref="RulesetId"/>.</summary>
    void OnRulesetUpdate(ReadOnlySpan<byte> payload);

    /// <summary>Called once per game frame while active in the area.</summary>
    void OnFrame(float deltaSeconds);

    /// <summary>Called when leaving the area.</summary>
    void OnAreaLeft();
}

/// <summary>
/// Registers custom per-area client rulesets into empty slots of the 3.29 creator array (<c>0xaf</c> slots total).
/// </summary>
public interface IRulesetRegistry
{
    /// <summary>Maximum valid ruleset slot id (<c>0xaf - 1 = 0xae</c>).</summary>
    const byte MaxRulesetId = 0xAE;

    /// <summary>
    /// Default ruleset id reserved for the NativeMods <c>0x02C9</c> filter-hook host transport channel
    /// (intercepted before <c>rulesets.creator_table</c>, preserving backward compatibility with <c>ServerBridge</c> / <c>LocalExile</c>).
    /// Do NOT register native <c>rulesets.creator_table</c> entries at <c>0xA0</c>; use <see cref="FirstFreeNativeRulesetId"/> (<c>0xAE</c>) or another verified free slot instead.
    /// </summary>
    const byte DefaultHostChannelRulesetId = 0xA0;

    /// <summary>
    /// Highest verified free slot (<c>0xAE</c>) in 3.29.3d <c>rulesets.creator_table</c> recommended for native ruleset registration.
    /// Other verified free high slots include <c>0xAA</c>, <c>0xA9</c>, <c>0xA3</c>, <c>0x9C</c>, and <c>0x90</c>.
    /// </summary>
    const byte FirstFreeNativeRulesetId = 0xAE;

    /// <summary>
    /// Returns <c>true</c> if <paramref name="rulesetId"/> is a verified unoccupied slot in 3.29.3d <c>rulesets.creator_table</c>
    /// safe for native ruleset registration (107 free slots out of <c>0x00..0xAE</c>; <c>0xA0</c> is occupied by a stock ruleset).
    /// </summary>
    static bool IsNativeSlotFree(byte rulesetId) => rulesetId switch
    {
        <= 0x10 => true,
        >= 0x12 and <= 0x3F => true,
        >= 0x41 and <= 0x45 => true,
        >= 0x47 and <= 0x48 => true,
        >= 0x4D and <= 0x50 => true,
        >= 0x52 and <= 0x57 => true,
        >= 0x61 and <= 0x62 => true,
        >= 0x6C and <= 0x6E => true,
        0x71 or 0x73 or 0x79 or 0x7B => true,
        >= 0x80 and <= 0x84 => true,
        >= 0x8A and <= 0x8B => true,
        0x90 => true,
        >= 0x92 and <= 0x96 => true,
        0x9C or 0xA3 or 0xA9 or 0xAA or 0xAE => true,
        _ => false,
    };

    /// <summary>All ruleset ids currently registered by the host or client mods.</summary>
    IReadOnlyCollection<byte> RegisteredIds { get; }

    /// <summary>Registers a custom ruleset class using its <see cref="IClientRuleset.RulesetId"/>.</summary>
    IDisposable Register<TRuleset>() where TRuleset : class, IClientRuleset, new();

    /// <summary>
    /// Registers a custom ruleset factory for <paramref name="rulesetId"/>.
    /// Verified free slots (see <see cref="IsNativeSlotFree"/>) register natively in 3.29 <c>rulesets.creator_table</c>;
    /// <see cref="DefaultHostChannelRulesetId"/> (<c>0xA0</c>) is rejected; legacy pre-1.1.0 occupied non-host slots
    /// fall back to <c>0x02C9</c> filter-hook dispatch without touching <c>rulesets.creator_table</c>.
    /// </summary>
    IDisposable Register(byte rulesetId, Func<IClientRuleset> factory);

    /// <summary>
    /// Strictly registers a custom ruleset class into an unoccupied slot of native 3.29 <c>rulesets.creator_table</c>,
    /// rejecting all 68 stock-occupied slots (including <c>0xA0</c>).
    /// </summary>
    IDisposable RegisterNative<TRuleset>() where TRuleset : class, IClientRuleset, new() =>
        Register<TRuleset>();

    /// <summary>
    /// Strictly registers a custom ruleset factory into an unoccupied slot of native 3.29 <c>rulesets.creator_table</c>,
    /// rejecting all 68 stock-occupied slots (including <c>0xA0</c>) with a clear error pointing to verified free slots (<c>0xAE</c>, <c>0xAA</c>, <c>0xA9</c>, <c>0xA3</c>).
    /// </summary>
    IDisposable RegisterNative(byte rulesetId, Func<IClientRuleset> factory) =>
        Register(rulesetId, factory);

    /// <summary>Returns <c>true</c> if a custom ruleset is registered at <paramref name="rulesetId"/>.</summary>
    bool IsRegistered(byte rulesetId);

    /// <summary>Dispatches an incoming S→C <c>0x2c9</c> payload to the registered ruleset, returning <c>false</c> if absent (dropped silently).</summary>
    bool TryDispatchUpdate(byte rulesetId, ReadOnlySpan<byte> payload);
}

/// <summary>
/// Sequential reader over a live or synthetic native packet stream (<c>BufferStreamIn</c>).
/// </summary>
public interface INativePacketStream
{
    /// <summary>Attempts to read <paramref name="destination"/>.Length bytes from the stream.</summary>
    bool TryRead(Span<byte> destination);

    /// <summary>Reads exactly <paramref name="count"/> bytes from the stream or throws on underflow.</summary>
    byte[] ReadBytes(int count);

    /// <summary>Reads an unmanaged little-endian value of type <typeparamref name="T"/> from the stream.</summary>
    T Read<T>() where T : unmanaged;
}

/// <summary>
/// Registers custom network opcode handlers via the client's handler-group mechanism.
/// Per verified 3.29.3d findings, stock opcodes end at <c>0x2fb</c>, so reserved custom opcodes begin at <c>0x2fc</c>.
/// </summary>
public interface IOpcodeRegistry
{
    /// <summary>S→C ruleset update wire opcode (<c>0x2c9</c>).</summary>
    const ushort ServerRulesetOpcode = 0x02C9;

    /// <summary>C→S ruleset event wire opcode (<c>0x2ca</c>).</summary>
    const ushort ClientRulesetOpcode = 0x02CA;

    /// <summary>First free opcode above the 3.29.3d instance dispatcher upper bound (<c>0x2fb</c>).</summary>
    const ushort FirstReservedOpcode = 0x02FC;

    /// <summary>Opcodes currently claimed by registered handlers.</summary>
    IReadOnlyCollection<ushort> RegisteredOpcodes { get; }

    /// <summary>Registers a handler callback for an incoming opcode (<c>&gt;= 0x02FC</c>).</summary>
    /// <param name="opcode">Wire opcode identifier (<c>&gt;= 0x02FC</c>).</param>
    /// <param name="handler">Callback invoked with the raw payload bytes.</param>
    /// <param name="groupName">Optional logical handler group name.</param>
    IDisposable RegisterHandler(ushort opcode, Action<ReadOnlyMemory<byte>> handler, string? groupName = null);

    /// <summary>Registers a handler callback for an inclusive opcode range (<c>&gt;= 0x02FC</c>).</summary>
    /// <param name="minOpcode">Inclusive minimum opcode (<c>&gt;= 0x02FC</c>).</param>
    /// <param name="maxOpcode">Inclusive maximum opcode (<c>&gt;= minOpcode</c>).</param>
    /// <param name="handler">Callback receiving <c>(opcode, payload)</c>.</param>
    /// <param name="groupName">Optional logical handler group name.</param>
    IDisposable RegisterGroup(ushort minOpcode, ushort maxOpcode, Action<ushort, ReadOnlyMemory<byte>> handler, string? groupName = null);

    /// <summary>Registers a native stream-reading handler for an inclusive opcode range (<c>&gt;= 0x02FC</c>).</summary>
    /// <param name="minOpcode">Inclusive minimum opcode (<c>&gt;= 0x02FC</c>).</param>
    /// <param name="maxOpcode">Inclusive maximum opcode (<c>&gt;= minOpcode</c>).</param>
    /// <param name="handler">Callback receiving <c>(opcode, stream, preloadedPayload)</c> and returning <c>true</c> if handled.</param>
    /// <param name="groupName">Optional logical handler group name.</param>
    IDisposable RegisterStreamHandler(ushort minOpcode, ushort maxOpcode, Func<ushort, INativePacketStream, ReadOnlyMemory<byte>, bool> handler, string? groupName = null);

    /// <summary>Returns <c>true</c> if at least one handler is registered for <paramref name="opcode"/>.</summary>
    bool IsRegistered(ushort opcode);

    /// <summary>Dispatches a raw packet payload to registered handlers for <paramref name="opcode"/>.</summary>
    bool TryDispatch(ushort opcode, ReadOnlyMemory<byte> payload);
}

/// <summary>
/// Declares an append-only row to be merged into a client <c>.datc64</c> table.
/// </summary>
/// <param name="TableName">Target dat table name (for example <c>BaseItemTypes</c>, <c>MiscObjects</c>, <c>MonsterVarieties</c>).</param>
/// <param name="EntryKey">Unique entry key within the declaring mod.</param>
/// <param name="Fields">Column values for the appended row.</param>
public sealed record DatRowDeclaration(
    string TableName,
    string EntryKey,
    IReadOnlyDictionary<string, object?> Fields);

/// <summary>
/// Declares a new append-only item base riding an existing compiled <c>ItemClasses</c> enum value.
/// </summary>
public sealed record ItemBaseDeclaration(
    string MetadataPath,
    string Name,
    string InheritsFrom,
    string ItemClass,
    string ArtPath,
    int StackSize = 1,
    int Width = 1,
    int Height = 1);

/// <summary>
/// Declares a new server-creatable world object type using one of the 8 carrier dat tables (for example <c>MiscObjects</c>).
/// </summary>
public sealed record WorldObjectDeclaration(
    string MetadataPath,
    string CarrierTable,
    string? ScriptPath = null,
    string? OtOverlayContent = null);

/// <summary>
/// Declares an inline <c>.ot</c> or <c>.it</c> overlay text file.
/// </summary>
public sealed record ObjectOverlayDeclaration(
    string VirtualPath,
    string Content);

/// <summary>
/// Recorded row assignment and hash metadata for a single merged <c>.datc64</c> content entry
/// loaded from <c>Mods/Client/.merged-cache/content-row-map.json</c>.
/// </summary>
public sealed record ContentRowEntry
{
    /// <summary>Owning mod identifier that declared this entry.</summary>
    [JsonPropertyName("modId")]
    public required string ModId { get; init; }

    /// <summary>Deterministic zero-based row index inside the merged <c>.datc64</c> table.</summary>
    [JsonPropertyName("rowIndex")]
    public required int RowIndex { get; init; }

    /// <summary>Computed or explicit 32-bit MurmurHash2 (<c>HASH32</c>) for tables with a <c>HASH32</c> column.</summary>
    [JsonPropertyName("hash32")]
    public uint? Hash32 { get; init; }

    /// <summary>Allocated collision-free 16-bit hash (<c>HASH16</c>) for tables with a <c>HASH16</c> column.</summary>
    [JsonPropertyName("hash16")]
    public ushort? Hash16 { get; init; }

    /// <summary>Stock <c>Stats[row].Id</c> preserved for dead-stat slot allocations (null for appended rows).</summary>
    [JsonPropertyName("stockId")]
    public string? StockId { get; init; }
}

/// <summary>
/// Per-table merged row counts, SHA-256 checksum, and symbolic key map recorded in <c>content-row-map.json</c>.
/// </summary>
public sealed record ContentTableMap
{
    /// <summary>Number of rows in the stock 3.29 <c>.datc64</c> table prior to merging.</summary>
    [JsonPropertyName("stockRowCount")]
    public required int StockRowCount { get; init; }

    /// <summary>Total row count in the merged <c>.datc64</c> table.</summary>
    [JsonPropertyName("mergedRowCount")]
    public required int MergedRowCount { get; init; }

    /// <summary>Lowercase hex SHA-256 checksum of the merged <c>.datc64</c> binary.</summary>
    [JsonPropertyName("tableSha256")]
    public required string TableSha256 { get; init; }

    /// <summary>Symbolic entry keys mapped to their merged row index and allocated hashes.</summary>
    [JsonPropertyName("entries")]
    public required IReadOnlyDictionary<string, ContentRowEntry> Entries { get; init; }
}

/// <summary>
/// Metadata for a generated Route A text overlay (such as <c>Metadata/StatDescriptions/&lt;mod&gt;_custom_stats.txt</c>)
/// emitted alongside merged <c>.datc64</c> files in <c>.merged-cache/</c>.
/// </summary>
public sealed record ContentGeneratedOverlay
{
    /// <summary>Normalized virtual game path of the generated overlay.</summary>
    [JsonPropertyName("virtualPath")]
    public required string VirtualPath { get; init; }

    /// <summary>Lowercase hex SHA-256 checksum of the generated overlay file.</summary>
    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }
}

/// <summary>
/// Deterministic <c>content-row-map.json</c> manifest shared between the client host (<see cref="IContentRegistry"/>)
/// and <c>ServerBridge</c> so both sides resolve identical <c>(table, entryKey)</c> row indices, hashes, and
/// handshake digests.
/// </summary>
public sealed record ContentRowMap
{
    /// <summary>Current JSON schema version for <c>content-row-map.json</c>.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Canonical filename stored inside <c>Mods/Client/.merged-cache/</c>.</summary>
    public const string FileName = "content-row-map.json";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Schema version of this row map (currently <c>1</c>).</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Deterministic lowercase hex SHA-256 handshake digest covering all merged tables and overlays.</summary>
    [JsonPropertyName("digest")]
    public required string Digest { get; init; }

    /// <summary>Deterministically ordered list of mod identifiers included in this merged cache.</summary>
    [JsonPropertyName("mods")]
    public IReadOnlyList<string> Mods { get; init; } = [];

    /// <summary>Merged table summaries keyed by canonical <c>.datc64</c> table name.</summary>
    [JsonPropertyName("tables")]
    public IReadOnlyDictionary<string, ContentTableMap> Tables { get; init; } =
        new Dictionary<string, ContentTableMap>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Generated Route A text overlays included in this merged cache.</summary>
    [JsonPropertyName("generatedOverlays")]
    public IReadOnlyList<ContentGeneratedOverlay> GeneratedOverlays { get; init; } = [];

    /// <summary>
    /// Resolves a merged content entry by table name and symbolic entry key (case-insensitive on both).
    /// </summary>
    public bool TryResolveRow(string tableName, string entryKey, [NotNullWhen(true)] out ContentRowEntry? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(tableName) || string.IsNullOrWhiteSpace(entryKey))
        {
            return false;
        }

        string trimmedTable = tableName.Trim();
        string trimmedKey = entryKey.Trim();

        ContentTableMap? tableMap = null;
        if (!Tables.TryGetValue(trimmedTable, out tableMap))
        {
            foreach (KeyValuePair<string, ContentTableMap> kv in Tables)
            {
                if (string.Equals(kv.Key, trimmedTable, StringComparison.OrdinalIgnoreCase))
                {
                    tableMap = kv.Value;
                    break;
                }
            }
        }

        if (tableMap is null)
        {
            return false;
        }

        if (tableMap.Entries.TryGetValue(trimmedKey, out entry))
        {
            return true;
        }

        foreach (KeyValuePair<string, ContentRowEntry> kv in tableMap.Entries)
        {
            if (string.Equals(kv.Key, trimmedKey, StringComparison.OrdinalIgnoreCase))
            {
                entry = kv.Value;
                return true;
            }
        }

        return false;
    }

    /// <summary>Parses a <see cref="ContentRowMap"/> from <c>content-row-map.json</c> text.</summary>
    public static ContentRowMap Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ContentRowMap map = JsonSerializer.Deserialize<ContentRowMap>(json, ReadOptions)
            ?? throw new InvalidDataException("Failed to deserialize content-row-map.json: root evaluated to null.");
        if (map.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported content-row-map.json schemaVersion {map.SchemaVersion} (expected {CurrentSchemaVersion}).");
        }

        if (string.IsNullOrWhiteSpace(map.Digest))
        {
            throw new InvalidDataException("content-row-map.json is missing required 'digest'.");
        }

        return map;
    }

    /// <summary>Loads and parses a <see cref="ContentRowMap"/> from disk.</summary>
    public static ContentRowMap LoadFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllText(path));
    }

    /// <summary>Serializes this <see cref="ContentRowMap"/> back to canonical indented JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, WriteOptions);
}

/// <summary>
/// Collects append-only content declarations (dat rows, item bases, world objects, and <c>.ot</c>/<c>.it</c> overlays)
/// and exposes the live <c>content-row-map.json</c> mounted at <c>t=0</c> from <c>Mods/Client/.merged-cache/</c>.
/// </summary>
public interface IContentRegistry
{
    /// <summary>Live <see cref="ContentRowMap"/> parsed from <c>Mods/Client/.merged-cache/content-row-map.json</c> at <c>t=0</c>, or <c>null</c> if none is mounted.</summary>
    ContentRowMap? RowMap { get; }

    /// <summary>SHA-256 digest of the mounted <see cref="RowMap"/> advertised in <see cref="Protocol.ClientHandshake"/>, or <c>null</c> if no merged cache is mounted.</summary>
    string? ContentRowMapDigest { get; }

    /// <summary>All declared append-only dat table rows.</summary>
    IReadOnlyList<DatRowDeclaration> DatRows { get; }

    /// <summary>All declared item base definitions.</summary>
    IReadOnlyList<ItemBaseDeclaration> ItemBases { get; }

    /// <summary>All declared world object definitions.</summary>
    IReadOnlyList<WorldObjectDeclaration> WorldObjects { get; }

    /// <summary>All declared <c>.ot</c>/<c>.it</c> overlays.</summary>
    IReadOnlyList<ObjectOverlayDeclaration> Overlays { get; }

    /// <summary>
    /// Resolves a merged content row from <see cref="RowMap"/> by <paramref name="tableName"/> and <paramref name="entryKey"/>.
    /// </summary>
    bool TryResolveRow(string tableName, string entryKey, [NotNullWhen(true)] out ContentRowEntry? entry);

    /// <summary>Registers an append-only dat row declaration.</summary>
    IDisposable DeclareDatRow(DatRowDeclaration row);

    /// <summary>Registers a new item base declaration.</summary>
    IDisposable DeclareItemBase(ItemBaseDeclaration itemBase);

    /// <summary>Registers a new world object declaration (and its optional <c>.ot</c> overlay).</summary>
    IDisposable DeclareWorldObject(WorldObjectDeclaration worldObject);

    /// <summary>Registers a standalone <c>.ot</c> or <c>.it</c> overlay.</summary>
    IDisposable DeclareOverlay(ObjectOverlayDeclaration overlay);

    /// <summary>
    /// Computes a deterministic 32-bit FNV-1a identifier from <paramref name="modId"/> and <paramref name="entryKey"/>
    /// so client and server agree on content IDs across reloads without an extra sync round-trip.
    /// </summary>
    uint ComputeDeterministicId(string modId, string entryKey);
}
