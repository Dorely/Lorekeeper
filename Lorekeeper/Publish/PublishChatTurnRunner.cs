using System.Runtime.CompilerServices;
using Lorekeeper.ChatTurns;

namespace Lorekeeper.Publish;

public interface IPublishChatTurnRunner
{
    bool TryStart(
        Guid projectId,
        Guid? selectedEditionId,
        string userText,
        IReadOnlyList<ChatTurnImageAttachment> images);
    ChatTurnSnapshot? GetActiveTurn(Guid projectId);
    IChatTurnSubscription<PublishTurnUpdate>? Subscribe(Guid projectId);
    void Cancel(Guid projectId);
}

public sealed class PublishChatTurnRunner(
    IServiceScopeFactory scopeFactory,
    ChatTurnRuntime runtime) : IPublishChatTurnRunner
{
    private static ChatTurnKey Key(Guid projectId) => new(projectId, ChatTurnSurface.Publish);

    public bool TryStart(
        Guid projectId,
        Guid? selectedEditionId,
        string userText,
        IReadOnlyList<ChatTurnImageAttachment> images) =>
        runtime.TryStart(
            Key(projectId),
            userText,
            cancellationToken => RunAsync(
                projectId,
                selectedEditionId,
                userText,
                images.Select(image => image.ImageId).ToList(),
                cancellationToken),
            static (exception, cancelled) => new PublishTurnError(cancelled ? "Cancelled." : exception.Message, cancelled),
            static update => update is PublishAssistantMessageCompleted or PublishTurnError,
            images);

    public ChatTurnSnapshot? GetActiveTurn(Guid projectId) => runtime.GetActiveTurn(Key(projectId));
    public IChatTurnSubscription<PublishTurnUpdate>? Subscribe(Guid projectId) => runtime.Subscribe<PublishTurnUpdate>(Key(projectId));
    public void Cancel(Guid projectId) => runtime.Cancel(Key(projectId));

    private async IAsyncEnumerable<PublishTurnUpdate> RunAsync(
        Guid projectId,
        Guid? selectedEditionId,
        string userText,
        IReadOnlyList<Guid> imageIds,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var chat = scope.ServiceProvider.GetRequiredService<IPublishChatService>();
        await foreach (var update in chat.SendAsync(
            projectId,
            selectedEditionId,
            userText,
            imageIds,
            cancellationToken))
        {
            yield return update;
        }
    }
}
