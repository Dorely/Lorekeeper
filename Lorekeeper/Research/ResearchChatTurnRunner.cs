using System.Runtime.CompilerServices;
using Lorekeeper.ChatTurns;

namespace Lorekeeper.Research;

public interface IResearchChatTurnRunner
{
    bool TryStart(Guid projectId, string userText);
    ChatTurnSnapshot? GetActiveTurn(Guid projectId);
    IChatTurnSubscription<ResearchTurnUpdate>? Subscribe(Guid projectId);
    void Cancel(Guid projectId);
}

public sealed class ResearchChatTurnRunner(IServiceScopeFactory scopeFactory) : IResearchChatTurnRunner
{
    private readonly ChatTurnRuntime<ResearchTurnUpdate> _runtime = new();

    public bool TryStart(Guid projectId, string userText) =>
        _runtime.TryStart(
            projectId,
            userText,
            cancellationToken => RunAsync(projectId, userText, cancellationToken),
            static (ex, cancelled) => new ResearchTurnError(cancelled ? "Cancelled." : ex.Message, cancelled),
            static update => update is ResearchAssistantMessageCompleted or ResearchTurnError);

    public ChatTurnSnapshot? GetActiveTurn(Guid projectId) => _runtime.GetActiveTurn(projectId);

    public IChatTurnSubscription<ResearchTurnUpdate>? Subscribe(Guid projectId) => _runtime.Subscribe(projectId);

    public void Cancel(Guid projectId) => _runtime.Cancel(projectId);

    private async IAsyncEnumerable<ResearchTurnUpdate> RunAsync(
        Guid projectId,
        string userText,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var chat = scope.ServiceProvider.GetRequiredService<IResearchService>();
        await foreach (var update in chat.SendAsync(projectId, userText, cancellationToken))
            yield return update;
    }
}
