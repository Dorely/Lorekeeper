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
    Guid? CompositionId = null,
    Guid? ObjectId = null);

public static partial class PublicationDiagnosticText
{
    public static string SanitizeIdentifiers(string message) =>
        IdentifierRegex().Replace(message, "this item");

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
                target?.CompositionId,
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
            var matchingVariants = await db.PageCompositionVariants.AsNoTracking()
                .Include(item => item.Composition).ThenInclude(item => item.Chapter)
                .Include(item => item.Composition).ThenInclude(item => item.PublicationSection)
                .Where(item => item.Composition.ProjectId == projectId
                    && item.DetachedAt == null
                    && item.Composition.DetachedAt == null
                    && (selectedEditionId == null
                        ? item.Composition.EditionId == null
                        : item.Composition.EditionId == selectedEditionId
                            || item.Composition.EditionId == null)
                    && (item.SceneJson.Contains(canonicalCandidate)
                        || item.SceneJson.Contains(compactCandidate)))
                .ToListAsync(cancellationToken);
            var variantMatch = matchingVariants
                .Select(item => (Variant: item, ObjectId: SceneObjectReference(item.SceneJson, candidateId)))
                .Where(item => item.ObjectId is not null)
                .OrderByDescending(item => item.Variant.Composition.EditionId == selectedEditionId)
                .FirstOrDefault();
            if (variantMatch.Variant is not null)
                return CompositionTarget(variantMatch.Variant.Composition, variantMatch.ObjectId, selectedEditionId, chapterOrdinals);

            var composition = await db.PageCompositions.AsNoTracking()
                .Include(item => item.Chapter)
                .Include(item => item.PublicationSection)
                .Where(item => item.ProjectId == projectId
                    && item.Id == candidateId
                    && item.DetachedAt == null
                    && (selectedEditionId == null
                        ? item.EditionId == null
                        : item.EditionId == selectedEditionId || item.EditionId == null))
                .OrderByDescending(item => item.EditionId == selectedEditionId)
                .FirstOrDefaultAsync(cancellationToken);
            if (composition is not null)
                return CompositionTarget(composition, null, selectedEditionId, chapterOrdinals);

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
                return new(PublicationDiagnosticTargetKind.ChapterPage, label, ChapterId: chapter.Id);
            }

            var publicationSection = await db.PublicationSections.AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == candidateId && item.ProjectId == projectId, cancellationToken);
            if (publicationSection is not null)
            {
                return new(
                    PublicationDiagnosticTargetKind.PublicationSectionPage,
                    publicationSection.Title,
                    EditionId: publicationSection.EditionId,
                    PublicationSectionId: publicationSection.Id);
            }
        }

        return null;
    }

    private static ResolvedTarget CompositionTarget(
        PageComposition composition,
        Guid? objectId,
        Guid? selectedEditionId,
        IReadOnlyDictionary<Guid, int> chapterOrdinals)
    {
        var pageLabel = string.IsNullOrWhiteSpace(composition.Name)
            ? "Designed page"
            : composition.Name.Trim();
        if (composition.PublicationSection is { } section)
        {
            return new(
                PublicationDiagnosticTargetKind.PublicationSectionPage,
                $"{section.Title} · {pageLabel}",
                EditionId: section.EditionId,
                PublicationSectionId: section.Id,
                CompositionId: composition.Id,
                ObjectId: objectId);
        }

        if (composition.Chapter is { } chapter)
        {
            var chapterLabel = chapterOrdinals.TryGetValue(chapter.Id, out var ordinal)
                ? $"Chapter {ordinal}: {chapter.Title}"
                : chapter.Title;
            return new(
                PublicationDiagnosticTargetKind.ChapterPage,
                $"{chapterLabel} · {pageLabel}",
                ChapterId: chapter.Id,
                EditionId: composition.EditionId,
                CompositionId: composition.Id,
                ObjectId: objectId);
        }

        return new(
            PublicationDiagnosticTargetKind.None,
            pageLabel,
            EditionId: selectedEditionId ?? composition.EditionId,
            CompositionId: composition.Id,
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
        if (string.Equals(code, "PRESS_COMPOSITION_OBJECT_CLIPPED", StringComparison.OrdinalIgnoreCase))
            return "An item extends beyond the canvas and will be clipped to the page.";
        if (string.Equals(code, "PRESS_ALT_DECISION_REQUIRED", StringComparison.OrdinalIgnoreCase))
            return "An image requires alternative text or an explicit decorative decision before publishing.";
        if (string.Equals(code, "PRESS_COMPOSITION_RANGE_INVALID", StringComparison.OrdinalIgnoreCase))
            return "A text frame points to an invalid portion of its page content. Open the page to repair or replace that text.";

        var sanitized = ObjectReferenceRegex().Replace(message, "This item ");
        sanitized = PublicationDiagnosticText.SanitizeIdentifiers(sanitized);
        return sanitized.Trim();
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
        };

    [GeneratedRegex("(?i)(?:Composition\\s+)?(?:object|frame)\\s+['\"]?(?:[0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})['\"]?\\s*")]
    private static partial Regex ObjectReferenceRegex();

    [GeneratedRegex("(?i)(?:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|[0-9a-f]{32})")]
    private static partial Regex IdentifierRegex();

    private sealed record ResolvedTarget(
        PublicationDiagnosticTargetKind Kind,
        string Label,
        Guid? ChapterId = null,
        Guid? EditionId = null,
        Guid? PublicationSectionId = null,
        Guid? CompositionId = null,
        Guid? ObjectId = null);
}
