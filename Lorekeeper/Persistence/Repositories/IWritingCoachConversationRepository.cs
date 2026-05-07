using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface IWritingCoachConversationRepository
{
    Task<WritingCoachConversation?> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<List<WritingCoachMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task<int> GetMaxOrderAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task AddConversationAsync(WritingCoachConversation conversation, CancellationToken cancellationToken = default);
    Task AddMessageAsync(WritingCoachMessage message, CancellationToken cancellationToken = default);
    void UpdateMessage(WritingCoachMessage message);
    void RemoveConversation(WritingCoachConversation conversation);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}