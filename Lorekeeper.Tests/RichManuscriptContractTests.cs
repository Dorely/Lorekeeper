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
    public void V5_upgrade_preserves_content_and_adds_empty_note_ownership()
    {
        var id = Guid.NewGuid();
        var current = ManuscriptCodec.FromPlainText(id, "Before rich content", revision: 4);
        var root = JsonNode.Parse(ManuscriptCodec.Serialize(current))!.AsObject();
        root["schemaVersion"] = 5;
        root.Remove("notes");

        var upgraded = ManuscriptSchemaUpgrade.UpgradeV5DocumentJson(root.ToJsonString(), id, 4);
        var document = ManuscriptCodec.Deserialize(upgraded, id, 4);

        Assert.Equal(6, document.SchemaVersion);
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
    public void Html_and_markdown_keep_table_and_note_semantics()
    {
        var document = RichDocument();

        var html = SemanticPublishFormatting.Html(document, _ => "image.png");
        var markdown = SemanticPublishFormatting.Markdown(document, _ => null);

        Assert.Contains("data-table-id=\"table-a\"", html, StringComparison.Ordinal);
        Assert.Contains("role=\"doc-noteref\"", html, StringComparison.Ordinal);
        Assert.Contains(">1</a>", html, StringComparison.Ordinal);
        Assert.Contains("role=\"doc-footnotes\"", html, StringComparison.Ordinal);
        Assert.Contains("Footnote body", html, StringComparison.Ordinal);
        Assert.Contains("[^note-a]", markdown, StringComparison.Ordinal);
        Assert.Contains("[^note-a]: Footnote body", markdown, StringComparison.Ordinal);
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
        Assert.Equal(6, ManuscriptDocument.CurrentSchemaVersion);
        Assert.Equal(2, ProjectArchiveContract.RecordSchemaVersion);
        Assert.Equal(9, VersionHistorySnapshotContract.SchemaVersion);
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
