using System.Text.Json;
using Lorekeeper.Authoring;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Fonts;

public sealed class ProjectFontService(
    IAppDatabaseOperationFactory database,
    IWebHostEnvironment environment,
    IAuthoringHistoryService authoringHistory) : IProjectFontService
{
    public async Task<IReadOnlyList<ProjectFontFamilyView>> ListAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var custom = await db.ProjectFontFamilies
            .AsNoTracking()
            .Include(family => family.Faces)
            .Where(family => family.ProjectId == projectId)
            .OrderBy(family => family.Name)
            .ToListAsync(cancellationToken);

        return PublicationBuiltInFonts.Families
            .Concat(custom.Select(family => ToView(projectId, family)))
            .ToList();
    }

    public async Task<ProjectFontFamilyView> ImportAsync(
        Guid projectId,
        ProjectFontUpload upload,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var project = await db.Projects.FirstOrDefaultAsync(project => project.Id == projectId, cancellationToken)
            ?? throw new InvalidOperationException("Project was not found.");
        if (!upload.EmbeddingRightsConfirmed || string.IsNullOrWhiteSpace(upload.RightsDeclaration))
            throw new InvalidOperationException("Confirm and record your right to embed and distribute this font with publication files.");
        var normalized = ProjectFontBinary.Normalize(upload.Data, upload.FileName);
        var family = await db.ProjectFontFamilies
            .Include(candidate => candidate.Faces)
            .FirstOrDefaultAsync(
                candidate => candidate.ProjectId == projectId && candidate.Name.ToLower() == normalized.FamilyName.ToLower(),
                cancellationToken);
        if (family is null)
        {
            family = new ProjectFontFamily
            {
                ProjectId = projectId,
                Name = normalized.FamilyName,
                EmbeddingRightsConfirmed = true,
                RightsDeclaration = upload.RightsDeclaration.Trim(),
            };
            await db.ProjectFontFamilies.AddAsync(family, cancellationToken);
        }
        else if (family.Faces.Any(face => face.Weight == normalized.Weight && face.Italic == normalized.Italic))
        {
            throw new InvalidOperationException(
                $"{normalized.FamilyName} already has a {normalized.Weight}{(normalized.Italic ? " italic" : string.Empty)} face.");
        }
        else
        {
            family.EmbeddingRightsConfirmed = true;
            family.RightsDeclaration = upload.RightsDeclaration.Trim();
        }

        var face = new ProjectFontFace
        {
            FamilyId = family.Id,
            Family = family,
            SubfamilyName = normalized.SubfamilyName,
            FileName = normalized.FileName,
            ContentType = normalized.ContentType,
            Weight = normalized.Weight,
            Italic = normalized.Italic,
            Data = normalized.Data,
        };
        await db.ProjectFontFaces.AddAsync(face, cancellationToken);
        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return ToView(projectId, family);
    }

    public async Task DeleteFamilyAsync(
        Guid projectId,
        Guid familyId,
        bool clearAffectedHistory = false,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var family = await db.ProjectFontFamilies
            .Include(candidate => candidate.Faces)
            .FirstOrDefaultAsync(candidate => candidate.ProjectId == projectId && candidate.Id == familyId, cancellationToken);
        if (family is null)
            return;

        var key = CustomKey(family.Id);
        var usedBy = (await db.PageCompositionVariants
                .AsNoTracking()
                .Where(variant => variant.Composition.ProjectId == projectId
                    && variant.DetachedAt == null && variant.Composition.DetachedAt == null)
                .Select(variant => new { variant.Composition.Name, variant.SceneJson })
                .ToListAsync(cancellationToken))
            .Where(item => SceneUsesFont(item.SceneJson, key))
            .Select(item => item.Name)
            .Concat((await db.PublicationCoverDesigns
                    .AsNoTracking()
                    .Where(cover => cover.Edition.ProjectId == projectId)
                    .Select(cover => new { cover.Edition.Name, cover.CompositionSceneJson })
                    .ToListAsync(cancellationToken))
                .Where(item => SceneUsesFont(item.CompositionSceneJson, key))
                .Select(item => item.Name))
            .Concat((await db.ManuscriptStyleDefinitions
                    .AsNoTracking()
                    .Where(style => style.ProjectId == projectId)
                    .Select(style => new { style.Name, style.DefinitionJson })
                    .ToListAsync(cancellationToken))
                .Where(item => StyleUsesFont(item.DefinitionJson, key))
                .Select(item => $"Book Text Style {item.Name}"))
            .Concat((await db.Chapters
                    .AsNoTracking()
                    .Where(chapter => chapter.ProjectId == projectId)
                    .Select(chapter => new { chapter.Title, chapter.ManuscriptJson })
                    .ToListAsync(cancellationToken))
                .Where(item => ManuscriptUsesFont(item.ManuscriptJson, key))
                .Select(item => $"chapter {item.Title}"))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (usedBy.Count > 0)
        {
            throw new InvalidOperationException(
                $"{family.Name} is used by {usedBy.Count} book item(s): {string.Join(", ", usedBy)}. Choose another font before deleting it.");
        }

        var dependentHistory = await authoringHistory.FindDependentStreamsAsync(
            projectId, AuthoringHistoryDependencyKind.ProjectFont, familyId, cancellationToken);
        if (dependentHistory.Count > 0 && !clearAffectedHistory)
            throw new InvalidOperationException($"AUTHORING_HISTORY_DEPENDENCY: This font is retained by {dependentHistory.Count} Undo/Redo histor{(dependentHistory.Count == 1 ? "y" : "ies")}. Delete it and clear the affected history?");
        if (dependentHistory.Count > 0)
            await authoringHistory.ClearDependentStreamsAsync(projectId, AuthoringHistoryDependencyKind.ProjectFont, familyId, cancellationToken);

        db.ProjectFontFamilies.Remove(family);
        var project = await db.Projects.FirstAsync(project => project.Id == projectId, cancellationToken);
        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static bool SceneUsesFont(string json, string key)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            var scene = JsonSerializer.Deserialize<CompositionScene>(json, ManuscriptCodec.JsonOptions);
            return scene is not null && (scene.Objects.Any(item => string.Equals(item.FontFamilyKey, key, StringComparison.OrdinalIgnoreCase))
                || scene.Styles.Any(style => string.Equals(style.FontFamilyKey, key, StringComparison.OrdinalIgnoreCase)));
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static bool StyleUsesFont(string json, string key)
    {
        try
        {
            var definition = JsonSerializer.Deserialize<ManuscriptStyleProperties>(json, ManuscriptCodec.JsonOptions);
            return string.Equals(definition?.FontFamilyKey, key, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static bool ManuscriptUsesFont(string json, string key)
    {
        try
        {
            var document = ManuscriptCodec.Deserialize(json);
            return document.Content.Any(block => string.Equals(
                block.ParagraphPresentation?.FontFamilyKey,
                key,
                StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidDataException)
        {
            return true;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    public async Task<ProjectFontFaceData?> GetFaceDataAsync(
        Guid projectId,
        Guid faceId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var face = await db.ProjectFontFaces
            .AsNoTracking()
            .Include(candidate => candidate.Family)
            .FirstOrDefaultAsync(candidate => candidate.Id == faceId && candidate.Family.ProjectId == projectId, cancellationToken);
        return face is null ? null : ToData(face);
    }

    public async Task<ProjectFontFaceData?> ResolveFaceAsync(
        Guid projectId,
        string familyKey,
        int weight,
        bool italic,
        bool requireExact = false,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        if (PublicationBuiltInFonts.Find(familyKey) is { } builtIn)
        {
            var face = SelectFace(builtIn.Faces, weight, italic, requireExact);
            if (face is null)
                return null;
            var relativePath = face.ContentUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var path = Path.Combine(environment.WebRootPath, relativePath);
            if (!File.Exists(path))
                throw new InvalidOperationException($"Bundled font asset is missing: {relativePath}.");
            return new ProjectFontFaceData(
                Id: null,
                builtIn.Key,
                builtIn.Name,
                Path.GetFileName(path),
                "font/ttf",
                face.Weight,
                face.Italic,
                await File.ReadAllBytesAsync(path, cancellationToken));
        }

        if (!TryParseCustomKey(familyKey, out var familyId))
            return null;
        var family = await db.ProjectFontFamilies
            .AsNoTracking()
            .Include(candidate => candidate.Faces)
            .FirstOrDefaultAsync(candidate => candidate.ProjectId == projectId && candidate.Id == familyId, cancellationToken);
        if (family is null)
            return null;
        var selected = SelectFace(family.Faces, weight, italic, requireExact);
        return selected is null ? null : ToData(selected);
    }

    public static string CustomKey(Guid familyId) => $"project:{familyId:N}";

    private static ProjectFontFamilyView ToView(Guid projectId, ProjectFontFamily family) =>
        new(
            CustomKey(family.Id),
            family.Id,
            family.Name,
            "Imported",
            IsBuiltIn: false,
            family.EmbeddingRightsConfirmed,
            family.RightsDeclaration,
            family.Faces
                .OrderBy(face => face.Weight)
                .ThenBy(face => face.Italic)
                .Select(face => new ProjectFontFaceView(
                    face.Id,
                    face.SubfamilyName,
                    face.Weight,
                    face.Italic,
                    $"/projects/{projectId:N}/fonts/{face.Id:N}/content"))
                .ToList());

    private static ProjectFontFaceData ToData(ProjectFontFace face) =>
        new(
            face.Id,
            CustomKey(face.FamilyId),
            face.Family.Name,
            face.FileName,
            face.ContentType,
            face.Weight,
            face.Italic,
            face.Data);

    private static TFace? SelectFace<TFace>(
        IEnumerable<TFace> faces,
        int weight,
        bool italic,
        bool requireExact)
        where TFace : class
    {
        static int Weight(TFace face) => face switch
        {
            ProjectFontFaceView view => view.Weight,
            ProjectFontFace model => model.Weight,
            _ => 400,
        };
        static bool Italic(TFace face) => face switch
        {
            ProjectFontFaceView view => view.Italic,
            ProjectFontFace model => model.Italic,
            _ => false,
        };

        var exact = faces.FirstOrDefault(face => Weight(face) == weight && Italic(face) == italic);
        if (exact is not null || requireExact)
            return exact;
        return faces
            .OrderBy(face => Italic(face) == italic ? 0 : 1)
            .ThenBy(face => Math.Abs(Weight(face) - weight))
            .FirstOrDefault();
    }

    private static bool TryParseCustomKey(string key, out Guid familyId)
    {
        const string prefix = "project:";
        familyId = Guid.Empty;
        return key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && Guid.TryParseExact(key[prefix.Length..], "N", out familyId);
    }
}
