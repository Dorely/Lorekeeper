using System.Security.Cryptography;
using System.Text;

namespace Lorekeeper.ProjectArchive;

/// <summary>
/// An immutable declaration for one archive file. Its bytes remain on a stable
/// local file and are opened only when an archive consumer needs to stream them.
/// </summary>
public sealed class ProjectArchiveFileDescriptor
{
    private ProjectArchiveFileDescriptor(
        string archivePath,
        string kind,
        string mediaType,
        string localPath,
        long length,
        string sha256)
    {
        ArchivePath = ProjectArchivePath.Normalize(archivePath);
        Kind = Require(kind, nameof(kind));
        MediaType = Require(mediaType, nameof(mediaType));
        LocalPath = Path.GetFullPath(Require(localPath, nameof(localPath)));
        Length = length;
        Sha256 = sha256;
    }

    public string ArchivePath { get; }
    public string Kind { get; }
    public string MediaType { get; }
    public string LocalPath { get; }
    public long Length { get; }
    public string Sha256 { get; }

    public static async Task<ProjectArchiveFileDescriptor> CreateAsync(
        string archivePath,
        string kind,
        string mediaType,
        string stableLocalPath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(Require(stableLocalPath, nameof(stableLocalPath)));
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new ProjectArchiveException("Archive content must be a regular local file, not a missing file or reparse point.");

        var (length, sha256) = await ComputeHashAsync(fullPath, cancellationToken);
        return new ProjectArchiveFileDescriptor(archivePath, kind, mediaType, fullPath, length, sha256);
    }

    public Stream OpenRead()
    {
        var info = new FileInfo(LocalPath);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length != Length)
            throw new ProjectArchiveException($"Archive source '{ArchivePath}' changed after capture.");

        return new FileStream(LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    internal static async Task<(long Length, string Sha256)> ComputeHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81_920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                break;

            hash.AppendData(buffer, 0, read);
            total = checked(total + read);
        }

        return (total, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static string Require(string value, string name) =>
        string.IsNullOrWhiteSpace(value) || value.IndexOf('\0') >= 0
            ? throw new ArgumentException("A non-empty value without a null character is required.", name)
            : value;
}

public static class ProjectArchivePath
{
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.IndexOf('\\') >= 0
            || path.IndexOf('\0') >= 0
            || Path.IsPathRooted(path)
            || path.StartsWith("/", StringComparison.Ordinal)
            || path.IndexOf(':') >= 0)
        {
            throw new ProjectArchiveException($"Archive path '{path}' is unsafe.");
        }

        var normalized = path.Normalize(NormalizationForm.FormC);
        if (!string.Equals(path, normalized, StringComparison.Ordinal)
            || normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ProjectArchiveException($"Archive path '{path}' is not canonical.");
        }

        return normalized;
    }

    public static string CollisionKey(string path) => Normalize(path).ToUpperInvariant();
}
