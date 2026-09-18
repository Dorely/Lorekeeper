using Lorekeeper.Citations;
using Lorekeeper.ImportExport;
using Lorekeeper.Manuscripts;

namespace Lorekeeper.Tests;

public sealed class CitationPreservationTests
{
    [Fact]
    public void Archive_remaps_citations_in_body_tables_notes_and_repeated_page_content()
    {
        var record = Guid.NewGuid();
        var location = Guid.NewGuid();
        var mappedRecord = Guid.NewGuid();
        var mappedLocation = Guid.NewGuid();
        var manuscript = Document(record, location);
        var json = ManuscriptCodec.Serialize(manuscript);
        var pageId = Guid.NewGuid();
        var export = new ProjectExportDocument
        {
            Project = new(Guid.NewGuid(), "Book", "book", "", false, false),
            Chapters = [new() { Id = manuscript.ManuscriptId, ManuscriptJson = json }],
            DesignedPages = [new(pageId, "Shared page", null,
                [new(Guid.NewGuid(), pageId, null, json, "Description", 0, [])])],
        };

        var remapped = ProjectCitationRemapping.Remap(export,
            new Dictionary<Guid, Guid> { [record] = mappedRecord },
            new Dictionary<Guid, Guid> { [location] = mappedLocation });

        var documents = ProjectCitationRemapping.Manuscripts(remapped).ToList();
        Assert.Equal(2, documents.Count);
        foreach (var document in documents)
        {
            ManuscriptCodec.Validate(document, document.ManuscriptId, document.Revision);
            var citations = ManuscriptTraversal.EnumerateCitations(document);
            Assert.Equal(3, citations.Count);
            Assert.All(citations, occurrence =>
            {
                var item = Assert.Single(occurrence.Cluster.Items);
                Assert.Equal(mappedRecord, item.BibliographicRecordId);
                Assert.Equal(mappedLocation, item.SourceLocationId);
                Assert.Equal("42", item.LocatorValue);
            });
        }
        Assert.Equal(record, ManuscriptTraversal.EnumerateCitations(manuscript)[0].Cluster.Items[0].BibliographicRecordId);
    }

    [Fact]
    public void Evidence_omission_preserves_bibliography_and_locators_in_nested_content()
    {
        var record = Guid.NewGuid();
        var manuscript = Document(record, Guid.NewGuid());
        var export = new ProjectExportDocument
        {
            Project = new(Guid.NewGuid(), "Book", "book", "", false, false),
            Chapters = [new() { Id = manuscript.ManuscriptId, ManuscriptJson = ManuscriptCodec.Serialize(manuscript) }],
        };
        var omitted = ProjectCitationRemapping.OmitSourceEvidence(export);
        var items = ProjectCitationRemapping.Manuscripts(omitted).SelectMany(ManuscriptTraversal.EnumerateCitations)
            .SelectMany(occurrence => occurrence.Cluster.Items).ToList();
        Assert.Equal(3, items.Count);
        Assert.All(items, item =>
        {
            Assert.Equal(record, item.BibliographicRecordId);
            Assert.Equal("42", item.LocatorValue);
            Assert.Null(item.SourceLocationId);
        });
        Assert.All(ManuscriptTraversal.EnumerateCitations(manuscript), occurrence => Assert.NotNull(occurrence.Cluster.Items[0].SourceLocationId));
        var imported = ProjectCitationRemapping.Remap(omitted,
            new Dictionary<Guid, Guid> { [record] = Guid.NewGuid() }, new Dictionary<Guid, Guid>());
        Assert.Single(ProjectCitationRemapping.Manuscripts(imported));
    }

    [Fact]
    public void Missing_archive_dependencies_are_rejected_before_manuscripts_are_returned()
    {
        var record = Guid.NewGuid();
        var location = Guid.NewGuid();
        var json = ManuscriptCodec.Serialize(Document(record, location));
        Assert.Throws<InvalidDataException>(() => ProjectCitationRemapping.Rewrite(json,
            new Dictionary<Guid, Guid>(), new Dictionary<Guid, Guid>()));
        Assert.Throws<InvalidDataException>(() => ProjectCitationRemapping.Rewrite(json,
            new Dictionary<Guid, Guid> { [record] = Guid.NewGuid() }, new Dictionary<Guid, Guid>()));
    }

    [Fact]
    public void Source_locations_must_belong_to_the_cited_record_source()
    {
        var record = Guid.NewGuid();
        var location = Guid.NewGuid();
        var source = Guid.NewGuid();
        var items = ManuscriptTraversal.EnumerateCitations(Document(record, location))
            .SelectMany(occurrence => occurrence.Cluster.Items).ToList();
        var records = new Dictionary<Guid, Guid?> { [record] = source };
        CitationReferenceValidator.Validate(items, records, new Dictionary<Guid, Guid> { [location] = source });
        Assert.Throws<InvalidDataException>(() => CitationReferenceValidator.Validate(
            items, records, new Dictionary<Guid, Guid> { [location] = Guid.NewGuid() }));
        Assert.Throws<InvalidDataException>(() => CitationReferenceValidator.Validate(
            items, new Dictionary<Guid, Guid?>(), new Dictionary<Guid, Guid> { [location] = source }));
        Assert.Throws<InvalidDataException>(() => CitationReferenceValidator.Validate(
            items, new Dictionary<Guid, Guid?> { [record] = null }, new Dictionary<Guid, Guid> { [location] = source }));
    }

    internal static ManuscriptDocument Document(Guid record, Guid? location)
    {
        ManuscriptBlock Paragraph(string id) => new()
        {
            Id = id,
            Content = [new()
            {
                Id = id + "-citation", Type = ManuscriptInlineType.Citation,
                Citation = new() { Items = [new() { BibliographicRecordId = record, SourceLocationId = location, LocatorValue = "42" }] },
            }],
        };
        var body = Paragraph("body");
        return new()
        {
            ManuscriptId = Guid.NewGuid(),
            Content =
            [
                body with { Content = [.. body.Content, new() { Id = "note-ref", Type = ManuscriptInlineType.NoteReference, NoteId = "note" }] },
                new()
                {
                    Id = "table-block", Type = ManuscriptBlockType.Table,
                    Table = new() { Id = "table", ColumnWidthWeights = [1], Rows =
                        [new() { Id = "row", Cells = [new() { Id = "cell", Content = [Paragraph("cell-text")] }] }] },
                },
            ],
            Notes = [new() { Id = "note", Content = [Paragraph("note-text")] }],
        };
    }
}
