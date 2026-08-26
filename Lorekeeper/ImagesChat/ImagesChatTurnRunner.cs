using System.Runtime.CompilerServices;
using Lorekeeper.ChatTurns;
using Lorekeeper.VersionHistory.Services;

namespace Lorekeeper.ImagesChat;

public interface IImagesChatTurnRunner
{
    bool TryStart(Guid projectId, string userText, IReadOnlyList<ChatTurnImageAttachment> images, ChatTurnModelSnapshot model);
    ChatTurnSnapshot? GetActiveTurn(Guid projectId);
    IChatTurnSubscription<ImagesChatTurnUpdate>? Subscribe(Guid projectId);
    void Cancel(Guid projectId);
}

public sealed class ImagesChatTurnRunner(
    IServiceScopeFactory scopeFactory,
    ChatTurnRuntime runtime) : IImagesChatTurnRunner
{
    private static ChatTurnKey Key(Guid projectId) => new(projectId, ChatTurnSurface.Images);

    public bool TryStart(Guid projectId, string userText, IReadOnlyList<ChatTurnImageAttachment> images, ChatTurnModelSnapshot model) =>
        runtime.TryStart(
            Key(projectId),
            userText,
            cancellationToken => RunAsync(projectId, userText, images.Select(image => image.ImageId).ToList(), model.ProviderId, cancellationToken),
            static (ex, cancelled) => new ImagesChatTurnError(cancelled ? "Cancelled." : ex.Message, cancelled),
            static update => update is ImagesChatAssistantMessageCompleted or ImagesChatTurnError,
            images,
            model);

    public ChatTurnSnapshot? GetActiveTurn(Guid projectId) => runtime.GetActiveTurn(Key(projectId));

    public IChatTurnSubscription<ImagesChatTurnUpdate>? Subscribe(Guid projectId) => runtime.Subscribe<ImagesChatTurnUpdate>(Key(projectId));

    public void Cancel(Guid projectId) => runtime.Cancel(Key(projectId));

    private async IAsyncEnumerable<ImagesChatTurnUpdate> RunAsync(
        Guid projectId,
        string userText,
        IReadOnlyList<Guid> imageIds,
        int providerId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var chat = scope.ServiceProvider.GetRequiredService<IImagesChatService>();
        var checkpoints = scope.ServiceProvider.GetRequiredService<IAssistantVersionCheckpointService>();
        var mutated = false;
        await foreach (var update in chat.SendAsync(projectId, userText, imageIds, providerId, cancellationToken))
        {
            if (update is ImagesChatMutated)
                mutated = true;
            if (update is ImagesChatAssistantMessageCompleted && mutated)
                await checkpoints.TryCheckpointAsync(projectId, ChatTurnSurface.Images, cancellationToken);
            yield return update;
        }
    }
}
