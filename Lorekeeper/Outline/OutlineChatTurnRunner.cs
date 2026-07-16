using System.Runtime.CompilerServices;
using Lorekeeper.ChatTurns;

namespace Lorekeeper.Outline;

public interface IOutlineChatTurnRunner
{
    bool TryStart(Guid projectId, string userText, IReadOnlyList<ChatTurnImageAttachment> images);
    ChatTurnSnapshot? GetActiveTurn(Guid projectId);
    IChatTurnSubscription<OutlineTurnUpdate>? Subscribe(Guid projectId);
    void Cancel(Guid projectId);
}

public sealed class OutlineChatTurnRunner(
    IServiceScopeFactory scopeFactory,
    ChatTurnRuntime runtime) : IOutlineChatTurnRunner
{
    private static ChatTurnKey Key(Guid projectId) => new(projectId, ChatTurnSurface.Outline);

    public bool TryStart(Guid projectId, string userText, IReadOnlyList<ChatTurnImageAttachment> images) =>
        runtime.TryStart(
            Key(projectId),
            userText,
            cancellationToken => RunAsync(projectId, userText, images.Select(image => image.ImageId).ToList(), cancellationToken),
            static (ex, cancelled) => new TurnError(cancelled ? "Cancelled." : ex.Message, cancelled),
            static update => update is AssistantMessageCompleted or TurnError,
            images);

    public ChatTurnSnapshot? GetActiveTurn(Guid projectId) => runtime.GetActiveTurn(Key(projectId));

    public IChatTurnSubscription<OutlineTurnUpdate>? Subscribe(Guid projectId) => runtime.Subscribe<OutlineTurnUpdate>(Key(projectId));

    public void Cancel(Guid projectId) => runtime.Cancel(Key(projectId));

    private async IAsyncEnumerable<OutlineTurnUpdate> RunAsync(
        Guid projectId,
        string userText,
        IReadOnlyList<Guid> imageIds,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var chat = scope.ServiceProvider.GetRequiredService<IOutlineCollaborationService>();
        await foreach (var update in chat.SendAsync(projectId, userText, imageIds, cancellationToken))
            yield return update;
    }
}
