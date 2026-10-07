using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lorekeeper.Ingest;

public static class IngestWikiSheet
{
    public const string SummaryProperty = "summary";
    public const string AliasesProperty = "aliasesJson";
    public const string WikiSectionsProperty = "wikiSectionsJson";
    public const string RelationshipCitationsProperty = "citationsJson";
    public const string SourceEvidencePrefix = "sourceEvidence.";
    public const string SourceEvidenceMetaProperty = "sourceEvidenceMetaJson";

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

    public static IReadOnlyList<IngestSourceEvidence> ReadSourceEvidence(IReadOnlyDictionary<string, object?> properties)
    {
        var meta = ReadSourceEvidenceMeta(properties);
        var result = new List<IngestSourceEvidence>();
        foreach (var property in properties.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!IsSourceEvidenceProperty(property.Key)) continue;

            var slug = property.Key[SourceEvidencePrefix.Length..];
            var markdown = NormalizeText(property.Value?.ToString());
            if (string.IsNullOrWhiteSpace(markdown)) continue;

            meta.TryGetValue(slug, out var sourceMeta);
            result.Add(new IngestSourceEvidence(
                property.Key,
                slug,
                sourceMeta?.SourceId ?? string.Empty,
                sourceMeta?.SourceTitle ?? SourceTitleFromSlug(slug),
                sourceMeta?.SourceKind ?? string.Empty,
                markdown));
        }

        return result;
    }

    public static bool UpsertSourceEvidenceMarkdown(
        IDictionary<string, object?> properties,
        Guid sourceId,
        string sourceTitle,
        string sourceKind,
        Guid jobId,
        string markdown)
    {
        var normalized = NormalizeText(markdown);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var meta = ReadSourceEvidenceMeta(AsReadOnly(properties))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var sourceKey = sourceId.ToString("N");
        var existing = meta.FirstOrDefault(kv => string.Equals(kv.Value.SourceId, sourceKey, StringComparison.OrdinalIgnoreCase));
        var slug = string.IsNullOrWhiteSpace(existing.Key)
            ? ResolveSourceEvidenceSlug(properties, meta, sourceTitle, sourceId)
            : existing.Key;

        var propertyKey = SourceEvidencePrefix + slug;
        var changed = !properties.TryGetValue(propertyKey, out var current)
            || !string.Equals(NormalizeText(current?.ToString()), normalized, StringComparison.Ordinal);
        properties[propertyKey] = normalized;

        var normalizedMeta = new IngestSourceEvidenceMeta(
            sourceKey,
            NormalizeText(sourceTitle, "Untitled source"),
            NormalizeText(sourceKind),
            "ingestSource",
            jobId.ToString("N"),
            DateTime.UtcNow);
        if (!meta.TryGetValue(slug, out var currentMeta) || !Equals(currentMeta, normalizedMeta))
            changed = true;
        meta[slug] = normalizedMeta;
        properties[SourceEvidenceMetaProperty] = JsonSerializer.Serialize(meta, JsonOptions);
        return changed;
    }

    public static bool AddSourceEvidenceProvenance(
        IDictionary<string, object?> properties,
        Guid sourceId,
        string sourceTitle,
        string sourceKind,
        Guid jobId)
    {
        var meta = ReadSourceEvidenceMeta(AsReadOnly(properties))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var sourceKey = sourceId.ToString("N");
        if (meta.Values.Any(existing => string.Equals(existing.SourceId, sourceKey, StringComparison.OrdinalIgnoreCase)))
            return false;

        var slug = ResolveSourceEvidenceSlug(properties, meta, sourceTitle, sourceId);
        meta[slug] = new IngestSourceEvidenceMeta(
            sourceKey,
            NormalizeText(sourceTitle, "Untitled source"),
            NormalizeText(sourceKind),
            "ingestSource",
            jobId.ToString("N"),
            DateTime.UtcNow);
        properties[SourceEvidenceMetaProperty] = JsonSerializer.Serialize(meta, JsonOptions);
        return true;
    }

    public static bool RemoveSourceEvidence(
        IDictionary<string, object?> properties,
        Guid sourceId)
    {
        var meta = ReadSourceEvidenceMeta(AsReadOnly(properties))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        if (meta.Count == 0) return false;

        var sourceKey = sourceId.ToString("N");
        var removedSlugs = meta
            .Where(kv => string.Equals(kv.Value.SourceId, sourceKey, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToList();
        if (removedSlugs.Count == 0) return false;

        foreach (var slug in removedSlugs)
        {
            meta.Remove(slug);
            properties.Remove(SourceEvidencePrefix + slug);
        }

        if (meta.Count == 0)
            properties.Remove(SourceEvidenceMetaProperty);
        else
            properties[SourceEvidenceMetaProperty] = JsonSerializer.Serialize(meta, JsonOptions);
        return true;
    }

    public static bool HasSourceEvidence(IReadOnlyDictionary<string, object?> properties) =>
        properties.Keys.Any(IsSourceEvidenceProperty)
        || ReadSourceEvidenceMeta(properties).Count > 0;

    public static bool ContainsSourceEvidence(IReadOnlyDictionary<string, object?> properties, Guid sourceId)
    {
        var sourceKey = sourceId.ToString("N");
        return ReadSourceEvidenceMeta(properties).Values.Any(meta =>
            string.Equals(meta.SourceId, sourceKey, StringComparison.OrdinalIgnoreCase));
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
    public static string SourceSectionId(Guid sourceId) => $"source{sourceId:N}";

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
        || string.Equals(key, RelationshipCitationsProperty, StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, SourceEvidenceMetaProperty, StringComparison.OrdinalIgnoreCase);

    public static bool IsSourceEvidenceProperty(string key) =>
        key.StartsWith(SourceEvidencePrefix, StringComparison.OrdinalIgnoreCase)
        && key.Length > SourceEvidencePrefix.Length;

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

    private static IReadOnlyDictionary<string, IngestSourceEvidenceMeta> ReadSourceEvidenceMeta(IReadOnlyDictionary<string, object?> properties)
    {
        if (!properties.TryGetValue(SourceEvidenceMetaProperty, out var value))
            return new Dictionary<string, IngestSourceEvidenceMeta>(StringComparer.OrdinalIgnoreCase);

        try
        {
            Dictionary<string, IngestSourceEvidenceMeta>? parsed = value switch
            {
                JsonElement element when element.ValueKind == JsonValueKind.Object =>
                    JsonSerializer.Deserialize<Dictionary<string, IngestSourceEvidenceMeta>>(element.GetRawText(), JsonOptions),
                string text when LooksLikeJsonRoot(text, '{') =>
                    JsonSerializer.Deserialize<Dictionary<string, IngestSourceEvidenceMeta>>(text, JsonOptions),
                _ => null,
            };

            return (parsed ?? [])
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value.SourceId))
                .ToDictionary(
                    kv => NormalizeSourceEvidenceSlug(kv.Key),
                    kv => NormalizeSourceEvidenceMeta(kv.Value),
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, IngestSourceEvidenceMeta>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static IngestSourceEvidenceMeta NormalizeSourceEvidenceMeta(IngestSourceEvidenceMeta meta) =>
        meta with
        {
            SourceId = NormalizeText(meta.SourceId),
            SourceTitle = NormalizeText(meta.SourceTitle, "Untitled source"),
            SourceKind = NormalizeText(meta.SourceKind),
            Kind = NormalizeText(meta.Kind, "ingestSource"),
            LastFinalizedJobId = NormalizeText(meta.LastFinalizedJobId),
        };

    private static string ResolveSourceEvidenceSlug(
        IDictionary<string, object?> properties,
        IReadOnlyDictionary<string, IngestSourceEvidenceMeta> existingMeta,
        string sourceTitle,
        Guid sourceId)
    {
        var baseSlug = NormalizeSourceEvidenceSlug(sourceTitle);
        if (string.IsNullOrWhiteSpace(baseSlug))
            baseSlug = "source";

        if (!SourceEvidenceSlugExists(properties, existingMeta, baseSlug))
            return baseSlug;

        var suffix = sourceId.ToString("N")[..8];
        var maxBaseLength = Math.Max(1, 72 - suffix.Length - 1);
        var candidate = $"{baseSlug[..Math.Min(baseSlug.Length, maxBaseLength)]}-{suffix}";
        var index = 2;
        while (SourceEvidenceSlugExists(properties, existingMeta, candidate))
            candidate = $"{baseSlug[..Math.Min(baseSlug.Length, Math.Max(1, maxBaseLength - index.ToString().Length - 1))]}-{suffix}-{index++}";
        return candidate;
    }

    private static bool SourceEvidenceSlugExists(
        IDictionary<string, object?> properties,
        IReadOnlyDictionary<string, IngestSourceEvidenceMeta> existingMeta,
        string slug) =>
        existingMeta.ContainsKey(slug)
        || properties.ContainsKey(SourceEvidencePrefix + slug);

    private static string NormalizeSourceEvidenceSlug(string? value)
    {
        var text = NormalizeText(value).ToLowerInvariant();
        var builder = new StringBuilder();
        var lastWasSeparator = false;
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator && builder.Length > 0)
            {
                builder.Append('-');
                lastWasSeparator = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length <= 72 ? slug : slug[..72].Trim('-');
    }

    private static string SourceTitleFromSlug(string slug) =>
        string.Join(' ', slug.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..]));

    private static IngestWikiSection NormalizeSection(IngestWikiSection section) =>
        section with
        {
            Id = NormalizeSectionId(section.Id, section.Title, 0),
            Title = NormalizeText(section.Title),
            Body = NormalizeText(section.Body),
            Citations = section.Citations.Select(NormalizeCitation).ToList(),
        };

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

public sealed record IngestSourceEvidence(
    string PropertyKey,
    string Slug,
    string SourceId,
    string SourceTitle,
    string SourceKind,
    string Markdown);

public sealed record IngestSourceEvidenceMeta(
    string SourceId,
    string SourceTitle,
    string SourceKind,
    string Kind,
    string LastFinalizedJobId,
    DateTime LastFinalizedAt);

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
