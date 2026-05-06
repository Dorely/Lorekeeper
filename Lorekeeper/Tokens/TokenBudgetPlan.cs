namespace Lorekeeper.Tokens;

public sealed record TokenBudgetPlan(
    int ContextWindowTokens,
    int SourceTextTargetTokens,
    int ReservedTokens,
    int SystemPromptReserveTokens,
    int InstructionReserveTokens,
    int JobMemoryReserveTokens,
    int ToolSchemaReserveTokens,
    int ResponseReserveTokens,
    int SafetyMarginTokens,
    string? ModelName,
    string? EncodingName);