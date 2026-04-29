using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GraphNodes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NodeType = table.Column<string>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: true),
                    Properties = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GraphNodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LlmProviders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    EndpointUrl = table.Column<string>(type: "TEXT", nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", nullable: false),
                    AuthType = table.Column<string>(type: "TEXT", nullable: false),
                    ApiKey = table.Column<string>(type: "TEXT", nullable: true),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    CredentialSourceId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LlmProviders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LlmProviders_LlmProviders_CredentialSourceId",
                        column: x => x.CredentialSourceId,
                        principalTable: "LlmProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "GraphEdges",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FromNodeId = table.Column<long>(type: "INTEGER", nullable: false),
                    ToNodeId = table.Column<long>(type: "INTEGER", nullable: false),
                    EdgeType = table.Column<string>(type: "TEXT", nullable: false),
                    Properties = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GraphEdges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GraphEdges_GraphNodes_FromNodeId",
                        column: x => x.FromNodeId,
                        principalTable: "GraphNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GraphEdges_GraphNodes_ToNodeId",
                        column: x => x.ToNodeId,
                        principalTable: "GraphNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OAuthTokens",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProviderId = table.Column<int>(type: "INTEGER", nullable: false),
                    AccessToken = table.Column<string>(type: "TEXT", nullable: false),
                    RefreshToken = table.Column<string>(type: "TEXT", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Scope = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OAuthTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OAuthTokens_LlmProviders_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "LlmProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GraphEdges_FromNodeId_EdgeType",
                table: "GraphEdges",
                columns: new[] { "FromNodeId", "EdgeType" });

            migrationBuilder.CreateIndex(
                name: "IX_GraphEdges_ToNodeId_EdgeType",
                table: "GraphEdges",
                columns: new[] { "ToNodeId", "EdgeType" });

            migrationBuilder.CreateIndex(
                name: "IX_GraphNodes_NodeType",
                table: "GraphNodes",
                column: "NodeType");

            migrationBuilder.CreateIndex(
                name: "IX_GraphNodes_NodeType_Key",
                table: "GraphNodes",
                columns: new[] { "NodeType", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LlmProviders_CredentialSourceId",
                table: "LlmProviders",
                column: "CredentialSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_LlmProviders_Name",
                table: "LlmProviders",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OAuthTokens_ProviderId",
                table: "OAuthTokens",
                column: "ProviderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GraphEdges");

            migrationBuilder.DropTable(
                name: "OAuthTokens");

            migrationBuilder.DropTable(
                name: "GraphNodes");

            migrationBuilder.DropTable(
                name: "LlmProviders");
        }
    }
}
