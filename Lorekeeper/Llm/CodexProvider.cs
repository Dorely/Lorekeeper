using System.Net.Http.Headers;
using Lorekeeper.Models;

namespace Lorekeeper.Llm;

public static class CodexProvider
{
    public const string AccountProviderKey = "openai-account";
    public const string ResponsesEndpoint = "https://chatgpt.com/backend-api/codex/responses";
    public const string ModelsEndpoint = "https://chatgpt.com/backend-api/codex/models";
    public const string ModelNotOnPlanMessage =
        "This model isn't available on your ChatGPT plan. Choose another model or use Refresh models in Settings > Providers.";

    public static bool IsAccountBacked(LlmProvider provider) => provider.OpenAiAccountId is not null;

    /// <summary>True when a Codex backend error says the requested model is not offered to this ChatGPT account.</summary>
    public static bool IsModelNotOnPlanError(string? redactedErrorBody)
    {
        if (string.IsNullOrWhiteSpace(redactedErrorBody))
            return false;

        return redactedErrorBody.Contains(
                "not supported when using codex with a chatgpt account",
                StringComparison.OrdinalIgnoreCase)
            || (redactedErrorBody.Contains("model", StringComparison.OrdinalIgnoreCase)
                && redactedErrorBody.Contains("not supported", StringComparison.OrdinalIgnoreCase)
                && redactedErrorBody.Contains("chatgpt account", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Applies the ChatGPT account backend headers shared by Codex chat, vision,
    /// image generation, and model discovery requests.
    /// </summary>
    public static void ApplyAccountHeaders(
        HttpRequestMessage request,
        string accessToken,
        string externalAccountId,
        bool eventStream)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("chatgpt-account-id", externalAccountId);
        request.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=experimental");
        request.Headers.TryAddWithoutValidation("originator", "pi");
        request.Headers.TryAddWithoutValidation("User-Agent", "Lorekeeper");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(eventStream ? "text/event-stream" : "application/json"));
    }
}
