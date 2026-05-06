using Microsoft.Extensions.Options;

namespace Lorekeeper.Tokens;

public sealed class TokenBudgetPlanner(IOptions<TokenBudgetOptions> options) : ITokenBudgetPlanner
{
    public TokenBudgetPlan Plan(TokenBudgetRequest? request = null)
    {
        var resolvedRequest = request ?? new TokenBudgetRequest();
        var resolvedOptions = options.Value;
        var contextWindow = Math.Max(1, resolvedRequest.ContextWindowTokens ?? resolvedOptions.DefaultContextWindowTokens);

        var reservedTokens = Math.Max(0, resolvedOptions.SystemPromptReserveTokens)
            + Math.Max(0, resolvedOptions.InstructionReserveTokens)
            + Math.Max(0, resolvedOptions.JobMemoryReserveTokens)
            + Math.Max(0, resolvedOptions.ToolSchemaReserveTokens)
            + Math.Max(0, resolvedOptions.ResponseReserveTokens)
            + Math.Max(0, resolvedOptions.SafetyMarginTokens);

        var ratioTarget = (int)Math.Floor(contextWindow * Math.Clamp(resolvedOptions.SourceTextRatio, 0.05, 0.9));
        var availableAfterReserve = Math.Max(1, contextWindow - reservedTokens);
        var sourceTextTarget = resolvedRequest.SourceTextTargetTokens ?? Math.Min(ratioTarget, availableAfterReserve);
        sourceTextTarget = Math.Clamp(sourceTextTarget, 1, contextWindow);

        return new TokenBudgetPlan(
            ContextWindowTokens: contextWindow,
            SourceTextTargetTokens: sourceTextTarget,
            ReservedTokens: reservedTokens,
            SystemPromptReserveTokens: Math.Max(0, resolvedOptions.SystemPromptReserveTokens),
            InstructionReserveTokens: Math.Max(0, resolvedOptions.InstructionReserveTokens),
            JobMemoryReserveTokens: Math.Max(0, resolvedOptions.JobMemoryReserveTokens),
            ToolSchemaReserveTokens: Math.Max(0, resolvedOptions.ToolSchemaReserveTokens),
            ResponseReserveTokens: Math.Max(0, resolvedOptions.ResponseReserveTokens),
            SafetyMarginTokens: Math.Max(0, resolvedOptions.SafetyMarginTokens),
            ModelName: resolvedRequest.ModelName,
            EncodingName: resolvedRequest.EncodingName);
    }
}