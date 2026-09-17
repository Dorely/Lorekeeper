using System.Security.Cryptography;

namespace Lorekeeper.ImportExport;

/// <summary>
/// Owns the durable, app-private files behind import jobs.  The database stores
/// only the opaque file key and integrity declaration; project bytes never
/// travel through a JSON/base64 job column.
/// </summary>
public interface IProjectImportFileStore
{
    Task<ProjectImportStagedFile> StageAsync(Stream source, string fileName, long maximumBytes, CancellationToken cancellationToken = default);
    Stream OpenRead(ProjectImportJobFileKey key);
    void Delete(ProjectImportJobFileKey key);
}

public sealed record ProjectImportStagedFile(ProjectImportJobFileKey Key, long Length, string Sha256);

public readonly record struct ProjectImportJobFileKey(string Value)
{
    public override string ToString() => Value;
}

public sealed class ProjectImportFileStore : IProjectImportFileStore
{
    private const string Prefix = "import-";
    private readonly string _root;

    public ProjectImportFileStore()
        : this(Path.Combine(Path.GetTempPath(), "Lorekeeper", "project-imports"))
    {
    }

    internal ProjectImportFileStore(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
        EnsureRegularDirectory(_root);
    }

    public async Task<ProjectImportStagedFile> StageAsync(
        Stream source,
        string fileName,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new ArgumentException("Import source must be readable.", nameof(source));
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        var key = new ProjectImportJobFileKey(Prefix + Guid.NewGuid().ToString("N") + ".bin");
        var path = Resolve(key);
        try
        {
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81_920];
            long total = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0)
                    break;
                total = checked(total + read);
                if (total > maximumBytes)
                    throw new InvalidDataException("Import file exceeds the configured upload limit.");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            if (total == 0)
                throw new InvalidDataException("Import file is empty.");
            await output.FlushAsync(cancellationToken);
            return new ProjectImportStagedFile(key, total, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        catch
        {
            Delete(key);
            throw;
        }
    }

    public Stream OpenRead(ProjectImportJobFileKey key)
    {
        var path = Resolve(key);
        if (!File.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("The staged import file is unavailable or unsafe.");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public void Delete(ProjectImportJobFileKey key)
    {
        var path = Resolve(key);
        if (!File.Exists(path))
            return;
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Refusing to delete a reparse-point import staging file.");
        File.Delete(path);
    }

    private string Resolve(ProjectImportJobFileKey key)
    {
        if (string.IsNullOrWhiteSpace(key.Value)
            || !key.Value.StartsWith(Prefix, StringComparison.Ordinal)
            || key.Value.IndexOfAny(['/', '\\', ':', '\0']) >= 0)
        {
            throw new InvalidDataException("Import staging key is invalid.");
        }

        var path = Path.GetFullPath(Path.Combine(_root, key.Value));
        var prefix = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Import staging path escaped its owned root.");
        return path;
    }

    private static void EnsureRegularDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Import staging directory cannot be a reparse point.");
    }
}
