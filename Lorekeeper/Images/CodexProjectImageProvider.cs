using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
        httpClient.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.Value.RequestTimeoutSeconds, 1, 3600));

        ReportProgress(progress, new ProjectImageProviderProgress(ProjectImageProviderProgressKind.Started, "Image request started."));

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        requestId = ReadResponseRequestId(response);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var errorKind = ClassifyImageError(errorBody);
            var errorMessage = ReadErrorMessage(errorBody) ?? errorBody;
            var exception = new ProjectImageProviderException(
                $"Codex image request returned {(int)response.StatusCode} ({errorKind}): {errorMessage}",
                errorKind,
                requestId,
                statusCode: (int)response.StatusCode);
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
                var error = ReadResponseError(evt) ?? "Codex image generation failed.";
                var errorKind = ClassifyImageError(error);
                var exception = new ProjectImageProviderException(
                    $"Codex image generation failed ({errorKind}): {error}",
                    errorKind,
                    requestId,
                    responseId,
                    lastEventType: lastEventType,
                    eventCount: eventCount);
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
                callId),
            new
            {
                ResponseId = responseId,
                CallId = callId,
                RevisedPrompt = ReadString(item, "revised_prompt"),
                OutputFormat = outputFormat,
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
            BaseImageTool(request.Size, request.Quality, request.OutputFormat, request.OutputCompression, imageModel),
            "Use the image_generation tool to create one story illustration or project image. Interpret the user's prompt as a standalone description of the desired output. Supplied input images are reference-only, not edit sources: preserve only the identity, design, setting, or style traits the prompt explicitly assigns to them. Take pose, expression, gaze, action, camera, framing, layout, background, lighting, and composition from the target brief, and do not copy those traits from a reference unless explicitly requested. Treat conversational process language such as new, redo, from scratch, different, current image, or not a recreation as nonvisual context; render the concrete target description instead.");
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

        var tool = BaseImageTool(request.Size, request.Quality, request.OutputFormat, request.OutputCompression, imageModel);
        if (request.Mask is not null)
        {
            tool["input_image_mask"] = new Dictionary<string, object?>
            {
                ["image_url"] = ToDataUrl(request.Mask),
            };
        }

        return BasePayload(
            mainlineModel,
            content,
            tool,
            "Use the image_generation tool to edit the first supplied image as the source canvas. Apply any mask to guide the targeted edit, make the requested changes, and preserve the source elements the prompt marks invariant. Treat additional supplied images as references only for the roles and traits explicitly assigned to them; do not replace the source composition or inherit unrelated reference details.");
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
        string imageModel)
    {
        var normalizedOutputFormat = NormalizeOutputFormat(outputFormat);
        var tool = new Dictionary<string, object?>
        {
            ["type"] = "image_generation",
            ["model"] = imageModel,
            ["size"] = string.IsNullOrWhiteSpace(size) ? "auto" : size.Trim(),
            ["output_format"] = normalizedOutputFormat,
            ["background"] = "auto",
        };

        var partialImages = Math.Clamp(options.Value.PartialImages, 0, 3);
        if (partialImages > 0)
            tool["partial_images"] = partialImages;

        if (!string.IsNullOrWhiteSpace(quality) && !string.Equals(quality, "auto", StringComparison.OrdinalIgnoreCase))
            tool["quality"] = quality.Trim();

        if (normalizedOutputFormat == "jpeg" && outputCompression is int compression)
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

    private static string ClassifyImageError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "unknown";

        if (message.Contains("input-images", StringComparison.OrdinalIgnoreCase)
            && message.Contains("per min", StringComparison.OrdinalIgnoreCase)
            && message.Contains("gpt-image", StringComparison.OrdinalIgnoreCase))
        {
            return "codex_image_input_rate_limit";
        }

        if (message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || message.Contains("429", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Please try again", StringComparison.OrdinalIgnoreCase))
        {
            return "rate_limit";
        }

        if (message.Contains("403", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Cloudflare", StringComparison.OrdinalIgnoreCase))
        {
            return "codex_transport_forbidden";
        }

        if (message.Contains("invalid_value", StringComparison.OrdinalIgnoreCase)
            && message.Contains("gpt-image", StringComparison.OrdinalIgnoreCase))
        {
            return "image_model_invalid";
        }

        return "api_error";
    }

    private static string? ReadResponseError(JsonElement evt)
    {
        if (evt.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            return message.GetString();
        if (evt.TryGetProperty("error", out var error)
            && error.TryGetProperty("message", out var errorMessage)
            && errorMessage.ValueKind == JsonValueKind.String)
        {
            return errorMessage.GetString();
        }
        if (evt.TryGetProperty("response", out var response)
            && response.TryGetProperty("error", out error)
            && error.TryGetProperty("message", out errorMessage)
            && errorMessage.ValueKind == JsonValueKind.String)
        {
            return errorMessage.GetString();
        }
        return null;
    }

    private static string? ReadErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                return message.GetString();
            if (root.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var errorMessage)
                && errorMessage.ValueKind == JsonValueKind.String)
            {
                return errorMessage.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private sealed record CodexImageConnection(string Token, string AccountId);

    private sealed record CodexImageReadResult(ProjectImageProviderImage Image, object Metadata);
}
