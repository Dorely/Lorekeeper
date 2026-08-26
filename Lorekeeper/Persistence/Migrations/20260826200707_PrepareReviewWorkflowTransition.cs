using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PrepareReviewWorkflowTransition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "AiChangeApprovalEnabled",
                table: "Projects",
                newName: "ReviewEditsEnabled");

            migrationBuilder.AddColumn<string>(
                name: "DraftManuscriptJson",
                table: "ContestCandidates",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "OriginalManuscriptHash",
                table: "ContestBatches",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "OriginalManuscriptRevision",
                table: "ContestBatches",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "SelectedCandidateId",
                table: "ContestBatches",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DraftManuscriptJson",
                table: "ContestCandidates");

            migrationBuilder.DropColumn(
                name: "OriginalManuscriptRevision",
                table: "ContestBatches");

            migrationBuilder.DropColumn(
                name: "SelectedCandidateId",
                table: "ContestBatches");

            migrationBuilder.RenameColumn(
                name: "ReviewEditsEnabled",
                table: "Projects",
                newName: "AiChangeApprovalEnabled");

            migrationBuilder.DropColumn(
                name: "OriginalManuscriptHash",
                table: "ContestBatches");
        }
    }
}
