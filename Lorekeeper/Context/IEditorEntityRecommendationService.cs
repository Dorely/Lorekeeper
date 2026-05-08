namespace Lorekeeper.Context;

public interface IEditorEntityRecommendationService
{
    Task<IReadOnlyList<EditorEntityRecommendation>> ListAsync(
        Guid projectId,
        Guid chapterId,
        string? query = null,
        CancellationToken cancellationToken = default);
}

public sealed record EditorEntityRecommendation(
    Guid EntityId,
    string Type,
    string Name,
    IReadOnlyDictionary<string, string?> Properties,
    IReadOnlyList<string> Reasons,
    bool IsSearchResult);