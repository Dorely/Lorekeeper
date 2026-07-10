using System.Text.Json.Serialization;
using Lorekeeper.Models;

namespace Lorekeeper.ImportExport;

[JsonConverter(typeof(JsonStringEnumConverter<ProjectExportKind>))]
public enum ProjectExportKind
{
    Full,
    NonStructural,
}

public sealed record ProjectExportFile(
    string FileName,
    string ContentType,
    byte[] Content);

public sealed record ProjectExportDocument
{
    public const string CurrentFormatId = "lorekeeper.project-export";
    public const int CurrentFormatVersion = 6;

    public string FormatId { get; init; } = CurrentFormatId;
    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public ProjectExportKind ExportKind { get; init; }
    public DateTime ExportedAtUtc { get; init; } = DateTime.UtcNow;
    public required ProjectExportProject Project { get; init; }
    public List<ProjectExportEntityType> EntityTypes { get; init; } = [];
    public List<ProjectExportImage> Images { get; init; } = [];
    public List<ProjectExportEntityVisualExample> EntityVisualExamples { get; init; } = [];
    public List<ProjectExportPublishProfile> PublishProfiles { get; init; } = [];
    public List<ProjectExportAct> Acts { get; init; } = [];
    public List<ProjectExportChapter> Chapters { get; init; } = [];
    public List<ProjectExportNode> Nodes { get; init; } = [];
    public List<ProjectExportEdge> Edges { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public sealed record ProjectExportProject(
    Guid Id,
    string Name,
    string Slug,
    string SystemPrompt,
    bool IncludeCurrentChapterInContext,
    bool AiChangeApprovalEnabled);

public sealed record ProjectExportEntityType(
    string Type,
    string SingularLabel,
    string PluralLabel,
    string? Color,
    string? Icon,
    bool IsStructural,
    bool IsChapterScoped,
    int SortOrder,
    Dictionary<string, object?> DefaultProperties);

public sealed record ProjectExportAct(
    Guid Id,
    string Title,
    string Synopsis,
    int Order);

public sealed record ProjectExportImage(
    Guid Id,
    string FileName,
    string ContentType,
    byte[] Data,
    string AltText,
    PublishAssetSource Source,
    string Prompt,
    string GenerationModel,
    string SourceMetadataJson,
    Guid? DerivedFromImageId,
    double? CropXPercent,
    double? CropYPercent,
    double? CropWidthPercent,
    double? CropHeightPercent,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record ProjectExportEntityVisualExample(
    ProjectExportNodeRef Entity,
    Guid ImageId,
    string Label,
    int SortOrder,
    EntityVisualExampleOrigin Origin,
    string SourceUrl,
    string SourceLocator);

public sealed record ProjectExportPublishProfile(
    Guid Id,
    string TitleOverride,
    string Subtitle,
    string Author,
    string Language,
    string Publisher,
    string Copyright,
    string Isbn,
    string Description,
    string Dedication,
    string Acknowledgments,
    string References,
    bool IncludeTableOfContents,
    bool IncludeVisibleTableOfContents,
    bool IncludeActSynopses,
    bool IncludeChapterSynopses,
    bool IncludeActHeadings,
    bool IncludeChapterHeadings,
    bool NumberActs,
    bool NumberChapters,
    PublishTitlePageMode TitlePageMode,
    PrintPicturePageSpreadMode PrintPicturePageSpreadMode,
    EpubPicturePageSpreadMode EpubPicturePageSpreadMode,
    double PageWidthInches,
    double PageHeightInches,
    double PageMarginInches,
    double BodyFontSizePoints,
    double BodyLineHeight,
    Guid? SelectedCoverChapterId);

public sealed record ProjectExportChapter
{
    public Guid Id { get; init; }
    public Guid? ActId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public string Synopsis { get; init; } = string.Empty;
    public int Order { get; init; }
    public ChapterVisualMode VisualMode { get; init; } = ChapterVisualMode.Prose;
    public ChapterPageLayoutKind PageLayoutKind { get; init; } = ChapterPageLayoutKind.SinglePortrait;
    public string PageLayoutJson { get; init; } = string.Empty;
    public string IllustrationLayoutJson { get; init; } = string.Empty;
    public List<Guid> ExplicitImageContextImageIds { get; init; } = [];
}

public sealed record ProjectExportNode(
    string NodeType,
    string Key,
    string? Label,
    Dictionary<string, object?> Properties,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record ProjectExportEdge(
    ProjectExportNodeRef From,
    ProjectExportNodeRef To,
    string EdgeType,
    Dictionary<string, object?> Properties,
    int? SortOrder,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record ProjectExportNodeRef(string NodeType, string Key)
{
    public string StableKey => $"{NodeType}/{Key}";
}
