using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface IProjectReferenceRepository
{
    Task<List<ProjectReference>> ListByReferencingProjectAsync(
        Guid referencingProjectId,
        CancellationToken cancellationToken = default);

    Task<List<ProjectReference>> ListByReferencedProjectAsync(
        Guid referencedProjectId,
        CancellationToken cancellationToken = default);

    Task<ProjectReference?> GetAsync(
        Guid referencingProjectId,
        Guid referencedRepositoryId,
        Guid referencedProjectId,
        CancellationToken cancellationToken = default);

    Task<ProjectReference?> GetByIdAsync(
        Guid referenceId,
        CancellationToken cancellationToken = default);

    Task AddAsync(ProjectReference reference, CancellationToken cancellationToken = default);

    void Update(ProjectReference reference);

    void Remove(ProjectReference reference);
}
