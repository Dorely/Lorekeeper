using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GraphNodes_NodeType_Key",
                table: "GraphNodes");

            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                table: "GraphNodes",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Slug = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GraphNodes_ProjectId_NodeType_Key",
                table: "GraphNodes",
                columns: new[] { "ProjectId", "NodeType", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Projects_Slug",
                table: "Projects",
                column: "Slug",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_GraphNodes_Projects_ProjectId",
                table: "GraphNodes",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_GraphNodes_Projects_ProjectId",
                table: "GraphNodes");

            migrationBuilder.DropTable(
                name: "Projects");

            migrationBuilder.DropIndex(
                name: "IX_GraphNodes_ProjectId_NodeType_Key",
                table: "GraphNodes");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "GraphNodes");

            migrationBuilder.CreateIndex(
                name: "IX_GraphNodes_NodeType_Key",
                table: "GraphNodes",
                columns: new[] { "NodeType", "Key" },
                unique: true);
        }
    }
}
