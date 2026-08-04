using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPublicationCoreBookV20 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PublicationArtifacts_PublicationEditions_EditionId",
                table: "PublicationArtifacts");

            migrationBuilder.DropForeignKey(
                name: "FK_PublicationRenderJobs_PublicationEditions_EditionId",
                table: "PublicationRenderJobs");

            migrationBuilder.DropIndex(
                name: "IX_PublicationRenderJobs_EditionId_CreatedAt",
                table: "PublicationRenderJobs");

            migrationBuilder.DropIndex(
                name: "IX_PublicationArtifacts_EditionId_Kind_CreatedAt",
                table: "PublicationArtifacts");

            migrationBuilder.AlterColumn<Guid>(
                name: "EditionId",
                table: "PublicationRenderJobs",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                table: "PublicationRenderJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "TargetKind",
                table: "PublicationRenderJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "Release");

            migrationBuilder.AddColumn<Guid>(
                name: "CoreMatterId",
                table: "PublicationMatter",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsExcluded",
                table: "PublicationMatter",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "CorePlacementId",
                table: "PublicationImagePlacements",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsExcluded",
                table: "PublicationImagePlacements",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "InheritsCoreCover",
                table: "PublicationEditions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "OverrideFieldsJson",
                table: "PublicationEditions",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<bool>(
                name: "InheritsCoreFront",
                table: "PublicationCoverDesigns",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<Guid>(
                name: "EditionId",
                table: "PublicationArtifacts",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                table: "PublicationArtifacts",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "TargetKind",
                table: "PublicationArtifacts",
                type: "TEXT",
                nullable: false,
                defaultValue: "Release");

            migrationBuilder.Sql("""
                UPDATE "PublicationRenderJobs"
                SET "ProjectId" = (SELECT "ProjectId" FROM "PublicationEditions" WHERE "Id" = "PublicationRenderJobs"."EditionId"),
                    "TargetKind" = 'Release';
                UPDATE "PublicationArtifacts"
                SET "ProjectId" = (SELECT "ProjectId" FROM "PublicationEditions" WHERE "Id" = "PublicationArtifacts"."EditionId"),
                    "TargetKind" = 'Release';
                """);

            migrationBuilder.CreateTable(
                name: "PublicationBooks",
                columns: table => new
                {
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Subtitle = table.Column<string>(type: "TEXT", nullable: false),
                    Author = table.Column<string>(type: "TEXT", nullable: false),
                    Language = table.Column<string>(type: "TEXT", nullable: false),
                    Publisher = table.Column<string>(type: "TEXT", nullable: false),
                    Copyright = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    IncludeTableOfContents = table.Column<bool>(type: "INTEGER", nullable: false),
                    IncludeVisibleTableOfContents = table.Column<bool>(type: "INTEGER", nullable: false),
                    IncludeActSynopses = table.Column<bool>(type: "INTEGER", nullable: false),
                    IncludeChapterSynopses = table.Column<bool>(type: "INTEGER", nullable: false),
                    IncludeActHeadings = table.Column<bool>(type: "INTEGER", nullable: false),
                    IncludeChapterHeadings = table.Column<bool>(type: "INTEGER", nullable: false),
                    NumberActs = table.Column<bool>(type: "INTEGER", nullable: false),
                    NumberChapters = table.Column<bool>(type: "INTEGER", nullable: false),
                    TitlePageMode = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationBooks", x => x.ProjectId);
                    table.ForeignKey(
                        name: "FK_PublicationBooks_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PublicationPreparationJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetKind = table.Column<string>(type: "TEXT", nullable: false),
                    EditionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RenderJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Step = table.Column<string>(type: "TEXT", nullable: false),
                    ProgressPercent = table.Column<int>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    DiagnosticsJson = table.Column<string>(type: "TEXT", nullable: false),
                    SourceFingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    CancellationRequested = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationPreparationJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublicationPreparationJobs_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PublicationPreparationJobs_PublicationEditions_EditionId",
                        column: x => x.EditionId,
                        principalTable: "PublicationEditions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PublicationPreparationJobs_PublicationRenderJobs_RenderJobId",
                        column: x => x.RenderJobId,
                        principalTable: "PublicationRenderJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PublicationBookCoverDesigns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BackgroundColor = table.Column<string>(type: "TEXT", nullable: false),
                    CompositionSceneJson = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationBookCoverDesigns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublicationBookCoverDesigns_PublicationBooks_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "PublicationBooks",
                        principalColumn: "ProjectId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PublicationBookImagePlacements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AssetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetKind = table.Column<string>(type: "TEXT", nullable: false),
                    TargetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ChapterId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PlacementKind = table.Column<string>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Caption = table.Column<string>(type: "TEXT", nullable: false),
                    PresentationJson = table.Column<string>(type: "TEXT", nullable: false),
                    AltText = table.Column<string>(type: "TEXT", nullable: false),
                    Decorative = table.Column<bool>(type: "INTEGER", nullable: false),
                    Language = table.Column<string>(type: "TEXT", nullable: false),
                    AccessibilityRole = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationBookImagePlacements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublicationBookImagePlacements_Acts_ActId",
                        column: x => x.ActId,
                        principalTable: "Acts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PublicationBookImagePlacements_Chapters_ChapterId",
                        column: x => x.ChapterId,
                        principalTable: "Chapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PublicationBookImagePlacements_PublicationBooks_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "PublicationBooks",
                        principalColumn: "ProjectId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PublicationBookImagePlacements_PublishAssets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "PublishAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PublicationBookMatter",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Location = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    ManuscriptJson = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    IsIncluded = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationBookMatter", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublicationBookMatter_PublicationBooks_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "PublicationBooks",
                        principalColumn: "ProjectId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PublicationBookOutlineItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetKind = table.Column<string>(type: "TEXT", nullable: false),
                    TargetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ChapterId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsIncluded = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationBookOutlineItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublicationBookOutlineItems_Acts_ActId",
                        column: x => x.ActId,
                        principalTable: "Acts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PublicationBookOutlineItems_Chapters_ChapterId",
                        column: x => x.ChapterId,
                        principalTable: "Chapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PublicationBookOutlineItems_PublicationBooks_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "PublicationBooks",
                        principalColumn: "ProjectId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PublicationRenderJobs_EditionId",
                table: "PublicationRenderJobs",
                column: "EditionId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicationRenderJobs_ProjectId_TargetKind_EditionId_CreatedAt",
                table: "PublicationRenderJobs",
                columns: new[] { "ProjectId", "TargetKind", "EditionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PublicationMatter_CoreMatterId",
                table: "PublicationMatter",
                column: "CoreMatterId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicationMatter_EditionId_CoreMatterId",
                table: "PublicationMatter",
                columns: new[] { "EditionId", "CoreMatterId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublicationImagePlacements_CorePlacementId",
                table: "PublicationImagePlacements",
                column: "CorePlacementId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicationImagePlacements_EditionId_CorePlacementId",
                table: "PublicationImagePlacements",
                columns: new[] { "EditionId", "CorePlacementId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublicationArtifacts_EditionId",
                table: "PublicationArtifacts",
                column: "EditionId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicationArtifacts_ProjectId_TargetKind_EditionId_Kind_CreatedAt",
                table: "PublicationArtifacts",
                columns: new[] { "ProjectId", "TargetKind", "EditionId", "Kind", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PublicationBookCoverDesigns_ProjectId",
                table: "PublicationBookCoverDesigns",
                column: "ProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublicationBookImagePlacements_ActId",
                table: "PublicationBookImagePlacements",
                column: "ActId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicationBookImagePlacements_AssetId",
                table: "PublicationBookImagePlacements",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicationBookImagePlacements_ChapterId",
                table: "PublicationBookImagePlacements",
                column: "ChapterId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicationBookImagePlacements_ProjectId_TargetKind_TargetId_PlacementKind_SortOrder",
                table: "PublicationBookImagePlacements",
                columns: new[] { "ProjectId", "TargetKind", "TargetId", "PlacementKind", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_PublicationBookMatter_ProjectId_Location_SortOrder",
                table: "PublicationBookMatter",
                columns: new[] { "ProjectId", "Location", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_PublicationBookOutlineItems_ActId",
                table: "PublicationBookOutlineItems",
                column: "ActId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicationBookOutlineItems_ChapterId",
                table: "PublicationBookOutlineItems",
                column: "ChapterId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicationBookOutlineItems_ProjectId_SortOrder",
                table: "PublicationBookOutlineItems",
                columns: new[] { "ProjectId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_PublicationBookOutlineItems_ProjectId_TargetKind_TargetId",
                table: "PublicationBookOutlineItems",
                columns: new[] { "ProjectId", "TargetKind", "TargetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublicationPreparationJobs_EditionId",
                table: "PublicationPreparationJobs",
                column: "EditionId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicationPreparationJobs_ProjectId_TargetKind_EditionId_CreatedAt",
                table: "PublicationPreparationJobs",
                columns: new[] { "ProjectId", "TargetKind", "EditionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PublicationPreparationJobs_RenderJobId",
                table: "PublicationPreparationJobs",
                column: "RenderJobId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicationPreparationJobs_Status_CreatedAt",
                table: "PublicationPreparationJobs",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_PublicationArtifacts_Projects_ProjectId",
                table: "PublicationArtifacts",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PublicationArtifacts_PublicationEditions_EditionId",
                table: "PublicationArtifacts",
                column: "EditionId",
                principalTable: "PublicationEditions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_PublicationImagePlacements_PublicationBookImagePlacements_CorePlacementId",
                table: "PublicationImagePlacements",
                column: "CorePlacementId",
                principalTable: "PublicationBookImagePlacements",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PublicationMatter_PublicationBookMatter_CoreMatterId",
                table: "PublicationMatter",
                column: "CoreMatterId",
                principalTable: "PublicationBookMatter",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PublicationRenderJobs_Projects_ProjectId",
                table: "PublicationRenderJobs",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PublicationRenderJobs_PublicationEditions_EditionId",
                table: "PublicationRenderJobs",
                column: "EditionId",
                principalTable: "PublicationEditions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PublicationArtifacts_Projects_ProjectId",
                table: "PublicationArtifacts");

            migrationBuilder.DropForeignKey(
                name: "FK_PublicationArtifacts_PublicationEditions_EditionId",
                table: "PublicationArtifacts");

            migrationBuilder.DropForeignKey(
                name: "FK_PublicationImagePlacements_PublicationBookImagePlacements_CorePlacementId",
                table: "PublicationImagePlacements");

            migrationBuilder.DropForeignKey(
                name: "FK_PublicationMatter_PublicationBookMatter_CoreMatterId",
                table: "PublicationMatter");

            migrationBuilder.DropForeignKey(
                name: "FK_PublicationRenderJobs_Projects_ProjectId",
                table: "PublicationRenderJobs");

            migrationBuilder.DropForeignKey(
                name: "FK_PublicationRenderJobs_PublicationEditions_EditionId",
                table: "PublicationRenderJobs");

            migrationBuilder.DropTable(
                name: "PublicationBookCoverDesigns");

            migrationBuilder.DropTable(
                name: "PublicationBookImagePlacements");

            migrationBuilder.DropTable(
                name: "PublicationBookMatter");

            migrationBuilder.DropTable(
                name: "PublicationBookOutlineItems");

            migrationBuilder.DropTable(
                name: "PublicationPreparationJobs");

            migrationBuilder.DropTable(
                name: "PublicationBooks");

            migrationBuilder.DropIndex(
                name: "IX_PublicationRenderJobs_EditionId",
                table: "PublicationRenderJobs");

            migrationBuilder.DropIndex(
                name: "IX_PublicationRenderJobs_ProjectId_TargetKind_EditionId_CreatedAt",
                table: "PublicationRenderJobs");

            migrationBuilder.DropIndex(
                name: "IX_PublicationMatter_CoreMatterId",
                table: "PublicationMatter");

            migrationBuilder.DropIndex(
                name: "IX_PublicationMatter_EditionId_CoreMatterId",
                table: "PublicationMatter");

            migrationBuilder.DropIndex(
                name: "IX_PublicationImagePlacements_CorePlacementId",
                table: "PublicationImagePlacements");

            migrationBuilder.DropIndex(
                name: "IX_PublicationImagePlacements_EditionId_CorePlacementId",
                table: "PublicationImagePlacements");

            migrationBuilder.DropIndex(
                name: "IX_PublicationArtifacts_EditionId",
                table: "PublicationArtifacts");

            migrationBuilder.DropIndex(
                name: "IX_PublicationArtifacts_ProjectId_TargetKind_EditionId_Kind_CreatedAt",
                table: "PublicationArtifacts");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "PublicationRenderJobs");

            migrationBuilder.DropColumn(
                name: "TargetKind",
                table: "PublicationRenderJobs");

            migrationBuilder.DropColumn(
                name: "CoreMatterId",
                table: "PublicationMatter");

            migrationBuilder.DropColumn(
                name: "IsExcluded",
                table: "PublicationMatter");

            migrationBuilder.DropColumn(
                name: "CorePlacementId",
                table: "PublicationImagePlacements");

            migrationBuilder.DropColumn(
                name: "IsExcluded",
                table: "PublicationImagePlacements");

            migrationBuilder.DropColumn(
                name: "InheritsCoreCover",
                table: "PublicationEditions");

            migrationBuilder.DropColumn(
                name: "OverrideFieldsJson",
                table: "PublicationEditions");

            migrationBuilder.DropColumn(
                name: "InheritsCoreFront",
                table: "PublicationCoverDesigns");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "PublicationArtifacts");

            migrationBuilder.DropColumn(
                name: "TargetKind",
                table: "PublicationArtifacts");

            migrationBuilder.AlterColumn<Guid>(
                name: "EditionId",
                table: "PublicationRenderJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "EditionId",
                table: "PublicationArtifacts",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublicationRenderJobs_EditionId_CreatedAt",
                table: "PublicationRenderJobs",
                columns: new[] { "EditionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PublicationArtifacts_EditionId_Kind_CreatedAt",
                table: "PublicationArtifacts",
                columns: new[] { "EditionId", "Kind", "CreatedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_PublicationArtifacts_PublicationEditions_EditionId",
                table: "PublicationArtifacts",
                column: "EditionId",
                principalTable: "PublicationEditions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PublicationRenderJobs_PublicationEditions_EditionId",
                table: "PublicationRenderJobs",
                column: "EditionId",
                principalTable: "PublicationEditions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
