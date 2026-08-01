using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LorekeeperPressV15 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsLegacy",
                table: "PublicationRenderJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsLegacy",
                table: "PublicationArtifacts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(
                """
                UPDATE PublicationEditions
                SET VendorProfileVersion = CASE
                    WHEN Format = 'Epub' THEN 'epub3-v1'
                    WHEN Vendor = 'AmazonKdp' THEN 'kdp-paperback-v1'
                    WHEN Vendor = 'IngramSpark' THEN 'ingram-paperback-pdfx1a-v1'
                    ELSE 'generic-paperback-v1'
                END
                WHERE VendorProfileVersion IN (
                    'preview-1',
                    'kdp-paperback-6x9-preview-v1',
                    'ingram-pdf-x1a-preview-v1');

                UPDATE PublicationRenderJobs
                SET IsLegacy = 1
                WHERE Status NOT IN ('Queued', 'Rendering');

                UPDATE PublicationArtifacts
                SET IsLegacy = 1;

                UPDATE PublicationRenderJobs
                SET ProfileId = COALESCE((
                        SELECT CASE
                            WHEN e.Vendor = 'AmazonKdp' THEN 'kdp-paperback-v1'
                            WHEN e.Vendor = 'IngramSpark' THEN 'ingram-paperback-pdfx1a-v1'
                            ELSE 'generic-paperback-v1'
                        END
                        FROM PublicationEditions e
                        WHERE e.Id = PublicationRenderJobs.EditionId),
                    ProfileId),
                    RendererVersion = '',
                    Status = 'Queued',
                    ProgressPercent = 0,
                    ProgressMessage = 'Recovered for Lorekeeper Press 1.0',
                    StartedAt = NULL,
                    CompletedAt = NULL,
                    IsLegacy = 0
                WHERE Status IN ('Queued', 'Rendering');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsLegacy",
                table: "PublicationRenderJobs");

            migrationBuilder.DropColumn(
                name: "IsLegacy",
                table: "PublicationArtifacts");
        }
    }
}
