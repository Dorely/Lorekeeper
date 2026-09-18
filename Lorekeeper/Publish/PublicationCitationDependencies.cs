using Lorekeeper.Citations;
using Lorekeeper.Manuscripts;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Publish;

internal static class PublicationCitationDependencies
{
    public static async Task<IReadOnlyList<CitationRecord>> ReadAsync(
        AppDbContext db,
        Guid projectId,
        IEnumerable<string> manuscripts,
        CancellationToken cancellationToken)
    {
        var ids = new HashSet<Guid>();
        foreach (var json in manuscripts)
        {
            var document = System.Text.Json.JsonSerializer.Deserialize<ManuscriptDocument>(json, ManuscriptCodec.JsonOptions)
                ?? throw new InvalidDataException("Publication manuscript is missing.");
            foreach (var occurrence in ManuscriptTraversal.EnumerateCitations(document))
            foreach (var item in occurrence.Cluster.Items)
                ids.Add(item.BibliographicRecordId);
        }
        if (ids.Count == 0)
            return [];
        var records = await db.BibliographicRecords.AsNoTracking()
            .Where(record => record.ProjectId == projectId && ids.Contains(record.Id))
            .OrderBy(record => record.Id).ToListAsync(cancellationToken);
        if (records.Count != ids.Count)
            throw new InvalidDataException("Publication citations reference missing or foreign bibliography records.");
        return records.Select(CitationRecord.FromEntity).ToList();
    }
}
