namespace Lorekeeper.Models;

public sealed record LlmProviderResolvedMetadata(
    int? OpenAiAccountId,
    LlmModelOrigin Origin,
    AccountModelAvailability Availability,
    IReadOnlyList<LlmReasoningEffort> SupportedEfforts,
    LlmModelCapabilities Capabilities,
    int? AdvertisedContextWindowTokens,
    int? UsableInputBudgetTokens,
    LlmReasoningEffort? ReasoningEffort,
    int? CatalogSchemaVersion,
    string? CatalogSource,
    DateOnly? CatalogValidationDate);
