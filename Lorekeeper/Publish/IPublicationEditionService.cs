using Lorekeeper.Models;

namespace Lorekeeper.Publish;

public sealed record PublicationFingerprintSet(
    string Complete,
    string Interior,
    string Cover);

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
    Task<string> GetInteriorFingerprintAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken = default) =>
        GetSourceFingerprintAsync(projectId, editionId, cancellationToken);
    Task<string> GetCoverFingerprintAsync(Guid projectId, Guid editionId, int interiorPageCount, CancellationToken cancellationToken = default) =>
        GetSourceFingerprintAsync(projectId, editionId, cancellationToken);
    async Task<PublicationFingerprintSet> GetFingerprintsAsync(Guid projectId, Guid editionId, int interiorPageCount, CancellationToken cancellationToken = default) =>
        new(
            await GetSourceFingerprintAsync(projectId, editionId, cancellationToken),
            await GetInteriorFingerprintAsync(projectId, editionId, cancellationToken),
            await GetCoverFingerprintAsync(projectId, editionId, interiorPageCount, cancellationToken));
    Task<string> GetPaginationFingerprintAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken = default) =>
        GetSourceFingerprintAsync(projectId, editionId, cancellationToken);
}
