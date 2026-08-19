using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Lorekeeper.WireRepro;

/// <summary>
/// Raw SSE chat-completions client that bypasses the OpenAI SDK's stream
/// deserialization entirely. Unknown finish_reason values, mid-stream
/// truncations, and gateway error payloads surface as observable data instead
/// of SDK exceptions.
/// </summary>
public sealed record SseResult(
    string Scenario,
    int StatusCode,
    long FirstByteMs,
    long LastByteMs,
    long TotalMs,
    int Chunks,
    int TextChars,
    int ReasoningChars,
    int ToolCallDeltas,
    string? FinishReason,
    string? TextSample,
    IReadOnlyList<string> Events,
    string? TransportError)
{
    public bool HasTransportError => TransportError is not null;
}

public sealed record SseRequest(
    string EndpointUrl,
    string ApiKey,
    string ModelId,
    string? SystemPrompt,
    string UserPrompt,
    int ToolCount,
    string? ReasoningEffort,
    int? MaxOutputTokens,
    string MaxTokensField = "max_completion_tokens",
    bool RealisticToolSchemas = false)
{
    public string Scenario { get; set; } = "unnamed";
}

public static class RawSse
{
    public static async Task<SseResult> SendAsync(SseRequest request, CancellationToken cancellationToken = default)
    {
        var tools = new JsonArray();
        for (var i = 1; i <= request.ToolCount; i++)
        {
            tools.Add(request.RealisticToolSchemas
                ? BuildRealisticTool(i)
                : BuildCompactTool(i));
        }

        var messages = new JsonArray();
        if (request.SystemPrompt is not null)
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt });
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = request.UserPrompt });

        var body = new JsonObject
        {
            ["model"] = request.ModelId,
            ["messages"] = messages,
            ["stream"] = true,
        };
        if (tools.Count > 0)
        {
            body["tools"] = tools;
            body["tool_choice"] = "auto";
        }
        if (request.ReasoningEffort is not null)
            body["reasoning_effort"] = request.ReasoningEffort;
        if (request.MaxOutputTokens is { } budget)
            body[request.MaxTokensField] = budget;

        var json = body.ToJsonString();

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(11) };
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{request.EndpointUrl.TrimEnd('/')}/chat/completions");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);
        httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var events = new List<string>();
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception ex)
        {
            return new SseResult(request.Scenario, 0, -1, -1, stopwatch.ElapsedMilliseconds, 0, 0, 0, 0, null, null, events, $"SEND: {ex.GetType().Name}: {ex.Message}");
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            events.Add($"HTTP-{(int)response.StatusCode}-BODY: {Truncate(errorBody, 500)}");
            return new SseResult(request.Scenario, (int)response.StatusCode, -1, -1, stopwatch.ElapsedMilliseconds, 0, 0, 0, 0, null, null, events, null);
        }

        var textChars = 0;
        var reasoningChars = 0;
        var toolCallDeltas = 0;
        var chunks = 0;
        string? finishReason = null;
        long? firstByteAt = null;
        long? lastByteAt = null;
        var text = new StringBuilder();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        string? line;
        try
        {
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            if (line.Length == 0) continue;
            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                events.Add(line);
                continue;
            }
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]") { lastByteAt = stopwatch.ElapsedMilliseconds; break; }

            firstByteAt ??= stopwatch.ElapsedMilliseconds;
            lastByteAt = stopwatch.ElapsedMilliseconds;

            if (payload.Length < 2 || payload[0] != '{')
            {
                events.Add($"NONJSON-DATA: {Truncate(payload, 200)}");
                continue;
            }

            try
            {
                var doc = JsonNode.Parse(payload)?.AsObject();
                if (doc is null)
                {
                    events.Add($"UNPARSEABLE: {Truncate(payload, 200)}");
                    continue;
                }

                if (doc["error"] is { } streamError)
                {
                    events.Add($"STREAM-ERROR: {Truncate(streamError.ToJsonString(), 500)}");
                    continue;
                }

                if (doc["choices"] is not JsonArray choices)
                {
                    events.Add($"NO-CHOICES: {Truncate(doc.ToJsonString(), 300)}");
                    continue;
                }

                foreach (var choice in choices)
                {
                    if (choice is null) continue;
                    chunks++;
                    if (choice["finish_reason"] is { } fr && fr.GetValue<string>() is { Length: > 0 } reason)
                        finishReason = reason;
                    if (choice["delta"] is { } delta)
                    {
                        if (delta["content"] is { } content)
                        {
                            var t = content.GetValue<string>();
                            textChars += t.Length;
                            text.Append(t);
                        }
                        if (delta["reasoning_content"] is { } reasoningContent)
                        {
                            reasoningChars += reasoningContent.GetValue<string>().Length;
                        }
                        else if (delta["reasoning"] is { } reasoningAlt)
                        {
                            reasoningChars += reasoningAlt.GetValue<string>().Length;
                        }
                        if (delta["tool_calls"] is JsonArray toolCalls)
                            toolCallDeltas += toolCalls.Count;
                    }
                }
            }
            catch (Exception ex)
            {
                events.Add($"PARSE-FAIL({ex.GetType().Name}): {Truncate(payload, 200)}");
            }
        }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Stream-read failure (premature end, connection reset, read timeout).
            // Classified as a transport error so scenarios can record it instead of
            // throwing; this is the Qwen failure signature.
            events.Add($"READ-FAIL at {stopwatch.ElapsedMilliseconds}ms after {chunks} chunks: {ex.GetType().Name}: {ex.Message}");
            return new SseResult(
                request.Scenario,
                (int)response.StatusCode,
                firstByteAt ?? -1,
                lastByteAt ?? -1,
                stopwatch.ElapsedMilliseconds,
                chunks,
                textChars,
                reasoningChars,
                toolCallDeltas,
                finishReason,
                Truncate(text.ToString(), 300),
                events,
                $"{ex.GetType().Name}: {ex.Message}");
        }

        return new SseResult(
            request.Scenario,
            (int)response.StatusCode,
            firstByteAt ?? -1,
            lastByteAt ?? -1,
            stopwatch.ElapsedMilliseconds,
            chunks,
            textChars,
            reasoningChars,
            toolCallDeltas,
            finishReason,
            Truncate(text.ToString(), 300),
            events,
            null);
    }

    private static JsonNode BuildCompactTool(int i) =>
        new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = $"manuscript_tool_{i}",
                ["description"] = $"Editor manuscript tool {i}: validates and applies chapter {i} operations with revision checks.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["chapterId"] = new JsonObject { ["type"] = "string", ["description"] = "Chapter identifier" },
                        ["text"] = new JsonObject { ["type"] = "string", ["description"] = "Manuscript text to apply" },
                    },
                    ["required"] = new JsonArray("chapterId", "text"),
                },
            },
        };

    /// <summary>
    /// A tool definition sized like Lorekeeper's real Editor tools: long
    /// multi-paragraph description, nested schema with enums, defaults, and
    /// union types (~450 tokens each vs ~50 for the compact form).
    /// </summary>
    private static JsonNode BuildRealisticTool(int i) =>
        new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = $"manuscript_tool_{i}",
                ["description"] = $"Applies a validated semantic operation batch to chapter {i} of the manuscript. Each operation targets a stable block ID from the current manuscript snapshot included in the context. Operations are applied atomically: if any operation fails validation the entire batch is rejected and no changes are persisted. Supported operations include inserting paragraphs with optional Book Text Style roles, replacing paragraph text while preserving inline marks, splitting or merging blocks at explicit offsets, setting scene breaks, and updating Figure metadata including crop positioning and alternative text. All edits are recorded as a reviewable pending change with before/after snapshots and dependency diagnostics. The tool returns the new manuscript revision, the list of affected block IDs, and any warnings about style roles or geometry that require author attention. Prefer several small focused batches over one large batch so failures stay reviewable.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["chapterId"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Stable chapter identifier from the context.",
                        },
                        ["expectedRevision"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "Current manuscript revision for optimistic concurrency; rejects stale writes.",
                        },
                        ["operations"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["description"] = "Ordered semantic operations applied as one atomic batch.",
                            ["items"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["op"] = new JsonObject
                                    {
                                        ["type"] = "string",
                                        ["enum"] = new JsonArray("insertParagraph", "replaceText", "splitBlock", "mergeBlocks", "setSceneBreak", "updateFigure"),
                                    },
                                    ["blockId"] = new JsonObject { ["type"] = "string", ["description"] = "Target block for replace/split/merge/update operations." },
                                    ["afterBlockId"] = new JsonObject { ["type"] = "string", ["description"] = "Insertion anchor for insertParagraph." },
                                    ["text"] = new JsonObject { ["type"] = "string", ["description"] = "Plain or marked text for text-bearing operations." },
                                    ["styleRole"] = new JsonObject
                                    {
                                        ["type"] = new JsonArray("string", "null"),
                                        ["enum"] = new JsonArray("body", "dialogue", "letter", "transcript", "ornamental", "epigraph", null),
                                        ["default"] = null,
                                    },
                                    ["offset"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["description"] = "Character offset for split operations." },
                                },
                                ["required"] = new JsonArray("op"),
                            },
                        },
                    },
                    ["required"] = new JsonArray("chapterId", "expectedRevision", "operations"),
                },
            },
        };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
