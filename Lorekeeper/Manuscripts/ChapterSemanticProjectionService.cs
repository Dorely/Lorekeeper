using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Manuscripts;

public interface IChapterSemanticProjectionService
{
    Task<string> ExpandPlainTextAsync(Chapter chapter, CancellationToken cancellationToken = default);
    Task<string> ExpandPlainTextAsync(
        Guid projectId,
        ManuscriptDocument document,
        EditorContentTarget target,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<Guid, string>> ExpandPlainTextAsync(
        IReadOnlyCollection<Chapter> chapters,
        CancellationToken cancellationToken = default);
}

public sealed class ChapterSemanticProjectionService(IAppDatabaseOperationFactory database) : IChapterSemanticProjectionService
{
    public async Task<string> ExpandPlainTextAsync(
        Chapter chapter,
        CancellationToken cancellationToken = default) =>
        (await ExpandPlainTextAsync([chapter], cancellationToken)).GetValueOrDefault(chapter.Id, chapter.PlainText);

    public async Task<string> ExpandPlainTextAsync(
        Guid projectId,
        ManuscriptDocument document,
        EditorContentTarget target,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var pageIds = document.Content
            .Where(block => block.Type == ManuscriptBlockType.DesignedPage && block.DesignedPageId is not null)
            .Select(block => block.DesignedPageId!.Value)
            .Distinct()
            .ToArray();
        var rows = await databaseOperation.Db.DesignedPageContents.AsNoTracking()
            .Where(item => item.ProjectId == projectId
                && pageIds.Contains(item.DesignedPageId)
                && (item.EditionId == null || item.EditionId == target.EditionId))
            .Select(item => new { item.DesignedPageId, item.EditionId, item.SemanticManuscriptJson })
            .ToListAsync(cancellationToken);
        var effective = rows
            .GroupBy(item => item.DesignedPageId)
            .ToDictionary(
                group => group.Key,
                group => group.FirstOrDefault(item => item.EditionId == target.EditionId)
                    ?? group.First(item => item.EditionId == null));
        return ExpandDocument(document, effective.ToDictionary(item => item.Key, item => item.Value.SemanticManuscriptJson));
    }

    public async Task<IReadOnlyDictionary<Guid, string>> ExpandPlainTextAsync(
        IReadOnlyCollection<Chapter> chapters,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        if (chapters.Count == 0)
            return new Dictionary<Guid, string>();
        var pageIds = chapters.SelectMany(chapter => chapter.Manuscript.Content)
            .Where(block => block.Type == ManuscriptBlockType.DesignedPage && block.DesignedPageId is not null)
            .Select(block => block.DesignedPageId!.Value)
            .Distinct()
            .ToArray();
        var pageRows = await db.DesignedPageContents.AsNoTracking()
            .Where(item => pageIds.Contains(item.DesignedPageId) && item.EditionId == null)
            .Select(item => new { item.DesignedPageId, item.ProjectId, item.SemanticManuscriptJson })
            .ToListAsync(cancellationToken);
        var pageJson = pageRows.ToDictionary(item => item.DesignedPageId, item => item.SemanticManuscriptJson);
        return chapters.ToDictionary(chapter => chapter.Id, chapter =>
            ExpandDocument(chapter.Manuscript, pageJson));
    }

    private static string ExpandDocument(
        ManuscriptDocument document,
        IReadOnlyDictionary<Guid, string> pageJson)
    {
        var parts = new List<string>();
        foreach (var block in document.Content)
        {
            if (block.Type == ManuscriptBlockType.DesignedPage
                && block.DesignedPageId is Guid pageId
                && pageJson.TryGetValue(pageId, out var semanticJson))
            {
                var text = ManuscriptCodec.ProjectPlainText(ManuscriptCodec.Deserialize(semanticJson));
                if (!string.IsNullOrWhiteSpace(text)) parts.Add(text);
                continue;
            }
            var blockText = block.Type == ManuscriptBlockType.SceneBreak ? "***" : ManuscriptCodec.Text(block);
            if (!string.IsNullOrWhiteSpace(blockText)) parts.Add(blockText);
        }
        return string.Join("\n\n", parts);
    }
}
