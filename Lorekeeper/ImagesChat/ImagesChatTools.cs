using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lorekeeper.Chapters;
using Lorekeeper.Composition;
using Lorekeeper.Context;
using Lorekeeper.EditorChat;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Images;
using Lorekeeper.Llm;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Publish;
using Lorekeeper.Search;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Lorekeeper.ImagesChat;

public sealed class ImagesChatTools(
    IChapterService chapters,
    IProjectSearchService projectSearch,
    IProjectImageService projectImages,
    IEntityVisualExampleService entityVisualExamples,
    IEntityService entities,
    IProjectImageJobService imageJobs,
    IProjectImageGenerationRuntime imageRuntime,
    IImagePromptComposer imagePrompts,
    IManuscriptService manuscripts,
    ICompositionService compositions,
    IProjectPageSetupService pageSetups,
    IChapterSemanticProjectionService semanticProjection,
    IPublicationCoverService covers,
    IEditorContextService editorContext,
    IOptions<ProjectImageGenerationOptions> imageOptions,
    IOptions<EditorChatOptions> editorOptions)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public Task<IList<AITool>> BuildAsync(ImagesChatToolContext context, CancellationToken cancellationToken = default)
    {
        IList<AITool> tools =
        [
            AIFunctionFactory.Create(
                method: (string? query = null, string[]? sourceTypes = null, int topK = 10) =>
                    ListSearchSourcesAsync(context, query, sourceTypes, topK),
                name: "list_search_sources",
                description: "Return compact source discovery with complete IDs, total/returned counts, completeness, and exact read_project_source arguments."),

            AIFunctionFactory.Create(
                method: (string sourceType, Guid sourceId, int? pageNumber = null) =>
                    ReadProjectSourceAsync(context, sourceType, sourceId, pageNumber),
                name: "read_project_source",
                description: "Read one paginated project source by sourceType and sourceId. Supports chapters, acts, entities, ingest sources, and chunks."),

            AIFunctionFactory.Create(
                method: (string query, int topK = 8, string[]? sourceTypes = null, string[]? sourceIds = null, Guid? containerSourceId = null, bool lexicalOnly = false) =>
                    SearchProjectAsync(context, query, topK, sourceTypes, sourceIds, containerSourceId, lexicalOnly),
                name: "search_project",
                description: "Hybrid keyword + semantic compact discovery with full IDs, total/returned counts, labeled previews, and exact read_project_source arguments. Use source filters to narrow scope."),

            AIFunctionFactory.Create(
                method: () => ListChaptersAsync(context),
                name: "list_chapters",
                description: "List chapters with ids, order, title, synopsis, body line count, and read_chapter page count."),

            AIFunctionFactory.Create(
                method: (Guid chapterId, int? pageNumber = null) => ReadChapterAsync(context, chapterId, pageNumber),
                name: "read_chapter",
                description: "Read one paginated page of a chapter body with line numbers. Use pageNumber from returned pagination to continue."),

            AIFunctionFactory.Create(
                method: () => ListProjectImagesAsync(context),
                name: "list_project_images",
                description: "List project image library metadata, ids, preview URLs, source, prompts, model names, and sizes."),

            AIFunctionFactory.Create(
                method: () => ReadProjectPageSetupAsync(context),
                name: "read_project_page_setup",
                description: "Read project-owned authoring geometry and its revision for Figures, Designed Pages, preview, and target-bound generation. This is not a publication edition."),

            AIFunctionFactory.Create(
                method: (long expectedRevision, double pageWidthInches, double pageHeightInches, double pageMarginInches, double bodyFontSizePoints, double bodyLineHeight) =>
                    UpdateProjectPageSetupAsync(context, expectedRevision, pageWidthInches, pageHeightInches, pageMarginInches, bodyFontSizePoints, bodyLineHeight),
                name: "update_project_page_setup",
                description: "Revision-check the project authoring page setup. Read it first, then retain every value the user did not ask to change."),

            AIFunctionFactory.Create(
                method: (Guid imageId) => ReadProjectImageAsync(context, imageId),
                name: "read_project_image",
                description: "Read one project image's metadata and URLs and load it as visual context when the provider supports vision."),

            AIFunctionFactory.Create(
                method: (Guid entityId, int? pageNumber = null) => ReadEntityAsync(context, entityId, pageNumber),
                name: "read_entity",
                description: "Read an explicitly paginated entity and its ordered canonical visual references. Full identity fields and GUIDs repeat on every page; omit pageNumber for page 1 and follow nextPageArguments. Vision-ready providers receive the image bytes on the next round."),

            AIFunctionFactory.Create(
                method: (Guid entityId, int? pageNumber = null) => ListEntityLinksAsync(context, entityId, pageNumber),
                name: "list_entity_links",
                description: "List explicitly paginated graph links adjacent to an entity. Full identity fields and GUIDs repeat on every page; follow nextPageArguments until complete."),

            AIFunctionFactory.Create(
                method: (Guid entityId) => ListEntityVisualsAsync(context, entityId),
                name: "list_entity_canonical_references",
                description: "List and visually load the canonical appearance references attached to an entity."),

            AIFunctionFactory.Create(
                method: (Guid entityId, Guid imageId, string? label = null) => AttachEntityVisualAsync(context, entityId, imageId, label),
                name: "attach_entity_canonical_reference",
                description: "Attach one isolated, stable appearance or design image to an eligible story entity as a labeled canonical reference. Do not attach an ordinary narrative scene merely because the entity appears in it."),

            AIFunctionFactory.Create(
                method: (Guid canonicalReferenceId, string label, int? sortOrder = null) => UpdateEntityVisualAsync(context, canonicalReferenceId, label, sortOrder),
                name: "update_entity_canonical_reference",
                description: "Relabel or reorder an entity canonical visual reference."),

            AIFunctionFactory.Create(
                method: (Guid canonicalReferenceId) => DetachEntityVisualAsync(context, canonicalReferenceId),
                name: "detach_entity_canonical_reference",
                description: "Detach an entity canonical visual reference without deleting the library image."),

            AIFunctionFactory.Create(
                method: (Guid chapterId, int start = 0, int count = 30) => ListManuscriptVisualsAsync(context, chapterId, start, count),
                name: "list_manuscript_visuals",
                description: "List a bounded page of Figure and DesignedPage blocks with stable IDs, accessibility decisions, presentation, current manuscript revision, and continuation metadata."),

            AIFunctionFactory.Create(
                method: (Guid compositionId, Guid variantId, int semanticStart = 0, int semanticCount = 20, int objectStart = 0, int objectCount = 30, int structureStart = 0, int structureCount = 30) => ReadPageCompositionAsync(context, compositionId, variantId, semanticStart, semanticCount, objectStart, objectCount, structureStart, structureCount),
                name: "read_page_composition",
                description: "Read one selected geometry variant losslessly in bounded object pages, including complete surface, layers, styles, object fields, semantic excerpts, and revisions. Computed page overlays and image bytes are omitted."),

            AIFunctionFactory.Create(
                method: (Guid chapterId, long expectedRevision, int index, Guid imageId, string? caption, string? altText, bool decorative, string? language, FigureAccessibilityRole accessibilityRole, FigurePresentation presentation) =>
                    InsertFigureAsync(context, chapterId, expectedRevision, index, imageId, caption, altText, decorative, language, accessibilityRole, presentation),
                name: "insert_manuscript_figure",
                description: "Insert one project image as a revision-checked Figure block. Supply alt text or an explicit decorative decision and preserve server-owned generation geometry."),

            AIFunctionFactory.Create(
                method: (Guid chapterId, long expectedRevision, string blockId, Guid imageId, string? caption, string? altText, bool decorative, string? language, FigureAccessibilityRole accessibilityRole, FigurePresentation presentation) =>
                    PatchFigureAsync(context, chapterId, expectedRevision, blockId, imageId, caption, altText, decorative, language, accessibilityRole, presentation),
                name: "patch_manuscript_figure",
                description: "Revision-check replace or reformat one existing Figure while preserving unrelated manuscript blocks."),

            AIFunctionFactory.Create(
                method: (
                    Guid chapterId,
                    long expectedRevision,
                    int blockIndex,
                    string name,
                    DesignedPageLayoutMode layoutMode = DesignedPageLayoutMode.SinglePage,
                    Guid? imageId = null,
                    string? altText = null,
                    bool decorative = false,
                    FigureImageFit imageFit = FigureImageFit.Contain,
                    double cropXPercent = 50,
                    double cropYPercent = 50) =>
                    CreateDesignedPageAsync(
                        context,
                        chapterId,
                        expectedRevision,
                        blockIndex,
                        name,
                        layoutMode,
                        imageId,
                        altText,
                        decorative,
                        imageFit,
                        cropXPercent,
                        cropYPercent),
                name: "create_designed_page",
                description: "Atomically insert a complete Designed Page in project authoring geometry. Optional existing artwork must include Show whole image (Contain) or Fill frame (Cover), plus accessibility settings. The page opens as the selected authoring layout."),

            AIFunctionFactory.Create(
                method: (Guid compositionId) => GetOrCreateCompositionVariantAsync(context, compositionId),
                name: "get_or_create_page_composition_variant",
                description: "Get or create the active project-authoring geometry variant for a Designed Page."),

            AIFunctionFactory.Create(
                method: (Guid variantId) => ValidateCompositionAsync(context, variantId),
                name: "validate_page_composition",
                description: "Validate an authoring Designed Page for scene geometry, semantic coverage, reading order, accessibility, overflow, and font readiness. Publication DPI and edition compatibility are checked only in Publish. Returns compact prioritized diagnostics."),

            AIFunctionFactory.Create(
                method: (Guid variantId, long expectedRevision, string targetKind, Guid targetId, CompositionElementPatch patch) => PatchCompositionElementAsync(context, variantId, expectedRevision, targetKind, targetId, patch),
                name: "patch_page_composition_element",
                description: "Revision-check patch one stable composition object, layer, or style using only changed fields. Page overlays are computed and cannot be authored. Preserve all unrelated scene state; use full-scene staging only for structural edits."),
            AIFunctionFactory.Create(
                method: (Guid variantId, long expectedRevision, CompositionScene scene) => StageCompositionAsync(context, variantId, expectedRevision, scene),
                name: "stage_page_composition",
                description: "Submit one complete composition scene once. Returns an opaque one-use stage ID and compact validation summary without echoing the scene."),

            AIFunctionFactory.Create(
                method: (Guid stageId, long expectedRevision) => ApplyCompositionStageAsync(context, stageId, expectedRevision),
                name: "apply_page_composition_stage",
                description: "Apply a staged page scene using only its stage ID and expected revision; never repeat the full scene."),

            AIFunctionFactory.Create(
                method: (Guid compositionId, long expectedRevision, ManuscriptOperationInput[] operations) => StageCompositionSemanticAsync(context, compositionId, expectedRevision, operations),
                name: "stage_page_composition_semantic",
                description: "Stage focused block or inline-mark operations against a Designed Page's sole semantic manuscript. Returns a compact one-use stage ID without repeating content."),

            AIFunctionFactory.Create(
                method: (Guid stageId, long expectedRevision) => ApplyCompositionSemanticStageAsync(context, stageId, expectedRevision),
                name: "apply_page_composition_semantic_stage",
                description: "Apply a staged Designed Page semantic edit by one-use stage ID and exact composition revision."),
            AIFunctionFactory.Create(
                method: (Guid compositionId, long expectedCompositionRevision, Guid variantId, long expectedVariantRevision, ManuscriptOperationInput[] semanticOperations, CompositionScene scene) => StageCompositionWorkspaceAsync(context, compositionId, expectedCompositionRevision, variantId, expectedVariantRevision, semanticOperations, scene),
                name: "stage_page_composition_workspace",
                description: "Atomically stage coupled Designed Page content and layout changes. Semantic content accepts paragraph, heading, sceneBreak, blockQuote, or listItem blocks only; scene images represent Figures. Submit the payload once."),
            AIFunctionFactory.Create(
                method: (Guid stageId, long expectedCompositionRevision) => ApplyCompositionWorkspaceStageAsync(context, stageId, expectedCompositionRevision),
                name: "apply_page_composition_workspace_stage",
                description: "Apply a coupled content-and-layout stage by one-use stage ID; both stored revisions are checked."),

            AIFunctionFactory.Create(
                method: (Guid editionId, int objectStart = 0, int objectCount = 30, int structureStart = 0, int structureCount = 30) => ReadCoverAsync(context, editionId, objectStart, objectCount, structureStart, structureCount),
                name: "read_cover_composition",
                description: "Read compact cover geometry, diagnostics, layers, and one bounded scene-object page."),

            AIFunctionFactory.Create(
                method: (Guid editionId) => ValidateCoverAsync(context, editionId),
                name: "validate_cover_composition",
                description: "Validate the current format-aware cover for geometry, safe regions, folds, barcode reserve, accessibility, reading order, images, and current interior-derived spine state. Returns compact diagnostics."),

            AIFunctionFactory.Create(
                method: (Guid editionId, long expectedRevision, string targetKind, Guid targetId, CompositionElementPatch patch) => PatchCoverElementAsync(context, editionId, expectedRevision, targetKind, targetId, patch),
                name: "patch_cover_composition_element",
                description: "Revision-check patch one stable cover object, guide, layer, or style using only changed fields. Preserve unrelated cover state; use full-scene staging for structural edits."),

            AIFunctionFactory.Create(
                method: (Guid editionId, long expectedRevision, CompositionScene scene) => StageCoverAsync(context, editionId, expectedRevision, scene),
                name: "stage_cover_composition",
                description: "Submit one complete cover scene once and receive an opaque one-use stage ID without echoed payload."),

            AIFunctionFactory.Create(
                method: (Guid stageId, long expectedRevision) => ApplyCoverStageAsync(context, stageId, expectedRevision),
                name: "apply_cover_composition_stage",
                description: "Apply a staged cover scene by stage ID and expected revision; never repeat the full scene."),

            AIFunctionFactory.Create(
                method: (string targetKind, Guid targetId, Guid? variantId = null, Guid? editionId = null) => ReadLayoutGenerationTargetAsync(context, targetKind, targetId, variantId, editionId),
                name: "read_layout_generation_target",
                description: "Read server-owned dimensions, aspect ratio, provider canvas, and reserved regions. project-page, Figure, and page frame/surface targets use project authoring geometry and omit editionId; use the project ID for project-page. Cover targets require editionId. Composition page targets require the active variantId."),

            AIFunctionFactory.Create(
                method: (Guid sourceImageId, ProjectImageCropRegion crop, string? fileName = null, string? altText = null, EntityVisualTarget? entityTarget = null) =>
                    CropProjectImageAsync(context, sourceImageId, crop, fileName, altText, entityTarget),
                name: "crop_project_image",
                description: "Create a non-destructive project-library crop from an existing image using 0-100 percentage coordinates. Inspect the source first or use user-supplied coordinates and describe only the cropped subject in altText. Optionally attach the tight subject-only crop to one entity as its canonical reference; make separate crops for separate entities. Source associations are never inherited."),

            AIFunctionFactory.Create(
                method: (Guid imageId, string label, ProjectImageMaskShape[] shapes) =>
                    CreateShapeMaskAsync(context, imageId, label, shapes),
                name: "create_shape_mask",
                description: "Create a PNG edit mask for an existing image from percentage-based rect/ellipse/polygon shapes. Transparent pixels are the editable regions."),

            AIFunctionFactory.Create(
                method: (ImageGenerationBrief brief, ImageReferenceUse[]? references = null, ImageGenerationTarget? target = null, string? altText = null, string? quality = null, string? outputFormat = null, int? outputCompression = null, int count = 1, string? label = null) =>
                    GenerateImageAsync(context, brief, references, target, altText, quality, outputFormat, outputCompression, count, label),
                name: "generate_image",
                description: $"Generate unattached library images from a structured brief. Default to free-standing generation for reusable art and flowing Figures. Use a server-owned target only for a concrete Figure, page, or cover composition; omit manual size/aspect and respect protected regions. The returned raster is never rejected for aspect-ratio differences. Place it with Contain or Cover, then reposition a Cover crop directly if needed. Rendered text is disabled unless intentionally baked in. You may pass at most {Math.Max(0, imageOptions.Value.MaxReferenceImages)} references."),

            AIFunctionFactory.Create(
                method: (Guid sourceImageId, ImageEditBrief brief, Guid? maskId = null, ProjectImageMaskShape[]? maskShapes = null, string? maskLabel = null, ImageReferenceUse[]? references = null, ImageGenerationTarget? target = null, string? altText = null, string? quality = null, string? outputFormat = null, int? outputCompression = null, int count = 1, string? label = null) =>
                    EditImageAsync(context, sourceImageId, brief, maskId, maskShapes, maskLabel, references, target, altText, quality, outputFormat, outputCompression, count, label),
                name: "edit_image",
                description: $"Edit a project image when the requested result can be revised coherently. Prefer generation for spatial or compositional changes such as moving a character; do not frame edits as 'move this but change nothing else'. change describes the desired result and preserve lists only material continuity priorities, allowing nearby details to adapt naturally. Use a mask for genuinely localized work when available. references are labeled from provider input image 2 because the source is input image 1. Cover every depicted character with one available canonical reference each in focal order before optional references. You may pass at most {Math.Max(0, imageOptions.Value.MaxReferenceImages)} references. Outputs are unattached and never inherit source entity associations; attach only an intentionally isolated canonical result with the explicit canonical-reference tool."),

            AIFunctionFactory.Create(
                method: (Guid chapterId, Guid imageId) => AddProjectImageToContextAsync(context, chapterId, imageId),
                name: "add_project_image_to_context",
                description: "Add an existing project image as explicit chapter context without changing the chapter layout."),
        ];

        return Task.FromResult(tools);
    }

    private async Task<string> ReadProjectPageSetupAsync(ImagesChatToolContext ctx)
    {
        var setup = await pageSetups.GetOrCreateAsync(ctx.ProjectId, ctx.TurnCancellationToken);
        return JsonSerializer.Serialize(new
        {
            ok = true,
            targetId = setup.ProjectId,
            revision = setup.Revision,
            page = new { widthInches = setup.PageWidthInches, heightInches = setup.PageHeightInches, marginInches = setup.PageMarginInches },
            body = new { fontSizePoints = setup.BodyFontSizePoints, lineHeight = setup.BodyLineHeight },
        }, JsonOptions);
    }

    private async Task<string> UpdateProjectPageSetupAsync(
        ImagesChatToolContext ctx,
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
        ctx.MarkMutated();
        return JsonSerializer.Serialize(new
        {
            ok = true,
            targetId = setup.ProjectId,
            revision = setup.Revision,
            changedFields = new[] { "pageWidthInches", "pageHeightInches", "pageMarginInches", "bodyFontSizePoints", "bodyLineHeight" },
            summary = "Project page setup updated.",
            mutation = new { kind = "projectPageSetup", id = setup.ProjectId, revision = setup.Revision },
        }, JsonOptions);
    }

    private async Task<string> ListSearchSourcesAsync(ImagesChatToolContext ctx, string? query, string[]? sourceTypes, int topK)
    {
        topK = Math.Clamp(topK, 1, 30);
        var sources = await projectSearch.ListSourcesAsync(ctx.ProjectId, query, sourceTypes, topK);
        return ProjectSearchAgentPayload.SerializeSources(sources);
    }

    private async Task<string> ReadProjectSourceAsync(ImagesChatToolContext ctx, string sourceType, Guid sourceId, int? pageNumber)
    {
        var result = await projectSearch.ReadSourceAsync(ctx.ProjectId, sourceType, sourceId, pageNumber);
        if (result is null) return $"Error: source {sourceType}/{sourceId:N} was not found in this project.";
        if (string.Equals(sourceType, ProjectSearchSourceTypes.Entity, StringComparison.OrdinalIgnoreCase))
            await QueueEntityVisualsAsync(ctx, sourceId);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    private async Task<string> ReadEntityAsync(ImagesChatToolContext ctx, Guid entityId, int? pageNumber)
    {
        var entity = await entities.GetAsync(ctx.ProjectId, entityId);
        if (entity is null) return $"Error: entity {entityId} not found in this project.";
        var links = await entities.ListLinksAsync(ctx.ProjectId, entityId);
        var visuals = await QueueEntityVisualsAsync(ctx, entityId);
        var detail = JsonSerializer.SerializeToNode(new
        {
            properties = entity.Properties,
            summary = entity.Summary,
            aliases = entity.Aliases,
            wikiSections = entity.WikiSections,
            canonSources = entity.CanonSources,
            entity.IsIngestCreated,
            entity.CanonSourceCount,
            links = links.Select(link => new
            {
                link.EdgeId,
                link.EdgeType,
                direction = link.Direction.ToString(),
                link.OtherEntityId,
                link.OtherEntityName,
                link.OtherEntityType,
                link.SortOrder,
                link.Properties,
                link.Summary,
                link.RelationshipCitations,
                link.IsAutoLink,
            }),
            canonicalVisualReferences = visuals.Select(VisualPayload),
        }, JsonOptions);
        return AgentPayloadPaginator.SerializePage(
            AgentPayloadPaginator.EntityIdentity(entity.Id, entity.Type, entity.Name, entity.Order, entity.ParentId),
            detail,
            "read_entity",
            new JsonObject { ["entityId"] = entity.Id },
            pageNumber);
    }

    private async Task<string> ListEntityVisualsAsync(ImagesChatToolContext ctx, Guid entityId) =>
        JsonSerializer.Serialize((await QueueEntityVisualsAsync(ctx, entityId)).Select(VisualPayload), JsonOptions);

    private async Task<string> ListEntityLinksAsync(ImagesChatToolContext ctx, Guid entityId, int? pageNumber)
    {
        var entity = await entities.GetAsync(ctx.ProjectId, entityId);
        if (entity is null) return $"Error: entity {entityId} not found in this project.";
        var links = await entities.ListLinksAsync(ctx.ProjectId, entityId);
        return AgentPayloadPaginator.SerializePage(
            AgentPayloadPaginator.EntityIdentity(entity.Id, entity.Type, entity.Name, entity.Order, entity.ParentId),
            JsonSerializer.SerializeToNode(new { links }, JsonOptions),
            "list_entity_links",
            new JsonObject { ["entityId"] = entity.Id },
            pageNumber);
    }

    private async Task<string> AttachEntityVisualAsync(ImagesChatToolContext ctx, Guid entityId, Guid imageId, string? label)
    {
        try
        {
            var example = await entityVisualExamples.AttachAsync(ctx.ProjectId, entityId, imageId, label, EntityVisualExampleOrigin.Agent);
            ctx.AddModelOnlyImage(example.Image);
            ctx.MarkMutated();
            return JsonSerializer.Serialize(VisualPayload(example), JsonOptions);
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    private async Task<string> UpdateEntityVisualAsync(ImagesChatToolContext ctx, Guid exampleId, string label, int? sortOrder)
    {
        try
        {
            var example = await entityVisualExamples.UpdateAsync(ctx.ProjectId, exampleId, label, sortOrder);
            ctx.MarkMutated();
            return JsonSerializer.Serialize(VisualPayload(example), JsonOptions);
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    private async Task<string> DetachEntityVisualAsync(ImagesChatToolContext ctx, Guid exampleId)
    {
        await entityVisualExamples.DetachAsync(ctx.ProjectId, exampleId);
        ctx.MarkMutated();
        return JsonSerializer.Serialize(new { status = "detached", canonicalReferenceId = exampleId }, JsonOptions);
    }

    private async Task<IReadOnlyList<EntityVisualExampleView>> QueueEntityVisualsAsync(ImagesChatToolContext ctx, Guid entityId)
    {
        var visuals = await entityVisualExamples.ListForEntityAsync(ctx.ProjectId, entityId);
        foreach (var visual in visuals) ctx.AddModelOnlyImage(visual.Image);
        return visuals;
    }

    private async Task<string> SearchProjectAsync(
        ImagesChatToolContext ctx,
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
            lexicalOnly));

        return ProjectSearchAgentPayload.SerializeResults(query.Trim(), results);
    }

    private async Task<string> ListChaptersAsync(ImagesChatToolContext ctx)
    {
        var list = await chapters.ListAsync(ctx.ProjectId);
        if (list.Count == 0) return "No chapters in this project.";

        var pageMaxChars = EffectiveReadChapterPageMaxChars();
        var expanded = await semanticProjection.ExpandPlainTextAsync(list);
        var payload = list.Select(chapter => new
        {
            chapter.Id,
            order = chapter.Order + 1,
            chapter.Title,
            chapter.Synopsis,
            lines = ChapterFormatting.SplitLines(expanded.GetValueOrDefault(chapter.Id, chapter.PlainText)).Count,
            bodyChars = expanded.GetValueOrDefault(chapter.Id, chapter.PlainText).Length,
            readChapterPages = CountTextPages(ChapterFormatting.WithLineNumbers(expanded.GetValueOrDefault(chapter.Id, chapter.PlainText)), pageMaxChars),
        });
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private async Task<string> ReadChapterAsync(ImagesChatToolContext ctx, Guid chapterId, int? pageNumber)
    {
        var chapter = await chapters.GetAsync(chapterId);
        if (chapter is null || chapter.ProjectId != ctx.ProjectId)
            return $"Error: chapter {chapterId} not found in this project.";

        var text = ChapterFormatting.WithLineNumbers((await manuscripts.GetManuscriptAsync(chapter.Id))?.PlainText ?? chapter.PlainText);
        var pageMaxChars = EffectiveReadChapterPageMaxChars();
        var pageCount = CountTextPages(text, pageMaxChars);
        var requestedPage = Math.Clamp(pageNumber ?? 1, 1, pageCount);
        var start = text.Length == 0 ? 0 : (requestedPage - 1) * pageMaxChars;
        var content = text.Length == 0
            ? string.Empty
            : text.Substring(start, Math.Min(pageMaxChars, text.Length - start));

        return JsonSerializer.Serialize(new
        {
            chapter = new { chapter.Id, chapter.Title, chapter.Synopsis },
            pagination = new
            {
                currentPage = requestedPage,
                pageCount,
                pageMaxChars,
                totalTextChars = text.Length,
                hasPreviousPage = requestedPage > 1,
                previousPageNumber = requestedPage > 1 ? requestedPage - 1 : (int?)null,
                hasNextPage = requestedPage < pageCount,
                nextPageNumber = requestedPage < pageCount ? requestedPage + 1 : (int?)null,
            },
            previousPageArguments = requestedPage > 1 ? new { chapterId, pageNumber = requestedPage - 1 } : null,
            nextPageArguments = requestedPage < pageCount ? new { chapterId, pageNumber = requestedPage + 1 } : null,
            content,
        }, JsonOptions);
    }

    private async Task<string> ListProjectImagesAsync(ImagesChatToolContext ctx)
    {
        var images = await projectImages.ListAsync(ctx.ProjectId);
        if (images.Count == 0)
            return "No project images.";

        return JsonSerializer.Serialize(images.Select(ImagePayload), JsonOptions);
    }

    private async Task<string> ReadProjectImageAsync(ImagesChatToolContext ctx, Guid imageId)
    {
        var image = await projectImages.GetAsync(ctx.ProjectId, imageId);
        if (image is null)
            return $"Error: image {imageId:N} was not found in this project.";

        ctx.AddVisual(await BuildVisualAsync(
            ctx,
            image,
            title: image.FileName,
            caption: "Model-only image returned by read_project_image."));
        ctx.AddModelOnlyImage(image);

        return JsonSerializer.Serialize(new
        {
            image = ImagePayload(image),
            delivery = ctx.VisionReady
                ? "full image bytes will be supplied to the model on the next iteration"
                : "metadata only; the active chat provider is not vision-ready",
        }, JsonOptions);
    }

    private async Task<string> ListManuscriptVisualsAsync(ImagesChatToolContext ctx, Guid chapterId, int start, int count)
    {
        var chapter = await chapters.GetAsync(chapterId);
        if (chapter is null || chapter.ProjectId != ctx.ProjectId)
            return JsonSerializer.Serialize(new { ok = false, code = "NOT_FOUND", summary = "Chapter was not found." }, JsonOptions);
        var snapshot = await manuscripts.GetManuscriptAsync(chapterId, ctx.TurnCancellationToken);
        if (snapshot is null)
            return JsonSerializer.Serialize(new { ok = false, code = "NOT_FOUND", summary = "Manuscript was not found." }, JsonOptions);
        var visuals = snapshot.Document.Content.Where(block => block.Type is ManuscriptBlockType.Figure or ManuscriptBlockType.DesignedPage).ToList();
        start = Math.Clamp(start, 0, visuals.Count);
        count = Math.Clamp(count, 1, 50);
        var page = visuals.Skip(start).Take(count).Select(block => new
        {
            blockId = block.Id, type = block.Type.ToString(), block.ImageId, compositionId = block.PageCompositionId,
            block.FigurePresentation, block.Decorative, altText = block.Decorative ? null : block.AltText, block.Language,
        }).ToList();
        return JsonSerializer.Serialize(new
        {
            ok = true, targetId = chapterId, revision = snapshot.Revision, summary = $"{visuals.Count} manuscript visual block(s).",
            items = page,
            continuation = new { start, returned = page.Count, total = visuals.Count, hasMore = start + page.Count < visuals.Count, nextStart = start + page.Count < visuals.Count ? start + page.Count : (int?)null },
        }, JsonOptions);
    }

    private async Task<string> ReadPageCompositionAsync(
        ImagesChatToolContext ctx,
        Guid compositionId,
        Guid variantId,
        int semanticStart,
        int semanticCount,
        int objectStart,
        int objectCount,
        int structureStart,
        int structureCount)
    {
        try { return await CompositionAgentPayloads.ReadVariantAsync(compositions, ctx.ProjectId, compositionId, variantId, semanticStart, semanticCount, objectStart, objectCount, structureStart, structureCount, ctx.TurnCancellationToken); }
        catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException) { return JsonSerializer.Serialize(new { ok = false, code = "NOT_FOUND", targetId = variantId, summary = ex.Message }, JsonOptions); }
    }

    private async Task<string> PatchCompositionElementAsync(ImagesChatToolContext ctx, Guid variantId, long expectedRevision, string targetKind, Guid targetId, CompositionElementPatch patch)
    {
        var result = await CompositionAgentPayloads.PatchElementAsync(compositions, ctx.ProjectId, variantId, expectedRevision, targetKind, targetId, patch, ctx.TurnCancellationToken);
        if (JsonDocument.Parse(result).RootElement.GetProperty("ok").GetBoolean()) ctx.MarkMutated();
        return result;
    }

    private async Task<string> InsertFigureAsync(
        ImagesChatToolContext ctx,
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
            var result = await manuscripts.ApplyAsync(
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
                ctx.TurnCancellationToken);
            ctx.MarkMutated();
            return JsonSerializer.Serialize(new { ok = true, targetId = chapterId, revision = result.Snapshot.Revision, changedIds = result.ChangedBlockIds, summary = "Figure inserted.", mutation = new { kind = "manuscript", id = chapterId } }, JsonOptions);
        }
        catch (ManuscriptRevisionConflictException ex)
        {
            return JsonSerializer.Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = chapterId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread the compact manuscript visuals and retry against the current revision." }, JsonOptions);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return JsonSerializer.Serialize(new { ok = false, code = "FIGURE_REJECTED", targetId = chapterId, summary = ex.Message }, JsonOptions);
        }
    }

    private async Task<string> PatchFigureAsync(
        ImagesChatToolContext ctx,
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
            var result = await manuscripts.ApplyAsync(
                chapterId,
                expectedRevision,
                operations,
                ctx.TurnCancellationToken);
            ctx.MarkMutated();
            return JsonSerializer.Serialize(new { ok = true, targetId = chapterId, revision = result.Snapshot.Revision, changedIds = result.ChangedBlockIds, summary = "Figure updated.", mutation = new { kind = "manuscript", id = chapterId, selectId = blockId } }, JsonOptions);
        }
        catch (ManuscriptRevisionConflictException ex)
        {
            return JsonSerializer.Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = chapterId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread the compact manuscript visuals and retry against the current revision." }, JsonOptions);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return JsonSerializer.Serialize(new { ok = false, code = "FIGURE_REJECTED", targetId = chapterId, summary = ex.Message }, JsonOptions);
        }
    }

    private async Task<string> CreateDesignedPageAsync(
        ImagesChatToolContext ctx,
        Guid chapterId,
        long expectedRevision,
        int blockIndex,
        string name,
        DesignedPageLayoutMode layoutMode,
        Guid? imageId,
        string? altText,
        bool decorative,
        FigureImageFit imageFit,
        double cropXPercent,
        double cropYPercent)
    {
        try
        {
            var result = await compositions.CreateDesignedPageAsync(
                ctx.ProjectId,
                chapterId,
                blockIndex,
                name,
                expectedRevision,
                new DesignedPageInitialContent
                {
                    LayoutMode = layoutMode,
                    ImageId = imageId,
                    AltText = altText ?? string.Empty,
                    Decorative = decorative,
                    ImageFit = imageFit,
                    CropXPercent = cropXPercent,
                    CropYPercent = cropYPercent,
                },
                ctx.TurnCancellationToken);
            ctx.MarkMutated();
            return JsonSerializer.Serialize(new { ok = true, targetId = result.Composition.Id, revision = result.Manuscript.Revision, changedIds = new[] { result.BlockId }, variantId = result.Variant?.Id, initialImageId = imageId, layoutMode, summary = imageId is null ? "Designed Page inserted." : "Designed Page and initial artwork inserted.", mutation = new { kind = "pageComposition", id = result.Composition.Id, selectId = result.Variant?.Id } }, JsonOptions);
        }
        catch (ManuscriptRevisionConflictException ex)
        {
            return JsonSerializer.Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = chapterId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread manuscript visuals and retry." }, JsonOptions);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return JsonSerializer.Serialize(new { ok = false, code = "COMPOSITION_REJECTED", targetId = chapterId, summary = ex.Message }, JsonOptions);
        }
    }

    private async Task<string> GetOrCreateCompositionVariantAsync(ImagesChatToolContext ctx, Guid compositionId)
    {
        try
        {
            var variant = await compositions.GetOrCreateAuthoringVariantAsync(ctx.ProjectId, compositionId, ctx.TurnCancellationToken);
            ctx.MarkMutated();
            return JsonSerializer.Serialize(new { ok = true, targetId = variant.Id, revision = variant.Revision, summary = "Authoring layout is ready.", mutation = new { kind = "pageComposition", id = compositionId, selectId = variant.Id } }, JsonOptions);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return JsonSerializer.Serialize(new { ok = false, code = "INVALID_TARGET", targetId = compositionId, summary = ex.Message }, JsonOptions);
        }
    }

    private async Task<string> StageCompositionAsync(ImagesChatToolContext ctx, Guid variantId, long expectedRevision, CompositionScene scene)
    {
        try
        {
            var stage = await compositions.StageVariantAsync(ctx.ProjectId, ctx.ConversationId, variantId, expectedRevision, scene, ctx.TurnCancellationToken);
            return JsonSerializer.Serialize(new { ok = true, targetId = variantId, revision = expectedRevision, stageId = stage.Id, stage.ExpiresAt, summary = $"Validated {scene.Objects.Count} composition object(s).", diagnosticCounts = new { errors = 0, warnings = 0 } }, JsonOptions);
        }
        catch (CompositionRevisionConflictException ex)
        {
            return JsonSerializer.Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = variantId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread the bounded composition, preserve unrelated objects, and submit one replacement stage." }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, code = "INVALID_SCENE", targetId = variantId, summary = ex.Message }, JsonOptions);
        }
    }

    private async Task<string> ApplyCompositionStageAsync(ImagesChatToolContext ctx, Guid stageId, long expectedRevision)
    {
        try
        {
            var variant = await compositions.ApplyStageAsync(ctx.ProjectId, ctx.ConversationId, stageId, expectedRevision, ctx.TurnCancellationToken);
            ctx.MarkMutated();
            return JsonSerializer.Serialize(new { ok = true, targetId = variant.Id, revision = variant.Revision, changedIds = new[] { variant.Id }, summary = "Staged composition applied.", mutation = new { kind = "pageCompositionVariant", id = variant.Id } }, JsonOptions);
        }
        catch (CompositionRevisionConflictException ex)
        {
            return JsonSerializer.Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = stageId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread and submit a new non-replayed stage." }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, code = "STAGE_REJECTED", targetId = stageId, summary = ex.Message }, JsonOptions);
        }
    }

    private async Task<string> StageCompositionSemanticAsync(ImagesChatToolContext ctx, Guid compositionId, long expectedRevision, ManuscriptOperationInput[] operations)
    {
        try { var stage = await compositions.StageSemanticOperationsAsync(ctx.ProjectId, ctx.ConversationId, compositionId, expectedRevision, operations, ctx.TurnCancellationToken); return JsonSerializer.Serialize(new { ok = true, targetId = compositionId, revision = expectedRevision, stageId = stage.Id, stage.ExpiresAt, summary = $"Validated {operations.Length} semantic operation(s)." }, JsonOptions); }
        catch (CompositionRevisionConflictException ex) { return JsonSerializer.Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = compositionId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread the bounded composition and submit a replacement stage." }, JsonOptions); }
        catch (Exception ex) { return JsonSerializer.Serialize(new { ok = false, code = "SEMANTIC_STAGE_REJECTED", targetId = compositionId, summary = ex.Message }, JsonOptions); }
    }

    private async Task<string> ApplyCompositionSemanticStageAsync(ImagesChatToolContext ctx, Guid stageId, long expectedRevision)
    {
        try { var result = await compositions.ApplySemanticStageAsync(ctx.ProjectId, ctx.ConversationId, stageId, expectedRevision, ctx.TurnCancellationToken); ctx.MarkMutated(); return JsonSerializer.Serialize(new { ok = true, targetId = result.Composition.Id, revision = result.Composition.Revision, changedIds = result.ChangedBlockIds, summary = "Staged Designed Page content applied.", mutation = new { kind = "pageComposition", id = result.Composition.Id } }, JsonOptions); }
        catch (CompositionRevisionConflictException ex) { return JsonSerializer.Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = stageId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread and submit a new non-replayed stage." }, JsonOptions); }
        catch (Exception ex) { return JsonSerializer.Serialize(new { ok = false, code = "STAGE_REJECTED", targetId = stageId, summary = ex.Message }, JsonOptions); }
    }

    private async Task<string> StageCompositionWorkspaceAsync(ImagesChatToolContext ctx, Guid compositionId, long expectedCompositionRevision, Guid variantId, long expectedVariantRevision, ManuscriptOperationInput[] semanticOperations, CompositionScene scene)
    {
        try { var stage = await compositions.StageWorkspaceAsync(ctx.ProjectId, ctx.ConversationId, compositionId, expectedCompositionRevision, variantId, expectedVariantRevision, semanticOperations, scene, ctx.TurnCancellationToken); return JsonSerializer.Serialize(new { ok = true, targetId = compositionId, revision = expectedCompositionRevision, stageId = stage.Id, stage.ExpiresAt, summary = $"Validated {semanticOperations.Length} semantic operation(s) with {scene.Objects.Count} scene object(s)." }, JsonOptions); }
        catch (CompositionRevisionConflictException ex) { return JsonSerializer.Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = compositionId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread the compact workspace and submit one replacement stage." }, JsonOptions); }
        catch (Exception ex) { return JsonSerializer.Serialize(new { ok = false, code = "WORKSPACE_STAGE_REJECTED", targetId = compositionId, summary = ex.Message }, JsonOptions); }
    }

    private async Task<string> ApplyCompositionWorkspaceStageAsync(ImagesChatToolContext ctx, Guid stageId, long expectedCompositionRevision)
    {
        try { var result = await compositions.ApplyWorkspaceStageAsync(ctx.ProjectId, ctx.ConversationId, stageId, expectedCompositionRevision, ctx.TurnCancellationToken); ctx.MarkMutated(); return JsonSerializer.Serialize(new { ok = true, targetId = result.Composition.Id, revision = result.Composition.Revision, variantId = result.Variant.Id, variantRevision = result.Variant.Revision, changedIds = result.ChangedBlockIds, summary = "Designed Page content and layout applied atomically.", mutation = new { kind = "pageComposition", id = result.Composition.Id, selectId = result.Variant.Id } }, JsonOptions); }
        catch (CompositionRevisionConflictException ex) { return JsonSerializer.Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = stageId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread the compact workspace and submit a new non-replayed stage." }, JsonOptions); }
        catch (Exception ex) { return JsonSerializer.Serialize(new { ok = false, code = "WORKSPACE_STAGE_REJECTED", targetId = stageId, summary = ex.Message }, JsonOptions); }
    }

    private async Task<string> ReadCoverAsync(ImagesChatToolContext ctx, Guid editionId, int objectStart, int objectCount, int structureStart, int structureCount)
    {
        try
        {
            var cover = await covers.GetAsync(ctx.ProjectId, editionId, ctx.TurnCancellationToken);
            var scene = JsonSerializer.Deserialize<CompositionScene>(cover.CompositionSceneJson, ManuscriptCodec.JsonOptions) ?? new CompositionScene();
            objectStart = Math.Clamp(objectStart, 0, scene.Objects.Count);
            objectCount = Math.Clamp(objectCount, 1, 50);
            structureStart = Math.Max(0, structureStart);
            structureCount = Math.Clamp(structureCount, 1, 50);
            var objects = scene.Objects.Skip(objectStart).Take(objectCount).ToList();
            return JsonSerializer.Serialize(new { ok = true, targetId = cover.Id, editionId, revision = cover.Revision, summary = $"{scene.Objects.Count} cover object(s).", cover.Title, cover.Subtitle, cover.Author, cover.SpineText, cover.BackCopy, cover.Template, cover.Diagnostics, scene.SchemaVersion, scene.Surface, layers = scene.Layers.Skip(structureStart).Take(structureCount), styles = scene.Styles.Skip(structureStart).Take(structureCount), guides = scene.Guides.Skip(structureStart).Take(structureCount), objects, continuation = new { objects = new { start = objectStart, returned = objects.Count, total = scene.Objects.Count, hasMore = objectStart + objects.Count < scene.Objects.Count, nextStart = objectStart + objects.Count < scene.Objects.Count ? objectStart + objects.Count : (int?)null }, structure = new { start = structureStart, count = structureCount, layerTotal = scene.Layers.Count, styleTotal = scene.Styles.Count, guideTotal = scene.Guides.Count } } }, JsonOptions);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return JsonSerializer.Serialize(new { ok = false, code = "NOT_FOUND", targetId = editionId, summary = ex.Message }, JsonOptions);
        }
    }

    private async Task<string> PatchCoverElementAsync(ImagesChatToolContext ctx, Guid editionId, long expectedRevision, string targetKind, Guid targetId, CompositionElementPatch patch)
    {
        try { var cover = await covers.PatchElementAsync(ctx.ProjectId, editionId, expectedRevision, targetKind, targetId, patch, ctx.TurnCancellationToken); ctx.MarkMutated(); return JsonSerializer.Serialize(new { ok = true, targetId, revision = cover.Revision, changedIds = new[] { targetId }, summary = $"Patched cover {targetKind} {targetId:N}.", mutation = new { kind = "coverComposition", id = editionId } }, JsonOptions); }
        catch (Exception ex) { return JsonSerializer.Serialize(new { ok = false, code = ex is Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException ? "REVISION_CONFLICT" : "PATCH_REJECTED", targetId, summary = ex.Message, recovery = "Reread the cover and retry only the intended fields against its current revision." }, JsonOptions); }
    }

    private async Task<string> ValidateCoverAsync(ImagesChatToolContext ctx, Guid editionId)
    {
        try
        {
            var cover = await covers.GetAsync(ctx.ProjectId, editionId, ctx.TurnCancellationToken);
            return JsonSerializer.Serialize(new { ok = cover.Diagnostics.Count == 0, targetId = editionId, revision = cover.Revision, summary = cover.Diagnostics.Count == 0 ? "Cover validation passed." : $"Cover validation found {cover.Diagnostics.Count} diagnostic(s).", diagnosticCounts = new { errors = cover.Diagnostics.Count, warnings = 0 }, diagnostics = cover.Diagnostics.Take(12) }, JsonOptions);
        }
        catch (Exception ex) { return JsonSerializer.Serialize(new { ok = false, code = "VALIDATION_FAILED", targetId = editionId, summary = ex.Message }, JsonOptions); }
    }

    private async Task<string> StageCoverAsync(ImagesChatToolContext ctx, Guid editionId, long expectedRevision, CompositionScene scene)
    {
        try
        {
            var stage = await covers.StageSceneAsync(ctx.ProjectId, ctx.ConversationId, editionId, expectedRevision, scene, ctx.TurnCancellationToken);
            return JsonSerializer.Serialize(new { ok = true, targetId = editionId, revision = expectedRevision, stageId = stage.Id, stage.ExpiresAt, summary = $"Validated {scene.Objects.Count} cover object(s)." }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, code = ex is Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException ? "REVISION_CONFLICT" : "INVALID_SCENE", targetId = editionId, summary = ex.Message, recovery = "Reread the compact cover and submit one replacement stage." }, JsonOptions);
        }
    }

    private async Task<string> ApplyCoverStageAsync(ImagesChatToolContext ctx, Guid stageId, long expectedRevision)
    {
        try
        {
            var cover = await covers.ApplySceneStageAsync(ctx.ProjectId, ctx.ConversationId, stageId, expectedRevision, ctx.TurnCancellationToken);
            ctx.MarkMutated();
            return JsonSerializer.Serialize(new { ok = true, targetId = cover.Id, revision = cover.Revision, changedIds = new[] { cover.Id }, summary = "Staged cover composition applied.", mutation = new { kind = "coverComposition", id = cover.EditionId } }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, code = ex is Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException ? "REVISION_CONFLICT" : "STAGE_REJECTED", targetId = stageId, summary = ex.Message, recovery = "Reread the compact cover and submit a new non-replayed stage." }, JsonOptions);
        }
    }

    private async Task<string> ReadLayoutGenerationTargetAsync(ImagesChatToolContext ctx, string targetKind, Guid targetId, Guid? variantId, Guid? editionId)
    {
        try
        {
            var descriptor = targetKind.StartsWith("cover", StringComparison.OrdinalIgnoreCase)
                ? await compositions.DescribeGenerationTargetAsync(
                    ctx.ProjectId,
                    editionId ?? throw new ArgumentException("Cover targets require editionId."),
                    targetKind,
                    targetId,
                    variantId,
                    ctx.TurnCancellationToken)
                : await compositions.DescribeAuthoringGenerationTargetAsync(
                    ctx.ProjectId,
                    targetKind,
                    targetId,
                    variantId,
                    ctx.TurnCancellationToken);
            return JsonSerializer.Serialize(new { ok = true, targetId, summary = $"{descriptor.AspectRatio}; {descriptor.RecommendedWidthPixels}x{descriptor.RecommendedHeightPixels}px.", descriptor }, JsonOptions);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return JsonSerializer.Serialize(new { ok = false, code = "INVALID_TARGET", targetId, summary = ex.Message }, JsonOptions);
        }
    }

    private async Task<string> ValidateCompositionAsync(ImagesChatToolContext ctx, Guid variantId)
    {
        try
        {
            var result = await compositions.ValidateAuthoringVariantAsync(ctx.ProjectId, variantId, ctx.TurnCancellationToken);
            return JsonSerializer.Serialize(new { ok = result.ErrorCount == 0, targetId = result.TargetId, revision = result.Revision, summary = $"Validation found {result.ErrorCount} error(s) and {result.WarningCount} warning(s).", diagnosticCounts = new { errors = result.ErrorCount, warnings = result.WarningCount }, diagnostics = result.Diagnostics }, JsonOptions);
        }
        catch (Exception ex) { return JsonSerializer.Serialize(new { ok = false, code = "VALIDATION_FAILED", targetId = variantId, summary = ex.Message }, JsonOptions); }
    }

    private async Task<string> CreateShapeMaskAsync(ImagesChatToolContext ctx, Guid imageId, string label, ProjectImageMaskShape[] shapes)
    {
        if (shapes.Length == 0)
            return "Error: at least one mask shape is required.";

        var mask = await imageJobs.CreateMaskFromShapesAsync(
            ctx.ProjectId,
            imageId,
            new ProjectImageMaskShapeRequest(label, shapes),
            CancellationToken.None);
        ctx.MarkMutated();
        return JsonSerializer.Serialize(new
        {
            message = "Mask saved.",
            mask,
        }, JsonOptions);
    }

    private async Task<string> CropProjectImageAsync(
        ImagesChatToolContext ctx,
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
                return $"Error: {targetValidation.Error} Use an entity id returned by project/entity reads; otherwise omit entityTarget.";

            var image = await projectImages.CropAsync(ctx.ProjectId, sourceImageId, new ProjectImageCropRequest(
                crop,
                fileName?.Trim() ?? string.Empty,
                altText?.Trim() ?? string.Empty));
            object? attached = null;
            if (targetValidation.Targets is [var target])
            {
                var example = await entityVisualExamples.AttachAsync(
                    ctx.ProjectId,
                    target.EntityId,
                    image.Id,
                    target.Label,
                    EntityVisualExampleOrigin.Agent);
                attached = VisualPayload(example);
            }

            ctx.AddVisual(await BuildVisualAsync(ctx, image, image.FileName, "Cropped project image saved to the library."));
            ctx.AddModelOnlyImage(image);
            ctx.MarkMutated();
            return JsonSerializer.Serialize(new
            {
                status = "cropped",
                sourceImageId,
                image = ImagePayload(image),
                canonicalReference = attached,
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private async Task<string> GenerateImageAsync(
        ImagesChatToolContext ctx,
        ImageGenerationBrief brief,
        ImageReferenceUse[]? references,
        ImageGenerationTarget? target,
        string? altText,
        string? quality,
        string? outputFormat,
        int? outputCompression,
        int count,
        string? label)
    {
        CompiledImagePrompt compiled;
        try
        {
            compiled = await imagePrompts.CompileGenerationAsync(
                ctx.ProjectId,
                brief,
                references,
                target,
                ctx.TurnCancellationToken);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return $"Error: {ex.Message}";
        }

        ProjectImageJobView job;
        try
        {
            job = await imageJobs.CreateGenerateJobAsync(ctx.ProjectId, new ProjectImageGenerateJobRequest(
                compiled.Prompt,
                compiled.Size,
                CleanOr(quality, imageOptions.Value.DefaultQuality),
                CleanOr(outputFormat, imageOptions.Value.DefaultOutputFormat),
                outputCompression,
                altText?.Trim() ?? string.Empty,
                Math.Clamp(count, 1, Math.Max(1, imageOptions.Value.MaxOutputs)),
                compiled.ReferenceImageIds,
                label,
                EntityTargets: null,
                compiled.BriefJson,
                compiled.ReferenceManifestJson,
                compiled.TargetGeometryJson), ctx.TurnCancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return $"Error: {ex.Message}";
        }
        return await RunQueuedJobToolAsync(ctx, job.Id);
    }

    private async Task<string> EditImageAsync(
        ImagesChatToolContext ctx,
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
        int? outputCompression,
        int count,
        string? label)
    {
        if (sourceImageId == Guid.Empty)
            return "Error: sourceImageId is required.";
        CompiledImagePrompt compiled;
        try
        {
            compiled = await imagePrompts.CompileEditAsync(
                ctx.ProjectId,
                sourceImageId,
                brief,
                references,
                target,
                ctx.TurnCancellationToken);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return $"Error: {ex.Message}";
        }

        Guid? effectiveMaskId = maskId;
        if (effectiveMaskId is null && maskShapes is { Length: > 0 })
        {
            var mask = await imageJobs.CreateMaskFromShapesAsync(
                ctx.ProjectId,
                sourceImageId,
                new ProjectImageMaskShapeRequest(maskLabel ?? "Agent edit mask", maskShapes),
                ctx.TurnCancellationToken);
            effectiveMaskId = mask.Id;
        }

        ProjectImageJobView job;
        try
        {
            job = await imageJobs.CreateEditJobAsync(ctx.ProjectId, new ProjectImageEditJobRequest(
                sourceImageId,
                compiled.Prompt,
                compiled.Size,
                CleanOr(quality, imageOptions.Value.DefaultQuality),
                CleanOr(outputFormat, imageOptions.Value.DefaultOutputFormat),
                outputCompression,
                altText?.Trim() ?? string.Empty,
                Math.Clamp(count, 1, Math.Max(1, imageOptions.Value.MaxOutputs)),
                MaskPngDataUrl: null,
                ReferenceImageIds: compiled.ReferenceImageIds,
                Label: label,
                ExistingMaskId: effectiveMaskId,
                EntityTargets: null,
                InheritSourceEntityTargets: false,
                BriefJson: compiled.BriefJson,
                ReferenceManifestJson: compiled.ReferenceManifestJson,
                TargetGeometryJson: compiled.TargetGeometryJson), ctx.TurnCancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return $"Error: {ex.Message}";
        }
        return await RunQueuedJobToolAsync(ctx, job.Id);
    }

    private async Task<string> RunQueuedJobToolAsync(ImagesChatToolContext ctx, Guid jobId)
    {
        ctx.TrackImageGenerationJob(jobId);
        try
        {
            await imageRuntime.EnqueueProjectAsync(ctx.ProjectId, ctx.TurnCancellationToken);
            var timeout = TimeSpan.FromSeconds(Math.Clamp(imageOptions.Value.AgentJobWaitTimeoutSeconds, 1, 3600));
            var completed = await imageRuntime.WaitForJobCompletionAsync(jobId, timeout, ctx.TurnCancellationToken);
            var job = await imageJobs.GetJobAsync(ctx.ProjectId, jobId, ctx.TurnCancellationToken);
            if (job is null)
                return $"Error: image job {jobId:N} was not found after queueing.";

            var outputs = new List<object>();
            foreach (var imageId in job.OutputImageIds)
            {
                var image = await projectImages.GetAsync(ctx.ProjectId, imageId, ctx.TurnCancellationToken);
                if (image is null)
                    continue;

                var caption = string.Equals(ctx.CurrentToolName, "edit_image", StringComparison.Ordinal)
                    ? "Edited output saved to the image library."
                    : "Generated output saved to the image library.";
                var visual = await BuildVisualAsync(ctx, image, title: image.FileName, caption: caption);
                ctx.AddVisual(visual);
                ctx.AddModelOnlyImage(image);
                outputs.Add(ImageOutputPayload(image, job.Size, visual.Width, visual.Height));
            }

            ctx.MarkMutated();
            return JsonSerializer.Serialize(new
            {
                completed,
                job = JobPayload(job),
                images = outputs,
                note = completed ? null : "Timed out waiting for the image job. The Images tab will continue showing progress.",
            }, JsonOptions);
        }
        catch (OperationCanceledException) when (ctx.TurnCancellationToken.IsCancellationRequested)
        {
            await imageRuntime.CancelJobAsync(ctx.ProjectId, jobId, CancellationToken.None);
            return "Cancelled.";
        }
    }

    private async Task<string> AddProjectImageToContextAsync(ImagesChatToolContext ctx, Guid chapterId, Guid imageId)
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
        ctx.MarkMutated();
        return "Image added to chapter context.";
    }

    private async Task<ImagesChatVisualAttachment> BuildVisualAsync(
        ImagesChatToolContext ctx,
        ProjectImageView image,
        string? title = null,
        string? caption = null)
    {
        var data = await projectImages.GetDataAsync(ctx.ProjectId, image.Id, maxEdge: null, CancellationToken.None);
        var size = data is null ? (Width: (int?)null, Height: (int?)null) : ReadSize(data.Data);
        return new ImagesChatVisualAttachment(
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

    private int EffectiveReadChapterPageMaxChars() => Math.Max(256, editorOptions.Value.ReadChapterPageMaxChars);

    private static int CountTextPages(string text, int pageMaxChars) =>
        string.IsNullOrEmpty(text) ? 1 : (text.Length + pageMaxChars - 1) / pageMaxChars;

    private static string CleanOr(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

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

    private static object VisualPayload(EntityVisualExampleView example) => new
    {
        example.Id, example.EntityId, example.EntityName, example.EntityType, example.Label, example.SortOrder,
        image = ImagePayload(example.Image),
    };

    private static object ImagePayload(ProjectImageView image) => new
    {
        image.Id,
        image.FileName,
        image.ContentType,
        image.PreviewUrl,
        FullUrl = image.PreviewUrl.Replace("?maxEdge=640", string.Empty, StringComparison.Ordinal),
        image.AltText,
        image.Source,
        image.Prompt,
        image.GenerationModel,
        image.SourceMetadataJson,
        image.CreatedAt,
        image.UpdatedAt,
        image.SizeBytes,
    };

    private static object ImageOutputPayload(ProjectImageView image, string requestedSize, int? width, int? height) => new
    {
        image.Id,
        image.FileName,
        image.ContentType,
        image.PreviewUrl,
        FullUrl = image.PreviewUrl.Replace("?maxEdge=640", string.Empty, StringComparison.Ordinal),
        image.AltText,
        image.Source,
        image.Prompt,
        image.GenerationModel,
        image.SourceMetadataJson,
        requestedSize,
        actualRaster = RasterMetadata(width, height),
        image.CreatedAt,
        image.UpdatedAt,
        image.SizeBytes,
    };

    private static object JobPayload(ProjectImageJobView job) => new
    {
        job.Id,
        job.Kind,
        job.Status,
        job.Label,
        job.Prompt,
        job.BriefJson,
        job.ReferenceManifestJson,
        job.TargetGeometryJson,
        job.ProviderRevisedPrompts,
        job.Size,
        job.Quality,
        job.OutputFormat,
        job.Count,
        job.SourceImageId,
        job.MaskId,
        job.ReferenceImageIds,
        job.OutputImageIds,
        job.OutputStates,
        job.OutputErrors,
        job.Error,
        job.CreatedAt,
        job.StartedAt,
        job.CompletedAt,
    };

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


}
