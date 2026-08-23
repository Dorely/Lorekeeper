using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.VersionHistory.GitHub;
using Lorekeeper.VersionHistory.Services;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.VersionHistory.Sync;

/// <summary>
/// Claims and processes durable automatic push journal rows. Network work is
/// performed while the owning project's mutation lease is held; no EF context
/// or scoped service is retained across a network request.
/// </summary>
public sealed class ProjectVersionAutoPushService(
    IAppDatabaseOperationFactory database,
    IProjectMutationCoordinator projectMutations,
    ProjectVersionSyncService sync,
    ProjectVersionHistoryUiEvents historyEvents)
{
    public async Task ProcessPendingAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var candidate = await FindCandidateAsync(cancellationToken);
            if (candidate is null)
                return;

            await ProcessOneAsync(candidate, cancellationToken);
        }
    }

    private async Task ProcessOneAsync(
        AutoPushCandidate candidate,
        CancellationToken cancellationToken)
    {
        await using var projectLease = await projectMutations.AcquireAsync(
            candidate.ProjectId,
            cancellationToken);

        var claimed = await ClaimAsync(candidate, cancellationToken);
        if (!claimed)
            return;

        historyEvents.PublishRemoteSyncChanged(candidate.ProjectId);
        try
        {
            var status = await sync.PushAutomaticUnderLeaseAsync(
                candidate.RemoteId,
                candidate.TargetCommitSha,
                cancellationToken);
            if (status.Disposition is ProjectVersionSyncDisposition.Pushed
                or ProjectVersionSyncDisposition.AlreadySynchronized)
            {
                await CompleteAsync(
                    candidate.OperationId,
                    ProjectVersionOperationStatus.Succeeded,
                    isResumable: false,
                    null,
                    null);
            }
            else if (status.Disposition == ProjectVersionSyncDisposition.PushSkipped)
            {
                await CompleteAsync(
                    candidate.OperationId,
                    ProjectVersionOperationStatus.Canceled,
                    isResumable: false,
                    null,
                    status.Message);
            }
            else
            {
                await CompleteAsync(
                    candidate.OperationId,
                    ProjectVersionOperationStatus.Failed,
                    isResumable: true,
                    ProjectVersionSyncErrorCode.HistoryConflict.ToString(),
                    status.Message);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Leave the claimed row Running. It is an explicit interrupted
            // intent and will be picked up by the next startup pass.
        }
        catch (Exception exception)
        {
            await CompleteAsync(
                candidate.OperationId,
                ProjectVersionOperationStatus.Failed,
                isResumable: true,
                ErrorCodeFor(exception),
                SafeErrorMessage(exception));
        }
        finally
        {
            historyEvents.PublishRemoteSyncChanged(candidate.ProjectId);
        }
    }

    private async Task<AutoPushCandidate?> FindCandidateAsync(CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        return await operation.Db.ProjectVersionOperations
            .AsNoTracking()
            .Where(item => item.Kind == ProjectVersionOperationKind.AutoPush
                && item.TargetCommitSha != null
                && item.ProjectGitRemoteId != null
                && (item.Status == ProjectVersionOperationStatus.Pending
                    || item.Status == ProjectVersionOperationStatus.Running))
            .OrderBy(item => item.CreatedAt)
            .Select(item => new AutoPushCandidate(
                item.Id,
                item.ProjectVersionRepositoryId,
                item.Repository.ProjectId,
                item.ProjectGitRemoteId!.Value,
                item.TargetCommitSha!))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<bool> ClaimAsync(
        AutoPushCandidate candidate,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var row = await operation.Db.ProjectVersionOperations
            .SingleOrDefaultAsync(item => item.Id == candidate.OperationId, cancellationToken);
        if (row is null
            || row.Kind != ProjectVersionOperationKind.AutoPush
            || row.Status is not (ProjectVersionOperationStatus.Pending or ProjectVersionOperationStatus.Running)
            || row.ProjectGitRemoteId != candidate.RemoteId
            || !string.Equals(row.TargetCommitSha, candidate.TargetCommitSha, StringComparison.Ordinal))
            return false;

        var remoteExists = await operation.Db.ProjectGitRemotes
            .AsNoTracking()
            .AnyAsync(remote => remote.Id == candidate.RemoteId
                && remote.ProjectVersionRepositoryId == candidate.RepositoryId,
                cancellationToken);
        if (!remoteExists)
        {
            row.Status = ProjectVersionOperationStatus.Canceled;
            row.IsResumable = false;
            row.ErrorCode = ProjectVersionSyncErrorCode.RemoteNotFound.ToString();
            row.ErrorMessage = "The attached remote was removed before its automatic push could run.";
            row.CompletedAt = DateTime.UtcNow;
            row.HeartbeatAt = row.CompletedAt;
            row.UpdatedAt = row.CompletedAt.Value;
            await operation.SaveChangesAsync(cancellationToken);
            return false;
        }

        var now = DateTime.UtcNow;
        row.Status = ProjectVersionOperationStatus.Running;
        row.IsResumable = true;
        row.AttemptCount++;
        row.StartedAt = now;
        row.HeartbeatAt = now;
        row.CompletedAt = null;
        row.ErrorCode = null;
        row.ErrorMessage = null;
        row.UpdatedAt = now;
        await operation.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task CompleteAsync(
        Guid operationId,
        ProjectVersionOperationStatus status,
        bool isResumable,
        string? errorCode,
        string? errorMessage)
    {
        await using var operation = await database.OpenWriteAsync(CancellationToken.None);
        var row = await operation.Db.ProjectVersionOperations
            .SingleOrDefaultAsync(item => item.Id == operationId, CancellationToken.None);
        if (row is null)
            return;

        var now = DateTime.UtcNow;
        row.Status = status;
        row.IsResumable = isResumable;
        row.ErrorCode = errorCode;
        row.ErrorMessage = errorMessage;
        row.CompletedAt = now;
        row.HeartbeatAt = now;
        row.UpdatedAt = now;
        await operation.SaveChangesAsync(CancellationToken.None);
    }

    private static string ErrorCodeFor(Exception exception) => exception switch
    {
        ProjectVersionSyncException sync => sync.Code.ToString(),
        GitHubConnectionException github => $"GITHUB_{github.Code}",
        _ => ProjectVersionSyncErrorCode.OperationFailed.ToString(),
    };

    private static string SafeErrorMessage(Exception exception) => exception switch
    {
        ProjectVersionSyncException sync => sync.Message,
        GitHubConnectionException => "The GitHub connection is unavailable. Reauthorize GitHub before retrying automatic push.",
        _ => "Automatic push failed. Review the remote history and retry the operation.",
    };

    private sealed record AutoPushCandidate(
        Guid OperationId,
        Guid RepositoryId,
        Guid ProjectId,
        Guid RemoteId,
        string TargetCommitSha);
}
