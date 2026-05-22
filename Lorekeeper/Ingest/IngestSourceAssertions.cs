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
    private static readonly IReadOnlyDictionary<string, string?> EmptyObservedProperties =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyList<string> EmptyAliases = [];

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

    private static readonly string[] FactSheetFieldOrder =
    [
        "summary",
        "description",
        "role",
        "status",
        "affiliation",
        "history",
        "motivation",
        "significance",
        "relationship",
        "details",
    ];

    private static readonly string[] DisallowedExtractionRationalePhrases =
    [
        "not mentioned",
        "not explicitly mentioned",
        "does not mention",
        "doesn't mention",
        "did not mention",
        "didn't mention",
        "no mention of",
        "no information",
        "not enough information",
        "semantically similar",
        "semantic similarity",
        "came back as semantically",
        "because it was similar",
        "because it matched semantically",
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

    public static bool ContainsEntitySource(IReadOnlyDictionary<string, object?> properties, Guid sourceId) =>
        ContainsSource(properties, EntityAssertionsProperty, sourceId);

    public static bool ContainsRelationshipSource(IReadOnlyDictionary<string, object?> properties, Guid sourceId) =>
        ContainsSource(properties, RelationshipAssertionsProperty, sourceId);

    public static int CountEntityObservations(IReadOnlyDictionary<string, object?> properties) =>
        CountObservations(properties, EntityAssertionsProperty);

    public static int CountRelationshipObservations(IReadOnlyDictionary<string, object?> properties) =>
        CountObservations(properties, RelationshipAssertionsProperty);

    public static IReadOnlyList<IngestSourceAssertionSummary> SummarizeEntityAssertions(
        IReadOnlyDictionary<string, object?> properties,
        int maxSources = 5) =>
        SummarizeAssertions(properties, EntityAssertionsProperty, maxSources);

    public static IReadOnlyList<IngestSourceAssertionSummary> SummarizeRelationshipAssertions(
        IReadOnlyDictionary<string, object?> properties,
        int maxSources = 5) =>
        SummarizeAssertions(properties, RelationshipAssertionsProperty, maxSources);

    public static IReadOnlyList<IngestSourceObservation> ListEntityObservations(
        IReadOnlyDictionary<string, object?> properties,
        int maxObservations = 20) =>
        ListObservations(properties, EntityAssertionsProperty, maxObservations);

    public static IReadOnlyList<IngestSourceObservation> ListRelationshipObservations(
        IReadOnlyDictionary<string, object?> properties,
        int maxObservations = 20) =>
        ListObservations(properties, RelationshipAssertionsProperty, maxObservations);

    public static IngestEntityFactSheet BuildEntityFactSheet(
        IReadOnlyDictionary<string, object?> properties,
        int maxObservations = 20) =>
        BuildFactSheet(ListEntityObservations(properties, maxObservations));

    public static IngestEntityFactSheet BuildFactSheet(IReadOnlyList<IngestSourceObservation> observations)
    {
        var fieldBuilders = new Dictionary<string, FactSheetFieldBuilder>(StringComparer.OrdinalIgnoreCase);
        var safeObservations = observations.Where(observation => observation is not null).ToList();
        foreach (var observation in safeObservations
            .OrderBy(observation => observation.SourceTitle ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(observation => observation.SourceChunkIndex))
        {
            AddFactSheetField(fieldBuilders, "summary", observation.Summary, observation);
            foreach (var property in observation.ObservedProperties ?? EmptyObservedProperties)
                AddFactSheetField(fieldBuilders, property.Key, property.Value, observation);
        }

        var fields = fieldBuilders.Values
            .OrderBy(builder => FactSheetFieldRank(builder.Key))
            .ThenBy(builder => builder.Label, StringComparer.OrdinalIgnoreCase)
            .Select(builder => new IngestFactSheetField(
                builder.Key,
                builder.Label,
                string.Join("\n", builder.Values),
                builder.Evidence
                    .OrderBy(evidence => evidence.SourceTitle, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(evidence => evidence.SourceChunkIndex)
                    .ThenByDescending(evidence => evidence.UpdatedAt)
                    .ToArray()))
            .ToArray();

        var aliases = safeObservations
            .SelectMany(observation => observation.Aliases ?? EmptyAliases)
            .Select(NormalizeFactText)
            .Where(alias => alias.Length > 0 && !ContainsDisallowedExtractionRationale(alias))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var sources = safeObservations
            .GroupBy(observation => observation.SourceId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new IngestFactSheetSource(
                group.Key,
                group.Select(observation => observation.SourceTitle).FirstOrDefault(title => !string.IsNullOrWhiteSpace(title)) ?? string.Empty,
                group.Select(observation => observation.SourceKind).FirstOrDefault(kind => !string.IsNullOrWhiteSpace(kind)) ?? string.Empty,
                group.Count(),
                group.Max(observation => observation.UpdatedAt)))
            .OrderBy(source => source.SourceTitle, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new IngestEntityFactSheet(fields, aliases, sources);
    }

    public static bool ContainsDisallowedExtractionRationale(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = NormalizeFactText(value).ToLowerInvariant();
        return DisallowedExtractionRationalePhrases.Any(phrase => normalized.Contains(phrase, StringComparison.Ordinal));
    }

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

    private static bool ContainsSource(IReadOnlyDictionary<string, object?> properties, string propertyKey, Guid sourceId)
    {
        var document = ReadDocument(ReadRaw(properties, propertyKey));
        return document.Sources.ContainsKey(SourceKey(sourceId));
    }

    private static int CountObservations(IReadOnlyDictionary<string, object?> properties, string propertyKey)
    {
        var document = ReadDocument(ReadRaw(properties, propertyKey));
        return document.Sources.Values.Sum(source => source.Chunks.Count);
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

    private static IReadOnlyList<IngestSourceObservation> ListObservations(
        IReadOnlyDictionary<string, object?> properties,
        string propertyKey,
        int maxObservations)
    {
        var document = ReadDocument(ReadRaw(properties, propertyKey));
        return document.Sources.Values
            .OrderBy(source => source.SourceTitle ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .SelectMany(source => source.Chunks.Values
                .OrderBy(chunk => chunk.SourceChunkIndex)
                .Select(chunk => new IngestSourceObservation(
                    source.SourceId ?? string.Empty,
                    source.SourceTitle ?? string.Empty,
                    source.SourceKind ?? string.Empty,
                    chunk.JobId ?? string.Empty,
                    chunk.SourceChunkId ?? string.Empty,
                    chunk.SourceChunkIndex,
                    chunk.Summary ?? string.Empty,
                    new Dictionary<string, string?>(chunk.ObservedProperties ?? EmptyObservedProperties, StringComparer.OrdinalIgnoreCase),
                    (chunk.Aliases ?? EmptyAliases)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    chunk.Evidence ?? string.Empty,
                    chunk.Notes ?? string.Empty,
                    chunk.RecordedAt,
                    chunk.UpdatedAt)))
            .Take(Math.Max(0, maxObservations))
            .ToList();
    }

    private static IngestSourceAssertionDocument ReadDocument(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson) || !LooksLikeJsonRoot(rawJson, '{'))
            return new IngestSourceAssertionDocument();

        try
        {
            var document = JsonSerializer.Deserialize<IngestSourceAssertionDocument>(rawJson, JsonOptions)
                ?? new IngestSourceAssertionDocument();
            return SanitizeDocument(document);
        }
        catch (JsonException)
        {
            return new IngestSourceAssertionDocument();
        }
    }

    private static IngestSourceAssertionDocument SanitizeDocument(IngestSourceAssertionDocument document)
    {
        var sanitizedSources = new Dictionary<string, IngestSourceAssertion>(StringComparer.OrdinalIgnoreCase);

        if (document.Sources is not null)
        {
            foreach (var (sourceKey, source) in document.Sources)
            {
                if (source is null) continue;

                source.SourceId ??= string.Empty;
                source.SourceTitle ??= string.Empty;
                source.SourceKind ??= string.Empty;
                source.JobIds ??= [];

                var sanitizedChunks = new Dictionary<string, IngestSourceChunkAssertion>(StringComparer.OrdinalIgnoreCase);
                if (source.Chunks is not null)
                {
                    foreach (var (chunkKey, chunk) in source.Chunks)
                    {
                        if (chunk is null) continue;

                        chunk.JobId ??= string.Empty;
                        chunk.SourceChunkId ??= string.Empty;
                        chunk.Summary ??= string.Empty;
                        chunk.ObservedProperties = new Dictionary<string, string?>(
                            chunk.ObservedProperties ?? EmptyObservedProperties,
                            StringComparer.OrdinalIgnoreCase);
                        chunk.Aliases = (chunk.Aliases ?? [])
                            .Where(alias => !string.IsNullOrWhiteSpace(alias))
                            .ToList();
                        chunk.Evidence ??= string.Empty;
                        chunk.Notes ??= string.Empty;

                        var safeChunkKey = string.IsNullOrWhiteSpace(chunkKey) ? chunk.SourceChunkId : chunkKey;
                        if (!string.IsNullOrWhiteSpace(safeChunkKey))
                            sanitizedChunks[safeChunkKey] = chunk;
                    }
                }
                source.Chunks = sanitizedChunks;

                var safeSourceKey = string.IsNullOrWhiteSpace(sourceKey) ? source.SourceId : sourceKey;
                if (!string.IsNullOrWhiteSpace(safeSourceKey))
                    sanitizedSources[safeSourceKey] = source;
            }
        }

        document.Sources = sanitizedSources;
        return document;
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

    private static void AddFactSheetField(
        IDictionary<string, FactSheetFieldBuilder> builders,
        string key,
        string? value,
        IngestSourceObservation observation)
    {
        var normalizedKey = NormalizeFactKey(key);
        var normalizedValue = NormalizeFactText(value);
        if (normalizedKey.Length == 0 || normalizedValue.Length == 0) return;
        if (IsProtectedProperty(normalizedKey) || ContainsDisallowedExtractionRationale(normalizedValue)) return;

        if (!builders.TryGetValue(normalizedKey, out var builder))
        {
            builder = new FactSheetFieldBuilder(normalizedKey, HumanizeFactKey(normalizedKey));
            builders[normalizedKey] = builder;
        }

        if (!builder.Values.Contains(normalizedValue, StringComparer.OrdinalIgnoreCase))
            builder.Values.Add(normalizedValue);

        var evidenceText = ContainsDisallowedExtractionRationale(observation.Evidence)
            ? string.Empty
            : NormalizeFactText(observation.Evidence);
        var summaryText = ContainsDisallowedExtractionRationale(observation.Summary)
            ? string.Empty
            : NormalizeFactText(observation.Summary);
        var evidenceKey = $"{observation.SourceId}:{observation.SourceChunkId}:{normalizedKey}:{evidenceText}:{summaryText}";
        if (builder.EvidenceKeys.Add(evidenceKey))
        {
            builder.Evidence.Add(new IngestFactSheetEvidence(
                observation.SourceId,
                observation.SourceTitle,
                observation.SourceKind,
                observation.SourceChunkId,
                observation.SourceChunkIndex,
                evidenceText,
                summaryText,
                observation.UpdatedAt));
        }
    }

    private static int FactSheetFieldRank(string key)
    {
        var index = Array.FindIndex(FactSheetFieldOrder, ordered => string.Equals(ordered, key, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? FactSheetFieldOrder.Length : index;
    }

    private static string NormalizeFactKey(string key) =>
        (key ?? string.Empty).Trim();

    private static string NormalizeFactText(string? value) =>
        string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string HumanizeFactKey(string key)
    {
        var normalized = key.Trim();
        if (normalized.Length == 0) return string.Empty;
        var chars = new List<char> { char.ToUpperInvariant(normalized[0]) };
        for (var index = 1; index < normalized.Length; index++)
        {
            var current = normalized[index];
            var previous = normalized[index - 1];
            if ((current == '_' || current == '-') && chars[^1] != ' ')
            {
                chars.Add(' ');
                continue;
            }

            if (char.IsUpper(current) && char.IsLower(previous) && chars[^1] != ' ')
                chars.Add(' ');
            chars.Add(current);
        }

        return new string(chars.ToArray());
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

    private sealed class FactSheetFieldBuilder(string key, string label)
    {
        public string Key { get; } = key;
        public string Label { get; } = label;
        public List<string> Values { get; } = [];
        public List<IngestFactSheetEvidence> Evidence { get; } = [];
        public HashSet<string> EvidenceKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
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

public sealed record IngestSourceObservation(
    string SourceId,
    string SourceTitle,
    string SourceKind,
    string JobId,
    string SourceChunkId,
    int SourceChunkIndex,
    string Summary,
    IReadOnlyDictionary<string, string?> ObservedProperties,
    IReadOnlyList<string> Aliases,
    string Evidence,
    string Notes,
    DateTime RecordedAt,
    DateTime UpdatedAt);

public sealed record IngestEntityFactSheet(
    IReadOnlyList<IngestFactSheetField> Fields,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<IngestFactSheetSource> Sources);

public sealed record IngestFactSheetField(
    string Key,
    string Label,
    string Value,
    IReadOnlyList<IngestFactSheetEvidence> Evidence);

public sealed record IngestFactSheetEvidence(
    string SourceId,
    string SourceTitle,
    string SourceKind,
    string SourceChunkId,
    int SourceChunkIndex,
    string Evidence,
    string Summary,
    DateTime UpdatedAt);

public sealed record IngestFactSheetSource(
    string SourceId,
    string SourceTitle,
    string SourceKind,
    int ObservationCount,
    DateTime UpdatedAt);
