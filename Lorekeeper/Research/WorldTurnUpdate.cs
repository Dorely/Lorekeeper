using System.Text.Json.Serialization;
using Lorekeeper.ChatTurns;

namespace Lorekeeper.Research;

[JsonDerivedType(typeof(WorldTextDelta), typeDiscriminator: "text")]
[JsonDerivedType(typeof(WorldReasoningDelta), typeDiscriminator: "reasoning")]
[JsonDerivedType(typeof(WorldToolCallStarted), typeDiscriminator: "tool-start")]
[JsonDerivedType(typeof(WorldToolCallArgumentsDelta), typeDiscriminator: "tool-args")]
[JsonDerivedType(typeof(WorldToolCallCompleted), typeDiscriminator: "tool-end")]
[JsonDerivedType(typeof(WorldContextTrimmed), typeDiscriminator: "context-trimmed")]
[JsonDerivedType(typeof(WorldMutated), typeDiscriminator: "graph-mutated")]
[JsonDerivedType(typeof(WorldAssistantMessageCompleted), typeDiscriminator: "assistant-end")]
[JsonDerivedType(typeof(WorldTurnError), typeDiscriminator: "error")]
public abstract record WorldTurnUpdate;

public sealed record WorldTextDelta(string Text) : WorldTurnUpdate;

public sealed record WorldReasoningDelta(string Text) : WorldTurnUpdate;

public sealed record WorldToolCallStarted(
    string CallId,
    string ToolName,
    string ArgumentsJson,
    bool ArgumentsComplete = true) : WorldTurnUpdate;

public sealed record WorldToolCallArgumentsDelta(
    string CallId,
    string ArgumentsDelta,
    bool ArgumentsComplete) : WorldTurnUpdate;

public sealed record WorldToolCallCompleted(
    string CallId,
    string ToolName,
    string? Result,
    string? Error,
    double DurationMs) : WorldTurnUpdate;

public sealed record WorldContextTrimmed(ChatCompactionResult Result) : WorldTurnUpdate;

public sealed record WorldAssistantMessageCompleted(Guid MessageId) : WorldTurnUpdate;

public sealed record WorldMutated : WorldTurnUpdate;

public sealed record WorldTurnError(string Message, bool Cancelled) : WorldTurnUpdate;
