namespace Lorekeeper.Graph;

public interface IProjectGraphService
{
    Task<ProjectGraphSnapshot> GetSnapshotAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task CreateNodeAsync(Guid projectId, ProjectGraphNodeCreateRequest request, CancellationToken cancellationToken = default);
    Task CreateTypeAsync(Guid projectId, string labelOrType, CancellationToken cancellationToken = default);
    Task UpdateNodeAsync(Guid projectId, ProjectGraphNodeUpdateRequest request, CancellationToken cancellationToken = default);
    Task DeleteNodeAsync(Guid projectId, long nodeId, CancellationToken cancellationToken = default);
    Task MoveParentAsync(Guid projectId, ProjectGraphMoveParentRequest request, CancellationToken cancellationToken = default);
    Task CreateRelationshipAsync(Guid projectId, ProjectGraphRelationshipCreateRequest request, CancellationToken cancellationToken = default);
    Task UpdateRelationshipAsync(Guid projectId, ProjectGraphRelationshipUpdateRequest request, CancellationToken cancellationToken = default);
    Task DeleteRelationshipAsync(Guid projectId, long edgeId, CancellationToken cancellationToken = default);
}