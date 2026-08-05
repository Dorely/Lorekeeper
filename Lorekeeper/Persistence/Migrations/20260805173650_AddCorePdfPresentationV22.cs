using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCorePdfPresentationV22 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PublicationBookPdfPresentations",
                columns: table => new
                {
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AllowDesignedPageOverrides = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationBookPdfPresentations", x => x.ProjectId);
                    table.ForeignKey(
                        name: "FK_PublicationBookPdfPresentations_PublicationBooks_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "PublicationBooks",
                        principalColumn: "ProjectId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql(
                """
                INSERT INTO "PublicationBookPdfPresentations" ("ProjectId", "AllowDesignedPageOverrides")
                SELECT "ProjectId", 0 FROM "PublicationBooks";

                UPDATE "PublicationEditions"
                SET "OverrideFieldsJson" = CASE
                    WHEN "OverrideFieldsJson" = '[]' THEN '["AllowDesignedPageOverrides"]'
                    ELSE substr("OverrideFieldsJson", 1, length("OverrideFieldsJson") - 1)
                        || ',"AllowDesignedPageOverrides"]'
                END
                WHERE "Format" = 'DigitalPdf'
                  AND "AllowDesignedPageOverrides" = 1
                  AND instr("OverrideFieldsJson", '"AllowDesignedPageOverrides"') = 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PublicationBookPdfPresentations");
        }
    }
}
