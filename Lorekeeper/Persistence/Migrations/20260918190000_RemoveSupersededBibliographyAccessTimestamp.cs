using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Lorekeeper.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260918190000_RemoveSupersededBibliographyAccessTimestamp")]
public sealed class RemoveSupersededBibliographyAccessTimestamp : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // The preceding migration preserves the date before this obsolete storage is removed.
        migrationBuilder.Sql("ALTER TABLE \"BibliographicRecords\" DROP COLUMN \"AccessedAt\";");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE "BibliographicRecords" ADD COLUMN "AccessedAt" TEXT NULL;
            UPDATE "BibliographicRecords"
            SET "AccessedAt" = printf('%04d-%02d-%02d 00:00:00',
                "AccessedYear", COALESCE("AccessedMonth", 1), COALESCE("AccessedDay", 1))
            WHERE "AccessedYear" IS NOT NULL;
            """);
    }
}
