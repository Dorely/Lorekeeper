using System.Runtime.CompilerServices;
using Lorekeeper.ChatTurns;

namespace Lorekeeper.Writing;

public interface IWritingCoachTurnRunner
{
    bool TryStart(Guid projectId, string userText, string? currentSampleTitle, string? currentSampleBody, IReadOnlyList<ChatTurnImageAttachment> images, ChatTurnModelSnapshot model);
    ChatTurnSnapshot? GetActiveTurn(Guid projectId);
    IChatTurnSubscription<WritingCoachTurnUpdate>? Subscribe(Guid projectId);
    void Cancel(Guid projectId);
}

public sealed class WritingCoachTurnRunner(
    IServiceScopeFactory scopeFactory,
    ChatTurnRuntime runtime) : IWritingCoachTurnRunner
{
    private static ChatTurnKey Key(Guid projectId) => new(projectId, ChatTurnSurface.WritingCoach);

    public bool TryStart(Guid projectId, string userText, string? currentSampleTitle, string? currentSampleBody, IReadOnlyList<ChatTurnImageAttachment> images, ChatTurnModelSnapshot model) =>
        runtime.TryStart(
            Key(projectId),
            userText,
            cancellationToken => RunAsync(projectId, userText, currentSampleTitle, currentSampleBody, images.Select(image => image.ImageId).ToList(), model.ProviderId, cancellationToken),
            static (ex, cancelled) => new WritingCoachTurnError(cancelled ? "Cancelled." : ex.Message, cancelled),
            static update => update is WritingCoachAssistantMessageCompleted or WritingCoachTurnError,
            images,
            model);

    public ChatTurnSnapshot? GetActiveTurn(Guid projectId) => runtime.GetActiveTurn(Key(projectId));

    public IChatTurnSubscription<WritingCoachTurnUpdate>? Subscribe(Guid projectId) => runtime.Subscribe<WritingCoachTurnUpdate>(Key(projectId));

    public void Cancel(Guid projectId) => runtime.Cancel(Key(projectId));

    private async IAsyncEnumerable<WritingCoachTurnUpdate> RunAsync(
        Guid projectId,
        string userText,
        string? currentSampleTitle,
        string? currentSampleBody,
        IReadOnlyList<Guid> imageIds,
        int providerId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var coach = scope.ServiceProvider.GetRequiredService<IWritingCoachService>();
        await foreach (var update in coach.SendAsync(projectId, userText, currentSampleTitle, currentSampleBody, imageIds, providerId, cancellationToken))
            yield return update;
    }
}
