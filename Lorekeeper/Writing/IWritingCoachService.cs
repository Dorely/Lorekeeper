using Lorekeeper.Models;

namespace Lorekeeper.Writing;

public interface IWritingCoachService
{
    Task<WritingCoachConversation> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WritingCoachMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<WritingCoachTurnUpdate> SendAsync(
        Guid projectId,
        string userText,
        string? currentSampleTitle,
        string? currentSampleBody,
        CancellationToken cancellationToken = default);
    Task ResetAsync(Guid projectId, CancellationToken cancellationToken = default);
}