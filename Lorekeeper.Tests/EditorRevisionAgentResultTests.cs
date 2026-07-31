using Lorekeeper.EditorChat;
using Lorekeeper.Models;

namespace Lorekeeper.Tests;

public sealed class EditorRevisionAgentResultTests
{
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
            ErrorMessage: null);

        var json = EditorRevisionAgentService.SerializeRunResult(result);

        Assert.Contains("Drafted chapter", json, StringComparison.Ordinal);
        Assert.DoesNotContain("operationsJson", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rawResponse", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("instructions", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(json.Length < 1_000);
    }
}
