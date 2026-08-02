using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Manuscripts;

public interface IChapterSemanticProjectionService
{
    Task<string> ExpandPlainTextAsync(Chapter chapter, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<Guid, string>> ExpandPlainTextAsync(
        IReadOnlyCollection<Chapter> chapters,
        CancellationToken cancellationToken = default);
}

public sealed class ChapterSemanticProjectionService(AppDbContext db) : IChapterSemanticProjectionService
{
    public async Task<string> ExpandPlainTextAsync(
        Chapter chapter,
        CancellationToken cancellationToken = default) =>
        (await ExpandPlainTextAsync([chapter], cancellationToken)).GetValueOrDefault(chapter.Id, chapter.PlainText);

    public async Task<IReadOnlyDictionary<Guid, string>> ExpandPlainTextAsync(
        IReadOnlyCollection<Chapter> chapters,
        CancellationToken cancellationToken = default)
    {
        if (chapters.Count == 0)
            return new Dictionary<Guid, string>();
        var compositionIds = chapters.SelectMany(chapter => chapter.Manuscript.Content)
            .Where(block => block.Type == ManuscriptBlockType.DesignedPage && block.PageCompositionId is not null)
            .Select(block => block.PageCompositionId!.Value)
            .Distinct()
            .ToArray();
        var compositionRows = await db.PageCompositions.AsNoTracking()
            .Where(item => compositionIds.Contains(item.Id))
            .Select(item => new { item.Id, item.ProjectId, item.ChapterId, item.SemanticManuscriptJson })
            .ToListAsync(cancellationToken);
        var compositionJson = compositionRows.ToDictionary(item => item.Id);
        return chapters.ToDictionary(chapter => chapter.Id, chapter =>
        {
            var parts = new List<string>();
            foreach (var block in chapter.Manuscript.Content)
            {
                if (block.Type == ManuscriptBlockType.DesignedPage
                    && block.PageCompositionId is Guid compositionId
                    && compositionJson.TryGetValue(compositionId, out var composition)
                    && composition.ProjectId == chapter.ProjectId
                    && composition.ChapterId == chapter.Id)
                {
                    var text = ManuscriptCodec.ProjectPlainText(ManuscriptCodec.Deserialize(composition.SemanticManuscriptJson));
                    if (!string.IsNullOrWhiteSpace(text)) parts.Add(text);
                    continue;
                }
                var blockText = block.Type == ManuscriptBlockType.SceneBreak ? "***" : ManuscriptCodec.Text(block);
                if (!string.IsNullOrWhiteSpace(blockText)) parts.Add(blockText);
            }
            return string.Join("\n\n", parts);
        });
    }
}
