using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChatGptPlanAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "OpenAiAccounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HostId",
                table: "OpenAiAccounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IssuedClientId",
                table: "OpenAiAccounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "OpenAiAccounts",
                type: "TEXT",
                nullable: false,
                defaultValue: "Codex");

            migrationBuilder.AddColumn<DateTime>(
                name: "PlanIneligibleAt",
                table: "OpenAiAccounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PlanUsageGranted",
                table: "OpenAiAccounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "WelcomeAcknowledgedAt",
                table: "OpenAiAccounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdToken",
                table: "OAuthTokens",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Earlier builds would treat a ChatGPT plan account as a Codex login and send its
            // tokens to the Codex backend, so the plan account, its models, and tokens go.
            migrationBuilder.Sql("DELETE FROM OAuthTokens WHERE OpenAiAccountId IN (SELECT Id FROM OpenAiAccounts WHERE Kind = 'ChatGptPlan');");
            migrationBuilder.Sql("DELETE FROM LlmProviders WHERE OpenAiAccountId IN (SELECT Id FROM OpenAiAccounts WHERE Kind = 'ChatGptPlan');");
            migrationBuilder.Sql("DELETE FROM OpenAiAccounts WHERE Kind = 'ChatGptPlan';");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "OpenAiAccounts");

            migrationBuilder.DropColumn(
                name: "HostId",
                table: "OpenAiAccounts");

            migrationBuilder.DropColumn(
                name: "IssuedClientId",
                table: "OpenAiAccounts");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "OpenAiAccounts");

            migrationBuilder.DropColumn(
                name: "PlanIneligibleAt",
                table: "OpenAiAccounts");

            migrationBuilder.DropColumn(
                name: "PlanUsageGranted",
                table: "OpenAiAccounts");

            migrationBuilder.DropColumn(
                name: "WelcomeAcknowledgedAt",
                table: "OpenAiAccounts");

            migrationBuilder.DropColumn(
                name: "IdToken",
                table: "OAuthTokens");
        }
    }
}
