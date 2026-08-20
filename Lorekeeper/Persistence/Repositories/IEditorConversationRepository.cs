using Lorekeeper.ChatTurns;
using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface IEditorConversationRepository : IChatMessageStore<EditorMessage>
{
    Task<EditorConversation?> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<List<EditorMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task<List<EditorMessage>> LoadTranscriptMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(Guid conversationId, CancellationToken cancellationToken = default);

    Task<int> GetMaxOrderAsync(Guid conversationId, CancellationToken cancellationToken = default);

    Task AddConversationAsync(EditorConversation conversation, CancellationToken cancellationToken = default);
    void UpdateSelectedProvider(EditorConversation conversation);
    Task AddMessageVisualsAsync(IEnumerable<EditorMessageVisual> visuals, CancellationToken cancellationToken = default);
    void RemoveConversation(EditorConversation conversation);
}
