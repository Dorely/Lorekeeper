using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLegacyChapterVisualsV17 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EpubPicturePageSpreadMode",
                table: "PublicationEditions");

            migrationBuilder.DropColumn(
                name: "PrintPicturePageSpreadMode",
                table: "PublicationEditions");

            migrationBuilder.DropColumn(
                name: "IllustrationLayoutJson",
                table: "Chapters");

            migrationBuilder.DropColumn(
                name: "PageLayoutJson",
                table: "Chapters");

            migrationBuilder.DropColumn(
                name: "PageLayoutKind",
                table: "Chapters");

            migrationBuilder.DropColumn(
                name: "VisualMode",
                table: "Chapters");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EpubPicturePageSpreadMode",
                table: "PublicationEditions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PrintPicturePageSpreadMode",
                table: "PublicationEditions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

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

            migrationBuilder.AddColumn<string>(
                name: "PageLayoutKind",
                table: "Chapters",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "VisualMode",
                table: "Chapters",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }
    }
}
