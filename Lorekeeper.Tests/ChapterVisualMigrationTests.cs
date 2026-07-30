using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lorekeeper.ChapterVisuals;
using Lorekeeper.Fonts;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Publish;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

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

    [Fact]
    public void PicturePageReadingOrderMovesStableBlocksInsteadOfSwappingTheirText()
    {
        var source = ManuscriptCodec.FromPlainText(Guid.NewGuid(), "A\n\nB", revision: 2);
        var first = Element(Guid.NewGuid(), "A", readingOrder: 0, x: 11);
        var second = Element(Guid.NewGuid(), "B", readingOrder: 1, x: 57);
        var prior = ChapterTextLayoutSynchronizer.AttachReferences(
            new PicturePageLayout([], [first, second]),
            source);
        var submitted = prior with
        {
            TextElements =
            [
                prior.TextElements[0] with { ReadingOrder = 1 },
                prior.TextElements[1] with { ReadingOrder = 0 },
            ],
        };

        var (moved, _) = ManuscriptOperations.Apply(
            source,
            ChapterVisualService.BuildPlainTextLayoutOperations(source, prior, submitted));

        Assert.Equal(source.Content[1].Id, moved.Content[0].Id);
        Assert.Equal("B", ManuscriptCodec.Text(moved.Content[0]));
        Assert.Equal(source.Content[0].Id, moved.Content[1].Id);
        Assert.Equal("A", ManuscriptCodec.Text(moved.Content[1]));
    }

    [Fact]
    public void PicturePageDeletedBoxOrParagraphEmitsStructuredBlockDeletion()
    {
        var source = ManuscriptCodec.FromPlainText(Guid.NewGuid(), "A\n\nB", revision: 2);
        var priorBoxes = ChapterTextLayoutSynchronizer.AttachReferences(
            new PicturePageLayout(
                [],
                [
                    Element(Guid.NewGuid(), "A", readingOrder: 0, x: 11),
                    Element(Guid.NewGuid(), "B", readingOrder: 1, x: 57),
                ]),
            source);
        var withoutSecondBox = priorBoxes with
        {
            TextElements = [priorBoxes.TextElements[0]],
        };
        var (boxDeleted, _) = ManuscriptOperations.Apply(
            source,
            ChapterVisualService.BuildPlainTextLayoutOperations(
                source,
                priorBoxes,
                withoutSecondBox));
        Assert.Equal(source.Content[0].Id, Assert.Single(boxDeleted.Content).Id);

        var priorCombined = ChapterTextLayoutSynchronizer.AttachReferences(
            new PicturePageLayout(
                [],
                [Element(Guid.NewGuid(), "A\n\nB", readingOrder: 0, x: 11)]),
            source);
        var withoutSecondParagraph = priorCombined with
        {
            TextElements = [priorCombined.TextElements[0] with { Text = "A" }],
        };
        var (paragraphDeleted, _) = ManuscriptOperations.Apply(
            source,
            ChapterVisualService.BuildPlainTextLayoutOperations(
                source,
                priorCombined,
                withoutSecondParagraph));
        Assert.Equal(source.Content[0].Id, Assert.Single(paragraphDeleted.Content).Id);
    }

    [Theory]
    [InlineData("A\n\nNEW\n\nB", "A", "B", 0, 2)]
    [InlineData("NEW\n\nA\n\nB", "A", "B", 1, 2)]
    public void PicturePageParagraphInsertionPreservesMatchingStableBlockIds(
        string submittedText,
        string firstText,
        string secondText,
        int firstIndex,
        int secondIndex)
    {
        var source = ManuscriptCodec.FromPlainText(Guid.NewGuid(), "A\n\nB", revision: 2);
        var prior = ChapterTextLayoutSynchronizer.AttachReferences(
            new PicturePageLayout(
                [],
                [Element(Guid.NewGuid(), "A\n\nB", readingOrder: 0, x: 11)]),
            source);
        var submitted = prior with
        {
            TextElements = [prior.TextElements[0] with { Text = submittedText }],
        };

        var (updated, _) = ManuscriptOperations.Apply(
            source,
            ChapterVisualService.BuildPlainTextLayoutOperations(source, prior, submitted));

        Assert.Equal(firstText, ManuscriptCodec.Text(updated.Content[firstIndex]));
        Assert.Equal(source.Content[0].Id, updated.Content[firstIndex].Id);
        Assert.Equal(secondText, ManuscriptCodec.Text(updated.Content[secondIndex]));
        Assert.Equal(source.Content[1].Id, updated.Content[secondIndex].Id);
    }

    [Fact]
    public void PicturePageDeletingFirstParagraphPreservesRemainingStableBlockId()
    {
        var source = ManuscriptCodec.FromPlainText(Guid.NewGuid(), "A\n\nB", revision: 2);
        var prior = ChapterTextLayoutSynchronizer.AttachReferences(
            new PicturePageLayout(
                [],
                [Element(Guid.NewGuid(), "A\n\nB", readingOrder: 0, x: 11)]),
            source);
        var submitted = prior with
        {
            TextElements = [prior.TextElements[0] with { Text = "B" }],
        };

        var (updated, _) = ManuscriptOperations.Apply(
            source,
            ChapterVisualService.BuildPlainTextLayoutOperations(source, prior, submitted));

        Assert.Equal(source.Content[1].Id, Assert.Single(updated.Content).Id);
        Assert.Equal("B", ManuscriptCodec.Text(updated.Content[0]));
    }

    [Fact]
    public void PicturePageMovingParagraphAcrossBoxesPreservesStableBlockId()
    {
        var source = ManuscriptCodec.FromPlainText(Guid.NewGuid(), "A\n\nB", revision: 2);
        var prior = ChapterTextLayoutSynchronizer.AttachReferences(
            new PicturePageLayout(
                [],
                [
                    Element(Guid.NewGuid(), "A", readingOrder: 0, x: 11),
                    Element(Guid.NewGuid(), "B", readingOrder: 1, x: 57),
                ]),
            source);
        var submitted = prior with
        {
            TextElements =
            [
                prior.TextElements[0] with { Text = string.Empty },
                prior.TextElements[1] with { Text = "A\n\nB" },
            ],
        };

        var (updated, _) = ManuscriptOperations.Apply(
            source,
            ChapterVisualService.BuildPlainTextLayoutOperations(source, prior, submitted));

        Assert.Equal(source.Content[0].Id, updated.Content[0].Id);
        Assert.Equal(source.Content[1].Id, updated.Content[1].Id);
        Assert.Equal("A\n\nB", ManuscriptCodec.ProjectPlainText(updated));
    }

    [Fact]
    public async Task VisualLayoutRejectsStaleImageReintroductionAndNestedRemovalCompletes()
    {
        await using var fixture = await VisualFixture.CreateAsync(ChapterVisualMode.IllustratedProse);
        var imageId = Guid.NewGuid();
        fixture.Db.PublishAssets.Add(new PublishAsset
        {
            Id = imageId,
            ProjectId = fixture.Project.Id,
            FileName = "map.png",
            ContentType = "image/png",
            Data = [1],
        });
        var staleLayout = new IllustratedProseLayout(
        [
            new IllustratedProseImageBlock(
                Guid.NewGuid(),
                imageId,
                ChapterImageAnchorPosition.AfterParagraph,
                fixture.Chapter.Manuscript.Content[0].Id,
                70,
                ChapterImageAlignment.Center,
                string.Empty,
                string.Empty,
                0,
                false),
        ]);
        fixture.Chapter.IllustrationLayoutJson = JsonSerializer.Serialize(
            staleLayout,
            ManuscriptCodec.JsonOptions);
        await fixture.Db.SaveChangesAsync();

        await using (await fixture.Mutations.AcquireAsync(fixture.Project.Id))
        {
            await fixture.Visuals
                .RemoveImageReferencesUnderProjectMutationLeaseAsync(fixture.Project.Id, imageId)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        var current = await fixture.Visuals.GetAsync(fixture.Chapter.Id);
        Assert.NotNull(current);
        Assert.Empty(current.IllustrationLayout.Images);
        Assert.Equal(1, current.IllustrationLayout.Revision);

        await Assert.ThrowsAsync<ChapterVisualRevisionConflictException>(
            () => fixture.Visuals.SaveIllustrationLayoutAsync(fixture.Chapter.Id, staleLayout));

        var asset = await fixture.Db.PublishAssets.SingleAsync(candidate => candidate.Id == imageId);
        fixture.Db.PublishAssets.Remove(asset);
        await fixture.Db.SaveChangesAsync();
        var missingImageLayout = staleLayout with { Revision = current.IllustrationLayout.Revision };
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Visuals.SaveIllustrationLayoutAsync(fixture.Chapter.Id, missingImageLayout));
    }

    [Fact]
    public async Task PicturePageTextSaveCompletesThroughNestedManuscriptLease()
    {
        await using var fixture = await VisualFixture.CreateAsync(ChapterVisualMode.PicturePage);
        var initial = ChapterTextLayoutSynchronizer.AttachReferences(
            new PicturePageLayout(
                [],
                [Element(Guid.NewGuid(), "A", readingOrder: 0, x: 11)]),
            fixture.Chapter.Manuscript);
        fixture.Chapter.PageLayoutJson = JsonSerializer.Serialize(initial, ManuscriptCodec.JsonOptions);
        await fixture.Db.SaveChangesAsync();
        var submitted = initial with
        {
            TextElements = [initial.TextElements[0] with { Text = "B" }],
        };

        var updated = await fixture.Visuals
            .SavePageLayoutAsync(fixture.Chapter.Id, submitted)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("B", updated.Body);
        Assert.Equal(1, updated.PageLayout.Revision);
    }

    [Fact]
    public async Task ManuscriptSynchronizationInvalidatesAStalePicturePageLayout()
    {
        await using var fixture = await VisualFixture.CreateAsync(ChapterVisualMode.PicturePage);
        var stale = ChapterTextLayoutSynchronizer.AttachReferences(
            new PicturePageLayout(
                [],
                [Element(Guid.NewGuid(), "A", readingOrder: 0, x: 11)]),
            fixture.Chapter.Manuscript);
        fixture.Chapter.PageLayoutJson = JsonSerializer.Serialize(stale, ManuscriptCodec.JsonOptions);
        await fixture.Db.SaveChangesAsync();

        await fixture.Manuscripts.ApplyAsync(
            fixture.Chapter.Id,
            fixture.Chapter.ManuscriptRevision,
            [
                new ReplaceManuscriptBlockText(
                    fixture.Chapter.Manuscript.Content[0].Id,
                    "Updated"),
            ]);

        await Assert.ThrowsAsync<ChapterVisualRevisionConflictException>(
            () => fixture.Visuals.SavePageLayoutAsync(fixture.Chapter.Id, stale));
    }

    [Fact]
    public async Task LeavingPicturePageModeInvalidatesAStalePicturePageEditor()
    {
        await using var fixture = await VisualFixture.CreateAsync(ChapterVisualMode.PicturePage);
        var stale = ChapterTextLayoutSynchronizer.AttachReferences(
            new PicturePageLayout(
                [],
                [Element(Guid.NewGuid(), "A", readingOrder: 0, x: 11)]),
            fixture.Chapter.Manuscript);
        fixture.Chapter.PageLayoutJson = JsonSerializer.Serialize(stale, ManuscriptCodec.JsonOptions);
        await fixture.Db.SaveChangesAsync();

        var changed = await fixture.Visuals.SetModeAsync(
            fixture.Chapter.Id,
            new ChapterVisualModeUpdate(ChapterVisualMode.Prose));
        Assert.Equal(ChapterVisualMode.Prose, changed.VisualMode);

        await Assert.ThrowsAsync<ChapterVisualRevisionConflictException>(
            () => fixture.Visuals.SavePageLayoutAsync(fixture.Chapter.Id, stale));
        fixture.Project.UpdatedAt = fixture.Project.UpdatedAt.AddSeconds(1);
        await fixture.Db.SaveChangesAsync();
        Assert.Equal(
            ChapterVisualMode.Prose,
            (await fixture.Visuals.GetAsync(fixture.Chapter.Id))!.VisualMode);
    }

    [Fact]
    public async Task LeavingIllustratedProseModeInvalidatesAStaleIllustrationEditor()
    {
        await using var fixture = await VisualFixture.CreateAsync(ChapterVisualMode.IllustratedProse);
        var imageId = Guid.NewGuid();
        fixture.Db.PublishAssets.Add(new PublishAsset
        {
            Id = imageId,
            ProjectId = fixture.Project.Id,
            FileName = "map.png",
            ContentType = "image/png",
            Data = [1],
        });
        var stale = new IllustratedProseLayout(
        [
            new IllustratedProseImageBlock(
                Guid.NewGuid(),
                imageId,
                ChapterImageAnchorPosition.AfterParagraph,
                fixture.Chapter.Manuscript.Content[0].Id,
                70,
                ChapterImageAlignment.Center,
                string.Empty,
                string.Empty,
                0,
                false),
        ]);
        fixture.Chapter.IllustrationLayoutJson = JsonSerializer.Serialize(
            stale,
            ManuscriptCodec.JsonOptions);
        await fixture.Db.SaveChangesAsync();

        var changed = await fixture.Visuals.SetModeAsync(
            fixture.Chapter.Id,
            new ChapterVisualModeUpdate(ChapterVisualMode.Prose));
        Assert.Equal(ChapterVisualMode.Prose, changed.VisualMode);

        await Assert.ThrowsAsync<ChapterVisualRevisionConflictException>(
            () => fixture.Visuals.SaveIllustrationLayoutAsync(fixture.Chapter.Id, stale));
        fixture.Project.UpdatedAt = fixture.Project.UpdatedAt.AddSeconds(1);
        await fixture.Db.SaveChangesAsync();
        Assert.Equal(
            ChapterVisualMode.Prose,
            (await fixture.Visuals.GetAsync(fixture.Chapter.Id))!.VisualMode);
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

    private sealed class VisualFixture : IAsyncDisposable
    {
        private VisualFixture(
            AppDbContext db,
            Project project,
            Chapter chapter,
            ProjectMutationCoordinator mutations,
            IManuscriptService manuscripts,
            ChapterVisualService visuals)
        {
            Db = db;
            Project = project;
            Chapter = chapter;
            Mutations = mutations;
            Manuscripts = manuscripts;
            Visuals = visuals;
        }

        public AppDbContext Db { get; }
        public Project Project { get; }
        public Chapter Chapter { get; }
        public ProjectMutationCoordinator Mutations { get; }
        public IManuscriptService Manuscripts { get; }
        public ChapterVisualService Visuals { get; }

        public static async Task<VisualFixture> CreateAsync(ChapterVisualMode mode)
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(Path.GetTempPath(), $"lorekeeper-visual-{Guid.NewGuid():N}.db")}")
                .Options;
            var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
            await db.Database.MigrateAsync();
            var project = new Project
            {
                Name = "Visual fixture",
                Slug = $"visual-fixture-{Guid.NewGuid():N}",
            };
            var manuscript = ManuscriptCodec.FromPlainText(Guid.NewGuid(), "A", revision: 1);
            var chapter = new Chapter
            {
                Id = manuscript.ManuscriptId,
                Project = project,
                ProjectId = project.Id,
                Title = "Chapter",
                VisualMode = mode,
                ManuscriptJson = ManuscriptCodec.Serialize(manuscript),
                ManuscriptRevision = manuscript.Revision,
            };
            db.AddRange(project, chapter);
            await db.SaveChangesAsync();
            var mutations = new ProjectMutationCoordinator();
            var manuscripts = new TestManuscriptService(db, mutations);
            var visuals = new ChapterVisualService(
                db,
                manuscripts,
                new NoopProjectFontService(),
                new PageGeometryService(db),
                mutations);
            return new VisualFixture(db, project, chapter, mutations, manuscripts, visuals);
        }

        public async ValueTask DisposeAsync()
        {
            var databasePath = Db.Database.GetDbConnection().DataSource;
            await Db.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }
    }

    private sealed class TestManuscriptService(
        AppDbContext db,
        IProjectMutationCoordinator mutations) : IManuscriptService
    {
        public async Task<ManuscriptSnapshot?> GetManuscriptAsync(
            Guid chapterId,
            CancellationToken cancellationToken = default)
        {
            var chapter = await db.Chapters.FirstOrDefaultAsync(
                candidate => candidate.Id == chapterId,
                cancellationToken);
            return chapter is null ? null : Snapshot(chapter);
        }

        public async Task<ManuscriptMutationResult> ReplaceDocumentAsync(
            Guid chapterId,
            long expectedRevision,
            ManuscriptDocument document,
            CancellationToken cancellationToken = default) =>
            await SaveAsync(
                chapterId,
                expectedRevision,
                document,
                [],
                acquireMutationLease: true,
                cancellationToken);

        public async Task<ManuscriptMutationResult> ApplyAsync(
            Guid chapterId,
            long expectedRevision,
            IReadOnlyList<ManuscriptOperation> operations,
            CancellationToken cancellationToken = default)
        {
            var chapter = await db.Chapters.FirstAsync(
                candidate => candidate.Id == chapterId,
                cancellationToken);
            var (document, changedIds) = ManuscriptOperations.Apply(chapter.Manuscript, operations);
            return await SaveAsync(
                chapterId,
                expectedRevision,
                document,
                changedIds,
                acquireMutationLease: true,
                cancellationToken);
        }

        public async Task<ManuscriptMutationResult> ApplyUnderProjectMutationLeaseAsync(
            Guid chapterId,
            long expectedRevision,
            IReadOnlyList<ManuscriptOperation> operations,
            CancellationToken cancellationToken = default)
        {
            var chapter = await db.Chapters.FirstAsync(
                candidate => candidate.Id == chapterId,
                cancellationToken);
            var (document, changedIds) = ManuscriptOperations.Apply(chapter.Manuscript, operations);
            return await SaveAsync(
                chapterId,
                expectedRevision,
                document,
                changedIds,
                acquireMutationLease: false,
                cancellationToken);
        }

        public Task ValidateDocumentReferencesAsync(
            Guid chapterId,
            ManuscriptDocument document,
            IReadOnlyList<ManuscriptStyleView>? styleCatalog = null,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        private async Task<ManuscriptMutationResult> SaveAsync(
            Guid chapterId,
            long expectedRevision,
            ManuscriptDocument document,
            IReadOnlyList<string> changedIds,
            bool acquireMutationLease,
            CancellationToken cancellationToken)
        {
            var projectId = await db.Chapters
                .Where(chapter => chapter.Id == chapterId)
                .Select(chapter => chapter.ProjectId)
                .SingleAsync(cancellationToken);
            IAsyncDisposable? mutation = null;
            if (acquireMutationLease)
                mutation = await mutations.AcquireAsync(projectId, cancellationToken);
            await using var ownedMutation = mutation;
            var chapter = await db.Chapters.FirstAsync(
                candidate => candidate.Id == chapterId,
                cancellationToken);
            if (chapter.ManuscriptRevision != expectedRevision)
            {
                throw new ManuscriptRevisionConflictException(
                    expectedRevision,
                    chapter.ManuscriptRevision);
            }
            chapter.ManuscriptJson = ManuscriptCodec.Serialize(document);
            chapter.ManuscriptRevision = document.Revision;
            ChapterTextLayoutSynchronizer.SynchronizeFromManuscript(chapter, document);
            await db.SaveChangesAsync(cancellationToken);
            return new ManuscriptMutationResult(Snapshot(chapter), changedIds);
        }

        private static ManuscriptSnapshot Snapshot(Chapter chapter) =>
            new(
                chapter.Id,
                chapter.ManuscriptRevision,
                string.Empty,
                chapter.PlainText,
                chapter.Manuscript);
    }

    private sealed class NoopProjectFontService : IProjectFontService
    {
        public Task<IReadOnlyList<ProjectFontFamilyView>> ListAsync(
            Guid projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProjectFontFamilyView>>([]);

        public Task<ProjectFontFamilyView> ImportAsync(
            Guid projectId,
            ProjectFontUpload upload,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteFamilyAsync(
            Guid projectId,
            Guid familyId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<ProjectFontFaceData?> GetFaceDataAsync(
            Guid projectId,
            Guid faceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ProjectFontFaceData?>(null);

        public Task<ProjectFontFaceData?> ResolveFaceAsync(
            Guid projectId,
            string familyKey,
            int weight,
            bool italic,
            bool requireExact = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ProjectFontFaceData?>(null);
    }
}
