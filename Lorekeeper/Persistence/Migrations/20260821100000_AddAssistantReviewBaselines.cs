using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations;

/// <inheritdoc />
public partial class AddAssistantReviewBaselines : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AssistantReviewBaselines",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                ChapterId = table.Column<Guid>(type: "TEXT", nullable: false),
                TargetKind = table.Column<string>(type: "TEXT", nullable: false),
                EditionId = table.Column<Guid>(type: "TEXT", nullable: true),
                TargetKey = table.Column<string>(type: "TEXT", nullable: false),
                BeforeManuscriptJson = table.Column<string>(type: "TEXT", nullable: false),
                BeforeHash = table.Column<string>(type: "TEXT", nullable: false),
                AssistantTurnId = table.Column<Guid>(type: "TEXT", nullable: true),
                ActionLabel = table.Column<string>(type: "TEXT", nullable: false),
                CapturedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AssistantReviewBaselines", x => x.Id);
                table.ForeignKey(
                    name: "FK_AssistantReviewBaselines_Projects_ProjectId",
                    column: x => x.ProjectId,
                    principalTable: "Projects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_AssistantReviewBaselines_Chapters_ChapterId",
                    column: x => x.ChapterId,
                    principalTable: "Chapters",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_AssistantReviewBaselines_PublicationEditions_EditionId",
                    column: x => x.EditionId,
                    principalTable: "PublicationEditions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AssistantReviewBaselines_ProjectId_ChapterId_TargetKey",
            table: "AssistantReviewBaselines",
            columns: new[] { "ProjectId", "ChapterId", "TargetKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_AssistantReviewBaselines_ProjectId_ChapterId_CapturedAt",
            table: "AssistantReviewBaselines",
            columns: new[] { "ProjectId", "ChapterId", "CapturedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_AssistantReviewBaselines_ChapterId",
            table: "AssistantReviewBaselines",
            column: "ChapterId");

        migrationBuilder.CreateIndex(
            name: "IX_AssistantReviewBaselines_EditionId",
            table: "AssistantReviewBaselines",
            column: "EditionId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "AssistantReviewBaselines");
}
