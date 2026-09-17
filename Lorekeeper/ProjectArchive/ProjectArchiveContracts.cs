using System.Text.Json.Serialization;

namespace Lorekeeper.ProjectArchive;

/// <summary>
/// Stable identities for the portable project archive boundary. JSON project
/// documents retain their own import-only format identity.
/// </summary>
public static class ProjectArchiveContract
{
    public const string FormatId = "lorekeeper.archive";
    public const int EnvelopeVersion = 1;
    public const int RecordSchemaVersion = 1;
    public const int ManuscriptSchemaVersion = 5;
    public const int HistorySnapshotSchemaVersion = 8;
    public const string ManifestPath = "manifest.json";
}

[JsonConverter(typeof(JsonStringEnumConverter<ProjectDependencyTraversalPolicy>))]
public enum ProjectDependencyTraversalPolicy
{
    FullArchive,
    NonStructuralArchive,
    HistorySnapshot,
}

public sealed record ProjectArchiveLimits
{
    public static ProjectArchiveLimits Default { get; } = new();

    public int MaximumEntryCount { get; init; } = 10_000;
    public long MaximumCompressedBytes { get; init; } = 1L * 1024 * 1024 * 1024;
    public long MaximumExpandedBytes { get; init; } = 4L * 1024 * 1024 * 1024;
    public long MaximumEntryBytes { get; init; } = 512L * 1024 * 1024;
    public long MaximumManifestBytes { get; init; } = 4L * 1024 * 1024;

    internal void Validate()
    {
        if (MaximumEntryCount <= 0
            || MaximumCompressedBytes <= 0
            || MaximumExpandedBytes <= 0
            || MaximumEntryBytes <= 0
            || MaximumManifestBytes <= 0
            || MaximumManifestBytes > MaximumEntryBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(ProjectArchiveLimits), "Archive limits must be positive and internally consistent.");
        }
    }
}

public sealed class ProjectArchiveException(string message, Exception? innerException = null)
    : IOException(message, innerException);
