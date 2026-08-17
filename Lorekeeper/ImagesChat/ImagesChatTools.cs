using System.Text.Json;
using System.Text.Json.Nodes;
using Lorekeeper.Chapters;
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
    IProjectImageService projectImages,
    IEntityVisualExampleService entityVisualExamples,
    IEntityService entities,
    IProjectImageJobService imageJobs,
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
                method: (Guid imageId) => ReadProjectImageAsync(context, imageId),
                name: "read_project_image",
                description: "Read one project image's metadata and URLs and load it as visual context when the provider supports vision."),

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
                method: (Guid imageId, string label, ProjectImageMaskShape[] shapes) =>
                    CreateShapeMaskAsync(context, imageId, label, shapes),
                name: "create_shape_mask",
                description: "Create a PNG edit mask for an existing image from percentage-based rect/ellipse/polygon shapes. Transparent pixels are the editable regions."),

            AIFunctionFactory.Create(
                method: (ImageGenerationBrief brief, ImageReferenceUse[]? references = null, string? altText = null, string? quality = null, string? outputFormat = null, int? outputCompression = null, string? label = null) =>
                    GenerateImageAsync(context, brief, references, altText, quality, outputFormat, outputCompression, label),
                name: "generate_project_image",
                description: $"Generate one free-standing, unattached project-library image and wait for a terminal result. Inspect it before promoting it to an entity's canonical references. You may pass at most {Math.Max(0, imageOptions.Value.MaxReferenceImages)} references."),

            AIFunctionFactory.Create(
                method: (Guid sourceImageId, ImageEditBrief brief, Guid? maskId = null, ProjectImageMaskShape[]? maskShapes = null, string? maskLabel = null, ImageReferenceUse[]? references = null, string? altText = null, string? quality = null, string? outputFormat = null, int? outputCompression = null, string? label = null) =>
                    EditImageAsync(context, sourceImageId, brief, maskId, maskShapes, maskLabel, references, altText, quality, outputFormat, outputCompression, label),
                name: "edit_project_image",
                description: $"Edit one project image and wait for a terminal result. The output is a new free-standing, unattached library image; inspect it before promoting it to canon. You may pass at most {Math.Max(0, imageOptions.Value.MaxReferenceImages)} references."),

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
        string? altText,
        string? quality,
        string? outputFormat,
        int? outputCompression,
        string? label)
    {
        try
        {
            var result = await imageWorkflow.GenerateAsync(
                ctx.ProjectId,
                brief,
                references,
                null,
                altText,
                quality,
                outputFormat,
                outputCompression,
                string.IsNullOrWhiteSpace(label) ? "Images assistant image" : label.Trim(),
                ctx.TrackImageGenerationJob,
                ctx.TurnCancellationToken);
            return await BuildImageResultAsync(ctx, result, "Generated output saved to the image library.");
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
        Guid? maskId,
        ProjectImageMaskShape[]? maskShapes,
        string? maskLabel,
        ImageReferenceUse[]? references,
        string? altText,
        string? quality,
        string? outputFormat,
        int? outputCompression,
        string? label)
    {
        if (sourceImageId == Guid.Empty)
            return "Error: sourceImageId is required.";
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

        try
        {
            var result = await imageWorkflow.EditAsync(
                ctx.ProjectId,
                sourceImageId,
                brief,
                effectiveMaskId,
                references,
                null,
                altText,
                quality,
                outputFormat,
                outputCompression,
                string.IsNullOrWhiteSpace(label) ? "Images assistant edit" : label.Trim(),
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
            outputs.Add(ImageOutputPayload(output));
        }
        if (result.Images.Count > 0)
            ctx.MarkMutated();
        return JsonSerializer.Serialize(new
        {
            ok = result.Succeeded,
            jobId = result.JobId,
            status = result.Status,
            outputImageIds = result.Images.Select(image => image.Id),
            images = outputs,
            attached = false,
            diagnosticCounts = new { errors = result.Diagnostics.Count, warnings = 0 },
            diagnostics = result.Diagnostics.Take(3),
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
