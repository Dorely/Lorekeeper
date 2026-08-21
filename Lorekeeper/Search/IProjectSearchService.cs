namespace Lorekeeper.Search;

public interface IProjectSearchService
{
    Task<ProjectSearchResponse> SearchAsync(
        ProjectSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<ProjectSearchSourceResponse> ListSourcesAsync(
        Guid projectId,
        string? query = null,
        IReadOnlyCollection<string>? sourceTypes = null,
        int topK = 10,
        CancellationToken cancellationToken = default,
        bool includeReferencedProjects = false);

    Task<ProjectSourceReadResult?> ReadSourceAsync(
        Guid projectId,
        string sourceType,
        Guid sourceId,
        int? pageNumber = null,
        CancellationToken cancellationToken = default,
        Guid? originProjectId = null);
}
