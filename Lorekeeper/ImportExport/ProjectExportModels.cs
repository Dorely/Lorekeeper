using System.Text.Json;
using System.Text.Json.Serialization;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;

namespace Lorekeeper.ImportExport;

// These values exist only at the versioned import boundary for v19 and older files.
[JsonConverter(typeof(JsonStringEnumConverter<LegacyPublicationBinding>))]
public enum LegacyPublicationBinding { PerfectBound, Digital }
[JsonConverter(typeof(JsonStringEnumConverter<LegacyPublicationPaper>))]
public enum LegacyPublicationPaper { White, Cream, Digital }
[JsonConverter(typeof(JsonStringEnumConverter<LegacyPublicationInk>))]
public enum LegacyPublicationInk { BlackAndWhite, Color, Digital }

[JsonConverter(typeof(JsonStringEnumConverter<ProjectExportKind>))]
public enum ProjectExportKind
{
    Full,
    NonStructural,
}

public sealed record ProjectExportFile(
    string FileName,
    string ContentType,
    byte[] Content,
    IReadOnlyList<string>? Warnings = null);

/// <summary>
/// No-tracking, fully materialized creative-state capture used by the portable
/// archive traversal. It intentionally contains no write lease or defaults
/// creation behavior.
/// </summary>
public sealed record ProjectArchiveDocumentCapture(
    ProjectExportDocument Document,
    string ProjectSlug);

public static class ProjectExportWarningText
{
    public static string OutgoingReferencesOmitted(int referenceCount, string referencedProjectNames) =>
        $"This format v26 export omits {referenceCount} direct project reference link(s) ({referencedProjectNames}). Imports never infer project links; recreate them manually after import.";
}

public sealed record ProjectExportDocument
{
    public const string CurrentFormatId = "lorekeeper.project-export";
    // v31 is the final JSON writer. Newer portable archives use their own
    // envelope; this value remains the legacy-import ceiling.
    public const int CurrentFormatVersion = 31;

    public string FormatId { get; init; } = CurrentFormatId;
    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public ProjectExportKind ExportKind { get; init; }
    public DateTime ExportedAtUtc { get; init; } = DateTime.UtcNow;
    public required ProjectExportProject Project { get; init; }
    public ProjectExportPageSetup? PageSetup { get; init; }
    public ProjectExportBookBrief? BookBrief { get; init; }
    public List<ProjectExportIngestSource> IngestSources { get; init; } = [];
    public List<Guid> BookBriefCanonSourceIds { get; init; } = [];
    public List<ProjectExportEntityType> EntityTypes { get; init; } = [];
    public List<ProjectExportImage> Images { get; init; } = [];
    public List<ProjectExportEntityVisualExample> EntityVisualExamples { get; init; } = [];
    public ProjectExportPublicationBook? PublicationBook { get; init; }
    public List<ProjectExportPublicationEdition> PublicationEditions { get; init; } = [];
    // v30 and earlier carried owner-bound composition records. The v31 writer
    // omits this member and emits DesignedPages instead; keeping this readable
    // is the explicit legacy-import boundary.
    [JsonPropertyName("pageCompositions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProjectExportPageComposition>? LegacyPageCompositions { get; init; }
    [JsonIgnore]
    public IReadOnlyList<ProjectExportPageComposition> PageCompositions => LegacyPageCompositions ?? [];
    public List<ProjectExportDesignedPage> DesignedPages { get; init; } = [];
    public List<ProjectExportPublicationSection> PublicationSections { get; init; } = [];
    [JsonPropertyName("publishProfiles")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProjectExportLegacyPublishProfile>? LegacyPublishProfiles { get; init; }
    public List<ProjectExportManuscriptStyle> ManuscriptStyles { get; init; } = [];
    public List<ProjectExportFontFamily> FontFamilies { get; init; } = [];
    public List<ProjectExportAct> Acts { get; init; } = [];
    public List<ProjectExportChapter> Chapters { get; init; } = [];
    public List<ProjectExportManuscriptAnnotation> ManuscriptAnnotations { get; init; } = [];
    public List<ProjectExportNode> Nodes { get; init; } = [];
    public List<ProjectExportEdge> Edges { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public sealed record ProjectExportManuscriptAnnotation(
    Guid Id,
    Guid ChapterId,
    string ChapterTitle,
    Guid? EditionId,
    string? EditionName,
    ManuscriptAnnotationKind Kind,
    string NoteText,
    long Revision,
    long AnchorManuscriptRevision,
    ManuscriptAnnotationAnchorState AnchorState,
    string StartBlockId,
    int StartOffset,
    string EndBlockId,
    int EndOffset,
    string OriginalQuote,
    string ContextBefore,
    string ContextAfter,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record ProjectExportProject(
    Guid Id,
    string Name,
    string Slug,
    string ProjectGuidance,
    bool IncludeCurrentChapterInContext,
    [property: JsonIgnore] bool ReviewEditsEnabled)
{
    /// <summary>
    /// Legacy v19-and-earlier import input. It is intentionally nullable and
    /// never populated by current exports, so the renamed workflow setting does
    /// not leak into new project files.
    /// </summary>
    [JsonPropertyName("aiChangeApprovalEnabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyAiChangeApprovalEnabled { get; init; }

    [JsonPropertyName("systemPrompt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacySystemPrompt { get; init; }

    [JsonIgnore]
    public string EffectiveProjectGuidance =>
        !string.IsNullOrWhiteSpace(ProjectGuidance) ? ProjectGuidance : LegacySystemPrompt ?? string.Empty;
}

public sealed record ProjectExportPageSetup(
    double PageWidthInches,
    double PageHeightInches,
    double PageMarginInches,
    double BodyFontSizePoints,
    double BodyLineHeight);

// Read-only v13 import boundary. Current exports never populate these values.
public enum PrintPicturePageSpreadMode { WholeSpread, SidewaysWholeSpread, SplitLeaves }
public enum EpubPicturePageSpreadMode { RequestLandscape, SidewaysPortrait }

public sealed record ProjectExportBookBrief(
    BookKind BookKind,
    string Premise,
    string Genre,
    string PrimaryThemes,
    string Purpose,
    string CreativeConstraints,
    string TargetAudience,
    int? MinimumReaderAge,
    int? MaximumReaderAge,
    string ReadingLevelGuidance,
    int? TargetWordCount,
    string PointOfView,
    string Tense,
    string VoiceAndTone,
    string LanguageLocale,
    string HouseStyle,
    bool? ReadAloudPriority,
    string AccessibilityGoals,
    string VisualDirection);

public sealed record ProjectExportIngestSource(
    Guid Id,
    string Title,
    string SourceKind,
    string Description,
    string Synopsis,
    string UserInstructions,
    string SourceText,
    string SourceHash,
    string SourceUrl,
    string FinalUrl,
    string CanonicalUrl,
    DateTime? FetchedAt,
    string ContentType,
    string SourceMetadataJson,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    List<ProjectExportIngestSourceChunk> Chunks,
    List<ProjectExportIngestSourcePage> Pages,
    List<ProjectExportIngestSourceBlock> Blocks);

public sealed record ProjectExportIngestSourceChunk(
    Guid Id,
    int Index,
    string Title,
    string HeadingPath,
    int StartChar,
    int EndChar,
    int EstimatedTokenCount,
    string TokenCountMethod,
    string? TokenEncodingName,
    bool TokenCountIsExact,
    string Summary,
    string AgentNotes,
    IngestSourceChunkStructureStatus StructureStatus,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record ProjectExportIngestSourcePage(
    Guid Id,
    int PageNumber,
    string Text,
    int StartChar,
    int EndChar,
    string ExtractionMethod,
    int Width,
    int Height,
    string ImageHash,
    string RenderSettingsJson,
    int? VisionProviderId,
    string VisionModelName,
    string Diagnostics,
    DateTime CreatedAt);

public sealed record ProjectExportIngestSourceBlock(
    Guid Id,
    Guid? SourcePageId,
    int Index,
    string Kind,
    string Title,
    string Locator,
    int? PageNumber,
    int StartChar,
    int EndChar,
    string MetadataJson,
    DateTime CreatedAt);

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

/// <summary>
/// Keeps the versioned export boundary compatible with the persisted image
/// source values that predate the dedicated upscaled source. Before export
/// Before format v29, numeric source value 6 meant <see cref="PublishAssetSource.Imported"/>;
/// format v29 added a dedicated <see cref="PublishAssetSource.Upscaled"/>
/// source instead.
/// </summary>
internal static class ProjectExportImageCompatibility
{
    public static PublishAssetSource AdaptLegacySource(
        PublishAssetSource source,
        string? sourceMetadataJson)
    {
        if (source == PublishAssetSource.Upscaled)
            return PublishAssetSource.Imported;
        if (source != PublishAssetSource.Resized || string.IsNullOrWhiteSpace(sourceMetadataJson))
            return source;

        try
        {
            using var document = JsonDocument.Parse(sourceMetadataJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !TryGetPropertyIgnoreCase(document.RootElement, "transform", out var transform)
                || transform.ValueKind != JsonValueKind.Object
                || !TryGetPropertyIgnoreCase(transform, "kind", out var kind)
                || kind.ValueKind != JsonValueKind.String)
            {
                return source;
            }

            return string.Equals(kind.GetString(), "print-resample", StringComparison.Ordinal)
                ? PublishAssetSource.Upscaled
                : source;
        }
        catch (JsonException)
        {
            return source;
        }
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}

public sealed record ProjectExportFontFamily(
    Guid Id,
    string Name,
    List<ProjectExportFontFace> Faces,
    bool EmbeddingRightsConfirmed = false,
    string RightsDeclaration = "");

public sealed record ProjectExportFontFace(
    Guid Id,
    string SubfamilyName,
    string FileName,
    string ContentType,
    int Weight,
    bool Italic,
    byte[] Data,
    string Sha256);

public sealed record ProjectExportEntityVisualExample(
    ProjectExportNodeRef Entity,
    Guid ImageId,
    string Label,
    int SortOrder,
    EntityVisualExampleOrigin Origin,
    string SourceUrl,
    string SourceLocator);

public sealed record ProjectExportPublicationEdition(
    Guid Id,
    string Name,
    PublicationEditionFormat Format,
    PublicationVendor Vendor,
    string VendorProfileVersion,
    PublicationEditionStatus Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IsDefault,
    long Revision,
    string TitleOverride,
    string Subtitle,
    string Author,
    string Language,
    string Publisher,
    string Copyright,
    string Isbn,
    string Description,
    bool IncludeTableOfContents,
    bool IncludeVisibleTableOfContents,
    bool IncludeActSynopses,
    bool IncludeChapterSynopses,
    bool IncludeActHeadings,
    bool IncludeChapterHeadings,
    bool NumberActs,
    bool NumberChapters,
    PublishTitlePageMode TitlePageMode,
    double PageWidthInches,
    double PageHeightInches,
    double PageMarginInches,
    Guid? SelectedCoverImageId,
    LegacyPublicationBinding Binding,
    LegacyPublicationPaper Paper,
    LegacyPublicationInk Ink,
    bool Bleed,
    bool AllowDesignedPageOverrides,
    List<ProjectExportEditionOutlineItem> OutlineItems,
    ProjectExportCoverDesign? CoverDesign)
{
    public string PrintArtifactRegistryVersion { get; init; } = string.Empty;
    public string PrintArtifactProfileKey { get; init; } = string.Empty;
    [JsonPropertyName("printRegistryVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyPrintRegistryVersion { get; init; }
    [JsonPropertyName("printProductKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyPrintArtifactProfileKey { get; init; }
    [JsonIgnore]
    public string ImportedPrintArtifactRegistryVersion => string.IsNullOrWhiteSpace(PrintArtifactRegistryVersion)
        ? LegacyPrintRegistryVersion ?? string.Empty
        : PrintArtifactRegistryVersion;
    [JsonIgnore]
    public string ImportedPrintArtifactProfileKey => string.IsNullOrWhiteSpace(PrintArtifactProfileKey)
        ? LegacyPrintArtifactProfileKey ?? string.Empty
        : PrintArtifactProfileKey;
    public PrintCoverMode PrintCoverMode { get; init; } = PrintCoverMode.Simplex;
    public PrintProjectUse PrintProjectUse { get; init; } = PrintProjectUse.ForSale;
    public PrintIdentifierMode PrintIdentifierMode { get; init; } = PrintIdentifierMode.UserSuppliedIsbn;
    public PrintCoverSubmissionMode PrintCoverSubmissionMode { get; init; } = PrintCoverSubmissionMode.FullWrapMeasured;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? BodyFontSizePoints { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? BodyLineHeight { get; init; }
    [JsonIgnore]
    public double ImportedBodyFontSizePoints => BodyFontSizePoints ?? 12;
    [JsonIgnore]
    public double ImportedBodyLineHeight => BodyLineHeight ?? 1.55;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProjectExportPublicationMatter>? Matter { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProjectExportEditionStyleMapping>? StyleMappings { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProjectExportPublicationImagePlacement>? ImagePlacements { get; init; }
    [JsonIgnore]
    public IReadOnlyList<ProjectExportPublicationMatter> LegacyMatter => Matter ?? [];
    [JsonIgnore]
    public IReadOnlyList<ProjectExportEditionStyleMapping> LegacyStyleMappings => StyleMappings ?? [];
    [JsonIgnore]
    public IReadOnlyList<ProjectExportPublicationImagePlacement> LegacyImagePlacements => ImagePlacements ?? [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RectoChapterStarts { get; init; }
    public List<PublicationEditionOverrideField> OverrideFields { get; init; } = [];
    public bool InheritsCoreCover { get; init; }
    public bool EditionSpecificContentEnabled { get; init; }
    public List<ProjectExportEditionChapterOverride> ChapterOverrides { get; init; } = [];
    public Dictionary<Guid, int> PublicationSectionOrder { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PrintPicturePageSpreadMode PrintPicturePageSpreadMode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public EpubPicturePageSpreadMode EpubPicturePageSpreadMode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? SelectedCoverChapterId { get; init; }
}

public sealed record ProjectExportEditionChapterOverride(
    Guid Id,
    Guid ChapterId,
    string ManuscriptJson,
    long Revision,
    long BaseCoreRevision,
    string BaseCoreHash,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record ProjectExportPublicationBook(
    long Revision,
    string Title,
    string Subtitle,
    string Author,
    string Language,
    string Publisher,
    string Copyright,
    string Description,
    bool IncludeTableOfContents,
    bool IncludeVisibleTableOfContents,
    bool IncludeActSynopses,
    bool IncludeChapterSynopses,
    bool IncludeActHeadings,
    bool IncludeChapterHeadings,
    bool NumberActs,
    bool NumberChapters,
    PublishTitlePageMode TitlePageMode,
    List<ProjectExportEditionOutlineItem> OutlineItems,
    ProjectExportCoverDesign? CoverDesign)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProjectExportPublicationMatter>? Matter { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProjectExportPublicationImagePlacement>? ImagePlacements { get; init; }
    [JsonIgnore]
    public IReadOnlyList<ProjectExportPublicationMatter> LegacyMatter => Matter ?? [];
    [JsonIgnore]
    public IReadOnlyList<ProjectExportPublicationImagePlacement> LegacyImagePlacements => ImagePlacements ?? [];
    public bool AllowDesignedPageOverrides { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RectoChapterStarts { get; init; }
}

public sealed record ProjectExportCoverDesign(
    string Title,
    string Subtitle,
    string Author,
    string SpineText,
    string BackgroundColor,
    PublicationBarcodeMode BarcodeMode,
    double ImageCropXPercent,
    double ImageCropYPercent,
    string CompositionSceneJson,
    long Revision)
{
    public string SurfaceScenesJson { get; init; } = "{}";
    public SpineReadingDirection SpineReadingDirection { get; init; } = SpineReadingDirection.TopToBottom;

    [JsonPropertyName("backCopy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyBackCopy { get; init; }
}

public sealed record ProjectExportEditionOutlineItem(
    Guid Id,
    PublishOutlineTargetKind TargetKind,
    Guid TargetId,
    bool IsIncluded,
    int SortOrder);

public sealed record ProjectExportPublicationMatter(
    Guid Id,
    PublicationMatterLocation Location,
    PublicationMatterKind Kind,
    string Title,
    string ManuscriptJson,
    long Revision,
    bool IsIncluded,
    int SortOrder)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? CoreMatterId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsExcluded { get; init; }
}

public sealed record ProjectExportEditionStyleMapping(
    Guid Id,
    Guid ManuscriptStyleDefinitionId,
    string SemanticRole,
    ManuscriptStyleProperties Override,
    long Revision);

public sealed record ProjectExportPublicationImagePlacement(
    Guid Id,
    Guid AssetId,
    PublishOutlineTargetKind TargetKind,
    Guid TargetId,
    PublicationImagePlacementKind PlacementKind,
    string Caption,
    int SortOrder,
    FigurePresentation? Presentation = null,
    string AltText = "",
    bool Decorative = false,
    string Language = "en",
    FigureAccessibilityRole AccessibilityRole = FigureAccessibilityRole.Figure)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? CorePlacementId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsExcluded { get; init; }
}

public sealed record ProjectExportPublicationSection(
    Guid Id,
    Guid? EditionId,
    Guid? CoreSectionId,
    string Title,
    PublicationSectionKind Kind,
    PublicationSectionSystemRole SystemRole,
    PublicationSectionAnchor Anchor,
    PublishOutlineTargetKind? TargetKind,
    Guid? TargetId,
    PublicationSectionInclusionMode InclusionMode,
    PublicationSectionStartSide StartSide,
    bool IsExcluded,
    int LocalOrder,
    string ManuscriptJson,
    long Revision,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record ProjectExportLegacyPublishProfile(
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

public sealed record ProjectExportManuscriptStyle(
    Guid Id,
    string Name,
    ManuscriptStyleKind Kind,
    string SemanticRole,
    ManuscriptStyleProperties Definition,
    long Revision);

public sealed record ProjectExportChapter
{
    public Guid Id { get; init; }
    public Guid? ActId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string ManuscriptJson { get; init; } = string.Empty;
    public long ManuscriptRevision { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Body { get; init; }
    public string Synopsis { get; init; } = string.Empty;
    public int Order { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ChapterVisualMode VisualMode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ChapterPageLayoutKind PageLayoutKind { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string PageLayoutJson { get; init; } = string.Empty;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string IllustrationLayoutJson { get; init; } = string.Empty;
    public List<Guid> ExplicitImageContextImageIds { get; init; } = [];
}

public sealed record ProjectExportPageComposition(
    Guid Id,
    Guid? ChapterId,
    string Name,
    string SemanticManuscriptJson,
    long Revision,
    List<ProjectExportPageCompositionVariant> Variants,
    Guid? ActiveAuthoringVariantId = null)
{
    public Guid? EditionId { get; init; }
    public Guid? SourceCompositionId { get; init; }
    public Guid? PublicationSectionId { get; init; }
}

public sealed record ProjectExportPageCompositionVariant(
    Guid Id,
    string GeometryKey,
    string SceneJson,
    long Revision);

/// <summary>
/// v31 portable Designed Page identity. A page is project-owned; placements
/// remain in the manuscript blocks that reference its ID.
/// </summary>
public sealed record ProjectExportDesignedPage(
    Guid Id,
    string Name,
    Guid? ScopeEditionId,
    List<ProjectExportDesignedPageContent> Contents);

public sealed record ProjectExportDesignedPageContent(
    Guid Id,
    Guid DesignedPageId,
    Guid? EditionId,
    string SemanticManuscriptJson,
    string AccessibilityDescription,
    long Revision,
    List<ProjectExportDesignedPageVariant> Variants,
    Guid? ActiveVariantId = null);

public sealed record ProjectExportDesignedPageVariant(
    Guid Id,
    Guid ContentId,
    string GeometryKey,
    string SceneJson,
    long Revision);

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
