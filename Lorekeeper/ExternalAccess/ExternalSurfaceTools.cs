using Lorekeeper.Composition;
using Lorekeeper.Images;
using Lorekeeper.Manuscripts;
using Lorekeeper.Publish;

namespace Lorekeeper.ExternalAccess;

internal static class ExternalSurfaceTools
{
    public static void Add(List<ExternalToolDefinition> tools)
    {
        Add<WorkspaceRequest>("list_project_images", "Read the reusable image library and exact image IDs. Asset creation and entity association are separate.", ["images.read"], ["Images", "Editor", "World", "Publish", "Outline"], false, (owner, input, ct) => owner.ListImagesAsync(input, ct));
        Add<ImageReadRequest>("read_project_image", "Inspect the actual image pixels and metadata. Use the transfer helper for original-byte download.", ["images.read"], ["Images", "Editor", "World", "Publish", "Outline"], false, (owner, input, ct) => owner.ReadImageAsync(input, ct));
        Add<ImageAssociationRequest>("attach_entity_image", "Attach an inspected project image as canonical visual evidence for an entity.", ["images.read", "images.write", "canon.read", "canon.write"], ["Images", "Outline", "World"], true, (owner, input, ct) => owner.AttachImageAsync(input, ct));
        Add<EntityReadRequest>("list_entity_images", "Read exact canonical entity-image association identities.", ["images.read", "canon.read"], ["Images", "Outline", "World", "Editor"], false, (owner, input, ct) => owner.ListEntityImagesAsync(input, ct));
        Add<ImageCropRequest>("crop_project_image", "Create a new unattached crop; preserve the original artwork.", ["images.read", "images.write"], ["Images"], true, (owner, input, ct) => owner.CropImageAsync(input, ct));
        Add<ImageGenerateRequest>("generate_project_image", "Queue unattached artwork using the user's configured default provider/model. Inspect its completed output before association or placement.", ["images.read", "images.generate"], ["Images", "Editor", "Publish"], true, (owner, input, ct) => owner.GenerateImageAsync(input, ct));
        Add<ImageGenerateRequest>("edit_project_image", "Queue an unattached source-driven edit using the default image provider. Set sourceImageId and editBrief; preserve unrelated scene content.", ["images.read", "images.generate"], ["Images", "Editor", "Publish"], true, (owner, input, ct) => owner.GenerateImageAsync(input, ct));
        Add<JobRequest>("read_image_job", "Read durable generation/edit status and output image IDs without retaining a provider stream.", ["images.read"], ["Images", "Editor", "Publish"], false, (owner, input, ct) => owner.ReadImageJobAsync(input, ct));
        Add<WorkspaceRequest>("list_image_jobs", "Recover recent durable image job identities and bounded status after interruption; use read_image_job for exact outputs.", ["images.read"], ["Images", "Editor", "Publish"], false, (owner, input, ct) => owner.ListImageJobsAsync(input, ct));
        Add<CancelJobRequest>("cancel_image_job", "Cancel a queued/running image job without discarding received partial output.", ["images.generate"], ["Images", "Editor", "Publish"], true, (owner, input, ct) => owner.CancelImageJobAsync(input, ct));
        Add<WorkspaceRequest>("list_writing_samples", "Read project-wide style samples and their revisions.", ["voice.read"], ["Voice", "Editor", "World"], false, (owner, input, ct) => owner.ListSamplesAsync(input, ct));
        Add<SampleWriteRequest>("save_writing_sample", "Create a requested project-wide style sample from the author's own writing or writing they supply to influence style, or revise one at its exact expectedRevision. Never use samples for lines taken from the story. Omitted fields remain unchanged.", ["voice.read", "voice.write"], ["Voice"], true, (owner, input, ct) => owner.SaveSampleAsync(input, ct));
        Add<EntityReadRequest>("read_voice_profile", "Read a Character's free-form voice profile by its entityId and the exact expectedContent for saving.", ["voice.read", "canon.read"], ["Voice"], false, (owner, input, ct) => owner.ReadVoiceAsync(input, ct));
        Add<VoiceWriteRequest>("save_voice_profile", "Save one Character's dialogue and POV voice using exact previously read content: described speech characteristics plus example speech and dialogue, quoting their existing lines where they exist. Keep appearance and backstory on the entity. Empty content clears it.", ["voice.read", "voice.write"], ["Voice"], true, (owner, input, ct) => owner.SaveVoiceAsync(input, ct));
        Add<WorkspaceRequest>("list_review_annotations", "Read annotations on the exact Core/release chapter target.", ["review.read", "manuscript.read"], ["Editor"], false, (owner, input, ct) => owner.ListAnnotationsAsync(input, ct));
        Add<AnnotationWriteRequest>("save_review_annotation", "Create a chapter note/highlight at the manuscript revision or update a note at its annotation revision.", ["review.write", "manuscript.read"], ["Editor"], true, (owner, input, ct) => owner.SaveAnnotationAsync(input, ct));
        Add<WorkspaceRequest>("list_designed_pages", "Read effective project Designed Pages for this Core/release target.", ["composition.read"], ["Editor", "Publish"], false, (owner, input, ct) => owner.ListPagesAsync(input, ct));
        Add<CreatePageRequest>("create_designed_page", "Create and insert an independent Designed Page into the selected chapter at its current revision.", ["composition.write", "manuscript.write", "manuscript.read"], ["Editor"], true, (owner, input, ct) => owner.CreatePageAsync(input, ct));
        Add<PageReadRequest>("read_designed_page", "Read effective semantic content and authored variants before focused page mutations.", ["composition.read", "manuscript.read", "images.read"], ["Editor", "Publish"], false, (owner, input, ct) => owner.ReadPageAsync(input, ct));
        Add<PagePatchRequest>("patch_page_object", "Patch one object on a previously read exact variant at expectedRevision. Inspect annotated and clean previews after changes.", ["composition.read", "composition.write"], ["Editor", "Publish"], true, (owner, input, ct) => owner.PatchPageAsync(input, ct));
        Add<PagePreviewRequest>("preview_designed_page", "Inspect exact saved page pixels with clean or annotated guides and diagnostics.", ["composition.read", "images.read"], ["Editor", "Publish"], false, (owner, input, ct) => owner.PreviewPageAsync(input, ct));
        Add<WorkspaceRequest>("read_publication", "Read Core Book, release identities and selected effective release settings. No artifact is claimed ready by this read.", ["publication.read"], ["Publish"], false, (owner, input, ct) => owner.ReadPublicationAsync(input, ct));
        Add<BookWriteRequest>("update_core_book", "Patch shared Core publication intent at its exact revision.", ["publication.read", "publication.write"], ["Publish"], true, (owner, input, ct) => owner.SaveBookAsync(input, ct));
        Add<ReleaseCreateRequest>("create_release", "Create an explicit paperback, hardcover, EPUB or PDF release using application-owned destination defaults.", ["publication.write"], ["Publish"], true, (owner, input, ct) => owner.CreateReleaseAsync(input, ct));
        Add<ReleaseWriteRequest>("update_release", "Patch the selected release's sparse overrides at expectedRevision.", ["publication.read", "publication.write"], ["Publish"], true, (owner, input, ct) => owner.SaveReleaseAsync(input, ct));
        Add<WorkspaceRequest>("list_publication_sections", "Read effective ordered Core/release sections and exact manuscript revisions.", ["publication.read", "manuscript.read"], ["Publish"], false, (owner, input, ct) => owner.ListSectionsAsync(input, ct));
        Add<SectionWriteRequest>("save_publication_section", "Create an empty section or update settings at ExpectedRevision. Use semantic section operations for existing prose.", ["publication.read", "publication.write"], ["Publish"], true, (owner, input, ct) => owner.SaveSectionAsync(input, ct));
        Add<SectionOperationsRequest>("apply_section_operations", "Apply revision-checked semantic prose operations to one publication section.", ["publication.write", "manuscript.read", "manuscript.write"], ["Publish"], true, (owner, input, ct) => owner.ApplySectionAsync(input, ct));
        Add<PreparePublicationRequest>("prepare_publication", "Queue private Core reading-copy or selected release preparation. Read readiness and final artifact evidence; preparation never publishes externally.", ["publication.read", "publication.prepare", "images.write"], ["Publish"], true, (owner, input, ct) => owner.PreparePublicationAsync(input, ct));
        Add<WorkspaceRequest>("list_publication_jobs", "Read preparation status and final evidence for this exact Core/release target.", ["publication.read"], ["Publish"], false, (owner, input, ct) => owner.ListPublicationJobsAsync(input, ct));

        void Add<T>(string name, string description, string[] capabilities, string[] surfaces, bool mutates,
            Func<ExternalAccessCommands, T, CancellationToken, Task<object>> invoke) =>
            tools.Add(ExternalToolCatalog.Define(name, description, capabilities, surfaces, mutates, invoke));
    }
}

internal sealed record ExternalImageResult(byte[] Data, string MimeType, object Metadata, string? DownloadResource = null);
public sealed record ImageReadRequest(Guid ProjectId, Guid WorkspaceId, Guid ImageId, bool FullResolution = false);
public sealed record ImageAssociationRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, Guid EntityId, Guid ImageId, string Label = "Canonical reference");
public sealed record ImageCropRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, Guid ImageId, ProjectImageCropRequest Crop);
public sealed record ImageGenerateRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, ImageGenerationBrief? Brief = null,
    Guid? SourceImageId = null, ImageEditBrief? EditBrief = null, ImageReferenceUse[]? References = null,
    ImageGenerationTarget? Target = null, string Quality = "auto", string Background = "auto");
public sealed record JobRequest(Guid ProjectId, Guid WorkspaceId, Guid JobId);
public sealed record CancelJobRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, Guid JobId);
public sealed record SampleWriteRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, Guid? SampleId = null, long? ExpectedRevision = null, string? Title = null, string? Body = null);
public sealed record VoiceWriteRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, Guid CharacterId, string ExpectedContent, string Content);
public sealed record AnnotationWriteRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, Guid? AnnotationId,
    string Note, long ExpectedRevision, ManuscriptAnnotationRange? Range = null);
public sealed record CreatePageRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, string Name, int BlockIndex, long ExpectedRevision);
public sealed record PageReadRequest(Guid ProjectId, Guid WorkspaceId, Guid PageId, int PageNumber = 1);
public sealed record PagePatchRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, Guid VariantId, long ExpectedRevision,
    string TargetKind, Guid TargetId, CompositionElementPatch Patch);
public sealed record PagePreviewRequest(Guid ProjectId, Guid WorkspaceId, Guid ContentId, Guid VariantId, bool Annotated = true);
public sealed record BookWriteRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, PublicationBookPatch Patch);
public sealed record ReleaseCreateRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, PublicationEditionCreate Release);
public sealed record ReleaseWriteRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, PublicationReleaseOverridePatch Patch);
public sealed record SectionWriteRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, PublicationSectionInput Section);
public sealed record SectionOperationsRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, Guid SectionId, long ExpectedRevision, ManuscriptOperationInput[] Operations);
public sealed record PreparePublicationRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId);
