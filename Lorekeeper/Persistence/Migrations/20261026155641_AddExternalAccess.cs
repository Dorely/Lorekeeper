using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExternalAccessSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalAccessSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExternalMutationReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientGrantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: true),
                    MutationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Operation = table.Column<string>(type: "TEXT", nullable: false),
                    RequestHash = table.Column<string>(type: "TEXT", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalMutationReceipts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExternalProjectLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ExternalKey = table.Column<string>(type: "TEXT", nullable: false),
                    BaselineCaptureId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalProjectLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExternalProjectLinks_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "McpClientGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CredentialHash = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedCredential = table.Column<string>(type: "TEXT", nullable: false),
                    PermissionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ProjectIdsJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpClientGrants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExternalSourceIdentities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LinkId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExternalKey = table.Column<string>(type: "TEXT", nullable: false),
                    TargetKind = table.Column<string>(type: "TEXT", nullable: false),
                    TargetId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalSourceIdentities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExternalSourceIdentities_ExternalProjectLinks_LinkId",
                        column: x => x.LinkId,
                        principalTable: "ExternalProjectLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExternalTransferCaptures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LinkId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PreviousBaselineId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ExpectedLinkRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    ManifestJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AcknowledgedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalTransferCaptures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExternalTransferCaptures_ExternalProjectLinks_LinkId",
                        column: x => x.LinkId,
                        principalTable: "ExternalProjectLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExternalWorks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientGrantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Task = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    HasMutations = table.Column<bool>(type: "INTEGER", nullable: false),
                    CheckpointRequestKey = table.Column<string>(type: "TEXT", nullable: true),
                    CompletionError = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalWorks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExternalWorks_McpClientGrants_ClientGrantId",
                        column: x => x.ClientGrantId,
                        principalTable: "McpClientGrants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExternalWorks_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExternalWorkspaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Surface = table.Column<string>(type: "TEXT", nullable: false),
                    ChapterId = table.Column<Guid>(type: "TEXT", nullable: true),
                    EditionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalWorkspaces", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExternalWorkspaces_ExternalWorks_WorkId",
                        column: x => x.WorkId,
                        principalTable: "ExternalWorks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalMutationReceipts_ClientGrantId_MutationId",
                table: "ExternalMutationReceipts",
                columns: new[] { "ClientGrantId", "MutationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalProjectLinks_ProjectId_ExternalKey",
                table: "ExternalProjectLinks",
                columns: new[] { "ProjectId", "ExternalKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalSourceIdentities_LinkId_ExternalKey",
                table: "ExternalSourceIdentities",
                columns: new[] { "LinkId", "ExternalKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalTransferCaptures_LinkId",
                table: "ExternalTransferCaptures",
                column: "LinkId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalWorks_ClientGrantId",
                table: "ExternalWorks",
                column: "ClientGrantId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalWorks_ProjectId",
                table: "ExternalWorks",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalWorkspaces_WorkId",
                table: "ExternalWorkspaces",
                column: "WorkId");

            migrationBuilder.CreateIndex(
                name: "IX_McpClientGrants_CredentialHash",
                table: "McpClientGrants",
                column: "CredentialHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExternalAccessSettings");

            migrationBuilder.DropTable(
                name: "ExternalMutationReceipts");

            migrationBuilder.DropTable(
                name: "ExternalSourceIdentities");

            migrationBuilder.DropTable(
                name: "ExternalTransferCaptures");

            migrationBuilder.DropTable(
                name: "ExternalWorkspaces");

            migrationBuilder.DropTable(
                name: "ExternalProjectLinks");

            migrationBuilder.DropTable(
                name: "ExternalWorks");

            migrationBuilder.DropTable(
                name: "McpClientGrants");
        }
    }
}
