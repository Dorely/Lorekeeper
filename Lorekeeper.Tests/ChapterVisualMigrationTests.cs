using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lorekeeper.ChapterVisuals;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;

namespace Lorekeeper.Tests;

public sealed class ChapterVisualMigrationTests
{
    [Fact]
    public void PicturePageReferencesPreserveMultipleTextBoxesAndGeometry()
    {
        var manuscript = ManuscriptCodec.FromPlainText(Guid.NewGuid(), "First box\n\nSecond box", deterministicIds: true);
        var first = Element(Guid.NewGuid(), "First box", readingOrder: 0, x: 11);
        var second = Element(Guid.NewGuid(), "Second box", readingOrder: 1, x: 57);
        var layout = new PicturePageLayout([], [first, second]);

        var referenced = ChapterTextLayoutSynchronizer.AttachReferences(layout, manuscript);
        var json = JsonSerializer.Serialize(referenced, ManuscriptCodec.JsonOptions);
        var persisted = JsonSerializer.Deserialize<PicturePageLayout>(json, ManuscriptCodec.JsonOptions)!;
        var hydrated = ChapterTextLayoutSynchronizer.Hydrate(persisted, manuscript);

        Assert.Equal(2, hydrated.TextElements.Count);
        Assert.Equal(first.Id, hydrated.TextElements[0].Id);
        Assert.Equal(second.Id, hydrated.TextElements[1].Id);
        Assert.Equal(11, hydrated.TextElements[0].XPercent);
        Assert.Equal(57, hydrated.TextElements[1].XPercent);
        Assert.Equal(
            "First box\n\nSecond box",
            ManuscriptCodec.NormalizePlainText(ChapterTextLayoutSynchronizer.ProjectBody(hydrated)));
        Assert.DoesNotContain("\"text\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.All(hydrated.TextElements, element => Assert.Single(element.ContentReferences!));
    }

    [Fact]
    public void IllustratedProseLegacyAnchorMapsOnlyWhenHashMatches()
    {
        var chapterId = Guid.NewGuid();
        const string body = "Alpha\n\nBeta";
        var manuscript = ManuscriptCodec.FromPlainText(chapterId, body, deterministicIds: true);
        var imageId = Guid.NewGuid();
        var elementId = Guid.NewGuid();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("Beta")))[..16].ToLowerInvariant();
        var json = JsonSerializer.Serialize(new
        {
            images = new[]
            {
                new
                {
                    id = elementId,
                    imageId,
                    anchorPosition = "AfterParagraph",
                    paragraphIndex = 1,
                    paragraphHash = hash,
                    widthPercent = 70,
                    alignment = "Center",
                    caption = "Caption",
                    altTextOverride = "Alt",
                    sortOrder = 0,
                    startOnNewPage = false,
                },
            },
        }, ManuscriptCodec.JsonOptions);

        var migratedJson = ManuscriptMigrationService.MigrateLegacyIllustrations(chapterId, body, json, manuscript);
        var migrated = JsonSerializer.Deserialize<IllustratedProseLayout>(migratedJson, ManuscriptCodec.JsonOptions)!;

        Assert.Equal(manuscript.Content[1].Id, Assert.Single(migrated.Images).BlockId);
        Assert.Throws<InvalidDataException>(() =>
            ManuscriptMigrationService.MigrateLegacyIllustrations(
                chapterId,
                body,
                json.Replace(hash, "0000000000000000", StringComparison.Ordinal),
                manuscript));
    }

    [Fact]
    public void PicturePageReconciliationPreservesBoxesAcrossBlockInsertAndDelete()
    {
        var source = ManuscriptCodec.FromPlainText(
            Guid.NewGuid(),
            "First\n\nSecond\n\nThird",
            revision: 3);
        var firstElement = Element(Guid.NewGuid(), "First", readingOrder: 0, x: 11);
        var secondElement = Element(Guid.NewGuid(), "Second\n\nThird", readingOrder: 1, x: 57);
        var chapter = new Chapter
        {
            Id = source.ManuscriptId,
            ProjectId = Guid.NewGuid(),
            Title = "Chapter",
            ManuscriptJson = ManuscriptCodec.Serialize(source),
            ManuscriptRevision = source.Revision,
            PageLayoutJson = JsonSerializer.Serialize(
                ChapterTextLayoutSynchronizer.AttachReferences(
                    new PicturePageLayout([], [firstElement, secondElement]),
                    source),
                ManuscriptCodec.JsonOptions),
        };
        var (inserted, _) = ManuscriptOperations.Apply(
            source,
            [new InsertManuscriptBlock(1, ManuscriptBlockType.Paragraph, "Inserted")]);

        Assert.True(ChapterTextLayoutSynchronizer.SynchronizeFromManuscript(chapter, inserted));
        var afterInsert = ChapterTextLayoutSynchronizer.ReadLayout(chapter.PageLayoutJson);
        Assert.Equal([firstElement.Id, secondElement.Id], afterInsert.TextElements.Select(element => element.Id));
        Assert.Equal(2, afterInsert.TextElements[0].ContentReferences!.Count);
        Assert.Equal(2, afterInsert.TextElements[1].ContentReferences!.Count);

        var (deleted, _) = ManuscriptOperations.Apply(
            inserted,
            [new DeleteManuscriptBlock(source.Content[0].Id)]);
        Assert.True(ChapterTextLayoutSynchronizer.SynchronizeFromManuscript(chapter, deleted));
        var afterDelete = ChapterTextLayoutSynchronizer.ReadLayout(chapter.PageLayoutJson);
        Assert.Equal([firstElement.Id, secondElement.Id], afterDelete.TextElements.Select(element => element.Id));
        Assert.Equal(
            deleted.Content.Select(block => block.Id),
            afterDelete.TextElements.SelectMany(element => element.ContentReferences!).Select(reference => reference.BlockId));
    }

    [Fact]
    public void DeletingAnIllustrationAnchorFailsClosed()
    {
        var source = ManuscriptCodec.FromPlainText(Guid.NewGuid(), "First\n\nSecond", revision: 2);
        var chapter = new Chapter
        {
            Id = source.ManuscriptId,
            ProjectId = Guid.NewGuid(),
            Title = "Chapter",
            ManuscriptJson = ManuscriptCodec.Serialize(source),
            ManuscriptRevision = source.Revision,
            IllustrationLayoutJson = JsonSerializer.Serialize(
                new IllustratedProseLayout(
                [
                    new IllustratedProseImageBlock(
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        ChapterImageAnchorPosition.AfterParagraph,
                        source.Content[0].Id,
                        70,
                        ChapterImageAlignment.Center,
                        string.Empty,
                        string.Empty,
                        0,
                        false),
                ]),
                ManuscriptCodec.JsonOptions),
        };
        var (deleted, _) = ManuscriptOperations.Apply(
            source,
            [new DeleteManuscriptBlock(source.Content[0].Id)]);

        Assert.Throws<InvalidOperationException>(() =>
            ChapterTextLayoutSynchronizer.ValidateIllustrationReferences(chapter, deleted));
    }

    [Fact]
    public void MovingBlocksAcrossPicturePageBoxesKeepsManuscriptReadingOrder()
    {
        var source = ManuscriptCodec.FromPlainText(Guid.NewGuid(), "A\n\nB", revision: 2);
        var layout = ChapterTextLayoutSynchronizer.AttachReferences(
            new PicturePageLayout(
                [],
                [
                    Element(Guid.NewGuid(), "A", readingOrder: 0, x: 11),
                    Element(Guid.NewGuid(), "B", readingOrder: 1, x: 57),
                ]),
            source);
        var chapter = new Chapter
        {
            Id = source.ManuscriptId,
            ProjectId = Guid.NewGuid(),
            Title = "Chapter",
            ManuscriptJson = ManuscriptCodec.Serialize(source),
            ManuscriptRevision = source.Revision,
            PageLayoutJson = JsonSerializer.Serialize(layout, ManuscriptCodec.JsonOptions),
        };
        var (moved, _) = ManuscriptOperations.Apply(
            source,
            [new MoveManuscriptBlock(source.Content[1].Id, 0)]);

        Assert.True(ChapterTextLayoutSynchronizer.SynchronizeFromManuscript(chapter, moved));
        var hydrated = ChapterTextLayoutSynchronizer.Hydrate(
            ChapterTextLayoutSynchronizer.ReadLayout(chapter.PageLayoutJson),
            moved);
        Assert.Equal("B\n\nA", ManuscriptCodec.NormalizePlainText(ChapterTextLayoutSynchronizer.ProjectBody(hydrated)));
        Assert.Equal("B", hydrated.TextElements.Single(element => element.ReadingOrder == 0).Text);
        Assert.Equal("A", hydrated.TextElements.Single(element => element.ReadingOrder == 1).Text);
    }

    private static PicturePageTextElement Element(Guid id, string text, int readingOrder, double x) =>
        new(
            id,
            text,
            x,
            YPercent: 12,
            WidthPercent: 30,
            HeightPercent: 20,
            ZIndex: 5,
            readingOrder,
            PicturePageFontKeys.Default,
            FontWeight: 400,
            Italic: false,
            FontSizePoints: 24,
            LetterSpacingEm: 0,
            LineHeight: 1.35,
            Color: "#111827",
            BackgroundColor: "#FFFFFF",
            BackgroundOpacity: 0,
            PicturePageTextAlign.Left,
            ChapterTextVerticalAlign.Top,
            PicturePageTextShadow.None);
}
