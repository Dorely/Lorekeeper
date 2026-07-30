using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PublicationPressArtifactsV11 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PublicationRenderJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EditionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    SourceFingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    RendererVersion = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", nullable: false),
                    DiagnosticsJson = table.Column<string>(type: "TEXT", nullable: false),
                    EvidenceJson = table.Column<string>(type: "TEXT", nullable: false),
                    ProgressPercent = table.Column<int>(type: "INTEGER", nullable: false),
                    ProgressMessage = table.Column<string>(type: "TEXT", nullable: false),
                    CancellationRequested = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationRenderJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublicationRenderJobs_PublicationEditions_EditionId",
                        column: x => x.EditionId,
                        principalTable: "PublicationEditions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PublicationArtifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EditionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RenderJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", nullable: false),
                    Data = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", nullable: false),
                    ByteLength = table.Column<long>(type: "INTEGER", nullable: false),
                    PageCount = table.Column<int>(type: "INTEGER", nullable: true),
                    SourceFingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    RendererVersion = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationArtifacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublicationArtifacts_PublicationEditions_EditionId",
                        column: x => x.EditionId,
                        principalTable: "PublicationEditions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PublicationArtifacts_PublicationRenderJobs_RenderJobId",
                        column: x => x.RenderJobId,
                        principalTable: "PublicationRenderJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PublicationPageMapEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RenderJobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChapterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BlockId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PageNumber = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationPageMapEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublicationPageMapEntries_PublicationRenderJobs_RenderJobId",
                        column: x => x.RenderJobId,
                        principalTable: "PublicationRenderJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PublicationArtifacts_EditionId_Kind_CreatedAt",
                table: "PublicationArtifacts",
                columns: new[] { "EditionId", "Kind", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PublicationArtifacts_RenderJobId_Kind",
                table: "PublicationArtifacts",
                columns: new[] { "RenderJobId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublicationPageMapEntries_RenderJobId_ChapterId_BlockId",
                table: "PublicationPageMapEntries",
                columns: new[] { "RenderJobId", "ChapterId", "BlockId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublicationPageMapEntries_RenderJobId_PageNumber",
                table: "PublicationPageMapEntries",
                columns: new[] { "RenderJobId", "PageNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_PublicationRenderJobs_EditionId_CreatedAt",
                table: "PublicationRenderJobs",
                columns: new[] { "EditionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PublicationRenderJobs_Status_CreatedAt",
                table: "PublicationRenderJobs",
                columns: new[] { "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PublicationArtifacts");

            migrationBuilder.DropTable(
                name: "PublicationPageMapEntries");

            migrationBuilder.DropTable(
                name: "PublicationRenderJobs");
        }
    }
}
