using Lorekeeper.Models;
using Lorekeeper.ChatTurns;

namespace Lorekeeper.Persistence.Repositories;

public interface IOutlineConversationRepository : IChatMessageStore<OutlineMessage>
{
    /// <summary>Returns the project's conversation row, or null if it has not been created yet.</summary>
    Task<OutlineConversation?> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>Loads all messages for a conversation in <see cref="OutlineMessage.Order"/> ascending order.</summary>
    Task<List<OutlineMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>Returns the highest <see cref="OutlineMessage.Order"/> in the conversation, or -1 if empty.</summary>
    Task<int> GetMaxOrderAsync(Guid conversationId, CancellationToken cancellationToken = default);

    Task AddConversationAsync(OutlineConversation conversation, CancellationToken cancellationToken = default);

    void RemoveConversation(OutlineConversation conversation);
}
