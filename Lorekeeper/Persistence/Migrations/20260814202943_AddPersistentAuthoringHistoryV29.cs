using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPersistentAuthoringHistoryV29 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DetachedAt",
                table: "PageCompositionVariants",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DetachedAt",
                table: "PageCompositions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AuthoringHistoryStreams",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StreamKey = table.Column<string>(type: "TEXT", nullable: false),
                    DocumentKind = table.Column<string>(type: "TEXT", nullable: false),
                    DocumentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EditionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    BaselineSnapshot = table.Column<byte[]>(type: "BLOB", nullable: false),
                    BaselineHash = table.Column<string>(type: "TEXT", nullable: false),
                    FirstSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    CursorSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthoringHistoryStreams", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuthoringHistoryStreams_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AuthoringHistoryEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StreamId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    ActionLabel = table.Column<string>(type: "TEXT", nullable: false),
                    Origin = table.Column<string>(type: "TEXT", nullable: false),
                    AssistantTurnId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ResultSnapshot = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ResultHash = table.Column<string>(type: "TEXT", nullable: false),
                    SelectionJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthoringHistoryEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuthoringHistoryEntries_AuthoringHistoryStreams_StreamId",
                        column: x => x.StreamId,
                        principalTable: "AuthoringHistoryStreams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AuthoringTurnHistoryBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StreamId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AssistantTurnId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActionLabel = table.Column<string>(type: "TEXT", nullable: false),
                    BeforeSnapshot = table.Column<byte[]>(type: "BLOB", nullable: false),
                    BeforeHash = table.Column<string>(type: "TEXT", nullable: false),
                    AfterSnapshot = table.Column<byte[]>(type: "BLOB", nullable: false),
                    AfterHash = table.Column<string>(type: "TEXT", nullable: false),
                    SelectionJson = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinalizedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthoringTurnHistoryBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuthoringTurnHistoryBatches_AuthoringHistoryStreams_StreamId",
                        column: x => x.StreamId,
                        principalTable: "AuthoringHistoryStreams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AuthoringHistoryDependencies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TurnBatchId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    ResourceId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthoringHistoryDependencies", x => x.Id);
                    table.CheckConstraint("CK_AuthoringHistoryDependencies_Owner", "(\"EntryId\" IS NULL) <> (\"TurnBatchId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_AuthoringHistoryDependencies_AuthoringHistoryEntries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "AuthoringHistoryEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AuthoringHistoryDependencies_AuthoringTurnHistoryBatches_TurnBatchId",
                        column: x => x.TurnBatchId,
                        principalTable: "AuthoringTurnHistoryBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuthoringHistoryDependencies_EntryId",
                table: "AuthoringHistoryDependencies",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_AuthoringHistoryDependencies_Kind_ResourceId",
                table: "AuthoringHistoryDependencies",
                columns: new[] { "Kind", "ResourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuthoringHistoryDependencies_TurnBatchId",
                table: "AuthoringHistoryDependencies",
                column: "TurnBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_AuthoringHistoryEntries_AssistantTurnId",
                table: "AuthoringHistoryEntries",
                column: "AssistantTurnId");

            migrationBuilder.CreateIndex(
                name: "IX_AuthoringHistoryEntries_StreamId_Sequence",
                table: "AuthoringHistoryEntries",
                columns: new[] { "StreamId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthoringHistoryStreams_ProjectId_DocumentKind_DocumentId",
                table: "AuthoringHistoryStreams",
                columns: new[] { "ProjectId", "DocumentKind", "DocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuthoringHistoryStreams_ProjectId_StreamKey",
                table: "AuthoringHistoryStreams",
                columns: new[] { "ProjectId", "StreamKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthoringTurnHistoryBatches_AssistantTurnId_StreamId",
                table: "AuthoringTurnHistoryBatches",
                columns: new[] { "AssistantTurnId", "StreamId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthoringTurnHistoryBatches_Status_UpdatedAt",
                table: "AuthoringTurnHistoryBatches",
                columns: new[] { "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuthoringTurnHistoryBatches_StreamId",
                table: "AuthoringTurnHistoryBatches",
                column: "StreamId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuthoringHistoryDependencies");

            migrationBuilder.DropTable(
                name: "AuthoringHistoryEntries");

            migrationBuilder.DropTable(
                name: "AuthoringTurnHistoryBatches");

            migrationBuilder.DropTable(
                name: "AuthoringHistoryStreams");

            migrationBuilder.DropColumn(
                name: "DetachedAt",
                table: "PageCompositionVariants");

            migrationBuilder.DropColumn(
                name: "DetachedAt",
                table: "PageCompositions");
        }
    }
}
