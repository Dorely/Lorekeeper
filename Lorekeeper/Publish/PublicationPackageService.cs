using System.IO.Compression;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
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
    PublicationArtifactKind? ArtifactKind = null);

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
    PublicationProofStatus DigitalProof,
    PublicationProofStatus PhysicalProof,
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

public sealed record PublicationProofStatus(
    string Status,
    string? PackageSha256,
    DateTime? RecordedAtUtc,
    string? Note,
    IReadOnlyList<string> Checklist);

[JsonConverter(typeof(JsonStringEnumConverter<PublicationProofKind>))]
public enum PublicationProofKind
{
    Digital,
    Physical,
}

public sealed record PublicationProofRecord(
    PublicationProofKind Kind,
    string SourceFingerprint,
    Guid PackageArtifactId,
    string PackageSha256,
    DateTime RecordedAtUtc,
    string Note);

public sealed record PublicationPackageResult(
    PublicationPreflightReport Preflight,
    IReadOnlyList<PublicationArtifactView> Artifacts);

public interface IPublicationPackageService
{
    Task<PublicationPreflightReport> PreflightAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken = default);
    Task<PublicationPackageResult> BuildAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken = default);
    Task<PublicationPreflightReport> RecordProofAsync(
        Guid projectId,
        Guid editionId,
        Guid packageArtifactId,
        PublicationProofKind kind,
        string note,
        CancellationToken cancellationToken = default);
}

public sealed class PublicationPackageService(
    AppDbContext db,
    IPublishService publishing,
    IPublicationEditionService editions,
    IPublicationCoverService covers,
    IProjectMutationCoordinator projectMutations,
    IPublicationPressRuntime? pressRuntime = null) : IPublicationPackageService
{
    private const string AssemblerVersion = "lorekeeper-package-v1";
    private const string EpubExporterVersion = "lorekeeper-epub-v2";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private static readonly string[] PaperbackDigitalProofChecklist =
    [
        "Inspect the complete interior and cover PDFs at page level.",
        "Confirm title, author, identifiers, page order, and intentional blank pages.",
        "Confirm the package manifest hashes match the files being approved.",
    ];
    private static readonly string[] EpubDigitalProofChecklist =
    [
        "Open the exact packaged EPUB in representative readers and inspect navigation, links, reflow, images, and alternative text.",
        "Confirm title, author, identifiers, reading order, landmarks, and intentional page breaks.",
        "Confirm the package manifest hashes match the files being approved.",
    ];
    private static readonly string[] PhysicalProofChecklist =
    [
        "Inspect trim, bleed, crop, binding, spine alignment, and cover safety.",
        "Inspect type density, margins, running elements, blank pages, images, and color.",
        "Scan the printed barcode and confirm its human-readable ISBN.",
        "Record corrections or confirm that this exact package needs no physical changes.",
    ];
    private static readonly IReadOnlyList<PublicationPreflightProfile> Profiles =
    [
        new(
            PublicationEditionFormat.Paperback,
            PublicationVendor.AmazonKdp,
            "amazon-kdp-paperback",
            "kdp-paperback-v1",
            new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc),
            24,
            800,
            [
                "https://kdp.amazon.com/en_US/help/topic/G201857950",
                "https://kdp.amazon.com/en_US/help/topic/G201953020",
            ]),
        new(
            PublicationEditionFormat.Paperback,
            PublicationVendor.IngramSpark,
            "ingramspark-paperback",
            "ingram-paperback-pdfx1a-v1",
            new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc),
            18,
            800,
            ["https://www.ingramspark.com/hubfs/downloads/file-creation-guide.pdf"]),
        new(
            PublicationEditionFormat.Paperback,
            PublicationVendor.Generic,
            "generic-paperback",
            "generic-paperback-v1",
            new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc),
            2,
            800,
            ["https://www.pdfa.org/wp-content/uploads/2017/05/PDFX-in-a-Nutshell.pdf"]),
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
        var edition = await db.PublicationEditions.AsNoTracking().FirstOrDefaultAsync(
            candidate => candidate.Id == editionId && candidate.ProjectId == projectId,
            cancellationToken) ?? throw new KeyNotFoundException("Publication edition not found.");
        var fingerprint = await editions.GetSourceFingerprintAsync(projectId, editionId, cancellationToken);
        var artifacts = await db.PublicationArtifacts.AsNoTracking()
            .Where(artifact => artifact.EditionId == editionId
                && (artifact.Kind == PublicationArtifactKind.InteriorPdf
                    || artifact.Kind == PublicationArtifactKind.CoverPdf))
            .OrderByDescending(artifact => artifact.CreatedAt)
            .ToListAsync(cancellationToken);
        var interior = artifacts.FirstOrDefault(artifact => artifact.Kind == PublicationArtifactKind.InteriorPdf);
        var coverArtifact = artifacts.FirstOrDefault(artifact => artifact.Kind == PublicationArtifactKind.CoverPdf);
        var items = new List<PublicationPreflightItem>();
        var profile = Profiles.Single(candidate =>
            candidate.Format == edition.Format && candidate.Vendor == edition.Vendor);
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
        if (edition.Format == PublicationEditionFormat.Paperback)
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
            ValidatePdf(coverArtifact, fingerprint, currentRendererVersion, edition.VendorProfileVersion, PublicationArtifactKind.CoverPdf, "COVER", items);
            if (coverArtifact is not null && coverArtifact.PageCount != 1)
                items.Add(Error("COVER_PAGE_COUNT_INVALID", "The full-wrap cover PDF must contain exactly one page.", PublicationArtifactKind.CoverPdf));
            if (interior?.IsLegacy == true || coverArtifact?.IsLegacy == true)
            {
                items.Add(Error(
                    "PRESS_RENDERER_LEGACY",
                    "Legacy PDFs remain downloadable but cannot satisfy current validation or package readiness."));
            }
            coverDesign = await covers.GetAsync(projectId, editionId, cancellationToken);
            items.AddRange(coverDesign.Diagnostics.Select(message =>
                new PublicationPreflightItem(
                    message.Contains("requires", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("must contain", StringComparison.OrdinalIgnoreCase)
                        ? "error"
                        : "warning",
                    "COVER_DESIGN",
                    message,
                    PublicationArtifactKind.CoverPdf)));
            if (!coverDesign.Template.IsAcknowledged)
                items.Add(Error("COVER_TEMPLATE_ACK_REQUIRED", "Acknowledge the current cover template.", PublicationArtifactKind.CoverPdf));
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
            await AddPressEvidenceAsync(edition, interior, coverArtifact, coverDesign.Template, currentRendererVersion, items, cancellationToken);
        }
        var document = await publishing.GetDocumentAsync(projectId, editionId, cancellationToken);
        ValidateProfile(edition, profile, interior, document, coverDesign, items);
        var matter = await db.PublicationMatter.AsNoTracking()
            .Where(item => item.EditionId == editionId && item.IsIncluded)
            .OrderBy(item => item.Location)
            .ThenBy(item => item.SortOrder)
            .ToListAsync(cancellationToken);
        foreach (var item in matter.Where(item => PublicationMatterFormatting.IsGeneratedPageKind(item.Kind)))
        {
            items.Add(Error(
                "MATTER_GENERATED_PAGE_CONFLICT",
                $"{item.Kind} is generated from edition settings and cannot also be included as publication matter."));
        }
        ValidateLanguageScope(document, coverDesign, matter, items);
        if (!string.Equals(edition.Language, "en", StringComparison.OrdinalIgnoreCase)
            && !edition.Language.StartsWith("en-", StringComparison.OrdinalIgnoreCase))
            items.Add(Error("LANGUAGE_SCOPE_UNSUPPORTED", "Lorekeeper Press 1.0 supports English/Latin left-to-right publishing only."));
        items.Add(new(
            "info",
            "LOREKEEPER_VALIDATED_SCOPE",
            "Lorekeeper validation covers the generated file structure and selected profile. Human proof and recorded vendor results remain separate evidence."));
        var packageIdentity = PackageIdentity(profile, fingerprint, interior, coverArtifact);
        var currentPackageEntity = await db.PublicationArtifacts.AsNoTracking()
            .Where(artifact => artifact.EditionId == editionId
                && artifact.Kind == PublicationArtifactKind.PublicationPackage
                && !artifact.IsLegacy
                && artifact.SourceFingerprint == fingerprint
                && artifact.RendererVersion == RuntimeVersion(profile.Format)
                && artifact.ProfileId == packageIdentity)
            .OrderByDescending(artifact => artifact.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (currentPackageEntity is not null && !ArtifactBytesMatch(currentPackageEntity))
        {
            items.Add(Error("PACKAGE_ARTIFACT_INVALID", "The stored publication package failed its length or SHA-256 check."));
            currentPackageEntity = null;
        }
        var currentPackage = currentPackageEntity is null ? null : ArtifactView(currentPackageEntity, false);
        var (digitalProof, physicalProof) = await ReadProofStatusAsync(
            editionId,
            fingerprint,
            currentPackageEntity,
            edition.Format,
            cancellationToken);
        if (edition.Format == PublicationEditionFormat.Epub)
            physicalProof = NotApplicableProofStatus();
        if (items.Any(item => item.Code is
                "PRESS_RENDERER_STALE" or
                "PRESS_PROFILE_STALE" or
                "PRESS_RUNTIME_UNAVAILABLE"))
        {
            currentPackage = null;
            digitalProof = ProofStatus(null, DigitalProofChecklist(edition.Format));
            physicalProof = edition.Format == PublicationEditionFormat.Paperback
                ? ProofStatus(null, PhysicalProofChecklist)
                : NotApplicableProofStatus();
        }
        var finalFingerprint = await editions.GetSourceFingerprintAsync(projectId, editionId, cancellationToken);
        if (!string.Equals(finalFingerprint, fingerprint, StringComparison.Ordinal))
        {
            items.Add(Error("PREFLIGHT_SOURCE_CHANGED", "The edition changed during preflight. Run it again."));
            currentPackage = null;
            digitalProof = ProofStatus(null, DigitalProofChecklist(edition.Format));
            physicalProof = edition.Format == PublicationEditionFormat.Paperback
                ? ProofStatus(null, PhysicalProofChecklist)
                : NotApplicableProofStatus();
        }
        return new(
            profile.Id,
            profile.Version,
            profile.ReviewedAtUtc,
            profile.Sources,
            fingerprint,
            packageIdentity,
            edition.Format == PublicationEditionFormat.Paperback
                ? artifacts.Where(artifact => artifact.Id == interior?.Id || artifact.Id == coverArtifact?.Id)
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
            digitalProof,
            physicalProof,
            items)
        {
            ValidatedArtifactIds = edition.Format == PublicationEditionFormat.Paperback
                ? new[] { interior?.Id ?? Guid.Empty, coverArtifact?.Id ?? Guid.Empty }
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
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var report = await PreflightAsync(projectId, editionId, cancellationToken);
        if (!report.CanPackage)
            throw new InvalidOperationException("Publication package is blocked by preflight errors.");
        var edition = await db.PublicationEditions.AsNoTracking().SingleAsync(
            candidate => candidate.Id == editionId && candidate.ProjectId == projectId,
            cancellationToken);
        PublicationEditionService.EnsureDraft(edition);
        var validatedArtifactIds = report.ValidatedArtifactIds;
        var sourceArtifacts = await db.PublicationArtifacts.AsNoTracking()
            .Where(artifact => artifact.EditionId == editionId
                && !artifact.IsLegacy
                && validatedArtifactIds.Contains(artifact.Id))
            .ToListAsync(cancellationToken);
        if (sourceArtifacts.Count != validatedArtifactIds.Count
            || sourceArtifacts.Any(artifact => !ArtifactBytesMatch(artifact)))
        {
            throw new InvalidOperationException("A preflighted press artifact is missing or failed its hash check.");
        }
        var interiorArtifact = sourceArtifacts.FirstOrDefault(artifact => artifact.Kind == PublicationArtifactKind.InteriorPdf);
        var coverArtifact = sourceArtifacts.FirstOrDefault(artifact => artifact.Kind == PublicationArtifactKind.CoverPdf);
        var profile = Profiles.Single(candidate =>
            candidate.Format == edition.Format && candidate.Vendor == edition.Vendor);
        var packageIdentity = PackageIdentity(profile, report.SourceFingerprint, interiorArtifact, coverArtifact);
        if (!string.Equals(packageIdentity, report.PackageIdentity, StringComparison.Ordinal))
            throw new InvalidOperationException("The press artifacts changed after preflight. Run preflight again.");
        var files = new Dictionary<string, (PublicationArtifactKind Kind, string MediaType, byte[] Data)>(
            StringComparer.Ordinal);
        if (edition.Format == PublicationEditionFormat.Paperback)
        {
            files["interior.pdf"] = (PublicationArtifactKind.InteriorPdf, "application/pdf", interiorArtifact!.Data);
            files["cover.pdf"] = (PublicationArtifactKind.CoverPdf, "application/pdf", coverArtifact!.Data);
        }
        if (edition.Format == PublicationEditionFormat.Epub)
        {
            var epub = await publishing.ExportAsync(projectId, editionId, PublishExportFormat.Epub, cancellationToken);
            var normalizedEpub = NormalizeEpub(epub.Content);
            ValidateEpubStructure(normalizedEpub);
            files["book.epub"] = (PublicationArtifactKind.Epub, epub.ContentType, normalizedEpub);
        }

        var document = await publishing.GetDocumentAsync(projectId, editionId, cancellationToken);
        if (document.CoverAsset is { } frontCover)
            files[$"front-cover{ExtensionFor(frontCover.ContentType)}"] =
                (PublicationArtifactKind.FrontCoverImage, frontCover.ContentType, frontCover.Data);
        var packagedReport = report with
        {
            CurrentPackage = null,
            DigitalProof = ProofStatus(null, DigitalProofChecklist(edition.Format)),
            PhysicalProof = edition.Format == PublicationEditionFormat.Paperback
                ? ProofStatus(null, PhysicalProofChecklist)
                : NotApplicableProofStatus(),
        };
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
                    || artifact.Kind == PublicationArtifactKind.CoverPdf))
            .OrderByDescending(artifact => artifact.CreatedAt)
            .ToListAsync(cancellationToken);
        var finalPackageIdentity = PackageIdentity(
            profile,
            report.SourceFingerprint,
            latestArtifacts.FirstOrDefault(artifact => artifact.Kind == PublicationArtifactKind.InteriorPdf),
            latestArtifacts.FirstOrDefault(artifact => artifact.Kind == PublicationArtifactKind.CoverPdf));
        if (!string.Equals(finalPackageIdentity, packageIdentity, StringComparison.Ordinal))
            throw new InvalidOperationException("The press artifacts changed while the publication package was being built.");
        var generated = new List<PublicationArtifact>();
        foreach (var file in files.Where(file => file.Value.Kind is not PublicationArtifactKind.InteriorPdf and not PublicationArtifactKind.CoverPdf))
            generated.Add(Artifact(
                editionId,
                file.Value.Kind,
                file.Key,
                file.Value.MediaType,
                file.Value.Data,
                report.SourceFingerprint,
                RuntimeVersion(edition.Format),
                packageIdentity));
        generated.Add(Artifact(
            editionId,
            PublicationArtifactKind.PublicationPackage,
            "publication-package.zip",
            "application/zip",
            packageData,
            report.SourceFingerprint,
            RuntimeVersion(edition.Format),
            packageIdentity));
        db.PublicationArtifacts.AddRange(generated);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var packageView = ArtifactView(
            generated.Single(artifact => artifact.Kind == PublicationArtifactKind.PublicationPackage),
            false);
        return new(
            report with
            {
                CurrentPackage = packageView,
                DigitalProof = ProofStatus(null, DigitalProofChecklist(edition.Format)),
                PhysicalProof = edition.Format == PublicationEditionFormat.Paperback
                    ? ProofStatus(null, PhysicalProofChecklist)
                    : NotApplicableProofStatus(),
            },
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

    public async Task<PublicationPreflightReport> RecordProofAsync(
        Guid projectId,
        Guid editionId,
        Guid packageArtifactId,
        PublicationProofKind kind,
        string note,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentException("Proof kind is invalid.", nameof(kind));
        var cleanNote = note.Trim();
        if (cleanNote.Length > 2_000)
            throw new ArgumentException("Proof notes cannot exceed 2,000 characters.", nameof(note));
        if (kind == PublicationProofKind.Physical && cleanNote.Length == 0)
            throw new ArgumentException("Record a physical-proof note.", nameof(note));

        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var edition = await db.PublicationEditions.AsNoTracking().FirstOrDefaultAsync(
            candidate => candidate.Id == editionId && candidate.ProjectId == projectId,
            cancellationToken) ?? throw new KeyNotFoundException("Publication edition not found.");
        PublicationEditionService.EnsureDraft(edition);
        if (kind == PublicationProofKind.Physical
            && edition.Format != PublicationEditionFormat.Paperback)
        {
            throw new InvalidOperationException("Physical proof approval applies only to paperback editions.");
        }
        var report = await PreflightAsync(projectId, editionId, cancellationToken);
        if (!report.CanPackage)
            throw new InvalidOperationException("The edition no longer passes preflight.");
        if (report.CurrentPackage?.Id != packageArtifactId)
            throw new InvalidOperationException("The selected publication package is no longer current.");
        var fingerprint = report.SourceFingerprint;
        var package = await db.PublicationArtifacts.FirstOrDefaultAsync(
            artifact => artifact.Id == packageArtifactId
                && artifact.EditionId == editionId
                && artifact.Edition.ProjectId == projectId
                && artifact.Kind == PublicationArtifactKind.PublicationPackage
                && !artifact.IsLegacy,
            cancellationToken) ?? throw new KeyNotFoundException("Publication package not found.");
        if (kind == PublicationProofKind.Physical)
        {
            var digitalRecorded = (await ReadProofStatusAsync(
                editionId,
                fingerprint,
                package,
                edition.Format,
                cancellationToken))
                .Digital.PackageSha256;
            if (!string.Equals(digitalRecorded, package.Sha256, StringComparison.Ordinal))
                throw new InvalidOperationException("Record the digital proof for this exact package before the physical proof.");
        }
        var record = new PublicationProofRecord(
            kind,
            fingerprint,
            package.Id,
            package.Sha256,
            DateTime.UtcNow,
            cleanNote);
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var finalFingerprint = await editions.GetSourceFingerprintAsync(projectId, editionId, cancellationToken);
        if (!string.Equals(finalFingerprint, fingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("The edition changed while proof approval was being recorded.");
        db.PublicationArtifacts.Add(Artifact(
            editionId,
            PublicationArtifactKind.ProofRecord,
            $"{kind.ToString().ToLowerInvariant()}-proof.json",
            "application/json",
            JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions),
            fingerprint,
            package.RendererVersion,
            package.ProfileId));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await PreflightAsync(projectId, editionId, cancellationToken);
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

    private static void ValidateProfile(
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
        if (chapters.Any(chapter => string.IsNullOrWhiteSpace(chapter.PlainText)))
            items.Add(Error("CONTENT_CHAPTER_EMPTY", "Every included chapter must contain publication text."));

        if (edition.Format == PublicationEditionFormat.Epub)
        {
            if (edition.Binding != PublicationBinding.Digital
                || edition.Paper != PublicationPaper.Digital
                || edition.Ink != PublicationInk.Digital)
            {
                items.Add(Error("EPUB_PRODUCT_SETTINGS", "EPUB editions require digital binding, paper, and ink settings."));
            }
            if (document.CoverAsset is null)
                items.Add(Error("EPUB_COVER_REQUIRED", "Select a front-cover source for the EPUB edition."));
            else if (string.IsNullOrWhiteSpace(document.CoverAsset.AltText))
                items.Add(Error("EPUB_COVER_ALT_TEXT_REQUIRED", "The EPUB cover requires alternative text."));
            var missingAssetAlt = document.Assets
                .Concat(document.Placements.Select(placement => placement.Asset))
                .DistinctBy(asset => asset.Id)
                .Any(asset => string.IsNullOrWhiteSpace(asset.AltText));
            var semanticManuscripts = chapters.Select(chapter => chapter.Manuscript)
                .Concat(document.Matter.Select(item => item.Manuscript));
            var missingFigureAlt = semanticManuscripts.Any(manuscript =>
                    manuscript.Content.Any(block =>
                        block.Type == ManuscriptBlockType.Figure
                        && (string.IsNullOrWhiteSpace(block.AltText)
                            || block.ImageId is not Guid imageId
                            || document.Assets.All(asset => asset.Id != imageId))))
                || chapters.Any(chapter =>
                    chapter.IllustrationLayout.Images.Any(image =>
                        string.IsNullOrWhiteSpace(image.AltTextOverride)
                        && string.IsNullOrWhiteSpace(document.Assets.FirstOrDefault(asset => asset.Id == image.ImageId)?.AltText)));
            if (missingAssetAlt || missingFigureAlt)
                items.Add(Error("EPUB_ALT_TEXT_REQUIRED", "Every EPUB image requires alternative text."));
            return;
        }

        if (edition.Binding != PublicationBinding.PerfectBound)
            items.Add(Error("PRINT_BINDING_UNSUPPORTED", "The current paperback profiles support perfect binding only."));
        if (edition.Paper is not PublicationPaper.White and not PublicationPaper.Cream)
            items.Add(Error("PRINT_PAPER_UNSUPPORTED", "The current paperback profiles support white or cream paper."));
        if (edition.Ink != PublicationInk.BlackAndWhite)
            items.Add(Error("PRINT_INK_UNSUPPORTED", "The current paperback profiles support black-and-white interiors only."));
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
            "BLANK_PAGE_PROOF_REQUIRED",
            "Confirm intentional and accidental blank pages in the exact digital proof before approval."));
    }

    private static string PackageIdentity(
        PublicationPreflightProfile profile,
        string sourceFingerprint,
        PublicationArtifact? interior,
        PublicationArtifact? cover)
    {
        if (profile.Format == PublicationEditionFormat.Epub)
        {
            interior = null;
            cover = null;
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
            cover?.Sha256 ?? string.Empty,
            cover?.RendererVersion ?? string.Empty,
            cover?.ProfileId ?? string.Empty);
        return $"package:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)))}";
    }

    private static void ValidateLanguageScope(
        PublishDocument document,
        PublicationCoverDesignView? coverDesign,
        IReadOnlyList<PublicationMatter> matter,
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
            chapter.IllustrationLayout.Images.SelectMany(image => new[]
            {
                ($"chapter {chapter.Id:N} illustration {image.Id:N} caption", image.Caption),
                ($"chapter {chapter.Id:N} illustration {image.Id:N} alternative text override", image.AltTextOverride),
            })));
        renderedText.AddRange(document.Placements.SelectMany(placement =>
            new[]
            {
                ($"placement {placement.Id:N} caption", placement.Caption),
                ($"asset {placement.Asset.Id:N} alternative text", placement.Asset.AltText),
            }));
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
        foreach (var item in matter)
        {
            try
            {
                var manuscript = ManuscriptCodec.Deserialize(item.ManuscriptJson, item.Id, item.Revision);
                ValidateManuscriptLanguage($"Matter {item.Id:N} '{item.Title}'", manuscript, items);
                if (item.Title.EnumerateRunes().Any(rune => !IsSupportedLatinRune(rune)))
                    items.Add(Error("SCRIPT_SCOPE_UNSUPPORTED", $"Matter {item.Id:N} title is outside the tested Latin-script LTR scope."));
            }
            catch (JsonException)
            {
                items.Add(Error("MATTER_MANUSCRIPT_INVALID", $"Matter {item.Id:N} could not be validated."));
            }
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
        !string.IsNullOrWhiteSpace(value)
        && (string.Equals(value, "en", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("en-", StringComparison.OrdinalIgnoreCase));

    private static bool IsSupportedLatinRune(Rune rune) =>
        rune.Value <= 0x024F
        || rune.Value is >= 0x0300 and <= 0x036F
        || rune.Value is >= 0x2000 and <= 0x206F
        || rune.Value is >= 0x20A0 and <= 0x20CF
        || rune.Value is >= 0x2100 and <= 0x214F
        || rune.Value == 0xFEFF;

    private async Task<(PublicationProofStatus Digital, PublicationProofStatus Physical)> ReadProofStatusAsync(
        Guid editionId,
        string fingerprint,
        PublicationArtifact? currentPackage,
        PublicationEditionFormat format,
        CancellationToken cancellationToken)
    {
        if (currentPackage is null)
        {
            return (
                ProofStatus(null, DigitalProofChecklist(format)),
                ProofStatus(null, PhysicalProofChecklist));
        }
        var records = await db.PublicationArtifacts.AsNoTracking()
            .Where(artifact => artifact.EditionId == editionId
                && artifact.Kind == PublicationArtifactKind.ProofRecord
                && !artifact.IsLegacy
                && artifact.SourceFingerprint == fingerprint
                && artifact.RendererVersion == currentPackage.RendererVersion
                && artifact.ProfileId == currentPackage.ProfileId)
            .OrderByDescending(artifact => artifact.CreatedAt)
            .ToListAsync(cancellationToken);
        var parsed = new List<PublicationProofRecord>();
        foreach (var artifact in records.Where(ArtifactBytesMatch))
        {
            try
            {
                if (JsonSerializer.Deserialize<PublicationProofRecord>(artifact.Data, JsonOptions) is { } record
                    && string.Equals(record.SourceFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    if (record.PackageArtifactId == currentPackage.Id
                        && string.Equals(record.PackageSha256, currentPackage.Sha256, StringComparison.Ordinal))
                    {
                        parsed.Add(record);
                    }
                }
            }
            catch (JsonException)
            {
            }
        }
        return (
            ProofStatus(
                parsed.FirstOrDefault(record => record.Kind == PublicationProofKind.Digital),
                DigitalProofChecklist(format)),
            ProofStatus(parsed.FirstOrDefault(record => record.Kind == PublicationProofKind.Physical), PhysicalProofChecklist));
    }

    private static IReadOnlyList<string> DigitalProofChecklist(PublicationEditionFormat format) =>
        format == PublicationEditionFormat.Epub
            ? EpubDigitalProofChecklist
            : PaperbackDigitalProofChecklist;

    private static PublicationProofStatus ProofStatus(
        PublicationProofRecord? record,
        IReadOnlyList<string> checklist) => record is null
            ? new("Pending", null, null, null, checklist)
            : new("Recorded", record.PackageSha256, record.RecordedAtUtc, record.Note, checklist);

    private static PublicationProofStatus NotApplicableProofStatus() =>
        new("Not applicable", null, null, null, []);

    private static string RuntimeVersion(PublicationEditionFormat format) =>
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
        PublicationArtifact? cover,
        PublicationCoverTemplate template,
        string? currentRendererVersion,
        List<PublicationPreflightItem> items,
        CancellationToken cancellationToken)
    {
        if (interior?.RenderJobId is not Guid renderJobId
            || cover?.RenderJobId != renderJobId)
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
                    "cover-pdf" => PublicationArtifactKind.CoverPdf,
                    "interior-pdf" => PublicationArtifactKind.InteriorPdf,
                    _ => null,
                })));

            using var evidence = JsonDocument.Parse(job.EvidenceJson);
            var root = evidence.RootElement;
            RequireTrue(root, "interiorPageBoxesConsistent", "INTERIOR_PAGE_BOXES", "Interior page boxes are inconsistent.", items);
            RequireTrue(root, "coverPageBoxesConsistent", "COVER_PAGE_BOXES", "Cover page boxes are inconsistent.", items);
            RequireFalse(root, "hasEncryption", "PDF_ENCRYPTED", "Publication PDFs must not be encrypted.", items);
            RequireFalse(root, "hasForbiddenActions", "PDF_FORBIDDEN_ACTIONS", "Publication PDFs contain forbidden actions.", items);
            RequireZero(root, "annotationCount", "PDF_ANNOTATIONS", "Publication PDFs contain annotations.", items);
            RequireDimension(root, "interiorWidthPoints", edition.PageWidthInches * 72, "INTERIOR_WIDTH", items);
            RequireDimension(root, "interiorHeightPoints", edition.PageHeightInches * 72, "INTERIOR_HEIGHT", items);
            RequireDimension(root, "coverWidthPoints", template.FullWidthInches * 72, "COVER_WIDTH", items);
            RequireDimension(root, "coverHeightPoints", template.FullHeightInches * 72, "COVER_HEIGHT", items);
            var expectedPdfVersion = edition.Vendor == PublicationVendor.IngramSpark ? "1.3" : "1.7";
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
                    || outputIntentCount.GetInt32() != 2)
                {
                    items.Add(Error("PDF_OUTPUT_INTENT_REQUIRED", "Both Ingram PDFs require an output intent."));
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
            if (currentRendererVersion is not null && edition.Ink == PublicationInk.BlackAndWhite)
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
                    "Review effective image resolution in the rendered page map and proof checklist."));
            }
        }
        catch (JsonException)
        {
            items.Add(Error("PRESS_EVIDENCE_INVALID", "Stored press evidence could not be read."));
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

    private static string ExtensionFor(string contentType) => contentType switch
    {
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        _ => ".png",
    };

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
