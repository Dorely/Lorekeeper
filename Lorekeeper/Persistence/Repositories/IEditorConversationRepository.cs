using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface IEditorConversationRepository
{
    Task<EditorConversation?> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<List<EditorMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);

    Task<int> GetMaxOrderAsync(Guid conversationId, CancellationToken cancellationToken = default);

    Task AddConversationAsync(EditorConversation conversation, CancellationToken cancellationToken = default);
    Task AddMessageAsync(EditorMessage message, CancellationToken cancellationToken = default);
    void UpdateMessage(EditorMessage message);
    void RemoveConversation(EditorConversation conversation);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}