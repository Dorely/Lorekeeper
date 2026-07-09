using System.Runtime.CompilerServices;
using Lorekeeper.ChatTurns;

namespace Lorekeeper.Writing;

public interface IWritingCoachTurnRunner
{
    bool TryStart(Guid projectId, string userText, string? currentSampleTitle, string? currentSampleBody);
    ChatTurnSnapshot? GetActiveTurn(Guid projectId);
    IChatTurnSubscription<WritingCoachTurnUpdate>? Subscribe(Guid projectId);
    void Cancel(Guid projectId);
}

public sealed class WritingCoachTurnRunner(IServiceScopeFactory scopeFactory) : IWritingCoachTurnRunner
{
    private readonly ChatTurnRuntime<WritingCoachTurnUpdate> _runtime = new();

    public bool TryStart(Guid projectId, string userText, string? currentSampleTitle, string? currentSampleBody) =>
        _runtime.TryStart(
            projectId,
            userText,
            cancellationToken => RunAsync(projectId, userText, currentSampleTitle, currentSampleBody, cancellationToken),
            static (ex, cancelled) => new WritingCoachTurnError(cancelled ? "Cancelled." : ex.Message, cancelled),
            static update => update is WritingCoachAssistantMessageCompleted or WritingCoachTurnError);

    public ChatTurnSnapshot? GetActiveTurn(Guid projectId) => _runtime.GetActiveTurn(projectId);

    public IChatTurnSubscription<WritingCoachTurnUpdate>? Subscribe(Guid projectId) => _runtime.Subscribe(projectId);

    public void Cancel(Guid projectId) => _runtime.Cancel(projectId);

    private async IAsyncEnumerable<WritingCoachTurnUpdate> RunAsync(
        Guid projectId,
        string userText,
        string? currentSampleTitle,
        string? currentSampleBody,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var coach = scope.ServiceProvider.GetRequiredService<IWritingCoachService>();
        await foreach (var update in coach.SendAsync(projectId, userText, currentSampleTitle, currentSampleBody, cancellationToken))
            yield return update;
    }
}
