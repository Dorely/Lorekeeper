using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lorekeeper.Chapters;
using Lorekeeper.Composition;
using Lorekeeper.Context;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Fonts;
using Lorekeeper.Images;
using Lorekeeper.Ingest;
using Lorekeeper.Llm;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;
using Lorekeeper.Publish;
using Lorekeeper.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Lorekeeper.EditorChat;

public sealed class EditorChatTools(
IAppDatabaseOperationFactory database, IActService acts,
    IChapterService chapters,
    IManuscriptService manuscripts,
    IManuscriptAnnotationService annotations,
    IManuscriptMigrationService manuscriptMigrations,
    IManuscriptStyleService manuscriptStyles,
    IProjectFontService projectFonts,
    IEntityService entities,
    IEntityTypeService entityTypes,
    IProjectFactService projectFacts,
    IEditorContextService editorContext,
    IEntityRelationContextService entityRelations,
    IProjectSearchService projectSearch,
    IReferenceVisualService referenceVisuals,
    IEditorRevisionAgentService revisionAgents,
    OutlineCollaborationTools outlineTools,
    IProjectImageService projectImages,
    IEntityVisualExampleService entityVisualExamples,
    IProjectImageJobService imageJobs,
    IAgentProjectImageWorkflow imageWorkflow,
    ICompositionService compositions,
    IProjectPageSetupService pageSetups,
    IChapterPreviewService chapterPreviews,
    ICompositionCanvasPreviewService canvasPreviews,
    IChapterSemanticProjectionService semanticProjection,
    EditorManuscriptApplyService manuscriptApplies,
    IOptions<EditorChatOptions> editorOptions,
    IOptions<ProjectImageGenerationOptions> imageOptions)
{
    private static readonly EntityRelationContextOptions _listEntityRelationOptions = new()
    {
        Depth = 2,
        MaxDirectLinks = 8,
        MaxTraversalPaths = 10,
        MaxLinksPerNode = 8,
    };

    private static readonly EntityRelationContextOptions _detailEntityRelationOptions = new()
    {
        Depth = 2,
        MaxDirectLinks = 16,
        MaxTraversalPaths = 24,
        MaxLinksPerNode = 10,
    };

    public async Task<IList<AITool>> BuildAsync(
        EditorChatContext context,
        EditorChatToolMode mode = EditorChatToolMode.Normal,
        CancellationToken cancellationToken = default)
    {
        var tools = new List<AITool>();
        var impactDescription =
            "Read-only book-level impact map for continuity changes. Use this before choosing prose targets for an explicit book-wide or multi-chapter request. "
            + "Search expected source content, not edit instructions: use one 2–6-term facet per call and keep combined query plus keywords under ten high-signal terms. Keywords are optional exact aliases or terms expected to coexist with the query; put alternatives in separate calls. "
            + "Resolve exact chapter, entity, and event IDs first and include only directly affected IDs. anchorChapterId is forward-only for a known originating chapter: omit it for backward, whole-book, bidirectional, or direction-neutral impact. "
            + "Combine and deduplicate facet candidates yourself, then verify them with focused search_project and read_chapter calls before assigning revisions. An exact downstream read that demonstrates the requested consequence is sufficient even without a lexical hit; reject anchor-only, generic-term-only, and opaque-score-only candidates. "
            + (mode == EditorChatToolMode.Normal
                ? "Do not silently narrow explicit user scope. Classify the complete verified set before any manuscript mutation. For three or more verified semantic prose chapters, make one start_revision_agents call containing the complete set; do not edit the first targets directly. For one or two, direct manuscript tools are the default unless the user explicitly requests one worker call for two. Put the complete requested change in chapter-specific worker instructions after retrieval. "
                : "Contest Mode has no start_revision_agents tool; use this map only as read-only grounding for the single-chapter start_contest workflow, whose captured conversation retains the complete request. ")
            + "The map combines outline order, chapter synopses, server-side keyword/body checks, hybrid project-search hits, affected entities/events, adjacency, and downstream chapters.";

        tools.Add(AIFunctionFactory.Create(
            method: (int offset = 0, int limit = 50) => ListManuscriptAnnotationsAsync(context, offset, limit),
            name: "list_manuscript_annotations",
            description: "List open review annotations across the protected selected Core/release target in explicit pages. Returns annotation IDs, chapter IDs, current/outdated state, note text, bounded quoted text, revisions, and pagination."));
        if (mode == EditorChatToolMode.Normal)
        {
            tools.Add(AIFunctionFactory.Create(
                method: (Guid annotationId, long expectedRevision) => CompleteManuscriptAnnotationAsync(context, annotationId, expectedRevision),
                name: "complete_manuscript_annotation",
                description: "Permanently complete one user review annotation in the protected selected Core/release target. Use only after applying the requested manuscript edit or when the user explicitly instructs you to complete it. This cannot create or rewrite user note text. With Review edits enabled, completion is staged and depends on the corresponding staged manuscript edit when one exists."));
        }

        tools.AddRange([
            AIFunctionFactory.Create(
                method: (string? query = null, string[]? sourceTypes = null, int topK = 10) =>
                    ListSearchSourcesAsync(context, query, sourceTypes, topK),
                name: "list_search_sources",
                description: "Return compact current-project and direct-reference source discovery with origin project provenance and exact read_project_source arguments. References are read-only continuity evidence."),

            AIFunctionFactory.Create(
                method: (string sourceType, Guid sourceId, int? pageNumber = null, Guid? originProjectId = null) =>
                    ReadProjectSourceAsync(context, sourceType, sourceId, pageNumber, originProjectId),
                name: "read_project_source",
                description: "Read one paginated active-project or direct-reference source. Pass the exact originProjectId returned by discovery; arbitrary foreign IDs are rejected."),

            AIFunctionFactory.Create(
                method: (
                    string query,
                    int topK = 8,
                    string[]? sourceTypes = null,
                    string[]? sourceIds = null,
                    Guid? containerSourceId = null,
                    bool lexicalOnly = false) =>
                    SearchProjectAsync(context, query, topK, sourceTypes, sourceIds, containerSourceId, lexicalOnly),
                name: "search_project",
                description: "Hybrid keyword + semantic compact discovery across the active project and direct references, with origin provenance and exact origin-qualified read arguments. References are read-only."),

            AIFunctionFactory.Create(
                method: () => ListReferenceVisualsAsync(context),
                name: "list_reference_visuals",
                description: "List canonical entity visuals from direct referenced projects only with project/entity/image provenance."),

            AIFunctionFactory.Create(
                method: (Guid originProjectId, Guid imageId) => ReadReferenceVisualAsync(context, originProjectId, imageId),
                name: "read_reference_visual",
                description: "Read one canonical visual attached to a direct referenced project. Arbitrary foreign or general-library images fail closed; bytes are model-only and never valid for mutation or placement."),

            AIFunctionFactory.Create(
                method: () => ListChaptersAsync(context),
                name: "list_chapters",
                description: "List every format-neutral chapter in the current project with id, order, title, synopsis, body line count, and read_chapter page count."),

            AIFunctionFactory.Create(
                method: (
                    string query,
                    Guid? anchorChapterId = null,
                    Guid[]? affectedEntityIds = null,
                    Guid[]? eventIds = null,
                    string[]? keywords = null,
                    int topK = 20) =>
                    FindImpactedChaptersAsync(context, query, anchorChapterId, affectedEntityIds, eventIds, keywords, topK),
                name: "find_impacted_chapters",
                description: impactDescription),

            AIFunctionFactory.Create(
                method: () => ListProjectFactsAsync(context),
                name: "list_project_facts",
                description: "Read project-level fact ids, keys/values, linked entity ids, and relation context as JSON. The fact text is already in the Context Feed; use this for structured ids, linked-entity grounding, or fact-change verification."),

            AIFunctionFactory.Create(
                method: (string query, int topK = 10, string? type = null, string? parentId = null) =>
                    SearchEntitiesAsync(context, query, topK, type, parentId),
                name: "search_entities",
                description: "Compact entity discovery with full IDs, total/returned counts, completeness, labeled previews, and exact read_entity arguments. Review mode includes staged state."),

            AIFunctionFactory.Create(
                method: (Guid entityId, int? pageNumber = null) => ReadEntityAsync(context, entityId, pageNumber),
                name: "read_entity",
                description: "Read one explicitly paginated graph entity by id, including properties, structured wiki data, all adjacent manual and AutoMention links, bounded relation context, and visible thumbnail chips for attached canonical visual references. Full identity fields and GUIDs are repeated on every page. Omit pageNumber for page 1 and follow nextPageArguments. When Review edits is enabled, returns the latest staged entity and link state from this turn. In normal editor chat, this also adds the entity's data—but not its graph relationships—to the active chapter's Context Feed."),

            AIFunctionFactory.Create(
                method: (Guid entityId, int? pageNumber = null) => ListEntityLinksAsync(context, entityId, pageNumber),
                name: "list_entity_links",
                description: "List explicitly paginated graph links adjacent to an entity, including structural HasChild links and semantic story relationships. Full entity identity is repeated on every page; follow nextPageArguments until pagination.isComplete or hasNextPage is false. When Review edits is enabled, includes staged entity and link changes from this turn."),

            AIFunctionFactory.Create(
                method: (Guid entityId) => ListEntityVisualExamplesAsync(context, entityId),
                name: "list_entity_canonical_references",
                description: "List the ordered canonical visual references attached to one entity and show them as visible thumbnail chips. Full image bytes are supplied on the next iteration when the provider is vision-ready."),

            AIFunctionFactory.Create(
                method: (Guid chapterId, int? pageNumber = null) =>
                    ReadChapterAsync(context, chapterId, pageNumber),
                name: "read_chapter",
                description:
                    "Read one paginated page of a chapter's current body with line numbers (0001: ...). " +
                    "Use chapter ids from the Context Feed outline when available; use list_chapters for missing ids, line counts, and page counts. " +
                    "Provide pageNumber to read a specific page of the full chapter; omit it to read page 1. " +
                    "Always returns content plus pagination metadata. If this turn already staged an edit to the chapter, returns the latest staged body for this turn."),

            AIFunctionFactory.Create(
                method: (Guid chapterId, string? blockId = null, int? pageNumber = null) =>
                    PreviewChapterPageAsync(context, chapterId, blockId, pageNumber),
                name: "preview_chapter_page",
                description:
                    "Render one Press-typeset chapter page as a PNG for visual inspection. Provide exactly one of blockId or pageNumber. " +
                    "blockId is a stable semantic manuscript block ID and selects the first typeset page containing that block; pageNumber is 1-based chapter-local typeset pagination, not read_chapter text pagination. " +
                    "The image is attached to the turn and, when vision is available, supplied to the model on the next iteration."),

            AIFunctionFactory.Create(
                method: (Guid compositionId, Guid variantId, string mode = "annotated") =>
                    PreviewPageCanvasAsync(context, compositionId, variantId, mode),
                name: "preview_page_canvas",
                description:
                    "Render one complete Designed Page authoring surface directly as a transient PNG, without chapter pagination or publication-edition context. " +
                    "Use mode 'annotated' while arranging objects to see safe area, gutter, object IDs, clipping, and overflow; use mode 'clean' for final visual verification. " +
                    "A facing spread is returned as one complete wide surface. The preview is attached to the turn and never creates a project-image asset."),

            AIFunctionFactory.Create(
                method: (Guid chapterId, int startBlock = 0, int blockCount = 40) =>
                    ReadManuscriptAsync(context, chapterId, startBlock, blockCount),
                name: "read_manuscript",
                description:
                    "Read bounded agent-manuscript-v1 semantic rows with stable block IDs, exact text, sparse structure, UTF-16 inline marks, interned paragraph formatting, Figure/Designed Page metadata, source hash, and the current revision token. " +
                    "The active Context Feed normally already includes the complete current manuscript snapshot for direct edits. Use this tool when that snapshot is missing, incomplete, stale, non-active, or insufficient; then pass the returned revision and operations once to apply_manuscript_operations. After a mutation returns requiresReadback=true, call this tool for every exact readbackRanges entry and require the returned revision and sourceHash to match before continuing. For reusable formatting, use the focused Book Text Style tools instead of emitting one operation per block."),

            AIFunctionFactory.Create(
                method: (Guid chapterId, string? query = null, string? blockType = null, string? styleRole = null, int start = 0, int count = 40) =>
                    InspectManuscriptAsync(context, chapterId, query, blockType, styleRole, start, count),
                name: "inspect_manuscript",
                description:
                    "Validate a manuscript and structurally search all blocks by optional text, blockType, and semantic styleRole. " +
                    "Returns at most 40 matching agent-manuscript-v1 rows plus bounded normalization/schema diagnostics, total counts, start, and hasMore. Review mode inspects the current staged manuscript."),

            AIFunctionFactory.Create(
                method: (Guid chapterId, long expectedRevision, ManuscriptOperationInput[] operations) =>
                    manuscriptApplies.ApplyAsync(context, chapterId, expectedRevision, operations),
                name: "apply_manuscript_operations",
                description:
                    "Validate and apply one complete semantic manuscript operation set in a single revision-checked call. " + ManuscriptOperationInput.ToolOperationGuidance + " Use apply_manuscript_style for chapter-wide or repeated reusable styling. " +
                    "Submit chapterId, expectedRevision, and each intended operation payload exactly once; in Review Edits mode the result is staged for approval, otherwise it is persisted immediately. " +
                    "The result returns operation and block counts, diagnostics, revision, full sourceHash, and exact readbackRanges. When requiresReadback=true, read every returned range and verify the revision, hash, ordering, and absence of superseded prose before continuing. A correction is a fresh operation set against that verified revision, never a replay of the earlier payload. Stale revisions and invalid operations fail closed."),

            AIFunctionFactory.Create(
                method: () => ReadManuscriptMigrationStateAsync(context),
                name: "read_manuscript_migration_state",
                description:
                    "Read structured-manuscript migration, validation journal, protected backup, and recovery state. Read-only; never treats an incomplete migration as successful."),

            AIFunctionFactory.Create(
                method: () => ListBookFontsAsync(context),
                name: "list_book_fonts",
                description:
                    "List the compact project-owned font catalog with stable family keys and available weights/styles. Read this before choosing an exact font family for direct paragraph formatting or a Book Text Style; never invent a font key."),
            AIFunctionFactory.Create(
                method: () => ListManuscriptStylesAsync(context),
                name: "list_manuscript_styles",
                description:
                    "Read every project named paragraph and character style, including stable ID, semantic role, validated definition, and revision token."),

            AIFunctionFactory.Create(
                method: () => ListProjectImagesAsync(context),
                name: "list_project_images",
                description: "List the project image library with ids, filenames, alt text, source, prompt, model, size, and preview URL. Read-only and available in Contest preparation."),

            AIFunctionFactory.Create(
                method: () => ReadProjectPageSetupAsync(context),
                name: "read_project_page_setup",
                description: "Read the project-owned authoring page geometry and revision used by chapter preview, Figures, Designed Pages, and target-bound image generation. Publication releases do not govern authoring geometry."),

            AIFunctionFactory.Create(
                method: (Guid imageId) => ReadProjectImageAsync(context, imageId),
                name: "read_project_image",
                description: "Read one project image's metadata and expose it as explicit visual context. If the active provider is vision-ready, the image bytes are supplied to the model on the next iteration."),

            AIFunctionFactory.Create(
                method: (Guid chapterId, int start = 0, int count = 30) =>
                    ListManuscriptVisualsAsync(context, chapterId, start, count),
                name: "list_manuscript_visuals",
                description: "List a bounded page of Figure and DesignedPage blocks in one format-neutral chapter. Returns stable block and target IDs, current manuscript revision, compact presentation/accessibility summaries, counts, diagnostics, and continuation metadata."),

            AIFunctionFactory.Create(
                method: (Guid compositionId, Guid variantId, int semanticStart = 0, int semanticCount = 20, int objectStart = 0, int objectCount = 30, int structureStart = 0, int structureCount = 30) =>
                    ReadPageCompositionAsync(context, compositionId, variantId, semanticStart, semanticCount, objectStart, objectCount, structureStart, structureCount),
                name: "read_page_composition",
                description: "Read one selected page-composition variant losslessly in bounded object pages. Returns its complete surface, layers, styles, object fields, semantic excerpts, revisions, and continuation metadata; computed page overlays and image bytes are omitted."),
        ]);

        if (mode == EditorChatToolMode.ContestPreparation)
        {
            tools.Add(AIFunctionFactory.Create(
                method: (Guid chapterId) => StartContestAsync(context, chapterId),
                name: "start_contest",
                description:
                    "Start a Contest Mode generation job for chapter-body mutations. " +
                    "Call this exactly once after gathering enough read-only context. "));
            return tools;
        }

        tools.AddRange([
            AIFunctionFactory.Create(
                method: (long expectedRevision, double pageWidthInches, double pageHeightInches, double pageMarginInches, double bodyFontSizePoints, double bodyLineHeight) =>
                    UpdateProjectPageSetupAsync(context, expectedRevision, pageWidthInches, pageHeightInches, pageMarginInches, bodyFontSizePoints, bodyLineHeight),
                name: "update_project_page_setup",
                description: "Revision-check the project authoring page setup. Execute direct user instructions proactively; preserve omitted decisions by first reading the current setup and passing its unchanged values."),

            AIFunctionFactory.Create(
                method: (Guid chapterId, long expectedRevision, int index, Guid imageId, string? caption, string? altText, bool decorative, string? language, FigureAccessibilityRole accessibilityRole, FigurePresentation presentation) =>
                    InsertFigureAsync(context, chapterId, expectedRevision, index, imageId, caption, altText, decorative, language, accessibilityRole, presentation),
                name: "insert_manuscript_figure",
                description: "Insert one project image as a revision-checked Figure block. Supply alt text or an explicit decorative decision. Returns only the new revision and changed block ID."),
            AIFunctionFactory.Create(
                method: (Guid chapterId, long expectedRevision, string blockId, Guid imageId, string? caption, string? altText, bool decorative, string? language, FigureAccessibilityRole accessibilityRole, FigurePresentation presentation) =>
                    PatchFigureAsync(context, chapterId, expectedRevision, blockId, imageId, caption, altText, decorative, language, accessibilityRole, presentation),
                name: "patch_manuscript_figure",
                description: "Revision-check replace or reformat one stable Figure while preserving every unrelated manuscript block. Omit caption to preserve it."),
            AIFunctionFactory.Create(
                method: (Guid chapterId, long expectedRevision, int blockIndex, string name, DesignedPageLayoutMode layoutMode = DesignedPageLayoutMode.SinglePage) =>
                    InsertDesignedPageAsync(context, chapterId, expectedRevision, blockIndex, name, layoutMode),
                name: "insert_manuscript_designed_page",
                description: "Insert an empty single-page or facing-spread Designed Page into a chapter at an exact manuscript revision. Add completed project images afterward through separate focused placement tools."),
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
                    UpsertManuscriptStyleAsync(
                        context,
                        styleId,
                        name,
                        kind,
                        expectedRevision,
                        fontFamilyKey,
                        fontSizePoints,
                        fontWeight,
                        italic,
                        smallCaps,
                        lineHeight,
                        spaceBeforePoints,
                        spaceAfterPoints,
                        keepWithNext,
                        textAlign,
                        leftIndentEm,
                        rightIndentEm,
                        firstLineIndentEm,
                        startOnNewPage),
                name: "upsert_manuscript_style",
                description:
                    "Create or revision-check update a reusable paragraph or character Book Text Style from a compact definition. Lorekeeper owns the internal semantic key. Paragraph styles can define font treatment, spacing, Start/Center/End/Justify alignment, whole-paragraph and first-line or hanging indents, and page-start behavior. Use list_manuscript_styles first; updates require styleId and expectedRevision."),
            AIFunctionFactory.Create(
                method: (Guid chapterId, long expectedRevision, string blockId, string name) =>
                    CreateParagraphStyleFromBlockAsync(context, chapterId, expectedRevision, blockId, name),
                name: "create_paragraph_style_from_block",
                description:
                    "Create a reusable paragraph Book Text Style by extracting one styled paragraph, heading, block quote, or list item. Reads the existing style plus direct paragraph formatting server-side, so do not resend text, marks, or formatting. The source chapter revision must be current."),
            AIFunctionFactory.Create(
                method: (Guid styleId, Guid chapterId, long expectedRevision, string[]? blockIds = null) =>
                    ApplyManuscriptStyleAsync(context, styleId, chapterId, expectedRevision, blockIds),
                name: "apply_manuscript_style",
                description:
                    "Apply one saved paragraph Book Text Style without sending per-block style operations. Omit blockIds to style every paragraph, heading, block quote, and list item in the chapter; provide stable block IDs for a focused application. Direct paragraph overrides are cleared so the saved style controls appearance. Figures, Designed Pages, and scene breaks are preserved. Use one compact call per chapter."),
            AIFunctionFactory.Create(
                method: (Guid styleId, long expectedRevision) =>
                    DeleteManuscriptStyleAsync(context, styleId, expectedRevision),
                name: "delete_manuscript_style",
                description:
                    "Delete a Book Text Style through the shared revision-checked service. Use list_manuscript_styles first."),
            AIFunctionFactory.Create(
                method: (Guid entityId, Guid imageId, string? label = null) => AttachProjectImageToEntityAsync(context, entityId, imageId, label),
                name: "attach_entity_canonical_reference",
                description: "Attach one isolated, stable appearance or design image to an eligible story entity as an ordered canonical reference. Use a concise role label such as 'default appearance', 'winter outfit', or 'exterior view'. Do not attach an ordinary narrative scene merely because the entity appears in it."),
            AIFunctionFactory.Create(
                method: (Guid canonicalReferenceId, string label, int? sortOrder = null) => UpdateEntityVisualExampleAsync(context, canonicalReferenceId, label, sortOrder),
                name: "update_entity_canonical_reference",
                description: "Update an entity canonical visual reference's role label and optionally its zero-based order."),
            AIFunctionFactory.Create(
                method: (Guid canonicalReferenceId) => DetachProjectImageFromEntityAsync(context, canonicalReferenceId),
                name: "detach_entity_canonical_reference",
                description: "Detach one canonical visual reference without deleting the project image."),
            AIFunctionFactory.Create(
                method: (Guid sourceImageId, ProjectImageCropRegion crop, string? fileName = null, string? altText = null, EntityVisualTarget? entityTarget = null) =>
                    CropProjectImageAsync(context, sourceImageId, crop, fileName, altText, entityTarget),
                name: "crop_project_image",
                description: "Create a non-destructive project-library crop from an existing image using 0-100 percentage coordinates. Inspect the source first or use user-supplied coordinates and describe only the cropped subject in altText. Optionally attach the tight subject-only crop to one entity as its canonical reference; make separate crops for separate entities. Source associations are never inherited."),
    ]);

        tools.Add(AIFunctionFactory.Create(
            method: (ImageGenerationBrief brief, ImageReferenceUse[]? references = null, ImageGenerationTarget? target = null, string? altText = null, string? quality = null, string? outputFormat = null, int? outputCompression = null) =>
                GenerateProjectImageAsync(context, brief, references, target, altText, quality, outputFormat, outputCompression),
            name: "generate_project_image",
            description:
                $"Generate one unattached project image and wait for a terminal result. intendedUse and scene are required. A target supplies geometry guidance only and never places the output. Inspect the returned image, then use its project-image ID in a separate Figure or Designed Page placement tool during this turn. At most {Math.Max(0, imageOptions.Value.MaxReferenceImages)} references are allowed."));

        tools.Add(AIFunctionFactory.Create(
            method: (Guid imageId, string label, ProjectImageMaskShape[] shapes) =>
                CreateShapeMaskAsync(context, imageId, label, shapes),
            name: "create_shape_mask",
            description: "Create a reusable PNG edit mask for an existing project image from percentage-based rect, ellipse, or polygon shapes. Transparent pixels are editable regions."));

        tools.Add(AIFunctionFactory.Create(
            method: (Guid sourceImageId, ImageEditBrief brief, Guid? maskId = null, ProjectImageMaskShape[]? maskShapes = null, string? maskLabel = null, ImageReferenceUse[]? references = null, ImageGenerationTarget? target = null, string? altText = null, string? quality = null, string? outputFormat = null, int? outputCompression = null) =>
                EditProjectImageAsync(context, sourceImageId, brief, maskId, maskShapes, maskLabel, references, target, altText, quality, outputFormat, outputCompression),
            name: "edit_project_image",
            description:
                $"Edit one project image and wait for a terminal result. The result is always a new unattached project image. Geometry guidance never places it. Inspect the returned image, then apply its ID with a separate placement tool when requested. At most {Math.Max(0, imageOptions.Value.MaxReferenceImages)} references are allowed."));

        tools.Add(AIFunctionFactory.Create(
            method: (Guid jobId) => ReadProjectImageJobAsync(context, jobId, wait: false),
            name: "read_project_image_job",
            description: "Read compact status for an existing project-image job without replaying its prompt."));
        tools.Add(AIFunctionFactory.Create(
            method: (Guid jobId) => ReadProjectImageJobAsync(context, jobId, wait: true),
            name: "wait_project_image_job",
            description: "Reconnect to an existing project-image job and wait for terminal output without replaying its prompt."));
        tools.Add(AIFunctionFactory.Create(
            method: (Guid jobId) => CancelProjectImageJobAsync(context, jobId),
            name: "cancel_project_image_job",
            description: "Cancel one queued or running project-image job. No image is placed."));

        tools.AddRange([
            AIFunctionFactory.Create(
                method: (string targetKind, Guid targetId, Guid? variantId = null) =>
                    ReadLayoutGenerationTargetAsync(context, targetKind, targetId, variantId),
                name: "read_layout_generation_target",
                description: "Read exact project-authoring geometry, moderate requested raster, print-DPI recommendation, and protected regions for a project page, Figure placement, or Designed Page frame/surface. Use the project ID for project-page; composition targets require the active variantId."),
            AIFunctionFactory.Create(
                method: (Guid compositionId) =>
                    GetOrCreateCompositionVariantAsync(context, compositionId),
                name: "get_or_create_page_composition_variant",
                description: "Get or create the exact layout variant for the selected Editor target. Core uses project authoring geometry; release mode uses that release's geometry."),
            AIFunctionFactory.Create(
                method: (Guid variantId, long expectedRevision, string targetKind, Guid targetId, CompositionElementPatch patch) =>
                    PatchCompositionElementAsync(context, variantId, expectedRevision, targetKind, targetId, patch),
                name: "patch_page_composition_element",
                description: "Revision-check patch one stable object, layer, or style without resending or replacing the scene. Page guides are computed overlays. Send only changed fields; use one-use staging only for large structural edits."),
            AIFunctionFactory.Create(
                method: (Guid variantId, long expectedRevision, Guid targetId, bool retainAspectRatio = true) =>
                    FillPageImageCanvasAsync(context, variantId, expectedRevision, targetId, retainAspectRatio),
                name: "fill_page_image_canvas",
                description: "Make one Designed Page image cover the complete canvas. With retainAspectRatio=true, this uses proportional crop-to-fill (Cover) so no edge bands remain; false stretches the raster. Use read_page_composition afterward and verify imageCoversCanvas."),
            AIFunctionFactory.Create(
                method: (Guid variantId, long expectedRevision, Guid targetId, Guid imageId, FigureImageFit fit, string? altText, bool decorative, int? readingOrder = null) =>
                    PlacePageImageAsync(context, variantId, expectedRevision, targetId, imageId, fit, altText, decorative, readingOrder),
                name: "place_project_image_in_page_frame",
                description: "Place an existing project-image ID into an existing Designed Page image frame. Contain and Cover retain aspect ratio; Stretch permits distortion. Requires an alt-text or decorative decision. This never generates an image."),
            AIFunctionFactory.Create(
                method: (Guid variantId, long expectedRevision, Guid imageId, FigureImageFit fit, string? altText, bool decorative, CompositionBounds? bounds = null, int? readingOrder = null) =>
                    AddPageImageAsync(context, variantId, expectedRevision, imageId, fit, altText, decorative, bounds, readingOrder),
                name: "add_project_image_to_page",
                description: "Add a new image object to a Designed Page using an existing project-image ID. Contain and Cover retain aspect ratio; Stretch permits distortion. Requires an alt-text or decorative decision. Bounds default to the safe full surface."),
            AIFunctionFactory.Create(
                method: (Guid variantId, long expectedRevision, CompositionScene scene) =>
                    StageCompositionVariantAsync(context, variantId, expectedRevision, scene),
                name: "stage_page_composition",
                description: "Validate and persist one complete composition scene once. Returns only an opaque one-use stageId and compact diagnostics; it never echoes the submitted scene."),
            AIFunctionFactory.Create(
                method: (Guid stageId, long expectedRevision) =>
                    ApplyCompositionStageAsync(context, stageId, expectedRevision),
                name: "apply_page_composition_stage",
                description: "Apply an already validated page-composition stage using only its one-use stageId and expected revision. Never repeat the scene payload."),
            AIFunctionFactory.Create(
                method: (Guid compositionId, long expectedRevision, ManuscriptOperationInput[] operations) =>
                    StageCompositionSemanticAsync(context, compositionId, expectedRevision, operations),
                name: "stage_page_composition_semantic",
                description: "Stage focused operations against the Designed Page's sole semantic manuscript without echoing its content. " + ManuscriptOperationInput.ToolOperationGuidance + " Returns a one-use stage ID."),
            AIFunctionFactory.Create(
                method: (Guid stageId, long expectedRevision) => ApplyCompositionSemanticStageAsync(context, stageId, expectedRevision),
                name: "apply_page_composition_semantic_stage",
                description: "Apply a staged semantic-manuscript edit using only its one-use stage ID and expected composition revision."),
            AIFunctionFactory.Create(
                method: (Guid compositionId, long expectedCompositionRevision, Guid variantId, long expectedVariantRevision, ManuscriptOperationInput[] semanticOperations, CompositionScene scene) => StageCompositionWorkspaceAsync(context, compositionId, expectedCompositionRevision, variantId, expectedVariantRevision, semanticOperations, scene),
                name: "stage_page_composition_workspace",
                description: "Atomically stage coupled Designed Page content and layout changes. Semantic content accepts paragraph, heading, sceneBreak, blockQuote, or listItem blocks only; scene images represent Figures. Submit the scene and semantic operations once; the result does not echo them."),
            AIFunctionFactory.Create(
                method: (Guid stageId, long expectedCompositionRevision) => ApplyCompositionWorkspaceStageAsync(context, stageId, expectedCompositionRevision),
                name: "apply_page_composition_workspace_stage",
                description: "Apply a coupled content-and-layout stage by one-use stage ID and exact composition revision; the staged variant revision is checked automatically."),
        ]);

        tools.Add(AIFunctionFactory.Create(
            method: (Guid chapterId, Guid imageId) => AddProjectImageToContextAsync(context, chapterId, imageId),
            name: "add_project_image_to_context",
            description: "Add an existing project image as explicit context for a chapter without placing it into the chapter layout."));

        tools.Add(AIFunctionFactory.Create(
            method: (Guid chapterId, Guid imageId) => RemoveProjectImageFromContextAsync(context, chapterId, imageId),
            name: "remove_project_image_from_context",
            description: "Remove an explicit project image context inclusion from a chapter without deleting the image or changing chapter layout."));

        tools.Add(AIFunctionFactory.Create(
            method: (EditorRevisionAgentAssignmentInput[] chapters) => StartRevisionAgentsAsync(context, chapters),
            name: "start_revision_agents",
            description:
                "Run prose-only revision workers for a verified set of three or more semantic prose chapters, using one call containing the complete set. " +
                "For one or two chapters, use direct manuscript tools by default; an explicit user request may use one worker call for two genuinely distributed chapters. " +
                "Choose this path before any manuscript mutation: do not directly mutate a target assigned here or work through the first two targets directly. Call only after discovery, exact reads, and warranted canon/outline/entity mutations, in a separate assistant round and tool batch; do not combine it with discovery, search, read, or mutation tools. " +
                "After workers finish, direct tools are reserved for specific verified corrections and coordinator-owned non-prose work against the resulting projected manuscript. " +
                "Each item must include chapterId, reason, and chapter-specific instructions for semantic manuscript work. Workers may edit related Figures, but Figure-only work does not count toward the three-prose-chapter threshold; the coordinator owns reusable style, typography, Designed Page, layout, composition, and page-scene work. " +
                $"Workers can alter only the {(context.ContentTarget.IsCore ? "Core" : "selected release")} chapter body; this coordinator reviews their completed/staged changes and takes follow-up action only if needed. " +
                (context.ContentTarget.IsCore
                    ? "Before calling this, make only the broader canon, outline, entity, beat, relationship, fact, or synopsis updates warranted under the Editor continuity-memory discipline; no such mutation is required solely to delegate prose, and a prose revision alone does not authorize a broad post-draft update. "
                    : "Do not change shared outline, canon, entities, beats, relationships, facts, or synopses from this release target. ") +
                "A successful worker result is already applied or staged; do not rerun it merely because persisted reads still show the pre-review manuscript."));

        if (context.ContentTarget.IsCore)
        {
            var existingNames = tools.OfType<AIFunction>().Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var outlineTool in await outlineTools.BuildEditorSharedAsync(new OutlineCollaborationContext(
                context.ProjectId,
                context.OnMutated,
                context.OutlineStaging,
                bookBriefUpdatePolicy: BookBriefUpdatePolicy.ExplicitUserRequestOnly,
                surface: OutlineToolSurface.Editor)))
            {
                if (outlineTool is AIFunction function && existingNames.Add(function.Name))
                    tools.Add(outlineTool);
            }
        }
        else
        {
            var unavailable = new HashSet<string>(StringComparer.Ordinal)
            {
                "update_project_page_setup",
                "delete_manuscript_style",
                "attach_entity_canonical_reference",
                "update_entity_canonical_reference",
                "detach_entity_canonical_reference",
                "add_project_image_to_context",
                "remove_project_image_from_context",
            };
            tools.RemoveAll(tool => tool is AIFunction function && unavailable.Contains(function.Name));
        }

        return tools;
    }

    private async Task<string> ListManuscriptStylesAsync(EditorChatContext ctx) =>
        JsonSerializer.Serialize(
            new
            {
                styles = ctx.ReviewEdits && ctx.EditorStaging is not null
                    ? await ctx.EditorStaging.ListManuscriptStyleDraftsAsync(
                        manuscriptStyles,
                        ctx.TurnCancellationToken)
                    : await manuscriptStyles.ListAsync(ctx.ProjectId, ctx.TurnCancellationToken),
            },
            ManuscriptCodec.JsonOptions);

    private async Task<string> ListBookFontsAsync(EditorChatContext ctx) =>
        JsonSerializer.Serialize(new
        {
            fonts = (await projectFonts.ListAsync(ctx.ProjectId, ctx.TurnCancellationToken))
                .Select(family => new
                {
                    family.Key,
                    family.Name,
                    family.Category,
                    faces = family.Faces.Select(face => new { face.Weight, face.Italic }),
                }),
        }, ManuscriptCodec.JsonOptions);

    private async Task<string> UpsertManuscriptStyleAsync(
        EditorChatContext ctx,
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
            if (!ctx.ContentTarget.IsCore && styleId is not null)
                throw new InvalidOperationException("Existing Book Text Styles are shared across Core and every release. Create a new style with a distinct name and apply that copy to this edition instead of updating the shared definition.");
            if (!Enum.TryParse<ManuscriptStyleKind>(kind, ignoreCase: true, out var parsedKind))
                throw new InvalidOperationException("Style kind must be Paragraph or Character.");
            var styles = await CurrentManuscriptStylesAsync(ctx);
            var current = styleId is Guid currentId
                ? styles.FirstOrDefault(style => style.Id == currentId)
                    ?? throw new InvalidOperationException("The Book Text Style was not found.")
                : null;
            var stagedStyleId = styleId
                ?? (ctx.ReviewEdits && ctx.EditorStaging is not null ? Guid.NewGuid() : null);
            var input = new ManuscriptStyleInput(
                    stagedStyleId,
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
                        TextAlignmentValue(textAlign),
                        leftIndentEm,
                        rightIndentEm,
                        firstLineIndentEm,
                        startOnNewPage),
                    expectedRevision);
            return await UpsertManuscriptStyleInputAsync(ctx, input, current, styles);
        }
        catch (ManuscriptStyleConflictException exception)
        {
            return JsonSerializer.Serialize(new
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
            return JsonSerializer.Serialize(new
            {
                ok = false,
                code = "STYLE_UPDATE_REJECTED",
                targetId = styleId,
                summary = exception.Message,
            });
        }
    }

    private static string? TextAlignmentValue(ParagraphAlignment? alignment) => alignment switch
    {
        ParagraphAlignment.Start => "left",
        ParagraphAlignment.Center => "center",
        ParagraphAlignment.End => "right",
        ParagraphAlignment.Justify => "justify",
        null => null,
        _ => throw new InvalidOperationException("Text alignment must be Start, Center, End, Justify, or omitted."),
    };

    private async Task<string> PreviewChapterPageAsync(
        EditorChatContext ctx,
        Guid chapterId,
        string? blockId,
        int? pageNumber)
    {
        try
        {
            blockId = string.IsNullOrWhiteSpace(blockId) ? null : blockId.Trim();
            if ((blockId is null) == (pageNumber is null))
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    code = "PREVIEW_TARGET_REQUIRED",
                    summary = "Provide exactly one of blockId or pageNumber.",
                }, ManuscriptCodec.JsonOptions);
            if (pageNumber is <= 0)
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    code = "PREVIEW_PAGE_INVALID",
                    summary = "pageNumber must be a positive 1-based chapter-local page number.",
                }, ManuscriptCodec.JsonOptions);

            var chapter = await chapters.GetAsync(chapterId, ctx.TurnCancellationToken);
            if (chapter is null || chapter.ProjectId != ctx.ProjectId)
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    code = "CHAPTER_NOT_FOUND",
                    summary = $"Chapter {chapterId:N} was not found in this project.",
                }, ManuscriptCodec.JsonOptions);

            var snapshot = await manuscripts.GetManuscriptAsync(ctx.ContentTarget, chapterId, ctx.TurnCancellationToken)
                ?? throw new InvalidDataException("The chapter manuscript was not found.");
            var sourceIsStaged = false;
            ManuscriptDocument document;
            if (ctx.ReviewEdits
                && ctx.EditorStaging?.TryGetChapterManuscriptDraft(chapterId, out var stagedDocument) == true)
            {
                document = stagedDocument;
                sourceIsStaged = true;
            }
            else
            {
                document = snapshot.Document;
            }

            var styles = ctx.ReviewEdits && ctx.EditorStaging is not null
                ? await ctx.EditorStaging.ListManuscriptStyleDraftsAsync(
                    manuscriptStyles,
                    ctx.TurnCancellationToken)
                : await manuscriptStyles.ListAsync(ctx.ProjectId, ctx.TurnCancellationToken);
            var preview = await chapterPreviews.RenderPageAsync(
                ctx.ProjectId,
                chapterId,
                new ChapterPreviewSource(document, styles),
                ctx.ContentTarget,
                new ChapterPreviewPageTarget(blockId, pageNumber),
                ctx.TurnCancellationToken);

            var visualId = Guid.NewGuid();
            var fileName = $"chapter-{chapterId:N}-page-{preview.ChapterPageNumber}.png";
            var contentUrl = $"/projects/{ctx.ProjectId:N}/editor-chat-visuals/{visualId:N}/content";
            ctx.AddVisual(new EditorChatVisualAttachment(
                visualId,
                $"{chapter.Title} — page {preview.ChapterPageNumber}",
                "Press-backed page preview for visual typesetting verification.",
                $"{contentUrl}?maxEdge=640",
                contentUrl,
                preview.PixelWidth,
                preview.PixelHeight,
                ctx.CurrentToolCallId,
                SourceKind: "chapterPreview",
                SourceRefId: chapterId,
                ContentType: "image/png",
                FileName: fileName,
                Data: preview.Data));
            if (ctx.VisionReady)
                ctx.AddModelOnlyImage(visualId, fileName, "image/png", preview.Data);

            return JsonSerializer.Serialize(new
            {
                ok = true,
                chapter = new { id = chapter.Id, title = chapter.Title },
                target = blockId is not null ? "block" : "chapterPage",
                blockId,
                requestedPageNumber = pageNumber,
                chapterPageNumber = preview.ChapterPageNumber,
                physicalPage = preview.PhysicalPage,
                pageCount = preview.PageCount,
                pageLabel = preview.Page.PageLabel,
                revision = document.Revision,
                source = sourceIsStaged ? "stagedDraft" : "persisted",
                dimensions = new { widthPixels = preview.PixelWidth, heightPixels = preview.PixelHeight },
                visualId,
                diagnostics = preview.Diagnostics.Take(12),
                delivery = ctx.VisionReady
                    ? "The rendered page image is attached as model-only visual context for the next reasoning iteration."
                    : "The rendered page image is attached for the user, but the active provider is not vision-ready so model-only image delivery was skipped.",
            }, ManuscriptCodec.JsonOptions);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                code = "CHAPTER_PREVIEW_FAILED",
                summary = exception.Message,
                recovery = "Correct the chapter, block ID, page number, font, image, or Press preview issue and retry.",
            }, ManuscriptCodec.JsonOptions);
        }
    }

    private async Task<string> PreviewPageCanvasAsync(
        EditorChatContext ctx,
        Guid compositionId,
        Guid variantId,
        string mode)
    {
        var previewMode = (mode ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "annotated" => CompositionCanvasPreviewMode.Annotated,
            "clean" => CompositionCanvasPreviewMode.Clean,
            _ => (CompositionCanvasPreviewMode?)null,
        };
        if (previewMode is null)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                code = "CANVAS_PREVIEW_MODE_INVALID",
                summary = "mode must be annotated or clean.",
            }, ManuscriptCodec.JsonOptions);
        }

        try
        {
            var composition = await compositions.GetAsync(ctx.ProjectId, compositionId)
                ?? throw new KeyNotFoundException("Page composition was not found.");
            CompositionCanvasPreviewResult preview;
            if (ctx.ContentTarget.EditionId is Guid editionId && composition.EditionId is null)
            {
                await EnsureCompositionReadableAsync(ctx, composition);
                var sourceVariant = await compositions.ReadVariantAsync(
                    ctx.ProjectId,
                    variantId,
                    ctx.TurnCancellationToken);
                if (sourceVariant.CompositionId != compositionId)
                    throw new InvalidOperationException("The selected layout does not belong to this Designed Page.");
                var effectiveVariant = await compositions.PreviewEditionVariantAsync(
                    ctx.ProjectId,
                    compositionId,
                    editionId,
                    ctx.TurnCancellationToken);
                var scene = JsonSerializer.Deserialize<CompositionScene>(
                    effectiveVariant.SceneJson,
                    ManuscriptCodec.JsonOptions)
                    ?? throw new InvalidDataException("The composition scene is empty.");
                var semantic = ManuscriptCodec.Deserialize(
                    composition.SemanticManuscriptJson,
                    composition.Id,
                    composition.Revision);
                preview = await canvasPreviews.RenderSceneAsync(
                    ctx.ProjectId,
                    compositionId,
                    composition.Revision,
                    scene,
                    semantic,
                    previewMode.Value,
                    ctx.TurnCancellationToken);
            }
            else
            {
                await RequireVariantTargetAsync(ctx, variantId);
                preview = await canvasPreviews.RenderAsync(
                    ctx.ProjectId,
                    compositionId,
                    variantId,
                    previewMode.Value,
                    ctx.TurnCancellationToken);
            }
            var visualId = Guid.NewGuid();
            var fileName = $"composition-{compositionId:N}-{previewMode.Value.ToString().ToLowerInvariant()}.png";
            var contentUrl = $"/projects/{ctx.ProjectId:N}/editor-chat-visuals/{visualId:N}/content";
            ctx.AddVisual(new EditorChatVisualAttachment(
                visualId,
                previewMode == CompositionCanvasPreviewMode.Annotated
                    ? "Designed Page — annotated canvas"
                    : "Designed Page — clean canvas",
                "Direct authoring-canvas preview for page-design verification.",
                $"{contentUrl}?maxEdge=640",
                contentUrl,
                preview.PixelWidth,
                preview.PixelHeight,
                ctx.CurrentToolCallId,
                SourceKind: "compositionCanvasPreview",
                SourceRefId: compositionId,
                ContentType: "image/png",
                FileName: fileName,
                Data: preview.Data));
            if (ctx.VisionReady)
                ctx.AddModelOnlyImage(visualId, fileName, "image/png", preview.Data);

            var prioritizedDiagnostics = preview.Diagnostics.Take(10).ToList();
            return JsonSerializer.Serialize(new
            {
                ok = true,
                targetId = compositionId,
                currentRevision = preview.VariantRevision,
                compositionId,
                variantId,
                compositionRevision = preview.CompositionRevision,
                variantRevision = preview.VariantRevision,
                mode = preview.Mode.ToString().ToLowerInvariant(),
                surface = new
                {
                    widthPoints = preview.SurfaceWidthPoints,
                    heightPoints = preview.SurfaceHeightPoints,
                    widthInches = Math.Round(preview.SurfaceWidthPoints / 72, 4),
                    heightInches = Math.Round(preview.SurfaceHeightPoints / 72, 4),
                    widthPixels = preview.PixelWidth,
                    heightPixels = preview.PixelHeight,
                },
                visualId,
                objects = new { visible = preview.VisibleObjectCount, hidden = preview.HiddenObjectCount },
                diagnosticCounts = new
                {
                    total = preview.Diagnostics.Count,
                    errors = preview.Diagnostics.Count(item => item.Severity == "error"),
                    warnings = preview.Diagnostics.Count(item => item.Severity == "warning"),
                },
                diagnostics = prioritizedDiagnostics,
                hasMoreDiagnostics = preview.Diagnostics.Count > prioritizedDiagnostics.Count,
                delivery = ctx.VisionReady
                    ? "The complete canvas image is attached as model-only visual context for the next reasoning iteration."
                    : "The complete canvas image is attached for the user, but the active provider is not vision-ready; do not claim visual verification.",
            }, ManuscriptCodec.JsonOptions);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                code = "CANVAS_PREVIEW_FAILED",
                targetId = compositionId,
                summary = exception.Message,
                recovery = "Read the composition and exact authoring variant, correct missing image or font data, then retry the canvas preview.",
            }, ManuscriptCodec.JsonOptions);
        }
    }

    private async Task<string> CreateParagraphStyleFromBlockAsync(
        EditorChatContext ctx,
        Guid chapterId,
        long expectedRevision,
        string blockId,
        string name)
    {
        var chapter = await chapters.GetAsync(chapterId, ctx.TurnCancellationToken)
            ?? throw new InvalidOperationException("The chapter was not found.");
        if (chapter.ProjectId != ctx.ProjectId)
            throw new InvalidOperationException("The chapter was not found in this project.");
        var snapshot = await manuscripts.GetManuscriptAsync(ctx.ContentTarget, chapterId, ctx.TurnCancellationToken)
            ?? throw new InvalidOperationException("The chapter manuscript was not found.");
        var source = ctx.ReviewEdits
            && ctx.EditorStaging?.TryGetChapterManuscriptDraft(chapterId, out var staged) == true
                ? staged
                : snapshot.Document;
        if (source.Revision != expectedRevision)
            throw new ManuscriptRevisionConflictException(expectedRevision, source.Revision);
        ManuscriptBlock block;
        try
        {
            block = ManuscriptOperations.FindBlock(source.Content, blockId);
        }
        catch (KeyNotFoundException exception)
        {
            throw new InvalidOperationException("The source paragraph was not found.", exception);
        }
        var styles = await CurrentManuscriptStylesAsync(ctx);
        var input = new ManuscriptStyleInput(
            ctx.ReviewEdits && ctx.EditorStaging is not null ? Guid.NewGuid() : null,
            name,
            ManuscriptStyleKind.Paragraph,
            ManuscriptStyleService.RoleFromName(name),
            ManuscriptStyleTemplateExtractor.Extract(block, styles));
        return await UpsertManuscriptStyleInputAsync(ctx, input, null, styles);
    }

    private async Task<string> ApplyManuscriptStyleAsync(
        EditorChatContext ctx,
        Guid styleId,
        Guid chapterId,
        long expectedRevision,
        string[]? blockIds)
    {
        try
        {
            var style = (await CurrentManuscriptStylesAsync(ctx)).FirstOrDefault(candidate => candidate.Id == styleId)
                ?? throw new InvalidOperationException("The Book Text Style was not found.");
            if (style.Kind != ManuscriptStyleKind.Paragraph)
                throw new InvalidOperationException("Choose a paragraph Book Text Style for manuscript blocks.");
            var snapshot = await manuscripts.GetManuscriptAsync(ctx.ContentTarget, chapterId, ctx.TurnCancellationToken)
                ?? throw new InvalidOperationException("The chapter manuscript was not found.");
            var source = ctx.ReviewEdits
                && ctx.EditorStaging?.TryGetChapterManuscriptDraft(chapterId, out var staged) == true
                    ? staged
                    : snapshot.Document;
            if (source.Revision != expectedRevision)
                throw new ManuscriptRevisionConflictException(expectedRevision, source.Revision);
            var operations = ManuscriptStyleTemplateExtractor.BuildApplyOperations(
                source,
                style.SemanticRole,
                blockIds);
            var affectedCount = operations.Count / 2;
            return await ApplyFocusedManuscriptOperationsAsync(
                ctx,
                chapterId,
                expectedRevision,
                operations,
                $"Apply Book Text Style {style.Name}",
                $"Applied '{style.Name}' to {affectedCount} paragraph(s).",
                changedIdLimit: 8);
        }
        catch (ManuscriptRevisionConflictException exception)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                code = "REVISION_CONFLICT",
                targetId = chapterId,
                currentRevision = exception.ActualRevision,
                summary = exception.Message,
                recovery = "Reread the compact manuscript and retry with the current revision; do not resend per-block style operations.",
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                code = "STYLE_APPLICATION_REJECTED",
                targetId = chapterId,
                summary = exception.Message,
            });
        }
    }

    private async Task<IReadOnlyList<ManuscriptStyleView>> CurrentManuscriptStylesAsync(EditorChatContext ctx) =>
        ctx.ReviewEdits && ctx.EditorStaging is not null
            ? await ctx.EditorStaging.ListManuscriptStyleDraftsAsync(
                manuscriptStyles,
                ctx.TurnCancellationToken)
            : await manuscriptStyles.ListAsync(ctx.ProjectId, ctx.TurnCancellationToken);

    private async Task<string> UpsertManuscriptStyleInputAsync(
        EditorChatContext ctx,
        ManuscriptStyleInput input,
        ManuscriptStyleView? current,
        IReadOnlyList<ManuscriptStyleView> styles)
    {
        if (ctx.ReviewEdits && ctx.EditorStaging is not null)
        {
            var preview = ManuscriptStyleService.PreviewUpsert(styles, input);
            var payload = JsonSerializer.Serialize(
                new
                {
                    ok = true,
                    staged = true,
                    targetId = preview.Id,
                    revision = preview.Revision,
                    preview.Name,
                    kind = preview.Kind.ToString(),
                    summary = $"Saved Book Text Style '{preview.Name}'.",
                    mutation = new { kind = "manuscriptStyles" },
                },
                ManuscriptCodec.JsonOptions);
            await ctx.EditorStaging.StageManuscriptStyleChangeAsync(
                current,
                input,
                preview,
                $"Upsert Book Text Style {preview.Name}",
                payload,
                ctx.TurnCancellationToken);
            return payload;
        }
        var style = await manuscriptStyles.UpsertAsync(
            ctx.ProjectId,
            input,
            ctx.TurnCancellationToken);
        ctx.OnMutated();
        return JsonSerializer.Serialize(new
        {
            ok = true,
            targetId = style.Id,
            revision = style.Revision,
            style.Name,
            kind = style.Kind.ToString(),
            summary = $"Saved Book Text Style '{style.Name}'.",
            mutation = new { kind = "manuscriptStyles" },
        }, ManuscriptCodec.JsonOptions);
    }

    private async Task<string> DeleteManuscriptStyleAsync(
        EditorChatContext ctx,
        Guid styleId,
        long expectedRevision)
    {
        if (ctx.ReviewEdits && ctx.EditorStaging is not null)
        {
            if (ctx.EditorStaging.HasStagedManuscriptEdits)
            {
                throw new InvalidOperationException(
                    "Do not delete Book Text Styles in the same review turn as manuscript edits. "
                    + "Apply or reject the manuscript changes first.");
            }
            var before = (await ctx.EditorStaging.ListManuscriptStyleDraftsAsync(
                    manuscriptStyles,
                    ctx.TurnCancellationToken))
                .FirstOrDefault(style => style.Id == styleId)
                ?? throw new InvalidOperationException("The Book Text Style was not found.");
            if (before.Revision != expectedRevision)
                throw new ManuscriptStyleConflictException(expectedRevision, before.Revision);
            await manuscriptStyles.ValidateDeleteAsync(
                ctx.ProjectId,
                styleId,
                expectedRevision,
                ctx.TurnCancellationToken);
            var payload = JsonSerializer.Serialize(
                new { staged = true, deletedStyleId = styleId, expectedRevision },
                ManuscriptCodec.JsonOptions);
            await ctx.EditorStaging.StageManuscriptStyleChangeAsync(
                before,
                null,
                null,
                $"Delete Book Text Style {before.Name}",
                payload,
                ctx.TurnCancellationToken);
            return payload;
        }
        await manuscriptStyles.DeleteAsync(
            ctx.ProjectId,
            styleId,
            expectedRevision,
            ctx.TurnCancellationToken);
        ctx.OnMutated();
        return JsonSerializer.Serialize(
            new { deletedStyleId = styleId, expectedRevision },
            ManuscriptCodec.JsonOptions);
    }

    private async Task<string> ReadProjectPageSetupAsync(EditorChatContext ctx)
    {
        var setup = await pageSetups.GetOrCreateAsync(ctx.ProjectId, ctx.TurnCancellationToken);
        return JsonSerializer.Serialize(new
        {
            ok = true,
            targetId = setup.ProjectId,
            revision = setup.Revision,
            page = new { widthInches = setup.PageWidthInches, heightInches = setup.PageHeightInches, marginInches = setup.PageMarginInches },
            body = new { fontSizePoints = setup.BodyFontSizePoints, lineHeight = setup.BodyLineHeight },
        });
    }

    private async Task<string> UpdateProjectPageSetupAsync(
        EditorChatContext ctx,
        long expectedRevision,
        double pageWidthInches,
        double pageHeightInches,
        double pageMarginInches,
        double bodyFontSizePoints,
        double bodyLineHeight)
    {
        var setup = await pageSetups.UpdateAsync(ctx.ProjectId, expectedRevision,
            new(pageWidthInches, pageHeightInches, pageMarginInches, bodyFontSizePoints, bodyLineHeight),
            ctx.TurnCancellationToken);
        ctx.OnMutated();
        return JsonSerializer.Serialize(new
        {
            ok = true,
            targetId = setup.ProjectId,
            revision = setup.Revision,
            changedFields = new[] { "pageWidthInches", "pageHeightInches", "pageMarginInches", "bodyFontSizePoints", "bodyLineHeight" },
            summary = "Project page setup updated.",
            mutation = new { kind = "projectPageSetup", id = setup.ProjectId, revision = setup.Revision },
        });
    }

    private async Task<string> ListSearchSourcesAsync(
        EditorChatContext ctx,
        string? query,
        string[]? sourceTypes,
        int topK)
    {
        topK = Math.Clamp(topK, 1, 30);
        var sources = await projectSearch.ListSourcesAsync(ctx.ProjectId, query, sourceTypes, topK, includeReferencedProjects: true);
        return ProjectSearchAgentPayload.SerializeSources(sources);
    }

    private async Task<string> ListReferenceVisualsAsync(EditorChatContext ctx)
    {
        var visuals = await referenceVisuals.ListAsync(ctx.ProjectId, ctx.TurnCancellationToken);
        return JsonSerializer.Serialize(new
        {
            resultKind = "referenceVisualDiscovery", returnedCount = visuals.Count,
            boundedLimit = ReferenceVisualService.MaximumListResults,
            mayHaveMore = visuals.Count == ReferenceVisualService.MaximumListResults,
            note = "Direct-reference canonical visuals are read-only continuity evidence and cannot be placed or mutated in the active project.",
            visuals,
        });
    }

    private async Task<string> ReadReferenceVisualAsync(EditorChatContext ctx, Guid originProjectId, Guid imageId)
    {
        var visual = await referenceVisuals.ReadAsync(ctx.ProjectId, originProjectId, imageId, ctx.VisionReady, ctx.TurnCancellationToken);
        if (visual is null) return $"Error: image {imageId:N} is not an eligible canonical visual on a direct referenced project.";
        if (visual.Data is not null)
            ctx.AddModelOnlyImage(visual.ImageId, visual.FileName, visual.ContentType, visual.Data);
        return JsonSerializer.Serialize(new
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
        EditorChatContext ctx,
        string sourceType,
        Guid sourceId,
        int? pageNumber,
        Guid? originProjectId)
    {
        var result = await projectSearch.ReadSourceAsync(ctx.ProjectId, sourceType, sourceId, pageNumber, originProjectId: originProjectId);
        if (result is null) return $"Error: source {sourceType}/{sourceId:N} was not found in this project.";
        if (string.Equals(sourceType, ProjectSearchSourceTypes.Entity, StringComparison.OrdinalIgnoreCase))
            await AddEntityVisualsToModelAsync(ctx, sourceId);
        return JsonSerializer.Serialize(result);
    }

    private async Task<string> SearchProjectAsync(
        EditorChatContext ctx,
        string query,
        int topK,
        string[]? sourceTypes,
        string[]? sourceIds,
        Guid? containerSourceId,
        bool lexicalOnly)
    {
        if (string.IsNullOrWhiteSpace(query)) return "Error: query is required.";
        var parsedSourceIds = ParseSourceIds(sourceIds, out var parseError);
        if (parseError is not null) return parseError;

        var results = await projectSearch.SearchAsync(new ProjectSearchRequest(
            ctx.ProjectId,
            query.Trim(),
            Math.Clamp(topK, 1, 30),
            sourceTypes,
            parsedSourceIds,
            containerSourceId,
            lexicalOnly,
            IncludeReferencedProjects: true));

        return ProjectSearchAgentPayload.SerializeResults(query.Trim(), results);
    }

    private async Task<string> ListChaptersAsync(EditorChatContext ctx)
    {
        var list = await chapters.ListAsync(ctx.ProjectId);
        if (list.Count == 0) return "No chapters in this project.";

        var sb = new StringBuilder();
        var expanded = await semanticProjection.ExpandPlainTextAsync(list);
        foreach (var chapter in list)
        {
            var plainText = expanded.GetValueOrDefault(chapter.Id, chapter.PlainText);
            var lineCount = ChapterFormatting.SplitLines(plainText).Count;
            var pageCount = CountReadChapterPages(plainText, EffectiveReadChapterPageMaxChars());

            sb.Append(chapter.Order + 1).Append(". ").Append(chapter.Title)
              .Append(" - id=").Append(chapter.Id);
            sb
              .Append(" - lines=").Append(lineCount)
              .Append(" - bodyChars=").Append(plainText.Length)
              .Append(" - readChapterPages=").Append(pageCount);
            if (!string.IsNullOrWhiteSpace(chapter.Synopsis))
                sb.Append(" - ").Append(chapter.Synopsis);
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> FindImpactedChaptersAsync(
        EditorChatContext ctx,
        string query,
        Guid? anchorChapterId,
        Guid[]? affectedEntityIds,
        Guid[]? eventIds,
        string[]? keywords,
        int topK)
    {
        var terms = BuildImpactTerms(query, keywords);
        if (terms.Count == 0)
            return "Error: query or keywords are required.";

        topK = Math.Clamp(topK, 1, 50);
        var orderedChapters = await ListOrderedChaptersAsync(ctx.ProjectId);
        if (orderedChapters.Count == 0)
            return "No chapters in this project.";

        var candidates = orderedChapters.ToDictionary(
            item => item.Chapter.Id,
            item => new ImpactCandidate(item.Chapter, item.GlobalOrder));
        var expanded = await semanticProjection.ExpandPlainTextAsync(
            orderedChapters.Select(item => item.Chapter).ToList());

        if (anchorChapterId is { } anchor && candidates.TryGetValue(anchor, out var anchorCandidate))
        {
            anchorCandidate.Add(45, "anchor", "Anchor chapter supplied by the coordinator.");
            foreach (var candidate in candidates.Values.Where(candidate => candidate.GlobalOrder > anchorCandidate.GlobalOrder))
            {
                var distance = candidate.GlobalOrder - anchorCandidate.GlobalOrder;
                var score = Math.Max(10, 35 - Math.Min(distance, 10) * 2);
                candidate.Add(score, "downstream", $"Chapter is {distance} chapter(s) after the anchor chapter.");
            }

            foreach (var candidate in candidates.Values.Where(candidate => Math.Abs(candidate.GlobalOrder - anchorCandidate.GlobalOrder) == 1))
                candidate.Add(20, "adjacent", "Chapter is adjacent to the anchor chapter.");
        }

        foreach (var candidate in candidates.Values)
        {
            ScoreText(candidate, "title", candidate.Chapter.Title, terms, 24);
            ScoreText(candidate, "synopsis", candidate.Chapter.Synopsis, terms, 30);
            ScoreText(candidate, "body-keyword", expanded.GetValueOrDefault(candidate.Chapter.Id, candidate.Chapter.PlainText), terms, 18);
        }

        await ScoreProjectSearchHitsAsync(ctx.ProjectId, terms, candidates);
        await ScoreEntityAnchorsAsync(ctx.ProjectId, affectedEntityIds, "affected-entity", candidates);
        await ScoreEntityAnchorsAsync(ctx.ProjectId, eventIds, "affected-event", candidates);

        var results = candidates.Values
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.GlobalOrder)
            .Take(topK)
            .Select(candidate => new
            {
                chapterId = candidate.Chapter.Id,
                title = candidate.Chapter.Title,
                order = candidate.GlobalOrder,
                synopsis = candidate.Chapter.Synopsis,
                bodyStats = new
                {
                    lines = ChapterFormatting.SplitLines(expanded.GetValueOrDefault(candidate.Chapter.Id, candidate.Chapter.PlainText)).Count,
                    chars = expanded.GetValueOrDefault(candidate.Chapter.Id, candidate.Chapter.PlainText).Length,
                    readChapterPages = CountReadChapterPages(expanded.GetValueOrDefault(candidate.Chapter.Id, candidate.Chapter.PlainText), EffectiveReadChapterPageMaxChars()),
                },
                score = candidate.Score,
                impact = candidate.Score >= 90 ? "high" : candidate.Score >= 45 ? "medium" : "low",
                reasons = candidate.Reasons,
                evidence = candidate.Evidence.Take(6).ToList(),
            })
            .ToList();

        if (results.Count == 0)
            return JsonSerializer.Serialize(new
            {
                query,
                message = "No impacted chapters found from the available outline, entity, keyword, and project-search signals.",
                candidates = Array.Empty<object>(),
            });

        return JsonSerializer.Serialize(new
        {
            query,
            anchors = new
            {
                anchorChapterId,
                affectedEntityIds = affectedEntityIds ?? [],
                eventIds = eventIds ?? [],
                keywords = keywords ?? [],
            },
            candidates = results,
        });
    }

    private async Task<string> StartRevisionAgentsAsync(EditorChatContext ctx, EditorRevisionAgentAssignmentInput[] assignments)
    {
        await using var databaseOperation = await database.OpenReadAsync(default);
        var aiChanges = databaseOperation.Repositories.AiChanges;
        if (assignments is null || assignments.Length == 0)
            return "Error: chapters is required.";

        foreach (var assignment in assignments)
        {
            if (assignment is null)
                return "Error: each revision-agent assignment is required.";
            var chapter = await chapters.GetAsync(assignment.ChapterId);
            if (chapter is null || chapter.ProjectId != ctx.ProjectId)
                return $"Error: chapter {assignment.ChapterId} not found in this project.";
            if (ctx.ReviewEdits
                && ctx.EditorStaging?.TryGetChapterManuscriptDraft(assignment.ChapterId, out _) == true)
            {
                return $"Error: chapter {assignment.ChapterId:N} already has a staged manuscript revision in this turn. Review the existing staged change instead of starting another revision worker.";
            }
        }

        var request = new EditorRevisionAgentRunRequest(
            ctx.ProjectId,
            ctx.ConversationId,
            ctx.CurrentAssistantMessageId,
            ctx.CurrentToolCallId,
            ctx.CurrentArgumentsJson,
            ctx.ContentTarget,
            assignments);
        var result = await revisionAgents.RunAsync(request, ctx.TurnCancellationToken);
        if (ctx.ReviewEdits && ctx.EditorStaging is not null)
        {
            foreach (var changeId in result.PendingChangeIds)
            {
                var change = await aiChanges.GetChangeAsync(changeId, ctx.TurnCancellationToken)
                    ?? throw new InvalidOperationException($"The staged revision-worker change {changeId:N} could not be reloaded.");
                ctx.EditorStaging.AdoptChapterManuscriptChange(change);
            }
        }

        return EditorRevisionAgentService.SerializeRunResult(result);
    }

    private async Task<string> ListProjectFactsAsync(EditorChatContext ctx)
    {
        var facts = await projectFacts.ListAsync(ctx.ProjectId);
        var payload = new List<object>();
        foreach (var fact in facts)
        {
            var linkedEntities = new List<object>();
            foreach (var link in fact.LinkedEntities)
            {
                linkedEntities.Add(new
                {
                    link.EdgeType,
                    direction = link.Direction.ToString(),
                    link.EntityId,
                    link.EntityName,
                    link.EntityType,
                    relationContext = await entityRelations.BuildForEntityAsync(ctx.ProjectId, link.EntityId, _listEntityRelationOptions),
                });
            }

            payload.Add(new
            {
                fact.Id,
                fact.Key,
                fact.Name,
                fact.Value,
                linkedEntities,
            });
        }

        return JsonSerializer.Serialize(payload);
    }

    private async Task<string> SearchEntitiesAsync(
        EditorChatContext ctx,
        string query,
        int topK,
        string? type,
        string? parentId)
    {
        if (string.IsNullOrWhiteSpace(query)) return "Error: query is required.";
        topK = Math.Clamp(topK, 1, 20);

        Guid? parent = null;
        if (!string.IsNullOrWhiteSpace(parentId))
        {
            if (!Guid.TryParse(parentId, out var parsedParent))
                return $"Error: parentId '{parentId}' is not a valid Guid.";
            parent = parsedParent;
        }

        if (ctx.ReviewEdits && ctx.OutlineStaging is not null)
            return await ctx.OutlineStaging.SearchEntitiesAsync(query, topK, type, parent);

        var searchTerms = SearchTerms(query);

        var matches = new List<(StoryEntity Entity, int Score)>();
        var typeNames = await SearchableTypeNamesAsync(ctx.ProjectId, type);
        foreach (var typeName in typeNames)
        {
            var list = await entities.ListAsync(ctx.ProjectId, typeName, parent);
            matches.AddRange(list
                .Select(entity => (Entity: entity, Score: SearchScore(entity, query, searchTerms)))
                .Where(match => match.Score > 0));
        }

        var selected = matches
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Entity.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(match => match.Entity.Name, StringComparer.OrdinalIgnoreCase)
            .Take(topK)
            .ToList();
        var visuals = await entityVisualExamples.ListForEntitiesAsync(ctx.ProjectId, selected.Select(match => match.Entity.Id).ToList());
        var payload = selected.Select(match => CompactEntitySearchPayload(
            match.Entity, match.Score, visuals.GetValueOrDefault(match.Entity.Id, [])));

        return AgentPayloadPaginator.SerializeCompactDiscovery(query.Trim(), topK, matches.Count, payload, "read_entity");
    }

    private async Task<IReadOnlyList<string>> SearchableTypeNamesAsync(Guid projectId, string? type)
    {
        if (!string.IsNullOrWhiteSpace(type))
            return [type.Trim()];

        var list = await entityTypes.ListAsync(projectId, includeStructural: true);
        return list
            .Where(typeDefinition => IsSearchableEntityType(typeDefinition.Type))
            .Select(typeDefinition => typeDefinition.Type)
            .ToList();
    }

    private async Task<string> ReadEntityAsync(EditorChatContext ctx, Guid entityId, int? pageNumber)
    {
        if (ctx.ReviewEdits && ctx.OutlineStaging is not null)
        {
            var stagedEntityType = await ctx.OutlineStaging.GetEntityTypeAsync(entityId);
            if (stagedEntityType is null)
                return $"Error: entity {entityId} not found in this project.";

            var stagedAddedToContextFeed = false;
            if (ctx.AutoPinReadEntities && ctx.CurrentChapterId is { } stagedCurrentChapterId && IsSearchableEntityType(stagedEntityType))
            {
                await editorContext.SetItemIncludedAsync(
                    ctx.ProjectId,
                    stagedCurrentChapterId,
                    ContextItemKind.Entity,
                    EditorContextKeys.Entity(entityId),
                    isIncluded: true);
                ctx.OnMutated();
                stagedAddedToContextFeed = true;
            }

            await AddEntityVisualsToModelAsync(ctx, entityId);
            return await ctx.OutlineStaging.ReadEntityAsync(entityId, stagedAddedToContextFeed, _detailEntityRelationOptions, pageNumber);
        }

        var entity = await entities.GetAsync(ctx.ProjectId, entityId);
        if (entity is null)
            return $"Error: entity {entityId} not found in this project.";

        var addedToContextFeed = false;
        if (ctx.AutoPinReadEntities && ctx.CurrentChapterId is { } currentChapterId && IsSearchableEntityType(entity.Type))
        {
            await editorContext.SetItemIncludedAsync(
                ctx.ProjectId,
                currentChapterId,
                ContextItemKind.Entity,
                EditorContextKeys.Entity(entity.Id),
                isIncluded: true);
            ctx.OnMutated();
            addedToContextFeed = true;
        }

        var links = await entities.ListLinksAsync(ctx.ProjectId, entityId);
        var manualLinks = links.Where(link => !link.IsAutoLink).Select(LinkPayload).ToList();
        var autoMentionLinks = links.Where(link => link.IsAutoLink).Select(LinkPayload).ToList();
        var relationContext = await entityRelations.BuildForEntityAsync(ctx.ProjectId, entityId, _detailEntityRelationOptions);
        var canonicalVisualReferences = await AddEntityVisualsToModelAsync(ctx, entityId);
        var identity = AgentPayloadPaginator.EntityIdentity(
            entity.Id,
            entity.Type,
            entity.Name,
            entity.Order,
            entity.ParentId,
            ("addedToContextFeed", JsonValue.Create(addedToContextFeed)));
        var detail = JsonSerializer.SerializeToNode(new
        {
            properties = entity.Properties,
            summary = entity.Summary,
            aliases = entity.Aliases,
            wikiSections = entity.WikiSections,
            sourceEvidence = entity.SourceEvidence,
            canonicalVisualReferences = canonicalVisualReferences.Select(VisualExamplePayload),
            manualLinks,
            autoMentionLinks,
            relationContextPreview = RelationContextPreview(entity.Id, _detailEntityRelationOptions, relationContext),
        });
        return AgentPayloadPaginator.SerializePage(
            identity,
            detail,
            "read_entity",
            new JsonObject { ["entityId"] = entity.Id },
            pageNumber);
    }

    private async Task<string> ListEntityVisualExamplesAsync(EditorChatContext ctx, Guid entityId)
    {
        if (await entities.GetAsync(ctx.ProjectId, entityId) is null)
            return $"Error: entity {entityId} not found in this project.";
        var examples = await AddEntityVisualsToModelAsync(ctx, entityId);
        return JsonSerializer.Serialize(examples.Select(VisualExamplePayload));
    }

    private async Task<string> AttachProjectImageToEntityAsync(EditorChatContext ctx, Guid entityId, Guid imageId, string? label)
    {
        try
        {
            if (ctx.OutlineStaging is not null)
            {
                var after = new EntityVisualChange("attach", EntityId: entityId, ImageId: imageId, Label: label?.Trim() ?? string.Empty);
                return await ctx.OutlineStaging.StageExternalChangeAsync(
                    "Attach a canonical visual reference to an entity", null, after,
                    new { status = "staged", entityId, imageId, label }, "EntityCanonicalReference", $"{entityId:N}/{imageId:N}");
            }
            var example = await entityVisualExamples.AttachAsync(ctx.ProjectId, entityId, imageId, label, EntityVisualExampleOrigin.Agent);
            ctx.AddModelOnlyImage(example.Image);
            ctx.OnMutated();
            return JsonSerializer.Serialize(VisualExamplePayload(example));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private async Task<string> UpdateEntityVisualExampleAsync(EditorChatContext ctx, Guid exampleId, string label, int? sortOrder)
    {
        try
        {
            if (ctx.OutlineStaging is not null)
            {
                var current = await entityVisualExamples.GetAsync(ctx.ProjectId, exampleId);
                if (current is null) return "Error: entity canonical visual reference was not found.";
                var before = new EntityVisualChange("update", current.Id, current.EntityId, current.Image.Id, Label: current.Label, SortOrder: current.SortOrder);
                var after = before with { Label = label.Trim(), SortOrder = sortOrder ?? current.SortOrder };
                return await ctx.OutlineStaging.StageExternalChangeAsync(
                    "Update an entity canonical visual reference", before, after,
                    new { status = "staged", canonicalReferenceId = exampleId, label, sortOrder }, "EntityCanonicalReference", exampleId.ToString("N"));
            }
            var example = await entityVisualExamples.UpdateAsync(ctx.ProjectId, exampleId, label, sortOrder);
            ctx.OnMutated();
            return JsonSerializer.Serialize(VisualExamplePayload(example));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private async Task<string> DetachProjectImageFromEntityAsync(EditorChatContext ctx, Guid exampleId)
    {
        if (ctx.OutlineStaging is not null)
        {
            var current = await entityVisualExamples.GetAsync(ctx.ProjectId, exampleId);
            if (current is null) return "Error: entity canonical visual reference was not found.";
            var before = new EntityVisualChange("detach", current.Id, current.EntityId, current.Image.Id, Label: current.Label, SortOrder: current.SortOrder);
            return await ctx.OutlineStaging.StageExternalChangeAsync(
                "Detach an entity canonical visual reference", before, null,
                new { status = "staged", canonicalReferenceId = exampleId }, "EntityCanonicalReference", exampleId.ToString("N"));
        }
        await entityVisualExamples.DetachAsync(ctx.ProjectId, exampleId);
        ctx.OnMutated();
        return JsonSerializer.Serialize(new { status = "detached", canonicalReferenceId = exampleId });
    }

    private async Task<string> CropProjectImageAsync(
        EditorChatContext ctx,
        Guid sourceImageId,
        ProjectImageCropRegion crop,
        string? fileName,
        string? altText,
        EntityVisualTarget? entityTarget)
    {
        try
        {
            var targetValidation = await entityVisualExamples.ValidateTargetsAsync(
                ctx.ProjectId,
                entityTarget is null ? null : [entityTarget]);
            if (!targetValidation.IsValid)
                return $"Error: {targetValidation.Error} Use a grounded entity id or omit entityTarget.";

            var image = await projectImages.CropAsync(ctx.ProjectId, sourceImageId, new ProjectImageCropRequest(
                crop,
                fileName?.Trim() ?? string.Empty,
                altText?.Trim() ?? string.Empty));
            object? canonicalReference = null;
            if (targetValidation.Targets is [var target])
            {
                if (ctx.OutlineStaging is null)
                {
                    var example = await entityVisualExamples.AttachAsync(
                        ctx.ProjectId,
                        target.EntityId,
                        image.Id,
                        target.Label,
                        EntityVisualExampleOrigin.Agent);
                    canonicalReference = VisualExamplePayload(example);
                }
                else
                {
                    var after = new EntityVisualChange("attach", EntityId: target.EntityId, ImageId: image.Id, Label: target.Label?.Trim() ?? string.Empty);
                    var staged = await ctx.OutlineStaging.StageExternalChangeAsync(
                        $"Attach cropped canonical reference to entity {target.EntityId:N}",
                        null,
                        after,
                        new { status = "staged", target.EntityId, imageId = image.Id, target.Label },
                        "EntityCanonicalReference",
                        $"{target.EntityId:N}/{image.Id:N}");
                    canonicalReference = JsonSerializer.Deserialize<JsonElement>(staged);
                }
            }

            ctx.AddVisual(await BuildVisualAsync(ctx, image, image.FileName, "Cropped project image saved to the library."));
            ctx.AddModelOnlyImage(image);
            ctx.OnMutated();
            return JsonSerializer.Serialize(new
            {
                status = "cropped",
                sourceImageId,
                image = new
                {
                    image.Id,
                    image.FileName,
                    image.ContentType,
                    image.PreviewUrl,
                    image.AltText,
                    image.Source,
                    image.SizeBytes,
                },
                canonicalReference,
            });
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private async Task<IReadOnlyList<EntityVisualExampleView>> AddEntityVisualsToModelAsync(EditorChatContext ctx, Guid entityId)
    {
        var examples = await entityVisualExamples.ListForEntityAsync(ctx.ProjectId, entityId);
        foreach (var example in examples)
        {
            ctx.AddModelOnlyImage(example.Image);
            ctx.AddVisual(await BuildVisualAsync(
                ctx,
                example.Image,
                title: example.Image.FileName,
                caption: string.IsNullOrWhiteSpace(example.Label)
                    ? $"Canonical visual reference for {example.EntityName}."
                    : $"{example.EntityName}: {example.Label}."));
        }
        return examples;
    }

    private static object VisualExamplePayload(EntityVisualExampleView example) => new
    {
        example.Id,
        example.EntityId,
        example.EntityType,
        example.EntityName,
        example.Label,
        example.SortOrder,
        example.Origin,
        image = new
        {
            example.Image.Id,
            example.Image.FileName,
            example.Image.ContentType,
            example.Image.PreviewUrl,
            example.Image.AltText,
            example.Image.Prompt,
        },
    };

    private async Task<object> EntityPayloadAsync(
        Guid projectId,
        StoryEntity entity,
        EntityRelationContextOptions relationOptions)
    {
        var relationContext = await entityRelations.BuildForEntityAsync(projectId, entity.Id, relationOptions);
        return new
        {
            id = entity.Id,
            type = entity.Type,
            name = entity.Name,
            order = entity.Order,
            parentId = entity.ParentId,
            properties = entity.Properties,
            summary = entity.Summary,
            aliases = entity.Aliases,
            wikiSections = entity.WikiSections,
            sourceEvidence = entity.SourceEvidence,
            relationContext,
        };
    }

    private async Task<string> ListEntityLinksAsync(EditorChatContext ctx, Guid entityId, int? pageNumber)
    {
        if (ctx.ReviewEdits && ctx.OutlineStaging is not null)
            return await ctx.OutlineStaging.ListEntityLinksAsync(entityId, pageNumber);

        var entity = await entities.GetAsync(ctx.ProjectId, entityId);
        if (entity is null)
            return $"Error: entity {entityId} not found in this project.";
        var links = await entities.ListLinksAsync(ctx.ProjectId, entityId);
        return AgentPayloadPaginator.SerializePage(
            AgentPayloadPaginator.EntityIdentity(entity.Id, entity.Type, entity.Name, entity.Order, entity.ParentId),
            JsonSerializer.SerializeToNode(new { links = links.Select(LinkPayload) }),
            "list_entity_links",
            new JsonObject { ["entityId"] = entity.Id },
            pageNumber);
    }

    private static object RelationContextPreview(Guid entityId, EntityRelationContextOptions options, object relationContext) => new
    {
        isComplete = false,
        note = "This traversal is a bounded orientation preview. All adjacent links are included separately; use list_entity_links and read_entity to continue traversal.",
        bounds = new { options.Depth, options.MaxDirectLinks, options.MaxTraversalPaths, options.MaxLinksPerNode },
        detailReadTool = "list_entity_links",
        detailReadArguments = new { entityId, pageNumber = 1 },
        value = relationContext,
    };

    private async Task<string> ReadChapterAsync(
        EditorChatContext ctx,
        Guid chapterId,
        int? pageNumber)
    {
        var chapter = await chapters.GetAsync(chapterId);
        if (chapter is null || chapter.ProjectId != ctx.ProjectId)
            return $"Error: chapter {chapterId} not found in this project.";

        var body = (await manuscripts.GetManuscriptAsync(ctx.ContentTarget, chapter.Id, ctx.TurnCancellationToken))?.PlainText ?? chapter.PlainText;
        var source = "persisted";
        if (ctx.ReviewEdits && ctx.EditorStaging?.TryGetChapterBodyDraft(chapter.Id, out var draftBody) == true)
        {
            body = draftBody;
            source = "stagedDraft";
        }

        var requestedPageNumber = pageNumber ?? 1;
        if (requestedPageNumber < 1)
            return "Error: pageNumber must be 1 or greater.";

        var pageMaxChars = EffectiveReadChapterPageMaxChars();
        var lines = ChapterFormatting.SplitLines(body);

        if (lines.Count == 0)
        {
            if (requestedPageNumber > 1)
                return "Error: pageNumber 1 is the only page available for an empty chapter.";

            return JsonSerializer.Serialize(new
            {
                chapter = new
                {
                    id = chapter.Id,
                    chapter.Title,
                    chapter.Synopsis,
                    source,
                },
                request = new
                {
                    pageNumber = requestedPageNumber,
                },
                chapterStats = new
                {
                    totalChapterLines = 0,
                    totalChapterChars = body.Length,
                },
                pagination = new
                {
                    pageStartLine = 0,
                    pageEndLine = 0,
                    pageStartColumn = 0,
                    pageEndColumn = 0,
                    currentPage = 1,
                    pageCount = 1,
                    pageMaxChars,
                    contentCharCount = 7,
                    hasPreviousPage = false,
                    hasNextPage = false,
                    previousPageNumber = (int?)null,
                    nextPageNumber = (int?)null,
                    startsInsideLine = false,
                    endsInsideLine = false,
                    containsPartialLine = false,
                },
                previousPageArguments = (object?)null,
                nextPageArguments = (object?)null,
                content = "(empty)",
            });
        }

        var pages = BuildReadChapterPages(lines, pageMaxChars);
        if (requestedPageNumber > pages.Count)
            return $"Error: pageNumber {requestedPageNumber} is beyond the chapter's {pages.Count} page(s).";

        var selectedPage = pages[requestedPageNumber - 1];
        var lineNumberWidth = Math.Max(4, lines.Count.ToString().Length);
        var content = FormatReadChapterPageContent(selectedPage, lineNumberWidth);

        return JsonSerializer.Serialize(new
        {
            chapter = new
            {
                id = chapter.Id,
                chapter.Title,
                chapter.Synopsis,
                source,
            },
            request = new
            {
                pageNumber = requestedPageNumber,
            },
            chapterStats = new
            {
                totalChapterLines = lines.Count,
                totalChapterChars = body.Length,
            },
            pagination = new
            {
                pageStartLine = selectedPage.PageStartLine,
                pageEndLine = selectedPage.PageEndLine,
                pageStartColumn = selectedPage.PageStartColumn,
                pageEndColumn = selectedPage.PageEndColumn,
                currentPage = requestedPageNumber,
                pageCount = pages.Count,
                pageMaxChars,
                contentCharCount = content.Length,
                hasPreviousPage = requestedPageNumber > 1,
                hasNextPage = requestedPageNumber < pages.Count,
                previousPageNumber = requestedPageNumber > 1 ? requestedPageNumber - 1 : (int?)null,
                nextPageNumber = requestedPageNumber < pages.Count ? requestedPageNumber + 1 : (int?)null,
                startsInsideLine = selectedPage.StartsInsideLine,
                endsInsideLine = selectedPage.EndsInsideLine,
                containsPartialLine = selectedPage.ContainsPartialLine,
            },
            previousPageArguments = requestedPageNumber > 1
                ? new { chapterId = chapter.Id, pageNumber = requestedPageNumber - 1 }
                : null,
            nextPageArguments = requestedPageNumber < pages.Count
                ? new { chapterId = chapter.Id, pageNumber = requestedPageNumber + 1 }
                : null,
            content,
        });
    }

    private async Task<string> ReadManuscriptAsync(
        EditorChatContext ctx,
        Guid chapterId,
        int startBlock,
        int blockCount)
    {
        var chapter = await chapters.GetAsync(chapterId, ctx.TurnCancellationToken);
        if (chapter is null || chapter.ProjectId != ctx.ProjectId)
            return $"Error: chapter {chapterId:N} was not found in this project.";
        var snapshot = await manuscripts.GetManuscriptAsync(ctx.ContentTarget, chapterId, ctx.TurnCancellationToken);
        if (snapshot is null)
            return $"Error: manuscript {chapterId:N} was not found.";
        var document = ctx.ReviewEdits
            && ctx.EditorStaging?.TryGetChapterManuscriptDraft(chapterId, out var staged) == true
                ? staged
                : snapshot.Document;
        startBlock = Math.Clamp(startBlock, 0, document.Content.Count);
        blockCount = Math.Clamp(blockCount, 1, 100);
        return AgentManuscriptProjection.SerializeDocument(
            document,
            ReferenceEquals(document, snapshot.Document) ? "persisted" : "stagedDraft",
            startBlock,
            blockCount,
            chapter.Id,
            chapter.Title,
            ManuscriptCodec.HashPlainText(ManuscriptCodec.ProjectPlainText(document)));
    }

    private async Task<string> InspectManuscriptAsync(
        EditorChatContext ctx,
        Guid chapterId,
        string? query,
        string? blockType,
        string? styleRole,
        int start,
        int count)
    {
        var chapter = await chapters.GetAsync(chapterId, ctx.TurnCancellationToken);
        if (chapter is null || chapter.ProjectId != ctx.ProjectId)
            return $"Error: chapter {chapterId:N} was not found in this project.";
        var snapshot = await manuscripts.GetManuscriptAsync(ctx.ContentTarget, chapterId, ctx.TurnCancellationToken);
        if (snapshot is null)
            return $"Error: manuscript {chapterId:N} was not found.";
        var stagedDraft = ctx.ReviewEdits
            && ctx.EditorStaging?.TryGetChapterManuscriptDraft(chapterId, out var staged) == true
            ? staged
            : null;
        var document = stagedDraft ?? snapshot.Document;
        var inspection = ManuscriptInspection.Inspect(document, query, blockType, styleRole, start, count);
        return AgentManuscriptProjection.SerializeInspection(
            document,
            stagedDraft is not null ? "stagedDraft" : "persisted",
            inspection,
            chapter.Id,
            chapter.Title,
            ManuscriptCodec.HashPlainText(ManuscriptCodec.ProjectPlainText(document)));
    }

    private async Task<string> ReadManuscriptMigrationStateAsync(EditorChatContext ctx)
    {
        var state = await manuscriptMigrations.GetStateAsync(ctx.TurnCancellationToken);
        return JsonSerializer.Serialize(new
        {
            state.MigrationRequired,
            state.RecoveryRequired,
            journals = state.Journals.Select(journal => new
            {
                journal.Id,
                journal.MigrationName,
                journal.SourceSchemaVersion,
                journal.TargetSchemaVersion,
                journal.Phase,
                journal.Status,
                BackupFileName = Path.GetFileName(journal.BackupPath),
                journal.ChapterCount,
                journal.ContestBatchCount,
                journal.ContestCandidateCount,
                journal.RevisionSessionCount,
                journal.SourceHash,
                journal.TargetHash,
                journal.ValidationReportJson,
                journal.ErrorDetail,
                journal.StartedAt,
                journal.CompletedAt,
            }),
            backups = state.Backups.Select(backup => new
            {
                FileName = Path.GetFileName(backup.Path),
                backup.SizeBytes,
                backup.CreatedAtUtc,
            }),
            restorePolicy = "A restore requires an explicit, expiring user confirmation token and cannot be executed by this read tool.",
        });
    }

    private async Task<string> ListProjectImagesAsync(EditorChatContext ctx)
    {
        var images = await projectImages.ListAsync(ctx.ProjectId);
        if (images.Count == 0)
            return "No project images.";

        return JsonSerializer.Serialize(images.Select(image => new
        {
            image.Id,
            image.FileName,
            image.ContentType,
            image.PreviewUrl,
            image.AltText,
            image.Source,
            image.Prompt,
            image.GenerationModel,
            image.CreatedAt,
            image.UpdatedAt,
            image.SizeBytes,
        }));
    }

    private async Task<string> ReadProjectImageAsync(EditorChatContext ctx, Guid imageId)
    {
        var image = await projectImages.GetAsync(ctx.ProjectId, imageId);
        if (image is null)
            return $"Error: image {imageId:N} was not found in this project.";

        ctx.AddVisual(await BuildVisualAsync(
            ctx,
            image,
            title: image.FileName,
            caption: "Image read into Editor Chat context."));
        ctx.AddModelOnlyImage(image);

        return JsonSerializer.Serialize(new
        {
            image = new
            {
                image.Id,
                image.FileName,
                image.ContentType,
                image.PreviewUrl,
                image.AltText,
                image.Source,
                image.Prompt,
                image.GenerationModel,
                image.CreatedAt,
                image.UpdatedAt,
                image.SizeBytes,
            },
            delivery = ctx.VisionReady
                ? "full image bytes will be supplied to the model on the next iteration"
                : "metadata only; the active chat provider is not vision-ready",
        });
    }

    private async Task<string> ListManuscriptVisualsAsync(EditorChatContext ctx, Guid chapterId, int start, int count)
    {
        var chapter = await chapters.GetAsync(chapterId);
        if (chapter is null || chapter.ProjectId != ctx.ProjectId)
            return JsonSerializer.Serialize(new { ok = false, code = "NOT_FOUND", summary = "Chapter was not found." });
        var snapshot = await manuscripts.GetManuscriptAsync(ctx.ContentTarget, chapterId, ctx.TurnCancellationToken);
        if (snapshot is null)
            return JsonSerializer.Serialize(new { ok = false, code = "NOT_FOUND", summary = "Manuscript was not found." });
        var document = ctx.ReviewEdits
            && ctx.EditorStaging?.TryGetChapterManuscriptDraft(chapterId, out var staged) == true
                ? staged
                : snapshot.Document;
        var visuals = document.Content.Where(block => block.Type is ManuscriptBlockType.Figure or ManuscriptBlockType.DesignedPage).ToList();
        start = Math.Clamp(start, 0, visuals.Count);
        count = Math.Clamp(count, 1, 50);
        var page = visuals.Skip(start).Take(count).Select(block => new
        {
            blockId = block.Id,
            type = block.Type.ToString(),
            imageId = block.ImageId,
            compositionId = block.PageCompositionId,
            caption = block.Type == ManuscriptBlockType.Figure
                ? string.Concat(block.Content.Select(inline => inline.Text))
                : null,
            accessibility = block.Type == ManuscriptBlockType.Figure ? new { block.Decorative, altText = block.Decorative ? null : block.AltText, block.Language } : null,
            presentation = block.Type == ManuscriptBlockType.Figure ? block.FigurePresentation : null,
        }).ToList();
        var diagnostics = visuals.Where(block => block.Type == ManuscriptBlockType.Figure && !block.Decorative && string.IsNullOrWhiteSpace(block.AltText))
            .Take(5).Select(block => new { severity = "error", code = "ALT_DECISION_REQUIRED", targetId = block.Id }).ToList();
        return JsonSerializer.Serialize(new
        {
            ok = true,
            targetId = chapterId,
            revision = document.Revision,
            summary = $"{visuals.Count} manuscript visual block(s).",
            counts = new { figures = visuals.Count(block => block.Type == ManuscriptBlockType.Figure), designedPages = visuals.Count(block => block.Type == ManuscriptBlockType.DesignedPage), diagnostics = diagnostics.Count },
            items = page,
            diagnostics,
            continuation = new { start, returned = page.Count, total = visuals.Count, hasMore = start + page.Count < visuals.Count, nextStart = start + page.Count < visuals.Count ? start + page.Count : (int?)null },
        }, ManuscriptCodec.JsonOptions);
    }

    private async Task<string> ReadPageCompositionAsync(EditorChatContext ctx, Guid compositionId, Guid variantId, int semanticStart, int semanticCount, int objectStart, int objectCount, int structureStart, int structureCount)
    {
        var composition = await compositions.GetAsync(ctx.ProjectId, compositionId)
            ?? throw new KeyNotFoundException("Page composition was not found.");
        await EnsureCompositionReadableAsync(ctx, composition);
        try
        {
            return await CompositionAgentPayloads.ReadVariantAsync(
                compositions, ctx.ProjectId, compositionId, variantId,
                semanticStart, semanticCount, objectStart, objectCount, structureStart, structureCount, ctx.TurnCancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or KeyNotFoundException)
        {
            return JsonSerializer.Serialize(new { ok = false, code = "NOT_FOUND", targetId = variantId, summary = exception.Message });
        }
    }

    private async Task<string> InsertFigureAsync(
        EditorChatContext ctx,
        Guid chapterId,
        long expectedRevision,
        int index,
        Guid imageId,
        string? caption,
        string? altText,
        bool decorative,
        string? language,
        FigureAccessibilityRole accessibilityRole,
        FigurePresentation presentation)
    {
        try
        {
            _ = await projectImages.GetAsync(ctx.ProjectId, imageId, ctx.TurnCancellationToken)
                ?? throw new KeyNotFoundException("Project image was not found.");
            return await ApplyFocusedManuscriptOperationsAsync(
                ctx,
                chapterId,
                expectedRevision,
                [new InsertManuscriptBlock(
                    index,
                    ManuscriptBlockType.Figure,
                    caption ?? string.Empty,
                    ManuscriptStyleRoles.FigureCaption,
                    imageId,
                    altText,
                    Decorative: decorative,
                    FigurePresentation: presentation,
                    Language: language,
                    AccessibilityRole: accessibilityRole)],
                "Insert manuscript Figure",
                "Figure inserted.");
        }
        catch (ManuscriptRevisionConflictException ex)
        {
            return JsonSerializer.Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = chapterId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread the compact manuscript visuals and retry against the current revision." });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or InvalidDataException)
        {
            return JsonSerializer.Serialize(new { ok = false, code = "FIGURE_REJECTED", targetId = chapterId, summary = ex.Message });
        }
    }

    private async Task<string> PatchFigureAsync(
        EditorChatContext ctx,
        Guid chapterId,
        long expectedRevision,
        string blockId,
        Guid imageId,
        string? caption,
        string? altText,
        bool decorative,
        string? language,
        FigureAccessibilityRole accessibilityRole,
        FigurePresentation presentation)
    {
        try
        {
            _ = await projectImages.GetAsync(ctx.ProjectId, imageId, ctx.TurnCancellationToken)
                ?? throw new KeyNotFoundException("Project image was not found.");
            var operations = new List<ManuscriptOperation>
            {
                new SetFigurePresentation(blockId, imageId, altText, decorative, language, presentation, accessibilityRole),
            };
            if (caption is not null)
                operations.Insert(0, new ReplaceManuscriptBlockText(blockId, caption));
            return await ApplyFocusedManuscriptOperationsAsync(
                ctx,
                chapterId,
                expectedRevision,
                operations,
                "Update manuscript Figure",
                "Figure updated.",
                blockId);
        }
        catch (ManuscriptRevisionConflictException ex)
        {
            return JsonSerializer.Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = chapterId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread the compact manuscript visuals and retry against the current revision." });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or InvalidDataException)
        {
            return JsonSerializer.Serialize(new { ok = false, code = "FIGURE_REJECTED", targetId = chapterId, summary = ex.Message });
        }
    }

    private async Task<string> InsertDesignedPageAsync(
        EditorChatContext ctx,
        Guid chapterId,
        long expectedRevision,
        int blockIndex,
        string name,
        DesignedPageLayoutMode layoutMode)
    {
        var chapter = await chapters.GetAsync(chapterId, ctx.TurnCancellationToken);
        if (chapter is null || chapter.ProjectId != ctx.ProjectId)
            return JsonSerializer.Serialize(new { ok = false, code = "CHAPTER_NOT_FOUND", targetId = chapterId, summary = "Chapter was not found in this project." });

        try
        {
            var initialContent = new DesignedPageInitialContent { LayoutMode = layoutMode };
            if (ctx.ReviewEdits && ctx.EditorStaging is not null)
            {
                var snapshot = await manuscripts.GetManuscriptAsync(ctx.ContentTarget, chapterId, ctx.TurnCancellationToken)
                    ?? throw new KeyNotFoundException("Manuscript was not found.");
                var source = ctx.EditorStaging.TryGetChapterManuscriptDraft(chapterId, out var staged)
                    ? staged
                    : snapshot.Document;
                if (source.Revision != expectedRevision)
                    throw new ManuscriptRevisionConflictException(expectedRevision, source.Revision);

                var identity = new DesignedPageIdentity(Guid.NewGuid(), Guid.NewGuid().ToString("N"));
                var applied = ManuscriptOperations.Apply(
                    source,
                    [new InsertManuscriptBlock(
                        blockIndex,
                        ManuscriptBlockType.DesignedPage,
                        string.Empty,
                        ManuscriptStyleRoles.DesignedPage,
                        PageCompositionId: identity.CompositionId,
                        BlockId: identity.BlockId)]);
                var stagedResult = JsonSerializer.Serialize(new
                {
                    ok = true,
                    staged = true,
                    requiresReview = true,
                    targetId = identity.CompositionId,
                    revision = applied.Document.Revision,
                    changedIds = applied.ChangedBlockIds,
                    variantId = (Guid?)null,
                    layoutMode,
                    summary = $"{LayoutLabel(layoutMode)} Designed Page is ready for review.",
                });
                await ctx.EditorStaging.StageChapterManuscriptEditAsync(
                    chapter,
                    source,
                    applied.Document,
                    $"Insert {LayoutLabel(layoutMode)} Designed Page '{(string.IsNullOrWhiteSpace(name) ? "Designed page" : name.Trim())}'",
                    stagedResult,
                    ctx.TurnCancellationToken);
                return stagedResult;
            }

            var result = await compositions.CreateDesignedPageAsync(
                ctx.ContentTarget,
                ctx.ProjectId,
                chapterId,
                blockIndex,
                name,
                expectedRevision,
                initialContent,
                ctx.TurnCancellationToken);
            ctx.OnMutated();
            return JsonSerializer.Serialize(new
            {
                ok = true,
                targetId = result.Composition.Id,
                revision = result.Manuscript.Revision,
                changedIds = new[] { result.BlockId },
                variantId = result.Variant?.Id,
                layoutMode,
                summary = "Designed Page inserted.",
                mutation = new { kind = "pageComposition", id = result.Composition.Id, selectId = result.Variant?.Id },
            });
        }
        catch (ManuscriptRevisionConflictException ex)
        {
            return JsonSerializer.Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = chapterId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread the manuscript and retry against its current revision." });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or KeyNotFoundException)
        {
            return JsonSerializer.Serialize(new { ok = false, code = "COMPOSITION_REJECTED", targetId = chapterId, summary = ex.Message });
        }
    }

    private static string LayoutLabel(DesignedPageLayoutMode layoutMode) => layoutMode switch
    {
        DesignedPageLayoutMode.FacingSpread => "facing-spread",
        _ => "single-page",
    };

    private async Task<string> ApplyFocusedManuscriptOperationsAsync(
        EditorChatContext ctx,
        Guid chapterId,
        long expectedRevision,
        IReadOnlyList<ManuscriptOperation> operations,
        string reviewSummary,
        string resultSummary,
        string? selectId = null,
        int? changedIdLimit = null)
    {
        var chapter = await chapters.GetAsync(chapterId, ctx.TurnCancellationToken)
            ?? throw new KeyNotFoundException("Chapter was not found.");
        if (chapter.ProjectId != ctx.ProjectId)
            throw new KeyNotFoundException("Chapter was not found in this project.");

        var snapshot = await manuscripts.GetManuscriptAsync(ctx.ContentTarget, chapterId, ctx.TurnCancellationToken)
            ?? throw new KeyNotFoundException("Manuscript was not found.");
        var source = ctx.ReviewEdits
            && ctx.EditorStaging?.TryGetChapterManuscriptDraft(chapterId, out var staged) == true
                ? staged
                : snapshot.Document;
        if (source.Revision != expectedRevision)
            throw new ManuscriptRevisionConflictException(expectedRevision, source.Revision);

        var applied = ManuscriptOperations.Apply(source, operations);
        var returnedChangedIds = changedIdLimit is int limit
            ? applied.ChangedBlockIds.Take(limit).ToList()
            : applied.ChangedBlockIds;
        var styleCatalog = ctx.ReviewEdits && ctx.EditorStaging is not null
            ? await ctx.EditorStaging.ListManuscriptStyleDraftsAsync(
                manuscriptStyles,
                ctx.TurnCancellationToken)
            : null;
        await manuscripts.ValidateDocumentReferencesAsync(
            ctx.ContentTarget,
            chapterId,
            applied.Document,
            styleCatalog,
            ctx.TurnCancellationToken);

        if (ctx.ReviewEdits && ctx.EditorStaging is not null)
        {
            var stagedResult = JsonSerializer.Serialize(new
            {
                ok = true,
                staged = true,
                targetId = chapterId,
                revision = applied.Document.Revision,
                changedIds = returnedChangedIds,
                changedBlockCount = applied.ChangedBlockIds.Count,
                summary = resultSummary,
                selectId,
            });
            await ctx.EditorStaging.StageChapterManuscriptEditAsync(
                chapter,
                source,
                applied.Document,
                reviewSummary,
                stagedResult,
                ctx.TurnCancellationToken);
            return stagedResult;
        }

        var result = await manuscripts.ReplaceDocumentAsync(
            ctx.ContentTarget,
            chapterId,
            expectedRevision,
            applied.Document,
            ctx.TurnCancellationToken);
        ctx.OnMutated();
        return JsonSerializer.Serialize(new
        {
            ok = true,
            targetId = chapterId,
            revision = result.Snapshot.Revision,
            changedIds = returnedChangedIds,
            changedBlockCount = applied.ChangedBlockIds.Count,
            summary = resultSummary,
            selectId,
            mutation = new { kind = "manuscript", id = chapterId, selectId },
        });
    }

    private async Task<string> PatchCompositionElementAsync(EditorChatContext ctx, Guid variantId, long expectedRevision, string targetKind, Guid targetId, CompositionElementPatch patch)
    {
        await RequireVariantTargetAsync(ctx, variantId);
        var result = await CompositionAgentPayloads.PatchElementAsync(
            compositions, ctx.ContentTarget, ctx.ProjectId, variantId, expectedRevision, targetKind, targetId, patch, ctx.TurnCancellationToken);
        if (JsonDocument.Parse(result).RootElement.GetProperty("ok").GetBoolean()) ctx.OnMutated();
        return result;
    }

    private async Task<string> FillPageImageCanvasAsync(
        EditorChatContext ctx,
        Guid variantId,
        long expectedRevision,
        Guid targetId,
        bool retainAspectRatio)
    {
        try
        {
            var variant = await compositions.ReadVariantAsync(ctx.ProjectId, variantId, ctx.TurnCancellationToken);
            EnsureCompositionTarget(ctx, variant.Composition);
            var scene = JsonSerializer.Deserialize<CompositionScene>(variant.SceneJson, ManuscriptCodec.JsonOptions)
                ?? throw new InvalidDataException("The composition scene is empty.");
            var item = scene.Objects.FirstOrDefault(candidate => candidate.Id == targetId)
                ?? throw new KeyNotFoundException("Composition object was not found.");
            var filled = CompositionImageLayout.FillCanvas(item, retainAspectRatio);
            return await PatchCompositionElementAsync(
                ctx,
                variantId,
                expectedRevision,
                "object",
                targetId,
                new CompositionElementPatch(Bounds: filled.Bounds, ImageFit: filled.ImageFit));
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or KeyNotFoundException)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                code = "IMAGE_LAYOUT_REJECTED",
                targetId,
                summary = exception.Message,
            });
        }
    }

    private async Task<string> PlacePageImageAsync(
        EditorChatContext ctx,
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
            return JsonSerializer.Serialize(new { ok = false, code = "ALT_DECISION_REQUIRED", targetId, summary = "Provide alternative text or explicitly mark the artwork decorative." });
        if (await projectImages.GetAsync(ctx.ProjectId, imageId, ctx.TurnCancellationToken) is null)
            return JsonSerializer.Serialize(new { ok = false, code = "IMAGE_NOT_FOUND", targetId, imageId, summary = "Project image was not found." });
        return await PatchCompositionElementAsync(
            ctx,
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

    private async Task<string> AddPageImageAsync(
        EditorChatContext ctx,
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
            await RequireVariantTargetAsync(ctx, variantId);
            if (await projectImages.GetAsync(ctx.ProjectId, imageId, ctx.TurnCancellationToken) is null)
                return JsonSerializer.Serialize(new { ok = false, code = "IMAGE_NOT_FOUND", targetId = variantId, imageId, summary = "Project image was not found." });
            var placed = await compositions.AddImageObjectAsync(
                ctx.ContentTarget, ctx.ProjectId, variantId, expectedRevision, imageId, fit, altText, decorative, bounds, readingOrder, ctx.TurnCancellationToken);
            ctx.OnMutated();
            return JsonSerializer.Serialize(new { ok = true, targetId = variantId, variantId = placed.Variant.Id, revision = placed.Variant.Revision, changedIds = new[] { placed.ObjectId }, selectId = placed.ObjectId, summary = "Project image added to the Designed Page.", mutation = new { kind = "pageComposition", id = placed.Variant.CompositionId, variantId = placed.Variant.Id, selectId = placed.ObjectId } });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or KeyNotFoundException or CompositionRevisionConflictException or DbUpdateConcurrencyException)
        {
            return JsonSerializer.Serialize(new { ok = false, code = ex is CompositionRevisionConflictException or DbUpdateConcurrencyException ? "REVISION_CONFLICT" : "PLACEMENT_REJECTED", targetId = variantId, summary = ex.Message, recovery = "Reread the compact page composition and retry with the same project-image ID." });
        }
    }

    private async Task<string> ReadLayoutGenerationTargetAsync(EditorChatContext ctx, string targetKind, Guid targetId, Guid? variantId)
    {
        try
        {
            if (variantId is Guid selectedVariantId)
                await RequireVariantTargetAsync(ctx, selectedVariantId);
            var descriptor = ctx.ContentTarget.EditionId is Guid editionId
                ? await compositions.DescribeGenerationTargetAsync(ctx.ProjectId, editionId, targetKind, targetId, variantId, ctx.TurnCancellationToken)
                : await compositions.DescribeAuthoringGenerationTargetAsync(ctx.ProjectId, targetKind, targetId, variantId, ctx.TurnCancellationToken);
            return JsonSerializer.Serialize(new { ok = true, targetId, summary = $"{descriptor.TargetKind} target {descriptor.AspectRatio}, {descriptor.RecommendedWidthPixels}x{descriptor.RecommendedHeightPixels}px.", descriptor }, ManuscriptCodec.JsonOptions);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return JsonSerializer.Serialize(new { ok = false, code = "INVALID_TARGET", targetId, summary = ex.Message });
        }
    }

    private async Task<string> GetOrCreateCompositionVariantAsync(EditorChatContext ctx, Guid compositionId)
    {
        try
        {
            var composition = await compositions.GetAsync(ctx.ProjectId, compositionId)
                ?? throw new KeyNotFoundException("Page composition was not found.");
            if (ctx.ContentTarget.EditionId is not null && composition.EditionId is null)
            {
                compositionId = await manuscripts.EnsureEditionCompositionAsync(
                    ctx.ContentTarget,
                    composition.ChapterId ?? throw new InvalidOperationException("The selected Designed Page is owned by a publication section."),
                    composition.Id,
                    ctx.TurnCancellationToken);
                composition = await compositions.GetAsync(ctx.ProjectId, compositionId)
                    ?? throw new KeyNotFoundException("The release Designed Page could not be loaded.");
            }
            EnsureCompositionTarget(ctx, composition);
            var variant = ctx.ContentTarget.EditionId is Guid editionId
                ? await compositions.GetOrCreateVariantAsync(ctx.ProjectId, compositionId, editionId, ctx.TurnCancellationToken)
                : await compositions.GetOrCreateAuthoringVariantAsync(ctx.ProjectId, compositionId, ctx.TurnCancellationToken);
            ctx.OnMutated();
            return JsonSerializer.Serialize(new { ok = true, targetId = variant.Id, revision = variant.Revision, summary = "The selected Editor target layout is ready.", changedIds = new[] { variant.Id }, mutation = new { kind = "pageComposition", id = compositionId, selectId = variant.Id } });
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            return JsonSerializer.Serialize(new { ok = false, code = "INVALID_TARGET", targetId = compositionId, summary = ex.Message });
        }
    }

    private async Task<string> StageCompositionVariantAsync(EditorChatContext ctx, Guid variantId, long expectedRevision, CompositionScene scene)
    {
        try
        {
            await RequireVariantTargetAsync(ctx, variantId);
            var stage = await compositions.StageVariantAsync(ctx.ContentTarget, ctx.ProjectId, ctx.ConversationId, variantId, expectedRevision, scene, ctx.TurnCancellationToken);
            return JsonSerializer.Serialize(new { ok = true, targetId = variantId, revision = expectedRevision, summary = $"Validated {scene.Objects.Count} composition object(s).", stageId = stage.Id, expiresAt = stage.ExpiresAt, diagnosticCounts = new { errors = 0, warnings = 0 } });
        }
        catch (CompositionRevisionConflictException ex)
        {
            return JsonSerializer.Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = variantId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread the composition, preserve unrelated objects, then stage a new scene once." });
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or KeyNotFoundException)
        {
            return JsonSerializer.Serialize(new { ok = false, code = "VALIDATION_FAILED", targetId = variantId, summary = ex.Message });
        }
    }

    private async Task<string> ApplyCompositionStageAsync(EditorChatContext ctx, Guid stageId, long expectedRevision)
    {
        try
        {
            var variant = await compositions.ApplyStageAsync(ctx.ContentTarget, ctx.ProjectId, ctx.ConversationId, stageId, expectedRevision, ctx.TurnCancellationToken);
            ctx.OnMutated();
            return JsonSerializer.Serialize(new { ok = true, targetId = variant.Id, variantId = variant.Id, revision = variant.Revision, summary = "Staged composition applied.", changedIds = new[] { variant.Id }, mutation = new { kind = "pageComposition", id = variant.CompositionId, variantId = variant.Id } });
        }
        catch (CompositionRevisionConflictException ex)
        {
            return JsonSerializer.Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = stageId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread and stage a new scene; stages are not rebased." });
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or KeyNotFoundException or DbUpdateConcurrencyException)
        {
            return JsonSerializer.Serialize(new { ok = false, code = ex is DbUpdateConcurrencyException ? "REVISION_CONFLICT" : "STAGE_REJECTED", targetId = stageId, summary = ex.Message, recovery = ex is DbUpdateConcurrencyException ? "Reread the composition and submit a new non-replayed stage." : null });
        }
    }

    private async Task<string> StageCompositionSemanticAsync(EditorChatContext ctx, Guid compositionId, long expectedRevision, ManuscriptOperationInput[] operations)
    {
        try { var composition = await compositions.GetAsync(ctx.ProjectId, compositionId) ?? throw new KeyNotFoundException("Page composition was not found."); EnsureCompositionTarget(ctx, composition); var stage = await compositions.StageSemanticOperationsAsync(ctx.ContentTarget, ctx.ProjectId, ctx.ConversationId, compositionId, expectedRevision, operations, ctx.TurnCancellationToken); return JsonSerializer.Serialize(new { ok = true, targetId = compositionId, revision = expectedRevision, stageId = stage.Id, stage.ExpiresAt, summary = $"Validated {operations.Length} semantic operation(s)." }); }
        catch (CompositionRevisionConflictException ex) { return JsonSerializer.Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = compositionId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread the bounded composition and submit a replacement stage." }); }
        catch (Exception ex) { return JsonSerializer.Serialize(new { ok = false, code = "SEMANTIC_STAGE_REJECTED", targetId = compositionId, summary = ex.Message }); }
    }

    private async Task<string> ApplyCompositionSemanticStageAsync(EditorChatContext ctx, Guid stageId, long expectedRevision)
    {
        try { var result = await compositions.ApplySemanticStageAsync(ctx.ContentTarget, ctx.ProjectId, ctx.ConversationId, stageId, expectedRevision, ctx.TurnCancellationToken); ctx.OnMutated(); return JsonSerializer.Serialize(new { ok = true, targetId = result.Composition.Id, revision = result.Composition.Revision, changedIds = result.ChangedBlockIds, summary = "Staged Designed Page content applied.", mutation = new { kind = "pageComposition", id = result.Composition.Id } }); }
        catch (CompositionRevisionConflictException ex) { return JsonSerializer.Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = stageId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread and submit a new non-replayed stage." }); }
        catch (Exception ex) { return JsonSerializer.Serialize(new { ok = false, code = "STAGE_REJECTED", targetId = stageId, summary = ex.Message }); }
    }

    private async Task<string> StageCompositionWorkspaceAsync(EditorChatContext ctx, Guid compositionId, long expectedCompositionRevision, Guid variantId, long expectedVariantRevision, ManuscriptOperationInput[] semanticOperations, CompositionScene scene)
    {
        try { await RequireVariantTargetAsync(ctx, variantId); var stage = await compositions.StageWorkspaceAsync(ctx.ContentTarget, ctx.ProjectId, ctx.ConversationId, compositionId, expectedCompositionRevision, variantId, expectedVariantRevision, semanticOperations, scene, ctx.TurnCancellationToken); return JsonSerializer.Serialize(new { ok = true, targetId = compositionId, revision = expectedCompositionRevision, stageId = stage.Id, stage.ExpiresAt, summary = $"Validated {semanticOperations.Length} semantic operation(s) with {scene.Objects.Count} scene object(s)." }); }
        catch (CompositionRevisionConflictException ex) { return JsonSerializer.Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = compositionId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread the compact workspace and submit one replacement stage." }); }
        catch (Exception ex) { return JsonSerializer.Serialize(new { ok = false, code = "WORKSPACE_STAGE_REJECTED", targetId = compositionId, summary = ex.Message }); }
    }

    private async Task<string> ApplyCompositionWorkspaceStageAsync(EditorChatContext ctx, Guid stageId, long expectedCompositionRevision)
    {
        try { var result = await compositions.ApplyWorkspaceStageAsync(ctx.ContentTarget, ctx.ProjectId, ctx.ConversationId, stageId, expectedCompositionRevision, ctx.TurnCancellationToken); ctx.OnMutated(); return JsonSerializer.Serialize(new { ok = true, targetId = result.Composition.Id, revision = result.Composition.Revision, variantId = result.Variant.Id, variantRevision = result.Variant.Revision, changedIds = result.ChangedBlockIds, summary = "Designed Page content and layout applied atomically.", mutation = new { kind = "pageComposition", id = result.Composition.Id, selectId = result.Variant.Id } }); }
        catch (CompositionRevisionConflictException ex) { return JsonSerializer.Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = stageId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread the compact workspace and submit a new non-replayed stage." }); }
        catch (Exception ex) { return JsonSerializer.Serialize(new { ok = false, code = "WORKSPACE_STAGE_REJECTED", targetId = stageId, summary = ex.Message }); }
    }

    private async Task RequireVariantTargetAsync(EditorChatContext context, Guid variantId)
    {
        var variant = await compositions.ReadVariantAsync(context.ProjectId, variantId, context.TurnCancellationToken);
        EnsureCompositionTarget(context, variant.Composition);
    }

    private static void EnsureCompositionTarget(EditorChatContext context, PageComposition composition)
    {
        if (composition.EditionId != context.ContentTarget.EditionId)
            throw new InvalidOperationException("The page composition belongs to a different Editor content target.");
    }

    private async Task EnsureCompositionReadableAsync(EditorChatContext context, PageComposition composition)
    {
        if (composition.EditionId == context.ContentTarget.EditionId)
            return;
        if (context.ContentTarget.EditionId is not null
            && composition.EditionId is null
            && context.CurrentChapterId == composition.ChapterId)
        {
            var effective = await manuscripts.GetManuscriptAsync(
                context.ContentTarget,
                composition.ChapterId ?? throw new InvalidOperationException("The selected Designed Page is owned by a publication section."),
                context.TurnCancellationToken);
            if (effective?.Document.Content.Any(block => block.PageCompositionId == composition.Id) == true)
                return;
        }
        throw new InvalidOperationException("The page composition belongs to a different Editor content target.");
    }

    private async Task<string> GenerateProjectImageAsync(
        EditorChatContext ctx,
        ImageGenerationBrief brief,
        ImageReferenceUse[]? references,
        ImageGenerationTarget? target,
        string? altText,
        string? quality,
        string? outputFormat,
        int? outputCompression)
    {
        try
        {
            var result = await imageWorkflow.GenerateAsync(
                ctx.ProjectId,
                brief,
                references,
                target,
                altText,
                quality,
                outputFormat,
                outputCompression,
                "Editor chat image",
                ctx.TrackImageGenerationJob,
                ctx.TurnCancellationToken);
            return await BuildImageResultAsync(ctx, result, "Generated output saved to the image library.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return $"Error: {ex.Message}";
        }
    }

    private async Task<string> CreateShapeMaskAsync(
        EditorChatContext ctx,
        Guid imageId,
        string label,
        ProjectImageMaskShape[] shapes)
    {
        try
        {
            var mask = await imageJobs.CreateMaskFromShapesAsync(
                ctx.ProjectId,
                imageId,
                new ProjectImageMaskShapeRequest(label, shapes),
                ctx.TurnCancellationToken);
            ctx.OnMutated();
            return JsonSerializer.Serialize(mask);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return $"Error: {ex.Message}";
        }
    }

    private async Task<string> EditProjectImageAsync(
        EditorChatContext ctx,
        Guid sourceImageId,
        ImageEditBrief brief,
        Guid? maskId,
        ProjectImageMaskShape[]? maskShapes,
        string? maskLabel,
        ImageReferenceUse[]? references,
        ImageGenerationTarget? target,
        string? altText,
        string? quality,
        string? outputFormat,
        int? outputCompression)
    {
        Guid? effectiveMaskId = maskId;
        if (effectiveMaskId is null && maskShapes is { Length: > 0 })
        {
            try
            {
                var mask = await imageJobs.CreateMaskFromShapesAsync(
                    ctx.ProjectId,
                    sourceImageId,
                    new ProjectImageMaskShapeRequest(maskLabel ?? "Editor image edit mask", maskShapes),
                    ctx.TurnCancellationToken);
                effectiveMaskId = mask.Id;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return $"Error: {ex.Message}";
            }
        }

        try
        {
            var result = await imageWorkflow.EditAsync(
                ctx.ProjectId,
                sourceImageId,
                brief,
                effectiveMaskId,
                references,
                target,
                altText,
                quality,
                outputFormat,
                outputCompression,
                "Editor chat image edit",
                ctx.TrackImageGenerationJob,
                ctx.TurnCancellationToken);
            return await BuildImageResultAsync(ctx, result, "Edited output saved to the image library.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return $"Error: {ex.Message}";
        }
    }

    private async Task<string> BuildImageResultAsync(
        EditorChatContext ctx,
        AgentProjectImageResult result,
        string caption)
    {
        var payloads = new List<object>();
        foreach (var output in result.Outputs)
        {
            var image = output.Image;
            var visual = await BuildVisualAsync(ctx, image, image.FileName, caption);
            ctx.AddVisual(visual);
            ctx.AddModelOnlyImage(image);
            payloads.Add(new
            {
                image.Id,
                image.PreviewUrl,
                targetAspect = result.TargetAspect,
                requestedRaster = result.RequestedRaster,
                actualRaster = output.ActualRaster,
                rasterMatched = output.RasterMatched,
                aspectMatched = output.AspectMatched,
                effectiveDpi = output.EffectiveDpi is { } dpi ? (double?)Math.Round(dpi, 1) : null,
            });
        }
        if (result.Images.Count > 0)
            ctx.OnMutated();
        return JsonSerializer.Serialize(new
        {
            ok = result.Succeeded,
            jobId = result.JobId,
            status = result.Status,
            outputImageIds = result.Images.Select(image => image.Id),
            images = payloads,
            attached = false,
            diagnosticCounts = new
            {
                errors = result.Diagnostics.Count,
                warnings = result.LayoutBound ? result.Outputs.Count(output => !output.AspectMatched) : 0,
            },
            diagnostics = result.Diagnostics.Take(3),
            aspectWarnings = result.LayoutBound
                ? result.Outputs.Where(output => !output.AspectMatched).Select(output => new
                {
                    code = "LAYOUT_IMAGE_ASPECT_MISMATCH",
                    message = $"Provider output {output.ActualRaster} does not match the target aspect {result.TargetAspect}. Inspect the image before deciding whether to place or regenerate it.",
                })
                : [],
            summary = result.Summary,
            nextAction = result.Succeeded
                ? "Inspect a returned image, then place its project-image ID with a separate Figure or Designed Page tool before completing an authoring request."
                : null,
        });
    }

    private async Task<string> ReadProjectImageJobAsync(EditorChatContext ctx, Guid jobId, bool wait)
    {
        var result = wait
            ? await imageWorkflow.WaitAsync(ctx.ProjectId, jobId, ctx.TrackImageGenerationJob, ctx.TurnCancellationToken)
            : await imageWorkflow.ReadAsync(ctx.ProjectId, jobId, ctx.TurnCancellationToken);
        return result is null
            ? JsonSerializer.Serialize(new { ok = false, code = "NOT_FOUND", jobId, summary = "Image job was not found in this project." })
            : await BuildImageResultAsync(ctx, result, "Project image job output loaded from the image library.");
    }

    private async Task<string> CancelProjectImageJobAsync(EditorChatContext ctx, Guid jobId)
    {
        await imageWorkflow.CancelAsync(ctx.ProjectId, jobId, ctx.TurnCancellationToken);
        return JsonSerializer.Serialize(new { ok = true, jobId, status = "cancelled", summary = "Image job cancelled; no image was placed." });
    }

    private async Task<string> AddProjectImageToContextAsync(EditorChatContext ctx, Guid chapterId, Guid imageId)
    {
        var chapter = await chapters.GetAsync(chapterId);
        if (chapter is null || chapter.ProjectId != ctx.ProjectId)
            return $"Error: chapter {chapterId} not found in this project.";
        if (await projectImages.GetAsync(ctx.ProjectId, imageId) is null)
            return $"Error: image {imageId} not found in this project.";

        await editorContext.SetItemIncludedAsync(
            ctx.ProjectId,
            chapterId,
            ContextItemKind.ProjectImage,
            EditorContextKeys.ProjectImage(imageId),
            isIncluded: true);
        ctx.OnMutated();
        return "Image added to chapter context.";
    }

    private async Task<string> RemoveProjectImageFromContextAsync(EditorChatContext ctx, Guid chapterId, Guid imageId)
    {
        var chapter = await chapters.GetAsync(chapterId);
        if (chapter is null || chapter.ProjectId != ctx.ProjectId)
            return $"Error: chapter {chapterId} not found in this project.";

        await editorContext.SetItemIncludedAsync(
            ctx.ProjectId,
            chapterId,
            ContextItemKind.ProjectImage,
            EditorContextKeys.ProjectImage(imageId),
            isIncluded: false);
        ctx.OnMutated();
        return "Image removed from chapter context.";
    }

    private int EffectiveReadChapterPageMaxChars() => Math.Max(256, editorOptions.Value.ReadChapterPageMaxChars);

    private static int CountReadChapterPages(string body, int pageMaxChars)
    {
        var lines = ChapterFormatting.SplitLines(body);
        return lines.Count == 0
            ? 1
            : BuildReadChapterPages(lines, pageMaxChars).Count;
    }

    private static List<ReadChapterPage> BuildReadChapterPages(
        IReadOnlyList<string> lines,
        int pageMaxChars)
    {
        var pages = new List<ReadChapterPage>();
        var currentPage = new ReadChapterPage();
        var lineNumberWidth = Math.Max(4, lines.Count.ToString().Length);
        var prefixLength = lineNumberWidth + 2;

        for (var lineNumber = 1; lineNumber <= lines.Count; lineNumber++)
        {
            var text = lines[lineNumber - 1];
            var fullLineLength = prefixLength + text.Length;
            if (fullLineLength <= pageMaxChars)
            {
                var addLength = fullLineLength + (currentPage.Segments.Count == 0 ? 0 : 1);
                if (currentPage.Segments.Count > 0 && currentPage.ContentCharCount + addLength > pageMaxChars)
                {
                    pages.Add(currentPage);
                    currentPage = new ReadChapterPage();
                }

                currentPage.Add(new ReadChapterLineSegment(
                    lineNumber,
                    text,
                    StartColumn: 1,
                    EndColumn: text.Length,
                    LineLength: text.Length,
                    IsFullLine: true), lineNumberWidth);
                continue;
            }

            if (currentPage.Segments.Count > 0)
            {
                pages.Add(currentPage);
                currentPage = new ReadChapterPage();
            }

            var maxTextChars = Math.Max(1, pageMaxChars - prefixLength);
            for (var offset = 0; offset < text.Length; offset += maxTextChars)
            {
                var length = Math.Min(maxTextChars, text.Length - offset);
                var segment = new ReadChapterLineSegment(
                    lineNumber,
                    text.Substring(offset, length),
                    StartColumn: offset + 1,
                    EndColumn: offset + length,
                    LineLength: text.Length,
                    IsFullLine: false);

                var partialPage = new ReadChapterPage();
                partialPage.Add(segment, lineNumberWidth);
                pages.Add(partialPage);
            }
        }

        if (currentPage.Segments.Count > 0)
            pages.Add(currentPage);

        return pages;
    }

    private static string FormatReadChapterPageContent(ReadChapterPage page, int lineNumberWidth)
    {
        var sb = new StringBuilder(page.ContentCharCount);
        for (var i = 0; i < page.Segments.Count; i++)
        {
            var segment = page.Segments[i];
            sb.Append(segment.LineNumber.ToString().PadLeft(lineNumberWidth, '0'));
            sb.Append(": ");
            sb.Append(segment.Text);
            if (i < page.Segments.Count - 1) sb.Append('\n');
        }

        return sb.ToString();
    }

    private sealed record ReadChapterLineSegment(
        int LineNumber,
        string Text,
        int StartColumn,
        int EndColumn,
        int LineLength,
        bool IsFullLine);

    private sealed class ReadChapterPage
    {
        public List<ReadChapterLineSegment> Segments { get; } = [];
        public int ContentCharCount { get; private set; }

        public int PageStartLine => Segments[0].LineNumber;
        public int PageEndLine => Segments[^1].LineNumber;
        public int PageStartColumn => Segments[0].StartColumn;
        public int PageEndColumn => Segments[^1].EndColumn;
        public bool StartsInsideLine => Segments[0].StartColumn > 1;
        public bool EndsInsideLine => Segments[^1].EndColumn < Segments[^1].LineLength;
        public bool ContainsPartialLine => Segments.Any(segment => !segment.IsFullLine);

        public void Add(ReadChapterLineSegment segment, int lineNumberWidth)
        {
            if (Segments.Count > 0) ContentCharCount++;
            ContentCharCount += lineNumberWidth + 2 + segment.Text.Length;
            Segments.Add(segment);
        }
    }

    private async Task<string> StartContestAsync(
        EditorChatContext ctx,
        Guid chapterId)
    {
        if (chapterId == Guid.Empty)
            return "Error: chapterId is required.";

        var chapter = await chapters.GetAsync(chapterId);
        if (chapter is null || chapter.ProjectId != ctx.ProjectId)
            return $"Error: chapter {chapterId} not found in this project.";

        ctx.RequestContest(new EditorContestStartRequest(chapterId, ctx.ContentTarget));

        return "Contest started. Candidate status will stream into the Contest Review workspace.";
    }

    private async Task<IReadOnlyList<OrderedChapter>> ListOrderedChaptersAsync(Guid projectId)
    {
        var actList = await acts.ListAsync(projectId);
        var allChapters = await chapters.ListAsync(projectId);
        var ordered = new List<OrderedChapter>();

        foreach (var act in actList.OrderBy(act => act.Order))
        {
            foreach (var chapter in allChapters
                .Where(chapter => chapter.ActId == act.Id)
                .OrderBy(chapter => chapter.Order))
            {
                ordered.Add(new OrderedChapter(chapter, ordered.Count));
            }
        }

        foreach (var chapter in allChapters
            .Where(chapter => chapter.ActId is null)
            .OrderBy(chapter => chapter.Order))
        {
            ordered.Add(new OrderedChapter(chapter, ordered.Count));
        }

        return ordered;
    }

    private async Task ScoreProjectSearchHitsAsync(
        Guid projectId,
        IReadOnlyList<string> terms,
        IReadOnlyDictionary<Guid, ImpactCandidate> candidates)
    {
        try
        {
            var queryText = string.Join(' ', terms);
            var results = await projectSearch.SearchAsync(new ProjectSearchRequest(
                projectId,
                queryText,
                Math.Min(50, Math.Max(12, candidates.Count)),
                [ProjectSearchSourceTypes.Chapter, ProjectSearchSourceTypes.ContextChapter]));

            foreach (var result in results.Results)
            {
                if (result.SourceId is not Guid chapterId
                    || !candidates.TryGetValue(chapterId, out var candidate))
                {
                    continue;
                }

                var score = result.Reasons.Contains("semantic")
                    ? 42
                    : 30;
                candidate.Add(score, "project-search", $"Chapter/context search hit: {result.Metadata ?? result.Title}.");
                candidate.AddEvidence("project-search", Truncate(result.Content, 420));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var candidate in candidates.Values.Take(1))
                candidate.AddEvidence("project-search-warning", $"Project search was unavailable: {ex.Message}");
        }
    }

    private async Task ScoreEntityAnchorsAsync(
        Guid projectId,
        IReadOnlyList<Guid>? entityIds,
        string reasonKind,
        IReadOnlyDictionary<Guid, ImpactCandidate> candidates)
    {
        if (entityIds is null || entityIds.Count == 0) return;

        foreach (var entityId in entityIds.Distinct())
        {
            var entity = await entities.GetAsync(projectId, entityId);
            if (entity is null) continue;

            if (entity.ParentId is { } parentId && candidates.TryGetValue(parentId, out var parentCandidate))
            {
                parentCandidate.Add(38, reasonKind, $"{entity.Type} '{entity.Name}' is scoped to this chapter.");
                parentCandidate.AddEvidence(reasonKind, $"{entity.Type}: {entity.Name}");
            }

            foreach (var link in await entities.ListLinksAsync(projectId, entityId))
            {
                if (candidates.TryGetValue(link.OtherEntityId, out var chapterCandidate))
                {
                    chapterCandidate.Add(34, reasonKind, $"{entity.Type} '{entity.Name}' links to this chapter via {link.EdgeType}.");
                    chapterCandidate.AddEvidence(reasonKind, $"{entity.Name} {link.EdgeType} {link.OtherEntityName}");
                    continue;
                }

                if (string.Equals(link.OtherEntityType, EntityTypeService.EventNodeType, StringComparison.OrdinalIgnoreCase))
                {
                    var linkedEvent = await entities.GetAsync(projectId, link.OtherEntityId);
                    if (linkedEvent?.ParentId is { } eventChapterId && candidates.TryGetValue(eventChapterId, out var eventCandidate))
                    {
                        eventCandidate.Add(30, reasonKind, $"{entity.Type} '{entity.Name}' links to event '{linkedEvent.Name}' in this chapter.");
                        eventCandidate.AddEvidence(reasonKind, $"{entity.Name} {link.EdgeType} {linkedEvent.Name}");
                    }
                }
            }
        }
    }

    private static IReadOnlyList<string> BuildImpactTerms(string query, IReadOnlyList<string>? keywords)
    {
        var terms = new List<string>();
        if (!string.IsNullOrWhiteSpace(query))
            terms.AddRange(SearchTerms(query));
        if (keywords is not null)
            terms.AddRange(keywords.SelectMany(SearchTerms));

        return terms
            .Where(term => term.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .ToList();
    }

    private static void ScoreText(ImpactCandidate candidate, string source, string? text, IReadOnlyList<string> terms, int maxScore)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var matches = terms
            .Where(term => text.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Take(8)
            .ToList();
        if (matches.Count == 0) return;

        var score = Math.Min(maxScore, matches.Count * 8);
        candidate.Add(score, source, $"{source} matched: {string.Join(", ", matches)}.");
        candidate.AddEvidence(source, ExtractEvidence(text, matches[0]));
    }

    private static string ExtractEvidence(string text, string term)
    {
        var index = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return Truncate(text, 240);

        var start = Math.Max(0, index - 120);
        var length = Math.Min(text.Length - start, term.Length + 240);
        var snippet = text.Substring(start, length).ReplaceLineEndings(" ");
        if (start > 0) snippet = "..." + snippet;
        if (start + length < text.Length) snippet += "...";
        return snippet;
    }

    private async Task<EditorChatVisualAttachment> BuildVisualAsync(
        EditorChatContext ctx,
        ProjectImageView image,
        string? title = null,
        string? caption = null)
    {
        var data = await projectImages.GetDataAsync(ctx.ProjectId, image.Id, maxEdge: null, CancellationToken.None);
        var size = data is null ? (Width: (int?)null, Height: (int?)null) : ReadSize(data.Data);
        return new EditorChatVisualAttachment(
            Guid.NewGuid(),
            title ?? image.FileName,
            caption ?? (string.IsNullOrWhiteSpace(image.Prompt) ? image.Source.ToString() : Truncate(image.Prompt, 120)),
            image.PreviewUrl,
            $"/projects/{ctx.ProjectId:N}/images/{image.Id:N}/content",
            size.Width,
            size.Height,
            ctx.CurrentToolCallId,
            SourceKind: "projectImage",
            SourceRefId: image.Id,
            ContentType: image.ContentType,
            FileName: image.FileName);
    }

    private static (int? Width, int? Height) ReadSize(byte[] data)
    {
        using var bitmap = SKBitmap.Decode(data);
        return bitmap is null
            ? ((int?)null, (int?)null)
            : (bitmap.Width, bitmap.Height);
    }

    private static object RasterMetadata(int? width, int? height) => new
    {
        width,
        height,
        orientation = width is null || height is null
            ? "unknown"
            : width == height
                ? "square"
                : width > height ? "landscape" : "portrait",
        aspectRatio = width is null || height is null || width <= 0 || height <= 0
            ? "unknown"
            : ReducedAspectRatio(width.Value, height.Value),
    };

    private static string ReducedAspectRatio(int width, int height)
    {
        var a = Math.Abs(width);
        var b = Math.Abs(height);
        while (b != 0)
        {
            var next = a % b;
            a = b;
            b = next;
        }

        var divisor = Math.Max(1, a);
        return $"{width / divisor}:{height / divisor}";
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";

    private static string[] SearchTerms(string query) =>
        query.Split([' ', '\t', '\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => term.Trim('"', '\'', '`', '(', ')', '[', ']', '{', '}', '.', ':'))
            .Where(term => term.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static int SearchScore(StoryEntity entity, string query, IReadOnlyList<string> searchTerms)
    {
        var score = TextMatchScore(entity.Name, query, titleWeight: 80, detailWeight: 30);
        score += TextMatchScore(entity.Type, query, titleWeight: 12, detailWeight: 8);
        score += TextMatchScore(entity.Summary, query, titleWeight: 20, detailWeight: 12);
        foreach (var alias in entity.Aliases)
            score += TextMatchScore(alias, query, titleWeight: 30, detailWeight: 16);
        foreach (var section in entity.WikiSections)
        {
            score += TextMatchScore(section.Title, query, titleWeight: 12, detailWeight: 6);
            score += TextMatchScore(section.Body, query, titleWeight: 12, detailWeight: 8);
        }
        foreach (var sourceEvidence in entity.SourceEvidence)
        {
            score += TextMatchScore(sourceEvidence.SourceTitle, query, titleWeight: 12, detailWeight: 6);
            score += TextMatchScore(sourceEvidence.Markdown, query, titleWeight: 12, detailWeight: 8);
        }
        foreach (var property in entity.Properties)
        {
            score += TextMatchScore(property.Key, query, titleWeight: 8, detailWeight: 4);
            score += TextMatchScore(property.Value, query, titleWeight: 8, detailWeight: 4);
        }

        foreach (var term in searchTerms)
        {
            score += TextMatchScore(entity.Name, term, titleWeight: 180, detailWeight: 60);
            score += TextMatchScore(entity.Type, term, titleWeight: 16, detailWeight: 8);
            score += TextMatchScore(entity.Summary, term, titleWeight: 28, detailWeight: 14);
            foreach (var alias in entity.Aliases)
                score += TextMatchScore(alias, term, titleWeight: 70, detailWeight: 24);
            foreach (var section in entity.WikiSections)
            {
                score += TextMatchScore(section.Title, term, titleWeight: 18, detailWeight: 8);
                score += TextMatchScore(section.Body, term, titleWeight: 18, detailWeight: 10);
            }
            foreach (var sourceEvidence in entity.SourceEvidence)
            {
                score += TextMatchScore(sourceEvidence.SourceTitle, term, titleWeight: 18, detailWeight: 8);
                score += TextMatchScore(sourceEvidence.Markdown, term, titleWeight: 18, detailWeight: 10);
            }
            foreach (var property in entity.Properties)
            {
                score += TextMatchScore(property.Key, term, titleWeight: 10, detailWeight: 5);
                score += TextMatchScore(property.Value, term, titleWeight: 10, detailWeight: 5);
            }
        }

        return score;
    }

    private static int TextMatchScore(string? value, string query, int titleWeight, int detailWeight)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(query)) return 0;
        if (value.Equals(query, StringComparison.OrdinalIgnoreCase)) return titleWeight * 4;
        if (value.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return titleWeight * 2;
        return value.Contains(query, StringComparison.OrdinalIgnoreCase) ? detailWeight : 0;
    }

    private static object CompactEntitySearchPayload(StoryEntity entity, int score, IReadOnlyList<EntityVisualExampleView>? visuals = null) => new
    {
        id = entity.Id,
        type = entity.Type,
        name = entity.Name,
        order = entity.Order,
        parentId = entity.ParentId,
        matchScore = score,
        previewIsComplete = false,
        previewCounts = new
        {
            summaryCharacters = entity.Summary?.Length ?? 0,
            aliases = entity.Aliases.Count,
            wikiSections = entity.WikiSections.Count,
            sourceEvidence = entity.SourceEvidence.Count,
            properties = entity.Properties.Count,
            visuals = visuals?.Count ?? 0,
        },
        preview = new
        {
            summaryText = TruncatePropertyValue(entity.Summary),
            aliases = entity.Aliases.Take(8).ToArray(),
            wikiSections = CompactWikiSections(entity.WikiSections),
            sourceEvidence = CompactSourceEvidence(entity.SourceEvidence),
            properties = CompactProperties(entity.Properties),
            canonicalVisualReferences = (visuals ?? []).Select(example => new { example.Image.Id, example.Label, example.SortOrder, example.Image.AltText, example.Image.Prompt }),
        },
        detailReadTool = "read_entity",
        detailReadArguments = new { entityId = entity.Id, pageNumber = 1 },
    };

    private static IReadOnlyList<Guid>? ParseSourceIds(string[]? sourceIds, out string? error)
    {
        error = null;
        if (sourceIds is not { Length: > 0 }) return null;

        var parsed = new List<Guid>();
        foreach (var value in sourceIds.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            if (!Guid.TryParse(value, out var id))
            {
                error = $"Error: sourceId '{value}' is not a valid Guid.";
                return null;
            }
            parsed.Add(id);
        }

        return parsed.Count == 0 ? null : parsed;
    }

    private async Task<string> ListManuscriptAnnotationsAsync(EditorChatContext context, int offset, int limit)
    {
        try
        {
            var page = await annotations.ListTargetAsync(
                context.ProjectId,
                context.ContentTarget,
                offset,
                limit,
                context.TurnCancellationToken);
            return JsonSerializer.Serialize(new
            {
                ok = true,
                target = context.ContentTarget.StorageKey,
                annotations = page.Items.Select(item => new
                {
                    annotationId = item.Id,
                    item.ChapterId,
                    item.EditionId,
                    kind = item.Kind.ToString(),
                    state = item.AnchorState.ToString(),
                    item.NoteText,
                    quote = ManuscriptAnnotationText.Bound(item.Quote, 1_000),
                    item.Revision,
                    item.AnchorManuscriptRevision,
                }),
                pagination = new
                {
                    page.Offset,
                    page.Limit,
                    page.Total,
                    returned = page.Items.Count,
                    hasMore = page.Offset + page.Items.Count < page.Total,
                    nextOffset = page.Offset + page.Items.Count < page.Total ? page.Offset + page.Items.Count : (int?)null,
                },
            });
        }
        catch (Exception exception)
        {
            return JsonSerializer.Serialize(new { ok = false, error = exception.Message });
        }
    }

    private async Task<string> CompleteManuscriptAnnotationAsync(
        EditorChatContext context,
        Guid annotationId,
        long expectedRevision)
    {
        try
        {
            var annotation = await annotations.GetAsync(
                context.ProjectId,
                context.ContentTarget,
                annotationId,
                context.TurnCancellationToken)
                ?? throw new KeyNotFoundException("The review annotation was not found in the selected content target.");
            if (annotation.Revision != expectedRevision)
                throw new InvalidOperationException($"Annotation revision conflict: expected {expectedRevision}, current revision is {annotation.Revision}.");
            if (context.ReviewEdits && context.EditorStaging is not null)
            {
                var result = JsonSerializer.Serialize(new
                {
                    ok = true,
                    annotationId,
                    requiresReview = true,
                    summary = "Annotation completion staged. It will be permanent only if the reviewed dependent edit is kept.",
                });
                await context.EditorStaging.StageAnnotationCompletionAsync(
                    annotation,
                    result,
                    context.TurnCancellationToken);
                return result;
            }

            await annotations.CompleteAsync(
                context.ProjectId,
                context.ContentTarget,
                annotationId,
                expectedRevision,
                context.TurnCancellationToken);
            context.OnMutated();
            return JsonSerializer.Serialize(new
            {
                ok = true,
                annotationId,
                requiresReview = false,
                summary = "Review annotation completed and permanently removed.",
            });
        }
        catch (Exception exception)
        {
            return JsonSerializer.Serialize(new { ok = false, error = exception.Message });
        }
    }

    private static object LinkPayload(EntityLink link) => new
    {
        edgeId = link.EdgeId,
        edgeType = link.EdgeType,
        direction = link.Direction.ToString(),
        otherEntityId = link.OtherEntityId,
        otherEntityName = link.OtherEntityName,
        otherEntityType = link.OtherEntityType,
        sortOrder = link.SortOrder,
        properties = link.Properties,
        summary = link.Summary,
        relationshipCitations = link.RelationshipCitations,
        isAutoLink = link.IsAutoLink,
    };

    private static object[] CompactSourceEvidence(IReadOnlyList<IngestSourceEvidence> sources) =>
        sources
            .Take(4)
            .Select(source => new
            {
                source.SourceTitle,
                source.SourceKind,
                markdown = TruncatePropertyValue(source.Markdown),
            })
            .ToArray();

    private static object[] CompactWikiSections(IReadOnlyList<IngestWikiSection> sections) =>
        sections
            .Take(4)
            .Select(section => new
            {
                section.Id,
                section.Title,
                body = TruncatePropertyValue(section.Body),
            })
            .ToArray();

    private static Dictionary<string, string?> CompactProperties(IReadOnlyDictionary<string, string?> properties)
    {
        var compact = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties
            .Where(property => !string.Equals(property.Key, "order", StringComparison.OrdinalIgnoreCase))
            .OrderBy(property => property.Key, StringComparer.OrdinalIgnoreCase)
            .Take(8))
        {
            compact[property.Key] = TruncatePropertyValue(property.Value);
        }

        return compact;
    }

    private static string? TruncatePropertyValue(string? value) =>
        string.IsNullOrEmpty(value) || value.Length <= 240 ? value : value[..240] + "...";

    private static double HeightUtilizationPercent(double availableHeightPixels, double requiredHeightPixels) =>
        availableHeightPixels <= 0
            ? 0
            : Math.Round(requiredHeightPixels / availableHeightPixels * 100, 1, MidpointRounding.AwayFromZero);

    private static bool IsSearchableEntityType(string type) =>
        !string.Equals(type, EntityTypeService.ProjectNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.ActNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.ChapterNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.ProjectFactNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.SourceNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.SourceChunkNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.SourceBlockNodeType, StringComparison.OrdinalIgnoreCase);

    private sealed record OrderedChapter(Chapter Chapter, int GlobalOrder);

    private sealed class ImpactCandidate(Chapter chapter, int globalOrder)
    {
        private readonly HashSet<string> _reasonKeys = new(StringComparer.OrdinalIgnoreCase);

        public Chapter Chapter { get; } = chapter;
        public int GlobalOrder { get; } = globalOrder;
        public int Score { get; private set; }
        public List<string> Reasons { get; } = [];
        public List<object> Evidence { get; } = [];

        public void Add(int score, string key, string reason)
        {
            Score += score;
            if (_reasonKeys.Add($"{key}:{reason}"))
                Reasons.Add(reason);
        }

        public void AddEvidence(string label, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            Evidence.Add(new { label, text });
        }
    }
}

public enum EditorChatToolMode
{
    Normal,
    ContestPreparation,
}
