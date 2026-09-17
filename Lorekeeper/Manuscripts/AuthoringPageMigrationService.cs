using System.Security.Cryptography;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lorekeeper.Composition;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Legacy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

namespace Lorekeeper.Manuscripts;

public interface IAuthoringPageMigrationService
{
    Task ApplyPendingAsync(AppDbContext db, CancellationToken cancellationToken = default);
}

public sealed class AuthoringPageMigrationService(
    IDatabaseMigrationRecoveryService recovery,
    ILogger<AuthoringPageMigrationService> logger) : IAuthoringPageMigrationService
{
    public const string MigrationName = "authoring-page-setup-v1";
    public const string SeedRecoveryMigrationName = "authoring-page-seed-recovery-v1";
    public const string SchemaMigrationId = "20260802180000_AddAuthoringPageSetupV19";
    private const string LegacyPicturePageSeedTargetKind = "page-composition-seed";
    private const string DesignedPageSeedTargetKind = "designed-page-seed";

    public async Task ApplyPendingAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        var authoringMigrationCompleted = await db.ManuscriptMigrationJournals.AsNoTracking().AnyAsync(
            item => item.MigrationName == MigrationName && item.Status == ManuscriptMigrationStatus.Completed,
            cancellationToken);
        var hasPicturePageSeeds = await db.CompositionMutationStages.AsNoTracking().AnyAsync(
            item => (item.TargetKind == LegacyPicturePageSeedTargetKind
                    || item.TargetKind == DesignedPageSeedTargetKind)
                && item.ConversationId == Guid.Empty,
            cancellationToken);
        if (authoringMigrationCompleted && !hasPicturePageSeeds)
            return;

        var migrationName = authoringMigrationCompleted ? SeedRecoveryMigrationName : MigrationName;
        var backupName = authoringMigrationCompleted ? "pre-authoring-seed-recovery" : "pre-authoring-pages-v4";
        var sourceSchemaVersion = authoringMigrationCompleted ? ManuscriptDocument.CurrentSchemaVersion : 3;
        var backupPath = await recovery.CreateBackupAsync("manuscripts", backupName, cancellationToken);
        try
        {
            var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
            if (!applied.Contains(SchemaMigrationId))
                await db.GetService<IMigrator>().MigrateAsync(SchemaMigrationId, cancellationToken);

            var journal = new ManuscriptMigrationJournal
            {
                MigrationName = migrationName,
                SourceSchemaVersion = sourceSchemaVersion,
                TargetSchemaVersion = ManuscriptDocument.CurrentSchemaVersion,
                Phase = ManuscriptMigrationPhase.Transform,
                Status = ManuscriptMigrationStatus.Running,
                BackupPath = backupPath,
            };
            db.ManuscriptMigrationJournals.Add(journal);
            await db.SaveChangesAsync(cancellationToken);

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var chapters = await db.Chapters.OrderBy(item => item.Id).ToListAsync(cancellationToken);
            var compositions = await db.LegacyPageCompositions.OrderBy(item => item.Id).ToListAsync(cancellationToken);
            var variants = await db.LegacyPageCompositionVariants.OrderBy(item => item.Id).ToListAsync(cancellationToken);
            var picturePageSeeds = await db.CompositionMutationStages
                .Where(item => (item.TargetKind == LegacyPicturePageSeedTargetKind
                        || item.TargetKind == DesignedPageSeedTargetKind)
                    && item.ConversationId == Guid.Empty)
                .OrderBy(item => item.Id)
                .ToListAsync(cancellationToken);
            var covers = await db.PublicationCoverDesigns.OrderBy(item => item.Id).ToListAsync(cancellationToken);
            var placements = await db.PublicationImagePlacements.OrderBy(item => item.Id).ToListAsync(cancellationToken);
            var sourceText = chapters.Select(item => SemanticText(item.ManuscriptJson)).ToArray();
            var sourceCompositionText = compositions.Select(item => SemanticText(item.SemanticManuscriptJson)).ToArray();
            var artifactState = await ArtifactStateAsync(db, cancellationToken);
            var sourceCounts = await CountsAsync(db, cancellationToken);

            foreach (var chapter in chapters)
                chapter.ManuscriptJson = UpgradeJson(
                    UpgradeManuscript(chapter.ManuscriptJson, chapter.Id, chapter.ManuscriptRevision),
                    removeGuides: false);
            foreach (var composition in compositions)
            {
                composition.SemanticManuscriptJson = UpgradeJson(
                    UpgradeManuscript(composition.SemanticManuscriptJson, composition.Id, composition.Revision),
                    removeGuides: false);
            }
            foreach (var variant in variants)
                variant.SceneJson = UpgradeScene(variant.SceneJson, removeGuides: true);
            var seedResult = MaterializePicturePageSeeds(db, compositions, variants, picturePageSeeds);
            var authoringSelections = compositions.ToDictionary(
                item => item.Id,
                item => seedResult.ActiveVariantIds.TryGetValue(item.Id, out var restoredVariantId)
                    ? (Guid?)restoredVariantId
                    : item.ActiveAuthoringVariantId
                        ?? variants.Where(variant => variant.CompositionId == item.Id)
                            .OrderByDescending(variant => variant.UpdatedAt).ThenByDescending(variant => variant.Id)
                            .Select(variant => (Guid?)variant.Id).FirstOrDefault());
            foreach (var cover in covers)
                cover.CompositionSceneJson = UpgradeScene(cover.CompositionSceneJson, removeGuides: true);
            foreach (var placement in placements)
                placement.PresentationJson = UpgradeJson(placement.PresentationJson, removeGuides: false);
            await UpgradeLegacyPendingDesignedPageArgumentsAsync(db, cancellationToken);

            await db.PublicationArtifacts.ExecuteUpdateAsync(
                setters => setters.SetProperty(item => item.IsLegacy, true), cancellationToken);
            await db.PublicationRenderJobs.ExecuteUpdateAsync(
                setters => setters.SetProperty(item => item.IsLegacy, true), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            foreach (var composition in compositions)
                composition.ActiveAuthoringVariantId = authoringSelections[composition.Id];
            await db.SaveChangesAsync(cancellationToken);

            var targetText = chapters.Select(item =>
            {
                _ = ManuscriptCodec.Deserialize(item.ManuscriptJson, item.Id, item.ManuscriptRevision);
                return SemanticText(item.ManuscriptJson);
            }).ToArray();
            if (!sourceText.SequenceEqual(targetText, StringComparer.Ordinal))
                throw new InvalidDataException("Authoring-page migration changed manuscript text.");
            var targetCompositionText = compositions.Select(item =>
            {
                _ = ManuscriptCodec.Deserialize(item.SemanticManuscriptJson, item.Id, item.Revision);
                return SemanticText(item.SemanticManuscriptJson);
            }).ToArray();
            if (!sourceCompositionText.SequenceEqual(targetCompositionText, StringComparer.Ordinal))
                throw new InvalidDataException("Authoring-page migration changed Designed Page semantic text.");
            if (!string.Equals(artifactState, await ArtifactStateAsync(db, cancellationToken), StringComparison.Ordinal))
                throw new InvalidDataException("Authoring-page migration changed artifact bytes or hashes.");
            var expectedCounts = sourceCounts with
            {
                Variants = sourceCounts.Variants + seedResult.InsertedVariantCount,
                MutationStages = sourceCounts.MutationStages - picturePageSeeds.Count,
            };
            if (expectedCounts != await CountsAsync(db, cancellationToken))
                throw new InvalidDataException("Authoring-page migration changed protected publication or authoring row counts.");
            if (await db.ProjectPageSetups.CountAsync(cancellationToken) != await db.Projects.CountAsync(cancellationToken))
                throw new InvalidDataException("Authoring-page migration did not create exactly one page setup per project.");
            await ValidateCompositionReferencesAsync(db, chapters, compositions, cancellationToken);
            await ValidateRecoveredPicturePageScenesAsync(db, seedResult.ExpectedSceneHashes, cancellationToken);
            await ValidateImageOwnershipAsync(db, chapters, compositions, variants, covers, placements, cancellationToken);
            if (await HasForeignKeyViolationsAsync(db, cancellationToken))
                throw new InvalidDataException("Authoring-page migration left invalid foreign keys.");

            journal.ChapterCount = chapters.Count;
            journal.SourceHash = Hash(string.Join('\n', sourceText));
            journal.TargetHash = Hash(string.Join('\n', targetText));
            journal.ValidationReportJson = JsonSerializer.Serialize(new
            {
                chapters = chapters.Count,
                compositions = compositions.Count,
                variants = variants.Count,
                restoredPicturePages = seedResult.ExpectedSceneHashes.Count,
                covers = covers.Count,
                pageSetups = await db.ProjectPageSetups.CountAsync(cancellationToken),
                protectedRows = sourceCounts,
            });
            journal.Phase = ManuscriptMigrationPhase.Complete;
            journal.Status = ManuscriptMigrationStatus.Completed;
            journal.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Authoring page migration failed.");
            await recovery.EnterRecoveryModeAsync(
                db,
                backupPath,
                migrationName,
                sourceSchemaVersion,
                ManuscriptDocument.CurrentSchemaVersion,
                exception,
                cancellationToken);
        }
    }

    private static PicturePageSeedResult MaterializePicturePageSeeds(
        AppDbContext db,
        IReadOnlyList<LegacyPageComposition> compositions,
        List<LegacyPageCompositionVariant> variants,
        IReadOnlyList<CompositionMutationStage> seeds)
    {
        var compositionsById = compositions.ToDictionary(item => item.Id);
        var activeVariantIds = new Dictionary<Guid, Guid>();
        var expectedSceneHashes = new Dictionary<Guid, string>();
        var insertedVariantCount = 0;
        foreach (var seed in seeds)
        {
            if (!compositionsById.TryGetValue(seed.TargetId, out var composition)
                || composition.ProjectId != seed.ProjectId)
                throw new InvalidDataException($"Picture Page seed {seed.Id:N} does not belong to its Designed Page.");
            if (seed.AppliedAt is not null || seed.ExpectedRevision != composition.Revision)
                throw new InvalidDataException($"Picture Page seed {seed.Id:N} has an invalid migration revision.");
            if (!string.Equals(Hash(seed.OperationsJson), seed.PayloadSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Picture Page seed {seed.Id:N} failed its SHA-256 integrity check.");
            if (activeVariantIds.ContainsKey(composition.Id))
                throw new InvalidDataException($"Designed Page {composition.Id:N} has more than one Picture Page seed.");

            var upgradedSceneJson = UpgradeScene(seed.OperationsJson, removeGuides: true);
            var scene = JsonSerializer.Deserialize<CompositionScene>(upgradedSceneJson, ManuscriptCodec.JsonOptions)
                ?? throw new InvalidDataException($"Picture Page seed {seed.Id:N} contains no composition scene.");
            var semantic = ManuscriptCodec.Deserialize(
                composition.SemanticManuscriptJson,
                composition.Id,
                composition.Revision);
            DesignedPageService.Validate(scene, semantic);
            var canonicalSceneJson = JsonSerializer.Serialize(scene, ManuscriptCodec.JsonOptions);
            var geometryKey = DesignedPageService.SceneGeometryKey(scene);
            var variant = variants.SingleOrDefault(item =>
                item.CompositionId == composition.Id
                && string.Equals(item.GeometryKey, geometryKey, StringComparison.Ordinal));
            if (variant is null)
            {
                variant = new LegacyPageCompositionVariant
                {
                    CompositionId = composition.Id,
                    GeometryKey = geometryKey,
                    SceneJson = canonicalSceneJson,
                };
                db.LegacyPageCompositionVariants.Add(variant);
                variants.Add(variant);
                insertedVariantCount++;
            }
            else
            {
                var existingScene = JsonSerializer.Deserialize<CompositionScene>(variant.SceneJson, ManuscriptCodec.JsonOptions)
                    ?? throw new InvalidDataException($"Designed Page variant {variant.Id:N} contains no composition scene.");
                var existingSceneJson = JsonSerializer.Serialize(existingScene, ManuscriptCodec.JsonOptions);
                if (variant.Revision == 0 && existingScene.Objects.Count == 0)
                {
                    variant.SceneJson = canonicalSceneJson;
                    variant.UpdatedAt = DateTime.UtcNow;
                }
                else if (!string.Equals(Hash(existingSceneJson), Hash(canonicalSceneJson), StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Designed Page {composition.Id:N} has both a migrated Picture Page and a different layout for the same geometry.");
                }
            }

            activeVariantIds.Add(composition.Id, variant.Id);
            expectedSceneHashes.Add(composition.Id, Hash(canonicalSceneJson));
            db.CompositionMutationStages.Remove(seed);
        }
        return new PicturePageSeedResult(insertedVariantCount, activeVariantIds, expectedSceneHashes);
    }

    private static async Task ValidateRecoveredPicturePageScenesAsync(
        AppDbContext db,
        IReadOnlyDictionary<Guid, string> expectedSceneHashes,
        CancellationToken cancellationToken)
    {
        foreach (var (compositionId, expectedHash) in expectedSceneHashes)
        {
            var restored = await db.LegacyPageCompositions.AsNoTracking()
                .Where(item => item.Id == compositionId && item.ActiveAuthoringVariantId != null)
                .Join(
                    db.LegacyPageCompositionVariants.AsNoTracking(),
                    composition => composition.ActiveAuthoringVariantId,
                    variant => variant.Id,
                    (_, variant) => variant.SceneJson)
                .SingleOrDefaultAsync(cancellationToken);
            if (restored is null)
                throw new InvalidDataException($"Designed Page {compositionId:N} has no restored active Picture Page layout.");
            var scene = JsonSerializer.Deserialize<CompositionScene>(restored, ManuscriptCodec.JsonOptions)
                ?? throw new InvalidDataException($"Designed Page {compositionId:N} has an empty restored Picture Page layout.");
            var actualHash = Hash(JsonSerializer.Serialize(scene, ManuscriptCodec.JsonOptions));
            if (!string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
                throw new InvalidDataException($"Designed Page {compositionId:N} did not preserve its Picture Page scene.");
        }
    }

    private static string UpgradeManuscript(string json, Guid id, long revision)
    {
        using var document = JsonDocument.Parse(json);
        var version = document.RootElement.GetProperty("schemaVersion").GetInt32();
        return version switch
        {
            ManuscriptDocument.CurrentSchemaVersion => json,
            4 => ManuscriptSchemaUpgrade.UpgradeV4DocumentJson(json, id, revision),
            3 => ManuscriptSchemaUpgrade.UpgradeV3DocumentJson(json, id, revision),
            2 => ManuscriptSchemaUpgrade.UpgradeV2DocumentJson(json, id, revision),
            1 => ManuscriptSchemaUpgrade.UpgradeV1DocumentJson(json, id, revision),
            _ => throw new InvalidDataException($"Manuscript {id:N} uses unsupported schema version {version}."),
        };
    }

    private static string UpgradeScene(string json, bool removeGuides) => UpgradeJson(json, removeGuides);

    private static string UpgradeDesignedPageArguments(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidDataException("A pending Designed Page change contains invalid arguments.");
        node.Remove("editionId");
        node.Remove("EditionId");
        Rewrite(node, removeGuides: false);
        return node.ToJsonString(ManuscriptCodec.JsonOptions);
    }

    private static async Task UpgradeLegacyPendingDesignedPageArgumentsAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        await using (var exists = connection.CreateCommand())
        {
            exists.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'AiChanges' LIMIT 1;";
            if (await exists.ExecuteScalarAsync(cancellationToken) is null)
                return;
        }

        var rows = new List<(string Id, string Arguments)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            select.CommandText = """
                SELECT Id, ArgumentsJson
                FROM AiChanges
                WHERE lower(Status) = 'pending'
                  AND ToolName = 'insert_outline_designed_page';
                """;
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                rows.Add((reader.GetValue(0).ToString()!, reader.IsDBNull(1) ? string.Empty : reader.GetString(1)));
        }

        foreach (var row in rows)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            update.CommandText = "UPDATE AiChanges SET ArgumentsJson = $arguments, UpdatedAt = $updatedAt WHERE Id = $id;";
            AddParameter(update, "$arguments", UpgradeDesignedPageArguments(row.Arguments));
            AddParameter(update, "$updatedAt", DateTime.UtcNow);
            AddParameter(update, "$id", row.Id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    internal static string UpgradeJson(string json, bool removeGuides)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;
        var node = JsonNode.Parse(json) ?? throw new InvalidDataException("A persisted layout contains empty JSON.");
        Rewrite(node, removeGuides);
        return node.ToJsonString(ManuscriptCodec.JsonOptions);
    }

    private static void Rewrite(JsonNode? node, bool removeGuides)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array) Rewrite(item, removeGuides);
            return;
        }
        if (node is not JsonObject obj) return;
        Rename(obj, "focalXPercent", "cropXPercent");
        Rename(obj, "focalYPercent", "cropYPercent");
        Rename(obj, "imageFocalXPercent", "imageCropXPercent");
        Rename(obj, "imageFocalYPercent", "imageCropYPercent");
        obj.Remove("layoutTargetEditionId");
        if (removeGuides && obj.ContainsKey("guides")) obj["guides"] = new JsonArray();
        NormalizeFit(obj, "fit");
        NormalizeFit(obj, "imageFit");
        if (string.Equals(obj["kind"]?.GetValue<string>(), "image", StringComparison.OrdinalIgnoreCase)
            && (!obj.TryGetPropertyValue("imageFit", out var fit) || fit is null))
            obj["imageFit"] = "contain";
        foreach (var property in obj.ToArray()) Rewrite(property.Value, removeGuides);
    }

    private static void NormalizeFit(JsonObject obj, string propertyName)
    {
        if (!obj.TryGetPropertyValue(propertyName, out var fit)
            || fit is null
            || !string.Equals(fit.GetValue<string>(), "fill", StringComparison.OrdinalIgnoreCase))
            return;
        obj[propertyName] = "contain";
    }

    private static void Rename(JsonObject obj, string oldName, string newName)
    {
        if (!obj.TryGetPropertyValue(oldName, out var value)) return;
        if (!obj.ContainsKey(newName)) obj[newName] = value?.DeepClone();
        obj.Remove(oldName);
    }

    private static string SemanticText(string json)
    {
        using var document = JsonDocument.Parse(json);
        return string.Join('\n', document.RootElement.GetProperty("content").EnumerateArray()
            .Select(block => block.TryGetProperty("content", out var content)
                ? string.Concat(content.EnumerateArray().Select(inline => inline.TryGetProperty("text", out var text) ? text.GetString() : string.Empty))
                : string.Empty));
    }

    private static async Task<string> ArtifactStateAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.PublicationArtifacts.AsNoTracking().OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.Sha256, item.ByteLength, item.Data }).ToListAsync(cancellationToken);
        return Hash(string.Join('|', rows.Select(item => $"{item.Id:N}:{item.Sha256}:{item.ByteLength}:{Convert.ToHexStringLower(SHA256.HashData(item.Data))}")));
    }

    private static async Task<bool> HasForeignKeyViolationsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check";
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken);
    }

    private static async Task ValidateCompositionReferencesAsync(
        AppDbContext db,
        IReadOnlyList<Chapter> chapters,
        IReadOnlyList<LegacyPageComposition> compositions,
        CancellationToken cancellationToken)
    {
        var byId = compositions.ToDictionary(item => item.Id);
        var referencedCompositionIds = new HashSet<Guid>();
        foreach (var chapter in chapters)
        {
            var document = ManuscriptCodec.Deserialize(chapter.ManuscriptJson, chapter.Id, chapter.ManuscriptRevision);
            foreach (var compositionId in document.Content
                .Where(item => item.Type == ManuscriptBlockType.DesignedPage)
                .Select(item => item.DesignedPageId))
            {
                if (compositionId is not Guid id || !byId.TryGetValue(id, out var composition) || composition.ChapterId != chapter.Id)
                    throw new InvalidDataException($"Chapter {chapter.Id:N} contains an invalid Designed Page reference.");
                referencedCompositionIds.Add(id);
            }
        }
        foreach (var compositionId in referencedCompositionIds)
        {
            var composition = byId[compositionId];
            if (composition.ActiveAuthoringVariantId is not Guid variantId)
                throw new InvalidDataException($"Designed Page {composition.Id:N} has no active authoring layout.");
            if (!await db.LegacyPageCompositionVariants.AsNoTracking().AnyAsync(
                    item => item.Id == variantId && item.CompositionId == composition.Id,
                    cancellationToken))
                throw new InvalidDataException($"Designed Page {composition.Id:N} has an invalid active authoring layout.");
        }
    }

    private static async Task ValidateImageOwnershipAsync(
        AppDbContext db,
        IReadOnlyList<Chapter> chapters,
        IReadOnlyList<LegacyPageComposition> compositions,
        IReadOnlyList<LegacyPageCompositionVariant> variants,
        IReadOnlyList<PublicationCoverDesign> covers,
        IReadOnlyList<PublicationImagePlacement> placements,
        CancellationToken cancellationToken)
    {
        var owners = await db.PublishAssets.AsNoTracking()
            .Select(item => new { item.Id, item.ProjectId })
            .ToDictionaryAsync(item => item.Id, item => item.ProjectId, cancellationToken);
        var compositionProjects = compositions.ToDictionary(item => item.Id, item => item.ProjectId);
        foreach (var chapter in chapters)
            RequireOwners(ReferencedImageIds(chapter.ManuscriptJson), chapter.ProjectId, owners, $"chapter {chapter.Id:N}");
        foreach (var composition in compositions)
            RequireOwners(ReferencedImageIds(composition.SemanticManuscriptJson), composition.ProjectId, owners, $"Designed Page {composition.Id:N}");
        foreach (var variant in variants)
            RequireOwners(ReferencedImageIds(variant.SceneJson), compositionProjects[variant.CompositionId], owners, $"Designed Page layout {variant.Id:N}");

        var editionProjects = await db.PublicationEditions.AsNoTracking()
            .Select(item => new { item.Id, item.ProjectId })
            .ToDictionaryAsync(item => item.Id, item => item.ProjectId, cancellationToken);
        foreach (var cover in covers)
            RequireOwners(ReferencedImageIds(cover.CompositionSceneJson), editionProjects[cover.EditionId], owners, $"cover {cover.Id:N}");
        foreach (var placement in placements)
        {
            var projectId = editionProjects[placement.EditionId];
            if (!owners.TryGetValue(placement.AssetId, out var owner) || owner != projectId)
                throw new InvalidDataException($"Publication placement {placement.Id:N} references an image outside its project.");
        }
    }

    private static IEnumerable<Guid> ReferencedImageIds(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) yield break;
        using var document = JsonDocument.Parse(json);
        foreach (var id in ReferencedImageIds(document.RootElement)) yield return id;
    }

    private static IEnumerable<Guid> ReferencedImageIds(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("imageId") && property.Value.ValueKind == JsonValueKind.String
                    && Guid.TryParse(property.Value.GetString(), out var id))
                    yield return id;
                foreach (var nested in ReferencedImageIds(property.Value)) yield return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                foreach (var nested in ReferencedImageIds(item)) yield return nested;
        }
    }

    private static void RequireOwners(
        IEnumerable<Guid> imageIds,
        Guid projectId,
        IReadOnlyDictionary<Guid, Guid> owners,
        string source)
    {
        foreach (var imageId in imageIds.Distinct())
            if (!owners.TryGetValue(imageId, out var owner) || owner != projectId)
                throw new InvalidDataException($"The {source} references a project image that is missing or owned by another project.");
    }

    private static async Task<MigrationCounts> CountsAsync(AppDbContext db, CancellationToken cancellationToken) => new(
        await db.Projects.CountAsync(cancellationToken),
        await db.Chapters.CountAsync(cancellationToken),
        await db.LegacyPageCompositions.CountAsync(cancellationToken),
        await db.LegacyPageCompositionVariants.CountAsync(cancellationToken),
        await db.CompositionMutationStages.CountAsync(cancellationToken),
        await db.PublicationEditions.CountAsync(cancellationToken),
        await db.PublicationCoverDesigns.CountAsync(cancellationToken),
        await db.PublicationImagePlacements.CountAsync(cancellationToken),
        await db.PublicationRenderJobs.CountAsync(cancellationToken),
        await db.PublicationArtifacts.CountAsync(cancellationToken),
        await db.PublicationPageMapEntries.CountAsync(cancellationToken),
        await db.PublicationEditionAuditEntries.CountAsync(cancellationToken),
        await db.PublishAssets.CountAsync(cancellationToken),
        await db.ProjectFontFamilies.CountAsync(cancellationToken),
        await db.ProjectFontFaces.CountAsync(cancellationToken));

    private sealed record MigrationCounts(
        int Projects,
        int Chapters,
        int Compositions,
        int Variants,
        int MutationStages,
        int Editions,
        int Covers,
        int Placements,
        int RenderJobs,
        int Artifacts,
        int PageMapEntries,
        int Audits,
        int Images,
        int FontFamilies,
        int FontFaces);

    private sealed record PicturePageSeedResult(
        int InsertedVariantCount,
        IReadOnlyDictionary<Guid, Guid> ActiveVariantIds,
        IReadOnlyDictionary<Guid, string> ExpectedSceneHashes);

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
