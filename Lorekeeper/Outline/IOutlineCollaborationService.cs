using Lorekeeper.Models;
using Lorekeeper.Llm;

namespace Lorekeeper.Outline;

/// <summary>
/// Per-project, persisted, multi-turn collaborative outline-building chat. Drives a
/// streaming tool-call loop where the LLM mutates the project's acts/chapters/metadata
/// directly through tools while conversing with the user.
/// </summary>
public interface IOutlineCollaborationService
{
    /// <summary>
    /// Returns the project's conversation, creating it on first call (and seeding the
    /// initial assistant greeting). Always loads the full message history.
    /// </summary>
    Task<OutlineConversation> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the persisted message log for a conversation, ordered ascending.
    /// </summary>
    Task<IReadOnlyList<OutlineMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task<ChatProviderAvailability> GetChatProviderAvailabilityAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task SetSelectedProviderAsync(Guid projectId, int? providerId, CancellationToken cancellationToken = default);
    Task<string> GetSystemPromptAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<bool> GetAiChangeApprovalEnabledAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task SetAiChangeApprovalEnabledAsync(Guid projectId, bool enabled, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AiChangeBatch>> ListPendingChangesAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task ApplyAiChangeAsync(Guid changeId, CancellationToken cancellationToken = default);
    Task RejectAiChangeAsync(Guid changeId, string? message, CancellationToken cancellationToken = default);
    Task ApplyAiChangeBatchAsync(Guid batchId, CancellationToken cancellationToken = default);
    Task RejectAiChangeBatchAsync(Guid batchId, string? message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a user message and yields incremental updates as the assistant responds and
    /// invokes tools. Persists user / assistant / tool messages as the turn progresses.
    /// </summary>
    IAsyncEnumerable<OutlineTurnUpdate> SendAsync(Guid projectId, string userText, IReadOnlyList<Guid> imageIds, int providerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the project's transcript with its initial greeting while retaining the
    /// conversation row and its selected model.
    /// </summary>
    Task ResetAsync(Guid projectId, CancellationToken cancellationToken = default);
}
