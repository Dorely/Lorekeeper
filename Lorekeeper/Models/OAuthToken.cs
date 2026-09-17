namespace Lorekeeper.Models;

public class OAuthToken
{
    public int Id { get; set; }
    public int OpenAiAccountId { get; set; }
    public required string AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string? Scope { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public OpenAiAccount OpenAiAccount { get; set; } = null!;
}
