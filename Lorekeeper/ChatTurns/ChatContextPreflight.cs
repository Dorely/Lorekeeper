using Lorekeeper.Tokens;
using Microsoft.Extensions.AI;

namespace Lorekeeper.ChatTurns;

public sealed record ChatContextPreflightResult(
    bool IsWithinBudget,
    int InputTokens,
    int SafeInputLimitTokens,
    int ContextWindowTokens,
    bool IsExact,
    string? Warning)
{
    public string ErrorMessage =>
        $"The next chat round needs {(IsExact ? string.Empty : "about ")}{InputTokens:N0} input tokens, "
        + $"above the configured safe limit of {SafeInputLimitTokens:N0} for a {ContextWindowTokens:N0}-token context window. "
        + "Start a new chat or reduce the supplied context before continuing.";
}

/// <summary>Shared provider-round guard used immediately before every model request.</summary>
public sealed class ChatContextPreflight(
    ITokenCounter tokenCounter,
    ITokenBudgetPlanner budgetPlanner)
{
    public ChatContextPreflightResult Check(
        IReadOnlyList<ChatMessage> messages,
        TokenBudgetRequest? request = null)
    {
        var plan = budgetPlanner.Plan(request);
        var count = tokenCounter.Count(
            ChatModelHistory.FormatForTokenCount(messages),
            new TokenCountRequest(plan.ModelName, plan.EncodingName));
        var safeInputLimit = Math.Max(
            1,
            plan.ContextWindowTokens
            - plan.ToolSchemaReserveTokens
            - plan.JobMemoryReserveTokens
            - plan.ResponseReserveTokens
            - plan.SafetyMarginTokens);

        return new ChatContextPreflightResult(
            count.TokenCount <= safeInputLimit,
            count.TokenCount,
            safeInputLimit,
            plan.ContextWindowTokens,
            count.IsExact,
            count.Warning);
    }
}
