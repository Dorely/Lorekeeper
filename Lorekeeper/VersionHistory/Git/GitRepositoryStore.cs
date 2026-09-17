using System.Collections.Concurrent;
using LibGit2Sharp;
using Lorekeeper.ProjectArchive;

namespace Lorekeeper.VersionHistory.Git;

/// <summary>
/// Stores Lorekeeper snapshots as immutable trees in one bare Git repository
/// per project repository identity. The only mutable Git state is the app-owned
/// refs/heads/main reference, which is updated after all objects are created.
/// </summary>
public sealed class GitRepositoryStore : IGitRepositoryStore
{
    public const string MainReferenceName = "refs/heads/main";
    public const string AppAuthorName = "Lorekeeper";
    public const string AppAuthorEmail = "lorekeeper@localhost";

    private static readonly ConcurrentDictionary<string, object> RepositoryLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly GitRepositoryStoreOptions _options;
    private readonly string _historyRoot;

    public GitRepositoryStore(GitRepositoryStoreOptions? options = null)
    {
        _options = options ?? new GitRepositoryStoreOptions();
        _historyRoot = _options.ResolveHistoryRoot();
    }

    public string HistoryRoot => _historyRoot;

    public string GetRepositoryPath(Guid repositoryId)
    {
        ValidateRepositoryId(repositoryId);

        var root = Path.GetFullPath(_historyRoot);
        var candidate = Path.GetFullPath(Path.Combine(root, $"{repositoryId:N}.git"));
        var relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", GetPathComparison())
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", GetPathComparison())
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", GetPathComparison()))
        {
            throw new InvalidOperationException("The repository path escaped the configured history root.");
        }

        return candidate;
    }

    public GitRepositoryDeletionStage StageRepositoryDeletion(Guid repositoryId)
    {
        var originalPath = GetRepositoryPath(repositoryId);
        lock (GetRepositoryLock(originalPath))
        {
            EnsureHistoryRoot();
            ValidateExactHistoryPath(originalPath, repositoryId);
            EnsureNoPendingDeletion(repositoryId);

            var deletionRoot = Path.Combine(Path.GetFullPath(_historyRoot), ".deleting");
            ValidatePathWithinHistoryRoot(deletionRoot);
            EnsureNoReparsePoints(deletionRoot);
            Directory.CreateDirectory(deletionRoot);
            EnsureNoReparsePoints(deletionRoot);

            var stagedPath = Path.Combine(deletionRoot, $"{repositoryId:N}.{Guid.NewGuid():N}.deleting");
            ValidatePathWithinHistoryRoot(stagedPath);
            EnsureNoReparsePoints(stagedPath);

            if (!Directory.Exists(originalPath))
            {
                if (File.Exists(originalPath))
                    throw new GitRepositoryDeletionException(
                        $"The local history path is occupied by a file: {originalPath}",
                        new GitRepositoryDeletionStage(repositoryId, originalPath, stagedPath, WasPresent: false));

                Directory.CreateDirectory(stagedPath);
                return new GitRepositoryDeletionStage(repositoryId, originalPath, stagedPath, WasPresent: false);
            }

            EnsureNoReparsePoints(originalPath);
            EnsureNoNestedReparsePoints(originalPath);
            if (!Repository.IsValid(originalPath))
                throw new GitRepositoryDeletionException(
                    $"The existing local history path is not a valid Git repository: {originalPath}",
                    new GitRepositoryDeletionStage(repositoryId, originalPath, stagedPath, WasPresent: true));

            try
            {
                Directory.Move(originalPath, stagedPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new GitRepositoryDeletionException(
                    $"The local history repository could not be staged for deletion: {originalPath}",
                    new GitRepositoryDeletionStage(repositoryId, originalPath, stagedPath, WasPresent: true),
                    exception);
            }

            return new GitRepositoryDeletionStage(repositoryId, originalPath, stagedPath, WasPresent: true);
        }
    }

    public void RollbackRepositoryDeletion(GitRepositoryDeletionStage stage)
    {
        ValidateDeletionStage(stage);
        if (!stage.WasPresent)
        {
            RemoveDeletionMarker(stage);
            return;
        }

        lock (GetRepositoryLock(stage.OriginalPath))
        {
            EnsureNoReparsePoints(stage.OriginalPath);
            EnsureNoReparsePoints(stage.StagedPath);
            if (!Directory.Exists(stage.StagedPath))
                throw new GitRepositoryDeletionException(
                    $"The staged local history repository is missing and could not be rolled back: {stage.StagedPath}",
                    stage);
            if (Directory.Exists(stage.OriginalPath) || File.Exists(stage.OriginalPath))
                throw new GitRepositoryDeletionException(
                    $"The original local history path is occupied and could not be restored: {stage.OriginalPath}",
                    stage);

            try
            {
                Directory.Move(stage.StagedPath, stage.OriginalPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new GitRepositoryDeletionException(
                    $"The local history repository could not be restored from its deletion staging path: {stage.StagedPath}",
                    stage,
                    exception);
            }
        }
    }

    public void FinalizeRepositoryDeletion(GitRepositoryDeletionStage stage)
    {
        ValidateDeletionStage(stage);
        if (!stage.WasPresent)
        {
            RemoveDeletionMarker(stage);
            return;
        }

        lock (GetRepositoryLock(stage.OriginalPath))
        {
            EnsureNoReparsePoints(stage.StagedPath);
            if (!Directory.Exists(stage.StagedPath))
                return;

            try
            {
                EnsureNoNestedReparsePoints(stage.StagedPath);
                Directory.Delete(stage.StagedPath, recursive: true);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                throw new GitRepositoryDeletionException(
                    $"The project was deleted, but local history cleanup is pending at '{stage.StagedPath}'. This staged history is recoverable by a later local-history reconciliation.",
                    stage,
                    exception);
            }
        }
    }

    public IReadOnlyList<GitRepositoryDeletionTombstone> ListDeletionTombstones()
    {
        var deletionRoot = Path.Combine(Path.GetFullPath(_historyRoot), ".deleting");
        ValidatePathWithinHistoryRoot(deletionRoot);
        if (File.Exists(deletionRoot))
        {
            return
            [
                new GitRepositoryDeletionTombstone(
                    null,
                    deletionRoot,
                    IsValid: false,
                    "The deletion tombstone root is a file, not an app-owned directory."),
            ];
        }

        if (!Directory.Exists(deletionRoot))
            return [];

        try
        {
            EnsureNoReparsePoints(deletionRoot);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return
            [
                new GitRepositoryDeletionTombstone(
                    null,
                    deletionRoot,
                    IsValid: false,
                    exception.Message),
            ];
        }

        string[] paths;
        try
        {
            paths = Directory.GetFileSystemEntries(deletionRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return
            [
                new GitRepositoryDeletionTombstone(
                    null,
                    deletionRoot,
                    IsValid: false,
                    exception.Message),
            ];
        }

        var tombstones = new List<GitRepositoryDeletionTombstone>(paths.Length);
        foreach (var path in paths)
        {
            var fullPath = Path.GetFullPath(path);
            try
            {
                var name = Path.GetFileName(fullPath);
                var repositoryId = ParseTombstoneRepositoryId(name, out var parseDiagnostic);
                var isDirectory = Directory.Exists(fullPath);
                var isReparsePoint = File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint);
                var isDirectChild = string.Equals(
                    Path.GetDirectoryName(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    deletionRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    GetPathComparison());
                var diagnostic = parseDiagnostic;
                if (!isDirectChild)
                    diagnostic ??= "The deletion tombstone is not a direct child of HistoryRoot/.deleting.";
                if (!isDirectory)
                    diagnostic ??= "The deletion tombstone is not a directory.";
                if (isReparsePoint)
                    diagnostic ??= "The deletion tombstone is a symbolic link or reparse point.";

                tombstones.Add(new GitRepositoryDeletionTombstone(
                    repositoryId,
                    fullPath,
                    IsValid: diagnostic is null,
                    diagnostic));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                tombstones.Add(new GitRepositoryDeletionTombstone(
                    null,
                    fullPath,
                    IsValid: false,
                    exception.Message));
            }
        }

        return tombstones;
    }

    public GitRepositoryDeletionStage ReadDeletionTombstone(
        GitRepositoryDeletionTombstone tombstone)
    {
        ArgumentNullException.ThrowIfNull(tombstone);
        if (!tombstone.IsValid || tombstone.RepositoryId is not { } repositoryId || repositoryId == Guid.Empty)
            throw new InvalidOperationException(
                tombstone.Diagnostic ?? "The deletion tombstone is not strictly valid.");

        var originalPath = GetRepositoryPath(repositoryId);
        var stage = new GitRepositoryDeletionStage(
            repositoryId,
            originalPath,
            tombstone.Path,
            WasPresent: false);
        ValidateDeletionStage(stage);
        EnsureNoReparsePoints(stage.StagedPath);
        if (!Directory.Exists(stage.StagedPath))
            throw new GitRepositoryDeletionException(
                $"The deletion tombstone directory is missing: {stage.StagedPath}",
                stage);

        try
        {
            if (Repository.IsValid(stage.StagedPath))
                return stage with { WasPresent = true };

            if (Directory.EnumerateFileSystemEntries(stage.StagedPath).Any())
                throw new InvalidDataException(
                    $"The deletion tombstone contains data that is not a valid bare Git repository: {stage.StagedPath}");

            return stage;
        }
        catch (GitRepositoryDeletionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new GitRepositoryDeletionException(
                $"The deletion tombstone could not be validated: {stage.StagedPath}",
                stage,
                exception);
        }
    }

    public void InitializeRepository(Guid repositoryId)
    {
        var repositoryPath = GetRepositoryPath(repositoryId);
        lock (GetRepositoryLock(repositoryPath))
        {
            EnsureHistoryRoot();
            EnsureNoPendingDeletion(repositoryId);
            EnsureNoReparsePoints(repositoryPath);

            if (Directory.Exists(repositoryPath))
            {
                if (!Repository.IsValid(repositoryPath))
                    throw new InvalidDataException($"The existing history path is not a valid Git repository: {repositoryPath}");

                return;
            }

            if (File.Exists(repositoryPath))
                throw new InvalidDataException($"The history repository path is occupied by a file: {repositoryPath}");

            Repository.Init(repositoryPath, isBare: true);
            using var repository = new Repository(repositoryPath);
            EnsureMainHeadReference(repository);
        }
    }

    public GitHeadInfo GetHead(Guid repositoryId)
    {
        var repositoryPath = GetRepositoryPath(repositoryId);
        lock (GetRepositoryLock(repositoryPath))
        {
            using var repository = OpenRepository(repositoryPath);
            return new GitHeadInfo(MainReferenceName, ToMetadata(GetMainHead(repository)));
        }
    }

    public GitCommitWriteResult WriteSnapshot(
        Guid repositoryId,
        IReadOnlyCollection<ProjectArchiveFileDescriptor> files,
        string semanticMessage,
        DateTimeOffset authoredAt)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (string.IsNullOrWhiteSpace(semanticMessage))
            throw new ArgumentException("A semantic commit message is required.", nameof(semanticMessage));
        if (semanticMessage.Contains('\0'))
            throw new ArgumentException("A commit message cannot contain a null character.", nameof(semanticMessage));

        var entries = NormalizeFiles(files);
        var repositoryPath = GetRepositoryPath(repositoryId);
        lock (GetRepositoryLock(repositoryPath))
        {
            InitializeRepository(repositoryId);
            using var repository = OpenRepository(repositoryPath);
            var head = GetMainHead(repository);
            var tree = CreateTree(repository, entries);

            if (head is not null && head.Tree.Id.Equals(tree.Id))
                return new GitCommitWriteResult(Created: false, ToMetadata(head)!);

            var signature = new Signature(AppAuthorName, AppAuthorEmail, authoredAt);
            var parents = head is null ? Array.Empty<Commit>() : [head];
            var commit = repository.ObjectDatabase.CreateCommit(
                signature,
                signature,
                semanticMessage,
                tree,
                parents,
                prettifyMessage: false);

            UpdateMainReference(repository, commit.Id);
            EnsureMainHeadReference(repository);
            return new GitCommitWriteResult(Created: true, ToMetadata(commit)!);
        }
    }

    public void MaterializeTree(
        Guid repositoryId,
        string destinationDirectory,
        string? commitSha = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory))
            throw new ArgumentException("A destination directory is required.", nameof(destinationDirectory));

        var repositoryPath = GetRepositoryPath(repositoryId);
        var root = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(root);
        EnsureNoReparsePoints(root);
        lock (GetRepositoryLock(repositoryPath))
        {
            using var repository = OpenRepository(repositoryPath);
            var commit = ResolveCommit(repository, commitSha)
                ?? throw new InvalidOperationException("The repository has no commit at the requested revision.");
            MaterializeTree(commit.Tree, root, prefix: string.Empty, cancellationToken);
        }
    }

    public GitCommitMetadata GetCommitMetadata(Guid repositoryId, string commitSha)
    {
        var repositoryPath = GetRepositoryPath(repositoryId);
        lock (GetRepositoryLock(repositoryPath))
        {
            using var repository = OpenRepository(repositoryPath);
            return ToMetadata(ResolveCommit(repository, commitSha))
                ?? throw new InvalidOperationException($"The commit was not found: {commitSha}");
        }
    }

    public IReadOnlyList<GitCommitMetadata> ListCommits(Guid repositoryId, int maxCount = 100)
    {
        if (maxCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxCount), "The commit limit must be positive.");

        var repositoryPath = GetRepositoryPath(repositoryId);
        lock (GetRepositoryLock(repositoryPath))
        {
            using var repository = OpenRepository(repositoryPath);
            var head = GetMainHead(repository);
            if (head is null)
                return [];

            var commits = new List<GitCommitMetadata>(Math.Min(maxCount, 256));
            var pending = new Stack<Commit>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            pending.Push(head);

            while (pending.Count > 0 && commits.Count < maxCount)
            {
                var commit = pending.Pop();
                if (!visited.Add(commit.Sha))
                    continue;

                commits.Add(ToMetadata(commit)!);
                var parents = commit.Parents.ToArray();
                for (var index = parents.Length - 1; index >= 0; index--)
                    pending.Push(parents[index]);
            }

            return commits;
        }
    }

    public GitCommitMetadata? FindMergeBase(Guid repositoryId, string currentCommitSha, string candidateCommitSha)
    {
        var repositoryPath = GetRepositoryPath(repositoryId);
        lock (GetRepositoryLock(repositoryPath))
        {
            using var repository = OpenRepository(repositoryPath);
            var current = ResolveCommit(repository, currentCommitSha)
                ?? throw new InvalidOperationException($"The commit was not found: {currentCommitSha}");
            var candidate = ResolveCommit(repository, candidateCommitSha)
                ?? throw new InvalidOperationException($"The commit was not found: {candidateCommitSha}");
            return ToMetadata(repository.ObjectDatabase.FindMergeBase(current, candidate));
        }
    }

    public GitHistoryComparison CompareHistory(
        Guid repositoryId,
        string? currentCommitSha,
        string? candidateCommitSha)
    {
        var repositoryPath = GetRepositoryPath(repositoryId);
        lock (GetRepositoryLock(repositoryPath))
        {
            using var repository = OpenRepository(repositoryPath);
            var current = ResolveCommit(repository, currentCommitSha);
            var candidate = ResolveCommit(repository, candidateCommitSha);

            if (current is null && candidate is null)
                return new GitHistoryComparison(null, null, null, GitHistoryRelation.Empty);
            if (current is null)
                return new GitHistoryComparison(null, candidate!.Sha, null, GitHistoryRelation.CandidateFastForward);
            if (candidate is null)
                return new GitHistoryComparison(current.Sha, null, null, GitHistoryRelation.CurrentFastForward);
            if (current.Id.Equals(candidate.Id))
                return new GitHistoryComparison(current.Sha, candidate.Sha, current.Sha, GitHistoryRelation.Identical);

            var mergeBase = repository.ObjectDatabase.FindMergeBase(current, candidate);
            if (mergeBase is null)
                return new GitHistoryComparison(current.Sha, candidate.Sha, null, GitHistoryRelation.Unrelated);
            if (mergeBase.Id.Equals(current.Id))
                return new GitHistoryComparison(current.Sha, candidate.Sha, mergeBase.Sha, GitHistoryRelation.CandidateFastForward);
            if (mergeBase.Id.Equals(candidate.Id))
                return new GitHistoryComparison(current.Sha, candidate.Sha, mergeBase.Sha, GitHistoryRelation.CurrentFastForward);

            return new GitHistoryComparison(current.Sha, candidate.Sha, mergeBase.Sha, GitHistoryRelation.Diverged);
        }
    }

    public GitHeadInfo FastForwardMain(
        Guid repositoryId,
        string? expectedCurrentCommitSha,
        string targetCommitSha)
    {
        var repositoryPath = GetRepositoryPath(repositoryId);
        lock (GetRepositoryLock(repositoryPath))
        {
            using var repository = OpenRepository(repositoryPath);
            var current = GetMainHead(repository);
            if (string.IsNullOrWhiteSpace(expectedCurrentCommitSha))
            {
                if (current is not null)
                    throw new InvalidOperationException("The expected history head was empty, but the repository already has a head.");
            }
            else
            {
                var expected = ResolveFullCommit(repository, expectedCurrentCommitSha, nameof(expectedCurrentCommitSha));
                if (current is null || !current.Id.Equals(expected.Id))
                    throw new InvalidOperationException("The repository head changed before the fast-forward could be applied.");
            }

            var target = ResolveFullCommit(repository, targetCommitSha, nameof(targetCommitSha));
            if (current is not null)
            {
                var mergeBase = repository.ObjectDatabase.FindMergeBase(current, target);
                if (mergeBase is null || !mergeBase.Id.Equals(current.Id))
                    throw new InvalidOperationException("The target history is not a fast-forward descendant of the expected head.");
            }

            if (current is null || !current.Id.Equals(target.Id))
            {
                UpdateMainReference(repository, target.Id);
                EnsureMainHeadReference(repository);
            }

            return new GitHeadInfo(MainReferenceName, ToMetadata(target)!);
        }
    }

    public GitHeadInfo RollbackMain(
        Guid repositoryId,
        string expectedCurrentCommitSha,
        string previousCommitSha)
    {
        var repositoryPath = GetRepositoryPath(repositoryId);
        lock (GetRepositoryLock(repositoryPath))
        {
            using var repository = OpenRepository(repositoryPath);
            var current = GetMainHead(repository)
                ?? throw new InvalidOperationException(
                    "The repository head changed before the rollback could be applied.");
            var expected = ResolveFullCommit(
                repository,
                expectedCurrentCommitSha,
                nameof(expectedCurrentCommitSha));
            if (!current.Id.Equals(expected.Id))
                throw new InvalidOperationException(
                    "The repository head changed before the rollback could be applied.");

            var previous = ResolveFullCommit(
                repository,
                previousCommitSha,
                nameof(previousCommitSha));
            if (previous.Id.Equals(current.Id))
                throw new InvalidOperationException(
                    "The rollback target must precede the expected current history head.");

            var mergeBase = repository.ObjectDatabase.FindMergeBase(current, previous);
            if (mergeBase is null || !mergeBase.Id.Equals(previous.Id))
                throw new InvalidOperationException(
                    "The rollback target is not an ancestor of the expected current history head.");

            UpdateMainReference(repository, previous.Id);
            EnsureMainHeadReference(repository);
            return new GitHeadInfo(MainReferenceName, ToMetadata(previous)!);
        }
    }

    private void EnsureHistoryRoot()
    {
        EnsureNoReparsePoints(_historyRoot);
        Directory.CreateDirectory(_historyRoot);
        EnsureNoReparsePoints(_historyRoot);
    }

    private Repository OpenRepository(string repositoryPath)
    {
        EnsureHistoryRoot();
        EnsureNoReparsePoints(repositoryPath);
        if (!Directory.Exists(repositoryPath) || !Repository.IsValid(repositoryPath))
            throw new InvalidOperationException($"The history repository is not initialized: {repositoryPath}");

        return new Repository(repositoryPath);
    }

    private static void UpdateMainReference(Repository repository, ObjectId commitId)
    {
        var mainReference = repository.Refs[MainReferenceName];
        if (mainReference is null)
            repository.Refs.Add(MainReferenceName, commitId);
        else
            repository.Refs.UpdateTarget(mainReference, commitId);
    }

    private static void EnsureMainHeadReference(Repository repository)
    {
        var mainReference = repository.Refs[MainReferenceName];
        if (mainReference is not null)
            repository.Refs.Add("HEAD", mainReference, allowOverwrite: true);
    }

    private static Commit? GetMainHead(Repository repository)
    {
        var mainReference = repository.Refs[MainReferenceName];
        if (mainReference is null)
            return null;

        var commit = repository.Lookup(mainReference.TargetIdentifier, ObjectType.Commit) as Commit;
        if (commit is null)
            throw new InvalidDataException("The main history reference does not point to a commit.");
        return commit;
    }

    private static Commit? ResolveCommit(Repository repository, string? commitSha)
    {
        if (string.IsNullOrWhiteSpace(commitSha))
            return GetMainHead(repository);
        if (commitSha.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
            || commitSha.Equals(MainReferenceName, StringComparison.Ordinal))
        {
            return GetMainHead(repository);
        }

        if (commitSha.Length != 40 || commitSha.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("A commit identifier must be a full 40-character hexadecimal SHA-1.", nameof(commitSha));

        return repository.Lookup(commitSha, ObjectType.Commit) as Commit;
    }

    private static Commit ResolveFullCommit(Repository repository, string commitSha, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(commitSha)
            || commitSha.Length != 40
            || commitSha.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("A commit identifier must be a full 40-character hexadecimal SHA-1.", parameterName);
        }

        return repository.Lookup(commitSha, ObjectType.Commit) as Commit
            ?? throw new InvalidOperationException($"The commit was not found: {commitSha}");
    }

    private static Tree CreateTree(Repository repository, IReadOnlyList<ProjectArchiveFileDescriptor> entries)
    {
        var definition = new TreeDefinition();
        foreach (var entry in entries)
        {
            using var stream = entry.OpenRead();
            var blob = repository.ObjectDatabase.CreateBlob(stream);
            definition.Add(entry.ArchivePath, blob, Mode.NonExecutableFile);
        }

        return repository.ObjectDatabase.CreateTree(definition);
    }

    private static void MaterializeTree(
        Tree tree,
        string root,
        string prefix,
        CancellationToken cancellationToken)
    {
        foreach (var entry in tree)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateTreeSegment(entry.Name);
            var path = string.IsNullOrEmpty(prefix) ? entry.Name : $"{prefix}/{entry.Name}";
            ValidateGitPath(path);

            switch (entry.TargetType)
            {
                case TreeEntryTargetType.Tree:
                    if (entry.Mode != Mode.Directory || entry.Target is not Tree subtree)
                        throw new InvalidDataException($"The Git tree contains an invalid directory entry: {path}");
                    MaterializeTree(subtree, root, path, cancellationToken);
                    break;

                case TreeEntryTargetType.Blob:
                    if (entry.Mode != Mode.NonExecutableFile || entry.Target is not Blob blob)
                        throw new InvalidDataException($"The Git tree contains a non-regular file entry: {path}");
                    var destination = ResolveTreeDestination(root, path);
                    var parent = Path.GetDirectoryName(destination)
                        ?? throw new InvalidDataException($"The Git tree contains an invalid path: {path}");
                    Directory.CreateDirectory(parent);
                    EnsureNoReparsePoints(parent);
                    using (var source = blob.GetContentStream())
                    using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
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
                    throw new InvalidDataException($"The Git tree contains an unsupported entry: {path}");
            }
        }
    }

    private static string ResolveTreeDestination(string root, string relativePath)
    {
        var destination = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(root, destination);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", GetPathComparison())
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", GetPathComparison())
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", GetPathComparison()))
        {
            throw new InvalidDataException($"The Git tree path escaped the destination: {relativePath}");
        }

        return destination;
    }

    private static IReadOnlyList<ProjectArchiveFileDescriptor> NormalizeFiles(
        IReadOnlyCollection<ProjectArchiveFileDescriptor> files)
    {
        var entries = files
            .Select(entry => entry ?? throw new ArgumentException("Snapshot file descriptors cannot be null.", nameof(files)))
            .OrderBy(entry => entry.ArchivePath, StringComparer.Ordinal)
            .ToArray();

        var knownPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            ValidateGitPath(entry.ArchivePath);
            if (!knownPaths.Add(entry.ArchivePath))
                throw new ArgumentException($"The snapshot contains a duplicate path: {entry.ArchivePath}", nameof(files));

            var separator = entry.ArchivePath.IndexOf('/');
            while (separator > 0)
            {
                var parent = entry.ArchivePath[..separator];
                if (knownPaths.Contains(parent))
                    throw new ArgumentException($"A snapshot file conflicts with a directory path: {entry.ArchivePath}", nameof(files));
                separator = entry.ArchivePath.IndexOf('/', separator + 1);
            }
        }

        return entries;
    }

    private static void ValidateGitPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.StartsWith('/')
            || path.EndsWith('/')
            || path.Contains('\\')
            || path.Contains('\0')
            || path.Contains("//", StringComparison.Ordinal))
        {
            throw new ArgumentException($"The Git tree path is not a portable relative path: {path}", nameof(path));
        }

        var segments = path.Split('/');
        foreach (var segment in segments)
            ValidateTreeSegment(segment);
    }

    private static void ValidateTreeSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment)
            || segment is "." or ".." or ".git"
            || segment.Contains('/')
            || segment.Contains('\\')
            || segment.Any(char.IsControl)
            || segment.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0
            || IsWindowsDeviceName(segment)
            || segment.EndsWith('.')
            || segment.EndsWith(' '))
        {
            throw new InvalidDataException($"The Git tree contains an invalid path segment: {segment}");
        }
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

    private static object GetRepositoryLock(string repositoryPath) =>
        RepositoryLocks.GetOrAdd(repositoryPath, static _ => new object());

    private static void ValidateRepositoryId(Guid repositoryId)
    {
        if (repositoryId == Guid.Empty)
            throw new ArgumentException("The repository identity cannot be empty.", nameof(repositoryId));
    }

    private static StringComparison GetPathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private void ValidateExactHistoryPath(string path, Guid repositoryId)
    {
        var expected = GetRepositoryPath(repositoryId);
        if (!string.Equals(Path.GetFullPath(path), expected, GetPathComparison()))
            throw new InvalidOperationException("The local history path did not match the repository identity.");
        ValidatePathWithinHistoryRoot(path);
    }

    private void ValidateDeletionStage(GitRepositoryDeletionStage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ValidateRepositoryId(stage.RepositoryId);
        ValidateExactHistoryPath(stage.OriginalPath, stage.RepositoryId);
        ValidatePathWithinHistoryRoot(stage.StagedPath);
        var deletingSegment = $"{Path.DirectorySeparatorChar}.deleting{Path.DirectorySeparatorChar}";
        var alternateDeletingSegment = $"{Path.AltDirectorySeparatorChar}.deleting{Path.AltDirectorySeparatorChar}";
        if (!Path.GetFileName(stage.StagedPath).StartsWith($"{stage.RepositoryId:N}.", StringComparison.OrdinalIgnoreCase)
            || !stage.StagedPath.Contains(deletingSegment, GetPathComparison())
                && !stage.StagedPath.Contains(alternateDeletingSegment, GetPathComparison()))
            throw new InvalidOperationException("The staged local history path is not app-owned.");
    }

    private void ValidatePathWithinHistoryRoot(string path)
    {
        var root = Path.GetFullPath(_historyRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", GetPathComparison())
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", GetPathComparison())
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", GetPathComparison()))
            throw new InvalidOperationException("The local history path escaped the configured history root.");
    }

    private void EnsureNoPendingDeletion(Guid repositoryId)
    {
        var deletionRoot = Path.Combine(Path.GetFullPath(_historyRoot), ".deleting");
        if (!Directory.Exists(deletionRoot))
            return;

        EnsureNoReparsePoints(deletionRoot);
        var prefix = $"{repositoryId:N}.";
        var pending = Directory.EnumerateFileSystemEntries(deletionRoot)
            .FirstOrDefault(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (pending is not null)
            throw new InvalidOperationException(
                $"Local history deletion is already pending for repository {repositoryId:N}: {pending}");
    }

    private static Guid? ParseTombstoneRepositoryId(
        string name,
        out string? diagnostic)
    {
        diagnostic = null;
        var parts = name.Split('.');
        if (parts.Length != 3
            || parts[2] != "deleting"
            || parts[0].Length != 32
            || parts[1].Length != 32
            || !Guid.TryParseExact(parts[0], "N", out var repositoryId)
            || repositoryId == Guid.Empty
            || !Guid.TryParseExact(parts[1], "N", out _))
        {
            diagnostic = "The deletion tombstone name is not in the expected <repository-id>.<tombstone-id>.deleting format.";
            return null;
        }

        return repositoryId;
    }

    private void RemoveDeletionMarker(GitRepositoryDeletionStage stage)
    {
        EnsureNoReparsePoints(stage.StagedPath);
        if (!Directory.Exists(stage.StagedPath))
            return;

        try
        {
            EnsureNoNestedReparsePoints(stage.StagedPath);
            Directory.Delete(stage.StagedPath, recursive: true);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new GitRepositoryDeletionException(
                $"The local history deletion marker could not be removed: {stage.StagedPath}",
                stage,
                exception);
        }
    }

    private static void EnsureNoReparsePoints(string path)
    {
        var current = new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException($"History paths may not contain symbolic links or reparse points: {current.FullName}");
            current = current.Parent;
        }
    }

    private static void EnsureNoNestedReparsePoints(string root)
    {
        if (!Directory.Exists(root))
            return;

        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var directory in current.EnumerateDirectories())
            {
                if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException($"History repositories may not contain symbolic links or reparse points: {directory.FullName}");
                pending.Push(directory);
            }

            foreach (var file in current.EnumerateFiles())
            {
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException($"History repositories may not contain symbolic links or reparse points: {file.FullName}");
            }
        }
    }

    private static GitCommitMetadata? ToMetadata(Commit? commit) => commit is null
        ? null
        : new GitCommitMetadata(
            commit.Sha,
            commit.Tree.Sha,
            commit.Message,
            commit.Author.Name,
            commit.Author.Email,
            commit.Author.When,
            commit.Committer.Name,
            commit.Committer.Email,
            commit.Committer.When,
            commit.Parents.Select(parent => parent.Sha).ToArray());
}
