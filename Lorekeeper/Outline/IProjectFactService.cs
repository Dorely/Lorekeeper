namespace Lorekeeper.Outline;

public interface IProjectFactService
{
    Task<IReadOnlyList<ProjectFact>> ListAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<ProjectFact?> GetByKeyAsync(Guid projectId, string key, CancellationToken cancellationToken = default);

    Task<ProjectFact> UpsertAsync(
        Guid projectId,
        string key,
        string? value,
        Guid? id = null,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid projectId, Guid factId, CancellationToken cancellationToken = default);
}

public sealed record ProjectFact(
    Guid Id,
    string Key,
    string Name,
    string Value,
    IReadOnlyList<ProjectFactLink> LinkedEntities);

public sealed record ProjectFactLink(
    string EdgeType,
    EntityLinkDirection Direction,
    Guid EntityId,
    string EntityName,
    string EntityType);