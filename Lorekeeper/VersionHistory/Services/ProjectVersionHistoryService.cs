using Lorekeeper.ImportExport;
using Lorekeeper.Authoring;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.ProjectArchive;
using Lorekeeper.VersionHistory.Compare;
using Lorekeeper.VersionHistory.Git;
using Lorekeeper.VersionHistory.Snapshots;
using Lorekeeper.VersionHistory.Sync;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.VersionHistory.Services;

/// <summary>
/// Coordinates snapshot capture, local Git commits, and the durable database
/// timeline. The project mutation lease spans the whole operation while each
/// database context remains short-lived.
/// </summary>
public sealed class ProjectVersionHistoryService(
    IProjectMutationCoordinator projectMutations,
    IAppDatabaseOperationFactory database,
    IVersionHistorySnapshotWriter snapshotWriter,
    IVersionHistorySnapshotReader snapshotReader,
    IVersionHistorySnapshotComparer snapshotComparer,
    IGitRepositoryStore git,
    ProjectVersionHistoryCache reviewCache,
    ProjectVersionHistoryUiEvents historyEvents,
    IProjectVersionAutoPushQueue autoPushQueue,
    IManuscriptService manuscripts,
    IAuthoringMutationFence authoringFence) : IProjectVersionHistoryService
{
    private const string TemporaryDirectoryPrefix = "lorekeeper-version-history-";

    public async Task<ProjectVersionRepositoryView?> GetRepositoryAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        await using var operation = await database.OpenReadAsync(cancellationToken);
        var repository = await operation.Db.ProjectVersionRepositories
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken);
        return repository is null ? null : ToRepositoryView(repository, ProjectVersionRepositoryHealthFromCache(repository));
    }

    public async Task<ProjectVersionRepositoryView> EnsureRepositoryAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        await using var projectLease = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var repository = await EnsureRepositoryUnderLeaseAsync(projectId, cancellationToken);
        return ToRepositoryView(repository, ProjectVersionRepositoryHealthFromCache(repository));
    }

    public Task<ProjectVersionCheckpointView> CreateCheckpointAsync(
        Guid projectId,
        ProjectVersionCheckpointKind kind,
        string semanticMessage,
        string? requestKey = null,
        DateTimeOffset? authoredAt = null,
        CancellationToken cancellationToken = default) =>
        ExecuteFenceAsync(
            projectId,
            "capture a version-history checkpoint",
            token => CreateCheckpointCoreAsync(projectId, kind, semanticMessage, requestKey, authoredAt, token),
            cancellationToken);

    private async Task<ProjectVersionCheckpointView> CreateCheckpointCoreAsync(
        Guid projectId,
        ProjectVersionCheckpointKind kind,
        string semanticMessage,
        string? requestKey = null,
        DateTimeOffset? authoredAt = null,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        if (string.IsNullOrWhiteSpace(semanticMessage))
            throw new ArgumentException("A semantic checkpoint message is required.", nameof(semanticMessage));
        if (semanticMessage.Contains('\0'))
            throw new ArgumentException("A checkpoint message cannot contain a null character.", nameof(semanticMessage));
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (requestKey?.Length > 200)
            throw new ArgumentException("A checkpoint request key is too long.", nameof(requestKey));

        await using var projectLease = await projectMutations.AcquireAsync(projectId, cancellationToken);
        return await CreateCheckpointUnderLeaseAsync(
            projectId,
            kind,
            semanticMessage,
            requestKey,
            authoredAt,
            cancellationToken);
    }

    private async Task<ProjectVersionCheckpointView> CreateCheckpointUnderLeaseAsync(
        Guid projectId,
        ProjectVersionCheckpointKind kind,
        string semanticMessage,
        string? requestKey,
        DateTimeOffset? authoredAt,
        CancellationToken cancellationToken,
        VersionHistorySnapshotArtifact? capturedSnapshot = null,
        bool disableReviewEdits = false,
        bool publishEvents = true)
    {
        var repository = await EnsureRepositoryUnderLeaseAsync(projectId, cancellationToken);
        var operationId = await StartOperationAsync(
            repository.Id,
            ProjectVersionOperationKind.Checkpoint,
            requestKey,
            cancellationToken);
        string? temporaryDirectory = null;
        SnapshotDescriptorCapture? files = null;

        try
        {
            VersionHistorySnapshotArtifact validated;
            if (capturedSnapshot is null)
            {
                temporaryDirectory = CreateTemporaryDirectory();
                validated = await snapshotWriter.WriteAsync(
                    repository.Id,
                    projectId,
                    temporaryDirectory,
                    cancellationToken);
                EnsureNoReparsePointsRecursively(temporaryDirectory);
                files = await CaptureSnapshotFilesAsync(temporaryDirectory, cancellationToken);
            }
            else
            {
                EnsureNoReparsePointsRecursively(capturedSnapshot.RootDirectory);
                validated = capturedSnapshot;
                files = await CaptureSnapshotFilesAsync(capturedSnapshot.RootDirectory, cancellationToken);
            }

            var existingHead = ReadExistingHead(repository);
            if (existingHead?.Commit is not null)
            {
                var current = LoadGitCheckpoint(
                    repository.Id,
                    existingHead.Commit.Sha,
                    recordedCheckpoint: null,
                    projectId,
                    cancellationToken);
                if (snapshotComparer.Compare(current.Payload, validated.Payload).IsIdentical)
                {
                    var duplicate = await PersistCheckpointAndCompleteAsync(
                        repository.Id,
                        operationId,
                        current.Commit,
                        current.Manifest,
                        kind,
                        semanticMessage,
                        authoredAt ?? DateTimeOffset.UtcNow,
                        createdCommit: false,
                        cancellationToken,
                        projectId,
                        disableReviewEdits);
                    if (publishEvents)
                    {
                        historyEvents.PublishCheckpointCreated(projectId);
                        historyEvents.PublishReviewStateChanged(projectId);
                    }
                    return duplicate;
                }
            }

            var write = git.WriteSnapshot(
                repository.Id,
                files.Files,
                semanticMessage,
                authoredAt ?? DateTimeOffset.UtcNow);
            var checkpoint = await PersistCheckpointAndCompleteAsync(
                repository.Id,
                operationId,
                write.Commit,
                validated.Manifest,
                kind,
                semanticMessage,
                authoredAt ?? DateTimeOffset.UtcNow,
                write.Created,
                cancellationToken,
                projectId,
                disableReviewEdits);
            if (publishEvents)
            {
                historyEvents.PublishCheckpointCreated(projectId);
                historyEvents.PublishReviewStateChanged(projectId);
            }
            return checkpoint;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryCancelOperationAsync(operationId);
            throw;
        }
        catch (Exception exception)
        {
            await TryFailOperationAsync(operationId, exception, CancellationToken.None);
            throw;
        }
        finally
        {
            if (files is not null)
                await files.DisposeAsync();
            CleanupTemporaryDirectory(temporaryDirectory);
        }
    }

    public async Task<ProjectVersionTimelineView?> GetTimelineAsync(
        Guid projectId,
        int maxCheckpoints = 100,
        int maxOperations = 100,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        if (maxCheckpoints <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxCheckpoints));
        if (maxOperations <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxOperations));

        await using var operation = await database.OpenReadAsync(cancellationToken);
        var repository = await operation.Db.ProjectVersionRepositories
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken);
        if (repository is null)
            return null;

        var checkpoints = await operation.Db.ProjectVersionCheckpoints
            .AsNoTracking()
            .Where(item => item.ProjectVersionRepositoryId == repository.Id)
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Take(maxCheckpoints)
            .ToListAsync(cancellationToken);
        var operations = await operation.Db.ProjectVersionOperations
            .AsNoTracking()
            .Where(item => item.ProjectVersionRepositoryId == repository.Id)
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Take(maxOperations)
            .ToListAsync(cancellationToken);

        return new ProjectVersionTimelineView(
            ToRepositoryView(repository, ProjectVersionRepositoryHealthFromCache(repository)),
            checkpoints.Select(ToCheckpointView).ToList(),
            operations.Select(ToOperationView).ToList());
    }

    public async Task<int> ClearFailedOperationNoticesAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        await using var projectLease = await projectMutations.AcquireAsync(projectId, cancellationToken);
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var repository = await operation.Db.ProjectVersionRepositories
            .SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken);
        if (repository is null)
            return 0;

        var failedOperations = await operation.Db.ProjectVersionOperations
            .Where(item => item.ProjectVersionRepositoryId == repository.Id)
            .Where(item => item.Status == ProjectVersionOperationStatus.Failed)
            .Where(item => item.AcknowledgedAt == null)
            .ToListAsync(cancellationToken);
        if (failedOperations.Count == 0)
            return 0;

        var acknowledgedAt = DateTime.UtcNow;
        foreach (var failedOperation in failedOperations)
        {
            failedOperation.AcknowledgedAt = acknowledgedAt;
            failedOperation.UpdatedAt = acknowledgedAt;
        }

        await operation.SaveChangesAsync(cancellationToken);
        return failedOperations.Count;
    }

    public Task<ProjectVersionStatusView?> GetStatusAsync(
        Guid projectId,
        bool includeCurrentSnapshotHash = true,
        CancellationToken cancellationToken = default) =>
        ExecuteFenceAsync(
            projectId,
            "read version-history status",
            token => GetStatusCoreAsync(projectId, includeCurrentSnapshotHash, token),
            cancellationToken);

    private async Task<ProjectVersionStatusView?> GetStatusCoreAsync(
        Guid projectId,
        bool includeCurrentSnapshotHash = true,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        await using var projectLease = await projectMutations.AcquireAsync(projectId, cancellationToken);
        return await GetStatusUnderLeaseAsync(projectId, includeCurrentSnapshotHash, cancellationToken);
    }

    public async Task SetReviewEditsEnabledAsync(
        Guid projectId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        _ = await ExecuteFenceAsync(
            projectId,
            "change the Review Edits saved-state policy",
            async token =>
            {
                await SetReviewEditsEnabledCoreAsync(projectId, enabled, token);
                return true;
            },
            cancellationToken);
    }

    private async Task SetReviewEditsEnabledCoreAsync(
        Guid projectId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        await using var projectLease = await projectMutations.AcquireAsync(projectId, cancellationToken);

        var status = await GetStatusUnderLeaseAsync(
            projectId,
            includeCurrentSnapshotHash: true,
            cancellationToken);
        if (status is null)
        {
            if (!enabled)
                throw new InvalidOperationException("Review Edits cannot be disabled because project history is unavailable.");

            _ = await EnsureRepositoryUnderLeaseAsync(projectId, cancellationToken);
            status = await GetStatusUnderLeaseAsync(projectId, includeCurrentSnapshotHash: true, cancellationToken)
                ?? throw new InvalidOperationException("Review Edits could not initialize project history.");
        }

        if (enabled && string.IsNullOrWhiteSpace(status.Repository.HeadCommitSha))
        {
            _ = await CreateCheckpointUnderLeaseAsync(
                projectId,
                ProjectVersionCheckpointKind.Initial,
                "Initialize Review Edits baseline",
                requestKey: $"review-baseline:{projectId:N}",
                authoredAt: null,
                cancellationToken);
            status = await GetStatusUnderLeaseAsync(projectId, includeCurrentSnapshotHash: true, cancellationToken)
                ?? throw new InvalidOperationException("Review Edits could not read the initialized history.");
        }

        EnsureReviewRepositoryUsable(status.Repository);
        if (!enabled)
        {
            if (string.IsNullOrWhiteSpace(status.Repository.HeadCommitSha))
                throw new InvalidOperationException("Review Edits cannot be disabled until an approved Git baseline exists.");
            if (string.IsNullOrWhiteSpace(status.Repository.HeadContentHash)
                || string.IsNullOrWhiteSpace(status.CurrentContentHash))
            {
                throw new InvalidOperationException("Review Edits cannot be disabled because the live project or approved history could not be verified.");
            }
            if (status.Repository.IsDirty)
                throw new InvalidOperationException(
                    "Pending changes must be approved before Review Edits can be disabled.");
        }

        await SetReviewEditsPreferenceUnderLeaseAsync(projectId, enabled, cancellationToken);

        historyEvents.PublishReviewStateChanged(projectId);
    }

    private async Task SetReviewEditsPreferenceUnderLeaseAsync(
        Guid projectId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        operation.ShareWithNestedOperations();
        var project = await operation.Repositories.Projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");
        if (project.ReviewEditsEnabled == enabled)
            return;

        project.ReviewEditsEnabled = enabled;
        project.UpdatedAt = DateTime.UtcNow;
        operation.Repositories.Projects.Update(project);
        await operation.SaveChangesAsync(cancellationToken);
    }

    public Task<ProjectVersionReviewView?> GetReviewAsync(
        Guid projectId,
        IReadOnlyCollection<ProjectVersionReviewTarget>? targets = null,
        CancellationToken cancellationToken = default) =>
        ExecuteFenceAsync(
            projectId,
            "capture pending Review Edits state",
            token => GetReviewCoreAsync(projectId, targets, token),
            cancellationToken);

    private async Task<ProjectVersionReviewView?> GetReviewCoreAsync(
        Guid projectId,
        IReadOnlyCollection<ProjectVersionReviewTarget>? targets = null,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        await using var projectLease = await projectMutations.AcquireAsync(projectId, cancellationToken);

        // Status validates the immutable HEAD. Capture the live project once
        // below and derive its hash from that validated artifact, avoiding a
        // second complete export for the review projection.
        var status = await GetStatusUnderLeaseAsync(
            projectId,
            includeCurrentSnapshotHash: false,
            cancellationToken);
        if (status is null)
            return null;

        EnsureReviewRepositoryUsable(status.Repository);

        if (string.IsNullOrWhiteSpace(status.Repository.HeadCommitSha))
        {
            var uninitializedToken = new ProjectVersionReviewConcurrencyToken(
                status.Repository.RepositoryId,
                status.Repository.HeadCommitSha,
                status.Repository.HeadContentHash,
                string.Empty);
            return new ProjectVersionReviewView(
                status.Repository,
                ApprovedCheckpoint: null,
                uninitializedToken,
                Comparison: null,
                Chapters: [],
                DependencyGroups: [],
                DesignedPages: []);
        }

        var current = await CaptureCurrentSnapshotUnderLeaseAsync(
            status.Repository.RepositoryId,
            projectId,
            cancellationToken);
        var approved = LoadGitCheckpoint(
            status.Repository.RepositoryId,
            status.Repository.HeadCommitSha,
            recordedCheckpoint: null,
            projectId,
            cancellationToken);
        var comparison = snapshotComparer.Compare(approved.Payload, current.Payload);
        var reviewStatus = status with
        {
            CurrentContentHash = current.Manifest.ContentHash,
            Repository = status.Repository with { IsDirty = !comparison.IsIdentical },
        };
        var token = new ProjectVersionReviewConcurrencyToken(
            reviewStatus.Repository.RepositoryId,
            reviewStatus.Repository.HeadCommitSha,
            reviewStatus.Repository.HeadContentHash,
            current.Manifest.ContentHash);
        var dependencyGroups = BuildReviewDependencyGroups(comparison);
        var recordedCheckpoint = await LoadRecordedCheckpointUnderLeaseAsync(
            status.Repository.RepositoryId,
            approved.Commit.Sha,
            cancellationToken);
        var reviewTargets = targets is null
            ? BuildDefaultReviewTargets(approved.Payload, current.Payload)
            : NormalizeReviewTargets(targets);
        var chapters = reviewTargets
            .Select(target => BuildReviewChapter(approved.Payload, current.Payload, target, token))
            .Where(chapter => chapter is not null)
            .Cast<ProjectVersionReviewChapter>()
            .ToList();
        var designedPages = FindReviewDesignedPages(approved.Payload, current.Payload);
        if (targets is not null)
        {
            var targetKeys = reviewTargets
                .Select(target => (target.ChapterId, target.ContentTarget.StorageKey))
                .ToHashSet();
            designedPages = designedPages
                .Where(page => page.PlacementLinks.Any(link =>
                    targetKeys.Contains((link.ChapterId, link.ContentTarget.StorageKey))))
                .ToList();
        }

        return new ProjectVersionReviewView(
            reviewStatus.Repository,
            recordedCheckpoint is null ? null : ToCheckpointView(recordedCheckpoint),
            token,
            comparison,
            chapters,
            dependencyGroups,
            designedPages);
    }

    public async Task<ProjectVersionReviewChapter?> GetReviewChapterAsync(
        Guid projectId,
        Guid chapterId,
        EditorContentTarget contentTarget,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        if (chapterId == Guid.Empty)
            throw new ArgumentException("A chapter ID is required.", nameof(chapterId));
        ValidateReviewTarget(contentTarget);

        var review = await GetReviewAsync(
            projectId,
            [new ProjectVersionReviewTarget(chapterId, contentTarget)],
            cancellationToken);
        return review?.Chapters.SingleOrDefault();
    }

    public async Task<ProjectVersionHistoricalChapterReview?> GetLatestAffectingChapterAsync(
        Guid projectId,
        Guid chapterId,
        EditorContentTarget contentTarget,
        int maxCommits = 100,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        if (chapterId == Guid.Empty)
            throw new ArgumentException("A chapter ID is required.", nameof(chapterId));
        ValidateReviewTarget(contentTarget);
        if (maxCommits <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxCommits));

        await using var projectLease = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var status = await GetStatusUnderLeaseAsync(projectId, includeCurrentSnapshotHash: false, cancellationToken);
        if (status is null || string.IsNullOrWhiteSpace(status.Repository.HeadCommitSha))
            return null;
        EnsureReviewRepositoryUsable(status.Repository);

        if (reviewCache.TryGetHistoricalChapterReview(
                status.Repository.RepositoryId,
                status.Repository.HeadCommitSha,
                chapterId,
                contentTarget.StorageKey,
                maxCommits,
                out var cached))
        {
            return cached;
        }

        ProjectVersionHistoricalChapterReview? result = null;
        const int maxScanCheckpointEntries = 4;
        var scanCheckpoints = new Dictionary<string, LoadedGitCheckpoint>(StringComparer.Ordinal);
        var scanCheckpointOrder = new Queue<string>();

        LoadedGitCheckpoint LoadScanCheckpoint(string commitSha)
        {
            if (scanCheckpoints.TryGetValue(commitSha, out var existing))
                return existing;

            var loaded = LoadGitCheckpoint(
                status.Repository.RepositoryId,
                commitSha,
                recordedCheckpoint: null,
                projectId,
                cancellationToken);
            scanCheckpoints[commitSha] = loaded;
            scanCheckpointOrder.Enqueue(commitSha);
            while (scanCheckpointOrder.Count > maxScanCheckpointEntries)
            {
                var expiredSha = scanCheckpointOrder.Dequeue();
                scanCheckpoints.Remove(expiredSha);
            }
            return loaded;
        }

        foreach (var commit in git.ListCommits(status.Repository.RepositoryId, maxCommits))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var after = LoadScanCheckpoint(commit.Sha);
            var before = commit.ParentShas.FirstOrDefault() is { } parentSha
                ? LoadScanCheckpoint(parentSha)
                : null;
            var beforeChapter = before is null
                ? null
                : FindEffectiveChapter(
                    before.Payload,
                    new ProjectVersionReviewTarget(chapterId, contentTarget));
            var afterChapter = FindEffectiveChapter(
                after.Payload,
                new ProjectVersionReviewTarget(chapterId, contentTarget));
            var designedPageChanges = before is null
                ? []
                : FindReviewDesignedPages(
                    before.Payload,
                    after.Payload)
                    .Where(page => page.PlacementLinks.Any(link =>
                        link.ChapterId == chapterId
                        && link.ContentTarget == contentTarget))
                    .ToList();
            if (ProjectVersionReviewChapter.ManuscriptSemanticallyEquals(beforeChapter, afterChapter)
                && designedPageChanges.Count == 0)
                continue;

            var checkpoint = await LoadRecordedCheckpointUnderLeaseAsync(
                status.Repository.RepositoryId,
                commit.Sha,
                cancellationToken);
            result = new ProjectVersionHistoricalChapterReview(
                chapterId,
                contentTarget,
                commit,
                checkpoint is null ? null : ToCheckpointView(checkpoint),
                beforeChapter,
                afterChapter,
                designedPageChanges);
            break;
        }

        reviewCache.SetHistoricalChapterReview(
            status.Repository.RepositoryId,
            status.Repository.HeadCommitSha,
            chapterId,
            contentTarget.StorageKey,
            maxCommits,
            result);
        return result;
    }

    public async Task<ProjectVersionHistoricalRestoreResult> RestoreHistoricalChapterAsync(
        Guid projectId,
        Guid chapterId,
        EditorContentTarget contentTarget,
        string historicalCommitSha,
        ProjectVersionReviewConcurrencyToken expectedToken,
        string semanticMessage = "Undo manuscript to historical checkpoint",
        string? requestKey = null,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        if (chapterId == Guid.Empty)
            throw new ArgumentException("A chapter ID is required.", nameof(chapterId));
        ValidateReviewTarget(contentTarget);
        if (string.IsNullOrWhiteSpace(historicalCommitSha))
            throw new ArgumentException("A historical commit SHA is required.", nameof(historicalCommitSha));
        ArgumentNullException.ThrowIfNull(expectedToken);
        if (string.IsNullOrWhiteSpace(semanticMessage) || semanticMessage.Contains('\0'))
            throw new ArgumentException("A semantic undo message is required.", nameof(semanticMessage));
        if (requestKey?.Length > 200)
            throw new ArgumentException("A review request key is too long.", nameof(requestKey));

        ManuscriptDocument historicalDocument;
        long expectedRevision;
        await using (var projectLease = await projectMutations.AcquireAsync(projectId, cancellationToken))
        {
            var status = await GetStatusUnderLeaseAsync(projectId, includeCurrentSnapshotHash: true, cancellationToken)
                ?? throw new InvalidOperationException($"Project {projectId} has no version-history repository.");
            EnsureReviewRepositoryUsable(status.Repository);
            EnsureReviewTokenMatches(status, expectedToken);

            var current = await CaptureCurrentSnapshotUnderLeaseAsync(
                status.Repository.RepositoryId,
                projectId,
                cancellationToken);
            var target = new ProjectVersionReviewTarget(chapterId, contentTarget);
            var currentChapter = FindEffectiveChapter(current.Payload, target)
                ?? throw new InvalidOperationException("The live chapter target no longer exists.");
            var historical = LoadGitCheckpoint(
                status.Repository.RepositoryId,
                historicalCommitSha,
                recordedCheckpoint: null,
                projectId,
                cancellationToken);
            var historicalChapter = FindEffectiveChapter(historical.Payload, target)
                ?? throw new InvalidOperationException("The historical checkpoint does not contain this chapter target.");
            historicalDocument = ManuscriptCodec.Deserialize(
                historicalChapter.ManuscriptJson,
                chapterId,
                historicalChapter.ManuscriptRevision);
            var currentDocument = ManuscriptCodec.Deserialize(
                currentChapter.ManuscriptJson,
                chapterId,
                currentChapter.ManuscriptRevision);
            if (ManuscriptCodec.ContentEquals(currentDocument, historicalDocument))
                throw new InvalidOperationException("The live manuscript already matches the historical checkpoint.");

            expectedRevision = currentDocument.Revision;
        }

        // The manuscript service owns rich-document persistence, validation,
        // annotation rebasing, composition cleanup, and derived-state refresh.
        // Invoke it after releasing the history lease because the normal
        // mutation boundary acquires the same project lease itself.
        var restored = await manuscripts.ReplaceDocumentAsync(
            contentTarget,
            chapterId,
            expectedRevision,
            historicalDocument,
            cancellationToken);
        var restoredStatus = await GetStatusAsync(
            projectId,
            includeCurrentSnapshotHash: true,
            cancellationToken);
        var restoredToken = restoredStatus is null
            ? expectedToken
            : new ProjectVersionReviewConcurrencyToken(
                restoredStatus.Repository.RepositoryId,
                restoredStatus.Repository.HeadCommitSha,
                restoredStatus.Repository.HeadContentHash,
                restoredStatus.CurrentContentHash ?? string.Empty);
        historyEvents.PublishReviewStateChanged(projectId);
        return new ProjectVersionHistoricalRestoreResult(
            chapterId,
            contentTarget,
            historicalCommitSha,
            restored.Snapshot.Revision,
            restored.Snapshot.SourceHash,
            restoredToken);
    }

    public async Task<ProjectVersionCheckpointView> CreateReviewApprovalCheckpointAsync(
        Guid projectId,
        ProjectVersionReviewConcurrencyToken expectedToken,
        string semanticMessage = "Approved Review Edits",
        string? requestKey = null,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        ArgumentNullException.ThrowIfNull(expectedToken);
        if (string.IsNullOrWhiteSpace(semanticMessage))
            throw new ArgumentException("A semantic checkpoint message is required.", nameof(semanticMessage));
        if (semanticMessage.Contains('\0'))
            throw new ArgumentException("A checkpoint message cannot contain a null character.", nameof(semanticMessage));
        if (requestKey?.Length > 200)
            throw new ArgumentException("A checkpoint request key is too long.", nameof(requestKey));
        if (expectedToken.RepositoryId == Guid.Empty)
            throw new ArgumentException("A review token must identify a repository.", nameof(expectedToken));
        if (string.IsNullOrWhiteSpace(expectedToken.CurrentContentHash))
            throw new ArgumentException("A review token must include the current content hash.", nameof(expectedToken));

        await using var projectLease = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var status = await GetStatusUnderLeaseAsync(
            projectId,
            includeCurrentSnapshotHash: true,
            cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} has no version-history repository.");
        EnsureReviewRepositoryUsable(status.Repository);

        if (status.Repository.RepositoryId != expectedToken.RepositoryId
            || !string.Equals(status.Repository.HeadCommitSha, expectedToken.HeadCommitSha, StringComparison.Ordinal)
            || !string.Equals(status.Repository.HeadContentHash, expectedToken.HeadContentHash, StringComparison.Ordinal)
            || !string.Equals(status.CurrentContentHash, expectedToken.CurrentContentHash, StringComparison.Ordinal))
        {
            throw new ProjectVersionReviewConcurrencyException(
                "The project changed after this review was loaded. Reload the review before approving it.");
        }

        var checkpoint = await CreateCheckpointUnderLeaseAsync(
            projectId,
            ProjectVersionCheckpointKind.ReviewApproval,
            semanticMessage,
            requestKey,
            authoredAt: null,
            cancellationToken);
        historyEvents.PublishReviewStateChanged(projectId);
        return checkpoint;
    }

    public async Task<ProjectVersionCheckpointView> ApproveAllAndDisableReviewEditsAsync(
        Guid projectId,
        ProjectVersionReviewConcurrencyToken expectedToken,
        string semanticMessage = "Approved Review Edits before disabling Review Edits",
        string? requestKey = null,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        ArgumentNullException.ThrowIfNull(expectedToken);
        if (string.IsNullOrWhiteSpace(semanticMessage))
            throw new ArgumentException("A semantic checkpoint message is required.", nameof(semanticMessage));
        if (semanticMessage.Contains('\0'))
            throw new ArgumentException("A checkpoint message cannot contain a null character.", nameof(semanticMessage));
        if (requestKey?.Length > 200)
            throw new ArgumentException("A checkpoint request key is too long.", nameof(requestKey));
        if (expectedToken.RepositoryId == Guid.Empty)
            throw new ArgumentException("A review token must identify a repository.", nameof(expectedToken));
        if (string.IsNullOrWhiteSpace(expectedToken.CurrentContentHash))
            throw new ArgumentException("A review token must include the current content hash.", nameof(expectedToken));

        await using var projectLease = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var status = await GetStatusUnderLeaseAsync(
            projectId,
            includeCurrentSnapshotHash: false,
            cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} has no version-history repository.");
        EnsureReviewRepositoryUsable(status.Repository);
        if (string.IsNullOrWhiteSpace(status.Repository.HeadCommitSha))
            throw new InvalidOperationException("Review Edits cannot be disabled until an approved Git baseline exists.");

        var approved = LoadGitCheckpoint(
            status.Repository.RepositoryId,
            status.Repository.HeadCommitSha,
            recordedCheckpoint: null,
            projectId,
            cancellationToken);
        var temporaryDirectory = CreateTemporaryDirectory();
        try
        {
            var current = await snapshotWriter.WriteAsync(
                status.Repository.RepositoryId,
                projectId,
                temporaryDirectory,
                cancellationToken);
            EnsureNoReparsePointsRecursively(temporaryDirectory);
            var comparison = snapshotComparer.Compare(approved.Payload, current.Payload);
            var currentStatus = status with
            {
                CurrentContentHash = current.Manifest.ContentHash,
                Repository = status.Repository with { IsDirty = !comparison.IsIdentical },
            };
            EnsureReviewTokenMatches(currentStatus, expectedToken);

            if (comparison.IsIdentical)
            {
                var recordedCheckpoint = await LoadRecordedCheckpointUnderLeaseAsync(
                    status.Repository.RepositoryId,
                    approved.Commit.Sha,
                    cancellationToken)
                    ?? throw new InvalidOperationException("The approved Git head has no checkpoint record.");
                await SetReviewEditsPreferenceUnderLeaseAsync(projectId, enabled: false, cancellationToken);
                historyEvents.PublishReviewStateChanged(projectId);
                return ToCheckpointView(recordedCheckpoint);
            }

            var checkpoint = await CreateCheckpointUnderLeaseAsync(
                projectId,
                ProjectVersionCheckpointKind.ReviewApproval,
                semanticMessage,
                requestKey,
                authoredAt: null,
                cancellationToken,
                capturedSnapshot: current,
                disableReviewEdits: true,
                publishEvents: false);
            historyEvents.PublishCheckpointCreated(projectId);
            historyEvents.PublishReviewStateChanged(projectId);
            return checkpoint;
        }
        finally
        {
            CleanupTemporaryDirectory(temporaryDirectory);
        }
    }

    public async Task<ProjectVersionCheckpointView> CreateReviewApprovalForChapterAsync(
        Guid projectId,
        ProjectVersionReviewTarget target,
        ProjectVersionReviewConcurrencyToken expectedToken,
        string semanticMessage = "Approved chapter review changes",
        string? requestKey = null,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        ValidateReviewTarget(target.ContentTarget);
        if (target.ChapterId == Guid.Empty)
            throw new ArgumentException("A review target must identify a chapter.", nameof(target));
        ArgumentNullException.ThrowIfNull(expectedToken);
        if (string.IsNullOrWhiteSpace(semanticMessage) || semanticMessage.Contains('\0'))
            throw new ArgumentException("A semantic checkpoint message is required.", nameof(semanticMessage));
        if (requestKey?.Length > 200)
            throw new ArgumentException("A review request key is too long.", nameof(requestKey));

        await using var projectLease = await projectMutations.AcquireAsync(projectId, cancellationToken);
        // The status call validates the current Git head. The live hash is
        // derived from the one current artifact captured below so approval
        // does not export the project twice while still doing a fresh token
        // check immediately before synthesis.
        var status = await GetStatusUnderLeaseAsync(
            projectId,
            includeCurrentSnapshotHash: false,
            cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} has no version-history repository.");
        EnsureReviewRepositoryUsable(status.Repository);
        if (expectedToken.RepositoryId == Guid.Empty)
            throw new ArgumentException("A review token must identify a repository.", nameof(expectedToken));
        if (string.IsNullOrWhiteSpace(expectedToken.CurrentContentHash))
            throw new ArgumentException("A review token must include the current content hash.", nameof(expectedToken));
        if (string.IsNullOrWhiteSpace(status.Repository.HeadCommitSha))
            throw new InvalidOperationException("Chapter review approval requires an approved Git head.");

        var current = await CaptureCurrentSnapshotUnderLeaseAsync(
            status.Repository.RepositoryId,
            projectId,
            cancellationToken);
        var currentStatus = status with
        {
            CurrentContentHash = current.Manifest.ContentHash,
        };

        var approved = LoadGitCheckpoint(
            status.Repository.RepositoryId,
            status.Repository.HeadCommitSha,
            recordedCheckpoint: null,
            projectId,
            cancellationToken);
        var comparison = snapshotComparer.Compare(approved.Payload, current.Payload);
        currentStatus = currentStatus with
        {
            Repository = currentStatus.Repository with { IsDirty = !comparison.IsIdentical },
        };
        EnsureReviewTokenMatches(currentStatus, expectedToken);
        _ = FindEffectiveChapter(approved.Payload, target)
            ?? throw new InvalidOperationException("The approved review target no longer exists.");
        var currentChapter = FindEffectiveChapter(current.Payload, target)
            ?? throw new InvalidOperationException("The live review target no longer exists.");
        var currentDocument = ManuscriptCodec.Deserialize(
            currentChapter.ManuscriptJson,
            target.ChapterId,
            currentChapter.ManuscriptRevision);

        var generatedRoot = CreateTemporaryDirectory();
        try
        {
            git.MaterializeTree(status.Repository.RepositoryId, generatedRoot, status.Repository.HeadCommitSha, cancellationToken);
            var changes = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            // Start from approved HEAD and replace only this target's complete
            // semantic manuscript. Metadata, styles, publication settings, and
            // every other chapter/edition stay at their approved values.
            if (target.ContentTarget.IsCore)
                UpdateSynthesizedReviewTree(changes, approved.Payload, current.Payload, target, currentDocument);
            else
                UpdateSynthesizedEditionTargetReviewTree(changes, approved.Payload, current.Payload, target);

            WriteSnapshotJsonFiles(generatedRoot, changes);
            await RebuildSnapshotManifestAsync(generatedRoot, status.Repository.RepositoryId, projectId, cancellationToken);
            EnsureNoReparsePointsRecursively(generatedRoot);
            var generated = snapshotReader.Read(
                generatedRoot,
                status.Repository.RepositoryId,
                projectId, new() { IncludeAssetData = false, IncludeSourceDetails = false, CancellationToken = cancellationToken });
            if (snapshotComparer.Compare(approved.Payload, generated.Payload).IsIdentical)
                throw new InvalidOperationException("There are no chapter changes to approve.");

            var operationId = await StartOperationAsync(
                status.Repository.RepositoryId,
                ProjectVersionOperationKind.Checkpoint,
                requestKey,
                cancellationToken);
            var authoredAt = DateTimeOffset.UtcNow;
            try
            {
                await using var capture = await CaptureSnapshotFilesAsync(generatedRoot, cancellationToken);
                var write = git.WriteSnapshot(status.Repository.RepositoryId, capture.Files, semanticMessage, authoredAt);
                var checkpoint = await PersistCheckpointAndCompleteAsync(status.Repository.RepositoryId, operationId, write.Commit,
                    generated.Manifest, ProjectVersionCheckpointKind.ReviewApproval, semanticMessage, authoredAt, write.Created, cancellationToken);
                historyEvents.PublishCheckpointCreated(projectId);
                historyEvents.PublishReviewStateChanged(projectId);
                return checkpoint;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await TryCancelOperationAsync(operationId);
                throw;
            }
            catch (Exception exception)
            {
                await TryFailOperationAsync(operationId, exception, CancellationToken.None);
                throw;
            }
        }
        finally { CleanupTemporaryDirectory(generatedRoot); }
    }

    public async Task<ProjectVersionCheckpointView> CreateReviewApprovalForOtherAsync(
        Guid projectId,
        ProjectVersionReviewConcurrencyToken expectedToken,
        string semanticMessage = "Approved other project changes",
        string? requestKey = null,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        ArgumentNullException.ThrowIfNull(expectedToken);
        if (string.IsNullOrWhiteSpace(semanticMessage) || semanticMessage.Contains('\0'))
            throw new ArgumentException("A semantic checkpoint message is required.", nameof(semanticMessage));
        if (requestKey?.Length > 200)
            throw new ArgumentException("A review request key is too long.", nameof(requestKey));

        await using var projectLease = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var status = await GetStatusUnderLeaseAsync(
            projectId,
            includeCurrentSnapshotHash: true,
            cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} has no version-history repository.");
        EnsureReviewRepositoryUsable(status.Repository);
        EnsureReviewTokenMatches(status, expectedToken);
        if (string.IsNullOrWhiteSpace(status.Repository.HeadCommitSha))
            throw new InvalidOperationException("Other review approval requires an approved Git head.");

        var approved = LoadGitCheckpoint(
            status.Repository.RepositoryId,
            status.Repository.HeadCommitSha,
            recordedCheckpoint: null,
            projectId,
            cancellationToken);
        var currentRoot = CreateTemporaryDirectory();
        var generatedRoot = CreateTemporaryDirectory();
        try
        {
            var current = await snapshotWriter.WriteAsync(
                status.Repository.RepositoryId,
                projectId,
                currentRoot,
                cancellationToken);
            EnsureNoReparsePointsRecursively(currentRoot);
            git.MaterializeTree(status.Repository.RepositoryId, generatedRoot, status.Repository.HeadCommitSha, cancellationToken);
            await CopyCurrentOtherFilesAsync(currentRoot, generatedRoot, cancellationToken);
            SynthesizeOtherReviewChapterMetadata(generatedRoot, currentRoot);
            WriteSnapshotJsonFiles(generatedRoot, new Dictionary<string, byte[]>
            {
                ["publication/publication.json"] = VersionHistoryCanonicalJson.Serialize(
                    SynthesizeOtherReviewPublication(approved.Payload.Publication, current.Payload.Publication)),
            });
            await RebuildSnapshotManifestAsync(generatedRoot, status.Repository.RepositoryId, projectId, cancellationToken);
            EnsureNoReparsePointsRecursively(generatedRoot);
            var generated = snapshotReader.Read(
                generatedRoot,
                status.Repository.RepositoryId,
                projectId, new() { IncludeAssetData = false, IncludeSourceDetails = false, CancellationToken = cancellationToken });
            if (snapshotComparer.Compare(approved.Payload, generated.Payload).IsIdentical)
                throw new InvalidOperationException("There are no non-manuscript project changes to approve.");

            var operationId = await StartOperationAsync(status.Repository.RepositoryId, ProjectVersionOperationKind.Checkpoint, requestKey, cancellationToken);
            var authoredAt = DateTimeOffset.UtcNow;
            try
            {
                await using var capture = await CaptureSnapshotFilesAsync(generatedRoot, cancellationToken);
                var write = git.WriteSnapshot(status.Repository.RepositoryId, capture.Files, semanticMessage, authoredAt);
                var checkpoint = await PersistCheckpointAndCompleteAsync(status.Repository.RepositoryId, operationId, write.Commit,
                    generated.Manifest, ProjectVersionCheckpointKind.ReviewApproval, semanticMessage, authoredAt, write.Created, cancellationToken);
                historyEvents.PublishCheckpointCreated(projectId);
                historyEvents.PublishReviewStateChanged(projectId);
                return checkpoint;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { await TryCancelOperationAsync(operationId); throw; }
            catch (Exception exception) { await TryFailOperationAsync(operationId, exception, CancellationToken.None); throw; }
        }
        finally
        {
            CleanupTemporaryDirectory(generatedRoot);
            CleanupTemporaryDirectory(currentRoot);
        }
    }

    public async Task<ProjectVersionCheckpointView> CreateReviewApprovalForBlocksAsync(
        Guid projectId,
        ProjectVersionReviewTarget target,
        IReadOnlyCollection<string> blockIds,
        ProjectVersionReviewConcurrencyToken expectedToken,
        string semanticMessage = "Approved selected manuscript changes",
        string? requestKey = null,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        ValidateReviewTarget(target.ContentTarget);
        if (target.ChapterId == Guid.Empty)
            throw new ArgumentException("A review target must identify a chapter.", nameof(target));
        ArgumentNullException.ThrowIfNull(blockIds);
        ArgumentNullException.ThrowIfNull(expectedToken);
        if (blockIds.Count == 0)
            throw new ArgumentException("At least one manuscript block must be selected.", nameof(blockIds));
        if (string.IsNullOrWhiteSpace(semanticMessage) || semanticMessage.Contains('\0'))
            throw new ArgumentException("A semantic checkpoint message is required.", nameof(semanticMessage));
        if (requestKey?.Length > 200)
            throw new ArgumentException("A review request key is too long.", nameof(requestKey));

        var selectedBlockIds = blockIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.Ordinal);
        if (selectedBlockIds.Count == 0)
            throw new ArgumentException("At least one manuscript block must be selected.", nameof(blockIds));

        await using var projectLease = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var status = await GetStatusUnderLeaseAsync(projectId, includeCurrentSnapshotHash: true, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} has no version-history repository.");
        EnsureReviewRepositoryUsable(status.Repository);
        EnsureReviewTokenMatches(status, expectedToken);
        if (string.IsNullOrWhiteSpace(status.Repository.HeadCommitSha))
            throw new InvalidOperationException("Selected review approval requires an approved Git head.");

        var approved = LoadGitCheckpoint(
            status.Repository.RepositoryId,
            status.Repository.HeadCommitSha,
            recordedCheckpoint: null,
            projectId,
            cancellationToken);
        var current = await CaptureCurrentSnapshotUnderLeaseAsync(
            status.Repository.RepositoryId,
            projectId,
            cancellationToken);
        var approvedChapter = FindEffectiveChapter(approved.Payload, target)
            ?? throw new InvalidOperationException("The approved review target no longer exists.");
        var currentChapter = FindEffectiveChapter(current.Payload, target)
            ?? throw new InvalidOperationException("The live review target no longer exists.");
        var approvedDocument = ManuscriptCodec.Deserialize(
            approvedChapter.ManuscriptJson,
            target.ChapterId,
            approvedChapter.ManuscriptRevision);
        var currentDocument = ManuscriptCodec.Deserialize(
            currentChapter.ManuscriptJson,
            target.ChapterId,
            currentChapter.ManuscriptRevision);
        var synthesized = SynthesizeSelectedBlockApproval(
            approvedDocument,
            currentDocument,
            selectedBlockIds,
            target.ChapterId);
        if (ManuscriptCodec.ContentEquals(approvedDocument, synthesized))
            throw new InvalidOperationException("The selected manuscript blocks are already approved.");

        var generatedRoot = CreateTemporaryDirectory();
        try
        {
            git.MaterializeTree(status.Repository.RepositoryId, generatedRoot, status.Repository.HeadCommitSha, cancellationToken);
            var changes = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            UpdateSynthesizedReviewTree(changes, approved.Payload, current.Payload, target, synthesized);
            WriteSnapshotJsonFiles(generatedRoot, changes);
            await RebuildSnapshotManifestAsync(generatedRoot, status.Repository.RepositoryId, projectId, cancellationToken);
            EnsureNoReparsePointsRecursively(generatedRoot);
            var generatedManifest = snapshotReader.Read(
                generatedRoot,
                status.Repository.RepositoryId,
                projectId, new() { IncludeAssetData = false, IncludeSourceDetails = false, CancellationToken = cancellationToken }).Manifest;
            var operationId = await StartOperationAsync(status.Repository.RepositoryId, ProjectVersionOperationKind.Checkpoint, requestKey, cancellationToken);
            var authoredAt = DateTimeOffset.UtcNow;
            try
            {
                await using var capture = await CaptureSnapshotFilesAsync(generatedRoot, cancellationToken);
                var write = git.WriteSnapshot(status.Repository.RepositoryId, capture.Files, semanticMessage, authoredAt);
                var checkpoint = await PersistCheckpointAndCompleteAsync(status.Repository.RepositoryId, operationId, write.Commit,
                    generatedManifest, ProjectVersionCheckpointKind.ReviewApproval, semanticMessage, authoredAt, write.Created, cancellationToken);
                historyEvents.PublishCheckpointCreated(projectId);
                historyEvents.PublishReviewStateChanged(projectId);
                return checkpoint;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { await TryCancelOperationAsync(operationId); throw; }
            catch (Exception exception) { await TryFailOperationAsync(operationId, exception, CancellationToken.None); throw; }
        }
        finally { CleanupTemporaryDirectory(generatedRoot); }
    }

    public async Task<ProjectVersionCheckpointView> ApproveReviewDesignedPageAsync(
        Guid projectId,
        Guid designedPageId,
        ProjectVersionReviewConcurrencyToken expectedToken,
        string semanticMessage = "Approved Designed Page change",
        string? requestKey = null,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        if (designedPageId == Guid.Empty)
            throw new ArgumentException("A Designed Page ID is required.", nameof(designedPageId));
        ArgumentNullException.ThrowIfNull(expectedToken);
        if (string.IsNullOrWhiteSpace(semanticMessage) || semanticMessage.Contains('\0'))
            throw new ArgumentException("A semantic checkpoint message is required.", nameof(semanticMessage));
        if (requestKey?.Length > 200)
            throw new ArgumentException("A review request key is too long.", nameof(requestKey));

        await using var projectLease = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var status = await GetStatusUnderLeaseAsync(projectId, includeCurrentSnapshotHash: true, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} has no version-history repository.");
        EnsureReviewRepositoryUsable(status.Repository);
        EnsureReviewTokenMatches(status, expectedToken);
        if (string.IsNullOrWhiteSpace(status.Repository.HeadCommitSha))
            throw new InvalidOperationException("Designed Page approval requires an approved Git head.");

        var approved = LoadGitCheckpoint(
            status.Repository.RepositoryId,
            status.Repository.HeadCommitSha,
            recordedCheckpoint: null,
            projectId,
            cancellationToken);
        var current = await CaptureCurrentSnapshotUnderLeaseAsync(
            status.Repository.RepositoryId,
            projectId,
            cancellationToken);
        var currentDesignedPage = current.Payload.Composition.DesignedPages
            .SingleOrDefault(item => item.Id == designedPageId)
            ?? throw new InvalidOperationException("The live Designed Page no longer exists.");
        var approvedDesignedPage = approved.Payload.Composition.DesignedPages
            .SingleOrDefault(item => item.Id == designedPageId);
        if (ProjectVersionReviewDesignedPage.SemanticallyEquals(approvedDesignedPage, currentDesignedPage))
            throw new InvalidOperationException("The Designed Page change is already approved.");

        var synthesizedDesignedPages = approved.Payload.Composition.DesignedPages
            .Where(item => item.Id != designedPageId)
            .Append(currentDesignedPage)
            .OrderBy(item => item.Id)
            .ToList();
        var generatedRoot = CreateTemporaryDirectory();
        try
        {
            git.MaterializeTree(status.Repository.RepositoryId, generatedRoot, status.Repository.HeadCommitSha, cancellationToken);
            WriteSnapshotJsonFiles(generatedRoot, new Dictionary<string, byte[]>
            {
                ["composition/composition.json"] = VersionHistoryCanonicalJson.Serialize(
                    new VersionHistorySnapshotCompositionArea(synthesizedDesignedPages)),
            });
            await RebuildSnapshotManifestAsync(generatedRoot, status.Repository.RepositoryId, projectId, cancellationToken);
            EnsureNoReparsePointsRecursively(generatedRoot);
            var generatedManifest = snapshotReader.Read(
                generatedRoot,
                status.Repository.RepositoryId,
                projectId, new() { IncludeAssetData = false, IncludeSourceDetails = false, CancellationToken = cancellationToken }).Manifest;
            var operationId = await StartOperationAsync(status.Repository.RepositoryId, ProjectVersionOperationKind.Checkpoint, requestKey, cancellationToken);
            var authoredAt = DateTimeOffset.UtcNow;
            try
            {
                await using var capture = await CaptureSnapshotFilesAsync(generatedRoot, cancellationToken);
                var write = git.WriteSnapshot(status.Repository.RepositoryId, capture.Files, semanticMessage, authoredAt);
                var checkpoint = await PersistCheckpointAndCompleteAsync(status.Repository.RepositoryId, operationId, write.Commit,
                    generatedManifest, ProjectVersionCheckpointKind.ReviewApproval, semanticMessage, authoredAt, write.Created, cancellationToken);
                historyEvents.PublishCheckpointCreated(projectId);
                historyEvents.PublishReviewStateChanged(projectId);
                return checkpoint;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { await TryCancelOperationAsync(operationId); throw; }
            catch (Exception exception) { await TryFailOperationAsync(operationId, exception, CancellationToken.None); throw; }
        }
        finally { CleanupTemporaryDirectory(generatedRoot); }
    }

    public async Task<ProjectVersionReviewBlockMutationResult> RestoreReviewBlocksAsync(
        Guid projectId,
        ProjectVersionReviewTarget target,
        IReadOnlyCollection<string> blockIds,
        ProjectVersionReviewConcurrencyToken expectedToken,
        string semanticMessage = "Undid selected manuscript changes",
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        ValidateReviewTarget(target.ContentTarget);
        if (target.ChapterId == Guid.Empty)
            throw new ArgumentException("A review target must identify a chapter.", nameof(target));
        ArgumentNullException.ThrowIfNull(blockIds);
        ArgumentNullException.ThrowIfNull(expectedToken);
        if (blockIds.Count == 0)
            throw new ArgumentException("At least one manuscript block must be selected.", nameof(blockIds));
        if (string.IsNullOrWhiteSpace(semanticMessage) || semanticMessage.Contains('\0'))
            throw new ArgumentException("A semantic undo message is required.", nameof(semanticMessage));

        var selectedBlockIds = blockIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.Ordinal);
        if (selectedBlockIds.Count == 0)
            throw new ArgumentException("At least one manuscript block must be selected.", nameof(blockIds));

        ManuscriptDocument currentDocument;
        ManuscriptDocument approvedDocument;
        await using (var projectLease = await projectMutations.AcquireAsync(projectId, cancellationToken))
        {
            var status = await GetStatusUnderLeaseAsync(projectId, includeCurrentSnapshotHash: true, cancellationToken)
                ?? throw new InvalidOperationException($"Project {projectId} has no version-history repository.");
            EnsureReviewRepositoryUsable(status.Repository);
            EnsureReviewTokenMatches(status, expectedToken);
            if (string.IsNullOrWhiteSpace(status.Repository.HeadCommitSha))
                throw new InvalidOperationException("Selected review undo requires an approved Git head.");

            var approved = LoadGitCheckpoint(
                status.Repository.RepositoryId,
                status.Repository.HeadCommitSha,
                recordedCheckpoint: null,
                projectId,
                cancellationToken);
            var current = await CaptureCurrentSnapshotUnderLeaseAsync(
                status.Repository.RepositoryId,
                projectId,
                cancellationToken);
            var approvedChapter = FindEffectiveChapter(approved.Payload, target)
                ?? throw new InvalidOperationException("The approved review target no longer exists.");
            var currentChapter = FindEffectiveChapter(current.Payload, target)
                ?? throw new InvalidOperationException("The live review target no longer exists.");
            approvedDocument = ManuscriptCodec.Deserialize(
                approvedChapter.ManuscriptJson,
                target.ChapterId,
                approvedChapter.ManuscriptRevision);
            currentDocument = ManuscriptCodec.Deserialize(
                currentChapter.ManuscriptJson,
                target.ChapterId,
                currentChapter.ManuscriptRevision);
        }

        var restoredDocument = SynthesizeSelectedBlockApproval(
            currentDocument,
            approvedDocument,
            selectedBlockIds,
            target.ChapterId);
        if (ManuscriptCodec.ContentEquals(currentDocument, restoredDocument))
            throw new InvalidOperationException("The selected manuscript blocks are already at the approved state.");

        _ = await manuscripts.ReplaceDocumentAsync(
            target.ContentTarget,
            target.ChapterId,
            currentDocument.Revision,
            restoredDocument,
            cancellationToken);
        var restoredStatus = await GetStatusAsync(
            projectId,
            includeCurrentSnapshotHash: true,
            cancellationToken);
        var restoredToken = restoredStatus is null
            ? expectedToken
            : new ProjectVersionReviewConcurrencyToken(
                restoredStatus.Repository.RepositoryId,
                restoredStatus.Repository.HeadCommitSha,
                restoredStatus.Repository.HeadContentHash,
                restoredStatus.CurrentContentHash ?? string.Empty);
        historyEvents.PublishReviewStateChanged(projectId);
        return new ProjectVersionReviewBlockMutationResult(
            selectedBlockIds.Order(StringComparer.Ordinal).ToList(),
            restoredToken);
    }

    public async Task<ProjectVersionReviewBlockMutationResult> EditReviewBlockAsync(
        Guid projectId,
        ProjectVersionReviewTarget target,
        string blockId,
        string text,
        ProjectVersionReviewConcurrencyToken expectedToken,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        ValidateReviewTarget(target.ContentTarget);
        if (target.ChapterId == Guid.Empty)
            throw new ArgumentException("A review target must identify a chapter.", nameof(target));
        if (string.IsNullOrWhiteSpace(blockId))
            throw new ArgumentException("A stable manuscript block ID is required.", nameof(blockId));
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(expectedToken);
        var normalizedText = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (normalizedText.Contains('\n'))
            throw new InvalidOperationException("Inline review edits must stay within one manuscript block.");

        await using var projectLease = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var status = await GetStatusUnderLeaseAsync(projectId, includeCurrentSnapshotHash: true, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} has no version-history repository.");
        EnsureReviewRepositoryUsable(status.Repository);
        EnsureReviewTokenMatches(status, expectedToken);
        if (string.IsNullOrWhiteSpace(status.Repository.HeadCommitSha))
            throw new InvalidOperationException("Inline review editing requires an approved Git head.");

        var current = await CaptureCurrentSnapshotUnderLeaseAsync(
            status.Repository.RepositoryId,
            projectId,
            cancellationToken);
        var currentChapter = FindEffectiveChapter(current.Payload, target)
            ?? throw new InvalidOperationException("The live review target no longer exists.");
        var currentDocument = ManuscriptCodec.Deserialize(
            currentChapter.ManuscriptJson,
            target.ChapterId,
            currentChapter.ManuscriptRevision);
        var normalizedBlockId = blockId.Trim();
        var block = currentDocument.Content.SingleOrDefault(item =>
            string.Equals(item.Id, normalizedBlockId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The pending manuscript block no longer exists.");
        if (block.Type is ManuscriptBlockType.SceneBreak or ManuscriptBlockType.DesignedPage)
            throw new InvalidOperationException("This structural manuscript block cannot be edited as text.");
        if (string.Equals(ManuscriptCodec.Text(block), normalizedText, StringComparison.Ordinal))
            throw new InvalidOperationException("The manuscript block already contains that text.");

        _ = await manuscripts.ApplyPersistedUnderProjectMutationLeaseAsync(
            target.ContentTarget,
            target.ChapterId,
            currentDocument.Revision,
            [new ReplaceManuscriptBlockText(block.Id, normalizedText)],
            cancellationToken);
        var updatedStatus = await GetStatusUnderLeaseAsync(
            projectId,
            includeCurrentSnapshotHash: true,
            cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} has no version-history repository.");
        var updatedToken = new ProjectVersionReviewConcurrencyToken(
            updatedStatus.Repository.RepositoryId,
            updatedStatus.Repository.HeadCommitSha,
            updatedStatus.Repository.HeadContentHash,
            updatedStatus.CurrentContentHash ?? string.Empty);
        historyEvents.PublishReviewStateChanged(projectId);
        return new ProjectVersionReviewBlockMutationResult([block.Id], updatedToken);
    }

    private static ManuscriptDocument SynthesizeSelectedBlockApproval(
        ManuscriptDocument approved,
        ManuscriptDocument current,
        IReadOnlySet<string> selectedBlockIds,
        Guid chapterId)
    {
        var approvedById = approved.Content.ToDictionary(block => block.Id, StringComparer.Ordinal);
        var currentById = current.Content.ToDictionary(block => block.Id, StringComparer.Ordinal);
        var knownIds = approvedById.Keys.Concat(currentById.Keys).ToHashSet(StringComparer.Ordinal);
        var unknown = selectedBlockIds.Except(knownIds, StringComparer.Ordinal).FirstOrDefault();
        if (unknown is not null)
            throw new ArgumentException($"Manuscript block '{unknown}' is not part of this review.", nameof(selectedBlockIds));

        // Remove every selected baseline block first, then reinsert every
        // selected live block in live order. This preserves selected moves and
        // insertions while leaving unselected baseline blocks untouched.
        var merged = approved.Content
            .Where(block => !selectedBlockIds.Contains(block.Id))
            .ToList();

        foreach (var (block, currentIndex) in current.Content.Select((block, index) => (block, index)))
        {
            if (!selectedBlockIds.Contains(block.Id))
                continue;

            var previousId = current.Content
                .Take(currentIndex)
                .Reverse()
                .Select(item => item.Id)
                .FirstOrDefault(id => merged.Any(item => item.Id == id));
            var nextId = current.Content
                .Skip(currentIndex + 1)
                .Select(item => item.Id)
                .FirstOrDefault(id => merged.Any(item => item.Id == id));
            var insertionIndex = previousId is not null
                ? merged.FindIndex(item => item.Id == previousId) + 1
                : nextId is not null
                    ? merged.FindIndex(item => item.Id == nextId)
                    : merged.Count;
            merged.Insert(Math.Max(0, insertionIndex), block);
        }

        var result = approved with
        {
            ManuscriptId = chapterId,
            Revision = current.Revision,
            Content = merged,
        };
        ManuscriptCodec.Validate(result, chapterId, result.Revision);
        return result;
    }

    private static void UpdateSynthesizedReviewTree(
        SortedDictionary<string, byte[]> files,
        VersionHistorySnapshotPayload approved,
        VersionHistorySnapshotPayload current,
        ProjectVersionReviewTarget target,
        ManuscriptDocument synthesized)
    {
        var manuscriptJson = ManuscriptCodec.Serialize(synthesized);
        if (target.ContentTarget.IsCore)
        {
            var chapter = approved.Narrative.Chapters
                .Single(item => item.Id == target.ChapterId)
                with
                {
                    ManuscriptJson = manuscriptJson,
                    ManuscriptRevision = synthesized.Revision,
                    Body = null,
                };
            var chapterPath = $"narrative/chapters/{target.ChapterId:N}";
            files[$"{chapterPath}/chapter.json"] = VersionHistoryCanonicalJson.Serialize(
                VersionHistorySnapshotChapter.FromProjectExportChapter(chapter));
            files[$"{chapterPath}/manuscript.json"] = VersionHistoryCanonicalJson.SerializeDirectManuscript(manuscriptJson);
            return;
        }

        var editionId = target.ContentTarget.EditionId
            ?? throw new InvalidOperationException("An edition review target requires an edition ID.");
        var approvedEdition = approved.Publication.PublicationEditions
            .Single(item => item.Id == editionId);
        var currentEdition = current.Publication.PublicationEditions
            .SingleOrDefault(item => item.Id == editionId);
        var existingOverride = approvedEdition.ChapterOverrides
            .SingleOrDefault(item => item.ChapterId == target.ChapterId)
            ?? currentEdition?.ChapterOverrides.SingleOrDefault(item => item.ChapterId == target.ChapterId);
        var coreChapter = approved.Narrative.Chapters.Single(item => item.Id == target.ChapterId);
        var now = DateTime.UtcNow;
        var chapterOverride = existingOverride ?? new ProjectExportEditionChapterOverride(
            Guid.NewGuid(),
            target.ChapterId,
            manuscriptJson,
            synthesized.Revision,
            coreChapter.ManuscriptRevision,
            ManuscriptCodec.HashPlainText(ManuscriptCodec.ProjectPlainText(
                ManuscriptCodec.Deserialize(coreChapter.ManuscriptJson, coreChapter.Id, coreChapter.ManuscriptRevision))),
            now,
            now);
        chapterOverride = chapterOverride with
        {
            ManuscriptJson = manuscriptJson,
            Revision = synthesized.Revision,
        };
        var overrides = approvedEdition.ChapterOverrides
            .Where(item => item.ChapterId != target.ChapterId)
            .Append(chapterOverride)
            .OrderBy(item => item.ChapterId)
            .ThenBy(item => item.Id)
            .ToList();
        var edition = approvedEdition with { ChapterOverrides = overrides };
        var editions = approved.Publication.PublicationEditions
            .Where(item => item.Id != editionId)
            .Append(edition)
            .OrderBy(item => item.Id)
            .ToList();
        var publication = approved.Publication with { PublicationEditions = editions };
        files["publication/publication.json"] = VersionHistoryCanonicalJson.Serialize(publication);
    }

    private static void UpdateSynthesizedEditionTargetReviewTree(
        SortedDictionary<string, byte[]> files,
        VersionHistorySnapshotPayload approved,
        VersionHistorySnapshotPayload current,
        ProjectVersionReviewTarget target)
    {
        var editionId = target.ContentTarget.EditionId
            ?? throw new InvalidOperationException("An edition review target requires an edition ID.");
        var approvedEdition = approved.Publication.PublicationEditions
            .Single(item => item.Id == editionId);
        var currentEdition = current.Publication.PublicationEditions
            .Single(item => item.Id == editionId);
        var currentOverride = currentEdition.ChapterOverrides
            .SingleOrDefault(item => item.ChapterId == target.ChapterId);
        var overrides = approvedEdition.ChapterOverrides
            .Where(item => item.ChapterId != target.ChapterId)
            .Concat(currentOverride is null ? [] : [currentOverride])
            .OrderBy(item => item.ChapterId)
            .ThenBy(item => item.Id)
            .ToList();
        var edition = approvedEdition with { ChapterOverrides = overrides };
        var editions = approved.Publication.PublicationEditions
            .Where(item => item.Id != editionId)
            .Append(edition)
            .OrderBy(item => item.Id)
            .ToList();
        files["publication/publication.json"] = VersionHistoryCanonicalJson.Serialize(
            approved.Publication with { PublicationEditions = editions });
    }

    private static async Task RebuildSnapshotManifestAsync(
        string rootDirectory,
        Guid repositoryId,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var manifestPath = ResolveSnapshotPath(rootDirectory, VersionHistorySnapshotContract.ManifestFileName);
        if (File.Exists(manifestPath))
            File.Delete(manifestPath);

        var descriptors = new List<ProjectArchiveFileDescriptor>();
        var root = Path.GetFullPath(rootDirectory);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = GetSnapshotRelativePath(root, file);
            descriptors.Add(await ProjectArchiveFileDescriptor.CreateAsync(
                relative,
                "version-history-snapshot",
                relative.EndsWith(".json", StringComparison.Ordinal) ? "application/json" : "application/octet-stream",
                file,
                cancellationToken));
        }

        var contentHash = await ComputeContentHashAsync(descriptors, cancellationToken);
        var entries = descriptors.Select(item => new VersionHistorySnapshotFile(item.ArchivePath, item.Length, item.Sha256)).ToList();
        var withoutHash = new VersionHistorySnapshotManifest(
            VersionHistorySnapshotContract.FormatId,
            VersionHistorySnapshotContract.SchemaVersion,
            repositoryId,
            projectId,
            VersionHistorySnapshotContract.IncludedAreas,
            contentHash,
            string.Empty,
            entries);
        var manifestHash = VersionHistoryCanonicalJson.Sha256Hex(
            VersionHistoryCanonicalJson.Serialize(withoutHash));
        await File.WriteAllBytesAsync(manifestPath, VersionHistoryCanonicalJson.Serialize(
            withoutHash with { ManifestHash = manifestHash }), cancellationToken);
    }

    private static void WriteSnapshotJsonFiles(string rootDirectory, IReadOnlyDictionary<string, byte[]> files)
    {
        foreach (var (path, bytes) in files.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (!path.EndsWith(".json", StringComparison.Ordinal))
                throw new InvalidOperationException("Review synthesis may rewrite only JSON snapshot files.");
            var destination = ResolveSnapshotPath(rootDirectory, path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, bytes);
        }
    }

    private static string ResolveSnapshotPath(string rootDirectory, string relativePath)
    {
        var normalized = ProjectArchivePath.Normalize(relativePath);
        var root = Path.GetFullPath(rootDirectory);
        var candidate = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Snapshot path escaped its temporary root: {relativePath}");
        return candidate;
    }

    private static async Task<string> ComputeContentHashAsync(
        IReadOnlyList<ProjectArchiveFileDescriptor> files,
        CancellationToken cancellationToken)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[81_920];
        foreach (var file in files.OrderBy(item => item.ArchivePath, StringComparer.Ordinal))
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(file.ArchivePath));
            hash.AppendData([0]);
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            hash.AppendData([0]);
            await using var source = file.OpenRead();
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0)
                    break;
                hash.AppendData(buffer, 0, read);
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static bool IsManuscriptSnapshotPath(string path) =>
        path.StartsWith("narrative/chapters/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Approving Other changes may advance chapter metadata without approving
    /// the live manuscript. Keep each existing chapter's current metadata but
    /// retain the approved manuscript revision/body so manuscript changes stay
    /// pending. Added and removed chapters are deliberately left untouched;
    /// they remain chapter-scoped review work because their manuscript cannot
    /// be split from their identity safely.
    /// </summary>
    private static void SynthesizeOtherReviewChapterMetadata(string approvedRoot, string currentRoot)
    {
        foreach (var currentPath in Directory.EnumerateFiles(currentRoot, "chapter.json", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var path = GetSnapshotRelativePath(currentRoot, currentPath);
            if (!path.StartsWith("narrative/chapters/", StringComparison.Ordinal)
                || !path.EndsWith("/chapter.json", StringComparison.Ordinal)
                || !File.Exists(ResolveSnapshotPath(approvedRoot, path)))
                continue;

            var currentChapter = VersionHistoryCanonicalJson.Deserialize<VersionHistorySnapshotChapter>(File.ReadAllBytes(currentPath));
            var approvedChapter = VersionHistoryCanonicalJson.Deserialize<VersionHistorySnapshotChapter>(
                File.ReadAllBytes(ResolveSnapshotPath(approvedRoot, path)));
            File.WriteAllBytes(ResolveSnapshotPath(approvedRoot, path), VersionHistoryCanonicalJson.Serialize(
                currentChapter with
                {
                    ManuscriptRevision = approvedChapter.ManuscriptRevision,
                    Body = null,
                }));
        }
    }

    private static async Task CopyCurrentOtherFilesAsync(string currentRoot, string approvedRoot, CancellationToken cancellationToken)
    {
        foreach (var source in Directory.EnumerateFiles(currentRoot, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = GetSnapshotRelativePath(currentRoot, source);
            if (IsManuscriptSnapshotPath(path)
                || path.Equals("composition/composition.json", StringComparison.Ordinal)
                || path.Equals(VersionHistorySnapshotContract.ManifestFileName, StringComparison.Ordinal))
                continue;

            var destination = ResolveSnapshotPath(approvedRoot, path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81_920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, 81_920, cancellationToken);
        }
    }

    private static VersionHistorySnapshotPublicationArea SynthesizeOtherReviewPublication(
        VersionHistorySnapshotPublicationArea approved,
        VersionHistorySnapshotPublicationArea current)
    {
        var approvedEditions = approved.PublicationEditions.ToDictionary(item => item.Id);
        var editions = current.PublicationEditions
            .Select(edition => approvedEditions.TryGetValue(edition.Id, out var approvedEdition)
                ? edition with { ChapterOverrides = approvedEdition.ChapterOverrides.ToList() }
                : edition with { ChapterOverrides = [] })
            .ToList();
        return current with { PublicationEditions = editions };
    }

    /// <summary>
    /// Reads and validates status while the caller owns the project mutation
    /// lease. Sync uses this path so its cleanliness check and push remain one
    /// serialized operation without attempting to reacquire the same lease.
    /// </summary>
    internal async Task<ProjectVersionStatusView?> GetStatusUnderLeaseAsync(
        Guid projectId,
        bool includeCurrentSnapshotHash,
        CancellationToken cancellationToken)
    {
        ValidateProjectId(projectId);
        ProjectVersionRepository? repository;
        await using (var read = await database.OpenReadAsync(cancellationToken))
        {
            repository = await read.Db.ProjectVersionRepositories
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken);
        }
        if (repository is null)
            return null;

        string? currentContentHash = null;
        bool? semanticIsDirty = null;
        string? diagnostic = null;
        var health = ProjectVersionRepositoryHealthFromCache(repository);
        var head = ReadExistingHead(repository, out var headDiagnostic);
        diagnostic = headDiagnostic;

        if (head?.Commit is not null)
        {
            try
            {
                var loaded = LoadGitCheckpoint(repository.Id, head.Commit.Sha, null, projectId, cancellationToken);
                if (repository.HeadCommitSha is not null
                    && string.Equals(repository.HeadCommitSha, head.Commit.Sha, StringComparison.Ordinal)
                    && repository.HeadContentHash is not null
                    && !string.Equals(repository.HeadContentHash, loaded.Manifest.ContentHash, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The cached head content hash does not match the Git manifest.");
                }
                health = repository.HeadCommitSha is not null
                    && !string.Equals(repository.HeadCommitSha, head.Commit.Sha, StringComparison.Ordinal)
                    ? ProjectVersionRepositoryHealth.Diverged
                    : ProjectVersionRepositoryHealth.Healthy;
                if (includeCurrentSnapshotHash)
                {
                    var current = await CaptureCurrentSnapshotUnderLeaseAsync(
                        repository.Id,
                        projectId,
                        cancellationToken);
                    currentContentHash = current.Manifest.ContentHash;
                    semanticIsDirty = !snapshotComparer.Compare(loaded.Payload, current.Payload).IsIdentical;
                    if (semanticIsDirty == true)
                        health = ProjectVersionRepositoryHealth.Dirty;
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
            {
                health = ProjectVersionRepositoryHealth.Corrupt;
                diagnostic = exception.Message;
            }
        }
        else if (repository.HeadCommitSha is not null)
        {
            health = ProjectVersionRepositoryHealth.Corrupt;
            diagnostic ??= "The database has a committed head, but the local Git repository has no head.";
        }

        var view = ToRepositoryView(repository, health, diagnostic);
        var isDirty = semanticIsDirty == true;
        return new ProjectVersionStatusView(view with { IsDirty = isDirty }, currentContentHash);
    }

    /// <summary>
    /// Loads the approved head and a validated live snapshot without acquiring
    /// another project lease. Restore owns the lease while calling this method,
    /// which keeps review-token validation and the ensuing SQLite mutation in
    /// one serialized project operation.
    /// </summary>
    internal async Task<ProjectVersionReviewSnapshotContext?> LoadReviewSnapshotUnderLeaseAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var status = await GetStatusUnderLeaseAsync(
            projectId,
            includeCurrentSnapshotHash: false,
            cancellationToken);
        if (status is null)
            return null;

        EnsureReviewRepositoryUsable(status.Repository);
        if (string.IsNullOrWhiteSpace(status.Repository.HeadCommitSha))
            throw new InvalidOperationException("Review restore requires an approved Git head.");

        var temporaryDirectory = CreateTemporaryDirectory();
        ProjectVersionLoadedCheckpointLease? approvedLease = null;
        try
        {
            await snapshotWriter.WriteAsync(status.Repository.RepositoryId, projectId, temporaryDirectory, cancellationToken);
            EnsureNoReparsePointsRecursively(temporaryDirectory);
            var current = snapshotReader.Read(temporaryDirectory, status.Repository.RepositoryId, projectId,
                new VersionHistorySnapshotReadOptions { IncludeSourceOriginalBlobs = true, CancellationToken = cancellationToken });
            approvedLease = LoadRestoreGitCheckpoint(status.Repository.RepositoryId, status.Repository.HeadCommitSha,
                recordedCheckpoint: null, projectId, cancellationToken);
            var approved = approvedLease.Checkpoint;
            var comparison = snapshotComparer.Compare(approved.Payload, current.Payload);
            status = status with
            {
                CurrentContentHash = current.Manifest.ContentHash,
                Repository = status.Repository with { IsDirty = !comparison.IsIdentical },
            };
            var recordedCheckpoint = await LoadRecordedCheckpointUnderLeaseAsync(
                status.Repository.RepositoryId, approved.Commit.Sha, cancellationToken);
            var retainedLease = approvedLease;
            return new(status, current,
                approved with { RecordedCheckpoint = recordedCheckpoint is null ? null : ToCheckpointView(recordedCheckpoint) },
                () =>
                {
                    retainedLease.Dispose();
                    CleanupTemporaryDirectory(temporaryDirectory);
                });
        }
        catch
        {
            approvedLease?.Dispose();
            CleanupTemporaryDirectory(temporaryDirectory);
            throw;
        }
    }

    /// <summary>Loads a restore snapshot under the caller's project mutation lease.</summary>
    internal ProjectVersionLoadedCheckpointLease LoadCheckpointUnderLease(
        Guid repositoryId,
        string commitSha,
        Guid projectId,
        CancellationToken cancellationToken) =>
        LoadRestoreGitCheckpoint(repositoryId, commitSha, recordedCheckpoint: null, projectId, cancellationToken);

    public async Task<ProjectVersionLoadedCheckpoint> LoadCheckpointAsync(
        Guid projectId,
        string commitSha,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        if (string.IsNullOrWhiteSpace(commitSha))
            throw new ArgumentException("A checkpoint commit SHA is required.", nameof(commitSha));

        await using var projectLease = await projectMutations.AcquireAsync(projectId, cancellationToken);
        ProjectVersionRepository repository;
        ProjectVersionCheckpoint? recorded;
        await using (var read = await database.OpenReadAsync(cancellationToken))
        {
            repository = await read.Db.ProjectVersionRepositories
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken)
                ?? throw new InvalidOperationException($"Project {projectId} has no version-history repository.");
            recorded = await read.Db.ProjectVersionCheckpoints
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.ProjectVersionRepositoryId == repository.Id && item.CommitSha == commitSha,
                    cancellationToken);
        }

        var loaded = LoadGitCheckpoint(repository.Id, commitSha, recorded, projectId, cancellationToken);
        return new ProjectVersionLoadedCheckpoint(
            loaded.Commit,
            loaded.Manifest,
            loaded.Payload,
            recorded is null ? null : ToCheckpointView(recorded));
    }

    public async Task<ProjectVersionLoadedCheckpointLease> LoadCheckpointForRestoreAsync(
        Guid projectId,
        string commitSha,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        if (string.IsNullOrWhiteSpace(commitSha))
            throw new ArgumentException("A checkpoint commit SHA is required.", nameof(commitSha));

        await using var projectLease = await projectMutations.AcquireAsync(projectId, cancellationToken);
        ProjectVersionRepository repository;
        ProjectVersionCheckpoint? recorded;
        await using (var read = await database.OpenReadAsync(cancellationToken))
        {
            repository = await read.Db.ProjectVersionRepositories
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken)
                ?? throw new InvalidOperationException($"Project {projectId} has no version-history repository.");
            recorded = await read.Db.ProjectVersionCheckpoints
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.ProjectVersionRepositoryId == repository.Id && item.CommitSha == commitSha,
                    cancellationToken);
        }

        return LoadRestoreGitCheckpoint(repository.Id, commitSha, recorded, projectId, cancellationToken);
    }

    public async Task<ProjectVersionLoadedCheckpoint> LoadCheckpointForComparisonAsync(
        Guid projectId,
        string commitSha,
        CancellationToken cancellationToken = default,
        Guid? unboundedSourceId = null)
    {
        ValidateProjectId(projectId);
        if (string.IsNullOrWhiteSpace(commitSha))
            throw new ArgumentException("A checkpoint commit SHA is required.", nameof(commitSha));

        await using var projectLease = await projectMutations.AcquireAsync(projectId, cancellationToken);
        ProjectVersionRepository repository;
        ProjectVersionCheckpoint? recorded;
        await using (var read = await database.OpenReadAsync(cancellationToken))
        {
            repository = await read.Db.ProjectVersionRepositories
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken)
                ?? throw new InvalidOperationException($"Project {projectId} has no version-history repository.");
            recorded = await read.Db.ProjectVersionCheckpoints
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.ProjectVersionRepositoryId == repository.Id && item.CommitSha == commitSha,
                    cancellationToken);
        }

        var loaded = LoadGitCheckpoint(repository.Id, commitSha, recorded, projectId, cancellationToken, unboundedSourceId);
        return new ProjectVersionLoadedCheckpoint(
            loaded.Commit,
            loaded.Manifest,
            loaded.Payload,
            recorded is null ? null : ToCheckpointView(recorded));
    }

    private async Task<ProjectVersionRepository> EnsureRepositoryUnderLeaseAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var db = operation.Db;
        if (!await db.Projects.AnyAsync(item => item.Id == projectId, cancellationToken))
            throw new InvalidOperationException($"Project {projectId} was not found.");

        var repository = await db.ProjectVersionRepositories
            .SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken);
        if (repository is null)
        {
            repository = new ProjectVersionRepository
            {
                ProjectId = projectId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.ProjectVersionRepositories.Add(repository);
            await operation.SaveChangesAsync(cancellationToken);
        }

        return repository;
    }

    private async Task<Guid> StartOperationAsync(
        Guid repositoryId,
        ProjectVersionOperationKind kind,
        string? requestKey,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var db = operation.Db;
        if (requestKey is not null && await db.ProjectVersionOperations.AnyAsync(
                item => item.ProjectVersionRepositoryId == repositoryId && item.RequestKey == requestKey,
                cancellationToken))
        {
            throw new InvalidOperationException($"The version-history request key has already been used: {requestKey}");
        }
        var repository = await db.ProjectVersionRepositories
            .SingleAsync(item => item.Id == repositoryId, cancellationToken);

        var now = DateTime.UtcNow;
        var row = new ProjectVersionOperation
        {
            ProjectVersionRepositoryId = repositoryId,
            Repository = repository,
            Kind = kind,
            Status = ProjectVersionOperationStatus.Running,
            RequestKey = requestKey,
            IsResumable = true,
            AttemptCount = 1,
            StartedAt = now,
            HeartbeatAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.ProjectVersionOperations.Add(row);
        await operation.SaveChangesAsync(cancellationToken);
        return row.Id;
    }

    private async Task<ProjectVersionCheckpointView> PersistCheckpointAndCompleteAsync(
        Guid repositoryId,
        Guid operationId,
        GitCommitMetadata commit,
        VersionHistorySnapshotManifest manifest,
        ProjectVersionCheckpointKind kind,
        string message,
        DateTimeOffset authoredAt,
        bool createdCommit,
        CancellationToken cancellationToken,
        Guid? projectId = null,
        bool disableReviewEdits = false)
    {
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var db = operation.Db;
        var repository = await db.ProjectVersionRepositories
            .SingleAsync(item => item.Id == repositoryId, cancellationToken);
        var checkpoint = await db.ProjectVersionCheckpoints
            .Where(item => item.ProjectVersionRepositoryId == repositoryId)
            .Where(item => item.ContentHash == manifest.ContentHash)
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (checkpoint is null)
        {
            if (createdCommit || repository.HeadCommitSha is null)
                repository.CreativeRevision++;
            checkpoint = new ProjectVersionCheckpoint
            {
                ProjectVersionRepositoryId = repositoryId,
                ManifestSchemaVersion = manifest.SchemaVersion,
                CreativeRevision = repository.CreativeRevision,
                ContentHash = manifest.ContentHash,
                ManifestHash = manifest.ManifestHash,
                CommitSha = commit.Sha,
                ParentCommitSha = commit.ParentShas.FirstOrDefault(),
                Kind = kind,
                Source = ProjectVersionCheckpointSource.Local,
                Message = message,
                CreatedAt = authoredAt.UtcDateTime,
            };
            db.ProjectVersionCheckpoints.Add(checkpoint);
        }
        else
        {
            checkpoint.CommitSha ??= commit.Sha;
            checkpoint.ParentCommitSha ??= commit.ParentShas.FirstOrDefault();
        }

        repository.HeadCommitSha = commit.Sha;
        repository.HeadContentHash = manifest.ContentHash;
        repository.LastCheckpointRevision = checkpoint.CreativeRevision;
        repository.LastCheckpointContentHash = checkpoint.ContentHash;
        repository.LastCheckpointAt = checkpoint.CreatedAt;
        repository.UpdatedAt = DateTime.UtcNow;

        var journal = await db.ProjectVersionOperations
            .SingleAsync(item => item.Id == operationId, cancellationToken);
        journal.Status = ProjectVersionOperationStatus.Succeeded;
        journal.CompletedAt = DateTime.UtcNow;
        journal.HeartbeatAt = journal.CompletedAt;
        journal.UpdatedAt = journal.CompletedAt.Value;
        journal.ErrorCode = null;
        journal.ErrorMessage = null;

        if (disableReviewEdits)
        {
            if (projectId is not Guid projectIdValue || projectIdValue == Guid.Empty)
                throw new InvalidOperationException("Disabling Review Edits requires a project identity.");

            var project = await db.Projects
                .SingleOrDefaultAsync(item => item.Id == projectIdValue, cancellationToken)
                ?? throw new InvalidOperationException($"Project {projectIdValue} not found.");
            project.ReviewEditsEnabled = false;
            project.UpdatedAt = DateTime.UtcNow;
        }

        await AddPendingAutoPushIntentsAsync(
            db,
            repositoryId,
            commit.Sha,
            DateTime.UtcNow,
            cancellationToken);

        await operation.SaveChangesAsync(cancellationToken);
        autoPushQueue.Signal();
        return ToCheckpointView(checkpoint);
    }

    private static async Task AddPendingAutoPushIntentsAsync(
        AppDbContext db,
        Guid repositoryId,
        string targetCommitSha,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var remotes = await db.ProjectGitRemotes
            .Where(remote => remote.ProjectVersionRepositoryId == repositoryId)
            .ToListAsync(cancellationToken);
        foreach (var remote in remotes)
            await AddPendingAutoPushIntentAsync(
                db,
                repositoryId,
                remote,
                targetCommitSha,
                now,
                cancellationToken);
    }

    internal static async Task AddPendingAutoPushIntentAsync(
        AppDbContext db,
        Guid repositoryId,
        ProjectGitRemote remote,
        string? targetCommitSha,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(targetCommitSha))
            return;

        var existing = await db.ProjectVersionOperations
            .SingleOrDefaultAsync(
            operation => operation.ProjectVersionRepositoryId == repositoryId
                && operation.ProjectGitRemoteId == remote.Id
                && operation.TargetCommitSha == targetCommitSha,
            cancellationToken);
        if (existing is not null)
        {
            if (existing.Status is ProjectVersionOperationStatus.Failed or ProjectVersionOperationStatus.Canceled)
            {
                existing.Status = ProjectVersionOperationStatus.Pending;
                existing.IsResumable = true;
                existing.ErrorCode = null;
                existing.ErrorMessage = null;
                existing.StartedAt = null;
                existing.HeartbeatAt = null;
                existing.CompletedAt = null;
                existing.UpdatedAt = now;
            }
            return;
        }

        db.ProjectVersionOperations.Add(new ProjectVersionOperation
        {
            ProjectVersionRepositoryId = repositoryId,
            ProjectGitRemoteId = remote.Id,
            Kind = ProjectVersionOperationKind.AutoPush,
            Status = ProjectVersionOperationStatus.Pending,
            RequestKey = $"auto-push:{remote.Id:N}:{targetCommitSha}",
            TargetCommitSha = targetCommitSha,
            IsResumable = true,
            AttemptCount = 0,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    private async Task TryFailOperationAsync(
        Guid operationId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var operation = await database.OpenWriteAsync(cancellationToken);
            var journal = await operation.Db.ProjectVersionOperations
                .SingleOrDefaultAsync(item => item.Id == operationId, cancellationToken);
            if (journal is null || journal.Status != ProjectVersionOperationStatus.Running)
                return;

            journal.Status = ProjectVersionOperationStatus.Failed;
            journal.IsResumable = true;
            journal.ErrorCode = exception.GetType().Name;
            journal.ErrorMessage = exception.Message.Length <= 500
                ? exception.Message
                : exception.Message[..500];
            journal.CompletedAt = DateTime.UtcNow;
            journal.HeartbeatAt = journal.CompletedAt;
            journal.UpdatedAt = journal.CompletedAt.Value;
            await operation.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // The original operation failure is the useful error. A second
            // failure must not hide it or cause an unbounded recovery loop.
        }
    }

    private async Task TryCancelOperationAsync(Guid operationId)
    {
        try
        {
            await using var operation = await database.OpenWriteAsync(CancellationToken.None);
            var journal = await operation.Db.ProjectVersionOperations
                .SingleOrDefaultAsync(item => item.Id == operationId, CancellationToken.None);
            if (journal is null || journal.Status != ProjectVersionOperationStatus.Running)
                return;

            var now = DateTime.UtcNow;
            journal.Status = ProjectVersionOperationStatus.Canceled;
            journal.IsResumable = true;
            journal.ErrorCode = nameof(OperationCanceledException);
            journal.ErrorMessage = "The checkpoint operation was canceled.";
            journal.CompletedAt = now;
            journal.HeartbeatAt = now;
            journal.UpdatedAt = now;
            await operation.SaveChangesAsync(CancellationToken.None);
        }
        catch
        {
            // Cancellation must still reach the caller even if journal repair
            // cannot acquire the database during shutdown.
        }
    }

    private async Task<VersionHistorySnapshotArtifact> CaptureCurrentSnapshotUnderLeaseAsync(
        Guid repositoryId,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var temporaryDirectory = CreateTemporaryDirectory();
        try
        {
            var validated = await snapshotWriter.WriteAsync(
                repositoryId,
                projectId,
                temporaryDirectory,
                cancellationToken);
            EnsureNoReparsePointsRecursively(temporaryDirectory);
            return validated;
        }
        finally
        {
            CleanupTemporaryDirectory(temporaryDirectory);
        }
    }

    private async Task<ProjectVersionCheckpoint?> LoadRecordedCheckpointUnderLeaseAsync(
        Guid repositoryId,
        string commitSha,
        CancellationToken cancellationToken)
    {
        await using var read = await database.OpenReadAsync(cancellationToken);
        return await read.Db.ProjectVersionCheckpoints
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.ProjectVersionRepositoryId == repositoryId && item.CommitSha == commitSha,
                cancellationToken);
    }

    private static IReadOnlyList<ProjectVersionReviewTarget> BuildDefaultReviewTargets(
        VersionHistorySnapshotPayload baseline,
        VersionHistorySnapshotPayload candidate)
    {
        var baselineChapters = baseline.Narrative.Chapters.ToDictionary(chapter => chapter.Id);
        var candidateChapters = candidate.Narrative.Chapters.ToDictionary(chapter => chapter.Id);
        var targets = baselineChapters.Keys
            .Concat(candidateChapters.Keys)
            .Distinct()
            .OrderBy(id => id)
            .Where(id => !baselineChapters.TryGetValue(id, out var before)
                || !candidateChapters.TryGetValue(id, out var after)
                || !ProjectVersionReviewChapter.ManuscriptSemanticallyEquals(before, after))
            .Select(id => new ProjectVersionReviewTarget(id, EditorContentTarget.Core))
            .ToList();

        var baselineEditions = baseline.Publication.PublicationEditions.ToDictionary(item => item.Id);
        var candidateEditions = candidate.Publication.PublicationEditions.ToDictionary(item => item.Id);
        foreach (var editionId in baselineEditions.Keys.Concat(candidateEditions.Keys).Distinct().OrderBy(id => id))
        {
            var baselineEdition = baselineEditions.GetValueOrDefault(editionId);
            var candidateEdition = candidateEditions.GetValueOrDefault(editionId);
            var chapterIds = (baselineEdition?.ChapterOverrides ?? [])
                .Select(item => item.ChapterId)
                .Concat((candidateEdition?.ChapterOverrides ?? []).Select(item => item.ChapterId))
                .Distinct()
                .OrderBy(id => id);
            foreach (var chapterId in chapterIds)
            {
                var target = new ProjectVersionReviewTarget(
                    chapterId,
                    EditorContentTarget.ForEdition(editionId));
                var before = FindEffectiveChapter(baseline, target);
                var after = FindEffectiveChapter(candidate, target);
                if (!ProjectVersionReviewChapter.ManuscriptSemanticallyEquals(before, after))
                    targets.Add(target);
            }
        }

        return targets;
    }

    private static IReadOnlyList<ProjectVersionReviewDependencyGroup> BuildReviewDependencyGroups(
        VersionHistorySnapshotComparison comparison)
    {
        var changed = comparison.Areas
            .SelectMany(area => area.Entries.Select(entry => (area.Area, Entry: entry)))
            .GroupBy(item => item.Area.Equals("narrative", StringComparison.Ordinal)
                && item.Entry.Category.Equals("chapters", StringComparison.OrdinalIgnoreCase)
                ? $"narrative/chapters/{item.Entry.Key}"
                : $"{item.Area}/{item.Entry.Category}",
                StringComparer.Ordinal);

        return changed
            .Select(group =>
            {
                var entries = group
                    .Select(item => item.Entry)
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .ToList();
                var areas = group.Select(item => item.Area).Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToList();
                var categories = group.Select(item => item.Entry.Category).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList();
                var manuscriptScoped = group.All(item =>
                    item.Area.Equals("narrative", StringComparison.Ordinal)
                    && item.Entry.Category.Equals("chapters", StringComparison.OrdinalIgnoreCase)
                    && item.Entry.ManuscriptChanged
                    && !item.Entry.MetadataChanged);
                var label = entries.Count == 1
                    ? entries[0].Label
                    : $"{DisplayReviewArea(group.Key)} changes";
                return new ProjectVersionReviewDependencyGroup(
                    group.Key,
                    label,
                    IsAtomic: !manuscriptScoped,
                    IsManuscriptScoped: manuscriptScoped,
                    areas,
                    categories,
                    entries);
            })
            .OrderBy(group => group.IsManuscriptScoped ? 0 : 1)
            .ThenBy(group => group.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string DisplayReviewArea(string key)
    {
        var slash = key.IndexOf('/');
        var area = slash < 0 ? key : key[..slash];
        return area switch
        {
            "project" => "Project",
            "narrative" => "Narrative",
            "graph" => "Graph",
            "sources" => "Sources",
            "assets" => "Assets",
            "manuscript" => "Manuscript styles",
            "composition" => "Composition",
            "publication" => "Publication",
            _ => area,
        };
    }

    private static IReadOnlyList<ProjectVersionReviewTarget> NormalizeReviewTargets(
        IReadOnlyCollection<ProjectVersionReviewTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var normalized = new List<ProjectVersionReviewTarget>(targets.Count);
        var seen = new HashSet<(Guid ChapterId, string TargetKey)>();
        foreach (var target in targets)
        {
            if (target.ChapterId == Guid.Empty)
                throw new ArgumentException("A review target must identify a chapter.", nameof(targets));
            ValidateReviewTarget(target.ContentTarget);
            if (seen.Add((target.ChapterId, target.ContentTarget.StorageKey)))
                normalized.Add(target);
        }

        return normalized;
    }

    private static ProjectVersionReviewChapter? BuildReviewChapter(
        VersionHistorySnapshotPayload baseline,
        VersionHistorySnapshotPayload candidate,
        ProjectVersionReviewTarget target,
        ProjectVersionReviewConcurrencyToken token)
    {
        var before = FindEffectiveChapter(baseline, target);
        var after = FindEffectiveChapter(candidate, target);
        if (before is null && after is null)
            return null;

        return new ProjectVersionReviewChapter(
            target.ChapterId,
            target.ContentTarget,
            before,
            after,
            token);
    }

    private static IReadOnlyList<ProjectVersionReviewDesignedPage> FindReviewDesignedPages(
        VersionHistorySnapshotPayload baseline,
        VersionHistorySnapshotPayload candidate)
    {
        var before = baseline.Composition.DesignedPages.ToDictionary(item => item.Id);
        var after = candidate.Composition.DesignedPages.ToDictionary(item => item.Id);
        return before.Keys
            .Concat(after.Keys)
            .Distinct()
            .OrderBy(id => id)
            .Select(id => new ProjectVersionReviewDesignedPage(
                id,
                before.GetValueOrDefault(id),
                after.GetValueOrDefault(id),
                FindDesignedPagePlacementLinks(baseline, candidate, id)))
            .Where(page => page.HasChanges)
            .ToList();
    }

    private static IReadOnlyList<ProjectVersionReviewDesignedPagePlacement> FindDesignedPagePlacementLinks(
        VersionHistorySnapshotPayload baseline,
        VersionHistorySnapshotPayload candidate,
        Guid designedPageId)
    {
        var links = new Dictionary<(Guid ChapterId, string TargetKey, string BlockId), ProjectVersionReviewDesignedPagePlacement>();
        foreach (var payload in new[] { baseline, candidate })
        {
            var chapterIds = payload.Narrative.Chapters.Select(chapter => chapter.Id)
                .Concat(payload.Publication.PublicationEditions.SelectMany(edition => edition.ChapterOverrides.Select(overrideItem => overrideItem.ChapterId)))
                .Distinct();
            var targets = chapterIds.Select(chapterId => new ProjectVersionReviewTarget(chapterId, EditorContentTarget.Core))
                .Concat(payload.Publication.PublicationEditions.SelectMany(edition => chapterIds.Select(chapterId =>
                    new ProjectVersionReviewTarget(chapterId, EditorContentTarget.ForEdition(edition.Id)))));
            foreach (var target in targets)
            {
                var chapter = FindEffectiveChapter(payload, target);
                if (chapter is null)
                    continue;
                var document = ManuscriptCodec.Deserialize(chapter.ManuscriptJson, chapter.Id, chapter.ManuscriptRevision);
                foreach (var block in document.Content.Where(block =>
                    block.Type == ManuscriptBlockType.DesignedPage && block.DesignedPageId == designedPageId))
                {
                    var placement = new ProjectVersionReviewDesignedPagePlacement(target.ChapterId, target.ContentTarget, block.Id);
                    links[(placement.ChapterId, placement.ContentTarget.StorageKey, placement.BlockId)] = placement;
                }
            }
        }

        return links.Values
            .OrderBy(link => link.ChapterId)
            .ThenBy(link => link.ContentTarget.StorageKey, StringComparer.Ordinal)
            .ThenBy(link => link.BlockId, StringComparer.Ordinal)
            .ToList();
    }

    private static ProjectExportChapter? FindEffectiveChapter(
        VersionHistorySnapshotPayload payload,
        ProjectVersionReviewTarget target)
    {
        var chapter = payload.Narrative.Chapters
            .SingleOrDefault(item => item.Id == target.ChapterId);
        if (chapter is null || target.ContentTarget.IsCore)
            return chapter;

        var edition = payload.Publication.PublicationEditions
            .SingleOrDefault(item => item.Id == target.ContentTarget.EditionId);
        if (edition is null)
            return null;

        var chapterOverride = edition.ChapterOverrides
            .SingleOrDefault(item => item.ChapterId == target.ChapterId);
        return chapterOverride is null
            ? chapter
            : chapter with
            {
                ManuscriptJson = chapterOverride.ManuscriptJson,
                ManuscriptRevision = chapterOverride.Revision,
                Body = null,
            };
    }

    private static void ValidateReviewTarget(EditorContentTarget contentTarget)
    {
        if (!Enum.IsDefined(contentTarget.Kind))
        {
            throw new ArgumentException("The review content target is invalid.", nameof(contentTarget));
        }

        if (contentTarget.IsCore)
        {
            if (contentTarget.EditionId is not null)
                throw new ArgumentException("The Core review target cannot include an edition ID.", nameof(contentTarget));
            return;
        }

        if (contentTarget.EditionId is not Guid editionId || editionId == Guid.Empty)
            throw new ArgumentException("An edition review target requires an edition ID.", nameof(contentTarget));
    }

    private static void EnsureReviewRepositoryUsable(ProjectVersionRepositoryView repository)
    {
        if (repository.Health is ProjectVersionRepositoryHealth.Corrupt
            or ProjectVersionRepositoryHealth.Missing
            or ProjectVersionRepositoryHealth.Diverged)
        {
            throw new InvalidOperationException(
                repository.Diagnostic
                ?? "Version history is not in a usable state for review. Reconcile the repository before continuing.");
        }
    }

    private static void EnsureReviewTokenMatches(
        ProjectVersionStatusView status,
        ProjectVersionReviewConcurrencyToken expectedToken)
    {
        if (status.Repository.RepositoryId != expectedToken.RepositoryId
            || !string.Equals(status.Repository.HeadCommitSha, expectedToken.HeadCommitSha, StringComparison.Ordinal)
            || !string.Equals(status.Repository.HeadContentHash, expectedToken.HeadContentHash, StringComparison.Ordinal)
            || !string.Equals(status.CurrentContentHash, expectedToken.CurrentContentHash, StringComparison.Ordinal))
        {
            throw new ProjectVersionReviewConcurrencyException(
                "The project changed after this review was loaded. Reload the review before continuing.");
        }
    }

    private LoadedGitCheckpoint LoadGitCheckpoint(
        Guid repositoryId,
        string commitSha,
        ProjectVersionCheckpoint? recordedCheckpoint,
        Guid expectedProjectId,
        CancellationToken cancellationToken,
        Guid? unboundedSourceId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LoadedGitCheckpoint loaded;
        if (unboundedSourceId.HasValue || !reviewCache.TryGetGitCheckpoint(repositoryId, commitSha, out loaded!))
        {
            var commit = git.GetCommitMetadata(repositoryId, commitSha);
            var temporaryDirectory = CreateTemporaryDirectory();
            try
            {
                git.MaterializeTree(repositoryId, temporaryDirectory, commitSha, cancellationToken);
                EnsureNoReparsePointsRecursively(temporaryDirectory);
                var artifact = snapshotReader.Read(
                    temporaryDirectory,
                    repositoryId,
                    expectedProjectId,
                    new VersionHistorySnapshotReadOptions
                    {
                        IncludeAssetData = false,
                        IncludeSourceDetails = false,
                        UnboundedSourceReviewId = unboundedSourceId,
                        CancellationToken = cancellationToken,
                    });
                loaded = new LoadedGitCheckpoint(commit, artifact.Manifest, artifact.Payload);
            }
            finally
            {
                CleanupTemporaryDirectory(temporaryDirectory);
            }

            if (!unboundedSourceId.HasValue)
                reviewCache.SetGitCheckpoint(repositoryId, commitSha, loaded);
        }

        if (recordedCheckpoint is not null
            && !string.Equals(
                recordedCheckpoint.ContentHash,
                loaded.Manifest.ContentHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The recorded checkpoint content hash does not match its Git payload.");
        }

        return loaded;
    }

    private ProjectVersionLoadedCheckpointLease LoadRestoreGitCheckpoint(
        Guid repositoryId,
        string commitSha,
        ProjectVersionCheckpoint? recordedCheckpoint,
        Guid expectedProjectId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var commit = git.GetCommitMetadata(repositoryId, commitSha);
        var temporaryDirectory = CreateTemporaryDirectory();
        LoadedGitCheckpoint loaded;
        var completed = false;
        try
        {
            git.MaterializeTree(repositoryId, temporaryDirectory, commitSha, cancellationToken);
            EnsureNoReparsePointsRecursively(temporaryDirectory);
            var artifact = snapshotReader.Read(
                temporaryDirectory,
                repositoryId,
                expectedProjectId,
                new VersionHistorySnapshotReadOptions { IncludeSourceOriginalBlobs = true, CancellationToken = cancellationToken });
            loaded = new LoadedGitCheckpoint(commit, artifact.Manifest, artifact.Payload);

            if (recordedCheckpoint is not null
                && !string.Equals(
                    recordedCheckpoint.ContentHash,
                    loaded.Manifest.ContentHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("The recorded checkpoint content hash does not match its Git payload.");
            }

            var checkpoint = new ProjectVersionLoadedCheckpoint(
                loaded.Commit,
                loaded.Manifest,
                loaded.Payload,
                recordedCheckpoint is null ? null : ToCheckpointView(recordedCheckpoint));
            completed = true;
            return new ProjectVersionLoadedCheckpointLease(
                checkpoint,
                () => CleanupTemporaryDirectory(temporaryDirectory));
        }
        finally
        {
            if (!completed)
                CleanupTemporaryDirectory(temporaryDirectory);
        }
    }

    private GitHeadInfo? ReadExistingHead(ProjectVersionRepository repository)
    {
        var path = git.GetRepositoryPath(repository.Id);
        if (!Directory.Exists(path))
        {
            if (!string.IsNullOrWhiteSpace(repository.HeadCommitSha))
                throw new InvalidDataException("The local history repository is missing its committed head.");
            return null;
        }

        return git.GetHead(repository.Id);
    }

    private GitHeadInfo? ReadExistingHead(
        ProjectVersionRepository repository,
        out string? diagnostic)
    {
        diagnostic = null;
        try
        {
            return ReadExistingHead(repository);
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
        {
            diagnostic = exception.Message;
            return null;
        }
    }

    private static async Task<SnapshotDescriptorCapture> CaptureSnapshotFilesAsync(
        string rootDirectory,
        CancellationToken cancellationToken)
    {
        EnsureNoReparsePointsRecursively(rootDirectory);
        var root = Path.GetFullPath(rootDirectory);
        var capture = ProjectArchiveTemporaryCapture.Create(VersionHistoryTemporaryPaths.GetParentDirectory());
        try
        {
            var files = new List<ProjectArchiveFileDescriptor>();
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = GetSnapshotRelativePath(root, file);
                var info = new FileInfo(file);
                await using var source = new FileStream(
                    info.FullName,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    81_920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                files.Add(await capture.CaptureAsync(
                    source,
                    relative,
                    "version-history-snapshot",
                    relative.EndsWith(".json", StringComparison.Ordinal) ? "application/json" : "application/octet-stream",
                    long.MaxValue,
                    cancellationToken));
            }

            return new SnapshotDescriptorCapture(capture, files);
        }
        catch
        {
            await capture.DisposeAsync();
            throw;
        }
    }

    private static string GetSnapshotRelativePath(string rootDirectory, string file)
    {
        var root = Path.GetFullPath(rootDirectory);
        var full = Path.GetFullPath(file);
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
            || new FileInfo(full).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"Snapshot file escaped its temporary root: {file}");
        }

        return ProjectArchivePath.Normalize(Path.GetRelativePath(root, full).Replace('\\', '/'));
    }

    private sealed class SnapshotDescriptorCapture(
        ProjectArchiveTemporaryCapture capture,
        IReadOnlyList<ProjectArchiveFileDescriptor> files) : IAsyncDisposable
    {
        public IReadOnlyList<ProjectArchiveFileDescriptor> Files { get; } = files;

        public ValueTask DisposeAsync() => capture.DisposeAsync();
    }

    private static string CreateTemporaryDirectory()
    {
        var parent = VersionHistoryTemporaryPaths.GetParentDirectory();
        EnsureNoReparsePoints(parent);
        Directory.CreateDirectory(parent);
        EnsureNoReparsePoints(parent);
        var path = Path.Combine(parent, $"{TemporaryDirectoryPrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CleanupTemporaryDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;

        var parent = VersionHistoryTemporaryPaths.GetParentDirectory();
        var fullPath = Path.GetFullPath(path);
        var prefix = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(fullPath).StartsWith(TemporaryDirectoryPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to clean a non-owned version-history temporary directory.");
        }

        EnsureNoReparsePointsRecursively(fullPath);
        foreach (var file in Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(fullPath, recursive: true);
    }

    private static void EnsureNoReparsePoints(string path)
    {
        var current = new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException($"Version-history temporary paths may not contain reparse points: {current.FullName}");
            current = current.Parent;
        }
    }

    private static void EnsureNoReparsePointsRecursively(string path)
    {
        EnsureNoReparsePoints(path);
        foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
        {
            var info = new DirectoryInfo(directory);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException($"Version-history temporary paths may not contain reparse points: {directory}");
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException($"Version-history temporary paths may not contain reparse points: {file}");
        }
    }

    private static ProjectVersionRepositoryHealth ProjectVersionRepositoryHealthFromCache(
        ProjectVersionRepository repository) =>
        string.IsNullOrWhiteSpace(repository.HeadCommitSha)
            ? ProjectVersionRepositoryHealth.Uninitialized
            : ProjectVersionRepositoryHealth.Healthy;

    private static ProjectVersionRepositoryView ToRepositoryView(
        ProjectVersionRepository repository,
        ProjectVersionRepositoryHealth health,
        string? diagnostic = null) => new(
        repository.ProjectId,
        repository.Id,
        repository.CreativeRevision,
        repository.LastCheckpointRevision,
        repository.LastCheckpointContentHash,
        repository.HeadCommitSha,
        repository.HeadContentHash,
        repository.LastCheckpointAt,
        health,
        false,
        diagnostic);

    private static ProjectVersionCheckpointView ToCheckpointView(ProjectVersionCheckpoint checkpoint) => new(
        checkpoint.Id,
        checkpoint.ProjectVersionRepositoryId,
        checkpoint.ManifestSchemaVersion,
        checkpoint.CreativeRevision,
        checkpoint.ContentHash,
        checkpoint.ManifestHash,
        checkpoint.CommitSha,
        checkpoint.ParentCommitSha,
        checkpoint.Kind,
        checkpoint.Source,
        checkpoint.Message,
        checkpoint.CreatedAt);

    private static ProjectVersionOperationView ToOperationView(ProjectVersionOperation operation) => new(
        operation.Id,
        operation.ProjectVersionRepositoryId,
        operation.Kind,
        operation.Status,
        operation.RequestKey,
        operation.IsResumable,
        operation.AttemptCount,
        operation.ErrorCode,
        operation.ErrorMessage,
        operation.StartedAt,
        operation.HeartbeatAt,
        operation.CompletedAt,
        operation.AcknowledgedAt,
        operation.CreatedAt,
        operation.UpdatedAt);

    private static void ValidateProjectId(Guid projectId)
    {
        if (projectId == Guid.Empty)
            throw new ArgumentException("A project ID is required.", nameof(projectId));
    }

    private Task<T> ExecuteFenceAsync<T>(
        Guid projectId,
        string purpose,
        Func<CancellationToken, Task<T>> consume,
        CancellationToken cancellationToken)
    {
        ValidateProjectId(projectId);
        return authoringFence.ExecuteAsync(
            new AuthoringFenceRequest(projectId, [], purpose),
            (_, token) => consume(token),
            cancellationToken);
    }
}
