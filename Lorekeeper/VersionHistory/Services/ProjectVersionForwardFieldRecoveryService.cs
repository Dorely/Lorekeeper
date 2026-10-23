using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Projects;
using Lorekeeper.VersionHistory.Git;
using Lorekeeper.VersionHistory.Snapshots;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.VersionHistory.Services;

public interface IProjectVersionForwardFieldRecoveryService
{
    /// <summary>
    /// Brings content that an older Lorekeeper dropped from the project's head
    /// checkpoint back into the live project, then checkpoints it. Returns
    /// whether anything was carried forward.
    /// </summary>
    Task<bool> CarryForwardAsync(Guid projectId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs after an update: when an older app wrote the head checkpoint on top of
/// newer history, fields it could not store are recovered from that history
/// and filled into the live project wherever the live value is still empty.
/// </summary>
public sealed class ProjectVersionForwardFieldRecoveryService(
    IAppDatabaseOperationFactory database,
    IGitRepositoryStore git,
    IWorldBriefService worldBriefs,
    IProjectVersionHistoryService history) : IProjectVersionForwardFieldRecoveryService
{
    public async Task<bool> CarryForwardAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        Guid repositoryId;
        string headCommitSha;
        int headSchemaVersion;
        await using (var read = await database.OpenReadAsync(cancellationToken))
        {
            var repository = await read.Db.ProjectVersionRepositories
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken);
            if (repository?.HeadCommitSha is null)
                return false;

            var headSchema = await read.Db.ProjectVersionCheckpoints
                .Where(item => item.ProjectVersionRepositoryId == repository.Id
                    && item.CommitSha == repository.HeadCommitSha)
                .Select(item => (int?)item.ManifestSchemaVersion)
                .FirstOrDefaultAsync(cancellationToken);
            if (headSchema is not int schema || schema >= VersionHistorySnapshotContract.SchemaVersion)
                return false;

            // Only history written with a newer schema can hold content the head dropped.
            if (!await read.Db.ProjectVersionCheckpoints.AnyAsync(
                    item => item.ProjectVersionRepositoryId == repository.Id && item.ManifestSchemaVersion > schema,
                    cancellationToken))
                return false;

            repositoryId = repository.Id;
            headCommitSha = repository.HeadCommitSha;
            headSchemaVersion = schema;
        }

        var values = VersionHistoryForwardFields.Read(git, repositoryId, headCommitSha, headSchemaVersion, cancellationToken);
        if (values is null)
            return false;

        var changed = false;
        var brief = await worldBriefs.GetAsync(projectId, cancellationToken);
        if (!string.IsNullOrEmpty(values.WorldBrief) && string.IsNullOrEmpty(brief.Content))
        {
            brief = await worldBriefs.UpdateAsync(projectId, brief.Revision, values.WorldBrief, cancellationToken);
            changed = true;
        }
        if (!string.IsNullOrEmpty(values.WorldWorkingNotes) && string.IsNullOrEmpty(brief.WorkingNotes))
        {
            await worldBriefs.UpdateWorkingNotesAsync(
                projectId,
                brief.WorkingNotesRevision,
                values.WorldWorkingNotes,
                cancellationToken);
            changed = true;
        }

        if (values.PrinterDimensions.Count > 0)
        {
            await using var operation = await database.OpenWriteAsync(projectId, cancellationToken);
            var editions = await operation.Db.PublicationEditions
                .Where(item => item.ProjectId == projectId)
                .ToListAsync(cancellationToken);
            foreach (var edition in editions)
            {
                if (PrinterDimensions.From(edition) is not null
                    || !values.PrinterDimensions.TryGetValue(edition.Id, out var dimensions)
                    || !VersionHistoryForwardFields.AcceptsPrinterDimensions(edition.Vendor, edition.Format, dimensions))
                    continue;

                PrinterDimensions.Apply(edition, dimensions);
                edition.Revision++;
                edition.UpdatedAt = DateTime.UtcNow;
                changed = true;
            }

            await operation.SaveChangesAsync(cancellationToken);
        }

        if (changed)
        {
            await history.CreateCheckpointAsync(
                projectId,
                ProjectVersionCheckpointKind.Restored,
                VersionHistoryForwardFields.CheckpointMessage,
                cancellationToken: cancellationToken);
        }

        return changed;
    }
}
