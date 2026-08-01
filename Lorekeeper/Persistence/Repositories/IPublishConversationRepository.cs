using Lorekeeper.ChatTurns;
using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface IPublishConversationRepository : IChatMessageStore<PublishMessage>
{
    Task<PublishConversation?> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<List<PublishMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task<int> GetMaxOrderAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task AddConversationAsync(PublishConversation conversation, CancellationToken cancellationToken = default);
    void RemoveConversation(PublishConversation conversation);
}
