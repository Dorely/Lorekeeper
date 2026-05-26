namespace Lorekeeper.Models;

public class LlmProvider
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? DisplayName { get; set; }
    public required string EndpointUrl { get; set; }
    public required string ModelId { get; set; }
    public AuthType AuthType { get; set; }
    public string? ApiKey { get; set; }
    public bool IsDefault { get; set; }
    public bool LastChatTestSucceeded { get; set; }
    public DateTime? LastChatTestedAt { get; set; }
    public string? LastChatTestError { get; set; }
    public string? LastChatTestEndpointUrl { get; set; }
    public string? LastChatTestModelId { get; set; }
    public AuthType? LastChatTestAuthType { get; set; }
    public int? LastChatTestCredentialSourceId { get; set; }
    public bool LastVisionTestSucceeded { get; set; }
    public DateTime? LastVisionTestedAt { get; set; }
    public string? LastVisionTestError { get; set; }
    public string? LastVisionTestEndpointUrl { get; set; }
    public string? LastVisionTestModelId { get; set; }
    public AuthType? LastVisionTestAuthType { get; set; }
    public int? LastVisionTestCredentialSourceId { get; set; }

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

    public ICollection<OAuthToken> OAuthTokens { get; set; } = [];

    /// <summary>
    /// Returns the provider ID whose credentials should be used (follows CredentialSourceId if set).
    /// </summary>
    public int EffectiveCredentialProviderId => CredentialSourceId ?? Id;

    public bool HasCurrentChatTestSnapshot =>
        LastChatTestSucceeded
        && string.Equals(LastChatTestEndpointUrl, EndpointUrl, StringComparison.Ordinal)
        && string.Equals(LastChatTestModelId, ModelId, StringComparison.Ordinal)
        && LastChatTestAuthType == AuthType
        && LastChatTestCredentialSourceId == CredentialSourceId;

    public bool HasCurrentVisionTestSnapshot =>
        LastVisionTestSucceeded
        && string.Equals(LastVisionTestEndpointUrl, EndpointUrl, StringComparison.Ordinal)
        && string.Equals(LastVisionTestModelId, ModelId, StringComparison.Ordinal)
        && LastVisionTestAuthType == AuthType
        && LastVisionTestCredentialSourceId == CredentialSourceId;

    public void MarkChatTestSucceeded(DateTime testedAt)
    {
        LastChatTestSucceeded = true;
        LastChatTestedAt = testedAt;
        LastChatTestError = null;
        LastChatTestEndpointUrl = EndpointUrl;
        LastChatTestModelId = ModelId;
        LastChatTestAuthType = AuthType;
        LastChatTestCredentialSourceId = CredentialSourceId;
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
    }

    public void ClearVisionReadiness(string? reason = null)
    {
        LastVisionTestSucceeded = false;
        LastVisionTestError = reason;
    }
}
