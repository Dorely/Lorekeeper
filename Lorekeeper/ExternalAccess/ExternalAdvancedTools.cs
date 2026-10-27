using Lorekeeper.Composition;
using Lorekeeper.Images;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Publish;

namespace Lorekeeper.ExternalAccess;

internal static class ExternalAdvancedTools
{
    internal static readonly string[] CreativeReads = ["direction.read", "outline.read", "manuscript.read", "review.read", "canon.read", "sources.read", "images.read", "composition.read", "voice.read", "publication.read"];

    internal static void Add(List<ExternalToolDefinition> tools)
    {
        Add<OutlineWriteRequest>("update_outline_item", "Update act/chapter metadata at its exact metadataVersion. Omitted act assignment preserves it; assignAct explicitly moves a chapter.", ["outline.read", "outline.write"], ["Outline"], true, (owner, input, ct) => owner.UpdateOutlineAsync(input, ct));
        Add<OutlineOrderRequest>("reorder_outline", "Reorder a complete act or chapter bucket using the exact orderVersion read from list_outline. Does not discard omitted authored items.", ["outline.read", "outline.write"], ["Outline"], true, (owner, input, ct) => owner.ReorderOutlineAsync(input, ct));
        Add<RelationshipWriteRequest>("update_relationship", "Update one manual relationship using the exact linkVersion read from read_entity. Managed evidence remains protected.", ["canon.read", "canon.write"], ["Outline", "World"], true, (owner, input, ct) => owner.UpdateRelationshipAsync(input, ct));
        Add<WebSearchToolRequest>("web_search", "Search with the user's configured search provider. Returns discovery candidates, not established canon. Read complete pages before using evidence.", ["research.web", "sources.read"], ["World"], false, (owner, input, ct) => owner.WebSearchAsync(input, ct));
        Add<WebReadToolRequest>("read_webpage", "Read a guarded HTTP(S) page into the project research cache. Private network destinations and unsafe redirects remain blocked.", ["research.web", "sources.read"], ["World"], false, (owner, input, ct) => owner.WebReadAsync(input, ct));
        Add<ResearchReadRequest>("read_research_source", "Read exact cached research text, provenance and links without refetching or promoting it to canon.", ["sources.read"], ["World"], false, (owner, input, ct) => owner.ReadResearchAsync(input, ct));
        Add<ResearchSaveRequest>("retain_research_source", "Retain a previously read page as searchable source evidence using the app's IndexOnly ingestion workflow.", ["sources.read", "sources.write"], ["World"], true, (owner, input, ct) => owner.RetainResearchAsync(input, ct));
        Add<FigureWriteRequest>("update_figure", "Update a Figure's image, accessibility and presentation at its exact chapter revision. Read back and inspect its rendered placement.", ["manuscript.read", "manuscript.write", "images.read", "composition.read", "composition.write"], ["Editor"], true, (owner, input, ct) => owner.SaveFigureAsync(input, ct));
        Add<PageSceneWriteRequest>("save_page_scene", "Save a complete inspected Designed Page variant at its exact revision; preserves semantic manuscript content. Inspect annotated and clean previews afterward.", ["composition.read", "composition.write", "images.read"], ["Editor", "Publish"], true, (owner, input, ct) => owner.SavePageSceneAsync(input, ct));
        Add<PageSemanticWriteRequest>("apply_page_operations", "Apply focused semantic operations to a Designed Page at its current content revision; preserve its canvas references and inspect the final page.", ["composition.read", "composition.write", "manuscript.read", "manuscript.write"], ["Editor", "Publish"], true, (owner, input, ct) => owner.ApplyPageAsync(input, ct));
        Add<ImageMetadataWriteRequest>("update_image_metadata", "Update filename and alternative text at the previously read image metadata version.", ["images.read", "images.write"], ["Images"], true, (owner, input, ct) => owner.SaveImageMetadataAsync(input, ct));
        Add<ImageResizeToolRequest>("resize_project_image", "Create a separate resized image within configured provider raster constraints, preserving the source.", ["images.read", "images.write"], ["Images"], true, (owner, input, ct) => owner.ResizeImageAsync(input, ct));
        Add<WorkspaceRequest>("inspect_pending_review", "Read pending creative edits against the approved baseline without approving, restoring or pushing anything.", CreativeReads, ["Editor", "Outline", "Publish"], false, (owner, input, ct) => owner.ReadReviewAsync(input, ct));
        Add<CoverReadRequest>("read_publication_cover", "Read the exact Core/release cover, its scene, revisions, inheritance and diagnostics.", ["publication.read", "composition.read", "images.read"], ["Publish"], false, (owner, input, ct) => owner.ReadCoverAsync(input, ct));
        Add<CoverWriteRequest>("save_publication_cover", "Save an exact Core/release cover scene and metadata at its current revisions. This does not establish production readiness; inspect both preview modes.", ["publication.read", "publication.write", "composition.read", "composition.write", "images.read"], ["Publish"], true, (owner, input, ct) => owner.SaveCoverAsync(input, ct));
        Add<CoverPreviewRequest>("preview_publication_cover", "Inspect the saved cover pixels with clean or annotated guides. Print geometry may still require preparation.", ["publication.read", "composition.read", "images.read"], ["Publish"], false, (owner, input, ct) => owner.PreviewCoverAsync(input, ct));
        Add<WorkspaceRequest>("read_publication_readiness", "Inspect installed renderer readiness and release preflight evidence. Report blocked requirements accurately; never infer vendor acceptance.", CreativeReads, ["Publish"], false, (owner, input, ct) => owner.ReadReadinessAsync(input, ct));
        Add<WorkspaceRequest>("read_publication_document", "Read the current Core/release publication projection and private export resources without approving, checkpointing or externally publishing anything.", CreativeReads, ["Publish"], false, (owner, input, ct) => owner.ReadPublicationDocumentAsync(input, ct));
        Add<EnableReleaseEditingRequest>("enable_release_editing", "Enable explicit manuscript overrides for the selected release at its current release revision. Core remains the shared source; this never discards divergent content.", ["publication.read", "publication.write", "manuscript.write"], ["Publish", "Editor"], true, (owner, input, ct) => owner.EnableReleaseEditingAsync(input, ct));
        Add<ChapterPreviewToolRequest>("preview_chapter_page", "Inspect actual saved chapter page pixels around a stable block or one chapter page number. Use after Figure or formatting changes and review returned diagnostics.", ["manuscript.read", "composition.read", "images.read"], ["Editor"], false, (owner, input, ct) => owner.PreviewChapterAsync(input, ct));
        Add<WorkspaceRequest>("read_chapter_layout", "Read the saved chapter's page map, layout geometry and diagnostics for the exact workspace target.", ["manuscript.read", "composition.read", "images.read"], ["Editor"], false, (owner, input, ct) => owner.ReadChapterLayoutAsync(input, ct));
        Add<WorkspaceRequest>("list_publication_artifacts", "Read actual output artifacts, hashes, source fingerprints, staleness and authenticated download resources.", ["publication.read", "manuscript.read", "images.read"], ["Publish"], false, (owner, input, ct) => owner.ListArtifactsAsync(input, ct));
        Add<ArtifactPreviewRequest>("preview_publication_artifact", "Inspect a page of an actual prepared PDF artifact. This is output evidence, distinct from a canvas preview.", ["publication.read", "manuscript.read", "images.read"], ["Publish"], false, (owner, input, ct) => owner.PreviewArtifactAsync(input, ct));
        Add<CancelJobRequest>("cancel_publication_job", "Cancel a preparation job on the exact selected Core/release target.", ["publication.read", "publication.prepare"], ["Publish"], true, (owner, input, ct) => owner.CancelPublicationAsync(input, ct));

        void Add<T>(string name, string description, string[] capabilities, string[] surfaces, bool mutates,
            Func<ExternalAccessCommands, T, CancellationToken, Task<object>> invoke) =>
            tools.Add(ExternalToolCatalog.Define(name, description, capabilities, surfaces, mutates, invoke));
    }
}

public sealed record OutlineWriteRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, string Kind, Guid ItemId,
    string ExpectedVersion, string? Title = null, string? Synopsis = null, bool AssignAct = false, Guid? ActId = null);
public sealed record OutlineOrderRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, string Kind, string ExpectedVersion, Guid[] OrderedIds, Guid? ActId = null);
public sealed record RelationshipWriteRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, Guid EntityId, long EdgeId,
    string ExpectedVersion, string EdgeType, Dictionary<string, string?>? Properties = null);
public sealed record WebSearchToolRequest(Guid ProjectId, Guid WorkspaceId, string Query, int Count = 5);
public sealed record WebReadToolRequest(Guid ProjectId, Guid WorkspaceId, string Url, int PageNumber = 1);
public sealed record ResearchReadRequest(Guid ProjectId, Guid WorkspaceId, Guid SourceId, int PageNumber = 1);
public sealed record ResearchSaveRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, Guid SourceId, string ExpectedContentHash, string? Rationale = null);
public sealed record FigureWriteRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, long ExpectedRevision,
    string BlockId, Guid ImageId, FigurePresentation Presentation, string? AltText = null, bool Decorative = false,
    string? Language = null, FigureAccessibilityRole AccessibilityRole = FigureAccessibilityRole.Figure);
public sealed record PageSceneWriteRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, Guid VariantId, long ExpectedRevision, CompositionScene Scene);
public sealed record PageSemanticWriteRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, Guid ContentId, long ExpectedRevision, ManuscriptOperationInput[] Operations);
public sealed record ImageMetadataWriteRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, Guid ImageId, string ExpectedVersion, ProjectImageUpdate Update);
public sealed record ImageResizeToolRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, Guid ImageId, ProjectImageResizeRequest Resize);
public sealed record CoverReadRequest(Guid ProjectId, Guid WorkspaceId, string SurfaceRole = "front", int PageNumber = 1);
public sealed record CoverWriteRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, PublicationCoverDesignUpdate Update,
    CompositionScene Scene, long? ExpectedBookRevision = null, string SurfaceRole = "front");
public sealed record CoverPreviewRequest(Guid ProjectId, Guid WorkspaceId, string SurfaceRole = "front", bool Annotated = true);
public sealed record ArtifactPreviewRequest(Guid ProjectId, Guid WorkspaceId, Guid ArtifactId, int PageNumber = 1, int WidthPixels = 1000);
public sealed record EnableReleaseEditingRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, long ExpectedEditionRevision);
public sealed record ChapterPreviewToolRequest(Guid ProjectId, Guid WorkspaceId, string? BlockId = null, int? ChapterPageNumber = null);
