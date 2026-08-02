using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260802180000_AddAuthoringPageSetupV19")]
public sealed class AddAuthoringPageSetupV19 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.RenameColumn(name: "ImageFocalXPercent", table: "PublicationCoverDesigns", newName: "ImageCropXPercent");
        migrationBuilder.RenameColumn(name: "ImageFocalYPercent", table: "PublicationCoverDesigns", newName: "ImageCropYPercent");

        migrationBuilder.AddColumn<Guid>(
            name: "ActiveAuthoringVariantId",
            table: "PageCompositions",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "ProjectPageSetups",
            columns: table => new
            {
                ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                PageWidthInches = table.Column<double>(type: "REAL", nullable: false),
                PageHeightInches = table.Column<double>(type: "REAL", nullable: false),
                PageMarginInches = table.Column<double>(type: "REAL", nullable: false),
                BodyFontSizePoints = table.Column<double>(type: "REAL", nullable: false),
                BodyLineHeight = table.Column<double>(type: "REAL", nullable: false),
                Revision = table.Column<long>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProjectPageSetups", item => item.ProjectId);
                table.ForeignKey(
                    name: "FK_ProjectPageSetups_Projects_ProjectId",
                    column: item => item.ProjectId,
                    principalTable: "Projects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_PageCompositions_ActiveAuthoringVariantId",
            table: "PageCompositions",
            column: "ActiveAuthoringVariantId");

        migrationBuilder.Sql("""
            UPDATE PageCompositions
            SET ActiveAuthoringVariantId = (
                SELECT Id FROM PageCompositionVariants
                WHERE PageCompositionVariants.CompositionId = PageCompositions.Id
                ORDER BY UpdatedAt DESC, Id DESC LIMIT 1)
            """);

        migrationBuilder.Sql("""
            CREATE TRIGGER TR_PageCompositions_ActiveAuthoringVariant_Update
            BEFORE UPDATE OF ActiveAuthoringVariantId ON PageCompositions
            WHEN NEW.ActiveAuthoringVariantId IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM PageCompositionVariants
                  WHERE Id = NEW.ActiveAuthoringVariantId AND CompositionId = NEW.Id)
            BEGIN
                SELECT RAISE(ABORT, 'Active authoring variant must belong to the composition.');
            END
            """);
        migrationBuilder.Sql("""
            CREATE TRIGGER TR_PageCompositionVariants_ClearAuthoringSelection
            AFTER DELETE ON PageCompositionVariants
            BEGIN
                UPDATE PageCompositions SET ActiveAuthoringVariantId = NULL
                WHERE ActiveAuthoringVariantId = OLD.Id;
            END
            """);

        migrationBuilder.Sql("""
            INSERT INTO ProjectPageSetups (
                ProjectId, PageWidthInches, PageHeightInches, PageMarginInches,
                BodyFontSizePoints, BodyLineHeight, Revision, CreatedAt, UpdatedAt)
            SELECT Projects.Id,
                COALESCE(
                    (SELECT json_extract(PageCompositionVariants.SceneJson, '$.surface.widthPoints') / 72.0
                     FROM PageCompositionVariants
                     JOIN PageCompositions ON PageCompositions.Id = PageCompositionVariants.CompositionId
                     WHERE PageCompositions.ProjectId = Projects.Id
                       AND json_valid(PageCompositionVariants.SceneJson)
                     ORDER BY PageCompositionVariants.UpdatedAt DESC LIMIT 1),
                    (SELECT PageWidthInches FROM PublicationEditions
                     WHERE PublicationEditions.ProjectId = Projects.Id
                     ORDER BY IsDefault DESC, UpdatedAt DESC LIMIT 1),
                    6.0),
                COALESCE(
                    (SELECT json_extract(PageCompositionVariants.SceneJson, '$.surface.heightPoints') / 72.0
                     FROM PageCompositionVariants
                     JOIN PageCompositions ON PageCompositions.Id = PageCompositionVariants.CompositionId
                     WHERE PageCompositions.ProjectId = Projects.Id
                       AND json_valid(PageCompositionVariants.SceneJson)
                     ORDER BY PageCompositionVariants.UpdatedAt DESC LIMIT 1),
                    (SELECT PageHeightInches FROM PublicationEditions
                     WHERE PublicationEditions.ProjectId = Projects.Id
                     ORDER BY IsDefault DESC, UpdatedAt DESC LIMIT 1),
                    9.0),
                COALESCE(
                    (SELECT PageMarginInches FROM PublicationEditions
                     WHERE PublicationEditions.ProjectId = Projects.Id
                     ORDER BY IsDefault DESC, UpdatedAt DESC LIMIT 1),
                    0.75),
                COALESCE(
                    (SELECT BodyFontSizePoints FROM PublicationEditions
                     WHERE PublicationEditions.ProjectId = Projects.Id
                     ORDER BY IsDefault DESC, UpdatedAt DESC LIMIT 1),
                    12.0),
                COALESCE(
                    (SELECT BodyLineHeight FROM PublicationEditions
                     WHERE PublicationEditions.ProjectId = Projects.Id
                     ORDER BY IsDefault DESC, UpdatedAt DESC LIMIT 1),
                    1.55),
                1, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
            FROM Projects
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_PageCompositions_ActiveAuthoringVariant_Update");
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_PageCompositionVariants_ClearAuthoringSelection");
        migrationBuilder.DropTable(name: "ProjectPageSetups");
        migrationBuilder.DropIndex(name: "IX_PageCompositions_ActiveAuthoringVariantId", table: "PageCompositions");
        migrationBuilder.DropColumn(name: "ActiveAuthoringVariantId", table: "PageCompositions");
        migrationBuilder.RenameColumn(name: "ImageCropXPercent", table: "PublicationCoverDesigns", newName: "ImageFocalXPercent");
        migrationBuilder.RenameColumn(name: "ImageCropYPercent", table: "PublicationCoverDesigns", newName: "ImageFocalYPercent");
    }
}
