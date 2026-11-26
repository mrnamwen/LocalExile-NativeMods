using System.Globalization;
using System.Text.Json;
using LocalExile.Client.AssetPipeline.Dat64;
using LocalExile.Client.Waypoint;

namespace LocalExile.Client.Cli;

/// <summary>
/// Result of statically validating one or more <c>.datpatch.json</c> files against <see cref="Dat64SchemaRegistry"/>.
/// </summary>
public sealed record DatPatchValidationResult(
    IReadOnlyList<ValidationDiagnostic> Diagnostics,
    IReadOnlyList<CrossModConflict> Conflicts,
    IReadOnlyList<DatPatchDocument> ParsedDocuments)
{
    public bool IsValid =>
        !Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error) &&
        Conflicts.Count == 0;
}

/// <summary>
/// Static schema and policy validator for declarative <c>.datpatch.json</c> patch files and multi-mod sets.
/// Runs without requiring stock <c>.datc64</c> binaries so <c>nativemods validate</c> works offline in CI.
/// </summary>
public static class DatPatchValidator
{
    private const string RefPrefix = "$ref:";

    private static readonly HashSet<string> ForbiddenStatsModifyColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "Id",
        "HASH32",
    };

    /// <summary>
    /// Validates a single mod's <c>.datpatch.json</c> files (and optionally checks cross-mod compatibility
    /// against additional <paramref name="companionPatchFiles"/> from <c>--against</c> mods).
    /// </summary>
    public static DatPatchValidationResult ValidateFiles(
        IReadOnlyList<string> primaryPatchFiles,
        IReadOnlyList<string>? companionPatchFiles = null,
        Dat64SchemaRegistry? schemaRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(primaryPatchFiles);

        Dat64SchemaRegistry registry = schemaRegistry ?? Dat64SchemaRegistry.LoadDefault329();
        var diagnostics = new List<ValidationDiagnostic>();
        var conflicts = new List<CrossModConflict>();
        var parsedPrimary = new List<(string FilePath, DatPatchDocument Doc)>();
        var parsedAll = new List<(string FilePath, DatPatchDocument Doc, bool IsPrimary)>();

        foreach (string file in primaryPatchFiles)
        {
            if (TryParsePatchFile(file, diagnostics, out DatPatchDocument? doc) && doc is not null)
            {
                parsedPrimary.Add((file, doc));
                parsedAll.Add((file, doc, true));
            }
        }

        if (companionPatchFiles is not null)
        {
            foreach (string file in companionPatchFiles)
            {
                if (TryParsePatchFile(file, diagnostics, out DatPatchDocument? doc) && doc is not null)
                {
                    parsedAll.Add((file, doc, false));
                }
            }
        }

        // First pass: register every symbolic (Table, EntryKey) across the combined set and detect cross-mod collisions.
        var modOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var symbolOwners = new Dictionary<(string Table, string Key), (string ModId, string FilePath)>(TupleKeyComparer.Instance);
        var preferredSlotOwners = new Dictionary<int, (string ModId, string Key)>();
        int totalDeadStatCount = 0;

        foreach (var (filePath, doc, isPrimary) in parsedAll)
        {
            if (modOwners.TryGetValue(doc.ModId, out string? existingFile))
            {
                conflicts.Add(new CrossModConflict(
                    "DPF001",
                    "DatPatchModId",
                    doc.ModId,
                    doc.ModId,
                    doc.ModId,
                    $"Duplicate .datpatch.json modId '{doc.ModId}' found in '{existingFile}' and '{filePath}'."));
            }
            else
            {
                modOwners[doc.ModId] = filePath;
            }

            foreach (DeadStatAllocationSpec deadSpec in doc.AllocateDeadStat)
            {
                totalDeadStatCount++;
                if (string.IsNullOrWhiteSpace(deadSpec.Key))
                {
                    if (isPrimary)
                    {
                        diagnostics.Add(new(DiagnosticSeverity.Error, "DAT004",
                            $"Mod '{doc.ModId}' contains an allocateDeadStat entry with an empty key.", filePath));
                    }

                    continue;
                }

                string trimmedKey = deadSpec.Key.Trim();
                var symKey = ("Stats", trimmedKey);
                if (symbolOwners.TryGetValue(symKey, out var prior))
                {
                    if (string.Equals(prior.ModId, doc.ModId, StringComparison.OrdinalIgnoreCase))
                    {
                        if (isPrimary)
                        {
                            diagnostics.Add(new(DiagnosticSeverity.Error, "DAT005",
                                $"Duplicate allocateDeadStat key '{trimmedKey}' within mod '{doc.ModId}'.", filePath));
                        }
                    }
                    else
                    {
                        conflicts.Add(new CrossModConflict(
                            "DPF002",
                            "DatPatchKey",
                            $"Stats:{trimmedKey}",
                            prior.ModId,
                            doc.ModId,
                            $"Duplicate dead-stat key 'Stats:{trimmedKey}' declared by both '{prior.ModId}' and '{doc.ModId}'."));
                    }
                }
                else
                {
                    symbolOwners[symKey] = (doc.ModId, filePath);
                }

                if (deadSpec.PreferredSlot.HasValue)
                {
                    int slot = deadSpec.PreferredSlot.Value;
                    if (slot == Dat64SchemaRegistry.ForbiddenDummyStatRow)
                    {
                        if (isPrimary)
                        {
                            diagnostics.Add(new(DiagnosticSeverity.Error, "DAT006",
                                $"Mod '{doc.ModId}' requested forbidden Stats row {Dat64SchemaRegistry.ForbiddenDummyStatRow} ('dummy_stat_display_nothing') for '{trimmedKey}'.",
                                filePath));
                        }
                    }
                    else if (!Dat64SchemaRegistry.DeadStatSlotSet.Contains(slot))
                    {
                        if (isPrimary)
                        {
                            diagnostics.Add(new(DiagnosticSeverity.Error, "DAT007",
                                $"Mod '{doc.ModId}' requested Stats row {slot} for '{trimmedKey}', which is not in the 30 verified dead stock slots.",
                                filePath));
                        }
                    }
                    else if (preferredSlotOwners.TryGetValue(slot, out var priorSlot))
                    {
                        if (string.Equals(priorSlot.ModId, doc.ModId, StringComparison.OrdinalIgnoreCase))
                        {
                            if (isPrimary)
                            {
                                diagnostics.Add(new(DiagnosticSeverity.Error, "DAT008",
                                    $"Mod '{doc.ModId}' assigned preferredSlot {slot} to both '{priorSlot.Key}' and '{trimmedKey}'.",
                                    filePath));
                            }
                        }
                        else
                        {
                            conflicts.Add(new CrossModConflict(
                                "DPF003",
                                "DeadStatSlot",
                                $"Stats[{slot}]",
                                priorSlot.ModId,
                                doc.ModId,
                                $"Preferred dead Stats slot {slot} collides between '{priorSlot.ModId}' ('{priorSlot.Key}') and '{doc.ModId}' ('{trimmedKey}')."));
                        }
                    }
                    else
                    {
                        preferredSlotOwners[slot] = (doc.ModId, trimmedKey);
                    }
                }
            }

            foreach (var (rawTableName, tablePatch) in doc.Tables)
            {
                if (tablePatch is null)
                {
                    continue;
                }

                if (!registry.TryGetTable(rawTableName, out Dat64TableSchema? schema) || schema is null)
                {
                    if (isPrimary)
                    {
                        diagnostics.Add(new(DiagnosticSeverity.Error, "DAT009",
                            $"Unknown .datc64 table '{rawTableName}' in mod '{doc.ModId}' (not present in 3.29 schema registry).",
                            filePath));
                    }

                    continue;
                }

                string canonicalName = schema.Name;

                if (tablePatch.Append.Count > 0)
                {
                    if (Dat64SchemaRegistry.ClosedRegistryTables.Contains(canonicalName))
                    {
                        if (isPrimary)
                        {
                            diagnostics.Add(new(DiagnosticSeverity.Error, "DAT010",
                                $"Mod '{doc.ModId}' cannot append rows to closed table '{canonicalName}'.",
                                filePath));
                        }
                    }
                    else if (!Dat64SchemaRegistry.AppendableRegistryTables.Contains(canonicalName))
                    {
                        if (isPrimary)
                        {
                            diagnostics.Add(new(DiagnosticSeverity.Error, "DAT011",
                                $"Mod '{doc.ModId}' cannot append rows to non-appendable table '{canonicalName}'.",
                                filePath));
                        }
                    }

                    foreach (DatAppendEntry entry in tablePatch.Append)
                    {
                        if (string.IsNullOrWhiteSpace(entry.Key))
                        {
                            if (isPrimary)
                            {
                                diagnostics.Add(new(DiagnosticSeverity.Error, "DAT012",
                                    $"Mod '{doc.ModId}' has an append entry on '{canonicalName}' with an empty key.",
                                    filePath));
                            }

                            continue;
                        }

                        string trimmedKey = entry.Key.Trim();
                        var symKey = (canonicalName, trimmedKey);
                        if (symbolOwners.TryGetValue(symKey, out var prior))
                        {
                            if (string.Equals(prior.ModId, doc.ModId, StringComparison.OrdinalIgnoreCase))
                            {
                                if (isPrimary)
                                {
                                    diagnostics.Add(new(DiagnosticSeverity.Error, "DAT013",
                                        $"Duplicate append key '{canonicalName}:{trimmedKey}' within mod '{doc.ModId}'.",
                                        filePath));
                                }
                            }
                            else
                            {
                                conflicts.Add(new CrossModConflict(
                                    "DPF002",
                                    "DatPatchKey",
                                    $"{canonicalName}:{trimmedKey}",
                                    prior.ModId,
                                    doc.ModId,
                                    $"Duplicate append entry key '{canonicalName}:{trimmedKey}' declared by both '{prior.ModId}' and '{doc.ModId}'."));
                            }
                        }
                        else
                        {
                            symbolOwners[symKey] = (doc.ModId, filePath);
                        }
                    }
                }
            }
        }

        if (totalDeadStatCount > Dat64SchemaRegistry.DeadStatSlots.Count)
        {
            if (companionPatchFiles is { Count: > 0 })
            {
                conflicts.Add(new CrossModConflict(
                    "DPF004",
                    "DeadStatPool",
                    "Stats",
                    parsedAll.First().Doc.ModId,
                    parsedAll.Last().Doc.ModId,
                    $"Combined mods request {totalDeadStatCount} dead Stats slots, exceeding the 30 verified dead slots."));
            }
            else
            {
                diagnostics.Add(new(DiagnosticSeverity.Error, "DAT014",
                    $"Mod requests {totalDeadStatCount} dead Stats slots, exceeding the 30 verified dead slots."));
            }
        }

        // Check dependency cycles across parsed documents.
        ValidateDependencyGraph(parsedAll, diagnostics, conflicts);

        // Second pass: validate per-entry columns, forbidden Stats edits, and symbolic $ref targets on primary docs.
        foreach (var (filePath, doc) in parsedPrimary)
        {
            ValidatePrimaryDocumentDetails(filePath, doc, registry, symbolOwners, diagnostics);
        }

        return new DatPatchValidationResult(
            diagnostics,
            conflicts,
            parsedPrimary.Select(p => p.Doc).ToList());
    }

    private static bool TryParsePatchFile(
        string filePath,
        List<ValidationDiagnostic> diagnostics,
        out DatPatchDocument? doc)
    {
        doc = null;
        if (!File.Exists(filePath))
        {
            diagnostics.Add(new(DiagnosticSeverity.Error, "DAT001", $"Patch file not found: '{filePath}'.", filePath));
            return false;
        }

        try
        {
            doc = DatPatchDocument.LoadFromFile(filePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            diagnostics.Add(new(DiagnosticSeverity.Error, "DAT002", $"Failed to parse .datpatch.json: {ex.Message}", filePath));
            return false;
        }
    }

    private static void ValidatePrimaryDocumentDetails(
        string filePath,
        DatPatchDocument doc,
        Dat64SchemaRegistry registry,
        Dictionary<(string Table, string Key), (string ModId, string FilePath)> symbolOwners,
        List<ValidationDiagnostic> diagnostics)
    {
        if (registry.TryGetTable("Stats", out Dat64TableSchema? statsSchema) && statsSchema is not null)
        {
            foreach (DeadStatAllocationSpec deadSpec in doc.AllocateDeadStat)
            {
                ValidateFields(
                    filePath,
                    doc.ModId,
                    $"allocateDeadStat '{deadSpec.Key}'",
                    statsSchema,
                    deadSpec.Fields,
                    symbolOwners,
                    forbidStatsId: true,
                    diagnostics);

                if (!string.IsNullOrWhiteSpace(deadSpec.OutputPath))
                {
                    string normalized = deadSpec.OutputPath.Replace('\\', '/').Trim().TrimStart('/');
                    if (normalized.Contains("..", StringComparison.Ordinal) || normalized.Contains(':', StringComparison.Ordinal))
                    {
                        diagnostics.Add(new(DiagnosticSeverity.Error, "DAT015",
                            $"Unsafe allocateDeadStat outputPath '{deadSpec.OutputPath}' in mod '{doc.ModId}'.",
                            filePath));
                    }
                }
            }
        }

        foreach (var (rawTableName, tablePatch) in doc.Tables)
        {
            if (tablePatch is null || !registry.TryGetTable(rawTableName, out Dat64TableSchema? schema) || schema is null)
            {
                continue;
            }

            bool isStats = schema.Name.Equals("Stats", StringComparison.OrdinalIgnoreCase);

            foreach (DatAppendEntry entry in tablePatch.Append)
            {
                if (entry.CloneFromRow.HasValue && !string.IsNullOrWhiteSpace(entry.CloneFromId))
                {
                    diagnostics.Add(new(DiagnosticSeverity.Error, "DAT016",
                        $"Append entry '{entry.Key}' on '{schema.Name}' specifies both cloneFromRow and cloneFromId.",
                        filePath));
                }
                else if (entry.CloneFromRow is < 0)
                {
                    diagnostics.Add(new(DiagnosticSeverity.Error, "DAT017",
                        $"Append entry '{entry.Key}' on '{schema.Name}' has negative cloneFromRow ({entry.CloneFromRow.Value}).",
                        filePath));
                }

                ValidateFields(
                    filePath,
                    doc.ModId,
                    $"append '{entry.Key}'",
                    schema,
                    entry.Fields,
                    symbolOwners,
                    forbidStatsId: false,
                    diagnostics);
            }

            foreach (DatModifyEntry modEntry in tablePatch.Modify)
            {
                bool hasRow = modEntry.RowIndex.HasValue;
                bool hasId = !string.IsNullOrWhiteSpace(modEntry.Id);
                if (hasRow == hasId)
                {
                    diagnostics.Add(new(DiagnosticSeverity.Error, "DAT018",
                        $"Modify entry on '{schema.Name}' in mod '{doc.ModId}' must specify exactly one of 'rowIndex' or 'id'.",
                        filePath));
                }
                else if (modEntry.RowIndex is < 0)
                {
                    diagnostics.Add(new(DiagnosticSeverity.Error, "DAT017",
                        $"Modify entry on '{schema.Name}' has negative rowIndex ({modEntry.RowIndex.Value}).",
                        filePath));
                }

                if (isStats && modEntry.RowIndex == Dat64SchemaRegistry.ForbiddenDummyStatRow)
                {
                    diagnostics.Add(new(DiagnosticSeverity.Error, "DAT006",
                        $"Mod '{doc.ModId}' attempted to modify forbidden Stats row {Dat64SchemaRegistry.ForbiddenDummyStatRow} ('dummy_stat_display_nothing').",
                        filePath));
                }

                ValidateFields(
                    filePath,
                    doc.ModId,
                    $"modify {(hasId ? $"'{modEntry.Id}'" : $"row {modEntry.RowIndex}")}",
                    schema,
                    modEntry.Fields,
                    symbolOwners,
                    forbidStatsId: isStats,
                    diagnostics);
            }
        }
    }

    private static void ValidateFields(
        string filePath,
        string modId,
        string entryContext,
        Dat64TableSchema schema,
        IReadOnlyDictionary<string, JsonElement> fields,
        Dictionary<(string Table, string Key), (string ModId, string FilePath)> symbolOwners,
        bool forbidStatsId,
        List<ValidationDiagnostic> diagnostics)
    {
        foreach (var (fieldKey, value) in fields)
        {
            if (!schema.TryResolveColumn(fieldKey, out Dat64ColumnSchema? col) || col is null)
            {
                diagnostics.Add(new(DiagnosticSeverity.Error, "DAT019",
                    $"Unknown column '{fieldKey}' on table '{schema.Name}' in mod '{modId}' ({entryContext}).",
                    filePath));
                continue;
            }

            if (forbidStatsId && (ForbiddenStatsModifyColumns.Contains(col.Name) || col.Offset == 0))
            {
                diagnostics.Add(new(DiagnosticSeverity.Error, "DAT020",
                    $"Protected column 'Stats.{col.Name}' cannot be modified in mod '{modId}' ({entryContext}) without breaking LocalExile Stat.Checksum.",
                    filePath));
                continue;
            }

            if (col.IsArray)
            {
                if (value.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement el in value.EnumerateArray())
                    {
                        ValidateSymbolicRefIfString(filePath, modId, entryContext, schema.Name, col, el, symbolOwners, diagnostics);
                    }
                }
                else if (value.ValueKind != JsonValueKind.Null)
                {
                    diagnostics.Add(new(DiagnosticSeverity.Error, "DAT021",
                        $"Column '{schema.Name}.{col.Name}' expects a JSON array or null in mod '{modId}' ({entryContext}), got {value.ValueKind}.",
                        filePath));
                }
            }
            else
            {
                ValidateSymbolicRefIfString(filePath, modId, entryContext, schema.Name, col, value, symbolOwners, diagnostics);

                if (col.Name.Equals("HASH16", StringComparison.OrdinalIgnoreCase) &&
                    value.ValueKind == JsonValueKind.Number &&
                    value.TryGetInt64(out long h16) &&
                    h16 == 0)
                {
                    diagnostics.Add(new(DiagnosticSeverity.Error, "DAT022",
                        $"Column '{schema.Name}.HASH16' cannot be 0 in mod '{modId}' ({entryContext}).",
                        filePath));
                }
            }
        }
    }

    private static void ValidateSymbolicRefIfString(
        string filePath,
        string modId,
        string entryContext,
        string tableName,
        Dat64ColumnSchema col,
        JsonElement element,
        Dictionary<(string Table, string Key), (string ModId, string FilePath)> symbolOwners,
        List<ValidationDiagnostic> diagnostics)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            return;
        }

        string raw = element.GetString() ?? string.Empty;
        if (!raw.StartsWith(RefPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string remainder = raw[RefPrefix.Length..];
        int sep = remainder.IndexOf(':', StringComparison.Ordinal);
        if (sep <= 0 || sep + 1 >= remainder.Length)
        {
            diagnostics.Add(new(DiagnosticSeverity.Error, "DAT023",
                $"Malformed symbolic reference '{raw}' on '{tableName}.{col.Name}' in mod '{modId}' ({entryContext}). Expected '$ref:TableName:EntryKey'.",
                filePath));
            return;
        }

        string targetTable = remainder[..sep].Trim();
        string targetKey = remainder[(sep + 1)..].Trim();

        if (!string.IsNullOrWhiteSpace(col.ReferenceTable) &&
            !col.ReferenceTable.Equals(targetTable, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new(DiagnosticSeverity.Error, "DAT024",
                $"Cross-table $ref type mismatch on '{tableName}.{col.Name}' in mod '{modId}' ({entryContext}): column references '{col.ReferenceTable}', but '{raw}' targets '{targetTable}'.",
                filePath));
        }

        if (!symbolOwners.ContainsKey((targetTable, targetKey)))
        {
            diagnostics.Add(new(DiagnosticSeverity.Error, "DAT025",
                $"Unresolved symbolic reference '{raw}' on '{tableName}.{col.Name}' in mod '{modId}' ({entryContext}): no entry '{targetKey}' declared for table '{targetTable}'.",
                filePath));
        }
    }

    private static void ValidateDependencyGraph(
        List<(string FilePath, DatPatchDocument Doc, bool IsPrimary)> parsedAll,
        List<ValidationDiagnostic> diagnostics,
        List<CrossModConflict> conflicts)
    {
        var byId = new Dictionary<string, (string FilePath, DatPatchDocument Doc, bool IsPrimary)>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in parsedAll)
        {
            byId.TryAdd(item.Doc.ModId, item);
        }

        var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var dependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in byId.Keys)
        {
            inDegree[id] = 0;
            dependents[id] = [];
        }

        foreach (var (filePath, doc, isPrimary) in byId.Values)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string dep in doc.Depends)
            {
                if (string.IsNullOrWhiteSpace(dep))
                {
                    continue;
                }

                if (dep.Equals(doc.ModId, StringComparison.OrdinalIgnoreCase))
                {
                    if (isPrimary)
                    {
                        diagnostics.Add(new(DiagnosticSeverity.Error, "DAT026",
                            $"Mod '{doc.ModId}' cannot depend on itself in .datpatch.json.",
                            filePath));
                    }

                    continue;
                }

                if (byId.ContainsKey(dep) && seen.Add(dep))
                {
                    dependents[dep].Add(doc.ModId);
                    inDegree[doc.ModId]++;
                }
            }
        }

        var queue = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        int visited = 0;
        while (queue.Count > 0)
        {
            string cur = queue.Dequeue();
            visited++;
            foreach (string next in dependents[cur])
            {
                inDegree[next]--;
                if (inDegree[next] == 0)
                {
                    queue.Enqueue(next);
                }
            }
        }

        if (visited != byId.Count)
        {
            string[] cycleMembers = inDegree.Where(kv => kv.Value > 0)
                .Select(kv => kv.Key)
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            conflicts.Add(new CrossModConflict(
                "DPF005",
                "DependencyCycle",
                string.Join(" -> ", cycleMembers),
                cycleMembers.First(),
                cycleMembers.Last(),
                string.Create(CultureInfo.InvariantCulture, $"Dependency cycle detected among .datpatch.json mods: [{string.Join(", ", cycleMembers)}].")));
        }
    }

    private sealed class TupleKeyComparer : IEqualityComparer<(string Table, string Key)>
    {
        public static readonly TupleKeyComparer Instance = new();

        public bool Equals((string Table, string Key) x, (string Table, string Key) y) =>
            string.Equals(x.Table, y.Table, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Key, y.Key, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Table, string Key) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Table),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Key));
    }
}
