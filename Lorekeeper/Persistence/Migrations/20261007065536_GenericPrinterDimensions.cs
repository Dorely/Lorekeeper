using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GenericPrinterDimensions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "PrinterCaseHingeInches",
                table: "PublicationEditions",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PrinterCaseWrapInches",
                table: "PublicationEditions",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PrinterPaperThicknessInches",
                table: "PublicationEditions",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PrinterSpineAllowanceInches",
                table: "PublicationEditions",
                type: "REAL",
                nullable: true);

            // Other-printer releases now take their spine and case measurements from the user, so the
            // paper-specific estimated profiles collapse into one user-dimensioned profile per interior process.
            migrationBuilder.Sql("""
                UPDATE PublicationEditions
                SET PrintArtifactProfileKey = CASE PrintArtifactProfileKey
                        WHEN 'generic-pb-bw-50-white' THEN 'generic-pb-bw'
                        WHEN 'generic-pb-bw-60-cream' THEN 'generic-pb-bw'
                        WHEN 'generic-pb-stdcolor-60-white' THEN 'generic-pb-stdcolor'
                        WHEN 'generic-pb-premcolor-80-white' THEN 'generic-pb-premcolor'
                        WHEN 'generic-case-bw-50-white' THEN 'generic-case-bw'
                        WHEN 'generic-case-bw-60-cream' THEN 'generic-case-bw'
                        WHEN 'generic-case-stdcolor-60-white' THEN 'generic-case-stdcolor'
                        WHEN 'generic-case-premcolor-80-white' THEN 'generic-case-premcolor'
                    END,
                    PrintArtifactRegistryVersion = '2026.10.1'
                WHERE PrintArtifactProfileKey IN (
                    'generic-pb-bw-50-white', 'generic-pb-bw-60-cream', 'generic-pb-stdcolor-60-white', 'generic-pb-premcolor-80-white',
                    'generic-case-bw-50-white', 'generic-case-bw-60-cream', 'generic-case-stdcolor-60-white', 'generic-case-premcolor-80-white');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PrinterCaseHingeInches",
                table: "PublicationEditions");

            migrationBuilder.DropColumn(
                name: "PrinterCaseWrapInches",
                table: "PublicationEditions");

            migrationBuilder.DropColumn(
                name: "PrinterPaperThicknessInches",
                table: "PublicationEditions");

            migrationBuilder.DropColumn(
                name: "PrinterSpineAllowanceInches",
                table: "PublicationEditions");
        }
    }
}
