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
    public int Resolve(string? modelId)
    {
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
