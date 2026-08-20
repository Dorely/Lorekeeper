using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations;

/// <summary>
/// Adds the nullable per-conversation chat model override. These are soft scalar
/// references by design: deleting a provider must leave an unavailable override
/// visible to the chat picker instead of silently changing the conversation's
/// selected model.
/// </summary>
public partial class AddChatConversationModelSelection : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "SelectedProviderId",
            table: "EditorConversations",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "SelectedProviderId",
            table: "OutlineConversations",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "SelectedProviderId",
            table: "WritingCoachConversations",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "SelectedProviderId",
            table: "ResearchConversations",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "SelectedProviderId",
            table: "ProjectImageConversations",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "SelectedProviderId",
            table: "PublishConversations",
            type: "INTEGER",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "SelectedProviderId",
            table: "EditorConversations");

        migrationBuilder.DropColumn(
            name: "SelectedProviderId",
            table: "OutlineConversations");

        migrationBuilder.DropColumn(
            name: "SelectedProviderId",
            table: "WritingCoachConversations");

        migrationBuilder.DropColumn(
            name: "SelectedProviderId",
            table: "ResearchConversations");

        migrationBuilder.DropColumn(
            name: "SelectedProviderId",
            table: "ProjectImageConversations");

        migrationBuilder.DropColumn(
            name: "SelectedProviderId",
            table: "PublishConversations");
    }
}
