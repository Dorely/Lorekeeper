using System.Text.Json.Serialization;
using Lorekeeper.ChatTurns;

namespace Lorekeeper.Publish;

[JsonDerivedType(typeof(PublishTextDelta), typeDiscriminator: "text")]
[JsonDerivedType(typeof(PublishReasoningDelta), typeDiscriminator: "reasoning")]
[JsonDerivedType(typeof(PublishToolCallStarted), typeDiscriminator: "tool-start")]
[JsonDerivedType(typeof(PublishToolCallArgumentsDelta), typeDiscriminator: "tool-args")]
[JsonDerivedType(typeof(PublishToolCallCompleted), typeDiscriminator: "tool-end")]
[JsonDerivedType(typeof(PublishContextTrimmed), typeDiscriminator: "context-trimmed")]
[JsonDerivedType(typeof(PublishWorkspaceMutated), typeDiscriminator: "workspace-mutated")]
[JsonDerivedType(typeof(PublishAssistantMessageCompleted), typeDiscriminator: "assistant-end")]
[JsonDerivedType(typeof(PublishTurnError), typeDiscriminator: "error")]
public abstract record PublishTurnUpdate;

public sealed record PublishTextDelta(string Text) : PublishTurnUpdate;

public sealed record PublishReasoningDelta(string Text) : PublishTurnUpdate;

public sealed record PublishToolCallStarted(
    string CallId,
    string ToolName,
    string ArgumentsJson,
    bool ArgumentsComplete = true) : PublishTurnUpdate;

public sealed record PublishToolCallArgumentsDelta(
    string CallId,
    string ArgumentsDelta,
    bool ArgumentsComplete) : PublishTurnUpdate;

public sealed record PublishToolCallCompleted(
    string CallId,
    string ToolName,
    string? Result,
    string? Error,
    double DurationMs,
    IReadOnlyList<PublishChatVisualAttachment> Visuals) : PublishTurnUpdate;

public sealed record PublishContextTrimmed(ChatCompactionResult Result) : PublishTurnUpdate;

public sealed record PublishChatVisualAttachment(
    Guid Id,
    string Title,
    string Caption,
    string PreviewImageUrl,
    string FullImageUrl,
    int? Width,
    int? Height,
    string? ToolCallId,
    string SourceKind,
    Guid? SourceRefId,
    string ContentType,
    string FileName,
    byte[]? Data = null);

public sealed record PublishAssistantWorkspaceContext(
    string Surface,
    string TargetLabel,
    Guid? SectionId = null,
    string? SectionTitle = null,
    Guid? CompositionId = null,
    Guid? SelectedObjectId = null,
    Guid? VariantId = null,
    long? VariantRevision = null);

public enum PublishWorkspaceMutationKind
{
    Edition,
    Render,
    Preflight,
    Package,
    ImageLibrary,
}

public sealed record PublishWorkspaceMutated(
    Guid? EditionId,
    bool SelectEdition,
    PublishWorkspaceMutationKind Kind = PublishWorkspaceMutationKind.Edition,
    Guid? SelectedObjectId = null,
    Guid? SelectedSectionId = null,
    Guid? SelectedCompositionId = null,
    Guid? SelectedVariantId = null) : PublishTurnUpdate;

public sealed record PublishAssistantMessageCompleted(Guid MessageId) : PublishTurnUpdate;

public sealed record PublishTurnError(string Message, bool Cancelled) : PublishTurnUpdate;
