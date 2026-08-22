using System.Runtime.CompilerServices;
using Lorekeeper.ChatTurns;
using Lorekeeper.VersionHistory.Services;

namespace Lorekeeper.Outline;

public interface IOutlineChatTurnRunner
{
    bool TryStart(Guid projectId, string userText, IReadOnlyList<ChatTurnImageAttachment> images, ChatTurnModelSnapshot model);
    ChatTurnSnapshot? GetActiveTurn(Guid projectId);
    IChatTurnSubscription<OutlineTurnUpdate>? Subscribe(Guid projectId);
    void Cancel(Guid projectId);
}

public sealed class OutlineChatTurnRunner(
    IServiceScopeFactory scopeFactory,
    ChatTurnRuntime runtime) : IOutlineChatTurnRunner
{
    private static ChatTurnKey Key(Guid projectId) => new(projectId, ChatTurnSurface.Outline);

    public bool TryStart(Guid projectId, string userText, IReadOnlyList<ChatTurnImageAttachment> images, ChatTurnModelSnapshot model) =>
        runtime.TryStart(
            Key(projectId),
            userText,
            cancellationToken => RunAsync(projectId, userText, images.Select(image => image.ImageId).ToList(), model.ProviderId, cancellationToken),
            static (ex, cancelled) => new TurnError(cancelled ? "Cancelled." : ex.Message, cancelled),
            static update => update is AssistantMessageCompleted or TurnError,
            images,
            model);

    public ChatTurnSnapshot? GetActiveTurn(Guid projectId) => runtime.GetActiveTurn(Key(projectId));

    public IChatTurnSubscription<OutlineTurnUpdate>? Subscribe(Guid projectId) => runtime.Subscribe<OutlineTurnUpdate>(Key(projectId));

    public void Cancel(Guid projectId) => runtime.Cancel(Key(projectId));

    private async IAsyncEnumerable<OutlineTurnUpdate> RunAsync(
        Guid projectId,
        string userText,
        IReadOnlyList<Guid> imageIds,
        int providerId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var chat = scope.ServiceProvider.GetRequiredService<IOutlineCollaborationService>();
        var checkpoints = scope.ServiceProvider.GetRequiredService<IAssistantVersionCheckpointService>();
        await foreach (var update in chat.SendAsync(projectId, userText, imageIds, providerId, cancellationToken))
        {
            if (update is AssistantMessageCompleted)
                await checkpoints.TryCheckpointAsync(projectId, ChatTurnSurface.Outline, cancellationToken);
            yield return update;
        }
    }
}
