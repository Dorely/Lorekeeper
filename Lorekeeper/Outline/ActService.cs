using Lorekeeper.Context;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.Outline;

public class ActService(
IAppDatabaseOperationFactory database, IOutlineGraphSync outlineGraphSync, IContextIndexingService contextIndexing) : IActService
{
    public async Task<IReadOnlyList<Act>> ListAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var repo = databaseOperation.Repositories.Acts;
        return await repo.ListByProjectAsync(projectId, cancellationToken);
    }
    public async Task<Act?> GetAsync(Guid actId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var repo = databaseOperation.Repositories.Acts;
        return await repo.GetByIdAsync(actId, cancellationToken);
    }
    public async Task<Act> CreateAsync(Guid projectId, string? title = null, string? synopsis = null, Guid? id = null, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var repo = databaseOperation.Repositories.Acts;
        var projects = databaseOperation.Repositories.Projects;
        var project = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");

        var nextOrder = await repo.GetMaxOrderAsync(projectId, cancellationToken) + 1;
        var resolvedTitle = string.IsNullOrWhiteSpace(title)
            ? $"Act {nextOrder + 1}"
            : title.Trim();

        var act = new Act
        {
            Id = id ?? Guid.NewGuid(),
            ProjectId = projectId,
            Title = resolvedTitle,
            Synopsis = synopsis ?? string.Empty,
            Order = nextOrder,
        };

        await repo.AddAsync(act, cancellationToken);
        project.UpdatedAt = DateTime.UtcNow;
        projects.Update(project);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        await outlineGraphSync.EnsureActAsync(act, cancellationToken);
        await contextIndexing.ReindexActAsync(act.Id, cancellationToken);
        return act;
    }

    public async Task<Act> UpdateAsync(Guid actId, string? title = null, string? synopsis = null, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var repo = databaseOperation.Repositories.Acts;
        var projects = databaseOperation.Repositories.Projects;
        var act = await repo.GetByIdAsync(actId, cancellationToken)
            ?? throw new InvalidOperationException($"Act {actId} not found.");

        if (title is not null && title != act.Title) act.Title = title;
        if (synopsis is not null && synopsis != act.Synopsis) act.Synopsis = synopsis;

        act.UpdatedAt = DateTime.UtcNow;
        repo.Update(act);

        var project = await projects.GetByIdAsync(act.ProjectId, cancellationToken);
        if (project is not null)
        {
            project.UpdatedAt = DateTime.UtcNow;
            projects.Update(project);
        }

        await databaseOperation.SaveChangesAsync(cancellationToken);
        await outlineGraphSync.EnsureActAsync(act, cancellationToken);
        await contextIndexing.ReindexActAsync(act.Id, cancellationToken);
        return act;
    }

    public async Task DeleteAsync(Guid actId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var repo = databaseOperation.Repositories.Acts;
        var projects = databaseOperation.Repositories.Projects;
        var act = await repo.GetByIdAsync(actId, cancellationToken);
        if (act is null) return;
        var projectId = act.ProjectId;

        await contextIndexing.DeleteActAsync(projectId, act.Id, cancellationToken);
        repo.Remove(act);

        var project = await projects.GetByIdAsync(projectId, cancellationToken);
        if (project is not null)
        {
            project.UpdatedAt = DateTime.UtcNow;
            projects.Update(project);
        }

        await databaseOperation.SaveChangesAsync(cancellationToken);
        await outlineGraphSync.RemoveActAsync(projectId, act.Id, cancellationToken);
        await outlineGraphSync.RepairProjectAsync(projectId, cancellationToken);
    }

    public async Task ReorderAsync(Guid projectId, IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var repo = databaseOperation.Repositories.Acts;
        var projects = databaseOperation.Repositories.Projects;
        await repo.ReorderAsync(projectId, orderedIds, cancellationToken);

        var project = await projects.GetByIdAsync(projectId, cancellationToken);
        if (project is not null)
        {
            project.UpdatedAt = DateTime.UtcNow;
            projects.Update(project);
        }

        await databaseOperation.SaveChangesAsync(cancellationToken);
        await outlineGraphSync.RepairProjectAsync(projectId, cancellationToken);
        foreach (var actId in orderedIds)
            await contextIndexing.ReindexActAsync(actId, cancellationToken);
    }
}
