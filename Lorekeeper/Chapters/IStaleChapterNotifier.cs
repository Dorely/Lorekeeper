namespace Lorekeeper.Chapters;

/// <summary>
/// In-process pub/sub of chapters whose vector index has been marked stale.
/// Producer: <see cref="ChapterService"/> when body changes are persisted.
/// Consumer: <see cref="StaleChapterReindexer"/> background service.
/// </summary>
public interface IStaleChapterNotifier
{
    void Notify(Guid chapterId);
    IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken);
}
