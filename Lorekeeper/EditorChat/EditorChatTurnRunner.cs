using System.Runtime.CompilerServices;
using Lorekeeper.ChatTurns;

namespace Lorekeeper.EditorChat;

public interface IEditorChatTurnRunner
{
    bool TryStart(Guid projectId, Guid? currentChapterId, string userText);
    ChatTurnSnapshot? GetActiveTurn(Guid projectId);
    IChatTurnSubscription<EditorChatTurnUpdate>? Subscribe(Guid projectId);
    void Cancel(Guid projectId);
}

public sealed class EditorChatTurnRunner(
    IServiceScopeFactory scopeFactory,
    ChatTurnRuntime runtime) : IEditorChatTurnRunner
{
    private static ChatTurnKey Key(Guid projectId) => new(projectId, ChatTurnSurface.Editor);

    public bool TryStart(Guid projectId, Guid? currentChapterId, string userText) =>
        runtime.TryStart(
            Key(projectId),
            userText,
            cancellationToken => RunAsync(projectId, currentChapterId, userText, cancellationToken),
            static (ex, cancelled) => new EditorChatTurnError(cancelled ? "Cancelled." : ex.Message, cancelled),
            static update => update is EditorChatAssistantMessageCompleted or EditorChatTurnError);

    public ChatTurnSnapshot? GetActiveTurn(Guid projectId) => runtime.GetActiveTurn(Key(projectId));

    public IChatTurnSubscription<EditorChatTurnUpdate>? Subscribe(Guid projectId) => runtime.Subscribe<EditorChatTurnUpdate>(Key(projectId));

    public void Cancel(Guid projectId) => runtime.Cancel(Key(projectId));

    private async IAsyncEnumerable<EditorChatTurnUpdate> RunAsync(
        Guid projectId,
        Guid? currentChapterId,
        string userText,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var chat = scope.ServiceProvider.GetRequiredService<IEditorChatService>();
        await foreach (var update in chat.SendAsync(projectId, currentChapterId, userText, cancellationToken))
            yield return update;
    }
}
