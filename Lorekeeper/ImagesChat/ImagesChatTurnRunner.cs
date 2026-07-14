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

public sealed class ImagesChatTurnRunner(
    IServiceScopeFactory scopeFactory,
    ChatTurnRuntime runtime) : IImagesChatTurnRunner
{
    private static ChatTurnKey Key(Guid projectId) => new(projectId, ChatTurnSurface.Images);

    public bool TryStart(Guid projectId, string userText) =>
        runtime.TryStart(
            Key(projectId),
            userText,
            cancellationToken => RunAsync(projectId, userText, cancellationToken),
            static (ex, cancelled) => new ImagesChatTurnError(cancelled ? "Cancelled." : ex.Message, cancelled),
            static update => update is ImagesChatAssistantMessageCompleted or ImagesChatTurnError);

    public ChatTurnSnapshot? GetActiveTurn(Guid projectId) => runtime.GetActiveTurn(Key(projectId));

    public IChatTurnSubscription<ImagesChatTurnUpdate>? Subscribe(Guid projectId) => runtime.Subscribe<ImagesChatTurnUpdate>(Key(projectId));

    public void Cancel(Guid projectId) => runtime.Cancel(Key(projectId));

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
