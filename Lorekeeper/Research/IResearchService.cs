using Lorekeeper.Models;

namespace Lorekeeper.Research;

public interface IResearchService
{
    Task<ResearchConversation> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ResearchMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task<string> GetSystemPromptAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<bool> GetAiChangeApprovalEnabledAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task SetAiChangeApprovalEnabledAsync(Guid projectId, bool enabled, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AiChangeBatch>> ListPendingChangesAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<ResearchActivity> GetActivityAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<ResearchSourceDetail?> GetSourceDetailAsync(Guid projectId, Guid sourceId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<ResearchTurnUpdate> SendAsync(Guid projectId, string userText, CancellationToken cancellationToken = default);
    Task ResetAsync(Guid projectId, CancellationToken cancellationToken = default);
}
