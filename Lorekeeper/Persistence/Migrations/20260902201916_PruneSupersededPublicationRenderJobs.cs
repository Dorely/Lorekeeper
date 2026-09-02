using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PruneSupersededPublicationRenderJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE FROM PublicationRenderJobs
                WHERE Status NOT IN ('Queued', 'Rendering')
                  AND Id NOT IN (
                      SELECT KeepId FROM (
                          SELECT Id AS KeepId, ROW_NUMBER() OVER (
                              PARTITION BY ProjectId, IFNULL(EditionId, '00000000-0000-0000-0000-000000000000'), Scope
                              ORDER BY CreatedAt DESC, Id DESC) AS RowNumber
                          FROM PublicationRenderJobs
                          WHERE Status NOT IN ('Queued', 'Rendering')
                            AND EXISTS (
                                SELECT 1 FROM PublicationArtifacts
                                WHERE PublicationArtifacts.RenderJobId = PublicationRenderJobs.Id)
                      ) WHERE RowNumber = 1);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
