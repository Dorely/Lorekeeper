using Lorekeeper.ProjectArchive;

namespace Lorekeeper.VersionHistory.Git;

/// <summary>
/// Local repository operations used by version-history orchestration and
/// transport adapters.
/// </summary>
public interface IGitRepositoryStore
{
    string HistoryRoot { get; }

    string GetRepositoryPath(Guid repositoryId);

    GitRepositoryDeletionStage StageRepositoryDeletion(Guid repositoryId);

    void RollbackRepositoryDeletion(GitRepositoryDeletionStage stage);

    void FinalizeRepositoryDeletion(GitRepositoryDeletionStage stage);

    IReadOnlyList<GitRepositoryDeletionTombstone> ListDeletionTombstones();

    GitRepositoryDeletionStage ReadDeletionTombstone(
        GitRepositoryDeletionTombstone tombstone);

    void InitializeRepository(Guid repositoryId);

    GitHeadInfo GetHead(Guid repositoryId);

    GitCommitWriteResult WriteSnapshot(
        Guid repositoryId,
        IReadOnlyCollection<ProjectArchiveFileDescriptor> files,
        string semanticMessage,
        DateTimeOffset authoredAt);

    void MaterializeTree(
        Guid repositoryId,
        string destinationDirectory,
        string? commitSha = null,
        CancellationToken cancellationToken = default);

    GitCommitMetadata GetCommitMetadata(Guid repositoryId, string commitSha);

    IReadOnlyList<GitCommitMetadata> ListCommits(Guid repositoryId, int maxCount = 100);

    /// <summary>
    /// Visits the ancestors of a commit, children before parents and excluding
    /// the commit itself, with the bytes of each requested root-relative path
    /// present in that ancestor. The walk stops when <paramref name="visit"/>
    /// returns false.
    /// </summary>
    void VisitAncestorFiles(
        Guid repositoryId,
        string commitSha,
        IReadOnlyCollection<string> paths,
        Func<IReadOnlyDictionary<string, byte[]>, bool> visit,
        CancellationToken cancellationToken = default);

    GitCommitMetadata? FindMergeBase(Guid repositoryId, string currentCommitSha, string candidateCommitSha);

    GitHistoryComparison CompareHistory(
        Guid repositoryId,
        string? currentCommitSha,
        string? candidateCommitSha);

    GitHeadInfo FastForwardMain(
        Guid repositoryId,
        string? expectedCurrentCommitSha,
        string targetCommitSha);

    GitHeadInfo RollbackMain(
        Guid repositoryId,
        string expectedCurrentCommitSha,
        string previousCommitSha);
}
