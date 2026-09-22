using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations;

public partial class AddVoiceWorkspace : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE WritingCoachConversations RENAME TO VoiceConversations;
            ALTER TABLE WritingCoachMessages RENAME TO VoiceMessages;
            DROP INDEX IX_WritingCoachConversations_ProjectId;
            DROP INDEX IX_WritingCoachMessages_ConversationId_Order;
            CREATE UNIQUE INDEX IX_VoiceConversations_ProjectId ON VoiceConversations (ProjectId);
            CREATE INDEX IX_VoiceMessages_ConversationId_Order ON VoiceMessages (ConversationId, "Order");
            UPDATE ChatMessageImageAttachments SET Surface = 'Voice' WHERE Surface = 'WritingCoach';
            """);
        migrationBuilder.AddColumn<long>("Revision", "WritingSamples", type: "INTEGER", nullable: false, defaultValue: 0L);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE VoiceConversations RENAME TO WritingCoachConversations;
            ALTER TABLE VoiceMessages RENAME TO WritingCoachMessages;
            DROP INDEX IX_VoiceConversations_ProjectId;
            DROP INDEX IX_VoiceMessages_ConversationId_Order;
            CREATE UNIQUE INDEX IX_WritingCoachConversations_ProjectId ON WritingCoachConversations (ProjectId);
            CREATE INDEX IX_WritingCoachMessages_ConversationId_Order ON WritingCoachMessages (ConversationId, "Order");
            UPDATE ChatMessageImageAttachments SET Surface = 'WritingCoach' WHERE Surface = 'Voice';
            """);
        migrationBuilder.DropColumn("Revision", "WritingSamples");
    }
}
