using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SourceJobProcessingModes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Mode",
                table: "IngestJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "ExtractEntities");

            migrationBuilder.AddColumn<Guid>(
                name: "SourceExtractionVersionId",
                table: "IngestJobs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The preceding historical migration has no target-model metadata.
            // SQLite can remove these unindexed columns without EF rebuilding
            // the table from that absent model.
            migrationBuilder.Sql("ALTER TABLE \"IngestJobs\" DROP COLUMN \"Mode\";");
            migrationBuilder.Sql("ALTER TABLE \"IngestJobs\" DROP COLUMN \"SourceExtractionVersionId\";");
        }
    }
}
