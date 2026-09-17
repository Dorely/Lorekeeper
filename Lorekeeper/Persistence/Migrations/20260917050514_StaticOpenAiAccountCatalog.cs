using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StaticOpenAiAccountCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastCatalogRefreshAt",
                table: "OpenAiAccounts");

            migrationBuilder.DropColumn(
                name: "LastCatalogRefreshError",
                table: "OpenAiAccounts");

            migrationBuilder.DropColumn(
                name: "AccountAvailability",
                table: "LlmProviders");

            migrationBuilder.DropColumn(
                name: "AccountAvailabilityCheckedAt",
                table: "LlmProviders");

            migrationBuilder.DropColumn(
                name: "AccountAvailabilityError",
                table: "LlmProviders");

            migrationBuilder.DropColumn(
                name: "DiscoveredContextWindowTokens",
                table: "LlmProviders");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastCatalogRefreshAt",
                table: "OpenAiAccounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastCatalogRefreshError",
                table: "OpenAiAccounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AccountAvailability",
                table: "LlmProviders",
                type: "TEXT",
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<DateTime>(
                name: "AccountAvailabilityCheckedAt",
                table: "LlmProviders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AccountAvailabilityError",
                table: "LlmProviders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DiscoveredContextWindowTokens",
                table: "LlmProviders",
                type: "INTEGER",
                nullable: true);
        }
    }
}
