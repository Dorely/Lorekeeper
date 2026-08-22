using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence;
using Lorekeeper.Context;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Projects;

public sealed class ProjectReferenceService(
    IAppDatabaseOperationFactory database,
    IContextIndexingService contextIndexing,
    ILogger<ProjectReferenceService> logger) : IProjectReferenceService
{
    public async Task<IReadOnlyList<ProjectReferenceSummary>> ListAsync(
        Guid referencingProjectId,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        _ = await operation.Repositories.Projects.GetSnapshotByIdAsync(referencingProjectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {referencingProjectId} not found.");
        var references = await operation.Repositories.ProjectReferences
            .ListByReferencingProjectAsync(referencingProjectId, cancellationToken);

        return references
            .Select(reference => ToSummary(reference))
            .ToList();
    }

    public async Task<IReadOnlyList<ProjectReferenceCandidate>> ListCandidatesAsync(
        Guid referencingProjectId,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        var source = await operation.Repositories.Projects.GetSnapshotByIdAsync(referencingProjectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {referencingProjectId} not found.");
        var projects = await operation.Repositories.Projects.ListAsync(cancellationToken);
        var references = await operation.Repositories.ProjectReferences
            .ListByReferencingProjectAsync(referencingProjectId, cancellationToken);
        var referencedPairs = references
            .Select(reference => (reference.ReferencedRepositoryId, reference.ReferencedProjectId))
            .ToHashSet();
        var projectIds = projects.Select(project => project.Id).ToArray();
        var repositoryIds = await operation.Db.ProjectVersionRepositories
            .AsNoTracking()
            .Where(repository => projectIds.Contains(repository.ProjectId))
            .ToDictionaryAsync(repository => repository.ProjectId, repository => repository.Id, cancellationToken);

        return projects
            .Where(project => project.Id != source.Id)
            .Select(project => new ProjectReferenceCandidate(
                project.Id,
                project.Name,
                project.Slug,
                project.UpdatedAt,
                repositoryIds.TryGetValue(project.Id, out var repositoryId)
                    && referencedPairs.Contains((repositoryId, project.Id)),
                repositoryIds.GetValueOrDefault(project.Id)))
            .ToList();
    }

    public async Task<ProjectReferenceSummary> AddAsync(
        Guid referencingProjectId,
        Guid referencedRepositoryId,
        Guid referencedProjectId,
        CancellationToken cancellationToken = default)
    {
        if (referencingProjectId == Guid.Empty)
            throw new ArgumentException("Referencing project id is required.", nameof(referencingProjectId));
        if (referencedProjectId == Guid.Empty)
            throw new ArgumentException("Referenced project id is required.", nameof(referencedProjectId));

        await using var operation = await database.OpenWriteAsync(referencingProjectId, cancellationToken);
        operation.ShareWithNestedOperations();
        var projects = operation.Repositories.Projects;
        var references = operation.Repositories.ProjectReferences;
        var source = await projects.GetByIdAsync(referencingProjectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {referencingProjectId} not found.");
        var target = await projects.GetSnapshotByIdAsync(referencedProjectId, cancellationToken)
            ?? throw new InvalidOperationException($"Referenced project {referencedProjectId} not found.");

        var targetRepository = await operation.Db.ProjectVersionRepositories
            .SingleOrDefaultAsync(repository => repository.ProjectId == target.Id, cancellationToken);
        if (targetRepository is null)
        {
            targetRepository = new ProjectVersionRepository
            {
                ProjectId = target.Id,
                CreativeRevision = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            operation.Db.ProjectVersionRepositories.Add(targetRepository);
        }

        if (referencedRepositoryId != Guid.Empty && referencedRepositoryId != targetRepository.Id)
        {
            throw new InvalidOperationException("The selected repository identity does not belong to the selected target project.");
        }

        if (target.Id == source.Id)
        {
            var sourceRepositoryId = await operation.Db.ProjectVersionRepositories
                .Where(repository => repository.ProjectId == source.Id)
                .Select(repository => repository.Id)
                .SingleOrDefaultAsync(cancellationToken);
            if (sourceRepositoryId == Guid.Empty)
                sourceRepositoryId = targetRepository.Id;
            if (sourceRepositoryId == targetRepository.Id)
                throw new InvalidOperationException("A project cannot reference itself by repository and project identity.");
        }

        if (await references.GetAsync(referencingProjectId, targetRepository.Id, referencedProjectId, cancellationToken) is not null)
            throw new InvalidOperationException($"Project {referencingProjectId} already references this target repository and project identity.");

        var reference = new ProjectReference
        {
            Id = Guid.NewGuid(),
            ReferencingProjectId = referencingProjectId,
            ReferencedRepositoryId = targetRepository.Id,
            ReferencedProjectId = referencedProjectId,
            ResolvedProjectId = target.Id,
            ReferencedProjectName = target.Name,
            ReferencedProjectSlug = target.Slug,
            ResolvedAt = DateTime.UtcNow,
        };
        await references.AddAsync(reference, cancellationToken);
        source.UpdatedAt = DateTime.UtcNow;
        projects.Update(source);
        await operation.SaveChangesAsync(cancellationToken);

        await BackfillReferencedIndexesAsync(target.Id, operation, cancellationToken);

        return ToSummary(reference, target);
    }

    public async Task RemoveAsync(
        Guid referenceId,
        CancellationToken cancellationToken = default)
    {
        Guid? referencingProjectId;
        await using (var read = await database.OpenReadAsync(cancellationToken))
        {
            referencingProjectId = await read.Db.ProjectReferences
                .AsNoTracking()
                .Where(reference => reference.Id == referenceId)
                .Select(reference => (Guid?)reference.ReferencingProjectId)
                .SingleOrDefaultAsync(cancellationToken);
        }
        if (referencingProjectId is not Guid sourceId)
            return;

        await using var operation = await database.OpenWriteAsync(sourceId, cancellationToken);
        operation.ShareWithNestedOperations();
        var references = operation.Repositories.ProjectReferences;
        var reference = await references.GetByIdAsync(referenceId, cancellationToken);
        if (reference is null)
            return;

        references.Remove(reference);
        var source = await operation.Repositories.Projects.GetByIdAsync(sourceId, cancellationToken);
        if (source is not null)
        {
            source.UpdatedAt = DateTime.UtcNow;
            operation.Repositories.Projects.Update(source);
        }

        await operation.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> RelinkAvailableAsync(
        Guid referenceId,
        CancellationToken cancellationToken = default)
    {
        Guid? referencingProjectId;
        await using (var read = await database.OpenReadAsync(cancellationToken))
        {
            referencingProjectId = await read.Db.ProjectReferences
                .AsNoTracking()
                .Where(reference => reference.Id == referenceId)
                .Select(reference => (Guid?)reference.ReferencingProjectId)
                .SingleOrDefaultAsync(cancellationToken);
        }
        if (referencingProjectId is not Guid sourceId)
            return false;

        await using var operation = await database.OpenWriteAsync(sourceId, cancellationToken);
        operation.ShareWithNestedOperations();
        var reference = await operation.Repositories.ProjectReferences
            .GetByIdAsync(referenceId, cancellationToken);
        if (reference is null)
            return false;

        var targetRepository = await operation.Db.ProjectVersionRepositories
            .Include(repository => repository.Project)
            .SingleOrDefaultAsync(repository => repository.Id == reference.ReferencedRepositoryId
                && repository.ProjectId == reference.ReferencedProjectId, cancellationToken);
        if (targetRepository?.Project is null)
            return false;
        if (targetRepository.ProjectId == reference.ReferencingProjectId)
        {
            var sourceRepositoryId = await operation.Db.ProjectVersionRepositories
                .Where(repository => repository.ProjectId == reference.ReferencingProjectId)
                .Select(repository => repository.Id)
                .SingleOrDefaultAsync(cancellationToken);
            if (sourceRepositoryId == Guid.Empty || sourceRepositoryId == targetRepository.Id)
                throw new InvalidOperationException("A project cannot reference itself by repository and project identity.");
        }

        reference.ResolvedProjectId = targetRepository.Project.Id;
        reference.ReferencedProjectName = targetRepository.Project.Name;
        reference.ReferencedProjectSlug = targetRepository.Project.Slug;
        reference.ResolvedAt = DateTime.UtcNow;
        await operation.SaveChangesAsync(cancellationToken);
        await BackfillReferencedIndexesAsync(targetRepository.Project.Id, operation, cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<ProjectReadableScope>> ListReadableScopesAsync(
        Guid referencingProjectId,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        var source = await operation.Repositories.Projects.GetSnapshotByIdAsync(referencingProjectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {referencingProjectId} not found.");
        var references = await operation.Repositories.ProjectReferences
            .ListByReferencingProjectAsync(referencingProjectId, cancellationToken);
        var repositoryId = await operation.Db.ProjectVersionRepositories
            .AsNoTracking()
            .Where(repository => repository.ProjectId == source.Id)
            .Select(repository => (Guid?)repository.Id)
            .SingleOrDefaultAsync(cancellationToken);
        var resolvedReferences = references
            .Where(reference => reference.ResolvedProjectId is not null && reference.ResolvedProject is not null)
            .ToList();

        return [
            new ProjectReadableScope(source.Id, source.Name, source.Slug, IsReferenced: false, repositoryId),
            .. resolvedReferences.Select(reference => new ProjectReadableScope(
                reference.ResolvedProject!.Id,
                reference.ResolvedProject.Name,
                reference.ResolvedProject.Slug,
                IsReferenced: true,
                reference.ReferencedRepositoryId)),
        ];
    }

    public async Task<IReadOnlyList<ProjectReferenceManifest>> ListReferenceManifestsAsync(
        Guid referencingProjectId,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        _ = await operation.Repositories.Projects.GetSnapshotByIdAsync(referencingProjectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {referencingProjectId} not found.");

        var references = await operation.Repositories.ProjectReferences
            .ListByReferencingProjectAsync(referencingProjectId, cancellationToken);
        references = references
            .Where(reference => reference.ResolvedProjectId is not null && reference.ResolvedProject is not null)
            .ToList();
        if (references.Count == 0)
            return [];

        var referencedIds = references
            .Select(reference => reference.ResolvedProjectId!.Value)
            .ToArray();
        var db = operation.Db;
        var excludedStructuralTypes = new[]
        {
            EntityTypeService.ProjectNodeType,
            EntityTypeService.ActNodeType,
            EntityTypeService.ChapterNodeType,
            EntityTypeService.ProjectFactNodeType,
            EntityTypeService.SourceNodeType,
            EntityTypeService.SourceChunkNodeType,
            EntityTypeService.SourceBlockNodeType,
        };

        var actCounts = await db.Acts
            .AsNoTracking()
            .Where(act => referencedIds.Contains(act.ProjectId))
            .GroupBy(act => act.ProjectId)
            .Select(group => new { ProjectId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.ProjectId, item => item.Count, cancellationToken);
        var chapterCounts = await db.Chapters
            .AsNoTracking()
            .Where(chapter => referencedIds.Contains(chapter.ProjectId))
            .GroupBy(chapter => chapter.ProjectId)
            .Select(group => new { ProjectId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.ProjectId, item => item.Count, cancellationToken);
        var searchableEntityCounts = await db.GraphNodes
            .AsNoTracking()
            .Where(node => referencedIds.Contains(node.ProjectId) && !excludedStructuralTypes.Contains(node.NodeType))
            .GroupBy(node => node.ProjectId)
            .Select(group => new { ProjectId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.ProjectId, item => item.Count, cancellationToken);
        var factCounts = await db.GraphNodes
            .AsNoTracking()
            .Where(node => referencedIds.Contains(node.ProjectId)
                && node.NodeType == EntityTypeService.ProjectFactNodeType)
            .GroupBy(node => node.ProjectId)
            .Select(group => new { ProjectId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.ProjectId, item => item.Count, cancellationToken);
        var writingSampleCounts = await db.WritingSamples
            .AsNoTracking()
            .Where(sample => referencedIds.Contains(sample.ProjectId))
            .GroupBy(sample => sample.ProjectId)
            .Select(group => new { ProjectId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.ProjectId, item => item.Count, cancellationToken);
        var canonicalSourceCounts = await db.BookBriefCanonSources
            .AsNoTracking()
            .Where(selection => referencedIds.Contains(selection.BookBrief.ProjectId))
            .GroupBy(selection => selection.BookBrief.ProjectId)
            .Select(group => new { ProjectId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.ProjectId, item => item.Count, cancellationToken);
        var canonicalVisualCounts = await db.EntityVisualExamples
            .AsNoTracking()
            .Where(example => referencedIds.Contains(example.ProjectId))
            .GroupBy(example => example.ProjectId)
            .Select(group => new { ProjectId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.ProjectId, item => item.Count, cancellationToken);
        var briefs = await db.BookBriefs
            .AsNoTracking()
            .Where(brief => referencedIds.Contains(brief.ProjectId))
            .ToDictionaryAsync(brief => brief.ProjectId, cancellationToken);

        return references
            .Select(reference =>
            {
                var project = reference.ResolvedProject!;
                briefs.TryGetValue(project.Id, out var brief);
                return new ProjectReferenceManifest(
                    reference.Id,
                    reference.ReferencedRepositoryId,
                    project.Id,
                    reference.ResolvedProjectId!.Value,
                    project.Name,
                    project.Slug,
                    project.UpdatedAt,
                    brief?.Premise ?? string.Empty,
                    brief?.Genre ?? string.Empty,
                    actCounts.GetValueOrDefault(project.Id),
                    chapterCounts.GetValueOrDefault(project.Id),
                    searchableEntityCounts.GetValueOrDefault(project.Id),
                    factCounts.GetValueOrDefault(project.Id),
                    writingSampleCounts.GetValueOrDefault(project.Id),
                    canonicalSourceCounts.GetValueOrDefault(project.Id),
                    canonicalVisualCounts.GetValueOrDefault(project.Id));
            })
            .ToList();
    }

    public async Task<IReadOnlyList<ProjectReferenceImpact>> ListIncomingAsync(
        Guid referencedProjectId,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        _ = await operation.Repositories.Projects.GetSnapshotByIdAsync(referencedProjectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {referencedProjectId} not found.");
        var references = await operation.Repositories.ProjectReferences
            .ListByReferencedProjectAsync(referencedProjectId, cancellationToken);

        return references
            .Select(reference => new ProjectReferenceImpact(
                reference.ReferencingProjectId,
                reference.ReferencingProject.Name,
                reference.ReferencingProject.Slug,
                reference.CreatedAt))
            .ToList();
    }

    private static ProjectReferenceSummary ToSummary(
        ProjectReference reference,
        Project? resolvedProject = null)
    {
        resolvedProject ??= reference.ResolvedProject;
        return new ProjectReferenceSummary(
            reference.Id,
            reference.ReferencedRepositoryId,
            reference.ReferencedProjectId,
            reference.ResolvedProjectId,
            resolvedProject?.Name ?? reference.ReferencedProjectName,
            resolvedProject?.Slug ?? reference.ReferencedProjectSlug,
            resolvedProject?.UpdatedAt,
            reference.CreatedAt,
            reference.ResolvedAt);
    }

    private async Task BackfillReferencedIndexesAsync(
        Guid referencedProjectId,
        AppDatabaseWriteOperation operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await contextIndexing.ReindexProjectProfileAsync(referencedProjectId, cancellationToken);
            var samples = await operation.Repositories.WritingSamples
                .ListByProjectAsync(referencedProjectId, cancellationToken);
            foreach (var sample in samples)
                await contextIndexing.ReindexWritingSampleAsync(sample.Id, cancellationToken);

            var facts = await operation.Repositories.GraphNodes.ListByTypeAsync(
                referencedProjectId,
                EntityTypeService.ProjectFactNodeType,
                cancellationToken);
            foreach (var fact in facts)
            {
                if (Guid.TryParseExact(fact.Key, "N", out var factId))
                    await contextIndexing.ReindexEntityAsync(referencedProjectId, factId, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Best-effort reference index backfill failed for referenced project {ProjectId}",
                referencedProjectId);
        }
    }
}
