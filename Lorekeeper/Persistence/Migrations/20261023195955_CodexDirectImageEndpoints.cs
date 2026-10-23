using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CodexDirectImageEndpoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MainlineModel",
                table: "ProjectImagePartials");

            migrationBuilder.DropColumn(
                name: "MainlineModel",
                table: "ProjectImageGenerationJobs");

            migrationBuilder.DropColumn(
                name: "MainlineModelId",
                table: "ImageProviders");

            // ChatGPT account image requests now go straight to the Codex image
            // endpoints, which pick quality, ignore masks and size, return PNG
            // (converted locally for JPEG), and render the prompt's aspect at a
            // fixed pixel budget. Bundled rows take those capabilities; manual
            // rows stay as the user entered them.
            migrationBuilder.Sql("""
                UPDATE ImageProviderModels
                SET QualitiesJson = '["auto"]',
                    OutputFormatsJson = '["png","jpeg"]',
                    SupportsRegionalGuide = 0,
                    SupportsStreaming = 0,
                    RasterConstraintsJson = '{"sizeMultiple":16,"minimumPixels":1525000,"maximumPixels":1620000,"maximumEdge":2208,"maximumAspectRatio":3,"targetPixels":1572864,"fixedPixelBudget":true}',
                    UpdatedAt = CURRENT_TIMESTAMP
                WHERE Origin = 'Bundled'
                  AND ImageProviderId IN (SELECT Id FROM ImageProviders WHERE Kind = 'ChatGptAccount');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MainlineModel",
                table: "ProjectImagePartials",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MainlineModel",
                table: "ProjectImageGenerationJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MainlineModelId",
                table: "ImageProviders",
                type: "TEXT",
                nullable: true);
        }
    }
}
