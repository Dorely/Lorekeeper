namespace Lorekeeper.Tokens;

public sealed class TokenBudgetOptions
{
    public const string SectionName = "TokenBudget";

    public int DefaultContextWindowTokens { get; set; } = 128_000;
    public double SourceTextRatio { get; set; } = 0.5;
    public int SystemPromptReserveTokens { get; set; } = 4_000;
    public int InstructionReserveTokens { get; set; } = 4_000;
    public int JobMemoryReserveTokens { get; set; } = 12_000;
    public int ToolSchemaReserveTokens { get; set; } = 8_000;
    public int ResponseReserveTokens { get; set; } = 16_000;
    public int SafetyMarginTokens { get; set; } = 8_000;
}