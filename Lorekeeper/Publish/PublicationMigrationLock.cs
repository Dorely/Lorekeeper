using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Principal;
using Lorekeeper.Persistence;
using Microsoft.Data.Sqlite;

namespace Lorekeeper.Publish;

internal static class PublicationMigrationLock
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessLocks =
        new(StringComparer.OrdinalIgnoreCase);

    public static async Task<IAsyncDisposable> AcquireAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        var databasePath = DatabasePath(connectionString);
        var processLock = ProcessLocks.GetOrAdd(databasePath, static _ => new SemaphoreSlim(1, 1));
        await processLock.WaitAsync(cancellationToken);
        try
        {
            var lockPath = LockPath(connectionString);
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
            ProtectPath(Path.GetDirectoryName(lockPath)!, directory: true);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var stream = new FileStream(
                        lockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: 1,
                        FileOptions.Asynchronous);
                    ProtectPath(lockPath, directory: false);
                    return new Lease(stream, processLock);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                }
            }
        }
        catch
        {
            processLock.Release();
            throw;
        }
    }

    internal static string LockPath(string connectionString)
    {
        var root = Path.GetDirectoryName(DatabasePath(connectionString))!;
        return Path.Combine(root, ".migration-backups", "publication", "migration.lock");
    }

    private static string DatabasePath(string connectionString) =>
        Path.GetFullPath(new SqliteConnectionStringBuilder(connectionString).DataSource);

    private static void ProtectPath(string path, bool directory)
    {
        if (OperatingSystem.IsWindows())
        {
            var identity = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
            FileSystemSecurity security = directory ? new DirectorySecurity() : new FileSecurity();
            security.SetOwner(identity);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                identity,
                FileSystemRights.FullControl,
                directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow));
            if (directory)
                new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
            else
                new FileInfo(path).SetAccessControl((FileSecurity)security);
            return;
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;
        File.SetUnixFileMode(
            path,
            directory
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private sealed class Lease(FileStream stream, SemaphoreSlim processLock) : IAsyncDisposable
    {
        private bool _disposed;

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;
            await stream.DisposeAsync();
            processLock.Release();
        }
    }
}
