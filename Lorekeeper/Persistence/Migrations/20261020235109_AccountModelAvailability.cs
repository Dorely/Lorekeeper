using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AccountModelAvailability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastModelRefreshAt",
                table: "OpenAiAccounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastModelRefreshError",
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
                name: "AccountModelMetadataJson",
                table: "LlmProviders",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Earlier builds only understand Manual and BundledCatalog origins.
            migrationBuilder.Sql("UPDATE LlmProviders SET ModelOrigin = 'Manual' WHERE ModelOrigin = 'AccountDiscovered';");

            migrationBuilder.DropColumn(
                name: "LastModelRefreshAt",
                table: "OpenAiAccounts");

            migrationBuilder.DropColumn(
                name: "LastModelRefreshError",
                table: "OpenAiAccounts");

            migrationBuilder.DropColumn(
                name: "AccountAvailability",
                table: "LlmProviders");

            migrationBuilder.DropColumn(
                name: "AccountAvailabilityCheckedAt",
                table: "LlmProviders");

            migrationBuilder.DropColumn(
                name: "AccountModelMetadataJson",
                table: "LlmProviders");
        }
    }
}
