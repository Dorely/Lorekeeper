using System.ComponentModel.DataAnnotations.Schema;

namespace Lorekeeper.Models;

public class LlmProvider
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? DisplayName { get; set; }
    public required string EndpointUrl { get; set; }
    public required string ModelId { get; set; }
    public LlmReasoningEffort? ReasoningEffort { get; set; }

    /// <summary>
    /// Explicit output-token budget for chat requests, independent of the
    /// wire-compat default. When null, the endpoint-based wire-compat
    /// classification supplies the budget for non-OpenAI providers.
    /// </summary>
    public int? MaxOutputTokens { get; set; }

    /// <summary>
    /// Explicit input-context budget used for chat compaction decisions. When
    /// null, resolution falls back to the <c>ChatTokens</c> configuration
    /// mapping and then its default. Discovery for user-configured providers
    /// may prefill this from advertised context metadata; a manual value wins.
    /// </summary>
    public int? MaxInputTokens { get; set; }

    /// <summary>
    /// Overrides the max-tokens request field name chosen by the endpoint-based
    /// wire-compat classification. Use <see cref="LlmMaxTokensField.Legacy"/> for
    /// providers that reject <c>max_completion_tokens</c>.
    /// </summary>
    public LlmMaxTokensField MaxTokensField { get; set; } = LlmMaxTokensField.Default;

    public AuthType AuthType { get; set; }
    public string? ApiKey { get; set; }
    public int? OpenAiAccountId { get; set; }
    public OpenAiAccount? OpenAiAccount { get; set; }
    public LlmModelOrigin ModelOrigin { get; set; } = LlmModelOrigin.Manual;
    public bool IsDefault { get; set; }
    public bool LastChatTestSucceeded { get; set; }
    public DateTime? LastChatTestedAt { get; set; }
    public string? LastChatTestError { get; set; }
    public string? LastChatTestEndpointUrl { get; set; }
    public string? LastChatTestModelId { get; set; }
    public AuthType? LastChatTestAuthType { get; set; }
    public int? LastChatTestCredentialSourceId { get; set; }
    public LlmReasoningEffort? LastChatTestReasoningEffort { get; set; }
    public bool LastVisionTestSucceeded { get; set; }
    public DateTime? LastVisionTestedAt { get; set; }
    public string? LastVisionTestError { get; set; }
    public string? LastVisionTestEndpointUrl { get; set; }
    public string? LastVisionTestModelId { get; set; }
    public AuthType? LastVisionTestAuthType { get; set; }
    public int? LastVisionTestCredentialSourceId { get; set; }
    public LlmReasoningEffort? LastVisionTestReasoningEffort { get; set; }

    /// <summary>
    /// When set, credentials (API key / OAuth tokens) are resolved from the referenced
    /// parent provider instead of this entry. Allows multiple model configs to share
    /// a single set of credentials.
    /// </summary>
    public int? CredentialSourceId { get; set; }
    public LlmProvider? CredentialSource { get; set; }
    public ICollection<LlmProvider> ChildModels { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [NotMapped]
    public LlmProviderResolvedMetadata? ResolvedMetadata { get; set; }

    [NotMapped]
    public LlmReasoningEffort? EffectiveReasoningEffort =>
        ResolvedMetadata?.ReasoningEffort ?? ReasoningEffort;

    [NotMapped]
    public int? EffectiveMaxInputTokens =>
        ResolvedMetadata?.UsableInputBudgetTokens ?? MaxInputTokens;

    /// <summary>
    /// Returns the owning provider row for manual connection credentials. OpenAI
    /// account rows resolve credentials through <see cref="OpenAiAccountId"/>.
    /// </summary>
    public int EffectiveCredentialProviderId => CredentialSourceId ?? Id;

    public bool HasCurrentChatTestSnapshot =>
        LastChatTestSucceeded
        && string.Equals(LastChatTestEndpointUrl, EndpointUrl, StringComparison.Ordinal)
        && string.Equals(LastChatTestModelId, ModelId, StringComparison.Ordinal)
        && LastChatTestAuthType == AuthType
        && LastChatTestCredentialSourceId == CredentialSourceId
        && LastChatTestReasoningEffort == ReasoningEffort;

    public bool HasCurrentVisionTestSnapshot =>
        LastVisionTestSucceeded
        && string.Equals(LastVisionTestEndpointUrl, EndpointUrl, StringComparison.Ordinal)
        && string.Equals(LastVisionTestModelId, ModelId, StringComparison.Ordinal)
        && LastVisionTestAuthType == AuthType
        && LastVisionTestCredentialSourceId == CredentialSourceId
        && LastVisionTestReasoningEffort == ReasoningEffort;

    public void MarkChatTestSucceeded(DateTime testedAt)
    {
        LastChatTestSucceeded = true;
        LastChatTestedAt = testedAt;
        LastChatTestError = null;
        LastChatTestEndpointUrl = EndpointUrl;
        LastChatTestModelId = ModelId;
        LastChatTestAuthType = AuthType;
        LastChatTestCredentialSourceId = CredentialSourceId;
        LastChatTestReasoningEffort = ReasoningEffort;
    }

    public void MarkChatTestFailed(string error, DateTime testedAt)
    {
        LastChatTestSucceeded = false;
        LastChatTestedAt = testedAt;
        LastChatTestError = error;
        LastChatTestEndpointUrl = EndpointUrl;
        LastChatTestModelId = ModelId;
        LastChatTestAuthType = AuthType;
        LastChatTestCredentialSourceId = CredentialSourceId;
        LastChatTestReasoningEffort = ReasoningEffort;
    }

    public void ClearChatReadiness(string? reason = null)
    {
        LastChatTestSucceeded = false;
        LastChatTestError = reason;
    }

    public void MarkVisionTestSucceeded(DateTime testedAt)
    {
        LastVisionTestSucceeded = true;
        LastVisionTestedAt = testedAt;
        LastVisionTestError = null;
        LastVisionTestEndpointUrl = EndpointUrl;
        LastVisionTestModelId = ModelId;
        LastVisionTestAuthType = AuthType;
        LastVisionTestCredentialSourceId = CredentialSourceId;
        LastVisionTestReasoningEffort = ReasoningEffort;
    }

    public void MarkVisionTestFailed(string error, DateTime testedAt)
    {
        LastVisionTestSucceeded = false;
        LastVisionTestedAt = testedAt;
        LastVisionTestError = error;
        LastVisionTestEndpointUrl = EndpointUrl;
        LastVisionTestModelId = ModelId;
        LastVisionTestAuthType = AuthType;
        LastVisionTestCredentialSourceId = CredentialSourceId;
        LastVisionTestReasoningEffort = ReasoningEffort;
    }

    public void ClearVisionReadiness(string? reason = null)
    {
        LastVisionTestSucceeded = false;
        LastVisionTestError = reason;
    }
}
