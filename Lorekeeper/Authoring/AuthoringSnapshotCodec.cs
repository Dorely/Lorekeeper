using System.Text.Json;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Authoring;

public sealed record AuthoringCompositionVariantSnapshot(Guid Id, string GeometryKey, string SceneJson);

public sealed record AuthoringCompositionSnapshot(
    Guid Id,
    Guid? ChapterId,
    Guid? PublicationSectionId,
    Guid? EditionId,
    Guid? SourceCompositionId,
    string Name,
    string SemanticManuscriptJson,
    Guid? ActiveAuthoringVariantId,
    IReadOnlyList<AuthoringCompositionVariantSnapshot> Variants);

public sealed record AuthoringManuscriptSnapshot(
    string ManuscriptJson,
    IReadOnlyList<AuthoringCompositionSnapshot> Compositions,
    bool Inherited = false);

public sealed record AuthoringPageCompositionSnapshot(
    Guid Id,
    string Name,
    string SemanticManuscriptJson,
    Guid? ActiveAuthoringVariantId,
    IReadOnlyList<AuthoringCompositionVariantSnapshot> Variants,
    string SelectionJson = "");

public sealed record AuthoringCoreCoverSnapshot(
    string Title,
    string Subtitle,
    string Author,
    string BackgroundColor,
    string SceneJson,
    string SelectionJson = "");

public sealed record AuthoringReleaseCoverSnapshot(
    Guid DesignId,
    bool HasStoredDesign,
    bool InheritsCoreCover,
    Guid? SelectedCoverImageId,
    bool InheritsCoreFront,
    string Title,
    string Subtitle,
    string Author,
    string SpineText,
    SpineReadingDirection SpineReadingDirection,
    string BackgroundColor,
    PublicationBarcodeMode BarcodeMode,
    double ImageCropXPercent,
    double ImageCropYPercent,
    string AcknowledgedTemplateFingerprint,
    string SceneJson,
    string SurfaceScenesJson,
    string SelectionJson = "");

public static class AuthoringSnapshotCodec
{
    private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

    public static async Task<string> CaptureManuscriptAsync(
        AppDbContext db,
        ManuscriptDocument document,
        Guid projectId,
        Guid? chapterId,
        Guid? publicationSectionId,
        Guid? editionId,
        CancellationToken cancellationToken,
        bool inherited = false)
    {
        var normalized = document with { Revision = 0 };
        var ids = normalized.Content
            .Where(block => block.Type == ManuscriptBlockType.DesignedPage && block.PageCompositionId.HasValue)
            .Select(block => block.PageCompositionId!.Value)
            .Distinct()
            .Order()
            .ToList();
        var trackedEntries = db.ChangeTracker.Entries<PageComposition>()
            .Where(entry => entry.State != EntityState.Deleted && entry.Entity.DetachedAt == null
                && ids.Contains(entry.Entity.Id))
            .ToList();
        foreach (var entry in trackedEntries.Where(entry => entry.State != EntityState.Added
                     && !entry.Collection(item => item.Variants).IsLoaded))
            await entry.Collection(item => item.Variants).LoadAsync(cancellationToken);
        var tracked = trackedEntries.Select(entry => entry.Entity).ToList();
        var trackedIds = tracked.Select(item => item.Id).ToHashSet();
        var entities = ids.Count == 0
            ? []
            : await db.PageCompositions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Include(item => item.Variants)
                .Where(item => item.ProjectId == projectId && item.DetachedAt == null
                    && ids.Contains(item.Id) && !trackedIds.Contains(item.Id))
                .OrderBy(item => item.Id)
                .ToListAsync(cancellationToken);
        entities.AddRange(tracked);
        var compositions = entities
                .Select(item => new AuthoringCompositionSnapshot(
                    item.Id,
                    item.ChapterId,
                    item.PublicationSectionId,
                    item.EditionId,
                    item.SourceCompositionId,
                    item.Name,
                    NormalizeSemantic(item.SemanticManuscriptJson, item.Id, item.Revision),
                    item.ActiveAuthoringVariantId,
                    item.Variants.Where(variant => variant.DetachedAt == null)
                        .OrderBy(variant => variant.Id)
                        .Select(variant => new AuthoringCompositionVariantSnapshot(variant.Id, variant.GeometryKey, variant.SceneJson))
                        .ToList()))
                .ToList();
        if (compositions.Count != ids.Count)
            throw new InvalidDataException("A Designed Page referenced by the manuscript could not be captured for history.");
        return JsonSerializer.Serialize(
            new AuthoringManuscriptSnapshot(ManuscriptCodec.Serialize(normalized), compositions, inherited),
            _options);
    }

    public static AuthoringManuscriptSnapshot ReadManuscript(string payload) =>
        JsonSerializer.Deserialize<AuthoringManuscriptSnapshot>(payload, _options)
        ?? throw new InvalidDataException("The authoring-history manuscript snapshot is malformed.");

    public static string DescribeManuscriptAction(string beforePayload, string afterPayload, string documentName)
    {
        var before = ManuscriptCodec.Deserialize(ReadManuscript(beforePayload).ManuscriptJson);
        var after = ManuscriptCodec.Deserialize(ReadManuscript(afterPayload).ManuscriptJson);
        var beforePages = before.Content
            .Where(item => item.Type == ManuscriptBlockType.DesignedPage && item.PageCompositionId.HasValue)
            .Select(item => item.PageCompositionId!.Value)
            .ToList();
        var afterPages = after.Content
            .Where(item => item.Type == ManuscriptBlockType.DesignedPage && item.PageCompositionId.HasValue)
            .Select(item => item.PageCompositionId!.Value)
            .ToList();
        if (afterPages.Except(beforePages).Any())
            return "Insert Designed Page";
        if (beforePages.Except(afterPages).Any())
            return "Delete Designed Page";
        if (!beforePages.SequenceEqual(afterPages))
            return "Move Designed Page";

        var beforeFigures = before.Content
            .Where(item => item.Type == ManuscriptBlockType.Figure)
            .Select(item => JsonSerializer.Serialize(item, _options));
        var afterFigures = after.Content
            .Where(item => item.Type == ManuscriptBlockType.Figure)
            .Select(item => JsonSerializer.Serialize(item, _options));
        if (!beforeFigures.SequenceEqual(afterFigures))
            return "Edit Figure";

        var beforeStructure = before.Content.Select(item => (item.Id, item.Type));
        var afterStructure = after.Content.Select(item => (item.Id, item.Type));
        if (!beforeStructure.SequenceEqual(afterStructure))
            return $"Edit {documentName} structure";
        return !string.Equals(
            ManuscriptCodec.ProjectPlainText(before),
            ManuscriptCodec.ProjectPlainText(after),
            StringComparison.Ordinal)
            ? $"Edit {documentName} text"
            : $"Format {documentName} text";
    }

    public static async Task<string> CaptureCompositionAsync(
        AppDbContext db,
        Guid projectId,
        Guid compositionId,
        string selectionJson,
        CancellationToken cancellationToken)
    {
        var item = await db.PageCompositions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(value => value.Variants)
            .SingleAsync(value => value.ProjectId == projectId && value.Id == compositionId, cancellationToken);
        var snapshot = new AuthoringPageCompositionSnapshot(
            item.Id,
            item.Name,
            NormalizeSemantic(item.SemanticManuscriptJson, item.Id, item.Revision),
            item.ActiveAuthoringVariantId,
            item.Variants.Where(value => value.DetachedAt == null)
                .OrderBy(value => value.Id)
                .Select(value => new AuthoringCompositionVariantSnapshot(value.Id, value.GeometryKey, value.SceneJson))
                .ToList(),
            selectionJson);
        return Serialize(snapshot);
    }

    public static string CaptureComposition(PageComposition item, string selectionJson = "") =>
        Serialize(new AuthoringPageCompositionSnapshot(
            item.Id,
            item.Name,
            NormalizeSemantic(item.SemanticManuscriptJson, item.Id, item.Revision),
            item.ActiveAuthoringVariantId,
            item.Variants.Where(value => value.DetachedAt == null)
                .OrderBy(value => value.Id)
                .Select(value => new AuthoringCompositionVariantSnapshot(value.Id, value.GeometryKey, value.SceneJson))
                .ToList(),
            selectionJson));

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, _options);

    public static T Deserialize<T>(string value) =>
        JsonSerializer.Deserialize<T>(value, _options)
        ?? throw new InvalidDataException("The authoring-history snapshot is malformed.");

    public static IReadOnlySet<(AuthoringHistoryDependencyKind Kind, Guid ResourceId)> FindDependencies(
        params string[] snapshots) =>
        AuthoringDependencyScanner.FindDependencies(snapshots);

    private static string NormalizeSemantic(string json, Guid manuscriptId, long revision)
    {
        var document = ManuscriptCodec.Deserialize(json, manuscriptId, revision) with { Revision = 0 };
        return ManuscriptCodec.Serialize(document);
    }
}
