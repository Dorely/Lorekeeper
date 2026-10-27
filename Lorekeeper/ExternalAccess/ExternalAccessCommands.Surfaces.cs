using System.Text.Json;
using Lorekeeper.Composition;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Images;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence;
using Lorekeeper.Publish;
using Lorekeeper.Writing;

namespace Lorekeeper.ExternalAccess;

internal sealed partial class ExternalAccessCommands
{
    private CompiledImagePrompt? _preparedImage;
    private ResolvedImageModel? _preparedImageModel;

    internal async Task PrepareMutationAsync(string operation, JsonElement arguments, CancellationToken ct)
    {
        if (operation == "save_publication_cover" && Workspace!.EditionId is Guid editionId)
            await Service<PublicationCoverPreviewService>().EnsureGeometryAsync(Work!.ProjectId, editionId, ct);
        if (operation is not ("generate_project_image" or "edit_project_image")) return;
        var input = arguments.Deserialize<ImageGenerateRequest>(ExternalToolCatalog.Json)!;
        if (operation == "edit_project_image" && input.SourceImageId is null || operation == "generate_project_image" && input.SourceImageId is not null)
            throw new ArgumentException("Use the generation tool without sourceImageId, or the edit tool with sourceImageId.");
        if (input.Target?.EditionId is Guid edition && Workspace!.EditionId != edition)
            throw new ExternalAccessException("TARGET_CHANGED", "Open the workspace on this exact release before generating for it.");
        _preparedImageModel = await Service<IImageProviderService>().ResolveAsync(cancellationToken: ct);
        _preparedImage = input.SourceImageId is Guid source
            ? await Service<IImagePromptComposer>().CompileEditAsync(input.ProjectId, _preparedImageModel.Constraints, source,
                input.EditBrief ?? throw new ArgumentException("An editBrief is required."), input.References, input.Target, ct, input.Background)
            : await Service<IImagePromptComposer>().CompileGenerationAsync(input.ProjectId, _preparedImageModel.Constraints,
                input.Brief ?? throw new ArgumentException("A generation brief is required."), input.References, input.Target, ct, input.Background);
    }

    internal async Task<object> ListImagesAsync(WorkspaceRequest input, CancellationToken ct) =>
        Page((await Service<IProjectImageService>().ListAsync(input.ProjectId, ct)).Select(ImageMetadata), input, "list_project_images");

    internal async Task<object> ReadImageAsync(ImageReadRequest input, CancellationToken ct)
    {
        var metadata = await Service<IProjectImageService>().GetAsync(input.ProjectId, input.ImageId, ct)
            ?? throw new KeyNotFoundException("Image not found.");
        var image = await Service<IProjectImageService>().GetDataAsync(input.ProjectId, input.ImageId, input.FullResolution ? null : 1600, ct)
            ?? throw new KeyNotFoundException("Image pixels are unavailable.");
        return new ExternalImageResult(image.Data, image.ContentType, ImageMetadata(metadata),
            $"/external-access/transfers/images/{input.ImageId:N}?projectId={input.ProjectId:N}&workspaceId={input.WorkspaceId:N}");
    }

    private static object ImageMetadata(ProjectImageView value) => new
    {
        value.Id, value.FileName, value.ContentType, value.AltText, value.Width, value.Height,
        value.SizeBytes, value.Source, value.DerivedFromImageId, value.UpdatedAt,
        metadataVersion = ImageMetadataVersion(value),
    };

    private static string ImageMetadataVersion(ProjectImageView value) => ContentVersion.For(new
    { value.Id, value.FileName, value.AltText, value.UpdatedAt });

    internal async Task<object> AttachImageAsync(ImageAssociationRequest input, CancellationToken ct) =>
        VisualMetadata(await Service<IEntityVisualExampleService>().AttachAsync(input.ProjectId, input.EntityId, input.ImageId, input.Label,
            EntityVisualExampleOrigin.Manual, cancellationToken: ct));

    internal async Task<object> ListEntityImagesAsync(EntityReadRequest input, CancellationToken ct) =>
        Page((await Service<IEntityVisualExampleService>().ListForEntityAsync(input.ProjectId, input.EntityId, ct)).Select(VisualMetadata), input, "list_entity_images");

    private static object VisualMetadata(EntityVisualExampleView value) => new
    { value.Id, value.EntityId, value.EntityType, value.EntityName, value.Label, value.SortOrder, value.Origin,
        image = ImageMetadata(value.Image) };

    internal async Task<object> CropImageAsync(ImageCropRequest input, CancellationToken ct) =>
        ImageMetadata(await Service<IProjectImageService>().CropAsync(input.ProjectId, input.ImageId, input.Crop, ct));

    internal async Task<object> GenerateImageAsync(ImageGenerateRequest input, CancellationToken ct)
    {
        var prepared = _preparedImage ?? throw new InvalidOperationException("The image request was not prepared.");
        var model = _preparedImageModel!;
        var job = input.SourceImageId is Guid source
            ? await Service<IProjectImageJobService>().CreateEditJobAsync(input.ProjectId,
                new(source, prepared.Prompt, prepared.Size, input.Quality, "png", null, "", 1, prepared.ReferenceImageIds,
                    Label: "External agent artwork edit", InheritSourceEntityTargets: false,
                    BriefJson: prepared.BriefJson, ReferenceManifestJson: prepared.ReferenceManifestJson,
                    TargetGeometryJson: prepared.TargetGeometryJson, ImageModel: model.ModelId,
                    Background: input.Background, ImageProviderId: model.ProviderId), ct)
            : await Service<IProjectImageJobService>().CreateGenerateJobAsync(input.ProjectId,
                new(prepared.Prompt, prepared.Size, input.Quality, "png", null, "", 1, prepared.ReferenceImageIds,
                    Label: "External agent artwork", BriefJson: prepared.BriefJson, ReferenceManifestJson: prepared.ReferenceManifestJson,
                    TargetGeometryJson: prepared.TargetGeometryJson, ImageModel: model.ModelId,
                    Background: input.Background, ImageProviderId: model.ProviderId), ct);
        if (!MutationCommitEffects.TryDefer(token => Service<IProjectImageGenerationRuntime>().EnqueueProjectAsync(input.ProjectId, token)))
            await Service<IProjectImageGenerationRuntime>().EnqueueProjectAsync(input.ProjectId, ct);
        return new { jobId = job.Id, kind = "image", job.Status, inspected = false, unattached = true };
    }

    internal async Task<object> ReadImageJobAsync(JobRequest input, CancellationToken ct)
    {
        var job = await Service<IProjectImageJobService>().GetJobAsync(input.ProjectId, input.JobId, ct)
            ?? throw new KeyNotFoundException("Image job not found.");
        return new { jobId = job.Id, job.Kind, job.Status, job.OutputImageIds,
            error = string.IsNullOrEmpty(job.Error) ? null : "Image generation needs attention. Inspect this job in Lorekeeper Images for its provider diagnostic.",
            outputs = job.OutputStates.Select(value => new { value.OutputIndex, value.Status, value.Attempt }),
            requiresInspection = job.OutputImageIds.Count > 0 };
    }

    internal async Task<object> CancelImageJobAsync(CancelJobRequest input, CancellationToken ct)
    {
        await Service<IProjectImageJobService>().CancelJobAsync(input.ProjectId, input.JobId, ct);
        MutationCommitEffects.TryDefer(token => Service<IProjectImageGenerationRuntime>().CancelJobAsync(input.ProjectId, input.JobId, token));
        return new { input.JobId, cancellationRequested = true };
    }

    internal async Task<object> ListImageJobsAsync(WorkspaceRequest input, CancellationToken ct) => Page(
        (await Service<IProjectImageJobService>().ListJobsAsync(input.ProjectId, 100, ct))
            .Select(value => new { jobId = value.Id, value.Kind, value.Status, value.OutputImageIds }), input, "list_image_jobs");

    internal async Task<object> ListSamplesAsync(WorkspaceRequest input, CancellationToken ct) => Page(
        (await Service<IWritingSampleService>().ListAsync(input.ProjectId, ct)).Select(value => new { value.Id, value.Title, value.Body, value.Revision }),
        input, "list_writing_samples");

    internal async Task<object> SaveSampleAsync(SampleWriteRequest input, CancellationToken ct)
    {
        var sample = input.SampleId is Guid id
            ? await Service<IWritingSampleService>().UpdateAsync(input.ProjectId, id, input.ExpectedRevision ?? throw new ArgumentException("expectedRevision is required."), input.Title, input.Body, ct)
            : await Service<IWritingSampleService>().CreateAsync(input.ProjectId, input.Title, input.Body, ct);
        return new { sample.Id, sample.Revision };
    }

    internal async Task<object> ReadVoiceAsync(EntityReadRequest input, CancellationToken ct)
    {
        var character = await Service<IEntityService>().GetAsync(input.ProjectId, input.EntityId, ct) ?? throw new KeyNotFoundException("Character not found.");
        if (character.Type != "Character") throw new ArgumentException("Voice profiles belong to Character entities.");
        return Page(new { character.Id, character.Name, content = VoiceProfileService.Read(character) }, input, "read_voice_profile");
    }

    internal async Task<object> SaveVoiceAsync(VoiceWriteRequest input, CancellationToken ct)
    {
        var value = await Service<VoiceProfileService>().SaveAsync(input.ProjectId, input.CharacterId, input.ExpectedContent, input.Content, ct);
        return new { value.Id, saved = true };
    }

    internal async Task<object> ListAnnotationsAsync(WorkspaceRequest input, CancellationToken ct) =>
        new { items = await Service<IManuscriptAnnotationService>().ListTargetAsync(input.ProjectId, Target,
            (Math.Max(1, input.PageNumber) - 1) * 50, 50, ct), pageNumber = Math.Max(1, input.PageNumber), pageSize = 50,
            continuation = new { tool = "list_review_annotations", arguments = input with { PageNumber = Math.Max(1, input.PageNumber) + 1 } } };

    internal async Task<object> SaveAnnotationAsync(AnnotationWriteRequest input, CancellationToken ct)
    {
        if (input.AnnotationId is Guid id)
            return await Service<IManuscriptAnnotationService>().UpdateNoteAsync(input.ProjectId, Target, id, input.Note, input.ExpectedRevision, ct);
        var chapter = Workspace!.ChapterId ?? throw new ArgumentException("Select an exact chapter before adding an annotation.");
        return await Service<IManuscriptAnnotationService>().CreateAsync(input.ProjectId, Target, chapter,
            ManuscriptAnnotationKind.Note, input.Note, input.Range ?? throw new ArgumentException("A manuscript range is required."), input.ExpectedRevision, ct);
    }

    internal async Task<object> ListPagesAsync(WorkspaceRequest input, CancellationToken ct) =>
        Page((await Service<IDesignedPageService>().ListAsync(input.ProjectId, Target, ct)).Select(value => new
        { value.Page.Id, value.Page.Name, contentId = value.Content.Id, value.Content.Revision, value.IsInherited }), input, "list_designed_pages");

    internal async Task<object> CreatePageAsync(CreatePageRequest input, CancellationToken ct)
    {
        var chapter = Workspace!.ChapterId ?? throw new ArgumentException("Select an exact chapter before placing a page.");
        var result = await Service<IDesignedPageService>().CreateDesignedPageAsync(Target, input.ProjectId, chapter,
            input.BlockIndex, input.Name, input.ExpectedRevision, cancellationToken: ct);
        return new { pageId = result.Page.Id, contentId = result.Content.Id, result.PlacementId, manuscriptRevision = result.Manuscript.Revision };
    }

    internal async Task<object> ReadPageAsync(PageReadRequest input, CancellationToken ct)
    {
        var page = await Service<IDesignedPageService>().GetAsync(input.ProjectId, input.PageId, Target, ct)
            ?? throw new KeyNotFoundException("Designed Page not found.");
        var variants = await Service<IDesignedPageService>().ListVariantsAsync(input.ProjectId, page.Content.Id, ct);
        return Page(new { page = new { page.Page.Id, page.Page.Name }, content = new { page.Content.Id, page.Content.Revision,
            manuscript = JsonSerializer.Deserialize<JsonElement>(page.Content.SemanticManuscriptJson) },
            variants = variants.Select(value => new { value.Id, value.Revision, scene = JsonSerializer.Deserialize<JsonElement>(value.SceneJson) }) }, input, "read_designed_page");
    }

    internal async Task<object> PatchPageAsync(PagePatchRequest input, CancellationToken ct)
    {
        var variant = await Service<IDesignedPageService>().PatchElementAsync(Target, input.ProjectId, input.VariantId,
            input.ExpectedRevision, input.TargetKind, input.TargetId, input.Patch, ct);
        return new { variant.Id, variant.Revision, requiresVisualInspection = true };
    }

    internal async Task<object> PreviewPageAsync(PagePreviewRequest input, CancellationToken ct)
    {
        var pages = await Service<IDesignedPageService>().ListAsync(input.ProjectId, Target, ct);
        if (!pages.Any(value => value.Content.Id == input.ContentId)) throw new KeyNotFoundException("Page content not found in this workspace target.");
        var preview = await Service<ICompositionCanvasPreviewService>().RenderAsync(input.ProjectId, input.ContentId,
            input.VariantId, input.Annotated ? CompositionCanvasPreviewMode.Annotated : CompositionCanvasPreviewMode.Clean, ct);
        return new ExternalImageResult(preview.Data, "image/png", new { preview.ContentId, preview.VariantId, preview.CompositionRevision,
            preview.VariantRevision, preview.Mode, preview.PixelWidth, preview.PixelHeight, preview.Diagnostics },
            $"/external-access/transfers/previews/page?projectId={input.ProjectId:N}&workspaceId={input.WorkspaceId:N}&contentId={input.ContentId:N}&variantId={input.VariantId:N}&annotated={input.Annotated}&sha256={Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(preview.Data))}");
    }

    internal async Task<object> ReadPublicationAsync(WorkspaceRequest input, CancellationToken ct)
    {
        var workspace = Workspace!.EditionId is Guid id ? await Service<IPublishService>().GetWorkspaceAsync(input.ProjectId, id, ct) : null;
        return Page(new
        {
        core = await Service<IPublicationBookService>().GetOrCreateAsync(input.ProjectId, ct),
        releases = await Service<IPublicationEditionService>().ListAsync(input.ProjectId, ct),
        selectedRelease = workspace is null ? null : new { workspace.Edition, workspace.OverrideFields, workspace.HasContentOverrides },
        }, input, "read_publication");
    }

    internal async Task<object> SaveBookAsync(BookWriteRequest input, CancellationToken ct)
    {
        if (!Target.IsCore) throw new ExternalAccessException("CORE_OWNER_REQUIRED", "Open Publish on Core before changing shared publication intent.");
        return await Service<IPublicationBookService>().UpdateAsync(input.ProjectId, input.Patch, ct);
    }

    internal async Task<object> CreateReleaseAsync(ReleaseCreateRequest input, CancellationToken ct) =>
        await Service<IPublicationEditionService>().CreateAsync(input.ProjectId, input.Release, ct);

    internal async Task<object> SaveReleaseAsync(ReleaseWriteRequest input, CancellationToken ct) =>
        await Service<IPublicationEditionService>().PatchOverridesAsync(input.ProjectId,
            Workspace!.EditionId ?? throw new ArgumentException("Open Publish on the exact release first."), input.Patch, ct);

    internal async Task<object> ListSectionsAsync(WorkspaceRequest input, CancellationToken ct) =>
        Page(await Service<IPublicationSectionService>().ListAsync(new(input.ProjectId, Workspace!.EditionId), ct), input, "list_publication_sections");

    internal async Task<object> SaveSectionAsync(SectionWriteRequest input, CancellationToken ct)
    {
        if (input.Section.Id is null && !string.IsNullOrWhiteSpace(input.Section.ManuscriptJson))
            throw new ArgumentException("Create an empty section first, then use semantic operations to author it.");
        var section = input.Section;
        if (section.Id is null)
            section = section with { ManuscriptJson = ManuscriptCodec.Serialize(ManuscriptCodec.CreateEmpty(Guid.NewGuid())) };
        if (section.Id is not null)
        {
            if (section.ExpectedRevision is null) throw new ArgumentException("expectedRevision is required when updating an existing section.");
            var current = await Service<IPublicationSectionService>().GetAsync(new(input.ProjectId, Workspace!.EditionId), section.Id.Value, ct);
            if (!string.IsNullOrWhiteSpace(input.Section.ManuscriptJson)
                && !ManuscriptCodec.Serialize(current.Manuscript).Equals(input.Section.ManuscriptJson, StringComparison.Ordinal))
                throw new ArgumentException("Use apply_section_operations to change existing prose.");
            section = section with { ManuscriptJson = ManuscriptCodec.Serialize(current.Manuscript) };
        }
        return await Service<IPublicationSectionService>().UpsertAsync(new(input.ProjectId, Workspace!.EditionId), section, ct);
    }

    internal async Task<object> ApplySectionAsync(SectionOperationsRequest input, CancellationToken ct)
    {
        var section = await Service<IPublicationSectionService>().GetAsync(new(input.ProjectId, Workspace!.EditionId), input.SectionId, ct);
        RequireOperationPermissions(section.Manuscript, input.Operations);
        return await Service<IPublicationSectionService>().PatchManuscriptAsync(new(input.ProjectId, Workspace!.EditionId), input.SectionId, input.ExpectedRevision, input.Operations, ct);
    }

    internal async Task<object> PreparePublicationAsync(PreparePublicationRequest input, CancellationToken ct)
    {
        var job = Workspace!.EditionId is Guid id
            ? await Service<IPublicationPreparationService>().PrepareReleaseAsync(input.ProjectId, id, ct)
            : await Service<IPublicationPreparationService>().PrepareCoreAsync(input.ProjectId, ct);
        return new { jobId = job.Id, kind = "publication", job.Status };
    }

    internal async Task<object> ListPublicationJobsAsync(WorkspaceRequest input, CancellationToken ct) =>
        Page(await Service<IPublicationPreparationService>().ListAsync(input.ProjectId,
            Target.IsCore ? PublicationTargetKind.CoreBook : PublicationTargetKind.Release, Workspace!.EditionId, ct), input, "list_publication_jobs");
}
