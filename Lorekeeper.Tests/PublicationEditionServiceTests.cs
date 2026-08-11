using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Publish;
using Lorekeeper.Llm;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class PublicationEditionServiceTests
{
    [Fact]
    public async Task CoreValuesInheritLiveWhileExplicitEmptyOverrideRemainsStableAndCanReset()
    {
        await WithServiceAsync(async (db, service, project) =>
        {
            var coordinator = new ProjectMutationCoordinator(db.Database.GetConnectionString()!);
            var books = new PublicationBookService(db, coordinator);
            var resolver = new PublicationEffectiveConfigurationResolver(db);
            var core = await books.GetOrCreateAsync(project.Id);
            core = await books.UpdateAsync(project.Id, new PublicationBookPatch(core.Revision, Author: "Core Author"));
            var release = await service.CreateAsync(project.Id, new("Paperback", PublicationEditionFormat.Paperback));
            Assert.Equal("Core Author", (await resolver.ResolveReleaseAsync(project.Id, release.Id)).Edition.Author);

            release = await service.PatchOverridesAsync(project.Id, release.Id,
                new PublicationReleaseOverridePatch(release.Revision, Author: string.Empty));
            var fingerprint = await service.GetSourceFingerprintAsync(project.Id, release.Id);
            core = await books.UpdateAsync(project.Id, new PublicationBookPatch(core.Revision, Author: "Changed Core Author"));
            var overridden = await resolver.ResolveReleaseAsync(project.Id, release.Id);
            Assert.Equal(string.Empty, overridden.Edition.Author);
            Assert.Equal(fingerprint, await service.GetSourceFingerprintAsync(project.Id, release.Id));

            release = await service.PatchOverridesAsync(project.Id, release.Id,
                new PublicationReleaseOverridePatch(release.Revision, ResetFields: [PublicationEditionOverrideField.Author]));
            Assert.Equal("Changed Core Author", (await resolver.ResolveReleaseAsync(project.Id, release.Id)).Edition.Author);
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.PatchOverridesAsync(
                project.Id, release.Id, new PublicationReleaseOverridePatch(release.Revision - 1, Author: "stale")));
        });
    }

    [Fact]
    public async Task NewCoreContentFlowsIntoReleaseWithoutRemovingSparseExclusion()
    {
        await WithServiceAsync(async (db, service, project) =>
        {
            static Chapter Chapter(Guid projectId, string title, int order)
            {
                var chapter = new Chapter { ProjectId = projectId, Title = title, Order = order };
                chapter.ManuscriptJson = ManuscriptCodec.Serialize(ManuscriptCodec.CreateEmpty(chapter.Id));
                return chapter;
            }
            var first = Chapter(project.Id, "First", 0);
            db.Chapters.Add(first);
            await db.SaveChangesAsync();
            var coordinator = new ProjectMutationCoordinator(db.Database.GetConnectionString()!);
            var books = new PublicationBookService(db, coordinator);
            _ = await books.GetOrCreateAsync(project.Id);
            var release = await service.CreateAsync(project.Id, new("EPUB", PublicationEditionFormat.Epub));
            release = await service.SetOutlineSelectionsAsync(project.Id, release.Id,
                [new(PublishOutlineTargetKind.Chapter, first.Id, false)], release.Revision);

            var second = Chapter(project.Id, "Second", 1);
            db.Chapters.Add(second);
            await db.SaveChangesAsync();
            _ = await books.GetOrCreateAsync(project.Id);
            var effective = await new PublicationEffectiveConfigurationResolver(db).ResolveReleaseAsync(project.Id, release.Id);

            Assert.False(effective.OutlineItems.Single(item => item.TargetId == first.Id).IsIncluded);
            Assert.True(effective.OutlineItems.Single(item => item.TargetId == second.Id).IsIncluded);
            Assert.Single(await db.PublicationEditionOutlineItems.Where(item => item.EditionId == release.Id).ToListAsync());
        });
    }

    [Fact]
    public async Task NewPaperbackUsesTheOwnedKdpProfileDefaults()
    {
        await WithServiceAsync(async (_, service, project) =>
        {
            var edition = await service.CreateAsync(
                project.Id,
                new PublicationEditionCreate(
                    "Paperback",
                    PublicationEditionFormat.Paperback,
                    PublicationVendor.AmazonKdp));

            Assert.Equal(6, edition.PageWidthInches);
            Assert.Equal(9, edition.PageHeightInches);
            Assert.Equal(0.75, edition.PageMarginInches);
            Assert.Equal(12, edition.BodyFontSizePoints);
            Assert.Equal(1.55, edition.BodyLineHeight);
            Assert.True(edition.Bleed);
            Assert.Equal("kdp-paperback-v1", edition.VendorProfileVersion);
        });
    }

    [Fact]
    public async Task OneActionPreparationPersistsCoreAndReleaseTargetsAndCanCancel()
    {
        await WithServiceAsync(async (db, releases, project) =>
        {
            var connectionString = db.Database.GetConnectionString()!;
            var coordinator = new ProjectMutationCoordinator(connectionString);
            var queue = new RecordingPreparationQueue();
            var books = new PublicationBookService(db, coordinator);
            var service = new PublicationPreparationService(
                db,
                queue,
                books,
                releases,
                null!,
                coordinator);

            var coreJob = await service.PrepareCoreAsync(project.Id);
            Assert.Equal(PublicationTargetKind.CoreBook, coreJob.TargetKind);
            Assert.Equal(PublicationPreparationStatus.Queued, coreJob.Status);
            Assert.Null(coreJob.EditionId);
            Assert.Contains(coreJob.Id, queue.JobIds);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareCoreAsync(project.Id));

            var cancelled = await service.CancelAsync(project.Id, coreJob.Id);
            Assert.Equal(PublicationPreparationStatus.Cancelled, cancelled.Status);

            var release = await releases.CreateAsync(
                project.Id,
                new PublicationEditionCreate("EPUB", PublicationEditionFormat.Epub));
            var releaseJob = await service.PrepareReleaseAsync(project.Id, release.Id);
            Assert.Equal(PublicationTargetKind.Release, releaseJob.TargetKind);
            Assert.Equal(release.Id, releaseJob.EditionId);
            Assert.Contains(releaseJob.Id, queue.JobIds);
            Assert.Equal(2, await db.PublicationPreparationJobs.CountAsync());
        });
    }

    [Fact]
    public async Task StaleEditionRevisionFailsHonestlyAndRereadCanRetryWithoutLosingIntent()
    {
        await WithServiceAsync(async (_, service, project) =>
        {
            var original = await service.CreateAsync(
                project.Id,
                new PublicationEditionCreate("Paperback", PublicationEditionFormat.Paperback));
            var current = await service.PatchOverridesAsync(project.Id, original.Id,
                new PublicationReleaseOverridePatch(original.Revision, Author: "First author"));

            var conflict = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
                () => service.PatchOverridesAsync(project.Id, original.Id,
                    new PublicationReleaseOverridePatch(original.Revision, Publisher: "Intended publisher")));
            Assert.Contains("revision", conflict.Message, StringComparison.OrdinalIgnoreCase);

            var retried = await service.PatchOverridesAsync(project.Id, original.Id,
                new PublicationReleaseOverridePatch(current.Revision, Publisher: "Intended publisher"));
            Assert.Equal("First author", retried.Author);
            Assert.Equal("Intended publisher", retried.Publisher);
            Assert.True(retried.Revision > current.Revision);
        });
    }

    [Fact]
    public async Task ArchivedEditionsAreReadOnlyThroughServicesAndAssistantTools()
    {
        await WithServiceAsync(async (db, service, project) =>
        {
            var edition = await service.CreateAsync(
                project.Id,
                new PublicationEditionCreate("Paperback", PublicationEditionFormat.Paperback));
            await service.ArchiveAsync(project.Id, edition.Id, edition.Revision);
            var archivedRevision = edition.Revision + 1;
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.PatchOverridesAsync(project.Id, edition.Id,
                    new PublicationReleaseOverridePatch(archivedRevision, Author: "Changed")));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.UpsertMatterAsync(
                    project.Id,
                    edition.Id,
                    new PublicationMatterInput(
                        null,
                        PublicationMatterLocation.Front,
                        PublicationMatterKind.Dedication,
                        "Dedication",
                        ManuscriptCodec.Serialize(ManuscriptCodec.CreateEmpty(Guid.Empty)),
                        true,
                        0),
                    archivedRevision));
            var coverService = new PublicationCoverService(
                db,
                new ProjectMutationCoordinator(db.Database.GetConnectionString()!),
                service,
                new TestPublicationPressRuntime());
            var cover = await coverService.GetAsync(project.Id, edition.Id);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => coverService.UpdateAsync(
                    project.Id,
                    edition.Id,
                    CoverUpdate(cover, acknowledge: true)));

            var assistantTools = new PublishAssistantTools(
                service,
                null!,
                null!,
                null!,
                null!,
                coverService,
                null!,
                null!,
                null!);
            var tools = await assistantTools.BuildAsync(new PublishAssistantContext(project.Id));
            var updateTool = Assert.Single(
                tools.OfType<AIFunction>(),
                tool => tool.Name == "patch_publication_release_overrides");
            var toolException = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await updateTool.InvokeAsync(
                    ToolCallArguments.Create(new Dictionary<string, object?>
                    {
                        ["releaseId"] = edition.Id,
                        ["patch"] = new PublicationReleaseOverridePatch(archivedRevision, Author: "Changed"),
                    })));
            Assert.Contains("read-only", toolException.Message, StringComparison.OrdinalIgnoreCase);

            var persisted = await db.PublicationEditions.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == edition.Id);
            Assert.Equal(PublicationEditionStatus.Archived, persisted.Status);
            Assert.NotEqual("Changed", persisted.Author);
            Assert.Empty(await db.PublicationMatter.Where(item => item.EditionId == edition.Id).ToListAsync());
            Assert.Empty(await db.PublicationCoverDesigns.Where(item => item.EditionId == edition.Id).ToListAsync());
        });
    }

    [Fact]
    public async Task ClonedVendorEditionsCanShareAnIsbnButCannotDriftProductSettings()
    {
        await WithServiceAsync(async (db, service, project) =>
        {
            var first = await service.CreateAsync(
                project.Id,
                new PublicationEditionCreate(
                    "KDP paperback",
                    PublicationEditionFormat.Paperback,
                    PublicationVendor.AmazonKdp));
            first = await service.PatchOverridesAsync(project.Id, first.Id,
                new PublicationReleaseOverridePatch(first.Revision, Isbn: "9780306406157"));
            var clone = await service.CloneAsync(
                project.Id,
                first.Id,
                "Ingram paperback",
                first.Revision);

            clone = await service.PatchOverridesAsync(project.Id, clone.Id,
                new PublicationReleaseOverridePatch(
                    clone.Revision,
                    Destination: PublicationVendor.IngramSpark,
                    Isbn: "978-0-306-40615-7"));

            Assert.Equal("9780306406157", clone.Isbn);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.UpsertMatterAsync(
                    project.Id,
                    clone.Id,
                    new PublicationMatterInput(
                        null,
                        PublicationMatterLocation.Front,
                        PublicationMatterKind.Dedication,
                        "Dedication",
                        ManuscriptCodec.Serialize(ManuscriptCodec.FromPlainText(Guid.Empty, "For everyone.", 0)),
                        true,
                        0),
                    clone.Revision));
            Assert.Contains("shares an ISBN-13", exception.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task PublicationMatterRejectsUnknownStylesAndForeignFigures()
    {
        await WithServiceAsync(async (db, service, project) =>
        {
            var edition = await service.CreateAsync(
                project.Id,
                new PublicationEditionCreate("Paperback", PublicationEditionFormat.Paperback));
            foreach (var generatedKind in new[]
                {
                    PublicationMatterKind.TitlePage,
                    PublicationMatterKind.Copyright,
                    PublicationMatterKind.Contents,
                })
            {
                var generatedException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    service.UpsertMatterAsync(
                        project.Id,
                        edition.Id,
                        new PublicationMatterInput(
                            null,
                            PublicationMatterLocation.Front,
                            generatedKind,
                            generatedKind.ToString(),
                            ManuscriptCodec.Serialize(ManuscriptCodec.CreateEmpty(Guid.Empty)),
                            true,
                            0),
                        edition.Revision));
                Assert.Contains("generated from the effective release settings", generatedException.Message, StringComparison.Ordinal);
            }
            var styled = ManuscriptCodec.FromPlainText(Guid.Empty, "Styled", revision: 0);
            styled.Content[0] = styled.Content[0] with { StyleRole = "missing-style" };
            var styleException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.UpsertMatterAsync(
                    project.Id,
                    edition.Id,
                    new PublicationMatterInput(
                        null,
                        PublicationMatterLocation.Front,
                        PublicationMatterKind.Dedication,
                        "Dedication",
                        ManuscriptCodec.Serialize(styled),
                        true,
                        0),
                    edition.Revision));
            Assert.Contains("unknown paragraph style", styleException.Message, StringComparison.OrdinalIgnoreCase);

            var figure = ManuscriptCodec.FromPlainText(Guid.Empty, "Caption", revision: 0);
            figure.Content[0] = figure.Content[0] with
            {
                Type = ManuscriptBlockType.Figure,
                StyleRole = ManuscriptStyleRoles.FigureCaption,
                ImageId = Guid.NewGuid(),
                AltText = "Missing image",
                FigurePresentation = new FigurePresentation(),
            };
            var figureException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.UpsertMatterAsync(
                    project.Id,
                    edition.Id,
                    new PublicationMatterInput(
                        null,
                        PublicationMatterLocation.Back,
                        PublicationMatterKind.Custom,
                        "Illustrations",
                        ManuscriptCodec.Serialize(figure),
                        true,
                        0),
                    edition.Revision));
            Assert.Contains("owned by this project", figureException.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CoverTemplateAcknowledgementDoesNotStaleIdenticalRenderInputs()
    {
        await WithServiceAsync(async (db, service, project) =>
        {
            var edition = await service.CreateAsync(
                project.Id,
                new PublicationEditionCreate("Paperback", PublicationEditionFormat.Paperback));
            var coordinator = new ProjectMutationCoordinator(db.Database.GetConnectionString()!);
            var covers = new PublicationCoverService(
                db,
                coordinator,
                service,
                new TestPublicationPressRuntime());
            var initial = await covers.GetAsync(project.Id, edition.Id);
            initial = await covers.UpdateAsync(
                project.Id,
                edition.Id,
                CoverUpdate(initial, acknowledge: false));
            var renderFingerprint = await service.GetSourceFingerprintAsync(project.Id, edition.Id);
            db.PublicationArtifacts.Add(new PublicationArtifact
            {
                EditionId = edition.Id,
                Kind = PublicationArtifactKind.InteriorPdf,
                FileName = "interior.pdf",
                MediaType = "application/pdf",
                Data = "%PDF-fixture"u8.ToArray(),
                Sha256 = "fixture",
                ByteLength = 12,
                PageCount = 120,
                SourceFingerprint = renderFingerprint,
                RendererVersion = "0.2.0",
                ProfileId = "kdp-paperback-6x9-preview-v1",
            });
            await db.SaveChangesAsync();
            var calculated = await covers.GetAsync(project.Id, edition.Id);
            Assert.False(calculated.Template.IsAcknowledged);

            var acknowledged = await covers.UpdateAsync(
                project.Id,
                edition.Id,
                CoverUpdate(calculated, acknowledge: true));

            Assert.True(acknowledged.Template.IsAcknowledged);
            Assert.Equal(
                renderFingerprint,
                await service.GetSourceFingerprintAsync(project.Id, edition.Id));
        });
    }

    private static PublicationCoverDesignUpdate CoverUpdate(
        PublicationCoverDesignView cover,
        bool acknowledge) =>
        new(
            cover.Title,
            cover.Subtitle,
            cover.Author,
            cover.SpineText,
            cover.BackCopy,
            cover.BackgroundColor,
            cover.BarcodeMode,
            cover.ImageCropXPercent,
            cover.ImageCropYPercent,
            cover.Revision,
            acknowledge);

    private static async Task WithServiceAsync(
        Func<AppDbContext, PublicationEditionService, Project, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var connectionString = $"Data Source={Path.Combine(directory, "fixture.db")}";
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
            await using var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
            await db.Database.EnsureCreatedAsync();
            var project = new Project
            {
                Name = "Book",
                Slug = $"book-{Guid.NewGuid():N}",
            };
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            var service = new PublicationEditionService(
                db,
                new ProjectMutationCoordinator(connectionString),
                new PublicationActorContext());
            await test(db, service, project);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class RecordingPreparationQueue : IPublicationPreparationQueue
    {
        public List<Guid> JobIds { get; } = [];

        public ValueTask EnqueueAsync(Guid jobId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JobIds.Add(jobId);
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<Guid> ReadAllAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }
    }
}
