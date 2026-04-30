using Lorekeeper.Models;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.Outline;

public class ActService(
    IActRepository repo,
    IProjectRepository projects) : IActService
{
    public async Task<IReadOnlyList<Act>> ListAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await repo.ListByProjectAsync(projectId, cancellationToken);

    public Task<Act?> GetAsync(Guid actId, CancellationToken cancellationToken = default) =>
        repo.GetByIdAsync(actId, cancellationToken);

    public async Task<Act> CreateAsync(Guid projectId, string? title = null, string? synopsis = null, CancellationToken cancellationToken = default)
    {
        var project = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");

        var nextOrder = await repo.GetMaxOrderAsync(projectId, cancellationToken) + 1;
        var resolvedTitle = string.IsNullOrWhiteSpace(title)
            ? $"Act {nextOrder + 1}"
            : title.Trim();

        var act = new Act
        {
            ProjectId = projectId,
            Title = resolvedTitle,
            Synopsis = synopsis ?? string.Empty,
            Order = nextOrder,
        };

        await repo.AddAsync(act, cancellationToken);
        project.UpdatedAt = DateTime.UtcNow;
        projects.Update(project);
        await repo.SaveChangesAsync(cancellationToken);
        return act;
    }

    public async Task<Act> UpdateAsync(Guid actId, string? title = null, string? synopsis = null, CancellationToken cancellationToken = default)
    {
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

        await repo.SaveChangesAsync(cancellationToken);
        return act;
    }

    public async Task DeleteAsync(Guid actId, CancellationToken cancellationToken = default)
    {
        var act = await repo.GetByIdAsync(actId, cancellationToken);
        if (act is null) return;

        repo.Remove(act);

        var project = await projects.GetByIdAsync(act.ProjectId, cancellationToken);
        if (project is not null)
        {
            project.UpdatedAt = DateTime.UtcNow;
            projects.Update(project);
        }

        await repo.SaveChangesAsync(cancellationToken);
    }

    public async Task ReorderAsync(Guid projectId, IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken = default)
    {
        await repo.ReorderAsync(projectId, orderedIds, cancellationToken);

        var project = await projects.GetByIdAsync(projectId, cancellationToken);
        if (project is not null)
        {
            project.UpdatedAt = DateTime.UtcNow;
            projects.Update(project);
        }

        await repo.SaveChangesAsync(cancellationToken);
    }
}
