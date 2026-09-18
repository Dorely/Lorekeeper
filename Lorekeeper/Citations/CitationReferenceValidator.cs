using Lorekeeper.Manuscripts;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Citations;

/// <summary>Checks project and source ownership at manuscript write boundaries.</summary>
public static class CitationReferenceValidator
{
    public static async Task ValidateAsync(
        AppDbContext db,
        Guid projectId,
        ManuscriptDocument document,
        CancellationToken cancellationToken)
    {
        var items = ManuscriptTraversal.EnumerateCitations(document)
            .SelectMany(occurrence => occurrence.Cluster.Items).ToList();
        if (items.Count == 0)
            return;

        var recordIds = items.Select(item => item.BibliographicRecordId).Distinct().ToList();
        var records = await db.BibliographicRecords.AsNoTracking()
            .Where(record => record.ProjectId == projectId && recordIds.Contains(record.Id))
            .Select(record => new { record.Id, record.SourceId })
            .ToDictionaryAsync(record => record.Id, record => record.SourceId, cancellationToken);
        foreach (var record in db.BibliographicRecords.Local.Where(record => record.ProjectId == projectId
                     && recordIds.Contains(record.Id) && db.Entry(record).State != EntityState.Deleted))
            records[record.Id] = record.SourceId;
        var locationIds = items.Where(item => item.SourceLocationId.HasValue)
            .Select(item => item.SourceLocationId!.Value).Distinct().ToList();
        var locations = await db.SourceLocations.AsNoTracking()
            .Where(location => location.ProjectId == projectId && locationIds.Contains(location.Id))
            .Select(location => new { location.Id, location.SourceId })
            .ToDictionaryAsync(location => location.Id, location => location.SourceId, cancellationToken);
        foreach (var location in db.SourceLocations.Local.Where(location => location.ProjectId == projectId
                     && locationIds.Contains(location.Id) && db.Entry(location).State != EntityState.Deleted))
            locations[location.Id] = location.SourceId;

        foreach (var entry in db.ChangeTracker.Entries<Lorekeeper.Models.BibliographicRecord>()
                     .Where(entry => entry.State == EntityState.Deleted))
            records.Remove(entry.Entity.Id);
        foreach (var entry in db.ChangeTracker.Entries<Lorekeeper.Models.SourceLocation>()
                     .Where(entry => entry.State == EntityState.Deleted))
            locations.Remove(entry.Entity.Id);

        Validate(items, records, locations);
    }

    public static void Validate(
        IEnumerable<ManuscriptCitationItem> items,
        IReadOnlyDictionary<Guid, Guid?> records,
        IReadOnlyDictionary<Guid, Guid> locations)
    {
        foreach (var item in items)
        {
            if (!records.TryGetValue(item.BibliographicRecordId, out var sourceId))
                throw new InvalidDataException("A citation references a bibliographic record outside this project or one that no longer exists.");
            if (item.SourceLocationId is Guid locationId
                && (!locations.TryGetValue(locationId, out var locationSourceId) || sourceId != locationSourceId))
                throw new InvalidDataException("A citation's source location does not belong to its bibliographic record's retained source.");
        }
    }
}
