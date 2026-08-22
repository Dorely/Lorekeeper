using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.VersionHistory.Git;
using Lorekeeper.VersionHistory.Snapshots;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.VersionHistory.Services;

/// <summary>
/// Reconciles the durable database journal/cache with the app-managed local
/// repository without ever rewriting a Git ref or replacing existing history.
/// </summary>
public sealed class ProjectVersionHistoryReconciliationService(
    IProjectMutationCoordinator projectMutations,
    IAppDatabaseOperationFactory database,
    IVersionHistorySnapshotReader snapshotReader,
    IGitRepositoryStore git) : IProjectVersionHistoryReconciliationService
{
    private const string TemporaryDirectoryPrefix = "lorekeeper-version-history-reconcile-";

    public async Task<ProjectVersionReconciliationReport> ReconcileAsync(
        CancellationToken cancellationToken = default)
    {
        var deletionTombstones = await ReconcileDeletionTombstonesAsync(cancellationToken);
        IReadOnlyList<Guid> projectIds;
        await using (var read = await database.OpenReadAsync(cancellationToken))
        {
            projectIds = await read.Db.Projects
                .AsNoTracking()
                .Select(project => project.Id)
                .ToListAsync(cancellationToken);
        }

        var results = new List<ProjectVersionReconciliationItem>(projectIds.Count);
        foreach (var projectId in projectIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ReconcileProjectAsync(projectId, cancellationToken));
        }

        return new ProjectVersionReconciliationReport(DateTime.UtcNow, results)
        {
            DeletionTombstones = deletionTombstones,
        };
    }

    private async Task<IReadOnlyList<ProjectVersionDeletionTombstoneItem>> ReconcileDeletionTombstonesAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<GitRepositoryDeletionTombstone> tombstones;
        try
        {
            tombstones = git.ListDeletionTombstones();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return
            [
                new ProjectVersionDeletionTombstoneItem(
                    null,
                    Path.Combine(git.HistoryRoot, ".deleting"),
                    ProjectVersionDeletionTombstoneState.Preserved,
                    exception.Message),
            ];
        }

        var results = new List<ProjectVersionDeletionTombstoneItem>(tombstones.Count);
        foreach (var tombstone in tombstones)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!tombstone.IsValid || tombstone.RepositoryId is not { } repositoryId)
            {
                results.Add(new ProjectVersionDeletionTombstoneItem(
                    tombstone.RepositoryId,
                    tombstone.Path,
                    ProjectVersionDeletionTombstoneState.Preserved,
                    tombstone.Diagnostic ?? "The deletion tombstone was not strictly valid."));
                continue;
            }

            try
            {
                var identity = await FindRepositoryIdentityAsync(repositoryId, cancellationToken);
                if (identity is { HasProject: true })
                {
                    await using var projectLease = await projectMutations.AcquireAsync(
                        identity.ProjectId,
                        cancellationToken);
                    identity = await FindRepositoryIdentityAsync(repositoryId, cancellationToken);
                    if (identity is { HasProject: true })
                    {
                        var stage = git.ReadDeletionTombstone(tombstone);
                        if (File.Exists(stage.OriginalPath) || Directory.Exists(stage.OriginalPath))
                        {
                            results.Add(new ProjectVersionDeletionTombstoneItem(
                                repositoryId,
                                tombstone.Path,
                                ProjectVersionDeletionTombstoneState.Preserved,
                                $"The canonical local history path is occupied: {stage.OriginalPath}"));
                            continue;
                        }

                        git.RollbackRepositoryDeletion(stage);
                        results.Add(new ProjectVersionDeletionTombstoneItem(
                            repositoryId,
                            tombstone.Path,
                            ProjectVersionDeletionTombstoneState.RolledBack,
                            stage.WasPresent
                                ? $"Restored local history to {stage.OriginalPath} because its project and repository identity still exist."
                                : "Removed an empty deletion marker because its project and repository identity still exist, but no local repository directory was present."));
                        continue;
                    }
                }

                var orphanedStage = git.ReadDeletionTombstone(tombstone);
                git.FinalizeRepositoryDeletion(orphanedStage);
                results.Add(new ProjectVersionDeletionTombstoneItem(
                    repositoryId,
                    tombstone.Path,
                    ProjectVersionDeletionTombstoneState.Finalized,
                    "Finalized the local deletion tombstone because its database repository identity no longer exists."));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                results.Add(new ProjectVersionDeletionTombstoneItem(
                    repositoryId,
                    tombstone.Path,
                    ProjectVersionDeletionTombstoneState.Preserved,
                    exception.Message));
            }
        }

        return results;
    }

    private async Task<RepositoryIdentity?> FindRepositoryIdentityAsync(
        Guid repositoryId,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        var identity = await operation.Db.ProjectVersionRepositories
            .AsNoTracking()
            .Where(repository => repository.Id == repositoryId)
            .Select(repository => new
            {
                repository.ProjectId,
                HasProject = operation.Db.Projects.Any(project => project.Id == repository.ProjectId),
            })
            .SingleOrDefaultAsync(cancellationToken);
        return identity is null
            ? null
            : new RepositoryIdentity(identity.ProjectId, identity.HasProject);
    }

    private async Task<ProjectVersionReconciliationItem> ReconcileProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        await using var projectLease = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var repositoryId = Guid.Empty;
        try
        {
            var repository = await EnsureRepositoryRowAsync(projectId, cancellationToken);
            repositoryId = repository.Id;
            await MarkInterruptedOperationsAsync(repository.Id, cancellationToken);

            var repositoryPath = git.GetRepositoryPath(repository.Id);
            if (!Directory.Exists(repositoryPath))
            {
                if (!string.IsNullOrWhiteSpace(repository.HeadCommitSha))
                {
                    return Result(
                        projectId,
                        repository.Id,
                        ProjectVersionReconciliationState.Missing,
                        repository.HeadCommitSha,
                        0,
                        "The local history repository is missing while the database has a committed head.");
                }

                await InitializeEmptyRepositoryAsync(repository.Id, cancellationToken);
                return Result(
                    projectId,
                    repository.Id,
                    ProjectVersionReconciliationState.Initialized,
                    null,
                    0,
                    null);
            }

            GitHeadInfo head;
            try
            {
                head = git.GetHead(repository.Id);
            }
            catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
            {
                return Result(
                    projectId,
                    repository.Id,
                    ProjectVersionReconciliationState.Corrupt,
                    repository.HeadCommitSha,
                    0,
                    exception.Message);
            }

            if (head.Commit is null)
            {
                return Result(
                    projectId,
                    repository.Id,
                    string.IsNullOrWhiteSpace(repository.HeadCommitSha)
                        ? ProjectVersionReconciliationState.Empty
                        : ProjectVersionReconciliationState.Corrupt,
                    null,
                    0,
                    repository.HeadCommitSha is null
                        ? null
                        : "The database has a committed head, but the Git repository is empty.");
            }

            if (!IsCachedHeadCompatible(repository, head.Commit.Sha))
            {
                return Result(
                    projectId,
                    repository.Id,
                    ProjectVersionReconciliationState.Diverged,
                    head.Commit.Sha,
                    0,
                    "The database cache and local Git head are not in a fast-forward relationship.");
            }

            List<LoadedCommit> commits;
            try
            {
                var knownCommitShas = await ListKnownCheckpointCommitShasAsync(
                    repository.Id,
                    cancellationToken);
                var commitsToValidate = git.ListCommits(repository.Id, int.MaxValue)
                    .Where(commit => string.Equals(commit.Sha, head.Commit.Sha, StringComparison.Ordinal)
                        || !knownCommitShas.Contains(commit.Sha))
                    .ToList();
                commits = LoadAndValidateCommits(
                    repository.Id,
                    projectId,
                    commitsToValidate,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
            {
                return Result(
                    projectId,
                    repository.Id,
                    ProjectVersionReconciliationState.Corrupt,
                    head.Commit.Sha,
                    0,
                    exception.Message);
            }

            var headCommit = commits.SingleOrDefault(item => item.Commit.Sha == head.Commit.Sha)
                ?? throw new InvalidDataException("The Git head was not present in its reachable commit list.");
            var importedCount = await ImportMissingMetadataAsync(
                repository,
                headCommit,
                commits,
                cancellationToken);
            return Result(
                projectId,
                repository.Id,
                ProjectVersionReconciliationState.Healthy,
                head.Commit.Sha,
                importedCount,
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result(
                projectId,
                repositoryId,
                ProjectVersionReconciliationState.Corrupt,
                null,
                0,
                exception.Message);
        }
    }

    private async Task<HashSet<string>> ListKnownCheckpointCommitShasAsync(
        Guid repositoryId,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        var commitShas = await operation.Db.ProjectVersionCheckpoints
            .AsNoTracking()
            .Where(item => item.ProjectVersionRepositoryId == repositoryId && item.CommitSha != null)
            .Select(item => item.CommitSha!)
            .ToListAsync(cancellationToken);
        return commitShas.ToHashSet(StringComparer.Ordinal);
    }

    private async Task<ProjectVersionRepository> EnsureRepositoryRowAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var db = operation.Db;
        var repository = await db.ProjectVersionRepositories
            .SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken);
        if (repository is not null)
            return repository;

        repository = new ProjectVersionRepository
        {
            ProjectId = projectId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.ProjectVersionRepositories.Add(repository);
        await operation.SaveChangesAsync(cancellationToken);
        return repository;
    }

    private async Task MarkInterruptedOperationsAsync(
        Guid repositoryId,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var running = await operation.Db.ProjectVersionOperations
            .Where(item => item.ProjectVersionRepositoryId == repositoryId
                && item.Status == ProjectVersionOperationStatus.Running)
            .ToListAsync(cancellationToken);
        foreach (var journal in running)
        {
            journal.Status = ProjectVersionOperationStatus.Failed;
            journal.IsResumable = true;
            journal.ErrorCode = "StartupInterrupted";
            journal.ErrorMessage = "The operation was interrupted before the previous application exited.";
            journal.CompletedAt = now;
            journal.HeartbeatAt = now;
            journal.UpdatedAt = now;
        }

        if (running.Count > 0)
            await operation.SaveChangesAsync(cancellationToken);
    }

    private async Task InitializeEmptyRepositoryAsync(
        Guid repositoryId,
        CancellationToken cancellationToken)
    {
        var operationId = await StartOperationAsync(
            repositoryId,
            ProjectVersionOperationKind.Initialize,
            cancellationToken);
        try
        {
            git.InitializeRepository(repositoryId);
            await CompleteOperationAsync(operationId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await FailOperationAsync(operationId, exception);
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
            .SingleAsync(item => item.Id == repositoryId, cancellationToken);
        var now = DateTime.UtcNow;
        var journal = new ProjectVersionOperation
        {
            ProjectVersionRepositoryId = repositoryId,
            Repository = repository,
            Kind = kind,
            Status = ProjectVersionOperationStatus.Running,
            IsResumable = true,
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

    private async Task CompleteOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var journal = await operation.Db.ProjectVersionOperations
            .SingleAsync(item => item.Id == operationId, cancellationToken);
        var now = DateTime.UtcNow;
        journal.Status = ProjectVersionOperationStatus.Succeeded;
        journal.CompletedAt = now;
        journal.HeartbeatAt = now;
        journal.UpdatedAt = now;
        await operation.SaveChangesAsync(cancellationToken);
    }

    private async Task FailOperationAsync(Guid operationId, Exception exception)
    {
        try
        {
            await using var operation = await database.OpenWriteAsync();
            var journal = await operation.Db.ProjectVersionOperations
                .SingleOrDefaultAsync(item => item.Id == operationId);
            if (journal is null)
                return;

            var now = DateTime.UtcNow;
            journal.Status = ProjectVersionOperationStatus.Failed;
            journal.IsResumable = true;
            journal.ErrorCode = exception.GetType().Name;
            journal.ErrorMessage = exception.Message.Length <= 500
                ? exception.Message
                : exception.Message[..500];
            journal.CompletedAt = now;
            journal.HeartbeatAt = now;
            journal.UpdatedAt = now;
            await operation.SaveChangesAsync();
        }
        catch
        {
        }
    }

    private bool IsCachedHeadCompatible(
        ProjectVersionRepository repository,
        string gitHeadSha)
    {
        if (string.IsNullOrWhiteSpace(repository.HeadCommitSha)
            || string.Equals(repository.HeadCommitSha, gitHeadSha, StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            _ = git.GetCommitMetadata(repository.Id, repository.HeadCommitSha);
            var comparison = git.CompareHistory(repository.Id, repository.HeadCommitSha, gitHeadSha);
            return comparison.Relation is GitHistoryRelation.Identical or GitHistoryRelation.CandidateFastForward;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<int> ImportMissingMetadataAsync(
        ProjectVersionRepository repository,
        LoadedCommit head,
        IReadOnlyList<LoadedCommit> commits,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var db = operation.Db;
        var existing = await db.ProjectVersionCheckpoints
            .Where(item => item.ProjectVersionRepositoryId == repository.Id)
            .ToListAsync(cancellationToken);
        var byCommit = existing
            .Where(item => item.CommitSha is not null)
            .ToDictionary(item => item.CommitSha!, StringComparer.Ordinal);
        var knownContentHashes = existing
            .Select(item => item.ContentHash)
            .ToHashSet(StringComparer.Ordinal);
        var imported = 0;

        foreach (var item in commits.OrderBy(item => item.Commit.CommittedAt))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (byCommit.TryGetValue(item.Commit.Sha, out var recorded))
            {
                if (!string.Equals(recorded.ContentHash, item.Manifest.ContentHash, StringComparison.Ordinal))
                    throw new InvalidDataException($"Checkpoint metadata disagrees with Git commit {item.Commit.Sha}.");
                continue;
            }
            if (!knownContentHashes.Add(item.Manifest.ContentHash))
                continue;

            repository.CreativeRevision++;
            var checkpoint = new ProjectVersionCheckpoint
            {
                ProjectVersionRepositoryId = repository.Id,
                ManifestSchemaVersion = item.Manifest.SchemaVersion,
                CreativeRevision = repository.CreativeRevision,
                ContentHash = item.Manifest.ContentHash,
                ManifestHash = item.Manifest.ManifestHash,
                CommitSha = item.Commit.Sha,
                ParentCommitSha = item.Commit.ParentShas.FirstOrDefault(),
                Kind = ProjectVersionCheckpointKind.Imported,
                Source = ProjectVersionCheckpointSource.Recovery,
                Message = item.Commit.Message,
                CreatedAt = item.Commit.CommittedAt.UtcDateTime,
            };
            db.ProjectVersionCheckpoints.Add(checkpoint);
            byCommit[item.Commit.Sha] = checkpoint;
            imported++;
        }

        repository.HeadCommitSha = head.Commit.Sha;
        repository.HeadContentHash = head.Manifest.ContentHash;
        repository.LastCheckpointRevision = byCommit.TryGetValue(head.Commit.Sha, out var headCheckpoint)
            ? headCheckpoint.CreativeRevision
            : repository.CreativeRevision;
        repository.LastCheckpointContentHash = head.Manifest.ContentHash;
        repository.LastCheckpointAt = head.Commit.CommittedAt.UtcDateTime;
        repository.UpdatedAt = DateTime.UtcNow;
        await operation.SaveChangesAsync(cancellationToken);
        return imported;
    }

    private List<LoadedCommit> LoadAndValidateCommits(
        Guid repositoryId,
        Guid projectId,
        IReadOnlyList<GitCommitMetadata> commits,
        CancellationToken cancellationToken)
    {
        var result = new List<LoadedCommit>(commits.Count);
        foreach (var commit in commits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var files = git.ReadTree(repositoryId, commit.Sha);
            var temporaryDirectory = CreateTemporaryDirectory();
            try
            {
                WriteTreeToTemporaryDirectory(temporaryDirectory, files, cancellationToken);
                EnsureNoReparsePointsRecursively(temporaryDirectory);
                var artifact = snapshotReader.Read(temporaryDirectory, repositoryId, projectId);
                result.Add(new LoadedCommit(commit, artifact.Manifest));
            }
            finally
            {
                CleanupTemporaryDirectory(temporaryDirectory);
            }
        }

        return result;
    }

    private static ProjectVersionReconciliationItem Result(
        Guid projectId,
        Guid repositoryId,
        ProjectVersionReconciliationState state,
        string? headCommitSha,
        int importedCheckpointCount,
        string? diagnostic) => new(
        projectId,
        repositoryId,
        state,
        headCommitSha,
        importedCheckpointCount,
        diagnostic);

    private static void WriteTreeToTemporaryDirectory(
        string rootDirectory,
        IReadOnlyDictionary<string, byte[]> files,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(rootDirectory);
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        foreach (var entry in files.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = entry.Key.Replace('\\', '/');
            var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Git tree path escaped its temporary root: {relative}");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, entry.Value);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var parent = Path.Combine(Path.GetTempPath(), "Lorekeeper", "version-history");
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
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Lorekeeper", "version-history"));
        var full = Path.GetFullPath(path);
        var prefix = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith(TemporaryDirectoryPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to clean a non-owned version-history temporary directory.");
        }

        EnsureNoReparsePointsRecursively(full);
        foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(full, recursive: true);
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

    private sealed record LoadedCommit(
        GitCommitMetadata Commit,
        VersionHistorySnapshotManifest Manifest);

    private sealed record RepositoryIdentity(
        Guid ProjectId,
        bool HasProject);
}
