using System.Text.Json.Serialization;
using Lorekeeper.ChatTurns;

namespace Lorekeeper.Writing;

[JsonDerivedType(typeof(VoiceMutated), typeDiscriminator: "mutated")]
[JsonDerivedType(typeof(VoiceTextDelta), typeDiscriminator: "text")]
[JsonDerivedType(typeof(VoiceReasoningDelta), typeDiscriminator: "reasoning")]
[JsonDerivedType(typeof(VoiceToolCallStarted), typeDiscriminator: "tool-start")]
[JsonDerivedType(typeof(VoiceToolCallArgumentsDelta), typeDiscriminator: "tool-args")]
[JsonDerivedType(typeof(VoiceToolCallCompleted), typeDiscriminator: "tool-end")]
[JsonDerivedType(typeof(VoiceContextTrimmed), typeDiscriminator: "context-trimmed")]
[JsonDerivedType(typeof(VoiceAssistantMessageCompleted), typeDiscriminator: "assistant-end")]
[JsonDerivedType(typeof(VoiceTurnError), typeDiscriminator: "error")]
public abstract record VoiceTurnUpdate;

public sealed record VoiceTextDelta(string Text) : VoiceTurnUpdate;

public sealed record VoiceReasoningDelta(string Text) : VoiceTurnUpdate;

public sealed record VoiceToolCallStarted(
    string CallId,
    string ToolName,
    string ArgumentsJson,
    bool ArgumentsComplete = true) : VoiceTurnUpdate;

public sealed record VoiceToolCallArgumentsDelta(
    string CallId,
    string ArgumentsDelta,
    bool ArgumentsComplete) : VoiceTurnUpdate;

public sealed record VoiceToolCallCompleted(
    string CallId,
    string ToolName,
    string? Result,
    string? Error,
    double DurationMs) : VoiceTurnUpdate;

public sealed record VoiceContextTrimmed(ChatCompactionResult Result) : VoiceTurnUpdate;

public sealed record VoiceAssistantMessageCompleted(Guid MessageId) : VoiceTurnUpdate;

public sealed record VoiceTurnError(string Message, bool Cancelled) : VoiceTurnUpdate;

public sealed record VoiceMutated : VoiceTurnUpdate;
