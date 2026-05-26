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
            .Where(citation => !string.IsNullOrWhiteSpace(citation.SourceId) || !string.IsNullOrWhiteSpace(citation.Snippet))
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

    public static IReadOnlyList<IngestWikiCitation> BuildCitations(
        IEnumerable<IngestWikiCitationInput>? citations,
        IngestAgentContext context)
    {
        var result = (citations ?? [])
            .Select(input => BuildCitation(input, context))
            .Where(citation => !string.IsNullOrWhiteSpace(citation.SourceId) || !string.IsNullOrWhiteSpace(citation.Snippet))
            .DistinctBy(CitationKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (result.Count == 0)
            result.Add(BuildCitation(new IngestWikiCitationInput(), context));
        return result;
    }

    public static void ApplyRelationshipCitations(
        IDictionary<string, object?> properties,
        IEnumerable<IngestWikiCitationInput>? citations,
        IngestAgentContext context,
        bool mergeExisting)
    {
        var merged = mergeExisting
            ? ReadRelationshipCitations(AsReadOnly(properties)).ToList()
            : [];
        merged.AddRange(BuildCitations(citations, context));
        properties[RelationshipCitationsProperty] = JsonSerializer.Serialize(
            merged.DistinctBy(CitationKey, StringComparer.OrdinalIgnoreCase).ToList(),
            JsonOptions);
    }

    public static bool RemoveSourceCitations(IDictionary<string, object?> properties, Guid sourceId)
    {
        var changed = false;
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
            changed = nextSections.Where((section, index) => section.Citations.Count != sections[index].Citations.Count).Any();
            if (changed)
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
            parts.AddRange(section.Citations.Select(citation => citation.Snippet ?? string.Empty));
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
        if (!string.IsNullOrWhiteSpace(citation.Snippet))
            chunks.Add(citation.Snippet);
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
            .Select((section, index) => NormalizeSection(section, index, context))
            .Where(section => !string.IsNullOrWhiteSpace(section.Title) || !string.IsNullOrWhiteSpace(section.Body))
            .ToList();
        return JsonSerializer.Serialize(normalized, JsonOptions);
    }

    private static IngestWikiSection NormalizeSection(IngestWikiSectionInput input, int index, IngestAgentContext context)
    {
        var title = NormalizeText(input.Title);
        var body = NormalizeText(input.Body);
        var id = NormalizeSectionId(input.Id, title, index);
        return new IngestWikiSection(
            id,
            title,
            body,
            BuildCitations(input.Citations, context));
    }

    private static IngestWikiSection NormalizeSection(IngestWikiSection section) =>
        section with
        {
            Id = NormalizeSectionId(section.Id, section.Title, 0),
            Title = NormalizeText(section.Title),
            Body = NormalizeText(section.Body),
            Citations = section.Citations.Select(NormalizeCitation).ToList(),
        };

    private static IngestWikiCitation BuildCitation(IngestWikiCitationInput input, IngestAgentContext context) =>
        new(
            NormalizeSourceId(input.SourceId, context.SourceId),
            NormalizeText(input.SourceTitle, context.SourceTitle),
            NormalizeText(input.SourceKind, context.SourceKind),
            NormalizeSourceId(input.SourceChunkId, context.SourceChunkId),
            input.SourceChunkIndex ?? context.SourceChunkIndex,
            NormalizeNullableText(input.SourceBlockId),
            input.PageNumber,
            NormalizeNullableText(input.Locator),
            Truncate(NormalizeNullableText(input.Snippet), 360));

    private static IngestWikiCitation NormalizeCitation(IngestWikiCitation citation) =>
        citation with
        {
            SourceId = NormalizeText(citation.SourceId),
            SourceTitle = NormalizeText(citation.SourceTitle),
            SourceKind = NormalizeText(citation.SourceKind),
            SourceChunkId = NormalizeText(citation.SourceChunkId),
            SourceBlockId = NormalizeNullableText(citation.SourceBlockId),
            Locator = NormalizeNullableText(citation.Locator),
            Snippet = Truncate(NormalizeNullableText(citation.Snippet), 360),
        };

    private static string CitationKey(IngestWikiCitation citation) =>
        $"{citation.SourceId}|{citation.SourceChunkId}|{citation.SourceBlockId}|{citation.PageNumber}|{citation.Locator}|{NormalizeComparable(citation.Snippet ?? string.Empty)}";

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
    string? Locator,
    string? Snippet);

public sealed class IngestWikiSectionInput
{
    [JsonPropertyName("id")]
    [Description("Stable section id such as overview, appearance, history, relationships, rules, claims, or examples. Reuse existing ids when revising a sheet.")]
    public string? Id { get; set; }

    [JsonPropertyName("title")]
    [Description("Human-readable section title.")]
    public string? Title { get; set; }

    [JsonPropertyName("body")]
    [Description("Complete revised section body. Integrate new information with existing sheet content instead of appending raw notes.")]
    public string? Body { get; set; }

    [JsonPropertyName("citations")]
    [Description("Compact citations for the facts in this section.")]
    public IngestWikiCitationInput[]? Citations { get; set; }
}

public sealed class IngestWikiCitationInput
{
    [JsonPropertyName("sourceId")]
    public string? SourceId { get; set; }

    [JsonPropertyName("sourceTitle")]
    public string? SourceTitle { get; set; }

    [JsonPropertyName("sourceKind")]
    public string? SourceKind { get; set; }

    [JsonPropertyName("sourceChunkId")]
    public string? SourceChunkId { get; set; }

    [JsonPropertyName("sourceChunkIndex")]
    public int? SourceChunkIndex { get; set; }

    [JsonPropertyName("sourceBlockId")]
    [Description("Optional source block id when the fact came from a specific block listed in the prompt.")]
    public string? SourceBlockId { get; set; }

    [JsonPropertyName("pageNumber")]
    [Description("Optional page number from the source locator.")]
    public int? PageNumber { get; set; }

    [JsonPropertyName("locator")]
    [Description("Optional page, section, heading, or block locator label.")]
    public string? Locator { get; set; }

    [JsonPropertyName("snippet")]
    [Description("Short quote or close supporting snippet from the current source chunk.")]
    public string? Snippet { get; set; }
}
