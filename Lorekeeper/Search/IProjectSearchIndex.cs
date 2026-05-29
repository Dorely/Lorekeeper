namespace Lorekeeper.Search;

public interface IProjectSearchIndex
{
    Task StoreAsync(ProjectSearchIndexChunk chunk, CancellationToken cancellationToken = default);

    Task StoreManyAsync(IEnumerable<ProjectSearchIndexChunk> chunks, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProjectLexicalSearchResult>> SearchAsync(
        ProjectLexicalSearchRequest request,
        CancellationToken cancellationToken = default);

    Task DeleteBySourceAsync(
        string sourceType,
        string sourceId,
        string scopeKey,
        CancellationToken cancellationToken = default);

    Task DeleteByScopeAsync(string scopeKey, CancellationToken cancellationToken = default);
}
