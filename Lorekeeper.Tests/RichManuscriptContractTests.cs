using System.Text.Json.Nodes;
using Lorekeeper.Authoring;
using Lorekeeper.Manuscripts;
using Lorekeeper.ProjectArchive;
using Lorekeeper.Publish;
using Lorekeeper.VersionHistory.Snapshots;

namespace Lorekeeper.Tests;

public sealed class RichManuscriptContractTests
{
    [Fact]
    public void Note_reference_cannot_hide_an_unvalidated_citation_payload()
    {
        var document = RichDocument();
        var malformed = document with
        {
            Content = document.Content.Select(block => block with
            {
                Content = block.Content.Select(inline => inline.Type != ManuscriptInlineType.NoteReference ? inline : inline with
                {
                    Citation = new() { Items = [new() { BibliographicRecordId = Guid.NewGuid() }] },
                }).ToList(),
            }).ToList(),
        };
        Assert.Throws<InvalidDataException>(() => ManuscriptCodec.Serialize(malformed));
    }

    [Fact]
    public void List_metadata_changes_and_typing_have_canonical_reversible_operations()
    {
        var before = ManuscriptCodec.FromPlainText(Guid.NewGuid(), "Item");
        before = before with { Content = [before.Content[0] with
        {
            Type = ManuscriptBlockType.ListItem, StyleRole = ManuscriptStyleRoles.ListItem,
            List = new() { Id = "list-a", Ordered = true, Level = 1, Start = 4 },
        }] };
        var after = before with { Content = [before.Content[0] with { Content = [new() { Text = "Edited item" }] }] };
        var (forward, inverse) = AuthoringBatchReducer.CreateCanonicalDelta(before, after);
        Assert.Equal("replaceInlineContent", Assert.Single(forward).Kind);
        var applied = AuthoringBatchReducer.Apply(before, forward);
        Assert.Equal(before.Content[0].List, applied.Document.Content[0].List);
        Assert.True(ManuscriptCodec.ContentEquals(before, AuthoringBatchReducer.Apply(applied.Document, inverse).Document));
        after = after with { Content = [after.Content[0] with { List = before.Content[0].List! with { Level = 2, Start = 8 } }] };
        (forward, inverse) = AuthoringBatchReducer.CreateCanonicalDelta(before, after);
        applied = AuthoringBatchReducer.Apply(before, forward);
        Assert.True(ManuscriptCodec.ContentEquals(after, applied.Document));
        Assert.True(ManuscriptCodec.ContentEquals(before, AuthoringBatchReducer.Apply(applied.Document, inverse).Document));
    }

    [Fact]
    public void Assistant_inline_operations_preserve_atoms_and_reject_lossy_plain_text_edits()
    {
        var document = RichDocument();
        var atomBlock = document.Content.First(block => block.Content.Any(inline => inline.Type == ManuscriptInlineType.NoteReference));
        Assert.Throws<InvalidOperationException>(() => ManuscriptOperations.Apply(document,
            [new ReplaceManuscriptBlockText(atomBlock.Id, "Replacement would lose the note")]));
        var segment = ManuscriptTraversal.EnumerateText(document).Single(item => item.Block.Id == atomBlock.Id);
        var edited = atomBlock.Content.Select(inline => inline.Type == ManuscriptInlineType.Text
            ? inline with { Text = "Edited text" } : inline).ToList();
        var operations = ManuscriptOperationInput.ToOperations([
            new("ReplaceInlineContent", Position: segment.Start, InlineContent: edited),
        ]);
        var result = ManuscriptOperations.Apply(document, operations).Document;
        Assert.Single(result.Notes);
        var retainedReference = Assert.Single(result.Content.Single(block => block.Id == atomBlock.Id).Content,
            inline => inline.Type == ManuscriptInlineType.NoteReference);
        Assert.Equal("note-ref-a", retainedReference.Id);
        Assert.Equal("note-a", retainedReference.NoteId);
        Assert.Throws<ArgumentException>(() => ManuscriptOperationInput.ToOperations([
            new("ReplaceInlineContent", Position: segment.Start, InlineContent: [new() { Text = new string('x', 65537) }]),
        ]));
    }

    [Fact]
    public void V5_upgrade_preserves_content_and_adds_empty_note_ownership()
    {
        var id = Guid.NewGuid();
        var current = ManuscriptCodec.FromPlainText(id, "Before rich content", revision: 4);
        var root = JsonNode.Parse(ManuscriptCodec.Serialize(current))!.AsObject();
        root["schemaVersion"] = 5;
        root.Remove("notes");

        var upgraded = ManuscriptSchemaUpgrade.UpgradeV5DocumentJson(root.ToJsonString(), id, 4);
        var document = ManuscriptCodec.Deserialize(upgraded, id, 4);

        Assert.Equal(7, document.SchemaVersion);
        Assert.Empty(document.Notes);
        Assert.Equal("Before rich content", ManuscriptCodec.ProjectPlainText(document));
    }

    [Fact]
    public void Rich_document_validates_spans_notes_and_utf16_positions()
    {
        var document = RichDocument();

        ManuscriptCodec.Validate(document, document.ManuscriptId, document.Revision);
        var positions = ManuscriptTraversal.EnumerateText(document);

        Assert.Contains(positions, item => item.Start.BlockOrAtomId == "cell-a-paragraph"
            && item.Start.ContainerPath.SequenceEqual(
                ["document", "table:table-a", "row:row-a", "cell:cell-a"],
                StringComparer.Ordinal));
        Assert.Contains(positions, item => item.Start.BlockOrAtomId == "note-paragraph-a"
            && item.Start.ContainerPath.SequenceEqual(["notes", "note-a"], StringComparer.Ordinal));
        Assert.Equal("Name\tValue\nLorekeeper\tStory tools", ManuscriptCodec.Text(document.Content[1]));
        Assert.Throws<InvalidDataException>(() => ManuscriptCodec.Validate(
            document with
            {
                Notes = [document.Notes[0] with { Id = "orphan" }],
            },
            document.ManuscriptId,
            document.Revision));
    }

    [Fact]
    public void Rich_authoring_delta_is_exact_and_reversible_as_one_operation()
    {
        var before = ManuscriptCodec.FromPlainText(Guid.NewGuid(), "Simple", revision: 2);
        var after = RichDocument(before.ManuscriptId, before.Revision);
        var (forward, inverse) = AuthoringBatchReducer.CreateCanonicalDelta(before, after);

        var operation = Assert.Single(forward);
        Assert.Equal("replaceRichDocument", operation.Kind);
        Assert.True(AuthoringBatchReducer.ExactPreconditionsMatch(before, forward));

        var applied = AuthoringBatchReducer.Apply(before, forward);
        Assert.Equal(ManuscriptBlockType.Table, applied.Document.Content[1].Type);
        Assert.Single(applied.Document.Notes);

        var restored = AuthoringBatchReducer.Apply(
            applied.Document,
            inverse,
            allowCanonicalInverseOperations: true);
        Assert.True(ManuscriptCodec.ContentEquals(before, restored.Document));
        Assert.Empty(restored.Document.Notes);
    }

    [Fact]
    public void Nested_typing_and_citation_edits_have_bounded_exact_inverses()
    {
        var before = RichDocument();
        var cell = ManuscriptTraversal.EnumerateText(before).Single(item => item.Block.Id == "cell-a-paragraph");
        var note = ManuscriptTraversal.EnumerateText(before).Single(item => item.Block.Id == "note-paragraph-a");
        var after = ManuscriptOperations.Apply(before,
        [
            new ReplaceManuscriptInlineContent(cell.Start, [new() { Text = "Changed cell" }]),
            new ReplaceManuscriptInlineContent(note.Start,
            [
                new() { Text = "Changed note " },
                new() { Id = "citation", Type = ManuscriptInlineType.Citation,
                    Citation = new() { Items = [new() { BibliographicRecordId = Guid.NewGuid(), LocatorValue = "42" }] } },
            ]),
        ]).Document;
        var (forward, inverse) = AuthoringBatchReducer.CreateCanonicalDelta(before, after);
        Assert.Equal(2, forward.Count);
        Assert.All(forward, operation =>
        {
            Assert.Equal("replaceInlineContent", operation.Kind);
            Assert.Null(operation.RichDocument);
        });
        Assert.True(AuthoringBatchReducer.ExactPreconditionsMatch(before, forward));
        var applied = AuthoringBatchReducer.Apply(before, forward);
        Assert.True(ManuscriptCodec.ContentEquals(after, applied.Document));
        Assert.True(ManuscriptCodec.ContentEquals(before, AuthoringBatchReducer.Apply(applied.Document, inverse).Document));
        Assert.True(ManuscriptCodec.ContentEquals(before, AuthoringBatchReducer.Apply(applied.Document, applied.CanonicalInverse).Document));
        Assert.False(AuthoringBatchReducer.ExactPreconditionsMatch(after, forward));
        var foreign = forward[0] with { Position = forward[0].Position! with { DocumentId = Guid.NewGuid() } };
        Assert.False(AuthoringBatchReducer.ExactPreconditionsMatch(before, [foreign]));
        Assert.Throws<InvalidDataException>(() => AuthoringBatchReducer.Apply(before, [foreign]));
        var wrongPath = forward[0] with { Position = forward[0].Position! with { ContainerPath = ["document"] } };
        Assert.False(AuthoringBatchReducer.ExactPreconditionsMatch(before, [wrongPath]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Html_and_markdown_keep_table_and_note_semantics(bool referenceInTable)
    {
        var document = RichDocument();
        if (referenceInTable)
        {
            document.Content[1].Table!.Rows[1].Cells[0].Content.Add(document.Content[0]);
            document.Content.RemoveAt(0);
        }

        var html = SemanticPublishFormatting.Html(document, _ => "image.png");
        var markdown = SemanticPublishFormatting.Markdown(document, _ => null);

        Assert.Contains("data-table-id=\"table-a\"", html, StringComparison.Ordinal);
        Assert.Contains("role=\"doc-noteref\"", html, StringComparison.Ordinal);
        Assert.Contains(">1</a>", html, StringComparison.Ordinal);
        Assert.Contains("role=\"doc-footnotes\"", html, StringComparison.Ordinal);
        Assert.Contains("Footnote body", html, StringComparison.Ordinal);
        Assert.Contains("href=\"#note-note-a\" role=\"doc-noteref\">1</a>", markdown, StringComparison.Ordinal);
        Assert.Contains("id=\"note-note-a\"", markdown, StringComparison.Ordinal);
        Assert.Contains("1. Footnote body", markdown, StringComparison.Ordinal);
        Assert.Contains("[↩](#note-ref-note-a)", markdown, StringComparison.Ordinal);
        Assert.Contains("[1]", SemanticPublishFormatting.PlainText(document, _ => null), StringComparison.Ordinal);
    }

    [Fact]
    public void Removing_a_note_reference_removes_its_owned_note_in_the_same_operation()
    {
        var document = RichDocument();

        var (updated, changed) = ManuscriptOperations.Apply(
            document,
            [new DeleteManuscriptBlock("intro")]);

        Assert.Empty(updated.Notes);
        Assert.Contains("note-a", changed);
        ManuscriptCodec.Validate(updated, updated.ManuscriptId, updated.Revision);
    }

    [Fact]
    public void Table_gaps_and_multiply_referenced_notes_fail_closed()
    {
        var document = RichDocument();
        var gappedTable = document.Content[1] with
        {
            Table = document.Content[1].Table! with
            {
                Rows = [Row("gap-row", Cell("gap-cell", "Only one column"))],
                HeaderRowCount = 0,
            },
        };
        Assert.Throws<InvalidDataException>(() => ManuscriptCodec.Validate(
            document with { Content = [document.Content[0], gappedTable] },
            document.ManuscriptId,
            document.Revision));

        var duplicateReference = document.Content[0] with
        {
            Content =
            [
                .. document.Content[0].Content,
                new ManuscriptInline
                {
                    Id = "note-ref-b",
                    Type = ManuscriptInlineType.NoteReference,
                    NoteId = "note-a",
                },
            ],
        };
        Assert.Throws<InvalidDataException>(() => ManuscriptCodec.Validate(
            document with { Content = [duplicateReference, document.Content[1]] },
            document.ManuscriptId,
            document.Revision));
    }

    [Fact]
    public void Allocated_format_versions_match_the_rich_manuscript_boundary()
    {
        Assert.Equal(7, ManuscriptDocument.CurrentSchemaVersion);
        Assert.Equal(3, ProjectArchiveContract.RecordSchemaVersion);
        Assert.Equal(10, VersionHistorySnapshotContract.SchemaVersion);
        Assert.True(ProjectArchiveContract.CanReadRecordSchema(1));
        Assert.True(VersionHistorySnapshotContract.CanReadSchema(8));
    }

    private static ManuscriptDocument RichDocument(Guid? id = null, long revision = 3) => new()
    {
        ManuscriptId = id ?? Guid.NewGuid(),
        Revision = revision,
        Content =
        [
            new ManuscriptBlock
            {
                Id = "intro",
                Type = ManuscriptBlockType.Paragraph,
                StyleRole = ManuscriptStyleRoles.Body,
                Content =
                [
                    new ManuscriptInline { Text = "A note" },
                    new ManuscriptInline
                    {
                        Id = "note-ref-a",
                        Type = ManuscriptInlineType.NoteReference,
                        NoteId = "note-a",
                    },
                ],
            },
            new ManuscriptBlock
            {
                Id = "table-block-a",
                Type = ManuscriptBlockType.Table,
                StyleRole = ManuscriptStyleRoles.Table,
                Table = new ManuscriptTable
                {
                    Id = "table-a",
                    ColumnWidthWeights = [1, 2],
                    HeaderRowCount = 1,
                    Rows =
                    [
                        Row("row-a", Cell("cell-a", "Name"), Cell("cell-b", "Value")),
                        Row("row-b", Cell("cell-c", "Lorekeeper"), Cell("cell-d", "Story tools")),
                    ],
                },
            },
        ],
        Notes =
        [
            new ManuscriptNote
            {
                Id = "note-a",
                Kind = ManuscriptNoteKind.Footnote,
                Content = [Paragraph("note-paragraph-a", "Footnote body")],
            },
        ],
    };

    private static ManuscriptTableRow Row(string id, params ManuscriptTableCell[] cells) => new()
    {
        Id = id,
        Cells = [.. cells],
    };

    private static ManuscriptTableCell Cell(string id, string text) => new()
    {
        Id = id,
        Content = [Paragraph($"{id}-paragraph", text)],
    };

    private static ManuscriptBlock Paragraph(string id, string text) => new()
    {
        Id = id,
        Type = ManuscriptBlockType.Paragraph,
        StyleRole = ManuscriptStyleRoles.Body,
        Content = [new ManuscriptInline { Text = text }],
    };
}
