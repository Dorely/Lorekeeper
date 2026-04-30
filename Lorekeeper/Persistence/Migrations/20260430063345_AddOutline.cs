using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOutline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Metadata",
                table: "Projects",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<Guid>(
                name: "ActId",
                table: "Chapters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Acts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Synopsis = table.Column<string>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Acts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Acts_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Chapters_ActId_Order",
                table: "Chapters",
                columns: new[] { "ActId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_Acts_ProjectId_Order",
                table: "Acts",
                columns: new[] { "ProjectId", "Order" });

            migrationBuilder.AddForeignKey(
                name: "FK_Chapters_Acts_ActId",
                table: "Chapters",
                column: "ActId",
                principalTable: "Acts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Chapters_Acts_ActId",
                table: "Chapters");

            migrationBuilder.DropTable(
                name: "Acts");

            migrationBuilder.DropIndex(
                name: "IX_Chapters_ActId_Order",
                table: "Chapters");

            migrationBuilder.DropColumn(
                name: "Metadata",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "ActId",
                table: "Chapters");
        }
    }
}
