using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Publish;

public interface IPrintProductMigrationService
{
    Task ApplyPendingAsync(AppDbContext db, CancellationToken cancellationToken = default);
}

public sealed class PrintProductMigrationService(
    IDatabaseMigrationRecoveryService recovery,
    IPrintProductRegistry registry,
    ILogger<PrintProductMigrationService> logger) : IPrintProductMigrationService
{
    public const string MigrationName = "vendor-print-products-v1";
    public const string AdditiveMigrationId = "20260813004817_VendorPrintProductsV27";
    public const string CleanupMigrationId = "20260813010000_RemoveLegacyPrintProductColumnsV28";

    public async Task ApplyPendingAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        if (await db.PublicationEditionMigrationJournals.AsNoTracking().AnyAsync(
                item => item.MigrationName == MigrationName && item.Status == "Completed",
                cancellationToken))
            return;

        var source = await SnapshotAsync(db, cancellationToken);
        var backupPath = await recovery.CreateBackupAsync("publishing", "pre-print-products", cancellationToken);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE PublicationEditions
                SET PrintRegistryVersion = CASE
                        WHEN Format IN ('Paperback','Hardcover') THEN {registry.Version}
                        ELSE ''
                    END,
                    PrintProductKey = CASE
                        WHEN Format = 'Paperback' AND Vendor = 'AmazonKdp' AND Ink = 'Color' THEN 'kdp-pb-premium-color'
                        WHEN Format = 'Paperback' AND Vendor = 'AmazonKdp' AND Paper = 'Cream' THEN 'kdp-pb-bw-cream'
                        WHEN Format = 'Paperback' AND Vendor = 'AmazonKdp' THEN 'kdp-pb-bw-white'
                        WHEN Format = 'Paperback' AND Vendor = 'IngramSpark' AND Ink = 'Color' THEN 'ingram-pb-premium70'
                        WHEN Format = 'Paperback' AND Vendor = 'IngramSpark' AND Paper = 'Cream' THEN 'ingram-pb-bw-cream50'
                        WHEN Format = 'Paperback' AND Vendor = 'IngramSpark' THEN 'ingram-pb-bw-white50'
                        WHEN Format = 'Paperback' THEN 'generic-perfectbound-template'
                        WHEN Format = 'Hardcover' AND Vendor = 'AmazonKdp' THEN 'kdp-hc-bw-white'
                        WHEN Format = 'Hardcover' AND Vendor = 'IngramSpark' THEN 'ingram-hc-case-bw-white50'
                        WHEN Format = 'Hardcover' THEN 'generic-casebound-template'
                        ELSE ''
                    END,
                    PrintFinish = 'Matte',
                    PrintCoverMode = 'Simplex',
                    GenericPrintTemplateJson = CASE
                        WHEN Format IN ('Paperback','Hardcover') AND Vendor = 'Generic' THEN
                            json_object(
                                'trimWidthInches', PageWidthInches,
                                'trimHeightInches', PageHeightInches,
                                'bleedInches', CASE WHEN Bleed = 1 THEN 0.125 ELSE 0 END,
                                'safeInches', 0.25,
                                'wrapInches', 0,
                                'hingeInches', 0,
                                'gutterInches', 0,
                                'flapInches', 0,
                                'barcodeWidthInches', 2,
                                'barcodeHeightInches', 1.2,
                                'inchesPerPage', CASE WHEN Paper = 'Cream' THEN 0.0025 ELSE 0.002252 END,
                                'minimumPages', 2,
                                'maximumPages', 10000,
                                'pdfStandard', 'Printer-declared')
                        ELSE GenericPrintTemplateJson
                    END,
                    VendorProfileVersion = CASE
                        WHEN Format = 'Paperback' AND Vendor = 'AmazonKdp' THEN 'kdp-paperback-v2'
                        WHEN Format = 'Hardcover' AND Vendor = 'AmazonKdp' THEN 'kdp-hardcover-v1'
                        WHEN Format IN ('Paperback','Hardcover') AND Vendor = 'IngramSpark' THEN 'ingram-print-pdfx1a-v2'
                        WHEN Format IN ('Paperback','Hardcover')
                             AND VendorProfileVersion IN ('generic-paperback-v1','generic-paperback-v2','generic-hardcover-v1','generic-print-v1')
                            THEN 'generic-print-v2'
                        ELSE VendorProfileVersion
                    END;

                UPDATE PublicationArtifacts
                SET Kind = 'PerfectBoundCoverPdf', IsLegacy = 1
                WHERE Kind = 'CoverPdf';

                UPDATE PublicationArtifacts
                SET IsLegacy = 1
                WHERE Kind IN ('InteriorPdf','PerfectBoundCoverPdf','CaseCoverPdf','DustJacketPdf');
                """,
                cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            var migratedCovers = await db.PublicationCoverDesigns
                .Include(item => item.Edition)
                .ToListAsync(cancellationToken);
            foreach (var cover in migratedCovers.Where(item =>
                item.Edition.Format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover
                && !string.IsNullOrWhiteSpace(item.CompositionSceneJson)))
            {
                var role = PrimarySurface(registry.GetRequired(cover.Edition.PrintProductKey));
                cover.SurfaceScenesJson = JsonSerializer.Serialize(
                    new Dictionary<string, string>(StringComparer.Ordinal) { [role] = cover.CompositionSceneJson });
            }
            await db.SaveChangesAsync(cancellationToken);
            var target = await SnapshotAsync(db, cancellationToken);
            if (source.EditionCount != target.EditionCount
                || source.ArtifactCount != target.ArtifactCount
                || !string.Equals(source.ArtifactBytesHash, target.ArtifactBytesHash, StringComparison.Ordinal))
                throw new InvalidDataException("Print-product migration changed release counts or immutable artifact bytes/hashes.");

            db.PublicationEditionMigrationJournals.Add(new PublicationEditionMigrationJournal
            {
                MigrationName = MigrationName,
                Status = "Completed",
                BackupPath = backupPath,
                SourceProfileCount = source.EditionCount,
                EditionCount = target.EditionCount,
                SourcePlacementCount = source.ArtifactCount,
                PlacementCount = target.ArtifactCount,
                SourceHash = source.ArtifactBytesHash,
                TargetHash = target.ArtifactBytesHash,
                ValidationReportJson = JsonSerializer.Serialize(new
                {
                    registryVersion = registry.Version,
                    registrySha256 = registry.Sha256,
                    releases = "preserved",
                    artifactBytesAndHashes = "preserved",
                    legacyCovers = "reclassified",
                }),
                CompletedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Print-product migration failed; protected backup {BackupPath} remains available.", backupPath);
            db.ChangeTracker.Clear();
            await recovery.EnterRecoveryModeAsync(db, backupPath, MigrationName, 19, 20, exception, cancellationToken);
        }
    }

    private static string PrimarySurface(PrintProductDefinition product) =>
        product.RequiresPerfectBoundCover ? "perfect-bound-outside"
        : product.RequiresCaseCover ? "case-wrap"
        : product.RequiresDustJacket ? "dust-jacket"
        : product.RequiresClothManifest ? "digital-cloth-setup"
        : throw new InvalidDataException($"Print product '{product.Key}' has no cover surface.");

    private static async Task<MigrationSnapshot> SnapshotAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var editions = await db.PublicationEditions.AsNoTracking().CountAsync(cancellationToken);
        var artifacts = await db.PublicationArtifacts.AsNoTracking()
            .OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.Data, item.Sha256, item.ByteLength })
            .ToListAsync(cancellationToken);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var artifact in artifacts)
        {
            hash.AppendData(artifact.Id.ToByteArray());
            hash.AppendData(artifact.Data);
            hash.AppendData(Encoding.UTF8.GetBytes(artifact.Sha256));
            hash.AppendData(BitConverter.GetBytes(artifact.ByteLength));
        }
        return new(editions, artifacts.Count, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private sealed record MigrationSnapshot(int EditionCount, int ArtifactCount, string ArtifactBytesHash);
}
