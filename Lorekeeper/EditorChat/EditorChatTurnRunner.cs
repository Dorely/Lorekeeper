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

public sealed class EditorChatTurnRunner(IServiceScopeFactory scopeFactory) : IEditorChatTurnRunner
{
    private readonly ChatTurnRuntime<EditorChatTurnUpdate> _runtime = new();

    public bool TryStart(Guid projectId, Guid? currentChapterId, string userText) =>
        _runtime.TryStart(
            projectId,
            userText,
            cancellationToken => RunAsync(projectId, currentChapterId, userText, cancellationToken),
            static (ex, cancelled) => new EditorChatTurnError(cancelled ? "Cancelled." : ex.Message, cancelled),
            static update => update is EditorChatAssistantMessageCompleted or EditorChatTurnError);

    public ChatTurnSnapshot? GetActiveTurn(Guid projectId) => _runtime.GetActiveTurn(projectId);

    public IChatTurnSubscription<EditorChatTurnUpdate>? Subscribe(Guid projectId) => _runtime.Subscribe(projectId);

    public void Cancel(Guid projectId) => _runtime.Cancel(projectId);

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
