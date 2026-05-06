using Lorekeeper.Tokens;

namespace Lorekeeper.Ingest;

public sealed record IngestSourceStructureRequest(
    string SourceText,
    string? ModelName = null,
    string? EncodingName = null,
    int? SourceTextTargetTokens = null)
{
    public TokenCountRequest TokenCountRequest => new(ModelName, EncodingName);
    public TokenBudgetRequest BudgetRequest => new(ModelName, EncodingName, SourceTextTargetTokens: SourceTextTargetTokens);
}