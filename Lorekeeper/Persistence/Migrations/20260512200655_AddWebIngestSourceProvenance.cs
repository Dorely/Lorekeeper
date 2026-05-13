using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWebIngestSourceProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CanonicalUrl",
                table: "IngestSources",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ContentType",
                table: "IngestSources",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "FetchedAt",
                table: "IngestSources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FinalUrl",
                table: "IngestSources",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SourceMetadataJson",
                table: "IngestSources",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "SourceUrl",
                table: "IngestSources",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CanonicalUrl",
                table: "IngestSources");

            migrationBuilder.DropColumn(
                name: "ContentType",
                table: "IngestSources");

            migrationBuilder.DropColumn(
                name: "FetchedAt",
                table: "IngestSources");

            migrationBuilder.DropColumn(
                name: "FinalUrl",
                table: "IngestSources");

            migrationBuilder.DropColumn(
                name: "SourceMetadataJson",
                table: "IngestSources");

            migrationBuilder.DropColumn(
                name: "SourceUrl",
                table: "IngestSources");
        }
    }
}
