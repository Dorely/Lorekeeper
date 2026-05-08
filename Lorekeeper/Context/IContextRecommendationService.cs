namespace Lorekeeper.Context;

public interface IContextRecommendationService
{
    Task<IReadOnlyList<ContextRecommendation>> ListAsync(
        Guid projectId,
        Guid chapterId,
        string? query = null,
        CancellationToken cancellationToken = default);
}

public sealed record ContextRecommendation(
    string Key,
    ContextItemKind Kind,
    string Type,
    string Name,
    string Preview,
    IReadOnlyList<string> Reasons,
    bool IsSearchResult,
    double? Distance);