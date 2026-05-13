using Lorekeeper.Models;

namespace Lorekeeper.Research;

public interface IResearchService
{
    Task<ResearchConversation> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ResearchMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<ResearchTurnUpdate> SendAsync(Guid projectId, string userText, CancellationToken cancellationToken = default);
    Task ResetAsync(Guid projectId, CancellationToken cancellationToken = default);
}