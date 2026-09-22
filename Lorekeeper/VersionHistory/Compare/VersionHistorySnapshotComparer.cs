using System.Text.Json;
using System.Text.Json.Nodes;
using Lorekeeper.ImportExport;
using Lorekeeper.Manuscripts;
using Lorekeeper.VersionHistory.Snapshots;

namespace Lorekeeper.VersionHistory.Compare;

public interface IVersionHistorySnapshotComparer
{
    VersionHistorySnapshotComparison Compare(
        VersionHistorySnapshotPayload baseline,
        VersionHistorySnapshotPayload candidate,
        VersionHistoryCompareOptions? options = null);
}

/// <summary>
/// Controls how much readable text the comparison embeds in its entries. The
/// default bounds readable text so Review surfaces stay lightweight. History
/// summaries suppress text and request one exact entry only when it is opened.
/// </summary>
public sealed record VersionHistoryCompareOptions
{
    public static VersionHistoryCompareOptions Default { get; } = new();

    public bool IncludeBoundedReadableText { get; init; } = true;

    public VersionHistorySnapshotEntryKey? UnboundedReadableTextEntry { get; init; }

    internal string? Area { get; init; }

    internal VersionHistoryCompareOptions ForArea(string area) => this with { Area = area };

    internal bool Includes(string category, string key) =>
        IncludeBoundedReadableText || IsUnbounded(category, key);

    internal bool IsUnbounded(string category, string key) =>
        UnboundedReadableTextEntry is { } entry
        && string.Equals(entry.Area, Area, StringComparison.Ordinal)
        && string.Equals(entry.Category, category, StringComparison.Ordinal)
        && string.Equals(entry.Key, key, StringComparison.Ordinal);
}

public sealed record VersionHistorySnapshotEntryKey(string Area, string Category, string Key);

/// <summary>
/// Compares canonical snapshot payloads without touching Git, the database, or
/// restore services. Output contains stable identities, labels, and hashes;
/// image and font bytes are deliberately never read or returned.
/// </summary>
public sealed class VersionHistorySnapshotComparer : IVersionHistorySnapshotComparer
{
    private const int MaxReadableTextLength = 4_096;

    public VersionHistorySnapshotComparison Compare(
        VersionHistorySnapshotPayload baseline,
        VersionHistorySnapshotPayload candidate,
        VersionHistoryCompareOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        if (baseline.RepositoryId != candidate.RepositoryId)
            throw new ArgumentException("Snapshots must reference the same repository.", nameof(candidate));
        if (baseline.ProjectId != candidate.ProjectId)
            throw new ArgumentException("Snapshots must reference the same project.", nameof(candidate));
        if (baseline.ProjectId == Guid.Empty)
            throw new ArgumentException("Snapshots must reference a project.", nameof(baseline));

        var compareOptions = options ?? VersionHistoryCompareOptions.Default;
        var areas = new List<VersionHistorySnapshotAreaComparison>
        {
            CompareProject(baseline, candidate, compareOptions.ForArea("project")),
            CompareNarrative(baseline, candidate, compareOptions.ForArea("narrative")),
            CompareGraph(baseline, candidate, compareOptions.ForArea("graph")),
            CompareSources(baseline, candidate, compareOptions.ForArea("sources")),
            CompareAssets(baseline, candidate, compareOptions.ForArea("assets")),
            CompareManuscript(baseline, candidate, compareOptions.ForArea("manuscript")),
            CompareComposition(baseline, candidate, compareOptions.ForArea("composition")),
            ComparePublication(baseline, candidate, compareOptions.ForArea("publication")),
        };

        return new(
            baseline.RepositoryId,
            baseline.ProjectId,
            areas.All(area => !area.Summary.HasChanges),
            areas,
            VersionHistoryRestoreSelection.ForWholeProject());
    }

    private static VersionHistorySnapshotAreaComparison CompareProject(
        VersionHistorySnapshotPayload baseline,
        VersionHistorySnapshotPayload candidate,
        VersionHistoryCompareOptions options)
    {
        var accumulator = new AreaAccumulator("project");
        var beforeSettings = new ProjectSettingsValue(
            baseline.Project.Project,
            baseline.Project.PageSetup,
            baseline.Project.ContestModeEnabled);
        var afterSettings = new ProjectSettingsValue(
            candidate.Project.Project,
            candidate.Project.PageSetup,
            candidate.Project.ContestModeEnabled);
        accumulator.Add(CompareItems(
            options,
            "settings",
            [beforeSettings],
            [afterSettings],
            _ => "settings",
            value => value.Project.Name,
            id => Hash(id),
            metadataHash: id => Hash(id)));
        accumulator.Add(CompareItems(
            options,
            "references",
            baseline.Project.References,
            candidate.Project.References,
            reference => GuidKey(reference.ReferencedProjectId),
            ReferenceLabel,
            Hash,
            metadataHash: Hash));
        return accumulator.Build();
    }

    private static VersionHistorySnapshotAreaComparison CompareNarrative(
        VersionHistorySnapshotPayload baseline,
        VersionHistorySnapshotPayload candidate,
        VersionHistoryCompareOptions options)
    {
        var accumulator = new AreaAccumulator("narrative");
        accumulator.Add(CompareOptional(
            options, "world-brief", baseline.Narrative.WorldBrief, candidate.Narrative.WorldBrief,
            _ => "world-brief", _ => "World Brief", Hash, metadataHash: Hash, readableText: brief => brief));
        accumulator.Add(CompareOptional(
            options,
            "book-brief",
            baseline.Narrative.BookBrief,
            candidate.Narrative.BookBrief,
            brief => "book-brief",
            brief => brief.Genre,
            Hash,
            metadataHash: Hash,
            readableText: BookBriefReadableText));
        accumulator.Add(CompareItems(
            options,
            "entity-types",
            baseline.Narrative.EntityTypes,
            candidate.Narrative.EntityTypes,
            entityType => entityType.Type,
            entityType => entityType.SingularLabel,
            Hash,
            metadataHash: Hash));
        accumulator.Add(CompareItems(
            options,
            "acts",
            baseline.Narrative.Acts,
            candidate.Narrative.Acts,
            act => GuidKey(act.Id),
            act => act.Title,
            Hash,
            metadataHash: Hash));
        accumulator.Add(CompareItems(
            options,
            "chapters",
            baseline.Narrative.Chapters,
            candidate.Narrative.Chapters,
            chapter => GuidKey(chapter.Id),
            chapter => chapter.Title,
            ChapterFullHash,
            manuscriptHash: ChapterManuscriptHash,
            metadataHash: ChapterMetadataHash,
            readableText: ChapterReadableText));
        accumulator.Add(CompareItems(
            options,
            "writing-samples",
            baseline.Narrative.WritingSamples,
            candidate.Narrative.WritingSamples,
            sample => GuidKey(sample.Id),
            sample => sample.Title,
            Hash,
            manuscriptHash: sample => Hash(new { sample.Body }),
            metadataHash: sample => Hash(new { sample.Title }),
            readableText: sample => sample.Body));
        accumulator.Add(CompareItems(
            options,
            "context-preferences",
            baseline.Narrative.ContextPreferences,
            candidate.Narrative.ContextPreferences,
            preference => GuidKey(preference.Id),
            preference => $"{preference.Kind}: {preference.Key}",
            Hash,
            metadataHash: Hash));
        accumulator.Add(CompareItems(
            options,
            "annotations",
            baseline.Narrative.Annotations,
            candidate.Narrative.Annotations,
            annotation => GuidKey(annotation.Id),
            annotation => annotation.NoteText,
            Hash,
            metadataHash: Hash,
            readableText: AnnotationReadableText));
        return accumulator.Build();
    }

    private static VersionHistorySnapshotAreaComparison CompareGraph(
        VersionHistorySnapshotPayload baseline,
        VersionHistorySnapshotPayload candidate,
        VersionHistoryCompareOptions options)
    {
        var accumulator = new AreaAccumulator("graph");
        accumulator.Add(CompareItems(
            options,
            "nodes",
            baseline.Graph.Nodes,
            candidate.Graph.Nodes,
            node => node.StableKey(),
            node => node.Label ?? node.Key,
            Hash,
            metadataHash: Hash,
            readableText: GraphNodeReadableText));
        accumulator.Add(CompareItems(
            options,
            "edges",
            baseline.Graph.Edges,
            candidate.Graph.Edges,
            EdgeKey,
            edge => edge.EdgeType,
            Hash,
            metadataHash: Hash,
            readableText: GraphEdgeReadableText));
        return accumulator.Build();
    }

    private static VersionHistorySnapshotAreaComparison CompareSources(
        VersionHistorySnapshotPayload baseline,
        VersionHistorySnapshotPayload candidate,
        VersionHistoryCompareOptions options)
    {
        var accumulator = new AreaAccumulator("sources");
        accumulator.Add(CompareItems(options, "bibliography",
            baseline.Sources.UnlinkedBibliographicRecords, candidate.Sources.UnlinkedBibliographicRecords,
            record => GuidKey(record.Id), record => record.Title, Hash, metadataHash: Hash));
        var beforeSources = baseline.Sources.ReviewSummaries
            ?? baseline.Sources.RetainedSources.Select(source => VersionHistorySourceReviewSummary.Create(source,
                options.IsUnbounded("sources", GuidKey(source.Id)))).ToList();
        var afterSources = candidate.Sources.ReviewSummaries
            ?? candidate.Sources.RetainedSources.Select(source => VersionHistorySourceReviewSummary.Create(source,
                options.IsUnbounded("sources", GuidKey(source.Id)))).ToList();
        if (beforeSources.Concat(afterSources).Any(source =>
            options.IsUnbounded("sources", GuidKey(source.Id)) && !source.ReadableTextComplete))
            throw new InvalidOperationException("Load the selected source's complete checkpoint text before expanding its comparison.");
        accumulator.Add(CompareItems(
            options,
            "sources",
            beforeSources,
            afterSources,
            source => GuidKey(source.Id),
            source => source.Title,
            source => source.Hash,
            metadataHash: source => source.Hash,
            readableText: source => source.ReadableText));

        var beforeTitles = beforeSources.ToDictionary(source => source.Id, source => source.Title);
        var afterTitles = afterSources.ToDictionary(source => source.Id, source => source.Title);
        accumulator.Add(CompareItems(
            options,
            "canonical-selections",
            baseline.Narrative.BookBriefCanonSourceIds.Distinct().OrderBy(id => id),
            candidate.Narrative.BookBriefCanonSourceIds.Distinct().OrderBy(id => id),
            GuidKey,
            id => (afterTitles.TryGetValue(id, out var afterTitle) ? afterTitle : null)
                ?? (beforeTitles.TryGetValue(id, out var beforeTitle) ? beforeTitle : null)
                ?? id.ToString("N"),
            id => Hash(id),
            metadataHash: id => Hash(id)));
        return accumulator.Build();
    }

    private static VersionHistorySnapshotAreaComparison CompareAssets(
        VersionHistorySnapshotPayload baseline,
        VersionHistorySnapshotPayload candidate,
        VersionHistoryCompareOptions options)
    {
        var accumulator = new AreaAccumulator("assets");
        accumulator.Add(CompareItems(
            options,
            "images",
            baseline.Assets.Images,
            candidate.Assets.Images,
            image => GuidKey(image.Id),
            image => image.FileName,
            Hash,
            metadataHash: image => Hash(new
            {
                image.Id,
                image.FileName,
                image.ContentType,
                image.BlobPath,
                image.AltText,
                image.Source,
                image.Prompt,
                image.GenerationModel,
                image.SourceMetadataJson,
                image.DerivedFromImageId,
                image.CropXPercent,
                image.CropYPercent,
                image.CropWidthPercent,
                image.CropHeightPercent,
            }),
            binaryHash: image => Hash(new { image.Sha256, image.ByteLength })));
        accumulator.Add(CompareItems(
            options,
            "font-families",
            baseline.Assets.FontFamilies,
            candidate.Assets.FontFamilies,
            family => GuidKey(family.Id),
            family => family.Name,
            Hash,
            metadataHash: family => Hash(new
            {
                family.Id,
                family.Name,
                family.EmbeddingRightsConfirmed,
                family.RightsDeclaration,
            })));
        accumulator.Add(CompareItems(
            options,
            "font-faces",
            FlattenFontFaces(baseline.Assets.FontFamilies),
            FlattenFontFaces(candidate.Assets.FontFamilies),
            face => $"{GuidKey(face.FamilyId)}/{GuidKey(face.Face.Id)}",
            face => $"{face.FamilyName} / {face.Face.SubfamilyName}",
            face => Hash(face.Face),
            metadataHash: face => Hash(new
            {
                face.Face.Id,
                face.Face.SubfamilyName,
                face.Face.FileName,
                face.Face.ContentType,
                face.Face.Weight,
                face.Face.Italic,
                face.Face.BlobPath,
            }),
            binaryHash: face => Hash(new { face.Face.Sha256, face.Face.ByteLength })));
        accumulator.Add(CompareItems(
            options,
            "visual-examples",
            baseline.Assets.EntityVisualExamples,
            candidate.Assets.EntityVisualExamples,
            VisualExampleKey,
            example => example.Label,
            Hash,
            metadataHash: Hash));
        return accumulator.Build();
    }

    private static VersionHistorySnapshotAreaComparison CompareManuscript(
        VersionHistorySnapshotPayload baseline,
        VersionHistorySnapshotPayload candidate,
        VersionHistoryCompareOptions options)
    {
        var accumulator = new AreaAccumulator("manuscript");
        accumulator.Add(CompareItems(
            options,
            "styles",
            baseline.Manuscript.Styles,
            candidate.Manuscript.Styles,
            style => GuidKey(style.Id),
            style => style.Name,
            Hash,
            manuscriptHash: style => Hash(style.Definition),
            metadataHash: style => Hash(new
            {
                style.Id,
                style.Name,
                style.Kind,
                style.SemanticRole,
                style.Revision,
            })));
        return accumulator.Build();
    }

    private static VersionHistorySnapshotAreaComparison CompareComposition(
        VersionHistorySnapshotPayload baseline,
        VersionHistorySnapshotPayload candidate,
        VersionHistoryCompareOptions options)
    {
        var accumulator = new AreaAccumulator("composition");
        accumulator.Add(CompareItems(
            options,
            "designed-pages",
            baseline.Composition.DesignedPages,
            candidate.Composition.DesignedPages,
            page => GuidKey(page.Id),
            page => page.Name,
            Hash,
            manuscriptHash: DesignedPageManuscriptHash,
            metadataHash: DesignedPageMetadataHash,
            readableText: DesignedPageReadableText));
        return accumulator.Build();
    }

    private static VersionHistorySnapshotAreaComparison ComparePublication(
        VersionHistorySnapshotPayload baseline,
        VersionHistorySnapshotPayload candidate,
        VersionHistoryCompareOptions options)
    {
        var accumulator = new AreaAccumulator("publication");
        accumulator.Add(CompareOptional(
            options,
            "core",
            baseline.Publication.PublicationBook,
            candidate.Publication.PublicationBook,
            _ => "core",
            book => book.Title,
            Hash,
            manuscriptHash: book => Hash(new
            {
                Matter = book.Matter?.OrderBy(item => item.Id).Select(item => new { item.Id, item.ManuscriptJson, item.Revision }).ToList(),
            }),
            metadataHash: book => HashWithoutProperties(book, "Matter", "ImagePlacements"),
            readableText: PublicationBookReadableText));
        accumulator.Add(CompareItems(
            options,
            "editions",
            baseline.Publication.PublicationEditions,
            candidate.Publication.PublicationEditions,
            edition => GuidKey(edition.Id),
            edition => edition.Name,
            Hash,
            manuscriptHash: edition => Hash(new
            {
                Matter = edition.Matter?.OrderBy(item => item.Id).Select(item => new { item.Id, item.ManuscriptJson, item.Revision }).ToList(),
                ChapterOverrides = edition.ChapterOverrides.OrderBy(item => item.Id).Select(item => new { item.Id, item.ManuscriptJson, item.Revision }).ToList(),
            }),
            metadataHash: edition => HashWithoutProperties(edition, "Matter", "ChapterOverrides"),
            readableText: PublicationEditionReadableText));
        accumulator.Add(CompareItems(
            options,
            "sections",
            baseline.Publication.PublicationSections,
            candidate.Publication.PublicationSections,
            section => GuidKey(section.Id),
            section => section.Title,
            Hash,
            manuscriptHash: section => Hash(new { section.ManuscriptJson }),
            metadataHash: section => HashWithoutProperties(section, "ManuscriptJson"),
            readableText: PublicationSectionReadableText));
        return accumulator.Build();
    }

    private static ItemComparison CompareOptional<T>(
        VersionHistoryCompareOptions options,
        string category,
        T? before,
        T? after,
        Func<T, string> key,
        Func<T, string> label,
        Func<T, string> fullHash,
        Func<T, string>? manuscriptHash = null,
        Func<T, string>? metadataHash = null,
        Func<T, string>? binaryHash = null,
        Func<T, string?>? readableText = null)
        where T : class =>
        CompareItems(
            options,
            category,
            before is null ? [] : [before],
            after is null ? [] : [after],
            key,
            label,
            fullHash,
            manuscriptHash,
            metadataHash,
            binaryHash,
            readableText);

    private static ItemComparison CompareItems<T>(
        VersionHistoryCompareOptions options,
        string category,
        IEnumerable<T> beforeItems,
        IEnumerable<T> afterItems,
        Func<T, string> key,
        Func<T, string> label,
        Func<T, string> fullHash,
        Func<T, string>? manuscriptHash = null,
        Func<T, string>? metadataHash = null,
        Func<T, string>? binaryHash = null,
        Func<T, string?>? readableText = null)
    {
        var before = BuildIndex(beforeItems, key, category, "baseline");
        var after = BuildIndex(afterItems, key, category, "candidate");
        var entries = new List<VersionHistorySnapshotChangeEntry>();
        var added = 0;
        var removed = 0;
        var changed = 0;
        var unchanged = 0;

        foreach (var itemKey in before.Keys.Concat(after.Keys).Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal))
        {
            var existsBefore = before.TryGetValue(itemKey, out var beforeItem);
            var existsAfter = after.TryGetValue(itemKey, out var afterItem);
            if (!existsBefore)
            {
                added++;
                entries.Add(CreateEntry(
                    category,
                    itemKey,
                    VersionHistorySnapshotChangeKind.Added,
                    label(afterItem!),
                    null,
                    label(afterItem!),
                    null,
                    fullHash(afterItem!),
                    manuscriptHash is not null,
                    metadataHash is not null,
                    binaryHash is not null,
                    afterText: ProjectReadableText(options, category, itemKey, readableText, afterItem!)));
                continue;
            }

            if (!existsAfter)
            {
                removed++;
                entries.Add(CreateEntry(
                    category,
                    itemKey,
                    VersionHistorySnapshotChangeKind.Removed,
                    label(beforeItem!),
                    label(beforeItem!),
                    null,
                    fullHash(beforeItem!),
                    null,
                    manuscriptHash is not null,
                    metadataHash is not null,
                    binaryHash is not null,
                    beforeText: ProjectReadableText(options, category, itemKey, readableText, beforeItem!)));
                continue;
            }

            var beforeFullHash = fullHash(beforeItem!);
            var afterFullHash = fullHash(afterItem!);
            if (string.Equals(beforeFullHash, afterFullHash, StringComparison.Ordinal))
            {
                unchanged++;
                continue;
            }

            changed++;
            var beforeManuscriptHash = manuscriptHash?.Invoke(beforeItem!);
            var afterManuscriptHash = manuscriptHash?.Invoke(afterItem!);
            var beforeMetadataHash = metadataHash?.Invoke(beforeItem!);
            var afterMetadataHash = metadataHash?.Invoke(afterItem!);
            var beforeBinaryHash = binaryHash?.Invoke(beforeItem!);
            var afterBinaryHash = binaryHash?.Invoke(afterItem!);
            var manuscriptChanged = manuscriptHash is not null
                && !string.Equals(beforeManuscriptHash, afterManuscriptHash, StringComparison.Ordinal);
            var includeReadableText = options.Includes(category, itemKey);
            string? beforeText = includeReadableText && readableText is not null && (manuscriptHash is null || manuscriptChanged)
                ? readableText(beforeItem!)
                : null;
            string? afterText = includeReadableText && readableText is not null && (manuscriptHash is null || manuscriptChanged)
                ? readableText(afterItem!)
                : null;
            entries.Add(CreateEntry(
                category,
                itemKey,
                VersionHistorySnapshotChangeKind.Changed,
                label(afterItem!),
                label(beforeItem!),
                label(afterItem!),
                beforeFullHash,
                afterFullHash,
                manuscriptChanged,
                metadataHash is not null && !string.Equals(beforeMetadataHash, afterMetadataHash, StringComparison.Ordinal),
                binaryHash is not null && !string.Equals(beforeBinaryHash, afterBinaryHash, StringComparison.Ordinal),
                beforeText: ReadableText(options, category, itemKey, beforeText),
                afterText: ReadableText(options, category, itemKey, afterText)));
        }

        return new(added, removed, changed, unchanged, entries);
    }

    private static VersionHistorySnapshotChangeEntry CreateEntry(
        string category,
        string key,
        VersionHistorySnapshotChangeKind change,
        string label,
        string? beforeLabel,
        string? afterLabel,
        string? beforeHash,
        string? afterHash,
        bool manuscriptChanged,
        bool metadataChanged,
        bool binaryChanged,
        BoundedText? beforeText = null,
        BoundedText? afterText = null) =>
        new(category, key, change, label, beforeLabel, afterLabel, beforeHash, afterHash, manuscriptChanged, metadataChanged, binaryChanged)
        {
            BeforeText = beforeText?.Value,
            AfterText = afterText?.Value,
            BeforeTextTruncated = beforeText?.Truncated ?? false,
            AfterTextTruncated = afterText?.Truncated ?? false,
        };

    private static Dictionary<string, T> BuildIndex<T>(
        IEnumerable<T> items,
        Func<T, string> key,
        string category,
        string side)
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var itemKey = key(item);
            if (string.IsNullOrWhiteSpace(itemKey))
                throw new InvalidDataException($"Snapshot comparison item in '{category}' has an empty key.");
            if (!result.TryAdd(itemKey, item))
                throw new InvalidDataException($"Snapshot comparison has duplicate '{category}' key '{itemKey}' on the {side} side.");
        }

        return result;
    }

    private static string Hash(object? value) =>
        VersionHistoryCanonicalJson.Sha256Hex(VersionHistoryCanonicalJson.Serialize(value ?? new { }));

    private static string HashWithoutProperties(object value, params string[] propertyNames)
    {
        var node = JsonSerializer.SerializeToNode(value)
            ?? throw new InvalidDataException("Snapshot comparison projection serialized to null.");
        if (node is JsonObject jsonObject)
        {
            foreach (var propertyName in propertyNames)
            {
                var property = jsonObject.FirstOrDefault(item => string.Equals(item.Key, propertyName, StringComparison.OrdinalIgnoreCase));
                if (property.Key is not null)
                    jsonObject.Remove(property.Key);
            }
        }

        return Hash(node);
    }

    private static string ChapterManuscriptHash(ProjectExportChapter chapter) =>
        Hash(new
        {
            chapter.ManuscriptJson,
            chapter.ManuscriptRevision,
        });

    private static string ChapterFullHash(ProjectExportChapter chapter) =>
        HashWithoutProperties(chapter, nameof(ProjectExportChapter.Body));

    private static string ChapterReadableText(ProjectExportChapter chapter) =>
        ManuscriptPlainText(chapter.ManuscriptJson, chapter.Id, chapter.ManuscriptRevision);

    private static string DesignedPageReadableText(ProjectExportDesignedPage page) =>
        string.Join("\n\n", page.Contents.OrderBy(content => content.Id)
            .Select(content => ManuscriptPlainText(content.SemanticManuscriptJson, content.Id, content.Revision)));

    private static string GraphNodeReadableText(ProjectExportNode node) =>
        string.Join(
            "\n",
            new[] { $"Type: {node.NodeType}", $"Key: {node.Key}", $"Label: {node.Label ?? node.Key}" }
                .Concat(node.Properties
                    .OrderBy(property => property.Key, StringComparer.Ordinal)
                    .Select(property => $"{property.Key}: {ReadableGraphValue(property.Value)}")));

    private static string GraphEdgeReadableText(ProjectExportEdge edge) =>
        string.Join(
            "\n",
            new[] { $"From: {edge.From.StableKey}", $"To: {edge.To.StableKey}", $"Type: {edge.EdgeType}" }
                .Concat(edge.Properties
                    .OrderBy(property => property.Key, StringComparer.Ordinal)
                    .Select(property => $"{property.Key}: {ReadableGraphValue(property.Value)}")));

    private static string ReadableGraphValue(object? value) => value switch
    {
        null => "(none)",
        string text => text,
        bool boolean => boolean ? "true" : "false",
        JsonElement element when element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            => "(none)",
        JsonElement element => element.ToString(),
        JsonDocument document => document.RootElement.ToString(),
        JsonNode node => node.ToJsonString(),
        _ => SerializeGraphValue(value),
    };

    private static string SerializeGraphValue(object value)
    {
        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch (JsonException)
        {
            return Convert.ToString(value) ?? "(unavailable)";
        }
    }

    private static string BookBriefReadableText(ProjectExportBookBrief brief) =>
        string.Join(
            "\n\n",
            $"Premise: {brief.Premise}",
            $"Genre: {brief.Genre}",
            $"Primary themes: {brief.PrimaryThemes}",
            $"Purpose: {brief.Purpose}",
            $"Creative constraints: {brief.CreativeConstraints}",
            $"Target audience: {brief.TargetAudience}",
            $"Reading level guidance: {brief.ReadingLevelGuidance}",
            $"Point of view: {brief.PointOfView}",
            $"Tense: {brief.Tense}",
            $"Voice and tone: {brief.VoiceAndTone}",
            $"Language locale: {brief.LanguageLocale}",
            $"House style: {brief.HouseStyle}",
            $"Accessibility goals: {brief.AccessibilityGoals}",
            $"Visual direction: {brief.VisualDirection}");

    private static string AnnotationReadableText(ProjectExportManuscriptAnnotation annotation) =>
        string.Join(
            "\n\n",
            $"Note: {annotation.NoteText}",
            $"Original quote: {annotation.OriginalQuote}",
            $"Before: {annotation.ContextBefore}",
            $"After: {annotation.ContextAfter}");

    private static string PublicationBookReadableText(ProjectExportPublicationBook book) =>
        JoinManuscriptTexts(book.Matter);

    private static string PublicationEditionReadableText(ProjectExportPublicationEdition edition) =>
        string.Join(
            "\n\n",
            (edition.Matter ?? [])
                .OrderBy(item => item.Id)
                .Select(item => ManuscriptPlainText(item.ManuscriptJson, item.Id, item.Revision))
                .Concat(edition.ChapterOverrides
                    .OrderBy(item => item.Id)
                    .Select(item => ManuscriptPlainText(item.ManuscriptJson, item.ChapterId, item.Revision))));

    private static string PublicationSectionReadableText(ProjectExportPublicationSection section) =>
        ManuscriptPlainText(section.ManuscriptJson, section.Id, section.Revision);

    private static string JoinManuscriptTexts(IEnumerable<ProjectExportPublicationMatter>? matters) =>
        string.Join(
            "\n\n",
            matters?
                .OrderBy(item => item.Id)
                .Select(item => ManuscriptPlainText(item.ManuscriptJson, item.Id, item.Revision))
            ?? []);

    private static string ManuscriptPlainText(string json, Guid id, long revision) =>
        ManuscriptCodec.ProjectPlainText(ManuscriptCodec.Deserialize(json, id, revision));

    private static BoundedText? ProjectReadableText<T>(
        VersionHistoryCompareOptions options,
        string category,
        string key,
        Func<T, string?>? projector,
        T item)
    {
        if (projector is null || !options.Includes(category, key))
            return null;

        return ReadableText(options, category, key, projector(item));
    }

    private static BoundedText? ReadableText(
        VersionHistoryCompareOptions options,
        string category,
        string key,
        string? value)
    {
        if (value is null)
            return null;

        return options.IsUnbounded(category, key) ? new(value, false) : BoundText(value);
    }

    private static BoundedText? BoundText(string? value)
    {
        if (value is null)
            return null;

        if (value.Length <= MaxReadableTextLength)
            return new(value, false);

        var length = MaxReadableTextLength;
        if (length > 0 && char.IsHighSurrogate(value[length - 1]))
            length--;
        return new(value[..length], true);
    }

    private static string ChapterMetadataHash(ProjectExportChapter chapter) =>
        Hash(new
        {
            chapter.Id,
            chapter.ActId,
            chapter.Title,
            chapter.Synopsis,
            chapter.Order,
            chapter.VisualMode,
            chapter.PageLayoutKind,
            chapter.PageLayoutJson,
            chapter.IllustrationLayoutJson,
            ImageIds = chapter.ExplicitImageContextImageIds.OrderBy(id => id).ToList(),
        });

    private static string DesignedPageManuscriptHash(ProjectExportDesignedPage page) =>
        Hash(new
        {
            Contents = page.Contents.OrderBy(content => content.Id).Select(content => new
            {
                content.Id, content.EditionId, content.SemanticManuscriptJson, content.AccessibilityDescription, content.Revision,
                Variants = content.Variants.OrderBy(variant => variant.Id)
                    .Select(variant => new { variant.Id, variant.SceneJson, variant.Revision })
                .ToList(),
            }).ToList(),
        });

    private static string DesignedPageMetadataHash(ProjectExportDesignedPage page) =>
        Hash(new
        {
            page.Id,
            page.Name,
            page.ScopeEditionId,
            Contents = page.Contents.OrderBy(content => content.Id).Select(content => new
            {
                content.Id, content.EditionId, content.Revision, content.ActiveVariantId,
                Variants = content.Variants.OrderBy(variant => variant.Id)
                    .Select(variant => new { variant.Id, variant.GeometryKey, variant.Revision }).ToList(),
            }).ToList(),
        });

    private static IReadOnlyList<FontFaceItem> FlattenFontFaces(IEnumerable<VersionHistoryFontFamily> families) =>
        families
            .OrderBy(family => family.Id)
            .SelectMany(family => family.Faces
                .OrderBy(face => face.Id)
                .Select(face => new FontFaceItem(family.Id, family.Name, face)))
            .ToList();

    private static string ReferenceLabel(VersionHistoryProjectReference reference) =>
        string.IsNullOrWhiteSpace(reference.ReferencedProjectSlug)
            ? reference.ReferencedProjectName
            : $"{reference.ReferencedProjectName} ({reference.ReferencedProjectSlug})";

    private static string EdgeKey(ProjectExportEdge edge) =>
        $"{edge.From.StableKey}|{edge.EdgeType}|{edge.To.StableKey}";

    private static string VisualExampleKey(ProjectExportEntityVisualExample example) =>
        $"{example.Entity.StableKey}|{GuidKey(example.ImageId)}|{example.Origin}|{example.SourceLocator}";

    private static string GuidKey(Guid value) => value.ToString("N");

    private sealed record AreaAccumulator(string Area)
    {
        private readonly List<VersionHistorySnapshotChangeEntry> _entries = [];
        private int _added;
        private int _removed;
        private int _changed;
        private int _unchanged;

        public void Add(ItemComparison comparison)
        {
            _added += comparison.Added;
            _removed += comparison.Removed;
            _changed += comparison.Changed;
            _unchanged += comparison.Unchanged;
            _entries.AddRange(comparison.Entries);
        }

        public VersionHistorySnapshotAreaComparison Build() =>
            new(
                Area,
                new VersionHistorySnapshotAreaSummary(Area, _added, _removed, _changed, _unchanged),
                _entries
                    .OrderBy(entry => entry.Category, StringComparer.Ordinal)
                    .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                    .ToList());
    }

    private sealed record ItemComparison(
        int Added,
        int Removed,
        int Changed,
        int Unchanged,
        IReadOnlyList<VersionHistorySnapshotChangeEntry> Entries);

    private sealed record ProjectSettingsValue(
        ProjectExportProject Project,
        ProjectExportPageSetup? PageSetup,
        bool ContestModeEnabled);

    private sealed record FontFaceItem(
        Guid FamilyId,
        string FamilyName,
        VersionHistoryFontFace Face);

    private sealed record BoundedText(string Value, bool Truncated);
}

internal static class VersionHistoryProjectExportNodeExtensions
{
    public static string StableKey(this ProjectExportNode node) =>
        $"{node.NodeType}/{node.Key}";
}
