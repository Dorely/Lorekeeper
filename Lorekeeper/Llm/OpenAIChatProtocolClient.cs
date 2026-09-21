using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lorekeeper.Models;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Lorekeeper.Llm;

/// <summary>
/// Preserves supported OpenAI-compatible response protocol, including opaque reasoning.
/// The OpenAI SDK retains unknown JSON fields, but the Microsoft.Extensions.AI
/// streaming adapter does not copy them onto its final FunctionCallContent.
/// </summary>
internal sealed class OpenAIChatProtocolClient(IChatClient innerClient, LlmProvider provider)
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
        if (response.RawRepresentation is ChatCompletion rawResponse)
        {
            var metadata = new ReasoningMetadata(provider);
            using var document = JsonDocument.Parse(ModelReaderWriter.Write(rawResponse, ModelReaderWriterOptions.Json));
            var displayed = metadata.Observe(document.RootElement, streamed: false);
            foreach (var message in response.Messages.Where(message => message.Role == ChatRole.Assistant))
            {
                message.Contents = message.Contents.Where(content => content is not TextReasoningContent).ToList();
                if (displayed.Length > 0) message.Contents.Add(new TextReasoningContent(displayed));
                message.Contents.Add(new ChatProtocolContent(() =>
                {
                    var snapshot = metadata.Snapshot();
                    snapshot.FinishReason = response.FinishReason?.ToString();
                    snapshot.Usage = response.Usage;
                    return snapshot;
                }));
            }
        }
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<AIChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var metadata = new StreamingToolCallMetadata();
        var reasoning = new ReasoningMetadata(provider);
        var protocol = new ChatProtocolContent(reasoning.Snapshot);
        await foreach (var update in base.GetStreamingResponseAsync(
            PrepareMessages(messages),
            options,
            cancellationToken))
        {
            metadata.Observe(update.RawRepresentation);
            metadata.Apply(update.Contents);
            var displayed = string.Empty;
            if (update.RawRepresentation is StreamingChatCompletionUpdate rawUpdate)
            {
                using var document = JsonDocument.Parse(ModelReaderWriter.Write(rawUpdate, ModelReaderWriterOptions.Json));
                displayed = reasoning.Observe(document.RootElement, streamed: true);
            }
            // The raw protocol is authoritative; do not display SDK and extension text twice.
            update.Contents = update.Contents.Where(content => content is not TextReasoningContent).ToList();
            if (displayed.Length > 0)
                update.Contents.Add(new TextReasoningContent(displayed));
            update.Contents.Add(protocol);
            yield return update;
        }
    }

    private IReadOnlyList<AIChatMessage> PrepareMessages(IEnumerable<AIChatMessage> messages)
    {
        var prepared = new List<AIChatMessage>();
        foreach (var message in messages)
        {
            var toolCalls = message.Role == ChatRole.Assistant
                ? message.Contents.OfType<FunctionCallContent>().ToList()
                : [];
            var protocol = message.Role == ChatRole.Assistant
                ? message.Contents.OfType<ChatProtocolContent>().LastOrDefault()?.Snapshot() : null;
            if (protocol is not null && !protocol.Matches(provider)) protocol = null;
            if (protocol is null && (toolCalls.Count == 0
                || toolCalls.All(call => !TryGetExtraContent(call, out _))))
            {
                prepared.Add(message);
                continue;
            }

            var clone = message.Clone();
            clone.RawRepresentation = BuildRawAssistantMessage(message, toolCalls, protocol);
            prepared.Add(clone);
        }

        return prepared;
    }

    private static OpenAI.Chat.ChatMessage BuildRawAssistantMessage(
        AIChatMessage message,
        IReadOnlyList<FunctionCallContent> toolCalls,
        ChatResponseMetadata? protocol)
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

            if (protocol is not null)
            {
                foreach (var (name, value) in protocol.ReasoningFields)
                {
                    if (name is not ("reasoning_content" or "reasoning" or "reasoning_details")) continue;
                    writer.WritePropertyName(name);
                    value.WriteTo(writer);
                }
            }
            if (toolCalls.Count > 0)
            {
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
            }
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


    private sealed class ReasoningMetadata(LlmProvider provider)
    {
        private readonly Dictionary<string, JsonNode?> _fields = [];
        private readonly Dictionary<string, JsonObject> _details = [];
        private readonly Dictionary<string, StringBuilder> _textFields = [];
        private string? _displayField;
        private bool _hasStreamedDetails;

        public string Observe(JsonElement root, bool streamed)
        {
            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return string.Empty;
            var choice = choices[0];
            if (!choice.TryGetProperty(streamed ? "delta" : "message", out var message)
                || message.ValueKind != JsonValueKind.Object) return string.Empty;
            var displayDelta = string.Empty;
            foreach (var name in new[] { "reasoning_content", "reasoning", "reasoning_details" })
            {
                if (!message.TryGetProperty(name, out var value) || (streamed && value.ValueKind == JsonValueKind.Null)) continue;
                var node = JsonNode.Parse(value.GetRawText());
                // Display only incoming fragments from one representation, independently
                // of the indexed protocol blocks (which may arrive interleaved).
                if (_displayField is null || _displayField == name)
                {
                    var text = ReadDisplayText(node);
                    if (text.Length > 0)
                    {
                        _displayField = name;
                        displayDelta = text;
                    }
                }
                if (streamed && name == "reasoning_details" && node is JsonArray details)
                {
                    _hasStreamedDetails = true;
                    for (var index = 0; index < details.Count; index++)
                    {
                        if (details[index] is not JsonObject detail) continue;
                        var key = detail["index"] is { } detailIndex ? "index:" + detailIndex.ToJsonString()
                            : detail["id"] is { } detailId ? "id:" + detailId.ToJsonString()
                            : detail["type"]?.ToJsonString() + ":" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        if (!_details.TryGetValue(key, out var captured))
                        {
                            captured = new JsonObject();
                            _details.Add(key, captured);
                        }
                        foreach (var property in detail)
                        {
                            if (property.Value is null && captured.ContainsKey(property.Key)) continue;
                            if (property.Key is "text" or "summary" or "signature" or "data"
                                && property.Value is JsonValue fragment && fragment.TryGetValue<string>(out var text)
                                && captured[property.Key] is JsonValue previous && previous.TryGetValue<string>(out var prior))
                                captured[property.Key] = prior + text;
                            else
                                captured[property.Key] = property.Value?.DeepClone();
                        }
                    }
                    // Materialize the structured array only when a round snapshot is needed.
                }
                else if (value.ValueKind == JsonValueKind.String)
                {
                    if (!_textFields.TryGetValue(name, out var text))
                        _textFields[name] = text = new StringBuilder();
                    if (!streamed) text.Clear();
                    text.Append(value.GetString());
                }
                else
                    _fields[name] = node;
            }
            return displayDelta;
        }

        public ChatResponseMetadata Snapshot()
        {
            var metadata = ChatResponseMetadata.ForProvider(provider);
            foreach (var (name, value) in _fields)
                metadata.ReasoningFields[name] = JsonSerializer.SerializeToElement(value);
            foreach (var (name, value) in _textFields)
                metadata.ReasoningFields[name] = JsonSerializer.SerializeToElement(value.ToString());
            if (_hasStreamedDetails)
                metadata.ReasoningFields["reasoning_details"] = JsonSerializer.SerializeToElement(
                    _details.Values.OrderBy(detail =>
                        detail["index"] is JsonValue index && index.TryGetValue<int>(out var ordinal) ? ordinal : int.MaxValue));
            return metadata;
        }

        private static string? ReadText(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private static string ReadDisplayText(JsonNode? value) =>
            ReadText(value) ?? (value is JsonArray details
                ? string.Concat(details.OfType<JsonObject>().Select(detail =>
                    ReadText(detail["text"]) ?? ReadText(detail["summary"]) ?? string.Empty))
                : string.Empty);
    }

    private sealed record CapturedToolCallMetadata(string? CallId, string? ExtraContent);
}
