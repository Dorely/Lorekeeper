using Lorekeeper.Models;

namespace Lorekeeper.Llm;

/// <summary>Official transports are selected by connection endpoint, never by a gateway's model name.</summary>
public static class ProviderChatProtocol
{
    public const string ChatCompletions = "chat-completions";
    public const string Responses = "responses";
    public const string Anthropic = "anthropic-messages";

    public static string For(LlmProvider provider)
    {
        if (provider.OpenAiAccountId is not null) return Responses;
        if (!Uri.TryCreate(provider.EndpointUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https") return ChatCompletions;
        if (uri.Host.Equals("api.anthropic.com", StringComparison.OrdinalIgnoreCase)) return Anthropic;
        if (uri.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase) && SupportsResponses(provider.ModelId)) return Responses;
        return ChatCompletions;
    }

    public static bool SupportsExplicitCache(string model) =>
        model.StartsWith("gpt-6", StringComparison.Ordinal) || model.StartsWith("gpt-5.6", StringComparison.Ordinal)
        || model.StartsWith("gpt-5.7", StringComparison.Ordinal) || model.StartsWith("gpt-5.8", StringComparison.Ordinal)
        || model.StartsWith("gpt-5.9", StringComparison.Ordinal);

    private static bool SupportsResponses(string model) =>
        !model.Contains("audio", StringComparison.OrdinalIgnoreCase) && !model.Contains("realtime", StringComparison.OrdinalIgnoreCase)
        && (model.StartsWith("gpt-4o", StringComparison.Ordinal) || model.StartsWith("gpt-4.1", StringComparison.Ordinal)
            || model.StartsWith("gpt-5", StringComparison.Ordinal) || model.StartsWith("gpt-6", StringComparison.Ordinal)
            || model.StartsWith("o1", StringComparison.Ordinal) || model.StartsWith("o3", StringComparison.Ordinal)
            || model.StartsWith("o4", StringComparison.Ordinal));
}
