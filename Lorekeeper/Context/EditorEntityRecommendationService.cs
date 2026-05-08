using Lorekeeper.Outline;

namespace Lorekeeper.Context;

public sealed class EditorEntityRecommendationService(
    IEditorContextService editorContext,
    IEntityService entities,
    IEntityTypeService entityTypes) : IEditorEntityRecommendationService
{
    public async Task<IReadOnlyList<EditorEntityRecommendation>> ListAsync(
        Guid projectId,
        Guid chapterId,
        string? query = null,
        CancellationToken cancellationToken = default)
    {
        var included = (await editorContext.ListIncludedEntityIdsAsync(projectId, chapterId, cancellationToken)).ToHashSet();
        var results = new Dictionary<Guid, EditorEntityRecommendation>();

        foreach (var entity in await editorContext.ListAutoRelatedEntitiesAsync(projectId, chapterId, cancellationToken))
        {
            if (included.Contains(entity.Id)) continue;
            results[entity.Id] = Project(entity, ["Related to current chapter or its beats"], isSearchResult: false);
        }

        var trimmedQuery = query?.Trim();
        if (!string.IsNullOrWhiteSpace(trimmedQuery))
        {
            foreach (var entity in await SearchEntitiesAsync(projectId, trimmedQuery, cancellationToken))
            {
                if (included.Contains(entity.Id)) continue;

                if (results.TryGetValue(entity.Id, out var existing))
                {
                    results[entity.Id] = existing with
                    {
                        Reasons = existing.Reasons.Concat(["Matched manual search"]).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                        IsSearchResult = true,
                    };
                }
                else
                {
                    results[entity.Id] = Project(entity, ["Matched manual search"], isSearchResult: true);
                }
            }
        }

        return results.Values
            .OrderBy(recommendation => recommendation.IsSearchResult ? 0 : 1)
            .ThenBy(recommendation => recommendation.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(recommendation => recommendation.Name, StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToList();
    }

    private async Task<IReadOnlyList<StoryEntity>> SearchEntitiesAsync(
        Guid projectId,
        string query,
        CancellationToken cancellationToken)
    {
        var types = await entityTypes.ListAsync(projectId, includeStructural: true, cancellationToken);
        var matches = new List<StoryEntity>();

        foreach (var type in types.Where(type => IsSearchableEntityType(type.Type)))
        {
            var list = await entities.ListAsync(projectId, type.Type, parentId: null, cancellationToken);
            foreach (var entity in list)
            {
                if (Matches(entity, query))
                    matches.Add(entity);
            }
        }

        return matches;
    }

    private static EditorEntityRecommendation Project(StoryEntity entity, IReadOnlyList<string> reasons, bool isSearchResult) =>
        new(entity.Id, entity.Type, entity.Name, entity.Properties, reasons, isSearchResult);

    private static bool Matches(StoryEntity entity, string query) =>
        entity.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || entity.Type.Contains(query, StringComparison.OrdinalIgnoreCase)
        || entity.Properties.Any(property =>
            property.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (property.Value?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));

    private static bool IsSearchableEntityType(string type) =>
        !string.Equals(type, EntityTypeService.ProjectNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.ActNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.ChapterNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.ProjectFactNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.SourceNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.SourceChunkNodeType, StringComparison.OrdinalIgnoreCase);
}