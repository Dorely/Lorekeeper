namespace Lorekeeper.VersionHistory.Git;

public sealed record GitCommitMetadata(
    string Sha,
    string TreeSha,
    string Message,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthoredAt,
    string CommitterName,
    string CommitterEmail,
    DateTimeOffset CommittedAt,
    IReadOnlyList<string> ParentShas);

public sealed record GitHeadInfo(string RefName, GitCommitMetadata? Commit)
{
    public string? CommitSha => Commit?.Sha;
}

public sealed record GitCommitWriteResult(bool Created, GitCommitMetadata Commit)
{
    public string CommitSha => Commit.Sha;
}

public enum GitHistoryRelation
{
    Empty,
    Identical,
    CandidateFastForward,
    CurrentFastForward,
    Diverged,
    Unrelated,
}

public sealed record GitHistoryComparison(
    string? CurrentCommitSha,
    string? CandidateCommitSha,
    string? MergeBaseSha,
    GitHistoryRelation Relation);

/// <summary>
/// An app-owned local repository move held across the project database delete.
/// The staged path remains under the configured history root so a failed final
/// removal is recoverable without touching any remote repository.
/// </summary>
public sealed record GitRepositoryDeletionStage(
    Guid RepositoryId,
    string OriginalPath,
    string StagedPath,
    bool WasPresent);

public sealed record GitRepositoryDeletionTombstone(
    Guid? RepositoryId,
    string Path,
    bool IsValid,
    string? Diagnostic);

public sealed class GitRepositoryDeletionException : IOException
{
    public GitRepositoryDeletionException(
        string message,
        GitRepositoryDeletionStage stage,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Stage = stage;
    }

    public GitRepositoryDeletionStage Stage { get; }
}
