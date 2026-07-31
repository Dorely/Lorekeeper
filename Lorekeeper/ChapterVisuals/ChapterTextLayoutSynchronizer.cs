using System.Text.Json;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;

namespace Lorekeeper.ChapterVisuals;

/// <summary>
/// Keeps the chapter body and any persisted Picture Page text representation aligned.
/// The body is canonical whenever prose is edited outside the Picture Page surface.
/// </summary>
public static class ChapterTextLayoutSynchronizer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static bool SynchronizeFromManuscript(
        Chapter chapter,
        ManuscriptDocument manuscript,
        bool ensureLayout = false)
    {
        var body = ManuscriptCodec.ProjectPlainText(manuscript);
        if (!ensureLayout && string.IsNullOrWhiteSpace(chapter.PageLayoutJson))
            return false;

        var layout = ReadLayout(chapter.PageLayoutJson);
        PicturePageLayout synchronized;
        if (layout.TextElements.Count == 0)
        {
            synchronized = AttachReferences(
                layout with { TextElements = [DefaultTextElement(body)] },
                manuscript);
        }
        else if (layout.TextElements.All(element => element.ContentReferences is not { Count: > 0 }))
        {
            synchronized = AttachReferences(layout, manuscript);
        }
        else
        {
            synchronized = ReconcileReferences(layout, manuscript);
        }

        var currentRevisionJson = JsonSerializer.Serialize(
            synchronized with { Revision = layout.Revision },
            JsonOptions);
        if (string.Equals(chapter.PageLayoutJson, currentRevisionJson, StringComparison.Ordinal))
            return false;
        chapter.PageLayoutJson = JsonSerializer.Serialize(
            synchronized with { Revision = checked(layout.Revision + 1) },
            JsonOptions);
        return true;
    }

    public static void ValidateIllustrationReferences(Chapter chapter, ManuscriptDocument manuscript)
    {
        if (string.IsNullOrWhiteSpace(chapter.IllustrationLayoutJson))
            return;
        IllustratedProseLayout layout;
        try
        {
            layout = JsonSerializer.Deserialize<IllustratedProseLayout>(
                chapter.IllustrationLayoutJson,
                ManuscriptCodec.JsonOptions)
                ?? throw new InvalidDataException("The illustration layout is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The illustration layout is malformed.", exception);
        }

        var blockIds = manuscript.Content.Select(block => block.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var image in layout.Images)
        {
            if (string.IsNullOrWhiteSpace(image.BlockId) || !blockIds.Contains(image.BlockId))
            {
                throw new InvalidOperationException(
                    $"Illustration {image.Id:N} references a manuscript block that would be removed. "
                    + "Move or delete the illustration before deleting its anchored block.");
            }
        }
    }

    public static PicturePageLayout AttachReferences(
        PicturePageLayout layout,
        ManuscriptDocument manuscript)
    {
        var expectedBody = ManuscriptCodec.NormalizePlainText(ProjectBody(layout));
        var actualBody = ManuscriptCodec.NormalizePlainText(ManuscriptCodec.ProjectPlainText(manuscript));
        if (!string.Equals(expectedBody, actualBody, StringComparison.Ordinal))
            throw new InvalidDataException("Picture Page text does not match the owning manuscript.");

        var blockCursor = 0;
        var textElements = new List<PicturePageTextElement>(layout.TextElements.Count);
        foreach (var element in layout.TextElements.OrderBy(element => element.ReadingOrder))
        {
            var blockCount = string.IsNullOrWhiteSpace(ManuscriptCodec.NormalizePlainText(element.Text))
                ? 0
                : ManuscriptCodec.FromPlainText(Guid.Empty, element.Text).Content.Count;
            var references = manuscript.Content
                .Skip(blockCursor)
                .Take(blockCount)
                .Select(block => new ManuscriptRangeReference(block.Id))
                .ToList();
            if (references.Count != blockCount)
                throw new InvalidDataException($"Picture Page text element {element.Id:N} could not be mapped to manuscript blocks.");
            blockCursor += blockCount;
            textElements.Add(element with { ContentReferences = references });
        }

        if (blockCursor != manuscript.Content.Count)
            throw new InvalidDataException("Picture Page text references do not cover the full manuscript.");
        return layout with { TextElements = textElements };
    }

    public static PicturePageLayout Hydrate(
        PicturePageLayout layout,
        ManuscriptDocument manuscript)
    {
        var blocksById = manuscript.Content.ToDictionary(block => block.Id, StringComparer.Ordinal);
        var textElements = layout.TextElements
            .Select(element =>
            {
                if (element.ContentReferences is not { Count: > 0 })
                    return element;
                var values = element.ContentReferences.Select(reference =>
                {
                    if (!blocksById.TryGetValue(reference.BlockId, out var block))
                        throw new InvalidDataException(
                            $"Picture Page text element {element.Id:N} references missing manuscript block {reference.BlockId}.");
                    var text = ManuscriptCodec.Text(block);
                    var start = reference.StartOffset ?? 0;
                    var end = reference.EndOffset ?? text.Length;
                    if (start < 0 || end < start || end > text.Length)
                        throw new InvalidDataException(
                            $"Picture Page text element {element.Id:N} contains an invalid manuscript range.");
                    return text[start..end];
                });
                return element with { Text = string.Join("\n\n", values) };
            })
            .ToList();
        return layout with { TextElements = textElements };
    }

    private static PicturePageLayout ReconcileReferences(
        PicturePageLayout layout,
        ManuscriptDocument manuscript)
    {
        var blocksById = manuscript.Content.ToDictionary(block => block.Id, StringComparer.Ordinal);
        var existingReferences = new Dictionary<string, ManuscriptRangeReference>(StringComparer.Ordinal);
        var existingOwners = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var elementIndex = 0; elementIndex < layout.TextElements.Count; elementIndex++)
        {
            foreach (var reference in layout.TextElements[elementIndex].ContentReferences ?? [])
            {
                if (!blocksById.TryGetValue(reference.BlockId, out var block))
                    continue;
                if (!existingReferences.TryAdd(reference.BlockId, NormalizeRange(reference, block)))
                    throw new InvalidDataException($"Manuscript block {reference.BlockId} is assigned to multiple Picture Page text boxes.");
                existingOwners.Add(reference.BlockId, elementIndex);
            }
        }

        var assignments = Enumerable.Range(0, layout.TextElements.Count)
            .Select(_ => new List<ManuscriptRangeReference>())
            .ToList();
        var readingOrder = layout.TextElements
            .Select((element, index) => (element.ReadingOrder, Index: index))
            .OrderBy(item => item.ReadingOrder)
            .ThenBy(item => item.Index)
            .Select(item => item.Index)
            .ToList();
        var orderByElement = readingOrder
            .Select((elementIndex, orderIndex) => (elementIndex, orderIndex))
            .ToDictionary(item => item.elementIndex, item => item.orderIndex);
        var knownOwnerSequence = manuscript.Content
            .Where(block => existingOwners.ContainsKey(block.Id))
            .Select(block => orderByElement[existingOwners[block.Id]])
            .ToList();
        var ownersRemainOrdered = knownOwnerSequence
            .Zip(knownOwnerSequence.Skip(1))
            .All(pair => pair.First <= pair.Second);
        var desiredCounts = ownersRemainOrdered
            ? AssignInsertedBlocksToAdjacentBoxes(
                manuscript,
                existingOwners,
                readingOrder,
                orderByElement)
            : readingOrder
                .Select(index => layout.TextElements[index].ContentReferences?.Count ?? 0)
                .ToList();
        var difference = manuscript.Content.Count - desiredCounts.Sum();
        if (difference > 0)
        {
            desiredCounts[^1] += difference;
        }
        else
        {
            var toRemove = -difference;
            for (var index = desiredCounts.Count - 1; index >= 0 && toRemove > 0; index--)
            {
                var removed = Math.Min(desiredCounts[index], toRemove);
                desiredCounts[index] -= removed;
                toRemove -= removed;
            }
        }

        var blockCursor = 0;
        for (var orderIndex = 0; orderIndex < readingOrder.Count; orderIndex++)
        {
            var elementIndex = readingOrder[orderIndex];
            for (var offset = 0; offset < desiredCounts[orderIndex]; offset++)
            {
                var block = manuscript.Content[blockCursor++];
                assignments[elementIndex].Add(
                    existingReferences.GetValueOrDefault(
                        block.Id,
                        new ManuscriptRangeReference(block.Id)));
            }
        }

        return layout with
        {
            TextElements = layout.TextElements
                .Select((element, index) => element with { ContentReferences = assignments[index] })
                .ToList(),
        };
    }

    private static List<int> AssignInsertedBlocksToAdjacentBoxes(
        ManuscriptDocument manuscript,
        IReadOnlyDictionary<string, int> existingOwners,
        IReadOnlyList<int> readingOrder,
        IReadOnlyDictionary<int, int> orderByElement)
    {
        var desiredCounts = Enumerable.Repeat(0, readingOrder.Count).ToList();
        var nextKnownOwners = new int?[manuscript.Content.Count];
        int? nextKnownOwner = null;
        for (var index = manuscript.Content.Count - 1; index >= 0; index--)
        {
            if (existingOwners.TryGetValue(manuscript.Content[index].Id, out var elementIndex))
                nextKnownOwner = orderByElement[elementIndex];
            nextKnownOwners[index] = nextKnownOwner;
        }

        int? currentOwner = null;
        for (var index = 0; index < manuscript.Content.Count; index++)
        {
            if (existingOwners.TryGetValue(manuscript.Content[index].Id, out var elementIndex))
                currentOwner = orderByElement[elementIndex];
            var owner = currentOwner ?? nextKnownOwners[index] ?? 0;
            desiredCounts[owner]++;
        }
        return desiredCounts;
    }

    private static ManuscriptRangeReference NormalizeRange(
        ManuscriptRangeReference reference,
        ManuscriptBlock block)
    {
        var length = ManuscriptCodec.Text(block).Length;
        var start = Math.Clamp(reference.StartOffset ?? 0, 0, length);
        var end = Math.Clamp(reference.EndOffset ?? length, start, length);
        return reference with
        {
            StartOffset = reference.StartOffset is null ? null : start,
            EndOffset = reference.EndOffset is null ? null : end,
        };
    }

    public static string ProjectBody(PicturePageLayout layout) =>
        string.Join(
            Environment.NewLine + Environment.NewLine,
            layout.TextElements
                .OrderBy(text => text.ReadingOrder)
                .Select(text => text.Text.Trim())
                .Where(text => !string.IsNullOrWhiteSpace(text)));

    public static PicturePageLayout ReadLayout(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new PicturePageLayout([], []);

        try
        {
            return DeserializePersistedLayout(json);
        }
        catch (JsonException)
        {
            return new PicturePageLayout([], []);
        }
    }

    public static PicturePageLayout DeserializePersistedLayout(string json)
    {
        var layout = JsonSerializer.Deserialize<PicturePageLayout>(json, JsonOptions)
            ?? throw new JsonException("The Picture Page layout is null.");
        return new PicturePageLayout(
            layout.Images ?? [],
            (layout.TextElements ?? [])
                .Select(element => element with { Text = element.Text ?? string.Empty })
                .ToList())
        {
            Revision = layout.Revision,
        };
    }

    private static PicturePageTextElement DefaultTextElement(string body) =>
        new(
            Guid.NewGuid(),
            body,
            XPercent: 12,
            YPercent: 68,
            WidthPercent: 76,
            HeightPercent: 20,
            ZIndex: 10,
            ReadingOrder: 0,
            FontFamilyKey: PicturePageFontKeys.Default,
            FontWeight: 400,
            Italic: false,
            FontSizePoints: 24,
            LetterSpacingEm: 0,
            LineHeight: 1.35,
            Color: "#111827",
            BackgroundColor: "#FFFFFF",
            BackgroundOpacity: 0,
            TextAlign: PicturePageTextAlign.Left,
            VerticalAlign: ChapterTextVerticalAlign.Top,
            Shadow: PicturePageTextShadow.None);
}
