using System.Text.Json.Serialization;
using Lorekeeper.ChatTurns;

namespace Lorekeeper.Writing;

[JsonDerivedType(typeof(WritingCoachTextDelta), typeDiscriminator: "text")]
[JsonDerivedType(typeof(WritingCoachReasoningDelta), typeDiscriminator: "reasoning")]
[JsonDerivedType(typeof(WritingCoachToolCallStarted), typeDiscriminator: "tool-start")]
[JsonDerivedType(typeof(WritingCoachToolCallArgumentsDelta), typeDiscriminator: "tool-args")]
[JsonDerivedType(typeof(WritingCoachToolCallCompleted), typeDiscriminator: "tool-end")]
[JsonDerivedType(typeof(WritingCoachContextTrimmed), typeDiscriminator: "context-trimmed")]
[JsonDerivedType(typeof(WritingCoachAssistantMessageCompleted), typeDiscriminator: "assistant-end")]
[JsonDerivedType(typeof(WritingCoachTurnError), typeDiscriminator: "error")]
public abstract record WritingCoachTurnUpdate;

public sealed record WritingCoachTextDelta(string Text) : WritingCoachTurnUpdate;

public sealed record WritingCoachReasoningDelta(string Text) : WritingCoachTurnUpdate;

public sealed record WritingCoachToolCallStarted(
    string CallId,
    string ToolName,
    string ArgumentsJson,
    bool ArgumentsComplete = true) : WritingCoachTurnUpdate;

public sealed record WritingCoachToolCallArgumentsDelta(
    string CallId,
    string ArgumentsDelta,
    bool ArgumentsComplete) : WritingCoachTurnUpdate;

public sealed record WritingCoachToolCallCompleted(
    string CallId,
    string ToolName,
    string? Result,
    string? Error,
    double DurationMs) : WritingCoachTurnUpdate;

public sealed record WritingCoachContextTrimmed(ChatCompactionResult Result) : WritingCoachTurnUpdate;

public sealed record WritingCoachAssistantMessageCompleted(Guid MessageId) : WritingCoachTurnUpdate;

public sealed record WritingCoachTurnError(string Message, bool Cancelled) : WritingCoachTurnUpdate;
