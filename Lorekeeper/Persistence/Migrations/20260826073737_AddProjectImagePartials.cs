using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectImagePartials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProjectImagePartials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OutputIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    PartialImageIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", nullable: false),
                    Data = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    MainlineModel = table.Column<string>(type: "TEXT", nullable: false),
                    ImageModel = table.Column<string>(type: "TEXT", nullable: false),
                    RequestId = table.Column<string>(type: "TEXT", nullable: true),
                    ResponseId = table.Column<string>(type: "TEXT", nullable: true),
                    CallId = table.Column<string>(type: "TEXT", nullable: true),
                    ItemId = table.Column<string>(type: "TEXT", nullable: true),
                    LastEventType = table.Column<string>(type: "TEXT", nullable: true),
                    EventCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FinalOutputImageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectImagePartials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectImagePartials_ProjectImageGenerationJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "ProjectImageGenerationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectImagePartials_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectImagePartials_PublishAssets_FinalOutputImageId",
                        column: x => x.FinalOutputImageId,
                        principalTable: "PublishAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectImagePartials_FinalOutputImageId",
                table: "ProjectImagePartials",
                column: "FinalOutputImageId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectImagePartials_JobId_OutputIndex_Attempt_PartialImageIndex",
                table: "ProjectImagePartials",
                columns: new[] { "JobId", "OutputIndex", "Attempt", "PartialImageIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectImagePartials_ProjectId_CreatedAt",
                table: "ProjectImagePartials",
                columns: new[] { "ProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectImagePartials_ProjectId_JobId_OutputIndex_Attempt",
                table: "ProjectImagePartials",
                columns: new[] { "ProjectId", "JobId", "OutputIndex", "Attempt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectImagePartials");
        }
    }
}
