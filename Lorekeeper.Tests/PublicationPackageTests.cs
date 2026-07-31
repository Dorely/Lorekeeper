using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lorekeeper.ImportExport;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Publish;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class PublicationPackageTests
{
    [Fact]
    public void EpubStructureAcceptsRequiredContainerFiles()
    {
        var epub = BuildEpub(includeNavigation: true);

        PublicationPackageService.ValidateEpubStructure(epub);
    }

    [Fact]
    public void EpubStructureRejectsMissingNavigation()
    {
        var epub = BuildEpub(includeNavigation: false);

        var exception = Assert.Throws<InvalidDataException>(
            () => PublicationPackageService.ValidateEpubStructure(epub));

        Assert.Contains("navigation", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EpubStructureRejectsBrokenContainerRootfile()
    {
        var epub = BuildEpub(includeNavigation: true, rootfile: "OEBPS/missing.opf");

        Assert.Throws<InvalidDataException>(() =>
            PublicationPackageService.ValidateEpubStructure(epub));
    }

    [Fact]
    public void EpubStructureRejectsUnsafeManifestPath()
    {
        var epub = BuildEpub(includeNavigation: true, chapterHref: "../chapter.xhtml");

        Assert.Throws<InvalidDataException>(() =>
            PublicationPackageService.ValidateEpubStructure(epub));
    }

    [Fact]
    public void EpubStructureRejectsBrokenSpineReference()
    {
        var epub = BuildEpub(includeNavigation: true, spineIdRef: "missing");

        Assert.Throws<InvalidDataException>(() =>
            PublicationPackageService.ValidateEpubStructure(epub));
    }

    [Fact]
    public void EpubStructureRejectsMalformedSpineXhtml()
    {
        var epub = BuildEpub(includeNavigation: true, chapterContent: "<html>");

        Assert.Throws<InvalidDataException>(() =>
            PublicationPackageService.ValidateEpubStructure(epub));
    }

    [Fact]
    public void EpubStructureRejectsBrokenNavigationTarget()
    {
        var epub = BuildEpub(includeNavigation: true, navigationHref: "missing.xhtml");

        Assert.Throws<InvalidDataException>(() =>
            PublicationPackageService.ValidateEpubStructure(epub));
    }

    [Fact]
    public void EpubStructureRejectsMissingLandmarks()
    {
        var epub = BuildEpub(includeNavigation: true, includeLandmarks: false);

        Assert.Throws<InvalidDataException>(() =>
            PublicationPackageService.ValidateEpubStructure(epub));
    }

    [Fact]
    public void PackageZipIsIndependentOfInputInsertionOrder()
    {
        var first = new Dictionary<string, (PublicationArtifactKind, string, byte[])>
        {
            ["book.epub"] = (PublicationArtifactKind.Epub, "application/epub+zip", "epub"u8.ToArray()),
            ["manifest.json"] = (PublicationArtifactKind.Manifest, "application/json", "{}"u8.ToArray()),
        };
        var second = new Dictionary<string, (PublicationArtifactKind, string, byte[])>
        {
            ["manifest.json"] = (PublicationArtifactKind.Manifest, "application/json", "{}"u8.ToArray()),
            ["book.epub"] = (PublicationArtifactKind.Epub, "application/epub+zip", "epub"u8.ToArray()),
        };

        var firstZip = PublicationPackageService.CreateDeterministicZip(first);
        var secondZip = PublicationPackageService.CreateDeterministicZip(second);

        Assert.Equal(firstZip, secondZip);
        using var archive = new ZipArchive(new MemoryStream(firstZip), ZipArchiveMode.Read);
        Assert.All(archive.Entries, entry =>
            Assert.Equal(
                new DateTime(2000, 1, 1, 0, 0, 0),
                entry.LastWriteTime.DateTime));
    }

    [Fact]
    public void EpubNormalizationRemovesExportTimeAndIsDeterministic()
    {
        var first = BuildEpub(includeNavigation: true, modified: "2026-07-30T01:02:03Z");
        var second = BuildEpub(includeNavigation: true, modified: "2027-08-31T04:05:06Z");

        var firstNormalized = PublicationPackageService.NormalizeEpub(first);
        var secondNormalized = PublicationPackageService.NormalizeEpub(second);

        Assert.Equal(firstNormalized, secondNormalized);
        using var archive = new ZipArchive(new MemoryStream(firstNormalized), ZipArchiveMode.Read);
        var opf = archive.GetEntry("OEBPS/package.opf");
        Assert.NotNull(opf);
        using var reader = new StreamReader(opf.Open());
        Assert.Contains("2000-01-01T00:00:00Z", reader.ReadToEnd(), StringComparison.Ordinal);
        Assert.Equal("mimetype", archive.Entries[0].FullName);
        Assert.Equal(archive.Entries[0].Length, archive.Entries[0].CompressedLength);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"colorSpaces":[]}""")]
    [InlineData("""{"colorSpaces":"DeviceCMYK"}""")]
    [InlineData("""{"colorSpaces":["UnknownSpace"]}""")]
    public void IngramColorEvidenceFailsClosed(string json)
    {
        using var document = JsonDocument.Parse(json);
        var items = new List<PublicationPreflightItem>();

        PublicationPackageService.ValidateIngramColorSpaces(document.RootElement, items);

        Assert.Contains(items, item => item.Code == "PDF_COLOR_SPACE_EVIDENCE_INVALID");
    }

    [Fact]
    public void IngramColorEvidenceRejectsRgbAndAcceptsKnownPrintSpaces()
    {
        using var rejected = JsonDocument.Parse("""{"colorSpaces":["DeviceCMYK","DeviceRGB"]}""");
        var rejectedItems = new List<PublicationPreflightItem>();
        PublicationPackageService.ValidateIngramColorSpaces(rejected.RootElement, rejectedItems);
        Assert.Contains(rejectedItems, item => item.Code == "PDF_RGB_FORBIDDEN");

        using var accepted = JsonDocument.Parse("""{"colorSpaces":["DeviceCMYK","DeviceGray","Separation"]}""");
        var acceptedItems = new List<PublicationPreflightItem>();
        PublicationPackageService.ValidateIngramColorSpaces(accepted.RootElement, acceptedItems);
        Assert.Empty(acceptedItems);
    }

    [Fact]
    public async Task EpubProofsRemainPackageBoundAndRejectPhysicalApproval()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var connectionString = $"Data Source={Path.Combine(directory, "package.db")}";
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
            await using var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
            await db.Database.EnsureCreatedAsync();
            var project = new Project { Name = "Book", Slug = $"book-{Guid.NewGuid():N}" };
            var chapterId = Guid.NewGuid();
            var manuscript = ManuscriptCodec.FromPlainText(chapterId, "Chapter text.", revision: 1);
            var chapter = new Chapter
            {
                Id = chapterId,
                ProjectId = project.Id,
                Title = "Chapter",
                ManuscriptJson = ManuscriptCodec.Serialize(manuscript),
                ManuscriptRevision = manuscript.Revision,
            };
            var edition = new PublicationEdition
            {
                ProjectId = project.Id,
                Name = "EPUB",
                Format = PublicationEditionFormat.Epub,
                Vendor = PublicationVendor.Generic,
                VendorProfileVersion = "preview-1",
                TitleOverride = "Book",
                Author = "Author",
                Language = "en",
                Binding = PublicationBinding.Digital,
                Paper = PublicationPaper.Digital,
                Ink = PublicationInk.Digital,
            };
            db.AddRange(project, chapter, edition);
            db.PublicationEditionOutlineItems.Add(new PublicationEditionOutlineItem
            {
                EditionId = edition.Id,
                TargetKind = PublishOutlineTargetKind.Chapter,
                TargetId = chapter.Id,
                ChapterId = chapter.Id,
                IsIncluded = true,
            });
            await db.SaveChangesAsync();

            var coordinator = new ProjectMutationCoordinator(connectionString);
            var editionService = new PublicationEditionService(db, coordinator, new PublicationActorContext());
            var coverService = new PublicationCoverService(db, coordinator);
            var publishDocument = MinimalEpubDocument(project.Id, edition.Id, chapter, manuscript);
            var epub = new EpubPublishFormatter(new PageGeometryService(db)).Render(publishDocument);
            PublicationPackageService.ValidateEpubStructure(epub);
            var publishing = new FixturePublishService(publishDocument, epub);
            var packages = new PublicationPackageService(
                db,
                publishing,
                editionService,
                coverService,
                coordinator);

            var preflight = await packages.PreflightAsync(project.Id, edition.Id);
            Assert.True(preflight.CanPackage);
            Assert.Equal("Not applicable", preflight.PhysicalProof.Status);
            Assert.Empty(preflight.PhysicalProof.Checklist);
            Assert.Contains(preflight.DigitalProof.Checklist, item =>
                item.Contains("EPUB", StringComparison.Ordinal));
            Assert.DoesNotContain(preflight.DigitalProof.Checklist, item =>
                item.Contains("PDF", StringComparison.Ordinal));
            edition.Isbn = "not-an-isbn";
            await db.SaveChangesAsync();
            var invalidOptionalIsbn = await packages.PreflightAsync(project.Id, edition.Id);
            Assert.Contains(invalidOptionalIsbn.Items, item => item.Code == "META_ISBN13_INVALID");
            edition.Isbn = string.Empty;
            await db.SaveChangesAsync();

            var unsupportedManuscript = ManuscriptCodec.FromPlainText(
                chapter.Id,
                "English and Привет.",
                revision: 2);
            publishing.Document = MinimalEpubDocument(
                project.Id,
                edition.Id,
                chapter,
                unsupportedManuscript);
            var unsupportedScript = await packages.PreflightAsync(project.Id, edition.Id);
            Assert.Contains(unsupportedScript.Items, item => item.Code == "SCRIPT_SCOPE_UNSUPPORTED");
            var unsupportedLanguageMark = ManuscriptCodec.FromPlainText(
                chapter.Id,
                "English text.",
                revision: 3);
            unsupportedLanguageMark.Content[0] = unsupportedLanguageMark.Content[0] with
            {
                Content =
                [
                    new ManuscriptInline
                    {
                        Text = "English text.",
                        Marks = [new ManuscriptMark { Type = ManuscriptMarkType.Language, Value = "ar" }],
                    },
                ],
            };
            publishing.Document = MinimalEpubDocument(
                project.Id,
                edition.Id,
                chapter,
                unsupportedLanguageMark);
            var unsupportedMark = await packages.PreflightAsync(project.Id, edition.Id);
            Assert.Contains(unsupportedMark.Items, item => item.Code == "LANGUAGE_MARK_UNSUPPORTED");
            var figureImageId = Guid.NewGuid();
            var figureManuscript = ManuscriptCodec.FromPlainText(chapter.Id, "Figure caption.", revision: 4);
            figureManuscript.Content[0] = figureManuscript.Content[0] with
            {
                Type = ManuscriptBlockType.Figure,
                ImageId = figureImageId,
                AltText = "\u0418\u043b\u043b\u044e\u0441\u0442\u0440\u0430\u0446\u0438\u044f",
            };
            var figureAsset = new PublishAssetDocument(
                figureImageId,
                "figure.png",
                "image/png",
                "figure"u8.ToArray(),
                "Figure");
            publishing.Document = MinimalEpubDocument(
                project.Id,
                edition.Id,
                chapter,
                figureManuscript) with
            {
                Assets = [figureAsset],
                Sections =
                [
                    publishDocument.Sections[0] with
                    {
                        Chapters =
                        [
                            publishDocument.Sections[0].Chapters[0] with
                            {
                                Manuscript = figureManuscript,
                                IllustrationLayout = new IllustratedProseLayout(
                                [
                                    new IllustratedProseImageBlock(
                                        Guid.NewGuid(),
                                        figureImageId,
                                        ChapterImageAnchorPosition.AfterParagraph,
                                        figureManuscript.Content[0].Id,
                                        50,
                                        ChapterImageAlignment.Center,
                                        "\u041f\u043e\u0434\u043f\u0438\u0441\u044c",
                                        "\u041e\u043f\u0438\u0441\u0430\u043d\u0438\u0435",
                                        0,
                                        false),
                                ]),
                            },
                        ],
                    },
                ],
            };
            var unvalidatedFigureText = await packages.PreflightAsync(project.Id, edition.Id);
            Assert.Contains(unvalidatedFigureText.Items, item =>
                item.Code == "SCRIPT_SCOPE_UNSUPPORTED"
                && item.Message.Contains(figureManuscript.Content[0].Id, StringComparison.Ordinal));
            Assert.Contains(unvalidatedFigureText.Items, item =>
                item.Code == "SCRIPT_SCOPE_UNSUPPORTED"
                && item.Message.Contains("illustration", StringComparison.Ordinal));
            var localizedMatterDocument = ManuscriptCodec.FromPlainText(
                Guid.NewGuid(),
                "Благодарности.",
                revision: 1);
            var localizedMatter = new PublicationMatter
            {
                Id = localizedMatterDocument.ManuscriptId,
                EditionId = edition.Id,
                Location = PublicationMatterLocation.Back,
                Kind = PublicationMatterKind.Acknowledgments,
                Title = "Благодарности",
                ManuscriptJson = ManuscriptCodec.Serialize(localizedMatterDocument),
                Revision = localizedMatterDocument.Revision,
                IsIncluded = true,
            };
            db.PublicationMatter.Add(localizedMatter);
            await db.SaveChangesAsync();
            var localizedAsset = publishDocument.CoverAsset! with { AltText = "Обложка" };
            publishing.Document = publishDocument with
            {
                ProjectName = "Проект",
                CoverAsset = localizedAsset,
                Sections =
                [
                    publishDocument.Sections[0] with { Synopsis = "Краткое содержание." },
                ],
                Placements =
                [
                    new PublicationImagePlacementDocument(
                        Guid.NewGuid(),
                        localizedAsset with { AltText = "Illustration" },
                        PublishOutlineTargetKind.Chapter,
                        chapter.Id,
                        PublicationImagePlacementKind.AfterChapter,
                        "Подпись",
                        0),
                ],
            };
            var renderedTextScope = await packages.PreflightAsync(project.Id, edition.Id);
            Assert.Contains(renderedTextScope.Items, item => item.Message.Contains("project title", StringComparison.Ordinal));
            Assert.Contains(renderedTextScope.Items, item => item.Message.Contains("synopsis", StringComparison.Ordinal));
            Assert.Contains(renderedTextScope.Items, item => item.Message.Contains("placement", StringComparison.Ordinal));
            Assert.Contains(renderedTextScope.Items, item => item.Message.Contains("cover asset", StringComparison.Ordinal));
            Assert.Contains(renderedTextScope.Items, item => item.Message.Contains(localizedMatter.Id.ToString("N"), StringComparison.Ordinal));
            db.PublicationMatter.Remove(localizedMatter);
            await db.SaveChangesAsync();
            publishing.Document = publishDocument;

            var paperback = new PublicationEdition
            {
                ProjectId = project.Id,
                Name = "KDP paperback",
                Format = PublicationEditionFormat.Paperback,
                Vendor = PublicationVendor.AmazonKdp,
                VendorProfileVersion = "preview-1",
                TitleOverride = "Book",
                Author = "Author",
                Language = "en",
                Isbn = string.Empty,
                PageWidthInches = 8.5,
                PageHeightInches = 11,
                Bleed = false,
            };
            db.PublicationEditions.Add(paperback);
            db.PublicationCoverDesigns.Add(new PublicationCoverDesign
            {
                EditionId = paperback.Id,
                Title = "Book",
                Author = "Author",
                SpineText = "Book",
                BackCopy = "Текст на обложке",
                BarcodeMode = PublicationBarcodeMode.VendorOverlay,
            });
            db.PublicationEditionOutlineItems.Add(new PublicationEditionOutlineItem
            {
                EditionId = paperback.Id,
                TargetKind = PublishOutlineTargetKind.Chapter,
                TargetId = chapter.Id,
                ChapterId = chapter.Id,
                IsIncluded = true,
            });
            await db.SaveChangesAsync();
            var blockedPaperback = await packages.PreflightAsync(project.Id, paperback.Id);
            Assert.Contains(blockedPaperback.Items, item => item.Code == "PRINT_TRIM_UNSUPPORTED");
            Assert.Contains(blockedPaperback.Items, item => item.Code == "PRINT_COVER_BLEED_REQUIRED");
            Assert.Contains(blockedPaperback.Items, item =>
                item.Message.Contains("back copy", StringComparison.Ordinal));
            Assert.DoesNotContain(blockedPaperback.Items, item =>
                item.Code is "META_ISBN13_REQUIRED" or "META_ISBN13_BARCODE_REQUIRED");
            Assert.False(blockedPaperback.CanPackage);
            var paperbackCover = await db.PublicationCoverDesigns.SingleAsync(
                design => design.EditionId == paperback.Id);
            paperbackCover.BarcodeMode = PublicationBarcodeMode.LorekeeperBarcode;
            await db.SaveChangesAsync();
            var lorekeeperBarcodeWithoutIsbn = await packages.PreflightAsync(project.Id, paperback.Id);
            Assert.Contains(
                lorekeeperBarcodeWithoutIsbn.Items,
                item => item.Code == "META_ISBN13_BARCODE_REQUIRED");
            paperbackCover.BarcodeMode = PublicationBarcodeMode.VendorOverlay;
            paperback.Vendor = PublicationVendor.IngramSpark;
            await db.SaveChangesAsync();
            var ingramWithoutIsbn = await packages.PreflightAsync(project.Id, paperback.Id);
            Assert.Contains(ingramWithoutIsbn.Items, item => item.Code == "META_ISBN13_REQUIRED");

            publishing.OnNextExportAsync = async () =>
            {
                edition.TitleOverride = "Changed during package build";
                await db.SaveChangesAsync();
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                packages.BuildAsync(project.Id, edition.Id));
            Assert.Empty(await db.PublicationArtifacts.AsNoTracking().ToListAsync());

            var built = await packages.BuildAsync(project.Id, edition.Id);
            var package = Assert.Single(
                built.Artifacts,
                artifact => artifact.Kind == PublicationArtifactKind.PublicationPackage);
            var repeated = await packages.BuildAsync(project.Id, edition.Id);
            var repeatedPackage = Assert.Single(
                repeated.Artifacts,
                artifact => artifact.Kind == PublicationArtifactKind.PublicationPackage);
            Assert.Equal(package.Sha256, repeatedPackage.Sha256);
            var packageBytes = await db.PublicationArtifacts.AsNoTracking()
                .Where(artifact => artifact.Id == package.Id || artifact.Id == repeatedPackage.Id)
                .OrderBy(artifact => artifact.Id)
                .Select(artifact => artifact.Data)
                .ToListAsync();
            Assert.Equal(2, packageBytes.Count);
            Assert.Equal(packageBytes[0], packageBytes[1]);
            package = repeatedPackage;

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                packages.RecordProofAsync(
                    project.Id,
                    edition.Id,
                    package.Id,
                    PublicationProofKind.Physical,
                    "Printed proof inspected."));

            var digital = await packages.RecordProofAsync(
                project.Id,
                edition.Id,
                package.Id,
                PublicationProofKind.Digital,
                string.Empty);
            Assert.Equal("Recorded", digital.DigitalProof.Status);
            Assert.Equal("Not applicable", digital.PhysicalProof.Status);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                packages.RecordProofAsync(
                    project.Id,
                    edition.Id,
                    package.Id,
                    PublicationProofKind.Physical,
                    "Printed proof inspected."));

            var replacementData = "different package"u8.ToArray();
            db.PublicationArtifacts.Add(new PublicationArtifact
            {
                EditionId = edition.Id,
                Kind = PublicationArtifactKind.PublicationPackage,
                FileName = "replacement.zip",
                MediaType = "application/zip",
                Data = replacementData,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(replacementData)),
                ByteLength = replacementData.Length,
                SourceFingerprint = digital.SourceFingerprint,
                RendererVersion = package.RendererVersion,
                ProfileId = package.ProfileId,
                CreatedAt = DateTime.UtcNow.AddSeconds(1),
            });
            await db.SaveChangesAsync();

            var replaced = await packages.PreflightAsync(project.Id, edition.Id);
            Assert.NotEqual(package.Id, replaced.CurrentPackage?.Id);
            Assert.Equal("Pending", replaced.DigitalProof.Status);
            Assert.Equal("Not applicable", replaced.PhysicalProof.Status);

            edition.VendorProfileVersion = "preview-2";
            await db.SaveChangesAsync();
            var upgraded = await packages.PreflightAsync(project.Id, edition.Id);
            Assert.Contains(upgraded.Items, item => item.Code == "PROFILE_VERSION_UNSUPPORTED");
            Assert.Null(upgraded.CurrentPackage);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PublishAssistantCannotApproveProofs()
    {
        var tools = new PublishAssistantTools(
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);

        var catalog = await tools.BuildAsync(new PublishAssistantContext(Guid.NewGuid()));

        Assert.Contains(catalog, tool => tool.Name == "list_publication_named_styles");
        Assert.Contains(catalog, tool => tool.Name == "list_publication_project_images");
        Assert.DoesNotContain(catalog, tool =>
            tool.Name.Contains("proof", StringComparison.OrdinalIgnoreCase)
            && (tool.Name.Contains("approve", StringComparison.OrdinalIgnoreCase)
                || tool.Name.Contains("record", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task NewerRenderPairAfterPreflightBlocksPackagePersistence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var connectionString = $"Data Source={Path.Combine(directory, "render-drift.db")}";
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
            await using var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
            await db.Database.EnsureCreatedAsync();
            var project = new Project { Name = "Print Book", Slug = $"print-{Guid.NewGuid():N}" };
            var chapterId = Guid.NewGuid();
            var manuscript = ManuscriptCodec.FromPlainText(chapterId, "Chapter text.", revision: 1);
            var chapter = new Chapter
            {
                Id = chapterId,
                ProjectId = project.Id,
                Title = "Chapter",
                ManuscriptJson = ManuscriptCodec.Serialize(manuscript),
                ManuscriptRevision = manuscript.Revision,
            };
            var edition = new PublicationEdition
            {
                ProjectId = project.Id,
                Name = "KDP paperback",
                Format = PublicationEditionFormat.Paperback,
                Vendor = PublicationVendor.AmazonKdp,
                VendorProfileVersion = "preview-1",
                TitleOverride = "Print Book",
                Author = "Author",
                Language = "en",
                Binding = PublicationBinding.PerfectBound,
                Paper = PublicationPaper.White,
                Ink = PublicationInk.BlackAndWhite,
                Bleed = true,
                PageWidthInches = 6,
                PageHeightInches = 9,
                PageMarginInches = 0.75,
                BodyFontSizePoints = 11,
                BodyLineHeight = 1.4,
            };
            var coverDesign = new PublicationCoverDesign
            {
                EditionId = edition.Id,
                Title = edition.TitleOverride,
                Author = edition.Author,
                SpineText = edition.TitleOverride,
                BarcodeMode = PublicationBarcodeMode.VendorOverlay,
            };
            db.AddRange(project, chapter, edition, coverDesign);
            db.PublicationEditionOutlineItems.Add(new PublicationEditionOutlineItem
            {
                EditionId = edition.Id,
                TargetKind = PublishOutlineTargetKind.Chapter,
                TargetId = chapter.Id,
                ChapterId = chapter.Id,
                IsIncluded = true,
            });
            await db.SaveChangesAsync();

            var coordinator = new ProjectMutationCoordinator(connectionString);
            var editionService = new PublicationEditionService(db, coordinator, new PublicationActorContext());
            var coverService = new PublicationCoverService(db, coordinator);
            var fingerprint = await editionService.GetSourceFingerprintAsync(project.Id, edition.Id);
            var firstJob = AddRenderPair(
                db,
                edition.Id,
                fingerprint,
                "%PDF-first-interior"u8.ToArray(),
                "%PDF-first-cover"u8.ToArray());
            await db.SaveChangesAsync();
            var template = (await coverService.GetAsync(project.Id, edition.Id)).Template;
            coverDesign.AcknowledgedTemplateFingerprint = template.Fingerprint;
            firstJob.EvidenceJson = PressEvidence(template);
            await db.SaveChangesAsync();

            var document = MinimalEpubDocument(project.Id, edition.Id, chapter, manuscript) with
            {
                Profile = MinimalEpubDocument(project.Id, edition.Id, chapter, manuscript).Profile with
                {
                    TitleOverride = edition.TitleOverride,
                    Author = edition.Author,
                    PageWidthInches = 6,
                    PageHeightInches = 9,
                    PageMarginInches = 0.75,
                    BodyFontSizePoints = 11,
                    BodyLineHeight = 1.4,
                },
            };
            var epub = new EpubPublishFormatter(new PageGeometryService(db)).Render(document);
            var publishing = new FixturePublishService(document, epub);
            var packages = new PublicationPackageService(
                db,
                publishing,
                editionService,
                coverService,
                coordinator);
            var preflight = await packages.PreflightAsync(project.Id, edition.Id);
            Assert.True(preflight.CanPackage);
            Assert.Equal(2, preflight.ValidatedArtifacts.Count);
            Assert.Contains(preflight.DigitalProof.Checklist, item =>
                item.Contains("PDF", StringComparison.Ordinal));
            Assert.DoesNotContain(preflight.DigitalProof.Checklist, item =>
                item.Contains("EPUB", StringComparison.Ordinal));

            var built = await packages.BuildAsync(project.Id, edition.Id);
            Assert.Equal(0, publishing.ExportCallCount);
            Assert.DoesNotContain(built.Artifacts, artifact => artifact.Kind == PublicationArtifactKind.Epub);
            var packageArtifact = Assert.Single(
                await db.PublicationArtifacts.AsNoTracking()
                    .Where(artifact => artifact.Id == built.Preflight.CurrentPackage!.Id)
                    .ToListAsync());
            using (var archive = new ZipArchive(new MemoryStream(packageArtifact.Data), ZipArchiveMode.Read))
            {
                Assert.Contains(archive.Entries, entry => entry.FullName == "interior.pdf");
                Assert.Contains(archive.Entries, entry => entry.FullName == "cover.pdf");
                Assert.DoesNotContain(archive.Entries, entry => entry.FullName == "book.epub");
            }
            var packageCount = await db.PublicationArtifacts.CountAsync(
                artifact => artifact.Kind == PublicationArtifactKind.PublicationPackage);
            publishing.DocumentCallsBeforeCallback = 1;
            publishing.OnNextDocumentAsync = async () =>
            {
                var newerJob = AddRenderPair(
                    db,
                    edition.Id,
                    fingerprint,
                    "%PDF-newer-interior"u8.ToArray(),
                    "%PDF-newer-cover"u8.ToArray(),
                    DateTime.UtcNow.AddMinutes(1));
                newerJob.EvidenceJson = PressEvidence(template);
                await db.SaveChangesAsync();
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                packages.BuildAsync(project.Id, edition.Id));
            Assert.Equal(
                packageCount,
                await db.PublicationArtifacts.CountAsync(
                    artifact => artifact.Kind == PublicationArtifactKind.PublicationPackage));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static PublicationRenderJob AddRenderPair(
        AppDbContext db,
        Guid editionId,
        string fingerprint,
        byte[] interiorData,
        byte[] coverData,
        DateTime? createdAt = null)
    {
        var job = new PublicationRenderJob
        {
            EditionId = editionId,
            Status = PublicationRenderStatus.Completed,
            SourceFingerprint = fingerprint,
            RendererVersion = "0.2.0",
            ProfileId = "kdp-paperback-6x9-preview-v1",
            DiagnosticsJson = "[]",
            ProgressPercent = 100,
            ProgressMessage = "Complete",
            CreatedAt = createdAt ?? DateTime.UtcNow,
            CompletedAt = createdAt ?? DateTime.UtcNow,
        };
        db.PublicationRenderJobs.Add(job);
        db.PublicationArtifacts.AddRange(
            PdfArtifact(job, PublicationArtifactKind.InteriorPdf, "interior.pdf", interiorData, 24),
            PdfArtifact(job, PublicationArtifactKind.CoverPdf, "cover.pdf", coverData, 1));
        return job;
    }

    private static PublicationArtifact PdfArtifact(
        PublicationRenderJob job,
        PublicationArtifactKind kind,
        string fileName,
        byte[] data,
        int pageCount) => new()
    {
        EditionId = job.EditionId,
        RenderJobId = job.Id,
        Kind = kind,
        FileName = fileName,
        MediaType = "application/pdf",
        Data = data,
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(data)),
        ByteLength = data.Length,
        PageCount = pageCount,
        SourceFingerprint = job.SourceFingerprint,
        RendererVersion = job.RendererVersion,
        ProfileId = job.ProfileId,
        CreatedAt = job.CreatedAt,
    };

    private static string PressEvidence(PublicationCoverTemplate template) =>
        JsonSerializer.Serialize(new
        {
            pdfVersion = "1.7",
            interiorWidthPoints = 432,
            interiorHeightPoints = 648,
            coverWidthPoints = template.FullWidthInches * 72,
            coverHeightPoints = template.FullHeightInches * 72,
            interiorPageBoxesConsistent = true,
            coverPageBoxesConsistent = true,
            fonts = new[] { new { name = "Test", embedded = true } },
            colorSpaces = new[] { "DeviceGray" },
            imageCount = 0,
            annotationCount = 0,
            outputIntentCount = 0,
            hasTransparency = false,
            hasEncryption = false,
            hasForbiddenActions = false,
        });

    private static PublishDocument MinimalEpubDocument(
        Guid projectId,
        Guid editionId,
        Chapter chapter,
        ManuscriptDocument manuscript)
    {
        var cover = new PublishAssetDocument(
            Guid.NewGuid(),
            "cover.png",
            "image/png",
            "cover"u8.ToArray(),
            "Book cover");
        var profile = new PublishDocumentProfile(
            "Book",
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
            true,
            PrintPicturePageSpreadMode.WholeSpread,
            EpubPicturePageSpreadMode.RequestLandscape,
            6,
            9,
            0.75,
            11,
            1.4);
        var chapterDocument = new PublishChapterDocument(
            chapter.Id,
            null,
            chapter.Title,
            chapter.PlainText,
            string.Empty,
            0,
            true,
            ChapterVisualMode.Prose,
            ChapterPageLayoutKind.SinglePortrait,
            new IllustratedProseLayout([]),
            new PicturePageLayout([], []),
            manuscript);
        return new(
            editionId,
            projectId,
            "Book",
            "book",
            DateTime.UtcNow,
            profile,
            cover,
            [new PublishSectionDocument(null, "Unassigned", string.Empty, true, false, false, 0, [chapterDocument])],
            [cover],
            []);
    }

    private static byte[] BuildEpub(
        bool includeNavigation,
        string modified = "2026-07-30T00:00:00Z",
        string rootfile = "OEBPS/package.opf",
        string chapterHref = "chapter.xhtml",
        string spineIdRef = "chapter",
        string navigationHref = "chapter.xhtml",
        bool includeLandmarks = true,
        string chapterContent = """<html xmlns="http://www.w3.org/1999/xhtml"><body><p>Text</p></body></html>""")
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Write(
                archive,
                "META-INF/container.xml",
                $"""
                 <?xml version="1.0"?>
                 <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                   <rootfiles>
                     <rootfile full-path="{rootfile}" media-type="application/oebps-package+xml"/>
                   </rootfiles>
                 </container>
                 """,
                CompressionLevel.Optimal);
            var navigationManifest = includeNavigation
                ? """<item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>"""
                : string.Empty;
            Write(
                archive,
                "OEBPS/package.opf",
                $"""
                 <?xml version="1.0"?>
                 <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="book-id">
                   <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                     <dc:identifier id="book-id">urn:uuid:test</dc:identifier>
                     <dc:title>Test Book</dc:title>
                     <dc:language>en</dc:language>
                     <meta property="dcterms:modified">{modified}</meta>
                   </metadata>
                   <manifest>
                     {navigationManifest}
                     <item id="chapter" href="{chapterHref}" media-type="application/xhtml+xml"/>
                   </manifest>
                   <spine><itemref idref="{spineIdRef}"/></spine>
                 </package>
                 """,
                CompressionLevel.Optimal);
            if (includeNavigation)
            {
                var landmarks = includeLandmarks
                    ? $"""<nav epub:type="landmarks"><ol><li><a epub:type="bodymatter" href="{navigationHref}">Start</a></li></ol></nav>"""
                    : string.Empty;
                Write(
                    archive,
                    "OEBPS/nav.xhtml",
                    $"""
                    <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
                      <body>
                        <nav epub:type="toc"><ol><li><a href="{navigationHref}">Chapter</a></li></ol></nav>
                        {landmarks}
                      </body>
                    </html>
                    """,
                    CompressionLevel.Optimal);
            }
            Write(
                archive,
                "OEBPS/chapter.xhtml",
                chapterContent,
                CompressionLevel.Optimal);
        }
        return output.ToArray();
    }

    private static void Write(ZipArchive archive, string path, string content, CompressionLevel compression)
    {
        var entry = archive.CreateEntry(path, compression);
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    private sealed class FixturePublishService(PublishDocument document, byte[] epub) : IPublishService
    {
        public Func<Task>? OnNextExportAsync { get; set; }
        public Func<Task>? OnNextDocumentAsync { get; set; }
        public int DocumentCallsBeforeCallback { get; set; }
        public int ExportCallCount { get; private set; }
        public PublishDocument Document { get; set; } = document;

        public Task<PublishWorkspaceView> GetWorkspaceAsync(
            Guid projectId,
            Guid editionId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<PublishDocument> GetDocumentAsync(
            Guid projectId,
            Guid editionId,
            CancellationToken cancellationToken = default)
        {
            if (DocumentCallsBeforeCallback > 0)
            {
                DocumentCallsBeforeCallback--;
            }
            else if (OnNextDocumentAsync is { } callback)
            {
                OnNextDocumentAsync = null;
                await callback();
            }
            return Document;
        }

        public Task<PublishDocument> GetPrintDocumentAsync(
            Guid projectId,
            Guid editionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Document);

        public async Task<ProjectExportFile> ExportAsync(
            Guid projectId,
            Guid editionId,
            PublishExportFormat format,
            CancellationToken cancellationToken = default)
        {
            ExportCallCount++;
            if (OnNextExportAsync is { } callback)
            {
                OnNextExportAsync = null;
                await callback();
            }
            return new ProjectExportFile("book.epub", "application/epub+zip", epub);
        }
    }
}
