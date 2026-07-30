using System.Text.Json;
using Lorekeeper.ChapterVisuals;
using Lorekeeper.ImportExport;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;

namespace Lorekeeper.Tests;

public sealed class ProjectExportV8Tests
{
    [Fact]
    public void V8ChapterWritesStructuredManuscriptWithoutLegacyBody()
    {
        var chapterId = Guid.NewGuid();
        var manuscript = ManuscriptCodec.FromPlainText(chapterId, "Structured", revision: 6);
        var chapter = new ProjectExportChapter
        {
            Id = chapterId,
            Title = "Chapter",
            ManuscriptJson = ManuscriptCodec.Serialize(manuscript),
            ManuscriptRevision = manuscript.Revision,
        };

        var json = JsonSerializer.Serialize(chapter, ManuscriptCodec.JsonOptions);

        Assert.Contains("\"manuscriptJson\"", json, StringComparison.Ordinal);
        Assert.Contains("\"manuscriptRevision\":6", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"body\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V8ImportPrevalidationRejectsMalformedLayoutBeforeMutation()
    {
        var chapterId = Guid.NewGuid();
        var manuscript = ManuscriptCodec.FromPlainText(chapterId, "Structured", revision: 6);
        var document = Document(
            new ProjectExportChapter
            {
                Id = chapterId,
                Title = "Chapter",
                ManuscriptJson = ManuscriptCodec.Serialize(manuscript),
                ManuscriptRevision = manuscript.Revision,
                PageLayoutJson = "{ malformed",
            });

        var exception = Assert.Throws<InvalidOperationException>(
            () => ProjectImportJobProcessor.ValidateChapterPayloads(document));

        Assert.Contains(chapterId.ToString("N"), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void V8ImportPrevalidationRejectsIncompletePicturePageProjection()
    {
        var chapterId = Guid.NewGuid();
        var manuscript = ManuscriptCodec.FromPlainText(chapterId, "First\n\nSecond", revision: 6);
        var incomplete = new PicturePageLayout(
            [],
            [
                new PicturePageTextElement(
                    Guid.NewGuid(),
                    "First",
                    0,
                    0,
                    50,
                    50,
                    0,
                    0,
                    PicturePageFontKeys.Default,
                    400,
                    false,
                    12,
                    0,
                    1.4,
                    "#000000",
                    "#ffffff",
                    0,
                    PicturePageTextAlign.Left,
                    ChapterTextVerticalAlign.Top,
                    PicturePageTextShadow.None,
                    PicturePageTextRole.Body,
                    [new ManuscriptRangeReference(manuscript.Content[0].Id)]),
            ]);
        var document = Document(
            new ProjectExportChapter
            {
                Id = chapterId,
                Title = "Chapter",
                ManuscriptJson = ManuscriptCodec.Serialize(manuscript),
                ManuscriptRevision = manuscript.Revision,
                PageLayoutJson = JsonSerializer.Serialize(incomplete, ManuscriptCodec.JsonOptions),
            });

        Assert.Throws<InvalidOperationException>(
            () => ProjectImportJobProcessor.ValidateChapterPayloads(document));
    }

    private static ProjectExportDocument Document(ProjectExportChapter chapter) =>
        new()
        {
            Project = new ProjectExportProject(
                Guid.NewGuid(),
                "Import",
                "import",
                string.Empty,
                true,
                true),
            Chapters = [chapter],
        };
}
