using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PurgeTerminalJobsWithChildren : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM ProjectImagePartials
                WHERE JobId IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM ProjectImageGenerationJobs j WHERE j.Id = ProjectImagePartials.JobId);

                DELETE FROM IngestJobChunks
                WHERE JobId IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM IngestJobs j WHERE j.Id = IngestJobChunks.JobId);

                DELETE FROM IngestJobEvents
                WHERE JobId IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM IngestJobs j WHERE j.Id = IngestJobEvents.JobId);

                DELETE FROM IngestReportItems
                WHERE JobId IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM IngestJobs j WHERE j.Id = IngestReportItems.JobId);

                DELETE FROM IngestStagingRecords
                WHERE JobId IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM IngestJobs j WHERE j.Id = IngestStagingRecords.JobId);

                DELETE FROM ProjectImportReportItems
                WHERE JobId IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM ProjectImportJobs j WHERE j.Id = ProjectImportReportItems.JobId);

                DELETE FROM EditorRevisionMessages
                WHERE SessionId IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM EditorRevisionSessions s WHERE s.Id = EditorRevisionMessages.SessionId);

                DELETE FROM EditorRevisionSessions
                WHERE JobId IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM EditorRevisionJobs j WHERE j.Id = EditorRevisionSessions.JobId);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
