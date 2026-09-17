namespace Lorekeeper.ImportExport;

public interface IProjectImportExportService
{
    // Internal compatibility capture for the v1-v31 import adapter and the
    // archive traversal. It is not a user-facing JSON export path.
    Task<ProjectExportFile> ExportProjectAsync(Guid projectId, ProjectExportKind kind, CancellationToken cancellationToken = default);
    Task<ProjectArchiveDocumentCapture> CaptureArchiveDocumentAsync(Guid projectId, ProjectExportKind kind, CancellationToken cancellationToken = default);
    Task<ProjectImportJobListItem> CreateImportJobAsync(Guid projectId, string fileName, Stream content, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectImportJobListItem>> ListImportJobsAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<ProjectImportJobDetailView?> GetImportJobDetailAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task DeleteImportJobAsync(Guid jobId, CancellationToken cancellationToken = default);
}
