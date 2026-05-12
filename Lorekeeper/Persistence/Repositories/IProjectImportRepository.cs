using Lorekeeper.ImportExport;
using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface IProjectImportRepository
{
    Task<List<ProjectImportJob>> ListJobsByProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<List<ProjectImportJobListItem>> ListJobSummariesByProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<ProjectImportJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<ProjectImportJob?> GetJobDetailAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<ProjectImportJobDetailView?> GetJobDetailViewAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<List<ProjectImportJob>> ListQueuedJobsAsync(CancellationToken cancellationToken = default);
    Task<List<ProjectImportJob>> ListInterruptedJobsAsync(CancellationToken cancellationToken = default);
    Task<List<ProjectImportReportItem>> ListReportItemsAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task AddJobAsync(ProjectImportJob job, CancellationToken cancellationToken = default);
    Task AddReportItemAsync(ProjectImportReportItem item, CancellationToken cancellationToken = default);
    void UpdateJob(ProjectImportJob job);
    void UpdateReportItem(ProjectImportReportItem item);
    void RemoveJob(ProjectImportJob job);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
