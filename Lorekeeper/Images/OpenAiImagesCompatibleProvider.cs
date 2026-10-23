using System.Net;
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
/// Generates and edits images through the OpenAI Images API shape. Reference
/// images make a generation request a multipart edit, because the generations
/// endpoint accepts no references. Streaming models report partial images and
/// finish on a completed event.
/// </summary>
public sealed class OpenAiImagesCompatibleProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<ProjectImageGenerationOptions> options,
    ILogger<OpenAiImagesCompatibleProvider> logger) : IProjectImageProvider
{
    private const int ModelListMaximumBytes = 4 * 1024 * 1024;
    private const int ErrorBodyMaximumBytes = 64 * 1024;
    private const long DownloadMaximumBytes = 64L * 1024 * 1024;
    private const int PartialImageCount = 2;

    private static readonly JsonSerializerOptions BodyOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string[] MetadataProperties =
        ["created", "usage", "background", "output_format", "quality", "size"];

    private static readonly ImageModelCapabilities CompatibleImageCapabilities = new(
        ["auto", "low", "medium", "high"],
        ["png", "jpeg", "webp"],
        Generate: true,
        Edit: true,
        RegionalGuide: true,
        MaxReferenceImages: 16,
        TransparentBackground: true,
        Streaming: false);

    public ImageProviderKind Kind => ImageProviderKind.OpenAiImagesCompatible;

    public async Task<ProjectImageProviderResult> GenerateAsync(
        ImageProvider provider,
        ProjectImageProviderGenerateRequest request,
        CancellationToken cancellationToken = default,
        IProgress<ProjectImageProviderProgress>? progress = null)
    {
        var row = ResolveModelRow(provider, request.ImageModel);

        if (request.ReferenceImages.Count > 0)
        {
            var editContent = BuildEditContent(
                request.Prompt,
                request.ImageModel,
                request.Size,
                request.Quality,
                request.OutputFormat,
                request.OutputCompression,
                request.Background,
                request.ReferenceImages[0],
                mask: null,
                request.ReferenceImages.Skip(1),
                row.SupportsStreaming);
            return await SendImageRequestAsync(provider, "edits", editContent, request.ImageModel,
                row.SupportsStreaming, cancellationToken, progress);
        }

        var body = BuildGenerationBody(
            request.ImageModel,
            request.Prompt,
            request.Size,
            request.Quality,
            request.OutputFormat,
            request.OutputCompression,
            request.Background,
            row.SupportsStreaming);
        var content = new StringContent(
            JsonSerializer.Serialize(body, BodyOptions),
            Encoding.UTF8,
            "application/json");
        return await SendImageRequestAsync(provider, "generations", content, request.ImageModel,
            row.SupportsStreaming, cancellationToken, progress);
    }

    public async Task<ProjectImageProviderResult> EditAsync(
        ImageProvider provider,
        ProjectImageProviderEditRequest request,
        CancellationToken cancellationToken = default,
        IProgress<ProjectImageProviderProgress>? progress = null)
    {
        var row = ResolveModelRow(provider, request.ImageModel);

        var content = BuildEditContent(
            request.Prompt,
            request.ImageModel,
            request.Size,
            request.Quality,
            request.OutputFormat,
            request.OutputCompression,
            request.Background,
            request.SourceImage,
            request.Mask,
            request.ReferenceImages,
            row.SupportsStreaming);
        return await SendImageRequestAsync(provider, "edits", content, request.ImageModel,
            row.SupportsStreaming, cancellationToken, progress);
    }

    public async Task<ImageProviderTestResult> TestAsync(ImageProvider provider, CancellationToken cancellationToken = default)
    {
        try
        {
            var models = await DiscoverModelsAsync(provider, cancellationToken);
            var message = $"Endpoint reachable; {models.Count} image models listed.";
            if (models.Count == 0)
                message += " Add the model manually below.";
            return new ImageProviderTestResult(true, message);
        }
        catch (ProjectImageProviderException ex)
        {
            return new ImageProviderTestResult(false, ex.Message);
        }
        catch (HttpRequestException ex)
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
                "The image endpoint did not return its model list in time.",
                "request_timeout",
                innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ProjectImageProviderException(
                "The connection to the image endpoint failed.",
                "transport_error",
                statusCode: (int?)ex.StatusCode,
                innerException: ex);
        }
        catch (IOException ex)
        {
            throw new ProjectImageProviderException(
                "The image endpoint model list was interrupted.",
                "transport_error",
                innerException: ex);
        }
    }

    private async Task<IReadOnlyList<DiscoveredImageModel>> DiscoverModelsCoreAsync(
        ImageProvider provider,
        CancellationToken cancellationToken)
    {
        var baseUrl = ResolveBaseUrl(provider);

        var httpClient = httpClientFactory.CreateClient();
        httpClient.Timeout = Timeout.InfiniteTimeSpan;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models");
        ApplyHeaders(request, provider.ApiKey, "application/json");

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return [];
        if (!response.IsSuccessStatusCode)
        {
            await ThrowHttpErrorAsync(response, null, cancellationToken);
            return [];
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var body = await ReadCappedBodyAsync(
            stream,
            ModelListMaximumBytes,
            "The image endpoint returned an oversized model list.",
            cancellationToken);

        JsonElement data;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("data", out var value)
                || value.ValueKind != JsonValueKind.Array)
                return [];
            data = value.Clone();
        }
        catch (JsonException)
        {
            return [];
        }

        var models = new List<DiscoveredImageModel>();
        foreach (var entry in data.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;
            var modelId = ReadString(entry, "id")?.Trim();
            if (string.IsNullOrWhiteSpace(modelId))
                continue;

            if (ProjectImageModelCatalog.GptImageModels.FirstOrDefault(
                    model => string.Equals(model.ModelId, modelId, StringComparison.Ordinal)) is { } bundled)
            {
                models.Add(bundled);
                continue;
            }

            if (!modelId.Contains("image", StringComparison.OrdinalIgnoreCase))
                continue;

            models.Add(new DiscoveredImageModel(
                modelId,
                null,
                CompatibleImageCapabilities,
                ImageRasterConstraints.GptImage));
        }

        return models;
    }

    private async Task<ProjectImageProviderResult> SendImageRequestAsync(
        ImageProvider provider,
        string endpoint,
        HttpContent content,
        string imageModel,
        bool stream,
        CancellationToken cancellationToken,
        IProgress<ProjectImageProviderProgress>? progress)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.Value.RequestTimeoutSeconds, 1, 3600)));
        try
        {
            return await SendImageRequestCoreAsync(provider, endpoint, content, imageModel, stream, timeout.Token, progress);
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
                "The connection to the image endpoint failed.",
                "transport_error",
                statusCode: (int?)ex.StatusCode,
                innerException: ex);
        }
        catch (IOException ex)
        {
            throw new ProjectImageProviderException(
                "The image endpoint stream was interrupted.",
                "transport_error",
                innerException: ex);
        }
    }

    private async Task<ProjectImageProviderResult> SendImageRequestCoreAsync(
        ImageProvider provider,
        string endpoint,
        HttpContent content,
        string imageModel,
        bool stream,
        CancellationToken cancellationToken,
        IProgress<ProjectImageProviderProgress>? progress)
    {
        var baseUrl = ResolveBaseUrl(provider);

        var httpClient = httpClientFactory.CreateClient();
        httpClient.Timeout = Timeout.InfiniteTimeSpan;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/images/{endpoint}");
        ApplyHeaders(request, provider.ApiKey, stream ? "text/event-stream" : "application/json");
        request.Content = content;

        ReportProgress(progress, new ProjectImageProviderProgress(ProjectImageProviderProgressKind.Started, "Image request started."));

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            await ThrowHttpErrorAsync(response, progress, cancellationToken);

        // Some compatible servers ignore stream and answer with plain JSON.
        var streamed = stream
            && !string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase);
        return streamed
            ? await ReadStreamingResultAsync(response, provider, imageModel, cancellationToken, progress)
            : await ReadJsonResultAsync(response, httpClient, provider, imageModel, cancellationToken, progress);
    }

    private async Task<ProjectImageProviderResult> ReadJsonResultAsync(
        HttpResponseMessage response,
        HttpClient httpClient,
        ImageProvider provider,
        string imageModel,
        CancellationToken cancellationToken,
        IProgress<ProjectImageProviderProgress>? progress)
    {
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await ReadCappedBodyAsync(
            stream,
            ResponseMaximumBytes,
            "The image endpoint returned an image larger than the configured output limit.",
            cancellationToken);

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(payload);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new ProjectImageProviderException("The image endpoint returned no image.", "missing_image");
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

        var revisedPrompt = ReadString(item, "revised_prompt");
        byte[] image;
        var base64 = ReadString(item, "b64_json");
        if (!string.IsNullOrWhiteSpace(base64))
        {
            image = DecodeImageBytes(base64, "invalid_image_result");
        }
        else if (ReadString(item, "url") is { Length: > 0 } url)
        {
            image = await DownloadImageAsync(httpClient, url, cancellationToken);
        }
        else
        {
            throw new ProjectImageProviderException("The image endpoint returned no image.", "missing_image");
        }

        var reportedFormat = ReadString(root, "output_format");
        var (contentType, outputFormat) = ResolveImageFormat(image, reportedFormat);
        var metadataJson = SerializeMetadata(root);

        ReportProgress(progress, new ProjectImageProviderProgress(ProjectImageProviderProgressKind.Completed, "Image result received."));

        return new ProjectImageProviderResult(
            [new ProjectImageProviderImage(
                image,
                contentType,
                outputFormat,
                revisedPrompt,
                null,
                null,
                imageModel,
                ReportedQuality: ReadString(root, "quality"),
                ReportedBackground: ReadString(root, "background"),
                ReportedSize: ReadString(root, "size"),
                ReportedOutputFormat: reportedFormat)],
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

            if (lastEventType is "image_generation.partial_image" or "image_edit.partial_image")
            {
                ReportProgress(progress, new ProjectImageProviderProgress(
                    ProjectImageProviderProgressKind.PartialImage,
                    "Partial image received.",
                    PartialImageIndex: ReadInt(evt, "partial_image_index"),
                    PartialImageDataUrl: ReadPartialImageDataUrl(evt),
                    LastEventType: lastEventType,
                    EventCount: eventCount));
            }
            else if (lastEventType is "image_generation.completed" or "image_edit.completed")
            {
                return CompleteStreamingResult(evt, provider, imageModel, lastEventType, eventCount, progress);
            }
            else if (lastEventType == "error")
            {
                throw StreamError(evt, lastEventType, eventCount, progress);
            }
        }

        var exception = new ProjectImageProviderException(
            "The image endpoint ended the stream without an image.",
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
                "The image endpoint returned no image.",
                "missing_image",
                lastEventType: lastEventType,
                eventCount: eventCount);

        var image = DecodeImageBytes(base64, "invalid_image_result");
        var reportedFormat = ReadString(evt, "output_format");
        var (contentType, outputFormat) = ResolveImageFormat(image, reportedFormat);
        var metadataJson = SerializeMetadata(evt);

        ReportProgress(progress, new ProjectImageProviderProgress(
            ProjectImageProviderProgressKind.Completed,
            "Image result received.",
            LastEventType: lastEventType,
            EventCount: eventCount));

        return new ProjectImageProviderResult(
            [new ProjectImageProviderImage(
                image,
                contentType,
                outputFormat,
                ReadString(evt, "revised_prompt"),
                null,
                null,
                imageModel,
                ReportedQuality: ReadString(evt, "quality"),
                ReportedBackground: ReadString(evt, "background"),
                ReportedSize: ReadString(evt, "size"),
                ReportedOutputFormat: reportedFormat)],
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
        var message = ReadString(errorElement, "message") ?? "The image endpoint rejected the request.";
        var code = ReadStringOrNumber(errorElement, "code");
        var errorType = ReadString(errorElement, "type");
        var kind = ClassifyStreamError(code, message);
        var exception = new ProjectImageProviderException(
            $"Image endpoint failed ({kind}): {LogRedaction.RedactJson(message)}",
            kind,
            lastEventType: lastEventType,
            eventCount: eventCount,
            errorCode: code,
            errorType: errorType);
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
        var message = parsed.Message is null
            ? "The image endpoint rejected the request."
            : LogRedaction.RedactJson(parsed.Message);

        logger.LogDebug(
            "OpenAI Images-compatible request failed: status={StatusCode}, code={ErrorCode}, type={ErrorType}, error={Error}",
            statusCode,
            parsed.Code,
            parsed.Type,
            LogRedaction.RedactJson(parsed.Message ?? "(no error message)"));

        var exception = new ProjectImageProviderException(
            $"Image endpoint returned {statusCode} ({kind}): {message}",
            kind,
            statusCode: statusCode,
            errorCode: parsed.Code,
            errorType: parsed.Type,
            retryAfter: ReadRetryAfter(response));
        ReportProgress(progress, new ProjectImageProviderProgress(
            ProjectImageProviderProgressKind.Failed,
            exception.Message,
            ErrorKind: kind,
            StatusCode: statusCode));
        throw exception;
    }

    private async Task<byte[]> DownloadImageAsync(
        HttpClient httpClient,
        string url,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            throw new ProjectImageProviderException(
                "The image endpoint returned an image URL that is neither https nor loopback http.",
                "invalid_response");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("image/*");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ProjectImageProviderException(
                $"The image download returned {(int)response.StatusCode}.",
                "invalid_response",
                statusCode: (int)response.StatusCode);
        }

        if (response.Content.Headers.ContentLength is { } length && length > DownloadMaximumBytes)
        {
            throw new ProjectImageProviderException(
                "The downloaded image exceeds the 64 MiB limit.",
                "invalid_response");
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await ReadCappedBytesAsync(stream, DownloadMaximumBytes, cancellationToken);
    }

    private static Dictionary<string, object?> BuildGenerationBody(
        string imageModel,
        string prompt,
        string size,
        string quality,
        string outputFormat,
        int? outputCompression,
        string background,
        bool stream)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = imageModel,
            ["prompt"] = prompt,
            ["n"] = 1,
            ["size"] = size,
            ["output_format"] = outputFormat,
        };

        if (!IsAuto(quality))
            body["quality"] = quality;
        if (!IsAuto(background))
            body["background"] = background;
        if (outputCompression is int compression && IsCompressedFormat(outputFormat))
            body["output_compression"] = compression;
        if (stream)
        {
            body["stream"] = true;
            body["partial_images"] = PartialImageCount;
        }

        return body;
    }

    private static MultipartFormDataContent BuildEditContent(
        string prompt,
        string imageModel,
        string size,
        string quality,
        string outputFormat,
        int? outputCompression,
        string background,
        ProjectImageProviderReference source,
        ProjectImageProviderReference? mask,
        IEnumerable<ProjectImageProviderReference> referenceImages,
        bool stream)
    {
        var content = new MultipartFormDataContent();
        AddFilePart(content, "image[]", source);
        foreach (var reference in referenceImages)
            AddFilePart(content, "image[]", reference);
        if (mask is not null)
            AddFilePart(content, "mask", mask);

        content.Add(new StringContent(prompt), "prompt");
        content.Add(new StringContent(imageModel), "model");
        content.Add(new StringContent("1"), "n");
        content.Add(new StringContent(size), "size");
        if (!IsAuto(quality))
            content.Add(new StringContent(quality), "quality");
        if (!IsAuto(background))
            content.Add(new StringContent(background), "background");
        content.Add(new StringContent(outputFormat), "output_format");
        if (outputCompression is int compression && IsCompressedFormat(outputFormat))
            content.Add(new StringContent(compression.ToString(System.Globalization.CultureInfo.InvariantCulture)), "output_compression");
        if (stream)
        {
            content.Add(new StringContent("true"), "stream");
            content.Add(new StringContent(PartialImageCount.ToString(System.Globalization.CultureInfo.InvariantCulture)), "partial_images");
        }

        return content;
    }

    private static void AddFilePart(MultipartFormDataContent content, string name, ProjectImageProviderReference reference)
    {
        var part = new ByteArrayContent(reference.Data);
        if (MediaTypeHeaderValue.TryParse(reference.ContentType, out var mediaType))
            part.Headers.ContentType = mediaType;
        content.Add(part, name, reference.FileName);
    }

    private static byte[] DecodeImageBytes(string base64, string errorKind)
    {
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            throw new ProjectImageProviderException(
                "The image endpoint returned an image that could not be decoded.",
                errorKind,
                innerException: ex);
        }
    }

    private static (string ContentType, string OutputFormat) ResolveImageFormat(byte[] data, string? reportedFormat) =>
        (reportedFormat ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "png" => ("image/png", "png"),
            "jpeg" or "jpg" => ("image/jpeg", "jpeg"),
            "webp" => ("image/webp", "webp"),
            _ => SniffFormat(data) switch
            {
                "jpeg" => ("image/jpeg", "jpeg"),
                "webp" => ("image/webp", "webp"),
                _ => ("image/png", "png"),
            },
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

    private static string SerializeMetadata(JsonElement context)
    {
        var metadata = new Dictionary<string, object?>();
        if (context.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in MetadataProperties)
            {
                if (context.TryGetProperty(property, out var value))
                    metadata[property] = value.Clone();
            }
        }

        return JsonSerializer.Serialize(metadata, BodyOptions);
    }

    private static string? ReadPartialImageDataUrl(JsonElement evt)
    {
        var base64 = ReadString(evt, "b64_json");
        if (string.IsNullOrWhiteSpace(base64))
            return null;
        var contentType = (ReadString(evt, "output_format") ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "jpeg" or "jpg" => "image/jpeg",
            "webp" => "image/webp",
            _ => "image/png",
        };
        return $"data:{contentType};base64,{base64}";
    }

    private static bool IsAuto(string? value) =>
        string.Equals(value?.Trim(), "auto", StringComparison.OrdinalIgnoreCase);

    private static bool IsCompressedFormat(string outputFormat) =>
        outputFormat.Trim().ToLowerInvariant() is "jpeg" or "jpg" or "webp";

    private static ImageProviderModel ResolveModelRow(ImageProvider provider, string imageModel) =>
        provider.Models.FirstOrDefault(model => string.Equals(model.ModelId, imageModel, StringComparison.Ordinal))
        ?? throw new ProjectImageProviderException(
            $"Image model {imageModel} is not configured on this provider.",
            "invalid_request");

    private static string ResolveBaseUrl(ImageProvider provider) =>
        !string.IsNullOrWhiteSpace(provider.EndpointUrl)
            ? provider.EndpointUrl.Trim().TrimEnd('/')
            : throw new ProjectImageProviderException(
                "An OpenAI Images-compatible endpoint URL is required. Add it in Settings > Image providers.",
                "invalid_request");

    private static void ApplyHeaders(HttpRequestMessage request, string? apiKey, string accept)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Headers.Accept.ParseAdd(accept);
    }

    private static string ClassifyHttpError(int statusCode) =>
        statusCode switch
        {
            401 or 403 => "authentication_error",
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
        if (codeText.Contains("authentication") || codeText.Contains("api key") || codeText.Contains("api_key")
            || codeText.Contains("unauthorized")
            || messageText.Contains("authentication") || messageText.Contains("api key")
            || messageText.Contains("unauthorized"))
            return "authentication_error";
        if (codeText.Contains("invalid") || codeText.Contains("not found") || codeText.Contains("model")
            || messageText.Contains("invalid") || messageText.Contains("not found"))
            return "invalid_request";
        return "server_error";
    }

    private static (string? Message, string? Code, string? Type) ParseProviderError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object)
            {
                return (ReadString(error, "message"), ReadStringOrNumber(error, "code"), ReadString(error, "type"));
            }
        }
        catch (JsonException)
        {
        }

        return (null, null, null);
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

    private static async Task<byte[]> ReadCappedBytesAsync(
        Stream stream,
        long limit,
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
            {
                throw new ProjectImageProviderException(
                    "The downloaded image exceeds the 64 MiB limit.",
                    "invalid_response");
            }

            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
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
