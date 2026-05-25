using Lorekeeper.Models;

namespace Lorekeeper.Outline;

public interface IAiChangeApprovalService
{
    Task<IReadOnlyList<AiChangeBatch>> ListPendingBatchesAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<AiChangeBatch?> GetBatchAsync(Guid batchId, CancellationToken cancellationToken = default);
    Task SaveReviewDraftAsync(Guid changeId, string? draftAfterJson, string? reviewStateJson, CancellationToken cancellationToken = default);
    Task ClearReviewDraftAsync(Guid changeId, CancellationToken cancellationToken = default);
    Task PrepareChapterBodyLineReviewAsync(Guid projectId, Guid chapterId, CancellationToken cancellationToken = default);
    Task ResolveChapterBodyReviewLineAsync(Guid projectId, Guid chapterId, ChapterBodyReviewLineResolution request, CancellationToken cancellationToken = default);
    Task ApplyChangeAsync(Guid changeId, CancellationToken cancellationToken = default);
    Task ApplyChangesAsync(IReadOnlyCollection<Guid> changeIds, CancellationToken cancellationToken = default);
    Task RejectChangeAsync(Guid changeId, string? message, CancellationToken cancellationToken = default);
    Task ApplyBatchAsync(Guid batchId, CancellationToken cancellationToken = default);
    Task RejectBatchAsync(Guid batchId, string? message, CancellationToken cancellationToken = default);
}

public sealed record ChapterBodyReviewLineResolution(
    Guid ChangeId,
    int? PairId,
    int? OldLineNumber,
    int? NewLineNumber,
    string? OldText,
    string? NewText,
    ChapterBodyReviewLineAction Action,
    string? EditedText = null,
    string? RejectionMessage = null);

public enum ChapterBodyReviewLineAction
{
    Keep,
    Reject,
    Edit,
}
