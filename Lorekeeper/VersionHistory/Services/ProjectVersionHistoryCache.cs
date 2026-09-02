using System.Collections.Concurrent;
using Lorekeeper.VersionHistory.Git;
using Lorekeeper.VersionHistory.Snapshots;

namespace Lorekeeper.VersionHistory.Services;

/// <summary>
/// Shares immutable Git-backed review artifacts across short-lived service
/// scopes. Live SQLite snapshots are deliberately excluded because review
/// mutations must always validate current project state.
/// </summary>
public sealed class ProjectVersionHistoryCache
{
    private const int MaxLoadedGitCheckpointEntries = 8;
    private const int MaxHistoricalChapterReviewEntries = 32;

    private readonly ConcurrentDictionary<GitCheckpointCacheKey, LoadedGitCheckpoint> _loadedGitCheckpoints = new();
    private readonly ConcurrentDictionary<HistoricalChapterReviewCacheKey, HistoricalChapterReviewCacheEntry> _historicalChapterReviews = new();

    internal bool TryGetGitCheckpoint(
        Guid repositoryId,
        string commitSha,
        out LoadedGitCheckpoint checkpoint) =>
        _loadedGitCheckpoints.TryGetValue(new GitCheckpointCacheKey(repositoryId, commitSha), out checkpoint!);

    internal void SetGitCheckpoint(Guid repositoryId, string commitSha, LoadedGitCheckpoint checkpoint)
    {
        if (checkpoint.Payload.ImageData.Count != 0 || checkpoint.Payload.FontFaceData.Count != 0)
            throw new InvalidOperationException("Version-history cache entries must not retain binary asset data.");

        _loadedGitCheckpoints[new GitCheckpointCacheKey(repositoryId, commitSha)] = checkpoint;
        Trim(_loadedGitCheckpoints, MaxLoadedGitCheckpointEntries);
    }

    internal bool TryGetHistoricalChapterReview(
        Guid repositoryId,
        string headCommitSha,
        Guid chapterId,
        string contentTargetKey,
        int maxCommits,
        out ProjectVersionHistoricalChapterReview? review)
    {
        var found = _historicalChapterReviews.TryGetValue(
            new HistoricalChapterReviewCacheKey(
                repositoryId,
                headCommitSha,
                chapterId,
                contentTargetKey,
                maxCommits),
            out var entry);
        review = entry?.Review;
        return found;
    }

    internal void SetHistoricalChapterReview(
        Guid repositoryId,
        string headCommitSha,
        Guid chapterId,
        string contentTargetKey,
        int maxCommits,
        ProjectVersionHistoricalChapterReview? review)
    {
        _historicalChapterReviews[new HistoricalChapterReviewCacheKey(
            repositoryId,
            headCommitSha,
            chapterId,
            contentTargetKey,
            maxCommits)] = new HistoricalChapterReviewCacheEntry(review);
        Trim(_historicalChapterReviews, MaxHistoricalChapterReviewEntries);
    }

    private static void Trim<TKey, TValue>(ConcurrentDictionary<TKey, TValue> entries, int maximumEntries)
        where TKey : notnull
    {
        while (entries.Count > maximumEntries)
        {
            using var keys = entries.Keys.GetEnumerator();
            if (!keys.MoveNext() || !entries.TryRemove(keys.Current, out _))
                break;
        }
    }

    private readonly record struct GitCheckpointCacheKey(Guid RepositoryId, string CommitSha);

    private readonly record struct HistoricalChapterReviewCacheKey(
        Guid RepositoryId,
        string HeadCommitSha,
        Guid ChapterId,
        string ContentTargetKey,
        int MaxCommits);

    private sealed record HistoricalChapterReviewCacheEntry(ProjectVersionHistoricalChapterReview? Review);
}

internal sealed record LoadedGitCheckpoint(
    GitCommitMetadata Commit,
    VersionHistorySnapshotManifest Manifest,
    VersionHistorySnapshotPayload Payload);
