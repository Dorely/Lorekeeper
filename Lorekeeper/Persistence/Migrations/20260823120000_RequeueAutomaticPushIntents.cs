using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20260823120000_RequeueAutomaticPushIntents")]
    public partial class RequeueAutomaticPushIntents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Migration 20260823090000 could mark an automatic push as
            // Succeeded after a local-only tracking-ref update. Requeue those
            // the current-head row for each attached repository so corrected
            // GitHub API verification observes the actual remote ref without
            // replaying every historical push intent.
            migrationBuilder.Sql(
                """
                UPDATE ProjectVersionOperations
                SET Status = 'Pending',
                    IsResumable = 1,
                    ErrorCode = NULL,
                    ErrorMessage = NULL,
                    AcknowledgedAt = NULL,
                    StartedAt = NULL,
                    HeartbeatAt = NULL,
                    CompletedAt = NULL,
                    UpdatedAt = CURRENT_TIMESTAMP
                WHERE Kind = 'AutoPush'
                  AND Status = 'Succeeded'
                  AND ProjectGitRemoteId IS NOT NULL
                  AND TargetCommitSha IS NOT NULL
                  AND TargetCommitSha = (
                      SELECT repository.HeadCommitSha
                      FROM ProjectVersionRepositories AS repository
                      WHERE repository.Id = ProjectVersionOperations.ProjectVersionRepositoryId
                  );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // This data correction is intentionally not reversed: restoring a
            // false-success state would make the runtime claim an unverified
            // remote update again.
        }
    }
}
