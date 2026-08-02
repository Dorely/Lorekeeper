using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompleteCompositionContractsV18 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PaginationFingerprint",
                table: "PublicationRenderJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PresentationJson",
                table: "PublicationImagePlacements",
                type: "TEXT",
                nullable: false,
                defaultValue: "{\"placement\":\"dedicatedPage\",\"widthPercent\":100,\"alignment\":\"center\",\"textWrap\":\"none\",\"fit\":\"contain\",\"focalXPercent\":50,\"focalYPercent\":50,\"spacingBeforePoints\":6,\"spacingAfterPoints\":6,\"startOnNewPage\":true,\"keepWithCaption\":true,\"captionPlacement\":\"below\",\"layoutTargetEditionId\":null}");

            migrationBuilder.AddColumn<string>(
                name: "AccessibilityRole",
                table: "PublicationImagePlacements",
                type: "TEXT",
                nullable: false,
                defaultValue: "Figure");

            migrationBuilder.AddColumn<string>(
                name: "AltText",
                table: "PublicationImagePlacements",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "Decorative",
                table: "PublicationImagePlacements",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "PublicationImagePlacements",
                type: "TEXT",
                nullable: false,
                defaultValue: "en");

            migrationBuilder.AddColumn<string>(
                name: "PaginationFingerprint",
                table: "PublicationArtifacts",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("""
                UPDATE PublicationImagePlacements
                SET AltText = COALESCE(
                    NULLIF((SELECT AltText FROM PublishAssets WHERE PublishAssets.Id = PublicationImagePlacements.AssetId), ''),
                    (SELECT FileName FROM PublishAssets WHERE PublishAssets.Id = PublicationImagePlacements.AssetId),
                    '')
                """);

            migrationBuilder.Sql("""
                UPDATE PublicationCoverDesigns
                SET CompositionSceneJson = json_set(
                    CompositionSceneJson,
                    '$.surface.trimWidthPoints', (SELECT PageWidthInches * 72.0 FROM PublicationEditions WHERE PublicationEditions.Id = PublicationCoverDesigns.EditionId),
                    '$.surface.trimHeightPoints', (SELECT PageHeightInches * 72.0 FROM PublicationEditions WHERE PublicationEditions.Id = PublicationCoverDesigns.EditionId),
                    '$.surface.spineWidthPoints', MAX(0.0,
                        json_extract(CompositionSceneJson, '$.surface.widthPoints')
                        - (SELECT PageWidthInches * 144.0 FROM PublicationEditions WHERE PublicationEditions.Id = PublicationCoverDesigns.EditionId)
                        - 2.0 * COALESCE(json_extract(CompositionSceneJson, '$.surface.bleedPoints'), 0.0)))
                WHERE CompositionSceneJson <> '' AND json_valid(CompositionSceneJson)
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_CompositionMutationStages_Projects_ProjectId",
                table: "CompositionMutationStages",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CompositionMutationStages_Projects_ProjectId",
                table: "CompositionMutationStages");

            migrationBuilder.DropColumn(
                name: "PaginationFingerprint",
                table: "PublicationRenderJobs");

            migrationBuilder.DropColumn(
                name: "PresentationJson",
                table: "PublicationImagePlacements");

            migrationBuilder.DropColumn(
                name: "AccessibilityRole",
                table: "PublicationImagePlacements");

            migrationBuilder.DropColumn(
                name: "AltText",
                table: "PublicationImagePlacements");

            migrationBuilder.DropColumn(
                name: "Decorative",
                table: "PublicationImagePlacements");

            migrationBuilder.DropColumn(
                name: "Language",
                table: "PublicationImagePlacements");

            migrationBuilder.DropColumn(
                name: "PaginationFingerprint",
                table: "PublicationArtifacts");

        }
    }
}
