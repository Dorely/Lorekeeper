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
using Lorekeeper.Projects;
using Lorekeeper.Search;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Lorekeeper.ImagesChat;

public sealed class ImagesChatTools(
    IChapterService chapters,
    IProjectSearchService projectSearch,
    IReferenceVisualService referenceVisuals,
    IProjectImageService projectImages,
    IEntityVisualExampleService entityVisualExamples,
    IEntityService entities,
    IAgentProjectImageWorkflow imageWorkflow,
    IManuscriptService manuscripts,
    IChapterSemanticProjectionService semanticProjection,
    IBookBriefService bookBriefs,
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
                description: "Return compact current-project and direct-reference source discovery with origin project provenance and exact read_project_source arguments. References are read-only continuity evidence."),

            AIFunctionFactory.Create(
                method: (string sourceType, Guid sourceId, int? pageNumber = null, Guid? originProjectId = null) =>
                    ReadProjectSourceAsync(context, sourceType, sourceId, pageNumber, originProjectId),
                name: "read_project_source",
                description: "Read one paginated active-project or direct-reference source. Pass the exact originProjectId returned by discovery; arbitrary foreign IDs are rejected."),

            AIFunctionFactory.Create(
                method: (string query, int topK = 8, string[]? sourceTypes = null, string[]? sourceIds = null, Guid? containerSourceId = null, bool lexicalOnly = false) =>
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
                description: "List chapters with ids, order, title, synopsis, body line count, and read_chapter page count."),

            AIFunctionFactory.Create(
                method: (Guid chapterId, int? pageNumber = null) => ReadChapterAsync(context, chapterId, pageNumber),
                name: "read_chapter",
                description: "Read one paginated page of a chapter body with line numbers. Use pageNumber from returned pagination to continue."),

            AIFunctionFactory.Create(
                method: () => ListProjectImagesAsync(context),
                name: "list_project_images",
                description: "Discover project-library images that are not already attached to the current turn. Current-turn attachments already include complete library metadata and, for a vision-ready provider, pixels; they are excluded from this tool."),

            AIFunctionFactory.Create(
                method: (Guid imageId) => ReadProjectImageAsync(context, imageId),
                name: "read_project_image",
                description: "Read one project image that is not already attached to the current turn, returning complete metadata and loading its pixels as visual context when supported. Attached image IDs already have complete metadata and, for a vision-ready provider, pixels; they must not be reread."),

            AIFunctionFactory.Create(
                method: () => ReadProjectVisualDirectionAsync(context),
                name: "read_project_visual_direction",
                description: "Read the current project-wide Visual Direction exactly before proposing or saving a coherent approved art direction."),

            AIFunctionFactory.Create(
                method: (string expectedCurrentVisualDirection, string visualDirection) =>
                    SetProjectVisualDirectionAsync(context, expectedCurrentVisualDirection, visualDirection),
                name: "set_project_visual_direction",
                description: "Save only the user-approved project-wide Visual Direction. Pass the exact current value from read_project_visual_direction; stale updates are rejected instead of overwriting newer direction."),

            AIFunctionFactory.Create(
                method: (Guid entityId, int? pageNumber = null) => ReadEntityAsync(context, entityId, pageNumber),
                name: "read_entity",
                description: "Read an explicitly paginated entity with all adjacent manual and AutoMention links plus its ordered canonical visual references. Full identity fields and GUIDs repeat on every page; omit pageNumber for page 1 and follow nextPageArguments. Vision-ready providers receive the image bytes on the next round."),

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
                description: "Attach one approved, stable appearance or design image to an eligible story entity as a labeled canonical reference. Use isolated studies for characters/objects and intentional environmental studies for locations; never attach an ordinary narrative scene merely because the entity appears in it."),

            AIFunctionFactory.Create(
                method: (Guid canonicalReferenceId, string label, int? sortOrder = null) => UpdateEntityVisualAsync(context, canonicalReferenceId, label, sortOrder),
                name: "update_entity_canonical_reference",
                description: "Relabel or reorder an entity canonical visual reference."),

            AIFunctionFactory.Create(
                method: (Guid canonicalReferenceId) => DetachEntityVisualAsync(context, canonicalReferenceId),
                name: "detach_entity_canonical_reference",
                description: "Detach an entity canonical visual reference without deleting the library image."),

            AIFunctionFactory.Create(
                method: (Guid sourceImageId, ProjectImageCropRegion crop, string? fileName = null, string? altText = null, EntityVisualTarget? entityTarget = null) =>
                    CropProjectImageAsync(context, sourceImageId, crop, fileName, altText, entityTarget),
                name: "crop_project_image",
                description: "Create a non-destructive project-library crop from an existing image using 0-100 percentage coordinates. Inspect the source first or use user-supplied coordinates and describe only the cropped subject in altText. Optionally attach the tight subject-only crop to one entity as its canonical reference; make separate crops for separate entities. Source associations are never inherited."),

            AIFunctionFactory.Create(
                method: (Guid sourceImageId, int width, int height, string? fileName = null, string? altText = null) =>
                    ResizeImageAsync(context, sourceImageId, width, height, fileName, altText),
                name: "resize_project_image",
                description: "Deterministically resize an existing project image to an exact provider-valid WIDTHxHEIGHT raster while preserving its aspect ratio. This local pixel transform creates a new unattached source-linked image and adds no visual detail; it is not publication-quality enhancement. Use edit_project_image with the original source for same-aspect detail reconstruction or intentional outpainting."),

            AIFunctionFactory.Create(
                method: (ImageGenerationBrief brief, ImageReferenceUse[]? references = null, string? altText = null, string? quality = null, string? outputFormat = null, int? outputCompression = null, int? minimumDpi = null, string? aspectRatio = null, string? label = null) =>
                    GenerateImageAsync(context, brief, references, altText, quality, outputFormat, outputCompression, minimumDpi, aspectRatio, label),
                name: "generate_project_image",
                description: $"Generate one free-standing, unattached project-library image and wait for a terminal result. Omit minimumDpi for the moderate Core Book page raster; when explicitly requested, minimumDpi uses the exact Core Book page as its physical basis, or the largest fitting rectangle for aspectRatio. An infeasible request hard-rejects before dispatch with provider limits and a panel plan; use multiple Figure blocks or a Designed Page for multi-image publication coverage. Inspect effectiveDpi and minimumDpiMet before promoting it. You may pass at most {Math.Max(0, imageOptions.Value.MaxReferenceImages)} references."),

            AIFunctionFactory.Create(
                method: (Guid sourceImageId, ImageEditBrief brief, ProjectImageMaskShape[]? regionalGuideShapes = null, ImageReferenceUse[]? references = null, string? altText = null, string? quality = null, string? outputFormat = null, int? outputCompression = null, int? minimumDpi = null, string? aspectRatio = null, string? label = null) =>
                    EditImageAsync(context, sourceImageId, brief, regionalGuideShapes, references, altText, quality, outputFormat, outputCompression, minimumDpi, aspectRatio, label),
                name: "edit_project_image",
                description: $"Edit one project image and wait for a terminal result. Unmasked edits use the moderate Core Book page raster by default and may explicitly request minimumDpi plus an optional aspectRatio. Same-aspect up-resolution preserves complete source framing/content while reconstructing credible detail; it does not zoom out, crop, or invent surrounding canvas. Intentional framing expansion is separate outpainting: describe the new surroundings and direction in desired-result and composition. Regional guides are source-geometry-bound, reject explicit DPI, and accept only an aspect that preserves the source. An infeasible DPI request hard-rejects before dispatch; use multiple Figure blocks or a Designed Page when publication coverage requires multiple images. The output is unattached; inspect effectiveDpi and minimumDpiMet before promoting it. You may pass at most {Math.Max(0, imageOptions.Value.MaxReferenceImages)} references."),

            AIFunctionFactory.Create(
                method: (Guid jobId) => ReadImageJobAsync(context, jobId, wait: false),
                name: "read_project_image_job",
                description: "Read compact state for a previously started project-image generation or edit job without replaying its prompt."),

            AIFunctionFactory.Create(
                method: (Guid jobId) => ReadImageJobAsync(context, jobId, wait: true),
                name: "wait_project_image_job",
                description: "Reconnect to a previously started project-image generation or edit job and wait for its terminal result. Completed images remain unattached."),

            AIFunctionFactory.Create(
                method: (Guid jobId) => CancelImageJobAsync(context, jobId),
                name: "cancel_project_image_job",
                description: "Cancel a previously started project-image generation or edit job."),
        ];

        return Task.FromResult(tools);
    }

    private async Task<string> ListSearchSourcesAsync(ImagesChatToolContext ctx, string? query, string[]? sourceTypes, int topK)
    {
        topK = Math.Clamp(topK, 1, 30);
        var sources = await projectSearch.ListSourcesAsync(ctx.ProjectId, query, sourceTypes, topK, includeReferencedProjects: true);
        return ProjectSearchAgentPayload.SerializeSources(sources);
    }

    private async Task<string> ListReferenceVisualsAsync(ImagesChatToolContext ctx)
    {
        var visuals = await referenceVisuals.ListAsync(ctx.ProjectId, ctx.TurnCancellationToken);
        return JsonSerializer.Serialize(new
        {
            resultKind = "referenceVisualDiscovery", returnedCount = visuals.Count,
            boundedLimit = ReferenceVisualService.MaximumListResults,
            mayHaveMore = visuals.Count == ReferenceVisualService.MaximumListResults,
            note = "Direct-reference canonical visuals are read-only continuity evidence and cannot be placed or mutated in the active project.",
            visuals,
        }, JsonOptions);
    }

    private async Task<string> ReadReferenceVisualAsync(ImagesChatToolContext ctx, Guid originProjectId, Guid imageId)
    {
        var visual = await referenceVisuals.ReadAsync(ctx.ProjectId, originProjectId, imageId, ctx.VisionReady, ctx.TurnCancellationToken);
        if (visual is null) return $"Error: image {imageId:N} is not an eligible canonical visual on a direct referenced project.";
        if (visual.Data is not null)
            ctx.AddModelOnlyImage(new ProjectImageView(visual.ImageId, visual.FileName, visual.ContentType, visual.PreviewUrl, visual.AltText, visual.ImageSource, visual.Prompt, string.Empty, string.Empty, DateTime.UtcNow, DateTime.UtcNow, visual.Data.LongLength), visual.Data);
        return JsonSerializer.Serialize(new
        {
            visual.OriginProjectId, visual.OriginProjectName, visual.OriginProjectSlug,
            visual.EntityId, visual.EntityType, visual.EntityName, visual.CanonicalReferenceId,
            visual.Label, visual.SortOrder, visual.ImageId, visual.FileName, visual.ContentType,
            visual.PreviewUrl, visual.AltText, visual.Prompt, visual.ImageSource,
            visual.IsReferenced, visual.DataDelivered,
            detailReadArguments = new { originProjectId = visual.OriginProjectId, imageId = visual.ImageId },
        }, JsonOptions);
    }

    private async Task<string> ReadProjectSourceAsync(ImagesChatToolContext ctx, string sourceType, Guid sourceId, int? pageNumber, Guid? originProjectId)
    {
        var result = await projectSearch.ReadSourceAsync(ctx.ProjectId, sourceType, sourceId, pageNumber, originProjectId: originProjectId);
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
            sourceEvidence = entity.SourceEvidence,
            entity.IsIngestCreated,
            entity.SourceEvidenceCount,
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
            lexicalOnly,
            IncludeReferencedProjects: true));

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

        var text = ChapterFormatting.WithLineNumbers((await manuscripts.GetManuscriptAsync(EditorContentTarget.Core, chapter.Id))?.PlainText ?? chapter.PlainText);
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
        var images = (await projectImages.ListAsync(ctx.ProjectId))
            .Where(image => ctx.FindAttachedImage(image.Id) is null)
            .ToList();
        if (images.Count == 0)
            return "No additional project images outside the current-turn attachments.";

        return JsonSerializer.Serialize(images.Select(ImagesChatImagePayload.From), JsonOptions);
    }

    private async Task<string> ReadProjectImageAsync(ImagesChatToolContext ctx, Guid imageId)
    {
        if (ctx.FindAttachedImage(imageId) is { } attachedImage)
        {
            return JsonSerializer.Serialize(new
            {
                image = ImagesChatImagePayload.From(attachedImage),
                delivery = ctx.VisionReady
                    ? "complete metadata and full image bytes were already supplied in the current-turn attachment context; no additional visual read was performed"
                    : "complete metadata was already supplied in the current-turn attachment context; the active chat provider is not vision-ready, so no visual read was performed",
            }, JsonOptions);
        }

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
            image = ImagesChatImagePayload.From(image),
            delivery = ctx.VisionReady
                ? "full image bytes will be supplied to the model on the next iteration"
                : "metadata only; the active chat provider is not vision-ready",
        }, JsonOptions);
    }

    private async Task<string> ReadProjectVisualDirectionAsync(ImagesChatToolContext ctx)
    {
        var brief = await bookBriefs.GetOrCreateAsync(ctx.ProjectId, ctx.TurnCancellationToken);
        return JsonSerializer.Serialize(new
        {
            visualDirection = brief.VisualDirection,
            brief.UpdatedAt,
            summary = string.IsNullOrWhiteSpace(brief.VisualDirection)
                ? "No project-wide Visual Direction has been saved."
                : "Current project-wide Visual Direction read.",
        }, JsonOptions);
    }

    private async Task<string> SetProjectVisualDirectionAsync(
        ImagesChatToolContext ctx,
        string expectedCurrentVisualDirection,
        string visualDirection)
    {
        try
        {
            var brief = await bookBriefs.UpdateVisualDirectionAsync(
                ctx.ProjectId,
                expectedCurrentVisualDirection,
                visualDirection,
                ctx.TurnCancellationToken);
            ctx.MarkMutated();
            return JsonSerializer.Serialize(new
            {
                ok = true,
                visualDirection = brief.VisualDirection,
                brief.UpdatedAt,
                summary = "Approved project-wide Visual Direction saved.",
                mutation = new { kind = "bookBriefVisualDirection", id = ctx.ProjectId },
            }, JsonOptions);
        }
        catch (BookBriefVisualDirectionConflictException ex)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                code = "VISUAL_DIRECTION_CONFLICT",
                actualVisualDirection = ex.ActualVisualDirection,
                summary = ex.Message,
                recovery = "Review the current direction with the user before attempting a replacement.",
            }, JsonOptions);
        }
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
                image = ImagesChatImagePayload.From(image),
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
        string? altText,
        string? quality,
        string? outputFormat,
        int? outputCompression,
        int? minimumDpi,
        string? aspectRatio,
        string? label)
    {
        try
        {
            var result = await imageWorkflow.GenerateAsync(
                ctx.ProjectId,
                brief,
                references,
                MinimumDpiTarget(minimumDpi, aspectRatio),
                altText,
                quality,
                outputFormat,
                outputCompression,
                string.IsNullOrWhiteSpace(label) ? "Images assistant image" : label.Trim(),
                ctx.TrackImageGenerationJob,
                ctx.TurnCancellationToken);
            return await BuildImageResultAsync(ctx, result, "Generated output saved to the image library.");
        }
        catch (MinimumDpiUnachievableException ex)
        {
            return SerializeMinimumDpiRejection(ex);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return $"Error: {ex.Message}";
        }
    }

    private async Task<string> EditImageAsync(
        ImagesChatToolContext ctx,
        Guid sourceImageId,
        ImageEditBrief brief,
        ProjectImageMaskShape[]? regionalGuideShapes,
        ImageReferenceUse[]? references,
        string? altText,
        string? quality,
        string? outputFormat,
        int? outputCompression,
        int? minimumDpi,
        string? aspectRatio,
        string? label)
    {
        if (sourceImageId == Guid.Empty)
            return "Error: sourceImageId is required.";
        if (regionalGuideShapes is { Length: 0 })
            return "Error: regionalGuideShapes must contain at least one shape when supplied.";

        var regionalGuide = regionalGuideShapes is { Length: > 0 }
            ? new ProjectImageMaskShapeRequest("Images assistant regional guide", regionalGuideShapes)
            : null;

        try
        {
            var result = await imageWorkflow.EditAsync(
                ctx.ProjectId,
                sourceImageId,
                brief,
                regionalGuide,
                references,
                MinimumDpiTarget(minimumDpi, aspectRatio),
                altText,
                quality,
                outputFormat,
                outputCompression,
                string.IsNullOrWhiteSpace(label) ? "Images assistant edit" : label.Trim(),
                ctx.TrackImageGenerationJob,
                ctx.TurnCancellationToken);
            return await BuildImageResultAsync(ctx, result, "Edited output saved to the image library.");
        }
        catch (MinimumDpiUnachievableException ex)
        {
            return SerializeMinimumDpiRejection(ex);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return $"Error: {ex.Message}";
        }
    }

    private async Task<string> ResizeImageAsync(
        ImagesChatToolContext ctx,
        Guid sourceImageId,
        int width,
        int height,
        string? fileName,
        string? altText)
    {
        try
        {
            var image = await projectImages.ResizeAsync(
                ctx.ProjectId,
                sourceImageId,
                new ProjectImageResizeRequest(width, height, fileName?.Trim() ?? string.Empty, altText?.Trim() ?? string.Empty),
                ctx.TurnCancellationToken);
            ctx.AddVisual(await BuildVisualAsync(ctx, image, image.FileName, "Deterministically resized project image saved to the library."));
            ctx.AddModelOnlyImage(image);
            ctx.MarkMutated();
            return JsonSerializer.Serialize(new
            {
                ok = true,
                status = "resized",
                sourceImageId,
                targetRaster = $"{width}x{height}",
                actualRaster = $"{width}x{height}",
                requestedMinimumDpi = (double?)null,
                minimumDpiMet = (bool?)null,
                warningCodes = Array.Empty<string>(),
                sourceLinked = true,
                attached = false,
                interpolation = ProjectImageResize.DeterministicInterpolation,
                addsNewDetail = false,
                image = ImagesChatImagePayload.From(image),
                summary = $"Created an unattached source-linked image at exactly {width}x{height} using {ProjectImageResize.DeterministicInterpolation}. This local resize adds no visual detail and is not publication-quality enhancement; use an original-source generative edit for detail reconstruction or intentional outpainting.",
            }, JsonOptions);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static ImageGenerationTarget? MinimumDpiTarget(int? minimumDpi, string? aspectRatio)
    {
        if (minimumDpi is null && string.IsNullOrWhiteSpace(aspectRatio))
            return null;
        return new ImageGenerationTarget
        {
            MinimumDpi = minimumDpi,
            AspectRatio = aspectRatio?.Trim() ?? string.Empty,
        };
    }

    private static string SerializeMinimumDpiRejection(MinimumDpiUnachievableException exception) =>
        JsonSerializer.Serialize(new
        {
            ok = false,
            code = MinimumDpiUnachievableException.Code,
            stage = exception.Stage,
            physicalDimensions = new
            {
                widthInches = exception.Resolution.WidthInches,
                heightInches = exception.Resolution.HeightInches,
            },
            requestedMinimumDpi = exception.RequestedMinimumDpi,
            requiredRaster = new { widthPixels = exception.RequiredRaster.Width, heightPixels = exception.RequiredRaster.Height, size = exception.RequiredRaster.Size },
            largestCompatibleRaster = exception.MaximumRaster is null
                ? null
                : new { widthPixels = (int?)exception.MaximumRaster.Width, heightPixels = (int?)exception.MaximumRaster.Height, size = exception.MaximumRaster.Size },
            maximumAchievableDpi = Math.Round(exception.MaximumAchievableDpi, 1),
            bindingProviderLimits = exception.Resolution.BindingProviderLimits,
            providerConstraints = new
            {
                sizeMultiple = LayoutImageSizeResolver.SizeMultiple,
                minimumPixels = LayoutImageSizeResolver.MinimumPixels,
                maximumPixels = LayoutImageSizeResolver.MaximumPixels,
                maximumEdgePixels = LayoutImageSizeResolver.MaximumEdge,
                minimumAspectRatio = 1d / LayoutImageSizeResolver.MaximumAspectRatio,
                maximumAspectRatio = LayoutImageSizeResolver.MaximumAspectRatio,
            },
            splitSuggestions = exception.SplitSuggestions.Select(suggestion => new
            {
                rows = suggestion.Rows,
                columns = suggestion.Columns,
                panelCount = suggestion.PanelCount,
                panels = suggestion.Panels.Select(panel => new
                {
                    panel.Index,
                    panel.XPercent,
                    panel.YPercent,
                    panel.WidthPercent,
                    panel.HeightPercent,
                    panel.WidthInches,
                    panel.HeightInches,
                    panel.AspectRatio,
                    raster = panel.Raster.Size,
                    effectiveDpi = Math.Round(panel.EffectiveDpi, 1),
                }),
            }),
            partitioningSupported = false,
            summary = exception.Message,
            recovery = "One Figure block holds one image. Use multiple Figure blocks, or confirm a structural change to a Designed Page before using a multi-image layout; treat independently generated images as deliberate panels rather than a seamless panorama.",
        }, JsonOptions);

    private async Task<string> BuildImageResultAsync(
        ImagesChatToolContext ctx,
        AgentProjectImageResult result,
        string caption)
    {
        var outputs = new List<object>();
        foreach (var output in result.Outputs)
        {
            var image = output.Image;
            var visual = await BuildVisualAsync(ctx, image, image.FileName, caption);
            ctx.AddVisual(visual);
            ctx.AddModelOnlyImage(image);
            if (output.PrintImageId is { } printImageId
                && await projectImages.GetAsync(ctx.ProjectId, printImageId, ctx.TurnCancellationToken) is { } printImage)
            {
                var printVisual = await BuildVisualAsync(
                    ctx,
                    printImage,
                    printImage.FileName,
                    "Print-upscaled derivative for the physical print target; place this image ID.");
                ctx.AddVisual(printVisual);
                ctx.AddModelOnlyImage(printImage);
            }
            outputs.Add(ImageOutputPayload(output));
        }
        if (result.Images.Count > 0)
            ctx.MarkMutated();
        return JsonSerializer.Serialize(new
        {
            ok = result.Succeeded,
            jobId = result.JobId,
            status = result.Status,
            targetAspect = result.TargetAspect,
            requestedRaster = result.RequestedRaster,
            requestedMinimumDpi = result.RequestedMinimumDpi,
            minimumDpiMet = result.MinimumDpiMet,
            warningCodes = result.WarningCodes ?? [],
            outputImageIds = result.Images.Select(image => image.Id),
            images = outputs,
            attached = false,
            diagnosticCounts = new { errors = result.Diagnostics.Count, warnings = result.Outputs.Count(output => !output.RasterMatched || !output.AspectMatched || output.MinimumDpiMet == false) },
            diagnostics = result.Diagnostics.Take(3),
            warnings = result.Outputs.Where(output => !output.RasterMatched || !output.AspectMatched || output.MinimumDpiMet == false).Select(output => new
            {
                code = output.MinimumDpiMet == false
                    ? "MINIMUM_DPI_NOT_MET"
                    : !output.RasterMatched && !output.AspectMatched
                        ? "PROVIDER_IMAGE_RASTER_AND_ASPECT_MISMATCH"
                        : !output.RasterMatched ? "PROVIDER_IMAGE_RASTER_MISMATCH" : "IMAGE_ASPECT_MISMATCH",
                message = output.MinimumDpiMet == false
                    ? $"Provider output {output.ActualRaster} achieved only {output.EffectiveDpi:0.0#} effective DPI, below the requested {output.RequestedMinimumDpi:0.0#}. Keep this unattached and do not describe it as publication-compliant."
                    : $"Provider output {output.ActualRaster} did not satisfy {(output.RasterMatched ? string.Empty : $"requested raster {result.RequestedRaster}")}{(!output.RasterMatched && !output.AspectMatched ? " and " : string.Empty)}{(output.AspectMatched ? string.Empty : $"target aspect {result.TargetAspect}")}. Inspect before placement or reporting the requested dimensions as achieved.",
            }),
            summary = result.Summary,
        }, JsonOptions);
    }

    private async Task<string> ReadImageJobAsync(ImagesChatToolContext ctx, Guid jobId, bool wait)
    {
        var result = wait
            ? await imageWorkflow.WaitAsync(ctx.ProjectId, jobId, ctx.TrackImageGenerationJob, ctx.TurnCancellationToken)
            : await imageWorkflow.ReadAsync(ctx.ProjectId, jobId, ctx.TurnCancellationToken);
        return result is null
            ? JsonSerializer.Serialize(new { ok = false, code = "NOT_FOUND", jobId, summary = "Project-image job was not found." }, JsonOptions)
            : await BuildImageResultAsync(ctx, result, "Completed project image reconnected from its job.");
    }

    private async Task<string> CancelImageJobAsync(ImagesChatToolContext ctx, Guid jobId)
    {
        await imageWorkflow.CancelAsync(ctx.ProjectId, jobId, ctx.TurnCancellationToken);
        return JsonSerializer.Serialize(new { ok = true, jobId, status = "cancelled", summary = "Project-image job cancellation requested; no image was placed." }, JsonOptions);
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
        image = ImagesChatImagePayload.From(example.Image),
    };

    private static object ImageOutputPayload(AgentProjectImageOutput output) => new
    {
        output.Image.Id,
        output.Image.FileName,
        output.Image.ContentType,
        output.Image.PreviewUrl,
        FullUrl = output.Image.PreviewUrl.Replace("?maxEdge=640", string.Empty, StringComparison.Ordinal),
        output.Image.AltText,
        output.Image.Source,
        output.Image.Prompt,
        output.Image.GenerationModel,
        output.ActualRaster,
        output.RasterMatched,
        output.AspectMatched,
        effectiveDpi = output.EffectiveDpi is { } dpi ? (double?)Math.Round(dpi, 1) : null,
        printImageId = output.PrintImageId,
        printRaster = output.PrintRaster,
        printEffectiveDpi = output.PrintEffectiveDpi is { } printDpi ? (double?)Math.Round(printDpi, 1) : null,
        requestedMinimumDpi = output.RequestedMinimumDpi,
        minimumDpiMet = output.MinimumDpiMet,
        warningCodes = output.WarningCodes ?? [],
        output.Image.CreatedAt,
        output.Image.UpdatedAt,
        output.Image.SizeBytes,
    };

    private static (int? Width, int? Height) ReadSize(byte[] data)
    {
        using var bitmap = SKBitmap.Decode(data);
        return bitmap is null
            ? ((int?)null, (int?)null)
            : (bitmap.Width, bitmap.Height);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";


}
