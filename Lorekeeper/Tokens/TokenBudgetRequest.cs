namespace Lorekeeper.Tokens;

public sealed record TokenBudgetRequest(
    string? ModelName = null,
    string? EncodingName = null,
    int? ContextWindowTokens = null,
    int? SourceTextTargetTokens = null);