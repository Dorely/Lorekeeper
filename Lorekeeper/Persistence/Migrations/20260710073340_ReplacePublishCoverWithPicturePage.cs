using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReplacePublishCoverWithPicturePage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PublishProfiles_PublishAssets_SelectedCoverAssetId",
                table: "PublishProfiles");

            migrationBuilder.DropIndex(
                name: "IX_PublishProfiles_SelectedCoverAssetId",
                table: "PublishProfiles");

            migrationBuilder.DropColumn(
                name: "CoverLayoutJson",
                table: "PublishProfiles");

            migrationBuilder.DropColumn(
                name: "SelectedCoverAssetId",
                table: "PublishProfiles");

            migrationBuilder.AddColumn<Guid>(
                name: "SelectedCoverChapterId",
                table: "PublishProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublishProfiles_SelectedCoverChapterId",
                table: "PublishProfiles",
                column: "SelectedCoverChapterId");

            migrationBuilder.AddForeignKey(
                name: "FK_PublishProfiles_Chapters_SelectedCoverChapterId",
                table: "PublishProfiles",
                column: "SelectedCoverChapterId",
                principalTable: "Chapters",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PublishProfiles_Chapters_SelectedCoverChapterId",
                table: "PublishProfiles");

            migrationBuilder.DropIndex(
                name: "IX_PublishProfiles_SelectedCoverChapterId",
                table: "PublishProfiles");

            migrationBuilder.DropColumn(
                name: "SelectedCoverChapterId",
                table: "PublishProfiles");

            migrationBuilder.AddColumn<string>(
                name: "CoverLayoutJson",
                table: "PublishProfiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "SelectedCoverAssetId",
                table: "PublishProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublishProfiles_SelectedCoverAssetId",
                table: "PublishProfiles",
                column: "SelectedCoverAssetId");

            migrationBuilder.AddForeignKey(
                name: "FK_PublishProfiles_PublishAssets_SelectedCoverAssetId",
                table: "PublishProfiles",
                column: "SelectedCoverAssetId",
                principalTable: "PublishAssets",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
