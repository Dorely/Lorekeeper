using System.Text.Json.Serialization;

namespace Lorekeeper.ImagesChat;

[JsonDerivedType(typeof(ImagesChatTextDelta), typeDiscriminator: "text")]
[JsonDerivedType(typeof(ImagesChatToolCallStarted), typeDiscriminator: "tool-start")]
[JsonDerivedType(typeof(ImagesChatToolCallArgumentsDelta), typeDiscriminator: "tool-args")]
[JsonDerivedType(typeof(ImagesChatToolCallCompleted), typeDiscriminator: "tool-end")]
[JsonDerivedType(typeof(ImagesChatAssistantMessageCompleted), typeDiscriminator: "assistant-end")]
[JsonDerivedType(typeof(ImagesChatMutated), typeDiscriminator: "images-mutated")]
[JsonDerivedType(typeof(ImagesChatTurnError), typeDiscriminator: "error")]
public abstract record ImagesChatTurnUpdate;

public sealed record ImagesChatTextDelta(string Text) : ImagesChatTurnUpdate;

public sealed record ImagesChatToolCallStarted(
    string CallId,
    string ToolName,
    string ArgumentsJson,
    bool ArgumentsComplete = true) : ImagesChatTurnUpdate;

public sealed record ImagesChatToolCallArgumentsDelta(
    string CallId,
    string ArgumentsDelta,
    bool ArgumentsComplete) : ImagesChatTurnUpdate;

public sealed record ImagesChatToolCallCompleted(
    string CallId,
    string ToolName,
    string? Result,
    string? Error,
    double DurationMs,
    IReadOnlyList<ImagesChatVisualAttachment> Visuals) : ImagesChatTurnUpdate;

public sealed record ImagesChatAssistantMessageCompleted(Guid MessageId) : ImagesChatTurnUpdate;

public sealed record ImagesChatMutated : ImagesChatTurnUpdate;

public sealed record ImagesChatTurnError(string Message, bool Cancelled) : ImagesChatTurnUpdate;

public sealed record ImagesChatVisualAttachment(
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
    [property: JsonIgnore] byte[]? Data = null);
