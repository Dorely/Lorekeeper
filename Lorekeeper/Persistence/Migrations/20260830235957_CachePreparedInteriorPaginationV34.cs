using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CachePreparedInteriorPaginationV34 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PublicationInteriorPaginations",
                columns: table => new
                {
                    EditionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PageCount = table.Column<int>(type: "INTEGER", nullable: false),
                    PaginationFingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    RendererVersion = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationInteriorPaginations", x => x.EditionId);
                    table.ForeignKey(
                        name: "FK_PublicationInteriorPaginations_PublicationEditions_EditionId",
                        column: x => x.EditionId,
                        principalTable: "PublicationEditions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PublicationInteriorPaginations");
        }
    }
}
