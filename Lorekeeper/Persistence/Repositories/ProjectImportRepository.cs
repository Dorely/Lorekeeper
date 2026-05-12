using Lorekeeper.ImportExport;
using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public sealed class ProjectImportRepository(AppDbContext db) : IProjectImportRepository
{
    public Task<List<ProjectImportJob>> ListJobsByProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        db.ProjectImportJobs
            .Where(job => job.ProjectId == projectId)
            .OrderByDescending(job => job.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<List<ProjectImportJobListItem>> ListJobSummariesByProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        db.ProjectImportJobs
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
        db.ProjectImportJobs.FirstOrDefaultAsync(job => job.Id == jobId, cancellationToken);

    public Task<ProjectImportJob?> GetJobDetailAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        db.ProjectImportJobs
            .Include(job => job.ReportItems.OrderBy(item => item.CreatedAt))
            .FirstOrDefaultAsync(job => job.Id == jobId, cancellationToken);

    public async Task<ProjectImportJobDetailView?> GetJobDetailViewAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await db.ProjectImportJobs
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

        var reportItems = await db.ProjectImportReportItems
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

    public Task<List<ProjectImportJob>> ListQueuedJobsAsync(CancellationToken cancellationToken = default) =>
        db.ProjectImportJobs
            .Where(job => job.Status == ProjectImportJobStatus.Queued)
            .OrderBy(job => job.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<List<ProjectImportJob>> ListInterruptedJobsAsync(CancellationToken cancellationToken = default) =>
        db.ProjectImportJobs
            .Where(job => job.Status == ProjectImportJobStatus.Running)
            .OrderBy(job => job.UpdatedAt)
            .ToListAsync(cancellationToken);

    public Task<List<ProjectImportReportItem>> ListReportItemsAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        db.ProjectImportReportItems
            .Where(item => item.JobId == jobId)
            .OrderBy(item => item.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task AddJobAsync(ProjectImportJob job, CancellationToken cancellationToken = default) =>
        await db.ProjectImportJobs.AddAsync(job, cancellationToken);

    public async Task AddReportItemAsync(ProjectImportReportItem item, CancellationToken cancellationToken = default) =>
        await db.ProjectImportReportItems.AddAsync(item, cancellationToken);

    public void UpdateJob(ProjectImportJob job) => db.ProjectImportJobs.Update(job);

    public void UpdateReportItem(ProjectImportReportItem item) => db.ProjectImportReportItems.Update(item);

    public void RemoveJob(ProjectImportJob job) => db.ProjectImportJobs.Remove(job);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}
