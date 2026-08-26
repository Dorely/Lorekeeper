using System.Runtime.CompilerServices;
using Lorekeeper.ChatTurns;
using Lorekeeper.VersionHistory.Services;

namespace Lorekeeper.Publish;

public interface IPublishChatTurnRunner
{
    bool TryStart(
        Guid projectId,
        Guid? selectedEditionId,
        PublishAssistantWorkspaceContext? workspaceContext,
        string userText,
        IReadOnlyList<ChatTurnImageAttachment> images,
        ChatTurnModelSnapshot model);
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
        PublishAssistantWorkspaceContext? workspaceContext,
        string userText,
        IReadOnlyList<ChatTurnImageAttachment> images,
        ChatTurnModelSnapshot model) =>
        runtime.TryStart(
            Key(projectId),
            userText,
            cancellationToken => RunAsync(
                projectId,
                selectedEditionId,
                workspaceContext,
                userText,
                images.Select(image => image.ImageId).ToList(),
                model.ProviderId,
                cancellationToken),
            static (exception, cancelled) => new PublishTurnError(cancelled ? "Cancelled." : exception.Message, cancelled),
            static update => update is PublishAssistantMessageCompleted or PublishTurnError,
            images,
            model);

    public ChatTurnSnapshot? GetActiveTurn(Guid projectId) => runtime.GetActiveTurn(Key(projectId));
    public IChatTurnSubscription<PublishTurnUpdate>? Subscribe(Guid projectId) => runtime.Subscribe<PublishTurnUpdate>(Key(projectId));
    public void Cancel(Guid projectId) => runtime.Cancel(Key(projectId));

    private async IAsyncEnumerable<PublishTurnUpdate> RunAsync(
        Guid projectId,
        Guid? selectedEditionId,
        PublishAssistantWorkspaceContext? workspaceContext,
        string userText,
        IReadOnlyList<Guid> imageIds,
        int providerId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var chat = scope.ServiceProvider.GetRequiredService<IPublishChatService>();
        var checkpoints = scope.ServiceProvider.GetRequiredService<IAssistantVersionCheckpointService>();
        var mutated = false;
        await foreach (var update in chat.SendAsync(
            projectId,
            selectedEditionId,
            workspaceContext,
            userText,
            imageIds,
            providerId,
            cancellationToken))
        {
            if (update is PublishWorkspaceMutated)
                mutated = true;
            if (update is PublishAssistantMessageCompleted && mutated)
                await checkpoints.TryCheckpointAsync(projectId, ChatTurnSurface.Publish, cancellationToken);
            yield return update;
        }
    }
}
