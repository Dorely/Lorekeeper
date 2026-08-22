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
            .Include(reference => reference.ResolvedProject)
            .Where(reference => reference.ReferencingProjectId == referencingProjectId)
            .OrderBy(reference => reference.ReferencedProjectName)
            .ThenBy(reference => reference.ReferencedProjectId)
            .ToListAsync(cancellationToken);

    public Task<List<ProjectReference>> ListByReferencedProjectAsync(
        Guid referencedProjectId,
        CancellationToken cancellationToken = default) =>
        operation.Db.ProjectReferences
            .Include(reference => reference.ReferencingProject)
            .Where(reference => reference.ResolvedProjectId == referencedProjectId)
            .OrderBy(reference => reference.ReferencingProject.Name)
            .ThenBy(reference => reference.ReferencingProjectId)
            .ToListAsync(cancellationToken);

    public Task<ProjectReference?> GetAsync(
        Guid referencingProjectId,
        Guid referencedRepositoryId,
        Guid referencedProjectId,
        CancellationToken cancellationToken = default) =>
        operation.Db.ProjectReferences.FirstOrDefaultAsync(
            reference => reference.ReferencingProjectId == referencingProjectId
                && reference.ReferencedRepositoryId == referencedRepositoryId
                && reference.ReferencedProjectId == referencedProjectId,
            cancellationToken);

    public Task<ProjectReference?> GetByIdAsync(
        Guid referenceId,
        CancellationToken cancellationToken = default) =>
        operation.Db.ProjectReferences.FirstOrDefaultAsync(
            reference => reference.Id == referenceId,
            cancellationToken);

    public async Task AddAsync(ProjectReference reference, CancellationToken cancellationToken = default) =>
        await operation.Db.ProjectReferences.AddAsync(reference, cancellationToken);

    public void Update(ProjectReference reference) => operation.Db.MarkModified(reference);

    public void Remove(ProjectReference reference) => operation.Db.MarkDeleted(reference);
}
