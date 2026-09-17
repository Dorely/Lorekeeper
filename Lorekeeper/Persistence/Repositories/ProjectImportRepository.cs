using Lorekeeper.ImportExport;
using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public sealed class ProjectImportRepository(AppDatabaseReadOperation operation) : IProjectImportRepository
{
    public Task<List<ProjectImportJob>> ListJobsByProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        operation.Db.ProjectImportJobs
            .Where(job => job.ProjectId == projectId)
            .OrderByDescending(job => job.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<List<ProjectImportJobListItem>> ListJobSummariesByProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        operation.Db.ProjectImportJobs
            .AsNoTracking()
            .Where(job => job.ProjectId == projectId)
            .OrderByDescending(job => job.CreatedAt)
            .Select(job => new ProjectImportJobListItem(
                job.Id,
                job.ProjectId,
                job.FileName,
                job.ExportKind,
                job.Status,
                job.TotalSteps,
                job.CompletedSteps,
                job.CreatedNodeCount,
                job.MergedNodeCount,
                job.CreatedEdgeCount,
                job.MergedEdgeCount,
                job.CreatedActCount,
                job.CreatedChapterCount,
                job.CreatedBeatCount,
                job.WarningCount,
                job.CurrentMessage,
                job.ErrorMessage,
                job.CreatedAt,
                job.UpdatedAt))
            .ToListAsync(cancellationToken);

    public Task<ProjectImportJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        operation.Db.ProjectImportJobs.FirstOrDefaultAsync(job => job.Id == jobId, cancellationToken);

    public Task<ProjectImportJob?> GetJobDetailAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        operation.Db.ProjectImportJobs
            .Include(job => job.ReportItems.OrderBy(item => item.CreatedAt))
            .FirstOrDefaultAsync(job => job.Id == jobId, cancellationToken);

    public async Task<ProjectImportJobDetailView?> GetJobDetailViewAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await operation.Db.ProjectImportJobs
            .AsNoTracking()
            .Where(job => job.Id == jobId)
            .Select(job => new
            {
                job.Id,
                job.ProjectId,
                job.FileName,
                job.FormatId,
                job.FormatVersion,
                job.ExportKind,
                job.Status,
                job.TotalSteps,
                job.CompletedSteps,
                job.CreatedNodeCount,
                job.MergedNodeCount,
                job.CreatedEdgeCount,
                job.MergedEdgeCount,
                job.CreatedActCount,
                job.CreatedChapterCount,
                job.CreatedBeatCount,
                job.WarningCount,
                job.CurrentMessage,
                job.ErrorMessage,
                job.CreatedAt,
                job.UpdatedAt,
                job.StartedAt,
                job.CompletedAt,
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (job is null) return null;

        var reportItems = await operation.Db.ProjectImportReportItems
            .AsNoTracking()
            .Where(item => item.JobId == jobId)
            .OrderBy(item => item.CreatedAt)
            .Select(item => new ProjectImportReportItemView(
                item.Id,
                item.Kind,
                item.Status,
                item.Title,
                item.Summary,
                item.Notes,
                item.ResourceType,
                item.ResourceKey,
                item.EntityId,
                item.GraphNodeId,
                item.GraphEdgeId,
                item.PayloadJson,
                item.ErrorMessage,
                item.CreatedAt,
                item.UpdatedAt))
            .ToListAsync(cancellationToken);

        return new ProjectImportJobDetailView(
            job.Id,
            job.ProjectId,
            job.FileName,
            job.FormatId,
            job.FormatVersion,
            job.ExportKind,
            job.Status,
            job.TotalSteps,
            job.CompletedSteps,
            job.CreatedNodeCount,
            job.MergedNodeCount,
            job.CreatedEdgeCount,
            job.MergedEdgeCount,
            job.CreatedActCount,
            job.CreatedChapterCount,
            job.CreatedBeatCount,
            job.WarningCount,
            job.CurrentMessage,
            job.ErrorMessage,
            job.CreatedAt,
            job.UpdatedAt,
            job.StartedAt,
            job.CompletedAt,
            reportItems);
    }

    public Task<List<ProjectImportJob>> ListRunnableJobsAsync(CancellationToken cancellationToken = default) =>
        operation.Db.ProjectImportJobs
            .Where(job => job.Status == ProjectImportJobStatus.Staged
                || job.Status == ProjectImportJobStatus.Validated
                || job.Status == ProjectImportJobStatus.Committed
                || job.Status == ProjectImportJobStatus.Indexing)
            .OrderBy(job => job.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<List<ProjectImportJob>> ListInterruptedJobsAsync(CancellationToken cancellationToken = default) =>
        operation.Db.ProjectImportJobs
            .Where(job => job.Status == ProjectImportJobStatus.Applying
                || job.Status == ProjectImportJobStatus.Committed
                || job.Status == ProjectImportJobStatus.Indexing)
            .OrderBy(job => job.UpdatedAt)
            .ToListAsync(cancellationToken);

    public Task<List<ProjectImportReportItem>> ListReportItemsAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        operation.Db.ProjectImportReportItems
            .Where(item => item.JobId == jobId)
            .OrderBy(item => item.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task AddJobAsync(ProjectImportJob job, CancellationToken cancellationToken = default) =>
        await operation.Db.ProjectImportJobs.AddAsync(job, cancellationToken);

    public async Task AddReportItemAsync(ProjectImportReportItem item, CancellationToken cancellationToken = default) =>
        await operation.Db.ProjectImportReportItems.AddAsync(item, cancellationToken);

    public void UpdateJob(ProjectImportJob job) => operation.Db.MarkModified(job);

    public void UpdateReportItem(ProjectImportReportItem item) => operation.Db.MarkModified(item);

    public void RemoveJob(ProjectImportJob job) => operation.Db.MarkDeleted(job);
}
