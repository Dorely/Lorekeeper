using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPublicationSectionsV25 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_PageCompositions_ActiveAuthoringVariant_Update");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_PageCompositionVariants_ClearAuthoringSelection");

            migrationBuilder.AlterColumn<Guid>(
                name: "ChapterId",
                table: "PageCompositions",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AddColumn<Guid>(
                name: "PublicationSectionId",
                table: "PageCompositions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PublicationSections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EditionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CoreSectionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsExcluded = table.Column<bool>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    SystemRole = table.Column<string>(type: "TEXT", nullable: false),
                    Anchor = table.Column<string>(type: "TEXT", nullable: false),
                    TargetKind = table.Column<string>(type: "TEXT", nullable: true),
                    TargetId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ActId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ChapterId = table.Column<Guid>(type: "TEXT", nullable: true),
                    InclusionMode = table.Column<string>(type: "TEXT", nullable: false),
                    LocalOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    ManuscriptJson = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationSections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublicationSections_Acts_ActId",
                        column: x => x.ActId,
                        principalTable: "Acts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PublicationSections_Chapters_ChapterId",
                        column: x => x.ChapterId,
                        principalTable: "Chapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PublicationSections_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PublicationSections_PublicationEditions_EditionId",
                        column: x => x.EditionId,
                        principalTable: "PublicationEditions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PublicationSections_PublicationSections_CoreSectionId",
                        column: x => x.CoreSectionId,
                        principalTable: "PublicationSections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PageCompositions_PublicationSectionId",
                table: "PageCompositions",
                column: "PublicationSectionId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicationSections_ActId",
                table: "PublicationSections",
                column: "ActId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicationSections_ChapterId",
                table: "PublicationSections",
                column: "ChapterId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicationSections_CoreSectionId",
                table: "PublicationSections",
                column: "CoreSectionId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicationSections_EditionId_CoreSectionId",
                table: "PublicationSections",
                columns: new[] { "EditionId", "CoreSectionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublicationSections_ProjectId_EditionId_Anchor_TargetId_LocalOrder",
                table: "PublicationSections",
                columns: new[] { "ProjectId", "EditionId", "Anchor", "TargetId", "LocalOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_PublicationSections_ProjectId_SystemRole_EditionId",
                table: "PublicationSections",
                columns: new[] { "ProjectId", "SystemRole", "EditionId" },
                unique: true,
                filter: "\"SystemRole\" <> 'None' AND \"IsExcluded\" = 0");

            migrationBuilder.AddForeignKey(
                name: "FK_PageCompositions_PublicationSections_PublicationSectionId",
                table: "PageCompositions",
                column: "PublicationSectionId",
                principalTable: "PublicationSections",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_PageCompositions_ActiveAuthoringVariant_Update");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_PageCompositionVariants_ClearAuthoringSelection");

            migrationBuilder.DropForeignKey(
                name: "FK_PageCompositions_PublicationSections_PublicationSectionId",
                table: "PageCompositions");

            migrationBuilder.DropTable(
                name: "PublicationSections");

            migrationBuilder.DropIndex(
                name: "IX_PageCompositions_PublicationSectionId",
                table: "PageCompositions");

            migrationBuilder.DropColumn(
                name: "PublicationSectionId",
                table: "PageCompositions");

            migrationBuilder.AlterColumn<Guid>(
                name: "ChapterId",
                table: "PageCompositions",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

        }
    }
}
