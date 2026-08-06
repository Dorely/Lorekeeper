using Lorekeeper.EditorChat;
using Lorekeeper.Models;

namespace Lorekeeper.Tests;

public sealed class EditorRevisionAgentResultTests
{
    [Fact]
    public async Task PendingProgressReadCompletesBeforeEnumeratorDisposal()
    {
        var notifier = new EditorRevisionJobNotifier();
        await using var subscription = notifier.Subscribe(Guid.NewGuid());
        using var cancellation = new CancellationTokenSource();
        var enumerator = subscription
            .ReadAllAsync(cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        var pendingRead = enumerator.MoveNextAsync().AsTask();

        await EditorChatService.CompleteRevisionUpdateReadAsync(cancellation, pendingRead);
        await enumerator.DisposeAsync();

        Assert.True(pendingRead.IsCompleted);
    }

    [Fact]
    public void CoordinatorResultExcludesPersistedWorkerPayloads()
    {
        var result = new EditorRevisionAgentRunResult(
            Guid.NewGuid(),
            EditorRevisionJobStatus.Completed,
            [new EditorRevisionSessionResult(
                Guid.NewGuid(),
                0,
                Guid.NewGuid(),
                "Chapter",
                EditorRevisionSessionStatus.Completed,
                "Drafted chapter",
                ErrorMessage: null)],
            ErrorMessage: null,
            PendingChangeIds: []);

        var json = EditorRevisionAgentService.SerializeRunResult(result);

        Assert.Contains("Drafted chapter", json, StringComparison.Ordinal);
        Assert.DoesNotContain("operationsJson", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rawResponse", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("instructions", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(json.Length < 1_000);
    }
}
