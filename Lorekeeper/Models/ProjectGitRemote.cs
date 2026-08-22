namespace Lorekeeper.Models;

/// <summary>
/// Non-secret GitHub remote metadata associated with a project's local
/// version-control identity.
/// </summary>
public sealed class ProjectGitRemote
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectVersionRepositoryId { get; set; }
    public ProjectVersionRepository Repository { get; set; } = null!;

    public Guid? GitHubConnectionId { get; set; }
    public GitHubConnection? GitHubConnection { get; set; }

    public required string RemoteName { get; set; }
    public required string Owner { get; set; }
    public required string RepositoryName { get; set; }
    public long? GitHubRepositoryId { get; set; }
    public string? CloneUrl { get; set; }
    public string? WebUrl { get; set; }
    public string? DefaultBranch { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
