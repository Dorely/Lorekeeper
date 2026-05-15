using Lorekeeper.Models;
using Lorekeeper.Outline;

namespace Lorekeeper.EditorChat;

public interface IEditorChatService
{
    Task<EditorConversation> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EditorMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task<bool> GetAiChangeApprovalEnabledAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task SetAiChangeApprovalEnabledAsync(Guid projectId, bool enabled, CancellationToken cancellationToken = default);
    Task<EditorContestSettings> GetContestSettingsAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task SetContestModeEnabledAsync(Guid projectId, bool enabled, CancellationToken cancellationToken = default);
    Task SetContestProviderAsync(Guid projectId, int slot, int? providerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AiChangeBatch>> ListPendingChangesAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ContestBatch>> ListCurrentContestBatchesAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task StageContestCandidateAsync(Guid candidateId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<EditorChatTurnUpdate> SendAsync(Guid projectId, Guid? currentChapterId, string userText, CancellationToken cancellationToken = default);
    Task ResetAsync(Guid projectId, CancellationToken cancellationToken = default);
}

public sealed class EditorChatContext(
    Guid projectId,
    Guid? currentChapterId,
    Action onMutated,
    bool reviewEdits,
    bool autoPinReadEntities,
    OutlineToolStagingContext? outlineStaging,
    EditorChatChangeStagingContext? editorStaging)
{
    private EditorContestStartRequest? _contestRequest;

    public Guid ProjectId { get; } = projectId;
    public Guid? CurrentChapterId { get; } = currentChapterId;
    public Action OnMutated { get; } = onMutated;
    public bool ReviewEdits { get; } = reviewEdits;
    public bool AutoPinReadEntities { get; } = autoPinReadEntities;
    public OutlineToolStagingContext? OutlineStaging { get; } = outlineStaging;
    public EditorChatChangeStagingContext? EditorStaging { get; } = editorStaging;

    public void BeginToolCall(Guid assistantMessageId, string toolCallId, string toolName, string argumentsJson)
    {
        OutlineStaging?.BeginToolCall(assistantMessageId, toolCallId, toolName, argumentsJson);
        EditorStaging?.BeginToolCall(assistantMessageId, toolCallId, toolName, argumentsJson);
    }

    public void RequestContest(EditorContestStartRequest request)
    {
        if (_contestRequest is not null)
            throw new InvalidOperationException("start_contest can only be called once per editor chat turn.");
        _contestRequest = request;
    }

    public bool TryTakeContestRequest(out EditorContestStartRequest request)
    {
        if (_contestRequest is null)
        {
            request = null!;
            return false;
        }

        request = _contestRequest;
        _contestRequest = null;
        return true;
    }
}
