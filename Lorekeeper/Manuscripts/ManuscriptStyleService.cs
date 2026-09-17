using System.Text.Json;
using Lorekeeper.EditorChat;
using Lorekeeper.Fonts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Manuscripts;

public interface IManuscriptStyleService
{
    Task<IReadOnlyList<ManuscriptStyleView>> ListAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<Guid, int>> GetUsageCountsAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<ManuscriptStyleView> UpsertAsync(
        Guid projectId,
        ManuscriptStyleInput input,
        CancellationToken cancellationToken = default);
    Task<ManuscriptStyleView> PreviewUpsertAsync(
        Guid projectId,
        ManuscriptStyleInput input,
        CancellationToken cancellationToken = default);
    Task DeleteAsync(
        Guid projectId,
        Guid styleId,
        long expectedRevision,
        CancellationToken cancellationToken = default);
    Task ValidateDeleteAsync(
        Guid projectId,
        Guid styleId,
        long expectedRevision,
        CancellationToken cancellationToken = default);
}

public sealed class ManuscriptStyleService(
    IAppDatabaseOperationFactory database,
    IEditorContestMutationGuard contestGuard) : IManuscriptStyleService
{
    public static readonly IReadOnlySet<string> BuiltInParagraphRoles = new HashSet<string>(
        [
            ManuscriptStyleRoles.Body,
            ManuscriptStyleRoles.Heading,
            ManuscriptStyleRoles.ChapterHeading,
            ManuscriptStyleRoles.Subheading,
            ManuscriptStyleRoles.SceneBreak,
            ManuscriptStyleRoles.BlockQuote,
            ManuscriptStyleRoles.ListItem,
            ManuscriptStyleRoles.FigureCaption,
        ],
        StringComparer.OrdinalIgnoreCase);

    public static string RoleFromName(string name)
    {
        var normalized = string.Join(
            '-',
            name.Trim().ToLowerInvariant().Split(
                [' ', '_', '-', '.', '/', '\\'],
                StringSplitOptions.RemoveEmptyEntries)
                .Select(part => new string(part.Where(char.IsLetterOrDigit).ToArray()))
                .Where(part => part.Length > 0));
        if (string.IsNullOrWhiteSpace(normalized) || !char.IsLetter(normalized[0]))
            normalized = $"style-{normalized}";
        normalized = normalized.Length <= 72 ? normalized : normalized[..72];
        return ManuscriptSemanticRoles.IsValid(normalized)
            ? normalized
            : ManuscriptSemanticRoles.NormalizeLegacy($"style-{name}");
    }

    public async Task<IReadOnlyList<ManuscriptStyleView>> ListAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        return (await db.ManuscriptStyleDefinitions
                    .AsNoTracking()
                    .Where(style => style.ProjectId == projectId)
                    .OrderBy(style => style.Kind)
                    .ThenBy(style => style.Name)
                    .ToListAsync(cancellationToken))
                .Select(ToView)
                .ToList();
    }
    public async Task<IReadOnlyDictionary<Guid, int>> GetUsageCountsAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var styles = await db.ManuscriptStyleDefinitions.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .Select(item => new { item.Id, item.SemanticRole })
            .ToListAsync(cancellationToken);
        var countsByRole = styles.ToDictionary(item => item.SemanticRole, _ => 0, StringComparer.OrdinalIgnoreCase);
        var documents = new List<string>();
        documents.AddRange(await db.Chapters.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .Select(item => item.ManuscriptJson)
            .ToListAsync(cancellationToken));
        documents.AddRange(await db.PublicationEditionChapterOverrides.AsNoTracking()
            .Where(item => item.Edition.ProjectId == projectId)
            .Select(item => item.ManuscriptJson)
            .ToListAsync(cancellationToken));
        documents.AddRange(await db.DesignedPageContents.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .Select(item => item.SemanticManuscriptJson)
            .ToListAsync(cancellationToken));
        documents.AddRange(await db.PublicationBookMatter.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .Select(item => item.ManuscriptJson)
            .ToListAsync(cancellationToken));
        documents.AddRange(await db.PublicationMatter.AsNoTracking()
            .Where(item => item.Edition.ProjectId == projectId && !item.IsExcluded)
            .Select(item => item.ManuscriptJson)
            .ToListAsync(cancellationToken));
        foreach (var json in documents.Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            foreach (var block in ManuscriptCodec.Deserialize(json).Content)
            {
                if (countsByRole.ContainsKey(block.StyleRole))
                    countsByRole[block.StyleRole]++;
            }
        }
        return styles.ToDictionary(item => item.Id, item => countsByRole[item.SemanticRole]);
    }

    public async Task<ManuscriptStyleView> UpsertAsync(
        Guid projectId,
        ManuscriptStyleInput input,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        await contestGuard.EnsureMutationAllowedAsync(projectId, cancellationToken);
        var db = databaseOperation.Db;
        input = input with { Definition = NormalizeDefinition(input.Definition) };
        ValidateInput(input);
        await ValidateFontFamilyAsync(projectId, input.Definition.FontFamilyKey, cancellationToken);
        var name = RequiredName(input.Name, "Style name");
        var role = RequiredName(input.SemanticRole, "Semantic role");
        var nameKey = name.ToLowerInvariant();
        var roleKey = role.ToLowerInvariant();
        var project = await db.Projects.FirstOrDefaultAsync(
            candidate => candidate.Id == projectId,
            cancellationToken) ?? throw new InvalidOperationException("Project was not found.");
        var existing = input.Id is Guid inputId
            ? await db.ManuscriptStyleDefinitions.FirstOrDefaultAsync(
                candidate => candidate.ProjectId == projectId && candidate.Id == inputId,
                cancellationToken)
            : null;
        ManuscriptStyleDefinition style;
        if (input.ExpectedRevision is null)
        {
            if (existing is not null)
                throw new ManuscriptStyleConflictException(-1, existing.Revision);
            if (await db.ManuscriptStyleDefinitions.AnyAsync(
                candidate => candidate.ProjectId == projectId
                    && candidate.Kind == input.Kind
                    && candidate.NameKey == nameKey,
                cancellationToken))
            {
                throw new InvalidOperationException($"A {input.Kind.ToString().ToLowerInvariant()} style named '{name}' already exists.");
            }
            if (await db.ManuscriptStyleDefinitions.AnyAsync(
                candidate => candidate.ProjectId == projectId
                    && candidate.Kind == input.Kind
                    && candidate.SemanticRoleKey == roleKey,
                cancellationToken))
            {
                throw new InvalidOperationException(
                    $"Semantic role '{role}' already has a {input.Kind.ToString().ToLowerInvariant()} style.");
            }
            style = new ManuscriptStyleDefinition
            {
                Id = input.Id ?? Guid.NewGuid(),
                ProjectId = projectId,
                Name = name,
                NameKey = nameKey,
                Kind = input.Kind,
                SemanticRole = role,
                SemanticRoleKey = roleKey,
            };
            db.ManuscriptStyleDefinitions.Add(style);
        }
        else
        {
            style = existing ?? throw new InvalidOperationException("The Book Text Style was not found.");
            if (style.Revision != input.ExpectedRevision)
                throw new ManuscriptStyleConflictException(input.ExpectedRevision.Value, style.Revision);
            if (!string.Equals(style.SemanticRoleKey, roleKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "A Book Text Style's internal semantic key cannot be changed.");
            }
            if (style.Kind != input.Kind)
                throw new InvalidOperationException("A Book Text Style's paragraph/character kind cannot be changed.");
            if (await db.ManuscriptStyleDefinitions.AnyAsync(
                candidate => candidate.ProjectId == projectId
                    && candidate.Id != style.Id
                    && candidate.Kind == input.Kind
                    && candidate.NameKey == nameKey,
                cancellationToken))
            {
                throw new InvalidOperationException($"A {input.Kind.ToString().ToLowerInvariant()} style named '{name}' already exists.");
            }
            style.Revision = checked(style.Revision + 1);
        }

        style.Name = name;
        style.NameKey = nameKey;
        style.Kind = input.Kind;
        style.SemanticRole = role;
        style.DefinitionJson = JsonSerializer.Serialize(input.Definition, ManuscriptCodec.JsonOptions);
        style.UpdatedAt = DateTime.UtcNow;
        project.UpdatedAt = style.UpdatedAt;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            var actual = input.Id is Guid styleId
                ? await CurrentRevisionAsync(projectId, styleId, cancellationToken)
                : 0;
            throw new ManuscriptStyleConflictException(input.ExpectedRevision ?? -1, actual);
        }
        catch (DbUpdateException exception)
        {
            throw new InvalidOperationException(
                "The Book Text Style conflicts with another style created or updated at the same time. Refresh styles and try again.",
                exception);
        }
        return ToView(style);
    }

    public async Task<ManuscriptStyleView> PreviewUpsertAsync(
        Guid projectId,
        ManuscriptStyleInput input,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        await ValidateFontFamilyAsync(projectId, input.Definition.FontFamilyKey, cancellationToken);
        var styles = await db.ManuscriptStyleDefinitions
            .AsNoTracking()
            .Where(style => style.ProjectId == projectId)
            .ToListAsync(cancellationToken);
        return PreviewUpsert(styles.Select(ToView).ToList(), input);
    }

    public static ManuscriptStyleView PreviewUpsert(
        IReadOnlyList<ManuscriptStyleView> styles,
        ManuscriptStyleInput input)
    {
        input = input with { Definition = NormalizeDefinition(input.Definition) };
        ValidateInput(input);
        var name = RequiredName(input.Name, "Style name");
        var role = RequiredName(input.SemanticRole, "Semantic role");
        if (input.ExpectedRevision is null)
        {
            if (input.Id is Guid requestedId && styles.Any(style => style.Id == requestedId))
                throw new ManuscriptStyleConflictException(-1, styles.First(style => style.Id == requestedId).Revision);
            if (styles.Any(style =>
                style.Kind == input.Kind
                && string.Equals(style.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"A {input.Kind.ToString().ToLowerInvariant()} style named '{name}' already exists.");
            if (styles.Any(style =>
                style.Kind == input.Kind
                && string.Equals(style.SemanticRole, role, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Semantic role '{role}' already has a {input.Kind.ToString().ToLowerInvariant()} style.");
            return new ManuscriptStyleView(
                input.Id ?? Guid.NewGuid(),
                name,
                input.Kind,
                role,
                input.Definition,
                1);
        }

        var current = input.Id is Guid id
            ? styles.FirstOrDefault(style => style.Id == id)
            : null;
        if (current is null)
            throw new InvalidOperationException("The Book Text Style was not found.");
        if (current.Revision != input.ExpectedRevision)
            throw new ManuscriptStyleConflictException(input.ExpectedRevision.Value, current.Revision);
        if (current.Kind != input.Kind
            || !string.Equals(current.SemanticRole, role, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A Book Text Style's kind and internal semantic key are immutable.");
        if (styles.Any(style =>
            style.Id != current.Id
            && style.Kind == input.Kind
            && string.Equals(style.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"A {input.Kind.ToString().ToLowerInvariant()} style named '{name}' already exists.");
        }
        return new ManuscriptStyleView(
            current.Id,
            name,
            current.Kind,
            current.SemanticRole,
            input.Definition,
            checked(current.Revision + 1));
    }

    public static void ValidateInput(ManuscriptStyleInput input)
    {
        _ = RequiredName(input.Name, "Style name");
        var semanticRole = RequiredName(input.SemanticRole, "Semantic role");
        if (!ManuscriptSemanticRoles.IsValid(semanticRole))
        {
            throw new InvalidOperationException(
                "Semantic role must be a lowercase hyphenated identifier beginning with a letter.");
        }
        if (!Enum.IsDefined(input.Kind))
            throw new InvalidOperationException("Style kind must be Paragraph or Character.");
        ValidateDefinition(input.Kind, input.Definition);
    }

    public static void ValidateDocumentReferences(
        ManuscriptDocument document,
        IReadOnlyList<ManuscriptStyleView> styles)
    {
        var paragraphRoles = styles
            .Where(style => style.Kind == ManuscriptStyleKind.Paragraph)
            .Select(style => style.SemanticRole)
            .Concat(BuiltInParagraphRoles)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var characterRoles = styles
            .Where(style => style.Kind == ManuscriptStyleKind.Character)
            .Select(style => style.SemanticRole)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknownParagraph = document.Content.FirstOrDefault(
            block => block.Type != ManuscriptBlockType.DesignedPage
                && !paragraphRoles.Contains(block.StyleRole));
        if (unknownParagraph is not null)
        {
            throw new InvalidOperationException(
                $"Block {unknownParagraph.Id} references unknown paragraph style role '{unknownParagraph.StyleRole}'.");
        }
        var unknownCharacter = document.Content
            .SelectMany(block => block.Content)
            .SelectMany(inline => inline.Marks)
            .FirstOrDefault(mark =>
                mark.Type == ManuscriptMarkType.CharacterStyle
                && !characterRoles.Contains(mark.Value!));
        if (unknownCharacter is not null)
        {
            throw new InvalidOperationException(
                $"Manuscript references unknown character style role '{unknownCharacter.Value}'.");
        }
    }

    public async Task DeleteAsync(
        Guid projectId,
        Guid styleId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        await contestGuard.EnsureMutationAllowedAsync(projectId, cancellationToken);
        var db = databaseOperation.Db;
        var style = await RequireDeletableAsync(
            projectId,
            styleId,
            expectedRevision,
            tracking: true,
            cancellationToken);
        db.ManuscriptStyleDefinitions.Remove(style);
        var project = await db.Projects.FirstAsync(candidate => candidate.Id == projectId, cancellationToken);
        project.UpdatedAt = DateTime.UtcNow;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ManuscriptStyleConflictException(
                expectedRevision,
                await CurrentRevisionAsync(projectId, styleId, cancellationToken));
        }
    }

    public async Task ValidateDeleteAsync(
        Guid projectId,
        Guid styleId,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        _ = await RequireDeletableAsync(
            projectId,
            styleId,
            expectedRevision,
            tracking: false,
            cancellationToken);

    private async Task<ManuscriptStyleDefinition> RequireDeletableAsync(
        Guid projectId,
        Guid styleId,
        long expectedRevision,
        bool tracking,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var query = tracking
            ? db.ManuscriptStyleDefinitions.AsQueryable()
            : db.ManuscriptStyleDefinitions.AsNoTracking();
        var style = await query.FirstOrDefaultAsync(
            candidate => candidate.ProjectId == projectId && candidate.Id == styleId,
            cancellationToken);
        if (style is null)
            throw new InvalidOperationException("The Book Text Style was not found.");
        if (style.Revision != expectedRevision)
            throw new ManuscriptStyleConflictException(expectedRevision, style.Revision);
        var requiresDefinition = style.Kind == ManuscriptStyleKind.Character
            || !BuiltInParagraphRoles.Contains(style.SemanticRole);
        var isUsed = false;
        if (requiresDefinition)
        {
            var manuscripts = await db.Chapters
                .AsNoTracking()
                .Where(chapter => chapter.ProjectId == projectId)
                .Select(chapter => chapter.ManuscriptJson)
                .Concat(db.PublicationEditionChapterOverrides
                    .AsNoTracking()
                    .Where(item => item.Edition.ProjectId == projectId)
                    .Select(item => item.ManuscriptJson))
                .Concat(db.PublicationMatter
                    .AsNoTracking()
                    .Where(matter => matter.Edition.ProjectId == projectId)
                    .Select(matter => matter.ManuscriptJson))
                .ToListAsync(cancellationToken);
            isUsed = manuscripts
                .Select(ManuscriptCodec.Deserialize)
                .Any(document => style.Kind == ManuscriptStyleKind.Paragraph
                    ? document.Content.Any(block =>
                        string.Equals(block.StyleRole, style.SemanticRole, StringComparison.OrdinalIgnoreCase))
                    : document.Content.SelectMany(block => block.Content)
                        .SelectMany(inline => inline.Marks)
                        .Any(mark => mark.Type == ManuscriptMarkType.CharacterStyle
                            && string.Equals(mark.Value, style.SemanticRole, StringComparison.OrdinalIgnoreCase)));
        }
        if (isUsed)
            throw new InvalidOperationException("The Book Text Style is still used by manuscript or publication-matter content.");
        return style;
    }

    private static ManuscriptStyleView ToView(ManuscriptStyleDefinition style) =>
        new(
            style.Id,
            style.Name,
            style.Kind,
            style.SemanticRole,
            NormalizeDefinition(
                JsonSerializer.Deserialize<ManuscriptStyleProperties>(
                    style.DefinitionJson,
                    ManuscriptCodec.JsonOptions) ?? new ManuscriptStyleProperties()),
            style.Revision);

    private async Task<long> CurrentRevisionAsync(
        Guid projectId,
        Guid styleId,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        return await db.ManuscriptStyleDefinitions
            .AsNoTracking()
            .Where(style => style.ProjectId == projectId && style.Id == styleId)
            .Select(style => (long?)style.Revision)
            .SingleOrDefaultAsync(cancellationToken) ?? 0;
    }

    private static string RequiredName(string? value, string label)
    {
        if (value is null)
            throw new InvalidOperationException($"{label} is required.");
        var normalized = value.Trim();
        if (normalized.Length is < 1 or > 80)
            throw new InvalidOperationException($"{label} must contain 1 to 80 characters.");
        return normalized;
    }

    private static void ValidateDefinition(
        ManuscriptStyleKind kind,
        ManuscriptStyleProperties? value)
    {
        if (value is null)
            throw new InvalidOperationException("Style definition is required.");
        var doubles = new[]
        {
            value.FontSizePoints,
            value.LineHeight,
            value.SpaceBeforePoints,
            value.SpaceAfterPoints,
            value.LeftIndentEm,
            value.RightIndentEm,
            value.FirstLineIndentEm,
        };
        if (doubles.Any(number => number is double present && !double.IsFinite(present)))
            throw new InvalidOperationException("Style numeric properties must be finite numbers.");
        if (value.FontFamilyKey is { } fontFamily
            && !IsSupportedFontKey(fontFamily))
        {
            throw new InvalidOperationException("Choose a bundled, imported, serif, sans-serif, or monospace font family.");
        }
        if (value.TextAlign is { } alignment
            && !new[] { "left", "right", "center", "justify" }.Contains(
                alignment.Trim(),
                StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Text alignment must be left, right, center, justify, or omitted.");
        }
        if (value.FontSizePoints is <= 0 or > 288)
            throw new InvalidOperationException("Font size must be greater than 0 and no more than 288 points.");
        if (value.FontWeight is int fontWeight
            && (fontWeight is < 100 or > 900 || fontWeight % 100 != 0))
            throw new InvalidOperationException("Font weight must be a 100-step value from 100 through 900.");
        if (value.LineHeight is <= 0 or > 5)
            throw new InvalidOperationException("Line height must be greater than 0 and no more than 5.");
        if (value.SpaceBeforePoints is < 0 or > 288 || value.SpaceAfterPoints is < 0 or > 288)
            throw new InvalidOperationException("Style spacing must be between 0 and 288 points.");
        if (value.LeftIndentEm is < 0 or > 12 || value.RightIndentEm is < 0 or > 12)
            throw new InvalidOperationException("Style paragraph indents must be between 0 and 12 em.");
        if (value.FirstLineIndentEm is < -12 or > 12)
            throw new InvalidOperationException("Style first-line indent must be between -12 and 12 em.");
        if (kind == ManuscriptStyleKind.Character
            && (value.SpaceBeforePoints is not null
                || value.SpaceAfterPoints is not null
                || value.KeepWithNext is not null
                || value.TextAlign is not null
                || value.LeftIndentEm is not null
                || value.RightIndentEm is not null
                || value.FirstLineIndentEm is not null
                || value.StartOnNewPage is not null))
        {
            throw new InvalidOperationException(
                "Character styles cannot define paragraph spacing, indentation, pagination, or text alignment.");
        }
    }

    public static ManuscriptStyleProperties NormalizeDefinition(
        ManuscriptStyleProperties? definition)
    {
        if (definition is null)
            throw new InvalidOperationException("Style definition is required.");
        return definition with
        {
            FontFamilyKey = definition.FontFamilyKey?.Trim().ToLowerInvariant(),
            TextAlign = NormalizeTextAlignment(definition.TextAlign),
            Italic = definition.Italic is true ? true : null,
            SmallCaps = definition.SmallCaps is true ? true : null,
            KeepWithNext = definition.KeepWithNext is true ? true : null,
            StartOnNewPage = definition.StartOnNewPage is true ? true : null,
        };
    }

    public static bool IsSupportedFontKey(string value)
    {
        var key = value.Trim();
        return new[] { "serif", "sans", "mono" }.Contains(key, StringComparer.OrdinalIgnoreCase)
            || PublicationBuiltInFonts.Find(key) is not null
            || key.StartsWith("project:", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(key["project:".Length..], out var id)
                && id != Guid.Empty;
    }

    private async Task ValidateFontFamilyAsync(
        Guid projectId,
        string? fontFamilyKey,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        if (string.IsNullOrWhiteSpace(fontFamilyKey)
            || !fontFamilyKey.StartsWith("project:", StringComparison.OrdinalIgnoreCase))
            return;
        if (!Guid.TryParse(fontFamilyKey["project:".Length..], out var familyId)
            || !await db.ProjectFontFamilies.AsNoTracking().AnyAsync(
                item => item.Id == familyId && item.ProjectId == projectId,
                cancellationToken))
            throw new InvalidOperationException("The selected imported font family does not belong to this project.");
    }

    public static ManuscriptStyleProperties NormalizeOverride(
        ManuscriptStyleKind kind,
        ManuscriptStyleProperties? definition)
    {
        if (definition is null)
            throw new InvalidOperationException("Style definition is required.");
        var normalized = definition with
        {
            FontFamilyKey = definition.FontFamilyKey?.Trim().ToLowerInvariant(),
            TextAlign = NormalizeTextAlignment(definition.TextAlign),
        };
        ValidateDefinition(kind, normalized);
        return normalized;
    }

    private static string? NormalizeTextAlignment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return value.Trim().ToLowerInvariant() switch
        {
            "start" or "left" => "left",
            "end" or "right" => "right",
            "center" => "center",
            "justify" => "justify",
            var unsupported => unsupported,
        };
    }
}

public sealed record ManuscriptStyleInput(
    Guid? Id,
    string Name,
    ManuscriptStyleKind Kind,
    string SemanticRole,
    ManuscriptStyleProperties Definition,
    long? ExpectedRevision = null);

public sealed record ManuscriptStyleView(
    Guid Id,
    string Name,
    ManuscriptStyleKind Kind,
    string SemanticRole,
    ManuscriptStyleProperties Definition,
    long Revision);

public sealed record ManuscriptStyleChange(
    ManuscriptStyleView? Before,
    ManuscriptStyleInput? After);

public sealed record ManuscriptStyleProperties(
    string? FontFamilyKey = null,
    double? FontSizePoints = null,
    int? FontWeight = null,
    bool? Italic = null,
    bool? SmallCaps = null,
    double? LineHeight = null,
    double? SpaceBeforePoints = null,
    double? SpaceAfterPoints = null,
    bool? KeepWithNext = null,
    string? TextAlign = null,
    double? LeftIndentEm = null,
    double? RightIndentEm = null,
    double? FirstLineIndentEm = null,
    bool? StartOnNewPage = null);

public sealed class ManuscriptStyleConflictException(long expected, long actual)
    : InvalidOperationException($"Named-style revision conflict: expected {expected}, current revision is {actual}.")
{
    public long ExpectedRevision { get; } = expected;
    public long ActualRevision { get; } = actual;
}
