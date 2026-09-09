using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Lorekeeper.Diagnostics;
using Lorekeeper.Llm;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Images;

public sealed class CodexProjectImageProvider(
    ILlmProviderService providerService,
    ICodexAuthService codexAuth,
    IHttpClientFactory httpClientFactory,
    IOptions<ProjectImageGenerationOptions> options,
    ILogger<CodexProjectImageProvider> logger) : IProjectImageProvider
{
    public async Task<ProjectImageProviderResult> GenerateAsync(
        ProjectImageProviderGenerateRequest request,
        CancellationToken cancellationToken = default,
        IProgress<ProjectImageProviderProgress>? progress = null)
    {
        var connection = await ResolveConnectionAsync(cancellationToken);
        var mainlineModel = CleanModel(request.MainlineModel, options.Value.DefaultMainlineModel);
        var imageModel = CleanModel(request.ImageModel, options.Value.DefaultImageModel);
        var images = new List<ProjectImageProviderImage>();
        var metadata = new List<object?>();

        for (var i = 0; i < request.Count; i++)
        {
            var result = await SendImageRequestAsync(
                connection,
                mainlineModel,
                imageModel,
                BuildGeneratePayload(request, mainlineModel, imageModel),
                request.OutputFormat,
                cancellationToken,
                progress);
            images.Add(result.Image);
            metadata.Add(result.Metadata);
        }

        return new ProjectImageProviderResult(
            images,
            CodexProvider.Name,
            mainlineModel,
            imageModel,
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    public async Task<ProjectImageProviderResult> EditAsync(
        ProjectImageProviderEditRequest request,
        CancellationToken cancellationToken = default,
        IProgress<ProjectImageProviderProgress>? progress = null)
    {
        var connection = await ResolveConnectionAsync(cancellationToken);
        var mainlineModel = CleanModel(request.MainlineModel, options.Value.DefaultMainlineModel);
        var imageModel = CleanModel(request.ImageModel, options.Value.DefaultImageModel);
        var images = new List<ProjectImageProviderImage>();
        var metadata = new List<object?>();

        for (var i = 0; i < request.Count; i++)
        {
            var result = await SendImageRequestAsync(
                connection,
                mainlineModel,
                imageModel,
                BuildEditPayload(request, mainlineModel, imageModel),
                request.OutputFormat,
                cancellationToken,
                progress);
            images.Add(result.Image);
            metadata.Add(result.Metadata);
        }

        return new ProjectImageProviderResult(
            images,
            CodexProvider.Name,
            mainlineModel,
            imageModel,
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private async Task<CodexImageConnection> ResolveConnectionAsync(CancellationToken cancellationToken)
    {
        var provider = await providerService.GetByNameAsync(CodexProvider.Name, cancellationToken);
        if (provider is null || !CodexProvider.IsCodex(provider))
            throw new InvalidOperationException("No OpenAI Codex OAuth provider is configured. Connect OpenAI Codex in Settings first.");

        var token = await codexAuth.GetValidTokenAsync(provider.Id, cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Connect OpenAI Codex in Settings before generating images.");

        return new CodexImageConnection(token, CodexProvider.ExtractAccountId(token));
    }

    private async Task<CodexImageReadResult> SendImageRequestAsync(
        CodexImageConnection connection,
        string mainlineModel,
        string imageModel,
        Dictionary<string, object?> payload,
        string requestedOutputFormat,
        CancellationToken cancellationToken,
        IProgress<ProjectImageProviderProgress>? progress)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.Value.RequestTimeoutSeconds, 1, 3600)));
        try
        {
            return await SendImageRequestCoreAsync(connection, mainlineModel, imageModel, payload,
                requestedOutputFormat, timeout.Token, progress);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProjectImageProviderException("The image request timed out before a final image was received.",
                "request_timeout", innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ProjectImageProviderException("The connection to the image provider failed.",
                "transport_error", statusCode: (int?)ex.StatusCode, innerException: ex);
        }
        catch (IOException ex)
        {
            throw new ProjectImageProviderException("The image provider stream was interrupted.",
                "transport_error", innerException: ex);
        }
    }

    private async Task<CodexImageReadResult> SendImageRequestCoreAsync(
        CodexImageConnection connection,
        string mainlineModel,
        string imageModel,
        Dictionary<string, object?> payload,
        string requestedOutputFormat,
        CancellationToken cancellationToken,
        IProgress<ProjectImageProviderProgress>? progress)
    {
        var json = JsonSerializer.Serialize(payload);
        var stopwatch = Stopwatch.StartNew();
        string? requestId = null;
        string? responseId = null;
        string? lastEventType = null;
        var eventCount = 0;

        using var request = new HttpRequestMessage(HttpMethod.Post, CodexProvider.ResponsesEndpoint);
        request.Content = new StringContent(json, Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.Token);
        request.Headers.TryAddWithoutValidation("chatgpt-account-id", connection.AccountId);
        request.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=experimental");
        request.Headers.TryAddWithoutValidation("originator", "pi");
        request.Headers.TryAddWithoutValidation("User-Agent", "Lorekeeper");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var httpClient = httpClientFactory.CreateClient();
        httpClient.Timeout = Timeout.InfiniteTimeSpan;

        ReportProgress(progress, new ProjectImageProviderProgress(ProjectImageProviderProgressKind.Started, "Image request started."));

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        requestId = ReadResponseRequestId(response);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var error = ParseError(errorBody);
            var errorKind = ClassifyImageError(error, (int)response.StatusCode);
            var exception = new ProjectImageProviderException(
                $"Codex image request returned {(int)response.StatusCode} ({errorKind}): {error.Message}",
                errorKind,
                requestId,
                statusCode: (int)response.StatusCode,
                errorCode: error.Code,
                errorType: error.Type,
                retryAfter: ReadRetryAfter(response));
            ReportProgress(progress, FailedProgress(exception, ProjectImageProviderProgressKind.Failed));
            throw exception;
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        string? line;

        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
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
            catch (JsonException)
            {
                continue;
            }

            var type = ReadString(evt, "type");
            lastEventType = type;
            eventCount++;

            if (type == "response.created" && evt.TryGetProperty("response", out var createdResponse))
            {
                responseId = ReadString(createdResponse, "id");
                ReportProgress(progress, new ProjectImageProviderProgress(
                    ProjectImageProviderProgressKind.InProgress,
                    "Image response created.",
                    requestId,
                    responseId,
                    LastEventType: lastEventType,
                    EventCount: eventCount));
            }

            if (type == "response.failed" || type == "error")
            {
                var error = ReadError(evt);
                var errorKind = ClassifyImageError(error);
                var exception = new ProjectImageProviderException(
                    $"Codex image generation failed ({errorKind}): {error.Message}",
                    errorKind,
                    requestId,
                    responseId,
                    lastEventType: lastEventType,
                    eventCount: eventCount,
                    errorCode: error.Code,
                    errorType: error.Type,
                    retryAfter: ReadRetryAfter(response));
                ReportProgress(progress, FailedProgress(exception, ProjectImageProviderProgressKind.Failed));
                throw exception;
            }

            if (type is "response.image_generation_call.in_progress"
                or "response.image_generation_call.generating"
                or "response.image_generation_call.partial_image"
                or "response.image_generation_call.completed")
            {
                ReportProgress(progress, ImageGenerationCallProgress(
                    evt,
                    type,
                    requestedOutputFormat,
                    requestId,
                    responseId,
                    lastEventType,
                    eventCount));
            }

            if (type == "response.output_item.done" && evt.TryGetProperty("item", out var item))
            {
                var imageResult = TryReadImageResult(
                    item,
                    requestedOutputFormat,
                    responseId,
                    requestId,
                    mainlineModel,
                    imageModel,
                    stopwatch.Elapsed,
                    eventCount,
                    lastEventType);
                if (imageResult is not null)
                {
                    ReportProgress(progress, new ProjectImageProviderProgress(
                        ProjectImageProviderProgressKind.Completed,
                        "Image result received.",
                        requestId,
                        imageResult.Image.ResponseId,
                        imageResult.Image.CallId,
                        LastEventType: lastEventType,
                        EventCount: eventCount));
                    return imageResult;
                }
            }

            if (type == "response.completed"
                && evt.TryGetProperty("response", out var completedResponse))
            {
                responseId ??= ReadString(completedResponse, "id");
                var imageResult = TryReadImageResultFromResponse(
                    completedResponse,
                    requestedOutputFormat,
                    responseId,
                    requestId,
                    mainlineModel,
                    imageModel,
                    stopwatch.Elapsed,
                    eventCount,
                    lastEventType);
                if (imageResult is not null)
                {
                    ReportProgress(progress, new ProjectImageProviderProgress(
                        ProjectImageProviderProgressKind.Completed,
                        "Image result received.",
                        requestId,
                        imageResult.Image.ResponseId,
                        imageResult.Image.CallId,
                        LastEventType: lastEventType,
                        EventCount: eventCount));
                    return imageResult;
                }
            }
        }

        var missingImageException = new ProjectImageProviderException(
            $"Codex image generation completed without returning an image. responseId={responseId ?? "unknown"}, requestId={requestId ?? "unknown"}, events={eventCount}, lastEvent={lastEventType ?? "none"}.",
            "stream_completed_without_image",
            requestId,
            responseId,
            lastEventType: lastEventType,
            eventCount: eventCount);
        ReportProgress(progress, FailedProgress(missingImageException, ProjectImageProviderProgressKind.StreamEndedWithoutImage));
        throw missingImageException;
    }

    private CodexImageReadResult? TryReadImageResultFromResponse(
        JsonElement response,
        string requestedOutputFormat,
        string? responseId,
        string? requestId,
        string mainlineModel,
        string imageModel,
        TimeSpan elapsed,
        int eventCount,
        string? lastEventType)
    {
        if (!response.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in output.EnumerateArray())
        {
            var imageResult = TryReadImageResult(
                item,
                requestedOutputFormat,
                responseId,
                requestId,
                mainlineModel,
                imageModel,
                elapsed,
                eventCount,
                lastEventType);
            if (imageResult is not null)
                return imageResult;
        }

        return null;
    }

    private CodexImageReadResult? TryReadImageResult(
        JsonElement item,
        string requestedOutputFormat,
        string? responseId,
        string? requestId,
        string mainlineModel,
        string imageModel,
        TimeSpan elapsed,
        int eventCount,
        string? lastEventType)
    {
        if (ReadString(item, "type") != "image_generation_call")
            return null;

        var result = ReadString(item, "result");
        if (string.IsNullOrWhiteSpace(result))
            return null;

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(result);
        }
        catch (FormatException ex)
        {
            throw new ProjectImageProviderException(
                "Codex returned an image_generation_call result that was not valid base64.",
                "invalid_image_result",
                requestId,
                responseId,
                ReadString(item, "id"),
                lastEventType: lastEventType,
                eventCount: eventCount,
                innerException: ex);
        }

        var outputFormat = NormalizeOutputFormat(ReadString(item, "output_format") ?? requestedOutputFormat);
        var callId = ReadString(item, "id");
        logger.LogDebug(
            "Codex image result received: requestId={RequestId}, responseId={ResponseId}, mainlineModel={MainlineModel}, imageModel={ImageModel}, callId={CallId}, outputFormat={OutputFormat}, imageBytes={ImageBytes}, elapsedMs={ElapsedMs}, eventCount={EventCount}, lastEventType={LastEventType}",
            requestId,
            responseId,
            mainlineModel,
            imageModel,
            callId,
            outputFormat,
            bytes.Length,
            elapsed.TotalMilliseconds,
            eventCount,
            lastEventType);

        return new CodexImageReadResult(
            new ProjectImageProviderImage(
                bytes,
                $"image/{outputFormat}",
                outputFormat,
                ReadString(item, "revised_prompt"),
                responseId,
                callId,
                ReadString(item, "model"),
                ReadString(item, "quality"),
                ReadString(item, "background"),
                ReadString(item, "size"),
                ReadString(item, "output_format"),
                ReadInt(item, "output_compression")),
            new
            {
                ResponseId = responseId,
                CallId = callId,
                RevisedPrompt = ReadString(item, "revised_prompt"),
                OutputFormat = outputFormat,
                ReportedModel = ReadString(item, "model"),
                ReportedQuality = ReadString(item, "quality"),
                ReportedBackground = ReadString(item, "background"),
                ReportedSize = ReadString(item, "size"),
                ReportedOutputFormat = ReadString(item, "output_format"),
                ReportedOutputCompression = ReadInt(item, "output_compression"),
            });
    }

    private Dictionary<string, object?> BuildGeneratePayload(
        ProjectImageProviderGenerateRequest request,
        string mainlineModel,
        string imageModel)
    {
        var content = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["type"] = "input_text",
                ["text"] = request.Prompt,
            },
        };

        foreach (var reference in request.ReferenceImages.Take(options.Value.MaxReferenceImages))
            content.Add(InputImage(reference));

        return BasePayload(
            mainlineModel,
            content,
            BaseImageTool(request.Size, request.Quality, request.OutputFormat, request.OutputCompression, imageModel, "generate", request.Background),
            "Use the image_generation tool to create one story illustration or project image from the standalone target brief. Supplied input images are visual continuity references, not edit canvases: preserve the character identity, design, clothes, hair, age, proportions, palette, medium, recurring props, setting traits, and style assigned to each reference by the brief unless it requests a redesign or style break. Take expression, pose, gesture, gaze, body language, action, camera, framing, layout, background, lighting, and composition from the target brief rather than copying those shot-specific traits from a reference unless explicitly requested.");
    }

    private Dictionary<string, object?> BuildEditPayload(
        ProjectImageProviderEditRequest request,
        string mainlineModel,
        string imageModel)
    {
        var content = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["type"] = "input_text",
                ["text"] = request.Prompt,
            },
            InputImage(request.SourceImage),
        };

        foreach (var reference in request.ReferenceImages.Take(options.Value.MaxReferenceImages))
            content.Add(InputImage(reference));

        var tool = BaseImageTool(request.Size, request.Quality, request.OutputFormat, request.OutputCompression, imageModel, "edit", request.Background);
        if (request.Mask is not null)
        {
            tool["input_image_mask"] = new Dictionary<string, object?>
            {
                ["image_url"] = ToDataUrl(request.Mask),
            };
        }

        var instructions = request.Mask is null
            ? "Use the image_generation tool to edit the first supplied image. Make only the requested changes, preserve the named identity, story, style, and composition constraints and unrelated content, and adapt other details only where the requested transformation requires it. Treat additional supplied images as references only for the roles and traits explicitly assigned to them; do not replace the source composition or inherit unrelated reference details."
            : $"Use the image_generation tool to render one coherent complete edit from the first supplied image as the visual starting point. {ProjectImageRegionalGuide.PromptInstruction} Treat additional supplied images as references only for the roles and traits explicitly assigned to them; do not replace the source composition or inherit unrelated reference details.";

        return BasePayload(
            mainlineModel,
            content,
            tool,
            instructions);
    }

    private static Dictionary<string, object?> BasePayload(
        string mainlineModel,
        IReadOnlyList<Dictionary<string, object?>> content,
        Dictionary<string, object?> tool,
        string instructions) =>
        new()
        {
            ["model"] = mainlineModel,
            ["instructions"] = instructions,
            ["input"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = content,
                },
            },
            ["tools"] = new[] { tool },
            ["tool_choice"] = new Dictionary<string, object?> { ["type"] = "image_generation" },
            ["stream"] = true,
            ["store"] = false,
        };

    private Dictionary<string, object?> BaseImageTool(
        string size,
        string quality,
        string outputFormat,
        int? outputCompression,
        string imageModel,
        string action,
        string background)
    {
        var normalizedOutputFormat = NormalizeOutputFormat(outputFormat);
        var tool = new Dictionary<string, object?>
        {
            ["type"] = "image_generation",
            ["model"] = imageModel,
            ["action"] = action,
            ["size"] = string.IsNullOrWhiteSpace(size) ? "auto" : size.Trim(),
            ["output_format"] = normalizedOutputFormat,
            ["background"] = ProjectImageModelCatalog.NormalizeBackground(background),
        };

        var partialImages = Math.Clamp(options.Value.PartialImages, 0, 3);
        if (partialImages > 0)
            tool["partial_images"] = partialImages;

        if (!string.IsNullOrWhiteSpace(quality) && !string.Equals(quality, "auto", StringComparison.OrdinalIgnoreCase))
            tool["quality"] = quality.Trim();

        if (normalizedOutputFormat is "jpeg" or "webp" && outputCompression is int compression)
            tool["output_compression"] = Math.Clamp(compression, 0, 100);

        return tool;
    }

    private static Dictionary<string, object?> InputImage(ProjectImageProviderReference reference) =>
        new()
        {
            ["type"] = "input_image",
            ["image_url"] = ToDataUrl(reference),
            ["detail"] = "auto",
        };

    private static string ToDataUrl(ProjectImageProviderReference reference) =>
        DataUrl.ToDataUrl(reference.ContentType, reference.Data);

    private static string CleanModel(string? requested, string fallback) =>
        string.IsNullOrWhiteSpace(requested) ? fallback : requested.Trim();

    private static string NormalizeOutputFormat(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "jpg" => "jpeg",
            "jpeg" => "jpeg",
            "webp" => "webp",
            _ => "png",
        };

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numberValue))
            return numberValue;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var stringValue))
            return stringValue;
        return null;
    }

    private static ProjectImageProviderProgress ImageGenerationCallProgress(
        JsonElement evt,
        string type,
        string requestedOutputFormat,
        string? requestId,
        string? responseId,
        string? lastEventType,
        int eventCount)
    {
        var callId = ReadString(evt, "call_id")
            ?? ReadString(evt, "item_id")
            ?? ReadString(evt, "id");
        var itemId = ReadString(evt, "item_id");
        var outputIndex = ReadInt(evt, "output_index");
        var partialImageIndex = ReadInt(evt, "partial_image_index");
        var partialImageBase64 = ReadString(evt, "partial_image_b64");
        var outputFormat = NormalizeOutputFormat(requestedOutputFormat);

        return type switch
        {
            "response.image_generation_call.generating" => new ProjectImageProviderProgress(
                ProjectImageProviderProgressKind.Generating,
                "Generating image.",
                requestId,
                responseId,
                callId,
                itemId,
                outputIndex,
                LastEventType: lastEventType,
                EventCount: eventCount),
            "response.image_generation_call.partial_image" => new ProjectImageProviderProgress(
                ProjectImageProviderProgressKind.PartialImage,
                partialImageIndex is int index ? $"Received partial image {index + 1}." : "Received partial image.",
                requestId,
                responseId,
                callId,
                itemId,
                outputIndex,
                partialImageIndex,
                string.IsNullOrWhiteSpace(partialImageBase64) ? null : $"data:image/{outputFormat};base64,{partialImageBase64}",
                LastEventType: lastEventType,
                EventCount: eventCount),
            "response.image_generation_call.completed" => new ProjectImageProviderProgress(
                ProjectImageProviderProgressKind.Completed,
                "Image generation call completed.",
                requestId,
                responseId,
                callId,
                itemId,
                outputIndex,
                LastEventType: lastEventType,
                EventCount: eventCount),
            _ => new ProjectImageProviderProgress(
                ProjectImageProviderProgressKind.InProgress,
                "Image generation call started.",
                requestId,
                responseId,
                callId,
                itemId,
                outputIndex,
                LastEventType: lastEventType,
                EventCount: eventCount),
        };
    }

    private static ProjectImageProviderProgress FailedProgress(ProjectImageProviderException exception, ProjectImageProviderProgressKind kind) =>
        new(
            kind,
            exception.Message,
            exception.RequestId,
            exception.ResponseId,
            exception.CallId,
            ErrorKind: exception.ErrorKind,
            StatusCode: exception.StatusCode,
            LastEventType: exception.LastEventType,
            EventCount: exception.EventCount);

    private static void ReportProgress(IProgress<ProjectImageProviderProgress>? progress, ProjectImageProviderProgress update) =>
        progress?.Report(update);

    private static string? ReadResponseRequestId(HttpResponseMessage response) =>
        ReadHeader(response, "x-request-id")
        ?? ReadHeader(response, "openai-request-id")
        ?? ReadHeader(response, "request-id");

    private static string? ReadHeader(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values))
            return values.FirstOrDefault();
        if (response.Content.Headers.TryGetValues(name, out values))
            return values.FirstOrDefault();
        return null;
    }

    private static string ClassifyImageError(ImageProviderError error, int? statusCode = null)
    {
        var message = error.Message;
        var code = error.Code?.ToLowerInvariant();
        var type = error.Type?.ToLowerInvariant();
        if (code == "moderation_blocked" || type == "moderation_blocked") return "moderation_blocked";
        if (code == "image_generation_user_error" || type == "image_generation_user_error") return "image_generation_user_error";
        if (code is "insufficient_quota" or "billing_hard_limit_reached" or "billing_not_active"
            || type == "insufficient_quota"
            || message.Contains("quota", StringComparison.OrdinalIgnoreCase)
            || message.Contains("billing", StringComparison.OrdinalIgnoreCase)
            || message.Contains("usage limit", StringComparison.OrdinalIgnoreCase))
            return "quota_exhausted";
        if (statusCode == 401 || type == "authentication_error" || code is "invalid_api_key" or "invalid_token" or "token_expired")
            return "authentication_error";
        if (statusCode == 403 || code is "permission_denied" or "model_access_denied"
            || message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Cloudflare", StringComparison.OrdinalIgnoreCase))
            return "codex_transport_forbidden";
        if (code is "model_not_found" or "unsupported_model"
            || (code == "invalid_value" && message.Contains("model", StringComparison.OrdinalIgnoreCase)))
            return "image_model_invalid";
        if (code is "content_policy_violation" or "safety_violation"
            || message.Contains("moderation", StringComparison.OrdinalIgnoreCase)
            || message.Contains("content policy", StringComparison.OrdinalIgnoreCase))
            return "moderation_blocked";
        if (type == "invalid_request_error" || statusCode is 400 or 404 or 422)
            return "invalid_request";
        if (message.Contains("input-images", StringComparison.OrdinalIgnoreCase)
            && message.Contains("per min", StringComparison.OrdinalIgnoreCase)
            && message.Contains("gpt-image", StringComparison.OrdinalIgnoreCase))
        {
            return "codex_image_input_rate_limit";
        }

        if (statusCode == 429 || code is "rate_limit_exceeded" or "slow_down"
            || type == "rate_limit_error"
            || message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase))
        {
            return "rate_limit";
        }

        if (statusCode >= 500 || code is "server_error" or "server_is_overloaded" || type == "server_error")
            return "server_error";
        return "api_error";
    }

    private static ImageProviderError ReadError(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return new ImageProviderError("The image provider rejected the request.", null, null);
        if (element.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object)
            element = response;
        if (element.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            element = error;
        return new ImageProviderError(
            LogRedaction.RedactJson(ReadString(element, "message") ?? "The image provider rejected the request."),
            ReadString(element, "code"), ReadString(element, "type"));
    }

    private static ImageProviderError ParseError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return ReadError(document.RootElement);
        }
        catch (JsonException)
        {
        }

        return new ImageProviderError("The image provider returned an unsuccessful response without a structured error.", null, null);
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var delay = header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null);
        return delay > TimeSpan.Zero ? delay : null;
    }

    private sealed record ImageProviderError(string Message, string? Code, string? Type);

    private sealed record CodexImageConnection(string Token, string AccountId);

    private sealed record CodexImageReadResult(ProjectImageProviderImage Image, object Metadata);
}
