using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lorekeeper.Context;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Fonts;
using Lorekeeper.Images;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Composition;
using Lorekeeper.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using SkiaSharp;

namespace Lorekeeper.Publish;

public sealed class PublishAssistantContext(
    Guid projectId,
    Guid conversationId = default,
    Guid? selectedEditionId = null,
    PublishAssistantWorkspaceContext? workspaceContext = null,
    bool visionReady = false,
    CancellationToken turnCancellationToken = default)
{
    private readonly List<EntityVisualContextReference> _visuals = [];
    private readonly List<PublishAssistantTransientVisual> _transientVisuals = [];
    private readonly HashSet<Guid> _imageJobIds = [];

    public Guid ProjectId { get; } = projectId;
    public Guid ConversationId { get; } = conversationId;
    public Guid? SelectedEditionId { get; } = selectedEditionId;
    public PublishAssistantWorkspaceContext? WorkspaceContext { get; } = workspaceContext;
    public bool VisionReady { get; } = visionReady;
    public CancellationToken TurnCancellationToken { get; } = turnCancellationToken;
    public IReadOnlyList<Guid> ImageJobIds => _imageJobIds.ToList();
    public void TrackImageJob(Guid jobId) => _imageJobIds.Add(jobId);
    public void AddVisual(EntityVisualContextReference visual) => _visuals.Add(visual);
    public void AddTransientVisual(PublishAssistantTransientVisual visual) => _transientVisuals.Add(visual);
    public IReadOnlyList<EntityVisualContextReference> DrainVisuals()
    {
        var result = _visuals.ToList();
        _visuals.Clear();
        return result;
    }
    public IReadOnlyList<PublishAssistantTransientVisual> DrainTransientVisuals()
    {
        var result = _transientVisuals.ToList();
        _transientVisuals.Clear();
        return result;
    }
}

public sealed record PublishAssistantTransientVisual(
    Guid Id,
    string Title,
    string FileName,
    string ContentType,
    byte[] Data,
    string Caption,
    int? Width,
    int? Height,
    string SourceKind,
    Guid? SourceRefId);

public sealed record PublicationSectionToolInput(
    Guid? SectionId,
    string Title,
    PublicationSectionKind Kind,
    PublicationSectionAnchor Anchor,
    PublishOutlineTargetKind? TargetKind,
    Guid? TargetId,
    PublicationSectionInclusionMode Inclusion,
    PublicationSectionStartSide StartSide = PublicationSectionStartSide.Next,
    long? ExpectedRevision = null);

public sealed class PublishImageGenerationTarget
{
    [Description("Release ID for a release cover target. Omit for Core cover, page, Figure, and free-standing targets.")]
    public Guid? EditionId { get; init; }
    [Description("ProjectPage, Figure, PageFrame, PageSurface, CoreCoverFrame, CoreCoverSurface, CoverFrame, or CoverSurface. Omit for free-standing art.")]
    public string TargetKind { get; init; } = string.Empty;
    [Description("Stable target ID returned by the target read. Omit for free-standing art.")]
    public Guid? TargetId { get; init; }
    [Description("Exact page variant ID returned by the target read. Required for PageFrame and PageSurface targets.")]
    public Guid? VariantId { get; init; }
    [Description("Desired W:H, W/H, or decimal aspect for free-standing art. A bound target derives its aspect from Lorekeeper.")]
    public string AspectRatio { get; init; } = string.Empty;
    [Description("Optional canvas-local percentage bounds returned or verified for a PageSurface, CoverSurface, or CoreCoverSurface subregion.")]
    public CompositionBounds? SurfaceBounds { get; init; }
}

public interface IPublishAssistantTools
{
    Task<IList<AITool>> BuildAsync(
        PublishAssistantContext context,
        CancellationToken cancellationToken = default);
}

public sealed class PublishAssistantTools(
    IPublicationEditionService editions,
    IPublicationBookService books,
    IPublicationSectionService publicationSections,
    IPublicationPreparationService preparation,
    IPublishService publishing,
    IPublicationRenderService renders,
    IPublicationPaginationService pagination,
    IPublicationEpubPreviewService epubPreviews,
    IPublicationCoverService covers,
    IManuscriptStyleService manuscriptStyles,
    IProjectFontService projectFonts,
    IProjectPageSetupService pageSetups,
    IProjectImageService projectImages,
    IPrintArtifactProfileRegistry printArtifactProfiles,
    IPrintGeometryService printGeometry,
    IAppDatabaseOperationFactory database,
    IEditionContentService? editionContent = null,
    ICompositionCanvasPreviewService? canvasPreviews = null,
    ICompositionService? compositions = null,
    IAgentProjectImageWorkflow? imageWorkflow = null,
    IProjectSearchService? projectSearch = null,
    IReferenceVisualService? referenceVisuals = null) : IPublishAssistantTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public Task<IList<AITool>> BuildAsync(
        PublishAssistantContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IList<AITool> tools =
        [
            AIFunctionFactory.Create(
                method: (string? query = null, string[]? sourceTypes = null, int topK = 10) =>
                    ListSearchSourcesAsync(context, query, sourceTypes, topK),
                name: "list_search_sources",
                description: "Discover bounded active-project and direct-reference sources with stable IDs, origin provenance, and exact origin-qualified read arguments. References are read-only continuity evidence."),
            AIFunctionFactory.Create(
                method: (string sourceType, Guid sourceId, int? pageNumber = null, Guid? originProjectId = null) =>
                    ReadProjectSourceAsync(context, sourceType, sourceId, pageNumber, originProjectId),
                name: "read_project_source",
                description: "Read one paginated active-project or direct-reference source. Pass the exact originProjectId returned by discovery; arbitrary foreign IDs are rejected."),
            AIFunctionFactory.Create(
                method: (string query, int topK = 8, string[]? sourceTypes = null, string[]? sourceIds = null, Guid? containerSourceId = null, bool lexicalOnly = false) =>
                    SearchProjectAsync(context, query, topK, sourceTypes, sourceIds, containerSourceId, lexicalOnly),
                name: "search_project",
                description: "Run bounded hybrid search across the active project and direct references with stable provenance and exact origin-qualified detail-read arguments."),
            AIFunctionFactory.Create(
                method: () => ListReferenceVisualsAsync(context),
                name: "list_reference_visuals",
                description: "List canonical entity visuals from direct referenced projects only with project/entity/image provenance."),
            AIFunctionFactory.Create(
                method: (Guid originProjectId, Guid imageId) => ReadReferenceVisualAsync(context, originProjectId, imageId),
                name: "read_reference_visual",
                description: "Read one canonical visual attached to a direct referenced project. Arbitrary foreign or general-library images fail closed; bytes are read-only and never valid for placement or mutation."),
            AIFunctionFactory.Create(
                method: () => ReadPublicationBookAsync(context),
                name: "read_publication_book",
                description: "Read the Core Book's shared metadata, structure/design defaults, revision, and compact counts. Core Book is the target when no release is selected."),
            AIFunctionFactory.Create(
                method: (PublicationBookPatch patch) => PatchPublicationBookAsync(context, patch),
                name: "patch_publication_book",
                description: "Revision-check a sparse Core Book patch. Supply only changed values; omitted fields are preserved. Language accepts en, en-US, or en-GB. Results contain changed state and refresh metadata, not repeated manuscript or scene payloads."),
            AIFunctionFactory.Create(
                method: () => ReadEditionsAsync(context),
                name: "list_publication_releases",
                description: "List the optional Paperback, Hardcover, EPUB ebook, and PDF ebook releases with stable IDs, release format, destination, status, and revision."),
            AIFunctionFactory.Create(
                method: (PublicationEditionFormat format, PublicationVendor destination) => ListPrintArtifactOptions(format, destination),
                name: "list_print_artifact_options",
                description: "List only inputs that change Paperback or Hardcover artifacts: interior process, paper weight/thickness, cover construction, cover modes, trims, and page limits. Paper color and finish are intentionally excluded."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, int pageCount, string? surfaceRole = null) => ReadPrintGeometryAsync(context, releaseId, pageCount, surfaceRole),
                name: "read_print_artifact_geometry",
                description: "Calculate submitted, normalized, and reported page counts, exact spine width, and the requested outside, inside, case, or jacket surface geometry for the selected release artifact settings. Use the actual interior page count when available."),
            AIFunctionFactory.Create(
                method: (string name, PublicationEditionFormat format, PublicationVendor destination) => CreateReleaseAsync(context, name, format, destination),
                name: "create_publication_release",
                description: "Create an optional release from safe application-managed presets. Use Generic destination for EPUB and PDF ebook; Paperback and Hardcover destinations are AmazonKdp, IngramSpark, or Generic."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, int contentStart = 0, int contentCount = 30) => ReadWorkspaceAsync(context, releaseId, contentStart, contentCount),
                name: "read_publication_release",
                description: "Read a compact release projection with effective inherited values, field override markers, revision, bounded structure, counts, and current diagnostics."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, PublicationReleaseOverridePatch patch) => PatchReleaseAsync(context, releaseId, patch),
                name: "patch_publication_release_overrides",
                description: "Revision-check sparse release artifact settings and field overrides. ResetFields restores live Core inheritance. Language accepts en, en-US, or en-GB. Vendor profile versions are application-managed and cannot be supplied."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, bool enabled, long expectedRevision, bool confirmDiscard = false) =>
                    SetEditionContentEnabledAsync(context, releaseId, enabled, expectedRevision, confirmDiscard),
                name: "set_edition_specific_content",
                description: "Enable edition-specific manuscript editing, or disable it with confirmDiscard=true after reading differences. Disabling discards release chapter snapshots and layouts but never deletes project images or Book Text Styles."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, int offset = 0, int limit = 20) =>
                    ReadEditionContentDifferencesAsync(context, releaseId, offset, limit),
                name: "read_edition_content_differences",
                description: "Read a bounded compact list of chapters whose effective manuscript differs from Core, including comparison counts, Core-change warnings, and exact Editor links. This tool cannot mutate manuscript content or page layouts."),
            AIFunctionFactory.Create(
                method: (Guid? releaseId = null) => PrepareFilesAsync(context, releaseId),
                name: "prepare_publication_files",
                description: "Run the one-action compile, render/export, validation, and packaging workflow. Omit releaseId for the Core reading PDF; supply it for a publication release."),
            AIFunctionFactory.Create(
                method: (Guid preparationJobId) => CancelPreparationAsync(context, preparationJobId),
                name: "cancel_publication_preparation",
                description: "Cancel a queued or active Core/release file-preparation job."),
            AIFunctionFactory.Create(
                method: (Guid? releaseId = null) => ReadPreparationAsync(context, releaseId),
                name: "read_publication_readiness",
                description: "Read compact current preparation state, prioritized blockers, and immutable artifact download metadata for Core Book or one release."),
            AIFunctionFactory.Create(
                method: (Guid artifactId, int? locationIndex = null, int start = 0, int count = 4000) =>
                    ReadEpubArtifactPreviewAsync(context, artifactId, locationIndex, start, count),
                name: "read_epub_artifact_preview",
                description: "Read bounded navigation/spine metadata from one immutable prepared EPUB artifact. Supply locationIndex to read a bounded plain-text slice from that location. Returns no binary data or XHTML; preparation separately reports Lorekeeper's structural validation result."),
            AIFunctionFactory.Create(
                method: () => ReadCoreContentAsync(context),
                name: "read_publication_book_content",
                description: "Read the Core Book's ordered chapters with inclusion state in a compact list. Act presentation is configured on Core Book rather than treated as selectable content."),
            AIFunctionFactory.Create(
                method: (PublicationEditionOutlineItemUpdate[] updates, long expectedBookRevision) => PatchCoreContentAsync(context, updates, expectedBookRevision),
                name: "patch_publication_book_content",
                description: "Revision-check changed Core Book chapter inclusion choices. Use TargetKind Chapter; omitted chapters are preserved. Configure act headings and summaries with patch_publication_book."),
            AIFunctionFactory.Create(
                method: (Guid? releaseId = null, int offset = 0, int limit = 30) => ListPublicationSectionsAsync(context, releaseId, offset, limit),
                name: "list_publication_sections",
                description: "List a bounded page of effective Core or release publication sections with authored order, next/recto/verso start side, inclusion/inheritance state, revisions, and compact visual counts."),
            AIFunctionFactory.Create(
                method: (Guid sectionId, Guid? releaseId = null, int blockStart = 0, int blockCount = 30) => ReadPublicationSectionAsync(context, releaseId, sectionId, blockStart, blockCount),
                name: "read_publication_section",
                description: "Read one Core or release publication section with a bounded agent-manuscript-v1 semantic projection: compact rows, sparse structure and UTF-16 marks, interned paragraph formatting, figure/publication metadata, and designed-page pageCompositionId values. Use pageCompositionId with read_publication_page_composition and preview_publication_section_page_canvas before editing."),
            AIFunctionFactory.Create(
                method: (PublicationSectionToolInput input, Guid? releaseId = null) => UpsertPublicationSectionAsync(context, releaseId, input),
                name: "upsert_publication_section",
                description: "Create an empty Core/release publication section or revision-check only its metadata, including explicit inclusion and next/recto/verso start side. Use Next for a new ordinary custom single-page section unless the user explicitly requests a side; recto or verso may insert a numbered blank leaf. This tool never accepts or replaces manuscript content. After creating prose, use patch_publication_section_manuscript with focused operations. Supplying a selected release ID materializes an inherited section as a release customization while preserving its content. Choose one content mode per section: prose with optional Figures, or Designed Page canvases only."),
            AIFunctionFactory.Create(
                method: (Guid sectionId, long expectedRevision, ManuscriptOperationInput[] operations, Guid? releaseId = null) => PatchPublicationSectionManuscriptAsync(context, releaseId, sectionId, expectedRevision, operations),
                name: "patch_publication_section_manuscript",
                description: "Apply focused revision-checked manuscript operations to one user-authored Core/release publication section. Read the bounded section first. " + ManuscriptOperationInput.ToolOperationGuidance + " Title and copyright use their Designed Page scene tools; contents is generated automatically."),
            AIFunctionFactory.Create(
                method: (Guid[] orderedSectionIds, Guid? releaseId = null) => ReorderPublicationSectionsAsync(context, releaseId, orderedSectionIds),
                name: "reorder_publication_sections",
                description: "Reorder every publication section at one shared anchor without changing Core chapter order. Supply the complete bounded ID order returned by list_publication_sections for that anchor."),
            AIFunctionFactory.Create(
                method: (Guid sectionId, string name, DesignedPageLayoutMode layoutMode, int blockIndex, long expectedRevision, Guid? releaseId = null) =>
                    CreatePublicationSectionPageAsync(context, releaseId, sectionId, name, layoutMode, blockIndex, expectedRevision),
                name: "create_publication_section_designed_page",
                description: "Add a canvas to an empty or already-designed publication section using its current revision and the Core/release page geometry. Never add a page canvas to a prose section; create a separate section instead."),
            AIFunctionFactory.Create(
                method: (Guid sectionId, Guid? releaseId = null) => ResetOrDeletePublicationSectionAsync(context, releaseId, sectionId),
                name: "remove_publication_section",
                description: "Delete a Core or release-only section. For a customized inherited release section, reset it to the live Core section instead."),
            AIFunctionFactory.Create(
                method: (int offset = 0, int limit = 30) => ListNamedStylesAsync(context, offset, limit),
                name: "list_publication_book_text_styles",
                description: "List a compact page of project Book Text Styles with stable IDs, definitions, revisions, and continuation metadata."),
            AIFunctionFactory.Create(
                method: () => ListBookFontsAsync(context),
                name: "list_publication_book_fonts",
                description: "List the exact built-in and project font-family keys, names, categories, weights, and italic faces available to publication-section pages, covers, and Book Text Styles. Read this before choosing typography."),
            AIFunctionFactory.Create(
                method: (long expectedRevision, double pageWidthInches, double pageHeightInches, double pageMarginInches, double bodyFontSizePoints, double bodyLineHeight) =>
                    PatchPageSetupAsync(context, expectedRevision, pageWidthInches, pageHeightInches, pageMarginInches, bodyFontSizePoints, bodyLineHeight),
                name: "patch_publication_book_page_setup",
                description: "Revision-check the Core Book page and baseline text defaults. Read Core Book first and preserve unchanged values. Geometry changes safely reflow the reusable Core cover."),
            AIFunctionFactory.Create(
                method: (
                    string name,
                    string kind,
                    Guid? styleId = null,
                    long? expectedRevision = null,
                    string? fontFamilyKey = null,
                    double? fontSizePoints = null,
                    int? fontWeight = null,
                    bool? italic = null,
                    bool? smallCaps = null,
                    double? lineHeight = null,
                    double? spaceBeforePoints = null,
                    double? spaceAfterPoints = null,
                    bool? keepWithNext = null,
                    ParagraphAlignment? textAlign = null,
                    double? leftIndentEm = null,
                    double? rightIndentEm = null,
                    double? firstLineIndentEm = null,
                    bool? startOnNewPage = null) =>
                    UpsertBookTextStyleAsync(
                        context, styleId, name, kind, expectedRevision, fontFamilyKey,
                        fontSizePoints, fontWeight, italic, smallCaps, lineHeight,
                        spaceBeforePoints, spaceAfterPoints, keepWithNext, textAlign,
                        leftIndentEm, rightIndentEm, firstLineIndentEm, startOnNewPage),
                name: "upsert_publication_book_text_style",
                description: "Create or revision-check update one shared paragraph or character Book Text Style. Lorekeeper owns its stable semantic key. Read the style list first; updates require styleId and expectedRevision."),
            AIFunctionFactory.Create(
                method: (Guid styleId, long expectedRevision) => DeleteBookTextStyleAsync(context, styleId, expectedRevision),
                name: "delete_publication_book_text_style",
                description: "Delete one unused shared Book Text Style through the revision-checked service. Read the style list first."),
            AIFunctionFactory.Create(
                method: (int offset = 0, int limit = 30) => ListProjectImagesAsync(context, offset, limit),
                name: "list_project_images",
                description: "List a bounded page of reusable project images with stable asset IDs, file metadata, alt text, source, preview URL, and continuation metadata. Image bytes are omitted."),
            AIFunctionFactory.Create(
                method: (Guid imageId) => ReadProjectImageAsync(context, imageId),
                name: "read_project_image",
                description: "Read one project image and supply its bytes as visual context on the next model round when vision is available."),
            AIFunctionFactory.Create(
                method: (int offset = 0, int limit = 40) => ListManuscriptVisualsAsync(context, offset, limit),
                name: "list_publication_manuscript_visuals",
                description: "List a compact page of manuscript Figures and Designed Pages with stable target IDs, accessibility state, and composition IDs. No image bytes or manuscript text are returned."),
            AIFunctionFactory.Create(
                method: (Guid compositionId, Guid variantId, int semanticStart = 0, int semanticCount = 20, int objectStart = 0, int objectCount = 30, int structureStart = 0, int structureCount = 30) => ReadPageCompositionAsync(context, compositionId, variantId, semanticStart, semanticCount, objectStart, objectCount, structureStart, structureCount),
                name: "read_publication_page_composition",
                description: "Read one active-target geometry variant losslessly in bounded object pages. Call get_or_create_publication_section_page_variant first. Returns the complete surface, styles, object fields, semantic excerpts, revisions, image coverage, and continuation metadata without computed overlays."),
            AIFunctionFactory.Create(
                method: (Guid sectionId, Guid compositionId) => GetOrCreatePublicationSectionVariantAsync(context, sectionId, compositionId),
                name: "get_or_create_publication_section_page_variant",
                description: "Resolve the editable geometry variant for one publication-section page in the protected active Publish target. If the selected release still inherits the Core section, this materializes the release section and maps the source composition safely. Returns the effective section, composition, variant, and revisions."),
            AIFunctionFactory.Create(
                method: (Guid compositionId, Guid variantId, string mode = "annotated") => PreviewPublicationSectionPageAsync(context, context.SelectedEditionId, compositionId, variantId, mode),
                name: "preview_publication_section_page_canvas",
                description: "Render one complete publication-section Designed Page as a visible chat image and model-visible canvas when vision is available. Use annotated immediately after every scene mutation and clean after final validation. The preview creates no project image."),
            AIFunctionFactory.Create(
                method: (Guid variantId, long expectedRevision, string targetKind, Guid targetId, CompositionElementPatch patch) => PatchPublicationSectionPageElementAsync(context, context.SelectedEditionId, variantId, expectedRevision, targetKind, targetId, patch),
                name: "patch_publication_section_page_element",
                description: "Revision-check and patch one object, layer, or style on a Core/release publication-section Designed Page. Supply changed fields only."),
            AIFunctionFactory.Create(
                method: (Guid variantId, long expectedRevision, Guid targetId, bool retainAspectRatio = true) => FillPublicationSectionPageImageCanvasAsync(context, variantId, expectedRevision, targetId, retainAspectRatio),
                name: "fill_publication_section_page_image_canvas",
                description: "Make one image object cover the entire active publication-section page canvas. With retainAspectRatio=true it uses proportional crop-to-fill; false stretches the raster. Reread and require imageCoversCanvas=true before reporting success."),
            AIFunctionFactory.Create(
                method: (Guid variantId, long expectedRevision, Guid targetId, Guid imageId, FigureImageFit fit = FigureImageFit.Cover, string? altText = null, bool decorative = false, int? readingOrder = null) => PlacePublicationSectionPageImageAsync(context, variantId, expectedRevision, targetId, imageId, fit, altText, decorative, readingOrder),
                name: "place_project_image_in_publication_section_page_frame",
                description: "Place an existing project-image ID into one existing image frame on the active publication-section page. Defaults to Cover so the image crop-fills the frame. Use Contain only when the complete uncropped image matters, or Stretch only when distortion is intentional. Provide alt text or an explicit decorative decision."),
            AIFunctionFactory.Create(
                method: (Guid variantId, long expectedRevision, Guid imageId, FigureImageFit fit = FigureImageFit.Cover, string? altText = null, bool decorative = false, CompositionBounds? bounds = null, int? readingOrder = null) => AddPublicationSectionPageImageAsync(context, context.SelectedEditionId, variantId, expectedRevision, imageId, fit, altText, decorative, bounds, readingOrder),
                name: "add_project_image_to_publication_section_page",
                description: "Add an existing project-image ID to a publication-section Designed Page. Defaults to Cover at 0,0,100,100 so the image crop-fills the complete canvas; supply bounds only for a smaller intentional frame. Provide alt text or an explicit decorative decision."),
            AIFunctionFactory.Create(
                method: (Guid variantId, long expectedRevision, CompositionScene scene) => StagePublicationSectionPageCompositionAsync(context, variantId, expectedRevision, scene),
                name: "stage_publication_section_page_composition",
                description: "Submit one complete publication-section page scene exactly once. Returns a one-use stage ID and compact diagnostics without echoing the scene."),
            AIFunctionFactory.Create(
                method: (Guid stageId, long expectedRevision) => ApplyPublicationSectionPageCompositionStageAsync(context, stageId, expectedRevision),
                name: "apply_publication_section_page_composition_stage",
                description: "Apply one staged publication-section page scene using only its one-use stage ID and current variant revision."),
            AIFunctionFactory.Create(
                method: (Guid compositionId, long expectedRevision, ManuscriptOperationInput[] operations) => StagePublicationSectionPageSemanticAsync(context, compositionId, expectedRevision, operations),
                name: "stage_publication_section_page_semantic",
                description: "Stage focused semantic-copy operations for one user-authored publication-section page without repeating its full scene. Linked system copy cannot be removed or rebound."),
            AIFunctionFactory.Create(
                method: (Guid stageId, long expectedRevision) => ApplyPublicationSectionPageSemanticStageAsync(context, stageId, expectedRevision),
                name: "apply_publication_section_page_semantic_stage",
                description: "Apply one staged publication-section semantic edit using only its one-use stage ID and current composition revision."),
            AIFunctionFactory.Create(
                method: (Guid compositionId, long expectedCompositionRevision, Guid variantId, long expectedVariantRevision, ManuscriptOperationInput[] semanticOperations, CompositionScene scene) => StagePublicationSectionPageWorkspaceAsync(context, context.SelectedEditionId, compositionId, expectedCompositionRevision, variantId, expectedVariantRevision, semanticOperations, scene),
                name: "stage_publication_section_page_workspace",
                description: "Submit one complete publication-section page scene plus focused semantic operations exactly once. Returns a one-use stage ID and never echoes the scene."),
            AIFunctionFactory.Create(
                method: (Guid stageId, long expectedCompositionRevision) => ApplyPublicationSectionPageWorkspaceStageAsync(context, context.SelectedEditionId, stageId, expectedCompositionRevision),
                name: "apply_publication_section_page_workspace_stage",
                description: "Apply a staged publication-section page workspace using only its one-use stage ID and current composition revision."),
            AIFunctionFactory.Create(
                method: (string targetKind, Guid targetId, Guid? variantId = null, Guid? releaseId = null, string? surfaceRole = null, string? regionRole = null, CompositionBounds? surfaceBounds = null) => ReadLayoutGenerationTargetAsync(context, targetKind, targetId, variantId, releaseId, surfaceRole, regionRole, surfaceBounds),
                name: "read_publication_generation_target",
                description: "Resolve the aspect, protected regions, and reusable generationTarget for a Figure, page, cover surface/frame, or exact cover region. CoverRegion requires releaseId and regionRole Back, Spine, or Front. Pass surfaceBounds only for a verified PageSurface, CoverSurface, or CoreCoverSurface subregion. Use CoreCoverSurface/CoreCoverFrame with no releaseId for the Core front cover; use CoverSurface/CoverFrame with a releaseId for a release cover. Lorekeeper chooses the generation resolution and prepares the target asset internally."),
            AIFunctionFactory.Create(
                method: (Guid variantId) => ValidateCompositionAsync(context, context.SelectedEditionId, variantId),
                name: "validate_publication_page_composition",
                description: "Validate one publication-section Designed Page against the protected active Core/release target for geometry, semantic coverage, reading order, accessibility, overflow, image readiness, and font readiness."),
            AIFunctionFactory.Create(
                method: (ImageGenerationBrief brief, ImageReferenceUse[]? references = null, PublishImageGenerationTarget? geometryGuidance = null, string? altText = null, string? quality = null, string? outputFormat = null, int? outputCompression = null) =>
                    GenerateProjectImageAsync(context, brief, references, geometryGuidance, altText, quality, outputFormat, outputCompression),
                name: "generate_project_image",
                description: "Generate one unattached project image and wait for a terminal result. For a bound target, pass the generationTarget returned by read_publication_generation_target; for free-standing work, supply only the intended aspect when it matters. Compose edge-to-edge for the target and expect crop-to-fill placement. Lorekeeper selects resolution and prepares the appropriate target asset internally. Inspect the image, then place its returned imageId with the focused Cover placement tool in this turn."),
            AIFunctionFactory.Create(
                method: (Guid sourceImageId, ImageEditBrief brief, ImageReferenceUse[]? references = null, PublishImageGenerationTarget? geometryGuidance = null, string? altText = null, string? quality = null, string? outputFormat = null, int? outputCompression = null) =>
                    EditProjectImageAsync(context, sourceImageId, brief, references, geometryGuidance, altText, quality, outputFormat, outputCompression),
                name: "edit_project_image",
                description: "Edit one project image and wait for a terminal result. Use the original source for one coherent desired result. Pass a bound generationTarget when the edit must fill a specific page, frame, or cover region; describe any intentional framing expansion in the desired result. Lorekeeper selects resolution and prepares the appropriate target asset internally. Inspect the image, then place its returned imageId with Cover unless the user needs the complete uncropped source."),
            AIFunctionFactory.Create(
                method: (Guid jobId) => ReadProjectImageJobAsync(context, jobId, wait: false),
                name: "read_project_image_job",
                description: "Read compact status for an existing project-image job without replaying its prompt."),
            AIFunctionFactory.Create(
                method: (Guid jobId) => ReadProjectImageJobAsync(context, jobId, wait: true),
                name: "wait_project_image_job",
                description: "Reconnect to an existing project-image job and wait for terminal output without replaying its prompt."),
            AIFunctionFactory.Create(
                method: (Guid jobId) => CancelProjectImageJobAsync(context, jobId),
                name: "cancel_project_image_job",
                description: "Cancel one queued or running project-image job. No image is placed."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, PublicationEditionOutlineItemUpdate[] updates, long expectedRevision) =>
                    SetContentAsync(context, releaseId, updates, expectedRevision),
                name: "patch_publication_release_content",
                description: "Include or exclude chapters only where this release differs from Core Book. Act headings and summaries are sparse release setting overrides, not content rows."),
            AIFunctionFactory.Create(
                method: (Guid? releaseId = null, string? surfaceRole = null, int objectStart = 0, int objectCount = 30, int structureStart = 0, int structureCount = 30) => ReadCoverAsync(context, releaseId, surfaceRole, objectStart, objectCount, structureStart, structureCount),
                name: "read_publication_cover_design",
                description: "Read the Core front cover when releaseId is omitted, or a release's exact outside, inside, case, jacket, or cloth surface when releaseId and surfaceRole are supplied. Returns compact copy, artifact geometry, diagnostics, layers, and one bounded page of scene objects."),
            AIFunctionFactory.Create(
                method: (Guid? releaseId = null, string? surfaceRole = null, string mode = "annotated") => PreviewCoverCanvasAsync(context, releaseId, surfaceRole, mode),
                name: "preview_publication_cover_canvas",
                description: "Render the complete Core or exact release cover surface as a visible chat image and model-visible canvas when vision is available. Supply surfaceRole for outside, inside, case, or jacket work. Use annotated immediately after every mutation and clean after final validation. The preview never creates a project-image asset."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, string regionRole, string? surfaceRole = null) => ReadCoverRegionAsync(context, releaseId, regionRole, surfaceRole),
                name: "read_publication_cover_region",
                description: "Read one exact Back, Spine, or Front region including physical dimensions, aspect, safe inset, guides, output participation, orientation, and geometry fingerprint. Inspect the spine region before editing or generating spine artwork."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, string regionRole, string? surfaceRole = null, string mode = "annotated") => PreviewCoverRegionAsync(context, releaseId, regionRole, surfaceRole, mode),
                name: "preview_publication_cover_region",
                description: "Render and crop one Back, Spine, or Front region from the connected cover canvas. Use annotated after mutation and clean after validation; also inspect the complete wrap."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, long expectedRevision, Guid imageId, string regionRole, string? altText = null, bool decorative = false, string? surfaceRole = null) => FillCoverRegionAsync(context, releaseId, expectedRevision, imageId, regionRole, altText, decorative, surfaceRole),
                name: "fill_project_image_on_publication_cover_region",
                description: "Add an existing project image and crop-to-fill only the selected Back, Spine, or Front region without stretching or filling the whole wrap. The focal crop remains editable and region-constrained during spine reflow."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, long expectedRevision, SpineReadingDirection direction) => SetCoverSpineDirectionAsync(context, releaseId, expectedRevision, direction),
                name: "set_publication_cover_spine_direction",
                description: "Set spine copy to TopToBottom (US/English default), BottomToTop, or Horizontal and rotate the real text object without baking words into artwork."),
            AIFunctionFactory.Create(
                method: (Guid? releaseId = null) => ValidateCoverAsync(context, releaseId),
                name: "validate_publication_cover_composition",
                description: "Validate the Core front cover or a supplied release cover for geometry, accessibility, reading order, images, and construction-specific regions. Returns compact prioritized diagnostics."),
            AIFunctionFactory.Create(
                method: (long expectedBookRevision, long expectedCoverRevision, string targetKind, Guid targetId, CompositionElementPatch patch) => PatchCoreCoverElementAsync(context, expectedBookRevision, expectedCoverRevision, targetKind, targetId, patch),
                name: "patch_publication_core_cover_element",
                description: "Revision-check and patch one stable Core cover object, layer, or style with changed fields only. Core cover geometry comes from project page setup, and artwork remains below canonical cover copy."),
            AIFunctionFactory.Create(
                method: (long expectedBookRevision, long expectedCoverRevision, Guid targetId, Guid imageId, FigureImageFit fit = FigureImageFit.Cover, string? altText = null, bool decorative = false, int? readingOrder = null) => PlaceCoreCoverImageAsync(context, expectedBookRevision, expectedCoverRevision, targetId, imageId, fit, altText, decorative, readingOrder),
                name: "place_project_image_on_core_cover",
                description: "Place an existing project-image ID into one existing Core cover image object. Defaults to Cover so the image crop-fills the object. Use Contain only when the complete uncropped image matters, or Stretch only when distortion is intentional. Requires alt text or an explicit decorative decision."),
            AIFunctionFactory.Create(
                method: (long expectedBookRevision, long expectedCoverRevision, Guid imageId, FigureImageFit fit = FigureImageFit.Cover, string? altText = null, bool decorative = false, CompositionBounds? bounds = null, int? readingOrder = null) => AddCoreCoverImageAsync(context, expectedBookRevision, expectedCoverRevision, imageId, fit, altText, decorative, bounds, readingOrder),
                name: "add_project_image_to_core_cover",
                description: "Add an existing project-image ID as a new Core cover image object. Defaults to Cover at 0,0,100,100 so the image crop-fills the complete cover; supply bounds only for a smaller intentional frame. Requires alt text or an explicit decorative decision."),
            AIFunctionFactory.Create(
                method: (long expectedBookRevision, long expectedCoverRevision, CompositionScene scene) => StageCoreCoverCompositionAsync(context, expectedBookRevision, expectedCoverRevision, scene),
                name: "stage_publication_core_cover_composition",
                description: "Submit a complete Core front-cover scene exactly once. Reading order may be omitted; Lorekeeper preserves supplied relative order and uses object-array position as the deterministic fallback before validation. Artwork is normalized below canonical cover copy. Returns an opaque one-use stage ID and compact diagnostics without echoing the scene."),
            AIFunctionFactory.Create(
                method: (Guid stageId, long expectedBookRevision, long expectedCoverRevision) => ApplyCoreCoverCompositionStageAsync(context, stageId, expectedBookRevision, expectedCoverRevision),
                name: "apply_publication_core_cover_composition_stage",
                description: "Apply a staged Core front-cover scene using only its one-use stage ID and current Core/cover revisions. Never repeat the scene payload."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, long expectedReleaseRevision) => CustomizeReleaseCoverAsync(context, releaseId, expectedReleaseRevision),
                name: "customize_publication_release_cover",
                description: "Materialize the inherited Core front into an editable release cover while preserving the release's paperback spine, back, and barcode regions."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, long expectedReleaseRevision) => UseCoreCoverAsync(context, releaseId, expectedReleaseRevision),
                name: "use_core_publication_cover",
                description: "Delete the release cover override and restore live inheritance from the Core cover."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, PublicationCoverDesignUpdate update) =>
                    UpdateCoverAsync(context, releaseId, update),
                name: "update_publication_cover_design",
                description: "Update cover copy, colors, barcode behavior, and template acknowledgement. Use staged cover-composition tools for scene objects."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, long expectedRevision, string targetKind, Guid targetId, CompositionElementPatch patch) => PatchCoverElementAsync(context, releaseId, expectedRevision, targetKind, targetId, patch),
                name: "patch_publication_cover_element",
                description: "Revision-check patch one stable cover object, guide, layer, or style using only changed fields. Preserve unrelated cover state; cover artwork remains below canonical copy. Use full-scene staging for structural changes."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, string surfaceRole, long expectedRevision, string targetKind, Guid targetId, CompositionElementPatch patch) => PatchCoverSurfaceElementAsync(context, releaseId, surfaceRole, expectedRevision, targetKind, targetId, patch),
                name: "patch_publication_cover_surface_element",
                description: "Revision-check and patch one object, layer, or style on an exact outside, inside, case, or jacket surface. Read and visually preview that surface first; preserve every other surface."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, string surfaceRole, long expectedRevision, Guid targetId, Guid imageId, FigureImageFit fit = FigureImageFit.Cover, string? altText = null, bool decorative = false, int? readingOrder = null) => PlaceCoverSurfaceImageAsync(context, releaseId, surfaceRole, expectedRevision, targetId, imageId, fit, altText, decorative, readingOrder),
                name: "place_project_image_on_release_cover_surface",
                description: "Place an existing project-image ID into one image object on the exact outside, inside, case, or jacket surface. Defaults to Cover so the image crop-fills the object. This does not affect other surfaces."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, string surfaceRole, long expectedRevision, Guid imageId, FigureImageFit fit = FigureImageFit.Cover, string? altText = null, bool decorative = false, CompositionBounds? bounds = null, int? readingOrder = null) => AddCoverSurfaceImageAsync(context, releaseId, surfaceRole, expectedRevision, imageId, fit, altText, decorative, bounds, readingOrder),
                name: "add_project_image_to_release_cover_surface",
                description: "Add an existing project-image ID to the exact outside, inside, case, or jacket surface. Defaults to Cover at 0,0,100,100 so the image crop-fills the complete surface; supply bounds only for a smaller intentional frame. This does not affect other surfaces."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, long expectedRevision, Guid targetId, Guid imageId, FigureImageFit fit = FigureImageFit.Cover, string? altText = null, bool decorative = false, int? readingOrder = null) => PlaceCoverImageAsync(context, releaseId, expectedRevision, targetId, imageId, fit, altText, decorative, readingOrder),
                name: "place_project_image_on_release_cover",
                description: "Place an existing project-image ID into one release-cover image object. Defaults to Cover so the image crop-fills the object. Use Contain only when the complete uncropped image matters. Requires alt text or an explicit decorative decision."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, long expectedRevision, Guid imageId, FigureImageFit fit = FigureImageFit.Cover, string? altText = null, bool decorative = false, CompositionBounds? bounds = null, int? readingOrder = null) => AddCoverImageAsync(context, releaseId, expectedRevision, imageId, fit, altText, decorative, bounds, readingOrder),
                name: "add_project_image_to_release_cover",
                description: "Add an existing project-image ID as a new release-cover image object. Defaults to Cover at 0,0,100,100 so the image crop-fills the complete cover; supply bounds only for a smaller intentional frame. Requires alt text or an explicit decorative decision."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, long expectedRevision, CompositionScene scene, string? surfaceRole = null) =>
                    StageCoverCompositionAsync(context, releaseId, expectedRevision, scene, surfaceRole),
                name: "stage_publication_cover_composition",
                description: "Submit a complete release-cover scene exactly once. For an exact outside, inside, case, jacket, or cloth scene, pass the surfaceRole returned by read_publication_cover_design; omission targets the release's default surface. TextBinding is an editable text template and may contain repeatable {{title}}, {{subtitle}}, {{author}}, {{spineText}}, or {{backCopy}} tokens. Reading order may be omitted; Lorekeeper preserves supplied relative order and uses object-array position as the deterministic fallback before validation. Artwork is normalized below cover text. Returns an opaque one-use stage ID and compact diagnostics without echoing the scene."),
            AIFunctionFactory.Create(
                method: (Guid stageId, long expectedRevision) =>
                    ApplyCoverCompositionStageAsync(context, stageId, expectedRevision),
                name: "apply_publication_cover_composition_stage",
                description: "Apply a staged cover scene using only its one-use stage ID and expected cover revision. Never repeat the scene payload."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, PublishExportFormat format) =>
                    ExportAsync(context, releaseId, format),
                name: "export_publication_release",
                description: "Prepare a TXT, Markdown, or EPUB download and return safe metadata, the bound source fingerprint, and its in-app regeneration URL."),
        ];
        var currentTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "list_search_sources", "read_project_source", "search_project", "list_project_images", "read_project_image",
            "read_publication_book", "patch_publication_book", "list_publication_releases",
            "list_print_artifact_options", "read_print_artifact_geometry",
            "create_publication_release", "read_publication_release", "patch_publication_release_overrides",
            "set_edition_specific_content", "read_edition_content_differences",
            "prepare_publication_files", "cancel_publication_preparation", "read_publication_readiness",
            "read_publication_book_content", "patch_publication_book_content", "list_publication_sections",
            "read_publication_section", "upsert_publication_section", "patch_publication_section_manuscript", "reorder_publication_sections", "create_publication_section_designed_page", "remove_publication_section",
            "list_publication_book_text_styles", "list_publication_book_fonts", "patch_publication_book_page_setup", "upsert_publication_book_text_style", "delete_publication_book_text_style", "list_publication_manuscript_visuals",
            "read_publication_page_composition", "preview_publication_section_page_canvas", "patch_publication_section_page_element",
            "get_or_create_publication_section_page_variant", "fill_publication_section_page_image_canvas", "place_project_image_in_publication_section_page_frame",
            "add_project_image_to_publication_section_page", "stage_publication_section_page_composition", "apply_publication_section_page_composition_stage",
            "stage_publication_section_page_semantic", "apply_publication_section_page_semantic_stage",
            "stage_publication_section_page_workspace", "apply_publication_section_page_workspace_stage",
            "read_publication_generation_target", "validate_publication_page_composition",
            "generate_project_image", "edit_project_image", "read_project_image_job", "wait_project_image_job", "cancel_project_image_job",
            "patch_publication_release_content",
            "read_publication_cover_design", "preview_publication_cover_canvas", "validate_publication_cover_composition", "update_publication_cover_design",
            "read_publication_cover_region", "preview_publication_cover_region", "fill_project_image_on_publication_cover_region", "set_publication_cover_spine_direction",
            "patch_publication_core_cover_element", "place_project_image_on_core_cover", "add_project_image_to_core_cover", "customize_publication_release_cover", "use_core_publication_cover",
            "stage_publication_core_cover_composition", "apply_publication_core_cover_composition_stage",
            "patch_publication_cover_element", "patch_publication_cover_surface_element", "place_project_image_on_release_cover_surface", "add_project_image_to_release_cover_surface", "place_project_image_on_release_cover", "add_project_image_to_release_cover", "stage_publication_cover_composition", "apply_publication_cover_composition_stage",
            "export_publication_release",
        };
        return Task.FromResult<IList<AITool>>(tools
            .Where(tool => tool is AIFunction function && currentTools.Contains(function.Name))
            .ToList());
    }

    private async Task<string> ListSearchSourcesAsync(
        PublishAssistantContext context,
        string? query,
        string[]? sourceTypes,
        int topK)
    {
        if (projectSearch is null)
            return Serialize(new { ok = false, code = "SEARCH_UNAVAILABLE", summary = "Project search is unavailable." });
        var sources = await projectSearch.ListSourcesAsync(
            context.ProjectId,
            query,
            sourceTypes,
            Math.Clamp(topK, 1, 30),
            includeReferencedProjects: true);
        return ProjectSearchAgentPayload.SerializeSources(sources);
    }

    private async Task<string> ListReferenceVisualsAsync(PublishAssistantContext context)
    {
        if (referenceVisuals is null)
            return Serialize(new { ok = false, code = "REFERENCE_VISUALS_UNAVAILABLE", summary = "Reference visual access is unavailable." });
        var visuals = await referenceVisuals.ListAsync(context.ProjectId, context.TurnCancellationToken);
        return Serialize(new
        {
            resultKind = "referenceVisualDiscovery", returnedCount = visuals.Count,
            boundedLimit = ReferenceVisualService.MaximumListResults,
            mayHaveMore = visuals.Count == ReferenceVisualService.MaximumListResults,
            note = "Direct-reference canonical visuals are read-only continuity evidence and cannot be placed or mutated in the active project.",
            visuals,
        });
    }

    private async Task<string> ReadReferenceVisualAsync(PublishAssistantContext context, Guid originProjectId, Guid imageId)
    {
        if (referenceVisuals is null)
            return Serialize(new { ok = false, code = "REFERENCE_VISUALS_UNAVAILABLE", summary = "Reference visual access is unavailable." });
        var visual = await referenceVisuals.ReadAsync(context.ProjectId, originProjectId, imageId, context.VisionReady, context.TurnCancellationToken);
        if (visual is null)
            return Serialize(new { ok = false, code = "NOT_FOUND", originProjectId, imageId, summary = "The image is not an eligible canonical visual on a direct referenced project." });
        if (visual.Data is not null)
            context.AddTransientVisual(new(
                visual.ImageId,
                $"Referenced canonical visual: {visual.EntityName}",
                visual.FileName,
                visual.ContentType,
                visual.Data,
                $"Referenced project {visual.OriginProjectName} ({visual.OriginProjectId:N}); entity {visual.EntityType} {visual.EntityName} ({visual.EntityId:N}); label {visual.Label}; read-only continuity evidence.",
                null,
                null,
                "referencedCanonicalVisual",
                visual.ImageId));
        return Serialize(new
        {
            visual.OriginProjectId, visual.OriginProjectName, visual.OriginProjectSlug,
            visual.EntityId, visual.EntityType, visual.EntityName, visual.CanonicalReferenceId,
            visual.Label, visual.SortOrder, visual.ImageId, visual.FileName, visual.ContentType,
            visual.PreviewUrl, visual.AltText, visual.Prompt, visual.ImageSource,
            visual.IsReferenced, visual.DataDelivered,
            detailReadArguments = new { originProjectId = visual.OriginProjectId, imageId = visual.ImageId },
        });
    }

    private async Task<string> ReadProjectSourceAsync(
        PublishAssistantContext context,
        string sourceType,
        Guid sourceId,
        int? pageNumber,
        Guid? originProjectId)
    {
        if (projectSearch is null)
            return Serialize(new { ok = false, code = "SEARCH_UNAVAILABLE", summary = "Project search is unavailable." });
        var result = await projectSearch.ReadSourceAsync(context.ProjectId, sourceType, sourceId, pageNumber, originProjectId: originProjectId);
        return result is null
            ? Serialize(new { ok = false, code = "NOT_FOUND", sourceType, sourceId, summary = "Project source was not found." })
            : JsonSerializer.Serialize(result, JsonOptions);
    }

    private async Task<string> SearchProjectAsync(
        PublishAssistantContext context,
        string query,
        int topK,
        string[]? sourceTypes,
        string[]? sourceIds,
        Guid? containerSourceId,
        bool lexicalOnly)
    {
        if (projectSearch is null)
            return Serialize(new { ok = false, code = "SEARCH_UNAVAILABLE", summary = "Project search is unavailable." });
        IReadOnlyList<Guid>? parsedIds = null;
        if (sourceIds is { Length: > 0 })
        {
            var ids = new List<Guid>();
            foreach (var sourceId in sourceIds)
            {
                if (!Guid.TryParse(sourceId, out var id))
                    return Serialize(new { ok = false, code = "INVALID_SOURCE_ID", summary = $"'{sourceId}' is not a valid source ID." });
                ids.Add(id);
            }
            parsedIds = ids;
        }
        var result = await projectSearch.SearchAsync(new ProjectSearchRequest(
            context.ProjectId,
            query.Trim(),
            Math.Clamp(topK, 1, 30),
            sourceTypes,
            parsedIds,
            containerSourceId,
            lexicalOnly,
            IncludeReferencedProjects: true), context.TurnCancellationToken);
        return ProjectSearchAgentPayload.SerializeResults(query.Trim(), result);
    }

    private async Task<string> ReadProjectImageAsync(PublishAssistantContext context, Guid imageId)
    {
        var image = await projectImages.GetAsync(context.ProjectId, imageId, context.TurnCancellationToken);
        if (image is null)
            return Serialize(new { ok = false, code = "NOT_FOUND", targetId = imageId, summary = "Project image was not found." });
        context.AddVisual(new EntityVisualContextReference(
            image.Id,
            null,
            "ProjectImage",
            image.FileName,
            "explicit project image",
            0,
            image.FileName,
            image.AltText,
            image.Prompt,
            IsExplicitImage: true,
            ImageSource: image.Source));
        return Serialize(new
        {
            ok = true,
            targetId = image.Id,
            image.Id,
            image.FileName,
            image.ContentType,
            image.PreviewUrl,
            image.AltText,
            image.Source,
            image.Prompt,
            image.GenerationModel,
            image.SizeBytes,
            summary = "Project image loaded for visual inspection.",
        });
    }

    private async Task<string> ReadPublicationBookAsync(PublishAssistantContext context)
    {
        var book = await books.GetOrCreateAsync(context.ProjectId);
        return Serialize(new { ok = true, target = "core", targetId = context.ProjectId, book.Revision,
            summary = $"{book.IncludedChapterCount} included chapters. Publication sections are available through list_publication_sections.",
            values = new { book.Title, book.Subtitle, book.Author, book.Language, book.Publisher, book.Copyright, book.Description,
                book.IncludeTableOfContents, book.IncludeVisibleTableOfContents, book.IncludeActSynopses, book.IncludeChapterSynopses,
                book.IncludeActHeadings, book.IncludeChapterHeadings, book.NumberActs, book.NumberChapters, book.TitlePageMode,
                book.AllowDesignedPageOverrides, book.RectoChapterStarts, book.PageSetup },
            coverRevision = book.CoverRevision });
    }

    private async Task<string> PatchPublicationBookAsync(PublishAssistantContext context, PublicationBookPatch patch)
    {
        var book = await books.UpdateAsync(context.ProjectId, patch);
        return Serialize(new { ok = true, target = "core", targetId = context.ProjectId, revision = book.Revision,
            summary = "Core Book and linked title/copyright page copy updated; inheriting releases now resolve the changed values.",
            mutation = new { kind = "core-book", refresh = new[] { "core", "publication-sections", "releases", "readiness", "artifacts" } } });
    }

    private async Task<string> ReadCoreContentAsync(PublishAssistantContext context)
    {
        var book = await books.GetOrCreateAsync(context.ProjectId, context.TurnCancellationToken);
        var items = await books.ListOutlineAsync(context.ProjectId, context.TurnCancellationToken);
        return Serialize(new { ok = true, target = "core", revision = book.Revision,
            summary = $"{items.Count(item => item.IsIncluded && item.TargetKind == PublishOutlineTargetKind.Chapter)} chapters included.",
            items });
    }

    private async Task<string> PatchCoreContentAsync(
        PublishAssistantContext context,
        PublicationEditionOutlineItemUpdate[] updates,
        long expectedBookRevision)
    {
        var book = await books.SetOutlineSelectionsAsync(
            context.ProjectId, updates, expectedBookRevision, context.TurnCancellationToken);
        return Serialize(new { ok = true, target = "core", revision = book.Revision,
            changedIds = updates.Select(item => item.TargetId), summary = "Core Book content inclusion updated.",
            mutation = new { kind = "core-content", refresh = new[] { "core", "releases", "readiness", "artifacts" } } });
    }

    private async Task<string> ListPublicationSectionsAsync(
        PublishAssistantContext context,
        Guid? releaseId,
        int offset,
        int limit)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 50);
        var target = new PublicationSectionTarget(context.ProjectId, releaseId);
        var items = await publicationSections.ListAsync(target, context.TurnCancellationToken);
        var page = items.Skip(offset).Take(limit).Select(item => new
        {
            item.Id,
            item.CoreSectionId,
            item.Title,
            item.Kind,
            item.SystemRole,
            item.Anchor,
            item.TargetKind,
            anchorTargetId = item.TargetId,
            item.TargetTitle,
            item.InclusionMode,
            item.StartSide,
            item.IsIncluded,
            item.IsInherited,
            item.LocalOrder,
            item.Revision,
            item.DesignedPageCount,
            item.FigureCount,
            blockCount = item.Manuscript.Content.Count,
        }).ToList();
        return Serialize(new
        {
            ok = true,
            targetId = releaseId ?? context.ProjectId,
            releaseId,
            items = page,
            continuation = Continuation(offset, page.Count, items.Count),
            summary = $"{items.Count} effective publication section(s).",
        });
    }

    private async Task<string> ReadPublicationSectionAsync(
        PublishAssistantContext context,
        Guid? releaseId,
        Guid sectionId,
        int blockStart,
        int blockCount)
    {
        blockStart = Math.Max(0, blockStart);
        blockCount = Math.Clamp(blockCount, 1, 50);
        var item = await publicationSections.GetAsync(new(context.ProjectId, releaseId), sectionId, context.TurnCancellationToken);
        var returnedBlockCount = blockStart >= item.Manuscript.Content.Count
            ? 0
            : Math.Min(blockCount, item.Manuscript.Content.Count - blockStart);
        var pageCanvases = new List<object>();
        foreach (var compositionId in item.Manuscript.Content
            .Where(block => block.PageCompositionId.HasValue)
            .Select(block => block.PageCompositionId!.Value)
            .Distinct())
        {
            var composition = await (compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable."))
                .GetAsync(context.ProjectId, compositionId, context.TurnCancellationToken);
            if (composition is null)
                continue;
            var activeVariant = composition.ActiveAuthoringVariantId is Guid activeVariantId
                ? composition.Variants.FirstOrDefault(variant => variant.Id == activeVariantId)
                : composition.Variants.OrderByDescending(variant => variant.UpdatedAt).FirstOrDefault();
            pageCanvases.Add(new
            {
                compositionId = composition.Id,
                composition.Name,
                compositionRevision = composition.Revision,
                activeVariantId = activeVariant?.Id,
                activeVariantRevision = activeVariant?.Revision,
                activeVariant?.GeometryKey,
                editableInCurrentTarget = composition.EditionId == releaseId,
                requiresReleaseCustomization = releaseId is not null && composition.EditionId is null,
            });
        }
        var projection = JsonNode.Parse(AgentManuscriptProjection.SerializeDocument(
            item.Manuscript,
            item.IsInherited ? "inherited" : "persisted",
            blockStart,
            blockCount,
            sourceHash: null))?.AsObject()
            ?? throw new InvalidOperationException("The manuscript projection could not be created.");
        projection["ok"] = true;
        projection["targetId"] = item.Id;
        projection["sectionId"] = item.Id;
        if (releaseId is Guid releaseValue)
            projection["releaseId"] = releaseValue;
        projection["section"] = JsonSerializer.SerializeToNode(new
        {
            item.CoreSectionId,
            item.Title,
            item.Kind,
            item.SystemRole,
            item.Anchor,
            item.TargetKind,
            anchorTargetId = item.TargetId,
            item.InclusionMode,
            item.StartSide,
            item.IsInherited,
            item.Revision,
        }, ContextPayloadJson.Options);
        projection["pageCanvases"] = JsonSerializer.SerializeToNode(pageCanvases, JsonOptions);
        projection["continuation"] = JsonSerializer.SerializeToNode(
            Continuation(blockStart, returnedBlockCount, item.Manuscript.Content.Count),
            JsonOptions);
        return projection.ToJsonString(ContextPayloadJson.Options);
    }

    private async Task<string> UpsertPublicationSectionAsync(
        PublishAssistantContext context,
        Guid? releaseId,
        PublicationSectionToolInput input)
    {
        var isNew = input.SectionId is null;
        var document = input.SectionId is Guid sectionId
            ? (await publicationSections.GetAsync(new(context.ProjectId, releaseId), sectionId, context.TurnCancellationToken)).Manuscript
            : ManuscriptCodec.CreateEmpty(Guid.NewGuid());
        var saved = await publicationSections.UpsertAsync(new(context.ProjectId, releaseId), new(
            input.SectionId,
            input.Title,
            input.Kind,
            input.Anchor,
            input.TargetKind,
            input.TargetId,
            input.Inclusion,
            input.StartSide,
            ManuscriptCodec.Serialize(document),
            input.ExpectedRevision), context.TurnCancellationToken);
        return Serialize(new
        {
            ok = true,
            targetId = saved.Id,
            releaseId,
            revision = saved.Revision,
            created = isNew,
            changedFields = new[] { "title", "kind", "anchor", "target", "inclusion", "startSide" },
            summary = isNew
                ? $"Created empty publication section '{saved.Title}'. Add prose with focused manuscript operations or add a Designed Page canvas next."
                : $"Saved publication section metadata for '{saved.Title}' without replacing its content.",
            mutation = new { kind = "publication-section", releaseId, sectionId = saved.Id, refresh = new[] { "core", "release", "readiness", "artifacts" } },
        });
    }

    private async Task<string> CreatePublicationSectionPageAsync(
        PublishAssistantContext context,
        Guid? releaseId,
        Guid sectionId,
        string name,
        DesignedPageLayoutMode layoutMode,
        int blockIndex,
        long expectedRevision)
    {
        var result = await publicationSections.CreateDesignedPageAsync(
            new(context.ProjectId, releaseId), sectionId, blockIndex, name, layoutMode, expectedRevision,
            context.TurnCancellationToken);
        return Serialize(new
        {
            ok = true,
            targetId = result.Composition.Id,
            compositionId = result.Composition.Id,
            releaseId,
            sectionId = result.Section.Id,
            revision = result.Section.Revision,
            changedIds = new[] { result.Composition.Id },
            summary = $"Inserted Designed Page '{result.Composition.Name}' into '{result.Section.Title}'.",
            mutation = new { kind = "publication-section", releaseId, sectionId = result.Section.Id, compositionId = result.Composition.Id, refresh = new[] { "core", "release", "readiness", "artifacts" } },
        });
    }

    private async Task<string> PatchPublicationSectionManuscriptAsync(
        PublishAssistantContext context,
        Guid? releaseId,
        Guid sectionId,
        long expectedRevision,
        ManuscriptOperationInput[] operations)
    {
        var saved = await publicationSections.PatchManuscriptAsync(
            new(context.ProjectId, releaseId), sectionId, expectedRevision, operations,
            context.TurnCancellationToken);
        return Serialize(new
        {
            ok = true,
            targetId = saved.Id,
            releaseId,
            revision = saved.Revision,
            operationCount = operations.Length,
            summary = $"Updated publication section '{saved.Title}'.",
            mutation = new { kind = "publication-section", releaseId, sectionId = saved.Id, refresh = new[] { "core", "release", "readiness", "artifacts" } },
        });
    }

    private async Task<string> ReorderPublicationSectionsAsync(
        PublishAssistantContext context,
        Guid? releaseId,
        Guid[] orderedSectionIds)
    {
        await publicationSections.ReorderWithinAnchorAsync(
            new(context.ProjectId, releaseId), orderedSectionIds, context.TurnCancellationToken);
        return Serialize(new
        {
            ok = true,
            targetId = releaseId ?? context.ProjectId,
            releaseId,
            changedIds = orderedSectionIds,
            summary = "Publication sections reordered within their anchor.",
            mutation = new { kind = "publication-section", releaseId, refresh = new[] { "core", "release", "readiness", "artifacts" } },
        });
    }

    private async Task<string> ResetOrDeletePublicationSectionAsync(
        PublishAssistantContext context,
        Guid? releaseId,
        Guid sectionId)
    {
        var target = new PublicationSectionTarget(context.ProjectId, releaseId);
        var item = await publicationSections.GetAsync(target, sectionId, context.TurnCancellationToken);
        if (releaseId is Guid editionId && item.CoreSectionId is not null && !item.IsInherited)
            await publicationSections.ResetAsync(context.ProjectId, editionId, item.Id, context.TurnCancellationToken);
        else
            await publicationSections.DeleteAsync(target, item.Id, context.TurnCancellationToken);
        return Serialize(new
        {
            ok = true,
            targetId = sectionId,
            releaseId,
            summary = item.CoreSectionId is not null && !item.IsInherited ? "Release section reset to Core Book." : "Publication section removed.",
            mutation = new { kind = "publication-section", releaseId, sectionId, refresh = new[] { "core", "release", "readiness", "artifacts" } },
        });
    }

    private Task<string> CreateReleaseAsync(PublishAssistantContext context, string name, PublicationEditionFormat format, PublicationVendor destination) =>
        CreateReleaseCoreAsync(context, name, format,
            format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover ? destination : PublicationVendor.Generic);

    private string ListPrintArtifactOptions(PublicationEditionFormat format, PublicationVendor destination)
    {
        if (format is not (PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover))
            return Serialize(new { ok = false, code = "PRINT_FORMAT_REQUIRED", summary = "Choose Paperback or Hardcover." });
        var options = printArtifactProfiles.List(format, destination).Select(item => new
        {
            artifactProfileKey = item.Key,
            process = item.InteriorProcess.ToString(),
            item.BasisWeightPounds,
            item.Gsm,
            paperThicknessInchesPerPage = EffectiveCaliper(item),
            construction = item.CoverMaterial.ToString(),
            coverModes = item.CoverModes,
            trims = item.TrimSizes.Take(20),
            item.AllowsCustomTrim,
            submittedPageRange = new
            {
                minimum = item.MinimumSubmittedPages ?? item.MinimumPages,
                maximum = item.MaximumSubmittedPages ?? item.MaximumPages,
            },
            normalizedCoverPageRange = new { minimum = item.MinimumPages, maximum = item.MaximumPages },
        }).ToArray();
        return Serialize(new { ok = true, registryVersion = printArtifactProfiles.Version, registrySha256 = printArtifactProfiles.Sha256, count = options.Length, options });
    }

    private static decimal? EffectiveCaliper(PrintArtifactProfile profile)
    {
        if (profile.SpineModel.InchesPerPage is { } exact) return exact;
        var anchors = profile.SpineModel.Anchors?.OrderBy(item => item.Pages).ToArray() ?? [];
        return anchors.Length > 1
            ? (anchors[^1].Inches - anchors[0].Inches) / (anchors[^1].Pages - anchors[0].Pages)
            : null;
    }

    private async Task<string> ReadPrintGeometryAsync(PublishAssistantContext context, Guid releaseId, int pageCount, string? surfaceRole)
    {
        var workspace = await publishing.GetWorkspaceAsync(context.ProjectId, releaseId, context.TurnCancellationToken);
        var geometry = printGeometry.Calculate(new PublicationEdition
        {
            Id = workspace.Edition.Id,
            ProjectId = context.ProjectId,
            Name = workspace.Edition.Name,
            Format = workspace.Edition.Format,
            Vendor = workspace.Edition.Vendor,
            PrintArtifactRegistryVersion = workspace.Edition.PrintArtifactRegistryVersion,
            PrintArtifactProfileKey = workspace.Edition.PrintArtifactProfileKey,
            PrintCoverMode = workspace.Edition.PrintCoverMode,
            PageWidthInches = workspace.Edition.PageWidthInches,
            PageHeightInches = workspace.Edition.PageHeightInches,
            Bleed = workspace.Edition.Bleed,
        }, pageCount, surfaceRole);
        return Serialize(new { ok = true, targetId = releaseId, revision = workspace.Edition.Revision, geometry });
    }

    private async Task<string> PatchReleaseAsync(PublishAssistantContext context, Guid releaseId, PublicationReleaseOverridePatch patch)
    {
        var release = await editions.PatchOverridesAsync(context.ProjectId, releaseId, patch);
        return Serialize(new { ok = true, targetId = release.Id, revision = release.Revision,
            summary = "Release overrides updated.", mutation = new { kind = "release", releaseId = release.Id, selectRelease = true, refresh = new[] { "release", "readiness", "artifacts" } } });
    }

    private async Task<string> SetEditionContentEnabledAsync(
        PublishAssistantContext context,
        Guid releaseId,
        bool enabled,
        long expectedRevision,
        bool confirmDiscard)
    {
        try
        {
            var result = await RequireEditionContent().SetEnabledAsync(
                context.ProjectId,
                releaseId,
                expectedRevision,
                enabled,
                confirmDiscard,
                context.TurnCancellationToken);
            return Serialize(new
            {
                ok = true,
                targetId = releaseId,
                result.Enabled,
                result.DivergentChapterCount,
                summary = enabled
                    ? "Edition-specific manuscript editing enabled."
                    : "Edition-specific manuscript content discarded; shared Book Text Styles and project images were preserved.",
                mutation = new { kind = "edition-content", releaseId, refresh = new[] { "release", "readiness", "artifacts" } },
            });
        }
        catch (InvalidOperationException ex)
        {
            return Serialize(new { ok = false, code = "EDITION_CONTENT_REJECTED", targetId = releaseId, summary = ex.Message });
        }
    }

    private async Task<string> ReadEditionContentDifferencesAsync(
        PublishAssistantContext context,
        Guid releaseId,
        int offset,
        int limit)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 50);
        string projectSlug;
        await using (var operation = await database.OpenReadAsync(context.TurnCancellationToken))
        {
            projectSlug = await operation.Db.Projects
                .Where(item => item.Id == context.ProjectId)
                .Select(item => item.Slug)
                .SingleAsync(context.TurnCancellationToken);
        }
        var releases = await RequireEditionContent().ListReleasesAsync(context.ProjectId, context.TurnCancellationToken);
        var release = releases.SingleOrDefault(item => item.EditionId == releaseId)
            ?? throw new KeyNotFoundException("Publication release not found.");
        var differences = await RequireEditionContent().ReadDifferencesAsync(
            context.ProjectId, releaseId, offset, limit, context.TurnCancellationToken);
        return Serialize(new
        {
            ok = true,
            targetId = releaseId,
            release.Enabled,
            total = release.DivergentChapterCount,
            offset,
            returned = differences.Count,
            hasMore = offset + differences.Count < release.DivergentChapterCount,
            differences = differences.Select(item => new
            {
                item.ChapterId,
                item.ChapterTitle,
                item.Added,
                item.Removed,
                item.Moved,
                item.TextEdited,
                item.Figures,
                item.StyleReferences,
                item.DirectFormatting,
                item.DesignedPages,
                item.ChangedBlockIds,
                editorUrl = $"/projects/{projectSlug}/editor/{item.ChapterId:N}?edition={releaseId:N}",
                layoutIssues = item.LayoutIssues.Select(issue => new
                {
                    issue.Code,
                    issue.Message,
                    issue.CompositionId,
                    issue.ObjectId,
                    editorUrl = $"/projects/{projectSlug}/editor/{item.ChapterId:N}?edition={releaseId:N}&mode=pages&composition={issue.CompositionId:N}"
                        + (issue.ObjectId is Guid objectId ? $"&object={objectId:N}" : string.Empty),
                }),
            }),
        });
    }

    private IEditionContentService RequireEditionContent() =>
        editionContent ?? throw new InvalidOperationException("Edition-content services are unavailable.");

    private async Task<string> PrepareFilesAsync(PublishAssistantContext context, Guid? releaseId)
    {
        var job = releaseId is Guid id
            ? await preparation.PrepareReleaseAsync(context.ProjectId, id)
            : await preparation.PrepareCoreAsync(context.ProjectId);
        return Serialize(new { ok = true, targetId = releaseId ?? context.ProjectId, releaseId, job.Id, job.Status, job.Step, job.ProgressPercent, job.Message,
            imagePreparation = job.ImagePreparationSummary,
            summary = releaseId is null ? "Core reading-PDF preparation queued." : "Release file preparation queued.", mutation = new { kind = "preparation", releaseId } });
    }

    private async Task<string> CancelPreparationAsync(PublishAssistantContext context, Guid preparationJobId)
    {
        var job = await preparation.CancelAsync(context.ProjectId, preparationJobId);
        return Serialize(new { ok = true, targetId = job.EditionId ?? context.ProjectId, releaseId = job.EditionId, job.Id, job.Status, job.Message,
            imagePreparation = job.ImagePreparationSummary,
            summary = "Preparation cancellation recorded.", mutation = new { kind = "preparation", releaseId = job.EditionId } });
    }

    private async Task<string> ReadPreparationAsync(PublishAssistantContext context, Guid? releaseId)
    {
        var kind = releaseId is null ? PublicationTargetKind.CoreBook : PublicationTargetKind.Release;
        var jobs = await preparation.ListAsync(context.ProjectId, kind, releaseId);
        var cover = releaseId is Guid editionId
            ? await covers.GetAsync(context.ProjectId, editionId, context.TurnCancellationToken)
            : await books.GetCoverAsync(context.ProjectId, context.TurnCancellationToken);
        var coverDiagnostics = StructuredCoverDiagnostics(cover);
        var preparationDiagnostics = jobs.FirstOrDefault()?.Diagnostics ?? [];
        var currentDiagnostics = preparationDiagnostics
            .Select(item => (item.Severity, item.Code, item.Message))
            .Concat(coverDiagnostics.Select(item => (item.Severity, item.Code, item.Message)))
            .Distinct()
            .ToList();
        var artifacts = releaseId is Guid id
            ? await renders.ListArtifactsAsync(context.ProjectId, id)
            : (await renders.ListCoreAsync(context.ProjectId)).SelectMany(item => item.Artifacts).OrderByDescending(item => item.CreatedAt).ToList();
        return Serialize(new { ok = true, targetId = releaseId ?? context.ProjectId,
            current = jobs.Take(3).Select(job => new { job.Id, releaseId = job.EditionId, job.Status, job.Step,
                job.ProgressPercent, job.Message, job.CreatedAt, job.CompletedAt,
                imagePreparation = job.ImagePreparationSummary,
                diagnostics = job.Diagnostics.Take(5) }),
            diagnosticCounts = new
            {
                errors = currentDiagnostics.Count(item => item.Severity == "error"),
                warnings = currentDiagnostics.Count(item => item.Severity == "warning"),
            },
            coverDiagnostics = coverDiagnostics.Take(12),
            coverLegacyDiagnostics = cover.Diagnostics.Take(12),
            hasMoreCoverDiagnostics = coverDiagnostics.Count > 12,
            artifacts = artifacts.Where(item => !item.IsLegacy).Take(12).Select(item => DownloadView(context, item)), hasMoreArtifacts = artifacts.Count > 12 });
    }

    private async Task<string> ReadEpubArtifactPreviewAsync(
        PublishAssistantContext context,
        Guid artifactId,
        int? locationIndex,
        int start,
        int count)
    {
        PublicationEpubPreviewDocument? preview;
        try
        {
            preview = await epubPreviews.ReadAsync(context.ProjectId, artifactId, context.TurnCancellationToken);
        }
        catch (InvalidDataException exception)
        {
            return Serialize(new { ok = false, targetId = artifactId, code = "EPUB_PREVIEW_INVALID", summary = exception.Message });
        }
        if (preview is null)
            return Serialize(new { ok = false, targetId = artifactId, code = "EPUB_ARTIFACT_NOT_FOUND", summary = "The immutable EPUB artifact is unavailable." });

        var locations = preview.Locations.Take(80).Select(item => new
        {
            item.Index,
            item.Title,
            item.IsFixedLayout,
            viewport = item.ViewportWidth is int width && item.ViewportHeight is int height
                ? new { width, height }
                : null,
        });
        if (locationIndex is null)
        {
            return Serialize(new
            {
                ok = true,
                targetId = artifactId,
                preview.Title,
                artifactSha256 = preview.ArtifactSha256,
                locationCount = preview.Locations.Count,
                locations,
                hasMoreLocations = preview.Locations.Count > 80,
                previewAction = "Use Preview EPUB beside the current artifact in Publish.",
                downloadUrl = $"/projects/{context.ProjectId:N}/publish/artifacts/{artifactId:N}/download",
                validationScope = "Visual inspection of a Lorekeeper structurally validated artifact; not cross-reader acceptance.",
            });
        }

        PublicationEpubPreviewText? text;
        try
        {
            text = await epubPreviews.ReadLocationTextAsync(
                context.ProjectId,
                artifactId,
                locationIndex.Value,
                Math.Max(0, start),
                Math.Clamp(count, 1, 12000),
                context.TurnCancellationToken);
        }
        catch (InvalidDataException exception)
        {
            return Serialize(new { ok = false, targetId = artifactId, code = "EPUB_PREVIEW_INVALID", summary = exception.Message });
        }
        return text is null
            ? Serialize(new { ok = false, targetId = artifactId, code = "EPUB_LOCATION_NOT_FOUND", summary = "The requested EPUB spine location is unavailable." })
            : Serialize(new
            {
                ok = true,
                targetId = artifactId,
                text.Location.Index,
                text.Location.Title,
                text.Location.IsFixedLayout,
                text.Start,
                text.Count,
                text.TotalCharacters,
                text.HasMore,
                text.Text,
                nextArguments = text.HasMore
                    ? new { artifactId, locationIndex, start = text.Start + text.Count, count = Math.Clamp(count, 1, 12000) }
                    : null,
            });
    }

    private async Task<string> ReadEditionsAsync(PublishAssistantContext context) =>
        Serialize(await editions.ListAsync(context.ProjectId));

    private async Task<string> ReadWorkspaceAsync(PublishAssistantContext context, Guid editionId, int contentStart = 0, int contentCount = 30)
    {
        var workspace = await publishing.GetWorkspaceAsync(context.ProjectId, editionId);
        contentStart = Math.Max(0, contentStart);
        contentCount = Math.Clamp(contentCount, 1, 50);
        var content = workspace.Sections.SelectMany(section => section.Chapters.Select(chapter => new
        {
            section.ActId,
            ActTitle = section.Title,
            chapter.Id,
            chapter.Title,
            chapter.IsIncluded,
            chapter.FigureCount,
            chapter.DesignedPageCount,
            chapter.LayoutDiagnosticCount,
        })).ToList();
        return Serialize(new
        {
            ok = true,
            targetId = workspace.Edition.Id,
            revision = workspace.Edition.Revision,
            summary = $"{workspace.Sections.Sum(section => section.Chapters.Count)} chapter(s) and {workspace.PublicationSections.Count} effective publication section(s).",
            release = new
            {
                workspace.Edition.Id,
                workspace.Edition.Name,
                workspace.Edition.Format,
                Destination = workspace.Edition.Vendor,
                workspace.Edition.Status,
                workspace.Edition.Isbn,
                workspace.Edition.PrintArtifactRegistryVersion,
                workspace.Edition.PrintArtifactProfileKey,
                workspace.Edition.PrintCoverMode,
                PrintBleedManaged = workspace.Edition.Format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover,
                workspace.Edition.AllowDesignedPageOverrides,
                workspace.Edition.InheritsCoreCover,
                effective = new
                {
                    Title = workspace.Edition.TitleOverride,
                    workspace.Edition.Subtitle,
                    workspace.Edition.Author,
                    workspace.Edition.Language,
                    workspace.Edition.Publisher,
                    workspace.Edition.Copyright,
                    workspace.Edition.Description,
                    workspace.Edition.IncludeTableOfContents,
                    workspace.Edition.IncludeVisibleTableOfContents,
                    workspace.Edition.IncludeActHeadings,
                    workspace.Edition.IncludeChapterHeadings,
                    workspace.Edition.NumberActs,
                    workspace.Edition.NumberChapters,
                    workspace.Edition.TitlePageMode,
                    workspace.Edition.RectoChapterStarts,
                    workspace.Edition.PageWidthInches,
                    workspace.Edition.PageHeightInches,
                    workspace.Edition.PageMarginInches,
                    workspace.Edition.BodyFontSizePoints,
                    workspace.Edition.BodyLineHeight,
                },
                overrideFields = workspace.OverrideFields,
            },
            content = content.Skip(contentStart).Take(contentCount),
            publicationSections = workspace.PublicationSections.Take(12).Select(item => new
            {
                item.Id, item.CoreSectionId, item.Title, item.Kind, item.SystemRole, item.Anchor,
                item.TargetKind, item.TargetId, item.IsIncluded, item.IsInherited, item.Revision,
                item.DesignedPageCount, item.FigureCount,
            }),
            continuation = new { start = contentStart, returned = Math.Min(contentCount, Math.Max(0, content.Count - contentStart)), total = content.Count, hasMore = contentStart + contentCount < content.Count, nextStart = contentStart + contentCount < content.Count ? contentStart + contentCount : (int?)null },
            counts = new
            {
                publicationSections = workspace.PublicationSections.Count,
                availableReleases = workspace.Editions.Count,
            },
        });
    }

    private async Task<string> GenerateProjectImageAsync(
        PublishAssistantContext context,
        ImageGenerationBrief brief,
        ImageReferenceUse[]? references,
        PublishImageGenerationTarget? geometryGuidance,
        string? altText,
        string? quality,
        string? outputFormat,
        int? outputCompression)
    {
        if (imageWorkflow is null)
            return Serialize(new { ok = false, code = "IMAGE_RUNTIME_UNAVAILABLE", summary = "Image generation is unavailable." });
        try
        {
            var result = await imageWorkflow.GenerateAsync(
                context.ProjectId,
                brief,
                references,
                ToApplicationImageTarget(geometryGuidance),
                altText,
                quality,
                outputFormat,
                outputCompression,
                "Publish image",
                context.TrackImageJob,
                context.TurnCancellationToken);
            return ImageResult(context, result);
        }
        catch (MinimumDpiUnachievableException)
        {
            return Serialize(new { ok = false, code = "TARGET_PREPARATION_FAILED", summary = "Lorekeeper could not prepare an image for this target. Keep the image unattached and report the application error without proposing manual resolution workarounds." });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return IsLayoutBound(geometryGuidance)
                ? Serialize(new { ok = false, code = "TARGET_PREPARATION_FAILED", summary = "Lorekeeper could not prepare an image for this target. Keep the image unattached and report the application error without proposing manual resolution workarounds." })
                : Serialize(new { ok = false, code = "GENERATION_REJECTED", summary = ex.Message });
        }
    }

    private async Task<string> EditProjectImageAsync(
        PublishAssistantContext context,
        Guid sourceImageId,
        ImageEditBrief brief,
        ImageReferenceUse[]? references,
        PublishImageGenerationTarget? geometryGuidance,
        string? altText,
        string? quality,
        string? outputFormat,
        int? outputCompression)
    {
        if (imageWorkflow is null)
            return Serialize(new { ok = false, code = "IMAGE_RUNTIME_UNAVAILABLE", summary = "Image editing is unavailable." });
        try
        {
            var result = await imageWorkflow.EditAsync(
                context.ProjectId,
                sourceImageId,
                brief,
                null,
                references,
                ToApplicationImageTarget(geometryGuidance),
                altText,
                quality,
                outputFormat,
                outputCompression,
                "Publish image edit",
                context.TrackImageJob,
                context.TurnCancellationToken);
            return ImageResult(context, result);
        }
        catch (MinimumDpiUnachievableException)
        {
            return Serialize(new { ok = false, code = "TARGET_PREPARATION_FAILED", summary = "Lorekeeper could not prepare the edited image for this target. Keep the image unattached and report the application error without proposing manual resolution workarounds." });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return IsLayoutBound(geometryGuidance)
                ? Serialize(new { ok = false, code = "TARGET_PREPARATION_FAILED", summary = "Lorekeeper could not prepare the edited image for this target. Keep the image unattached and report the application error without proposing manual resolution workarounds." })
                : Serialize(new { ok = false, code = "EDIT_REJECTED", summary = ex.Message });
        }
    }

    private async Task<string> ReadProjectImageJobAsync(PublishAssistantContext context, Guid jobId, bool wait)
    {
        if (imageWorkflow is null)
            return Serialize(new { ok = false, code = "IMAGE_RUNTIME_UNAVAILABLE", summary = "Image generation is unavailable." });
        var result = wait
            ? await imageWorkflow.WaitAsync(context.ProjectId, jobId, context.TrackImageJob, context.TurnCancellationToken)
            : await imageWorkflow.ReadAsync(context.ProjectId, jobId, context.TurnCancellationToken);
        return result is null
            ? Serialize(new { ok = false, code = "NOT_FOUND", jobId, summary = "Image job was not found in this project." })
            : ImageResult(context, result);
    }

    private async Task<string> CancelProjectImageJobAsync(PublishAssistantContext context, Guid jobId)
    {
        if (imageWorkflow is null)
            return Serialize(new { ok = false, code = "IMAGE_RUNTIME_UNAVAILABLE", summary = "Image generation is unavailable." });
        await imageWorkflow.CancelAsync(context.ProjectId, jobId, context.TurnCancellationToken);
        return Serialize(new { ok = true, jobId, status = "cancelled", summary = "Image job cancelled; no image was placed." });
    }

    private static string ImageResult(PublishAssistantContext context, AgentProjectImageResult result)
    {
        foreach (var image in result.Images)
        {
            context.AddVisual(new EntityVisualContextReference(
                image.Id,
                null,
                "ProjectImage",
                image.FileName,
                "generated project image",
                0,
                image.FileName,
                image.AltText,
                image.Prompt,
                IsExplicitImage: true,
                ImageSource: image.Source));
        }
        var placementImageIds = result.Outputs
            .Select(output => output.PrintImageId ?? output.Image.Id)
            .ToList();
        return Serialize(new
        {
            ok = result.Succeeded,
            jobId = result.JobId,
            status = result.Succeeded ? "completed" : result.IsTerminal ? "failed" : result.Status,
            targetAspect = result.TargetAspect,
            outputImageIds = placementImageIds,
            images = result.Outputs.Select(output => new
            {
                imageId = output.PrintImageId ?? output.Image.Id,
                output.Image.FileName,
                output.Image.ContentType,
                output.Image.PreviewUrl,
            }),
            attached = false,
            diagnostics = result.Diagnostics.Take(3),
            summary = result.Succeeded
                ? $"Created {placementImageIds.Count} unattached project image(s) prepared for the target."
                : "Image generation did not produce a target-ready image.",
            nextAction = result.Succeeded
                ? "Inspect the image, then place its imageId with Cover (crop-to-fill) using the appropriate cover or publication-page tool before completing the request."
                : null,
        });
    }

    private static ImageGenerationTarget? ToApplicationImageTarget(PublishImageGenerationTarget? target)
    {
        if (target is null)
            return null;
        var layoutBound = target.TargetId is Guid targetId
            && targetId != Guid.Empty
            && !string.IsNullOrWhiteSpace(target.TargetKind);
        return new ImageGenerationTarget
        {
            EditionId = target.EditionId,
            TargetKind = target.TargetKind,
            TargetId = target.TargetId,
            VariantId = target.VariantId,
            AspectRatio = target.AspectRatio,
            SurfaceBounds = target.SurfaceBounds,
            UseApplicationResolutionPolicy = layoutBound,
            FillTarget = layoutBound,
        };
    }

    private static bool IsLayoutBound(PublishImageGenerationTarget? target) =>
        target?.TargetId is Guid targetId
        && targetId != Guid.Empty
        && !string.IsNullOrWhiteSpace(target.TargetKind);

    private string ReadPressRuntimeReadiness(PublicationEditionFormat format, PublicationVendor vendor)
    {
        var readiness = renders.GetRuntimeReadiness(format, vendor);
        PublicationPressDescription? description = readiness.IsReady
            ? renders.GetRuntimeDescription()
            : null;
        return Serialize(new { readiness, description });
    }

    private async Task<string> ListNamedStylesAsync(PublishAssistantContext context, int offset, int limit)
    {
        var all = await manuscriptStyles.ListAsync(context.ProjectId);
        var start = Math.Clamp(offset, 0, all.Count);
        var take = Math.Clamp(limit, 1, 60);
        var items = all.Skip(start).Take(take).ToList();
        return Serialize(new { ok = true, summary = $"{all.Count} Book Text Style(s).", items, continuation = Continuation(start, items.Count, all.Count) });
    }

    private async Task<string> ListBookFontsAsync(PublishAssistantContext context) =>
        Serialize(new
        {
            ok = true,
            targetId = context.ProjectId,
            fonts = (await projectFonts.ListAsync(context.ProjectId, context.TurnCancellationToken))
                .Select(family => new
                {
                    family.Key,
                    family.Name,
                    family.Category,
                    faces = family.Faces.Select(face => new { face.Weight, face.Italic }),
                }),
            summary = "Available book fonts for publication typography.",
        });

    private async Task<string> PatchPageSetupAsync(
        PublishAssistantContext context,
        long expectedRevision,
        double pageWidthInches,
        double pageHeightInches,
        double pageMarginInches,
        double bodyFontSizePoints,
        double bodyLineHeight)
    {
        try
        {
            var current = await books.GetOrCreateAsync(context.ProjectId, context.TurnCancellationToken);
            if (current.PageSetup.Revision != expectedRevision)
                throw new InvalidOperationException($"Page setup revision conflict: expected {expectedRevision}, current revision is {current.PageSetup.Revision}.");
            var changedFields = new List<string>();
            if (current.PageSetup.PageWidthInches != pageWidthInches) changedFields.Add("pageWidthInches");
            if (current.PageSetup.PageHeightInches != pageHeightInches) changedFields.Add("pageHeightInches");
            if (current.PageSetup.PageMarginInches != pageMarginInches) changedFields.Add("pageMarginInches");
            if (current.PageSetup.BodyFontSizePoints != bodyFontSizePoints) changedFields.Add("bodyFontSizePoints");
            if (current.PageSetup.BodyLineHeight != bodyLineHeight) changedFields.Add("bodyLineHeight");
            if (changedFields.Count == 0)
            {
                return Serialize(new
                {
                    ok = true,
                    target = "core-page-setup",
                    targetId = context.ProjectId,
                    revision = current.PageSetup.Revision,
                    changedFields,
                    summary = "Core Book page and baseline text defaults were already current.",
                });
            }
            var setup = await pageSetups.UpdateAsync(
                context.ProjectId,
                expectedRevision,
                new ProjectPageSetupInput(
                    pageWidthInches,
                    pageHeightInches,
                    pageMarginInches,
                    bodyFontSizePoints,
                    bodyLineHeight),
                context.TurnCancellationToken);
            var book = await books.GetOrCreateAsync(context.ProjectId, context.TurnCancellationToken);
            return Serialize(new
            {
                ok = true,
                target = "core-page-setup",
                targetId = context.ProjectId,
                revision = setup.Revision,
                coverRevision = book.CoverRevision,
                changedFields,
                summary = "Core Book page and baseline text defaults updated.",
                mutation = new
                {
                    kind = "core-book",
                    refresh = new[] { "core", "releases", "cover", "readiness", "artifacts" },
                },
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return Serialize(new
            {
                ok = false,
                code = exception.Message.Contains("revision conflict", StringComparison.OrdinalIgnoreCase)
                    ? "PAGE_SETUP_REVISION_CONFLICT"
                    : "PAGE_SETUP_REJECTED",
                targetId = context.ProjectId,
                summary = exception.Message,
                recovery = "Reread Core Book and retry with the current page-setup revision and complete unchanged values.",
            });
        }
    }

    private async Task<string> UpsertBookTextStyleAsync(
        PublishAssistantContext context,
        Guid? styleId,
        string name,
        string kind,
        long? expectedRevision,
        string? fontFamilyKey,
        double? fontSizePoints,
        int? fontWeight,
        bool? italic,
        bool? smallCaps,
        double? lineHeight,
        double? spaceBeforePoints,
        double? spaceAfterPoints,
        bool? keepWithNext,
        ParagraphAlignment? textAlign,
        double? leftIndentEm,
        double? rightIndentEm,
        double? firstLineIndentEm,
        bool? startOnNewPage)
    {
        try
        {
            if (!Enum.TryParse<ManuscriptStyleKind>(kind, ignoreCase: true, out var parsedKind))
                throw new InvalidOperationException("Style kind must be Paragraph or Character.");
            var current = styleId is Guid currentId
                ? (await manuscriptStyles.ListAsync(context.ProjectId, context.TurnCancellationToken))
                    .FirstOrDefault(style => style.Id == currentId)
                    ?? throw new InvalidOperationException("The Book Text Style was not found.")
                : null;
            var style = await manuscriptStyles.UpsertAsync(
                context.ProjectId,
                new ManuscriptStyleInput(
                    styleId,
                    name,
                    parsedKind,
                    current?.SemanticRole ?? ManuscriptStyleService.RoleFromName(name),
                    new ManuscriptStyleProperties(
                        fontFamilyKey,
                        fontSizePoints,
                        fontWeight,
                        italic,
                        smallCaps,
                        lineHeight,
                        spaceBeforePoints,
                        spaceAfterPoints,
                        keepWithNext,
                        PublicationStyleAlignment(textAlign),
                        leftIndentEm,
                        rightIndentEm,
                        firstLineIndentEm,
                        startOnNewPage),
                    expectedRevision),
                context.TurnCancellationToken);
            return Serialize(new
            {
                ok = true,
                target = "core-book-text-style",
                targetId = style.Id,
                revision = style.Revision,
                changedFields = new[] { "definition" },
                summary = $"Book Text Style '{style.Name}' saved.",
                mutation = new
                {
                    kind = "core-book",
                    refresh = new[] { "core", "releases", "readiness", "artifacts" },
                },
            });
        }
        catch (ManuscriptStyleConflictException exception)
        {
            return Serialize(new
            {
                ok = false,
                code = "STYLE_REVISION_CONFLICT",
                targetId = styleId,
                currentRevision = exception.ActualRevision,
                summary = exception.Message,
                recovery = "Reread the compact Book Text Style list and retry with the current revision.",
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return Serialize(new
            {
                ok = false,
                code = "STYLE_UPDATE_REJECTED",
                targetId = styleId,
                summary = exception.Message,
            });
        }
    }

    private async Task<string> DeleteBookTextStyleAsync(
        PublishAssistantContext context,
        Guid styleId,
        long expectedRevision)
    {
        try
        {
            await manuscriptStyles.DeleteAsync(
                context.ProjectId,
                styleId,
                expectedRevision,
                context.TurnCancellationToken);
            return Serialize(new
            {
                ok = true,
                target = "core-book-text-style",
                targetId = styleId,
                summary = "Book Text Style deleted.",
                mutation = new
                {
                    kind = "core-book",
                    refresh = new[] { "core", "releases", "readiness", "artifacts" },
                },
            });
        }
        catch (ManuscriptStyleConflictException exception)
        {
            return Serialize(new
            {
                ok = false,
                code = "STYLE_REVISION_CONFLICT",
                targetId = styleId,
                currentRevision = exception.ActualRevision,
                summary = exception.Message,
                recovery = "Reread the compact Book Text Style list and retry with the current revision.",
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
        {
            return Serialize(new
            {
                ok = false,
                code = "STYLE_DELETE_REJECTED",
                targetId = styleId,
                summary = exception.Message,
            });
        }
    }

    private static string? PublicationStyleAlignment(ParagraphAlignment? alignment) => alignment switch
    {
        ParagraphAlignment.Start => "left",
        ParagraphAlignment.Center => "center",
        ParagraphAlignment.End => "right",
        ParagraphAlignment.Justify => "justify",
        null => null,
        _ => throw new InvalidOperationException("Text alignment must be Start, Center, End, Justify, or omitted."),
    };

    private async Task<string> ListProjectImagesAsync(PublishAssistantContext context, int offset, int limit)
    {
        var all = await projectImages.ListAsync(context.ProjectId);
        var start = Math.Clamp(offset, 0, all.Count);
        var take = Math.Clamp(limit, 1, 60);
        var items = all.Skip(start).Take(take).Select(image => new
        {
            image.Id,
            image.FileName,
            image.ContentType,
            image.PreviewUrl,
            image.AltText,
            image.Source,
            image.SizeBytes,
            image.UpdatedAt,
        }).ToList();
        return Serialize(new { ok = true, summary = $"{all.Count} project image(s).", items, continuation = Continuation(start, items.Count, all.Count) });
    }

    private async Task<string> ListManuscriptVisualsAsync(PublishAssistantContext context, int offset, int limit)
    {
        var start = Math.Max(0, offset);
        var take = Math.Clamp(limit, 1, 80);
        IReadOnlyList<Chapter> chapters;
        await using (var operation = await database.OpenReadAsync(context.TurnCancellationToken))
        {
            chapters = await operation.Db.Chapters
                .Where(item => item.ProjectId == context.ProjectId)
                .OrderBy(item => item.Order)
                .Select(item => new Chapter
                {
                    Id = item.Id,
                    Title = item.Title,
                    ManuscriptJson = item.ManuscriptJson,
                })
                .ToListAsync(context.TurnCancellationToken);
        }
        var visuals = chapters.SelectMany(chapter => ManuscriptCodec.Deserialize(chapter.ManuscriptJson).Content
            .Where(block => block.Type is ManuscriptBlockType.Figure or ManuscriptBlockType.DesignedPage)
            .Select(block => new
            {
                chapterId = chapter.Id, chapter.Title, blockId = block.Id, type = block.Type.ToString(),
                block.ImageId, compositionId = block.PageCompositionId,
                accessibility = block.Type == ManuscriptBlockType.Figure
                    ? block.Decorative ? "decorative" : string.IsNullOrWhiteSpace(block.AltText) ? "missing-alt" : "described"
                    : "composition-reading-order",
            })).ToList();
        var items = visuals.Skip(start).Take(take).ToList();
        return Serialize(new { ok = true, summary = $"{visuals.Count} manuscript visual(s).", items, nextOffset = start + items.Count < visuals.Count ? start + items.Count : (int?)null });
    }

    private async Task<string> GetOrCreatePublicationSectionVariantAsync(
        PublishAssistantContext context,
        Guid sectionId,
        Guid compositionId)
    {
        try
        {
            var releaseId = context.SelectedEditionId;
            var visibleVariantId = context.WorkspaceContext is
                {
                    SectionId: var visibleSectionId,
                    CompositionId: var visibleCompositionId,
                    VariantId: Guid openVariantId,
                }
                && visibleSectionId == sectionId
                && visibleCompositionId == compositionId
                    ? openVariantId
                    : (Guid?)null;
            var target = new PublicationSectionTarget(context.ProjectId, releaseId);
            var section = await publicationSections.GetAsync(target, sectionId, context.TurnCancellationToken);
            var effectiveCompositionId = compositionId;
            var customized = false;
            if (releaseId is Guid selectedReleaseId && section.IsInherited)
            {
                section = await publicationSections.CustomizeAsync(
                    context.ProjectId,
                    selectedReleaseId,
                    section.Id,
                    context.TurnCancellationToken);
                customized = true;
                effectiveCompositionId = Guid.Empty;
                foreach (var candidateId in section.Manuscript.Content
                    .Where(block => block.PageCompositionId.HasValue)
                    .Select(block => block.PageCompositionId!.Value)
                    .Distinct())
                {
                    var candidate = await (compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable."))
                        .GetAsync(context.ProjectId, candidateId, context.TurnCancellationToken);
                    if (candidate?.SourceCompositionId == compositionId)
                    {
                        effectiveCompositionId = candidate.Id;
                        break;
                    }
                }
                if (effectiveCompositionId == Guid.Empty)
                    throw new KeyNotFoundException("The customized release section does not contain a page derived from the selected Core composition.");
            }
            else if (!section.Manuscript.Content.Any(block => block.PageCompositionId == compositionId))
            {
                throw new InvalidOperationException("The selected page composition is not part of this publication section.");
            }

            section = await publicationSections.EnsureSystemDesignedPageAsync(
                target,
                section.Id,
                context.TurnCancellationToken);

            var service = compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable.");
            var composition = await service.GetAsync(context.ProjectId, effectiveCompositionId, context.TurnCancellationToken)
                ?? throw new KeyNotFoundException("Publication-section page composition was not found.");
            if (composition.PublicationSectionId != section.Id || composition.EditionId != releaseId)
                throw new InvalidOperationException("The page composition does not belong to the active Publish target.");
            PageCompositionVariant? variant = null;
            if (visibleVariantId is Guid requestedVariantId)
            {
                var requested = await service.ReadVariantAsync(context.ProjectId, requestedVariantId, context.TurnCancellationToken);
                if (requested.CompositionId == composition.Id)
                {
                    variant = requested;
                }
                else if (customized && requested.CompositionId == compositionId)
                {
                    variant = (await service.ListVariantsAsync(context.ProjectId, composition.Id, context.TurnCancellationToken))
                        .FirstOrDefault(candidate => string.Equals(candidate.GeometryKey, requested.GeometryKey, StringComparison.Ordinal));
                }
            }
            variant ??= releaseId is Guid editionId
                ? await service.GetOrCreateVariantAsync(context.ProjectId, composition.Id, editionId, context.TurnCancellationToken)
                : await service.GetOrCreateAuthoringVariantAsync(context.ProjectId, composition.Id, context.TurnCancellationToken);
            composition = await service.GetAsync(context.ProjectId, composition.Id, context.TurnCancellationToken)
                ?? composition;
            return Serialize(new
            {
                ok = true,
                targetId = variant.Id,
                releaseId,
                sectionId = section.Id,
                sourceSectionId = section.CoreSectionId,
                compositionId = composition.Id,
                sourceCompositionId = composition.SourceCompositionId,
                compositionRevision = composition.Revision,
                variantId = variant.Id,
                variantRevision = variant.Revision,
                variant.GeometryKey,
                customized,
                summary = customized
                    ? "Materialized the inherited release section and resolved its editable page variant."
                    : "Resolved the editable publication-section page variant.",
                mutation = new { kind = "publication-section-page", releaseId, sectionId = section.Id, compositionId = composition.Id, variantId = variant.Id },
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or KeyNotFoundException)
        {
            return Serialize(new { ok = false, code = "INVALID_TARGET", targetId = compositionId, summary = ex.Message });
        }
    }

    private async Task<string> ReadPageCompositionAsync(PublishAssistantContext context, Guid compositionId, Guid variantId, int semanticStart, int semanticCount, int objectStart, int objectCount, int structureStart, int structureCount)
    {
        var service = compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable.");
        try
        {
            await RequirePublicationSectionVariantAsync(context, context.SelectedEditionId, variantId, compositionId);
            return await CompositionAgentPayloads.ReadVariantAsync(service, context.ProjectId, compositionId, variantId, semanticStart, semanticCount, objectStart, objectCount, structureStart, structureCount, context.TurnCancellationToken);
        }
        catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException) { return Serialize(new { ok = false, code = "NOT_FOUND", targetId = variantId, summary = ex.Message }); }
        catch (InvalidOperationException ex) { return Serialize(new { ok = false, code = "INVALID_TARGET", targetId = variantId, summary = ex.Message }); }
    }

    private async Task<string> PreviewPublicationSectionPageAsync(
        PublishAssistantContext context,
        Guid? releaseId,
        Guid compositionId,
        Guid variantId,
        string mode)
    {
        try
        {
            await RequirePublicationSectionVariantAsync(context, releaseId, variantId);
            var previewMode = mode.Equals("clean", StringComparison.OrdinalIgnoreCase)
                ? CompositionCanvasPreviewMode.Clean
                : mode.Equals("annotated", StringComparison.OrdinalIgnoreCase)
                    ? CompositionCanvasPreviewMode.Annotated
                    : throw new ArgumentException("Preview mode must be annotated or clean.", nameof(mode));
            var preview = await (canvasPreviews ?? throw new InvalidOperationException("Canvas previews are unavailable."))
                .RenderAsync(context.ProjectId, compositionId, variantId, previewMode, context.TurnCancellationToken);
            var visualId = Guid.NewGuid();
            context.AddTransientVisual(new(
                visualId,
                previewMode == CompositionCanvasPreviewMode.Annotated
                    ? "Publication page — annotated canvas"
                    : "Publication page — clean canvas",
                $"publication-section-page-{compositionId:N}-{previewMode.ToString().ToLowerInvariant()}.png",
                "image/png",
                preview.Data,
                $"{previewMode} publication-section page preview at composition revision {preview.CompositionRevision}, variant revision {preview.VariantRevision}",
                preview.PixelWidth,
                preview.PixelHeight,
                "publicationSectionCanvasPreview",
                compositionId));
            return Serialize(new
            {
                ok = true,
                targetId = compositionId,
                variantId,
                releaseId,
                revision = preview.CompositionRevision,
                variantRevision = preview.VariantRevision,
                visualId,
                surface = new { widthPoints = preview.SurfaceWidthPoints, heightPoints = preview.SurfaceHeightPoints },
                objectCounts = new { visible = preview.VisibleObjectCount, hidden = preview.HiddenObjectCount },
                diagnosticCounts = new { total = preview.Diagnostics.Count },
                diagnostics = preview.Diagnostics.Take(8),
                delivery = context.VisionReady
                    ? "The complete canvas image is visible in chat and attached as model visual context for the next reasoning iteration."
                    : "The complete canvas image is visible in chat, but the active provider is not vision-ready; do not claim visual verification.",
                summary = $"Rendered the complete {previewMode.ToString().ToLowerInvariant()} publication-section page canvas.",
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or KeyNotFoundException)
        {
            return Serialize(new { ok = false, code = "PREVIEW_REJECTED", targetId = compositionId, summary = ex.Message });
        }
    }

    private async Task<string> PatchPublicationSectionPageElementAsync(
        PublishAssistantContext context,
        Guid? releaseId,
        Guid variantId,
        long expectedRevision,
        string targetKind,
        Guid targetId,
        CompositionElementPatch patch)
    {
        try
        {
            EnsureActiveRelease(context, releaseId);
            return await PatchPublicationSectionPageElementCoreAsync(
                context, variantId, expectedRevision, targetKind, targetId, patch);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or KeyNotFoundException)
        {
            return Serialize(new { ok = false, code = "PATCH_REJECTED", targetId, summary = ex.Message });
        }
    }

    private async Task<string> FillPublicationSectionPageImageCanvasAsync(
        PublishAssistantContext context,
        Guid variantId,
        long expectedRevision,
        Guid targetId,
        bool retainAspectRatio)
    {
        try
        {
            var variant = await RequirePublicationSectionVariantAsync(context, context.SelectedEditionId, variantId);
            var scene = JsonSerializer.Deserialize<CompositionScene>(variant.SceneJson, ManuscriptCodec.JsonOptions)
                ?? throw new InvalidDataException("The composition scene is empty.");
            var item = scene.Objects.FirstOrDefault(candidate => candidate.Id == targetId)
                ?? throw new KeyNotFoundException("Composition object was not found.");
            var filled = CompositionImageLayout.FillCanvas(item, retainAspectRatio);
            return await PatchPublicationSectionPageElementCoreAsync(
                context,
                variantId,
                expectedRevision,
                "object",
                targetId,
                new CompositionElementPatch(Bounds: filled.Bounds, ImageFit: filled.ImageFit));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or KeyNotFoundException)
        {
            return Serialize(new { ok = false, code = "IMAGE_LAYOUT_REJECTED", targetId, summary = ex.Message });
        }
    }

    private async Task<string> PlacePublicationSectionPageImageAsync(
        PublishAssistantContext context,
        Guid variantId,
        long expectedRevision,
        Guid targetId,
        Guid imageId,
        FigureImageFit fit,
        string? altText,
        bool decorative,
        int? readingOrder)
    {
        if (!decorative && string.IsNullOrWhiteSpace(altText))
            return Serialize(new { ok = false, code = "ALT_DECISION_REQUIRED", targetId, summary = "Provide alternative text or explicitly mark the artwork decorative." });
        if (await projectImages.GetAsync(context.ProjectId, imageId, context.TurnCancellationToken) is null)
            return Serialize(new { ok = false, code = "IMAGE_NOT_FOUND", targetId, imageId, summary = "Project image was not found." });
        try
        {
            return await PatchPublicationSectionPageElementCoreAsync(
                context,
                variantId,
                expectedRevision,
                "object",
                targetId,
                new CompositionElementPatch(
                    ImageId: imageId,
                    ImageFit: fit,
                    AltText: decorative ? string.Empty : altText?.Trim(),
                    Decorative: decorative,
                    AccessibilityDecisionPending: false,
                    SemanticRole: decorative ? CompositionSemanticRole.Artifact : CompositionSemanticRole.Figure,
                    ReadingOrder: decorative ? null : readingOrder,
                    ClearReadingOrder: decorative));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or KeyNotFoundException)
        {
            return Serialize(new { ok = false, code = "PLACEMENT_REJECTED", targetId, summary = ex.Message, recovery = "Reread the page and retry with the same project-image ID." });
        }
    }

    private async Task<string> PatchPublicationSectionPageElementCoreAsync(
        PublishAssistantContext context,
        Guid variantId,
        long expectedRevision,
        string targetKind,
        Guid targetId,
        CompositionElementPatch patch)
    {
        try
        {
            var current = await RequirePublicationSectionVariantAsync(context, context.SelectedEditionId, variantId);
            var updated = await (compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable."))
                .PatchElementAsync(
                    SectionContentTarget(context.SelectedEditionId),
                    context.ProjectId,
                    variantId,
                    expectedRevision,
                    targetKind,
                    targetId,
                    patch,
                    context.TurnCancellationToken);
            return Serialize(new
            {
                ok = true,
                targetId,
                releaseId = context.SelectedEditionId,
                sectionId = current.Composition.PublicationSectionId,
                compositionId = updated.CompositionId,
                variantId = updated.Id,
                revision = updated.Revision,
                changedIds = new[] { targetId },
                selectId = targetId,
                summary = $"Patched publication-section page {targetKind} {targetId:N}.",
                mutation = new { kind = "publication-section-page", releaseId = context.SelectedEditionId, sectionId = current.Composition.PublicationSectionId, compositionId = updated.CompositionId, variantId = updated.Id, selectId = targetId },
            });
        }
        catch (CompositionRevisionConflictException ex)
        {
            return Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread the selected page variant, then retry only the intended fields." });
        }
    }

    private async Task<string> AddPublicationSectionPageImageAsync(
        PublishAssistantContext context,
        Guid? releaseId,
        Guid variantId,
        long expectedRevision,
        Guid imageId,
        FigureImageFit fit,
        string? altText,
        bool decorative,
        CompositionBounds? bounds,
        int? readingOrder)
    {
        try
        {
            await RequirePublicationSectionVariantAsync(context, releaseId, variantId);
            if (await projectImages.GetAsync(context.ProjectId, imageId, context.TurnCancellationToken) is null)
                throw new KeyNotFoundException("Project image was not found.");
            var result = await compositions!.AddImageObjectAsync(
                SectionContentTarget(releaseId), context.ProjectId, variantId, expectedRevision,
                imageId, fit, altText, decorative, bounds ?? new CompositionBounds(), readingOrder, context.TurnCancellationToken);
            return Serialize(new
            {
                ok = true,
                targetId = variantId,
                releaseId,
                sectionId = result.Variant.Composition.PublicationSectionId,
                compositionId = result.Variant.CompositionId,
                variantId = result.Variant.Id,
                revision = result.Variant.Revision,
                changedIds = new[] { result.ObjectId },
                selectId = result.ObjectId,
                summary = "Project image added to the publication-section page.",
                mutation = new { kind = "publication-section-page", releaseId, compositionId = result.Variant.CompositionId, variantId = result.Variant.Id, selectId = result.ObjectId },
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or KeyNotFoundException or CompositionRevisionConflictException)
        {
            return Serialize(new { ok = false, code = ex is CompositionRevisionConflictException ? "REVISION_CONFLICT" : "PLACEMENT_REJECTED", targetId = variantId, summary = ex.Message, recovery = "Reread the page and retry with the same project-image ID." });
        }
    }

    private async Task<string> StagePublicationSectionPageCompositionAsync(
        PublishAssistantContext context,
        Guid variantId,
        long expectedRevision,
        CompositionScene scene)
    {
        try
        {
            await RequirePublicationSectionVariantAsync(context, context.SelectedEditionId, variantId);
            var stage = await (compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable."))
                .StageVariantAsync(
                    SectionContentTarget(context.SelectedEditionId),
                    context.ProjectId,
                    context.ConversationId,
                    variantId,
                    expectedRevision,
                    scene,
                    context.TurnCancellationToken);
            return Serialize(new
            {
                ok = true,
                targetId = variantId,
                releaseId = context.SelectedEditionId,
                revision = expectedRevision,
                stageId = stage.Id,
                stage.ExpiresAt,
                summary = $"Validated {scene.Objects.Count} publication-section page object(s).",
                diagnosticCounts = new { errors = 0, warnings = 0 },
            });
        }
        catch (CompositionRevisionConflictException ex)
        {
            return Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = variantId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread the page and stage one replacement scene." });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or KeyNotFoundException)
        {
            return Serialize(new { ok = false, code = "SCENE_STAGE_REJECTED", targetId = variantId, summary = ex.Message });
        }
    }

    private async Task<string> ApplyPublicationSectionPageCompositionStageAsync(
        PublishAssistantContext context,
        Guid stageId,
        long expectedRevision)
    {
        try
        {
            var variant = await (compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable."))
                .ApplyStageAsync(
                    SectionContentTarget(context.SelectedEditionId),
                    context.ProjectId,
                    context.ConversationId,
                    stageId,
                    expectedRevision,
                    context.TurnCancellationToken);
            if (variant.Composition.PublicationSectionId is not Guid sectionId
                || variant.Composition.EditionId != context.SelectedEditionId)
                throw new InvalidOperationException("The staged page does not belong to the active Publish target.");
            return Serialize(new
            {
                ok = true,
                targetId = variant.Id,
                releaseId = context.SelectedEditionId,
                sectionId,
                compositionId = variant.CompositionId,
                variantId = variant.Id,
                revision = variant.Revision,
                summary = "Staged publication-section page scene applied.",
                mutation = new { kind = "publication-section-page", releaseId = context.SelectedEditionId, sectionId, compositionId = variant.CompositionId, variantId = variant.Id },
            });
        }
        catch (CompositionRevisionConflictException ex)
        {
            return Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = stageId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread and submit a new non-replayed stage." });
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or KeyNotFoundException)
        {
            return Serialize(new { ok = false, code = "STAGE_REJECTED", targetId = stageId, summary = ex.Message });
        }
    }

    private async Task<string> StagePublicationSectionPageSemanticAsync(
        PublishAssistantContext context,
        Guid compositionId,
        long expectedRevision,
        ManuscriptOperationInput[] operations)
    {
        try
        {
            var composition = await RequirePublicationSectionCompositionAsync(context, compositionId);
            var section = await publicationSections.GetAsync(
                new(context.ProjectId, context.SelectedEditionId),
                composition.PublicationSectionId!.Value,
                context.TurnCancellationToken);
            if (section.SystemRole is not PublicationSectionSystemRole.None)
                throw new InvalidOperationException("Linked system copy is edited through Core or release Book details; page tools may change only its placement and typography.");
            var stage = await (compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable."))
                .StageSemanticOperationsAsync(
                    SectionContentTarget(context.SelectedEditionId),
                    context.ProjectId,
                    context.ConversationId,
                    compositionId,
                    expectedRevision,
                    operations,
                    context.TurnCancellationToken);
            return Serialize(new { ok = true, targetId = compositionId, releaseId = context.SelectedEditionId, revision = expectedRevision, stageId = stage.Id, stage.ExpiresAt, summary = $"Validated {operations.Length} semantic operation(s)." });
        }
        catch (CompositionRevisionConflictException ex)
        {
            return Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = compositionId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread the page and submit a replacement semantic stage." });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or KeyNotFoundException)
        {
            return Serialize(new { ok = false, code = "SEMANTIC_STAGE_REJECTED", targetId = compositionId, summary = ex.Message });
        }
    }

    private async Task<string> ApplyPublicationSectionPageSemanticStageAsync(
        PublishAssistantContext context,
        Guid stageId,
        long expectedRevision)
    {
        try
        {
            var result = await (compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable."))
                .ApplySemanticStageAsync(
                    SectionContentTarget(context.SelectedEditionId),
                    context.ProjectId,
                    context.ConversationId,
                    stageId,
                    expectedRevision,
                    context.TurnCancellationToken);
            if (result.Composition.PublicationSectionId is not Guid sectionId
                || result.Composition.EditionId != context.SelectedEditionId)
                throw new InvalidOperationException("The staged content does not belong to the active Publish target.");
            return Serialize(new
            {
                ok = true,
                targetId = result.Composition.Id,
                releaseId = context.SelectedEditionId,
                sectionId,
                compositionId = result.Composition.Id,
                revision = result.Composition.Revision,
                changedIds = result.ChangedBlockIds,
                summary = "Staged publication-section page content applied.",
                mutation = new { kind = "publication-section-page", releaseId = context.SelectedEditionId, sectionId, compositionId = result.Composition.Id },
            });
        }
        catch (CompositionRevisionConflictException ex)
        {
            return Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = stageId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread and submit a new non-replayed semantic stage." });
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or KeyNotFoundException)
        {
            return Serialize(new { ok = false, code = "STAGE_REJECTED", targetId = stageId, summary = ex.Message });
        }
    }

    private async Task<string> StagePublicationSectionPageWorkspaceAsync(
        PublishAssistantContext context,
        Guid? releaseId,
        Guid compositionId,
        long expectedCompositionRevision,
        Guid variantId,
        long expectedVariantRevision,
        ManuscriptOperationInput[] semanticOperations,
        CompositionScene scene)
    {
        try
        {
            var variant = await RequirePublicationSectionVariantAsync(context, releaseId, variantId, compositionId);
            var section = await publicationSections.GetAsync(
                new(context.ProjectId, releaseId),
                variant.Composition.PublicationSectionId!.Value,
                context.TurnCancellationToken);
            if (section.SystemRole is not PublicationSectionSystemRole.None && semanticOperations.Length > 0)
                throw new InvalidOperationException("Linked system copy is edited through Core or release Book details; stage this page's scene without semantic operations.");
            var stage = await compositions!.StageWorkspaceAsync(
                SectionContentTarget(releaseId), context.ProjectId, context.ConversationId,
                compositionId, expectedCompositionRevision, variantId, expectedVariantRevision,
                semanticOperations, scene, context.TurnCancellationToken);
            return Serialize(new { ok = true, targetId = compositionId, releaseId, revision = expectedCompositionRevision, stageId = stage.Id, stage.ExpiresAt, summary = $"Validated {semanticOperations.Length} semantic operation(s) and {scene.Objects.Count} page object(s)." });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or KeyNotFoundException or CompositionRevisionConflictException)
        {
            return Serialize(new { ok = false, code = ex is CompositionRevisionConflictException ? "REVISION_CONFLICT" : "WORKSPACE_STAGE_REJECTED", targetId = compositionId, summary = ex.Message, recovery = "Reread the bounded page workspace and submit one replacement stage." });
        }
    }

    private async Task<string> ApplyPublicationSectionPageWorkspaceStageAsync(
        PublishAssistantContext context,
        Guid? releaseId,
        Guid stageId,
        long expectedCompositionRevision)
    {
        try
        {
            EnsureActiveRelease(context, releaseId);
            var result = await (compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable."))
                .ApplyWorkspaceStageAsync(SectionContentTarget(releaseId), context.ProjectId, context.ConversationId, stageId, expectedCompositionRevision, context.TurnCancellationToken);
            if (result.Composition.PublicationSectionId is null)
                throw new InvalidOperationException("The staged page does not belong to a publication section.");
            return Serialize(new
            {
                ok = true,
                targetId = result.Composition.Id,
                releaseId,
                sectionId = result.Composition.PublicationSectionId,
                compositionId = result.Composition.Id,
                revision = result.Composition.Revision,
                variantId = result.Variant.Id,
                variantRevision = result.Variant.Revision,
                changedIds = result.ChangedBlockIds,
                summary = "Publication-section page content and layout applied atomically.",
                mutation = new { kind = "publication-section-page", releaseId, sectionId = result.Composition.PublicationSectionId, compositionId = result.Composition.Id, variantId = result.Variant.Id },
            });
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or KeyNotFoundException or CompositionRevisionConflictException)
        {
            return Serialize(new { ok = false, code = ex is CompositionRevisionConflictException ? "REVISION_CONFLICT" : "STAGE_REJECTED", targetId = stageId, summary = ex.Message, recovery = "Reread and stage a replacement page workspace; stages are not rebased." });
        }
    }

    private async Task<PageCompositionVariant> RequirePublicationSectionVariantAsync(
        PublishAssistantContext context,
        Guid? releaseId,
        Guid variantId,
        Guid? compositionId = null)
    {
        EnsureActiveRelease(context, releaseId);
        var service = compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable.");
        var variant = await service.ReadVariantAsync(context.ProjectId, variantId, context.TurnCancellationToken);
        if (compositionId is Guid expectedCompositionId && variant.CompositionId != expectedCompositionId)
            throw new InvalidOperationException("The selected variant does not belong to that publication-section page.");
        if (variant.Composition.PublicationSectionId is null || variant.Composition.EditionId != releaseId)
            throw new InvalidOperationException("The page does not belong to the selected Core or release publication section. Customize an inherited release section before changing its page.");
        _ = await publicationSections.EnsureSystemDesignedPageAsync(
            new PublicationSectionTarget(context.ProjectId, releaseId),
            variant.Composition.PublicationSectionId.Value,
            context.TurnCancellationToken);
        variant = await service.ReadVariantAsync(context.ProjectId, variantId, context.TurnCancellationToken);
        return variant;
    }

    private async Task<PageComposition> RequirePublicationSectionCompositionAsync(
        PublishAssistantContext context,
        Guid compositionId)
    {
        var composition = await (compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable."))
            .GetAsync(context.ProjectId, compositionId, context.TurnCancellationToken)
            ?? throw new KeyNotFoundException("Publication-section page composition was not found.");
        if (composition.PublicationSectionId is null || composition.EditionId != context.SelectedEditionId)
            throw new InvalidOperationException("The page composition does not belong to the active Publish target.");
        return composition;
    }

    private static void EnsureActiveRelease(PublishAssistantContext context, Guid? releaseId)
    {
        if (context.SelectedEditionId != releaseId)
            throw new InvalidOperationException("The requested page target does not match the release selected in the Publish workspace.");
    }

    private async Task EnsurePrintCoverPaginationAsync(
        PublishAssistantContext context,
        Guid editionId)
    {
        PublicationEditionFormat? format;
        await using (var operation = await database.OpenReadAsync(context.TurnCancellationToken))
        {
            format = await operation.Db.PublicationEditions.AsNoTracking()
                .Where(item => item.ProjectId == context.ProjectId && item.Id == editionId)
                .Select(item => (PublicationEditionFormat?)item.Format)
                .SingleOrDefaultAsync(context.TurnCancellationToken);
        }
        if (format is null)
            throw new KeyNotFoundException("Publication release not found.");
        if (format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover)
            await pagination.EnsureCurrentAsync(context.ProjectId, editionId, context.TurnCancellationToken);
    }

    private static EditorContentTarget SectionContentTarget(Guid? releaseId) =>
        releaseId is Guid id ? EditorContentTarget.ForEdition(id) : EditorContentTarget.Core;

    private async Task<string> ReadLayoutGenerationTargetAsync(
        PublishAssistantContext context,
        string targetKind,
        Guid targetId,
        Guid? variantId,
        Guid? editionId,
        string? surfaceRole,
        string? regionRole,
        CompositionBounds? surfaceBounds)
    {
        try
        {
            if (targetKind.Trim().Equals("CoverRegion", StringComparison.OrdinalIgnoreCase))
            {
                if (surfaceBounds is not null)
                    throw new ArgumentException("surfaceBounds applies only to server-owned CoverSurface, PageSurface, or CoreCoverSurface targets.");
                if (editionId is not Guid regionReleaseId)
                    throw new ArgumentException("CoverRegion requires releaseId.");
                await EnsurePrintCoverPaginationAsync(context, regionReleaseId);
                var role = ParseCoverRegion(regionRole ?? string.Empty);
                var cover = string.IsNullOrWhiteSpace(surfaceRole)
                    ? await covers.GetAsync(context.ProjectId, regionReleaseId, context.TurnCancellationToken)
                    : await covers.GetSurfaceAsync(context.ProjectId, regionReleaseId, surfaceRole, context.TurnCancellationToken);
                var region = cover.Regions.Single(item => item.Role == role);
                return Serialize(new
                {
                    ok = true,
                    descriptor = new
                    {
                        releaseId = regionReleaseId,
                        surfaceRole,
                        targetKind = "cover-region",
                        targetId,
                        regionRole = role,
                        aspectRatio = region.AspectRatio,
                        generationTarget = new
                        {
                            editionId = regionReleaseId,
                            targetKind = "CoverSurface",
                            targetId = cover.Id,
                            surfaceBounds = region.Bounds,
                        },
                        protectedRegions = region.Guides,
                        orientation = role == CompositionRegionConstraint.Spine ? cover.SpineReadingDirection.ToString() : "upright",
                        geometryFingerprint = region.GeometryFingerprint,
                        placement = "Generate edge-to-edge, then place with Cover so the image crop-fills this region without stretching.",
                    },
                });
            }
            var service = compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable.");
            if (surfaceBounds is not null
                && !targetKind.Trim().Equals("PageSurface", StringComparison.OrdinalIgnoreCase)
                && !targetKind.Trim().Equals("CoverSurface", StringComparison.OrdinalIgnoreCase)
                && !targetKind.Trim().Equals("CoreCoverSurface", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("surfaceBounds requires a PageSurface, CoverSurface, or CoreCoverSurface target.");
            var descriptor = editionId is Guid releaseId
                ? await service.DescribeGenerationTargetAsync(context.ProjectId, releaseId, targetKind, targetId, variantId, context.TurnCancellationToken, surfaceBounds)
                : await service.DescribeAuthoringGenerationTargetAsync(context.ProjectId, targetKind, targetId, variantId, context.TurnCancellationToken, surfaceBounds);
            return Serialize(new
            {
                ok = true,
                descriptor = new
                {
                    releaseId = descriptor.EditionId,
                    descriptor.VariantId,
                    descriptor.GeometryKey,
                    descriptor.GeometrySource,
                    descriptor.TargetKind,
                    descriptor.TargetId,
                    descriptor.AspectRatio,
                    protectedRegions = descriptor.Regions,
                    generationTarget = new
                    {
                        editionId = descriptor.EditionId,
                        targetKind = descriptor.TargetKind,
                        targetId = descriptor.TargetId,
                        variantId = descriptor.VariantId,
                        surfaceBounds = descriptor.SurfaceBounds,
                    },
                    placement = "Generate edge-to-edge, then place with Cover so the image crop-fills the complete target without stretching.",
                },
            });
        }
        catch (Exception ex) { return Serialize(new { ok = false, code = "INVALID_TARGET", targetId, summary = ex.Message }); }
    }

    private async Task<string> ValidateCompositionAsync(PublishAssistantContext context, Guid? releaseId, Guid variantId)
    {
        try
        {
            EnsureActiveRelease(context, releaseId);
            await RequirePublicationSectionVariantAsync(context, releaseId, variantId);
            var service = compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable.");
            var result = releaseId is Guid editionId
                ? await service.ValidateVariantAsync(context.ProjectId, editionId, variantId, context.TurnCancellationToken)
                : await service.ValidateAuthoringVariantAsync(context.ProjectId, variantId, context.TurnCancellationToken);
            return Serialize(new { ok = result.ErrorCount == 0, targetId = result.TargetId, revision = result.Revision, summary = $"Validation found {result.ErrorCount} error(s) and {result.WarningCount} warning(s).", diagnosticCounts = new { errors = result.ErrorCount, warnings = result.WarningCount }, diagnostics = result.Diagnostics });
        }
        catch (Exception ex) { return Serialize(new { ok = false, code = "VALIDATION_FAILED", targetId = variantId, summary = ex.Message }); }
    }

    private async Task<string> CreateReleaseCoreAsync(
        PublishAssistantContext context,
        string name,
        PublicationEditionFormat format,
        PublicationVendor vendor) =>
        Serialize(await editions.CreateAsync(context.ProjectId, new(name, format, vendor)));

    private async Task<string> SetContentAsync(
        PublishAssistantContext context,
        Guid editionId,
        PublicationEditionOutlineItemUpdate[] updates,
        long expectedRevision) =>
        Serialize(await editions.SetOutlineSelectionsAsync(context.ProjectId, editionId, updates, expectedRevision));

    private async Task<string> ReadCoverAsync(
        PublishAssistantContext context,
        Guid? releaseId,
        string? surfaceRole,
        int objectStart,
        int objectCount,
        int structureStart,
        int structureCount)
    {
        if (releaseId is Guid paginatedReleaseId)
            await EnsurePrintCoverPaginationAsync(context, paginatedReleaseId);
        var cover = releaseId is Guid editionId
            ? string.IsNullOrWhiteSpace(surfaceRole)
                ? await covers.GetAsync(context.ProjectId, editionId)
                : await covers.GetSurfaceAsync(context.ProjectId, editionId, surfaceRole)
            : await books.GetCoverAsync(context.ProjectId);
        var scene = JsonSerializer.Deserialize<CompositionScene>(cover.CompositionSceneJson, ManuscriptCodec.JsonOptions)
            ?? new CompositionScene();
        var start = Math.Max(0, objectStart);
        var take = Math.Clamp(objectCount, 1, 50);
        structureStart = Math.Max(0, structureStart);
        structureCount = Math.Clamp(structureCount, 1, 50);
        var objects = scene.Objects.Skip(start).Take(take).ToList();
        return Serialize(new
        {
            ok = true,
            target = releaseId is null ? "core" : "release",
            targetId = releaseId ?? context.ProjectId,
            revision = cover.Revision,
            surfaceRole = cover.SurfaceRole,
            cover.Title,
            cover.Subtitle,
            cover.Author,
            cover.SpineText,
            cover.BackCopy,
            cover.BackgroundColor,
            cover.BarcodeMode,
            cover.Template,
            cover.Diagnostics,
            bindableTextTokens = CoverTextTokens.Definitions.Select(item => new { item.Token, item.Label }),
            scene.SchemaVersion,
            scene.Surface,
            layers = scene.Layers.Skip(structureStart).Take(structureCount),
            styles = scene.Styles.Skip(structureStart).Take(structureCount),
            guides = scene.Guides.Skip(structureStart).Take(structureCount),
            objects,
            continuation = new { objects = new { start, returned = objects.Count, total = scene.Objects.Count, hasMore = start + objects.Count < scene.Objects.Count, nextObjectStart = start + objects.Count < scene.Objects.Count ? start + objects.Count : (int?)null }, structure = new { start = structureStart, count = structureCount, layerTotal = scene.Layers.Count, styleTotal = scene.Styles.Count, guideTotal = scene.Guides.Count } },
        });
    }

    private async Task<string> PreviewCoverCanvasAsync(
        PublishAssistantContext context,
        Guid? releaseId,
        string? surfaceRole,
        string mode)
    {
        if (canvasPreviews is null)
            return Serialize(new { ok = false, code = "CANVAS_PREVIEW_UNAVAILABLE", summary = "Cover canvas preview is unavailable." });
        var previewMode = (mode ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "annotated" => CompositionCanvasPreviewMode.Annotated,
            "clean" => CompositionCanvasPreviewMode.Clean,
            _ => (CompositionCanvasPreviewMode?)null,
        };
        if (previewMode is null)
            return Serialize(new { ok = false, code = "CANVAS_PREVIEW_MODE_INVALID", summary = "mode must be annotated or clean." });

        try
        {
            if (releaseId is Guid paginatedReleaseId)
                await EnsurePrintCoverPaginationAsync(context, paginatedReleaseId);
            var cover = releaseId is Guid editionId
                ? string.IsNullOrWhiteSpace(surfaceRole)
                    ? await covers.GetAsync(context.ProjectId, editionId, context.TurnCancellationToken)
                    : await covers.GetSurfaceAsync(context.ProjectId, editionId, surfaceRole, context.TurnCancellationToken)
                : await books.GetCoverAsync(context.ProjectId, context.TurnCancellationToken);
            var scene = JsonSerializer.Deserialize<CompositionScene>(cover.CompositionSceneJson, ManuscriptCodec.JsonOptions)
                ?? throw new InvalidDataException("The cover composition is empty.");
            var targetId = releaseId ?? context.ProjectId;
            var preview = await canvasPreviews.RenderSceneAsync(
                context.ProjectId,
                targetId,
                cover.Revision,
                scene,
                previewMode.Value,
                context.TurnCancellationToken,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["title"] = cover.Title,
                    ["subtitle"] = cover.Subtitle,
                    ["author"] = cover.Author,
                    ["spineText"] = cover.SpineText,
                    ["backCopy"] = cover.BackCopy,
                });
            var visualId = Guid.NewGuid();
            var fileName = $"cover-{targetId:N}-{previewMode.Value.ToString().ToLowerInvariant()}.png";
            context.AddTransientVisual(new(
                visualId,
                previewMode == CompositionCanvasPreviewMode.Annotated
                    ? "Publication cover — annotated canvas"
                    : "Publication cover — clean canvas",
                fileName,
                "image/png",
                preview.Data,
                previewMode == CompositionCanvasPreviewMode.Annotated
                    ? "Annotated direct cover-canvas preview."
                    : "Clean direct cover-canvas preview.",
                preview.PixelWidth,
                preview.PixelHeight,
                "publicationCoverCanvasPreview",
                targetId));
            var diagnostics = preview.Diagnostics.Take(10).ToList();
            var allCoverDiagnostics = StructuredCoverDiagnostics(cover);
            var coverDiagnostics = allCoverDiagnostics.Take(10).ToList();
            return Serialize(new
            {
                ok = true,
                target = releaseId is null ? "core" : "release",
                targetId,
                currentRevision = cover.Revision,
                mode = preview.Mode.ToString().ToLowerInvariant(),
                visualId,
                surface = new
                {
                    widthPoints = preview.SurfaceWidthPoints,
                    heightPoints = preview.SurfaceHeightPoints,
                    widthInches = Math.Round(preview.SurfaceWidthPoints / 72, 4),
                    heightInches = Math.Round(preview.SurfaceHeightPoints / 72, 4),
                    widthPixels = preview.PixelWidth,
                    heightPixels = preview.PixelHeight,
                },
                objects = new { visible = preview.VisibleObjectCount, hidden = preview.HiddenObjectCount },
                diagnosticCounts = new
                {
                    total = preview.Diagnostics.Count + allCoverDiagnostics.Count,
                    errors = preview.Diagnostics.Count(item => item.Severity == "error")
                        + allCoverDiagnostics.Count(item => item.Severity == "error"),
                    warnings = preview.Diagnostics.Count(item => item.Severity == "warning")
                        + allCoverDiagnostics.Count(item => item.Severity == "warning"),
                },
                diagnostics,
                hasMoreDiagnostics = preview.Diagnostics.Count > diagnostics.Count,
                coverDiagnostics,
                hasMoreCoverDiagnostics = allCoverDiagnostics.Count > coverDiagnostics.Count,
                delivery = context.VisionReady
                    ? "The complete cover image is visible in chat and attached as model visual context for the next reasoning iteration."
                    : "The complete cover image is visible in chat, but the active provider is not vision-ready; do not claim visual verification.",
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Serialize(new
            {
                ok = false,
                code = "COVER_CANVAS_PREVIEW_FAILED",
                targetId = releaseId ?? context.ProjectId,
                summary = exception.Message,
                recovery = "Reread the current cover, correct missing image or font data, and retry.",
            });
        }
    }

    private async Task<string> ReadCoverRegionAsync(
        PublishAssistantContext context,
        Guid releaseId,
        string regionRole,
        string? surfaceRole)
    {
        try
        {
            await EnsurePrintCoverPaginationAsync(context, releaseId);
            var role = ParseCoverRegion(regionRole);
            var cover = string.IsNullOrWhiteSpace(surfaceRole)
                ? await covers.GetAsync(context.ProjectId, releaseId, context.TurnCancellationToken)
                : await covers.GetSurfaceAsync(context.ProjectId, releaseId, surfaceRole, context.TurnCancellationToken);
            var region = cover.Regions.Single(item => item.Role == role);
            return Serialize(new
            {
                ok = true,
                targetId = releaseId,
                surfaceRole,
                revision = cover.Revision,
                region,
                spineReadingDirection = cover.SpineReadingDirection,
                generationGuidance = role == CompositionRegionConstraint.Spine
                    ? "Read the region generation target, compose edge-to-edge without baked-in words, preserve a quiet center lane for real title and author text, then crop-to-fill the region and inspect both the annotated region and whole wrap."
                    : "Compose edge-to-edge without baked-in copy, preserve quiet zones for real cover typography, and crop-to-fill the region.",
            });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Serialize(new { ok = false, code = "COVER_REGION_INVALID", targetId = releaseId, surfaceRole, summary = exception.Message });
        }
    }

    private async Task<string> PreviewCoverRegionAsync(
        PublishAssistantContext context,
        Guid releaseId,
        string regionRole,
        string? surfaceRole,
        string mode)
    {
        if (canvasPreviews is null)
            return Serialize(new { ok = false, code = "CANVAS_PREVIEW_UNAVAILABLE", summary = "Cover canvas preview is unavailable." });
        try
        {
            await EnsurePrintCoverPaginationAsync(context, releaseId);
            var role = ParseCoverRegion(regionRole);
            var previewMode = mode.Trim().ToLowerInvariant() switch
            {
                "annotated" => CompositionCanvasPreviewMode.Annotated,
                "clean" => CompositionCanvasPreviewMode.Clean,
                _ => throw new ArgumentException("mode must be annotated or clean."),
            };
            var cover = string.IsNullOrWhiteSpace(surfaceRole)
                ? await covers.GetAsync(context.ProjectId, releaseId, context.TurnCancellationToken)
                : await covers.GetSurfaceAsync(context.ProjectId, releaseId, surfaceRole, context.TurnCancellationToken);
            var region = cover.Regions.Single(item => item.Role == role);
            var scene = JsonSerializer.Deserialize<CompositionScene>(cover.CompositionSceneJson, ManuscriptCodec.JsonOptions)
                ?? throw new InvalidDataException("The cover composition is empty.");
            var preview = await canvasPreviews.RenderSceneAsync(
                context.ProjectId,
                releaseId,
                cover.Revision,
                scene,
                previewMode,
                context.TurnCancellationToken,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["title"] = cover.Title,
                    ["subtitle"] = cover.Subtitle,
                    ["author"] = cover.Author,
                    ["spineText"] = cover.SpineText,
                    ["backCopy"] = cover.BackCopy,
                });
            using var source = SKBitmap.Decode(preview.Data) ?? throw new InvalidDataException("The cover preview could not be decoded.");
            var bounds = region.Bounds;
            var left = Math.Clamp((int)Math.Floor(bounds.XPercent / 100 * source.Width), 0, source.Width - 1);
            var top = Math.Clamp((int)Math.Floor(bounds.YPercent / 100 * source.Height), 0, source.Height - 1);
            var width = Math.Clamp((int)Math.Ceiling(bounds.WidthPercent / 100 * source.Width), 1, source.Width - left);
            var height = Math.Clamp((int)Math.Ceiling(bounds.HeightPercent / 100 * source.Height), 1, source.Height - top);
            using var cropped = new SKBitmap(width, height);
            using (var canvas = new SKCanvas(cropped))
                canvas.DrawBitmap(source, new SKRectI(left, top, left + width, top + height), new SKRect(0, 0, width, height));
            using var image = SKImage.FromBitmap(cropped);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            var data = encoded.ToArray();
            var visualId = Guid.NewGuid();
            context.AddTransientVisual(new(
                visualId,
                $"Publication cover — {role} ({previewMode.ToString().ToLowerInvariant()})",
                $"cover-{releaseId:N}-{role.ToString().ToLowerInvariant()}-{previewMode.ToString().ToLowerInvariant()}.png",
                "image/png",
                data,
                $"{previewMode} rendered inspection of the exact {role} region.",
                width,
                height,
                "publicationCoverRegionPreview",
                releaseId));
            return Serialize(new { ok = true, targetId = releaseId, visualId, surfaceRole, revision = cover.Revision, region, pixelWidth = width, pixelHeight = height, diagnostics = preview.Diagnostics.Take(10), inspectWholeWrapNext = true });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException)
        {
            return Serialize(new { ok = false, code = "COVER_REGION_PREVIEW_FAILED", targetId = releaseId, surfaceRole, summary = exception.Message });
        }
    }

    private async Task<string> FillCoverRegionAsync(
        PublishAssistantContext context,
        Guid releaseId,
        long expectedRevision,
        Guid imageId,
        string regionRole,
        string? altText,
        bool decorative,
        string? surfaceRole)
    {
        try
        {
            await EnsurePrintCoverPaginationAsync(context, releaseId);
            if (!decorative && string.IsNullOrWhiteSpace(altText))
                throw new ArgumentException("Provide alternative text or explicitly mark the artwork decorative.");
            if (await projectImages.GetAsync(context.ProjectId, imageId, context.TurnCancellationToken) is null)
                throw new KeyNotFoundException("Project image was not found.");
            var role = ParseCoverRegion(regionRole);
            var cover = string.IsNullOrWhiteSpace(surfaceRole)
                ? await covers.GetAsync(context.ProjectId, releaseId, context.TurnCancellationToken)
                : await covers.GetSurfaceAsync(context.ProjectId, releaseId, surfaceRole, context.TurnCancellationToken);
            if (cover.Revision != expectedRevision)
                throw new DbUpdateConcurrencyException("The cover changed; reread it before retrying.");
            var region = cover.Regions.Single(item => item.Role == role);
            var scene = JsonSerializer.Deserialize<CompositionScene>(cover.CompositionSceneJson, ManuscriptCodec.JsonOptions)
                ?? throw new InvalidDataException("The cover composition is empty.");
            var mutation = CompositionService.AddImageObjectToScene(
                scene,
                imageId,
                FigureImageFit.Cover,
                altText,
                decorative,
                region.Bounds,
                decorative ? null : scene.Objects.Where(item => item.ReadingOrder is not null).Select(item => item.ReadingOrder!.Value).DefaultIfEmpty().Max() + 1);
            var filledScene = mutation.Scene with
            {
                Objects = mutation.Scene.Objects.Select(item => item.Id == mutation.ObjectId
                    ? CompositionImageLayout.FillRegion(item, role, region.Bounds)
                    : item).ToList(),
            };
            var update = new PublicationCoverDesignUpdate(
                cover.Title, cover.Subtitle, cover.Author, cover.SpineText, cover.BackCopy,
                cover.BackgroundColor, cover.BarcodeMode, cover.ImageCropXPercent, cover.ImageCropYPercent,
                expectedRevision, true, cover.SpineReadingDirection);
            var saved = string.IsNullOrWhiteSpace(surfaceRole)
                ? await covers.SaveWorkspaceAsync(context.ProjectId, releaseId, update, filledScene, context.TurnCancellationToken)
                : await covers.SaveSurfaceWorkspaceAsync(context.ProjectId, releaseId, surfaceRole, update, filledScene, context.TurnCancellationToken);
            return Serialize(new { ok = true, targetId = releaseId, surfaceRole, revision = saved.Revision, regionRole = role, changedIds = new[] { mutation.ObjectId }, selectId = mutation.ObjectId, fit = "crop-to-fill", stretched = false, summary = $"Project image filled only the {role} region. Inspect the annotated region and full wrap next." });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException or KeyNotFoundException or DbUpdateConcurrencyException)
        {
            return Serialize(new { ok = false, code = exception is DbUpdateConcurrencyException ? "REVISION_CONFLICT" : "REGION_FILL_REJECTED", targetId = releaseId, surfaceRole, imageId, summary = exception.Message });
        }
    }

    private async Task<string> SetCoverSpineDirectionAsync(
        PublishAssistantContext context,
        Guid releaseId,
        long expectedRevision,
        SpineReadingDirection direction)
    {
        await EnsurePrintCoverPaginationAsync(context, releaseId);
        var cover = await covers.GetAsync(context.ProjectId, releaseId, context.TurnCancellationToken);
        if (cover.Revision != expectedRevision)
            return Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = releaseId, summary = "The cover changed; reread it before retrying." });
        var saved = await covers.UpdateAsync(
            context.ProjectId,
            releaseId,
            new PublicationCoverDesignUpdate(
                cover.Title, cover.Subtitle, cover.Author, cover.SpineText, cover.BackCopy,
                cover.BackgroundColor, cover.BarcodeMode, cover.ImageCropXPercent, cover.ImageCropYPercent,
                expectedRevision, true, direction),
            context.TurnCancellationToken);
        return Serialize(new { ok = true, targetId = releaseId, revision = saved.Revision, spineReadingDirection = saved.SpineReadingDirection, summary = "Spine direction updated. Inspect the annotated spine and complete wrap next." });
    }

    private static CompositionRegionConstraint ParseCoverRegion(string value) =>
        Enum.TryParse<CompositionRegionConstraint>(value, true, out var role)
        && role is CompositionRegionConstraint.Back or CompositionRegionConstraint.Spine or CompositionRegionConstraint.Front
            ? role
            : throw new ArgumentException("regionRole must be Back, Spine, or Front.");

    private async Task<string> PatchCoreCoverElementAsync(
        PublishAssistantContext context,
        long expectedBookRevision,
        long expectedCoverRevision,
        string targetKind,
        Guid targetId,
        CompositionElementPatch patch)
    {
        try
        {
            var cover = await books.GetCoverAsync(context.ProjectId, context.TurnCancellationToken);
            if (cover.Revision != expectedCoverRevision || cover.CoreBookRevision != expectedBookRevision)
                throw new DbUpdateConcurrencyException("Core Book or its cover changed; reread the cover before retrying.");
            var scene = JsonSerializer.Deserialize<CompositionScene>(cover.CompositionSceneJson, ManuscriptCodec.JsonOptions)
                ?? throw new InvalidDataException("The Core cover composition is empty.");
            var patched = CompositionService.ApplyElementPatch(scene, targetKind, targetId, patch);
            var saved = await books.SaveCoverAsync(
                context.ProjectId,
                expectedBookRevision,
                new PublicationCoverDesignUpdate(
                    cover.Title, cover.Subtitle, cover.Author, string.Empty, string.Empty,
                    cover.BackgroundColor, PublicationBarcodeMode.None, 50, 50, expectedCoverRevision, true),
                patched,
                context.TurnCancellationToken);
            return Serialize(new { ok = true, target = "core", targetId, revision = saved.Revision,
                bookRevision = saved.CoreBookRevision, changedIds = new[] { targetId },
                summary = $"Patched Core cover {targetKind} {targetId:N}.",
                mutation = new { kind = "core-cover", refresh = new[] { "core", "covers", "readiness", "artifacts" } } });
        }
        catch (Exception exception)
        {
            return Serialize(new { ok = false, code = exception is DbUpdateConcurrencyException ? "REVISION_CONFLICT" : "PATCH_REJECTED",
                targetId, summary = exception.Message, recovery = "Reread the Core cover and retry only the intended fields against current revisions." });
        }
    }

    private async Task<string> PlaceCoreCoverImageAsync(
        PublishAssistantContext context,
        long expectedBookRevision,
        long expectedCoverRevision,
        Guid targetId,
        Guid imageId,
        FigureImageFit fit,
        string? altText,
        bool decorative,
        int? readingOrder)
    {
        if (!decorative && string.IsNullOrWhiteSpace(altText))
            return Serialize(new { ok = false, code = "ALT_DECISION_REQUIRED", targetId, summary = "Provide alternative text or explicitly mark the artwork decorative." });
        if (await projectImages.GetAsync(context.ProjectId, imageId, context.TurnCancellationToken) is null)
            return Serialize(new { ok = false, code = "IMAGE_NOT_FOUND", targetId, imageId, summary = "Project image was not found." });
        return await PatchCoreCoverElementAsync(
            context,
            expectedBookRevision,
            expectedCoverRevision,
            "object",
            targetId,
            new CompositionElementPatch(
                ImageId: imageId,
                ImageFit: fit,
                AltText: decorative ? string.Empty : altText?.Trim(),
                Decorative: decorative,
                AccessibilityDecisionPending: false,
                SemanticRole: decorative ? CompositionSemanticRole.Artifact : CompositionSemanticRole.Figure,
                ReadingOrder: decorative ? null : readingOrder,
                ClearReadingOrder: decorative));
    }

    private async Task<string> AddCoreCoverImageAsync(
        PublishAssistantContext context,
        long expectedBookRevision,
        long expectedCoverRevision,
        Guid imageId,
        FigureImageFit fit,
        string? altText,
        bool decorative,
        CompositionBounds? bounds,
        int? readingOrder)
    {
        try
        {
            if (await projectImages.GetAsync(context.ProjectId, imageId, context.TurnCancellationToken) is null)
                return Serialize(new { ok = false, code = "IMAGE_NOT_FOUND", imageId, summary = "Project image was not found." });
            var cover = await books.GetCoverAsync(context.ProjectId, context.TurnCancellationToken);
            if (cover.Revision != expectedCoverRevision || cover.CoreBookRevision != expectedBookRevision)
                throw new DbUpdateConcurrencyException("Core Book or its cover changed; reread the cover before retrying.");
            var scene = JsonSerializer.Deserialize<CompositionScene>(cover.CompositionSceneJson, ManuscriptCodec.JsonOptions)
                ?? throw new InvalidDataException("The Core cover composition is empty.");
            var mutation = CompositionService.AddImageObjectToScene(scene, imageId, fit, altText, decorative, bounds ?? new CompositionBounds(), readingOrder);
            var saved = await books.SaveCoverAsync(
                context.ProjectId,
                expectedBookRevision,
                new PublicationCoverDesignUpdate(
                    cover.Title, cover.Subtitle, cover.Author, string.Empty, string.Empty,
                    cover.BackgroundColor, PublicationBarcodeMode.None, 50, 50, expectedCoverRevision, true),
                mutation.Scene,
                context.TurnCancellationToken);
            return Serialize(new { ok = true, target = "core", targetId = context.ProjectId, revision = saved.Revision,
                bookRevision = saved.CoreBookRevision, changedIds = new[] { mutation.ObjectId }, selectId = mutation.ObjectId,
                summary = "Project image added to the Core cover.",
                mutation = new { kind = "core-cover", selectId = mutation.ObjectId, refresh = new[] { "core", "covers", "readiness", "artifacts" } } });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException or DbUpdateConcurrencyException)
        {
            return Serialize(new { ok = false, code = exception is DbUpdateConcurrencyException ? "REVISION_CONFLICT" : "PLACEMENT_REJECTED",
                imageId, summary = exception.Message, recovery = "Reread the Core cover and retry with the same project-image ID." });
        }
    }

    private async Task<string> CustomizeReleaseCoverAsync(
        PublishAssistantContext context,
        Guid releaseId,
        long expectedReleaseRevision)
    {
        await EnsurePrintCoverPaginationAsync(context, releaseId);
        var cover = await covers.CustomizeFromCoreAsync(
            context.ProjectId, releaseId, expectedReleaseRevision, context.TurnCancellationToken);
        return Serialize(new { ok = true, targetId = releaseId, revision = cover.Revision,
            summary = "The release cover is now independently editable.",
            mutation = new { kind = "release-cover", releaseId, selectRelease = true, refresh = new[] { "release", "covers", "readiness", "artifacts" } } });
    }

    private async Task<string> StageCoreCoverCompositionAsync(
        PublishAssistantContext context,
        long expectedBookRevision,
        long expectedCoverRevision,
        CompositionScene scene)
    {
        try
        {
            var normalized = CompositionSceneResolver.NormalizeLogicalReadingOrder(scene);
            var stage = await books.StageCoverSceneAsync(context.ProjectId, context.ConversationId,
                expectedBookRevision, expectedCoverRevision, normalized.Scene, context.TurnCancellationToken);
            return Serialize(new { ok = true, target = "core", targetId = context.ProjectId,
                revision = expectedCoverRevision, stageId = stage.Id, stage.ExpiresAt,
                normalizedReadingOrderCount = normalized.ChangedObjectCount,
                summary = $"Staged {scene.Objects.Count} Core cover objects across {scene.Layers.Count} layers." });
        }
        catch (Exception exception) when (IsExpectedSceneToolFailure(exception))
        {
            return SerializeSceneToolFailure(exception, context.ProjectId,
                "Reread the Core cover, correct the reported scene issue, and retry against its current revisions.");
        }
    }

    private async Task<string> ApplyCoreCoverCompositionStageAsync(
        PublishAssistantContext context,
        Guid stageId,
        long expectedBookRevision,
        long expectedCoverRevision)
    {
        try
        {
            var cover = await books.ApplyCoverSceneStageAsync(context.ProjectId, context.ConversationId,
                stageId, expectedBookRevision, expectedCoverRevision, context.TurnCancellationToken);
            return Serialize(new { ok = true, target = "core", targetId = context.ProjectId,
                revision = cover.Revision, bookRevision = cover.CoreBookRevision,
                changedFields = new[] { "compositionScene" }, diagnosticCount = cover.Diagnostics.Count,
                diagnostics = cover.Diagnostics.Take(5),
                mutation = new { kind = "core-cover", refresh = new[] { "core", "covers", "readiness", "artifacts" } } });
        }
        catch (Exception exception) when (IsExpectedSceneToolFailure(exception))
        {
            return SerializeSceneToolFailure(exception, context.ProjectId,
                "Reread the Core cover. Restage the scene only if the stage expired or its revisions are stale.");
        }
    }

    private async Task<string> UseCoreCoverAsync(
        PublishAssistantContext context,
        Guid releaseId,
        long expectedReleaseRevision)
    {
        await EnsurePrintCoverPaginationAsync(context, releaseId);
        await covers.UseCoreAsync(context.ProjectId, releaseId, expectedReleaseRevision, context.TurnCancellationToken);
        return Serialize(new { ok = true, targetId = releaseId,
            summary = "The release now inherits the Core cover live.",
            mutation = new { kind = "release-cover", releaseId, selectRelease = true, refresh = new[] { "release", "covers", "readiness", "artifacts" } } });
    }

    private async Task<string> PatchCoverElementAsync(PublishAssistantContext context, Guid editionId, long expectedRevision, string targetKind, Guid targetId, CompositionElementPatch patch)
    {
        try { await EnsurePrintCoverPaginationAsync(context, editionId); var cover = await covers.PatchElementAsync(context.ProjectId, editionId, expectedRevision, targetKind, targetId, patch, context.TurnCancellationToken); return Serialize(new { ok = true, targetId, revision = cover.Revision, changedIds = new[] { targetId }, summary = $"Patched cover {targetKind} {targetId:N}.", mutation = new { kind = "coverComposition", id = editionId } }); }
        catch (Exception ex) { return Serialize(new { ok = false, code = ex is DbUpdateConcurrencyException ? "REVISION_CONFLICT" : "PATCH_REJECTED", targetId, summary = ex.Message, recovery = "Reread the cover and retry only the intended fields against its current revision." }); }
    }

    private async Task<string> PatchCoverSurfaceElementAsync(
        PublishAssistantContext context,
        Guid editionId,
        string surfaceRole,
        long expectedRevision,
        string targetKind,
        Guid targetId,
        CompositionElementPatch patch)
    {
        try
        {
            await EnsurePrintCoverPaginationAsync(context, editionId);
            var cover = await covers.PatchSurfaceElementAsync(
                context.ProjectId, editionId, surfaceRole, expectedRevision, targetKind, targetId, patch,
                context.TurnCancellationToken);
            return Serialize(new
            {
                ok = true,
                targetId,
                surfaceRole,
                revision = cover.Revision,
                changedIds = new[] { targetId },
                summary = $"Patched {surfaceRole} {targetKind}.",
                mutation = new { kind = "coverComposition", id = editionId, surfaceRole, selectId = targetId },
            });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException or DbUpdateConcurrencyException)
        {
            return Serialize(new
            {
                ok = false,
                code = exception is DbUpdateConcurrencyException ? "REVISION_CONFLICT" : "PATCH_REJECTED",
                targetId,
                surfaceRole,
                summary = exception.Message,
                recovery = "Reread and preview this exact cover surface, then retry only the intended fields.",
            });
        }
    }

    private async Task<string> PlaceCoverImageAsync(
        PublishAssistantContext context,
        Guid editionId,
        long expectedRevision,
        Guid targetId,
        Guid imageId,
        FigureImageFit fit,
        string? altText,
        bool decorative,
        int? readingOrder)
    {
        if (!decorative && string.IsNullOrWhiteSpace(altText))
            return Serialize(new { ok = false, code = "ALT_DECISION_REQUIRED", targetId, summary = "Provide alternative text or explicitly mark the artwork decorative." });
        if (await projectImages.GetAsync(context.ProjectId, imageId, context.TurnCancellationToken) is null)
            return Serialize(new { ok = false, code = "IMAGE_NOT_FOUND", targetId, imageId, summary = "Project image was not found." });
        return await PatchCoverElementAsync(
            context,
            editionId,
            expectedRevision,
            "object",
            targetId,
            new CompositionElementPatch(
                ImageId: imageId,
                ImageFit: fit,
                AltText: decorative ? string.Empty : altText?.Trim(),
                Decorative: decorative,
                AccessibilityDecisionPending: false,
                SemanticRole: decorative ? CompositionSemanticRole.Artifact : CompositionSemanticRole.Figure,
                ReadingOrder: decorative ? null : readingOrder,
                ClearReadingOrder: decorative));
    }

    private async Task<string> PlaceCoverSurfaceImageAsync(
        PublishAssistantContext context,
        Guid editionId,
        string surfaceRole,
        long expectedRevision,
        Guid targetId,
        Guid imageId,
        FigureImageFit fit,
        string? altText,
        bool decorative,
        int? readingOrder)
    {
        if (!decorative && string.IsNullOrWhiteSpace(altText))
            return Serialize(new { ok = false, code = "ALT_DECISION_REQUIRED", targetId, surfaceRole, summary = "Provide alternative text or explicitly mark the artwork decorative." });
        if (await projectImages.GetAsync(context.ProjectId, imageId, context.TurnCancellationToken) is null)
            return Serialize(new { ok = false, code = "IMAGE_NOT_FOUND", targetId, surfaceRole, imageId, summary = "Project image was not found." });
        return await PatchCoverSurfaceElementAsync(
            context,
            editionId,
            surfaceRole,
            expectedRevision,
            "object",
            targetId,
            new CompositionElementPatch(
                ImageId: imageId,
                ImageFit: fit,
                AltText: decorative ? string.Empty : altText?.Trim(),
                Decorative: decorative,
                AccessibilityDecisionPending: false,
                SemanticRole: decorative ? CompositionSemanticRole.Artifact : CompositionSemanticRole.Figure,
                ReadingOrder: decorative ? null : readingOrder,
                ClearReadingOrder: decorative));
    }

    private async Task<string> AddCoverSurfaceImageAsync(
        PublishAssistantContext context,
        Guid editionId,
        string surfaceRole,
        long expectedRevision,
        Guid imageId,
        FigureImageFit fit,
        string? altText,
        bool decorative,
        CompositionBounds? bounds,
        int? readingOrder)
    {
        try
        {
            await EnsurePrintCoverPaginationAsync(context, editionId);
            if (!decorative && string.IsNullOrWhiteSpace(altText))
                return Serialize(new { ok = false, code = "ALT_DECISION_REQUIRED", targetId = editionId, surfaceRole, summary = "Provide alternative text or explicitly mark the artwork decorative." });
            if (await projectImages.GetAsync(context.ProjectId, imageId, context.TurnCancellationToken) is null)
                return Serialize(new { ok = false, code = "IMAGE_NOT_FOUND", targetId = editionId, surfaceRole, imageId, summary = "Project image was not found." });
            var cover = await covers.GetSurfaceAsync(context.ProjectId, editionId, surfaceRole, context.TurnCancellationToken);
            if (cover.Revision != expectedRevision)
                throw new DbUpdateConcurrencyException("The cover surface changed; reread it before retrying.");
            var scene = JsonSerializer.Deserialize<CompositionScene>(cover.CompositionSceneJson, ManuscriptCodec.JsonOptions)
                ?? throw new InvalidDataException("The release cover surface is empty.");
            var mutation = CompositionService.AddImageObjectToScene(scene, imageId, fit, altText, decorative, bounds ?? new CompositionBounds(), readingOrder);
            var saved = await covers.SaveSurfaceWorkspaceAsync(
                context.ProjectId,
                editionId,
                surfaceRole,
                new PublicationCoverDesignUpdate(
                    cover.Title, cover.Subtitle, cover.Author, cover.SpineText, cover.BackCopy,
                    cover.BackgroundColor, cover.BarcodeMode, cover.ImageCropXPercent, cover.ImageCropYPercent,
                    expectedRevision, true),
                mutation.Scene,
                context.TurnCancellationToken);
            return Serialize(new
            {
                ok = true,
                targetId = editionId,
                surfaceRole,
                revision = saved.Revision,
                changedIds = new[] { mutation.ObjectId },
                selectId = mutation.ObjectId,
                summary = $"Project image added to the {surfaceRole} cover surface.",
                mutation = new { kind = "coverComposition", id = editionId, surfaceRole, selectId = mutation.ObjectId },
            });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException or DbUpdateConcurrencyException)
        {
            return Serialize(new
            {
                ok = false,
                code = exception is DbUpdateConcurrencyException ? "REVISION_CONFLICT" : "PLACEMENT_REJECTED",
                targetId = editionId,
                surfaceRole,
                imageId,
                summary = exception.Message,
                recovery = "Reread this exact cover surface and retry with the same project-image ID.",
            });
        }
    }

    private async Task<string> AddCoverImageAsync(
        PublishAssistantContext context,
        Guid editionId,
        long expectedRevision,
        Guid imageId,
        FigureImageFit fit,
        string? altText,
        bool decorative,
        CompositionBounds? bounds,
        int? readingOrder)
    {
        try
        {
            await EnsurePrintCoverPaginationAsync(context, editionId);
            if (await projectImages.GetAsync(context.ProjectId, imageId, context.TurnCancellationToken) is null)
                return Serialize(new { ok = false, code = "IMAGE_NOT_FOUND", targetId = editionId, imageId, summary = "Project image was not found." });
            var cover = await covers.GetAsync(context.ProjectId, editionId, context.TurnCancellationToken);
            if (cover.Revision != expectedRevision)
                throw new DbUpdateConcurrencyException("The cover changed; reread it before retrying.");
            var scene = JsonSerializer.Deserialize<CompositionScene>(cover.CompositionSceneJson, ManuscriptCodec.JsonOptions)
                ?? throw new InvalidDataException("The release cover composition is empty.");
            var mutation = CompositionService.AddImageObjectToScene(scene, imageId, fit, altText, decorative, bounds ?? new CompositionBounds(), readingOrder);
            var saved = await covers.SaveWorkspaceAsync(
                context.ProjectId,
                editionId,
                new PublicationCoverDesignUpdate(
                    cover.Title, cover.Subtitle, cover.Author, cover.SpineText, cover.BackCopy,
                    cover.BackgroundColor, cover.BarcodeMode, cover.ImageCropXPercent, cover.ImageCropYPercent,
                    expectedRevision, true),
                mutation.Scene,
                context.TurnCancellationToken);
            return Serialize(new { ok = true, targetId = editionId, revision = saved.Revision,
                changedIds = new[] { mutation.ObjectId }, selectId = mutation.ObjectId,
                summary = "Project image added to the release cover.",
                mutation = new { kind = "coverComposition", id = editionId, selectId = mutation.ObjectId } });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException or DbUpdateConcurrencyException)
        {
            return Serialize(new { ok = false, code = exception is DbUpdateConcurrencyException ? "REVISION_CONFLICT" : "PLACEMENT_REJECTED",
                targetId = editionId, imageId, summary = exception.Message,
                recovery = "Reread the release cover and retry with the same project-image ID." });
        }
    }

    private async Task<string> UpdateCoverAsync(
        PublishAssistantContext context,
        Guid editionId,
        PublicationCoverDesignUpdate update)
    {
        await EnsurePrintCoverPaginationAsync(context, editionId);
        return Serialize(await covers.UpdateAsync(context.ProjectId, editionId, update, context.TurnCancellationToken));
    }

    private static IReadOnlyList<PublicationCoverDiagnostic> StructuredCoverDiagnostics(
        PublicationCoverDesignView cover) =>
        cover.DiagnosticDetails.Count > 0
            ? cover.DiagnosticDetails
            : cover.Diagnostics
                .Select(message => new PublicationCoverDiagnostic("error", "COVER_DESIGN_LEGACY", message))
                .ToList();

    private async Task<string> ValidateCoverAsync(PublishAssistantContext context, Guid? releaseId)
    {
        try
        {
            if (releaseId is Guid paginatedReleaseId)
                await EnsurePrintCoverPaginationAsync(context, paginatedReleaseId);
            var cover = releaseId is Guid editionId
                ? await covers.GetAsync(context.ProjectId, editionId, context.TurnCancellationToken)
                : await books.GetCoverAsync(context.ProjectId, context.TurnCancellationToken);
            var diagnostics = StructuredCoverDiagnostics(cover);
            var errors = diagnostics.Count(item => item.Severity == "error");
            var warnings = diagnostics.Count(item => item.Severity == "warning");
            return Serialize(new
            {
                ok = errors == 0,
                target = releaseId is null ? "core" : "release",
                targetId = releaseId ?? context.ProjectId,
                revision = cover.Revision,
                summary = errors == 0
                    ? warnings == 0 ? "Cover validation passed." : $"Cover validation passed with {warnings} warning(s)."
                    : $"Cover validation found {errors} error(s) and {warnings} warning(s).",
                diagnosticCounts = new { total = diagnostics.Count, errors, warnings },
                diagnostics = diagnostics.Take(12),
                legacyDiagnostics = cover.Diagnostics.Take(12),
                hasMoreDiagnostics = diagnostics.Count > 12,
            });
        }
        catch (Exception ex) { return Serialize(new { ok = false, code = "VALIDATION_FAILED", targetId = releaseId ?? context.ProjectId, summary = ex.Message }); }
    }

    private async Task<string> StageCoverCompositionAsync(
        PublishAssistantContext context,
        Guid editionId,
        long expectedRevision,
        CompositionScene scene,
        string? surfaceRole)
    {
        try
        {
            await EnsurePrintCoverPaginationAsync(context, editionId);
            var normalized = CompositionSceneResolver.NormalizeLogicalReadingOrder(scene);
            var stage = await covers.StageSceneAsync(
                context.ProjectId,
                context.ConversationId,
                editionId,
                expectedRevision,
                normalized.Scene,
                surfaceRole,
                context.TurnCancellationToken);
            return Serialize(new
            {
                ok = true,
                targetId = editionId,
                revision = expectedRevision,
                stageId = stage.Id,
                stage.ExpiresAt,
                surfaceRole = stage.TargetKind["cover-scene:".Length..],
                normalizedReadingOrderCount = normalized.ChangedObjectCount,
                summary = $"Staged {scene.Objects.Count} cover objects across {scene.Layers.Count} layers.",
            });
        }
        catch (Exception exception) when (IsExpectedSceneToolFailure(exception))
        {
            return SerializeSceneToolFailure(exception, editionId,
                "Reread the release cover, correct the reported scene issue, and retry against its current revision.");
        }
    }

    private async Task<string> ApplyCoverCompositionStageAsync(
        PublishAssistantContext context,
        Guid stageId,
        long expectedRevision)
    {
        try
        {
            Guid editionId;
            await using (var operation = await database.OpenReadAsync(context.TurnCancellationToken))
            {
                var stagedTarget = await operation.Db.CompositionMutationStages.AsNoTracking()
                    .Where(item => item.Id == stageId
                        && item.ProjectId == context.ProjectId
                        && item.ConversationId == context.ConversationId
                        && (item.TargetKind == "cover-scene" || item.TargetKind.StartsWith("cover-scene:")))
                    .Select(item => new { item.TargetId })
                    .SingleOrDefaultAsync(context.TurnCancellationToken);
                editionId = stagedTarget?.TargetId ?? Guid.Empty;
            }
            if (editionId == Guid.Empty)
                throw new KeyNotFoundException("Cover stage was not found for this conversation.");
            await EnsurePrintCoverPaginationAsync(context, editionId);
            var cover = await covers.ApplySceneStageAsync(
                context.ProjectId,
                context.ConversationId,
                stageId,
                expectedRevision,
                context.TurnCancellationToken);
            return Serialize(new
            {
                ok = true,
                targetId = cover.EditionId,
                revision = cover.Revision,
                surfaceRole = cover.SurfaceRole,
                changedFields = new[] { "surfaceScene" },
                diagnosticCount = cover.Diagnostics.Count,
                diagnostics = cover.Diagnostics.Take(5),
                mutation = new { kind = "coverComposition", id = cover.EditionId, selectId = cover.EditionId },
            });
        }
        catch (Exception exception) when (IsExpectedSceneToolFailure(exception))
        {
            return SerializeSceneToolFailure(exception, stageId,
                "Reread the release cover. Restage the scene only if the stage expired or its revision is stale.");
        }
    }

    private static bool IsExpectedSceneToolFailure(Exception exception) =>
        exception is ArgumentException
            or InvalidDataException
            or InvalidOperationException
            or DbUpdateConcurrencyException
            or KeyNotFoundException;

    private static string SerializeSceneToolFailure(Exception exception, Guid referenceId, string recovery) =>
        Serialize(new
        {
            ok = false,
            code = exception switch
            {
                DbUpdateConcurrencyException => "REVISION_CONFLICT",
                KeyNotFoundException => "STAGE_NOT_FOUND",
                _ => "SCENE_REJECTED",
            },
            referenceId,
            summary = exception.Message,
            recovery,
        });

    private async Task<string> ExportAsync(
        PublishAssistantContext context,
        Guid editionId,
        PublishExportFormat format)
    {
        var file = await publishing.ExportAsync(context.ProjectId, editionId, format);
        var workspace = await publishing.GetWorkspaceAsync(context.ProjectId, editionId);
        return Serialize(new
        {
            file.FileName,
            file.ContentType,
            workspace.SourceFingerprint,
            RegeneratedAtDownload = true,
            DownloadUrl = $"/projects/{context.ProjectId:N}/publish/releases/{editionId:N}/exports/{format}",
        });
    }

    internal static object DownloadView(PublishAssistantContext context, PublicationArtifactView artifact) => new
    {
        artifact.Id,
        artifact.Kind,
        artifact.FileName,
        artifact.MediaType,
        artifact.ByteLength,
        artifact.PageCount,
        artifact.CreatedAt,
        artifact.IsLegacy,
        artifact.IsStale,
        State = artifact.IsLegacy ? "Legacy" : artifact.IsStale ? "Stale" : "Current",
        ViewUrl = $"/projects/{context.ProjectId:N}/publish/artifacts/{artifact.Id:N}",
        DownloadUrl = $"/projects/{context.ProjectId:N}/publish/artifacts/{artifact.Id:N}/download",
    };

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    private static object Continuation(int start, int returned, int total) => new
    {
        start,
        returned,
        total,
        hasMore = start + returned < total,
        nextStart = start + returned < total ? start + returned : (int?)null,
    };
    private static string Truncate(string value, int maximum) => value.Length <= maximum ? value : value[..Math.Max(0, maximum - 1)] + "…";
}
