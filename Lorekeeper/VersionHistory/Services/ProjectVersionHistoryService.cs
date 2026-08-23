using Lorekeeper.Models;
using Lorekeeper.Persistence;
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
    IGitRepositoryStore git,
    ProjectVersionHistoryUiEvents historyEvents,
    IProjectVersionAutoPushQueue autoPushQueue) : IProjectVersionHistoryService
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

    public async Task<ProjectVersionCheckpointView> CreateCheckpointAsync(
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
        var repository = await EnsureRepositoryUnderLeaseAsync(projectId, cancellationToken);
        var operationId = await StartOperationAsync(
            repository.Id,
            ProjectVersionOperationKind.Checkpoint,
            requestKey,
            cancellationToken);
        string? temporaryDirectory = null;

        try
        {
            temporaryDirectory = CreateTemporaryDirectory();
            _ = await snapshotWriter.WriteAsync(
                repository.Id,
                projectId,
                temporaryDirectory,
                cancellationToken);
            EnsureNoReparsePointsRecursively(temporaryDirectory);
            var validated = snapshotReader.Read(temporaryDirectory, repository.Id, projectId);
            var files = ReadSnapshotFiles(temporaryDirectory, cancellationToken);

            var existingHead = ReadExistingHead(repository);
            if (existingHead?.Commit is not null)
            {
                var current = LoadGitCheckpoint(
                    repository.Id,
                    existingHead.Commit.Sha,
                    recordedCheckpoint: null,
                    projectId,
                    cancellationToken);
                if (string.Equals(
                        current.Manifest.ContentHash,
                        validated.Manifest.ContentHash,
                        StringComparison.Ordinal))
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
                        cancellationToken);
                    historyEvents.PublishCheckpointCreated(projectId);
                    return duplicate;
                }
            }

            var write = git.WriteSnapshot(
                repository.Id,
                files,
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
                cancellationToken);
            historyEvents.PublishCheckpointCreated(projectId);
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

    public async Task<ProjectVersionStatusView?> GetStatusAsync(
        Guid projectId,
        bool includeCurrentSnapshotHash = true,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        await using var projectLease = await projectMutations.AcquireAsync(projectId, cancellationToken);
        return await GetStatusUnderLeaseAsync(projectId, includeCurrentSnapshotHash, cancellationToken);
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
                    currentContentHash = await CaptureCurrentContentHashAsync(repository, projectId, cancellationToken);
                if (currentContentHash is not null
                    && !string.Equals(currentContentHash, loaded.Manifest.ContentHash, StringComparison.Ordinal))
                {
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
        var isDirty = currentContentHash is not null
            && !string.Equals(currentContentHash, repository.HeadContentHash, StringComparison.Ordinal);
        return new ProjectVersionStatusView(view with { IsDirty = isDirty }, currentContentHash);
    }

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
        CancellationToken cancellationToken)
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

    private async Task<string> CaptureCurrentContentHashAsync(
        ProjectVersionRepository repository,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var temporaryDirectory = CreateTemporaryDirectory();
        try
        {
            _ = await snapshotWriter.WriteAsync(
                repository.Id,
                projectId,
                temporaryDirectory,
                cancellationToken);
            EnsureNoReparsePointsRecursively(temporaryDirectory);
            var validated = snapshotReader.Read(temporaryDirectory, repository.Id, projectId);
            return validated.Manifest.ContentHash;
        }
        finally
        {
            CleanupTemporaryDirectory(temporaryDirectory);
        }
    }

    private LoadedGitCheckpoint LoadGitCheckpoint(
        Guid repositoryId,
        string commitSha,
        ProjectVersionCheckpoint? recordedCheckpoint,
        Guid expectedProjectId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var commit = git.GetCommitMetadata(repositoryId, commitSha);
        var files = git.ReadTree(repositoryId, commitSha);
        var temporaryDirectory = CreateTemporaryDirectory();
        try
        {
            WriteTreeToTemporaryDirectory(temporaryDirectory, files, cancellationToken);
            EnsureNoReparsePointsRecursively(temporaryDirectory);
            var artifact = snapshotReader.Read(temporaryDirectory, repositoryId, expectedProjectId);
            if (recordedCheckpoint is not null
                && !string.Equals(
                    recordedCheckpoint.ContentHash,
                    artifact.Manifest.ContentHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("The recorded checkpoint content hash does not match its Git payload.");
            }

            return new LoadedGitCheckpoint(commit, artifact.Manifest, artifact.Payload);
        }
        finally
        {
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

    private static SortedDictionary<string, byte[]> ReadSnapshotFiles(
        string rootDirectory,
        CancellationToken cancellationToken)
    {
        EnsureNoReparsePointsRecursively(rootDirectory);
        var root = Path.GetFullPath(rootDirectory);
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException($"Snapshot file is a reparse point: {file}");
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var full = Path.GetFullPath(file);
            var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Snapshot file escaped its temporary root: {relative}");
            if (!files.TryAdd(relative, File.ReadAllBytes(file)))
                throw new InvalidDataException($"Snapshot contains duplicate path: {relative}");
        }

        return files;
    }

    private static void WriteTreeToTemporaryDirectory(
        string rootDirectory,
        IReadOnlyDictionary<string, byte[]> files,
        CancellationToken cancellationToken)
    {
        foreach (var entry in files.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = entry.Key.Replace('\\', '/');
            var full = Path.GetFullPath(Path.Combine(rootDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
            var rootPrefix = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
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

    private sealed record LoadedGitCheckpoint(
        GitCommitMetadata Commit,
        VersionHistorySnapshotManifest Manifest,
        VersionHistorySnapshotPayload Payload);

    private static void ValidateProjectId(Guid projectId)
    {
        if (projectId == Guid.Empty)
            throw new ArgumentException("A project ID is required.", nameof(projectId));
    }
}
