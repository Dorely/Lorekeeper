using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public sealed class EditorRevisionRepository(AppDbContext db) : IEditorRevisionRepository
{
    public Task<EditorRevisionJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        db.EditorRevisionJobs
            .Include(job => job.Sessions.OrderBy(session => session.Order))
            .FirstOrDefaultAsync(job => job.Id == jobId, cancellationToken);

    public Task<EditorRevisionSession?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        db.EditorRevisionSessions
            .Include(session => session.Job)
            .Include(session => session.Messages.OrderBy(message => message.Order))
            .FirstOrDefaultAsync(session => session.Id == sessionId, cancellationToken);

    public Task<List<EditorRevisionJob>> ListCurrentByProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        db.EditorRevisionJobs
            .Include(job => job.Sessions.OrderBy(session => session.Order))
            .Where(job => job.ProjectId == projectId
                && (job.Status == EditorRevisionJobStatus.Queued
                    || job.Status == EditorRevisionJobStatus.Running
                    || job.Status == EditorRevisionJobStatus.Completed
                    || job.Status == EditorRevisionJobStatus.Failed))
            .OrderByDescending(job => job.CreatedAt)
            .Take(10)
            .ToListAsync(cancellationToken);

    public Task<List<EditorRevisionMessage>> LoadSessionMessagesAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        db.EditorRevisionMessages
            .Where(message => message.SessionId == sessionId)
            .OrderBy(message => message.Order)
            .ToListAsync(cancellationToken);

    public async Task<int> GetMaxMessageOrderAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var any = await db.EditorRevisionMessages.AnyAsync(message => message.SessionId == sessionId, cancellationToken);
        if (!any) return -1;
        return await db.EditorRevisionMessages
            .Where(message => message.SessionId == sessionId)
            .MaxAsync(message => message.Order, cancellationToken);
    }

    public async Task AddJobAsync(EditorRevisionJob job, CancellationToken cancellationToken = default) =>
        await db.EditorRevisionJobs.AddAsync(job, cancellationToken);

    public async Task AddSessionAsync(EditorRevisionSession session, CancellationToken cancellationToken = default) =>
        await db.EditorRevisionSessions.AddAsync(session, cancellationToken);

    public async Task AddMessageAsync(EditorRevisionMessage message, CancellationToken cancellationToken = default) =>
        await db.EditorRevisionMessages.AddAsync(message, cancellationToken);

    public void UpdateJob(EditorRevisionJob job) => db.EditorRevisionJobs.Update(job);

    public void UpdateSession(EditorRevisionSession session) => db.EditorRevisionSessions.Update(session);

    public void UpdateMessage(EditorRevisionMessage message) => db.EditorRevisionMessages.Update(message);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}
