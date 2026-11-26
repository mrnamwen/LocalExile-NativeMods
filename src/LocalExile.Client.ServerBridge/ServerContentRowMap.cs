using System.Diagnostics.CodeAnalysis;
using LocalExile.Client.Sdk.Extend;

namespace LocalExile.Client.ServerBridge;

/// <summary>
/// Server-side helper loader and resolver for <c>Mods/Client/.merged-cache/content-row-map.json</c>.
/// Allows LocalExile server mods to load the Waypoint-built row map, bind its SHA-256 digest onto
/// <see cref="ClientBridgeHost"/> for handshake verification, and resolve symbolic
/// <c>(table, entryKey) -&gt; { rowIndex, hash32, hash16, stockId }</c> values deterministically.
/// </summary>
public sealed class ServerContentRowMap
{
    /// <summary>Underlying parsed <see cref="ContentRowMap"/> manifest.</summary>
    public ContentRowMap RowMap { get; }

    /// <summary>Deterministic SHA-256 digest of <see cref="RowMap"/> matching <see cref="Sdk.Protocol.ClientHandshake.ContentRowMapDigest"/>.</summary>
    public string Digest => RowMap.Digest;

    /// <summary>Deterministically ordered mod identifiers included in this merged cache.</summary>
    public IReadOnlyList<string> Mods => RowMap.Mods;

    /// <summary>Creates a new <see cref="ServerContentRowMap"/> wrapper around <paramref name="rowMap"/>.</summary>
    public ServerContentRowMap(ContentRowMap rowMap)
    {
        RowMap = rowMap ?? throw new ArgumentNullException(nameof(rowMap));
    }

    /// <summary>Parses a <c>content-row-map.json</c> string into a <see cref="ServerContentRowMap"/>.</summary>
    public static ServerContentRowMap Parse(string json) =>
        new(ContentRowMap.Parse(json));

    /// <summary>Loads <c>content-row-map.json</c> directly from <paramref name="path"/>.</summary>
    public static ServerContentRowMap LoadFromFile(string path) =>
        new(ContentRowMap.LoadFromFile(path));

    /// <summary>
    /// Attempts to load <c>content-row-map.json</c> from a <c>.merged-cache/</c> directory (or directly from a file path).
    /// Returns <c>null</c> if the file does not exist.
    /// </summary>
    public static ServerContentRowMap? TryLoadFromMergedCache(string mergedCachePathOrFile)
    {
        if (string.IsNullOrWhiteSpace(mergedCachePathOrFile))
        {
            return null;
        }

        string filePath = File.Exists(mergedCachePathOrFile)
            ? mergedCachePathOrFile
            : Path.Combine(mergedCachePathOrFile, ContentRowMap.FileName);

        return File.Exists(filePath) ? LoadFromFile(filePath) : null;
    }

    /// <summary>
    /// Attempts to load <c>&lt;localExileRoot&gt;/Mods/Client/.merged-cache/content-row-map.json</c>.
    /// Returns <c>null</c> if no merged cache exists.
    /// </summary>
    public static ServerContentRowMap? TryLoadFromLocalExileRoot(string localExileRoot)
    {
        if (string.IsNullOrWhiteSpace(localExileRoot))
        {
            return null;
        }

        string mergedCacheDir = Path.Combine(localExileRoot, "Mods", "Client", ".merged-cache");
        return TryLoadFromMergedCache(mergedCacheDir);
    }

    /// <summary>
    /// Loads <c>content-row-map.json</c> from <paramref name="pathOrMergedCacheDir"/> and binds it onto
    /// <paramref name="bridge"/> so all incoming client handshakes are verified against its digest.
    /// </summary>
    public static ServerContentRowMap? TryLoadAndBind(ClientBridgeHost bridge, string pathOrMergedCacheDir)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ServerContentRowMap? loaded = TryLoadFromMergedCache(pathOrMergedCacheDir);
        if (loaded is not null)
        {
            bridge.SetExpectedContentRowMap(loaded.RowMap);
        }

        return loaded;
    }

    /// <summary>Resolves a merged entry by table name and symbolic entry key.</summary>
    public bool TryResolveRow(string tableName, string entryKey, [NotNullWhen(true)] out ContentRowEntry? entry) =>
        RowMap.TryResolveRow(tableName, entryKey, out entry);

    /// <summary>Resolves a merged entry or throws <see cref="KeyNotFoundException"/> if absent.</summary>
    public ContentRowEntry RequireRow(string tableName, string entryKey)
    {
        if (RowMap.TryResolveRow(tableName, entryKey, out ContentRowEntry? entry))
        {
            return entry;
        }

        throw new KeyNotFoundException(
            $"Entry ('{tableName}', '{entryKey}') was not found in content-row-map.json (digest {Digest}).");
    }

    /// <summary>Resolves the merged row index for <c>(tableName, entryKey)</c>.</summary>
    public int RequireRowIndex(string tableName, string entryKey) =>
        RequireRow(tableName, entryKey).RowIndex;

    /// <summary>Resolves the computed <c>HASH32</c> for <c>(tableName, entryKey)</c>.</summary>
    public uint RequireHash32(string tableName, string entryKey)
    {
        ContentRowEntry entry = RequireRow(tableName, entryKey);
        return entry.Hash32
            ?? throw new InvalidOperationException($"Entry ('{tableName}', '{entryKey}') does not record a HASH32 value.");
    }

    /// <summary>Resolves the allocated <c>HASH16</c> for <c>(tableName, entryKey)</c>.</summary>
    public ushort RequireHash16(string tableName, string entryKey)
    {
        ContentRowEntry entry = RequireRow(tableName, entryKey);
        return entry.Hash16
            ?? throw new InvalidOperationException($"Entry ('{tableName}', '{entryKey}') does not record a HASH16 value.");
    }

    /// <summary>Resolves the preserved stock <c>Stats[row].Id</c> for a dead-stat allocation.</summary>
    public string RequireStockId(string tableName, string entryKey)
    {
        ContentRowEntry entry = RequireRow(tableName, entryKey);
        return !string.IsNullOrWhiteSpace(entry.StockId)
            ? entry.StockId
            : throw new InvalidOperationException($"Entry ('{tableName}', '{entryKey}') does not record a stockId value.");
    }
}
