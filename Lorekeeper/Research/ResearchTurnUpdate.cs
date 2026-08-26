using System.Text.Json.Serialization;
using Lorekeeper.ChatTurns;

namespace Lorekeeper.Research;

[JsonDerivedType(typeof(ResearchTextDelta), typeDiscriminator: "text")]
[JsonDerivedType(typeof(ResearchReasoningDelta), typeDiscriminator: "reasoning")]
[JsonDerivedType(typeof(ResearchToolCallStarted), typeDiscriminator: "tool-start")]
[JsonDerivedType(typeof(ResearchToolCallArgumentsDelta), typeDiscriminator: "tool-args")]
[JsonDerivedType(typeof(ResearchToolCallCompleted), typeDiscriminator: "tool-end")]
[JsonDerivedType(typeof(ResearchContextTrimmed), typeDiscriminator: "context-trimmed")]
[JsonDerivedType(typeof(ResearchGraphMutated), typeDiscriminator: "graph-mutated")]
[JsonDerivedType(typeof(ResearchAssistantMessageCompleted), typeDiscriminator: "assistant-end")]
[JsonDerivedType(typeof(ResearchTurnError), typeDiscriminator: "error")]
public abstract record ResearchTurnUpdate;

public sealed record ResearchTextDelta(string Text) : ResearchTurnUpdate;

public sealed record ResearchReasoningDelta(string Text) : ResearchTurnUpdate;

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

public sealed record ResearchContextTrimmed(ChatCompactionResult Result) : ResearchTurnUpdate;

public sealed record ResearchAssistantMessageCompleted(Guid MessageId) : ResearchTurnUpdate;

public sealed record ResearchGraphMutated : ResearchTurnUpdate;

public sealed record ResearchTurnError(string Message, bool Cancelled) : ResearchTurnUpdate;
