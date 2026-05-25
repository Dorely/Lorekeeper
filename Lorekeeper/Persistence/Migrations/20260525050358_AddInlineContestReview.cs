using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInlineContestReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReviewStateJson",
                table: "ContestCandidates",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "AcceptedChapterBody",
                table: "ContestBatches",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "WinningCandidateId",
                table: "ContestBatches",
                type: "TEXT",
                nullable: true);

            migrationBuilder.Sql("UPDATE ContestBatches SET Status = 'Finished' WHERE Status = 'Staged';");
            migrationBuilder.Sql("UPDATE ContestBatches SET AcceptedChapterBody = OriginalChapterBody WHERE AcceptedChapterBody = '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE ContestBatches SET Status = 'Staged' WHERE Status = 'Finished';");

            migrationBuilder.DropColumn(
                name: "ReviewStateJson",
                table: "ContestCandidates");

            migrationBuilder.DropColumn(
                name: "AcceptedChapterBody",
                table: "ContestBatches");

            migrationBuilder.DropColumn(
                name: "WinningCandidateId",
                table: "ContestBatches");
        }
    }
}
