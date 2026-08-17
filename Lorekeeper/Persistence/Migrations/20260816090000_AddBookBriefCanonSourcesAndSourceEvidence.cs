using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260816090000_AddBookBriefCanonSourcesAndSourceEvidence")]
public partial class AddBookBriefCanonSourcesAndSourceEvidence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "BookBriefCanonSources",
            columns: table => new
            {
                BookBriefId = table.Column<Guid>(type: "TEXT", nullable: false),
                IngestSourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BookBriefCanonSources", item => new { item.BookBriefId, item.IngestSourceId });
                table.ForeignKey(
                    name: "FK_BookBriefCanonSources_BookBriefs_BookBriefId",
                    column: item => item.BookBriefId,
                    principalTable: "BookBriefs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_BookBriefCanonSources_IngestSources_IngestSourceId",
                    column: item => item.IngestSourceId,
                    principalTable: "IngestSources",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_BookBriefCanonSources_IngestSourceId",
            table: "BookBriefCanonSources",
            column: "IngestSourceId");

        migrationBuilder.Sql("""
            UPDATE GraphNodes
            SET Properties = replace(
                replace(Properties, '"canonSource.', '"sourceEvidence.'),
                '"canonSourceMetaJson"', '"sourceEvidenceMetaJson"')
            WHERE Properties LIKE '%"canonSource.%'
               OR Properties LIKE '%"canonSourceMetaJson"%';

            UPDATE GraphEdges
            SET Properties = replace(
                replace(Properties, '"canonSource.', '"sourceEvidence.'),
                '"canonSourceMetaJson"', '"sourceEvidenceMetaJson"')
            WHERE Properties LIKE '%"canonSource.%'
               OR Properties LIKE '%"canonSourceMetaJson"%';

            UPDATE GraphEdges
            SET EdgeType = 'RelevantTo', UpdatedAt = CURRENT_TIMESTAMP
            WHERE EdgeType = 'AppearsIn'
              AND ToNodeId IN (SELECT Id FROM GraphNodes WHERE NodeType = 'Chapter');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE GraphNodes
            SET Properties = replace(
                replace(Properties, '"sourceEvidence.', '"canonSource.'),
                '"sourceEvidenceMetaJson"', '"canonSourceMetaJson"')
            WHERE Properties LIKE '%"sourceEvidence.%'
               OR Properties LIKE '%"sourceEvidenceMetaJson"%';

            UPDATE GraphEdges
            SET Properties = replace(
                replace(Properties, '"sourceEvidence.', '"canonSource.'),
                '"sourceEvidenceMetaJson"', '"canonSourceMetaJson"')
            WHERE Properties LIKE '%"sourceEvidence.%'
               OR Properties LIKE '%"sourceEvidenceMetaJson"%';

            """);

        migrationBuilder.DropTable(name: "BookBriefCanonSources");
    }
}
