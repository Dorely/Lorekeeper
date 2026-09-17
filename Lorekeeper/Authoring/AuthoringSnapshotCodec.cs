using System.Text.Json;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Authoring;

public sealed record AuthoringDesignedPageVariantSnapshot(Guid Id, string GeometryKey, string SceneJson);

public sealed record AuthoringManuscriptSnapshot(
    string ManuscriptJson,
    bool Inherited = false);

public sealed record AuthoringDesignedPageContentSnapshot(
    Guid PageId,
    Guid ContentId,
    string Name,
    string SemanticManuscriptJson,
    Guid? ActiveVariantId,
    IReadOnlyList<AuthoringDesignedPageVariantSnapshot> Variants,
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

    public static Task<string> CaptureManuscriptAsync(
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
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(JsonSerializer.Serialize(
            new AuthoringManuscriptSnapshot(ManuscriptCodec.Serialize(normalized), inherited),
            _options));
    }

    public static AuthoringManuscriptSnapshot ReadManuscript(string payload) =>
        JsonSerializer.Deserialize<AuthoringManuscriptSnapshot>(payload, _options)
        ?? throw new InvalidDataException("The authoring-history manuscript snapshot is malformed.");

    public static string DescribeManuscriptAction(string beforePayload, string afterPayload, string documentName)
    {
        var before = ManuscriptCodec.Deserialize(ReadManuscript(beforePayload).ManuscriptJson);
        var after = ManuscriptCodec.Deserialize(ReadManuscript(afterPayload).ManuscriptJson);
        var beforePages = before.Content
            .Where(item => item.Type == ManuscriptBlockType.DesignedPage && item.DesignedPageId.HasValue)
            .Select(item => item.DesignedPageId!.Value)
            .ToList();
        var afterPages = after.Content
            .Where(item => item.Type == ManuscriptBlockType.DesignedPage && item.DesignedPageId.HasValue)
            .Select(item => item.DesignedPageId!.Value)
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

    public static async Task<string> CaptureDesignedPageAsync(
        AppDbContext db,
        Guid projectId,
        Guid contentId,
        string selectionJson,
        CancellationToken cancellationToken)
    {
        var item = await db.DesignedPageContents
            .AsNoTracking()
            .Include(value => value.Page)
            .Include(value => value.Variants)
            .SingleAsync(value => value.ProjectId == projectId && value.Id == contentId, cancellationToken);
        var snapshot = new AuthoringDesignedPageContentSnapshot(
            item.DesignedPageId,
            item.Id,
            item.Page.Name,
            NormalizeSemantic(item.SemanticManuscriptJson, item.Id, item.Revision),
            item.ActiveVariantId,
            item.Variants
                .OrderBy(value => value.Id)
                .Select(value => new AuthoringDesignedPageVariantSnapshot(value.Id, value.GeometryKey, value.SceneJson))
                .ToList(),
            selectionJson);
        return Serialize(snapshot);
    }

    public static string CaptureDesignedPage(DesignedPageContent item, string selectionJson = "") =>
        Serialize(new AuthoringDesignedPageContentSnapshot(
            item.DesignedPageId,
            item.Id,
            item.Page.Name,
            NormalizeSemantic(item.SemanticManuscriptJson, item.Id, item.Revision),
            item.ActiveVariantId,
            item.Variants
                .OrderBy(value => value.Id)
                .Select(value => new AuthoringDesignedPageVariantSnapshot(value.Id, value.GeometryKey, value.SceneJson))
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
