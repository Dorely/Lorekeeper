using Lorekeeper.Models;

namespace Lorekeeper.Outline;

public interface IActService
{
    Task<IReadOnlyList<Act>> ListAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<Act?> GetAsync(Guid actId, CancellationToken cancellationToken = default);
    Task<Act> CreateAsync(Guid projectId, string? title = null, string? synopsis = null, Guid? id = null, CancellationToken cancellationToken = default);
    Task<Act> UpdateAsync(Guid actId, string? title = null, string? synopsis = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the act. Owned chapters are NOT deleted; their <see cref="Chapter.ActId"/>
    /// is cleared (FK <c>OnDelete.SetNull</c>) so they fall back into the project-level
    /// "Unassigned" bucket.
    /// </summary>
    Task DeleteAsync(Guid actId, CancellationToken cancellationToken = default);

    Task ReorderAsync(Guid projectId, IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken = default);
}
