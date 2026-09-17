using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lorekeeper.ProjectArchive;

public sealed record ProjectArchiveManifestEntry(
    string Path,
    string Kind,
    string MediaType,
    long Length,
    string Sha256);

public sealed record ProjectArchiveSchemaVersions(
    int Manuscript,
    int ArchiveRecord,
    int HistorySnapshot)
{
    internal void Validate()
    {
        if (Manuscript <= 0 || ArchiveRecord <= 0 || HistorySnapshot <= 0)
            throw new ProjectArchiveException("Archive schema versions must be positive.");
    }
}

public sealed record ProjectArchiveManifest(
    string FormatId,
    int EnvelopeVersion,
    string ProjectId,
    ProjectArchiveSchemaVersions SchemaVersions,
    ProjectDependencyTraversalPolicy Policy,
    IReadOnlyList<ProjectArchiveManifestEntry> Entries,
    IReadOnlyList<string> Warnings,
    string ContentHash,
    string ManifestHash)
{
    public static ProjectArchiveManifest Create(
        string projectId,
        ProjectArchiveSchemaVersions schemaVersions,
        ProjectDependencyTraversalPolicy policy,
        IEnumerable<ProjectArchiveManifestEntry> entries,
        IEnumerable<string> warnings)
    {
        ValidateProjectId(projectId);
        ArgumentNullException.ThrowIfNull(schemaVersions);
        schemaVersions.Validate();
        if (schemaVersions.ArchiveRecord != ProjectArchiveContract.RecordSchemaVersion)
            throw new ProjectArchiveException("Archive manifest declares an unsupported archive record schema.");
        if (!Enum.IsDefined(policy))
            throw new ArgumentOutOfRangeException(nameof(policy));
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(warnings);

        var canonicalEntries = entries
            .Select(ValidateEntry)
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ToArray();
        EnsureUnique(canonicalEntries);
        var canonicalWarnings = warnings
            .Select(ValidateWarning)
            .OrderBy(warning => warning, StringComparer.Ordinal)
            .ToArray();
        EnsureUniqueWarnings(canonicalWarnings);
        var contentHash = ProjectArchiveCanonicalJson.ComputeContentHash(canonicalEntries, canonicalWarnings);
        var withoutHash = new ProjectArchiveManifest(
            ProjectArchiveContract.FormatId,
            ProjectArchiveContract.EnvelopeVersion,
            projectId,
            schemaVersions,
            policy,
            Array.AsReadOnly(canonicalEntries),
            Array.AsReadOnly(canonicalWarnings),
            contentHash,
            string.Empty);
        return withoutHash with { ManifestHash = ProjectArchiveCanonicalJson.ComputeManifestHash(withoutHash) };
    }

    internal static ProjectArchiveManifestEntry ValidateEntry(ProjectArchiveManifestEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var path = ProjectArchivePath.Normalize(entry.Path);
        if (string.IsNullOrWhiteSpace(entry.Kind)
            || string.IsNullOrWhiteSpace(entry.MediaType)
            || entry.Length < 0
            || !IsSha256(entry.Sha256))
        {
            throw new ProjectArchiveException("Archive manifest contains an invalid file declaration.");
        }

        return entry with { Path = path, Sha256 = entry.Sha256.ToLowerInvariant() };
    }

    internal static void EnsureUnique(IEnumerable<ProjectArchiveManifestEntry> entries)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!keys.Add(ProjectArchivePath.CollisionKey(entry.Path)))
                throw new ProjectArchiveException("Archive manifest contains duplicate or normalization-colliding paths.");
        }
    }

    internal static string ValidateWarning(string warning)
    {
        if (string.IsNullOrWhiteSpace(warning)
            || !string.Equals(warning, warning.Trim(), StringComparison.Ordinal)
            || !string.Equals(warning, warning.Normalize(NormalizationForm.FormC), StringComparison.Ordinal))
        {
            throw new ProjectArchiveException("Archive manifest contains an invalid warning code.");
        }
        return warning;
    }

    internal static void EnsureUniqueWarnings(IEnumerable<string> warnings)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var warning in warnings)
        {
            if (!values.Add(warning))
                throw new ProjectArchiveException("Archive manifest contains duplicate warning codes.");
        }
    }

    internal static void ValidateProjectId(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId)
            || !string.Equals(projectId, projectId.Trim(), StringComparison.Ordinal)
            || !string.Equals(projectId, projectId.Normalize(NormalizationForm.FormC), StringComparison.Ordinal))
        {
            throw new ProjectArchiveException("Archive manifest must declare a non-empty canonical project ID.");
        }
    }

    internal static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character => Uri.IsHexDigit(character));
}

public static class ProjectArchiveCanonicalJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter<ProjectDependencyTraversalPolicy>() },
    };

    public static void WriteManifest(Stream target, ProjectArchiveManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(manifest);
        JsonSerializer.Serialize(target, manifest, Options);
    }

    public static string ComputeContentHash(
        IEnumerable<ProjectArchiveManifestEntry> entries,
        IEnumerable<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(warnings);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in entries.OrderBy(item => item.Path, StringComparer.Ordinal))
        {
            Append(hash, entry.Path);
            Append(hash, entry.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, entry.Sha256.ToLowerInvariant());
        }
        foreach (var warning in warnings.OrderBy(item => item, StringComparer.Ordinal))
            Append(hash, warning);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static string ComputeManifestHash(ProjectArchiveManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var memory = new MemoryStream();
        WriteManifest(memory, manifest with { ManifestHash = string.Empty });
        return Convert.ToHexStringLower(SHA256.HashData(memory.GetBuffer().AsSpan(0, checked((int)memory.Length))));
    }

    internal static ProjectArchiveManifest ReadCanonicalManifest(Stream source, long maximumBytes)
    {
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        using var memory = new MemoryStream();
        var buffer = new byte[81_920];
        long total = 0;
        while (true)
        {
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            total = checked(total + read);
            if (total > maximumBytes)
                throw new ProjectArchiveException("Archive manifest exceeds its configured byte limit.");
            memory.Write(buffer, 0, read);
        }

        ProjectArchiveManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ProjectArchiveManifest>(memory.GetBuffer().AsSpan(0, checked((int)memory.Length)), Options)
                ?? throw new ProjectArchiveException("Archive manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new ProjectArchiveException("Archive manifest is malformed JSON.", exception);
        }

        using var canonical = new MemoryStream();
        WriteManifest(canonical, manifest);
        if (!memory.GetBuffer().AsSpan(0, checked((int)memory.Length)).SequenceEqual(canonical.GetBuffer().AsSpan(0, checked((int)canonical.Length))))
            throw new ProjectArchiveException("Archive manifest is not canonical JSON.");
        return manifest;
    }

    private static void Append(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }
}
