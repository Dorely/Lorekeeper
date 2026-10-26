using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lorekeeper.Diagnostics;
using Lorekeeper.ChatTurns;
using System.Collections.Concurrent;
using Lorekeeper.Models;
using Lorekeeper.Search;
using Microsoft.Extensions.AI;

namespace Lorekeeper.Llm;

/// <summary>
/// Shared stateless Responses protocol for API-key and account connections.
/// </summary>
public sealed partial class ResponsesChatClient : IChatClient, IModelWebSearch
{
    private const int MaxBufferedResponseAttempts = 2;
    private static readonly ConcurrentDictionary<string, bool> CacheRejected = new();
    private static readonly ConcurrentDictionary<string, bool> InitialInstructionsRequired = new();
    private readonly LlmProvider? _provider;
    private string CacheCapabilityKey => $"{_profile.Endpoint}:{_model}:{_provider?.EffectiveCredentialProviderId}";
    private bool ExplicitCache => ProviderChatProtocol.SupportsExplicitCache(_model) && !CacheRejected.ContainsKey(CacheCapabilityKey);

    private readonly HttpClient _httpClient;
    private readonly string _accessToken;
    private readonly string _model;
    private readonly string? _reasoningEffort;
    private readonly string _accountId;
    private readonly ILogger<ResponsesChatClient> _logger;
    private readonly ResponsesTransportProfile _profile;
    private readonly Func<CancellationToken, Task>? _onPlanNotEligible;

    public ResponsesChatClient(
        HttpClient httpClient,
        string accessToken,
        string externalAccountId,
        string model,
        LlmReasoningEffort? reasoningEffort,
        ILogger<ResponsesChatClient> logger,
        ResponsesTransportProfile profile,
        Func<CancellationToken, Task>? onPlanNotEligible = null,
        LlmProvider? provider = null)
    {
        _httpClient = httpClient;
        _accessToken = accessToken;
        _model = string.IsNullOrWhiteSpace(model) ? LlmProviderCatalog.OpenAiDefaultMainlineModel : model;
        _reasoningEffort = reasoningEffort?.ToWireValue();
        _accountId = externalAccountId;
        _logger = logger;
        _profile = profile;
        _onPlanNotEligible = onPlanNotEligible;
        _provider = provider;
    }

    public ChatClientMetadata Metadata => _profile.Kind is null
        ? new("OpenAIResponsesAPI", new Uri("https://api.openai.com"))
        : _profile.Kind == OpenAiAccountKind.ChatGptPlan
        ? new("ChatGptPlanResponsesAPI", new Uri("https://api.openai.com"))
        : new("CodexResponsesAPI", new Uri("https://chatgpt.com"));

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var bufferedMessages = chatMessages as IReadOnlyList<ChatMessage> ?? chatMessages.ToList();

        for (var attempt = 1; attempt <= MaxBufferedResponseAttempts; attempt++)
        {
            var fullText = new StringBuilder();
            var functionCalls = new List<FunctionCallContent>();
            var capture = new ChatResponseCapture();

            try
            {
                await foreach (var update in GetStreamingResponseAsync(bufferedMessages, options, cancellationToken))
                {
                    capture.Observe(update);
                    foreach (var content in update.Contents)
                    {
                        if (content is TextContent tc && tc.Text is { Length: > 0 })
                            fullText.Append(tc.Text);
                        else if (content is FunctionCallContent fcc)
                            functionCalls.Add(fcc);
                    }
                }
            }
            catch (HttpIOException ex) when (IsResponseEnded(ex)
                && attempt < MaxBufferedResponseAttempts
                && fullText.Length == 0
                && functionCalls.Count == 0
                && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    ex,
                    "{Transport} streaming response ended before assistant content on attempt {Attempt}; retrying once.",
                    _profile.Label,
                    attempt);
                continue;
            }
            catch (HttpIOException ex) when (IsResponseEnded(ex) && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    ex,
                    "{Transport} streaming response ended after partial assistant content; not retrying. TextChars={TextChars}, FunctionCalls={FunctionCallCount}",
                    _profile.Label,
                    fullText.Length,
                    functionCalls.Count);
                throw;
            }

            var result = CreateResponse(fullText, functionCalls);
            result.Messages[0].Contents = capture.Attach(result.Messages[0].Contents.ToList());
            result.Usage = capture.Metadata.Usage;
            result.FinishReason = capture.Metadata.FinishReason is { } finish ? new ChatFinishReason(finish) : ChatFinishReason.Stop;
            return result;
        }

        throw new HttpRequestException(
            $"{_profile.Label} streaming response ended prematurely before assistant content after retry.");
    }

    private static ChatResponse CreateResponse(StringBuilder fullText, IReadOnlyList<FunctionCallContent> functionCalls)
    {
        var contents = new List<AIContent>();
        if (fullText.Length > 0)
            contents.Add(new TextContent(fullText.ToString()));
        contents.AddRange(functionCalls);

        return new ChatResponse([new ChatMessage(ChatRole.Assistant, contents)]);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messages = chatMessages.ToList();
        for (var attempt = 0; ; attempt++)
        {
            var emitted = false;
            var retry = false;
            await using var stream = StreamAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                bool hasNext;
                try { hasNext = await stream.MoveNextAsync(); }
                catch (ContextPlacementRejectedException) when (!emitted && attempt < 2)
                {
                    InitialInstructionsRequired[CacheCapabilityKey] = true;
                    retry = true;
                    _logger.LogDebug("{Transport} requires initial instructions; retaining fresh authoritative context in the supported position", _profile.Label);
                    break;
                }
                catch (CacheControlRejectedException) when (!emitted && attempt < 2)
                {
                    CacheRejected[CacheCapabilityKey] = true;
                    retry = true;
                    _logger.LogDebug("{Transport} cache controls unsupported; retrying the same model without them", _profile.Label);
                    break;
                }
                if (!hasNext) break;
                emitted |= stream.Current.Contents.Any(content => content is TextContent or TextReasoningContent or FunctionCallContent or FunctionCallStartedContent);
                yield return stream.Current;
            }
            if (!retry) yield break;
        }
    }

    private sealed class CacheControlRejectedException : Exception;
    private sealed class ContextPlacementRejectedException : Exception;

    private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var bufferedMessages = chatMessages as IReadOnlyCollection<ChatMessage> ?? chatMessages.ToList();
        var body = BuildRequestBody(bufferedMessages, options);
        var json = JsonSerializer.Serialize(body);
        var toolCount = options?.Tools?.Count ?? 0;

        var metadata = _provider is null ? new ChatResponseMetadata { Transport = ProviderChatProtocol.Responses, ModelId = _model }
            : ChatResponseMetadata.ForProvider(_provider);
        metadata.CacheMode = body.ContainsKey("prompt_cache_options") ? "explicit" : "implicit";
        var protocol = new ChatProtocolContent(metadata);


        _logger.LogDebug(
            "{Transport} request: POST {Endpoint}, account={AccountId}, model={Model}, messages={MessageCount}, tools={ToolCount}, bodyChars={BodyChars}",
            _profile.Label,
            _profile.Endpoint,
            _accountId,
            _model,
            bufferedMessages.Count,
            toolCount,
            json.Length);

        using var response = await ChatHttpRequests.SendAsync(_httpClient, () => CreateResponsesRequest(json), cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var redactedErrorBody = LogRedaction.RedactJson(errorBody);
            if ((int)response.StatusCode is 400 or 422 && !InitialInstructionsRequired.ContainsKey(CacheCapabilityKey) && RejectsContextPlacement(errorBody))
                throw new ContextPlacementRejectedException();
            if ((int)response.StatusCode is 400 or 422 && ExplicitCache && RejectsCache(errorBody))
                throw new CacheControlRejectedException();
            _logger.LogError(
                "{Transport} API error {StatusCode}: {Body}",
                _profile.Label,
                (int)response.StatusCode,
                redactedErrorBody);
            _logger.LogDebug(
                "{Transport} API error response body: {Body}",
                _profile.Label,
                redactedErrorBody);
            throw await MapAsync(_profile.MapHttpError(
                (int)response.StatusCode,
                redactedErrorBody,
                $"{_profile.Label} API returned {(int)response.StatusCode}: {redactedErrorBody}"));
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? line;
        string? pendingCallId = null;
        string? pendingFuncName = null;
        var pendingArgs = new StringBuilder();
        var eventCount = 0;
        var outputTextChars = 0;
        var functionCallCount = 0;
        string? lastEventType = null;
        var lastEventData = string.Empty;
        var nativeReasoning = new List<JsonElement>();
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [protocol] };

        while (true)
        {
            try
            {
                line = await reader.ReadLineAsync(cancellationToken);
            }
            catch (HttpIOException ex) when (IsResponseEnded(ex) && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    ex,
                    "{Transport} streaming response ended prematurely after {EventCount} SSE events; lastEvent={LastEventType}; textChars={TextChars}; functionCalls={FunctionCallCount}; model={Model}; bodyChars={BodyChars}",
                    _profile.Label,
                    eventCount,
                    lastEventType ?? "(none)",
                    outputTextChars,
                    functionCallCount,
                    _model,
                    json.Length);
                throw;
            }

            if (line is null) break;

            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            var data = line["data: ".Length..];
            if (data == "[DONE]")
                break;
            lastEventData = data;

            JsonElement evt;
            try
            {
                evt = JsonSerializer.Deserialize<JsonElement>(data);
            }
            catch
            {
                continue;
            }

            var type = evt.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
            eventCount++;
            lastEventType = type ?? "(missing)";

            switch (type)
            {
                case "response.output_text.delta":
                    if (evt.TryGetProperty("delta", out var delta))
                    {
                        var text = delta.GetString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            outputTextChars += text.Length;
                            yield return new ChatResponseUpdate
                            {
                                Role = ChatRole.Assistant,
                                Contents = [new TextContent(text)]
                            };
                        }
                    }
                    break;

                case "response.reasoning_summary_text.delta":
                    if (evt.TryGetProperty("delta", out var reasoningDelta))
                    {
                        var reasoningText = reasoningDelta.GetString();
                        if (!string.IsNullOrEmpty(reasoningText))
                        {
                            yield return new ChatResponseUpdate
                            {
                                Role = ChatRole.Assistant,
                                Contents = [new TextReasoningContent(reasoningText)]
                            };
                        }
                    }
                    break;

                case "response.output_item.added":
                    if (evt.TryGetProperty("item", out var item) &&
                        item.TryGetProperty("type", out var itemType) &&
                        itemType.GetString() == "function_call")
                    {
                        pendingCallId = item.TryGetProperty("call_id", out var cid) ? cid.GetString() : null;
                        pendingFuncName = item.TryGetProperty("name", out var fname) ? fname.GetString() : null;
                        pendingArgs.Clear();
                        if (pendingCallId is not null && pendingFuncName is not null)
                        {
                            yield return new ChatResponseUpdate
                            {
                                Role = ChatRole.Assistant,
                                Contents = [new FunctionCallStartedContent(pendingCallId, pendingFuncName)]
                            };
                        }
                    }
                    break;

                case "response.function_call_arguments.delta":
                    if (evt.TryGetProperty("delta", out var argDelta))
                    {
                        var argumentsDelta = argDelta.GetString();
                        if (!string.IsNullOrEmpty(argumentsDelta))
                        {
                            pendingArgs.Append(argumentsDelta);
                            if (pendingCallId is not null && pendingFuncName is not null)
                            {
                                yield return new ChatResponseUpdate
                                {
                                    Role = ChatRole.Assistant,
                                    Contents = [new FunctionCallArgumentsDeltaContent(pendingCallId, pendingFuncName, argumentsDelta)]
                                };
                            }
                        }
                    }
                    break;

                case "response.function_call_arguments.done":
                    if (pendingCallId is not null && pendingFuncName is not null)
                    {
                        functionCallCount++;
                        var argsJson = evt.TryGetProperty("arguments", out var doneArguments)
                            ? doneArguments.GetString() ?? pendingArgs.ToString()
                            : pendingArgs.ToString();
                        IDictionary<string, object?>? argsDict = null;
                        if (!string.IsNullOrEmpty(argsJson) && argsJson != "{}")
                        {
                            try
                            {
                                argsDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson);
                            }
                            catch
                            {
                                argsDict = new Dictionary<string, object?> { ["raw"] = argsJson };
                            }
                        }

                        yield return new ChatResponseUpdate
                        {
                            Role = ChatRole.Assistant,
                            Contents = [new FunctionCallContent(pendingCallId, pendingFuncName, argsDict)]
                        };

                        pendingCallId = null;
                        pendingFuncName = null;
                        pendingArgs.Clear();
                    }
                    break;

                case "response.output_item.done":
                    if (evt.TryGetProperty("item", out var completedItem) && OptionalString(completedItem, "type") == "reasoning")
                    {
                        nativeReasoning.Add(completedItem.Clone());
                        metadata.ReasoningFields["responses.reasoning"] = JsonSerializer.SerializeToElement(nativeReasoning);
                    }
                    break;

                case "response.completed" or "response.done" or "response.incomplete":
                    var terminal = new ChatResponseUpdate { Role = ChatRole.Assistant };
                    var terminalResponse = evt.TryGetProperty("response", out var nestedResponse) ? nestedResponse : evt;
                    var incomplete = terminalResponse.TryGetProperty("status", out var terminalStatus)
                        && terminalStatus.GetString() == "incomplete";
                    var incompleteReason = terminalResponse.TryGetProperty("incomplete_details", out var incompleteDetails)
                        && incompleteDetails.ValueKind == JsonValueKind.Object
                        && incompleteDetails.TryGetProperty("reason", out var reason) ? reason.GetString() : null;
                    terminal.FinishReason = incompleteReason == "max_output_tokens"
                        ? ChatFinishReason.Length
                        : incomplete ? new ChatFinishReason(incompleteReason ?? "incomplete")
                        : functionCallCount > 0 ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop;
                    if (terminalResponse.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                    {
                        var details = NativeChatUsage.Responses(usage);
                        terminal.Contents.Add(new UsageContent(details));
                    }
                    metadata.FinishReason = terminal.FinishReason?.ToString();
                    metadata.Incomplete = incomplete;
                    terminal.Contents.Add(protocol);
                    yield return terminal;
                    _logger.LogDebug(
                        "{Transport} streaming response completed after {EventCount} SSE events. TextChars={TextChars}, FunctionCalls={FunctionCallCount}",
                        _profile.Label,
                        eventCount,
                        outputTextChars,
                        functionCallCount);
                    yield break;

                case "response.created":
                case "response.in_progress":
                case "response.content_part.added":
                case "response.content_part.done":
                case "response.output_text.done":
                case "response.reasoning_summary_part.added":
                case "response.reasoning_summary_part.done":
                case "response.reasoning_summary_text.done":
                case "keepalive":
                    break;

                case "response.failed":
                    var responseFailedMessage = ExtractResponseErrorMessage(
                        evt,
                        $"{_profile.Label} API response failed");
                    var (failedCode, failedParam) = ResponsesTransportProfile.ReadStreamErrorIdentifiers(evt);
                    _logger.LogError(
                        "{Transport} streaming response failed after {EventCount} SSE events; lastEvent={LastEventType}; textChars={TextChars}; functionCalls={FunctionCallCount}; model={Model}; bodyChars={BodyChars}; event={EventData}",
                        _profile.Label,
                        eventCount,
                        lastEventType ?? "(none)",
                        outputTextChars,
                        functionCallCount,
                        _model,
                        json.Length,
                        LogRedaction.RedactJson(Truncate(lastEventData, 2000)));
                    if (!InitialInstructionsRequired.ContainsKey(CacheCapabilityKey) && RejectsContextPlacement(lastEventData))
                        throw new ContextPlacementRejectedException();
                    if (ExplicitCache && RejectsCache(lastEventData)) throw new CacheControlRejectedException();
                    throw await MapAsync(_profile.MapStreamError(failedCode, failedParam, responseFailedMessage));

                case "error":
                    var errorMessage = ExtractResponseErrorMessage(evt, "Unknown error");
                    var (errorCode, errorParam) = ResponsesTransportProfile.ReadStreamErrorIdentifiers(evt);
                    _logger.LogError(
                        "{Transport} streaming error after {EventCount} SSE events; lastEvent={LastEventType}; textChars={TextChars}; functionCalls={FunctionCallCount}; model={Model}; bodyChars={BodyChars}; event={EventData}",
                        _profile.Label,
                        eventCount,
                        lastEventType ?? "(none)",
                        outputTextChars,
                        functionCallCount,
                        _model,
                        json.Length,
                        LogRedaction.RedactJson(Truncate(lastEventData, 2000)));
                    if (!InitialInstructionsRequired.ContainsKey(CacheCapabilityKey) && RejectsContextPlacement(lastEventData))
                        throw new ContextPlacementRejectedException();
                    if (ExplicitCache && RejectsCache(lastEventData)) throw new CacheControlRejectedException();
                    throw await MapAsync(_profile.MapStreamError(
                        errorCode,
                        errorParam,
                        $"{_profile.Label} error: {errorMessage}"));

                default:
                    _logger.LogWarning(
                        "Unhandled {Transport} SSE event type: {EventType}",
                        _profile.Label,
                        type);
                    break;
            }
        }

        _logger.LogDebug(
            "{Transport} streaming response ended after {EventCount} SSE events without an explicit completion event. TextChars={TextChars}, FunctionCalls={FunctionCallCount}",
            _profile.Label,
            eventCount,
            outputTextChars,
            functionCallCount);
        throw new HttpRequestException($"{_profile.Label} stream ended before completion. Partial output is retained.");
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && _profile.Kind is not null && serviceType == typeof(IModelWebSearch) ? this : null;

    public void Dispose() { }

    private HttpRequestMessage CreateResponsesRequest(string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _profile.Endpoint);
        request.Content = new StringContent(json, Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        _profile.ApplyHeaders(request, _accessToken, _accountId);
        return request;
    }

    private async Task<Exception> MapAsync(Exception exception)
    {
        if (exception is ChatGptPlanException { IsNotEligible: true } && _onPlanNotEligible is not null)
        {
            try
            {
                await _onPlanNotEligible(CancellationToken.None);
            }
            catch (Exception callbackException)
            {
                _logger.LogWarning(
                    callbackException,
                    "Could not record that ChatGPT plan usage is ineligible for this account.");
            }
        }

        return exception;
    }

    private static bool IsResponseEnded(HttpIOException exception) =>
        exception.HttpRequestError == HttpRequestError.ResponseEnded;

    private static string ExtractResponseErrorMessage(JsonElement evt, string fallback)
    {
        foreach (var path in new[]
        {
            new[] { "message" },
            new[] { "detail" },
            new[] { "error", "message" },
            new[] { "response", "error", "message" },
            new[] { "error", "code" },
            new[] { "response", "error", "code" },
        })
        {
            if (TryGetStringByPath(evt, path, out var value))
                return value;
        }

        return fallback;
    }

    private static bool TryGetStringByPath(JsonElement element, IReadOnlyList<string> path, out string value)
    {
        value = string.Empty;
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return false;
        }

        value = current.ValueKind == JsonValueKind.String ? current.GetString() ?? string.Empty : current.GetRawText();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "...";
    }

    private static bool RejectsContextPlacement(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var message = ExtractResponseErrorMessage(document.RootElement, "");
            return (message.Contains("developer", StringComparison.OrdinalIgnoreCase) || message.Contains("system message", StringComparison.OrdinalIgnoreCase))
                && (message.Contains("not support", StringComparison.OrdinalIgnoreCase) || message.Contains("first", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("beginning", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException) { return false; }
    }

    private static bool RejectsCache(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var (_, param) = ResponsesTransportProfile.ReadStreamErrorIdentifiers(document.RootElement);
            if (param?.Contains("prompt_cache", StringComparison.Ordinal) is true) return true;
            if (param?.EndsWith(".output", StringComparison.Ordinal) is true
                && ExtractResponseErrorMessage(document.RootElement, "").Contains("string", StringComparison.OrdinalIgnoreCase)) return true;
            var message = ExtractResponseErrorMessage(document.RootElement, "");
            return message.Contains("prompt_cache", StringComparison.Ordinal)
                && (message.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("unknown", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("not supported", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("unrecognized", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException) { return false; }
    }

    private Dictionary<string, object> BuildRequestBody(IEnumerable<ChatMessage> chatMessages, ChatOptions? options)
    {
        var inputItems = new List<object>();
        var initialInstructions = new List<string>();
        foreach (var msg in chatMessages)
        {
            if (msg.Role == ChatRole.System && InitialInstructionsRequired.ContainsKey(CacheCapabilityKey))
            {
                initialInstructions.Add(msg.Text ?? "");
                continue;
            }
            var protocol = msg.Contents.OfType<ChatProtocolContent>().LastOrDefault()?.Snapshot();
            if (protocol?.Transport == ProviderChatProtocol.Responses
                && protocol.ReasoningFields.TryGetValue("responses.reasoning", out var reasoning))
                inputItems.AddRange(reasoning.EnumerateArray().Select(item => (object)item.Clone()));
            var calls = msg.Contents.OfType<FunctionCallContent>().ToList();
            var results = msg.Contents.OfType<FunctionResultContent>().ToList();
            if (calls.Count > 0)
            {
                if (!string.IsNullOrEmpty(msg.Text)) inputItems.Add(new { role = "assistant", content = new[] { new { type = "output_text", text = msg.Text } } });
                foreach (var call in calls)
                    inputItems.Add(new { type = "function_call", call_id = call.CallId, name = call.Name,
                        arguments = JsonSerializer.Serialize(call.Arguments ?? new Dictionary<string, object?>()) });
                continue;
            }
            if (results.Count > 0)
            {
                foreach (var result in results)
                {
                    var content = new Dictionary<string, object> { ["type"] = "input_text", ["text"] = result.Result?.ToString() ?? "" };
                    if (ExplicitCache && ChatRequestLayout.IsBoundary(msg) && result == results[^1])
                        content["prompt_cache_breakpoint"] = new { mode = "explicit" };
                    inputItems.Add(new { type = "function_call_output", call_id = result.CallId, output = ExplicitCache ? (object)new[] { content } : result.Result?.ToString() ?? "" });
                }
                continue;
            }
            if (msg.Role == ChatRole.System || msg.Role == ChatRole.User)
            {
                var content = BuildUserContent(msg).ToList();
                if (ExplicitCache && ChatRequestLayout.IsBoundary(msg) && content.LastOrDefault() is Dictionary<string, object> last)
                    last["prompt_cache_breakpoint"] = new { mode = "explicit" };
                inputItems.Add(new { role = msg.Role == ChatRole.System ? "developer" : "user", content });
            }
            else if (msg.Role == ChatRole.Assistant)
                inputItems.Add(new { role = "assistant", content = new[] { new { type = "output_text", text = msg.Text ?? "" } } });
        }
        var body = new Dictionary<string, object>
        {
            ["model"] = _model, ["stream"] = true, ["store"] = false,
            ["input"] = inputItems,
            ["instructions"] = initialInstructions.Count > 0 ? string.Join("\n\n", initialInstructions)
                : "You are Lorekeeper's assistant. Follow the supplied application instructions.",
            ["include"] = new[] { "reasoning.encrypted_content" },
        };
        if (ExplicitCache && chatMessages.Any(ChatRequestLayout.IsBoundary))
            body["prompt_cache_options"] = new { mode = "explicit" };
        if (_profile.Kind is null && (_provider?.MaxOutputTokens ?? options?.MaxOutputTokens) is > 0 and var limit)
            body["max_output_tokens"] = limit;

        if (_reasoningEffort is not null)
        {
            body["reasoning"] = new Dictionary<string, object>
            {
                ["effort"] = _reasoningEffort,
            };
        }

        // The ChatGPT Codex backend rejects the public Responses API's
        // max_output_tokens field. ChatOptions.MaxOutputTokens is therefore
        // intentionally not serialized on this OAuth request path.

        if (options?.Tools is { Count: > 0 } tools)
        {
            var responseTools = new List<object>();
            foreach (var tool in tools.OrderBy(tool => tool is AIFunction function ? function.Name : string.Empty, StringComparer.Ordinal))
            {
                if (tool is AIFunction func)
                {
                    var toolDef = new Dictionary<string, object>
                    {
                        ["type"] = "function",
                        ["name"] = func.Name,
                        ["description"] = func.Description ?? string.Empty,
                        ["strict"] = true,
                    };

                    var schema = func.JsonSchema;
                    if (schema.ValueKind != JsonValueKind.Undefined)
                    {
                        toolDef["parameters"] = PrepareStrictSchema(schema);
                    }
                    else
                    {
                        toolDef["parameters"] = new Dictionary<string, object>
                        {
                            ["type"] = "object",
                            ["properties"] = new Dictionary<string, object>(),
                            ["required"] = Array.Empty<string>(),
                            ["additionalProperties"] = false
                        };
                    }

                    responseTools.Add(toolDef);
                }
            }
            if (responseTools.Count > 0)
            {
                body["tools"] = responseTools;
                body["tool_choice"] = "auto";
            }
        }

        return body;
    }

    private static IReadOnlyList<object> BuildUserContent(ChatMessage message)
    {
        var content = new List<object>();
        var textParts = message.Contents.OfType<TextContent>()
            .Where(textContent => !string.IsNullOrEmpty(textContent.Text))
            .ToList();

        if (textParts.Count > 0)
        {
            foreach (var textPart in textParts)
            {
                content.Add(new Dictionary<string, object>
                {
                    ["type"] = "input_text",
                    ["text"] = textPart.Text!,
                });
            }
        }
        else if (!string.IsNullOrEmpty(message.Text))
        {
            content.Add(new Dictionary<string, object>
            {
                ["type"] = "input_text",
                ["text"] = message.Text,
            });
        }

        foreach (var image in message.Contents.OfType<DataContent>().Where(content => content.HasTopLevelMediaType("image")))
        {
            content.Add(new Dictionary<string, object>
            {
                ["type"] = "input_image",
                ["image_url"] = image.Uri,
            });
        }

        if (content.Count == 0)
        {
            content.Add(new Dictionary<string, object>
            {
                ["type"] = "input_text",
                ["text"] = string.Empty,
            });
        }

        return content;
    }

    private static JsonElement PrepareStrictSchema(JsonElement schema)
    {
        var prepared = EnforceStrictSchema(HoistSchemaReferences(schema));
        using var doc = JsonDocument.Parse(prepared.GetRawText());
        return doc.RootElement.Clone();
    }

    private static JsonElement HoistSchemaReferences(JsonElement schema)
    {
        var references = new Dictionary<string, string>(StringComparer.Ordinal);
        var definitions = new JsonObject();
        var root = JsonNode.Parse(Transform(schema).GetRawText())!.AsObject();
        if (definitions.Count > 0)
            root["$defs"] = definitions;
        return JsonSerializer.SerializeToElement(root);

        JsonElement Transform(JsonElement source) => AIJsonUtilities.TransformSchema(source, new()
        {
            TransformSchemaNode = (_, node) =>
            {
                if (node is not JsonObject obj)
                    return node;
                // Only referenced definitions are retained, all at the tool root.
                // The SDK traversal distinguishes schema maps from authored fields.
                obj.Remove("$defs");
                obj.Remove("definitions");
                if (obj["$ref"] is not JsonValue value || !value.TryGetValue<string>(out var reference))
                    return obj;
                if (reference == "#")
                    return obj;
                if (!references.TryGetValue(reference, out var name))
                {
                    var target = ResolveSchemaReference(schema, reference);
                    name = "ref" + references.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    // Register before descending so recursive types remain references.
                    references.Add(reference, name);
                    definitions[name] = JsonNode.Parse(Transform(target).GetRawText());
                }
                obj["$ref"] = "#/$defs/" + name;
                return obj;
            },
        });
    }

    private static JsonElement ResolveSchemaReference(JsonElement root, string reference)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal))
            throw new InvalidOperationException("Tool schemas must use local JSON-pointer references.");
        var target = root;
        foreach (var part in reference[2..].Split('/'))
        {
            var key = Uri.UnescapeDataString(part).Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            if (target.ValueKind == JsonValueKind.Object && target.TryGetProperty(key, out var property))
                target = property;
            else if (target.ValueKind == JsonValueKind.Array
                && int.TryParse(key, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var index)
                && index < target.GetArrayLength())
                target = target[index];
            else
                throw new InvalidOperationException("Tool schema contains an unresolved JSON-pointer reference.");
        }
        if (target.ValueKind is not (JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False))
            throw new InvalidOperationException("Tool schema reference does not identify a schema.");
        return target;
    }

    private static JsonElement EnforceStrictSchema(JsonElement element, bool isPropertiesContainer = false)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            var items = new List<JsonElement>();
            foreach (var item in element.EnumerateArray())
                items.Add(EnforceStrictSchema(item));
            return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(items));
        }

        if (element.ValueKind != JsonValueKind.Object)
            return element;

        if (isPropertiesContainer)
        {
            var properties = new Dictionary<string, object>();
            foreach (var prop in element.EnumerateObject())
            {
                properties[prop.Name] = prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                    ? EnforceStrictSchema(prop.Value)
                    : prop.Value;
            }

            return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(properties));
        }

        var dict = new Dictionary<string, object>();
        var isObject = false;
        var hasProperties = false;
        var hasAdditionalProperties = false;
        var propertyNames = new List<string>();

        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Name == "title")
                continue;

            if (prop.Name == "type" &&
                ((prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() == "object") ||
                 (prop.Value.ValueKind == JsonValueKind.Array && prop.Value.EnumerateArray().Any(e => e.GetString() == "object"))))
                isObject = true;
            if (prop.Name == "properties")
            {
                hasProperties = true;
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in prop.Value.EnumerateObject())
                        propertyNames.Add(p.Name);
                }
            }
            if (prop.Name == "additionalProperties")
                hasAdditionalProperties = true;

            var recurseAsProperties = prop.Name is "properties" or "$defs";
            dict[prop.Name] = prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? EnforceStrictSchema(prop.Value, isPropertiesContainer: recurseAsProperties)
                : prop.Value;
        }

        if ((isObject || hasProperties) && !hasAdditionalProperties)
            dict["additionalProperties"] = false;

        if (isObject || hasProperties)
            dict["required"] = propertyNames;

        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(dict));
    }
}
