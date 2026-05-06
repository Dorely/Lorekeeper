using System.Text.Json;

namespace Lorekeeper.Ingest;

public static class IngestSourceAssertions
{
    public const string EntityAssertionsProperty = "ingestSourceAssertionsJson";
    public const string RelationshipAssertionsProperty = "ingestRelationshipAssertionsJson";
    public const string EntityGraphActionProperty = "entityGraphAction";
    public const string RelationshipGraphActionProperty = "relationshipGraphAction";
    public const string GraphOriginProperty = "ingestGraphOrigin";
    public const string GraphOriginIngestValue = "ingest";
    public const string CreatedEntityAction = "CreatedEntity";
    public const string LinkedExistingEntityAction = "LinkedExistingEntity";
    public const string CreatedEdgeAction = "CreatedEdge";
    public const string LinkedExistingEdgeAction = "LinkedExistingEdge";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> ProtectedProperties =
    [
        EntityAssertionsProperty,
        RelationshipAssertionsProperty,
        GraphOriginProperty,
    ];

    private static readonly HashSet<string> LegacyIngestProperties =
    [
        "ingestJobId",
        "sourceChunkIndexes",
        "firstSeenSourceChunkIndex",
        "latestSeenSourceChunkIndex",
        "aliases",
        "evidence",
        "extractionNotes",
        "ingestReportNotes",
        "summary",
        "notes",
    ];

    public static bool IsProtectedProperty(string key) =>
        ProtectedProperties.Contains(key);

    public static bool IsLegacyIngestProperty(string key) =>
        LegacyIngestProperties.Contains(key);

    public static string SourceKey(Guid sourceId) => sourceId.ToString("N");

    public static string ChunkKey(Guid sourceChunkId) => sourceChunkId.ToString("N");

    public static bool IsIngestCreatedGraphObject(IReadOnlyDictionary<string, object?> properties) =>
        properties.TryGetValue(GraphOriginProperty, out var origin)
        && string.Equals(origin?.ToString(), GraphOriginIngestValue, StringComparison.OrdinalIgnoreCase);

    public static IngestAssertionWriteResult UpsertEntityAssertion(
        IDictionary<string, object?> properties,
        IngestAssertionInput input) =>
        UpsertAssertion(properties, EntityAssertionsProperty, input);

    public static IngestAssertionWriteResult UpsertEntityAssertion(
        IDictionary<string, string?> properties,
        IngestAssertionInput input) =>
        UpsertAssertion(properties, EntityAssertionsProperty, input);

    public static IngestAssertionWriteResult UpsertRelationshipAssertion(
        IDictionary<string, object?> properties,
        IngestAssertionInput input) =>
        UpsertAssertion(properties, RelationshipAssertionsProperty, input);

    public static IngestAssertionWriteResult UpsertRelationshipAssertion(
        IDictionary<string, string?> properties,
        IngestAssertionInput input) =>
        UpsertAssertion(properties, RelationshipAssertionsProperty, input);

    public static IngestAssertionRemovalResult RemoveEntitySource(
        IDictionary<string, object?> properties,
        Guid sourceId) =>
        RemoveSource(properties, EntityAssertionsProperty, sourceId);

    public static IngestAssertionRemovalResult RemoveRelationshipSource(
        IDictionary<string, object?> properties,
        Guid sourceId) =>
        RemoveSource(properties, RelationshipAssertionsProperty, sourceId);

    public static int CountEntitySources(IReadOnlyDictionary<string, object?> properties) =>
        CountSources(properties, EntityAssertionsProperty);

    public static int CountRelationshipSources(IReadOnlyDictionary<string, object?> properties) =>
        CountSources(properties, RelationshipAssertionsProperty);

    public static IReadOnlyList<IngestSourceAssertionSummary> SummarizeEntityAssertions(
        IReadOnlyDictionary<string, object?> properties,
        int maxSources = 5) =>
        SummarizeAssertions(properties, EntityAssertionsProperty, maxSources);

    public static IReadOnlyList<IngestSourceAssertionSummary> SummarizeRelationshipAssertions(
        IReadOnlyDictionary<string, object?> properties,
        int maxSources = 5) =>
        SummarizeAssertions(properties, RelationshipAssertionsProperty, maxSources);

    public static bool TryReadPayloadString(string payloadJson, string propertyName, out string? value)
    {
        value = null;
        if (!LooksLikeJsonRoot(payloadJson, '{')) return false;

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (!doc.RootElement.TryGetProperty(propertyName, out var property)) return false;
            value = property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : property.GetRawText();
            return !string.IsNullOrWhiteSpace(value);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static Guid? ReadPayloadSourceId(string payloadJson)
    {
        if (!TryReadPayloadString(payloadJson, "sourceId", out var sourceId)) return null;
        return Guid.TryParse(sourceId, out var parsed) ? parsed : null;
    }

    public static string? ReadEntityGraphAction(string payloadJson) =>
        TryReadPayloadString(payloadJson, EntityGraphActionProperty, out var action) ? action : null;

    public static string? ReadRelationshipGraphAction(string payloadJson) =>
        TryReadPayloadString(payloadJson, RelationshipGraphActionProperty, out var action) ? action : null;

    private static IngestAssertionWriteResult UpsertAssertion(
        IDictionary<string, object?> properties,
        string propertyKey,
        IngestAssertionInput input)
    {
        var document = ReadDocument(ReadRaw(properties, propertyKey));
        var result = UpsertAssertion(document, input);
        properties[propertyKey] = Serialize(document);
        return result;
    }

    private static IngestAssertionWriteResult UpsertAssertion(
        IDictionary<string, string?> properties,
        string propertyKey,
        IngestAssertionInput input)
    {
        var document = ReadDocument(properties.TryGetValue(propertyKey, out var raw) ? raw : null);
        var result = UpsertAssertion(document, input);
        properties[propertyKey] = Serialize(document);
        return result;
    }

    private static IngestAssertionWriteResult UpsertAssertion(
        IngestSourceAssertionDocument document,
        IngestAssertionInput input)
    {
        var now = DateTime.UtcNow;
        var sourceKey = SourceKey(input.SourceId);
        var chunkKey = ChunkKey(input.SourceChunkId);
        if (!document.Sources.TryGetValue(sourceKey, out var source))
        {
            source = new IngestSourceAssertion
            {
                SourceId = sourceKey,
                SourceTitle = input.SourceTitle.Trim(),
                SourceKind = input.SourceKind.Trim(),
                CreatedAt = now,
            };
            document.Sources[sourceKey] = source;
        }

        source.SourceTitle = input.SourceTitle.Trim();
        source.SourceKind = input.SourceKind.Trim();
        source.UpdatedAt = now;
        AddUnique(source.JobIds, input.JobId.ToString("N"));

        if (!source.Chunks.TryGetValue(chunkKey, out var chunk))
        {
            chunk = new IngestSourceChunkAssertion
            {
                JobId = input.JobId.ToString("N"),
                SourceChunkId = chunkKey,
                SourceChunkIndex = input.SourceChunkIndex,
                RecordedAt = now,
            };
            source.Chunks[chunkKey] = chunk;
        }

        chunk.JobId = input.JobId.ToString("N");
        chunk.SourceChunkIndex = input.SourceChunkIndex;
        chunk.UpdatedAt = now;
        if (!string.IsNullOrWhiteSpace(input.Summary))
            chunk.Summary = input.Summary.Trim();
        MergeProperties(chunk.ObservedProperties, input.ObservedProperties);
        MergeAliases(chunk.Aliases, input.Aliases);
        chunk.Evidence = MergeText(chunk.Evidence, input.Evidence, input.ReplaceExistingText);
        chunk.Notes = MergeText(chunk.Notes, input.Notes, input.ReplaceExistingText);

        return new IngestAssertionWriteResult(sourceKey, chunkKey, document.Sources.Count, source.Chunks.Count);
    }

    private static IngestAssertionRemovalResult RemoveSource(
        IDictionary<string, object?> properties,
        string propertyKey,
        Guid sourceId)
    {
        var document = ReadDocument(ReadRaw(properties, propertyKey));
        var sourceKey = SourceKey(sourceId);
        var removed = document.Sources.Remove(sourceKey);
        if (document.Sources.Count == 0)
            properties.Remove(propertyKey);
        else
            properties[propertyKey] = Serialize(document);
        return new IngestAssertionRemovalResult(removed, document.Sources.Count);
    }

    private static int CountSources(IReadOnlyDictionary<string, object?> properties, string propertyKey)
    {
        var document = ReadDocument(ReadRaw(properties, propertyKey));
        return document.Sources.Count;
    }

    private static IReadOnlyList<IngestSourceAssertionSummary> SummarizeAssertions(
        IReadOnlyDictionary<string, object?> properties,
        string propertyKey,
        int maxSources)
    {
        var document = ReadDocument(ReadRaw(properties, propertyKey));
        return document.Sources.Values
            .OrderBy(source => source.SourceTitle, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, maxSources))
            .Select(source => new IngestSourceAssertionSummary(
                source.SourceId,
                source.SourceTitle,
                source.SourceKind,
                source.Chunks.Count,
                source.Chunks.Values
                    .SelectMany(chunk => chunk.Aliases)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
                    .Take(12)
                    .ToArray(),
                source.Chunks.Values
                    .Select(chunk => chunk.Summary)
                    .FirstOrDefault(summary => !string.IsNullOrWhiteSpace(summary)) ?? string.Empty,
                source.UpdatedAt))
            .ToList();
    }

    private static IngestSourceAssertionDocument ReadDocument(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson) || !LooksLikeJsonRoot(rawJson, '{'))
            return new IngestSourceAssertionDocument();

        try
        {
            return JsonSerializer.Deserialize<IngestSourceAssertionDocument>(rawJson, JsonOptions)
                ?? new IngestSourceAssertionDocument();
        }
        catch (JsonException)
        {
            return new IngestSourceAssertionDocument();
        }
    }

    private static string Serialize(IngestSourceAssertionDocument document) =>
        JsonSerializer.Serialize(document, JsonOptions);

    private static string? ReadRaw(IDictionary<string, object?> properties, string propertyKey) =>
        properties.TryGetValue(propertyKey, out var value) ? value?.ToString() : null;

    private static string? ReadRaw(IReadOnlyDictionary<string, object?> properties, string propertyKey) =>
        properties.TryGetValue(propertyKey, out var value) ? value?.ToString() : null;

    private static void MergeProperties(
        IDictionary<string, string?> target,
        IReadOnlyDictionary<string, string?> source)
    {
        foreach (var kv in source)
        {
            var key = (kv.Key ?? string.Empty).Trim();
            if (key.Length == 0 || IsProtectedProperty(key)) continue;
            target[key] = kv.Value;
        }
    }

    private static void MergeAliases(ICollection<string> target, IReadOnlyList<string> aliases)
    {
        var existing = target.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in aliases)
        {
            var trimmed = alias.Trim();
            if (trimmed.Length > 0 && existing.Add(trimmed))
                target.Add(trimmed);
        }
    }

    private static string MergeText(string target, string? next, bool replaceExisting)
    {
        if (string.IsNullOrWhiteSpace(next)) return target;
        var trimmed = next.Trim();
        if (replaceExisting || string.IsNullOrWhiteSpace(target))
            return trimmed;

        if (!target.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            return target.TrimEnd() + "\n" + trimmed;
        return target;
    }

    private static void AddUnique(ICollection<string> values, string value)
    {
        if (!values.Contains(value, StringComparer.OrdinalIgnoreCase))
            values.Add(value);
    }

    private static bool LooksLikeJsonRoot(string json, char rootChar)
    {
        foreach (var ch in json)
        {
            if (char.IsWhiteSpace(ch)) continue;
            return ch == rootChar;
        }
        return false;
    }

    private sealed class IngestSourceAssertionDocument
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, IngestSourceAssertion> Sources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class IngestSourceAssertion
    {
        public string SourceId { get; set; } = string.Empty;
        public string SourceTitle { get; set; } = string.Empty;
        public string SourceKind { get; set; } = string.Empty;
        public List<string> JobIds { get; set; } = [];
        public Dictionary<string, IngestSourceChunkAssertion> Chunks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    private sealed class IngestSourceChunkAssertion
    {
        public string JobId { get; set; } = string.Empty;
        public string SourceChunkId { get; set; } = string.Empty;
        public int SourceChunkIndex { get; set; }
        public string Summary { get; set; } = string.Empty;
        public Dictionary<string, string?> ObservedProperties { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Aliases { get; set; } = [];
        public string Evidence { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
        public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}

public sealed record IngestAssertionInput(
    Guid JobId,
    Guid SourceId,
    string SourceTitle,
    string SourceKind,
    Guid SourceChunkId,
    int SourceChunkIndex,
    string? Summary,
    IReadOnlyDictionary<string, string?> ObservedProperties,
    IReadOnlyList<string> Aliases,
    string? Evidence,
    string? Notes,
    bool ReplaceExistingText = false);

public sealed record IngestAssertionWriteResult(
    string SourceKey,
    string ChunkKey,
    int SourceCount,
    int ChunkCountForSource);

public sealed record IngestAssertionRemovalResult(
    bool Removed,
    int RemainingSourceCount);

public sealed record IngestSourceAssertionSummary(
    string SourceId,
    string SourceTitle,
    string SourceKind,
    int ChunkCount,
    IReadOnlyList<string> Aliases,
    string Summary,
    DateTime UpdatedAt);