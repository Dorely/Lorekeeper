using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Lorekeeper.Persistence;

public interface IProjectMutationCoordinator
{
    ValueTask<IAsyncDisposable> AcquireAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    IDisposable ShareWithNestedOperations(Guid projectId);
}

public sealed class ProjectMutationCoordinator : IProjectMutationCoordinator
{
    private static readonly AsyncLocal<ProjectMutationScope?> Ambient = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = [];
    private readonly string _crossProcessLockDirectory;

    public ProjectMutationCoordinator(string? databaseConnectionString = null)
    {
        _crossProcessLockDirectory = ResolveLockDirectory(databaseConnectionString);
    }

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        if (Ambient.Value is { } current)
        {
            if (current.ProjectId != projectId)
            {
                throw new InvalidOperationException(
                    "A project mutation cannot acquire a different project while another project mutation lease is active.");
            }

            return NoopReleaser.Instance;
        }

        var projectLock = _locks.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        await projectLock.WaitAsync(cancellationToken);
        try
        {
            var fileLock = await AcquireFileLockAsync(projectId, cancellationToken);
            return new Releaser(projectLock, fileLock);
        }
        catch
        {
            projectLock.Release();
            throw;
        }
    }

    public IDisposable ShareWithNestedOperations(Guid projectId)
    {
        var previous = Ambient.Value;
        if (previous is not null && previous.ProjectId != projectId)
        {
            throw new InvalidOperationException(
                "A project mutation cannot share a different project while another project mutation lease is active.");
        }
        if (previous is not null)
            return NoopReleaser.Instance;
        var scope = new ProjectMutationScope(projectId);
        Ambient.Value = scope;
        return new AmbientReleaser(scope);
    }

    private sealed record ProjectMutationScope(Guid ProjectId);

    private sealed class NoopReleaser : IDisposable, IAsyncDisposable
    {
        public static NoopReleaser Instance { get; } = new();

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private async Task<FileStream> AcquireFileLockAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_crossProcessLockDirectory);
        var lockPath = Path.Combine(_crossProcessLockDirectory, $"{projectId:N}.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }
        }
    }

    private static string ResolveLockDirectory(string? databaseConnectionString)
    {
        if (string.IsNullOrWhiteSpace(databaseConnectionString))
        {
            return Path.Combine(
                Path.GetTempPath(),
                "Lorekeeper",
                "mutation-locks",
                $"process-{Environment.ProcessId}");
        }

        var dataSource = new SqliteConnectionStringBuilder(databaseConnectionString).DataSource;
        if (string.IsNullOrWhiteSpace(dataSource)
            || dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Project mutation locking requires a file-backed SQLite data source.");
        }

        var databasePath = Path.GetFullPath(dataSource);
        var databaseKey = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(databasePath)))[..16];
        return Path.Combine(
            Path.GetDirectoryName(databasePath)!,
            ".mutation-locks",
            databaseKey);
    }

    private sealed class Releaser(
        SemaphoreSlim projectLock,
        FileStream fileLock) : IAsyncDisposable
    {
        private bool _released;

        public async ValueTask DisposeAsync()
        {
            if (!_released)
            {
                _released = true;
                try
                {
                    await fileLock.DisposeAsync();
                }
                finally
                {
                    projectLock.Release();
                }
            }
        }
    }

    private sealed class AmbientReleaser(ProjectMutationScope scope) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (ReferenceEquals(Ambient.Value, scope))
                Ambient.Value = null;
        }
    }
}
