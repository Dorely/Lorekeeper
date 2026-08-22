using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectVersionHistoryFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GitHubConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GitHubUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    AccountLogin = table.Column<string>(type: "TEXT", nullable: false),
                    AccessToken = table.Column<string>(type: "TEXT", nullable: false),
                    AccessTokenExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Scope = table.Column<string>(type: "TEXT", nullable: true),
                    LastValidatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitHubConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProjectVersionRepositories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreativeRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    LastCheckpointRevision = table.Column<long>(type: "INTEGER", nullable: true),
                    LastCheckpointContentHash = table.Column<string>(type: "TEXT", nullable: true),
                    HeadCommitSha = table.Column<string>(type: "TEXT", nullable: true),
                    HeadContentHash = table.Column<string>(type: "TEXT", nullable: true),
                    LastCheckpointAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectVersionRepositories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectVersionRepositories_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectGitRemotes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectVersionRepositoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GitHubConnectionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RemoteName = table.Column<string>(type: "TEXT", nullable: false),
                    Owner = table.Column<string>(type: "TEXT", nullable: false),
                    RepositoryName = table.Column<string>(type: "TEXT", nullable: false),
                    GitHubRepositoryId = table.Column<long>(type: "INTEGER", nullable: true),
                    CloneUrl = table.Column<string>(type: "TEXT", nullable: true),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: true),
                    DefaultBranch = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectGitRemotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectGitRemotes_GitHubConnections_GitHubConnectionId",
                        column: x => x.GitHubConnectionId,
                        principalTable: "GitHubConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectGitRemotes_ProjectVersionRepositories_ProjectVersionRepositoryId",
                        column: x => x.ProjectVersionRepositoryId,
                        principalTable: "ProjectVersionRepositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectVersionCheckpoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectVersionRepositoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ManifestSchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    CreativeRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    ManifestHash = table.Column<string>(type: "TEXT", nullable: false),
                    CommitSha = table.Column<string>(type: "TEXT", nullable: true),
                    ParentCommitSha = table.Column<string>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectVersionCheckpoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectVersionCheckpoints_ProjectVersionRepositories_ProjectVersionRepositoryId",
                        column: x => x.ProjectVersionRepositoryId,
                        principalTable: "ProjectVersionRepositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectVersionOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectVersionRepositoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    RequestKey = table.Column<string>(type: "TEXT", nullable: true),
                    IsResumable = table.Column<bool>(type: "INTEGER", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorCode = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    HeartbeatAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectVersionOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectVersionOperations_ProjectVersionRepositories_ProjectVersionRepositoryId",
                        column: x => x.ProjectVersionRepositoryId,
                        principalTable: "ProjectVersionRepositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GitHubConnections_GitHubUserId",
                table: "GitHubConnections",
                column: "GitHubUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectGitRemotes_GitHubConnectionId",
                table: "ProjectGitRemotes",
                column: "GitHubConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectGitRemotes_ProjectVersionRepositoryId_Owner_RepositoryName",
                table: "ProjectGitRemotes",
                columns: new[] { "ProjectVersionRepositoryId", "Owner", "RepositoryName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectGitRemotes_ProjectVersionRepositoryId_RemoteName",
                table: "ProjectGitRemotes",
                columns: new[] { "ProjectVersionRepositoryId", "RemoteName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectVersionCheckpoints_ProjectVersionRepositoryId_ContentHash",
                table: "ProjectVersionCheckpoints",
                columns: new[] { "ProjectVersionRepositoryId", "ContentHash" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectVersionCheckpoints_ProjectVersionRepositoryId_CreatedAt",
                table: "ProjectVersionCheckpoints",
                columns: new[] { "ProjectVersionRepositoryId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectVersionOperations_ProjectVersionRepositoryId_CreatedAt",
                table: "ProjectVersionOperations",
                columns: new[] { "ProjectVersionRepositoryId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectVersionOperations_ProjectVersionRepositoryId_RequestKey",
                table: "ProjectVersionOperations",
                columns: new[] { "ProjectVersionRepositoryId", "RequestKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectVersionOperations_ProjectVersionRepositoryId_Status_UpdatedAt",
                table: "ProjectVersionOperations",
                columns: new[] { "ProjectVersionRepositoryId", "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectVersionRepositories_HeadCommitSha_HeadContentHash",
                table: "ProjectVersionRepositories",
                columns: new[] { "HeadCommitSha", "HeadContentHash" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectVersionRepositories_ProjectId",
                table: "ProjectVersionRepositories",
                column: "ProjectId",
                unique: true);

            // Existing projects receive an app-managed repository identity at
            // cutover. No snapshot or Git object is created by this migration.
            migrationBuilder.Sql("""
                INSERT INTO "ProjectVersionRepositories" (
                    "Id",
                    "ProjectId",
                    "CreativeRevision",
                    "CreatedAt",
                    "UpdatedAt")
                SELECT
                    lower(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-' ||
                        hex(randomblob(2)) || '-' || hex(randomblob(2)) || '-' ||
                        hex(randomblob(6))),
                    project."Id",
                    0,
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                FROM "Projects" AS project
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM "ProjectVersionRepositories" AS repository
                    WHERE repository."ProjectId" = project."Id");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectGitRemotes");

            migrationBuilder.DropTable(
                name: "ProjectVersionCheckpoints");

            migrationBuilder.DropTable(
                name: "ProjectVersionOperations");

            migrationBuilder.DropTable(
                name: "GitHubConnections");

            migrationBuilder.DropTable(
                name: "ProjectVersionRepositories");
        }
    }
}
