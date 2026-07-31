using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Lorekeeper.Llm;

/// <summary>
/// Preserves provider-specific fields attached to OpenAI-compatible tool calls.
/// The OpenAI SDK retains unknown JSON fields, but the Microsoft.Extensions.AI
/// streaming adapter does not copy them onto its final FunctionCallContent.
/// </summary>
internal sealed class OpenAIChatToolMetadataClient(IChatClient innerClient)
    : DelegatingChatClient(innerClient)
{
    internal const string ExtraContentPropertyName = "lorekeeper.openai.tool_call.extra_content";

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<AIChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(
            PrepareMessages(messages),
            options,
            cancellationToken);
        PreserveBufferedToolCallMetadata(response.Messages.SelectMany(message => message.Contents));
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<AIChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var metadata = new StreamingToolCallMetadata();
        await foreach (var update in base.GetStreamingResponseAsync(
            PrepareMessages(messages),
            options,
            cancellationToken))
        {
            metadata.Observe(update.RawRepresentation);
            metadata.Apply(update.Contents);
            yield return update;
        }
    }

    private static IReadOnlyList<AIChatMessage> PrepareMessages(IEnumerable<AIChatMessage> messages)
    {
        var prepared = new List<AIChatMessage>();
        foreach (var message in messages)
        {
            var toolCalls = message.Role == ChatRole.Assistant
                ? message.Contents.OfType<FunctionCallContent>().ToList()
                : [];
            if (toolCalls.Count == 0
                || toolCalls.All(call => !TryGetExtraContent(call, out _)))
            {
                prepared.Add(message);
                continue;
            }

            var clone = message.Clone();
            clone.RawRepresentation = BuildRawAssistantMessage(message, toolCalls);
            prepared.Add(clone);
        }

        return prepared;
    }

    private static OpenAI.Chat.ChatMessage BuildRawAssistantMessage(
        AIChatMessage message,
        IReadOnlyList<FunctionCallContent> toolCalls)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("role", "assistant");

            var text = message.Contents
                .OfType<TextContent>()
                .Select(content => content.Text)
                .Where(content => !string.IsNullOrEmpty(content))
                .ToArray();
            if (text.Length == 0)
                writer.WriteNull("content");
            else
                writer.WriteString("content", string.Concat(text));

            writer.WritePropertyName("tool_calls");
            writer.WriteStartArray();
            foreach (var call in toolCalls)
            {
                writer.WriteStartObject();
                writer.WriteString("id", call.CallId);
                writer.WriteString("type", "function");
                writer.WritePropertyName("function");
                writer.WriteStartObject();
                writer.WriteString("name", call.Name);
                writer.WriteString("arguments", ToolCallArguments.Serialize(call.Arguments));
                writer.WriteEndObject();

                if (TryGetExtraContent(call, out var extraContent))
                {
                    writer.WritePropertyName("extra_content");
                    using var document = JsonDocument.Parse(extraContent);
                    document.RootElement.WriteTo(writer);
                }

                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return ModelReaderWriter.Read<OpenAI.Chat.ChatMessage>(
            BinaryData.FromBytes(stream.ToArray()),
            ModelReaderWriterOptions.Json)
            ?? throw new InvalidOperationException("Unable to construct an OpenAI assistant tool-call message.");
    }

    private static void PreserveBufferedToolCallMetadata(IEnumerable<AIContent> contents)
    {
        foreach (var call in contents.OfType<FunctionCallContent>())
        {
            if (call.RawRepresentation is ChatToolCall rawCall
                && TryReadExtraContent(rawCall, out var extraContent))
            {
                SetExtraContent(call, extraContent);
            }
        }
    }

    private static bool TryReadExtraContent<T>(T rawToolCall, out string extraContent)
        where T : IPersistableModel<T>
    {
        try
        {
            var json = ModelReaderWriter.Write(rawToolCall, ModelReaderWriterOptions.Json);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("extra_content", out var value)
                && value.ValueKind == JsonValueKind.Object)
            {
                extraContent = value.GetRawText();
                return true;
            }
        }
        catch (JsonException)
        {
            // Provider extensions are optional. Invalid extension JSON is ignored.
        }

        extraContent = string.Empty;
        return false;
    }

    private static void SetExtraContent(FunctionCallContent call, string extraContent)
    {
        (call.AdditionalProperties ??= [])[ExtraContentPropertyName] = extraContent;
    }

    private static bool TryGetExtraContent(FunctionCallContent call, out string extraContent)
    {
        if (call.AdditionalProperties?.TryGetValue(ExtraContentPropertyName, out var value) is true
            && value is string json
            && !string.IsNullOrWhiteSpace(json))
        {
            extraContent = json;
            return true;
        }

        extraContent = string.Empty;
        return false;
    }

    private sealed class StreamingToolCallMetadata
    {
        private readonly Dictionary<int, CapturedToolCallMetadata> _byIndex = [];

        public void Observe(object? rawRepresentation)
        {
            if (rawRepresentation is not StreamingChatCompletionUpdate rawUpdate)
                return;

            foreach (var rawToolCall in rawUpdate.ToolCallUpdates)
            {
                _byIndex.TryGetValue(rawToolCall.Index, out var existing);
                var callId = string.IsNullOrWhiteSpace(rawToolCall.ToolCallId)
                    ? existing?.CallId
                    : rawToolCall.ToolCallId;
                var extraContent = TryReadExtraContent(rawToolCall, out var observedExtraContent)
                    ? observedExtraContent
                    : existing?.ExtraContent;
                _byIndex[rawToolCall.Index] = new CapturedToolCallMetadata(
                    callId,
                    extraContent);
            }
        }

        public void Apply(IEnumerable<AIContent> contents)
        {
            var calls = contents.OfType<FunctionCallContent>().ToList();
            if (calls.Count == 0 || _byIndex.Count == 0)
                return;

            for (var index = 0; index < calls.Count; index++)
            {
                var call = calls[index];
                var captured = _byIndex.Values.FirstOrDefault(item =>
                    !string.IsNullOrWhiteSpace(item.CallId)
                    && string.Equals(item.CallId, call.CallId, StringComparison.Ordinal));
                if (captured is null)
                    _byIndex.TryGetValue(index, out captured);
                if (captured?.ExtraContent is not null)
                    SetExtraContent(call, captured.ExtraContent);
            }
        }
    }

    private sealed record CapturedToolCallMetadata(string? CallId, string? ExtraContent);
}
