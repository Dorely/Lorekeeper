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
}
