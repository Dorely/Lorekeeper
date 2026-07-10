using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPublishPageOptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EpubPicturePageSpreadMode",
                table: "PublishProfiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "RequestLandscape");

            migrationBuilder.AddColumn<string>(
                name: "PrintPicturePageSpreadMode",
                table: "PublishProfiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "WholeSpread");

            migrationBuilder.AddColumn<string>(
                name: "TitlePageMode",
                table: "PublishProfiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "Automatic");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EpubPicturePageSpreadMode",
                table: "PublishProfiles");

            migrationBuilder.DropColumn(
                name: "PrintPicturePageSpreadMode",
                table: "PublishProfiles");

            migrationBuilder.DropColumn(
                name: "TitlePageMode",
                table: "PublishProfiles");
        }
    }
}
