namespace Lorekeeper.Manuscripts;

/// <summary>List metadata and numbering are shared by every semantic output.</summary>
public static class ManuscriptLists
{
    public static void Validate(ManuscriptBlock block)
    {
        if (block.List is not { } list) return;
        if (block.Type != ManuscriptBlockType.ListItem)
            throw new InvalidDataException($"Non-list block '{block.Id}' cannot carry list metadata.");
        if (string.IsNullOrWhiteSpace(list.Id) || list.Id.Length > 128
            || list.Id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new InvalidDataException($"List block '{block.Id}' requires a compact stable list ID.");
        if (list.Level is < 0 or > 8 || list.Start is < 1 or > 1_000_000 || !list.Ordered && list.Start is not null)
            throw new InvalidDataException($"List block '{block.Id}' has an invalid level or start number.");
    }

    /// <summary>
    /// Output-only projection: Start becomes the resolved ordinal on each ordered item.
    /// The stored list ID and explicit restart instructions remain untouched.
    /// Counters are independent in the body, each cell, and each note.
    /// </summary>
    public static ManuscriptDocument Resolve(ManuscriptDocument document) => document with
    {
        Content = Resolve(document.Content),
        Notes = document.Notes.Select(note => note with { Content = Resolve(note.Content) }).ToList(),
    };

    public static List<ManuscriptBlock> Resolve(IEnumerable<ManuscriptBlock> blocks)
    {
        var counters = new Dictionary<(string Id, int Level), int>();
        return blocks.Select(block =>
        {
            if (block.Table is { } table)
                return block with { Table = table with { Rows = table.Rows.Select(row => row with
                { Cells = row.Cells.Select(cell => cell with { Content = Resolve(cell.Content) }).ToList() }).ToList() } };
            if (block.List is not { } list) return block;
            // Stored metadata is validated by the codec. A projection may already
            // contain ordinals above the allowed explicit restart value.
            foreach (var nested in counters.Keys.Where(key => key.Id == list.Id && key.Level > list.Level).ToList())
                counters.Remove(nested);
            if (!list.Ordered) return block;
            var number = list.Start ?? checked(counters.GetValueOrDefault((list.Id, list.Level)) + 1);
            counters[(list.Id, list.Level)] = number;
            return block with { List = list with { Start = number } };
        }).ToList();
    }

    public static string Marker(ManuscriptBlock block) => block.List is { Ordered: true } list
        ? $"{list.Start ?? 1}." : "•";
}
