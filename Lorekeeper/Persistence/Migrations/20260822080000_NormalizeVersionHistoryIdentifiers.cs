using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations;

/// <summary>
/// Normalizes SQLite-generated version-history identities to the uppercase
/// representation used by the EF Core SQLite Guid mapping.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260822080000_NormalizeVersionHistoryIdentifiers")]
public partial class NormalizeVersionHistoryIdentifiers : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            PRAGMA defer_foreign_keys = ON;

            UPDATE "ProjectVersionCheckpoints"
            SET "ProjectVersionRepositoryId" = upper("ProjectVersionRepositoryId");

            UPDATE "ProjectVersionOperations"
            SET "ProjectVersionRepositoryId" = upper("ProjectVersionRepositoryId");

            UPDATE "ProjectGitRemotes"
            SET "ProjectVersionRepositoryId" = upper("ProjectVersionRepositoryId");

            UPDATE "ProjectReferences"
            SET
                "Id" = upper("Id"),
                "ReferencedRepositoryId" = upper("ReferencedRepositoryId");

            UPDATE "ProjectVersionRepositories"
            SET "Id" = upper("Id");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Identifier casing is a storage normalization. Reverting it would
        // make EF Core Guid lookups fail for migrated rows.
    }
}
