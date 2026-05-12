namespace Lorekeeper.ImportExport;

public interface IProjectImportExportService
{
    Task<ProjectExportFile> ExportProjectAsync(Guid projectId, ProjectExportKind kind, CancellationToken cancellationToken = default);
    Task<ProjectExportFile> ExportManuscriptAsync(Guid projectId, ManuscriptExportFormat format, CancellationToken cancellationToken = default);
    Task<ProjectImportJobListItem> CreateImportJobAsync(Guid projectId, string fileName, string contentJson, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectImportJobListItem>> ListImportJobsAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<ProjectImportJobDetailView?> GetImportJobDetailAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task DeleteImportJobAsync(Guid jobId, CancellationToken cancellationToken = default);
}
