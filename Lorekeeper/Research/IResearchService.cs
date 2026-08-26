using Lorekeeper.Models;
using Lorekeeper.Llm;

namespace Lorekeeper.Research;

public interface IResearchService
{
    Task<ResearchConversation> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ResearchMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task<ChatProviderAvailability> GetChatProviderAvailabilityAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task SetSelectedProviderAsync(Guid projectId, int? providerId, CancellationToken cancellationToken = default);
    Task<string> GetSystemPromptAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<ResearchActivity> GetActivityAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<ResearchSourceDetail?> GetSourceDetailAsync(Guid projectId, Guid sourceId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<ResearchTurnUpdate> SendAsync(Guid projectId, string userText, IReadOnlyList<Guid> imageIds, int providerId, CancellationToken cancellationToken = default);
    Task ResetAsync(Guid projectId, CancellationToken cancellationToken = default);
}
