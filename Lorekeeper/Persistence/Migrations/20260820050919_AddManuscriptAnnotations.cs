using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddManuscriptAnnotations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ManuscriptAnnotations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChapterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EditionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    NoteText = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    AnchorManuscriptRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    AnchorState = table.Column<string>(type: "TEXT", nullable: false),
                    StartBlockId = table.Column<string>(type: "TEXT", nullable: false),
                    StartOffset = table.Column<int>(type: "INTEGER", nullable: false),
                    EndBlockId = table.Column<string>(type: "TEXT", nullable: false),
                    EndOffset = table.Column<int>(type: "INTEGER", nullable: false),
                    OriginalQuote = table.Column<string>(type: "TEXT", maxLength: 32000, nullable: false),
                    ContextBefore = table.Column<string>(type: "TEXT", maxLength: 96, nullable: false),
                    ContextAfter = table.Column<string>(type: "TEXT", maxLength: 96, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManuscriptAnnotations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ManuscriptAnnotations_Chapters_ChapterId",
                        column: x => x.ChapterId,
                        principalTable: "Chapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ManuscriptAnnotations_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ManuscriptAnnotations_PublicationEditions_EditionId",
                        column: x => x.EditionId,
                        principalTable: "PublicationEditions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ManuscriptAnnotations_ChapterId_AnchorState_CreatedAt",
                table: "ManuscriptAnnotations",
                columns: new[] { "ChapterId", "AnchorState", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ManuscriptAnnotations_EditionId",
                table: "ManuscriptAnnotations",
                column: "EditionId");

            migrationBuilder.CreateIndex(
                name: "IX_ManuscriptAnnotations_ProjectId_EditionId_ChapterId",
                table: "ManuscriptAnnotations",
                columns: new[] { "ProjectId", "EditionId", "ChapterId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ManuscriptAnnotations");
        }
    }
}
