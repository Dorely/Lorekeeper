using System.Text.Json;
using Lorekeeper.Models;

namespace Lorekeeper.Llm;

public static class CodexProvider
{
    public const string LegacyProviderName = "openai-codex";
    public const string AccountProviderKey = "openai-account";
    public const string ResponsesEndpoint = "https://chatgpt.com/backend-api/codex/responses";
    public const string PlatformEmbeddingsEndpoint = "https://api.openai.com/v1/embeddings";
    public const string PlatformModelsEndpoint = "https://api.openai.com/v1/models";
    public const string DefaultEmbeddingModel = "text-embedding-3-small";

    public static bool IsAccountBacked(LlmProvider provider) => provider.OpenAiAccountId is not null;

    public static string ExtractAccountId(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
            throw new InvalidOperationException("OAuth token is not a valid JWT.");

        var payload = parts[1];
        payload += new string('=', (4 - payload.Length % 4) % 4);
        var decoded = Convert.FromBase64String(payload.Replace('-', '+').Replace('_', '/'));
        var json = JsonSerializer.Deserialize<JsonElement>(decoded);

        if (json.TryGetProperty("https://api.openai.com/auth", out var authClaim) &&
            authClaim.TryGetProperty("chatgpt_account_id", out var accountId))
        {
            return accountId.GetString() ?? throw new InvalidOperationException("chatgpt_account_id is null in token.");
        }

        throw new InvalidOperationException("OAuth token does not contain chatgpt_account_id. Make sure you signed in with your OpenAI account.");
    }
}
