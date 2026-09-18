using Lorekeeper.Persistence;
using Lorekeeper.Models;
using Lorekeeper.VersionHistory.Git;
using Lorekeeper.VersionHistory.Restore;
using Lorekeeper.VersionHistory.Services;
using Lorekeeper.VersionHistory.Snapshots;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.VersionHistory.Sync;

/// <summary>
/// Applies a fetched remote head to the live project. The current workspace
/// must be clean, the safety checkpoint is deduplicated on the old head, and
/// the sync service performs the compare-and-swap fast-forward before the
/// restore service atomically checks out the validated target payload.
/// </summary>
public sealed class ProjectVersionRemoteUpdateService(
    ProjectVersionSyncService sync,
    IProjectVersionHistoryService history,
    ProjectVersionRestoreService restore,
    IGitRepositoryStore git,
    IVersionHistorySnapshotWriter snapshotWriter,
    IVersionHistorySnapshotReader snapshotReader,
    IAppDatabaseOperationFactory database,
    IProjectMutationCoordinator projectMutations) : IProjectVersionRemoteUpdateService
{
    private const string CheckoutStagingDirectoryName = ".checkout-staging";

    public async Task<VersionHistoryRemoteUpdateResult> FastForwardAndApplyAsync(
        Guid remoteId,
        CancellationToken cancellationToken = default)
    {
        if (remoteId == Guid.Empty)
            throw new ProjectVersionRemoteUpdateException(
                ProjectVersionRemoteUpdateErrorCode.InvalidRequest,
                "A project GitHub remote identity is required.");

        var identity = await LoadRemoteIdentityAsync(remoteId, cancellationToken);
        var before = await history.GetStatusAsync(
            identity.ProjectId,
            includeCurrentSnapshotHash: true,
            cancellationToken: cancellationToken)
            ?? throw new ProjectVersionRemoteUpdateException(
                ProjectVersionRemoteUpdateErrorCode.InvalidRequest,
                "The project has no local version-history repository.");

        if (before.Repository.IsDirty)
            throw new ProjectVersionRemoteUpdateException(
                ProjectVersionRemoteUpdateErrorCode.DirtyWorkspace,
                "Remote checkout is blocked while the project has uncheckpointed changes. Create a checkpoint before updating from GitHub.");
        if (string.IsNullOrWhiteSpace(before.Repository.HeadCommitSha))
            throw new ProjectVersionRemoteUpdateException(
                ProjectVersionRemoteUpdateErrorCode.HistoryConflict,
                "The local project has no committed history. Import the remote repository into a new project before attaching it here.");

        cancellationToken.ThrowIfCancellationRequested();
        var safetyCheckpoint = await history.CreateCheckpointAsync(
            identity.ProjectId,
            ProjectVersionCheckpointKind.Manual,
            "Safety checkpoint before applying a GitHub fast-forward",
            requestKey: $"sync:checkout:{remoteId:N}:{Guid.NewGuid():N}",
            cancellationToken: cancellationToken);

        var afterCheckpoint = await history.GetStatusAsync(
            identity.ProjectId,
            includeCurrentSnapshotHash: true,
            cancellationToken: cancellationToken)
            ?? throw new ProjectVersionRemoteUpdateException(
                ProjectVersionRemoteUpdateErrorCode.HistoryConflict,
                "The project version-history repository disappeared while creating the safety checkpoint.");
        if (afterCheckpoint.Repository.IsDirty)
            throw new ProjectVersionRemoteUpdateException(
                ProjectVersionRemoteUpdateErrorCode.DirtyWorkspace,
                "The project changed while its safety checkpoint was being created. Remote checkout was refused; retry after the workspace is stable.");
        if (!string.Equals(
                before.CurrentContentHash,
                afterCheckpoint.CurrentContentHash,
                StringComparison.Ordinal))
        {
            throw new ProjectVersionRemoteUpdateException(
                ProjectVersionRemoteUpdateErrorCode.DirtyWorkspace,
                "The project changed while its safety checkpoint was being created. Remote checkout was refused; retry after the workspace is stable.");
        }
        if (!string.Equals(
                before.Repository.HeadCommitSha,
                afterCheckpoint.Repository.HeadCommitSha,
                StringComparison.Ordinal))
        {
            throw new ProjectVersionRemoteUpdateException(
                ProjectVersionRemoteUpdateErrorCode.HistoryConflict,
                "The local history head changed while preparing the remote checkout. The fetched remote was not applied.");
        }

        await using var mutationLease = await projectMutations.AcquireAsync(identity.ProjectId, cancellationToken);
        await ValidatePreFastForwardStateAsync(identity, before, cancellationToken);

        ProjectVersionSyncStatus status;
        try
        {
            status = await sync.FastForwardLocalUnderLeaseAsync(remoteId, cancellationToken);
        }
        catch
        {
            await RollbackIfAdvancedAsync(identity, before.Repository, CancellationToken.None);
            throw;
        }

        if (status.LocalCommitSha is null
            || string.Equals(status.LocalCommitSha, before.Repository.HeadCommitSha, StringComparison.Ordinal)
            || status.Disposition is not ProjectVersionSyncDisposition.FastForwarded)
        {
            return new VersionHistoryRemoteUpdateResult(
                identity.ProjectId,
                identity.ProjectVersionRepositoryId,
                status,
                safetyCheckpoint,
                null,
                []);
        }

        try
        {
            var liveContentHash = await CaptureCurrentContentHashAsync(
                identity.ProjectId,
                identity.ProjectVersionRepositoryId,
                CancellationToken.None);

            if (string.IsNullOrWhiteSpace(before.Repository.HeadContentHash)
                || string.IsNullOrWhiteSpace(before.CurrentContentHash)
                || string.IsNullOrWhiteSpace(liveContentHash))
            {
                await RollbackPreCommitCheckoutAsync(
                    identity,
                    before.Repository,
                    status.LocalCommitSha,
                    CancellationToken.None);
                throw new ProjectVersionRemoteUpdateException(
                    ProjectVersionRemoteUpdateErrorCode.HistoryConflict,
                    "The live project snapshot hash could not be recaptured after the remote fast-forward. The remote snapshot was not applied.");
            }

            if (!string.Equals(
                    liveContentHash,
                    before.CurrentContentHash,
                    StringComparison.Ordinal))
            {
                await RollbackPreCommitCheckoutAsync(
                    identity,
                    before.Repository,
                    status.LocalCommitSha,
                    CancellationToken.None);
                throw new ProjectVersionRemoteUpdateException(
                    ProjectVersionRemoteUpdateErrorCode.DirtyWorkspace,
                    "The project changed while GitHub history was being fast-forwarded. The remote snapshot was not applied; retry after the workspace is clean.");
            }
        }
        catch (ProjectVersionRemoteUpdateException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await RollbackPreCommitCheckoutAsync(
                identity,
                before.Repository,
                status.LocalCommitSha,
                CancellationToken.None);
            throw new ProjectVersionRemoteUpdateException(
                ProjectVersionRemoteUpdateErrorCode.HistoryConflict,
                "The live project state could not be recaptured after the remote fast-forward. The remote snapshot was not applied.",
                exception);
        }

        try
        {
            var checkout = await CheckoutHeadAsync(
                identity.ProjectId,
                identity.ProjectVersionRepositoryId,
                status.LocalCommitSha,
                before.Repository,
                CancellationToken.None);
            return new VersionHistoryRemoteUpdateResult(
                identity.ProjectId,
                identity.ProjectVersionRepositoryId,
                status,
                safetyCheckpoint,
                checkout,
                checkout.Warnings);
        }
        catch (OperationCanceledException)
        {
            await RollbackPreCommitCheckoutAsync(
                identity,
                before.Repository,
                status.LocalCommitSha,
                CancellationToken.None);
            throw;
        }
        catch (VersionHistoryRestoreException exception)
        {
            await RollbackPreCommitCheckoutAsync(
                identity,
                before.Repository,
                status.LocalCommitSha,
                CancellationToken.None);

            throw new ProjectVersionRemoteUpdateException(
                ProjectVersionRemoteUpdateErrorCode.CheckoutFailed,
                "The validated remote snapshot could not be applied. The local Git head and cached repository metadata were rolled back to the safety checkpoint; retry the remote checkout.",
                exception);
        }
        catch (Exception exception)
        {
            await RollbackPreCommitCheckoutAsync(
                identity,
                before.Repository,
                status.LocalCommitSha,
                CancellationToken.None);

            throw new ProjectVersionRemoteUpdateException(
                ProjectVersionRemoteUpdateErrorCode.CheckoutFailed,
                "The validated remote snapshot could not be applied. The local Git head and cached repository metadata were rolled back to the safety checkpoint; retry the remote checkout.",
                exception);
        }
    }

    private async Task ValidatePreFastForwardStateAsync(
        RemoteIdentity identity,
        ProjectVersionStatusView expectedStatus,
        CancellationToken cancellationToken)
    {
        var expectedRepository = expectedStatus.Repository;
        await using (var operation = await database.OpenReadAsync(cancellationToken))
        {
            var repository = await operation.Db.ProjectVersionRepositories
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == identity.ProjectVersionRepositoryId, cancellationToken);
            if (repository is null
                || repository.ProjectId != identity.ProjectId
                || !string.Equals(repository.HeadCommitSha, expectedRepository.HeadCommitSha, StringComparison.Ordinal)
                || !string.Equals(repository.HeadContentHash, expectedRepository.HeadContentHash, StringComparison.Ordinal))
            {
                throw new ProjectVersionRemoteUpdateException(
                    ProjectVersionRemoteUpdateErrorCode.HistoryConflict,
                    "The local history cache changed before the remote fast-forward could begin.");
            }
        }

        if (!string.Equals(
                git.GetHead(identity.ProjectVersionRepositoryId).CommitSha,
                expectedRepository.HeadCommitSha,
                StringComparison.Ordinal))
        {
            throw new ProjectVersionRemoteUpdateException(
                ProjectVersionRemoteUpdateErrorCode.HistoryConflict,
                "The local Git head changed before the remote fast-forward could begin.");
        }

        var liveContentHash = await CaptureCurrentContentHashAsync(
            identity.ProjectId,
            identity.ProjectVersionRepositoryId,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(expectedStatus.CurrentContentHash)
            || !string.Equals(liveContentHash, expectedStatus.CurrentContentHash, StringComparison.Ordinal))
        {
            throw new ProjectVersionRemoteUpdateException(
                ProjectVersionRemoteUpdateErrorCode.DirtyWorkspace,
                "The project changed before the remote fast-forward could begin. Create a checkpoint and retry.");
        }
    }

    private async Task<string> CaptureCurrentContentHashAsync(
        Guid projectId,
        Guid repositoryId,
        CancellationToken cancellationToken)
    {
        var stagingPath = CreateCheckoutStagingPath();
        try
        {
            _ = await snapshotWriter.WriteAsync(repositoryId, projectId, stagingPath, cancellationToken);
            return snapshotReader.Read(stagingPath, repositoryId, projectId,
                new() { IncludeAssetData = false, IncludeSourceDetails = false, CancellationToken = cancellationToken }).Manifest.ContentHash;
        }
        finally
        {
            DeleteCheckoutStagingPath(stagingPath);
        }
    }

    private async Task RollbackIfAdvancedAsync(
        RemoteIdentity identity,
        ProjectVersionRepositoryView previousRepository,
        CancellationToken cancellationToken)
    {
        var currentCommitSha = git.GetHead(identity.ProjectVersionRepositoryId).CommitSha;
        if (string.IsNullOrWhiteSpace(currentCommitSha)
            || string.Equals(currentCommitSha, previousRepository.HeadCommitSha, StringComparison.Ordinal))
        {
            return;
        }

        await RollbackPreCommitCheckoutAsync(
            identity,
            previousRepository,
            currentCommitSha,
            cancellationToken);
    }

    private async Task RollbackPreCommitCheckoutAsync(
        RemoteIdentity identity,
        ProjectVersionRepositoryView previousRepository,
        string targetCommitSha,
        CancellationToken cancellationToken)
    {
        try
        {
            var currentCommitSha = git.GetHead(identity.ProjectVersionRepositoryId).CommitSha;
            if (string.Equals(currentCommitSha, targetCommitSha, StringComparison.Ordinal))
            {
                git.RollbackMain(
                    identity.ProjectVersionRepositoryId,
                    targetCommitSha,
                    previousRepository.HeadCommitSha!);
            }
            else if (!string.Equals(
                         currentCommitSha,
                         previousRepository.HeadCommitSha,
                         StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The local Git head changed during failed remote checkout and could not be safely rolled back.");
            }

            await RestoreCachedRepositoryHeadAsync(
                identity.ProjectId,
                identity.ProjectVersionRepositoryId,
                previousRepository,
                targetCommitSha,
                cancellationToken);
        }
        catch (Exception rollbackException)
        {
            throw new ProjectVersionRemoteUpdateException(
                ProjectVersionRemoteUpdateErrorCode.CheckoutFailed,
                "The remote checkout failed before its database apply completed, but the local Git head could not be rolled back safely. Manual reconciliation is required before another sync.",
                rollbackException);
        }
    }

    private async Task RestoreCachedRepositoryHeadAsync(
        Guid projectId,
        Guid repositoryId,
        ProjectVersionRepositoryView previousRepository,
        string targetCommitSha,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenWriteAsync(projectId, cancellationToken);
        var repository = await operation.Db.ProjectVersionRepositories
            .SingleOrDefaultAsync(item => item.Id == repositoryId, cancellationToken);
        if (repository is null)
            return;
        if (!string.Equals(repository.HeadCommitSha, targetCommitSha, StringComparison.Ordinal))
            return;

        repository.HeadCommitSha = previousRepository.HeadCommitSha;
        repository.HeadContentHash = previousRepository.HeadContentHash;
        repository.LastCheckpointRevision = previousRepository.LastCheckpointRevision;
        repository.LastCheckpointContentHash = previousRepository.LastCheckpointContentHash;
        repository.LastCheckpointAt = previousRepository.LastCheckpointAt;
        repository.UpdatedAt = DateTime.UtcNow;
        await operation.SaveChangesAsync(cancellationToken);
    }

    private async Task<VersionHistoryCheckoutResult> CheckoutHeadAsync(
        Guid projectId,
        Guid repositoryId,
        string headCommitSha,
        ProjectVersionRepositoryView previousRepository,
        CancellationToken cancellationToken)
    {
        var commit = git.GetCommitMetadata(repositoryId, headCommitSha);
        var stagingPath = CreateCheckoutStagingPath();
        try
        {
            VersionHistorySnapshotArtifact artifact;
            try
            {
                git.MaterializeTree(repositoryId, stagingPath, headCommitSha, cancellationToken);
                artifact = snapshotReader.Read(
                    stagingPath,
                    repositoryId,
                    projectId,
                    new VersionHistorySnapshotReadOptions { IncludeSourceOriginalBlobs = true, CancellationToken = cancellationToken });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new VersionHistoryRestoreException(
                    "CheckoutHeadMismatch",
                    $"The fetched Git head did not contain a valid version-history snapshot: {exception.Message}");
            }

            await UpdateCachedRepositoryHeadAsync(
                projectId,
                repositoryId,
                previousRepository,
                headCommitSha,
                artifact.Manifest.ContentHash,
                cancellationToken);
            return await restore.CheckoutValidatedSnapshotAsync(
                new VersionHistoryValidatedProjectCheckout(
                    projectId,
                    artifact,
                    commit.Sha,
                    commit.TreeSha,
                    commit.ParentShas.FirstOrDefault(),
                    "Applied GitHub fast-forward snapshot"),
                cancellationToken);
        }
        finally
        {
            DeleteCheckoutStagingPath(stagingPath);
        }
    }

    private async Task UpdateCachedRepositoryHeadAsync(
        Guid projectId,
        Guid repositoryId,
        ProjectVersionRepositoryView previousRepository,
        string targetCommitSha,
        string targetContentHash,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenWriteAsync(projectId, cancellationToken);
        var repository = await operation.Db.ProjectVersionRepositories
            .SingleOrDefaultAsync(item => item.Id == repositoryId, cancellationToken)
            ?? throw new VersionHistoryRestoreException(
                "RepositoryNotFound",
                "The local version-history repository disappeared before the remote snapshot could be applied.");

        if (!string.Equals(repository.HeadCommitSha, previousRepository.HeadCommitSha, StringComparison.Ordinal)
            || !string.Equals(repository.HeadContentHash, previousRepository.HeadContentHash, StringComparison.Ordinal))
        {
            throw new VersionHistoryRestoreException(
                "CheckoutHeadMismatch",
                "The cached local version-history head changed before the remote snapshot could be applied.");
        }

        repository.HeadCommitSha = targetCommitSha;
        repository.HeadContentHash = targetContentHash;
        repository.UpdatedAt = DateTime.UtcNow;
        await operation.SaveChangesAsync(cancellationToken);
    }

    private async Task<RemoteIdentity> LoadRemoteIdentityAsync(
        Guid remoteId,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        var identity = await (
            from remote in operation.Db.ProjectGitRemotes.AsNoTracking()
            join repository in operation.Db.ProjectVersionRepositories.AsNoTracking()
                on remote.ProjectVersionRepositoryId equals repository.Id
            where remote.Id == remoteId
            select new RemoteIdentity(repository.ProjectId, repository.Id))
            .SingleOrDefaultAsync(cancellationToken);
        return identity is null
            ? throw new ProjectVersionRemoteUpdateException(
                ProjectVersionRemoteUpdateErrorCode.InvalidRequest,
                $"The project GitHub remote '{remoteId:N}' was not found.")
            : identity;
    }

    private string CreateCheckoutStagingPath()
    {
        var root = Path.GetFullPath(git.HistoryRoot);
        EnsureNoReparsePoints(root);
        Directory.CreateDirectory(root);
        EnsureNoReparsePoints(root);

        var stagingRoot = Path.Combine(root, CheckoutStagingDirectoryName);
        EnsureInsideHistoryRoot(root, stagingRoot);
        Directory.CreateDirectory(stagingRoot);
        EnsureNoReparsePoints(stagingRoot);

        var path = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        EnsureInsideHistoryRoot(root, path);
        Directory.CreateDirectory(path);
        EnsureNoReparsePoints(path);
        return path;
    }

    private static void EnsureInsideHistoryRoot(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(path);
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The checkout staging path escaped the configured history root.");
    }

    private static void EnsureNoReparsePoints(string path)
    {
        var current = new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Checkout staging paths may not contain reparse points.");
            current = current.Parent;
        }
    }

    private static void DeleteCheckoutStagingPath(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!Directory.Exists(fullPath))
                return;
            EnsureNoReparsePoints(fullPath);
            EnsureNoNestedReparsePoints(fullPath);
            Directory.Delete(fullPath, recursive: true);
        }
        catch (InvalidDataException)
        {
            // Preserve a tampered staging tree rather than following a link.
        }
        catch (IOException)
        {
            // The path is random and app-owned; a later run can reconcile it.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the checkout result; cleanup remains recoverable.
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
                    throw new InvalidDataException($"Checkout staging may not contain reparse points: {directory.FullName}");
                pending.Push(directory);
            }

            foreach (var file in current.EnumerateFiles())
            {
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException($"Checkout staging may not contain reparse points: {file.FullName}");
            }
        }
    }

    private sealed record RemoteIdentity(Guid ProjectId, Guid ProjectVersionRepositoryId);
}
