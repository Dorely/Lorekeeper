namespace Lorekeeper.Models;

public class OpenAiAccount
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = "OpenAI account";
    public string? ExternalAccountId { get; set; }
    public DateTime? RequiresReauthenticationAt { get; set; }
    public string? LastAuthenticationError { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<LlmProvider> Models { get; set; } = [];
    public ICollection<OAuthToken> OAuthTokens { get; set; } = [];
}
