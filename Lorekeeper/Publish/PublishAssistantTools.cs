using System.Text.Json;
using Lorekeeper.Images;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.Extensions.AI;

namespace Lorekeeper.Publish;

public sealed record PublishAssistantContext(Guid ProjectId);

public interface IPublishAssistantTools
{
    Task<IList<AITool>> BuildAsync(
        PublishAssistantContext context,
        CancellationToken cancellationToken = default);
}

public sealed class PublishAssistantTools(
    IPublicationEditionService editions,
    IPublishService publishing,
    IPublicationEditionMigrationService migrations,
    IPublicationRenderService renders,
    IPublicationCoverService covers,
    IPublicationPackageService packages,
    IManuscriptStyleService manuscriptStyles,
    IProjectImageService projectImages,
    IDatabaseMigrationRecoveryService recovery) : IPublishAssistantTools
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
                method: () => ReadEditionsAsync(context),
                name: "list_publication_editions",
                description: "List every publication edition with stable IDs, status, default state, and revision tokens."),
            AIFunctionFactory.Create(
                method: (Guid editionId) => ReadWorkspaceAsync(context, editionId),
                name: "read_publication_edition",
                description: "Read one complete edition workspace including settings, content, matter, style mappings, image placements, and source fingerprint."),
            AIFunctionFactory.Create(
                method: () => ListNamedStylesAsync(context),
                name: "list_publication_named_styles",
                description: "List every available project named style with the stable style ID, kind, semantic role, definition, and revision required for edition style mappings."),
            AIFunctionFactory.Create(
                method: () => ListProjectImagesAsync(context),
                name: "list_publication_project_images",
                description: "List every available project image with the stable asset ID, file metadata, alt text, source, and preview URL required for cover artwork, matter figures, and edition placements."),
            AIFunctionFactory.Create(
                method: (string name, PublicationEditionFormat format, PublicationVendor vendor) =>
                    CreateEditionAsync(context, name, format, vendor),
                name: "create_publication_edition",
                description: "Create a publication edition and return its first revision."),
            AIFunctionFactory.Create(
                method: (Guid editionId, string name, long expectedRevision) =>
                    CloneEditionAsync(context, editionId, name, expectedRevision),
                name: "clone_publication_edition",
                description: "Clone an edition at an expected revision. The clone receives a new identity and its ISBN is cleared."),
            AIFunctionFactory.Create(
                method: (Guid editionId, PublicationEditionUpdate update) =>
                    UpdateEditionAsync(context, editionId, update),
                name: "update_publication_edition",
                description: "Update all edition product, metadata, geometry, and typography settings through the same revision-aware service as the UI."),
            AIFunctionFactory.Create(
                method: (Guid editionId, long expectedRevision) =>
                    SetDefaultAsync(context, editionId, expectedRevision),
                name: "set_default_publication_edition",
                description: "Make an active edition the project default using its expected revision."),
            AIFunctionFactory.Create(
                method: (Guid editionId, long expectedRevision) =>
                    ArchiveAsync(context, editionId, expectedRevision),
                name: "archive_publication_edition",
                description: "Archive an edition using its expected revision."),
            AIFunctionFactory.Create(
                method: (Guid editionId, Guid? imageId, long expectedRevision) =>
                    SetCoverAsync(context, editionId, imageId, expectedRevision),
                name: "set_publication_cover_image",
                description: "Set or clear dedicated edition cover artwork using a stable project-image ID and expected revision. Discover image IDs with list_publication_project_images."),
            AIFunctionFactory.Create(
                method: (Guid editionId, PublicationEditionOutlineItemUpdate[] updates, long expectedRevision) =>
                    SetContentAsync(context, editionId, updates, expectedRevision),
                name: "set_publication_content",
                description: "Include or exclude acts and chapters for one edition through stable IDs and an expected revision."),
            AIFunctionFactory.Create(
                method: (Guid editionId, PublicationEditionOutlineItemOrder[] orderedItems, long expectedRevision) =>
                    ReorderContentAsync(context, editionId, orderedItems, expectedRevision),
                name: "reorder_publication_content",
                description: "Set the complete edition-specific reading order of acts and chapters using stable IDs and an expected revision."),
            AIFunctionFactory.Create(
                method: (Guid editionId, PublicationMatterInput input, long expectedRevision) =>
                    UpsertMatterAsync(context, editionId, input, expectedRevision),
                name: "upsert_publication_matter",
                description: "Create or update semantic front/back matter with its manuscript JSON and revision tokens."),
            AIFunctionFactory.Create(
                method: (Guid editionId, Guid matterId, long expectedRevision) =>
                    DeleteMatterAsync(context, editionId, matterId, expectedRevision),
                name: "delete_publication_matter",
                description: "Delete one matter item from an edition at an expected edition revision."),
            AIFunctionFactory.Create(
                method: (Guid editionId, PublicationEditionStyleMappingInput input, long expectedRevision) =>
                    UpsertStyleMappingAsync(context, editionId, input, expectedRevision),
                name: "upsert_publication_style_mapping",
                description: "Map a named manuscript style to edition-specific properties through the owning service."),
            AIFunctionFactory.Create(
                method: (Guid editionId, Guid mappingId, long expectedRevision) =>
                    DeleteStyleMappingAsync(context, editionId, mappingId, expectedRevision),
                name: "delete_publication_style_mapping",
                description: "Delete an edition style mapping at an expected revision."),
            AIFunctionFactory.Create(
                method: (Guid editionId, PublicationImagePlacementCreate input, long expectedRevision) =>
                    AddPlacementAsync(context, editionId, input, expectedRevision),
                name: "add_publication_image_placement",
                description: "Place a project image at an included edition target."),
            AIFunctionFactory.Create(
                method: (Guid editionId, Guid placementId, PublicationImagePlacementUpdate input, long expectedRevision) =>
                    UpdatePlacementAsync(context, editionId, placementId, input, expectedRevision),
                name: "update_publication_image_placement",
                description: "Update an edition image placement at an expected revision."),
            AIFunctionFactory.Create(
                method: (Guid editionId, Guid[] orderedPlacementIds, long expectedRevision) =>
                    ReorderPlacementsAsync(context, editionId, orderedPlacementIds, expectedRevision),
                name: "reorder_publication_image_placements",
                description: "Reorder all placements in one target/position group."),
            AIFunctionFactory.Create(
                method: (Guid editionId, Guid placementId, long expectedRevision) =>
                    DeletePlacementAsync(context, editionId, placementId, expectedRevision),
                name: "delete_publication_image_placement",
                description: "Delete an edition image placement at an expected revision."),
            AIFunctionFactory.Create(
                method: (Guid leftEditionId, Guid rightEditionId) =>
                    CompareAsync(context, leftEditionId, rightEditionId),
                name: "compare_publication_editions",
                description: "Compare two editions and return material product/content differences."),
            AIFunctionFactory.Create(
                method: (Guid editionId) => ReadAuditAsync(context, editionId),
                name: "read_publication_audit",
                description: "Read immutable edition mutation history with before/after fingerprints."),
            AIFunctionFactory.Create(
                method: () => ReadMigrationAsync(),
                name: "read_publication_migration_state",
                description: "Read the publication-edition migration journal and protected backup diagnostics. Restore remains user-confirmed."),
            AIFunctionFactory.Create(
                method: (PublicationVendor vendor) => ReadPressRuntimeReadiness(vendor),
                name: "read_publication_pdf_runtime",
                description: "Read Lorekeeper Press readiness, protocol version, renderer version, supported profiles, limits, and capabilities before proposing or requesting PDF generation."),
            AIFunctionFactory.Create(
                method: (Guid editionId) => RequestRenderAsync(context, editionId),
                name: "request_publication_render",
                description: "Queue deterministic interior and cover PDF rendering for a paperback edition."),
            AIFunctionFactory.Create(
                method: (Guid editionId) => ListRendersAsync(context, editionId),
                name: "list_publication_renders",
                description: "Inspect render status, progress, diagnostics, immutable artifacts, hashes, and stale state."),
            AIFunctionFactory.Create(
                method: (Guid editionId) => ListDownloadsAsync(context, editionId),
                name: "list_publication_downloads",
                description: "List generated publication artifacts with freshness, hashes, and safe in-app view/download URLs. Never claim a file was downloaded; the user must open a returned URL."),
            AIFunctionFactory.Create(
                method: (Guid editionId, Guid jobId) => CancelRenderAsync(context, editionId, jobId),
                name: "cancel_publication_render",
                description: "Cancel a queued or running press render."),
            AIFunctionFactory.Create(
                method: (Guid editionId, Guid jobId) => ReadPageMapAsync(context, editionId, jobId),
                name: "read_publication_page_map",
                description: "Read stable manuscript block IDs mapped to pages in one completed render."),
            AIFunctionFactory.Create(
                method: (Guid editionId, Guid leftJobId, Guid rightJobId) =>
                    CompareRendersAsync(context, editionId, leftJobId, rightJobId),
                name: "compare_publication_renders",
                description: "Explain page-count and block-pagination changes between two renders."),
            AIFunctionFactory.Create(
                method: (Guid editionId) => ReadCoverAsync(context, editionId),
                name: "read_publication_cover_design",
                description: "Read full-wrap cover copy, barcode behavior, focal controls, calculated vendor geometry, acknowledgement, and diagnostics."),
            AIFunctionFactory.Create(
                method: (Guid editionId, PublicationCoverDesignUpdate update) =>
                    UpdateCoverAsync(context, editionId, update),
                name: "update_publication_cover_design",
                description: "Update every editable cover property and optionally acknowledge the current calculated template."),
            AIFunctionFactory.Create(
                method: (Guid editionId) => PreflightAsync(context, editionId),
                name: "preflight_publication_edition",
                description: "Run the same versioned metadata, content, PDF, cover, barcode, staleness, and scope checks as the UI."),
            AIFunctionFactory.Create(
                method: (Guid editionId, PublishExportFormat format) =>
                    ExportAsync(context, editionId, format),
                name: "export_publication_edition",
                description: "Prepare a TXT, Markdown, or EPUB download and return safe metadata, the bound source fingerprint, and its in-app regeneration URL."),
            AIFunctionFactory.Create(
                method: (Guid editionId) => BuildPackageAsync(context, editionId),
                name: "build_publication_package",
                description: "Build the selected product-form package (EPUB or Lorekeeper-validated paperback PDFs), manifest, report, and cover image when preflight has no errors."),
        ];
        return Task.FromResult(tools);
    }

    private async Task<string> ReadEditionsAsync(PublishAssistantContext context) =>
        Serialize(await editions.ListAsync(context.ProjectId));

    private async Task<string> ReadWorkspaceAsync(PublishAssistantContext context, Guid editionId) =>
        Serialize(await publishing.GetWorkspaceAsync(context.ProjectId, editionId));

    private string ReadPressRuntimeReadiness(PublicationVendor vendor)
    {
        var readiness = renders.GetRuntimeReadiness(vendor);
        PublicationPressDescription? description = readiness.IsReady
            ? renders.GetRuntimeDescription()
            : null;
        return Serialize(new { readiness, description });
    }

    private async Task<string> ListNamedStylesAsync(PublishAssistantContext context) =>
        Serialize(await manuscriptStyles.ListAsync(context.ProjectId));

    private async Task<string> ListProjectImagesAsync(PublishAssistantContext context) =>
        Serialize(await projectImages.ListAsync(context.ProjectId));

    private async Task<string> CreateEditionAsync(
        PublishAssistantContext context,
        string name,
        PublicationEditionFormat format,
        PublicationVendor vendor) =>
        Serialize(await editions.CreateAsync(context.ProjectId, new(name, format, vendor)));

    private async Task<string> CloneEditionAsync(
        PublishAssistantContext context,
        Guid editionId,
        string name,
        long expectedRevision) =>
        Serialize(await editions.CloneAsync(context.ProjectId, editionId, name, expectedRevision));

    private async Task<string> UpdateEditionAsync(
        PublishAssistantContext context,
        Guid editionId,
        PublicationEditionUpdate update) =>
        Serialize(await editions.UpdateAsync(context.ProjectId, editionId, update));

    private async Task<string> SetDefaultAsync(PublishAssistantContext context, Guid editionId, long expectedRevision) =>
        Serialize(await editions.SetDefaultAsync(context.ProjectId, editionId, expectedRevision));

    private async Task<string> ArchiveAsync(PublishAssistantContext context, Guid editionId, long expectedRevision)
    {
        await editions.ArchiveAsync(context.ProjectId, editionId, expectedRevision);
        return """{"status":"archived"}""";
    }

    private async Task<string> SetCoverAsync(
        PublishAssistantContext context,
        Guid editionId,
        Guid? imageId,
        long expectedRevision) =>
        Serialize(await editions.SetCoverImageAsync(context.ProjectId, editionId, imageId, expectedRevision));

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

    private async Task<string> CompareAsync(PublishAssistantContext context, Guid left, Guid right) =>
        Serialize(await editions.CompareAsync(context.ProjectId, left, right));

    private async Task<string> ReadAuditAsync(PublishAssistantContext context, Guid editionId) =>
        Serialize(await editions.GetAuditAsync(context.ProjectId, editionId));

    private async Task<string> ReadMigrationAsync()
    {
        var recoveryState = await recovery.GetStateAsync();
        return Serialize(new
        {
            History = await migrations.GetHistoryAsync(),
            Recovery = new
            {
                recoveryState.RecoveryRequired,
                recoveryState.MigrationName,
                recoveryState.SourceVersion,
                recoveryState.TargetVersion,
                BackupFileName = recoveryState.BackupPath is null
                    ? null
                    : Path.GetFileName(recoveryState.BackupPath),
                recoveryState.Error,
                recoveryState.CreatedAtUtc,
            },
        });
    }

    private async Task<string> RequestRenderAsync(PublishAssistantContext context, Guid editionId) =>
        Serialize(await renders.RequestAsync(context.ProjectId, editionId));

    private async Task<string> ListRendersAsync(PublishAssistantContext context, Guid editionId) =>
        Serialize(await renders.ListAsync(context.ProjectId, editionId));

    private async Task<string> ListDownloadsAsync(PublishAssistantContext context, Guid editionId) =>
        Serialize((await renders.ListArtifactsAsync(context.ProjectId, editionId))
            .Where(artifact => artifact.Kind != PublicationArtifactKind.ProofRecord)
            .Select(artifact => DownloadView(context, artifact)));

    private async Task<string> CancelRenderAsync(PublishAssistantContext context, Guid editionId, Guid jobId) =>
        Serialize(await renders.CancelAsync(context.ProjectId, editionId, jobId));

    private async Task<string> ReadPageMapAsync(PublishAssistantContext context, Guid editionId, Guid jobId) =>
        Serialize(await renders.GetPageMapAsync(context.ProjectId, editionId, jobId));

    private async Task<string> CompareRendersAsync(
        PublishAssistantContext context,
        Guid editionId,
        Guid leftJobId,
        Guid rightJobId) =>
        Serialize(await renders.CompareAsync(context.ProjectId, editionId, leftJobId, rightJobId));

    private async Task<string> ReadCoverAsync(PublishAssistantContext context, Guid editionId) =>
        Serialize(await covers.GetAsync(context.ProjectId, editionId));

    private async Task<string> UpdateCoverAsync(
        PublishAssistantContext context,
        Guid editionId,
        PublicationCoverDesignUpdate update) =>
        Serialize(await covers.UpdateAsync(context.ProjectId, editionId, update));

    private async Task<string> PreflightAsync(PublishAssistantContext context, Guid editionId) =>
        Serialize(await packages.PreflightAsync(context.ProjectId, editionId));

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
            DownloadUrl = $"/projects/{context.ProjectId:N}/publish/editions/{editionId:N}/exports/{format}",
        });
    }

    private async Task<string> BuildPackageAsync(PublishAssistantContext context, Guid editionId)
    {
        var result = await packages.BuildAsync(context.ProjectId, editionId);
        return Serialize(new
        {
            result.Preflight,
            Artifacts = result.Artifacts.Select(artifact => DownloadView(context, artifact)),
        });
    }

    internal static object DownloadView(PublishAssistantContext context, PublicationArtifactView artifact) => new
    {
        artifact.Id,
        artifact.Kind,
        artifact.FileName,
        artifact.MediaType,
        artifact.Sha256,
        artifact.ByteLength,
        artifact.PageCount,
        artifact.SourceFingerprint,
        artifact.RendererVersion,
        artifact.ProfileId,
        artifact.CreatedAt,
        artifact.IsLegacy,
        artifact.IsStale,
        State = artifact.IsLegacy ? "Legacy" : artifact.IsStale ? "Stale" : "Current",
        ViewUrl = $"/projects/{context.ProjectId:N}/publish/artifacts/{artifact.Id:N}",
        DownloadUrl = $"/projects/{context.ProjectId:N}/publish/artifacts/{artifact.Id:N}/download",
    };

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
}
