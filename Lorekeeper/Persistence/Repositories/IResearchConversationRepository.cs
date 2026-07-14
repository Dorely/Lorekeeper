using Lorekeeper.Models;
using Lorekeeper.ChatTurns;

namespace Lorekeeper.Persistence.Repositories;

public interface IResearchConversationRepository : IChatMessageStore<ResearchMessage>
{
    Task<ResearchConversation?> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<List<ResearchMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task<int> GetMaxOrderAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task AddConversationAsync(ResearchConversation conversation, CancellationToken cancellationToken = default);
    void RemoveConversation(ResearchConversation conversation);
}
