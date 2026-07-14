using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StructuredImagePromptAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BriefJson",
                table: "ProjectImageGenerationJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ProviderRevisedPromptsJson",
                table: "ProjectImageGenerationJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ReferenceManifestJson",
                table: "ProjectImageGenerationJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TargetGeometryJson",
                table: "ProjectImageGenerationJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BriefJson",
                table: "ProjectImageGenerationJobs");

            migrationBuilder.DropColumn(
                name: "ProviderRevisedPromptsJson",
                table: "ProjectImageGenerationJobs");

            migrationBuilder.DropColumn(
                name: "ReferenceManifestJson",
                table: "ProjectImageGenerationJobs");

            migrationBuilder.DropColumn(
                name: "TargetGeometryJson",
                table: "ProjectImageGenerationJobs");
        }
    }
}
