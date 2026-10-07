using System.Text.Json.Serialization;
using Lorekeeper.Models;

namespace Lorekeeper.Manuscripts;

public sealed record ManuscriptDocument
{
    public const int CurrentSchemaVersion = 7;

    [JsonRequired]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    [JsonRequired]
    public Guid ManuscriptId { get; init; }
    [JsonRequired]
    public long Revision { get; init; }
    [JsonRequired]
    public List<ManuscriptBlock> Content { get; init; } = [];
    [JsonRequired]
    public List<ManuscriptNote> Notes { get; init; } = [];
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
    public bool Decorative { get; init; }
    public string? Language { get; init; }
    public FigureAccessibilityRole? AccessibilityRole { get; init; }
    public FigurePresentation? FigurePresentation { get; init; }
    public ParagraphPresentation? ParagraphPresentation { get; init; }
    public Guid? DesignedPageId { get; init; }
    public PublicationBoundField? PublicationField { get; init; }
    public ManuscriptTable? Table { get; init; }
    public ManuscriptListItem? List { get; init; }
    [JsonRequired]
    public List<ManuscriptInline> Content { get; init; } = [];
}

public sealed record ManuscriptInline
{
    public string? Id { get; init; }
    [JsonRequired]
    public ManuscriptInlineType Type { get; init; } = ManuscriptInlineType.Text;
    [JsonRequired]
    public string Text { get; init; } = string.Empty;
    public string? NoteId { get; init; }
    public ManuscriptCitationCluster? Citation { get; init; }
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
    DesignedPage,
    Table,
}

public sealed record ManuscriptListItem
{
    public required string Id { get; init; }
    public bool Ordered { get; init; }
    public int Level { get; init; }
    public int? Start { get; init; }
}

public sealed record ManuscriptTable
{
    public required string Id { get; init; }
    [JsonRequired]
    public List<int> ColumnWidthWeights { get; init; } = [];
    [JsonRequired]
    public int HeaderRowCount { get; init; }
    [JsonRequired]
    public List<ManuscriptTableRow> Rows { get; init; } = [];
}

public sealed record ManuscriptTableRow
{
    public required string Id { get; init; }
    [JsonRequired]
    public List<ManuscriptTableCell> Cells { get; init; } = [];
}

public sealed record ManuscriptTableCell
{
    public required string Id { get; init; }
    public int RowSpan { get; init; } = 1;
    public int ColumnSpan { get; init; } = 1;
    [JsonRequired]
    public List<ManuscriptBlock> Content { get; init; } = [];
}

public sealed record ManuscriptNote
{
    public required string Id { get; init; }
    public ManuscriptNoteKind Kind { get; init; } = ManuscriptNoteKind.Footnote;
    [JsonRequired]
    public List<ManuscriptBlock> Content { get; init; } = [];
}

[JsonConverter(typeof(ManuscriptEnumConverter<ManuscriptNoteKind>))]
public enum ManuscriptNoteKind
{
    Footnote,
    Endnote,
}

public sealed record FigurePresentation
{
    public FigurePlacementIntent Placement { get; init; } = FigurePlacementIntent.Centered;
    public double WidthPercent { get; init; } = 100;
    public FigureAlignment Alignment { get; init; } = FigureAlignment.Center;
    public FigureTextWrap TextWrap { get; init; } = FigureTextWrap.None;
    public FigureImageFit Fit { get; init; } = FigureImageFit.Contain;
    public double CropXPercent { get; init; } = 50;
    public double CropYPercent { get; init; } = 50;
    public double SpacingBeforePoints { get; init; } = 6;
    public double SpacingAfterPoints { get; init; } = 6;
    public bool StartOnNewPage { get; init; }
    public bool KeepWithCaption { get; init; } = true;
    public FigureCaptionPlacement CaptionPlacement { get; init; } = FigureCaptionPlacement.Below;
}

public sealed record ParagraphPresentation
{
    public string? FontFamilyKey { get; init; }
    public double? FontSizePoints { get; init; }
    public int? FontWeight { get; init; }
    public bool? Italic { get; init; }
    public bool? SmallCaps { get; init; }
    public double? LineHeight { get; init; }
    public ParagraphAlignment? Alignment { get; init; }
    public double? LeftIndentEm { get; init; }
    public double? RightIndentEm { get; init; }
    public double? FirstLineIndentEm { get; init; }
    public double? SpacingBeforePoints { get; init; }
    public double? SpacingAfterPoints { get; init; }
    public bool? KeepWithNext { get; init; }
    public bool? StartOnNewPage { get; init; }
}

[JsonConverter(typeof(ManuscriptEnumConverter<ParagraphAlignment>))]
public enum ParagraphAlignment
{
    Start,
    Center,
    End,
    Justify,
}

[JsonConverter(typeof(ManuscriptEnumConverter<FigureAccessibilityRole>))]
public enum FigureAccessibilityRole
{
    Figure,
    Illustration,
    Diagram,
    Map,
    Photograph,
    Ornament,
}

[JsonConverter(typeof(ManuscriptEnumConverter<FigurePlacementIntent>))]
public enum FigurePlacementIntent
{
    Inline,
    Centered,
    Float,
    FullWidth,
    FullBleed,
    DedicatedPage,
}

[JsonConverter(typeof(ManuscriptEnumConverter<FigureAlignment>))]
public enum FigureAlignment
{
    Start,
    Center,
    End,
}

[JsonConverter(typeof(ManuscriptEnumConverter<FigureTextWrap>))]
public enum FigureTextWrap
{
    None,
    Start,
    End,
}

[JsonConverter(typeof(ManuscriptEnumConverter<FigureImageFit>))]
public enum FigureImageFit
{
    Contain,
    Cover,
    Stretch,
}

[JsonConverter(typeof(ManuscriptEnumConverter<FigureCaptionPlacement>))]
public enum FigureCaptionPlacement
{
    Below,
    Above,
    Overlay,
    Hidden,
}

public enum ManuscriptInlineType
{
    Text,
    NoteReference,
    Citation,
}

public sealed record ManuscriptCitationCluster
{
    [JsonRequired]
    public List<ManuscriptCitationItem> Items { get; init; } = [];
}

public sealed record ManuscriptCitationItem
{
    [JsonRequired]
    public Guid BibliographicRecordId { get; init; }
    public string Prefix { get; init; } = string.Empty;
    public string Suffix { get; init; } = string.Empty;
    public string LocatorLabel { get; init; } = string.Empty;
    public string LocatorValue { get; init; } = string.Empty;
    public Guid? SourceLocationId { get; init; }
}

[JsonConverter(typeof(ManuscriptEnumConverter<ManuscriptPositionAffinity>))]
public enum ManuscriptPositionAffinity
{
    Before,
    After,
}

public sealed record ManuscriptPosition(
    Guid DocumentId,
    IReadOnlyList<string> ContainerPath,
    string BlockOrAtomId,
    int Offset,
    ManuscriptPositionAffinity Affinity);

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
    public const string DesignedPage = "designed-page";
    public const string Table = "table";
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
[JsonDerivedType(typeof(SetFigurePresentation), "setFigurePresentation")]
[JsonDerivedType(typeof(SetParagraphPresentation), "setParagraphPresentation")]
[JsonDerivedType(typeof(PutRichManuscriptBlock), "putRichBlock")]
[JsonDerivedType(typeof(ReplaceManuscriptNotes), "replaceNotes")]
[JsonDerivedType(typeof(ReplaceManuscriptStructure), "replaceStructure")]
[JsonDerivedType(typeof(ReplaceManuscriptInlineContent), "replaceInlineContent")]
public abstract record ManuscriptOperation;

public sealed record ReplaceManuscriptInlineContent(
    ManuscriptPosition Position,
    IReadOnlyList<ManuscriptInline> Content) : ManuscriptOperation;

public sealed record PutRichManuscriptBlock(
    int Index,
    ManuscriptBlock Block) : ManuscriptOperation;

public sealed record ReplaceManuscriptNotes(
    IReadOnlyList<ManuscriptNote> Notes) : ManuscriptOperation;

public sealed record ReplaceManuscriptStructure(
    IReadOnlyList<ManuscriptBlock> Content,
    IReadOnlyList<ManuscriptNote> Notes) : ManuscriptOperation;

public sealed record InsertManuscriptBlock(
    int Index,
    ManuscriptBlockType Type,
    string Text,
    string? StyleRole = null,
    Guid? ImageId = null,
    string? AltText = null,
    int? HeadingLevel = null,
    bool Decorative = false,
    FigurePresentation? FigurePresentation = null,
    Guid? DesignedPageId = null,
    string? Language = null,
    FigureAccessibilityRole AccessibilityRole = FigureAccessibilityRole.Figure,
    string? BlockId = null) : ManuscriptOperation;

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
    int? HeadingLevel = null,
    bool Decorative = false,
    FigurePresentation? FigurePresentation = null,
    Guid? DesignedPageId = null,
    string? Language = null,
    FigureAccessibilityRole AccessibilityRole = FigureAccessibilityRole.Figure) : ManuscriptOperation;

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

public sealed record SetFigurePresentation(
    string BlockId,
    Guid ImageId,
    string? AltText,
    bool Decorative,
    string? Language,
    FigurePresentation Presentation,
    FigureAccessibilityRole AccessibilityRole = FigureAccessibilityRole.Figure) : ManuscriptOperation;

public sealed record SetParagraphPresentation(
    string BlockId,
    ParagraphPresentation? Presentation) : ManuscriptOperation;

public sealed class ManuscriptRevisionConflictException(long expected, long actual)
    : InvalidOperationException($"Manuscript revision conflict: expected {expected}, current revision is {actual}.")
{
    public long ExpectedRevision { get; } = expected;
    public long ActualRevision { get; } = actual;
}
