using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchProvidersAndResearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ResearchConversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResearchConversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResearchConversations_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SearchProviders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    ProviderKind = table.Column<string>(type: "TEXT", nullable: false),
                    ApiKey = table.Column<string>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConfigurationJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SearchProviders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebIngestCandidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ResearchConversationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SearchProviderId = table.Column<int>(type: "INTEGER", nullable: true),
                    IngestJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DiscoveryKind = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    FinalUrl = table.Column<string>(type: "TEXT", nullable: false),
                    CanonicalUrl = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayUrl = table.Column<string>(type: "TEXT", nullable: false),
                    ParentUrl = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Snippet = table.Column<string>(type: "TEXT", nullable: false),
                    Excerpt = table.Column<string>(type: "TEXT", nullable: false),
                    ExtractedText = table.Column<string>(type: "TEXT", nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", nullable: false),
                    SourceProviderName = table.Column<string>(type: "TEXT", nullable: false),
                    SearchQuery = table.Column<string>(type: "TEXT", nullable: false),
                    SearchRank = table.Column<int>(type: "INTEGER", nullable: true),
                    CrawlDepth = table.Column<int>(type: "INTEGER", nullable: false),
                    StageRationale = table.Column<string>(type: "TEXT", nullable: false),
                    Diagnostics = table.Column<string>(type: "TEXT", nullable: false),
                    RawMetadataJson = table.Column<string>(type: "TEXT", nullable: false),
                    FetchedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    StagedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    QueuedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebIngestCandidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebIngestCandidates_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ResearchMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    ToolCallsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ToolCallId = table.Column<string>(type: "TEXT", nullable: true),
                    ToolName = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResearchMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResearchMessages_ResearchConversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "ResearchConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ResearchConversations_ProjectId",
                table: "ResearchConversations",
                column: "ProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResearchMessages_ConversationId_Order",
                table: "ResearchMessages",
                columns: new[] { "ConversationId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_SearchProviders_IsActive",
                table: "SearchProviders",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_SearchProviders_Name",
                table: "SearchProviders",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebIngestCandidates_IngestJobId",
                table: "WebIngestCandidates",
                column: "IngestJobId");

            migrationBuilder.CreateIndex(
                name: "IX_WebIngestCandidates_ProjectId_Status_CreatedAt",
                table: "WebIngestCandidates",
                columns: new[] { "ProjectId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WebIngestCandidates_ResearchConversationId_CreatedAt",
                table: "WebIngestCandidates",
                columns: new[] { "ResearchConversationId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ResearchMessages");

            migrationBuilder.DropTable(
                name: "SearchProviders");

            migrationBuilder.DropTable(
                name: "WebIngestCandidates");

            migrationBuilder.DropTable(
                name: "ResearchConversations");
        }
    }
}
