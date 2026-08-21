using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations;

/// <summary>
/// Removes the obsolete SQLite Undo/Redo streams after startup has copied any
/// durable Review baseline that can be recovered from them.
/// </summary>
public partial class RemovePersistentAuthoringHistory : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Keep this migration tolerant of databases created before all history
        // tables existed. The startup backfill runs before this migration.
        migrationBuilder.Sql("DROP TABLE IF EXISTS \"AuthoringHistoryDependencies\";");
        migrationBuilder.Sql("DROP TABLE IF EXISTS \"AuthoringTurnHistoryBatches\";");
        migrationBuilder.Sql("DROP TABLE IF EXISTS \"AuthoringHistoryEntries\";");
        migrationBuilder.Sql("DROP TABLE IF EXISTS \"AuthoringHistoryStreams\";");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Persistent authoring history is intentionally not restored by a
        // rollback. Review baselines remain the supported durable contract.
    }
}
