using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPublicationSectionStartSideV28 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StartSide",
                table: "PublicationSections",
                type: "TEXT",
                nullable: false,
                defaultValue: "Next");

            migrationBuilder.Sql(
                """
                UPDATE PublicationSections
                SET StartSide = CASE
                    WHEN SystemRole IN ('Title', 'Contents') THEN 'Recto'
                    WHEN SystemRole = 'Copyright' THEN 'Verso'
                    WHEN Kind IN ('Dedication', 'AboutAuthor', 'References') THEN 'Recto'
                    ELSE 'Next'
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StartSide",
                table: "PublicationSections");
        }
    }
}
