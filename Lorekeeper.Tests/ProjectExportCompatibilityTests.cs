using System.Text.Json;
using Lorekeeper.ImportExport;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;

namespace Lorekeeper.Tests;

public sealed class ProjectExportCompatibilityTests
{
    [Fact]
    public void V20WritesPrintProductsAndPublicationSectionsWithoutObsoleteDefaultReleaseState()
    {
        var coverImageId = Guid.NewGuid();
        var document = Document(new ProjectExportChapter()) with
        {
            PublicationBook = new ProjectExportPublicationBook(
                1, "Book", "", "Author", "en", "", "", "", true, false,
                false, false, true, true, false, false, PublishTitlePageMode.Automatic,
                [], null) { AllowDesignedPageOverrides = true },
            PublicationEditions =
            [
                Edition(coverImageId, null, []) with
                {
                    IsDefault = false,
                    BodyFontSizePoints = null,
                    BodyLineHeight = null,
                },
            ],
        };
        var json = JsonSerializer.Serialize(document, ManuscriptCodec.JsonOptions);

        Assert.Equal(21, ProjectExportDocument.CurrentFormatVersion);
        Assert.Contains("\"publicationEditions\"", json, StringComparison.Ordinal);
        Assert.Contains("\"publicationBook\"", json, StringComparison.Ordinal);
        Assert.Contains("\"allowDesignedPageOverrides\":true", json, StringComparison.Ordinal);
        Assert.Contains($"\"selectedCoverImageId\":\"{coverImageId}\"", json, StringComparison.Ordinal);
        Assert.Contains("\"editionSpecificContentEnabled\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"chapterOverrides\":[]", json, StringComparison.Ordinal);
        Assert.Contains("\"publicationSections\":[]", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"isDefault\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("selectedCoverChapterId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"publishProfiles\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"matter\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"styleMappings\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"imagePlacements\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"bodyFontSizePoints\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"bodyLineHeight\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void V12RejectsTheNewCoverImageFieldAtItsVersionBoundary()
    {
        var chapterId = Guid.NewGuid();
        var manuscript = ManuscriptCodec.CreateEmpty(chapterId);
        var document = Document(new ProjectExportChapter
        {
            Id = chapterId,
            Title = "Chapter",
            ManuscriptJson = ManuscriptCodec.Serialize(manuscript),
            ManuscriptRevision = manuscript.Revision,
        }) with
        {
            FormatVersion = 12,
            PublicationEditions = [Edition(Guid.NewGuid(), null, [])],
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => ProjectImportJobProcessor.ValidatePublicationPayloads(document));

        Assert.Contains("introduced after export format v12", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void V9PublishProfileIsolatedAdapterCreatesMatterAndClearsLegacyShape()
    {
        var projectId = Guid.NewGuid();
        var legacyId = Guid.NewGuid();
        var legacy = new ProjectExportLegacyPublishProfile(
            legacyId,
            "Legacy title",
            "",
            "Author",
            "en",
            "",
            "",
            "",
            "",
            "For my family",
            "Thanks",
            "",
            true,
            true,
            false,
            false,
            true,
            true,
            false,
            false,
            PublishTitlePageMode.Automatic,
            PrintPicturePageSpreadMode.WholeSpread,
            EpubPicturePageSpreadMode.RequestLandscape,
            6,
            9,
            0.75,
            11,
            1.3,
            null);
        var document = Document(new ProjectExportChapter()) with
        {
            FormatVersion = 9,
            Project = new ProjectExportProject(projectId, "Book", "book", "", true, true),
            LegacyPublishProfiles = [legacy],
        };

        var adapted = ProjectImportJobProcessor.AdaptLegacyPublicationEditions(document);

        Assert.Null(adapted.LegacyPublishProfiles);
        var edition = Assert.Single(adapted.PublicationEditions);
        Assert.Equal(legacyId, edition.Id);
        Assert.Equal(PublicationEditionFormat.Paperback, edition.Format);
        Assert.Equal(2, edition.LegacyMatter.Count);
        Assert.All(edition.LegacyMatter, item =>
            Assert.Equal(
                item.Id,
                ManuscriptCodec.Deserialize(item.ManuscriptJson).ManuscriptId));
    }

    [Fact]
    public void CurrentChapterWritesStructuredManuscriptWithoutLegacyBody()
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
    public void V8ImportUpgradesManuscriptV1AtTheImportBoundary()
    {
        var chapterId = Guid.NewGuid();
        var current = ManuscriptCodec.FromPlainText(chapterId, "Compatible", revision: 3);
        var v1Json = ManuscriptCodec.Serialize(current).Replace(
            $"\"schemaVersion\":{ManuscriptDocument.CurrentSchemaVersion}",
            "\"schemaVersion\":1",
            StringComparison.Ordinal);
        var document = Document(
            new ProjectExportChapter
            {
                Id = chapterId,
                Title = "Chapter",
                ManuscriptJson = v1Json,
                ManuscriptRevision = 3,
            }) with
        {
            FormatVersion = 8,
        };

        ProjectImportJobProcessor.ValidateChapterPayloads(document);
    }

    [Fact]
    public void V8ImportMaterializesAStyleForEachCustomLegacyRole()
    {
        var chapterId = Guid.NewGuid();
        var current = ManuscriptCodec.FromPlainText(chapterId, "Compatible", revision: 3);
        current.Content[0] = current.Content[0] with { StyleRole = "custom-opening" };
        var v1Json = ManuscriptCodec.Serialize(current).Replace(
            $"\"schemaVersion\":{ManuscriptDocument.CurrentSchemaVersion}",
            "\"schemaVersion\":1",
            StringComparison.Ordinal);
        var document = Document(
            new ProjectExportChapter
            {
                Id = chapterId,
                Title = "Chapter",
                ManuscriptJson = v1Json,
                ManuscriptRevision = 3,
            }) with
        {
            FormatVersion = 8,
        };

        var adapted = ProjectImportJobProcessor.AdaptLegacyManuscriptStyles(document);
        ProjectImportJobProcessor.ValidateChapterPayloads(adapted);

        var style = Assert.Single(adapted.ManuscriptStyles);
        Assert.Equal("custom-opening", style.SemanticRole);
        Assert.Equal(ManuscriptStyleKind.Paragraph, style.Kind);
    }

    [Fact]
    public void V8AdapterAllocatesUniqueNamesForLongRolesWithSharedPrefixes()
    {
        var firstRole = $"{new string('a', 79)}1";
        var secondRole = $"{new string('a', 79)}2";
        ProjectExportChapter Chapter(string role)
        {
            var chapterId = Guid.NewGuid();
            var manuscript = ManuscriptCodec.FromPlainText(chapterId, role, revision: 1);
            manuscript.Content[0] = manuscript.Content[0] with { StyleRole = role };
            return new ProjectExportChapter
            {
                Id = chapterId,
                Title = role[^1..],
                ManuscriptJson = ManuscriptCodec.Serialize(manuscript).Replace(
                    $"\"schemaVersion\":{ManuscriptDocument.CurrentSchemaVersion}",
                    "\"schemaVersion\":1",
                    StringComparison.Ordinal),
                ManuscriptRevision = 1,
            };
        }
        var document = Document(Chapter(firstRole)) with
        {
            FormatVersion = 8,
            Chapters = [Chapter(firstRole), Chapter(secondRole)],
        };

        var adapted = ProjectImportJobProcessor.AdaptLegacyManuscriptStyles(document);
        ProjectImportJobProcessor.ValidateChapterPayloads(adapted);

        Assert.Equal(2, adapted.ManuscriptStyles.Select(style => style.Name).Distinct().Count());
        Assert.All(adapted.ManuscriptStyles, style => Assert.InRange(style.Name.Length, 1, 80));
    }

    [Fact]
    public void ImportedStyleCollisionNamesAlwaysRemainWithinTheRuntimeLimit()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            new string('n', 80),
        };

        var allocated = ProjectImportJobProcessor.AllocateImportedStyleName(
            new string('n', 80),
            used,
            forceSuffix: true);

        Assert.Equal(80, allocated.Length);
        Assert.EndsWith("(imported)", allocated, StringComparison.Ordinal);
    }

    [Fact]
    public void CurrentImportUpgradesV1ManuscriptAndCarriesBookTextStyleDefinitions()
    {
        var chapterId = Guid.NewGuid();
        var current = ManuscriptCodec.FromPlainText(chapterId, "Current", revision: 2);
        var v1Json = ManuscriptCodec.Serialize(current).Replace(
            $"\"schemaVersion\":{ManuscriptDocument.CurrentSchemaVersion}",
            "\"schemaVersion\":1",
            StringComparison.Ordinal);
        var document = Document(new ProjectExportChapter
        {
            Id = chapterId,
            Title = "Chapter",
            ManuscriptJson = v1Json,
            ManuscriptRevision = 2,
        }) with
        {
            ManuscriptStyles =
            [
                new ProjectExportManuscriptStyle(
                    Guid.NewGuid(),
                    "Body",
                    ManuscriptStyleKind.Paragraph,
                    "body",
                    new ManuscriptStyleProperties(FontSizePoints: 11, LineHeight: 1.25),
                    1),
            ],
        };

        ProjectImportJobProcessor.ValidateChapterPayloads(document);
        var roundTrip = JsonSerializer.Deserialize<ProjectExportDocument>(
            JsonSerializer.Serialize(document, ManuscriptCodec.JsonOptions),
            ManuscriptCodec.JsonOptions)!;
        Assert.Equal("body", Assert.Single(roundTrip.ManuscriptStyles).SemanticRole);
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
    public void V8ImportPrevalidationPreservesIncompletePicturePageProjectionForTheUnplacedTray()
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

        ProjectImportJobProcessor.ValidateChapterPayloads(document);
    }

    [Fact]
    public void CurrentImportPrevalidationAcceptsPersistedEmptyPicturePageText()
    {
        var chapterId = Guid.NewGuid();
        var manuscript = ManuscriptCodec.CreateEmpty(chapterId, revision: 1);
        var layout = new PicturePageLayout(
            [],
            [
                new PicturePageTextElement(
                    Guid.NewGuid(),
                    string.Empty,
                    0,
                    0,
                    100,
                    100,
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
                    PicturePageTextShadow.None),
            ]);
        var document = Document(new ProjectExportChapter
        {
            Id = chapterId,
            Title = "Empty Picture Page",
            ManuscriptJson = ManuscriptCodec.Serialize(manuscript),
            ManuscriptRevision = manuscript.Revision,
            VisualMode = ChapterVisualMode.PicturePage,
            PageLayoutJson = JsonSerializer.Serialize(layout, ManuscriptCodec.JsonOptions),
        });

        ProjectImportJobProcessor.ValidateChapterPayloads(document);
    }

    [Fact]
    public void V9PrevalidationRejectsMissingFigureAssetsAndDuplicateImageIds()
    {
        var chapterId = Guid.NewGuid();
        var imageId = Guid.NewGuid();
        var manuscript = new ManuscriptDocument
        {
            ManuscriptId = chapterId,
            Revision = 1,
            Content =
            [
                new ManuscriptBlock
                {
                    Id = "figure",
                    Type = ManuscriptBlockType.Figure,
                    StyleRole = ManuscriptStyleRoles.FigureCaption,
                    ImageId = imageId,
                    AltText = "A map",
                    FigurePresentation = new FigurePresentation(),
                    Content = [new ManuscriptInline { Text = "Known lands" }],
                },
            ],
        };
        var document = Document(new ProjectExportChapter
        {
            Id = chapterId,
            Title = "Chapter",
            ManuscriptJson = ManuscriptCodec.Serialize(manuscript),
            ManuscriptRevision = 1,
        });

        Assert.Throws<InvalidOperationException>(
            () => ProjectImportJobProcessor.ValidateChapterPayloads(document));

        var image = new ProjectExportImage(
            imageId,
            "map.png",
            "image/png",
            [1, 2, 3],
            "A map",
            PublishAssetSource.Uploaded,
            string.Empty,
            string.Empty,
            "{}",
            null,
            null,
            null,
            null,
            null,
            DateTime.UtcNow,
            DateTime.UtcNow);
        document = document with { Images = [image, image] };
        Assert.Throws<InvalidOperationException>(
            () => ProjectImportJobProcessor.ValidateChapterPayloads(document));
    }

    [Fact]
    public void V9PrevalidationRejectsMissingPicturePageAssetsFromAnEmptyImageExport()
    {
        var chapterId = Guid.NewGuid();
        var manuscript = ManuscriptCodec.CreateEmpty(chapterId, revision: 1);
        var layout = new PicturePageLayout(
            [
                new PicturePageImageElement(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    0,
                    0,
                    100,
                    100,
                    ChapterImageFit.Cover,
                    1,
                    0,
                    string.Empty),
            ],
            []);
        var document = Document(new ProjectExportChapter
        {
            Id = chapterId,
            Title = "Picture Page",
            ManuscriptJson = ManuscriptCodec.Serialize(manuscript),
            ManuscriptRevision = manuscript.Revision,
            PageLayoutJson = JsonSerializer.Serialize(layout, ManuscriptCodec.JsonOptions),
        });

        var exception = Assert.Throws<InvalidOperationException>(
            () => ProjectImportJobProcessor.ValidateChapterPayloads(document));

        Assert.Contains("Picture Page", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not included", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void V11PrevalidationRejectsCustomFontReferencesBecauseTheBackupOmittedFontBinaries()
    {
        var chapterId = Guid.NewGuid();
        var missingFamilyId = Guid.NewGuid();
        var manuscript = ManuscriptCodec.FromPlainText(chapterId, "Custom type", revision: 2);
        var layout = new PicturePageLayout(
            [],
            [
                new PicturePageTextElement(
                    Guid.NewGuid(),
                    "Custom type",
                    0,
                    0,
                    100,
                    100,
                    0,
                    0,
                    $"project:{missingFamilyId:N}",
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
        var document = Document(new ProjectExportChapter
        {
            Id = chapterId,
            Title = "Picture Page",
            ManuscriptJson = ManuscriptCodec.Serialize(manuscript),
            ManuscriptRevision = manuscript.Revision,
            PageLayoutJson = JsonSerializer.Serialize(layout, ManuscriptCodec.JsonOptions),
        }) with
        {
            FormatVersion = 11,
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => ProjectImportJobProcessor.ValidateChapterPayloads(document));

        Assert.Contains("pre-v12 backup", exception.Message, StringComparison.Ordinal);
        Assert.Contains("font binary", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void V9PrevalidationRejectsMissingIllustratedProseAssetsFromAnEmptyImageExport()
    {
        var chapterId = Guid.NewGuid();
        var manuscript = ManuscriptCodec.FromPlainText(chapterId, "Anchored", revision: 1);
        var layout = new IllustratedProseLayout(
        [
            new IllustratedProseImageBlock(
                Guid.NewGuid(),
                Guid.NewGuid(),
                ChapterImageAnchorPosition.AfterParagraph,
                manuscript.Content[0].Id,
                70,
                ChapterImageAlignment.Center,
                string.Empty,
                string.Empty,
                0,
                false),
        ]);
        var document = Document(new ProjectExportChapter
        {
            Id = chapterId,
            Title = "Illustrated",
            ManuscriptJson = ManuscriptCodec.Serialize(manuscript),
            ManuscriptRevision = manuscript.Revision,
            IllustrationLayoutJson = JsonSerializer.Serialize(layout, ManuscriptCodec.JsonOptions),
        });

        var exception = Assert.Throws<InvalidOperationException>(
            () => ProjectImportJobProcessor.ValidateChapterPayloads(document));

        Assert.Contains("Illustrated Prose", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not included", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void V9PrevalidationRejectsMalformedStyleMembersBeforeMutation()
    {
        var document = Document(new ProjectExportChapter
        {
            Id = Guid.NewGuid(),
            Title = "Chapter",
            ManuscriptJson = ManuscriptCodec.Serialize(
                ManuscriptCodec.CreateEmpty(Guid.Empty)),
            ManuscriptRevision = 0,
        }) with
        {
            Chapters = [],
            ManuscriptStyles =
            [
                new ProjectExportManuscriptStyle(
                    Guid.NewGuid(),
                    null!,
                    (ManuscriptStyleKind)99,
                    null!,
                    null!,
                    1),
            ],
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => ProjectImportJobProcessor.ValidateChapterPayloads(document));
        Assert.Contains("required", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V9ImportRemapsFigureAssetWhilePreservingAltTextAndCaption()
    {
        var exportedChapterId = Guid.NewGuid();
        var localChapterId = Guid.NewGuid();
        var exportedImageId = Guid.NewGuid();
        var localImageId = Guid.NewGuid();
        var manuscript = new ManuscriptDocument
        {
            ManuscriptId = exportedChapterId,
            Revision = 4,
            Content =
            [
                new ManuscriptBlock
                {
                    Id = "figure",
                    Type = ManuscriptBlockType.Figure,
                    StyleRole = ManuscriptStyleRoles.FigureCaption,
                    ImageId = exportedImageId,
                    AltText = "A detailed map",
                    FigurePresentation = new FigurePresentation(),
                    Content = [new ManuscriptInline { Text = "The eastern road" }],
                },
            ],
        };
        var chapter = new ProjectExportChapter
        {
            Id = exportedChapterId,
            Title = "Map",
            ManuscriptJson = ManuscriptCodec.Serialize(manuscript),
            ManuscriptRevision = 4,
        };

        var remapped = ProjectImportJobProcessor.ImportCurrentManuscript(
            chapter,
            localChapterId,
            ProjectExportDocument.CurrentFormatVersion,
            new Dictionary<Guid, Guid> { [exportedImageId] = localImageId });

        var figure = Assert.Single(remapped.Content);
        Assert.Equal(localChapterId, remapped.ManuscriptId);
        Assert.Equal(localImageId, figure.ImageId);
        Assert.Equal("A detailed map", figure.AltText);
        Assert.Equal("The eastern road", ManuscriptCodec.Text(figure));
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

    private static ProjectExportPublicationEdition Edition(
        Guid? coverImageId,
        Guid? legacyCoverChapterId,
        List<ProjectExportEditionOutlineItem> outlineItems) =>
        new(
            Guid.NewGuid(),
            "Paperback",
            PublicationEditionFormat.Paperback,
            PublicationVendor.Generic,
            "preview-1",
            PublicationEditionStatus.Draft,
            true,
            0,
            string.Empty,
            string.Empty,
            "Author",
            "en",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            true,
            true,
            false,
            false,
            true,
            true,
            false,
            false,
            PublishTitlePageMode.Automatic,
            6,
            9,
            0.75,
            coverImageId,
            LegacyPublicationBinding.PerfectBound,
            LegacyPublicationPaper.White,
            LegacyPublicationInk.BlackAndWhite,
            false,
            false,
            outlineItems,
            null)
        {
            BodyFontSizePoints = 11,
            BodyLineHeight = 1.3,
            PrintPicturePageSpreadMode = PrintPicturePageSpreadMode.WholeSpread,
            EpubPicturePageSpreadMode = EpubPicturePageSpreadMode.RequestLandscape,
            SelectedCoverChapterId = legacyCoverChapterId,
        };
}
