using Lorekeeper.ChatTurns;
using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface IVoiceConversationRepository : IChatMessageStore<VoiceMessage>
{
    Task<VoiceConversation?> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<List<VoiceMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task<int> GetMaxOrderAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task AddConversationAsync(VoiceConversation conversation, CancellationToken cancellationToken = default);
    Task ResetMessagesAsync(
        VoiceConversation conversation,
        VoiceMessage greeting,
        CancellationToken cancellationToken = default);
    void UpdateSelectedProvider(VoiceConversation conversation);
}
