using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Lorekeeper.Llm;

/// <summary>
/// IChatClient implementation that calls the Codex Responses API at
/// chatgpt.com/backend-api/codex/responses using an OAuth token.
/// </summary>
public sealed class CodexChatClient : IChatClient
{
    private const string CodexEndpoint = "https://chatgpt.com/backend-api/codex/responses";
    private const string DefaultModel = "gpt-5.4-mini";
    private const int MaxBufferedResponseAttempts = 2;

    private readonly HttpClient _httpClient;
    private readonly string _accessToken;
    private readonly string _model;
    private readonly string _accountId;
    private readonly ILogger<CodexChatClient> _logger;

    public CodexChatClient(HttpClient httpClient, string accessToken, string model, ILogger<CodexChatClient> logger)
    {
        _httpClient = httpClient;
        _accessToken = accessToken;
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model;
        _accountId = ExtractAccountId(accessToken);
        _logger = logger;
    }

    public ChatClientMetadata Metadata => new("CodexResponsesAPI", new Uri("https://chatgpt.com"));

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

            try
            {
                await foreach (var update in GetStreamingResponseAsync(bufferedMessages, options, cancellationToken))
                {
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
                    "Codex streaming response ended before assistant content on attempt {Attempt}; retrying once.",
                    attempt);
                continue;
            }
            catch (HttpIOException ex) when (IsResponseEnded(ex) && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    ex,
                    "Codex streaming response ended after partial assistant content; not retrying. TextChars={TextChars}, FunctionCalls={FunctionCallCount}",
                    fullText.Length,
                    functionCalls.Count);
                throw;
            }

            return CreateResponse(fullText, functionCalls);
        }

        throw new HttpRequestException("Codex streaming response ended prematurely before assistant content after retry.");
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
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var bufferedMessages = chatMessages as IReadOnlyCollection<ChatMessage> ?? chatMessages.ToList();
        var body = BuildRequestBody(bufferedMessages, options);
        var json = JsonSerializer.Serialize(body);
        var toolCount = options?.Tools?.Count ?? 0;

        _logger.LogDebug("Codex request body: {Body}", json);

        using var request = new HttpRequestMessage(HttpMethod.Post, CodexEndpoint);
        request.Content = new StringContent(json, Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        request.Headers.TryAddWithoutValidation("chatgpt-account-id", _accountId);
        request.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=experimental");
        request.Headers.TryAddWithoutValidation("originator", "pi");
        request.Headers.TryAddWithoutValidation("User-Agent", "Lorekeeper");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        _logger.LogDebug(
            "Codex request: POST {Endpoint}, account={AccountId}, model={Model}, messages={MessageCount}, tools={ToolCount}, bodyChars={BodyChars}",
            CodexEndpoint,
            _accountId,
            _model,
            bufferedMessages.Count,
            toolCount,
            json.Length);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Codex API error {StatusCode}: {Body}", (int)response.StatusCode, errorBody);
            throw new HttpRequestException($"Codex API returned {(int)response.StatusCode}: {errorBody}");
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
                    "Codex streaming response ended prematurely after {EventCount} SSE events; lastEvent={LastEventType}; textChars={TextChars}; functionCalls={FunctionCallCount}; model={Model}; bodyChars={BodyChars}",
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
                                Contents = [new ReasoningContent(reasoningText)]
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
                    }
                    break;

                case "response.function_call_arguments.delta":
                    if (evt.TryGetProperty("delta", out var argDelta))
                        pendingArgs.Append(argDelta.GetString());
                    break;

                case "response.function_call_arguments.done":
                    if (pendingCallId is not null && pendingFuncName is not null)
                    {
                        functionCallCount++;
                        var argsJson = pendingArgs.ToString();
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

                case "response.completed" or "response.done":
                    _logger.LogDebug(
                        "Codex streaming response completed after {EventCount} SSE events. TextChars={TextChars}, FunctionCalls={FunctionCallCount}",
                        eventCount,
                        outputTextChars,
                        functionCallCount);
                    yield break;

                case "response.created":
                case "response.in_progress":
                case "response.content_part.added":
                case "response.content_part.done":
                case "response.output_item.done":
                case "response.output_text.done":
                case "response.reasoning_summary_part.added":
                case "response.reasoning_summary_part.done":
                case "response.reasoning_summary_text.done":
                case "keepalive":
                    break;

                case "response.failed":
                    var errorMsg = "Codex API error";
                    if (evt.TryGetProperty("response", out var resp) &&
                        resp.TryGetProperty("error", out var err) &&
                        err.TryGetProperty("message", out var msg))
                    {
                        errorMsg = msg.GetString() ?? errorMsg;
                    }
                    throw new InvalidOperationException(errorMsg);

                case "error":
                    var errMsg = evt.TryGetProperty("message", out var m) ? m.GetString() : "Unknown error";
                    throw new InvalidOperationException($"Codex error: {errMsg}");

                default:
                    _logger.LogWarning("Unhandled Codex SSE event type: {EventType}", type);
                    break;
            }
        }

        _logger.LogDebug(
            "Codex streaming response ended after {EventCount} SSE events without an explicit completion event. TextChars={TextChars}, FunctionCalls={FunctionCallCount}",
            eventCount,
            outputTextChars,
            functionCallCount);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

    private static bool IsResponseEnded(HttpIOException exception) =>
        exception.HttpRequestError == HttpRequestError.ResponseEnded;

    private Dictionary<string, object> BuildRequestBody(IEnumerable<ChatMessage> chatMessages, ChatOptions? options)
    {
        var instructions = new List<string>();
        var inputItems = new List<object>();

        foreach (var msg in chatMessages)
        {
            if (msg.Role == ChatRole.System)
            {
                instructions.Add(msg.Text ?? string.Empty);
                continue;
            }

            var functionCalls = msg.Contents.OfType<FunctionCallContent>().ToList();
            if (functionCalls.Count > 0)
            {
                var textParts = msg.Contents.OfType<TextContent>()
                    .Where(tc => tc.Text is { Length: > 0 })
                    .ToList();
                if (textParts.Count > 0)
                {
                    var combinedText = string.Join("", textParts.Select(tc => tc.Text));
                    inputItems.Add(new Dictionary<string, object>
                    {
                        ["role"] = "assistant",
                        ["content"] = new[] { new { type = "output_text", text = combinedText } }
                    });
                }

                foreach (var fc in functionCalls)
                {
                    inputItems.Add(new Dictionary<string, object>
                    {
                        ["type"] = "function_call",
                        ["call_id"] = fc.CallId ?? "",
                        ["name"] = fc.Name,
                        ["arguments"] = fc.Arguments is not null
                            ? JsonSerializer.Serialize(fc.Arguments)
                            : "{}"
                    });
                }
                continue;
            }

            var functionResults = msg.Contents.OfType<FunctionResultContent>().ToList();
            if (functionResults.Count > 0)
            {
                foreach (var fr in functionResults)
                {
                    inputItems.Add(new Dictionary<string, object>
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = fr.CallId ?? "",
                        ["output"] = fr.Result?.ToString() ?? ""
                    });
                }
                continue;
            }

            var text = msg.Text ?? string.Empty;
            if (msg.Role == ChatRole.User)
            {
                inputItems.Add(new Dictionary<string, object>
                {
                    ["role"] = "user",
                    ["content"] = new[] { new { type = "input_text", text } }
                });
            }
            else if (msg.Role == ChatRole.Assistant)
            {
                inputItems.Add(new Dictionary<string, object>
                {
                    ["role"] = "assistant",
                    ["content"] = new[] { new { type = "output_text", text } }
                });
            }
        }

        var body = new Dictionary<string, object>
        {
            ["model"] = _model,
            ["stream"] = true,
            ["store"] = false,
            ["input"] = inputItems,
            ["instructions"] = instructions.Count > 0
                ? string.Join("\n\n", instructions)
                : "You are a helpful assistant."
        };

        if (options?.Tools is { Count: > 0 } tools)
        {
            var codexTools = new List<object>();
            foreach (var tool in tools)
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
                            ["additionalProperties"] = false
                        };
                    }

                    codexTools.Add(toolDef);
                }
            }
            if (codexTools.Count > 0)
            {
                body["tools"] = codexTools;
                body["tool_choice"] = "auto";
            }
        }

        return body;
    }

    private static JsonElement PrepareStrictSchema(JsonElement schema)
    {
        var prepared = EnforceStrictSchema(schema);
        using var doc = JsonDocument.Parse(prepared.GetRawText());
        return doc.RootElement.Clone();
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

        var dict = new Dictionary<string, object>();
        var isObject = false;
        var hasProperties = false;
        var hasAdditionalProperties = false;
        var hasRequired = false;
        var propertyNames = new List<string>();

        foreach (var prop in element.EnumerateObject())
        {
            if (!isPropertiesContainer && (prop.Name == "$defs" || prop.Name == "title"))
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
            if (prop.Name == "required")
                hasRequired = true;

            var recurseAsProperties = prop.Name == "properties";
            dict[prop.Name] = prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? EnforceStrictSchema(prop.Value, isPropertiesContainer: recurseAsProperties)
                : prop.Value;
        }

        if ((isObject || hasProperties) && !hasAdditionalProperties)
            dict["additionalProperties"] = false;

        if ((isObject || hasProperties) && !hasRequired && propertyNames.Count > 0)
            dict["required"] = propertyNames;

        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(dict));
    }

    private static string ExtractAccountId(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
            throw new InvalidOperationException("OAuth token is not a valid JWT.");

        var payload = parts[1];
        payload += new string('=', (4 - payload.Length % 4) % 4);
        var decoded = Convert.FromBase64String(payload.Replace('-', '+').Replace('_', '/'));
        var json = JsonSerializer.Deserialize<JsonElement>(decoded);

        if (json.TryGetProperty("https://api.openai.com/auth", out var authClaim) &&
            authClaim.TryGetProperty("chatgpt_account_id", out var accountId))
        {
            return accountId.GetString() ?? throw new InvalidOperationException("chatgpt_account_id is null in token.");
        }

        throw new InvalidOperationException("OAuth token does not contain chatgpt_account_id. Make sure you signed in with your OpenAI account.");
    }
}
