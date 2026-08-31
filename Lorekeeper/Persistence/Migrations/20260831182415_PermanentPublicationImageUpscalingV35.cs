using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PermanentPublicationImageUpscalingV35 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImagePreparationSummaryJson",
                table: "PublicationPreparationJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");

            // PublishAssetSource is persisted by name. Reclassify only the
            // historical print-resample rows; existing Imported rows retain
            // their stored name even though the enum's numeric value changed.
            migrationBuilder.Sql(
                """
                UPDATE PublishAssets
                SET Source = 'Upscaled'
                WHERE Source = 'Resized'
                  AND instr(lower(SourceMetadataJson), '"kind":"print-resample"') > 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE PublishAssets SET Source = 'Resized' WHERE Source = 'Upscaled';");

            migrationBuilder.DropColumn(
                name: "ImagePreparationSummaryJson",
                table: "PublicationPreparationJobs");
        }
    }
}
