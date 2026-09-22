using Lorekeeper.ChatTurns;
using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface IWorldConversationRepository : IChatMessageStore<WorldMessage>
{
    Task<WorldConversation?> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<List<WorldMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task<int> GetMaxOrderAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task AddConversationAsync(WorldConversation conversation, CancellationToken cancellationToken = default);
    Task ResetMessagesAsync(
        WorldConversation conversation,
        WorldMessage greeting,
        CancellationToken cancellationToken = default);
    void UpdateSelectedProvider(WorldConversation conversation);
}
