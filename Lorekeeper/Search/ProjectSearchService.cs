using System.Text;
using System.Text.Json;
using Lorekeeper.Chapters;
using Lorekeeper.Context;
using Lorekeeper.Graph;
using Lorekeeper.Ingest;
using Lorekeeper.Knowledge;
using Lorekeeper.Llm;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;
using Lorekeeper.Projects;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Search;

public sealed class ProjectSearchService(
IAppDatabaseOperationFactory database, IProjectSearchIndex index, IVectorStore vectors, IEmbeddingService embeddings, IChapterSemanticProjectionService semanticProjection, ILogger<ProjectSearchService> logger) : IProjectSearchService
{
    private const int RrfK = 60;
    private const int ReadPageMaxChars = 12_000;

    public async Task<ProjectSearchResponse> SearchAsync(
        ProjectSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.ProjectId == Guid.Empty)
            throw new ArgumentException("Project id is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Query))
            return new ProjectSearchResponse([], 0, true, Math.Clamp(request.TopK, 1, 50));

        var scopes = await ResolveScopesAsync(request.ProjectId, request.IncludeReferencedProjects, cancellationToken);
        if (scopes.Count == 0)
            return new ProjectSearchResponse([], 0, true, Math.Clamp(request.TopK, 1, 50));

        var topK = Math.Clamp(request.TopK, 1, 50);
        var sourceTypes = NormalizeSourceTypes(request.SourceTypes);
        var sourceIds = NormalizeSourceIds(request.SourceIds);
        if (request.ContainerSourceId is Guid containerId)
            sourceIds = await ExpandContainerSourceFilterAsync(sourceIds, sourceTypes, containerId, cancellationToken);

        var lexical = new List<ProjectLexicalSearchResult>();
        foreach (var scope in scopes)
        {
            lexical.AddRange(await SearchLexicalAsync(
                scope,
                request.Query.Trim(),
                sourceTypes,
                sourceIds,
                request.ContainerSourceId,
                Math.Min(100, topK * 6),
                cancellationToken));
        }
        if (request.LexicalOnly)
        {
            var exactMatches = lexical
                .Where(result => ContainsExactText(result.Content, request.Query) || ContainsExactText(result.Title, request.Query))
                .ToList();
            if (exactMatches.Count > 0)
                lexical = exactMatches;
        }

        lexical = lexical
            .OrderBy(result => result.Rank)
            .ThenBy(result => result.ScopeKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.RowId)
            .ToList();

        var merged = new Dictionary<string, MutableProjectSearchResult>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < lexical.Count; i++)
        {
            var item = lexical[i];
            var key = ResultKey(item.ScopeKey, item.SourceType, item.SourceId, item.ChunkIndex, item.Content);
            if (!merged.TryGetValue(key, out var existing))
            {
                existing = MutableProjectSearchResult.FromLexical(item, FindScope(scopes, item.ScopeKey));
                merged[key] = existing;
            }

            existing.LexicalRank = item.Rank;
            existing.LexicalPosition = i + 1;
            existing.Score += Rrf(i + 1);
            existing.Reasons.Add("keyword");
        }

        if (!request.LexicalOnly && await embeddings.IsAvailableAsync(cancellationToken))
        {
            try
            {
                var embedding = await embeddings.GenerateEmbeddingAsync(request.Query.Trim(), cancellationToken);
                var vectorResults = await SearchVectorsAsync(
                    scopes,
                    embedding,
                    sourceTypes,
                    sourceIds,
                    request.ContainerSourceId,
                    Math.Min(100, topK * 6),
                    cancellationToken);

                for (var i = 0; i < vectorResults.Count; i++)
                {
                    var item = vectorResults[i];
                    var key = ResultKey(item.ScopeKey, item.SourceType, item.SourceId, item.ChunkIndex, item.Content);
                    if (!merged.TryGetValue(key, out var existing))
                    {
                        existing = MutableProjectSearchResult.FromVector(item, FindScope(scopes, item.ScopeKey));
                        merged[key] = existing;
                    }

                    existing.VectorDistance = item.Distance;
                    existing.VectorPosition = i + 1;
                    existing.Score += Rrf(i + 1);
                    existing.Reasons.Add("semantic");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Hybrid semantic project search failed for project {ProjectId}", request.ProjectId);
            }
        }

        var ordered = merged.Values
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.LexicalPosition ?? int.MaxValue)
            .ThenBy(result => result.VectorDistance ?? double.MaxValue)
            .Select(result => result.ToResult())
            .ToList();
        return new ProjectSearchResponse(ordered.Take(topK).ToList(), ordered.Count, false, topK);
    }

    public async Task<ProjectSearchSourceResponse> ListSourcesAsync(
        Guid projectId,
        string? query = null,
        IReadOnlyCollection<string>? sourceTypes = null,
        int topK = 10,
        CancellationToken cancellationToken = default,
        bool includeReferencedProjects = false)
    {
        var scopes = await ResolveScopesAsync(projectId, includeReferencedProjects, cancellationToken);
        var types = NormalizeSourceTypes(sourceTypes);
        var limit = Math.Clamp(topK, 1, 50);
        var results = new List<ProjectSearchSource>();
        foreach (var scope in scopes)
        {
            var scopedResults = await ListSourcesForProjectAsync(scope, query, types, cancellationToken);
            results.AddRange(scopedResults.Select(source => source with
            {
                OriginProjectId = scope.ProjectId,
                OriginProjectName = scope.Name,
                OriginProjectSlug = scope.Slug,
                IsReferenced = scope.IsReferenced,
            }));
        }

        var ordered = results
            .OrderBy(source => SourceTypeSort(source.SourceType))
            .ThenBy(source => source.OriginProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ProjectSearchSourceResponse(ordered.Take(limit).ToList(), ordered.Count, true, limit);
    }

    private async Task<IReadOnlyList<ProjectSearchSource>> ListSourcesForProjectAsync(
        SearchScope scope,
        string? query,
        IReadOnlyCollection<string>? types,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var projectId = scope.ProjectId;
        var chapters = databaseOperation.Repositories.Chapters;
        var acts = databaseOperation.Repositories.Acts;
        var nodes = databaseOperation.Repositories.GraphNodes;
        var ingest = databaseOperation.Repositories.Ingest;
        var results = new List<ProjectSearchSource>();

        bool Include(string type) => types is null || types.Contains(type);
        bool Matches(params string?[] values) =>
            string.IsNullOrWhiteSpace(query)
            || values.Any(value => value?.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) ?? false);

        if (Include(ProjectSearchSourceTypes.Chapter) || Include(ProjectSearchSourceTypes.ContextChapter))
        {
            var projectChapters = await chapters.ListByProjectAsync(projectId, cancellationToken);
            var expanded = await semanticProjection.ExpandPlainTextAsync(projectChapters, cancellationToken);
            foreach (var chapter in projectChapters)
            {
                var plainText = expanded.GetValueOrDefault(chapter.Id, chapter.PlainText);
                if (!Matches(chapter.Title, chapter.Synopsis, plainText)) continue;
                results.Add(new ProjectSearchSource(
                    ProjectSearchSourceTypes.Chapter,
                    chapter.Id,
                    null,
                    chapter.Title,
                    $"Chapter {chapter.Order + 1}",
                    Preview(!string.IsNullOrWhiteSpace(chapter.Synopsis) ? chapter.Synopsis : plainText)));
            }
        }

        if (Include(ProjectSearchSourceTypes.Act))
        {
            foreach (var act in await acts.ListByProjectAsync(projectId, cancellationToken))
            {
                if (!Matches(act.Title, act.Synopsis)) continue;
                results.Add(new ProjectSearchSource(
                    ProjectSearchSourceTypes.Act,
                    act.Id,
                    null,
                    act.Title,
                    $"Act {act.Order + 1}",
                    Preview(act.Synopsis)));
            }
        }

        if (Include(ProjectSearchSourceTypes.Entity))
        {
            foreach (var node in await nodes.ListByProjectAsync(projectId, cancellationToken))
            {
                if (!Guid.TryParseExact(node.Key, "N", out var entityId) || !IsSearchEntityType(node.NodeType))
                    continue;
                var text = await BuildEntityTextAsync(
                    node,
                    databaseOperation.Db,
                    databaseOperation.Repositories.GraphEdges,
                    nodes,
                    cancellationToken);
                if (!Matches(node.Label, node.NodeType, text)) continue;
                results.Add(new ProjectSearchSource(
                    ProjectSearchSourceTypes.Entity,
                    entityId,
                    null,
                    node.Label ?? node.Key,
                    node.NodeType,
                    Preview(text)));
            }
        }

        if (Include(ProjectSearchSourceTypes.IngestSource) || Include(ProjectSearchSourceTypes.RawIngestSource))
        {
            foreach (var source in await ingest.ListSourcesByProjectAsync(projectId, cancellationToken))
            {
                if (scope.IsReferenced && !scope.CanonicalIngestSourceIds.Contains(source.Id.ToString("N")))
                    continue;
                if (!Matches(source.Title, source.SourceKind, source.Description, source.Synopsis, source.SourceText)) continue;
                results.Add(new ProjectSearchSource(
                    ProjectSearchSourceTypes.RawIngestSource,
                    source.Id,
                    source.Id,
                    source.Title,
                    string.IsNullOrWhiteSpace(source.SourceKind) ? "Ingest source" : source.SourceKind,
                    Preview(!string.IsNullOrWhiteSpace(source.Synopsis) ? source.Synopsis : source.SourceText)));
            }
        }

        if (Include(ProjectSearchSourceTypes.IngestSourceChunk))
        {
            foreach (var source in await ingest.ListSourcesByProjectAsync(projectId, cancellationToken))
            {
                if (scope.IsReferenced && !scope.CanonicalIngestSourceIds.Contains(source.Id.ToString("N")))
                    continue;
                foreach (var chunk in await ingest.ListSourceChunksAsync(source.Id, cancellationToken))
                {
                    if (scope.IsReferenced
                        && !scope.CanonicalIngestSourceIds.Contains(source.Id.ToString("N"))
                        && !scope.CanonicalIngestSourceIds.Contains(chunk.Id.ToString("N")))
                        continue;
                    var excerpt = await ingest.GetSourceChunkExcerptAsync(chunk.Id, 2_000, cancellationToken);
                    if (!Matches(source.Title, chunk.Title, chunk.HeadingPath, chunk.Summary, chunk.AgentNotes, excerpt?.Text)) continue;
                    results.Add(new ProjectSearchSource(
                        ProjectSearchSourceTypes.IngestSourceChunk,
                        chunk.Id,
                        source.Id,
                        $"{source.Title} Part {chunk.Index + 1}",
                        chunk.Title,
                        Preview(!string.IsNullOrWhiteSpace(chunk.Summary) ? chunk.Summary : excerpt?.Text)));
                }
            }
        }

        if (Include(ProjectSearchSourceTypes.ProjectProfile))
        {
            var project = await databaseOperation.Repositories.Projects.GetSnapshotByIdAsync(projectId, cancellationToken);
            var brief = await databaseOperation.Db.BookBriefs
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken);
            var profile = project is null ? null : ProjectProfileFormatter.Build(project, brief);
            if (project is not null && Matches(project.Name, project.Slug, profile))
            {
                results.Add(new ProjectSearchSource(
                    ProjectSearchSourceTypes.ProjectProfile,
                    project.Id,
                    null,
                    project.Name,
                    "Project profile",
                    Preview(profile)));
            }
        }

        if (Include(ProjectSearchSourceTypes.WritingSample))
        {
            foreach (var sample in await databaseOperation.Repositories.WritingSamples.ListByProjectAsync(projectId, cancellationToken))
            {
                if (!Matches(sample.Title, sample.Body)) continue;
                results.Add(new ProjectSearchSource(
                    ProjectSearchSourceTypes.WritingSample,
                    sample.Id,
                    null,
                    sample.Title,
                    "Writing sample",
                    Preview(sample.Body)));
            }
        }

        if (Include(ProjectSearchSourceTypes.DesignedPage) && !scope.IsReferenced)
        {
            var pages = await databaseOperation.Db.DesignedPageContents
                .AsNoTracking()
                .Include(content => content.Page)
                .Where(content => content.ProjectId == projectId)
                .OrderBy(content => content.Page.Name)
                .ThenBy(content => content.EditionId)
                .ToListAsync(cancellationToken);
            foreach (var content in pages)
            {
                var text = DesignedPageText(content);
                if (!Matches(content.Page.Name, TargetLabel(content), text)) continue;
                results.Add(new ProjectSearchSource(
                    ProjectSearchSourceTypes.DesignedPage,
                    content.Id,
                    content.DesignedPageId,
                    content.Page.Name,
                    TargetLabel(content),
                    Preview(text)));
            }
        }

        return results;
    }

    public async Task<ProjectSourceReadResult?> ReadSourceAsync(
        Guid projectId,
        string sourceType,
        Guid sourceId,
        int? pageNumber = null,
        CancellationToken cancellationToken = default,
        Guid? originProjectId = null)
    {
        var scopes = await ResolveScopesAsync(projectId, includeReferencedProjects: true, cancellationToken);
        var scope = scopes.FirstOrDefault(candidate => candidate.ProjectId == (originProjectId ?? projectId));
        if (scope is null)
            return null;

        var normalizedType = ProjectSearchSourceTypes.Normalize(sourceType);
        if (scope.IsReferenced)
        {
            if (!ProjectSearchSourceTypes.DirectReferenceNarrativeTypes.Contains(normalizedType))
                return null;
            if (IsIngestSourceType(normalizedType)
                && !await IsCanonicalIngestSourceAsync(scope, normalizedType, sourceId, cancellationToken))
                return null;
        }

        var (title, containerSourceId, content) = normalizedType switch
        {
            ProjectSearchSourceTypes.Chapter or ProjectSearchSourceTypes.ContextChapter
                => await ReadChapterAsync(scope.ProjectId, sourceId, cancellationToken),
            ProjectSearchSourceTypes.Act
                => await ReadActAsync(scope.ProjectId, sourceId, cancellationToken),
            ProjectSearchSourceTypes.Entity
                => await ReadEntityAsync(scope.ProjectId, sourceId, cancellationToken),
            ProjectSearchSourceTypes.IngestSource or ProjectSearchSourceTypes.RawIngestSource
                => await ReadIngestSourceAsync(scope.ProjectId, sourceId, cancellationToken),
            ProjectSearchSourceTypes.IngestSourceChunk
                => await ReadIngestSourceChunkAsync(scope.ProjectId, sourceId, cancellationToken),
            ProjectSearchSourceTypes.ProjectProfile
                => sourceId == scope.ProjectId
                    ? await ReadProjectProfileAsync(scope.ProjectId, cancellationToken)
                    : (null, null, null),
            ProjectSearchSourceTypes.WritingSample
                => await ReadWritingSampleAsync(scope.ProjectId, sourceId, cancellationToken),
            ProjectSearchSourceTypes.DesignedPage
                => await ReadDesignedPageAsync(scope.ProjectId, sourceId, cancellationToken),
            _ => (null, null, null),
        };

        if (title is null || content is null) return null;

        var page = Math.Max(1, pageNumber ?? 1);
        var pageCount = Math.Max(1, (int)Math.Ceiling(content.Length / (double)ReadPageMaxChars));
        page = Math.Min(page, pageCount);
        var start = Math.Min(content.Length, (page - 1) * ReadPageMaxChars);
        var length = Math.Min(ReadPageMaxChars, content.Length - start);
        var pageText = length <= 0 ? string.Empty : content.Substring(start, length);

        return new ProjectSourceReadResult(
            normalizedType,
            sourceId,
            containerSourceId,
            title,
            page,
            pageCount,
            page > 1,
            page < pageCount,
            pageText,
            scope.ProjectId,
            scope.Name,
            scope.Slug,
            scope.IsReferenced);
    }

    private async Task<List<KnowledgeResult>> SearchVectorsAsync(
        IReadOnlyList<SearchScope> scopes,
        float[] embedding,
        IReadOnlyCollection<string>? sourceTypes,
        IReadOnlyCollection<string>? sourceIds,
        Guid? containerSourceId,
        int fetchLimit,
        CancellationToken cancellationToken)
    {
        var scopeKeys = scopes.Select(scope => Project.ScopeKey(scope.ProjectId)).ToList();
        List<KnowledgeResult> results;
        if (sourceTypes is { Count: > 0 })
        {
            results = [];
            foreach (var type in sourceTypes)
                results.AddRange(await vectors.SearchMultiScopeAsync(embedding, scopeKeys, fetchLimit, type, cancellationToken));
        }
        else
        {
            results = await vectors.SearchMultiScopeAsync(embedding, scopeKeys, fetchLimit, cancellationToken: cancellationToken);
        }

        var sourceIdSet = sourceIds?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (sourceIdSet is not null)
            results = results.Where(result => result.SourceId is not null && sourceIdSet.Contains(result.SourceId)).ToList();

        results = results
            .Where(result =>
            {
                var scope = FindScope(scopes, result.ScopeKey);
                return scope is not null
                    && (!scope.IsReferenced
                    || (ProjectSearchSourceTypes.DirectReferenceNarrativeTypes.Contains(
                            ProjectSearchSourceTypes.Normalize(result.SourceType))
                        && (!IsIngestSourceType(result.SourceType)
                            || (result.SourceId is not null && scope.CanonicalIngestSourceIds.Contains(result.SourceId)))));
            })
            .ToList();

        if (containerSourceId is Guid container)
        {
            var containerKey = container.ToString("N");
            IReadOnlyList<IngestSourceChunk> sourceChunks;
            await using (var readOperation = await database.OpenReadAsync(cancellationToken))
            {
                sourceChunks = await readOperation.Repositories.Ingest.ListSourceChunksAsync(container, cancellationToken);
            }
            var chunkIds = sourceChunks
                .Select(chunk => chunk.Id.ToString("N"))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            results = results
                .Where(result => string.Equals(result.SourceId, containerKey, StringComparison.OrdinalIgnoreCase)
                    || (result.SourceId is not null && chunkIds.Contains(result.SourceId)))
                .ToList();
        }

        return results
            .OrderBy(result => result.Distance)
            .Take(fetchLimit)
            .ToList();
    }

    private async Task<IReadOnlyCollection<string>?> ExpandContainerSourceFilterAsync(
        IReadOnlyCollection<string>? sourceIds,
        IReadOnlyCollection<string>? sourceTypes,
        Guid containerSourceId,
        CancellationToken cancellationToken)
    {
        if (sourceIds is { Count: > 0 })
            return sourceIds;

        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { containerSourceId.ToString("N") };
        var includeChunks = sourceTypes is null
            || sourceTypes.Contains(ProjectSearchSourceTypes.IngestSourceChunk)
            || sourceTypes.Contains(ProjectSearchSourceTypes.RawIngestSource)
            || sourceTypes.Contains(ProjectSearchSourceTypes.IngestSource);
        if (includeChunks)
        {
            IReadOnlyList<IngestSourceChunk> chunks;
            await using (var readOperation = await database.OpenReadAsync(cancellationToken))
            {
                chunks = await readOperation.Repositories.Ingest.ListSourceChunksAsync(containerSourceId, cancellationToken);
            }
            foreach (var chunk in chunks)
                expanded.Add(chunk.Id.ToString("N"));
        }

        return expanded;
    }

    private async Task<List<ProjectLexicalSearchResult>> SearchLexicalAsync(
        SearchScope scope,
        string query,
        IReadOnlyCollection<string>? sourceTypes,
        IReadOnlyCollection<string>? sourceIds,
        Guid? containerSourceId,
        int topK,
        CancellationToken cancellationToken)
    {
        var scopeKey = Project.ScopeKey(scope.ProjectId);
        if (!scope.IsReferenced)
        {
            var localResults = (await index.SearchAsync(
                new ProjectLexicalSearchRequest(
                    scopeKey,
                    query,
                    topK,
                    sourceTypes,
                    sourceIds,
                    containerSourceId?.ToString("N")),
                cancellationToken)).ToList();
            if (sourceTypes is null || sourceTypes.Contains(ProjectSearchSourceTypes.DesignedPage))
            {
                localResults.AddRange(await SearchDesignedPagesLexicallyAsync(
                    scope.ProjectId, scopeKey, query, sourceIds, cancellationToken));
            }
            return localResults;
        }

        var requestedTypes = (sourceTypes?.ToList() ?? ProjectSearchSourceTypes.All.ToList())
            .Where(ProjectSearchSourceTypes.DirectReferenceNarrativeTypes.Contains)
            .ToList();
        var ingestTypes = requestedTypes.Where(IsIngestSourceType).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var nonIngestTypes = requestedTypes.Where(type => !IsIngestSourceType(type)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var results = new List<ProjectLexicalSearchResult>();

        if (nonIngestTypes.Count > 0)
        {
            results.AddRange(await index.SearchAsync(
                new ProjectLexicalSearchRequest(
                    scopeKey,
                    query,
                    topK,
                    nonIngestTypes,
                    sourceIds,
                    containerSourceId?.ToString("N")),
                cancellationToken));
        }

        if (ingestTypes.Count > 0 && scope.CanonicalIngestSourceIds.Count > 0)
        {
            var canonicalIds = sourceIds is null
                ? scope.CanonicalIngestSourceIds.ToList()
                : sourceIds.Intersect(scope.CanonicalIngestSourceIds, StringComparer.OrdinalIgnoreCase).ToList();
            if (canonicalIds.Count > 0)
            {
                results.AddRange(await index.SearchAsync(
                    new ProjectLexicalSearchRequest(
                        scopeKey,
                        query,
                        topK,
                        ingestTypes,
                        canonicalIds,
                        containerSourceId?.ToString("N")),
                    cancellationToken));
            }
        }

        return results;
    }

    private async Task<IReadOnlyList<SearchScope>> ResolveScopesAsync(
        Guid projectId,
        bool includeReferencedProjects,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ProjectReadableScope> readableScopes;
        if (includeReferencedProjects)
        {
            await using var operation = await database.OpenReadAsync(cancellationToken);
            var project = await operation.Repositories.Projects.GetSnapshotByIdAsync(projectId, cancellationToken)
                ?? throw new InvalidOperationException($"Project {projectId} not found.");
            var references = await operation.Repositories.ProjectReferences
                .ListByReferencingProjectAsync(projectId, cancellationToken);
            references = references
                .Where(reference => reference.ResolvedProjectId is not null && reference.ResolvedProject is not null)
                .ToList();
            readableScopes =
            [
                new ProjectReadableScope(project.Id, project.Name, project.Slug, IsReferenced: false),
                .. references.Select(reference => new ProjectReadableScope(
                    reference.ResolvedProject!.Id,
                    reference.ResolvedProject.Name,
                    reference.ResolvedProject.Slug,
                    IsReferenced: true,
                    reference.ReferencedRepositoryId)),
            ];
        }
        else
        {
            await using var operation = await database.OpenReadAsync(cancellationToken);
            var project = await operation.Repositories.Projects.GetSnapshotByIdAsync(projectId, cancellationToken)
                ?? throw new InvalidOperationException($"Project {projectId} not found.");
            readableScopes = [new ProjectReadableScope(project.Id, project.Name, project.Slug, IsReferenced: false)];
        }

        var referencedIds = readableScopes
            .Where(scope => scope.IsReferenced)
            .Select(scope => scope.ProjectId)
            .ToList();
        var canonicalByProject = referencedIds.ToDictionary(id => id, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        if (referencedIds.Count > 0)
        {
            await using var operation = await database.OpenReadAsync(cancellationToken);
            var selected = await operation.Db.BookBriefCanonSources
                .AsNoTracking()
                .Where(selection => referencedIds.Contains(selection.BookBrief.ProjectId))
                .Select(selection => new { ProjectId = selection.BookBrief.ProjectId, selection.IngestSourceId })
                .ToListAsync(cancellationToken);
            foreach (var item in selected)
                canonicalByProject[item.ProjectId].Add(item.IngestSourceId.ToString("N"));

            var sourceIds = selected.Select(item => item.IngestSourceId).ToList();
            if (sourceIds.Count > 0)
            {
                var chunks = await operation.Db.IngestSourceChunks
                    .AsNoTracking()
                    .Where(chunk => sourceIds.Contains(chunk.SourceId))
                    .Select(chunk => new { chunk.Source.ProjectId, chunk.Id })
                    .ToListAsync(cancellationToken);
                foreach (var chunk in chunks)
                    canonicalByProject[chunk.ProjectId].Add(chunk.Id.ToString("N"));
            }
        }

        return readableScopes
            .Select(scope => new SearchScope(
                scope.ProjectId,
                scope.Name,
                scope.Slug,
                scope.IsReferenced,
                canonicalByProject.GetValueOrDefault(scope.ProjectId) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
            .ToList();
    }

    private static SearchScope? FindScope(IReadOnlyList<SearchScope> scopes, string scopeKey) =>
        scopes.FirstOrDefault(scope => string.Equals(Project.ScopeKey(scope.ProjectId), scopeKey, StringComparison.OrdinalIgnoreCase));

    private async Task<bool> IsCanonicalIngestSourceAsync(
        SearchScope scope,
        string sourceType,
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        if (!scope.IsReferenced) return true;
        if (sourceType is ProjectSearchSourceTypes.IngestSource or ProjectSearchSourceTypes.RawIngestSource)
            return scope.CanonicalIngestSourceIds.Contains(sourceId.ToString("N"));

        await using var operation = await database.OpenReadAsync(cancellationToken);
        var chunk = await operation.Repositories.Ingest.GetSourceChunkAsync(sourceId, cancellationToken);
        return chunk is not null
            && scope.CanonicalIngestSourceIds.Contains(chunk.SourceId.ToString("N"));
    }

    private async Task<(string? Title, Guid? ContainerSourceId, string? Content)> ReadProjectProfileAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        var project = await operation.Repositories.Projects.GetSnapshotByIdAsync(projectId, cancellationToken);
        if (project is null) return (null, null, null);
        var brief = await operation.Db.BookBriefs
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken);
        var content = ProjectProfileFormatter.Build(project, brief);
        return (project.Name, null, content);
    }

    private async Task<(string? Title, Guid? ContainerSourceId, string? Content)> ReadWritingSampleAsync(
        Guid projectId,
        Guid sampleId,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        var sample = await operation.Repositories.WritingSamples.GetByIdAsync(sampleId, cancellationToken);
        if (sample is null || sample.ProjectId != projectId) return (null, null, null);
        return (sample.Title, null, $"# {sample.Title}\n\n{(string.IsNullOrWhiteSpace(sample.Body) ? "(empty)" : sample.Body.Trim())}");
    }

    private async Task<(string? Title, Guid? ContainerSourceId, string? Content)> ReadDesignedPageAsync(
        Guid projectId,
        Guid contentId,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        var content = await operation.Db.DesignedPageContents
            .AsNoTracking()
            .Include(item => item.Page)
            .SingleOrDefaultAsync(item => item.Id == contentId && item.ProjectId == projectId, cancellationToken);
        if (content is null)
            return (null, null, null);

        var text = DesignedPageText(content);
        var body = new StringBuilder();
        body.Append("# ").AppendLine(content.Page.Name);
        body.Append("Target: ").AppendLine(TargetLabel(content));
        body.Append("Designed Page ID: ").AppendLine(content.DesignedPageId.ToString("D"));
        body.Append("Content ID: ").AppendLine(content.Id.ToString("D"));
        body.Append("Revision: ").AppendLine(content.Revision.ToString());
        AppendOptional(body, "Accessibility description", content.AccessibilityDescription);
        AppendOptional(body, "Semantic content", text);
        return (content.Page.Name, content.DesignedPageId, body.ToString().TrimEnd());
    }

    private async Task<IReadOnlyList<ProjectLexicalSearchResult>> SearchDesignedPagesLexicallyAsync(
        Guid projectId,
        string scopeKey,
        string query,
        IReadOnlyCollection<string>? sourceIds,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        var contents = await operation.Db.DesignedPageContents
            .AsNoTracking()
            .Include(content => content.Page)
            .Where(content => content.ProjectId == projectId)
            .OrderBy(content => content.Page.Name)
            .ThenBy(content => content.EditionId)
            .ToListAsync(cancellationToken);
        var matches = new List<ProjectLexicalSearchResult>();
        foreach (var content in contents)
        {
            if (sourceIds is not null && !sourceIds.Contains(content.Id.ToString("N"), StringComparer.OrdinalIgnoreCase))
                continue;
            var text = DesignedPageText(content);
            var searchable = $"{content.Page.Name}\n{TargetLabel(content)}\n{text}";
            if (!searchable.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;
            matches.Add(new ProjectLexicalSearchResult(
                RowId: matches.Count + 1,
                SourceType: ProjectSearchSourceTypes.DesignedPage,
                SourceId: content.Id.ToString("N"),
                ContainerSourceId: content.DesignedPageId.ToString("N"),
                Title: content.Page.Name,
                Content: text,
                Snippet: Preview(text),
                Metadata: $"{TargetLabel(content)}; pageId={content.DesignedPageId:N}; contentId={content.Id:N}",
                ChunkIndex: null,
                Rank: matches.Count,
                ScopeKey: scopeKey));
        }
        return matches;
    }

    private static string TargetLabel(DesignedPageContent content) => content.EditionId is Guid editionId
        ? $"Release content ({editionId:D})"
        : "Core content";

    private static string DesignedPageText(DesignedPageContent content)
    {
        try
        {
            return ManuscriptCodec.ProjectPlainText(
                content.SemanticManuscriptJson,
                content.Id,
                content.Revision);
        }
        catch (InvalidDataException)
        {
            return string.Empty;
        }
    }

    private static bool IsIngestSourceType(string sourceType) =>
        sourceType is ProjectSearchSourceTypes.IngestSource
            or ProjectSearchSourceTypes.RawIngestSource
            or ProjectSearchSourceTypes.IngestSourceChunk;

    private async Task<(string? Title, Guid? ContainerSourceId, string? Content)> ReadChapterAsync(
        Guid projectId,
        Guid chapterId,
        CancellationToken cancellationToken)
    {
        Chapter? chapter;
        await using (var readOperation = await database.OpenReadAsync(cancellationToken))
        {
            chapter = await readOperation.Repositories.Chapters.GetByIdAsync(chapterId, cancellationToken);
        }
        if (chapter is null || chapter.ProjectId != projectId) return (null, null, null);
        var plainText = await semanticProjection.ExpandPlainTextAsync(chapter, cancellationToken);
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(chapter.Title);
        AppendOptional(sb, "Synopsis", chapter.Synopsis);
        AppendOptional(sb, "Body", ChapterFormatting.WithLineNumbers(plainText));
        return (chapter.Title, null, sb.ToString().TrimEnd());
    }

    private async Task<(string? Title, Guid? ContainerSourceId, string? Content)> ReadActAsync(
        Guid projectId,
        Guid actId,
        CancellationToken cancellationToken)
    {
        Act? act;
        IReadOnlyList<Chapter> projectChapters;
        await using (var readOperation = await database.OpenReadAsync(cancellationToken))
        {
            act = await readOperation.Repositories.Acts.GetByIdAsync(actId, cancellationToken);
            projectChapters = await readOperation.Repositories.Chapters.ListByProjectAsync(projectId, cancellationToken);
        }
        if (act is null || act.ProjectId != projectId) return (null, null, null);
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(act.Title);
        AppendOptional(sb, "Synopsis", act.Synopsis);
        var actChapters = projectChapters
            .Where(chapter => chapter.ActId == act.Id)
            .OrderBy(chapter => chapter.Order);
        foreach (var chapter in actChapters)
            sb.Append("- Chapter ").Append(chapter.Order + 1).Append(": ").Append(chapter.Title).Append(" - ").AppendLine(chapter.Synopsis);
        return (act.Title, null, sb.ToString().TrimEnd());
    }

    private async Task<(string? Title, Guid? ContainerSourceId, string? Content)> ReadEntityAsync(
        Guid projectId,
        Guid entityId,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var nodes = databaseOperation.Repositories.GraphNodes;
        var node = await nodes.FindByKeyAsync(projectId, entityId.ToString("N"), cancellationToken);
        if (node is null) return (null, null, null);
        var title = $"{node.NodeType}: {node.Label ?? node.Key}";
        return (
            title,
            null,
            await BuildEntityTextAsync(
                node,
                databaseOperation.Db,
                databaseOperation.Repositories.GraphEdges,
                nodes,
                cancellationToken));
    }

    private async Task<(string? Title, Guid? ContainerSourceId, string? Content)> ReadIngestSourceAsync(
        Guid projectId,
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var ingest = databaseOperation.Repositories.Ingest;
        var source = await ingest.GetSourceAsync(sourceId, cancellationToken);
        if (source is null || source.ProjectId != projectId) return (null, null, null);
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(source.Title);
        AppendOptional(sb, "Kind", source.SourceKind);
        AppendOptional(sb, "Description", source.Description);
        AppendOptional(sb, "Synopsis", source.Synopsis);
        AppendOptional(sb, "Source text", source.SourceText);
        return (source.Title, source.Id, sb.ToString().TrimEnd());
    }

    private async Task<(string? Title, Guid? ContainerSourceId, string? Content)> ReadIngestSourceChunkAsync(
        Guid projectId,
        Guid sourceChunkId,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var ingest = databaseOperation.Repositories.Ingest;
        var chunk = await ingest.GetSourceChunkAsync(sourceChunkId, cancellationToken);
        if (chunk is null) return (null, null, null);
        var source = await ingest.GetSourceAsync(chunk.SourceId, cancellationToken);
        if (source is null || source.ProjectId != projectId) return (null, null, null);
        var excerpt = await ingest.GetSourceChunkExcerptAsync(chunk.Id, int.MaxValue, cancellationToken);
        var title = $"{source.Title} Part {chunk.Index + 1}";
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(title);
        AppendOptional(sb, "Chunk title", chunk.Title);
        AppendOptional(sb, "Heading", chunk.HeadingPath);
        AppendOptional(sb, "Summary", chunk.Summary);
        AppendOptional(sb, "Notes", chunk.AgentNotes);
        AppendOptional(sb, "Source text", excerpt?.Text);
        return (title, source.Id, sb.ToString().TrimEnd());
    }

    private static IReadOnlyCollection<string>? NormalizeSourceTypes(IReadOnlyCollection<string>? values)
    {
        var normalized = values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(ProjectSearchSourceTypes.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return normalized is { Count: > 0 } ? normalized : null;
    }

    private static IReadOnlyCollection<string>? NormalizeSourceIds(IReadOnlyCollection<Guid>? values)
    {
        var normalized = values?
            .Where(value => value != Guid.Empty)
            .Select(value => value.ToString("N"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return normalized is { Count: > 0 } ? normalized : null;
    }

    private static string ResultKey(string scopeKey, string sourceType, string? sourceId, int? chunkIndex, string content) =>
        $"{scopeKey}|{sourceType}|{sourceId}|{chunkIndex?.ToString() ?? "?"}|{StableContentKey(content)}";

    private static string StableContentKey(string content)
    {
        var trimmed = content.Trim();
        return trimmed.Length <= 64 ? trimmed : trimmed[..64];
    }

    private static double Rrf(int position) => 1d / (RrfK + Math.Max(1, position));

    private static int SourceTypeSort(string sourceType) => sourceType switch
    {
        ProjectSearchSourceTypes.Chapter => 0,
        ProjectSearchSourceTypes.Act => 1,
        ProjectSearchSourceTypes.Entity => 2,
        ProjectSearchSourceTypes.IngestSource => 3,
        ProjectSearchSourceTypes.RawIngestSource => 4,
        ProjectSearchSourceTypes.IngestSourceChunk => 5,
        ProjectSearchSourceTypes.ProjectProfile => 6,
        ProjectSearchSourceTypes.WritingSample => 7,
        ProjectSearchSourceTypes.DesignedPage => 8,
        _ => 20,
    };

    private static async Task<string> BuildEntityTextAsync(
        GraphNode node,
        AppDbContext db,
        IGraphEdgeRepository edges,
        IGraphNodeRepository nodes,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.Append("Type: ").AppendLine(node.NodeType);
        sb.Append("Name: ").AppendLine(node.Label ?? node.Key);
        AppendOptional(sb, "Summary", IngestWikiSheet.ReadSummary(node.Properties));

        foreach (var property in node.Properties
            .Where(property => !IngestSourceAssertions.IsProtectedProperty(property.Key)
                && !IngestWikiSheet.IsWikiStorageProperty(property.Key)
                && !IngestWikiSheet.IsSourceEvidenceProperty(property.Key)
                && !string.IsNullOrWhiteSpace(property.Value?.ToString()))
            .OrderBy(property => property.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(property.Key).Append(": ").AppendLine(property.Value?.ToString());
        }

        var aliases = IngestWikiSheet.ReadAliases(node.Properties);
        if (aliases.Count > 0)
            sb.Append("Aliases: ").AppendLine(string.Join(", ", aliases));

        foreach (var section in IngestWikiSheet.ReadSections(node.Properties))
            sb.Append(section.Title).Append(": ").AppendLine(section.Body);

        foreach (var source in IngestWikiSheet.ReadSourceEvidence(node.Properties))
        {
            sb.Append("Source evidence: ").Append(source.SourceTitle).Append(" (").Append(source.SourceKind).AppendLine(")");
            sb.AppendLine(source.Markdown);
        }

        var visualExamples = await db.EntityVisualExamples
            .AsNoTracking()
            .Include(example => example.Image)
            .Where(example => example.GraphNodeId == node.Id)
            .OrderBy(example => example.SortOrder)
            .ToListAsync(cancellationToken);
        if (visualExamples.Count > 0)
        {
            sb.AppendLine("Canonical visual references:");
            foreach (var example in visualExamples)
            {
                sb.Append("- ").Append(example.Label)
                    .Append(" [imageId: ").Append(example.ImageId.ToString("N")).AppendLine("]");
                AppendOptional(sb, "  Alt text", example.Image.AltText);
                AppendOptional(sb, "  Prompt", example.Image.Prompt);
            }
        }

        var adjacent = (await edges.GetAdjacentAsync(
                node.Id,
                EdgeDirection.Both,
                edgeTypes: null,
                maxResults: 30,
                cancellationToken))
            .Where(edge => !GraphAutoLinkService.IsAutoMentionEdge(edge))
            .ToList();
        if (adjacent.Count > 0)
        {
            var otherIds = adjacent
                .Select(edge => edge.FromNodeId == node.Id ? edge.ToNodeId : edge.FromNodeId)
                .Distinct();
            var otherNodes = (await nodes.GetByIdsAsync(otherIds, cancellationToken)).ToDictionary(other => other.Id);
            sb.AppendLine("Relationships:");
            foreach (var edge in adjacent)
            {
                var otherNodeId = edge.FromNodeId == node.Id ? edge.ToNodeId : edge.FromNodeId;
                if (!otherNodes.TryGetValue(otherNodeId, out var other)) continue;
                var direction = edge.FromNodeId == node.Id ? "->" : "<-";
                sb.Append("- ").Append(direction).Append(' ').Append(edge.EdgeType).Append(' ')
                    .Append(other.Label ?? other.Key).Append(" (").Append(other.NodeType).AppendLine(")");
                AppendOptional(sb, "  Summary", ReadProperty(edge.Properties, IngestWikiSheet.SummaryProperty));
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string Preview(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var preview = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return preview.Length <= 180 ? preview : preview[..180] + "...";
    }

    private static void AppendOptional(StringBuilder sb, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        sb.Append(label).Append(": ").AppendLine(value.Trim());
    }

    private static string ReadProperty(IReadOnlyDictionary<string, object?> properties, string key) =>
        properties.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;

    private static bool ContainsExactText(string value, string query)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(query)) return false;

        var needle = query.Trim();
        var start = 0;
        while (start < value.Length)
        {
            var index = value.IndexOf(needle, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;
            var before = index == 0 ? '\0' : value[index - 1];
            var afterIndex = index + needle.Length;
            var after = afterIndex >= value.Length ? '\0' : value[afterIndex];
            if (!IsTokenChar(before) && !IsTokenChar(after))
                return true;
            start = index + 1;
        }

        return false;
    }

    private static bool IsTokenChar(char value) =>
        value != '\0' && char.IsLetterOrDigit(value);

    private static bool IsSearchEntityType(string type) =>
        !string.Equals(type, EntityTypeService.ProjectNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.SourceNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.SourceChunkNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.SourceBlockNodeType, StringComparison.OrdinalIgnoreCase);

    private sealed record SearchScope(
        Guid ProjectId,
        string Name,
        string Slug,
        bool IsReferenced,
        IReadOnlySet<string> CanonicalIngestSourceIds);

    private sealed class MutableProjectSearchResult
    {
        public required Guid OriginProjectId { get; init; }
        public required string OriginProjectName { get; init; }
        public required string OriginProjectSlug { get; init; }
        public bool IsReferenced { get; init; }
        public required string SourceType { get; init; }
        public Guid? SourceId { get; init; }
        public Guid? ContainerSourceId { get; init; }
        public required string Title { get; init; }
        public required string Content { get; init; }
        public required string Snippet { get; init; }
        public string? Metadata { get; init; }
        public int? ChunkIndex { get; init; }
        public double? LexicalRank { get; set; }
        public int? LexicalPosition { get; set; }
        public double? VectorDistance { get; set; }
        public int? VectorPosition { get; set; }
        public double Score { get; set; }
        public HashSet<string> Reasons { get; } = new(StringComparer.OrdinalIgnoreCase);

        public static MutableProjectSearchResult FromLexical(ProjectLexicalSearchResult result, SearchScope? scope) => new()
        {
            OriginProjectId = scope?.ProjectId ?? Guid.Empty,
            OriginProjectName = scope?.Name ?? string.Empty,
            OriginProjectSlug = scope?.Slug ?? string.Empty,
            IsReferenced = scope?.IsReferenced ?? false,
            SourceType = result.SourceType,
            SourceId = ParseGuid(result.SourceId),
            ContainerSourceId = ParseGuid(result.ContainerSourceId),
            Title = result.Title,
            Content = result.Content,
            Snippet = string.IsNullOrWhiteSpace(result.Snippet) ? Preview(result.Content) : result.Snippet,
            Metadata = result.Metadata,
            ChunkIndex = result.ChunkIndex,
        };

        public static MutableProjectSearchResult FromVector(KnowledgeResult result, SearchScope? scope) => new()
        {
            OriginProjectId = scope?.ProjectId ?? Guid.Empty,
            OriginProjectName = scope?.Name ?? string.Empty,
            OriginProjectSlug = scope?.Slug ?? string.Empty,
            IsReferenced = scope?.IsReferenced ?? false,
            SourceType = result.SourceType,
            SourceId = ParseGuid(result.SourceId),
            ContainerSourceId = null,
            Title = result.Metadata ?? $"{result.SourceType}/{result.SourceId}",
            Content = result.Content,
            Snippet = Preview(result.Content),
            Metadata = result.Metadata,
            ChunkIndex = result.ChunkIndex,
        };

        public ProjectSearchResult ToResult() => new(
            SourceType,
            SourceId,
            ContainerSourceId,
            Title,
            Content,
            Snippet,
            Metadata,
            ChunkIndex,
            LexicalRank,
            LexicalPosition,
            VectorDistance,
            VectorPosition,
            Score,
            Reasons.OrderBy(reason => reason, StringComparer.OrdinalIgnoreCase).ToList(),
            OriginProjectId,
            OriginProjectName,
            OriginProjectSlug,
            IsReferenced);

        private static Guid? ParseGuid(string? value) =>
            !string.IsNullOrWhiteSpace(value) && Guid.TryParseExact(value, "N", out var parsed)
                ? parsed
                : null;
    }
}
