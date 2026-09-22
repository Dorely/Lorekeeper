using Lorekeeper.Models;
using Lorekeeper.Llm;

namespace Lorekeeper.Writing;

public interface IVoiceService
{
    Task<VoiceConversation> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VoiceMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task<ChatProviderAvailability> GetChatProviderAvailabilityAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task SetSelectedProviderAsync(Guid projectId, int? providerId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<VoiceTurnUpdate> SendAsync(
        Guid projectId,
        string userText,
        string? currentSampleTitle,
        string? currentSampleBody,
        IReadOnlyList<Guid> imageIds,
        int providerId,
        CancellationToken cancellationToken = default);
    Task ResetAsync(Guid projectId, CancellationToken cancellationToken = default);
}
