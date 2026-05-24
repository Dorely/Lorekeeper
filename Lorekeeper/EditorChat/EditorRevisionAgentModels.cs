using Lorekeeper.Models;

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
    IReadOnlyList<EditorRevisionAgentAssignmentInput> Chapters);

public sealed record EditorRevisionAgentRunResult(
    Guid JobId,
    EditorRevisionJobStatus Status,
    IReadOnlyList<EditorRevisionSessionResult> Sessions,
    string? ErrorMessage);

public sealed record EditorRevisionSessionResult(
    Guid SessionId,
    int Order,
    Guid ChapterId,
    string ChapterTitle,
    string Reason,
    string Instructions,
    EditorRevisionSessionStatus Status,
    string Summary,
    string Rationale,
    string MutationKind,
    int? StartLine,
    int? EndLine,
    string ReplacementText,
    string Notes,
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
    string MutationKind,
    int? StartLine,
    int? EndLine,
    string ReplacementText,
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
