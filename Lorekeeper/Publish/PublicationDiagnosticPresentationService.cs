using System.Text.RegularExpressions;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Publish;

public enum PublicationDiagnosticTargetKind
{
    None,
    ChapterPage,
    PublicationSectionPage,
    Cover,
}

public sealed record PublicationDiagnosticPresentation(
    string Severity,
    string Title,
    string Message,
    string? TargetLabel,
    PublicationDiagnosticTargetKind TargetKind,
    Guid? ChapterId = null,
    Guid? EditionId = null,
    Guid? PublicationSectionId = null,
    Guid? DesignedPageId = null,
    Guid? ObjectId = null);

public static partial class PublicationDiagnosticText
{
    public static string SanitizeIdentifiers(string message) =>
        SanitizeUserFacing(message);

    public static string SanitizeUserFacing(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return message;
        var sanitized = ObjectIdentifierRegex().Replace(message, "This item ");
        return IdentifierRegex().Replace(sanitized, "this item").Trim();
    }

    [GeneratedRegex("(?i)(?:Composition\\s+)?(?:object|frame|item)\\s+['\"]?(?:[0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})['\"]?\\s*")]
    private static partial Regex ObjectIdentifierRegex();

    [GeneratedRegex("(?i)(?:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|[0-9a-f]{32})")]
    private static partial Regex IdentifierRegex();
}

public interface IPublicationDiagnosticPresentationService
{
    Task<IReadOnlyList<PublicationDiagnosticPresentation>> ResolveAsync(
        Guid projectId,
        Guid? editionId,
        IReadOnlyList<PublicationPreflightItem> diagnostics,
        CancellationToken cancellationToken = default);
}

public sealed partial class PublicationDiagnosticPresentationService(IAppDatabaseOperationFactory database)
    : IPublicationDiagnosticPresentationService
{
    public async Task<IReadOnlyList<PublicationDiagnosticPresentation>> ResolveAsync(
        Guid projectId,
        Guid? editionId,
        IReadOnlyList<PublicationPreflightItem> diagnostics,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        if (diagnostics.Count == 0)
            return [];

        var chapterOrdinals = (await db.Chapters.AsNoTracking()
                .Where(item => item.ProjectId == projectId)
                .OrderBy(item => item.Act == null ? int.MaxValue : item.Act.Order)
                .ThenBy(item => item.Order)
                .ThenBy(item => item.CreatedAt)
                .Select(item => item.Id)
                .ToListAsync(cancellationToken))
            .Select((id, index) => (id, ordinal: index + 1))
            .ToDictionary(item => item.id, item => item.ordinal);

        var output = new List<PublicationDiagnosticPresentation>(diagnostics.Count);
        foreach (var diagnostic in diagnostics)
        {
            var target = await ResolveTargetAsync(
                projectId,
                editionId,
                diagnostic,
                chapterOrdinals,
                cancellationToken);
            output.Add(new(
                diagnostic.Severity,
                FriendlyTitle(diagnostic.Code),
                FriendlyMessage(diagnostic.Code, diagnostic.Message),
                target?.Label,
                target?.Kind ?? PublicationDiagnosticTargetKind.None,
                target?.ChapterId,
                target?.EditionId,
                target?.PublicationSectionId,
                target?.DesignedPageId,
                target?.ObjectId));
        }
        return output;
    }

    private async Task<ResolvedTarget?> ResolveTargetAsync(
        Guid projectId,
        Guid? selectedEditionId,
        PublicationPreflightItem diagnostic,
        IReadOnlyDictionary<Guid, int> chapterOrdinals,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var candidateIds = ExtractIds(diagnostic).ToList();
        foreach (var candidateId in candidateIds)
        {
            var canonicalCandidate = candidateId.ToString("D");
            var compactCandidate = candidateId.ToString("N");
            var matchingVariants = await db.DesignedPageVariants.AsNoTracking()
                .Include(item => item.Content).ThenInclude(item => item.Page)
                .Where(item => item.Content.ProjectId == projectId
                    && (selectedEditionId == null
                        ? item.Content.EditionId == null
                        : item.Content.EditionId == selectedEditionId || item.Content.EditionId == null)
                    && (item.SceneJson.Contains(canonicalCandidate)
                        || item.SceneJson.Contains(compactCandidate)))
                .ToListAsync(cancellationToken);
            var variantMatch = matchingVariants
                .Select(item => (Variant: item, ObjectId: SceneObjectReference(item.SceneJson, candidateId)))
                .Where(item => item.ObjectId is not null)
                .OrderByDescending(item => item.Variant.Content.EditionId == selectedEditionId)
                .FirstOrDefault();
            if (variantMatch.Variant is not null)
                return await DesignedPageTargetAsync(
                    db,
                    variantMatch.Variant.Content.Page,
                    variantMatch.ObjectId,
                    selectedEditionId,
                    chapterOrdinals,
                    cancellationToken);

            var composition = await db.DesignedPages.AsNoTracking()
                .Where(item => item.ProjectId == projectId
                    && item.Id == candidateId
                    && (selectedEditionId == null
                        ? item.ScopeEditionId == null
                        : item.ScopeEditionId == selectedEditionId || item.ScopeEditionId == null))
                .OrderByDescending(item => item.ScopeEditionId == selectedEditionId)
                .FirstOrDefaultAsync(cancellationToken);
            if (composition is not null)
                return await DesignedPageTargetAsync(
                    db,
                    composition,
                    null,
                    selectedEditionId,
                    chapterOrdinals,
                    cancellationToken);

            if (selectedEditionId is Guid editionId)
            {
                var releaseCover = await db.PublicationCoverDesigns.AsNoTracking()
                    .Include(item => item.Edition)
                    .SingleOrDefaultAsync(item => item.EditionId == editionId
                        && item.Edition.ProjectId == projectId
                        && item.CompositionSceneJson.Contains(candidateId.ToString()), cancellationToken);
                var objectId = releaseCover is null
                    ? null
                    : SceneObjectReference(releaseCover.CompositionSceneJson, candidateId);
                if (releaseCover is not null && objectId is not null)
                {
                    return new(
                        PublicationDiagnosticTargetKind.Cover,
                        $"{releaseCover.Edition.Name} cover",
                        EditionId: releaseCover.EditionId,
                        ObjectId: objectId);
                }
            }
            else
            {
                var coreCover = await db.PublicationBookCoverDesigns.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.ProjectId == projectId
                        && item.CompositionSceneJson.Contains(candidateId.ToString()), cancellationToken);
                var objectId = coreCover is null
                    ? null
                    : SceneObjectReference(coreCover.CompositionSceneJson, candidateId);
                if (coreCover is not null && objectId is not null)
                {
                    return new(PublicationDiagnosticTargetKind.Cover, "Core Book cover", ObjectId: objectId);
                }
            }

            var chapter = await db.Chapters.AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == candidateId && item.ProjectId == projectId, cancellationToken);
            if (chapter is not null)
            {
                var label = chapterOrdinals.TryGetValue(chapter.Id, out var ordinal)
                    ? $"Chapter {ordinal}: {chapter.Title}"
                    : chapter.Title;
                return new(PublicationDiagnosticTargetKind.ChapterPage, label, ChapterId: chapter.Id, EditionId: selectedEditionId);
            }

            var publicationSection = await db.PublicationSections.AsNoTracking()
                .Where(item => item.Id == candidateId
                    && item.ProjectId == projectId
                    && (selectedEditionId == null
                        ? item.EditionId == null
                        : item.EditionId == selectedEditionId || item.EditionId == null))
                .OrderByDescending(item => item.EditionId == selectedEditionId)
                .FirstOrDefaultAsync(cancellationToken);
            if (publicationSection is not null)
            {
                return new(
                    PublicationDiagnosticTargetKind.PublicationSectionPage,
                    publicationSection.Title,
                    EditionId: selectedEditionId ?? publicationSection.EditionId,
                    PublicationSectionId: publicationSection.Id);
            }
        }

        return null;
    }

    private static async Task<ResolvedTarget> DesignedPageTargetAsync(
        AppDbContext db,
        DesignedPage page,
        Guid? objectId,
        Guid? selectedEditionId,
        IReadOnlyDictionary<Guid, int> chapterOrdinals,
        CancellationToken cancellationToken)
    {
        var placement = await db.DesignedPagePlacementReferences.AsNoTracking()
            .Where(item => item.ProjectId == page.ProjectId && item.DesignedPageId == page.Id
                && (selectedEditionId == null
                    ? item.EditionId == null
                    : item.EditionId == selectedEditionId || item.EditionId == null))
            .OrderByDescending(item => item.EditionId == selectedEditionId)
            .ThenBy(item => item.ContainerKind)
            .ThenBy(item => item.ContainerId)
            .FirstOrDefaultAsync(cancellationToken);
        if (placement?.ContainerKind == DesignedPageContainerKind.Chapter)
        {
            var chapter = await db.Chapters.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == placement.ContainerId, cancellationToken);
            if (chapter is not null)
            {
                var label = chapterOrdinals.TryGetValue(chapter.Id, out var ordinal)
                    ? $"Chapter {ordinal}: {chapter.Title}"
                    : chapter.Title;
                return new(PublicationDiagnosticTargetKind.ChapterPage, label, chapter.Id,
                    selectedEditionId ?? placement.EditionId, DesignedPageId: page.Id, ObjectId: objectId);
            }
        }
        if (placement?.ContainerKind == DesignedPageContainerKind.PublicationSection)
        {
            var section = await db.PublicationSections.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == placement.ContainerId, cancellationToken);
            if (section is not null)
            {
                return new(PublicationDiagnosticTargetKind.PublicationSectionPage, section.Title,
                    EditionId: selectedEditionId ?? placement.EditionId,
                    PublicationSectionId: section.Id, DesignedPageId: page.Id, ObjectId: objectId);
            }
        }

        var pageLabel = string.IsNullOrWhiteSpace(page.Name)
            ? "Designed page"
            : page.Name.Trim();
        return new(
            PublicationDiagnosticTargetKind.None,
            pageLabel,
            EditionId: selectedEditionId ?? page.ScopeEditionId,
            DesignedPageId: page.Id,
            ObjectId: objectId);
    }

    private static IEnumerable<Guid> ExtractIds(PublicationPreflightItem diagnostic)
    {
        var seen = new HashSet<Guid>();
        if (Guid.TryParse(diagnostic.SourceId, out var sourceId) && seen.Add(sourceId))
            yield return sourceId;
        foreach (Match match in IdentifierRegex().Matches(diagnostic.Message))
        {
            if (Guid.TryParse(match.Value, out var id) && seen.Add(id))
                yield return id;
        }
    }

    private static Guid? SceneObjectReference(string sceneJson, Guid referencedId)
    {
        try
        {
            var scene = System.Text.Json.JsonSerializer.Deserialize<CompositionScene>(
                sceneJson,
                ManuscriptCodec.JsonOptions);
            var compact = referencedId.ToString("N");
            var canonical = referencedId.ToString("D");
            return scene?.Objects
                .FirstOrDefault(item => item.Id == referencedId
                    || item.ImageId == referencedId
                    || item.ContentReferences.Any(reference =>
                        reference.BlockId.Contains(compact, StringComparison.OrdinalIgnoreCase)
                        || reference.BlockId.Contains(canonical, StringComparison.OrdinalIgnoreCase)))
                ?.Id;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string FriendlyTitle(string code)
    {
        if (FriendlyTitles.TryGetValue(code, out var title))
            return title;
        var words = code.StartsWith("PRESS_", StringComparison.OrdinalIgnoreCase)
            ? code[6..]
            : code;
        words = words.Replace('_', ' ').Trim().ToLowerInvariant();
        return words.Length == 0 ? "Production note" : char.ToUpperInvariant(words[0]) + words[1..];
    }

    private static string FriendlyMessage(string code, string message)
    {
        if (string.Equals(code, "COVER_OBJECT_OUTSIDE_SURFACE", StringComparison.OrdinalIgnoreCase))
            return "This item extends outside the physical cover surface.";
        if (string.Equals(code, "COVER_BARCODE_OVERLAP", StringComparison.OrdinalIgnoreCase))
            return "This item places important content in the barcode placement area. Background artwork may continue through this area, but the printer may cover it with a barcode.";
        if (string.Equals(code, "COVER_SAFE_AREA_OVERFLOW", StringComparison.OrdinalIgnoreCase))
            return "This item extends outside the safe area for its cover region.";
        if (string.Equals(code, "COVER_TRANSPARENCY_UNSUPPORTED", StringComparison.OrdinalIgnoreCase))
            return "Overlapping transparent cover items cannot be preserved in this printer's PDF format. Make the upper item opaque or combine the artwork into one image.";
        if (string.Equals(code, "PRESS_COMPOSITION_OBJECT_CLIPPED", StringComparison.OrdinalIgnoreCase))
            return "An item extends beyond the canvas and will be clipped to the page.";
        if (string.Equals(code, "PRESS_ALT_DECISION_REQUIRED", StringComparison.OrdinalIgnoreCase))
            return "An image requires alternative text or an explicit decorative decision before publishing.";
        if (string.Equals(code, "PRESS_COMPOSITION_RANGE_INVALID", StringComparison.OrdinalIgnoreCase))
            return "A text frame points to an invalid portion of its page content. Open the page to repair or replace that text.";

        return PublicationDiagnosticText.SanitizeUserFacing(message);
    }

    private static readonly IReadOnlyDictionary<string, string> FriendlyTitles =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PRESS_COMPOSITION_OBJECT_CLIPPED"] = "Page item extends beyond the canvas",
            ["PRESS_COMPOSITION_TEXT_OVERFLOW"] = "Text does not fit its frame",
            ["PRESS_ALT_DECISION_REQUIRED"] = "Image accessibility needs attention",
            ["PRESS_COMPOSITION_CONTENT_UNPLACED"] = "Page content has not been placed",
            ["PRESS_COMPOSITION_VARIANT_MISSING"] = "Page layout is missing",
            ["PRESS_COMPOSITION_MISSING"] = "Designed Page is missing",
            ["PRESS_COMPOSITION_RANGE_INVALID"] = "Page text reference needs repair",
            ["PRESS_PAGE_MAP_INCOMPLETE"] = "Some content was not placed on a page",
            ["PRESS_PAGE_MAP_DUPLICATE"] = "Duplicate page entry",
            ["PRESS_PAGE_MAP_UNEXPECTED"] = "Unexpected page entry",
            ["COVER_OBJECT_OUTSIDE_SURFACE"] = "Cover item outside the surface",
            ["COVER_BARCODE_OVERLAP"] = "Barcode placement warning",
            ["COVER_SAFE_AREA_OVERFLOW"] = "Cover item outside the safe area",
            ["COVER_TRANSPARENCY_UNSUPPORTED"] = "Cover transparency is unsupported",
        };

    [GeneratedRegex("(?i)(?:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|[0-9a-f]{32})")]
    private static partial Regex IdentifierRegex();

    private sealed record ResolvedTarget(
        PublicationDiagnosticTargetKind Kind,
        string Label,
        Guid? ChapterId = null,
        Guid? EditionId = null,
        Guid? PublicationSectionId = null,
        Guid? DesignedPageId = null,
        Guid? ObjectId = null);
}
