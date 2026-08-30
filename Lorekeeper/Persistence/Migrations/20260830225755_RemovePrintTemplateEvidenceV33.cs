using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemovePrintTemplateEvidenceV33 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE "PublicationEditions"
                SET "PrintArtifactProfileKey" = CASE "PrintArtifactProfileKey"
                    WHEN 'generic-perfectbound-template' THEN 'generic-perfectbound-v1'
                    WHEN 'generic-casebound-template' THEN 'generic-casebound-v1'
                    ELSE "PrintArtifactProfileKey"
                END;
                """);

            migrationBuilder.DropColumn(
                name: "PrintTemplateEvidenceJson",
                table: "PublicationEditions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PrintTemplateEvidenceJson",
                table: "PublicationEditions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(
                """
                UPDATE "PublicationEditions"
                SET "PrintArtifactProfileKey" = CASE "PrintArtifactProfileKey"
                    WHEN 'generic-perfectbound-v1' THEN 'generic-perfectbound-template'
                    WHEN 'generic-casebound-v1' THEN 'generic-casebound-template'
                    ELSE "PrintArtifactProfileKey"
                END;
                """);
        }
    }
}
