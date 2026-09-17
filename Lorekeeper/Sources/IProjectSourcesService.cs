using Lorekeeper.Models;

namespace Lorekeeper.Sources;

public interface IProjectSourcesService
{
    Task<ProjectSourcesWorkspace> GetWorkspaceAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<ProjectSourceReading?> GetReadingAsync(Guid projectId, Guid sourceId, Guid? blockId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectSourceSearchHit>> SearchAsync(Guid projectId, string query, CancellationToken cancellationToken = default);
    Task<ProjectSourceOriginalDownload?> GetOriginalDownloadAsync(Guid projectId, Guid sourceId, CancellationToken cancellationToken = default);
    Task CopyOriginalAsync(ProjectSourceOriginalDownload download, Stream destination, CancellationToken cancellationToken = default);
    Task<ProjectSourcePdfPage?> RenderPdfPageAsync(Guid projectId, Guid sourceId, int pageNumber, int maxEdge, CancellationToken cancellationToken = default);
    Task<SourceDeletionUsageReport?> GetDeletionUsageAsync(Guid projectId, Guid sourceId, CancellationToken cancellationToken = default);
    Task ReextractSourceAsync(Guid projectId, Guid sourceId, CancellationToken cancellationToken = default);
    Task DetachBibliographicRecordAsync(Guid projectId, Guid bibliographicRecordId, CancellationToken cancellationToken = default);
    Task DeleteSourceAsync(Guid projectId, Guid sourceId, CancellationToken cancellationToken = default);
}
