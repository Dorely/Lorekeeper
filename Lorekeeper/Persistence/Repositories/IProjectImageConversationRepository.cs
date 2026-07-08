using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface IProjectImageConversationRepository
{
    Task<ProjectImageConversation?> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<List<ProjectImageMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task<int> GetMaxOrderAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task AddConversationAsync(ProjectImageConversation conversation, CancellationToken cancellationToken = default);
    Task AddMessageAsync(ProjectImageMessage message, CancellationToken cancellationToken = default);
    Task AddMessageVisualsAsync(IEnumerable<ProjectImageMessageVisual> visuals, CancellationToken cancellationToken = default);
    void UpdateMessage(ProjectImageMessage message);
    void RemoveConversation(ProjectImageConversation conversation);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
