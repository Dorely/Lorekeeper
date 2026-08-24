using Microsoft.Extensions.Options;

namespace Lorekeeper.Tokens;

internal sealed class ChatTokenLimitOptions
{
    public const string SectionName = "ChatTokens";

    public int DefaultMaxInputTokens { get; set; } = 200_000;
    public Dictionary<string, int> ModelMaxInputTokens { get; set; } = [];
}

internal sealed class ChatTokenLimitResolver(IOptions<ChatTokenLimitOptions> options)
{
    /// <summary>
    /// Resolves the advisory maximum input-token budget: an explicit
    /// provider-model value wins, then the configured per-model mapping, then
    /// the configured default.
    /// </summary>
    public int Resolve(int? providerMaxInputTokens, string? modelId)
    {
        if (providerMaxInputTokens is > 0)
            return providerMaxInputTokens.Value;

        var configured = options.Value;
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            foreach (var entry in configured.ModelMaxInputTokens)
            {
                if (string.Equals(entry.Key, modelId, StringComparison.OrdinalIgnoreCase))
                    return entry.Value;
            }
        }

        return configured.DefaultMaxInputTokens;
    }
}
