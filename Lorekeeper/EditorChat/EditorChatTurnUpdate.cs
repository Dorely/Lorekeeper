using System.Text.Json.Serialization;
using Lorekeeper.ChatTurns;

namespace Lorekeeper.EditorChat;

[JsonDerivedType(typeof(EditorChatTextDelta), typeDiscriminator: "text")]
[JsonDerivedType(typeof(EditorChatReasoningDelta), typeDiscriminator: "reasoning")]
[JsonDerivedType(typeof(EditorChatToolCallStarted), typeDiscriminator: "tool-start")]
[JsonDerivedType(typeof(EditorChatToolCallArgumentsDelta), typeDiscriminator: "tool-args")]
[JsonDerivedType(typeof(EditorChatToolCallCompleted), typeDiscriminator: "tool-end")]
[JsonDerivedType(typeof(EditorChatContextTrimmed), typeDiscriminator: "context-trimmed")]
[JsonDerivedType(typeof(EditorChatContestStarted), typeDiscriminator: "contest-start")]
[JsonDerivedType(typeof(EditorChatContestCandidateUpdated), typeDiscriminator: "contest-candidate")]
[JsonDerivedType(typeof(EditorChatContestCandidateProseDelta), typeDiscriminator: "contest-prose")]
[JsonDerivedType(typeof(EditorChatContestCompleted), typeDiscriminator: "contest-end")]
[JsonDerivedType(typeof(EditorChatRevisionJobUpdated), typeDiscriminator: "revision-job")]
[JsonDerivedType(typeof(EditorChatImageGenerationJobUpdated), typeDiscriminator: "image-job")]
[JsonDerivedType(typeof(EditorChatAssistantMessageCompleted), typeDiscriminator: "assistant-end")]
[JsonDerivedType(typeof(EditorChatMutated), typeDiscriminator: "editor-mutated")]
[JsonDerivedType(typeof(EditorWorkspaceMutated), typeDiscriminator: "workspace-mutated")]
[JsonDerivedType(typeof(EditorChatTurnError), typeDiscriminator: "error")]
public abstract record EditorChatTurnUpdate;

public sealed record EditorChatTextDelta(string Text) : EditorChatTurnUpdate;

public sealed record EditorChatReasoningDelta(string Text) : EditorChatTurnUpdate;

public sealed record EditorChatToolCallStarted(string CallId, string ToolName, string ArgumentsJson, bool ArgumentsComplete = true) : EditorChatTurnUpdate;

public sealed record EditorChatToolCallArgumentsDelta(string CallId, string ArgumentsDelta, bool ArgumentsComplete) : EditorChatTurnUpdate;

public sealed record EditorChatToolCallCompleted(
    string CallId,
    string ToolName,
    string? Result,
    string? Error,
    double DurationMs,
    IReadOnlyList<EditorChatVisualAttachment> Visuals) : EditorChatTurnUpdate;

public sealed record EditorChatContextTrimmed(ChatCompactionResult Result) : EditorChatTurnUpdate;

public sealed record EditorChatVisualAttachment(
    Guid Id,
    string Title,
    string Caption,
    string PreviewImageUrl,
    string FullImageUrl,
    int? Width,
    int? Height,
    string? ToolCallId = null,
    string SourceKind = "",
    Guid? SourceRefId = null,
    string ContentType = "",
    string FileName = "",
    byte[]? Data = null);

public sealed record EditorChatContestStarted(Guid BatchId) : EditorChatTurnUpdate;

public sealed record EditorChatContestCandidateUpdated(Guid BatchId, Guid CandidateId, string Status) : EditorChatTurnUpdate;

public sealed record EditorChatContestCandidateProseDelta(Guid BatchId, Guid CandidateId, string Delta, string RawResponse) : EditorChatTurnUpdate;

public sealed record EditorChatContestCompleted(Guid BatchId, string Status) : EditorChatTurnUpdate;

public sealed record EditorChatRevisionJobUpdated(
    string ToolCallId,
    Guid JobId,
    Guid? SessionId,
    EditorRevisionJobUpdateKind Kind,
    DateTime UpdatedAtUtc,
    EditorRevisionJobProgress Progress) : EditorChatTurnUpdate;

public sealed record EditorChatImageGenerationOutputProgress(
    int OutputIndex,
    string Status,
    string Message,
    string? ErrorMessage,
    int Attempt,
    string? PartialImageUrl);

public sealed record EditorChatImageGenerationJobUpdated(
    string ToolCallId,
    Guid JobId,
    string Status,
    DateTime UpdatedAtUtc,
    int TotalCount,
    int QueuedCount,
    int RunningCount,
    int CompletedCount,
    int FailedCount,
    int CancelledCount,
    IReadOnlyList<EditorChatImageGenerationOutputProgress> Outputs,
    string? LatestPartialImageUrl) : EditorChatTurnUpdate;

public sealed record EditorChatAssistantMessageCompleted(Guid MessageId) : EditorChatTurnUpdate;

public sealed record EditorChatMutated : EditorChatTurnUpdate;

public enum EditorWorkspaceMutationKind
{
    Manuscript,
    DesignedPage,
    ProjectPageSetup,
    ImageLibrary,
    Other,
}

public sealed record EditorWorkspaceMutated(
    EditorWorkspaceMutationKind Kind,
    Guid? ChapterId = null,
    Guid? DesignedPageId = null,
    Guid? VariantId = null,
    long? Revision = null,
    IReadOnlyList<string>? ChangedIds = null,
    Guid? SelectedObjectId = null) : EditorChatTurnUpdate;

public sealed record EditorChatTurnError(string Message, bool Cancelled) : EditorChatTurnUpdate;
