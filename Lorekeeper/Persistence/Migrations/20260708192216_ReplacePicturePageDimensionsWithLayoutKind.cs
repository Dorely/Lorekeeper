using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReplacePicturePageDimensionsWithLayoutKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PageLayoutKind",
                table: "Chapters",
                type: "TEXT",
                nullable: false,
                defaultValue: "SinglePortrait");

            migrationBuilder.Sql(
                """
                UPDATE Chapters
                SET PageLayoutKind = CASE
                    WHEN PicturePageIsSpread <> 0 AND PicturePageWidthInches > PicturePageHeightInches THEN 'DoubleLandscape'
                    WHEN PicturePageIsSpread <> 0 THEN 'DoublePortrait'
                    WHEN PicturePageWidthInches > PicturePageHeightInches THEN 'SingleLandscape'
                    ELSE 'SinglePortrait'
                END
                """);

            migrationBuilder.DropColumn(
                name: "PicturePageHeightInches",
                table: "Chapters");

            migrationBuilder.DropColumn(
                name: "PicturePageIsSpread",
                table: "Chapters");

            migrationBuilder.DropColumn(
                name: "PicturePageWidthInches",
                table: "Chapters");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "PicturePageHeightInches",
                table: "Chapters",
                type: "REAL",
                nullable: false,
                defaultValue: 11.0);

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

            migrationBuilder.Sql(
                """
                UPDATE Chapters
                SET
                    PicturePageWidthInches = CASE
                        WHEN PageLayoutKind IN ('SingleLandscape', 'DoubleLandscape') THEN 11.0
                        ELSE 8.5
                    END,
                    PicturePageHeightInches = CASE
                        WHEN PageLayoutKind IN ('SingleLandscape', 'DoubleLandscape') THEN 8.5
                        ELSE 11.0
                    END,
                    PicturePageIsSpread = CASE
                        WHEN PageLayoutKind IN ('DoublePortrait', 'DoubleLandscape') THEN 1
                        ELSE 0
                    END
                """);

            migrationBuilder.DropColumn(
                name: "PageLayoutKind",
                table: "Chapters");
        }
    }
}
