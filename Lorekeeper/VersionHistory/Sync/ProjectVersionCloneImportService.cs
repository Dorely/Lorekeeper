using Lorekeeper.Persistence;
using Lorekeeper.VersionHistory.Restore;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.VersionHistory.Sync;

/// <summary>
/// Coordinates the validated Git clone, exact snapshot import, and origin
/// remote attachment. The clone service owns transport validation and the
/// restore service owns the database import; this coordinator never reads
/// unvalidated clone bytes.
/// </summary>
public sealed class ProjectVersionCloneImportService(
    IProjectVersionSyncService sync,
    IProjectVersionRestoreService restore,
    IAppDatabaseOperationFactory database,
    ILogger<ProjectVersionCloneImportService> logger) : IProjectVersionCloneImportService
{
    public async Task<VersionHistoryCloneImportResult> ImportCloneAsync(
        VersionHistoryCloneImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.CloneRequest);

        VersionHistoryImportCloneResult clone;
        try
        {
            clone = await sync.CloneForImportAsync(request.CloneRequest, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "GitHub clone import of {Repository} failed before the snapshot was imported.",
                request.CloneRequest.Repository?.FullName);
            throw new ProjectVersionCloneImportException(
                ProjectVersionCloneImportErrorCode.CloneFailed,
                CloneFailureMessage(exception),
                exception);
        }

        using var snapshotLease = clone;
        VersionHistoryImportResult imported;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            imported = await restore.ImportCloneAsync(clone, clone.Artifact, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await TryCleanupUnownedCloneAsync(clone, cancellationToken: CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "GitHub clone import of repository {RepositoryId} failed while importing the validated snapshot.",
                clone.RepositoryId);
            var cleanup = await TryCleanupUnownedCloneAsync(clone, CancellationToken.None);
            if (!cleanup.Removed && !cleanup.DatabaseIdentityExists)
            {
                throw new ProjectVersionCloneImportException(
                    ProjectVersionCloneImportErrorCode.CleanupFailed,
                    "The snapshot import failed and the installed repository could not be safely removed. Retry is still protected by strict identity validation.",
                    exception);
            }

            throw new ProjectVersionCloneImportException(
                ProjectVersionCloneImportErrorCode.RestoreFailed,
                cleanup.DatabaseIdentityExists
                    ? "The snapshot import failed after the project identity was committed; the imported local history was preserved."
                    : "The snapshot import failed; the newly installed repository was removed and can be retried.",
                exception);
        }

        var warnings = imported.Warnings.ToList();
        var attachment = await AttachOriginAsync(clone, imported, warnings, cancellationToken);
        return new VersionHistoryCloneImportResult(clone, imported, attachment, warnings);
    }

    private async Task<VersionHistoryCloneRemoteAttachment> AttachOriginAsync(
        VersionHistoryImportCloneResult clone,
        VersionHistoryImportResult imported,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            var status = await sync.AttachRemoteAsync(
                imported.RepositoryId,
                clone.Selection,
                cancellationToken);
            return new VersionHistoryCloneRemoteAttachment(
                true,
                status,
                "The imported project is connected to its GitHub origin and is ready for explicit sync.");
        }
        catch (OperationCanceledException)
        {
            var message = "The project was imported successfully, but attaching its GitHub origin was canceled. Attach the origin manually and retry sync.";
            warnings.Add(message);
            return new VersionHistoryCloneRemoteAttachment(
                false,
                null,
                message,
                ProjectVersionCloneImportErrorCode.Canceled.ToString());
        }
        catch (Exception exception)
        {
            var message = "The project was imported successfully, but its GitHub origin could not be attached. Attach the origin manually and retry sync.";
            warnings.Add(message);
            return new VersionHistoryCloneRemoteAttachment(
                false,
                null,
                message,
                ErrorCodeFor(exception));
        }
    }

    private async Task<CleanupResult> TryCleanupUnownedCloneAsync(
        VersionHistoryImportCloneResult clone,
        CancellationToken cancellationToken)
    {
        bool databaseIdentityExists;
        try
        {
            databaseIdentityExists = await HasDatabaseIdentityAsync(clone, cancellationToken);
        }
        catch
        {
            return new CleanupResult(false, false);
        }

        if (databaseIdentityExists)
            return new CleanupResult(false, true);

        try
        {
            var removed = await sync.RemoveImportedRepositoryIfUnownedAsync(clone, cancellationToken);
            return new CleanupResult(removed, false);
        }
        catch
        {
            return new CleanupResult(false, false);
        }
    }

    private async Task<bool> HasDatabaseIdentityAsync(
        VersionHistoryImportCloneResult clone,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        return await operation.Db.Projects.AsNoTracking().AnyAsync(
                   project => project.Id == clone.ProjectId,
                   cancellationToken)
            || await operation.Db.ProjectVersionRepositories.AsNoTracking().AnyAsync(
                repository => repository.Id == clone.RepositoryId,
                cancellationToken);
    }

    // Sync exceptions carry curated, credential-free reasons; anything else
    // stays generic in the UI and is diagnosable from the log.
    private static string CloneFailureMessage(Exception exception) => exception switch
    {
        ProjectVersionSyncException sync => $"The GitHub repository could not be imported. {sync.Message}",
        _ => "The GitHub repository could not be cloned and validated for import.",
    };

    private static string ErrorCodeFor(Exception exception) => exception switch
    {
        ProjectVersionSyncException sync => sync.Code.ToString(),
        Lorekeeper.VersionHistory.GitHub.GitHubConnectionException github => $"GITHUB_{github.Code}",
        OperationCanceledException => ProjectVersionCloneImportErrorCode.Canceled.ToString(),
        _ => ProjectVersionCloneImportErrorCode.RestoreFailed.ToString(),
    };

    private readonly record struct CleanupResult(bool Removed, bool DatabaseIdentityExists);
}
