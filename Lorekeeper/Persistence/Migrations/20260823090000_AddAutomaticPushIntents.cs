using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20260823090000_AddAutomaticPushIntents")]
    public partial class AddAutomaticPushIntents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProjectGitRemoteId",
                table: "ProjectVersionOperations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetCommitSha",
                table: "ProjectVersionOperations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectVersionOperations_ProjectGitRemoteId_TargetCommitSha",
                table: "ProjectVersionOperations",
                columns: new[] { "ProjectGitRemoteId", "TargetCommitSha" },
                unique: true);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProjectVersionOperations_ProjectGitRemoteId_TargetCommitSha",
                table: "ProjectVersionOperations");

            migrationBuilder.DropColumn(
                name: "ProjectGitRemoteId",
                table: "ProjectVersionOperations");

            migrationBuilder.DropColumn(
                name: "TargetCommitSha",
                table: "ProjectVersionOperations");
        }
    }
}
