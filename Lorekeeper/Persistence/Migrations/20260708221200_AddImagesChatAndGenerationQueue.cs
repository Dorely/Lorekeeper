using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddImagesChatAndGenerationQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProjectImageConversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectImageConversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectImageConversations_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectImageGenerationJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    Prompt = table.Column<string>(type: "TEXT", nullable: false),
                    Size = table.Column<string>(type: "TEXT", nullable: false),
                    Quality = table.Column<string>(type: "TEXT", nullable: false),
                    OutputFormat = table.Column<string>(type: "TEXT", nullable: false),
                    OutputCompression = table.Column<int>(type: "INTEGER", nullable: true),
                    Count = table.Column<int>(type: "INTEGER", nullable: false),
                    AltText = table.Column<string>(type: "TEXT", nullable: false),
                    SourceImageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    MaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReferenceImageIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    OutputImageIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    OutputStatesJson = table.Column<string>(type: "TEXT", nullable: false),
                    OutputErrorsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    MainlineModel = table.Column<string>(type: "TEXT", nullable: false),
                    ImageModel = table.Column<string>(type: "TEXT", nullable: false),
                    RawProviderResponseJson = table.Column<string>(type: "TEXT", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectImageGenerationJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectImageGenerationJobs_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectImageMasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ImageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", nullable: false),
                    Data = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false),
                    OwnerKind = table.Column<string>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectImageMasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectImageMasks_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectImageMasks_PublishAssets_ImageId",
                        column: x => x.ImageId,
                        principalTable: "PublishAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectImageMessages",
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
                    table.PrimaryKey("PK_ProjectImageMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectImageMessages_ProjectImageConversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "ProjectImageConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectImageMessageVisuals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    ToolCallId = table.Column<string>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Caption = table.Column<string>(type: "TEXT", nullable: false),
                    SourceKind = table.Column<string>(type: "TEXT", nullable: false),
                    SourceRefId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ContentType = table.Column<string>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: true),
                    Height = table.Column<int>(type: "INTEGER", nullable: true),
                    Data = table.Column<byte[]>(type: "BLOB", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectImageMessageVisuals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectImageMessageVisuals_ProjectImageMessages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "ProjectImageMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectImageConversations_ProjectId",
                table: "ProjectImageConversations",
                column: "ProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectImageGenerationJobs_MaskId",
                table: "ProjectImageGenerationJobs",
                column: "MaskId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectImageGenerationJobs_ProjectId_Kind_CreatedAt",
                table: "ProjectImageGenerationJobs",
                columns: new[] { "ProjectId", "Kind", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectImageGenerationJobs_ProjectId_Status_CreatedAt",
                table: "ProjectImageGenerationJobs",
                columns: new[] { "ProjectId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectImageGenerationJobs_SourceImageId",
                table: "ProjectImageGenerationJobs",
                column: "SourceImageId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectImageMasks_ImageId",
                table: "ProjectImageMasks",
                column: "ImageId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectImageMasks_ProjectId_ImageId_CreatedAt",
                table: "ProjectImageMasks",
                columns: new[] { "ProjectId", "ImageId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectImageMasks_ProjectId_OwnerKind_OwnerId",
                table: "ProjectImageMasks",
                columns: new[] { "ProjectId", "OwnerKind", "OwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectImageMessages_ConversationId_Order",
                table: "ProjectImageMessages",
                columns: new[] { "ConversationId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectImageMessageVisuals_MessageId_SortOrder",
                table: "ProjectImageMessageVisuals",
                columns: new[] { "MessageId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectImageMessageVisuals_ToolCallId_CreatedAt",
                table: "ProjectImageMessageVisuals",
                columns: new[] { "ToolCallId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectImageGenerationJobs");

            migrationBuilder.DropTable(
                name: "ProjectImageMasks");

            migrationBuilder.DropTable(
                name: "ProjectImageMessageVisuals");

            migrationBuilder.DropTable(
                name: "ProjectImageMessages");

            migrationBuilder.DropTable(
                name: "ProjectImageConversations");
        }
    }
}
