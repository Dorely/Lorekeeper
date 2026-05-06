namespace Lorekeeper.Tokens;

public sealed class TokenCountingOptions
{
    public const string SectionName = "TokenCounting";

    public string DefaultEncoding { get; set; } = "o200k_base";
    public double EstimatedCharsPerToken { get; set; } = 3.0;

    public Dictionary<string, string> ModelEncodings { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-5"] = "o200k_base",
        ["gpt-4.1"] = "o200k_base",
        ["gpt-4o"] = "o200k_base",
        ["o1"] = "o200k_base",
        ["o3"] = "o200k_base",
        ["o4"] = "o200k_base",
        ["gpt-4"] = "cl100k_base",
        ["gpt-3.5"] = "cl100k_base",
        ["text-embedding-3"] = "cl100k_base",
    };
}