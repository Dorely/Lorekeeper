using System.Runtime.CompilerServices;
using Lorekeeper.ChatTurns;
using Lorekeeper.VersionHistory.Services;

namespace Lorekeeper.Research;

public interface IWorldChatTurnRunner
{
    bool TryStart(Guid projectId, string userText, IReadOnlyList<ChatTurnImageAttachment> images, ChatTurnModelSnapshot model);
    ChatTurnSnapshot? GetActiveTurn(Guid projectId);
    IChatTurnSubscription<WorldTurnUpdate>? Subscribe(Guid projectId);
    void Cancel(Guid projectId);
}

public sealed class WorldChatTurnRunner(
    IServiceScopeFactory scopeFactory,
    ChatTurnRuntime runtime) : IWorldChatTurnRunner
{
    private static ChatTurnKey Key(Guid projectId) => new(projectId, ChatTurnSurface.World);

    public bool TryStart(Guid projectId, string userText, IReadOnlyList<ChatTurnImageAttachment> images, ChatTurnModelSnapshot model) =>
        runtime.TryStart(
            Key(projectId),
            userText,
            cancellationToken => RunAsync(projectId, userText, images.Select(image => image.ImageId).ToList(), model.ProviderId, cancellationToken),
            static (ex, cancelled) => new WorldTurnError(cancelled ? "Cancelled." : ex.Message, cancelled),
            static update => update is WorldAssistantMessageCompleted or WorldTurnError,
            images,
            model);

    public ChatTurnSnapshot? GetActiveTurn(Guid projectId) => runtime.GetActiveTurn(Key(projectId));

    public IChatTurnSubscription<WorldTurnUpdate>? Subscribe(Guid projectId) => runtime.Subscribe<WorldTurnUpdate>(Key(projectId));

    public void Cancel(Guid projectId) => runtime.Cancel(Key(projectId));

    private async IAsyncEnumerable<WorldTurnUpdate> RunAsync(
        Guid projectId,
        string userText,
        IReadOnlyList<Guid> imageIds,
        int providerId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var chat = scope.ServiceProvider.GetRequiredService<IWorldService>();
        var checkpoints = scope.ServiceProvider.GetRequiredService<IAssistantVersionCheckpointService>();
        var mutated = false;
        await foreach (var update in chat.SendAsync(projectId, userText, imageIds, providerId, cancellationToken))
        {
            if (update is WorldMutated)
                mutated = true;
            if (update is WorldAssistantMessageCompleted && mutated)
                await checkpoints.TryCheckpointAsync(projectId, ChatTurnSurface.World, cancellationToken);
            yield return update;
        }
    }
}
