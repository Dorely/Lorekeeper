using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lorekeeper.Authoring;
using Lorekeeper.Composition;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace Lorekeeper.Publish;

public sealed record PublicationCoverDiagnostic(
    string Severity,
    string Code,
    string Message,
    Guid? ObjectId = null);

public sealed record PublicationCoverCanvasPreviewRequest(
    Guid CoverId,
    long Revision,
    string SurfaceLabel,
    CompositionScene Scene,
    IReadOnlyDictionary<string, string> TextBindings);

public sealed record PublicationCoverDesignView(
    Guid Id,
    Guid EditionId,
    string Title,
    string Subtitle,
    string Author,
    string SpineText,
    string Description,
    string BackgroundColor,
    PublicationBarcodeMode BarcodeMode,
    double ImageCropXPercent,
    double ImageCropYPercent,
    string CompositionSceneJson,
    long Revision,
    PublicationCoverTemplate Template,
    IReadOnlyList<string> Diagnostics)
{
    public long? CoreBookRevision { get; init; }
    public IReadOnlyDictionary<string, string> SurfaceScenes { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<PublicationCoverDiagnostic> DiagnosticDetails { get; init; } = [];
    public SpineReadingDirection SpineReadingDirection { get; init; } = SpineReadingDirection.TopToBottom;
    public IReadOnlyList<CoverRegionDescriptor> Regions { get; init; } = [];
    public string SurfaceRole { get; init; } = "front";
}

public sealed record PublicationCoverTemplate(
    int PageCount,
    double TrimWidthInches,
    double TrimHeightInches,
    double BleedInches,
    double SpineWidthInches,
    double FullWidthInches,
    double FullHeightInches,
    double SafetyInches,
    double BarcodeWidthInches,
    double BarcodeHeightInches,
    string Fingerprint,
    bool IsAcknowledged)
{
    public double BarcodeInsetInches { get; init; } = 0.25;
    public double BackRegionWidthInches { get; init; }
    public double FrontRegionWidthInches { get; init; }
    public double CoverRegionYInches { get; init; }
    public double CoverRegionHeightInches { get; init; }
}

public sealed record CoverRegionDescriptor(
    CompositionRegionConstraint Role,
    CompositionBounds Bounds,
    double WidthInches,
    double HeightInches,
    double AspectRatio,
    double SafeInsetInches,
    IReadOnlyList<string> Guides,
    string GeometryFingerprint,
    bool ParticipatesInOutput,
    string OutputRole);

public sealed record PublicationCoverDesignUpdate(
    string Title,
    string Subtitle,
    string Author,
    string SpineText,
    string BackgroundColor,
    PublicationBarcodeMode BarcodeMode,
    double ImageCropXPercent,
    double ImageCropYPercent,
    long ExpectedRevision,
    bool AcknowledgeTemplate,
    SpineReadingDirection? SpineReadingDirection = null);

public interface IPublicationCoverService
{
    Task<PublicationCoverDesignView> GetAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken = default);
    Task<PublicationCoverDesignView> GetSurfaceAsync(Guid projectId, Guid editionId, string surfaceRole, CancellationToken cancellationToken = default);
    Task<PublicationCoverDesignView> UpdateAsync(Guid projectId, Guid editionId, PublicationCoverDesignUpdate update, CancellationToken cancellationToken = default);
    Task<PublicationCoverDesignView> SaveWorkspaceAsync(Guid projectId, Guid editionId, PublicationCoverDesignUpdate update, CompositionScene scene, CancellationToken cancellationToken = default);
    Task<PublicationCoverDesignView> SaveSurfaceWorkspaceAsync(Guid projectId, Guid editionId, string surfaceRole, PublicationCoverDesignUpdate update, CompositionScene scene, CancellationToken cancellationToken = default);
    Task<PublicationCoverDesignView> UpdateSceneAsync(Guid projectId, Guid editionId, string sceneJson, long expectedRevision, CancellationToken cancellationToken = default);
    Task<PublicationCoverDesignView> PatchElementAsync(Guid projectId, Guid editionId, long expectedRevision, string targetKind, Guid targetId, CompositionElementPatch patch, CancellationToken cancellationToken = default);
    Task<PublicationCoverDesignView> PatchSurfaceElementAsync(Guid projectId, Guid editionId, string surfaceRole, long expectedRevision, string targetKind, Guid targetId, CompositionElementPatch patch, CancellationToken cancellationToken = default);
    Task<CompositionMutationStage> StageSceneAsync(Guid projectId, Guid conversationId, Guid editionId, long expectedRevision, CompositionScene scene, string? surfaceRole = null, CancellationToken cancellationToken = default);
    Task<PublicationCoverDesignView> ApplySceneStageAsync(Guid projectId, Guid conversationId, Guid stageId, long expectedRevision, CancellationToken cancellationToken = default);
    Task<PublicationCoverDesignView> CustomizeFromCoreAsync(Guid projectId, Guid editionId, long expectedEditionRevision, CancellationToken cancellationToken = default);
    Task UseCoreAsync(Guid projectId, Guid editionId, long expectedEditionRevision, CancellationToken cancellationToken = default);
}

public sealed class PublicationCoverService(
    IAppDatabaseOperationFactory database,
    IPublicationEditionService editions,
    IPublicationEffectiveConfigurationResolver effectiveConfigurations,
    IPublicationPressRuntime pressRuntime,
    IPrintGeometryService printGeometry,
    IPrintArtifactProfileRegistry printArtifactProfiles,
    IAuthoringGenerationService? authoringGenerations = null,
    IAuthoringMutationContextAccessor? authoringMutationContext = null) : IPublicationCoverService
{
    private const string CoverSceneStagePrefix = "cover-scene:";

    public PublicationCoverService(
        IAppDatabaseOperationFactory database,
        IPublicationEditionService editions,
        IPublicationPressRuntime pressRuntime)
        : this(database, editions, new PublicationEffectiveConfigurationResolver(database), pressRuntime,
            new PrintGeometryService(new PrintArtifactProfileRegistry()), new PrintArtifactProfileRegistry(), null, null)
    {
    }

    public async Task<PublicationCoverDesignView> GetAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var edition = await GetEffectiveEditionAsync(projectId, editionId, cancellationToken);
        var design = await db.PublicationCoverDesigns.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.EditionId == editionId, cancellationToken)
            ?? await DefaultAsync(projectId, edition, lockCoreLayers: true, cancellationToken);
        return await ViewAsync(edition, design, cancellationToken);
    }

    public async Task<PublicationCoverDesignView> GetSurfaceAsync(
        Guid projectId,
        Guid editionId,
        string surfaceRole,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var edition = await GetEffectiveEditionAsync(projectId, editionId, cancellationToken);
        var design = await db.PublicationCoverDesigns.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.EditionId == editionId, cancellationToken)
            ?? await DefaultAsync(projectId, edition, lockCoreLayers: true, cancellationToken);
        return await ViewAsync(edition, design, cancellationToken, surfaceRole);
    }

    public async Task<PublicationCoverDesignView> CustomizeFromCoreAsync(
        Guid projectId,
        Guid editionId,
        long expectedEditionRevision,
        CancellationToken cancellationToken = default)
    {
        await RequireCurrentInteriorPaginationAsync(projectId, editionId, cancellationToken);
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var stored = await GetEditionAsync(projectId, editionId, cancellationToken, tracked: true);
        PublicationEditionService.EnsureDraft(stored);
        if (stored.Revision != expectedEditionRevision)
            throw new DbUpdateConcurrencyException("The publication release changed; reread it before customizing the cover.");
        var effective = await GetEffectiveEditionAsync(projectId, editionId, cancellationToken);
        var design = await db.PublicationCoverDesigns.SingleOrDefaultAsync(
            item => item.EditionId == editionId,
            cancellationToken);
        if (!stored.InheritsCoreCover && design is not null)
            return await ViewAsync(effective, design, cancellationToken);

        var beforeDesign = design
            ?? await DefaultAsync(projectId, effective, lockCoreLayers: true, cancellationToken);
        var beforeHistory = CaptureReleaseCover(beforeDesign, stored, design is not null);
        if (design is null)
        {
            design = await DefaultAsync(projectId, effective, lockCoreLayers: false, cancellationToken);
            db.PublicationCoverDesigns.Add(design);
        }
        stored.InheritsCoreCover = false;
        stored.Revision = checked(stored.Revision + 1);
        stored.UpdatedAt = DateTime.UtcNow;
        var afterHistory = CaptureReleaseCover(design, stored, hasStoredDesign: true);
        await db.SaveChangesAsync(cancellationToken);
        await RecordReleaseCoverMutationAsync(
            projectId, editionId, beforeHistory, afterHistory, "Customize release cover");
        return await ViewAsync(await GetEffectiveEditionAsync(projectId, editionId, cancellationToken), design, cancellationToken);
    }

    public async Task UseCoreAsync(
        Guid projectId,
        Guid editionId,
        long expectedEditionRevision,
        CancellationToken cancellationToken = default)
    {
        await RequireCurrentInteriorPaginationAsync(projectId, editionId, cancellationToken);
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var stored = await GetEditionAsync(projectId, editionId, cancellationToken, tracked: true);
        PublicationEditionService.EnsureDraft(stored);
        if (stored.Revision != expectedEditionRevision)
            throw new DbUpdateConcurrencyException("The publication release changed; reread it before restoring the Core cover.");
        var design = await db.PublicationCoverDesigns.SingleOrDefaultAsync(
            item => item.EditionId == editionId,
            cancellationToken);
        if (stored.InheritsCoreCover && design is null)
            return;

        var effective = await GetEffectiveEditionAsync(projectId, editionId, cancellationToken);
        var beforeDesign = design
            ?? await DefaultAsync(projectId, effective, lockCoreLayers: true, cancellationToken);
        var beforeHistory = CaptureReleaseCover(beforeDesign, stored, design is not null);
        if (design is not null)
            db.PublicationCoverDesigns.Remove(design);
        stored.InheritsCoreCover = true;
        stored.SelectedCoverImageId = null;
        stored.Revision = checked(stored.Revision + 1);
        stored.UpdatedAt = DateTime.UtcNow;
        var inherited = await DefaultAsync(
            projectId,
            await GetEffectiveEditionAsync(projectId, editionId, cancellationToken),
            lockCoreLayers: true,
            cancellationToken);
        var afterHistory = CaptureReleaseCover(inherited, stored, hasStoredDesign: false);
        await db.SaveChangesAsync(cancellationToken);
        await RecordReleaseCoverMutationAsync(
            projectId, editionId, beforeHistory, afterHistory, "Use Core cover");
    }

    public async Task<PublicationCoverDesignView> UpdateAsync(
        Guid projectId,
        Guid editionId,
        PublicationCoverDesignUpdate update,
        CancellationToken cancellationToken = default)
    {
        await RequireCurrentInteriorPaginationAsync(projectId, editionId, cancellationToken);
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        Validate(update);
        var storedEdition = await GetEditionAsync(projectId, editionId, cancellationToken, tracked: true);
        var edition = await GetEffectiveEditionAsync(projectId, editionId, cancellationToken);
        ValidateProduct(update, edition);
        PublicationEditionService.EnsureDraft(storedEdition);
        var design = await db.PublicationCoverDesigns
            .FirstOrDefaultAsync(candidate => candidate.EditionId == editionId, cancellationToken);
        var hadStoredDesign = design is not null;
        var beforeDesign = design
            ?? await DefaultAsync(projectId, edition, lockCoreLayers: true, cancellationToken);
        var beforeHistory = CaptureReleaseCover(beforeDesign, storedEdition, hadStoredDesign);
        if (design is null)
        {
            if (update.ExpectedRevision != 0)
                throw new DbUpdateConcurrencyException("The cover design changed.");
            design = await DefaultAsync(projectId, edition, lockCoreLayers: false, cancellationToken);
            db.PublicationCoverDesigns.Add(design);
            storedEdition.InheritsCoreCover = false;
        }
        else if (design.Revision != update.ExpectedRevision)
        {
            throw new DbUpdateConcurrencyException("The cover design changed.");
        }
        var template = await TemplateAsync(edition, design, cancellationToken);
        design.Title = update.Title.Trim();
        design.Subtitle = update.Subtitle.Trim();
        design.Author = update.Author.Trim();
        design.SpineText = update.SpineText.Trim();
        design.BackgroundColor = update.BackgroundColor.Trim().ToLowerInvariant();
        design.BarcodeMode = edition.Vendor == PublicationVendor.BarnesAndNoblePress
            ? PublicationBarcodeMode.VendorOverlay
            : edition.Vendor == PublicationVendor.Lulu
                ? edition.PrintProjectUse == PrintProjectUse.PersonalUse
                    ? PublicationBarcodeMode.None
                    : PublicationBarcodeMode.LorekeeperBarcode
                : update.BarcodeMode;
        if (update.SpineReadingDirection is { } spineReadingDirection)
        {
            design.SpineReadingDirection = spineReadingDirection;
            var storedScene = JsonSerializer.Deserialize<CompositionScene>(design.CompositionSceneJson, ManuscriptCodec.JsonOptions);
            if (storedScene is not null)
                design.CompositionSceneJson = JsonSerializer.Serialize(
                    CoverCompositionFactory.ApplySpineReadingDirection(storedScene, spineReadingDirection),
                    ManuscriptCodec.JsonOptions);
        }
        design.ImageCropXPercent = update.ImageCropXPercent;
        design.ImageCropYPercent = update.ImageCropYPercent;
        design.AcknowledgedTemplateFingerprint = update.AcknowledgeTemplate
            ? template.Fingerprint
            : design.AcknowledgedTemplateFingerprint;
        design.Revision++;
        design.UpdatedAt = DateTime.UtcNow;
        var afterHistory = CaptureReleaseCover(design, storedEdition, hasStoredDesign: true);
        if (string.Equals(beforeHistory, afterHistory, StringComparison.Ordinal))
        {
            db.ChangeTracker.Clear();
            return await ViewAsync(edition, beforeDesign, cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
        await RecordReleaseCoverMutationAsync(
            projectId, edition.Id, beforeHistory, afterHistory, "Edit release cover");
        return await ViewAsync(edition, design, cancellationToken);
    }

    public async Task<PublicationCoverDesignView> PatchElementAsync(
        Guid projectId,
        Guid editionId,
        long expectedRevision,
        string targetKind,
        Guid targetId,
        CompositionElementPatch patch,
        CancellationToken cancellationToken = default)
    {
        var cover = await GetAsync(projectId, editionId, cancellationToken);
        if (cover.Revision != expectedRevision)
            throw new DbUpdateConcurrencyException("The cover design changed.");
        var scene = JsonSerializer.Deserialize<CompositionScene>(cover.CompositionSceneJson, ManuscriptCodec.JsonOptions)
            ?? throw new InvalidDataException("The cover scene is empty.");
        var patched = DesignedPageService.ApplyElementPatch(scene, targetKind, targetId, patch);
        return await UpdateSceneAsync(
            projectId,
            editionId,
            JsonSerializer.Serialize(patched, ManuscriptCodec.JsonOptions),
            expectedRevision,
            cancellationToken);
    }

    public async Task<PublicationCoverDesignView> PatchSurfaceElementAsync(
        Guid projectId,
        Guid editionId,
        string surfaceRole,
        long expectedRevision,
        string targetKind,
        Guid targetId,
        CompositionElementPatch patch,
        CancellationToken cancellationToken = default)
    {
        var cover = await GetSurfaceAsync(projectId, editionId, surfaceRole, cancellationToken);
        if (cover.Revision != expectedRevision)
            throw new DbUpdateConcurrencyException("The cover design changed.");
        var scene = JsonSerializer.Deserialize<CompositionScene>(cover.CompositionSceneJson, ManuscriptCodec.JsonOptions)
            ?? throw new InvalidDataException("The cover surface scene is empty.");
        var patched = DesignedPageService.ApplyElementPatch(scene, targetKind, targetId, patch);
        return await SaveSurfaceWorkspaceAsync(
            projectId,
            editionId,
            surfaceRole,
            new PublicationCoverDesignUpdate(
                cover.Title, cover.Subtitle, cover.Author, cover.SpineText,
                cover.BackgroundColor, cover.BarcodeMode, cover.ImageCropXPercent, cover.ImageCropYPercent,
                expectedRevision, true),
            patched,
            cancellationToken);
    }

    public Task<PublicationCoverDesignView> SaveWorkspaceAsync(
        Guid projectId,
        Guid editionId,
        PublicationCoverDesignUpdate update,
        CompositionScene scene,
        CancellationToken cancellationToken = default) =>
        SaveSurfaceWorkspaceAsync(projectId, editionId, string.Empty, update, scene, cancellationToken);

    public async Task<PublicationCoverDesignView> SaveSurfaceWorkspaceAsync(
        Guid projectId,
        Guid editionId,
        string surfaceRole,
        PublicationCoverDesignUpdate update,
        CompositionScene scene,
        CancellationToken cancellationToken = default)
    {
        await RequireCurrentInteriorPaginationAsync(projectId, editionId, cancellationToken);
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        scene = CoverCompositionFactory.KeepArtworkBehindCopy(scene);
        ValidateAuthoringUpdate(update);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var storedEdition = await GetEditionAsync(projectId, editionId, cancellationToken, tracked: true);
        var edition = await GetEffectiveEditionAsync(projectId, editionId, cancellationToken);
        ValidateProduct(update, edition);
        PublicationEditionService.EnsureDraft(storedEdition);
        var design = await db.PublicationCoverDesigns.FirstOrDefaultAsync(
            candidate => candidate.EditionId == editionId,
            cancellationToken);
        var hadStoredDesign = design is not null;
        var beforeDesign = design
            ?? await DefaultAsync(projectId, edition, lockCoreLayers: true, cancellationToken);
        var beforeHistory = CaptureReleaseCover(beforeDesign, storedEdition, hadStoredDesign);
        if (design is null)
        {
            if (update.ExpectedRevision != 0)
                throw new DbUpdateConcurrencyException("The cover design changed.");
            design = await DefaultAsync(projectId, edition, lockCoreLayers: false, cancellationToken);
            db.PublicationCoverDesigns.Add(design);
            storedEdition.InheritsCoreCover = false;
        }
        else if (design.Revision != update.ExpectedRevision)
        {
            throw new DbUpdateConcurrencyException("The cover design changed.");
        }
        surfaceRole = NormalizeSurfaceRole(edition, surfaceRole);
        var template = await TemplateAsync(edition, design, cancellationToken, surfaceRole);
        scene = ReflowToCurrentGeometry(edition, design, template, scene, out _, surfaceRole);
        scene = CoverCompositionFactory.ApplySpineReadingDirection(
            scene,
            update.SpineReadingDirection ?? design.SpineReadingDirection);
        ValidateAuthoringScene(scene, edition, template);
        await ValidateSceneAssetsAsync(projectId, scene, cancellationToken);
        design.Title = update.Title.Trim();
        design.Subtitle = update.Subtitle.Trim();
        design.Author = update.Author.Trim();
        design.SpineText = update.SpineText.Trim();
        design.BackgroundColor = update.BackgroundColor.Trim().ToLowerInvariant();
        design.BarcodeMode = edition.Vendor == PublicationVendor.BarnesAndNoblePress
            ? PublicationBarcodeMode.VendorOverlay
            : edition.Vendor == PublicationVendor.Lulu
                ? edition.PrintProjectUse == PrintProjectUse.PersonalUse
                    ? PublicationBarcodeMode.None
                    : PublicationBarcodeMode.LorekeeperBarcode
                : update.BarcodeMode;
        if (update.SpineReadingDirection is { } spineReadingDirection)
            design.SpineReadingDirection = spineReadingDirection;
        design.ImageCropXPercent = update.ImageCropXPercent;
        design.ImageCropYPercent = update.ImageCropYPercent;
        design.AcknowledgedTemplateFingerprint = update.AcknowledgeTemplate
            ? template.Fingerprint
            : design.AcknowledgedTemplateFingerprint;
        var sceneJson = System.Text.Json.JsonSerializer.Serialize(scene, ManuscriptCodec.JsonOptions);
        var surfaceScenes = ReadSurfaceScenes(design.SurfaceScenesJson).ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        surfaceScenes[surfaceRole] = sceneJson;
        design.SurfaceScenesJson = JsonSerializer.Serialize(surfaceScenes, ManuscriptCodec.JsonOptions);
        if (surfaceRole == NormalizeSurfaceRole(edition, string.Empty))
            design.CompositionSceneJson = sceneJson;
        design.Revision = checked(design.Revision + 1);
        design.UpdatedAt = DateTime.UtcNow;
        storedEdition.SelectedCoverImageId = CompositionSceneResolver.Flatten(scene)
            .Where(item => item.Visible && item.Kind == CompositionObjectKind.Image && item.ImageId is not null)
            .OrderBy(item => item.ZIndex)
            .Select(item => item.ImageId)
            .FirstOrDefault();
        storedEdition.Revision = checked(storedEdition.Revision + 1);
        storedEdition.UpdatedAt = DateTime.UtcNow;
        var afterHistory = CaptureReleaseCover(design, storedEdition, hasStoredDesign: true);
        if (string.Equals(beforeHistory, afterHistory, StringComparison.Ordinal))
        {
            db.ChangeTracker.Clear();
            return await ViewAsync(edition, beforeDesign, cancellationToken, surfaceRole);
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await RecordReleaseCoverMutationAsync(
            projectId, edition.Id, beforeHistory, afterHistory, $"Edit {SurfaceLabel(surfaceRole)} cover");
        return await ViewAsync(await GetEffectiveEditionAsync(projectId, editionId, cancellationToken), design, cancellationToken, surfaceRole);
    }

    private static string CaptureReleaseCover(
        PublicationCoverDesign design,
        PublicationEdition edition,
        bool hasStoredDesign) =>
        AuthoringSnapshotCodec.Serialize(new AuthoringReleaseCoverSnapshot(
            hasStoredDesign ? design.Id : Guid.Empty,
            hasStoredDesign,
            edition.InheritsCoreCover,
            edition.SelectedCoverImageId,
            design.InheritsCoreFront,
            design.Title,
            design.Subtitle,
            design.Author,
            design.SpineText,
            design.SpineReadingDirection,
            design.BackgroundColor,
            design.BarcodeMode,
            design.ImageCropXPercent,
            design.ImageCropYPercent,
            design.AcknowledgedTemplateFingerprint,
            design.CompositionSceneJson,
            design.SurfaceScenesJson));

    private async Task RecordReleaseCoverMutationAsync(
        Guid projectId,
        Guid editionId,
        string beforeHistory,
        string afterHistory,
        string actionLabel)
    {
        if (authoringGenerations is null
            || authoringMutationContext?.IsHistorySuppressed == true
            || string.Equals(beforeHistory, afterHistory, StringComparison.Ordinal))
            return;
        var coverId = (await GetAsync(projectId, editionId, CancellationToken.None)).Id;
        await authoringGenerations.InvalidateAsync(
            projectId,
            [$"release:{editionId:D}:cover:{coverId:D}"],
            CancellationToken.None);
    }

    private static string SurfaceLabel(string surfaceRole) =>
        string.IsNullOrWhiteSpace(surfaceRole) ? "release" : surfaceRole.Replace('-', ' ');

    public async Task<PublicationCoverDesignView> UpdateSceneAsync(
        Guid projectId,
        Guid editionId,
        string sceneJson,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await RequireCurrentInteriorPaginationAsync(projectId, editionId, cancellationToken);
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var scene = System.Text.Json.JsonSerializer.Deserialize<CompositionScene>(sceneJson, ManuscriptCodec.JsonOptions)
            ?? throw new InvalidDataException("Cover composition is empty.");
        scene = CoverCompositionFactory.KeepArtworkBehindCopy(scene);
        var storedEdition = await GetEditionAsync(projectId, editionId, cancellationToken, tracked: true);
        var edition = await GetEffectiveEditionAsync(projectId, editionId, cancellationToken);
        PublicationEditionService.EnsureDraft(storedEdition);
        var design = await db.PublicationCoverDesigns.FirstOrDefaultAsync(
            item => item.EditionId == editionId, cancellationToken);
        var hadStoredDesign = design is not null;
        var beforeDesign = design
            ?? await DefaultAsync(projectId, edition, lockCoreLayers: true, cancellationToken);
        var beforeHistory = CaptureReleaseCover(beforeDesign, storedEdition, hadStoredDesign);
        design ??= await DefaultAsync(projectId, edition, lockCoreLayers: false, cancellationToken);
        if (design.Revision != expectedRevision)
            throw new DbUpdateConcurrencyException("The cover composition changed.");
        var template = await TemplateAsync(edition, design, cancellationToken);
        scene = ReflowToCurrentGeometry(edition, design, template, scene, out _);
        ValidateAuthoringScene(scene, edition, template);
        await ValidateSceneAssetsAsync(projectId, scene, cancellationToken);
        if (db.Entry(design).State == EntityState.Detached)
        {
            db.PublicationCoverDesigns.Add(design);
            storedEdition.InheritsCoreCover = false;
        }
        design.CompositionSceneJson = System.Text.Json.JsonSerializer.Serialize(scene, ManuscriptCodec.JsonOptions);
        design.Revision = checked(design.Revision + 1);
        design.UpdatedAt = DateTime.UtcNow;
        storedEdition.SelectedCoverImageId = CompositionSceneResolver.Flatten(scene)
            .Where(item => item.Visible && item.Kind == CompositionObjectKind.Image && item.ImageId is not null)
            .OrderBy(item => item.ZIndex)
            .Select(item => item.ImageId)
            .FirstOrDefault();
        storedEdition.Revision = checked(storedEdition.Revision + 1);
        storedEdition.UpdatedAt = DateTime.UtcNow;
        var afterHistory = CaptureReleaseCover(design, storedEdition, hasStoredDesign: true);
        if (string.Equals(beforeHistory, afterHistory, StringComparison.Ordinal))
        {
            db.ChangeTracker.Clear();
            return await ViewAsync(edition, beforeDesign, cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
        await RecordReleaseCoverMutationAsync(
            projectId, editionId, beforeHistory, afterHistory, "Edit release cover");
        return await ViewAsync(await GetEffectiveEditionAsync(projectId, editionId, cancellationToken), design, cancellationToken);
    }

    public async Task<CompositionMutationStage> StageSceneAsync(
        Guid projectId,
        Guid conversationId,
        Guid editionId,
        long expectedRevision,
        CompositionScene scene,
        string? surfaceRole = null,
        CancellationToken cancellationToken = default)
    {
        await RequireCurrentInteriorPaginationAsync(projectId, editionId, cancellationToken);
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        scene = CoverCompositionFactory.KeepArtworkBehindCopy(scene);
        if (conversationId == Guid.Empty)
            throw new ArgumentException("A conversation is required for staged cover changes.", nameof(conversationId));
        await db.CompositionMutationStages
            .Where(item => item.ProjectId == projectId
                && (item.ExpiresAt <= DateTime.UtcNow || item.AppliedAt != null))
            .ExecuteDeleteAsync(cancellationToken);
        var edition = await GetEffectiveEditionAsync(projectId, editionId, cancellationToken);
        surfaceRole = NormalizeSurfaceRole(edition, surfaceRole);
        var design = await db.PublicationCoverDesigns.AsNoTracking()
            .FirstOrDefaultAsync(item => item.EditionId == editionId, cancellationToken)
            ?? await DefaultAsync(projectId, edition, lockCoreLayers: false, cancellationToken);
        if (design.Revision != expectedRevision)
            throw new DbUpdateConcurrencyException("The cover composition changed.");
        var template = await TemplateAsync(edition, design, cancellationToken, surfaceRole);
        scene = ReflowToCurrentGeometry(edition, design, template, scene, out _, surfaceRole);
        ValidateAuthoringScene(scene, edition, template);
        await ValidateSceneAssetsAsync(projectId, scene, cancellationToken);
        var payload = System.Text.Json.JsonSerializer.Serialize(scene, ManuscriptCodec.JsonOptions);
        var stage = new CompositionMutationStage
        {
            ProjectId = projectId,
            ConversationId = conversationId,
            TargetKind = CoverSceneStageTarget(surfaceRole),
            TargetId = editionId,
            ExpectedRevision = expectedRevision,
            OperationsJson = payload,
            PayloadSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload))),
            ExpiresAt = DateTime.UtcNow.AddMinutes(30),
        };
        db.CompositionMutationStages.Add(stage);
        await db.SaveChangesAsync(cancellationToken);
        return stage;
    }

    public async Task<PublicationCoverDesignView> ApplySceneStageAsync(
        Guid projectId,
        Guid conversationId,
        Guid stageId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        Guid editionId;
        await using (var read = await database.OpenReadAsync(cancellationToken))
        {
            editionId = await read.Db.CompositionMutationStages.AsNoTracking()
                .Where(item => item.Id == stageId
                    && item.ProjectId == projectId
                    && item.ConversationId == conversationId
                    && (item.TargetKind == "cover-scene" || item.TargetKind.StartsWith(CoverSceneStagePrefix)))
                .Select(item => item.TargetId)
                .SingleOrDefaultAsync(cancellationToken);
        }
        if (editionId == Guid.Empty)
            throw new KeyNotFoundException("Cover stage was not found for this conversation.");
        await RequireCurrentInteriorPaginationAsync(projectId, editionId, cancellationToken);
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var stage = await db.CompositionMutationStages.SingleOrDefaultAsync(item =>
            item.Id == stageId
            && item.ProjectId == projectId
            && item.ConversationId == conversationId
            && (item.TargetKind == "cover-scene" || item.TargetKind.StartsWith(CoverSceneStagePrefix)), cancellationToken)
            ?? throw new KeyNotFoundException("Cover stage was not found for this conversation.");
        if (stage.AppliedAt is not null)
            throw new InvalidOperationException("Cover composition stages are non-replayable.");
        if (stage.ExpiresAt <= DateTime.UtcNow)
            throw new InvalidOperationException("The cover composition stage has expired; submit it again.");
        if (stage.ExpectedRevision != expectedRevision)
            throw new DbUpdateConcurrencyException("The staged cover revision does not match the requested revision.");
        var payloadHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stage.OperationsJson)));
        if (!string.Equals(payloadHash, stage.PayloadSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The staged cover composition failed its integrity check.");
        var scene = System.Text.Json.JsonSerializer.Deserialize<CompositionScene>(stage.OperationsJson, ManuscriptCodec.JsonOptions)
            ?? throw new InvalidDataException("The staged cover composition is empty.");
        scene = CoverCompositionFactory.KeepArtworkBehindCopy(scene);
        var storedEdition = await GetEditionAsync(projectId, stage.TargetId, cancellationToken, tracked: true);
        var edition = await GetEffectiveEditionAsync(projectId, stage.TargetId, cancellationToken);
        var surfaceRole = NormalizeSurfaceRole(edition, CoverSceneStageSurfaceRole(stage.TargetKind));
        PublicationEditionService.EnsureDraft(storedEdition);
        var design = await db.PublicationCoverDesigns.FirstOrDefaultAsync(
            item => item.EditionId == edition.Id, cancellationToken);
        var hadStoredDesign = design is not null;
        var beforeDesign = design
            ?? await DefaultAsync(projectId, edition, lockCoreLayers: true, cancellationToken);
        var beforeHistory = CaptureReleaseCover(beforeDesign, storedEdition, hadStoredDesign);
        design ??= await DefaultAsync(projectId, edition, lockCoreLayers: false, cancellationToken);
        if (design.Revision != expectedRevision)
            throw new DbUpdateConcurrencyException("The cover composition changed after it was staged.");
        var template = await TemplateAsync(edition, design, cancellationToken, surfaceRole);
        scene = ReflowToCurrentGeometry(edition, design, template, scene, out _, surfaceRole);
        ValidateAuthoringScene(scene, edition, template);
        await ValidateSceneAssetsAsync(projectId, scene, cancellationToken);
        if (db.Entry(design).State == EntityState.Detached)
        {
            db.PublicationCoverDesigns.Add(design);
            storedEdition.InheritsCoreCover = false;
        }
        var sceneJson = System.Text.Json.JsonSerializer.Serialize(scene, ManuscriptCodec.JsonOptions);
        var surfaceScenes = ReadSurfaceScenes(design.SurfaceScenesJson).ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        surfaceScenes[surfaceRole] = sceneJson;
        design.SurfaceScenesJson = JsonSerializer.Serialize(surfaceScenes, ManuscriptCodec.JsonOptions);
        if (surfaceRole == NormalizeSurfaceRole(edition, null))
            design.CompositionSceneJson = sceneJson;
        design.Revision = checked(design.Revision + 1);
        design.UpdatedAt = DateTime.UtcNow;
        storedEdition.SelectedCoverImageId = CompositionSceneResolver.Flatten(scene)
            .Where(item => item.Visible && item.Kind == CompositionObjectKind.Image && item.ImageId is not null)
            .OrderBy(item => item.ZIndex)
            .Select(item => item.ImageId)
            .FirstOrDefault();
        storedEdition.Revision = checked(storedEdition.Revision + 1);
        storedEdition.UpdatedAt = DateTime.UtcNow;
        db.CompositionMutationStages.Remove(stage);
        var afterHistory = CaptureReleaseCover(design, storedEdition, hasStoredDesign: true);
        if (string.Equals(beforeHistory, afterHistory, StringComparison.Ordinal))
        {
            db.Entry(design).State = hadStoredDesign ? EntityState.Unchanged : EntityState.Detached;
            db.Entry(storedEdition).State = EntityState.Unchanged;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return await GetSurfaceAsync(projectId, edition.Id, surfaceRole, cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await RecordReleaseCoverMutationAsync(
            projectId, edition.Id, beforeHistory, afterHistory, "Edit release cover");
        return await ViewAsync(await GetEffectiveEditionAsync(projectId, edition.Id, cancellationToken), design, cancellationToken, surfaceRole);
    }

    private async Task ValidateSceneAssetsAsync(
        Guid projectId,
        CompositionScene scene,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var imageIds = scene.Objects.Where(item => item.ImageId is not null)
            .Select(item => item.ImageId!.Value).Distinct().ToList();
        var ownedImages = await db.PublishAssets.AsNoTracking()
            .CountAsync(item => item.ProjectId == projectId
                && imageIds.Contains(item.Id)
                && (item.ContentType == "image/png" || item.ContentType == "image/jpeg"), cancellationToken);
        if (ownedImages != imageIds.Count)
            throw new InvalidDataException("The cover composition references an image outside this project or a non-publication PNG/JPEG asset.");
    }

    private async Task<PublicationCoverDesignView> ViewAsync(
        PublicationEdition edition,
        PublicationCoverDesign design,
        CancellationToken cancellationToken,
        string? surfaceRole = null)
    {
        surfaceRole = NormalizeSurfaceRole(edition, surfaceRole);
        var template = await TemplateAsync(edition, design, cancellationToken, surfaceRole);
        var diagnosticDetails = new List<PublicationCoverDiagnostic>();
        if (edition.Format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover && template.PageCount <= 0)
            AddDiagnostic(diagnosticDetails, "warning", "COVER_GEOMETRY_PENDING", "The full-wrap spine geometry will be finalized from the interior page count during preparation.");
        if (design.BarcodeMode == PublicationBarcodeMode.LorekeeperBarcode
            && !PublicationIsbn.IsValidIsbn13(edition.Isbn))
            AddDiagnostic(diagnosticDetails, "error", "COVER_BARCODE_ISBN_REQUIRED", "Lorekeeper barcode output requires a valid ISBN-13.");
        if (edition.Vendor == PublicationVendor.IngramSpark
            && !PublicationIsbn.IsValidIsbn13(edition.Isbn))
            AddDiagnostic(diagnosticDetails, "error", "COVER_BARCODE_ISBN_REQUIRED", "Ingram cover output requires a valid ISBN-13 barcode.");
        if (edition.Vendor == PublicationVendor.IngramSpark
            && design.BarcodeMode == PublicationBarcodeMode.VendorOverlay)
            AddDiagnostic(diagnosticDetails, "error", "COVER_BARCODE_REQUIRED", "Ingram covers must contain Lorekeeper's ISBN-13 barcode.");
        if (edition.Vendor == PublicationVendor.BarnesAndNoblePress
            && template.PageCount is > 0 and <= 50
            && !string.IsNullOrWhiteSpace(design.SpineText))
            AddDiagnostic(diagnosticDetails, "error", "COVER_SPINE_TEXT_DISABLED", "B&N Press does not allow spine text for books with 50 pages or fewer.");
        else if (template.SpineWidthInches < 0.24 && !string.IsNullOrWhiteSpace(design.SpineText))
            AddDiagnostic(diagnosticDetails, "warning", "COVER_SPINE_TEXT_DISABLED", "Spine text is disabled below the initial 0.24-inch safety threshold.");
        if (!template.IsAcknowledged)
            AddDiagnostic(diagnosticDetails, "warning", "COVER_TEMPLATE_REVIEW_REQUIRED", "Cover geometry changed; review and acknowledge the current template before preparing files.");
        var storedSurfaceScenes = ReadSurfaceScenes(design.SurfaceScenesJson);
        var selectedSceneJson = storedSurfaceScenes.GetValueOrDefault(surfaceRole,
            surfaceRole == "perfect-bound-inside" ? string.Empty : design.CompositionSceneJson);
        var scene = string.IsNullOrWhiteSpace(selectedSceneJson)
            ? CoverCompositionFactory.Create(edition, design, template.PageCount)
            : System.Text.Json.JsonSerializer.Deserialize<CompositionScene>(selectedSceneJson, ManuscriptCodec.JsonOptions)
                ?? CoverCompositionFactory.Create(edition, design, template.PageCount);
        if (surfaceRole == "perfect-bound-inside" && string.IsNullOrWhiteSpace(selectedSceneJson))
            scene = scene with { Objects = [] };
        var expectedGeometry = CoverCompositionFactory.Geometry(edition, template.PageCount, surfaceRole);
        scene = ReflowToCurrentGeometry(edition, design, template, scene, out var geometryChanged, surfaceRole);
        if (geometryChanged)
        {
            AddDiagnostic(diagnosticDetails, "warning", "COVER_GEOMETRY_RECALCULATED", "Cover geometry was recalculated. Review constraint-bound objects and save the composition.");
        }
        scene = CoverCompositionFactory.KeepArtworkBehindCopy(scene);
        AddSceneDiagnostics(edition, expectedGeometry, scene, diagnosticDetails);
        await AddImageDpiDiagnosticsAsync(database, edition.ProjectId, edition, scene, diagnosticDetails, cancellationToken);
        var diagnostics = diagnosticDetails.Select(item => item.Message).ToList();
        return new(
            design.Id,
            edition.Id,
            design.Title,
            design.Subtitle,
            design.Author,
            design.SpineText,
            edition.Description,
            design.BackgroundColor,
            design.BarcodeMode,
            design.ImageCropXPercent,
            design.ImageCropYPercent,
            System.Text.Json.JsonSerializer.Serialize(scene, ManuscriptCodec.JsonOptions),
            design.Revision,
            template,
            diagnostics)
        {
            SurfaceScenes = storedSurfaceScenes,
            DiagnosticDetails = diagnosticDetails,
            SpineReadingDirection = design.SpineReadingDirection,
            Regions = DescribeRegions(edition, template, expectedGeometry),
            SurfaceRole = surfaceRole,
        };
    }

    private static IReadOnlyList<CoverRegionDescriptor> DescribeRegions(
        PublicationEdition edition,
        PublicationCoverTemplate template,
        CoverGeometry geometry)
    {
        return new[]
        {
            (CompositionRegionConstraint.Back, geometry.HasMeasuredRegions ? geometry.BackRegionWidthPoints / 72 : template.TrimWidthInches, geometry.HasMeasuredRegions ? geometry.CoverRegionHeightPoints / 72 : template.TrimHeightInches, "back-cover"),
            (CompositionRegionConstraint.Spine, Math.Max(template.SpineWidthInches, .0001), geometry.HasMeasuredRegions ? geometry.CoverRegionHeightPoints / 72 : template.TrimHeightInches, "spine"),
            (CompositionRegionConstraint.Front, geometry.HasMeasuredRegions ? geometry.FrontRegionWidthPoints / 72 : template.TrimWidthInches, geometry.HasMeasuredRegions ? geometry.CoverRegionHeightPoints / 72 : template.TrimHeightInches, "front-cover"),
        }.Select(item =>
        {
            var participates = item.Item1 != CompositionRegionConstraint.Spine
                || edition.PrintCoverSubmissionMode == PrintCoverSubmissionMode.FullWrapMeasured;
            var fingerprintSource = $"{template.Fingerprint}|{item.Item1}|{item.Item2:0.#####}|{item.Item3:0.#####}|{participates}";
            return new CoverRegionDescriptor(
                item.Item1,
                CoverCompositionFactory.RegionBoundsPercent(item.Item1, geometry),
                item.Item2,
                item.Item3,
                item.Item2 / item.Item3,
                template.SafetyInches,
                item.Item1 == CompositionRegionConstraint.Spine
                    ? ["bleed", "trim", "spine-safe-area", "fold"]
                    : item.Item1 == CompositionRegionConstraint.Back
                        ? ["bleed", "trim", "safe-area", "barcode-reserve"]
                        : ["bleed", "trim", "safe-area"],
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintSource))),
                participates,
                participates ? item.Item4 : "vendor-generated");
        }).ToArray();
    }

    private static IReadOnlyDictionary<string, string> ReadSurfaceScenes(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, ManuscriptCodec.JsonOptions)
                ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    private string NormalizeSurfaceRole(PublicationEdition edition, string? surfaceRole)
    {
        if (edition.Format is not (PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover))
            return "front";
        var product = printArtifactProfiles.GetRequired(edition.PrintArtifactProfileKey);
        var available = new List<string>();
        if (product.RequiresPerfectBoundCover)
        {
            available.Add("perfect-bound-outside");
            if (edition.PrintCoverMode == PrintCoverMode.Duplex) available.Add("perfect-bound-inside");
        }
        if (product.RequiresCaseCover) available.Add("case-wrap");
        if (product.RequiresDustJacket) available.Add("dust-jacket");
        if (product.RequiresClothManifest) available.Add("digital-cloth-setup");
        if (!string.IsNullOrWhiteSpace(surfaceRole) && available.Contains(surfaceRole, StringComparer.Ordinal))
            return surfaceRole;
        return available.FirstOrDefault() ?? "front";
    }

    private static string CoverSceneStageTarget(string surfaceRole) =>
        $"{CoverSceneStagePrefix}{surfaceRole}";

    private static string? CoverSceneStageSurfaceRole(string targetKind) =>
        targetKind.StartsWith(CoverSceneStagePrefix, StringComparison.Ordinal)
            ? targetKind[CoverSceneStagePrefix.Length..]
            : null;

    internal static async Task AddImageDpiDiagnosticsAsync(
        IAppDatabaseOperationFactory database,
        Guid projectId,
        PublicationEdition edition,
        CompositionScene scene,
        ICollection<PublicationCoverDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var imageObjects = CompositionSceneResolver.Flatten(scene)
            .Where(item => item.Visible && item.Kind == CompositionObjectKind.Image && item.ImageId is not null)
            .ToList();
        if (imageObjects.Count == 0)
            return;

        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var imageIds = imageObjects.Select(item => item.ImageId!.Value).Distinct().ToList();
        var assets = await db.PublishAssets.AsNoTracking()
            .Where(item => item.ProjectId == projectId
                && imageIds.Contains(item.Id)
                && (item.ContentType == "image/png" || item.ContentType == "image/jpeg"))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var requiredDpi = edition.Format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover
            ? 300d
            : 180d;
        foreach (var item in imageObjects)
        {
            if (!assets.TryGetValue(item.ImageId!.Value, out var asset))
                continue;
            using var bitmap = SKBitmap.Decode(asset.Data);
            if (bitmap is null)
                continue;
            var widthInches = scene.Surface.WidthPoints / 72 * item.Bounds.WidthPercent / 100;
            var heightInches = scene.Surface.HeightPoints / 72 * item.Bounds.HeightPercent / 100;
            var effectiveDpi = Math.Min(
                bitmap.Width / Math.Max(.01, widthInches),
                bitmap.Height / Math.Max(.01, heightInches));
            if (effectiveDpi < requiredDpi)
            {
                AddDiagnostic(
                    diagnostics,
                    "warning",
                    "IMAGE_DPI_LOW",
                    $"Image resolves to approximately {effectiveDpi:0} DPI; this edition expects {requiredDpi:0} DPI.",
                    item.Id);
            }
        }
    }

    internal static void AddSceneDiagnostics(
        PublicationEdition edition,
        CoverGeometry geometry,
        CompositionScene scene,
        List<string> diagnostics)
    {
        var structured = new List<PublicationCoverDiagnostic>();
        AddSceneDiagnostics(edition, geometry, scene, structured);
        diagnostics.AddRange(structured.Select(item => item.Message));
    }

    internal static void AddSceneDiagnostics(
        PublicationEdition edition,
        CoverGeometry geometry,
        CompositionScene scene,
        List<PublicationCoverDiagnostic> diagnostics)
    {
        if (edition.Vendor == PublicationVendor.IngramSpark
            && CompositionSceneResolver.FindPdfxTransparencyOverlap(scene) is { } opacityOverlap)
        {
            AddDiagnostic(
                diagnostics,
                "error",
                "COVER_TRANSPARENCY_UNSUPPORTED",
                $"Object {opacityOverlap.TransparentObjectId:N} uses opacity over lower object {opacityOverlap.LowerObjectId:N} in a form that cannot be precomposed for PDF/X-1a. Make it opaque or combine the visual artwork into one image.",
                opacityOverlap.TransparentObjectId);
        }
        var barcode = CoverCompositionFactory.RegionBoundsPercent(CompositionRegionConstraint.BarcodeReserve, geometry);
        foreach (var item in CompositionSceneResolver.Flatten(scene).Where(item => item.Visible))
        {
            if (!Contains(new CompositionBounds(), item.Bounds))
            {
                AddDiagnostic(
                    diagnostics,
                    "error",
                    "COVER_OBJECT_OUTSIDE_SURFACE",
                    $"Object {item.Id:N} extends outside the physical cover surface.",
                    item.Id);
            }
            if (item.Kind == CompositionObjectKind.Text && string.IsNullOrWhiteSpace(item.TextBinding))
                AddDiagnostic(diagnostics, "error", "COVER_TEXT_REQUIRED", $"Text object {item.Id:N} requires text or a bindable cover-copy token.", item.Id);
            if (item.Kind == CompositionObjectKind.Text
                && PublicationTextBindings.UnknownTokens(item.TextBinding) is { Count: > 0 } unknownTokens)
                AddDiagnostic(diagnostics, "error", "COVER_TEXT_TOKEN_INVALID", $"Text object {item.Id:N} contains unsupported token(s): {string.Join(", ", unknownTokens)}.", item.Id);
            if (edition.Format is not (PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover)
                && item.Kind == CompositionObjectKind.Text
                && PublicationTextBindings.UsesBinding(item.TextBinding, "spineText"))
                AddDiagnostic(diagnostics, "error", "COVER_TEXT_BINDING_INVALID", $"Digital cover text object {item.Id:N} requires a front-cover copy binding before publishing.", item.Id);
            if (edition.Format is not (PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover)
                && item.RegionConstraint is not CompositionRegionConstraint.Page
                    and not CompositionRegionConstraint.SafeArea
                    and not CompositionRegionConstraint.Front)
                AddDiagnostic(diagnostics, "error", "COVER_REGION_INVALID", $"Digital cover object {item.Id:N} requires a front-cover region before publishing.", item.Id);
            if (item.Kind == CompositionObjectKind.Image && item.ImageId is null)
                AddDiagnostic(diagnostics, "error", "COVER_IMAGE_REQUIRED", $"Image object {item.Id:N} requires project artwork before publishing.", item.Id);
            if (item.Kind == CompositionObjectKind.Image && !item.Decorative
                && (item.AccessibilityDecisionPending || string.IsNullOrWhiteSpace(item.AltText)))
                AddDiagnostic(diagnostics, "error", "COVER_IMAGE_ALT_TEXT_REQUIRED", $"Image object {item.Id:N} requires alternative text or an explicit decorative decision.", item.Id);
            if (!item.Decorative && item.SemanticRole != CompositionSemanticRole.Artifact
                && item.RegionConstraint != CompositionRegionConstraint.BarcodeReserve
                && edition.Format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover
                && Intersects(item.Bounds, barcode))
                AddDiagnostic(
                    diagnostics,
                    "warning",
                    "COVER_BARCODE_OVERLAP",
                    $"Object {item.Id:N} places important content in the barcode placement area. Background artwork may continue through this area, but the printer may cover it with a barcode.",
                    item.Id);
            if (item.Kind != CompositionObjectKind.Text) continue;
            var safe = CoverCompositionFactory.SafeRegionBoundsPercent(item.RegionConstraint, geometry);
            if (!Contains(safe, item.Bounds))
                AddDiagnostic(diagnostics, "error", "COVER_SAFE_AREA_OVERFLOW", $"Object {item.Id:N} extends outside the safe area for its {item.RegionConstraint} region.", item.Id);
        }
        var semanticObjects = CompositionSceneResolver.Flatten(scene)
            .Where(item => item.Visible && !item.Decorative && item.SemanticRole != CompositionSemanticRole.Artifact)
            .ToList();
        var duplicateReadingOrders = semanticObjects.Where(item => item.ReadingOrder is not null)
            .GroupBy(item => item.ReadingOrder!.Value)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();
        foreach (var item in semanticObjects.Where(item => item.ReadingOrder is null
            || duplicateReadingOrders.Contains(item.ReadingOrder.Value)))
        {
            AddDiagnostic(diagnostics, "error", "COVER_READING_ORDER_REQUIRED", $"Object {item.Id:N} requires a unique logical reading order before publishing.", item.Id);
        }
    }

    private static void AddDiagnostic(
        ICollection<PublicationCoverDiagnostic> diagnostics,
        string severity,
        string code,
        string message,
        Guid? objectId = null) => diagnostics.Add(new(severity, code, message, objectId));

    private static bool Intersects(CompositionBounds left, CompositionBounds right) =>
        left.XPercent < right.XPercent + right.WidthPercent
        && right.XPercent < left.XPercent + left.WidthPercent
        && left.YPercent < right.YPercent + right.HeightPercent
        && right.YPercent < left.YPercent + left.HeightPercent;

    private static bool Contains(CompositionBounds outer, CompositionBounds inner) =>
        inner.XPercent >= outer.XPercent - .001
        && inner.YPercent >= outer.YPercent - .001
        && inner.XPercent + inner.WidthPercent <= outer.XPercent + outer.WidthPercent + .001
        && inner.YPercent + inner.HeightPercent <= outer.YPercent + outer.HeightPercent + .001;

    private async Task<PublicationCoverTemplate> TemplateAsync(
        PublicationEdition edition,
        PublicationCoverDesign design,
        CancellationToken cancellationToken,
        string? surfaceRole = null)
    {
        if (edition.Format is not (PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover))
            return DigitalTemplate(edition, design);
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var currentFingerprint = await editions.GetPaginationFingerprintAsync(edition.ProjectId, edition.Id, cancellationToken);
        string currentRendererVersion;
        try
        {
            currentRendererVersion = pressRuntime.GetDescription().RendererVersion;
        }
        catch (InvalidOperationException)
        {
            currentRendererVersion = string.Empty;
        }
        var pages = await db.PublicationArtifacts.AsNoTracking()
            .Where(artifact => artifact.EditionId == edition.Id
                && artifact.Kind == PublicationArtifactKind.InteriorPdf
                && !artifact.IsLegacy
                && artifact.PaginationFingerprint == currentFingerprint
                && artifact.RendererVersion == currentRendererVersion)
            .OrderByDescending(artifact => artifact.CreatedAt)
            .Select(artifact => artifact.PageCount)
            .FirstOrDefaultAsync(cancellationToken) ?? 0;
        var snapshot = pages > 0
            ? null
            : await db.PublicationInteriorPaginations.AsNoTracking()
                .SingleOrDefaultAsync(item => item.EditionId == edition.Id, cancellationToken);
        if (pages <= 0
            && snapshot is { PageCount: > 0 }
            && string.Equals(snapshot.PaginationFingerprint, currentFingerprint, StringComparison.Ordinal)
            && string.Equals(snapshot.RendererVersion, currentRendererVersion, StringComparison.Ordinal))
        {
            pages = snapshot.PageCount;
        }
        var geometryPages = pages > 0
            ? pages
            : printArtifactProfiles.GetRequired(edition.PrintArtifactProfileKey).MinimumPages;
        var product = printArtifactProfiles.GetRequired(edition.PrintArtifactProfileKey);
        var geometry = printGeometry.Calculate(edition, geometryPages, surfaceRole);
        return new(pages, edition.PageWidthInches, edition.PageHeightInches, (double)geometry.BleedInches,
            (double)geometry.SpineWidthInches, (double)geometry.SurfaceWidthInches, (double)geometry.SurfaceHeightInches,
            (double)product.CoverSafetyInches,
            (double)product.BarcodeWidthInches,
            (double)product.BarcodeHeightInches,
            geometry.GeometryFingerprint,
            string.Equals(geometry.GeometryFingerprint, design.AcknowledgedTemplateFingerprint, StringComparison.Ordinal))
        {
            BarcodeInsetInches = (double)product.BarcodeInsetInches,
            BackRegionWidthInches = (double)geometry.BackRegionWidthInches,
            FrontRegionWidthInches = (double)geometry.FrontRegionWidthInches,
            CoverRegionYInches = (double)geometry.CoverRegionYInches,
            CoverRegionHeightInches = (double)geometry.CoverRegionHeightInches,
        };
    }

    // Digital covers are a single trim-sized surface: they have no spine, bleed,
    // vendor product or interior page count, so no print artifact profile applies.
    private static PublicationCoverTemplate DigitalTemplate(PublicationEdition edition, PublicationCoverDesign design)
    {
        var fingerprintSource = string.Join('|', "digital-cover-v1", edition.Format,
            edition.PageWidthInches.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            edition.PageHeightInches.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintSource))).ToLowerInvariant();
        return new(0, edition.PageWidthInches, edition.PageHeightInches, 0, 0,
            edition.PageWidthInches, edition.PageHeightInches, 0.25, 2, 1.2, fingerprint,
            string.Equals(fingerprint, design.AcknowledgedTemplateFingerprint, StringComparison.Ordinal));
    }

    private async Task RequireCurrentInteriorPaginationAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken)
    {
        var edition = await GetEditionAsync(projectId, editionId, cancellationToken);
        if (edition.Format is not (PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover))
            return;

        var paginationFingerprint = await editions.GetPaginationFingerprintAsync(projectId, editionId, cancellationToken);
        var rendererVersion = pressRuntime.GetDescription().RendererVersion;
        PublicationInteriorPagination? snapshot;
        await using (var operation = await database.OpenReadAsync(cancellationToken))
        {
            snapshot = await operation.Db.PublicationInteriorPaginations.AsNoTracking()
                .SingleOrDefaultAsync(item => item.EditionId == editionId, cancellationToken);
        }
        if (snapshot is not { PageCount: > 0 }
            || !string.Equals(snapshot.PaginationFingerprint, paginationFingerprint, StringComparison.Ordinal)
            || !string.Equals(snapshot.RendererVersion, rendererVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Prepare the current interior pagination before editing this print cover.");
        }
    }

    private async Task<PublicationEdition> GetEditionAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken,
        bool tracked = false)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var editions = tracked ? db.PublicationEditions : db.PublicationEditions.AsNoTracking();
        return await editions.FirstOrDefaultAsync(
            edition => edition.Id == editionId && edition.ProjectId == projectId,
            cancellationToken) ?? throw new KeyNotFoundException("Publication release not found.");
    }

    private async Task<PublicationEdition> GetEffectiveEditionAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken) =>
        (await effectiveConfigurations.ResolveReleaseAsync(projectId, editionId, cancellationToken)).Edition;

    private static PublicationCoverDesign Default(PublicationEdition edition)
    {
        var design = new PublicationCoverDesign
        {
            EditionId = edition.Id,
            Title = edition.TitleOverride,
            Subtitle = edition.Subtitle,
            Author = edition.Author,
            // A new paperback must remain renderable even when its first interior is
            // too short for safe spine copy. Users can add spine text after the
            // calculated template proves that it fits.
            SpineText = string.Empty,
            SpineReadingDirection = SpineReadingDirection.TopToBottom,
            BarcodeMode = edition.Format is not (PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover)
                ? PublicationBarcodeMode.None
                : edition.Vendor == PublicationVendor.IngramSpark
                    ? PublicationBarcodeMode.LorekeeperBarcode
                    : edition.Vendor == PublicationVendor.Lulu
                        ? edition.PrintProjectUse == PrintProjectUse.PersonalUse
                            ? PublicationBarcodeMode.None
                            : PublicationBarcodeMode.LorekeeperBarcode
                        : PublicationBarcodeMode.VendorOverlay,
        };
        design.CompositionSceneJson = System.Text.Json.JsonSerializer.Serialize(
            CoverCompositionFactory.Create(edition, design),
            ManuscriptCodec.JsonOptions);
        return design;
    }

    private async Task<PublicationCoverDesign> DefaultAsync(
        Guid projectId,
        PublicationEdition edition,
        bool lockCoreLayers,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var design = Default(edition);
        if (!edition.InheritsCoreCover)
            return design;
        var coreSceneJson = await db.PublicationBookCoverDesigns.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .Select(item => item.CompositionSceneJson)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(coreSceneJson))
            return design;
        var coreScene = JsonSerializer.Deserialize<CompositionScene>(coreSceneJson, ManuscriptCodec.JsonOptions)
            ?? throw new InvalidDataException("The Core cover composition is empty.");
        var template = await TemplateAsync(edition, design, cancellationToken);
        design.CompositionSceneJson = JsonSerializer.Serialize(
            CoverCompositionFactory.CreateReleaseFromCore(
                edition,
                design,
                coreScene,
                template.PageCount,
                lockCoreLayers),
            ManuscriptCodec.JsonOptions);
        return design;
    }

    private static int EstimatePageCount(PublicationEdition edition, double widthPoints)
    {
        if (edition.Format is not (PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover))
            return 0;
        return 0;
    }

    internal static CompositionScene ReflowToCurrentGeometry(
        PublicationEdition edition,
        PublicationCoverDesign design,
        PublicationCoverTemplate template,
        CompositionScene scene,
        out bool geometryChanged,
        string? surfaceRole = null)
    {
        var expected = CoverCompositionFactory.Geometry(edition, template.PageCount, surfaceRole);
        geometryChanged = scene.SchemaVersion == CompositionScene.CurrentSchemaVersion
            && double.IsFinite(scene.Surface.WidthPoints)
            && double.IsFinite(scene.Surface.HeightPoints)
            && scene.Surface.WidthPoints > 0
            && scene.Surface.HeightPoints > 0
            && !SurfaceMatches(scene.Surface, expected);
        if (!geometryChanged)
            return scene;

        var oldPageCount = EstimatePageCount(edition, scene.Surface.WidthPoints);
        return CoverCompositionFactory.Reflow(
            edition,
            design,
            scene,
            oldPageCount,
            template.PageCount,
            surfaceRole);
    }

    private static bool SurfaceMatches(CompositionSurface surface, CoverGeometry expected) =>
        Math.Abs(surface.WidthPoints - expected.WidthPoints) <= .01
        && Math.Abs(surface.HeightPoints - expected.HeightPoints) <= .01
        && Math.Abs(surface.TrimWidthPoints - expected.TrimWidthPoints) <= .01
        && Math.Abs(surface.TrimHeightPoints - expected.TrimHeightPoints) <= .01
        && Math.Abs(surface.BleedPoints - expected.BleedPoints) <= .01
        && Math.Abs(surface.SafeInsetPoints - expected.SafeInsetPoints) <= .01
        && Math.Abs(surface.SpineWidthPoints - expected.SpineWidthPoints) <= .01
        && Math.Abs(surface.BackRegionWidthPoints - expected.BackRegionWidthPoints) <= .01
        && Math.Abs(surface.FrontRegionWidthPoints - expected.FrontRegionWidthPoints) <= .01
        && Math.Abs(surface.CoverRegionYPoints - expected.CoverRegionYPoints) <= .01
        && Math.Abs(surface.CoverRegionHeightPoints - expected.CoverRegionHeightPoints) <= .01;

    internal static void ValidateAuthoringScene(
        CompositionScene scene,
        PublicationEdition edition,
        PublicationCoverTemplate template)
    {
        var expected = CoverCompositionFactory.Geometry(edition, template.PageCount);
        if (scene.SchemaVersion != CompositionScene.CurrentSchemaVersion
            || !SurfaceMatches(scene.Surface, expected))
            throw new InvalidDataException("The cover composition does not match the edition's current cover geometry.");
        var layerIds = scene.Layers.Select(item => item.Id).ToHashSet();
        if (layerIds.Count != scene.Layers.Count || layerIds.Contains(Guid.Empty))
            throw new InvalidDataException("Cover layers require unique, non-empty IDs.");
        var styleIds = scene.Styles.Select(style => style.Id).ToHashSet();
        if (styleIds.Count != scene.Styles.Count || styleIds.Contains(Guid.Empty)
            || scene.Styles.Any(style => string.IsNullOrWhiteSpace(style.Name)
                || style.FontSizePoints is < 4 or > 288
                || style.LineHeight is < .5 or > 4
                || style.StrokeWidthPoints is < 0 or > 72))
            throw new InvalidDataException("Cover object styles require unique IDs, names, and valid typography and stroke values.");
        var objectIds = new HashSet<Guid>();
        foreach (var item in scene.Objects)
        {
            if (item.Id == Guid.Empty || !objectIds.Add(item.Id) || !layerIds.Contains(item.LayerId))
                throw new InvalidDataException("Cover objects require unique IDs and an existing layer.");
            if (!double.IsFinite(item.Bounds.XPercent)
                || !double.IsFinite(item.Bounds.YPercent)
                || !double.IsFinite(item.Bounds.WidthPercent)
                || !double.IsFinite(item.Bounds.HeightPercent)
                || item.Bounds.WidthPercent is <= 0 or > 400
                || item.Bounds.HeightPercent is <= 0 or > 400
                || item.Opacity is < 0 or > 1)
                throw new InvalidDataException($"Cover object {item.Id:N} has invalid geometry.");
            if (item.StyleId is Guid styleId && !styleIds.Contains(styleId))
                throw new InvalidDataException($"Cover object {item.Id:N} references a missing object style.");
        }
    }

    private static void ValidateAuthoringUpdate(PublicationCoverDesignUpdate update)
    {
        if (!Enum.IsDefined(update.BarcodeMode))
            throw new ArgumentException("Barcode mode is invalid.");
        if (update.SpineReadingDirection is { } direction && !Enum.IsDefined(direction))
            throw new ArgumentException("Spine reading direction is invalid.");
        if (update.Title.Trim().Length > 160
            || update.Subtitle.Trim().Length > 240
            || update.Author.Trim().Length > 160
            || update.SpineText.Trim().Length > 120)
            throw new ArgumentException("Cover copy exceeds its allowed length.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(update.BackgroundColor, "^#[0-9a-fA-F]{6}$"))
            throw new ArgumentException("Background color must be a six-digit hex color.");
        if (!double.IsFinite(update.ImageCropXPercent)
            || !double.IsFinite(update.ImageCropYPercent)
            || update.ImageCropXPercent is < 0 or > 100
            || update.ImageCropYPercent is < 0 or > 100)
            throw new ArgumentException("Image crop positions must be between 0 and 100 percent.");
    }

    private static void Validate(PublicationCoverDesignUpdate update)
    {
        ValidateAuthoringUpdate(update);
        if (update.Title.Trim().Length == 0)
            throw new ArgumentException("A publication cover title is required.");
    }

    private static void ValidateProduct(PublicationCoverDesignUpdate update, PublicationEdition edition)
    {
        if (edition.Format is not (PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover)
            && update.BarcodeMode != PublicationBarcodeMode.None)
            throw new InvalidOperationException("Digital covers do not support print barcode regions or overlays.");
    }
}

public static class PublicationIsbn
{
    public static bool IsValidIsbn13(string value)
    {
        if (!TryNormalizeIsbn13(value, out var digits))
            return false;
        var sum = digits.Take(12).Select((digit, index) => (digit - '0') * (index % 2 == 0 ? 1 : 3)).Sum();
        return (10 - sum % 10) % 10 == digits[12] - '0';
    }

    public static bool TryNormalizeIsbn13(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var trimmed = value.Trim();
        if (trimmed.Any(character => !char.IsAsciiDigit(character) && character is not '-' and not ' '))
            return false;
        normalized = new string(trimmed.Where(char.IsAsciiDigit).ToArray());
        return normalized.Length == 13;
    }

    public static string NormalizeValidOrEmpty(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        if (!TryNormalizeIsbn13(value, out var normalized) || !IsValidIsbn13(normalized))
            throw new InvalidOperationException("ISBN must be a valid ISBN-13 containing only digits, spaces, or hyphens.");
        return normalized;
    }

    public static string CanonicalForOutput(string? value) =>
        TryNormalizeIsbn13(value, out var normalized) && IsValidIsbn13(normalized)
            ? normalized
            : value?.Trim() ?? string.Empty;
}
