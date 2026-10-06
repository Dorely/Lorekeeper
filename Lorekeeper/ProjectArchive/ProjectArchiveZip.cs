using System.IO.Compression;
using System.Security.Cryptography;

namespace Lorekeeper.ProjectArchive;

public sealed record ProjectArchiveReadResult(ProjectArchiveManifest Manifest);

public sealed class ProjectArchiveStagedArchive : IDisposable, IAsyncDisposable
{
    private bool _disposed;

    internal ProjectArchiveStagedArchive(
        ProjectArchiveTemporaryCapture capture,
        ProjectArchiveFileDescriptor archiveFile,
        ProjectArchiveManifest manifest)
    {
        Capture = capture;
        ArchiveFile = archiveFile;
        Manifest = manifest;
    }

    public ProjectArchiveTemporaryCapture Capture { get; }
    public ProjectArchiveFileDescriptor ArchiveFile { get; }
    public ProjectArchiveManifest Manifest { get; }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Capture.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

public static class ProjectArchiveZip
{
    public static async Task<ProjectArchiveManifest> WriteAsync(
        Stream destination,
        string projectId,
        ProjectArchiveSchemaVersions schemaVersions,
        ProjectDependencyTraversalPolicy policy,
        IEnumerable<ProjectArchiveFileDescriptor> descriptors,
        IEnumerable<string> warnings,
        ProjectArchiveLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(descriptors);
        if (!destination.CanWrite)
            throw new ArgumentException("Archive destination must be writable.", nameof(destination));

        var effectiveLimits = limits ?? ProjectArchiveLimits.Default;
        effectiveLimits.Validate();
        var files = descriptors.OrderBy(item => item.ArchivePath, StringComparer.Ordinal).ToArray();
        ValidateDescriptors(files, effectiveLimits);
        var manifest = ProjectArchiveManifest.Create(projectId, schemaVersions, policy, files.Select(file => new ProjectArchiveManifestEntry(
            file.ArchivePath,
            file.Kind,
            file.MediaType,
            file.Length,
            file.Sha256)), warnings);

        using var manifestBytes = new MemoryStream();
        using (var manifestLimit = new ProjectArchiveWriteLimitStream(manifestBytes, effectiveLimits.MaximumManifestBytes))
            ProjectArchiveCanonicalJson.WriteManifest(manifestLimit, manifest);
        if (checked(files.Sum(file => file.Length) + manifestBytes.Length) > effectiveLimits.MaximumExpandedBytes)
            throw new ProjectArchiveException("Archive expanded content and manifest exceed the configured byte limit.");
        if (destination.CanSeek)
        {
            await WriteEntriesAsync(destination, files, manifestBytes, effectiveLimits, cancellationToken);
            return manifest;
        }

        // ZipArchive closes entries with synchronous writes, which HTTP response bodies reject, so a forward-only
        // destination receives the archive from a temporary file copied asynchronously.
        var stagingDirectory = Path.Combine(Path.GetTempPath(), "Lorekeeper", "project-archive");
        Directory.CreateDirectory(stagingDirectory);
        await using var staged = new FileStream(
            Path.Combine(stagingDirectory, $"export-{Guid.NewGuid():N}.zip"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            81_920,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        await WriteEntriesAsync(staged, files, manifestBytes, effectiveLimits, cancellationToken);
        staged.Position = 0;
        await staged.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);
        return manifest;
    }

    private static async Task WriteEntriesAsync(
        Stream destination,
        ProjectArchiveFileDescriptor[] files,
        MemoryStream manifestBytes,
        ProjectArchiveLimits effectiveLimits,
        CancellationToken cancellationToken)
    {
        using var destinationLimit = new ProjectArchiveWriteLimitStream(destination, effectiveLimits.MaximumCompressedBytes);
        using (var archive = new ZipArchive(destinationLimit, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = archive.CreateEntry(file.ArchivePath, CompressionLevel.Optimal);
                entry.LastWriteTime = ZipTimestamp;
                await using var input = file.OpenRead();
                await using var output = entry.Open();
                await CopyAndVerifyAsync(input, output, file.Length, file.Sha256, effectiveLimits.MaximumEntryBytes, cancellationToken);
            }

            var manifestEntry = archive.CreateEntry(ProjectArchiveContract.ManifestPath, CompressionLevel.Optimal);
            manifestEntry.LastWriteTime = ZipTimestamp;
            await using var manifestStream = manifestEntry.Open();
            manifestBytes.Position = 0;
            await manifestBytes.CopyToAsync(manifestStream, cancellationToken);
        }
    }

    public static Task<ProjectArchiveReadResult> ReadAsync(
        Stream source,
        ProjectArchiveLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var effectiveLimits = limits ?? ProjectArchiveLimits.Default;
        effectiveLimits.Validate();
        try
        {
            if (!source.CanSeek)
                throw new ProjectArchiveException("Stage non-seekable archive input before validation to keep memory bounded.");
            if (source.Length > effectiveLimits.MaximumCompressedBytes)
                throw new ProjectArchiveException("Archive input exceeds its configured compressed byte limit.");
            using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
            ValidateZipEntries(archive, effectiveLimits);
            var manifestEntry = archive.Entries.Single(entry => entry.FullName == ProjectArchiveContract.ManifestPath);
            ProjectArchiveManifest manifest;
            using (var manifestStream = manifestEntry.Open())
                manifest = ProjectArchiveCanonicalJson.ReadCanonicalManifest(manifestStream, effectiveLimits.MaximumManifestBytes);
            ValidateManifest(manifest, archive, effectiveLimits, cancellationToken);
            return Task.FromResult(new ProjectArchiveReadResult(manifest));
        }
        catch (InvalidDataException exception)
        {
            throw new ProjectArchiveException("Archive ZIP container is invalid.", exception);
        }
    }

    public static async Task<ProjectArchiveStagedArchive> StageAsync(
        Stream source,
        ProjectArchiveLimits? limits = null,
        string? stagingParentDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveLimits = limits ?? ProjectArchiveLimits.Default;
        effectiveLimits.Validate();
        var capture = ProjectArchiveTemporaryCapture.Create(stagingParentDirectory);
        try
        {
            var archiveFile = await capture.CaptureAsync(
                source,
                "staged/archive.lorekeeper",
                "archive-container",
                "application/vnd.lorekeeper.archive+zip",
                effectiveLimits.MaximumCompressedBytes,
                cancellationToken);
            await using var input = archiveFile.OpenRead();
            var result = await ReadAsync(input, effectiveLimits, cancellationToken);
            return new ProjectArchiveStagedArchive(capture, archiveFile, result.Manifest);
        }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    private static void ValidateDescriptors(
        IReadOnlyList<ProjectArchiveFileDescriptor> descriptors,
        ProjectArchiveLimits limits)
    {
        if (descriptors.Count > limits.MaximumEntryCount - 1)
            throw new ProjectArchiveException("Archive has too many entries.");
        var total = 0L;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            if (descriptor.ArchivePath == ProjectArchiveContract.ManifestPath)
                throw new ProjectArchiveException("Archive payload cannot replace manifest.json.");
            if (descriptor.Length > limits.MaximumEntryBytes)
                throw new ProjectArchiveException("Archive entry exceeds its configured byte limit.");
            if (!paths.Add(ProjectArchivePath.CollisionKey(descriptor.ArchivePath)))
                throw new ProjectArchiveException("Archive contains duplicate or normalization-colliding paths.");
            total = checked(total + descriptor.Length);
            if (total > limits.MaximumExpandedBytes)
                throw new ProjectArchiveException("Archive expanded content exceeds its configured byte limit.");
        }
    }

    private static void ValidateZipEntries(ZipArchive archive, ProjectArchiveLimits limits)
    {
        if (archive.Entries.Count == 0 || archive.Entries.Count > limits.MaximumEntryCount)
            throw new ProjectArchiveException("Archive entry count is invalid.");

        var paths = new HashSet<string>(StringComparer.Ordinal);
        long compressed = 0;
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            var path = ProjectArchivePath.Normalize(entry.FullName);
            if (!string.Equals(path, entry.FullName, StringComparison.Ordinal)
                || entry.FullName.EndsWith("/", StringComparison.Ordinal)
                || IsLinkOrReparsePoint(entry))
            {
                throw new ProjectArchiveException("Archive contains a non-regular or non-canonical entry.");
            }
            if (!paths.Add(ProjectArchivePath.CollisionKey(path)))
                throw new ProjectArchiveException("Archive contains duplicate or normalization-colliding entries.");
            if (entry.Length < 0 || entry.CompressedLength < 0 || entry.Length > limits.MaximumEntryBytes)
                throw new ProjectArchiveException("Archive declares an oversized expanded entry.");
            compressed = checked(compressed + entry.CompressedLength);
            expanded = checked(expanded + entry.Length);
            if (compressed > limits.MaximumCompressedBytes || expanded > limits.MaximumExpandedBytes)
                throw new ProjectArchiveException("Archive declares oversized compressed or expanded content.");
        }

        if (!paths.Contains(ProjectArchivePath.CollisionKey(ProjectArchiveContract.ManifestPath))
            || archive.Entries.Count(entry => entry.FullName == ProjectArchiveContract.ManifestPath) != 1)
        {
            throw new ProjectArchiveException("Archive must contain exactly one manifest.json entry.");
        }
    }

    private static void ValidateManifest(
        ProjectArchiveManifest manifest,
        ZipArchive archive,
        ProjectArchiveLimits limits,
        CancellationToken cancellationToken)
    {
        if (manifest.FormatId != ProjectArchiveContract.FormatId
            || manifest.EnvelopeVersion != ProjectArchiveContract.EnvelopeVersion
            || manifest.SchemaVersions is null
            || !ProjectArchiveContract.CanReadRecordSchema(manifest.SchemaVersions.ArchiveRecord)
            || !ProjectArchiveContract.CanReadManuscriptSchema(manifest.SchemaVersions.Manuscript)
            || !ProjectArchiveContract.CanReadHistorySchema(manifest.SchemaVersions.HistorySnapshot)
            || manifest.Entries is null
            || manifest.Warnings is null
            || !Enum.IsDefined(manifest.Policy))
        {
            throw new ProjectArchiveException("Archive manifest declares an unsupported contract.");
        }

        ProjectArchiveManifest.ValidateProjectId(manifest.ProjectId);
        manifest.SchemaVersions.Validate();
        var declaredEntries = manifest.Entries.ToArray();
        var entries = declaredEntries.Select(ProjectArchiveManifest.ValidateEntry).ToArray();
        if (!declaredEntries.SequenceEqual(entries))
            throw new ProjectArchiveException("Archive manifest contains non-canonical file declarations.");
        if (!entries.SequenceEqual(entries.OrderBy(entry => entry.Path, StringComparer.Ordinal)))
            throw new ProjectArchiveException("Archive manifest entries are not deterministically ordered.");
        ProjectArchiveManifest.EnsureUnique(entries);
        var warnings = manifest.Warnings.Select(ProjectArchiveManifest.ValidateWarning).ToArray();
        if (!warnings.SequenceEqual(warnings.OrderBy(warning => warning, StringComparer.Ordinal)))
            throw new ProjectArchiveException("Archive manifest warnings are not deterministically ordered.");
        ProjectArchiveManifest.EnsureUniqueWarnings(warnings);
        if (entries.Length > limits.MaximumEntryCount - 1
            || entries.Any(entry => entry.Length > limits.MaximumEntryBytes)
            || entries.Sum(entry => entry.Length) > limits.MaximumExpandedBytes)
        {
            throw new ProjectArchiveException("Archive manifest declares oversized content.");
        }
        if (!string.Equals(manifest.ContentHash, ProjectArchiveCanonicalJson.ComputeContentHash(entries, warnings), StringComparison.Ordinal)
            || !string.Equals(manifest.ManifestHash, ProjectArchiveCanonicalJson.ComputeManifestHash(manifest), StringComparison.Ordinal))
        {
            throw new ProjectArchiveException("Archive manifest hash does not match its contents.");
        }

        var zipEntries = archive.Entries
            .Where(entry => entry.FullName != ProjectArchiveContract.ManifestPath)
            .ToDictionary(entry => entry.FullName, StringComparer.Ordinal);
        if (zipEntries.Count != entries.Length
            || entries.Any(entry => !zipEntries.ContainsKey(entry.Path)))
        {
            throw new ProjectArchiveException("Archive manifest file set does not match ZIP entries.");
        }

        foreach (var declared in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var zipEntry = zipEntries[declared.Path];
            if (zipEntry.Length != declared.Length)
                throw new ProjectArchiveException($"Archive entry '{declared.Path}' length does not match its manifest.");
            using var stream = zipEntry.Open();
            VerifyStream(stream, declared.Length, declared.Sha256, limits.MaximumEntryBytes, cancellationToken);
        }
    }

    private static async Task CopyAndVerifyAsync(
        Stream input,
        Stream output,
        long expectedLength,
        string expectedHash,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81_920];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                break;
            total = checked(total + read);
            if (total > maximumBytes)
                throw new ProjectArchiveException("Archive entry exceeded its configured byte limit while streaming.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        EnsureHash(total, hash.GetHashAndReset(), expectedLength, expectedHash);
    }

    private static void VerifyStream(
        Stream input,
        long expectedLength,
        string expectedHash,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81_920];
        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            total = checked(total + read);
            if (total > maximumBytes)
                throw new ProjectArchiveException("Archive entry exceeded its configured byte limit while reading.");
            hash.AppendData(buffer, 0, read);
        }
        EnsureHash(total, hash.GetHashAndReset(), expectedLength, expectedHash);
    }

    private static void EnsureHash(long actualLength, byte[] actualHash, long expectedLength, string expectedHash)
    {
        if (actualLength != expectedLength
            || !string.Equals(Convert.ToHexStringLower(actualHash), expectedHash, StringComparison.Ordinal))
        {
            throw new ProjectArchiveException("Archive entry content does not match its declared length or SHA-256 hash.");
        }
    }

    private static bool IsLinkOrReparsePoint(ZipArchiveEntry entry)
    {
        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        var windowsAttributes = entry.ExternalAttributes & 0xFFFF;
        return unixType == 0xA000 || (windowsAttributes & (int)FileAttributes.ReparsePoint) != 0;
    }

    private static readonly DateTimeOffset ZipTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
}
