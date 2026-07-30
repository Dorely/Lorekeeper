using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SemanticManuscriptV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ManuscriptStyleDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    NameKey = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    SemanticRole = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    SemanticRoleKey = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    DefinitionJson = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManuscriptStyleDefinitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ManuscriptStyleDefinitions_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ManuscriptStyleDefinitions_ProjectId_Kind_NameKey",
                table: "ManuscriptStyleDefinitions",
                columns: new[] { "ProjectId", "Kind", "NameKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ManuscriptStyleDefinitions_ProjectId_Kind_SemanticRoleKey",
                table: "ManuscriptStyleDefinitions",
                columns: new[] { "ProjectId", "Kind", "SemanticRoleKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ManuscriptStyleDefinitions");
        }
    }
}
