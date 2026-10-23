using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lorekeeper.Composition;
using Lorekeeper.Diagnostics;
using Lorekeeper.Models;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Images;

/// <summary>
/// Generates and edits images through OpenRouter's dedicated Image API. Edits
/// send the source as the first reference. Aspect-tier models receive the
/// nearest listed aspect ratio and resolution tier for the planned raster.
/// </summary>
public sealed class OpenRouterImageProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<ProjectImageGenerationOptions> options,
    ILogger<OpenRouterImageProvider> logger) : IProjectImageProvider
{
    private const string BaseUrl = "https://openrouter.ai/api/v1";
    private const int ModelListMaximumBytes = 4 * 1024 * 1024;
    private const int ErrorBodyMaximumBytes = 64 * 1024;

    private static readonly string[] SupportedOutputFormats = ["png", "jpeg", "webp"];

    private static readonly JsonSerializerOptions BodyOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public ImageProviderKind Kind => ImageProviderKind.OpenRouter;

    public async Task<ProjectImageProviderResult> GenerateAsync(
        ImageProvider provider,
        ProjectImageProviderGenerateRequest request,
        CancellationToken cancellationToken = default,
        IProgress<ProjectImageProviderProgress>? progress = null)
    {
        var row = ResolveModelRow(provider, request.ImageModel);
        var constraints = ImageRasterConstraints.FromJson(row.RasterConstraintsJson);
        var body = BuildBody(
            constraints,
            row,
            request.Prompt,
            request.Size,
            request.OutputFormat,
            request.Quality,
            request.OutputCompression,
            request.Background,
            request.ReferenceImages,
            row.SupportsStreaming);
        return await SendImageRequestAsync(provider, body, request.ImageModel, row.SupportsStreaming,
            cancellationToken, progress);
    }

    public async Task<ProjectImageProviderResult> EditAsync(
        ImageProvider provider,
        ProjectImageProviderEditRequest request,
        CancellationToken cancellationToken = default,
        IProgress<ProjectImageProviderProgress>? progress = null)
    {
        var row = ResolveModelRow(provider, request.ImageModel);
        var constraints = ImageRasterConstraints.FromJson(row.RasterConstraintsJson);
        var references = new List<ProjectImageProviderReference> { request.SourceImage };
        references.AddRange(request.ReferenceImages);
        var body = BuildBody(
            constraints,
            row,
            request.Prompt,
            request.Size,
            request.OutputFormat,
            request.Quality,
            request.OutputCompression,
            request.Background,
            references,
            row.SupportsStreaming);
        return await SendImageRequestAsync(provider, body, request.ImageModel, row.SupportsStreaming,
            cancellationToken, progress);
    }

    public async Task<ImageProviderTestResult> TestAsync(ImageProvider provider, CancellationToken cancellationToken = default)
    {
        try
        {
            var models = await DiscoverModelsAsync(provider, cancellationToken);
            return new ImageProviderTestResult(true, $"OpenRouter key accepted; {models.Count} image models available.");
        }
        catch (ProjectImageProviderException ex)
        {
            return new ImageProviderTestResult(false, ex.Message);
        }
    }

    public async Task<IReadOnlyList<DiscoveredImageModel>> DiscoverModelsAsync(
        ImageProvider provider,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.Value.RequestTimeoutSeconds, 1, 3600)));
        try
        {
            return await DiscoverModelsCoreAsync(provider, timeout.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProjectImageProviderException(
                "OpenRouter did not return its image model list in time.",
                "request_timeout",
                innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ProjectImageProviderException(
                "The connection to OpenRouter failed.",
                "transport_error",
                statusCode: (int?)ex.StatusCode,
                innerException: ex);
        }
        catch (IOException ex)
        {
            throw new ProjectImageProviderException(
                "The OpenRouter model list was interrupted.",
                "transport_error",
                innerException: ex);
        }
    }

    private async Task<IReadOnlyList<DiscoveredImageModel>> DiscoverModelsCoreAsync(
        ImageProvider provider,
        CancellationToken cancellationToken)
    {
        var apiKey = RequireApiKey(provider);

        var httpClient = httpClientFactory.CreateClient();
        httpClient.Timeout = Timeout.InfiniteTimeSpan;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/images/models");
        ApplyHeaders(request, apiKey, "application/json");

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowHttpErrorAsync(response, null, cancellationToken);
            return [];
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var body = await ReadCappedBodyAsync(stream, ModelListMaximumBytes, "OpenRouter returned an oversized model list.", cancellationToken);

        JsonElement data;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("data", out var value)
                && value.ValueKind == JsonValueKind.Array)
            {
                data = value.Clone();
            }
            else
            {
                throw new ProjectImageProviderException("OpenRouter returned an invalid model list.", "server_error");
            }
        }
        catch (JsonException)
        {
            throw new ProjectImageProviderException("OpenRouter returned an invalid model list.", "server_error");
        }

        var models = new List<DiscoveredImageModel>();
        foreach (var entry in data.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;
            var modelId = ReadString(entry, "id");
            if (string.IsNullOrWhiteSpace(modelId))
                continue;
            if (!entry.TryGetProperty("architecture", out var architecture)
                || architecture.ValueKind != JsonValueKind.Object)
                continue;
            if (!ReadArrayValues(architecture, "output_modalities").Contains("image", StringComparer.OrdinalIgnoreCase))
                continue;
            var hasImageInput = ReadArrayValues(architecture, "input_modalities")
                .Contains("image", StringComparer.OrdinalIgnoreCase);

            var parameters = entry.TryGetProperty("supported_parameters", out var supported)
                && supported.ValueKind == JsonValueKind.Object
                    ? supported
                    : default;

            var qualities = ReadArrayValues(Parameter(parameters, "quality"), "values")
                .Select(value => value.Trim().ToLowerInvariant())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (qualities.Count == 0)
                qualities = ["auto"];
            if (qualities.Remove("auto"))
                qualities.Insert(0, "auto");

            var outputFormats = ReadArrayValues(Parameter(parameters, "output_format"), "values")
                .Select(value => value.Trim().ToLowerInvariant())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Intersect(SupportedOutputFormats)
                .ToList();
            if (outputFormats.Count == 0)
                outputFormats = ["png"];

            var inputReferenceDescriptor = Parameter(parameters, "input_references");
            var maxReferenceImages = 0;
            if (inputReferenceDescriptor.ValueKind == JsonValueKind.Object)
            {
                var rangeMaximum = ReadRangeMaximum(inputReferenceDescriptor);
                maxReferenceImages = rangeMaximum is { } maximum
                    ? (int)Math.Round(Math.Clamp(maximum, 0, 64))
                    : hasImageInput ? 1 : 0;
            }

            var backgroundValues = ReadArrayValues(Parameter(parameters, "background"), "values")
                .Select(value => value.Trim().ToLowerInvariant())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var streaming = entry.TryGetProperty("supports_streaming", out var streamingElement)
                && streamingElement.ValueKind == JsonValueKind.True;

            var aspectRatioDescriptor = Parameter(parameters, "aspect_ratio");
            var resolutionDescriptor = Parameter(parameters, "resolution");
            ImageRasterConstraints constraints;
            IReadOnlyList<string> aspectValues;
            if (aspectRatioDescriptor.ValueKind == JsonValueKind.Object)
            {
                aspectValues = ReadArrayValues(aspectRatioDescriptor, "values")
                    .Where(value => !value.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            else
            {
                aspectValues = ["1:1"];
            }

            if (aspectRatioDescriptor.ValueKind == JsonValueKind.Object
                || resolutionDescriptor.ValueKind == JsonValueKind.Object)
            {
                var tierValues = resolutionDescriptor.ValueKind == JsonValueKind.Object
                    ? ReadArrayValues(resolutionDescriptor, "values")
                    : [];
                constraints = ImageRasterConstraints.FromAspectTiers(aspectValues, tierValues);
            }
            else if (parameters.ValueKind == JsonValueKind.Object
                && parameters.TryGetProperty("size", out _))
            {
                constraints = ImageRasterConstraints.GptImage;
            }
            else
            {
                constraints = ImageRasterConstraints.FromAspectTiers(["1:1"], ["1K"]);
            }

            var capabilities = new ImageModelCapabilities(
                qualities,
                outputFormats,
                Generate: true,
                Edit: maxReferenceImages > 0,
                MaxReferenceImages: maxReferenceImages,
                TransparentBackground: backgroundValues.Contains("transparent", StringComparer.Ordinal),
                Streaming: streaming);
            models.Add(new DiscoveredImageModel(modelId, ReadString(entry, "name"), capabilities, constraints));
        }

        return models;
    }

    private async Task<ProjectImageProviderResult> SendImageRequestAsync(
        ImageProvider provider,
        Dictionary<string, object?> body,
        string imageModel,
        bool stream,
        CancellationToken cancellationToken,
        IProgress<ProjectImageProviderProgress>? progress)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.Value.RequestTimeoutSeconds, 1, 3600)));
        try
        {
            return await SendImageRequestCoreAsync(provider, body, imageModel, stream, timeout.Token, progress);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProjectImageProviderException(
                "The image request timed out before a final image was received.",
                "request_timeout",
                innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ProjectImageProviderException(
                "The connection to the image provider failed.",
                "transport_error",
                statusCode: (int?)ex.StatusCode,
                innerException: ex);
        }
        catch (IOException ex)
        {
            throw new ProjectImageProviderException(
                "The image provider stream was interrupted.",
                "transport_error",
                innerException: ex);
        }
    }

    private async Task<ProjectImageProviderResult> SendImageRequestCoreAsync(
        ImageProvider provider,
        Dictionary<string, object?> body,
        string imageModel,
        bool stream,
        CancellationToken cancellationToken,
        IProgress<ProjectImageProviderProgress>? progress)
    {
        var apiKey = RequireApiKey(provider);

        var httpClient = httpClientFactory.CreateClient();
        httpClient.Timeout = Timeout.InfiniteTimeSpan;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/images");
        ApplyHeaders(request, apiKey, stream ? "text/event-stream" : "application/json");
        request.Content = new StringContent(
            JsonSerializer.Serialize(body, BodyOptions),
            Encoding.UTF8,
            "application/json");

        ReportProgress(progress, new ProjectImageProviderProgress(ProjectImageProviderProgressKind.Started, "Image request started."));

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            await ThrowHttpErrorAsync(response, progress, cancellationToken);

        return stream
            ? await ReadStreamingResultAsync(response, provider, imageModel, cancellationToken, progress)
            : await ReadJsonResultAsync(response, provider, imageModel, cancellationToken, progress);
    }

    private async Task<ProjectImageProviderResult> ReadJsonResultAsync(
        HttpResponseMessage response,
        ImageProvider provider,
        string imageModel,
        CancellationToken cancellationToken,
        IProgress<ProjectImageProviderProgress>? progress)
    {
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await ReadCappedBodyAsync(
            stream,
            ResponseMaximumBytes,
            "OpenRouter returned an image larger than the configured output limit.",
            cancellationToken);
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(payload);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new ProjectImageProviderException("OpenRouter returned no image.", "missing_image");
        }

        JsonElement item = default;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var candidate in data.EnumerateArray())
            {
                item = candidate;
                break;
            }
        }

        var base64 = ReadString(item, "b64_json");
        if (string.IsNullOrWhiteSpace(base64))
            throw new ProjectImageProviderException("OpenRouter returned no image.", "missing_image");

        var image = DecodeImageBytes(base64, "invalid_image_result");
        var mediaType = ReadString(item, "media_type");
        var (contentType, outputFormat) = ResolveImageFormat(image, mediaType);
        var metadataJson = SerializeMetadata(root, mediaType);

        ReportProgress(progress, new ProjectImageProviderProgress(ProjectImageProviderProgressKind.Completed, "Image result received."));

        return new ProjectImageProviderResult(
            [new ProjectImageProviderImage(image, contentType, outputFormat, null, null, null, imageModel)],
            provider.Name,
            imageModel,
            metadataJson);
    }

    private async Task<ProjectImageProviderResult> ReadStreamingResultAsync(
        HttpResponseMessage response,
        ImageProvider provider,
        string imageModel,
        CancellationToken cancellationToken,
        IProgress<ProjectImageProviderProgress>? progress)
    {
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var eventCount = 0;
        string? lastEventType = null;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line) || line[0] == ':')
                continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;
            var payload = line["data:".Length..].Trim();
            if (payload.Length == 0)
                continue;
            if (payload == "[DONE]")
                break;

            JsonElement evt;
            try
            {
                using var document = JsonDocument.Parse(payload);
                evt = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }

            eventCount++;
            lastEventType = ReadString(evt, "type");

            if (lastEventType == "image_generation.partial_image")
            {
                ReportProgress(progress, new ProjectImageProviderProgress(
                    ProjectImageProviderProgressKind.PartialImage,
                    "Partial image received.",
                    PartialImageIndex: ReadInt(evt, "partial_image_index"),
                    PartialImageDataUrl: ReadPartialImageDataUrl(evt),
                    LastEventType: lastEventType,
                    EventCount: eventCount));
            }
            else if (lastEventType == "image_generation.completed")
            {
                return CompleteStreamingResult(evt, provider, imageModel, lastEventType, eventCount, progress);
            }
            else if (lastEventType == "error")
            {
                throw StreamError(evt, lastEventType, eventCount, progress);
            }
        }

        var exception = new ProjectImageProviderException(
            "OpenRouter ended the stream without an image.",
            "stream_ended_without_image",
            lastEventType: lastEventType,
            eventCount: eventCount);
        ReportProgress(progress, new ProjectImageProviderProgress(
            ProjectImageProviderProgressKind.StreamEndedWithoutImage,
            exception.Message,
            LastEventType: lastEventType,
            EventCount: eventCount));
        throw exception;
    }

    private ProjectImageProviderResult CompleteStreamingResult(
        JsonElement evt,
        ImageProvider provider,
        string imageModel,
        string? lastEventType,
        int eventCount,
        IProgress<ProjectImageProviderProgress>? progress)
    {
        var base64 = ReadString(evt, "b64_json");
        if (string.IsNullOrWhiteSpace(base64))
            throw new ProjectImageProviderException(
                "OpenRouter returned no image.",
                "missing_image",
                lastEventType: lastEventType,
                eventCount: eventCount);

        var image = DecodeImageBytes(base64, "invalid_image_result");
        var mediaType = ReadString(evt, "media_type");
        var (contentType, outputFormat) = ResolveImageFormat(image, mediaType);
        var metadataJson = SerializeMetadata(evt, mediaType);

        ReportProgress(progress, new ProjectImageProviderProgress(
            ProjectImageProviderProgressKind.Completed,
            "Image result received.",
            LastEventType: lastEventType,
            EventCount: eventCount));

        return new ProjectImageProviderResult(
            [new ProjectImageProviderImage(image, contentType, outputFormat, null, null, null, imageModel)],
            provider.Name,
            imageModel,
            metadataJson);
    }

    private ProjectImageProviderException StreamError(
        JsonElement evt,
        string? lastEventType,
        int eventCount,
        IProgress<ProjectImageProviderProgress>? progress)
    {
        var errorElement = evt.ValueKind == JsonValueKind.Object
            && evt.TryGetProperty("error", out var nested)
            && nested.ValueKind == JsonValueKind.Object
                ? nested
                : evt;
        var message = ReadString(errorElement, "message") ?? "The image provider rejected the request.";
        var code = ReadStringOrNumber(errorElement, "code");
        var kind = ClassifyStreamError(code, message);
        var exception = new ProjectImageProviderException(
            $"OpenRouter image generation failed ({kind}): {LogRedaction.RedactJson(message)}",
            kind,
            lastEventType: lastEventType,
            eventCount: eventCount,
            errorCode: code);
        ReportProgress(progress, new ProjectImageProviderProgress(
            ProjectImageProviderProgressKind.Failed,
            exception.Message,
            ErrorKind: kind,
            LastEventType: lastEventType,
            EventCount: eventCount));
        return exception;
    }

    private async Task ThrowHttpErrorAsync(
        HttpResponseMessage response,
        IProgress<ProjectImageProviderProgress>? progress,
        CancellationToken cancellationToken)
    {
        var statusCode = (int)response.StatusCode;
        var kind = ClassifyHttpError(statusCode);

        string body;
        using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            body = await ReadBoundedBodyAsync(stream, ErrorBodyMaximumBytes, cancellationToken);
        }

        var parsed = ParseProviderError(body);
        string message;
        string? errorCode = null;
        if (statusCode == 402)
        {
            message = "OpenRouter reports insufficient credits. Add credits at openrouter.ai.";
            errorCode = parsed.Code;
        }
        else if (statusCode == 502)
        {
            message = "OpenRouter could not generate the image.";
            errorCode = parsed.Code;
        }
        else
        {
            errorCode = parsed.Code;
            message = parsed.Message is null
                ? "The image provider rejected the request."
                : LogRedaction.RedactJson(parsed.Message);
        }

        logger.LogDebug(
            "OpenRouter image request failed: status={StatusCode}, code={ErrorCode}, error={Error}",
            statusCode,
            errorCode,
            LogRedaction.RedactJson(parsed.Message ?? "(no error message)"));

        var exception = new ProjectImageProviderException(
            $"OpenRouter image request returned {statusCode} ({kind}): {message}",
            kind,
            statusCode: statusCode,
            errorCode: errorCode,
            retryAfter: ReadRetryAfter(response));
        ReportProgress(progress, new ProjectImageProviderProgress(
            ProjectImageProviderProgressKind.Failed,
            exception.Message,
            ErrorKind: kind,
            StatusCode: statusCode));
        throw exception;
    }

    private byte[] DecodeImageBytes(string base64, string errorKind)
    {
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            throw new ProjectImageProviderException(
                "OpenRouter returned an image that could not be decoded.",
                errorKind,
                innerException: ex);
        }
    }

    private static (string ContentType, string OutputFormat) ResolveImageFormat(byte[] data, string? mediaType) =>
        mediaType switch
        {
            "image/png" => ("image/png", "png"),
            "image/jpeg" or "image/jpg" => ("image/jpeg", "jpeg"),
            "image/webp" => ("image/webp", "webp"),
            _ when SniffFormat(data) is { } sniffed => ($"image/{sniffed}", sniffed),
            _ => ("image/png", "png"),
        };

    private static string? SniffFormat(byte[] data)
    {
        if (data.Length >= 8
            && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
            && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
            return "png";
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return "jpeg";
        if (data.Length >= 12
            && data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
            && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
            return "webp";
        return null;
    }

    private static string SerializeMetadata(JsonElement context, string? mediaType)
    {
        var metadata = new Dictionary<string, object?>();
        if (context.ValueKind == JsonValueKind.Object)
        {
            if (context.TryGetProperty("created", out var createdElement))
                metadata["created"] = createdElement.Clone();
            if (context.TryGetProperty("usage", out var usageElement)
                && usageElement.ValueKind == JsonValueKind.Object)
                metadata["usage"] = usageElement.Clone();
        }

        if (mediaType is not null)
            metadata["media_type"] = mediaType;
        return JsonSerializer.Serialize(metadata, BodyOptions);
    }

    private static string? ReadPartialImageDataUrl(JsonElement evt)
    {
        var base64 = ReadString(evt, "b64_json");
        return string.IsNullOrWhiteSpace(base64) ? null : $"data:image/png;base64,{base64}";
    }

    private static Dictionary<string, object?> BuildBody(
        ImageRasterConstraints constraints,
        ImageProviderModel row,
        string prompt,
        string size,
        string outputFormat,
        string quality,
        int? outputCompression,
        string background,
        IReadOnlyList<ProjectImageProviderReference> references,
        bool stream)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = row.ModelId,
            ["prompt"] = prompt,
            ["n"] = 1,
            ["output_format"] = outputFormat,
        };

        if (constraints.UsesAspectTiers)
        {
            if (!LayoutImageSizeResolver.TryParse(size, out var parsedSize))
                throw new ProjectImageProviderException(
                    $"OpenRouter received an invalid image size '{size}'.",
                    "invalid_request");
            var (aspectRatio, resolution) = constraints.MapToAspectTier(parsedSize.Width, parsedSize.Height);
            if (aspectRatio is not null)
                body["aspect_ratio"] = aspectRatio;
            if (resolution is not null)
                body["resolution"] = resolution;
        }
        else
        {
            body["size"] = size;
        }

        if (!string.Equals(quality, "auto", StringComparison.OrdinalIgnoreCase))
            body["quality"] = quality;
        if (stream)
            body["stream"] = true;
        if (!string.Equals(background, "auto", StringComparison.OrdinalIgnoreCase))
            body["background"] = background;
        if (outputCompression is int compression && IsCompressedFormat(outputFormat))
            body["output_compression"] = compression;
        if (references.Count > 0)
            body["input_references"] = references.Select(ToReferencePayload).ToArray();

        return body;
    }

    private static Dictionary<string, object?> ToReferencePayload(ProjectImageProviderReference reference) =>
        new()
        {
            ["type"] = "image_url",
            ["image_url"] = new Dictionary<string, object?>
            {
                ["url"] = $"data:{reference.ContentType};base64,{Convert.ToBase64String(reference.Data)}",
            },
        };

    private static bool IsCompressedFormat(string outputFormat) =>
        outputFormat.Trim().ToLowerInvariant() is "jpeg" or "jpg" or "webp";

    private static ImageProviderModel ResolveModelRow(ImageProvider provider, string imageModel) =>
        provider.Models.FirstOrDefault(model => string.Equals(model.ModelId, imageModel, StringComparison.Ordinal))
        ?? throw new ProjectImageProviderException(
            $"OpenRouter model {imageModel} is not configured on this provider.",
            "invalid_request");

    private static string RequireApiKey(ImageProvider provider)
    {
        var apiKey = provider.ApiKey?.Trim();
        return string.IsNullOrWhiteSpace(apiKey)
            ? throw new ProjectImageProviderException(
                "An OpenRouter API key is required. Add it in Settings > Image providers.",
                "authentication_error")
            : apiKey;
    }

    private static void ApplyHeaders(HttpRequestMessage request, string apiKey, string accept)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Add("X-Title", "Lorekeeper");
        request.Headers.Accept.ParseAdd(accept);
    }

    private static string ClassifyHttpError(int statusCode) =>
        statusCode switch
        {
            401 or 403 => "authentication_error",
            402 => "insufficient_credits",
            400 or 404 or 422 => "invalid_request",
            429 => "rate_limit",
            >= 500 => "server_error",
            >= 400 => "invalid_request",
            _ => "server_error",
        };

    private static string ClassifyStreamError(string? code, string message)
    {
        if (code is not null && int.TryParse(code, out var statusCode))
            return ClassifyHttpError(statusCode);
        var codeText = code?.ToLowerInvariant() ?? string.Empty;
        var messageText = message.ToLowerInvariant();
        if (codeText.Contains("credit") || codeText.Contains("quota")
            || messageText.Contains("insufficient") || messageText.Contains("credit") || messageText.Contains("quota"))
            return "insufficient_credits";
        if (codeText.Contains("rate limit") || codeText.Contains("rate_limit")
            || messageText.Contains("rate limit") || messageText.Contains("rate_limit"))
            return "rate_limit";
        if (codeText.Contains("authentication") || codeText.Contains("api key")
            || codeText.Contains("unauthorized")
            || messageText.Contains("authentication") || messageText.Contains("api key")
            || messageText.Contains("unauthorized"))
            return "authentication_error";
        if (codeText.Contains("invalid") || codeText.Contains("not found") || codeText.Contains("model")
            || messageText.Contains("invalid") || messageText.Contains("not found"))
            return "invalid_request";
        return "server_error";
    }

    private static (string? Message, string? Code) ParseProviderError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object)
            {
                return (ReadString(error, "message"), ReadStringOrNumber(error, "code"));
            }
        }
        catch (JsonException)
        {
        }

        return (null, null);
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var delay = header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null);
        return delay > TimeSpan.Zero ? delay : null;
    }

    /// <summary>Base64 output plus JSON framing for one image within the configured byte limit.</summary>
    private long ResponseMaximumBytes =>
        Math.Max(1, (long)options.Value.MaxProviderOutputBytes) * 4 / 3 + 1024 * 1024;

    private static async Task<string> ReadCappedBodyAsync(
        Stream stream,
        long limit,
        string oversizedMessage,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        using var memory = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read <= 0)
                break;
            if (memory.Length + read > limit)
                throw new ProjectImageProviderException(oversizedMessage, "server_error");
            memory.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static async Task<string> ReadBoundedBodyAsync(Stream stream, int limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var memory = new MemoryStream();
        while (memory.Length < limit)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, limit - (int)memory.Length)),
                cancellationToken);
            if (read <= 0)
                break;
            memory.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static double? ReadRangeMaximum(JsonElement descriptor)
    {
        if (descriptor.ValueKind != JsonValueKind.Object)
            return null;
        if (!string.Equals(ReadString(descriptor, "type"), "range", StringComparison.Ordinal))
            return null;
        return descriptor.TryGetProperty("max", out var maximum)
            && maximum.ValueKind == JsonValueKind.Number
            && maximum.TryGetDouble(out var value)
                ? value
                : null;
    }

    private static IReadOnlyList<string> ReadArrayValues(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Array)
            return [];
        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 })
            .Select(item => item.GetString()!)
            .ToList();
    }

    private static JsonElement Parameter(JsonElement parameters, string name) =>
        parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out var value)
            ? value
            : default;

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadStringOrNumber(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetDouble(out var number) => number
                .ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    private static int? ReadInt(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var intValue)
            ? intValue
            : null;

    private static void ReportProgress(IProgress<ProjectImageProviderProgress>? progress, ProjectImageProviderProgress update) =>
        progress?.Report(update);

    public TimeSpan? GetRetryDelay(Exception error, int attempt) =>
        ImageProviderRetryPolicy.Default(error, attempt);
}
