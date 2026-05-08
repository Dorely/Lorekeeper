using System.Text.Json.Serialization;

namespace Lorekeeper.Writing;

[JsonDerivedType(typeof(WritingCoachTextDelta), typeDiscriminator: "text")]
[JsonDerivedType(typeof(WritingCoachToolCallStarted), typeDiscriminator: "tool-start")]
[JsonDerivedType(typeof(WritingCoachToolCallCompleted), typeDiscriminator: "tool-end")]
[JsonDerivedType(typeof(WritingCoachAssistantMessageCompleted), typeDiscriminator: "assistant-end")]
[JsonDerivedType(typeof(WritingCoachTurnError), typeDiscriminator: "error")]
public abstract record WritingCoachTurnUpdate;

public sealed record WritingCoachTextDelta(string Text) : WritingCoachTurnUpdate;

public sealed record WritingCoachToolCallStarted(
    string CallId,
    string ToolName,
    string ArgumentsJson) : WritingCoachTurnUpdate;

public sealed record WritingCoachToolCallCompleted(
    string CallId,
    string ToolName,
    string? Result,
    string? Error,
    double DurationMs) : WritingCoachTurnUpdate;

public sealed record WritingCoachAssistantMessageCompleted(Guid MessageId) : WritingCoachTurnUpdate;

public sealed record WritingCoachTurnError(string Message, bool Cancelled) : WritingCoachTurnUpdate;