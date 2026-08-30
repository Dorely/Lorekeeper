using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BarnesAndNoblePrintPublishingV31 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "GenericPrintTemplateJson",
                table: "PublicationEditions",
                newName: "PrintTemplateEvidenceJson");

            migrationBuilder.AddColumn<int>(
                name: "PrintCoverSubmissionMode",
                table: "PublicationEditions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PrintIdentifierMode",
                table: "PublicationEditions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<int>(
                name: "PrintProjectUse",
                table: "PublicationEditions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "SpineReadingDirection",
                table: "PublicationCoverDesigns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PrintCoverSubmissionMode",
                table: "PublicationEditions");

            migrationBuilder.DropColumn(
                name: "PrintIdentifierMode",
                table: "PublicationEditions");

            migrationBuilder.DropColumn(
                name: "PrintProjectUse",
                table: "PublicationEditions");

            migrationBuilder.DropColumn(
                name: "SpineReadingDirection",
                table: "PublicationCoverDesigns");

            migrationBuilder.RenameColumn(
                name: "PrintTemplateEvidenceJson",
                table: "PublicationEditions",
                newName: "GenericPrintTemplateJson");
        }
    }
}
