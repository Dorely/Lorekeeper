using Lorekeeper.Context;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.VersionHistory.Services;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Projects;

public interface IWorldBriefService
{
    Task<WorldBrief> GetAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<WorldBrief> UpdateAsync(Guid projectId, long expectedRevision, string content, CancellationToken cancellationToken = default);
}

public sealed class WorldBriefService(IAppDatabaseOperationFactory database, IContextIndexingService indexing,
    ProjectVersionHistoryUiEvents? historyEvents = null) : IWorldBriefService
{
    public async Task<WorldBrief> GetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        if (!await operation.Db.Projects.AnyAsync(project => project.Id == projectId, cancellationToken))
            throw new InvalidOperationException("Project not found.");
        return await operation.Db.WorldBriefs.SingleOrDefaultAsync(brief => brief.ProjectId == projectId, cancellationToken)
            ?? new WorldBrief { ProjectId = projectId };
    }

    public async Task<WorldBrief> UpdateAsync(Guid projectId, long expectedRevision, string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        WorldBrief brief;
        await using (var operation = await database.OpenWriteAsync(projectId, cancellationToken))
        {
            var project = await operation.Db.Projects.SingleOrDefaultAsync(value => value.Id == projectId, cancellationToken)
                ?? throw new InvalidOperationException("Project not found.");
            var existing = await operation.Db.WorldBriefs.SingleOrDefaultAsync(value => value.ProjectId == projectId, cancellationToken);
            brief = existing ?? new WorldBrief { ProjectId = projectId };
            if (brief.Revision != expectedRevision)
                throw new InvalidOperationException("The World Brief changed. Read it again before saving; your draft has been retained.");
            if (brief.Content == content) return brief;
            brief.Content = content;
            brief.Revision++;
            project.UpdatedAt = DateTime.UtcNow;
            if (existing is null) operation.Db.WorldBriefs.Add(brief);
            await operation.SaveChangesAsync(cancellationToken);
        }
        historyEvents?.PublishReviewStateChanged(projectId);
        await indexing.ReindexProjectProfileAsync(projectId, cancellationToken);
        return brief;
    }
}
