using System.Text;
using System.Text.Json;
using Lorekeeper.Chapters;
using Lorekeeper.Context;
using Lorekeeper.Ingest;
using Lorekeeper.Knowledge;
using Lorekeeper.Llm;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;

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

        var topK = Math.Clamp(request.TopK, 1, 50);
        var sourceTypes = NormalizeSourceTypes(request.SourceTypes);
        var sourceIds = NormalizeSourceIds(request.SourceIds);
        if (request.ContainerSourceId is Guid containerId)
            sourceIds = await ExpandContainerSourceFilterAsync(sourceIds, sourceTypes, containerId, cancellationToken);

        var lexical = await index.SearchAsync(
            new ProjectLexicalSearchRequest(
                Project.ScopeKey(request.ProjectId),
                request.Query.Trim(),
                Math.Min(100, topK * 6),
                sourceTypes,
                sourceIds,
                request.ContainerSourceId?.ToString("N")),
            cancellationToken);
        if (request.LexicalOnly)
        {
            var exactMatches = lexical
                .Where(result => ContainsExactText(result.Content, request.Query) || ContainsExactText(result.Title, request.Query))
                .ToList();
            if (exactMatches.Count > 0)
                lexical = exactMatches;
        }

        var merged = new Dictionary<string, MutableProjectSearchResult>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < lexical.Count; i++)
        {
            var item = lexical[i];
            var key = ResultKey(item.SourceType, item.SourceId, item.ChunkIndex, item.Content);
            if (!merged.TryGetValue(key, out var existing))
            {
                existing = MutableProjectSearchResult.FromLexical(item);
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
                    request.ProjectId,
                    embedding,
                    sourceTypes,
                    sourceIds,
                    request.ContainerSourceId,
                    Math.Min(100, topK * 6),
                    cancellationToken);

                for (var i = 0; i < vectorResults.Count; i++)
                {
                    var item = vectorResults[i];
                    var key = ResultKey(item.SourceType, item.SourceId, item.ChunkIndex, item.Content);
                    if (!merged.TryGetValue(key, out var existing))
                    {
                        existing = MutableProjectSearchResult.FromVector(item);
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
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var chapters = databaseOperation.Repositories.Chapters;
        var acts = databaseOperation.Repositories.Acts;
        var nodes = databaseOperation.Repositories.GraphNodes;
        var ingest = databaseOperation.Repositories.Ingest;
        var types = NormalizeSourceTypes(sourceTypes);
        var limit = Math.Clamp(topK, 1, 50);
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
                var text = BuildEntityText(node);
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
                foreach (var chunk in await ingest.ListSourceChunksAsync(source.Id, cancellationToken))
                {
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

        var ordered = results
            .OrderBy(source => SourceTypeSort(source.SourceType))
            .ThenBy(source => source.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ProjectSearchSourceResponse(ordered.Take(limit).ToList(), ordered.Count, true, limit);
    }

    public async Task<ProjectSourceReadResult?> ReadSourceAsync(
        Guid projectId,
        string sourceType,
        Guid sourceId,
        int? pageNumber = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedType = ProjectSearchSourceTypes.Normalize(sourceType);
        var (title, containerSourceId, content) = normalizedType switch
        {
            ProjectSearchSourceTypes.Chapter or ProjectSearchSourceTypes.ContextChapter
                => await ReadChapterAsync(projectId, sourceId, cancellationToken),
            ProjectSearchSourceTypes.Act
                => await ReadActAsync(projectId, sourceId, cancellationToken),
            ProjectSearchSourceTypes.Entity
                => await ReadEntityAsync(projectId, sourceId, cancellationToken),
            ProjectSearchSourceTypes.IngestSource or ProjectSearchSourceTypes.RawIngestSource
                => await ReadIngestSourceAsync(projectId, sourceId, cancellationToken),
            ProjectSearchSourceTypes.IngestSourceChunk
                => await ReadIngestSourceChunkAsync(projectId, sourceId, cancellationToken),
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
            pageText);
    }

    private async Task<List<KnowledgeResult>> SearchVectorsAsync(
        Guid projectId,
        float[] embedding,
        IReadOnlyCollection<string>? sourceTypes,
        IReadOnlyCollection<string>? sourceIds,
        Guid? containerSourceId,
        int fetchLimit,
        CancellationToken cancellationToken)
    {
        var scopeKey = Project.ScopeKey(projectId);
        List<KnowledgeResult> results;
        if (sourceTypes is { Count: > 0 })
        {
            results = [];
            foreach (var type in sourceTypes)
                results.AddRange(await vectors.SearchAsync(embedding, scopeKey, fetchLimit, type, cancellationToken));
        }
        else
        {
            results = await vectors.SearchAsync(embedding, scopeKey, fetchLimit, cancellationToken: cancellationToken);
        }

        var sourceIdSet = sourceIds?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (sourceIdSet is not null)
            results = results.Where(result => result.SourceId is not null && sourceIdSet.Contains(result.SourceId)).ToList();

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
        return (title, null, BuildEntityText(node));
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

    private static string ResultKey(string sourceType, string? sourceId, int? chunkIndex, string content) =>
        $"{sourceType}|{sourceId}|{chunkIndex?.ToString() ?? "?"}|{StableContentKey(content)}";

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
        _ => 20,
    };

    private static string BuildEntityText(GraphNode node)
    {
        var sb = new StringBuilder();
        sb.Append("Type: ").AppendLine(node.NodeType);
        sb.Append("Name: ").AppendLine(node.Label ?? node.Key);
        AppendOptional(sb, "Summary", IngestWikiSheet.ReadSummary(node.Properties));

        foreach (var property in node.Properties
            .Where(property => !IngestSourceAssertions.IsProtectedProperty(property.Key)
                && !IngestWikiSheet.IsWikiStorageProperty(property.Key)
                && !IngestWikiSheet.IsCanonSourceProperty(property.Key)
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

        foreach (var source in IngestWikiSheet.ReadCanonSources(node.Properties))
        {
            sb.Append("Canon source: ").Append(source.SourceTitle).Append(" (").Append(source.SourceKind).AppendLine(")");
            sb.AppendLine(source.Markdown);
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

    private sealed class MutableProjectSearchResult
    {
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

        public static MutableProjectSearchResult FromLexical(ProjectLexicalSearchResult result) => new()
        {
            SourceType = result.SourceType,
            SourceId = ParseGuid(result.SourceId),
            ContainerSourceId = ParseGuid(result.ContainerSourceId),
            Title = result.Title,
            Content = result.Content,
            Snippet = string.IsNullOrWhiteSpace(result.Snippet) ? Preview(result.Content) : result.Snippet,
            Metadata = result.Metadata,
            ChunkIndex = result.ChunkIndex,
        };

        public static MutableProjectSearchResult FromVector(KnowledgeResult result) => new()
        {
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
            Reasons.OrderBy(reason => reason, StringComparer.OrdinalIgnoreCase).ToList());

        private static Guid? ParseGuid(string? value) =>
            !string.IsNullOrWhiteSpace(value) && Guid.TryParseExact(value, "N", out var parsed)
                ? parsed
                : null;
    }
}
