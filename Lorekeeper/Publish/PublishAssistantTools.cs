using System.Text.Json;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Microsoft.Extensions.AI;

namespace Lorekeeper.Publish;

public sealed record PublishAssistantContext(Guid ProjectId);

public sealed class PublishAssistantTools(
    IPublicationEditionService editions,
    IPublishService publishing,
    IPublicationEditionMigrationService migrations)
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
                method: (Guid editionId, Guid? chapterId, long expectedRevision) =>
                    SetCoverAsync(context, editionId, chapterId, expectedRevision),
                name: "set_publication_cover_source",
                description: "Set or clear the edition cover Picture Page using a stable chapter ID and expected revision."),
            AIFunctionFactory.Create(
                method: (Guid editionId, PublicationEditionOutlineItemUpdate[] updates, long expectedRevision) =>
                    SetContentAsync(context, editionId, updates, expectedRevision),
                name: "set_publication_content",
                description: "Include or exclude acts and chapters for one edition through stable IDs and an expected revision."),
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
        ];
        return Task.FromResult(tools);
    }

    private async Task<string> ReadEditionsAsync(PublishAssistantContext context) =>
        Serialize(await editions.ListAsync(context.ProjectId));

    private async Task<string> ReadWorkspaceAsync(PublishAssistantContext context, Guid editionId) =>
        Serialize(await publishing.GetWorkspaceAsync(context.ProjectId, editionId));

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
        Guid? chapterId,
        long expectedRevision) =>
        Serialize(await editions.SetCoverChapterAsync(context.ProjectId, editionId, chapterId, expectedRevision));

    private async Task<string> SetContentAsync(
        PublishAssistantContext context,
        Guid editionId,
        PublicationEditionOutlineItemUpdate[] updates,
        long expectedRevision) =>
        Serialize(await editions.SetOutlineSelectionsAsync(context.ProjectId, editionId, updates, expectedRevision));

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

    private async Task<string> ReadMigrationAsync() =>
        Serialize(await migrations.GetHistoryAsync());

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
}
