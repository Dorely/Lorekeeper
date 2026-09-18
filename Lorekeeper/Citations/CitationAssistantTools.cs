using System.Text.Json;
using Lorekeeper.Manuscripts;
using Lorekeeper.Sources;
using Microsoft.Extensions.AI;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Citations;

/// <summary>Shared assistant access to the same project-owned bibliography service as Sources.</summary>
public sealed class CitationAssistantTools(IProjectSourcesService sources)
{
    public IReadOnlyList<AITool> Build(Guid projectId, CancellationToken cancellationToken, bool allowMutations, Action? onMutated = null)
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                method: (int offset = 0, int limit = 20) => ListAsync(projectId, offset, limit, cancellationToken),
                name: "list_bibliography",
                description: "List a bounded page of this project's bibliographic IDs, titles, kinds, retained-source links, and revision timestamps. Read a record before changing it. Citation insertion uses these IDs through ReplaceInlineContent in the revision-checked manuscript tools; source evidence links alone do not create citations."),
            AIFunctionFactory.Create(
                method: (Guid recordId) => ReadAsync(projectId, recordId, cancellationToken),
                name: "read_bibliographic_record",
                description: "Read one project-owned bibliography record with full contributors, dates, locators, notes, source ID, and updatedAt. Use updatedAt as expectedUpdatedAt when editing. Preserve fields not requested for change."),
        };
        if (allowMutations)
            tools.Add(AIFunctionFactory.Create(
                method: (BibliographicRecordInput record) => SaveAsync(projectId, record, onMutated, cancellationToken),
                name: "save_bibliographic_record",
                description: "Create or update one bibliography record through the shared Sources service. New records omit id and expectedUpdatedAt. Existing records require the exact id and expectedUpdatedAt from a fresh read, and all retained fields. Changing sourceId detaches affected citation evidence and clears affected Undo histories; do so only when requested. A conflict requires rereading. After an uncertain create response, list/read before retrying to avoid duplicate records."));
        return tools;
    }

    private async Task<string> ListAsync(Guid projectId, int offset, int limit, CancellationToken cancellationToken)
    {
        if (offset < 0 || limit is < 1 or > 50) return Json(new { error = "Use offset >= 0 and limit 1–50." });
        var page = await sources.ReadBibliographyPageAsync(projectId, offset, limit + 1, cancellationToken: cancellationToken);
        return Json(new
        {
            records = page.Take(limit).Select(record => new { record.Id, record.Title, record.Kind, record.SourceId, record.UpdatedAt }),
            hasMore = page.Count > limit,
            nextOffset = page.Count > limit ? (int?)(offset + limit) : null,
        });
    }

    private async Task<string> ReadAsync(Guid projectId, Guid recordId, CancellationToken cancellationToken)
    {
        var records = await sources.ReadBibliographyPageAsync(projectId, 0, 1, recordId, cancellationToken);
        return records.Count == 0 ? Json(new { error = "Bibliographic record not found in this project." }) : Json(records[0]);
    }

    private async Task<string> SaveAsync(Guid projectId, BibliographicRecordInput record, Action? onMutated, CancellationToken cancellationToken)
    {
        try
        {
            var saved = await sources.SaveBibliographicRecordAsync(projectId, record, cancellationToken);
            onMutated?.Invoke();
            return Json(new { saved = true, saved.Id, saved.UpdatedAt, saved.SourceId });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or DbUpdateConcurrencyException)
        {
            return Json(new { saved = false, error = exception.Message });
        }
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, ManuscriptCodec.JsonOptions);
}
