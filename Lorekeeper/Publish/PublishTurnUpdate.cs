using System.Text.Json.Serialization;

namespace Lorekeeper.Publish;

[JsonDerivedType(typeof(PublishTextDelta), typeDiscriminator: "text")]
[JsonDerivedType(typeof(PublishToolCallStarted), typeDiscriminator: "tool-start")]
[JsonDerivedType(typeof(PublishToolCallArgumentsDelta), typeDiscriminator: "tool-args")]
[JsonDerivedType(typeof(PublishToolCallCompleted), typeDiscriminator: "tool-end")]
[JsonDerivedType(typeof(PublishWorkspaceMutated), typeDiscriminator: "workspace-mutated")]
[JsonDerivedType(typeof(PublishAssistantMessageCompleted), typeDiscriminator: "assistant-end")]
[JsonDerivedType(typeof(PublishTurnError), typeDiscriminator: "error")]
public abstract record PublishTurnUpdate;

public sealed record PublishTextDelta(string Text) : PublishTurnUpdate;

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
    double DurationMs) : PublishTurnUpdate;

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
    Guid? SelectedObjectId = null) : PublishTurnUpdate;

public sealed record PublishAssistantMessageCompleted(Guid MessageId) : PublishTurnUpdate;

public sealed record PublishTurnError(string Message, bool Cancelled) : PublishTurnUpdate;
