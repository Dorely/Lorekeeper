using System.Text.Json.Serialization;

namespace Lorekeeper.Writing;

[JsonDerivedType(typeof(WritingCoachTextDelta), typeDiscriminator: "text")]
[JsonDerivedType(typeof(WritingCoachAssistantMessageCompleted), typeDiscriminator: "assistant-end")]
[JsonDerivedType(typeof(WritingCoachTurnError), typeDiscriminator: "error")]
public abstract record WritingCoachTurnUpdate;

public sealed record WritingCoachTextDelta(string Text) : WritingCoachTurnUpdate;

public sealed record WritingCoachAssistantMessageCompleted(Guid MessageId) : WritingCoachTurnUpdate;

public sealed record WritingCoachTurnError(string Message, bool Cancelled) : WritingCoachTurnUpdate;