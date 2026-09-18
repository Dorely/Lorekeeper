using System.Data;
using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lorekeeper.Composition;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Lorekeeper.Persistence;

public interface IDesignedPageMigrationService
{
    Task ApplyPendingAsync(AppDbContext db, CancellationToken cancellationToken = default);
}

public sealed class DesignedPageMigrationService : IDesignedPageMigrationService
{
    public const string AdditiveMigrationId = "20260917140000_AddDesignedPagesM2";
    public const string CleanupMigrationId = "20260917140001_FinalizeDesignedPagesM2";

    public async Task ApplyPendingAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        if (await db.DesignedPages.AsNoTracking().AnyAsync(cancellationToken))
        {
            await ValidateCompletedTransformAsync(db, cancellationToken);
            return;
        }
        if (!await HasLegacyTableAsync(db, cancellationToken))
            return;

        var legacy = await ReadLegacyCompositionsAsync(db, cancellationToken);
        var variants = await ReadLegacyVariantsAsync(db, cancellationToken);
        var active = legacy.Where(item => item.DetachedAt is null).ToList();
        var byId = active.ToDictionary(item => item.Id);
        var unambiguousClones = active
            .Where(item => item.EditionId is not null && item.SourceCompositionId is not null)
            .GroupBy(item => new { item.ProjectId, item.EditionId, item.SourceCompositionId })
            .Where(group => group.Count() == 1
                && byId.TryGetValue(group.Key.SourceCompositionId!.Value, out var source)
                && source.ProjectId == group.Key.ProjectId
                && source.EditionId is null)
            .SelectMany(group => group)
            .Select(item => item.Id)
            .ToHashSet();

        var pageIds = active.ToDictionary(
            item => item.Id,
            item => unambiguousClones.Contains(item.Id)
                ? item.SourceCompositionId!.Value
                : item.Id);
        var pages = new Dictionary<Guid, DesignedPage>();
        var activeVariantIds = new Dictionary<Guid, Guid?>();
        foreach (var item in active.OrderBy(item => item.EditionId is not null).ThenBy(item => item.CreatedAt))
        {
            var pageId = pageIds[item.Id];
            if (!pages.ContainsKey(pageId))
            {
                var page = new DesignedPage
                {
                    Id = pageId,
                    ProjectId = item.ProjectId,
                    Name = item.Name,
                    ScopeEditionId = unambiguousClones.Contains(item.Id) || item.EditionId is null
                        ? null
                        : item.EditionId,
                    CreatedAt = item.CreatedAt,
                    UpdatedAt = item.UpdatedAt,
                };
                pages.Add(page.Id, page);
                db.DesignedPages.Add(page);
            }

            var content = new DesignedPageContent
            {
                Id = item.Id,
                ProjectId = item.ProjectId,
                DesignedPageId = pageId,
                Page = pages[pageId],
                EditionId = item.EditionId,
                SemanticManuscriptJson = UpgradeManuscript(item.SemanticManuscriptJson, pageIds),
                Revision = item.Revision,
                // The active pointer and the variant's content foreign key form
                // a deliberate cycle. Insert the content and variants first,
                // then restore this pointer in a second save so SQLite's
                // immediate foreign-key checks can validate both rows.
                ActiveVariantId = null,
                CreatedAt = item.CreatedAt,
                UpdatedAt = item.UpdatedAt,
            };
            activeVariantIds.Add(content.Id, item.ActiveAuthoringVariantId);
            pages[pageId].Contents.Add(content);
            foreach (var source in variants.Where(variant => variant.CompositionId == item.Id && variant.DetachedAt is null))
            {
                content.Variants.Add(new DesignedPageVariant
                {
                    Id = source.Id,
                    ContentId = content.Id,
                    Content = content,
                    GeometryKey = source.GeometryKey,
                    SceneJson = source.SceneJson,
                    Revision = source.Revision,
                    CreatedAt = source.CreatedAt,
                    UpdatedAt = source.UpdatedAt,
                });
            }
            if (activeVariantIds[content.Id] is Guid activeVariantId
                && content.Variants.All(item => item.Id != activeVariantId))
            {
                throw new InvalidDataException($"Designed Page content {content.Id:N} references a missing active variant.");
            }
            if (content.Variants.GroupBy(item => item.GeometryKey, StringComparer.Ordinal).Any(group => group.Count() > 1))
                throw new InvalidDataException($"Designed Page content {content.Id:N} has duplicate geometry variants.");
        }

        await MigrateChapterManuscriptsAsync(db, pageIds, pages, cancellationToken);
        await MigrateEditionChapterManuscriptsAsync(db, pageIds, pages, cancellationToken);
        await MigratePublicationSectionManuscriptsAsync(db, pageIds, pages, cancellationToken);
        await UpgradePayloadColumnsAsync(db, pageIds, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        foreach (var page in pages.Values)
        {
            foreach (var content in page.Contents)
                content.ActiveVariantId = activeVariantIds[content.Id];
        }
        await db.SaveChangesAsync(cancellationToken);
        await ValidateCompletedTransformAsync(db, cancellationToken);
    }

    private static async Task<bool> HasLegacyTableAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'PageCompositions' LIMIT 1;";
            return await command.ExecuteScalarAsync(cancellationToken) is not null;
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

    internal static string UpgradeManuscript(
        string json,
        IReadOnlyDictionary<Guid, Guid> pageIds)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject
                ?? throw new InvalidDataException("The manuscript root is not an object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("A manuscript required by the Designed Page migration is malformed.", exception);
        }
        var version = root["schemaVersion"]?.GetValue<int>()
            ?? throw new InvalidDataException("A manuscript required by the Designed Page migration has no schema version.");
        if (version is < 1 or > ManuscriptDocument.CurrentSchemaVersion)
            throw new InvalidDataException($"Cannot migrate manuscript schema version {version} to v{ManuscriptDocument.CurrentSchemaVersion}.");
        if (!Guid.TryParse(root["manuscriptId"]?.GetValue<string>(), out var manuscriptId))
            throw new InvalidDataException("A manuscript required by the Designed Page migration has an invalid manuscript ID.");
        var revision = root["revision"]?.GetValue<long>()
            ?? throw new InvalidDataException("A manuscript required by the Designed Page migration has no revision.");
        var schemaUpgraded = version switch
        {
            1 => ManuscriptSchemaUpgrade.UpgradeV1DocumentJson(json, manuscriptId, revision),
            2 => ManuscriptSchemaUpgrade.UpgradeV2DocumentJson(json, manuscriptId, revision),
            3 => ManuscriptSchemaUpgrade.UpgradeV3DocumentJson(json, manuscriptId, revision),
            4 => ManuscriptSchemaUpgrade.UpgradeV4DocumentJson(json, manuscriptId, revision),
            5 => ManuscriptSchemaUpgrade.UpgradeV5DocumentJson(json, manuscriptId, revision),
            _ => json,
        };
        root = JsonNode.Parse(schemaUpgraded) as JsonObject
            ?? throw new InvalidDataException("The upgraded manuscript root is not an object.");
        UpgradeNode(root, pageIds);
        var result = root.ToJsonString(ManuscriptCodec.JsonOptions);
        _ = ManuscriptCodec.Deserialize(result);
        return result;
    }

    private static void UpgradeNode(JsonNode node, IReadOnlyDictionary<Guid, Guid> pageIds)
    {
        if (node is JsonObject value)
        {
            var propertyName = value["pageCompositionId"] is not null
                ? "pageCompositionId"
                : value["designedPageId"] is not null
                    ? "designedPageId"
                    : null;
            if (propertyName is not null && value[propertyName] is JsonNode oldNode)
            {
                var oldValue = ReadGuid(oldNode);
                if (!pageIds.TryGetValue(oldValue, out var pageId))
                    throw new InvalidDataException($"A manuscript references missing page composition {oldValue:N}.");
                value.Remove("pageCompositionId");
                value["designedPageId"] = pageId;
            }
            foreach (var child in value.ToList())
            {
                if (child.Value is not null)
                    UpgradeNode(child.Value, pageIds);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                if (child is not null)
                    UpgradeNode(child, pageIds);
            }
        }
    }

    private static Guid ReadGuid(JsonNode value)
    {
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<Guid>(out var guid))
            return guid;
        if (value is JsonValue stringValue
            && stringValue.TryGetValue<string>(out var text)
            && Guid.TryParse(text, out guid))
            return guid;
        throw new InvalidDataException("A manuscript contains a malformed Designed Page reference.");
    }

    private static async Task MigrateChapterManuscriptsAsync(
        AppDbContext db,
        IReadOnlyDictionary<Guid, Guid> pageIds,
        IReadOnlyDictionary<Guid, DesignedPage> pages,
        CancellationToken cancellationToken)
    {
        var rows = await ReadRowsAsync(db,
            "SELECT \"Id\", \"ProjectId\", NULL, \"ManuscriptRevision\", \"ManuscriptJson\" FROM \"Chapters\";",
            cancellationToken);
        foreach (var row in rows)
            await UpgradeAuthoritativeRowAsync(db, "Chapters", "ManuscriptJson", row, DesignedPageContainerKind.Chapter,
                pageIds, pages, cancellationToken);
    }

    private static async Task MigrateEditionChapterManuscriptsAsync(
        AppDbContext db,
        IReadOnlyDictionary<Guid, Guid> pageIds,
        IReadOnlyDictionary<Guid, DesignedPage> pages,
        CancellationToken cancellationToken)
    {
        var rows = await ReadRowsAsync(db,
            """
            SELECT override."Id", chapter."ProjectId", override."EditionId", override."Revision", override."ManuscriptJson", override."ChapterId"
            FROM "PublicationEditionChapterOverrides" AS override
            INNER JOIN "Chapters" AS chapter ON chapter."Id" = override."ChapterId";
            """,
            cancellationToken,
            hasContainerId: true);
        foreach (var row in rows)
            await UpgradeAuthoritativeRowAsync(db, "PublicationEditionChapterOverrides", "ManuscriptJson", row,
                DesignedPageContainerKind.Chapter, pageIds, pages, cancellationToken);
    }

    private static async Task MigratePublicationSectionManuscriptsAsync(
        AppDbContext db,
        IReadOnlyDictionary<Guid, Guid> pageIds,
        IReadOnlyDictionary<Guid, DesignedPage> pages,
        CancellationToken cancellationToken)
    {
        var rows = await ReadRowsAsync(db,
            "SELECT \"Id\", \"ProjectId\", \"EditionId\", \"Revision\", \"ManuscriptJson\" FROM \"PublicationSections\";",
            cancellationToken);
        foreach (var row in rows)
            await UpgradeAuthoritativeRowAsync(db, "PublicationSections", "ManuscriptJson", row,
                DesignedPageContainerKind.PublicationSection, pageIds, pages, cancellationToken);
    }

    private static async Task UpgradeAuthoritativeRowAsync(
        AppDbContext db,
        string table,
        string column,
        ManuscriptRow row,
        DesignedPageContainerKind containerKind,
        IReadOnlyDictionary<Guid, Guid> pageIds,
        IReadOnlyDictionary<Guid, DesignedPage> pages,
        CancellationToken cancellationToken)
    {
        var upgraded = UpgradeManuscript(row.Json, pageIds);
        var document = ManuscriptCodec.Deserialize(upgraded, row.ContainerId, row.Revision);
        var designedPageBlocks = document.Content
            .Where(item => item.Type == ManuscriptBlockType.DesignedPage)
            .ToList();
        if (designedPageBlocks.GroupBy(item => item.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new InvalidDataException($"Designed Page placement IDs are duplicated within {containerKind} {row.ContainerId:N}.");
        foreach (var block in designedPageBlocks)
        {
            var pageId = block.DesignedPageId!.Value;
            if (!pages.TryGetValue(pageId, out var page) || page.ProjectId != row.ProjectId)
                throw new InvalidDataException($"Designed Page placement {block.Id} references a page outside its project.");
            if (page.ScopeEditionId is Guid scopeId && scopeId != row.EditionId)
                throw new InvalidDataException($"Designed Page placement {block.Id} references a page outside its release scope.");
            db.DesignedPagePlacementReferences.Add(new DesignedPagePlacementReference
            {
                Id = block.Id,
                ProjectId = row.ProjectId,
                DesignedPageId = pageId,
                ContainerKind = containerKind,
                ContainerId = row.ContainerId,
                EditionId = row.EditionId,
                ManuscriptRevision = row.Revision,
            });
        }
        await UpdateJsonAsync(db, table, column, row.Id, upgraded, cancellationToken);
    }

    private static async Task UpgradePayloadColumnsAsync(
        AppDbContext db,
        IReadOnlyDictionary<Guid, Guid> pageIds,
        CancellationToken cancellationToken)
    {
        (string Table, string Column)[] columns =
        [
            ("PublicationBooks", "ManuscriptJson"),
            ("PublicationBookMatter", "ManuscriptJson"),
            ("PublicationMatter", "ManuscriptJson"),
            ("ContestBatches", "OriginalManuscriptJson"),
            ("ContestCandidates", "ProposedManuscriptJson"),
            ("ContestCandidates", "DraftManuscriptJson"),
            ("EditorRevisionSessions", "OriginalManuscriptJson"),
        ];
        foreach (var (table, column) in columns)
        {
            if (!await HasTableAndColumnAsync(db, table, column, cancellationToken))
                continue;
            var rows = await ReadPayloadRowsAsync(db, table, column, cancellationToken);
            foreach (var row in rows.Where(item => !string.IsNullOrWhiteSpace(item.Json)))
            {
                JsonNode? root;
                try { root = JsonNode.Parse(row.Json); }
                catch (JsonException exception) { throw new InvalidDataException($"{table}.{column} contains malformed JSON.", exception); }
                if (root is not JsonObject value || value["schemaVersion"] is null)
                    continue;
                await UpdateJsonAsync(db, table, column, row.Id, UpgradeManuscript(row.Json, pageIds), cancellationToken);
            }
        }
    }

    private static async Task ValidateCompletedTransformAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var duplicateContent = await db.DesignedPageContents.AsNoTracking()
            .GroupBy(item => new { item.DesignedPageId, item.EditionId })
            .Where(group => group.Count() > 1)
            .AnyAsync(cancellationToken);
        if (duplicateContent)
            throw new InvalidDataException("The Designed Page migration produced duplicate target content.");
        var missingPages = await db.DesignedPageContents.AsNoTracking()
            .AnyAsync(item => !db.DesignedPages.Any(page => page.Id == item.DesignedPageId && page.ProjectId == item.ProjectId), cancellationToken);
        if (missingPages)
            throw new InvalidDataException("The Designed Page migration produced orphan content.");
        var missingPlacements = await db.DesignedPagePlacementReferences.AsNoTracking()
            .AnyAsync(item => !db.DesignedPages.Any(page => page.Id == item.DesignedPageId && page.ProjectId == item.ProjectId), cancellationToken);
        if (missingPlacements)
            throw new InvalidDataException("The Designed Page migration produced an orphan placement reference.");
    }

    private static async Task<List<LegacyComposition>> ReadLegacyCompositionsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT "Id", "ProjectId", "ChapterId", "PublicationSectionId", "EditionId", "SourceCompositionId",
                "Name", "SemanticManuscriptJson", "Revision", "ActiveAuthoringVariantId", "CreatedAt", "UpdatedAt", "DetachedAt"
            FROM "PageCompositions";
            """;
        return await ReadAsync(db, sql, reader => new LegacyComposition(
            reader.GetGuid(0), reader.GetGuid(1), GetNullableGuid(reader, 2), GetNullableGuid(reader, 3),
            GetNullableGuid(reader, 4), GetNullableGuid(reader, 5), reader.GetString(6), reader.GetString(7),
            reader.GetInt64(8), GetNullableGuid(reader, 9), reader.GetDateTime(10), reader.GetDateTime(11),
            reader.IsDBNull(12) ? null : reader.GetDateTime(12)), cancellationToken);
    }

    private static async Task<List<LegacyVariant>> ReadLegacyVariantsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT "Id", "CompositionId", "GeometryKey", "SceneJson", "Revision", "CreatedAt", "UpdatedAt", "DetachedAt"
            FROM "PageCompositionVariants";
            """;
        return await ReadAsync(db, sql, reader => new LegacyVariant(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4),
            reader.GetDateTime(5), reader.GetDateTime(6), reader.IsDBNull(7) ? null : reader.GetDateTime(7)), cancellationToken);
    }

    private static async Task<List<ManuscriptRow>> ReadRowsAsync(
        AppDbContext db,
        string sql,
        CancellationToken cancellationToken,
        bool hasContainerId = false) =>
        await ReadAsync(db, sql, reader => new ManuscriptRow(
            reader.GetGuid(0), reader.GetGuid(1), GetNullableGuid(reader, 2), reader.GetInt64(3), reader.GetString(4),
            hasContainerId ? reader.GetGuid(5) : reader.GetGuid(0)), cancellationToken);

    private static async Task<List<PayloadRow>> ReadPayloadRowsAsync(
        AppDbContext db,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
#pragma warning disable EF1002
        return await ReadAsync(db, $"SELECT \"Id\", \"{column}\" FROM \"{table}\";",
            reader => new PayloadRow(reader.GetGuid(0), reader.GetString(1)), cancellationToken);
#pragma warning restore EF1002
    }

    private static async Task<List<T>> ReadAsync<T>(
        AppDbContext db,
        string sql,
        Func<DbDataReader, T> map,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        var result = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(map(reader));
        return result;
    }

    private static async Task UpdateJsonAsync(
        AppDbContext db,
        string table,
        string column,
        Guid id,
        string json,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
#pragma warning disable EF1002
        command.CommandText = $"UPDATE \"{table}\" SET \"{column}\" = $json WHERE \"Id\" = $id;";
#pragma warning restore EF1002
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        var idParameter = command.CreateParameter();
        idParameter.ParameterName = "$id";
        idParameter.Value = id;
        command.Parameters.Add(idParameter);
        var jsonParameter = command.CreateParameter();
        jsonParameter.ParameterName = "$json";
        jsonParameter.Value = json;
        command.Parameters.Add(jsonParameter);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidDataException($"Could not update {table}.{column} for {id:N}.");
    }

    private static async Task<bool> HasTableAndColumnAsync(
        AppDbContext db,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $column;";
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        var tableParameter = command.CreateParameter();
        tableParameter.ParameterName = "$table";
        tableParameter.Value = table;
        command.Parameters.Add(tableParameter);
        var columnParameter = command.CreateParameter();
        columnParameter.ParameterName = "$column";
        columnParameter.Value = column;
        command.Parameters.Add(columnParameter);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static Guid? GetNullableGuid(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);

    private sealed record LegacyComposition(
        Guid Id, Guid ProjectId, Guid? ChapterId, Guid? PublicationSectionId, Guid? EditionId,
        Guid? SourceCompositionId, string Name, string SemanticManuscriptJson, long Revision,
        Guid? ActiveAuthoringVariantId, DateTime CreatedAt, DateTime UpdatedAt, DateTime? DetachedAt);

    private sealed record LegacyVariant(
        Guid Id, Guid CompositionId, string GeometryKey, string SceneJson, long Revision,
        DateTime CreatedAt, DateTime UpdatedAt, DateTime? DetachedAt);

    private sealed record ManuscriptRow(
        Guid Id, Guid ProjectId, Guid? EditionId, long Revision, string Json, Guid ContainerId);

    private sealed record PayloadRow(Guid Id, string Json);
}
