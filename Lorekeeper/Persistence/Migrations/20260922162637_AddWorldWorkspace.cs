using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations;

public partial class AddWorldWorkspace : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE ResearchConversations RENAME TO WorldConversations;
            ALTER TABLE ResearchMessages RENAME TO WorldMessages;
            DROP INDEX IX_ResearchConversations_ProjectId;
            DROP INDEX IX_ResearchMessages_ConversationId_Order;
            CREATE UNIQUE INDEX IX_WorldConversations_ProjectId ON WorldConversations (ProjectId);
            CREATE INDEX IX_WorldMessages_ConversationId_Order ON WorldMessages (ConversationId, "Order");
            ALTER TABLE WebIngestCandidates RENAME COLUMN ResearchConversationId TO WorldConversationId;
            DROP INDEX IX_WebIngestCandidates_ResearchConversationId_CreatedAt;
            CREATE INDEX IX_WebIngestCandidates_WorldConversationId_CreatedAt ON WebIngestCandidates (WorldConversationId, CreatedAt);
            UPDATE ChatMessageImageAttachments SET Surface = 'World' WHERE Surface = 'Research';
            CREATE TABLE WorldBriefs (
                ProjectId TEXT NOT NULL CONSTRAINT PK_WorldBriefs PRIMARY KEY,
                Content TEXT NOT NULL,
                Revision INTEGER NOT NULL,
                CONSTRAINT FK_WorldBriefs_Projects_ProjectId FOREIGN KEY (ProjectId) REFERENCES Projects (Id) ON DELETE CASCADE
            );
            INSERT INTO WorldBriefs (ProjectId, Content, Revision) SELECT Id, '', 0 FROM Projects;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE WorldBriefs;
            ALTER TABLE WorldConversations RENAME TO ResearchConversations;
            ALTER TABLE WorldMessages RENAME TO ResearchMessages;
            DROP INDEX IX_WorldConversations_ProjectId;
            DROP INDEX IX_WorldMessages_ConversationId_Order;
            CREATE UNIQUE INDEX IX_ResearchConversations_ProjectId ON ResearchConversations (ProjectId);
            CREATE INDEX IX_ResearchMessages_ConversationId_Order ON ResearchMessages (ConversationId, "Order");
            ALTER TABLE WebIngestCandidates RENAME COLUMN WorldConversationId TO ResearchConversationId;
            DROP INDEX IX_WebIngestCandidates_WorldConversationId_CreatedAt;
            CREATE INDEX IX_WebIngestCandidates_ResearchConversationId_CreatedAt ON WebIngestCandidates (ResearchConversationId, CreatedAt);
            UPDATE ChatMessageImageAttachments SET Surface = 'Research' WHERE Surface = 'World';
            """);
    }
}
