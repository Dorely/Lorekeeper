using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveCoverBackCopy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE PublicationCoverDesigns
                SET CompositionSceneJson = replace(
                        replace(CompositionSceneJson, '{{backCopy}}', '{{description}}'),
                        '"textBinding":"backCopy"',
                        '"textBinding":"description"'),
                    SurfaceScenesJson = replace(
                        replace(
                            replace(SurfaceScenesJson, '{{backCopy}}', '{{description}}'),
                            '\"textBinding\":\"backCopy\"',
                            '\"textBinding\":\"description\"'),
                        '\u0022textBinding\u0022:\u0022backCopy\u0022',
                        '\u0022textBinding\u0022:\u0022description\u0022');
                """);

            migrationBuilder.DropColumn(
                name: "BackCopy",
                table: "PublicationCoverDesigns");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BackCopy",
                table: "PublicationCoverDesigns",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(
                """
                UPDATE PublicationCoverDesigns
                SET BackCopy = COALESCE(
                        (SELECT Description
                         FROM PublicationEditions
                         WHERE PublicationEditions.Id = PublicationCoverDesigns.EditionId),
                        ''),
                    CompositionSceneJson = replace(
                        replace(CompositionSceneJson, '{{description}}', '{{backCopy}}'),
                        '"textBinding":"description"',
                        '"textBinding":"backCopy"'),
                    SurfaceScenesJson = replace(
                        replace(
                            replace(SurfaceScenesJson, '{{description}}', '{{backCopy}}'),
                            '\"textBinding\":\"description\"',
                            '\"textBinding\":\"backCopy\"'),
                        '\u0022textBinding\u0022:\u0022description\u0022',
                        '\u0022textBinding\u0022:\u0022backCopy\u0022');
                """);
        }
    }
}
