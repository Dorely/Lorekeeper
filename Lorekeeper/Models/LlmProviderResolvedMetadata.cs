namespace Lorekeeper.Models;

public sealed record LlmProviderResolvedMetadata(
    int? OpenAiAccountId,
    LlmModelOrigin Origin,
    IReadOnlyList<LlmReasoningEffort> SupportedEfforts,
    LlmModelCapabilities Capabilities,
    int? UsableInputBudgetTokens,
    LlmReasoningEffort? ReasoningEffort,
    int? CatalogSchemaVersion,
    string? CatalogSource,
    DateOnly? CatalogValidationDate);
