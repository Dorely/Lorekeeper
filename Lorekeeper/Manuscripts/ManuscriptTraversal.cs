namespace Lorekeeper.Manuscripts;

public sealed record ManuscriptTextSegment(
    ManuscriptPosition Start,
    ManuscriptPosition End,
    string Text,
    ManuscriptBlock Block);

public sealed record ManuscriptNoteOccurrence(
    string ReferenceId,
    string NoteId,
    ManuscriptNoteKind Kind,
    int Number,
    ManuscriptPosition Position);

/// <summary>
/// Canonical recursive traversal for manuscript text and atom positions. Offsets
/// are .NET string offsets and therefore use the persisted UTF-16 coordinate space.
/// </summary>
public static class ManuscriptTraversal
{
    public static IReadOnlyList<ManuscriptCitationAtomOccurrence> EnumerateCitations(
        ManuscriptDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var result = new List<ManuscriptCitationAtomOccurrence>();
        AddCitations(document, document.Content, ["document"], result);
        foreach (var note in document.Notes)
            AddCitations(document, note.Content, ["notes", note.Id], result);
        return result;
    }

    public static IReadOnlyList<ManuscriptNoteOccurrence> NumberNotes(ManuscriptDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var notes = document.Notes.ToDictionary(note => note.Id, StringComparer.Ordinal);
        var result = new List<ManuscriptNoteOccurrence>();
        AddNoteOccurrences(document, document.Content, ["document"], notes, result, new Dictionary<ManuscriptNoteKind, int>());
        return result;
    }

    public static IReadOnlyList<ManuscriptBlock> EnumerateBlocks(ManuscriptDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var result = new List<ManuscriptBlock>();
        AddRecursive(document.Content, result);
        foreach (var note in document.Notes)
            AddRecursive(note.Content, result);
        return result;
    }

    public static IReadOnlyList<ManuscriptTextSegment> EnumerateText(ManuscriptDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var result = new List<ManuscriptTextSegment>();
        AddBlocks(document.ManuscriptId, document.Content, ["document"], result);
        foreach (var note in document.Notes)
            AddBlocks(document.ManuscriptId, note.Content, ["notes", note.Id], result);
        return result;
    }

    public static ManuscriptTextSegment Resolve(ManuscriptDocument document, ManuscriptPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        if (position.DocumentId != document.ManuscriptId)
            throw new InvalidDataException("The manuscript position belongs to another document.");
        var matches = EnumerateText(document)
            .Where(segment => string.Equals(segment.Start.BlockOrAtomId, position.BlockOrAtomId, StringComparison.Ordinal)
                && segment.Start.ContainerPath.SequenceEqual(position.ContainerPath, StringComparer.Ordinal))
            .ToList();
        if (matches.Count != 1)
            throw new InvalidDataException($"Manuscript position target '{position.BlockOrAtomId}' is missing or ambiguous.");
        var match = matches[0];
        if (position.Offset < 0 || position.Offset > match.Text.Length)
            throw new InvalidDataException("The manuscript position offset is outside its target.");
        RequireUnicodeBoundary(match.Text, position.Offset);
        return match;
    }

    private static void AddBlocks(
        Guid documentId,
        IEnumerable<ManuscriptBlock> blocks,
        IReadOnlyList<string> path,
        ICollection<ManuscriptTextSegment> result)
    {
        foreach (var block in blocks)
        {
            if (block.Type == ManuscriptBlockType.Table && block.Table is { } table)
            {
                foreach (var row in table.Rows)
                {
                    foreach (var cell in row.Cells)
                    {
                        AddBlocks(
                            documentId,
                            cell.Content,
                            [.. path, $"table:{table.Id}", $"row:{row.Id}", $"cell:{cell.Id}"],
                            result);
                    }
                }
                continue;
            }

            var text = ManuscriptCodec.Text(block);
            var start = new ManuscriptPosition(
                documentId,
                path,
                block.Id,
                0,
                ManuscriptPositionAffinity.After);
            result.Add(new ManuscriptTextSegment(
                start,
                start with { Offset = text.Length, Affinity = ManuscriptPositionAffinity.Before },
                text,
                block));
        }
    }

    private static void AddCitations(
        ManuscriptDocument document,
        IEnumerable<ManuscriptBlock> blocks,
        IReadOnlyList<string> path,
        ICollection<ManuscriptCitationAtomOccurrence> result)
    {
        foreach (var block in blocks)
        {
            if (block.Table is { } table)
            {
                foreach (var row in table.Rows)
                foreach (var cell in row.Cells)
                {
                    AddCitations(
                        document,
                        cell.Content,
                        [.. path, $"table:{table.Id}", $"row:{row.Id}", $"cell:{cell.Id}"],
                        result);
                }
                continue;
            }

            var offset = 0;
            foreach (var inline in block.Content)
            {
                if (inline.Type == ManuscriptInlineType.Citation)
                {
                    result.Add(new(
                        inline.Id!,
                        inline.Citation!,
                        block.Id,
                        new ManuscriptPosition(
                            document.ManuscriptId,
                            path,
                            inline.Id!,
                            offset,
                            ManuscriptPositionAffinity.After)));
                }
                offset += inline.Text.Length;
            }
        }
    }

    private static void AddNoteOccurrences(
        ManuscriptDocument document,
        IEnumerable<ManuscriptBlock> blocks,
        IReadOnlyList<string> path,
        IReadOnlyDictionary<string, ManuscriptNote> notes,
        ICollection<ManuscriptNoteOccurrence> result,
        IDictionary<ManuscriptNoteKind, int> counts)
    {
        foreach (var block in blocks)
        {
            if (block.Table is { } table)
            {
                foreach (var row in table.Rows)
                foreach (var cell in row.Cells)
                {
                    AddNoteOccurrences(
                        document,
                        cell.Content,
                        [.. path, $"table:{table.Id}", $"row:{row.Id}", $"cell:{cell.Id}"],
                        notes,
                        result,
                        counts);
                }
                continue;
            }
            var offset = 0;
            foreach (var inline in block.Content)
            {
                if (inline.Type == ManuscriptInlineType.NoteReference
                    && notes.TryGetValue(inline.NoteId!, out var note))
                {
                    counts.TryGetValue(note.Kind, out var current);
                    var number = current + 1;
                    counts[note.Kind] = number;
                    result.Add(new(
                        inline.Id!,
                        note.Id,
                        note.Kind,
                        number,
                        new ManuscriptPosition(
                            document.ManuscriptId,
                            path,
                            inline.Id!,
                            offset,
                            ManuscriptPositionAffinity.After)));
                }
                offset += inline.Text.Length;
            }
        }
    }

    private static void AddRecursive(
        IEnumerable<ManuscriptBlock> blocks,
        ICollection<ManuscriptBlock> result)
    {
        foreach (var block in blocks)
        {
            result.Add(block);
            if (block.Table is not { } table)
                continue;
            foreach (var row in table.Rows)
            foreach (var cell in row.Cells)
                AddRecursive(cell.Content, result);
        }
    }

    private static void RequireUnicodeBoundary(string text, int offset)
    {
        if (offset > 0 && offset < text.Length
            && char.IsHighSurrogate(text[offset - 1])
            && char.IsLowSurrogate(text[offset]))
        {
            throw new InvalidDataException("The manuscript position splits a Unicode surrogate pair.");
        }
    }
}

public sealed record ManuscriptCitationAtomOccurrence(
    string CitationAtomId,
    ManuscriptCitationCluster Cluster,
    string BlockId,
    ManuscriptPosition Position);

internal static class RichManuscriptValidator
{
    public static void Validate(ManuscriptDocument document)
    {
        if (document.Notes is null)
            throw new InvalidDataException("The manuscript note collection is required.");

        var identities = new HashSet<string>(StringComparer.Ordinal);
        var noteReferences = new Dictionary<string, int>(StringComparer.Ordinal);
        ValidateBlocks(document.Content, identities, noteReferences, ManuscriptContentScope.Document);

        foreach (var note in document.Notes)
        {
            if (note is null || !AddIdentity(note.Id, identities))
                throw new InvalidDataException("Manuscript note IDs must be non-empty and globally unique.");
            if (!Enum.IsDefined(note.Kind))
                throw new InvalidDataException($"Note {note.Id} has an unsupported kind.");
            if (note.Content is null || note.Content.Count == 0)
                throw new InvalidDataException($"Note {note.Id} must contain semantic content.");
            ValidateBlocks(note.Content, identities, noteReferences, ManuscriptContentScope.Note);
        }

        var notes = document.Notes.ToDictionary(note => note.Id, StringComparer.Ordinal);
        foreach (var reference in noteReferences)
        {
            if (!notes.ContainsKey(reference.Key))
                throw new InvalidDataException($"Note reference targets missing note '{reference.Key}'.");
            if (reference.Value != 1)
                throw new InvalidDataException($"Note '{reference.Key}' must have exactly one reference.");
        }
        foreach (var note in notes.Values)
        {
            if (!noteReferences.ContainsKey(note.Id))
                throw new InvalidDataException($"Note '{note.Id}' is orphaned.");
        }
    }

    private static void ValidateBlocks(
        IEnumerable<ManuscriptBlock> blocks,
        HashSet<string> identities,
        Dictionary<string, int> noteReferences,
        ManuscriptContentScope scope)
    {
        foreach (var block in blocks)
        {
            if (!AddIdentity(block.Id, identities))
                throw new InvalidDataException("Manuscript block and atom IDs must be non-empty and globally unique.");

            if (scope == ManuscriptContentScope.Cell
                && block.Type is not (ManuscriptBlockType.Paragraph or ManuscriptBlockType.ListItem or ManuscriptBlockType.Figure))
            {
                throw new InvalidDataException($"Table cell {block.Id} contains unsupported {block.Type} content.");
            }
            if (scope == ManuscriptContentScope.Note
                && block.Type is not (ManuscriptBlockType.Paragraph or ManuscriptBlockType.ListItem or ManuscriptBlockType.Figure))
            {
                throw new InvalidDataException($"Note content {block.Id} contains unsupported {block.Type} content.");
            }

            if (block.Type == ManuscriptBlockType.Table)
            {
                if (scope != ManuscriptContentScope.Document)
                    throw new InvalidDataException("Nested tables are not supported.");
                ValidateTable(block, identities, noteReferences);
            }
            else if (block.Table is not null)
            {
                throw new InvalidDataException($"Non-table block {block.Id} cannot contain a table payload.");
            }

            foreach (var inline in block.Content)
            {
                if (inline.Type == ManuscriptInlineType.Citation)
                {
                    if (!AddIdentity(inline.Id, identities))
                        throw new InvalidDataException("Inline citation IDs must be non-empty and globally unique.");
                    if (inline.Text.Length != 0 || inline.Marks.Count != 0 || inline.NoteId is not null
                        || inline.Citation is not { Items.Count: > 0 })
                    {
                        throw new InvalidDataException("A citation atom must contain only its stable ID and a non-empty citation cluster.");
                    }
                    if (inline.Citation.Items.Any(item => item is null
                        || item.BibliographicRecordId == Guid.Empty
                        || item.SourceLocationId == Guid.Empty
                        || item.Prefix is null || item.Prefix.Length > 1_000
                        || item.Suffix is null || item.Suffix.Length > 1_000
                        || item.LocatorLabel is null || item.LocatorLabel.Length > 100
                        || item.LocatorValue is null || item.LocatorValue.Length > 500
                        || !ManuscriptCodec.ContainsOnlyXmlCharacters(item.Prefix)
                        || !ManuscriptCodec.ContainsOnlyXmlCharacters(item.Suffix)
                        || !ManuscriptCodec.ContainsOnlyXmlCharacters(item.LocatorLabel)
                        || !ManuscriptCodec.ContainsOnlyXmlCharacters(item.LocatorValue)))
                    {
                        throw new InvalidDataException("A citation item contains an invalid record, source location, prefix, suffix, or locator.");
                    }
                    continue;
                }
                if (inline.Type != ManuscriptInlineType.NoteReference)
                {
                    if (inline.Citation is not null || inline.NoteId is not null || inline.Id is not null)
                        throw new InvalidDataException("Text runs cannot contain note or citation payloads.");
                    continue;
                }
                if (scope == ManuscriptContentScope.Note)
                    throw new InvalidDataException("Notes cannot contain recursive note references.");
                if (!AddIdentity(inline.Id, identities))
                    throw new InvalidDataException("Inline note-reference IDs must be non-empty and globally unique.");
                if (string.IsNullOrWhiteSpace(inline.NoteId)
                    || inline.Text.Length != 0
                    || inline.Marks.Count != 0
                    || inline.Citation is not null)
                {
                    throw new InvalidDataException("A note reference must contain only its stable ID and note ID.");
                }
                noteReferences[inline.NoteId] = noteReferences.GetValueOrDefault(inline.NoteId) + 1;
            }
        }
    }

    private static void ValidateTable(
        ManuscriptBlock block,
        HashSet<string> identities,
        Dictionary<string, int> noteReferences)
    {
        var table = block.Table
            ?? throw new InvalidDataException($"Table block {block.Id} requires a table payload.");
        if (block.Content.Count != 0)
            throw new InvalidDataException($"Table block {block.Id} cannot contain direct inline content.");
        if (!AddIdentity(table.Id, identities))
            throw new InvalidDataException("Table IDs must be non-empty and globally unique.");
        if (table.ColumnWidthWeights is null || table.ColumnWidthWeights.Count == 0
            || table.ColumnWidthWeights.Any(weight => weight <= 0))
        {
            throw new InvalidDataException($"Table {table.Id} requires positive column-width weights.");
        }
        if (table.Rows is null || table.Rows.Count == 0)
            throw new InvalidDataException($"Table {table.Id} requires at least one row.");
        if (table.HeaderRowCount < 0 || table.HeaderRowCount > table.Rows.Count)
            throw new InvalidDataException($"Table {table.Id} has an invalid leading header-row count.");

        var occupied = new bool[table.Rows.Count, table.ColumnWidthWeights.Count];
        for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            var row = table.Rows[rowIndex];
            if (row is null || !AddIdentity(row.Id, identities))
                throw new InvalidDataException("Table row IDs must be non-empty and globally unique.");
            if (row.Cells is null || row.Cells.Count == 0)
                throw new InvalidDataException($"Table row {row.Id} requires at least one cell.");
            var column = 0;
            foreach (var cell in row.Cells)
            {
                while (column < table.ColumnWidthWeights.Count && occupied[rowIndex, column])
                    column++;
                if (cell is null || !AddIdentity(cell.Id, identities))
                    throw new InvalidDataException("Table cell IDs must be non-empty and globally unique.");
                if (cell.RowSpan <= 0 || cell.ColumnSpan <= 0
                    || rowIndex + cell.RowSpan > table.Rows.Count
                    || column + cell.ColumnSpan > table.ColumnWidthWeights.Count)
                {
                    throw new InvalidDataException($"Table cell {cell.Id} has an out-of-range span.");
                }
                for (var rowOffset = 0; rowOffset < cell.RowSpan; rowOffset++)
                {
                    for (var columnOffset = 0; columnOffset < cell.ColumnSpan; columnOffset++)
                    {
                        if (occupied[rowIndex + rowOffset, column + columnOffset])
                            throw new InvalidDataException($"Table cell {cell.Id} overlaps another span.");
                        occupied[rowIndex + rowOffset, column + columnOffset] = true;
                    }
                }
                if (cell.Content is null || cell.Content.Count == 0)
                    throw new InvalidDataException($"Table cell {cell.Id} requires semantic content.");
                ValidateBlocks(cell.Content, identities, noteReferences, ManuscriptContentScope.Cell);
                column += cell.ColumnSpan;
            }
        }

        for (var row = 0; row < table.Rows.Count; row++)
        for (var column = 0; column < table.ColumnWidthWeights.Count; column++)
        {
            if (!occupied[row, column])
                throw new InvalidDataException($"Table {table.Id} contains a grid gap.");
        }
    }

    private static bool AddIdentity(string? value, ISet<string> identities) =>
        !string.IsNullOrWhiteSpace(value) && identities.Add(value);

    private enum ManuscriptContentScope
    {
        Document,
        Cell,
        Note,
    }
}

internal static class ManuscriptClone
{
    public static ManuscriptDocument Document(ManuscriptDocument document) => document with
    {
        Content = document.Content.Select(Block).ToList(),
        Notes = document.Notes.Select(Note).ToList(),
    };

    public static ManuscriptBlock Block(ManuscriptBlock block) => block with
    {
        Content = block.Content.Select(Inline).ToList(),
        Table = block.Table is null ? null : Table(block.Table),
    };

    public static ManuscriptNote Note(ManuscriptNote note) => note with
    {
        Content = note.Content.Select(Block).ToList(),
    };

    private static ManuscriptTable Table(ManuscriptTable table) => table with
    {
        ColumnWidthWeights = table.ColumnWidthWeights.ToList(),
        Rows = table.Rows.Select(row => row with
        {
            Cells = row.Cells.Select(cell => cell with
            {
                Content = cell.Content.Select(Block).ToList(),
            }).ToList(),
        }).ToList(),
    };

    private static ManuscriptInline Inline(ManuscriptInline inline) => inline with
    {
        Marks = inline.Marks.Select(mark => mark with { }).ToList(),
        Citation = inline.Citation is null ? null : inline.Citation with
        {
            Items = inline.Citation.Items.Select(item => item with { }).ToList(),
        },
    };
}
