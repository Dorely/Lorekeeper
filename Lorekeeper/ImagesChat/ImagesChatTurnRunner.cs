using System.Runtime.CompilerServices;
using Lorekeeper.ChatTurns;

namespace Lorekeeper.ImagesChat;

public interface IImagesChatTurnRunner
{
    bool TryStart(Guid projectId, string userText);
    ChatTurnSnapshot? GetActiveTurn(Guid projectId);
    IChatTurnSubscription<ImagesChatTurnUpdate>? Subscribe(Guid projectId);
    void Cancel(Guid projectId);
}

public sealed class ImagesChatTurnRunner(IServiceScopeFactory scopeFactory) : IImagesChatTurnRunner
{
    private readonly ChatTurnRuntime<ImagesChatTurnUpdate> _runtime = new();

    public bool TryStart(Guid projectId, string userText) =>
        _runtime.TryStart(
            projectId,
            userText,
            cancellationToken => RunAsync(projectId, userText, cancellationToken),
            static (ex, cancelled) => new ImagesChatTurnError(cancelled ? "Cancelled." : ex.Message, cancelled),
            static update => update is ImagesChatAssistantMessageCompleted or ImagesChatTurnError);

    public ChatTurnSnapshot? GetActiveTurn(Guid projectId) => _runtime.GetActiveTurn(projectId);

    public IChatTurnSubscription<ImagesChatTurnUpdate>? Subscribe(Guid projectId) => _runtime.Subscribe(projectId);

    public void Cancel(Guid projectId) => _runtime.Cancel(projectId);

    private async IAsyncEnumerable<ImagesChatTurnUpdate> RunAsync(
        Guid projectId,
        string userText,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var chat = scope.ServiceProvider.GetRequiredService<IImagesChatService>();
        await foreach (var update in chat.SendAsync(projectId, userText, cancellationToken))
            yield return update;
    }
}
