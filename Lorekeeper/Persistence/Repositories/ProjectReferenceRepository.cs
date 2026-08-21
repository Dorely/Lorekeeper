using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public sealed class ProjectReferenceRepository(AppDatabaseReadOperation operation) : IProjectReferenceRepository
{
    public Task<List<ProjectReference>> ListByReferencingProjectAsync(
        Guid referencingProjectId,
        CancellationToken cancellationToken = default) =>
        operation.Db.ProjectReferences
            .AsNoTracking()
            .Include(reference => reference.ReferencedProject)
            .Where(reference => reference.ReferencingProjectId == referencingProjectId)
            .OrderBy(reference => reference.ReferencedProject.Name)
            .ThenBy(reference => reference.ReferencedProjectId)
            .ToListAsync(cancellationToken);

    public Task<List<ProjectReference>> ListByReferencedProjectAsync(
        Guid referencedProjectId,
        CancellationToken cancellationToken = default) =>
        operation.Db.ProjectReferences
            .Include(reference => reference.ReferencingProject)
            .Where(reference => reference.ReferencedProjectId == referencedProjectId)
            .OrderBy(reference => reference.ReferencingProject.Name)
            .ThenBy(reference => reference.ReferencingProjectId)
            .ToListAsync(cancellationToken);

    public Task<ProjectReference?> GetAsync(
        Guid referencingProjectId,
        Guid referencedProjectId,
        CancellationToken cancellationToken = default) =>
        operation.Db.ProjectReferences.FirstOrDefaultAsync(
            reference => reference.ReferencingProjectId == referencingProjectId
                && reference.ReferencedProjectId == referencedProjectId,
            cancellationToken);

    public async Task AddAsync(ProjectReference reference, CancellationToken cancellationToken = default) =>
        await operation.Db.ProjectReferences.AddAsync(reference, cancellationToken);

    public void Remove(ProjectReference reference) => operation.Db.MarkDeleted(reference);
}
