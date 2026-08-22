using Lorekeeper.Models;

namespace Lorekeeper.Projects;

public interface IProjectReferenceService
{
    Task<IReadOnlyList<ProjectReferenceSummary>> ListAsync(
        Guid referencingProjectId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProjectReferenceCandidate>> ListCandidatesAsync(
        Guid referencingProjectId,
        CancellationToken cancellationToken = default);

    Task<ProjectReferenceSummary> AddAsync(
        Guid referencingProjectId,
        Guid referencedRepositoryId,
        Guid referencedProjectId,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(
        Guid referenceId,
        CancellationToken cancellationToken = default);

    Task<bool> RelinkAvailableAsync(
        Guid referenceId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProjectReadableScope>> ListReadableScopesAsync(
        Guid referencingProjectId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProjectReferenceManifest>> ListReferenceManifestsAsync(
        Guid referencingProjectId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProjectReferenceImpact>> ListIncomingAsync(
        Guid referencedProjectId,
        CancellationToken cancellationToken = default);
}

public sealed record ProjectReferenceSummary(
    Guid ReferenceId,
    Guid ReferencedRepositoryId,
    Guid ProjectId,
    Guid? ResolvedProjectId,
    string Name,
    string Slug,
    DateTime? ResolvedProjectUpdatedAt,
    DateTime CreatedAt,
    DateTime? ResolvedAt)
{
    public DateTime UpdatedAt => ResolvedProjectUpdatedAt ?? CreatedAt;
}

public sealed record ProjectReferenceCandidate(
    Guid ProjectId,
    string Name,
    string Slug,
    DateTime UpdatedAt,
    bool IsReferenced,
    Guid? ReferencedRepositoryId);

/// <summary>
/// A project scope readable from an active project. The active project is always
/// included; all other entries are direct references only.
/// </summary>
public sealed record ProjectReadableScope(
    Guid ProjectId,
    string Name,
    string Slug,
    bool IsReferenced,
    Guid? RepositoryId = null);

public sealed record ProjectReferenceManifest(
    Guid ReferenceId,
    Guid ReferencedRepositoryId,
    Guid ProjectId,
    Guid ResolvedProjectId,
    string Name,
    string Slug,
    DateTime UpdatedAt,
    string BookBriefPremise,
    string BookBriefGenre,
    int ActCount,
    int ChapterCount,
    int SearchableEntityCount,
    int FactCount,
    int WritingSampleCount,
    int CanonicalSourceCount,
    int CanonicalVisualCount);

public sealed record ProjectReferenceImpact(
    Guid ReferencingProjectId,
    string ReferencingProjectName,
    string ReferencingProjectSlug,
    DateTime ReferenceCreatedAt);
