using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lorekeeper.Ingest;

public static class IngestWikiSheet
{
    public const string SummaryProperty = "summary";
    public const string AliasesProperty = "aliasesJson";
    public const string WikiSectionsProperty = "wikiSectionsJson";
    public const string RelationshipCitationsProperty = "citationsJson";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string ReadSummary(IReadOnlyDictionary<string, object?> properties)
    {
        if (TryReadString(properties, SummaryProperty, out var summary))
            return summary;
        if (TryReadString(properties, "description", out var description))
            return description;
        if (TryReadString(properties, "role", out var role))
            return role;
        return string.Empty;
    }

    public static IReadOnlyList<string> ReadAliases(IReadOnlyDictionary<string, object?> properties)
    {
        if (!properties.TryGetValue(AliasesProperty, out var value)) return [];
        return ReadStringArray(value);
    }

    public static IReadOnlyList<IngestWikiSection> ReadSections(IReadOnlyDictionary<string, object?> properties)
    {
        if (!properties.TryGetValue(WikiSectionsProperty, out var value)) return [];
        return ReadJsonArray<IngestWikiSection>(value)
            .Select(NormalizeSection)
            .Where(section => !string.IsNullOrWhiteSpace(section.Title) || !string.IsNullOrWhiteSpace(section.Body))
            .ToList();
    }

    public static IReadOnlyList<IngestWikiCitation> ReadRelationshipCitations(IReadOnlyDictionary<string, object?> properties)
    {
        if (!properties.TryGetValue(RelationshipCitationsProperty, out var value)) return [];
        return ReadJsonArray<IngestWikiCitation>(value)
            .Select(NormalizeCitation)
            .Where(citation => !string.IsNullOrWhiteSpace(citation.SourceId))
            .ToList();
    }

    public static void ApplyEntitySheet(
        IDictionary<string, object?> properties,
        string summary,
        IEnumerable<string>? aliases,
        IEnumerable<IngestWikiSectionInput>? sections,
        IngestAgentContext context)
    {
        properties[SummaryProperty] = NormalizeText(summary);
        properties[AliasesProperty] = SerializeAliases(aliases);
        properties[WikiSectionsProperty] = SerializeSections(sections, context);
    }

    public static void ApplyEntitySheetPatch(
        IDictionary<string, object?> properties,
        string summary,
        IEnumerable<string>? aliasesToAdd,
        IEnumerable<IngestWikiSectionInput>? sectionPatches,
        IngestAgentContext context)
    {
        properties[SummaryProperty] = NormalizeText(summary);

        var aliases = ReadAliases(AsReadOnly(properties))
            .Concat(aliasesToAdd ?? Enumerable.Empty<string>());
        properties[AliasesProperty] = SerializeAliases(aliases);

        var sections = ReadSections(AsReadOnly(properties)).ToList();
        var patchIndex = sections.Count;
        foreach (var patch in sectionPatches ?? Enumerable.Empty<IngestWikiSectionInput>())
        {
            if (patch is null) continue;

            var normalized = NormalizeSection(patch, patchIndex++, context);
            if (string.IsNullOrWhiteSpace(normalized.Title) && string.IsNullOrWhiteSpace(normalized.Body))
                continue;

            var existingIndex = sections.FindIndex(section =>
                string.Equals(section.Id, normalized.Id, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
                sections[existingIndex] = normalized;
            else
                sections.Add(normalized);
        }

        properties[WikiSectionsProperty] = SerializeSections(sections);
    }

    public static void UpsertSourceWikiSection(
        IDictionary<string, object?> properties,
        Guid sourceId,
        string sourceTitle,
        string sourceKind,
        string body)
    {
        var sectionId = SourceSectionId(sourceId);
        var title = SourceSectionTitle(sourceTitle);
        var sections = ReadSections(AsReadOnly(properties))
            .Where(section => !string.Equals(section.Id, sectionId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        sections.Add(new IngestWikiSection(
            sectionId,
            title,
            NormalizeText(body),
            BuildSourceSectionCitations(sourceId, sourceTitle, sourceKind)));

        properties[WikiSectionsProperty] = SerializeSections(sections);
    }

    public static bool RemoveSourceWikiSection(IDictionary<string, object?> properties, Guid sourceId)
    {
        var sections = ReadSections(AsReadOnly(properties));
        if (sections.Count == 0) return false;

        var sectionId = SourceSectionId(sourceId);
        var next = sections
            .Where(section => !string.Equals(section.Id, sectionId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (next.Count == sections.Count) return false;

        if (next.Count == 0)
            properties.Remove(WikiSectionsProperty);
        else
            properties[WikiSectionsProperty] = JsonSerializer.Serialize(next, JsonOptions);
        return true;
    }

    public static bool RemoveSourceCitations(IDictionary<string, object?> properties, Guid sourceId)
    {
        var changed = RemoveSourceWikiSection(properties, sourceId);
        var sourceKey = sourceId.ToString("N");

        var sections = ReadSections(AsReadOnly(properties));
        if (sections.Count > 0)
        {
            var nextSections = sections
                .Select(section => section with
                {
                    Citations = section.Citations
                        .Where(citation => !string.Equals(citation.SourceId, sourceKey, StringComparison.OrdinalIgnoreCase))
                        .ToList(),
                })
                .ToList();
            var citationsChanged = nextSections.Where((section, index) => section.Citations.Count != sections[index].Citations.Count).Any();
            changed = changed || citationsChanged;
            if (citationsChanged)
                properties[WikiSectionsProperty] = JsonSerializer.Serialize(nextSections, JsonOptions);
        }

        var relationshipCitations = ReadRelationshipCitations(AsReadOnly(properties));
        if (relationshipCitations.Count > 0)
        {
            var next = relationshipCitations
                .Where(citation => !string.Equals(citation.SourceId, sourceKey, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (next.Count != relationshipCitations.Count)
            {
                properties[RelationshipCitationsProperty] = JsonSerializer.Serialize(next, JsonOptions);
                changed = true;
            }
        }

        return changed;
    }

    public static bool RemoveSourceChunkCitations(
        IDictionary<string, object?> properties,
        Guid sourceId,
        Guid sourceChunkId,
        bool removeSourceWikiSection)
    {
        var changed = removeSourceWikiSection && RemoveSourceWikiSection(properties, sourceId);
        var sourceKey = sourceId.ToString("N");
        var sourceChunkKey = sourceChunkId.ToString("N");

        var sections = ReadSections(AsReadOnly(properties));
        if (sections.Count > 0)
        {
            var nextSections = sections
                .Select(section => section with
                {
                    Citations = section.Citations
                        .Where(citation => !CitationMatchesChunk(citation, sourceKey, sourceChunkKey))
                        .ToList(),
                })
                .ToList();
            var citationsChanged = nextSections.Where((section, index) => section.Citations.Count != sections[index].Citations.Count).Any();
            changed = changed || citationsChanged;
            if (citationsChanged)
                properties[WikiSectionsProperty] = JsonSerializer.Serialize(nextSections, JsonOptions);
        }

        var relationshipCitations = ReadRelationshipCitations(AsReadOnly(properties));
        if (relationshipCitations.Count > 0)
        {
            var next = relationshipCitations
                .Where(citation => !CitationMatchesChunk(citation, sourceKey, sourceChunkKey))
                .ToList();
            if (next.Count != relationshipCitations.Count)
            {
                properties[RelationshipCitationsProperty] = JsonSerializer.Serialize(next, JsonOptions);
                changed = true;
            }
        }

        return changed;
    }

    public static string SourceSectionId(Guid sourceId) => $"source{sourceId:N}";

    public static string SourceSectionTitle(string sourceTitle) =>
        $"Source: {NormalizeText(sourceTitle, "Untitled source")}";

    public static bool HasCitations(IReadOnlyDictionary<string, object?> properties) =>
        ReadSections(properties).Any(section => section.Citations.Count > 0)
        || ReadRelationshipCitations(properties).Count > 0;

    public static IReadOnlyDictionary<string, string?> VisibleProperties(IReadOnlyDictionary<string, object?> properties)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in properties)
        {
            if (IsInternalProperty(kv.Key) || IsWikiStorageProperty(kv.Key)) continue;
            result[kv.Key] = kv.Value?.ToString();
        }
        return result;
    }

    public static string BuildEntitySearchText(IReadOnlyDictionary<string, object?> properties)
    {
        var parts = new List<string>();
        parts.Add(ReadSummary(properties));
        parts.AddRange(ReadAliases(properties));
        foreach (var section in ReadSections(properties))
        {
            parts.Add(section.Title);
            parts.Add(section.Body);
            parts.AddRange(section.Citations.Select(CitationPreview));
        }

        foreach (var property in VisibleProperties(properties))
            parts.Add($"{property.Key}: {property.Value}");

        return string.Join("\n", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    public static string CitationPreview(IngestWikiCitation citation)
    {
        var chunks = new List<string>();
        if (!string.IsNullOrWhiteSpace(citation.SourceTitle))
            chunks.Add(citation.SourceTitle);
        if (citation.SourceChunkIndex >= 0)
            chunks.Add($"chunk {citation.SourceChunkIndex + 1}");
        if (!string.IsNullOrWhiteSpace(citation.Locator))
            chunks.Add(citation.Locator);
        if (citation.PageNumber is int page)
            chunks.Add($"page {page}");
        return string.Join(" - ", chunks);
    }

    public static bool IsWikiStorageProperty(string key) =>
        string.Equals(key, SummaryProperty, StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, AliasesProperty, StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, WikiSectionsProperty, StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, RelationshipCitationsProperty, StringComparison.OrdinalIgnoreCase);

    public static bool IsInternalProperty(string key) =>
        IngestSourceAssertions.IsProtectedProperty(key)
        || string.Equals(key, "sourceType", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "sourceId", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "sourceChunkId", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "sourceChunkIndex", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "sourceBlockId", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "sourcePageId", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "sourceGraphTargetType", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "structural", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "order", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("vectorIndex", StringComparison.OrdinalIgnoreCase);

    private static string SerializeAliases(IEnumerable<string>? aliases) =>
        JsonSerializer.Serialize(
            (aliases ?? Enumerable.Empty<string>())
                .Select(alias => NormalizeText(alias))
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            JsonOptions);

    private static string SerializeSections(IEnumerable<IngestWikiSectionInput>? sections, IngestAgentContext context)
    {
        var normalized = (sections ?? Enumerable.Empty<IngestWikiSectionInput>())
            .Where(section => section is not null)
            .Select((section, index) => NormalizeSection(section, index, context))
            .Where(section => !string.IsNullOrWhiteSpace(section.Title) || !string.IsNullOrWhiteSpace(section.Body))
            .ToList();
        return SerializeSections(normalized);
    }

    private static string SerializeSections(IEnumerable<IngestWikiSection> sections) =>
        JsonSerializer.Serialize(
            sections
                .Select(NormalizeSection)
                .Where(section => !string.IsNullOrWhiteSpace(section.Title) || !string.IsNullOrWhiteSpace(section.Body))
                .ToList(),
            JsonOptions);

    private static IngestWikiSection NormalizeSection(IngestWikiSectionInput input, int index, IngestAgentContext context)
    {
        var title = NormalizeText(input.Title);
        var body = NormalizeText(input.Body);
        var id = NormalizeSectionId(input.Id, title, index);
        return new IngestWikiSection(
            id,
            title,
            body,
            BuildSectionCitations(context));
    }

    private static IngestWikiSection NormalizeSection(IngestWikiSection section) =>
        section with
        {
            Id = NormalizeSectionId(section.Id, section.Title, 0),
            Title = NormalizeText(section.Title),
            Body = NormalizeText(section.Body),
            Citations = section.Citations.Select(NormalizeCitation).ToList(),
        };

    private static IReadOnlyList<IngestWikiCitation> BuildSectionCitations(IngestAgentContext context) =>
    [
        new(
            context.SourceId.ToString("N"),
            NormalizeText(context.SourceTitle),
            NormalizeText(context.SourceKind),
            context.SourceChunkId.ToString("N"),
            context.SourceChunkIndex,
            SourceBlockId: null,
            PageNumber: null,
            Locator: null),
    ];

    private static IngestWikiCitation NormalizeCitation(IngestWikiCitation citation) =>
        citation with
        {
            SourceId = NormalizeText(citation.SourceId),
            SourceTitle = NormalizeText(citation.SourceTitle),
            SourceKind = NormalizeText(citation.SourceKind),
            SourceChunkId = NormalizeText(citation.SourceChunkId),
            SourceBlockId = NormalizeNullableText(citation.SourceBlockId),
            Locator = NormalizeNullableText(citation.Locator),
        };

    private static IReadOnlyList<IngestWikiCitation> BuildSourceSectionCitations(
        Guid sourceId,
        string sourceTitle,
        string sourceKind)
    {
        var sourceKey = sourceId.ToString("N");
        return
        [
            new(
                sourceKey,
                NormalizeText(sourceTitle),
                NormalizeText(sourceKind),
                string.Empty,
                -1,
                SourceBlockId: null,
                PageNumber: null,
                Locator: null),
        ];
    }

    private static string CitationKey(IngestWikiCitation citation) =>
        $"{citation.SourceId}|{citation.SourceChunkId}|{citation.SourceBlockId}|{citation.PageNumber}|{citation.Locator}";

    private static bool CitationMatchesChunk(IngestWikiCitation citation, string sourceKey, string sourceChunkKey) =>
        string.Equals(citation.SourceId, sourceKey, StringComparison.OrdinalIgnoreCase)
        && string.Equals(citation.SourceChunkId, sourceChunkKey, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeSectionId(string? id, string title, int index)
    {
        var source = string.IsNullOrWhiteSpace(id) ? title : id;
        var normalized = NormalizeComparable(source ?? string.Empty);
        if (normalized.Length == 0)
            normalized = $"section{index + 1}";
        return normalized.Length <= 48 ? normalized : normalized[..48];
    }

    private static IReadOnlyList<string> ReadStringArray(object? value)
    {
        if (value is JsonElement element)
            return element.ValueKind == JsonValueKind.Array
                ? element.EnumerateArray()
                    .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : ReadStringArray(element.GetRawText());

        if (value is string text)
        {
            if (LooksLikeJsonRoot(text, '['))
            {
                try
                {
                    return (JsonSerializer.Deserialize<string[]>(text, JsonOptions) ?? [])
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Select(item => item.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                catch (JsonException) { }
            }
            return string.IsNullOrWhiteSpace(text) ? [] : [text.Trim()];
        }

        return [];
    }

    private static IReadOnlyList<T> ReadJsonArray<T>(object? value)
    {
        try
        {
            if (value is JsonElement element)
            {
                if (element.ValueKind != JsonValueKind.Array) return [];
                return JsonSerializer.Deserialize<List<T>>(element.GetRawText(), JsonOptions) ?? [];
            }

            if (value is string text && LooksLikeJsonRoot(text, '['))
                return JsonSerializer.Deserialize<List<T>>(text, JsonOptions) ?? [];
        }
        catch (JsonException) { }

        return [];
    }

    private static bool TryReadString(IReadOnlyDictionary<string, object?> properties, string key, out string value)
    {
        value = string.Empty;
        if (!properties.TryGetValue(key, out var raw)) return false;
        value = NormalizeText(raw?.ToString());
        return !string.IsNullOrWhiteSpace(value);
    }

    private static IReadOnlyDictionary<string, object?> AsReadOnly(IDictionary<string, object?> properties) =>
        properties as IReadOnlyDictionary<string, object?>
        ?? properties.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

    private static string NormalizeSourceId(string? value, Guid fallback)
    {
        var normalized = NormalizeText(value);
        if (Guid.TryParse(normalized, out var guid))
            return guid.ToString("N");
        return fallback.ToString("N");
    }

    private static string NormalizeText(string? value, string fallback = "")
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback.Trim();
        return value.Trim();
    }

    private static string? NormalizeNullableText(string? value)
    {
        var normalized = NormalizeText(value);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "...";
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

    private static string NormalizeComparable(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}

public sealed record IngestWikiSection(
    string Id,
    string Title,
    string Body,
    IReadOnlyList<IngestWikiCitation> Citations);

public sealed record IngestWikiCitation(
    string SourceId,
    string SourceTitle,
    string SourceKind,
    string SourceChunkId,
    int SourceChunkIndex,
    string? SourceBlockId,
    int? PageNumber,
    string? Locator);

public sealed class IngestWikiSectionInput
{
    [JsonPropertyName("id")]
    [Description("Stable section id such as overview, role, timeline, relationships, traits, events, quotes, rules, claims, or examples.")]
    public string? Id { get; set; }

    [JsonPropertyName("title")]
    [Description("Human-readable section title.")]
    public string? Title { get; set; }

    [JsonPropertyName("body")]
    [Description("Source-grounded observation body for this section. Keep narrative detail here instead of relationship records.")]
    public string? Body { get; set; }
}
