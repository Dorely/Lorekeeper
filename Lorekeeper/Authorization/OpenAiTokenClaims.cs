using System.Text.Json;

namespace Lorekeeper.Authorization;

internal static class OpenAiTokenClaims
{
    public static string ReadExternalAccountId(string accessToken)
    {
        var parts = accessToken.Split('.');
        if (parts.Length != 3)
            throw new InvalidOperationException("OpenAI did not return a usable account identity.");

        try
        {
            var payload = parts[1];
            payload += new string('=', (4 - payload.Length % 4) % 4);
            var decoded = Convert.FromBase64String(payload.Replace('-', '+').Replace('_', '/'));
            using var document = JsonDocument.Parse(decoded);
            if (document.RootElement.TryGetProperty("https://api.openai.com/auth", out var authClaim)
                && authClaim.TryGetProperty("chatgpt_account_id", out var accountId)
                && accountId.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(accountId.GetString()))
            {
                return accountId.GetString()!;
            }
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
        }

        throw new InvalidOperationException("OpenAI did not return a usable account identity.");
    }
}
