using System.Runtime.CompilerServices;
using Lorekeeper.ChatTurns;
using Lorekeeper.Manuscripts;
using Lorekeeper.VersionHistory.Services;

namespace Lorekeeper.EditorChat;

public interface IEditorChatTurnRunner
{
    bool TryStart(Guid projectId, Guid? currentChapterId, Guid? currentCompositionId, EditorContentTarget contentTarget, string userText, IReadOnlyList<ChatTurnImageAttachment> images, ChatTurnModelSnapshot model);
    ChatTurnSnapshot? GetActiveTurn(Guid projectId);
    IChatTurnSubscription<EditorChatTurnUpdate>? Subscribe(Guid projectId);
    void Cancel(Guid projectId);
}

public sealed class EditorChatTurnRunner(
    IServiceScopeFactory scopeFactory,
    ChatTurnRuntime runtime) : IEditorChatTurnRunner
{
    private static ChatTurnKey Key(Guid projectId) => new(projectId, ChatTurnSurface.Editor);

    public bool TryStart(Guid projectId, Guid? currentChapterId, Guid? currentCompositionId, EditorContentTarget contentTarget, string userText, IReadOnlyList<ChatTurnImageAttachment> images, ChatTurnModelSnapshot model) =>
        runtime.TryStart(
            Key(projectId),
            userText,
            cancellationToken => RunAsync(projectId, currentChapterId, currentCompositionId, contentTarget, userText, images.Select(image => image.ImageId).ToList(), model.ProviderId, cancellationToken),
            static (ex, cancelled) => new EditorChatTurnError(cancelled ? "Cancelled." : ex.Message, cancelled),
            static update => update is EditorChatAssistantMessageCompleted or EditorChatTurnError,
            images,
            model);

    public ChatTurnSnapshot? GetActiveTurn(Guid projectId) => runtime.GetActiveTurn(Key(projectId));

    public IChatTurnSubscription<EditorChatTurnUpdate>? Subscribe(Guid projectId) => runtime.Subscribe<EditorChatTurnUpdate>(Key(projectId));

    public void Cancel(Guid projectId) => runtime.Cancel(Key(projectId));

    private async IAsyncEnumerable<EditorChatTurnUpdate> RunAsync(
        Guid projectId,
        Guid? currentChapterId,
        Guid? currentCompositionId,
        EditorContentTarget contentTarget,
        string userText,
        IReadOnlyList<Guid> imageIds,
        int providerId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var chat = scope.ServiceProvider.GetRequiredService<IEditorChatService>();
        var checkpoints = scope.ServiceProvider.GetRequiredService<IAssistantVersionCheckpointService>();
        await foreach (var update in chat.SendAsync(projectId, currentChapterId, currentCompositionId, contentTarget, userText, imageIds, providerId, cancellationToken))
        {
            if (update is EditorChatAssistantMessageCompleted)
                await checkpoints.TryCheckpointAsync(projectId, ChatTurnSurface.Editor, cancellationToken);
            yield return update;
        }
    }
}
