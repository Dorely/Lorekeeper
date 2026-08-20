using Lorekeeper.ChatTurns;
using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface IProjectImageConversationRepository : IChatMessageStore<ProjectImageMessage>
{
    Task<ProjectImageConversation?> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<List<ProjectImageMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task<int> GetMaxOrderAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task AddConversationAsync(ProjectImageConversation conversation, CancellationToken cancellationToken = default);
    void UpdateSelectedProvider(ProjectImageConversation conversation);
    Task AddMessageVisualsAsync(IEnumerable<ProjectImageMessageVisual> visuals, CancellationToken cancellationToken = default);
    void RemoveConversation(ProjectImageConversation conversation);
}
