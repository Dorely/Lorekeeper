using System.Text.Json.Serialization;

namespace Lorekeeper.ImportExport;

[JsonConverter(typeof(JsonStringEnumConverter<ProjectExportKind>))]
public enum ProjectExportKind
{
    Full,
    NonStructural,
}

[JsonConverter(typeof(JsonStringEnumConverter<ManuscriptExportFormat>))]
public enum ManuscriptExportFormat
{
    PlainText,
    Markdown,
    Epub,
}

public sealed record ProjectExportFile(
    string FileName,
    string ContentType,
    byte[] Content);

public sealed record ProjectExportDocument
{
    public const string CurrentFormatId = "lorekeeper.project-export";
    public const int CurrentFormatVersion = 1;

    public string FormatId { get; init; } = CurrentFormatId;
    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public ProjectExportKind ExportKind { get; init; }
    public DateTime ExportedAtUtc { get; init; } = DateTime.UtcNow;
    public required ProjectExportProject Project { get; init; }
    public List<ProjectExportEntityType> EntityTypes { get; init; } = [];
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

public sealed record ProjectExportChapter(
    Guid Id,
    Guid? ActId,
    string Title,
    string Body,
    string Synopsis,
    int Order);

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
