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
}

public sealed class ProjectMutationCoordinator : IProjectMutationCoordinator
{
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
                await fileLock.DisposeAsync();
                projectLock.Release();
            }
        }
    }
}
