using Lorekeeper.Models;

namespace Lorekeeper.Publish;

public interface IPublicationEditionService
{
    Task<IReadOnlyList<PublicationEditionSummary>> ListAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<PublicationEditionView> CreateAsync(Guid projectId, PublicationEditionCreate input, CancellationToken cancellationToken = default);
    Task<PublicationEditionView> CloneAsync(Guid projectId, Guid editionId, string name, long expectedRevision, CancellationToken cancellationToken = default);
    Task<PublicationEditionView> UpdateAsync(Guid projectId, Guid editionId, PublicationEditionUpdate input, CancellationToken cancellationToken = default);
    Task ArchiveAsync(Guid projectId, Guid editionId, long expectedRevision, CancellationToken cancellationToken = default);
    Task<PublicationEditionView> SetDefaultAsync(Guid projectId, Guid editionId, long expectedRevision, CancellationToken cancellationToken = default);
    Task<PublicationEditionView> SetCoverImageAsync(Guid projectId, Guid editionId, Guid? imageId, long expectedRevision, CancellationToken cancellationToken = default);
    Task<PublicationEditionView> SetOutlineSelectionsAsync(Guid projectId, Guid editionId, IReadOnlyList<PublicationEditionOutlineItemUpdate> updates, long expectedRevision, CancellationToken cancellationToken = default);
    Task<PublicationEditionView> ReorderOutlineAsync(Guid projectId, Guid editionId, IReadOnlyList<PublicationEditionOutlineItemOrder> orderedItems, long expectedRevision, CancellationToken cancellationToken = default);
    Task<PublicationMatterView> UpsertMatterAsync(Guid projectId, Guid editionId, PublicationMatterInput input, long expectedEditionRevision, CancellationToken cancellationToken = default);
    Task DeleteMatterAsync(Guid projectId, Guid editionId, Guid matterId, long expectedEditionRevision, CancellationToken cancellationToken = default);
    Task<PublicationEditionStyleMappingView> UpsertStyleMappingAsync(Guid projectId, Guid editionId, PublicationEditionStyleMappingInput input, long expectedEditionRevision, CancellationToken cancellationToken = default);
    Task DeleteStyleMappingAsync(Guid projectId, Guid editionId, Guid mappingId, long expectedEditionRevision, CancellationToken cancellationToken = default);
    Task<PublicationImagePlacementView> AddImagePlacementAsync(Guid projectId, Guid editionId, PublicationImagePlacementCreate input, long expectedEditionRevision, CancellationToken cancellationToken = default);
    Task<PublicationImagePlacementView> UpdateImagePlacementAsync(Guid projectId, Guid editionId, Guid placementId, PublicationImagePlacementUpdate input, long expectedEditionRevision, CancellationToken cancellationToken = default);
    Task ReorderImagePlacementsAsync(Guid projectId, Guid editionId, IReadOnlyList<Guid> orderedPlacementIds, long expectedEditionRevision, CancellationToken cancellationToken = default);
    Task DeleteImagePlacementAsync(Guid projectId, Guid editionId, Guid placementId, long expectedEditionRevision, CancellationToken cancellationToken = default);
    Task<PublicationEditionCompareView> CompareAsync(Guid projectId, Guid leftEditionId, Guid rightEditionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PublicationEditionAuditView>> GetAuditAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken = default);
    Task<string> GetSourceFingerprintAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken = default);
}
