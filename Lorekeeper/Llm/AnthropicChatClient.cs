using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lorekeeper.ChatTurns;
using Lorekeeper.Diagnostics;
using Lorekeeper.Models;
using Microsoft.Extensions.AI;

namespace Lorekeeper.Llm;

/// <summary>Native Claude Messages protocol. Credentials never enter response metadata.</summary>
internal sealed class AnthropicChatClient(
    HttpClient httpClient, string apiKey, LlmProvider provider, ILogger<AnthropicChatClient> logger) : IChatClient
{
    public const string ApiVersion = "2023-06-01";
    private static readonly ConcurrentDictionary<string, ClaudeModel> Models = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, bool> CacheRejected = new(StringComparer.Ordinal);
    private string ModelKey => $"{provider.EffectiveCredentialProviderId}:{provider.ModelId}";
    public ChatClientMetadata Metadata => new("AnthropicMessagesAPI", new Uri("https://api.anthropic.com"), provider.ModelId);

    public static void ApplyHeaders(HttpRequestMessage request, string key)
    {
        request.Headers.TryAddWithoutValidation("x-api-key", key);
        request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var text = new StringBuilder();
        var calls = new List<FunctionCallContent>();
        var capture = new ChatResponseCapture();
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            capture.Observe(update);
            foreach (var content in update.Contents)
            {
                if (content is TextContent part) text.Append(part.Text);
                if (content is FunctionCallContent call) calls.Add(call);
            }
        }
        var contents = new List<AIContent>();
        if (text.Length > 0) contents.Add(new TextContent(text.ToString()));
        contents.AddRange(calls);
        return new ChatResponse([new ChatMessage(ChatRole.Assistant, capture.Attach(contents))])
        {
            Usage = capture.Metadata.Usage,
            FinishReason = capture.Metadata.FinishReason is { } finish ? new ChatFinishReason(finish) : ChatFinishReason.Stop,
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = await ResolveModelAsync(cancellationToken);
        var source = messages.ToList();
        var cache = !CacheRejected.ContainsKey(ModelKey);
        // Capability rejection is retried only before opening a successful response stream.
        using var response = await SendAsync(source, options, model, cache, cancellationToken);
        var metadata = ChatResponseMetadata.ForProvider(provider);
        metadata.CacheMode = CacheRejected.ContainsKey(ModelKey) ? "none" : "explicit";
        var protocol = new ChatProtocolContent(metadata);
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [protocol] };
        var blocks = new SortedDictionary<int, JsonObject>();
        var arguments = new Dictionary<int, StringBuilder>();
        long? input = null, read = null, write = null, output = null;
        var finished = false;
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            using var document = JsonDocument.Parse(line[5..].TrimStart());
            var evt = document.RootElement;
            var type = String(evt, "type");
            var index = evt.TryGetProperty("index", out var position) ? position.GetInt32() : 0;
            if (type == "message_start" && evt.TryGetProperty("message", out var message))
            {
                ReadUsage(message);
                continue;
            }
            if (type == "content_block_start" && evt.TryGetProperty("content_block", out var start))
            {
                var block = JsonNode.Parse(start.GetRawText())!.AsObject();
                blocks[index] = block;
                switch (String(start, "type"))
                {
                    case "tool_use":
                        arguments[index] = new StringBuilder();
                        yield return Update(new FunctionCallStartedContent(String(start, "id")!, String(start, "name")!));
                        break;
                    case "text" when String(start, "text") is { Length: > 0 } text:
                        yield return Update(new TextContent(text));
                        break;
                    case "thinking" when String(start, "thinking") is { Length: > 0 } thinking:
                        yield return Update(new TextReasoningContent(thinking));
                        break;
                }
            }
            else if (type == "content_block_delta" && evt.TryGetProperty("delta", out var delta) && blocks.TryGetValue(index, out var block))
            {
                switch (String(delta, "type"))
                {
                    case "text_delta":
                        var text = String(delta, "text") ?? "";
                        block["text"] = (block["text"]?.GetValue<string>() ?? "") + text;
                        yield return Update(new TextContent(text));
                        break;
                    case "thinking_delta":
                        var thinking = String(delta, "thinking") ?? "";
                        block["thinking"] = (block["thinking"]?.GetValue<string>() ?? "") + thinking;
                        yield return Update(new TextReasoningContent(thinking));
                        break;
                    case "signature_delta":
                        block["signature"] = (block["signature"]?.GetValue<string>() ?? "") + String(delta, "signature");
                        break;
                    case "input_json_delta":
                        var partial = String(delta, "partial_json") ?? "";
                        arguments[index].Append(partial);
                        yield return Update(new FunctionCallArgumentsDeltaContent(block["id"]!.GetValue<string>(), block["name"]!.GetValue<string>(), partial));
                        break;
                }
            }
            else if (type == "content_block_stop" && blocks.TryGetValue(index, out var completed) && completed["type"]?.GetValue<string>() == "tool_use")
            {
                var json = arguments[index].ToString();
                if (json.Length > 0) completed["input"] = JsonNode.Parse(json);
                var values = completed["input"]?.Deserialize<Dictionary<string, object?>>() ?? [];
                yield return Update(new FunctionCallContent(completed["id"]!.GetValue<string>(), completed["name"]!.GetValue<string>(), values));
            }
            else if (type == "message_delta")
            {
                ReadUsage(evt);
                if (evt.TryGetProperty("delta", out var terminal)) metadata.FinishReason = String(terminal, "stop_reason");
            }
            else if (type == "error")
            {
                metadata.Incomplete = true;
                metadata.FinishReason = "error";
                throw new HttpRequestException("Claude request failed: " + LogRedaction.RedactJson(evt.GetRawText()));
            }
            else if (type == "message_stop")
            {
                finished = true;
                break;
            }
            // Preserve exact ordered native blocks in active-turn protocol, including opaque signatures.
            metadata.ReasoningFields["anthropic.content"] = JsonSerializer.SerializeToElement(blocks.Values);
        }
        metadata.ReasoningFields["anthropic.content"] = JsonSerializer.SerializeToElement(blocks.Values);
        metadata.Incomplete = !finished;
        if (!finished) throw new HttpRequestException("Claude stream ended before completion. Partial output is retained.");
        var usage = new UsageDetails
        {
            InputTokenCount = input is null ? null : input + (read ?? 0) + (write ?? 0),
            OutputTokenCount = output,
            CachedInputTokenCount = read,
        };
        usage.TotalTokenCount = usage.InputTokenCount + output;
        if (write is { } written) (usage.AdditionalCounts ??= [])[NativeChatUsage.CacheWriteInputTokens] = written;
        metadata.Usage = usage;
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            FinishReason = metadata.OutputLimitReached ? ChatFinishReason.Length
                : metadata.FinishReason == "tool_use" ? ChatFinishReason.ToolCalls
                : metadata.FinishReason is "end_turn" or "stop_sequence" ? ChatFinishReason.Stop : new ChatFinishReason("incomplete"),
            Contents = [new UsageContent(usage), protocol],
        };

        void ReadUsage(JsonElement envelope)
        {
            if (!envelope.TryGetProperty("usage", out var usage)) return;
            // message_delta counters are cumulative, not increments.
            input = NativeChatUsage.Count(usage, "input_tokens") ?? input;
            read = NativeChatUsage.Count(usage, "cache_read_input_tokens") ?? read;
            write = NativeChatUsage.Count(usage, "cache_creation_input_tokens") ?? write;
            output = NativeChatUsage.Count(usage, "output_tokens") ?? output;
            metadata.Usage = new UsageDetails { InputTokenCount = input is null ? null : input + (read ?? 0) + (write ?? 0),
                OutputTokenCount = output, CachedInputTokenCount = read };
            metadata.Usage.TotalTokenCount = metadata.Usage.InputTokenCount + output;
            if (write is { } written) (metadata.Usage.AdditionalCounts ??= [])[NativeChatUsage.CacheWriteInputTokens] = written;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options,
        ClaudeModel model, bool cache, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var json = JsonSerializer.Serialize(BuildRequest(messages, options, model, cache));
            var response = await ChatHttpRequests.SendAsync(httpClient, () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
                ApplyHeaders(request, apiKey);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                return request;
            }, cancellationToken);
            if (response.IsSuccessStatusCode) return response;
            var error = LogRedaction.RedactJson(await response.Content.ReadAsStringAsync(cancellationToken));
            var status = (int)response.StatusCode;
            response.Dispose();
            if (attempt == 0 && status is 400 or 422 && cache && error.Contains("cache_control", StringComparison.Ordinal))
            {
                CacheRejected[ModelKey] = true;
                cache = false;
                logger.LogDebug("Claude cache controls unsupported; retrying the selected model without caching");
                continue;
            }
            throw new HttpRequestException($"Claude request failed (HTTP {status}): {LlmErrorNormalizer.Truncate(error)}");
        }
    }

    private Dictionary<string, object> BuildRequest(IReadOnlyList<ChatMessage> source, ChatOptions? options, ClaudeModel model, bool cache)
    {
        var system = new List<Dictionary<string, object>>();
        var messages = new List<Dictionary<string, object>>();
        var pendingSystem = new List<Dictionary<string, object>>();
        Dictionary<string, object>? workspaceBoundary = null;
        foreach (var message in source)
        {
            var blocks = new List<Dictionary<string, object>>();
            var protocol = message.Contents.OfType<ChatProtocolContent>().LastOrDefault()?.Snapshot();
            if (!ChatRequestLayout.IsCanonical(message) && protocol?.Matches(provider) is true
                && protocol.ReasoningFields.TryGetValue("anthropic.content", out var native))
                blocks.AddRange(native.Deserialize<List<Dictionary<string, object>>>() ?? []);
            else
            {
                foreach (var content in message.Contents)
                {
                    switch (content)
                    {
                        case TextContent text when !string.IsNullOrEmpty(text.Text):
                            blocks.Add(new() { ["type"] = "text", ["text"] = text.Text });
                            break;
                        case DataContent image when image.HasTopLevelMediaType("image"):
                            blocks.Add(new() { ["type"] = "image", ["source"] = new { type = "base64", media_type = image.MediaType, data = Convert.ToBase64String(image.Data.Span) } });
                            break;
                        case FunctionCallContent call:
                            blocks.Add(new() { ["type"] = "tool_use", ["id"] = call.CallId, ["name"] = call.Name, ["input"] = call.Arguments ?? new Dictionary<string, object?>() });
                            break;
                        case FunctionResultContent result:
                            blocks.Add(new() { ["type"] = "tool_result", ["tool_use_id"] = result.CallId, ["content"] = result.Result?.ToString() ?? "" });
                            break;
                    }
                }
            }
            if (blocks.Count == 0) continue;
            if (cache && ChatRequestLayout.IsBoundary(message))
            {
                var block = blocks.LastOrDefault(block => block["type"] as string is not ("thinking" or "redacted_thinking"));
                if (block is not null)
                {
                    block["cache_control"] = new { type = "ephemeral" };
                    if (!ChatRequestLayout.IsCanonical(message) && !ChatRequestLayout.IsApplication(message)) workspaceBoundary = block;
                }
            }
            if (message.Role == ChatRole.System)
            {
                if (messages.Count == 0 || !model.MidConversationSystem) system.AddRange(blocks);
                else pendingSystem.AddRange(blocks);
            }
            else
            {
                var role = message.Role == ChatRole.Assistant ? "assistant" : "user";
                if (role == "assistant") FlushSystem();
                // Merge visual deliveries and parallel results into their owning user turn.
                if (messages.LastOrDefault()?.GetValueOrDefault("role") as string == role)
                    ((List<Dictionary<string, object>>)messages[^1]["content"]).AddRange(blocks);
                else messages.Add(new() { ["role"] = role, ["content"] = blocks });
            }
        }
        FlushSystem();
        // Late system context follows user visuals on models that support it.
        if (workspaceBoundary is not null && messages.Count > 0)
        {
            workspaceBoundary.Remove("cache_control");
            var endpoint = ((List<Dictionary<string, object>>)messages[^1]["content"])
                .LastOrDefault(block => block["type"] as string is not ("thinking" or "redacted_thinking"));
            if (endpoint is not null) endpoint["cache_control"] = new { type = "ephemeral" };
        }
        var body = new Dictionary<string, object>
        {
            ["model"] = provider.ModelId, ["stream"] = true,
            ["max_tokens"] = provider.MaxOutputTokens ?? options?.MaxOutputTokens ?? model.MaxOutputTokens,
            ["system"] = system, ["messages"] = messages,
        };
        if (options?.Tools is { Count: > 0 })
        {
            body["tools"] = options.Tools.OfType<AIFunction>().OrderBy(tool => tool.Name, StringComparer.Ordinal)
                .Select(tool => new { name = tool.Name, description = tool.Description, input_schema = tool.JsonSchema }).ToList();
            body["tool_choice"] = options.ToolMode == ChatToolMode.None ? new { type = "none" } : new { type = "auto" };
        }
        if (provider.EffectiveReasoningEffort is { } effort && model.Efforts.Contains(effort.ToWireValue()))
            body["output_config"] = new { effort = effort.ToWireValue() };
        return body;

        void FlushSystem()
        {
            if (pendingSystem.Count == 0) return;
            messages.Add(new() { ["role"] = "system", ["content"] = pendingSystem.ToList() });
            pendingSystem.Clear();
        }
    }

    private async Task<ClaudeModel> ResolveModelAsync(CancellationToken cancellationToken)
    {
        if (Models.TryGetValue(ModelKey, out var cached)) return cached;
        var model = DefaultModel(provider.ModelId);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models/" + Uri.EscapeDataString(provider.ModelId));
            ApplyHeaders(request, apiKey);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await httpClient.SendAsync(request, timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                var root = json.RootElement;
                if (NativeChatUsage.Count(root, "max_tokens") is > 0 and var maximum)
                    model = model with { MaxOutputTokens = (int)Math.Min(maximum, int.MaxValue) };
                if (root.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Object)
                {
                    if (caps.TryGetProperty("mid_conversation_system_messages", out var mid) && mid.ValueKind == JsonValueKind.Object && mid.TryGetProperty("supported", out var supported))
                        model = model with { MidConversationSystem = supported.ValueKind == JsonValueKind.True };
                    if (caps.TryGetProperty("effort", out var effort) && effort.ValueKind == JsonValueKind.Object)
                    {
                        var supportedEfforts = effort.EnumerateObject().Where(item => item.Name is "low" or "medium" or "high" or "xhigh" or "max")
                            .Where(item => item.Value.ValueKind == JsonValueKind.True || item.Value.ValueKind == JsonValueKind.Object
                                && item.Value.TryGetProperty("supported", out var enabled) && enabled.ValueKind == JsonValueKind.True)
                            .Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
                        if (supportedEfforts.Count > 0 || effort.TryGetProperty("supported", out var enabled) && enabled.ValueKind == JsonValueKind.False)
                            model = model with { Efforts = supportedEfforts };
                    }
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Claude model metadata unavailable; retaining documented model capabilities");
        }
        cancellationToken.ThrowIfCancellationRequested();
        Models[ModelKey] = model;
        return model;
    }

    private static ClaudeModel DefaultModel(string id)
    {
        var current = id.StartsWith("claude-fable-5", StringComparison.Ordinal) || id.StartsWith("claude-mythos-5", StringComparison.Ordinal)
            || id.StartsWith("claude-opus-5", StringComparison.Ordinal) || id.StartsWith("claude-sonnet-5-5", StringComparison.Ordinal)
            || id.StartsWith("claude-haiku-5-5", StringComparison.Ordinal);
        var mid = current || id.StartsWith("claude-opus-4-8", StringComparison.Ordinal);
        var max = current || id.StartsWith("claude-opus-4-6", StringComparison.Ordinal)
            || id.StartsWith("claude-opus-4-7", StringComparison.Ordinal) || id.StartsWith("claude-opus-4-8", StringComparison.Ordinal) ? 128_000 : 64_000;
        var efforts = current || id.StartsWith("claude-opus-4-6", StringComparison.Ordinal)
            || id.StartsWith("claude-opus-4-7", StringComparison.Ordinal) || id.StartsWith("claude-opus-4-8", StringComparison.Ordinal)
            || id.StartsWith("claude-sonnet-4-6", StringComparison.Ordinal)
                ? new HashSet<string>(["low", "medium", "high", "max"], StringComparer.Ordinal)
                : id.StartsWith("claude-opus-4-5", StringComparison.Ordinal)
                    ? new HashSet<string>(["low", "medium", "high"], StringComparer.Ordinal) : [];
        if (current || id.StartsWith("claude-opus-4-7", StringComparison.Ordinal)
            || id.StartsWith("claude-opus-4-8", StringComparison.Ordinal)) efforts.Add("xhigh");
        return new(max, mid, efforts);
    }

    private sealed record ClaudeModel(int MaxOutputTokens, bool MidConversationSystem, HashSet<string> Efforts);
    private static string? String(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    private static ChatResponseUpdate Update(AIContent content) => new() { Role = ChatRole.Assistant, Contents = [content] };
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
