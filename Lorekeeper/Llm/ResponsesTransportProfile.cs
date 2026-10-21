using System.Net.Http.Headers;
using System.Text.Json;
using Lorekeeper.Models;

namespace Lorekeeper.Llm;

/// <summary>
/// The endpoint, headers, and failure mapping for one OpenAI account kind's
/// Responses transport. Codex uses the ChatGPT backend; ChatGPT plan usage uses
/// the public Responses API with the bearer token alone.
/// </summary>
public sealed class ResponsesTransportProfile
{
    private const string PlanUsageUnavailableMessage =
        "ChatGPT plan usage is temporarily unavailable. Try again shortly.";

    private ResponsesTransportProfile(OpenAiAccountKind kind, string endpoint, string label)
    {
        Kind = kind;
        Endpoint = endpoint;
        Label = label;
    }

    public static readonly ResponsesTransportProfile Codex =
        new(OpenAiAccountKind.Codex, CodexProvider.ResponsesEndpoint, "Codex");

    public static readonly ResponsesTransportProfile ChatGptPlan =
        new(OpenAiAccountKind.ChatGptPlan, CodexProvider.PlanResponsesEndpoint, "ChatGPT plan");

    public static ResponsesTransportProfile For(OpenAiAccountKind kind) => kind switch
    {
        OpenAiAccountKind.ChatGptPlan => ChatGptPlan,
        _ => Codex,
    };

    public OpenAiAccountKind Kind { get; }

    public string Endpoint { get; }

    /// <summary>Used in log lines and error prefixes instead of the literal "Codex".</summary>
    public string Label { get; }

    public void ApplyHeaders(HttpRequestMessage request, string accessToken, string externalAccountId)
    {
        if (Kind != OpenAiAccountKind.ChatGptPlan)
        {
            CodexProvider.ApplyAccountHeaders(request, accessToken, externalAccountId, eventStream: true);
            return;
        }

        // ChatGPT plan usage authorizes with the bearer token alone. The Codex
        // account header, beta header, and originator are rejected on this route.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.TryAddWithoutValidation("User-Agent", "Lorekeeper");
    }

    /// <summary>
    /// Returns the exception to throw for a non-success HTTP response, given the
    /// status and the already-redacted body.
    /// </summary>
    public Exception MapHttpError(int statusCode, string redactedBody, string fallbackMessage)
    {
        if (Kind != OpenAiAccountKind.ChatGptPlan)
        {
            return CodexProvider.IsModelNotOnPlanError(redactedBody)
                ? new HttpRequestException(CodexProvider.ModelNotOnPlanMessage)
                : new HttpRequestException(fallbackMessage);
        }

        var (code, param) = ReadErrorIdentifiers(redactedBody);
        return MapPlanCode(code, param) ?? statusCode switch
        {
            401 => new HttpRequestException(
                "ChatGPT plan usage was not accepted for this sign-in. Reconnect in Settings > Providers."),
            403 => new HttpRequestException("ChatGPT plan usage was blocked for this request."),
            503 => new HttpRequestException(PlanUsageUnavailableMessage),
            _ => new HttpRequestException(fallbackMessage),
        };
    }

    /// <summary>
    /// Returns the exception to throw for a streamed <c>response.failed</c> or
    /// <c>error</c> event whose code, param, and message were already extracted.
    /// </summary>
    public Exception MapStreamError(string? code, string? param, string message)
    {
        if (Kind != OpenAiAccountKind.ChatGptPlan)
            return new InvalidOperationException(message);

        return (Exception?)MapPlanCode(code, param) ?? new InvalidOperationException(message);
    }

    private static ChatGptPlanException? MapPlanCode(string? code, string? param)
    {
        if (code is null)
            return null;

        return code switch
        {
            "subscription_sharing_user_not_eligible" =>
                new ChatGptPlanException(code, CodexProvider.PlanNotEligibleMessage),
            "subscription_sharing_usage_limit_exceeded" =>
                new ChatGptPlanException(code, CodexProvider.PlanUsageLimitMessage),
            "subscription_sharing_usage_unavailable" or "subscription_sharing_user_unavailable" =>
                new ChatGptPlanException(code, PlanUsageUnavailableMessage),
            "subscription_sharing_unsupported_capability" => new ChatGptPlanException(
                code,
                param is null
                    ? "ChatGPT plan usage doesn't support part of this request."
                    : $"ChatGPT plan usage doesn't support '{param}' in this request."),
            "subscription_sharing_route_not_supported" =>
                new ChatGptPlanException(code, "This request isn't supported for ChatGPT plan usage."),
            "subscription_sharing_invalid_user" => new ChatGptPlanException(
                code,
                "ChatGPT couldn't validate this sign-in. Try again; reconnect in Settings > Providers if it keeps failing."),
            "chatpass_v2_scope_not_authorized" or "chatpass_v2_invalid_authorization_context" =>
                new ChatGptPlanException(code, CodexProvider.PlanUsageNotGrantedMessage),
            _ => null,
        };
    }

    /// <summary>
    /// Reads the structured error code and parameter from a streamed
    /// <c>response.failed</c> or <c>error</c> event.
    /// </summary>
    public static (string? Code, string? Param) ReadStreamErrorIdentifiers(JsonElement evt) =>
        (ReadStreamErrorField(evt, "code"), ReadStreamErrorField(evt, "param"));

    private static string? ReadStreamErrorField(JsonElement evt, string name)
    {
        if (evt.ValueKind != JsonValueKind.Object)
            return null;

        if (evt.TryGetProperty("response", out var response)
            && response.ValueKind == JsonValueKind.Object
            && response.TryGetProperty("error", out var responseError)
            && responseError.ValueKind == JsonValueKind.Object
            && ReadString(responseError, name) is { } fromResponse)
        {
            return fromResponse;
        }

        if (evt.TryGetProperty("error", out var error)
            && error.ValueKind == JsonValueKind.Object
            && ReadString(error, name) is { } fromError)
        {
            return fromError;
        }

        // A top-level `error` event carries its code and param directly.
        return ReadString(evt, name);
    }

    private static (string? Code, string? Param) ReadErrorIdentifiers(string? redactedBody)
    {
        if (string.IsNullOrWhiteSpace(redactedBody))
            return (null, null);

        try
        {
            using var document = JsonDocument.Parse(redactedBody);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            return (ReadString(error, "code"), ReadString(error, "param"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
}

/// <summary>A ChatGPT plan usage rejection identified by its structured error code.</summary>
public sealed class ChatGptPlanException(string code, string message) : HttpRequestException(message)
{
    public string Code { get; } = code;

    public bool IsUsageLimit => Code == "subscription_sharing_usage_limit_exceeded";

    public bool IsNotEligible => Code == "subscription_sharing_user_not_eligible";
}
