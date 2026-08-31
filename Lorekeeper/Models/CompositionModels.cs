using System.Text.Json.Serialization;
using Lorekeeper.Manuscripts;

namespace Lorekeeper.Models;

public class PageComposition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public Guid? ChapterId { get; set; }
    public Chapter? Chapter { get; set; }
    public Guid? PublicationSectionId { get; set; }
    public PublicationSection? PublicationSection { get; set; }
    public Guid? EditionId { get; set; }
    public PublicationEdition? Edition { get; set; }
    public Guid? SourceCompositionId { get; set; }
    public string Name { get; set; } = "Designed page";
    public string SemanticManuscriptJson { get; set; } = string.Empty;
    public long Revision { get; set; }
    public Guid? ActiveAuthoringVariantId { get; set; }
    public ICollection<PageCompositionVariant> Variants { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DetachedAt { get; set; }
}

public class PageCompositionVariant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompositionId { get; set; }
    public PageComposition Composition { get; set; } = null!;
    public string GeometryKey { get; set; } = string.Empty;
    public string SceneJson { get; set; } = string.Empty;
    public long Revision { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DetachedAt { get; set; }
}

public class CompositionMutationStage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public Guid ConversationId { get; set; }
    public string TargetKind { get; set; } = string.Empty;
    public Guid TargetId { get; set; }
    public string ContentTargetKind { get; set; } = "Core";
    public Guid? ContentTargetEditionId { get; set; }
    public long ExpectedRevision { get; set; }
    public string OperationsJson { get; set; } = "[]";
    public string PayloadSha256 { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime? AppliedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed record CompositionScene
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public CompositionSurface Surface { get; init; } = new();
    public IReadOnlyList<CompositionLayer> Layers { get; init; } = [];
    public IReadOnlyList<CompositionObjectStyle> Styles { get; init; } = [];
    public IReadOnlyList<CompositionGuide> Guides { get; init; } = [];
    public IReadOnlyList<CompositionObject> Objects { get; init; } = [];
}

public sealed record DesignedPageInitialContent
{
    public DesignedPageLayoutMode LayoutMode { get; init; } = DesignedPageLayoutMode.SinglePage;
    public Guid? ImageId { get; init; }
    public string AltText { get; init; } = string.Empty;
    public bool Decorative { get; init; }
    public FigureImageFit ImageFit { get; init; } = FigureImageFit.Contain;
    public double CropXPercent { get; init; } = 50;
    public double CropYPercent { get; init; } = 50;
}

public sealed record CompositionGuide(Guid Id, CompositionGuideAxis Axis, double PositionPercent);

public sealed record CompositionSurface
{
    public CompositionSurfaceKind Kind { get; init; } = CompositionSurfaceKind.SinglePage;
    public CompositionOutputPageMode OutputPageMode { get; init; } = CompositionOutputPageMode.EditionLeaves;
    public double WidthPoints { get; init; } = 432;
    public double HeightPoints { get; init; } = 648;
    public double BleedPoints { get; init; }
    public double SafeInsetPoints { get; init; } = 36;
    public bool AllowIndependentPdfPage { get; init; }
    public double TrimWidthPoints { get; init; }
    public double TrimHeightPoints { get; init; }
    public double SpineWidthPoints { get; init; }
    public double BackRegionWidthPoints { get; init; }
    public double FrontRegionWidthPoints { get; init; }
    public double CoverRegionYPoints { get; init; }
    public double CoverRegionHeightPoints { get; init; }
}

public sealed record CompositionLayer(
    Guid Id,
    string Name,
    int Order,
    bool Visible = true,
    bool Locked = false);

public sealed record CompositionObjectStyle
{
    public required Guid Id { get; init; }
    public string Name { get; init; } = "Object style";
    public string FontFamilyKey { get; init; } = "builtin:nunito";
    public int FontWeight { get; init; } = 400;
    public bool Italic { get; init; }
    public double FontSizePoints { get; init; } = 12;
    public double LineHeight { get; init; } = 1.2;
    public double LetterSpacingEm { get; init; }
    public string FillColor { get; init; } = "#000000";
    public string BackgroundColor { get; init; } = "transparent";
    public double BackgroundOpacity { get; init; }
    public string StrokeColor { get; init; } = "transparent";
    public double StrokeWidthPoints { get; init; }
    public CompositionTextAlignment TextAlignment { get; init; } = CompositionTextAlignment.Start;
    public CompositionVerticalAlignment VerticalAlignment { get; init; } = CompositionVerticalAlignment.Top;
    public CompositionTextShadow TextShadow { get; init; }
}

public sealed record CompositionObject
{
    public required Guid Id { get; init; }
    public required Guid LayerId { get; init; }
    public required CompositionObjectKind Kind { get; init; }
    public CompositionBounds Bounds { get; init; } = new();
    public double RotationDegrees { get; init; }
    public double Opacity { get; init; } = 1;
    public int ZIndex { get; init; }
    public bool Visible { get; init; } = true;
    public bool Locked { get; init; }
    public string Name { get; init; } = string.Empty;
    public Guid? StyleId { get; init; }
    public Guid? ImageId { get; init; }
    public FigureImageFit ImageFit { get; init; } = FigureImageFit.Contain;
    public double CropXPercent { get; init; } = 50;
    public double CropYPercent { get; init; } = 50;
    public string TextBinding { get; init; } = string.Empty;
    public IReadOnlyList<ManuscriptRangeReference> ContentReferences { get; init; } = [];
    public string FontFamilyKey { get; init; } = "builtin:nunito";
    public int FontWeight { get; init; } = 400;
    public bool Italic { get; init; }
    public double FontSizePoints { get; init; } = 12;
    public double LineHeight { get; init; } = 1.2;
    public double LetterSpacingEm { get; init; }
    public string FillColor { get; init; } = "#000000";
    public string BackgroundColor { get; init; } = "transparent";
    public double BackgroundOpacity { get; init; }
    public string StrokeColor { get; init; } = "transparent";
    public double StrokeWidthPoints { get; init; }
    public CompositionTextAlignment TextAlignment { get; init; } = CompositionTextAlignment.Start;
    public CompositionVerticalAlignment VerticalAlignment { get; init; } = CompositionVerticalAlignment.Top;
    public CompositionTextShadow TextShadow { get; init; }
    public string AltText { get; init; } = string.Empty;
    public bool Decorative { get; init; }
    public bool AccessibilityDecisionPending { get; init; }
    public string Language { get; init; } = string.Empty;
    public CompositionSemanticRole SemanticRole { get; init; } = CompositionSemanticRole.Artifact;
    public int? ReadingOrder { get; init; }
    public CompositionRegionConstraint RegionConstraint { get; init; } = CompositionRegionConstraint.Page;
    public Guid? GroupId { get; init; }
}

public sealed record CompositionBounds
{
    public double XPercent { get; init; }
    public double YPercent { get; init; }
    public double WidthPercent { get; init; } = 100;
    public double HeightPercent { get; init; } = 100;
}

[JsonConverter(typeof(JsonStringEnumConverter<CompositionSurfaceKind>))]
public enum CompositionSurfaceKind { SinglePage, FacingSpread, IndependentPage }
[JsonConverter(typeof(JsonStringEnumConverter<DesignedPageLayoutMode>))]
public enum DesignedPageLayoutMode { SinglePage, FacingSpread }
[JsonConverter(typeof(JsonStringEnumConverter<CompositionOutputPageMode>))]
public enum CompositionOutputPageMode { EditionLeaves, SingleSurface }
[JsonConverter(typeof(JsonStringEnumConverter<CompositionObjectKind>))]
public enum CompositionObjectKind { Image, Text, Rectangle, Ellipse, Line, Group }
[JsonConverter(typeof(JsonStringEnumConverter<CompositionSemanticRole>))]
public enum CompositionSemanticRole { Artifact, Paragraph, Heading1, Heading2, Heading3, Figure, Caption, Credit }
[JsonConverter(typeof(JsonStringEnumConverter<CompositionTextAlignment>))]
public enum CompositionTextAlignment { Start, Center, End, Justify }
[JsonConverter(typeof(JsonStringEnumConverter<CompositionVerticalAlignment>))]
public enum CompositionVerticalAlignment { Top, Center, Bottom }
[JsonConverter(typeof(JsonStringEnumConverter<CompositionTextShadow>))]
public enum CompositionTextShadow { None, Soft, Strong, Glow }
[JsonConverter(typeof(JsonStringEnumConverter<CompositionGuideAxis>))]
public enum CompositionGuideAxis { Horizontal, Vertical }
[JsonConverter(typeof(JsonStringEnumConverter<CompositionRegionConstraint>))]
public enum CompositionRegionConstraint { Page, SafeArea, Front, Spine, Back, Gutter, BarcodeReserve }

public sealed record LayoutGenerationRegionDescriptor(
    string Kind,
    string Label,
    CompositionBounds Bounds,
    bool KeepClear);

public sealed record CompositionEditionGeometry(
    double LeafWidthPoints,
    double LeafHeightPoints,
    bool AllowIndependentPdfPage);

public sealed record LayoutGenerationTargetDescriptor(
    Guid? EditionId,
    Guid? VariantId,
    string GeometryKey,
    string GeometrySource,
    string TargetKind,
    Guid TargetId,
    double WidthInches,
    double HeightInches,
    string AspectRatio,
    int RecommendedWidthPixels,
    int RecommendedHeightPixels,
    int RequestedWidthPixels,
    int RequestedHeightPixels,
    string RequestedRaster,
    double EffectiveDpiExpectation,
    IReadOnlyList<LayoutGenerationRegionDescriptor> Regions,
    IReadOnlyList<string> Diagnostics,
    CompositionBounds? SurfaceBounds = null,
    double? RequestedMinimumDpi = null);

public sealed record LayoutValidationDiagnostic(string Severity, string Code, string Message, Guid? ObjectId = null);

public sealed record LayoutValidationView(
    Guid TargetId,
    long Revision,
    int ErrorCount,
    int WarningCount,
    IReadOnlyList<LayoutValidationDiagnostic> Diagnostics);
