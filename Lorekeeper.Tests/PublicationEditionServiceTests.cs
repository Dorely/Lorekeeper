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
            Assert.Equal(11, edition.BodyFontSizePoints);
            Assert.Equal(1.4, edition.BodyLineHeight);
            Assert.False(edition.Bleed);
            Assert.Equal("kdp-paperback-v1", edition.VendorProfileVersion);
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
            var firstUpdate = Update(original, string.Empty, original.Vendor, original.VendorProfileVersion) with
            {
                Author = "First author",
            };
            var current = await service.UpdateAsync(project.Id, original.Id, firstUpdate);

            var staleUpdate = Update(original, string.Empty, original.Vendor, original.VendorProfileVersion) with
            {
                Publisher = "Intended publisher",
            };
            var conflict = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
                () => service.UpdateAsync(project.Id, original.Id, staleUpdate));
            Assert.Contains("revision", conflict.Message, StringComparison.OrdinalIgnoreCase);

            var retried = await service.UpdateAsync(
                project.Id,
                original.Id,
                Update(current, current.Isbn, current.Vendor, current.VendorProfileVersion) with
                {
                    Publisher = "Intended publisher",
                });
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
            var update = Update(
                edition,
                string.Empty,
                edition.Vendor,
                edition.VendorProfileVersion) with
            {
                ExpectedRevision = archivedRevision,
                Author = "Changed",
            };

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.UpdateAsync(project.Id, edition.Id, update));
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
                coverService,
                null!,
                null!,
                null!,
                null!);
            var tools = await assistantTools.BuildAsync(new PublishAssistantContext(project.Id));
            var updateTool = Assert.Single(
                tools.OfType<AIFunction>(),
                tool => tool.Name == "update_publication_edition");
            var toolException = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await updateTool.InvokeAsync(
                    ToolCallArguments.Create(new Dictionary<string, object?>
                    {
                        ["editionId"] = edition.Id,
                        ["update"] = update,
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
            first = await service.UpdateAsync(
                project.Id,
                first.Id,
                Update(first, isbn: "9780306406157", PublicationVendor.AmazonKdp, "kdp-paperback-v1"));
            var clone = await service.CloneAsync(
                project.Id,
                first.Id,
                "Ingram paperback",
                first.Revision);

            clone = await service.UpdateAsync(
                project.Id,
                clone.Id,
                Update(clone, isbn: "978-0-306-40615-7", PublicationVendor.IngramSpark, "ingram-paperback-pdfx1a-v1"));

            Assert.Equal("9780306406157", clone.Isbn);
            var drifting = Update(
                clone,
                clone.Isbn,
                PublicationVendor.IngramSpark,
                "ingram-paperback-pdfx1a-v1") with
            {
                IncludeVisibleTableOfContents = !clone.IncludeVisibleTableOfContents,
            };
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.UpdateAsync(project.Id, clone.Id, drifting));
            Assert.Contains("product-form settings", exception.Message, StringComparison.Ordinal);
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
                Assert.Contains("generated from edition settings", generatedException.Message, StringComparison.Ordinal);
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

    private static PublicationEditionUpdate Update(
        PublicationEditionView edition,
        string isbn,
        PublicationVendor vendor,
        string vendorProfileVersion) =>
        new(
            edition.TitleOverride,
            edition.Subtitle,
            edition.Author,
            edition.Language,
            edition.Publisher,
            edition.Copyright,
            isbn,
            edition.Description,
            edition.IncludeTableOfContents,
            edition.IncludeVisibleTableOfContents,
            edition.IncludeActSynopses,
            edition.IncludeChapterSynopses,
            edition.IncludeActHeadings,
            edition.IncludeChapterHeadings,
            edition.NumberActs,
            edition.NumberChapters,
            edition.TitlePageMode,
            edition.PageWidthInches,
            edition.PageHeightInches,
            edition.PageMarginInches,
            edition.BodyFontSizePoints,
            edition.BodyLineHeight,
            edition.Revision,
            edition.Name,
            edition.Format,
            vendor,
            vendorProfileVersion,
            edition.Binding,
            edition.Paper,
            edition.Ink,
            edition.Bleed,
            edition.AllowDesignedPageOverrides);

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
            cover.ImageFocalXPercent,
            cover.ImageFocalYPercent,
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
}
