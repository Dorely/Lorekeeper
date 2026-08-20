using Lorekeeper.ChatTurns;
using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface IWritingCoachConversationRepository : IChatMessageStore<WritingCoachMessage>
{
    Task<WritingCoachConversation?> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<List<WritingCoachMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task<int> GetMaxOrderAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task AddConversationAsync(WritingCoachConversation conversation, CancellationToken cancellationToken = default);
    void UpdateSelectedProvider(WritingCoachConversation conversation);
    void RemoveConversation(WritingCoachConversation conversation);
}
