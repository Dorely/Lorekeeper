using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Lorekeeper.Authorization;
using Lorekeeper.Diagnostics;
using Lorekeeper.Models;

namespace Lorekeeper.Llm;

public sealed class OpenAiAccountModelDiscovery(
    IHttpClientFactory httpClientFactory,
    ILogger<OpenAiAccountModelDiscovery> logger) : IOpenAiAccountModelDiscovery
{
    private const int MaxResponseBytes = 1024 * 1024;
    private const int DefaultEffectiveContextPercent = 95;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly string ClientVersion = ResolveClientVersion();

    public async Task<IReadOnlyList<DiscoveredAccountModel>> DiscoverAsync(
        OpenAiAccountAccess access,
        CancellationToken cancellationToken = default)
    {
        var plan = access.Kind == OpenAiAccountKind.ChatGptPlan;
        using var request = CreateRequest(access, plan);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            var httpClient = httpClientFactory.CreateClient();
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            var body = await ReadBodyAsync(response, timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "ChatGPT account model list returned {StatusCode}: {Body}",
                    (int)response.StatusCode,
                    LogRedaction.RedactJson(Encoding.UTF8.GetString(body)));
                if (plan && ReadErrorCode(body) == "subscription_sharing_user_not_eligible")
                    throw new OpenAiAccountModelDiscoveryException(CodexProvider.PlanNotEligibleMessage) { PlanNotEligible = true };
                throw CreateStatusException(response.StatusCode);
            }

            return Parse(body, plan);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OpenAiAccountModelDiscoveryException("The account model list request timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new OpenAiAccountModelDiscoveryException("The account model list could not be reached.", ex);
        }
    }

    private static HttpRequestMessage CreateRequest(OpenAiAccountAccess access, bool plan)
    {
        if (!plan)
        {
            var codexRequest = new HttpRequestMessage(
                HttpMethod.Get,
                $"{CodexProvider.ModelsEndpoint}?client_version={ClientVersion}");
            CodexProvider.ApplyAccountHeaders(codexRequest, access.AccessToken, access.ExternalAccountId, eventStream: false);
            return codexRequest;
        }

        // ChatGPT plan usage authorizes with the bearer token alone.
        var request = new HttpRequestMessage(HttpMethod.Get, CodexProvider.PlanModelsEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("User-Agent", "Lorekeeper");
        return request;
    }

    private IReadOnlyList<DiscoveredAccountModel> Parse(byte[] body, bool plan)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new OpenAiAccountModelDiscoveryException("The account model list response was not understood.", ex);
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("models", out var models)
                || models.ValueKind != JsonValueKind.Array)
            {
                logger.LogDebug(
                    "ChatGPT account model list read: {KeptCount} kept, {SkippedCount} skipped.",
                    0,
                    0);
                return [];
            }

            var kept = new List<DiscoveredAccountModel>();
            var skipped = 0;
            foreach (var model in models.EnumerateArray())
            {
                if (TryReadModel(model) is { } discovered)
                {
                    // The plan model list is already in OpenAI's recommended order.
                    kept.Add(plan ? discovered with { Priority = kept.Count } : discovered);
                }
                else
                    skipped++;
            }

            logger.LogDebug(
                "ChatGPT account model list read: {KeptCount} kept, {SkippedCount} skipped.",
                kept.Count,
                skipped);

            return kept
                .OrderBy(model => model.Priority)
                .ThenBy(model => model.Slug, StringComparer.Ordinal)
                .ToList();
        }
    }

    private static DiscoveredAccountModel? TryReadModel(JsonElement model)
    {
        if (model.ValueKind != JsonValueKind.Object)
            return null;

        var slug = ReadString(model, "slug");
        if (string.IsNullOrWhiteSpace(slug) || !IsListed(model))
            return null;

        var displayName = ReadString(model, "display_name");
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = slug;

        return new DiscoveredAccountModel(
            slug,
            displayName,
            ReadInt32(model, "priority") ?? int.MaxValue,
            ReadSupportedEfforts(model),
            ReadEffort(ReadString(model, "default_reasoning_level")),
            ReadCapabilities(model),
            ReadContextWindow(model),
            ReadInt32(model, "effective_context_window_percent") ?? DefaultEffectiveContextPercent);
    }

    private static bool IsListed(JsonElement model) =>
        ReadString(model, "visibility") is { } visibility
        && string.Equals(visibility, "list", StringComparison.OrdinalIgnoreCase);

    private static int? ReadContextWindow(JsonElement model) =>
        ReadInt32(model, "context_window") ?? ReadInt32(model, "max_context_window");

    private static IReadOnlyList<LlmReasoningEffort> ReadSupportedEfforts(JsonElement model)
    {
        if (!model.TryGetProperty("supported_reasoning_levels", out var levels)
            || levels.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var efforts = new List<LlmReasoningEffort>();
        foreach (var level in levels.EnumerateArray())
        {
            if (level.ValueKind != JsonValueKind.Object
                || !level.TryGetProperty("effort", out var effortElement)
                || MapEffort(effortElement) is not { } effort
                || efforts.Contains(effort))
            {
                continue;
            }

            efforts.Add(effort);
        }

        return efforts;
    }

    private static LlmModelCapabilities ReadCapabilities(JsonElement model)
    {
        if (!model.TryGetProperty("input_modalities", out var modalities)
            || modalities.ValueKind != JsonValueKind.Array)
        {
            return LlmModelCapabilities.TextInput | LlmModelCapabilities.ImageInput;
        }

        var capabilities = LlmModelCapabilities.None;
        foreach (var modality in modalities.EnumerateArray())
        {
            if (modality.ValueKind != JsonValueKind.String)
                continue;

            var value = modality.GetString();
            if (string.Equals(value, "text", StringComparison.OrdinalIgnoreCase))
                capabilities |= LlmModelCapabilities.TextInput;
            else if (string.Equals(value, "image", StringComparison.OrdinalIgnoreCase))
                capabilities |= LlmModelCapabilities.ImageInput;
        }

        return capabilities == LlmModelCapabilities.None ? LlmModelCapabilities.TextInput : capabilities;
    }

    private static LlmReasoningEffort? MapEffort(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? ReadEffort(element.GetString()) : null;

    private static LlmReasoningEffort? ReadEffort(string? value) => value?.ToLowerInvariant() switch
    {
        "none" => LlmReasoningEffort.None,
        "minimal" => LlmReasoningEffort.Minimal,
        "low" => LlmReasoningEffort.Low,
        "medium" => LlmReasoningEffort.Medium,
        "high" => LlmReasoningEffort.High,
        "xhigh" => LlmReasoningEffort.ExtraHigh,
        "max" => LlmReasoningEffort.Maximum,
        _ => null,
    };

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? ReadInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetInt32(out var value)
            ? value
            : null;

    private static string? ReadErrorCode(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                    ? ReadString(error, "code")
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static OpenAiAccountModelDiscoveryException CreateStatusException(HttpStatusCode statusCode) =>
        statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new OpenAiAccountModelDiscoveryException("The OpenAI account must be reconnected before its models can be listed."),
            HttpStatusCode.TooManyRequests =>
                new OpenAiAccountModelDiscoveryException("The account model list is rate limited. Try Refresh models again shortly."),
            _ =>
                new OpenAiAccountModelDiscoveryException($"The account model list request failed ({(int)statusCode})."),
        };

    private static async Task<byte[]> ReadBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var remaining = MaxResponseBytes - (int)buffer.Length;
            var read = await stream.ReadAsync(
                chunk.AsMemory(0, Math.Min(chunk.Length, remaining + 1)),
                cancellationToken);
            if (read == 0)
                break;
            if (read > remaining)
                throw new OpenAiAccountModelDiscoveryException("The account model list was too large to read.");

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static string ResolveClientVersion()
    {
        var informationalVersion = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var buildSeparator = informationalVersion.IndexOf('+');
            var version = (buildSeparator >= 0 ? informationalVersion[..buildSeparator] : informationalVersion).Trim();
            if (version.Length > 0)
                return Uri.EscapeDataString(version);
        }

        return "1.0.0";
    }
}
