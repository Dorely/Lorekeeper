using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEditionSpecificContentV23 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite rebuilds PageCompositions while adding edition ownership.
            // The V19 triggers reference that table and cannot survive its temporary rename.
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_PageCompositions_ActiveAuthoringVariant_Update");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_PageCompositionVariants_ClearAuthoringSelection");

            migrationBuilder.DropIndex(
                name: "IX_PageCompositions_ProjectId_ChapterId_UpdatedAt",
                table: "PageCompositions");

            migrationBuilder.AddColumn<bool>(
                name: "EditionSpecificContentEnabled",
                table: "PublicationEditions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "EditionId",
                table: "PageCompositions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceCompositionId",
                table: "PageCompositions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ContentTargetEditionId",
                table: "EditorRevisionJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContentTargetKind",
                table: "EditorRevisionJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "ContentTargetEditionId",
                table: "EditorMessages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContentTargetKind",
                table: "EditorMessages",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "ContentTargetEditionId",
                table: "ContestBatches",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContentTargetKind",
                table: "ContestBatches",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "ContentTargetEditionId",
                table: "CompositionMutationStages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContentTargetKind",
                table: "CompositionMutationStages",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "ContentTargetEditionId",
                table: "AiChangeBatches",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContentTargetKind",
                table: "AiChangeBatches",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "PublicationEditionChapterOverrides",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EditionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChapterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ManuscriptJson = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    BaseCoreRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    BaseCoreHash = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationEditionChapterOverrides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublicationEditionChapterOverrides_Chapters_ChapterId",
                        column: x => x.ChapterId,
                        principalTable: "Chapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PublicationEditionChapterOverrides_PublicationEditions_EditionId",
                        column: x => x.EditionId,
                        principalTable: "PublicationEditions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PageCompositions_EditionId_SourceCompositionId",
                table: "PageCompositions",
                columns: new[] { "EditionId", "SourceCompositionId" });

            migrationBuilder.CreateIndex(
                name: "IX_PageCompositions_ProjectId_ChapterId_EditionId_UpdatedAt",
                table: "PageCompositions",
                columns: new[] { "ProjectId", "ChapterId", "EditionId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PublicationEditionChapterOverrides_ChapterId_UpdatedAt",
                table: "PublicationEditionChapterOverrides",
                columns: new[] { "ChapterId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PublicationEditionChapterOverrides_EditionId_ChapterId",
                table: "PublicationEditionChapterOverrides",
                columns: new[] { "EditionId", "ChapterId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_PageCompositions_PublicationEditions_EditionId",
                table: "PageCompositions",
                column: "EditionId",
                principalTable: "PublicationEditions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_PageCompositions_ActiveAuthoringVariant_Update");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_PageCompositionVariants_ClearAuthoringSelection");

            migrationBuilder.DropForeignKey(
                name: "FK_PageCompositions_PublicationEditions_EditionId",
                table: "PageCompositions");

            migrationBuilder.DropTable(
                name: "PublicationEditionChapterOverrides");

            migrationBuilder.DropIndex(
                name: "IX_PageCompositions_EditionId_SourceCompositionId",
                table: "PageCompositions");

            migrationBuilder.DropIndex(
                name: "IX_PageCompositions_ProjectId_ChapterId_EditionId_UpdatedAt",
                table: "PageCompositions");

            migrationBuilder.DropColumn(
                name: "EditionSpecificContentEnabled",
                table: "PublicationEditions");

            migrationBuilder.DropColumn(
                name: "EditionId",
                table: "PageCompositions");

            migrationBuilder.DropColumn(
                name: "SourceCompositionId",
                table: "PageCompositions");

            migrationBuilder.DropColumn(
                name: "ContentTargetEditionId",
                table: "EditorRevisionJobs");

            migrationBuilder.DropColumn(
                name: "ContentTargetKind",
                table: "EditorRevisionJobs");

            migrationBuilder.DropColumn(
                name: "ContentTargetEditionId",
                table: "EditorMessages");

            migrationBuilder.DropColumn(
                name: "ContentTargetKind",
                table: "EditorMessages");

            migrationBuilder.DropColumn(
                name: "ContentTargetEditionId",
                table: "ContestBatches");

            migrationBuilder.DropColumn(
                name: "ContentTargetKind",
                table: "ContestBatches");

            migrationBuilder.DropColumn(
                name: "ContentTargetEditionId",
                table: "CompositionMutationStages");

            migrationBuilder.DropColumn(
                name: "ContentTargetKind",
                table: "CompositionMutationStages");

            migrationBuilder.DropColumn(
                name: "ContentTargetEditionId",
                table: "AiChangeBatches");

            migrationBuilder.DropColumn(
                name: "ContentTargetKind",
                table: "AiChangeBatches");

            migrationBuilder.CreateIndex(
                name: "IX_PageCompositions_ProjectId_ChapterId_UpdatedAt",
                table: "PageCompositions",
                columns: new[] { "ProjectId", "ChapterId", "UpdatedAt" });

        }
    }
}
