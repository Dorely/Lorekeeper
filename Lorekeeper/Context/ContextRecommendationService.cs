using System.Text;
using Lorekeeper.Chapters;
using Lorekeeper.Knowledge;
using Lorekeeper.Llm;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.Context;

public sealed class ContextRecommendationService(
IAppDatabaseOperationFactory database, IEditorContextService editorContext,
    IEntityService entities,
    IEntityTypeService entityTypes,
    IChapterService chapters,
    IActService acts,
    IConfiguration configuration,
    IEmbeddingService embeddings,
    IVectorStore vectors,
    IChapterSemanticProjectionService semanticProjection,
    ILogger<ContextRecommendationService> logger) : IContextRecommendationService
{
    private const int RecommendationLimit = 50;
    private const int PerSourceTypeVectorLimit = 12;
    private const int SemanticQueryBodyChars = 6_000;
    private const int ExactTitleSearchRank = 0;
    private const int BoundaryPrefixTitleSearchRank = 1;
    private const int PrefixTitleSearchRank = 2;
    private const int ContainsTitleSearchRank = 3;
    private const int DetailSearchRank = 4;
    private const int SemanticSearchRank = 5;
    private const int NoSearchRank = int.MaxValue;

    private int SemanticQueryMaxChars => Math.Max(100, configuration.GetValue("Embeddings:MaxChunkChars", SemanticQueryBodyChars));

    public async Task<IReadOnlyList<ContextRecommendation>> ListAsync(
        Guid projectId,
        Guid chapterId,
        string? query = null,
        CancellationToken cancellationToken = default)
    {
        var includedKeys = (await editorContext.ListIncludedContextKeysAsync(projectId, chapterId, cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        includedKeys.Add(EditorContextKeys.ChapterReference(chapterId));

        var includedEntityIds = (await editorContext.ListIncludedEntityIdsAsync(projectId, chapterId, cancellationToken)).ToHashSet();
        var results = new Dictionary<string, RankedContextRecommendation>(StringComparer.OrdinalIgnoreCase);
        var trimmedQuery = query?.Trim();

        await AddSecondDegreeGraphRecommendationsAsync(projectId, includedEntityIds, includedKeys, results, cancellationToken);
        if (await embeddings.IsAvailableAsync(cancellationToken))
            await AddSemanticRecommendationsAsync(projectId, chapterId, includedEntityIds, includedKeys, results, trimmedQuery, cancellationToken);

        if (!string.IsNullOrWhiteSpace(trimmedQuery))
            await AddManualSearchRecommendationsAsync(projectId, chapterId, includedKeys, results, trimmedQuery, cancellationToken);

        return results.Values
            .OrderBy(result => result.Recommendation.IsSearchResult ? 0 : 1)
            .ThenBy(result => result.SearchRank)
            .ThenBy(result => result.Recommendation.Distance ?? double.MaxValue)
            .ThenBy(result => result.Recommendation.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.Recommendation.Name, StringComparer.OrdinalIgnoreCase)
            .Select(result => result.Recommendation)
            .Take(RecommendationLimit)
            .ToList();
    }

    private async Task AddSecondDegreeGraphRecommendationsAsync(
        Guid projectId,
        IReadOnlyCollection<Guid> includedEntityIds,
        IReadOnlySet<string> includedKeys,
        IDictionary<string, RankedContextRecommendation> results,
        CancellationToken cancellationToken)
    {
        foreach (var rootId in includedEntityIds.Take(24))
        {
            var root = await entities.GetAsync(projectId, rootId, cancellationToken);
            if (root is null) continue;

            foreach (var link in await entities.ListLinksAsync(projectId, root.Id, cancellationToken))
            {
                if (link.IsAutoLink) continue;
                if (!IsContextEntityType(link.OtherEntityType)) continue;
                if (includedEntityIds.Contains(link.OtherEntityId)) continue;

                var key = EditorContextKeys.Entity(link.OtherEntityId);
                if (includedKeys.Contains(key)) continue;

                var entity = await entities.GetAsync(projectId, link.OtherEntityId, cancellationToken);
                if (entity is null || !IsContextEntityType(entity.Type)) continue;

                AddOrMerge(results, ProjectEntity(entity, [$"Linked to {root.Type} — {root.Name}"], isSearchResult: false), NoSearchRank);
            }
        }
    }

    private async Task AddSemanticRecommendationsAsync(
        Guid projectId,
        Guid chapterId,
        IReadOnlyCollection<Guid> includedEntityIds,
        IReadOnlySet<string> includedKeys,
        IDictionary<string, RankedContextRecommendation> results,
        string? query,
        CancellationToken cancellationToken)
    {
        var semanticQuery = await BuildSemanticQueryAsync(projectId, chapterId, includedEntityIds, query, cancellationToken);
        if (string.IsNullOrWhiteSpace(semanticQuery)) return;
        semanticQuery = LimitSemanticQuery(semanticQuery, projectId, chapterId);

        try
        {
            var embedding = await embeddings.GenerateEmbeddingAsync(semanticQuery, cancellationToken);
            foreach (var sourceType in ContextVectorSourceTypes.All)
            {
                var matches = await vectors.SearchAsync(
                    embedding,
                    Project.ScopeKey(projectId),
                    PerSourceTypeVectorLimit,
                    sourceType,
                    cancellationToken);

                foreach (var match in matches)
                    await AddVectorRecommendationAsync(projectId, chapterId, includedKeys, results, match, isSearchResult: !string.IsNullOrWhiteSpace(query), cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to load semantic context recommendations for project {ProjectId}", projectId);
        }
    }

    private async Task<string> BuildSemanticQueryAsync(
        Guid projectId,
        Guid chapterId,
        IReadOnlyCollection<Guid> includedEntityIds,
        string? query,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(query)) return query.Trim();

        var chapter = await chapters.GetAsync(chapterId, cancellationToken);
        if (chapter is null || chapter.ProjectId != projectId) return string.Empty;

        var sb = new StringBuilder();
        sb.Append("Current chapter: ").AppendLine(chapter.Title);
        AppendOptional(sb, "Synopsis", chapter.Synopsis);
        AppendOptional(sb, "Body", Truncate(await semanticProjection.ExpandPlainTextAsync(chapter, cancellationToken), SemanticQueryBodyChars));

        foreach (var entityId in includedEntityIds.Take(12))
        {
            var entity = await entities.GetAsync(projectId, entityId, cancellationToken);
            if (entity is null) continue;
            sb.Append("Context entity: ").Append(entity.Type).Append(" — ").AppendLine(entity.Name);
            foreach (var property in entity.Properties
                .Where(property => !string.IsNullOrWhiteSpace(property.Value))
                .OrderBy(property => property.Key, StringComparer.OrdinalIgnoreCase)
                .Take(6))
            {
                sb.Append(property.Key).Append(": ").AppendLine(property.Value);
            }
        }

        return sb.ToString().Trim();
    }

    private string LimitSemanticQuery(string semanticQuery, Guid projectId, Guid chapterId)
    {
        var maxChars = SemanticQueryMaxChars;
        if (semanticQuery.Length <= maxChars) return semanticQuery;

        logger.LogDebug(
            "Context recommendation semantic query for project {ProjectId} chapter {ChapterId} was {Length} chars; trimming to {MaxChars} before embedding.",
            projectId,
            chapterId,
            semanticQuery.Length,
            maxChars);
        return Truncate(semanticQuery, maxChars);
    }

    private async Task AddVectorRecommendationAsync(
        Guid projectId,
        Guid currentChapterId,
        IReadOnlySet<string> includedKeys,
        IDictionary<string, RankedContextRecommendation> results,
        KnowledgeResult match,
        bool isSearchResult,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(match.SourceId) || !Guid.TryParseExact(match.SourceId, "N", out var sourceId)) return;

        var reason = isSearchResult ? "Matched context search" : "Recommended from chapter context";
        ContextRecommendation? recommendation = match.SourceType switch
        {
            ContextVectorSourceTypes.Entity => await BuildEntityRecommendationAsync(projectId, sourceId, reason, isSearchResult, match.Distance, cancellationToken),
            ContextVectorSourceTypes.Chapter when sourceId != currentChapterId => await BuildChapterRecommendationAsync(projectId, sourceId, reason, isSearchResult, match.Distance, cancellationToken),
            ContextVectorSourceTypes.Act => await BuildActRecommendationAsync(projectId, sourceId, reason, isSearchResult, match.Distance, cancellationToken),
            ContextVectorSourceTypes.IngestSource => await BuildIngestSourceRecommendationAsync(projectId, sourceId, reason, isSearchResult, match.Distance, cancellationToken),
            ContextVectorSourceTypes.IngestSourceChunk => await BuildIngestSourceChunkRecommendationAsync(projectId, sourceId, reason, isSearchResult, match.Distance, cancellationToken),
            _ => null,
        };

        if (recommendation is null || includedKeys.Contains(recommendation.Key)) return;
        AddOrMerge(results, recommendation, isSearchResult ? SemanticSearchRank : NoSearchRank);
    }

    private async Task AddManualSearchRecommendationsAsync(
        Guid projectId,
        Guid currentChapterId,
        IReadOnlySet<string> includedKeys,
        IDictionary<string, RankedContextRecommendation> results,
        string query,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var ingest = databaseOperation.Repositories.Ingest;
        foreach (var (entity, searchRank) in await SearchEntitiesAsync(projectId, query, cancellationToken))
        {
            var recommendation = ProjectEntity(entity, ["Matched manual search"], isSearchResult: true);
            if (!includedKeys.Contains(recommendation.Key)) AddOrMerge(results, recommendation, searchRank);
        }

        var projectChapters = await chapters.ListAsync(projectId, cancellationToken);
        var expanded = await semanticProjection.ExpandPlainTextAsync(projectChapters, cancellationToken);
        foreach (var chapter in projectChapters)
        {
            if (chapter.Id == currentChapterId) continue;
            var plainText = expanded.GetValueOrDefault(chapter.Id, chapter.PlainText);
            var searchRank = SearchRank(chapter, plainText, query);
            if (searchRank is null) continue;

            var recommendation = ProjectChapter(chapter, plainText, ["Matched manual search"], isSearchResult: true);
            if (!includedKeys.Contains(recommendation.Key)) AddOrMerge(results, recommendation, searchRank.Value);
        }

        foreach (var act in await acts.ListAsync(projectId, cancellationToken))
        {
            var searchRank = SearchRank(act, query);
            if (searchRank is null) continue;

            var recommendation = ProjectAct(act, ["Matched manual search"], isSearchResult: true);
            if (!includedKeys.Contains(recommendation.Key)) AddOrMerge(results, recommendation, searchRank.Value);
        }

        foreach (var source in await ingest.ListSourcesByProjectAsync(projectId, cancellationToken))
        {
            var sourceSearchRank = SearchRank(source, query);
            if (sourceSearchRank is not null)
            {
                var recommendation = ProjectIngestSource(source, ["Matched manual search"], isSearchResult: true);
                if (!includedKeys.Contains(recommendation.Key)) AddOrMerge(results, recommendation, sourceSearchRank.Value);
            }

            foreach (var sourceChunk in await ingest.ListSourceChunksAsync(source.Id, cancellationToken))
            {
                var chunkSearchRank = SearchRank(source, sourceChunk, query);
                if (chunkSearchRank is null)
                {
                    var excerpt = await ingest.GetSourceChunkExcerptAsync(sourceChunk.Id, 4_000, cancellationToken);
                    if (Contains(excerpt?.Text, query))
                        chunkSearchRank = DetailSearchRank;
                }
                if (chunkSearchRank is null) continue;

                var chunkRecommendation = ProjectIngestSourceChunk(source, sourceChunk, ["Matched manual search"], isSearchResult: true);
                if (!includedKeys.Contains(chunkRecommendation.Key)) AddOrMerge(results, chunkRecommendation, chunkSearchRank.Value);
            }
        }
    }

    private async Task<IReadOnlyList<(StoryEntity Entity, int SearchRank)>> SearchEntitiesAsync(Guid projectId, string query, CancellationToken cancellationToken)
    {
        var types = await entityTypes.ListAsync(projectId, includeStructural: true, cancellationToken);
        var matches = new List<(StoryEntity Entity, int SearchRank)>();

        foreach (var type in types.Where(type => IsContextEntityType(type.Type)))
        {
            var list = await entities.ListAsync(projectId, type.Type, parentId: null, cancellationToken);
            matches.AddRange(list
                .Select(entity => (Entity: entity, SearchRank: SearchRank(entity, query)))
                .Where(match => match.SearchRank is not null)
                .Select(match => (match.Entity, match.SearchRank!.Value)));
        }

        return matches;
    }

    private async Task<ContextRecommendation?> BuildEntityRecommendationAsync(
        Guid projectId,
        Guid entityId,
        string reason,
        bool isSearchResult,
        double? distance,
        CancellationToken cancellationToken)
    {
        var entity = await entities.GetAsync(projectId, entityId, cancellationToken);
        return entity is null || !IsContextEntityType(entity.Type)
            ? null
            : ProjectEntity(entity, [reason], isSearchResult, distance);
    }

    private async Task<ContextRecommendation?> BuildChapterRecommendationAsync(
        Guid projectId,
        Guid chapterId,
        string reason,
        bool isSearchResult,
        double? distance,
        CancellationToken cancellationToken)
    {
        var chapter = await chapters.GetAsync(chapterId, cancellationToken);
        return chapter is null || chapter.ProjectId != projectId
            ? null
            : ProjectChapter(
                chapter,
                await semanticProjection.ExpandPlainTextAsync(chapter, cancellationToken),
                [reason],
                isSearchResult,
                distance);
    }

    private async Task<ContextRecommendation?> BuildActRecommendationAsync(
        Guid projectId,
        Guid actId,
        string reason,
        bool isSearchResult,
        double? distance,
        CancellationToken cancellationToken)
    {
        var act = await acts.GetAsync(actId, cancellationToken);
        return act is null || act.ProjectId != projectId
            ? null
            : ProjectAct(act, [reason], isSearchResult, distance);
    }

    private async Task<ContextRecommendation?> BuildIngestSourceRecommendationAsync(
        Guid projectId,
        Guid sourceId,
        string reason,
        bool isSearchResult,
        double? distance,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var ingest = databaseOperation.Repositories.Ingest;
        var source = await ingest.GetSourceAsync(sourceId, cancellationToken);
        return source is null || source.ProjectId != projectId
            ? null
            : ProjectIngestSource(source, [reason], isSearchResult, distance);
    }

    private async Task<ContextRecommendation?> BuildIngestSourceChunkRecommendationAsync(
        Guid projectId,
        Guid sourceChunkId,
        string reason,
        bool isSearchResult,
        double? distance,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var ingest = databaseOperation.Repositories.Ingest;
        var sourceChunk = await ingest.GetSourceChunkAsync(sourceChunkId, cancellationToken);
        if (sourceChunk is null) return null;
        var source = await ingest.GetSourceAsync(sourceChunk.SourceId, cancellationToken);
        return source is null || source.ProjectId != projectId
            ? null
            : ProjectIngestSourceChunk(source, sourceChunk, [reason], isSearchResult, distance);
    }

    private static ContextRecommendation ProjectEntity(
        StoryEntity entity,
        IReadOnlyList<string> reasons,
        bool isSearchResult,
        double? distance = null) =>
        new(
            EditorContextKeys.Entity(entity.Id),
            ContextItemKind.Entity,
            entity.Type,
            entity.Name,
            Preview(entity.Properties.Values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty),
            reasons,
            isSearchResult,
            distance);

    private static ContextRecommendation ProjectChapter(Chapter chapter, string plainText, IReadOnlyList<string> reasons, bool isSearchResult, double? distance = null) =>
        new(
            EditorContextKeys.ChapterReference(chapter.Id),
            ContextItemKind.ChapterReference,
            "Chapter",
            chapter.Title,
            Preview(!string.IsNullOrWhiteSpace(chapter.Synopsis) ? chapter.Synopsis : plainText),
            reasons,
            isSearchResult,
            distance);

    private static ContextRecommendation ProjectAct(Act act, IReadOnlyList<string> reasons, bool isSearchResult, double? distance = null) =>
        new(
            EditorContextKeys.ActReference(act.Id),
            ContextItemKind.ActReference,
            "Act",
            act.Title,
            Preview(act.Synopsis),
            reasons,
            isSearchResult,
            distance);

    private static ContextRecommendation ProjectIngestSource(IngestSource source, IReadOnlyList<string> reasons, bool isSearchResult, double? distance = null) =>
        new(
            EditorContextKeys.IngestSourceReference(source.Id),
            ContextItemKind.IngestSourceReference,
            "Source",
            source.Title,
            Preview(!string.IsNullOrWhiteSpace(source.Description) ? source.Description : source.SourceKind),
            reasons,
            isSearchResult,
            distance);

    private static ContextRecommendation ProjectIngestSourceChunk(
        IngestSource source,
        IngestSourceChunk sourceChunk,
        IReadOnlyList<string> reasons,
        bool isSearchResult,
        double? distance = null) =>
        new(
            EditorContextKeys.IngestSourceChunkReference(sourceChunk.Id),
            ContextItemKind.IngestSourceChunkReference,
            "Source chunk",
            $"{source.Title} Part {sourceChunk.Index + 1}",
            Preview(!string.IsNullOrWhiteSpace(sourceChunk.Summary) ? sourceChunk.Summary : sourceChunk.AgentNotes),
            reasons,
            isSearchResult,
            distance);

    private static void AddOrMerge(IDictionary<string, RankedContextRecommendation> results, ContextRecommendation recommendation, int searchRank)
    {
        if (results.TryGetValue(recommendation.Key, out var existing))
        {
            var mergedRecommendation = existing.Recommendation with
            {
                Reasons = existing.Recommendation.Reasons.Concat(recommendation.Reasons).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                IsSearchResult = existing.Recommendation.IsSearchResult || recommendation.IsSearchResult,
                Distance = existing.Recommendation.Distance is null
                    ? recommendation.Distance
                    : recommendation.Distance is null
                        ? existing.Recommendation.Distance
                        : Math.Min(existing.Recommendation.Distance.Value, recommendation.Distance.Value),
            };

            results[recommendation.Key] = existing with
            {
                Recommendation = mergedRecommendation,
                SearchRank = Math.Min(existing.SearchRank, searchRank),
            };
            return;
        }

        results[recommendation.Key] = new RankedContextRecommendation(recommendation, searchRank);
    }

    private static int? SearchRank(StoryEntity entity, string query)
    {
        var titleRank = BestTitleSearchRank(query, entity.Name);
        if (titleRank is not null) return titleRank.Value;

        return Contains(entity.Type, query)
            || Contains(entity.Summary, query)
            || entity.Aliases.Any(alias => Contains(alias, query))
            || entity.WikiSections.Any(section => Contains(section.Title, query) || Contains(section.Body, query))
            || entity.SourceEvidence.Any(source => Contains(source.SourceTitle, query) || Contains(source.Markdown, query))
            || entity.Properties.Any(property => Contains(property.Key, query) || Contains(property.Value, query))
            ? DetailSearchRank
            : null;
    }

    private static int? SearchRank(Chapter chapter, string plainText, string query)
    {
        var titleRank = BestTitleSearchRank(
            query,
            chapter.Title,
            $"Chapter {chapter.Order + 1}",
            $"Chapter {chapter.Order + 1} {chapter.Title}");
        if (titleRank is not null) return titleRank.Value;

        return Contains(chapter.Synopsis, query) || Contains(plainText, query)
            ? DetailSearchRank
            : null;
    }

    private static int? SearchRank(Act act, string query)
    {
        var titleRank = BestTitleSearchRank(
            query,
            act.Title,
            $"Act {act.Order + 1}",
            $"Act {act.Order + 1} {act.Title}");
        if (titleRank is not null) return titleRank.Value;

        return Contains(act.Synopsis, query) ? DetailSearchRank : null;
    }

    private static int? SearchRank(IngestSource source, string query)
    {
        var titleRank = BestTitleSearchRank(query, source.Title);
        if (titleRank is not null) return titleRank.Value;

        return Contains(source.SourceKind, query) || Contains(source.Description, query) || Contains(source.UserInstructions, query)
            ? DetailSearchRank
            : null;
    }

    private static int? SearchRank(IngestSource source, IngestSourceChunk sourceChunk, string query)
    {
        var titleRank = BestTitleSearchRank(
            query,
            sourceChunk.Title,
            sourceChunk.HeadingPath,
            $"{source.Title} Part {sourceChunk.Index + 1}");
        if (titleRank is not null) return titleRank.Value;

        return Contains(sourceChunk.Summary, query) || Contains(sourceChunk.AgentNotes, query)
            ? DetailSearchRank
            : null;
    }

    private static int? BestTitleSearchRank(string query, params string?[] values)
    {
        int? bestRank = null;
        foreach (var value in values)
        {
            var rank = TitleSearchRank(value, query);
            if (rank is null) continue;
            bestRank = bestRank is null ? rank.Value : Math.Min(bestRank.Value, rank.Value);
        }

        return bestRank;
    }

    private static int? TitleSearchRank(string? value, string query)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var title = value.Trim();
        var trimmedQuery = query.Trim();
        if (title.Equals(trimmedQuery, StringComparison.OrdinalIgnoreCase)) return ExactTitleSearchRank;
        if (title.StartsWith(trimmedQuery, StringComparison.OrdinalIgnoreCase))
        {
            return HasBoundaryAfterPrefix(title, trimmedQuery.Length)
                ? BoundaryPrefixTitleSearchRank
                : PrefixTitleSearchRank;
        }

        return title.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase)
            ? ContainsTitleSearchRank
            : null;
    }

    private static bool HasBoundaryAfterPrefix(string value, int prefixLength) =>
        prefixLength >= value.Length || !char.IsLetterOrDigit(value[prefixLength]);

    private static bool Contains(string? value, string query) =>
        value?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false;

    private sealed record RankedContextRecommendation(ContextRecommendation Recommendation, int SearchRank);

    private static string Preview(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var preview = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return preview.Length <= 140 ? preview : preview[..140] + "...";
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static void AppendOptional(StringBuilder sb, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        sb.Append(label).Append(": ").AppendLine(value.Trim());
    }

    private static bool IsContextEntityType(string type) =>
        !string.Equals(type, EntityTypeService.ProjectNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.ActNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.ChapterNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.ProjectFactNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.SourceNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.SourceChunkNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.SourceBlockNodeType, StringComparison.OrdinalIgnoreCase);
}
