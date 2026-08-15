using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface IChapterRepository
{
    Task<List<Chapter>> ListByProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<Chapter?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Chapter?> ReloadFromStoreAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Highest <see cref="Chapter.Order"/> within a single act bucket. Pass <c>null</c>
    /// for the project's "unassigned" bucket (chapters with no <see cref="Chapter.ActId"/>).
    /// Returns <c>-1</c> when the bucket is empty.
    /// </summary>
    Task<int> GetMaxOrderAsync(Guid projectId, Guid? actId, CancellationToken cancellationToken = default);

    Task<int> CountByProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task AddAsync(Chapter chapter, CancellationToken cancellationToken = default);
    void Update(Chapter chapter);
    void Remove(Chapter chapter);

    /// <summary>
    /// Rewrites <see cref="Chapter.Order"/> for chapters within a single act bucket.
    /// Pass <c>null</c> for <paramref name="actId"/> to reorder the unassigned bucket.
    /// </summary>
    Task ReorderAsync(Guid projectId, Guid? actId, IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken = default);
}
