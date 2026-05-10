using System.Text.Json.Serialization;
using Lorekeeper.Models;

namespace Lorekeeper.EditorChat;

public sealed record EditorContestSettings(
    bool Enabled,
    int? ProviderSlot1Id,
    int? ProviderSlot2Id,
    int? ProviderSlot3Id);

public sealed record EditorContestStartRequest(
    Guid ChapterId,
    string OperationKind,
    string UserGoal,
    string MutationInstructions,
    string TargetRangesJson);

public sealed record ContestTurnSnapshot(
    string UserMessage,
    IReadOnlyList<ContestContextItemSnapshot> ContextItems,
    IReadOnlyList<ContestToolTrace> ToolTraces,
    string AssistantText);

public sealed record ContestContextItemSnapshot(
    string Kind,
    string Label,
    string Body);

public sealed record ContestToolTrace(
    string CallId,
    string ToolName,
    string ArgumentsJson,
    string? Result,
    string? Error);

public abstract record EditorContestRunUpdate;

public sealed record EditorContestStarted(Guid BatchId) : EditorContestRunUpdate;

public sealed record EditorContestCandidateUpdated(Guid BatchId, Guid CandidateId, ContestCandidateStatus Status) : EditorContestRunUpdate;

public sealed record EditorContestCompleted(Guid BatchId, ContestBatchStatus Status) : EditorContestRunUpdate;

public sealed record ContestCandidateResponse(
    string Summary,
    IReadOnlyList<ContestChapterMutation> Mutations,
    string? Notes = null);

public sealed record ContestChapterMutation(
    string MutationKind,
    int? StartLine,
    int? EndLine,
    string ReplacementText,
    string? Rationale = null);

public sealed record ContestCandidateProvider(
    int Id,
    string Name,
    string ModelName);

public sealed class ContestTurnCollector(
    string userMessage,
    IReadOnlyList<ContestContextItemSnapshot> contextItems)
{
    private readonly List<ContestToolTrace> _toolTraces = [];
    private readonly Dictionary<string, PendingToolTrace> _pendingTools = new(StringComparer.Ordinal);
    private readonly System.Text.StringBuilder _assistantText = new();

    public void AppendAssistantText(string text)
    {
        if (!string.IsNullOrEmpty(text))
            _assistantText.Append(text);
    }

    public void ToolStarted(string callId, string toolName, string argumentsJson)
    {
        _pendingTools[callId] = new PendingToolTrace(toolName, argumentsJson);
    }

    public void ToolCompleted(string callId, string? result, string? error)
    {
        if (!_pendingTools.Remove(callId, out var pending)) return;
        _toolTraces.Add(new ContestToolTrace(callId, pending.ToolName, pending.ArgumentsJson, result, error));
    }

    public ContestTurnSnapshot Snapshot() =>
        new(userMessage, contextItems, _toolTraces, _assistantText.ToString());

    private sealed record PendingToolTrace(string ToolName, string ArgumentsJson);
}
