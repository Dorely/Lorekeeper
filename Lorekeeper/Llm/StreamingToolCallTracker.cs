using System.Text;
using Microsoft.Extensions.AI;

namespace Lorekeeper.Llm;

public sealed class StreamingToolCallTracker
{
    private readonly Dictionary<string, PendingStreamingToolCall> _pending = new(StringComparer.Ordinal);

    public IReadOnlyList<StreamingToolCallUpdate> Process(AIContent content, int textOffset)
    {
        switch (content)
        {
            case FunctionCallStartedContent started:
                _pending[started.CallId] = new PendingStreamingToolCall(started.Name, textOffset);
                return [new StreamingToolCallStartedUpdate(started.CallId, started.Name, string.Empty, ArgumentsComplete: false)];

            case FunctionCallArgumentsDeltaContent delta:
                var pendingDelta = GetOrAddPending(delta.CallId, delta.Name, textOffset);
                pendingDelta.Arguments.Append(delta.Delta);
                return [new StreamingToolCallArgumentsDeltaUpdate(delta.CallId, delta.Delta, ArgumentsComplete: false)];

            case FunctionCallContent functionCall:
                var callId = functionCall.CallId ?? functionCall.Name;
                var finalArgumentsJson = SerializeArguments(functionCall.Arguments);
                if (_pending.Remove(callId, out var pendingCall))
                {
                    var rawArgumentsJson = pendingCall.Arguments.Length > 0
                        ? pendingCall.Arguments.ToString()
                        : finalArgumentsJson;
                    return [
                        new StreamingToolCallArgumentsDeltaUpdate(callId, string.Empty, ArgumentsComplete: true),
                        new StreamingToolCallReadyUpdate(functionCall, callId, functionCall.Name, rawArgumentsJson, pendingCall.TextOffset),
                    ];
                }

                return [
                    new StreamingToolCallStartedUpdate(callId, functionCall.Name, finalArgumentsJson, ArgumentsComplete: true),
                    new StreamingToolCallReadyUpdate(functionCall, callId, functionCall.Name, finalArgumentsJson, textOffset),
                ];

            default:
                return [];
        }
    }

    private PendingStreamingToolCall GetOrAddPending(string callId, string name, int textOffset)
    {
        if (_pending.TryGetValue(callId, out var pending))
            return pending;

        pending = new PendingStreamingToolCall(name, textOffset);
        _pending[callId] = pending;
        return pending;
    }

    private static string SerializeArguments(IDictionary<string, object?>? arguments) =>
        ToolCallArguments.Serialize(arguments);

    private sealed record PendingStreamingToolCall(string Name, int TextOffset)
    {
        public StringBuilder Arguments { get; } = new();
    }
}

public abstract record StreamingToolCallUpdate;

public sealed record StreamingToolCallStartedUpdate(
    string CallId,
    string ToolName,
    string ArgumentsJson,
    bool ArgumentsComplete) : StreamingToolCallUpdate;

public sealed record StreamingToolCallArgumentsDeltaUpdate(
    string CallId,
    string ArgumentsDelta,
    bool ArgumentsComplete) : StreamingToolCallUpdate;

public sealed record StreamingToolCallReadyUpdate(
    FunctionCallContent Content,
    string CallId,
    string ToolName,
    string ArgumentsJson,
    int TextOffset) : StreamingToolCallUpdate;
