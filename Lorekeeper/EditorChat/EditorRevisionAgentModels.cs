using Lorekeeper.Models;
using Lorekeeper.Manuscripts;

namespace Lorekeeper.EditorChat;

public sealed class EditorRevisionAgentAssignmentInput
{
    public Guid ChapterId { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string Instructions { get; set; } = string.Empty;
}

public sealed record EditorRevisionAgentRunRequest(
    Guid ProjectId,
    Guid ConversationId,
    Guid? AssistantMessageId,
    string ToolCallId,
    string ArgumentsJson,
    EditorContentTarget ContentTarget,
    IReadOnlyList<EditorRevisionAgentAssignmentInput> Chapters);

public sealed record EditorRevisionAgentRunResult(
    Guid JobId,
    EditorRevisionJobStatus Status,
    IReadOnlyList<EditorRevisionSessionResult> Sessions,
    string? ErrorMessage,
    IReadOnlyList<Guid> PendingChangeIds);

public sealed record EditorRevisionJobProgress(
    Guid JobId,
    EditorRevisionJobStatus Status,
    int TotalSessions,
    int QueuedCount,
    int RunningCount,
    int CompletedCount,
    int FailedCount,
    int InvalidCount,
    int CancelledCount,
    DateTime UpdatedAt,
    IReadOnlyList<EditorRevisionSessionProgress> Sessions);

public sealed record EditorRevisionSessionProgress(
    Guid SessionId,
    int Order,
    Guid ChapterId,
    string ChapterTitle,
    EditorRevisionSessionStatus Status,
    string Summary,
    string? ErrorMessage,
    DateTime UpdatedAt);

public sealed record EditorRevisionSessionResult(
    Guid SessionId,
    int Order,
    Guid ChapterId,
    string ChapterTitle,
    EditorRevisionSessionStatus Status,
    string Summary,
    string? ErrorMessage);

public sealed record EditorRevisionJobDetail(
    Guid Id,
    Guid ProjectId,
    Guid ConversationId,
    EditorRevisionJobStatus Status,
    string? ErrorMessage,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? CompletedAt,
    IReadOnlyList<EditorRevisionSessionDetail> Sessions);

public sealed record EditorRevisionSessionDetail(
    Guid Id,
    int Order,
    Guid ChapterId,
    string ChapterTitle,
    string Reason,
    string Instructions,
    EditorRevisionSessionStatus Status,
    string Summary,
    string Rationale,
    string OperationFormat,
    string OperationsJson,
    string Notes,
    string? ErrorMessage,
    double? DurationMs,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? CompletedAt);

public sealed record EditorRevisionSessionTranscript(
    EditorRevisionSessionDetail Session,
    IReadOnlyList<EditorRevisionTranscriptMessage> Messages);

public sealed record EditorRevisionTranscriptMessage(
    Guid Id,
    string Role,
    string Content,
    string ToolCallsJson,
    string? ToolCallId,
    string? ToolName,
    string Status,
    string? ErrorMessage,
    DateTime CreatedAt);
