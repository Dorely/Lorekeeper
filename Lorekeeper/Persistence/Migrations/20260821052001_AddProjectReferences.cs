using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectReferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProjectReferences",
                columns: table => new
                {
                    ReferencingProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReferencedProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectReferences", x => new { x.ReferencingProjectId, x.ReferencedProjectId });
                    table.CheckConstraint("CK_ProjectReferences_NotSelf", "\"ReferencingProjectId\" <> \"ReferencedProjectId\"");
                    table.ForeignKey(
                        name: "FK_ProjectReferences_Projects_ReferencedProjectId",
                        column: x => x.ReferencedProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectReferences_Projects_ReferencingProjectId",
                        column: x => x.ReferencingProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectReferences_ReferencedProjectId",
                table: "ProjectReferences",
                column: "ReferencedProjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectReferences");
        }
    }
}
