using System.Text.Json.Serialization;

namespace Lorekeeper.Research;

[JsonDerivedType(typeof(ResearchTextDelta), typeDiscriminator: "text")]
[JsonDerivedType(typeof(ResearchToolCallStarted), typeDiscriminator: "tool-start")]
[JsonDerivedType(typeof(ResearchToolCallArgumentsDelta), typeDiscriminator: "tool-args")]
[JsonDerivedType(typeof(ResearchToolCallCompleted), typeDiscriminator: "tool-end")]
[JsonDerivedType(typeof(ResearchPendingAiChangeCreated), typeDiscriminator: "pending-change")]
[JsonDerivedType(typeof(ResearchGraphMutated), typeDiscriminator: "graph-mutated")]
[JsonDerivedType(typeof(ResearchAssistantMessageCompleted), typeDiscriminator: "assistant-end")]
[JsonDerivedType(typeof(ResearchTurnError), typeDiscriminator: "error")]
public abstract record ResearchTurnUpdate;

public sealed record ResearchTextDelta(string Text) : ResearchTurnUpdate;

public sealed record ResearchToolCallStarted(
    string CallId,
    string ToolName,
    string ArgumentsJson,
    bool ArgumentsComplete = true) : ResearchTurnUpdate;

public sealed record ResearchToolCallArgumentsDelta(
    string CallId,
    string ArgumentsDelta,
    bool ArgumentsComplete) : ResearchTurnUpdate;

public sealed record ResearchToolCallCompleted(
    string CallId,
    string ToolName,
    string? Result,
    string? Error,
    double DurationMs) : ResearchTurnUpdate;

public sealed record ResearchAssistantMessageCompleted(Guid MessageId) : ResearchTurnUpdate;

public sealed record ResearchPendingAiChangeCreated(
    Guid BatchId,
    Guid ChangeId,
    string ToolCallId,
    string ToolName,
    string Summary) : ResearchTurnUpdate;

public sealed record ResearchGraphMutated : ResearchTurnUpdate;

public sealed record ResearchTurnError(string Message, bool Cancelled) : ResearchTurnUpdate;
