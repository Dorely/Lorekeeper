using System.Text.RegularExpressions;

namespace Lorekeeper.Diagnostics;

public static partial class LogRedaction
{
    public const string RedactedPlaceholder = "[redacted]";

    private static readonly string[] SensitiveKeys =
    [
        "authorization",
        "api-key",
        "x-api-key",
        "chatgpt-account-id",
        "openai-organization",
        "token",
        "access_token",
        "refresh_token",
        "api_key",
        "apikey",
        "session_key",
    ];

    public static IReadOnlyDictionary<string, string> RedactHeaders(IEnumerable<KeyValuePair<string, string>>? headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (headers is not null)
            {
                foreach (var header in headers)
                {
                    var name = header.Key.Trim();
                    result[name] = IsSensitive(name) ? RedactedPlaceholder : header.Value;
                }
            }
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["payload"] = FullyRedactedPlaceholder,
            };
        }
        return result;
    }

    public static string RedactJson(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return payload ?? string.Empty;
        try
        {
            return KeyValueJsonRegex().Replace(payload, static match =>
            {
                var key = match.Groups["key"].Value;
                return IsSensitive(key)
                    ? $"{match.Groups["prefix"].Value}{RedactedPlaceholder}{match.Groups["suffix"].Value}"
                    : match.Value;
            });
        }
        catch
        {
            return FullyRedactedPlaceholder;
        }
    }

    private static bool IsSensitive(string key) =>
        SensitiveKeys.Any(candidate => key.Trim().Equals(candidate, StringComparison.OrdinalIgnoreCase));

    private static string FullyRedactedPlaceholder => $"{{\"payload\":\"{RedactedPlaceholder}\"}}";

    [GeneratedRegex("(?<prefix>\"(?<key>[^\"\\\\]*)\"\\s*:\\s*)(?<suffix>(\"(?:[^\"\\\\]|\\\\.)*\"|\\[[^\\]]*\\]|\\{[^{}]*\\}|[^,\\}\\]\\r\\n]*))", RegexOptions.ExplicitCapture)]
    private static partial Regex KeyValueJsonRegex();
}
