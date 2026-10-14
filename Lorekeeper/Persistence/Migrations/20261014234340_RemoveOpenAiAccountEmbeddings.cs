using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations;

/// <summary>
/// OpenAI account authorization no longer provides embeddings, so an active embedding
/// configuration on an account-backed connection is removed. Semantic features stay
/// disabled until the user selects another embedding connection; stored vectors remain
/// and are rebuilt by the next configured model.
/// </summary>
public partial class RemoveOpenAiAccountEmbeddings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DELETE FROM EmbeddingConfigurations
            WHERE ProviderId IN (SELECT Id FROM LlmProviders WHERE OpenAiAccountId IS NOT NULL);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
