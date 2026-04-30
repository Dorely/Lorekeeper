using Lorekeeper.Models;

namespace Lorekeeper.Chapters;

public interface IChapterService
{
    Task<IReadOnlyList<Chapter>> ListAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<Chapter?> GetAsync(Guid chapterId, CancellationToken cancellationToken = default);
    Task<Chapter> CreateAsync(Guid projectId, string? title = null, CancellationToken cancellationToken = default);
    Task<Chapter> UpdateAsync(Guid chapterId, string? title = null, string? body = null, string? synopsis = null, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid chapterId, CancellationToken cancellationToken = default);
    Task ReorderAsync(Guid projectId, IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken = default);

    /// <summary>Re-embed and replace all vector chunks for the chapter. Updates the
    /// chapter's <see cref="Models.VectorIndexState"/> based on success/failure.</summary>
    Task ReindexAsync(Guid chapterId, CancellationToken cancellationToken = default);
}
