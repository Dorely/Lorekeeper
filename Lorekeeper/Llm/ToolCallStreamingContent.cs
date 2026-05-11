using Microsoft.Extensions.AI;

namespace Lorekeeper.Llm;

public sealed class FunctionCallStartedContent(string callId, string name) : AIContent
{
    public string CallId { get; } = callId;

    public string Name { get; } = name;
}

public sealed class FunctionCallArgumentsDeltaContent(string callId, string name, string delta) : AIContent
{
    public string CallId { get; } = callId;

    public string Name { get; } = name;

    public string Delta { get; } = delta;
}
