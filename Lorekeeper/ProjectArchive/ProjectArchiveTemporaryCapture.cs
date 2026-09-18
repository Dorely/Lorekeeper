using System.Text.Json;

namespace Lorekeeper.ProjectArchive;

/// <summary>
/// Owns a short-lived, containment-checked local staging directory. Disposing
/// the capture removes only files created below that directory.
/// </summary>
public sealed class ProjectArchiveTemporaryCapture : IDisposable, IAsyncDisposable
{
    private const string DirectoryPrefix = "lorekeeper-project-archive-";
    private bool _disposed;

    private ProjectArchiveTemporaryCapture(string rootDirectory)
    {
        RootDirectory = rootDirectory;
    }

    public string RootDirectory { get; }

    public static ProjectArchiveTemporaryCapture Create(string? parentDirectory = null)
    {
        var parent = Path.GetFullPath(parentDirectory ?? Path.Combine(Path.GetTempPath(), "Lorekeeper", "project-archive"));
        Directory.CreateDirectory(parent);
        EnsureNoReparsePoints(parent);
        var root = Path.Combine(parent, DirectoryPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new ProjectArchiveTemporaryCapture(root);
    }

    public async Task<ProjectArchiveFileDescriptor> CaptureAsync(
        Stream source,
        string archivePath,
        string kind,
        string mediaType,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        var normalizedPath = ProjectArchivePath.Normalize(archivePath);
        var destinationPath = ResolveOwnedPath(normalizedPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using (var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[81_920];
            long total = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0)
                    break;

                total = checked(total + read);
                if (total > maximumBytes)
                    throw new ProjectArchiveException("Archive staging input exceeds its configured byte limit.");
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }

        return await ProjectArchiveFileDescriptor.CreateAsync(normalizedPath, kind, mediaType, destinationPath, cancellationToken);
    }

    public Task<ProjectArchiveFileDescriptor> CaptureJsonAsync<T>(
        T value,
        string archivePath,
        string kind,
        long maximumBytes,
        JsonSerializerOptions options,
        CancellationToken cancellationToken = default) =>
        CaptureWrittenAsync(
            archivePath,
            kind,
            "application/json; charset=utf-8",
            maximumBytes,
            (stream, token) => JsonSerializer.SerializeAsync(stream, value, options, token),
            cancellationToken);

    public async Task<ProjectArchiveFileDescriptor> CaptureWrittenAsync(
        string archivePath,
        string kind,
        string mediaType,
        long maximumBytes,
        Func<Stream, CancellationToken, Task> write,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(write);
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        var normalizedPath = ProjectArchivePath.Normalize(archivePath);
        var destinationPath = ResolveOwnedPath(normalizedPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        try
        {
            await using (var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var bounded = new ProjectArchiveWriteLimitStream(destination, maximumBytes))
            {
                await write(bounded, cancellationToken);
                await bounded.FlushAsync(cancellationToken);
            }
        }
        catch
        {
            if (File.Exists(destinationPath))
                File.Delete(destinationPath);
            throw;
        }

        return await ProjectArchiveFileDescriptor.CreateAsync(normalizedPath, kind, mediaType, destinationPath, cancellationToken);
    }

    public string ResolveOwnedPath(string archivePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalizedPath = ProjectArchivePath.Normalize(archivePath);
        var candidate = Path.GetFullPath(Path.Combine(RootDirectory, normalizedPath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = RootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ProjectArchiveException("Archive staging path escaped its owned root.");
        return candidate;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        DeleteOwnedRoot();
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void DeleteOwnedRoot()
    {
        if (!Directory.Exists(RootDirectory))
            return;

        var parent = Path.GetDirectoryName(RootDirectory)
            ?? throw new ProjectArchiveException("Archive staging root has no parent.");
        var expectedPrefix = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar + DirectoryPrefix;
        if (!Path.GetFullPath(RootDirectory).StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ProjectArchiveException("Refusing to delete a non-owned archive staging directory.");

        EnsureNoReparsePoints(RootDirectory);
        Directory.Delete(RootDirectory, recursive: true);
    }

    private static void EnsureNoReparsePoints(string root)
    {
        var directory = new DirectoryInfo(root);
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new ProjectArchiveException("Archive staging directories cannot be reparse points.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            if (File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint))
                throw new ProjectArchiveException("Archive staging content cannot contain reparse points.");
        }
    }

}
