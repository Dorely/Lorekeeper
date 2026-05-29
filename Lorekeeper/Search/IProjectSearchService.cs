namespace Lorekeeper.Search;

public interface IProjectSearchService
{
    Task<IReadOnlyList<ProjectSearchResult>> SearchAsync(
        ProjectSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProjectSearchSource>> ListSourcesAsync(
        Guid projectId,
        string? query = null,
        IReadOnlyCollection<string>? sourceTypes = null,
        int topK = 10,
        CancellationToken cancellationToken = default);

    Task<ProjectSourceReadResult?> ReadSourceAsync(
        Guid projectId,
        string sourceType,
        Guid sourceId,
        int? pageNumber = null,
        CancellationToken cancellationToken = default);
}
