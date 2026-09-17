using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations;

/// <summary>
/// Moves extraction child identities from the legacy source-wide namespace to
/// the immutable extraction namespace, allowing historical extractions to stay
/// present when a retained original is normalized again.
/// </summary>
[Migration("20260917234500_AllowSourceReextractionM4")]
[DbContext(typeof(AppDbContext))]
public partial class AllowSourceReextractionM4 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_IngestSourceChunks_SourceId_Index", "IngestSourceChunks");
        migrationBuilder.DropIndex("IX_IngestSourceChunks_SourceExtractionVersionId", "IngestSourceChunks");
        migrationBuilder.CreateIndex(
            "IX_IngestSourceChunks_SourceExtractionVersionId_Index",
            "IngestSourceChunks",
            new[] { "SourceExtractionVersionId", "Index" },
            unique: true);
        migrationBuilder.CreateIndex("IX_IngestSourceChunks_SourceId", "IngestSourceChunks", "SourceId");

        migrationBuilder.DropIndex("IX_IngestSourcePages_SourceId_PageNumber", "IngestSourcePages");
        migrationBuilder.DropIndex("IX_IngestSourcePages_SourceId_StartChar", "IngestSourcePages");
        migrationBuilder.DropIndex("IX_IngestSourcePages_SourceExtractionVersionId", "IngestSourcePages");
        migrationBuilder.CreateIndex(
            "IX_IngestSourcePages_SourceExtractionVersionId_PageNumber",
            "IngestSourcePages",
            new[] { "SourceExtractionVersionId", "PageNumber" },
            unique: true);
        migrationBuilder.CreateIndex(
            "IX_IngestSourcePages_SourceExtractionVersionId_StartChar",
            "IngestSourcePages",
            new[] { "SourceExtractionVersionId", "StartChar" });
        migrationBuilder.CreateIndex("IX_IngestSourcePages_SourceId", "IngestSourcePages", "SourceId");

        migrationBuilder.DropIndex("IX_IngestSourceBlocks_SourceId_Index", "IngestSourceBlocks");
        migrationBuilder.DropIndex("IX_IngestSourceBlocks_SourceId_StartChar", "IngestSourceBlocks");
        migrationBuilder.CreateIndex(
            "IX_IngestSourceBlocks_SourceExtractionVersionId_StartChar",
            "IngestSourceBlocks",
            new[] { "SourceExtractionVersionId", "StartChar" });
        migrationBuilder.CreateIndex("IX_IngestSourceBlocks_SourceId", "IngestSourceBlocks", "SourceId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_IngestSourceChunks_SourceId", "IngestSourceChunks");
        migrationBuilder.DropIndex("IX_IngestSourceChunks_SourceExtractionVersionId_Index", "IngestSourceChunks");
        migrationBuilder.CreateIndex(
            "IX_IngestSourceChunks_SourceId_Index",
            "IngestSourceChunks",
            new[] { "SourceId", "Index" },
            unique: true);
        migrationBuilder.CreateIndex("IX_IngestSourceChunks_SourceExtractionVersionId", "IngestSourceChunks", "SourceExtractionVersionId");

        migrationBuilder.DropIndex("IX_IngestSourcePages_SourceId", "IngestSourcePages");
        migrationBuilder.DropIndex("IX_IngestSourcePages_SourceExtractionVersionId_PageNumber", "IngestSourcePages");
        migrationBuilder.DropIndex("IX_IngestSourcePages_SourceExtractionVersionId_StartChar", "IngestSourcePages");
        migrationBuilder.CreateIndex(
            "IX_IngestSourcePages_SourceId_PageNumber",
            "IngestSourcePages",
            new[] { "SourceId", "PageNumber" },
            unique: true);
        migrationBuilder.CreateIndex(
            "IX_IngestSourcePages_SourceId_StartChar",
            "IngestSourcePages",
            new[] { "SourceId", "StartChar" });
        migrationBuilder.CreateIndex("IX_IngestSourcePages_SourceExtractionVersionId", "IngestSourcePages", "SourceExtractionVersionId");

        migrationBuilder.DropIndex("IX_IngestSourceBlocks_SourceId", "IngestSourceBlocks");
        migrationBuilder.DropIndex("IX_IngestSourceBlocks_SourceExtractionVersionId_StartChar", "IngestSourceBlocks");
        migrationBuilder.CreateIndex(
            "IX_IngestSourceBlocks_SourceId_Index",
            "IngestSourceBlocks",
            new[] { "SourceId", "Index" },
            unique: true);
        migrationBuilder.CreateIndex(
            "IX_IngestSourceBlocks_SourceId_StartChar",
            "IngestSourceBlocks",
            new[] { "SourceId", "StartChar" });
    }
}
