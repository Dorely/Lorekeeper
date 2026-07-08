using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChapterVisualModesAndImages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "BodyFontSizePoints",
                table: "PublishProfiles",
                type: "REAL",
                nullable: false,
                defaultValue: 12.0);

            migrationBuilder.AddColumn<double>(
                name: "BodyLineHeight",
                table: "PublishProfiles",
                type: "REAL",
                nullable: false,
                defaultValue: 1.55);

            migrationBuilder.AddColumn<double>(
                name: "PageHeightInches",
                table: "PublishProfiles",
                type: "REAL",
                nullable: false,
                defaultValue: 11.0);

            migrationBuilder.AddColumn<double>(
                name: "PageMarginInches",
                table: "PublishProfiles",
                type: "REAL",
                nullable: false,
                defaultValue: 0.75);

            migrationBuilder.AddColumn<double>(
                name: "PageWidthInches",
                table: "PublishProfiles",
                type: "REAL",
                nullable: false,
                defaultValue: 8.5);

            migrationBuilder.AddColumn<string>(
                name: "IllustrationLayoutJson",
                table: "Chapters",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PageLayoutJson",
                table: "Chapters",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<double>(
                name: "PicturePageHeightInches",
                table: "Chapters",
                type: "REAL",
                nullable: false,
                defaultValue: 8.5);

            migrationBuilder.AddColumn<bool>(
                name: "PicturePageIsSpread",
                table: "Chapters",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "PicturePageWidthInches",
                table: "Chapters",
                type: "REAL",
                nullable: false,
                defaultValue: 8.5);

            migrationBuilder.AddColumn<string>(
                name: "VisualMode",
                table: "Chapters",
                type: "TEXT",
                nullable: false,
                defaultValue: "Prose");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BodyFontSizePoints",
                table: "PublishProfiles");

            migrationBuilder.DropColumn(
                name: "BodyLineHeight",
                table: "PublishProfiles");

            migrationBuilder.DropColumn(
                name: "PageHeightInches",
                table: "PublishProfiles");

            migrationBuilder.DropColumn(
                name: "PageMarginInches",
                table: "PublishProfiles");

            migrationBuilder.DropColumn(
                name: "PageWidthInches",
                table: "PublishProfiles");

            migrationBuilder.DropColumn(
                name: "IllustrationLayoutJson",
                table: "Chapters");

            migrationBuilder.DropColumn(
                name: "PageLayoutJson",
                table: "Chapters");

            migrationBuilder.DropColumn(
                name: "PicturePageHeightInches",
                table: "Chapters");

            migrationBuilder.DropColumn(
                name: "PicturePageIsSpread",
                table: "Chapters");

            migrationBuilder.DropColumn(
                name: "PicturePageWidthInches",
                table: "Chapters");

            migrationBuilder.DropColumn(
                name: "VisualMode",
                table: "Chapters");
        }
    }
}
