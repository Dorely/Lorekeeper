using System.Text;
using Lorekeeper.Chapters;
using Lorekeeper.Knowledge;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.Context;

public sealed class ContextRecommendationService(
    IEditorContextService editorContext,
    IEntityService entities,
    IEntityTypeService entityTypes,
    IChapterService chapters,
    IActService acts,
    IIngestRepository ingest,
    IEmbeddingService embeddings,
    IVectorStore vectors,
    ILogger<ContextRecommendationService> logger) : IContextRecommendationService
{
    private const int RecommendationLimit = 50;
    private const int PerSourceTypeVectorLimit = 12;
    private const int SemanticQueryBodyChars = 6_000;

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
        var results = new Dictionary<string, ContextRecommendation>(StringComparer.OrdinalIgnoreCase);
        var trimmedQuery = query?.Trim();

        await AddSecondDegreeGraphRecommendationsAsync(projectId, includedEntityIds, includedKeys, results, cancellationToken);
        await AddSemanticRecommendationsAsync(projectId, chapterId, includedEntityIds, includedKeys, results, trimmedQuery, cancellationToken);

        if (!string.IsNullOrWhiteSpace(trimmedQuery))
            await AddManualSearchRecommendationsAsync(projectId, chapterId, includedKeys, results, trimmedQuery, cancellationToken);

        return results.Values
            .OrderBy(recommendation => recommendation.IsSearchResult ? 0 : 1)
            .ThenBy(recommendation => recommendation.Distance ?? double.MaxValue)
            .ThenBy(recommendation => recommendation.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(recommendation => recommendation.Name, StringComparer.OrdinalIgnoreCase)
            .Take(RecommendationLimit)
            .ToList();
    }

    private async Task AddSecondDegreeGraphRecommendationsAsync(
        Guid projectId,
        IReadOnlyCollection<Guid> includedEntityIds,
        IReadOnlySet<string> includedKeys,
        IDictionary<string, ContextRecommendation> results,
        CancellationToken cancellationToken)
    {
        foreach (var rootId in includedEntityIds.Take(24))
        {
            var root = await entities.GetAsync(projectId, rootId, cancellationToken);
            if (root is null) continue;

            foreach (var link in await entities.ListLinksAsync(projectId, root.Id, cancellationToken))
            {
                if (!IsContextEntityType(link.OtherEntityType)) continue;
                if (includedEntityIds.Contains(link.OtherEntityId)) continue;

                var key = EditorContextKeys.Entity(link.OtherEntityId);
                if (includedKeys.Contains(key)) continue;

                var entity = await entities.GetAsync(projectId, link.OtherEntityId, cancellationToken);
                if (entity is null || !IsContextEntityType(entity.Type)) continue;

                AddOrMerge(results, ProjectEntity(entity, [$"Linked to {root.Type} — {root.Name}"], isSearchResult: false));
            }
        }
    }

    private async Task AddSemanticRecommendationsAsync(
        Guid projectId,
        Guid chapterId,
        IReadOnlyCollection<Guid> includedEntityIds,
        IReadOnlySet<string> includedKeys,
        IDictionary<string, ContextRecommendation> results,
        string? query,
        CancellationToken cancellationToken)
    {
        var semanticQuery = await BuildSemanticQueryAsync(projectId, chapterId, includedEntityIds, query, cancellationToken);
        if (string.IsNullOrWhiteSpace(semanticQuery)) return;

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
        AppendOptional(sb, "Body", Truncate(chapter.Body, SemanticQueryBodyChars));

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

    private async Task AddVectorRecommendationAsync(
        Guid projectId,
        Guid currentChapterId,
        IReadOnlySet<string> includedKeys,
        IDictionary<string, ContextRecommendation> results,
        KnowledgeResult match,
        bool isSearchResult,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(match.SourceId) || !Guid.TryParseExact(match.SourceId, "N", out var sourceId)) return;

        var reason = isSearchResult ? "Matched semantic search" : "Semantically matched chapter context";
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
        AddOrMerge(results, recommendation);
    }

    private async Task AddManualSearchRecommendationsAsync(
        Guid projectId,
        Guid currentChapterId,
        IReadOnlySet<string> includedKeys,
        IDictionary<string, ContextRecommendation> results,
        string query,
        CancellationToken cancellationToken)
    {
        foreach (var entity in await SearchEntitiesAsync(projectId, query, cancellationToken))
        {
            var recommendation = ProjectEntity(entity, ["Matched manual search"], isSearchResult: true);
            if (!includedKeys.Contains(recommendation.Key)) AddOrMerge(results, recommendation);
        }

        foreach (var chapter in (await chapters.ListAsync(projectId, cancellationToken)).Where(chapter => chapter.Id != currentChapterId && Matches(chapter, query)))
        {
            var recommendation = ProjectChapter(chapter, ["Matched manual search"], isSearchResult: true);
            if (!includedKeys.Contains(recommendation.Key)) AddOrMerge(results, recommendation);
        }

        foreach (var act in (await acts.ListAsync(projectId, cancellationToken)).Where(act => Matches(act, query)))
        {
            var recommendation = ProjectAct(act, ["Matched manual search"], isSearchResult: true);
            if (!includedKeys.Contains(recommendation.Key)) AddOrMerge(results, recommendation);
        }

        foreach (var source in (await ingest.ListSourcesByProjectAsync(projectId, cancellationToken)).Where(source => Matches(source, query)))
        {
            var recommendation = ProjectIngestSource(source, ["Matched manual search"], isSearchResult: true);
            if (!includedKeys.Contains(recommendation.Key)) AddOrMerge(results, recommendation);

            foreach (var sourceChunk in (await ingest.ListSourceChunksAsync(source.Id, cancellationToken)).Where(sourceChunk => Matches(sourceChunk, query)))
            {
                var chunkRecommendation = ProjectIngestSourceChunk(source, sourceChunk, ["Matched manual search"], isSearchResult: true);
                if (!includedKeys.Contains(chunkRecommendation.Key)) AddOrMerge(results, chunkRecommendation);
            }
        }
    }

    private async Task<IReadOnlyList<StoryEntity>> SearchEntitiesAsync(Guid projectId, string query, CancellationToken cancellationToken)
    {
        var types = await entityTypes.ListAsync(projectId, includeStructural: true, cancellationToken);
        var matches = new List<StoryEntity>();

        foreach (var type in types.Where(type => IsContextEntityType(type.Type)))
        {
            var list = await entities.ListAsync(projectId, type.Type, parentId: null, cancellationToken);
            matches.AddRange(list.Where(entity => Matches(entity, query)));
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
            : ProjectChapter(chapter, [reason], isSearchResult, distance);
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

    private static ContextRecommendation ProjectChapter(Chapter chapter, IReadOnlyList<string> reasons, bool isSearchResult, double? distance = null) =>
        new(
            EditorContextKeys.ChapterReference(chapter.Id),
            ContextItemKind.ChapterReference,
            "Chapter",
            chapter.Title,
            Preview(!string.IsNullOrWhiteSpace(chapter.Synopsis) ? chapter.Synopsis : chapter.Body),
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

    private static void AddOrMerge(IDictionary<string, ContextRecommendation> results, ContextRecommendation recommendation)
    {
        if (results.TryGetValue(recommendation.Key, out var existing))
        {
            results[recommendation.Key] = existing with
            {
                Reasons = existing.Reasons.Concat(recommendation.Reasons).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                IsSearchResult = existing.IsSearchResult || recommendation.IsSearchResult,
                Distance = existing.Distance is null
                    ? recommendation.Distance
                    : recommendation.Distance is null
                        ? existing.Distance
                        : Math.Min(existing.Distance.Value, recommendation.Distance.Value),
            };
            return;
        }

        results[recommendation.Key] = recommendation;
    }

    private static bool Matches(StoryEntity entity, string query) =>
        Contains(entity.Name, query)
        || Contains(entity.Type, query)
        || entity.Properties.Any(property => Contains(property.Key, query) || Contains(property.Value, query));

    private static bool Matches(Chapter chapter, string query) =>
        Contains(chapter.Title, query) || Contains(chapter.Synopsis, query) || Contains(chapter.Body, query);

    private static bool Matches(Act act, string query) =>
        Contains(act.Title, query) || Contains(act.Synopsis, query);

    private static bool Matches(IngestSource source, string query) =>
        Contains(source.Title, query) || Contains(source.SourceKind, query) || Contains(source.Description, query) || Contains(source.UserInstructions, query);

    private static bool Matches(IngestSourceChunk sourceChunk, string query) =>
        Contains(sourceChunk.Title, query) || Contains(sourceChunk.HeadingPath, query) || Contains(sourceChunk.Summary, query) || Contains(sourceChunk.AgentNotes, query);

    private static bool Contains(string? value, string query) =>
        value?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false;

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
        && !string.Equals(type, EntityTypeService.SourceChunkNodeType, StringComparison.OrdinalIgnoreCase);
}