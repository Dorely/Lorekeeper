using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPageCompositionsV16 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EmbeddingRightsConfirmed",
                table: "ProjectFontFamilies",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "RightsDeclaration",
                table: "ProjectFontFamilies",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "AllowDesignedPageOverrides",
                table: "PublicationEditions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CompositionSceneJson",
                table: "PublicationCoverDesigns",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "CompositionMutationStages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetKind = table.Column<string>(type: "TEXT", nullable: false),
                    TargetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExpectedRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    OperationsJson = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AppliedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompositionMutationStages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PageCompositions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChapterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    SemanticManuscriptJson = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PageCompositions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PageCompositions_Chapters_ChapterId",
                        column: x => x.ChapterId,
                        principalTable: "Chapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PageCompositions_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PageCompositionVariants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompositionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GeometryKey = table.Column<string>(type: "TEXT", nullable: false),
                    SceneJson = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PageCompositionVariants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PageCompositionVariants_PageCompositions_CompositionId",
                        column: x => x.CompositionId,
                        principalTable: "PageCompositions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompositionMutationStages_PayloadSha256",
                table: "CompositionMutationStages",
                column: "PayloadSha256");

            migrationBuilder.CreateIndex(
                name: "IX_CompositionMutationStages_ProjectId_ConversationId_ExpiresAt",
                table: "CompositionMutationStages",
                columns: new[] { "ProjectId", "ConversationId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PageCompositions_ChapterId",
                table: "PageCompositions",
                column: "ChapterId");

            migrationBuilder.CreateIndex(
                name: "IX_PageCompositions_ProjectId_ChapterId_UpdatedAt",
                table: "PageCompositions",
                columns: new[] { "ProjectId", "ChapterId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PageCompositionVariants_CompositionId_GeometryKey",
                table: "PageCompositionVariants",
                columns: new[] { "CompositionId", "GeometryKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmbeddingRightsConfirmed",
                table: "ProjectFontFamilies");

            migrationBuilder.DropColumn(
                name: "RightsDeclaration",
                table: "ProjectFontFamilies");

            migrationBuilder.DropTable(
                name: "CompositionMutationStages");

            migrationBuilder.DropTable(
                name: "PageCompositionVariants");

            migrationBuilder.DropTable(
                name: "PageCompositions");

            migrationBuilder.DropColumn(
                name: "AllowDesignedPageOverrides",
                table: "PublicationEditions");

            migrationBuilder.DropColumn(
                name: "CompositionSceneJson",
                table: "PublicationCoverDesigns");
        }
    }
}
