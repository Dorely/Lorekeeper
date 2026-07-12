using Lorekeeper.Models;

namespace Lorekeeper.Chapters;

/// <summary>
/// Three-state act assignment for <see cref="IChapterService.UpdateAsync"/>:
/// omit the parameter to leave the chapter's act unchanged; pass <c>new(null)</c>
/// to move it to the unassigned bucket; pass <c>new(actId)</c> to move into that act.
/// Distinct from a plain <c>Guid?</c> so callers can distinguish "unspecified" from "set to null".
/// </summary>
public readonly record struct ChapterActAssignment(Guid? Value);

public interface IChapterService
{
    Task<IReadOnlyList<Chapter>> ListAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<Chapter?> GetAsync(Guid chapterId, CancellationToken cancellationToken = default);
    Task<Chapter?> ReloadFromStoreAsync(Guid chapterId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a chapter. Pass <paramref name="actId"/> to assign to a specific act,
    /// or <c>null</c> to land in the project's "Unassigned" bucket. <see cref="Models.Chapter.Order"/>
    /// is auto-assigned to the end of the chosen bucket.
    /// </summary>
    Task<Chapter> CreateAsync(Guid projectId, Guid? actId = null, string? title = null, string? synopsis = null, Guid? id = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates one or more fields on a chapter. Pass <paramref name="actId"/> as a wrapper
    /// (with <c>HasValue == true</c>) to MOVE the chapter into a different act bucket
    /// (or to unassigned via <c>new(null)</c>). Omit it (default) to leave the act unchanged.
    /// </summary>
    Task<Chapter> UpdateAsync(
        Guid chapterId,
        string? title = null,
        string? body = null,
        string? synopsis = null,
        ChapterActAssignment? actId = null,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid chapterId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rewrites <see cref="Models.Chapter.Order"/> within a single act bucket.
    /// Pass <paramref name="actId"/> = <c>null</c> for the project's unassigned bucket.
    /// </summary>
    Task ReorderAsync(Guid projectId, Guid? actId, IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken = default);

    /// <summary>Re-embed and replace all vector chunks for the chapter. Updates the
    /// chapter's <see cref="Models.VectorIndexState"/> based on success/failure.</summary>
    Task ReindexAsync(Guid chapterId, CancellationToken cancellationToken = default);
}
