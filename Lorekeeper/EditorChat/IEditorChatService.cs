using Lorekeeper.Models;
using Lorekeeper.Outline;

namespace Lorekeeper.EditorChat;

public interface IEditorChatService
{
    Task<EditorConversation> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EditorMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task<bool> GetAiChangeApprovalEnabledAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task SetAiChangeApprovalEnabledAsync(Guid projectId, bool enabled, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AiChangeBatch>> ListPendingChangesAsync(Guid projectId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<EditorChatTurnUpdate> SendAsync(Guid projectId, Guid? currentChapterId, string userText, CancellationToken cancellationToken = default);
    Task ResetAsync(Guid projectId, CancellationToken cancellationToken = default);
}

public sealed class EditorChatContext(
    Guid projectId,
    Guid? currentChapterId,
    Action onMutated,
    bool reviewEdits,
    OutlineToolStagingContext? outlineStaging,
    EditorChatChangeStagingContext? editorStaging)
{
    public Guid ProjectId { get; } = projectId;
    public Guid? CurrentChapterId { get; } = currentChapterId;
    public Action OnMutated { get; } = onMutated;
    public bool ReviewEdits { get; } = reviewEdits;
    public OutlineToolStagingContext? OutlineStaging { get; } = outlineStaging;
    public EditorChatChangeStagingContext? EditorStaging { get; } = editorStaging;

    public void BeginToolCall(Guid assistantMessageId, string toolCallId, string toolName, string argumentsJson)
    {
        OutlineStaging?.BeginToolCall(assistantMessageId, toolCallId, toolName, argumentsJson);
        EditorStaging?.BeginToolCall(assistantMessageId, toolCallId, toolName, argumentsJson);
    }
}