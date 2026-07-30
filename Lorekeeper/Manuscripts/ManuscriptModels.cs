using System.Text.Json.Serialization;

namespace Lorekeeper.Manuscripts;

public sealed record ManuscriptDocument
{
    public const int CurrentSchemaVersion = 2;

    [JsonRequired]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    [JsonRequired]
    public Guid ManuscriptId { get; init; }
    [JsonRequired]
    public long Revision { get; init; }
    [JsonRequired]
    public List<ManuscriptBlock> Content { get; init; } = [];
}

public sealed record ManuscriptBlock
{
    public required string Id { get; init; }
    [JsonRequired]
    public ManuscriptBlockType Type { get; init; } = ManuscriptBlockType.Paragraph;
    [JsonRequired]
    public string StyleRole { get; init; } = ManuscriptStyleRoles.Body;
    [JsonRequired]
    public int? HeadingLevel { get; init; }
    [JsonRequired]
    public Guid? ImageId { get; init; }
    [JsonRequired]
    public string? AltText { get; init; }
    [JsonRequired]
    public List<ManuscriptInline> Content { get; init; } = [];
}

public sealed record ManuscriptInline
{
    [JsonRequired]
    public ManuscriptInlineType Type { get; init; } = ManuscriptInlineType.Text;
    [JsonRequired]
    public string Text { get; init; } = string.Empty;
    [JsonRequired]
    public List<ManuscriptMark> Marks { get; init; } = [];
}

public sealed record ManuscriptMark
{
    public required ManuscriptMarkType Type { get; init; }
    public string? Value { get; init; }
}

public enum ManuscriptBlockType
{
    Paragraph,
    Heading,
    SceneBreak,
    BlockQuote,
    ListItem,
    Figure,
}

public enum ManuscriptInlineType
{
    Text,
}

public enum ManuscriptMarkType
{
    Emphasis,
    Strong,
    Underline,
    Strikethrough,
    Code,
    Link,
    Language,
    SmallCaps,
    Superscript,
    Subscript,
    CharacterStyle,
}

public static class ManuscriptStyleRoles
{
    public const string Body = "body";
    public const string Heading = "heading";
    public const string ChapterHeading = "chapter-heading";
    public const string Subheading = "subheading";
    public const string SceneBreak = "scene-break";
    public const string BlockQuote = "block-quote";
    public const string ListItem = "list-item";
    public const string FigureCaption = "figure-caption";
}

public sealed record ManuscriptSnapshot(
    Guid ChapterId,
    long Revision,
    string SourceHash,
    string PlainText,
    ManuscriptDocument Document);

public sealed record ManuscriptMutationResult(
    ManuscriptSnapshot Snapshot,
    IReadOnlyList<string> ChangedBlockIds);

public sealed record ManuscriptRangeReference(
    string BlockId,
    int? StartOffset = null,
    int? EndOffset = null);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "operation")]
[JsonDerivedType(typeof(InsertManuscriptBlock), "insertBlock")]
[JsonDerivedType(typeof(ReplaceManuscriptBlockText), "replaceBlockText")]
[JsonDerivedType(typeof(DeleteManuscriptBlock), "deleteBlock")]
[JsonDerivedType(typeof(MoveManuscriptBlock), "moveBlock")]
[JsonDerivedType(typeof(SplitManuscriptBlock), "splitBlock")]
[JsonDerivedType(typeof(MergeManuscriptBlocks), "mergeBlocks")]
[JsonDerivedType(typeof(SetManuscriptBlockType), "setBlockType")]
[JsonDerivedType(typeof(SetManuscriptBlockStyle), "setBlockStyle")]
[JsonDerivedType(typeof(SetManuscriptInlineMark), "setInlineMark")]
public abstract record ManuscriptOperation;

public sealed record InsertManuscriptBlock(
    int Index,
    ManuscriptBlockType Type,
    string Text,
    string? StyleRole = null,
    Guid? ImageId = null,
    string? AltText = null,
    int? HeadingLevel = null) : ManuscriptOperation;

public sealed record ReplaceManuscriptBlockText(
    string BlockId,
    string Text) : ManuscriptOperation;

public sealed record DeleteManuscriptBlock(
    string BlockId) : ManuscriptOperation;

public sealed record MoveManuscriptBlock(
    string BlockId,
    int TargetIndex) : ManuscriptOperation;

public sealed record SplitManuscriptBlock(
    string BlockId,
    int Offset) : ManuscriptOperation;

public sealed record MergeManuscriptBlocks(
    string FirstBlockId,
    string SecondBlockId) : ManuscriptOperation;

public sealed record SetManuscriptBlockType(
    string BlockId,
    ManuscriptBlockType Type,
    string? StyleRole = null,
    Guid? ImageId = null,
    string? AltText = null,
    int? HeadingLevel = null) : ManuscriptOperation;

public sealed record SetManuscriptBlockStyle(
    string BlockId,
    string StyleRole) : ManuscriptOperation;

public sealed record SetManuscriptInlineMark(
    string BlockId,
    int StartOffset,
    int EndOffset,
    ManuscriptMarkType Mark,
    bool Enabled,
    string? Value = null) : ManuscriptOperation;

public sealed class ManuscriptRevisionConflictException(long expected, long actual)
    : InvalidOperationException($"Manuscript revision conflict: expected {expected}, current revision is {actual}.")
{
    public long ExpectedRevision { get; } = expected;
    public long ActualRevision { get; } = actual;
}
