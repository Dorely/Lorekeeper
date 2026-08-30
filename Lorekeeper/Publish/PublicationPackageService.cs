using System.Data;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Lorekeeper.Composition;
using Lorekeeper.ImportExport;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Publish;

public sealed record PublicationPreflightItem(
    string Severity,
    string Code,
    string Message,
    PublicationArtifactKind? ArtifactKind = null,
    int? Page = null,
    string? SourceKind = null,
    string? SourceId = null);

public sealed record PublicationPreflightReport(
    string ProfileId,
    string ProfileVersion,
    DateTime ProfileReviewedAtUtc,
    IReadOnlyList<string> ProfileSources,
    string SourceFingerprint,
    string PackageIdentity,
    IReadOnlyList<PublicationValidatedArtifact> ValidatedArtifacts,
    bool IsLorekeeperValidated,
    bool CanPackage,
    PublicationArtifactView? CurrentPackage,
    IReadOnlyList<PublicationPreflightItem> Items)
{
    [JsonIgnore]
    internal IReadOnlyList<Guid> ValidatedArtifactIds { get; init; } = [];
}

public sealed record PublicationValidatedArtifact(
    PublicationArtifactKind Kind,
    string Sha256,
    long ByteLength,
    int? PageCount,
    string RendererVersion,
    string ProfileId);

public sealed record PublicationPackageResult(
    PublicationPreflightReport Preflight,
    IReadOnlyList<PublicationArtifactView> Artifacts);

public interface IPublicationPackageService
{
    Task<PublicationPreflightReport> PreflightAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken = default);
    Task<PublicationPackageResult> BuildAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken = default);
    Task<PublicationPackageResult> BuildFromPreflightAsync(Guid projectId, Guid editionId, PublicationPreflightReport report, CancellationToken cancellationToken = default);
}

public sealed class PublicationPackageService(
    IAppDatabaseOperationFactory database,
    IPublishService publishing,
    IPublicationEditionService editions,
    IPublicationCoverService covers,
    IPublicationEffectiveConfigurationResolver effectiveConfigurations,
    IPrintArtifactProfileRegistry printArtifactProfiles,
    IPrintGeometryService printGeometry,
    IPublicationPressRuntime? pressRuntime = null) : IPublicationPackageService
{
    private const string AssemblerVersion = "lorekeeper-package-v1";
    private const string EpubExporterVersion = "lorekeeper-epub-v3";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private static readonly IReadOnlyList<PublicationPreflightProfile> Profiles =
    [
        new(
            PublicationEditionFormat.Epub,
            PublicationVendor.AmazonKdp,
            "amazon-kdp-epub",
            "epub3-v1",
            new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc),
            0,
            0,
            [
                "https://kdp.amazon.com/en_US/help/topic/G200634390/",
                "https://www.w3.org/TR/epub-33/",
            ]),
        new(
            PublicationEditionFormat.DigitalPdf,
            PublicationVendor.Generic,
            "generic-digital-pdf",
            "generic-digital-pdf-v1",
            new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            1,
            5000,
            ["https://www.adobe.com/accessibility/pdf/pdf-accessibility-overview.html"]),
        new(
            PublicationEditionFormat.DigitalPdf,
            PublicationVendor.AmazonKdp,
            "generic-digital-pdf",
            "generic-digital-pdf-v1",
            new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            1,
            5000,
            ["https://www.adobe.com/accessibility/pdf/pdf-accessibility-overview.html"]),
        new(
            PublicationEditionFormat.DigitalPdf,
            PublicationVendor.IngramSpark,
            "generic-digital-pdf",
            "generic-digital-pdf-v1",
            new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            1,
            5000,
            ["https://www.adobe.com/accessibility/pdf/pdf-accessibility-overview.html"]),
        new(
            PublicationEditionFormat.Epub,
            PublicationVendor.Generic,
            "generic-epub3",
            "epub3-v1",
            new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc),
            0,
            0,
            [
                "https://www.w3.org/TR/epub-33/",
                "https://www.w3.org/TR/epub-a11y-11/",
            ]),
        new(
            PublicationEditionFormat.Epub,
            PublicationVendor.IngramSpark,
            "generic-epub3",
            "epub3-v1",
            new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc),
            0,
            0,
            [
                "https://www.w3.org/TR/epub-33/",
                "https://www.w3.org/TR/epub-a11y-11/",
            ]),
    ];

    public async Task<PublicationPreflightReport> PreflightAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var edition = (await effectiveConfigurations.ResolveReleaseAsync(projectId, editionId, cancellationToken)).Edition;
        edition.Language = PublicationLanguage.Normalize(edition.Language);
        var fingerprint = await editions.GetSourceFingerprintAsync(projectId, editionId, cancellationToken);
        var artifacts = await db.PublicationArtifacts.AsNoTracking()
            .Where(artifact => artifact.EditionId == editionId
                && (artifact.Kind == PublicationArtifactKind.InteriorPdf
                    || artifact.Kind == PublicationArtifactKind.PerfectBoundCoverPdf
                    || artifact.Kind == PublicationArtifactKind.CaseCoverPdf
                    || artifact.Kind == PublicationArtifactKind.DustJacketPdf
                    || artifact.Kind == PublicationArtifactKind.FrontCoverPdf
                    || artifact.Kind == PublicationArtifactKind.BackCoverPdf
                    || artifact.Kind == PublicationArtifactKind.PrintSetupManifest
                    || artifact.Kind == PublicationArtifactKind.BookPdf))
            .OrderByDescending(artifact => artifact.CreatedAt)
            .ToListAsync(cancellationToken);
        var interior = artifacts.FirstOrDefault(artifact => artifact.Kind == PublicationArtifactKind.InteriorPdf);
        var physicalArtifacts = new List<PublicationArtifact>();
        var bookArtifact = artifacts.FirstOrDefault(artifact => artifact.Kind == PublicationArtifactKind.BookPdf);
        var items = new List<PublicationPreflightItem>();
        var profile = ResolveProfile(edition);
        if (!string.Equals(edition.VendorProfileVersion, profile.Version, StringComparison.Ordinal))
        {
            items.Add(Error(
                "PROFILE_VERSION_UNSUPPORTED",
                $"Profile version '{edition.VendorProfileVersion}' is not installed; select '{profile.Version}'."));
        }
        if (string.IsNullOrWhiteSpace(edition.TitleOverride))
            items.Add(Error("META_TITLE_REQUIRED", "A publication title is required."));
        if (string.IsNullOrWhiteSpace(edition.Author))
            items.Add(Error("META_AUTHOR_REQUIRED", "An author is required."));
        if (string.IsNullOrWhiteSpace(edition.Language))
            items.Add(Error("META_LANGUAGE_REQUIRED", "A language code is required."));
        if (!string.IsNullOrWhiteSpace(edition.Isbn) && !PublicationIsbn.IsValidIsbn13(edition.Isbn))
            items.Add(Error("META_ISBN13_INVALID", "The supplied ISBN must be a valid ISBN-13."));
        PublicationCoverDesignView? coverDesign = null;
        if (edition.Format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover)
        {
            string? currentRendererVersion = null;
            if (pressRuntime is not null)
            {
                var readiness = pressRuntime.GetReadiness();
                if (!readiness.IsReady)
                    items.Add(Error("PRESS_RUNTIME_UNAVAILABLE", readiness.Message));
                else
                {
                    var description = pressRuntime.GetDescription();
                    currentRendererVersion = description.RendererVersion;
                    if (!description.Profiles.Contains(edition.VendorProfileVersion, StringComparer.Ordinal))
                        items.Add(Error("PRESS_PROFILE_STALE", "The selected profile is not available in the installed Lorekeeper Press runtime."));
                }
            }
            ValidatePdf(interior, fingerprint, currentRendererVersion, edition.VendorProfileVersion, PublicationArtifactKind.InteriorPdf, "INTERIOR", items);
            PrintArtifactProfile product;
            try
            {
                product = printArtifactProfiles.GetRequired(edition.PrintArtifactProfileKey);
            }
            catch (InvalidOperationException exception)
            {
                items.Add(Error("PRINT_PRODUCT_UNAVAILABLE", exception.Message));
                product = null!;
            }
            if (product is not null)
            {
                var requiredKinds = RequiredArtifactKinds(product, edition);
                physicalArtifacts = requiredKinds
                    .Select(kind => artifacts.FirstOrDefault(item => item.Kind == kind))
                    .Where(item => item is not null)
                    .Cast<PublicationArtifact>()
                    .ToList();
                foreach (var requiredKind in requiredKinds)
                {
                    var artifact = artifacts.FirstOrDefault(item => item.Kind == requiredKind);
                    if (requiredKind == PublicationArtifactKind.PrintSetupManifest)
                    {
                        ValidateStoredArtifact(artifact, fingerprint, currentRendererVersion, edition.VendorProfileVersion, requiredKind, "CLOTH_SETUP", items);
                        continue;
                    }
                    ValidatePdf(artifact, fingerprint, currentRendererVersion, edition.VendorProfileVersion, requiredKind, requiredKind.ToString().ToUpperInvariant(), items);
                    var expectedPages = requiredKind == PublicationArtifactKind.PerfectBoundCoverPdf
                        && edition.PrintCoverMode == PrintCoverMode.Duplex ? 2 : 1;
                    if (artifact is not null && artifact.PageCount != expectedPages)
                        items.Add(Error("COVER_PAGE_COUNT_INVALID", $"The {requiredKind} must contain exactly {expectedPages} page(s).", requiredKind));
                }
            }
            if (interior?.IsLegacy == true || physicalArtifacts.Any(item => item.IsLegacy))
            {
                items.Add(Error(
                    "PRESS_RENDERER_LEGACY",
                    "Legacy PDFs remain downloadable but cannot satisfy current validation or package readiness."));
            }
            coverDesign = await covers.GetAsync(projectId, editionId, cancellationToken);
            items.AddRange(StructuredCoverDiagnostics(coverDesign).Select(diagnostic =>
                new PublicationPreflightItem(
                    diagnostic.Severity,
                    diagnostic.Code,
                    diagnostic.Message,
                    PublicationArtifactKind.PerfectBoundCoverPdf)));
            if (!coverDesign.Template.IsAcknowledged)
            {
                items.Add(new PublicationPreflightItem(
                    "warning",
                    "COVER_TEMPLATE_REVIEW_RECOMMENDED",
                    "Review the calculated full-wrap cover template before preparing print files.",
                    PublicationArtifactKind.PerfectBoundCoverPdf));
            }
            if (edition.Vendor == PublicationVendor.IngramSpark
                && !PublicationIsbn.IsValidIsbn13(edition.Isbn))
            {
                items.Add(Error("META_ISBN13_REQUIRED", "The Ingram profile requires a valid ISBN-13."));
            }
            if (coverDesign.BarcodeMode == PublicationBarcodeMode.LorekeeperBarcode
                && !PublicationIsbn.IsValidIsbn13(edition.Isbn))
            {
                items.Add(Error("META_ISBN13_BARCODE_REQUIRED", "Lorekeeper barcode output requires a valid ISBN-13."));
            }
            if (edition.Vendor == PublicationVendor.BarnesAndNoblePress)
            {
                if (coverDesign.BarcodeMode != PublicationBarcodeMode.VendorOverlay)
                    items.Add(Error("COVER_BARCODE_VENDOR_OVERLAY_REQUIRED", "B&N Press generates the SKU or ISBN barcode; remove uploaded barcode artwork and keep the reserved area clear."));
            }
            await AddPressEvidenceAsync(edition, interior, physicalArtifacts, coverDesign.Template, currentRendererVersion, items, cancellationToken);
        }
        else if (edition.Format == PublicationEditionFormat.DigitalPdf)
        {
            string? currentRendererVersion = null;
            if (pressRuntime is not null)
            {
                var readiness = pressRuntime.GetReadiness();
                if (!readiness.IsReady)
                    items.Add(Error("PRESS_RUNTIME_UNAVAILABLE", readiness.Message));
                else
                {
                    var description = pressRuntime.GetDescription();
                    currentRendererVersion = description.RendererVersion;
                    if (!description.Profiles.Contains(edition.VendorProfileVersion, StringComparer.Ordinal))
                        items.Add(Error("PRESS_PROFILE_STALE", "The selected profile is not available in the installed Lorekeeper Press runtime."));
                }
            }
            ValidatePdf(bookArtifact, fingerprint, currentRendererVersion, edition.VendorProfileVersion, PublicationArtifactKind.BookPdf, "BOOK", items);
            if (bookArtifact?.IsLegacy == true)
                items.Add(Error("PRESS_RENDERER_LEGACY", "A legacy book PDF cannot satisfy current validation or package readiness."));
            coverDesign = await covers.GetAsync(projectId, editionId, cancellationToken);
            items.AddRange(StructuredCoverDiagnostics(coverDesign).Select(diagnostic => new PublicationPreflightItem(
                diagnostic.Severity,
                diagnostic.Code,
                diagnostic.Message,
                PublicationArtifactKind.BookPdf)));
            await AddDigitalPressEvidenceAsync(edition, bookArtifact, currentRendererVersion, items, cancellationToken);
        }
        var document = await publishing.GetDocumentAsync(projectId, editionId, cancellationToken);
        ValidateProfile(edition, profile, interior ?? bookArtifact, document, coverDesign, items);
        ValidateLanguageScope(document, coverDesign, items);
        if (!string.Equals(edition.Language, "en", StringComparison.OrdinalIgnoreCase)
            && !edition.Language.StartsWith("en-", StringComparison.OrdinalIgnoreCase))
            items.Add(Error("LANGUAGE_SCOPE_UNSUPPORTED", "Lorekeeper Press currently supports English/Latin left-to-right publishing only."));
        items.Add(new(
            "info",
            "LOREKEEPER_VALIDATED_SCOPE",
            "Lorekeeper validation covers the generated file structure and selected profile; vendor acceptance occurs only after upload to the selected service."));
        var primaryPdf = edition.Format == PublicationEditionFormat.DigitalPdf ? bookArtifact : interior;
        var packageIdentity = PackageIdentity(profile, fingerprint, primaryPdf, physicalArtifacts);
        var currentPackageEntity = await db.PublicationArtifacts.AsNoTracking()
            .Where(artifact => artifact.EditionId == editionId
                && artifact.Kind == PublicationArtifactKind.PublicationPackage
                && !artifact.IsLegacy
                && artifact.SourceFingerprint == fingerprint
                && artifact.RendererVersion == PackageRuntimeVersion(profile.Format)
                && artifact.ProfileId == packageIdentity)
            .OrderByDescending(artifact => artifact.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (currentPackageEntity is not null && !ArtifactBytesMatch(currentPackageEntity))
        {
            items.Add(Error("PACKAGE_ARTIFACT_INVALID", "The stored publication package failed its length or SHA-256 check."));
            currentPackageEntity = null;
        }
        var currentPackage = currentPackageEntity is null ? null : ArtifactView(currentPackageEntity, false);
        if (items.Any(item => item.Code is
                "PRESS_RENDERER_STALE" or
                "PRESS_PROFILE_STALE" or
                "PRESS_RUNTIME_UNAVAILABLE"))
        {
            currentPackage = null;
        }
        var finalFingerprint = await editions.GetSourceFingerprintAsync(projectId, editionId, cancellationToken);
        if (!string.Equals(finalFingerprint, fingerprint, StringComparison.Ordinal))
        {
            items.Add(Error("PREFLIGHT_SOURCE_CHANGED", "The edition changed during preflight. Run it again."));
            currentPackage = null;
        }
        return new(
            profile.Id,
            profile.Version,
            profile.ReviewedAtUtc,
            profile.Sources,
            fingerprint,
            packageIdentity,
            edition.Format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover or PublicationEditionFormat.DigitalPdf
                ? artifacts.Where(artifact => artifact.Id == primaryPdf?.Id || physicalArtifacts.Any(item => item.Id == artifact.Id))
                    .Select(artifact => new PublicationValidatedArtifact(
                        artifact.Kind,
                        artifact.Sha256,
                        artifact.ByteLength,
                        artifact.PageCount,
                        artifact.RendererVersion,
                        artifact.ProfileId))
                    .OrderBy(artifact => artifact.Kind)
                    .ToList()
                : [],
            IsLorekeeperValidated: items.All(item => item.Severity != "error"),
            CanPackage: items.All(item => item.Severity != "error"),
            currentPackage,
            items)
        {
            ValidatedArtifactIds = edition.Format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover or PublicationEditionFormat.DigitalPdf
                ? new[] { primaryPdf?.Id ?? Guid.Empty }.Concat(physicalArtifacts.Select(item => item.Id))
                    .Where(id => id != Guid.Empty)
                    .ToList()
                : [],
        };
    }

    public async Task<PublicationPackageResult> BuildAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken = default)
    {
        var report = await PreflightAsync(projectId, editionId, cancellationToken);
        return await BuildFromPreflightAsync(projectId, editionId, report, cancellationToken);
    }

    public async Task<PublicationPackageResult> BuildFromPreflightAsync(
        Guid projectId,
        Guid editionId,
        PublicationPreflightReport report,
        CancellationToken cancellationToken = default)
    {
        if (!report.CanPackage)
            throw new InvalidOperationException("Publication package is blocked by preflight errors.");
        PublicationEdition edition;
        List<PublicationArtifact> sourceArtifacts;
        await using (var readOperation = await database.OpenReadAsync(cancellationToken))
        {
            var readDb = readOperation.Db;
            edition = await readDb.PublicationEditions.AsNoTracking().SingleAsync(
                candidate => candidate.Id == editionId && candidate.ProjectId == projectId,
                cancellationToken);
            PublicationEditionService.EnsureDraft(edition);
            var validatedArtifactIds = report.ValidatedArtifactIds;
            sourceArtifacts = await readDb.PublicationArtifacts.AsNoTracking()
                .Where(artifact => artifact.EditionId == editionId
                    && !artifact.IsLegacy
                    && validatedArtifactIds.Contains(artifact.Id))
                .ToListAsync(cancellationToken);
            if (sourceArtifacts.Count != validatedArtifactIds.Count
                || sourceArtifacts.Any(artifact => !ArtifactBytesMatch(artifact)))
            {
                throw new InvalidOperationException("A preflighted press artifact is missing or failed its hash check.");
            }
        }
        var interiorArtifact = sourceArtifacts.FirstOrDefault(artifact => artifact.Kind == PublicationArtifactKind.InteriorPdf);
        var physicalArtifacts = sourceArtifacts.Where(artifact => artifact.Kind is
            PublicationArtifactKind.PerfectBoundCoverPdf or PublicationArtifactKind.CaseCoverPdf
            or PublicationArtifactKind.DustJacketPdf or PublicationArtifactKind.FrontCoverPdf
            or PublicationArtifactKind.BackCoverPdf or PublicationArtifactKind.PrintSetupManifest).ToList();
        var bookArtifact = sourceArtifacts.FirstOrDefault(artifact => artifact.Kind == PublicationArtifactKind.BookPdf);
        var profile = ResolveProfile(edition);
        var primaryPdf = edition.Format == PublicationEditionFormat.DigitalPdf ? bookArtifact : interiorArtifact;
        var packageIdentity = PackageIdentity(profile, report.SourceFingerprint, primaryPdf, physicalArtifacts);
        if (!string.Equals(packageIdentity, report.PackageIdentity, StringComparison.Ordinal))
            throw new InvalidOperationException("The press artifacts changed after preflight. Run preflight again.");
        var files = new Dictionary<string, (PublicationArtifactKind Kind, string MediaType, byte[] Data)>(
            StringComparer.Ordinal);
        if (edition.Format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover)
        {
            files["interior.pdf"] = (PublicationArtifactKind.InteriorPdf, "application/pdf", interiorArtifact!.Data);
            foreach (var artifact in physicalArtifacts)
            {
                var fileName = artifact.Kind switch
                {
                    PublicationArtifactKind.PerfectBoundCoverPdf => edition.PrintCoverMode == PrintCoverMode.Duplex ? "duplex-cover.pdf" : "outside-cover.pdf",
                    PublicationArtifactKind.CaseCoverPdf => "case-wrap.pdf",
                    PublicationArtifactKind.DustJacketPdf => "dust-jacket.pdf",
                    PublicationArtifactKind.FrontCoverPdf => "front-cover.pdf",
                    PublicationArtifactKind.BackCoverPdf => "back-cover.pdf",
                    PublicationArtifactKind.PrintSetupManifest => edition.Vendor == PublicationVendor.BarnesAndNoblePress ? "print-setup.json" : "cloth-setup.json",
                    _ => throw new InvalidOperationException("Unexpected print artifact."),
                };
                files[fileName] = (artifact.Kind, artifact.MediaType, artifact.Data);
            }
            var uploadMap = JsonSerializer.SerializeToUtf8Bytes(new
            {
                artifactProfile = edition.PrintArtifactProfileKey,
                files = files
                    .Where(item => item.Value.Kind is PublicationArtifactKind.InteriorPdf
                        or PublicationArtifactKind.PerfectBoundCoverPdf
                        or PublicationArtifactKind.CaseCoverPdf
                        or PublicationArtifactKind.DustJacketPdf
                        or PublicationArtifactKind.FrontCoverPdf
                        or PublicationArtifactKind.BackCoverPdf
                        or PublicationArtifactKind.PrintSetupManifest)
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => new
                    {
                        file = item.Key,
                        uploadAs = item.Value.Kind switch
                        {
                            PublicationArtifactKind.InteriorPdf => "interior",
                            PublicationArtifactKind.PerfectBoundCoverPdf => edition.PrintCoverMode == PrintCoverMode.Duplex ? "two-page cover: outside then inside" : "outside cover",
                            PublicationArtifactKind.CaseCoverPdf => "case wrap",
                            PublicationArtifactKind.DustJacketPdf => "dust jacket",
                            PublicationArtifactKind.FrontCoverPdf => "front cover",
                            PublicationArtifactKind.BackCoverPdf => "back cover",
                            PublicationArtifactKind.PrintSetupManifest => edition.Vendor == PublicationVendor.BarnesAndNoblePress ? "B&N setup reference" : "Digital Cloth setup reference",
                            _ => string.Empty,
                        },
                    }),
            }, JsonOptions);
            files["upload-map.json"] = (PublicationArtifactKind.Manifest, "application/json", uploadMap);
        }
        if (edition.Format == PublicationEditionFormat.DigitalPdf)
            files["book.pdf"] = (PublicationArtifactKind.BookPdf, "application/pdf", bookArtifact!.Data);
        if (edition.Format == PublicationEditionFormat.Epub)
        {
            var epub = await publishing.ExportAsync(projectId, editionId, PublishExportFormat.Epub, cancellationToken);
            var normalizedEpub = NormalizeEpub(epub.Content);
            ValidateEpubStructure(normalizedEpub);
            files["book.epub"] = (PublicationArtifactKind.Epub, epub.ContentType, normalizedEpub);
            if (TryReadEpubCover(normalizedEpub) is { } epubCover)
                files[$"front-cover{ExtensionFor(epubCover.MediaType)}"] =
                    (PublicationArtifactKind.FrontCoverImage, epubCover.MediaType, epubCover.Data);
        }

        if (edition.Format == PublicationEditionFormat.Epub
            && !files.Values.Any(item => item.Kind == PublicationArtifactKind.FrontCoverImage))
        {
            var document = await publishing.GetDocumentAsync(projectId, editionId, cancellationToken);
            if (document.Cover is null && document.CoverAsset is { } frontCover)
                files[$"front-cover{ExtensionFor(frontCover.ContentType)}"] =
                    (PublicationArtifactKind.FrontCoverImage, frontCover.ContentType, frontCover.Data);
        }
        var packagedReport = report with { CurrentPackage = null };
        var reportData = JsonSerializer.SerializeToUtf8Bytes(packagedReport, JsonOptions);
        files["preflight.json"] = (PublicationArtifactKind.PreflightReport, "application/json", reportData);
        var manifestEntries = files.Select(file => new
        {
            path = file.Key,
            kind = file.Value.Kind,
            mediaType = file.Value.MediaType,
            byteLength = file.Value.Data.LongLength,
            sha256 = Convert.ToHexStringLower(SHA256.HashData(file.Value.Data)),
        }).OrderBy(entry => entry.path).ToList();
        var manifest = new
        {
            schemaVersion = 1,
            label = "Lorekeeper validated",
            editionId,
            sourceFingerprint = report.SourceFingerprint,
            profile = new { report.ProfileId, report.ProfileVersion, report.ProfileReviewedAtUtc },
            printArtifactSettings = edition.Format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover
                ? new
                {
                    registryVersion = edition.PrintArtifactRegistryVersion,
                    registrySha256 = printArtifactProfiles.Sha256,
                    edition.PrintArtifactProfileKey,
                    edition.PrintCoverMode,
                }
                : null,
            runtime = new
            {
                assembler = AssemblerVersion,
                epubExporter = edition.Format == PublicationEditionFormat.Epub ? EpubExporterVersion : null,
                packageIdentity,
            },
            files = manifestEntries,
        };
        var manifestData = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        files["manifest.json"] = (PublicationArtifactKind.Manifest, "application/json", manifestData);
        var packageData = CreateDeterministicZip(files);
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var finalFingerprint = await editions.GetSourceFingerprintAsync(projectId, editionId, cancellationToken);
        if (!string.Equals(finalFingerprint, report.SourceFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("The edition changed while its publication package was being built. Run preflight again.");
        var latestArtifacts = await db.PublicationArtifacts.AsNoTracking()
            .Where(artifact => artifact.EditionId == editionId
                && !artifact.IsLegacy
                && artifact.SourceFingerprint == report.SourceFingerprint
                && (artifact.Kind == PublicationArtifactKind.InteriorPdf
                    || artifact.Kind == PublicationArtifactKind.PerfectBoundCoverPdf
                    || artifact.Kind == PublicationArtifactKind.CaseCoverPdf
                    || artifact.Kind == PublicationArtifactKind.DustJacketPdf
                    || artifact.Kind == PublicationArtifactKind.FrontCoverPdf
                    || artifact.Kind == PublicationArtifactKind.BackCoverPdf
                    || artifact.Kind == PublicationArtifactKind.PrintSetupManifest
                    || artifact.Kind == PublicationArtifactKind.BookPdf))
            .OrderByDescending(artifact => artifact.CreatedAt)
            .ToListAsync(cancellationToken);
        var finalPackageIdentity = PackageIdentity(
            profile,
            report.SourceFingerprint,
            latestArtifacts.FirstOrDefault(artifact => artifact.Kind ==
                (edition.Format == PublicationEditionFormat.DigitalPdf
                    ? PublicationArtifactKind.BookPdf
                    : PublicationArtifactKind.InteriorPdf)),
            latestArtifacts.Where(artifact => artifact.Kind is PublicationArtifactKind.PerfectBoundCoverPdf
                or PublicationArtifactKind.CaseCoverPdf or PublicationArtifactKind.DustJacketPdf
                or PublicationArtifactKind.FrontCoverPdf or PublicationArtifactKind.BackCoverPdf
                or PublicationArtifactKind.PrintSetupManifest).ToList());
        if (!string.Equals(finalPackageIdentity, packageIdentity, StringComparison.Ordinal))
            throw new InvalidOperationException("The press artifacts changed while the publication package was being built.");
        var generated = new List<PublicationArtifact>();
        foreach (var file in files.Where(file => file.Value.Kind is not PublicationArtifactKind.InteriorPdf
            and not PublicationArtifactKind.PerfectBoundCoverPdf
            and not PublicationArtifactKind.CaseCoverPdf
            and not PublicationArtifactKind.DustJacketPdf
            and not PublicationArtifactKind.FrontCoverPdf
            and not PublicationArtifactKind.BackCoverPdf
            and not PublicationArtifactKind.PrintSetupManifest
            and not PublicationArtifactKind.BookPdf))
            generated.Add(Artifact(
                editionId,
                file.Value.Kind,
                file.Key,
                file.Value.MediaType,
                file.Value.Data,
                report.SourceFingerprint,
                PackageRuntimeVersion(edition.Format),
                packageIdentity));
        generated.Add(Artifact(
            editionId,
            PublicationArtifactKind.PublicationPackage,
            "publication-package.zip",
            "application/zip",
            packageData,
            report.SourceFingerprint,
            PackageRuntimeVersion(edition.Format),
            packageIdentity));
        db.PublicationArtifacts.AddRange(generated);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var packageView = ArtifactView(
            generated.Single(artifact => artifact.Kind == PublicationArtifactKind.PublicationPackage),
            false);
        return new(
            report with { CurrentPackage = packageView },
            generated.Select(artifact => new PublicationArtifactView(
                artifact.Id,
                artifact.Kind,
                artifact.FileName,
                artifact.MediaType,
                artifact.Sha256,
                artifact.ByteLength,
                artifact.PageCount,
                artifact.SourceFingerprint,
                artifact.RendererVersion,
                artifact.ProfileId,
                artifact.CreatedAt,
                artifact.IsLegacy,
                IsStale: false)).ToList());
    }

    internal static void ValidatePdf(
        PublicationArtifact? artifact,
        string fingerprint,
        string? currentRendererVersion,
        string currentProfile,
        PublicationArtifactKind kind,
        string prefix,
        List<PublicationPreflightItem> items)
    {
        if (artifact is null)
        {
            items.Add(Error($"{prefix}_PDF_REQUIRED", $"Generate the {prefix.ToLowerInvariant()} PDF.", kind));
            return;
        }
        if (!string.Equals(artifact.SourceFingerprint, fingerprint, StringComparison.Ordinal))
            items.Add(Error($"{prefix}_PDF_STALE", $"The {prefix.ToLowerInvariant()} PDF is stale.", kind));
        if (artifact.IsLegacy)
            items.Add(Error($"{prefix}_PDF_LEGACY", $"The {prefix.ToLowerInvariant()} PDF was created by a retired renderer.", kind));
        if (currentRendererVersion is not null
            && !string.Equals(artifact.RendererVersion, currentRendererVersion, StringComparison.Ordinal))
            items.Add(Error("PRESS_RENDERER_STALE", $"The {prefix.ToLowerInvariant()} PDF was created by a different Lorekeeper Press renderer.", kind));
        if (currentRendererVersion is not null
            && !string.Equals(artifact.ProfileId, currentProfile, StringComparison.Ordinal))
            items.Add(Error("PRESS_PROFILE_STALE", $"The {prefix.ToLowerInvariant()} PDF was created for a different publication profile.", kind));
        if (!artifact.Data.AsSpan().StartsWith("%PDF-"u8)
            || artifact.PageCount is not > 0
            || !ArtifactBytesMatch(artifact))
            items.Add(Error($"{prefix}_PDF_INVALID", $"The {prefix.ToLowerInvariant()} PDF is invalid.", kind));
    }

    private static void ValidateStoredArtifact(
        PublicationArtifact? artifact,
        string fingerprint,
        string? currentRendererVersion,
        string currentProfile,
        PublicationArtifactKind kind,
        string prefix,
        List<PublicationPreflightItem> items)
    {
        if (artifact is null)
        {
            items.Add(Error($"{prefix}_REQUIRED", $"Generate the required {prefix.ToLowerInvariant()} file.", kind));
            return;
        }
        if (!string.Equals(artifact.SourceFingerprint, fingerprint, StringComparison.Ordinal))
            items.Add(Error($"{prefix}_STALE", $"The {prefix.ToLowerInvariant()} file is stale.", kind));
        if (artifact.IsLegacy || !ArtifactBytesMatch(artifact))
            items.Add(Error($"{prefix}_INVALID", $"The {prefix.ToLowerInvariant()} file is invalid or legacy.", kind));
        if (currentRendererVersion is not null
            && (!string.Equals(artifact.RendererVersion, currentRendererVersion, StringComparison.Ordinal)
                || !string.Equals(artifact.ProfileId, currentProfile, StringComparison.Ordinal)))
            items.Add(Error("PRESS_RENDERER_STALE", $"The {prefix.ToLowerInvariant()} file belongs to a different renderer or profile.", kind));
    }

    private static IReadOnlyList<PublicationArtifactKind> RequiredArtifactKinds(
        PrintArtifactProfile product,
        PublicationEdition edition)
    {
        var result = new List<PublicationArtifactKind>();
        if (edition.Vendor == PublicationVendor.BarnesAndNoblePress
            && edition.PrintCoverSubmissionMode == PrintCoverSubmissionMode.SeparatePanelsVendorSpine)
        {
            result.Add(PublicationArtifactKind.FrontCoverPdf);
            result.Add(PublicationArtifactKind.BackCoverPdf);
        }
        else
        {
            if (product.RequiresPerfectBoundCover)
                result.Add(PublicationArtifactKind.PerfectBoundCoverPdf);
            if (product.RequiresCaseCover)
                result.Add(PublicationArtifactKind.CaseCoverPdf);
            if (product.RequiresDustJacket)
                result.Add(PublicationArtifactKind.DustJacketPdf);
        }
        if (product.RequiresClothManifest)
            result.Add(PublicationArtifactKind.PrintSetupManifest);
        if (edition.Vendor == PublicationVendor.BarnesAndNoblePress)
            result.Add(PublicationArtifactKind.PrintSetupManifest);
        return result;
    }

    private static bool ArtifactBytesMatch(PublicationArtifact artifact) =>
        artifact.ByteLength == artifact.Data.LongLength
        && string.Equals(
            artifact.Sha256,
            Convert.ToHexStringLower(SHA256.HashData(artifact.Data)),
            StringComparison.Ordinal);

    internal static void ValidateEpubStructure(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count == 0)
            throw new InvalidDataException("EPUB is empty.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            ValidateArchivePath(entry.FullName);
            if (!entries.TryAdd(entry.FullName, entry))
                throw new InvalidDataException($"EPUB contains duplicate entry '{entry.FullName}'.");
        }
        var mimetype = archive.GetEntry("mimetype")
            ?? throw new InvalidDataException("EPUB is missing its mimetype entry.");
        using var reader = new StreamReader(mimetype.Open(), Encoding.ASCII);
        if (reader.ReadToEnd() != "application/epub+zip"
            || archive.Entries[0] != mimetype
            || mimetype.CompressedLength != mimetype.Length)
        {
            throw new InvalidDataException("EPUB structure validation failed.");
        }

        var container = entries.GetValueOrDefault("META-INF/container.xml")
            ?? throw new InvalidDataException("EPUB is missing META-INF/container.xml.");
        var containerDocument = ReadXml(container);
        XNamespace containerNamespace = "urn:oasis:names:tc:opendocument:xmlns:container";
        if (containerDocument.Root?.Name != containerNamespace + "container"
            || containerDocument.Root.Attribute("version")?.Value != "1.0")
        {
            throw new InvalidDataException("EPUB container metadata is invalid.");
        }
        var rootFiles = containerDocument
            .Descendants(containerNamespace + "rootfile")
            .Where(root => root.Attribute("media-type")?.Value == "application/oebps-package+xml")
            .Select(root => root.Attribute("full-path")?.Value)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToList();
        if (rootFiles.Count != 1)
            throw new InvalidDataException("EPUB must declare exactly one OPF package rootfile.");
        ValidateArchivePath(rootFiles[0]);
        if (!entries.TryGetValue(rootFiles[0], out var packageEntry))
            throw new InvalidDataException("EPUB package rootfile does not exist.");

        var packageDocument = ReadXml(packageEntry);
        XNamespace opf = "http://www.idpf.org/2007/opf";
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        if (packageDocument.Root?.Name != opf + "package"
            || packageDocument.Root.Attribute("version")?.Value.StartsWith("3.", StringComparison.Ordinal) != true)
        {
            throw new InvalidDataException("EPUB package must be OPF 3.");
        }
        var metadata = packageDocument.Root.Element(opf + "metadata")
            ?? throw new InvalidDataException("EPUB package metadata is missing.");
        var uniqueIdentifier = packageDocument.Root.Attribute("unique-identifier")?.Value;
        if (string.IsNullOrWhiteSpace(uniqueIdentifier)
            || !metadata.Elements(dc + "identifier").Any(identifier =>
                identifier.Attribute("id")?.Value == uniqueIdentifier
                && !string.IsNullOrWhiteSpace(identifier.Value))
            || !metadata.Elements(dc + "title").Any(title => !string.IsNullOrWhiteSpace(title.Value))
            || !metadata.Elements(dc + "language").Any(language => !string.IsNullOrWhiteSpace(language.Value)))
        {
            throw new InvalidDataException("EPUB package metadata is incomplete.");
        }

        var manifest = packageDocument.Root.Element(opf + "manifest")
            ?? throw new InvalidDataException("EPUB manifest is missing.");
        var manifestItems = new Dictionary<string, (string Path, XElement Element)>(StringComparer.Ordinal);
        foreach (var item in manifest.Elements(opf + "item"))
        {
            var id = item.Attribute("id")?.Value;
            var href = item.Attribute("href")?.Value;
            var mediaType = item.Attribute("media-type")?.Value;
            if (string.IsNullOrWhiteSpace(id)
                || string.IsNullOrWhiteSpace(href)
                || string.IsNullOrWhiteSpace(mediaType)
                || !manifestItems.TryAdd(id, (ResolveArchivePath(rootFiles[0], href), item)))
            {
                throw new InvalidDataException("EPUB manifest contains an invalid or duplicate item.");
            }
        }
        if (manifestItems.Count == 0
            || manifestItems.Values.Any(item => !entries.ContainsKey(item.Path)))
        {
            throw new InvalidDataException("EPUB manifest references a missing resource.");
        }
        var navigationItems = manifestItems.Values.Where(item =>
            (item.Element.Attribute("properties")?.Value ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains("nav", StringComparer.Ordinal)).ToList();
        if (navigationItems.Count != 1)
            throw new InvalidDataException("EPUB manifest must identify exactly one navigation document.");

        var spine = packageDocument.Root.Element(opf + "spine")
            ?? throw new InvalidDataException("EPUB spine is missing.");
        var spineReferences = spine.Elements(opf + "itemref")
            .Select(item => item.Attribute("idref")?.Value)
            .ToList();
        if (spineReferences.Count == 0
            || spineReferences.Any(reference =>
                string.IsNullOrWhiteSpace(reference) || !manifestItems.ContainsKey(reference)))
        {
            throw new InvalidDataException("EPUB spine contains a missing manifest reference.");
        }

        var xhtmlDocuments = new Dictionary<string, XDocument>(StringComparer.Ordinal);
        foreach (var reference in spineReferences.Cast<string>())
        {
            var item = manifestItems[reference];
            if (item.Element.Attribute("media-type")?.Value != "application/xhtml+xml")
                throw new InvalidDataException("Every EPUB spine item must be an XHTML content document.");
            xhtmlDocuments[item.Path] = ReadXhtml(entries[item.Path]);
        }
        var navigationPath = navigationItems[0].Path;
        var navigationDocument = ReadXhtml(entries[navigationPath]);
        xhtmlDocuments[navigationPath] = navigationDocument;
        XNamespace xhtml = "http://www.w3.org/1999/xhtml";
        XNamespace epub = "http://www.idpf.org/2007/ops";
        var tocNavigation = navigationDocument.Descendants(xhtml + "nav").Where(navigation =>
                (navigation.Attribute(epub + "type")?.Value ?? string.Empty)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains("toc", StringComparer.Ordinal)).ToList();
        var landmarksNavigation = navigationDocument.Descendants(xhtml + "nav").Where(navigation =>
                (navigation.Attribute(epub + "type")?.Value ?? string.Empty)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains("landmarks", StringComparer.Ordinal)).ToList();
        if (tocNavigation.Count != 1
            || landmarksNavigation.Count != 1
            || !landmarksNavigation[0].Descendants(xhtml + "a").Any(anchor =>
                (anchor.Attribute(epub + "type")?.Value ?? string.Empty)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains("bodymatter", StringComparer.Ordinal)))
        {
            throw new InvalidDataException("EPUB navigation must contain one table of contents and one bodymatter landmark.");
        }
        foreach (var anchor in tocNavigation[0].Descendants(xhtml + "a")
            .Concat(landmarksNavigation[0].Descendants(xhtml + "a")))
        {
            ValidateXhtmlReference(
                navigationPath,
                anchor.Attribute("href")?.Value,
                entries,
                xhtmlDocuments);
        }
        foreach (var (path, content) in xhtmlDocuments.ToList())
        {
            foreach (var anchor in content.Descendants(xhtml + "a"))
            {
                ValidateXhtmlReference(
                    path,
                    anchor.Attribute("href")?.Value,
                    entries,
                    xhtmlDocuments,
                    allowExternal: true);
            }
            foreach (var source in content.Descendants().Attributes("src"))
                ValidateXhtmlReference(path, source.Value, entries, xhtmlDocuments, requireXhtml: false);
            foreach (var stylesheet in content.Descendants(xhtml + "link").Where(link =>
                (link.Attribute("rel")?.Value ?? string.Empty)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains("stylesheet", StringComparer.OrdinalIgnoreCase)))
            {
                ValidateXhtmlReference(
                    path,
                    stylesheet.Attribute("href")?.Value,
                    entries,
                    xhtmlDocuments,
                    requireXhtml: false);
            }
        }
    }

    private static XDocument ReadXml(ZipArchiveEntry entry)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            MaxCharactersInDocument = 16 * 1024 * 1024,
            XmlResolver = null,
        };
        try
        {
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, settings);
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException($"EPUB XML entry '{entry.FullName}' is invalid.", exception);
        }
    }

    private static XDocument ReadXhtml(ZipArchiveEntry entry)
    {
        var document = ReadXml(entry);
        XNamespace xhtml = "http://www.w3.org/1999/xhtml";
        if (document.Root?.Name != xhtml + "html")
            throw new InvalidDataException($"EPUB content entry '{entry.FullName}' is not XHTML.");
        return document;
    }

    private static void ValidateXhtmlReference(
        string sourcePath,
        string? href,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        IDictionary<string, XDocument> xhtmlDocuments,
        bool requireXhtml = true,
        bool allowExternal = false)
    {
        if (allowExternal
            && Uri.TryCreate(href, UriKind.Absolute, out var external)
            && external.Scheme is "http" or "https" or "mailto" or "tel")
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(href)
            || Uri.TryCreate(href, UriKind.Absolute, out _)
            || href.Contains('?')
            || href.Contains('\\'))
        {
            throw new InvalidDataException("EPUB content contains an unsafe or missing reference.");
        }
        var hashIndex = href.IndexOf('#');
        var pathPart = hashIndex < 0 ? href : href[..hashIndex];
        var fragment = hashIndex < 0 ? string.Empty : Uri.UnescapeDataString(href[(hashIndex + 1)..]);
        if (hashIndex >= 0 && string.IsNullOrWhiteSpace(fragment))
            throw new InvalidDataException("EPUB content contains an empty fragment reference.");
        var targetPath = string.IsNullOrEmpty(pathPart)
            ? sourcePath
            : ResolveArchivePath(sourcePath, pathPart);
        if (!entries.TryGetValue(targetPath, out var targetEntry))
            throw new InvalidDataException($"EPUB content reference '{href}' does not exist.");
        if (!requireXhtml && string.IsNullOrEmpty(fragment))
            return;
        if (requireXhtml && !xhtmlDocuments.TryGetValue(targetPath, out var targetDocument))
            throw new InvalidDataException($"EPUB navigation target '{href}' is not in the reading order.");
        if (!xhtmlDocuments.TryGetValue(targetPath, out targetDocument))
        {
            targetDocument = ReadXhtml(targetEntry);
            xhtmlDocuments[targetPath] = targetDocument;
        }
        if (!string.IsNullOrEmpty(fragment)
            && targetDocument.Root?.DescendantsAndSelf().Attributes("id").Any(id => id.Value == fragment) != true)
        {
            throw new InvalidDataException($"EPUB fragment reference '{href}' does not exist.");
        }
    }

    private static string ResolveArchivePath(string packagePath, string href)
    {
        if (Uri.TryCreate(href, UriKind.Absolute, out _)
            || href.Contains('#')
            || href.Contains('?')
            || href.Contains('\\'))
        {
            throw new InvalidDataException("EPUB manifest href is not a safe package-relative path.");
        }
        var directory = packagePath.Contains('/')
            ? packagePath[..(packagePath.LastIndexOf('/') + 1)]
            : string.Empty;
        var combined = $"{directory}{Uri.UnescapeDataString(href)}";
        ValidateArchivePath(combined);
        return combined;
    }

    private static void ValidateArchivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.StartsWith("/", StringComparison.Ordinal)
            || path.Contains('\\')
            || path.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException($"EPUB entry path '{path}' is unsafe.");
        }
    }

    internal static byte[] NormalizeEpub(byte[] data)
    {
        using var input = new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read);
        var files = input.Entries.ToDictionary(
            entry => entry.FullName,
            entry =>
            {
                using var stream = entry.Open();
                using var content = new MemoryStream();
                stream.CopyTo(content);
                var bytes = content.ToArray();
                if (entry.FullName.EndsWith(".opf", StringComparison.OrdinalIgnoreCase))
                {
                    var text = Encoding.UTF8.GetString(bytes);
                    text = Regex.Replace(
                        text,
                        @"(<meta\s+property=""dcterms:modified"">)[^<]*(</meta>)",
                        "${1}2000-01-01T00:00:00Z${2}",
                        RegexOptions.CultureInvariant);
                    bytes = Encoding.UTF8.GetBytes(text);
                }
                return bytes;
            },
            StringComparer.Ordinal);
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            WriteNormalizedEpubEntry(archive, "mimetype", files["mimetype"], CompressionLevel.NoCompression);
            foreach (var file in files
                .Where(file => file.Key != "mimetype")
                .OrderBy(file => file.Key, StringComparer.Ordinal))
            {
                WriteNormalizedEpubEntry(archive, file.Key, file.Value, CompressionLevel.Optimal);
            }
        }
        return output.ToArray();
    }

    private static EpubCoverArtifact? TryReadEpubCover(byte[] data)
    {
        using var archive = new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read);
        var entry = archive.Entries.FirstOrDefault(candidate =>
            candidate.FullName.StartsWith("EPUB/images/cover.", StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return null;
        using var input = entry.Open();
        using var output = new MemoryStream();
        input.CopyTo(output);
        var extension = Path.GetExtension(entry.FullName).ToLowerInvariant();
        var mediaType = extension switch
        {
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => string.Empty,
        };
        return string.IsNullOrEmpty(mediaType) ? null : new(mediaType, output.ToArray());
    }

    private void ValidateProfile(
        PublicationEdition edition,
        PublicationPreflightProfile profile,
        PublicationArtifact? interior,
        PublishDocument document,
        PublicationCoverDesignView? coverDesign,
        List<PublicationPreflightItem> items)
    {
        var chapters = document.Sections.SelectMany(section => section.Chapters).ToList();
        if (chapters.Count == 0)
            items.Add(Error("CONTENT_EMPTY", "Include at least one chapter."));
        if (chapters.Any(chapter => !ChapterHasRenderableContent(chapter)))
            items.Add(Error("CONTENT_CHAPTER_EMPTY", "Every included chapter must contain semantic text, a Figure, or a Designed Page with renderable objects."));

        if (edition.Format is PublicationEditionFormat.Epub or PublicationEditionFormat.DigitalPdf)
        {
            if (edition.Format == PublicationEditionFormat.Epub && document.Cover is null && document.CoverAsset is null)
                items.Add(Error("EPUB_COVER_REQUIRED", "Create a composed front cover for the EPUB edition."));
            else if (edition.Format == PublicationEditionFormat.Epub
                && document.Cover is null
                && string.IsNullOrWhiteSpace(document.CoverAsset?.AltText))
                items.Add(Error("EPUB_COVER_ALT_TEXT_REQUIRED", "The EPUB cover requires alternative text."));
            if (edition.Format == PublicationEditionFormat.DigitalPdf && coverDesign is null)
                items.Add(Error("DIGITAL_PDF_COVER_REQUIRED", "Create the front cover for this Digital PDF edition."));
            var semanticManuscripts = chapters.Select(chapter => chapter.Manuscript)
                .Concat(chapters.SelectMany(chapter => chapter.PageCompositions).Select(composition => composition.SemanticManuscript))
                .Concat(document.PublicationSections.Select(item => item.Manuscript))
                .Concat(document.PublicationSections.SelectMany(item => item.PageCompositions).Select(composition => composition.SemanticManuscript));
            var missingFigureAlt = semanticManuscripts.Any(manuscript =>
                    manuscript.Content.Any(block =>
                        block.Type == ManuscriptBlockType.Figure
                        && !block.Decorative
                        && (string.IsNullOrWhiteSpace(block.AltText)
                            || block.ImageId is not Guid imageId
                            || document.Assets.All(asset => asset.Id != imageId))))
                || chapters.SelectMany(chapter => chapter.PageCompositions)
                    .Concat(document.PublicationSections.SelectMany(section => section.PageCompositions))
                    .SelectMany(composition => composition.Variants)
                    .Any(variant =>
                    {
                        var visibleLayers = variant.Scene.Layers.Where(layer => layer.Visible).Select(layer => layer.Id).ToHashSet();
                        return CompositionSceneResolver.Flatten(variant.Scene).Any(item => item.Kind == CompositionObjectKind.Image
                            && item.Visible
                            && visibleLayers.Contains(item.LayerId)
                            && !item.Decorative
                            && (item.AccessibilityDecisionPending || string.IsNullOrWhiteSpace(item.AltText)));
                    });
            var missingCoverAlt = document.Cover is { } composedCover
                && CompositionSceneResolver.Flatten(composedCover.Scene).Any(item =>
                    item.Kind == CompositionObjectKind.Image
                    && item.Visible
                    && !item.Decorative
                    && (item.AccessibilityDecisionPending || string.IsNullOrWhiteSpace(item.AltText)));
            if (missingFigureAlt || missingCoverAlt)
                items.Add(Error("DIGITAL_ALT_TEXT_REQUIRED", "Every meaningful digital-publication image requires alternative text or an explicit decorative designation."));
            if (interior?.PageCount is int digitalPages
                && (digitalPages < profile.MinimumPages || digitalPages > profile.MaximumPages))
            {
                items.Add(Error(
                    "DIGITAL_PAGE_COUNT_UNSUPPORTED",
                    $"The installed profile supports {profile.MinimumPages}–{profile.MaximumPages} pages."));
            }
            return;
        }

        PrintArtifactProfile product;
        try
        {
            product = printArtifactProfiles.GetRequired(edition.PrintArtifactProfileKey);
            if (product.Format != edition.Format || product.Vendor != edition.Vendor)
                items.Add(Error("PRINT_ARTIFACT_SETTINGS_UNAVAILABLE", "The selected print artifact settings are not available for this format and destination."));
            if (!product.CoverModes.Contains(edition.PrintCoverMode))
                items.Add(Error("PRINT_ARTIFACT_OPTION_UNAVAILABLE", "The selected cover-printing mode is not available for these artifact settings."));
            if (!product.AllowsCustomTrim && !product.TrimSizes.Contains($"{edition.PageWidthInches:0.##}x{edition.PageHeightInches:0.##}", StringComparer.Ordinal))
                items.Add(Error("PRINT_TRIM_UNSUPPORTED", "The selected trim is not available for these print artifact settings."));
            if (interior?.PageCount is int productPages)
                _ = printGeometry.Calculate(edition, productPages);
        }
        catch (InvalidOperationException exception)
        {
            items.Add(Error("PRINT_ARTIFACT_SETTINGS_UNAVAILABLE", exception.Message));
            return;
        }
        if (edition.PageWidthInches is < 3.5 or > 12
            || edition.PageHeightInches is < 5 or > 15)
        {
            items.Add(Error("PRINT_TRIM_UNSUPPORTED", "Lorekeeper Press supports trim sizes from 3.5 × 5 through 12 × 15 inches."));
        }
        if (edition.PageMarginInches < 0.5)
            items.Add(Error("PRINT_MARGIN_UNSAFE", "The initial paperback profile requires at least a 0.5-inch page margin."));
        if (edition.BodyFontSizePoints is < 9 or > 14 || edition.BodyLineHeight is < 1.1 or > 2)
            items.Add(Error("PRINT_TYPOGRAPHY_UNSUPPORTED", "Body type must be 9–14 points with 1.1–2.0 line height."));
        if (edition.Vendor != PublicationVendor.Generic && !edition.Bleed)
            items.Add(Error("PRINT_COVER_BLEED_REQUIRED", "The installed vendor cover profile requires 0.125-inch bleed."));
        if (coverDesign is not null
            && (!string.Equals(coverDesign.Title.Trim(), edition.TitleOverride.Trim(), StringComparison.Ordinal)
                || !string.Equals(coverDesign.Author.Trim(), edition.Author.Trim(), StringComparison.Ordinal)))
        {
            items.Add(Error("COVER_METADATA_MISMATCH", "Cover title and author must match the edition metadata."));
        }
        if (interior?.PageCount is int pages)
        {
            if (pages < profile.MinimumPages || pages > profile.MaximumPages)
            {
                items.Add(Error(
                    "PRINT_PAGE_COUNT_UNSUPPORTED",
                    $"The installed profile supports {profile.MinimumPages}–{profile.MaximumPages} pages."));
            }
            if (pages % 2 != 0)
                items.Add(Error("PRINT_PAGE_COUNT_ODD", "The paperback interior page count must be even."));
        }
        items.Add(new(
            "warning",
            "BLANK_PAGE_REVIEW_RECOMMENDED",
            "Inspect the prepared interior for intentional or accidental blank pages before upload."));
    }

    private static bool ChapterHasRenderableContent(PublishChapterDocument chapter) =>
        chapter.Manuscript.Content.Any(block => block.Type switch
        {
            ManuscriptBlockType.Figure => block.ImageId is not null,
            ManuscriptBlockType.DesignedPage => block.PageCompositionId is Guid compositionId
                && chapter.PageCompositions.FirstOrDefault(item => item.Id == compositionId) is { } composition
                && composition.Variants.Any(variant => CompositionSceneResolver.Flatten(variant.Scene).Any(item => item.Visible)),
            ManuscriptBlockType.SceneBreak => true,
            _ => !string.IsNullOrWhiteSpace(ManuscriptCodec.Text(block)),
        });

    private static string PackageIdentity(
        PublicationPreflightProfile profile,
        string sourceFingerprint,
        PublicationArtifact? interior,
        IReadOnlyList<PublicationArtifact> physicalArtifacts)
    {
        if (profile.Format == PublicationEditionFormat.Epub)
        {
            interior = null;
            physicalArtifacts = [];
        }
        var source = string.Join(
            "|",
            AssemblerVersion,
            profile.Format == PublicationEditionFormat.Epub ? EpubExporterVersion : string.Empty,
            profile.Id,
            profile.Version,
            sourceFingerprint,
            interior?.Sha256 ?? string.Empty,
            interior?.RendererVersion ?? string.Empty,
            interior?.ProfileId ?? string.Empty,
            string.Join(";", physicalArtifacts.OrderBy(item => item.Kind).Select(item =>
                $"{item.Kind}:{item.Sha256}:{item.RendererVersion}:{item.ProfileId}")));
        return $"package:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)))}";
    }

    private static void ValidateLanguageScope(
        PublishDocument document,
        PublicationCoverDesignView? coverDesign,
        List<PublicationPreflightItem> items)
    {
        var renderedText = new List<(string Label, string Text)>
        {
            ("project title", document.ProjectName),
            ("edition title", document.Profile.TitleOverride),
            ("edition subtitle", document.Profile.Subtitle),
            ("edition author", document.Profile.Author),
            ("publisher", document.Profile.Publisher),
            ("copyright", document.Profile.Copyright),
            ("description", document.Profile.Description),
        };
        renderedText.AddRange(document.Sections.SelectMany(section =>
            new[]
            {
                ($"section {section.ActId?.ToString("N") ?? "unassigned"} title", section.Title),
                ($"section {section.ActId?.ToString("N") ?? "unassigned"} synopsis", section.Synopsis),
            }));
        renderedText.AddRange(document.Sections.SelectMany(section => section.Chapters).SelectMany(chapter =>
            new[]
            {
                ($"chapter {chapter.Id:N} title", chapter.Title),
                ($"chapter {chapter.Id:N} synopsis", chapter.Synopsis),
            }));
        renderedText.AddRange(document.Sections.SelectMany(section => section.Chapters).SelectMany(chapter =>
            chapter.PageCompositions.SelectMany(composition => composition.Variants)
                .SelectMany(variant => variant.Scene.Objects)
                .SelectMany(item => new[]
                {
                    ($"composition object {item.Id:N} text binding", item.TextBinding),
                    ($"composition object {item.Id:N} alternative text", item.AltText),
                })));
        renderedText.AddRange(document.PublicationSections.SelectMany(section =>
            section.Manuscript.Content.SelectMany(block => block.Content.Select(inline =>
                ($"publication section {section.Id:N} block {block.Id}", inline.Text)))));
        renderedText.AddRange(document.Assets.Select(asset =>
            ($"asset {asset.Id:N} alternative text", asset.AltText)));
        if (document.CoverAsset is { } coverAsset)
            renderedText.Add(($"cover asset {coverAsset.Id:N} alternative text", coverAsset.AltText));
        if (coverDesign is not null)
        {
            renderedText.AddRange(
            [
                ($"cover {coverDesign.Id:N} title", coverDesign.Title),
                ($"cover {coverDesign.Id:N} subtitle", coverDesign.Subtitle),
                ($"cover {coverDesign.Id:N} author", coverDesign.Author),
                ($"cover {coverDesign.Id:N} spine", coverDesign.SpineText),
                ($"cover {coverDesign.Id:N} back copy", coverDesign.BackCopy),
            ]);
        }
        foreach (var source in renderedText.Where(source =>
            source.Text.EnumerateRunes().Any(rune => !IsSupportedLatinRune(rune))))
        {
            items.Add(Error(
                "SCRIPT_SCOPE_UNSUPPORTED",
                $"{source.Label} contains text outside the tested Latin-script LTR scope."));
        }

        foreach (var chapter in document.Sections.SelectMany(section => section.Chapters))
            ValidateManuscriptLanguage($"Chapter '{chapter.Title}'", chapter.Manuscript, items);
        foreach (var section in document.PublicationSections)
        {
            ValidateManuscriptLanguage($"Publication section '{section.Title}'", section.Manuscript, items);
            foreach (var composition in section.PageCompositions)
                ValidateManuscriptLanguage($"Publication section '{section.Title}' Designed Page '{composition.Name}'", composition.SemanticManuscript, items);
            if (section.Title.EnumerateRunes().Any(rune => !IsSupportedLatinRune(rune)))
                items.Add(Error("SCRIPT_SCOPE_UNSUPPORTED", $"Publication section {section.Id:N} title is outside the tested Latin-script LTR scope."));
        }
    }

    private static void ValidateManuscriptLanguage(
        string label,
        ManuscriptDocument manuscript,
        List<PublicationPreflightItem> items)
    {
        foreach (var block in manuscript.Content)
        {
            var unsupportedLanguage = block.Content
                .SelectMany(inline => inline.Marks)
                .Where(mark => mark.Type == ManuscriptMarkType.Language)
                .Select(mark => mark.Value)
                .FirstOrDefault(value => !IsEnglishLanguage(value));
            if (unsupportedLanguage is not null)
            {
                items.Add(Error(
                    "LANGUAGE_MARK_UNSUPPORTED",
                    $"{label}, block {block.Id}, uses unsupported language mark '{unsupportedLanguage}'."));
            }
            var text = string.Concat(block.Content.Select(inline => inline.Text));
            if (text.EnumerateRunes().Any(rune => !IsSupportedLatinRune(rune)))
            {
                items.Add(Error(
                    "SCRIPT_SCOPE_UNSUPPORTED",
                    $"{label}, block {block.Id}, contains text outside the tested Latin-script LTR scope."));
            }
            if (block.Type == ManuscriptBlockType.Figure
                && (block.AltText ?? string.Empty).EnumerateRunes().Any(rune => !IsSupportedLatinRune(rune)))
            {
                items.Add(Error(
                    "SCRIPT_SCOPE_UNSUPPORTED",
                    $"{label}, figure block {block.Id}, alternative text is outside the tested Latin-script LTR scope."));
            }
        }
    }

    private static bool IsEnglishLanguage(string? value) =>
        PublicationLanguage.IsPressSupported(value);

    private static bool IsSupportedLatinRune(Rune rune) =>
        rune.Value <= 0x024F
        || rune.Value is >= 0x0300 and <= 0x036F
        || rune.Value is >= 0x2000 and <= 0x206F
        || rune.Value is >= 0x20A0 and <= 0x20CF
        || rune.Value is >= 0x2100 and <= 0x214F
        || rune.Value == 0xFEFF;

    internal static string PackageRuntimeVersion(PublicationEditionFormat format) =>
        format == PublicationEditionFormat.Epub
            ? $"{AssemblerVersion}+{EpubExporterVersion}"
            : AssemblerVersion;

    private static PublicationArtifactView ArtifactView(PublicationArtifact artifact, bool isStale) => new(
        artifact.Id,
        artifact.Kind,
        artifact.FileName,
        artifact.MediaType,
        artifact.Sha256,
        artifact.ByteLength,
        artifact.PageCount,
        artifact.SourceFingerprint,
        artifact.RendererVersion,
        artifact.ProfileId,
        artifact.CreatedAt,
        artifact.IsLegacy,
        isStale);

    private static void WriteNormalizedEpubEntry(
        ZipArchive archive,
        string path,
        byte[] data,
        CompressionLevel compression)
    {
        var entry = archive.CreateEntry(path, compression);
        entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var target = entry.Open();
        target.Write(data);
    }

    private async Task AddPressEvidenceAsync(
        PublicationEdition edition,
        PublicationArtifact? interior,
        IReadOnlyList<PublicationArtifact> physicalArtifacts,
        PublicationCoverTemplate template,
        string? currentRendererVersion,
        List<PublicationPreflightItem> items,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        if (interior?.RenderJobId is not Guid renderJobId
            || physicalArtifacts.Count == 0
            || physicalArtifacts.Any(item => item.RenderJobId != renderJobId))
        {
            items.Add(Error("PRESS_EVIDENCE_REQUIRED", "The current PDFs must come from the same completed press render."));
            return;
        }
        var job = await db.PublicationRenderJobs.AsNoTracking().FirstOrDefaultAsync(
            candidate => candidate.Id == renderJobId
                && candidate.EditionId == edition.Id
                && candidate.Status == PublicationRenderStatus.Completed,
            cancellationToken);
        if (job is null)
        {
            items.Add(Error("PRESS_EVIDENCE_REQUIRED", "The current PDFs do not have completed press evidence."));
            return;
        }
        if (currentRendererVersion is not null
            && !string.Equals(job.RendererVersion, currentRendererVersion, StringComparison.Ordinal))
        {
            items.Add(Error("PRESS_RENDERER_STALE", "The completed render evidence belongs to a different Lorekeeper Press renderer."));
        }
        if (currentRendererVersion is not null
            && !string.Equals(job.ProfileId, edition.VendorProfileVersion, StringComparison.Ordinal))
            items.Add(Error("PRESS_PROFILE_STALE", "The completed render evidence belongs to a different publication profile."));
        try
        {
            var diagnostics = JsonSerializer.Deserialize<List<PublicationRenderDiagnostic>>(
                job.DiagnosticsJson,
                JsonOptions) ?? [];
            items.AddRange(diagnostics.Select(diagnostic => new PublicationPreflightItem(
                diagnostic.Severity,
                diagnostic.Code,
                diagnostic.Message,
                diagnostic.ArtifactKind switch
                {
                    "cover-pdf" or "perfect-bound-cover-pdf" => PublicationArtifactKind.PerfectBoundCoverPdf,
                    "case-cover-pdf" => PublicationArtifactKind.CaseCoverPdf,
                    "dust-jacket-pdf" => PublicationArtifactKind.DustJacketPdf,
                    "front-cover-pdf" => PublicationArtifactKind.FrontCoverPdf,
                    "back-cover-pdf" => PublicationArtifactKind.BackCoverPdf,
                    "interior-pdf" => PublicationArtifactKind.InteriorPdf,
                    _ => null,
                },
                diagnostic.Page,
                diagnostic.SourceKind,
                diagnostic.SourceId)));

            using var evidence = JsonDocument.Parse(job.EvidenceJson);
            var root = evidence.RootElement;
            RequireTrue(root, "interiorPageBoxesConsistent", "INTERIOR_PAGE_BOXES", "Interior page boxes are inconsistent.", items);
            RequireTrue(root, "coverPageBoxesConsistent", "COVER_PAGE_BOXES", "Cover page boxes are inconsistent.", items);
            RequireFalse(root, "hasEncryption", "PDF_ENCRYPTED", "Publication PDFs must not be encrypted.", items);
            RequireFalse(root, "hasForbiddenActions", "PDF_FORBIDDEN_ACTIONS", "Publication PDFs contain forbidden actions.", items);
            RequireZero(root, "annotationCount", "PDF_ANNOTATIONS", "Publication PDFs contain annotations.", items);
            RequireDimension(root, "interiorWidthPoints", edition.PageWidthInches * 72, "INTERIOR_WIDTH", items);
            RequireDimension(root, "interiorHeightPoints", edition.PageHeightInches * 72, "INTERIOR_HEIGHT", items);
            ValidateCoverSurfaceEvidence(edition, interior.PageCount ?? 0, physicalArtifacts, root, items);
            var expectedPdfVersion = edition.Vendor switch
            {
                PublicationVendor.IngramSpark => "1.3",
                PublicationVendor.BarnesAndNoblePress => "1.4",
                _ => "1.7",
            };
            if (!root.TryGetProperty("pdfVersion", out var pdfVersion)
                || pdfVersion.GetString() != expectedPdfVersion)
            {
                items.Add(Error("PDF_VERSION_UNSUPPORTED", $"The installed profile requires PDF {expectedPdfVersion}."));
            }
            if (!root.TryGetProperty("fonts", out var fonts)
                || fonts.ValueKind != JsonValueKind.Array
                || fonts.GetArrayLength() == 0
                || fonts.EnumerateArray().Any(font =>
                    !font.TryGetProperty("embedded", out var embedded) || embedded.ValueKind != JsonValueKind.True))
            {
                items.Add(Error("PDF_FONTS_NOT_EMBEDDED", "Every PDF font must be embedded."));
            }
            if (edition.Vendor == PublicationVendor.IngramSpark)
            {
                RequireFalse(root, "hasTransparency", "PDF_TRANSPARENCY", "The Ingram PDF contains transparency.", items);
                if (!root.TryGetProperty("outputIntentCount", out var outputIntentCount)
                    || outputIntentCount.ValueKind != JsonValueKind.Number
                    || outputIntentCount.GetInt32() != 1 + physicalArtifacts.Count(item => item.MediaType == "application/pdf"))
                {
                    items.Add(Error("PDF_OUTPUT_INTENT_REQUIRED", "Every Ingram PDF requires its own output intent."));
                }
                if (!root.TryGetProperty("declaredStandard", out var declaredStandard)
                    || declaredStandard.GetString() != "PDF/X-1a:2001")
                {
                    items.Add(Error("PDF_STANDARD_DECLARATION", "The Ingram artifacts must declare PDF/X-1a:2001."));
                }
                ValidateIngramColorSpaces(root, items);
            }
            else
            {
                RequireFalse(root, "hasTransparency", "PDF_TRANSPARENCY", "The KDP PDFs contain transparency.", items);
            }
            if (currentRendererVersion is not null
                && printArtifactProfiles.GetRequired(edition.PrintArtifactProfileKey).InteriorProcess == PrintInteriorProcess.BlackAndWhite)
            {
                if (!root.TryGetProperty("interiorImageCount", out var interiorImageCount)
                    || interiorImageCount.ValueKind != JsonValueKind.Number
                    || interiorImageCount.GetInt32() < 0)
                {
                    items.Add(Error("PRESS_EVIDENCE_INVALID", "Current press evidence must report the number of placed interior images."));
                }
                else if (interiorImageCount.GetInt32() > 0
                    && (!root.TryGetProperty("interiorImageColorSpace", out var interiorImageColorSpace)
                        || interiorImageColorSpace.ValueKind != JsonValueKind.String
                        || interiorImageColorSpace.GetString() != "DeviceGray"))
                {
                    items.Add(Error("PDF_BLACK_AND_WHITE_INTERIOR_REQUIRED", "Black-and-white editions require grayscale interior image content."));
                }
            }
            if (root.TryGetProperty("imageCount", out var imageCount)
                && imageCount.ValueKind == JsonValueKind.Number
                && imageCount.GetInt32() > 0)
            {
                items.Add(new(
                    "warning",
                    "IMAGE_RESOLUTION_REVIEW",
                    "Review effective image resolution in the rendered page map before upload."));
            }
        }
        catch (JsonException)
        {
            items.Add(Error("PRESS_EVIDENCE_INVALID", "Stored press evidence could not be read."));
        }
    }

    private void ValidateCoverSurfaceEvidence(
        PublicationEdition edition,
        int interiorPageCount,
        IReadOnlyList<PublicationArtifact> physicalArtifacts,
        JsonElement evidence,
        List<PublicationPreflightItem> items)
    {
        if (!evidence.TryGetProperty("coverSurfaces", out var surfaces) || surfaces.ValueKind != JsonValueKind.Array)
        {
            items.Add(Error("COVER_SURFACE_EVIDENCE", "The render does not contain construction-specific cover-surface evidence."));
            return;
        }
        foreach (var artifact in physicalArtifacts.Where(item => item.MediaType == "application/pdf"))
        {
            var role = artifact.Kind switch
            {
                PublicationArtifactKind.PerfectBoundCoverPdf => "perfect-bound-outside",
                PublicationArtifactKind.CaseCoverPdf => "case-wrap",
                PublicationArtifactKind.DustJacketPdf => "dust-jacket",
                _ => null,
            };
            if (role is null) continue;
            var surface = surfaces.EnumerateArray().FirstOrDefault(item =>
                item.TryGetProperty("role", out var value) && value.GetString() == role);
            if (surface.ValueKind == JsonValueKind.Undefined)
            {
                items.Add(Error("COVER_SURFACE_EVIDENCE", $"The render is missing geometry evidence for {role}.", artifact.Kind));
                continue;
            }
            var geometry = printGeometry.Calculate(edition, interiorPageCount, role);
            RequireDimension(surface, "widthPoints", (double)geometry.SurfaceWidthInches * 72, "COVER_WIDTH", items);
            RequireDimension(surface, "heightPoints", (double)geometry.SurfaceHeightInches * 72, "COVER_HEIGHT", items);
            if (!surface.TryGetProperty("pageCount", out var pageCount)
                || pageCount.GetInt32() != (edition.PrintCoverMode == PrintCoverMode.Duplex
                    && artifact.Kind == PublicationArtifactKind.PerfectBoundCoverPdf ? 2 : 1))
                items.Add(Error("COVER_PAGE_COUNT", $"The {role} PDF has the wrong page count.", artifact.Kind));
        }
    }

    private async Task AddDigitalPressEvidenceAsync(
        PublicationEdition edition,
        PublicationArtifact? book,
        string? currentRendererVersion,
        List<PublicationPreflightItem> items,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        if (book?.RenderJobId is not Guid renderJobId)
        {
            items.Add(Error("PRESS_EVIDENCE_REQUIRED", "The current book PDF must come from a completed Lorekeeper Press render.", PublicationArtifactKind.BookPdf));
            return;
        }
        var job = await db.PublicationRenderJobs.AsNoTracking().FirstOrDefaultAsync(
            candidate => candidate.Id == renderJobId
                && candidate.EditionId == edition.Id
                && candidate.Status == PublicationRenderStatus.Completed,
            cancellationToken);
        if (job is null)
        {
            items.Add(Error("PRESS_EVIDENCE_REQUIRED", "The current book PDF does not have completed Press evidence.", PublicationArtifactKind.BookPdf));
            return;
        }
        if (currentRendererVersion is not null
            && !string.Equals(job.RendererVersion, currentRendererVersion, StringComparison.Ordinal))
        {
            items.Add(Error("PRESS_RENDERER_STALE", "The completed render evidence belongs to a different Lorekeeper Press renderer.", PublicationArtifactKind.BookPdf));
        }
        if (!string.Equals(job.ProfileId, edition.VendorProfileVersion, StringComparison.Ordinal))
            items.Add(Error("PRESS_PROFILE_STALE", "The completed render evidence belongs to a different publication profile.", PublicationArtifactKind.BookPdf));
        try
        {
            var diagnostics = JsonSerializer.Deserialize<List<PublicationRenderDiagnostic>>(
                job.DiagnosticsJson,
                JsonOptions) ?? [];
            items.AddRange(diagnostics.Select(diagnostic => new PublicationPreflightItem(
                diagnostic.Severity,
                diagnostic.Code,
                diagnostic.Message,
                PublicationArtifactKind.BookPdf,
                diagnostic.Page,
                diagnostic.SourceKind,
                diagnostic.SourceId)));

            using var evidence = JsonDocument.Parse(job.EvidenceJson);
            var root = evidence.RootElement;
            if (!root.TryGetProperty("validationStatus", out var validation)
                || validation.GetString() != "validated")
            {
                items.Add(Error("PRESS_EVIDENCE_INVALID", "Lorekeeper Press did not record a validated Digital PDF result.", PublicationArtifactKind.BookPdf));
            }
            RequireTrue(root, "interiorPageBoxesConsistent", "BOOK_PAGE_BOXES", "Book page boxes are inconsistent.", items);
            RequireFalse(root, "hasEncryption", "PDF_ENCRYPTED", "The book PDF must not be encrypted.", items);
            RequireFalse(root, "hasForbiddenActions", "PDF_FORBIDDEN_ACTIONS", "The book PDF contains forbidden actions.", items);
            if (!root.TryGetProperty("pdfVersion", out var pdfVersion) || pdfVersion.GetString() != "1.7")
                items.Add(Error("PDF_VERSION_UNSUPPORTED", "The Digital PDF profile requires PDF 1.7.", PublicationArtifactKind.BookPdf));
            if (!root.TryGetProperty("fontsEmbedded", out var embedded) || embedded.ValueKind != JsonValueKind.True)
                items.Add(Error("PDF_FONTS_NOT_EMBEDDED", "Every Digital PDF font must be embedded.", PublicationArtifactKind.BookPdf));
            if (!root.TryGetProperty("toUnicodeMapsPresent", out var toUnicode) || toUnicode.ValueKind != JsonValueKind.True)
                items.Add(Error("PDF_TOUNICODE_REQUIRED", "Digital PDF fonts require ToUnicode maps for selectable, accessible text.", PublicationArtifactKind.BookPdf));
        }
        catch (JsonException)
        {
            items.Add(Error("PRESS_EVIDENCE_INVALID", "Stored Digital PDF evidence could not be read.", PublicationArtifactKind.BookPdf));
        }
    }

    internal static void ValidateIngramColorSpaces(
        JsonElement root,
        List<PublicationPreflightItem> items)
    {
        if (!root.TryGetProperty("colorSpaces", out var colorSpaces)
            || colorSpaces.ValueKind != JsonValueKind.Array
            || colorSpaces.GetArrayLength() == 0
            || colorSpaces.EnumerateArray().Any(color =>
                color.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(color.GetString())))
        {
            items.Add(Error(
                "PDF_COLOR_SPACE_EVIDENCE_INVALID",
                "The Ingram PDFs require non-empty, well-formed color-space evidence."));
            return;
        }

        var allowed = new HashSet<string>(
            ["DeviceCMYK", "DeviceGray", "ICCBased", "Indexed", "Separation", "DeviceN"],
            StringComparer.Ordinal);
        var reported = colorSpaces.EnumerateArray().Select(color => color.GetString()!).ToList();
        if (reported.Contains("DeviceRGB", StringComparer.Ordinal))
            items.Add(Error("PDF_RGB_FORBIDDEN", "The Ingram PDFs contain DeviceRGB content."));
        var unsupported = reported.Where(color => !allowed.Contains(color)).Distinct(StringComparer.Ordinal).ToList();
        if (unsupported.Count > 0)
        {
            items.Add(Error(
                "PDF_COLOR_SPACE_EVIDENCE_INVALID",
                $"The Ingram PDFs contain unsupported or unrecognized color-space evidence: {string.Join(", ", unsupported)}."));
        }
    }

    private static void RequireTrue(
        JsonElement root,
        string property,
        string code,
        string message,
        List<PublicationPreflightItem> items)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.True)
            items.Add(Error(code, message));
    }

    private static void RequireFalse(
        JsonElement root,
        string property,
        string code,
        string message,
        List<PublicationPreflightItem> items)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.False)
            items.Add(Error(code, message));
    }

    private static void RequireZero(
        JsonElement root,
        string property,
        string code,
        string message,
        List<PublicationPreflightItem> items)
    {
        if (!root.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Number
            || value.GetInt32() != 0)
        {
            items.Add(Error(code, message));
        }
    }

    private static void RequireDimension(
        JsonElement root,
        string property,
        double expected,
        string code,
        List<PublicationPreflightItem> items)
    {
        if (!root.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Number
            || Math.Abs(value.GetDouble() - expected) > 0.25)
        {
            items.Add(Error(code, $"{property} does not match the calculated edition geometry."));
        }
    }

    internal static byte[] CreateDeterministicZip(
        IReadOnlyDictionary<string, (PublicationArtifactKind Kind, string MediaType, byte[] Data)> files)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var file in files.OrderBy(file => file.Key, StringComparer.Ordinal))
            {
                var entry = archive.CreateEntry(file.Key, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var target = entry.Open();
                target.Write(file.Value.Data);
            }
        return output.ToArray();
    }

    private static PublicationArtifact Artifact(
        Guid editionId,
        PublicationArtifactKind kind,
        string fileName,
        string mediaType,
        byte[] data,
        string fingerprint,
        string rendererVersion,
        string profileId) => new()
        {
            EditionId = editionId,
            Kind = kind,
            FileName = fileName,
            MediaType = mediaType,
            Data = data,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(data)),
            ByteLength = data.LongLength,
            SourceFingerprint = fingerprint,
            RendererVersion = rendererVersion,
            ProfileId = profileId,
        };

    private static PublicationPreflightItem Error(
        string code,
        string message,
        PublicationArtifactKind? kind = null) => new("error", code, message, kind);

    private static IReadOnlyList<PublicationCoverDiagnostic> StructuredCoverDiagnostics(
        PublicationCoverDesignView coverDesign) =>
        coverDesign.DiagnosticDetails.Count > 0
            ? coverDesign.DiagnosticDetails
            : coverDesign.Diagnostics
                .Select(message => new PublicationCoverDiagnostic(
                    message.Contains("requires", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("must contain", StringComparison.OrdinalIgnoreCase)
                        ? "error"
                        : "warning",
                    "COVER_DESIGN_LEGACY",
                    message))
                .ToList();

    private PublicationPreflightProfile ResolveProfile(PublicationEdition edition)
    {
        if (edition.Format is not (PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover))
            return Profiles.Single(candidate => candidate.Format == edition.Format && candidate.Vendor == edition.Vendor);
        var product = printArtifactProfiles.GetRequired(edition.PrintArtifactProfileKey);
        return new(
            edition.Format,
            edition.Vendor,
            product.Key,
            product.PdfProfile,
            new DateTime(2026, 8, 12, 0, 0, 0, DateTimeKind.Utc),
            product.MinimumSubmittedPages ?? product.MinimumPages,
            product.MaximumSubmittedPages ?? product.MaximumPages,
            ["Bundled Lorekeeper print-artifact profile registry " + printArtifactProfiles.Version]);
    }

    private static string ExtensionFor(string contentType) => contentType switch
    {
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/svg+xml" => ".svg",
        _ => ".png",
    };

    private sealed record EpubCoverArtifact(string MediaType, byte[] Data);

    private sealed record PublicationPreflightProfile(
        PublicationEditionFormat Format,
        PublicationVendor Vendor,
        string Id,
        string Version,
        DateTime ReviewedAtUtc,
        int MinimumPages,
        int MaximumPages,
        IReadOnlyList<string> Sources);
}
