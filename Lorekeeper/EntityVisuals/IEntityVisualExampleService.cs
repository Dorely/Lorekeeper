namespace Lorekeeper.EntityVisuals;

public interface IEntityVisualExampleService
{
    Task<IReadOnlyList<EntityVisualExampleView>> ListForEntityAsync(Guid projectId, Guid entityId, CancellationToken cancellationToken = default);
    Task<EntityVisualExampleView?> GetAsync(Guid projectId, Guid exampleId, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<EntityVisualExampleView>>> ListForEntitiesAsync(Guid projectId, IReadOnlyCollection<Guid> entityIds, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EntityVisualExampleView>> ListForImageAsync(Guid projectId, Guid imageId, CancellationToken cancellationToken = default);
    Task<EntityVisualExampleView> AttachAsync(Guid projectId, Guid entityId, Guid imageId, string? label = null, Models.EntityVisualExampleOrigin origin = Models.EntityVisualExampleOrigin.Manual, Guid? sourceVisualCandidateId = null, CancellationToken cancellationToken = default);
    Task<EntityVisualExampleView> UpdateAsync(Guid projectId, Guid exampleId, string label, int? sortOrder = null, Models.EntityVisualExampleOrigin? origin = null, CancellationToken cancellationToken = default);
    Task ReorderAsync(Guid projectId, Guid entityId, IReadOnlyList<Guid> orderedExampleIds, bool markManual = true, CancellationToken cancellationToken = default);
    Task DetachAsync(Guid projectId, Guid exampleId, CancellationToken cancellationToken = default);
    Task<SourceVisualCandidateView> CreateCandidateAsync(SourceVisualCandidateCreateRequest request, CancellationToken cancellationToken = default);
    Task<SourceVisualCandidateView?> GetCandidateAsync(Guid projectId, Guid candidateId, CancellationToken cancellationToken = default);
    Task<SourceVisualCandidateData?> GetCandidateDataAsync(Guid projectId, Guid candidateId, int? maxEdge = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SourceVisualCandidateView>> ListIngestCandidatesAsync(Guid projectId, Guid ingestSourceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SourceVisualCandidateView>> ListWebCandidatesAsync(Guid projectId, Guid webCandidateId, CancellationToken cancellationToken = default);
    Task<Images.ProjectImageView> PromoteCandidateAsync(Guid projectId, Guid candidateId, CancellationToken cancellationToken = default);
    Task<SourceVisualCandidateView> SetCandidateStatusAsync(Guid projectId, Guid candidateId, Models.SourceVisualCandidateStatus status, string? errorMessage = null, CancellationToken cancellationToken = default);
    Task RemoveIngestOwnedAsync(Guid projectId, Guid ingestSourceId, bool deleteCandidates = true, CancellationToken cancellationToken = default);
}
