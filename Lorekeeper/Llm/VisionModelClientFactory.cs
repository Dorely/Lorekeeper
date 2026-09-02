using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Lorekeeper.Diagnostics;
using Lorekeeper.Models;
using SkiaSharp;

namespace Lorekeeper.Llm;

public sealed class VisionModelClientFactory(
    ILlmProviderService providerService,
    IHttpClientFactory httpClientFactory,
    ILogger<VisionModelClientFactory> logger) : IVisionModelClientFactory
{
    private const string VisionProbeCode = "LK7";

    public async Task<string> ReadImageAsync(
        int providerId,
        byte[] imageBytes,
        string mediaType,
        string prompt,
        int maxOutputTokens = 2048,
        CancellationToken cancellationToken = default)
    {
        var provider = await providerService.GetByIdAsync(providerId, cancellationToken)
            ?? throw new InvalidOperationException($"Provider {providerId} not found.");

        return await ReadImageAsync(provider, imageBytes, mediaType, prompt, maxOutputTokens, cancellationToken);
    }

    public async Task<string> ReadImageAsync(
        LlmProvider provider,
        byte[] imageBytes,
        string mediaType,
        string prompt,
        int maxOutputTokens = 2048,
        CancellationToken cancellationToken = default)
    {
        if (imageBytes.Length == 0)
            throw new ArgumentException("Image bytes are required.", nameof(imageBytes));
        if (string.IsNullOrWhiteSpace(mediaType))
            throw new ArgumentException("Image media type is required.", nameof(mediaType));
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Prompt is required.", nameof(prompt));

        var (apiKey, effectiveAuthType) = await LlmConnectionResolver.ResolveAsync(providerService, provider, cancellationToken);
        var httpClient = httpClientFactory.CreateClient();
        httpClient.Timeout = TimeSpan.FromMinutes(5);

        if (effectiveAuthType == AuthType.OAuth && apiKey is not null && IsJwt(apiKey))
        {
            return await ReadCodexImageAsync(
                httpClient,
                apiKey,
                provider.ModelId,
                provider.ReasoningEffort,
                imageBytes,
                mediaType,
                prompt,
                maxOutputTokens,
                cancellationToken);
        }

        if (effectiveAuthType != AuthType.None && apiKey is null)
            throw new InvalidOperationException($"No valid API key or token for provider '{provider.Name}'.");

        return await ReadOpenAiCompatibleImageAsync(
            httpClient,
            provider,
            apiKey,
            effectiveAuthType,
            imageBytes,
            mediaType,
            prompt,
            maxOutputTokens,
            cancellationToken);
    }

    public async Task TestVisionModelAsync(int providerId, CancellationToken cancellationToken = default)
    {
        var provider = await providerService.GetByIdAsync(providerId, cancellationToken)
            ?? throw new InvalidOperationException($"Provider {providerId} not found.");

        await TestVisionModelAsync(provider, cancellationToken);
    }

    public async Task TestVisionModelAsync(LlmProvider provider, CancellationToken cancellationToken = default)
    {
        var imageBytes = CreateVisionProbeImage();
        var response = await ReadImageAsync(
            provider,
            imageBytes,
            "image/png",
            $"Return only the exact three-character code shown in this image. The code contains letters and digits. Do not add punctuation, spaces, or explanation.",
            maxOutputTokens: 512,
            cancellationToken);

        var normalized = NormalizeProbeResponse(response);
        if (!string.Equals(normalized, VisionProbeCode, StringComparison.Ordinal))
            throw new InvalidOperationException($"Vision probe expected {VisionProbeCode}, but the model returned '{Truncate(response, 80)}'.");
    }

    private async Task<string> ReadCodexImageAsync(
        HttpClient httpClient,
        string accessToken,
        string model,
        LlmReasoningEffort? reasoningEffort,
        byte[] imageBytes,
        string mediaType,
        string prompt,
        int maxOutputTokens,
        CancellationToken cancellationToken)
    {
        var accountId = CodexProvider.ExtractAccountId(accessToken);
        var content = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["type"] = "input_text",
                ["text"] = prompt,
            },
            new()
            {
                ["type"] = "input_image",
                ["image_url"] = ToDataUrl(imageBytes, mediaType),
            },
        };

        var body = new Dictionary<string, object?>
        {
            ["model"] = string.IsNullOrWhiteSpace(model) ? LlmProviderCatalog.OpenAiDefaultMainlineModel : model,
            ["stream"] = true,
            ["store"] = false,
            ["instructions"] = "Read the supplied image accurately and answer the user's request.",
            ["input"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = content,
                },
            },
        };

        // Unlike the public Responses API, the ChatGPT Codex backend rejects
        // max_output_tokens. Keep the requested budget off this OAuth path.

        if (reasoningEffort is not null)
        {
            body["reasoning"] = new Dictionary<string, object?>
            {
                ["effort"] = reasoningEffort.Value.ToWireValue(),
            };
        }
        var json = JsonSerializer.Serialize(body);

        logger.LogDebug(
            "Codex vision request: POST {Endpoint}, account={AccountId}, model={Model}, promptChars={PromptChars}, imageBytes={ImageBytes}, mediaType={MediaType}, requestedMaxOutputTokens={MaxOutputTokens}, bodyChars={BodyChars}",
            CodexProvider.ResponsesEndpoint,
            accountId,
            body["model"],
            prompt.Length,
            imageBytes.Length,
            mediaType,
            Math.Clamp(maxOutputTokens, 1, 32_000),
            json.Length);

        using var request = new HttpRequestMessage(HttpMethod.Post, CodexProvider.ResponsesEndpoint);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("chatgpt-account-id", accountId);
        request.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=experimental");
        request.Headers.TryAddWithoutValidation("originator", "pi");
        request.Headers.TryAddWithoutValidation("User-Agent", "Lorekeeper");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var redactedErrorBody = LogRedaction.RedactJson(errorBody);
            logger.LogError("Codex vision API error {StatusCode}: {Body}", (int)response.StatusCode, redactedErrorBody);
            logger.LogDebug("Codex vision API error response body: {Body}", redactedErrorBody);
            throw new HttpRequestException(LlmErrorNormalizer.SummarizeHttpError("Codex vision request", (int)response.StatusCode, redactedErrorBody));
        }

        var result = new StringBuilder();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var eventCount = 0;
        var outputTextChars = 0;
        string? lastEventType = null;

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

            var data = line["data: ".Length..];
            if (data == "[DONE]") break;

            JsonElement evt;
            try
            {
                evt = JsonSerializer.Deserialize<JsonElement>(data);
            }
            catch (JsonException)
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
                            result.Append(text);
                        }
                    }
                    break;
                case "response.completed" or "response.done":
                    logger.LogDebug(
                        "Codex vision response completed after {EventCount} SSE events. TextChars={TextChars}",
                        eventCount,
                        outputTextChars);
                    return result.ToString().Trim();
                case "response.failed":
                    throw new InvalidOperationException(ReadCodexResponseError(evt) ?? "Codex vision request failed.");
                case "error":
                    var errMsg = evt.TryGetProperty("message", out var message)
                        ? message.GetString()
                        : "Unknown Codex vision error.";
                    throw new InvalidOperationException($"Codex vision error: {errMsg}");
                case "response.created":
                case "response.in_progress":
                case "response.content_part.added":
                case "response.content_part.done":
                case "response.output_item.added":
                case "response.output_item.done":
                case "response.output_text.done":
                case "keepalive":
                    break;
                default:
                    logger.LogDebug("Unhandled Codex vision SSE event type: {EventType}", type);
                    break;
            }
        }

        logger.LogDebug(
            "Codex vision stream ended after {EventCount} SSE events without explicit completion. LastEvent={LastEventType}; TextChars={TextChars}",
            eventCount,
            lastEventType ?? "(none)",
            outputTextChars);
        return result.ToString().Trim();
    }

    private async Task<string> ReadOpenAiCompatibleImageAsync(
        HttpClient httpClient,
        LlmProvider provider,
        string? apiKey,
        AuthType effectiveAuthType,
        byte[] imageBytes,
        string mediaType,
        string prompt,
        int maxOutputTokens,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildChatCompletionsEndpoint(provider.EndpointUrl));
        if (effectiveAuthType != AuthType.None && !string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var body = new Dictionary<string, object?>
        {
            ["model"] = provider.ModelId,
            ["messages"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = new object[]
                    {
                        new Dictionary<string, object?> { ["type"] = "text", ["text"] = prompt },
                        new Dictionary<string, object?>
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new Dictionary<string, object?> { ["url"] = ToDataUrl(imageBytes, mediaType) },
                        },
                    },
                },
            },
        };

        var clampedMaxOutputTokens = Math.Clamp(maxOutputTokens, 1, 32_000);
        if (provider.ReasoningEffort is { } reasoningEffort)
        {
            body["reasoning_effort"] = reasoningEffort.ToWireValue();
            body["max_completion_tokens"] = clampedMaxOutputTokens;
        }
        else
        {
            body["temperature"] = 0;
            body["max_tokens"] = clampedMaxOutputTokens;
        }
        var json = JsonSerializer.Serialize(body);

        logger.LogDebug(
            "Vision chat-completions request: POST {Endpoint}, provider={ProviderName}, model={Model}, authType={AuthType}, promptChars={PromptChars}, imageBytes={ImageBytes}, mediaType={MediaType}, maxTokens={MaxTokens}, bodyChars={BodyChars}",
            request.RequestUri,
            provider.Name,
            provider.ModelId,
            effectiveAuthType,
            prompt.Length,
            imageBytes.Length,
            mediaType,
            Math.Clamp(maxOutputTokens, 1, 32_000),
            json.Length);

        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Vision chat-completions API error {StatusCode}: {Body}", (int)response.StatusCode, LogRedaction.RedactJson(responseBody));
            logger.LogDebug("Vision chat-completions error response body: {Body}", LogRedaction.RedactJson(responseBody));
            throw new HttpRequestException(LlmErrorNormalizer.SummarizeHttpError("Vision request", (int)response.StatusCode, LogRedaction.RedactJson(responseBody)));
        }

        try
        {
            // Gateways like Cline wrap non-streaming completions in a {"data": {...}, "success": true}
            // envelope; unwrap it before looking for the standard chat-completions payload.
            var payloadBody = OpenAICompatEnvelopeHandler.TryUnwrapEnvelope(responseBody) ?? responseBody;
            using var document = JsonDocument.Parse(payloadBody);
            var root = document.RootElement;
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                var first = choices.EnumerateArray().FirstOrDefault();
                if (first.ValueKind != JsonValueKind.Undefined
                    && first.TryGetProperty("message", out var message)
                    && message.TryGetProperty("content", out var content))
                {
                    return ReadContentText(content).Trim();
                }
            }

            if (root.TryGetProperty("error", out var error))
                throw new InvalidOperationException(error.ToString());
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Vision response was not valid JSON.");
        }

        throw new InvalidOperationException("Vision request completed without text content.");
    }

    private static string ReadContentText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;

        if (content.ValueKind != JsonValueKind.Array)
            return content.ToString();

        var sb = new StringBuilder();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                sb.Append(item.GetString());
                continue;
            }

            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String)
            {
                sb.Append(text.GetString());
            }
        }

        return sb.ToString();
    }

    private static Uri BuildChatCompletionsEndpoint(string endpointUrl)
    {
        var trimmed = endpointUrl.TrimEnd('/');
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return new Uri(trimmed);

        return new Uri(trimmed + "/chat/completions");
    }

    private static byte[] CreateVisionProbeImage()
    {
        using var surface = SKSurface.Create(new SKImageInfo(220, 100));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        using var paint = new SKPaint
        {
            Color = SKColors.Black,
            IsAntialias = true,
        };
        using var font = new SKFont(SKTypeface.Default, 56);
        canvas.DrawText(VisionProbeCode, 48, 68, font, paint);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static string ToDataUrl(byte[] imageBytes, string mediaType) =>
        $"data:{mediaType};base64,{Convert.ToBase64String(imageBytes)}";

    private static string NormalizeProbeResponse(string response)
    {
        var chars = response
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray();
        return new string(chars);
    }

    private static string? ReadCodexResponseError(JsonElement evt)
    {
        if (evt.TryGetProperty("response", out var response)
            && response.TryGetProperty("error", out var error)
            && error.TryGetProperty("message", out var message))
        {
            return message.GetString();
        }

        if (evt.TryGetProperty("message", out var topLevel))
            return topLevel.GetString();

        return null;
    }

    private static bool IsJwt(string token) =>
        !token.StartsWith("sk-", StringComparison.Ordinal) && token.Split('.').Length == 3;

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "...";
    }
}
