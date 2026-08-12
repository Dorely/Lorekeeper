using Lorekeeper.Models;

namespace Lorekeeper.Publish;

public interface IPublicationEditionService
{
    Task<IReadOnlyList<PublicationEditionSummary>> ListAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<PublicationEditionView> CreateAsync(Guid projectId, PublicationEditionCreate input, CancellationToken cancellationToken = default);
    Task<PublicationEditionView> CloneAsync(Guid projectId, Guid editionId, string name, long expectedRevision, CancellationToken cancellationToken = default);
    Task<PublicationEditionView> PatchOverridesAsync(Guid projectId, Guid editionId, PublicationReleaseOverridePatch patch, CancellationToken cancellationToken = default);
    Task ArchiveAsync(Guid projectId, Guid editionId, long expectedRevision, CancellationToken cancellationToken = default);
    Task<PublicationEditionView> SetOutlineSelectionsAsync(Guid projectId, Guid editionId, IReadOnlyList<PublicationEditionOutlineItemUpdate> updates, long expectedRevision, CancellationToken cancellationToken = default);
    Task<PublicationEditionCompareView> CompareAsync(Guid projectId, Guid leftEditionId, Guid rightEditionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PublicationEditionAuditView>> GetAuditAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken = default);
    Task<string> GetSourceFingerprintAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken = default);
    Task<string> GetPaginationFingerprintAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken = default) =>
        GetSourceFingerprintAsync(projectId, editionId, cancellationToken);
}
