namespace Lorekeeper.Models;

/// <summary>
/// App-level GitHub account and OAuth credential. This entity is intentionally
/// not part of project snapshots or version manifests.
/// </summary>
public sealed class GitHubConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public long GitHubUserId { get; set; }
    public required string AccountLogin { get; set; }
    public required string AccessToken { get; set; }
    public DateTime? AccessTokenExpiresAt { get; set; }
    public string? Scope { get; set; }
    public DateTime? LastValidatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<ProjectGitRemote> GitRemotes { get; set; } = [];
}
