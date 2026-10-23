using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Lorekeeper.Authorization;
using Lorekeeper.Diagnostics;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Images;

/// <summary>
/// Generates images through a signed-in ChatGPT (Codex) account with the Codex
/// image endpoints: images/generations for a text-only request and images/edits
/// whenever input images are supplied, the edit source first. No chat model is
/// involved. The backend ignores size and quality; the prompt states the
/// target aspect, and every output holds about 1.57 MP. ChatGPT plan sign-in
/// cannot serve this route.
/// </summary>
public sealed class ChatGptAccountImageProvider(
    IOpenAiAccountTokenService accountTokens,
    IHttpClientFactory httpClientFactory,
    IOptions<ProjectImageGenerationOptions> options,
    ILogger<ChatGptAccountImageProvider> logger) : IProjectImageProvider
{
    private const string ReconnectMessage =
        "The ChatGPT (Codex) sign-in was rejected. Reconnect the account in Settings > Providers, then try again.";

    private static readonly JsonSerializerOptions MetadataJsonOptions = new(JsonSerializerDefaults.Web);

    public ImageProviderKind Kind => ImageProviderKind.ChatGptAccount;

    public async Task<ProjectImageProviderResult> GenerateAsync(
        ImageProvider provider,
        ProjectImageProviderGenerateRequest request,
        CancellationToken cancellationToken = default,
        IProgress<ProjectImageProviderProgress>? progress = null)
    {
        var inputs = request.ReferenceImages.Take(options.Value.MaxReferenceImages).ToList();
        return await RunAsync(
            provider,
            new CodexImageRequest(request.Prompt, request.Size, request.Quality, request.Background,
                RequireImageModel(request.ImageModel), inputs, request.OutputFormat, request.OutputCompression),
            request.Count,
            cancellationToken,
            progress);
    }

    public async Task<ProjectImageProviderResult> EditAsync(
        ImageProvider provider,
        ProjectImageProviderEditRequest request,
        CancellationToken cancellationToken = default,
        IProgress<ProjectImageProviderProgress>? progress = null)
    {
        var inputs = new List<ProjectImageProviderReference> { request.SourceImage };
        inputs.AddRange(request.ReferenceImages.Take(options.Value.MaxReferenceImages));
        return await RunAsync(
            provider,
            new CodexImageRequest(request.Prompt, request.Size, request.Quality, request.Background,
                RequireImageModel(request.ImageModel), inputs, request.OutputFormat, request.OutputCompression),
            request.Count,
            cancellationToken,
            progress);
    }

    public async Task<ImageProviderTestResult> TestAsync(ImageProvider provider, CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await ResolveConnectionAsync(provider, cancellationToken);
            return new ImageProviderTestResult(true, "The ChatGPT (Codex) account is signed in. Image requests use the account's image allowance.");
        }
        catch (InvalidOperationException ex)
        {
            return new ImageProviderTestResult(false, ex.Message);
        }
    }

    public Task<IReadOnlyList<DiscoveredImageModel>> DiscoverModelsAsync(
        ImageProvider provider,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(ProjectImageModelCatalog.CodexImageModels);

    public TimeSpan? GetRetryDelay(Exception error, int attempt)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is not ProjectImageProviderException providerError)
                continue;
            var imageInputLimit = providerError.ErrorKind == "codex_image_input_rate_limit";
            if (!imageInputLimit && !ImageProviderRetryPolicy.IsTransient(providerError))
                return null;
            if (providerError.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero)
                return retryAfter;
            if (ReadMessageRetryDelay(providerError) is { } hinted)
                return hinted;
            return imageInputLimit || IsPerMinuteImageLimit(providerError.Message)
                ? TimeSpan.FromSeconds(Math.Min(30, 10 * attempt))
                : ImageProviderRetryPolicy.Backoff(attempt);
        }

        return null;
    }

    /// <summary>
    /// Reads the backend's "Please try again in N s/ms" hint. Per-minute image
    /// limits get at least ten seconds, since a shorter wait fails again.
    /// </summary>
    private static TimeSpan? ReadMessageRetryDelay(ProjectImageProviderException error)
    {
        const string marker = "Please try again in ";
        var message = error.Message;
        var markerIndex = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return null;

        var valueStart = markerIndex + marker.Length;
        while (valueStart < message.Length && char.IsWhiteSpace(message[valueStart]))
            valueStart++;

        var valueEnd = valueStart;
        while (valueEnd < message.Length && (char.IsDigit(message[valueEnd]) || message[valueEnd] == '.'))
            valueEnd++;

        if (valueEnd == valueStart
            || !double.TryParse(message[valueStart..valueEnd], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        var unit = message[valueEnd..].TrimStart();
        TimeSpan delay;
        if (unit.StartsWith("ms", StringComparison.OrdinalIgnoreCase)
            || unit.StartsWith("millisecond", StringComparison.OrdinalIgnoreCase))
            delay = TimeSpan.FromMilliseconds(value);
        else if (unit.StartsWith("s", StringComparison.OrdinalIgnoreCase))
            delay = TimeSpan.FromSeconds(value);
        else
            return null;

        if (delay <= TimeSpan.Zero)
            return null;
        var minimum = IsPerMinuteImageLimit(message) ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(500);
        return delay < minimum ? minimum : delay;
    }

    private static bool IsPerMinuteImageLimit(string message) =>
        message.Contains("per min", StringComparison.OrdinalIgnoreCase)
        && message.Contains("image", StringComparison.OrdinalIgnoreCase);

    private async Task<ProjectImageProviderResult> RunAsync(
        ImageProvider provider,
        CodexImageRequest request,
        int count,
        CancellationToken cancellationToken,
        IProgress<ProjectImageProviderProgress>? progress)
    {
        var connection = await ResolveConnectionAsync(provider, cancellationToken);
        var endpoint = request.Inputs.Count > 0 ? CodexProvider.ImageEditsEndpoint : CodexProvider.ImageGenerationsEndpoint;
        var json = JsonSerializer.Serialize(BuildPayload(request));
        var images = new List<ProjectImageProviderImage>();
        var metadata = new List<object>();

        for (var i = 0; i < count; i++)
        {
            var result = await SendImageRequestAsync(connection, endpoint, json, request, cancellationToken, progress);
            images.Add(result.Image);
            metadata.Add(result.Metadata);
        }

        return new ProjectImageProviderResult(
            images,
            provider.Name,
            request.ImageModel,
            JsonSerializer.Serialize(metadata, MetadataJsonOptions));
    }

    private async Task<CodexImageConnection> ResolveConnectionAsync(ImageProvider provider, CancellationToken cancellationToken)
    {
        // ChatGPT plan sign-in does not permit image generation; only a Codex account can serve it.
        if (provider.OpenAiAccountId is not { } accountId)
            throw new InvalidOperationException($"Image provider '{provider.DisplayName ?? provider.Name}' has no ChatGPT account. Choose one in Settings > Image providers.");

        var access = await accountTokens.GetValidAccessAsync(accountId, cancellationToken);
        if (access is null)
            throw new InvalidOperationException("Connect or reauthenticate the ChatGPT (Codex) account in Settings > Providers before generating images.");
        if (access.Kind != OpenAiAccountKind.Codex)
            throw new InvalidOperationException("ChatGPT plan sign-in cannot generate images. Choose the ChatGPT (Codex) account in Settings > Image providers.");

        return new CodexImageConnection(accountId, access.AccessToken, access.ExternalAccountId);
    }

    private static string RequireImageModel(string imageModel) =>
        string.IsNullOrWhiteSpace(imageModel)
            ? throw new ArgumentException("An image model is required.", nameof(imageModel))
            : imageModel.Trim();

    private static Dictionary<string, object?> BuildPayload(CodexImageRequest request)
    {
        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = request.Prompt,
            ["model"] = request.ImageModel,
            ["size"] = string.IsNullOrWhiteSpace(request.Size) ? "auto" : request.Size.Trim(),
            ["quality"] = string.IsNullOrWhiteSpace(request.Quality) ? "auto" : request.Quality.Trim(),
            ["background"] = ProjectImageModelCatalog.NormalizeBackground(request.Background),
        };
        if (request.Inputs.Count > 0)
        {
            payload["images"] = request.Inputs
                .Select(input => new Dictionary<string, object?> { ["image_url"] = DataUrl.ToDataUrl(input.ContentType, input.Data) })
                .ToArray();
        }

        return payload;
    }

    private async Task<CodexImageReadResult> SendImageRequestAsync(
        CodexImageConnection connection,
        string endpoint,
        string json,
        CodexImageRequest request,
        CancellationToken cancellationToken,
        IProgress<ProjectImageProviderProgress>? progress)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.Value.RequestTimeoutSeconds, 1, 3600)));
        try
        {
            return await SendImageRequestCoreAsync(connection, endpoint, json, request, timeout.Token, progress);
        }
        catch (ProjectImageProviderException ex)
        {
            ReportProgress(progress, FailedProgress(ex));
            throw;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failed(progress, new ProjectImageProviderException("The image request timed out before an image was received.",
                "request_timeout", innerException: ex));
        }
        catch (HttpRequestException ex)
        {
            throw Failed(progress, new ProjectImageProviderException("The connection to the image provider failed.",
                "transport_error", statusCode: (int?)ex.StatusCode, innerException: ex));
        }
        catch (IOException ex)
        {
            throw Failed(progress, new ProjectImageProviderException("The image provider response was interrupted.",
                "transport_error", innerException: ex));
        }
    }

    private static ProjectImageProviderException Failed(IProgress<ProjectImageProviderProgress>? progress, ProjectImageProviderException exception)
    {
        ReportProgress(progress, FailedProgress(exception));
        return exception;
    }

    private async Task<CodexImageReadResult> SendImageRequestCoreAsync(
        CodexImageConnection connection,
        string endpoint,
        string json,
        CodexImageRequest request,
        CancellationToken cancellationToken,
        IProgress<ProjectImageProviderProgress>? progress)
    {
        var stopwatch = Stopwatch.StartNew();
        var httpClient = httpClientFactory.CreateClient();
        httpClient.Timeout = Timeout.InfiniteTimeSpan;
        ReportProgress(progress, new ProjectImageProviderProgress(ProjectImageProviderProgressKind.Started, "Image request started."));

        var response = await PostAsync(httpClient, connection, endpoint, json, cancellationToken);
        try
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // A token can be invalidated before its recorded expiry; refresh once and retry.
                response.Dispose();
                var refreshed = await accountTokens.RefreshRejectedAccessAsync(connection.AccountRowId, connection.Token, cancellationToken);
                if (refreshed is null)
                    throw new ProjectImageProviderException(ReconnectMessage, "authentication_error", statusCode: 401);

                connection.Token = refreshed.AccessToken;
                connection.AccountId = refreshed.ExternalAccountId;
                response = await PostAsync(httpClient, connection, endpoint, json, cancellationToken);
            }

            var requestId = ReadResponseRequestId(response);
            var body = await ReadBodyAsync(response.Content, MaxResponseBytes(), cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw CreateHttpError(response, body, requestId);

            var result = ReadImageResult(response, body, request, endpoint, requestId);
            logger.LogDebug(
                "Codex image result received: requestId={RequestId}, generationId={GenerationId}, imageModel={ImageModel}, route={Route}, raster={Width}x{Height}, imageBytes={ImageBytes}, elapsedMs={ElapsedMs}",
                requestId,
                result.Image.ResponseId,
                request.ImageModel,
                RouteName(endpoint),
                result.Width,
                result.Height,
                result.Image.Data.Length,
                stopwatch.Elapsed.TotalMilliseconds);
            ReportProgress(progress, new ProjectImageProviderProgress(
                ProjectImageProviderProgressKind.Completed,
                "Image result received.",
                requestId,
                result.Image.ResponseId));
            return result;
        }
        finally
        {
            response.Dispose();
        }
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient httpClient,
        CodexImageConnection connection,
        string endpoint,
        string json,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(json, Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        CodexProvider.ApplyAccountIdentity(request, connection.Token, connection.AccountId);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    /// <summary>The response cap: a base64 PNG at the provider output limit plus JSON overhead.</summary>
    private long MaxResponseBytes() =>
        (long)Math.Max(1, options.Value.MaxProviderOutputBytes) * 4 / 3 + 1024 * 1024;

    private static async Task<byte[]> ReadBodyAsync(HttpContent content, long maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maxBytes)
            throw TooLarge();

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw TooLarge();
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();

        static ProjectImageProviderException TooLarge() =>
            new("The image provider response is larger than the configured provider output limit.", "invalid_image_result");
    }

    private CodexImageReadResult ReadImageResult(
        HttpResponseMessage response,
        byte[] body,
        CodexImageRequest request,
        string endpoint,
        string? requestId)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new ProjectImageProviderException("The image provider returned a response that was not valid JSON.",
                "invalid_image_result", requestId, innerException: ex);
        }

        using (document)
        {
            var root = document.RootElement;
            var item = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Array
                && data.GetArrayLength() > 0
                    ? data[0]
                    : default;
            var base64 = item.ValueKind == JsonValueKind.Object ? ReadString(item, "b64_json") : null;
            if (string.IsNullOrWhiteSpace(base64))
            {
                throw new ProjectImageProviderException("The image provider completed without returning an image.",
                    "image_missing", requestId, statusCode: (int)response.StatusCode);
            }

            var generationId = ReadString(item, "generation_id");
            NormalizedProjectImage decoded;
            try
            {
                decoded = ProjectImageBinary.DecodeProviderOutput(
                    Convert.FromBase64String(base64),
                    NormalizeOutputFormat(request.OutputFormat),
                    request.OutputCompression,
                    Math.Max(1, options.Value.MaxProviderOutputBytes));
            }
            catch (Exception ex) when (ex is FormatException or InvalidDataException)
            {
                throw new ProjectImageProviderException($"The image provider returned an unusable image: {ex.Message}",
                    "invalid_image_result", requestId, generationId, innerException: ex);
            }

            var outputFormat = decoded.ContentType["image/".Length..];
            var reportedSize = ReadString(root, "size");
            var reportedQuality = ReadString(root, "quality");
            var reportedBackground = ReadString(root, "background");
            var reportedOutputFormat = ReadString(root, "output_format");
            return new CodexImageReadResult(
                new ProjectImageProviderImage(
                    decoded.Data,
                    decoded.ContentType,
                    outputFormat,
                    RevisedPrompt: null,
                    ResponseId: generationId,
                    CallId: null,
                    ReportedModel: null,
                    reportedQuality,
                    reportedBackground,
                    reportedSize,
                    reportedOutputFormat),
                new
                {
                    Route = RouteName(endpoint),
                    RequestId = requestId,
                    GenerationId = generationId,
                    Created = ReadLong(root, "created"),
                    InputImages = request.Inputs.Count,
                    ReportedSize = reportedSize,
                    ReportedQuality = reportedQuality,
                    ReportedBackground = reportedBackground,
                    ReportedOutputFormat = reportedOutputFormat,
                    DecodedRaster = $"{decoded.Width}x{decoded.Height}",
                    OutputFormat = outputFormat,
                    Usage = root.TryGetProperty("usage", out var usage) ? usage.Clone() : (JsonElement?)null,
                    Allowance = ReadAllowance(response),
                },
                decoded.Width,
                decoded.Height);
        }
    }

    private static ProjectImageProviderException CreateHttpError(HttpResponseMessage response, byte[] body, string? requestId)
    {
        var statusCode = (int)response.StatusCode;
        var error = ParseError(body);
        var errorKind = ClassifyImageError(error, statusCode);
        var message = $"Codex image request returned {statusCode} ({errorKind}): {error.Message}";
        if (errorKind == "quota_exhausted" && ReadAllowanceReset(response) is { } resetAt)
            message += $" The ChatGPT image allowance resets at {resetAt.ToLocalTime():g}.";
        return new ProjectImageProviderException(
            errorKind == "authentication_error" ? ReconnectMessage : message,
            errorKind,
            requestId,
            statusCode: statusCode,
            errorCode: error.Code,
            errorType: error.Type,
            retryAfter: ReadRetryAfter(response));
    }

    private static object ReadAllowance(HttpResponseMessage response) => new
    {
        ActiveLimit = ReadHeader(response, "x-codex-active-limit"),
        PlanType = ReadHeader(response, "x-codex-plan-type"),
        PrimaryUsedPercent = ReadHeader(response, "x-codex-primary-used-percent"),
        PrimaryWindowMinutes = ReadHeader(response, "x-codex-primary-window-minutes"),
        PrimaryResetAt = ReadAllowanceReset(response),
    };

    private static DateTimeOffset? ReadAllowanceReset(HttpResponseMessage response) =>
        long.TryParse(ReadHeader(response, "x-codex-primary-reset-at"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;

    private static string RouteName(string endpoint) =>
        endpoint == CodexProvider.ImageEditsEndpoint ? "edits" : "generations";

    private static string NormalizeOutputFormat(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => "jpeg",
            "webp" => "webp",
            _ => "png",
        };

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ReadLong(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : null;

    private static ProjectImageProviderProgress FailedProgress(ProjectImageProviderException exception) =>
        new(
            ProjectImageProviderProgressKind.Failed,
            exception.Message,
            exception.RequestId,
            exception.ResponseId,
            exception.CallId,
            ErrorKind: exception.ErrorKind,
            StatusCode: exception.StatusCode);

    private static void ReportProgress(IProgress<ProjectImageProviderProgress>? progress, ProjectImageProviderProgress update) =>
        progress?.Report(update);

    private static string? ReadResponseRequestId(HttpResponseMessage response) =>
        ReadHeader(response, "x-codex-imagegen-request-id")
        ?? ReadHeader(response, "x-request-id")
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

    private static string ClassifyImageError(ImageProviderError error, int statusCode)
    {
        var message = error.Message;
        var code = error.Code?.ToLowerInvariant();
        var type = error.Type?.ToLowerInvariant();
        if (code == "moderation_blocked" || type == "moderation_blocked") return "moderation_blocked";
        if (code == "image_generation_user_error" || type == "image_generation_user_error") return "image_generation_user_error";
        if (code is "insufficient_quota" or "billing_hard_limit_reached" or "billing_not_active" or "usage_limit_reached"
            || type is "insufficient_quota" or "usage_limit_reached"
            || message.Contains("quota", StringComparison.OrdinalIgnoreCase)
            || message.Contains("billing", StringComparison.OrdinalIgnoreCase)
            || message.Contains("usage limit", StringComparison.OrdinalIgnoreCase))
            return "quota_exhausted";
        if (statusCode == 401 || type == "authentication_error" || code is "invalid_api_key" or "invalid_token" or "token_expired" or "token_revoked")
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

    /// <summary>Reads an error from {"error": {...}}, {"detail": {...}}, or {"detail": "..."}.</summary>
    private static ImageProviderError ParseError(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var element = document.RootElement;
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                    element = error;
                else if (element.TryGetProperty("detail", out var detail))
                    element = detail;
            }

            var message = element.ValueKind == JsonValueKind.String ? element.GetString() : ReadString(element, "message");
            return new ImageProviderError(
                LogRedaction.RedactJson(string.IsNullOrWhiteSpace(message) ? "The image provider rejected the request." : message),
                ReadString(element, "code"),
                ReadString(element, "type"));
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

    private sealed record CodexImageRequest(
        string Prompt,
        string Size,
        string Quality,
        string Background,
        string ImageModel,
        IReadOnlyList<ProjectImageProviderReference> Inputs,
        string OutputFormat,
        int? OutputCompression);

    /// <summary>The account a request signs with; a refresh after a rejected token updates it for later outputs.</summary>
    private sealed class CodexImageConnection(int accountRowId, string token, string accountId)
    {
        public int AccountRowId { get; } = accountRowId;
        public string Token { get; set; } = token;
        public string AccountId { get; set; } = accountId;
    }

    private sealed record CodexImageReadResult(ProjectImageProviderImage Image, object Metadata, int Width, int Height);
}
