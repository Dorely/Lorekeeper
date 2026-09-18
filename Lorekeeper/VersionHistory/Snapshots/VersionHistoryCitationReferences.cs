using Lorekeeper.Citations;
using Lorekeeper.ImportExport;
using Lorekeeper.Manuscripts;

namespace Lorekeeper.VersionHistory.Snapshots;

internal static class VersionHistoryCitationReferences
{
    public static void Validate(VersionHistorySnapshotPayload payload)
    {
        var document = new ProjectExportDocument
        {
            Project = payload.Project.Project,
            Chapters = payload.Narrative.Chapters.ToList(),
            PublicationSections = payload.Publication.PublicationSections.ToList(),
            PublicationEditions = payload.Publication.PublicationEditions.ToList(),
            PublicationBook = payload.Publication.PublicationBook,
            DesignedPages = payload.Composition.DesignedPages.ToList(),
        };
        var records = payload.Sources.RetainedSources.SelectMany(source => source.BibliographicRecords)
            .Concat(payload.Sources.UnlinkedBibliographicRecords)
            .ToDictionary(record => record.Id, record => record.SourceId);
        var locations = payload.Sources.RetainedSources.SelectMany(source => source.Locations)
            .ToDictionary(location => location.Id, location => location.SourceId);
        foreach (var manuscript in ProjectCitationRemapping.Manuscripts(document))
            CitationReferenceValidator.Validate(
                ManuscriptTraversal.EnumerateCitations(manuscript).SelectMany(item => item.Cluster.Items), records, locations);
    }
}
