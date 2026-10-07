using System.Text.Json.Serialization;
using Lorekeeper.ImportExport;
using Lorekeeper.Models;

namespace Lorekeeper.VersionHistory.Snapshots;

public static class VersionHistorySnapshotContract
{
    public const string FormatId = "lorekeeper.version-history-snapshot";
    public const int MinimumReadableSchemaVersion = 1;
    public const int SchemaVersion = 12;
    public const int ImageUpscaleSchemaVersion = 5;
    public const int CoverDescriptionSchemaVersion = 6;
    public const int DesignedPagesSchemaVersion = 7;
    public const int RichManuscriptSchemaVersion = 9;
    public const int CitationSchemaVersion = 10;
    public const string ManifestFileName = "manifest.json";

    public static readonly IReadOnlyList<string> IncludedAreas =
    [
        "project",
        "narrative",
        "graph",
        "sources",
        "assets",
        "manuscript",
        "composition",
        "publication",
    ];

    public static bool CanReadSchema(int schemaVersion) =>
        schemaVersion is >= MinimumReadableSchemaVersion and <= SchemaVersion;
}

public sealed record VersionHistorySnapshotFile(
    string Path,
    long Length,
    string Sha256);

public sealed record VersionHistorySnapshotManifest(
    string FormatId,
    int SchemaVersion,
    Guid RepositoryId,
    Guid ProjectId,
    IReadOnlyList<string> IncludedAreas,
    string ContentHash,
    string ManifestHash,
    IReadOnlyList<VersionHistorySnapshotFile> Files);

public sealed record VersionHistorySnapshotArtifact(
    string RootDirectory,
    VersionHistorySnapshotManifest Manifest,
    VersionHistorySnapshotPayload Payload);

public sealed record VersionHistorySnapshotProjectArea(
    ProjectExportProject Project,
    ProjectExportPageSetup? PageSetup,
    bool ContestModeEnabled,
    IReadOnlyList<VersionHistoryProjectReference> References);

public sealed record VersionHistorySnapshotNarrativeArea(
    ProjectExportBookBrief? BookBrief,
    IReadOnlyList<Guid> BookBriefCanonSourceIds,
    IReadOnlyList<ProjectExportEntityType> EntityTypes,
    IReadOnlyList<ProjectExportAct> Acts,
    IReadOnlyList<ProjectExportChapter> Chapters,
    IReadOnlyList<VersionHistoryWritingSample> WritingSamples,
    IReadOnlyList<VersionHistoryContextPreference> ContextPreferences,
    IReadOnlyList<ProjectExportManuscriptAnnotation> Annotations,
    string WorldBrief = "");

/// <summary>
/// The non-chapter portion of <c>narrative/narrative.json</c>. Chapters are
/// intentionally stored in stable ID folders so a chapter edit does not move
/// any other chapter's path.
/// </summary>
public sealed record VersionHistorySnapshotNarrativeFile(
    ProjectExportBookBrief? BookBrief,
    IReadOnlyList<Guid> BookBriefCanonSourceIds,
    IReadOnlyList<ProjectExportEntityType> EntityTypes,
    IReadOnlyList<ProjectExportAct> Acts,
    IReadOnlyList<VersionHistoryWritingSample> WritingSamples,
    IReadOnlyList<VersionHistoryContextPreference> ContextPreferences,
    IReadOnlyList<ProjectExportManuscriptAnnotation> Annotations,
    string WorldBrief = "")
{
    public static VersionHistorySnapshotNarrativeFile FromArea(VersionHistorySnapshotNarrativeArea area) => new(
        area.BookBrief,
        area.BookBriefCanonSourceIds,
        area.EntityTypes,
        area.Acts,
        area.WritingSamples,
        area.ContextPreferences,
        area.Annotations,
        area.WorldBrief);

    public VersionHistorySnapshotNarrativeArea ToArea(IReadOnlyList<ProjectExportChapter> chapters) => new(
        BookBrief,
        BookBriefCanonSourceIds,
        EntityTypes,
        Acts,
        chapters,
        WritingSamples,
        ContextPreferences,
        Annotations,
        WorldBrief);
}

// Explicit predecessor boundary: preserve canonical bytes of schema 1-10 narrative files.
public sealed record VersionHistorySnapshotNarrativeFileV10(
    ProjectExportBookBrief? BookBrief,
    IReadOnlyList<Guid> BookBriefCanonSourceIds,
    IReadOnlyList<ProjectExportEntityType> EntityTypes,
    IReadOnlyList<ProjectExportAct> Acts,
    IReadOnlyList<VersionHistoryWritingSample> WritingSamples,
    IReadOnlyList<VersionHistoryContextPreference> ContextPreferences,
    IReadOnlyList<ProjectExportManuscriptAnnotation> Annotations)
{
    public VersionHistorySnapshotNarrativeFile ToCurrent() => new(
        BookBrief, BookBriefCanonSourceIds, EntityTypes, Acts, WritingSamples, ContextPreferences, Annotations, "");
}

/// <summary>
/// All chapter metadata except the manuscript body. The body is a direct JSON
/// file beside this metadata and is reconstructed into the in-memory export
/// DTO when the snapshot is read.
/// </summary>
public sealed record VersionHistorySnapshotChapter(
    Guid Id,
    Guid? ActId,
    string Title,
    long ManuscriptRevision,
    string? Body,
    string Synopsis,
    int Order,
    ChapterVisualMode VisualMode,
    ChapterPageLayoutKind PageLayoutKind,
    string PageLayoutJson,
    string IllustrationLayoutJson,
    IReadOnlyList<Guid> ExplicitImageContextImageIds)
{
    public static VersionHistorySnapshotChapter FromProjectExportChapter(ProjectExportChapter chapter) => new(
        chapter.Id,
        chapter.ActId,
        chapter.Title,
        chapter.ManuscriptRevision,
        chapter.Body,
        chapter.Synopsis,
        chapter.Order,
        chapter.VisualMode,
        chapter.PageLayoutKind,
        chapter.PageLayoutJson,
        chapter.IllustrationLayoutJson,
        chapter.ExplicitImageContextImageIds);

    public ProjectExportChapter ToProjectExportChapter(string manuscriptJson) => new()
    {
        Id = Id,
        ActId = ActId,
        Title = Title,
        ManuscriptJson = manuscriptJson,
        ManuscriptRevision = ManuscriptRevision,
        Body = Body,
        Synopsis = Synopsis,
        Order = Order,
        VisualMode = VisualMode,
        PageLayoutKind = PageLayoutKind,
        PageLayoutJson = PageLayoutJson,
        IllustrationLayoutJson = IllustrationLayoutJson,
        ExplicitImageContextImageIds = ExplicitImageContextImageIds.ToList(),
    };
}

public sealed record VersionHistorySnapshotGraphArea(
    IReadOnlyList<ProjectExportNode> Nodes,
    IReadOnlyList<ProjectExportEdge> Edges);

public sealed record VersionHistorySnapshotSourcesArea(
    IReadOnlyList<VersionHistoryRetainedSource> RetainedSources)
{
    public IReadOnlyList<VersionHistoryBibliographicRecord> UnlinkedBibliographicRecords { get; init; } = [];

    /// <summary>Bounded review-only projections; a payload with these cannot be restored.</summary>
    public IReadOnlyList<VersionHistorySourceReviewSummary>? ReviewSummaries { get; init; }
}

public sealed record VersionHistorySourceReviewSummary(Guid Id, string Title, string Hash, string ReadableText)
{
    public bool ReadableTextComplete { get; init; }

    internal static VersionHistorySourceReviewSummary Create(VersionHistoryRetainedSource source, bool unbounded = false)
    {
        var maximum = unbounded ? int.MaxValue : 4097; // One extra character lets the comparer report truncation.
        var text = source.Extractions.SingleOrDefault(extraction => extraction.Id == source.ActiveExtractionVersionId)?.NormalizedText ?? string.Empty;
        var synopsis = string.IsNullOrWhiteSpace(source.Synopsis) ? string.Empty : source.Synopsis[..Math.Min(source.Synopsis.Length, maximum)] + "\n\n";
        var readable = synopsis.Length >= maximum ? synopsis[..maximum] : synopsis + text[..Math.Min(text.Length, maximum - synopsis.Length)];
        return new(source.Id, source.Title,
            VersionHistoryCanonicalJson.Sha256Hex(VersionHistoryCanonicalJson.Serialize(source)), readable)
        {
            ReadableTextComplete = string.IsNullOrWhiteSpace(source.Synopsis)
                ? text.Length <= maximum
                : (long)source.Synopsis.Length + 2 + text.Length <= maximum,
        };
    }
}

public sealed record VersionHistorySnapshotAssetsArea(
    IReadOnlyList<VersionHistoryImageAsset> Images,
    IReadOnlyList<ProjectExportEntityVisualExample> EntityVisualExamples,
    IReadOnlyList<VersionHistoryFontFamily> FontFamilies);

public sealed record VersionHistorySnapshotManuscriptArea(
    IReadOnlyList<ProjectExportManuscriptStyle> Styles);

public sealed record VersionHistorySnapshotCompositionArea(
    IReadOnlyList<ProjectExportDesignedPage> DesignedPages)
{
    /// <summary>
    /// Read-only schema-v1-v6 input. Schema v7 writers retain only project
    /// owned Designed Pages; placement remains in manuscript block identity.
    /// </summary>
    [JsonPropertyName("pageCompositions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ProjectExportPageComposition>? LegacyPageCompositions { get; init; }

    /// <summary>
    /// Isolated v1-v6 compatibility accessor for the old review/restore
    /// adapter. New snapshot writers and all v7 full restores use
    /// <see cref="DesignedPages"/> directly.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<ProjectExportPageComposition> PageCompositions => LegacyPageCompositions ?? [];
}

public sealed record VersionHistorySnapshotPublicationArea(
    ProjectExportPublicationBook? PublicationBook,
    IReadOnlyList<ProjectExportPublicationEdition> PublicationEditions,
    IReadOnlyList<ProjectExportPublicationSection> PublicationSections);

public sealed record VersionHistoryProjectReference(
    Guid ReferenceId,
    Guid ReferencingProjectId,
    Guid ReferencedProjectId,
    Guid? ReferencedRepositoryId,
    string ReferencedProjectName,
    string ReferencedProjectSlug);

public sealed record VersionHistoryWritingSample(
    Guid Id,
    string Title,
    string Body);

public sealed record VersionHistoryContextPreference(
    Guid Id,
    Guid ChapterId,
    string Kind,
    string Key,
    bool IsIncluded,
    int? SortOrder);

public sealed record VersionHistoryImageAsset(
    Guid Id,
    string FileName,
    string ContentType,
    string BlobPath,
    string Sha256,
    long ByteLength,
    string AltText,
    PublishAssetSource Source,
    string Prompt,
    string GenerationModel,
    string SourceMetadataJson,
    Guid? DerivedFromImageId,
    double? CropXPercent,
    double? CropYPercent,
    double? CropWidthPercent,
    double? CropHeightPercent);

public sealed record VersionHistoryFontFamily(
    Guid Id,
    string Name,
    bool EmbeddingRightsConfirmed,
    string RightsDeclaration,
    IReadOnlyList<VersionHistoryFontFace> Faces);

public sealed record VersionHistoryFontFace(
    Guid Id,
    string SubfamilyName,
    string FileName,
    string ContentType,
    int Weight,
    bool Italic,
    string BlobPath,
    string Sha256,
    long ByteLength);

public sealed record VersionHistorySnapshotPayload(
    Guid RepositoryId,
    Guid ProjectId,
    VersionHistorySnapshotProjectArea Project,
    VersionHistorySnapshotNarrativeArea Narrative,
    VersionHistorySnapshotGraphArea Graph,
    VersionHistorySnapshotSourcesArea Sources,
    VersionHistorySnapshotAssetsArea Assets,
    VersionHistorySnapshotManuscriptArea Manuscript,
    VersionHistorySnapshotCompositionArea Composition,
    VersionHistorySnapshotPublicationArea Publication)
{
    public IReadOnlyDictionary<Guid, byte[]> ImageData { get; init; } = new Dictionary<Guid, byte[]>();

    public IReadOnlyDictionary<Guid, byte[]> FontFaceData { get; init; } = new Dictionary<Guid, byte[]>();

    /// <summary>Validated original-blob descriptors, populated only for restore/import reads.</summary>
    public IReadOnlyDictionary<string, VersionHistorySourceBlobDescriptor> SourceOriginalBlobs { get; init; }
        = new Dictionary<string, VersionHistorySourceBlobDescriptor>(StringComparer.Ordinal);
}

/// <summary>
/// A validated, bounded source chunk held on stable local storage. The owner of
/// the containing snapshot keeps that storage alive for the complete restore.
/// </summary>
public sealed class VersionHistorySourceBlobDescriptor
{
    internal VersionHistorySourceBlobDescriptor(string sha256, long length, string localPath)
    {
        Sha256 = sha256;
        Length = length;
        LocalPath = Path.GetFullPath(localPath);
    }

    public string Sha256 { get; }

    public long Length { get; }

    internal string LocalPath { get; }

    public Stream OpenRead()
    {
        var file = new FileInfo(LocalPath);
        if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length != Length)
            throw new InvalidDataException($"Source blob '{Sha256}' changed after snapshot validation.");

        return new FileStream(
            LocalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }
}
