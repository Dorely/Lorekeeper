using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface IResearchConversationRepository
{
    Task<ResearchConversation?> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<List<ResearchMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task<int> GetMaxOrderAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task AddConversationAsync(ResearchConversation conversation, CancellationToken cancellationToken = default);
    Task AddMessageAsync(ResearchMessage message, CancellationToken cancellationToken = default);
    void UpdateMessage(ResearchMessage message);
    void RemoveConversation(ResearchConversation conversation);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}