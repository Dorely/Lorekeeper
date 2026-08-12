using System.Data.Common;
using System.Text.Json;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Lorekeeper.Publish;

public interface IEditionContentMigrationService
{
    Task ApplyPendingAsync(AppDbContext db, CancellationToken cancellationToken = default);
}

public sealed class EditionContentMigrationService(
    IDatabaseMigrationRecoveryService recovery,
    IManuscriptService manuscripts,
    ILogger<EditionContentMigrationService> logger) : IEditionContentMigrationService
{
    public const string MigrationName = "edition-specific-content-v1";
    public const string AdditiveMigrationId = "20260811040955_AddEditionSpecificContentV23";
    public const string CleanupMigrationId = "20260811043616_RemoveProofStyleMappingsAndReleaseTypographyV24";

    public async Task ApplyPendingAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        if (applied.Contains(CleanupMigrationId))
            return;
        if (!applied.Contains(AdditiveMigrationId))
            await db.GetService<IMigrator>().MigrateAsync(AdditiveMigrationId, cancellationToken);
        await DatabaseStartupMigrationService.EnsurePublicationSectionCompatibilityColumnsAsync(db, cancellationToken);
        if (await db.ManuscriptMigrationJournals.AsNoTracking().AnyAsync(
            item => item.MigrationName == MigrationName && item.Status == ManuscriptMigrationStatus.Completed,
            cancellationToken))
            return;

        var backupPath = await recovery.CreateBackupAsync("publishing", "pre-edition-content-v1", cancellationToken);
        try
        {
            var journal = new ManuscriptMigrationJournal
            {
                MigrationName = MigrationName,
                SourceSchemaVersion = ManuscriptDocument.CurrentSchemaVersion,
                TargetSchemaVersion = ManuscriptDocument.CurrentSchemaVersion,
                Phase = ManuscriptMigrationPhase.Transform,
                Status = ManuscriptMigrationStatus.Running,
                BackupPath = backupPath,
            };
            db.ManuscriptMigrationJournals.Add(journal);
            await db.SaveChangesAsync(cancellationToken);

            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM PublicationArtifacts WHERE Kind = 'ProofRecord';",
                cancellationToken);
            var artifactState = await db.PublicationArtifacts.AsNoTracking()
                .OrderBy(item => item.Id)
                .Select(item => new { item.Id, item.Sha256, item.ByteLength })
                .ToListAsync(cancellationToken);
            var mappings = await ReadMappingsAsync(db, cancellationToken);
            var typography = await ReadTypographyAsync(db, cancellationToken);
            var touchedEditions = new HashSet<Guid>();
            var editionsToMaterialize = mappings.Select(item => item.EditionId)
                .Concat(typography.Where(item => item.RequiresMaterialization).Select(item => item.EditionId))
                .Distinct()
                .ToList();
            foreach (var editionId in editionsToMaterialize)
            {
                var edition = await db.PublicationEditions.SingleAsync(item => item.Id == editionId, cancellationToken);
                var editionTypography = typography.Single(item => item.EditionId == edition.Id);
                var projectStyles = await db.ManuscriptStyleDefinitions
                    .Where(item => item.ProjectId == edition.ProjectId)
                    .ToListAsync(cancellationToken);
                var roleMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var mapping in mappings.Where(item => item.EditionId == edition.Id))
                {
                    var source = projectStyles.Single(item => item.Id == mapping.StyleId);
                    var inherited = JsonSerializer.Deserialize<ManuscriptStyleProperties>(
                        source.DefinitionJson, ManuscriptCodec.JsonOptions) ?? new ManuscriptStyleProperties();
                    var editionOverride = JsonSerializer.Deserialize<ManuscriptStyleProperties>(
                        mapping.OverrideJson, ManuscriptCodec.JsonOptions) ?? new ManuscriptStyleProperties();
                    var merged = Merge(inherited, editionOverride);
                    if (string.Equals(source.SemanticRole, ManuscriptStyleRoles.Body, StringComparison.OrdinalIgnoreCase)
                        && editionTypography.RequiresMaterialization)
                    {
                        merged = merged with
                        {
                            FontSizePoints = editionTypography.BodyFontSizePoints,
                            LineHeight = editionTypography.BodyLineHeight,
                        };
                    }
                    var role = UniqueRole(source.SemanticRole, edition.Id, projectStyles);
                    var name = UniqueName($"{source.Name} — {edition.Name}", source.Kind, projectStyles);
                    var reusable = new ManuscriptStyleDefinition
                    {
                        ProjectId = edition.ProjectId,
                        Name = name,
                        NameKey = name.Trim().ToUpperInvariant(),
                        Kind = source.Kind,
                        SemanticRole = role,
                        SemanticRoleKey = role.Trim().ToUpperInvariant(),
                        DefinitionJson = JsonSerializer.Serialize(merged, ManuscriptCodec.JsonOptions),
                        Revision = 1,
                    };
                    db.ManuscriptStyleDefinitions.Add(reusable);
                    projectStyles.Add(reusable);
                    roleMap[source.SemanticRole] = role;
                }
                if (editionTypography.RequiresMaterialization
                    && !roleMap.ContainsKey(ManuscriptStyleRoles.Body))
                {
                    var role = UniqueRole(ManuscriptStyleRoles.Body, edition.Id, projectStyles);
                    var name = UniqueName($"Body text — {edition.Name}", ManuscriptStyleKind.Paragraph, projectStyles);
                    var reusable = new ManuscriptStyleDefinition
                    {
                        ProjectId = edition.ProjectId,
                        Name = name,
                        NameKey = name.Trim().ToUpperInvariant(),
                        Kind = ManuscriptStyleKind.Paragraph,
                        SemanticRole = role,
                        SemanticRoleKey = role.Trim().ToUpperInvariant(),
                        DefinitionJson = JsonSerializer.Serialize(new ManuscriptStyleProperties(
                            FontSizePoints: editionTypography.BodyFontSizePoints,
                            LineHeight: editionTypography.BodyLineHeight), ManuscriptCodec.JsonOptions),
                        Revision = 1,
                    };
                    db.ManuscriptStyleDefinitions.Add(reusable);
                    projectStyles.Add(reusable);
                    roleMap[ManuscriptStyleRoles.Body] = role;
                }
                await db.SaveChangesAsync(cancellationToken);

                edition.EditionSpecificContentEnabled = true;
                edition.Revision = checked(edition.Revision + 1);
                edition.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                var chapters = await db.Chapters.AsNoTracking()
                    .Where(item => item.ProjectId == edition.ProjectId)
                    .OrderBy(item => item.Order)
                    .ToListAsync(cancellationToken);
                foreach (var chapter in chapters)
                {
                    var transformed = ReplaceStyleRoles(chapter.Manuscript, roleMap);
                    if (ManuscriptCodec.ContentEquals(chapter.Manuscript, transformed))
                        continue;
                    await manuscripts.ReplaceDocumentAsync(
                        EditorContentTarget.ForEdition(edition.Id),
                        chapter.Id,
                        chapter.ManuscriptRevision,
                        transformed,
                        cancellationToken);
                }
                var editionCompositions = await db.PageCompositions
                    .Where(item => item.EditionId == edition.Id)
                    .ToListAsync(cancellationToken);
                foreach (var composition in editionCompositions)
                {
                    var semantic = ManuscriptCodec.Deserialize(
                        composition.SemanticManuscriptJson, composition.Id, composition.Revision);
                    var transformed = ReplaceStyleRoles(semantic, roleMap);
                    if (!ManuscriptCodec.ContentEquals(semantic, transformed))
                    {
                        composition.SemanticManuscriptJson = ManuscriptCodec.Serialize(transformed);
                        composition.Revision = checked(composition.Revision + 1);
                        composition.UpdatedAt = DateTime.UtcNow;
                    }
                }
                var editionMatter = await db.PublicationMatter
                    .Where(item => item.EditionId == edition.Id)
                    .ToListAsync(cancellationToken);
                foreach (var matter in editionMatter)
                {
                    var semantic = ManuscriptCodec.Deserialize(matter.ManuscriptJson, matter.Id, matter.Revision);
                    var transformed = ReplaceStyleRoles(semantic, roleMap);
                    if (!ManuscriptCodec.ContentEquals(semantic, transformed))
                    {
                        matter.ManuscriptJson = ManuscriptCodec.Serialize(transformed);
                        matter.Revision = checked(matter.Revision + 1);
                        matter.UpdatedAt = DateTime.UtcNow;
                    }
                }
                touchedEditions.Add(edition.Id);
                await db.SaveChangesAsync(cancellationToken);
            }

            if (touchedEditions.Count > 0)
            {
                await db.PublicationArtifacts.Where(item => item.EditionId.HasValue && touchedEditions.Contains(item.EditionId.Value))
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsLegacy, true), cancellationToken);
                await db.PublicationRenderJobs.Where(item => item.EditionId.HasValue && touchedEditions.Contains(item.EditionId.Value))
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsLegacy, true), cancellationToken);
            }
            var finalArtifactState = await db.PublicationArtifacts.AsNoTracking()
                .OrderBy(item => item.Id)
                .Select(item => new { item.Id, item.Sha256, item.ByteLength })
                .ToListAsync(cancellationToken);
            if (!artifactState.SequenceEqual(finalArtifactState))
                throw new InvalidDataException("Edition-content migration changed retained artifact identities or hashes.");

            journal.ChapterCount = await db.PublicationEditionChapterOverrides.CountAsync(cancellationToken);
            journal.ValidationReportJson = JsonSerializer.Serialize(new
            {
                legacyMappings = mappings.Count,
                legacyTypographyOverrides = typography.Count(item => item.RequiresMaterialization),
                migratedEditions = touchedEditions.Count,
                editionChapters = journal.ChapterCount,
                obsoleteArtifactRowsRemoved = true,
            });
            journal.Phase = ManuscriptMigrationPhase.Complete;
            journal.Status = ManuscriptMigrationStatus.Completed;
            journal.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Edition-specific content migration failed.");
            await recovery.EnterRecoveryModeAsync(
                db,
                backupPath,
                MigrationName,
                17,
                18,
                exception,
                cancellationToken);
        }
    }

    private static async Task<List<LegacyMapping>> ReadMappingsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var result = new List<LegacyMapping>();
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EditionId, ManuscriptStyleDefinitionId, OverrideJson FROM PublicationEditionStyleMappings ORDER BY EditionId, Id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2)));
        return result;
    }

    private static async Task<List<LegacyTypography>> ReadTypographyAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var result = new List<LegacyTypography>();
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.Id, e.BodyFontSizePoints, e.BodyLineHeight,
                   s.BodyFontSizePoints, s.BodyLineHeight, e.OverrideFieldsJson
            FROM PublicationEditions e
            JOIN ProjectPageSetups s ON s.ProjectId = e.ProjectId
            ORDER BY e.Id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var bodySize = reader.GetDouble(1);
            var lineHeight = reader.GetDouble(2);
            var coreSize = reader.GetDouble(3);
            var coreLineHeight = reader.GetDouble(4);
            var overrideJson = reader.GetString(5);
            result.Add(new LegacyTypography(
                Guid.Parse(reader.GetString(0)),
                bodySize,
                lineHeight,
                HasLegacyTypographyOverride(overrideJson)
                    || Math.Abs(bodySize - coreSize) > 0.0001
                    || Math.Abs(lineHeight - coreLineHeight) > 0.0001));
        }
        return result;
    }

    private static bool HasLegacyTypographyOverride(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Array
                && document.RootElement.EnumerateArray().Any(item =>
                    (item.ValueKind == JsonValueKind.Number
                        && item.TryGetInt32(out var value)
                        && value is 19 or 20)
                    || (item.ValueKind == JsonValueKind.String
                        && item.GetString() is "BodyFontSizePoints" or "BodyLineHeight"));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ManuscriptDocument ReplaceStyleRoles(
        ManuscriptDocument document,
        IReadOnlyDictionary<string, string> roles) => document with
    {
        Content = document.Content.Select(block => block with
        {
            StyleRole = ReplacementRole(block, roles),
            Content = block.Content.Select(inline => inline with
            {
                Marks = inline.Marks.Select(mark =>
                    mark.Type == ManuscriptMarkType.CharacterStyle
                    && mark.Value is { } value
                    && roles.TryGetValue(value, out var replacement)
                        ? mark with { Value = replacement }
                        : mark).ToList(),
            }).ToList(),
        }).ToList(),
    };

    private static string ReplacementRole(
        ManuscriptBlock block,
        IReadOnlyDictionary<string, string> roles)
    {
        if (!string.IsNullOrWhiteSpace(block.StyleRole))
            return roles.GetValueOrDefault(block.StyleRole, block.StyleRole);
        return block.Type == ManuscriptBlockType.Paragraph
            ? roles.GetValueOrDefault(ManuscriptStyleRoles.Body, block.StyleRole ?? string.Empty)
            : block.StyleRole ?? string.Empty;
    }

    private static ManuscriptStyleProperties Merge(ManuscriptStyleProperties inherited, ManuscriptStyleProperties value) => inherited with
    {
        FontFamilyKey = value.FontFamilyKey ?? inherited.FontFamilyKey,
        FontSizePoints = value.FontSizePoints ?? inherited.FontSizePoints,
        FontWeight = value.FontWeight ?? inherited.FontWeight,
        Italic = value.Italic ?? inherited.Italic,
        SmallCaps = value.SmallCaps ?? inherited.SmallCaps,
        LineHeight = value.LineHeight ?? inherited.LineHeight,
        SpaceBeforePoints = value.SpaceBeforePoints ?? inherited.SpaceBeforePoints,
        SpaceAfterPoints = value.SpaceAfterPoints ?? inherited.SpaceAfterPoints,
        KeepWithNext = value.KeepWithNext ?? inherited.KeepWithNext,
        TextAlign = value.TextAlign ?? inherited.TextAlign,
        LeftIndentEm = value.LeftIndentEm ?? inherited.LeftIndentEm,
        RightIndentEm = value.RightIndentEm ?? inherited.RightIndentEm,
        FirstLineIndentEm = value.FirstLineIndentEm ?? inherited.FirstLineIndentEm,
        StartOnNewPage = value.StartOnNewPage ?? inherited.StartOnNewPage,
    };

    private static string UniqueRole(string sourceRole, Guid editionId, IReadOnlyCollection<ManuscriptStyleDefinition> styles)
    {
        var stem = $"{sourceRole}-edition-{editionId:N}"[..Math.Min(72, sourceRole.Length + 17)];
        var value = stem;
        for (var suffix = 2; styles.Any(item => string.Equals(item.SemanticRole, value, StringComparison.OrdinalIgnoreCase)); suffix++)
            value = $"{stem}-{suffix}";
        return value;
    }

    private static string UniqueName(string stem, ManuscriptStyleKind kind, IReadOnlyCollection<ManuscriptStyleDefinition> styles)
    {
        stem = stem[..Math.Min(72, stem.Length)];
        var value = stem;
        for (var suffix = 2; styles.Any(item => item.Kind == kind && string.Equals(item.Name, value, StringComparison.OrdinalIgnoreCase)); suffix++)
            value = $"{stem} {suffix}";
        return value;
    }

    private sealed record LegacyMapping(Guid EditionId, Guid StyleId, string OverrideJson);
    private sealed record LegacyTypography(
        Guid EditionId,
        double BodyFontSizePoints,
        double BodyLineHeight,
        bool RequiresMaterialization);
}
