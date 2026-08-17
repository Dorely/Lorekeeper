using System.Text.Json;

namespace Lorekeeper.Llm;

/// <summary>
/// Normalizes provider API errors into concise, user-presentable messages by
/// extracting the OpenAI-style <c>error.message</c> (or equivalent) detail from
/// raw response bodies shared across chat, vision, and model-discovery calls.
/// </summary>
internal static class LlmErrorNormalizer
{
    public static string SummarizeHttpError(string operation, int statusCode, string? body)
    {
        var detail = ExtractErrorDetail(body);
        if (string.IsNullOrWhiteSpace(detail))
            detail = Truncate(body);

        return string.IsNullOrWhiteSpace(detail)
            ? $"{operation} failed with HTTP {statusCode}."
            : $"{operation} failed with HTTP {statusCode}: {detail}";
    }

    public static string? ExtractErrorDetail(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.String)
                return root.GetString();

            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                    return error.GetString();

                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    var code = error.TryGetProperty("code", out var errorProperty)
                        && errorProperty.ValueKind == JsonValueKind.String
                            ? errorProperty.GetString()
                            : null;
                    return string.IsNullOrWhiteSpace(code)
                        ? message.GetString()
                        : $"{message.GetString()} (code {code})";
                }
            }

            if (root.TryGetProperty("message", out var topLevelMessage)
                && topLevelMessage.ValueKind == JsonValueKind.String)
                return topLevelMessage.GetString();
        }
        catch (JsonException)
        {
            // Not JSON; the raw truncated body is used instead.
        }

        return null;
    }

    public static string Truncate(string? value, int max = 400)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "...";
    }
}
