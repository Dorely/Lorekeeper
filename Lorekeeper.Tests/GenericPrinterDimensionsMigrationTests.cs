using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class GenericPrinterDimensionsMigrationTests
{
    private const string PreviousMigration = "20260922162637_AddWorldWorkspace";

    [Fact]
    public async Task Upgrade_maps_estimated_generic_profiles_to_user_dimensioned_profiles_and_leaves_named_vendors_alone()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var projectId = Guid.NewGuid();
        var legacy = new Dictionary<string, string>
        {
            ["generic-pb-bw-50-white"] = "generic-pb-bw",
            ["generic-pb-bw-60-cream"] = "generic-pb-bw",
            ["generic-pb-stdcolor-60-white"] = "generic-pb-stdcolor",
            ["generic-pb-premcolor-80-white"] = "generic-pb-premcolor",
            ["generic-case-bw-50-white"] = "generic-case-bw",
            ["generic-case-bw-60-cream"] = "generic-case-bw",
            ["generic-case-stdcolor-60-white"] = "generic-case-stdcolor",
            ["generic-case-premcolor-80-white"] = "generic-case-premcolor",
        };
        var editionKeys = legacy.Keys.ToDictionary(_ => Guid.NewGuid(), key => key);
        var kdpEditionId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(directory, "generic-print.db")};Pooling=False")
            .Options;
        try
        {
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.Database.MigrateAsync();
                db.Projects.Add(new Project { Id = projectId, Name = "Generic print fixture", Slug = "generic-print-fixture" });
                foreach (var (id, key) in editionKeys)
                {
                    db.PublicationEditions.Add(new PublicationEdition
                    {
                        Id = id,
                        ProjectId = projectId,
                        Name = key,
                        Format = key.StartsWith("generic-case", StringComparison.Ordinal) ? PublicationEditionFormat.Hardcover : PublicationEditionFormat.Paperback,
                        Vendor = PublicationVendor.Generic,
                        PrintArtifactRegistryVersion = "2026.09.3",
                        PrintArtifactProfileKey = key,
                        PageWidthInches = 6,
                        PageHeightInches = 9,
                    });
                }
                db.PublicationEditions.Add(new PublicationEdition
                {
                    Id = kdpEditionId,
                    ProjectId = projectId,
                    Name = "KDP",
                    Vendor = PublicationVendor.AmazonKdp,
                    VendorProfileVersion = "kdp-paperback-v2",
                    PrintArtifactRegistryVersion = "2026.09.3",
                    PrintArtifactProfileKey = "kdp-pb-bw-50-2252",
                });
                await db.SaveChangesAsync();
                await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.Database.MigrateAsync();
                var editions = await db.PublicationEditions.AsNoTracking().ToDictionaryAsync(edition => edition.Id);
                foreach (var (id, key) in editionKeys)
                {
                    var edition = editions[id];
                    Assert.Equal(legacy[key], edition.PrintArtifactProfileKey);
                    Assert.Equal("2026.10.1", edition.PrintArtifactRegistryVersion);
                    Assert.Null(PrinterDimensions.From(edition));
                }
                var kdp = editions[kdpEditionId];
                Assert.Equal("kdp-pb-bw-50-2252", kdp.PrintArtifactProfileKey);
                Assert.Equal("2026.09.3", kdp.PrintArtifactRegistryVersion);
                Assert.Null(PrinterDimensions.From(kdp));
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
