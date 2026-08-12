using System.Text.Json;
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

namespace Lorekeeper.Publish;

public sealed class PublishAssistantContext(
    Guid projectId,
    Guid conversationId = default,
    Guid? selectedEditionId = null,
    PublishAssistantWorkspaceContext? workspaceContext = null,
    CancellationToken turnCancellationToken = default)
{
    private readonly List<EntityVisualContextReference> _visuals = [];
    private readonly List<PublishAssistantTransientVisual> _transientVisuals = [];
    private readonly HashSet<Guid> _imageJobIds = [];

    public Guid ProjectId { get; } = projectId;
    public Guid ConversationId { get; } = conversationId;
    public Guid? SelectedEditionId { get; } = selectedEditionId;
    public PublishAssistantWorkspaceContext? WorkspaceContext { get; } = workspaceContext;
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
    string FileName,
    string ContentType,
    byte[] Data,
    string Caption);

public sealed record PublicationSectionToolInput(
    Guid? SectionId,
    string Title,
    PublicationSectionKind Kind,
    PublicationSectionAnchor Anchor,
    PublishOutlineTargetKind? TargetKind,
    Guid? TargetId,
    PublicationSectionInclusionMode Inclusion,
    long? ExpectedRevision = null,
    string? ManuscriptJson = null);

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
    IPublicationCoverService covers,
    IManuscriptStyleService manuscriptStyles,
    IProjectFontService projectFonts,
    IProjectPageSetupService pageSetups,
    IProjectImageService projectImages,
    IEditionContentService? editionContent = null,
    ICompositionCanvasPreviewService? canvasPreviews = null,
    ICompositionService? compositions = null,
    AppDbContext? db = null,
    IAgentProjectImageWorkflow? imageWorkflow = null,
    IProjectSearchService? projectSearch = null) : IPublishAssistantTools
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
                description: "Discover bounded project sources with stable IDs and exact read arguments."),
            AIFunctionFactory.Create(
                method: (string sourceType, Guid sourceId, int? pageNumber = null) =>
                    ReadProjectSourceAsync(context, sourceType, sourceId, pageNumber),
                name: "read_project_source",
                description: "Read one paginated project source, including chapter bodies, research sources, acts, entities, and ingest material."),
            AIFunctionFactory.Create(
                method: (string query, int topK = 8, string[]? sourceTypes = null, string[]? sourceIds = null, Guid? containerSourceId = null, bool lexicalOnly = false) =>
                    SearchProjectAsync(context, query, topK, sourceTypes, sourceIds, containerSourceId, lexicalOnly),
                name: "search_project",
                description: "Run bounded hybrid project search and return stable source IDs, compact excerpts, and exact detail-read arguments."),
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
                description: "List the optional Paperback, EPUB ebook, and PDF ebook releases with stable IDs, product type, destination, status, and revision."),
            AIFunctionFactory.Create(
                method: (string name, PublicationEditionFormat format, PublicationVendor destination) => CreateReleaseAsync(context, name, format, destination),
                name: "create_publication_release",
                description: "Create an optional release from safe application-managed presets. Use Generic destination for EPUB and PDF ebook; Paperback destinations are AmazonKdp, IngramSpark, or Generic."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, int contentStart = 0, int contentCount = 30) => ReadWorkspaceAsync(context, releaseId, contentStart, contentCount),
                name: "read_publication_release",
                description: "Read a compact release projection with effective inherited values, field override markers, revision, bounded structure, counts, and current diagnostics."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, PublicationReleaseOverridePatch patch) => PatchReleaseAsync(context, releaseId, patch),
                name: "patch_publication_release_overrides",
                description: "Revision-check sparse release product settings and field overrides. ResetFields restores live Core inheritance. Language accepts en, en-US, or en-GB. Vendor profile versions are application-managed and cannot be supplied."),
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
                description: "List a bounded page of effective Core or release publication sections with positions, inclusion/inheritance state, revisions, and compact visual counts."),
            AIFunctionFactory.Create(
                method: (Guid sectionId, Guid? releaseId = null, int blockStart = 0, int blockCount = 30) => ReadPublicationSectionAsync(context, releaseId, sectionId, blockStart, blockCount),
                name: "read_publication_section",
                description: "Read one Core or release publication section with bounded semantic blocks. Designed-page blocks expose pageCompositionId; use it with read_publication_page_composition and preview_publication_section_page_canvas before editing."),
            AIFunctionFactory.Create(
                method: (PublicationSectionToolInput input, Guid? releaseId = null) => UpsertPublicationSectionAsync(context, releaseId, input),
                name: "upsert_publication_section",
                description: "Create or revision-check a Core/release publication section and its metadata. Supplying a selected release ID materializes an inherited section as a release customization while preserving its content. Choose one content mode per section: prose with optional Figures, or Designed Page canvases only. For existing prose, use patch_publication_section_manuscript instead of repeating the complete manuscript."),
            AIFunctionFactory.Create(
                method: (Guid sectionId, long expectedRevision, ManuscriptOperationInput[] operations, Guid? releaseId = null) => PatchPublicationSectionManuscriptAsync(context, releaseId, sectionId, expectedRevision, operations),
                name: "patch_publication_section_manuscript",
                description: "Apply focused revision-checked manuscript operations to one user-authored Core/release publication section. Read the bounded section first. Title and copyright use their Designed Page scene tools; contents is generated automatically."),
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
                description: "Render one complete publication-section Designed Page as a transient model-visible canvas. Use annotated while designing and clean before completion. The preview creates no project image."),
            AIFunctionFactory.Create(
                method: (Guid variantId, long expectedRevision, string targetKind, Guid targetId, CompositionElementPatch patch) => PatchPublicationSectionPageElementAsync(context, context.SelectedEditionId, variantId, expectedRevision, targetKind, targetId, patch),
                name: "patch_publication_section_page_element",
                description: "Revision-check and patch one object, layer, or style on a Core/release publication-section Designed Page. Supply changed fields only."),
            AIFunctionFactory.Create(
                method: (Guid variantId, long expectedRevision, Guid targetId, bool retainAspectRatio = true) => FillPublicationSectionPageImageCanvasAsync(context, variantId, expectedRevision, targetId, retainAspectRatio),
                name: "fill_publication_section_page_image_canvas",
                description: "Make one image object cover the entire active publication-section page canvas. With retainAspectRatio=true it uses proportional crop-to-fill; false stretches the raster. Reread and require imageCoversCanvas=true before reporting success."),
            AIFunctionFactory.Create(
                method: (Guid variantId, long expectedRevision, Guid targetId, Guid imageId, FigureImageFit fit, string? altText, bool decorative, int? readingOrder = null) => PlacePublicationSectionPageImageAsync(context, variantId, expectedRevision, targetId, imageId, fit, altText, decorative, readingOrder),
                name: "place_project_image_in_publication_section_page_frame",
                description: "Place an existing project-image ID into one existing image frame on the active publication-section page. Generation remains separate. Provide Contain, Cover, or Stretch and an alt-text or explicit decorative decision."),
            AIFunctionFactory.Create(
                method: (Guid variantId, long expectedRevision, Guid imageId, FigureImageFit fit, string? altText, bool decorative, CompositionBounds? bounds = null, int? readingOrder = null) => AddPublicationSectionPageImageAsync(context, context.SelectedEditionId, variantId, expectedRevision, imageId, fit, altText, decorative, bounds, readingOrder),
                name: "add_project_image_to_publication_section_page",
                description: "Add an existing project-image ID to a publication-section Designed Page. Generation remains separate; provide fit and an alt-text or decorative decision."),
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
                method: (string targetKind, Guid targetId, Guid? variantId = null, Guid? releaseId = null) => ReadLayoutGenerationTargetAsync(context, targetKind, targetId, variantId, releaseId),
                name: "read_publication_generation_target",
                description: "Resolve optional composition guidance for a concrete Figure placement, page surface/frame, or cover surface/frame. Page targets require the exact selected composition variantId. Use it when artwork must honor protected physical regions; it does not restrict later placement of other source-image shapes."),
            AIFunctionFactory.Create(
                method: (Guid variantId) => ValidateCompositionAsync(context, context.SelectedEditionId, variantId),
                name: "validate_publication_page_composition",
                description: "Validate one publication-section Designed Page against the protected active Core/release target for geometry, semantic coverage, reading order, accessibility, overflow, image DPI, and font readiness."),
            AIFunctionFactory.Create(
                method: (ImageGenerationBrief brief, ImageReferenceUse[]? references = null, ImageGenerationTarget? geometryGuidance = null, string? altText = null, string? quality = null, string? outputFormat = null, int? outputCompression = null) =>
                    GenerateProjectImageAsync(context, brief, references, geometryGuidance, altText, quality, outputFormat, outputCompression),
                name: "generate_project_image",
                description: "Generate one unattached project image and wait for a terminal result. Optional page, Figure, frame, or cover geometry guides composition only and never places output. Inspect the returned image, then apply its project-image ID with a focused cover tool or edit the relevant publication section during this turn."),
            AIFunctionFactory.Create(
                method: (Guid sourceImageId, ImageEditBrief brief, ImageReferenceUse[]? references = null, ImageGenerationTarget? geometryGuidance = null, string? altText = null, string? quality = null, string? outputFormat = null, int? outputCompression = null) =>
                    EditProjectImageAsync(context, sourceImageId, brief, references, geometryGuidance, altText, quality, outputFormat, outputCompression),
                name: "edit_project_image",
                description: "Edit one project image and wait for a terminal result. The output remains an unattached project image; inspect it and place its ID with a separate publication tool."),
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
                method: (Guid? releaseId = null, int objectStart = 0, int objectCount = 30, int structureStart = 0, int structureCount = 30) => ReadCoverAsync(context, releaseId, objectStart, objectCount, structureStart, structureCount),
                name: "read_publication_cover_design",
                description: "Read the Core front cover when releaseId is omitted, or one release cover when supplied. Returns compact copy, inheritance state, geometry, diagnostics, layers, and one bounded page of scene objects."),
            AIFunctionFactory.Create(
                method: (Guid? releaseId = null, string mode = "annotated") => PreviewCoverCanvasAsync(context, releaseId, mode),
                name: "preview_publication_cover_canvas",
                description: "Render the complete Core or release cover authoring canvas directly as a transient image. Omit releaseId for Core. Use annotated while designing and clean for final visual verification. The preview is model-visible when vision is available and never creates a project-image asset."),
            AIFunctionFactory.Create(
                method: (Guid? releaseId = null) => ValidateCoverAsync(context, releaseId),
                name: "validate_publication_cover_composition",
                description: "Validate the Core front cover or a supplied release cover for geometry, accessibility, reading order, images, and product-specific regions. Returns compact prioritized diagnostics."),
            AIFunctionFactory.Create(
                method: (long expectedBookRevision, long expectedCoverRevision, string targetKind, Guid targetId, CompositionElementPatch patch) => PatchCoreCoverElementAsync(context, expectedBookRevision, expectedCoverRevision, targetKind, targetId, patch),
                name: "patch_publication_core_cover_element",
                description: "Revision-check and patch one stable Core cover object, layer, or style with changed fields only. Core cover geometry comes from project page setup, and artwork remains below canonical cover copy."),
            AIFunctionFactory.Create(
                method: (long expectedBookRevision, long expectedCoverRevision, Guid targetId, Guid imageId, FigureImageFit fit, string? altText, bool decorative, int? readingOrder = null) => PlaceCoreCoverImageAsync(context, expectedBookRevision, expectedCoverRevision, targetId, imageId, fit, altText, decorative, readingOrder),
                name: "place_project_image_on_core_cover",
                description: "Place an existing project-image ID into one existing Core cover image object. Contain and Cover retain aspect ratio; Stretch permits distortion. Requires an alt-text or decorative decision. This is separate from image generation."),
            AIFunctionFactory.Create(
                method: (long expectedBookRevision, long expectedCoverRevision, Guid imageId, FigureImageFit fit, string? altText, bool decorative, CompositionBounds? bounds = null, int? readingOrder = null) => AddCoreCoverImageAsync(context, expectedBookRevision, expectedCoverRevision, imageId, fit, altText, decorative, bounds, readingOrder),
                name: "add_project_image_to_core_cover",
                description: "Add an existing project-image ID as a new Core cover image object. Contain and Cover retain aspect ratio; Stretch permits distortion. Requires an alt-text or decorative decision. This is separate from image generation."),
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
                description: "Materialize the inherited Core front into an editable release cover while preserving product-specific paperback spine/back/barcode regions."),
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
                method: (Guid releaseId, long expectedRevision, Guid targetId, Guid imageId, FigureImageFit fit, string? altText, bool decorative, int? readingOrder = null) => PlaceCoverImageAsync(context, releaseId, expectedRevision, targetId, imageId, fit, altText, decorative, readingOrder),
                name: "place_project_image_on_release_cover",
                description: "Place an existing project-image ID into one existing release-cover image object. Contain and Cover retain aspect ratio; Stretch permits distortion. Requires an alt-text or decorative decision. This is separate from image generation."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, long expectedRevision, Guid imageId, FigureImageFit fit, string? altText, bool decorative, CompositionBounds? bounds = null, int? readingOrder = null) => AddCoverImageAsync(context, releaseId, expectedRevision, imageId, fit, altText, decorative, bounds, readingOrder),
                name: "add_project_image_to_release_cover",
                description: "Add an existing project-image ID as a new release-cover image object. Contain and Cover retain aspect ratio; Stretch permits distortion. Requires an alt-text or decorative decision. This is separate from image generation."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, long expectedRevision, CompositionScene scene) =>
                    StageCoverCompositionAsync(context, releaseId, expectedRevision, scene),
                name: "stage_publication_cover_composition",
                description: "Submit a complete cover scene exactly once. Reading order may be omitted; Lorekeeper preserves supplied relative order and uses object-array position as the deterministic fallback before validation. Artwork is normalized below canonical cover copy. Returns an opaque one-use stage ID and compact diagnostics without echoing the scene."),
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
            "patch_publication_core_cover_element", "place_project_image_on_core_cover", "add_project_image_to_core_cover", "customize_publication_release_cover", "use_core_publication_cover",
            "stage_publication_core_cover_composition", "apply_publication_core_cover_composition_stage",
            "patch_publication_cover_element", "place_project_image_on_release_cover", "add_project_image_to_release_cover", "stage_publication_cover_composition", "apply_publication_cover_composition_stage",
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
            Math.Clamp(topK, 1, 30));
        return ProjectSearchAgentPayload.SerializeSources(sources);
    }

    private async Task<string> ReadProjectSourceAsync(
        PublishAssistantContext context,
        string sourceType,
        Guid sourceId,
        int? pageNumber)
    {
        if (projectSearch is null)
            return Serialize(new { ok = false, code = "SEARCH_UNAVAILABLE", summary = "Project search is unavailable." });
        var result = await projectSearch.ReadSourceAsync(context.ProjectId, sourceType, sourceId, pageNumber);
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
            lexicalOnly), context.TurnCancellationToken);
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
                book.AllowDesignedPageOverrides, book.PageSetup },
            coverRevision = book.CoverRevision });
    }

    private async Task<string> PatchPublicationBookAsync(PublishAssistantContext context, PublicationBookPatch patch)
    {
        var book = await books.UpdateAsync(context.ProjectId, patch);
        return Serialize(new { ok = true, target = "core", targetId = context.ProjectId, revision = book.Revision,
            summary = "Core Book updated; inheriting releases now resolve the changed values.", mutation = new { kind = "core-book", refresh = new[] { "core", "releases", "readiness", "artifacts" } } });
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
        var blocks = item.Manuscript.Content.Skip(blockStart).Take(blockCount).ToList();
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
        return Serialize(new
        {
            ok = true,
            targetId = item.Id,
            sectionId = item.Id,
            releaseId,
            item.CoreSectionId,
            item.Title,
            item.Kind,
            item.SystemRole,
            item.Anchor,
            item.TargetKind,
            anchorTargetId = item.TargetId,
            item.InclusionMode,
            item.IsInherited,
            item.Revision,
            blocks,
            pageCanvases,
            continuation = Continuation(blockStart, blocks.Count, item.Manuscript.Content.Count),
        });
    }

    private async Task<string> UpsertPublicationSectionAsync(
        PublishAssistantContext context,
        Guid? releaseId,
        PublicationSectionToolInput input)
    {
        ManuscriptDocument document;
        if (!string.IsNullOrWhiteSpace(input.ManuscriptJson))
            document = ManuscriptCodec.Deserialize(input.ManuscriptJson, input.SectionId ?? Guid.NewGuid(), input.ExpectedRevision ?? 0);
        else if (input.SectionId is Guid sectionId)
            document = (await publicationSections.GetAsync(new(context.ProjectId, releaseId), sectionId, context.TurnCancellationToken)).Manuscript;
        else
            document = ManuscriptCodec.CreateEmpty(Guid.NewGuid());
        var saved = await publicationSections.UpsertAsync(new(context.ProjectId, releaseId), new(
            input.SectionId,
            input.Title,
            input.Kind,
            input.Anchor,
            input.TargetKind,
            input.TargetId,
            input.Inclusion,
            ManuscriptCodec.Serialize(document),
            input.ExpectedRevision), context.TurnCancellationToken);
        return Serialize(new
        {
            ok = true,
            targetId = saved.Id,
            releaseId,
            revision = saved.Revision,
            changedFields = new[] { "title", "kind", "anchor", "target", "inclusion", "manuscript" },
            summary = $"Saved publication section '{saved.Title}'.",
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
        CreateReleaseCoreAsync(context, name, format, format == PublicationEditionFormat.Paperback ? destination : PublicationVendor.Generic);

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
        var projectSlug = await (db ?? throw new InvalidOperationException("Project lookup is unavailable.")).Projects.AsNoTracking()
            .Where(item => item.Id == context.ProjectId)
            .Select(item => item.Slug)
            .SingleAsync(context.TurnCancellationToken);
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
            summary = releaseId is null ? "Core reading-PDF preparation queued." : "Release file preparation queued.", mutation = new { kind = "preparation", releaseId } });
    }

    private async Task<string> CancelPreparationAsync(PublishAssistantContext context, Guid preparationJobId)
    {
        var job = await preparation.CancelAsync(context.ProjectId, preparationJobId);
        return Serialize(new { ok = true, targetId = job.EditionId ?? context.ProjectId, releaseId = job.EditionId, job.Id, job.Status, job.Message,
            summary = "Preparation cancellation recorded.", mutation = new { kind = "preparation", releaseId = job.EditionId } });
    }

    private async Task<string> ReadPreparationAsync(PublishAssistantContext context, Guid? releaseId)
    {
        var kind = releaseId is null ? PublicationTargetKind.CoreBook : PublicationTargetKind.Release;
        var jobs = await preparation.ListAsync(context.ProjectId, kind, releaseId);
        var artifacts = releaseId is Guid id
            ? await renders.ListArtifactsAsync(context.ProjectId, id)
            : (await renders.ListCoreAsync(context.ProjectId)).SelectMany(item => item.Artifacts).OrderByDescending(item => item.CreatedAt).ToList();
        return Serialize(new { ok = true, targetId = releaseId ?? context.ProjectId,
            current = jobs.Take(3).Select(job => new { job.Id, releaseId = job.EditionId, job.Status, job.Step,
                job.ProgressPercent, job.Message, job.CreatedAt, job.CompletedAt,
                diagnostics = job.Diagnostics.Take(5) }),
            diagnosticCounts = jobs.FirstOrDefault()?.Diagnostics.GroupBy(item => item.Severity).ToDictionary(group => group.Key, group => group.Count()),
            artifacts = artifacts.Where(item => !item.IsLegacy).Take(12).Select(item => DownloadView(context, item)), hasMoreArtifacts = artifacts.Count > 12 });
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
                workspace.Edition.Binding,
                workspace.Edition.Paper,
                workspace.Edition.Ink,
                PrintBleedManaged = workspace.Edition.Format == PublicationEditionFormat.Paperback,
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
        ImageGenerationTarget? geometryGuidance,
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
                geometryGuidance,
                altText,
                quality,
                outputFormat,
                outputCompression,
                "Publish image",
                context.TrackImageJob,
                context.TurnCancellationToken);
            return ImageResult(context, result);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return Serialize(new { ok = false, code = "GENERATION_REJECTED", summary = ex.Message });
        }
    }

    private async Task<string> EditProjectImageAsync(
        PublishAssistantContext context,
        Guid sourceImageId,
        ImageEditBrief brief,
        ImageReferenceUse[]? references,
        ImageGenerationTarget? geometryGuidance,
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
                geometryGuidance,
                altText,
                quality,
                outputFormat,
                outputCompression,
                "Publish image edit",
                context.TrackImageJob,
                context.TurnCancellationToken);
            return ImageResult(context, result);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return Serialize(new { ok = false, code = "EDIT_REJECTED", summary = ex.Message });
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
        return Serialize(new
        {
            ok = result.Succeeded,
            jobId = result.JobId,
            status = result.Status,
            targetAspect = result.TargetAspect,
            requestedRaster = result.RequestedRaster,
            outputImageIds = result.Images.Select(image => image.Id),
            images = result.Outputs.Select(output => new { output.Image.Id, output.Image.FileName, output.Image.ContentType, output.Width, output.Height, output.ActualRaster, output.GeometryMatched, effectiveDpi = output.EffectiveDpi is { } dpi ? (double?)Math.Round(dpi, 1) : null, output.Image.PreviewUrl }),
            attached = false,
            diagnosticCounts = new { errors = result.Diagnostics.Count, warnings = result.LayoutBound ? result.Outputs.Count(output => !output.GeometryMatched) : 0 },
            diagnostics = result.Diagnostics.Take(3),
            geometryWarnings = result.LayoutBound
                ? result.Outputs.Where(output => !output.GeometryMatched).Select(output => new { code = "LAYOUT_IMAGE_GEOMETRY_MISMATCH", message = $"Provider returned {output.ActualRaster} instead of requested {result.RequestedRaster}. Inspect before placement or regeneration." })
                : [],
            summary = result.Summary,
            nextAction = result.Succeeded
                ? "Inspect the returned project image, then place its ID with a separate cover or publication tool before completing the request."
                : null,
        });
    }

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
        var store = db ?? throw new InvalidOperationException("Publication visual storage is unavailable.");
        var chapters = await store.Chapters.AsNoTracking().Where(item => item.ProjectId == context.ProjectId)
            .OrderBy(item => item.Order).Select(item => new { item.Id, item.Title, item.ManuscriptJson }).ToListAsync(context.TurnCancellationToken);
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

            var service = compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable.");
            var composition = await service.GetAsync(context.ProjectId, effectiveCompositionId, context.TurnCancellationToken)
                ?? throw new KeyNotFoundException("Publication-section page composition was not found.");
            if (composition.PublicationSectionId != section.Id || composition.EditionId != releaseId)
                throw new InvalidOperationException("The page composition does not belong to the active Publish target.");
            var variant = releaseId is Guid editionId
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
                $"publication-section-page-{compositionId:N}-{previewMode.ToString().ToLowerInvariant()}.png",
                "image/png",
                preview.Data,
                $"{previewMode} publication-section page preview at composition revision {preview.CompositionRevision}, variant revision {preview.VariantRevision}"));
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
                imageId, fit, altText, decorative, bounds, readingOrder, context.TurnCancellationToken);
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

    private static EditorContentTarget SectionContentTarget(Guid? releaseId) =>
        releaseId is Guid id ? EditorContentTarget.ForEdition(id) : EditorContentTarget.Core;

    private async Task<string> ReadLayoutGenerationTargetAsync(PublishAssistantContext context, string targetKind, Guid targetId, Guid? variantId, Guid? editionId)
    {
        try
        {
            var service = compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable.");
            var descriptor = editionId is Guid releaseId
                ? await service.DescribeGenerationTargetAsync(context.ProjectId, releaseId, targetKind, targetId, variantId, context.TurnCancellationToken)
                : await service.DescribeAuthoringGenerationTargetAsync(context.ProjectId, targetKind, targetId, variantId, context.TurnCancellationToken);
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
                    descriptor.WidthInches,
                    descriptor.HeightInches,
                    descriptor.AspectRatio,
                    descriptor.RecommendedWidthPixels,
                    descriptor.RecommendedHeightPixels,
                    descriptor.RequestedWidthPixels,
                    descriptor.RequestedHeightPixels,
                    descriptor.RequestedRaster,
                    descriptor.EffectiveDpiExpectation,
                    descriptor.Regions,
                    descriptor.Diagnostics,
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
        int objectStart,
        int objectCount,
        int structureStart,
        int structureCount)
    {
        var cover = releaseId is Guid editionId
            ? await covers.GetAsync(context.ProjectId, editionId)
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
            cover.Title,
            cover.Subtitle,
            cover.Author,
            cover.SpineText,
            cover.BackCopy,
            cover.BackgroundColor,
            cover.BarcodeMode,
            cover.Template,
            cover.Diagnostics,
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
            var cover = releaseId is Guid editionId
                ? await covers.GetAsync(context.ProjectId, editionId, context.TurnCancellationToken)
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
                context.TurnCancellationToken);
            var visualId = Guid.NewGuid();
            var fileName = $"cover-{targetId:N}-{previewMode.Value.ToString().ToLowerInvariant()}.png";
            context.AddTransientVisual(new(
                visualId,
                fileName,
                "image/png",
                preview.Data,
                previewMode == CompositionCanvasPreviewMode.Annotated
                    ? "Annotated direct cover-canvas preview."
                    : "Clean direct cover-canvas preview."));
            var diagnostics = preview.Diagnostics.Take(10).ToList();
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
                    total = preview.Diagnostics.Count,
                    errors = preview.Diagnostics.Count(item => item.Severity == "error"),
                    warnings = preview.Diagnostics.Count(item => item.Severity == "warning"),
                },
                diagnostics,
                hasMoreDiagnostics = preview.Diagnostics.Count > diagnostics.Count,
                delivery = "The complete cover canvas is attached as transient visual context when the active provider supports vision.",
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
            var mutation = CompositionService.AddImageObjectToScene(scene, imageId, fit, altText, decorative, bounds, readingOrder);
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
        await covers.UseCoreAsync(context.ProjectId, releaseId, expectedReleaseRevision, context.TurnCancellationToken);
        return Serialize(new { ok = true, targetId = releaseId,
            summary = "The release now inherits the Core cover live.",
            mutation = new { kind = "release-cover", releaseId, selectRelease = true, refresh = new[] { "release", "covers", "readiness", "artifacts" } } });
    }

    private async Task<string> PatchCoverElementAsync(PublishAssistantContext context, Guid editionId, long expectedRevision, string targetKind, Guid targetId, CompositionElementPatch patch)
    {
        try { var cover = await covers.PatchElementAsync(context.ProjectId, editionId, expectedRevision, targetKind, targetId, patch, context.TurnCancellationToken); return Serialize(new { ok = true, targetId, revision = cover.Revision, changedIds = new[] { targetId }, summary = $"Patched cover {targetKind} {targetId:N}.", mutation = new { kind = "coverComposition", id = editionId } }); }
        catch (Exception ex) { return Serialize(new { ok = false, code = ex is DbUpdateConcurrencyException ? "REVISION_CONFLICT" : "PATCH_REJECTED", targetId, summary = ex.Message, recovery = "Reread the cover and retry only the intended fields against its current revision." }); }
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
            if (await projectImages.GetAsync(context.ProjectId, imageId, context.TurnCancellationToken) is null)
                return Serialize(new { ok = false, code = "IMAGE_NOT_FOUND", targetId = editionId, imageId, summary = "Project image was not found." });
            var cover = await covers.GetAsync(context.ProjectId, editionId, context.TurnCancellationToken);
            if (cover.Revision != expectedRevision)
                throw new DbUpdateConcurrencyException("The cover changed; reread it before retrying.");
            var scene = JsonSerializer.Deserialize<CompositionScene>(cover.CompositionSceneJson, ManuscriptCodec.JsonOptions)
                ?? throw new InvalidDataException("The release cover composition is empty.");
            var mutation = CompositionService.AddImageObjectToScene(scene, imageId, fit, altText, decorative, bounds, readingOrder);
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
        PublicationCoverDesignUpdate update) =>
        Serialize(await covers.UpdateAsync(context.ProjectId, editionId, update));

    private async Task<string> ValidateCoverAsync(PublishAssistantContext context, Guid? releaseId)
    {
        try
        {
            var cover = releaseId is Guid editionId
                ? await covers.GetAsync(context.ProjectId, editionId, context.TurnCancellationToken)
                : await books.GetCoverAsync(context.ProjectId, context.TurnCancellationToken);
            return Serialize(new { ok = cover.Diagnostics.Count == 0, target = releaseId is null ? "core" : "release", targetId = releaseId ?? context.ProjectId, revision = cover.Revision, summary = cover.Diagnostics.Count == 0 ? "Cover validation passed." : $"Cover validation found {cover.Diagnostics.Count} diagnostic(s).", diagnosticCounts = new { errors = cover.Diagnostics.Count, warnings = 0 }, diagnostics = cover.Diagnostics.Take(12) });
        }
        catch (Exception ex) { return Serialize(new { ok = false, code = "VALIDATION_FAILED", targetId = releaseId ?? context.ProjectId, summary = ex.Message }); }
    }

    private async Task<string> StageCoverCompositionAsync(
        PublishAssistantContext context,
        Guid editionId,
        long expectedRevision,
        CompositionScene scene)
    {
        try
        {
            var normalized = CompositionSceneResolver.NormalizeLogicalReadingOrder(scene);
            var stage = await covers.StageSceneAsync(
                context.ProjectId,
                context.ConversationId,
                editionId,
                expectedRevision,
                normalized.Scene,
                context.TurnCancellationToken);
            return Serialize(new
            {
                ok = true,
                targetId = editionId,
                revision = expectedRevision,
                stageId = stage.Id,
                stage.ExpiresAt,
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
                changedFields = new[] { "compositionScene" },
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
