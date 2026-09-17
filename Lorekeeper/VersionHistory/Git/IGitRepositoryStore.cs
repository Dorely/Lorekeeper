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
