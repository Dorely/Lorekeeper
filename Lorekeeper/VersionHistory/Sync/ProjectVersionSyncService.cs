using System.Collections.Concurrent;
using System.Text.Json;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.VersionHistory.Git;
using Lorekeeper.VersionHistory.GitHub;
using Lorekeeper.VersionHistory.Services;
using Lorekeeper.VersionHistory.Snapshots;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.VersionHistory.Sync;

/// <summary>
/// Performs explicit, user-requested synchronization against a selected
/// GitHub repository. Network operations are deliberately synchronous inside
/// this service: there is no background sync worker and no merge/rebase path.
/// </summary>
public sealed class ProjectVersionSyncService(
    IGitRepositoryStore git,
    IGitHubConnectionService github,
    IAppDatabaseOperationFactory database,
    IVersionHistorySnapshotReader snapshotReader,
    ProjectVersionHistoryService history,
    IProjectMutationCoordinator projectMutations,
    IProjectVersionAutoPushQueue autoPushQueue,
    ProjectVersionHistoryUiEvents historyEvents) : IProjectVersionSyncService
{
    private const string MainReferenceName = "refs/heads/main";
    private const string DefaultRemoteName = "origin";
    private const string ImportStagingDirectoryName = ".import-staging";

    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, object> RepositoryLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public async Task<IReadOnlyList<ProjectVersionRemoteView>> ListRemotesAsync(
        Guid projectVersionRepositoryId,
        CancellationToken cancellationToken = default)
    {
        if (projectVersionRepositoryId == Guid.Empty)
            throw InvalidRequest("A local version-history repository identity is required.");

        await EnsureRepositoryExistsAsync(projectVersionRepositoryId, cancellationToken);
        await using var operation = await database.OpenReadAsync(cancellationToken);
        return await operation.Db.ProjectGitRemotes
            .AsNoTracking()
            .Where(remote => remote.ProjectVersionRepositoryId == projectVersionRepositoryId)
            .OrderBy(remote => remote.RemoteName)
            .Select(remote => new ProjectVersionRemoteView(
                remote.Id,
                remote.ProjectVersionRepositoryId,
                remote.GitHubConnectionId,
                remote.GitHubConnection == null ? null : remote.GitHubConnection.GitHubUserId,
                remote.GitHubConnection == null ? null : remote.GitHubConnection.AccountLogin,
                remote.GitHubConnection == null ? null : remote.GitHubConnection.AccessTokenExpiresAt,
                remote.GitHubConnection == null ? null : remote.GitHubConnection.LastValidatedAt,
                remote.GitHubConnection == null ? null : remote.GitHubConnection.RevokedAt,
                remote.RemoteName,
                remote.Owner,
                remote.RepositoryName,
                remote.GitHubRepositoryId,
                remote.CloneUrl,
                remote.WebUrl,
                remote.DefaultBranch,
                remote.CreatedAt,
                remote.UpdatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<ProjectVersionSyncStatus> GetCachedStatusAsync(
        Guid remoteId,
        CancellationToken cancellationToken = default)
    {
        if (remoteId == Guid.Empty)
            throw InvalidRequest("A project GitHub remote identity is required.");

        var remote = await LoadRemoteAsync(remoteId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var target = remote.ToTarget();
        AutoPushState? autoPushState;
        await using (var operation = await database.OpenReadAsync(cancellationToken))
        {
            autoPushState = await operation.Db.ProjectVersionOperations
                .AsNoTracking()
                .Where(item => item.ProjectGitRemoteId == remoteId
                    && item.Kind == ProjectVersionOperationKind.AutoPush
                    && item.TargetCommitSha != null)
                .OrderByDescending(item => item.CreatedAt)
                .Select(item => new AutoPushState(item.Status, item.ErrorMessage))
                .FirstOrDefaultAsync(cancellationToken);
        }
        EnsureLocalGitRepository(remote.ProjectVersionRepositoryId, initializeIfMissing: false);
        var comparison = ReadComparison(remote.ProjectVersionRepositoryId, target);
        cancellationToken.ThrowIfCancellationRequested();

        // A missing tracking ref means the remote has never been fetched in
        // this local repository. Do not present the local-only state as
        // CurrentFastForward: the UI needs an explicit attached/never-fetched
        // state until a network fetch supplies a candidate commit.
        if (comparison.CandidateCommitSha is null)
        {
            return CreateStatus(
                remote.ProjectVersionRepositoryId,
                remoteId,
                target,
                comparison.CurrentCommitSha,
                null,
                null,
                GitHistoryRelation.Empty,
                AutomaticDisposition(autoPushState, ProjectVersionSyncDisposition.Attached),
                localUpdated: false,
                remoteUpdated: false,
                AutomaticMessage(autoPushState, "The remote is attached but has not been fetched into this local repository."));
        }

        return CreateComparisonStatus(
            remote.ProjectVersionRepositoryId,
            remoteId,
            target,
            comparison,
            AutomaticDisposition(autoPushState, ProjectVersionSyncDispositionFor(comparison.Relation)),
            localUpdated: false,
            remoteUpdated: false,
            AutomaticMessage(autoPushState, "The status reflects the existing local main and remote-tracking refs; no network request was made."));
    }

    public async Task<ProjectVersionSyncStatus> AttachRemoteAsync(
        Guid projectVersionRepositoryId,
        GitHubRepositorySelection selection,
        CancellationToken cancellationToken = default)
    {
        await EnsureRepositoryExistsAsync(projectVersionRepositoryId, cancellationToken);
        var projectId = await LoadProjectIdAsync(projectVersionRepositoryId, cancellationToken);
        await using var projectLease = await projectMutations.AcquireAsync(projectId, cancellationToken);
        return await RunJournaledAsync(
            projectVersionRepositoryId,
            ProjectVersionOperationKind.Relink,
            async () =>
            {
                var remoteId = Guid.NewGuid();
                var target = ValidateSelection(selection, remoteId);
                EnsureLocalGitRepository(projectVersionRepositoryId, initializeIfMissing: true);
                var localHead = git.GetHead(projectVersionRepositoryId).CommitSha;

                await using var operation = await database.OpenWriteAsync(cancellationToken);
                var existing = await operation.Db.ProjectGitRemotes
                    .AsNoTracking()
                    .SingleOrDefaultAsync(remote =>
                        remote.ProjectVersionRepositoryId == projectVersionRepositoryId &&
                        remote.RemoteName == target.RemoteName,
                        cancellationToken);
                if (existing is not null)
                    throw InvalidRequest($"The local remote name '{target.RemoteName}' is already attached to this project.");
                var duplicateRepository = await operation.Db.ProjectGitRemotes
                    .AsNoTracking()
                    .AnyAsync(remote =>
                        remote.ProjectVersionRepositoryId == projectVersionRepositoryId &&
                        remote.Owner == target.Owner &&
                        remote.RepositoryName == target.RepositoryName,
                        cancellationToken);
                if (duplicateRepository)
                    throw InvalidRequest("This GitHub repository is already attached to the project under another remote name.");

                ConfigureGitRemote(projectVersionRepositoryId, target);
                var now = DateTime.UtcNow;
                var entity = new ProjectGitRemote
                {
                    Id = remoteId,
                    ProjectVersionRepositoryId = projectVersionRepositoryId,
                    GitHubConnectionId = target.ConnectionId,
                    RemoteName = target.RemoteName,
                    Owner = target.Owner,
                    RepositoryName = target.RepositoryName,
                    GitHubRepositoryId = target.GitHubRepositoryId,
                    CloneUrl = target.CloneUrl.AbsoluteUri,
                    WebUrl = selection.Repository.HtmlUrl,
                    DefaultBranch = target.DefaultBranch,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                operation.Db.ProjectGitRemotes.Add(entity);
                await ProjectVersionHistoryService.AddPendingAutoPushIntentAsync(
                    operation.Db,
                    projectVersionRepositoryId,
                    entity,
                    localHead,
                    now,
                    cancellationToken);
                await operation.SaveChangesAsync(cancellationToken);

                autoPushQueue.Signal();
                return CreateStatus(
                    projectVersionRepositoryId,
                    remoteId,
                    target,
                    localHead,
                    null,
                    null,
                    GitHistoryRelation.Empty,
                    ProjectVersionSyncDisposition.Attached,
                    localUpdated: false,
                    remoteUpdated: false,
                    "The GitHub remote was attached. Fetch before comparing remote history.");
            },
            cancellationToken);
    }

    public async Task<ProjectVersionSyncStatus> UpdateRemoteAsync(
        Guid remoteId,
        GitHubRepositorySelection selection,
        CancellationToken cancellationToken = default)
    {
        var remote = await LoadRemoteAsync(remoteId, cancellationToken);
        var projectId = await LoadProjectIdAsync(remote.ProjectVersionRepositoryId, cancellationToken);
        await using var projectLease = await projectMutations.AcquireAsync(projectId, cancellationToken);
        remote = await LoadRemoteAsync(remoteId, cancellationToken);
        return await RunJournaledAsync(
            remote.ProjectVersionRepositoryId,
            ProjectVersionOperationKind.Relink,
            async () =>
            {
                var target = ValidateSelection(selection, remote.Id);
                if (!string.Equals(target.RemoteName, remote.RemoteName, StringComparison.Ordinal))
                    throw InvalidRequest("A remote name cannot be changed in place because its tracking refs must be preserved. Remove and attach the remote with the new name.");

                EnsureLocalGitRepository(remote.ProjectVersionRepositoryId, initializeIfMissing: false);
                ConfigureGitRemote(remote.ProjectVersionRepositoryId, target);

                await using var operation = await database.OpenWriteAsync(cancellationToken);
                var entity = await operation.Db.ProjectGitRemotes
                    .SingleOrDefaultAsync(item => item.Id == remoteId, cancellationToken)
                    ?? throw RemoteNotFound(remoteId);
                var duplicateRepository = await operation.Db.ProjectGitRemotes
                    .AsNoTracking()
                    .AnyAsync(item =>
                        item.Id != remoteId &&
                        item.ProjectVersionRepositoryId == remote.ProjectVersionRepositoryId &&
                        item.Owner == target.Owner &&
                        item.RepositoryName == target.RepositoryName,
                        cancellationToken);
                if (duplicateRepository)
                    throw InvalidRequest("This GitHub repository is already attached to the project under another remote name.");
                entity.GitHubConnectionId = target.ConnectionId;
                entity.Owner = target.Owner;
                entity.RepositoryName = target.RepositoryName;
                entity.GitHubRepositoryId = target.GitHubRepositoryId;
                entity.CloneUrl = target.CloneUrl.AbsoluteUri;
                entity.WebUrl = selection.Repository.HtmlUrl;
                entity.DefaultBranch = target.DefaultBranch;
                entity.UpdatedAt = DateTime.UtcNow;
                var localHead = git.GetHead(remote.ProjectVersionRepositoryId).CommitSha;
                await ProjectVersionHistoryService.AddPendingAutoPushIntentAsync(
                    operation.Db,
                    remote.ProjectVersionRepositoryId,
                    entity,
                    localHead,
                    entity.UpdatedAt,
                    cancellationToken);
                await operation.SaveChangesAsync(cancellationToken);

                autoPushQueue.Signal();
                return CreateStatus(
                    remote.ProjectVersionRepositoryId,
                    remoteId,
                    target,
                    localHead,
                    null,
                    null,
                    GitHistoryRelation.Empty,
                    ProjectVersionSyncDisposition.Updated,
                    localUpdated: false,
                    remoteUpdated: false,
                    "The GitHub remote metadata and local Git remote were updated.");
            },
            cancellationToken);
    }

    public async Task<ProjectVersionSyncStatus> RemoveRemoteAsync(
        Guid remoteId,
        CancellationToken cancellationToken = default)
    {
        var remote = await LoadRemoteAsync(remoteId, cancellationToken);
        var projectId = await LoadProjectIdAsync(remote.ProjectVersionRepositoryId, cancellationToken);
        await using var projectLease = await projectMutations.AcquireAsync(projectId, cancellationToken);
        remote = await LoadRemoteAsync(remoteId, cancellationToken);
        return await RunJournaledAsync(
            remote.ProjectVersionRepositoryId,
            ProjectVersionOperationKind.Relink,
            async () =>
            {
                RemoveGitRemote(remote.ProjectVersionRepositoryId, remote.RemoteName);

                await using var operation = await database.OpenWriteAsync(cancellationToken);
                var entity = await operation.Db.ProjectGitRemotes
                    .SingleOrDefaultAsync(item => item.Id == remoteId, cancellationToken)
                    ?? throw RemoteNotFound(remoteId);
                var interruptedPushes = await operation.Db.ProjectVersionOperations
                    .Where(item => item.ProjectGitRemoteId == remoteId
                        && item.Kind == ProjectVersionOperationKind.AutoPush
                        && (item.Status == ProjectVersionOperationStatus.Pending
                            || item.Status == ProjectVersionOperationStatus.Running))
                    .ToListAsync(cancellationToken);
                var removedAt = DateTime.UtcNow;
                foreach (var interruptedPush in interruptedPushes)
                {
                    interruptedPush.Status = ProjectVersionOperationStatus.Canceled;
                    interruptedPush.IsResumable = false;
                    interruptedPush.ErrorCode = ProjectVersionSyncErrorCode.RemoteNotFound.ToString();
                    interruptedPush.ErrorMessage = "The attached remote was removed before its automatic push completed.";
                    interruptedPush.CompletedAt = removedAt;
                    interruptedPush.HeartbeatAt = removedAt;
                    interruptedPush.UpdatedAt = removedAt;
                }
                operation.Db.ProjectGitRemotes.Remove(entity);
                await operation.SaveChangesAsync(cancellationToken);

                var localHead = git.GetHead(remote.ProjectVersionRepositoryId).CommitSha;
                var target = remote.ToTarget();
                return CreateStatus(
                    remote.ProjectVersionRepositoryId,
                    remoteId,
                    target,
                    localHead,
                    null,
                    null,
                    GitHistoryRelation.Empty,
                    ProjectVersionSyncDisposition.Removed,
                    localUpdated: false,
                    remoteUpdated: false,
                    "The remote connection was removed. Existing remote-tracking refs were preserved.");
            },
            cancellationToken);
    }

    public async Task<ProjectVersionSyncStatus> FetchAsync(
        Guid remoteId,
        CancellationToken cancellationToken = default)
    {
        var remote = await LoadRemoteAsync(remoteId, cancellationToken);
        return await RunJournaledAsync(
            remote.ProjectVersionRepositoryId,
            ProjectVersionOperationKind.Fetch,
            async () =>
            {
                var target = remote.ToTarget();
                EnsureLocalGitRepository(remote.ProjectVersionRepositoryId, initializeIfMissing: false);
                ConfigureGitRemote(remote.ProjectVersionRepositoryId, target);
                var credentials = await CreateCredentialsAsync(remote, cancellationToken);
                FetchRemote(remote.ProjectVersionRepositoryId, target, credentials, cancellationToken);
                var comparison = await ReadVerifiedComparisonAsync(
                    remote.ProjectVersionRepositoryId,
                    target,
                    cancellationToken);
                return CreateComparisonStatus(
                    remote.ProjectVersionRepositoryId,
                    remoteId,
                    target,
                    comparison,
                    ProjectVersionSyncDispositionFor(comparison.Relation),
                    localUpdated: false,
                    remoteUpdated: false,
                    "The remote branch was fetched without changing local main.");
            },
            cancellationToken);
    }

    internal async Task<ProjectVersionSyncStatus> FastForwardLocalUnderLeaseAsync(
        Guid remoteId,
        CancellationToken cancellationToken = default)
    {
        var remote = await LoadRemoteAsync(remoteId, cancellationToken);
        return await FastForwardLocalUnderLeaseAsync(remote, cancellationToken);
    }

    private async Task<ProjectVersionSyncStatus> FastForwardLocalUnderLeaseAsync(
        RemoteContext remote,
        CancellationToken cancellationToken)
    {
        return await RunJournaledAsync(
            remote.ProjectVersionRepositoryId,
            ProjectVersionOperationKind.Pull,
            async () =>
            {
                var target = remote.ToTarget();
                EnsureLocalGitRepository(remote.ProjectVersionRepositoryId, initializeIfMissing: false);
                ConfigureGitRemote(remote.ProjectVersionRepositoryId, target);
                var credentials = await CreateCredentialsAsync(remote, cancellationToken);
                FetchRemote(remote.ProjectVersionRepositoryId, target, credentials, cancellationToken);
                var comparison = await ReadVerifiedComparisonAsync(
                    remote.ProjectVersionRepositoryId,
                    target,
                    cancellationToken);

                if (comparison.Relation is not GitHistoryRelation.CandidateFastForward)
                {
                    var disposition = comparison.Relation switch
                    {
                        GitHistoryRelation.Identical => ProjectVersionSyncDisposition.AlreadySynchronized,
                        GitHistoryRelation.CurrentFastForward => ProjectVersionSyncDisposition.LocalAhead,
                        GitHistoryRelation.Diverged => ProjectVersionSyncDisposition.Diverged,
                        GitHistoryRelation.Unrelated => ProjectVersionSyncDisposition.Unrelated,
                        _ => ProjectVersionSyncDisposition.Empty,
                    };
                    return CreateComparisonStatus(
                        remote.ProjectVersionRepositoryId,
                        remote.Id,
                        target,
                        comparison,
                        disposition,
                        localUpdated: false,
                        remoteUpdated: false,
                        "Local main was not changed because the fetched remote tip is not a fast-forward descendant.");
                }

                var forwarded = git.FastForwardMain(
                    remote.ProjectVersionRepositoryId,
                    comparison.CurrentCommitSha,
                    comparison.CandidateCommitSha!);
                var finalComparison = ReadComparison(remote.ProjectVersionRepositoryId, target);
                return CreateComparisonStatus(
                    remote.ProjectVersionRepositoryId,
                    remote.Id,
                    target,
                    finalComparison,
                    ProjectVersionSyncDisposition.FastForwarded,
                    localUpdated: forwarded.CommitSha is not null,
                    remoteUpdated: false,
                    "Local main was advanced by fast-forward only; no merge or rebase was performed.");
            },
            cancellationToken);
    }

    public async Task<ProjectVersionSyncStatus> PushAsync(
        Guid remoteId,
        CancellationToken cancellationToken = default)
    {
        var remote = await LoadRemoteAsync(remoteId, cancellationToken);
        var projectId = await LoadProjectIdAsync(remote.ProjectVersionRepositoryId, cancellationToken);
        await using var projectLease = await projectMutations.AcquireAsync(projectId, cancellationToken);
        remote = await LoadRemoteAsync(remoteId, cancellationToken);
        try
        {
            var result = await RunJournaledAsync(
                remote.ProjectVersionRepositoryId,
                ProjectVersionOperationKind.Push,
                () => PushUnderLeaseAsync(remote, targetCommitSha: null, cancellationToken),
                cancellationToken);
            if (result.LocalCommitSha is not null
                && result.Disposition is (ProjectVersionSyncDisposition.Pushed
                    or ProjectVersionSyncDisposition.AlreadySynchronized))
                await ResolveMatchingAutoPushIntentAsync(
                    remote.Id,
                    result.LocalCommitSha,
                    CancellationToken.None);
            historyEvents.PublishRemoteSyncChanged(projectId);
            return result;
        }
        catch
        {
            historyEvents.PublishRemoteSyncChanged(projectId);
            throw;
        }
    }

    /// <summary>
    /// Executes the same fetch/compare/non-force push transport used by the
    /// public manual operation. The caller owns the project mutation lease and
    /// the durable AutoPush journal row.
    /// </summary>
    internal async Task<ProjectVersionSyncStatus> PushAutomaticUnderLeaseAsync(
        Guid remoteId,
        string targetCommitSha,
        CancellationToken cancellationToken)
    {
        var remote = await LoadRemoteAsync(remoteId, cancellationToken);
        return await PushUnderLeaseAsync(remote, targetCommitSha, cancellationToken);
    }

    private async Task<ProjectVersionSyncStatus> PushUnderLeaseAsync(
        RemoteContext remote,
        string? targetCommitSha,
        CancellationToken cancellationToken)
    {
        await EnsureCleanForPushAsync(remote.ProjectVersionRepositoryId, cancellationToken);
        var target = remote.ToTarget();
        EnsureLocalGitRepository(remote.ProjectVersionRepositoryId, initializeIfMissing: false);
        ConfigureGitRemote(remote.ProjectVersionRepositoryId, target);
        var credentials = await CreateCredentialsAsync(remote, cancellationToken);
        FetchRemote(remote.ProjectVersionRepositoryId, target, credentials, cancellationToken);
        var comparison = await ReadVerifiedComparisonAsync(
            remote.ProjectVersionRepositoryId,
            target,
            cancellationToken);

        if (targetCommitSha is not null &&
            !string.Equals(comparison.CurrentCommitSha, targetCommitSha, StringComparison.Ordinal))
        {
            if (comparison.CurrentCommitSha is null)
                throw new ProjectVersionSyncException(
                    ProjectVersionSyncErrorCode.HistoryConflict,
                    "The automatic push target is no longer the local main head.");

            var targetRelation = git.CompareHistory(
                remote.ProjectVersionRepositoryId,
                targetCommitSha,
                comparison.CurrentCommitSha);
            if (targetRelation.Relation is not (GitHistoryRelation.CandidateFastForward or GitHistoryRelation.Identical))
                throw new ProjectVersionSyncException(
                    ProjectVersionSyncErrorCode.HistoryConflict,
                    "The automatic push target is no longer an ancestor of local main.");

            return CreateComparisonStatus(
                remote.ProjectVersionRepositoryId,
                remote.Id,
                target,
                comparison,
                ProjectVersionSyncDisposition.PushSkipped,
                localUpdated: false,
                remoteUpdated: false,
                "The automatic push target was superseded by a newer local checkpoint.");
        }

        var pushAllowed = comparison.CurrentCommitSha is not null &&
            (comparison.CandidateCommitSha is null ||
             comparison.Relation is GitHistoryRelation.CurrentFastForward);
        if (!pushAllowed)
        {
            var disposition = comparison.Relation switch
            {
                GitHistoryRelation.Empty => ProjectVersionSyncDisposition.PushSkipped,
                GitHistoryRelation.Identical => ProjectVersionSyncDisposition.AlreadySynchronized,
                GitHistoryRelation.CandidateFastForward => ProjectVersionSyncDisposition.RemoteAhead,
                GitHistoryRelation.Diverged => ProjectVersionSyncDisposition.Diverged,
                GitHistoryRelation.Unrelated => ProjectVersionSyncDisposition.Unrelated,
                _ => ProjectVersionSyncDisposition.PushSkipped,
            };
            return CreateComparisonStatus(
                remote.ProjectVersionRepositoryId,
                remote.Id,
                target,
                comparison,
                disposition,
                localUpdated: false,
                remoteUpdated: false,
                "Push was not performed because the remote tip is not empty or an ancestor of local main.");
        }

        var pushedCommitSha = comparison.CurrentCommitSha!;
        var pushOperation = await CreatePushOperationAsync(target, pushedCommitSha, cancellationToken);
        PushRemote(remote.ProjectVersionRepositoryId, target, pushOperation, cancellationToken);

        // A successful libgit2 transport does not by itself prove that the
        // server updated the requested ref: GitHub can report a ref-level
        // rejection through PushStatusError. Fetch again over the network and
        // only report success when the exact target SHA is observed there.
        FetchRemote(remote.ProjectVersionRepositoryId, target, credentials, cancellationToken);
        var pushedComparison = await ReadVerifiedComparisonAsync(
            remote.ProjectVersionRepositoryId,
            target,
            cancellationToken);
        if (!string.Equals(pushedComparison.CandidateCommitSha, pushedCommitSha, StringComparison.Ordinal))
            throw new ProjectVersionSyncException(
                ProjectVersionSyncErrorCode.TransportFailure,
                "GitHub did not report the requested commit on the configured branch. The remote was not marked synchronized; retry after checking repository access and history.");

        return CreateComparisonStatus(
            remote.ProjectVersionRepositoryId,
            remote.Id,
            target,
            pushedComparison,
            ProjectVersionSyncDisposition.Pushed,
            localUpdated: false,
            remoteUpdated: true,
            "Local main was pushed with a non-force fast-forward update.");
    }

    private async Task EnsureCleanForPushAsync(
        Guid projectVersionRepositoryId,
        CancellationToken cancellationToken)
    {
        var projectId = await LoadProjectIdAsync(projectVersionRepositoryId, cancellationToken);

        var status = await history.GetStatusUnderLeaseAsync(
            projectId,
            includeCurrentSnapshotHash: true,
            cancellationToken);
        if (status?.Repository.IsDirty == true)
            throw new ProjectVersionSyncException(
                ProjectVersionSyncErrorCode.HistoryConflict,
                "Push is blocked while the project has uncheckpointed changes. Create a local checkpoint before pushing to GitHub.");
    }

    private async Task<Guid> LoadProjectIdAsync(
        Guid projectVersionRepositoryId,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        var projectId = await operation.Db.ProjectVersionRepositories
            .AsNoTracking()
            .Where(repository => repository.Id == projectVersionRepositoryId)
            .Select(repository => repository.ProjectId)
            .SingleOrDefaultAsync(cancellationToken);
        return projectId == Guid.Empty
            ? throw RepositoryNotFound(projectVersionRepositoryId)
            : projectId;
    }

    private async Task ResolveMatchingAutoPushIntentAsync(
        Guid remoteId,
        string commitSha,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var rows = await operation.Db.ProjectVersionOperations
            .Where(item => item.ProjectGitRemoteId == remoteId
                && item.Kind == ProjectVersionOperationKind.AutoPush
                && item.TargetCommitSha == commitSha
                && item.Status != ProjectVersionOperationStatus.Succeeded)
            .ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            row.Status = ProjectVersionOperationStatus.Succeeded;
            row.IsResumable = false;
            row.ErrorCode = null;
            row.ErrorMessage = null;
            row.CompletedAt = now;
            row.HeartbeatAt = now;
            row.UpdatedAt = now;
        }

        if (rows.Count > 0)
            await operation.SaveChangesAsync(cancellationToken);
    }

    public async Task<VersionHistoryImportCloneResult> CloneForImportAsync(
        VersionHistoryImportCloneRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = ValidateSelection(new GitHubRepositorySelection(request.ConnectionId, request.Repository));
        var validatedSelection = new GitHubRepositorySelection(
            target.ConnectionId,
            request.Repository,
            target.RemoteName);
        var credentials = await CreateCredentialsAsync(target.ConnectionId, cancellationToken);
        var stagingPath = CreateStagingRepositoryPath();
        var extractionPath = CreateStagingExtractionPath(stagingPath);
        Guid? journalId = null;
        VersionHistorySnapshotManifest? installedManifest = null;
        Guid? installedRepositoryId = null;
        string? installedDestinationPath = null;
        var installedByThisAttempt = false;
        var snapshotOwnershipTransferred = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            CloneRepository(target, credentials, stagingPath);
            cancellationToken.ThrowIfCancellationRequested();

            string headCommitSha;
            string headTreeSha;
            using (var stagedRepository = OpenRepository(stagingPath))
            {
                var head = ResolveCloneHead(stagedRepository, target.DefaultBranch);
                headCommitSha = head.Sha;
                headTreeSha = head.Tree.Sha;
                MaterializeImportTree(head.Tree, extractionPath, string.Empty, depth: 0, cancellationToken);
            }

            var manifest = ReadManifest(extractionPath);
            if (manifest.RepositoryId == Guid.Empty || manifest.ProjectId == Guid.Empty)
                throw InvalidManifest("The imported manifest did not contain valid repository and project identities.");
            installedManifest = manifest;

            var artifact = ReadStrictArtifact(
                extractionPath,
                manifest.RepositoryId,
                manifest.ProjectId,
                cancellationToken);
            journalId = await TryStartCloneJournalAsync(manifest.RepositoryId, cancellationToken);

            var destinationPath = git.GetRepositoryPath(manifest.RepositoryId);
            EnsureHistoryPath(destinationPath);
            var installedNewRepository = true;
            if (Directory.Exists(destinationPath) || File.Exists(destinationPath))
            {
                if (File.Exists(destinationPath) || !await IsUnownedDatabaseIdentityAsync(manifest, cancellationToken))
                    throw new ProjectVersionSyncException(
                        ProjectVersionSyncErrorCode.RepositoryCollision,
                        "The imported repository identity already has a local history path or database identity. The existing local identity was not overwritten.");

                AdoptIdenticalOrphanRepository(
                    destinationPath,
                    headCommitSha,
                    headTreeSha,
                    artifact,
                    extractionPath,
                    cancellationToken);
                installedNewRepository = false;
            }
            else
            {
                Directory.Move(stagingPath, destinationPath);
                installedByThisAttempt = true;
                installedRepositoryId = manifest.RepositoryId;
                installedDestinationPath = destinationPath;
                using var destinationRepository = OpenRepository(destinationPath);
                var destinationHead = destinationRepository.Lookup(headCommitSha, ObjectType.Commit) as Commit
                    ?? throw InvalidManifest("The cloned repository lost its validated head commit while being installed.");
                SetMainReference(destinationRepository, destinationHead);
            }

            var result = new VersionHistoryImportCloneResult(
                artifact.Manifest.RepositoryId,
                artifact.Manifest.ProjectId,
                destinationPath,
                headCommitSha,
                headTreeSha,
                artifact,
                validatedSelection,
                installedNewRepository);
            if (journalId is Guid completedOperationId)
                await CompleteOperationAsync(completedOperationId, ProjectVersionOperationStatus.Succeeded, null, null);
            result.RetainSnapshot(() => DeleteTemporaryDirectory(extractionPath));
            snapshotOwnershipTransferred = true;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (journalId is Guid canceledOperationId)
                await CompleteOperationAsync(canceledOperationId, ProjectVersionOperationStatus.Canceled, ProjectVersionSyncErrorCode.Canceled.ToString(), "The clone operation was canceled.");
            throw;
        }
        catch (Exception exception)
        {
            if (installedByThisAttempt
                && installedManifest is not null
                && installedRepositoryId is Guid repositoryId
                && installedDestinationPath is not null)
            {
                await TryRemoveFailedCloneInstallAsync(
                    repositoryId,
                    installedDestinationPath,
                    installedManifest);
            }

            if (journalId is Guid failedOperationId)
                await CompleteOperationAsync(failedOperationId, ProjectVersionOperationStatus.Failed, ErrorCodeFor(exception), SafeErrorMessage(exception));
            throw WrapCloneException(exception);
        }
        finally
        {
            if (!snapshotOwnershipTransferred)
                DeleteTemporaryDirectory(extractionPath);
            DeleteTemporaryDirectory(stagingPath);
        }
    }

    private async Task TryRemoveFailedCloneInstallAsync(
        Guid repositoryId,
        string destinationPath,
        VersionHistorySnapshotManifest manifest)
    {
        try
        {
            var expectedPath = git.GetRepositoryPath(repositoryId);
            if (!string.Equals(
                    Path.GetFullPath(destinationPath),
                    Path.GetFullPath(expectedPath),
                    PathComparison()))
                return;
            EnsureHistoryPath(expectedPath);
            if (!await IsUnownedDatabaseIdentityAsync(manifest, CancellationToken.None))
                return;

            var stage = git.StageRepositoryDeletion(repositoryId);
            try
            {
                if (!await IsUnownedDatabaseIdentityAsync(manifest, CancellationToken.None))
                {
                    git.RollbackRepositoryDeletion(stage);
                    return;
                }

                git.FinalizeRepositoryDeletion(stage);
            }
            catch
            {
                try
                {
                    git.RollbackRepositoryDeletion(stage);
                }
                catch
                {
                    // Preserve the clone failure; staged deletion remains
                    // recoverable by the store's normal reconciliation.
                }
            }
        }
        catch
        {
            // A failed install remains a validated, app-owned orphan. The
            // next identical clone can adopt it only after strict validation.
        }
    }

    public async Task<bool> RemoveImportedRepositoryIfUnownedAsync(
        VersionHistoryImportCloneResult clone,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clone);
        if (!clone.InstalledNewRepository)
            return false;
        if (clone.RepositoryId == Guid.Empty || clone.ProjectId == Guid.Empty || clone.Artifact is null)
            throw InvalidRequest("The clone result does not contain complete repository and project identity.");
        if (clone.Artifact.Manifest.RepositoryId != clone.RepositoryId
            || clone.Artifact.Manifest.ProjectId != clone.ProjectId)
            throw InvalidRequest("The clone result artifact identity does not match the installed repository identity.");

        var expectedPath = git.GetRepositoryPath(clone.RepositoryId);
        if (!string.Equals(
                Path.GetFullPath(clone.RepositoryPath),
                Path.GetFullPath(expectedPath),
                PathComparison()))
            throw InvalidRequest("The clone result path does not match the repository identity.");
        EnsureHistoryPath(expectedPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (!await IsUnownedDatabaseIdentityAsync(clone.Artifact.Manifest, cancellationToken))
            return false;

        var stage = git.StageRepositoryDeletion(clone.RepositoryId);
        try
        {
            // Recheck after staging the exact path so a concurrent restore
            // cannot turn the safety check into deletion of an adopted repo.
            if (!await IsUnownedDatabaseIdentityAsync(clone.Artifact.Manifest, cancellationToken))
            {
                git.RollbackRepositoryDeletion(stage);
                return false;
            }

            git.FinalizeRepositoryDeletion(stage);
            return true;
        }
        catch
        {
            try
            {
                git.RollbackRepositoryDeletion(stage);
            }
            catch
            {
                // Preserve the original cleanup failure. The deletion API's
                // staged path remains recoverable by its normal reconciliation.
            }
            throw;
        }
    }

    private async Task<T> RunJournaledAsync<T>(
        Guid repositoryId,
        ProjectVersionOperationKind kind,
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var operationId = await StartOperationAsync(repositoryId, kind, cancellationToken);
        try
        {
            var result = await operation();
            await CompleteOperationAsync(operationId, ProjectVersionOperationStatus.Succeeded, null, null);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CompleteOperationAsync(
                operationId,
                ProjectVersionOperationStatus.Canceled,
                ProjectVersionSyncErrorCode.Canceled.ToString(),
                "The version-history operation was canceled.");
            throw;
        }
        catch (Exception exception)
        {
            await CompleteOperationAsync(
                operationId,
                ProjectVersionOperationStatus.Failed,
                ErrorCodeFor(exception),
                SafeErrorMessage(exception));
            throw;
        }
    }

    private async Task<Guid> StartOperationAsync(
        Guid repositoryId,
        ProjectVersionOperationKind kind,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var repository = await operation.Db.ProjectVersionRepositories
            .SingleOrDefaultAsync(item => item.Id == repositoryId, cancellationToken)
            ?? throw RepositoryNotFound(repositoryId);

        var now = DateTime.UtcNow;
        var journal = new ProjectVersionOperation
        {
            ProjectVersionRepositoryId = repositoryId,
            Repository = repository,
            Kind = kind,
            Status = ProjectVersionOperationStatus.Running,
            RequestKey = $"sync:{kind}:{Guid.NewGuid():N}",
            IsResumable = kind is ProjectVersionOperationKind.Fetch or ProjectVersionOperationKind.Push or ProjectVersionOperationKind.Pull,
            AttemptCount = 1,
            StartedAt = now,
            HeartbeatAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        operation.Db.ProjectVersionOperations.Add(journal);
        await operation.SaveChangesAsync(cancellationToken);
        return journal.Id;
    }

    private async Task<Guid?> TryStartCloneJournalAsync(
        Guid repositoryId,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var repository = await operation.Db.ProjectVersionRepositories
            .SingleOrDefaultAsync(item => item.Id == repositoryId, cancellationToken);
        if (repository is null)
            return null;

        var now = DateTime.UtcNow;
        var journal = new ProjectVersionOperation
        {
            ProjectVersionRepositoryId = repositoryId,
            Repository = repository,
            Kind = ProjectVersionOperationKind.Clone,
            Status = ProjectVersionOperationStatus.Running,
            RequestKey = $"sync:{ProjectVersionOperationKind.Clone}:{Guid.NewGuid():N}",
            IsResumable = false,
            AttemptCount = 1,
            StartedAt = now,
            HeartbeatAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        operation.Db.ProjectVersionOperations.Add(journal);
        await operation.SaveChangesAsync(cancellationToken);
        return journal.Id;
    }

    private async Task<bool> IsUnownedDatabaseIdentityAsync(
        VersionHistorySnapshotManifest manifest,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        return !await operation.Db.Projects.AsNoTracking().AnyAsync(
                   project => project.Id == manifest.ProjectId,
                   cancellationToken)
            && !await operation.Db.ProjectVersionRepositories.AsNoTracking().AnyAsync(
                repository => repository.Id == manifest.RepositoryId,
                cancellationToken);
    }

    private async Task CompleteOperationAsync(
        Guid operationId,
        ProjectVersionOperationStatus status,
        string? errorCode,
        string? errorMessage)
    {
        await using var operation = await database.OpenWriteAsync(CancellationToken.None);
        var journal = await operation.Db.ProjectVersionOperations
            .SingleOrDefaultAsync(item => item.Id == operationId, CancellationToken.None);
        if (journal is null)
            return;

        var now = DateTime.UtcNow;
        journal.Status = status;
        journal.ErrorCode = errorCode;
        journal.ErrorMessage = errorMessage;
        journal.HeartbeatAt = now;
        journal.CompletedAt = now;
        journal.UpdatedAt = now;
        await operation.SaveChangesAsync(CancellationToken.None);
    }

    private async Task EnsureRepositoryExistsAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        if (!await operation.Db.ProjectVersionRepositories.AsNoTracking()
                .AnyAsync(repository => repository.Id == repositoryId, cancellationToken))
            throw RepositoryNotFound(repositoryId);
    }

    private async Task<RemoteContext> LoadRemoteAsync(Guid remoteId, CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        var remote = await operation.Db.ProjectGitRemotes
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == remoteId, cancellationToken);
        return remote is null ? throw RemoteNotFound(remoteId) : new RemoteContext(remote);
    }

    private async Task<CredentialsHandler> CreateCredentialsAsync(
        RemoteContext remote,
        CancellationToken cancellationToken)
    {
        if (remote.GitHubConnectionId is not Guid connectionId || connectionId == Guid.Empty)
            throw new ProjectVersionSyncException(
                ProjectVersionSyncErrorCode.CredentialUnavailable,
                "The remote has no reusable GitHub connection. Reattach it to a GitHub account.");

        return await CreateCredentialsAsync(connectionId, cancellationToken);
    }

    private async Task<CredentialsHandler> CreateCredentialsAsync(
        Guid connectionId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await github.CreateCredentialsHandlerAsync(connectionId, cancellationToken);
        }
        catch (GitHubConnectionException exception)
        {
            throw new ProjectVersionSyncException(
                ProjectVersionSyncErrorCode.CredentialUnavailable,
                "The selected GitHub connection is unavailable. Reauthorize GitHub before syncing.",
                exception);
        }
    }

    private async Task<IGitHubPushOperation> CreatePushOperationAsync(
        RemoteTarget target,
        string sourceCommitSha,
        CancellationToken cancellationToken)
    {
        try
        {
            return await github.CreatePushOperationAsync(
                target.ConnectionId,
                target.GitHubRepositoryId,
                target.Owner,
                target.RepositoryName,
                sourceCommitSha,
                target.DefaultBranch,
                cancellationToken);
        }
        catch (GitHubConnectionException exception)
        {
            throw new ProjectVersionSyncException(
                ProjectVersionSyncErrorCode.CredentialUnavailable,
                "The selected GitHub connection is unavailable. Reauthorize GitHub before syncing.",
                exception);
        }
    }

    private void FetchRemote(
        Guid repositoryId,
        RemoteTarget target,
        CredentialsHandler credentials,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = git.GetRepositoryPath(repositoryId);
        lock (GetRepositoryLock(path))
        {
            using var repository = OpenRepository(path);
            var remote = repository.Network.Remotes[target.RemoteName]
                ?? throw InvalidRemote($"The configured Git remote '{target.RemoteName}' is missing.");
            var options = new FetchOptions
            {
                CredentialsProvider = credentials,
                Prune = true,
            };
            // Remove this attachment's last observed tip before the network
            // request. A deleted branch, empty repository, or failed fetch
            // must become Attached/unknown rather than stale Synchronized.
            var existingTracking = repository.Refs[target.TrackingRef];
            if (existingTracking is not null)
                repository.Refs.Remove(existingTracking);
            var refSpec = BuildFetchRefSpec(target.RemoteId);
            try
            {
                Commands.Fetch(repository, remote.Name, [refSpec], options, "Lorekeeper version-history fetch");
            }
            catch (Exception exception) when (exception is LibGit2SharpException or IOException)
            {
                throw new ProjectVersionSyncException(
                    ProjectVersionSyncErrorCode.TransportFailure,
                    "GitHub fetch failed. Check the repository permission and network connection, then retry.",
                    exception);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void PushRemote(
        Guid repositoryId,
        RemoteTarget target,
        IGitHubPushOperation pushOperation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = git.GetRepositoryPath(repositoryId);
        lock (GetRepositoryLock(path))
        {
            using var repository = OpenRepository(path);
            var remote = repository.Network.Remotes[target.RemoteName]
                ?? throw InvalidRemote($"The configured Git remote '{target.RemoteName}' is missing.");
            try
            {
                // There is intentionally no '+' force marker here. GitHub
                // rejects the push if the remote advanced after our fetch.
                pushOperation.Execute(repository, remote);
            }
            catch (Exception exception) when (exception is LibGit2SharpException or IOException)
            {
                throw new ProjectVersionSyncException(
                    ProjectVersionSyncErrorCode.TransportFailure,
                    "GitHub push failed or the remote advanced concurrently. Fetch again and review the history relationship.",
                    exception);
            }
            catch (GitHubConnectionException exception)
            {
                throw new ProjectVersionSyncException(
                    ProjectVersionSyncErrorCode.TransportFailure,
                    exception.Message,
                    exception);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private GitHistoryComparison ReadComparison(Guid repositoryId, RemoteTarget target)
    {
        var localHead = git.GetHead(repositoryId).CommitSha;
        var remoteHead = ReadTrackingCommitSha(repositoryId, target.TrackingRef);
        return git.CompareHistory(repositoryId, localHead, remoteHead);
    }

    private async Task<GitHistoryComparison> ReadVerifiedComparisonAsync(
        Guid repositoryId,
        RemoteTarget target,
        CancellationToken cancellationToken)
    {
        var localHead = git.GetHead(repositoryId).CommitSha;
        var trackingHead = ReadTrackingCommitSha(repositoryId, target.TrackingRef);
        var githubHead = await ObserveGitHubBranchHeadAsync(
            target.ConnectionId,
            target.GitHubRepositoryId,
            target.Owner,
            target.RepositoryName,
            target.DefaultBranch,
            cancellationToken);

        if (githubHead is null)
            return git.CompareHistory(repositoryId, localHead, null);
        if (!string.Equals(trackingHead, githubHead, StringComparison.Ordinal))
            throw new ProjectVersionSyncException(
                ProjectVersionSyncErrorCode.TransportFailure,
                "The authenticated GitHub branch head did not match the fetched tracking ref. The remote state was not trusted; fetch again and retry.");

        return git.CompareHistory(repositoryId, localHead, githubHead);
    }

    private async Task<string?> ObserveGitHubBranchHeadAsync(
        Guid connectionId,
        long repositoryId,
        string owner,
        string repositoryName,
        string branchName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await github.GetBranchHeadAsync(
                connectionId,
                repositoryId,
                owner,
                repositoryName,
                branchName,
                cancellationToken);
        }
        catch (GitHubConnectionException exception)
        {
            var code = exception.Code switch
            {
                GitHubConnectionErrorCode.Conflict => ProjectVersionSyncErrorCode.HistoryConflict,
                GitHubConnectionErrorCode.Unauthorized or
                GitHubConnectionErrorCode.Forbidden or
                GitHubConnectionErrorCode.ExpiredToken or
                GitHubConnectionErrorCode.ConnectionNotFound => ProjectVersionSyncErrorCode.CredentialUnavailable,
                _ => ProjectVersionSyncErrorCode.TransportFailure,
            };
            var message = exception.Code == GitHubConnectionErrorCode.Conflict
                ? exception.Message
                : "GitHub branch verification failed. Check the connection and repository access, then retry.";
            throw new ProjectVersionSyncException(code, message, exception);
        }
    }

    private string? ReadTrackingCommitSha(Guid repositoryId, string trackingRef)
    {
        var path = git.GetRepositoryPath(repositoryId);
        lock (GetRepositoryLock(path))
        {
            using var repository = OpenRepository(path);
            var reference = repository.Refs[trackingRef];
            if (reference is null)
                return null;
            var commit = repository.Lookup(reference.TargetIdentifier, ObjectType.Commit) as Commit;
            if (commit is null)
                throw InvalidRemote("The remote-tracking ref does not point to a commit.");
            return commit.Sha;
        }
    }

    private void ConfigureGitRemote(Guid repositoryId, RemoteTarget target)
    {
        var path = git.GetRepositoryPath(repositoryId);
        lock (GetRepositoryLock(path))
        {
            using var repository = OpenRepository(path);
            var fetchRefSpec = BuildFetchRefSpec(target.RemoteId);
            var remote = repository.Network.Remotes[target.RemoteName];
            if (remote is null)
            {
                repository.Network.Remotes.Add(target.RemoteName, target.CloneUrl.AbsoluteUri, fetchRefSpec);
                return;
            }

            repository.Network.Remotes.Update(target.RemoteName, updater =>
            {
                updater.Url = target.CloneUrl.AbsoluteUri;
                updater.PushUrl = target.CloneUrl.AbsoluteUri;
                updater.FetchRefSpecs = [fetchRefSpec];
                updater.PushRefSpecs = [];
            });
        }
    }

    private void RemoveGitRemote(Guid repositoryId, string remoteName)
    {
        var path = git.GetRepositoryPath(repositoryId);
        if (!Repository.IsValid(path))
            return;

        lock (GetRepositoryLock(path))
        {
            using var repository = OpenRepository(path);
            if (repository.Network.Remotes[remoteName] is not null)
                repository.Network.Remotes.Remove(remoteName);
            // Remote-tracking refs are intentionally not deleted.
        }
    }

    private void EnsureLocalGitRepository(Guid repositoryId, bool initializeIfMissing)
    {
        var path = git.GetRepositoryPath(repositoryId);
        EnsureHistoryPath(path);
        if (!Repository.IsValid(path))
        {
            if (!initializeIfMissing)
                throw RepositoryNotFound(repositoryId);
            git.InitializeRepository(repositoryId);
        }
    }

    private static Repository OpenRepository(string path)
    {
        if (!Directory.Exists(path) || !Repository.IsValid(path))
            throw new InvalidDataException("The local history repository is missing or invalid.");
        return new Repository(path);
    }

    private static void SetMainReference(Repository repository, Commit head)
    {
        var main = repository.Refs[MainReferenceName];
        if (main is null)
            main = repository.Refs.Add(MainReferenceName, head.Id);
        else
            repository.Refs.UpdateTarget(main, head.Id);
        repository.Refs.Add("HEAD", main, allowOverwrite: true);
    }

    private static Commit ResolveCloneHead(Repository repository, string defaultBranch)
    {
        var candidateNames = new[]
        {
            $"refs/remotes/origin/{defaultBranch}",
            $"refs/heads/{defaultBranch}",
        };
        foreach (var name in candidateNames)
        {
            var reference = repository.Refs[name];
            if (reference?.TargetIdentifier is string targetIdentifier &&
                repository.Lookup(targetIdentifier, ObjectType.Commit) is Commit commit)
                return commit;
        }

        var head = repository.Head.Tip;
        if (head is not null)
            return head;

        throw InvalidManifest("The imported Git repository has no commit at its default branch.");
    }

    private static void MaterializeImportTree(
        Tree tree,
        string root,
        string prefix,
        int depth,
        CancellationToken cancellationToken)
    {
        if (depth > 256)
            throw InvalidManifest("The imported Git tree is too deeply nested.");

        foreach (var entry in tree)
        {
            ValidateTreeSegment(entry.Name);
            var path = string.IsNullOrEmpty(prefix) ? entry.Name : $"{prefix}/{entry.Name}";
            ValidateGitPath(path);
            switch (entry.TargetType)
            {
                case TreeEntryTargetType.Tree when entry.Mode == Mode.Directory && entry.Target is Tree subtree:
                    MaterializeImportTree(subtree, root, path, depth + 1, cancellationToken);
                    break;
                case TreeEntryTargetType.Blob when entry.Mode == Mode.NonExecutableFile && entry.Target is Blob blob:
                    var destination = ResolveSafePath(root, path);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    EnsureNoReparsePoints(Path.GetDirectoryName(destination)!);
                    using (var source = blob.GetContentStream())
                    using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, FileOptions.SequentialScan))
                    {
                        var buffer = new byte[81_920];
                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var read = source.Read(buffer, 0, buffer.Length);
                            if (read == 0)
                                break;
                            output.Write(buffer, 0, read);
                        }
                    }
                    break;
                default:
                    throw InvalidManifest("The imported Git tree contains an unsupported entry type.");
            }
        }
    }

    private static VersionHistorySnapshotManifest ReadManifest(string extractionPath)
    {
        try
        {
            var manifestPath = ResolveSafePath(extractionPath, VersionHistorySnapshotContract.ManifestFileName);
            if (!File.Exists(manifestPath))
                throw InvalidManifest("The imported Git repository does not contain manifest.json at its root.");
            return JsonSerializer.Deserialize<VersionHistorySnapshotManifest>(
                       File.ReadAllBytes(manifestPath),
                       ManifestJsonOptions)
                   ?? throw new InvalidDataException("The snapshot manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw InvalidManifest("The imported manifest.json is malformed.", exception);
        }
    }

    private VersionHistorySnapshotArtifact ReadStrictArtifact(
        string extractionPath,
        Guid repositoryId,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        try
        {
            return snapshotReader.Read(
                extractionPath,
                repositoryId,
                projectId,
                new VersionHistorySnapshotReadOptions { IncludeSourceOriginalBlobs = true, CancellationToken = cancellationToken });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw InvalidManifest("The imported snapshot failed strict manifest and payload validation.", exception);
        }
    }

    private void AdoptIdenticalOrphanRepository(
        string destinationPath,
        string headCommitSha,
        string headTreeSha,
        VersionHistorySnapshotArtifact artifact,
        string extractionPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureHistoryPath(destinationPath);
        if (!Directory.Exists(destinationPath) || !Repository.IsValid(destinationPath))
            throw new ProjectVersionSyncException(
                ProjectVersionSyncErrorCode.RepositoryCollision,
                "The existing orphan history path is not a valid Git repository and was not adopted.");

        var existingExtractionPath = extractionPath + ".existing";
        EnsureHistoryPath(existingExtractionPath);
        try
        {
            using var repository = OpenRepository(destinationPath);
            var main = repository.Refs[MainReferenceName];
            if (main is not null
                && !string.Equals(main.TargetIdentifier, headCommitSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new ProjectVersionSyncException(
                    ProjectVersionSyncErrorCode.RepositoryCollision,
                    "The existing orphan history has a divergent local main head and was not overwritten.");
            }

            var commit = repository.Lookup(headCommitSha, ObjectType.Commit) as Commit
                ?? throw new ProjectVersionSyncException(
                    ProjectVersionSyncErrorCode.RepositoryCollision,
                    "The existing orphan history does not contain the validated clone head.");
            if (!string.Equals(commit.Tree.Sha, headTreeSha, StringComparison.OrdinalIgnoreCase))
                throw new ProjectVersionSyncException(
                    ProjectVersionSyncErrorCode.RepositoryCollision,
                    "The existing orphan history has a different tree for the validated clone head.");

            MaterializeImportTree(commit.Tree, existingExtractionPath, string.Empty, depth: 0, cancellationToken);
            var existingArtifact = ReadStrictArtifact(
                existingExtractionPath,
                artifact.Manifest.RepositoryId,
                artifact.Manifest.ProjectId,
                cancellationToken);
            if (!ManifestIdentityEquals(existingArtifact.Manifest, artifact.Manifest))
                throw new ProjectVersionSyncException(
                    ProjectVersionSyncErrorCode.RepositoryCollision,
                    "The existing orphan history does not contain an identical strict snapshot and was not adopted.");

            if (main is null)
                SetMainReference(repository, commit);
        }
        finally
        {
            DeleteTemporaryDirectory(existingExtractionPath);
        }
    }

    private static bool ManifestIdentityEquals(
        VersionHistorySnapshotManifest left,
        VersionHistorySnapshotManifest right)
    {
        if (!string.Equals(left.FormatId, right.FormatId, StringComparison.Ordinal)
            || left.SchemaVersion != right.SchemaVersion
            || left.RepositoryId != right.RepositoryId
            || left.ProjectId != right.ProjectId
            || !string.Equals(left.ContentHash, right.ContentHash, StringComparison.Ordinal)
            || !string.Equals(left.ManifestHash, right.ManifestHash, StringComparison.Ordinal)
            || !left.IncludedAreas.SequenceEqual(right.IncludedAreas, StringComparer.Ordinal)
            || left.Files.Count != right.Files.Count)
            return false;

        return left.Files.Zip(right.Files).All(pair =>
            string.Equals(pair.First.Path, pair.Second.Path, StringComparison.Ordinal)
            && pair.First.Length == pair.Second.Length
            && string.Equals(pair.First.Sha256, pair.Second.Sha256, StringComparison.Ordinal));
    }

    private string CreateStagingRepositoryPath()
    {
        var stagingRoot = Path.Combine(ValidateHistoryRoot(), ImportStagingDirectoryName);
        EnsureHistoryPath(stagingRoot);
        Directory.CreateDirectory(stagingRoot);
        EnsureNoReparsePoints(stagingRoot);
        var path = Path.Combine(stagingRoot, $"{Guid.NewGuid():N}.git");
        EnsureHistoryPath(path);
        return path;
    }

    private string CreateStagingExtractionPath(string stagingPath)
    {
        var path = stagingPath + ".extract";
        EnsureHistoryPath(path);
        Directory.CreateDirectory(path);
        EnsureNoReparsePoints(path);
        return path;
    }

    private void CloneRepository(RemoteTarget target, CredentialsHandler credentials, string stagingPath)
    {
        var fetchOptions = new FetchOptions { CredentialsProvider = credentials, Prune = false };
        var options = new CloneOptions(fetchOptions)
        {
            IsBare = true,
            Checkout = false,
        };
        try
        {
            Repository.Clone(target.CloneUrl.AbsoluteUri, stagingPath, options);
        }
        catch (Exception exception) when (exception is LibGit2SharpException or IOException)
        {
            throw new ProjectVersionSyncException(
                ProjectVersionSyncErrorCode.TransportFailure,
                "GitHub clone failed. Check the repository permission and network connection, then retry.",
                exception);
        }
    }

    private string ValidateHistoryRoot()
    {
        var root = Path.GetFullPath(git.HistoryRoot);
        EnsureNoReparsePoints(root);
        Directory.CreateDirectory(root);
        EnsureNoReparsePoints(root);
        return root;
    }

    private void EnsureHistoryPath(string path)
    {
        var root = ValidateHistoryRoot();
        var candidate = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathRooted(relative) || relative.Equals("..", PathComparison()) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison()) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", PathComparison()))
            throw new ProjectVersionSyncException(
                ProjectVersionSyncErrorCode.InvalidRequest,
                "The requested history path is outside the configured history root.");
        EnsureNoReparsePoints(candidate);
    }

    private static void EnsureNoReparsePoints(string path)
    {
        var current = new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new ProjectVersionSyncException(
                    ProjectVersionSyncErrorCode.InvalidRequest,
                    "History paths may not contain symbolic links or reparse points.");
            current = current.Parent;
        }
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                EnsureNoNestedReparsePoints(path);
                Directory.Delete(path, recursive: true);
            }
        }
        catch (InvalidDataException)
        {
            // Preserve a tampered staging tree rather than following a link.
        }
        catch (IOException)
        {
            // Cleanup is best effort; the staging path is random, validated,
            // and never used as a local identity.
        }
        catch (UnauthorizedAccessException)
        {
            // The operation result remains authoritative; a later cleanup can
            // remove the validated temporary directory.
        }
    }

    private static void EnsureNoNestedReparsePoints(string root)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var directory in current.EnumerateDirectories())
            {
                if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException($"Version-history staging may not contain reparse points: {directory.FullName}");
                pending.Push(directory);
            }

            foreach (var file in current.EnumerateFiles())
            {
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException($"Version-history staging may not contain reparse points: {file.FullName}");
            }
        }
    }

    private static RemoteTarget ValidateSelection(
        GitHubRepositorySelection selection,
        Guid? remoteId = null)
    {
        if (selection is null)
            throw InvalidRequest("A GitHub repository selection is required.");
        if (selection.ConnectionId == Guid.Empty)
            throw InvalidRequest("A reusable GitHub connection is required.");

        var repository = selection.Repository ?? throw InvalidRequest("A GitHub repository selection is required.");
        var remoteName = string.IsNullOrWhiteSpace(selection.RemoteName) ? DefaultRemoteName : selection.RemoteName.Trim();
        ValidateRemoteName(remoteName);
        ValidateRepositoryPart(repository.Owner, "owner");
        ValidateRepositoryPart(repository.Name, "repository name");
        if (repository.Id <= 0)
            throw InvalidRequest("The selected GitHub repository has no valid numeric identity.");
        if (!string.Equals(repository.FullName, $"{repository.Owner}/{repository.Name}", StringComparison.OrdinalIgnoreCase))
            throw InvalidRequest("The selected GitHub repository metadata is internally inconsistent.");
        var cloneUrl = ValidateCloneUrl(repository.CloneUrl, repository.Owner, repository.Name);
        var defaultBranch = string.IsNullOrWhiteSpace(repository.DefaultBranch)
            ? "main"
            : repository.DefaultBranch.Trim();
        ValidateBranchName(defaultBranch);

        return new RemoteTarget(
            selection.ConnectionId,
            null,
            remoteName,
            repository.Owner,
            repository.Name,
            repository.Id,
            cloneUrl,
            defaultBranch,
            BuildTrackingRef(remoteId, defaultBranch));
    }

    private static RemoteTarget ValidateStoredRemote(RemoteContext remote)
    {
        if (remote.GitHubConnectionId is not Guid connectionId || connectionId == Guid.Empty)
            throw new ProjectVersionSyncException(
                ProjectVersionSyncErrorCode.CredentialUnavailable,
                "The stored GitHub remote has no reusable connection.");
        ValidateRemoteName(remote.RemoteName);
        ValidateRepositoryPart(remote.Owner, "owner");
        ValidateRepositoryPart(remote.RepositoryName, "repository name");
        var cloneUrl = ValidateCloneUrl(remote.CloneUrl, remote.Owner, remote.RepositoryName);
        var defaultBranch = string.IsNullOrWhiteSpace(remote.DefaultBranch)
            ? "main"
            : remote.DefaultBranch.Trim();
        ValidateBranchName(defaultBranch);
        return new RemoteTarget(
            connectionId,
            remote.Id,
            remote.RemoteName,
            remote.Owner,
            remote.RepositoryName,
            remote.GitHubRepositoryId ?? 0,
            cloneUrl,
            defaultBranch,
            BuildTrackingRef(remote.Id, defaultBranch));
    }

    private static Uri ValidateCloneUrl(string? cloneUrl, string owner, string repositoryName)
    {
        if (string.IsNullOrWhiteSpace(cloneUrl) ||
            !Uri.TryCreate(cloneUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrWhiteSpace(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw InvalidRemote("GitHub clone URLs must use the trusted github.com HTTPS host without credentials, query strings, or fragments.");

        var path = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2 ||
            !string.Equals(segments[0], owner, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(segments[1], repositoryName, StringComparison.OrdinalIgnoreCase))
            throw InvalidRemote("The clone URL does not identify the selected GitHub owner and repository.");

        return uri;
    }

    private static void ValidateRepositoryPart(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) ||
            value.Contains('/', StringComparison.Ordinal) || value.Contains('\\', StringComparison.Ordinal) ||
            value.Contains('\0', StringComparison.Ordinal))
            throw InvalidRequest($"The selected GitHub {label} is invalid.");
    }

    private static void ValidateRemoteName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.Contains("..", StringComparison.Ordinal) ||
            value.StartsWith('.') || value.EndsWith('.') || value.Any(char.IsControl) ||
            value.Contains('/', StringComparison.Ordinal) || value.Contains('\\', StringComparison.Ordinal) ||
            value.Any(character => char.IsWhiteSpace(character) || character is ':' or '@' or '~' or '^'))
            throw InvalidRequest("The Git remote name is not a safe local ref segment.");
    }

    private static void ValidateBranchName(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch) || branch.Any(char.IsControl) || branch.Contains('\\', StringComparison.Ordinal) ||
            branch.Contains("..", StringComparison.Ordinal) || branch.Contains("//", StringComparison.Ordinal) ||
            branch.Contains("@{", StringComparison.Ordinal) || branch.StartsWith('/') || branch.EndsWith('/') ||
            branch.StartsWith('.') || branch.EndsWith('.') ||
            branch.Any(character => char.IsWhiteSpace(character) || character is ':' or '?' or '*' or '[' or '~' or '^'))
            throw InvalidRemote("The configured GitHub default branch is not a safe Git ref path.");
    }

    private static string BuildTrackingRef(Guid? remoteId, string defaultBranch) =>
        remoteId is Guid id
            ? $"refs/remotes/lorekeeper/{id:N}/{defaultBranch}"
            : string.Empty;

    private static string BuildFetchRefSpec(Guid? remoteId) =>
        remoteId is Guid id
            ? $"+refs/heads/*:refs/remotes/lorekeeper/{id:N}/*"
            : throw InvalidRemote("The attached remote has no stable local identity.");

    private static void ValidateTreeSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment) || segment is "." or ".." or ".git" || segment.Contains('/') ||
            segment.Contains('\\') || segment.Any(char.IsControl) || segment.EndsWith('.') || segment.EndsWith(' ') ||
            IsWindowsDeviceName(segment) ||
            segment.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0)
            throw InvalidManifest("The imported Git tree contains an unsafe path segment.");
    }

    private static bool IsWindowsDeviceName(string segment)
    {
        var stem = segment.Split('.', 2)[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase))
            return true;
        return stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && stem[3] is >= '1' and <= '9';
    }

    private static void ValidateGitPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.EndsWith('/') || path.Contains('\\') ||
            path.Contains('\0') || path.Contains("//", StringComparison.Ordinal))
            throw InvalidManifest("The imported Git tree contains an unsafe path.");
        foreach (var segment in path.Split('/'))
            ValidateTreeSegment(segment);
    }

    private static string ResolveSafePath(string root, string relativePath)
    {
        ValidateGitPath(relativePath);
        var fullRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        var normalizedRoot = fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw InvalidManifest("The imported path escapes the staging directory.");
        return candidate;
    }

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static object GetRepositoryLock(string path) =>
        RepositoryLocks.GetOrAdd(path, static _ => new object());

    private static ProjectVersionSyncStatus CreateComparisonStatus(
        Guid repositoryId,
        Guid? remoteId,
        RemoteTarget target,
        GitHistoryComparison comparison,
        ProjectVersionSyncDisposition disposition,
        bool localUpdated,
        bool remoteUpdated,
        string message) =>
        CreateStatus(
            repositoryId,
            remoteId,
            target,
            comparison.CurrentCommitSha,
            comparison.CandidateCommitSha,
            comparison.MergeBaseSha,
            comparison.Relation,
            disposition,
            localUpdated,
            remoteUpdated,
            message);

    private static ProjectVersionSyncStatus CreateStatus(
        Guid repositoryId,
        Guid? remoteId,
        RemoteTarget target,
        string? localCommitSha,
        string? remoteCommitSha,
        string? mergeBaseSha,
        GitHistoryRelation relation,
        ProjectVersionSyncDisposition disposition,
        bool localUpdated,
        bool remoteUpdated,
        string message) => new(
        repositoryId,
        remoteId,
        target.RemoteName,
        target.DefaultBranch,
        target.TrackingRef,
        localCommitSha,
        remoteCommitSha,
        mergeBaseSha,
        relation,
        disposition,
        localUpdated,
        remoteUpdated,
        message);

    private static ProjectVersionSyncDisposition ProjectVersionSyncDispositionFor(GitHistoryRelation relation) =>
        relation switch
        {
            GitHistoryRelation.Empty => ProjectVersionSyncDisposition.Empty,
            GitHistoryRelation.Identical => ProjectVersionSyncDisposition.AlreadySynchronized,
            GitHistoryRelation.CandidateFastForward => ProjectVersionSyncDisposition.RemoteAhead,
            GitHistoryRelation.CurrentFastForward => ProjectVersionSyncDisposition.LocalAhead,
            GitHistoryRelation.Diverged => ProjectVersionSyncDisposition.Diverged,
            GitHistoryRelation.Unrelated => ProjectVersionSyncDisposition.Unrelated,
            _ => ProjectVersionSyncDisposition.Empty,
        };

    private static string ErrorCodeFor(Exception exception) => exception switch
    {
        ProjectVersionSyncException sync => sync.Code.ToString(),
        GitHubConnectionException github => $"GITHUB_{github.Code}",
        OperationCanceledException => ProjectVersionSyncErrorCode.Canceled.ToString(),
        _ => ProjectVersionSyncErrorCode.OperationFailed.ToString(),
    };

    private static string SafeErrorMessage(Exception exception) => exception switch
    {
        ProjectVersionSyncException sync => sync.Message,
        GitHubConnectionException => "The GitHub connection operation failed. Reauthorize GitHub or retry the sync operation.",
        OperationCanceledException => "The version-history operation was canceled.",
        _ => "The version-history operation failed. Retry the operation after reviewing the local and remote history.",
    };

    private static Exception WrapCloneException(Exception exception) => exception switch
    {
        ProjectVersionSyncException => exception,
        OperationCanceledException => exception,
        _ => new ProjectVersionSyncException(
            ProjectVersionSyncErrorCode.OperationFailed,
            "The imported repository could not be validated and was not installed.",
            exception),
    };

    private static ProjectVersionSyncException RepositoryNotFound(Guid repositoryId) => new(
        ProjectVersionSyncErrorCode.RepositoryNotFound,
        $"The local version-history repository '{repositoryId:N}' was not found.");

    private static ProjectVersionSyncException RemoteNotFound(Guid remoteId) => new(
        ProjectVersionSyncErrorCode.RemoteNotFound,
        $"The project GitHub remote '{remoteId:N}' was not found.");

    private static ProjectVersionSyncException InvalidRequest(string message, Exception? inner = null) => new(
        ProjectVersionSyncErrorCode.InvalidRequest,
        message,
        inner);

    private static ProjectVersionSyncException InvalidRemote(string message, Exception? inner = null) => new(
        ProjectVersionSyncErrorCode.InvalidRemote,
        message,
        inner);

    private static ProjectVersionSyncException InvalidManifest(string message, Exception? inner = null) => new(
        ProjectVersionSyncErrorCode.InvalidManifest,
        message,
        inner);

    private sealed record RemoteTarget(
        Guid ConnectionId,
        Guid? RemoteId,
        string RemoteName,
        string Owner,
        string RepositoryName,
        long GitHubRepositoryId,
        Uri CloneUrl,
        string DefaultBranch,
        string TrackingRef);

    private sealed record AutoPushState(
        ProjectVersionOperationStatus Status,
        string? ErrorMessage);

    private static ProjectVersionSyncDisposition AutomaticDisposition(
        AutoPushState? state,
        ProjectVersionSyncDisposition fallback) => state?.Status switch
        {
            ProjectVersionOperationStatus.Pending or ProjectVersionOperationStatus.Running => ProjectVersionSyncDisposition.Syncing,
            ProjectVersionOperationStatus.Failed or ProjectVersionOperationStatus.Canceled => ProjectVersionSyncDisposition.Failed,
            _ => fallback,
        };

    private static string AutomaticMessage(
        AutoPushState? state,
        string fallback) => state?.Status switch
        {
            ProjectVersionOperationStatus.Pending => "A checkpoint is queued for automatic push to this attached remote.",
            ProjectVersionOperationStatus.Running => "Automatic push is synchronizing this attached remote.",
            ProjectVersionOperationStatus.Failed or ProjectVersionOperationStatus.Canceled =>
                $"Automatic push failed: {state.ErrorMessage ?? "retry the operation after reviewing the remote history."}",
            _ => fallback,
        };

    private sealed record RemoteContext(
        Guid Id,
        Guid ProjectVersionRepositoryId,
        Guid? GitHubConnectionId,
        string RemoteName,
        string Owner,
        string RepositoryName,
        long? GitHubRepositoryId,
        string? CloneUrl,
        string? DefaultBranch)
    {
        public RemoteContext(ProjectGitRemote remote)
            : this(
                remote.Id,
                remote.ProjectVersionRepositoryId,
                remote.GitHubConnectionId,
                remote.RemoteName,
                remote.Owner,
                remote.RepositoryName,
                remote.GitHubRepositoryId,
                remote.CloneUrl,
                remote.DefaultBranch)
        {
        }

        public RemoteTarget ToTarget() => ValidateStoredRemote(this);
    }
}
