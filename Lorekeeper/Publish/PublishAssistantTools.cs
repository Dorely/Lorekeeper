using System.Text.Json;
using Lorekeeper.EntityVisuals;
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
    CancellationToken turnCancellationToken = default)
{
    private readonly List<EntityVisualContextReference> _visuals = [];
    private readonly HashSet<Guid> _imageJobIds = [];

    public Guid ProjectId { get; } = projectId;
    public Guid ConversationId { get; } = conversationId;
    public CancellationToken TurnCancellationToken { get; } = turnCancellationToken;
    public IReadOnlyList<Guid> ImageJobIds => _imageJobIds.ToList();
    public void TrackImageJob(Guid jobId) => _imageJobIds.Add(jobId);
    public void AddVisual(EntityVisualContextReference visual) => _visuals.Add(visual);
    public IReadOnlyList<EntityVisualContextReference> DrainVisuals()
    {
        var result = _visuals.ToList();
        _visuals.Clear();
        return result;
    }
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
    IPublicationPreparationService preparation,
    IPublishService publishing,
    IPublicationRenderService renders,
    IPublicationCoverService covers,
    IManuscriptStyleService manuscriptStyles,
    IProjectImageService projectImages,
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
                description: "Revision-check a sparse Core Book patch. Supply only changed values; omitted fields are preserved. Results contain changed state and refresh metadata, not repeated manuscript or scene payloads."),
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
                description: "Revision-check sparse release product settings and field overrides. ResetFields restores live Core inheritance. Vendor profile versions are application-managed and cannot be supplied."),
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
                method: (Guid? matterId = null, int blockStart = 0, int blockCount = 20) => ReadCoreMatterAsync(context, matterId, blockStart, blockCount),
                name: "read_publication_book_matter",
                description: "List compact shared front/back matter metadata, or supply matterId to read a bounded page of semantic manuscript blocks for revision-safe editing."),
            AIFunctionFactory.Create(
                method: (PublicationMatterInput input, long expectedBookRevision) => UpsertCoreMatterAsync(context, input, expectedBookRevision),
                name: "upsert_publication_book_matter",
                description: "Revision-check create or update one shared semantic front/back matter item."),
            AIFunctionFactory.Create(
                method: (Guid matterId, long expectedBookRevision) => DeleteCoreMatterAsync(context, matterId, expectedBookRevision),
                name: "delete_publication_book_matter",
                description: "Delete one shared Core Book matter item using the current Core revision."),
            AIFunctionFactory.Create(
                method: () => ReadCorePlacementsAsync(context),
                name: "read_publication_book_placements",
                description: "Read compact shared opening/ending image placements, fit, caption, and accessibility decisions."),
            AIFunctionFactory.Create(
                method: (PublicationImagePlacementCreate input, long expectedBookRevision) => AddCorePlacementAsync(context, input, expectedBookRevision),
                name: "add_publication_book_placement",
                description: "Add one project-owned opening or ending image to Core Book with explicit fit and accessibility semantics."),
            AIFunctionFactory.Create(
                method: (Guid placementId, PublicationImagePlacementUpdate input, long expectedBookRevision) => UpdateCorePlacementAsync(context, placementId, input, expectedBookRevision),
                name: "update_publication_book_placement",
                description: "Revision-check and update one shared Core Book opening or ending image placement."),
            AIFunctionFactory.Create(
                method: (Guid[] orderedPlacementIds, long expectedBookRevision) => ReorderCorePlacementsAsync(context, orderedPlacementIds, expectedBookRevision),
                name: "reorder_publication_book_placements",
                description: "Revision-check the complete order of shared Core Book images at one target and position."),
            AIFunctionFactory.Create(
                method: (Guid placementId, long expectedBookRevision) => DeleteCorePlacementAsync(context, placementId, expectedBookRevision),
                name: "delete_publication_book_placement",
                description: "Delete one shared Core Book image placement using the current Core revision."),
            AIFunctionFactory.Create(
                method: (int offset = 0, int limit = 30) => ListNamedStylesAsync(context, offset, limit),
                name: "list_publication_book_text_styles",
                description: "List a compact page of project Book Text Styles with stable IDs, definitions, revisions, and continuation metadata."),
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
                description: "Read one selected geometry variant losslessly in bounded object pages, including complete surface, layers, styles, object fields, semantic excerpts, and revisions. Computed page overlays are omitted."),
            AIFunctionFactory.Create(
                method: (string targetKind, Guid targetId, Guid? variantId = null, Guid? releaseId = null) => ReadLayoutGenerationTargetAsync(context, targetKind, targetId, variantId, releaseId),
                name: "read_publication_generation_target",
                description: "Resolve optional composition guidance for a concrete Figure placement, page surface/frame, or cover surface/frame. Page targets require the exact selected composition variantId. Use it when artwork must honor protected physical regions; it does not restrict later placement of other source-image shapes."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, Guid variantId) => ValidateCompositionAsync(context, releaseId, variantId),
                name: "validate_publication_page_composition",
                description: "Validate one Designed Page variant for geometry, semantic coverage, reading order, accessibility, overflow, image DPI, font readiness, and release compatibility. Returns compact prioritized diagnostics."),
            AIFunctionFactory.Create(
                method: (ImageGenerationBrief brief, ImageReferenceUse[]? references = null, ImageGenerationTarget? geometryGuidance = null, string? altText = null, string? quality = null, string? outputFormat = null, int? outputCompression = null) =>
                    GenerateProjectImageAsync(context, brief, references, geometryGuidance, altText, quality, outputFormat, outputCompression),
                name: "generate_project_image",
                description: "Generate one unattached project image and wait for a terminal result. Optional page, Figure, frame, or cover geometry guides composition only and never places output. Inspect the returned image, then apply its project-image ID with a separate cover or publication placement tool during this turn."),
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
                method: (Guid compositionId, Guid releaseId) => GetOrCreateCompositionVariantAsync(context, compositionId, releaseId),
                name: "get_or_create_publication_composition_variant",
                description: "Create layout for this release when no exact Designed Page geometry exists. Copies the authoring layout into an independent release geometry variant for review without altering authoring state."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, Guid variantId, long expectedRevision, string targetKind, Guid targetId, CompositionElementPatch patch) => PatchCompositionElementAsync(context, releaseId, variantId, expectedRevision, targetKind, targetId, patch),
                name: "patch_publication_composition_element",
                description: "Revision-check patch one stable composition object, layer, or style using only changed fields. Page overlays are computed and cannot be authored. Preserve unrelated state and reserve full-scene staging for structural edits."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, Guid variantId, long expectedRevision, Guid targetId, Guid imageId, FigureImageFit fit, string? altText, bool decorative, int? readingOrder = null) => PlacePublicationPageImageAsync(context, releaseId, variantId, expectedRevision, targetId, imageId, fit, altText, decorative, readingOrder),
                name: "place_project_image_in_publication_page_frame",
                description: "Place an existing project-image ID into an existing release-specific Designed Page frame with explicit Contain/Cover fit and an accessibility decision."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, Guid variantId, long expectedRevision, Guid imageId, FigureImageFit fit, string? altText, bool decorative, CompositionBounds? bounds = null, int? readingOrder = null) => AddPublicationPageImageAsync(context, releaseId, variantId, expectedRevision, imageId, fit, altText, decorative, bounds, readingOrder),
                name: "add_project_image_to_publication_page",
                description: "Add a new image object to a release-specific Designed Page from an existing project-image ID with explicit Contain/Cover fit and an accessibility decision."),
            AIFunctionFactory.Create(
                method: (Guid variantId, long expectedRevision, CompositionScene scene) => StageCompositionAsync(context, variantId, expectedRevision, scene),
                name: "stage_publication_composition",
                description: "Submit a complete large scene once. Returns an opaque one-use stage ID, compact summary, and diagnostics without echoing the scene."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, Guid stageId, long expectedRevision) => ApplyCompositionStageAsync(context, releaseId, stageId, expectedRevision),
                name: "apply_publication_composition_stage",
                description: "Apply an already staged scene using only its one-use stage ID and expected revision. Never repeat the scene payload."),
            AIFunctionFactory.Create(
                method: (Guid compositionId, long expectedRevision, ManuscriptOperationInput[] operations) => StageCompositionSemanticAsync(context, compositionId, expectedRevision, operations),
                name: "stage_publication_composition_semantic",
                description: "Stage focused block or inline-mark operations against a Designed Page's sole semantic manuscript. Returns a compact one-use stage ID without repeating content."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, Guid stageId, long expectedRevision) => ApplyCompositionSemanticStageAsync(context, releaseId, stageId, expectedRevision),
                name: "apply_publication_composition_semantic_stage",
                description: "Apply a staged Designed Page semantic edit by one-use stage ID and exact composition revision."),
            AIFunctionFactory.Create(
                method: (Guid compositionId, long expectedCompositionRevision, Guid variantId, long expectedVariantRevision, ManuscriptOperationInput[] semanticOperations, CompositionScene scene) => StageCompositionWorkspaceAsync(context, compositionId, expectedCompositionRevision, variantId, expectedVariantRevision, semanticOperations, scene),
                name: "stage_publication_composition_workspace",
                description: "Atomically stage coupled Designed Page content and scene changes. Semantic fragments contain paragraph, heading, sceneBreak, blockQuote, or listItem blocks only; scene images carry visual content. Submit the complete payload once."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, Guid stageId, long expectedCompositionRevision) => ApplyCompositionWorkspaceStageAsync(context, releaseId, stageId, expectedCompositionRevision),
                name: "apply_publication_composition_workspace_stage",
                description: "Apply a coupled content-and-scene stage by one-use stage ID; both stored revisions are checked without repeating the payload."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, PublicationEditionOutlineItemUpdate[] updates, long expectedRevision) =>
                    SetContentAsync(context, releaseId, updates, expectedRevision),
                name: "patch_publication_release_content",
                description: "Include or exclude chapters only where this release differs from Core Book. Act headings and summaries are sparse release setting overrides, not content rows."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, PublicationEditionOutlineItemOrder[] orderedItems, long expectedRevision) =>
                    ReorderContentAsync(context, releaseId, orderedItems, expectedRevision),
                name: "reorder_publication_release_content",
                description: "Set a complete release-only reading order using stable IDs and an expected revision."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, PublicationMatterInput input, long expectedRevision) =>
                    UpsertMatterAsync(context, releaseId, input, expectedRevision),
                name: "upsert_publication_release_matter",
                description: "Create a release-only matter item or replace an inherited/effective item by ID with a sparse semantic overlay."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, Guid? matterId = null, int blockStart = 0, int blockCount = 20) => ReadReleaseMatterAsync(context, releaseId, matterId, blockStart, blockCount),
                name: "read_publication_release_matter",
                description: "List effective release matter overlays, or supply matterId to read a bounded page of semantic blocks before replacing or excluding it."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, Guid matterId, long expectedRevision) =>
                    DeleteMatterAsync(context, releaseId, matterId, expectedRevision),
                name: "delete_publication_release_matter",
                description: "Delete a release-only matter item or explicitly exclude inherited Core matter at an expected release revision."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, PublicationEditionStyleMappingInput input, long expectedRevision) =>
                    UpsertStyleMappingAsync(context, releaseId, input, expectedRevision),
                name: "upsert_publication_release_style_override",
                description: "Override a project Book Text Style for one release."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, Guid mappingId, long expectedRevision) =>
                    DeleteStyleMappingAsync(context, releaseId, mappingId, expectedRevision),
                name: "delete_publication_release_style_override",
                description: "Delete a release style override and restore the project Book Text Style."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, PublicationImagePlacementCreate input, long expectedRevision) =>
                    AddPlacementAsync(context, releaseId, input, expectedRevision),
                name: "add_publication_release_placement",
                description: "Add a release-only opening or ending image placement."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, Guid placementId, PublicationImagePlacementUpdate input, long expectedRevision) =>
                    UpdatePlacementAsync(context, releaseId, placementId, input, expectedRevision),
                name: "update_publication_release_placement",
                description: "Update a release image placement at an expected revision."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, Guid[] orderedPlacementIds, long expectedRevision) =>
                    ReorderPlacementsAsync(context, releaseId, orderedPlacementIds, expectedRevision),
                name: "reorder_publication_release_placements",
                description: "Reorder all release placements in one target/position group."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, Guid placementId, long expectedRevision) =>
                    DeletePlacementAsync(context, releaseId, placementId, expectedRevision),
                name: "delete_publication_release_placement",
                description: "Delete one release image placement at an expected revision."),
            AIFunctionFactory.Create(
                method: (Guid? releaseId = null, int objectStart = 0, int objectCount = 30, int structureStart = 0, int structureCount = 30) => ReadCoverAsync(context, releaseId, objectStart, objectCount, structureStart, structureCount),
                name: "read_publication_cover_design",
                description: "Read the Core front cover when releaseId is omitted, or one release cover when supplied. Returns compact copy, inheritance state, geometry, diagnostics, layers, and one bounded page of scene objects."),
            AIFunctionFactory.Create(
                method: (Guid? releaseId = null) => ValidateCoverAsync(context, releaseId),
                name: "validate_publication_cover_composition",
                description: "Validate the Core front cover or a supplied release cover for geometry, accessibility, reading order, images, and product-specific regions. Returns compact prioritized diagnostics."),
            AIFunctionFactory.Create(
                method: (long expectedBookRevision, long expectedCoverRevision, string targetKind, Guid targetId, CompositionElementPatch patch) => PatchCoreCoverElementAsync(context, expectedBookRevision, expectedCoverRevision, targetKind, targetId, patch),
                name: "patch_publication_core_cover_element",
                description: "Revision-check and patch one stable Core cover object, layer, or style with changed fields only. Core cover geometry comes from project page setup."),
            AIFunctionFactory.Create(
                method: (long expectedBookRevision, long expectedCoverRevision, Guid targetId, Guid imageId, FigureImageFit fit, string? altText, bool decorative, int? readingOrder = null) => PlaceCoreCoverImageAsync(context, expectedBookRevision, expectedCoverRevision, targetId, imageId, fit, altText, decorative, readingOrder),
                name: "place_project_image_on_core_cover",
                description: "Place an existing project-image ID into one existing Core cover image object. Requires explicit Contain/Cover fit and an alt-text or decorative decision. This is separate from image generation."),
            AIFunctionFactory.Create(
                method: (long expectedBookRevision, long expectedCoverRevision, Guid imageId, FigureImageFit fit, string? altText, bool decorative, CompositionBounds? bounds = null, int? readingOrder = null) => AddCoreCoverImageAsync(context, expectedBookRevision, expectedCoverRevision, imageId, fit, altText, decorative, bounds, readingOrder),
                name: "add_project_image_to_core_cover",
                description: "Add an existing project-image ID as a new Core cover image object. Requires explicit Contain/Cover fit and an alt-text or decorative decision. This is separate from image generation."),
            AIFunctionFactory.Create(
                method: (long expectedBookRevision, long expectedCoverRevision, CompositionScene scene) => StageCoreCoverCompositionAsync(context, expectedBookRevision, expectedCoverRevision, scene),
                name: "stage_publication_core_cover_composition",
                description: "Submit a complete Core front-cover scene exactly once. Returns an opaque one-use stage ID and compact diagnostics without echoing the scene."),
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
                description: "Revision-check patch one stable cover object, guide, layer, or style using only changed fields. Preserve unrelated cover state; use full-scene staging for structural changes."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, long expectedRevision, Guid targetId, Guid imageId, FigureImageFit fit, string? altText, bool decorative, int? readingOrder = null) => PlaceCoverImageAsync(context, releaseId, expectedRevision, targetId, imageId, fit, altText, decorative, readingOrder),
                name: "place_project_image_on_release_cover",
                description: "Place an existing project-image ID into one existing release-cover image object. Requires explicit Contain/Cover fit and an alt-text or decorative decision. This is separate from image generation."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, long expectedRevision, Guid imageId, FigureImageFit fit, string? altText, bool decorative, CompositionBounds? bounds = null, int? readingOrder = null) => AddCoverImageAsync(context, releaseId, expectedRevision, imageId, fit, altText, decorative, bounds, readingOrder),
                name: "add_project_image_to_release_cover",
                description: "Add an existing project-image ID as a new release-cover image object. Requires explicit Contain/Cover fit and an alt-text or decorative decision. This is separate from image generation."),
            AIFunctionFactory.Create(
                method: (Guid releaseId, long expectedRevision, CompositionScene scene) =>
                    StageCoverCompositionAsync(context, releaseId, expectedRevision, scene),
                name: "stage_publication_cover_composition",
                description: "Submit a complete cover scene exactly once. Returns an opaque one-use stage ID and compact diagnostics without echoing the scene."),
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
            "prepare_publication_files", "cancel_publication_preparation", "read_publication_readiness",
            "read_publication_book_content", "patch_publication_book_content", "read_publication_book_matter",
            "upsert_publication_book_matter", "delete_publication_book_matter", "read_publication_book_placements",
            "add_publication_book_placement", "update_publication_book_placement", "reorder_publication_book_placements", "delete_publication_book_placement",
            "list_publication_book_text_styles", "list_publication_manuscript_visuals",
            "read_publication_page_composition", "read_publication_generation_target", "validate_publication_page_composition",
            "generate_project_image", "edit_project_image", "read_project_image_job", "wait_project_image_job", "cancel_project_image_job", "get_or_create_publication_composition_variant",
            "patch_publication_composition_element", "place_project_image_in_publication_page_frame", "add_project_image_to_publication_page", "stage_publication_composition", "apply_publication_composition_stage",
            "stage_publication_composition_semantic", "apply_publication_composition_semantic_stage",
            "stage_publication_composition_workspace", "apply_publication_composition_workspace_stage",
            "patch_publication_release_content", "reorder_publication_release_content", "read_publication_release_matter", "upsert_publication_release_matter", "delete_publication_release_matter",
            "upsert_publication_release_style_override", "delete_publication_release_style_override", "add_publication_release_placement",
            "update_publication_release_placement", "reorder_publication_release_placements", "delete_publication_release_placement",
            "read_publication_cover_design", "validate_publication_cover_composition", "update_publication_cover_design",
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
            summary = $"{book.IncludedChapterCount} chapters, {book.MatterCount} matter items, {book.ImagePlacementCount} opening/ending images.",
            values = new { book.Title, book.Subtitle, book.Author, book.Language, book.Publisher, book.Copyright, book.Description,
                book.IncludeTableOfContents, book.IncludeVisibleTableOfContents, book.IncludeActSynopses, book.IncludeChapterSynopses,
                book.IncludeActHeadings, book.IncludeChapterHeadings, book.NumberActs, book.NumberChapters, book.TitlePageMode, book.PageSetup },
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

    private async Task<string> ReadCoreMatterAsync(PublishAssistantContext context, Guid? matterId, int blockStart, int blockCount)
    {
        var book = await books.GetOrCreateAsync(context.ProjectId, context.TurnCancellationToken);
        var matter = await books.ListMatterAsync(context.ProjectId, context.TurnCancellationToken);
        if (matterId is Guid id)
        {
            var item = matter.SingleOrDefault(candidate => candidate.Id == id)
                ?? throw new KeyNotFoundException("Core Book matter was not found.");
            blockStart = Math.Max(0, blockStart);
            blockCount = Math.Clamp(blockCount, 1, 40);
            var blocks = item.Manuscript.Content.Skip(blockStart).Take(blockCount).ToList();
            return Serialize(new { ok = true, target = "core", revision = book.Revision,
                item.Id, item.Location, item.Kind, item.Title, item.IsIncluded, item.SortOrder, item.Revision,
                blocks, continuation = Continuation(blockStart, blocks.Count, item.Manuscript.Content.Count) });
        }
        return Serialize(new { ok = true, target = "core", revision = book.Revision,
            summary = $"{matter.Count} shared matter item(s).",
            items = matter.Select(item => new { item.Id, item.Location, item.Kind, item.Title, item.IsIncluded, item.SortOrder, item.Revision }) });
    }

    private async Task<string> ReadReleaseMatterAsync(
        PublishAssistantContext context,
        Guid releaseId,
        Guid? matterId,
        int blockStart,
        int blockCount)
    {
        var workspace = await publishing.GetWorkspaceAsync(context.ProjectId, releaseId, context.TurnCancellationToken);
        if (matterId is Guid id)
        {
            var item = workspace.Matter.SingleOrDefault(candidate => candidate.Id == id)
                ?? throw new KeyNotFoundException("Release matter was not found.");
            blockStart = Math.Max(0, blockStart);
            blockCount = Math.Clamp(blockCount, 1, 40);
            var blocks = item.Manuscript.Content.Skip(blockStart).Take(blockCount).ToList();
            return Serialize(new { ok = true, targetId = releaseId, revision = workspace.Edition.Revision,
                item.Id, item.Location, item.Kind, item.Title, item.IsIncluded, item.SortOrder, item.Revision,
                blocks, continuation = Continuation(blockStart, blocks.Count, item.Manuscript.Content.Count) });
        }
        return Serialize(new { ok = true, targetId = releaseId, revision = workspace.Edition.Revision,
            items = workspace.Matter.Select(item => new { item.Id, item.Location, item.Kind, item.Title,
                item.IsIncluded, item.SortOrder, item.Revision }), summary = $"{workspace.Matter.Count} effective matter item(s)." });
    }

    private async Task<string> UpsertCoreMatterAsync(
        PublishAssistantContext context,
        PublicationMatterInput input,
        long expectedBookRevision)
    {
        var matter = await books.UpsertMatterAsync(
            context.ProjectId, input, expectedBookRevision, context.TurnCancellationToken);
        return Serialize(new { ok = true, target = "core", targetId = matter.Id, revision = matter.Revision,
            summary = $"Saved Core Book matter '{matter.Title}'.", changedIds = new[] { matter.Id },
            mutation = new { kind = "core-matter", refresh = new[] { "core", "releases", "readiness", "artifacts" } } });
    }

    private async Task<string> DeleteCoreMatterAsync(
        PublishAssistantContext context,
        Guid matterId,
        long expectedBookRevision)
    {
        await books.DeleteMatterAsync(context.ProjectId, matterId, expectedBookRevision, context.TurnCancellationToken);
        return Serialize(new { ok = true, target = "core", targetId = matterId,
            summary = "Core Book matter removed.", changedIds = new[] { matterId },
            mutation = new { kind = "core-matter", refresh = new[] { "core", "releases", "readiness", "artifacts" } } });
    }

    private async Task<string> ReadCorePlacementsAsync(PublishAssistantContext context)
    {
        var book = await books.GetOrCreateAsync(context.ProjectId, context.TurnCancellationToken);
        var placements = await books.ListImagePlacementsAsync(context.ProjectId, context.TurnCancellationToken);
        return Serialize(new { ok = true, target = "core", revision = book.Revision,
            summary = $"{placements.Count} shared opening/ending image placement(s).", placements });
    }

    private async Task<string> AddCorePlacementAsync(
        PublishAssistantContext context,
        PublicationImagePlacementCreate input,
        long expectedBookRevision)
    {
        var placement = await books.AddImagePlacementAsync(
            context.ProjectId, input, expectedBookRevision, context.TurnCancellationToken);
        return Serialize(new { ok = true, target = "core", targetId = placement.Id,
            summary = $"Placed '{placement.AssetFileName}' in Core Book.", changedIds = new[] { placement.Id },
            mutation = new { kind = "core-placement", refresh = new[] { "core", "releases", "readiness", "artifacts" } } });
    }

    private async Task<string> DeleteCorePlacementAsync(
        PublishAssistantContext context,
        Guid placementId,
        long expectedBookRevision)
    {
        await books.DeleteImagePlacementAsync(context.ProjectId, placementId, expectedBookRevision, context.TurnCancellationToken);
        return Serialize(new { ok = true, target = "core", targetId = placementId,
            summary = "Core Book image placement removed.", changedIds = new[] { placementId },
            mutation = new { kind = "core-placement", refresh = new[] { "core", "releases", "readiness", "artifacts" } } });
    }

    private async Task<string> UpdateCorePlacementAsync(
        PublishAssistantContext context,
        Guid placementId,
        PublicationImagePlacementUpdate input,
        long expectedBookRevision)
    {
        var placement = await books.UpdateImagePlacementAsync(context.ProjectId, placementId, input,
            expectedBookRevision, context.TurnCancellationToken);
        return Serialize(new { ok = true, target = "core", targetId = placement.Id,
            summary = $"Updated Core Book placement for '{placement.AssetFileName}'.", changedIds = new[] { placement.Id },
            mutation = new { kind = "core-placement", refresh = new[] { "core", "releases", "readiness", "artifacts" } } });
    }

    private async Task<string> ReorderCorePlacementsAsync(
        PublishAssistantContext context,
        Guid[] orderedPlacementIds,
        long expectedBookRevision)
    {
        await books.ReorderImagePlacementsAsync(context.ProjectId, orderedPlacementIds,
            expectedBookRevision, context.TurnCancellationToken);
        return Serialize(new { ok = true, target = "core", targetId = context.ProjectId,
            summary = "Core Book image placements reordered.", changedIds = orderedPlacementIds,
            mutation = new { kind = "core-placement", refresh = new[] { "core", "releases", "readiness", "artifacts" } } });
    }

    private Task<string> CreateReleaseAsync(PublishAssistantContext context, string name, PublicationEditionFormat format, PublicationVendor destination) =>
        CreateReleaseCoreAsync(context, name, format, format == PublicationEditionFormat.Paperback ? destination : PublicationVendor.Generic);

    private async Task<string> PatchReleaseAsync(PublishAssistantContext context, Guid releaseId, PublicationReleaseOverridePatch patch)
    {
        var release = await editions.PatchOverridesAsync(context.ProjectId, releaseId, patch);
        return Serialize(new { ok = true, targetId = release.Id, revision = release.Revision,
            summary = "Release overrides updated.", mutation = new { kind = "release", releaseId = release.Id, selectRelease = true, refresh = new[] { "release", "readiness", "artifacts" } } });
    }

    private async Task<string> PrepareFilesAsync(PublishAssistantContext context, Guid? releaseId)
    {
        var job = releaseId is Guid id
            ? await preparation.PrepareReleaseAsync(context.ProjectId, id)
            : await preparation.PrepareCoreAsync(context.ProjectId);
        return Serialize(new { ok = true, targetId = releaseId ?? context.ProjectId, job.Id, job.Status, job.Step, job.ProgressPercent, job.Message,
            summary = releaseId is null ? "Core reading-PDF preparation queued." : "Release file preparation queued.", mutation = new { kind = "preparation", releaseId } });
    }

    private async Task<string> CancelPreparationAsync(PublishAssistantContext context, Guid preparationJobId)
    {
        var job = await preparation.CancelAsync(context.ProjectId, preparationJobId);
        return Serialize(new { ok = true, targetId = job.EditionId ?? context.ProjectId, job.Id, job.Status, job.Message,
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
            summary = $"{workspace.Sections.Sum(section => section.Chapters.Count)} chapter(s), {workspace.Matter.Count} matter item(s), {workspace.Placements.Count} release illustration(s), and {workspace.StyleMappings.Count} style override(s).",
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
            matter = workspace.Matter.Take(12).Select(item => new { item.Id, item.Location, item.Kind, item.Title, item.IsIncluded, item.SortOrder, item.Revision }),
            placements = workspace.Placements.Take(12).Select(item => new { item.Id, item.AssetId, item.AssetFileName,
                item.TargetKind, item.TargetId, item.PlacementKind, item.Caption, item.AltText, item.Decorative, item.SortOrder }),
            continuation = new { start = contentStart, returned = Math.Min(contentCount, Math.Max(0, content.Count - contentStart)), total = content.Count, hasMore = contentStart + contentCount < content.Count, nextStart = contentStart + contentCount < content.Count ? contentStart + contentCount : (int?)null },
            counts = new
            {
                matter = workspace.Matter.Count,
                styleMappings = workspace.StyleMappings.Count,
                placements = workspace.Placements.Count,
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
            requestedCanvas = result.RequestedCanvas,
            outputImageIds = result.Images.Select(image => image.Id),
            images = result.Outputs.Select(output => new { output.Image.Id, output.Image.FileName, output.Image.ContentType, output.Width, output.Height, output.Image.PreviewUrl }),
            attached = false,
            diagnosticCounts = new { errors = result.Diagnostics.Count, warnings = 0 },
            diagnostics = result.Diagnostics.Take(3),
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

    private async Task<string> ReadPageCompositionAsync(PublishAssistantContext context, Guid compositionId, Guid variantId, int semanticStart, int semanticCount, int objectStart, int objectCount, int structureStart, int structureCount)
    {
        var service = compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable.");
        try { return await CompositionAgentPayloads.ReadVariantAsync(service, context.ProjectId, compositionId, variantId, semanticStart, semanticCount, objectStart, objectCount, structureStart, structureCount, context.TurnCancellationToken); }
        catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException) { return Serialize(new { ok = false, code = "NOT_FOUND", targetId = variantId, summary = ex.Message }); }
    }

    private async Task<string> PatchCompositionElementAsync(PublishAssistantContext context, Guid editionId, Guid variantId, long expectedRevision, string targetKind, Guid targetId, CompositionElementPatch patch) =>
        await CompositionAgentPayloads.PatchElementAsync(
            compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable."),
            context.ProjectId,
            variantId,
            expectedRevision,
            targetKind,
            targetId,
            patch,
            context.TurnCancellationToken);

    private async Task<string> PlacePublicationPageImageAsync(PublishAssistantContext context, Guid editionId, Guid variantId, long expectedRevision, Guid targetId, Guid imageId, FigureImageFit fit, string? altText, bool decorative, int? readingOrder)
    {
        if (!decorative && string.IsNullOrWhiteSpace(altText))
            return Serialize(new { ok = false, code = "ALT_DECISION_REQUIRED", targetId, summary = "Provide alternative text or explicitly mark the artwork decorative." });
        if (await projectImages.GetAsync(context.ProjectId, imageId, context.TurnCancellationToken) is null)
            return Serialize(new { ok = false, code = "IMAGE_NOT_FOUND", targetId, imageId, summary = "Project image was not found." });
        return await PatchCompositionElementAsync(context, editionId, variantId, expectedRevision, "object", targetId, new CompositionElementPatch(
            ImageId: imageId,
            ImageFit: fit,
            AltText: decorative ? string.Empty : altText?.Trim(),
            Decorative: decorative,
            AccessibilityDecisionPending: false,
            SemanticRole: decorative ? CompositionSemanticRole.Artifact : CompositionSemanticRole.Figure,
            ReadingOrder: decorative ? null : readingOrder,
            ClearReadingOrder: decorative));
    }

    private async Task<string> AddPublicationPageImageAsync(PublishAssistantContext context, Guid editionId, Guid variantId, long expectedRevision, Guid imageId, FigureImageFit fit, string? altText, bool decorative, CompositionBounds? bounds, int? readingOrder)
    {
        try
        {
            if (await projectImages.GetAsync(context.ProjectId, imageId, context.TurnCancellationToken) is null)
                return Serialize(new { ok = false, code = "IMAGE_NOT_FOUND", targetId = variantId, imageId, summary = "Project image was not found." });
            var service = compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable.");
            var placed = await service.AddImageObjectAsync(context.ProjectId, variantId, expectedRevision, imageId, fit, altText, decorative, bounds, readingOrder, context.TurnCancellationToken);
            return Serialize(new { ok = true, targetId = variantId, releaseId = editionId, variantId = placed.Variant.Id, revision = placed.Variant.Revision, changedIds = new[] { placed.ObjectId }, selectId = placed.ObjectId, summary = "Project image added to the release layout.", mutation = new { kind = "pageComposition", id = placed.Variant.CompositionId, variantId = placed.Variant.Id, selectId = placed.ObjectId } });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or KeyNotFoundException or CompositionRevisionConflictException)
        {
            return Serialize(new { ok = false, code = ex is CompositionRevisionConflictException ? "REVISION_CONFLICT" : "PLACEMENT_REJECTED", targetId = variantId, summary = ex.Message, recovery = "Reread the compact release layout and retry with the same project-image ID." });
        }
    }

    private async Task<string> ReadLayoutGenerationTargetAsync(PublishAssistantContext context, string targetKind, Guid targetId, Guid? variantId, Guid? editionId)
    {
        try
        {
            var service = compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable.");
            var descriptor = targetKind.StartsWith("cover", StringComparison.OrdinalIgnoreCase)
                ? await service.DescribeGenerationTargetAsync(context.ProjectId, editionId ?? throw new ArgumentException("Publication cover generation targets require releaseId."), targetKind, targetId, variantId, context.TurnCancellationToken)
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
                    descriptor.ProviderCanvas,
                    descriptor.EffectiveDpiExpectation,
                    descriptor.Regions,
                    descriptor.Diagnostics,
                },
            });
        }
        catch (Exception ex) { return Serialize(new { ok = false, code = "INVALID_TARGET", targetId, summary = ex.Message }); }
    }

    private async Task<string> ValidateCompositionAsync(PublishAssistantContext context, Guid editionId, Guid variantId)
    {
        try
        {
            var result = await (compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable.")).ValidateVariantAsync(context.ProjectId, editionId, variantId, context.TurnCancellationToken);
            return Serialize(new { ok = result.ErrorCount == 0, targetId = result.TargetId, revision = result.Revision, summary = $"Validation found {result.ErrorCount} error(s) and {result.WarningCount} warning(s).", diagnosticCounts = new { errors = result.ErrorCount, warnings = result.WarningCount }, diagnostics = result.Diagnostics });
        }
        catch (Exception ex) { return Serialize(new { ok = false, code = "VALIDATION_FAILED", targetId = variantId, summary = ex.Message }); }
    }

    private async Task<string> GetOrCreateCompositionVariantAsync(PublishAssistantContext context, Guid compositionId, Guid editionId)
    {
        try { var service = compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable."); var variant = await service.GetOrCreateVariantAsync(context.ProjectId, compositionId, editionId, context.TurnCancellationToken); return Serialize(new { ok = true, targetId = variant.Id, variantId = variant.Id, revision = variant.Revision, summary = "Release layout copied from the authoring layout and is ready for review.", mutation = new { kind = "pageComposition", id = compositionId, variantId = variant.Id } }); }
        catch (Exception ex) { return Serialize(new { ok = false, code = "INVALID_TARGET", targetId = compositionId, summary = ex.Message }); }
    }

    private async Task<string> StageCompositionAsync(PublishAssistantContext context, Guid variantId, long expectedRevision, CompositionScene scene)
    {
        try { var service = compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable."); var stage = await service.StageVariantAsync(context.ProjectId, context.ConversationId, variantId, expectedRevision, scene, context.TurnCancellationToken); return Serialize(new { ok = true, targetId = variantId, revision = expectedRevision, summary = $"Validated {scene.Objects.Count} composition object(s).", stageId = stage.Id, stage.ExpiresAt, diagnosticCounts = new { errors = 0, warnings = 0 } }); }
        catch (CompositionRevisionConflictException ex) { return Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = variantId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread the compact composition, preserve unrelated objects, and stage a new scene once." }); }
        catch (Exception ex) { return Serialize(new { ok = false, code = "INVALID_SCENE", targetId = variantId, summary = ex.Message }); }
    }

    private async Task<string> ApplyCompositionStageAsync(PublishAssistantContext context, Guid editionId, Guid stageId, long expectedRevision)
    {
        try { var service = compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable."); var variant = await service.ApplyStageAsync(context.ProjectId, context.ConversationId, stageId, expectedRevision, context.TurnCancellationToken); return Serialize(new { ok = true, targetId = variant.Id, releaseId = editionId, variantId = variant.Id, revision = variant.Revision, summary = "Staged composition applied.", changedIds = new[] { variant.Id }, mutation = new { kind = "pageComposition", id = variant.CompositionId, variantId = variant.Id } }); }
        catch (CompositionRevisionConflictException ex) { return Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = stageId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread and submit a replacement stage." }); }
        catch (Exception ex) { return Serialize(new { ok = false, code = "STAGE_REJECTED", targetId = stageId, summary = ex.Message }); }
    }

    private async Task<string> StageCompositionSemanticAsync(PublishAssistantContext context, Guid compositionId, long expectedRevision, ManuscriptOperationInput[] operations)
    {
        try { var service = compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable."); var stage = await service.StageSemanticOperationsAsync(context.ProjectId, context.ConversationId, compositionId, expectedRevision, operations, context.TurnCancellationToken); return Serialize(new { ok = true, targetId = compositionId, revision = expectedRevision, stageId = stage.Id, stage.ExpiresAt, summary = $"Validated {operations.Length} semantic operation(s)." }); }
        catch (CompositionRevisionConflictException ex) { return Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = compositionId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread the bounded composition and submit a replacement stage." }); }
        catch (Exception ex) { return Serialize(new { ok = false, code = "SEMANTIC_STAGE_REJECTED", targetId = compositionId, summary = ex.Message }); }
    }

    private async Task<string> ApplyCompositionSemanticStageAsync(PublishAssistantContext context, Guid editionId, Guid stageId, long expectedRevision)
    {
        try { var service = compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable."); var result = await service.ApplySemanticStageAsync(context.ProjectId, context.ConversationId, stageId, expectedRevision, context.TurnCancellationToken); return Serialize(new { ok = true, targetId = result.Composition.Id, releaseId = editionId, revision = result.Composition.Revision, changedIds = result.ChangedBlockIds, summary = "Staged Designed Page content applied.", mutation = new { kind = "pageComposition", id = result.Composition.Id } }); }
        catch (CompositionRevisionConflictException ex) { return Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = stageId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread and submit a new non-replayed stage." }); }
        catch (Exception ex) { return Serialize(new { ok = false, code = "STAGE_REJECTED", targetId = stageId, summary = ex.Message }); }
    }

    private async Task<string> StageCompositionWorkspaceAsync(PublishAssistantContext context, Guid compositionId, long expectedCompositionRevision, Guid variantId, long expectedVariantRevision, ManuscriptOperationInput[] semanticOperations, CompositionScene scene)
    {
        try { var service = compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable."); var stage = await service.StageWorkspaceAsync(context.ProjectId, context.ConversationId, compositionId, expectedCompositionRevision, variantId, expectedVariantRevision, semanticOperations, scene, context.TurnCancellationToken); return Serialize(new { ok = true, targetId = compositionId, revision = expectedCompositionRevision, stageId = stage.Id, stage.ExpiresAt, summary = $"Validated {semanticOperations.Length} semantic operation(s) with {scene.Objects.Count} scene object(s)." }); }
        catch (CompositionRevisionConflictException ex) { return Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = compositionId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread the compact workspace and submit one replacement stage." }); }
        catch (Exception ex) { return Serialize(new { ok = false, code = "WORKSPACE_STAGE_REJECTED", targetId = compositionId, summary = ex.Message }); }
    }

    private async Task<string> ApplyCompositionWorkspaceStageAsync(PublishAssistantContext context, Guid editionId, Guid stageId, long expectedCompositionRevision)
    {
        try { var service = compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable."); var result = await service.ApplyWorkspaceStageAsync(context.ProjectId, context.ConversationId, stageId, expectedCompositionRevision, context.TurnCancellationToken); return Serialize(new { ok = true, targetId = result.Composition.Id, releaseId = editionId, revision = result.Composition.Revision, variantId = result.Variant.Id, variantRevision = result.Variant.Revision, changedIds = result.ChangedBlockIds, summary = "Designed Page content and layout applied atomically.", mutation = new { kind = "pageComposition", id = result.Composition.Id, variantId = result.Variant.Id } }); }
        catch (CompositionRevisionConflictException ex) { return Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = stageId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread the compact workspace and submit a new non-replayed stage." }); }
        catch (Exception ex) { return Serialize(new { ok = false, code = "WORKSPACE_STAGE_REJECTED", targetId = stageId, summary = ex.Message }); }
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

    private async Task<string> ReorderContentAsync(
        PublishAssistantContext context,
        Guid editionId,
        PublicationEditionOutlineItemOrder[] orderedItems,
        long expectedRevision) =>
        Serialize(await editions.ReorderOutlineAsync(
            context.ProjectId,
            editionId,
            orderedItems,
            expectedRevision));

    private async Task<string> UpsertMatterAsync(
        PublishAssistantContext context,
        Guid editionId,
        PublicationMatterInput input,
        long expectedRevision) =>
        Serialize(await editions.UpsertMatterAsync(context.ProjectId, editionId, input, expectedRevision));

    private async Task<string> DeleteMatterAsync(
        PublishAssistantContext context,
        Guid editionId,
        Guid matterId,
        long expectedRevision)
    {
        await editions.DeleteMatterAsync(context.ProjectId, editionId, matterId, expectedRevision);
        return """{"status":"deleted"}""";
    }

    private async Task<string> UpsertStyleMappingAsync(
        PublishAssistantContext context,
        Guid editionId,
        PublicationEditionStyleMappingInput input,
        long expectedRevision) =>
        Serialize(await editions.UpsertStyleMappingAsync(context.ProjectId, editionId, input, expectedRevision));

    private async Task<string> DeleteStyleMappingAsync(
        PublishAssistantContext context,
        Guid editionId,
        Guid mappingId,
        long expectedRevision)
    {
        await editions.DeleteStyleMappingAsync(context.ProjectId, editionId, mappingId, expectedRevision);
        return """{"status":"deleted"}""";
    }

    private async Task<string> AddPlacementAsync(
        PublishAssistantContext context,
        Guid editionId,
        PublicationImagePlacementCreate input,
        long expectedRevision) =>
        Serialize(await editions.AddImagePlacementAsync(
            context.ProjectId,
            editionId,
            input,
            expectedRevision));

    private async Task<string> UpdatePlacementAsync(
        PublishAssistantContext context,
        Guid editionId,
        Guid placementId,
        PublicationImagePlacementUpdate input,
        long expectedRevision) =>
        Serialize(await editions.UpdateImagePlacementAsync(
            context.ProjectId,
            editionId,
            placementId,
            input,
            expectedRevision));

    private async Task<string> ReorderPlacementsAsync(
        PublishAssistantContext context,
        Guid editionId,
        Guid[] ids,
        long expectedRevision)
    {
        await editions.ReorderImagePlacementsAsync(context.ProjectId, editionId, ids, expectedRevision);
        return """{"status":"reordered"}""";
    }

    private async Task<string> DeletePlacementAsync(
        PublishAssistantContext context,
        Guid editionId,
        Guid placementId,
        long expectedRevision)
    {
        await editions.DeleteImagePlacementAsync(context.ProjectId, editionId, placementId, expectedRevision);
        return """{"status":"deleted"}""";
    }

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
        var stage = await books.StageCoverSceneAsync(context.ProjectId, context.ConversationId,
            expectedBookRevision, expectedCoverRevision, scene, context.TurnCancellationToken);
        return Serialize(new { ok = true, target = "core", targetId = context.ProjectId,
            revision = expectedCoverRevision, stageId = stage.Id, stage.ExpiresAt,
            summary = $"Staged {scene.Objects.Count} Core cover objects across {scene.Layers.Count} layers." });
    }

    private async Task<string> ApplyCoreCoverCompositionStageAsync(
        PublishAssistantContext context,
        Guid stageId,
        long expectedBookRevision,
        long expectedCoverRevision)
    {
        var cover = await books.ApplyCoverSceneStageAsync(context.ProjectId, context.ConversationId,
            stageId, expectedBookRevision, expectedCoverRevision, context.TurnCancellationToken);
        return Serialize(new { ok = true, target = "core", targetId = context.ProjectId,
            revision = cover.Revision, bookRevision = cover.CoreBookRevision,
            changedFields = new[] { "compositionScene" }, diagnosticCount = cover.Diagnostics.Count,
            diagnostics = cover.Diagnostics.Take(5),
            mutation = new { kind = "core-cover", refresh = new[] { "core", "covers", "readiness", "artifacts" } } });
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
        var stage = await covers.StageSceneAsync(
            context.ProjectId,
            context.ConversationId,
            editionId,
            expectedRevision,
            scene,
            context.TurnCancellationToken);
        return Serialize(new
        {
            ok = true,
            targetId = editionId,
            revision = expectedRevision,
            stageId = stage.Id,
            stage.ExpiresAt,
            summary = $"Staged {scene.Objects.Count} cover objects across {scene.Layers.Count} layers.",
        });
    }

    private async Task<string> ApplyCoverCompositionStageAsync(
        PublishAssistantContext context,
        Guid stageId,
        long expectedRevision)
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
