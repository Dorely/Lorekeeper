using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveProofStyleMappingsAndReleaseTypographyV24 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PublicationEditionStyleMappings");

            migrationBuilder.DropColumn(
                name: "BodyFontSizePoints",
                table: "PublicationEditions");

            migrationBuilder.DropColumn(
                name: "BodyLineHeight",
                table: "PublicationEditions");

            // V23 must rebuild PageCompositions on SQLite. Recreate its authoring
            // integrity triggers only after that migration's deferred rebuild completes.
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_PageCompositions_ActiveAuthoringVariant_Update");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_PageCompositionVariants_ClearAuthoringSelection");

            migrationBuilder.AddColumn<double>(
                name: "BodyFontSizePoints",
                table: "PublicationEditions",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "BodyLineHeight",
                table: "PublicationEditions",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.CreateTable(
                name: "PublicationEditionStyleMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EditionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ManuscriptStyleDefinitionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    OverrideJson = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    SemanticRole = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationEditionStyleMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublicationEditionStyleMappings_ManuscriptStyleDefinitions_ManuscriptStyleDefinitionId",
                        column: x => x.ManuscriptStyleDefinitionId,
                        principalTable: "ManuscriptStyleDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PublicationEditionStyleMappings_PublicationEditions_EditionId",
                        column: x => x.EditionId,
                        principalTable: "PublicationEditions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PublicationEditionStyleMappings_EditionId_ManuscriptStyleDefinitionId",
                table: "PublicationEditionStyleMappings",
                columns: new[] { "EditionId", "ManuscriptStyleDefinitionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublicationEditionStyleMappings_EditionId_SemanticRole",
                table: "PublicationEditionStyleMappings",
                columns: new[] { "EditionId", "SemanticRole" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublicationEditionStyleMappings_ManuscriptStyleDefinitionId",
                table: "PublicationEditionStyleMappings",
                column: "ManuscriptStyleDefinitionId");
        }
    }
}
