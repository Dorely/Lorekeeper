using System.Net.Http.Headers;
using Lorekeeper.Models;

namespace Lorekeeper.Llm;

public static class CodexProvider
{
    public const string AccountProviderKey = "openai-account";
    public const string ResponsesEndpoint = "https://chatgpt.com/backend-api/codex/responses";
    public const string ModelsEndpoint = "https://chatgpt.com/backend-api/codex/models";
    public const string ImageGenerationsEndpoint = "https://chatgpt.com/backend-api/codex/images/generations";
    public const string ImageEditsEndpoint = "https://chatgpt.com/backend-api/codex/images/edits";
    public const string PlanResponsesEndpoint = "https://api.openai.com/v1/responses";
    public const string PlanModelsEndpoint = "https://api.openai.com/v1/models";
    public const string ManageUsageUrl = "https://chatgpt.com/settings/usage";
    public const string ModelNotOnPlanMessage =
        "This model isn't available on your ChatGPT plan. Choose another model or use Refresh models in Settings > Providers.";

    public const string PlanNotEligibleMessage =
        "Your ChatGPT plan isn't eligible for plan usage in other apps (Plus or Pro is required). Use the ChatGPT (Codex) connection instead.";
    public const string PlanUsageNotGrantedMessage =
        "ChatGPT plan usage wasn't granted. Reconnect in Settings > Providers and allow ChatGPT plan usage.";
    public const string PlanUsageLimitMessage =
        "Usage limit reached. Review your plan or this app's limit in ChatGPT settings.";

    public static bool IsAccountBacked(LlmProvider provider) => provider.OpenAiAccountId is not null;

    /// <summary>True when a chat turn failed because the ChatGPT plan usage limit was reached.</summary>
    public static bool IsPlanUsageLimitMessage(string? message) =>
        message?.Contains(PlanUsageLimitMessage, StringComparison.Ordinal) == true;

    /// <summary>The Responses endpoint an account kind's models are served from.</summary>
    public static string ResponsesEndpointFor(OpenAiAccountKind kind) =>
        kind == OpenAiAccountKind.ChatGptPlan ? PlanResponsesEndpoint : ResponsesEndpoint;

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
    /// and model discovery requests.
    /// </summary>
    public static void ApplyAccountHeaders(
        HttpRequestMessage request,
        string accessToken,
        string externalAccountId,
        bool eventStream)
    {
        ApplyAccountIdentity(request, accessToken, externalAccountId);
        request.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=experimental");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(eventStream ? "text/event-stream" : "application/json"));
    }

    /// <summary>
    /// The account identity every Codex backend request carries. The image
    /// endpoints take only this plus a JSON accept header; Responses and model
    /// requests add the beta header through <see cref="ApplyAccountHeaders"/>.
    /// </summary>
    public static void ApplyAccountIdentity(
        HttpRequestMessage request,
        string accessToken,
        string externalAccountId)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("chatgpt-account-id", externalAccountId);
        request.Headers.TryAddWithoutValidation("originator", "pi");
        request.Headers.TryAddWithoutValidation("User-Agent", "Lorekeeper");
    }
}
