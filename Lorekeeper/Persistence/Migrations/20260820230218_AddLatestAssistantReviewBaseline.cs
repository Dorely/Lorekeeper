using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLatestAssistantReviewBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReviewBaselineManuscriptJson",
                table: "AuthoringTurnHistoryBatches",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LatestReviewActionLabel",
                table: "AuthoringHistoryStreams",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LatestReviewAssistantTurnId",
                table: "AuthoringHistoryStreams",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LatestReviewBeforeHash",
                table: "AuthoringHistoryStreams",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LatestReviewBeforeJson",
                table: "AuthoringHistoryStreams",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LatestReviewCapturedAt",
                table: "AuthoringHistoryStreams",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReviewBaselineManuscriptJson",
                table: "AuthoringTurnHistoryBatches");

            migrationBuilder.DropColumn(
                name: "LatestReviewActionLabel",
                table: "AuthoringHistoryStreams");

            migrationBuilder.DropColumn(
                name: "LatestReviewAssistantTurnId",
                table: "AuthoringHistoryStreams");

            migrationBuilder.DropColumn(
                name: "LatestReviewBeforeHash",
                table: "AuthoringHistoryStreams");

            migrationBuilder.DropColumn(
                name: "LatestReviewBeforeJson",
                table: "AuthoringHistoryStreams");

            migrationBuilder.DropColumn(
                name: "LatestReviewCapturedAt",
                table: "AuthoringHistoryStreams");
        }
    }
}
