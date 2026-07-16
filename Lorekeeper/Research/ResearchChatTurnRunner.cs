using System.Runtime.CompilerServices;
using Lorekeeper.ChatTurns;

namespace Lorekeeper.Research;

public interface IResearchChatTurnRunner
{
    bool TryStart(Guid projectId, string userText, IReadOnlyList<ChatTurnImageAttachment> images);
    ChatTurnSnapshot? GetActiveTurn(Guid projectId);
    IChatTurnSubscription<ResearchTurnUpdate>? Subscribe(Guid projectId);
    void Cancel(Guid projectId);
}

public sealed class ResearchChatTurnRunner(
    IServiceScopeFactory scopeFactory,
    ChatTurnRuntime runtime) : IResearchChatTurnRunner
{
    private static ChatTurnKey Key(Guid projectId) => new(projectId, ChatTurnSurface.Research);

    public bool TryStart(Guid projectId, string userText, IReadOnlyList<ChatTurnImageAttachment> images) =>
        runtime.TryStart(
            Key(projectId),
            userText,
            cancellationToken => RunAsync(projectId, userText, images.Select(image => image.ImageId).ToList(), cancellationToken),
            static (ex, cancelled) => new ResearchTurnError(cancelled ? "Cancelled." : ex.Message, cancelled),
            static update => update is ResearchAssistantMessageCompleted or ResearchTurnError,
            images);

    public ChatTurnSnapshot? GetActiveTurn(Guid projectId) => runtime.GetActiveTurn(Key(projectId));

    public IChatTurnSubscription<ResearchTurnUpdate>? Subscribe(Guid projectId) => runtime.Subscribe<ResearchTurnUpdate>(Key(projectId));

    public void Cancel(Guid projectId) => runtime.Cancel(Key(projectId));

    private async IAsyncEnumerable<ResearchTurnUpdate> RunAsync(
        Guid projectId,
        string userText,
        IReadOnlyList<Guid> imageIds,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var chat = scope.ServiceProvider.GetRequiredService<IResearchService>();
        await foreach (var update in chat.SendAsync(projectId, userText, imageIds, cancellationToken))
            yield return update;
    }
}
