using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lorekeeper.Models;
using Lorekeeper.Manuscripts;
using Lorekeeper.Composition;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Publish;

public sealed record PublicationCoverDesignView(
    Guid Id,
    Guid EditionId,
    string Title,
    string Subtitle,
    string Author,
    string SpineText,
    string BackCopy,
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
    bool IsAcknowledged);

public sealed record PublicationCoverDesignUpdate(
    string Title,
    string Subtitle,
    string Author,
    string SpineText,
    string BackCopy,
    string BackgroundColor,
    PublicationBarcodeMode BarcodeMode,
    double ImageCropXPercent,
    double ImageCropYPercent,
    long ExpectedRevision,
    bool AcknowledgeTemplate);

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
    Task<CompositionMutationStage> StageSceneAsync(Guid projectId, Guid conversationId, Guid editionId, long expectedRevision, CompositionScene scene, CancellationToken cancellationToken = default);
    Task<PublicationCoverDesignView> ApplySceneStageAsync(Guid projectId, Guid conversationId, Guid stageId, long expectedRevision, CancellationToken cancellationToken = default);
    Task<PublicationCoverDesignView> CustomizeFromCoreAsync(Guid projectId, Guid editionId, long expectedEditionRevision, CancellationToken cancellationToken = default);
    Task UseCoreAsync(Guid projectId, Guid editionId, long expectedEditionRevision, CancellationToken cancellationToken = default);
}

public sealed class PublicationCoverService(
    AppDbContext db,
    IProjectMutationCoordinator projectMutations,
    IPublicationEditionService editions,
    IPublicationEffectiveConfigurationResolver effectiveConfigurations,
    IPublicationPressRuntime pressRuntime,
    IPrintGeometryService printGeometry,
    IPrintProductRegistry printProducts) : IPublicationCoverService
{
    public PublicationCoverService(
        AppDbContext db,
        IProjectMutationCoordinator projectMutations,
        IPublicationEditionService editions,
        IPublicationPressRuntime pressRuntime)
        : this(db, projectMutations, editions, new PublicationEffectiveConfigurationResolver(db), pressRuntime,
            new PrintGeometryService(new PrintProductRegistry()), new PrintProductRegistry())
    {
    }

    public async Task<PublicationCoverDesignView> GetAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken = default)
    {
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
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var stored = await GetEditionAsync(projectId, editionId, cancellationToken, tracked: true);
        PublicationEditionService.EnsureDraft(stored);
        if (stored.Revision != expectedEditionRevision)
            throw new DbUpdateConcurrencyException("The publication release changed; reread it before customizing the cover.");
        var effective = await GetEffectiveEditionAsync(projectId, editionId, cancellationToken);
        var design = await db.PublicationCoverDesigns.SingleOrDefaultAsync(
            item => item.EditionId == editionId,
            cancellationToken);
        if (design is null)
        {
            design = await DefaultAsync(projectId, effective, lockCoreLayers: false, cancellationToken);
            db.PublicationCoverDesigns.Add(design);
        }
        stored.InheritsCoreCover = false;
        stored.Revision = checked(stored.Revision + 1);
        stored.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await ViewAsync(await GetEffectiveEditionAsync(projectId, editionId, cancellationToken), design, cancellationToken);
    }

    public async Task UseCoreAsync(
        Guid projectId,
        Guid editionId,
        long expectedEditionRevision,
        CancellationToken cancellationToken = default)
    {
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var stored = await GetEditionAsync(projectId, editionId, cancellationToken, tracked: true);
        PublicationEditionService.EnsureDraft(stored);
        if (stored.Revision != expectedEditionRevision)
            throw new DbUpdateConcurrencyException("The publication release changed; reread it before restoring the Core cover.");
        var design = await db.PublicationCoverDesigns.SingleOrDefaultAsync(
            item => item.EditionId == editionId,
            cancellationToken);
        if (design is not null)
            db.PublicationCoverDesigns.Remove(design);
        stored.InheritsCoreCover = true;
        stored.SelectedCoverImageId = null;
        stored.Revision = checked(stored.Revision + 1);
        stored.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PublicationCoverDesignView> UpdateAsync(
        Guid projectId,
        Guid editionId,
        PublicationCoverDesignUpdate update,
        CancellationToken cancellationToken = default)
    {
        Validate(update);
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var storedEdition = await GetEditionAsync(projectId, editionId, cancellationToken, tracked: true);
        var edition = await GetEffectiveEditionAsync(projectId, editionId, cancellationToken);
        ValidateProduct(update, edition);
        PublicationEditionService.EnsureDraft(storedEdition);
        var design = await db.PublicationCoverDesigns
            .FirstOrDefaultAsync(candidate => candidate.EditionId == editionId, cancellationToken);
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
        design.BackCopy = update.BackCopy.Trim();
        design.BackgroundColor = update.BackgroundColor.Trim().ToLowerInvariant();
        design.BarcodeMode = update.BarcodeMode;
        design.ImageCropXPercent = update.ImageCropXPercent;
        design.ImageCropYPercent = update.ImageCropYPercent;
        design.AcknowledgedTemplateFingerprint = update.AcknowledgeTemplate
            ? template.Fingerprint
            : design.AcknowledgedTemplateFingerprint;
        design.Revision++;
        design.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
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
        var patched = CompositionService.ApplyElementPatch(scene, targetKind, targetId, patch);
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
        var patched = CompositionService.ApplyElementPatch(scene, targetKind, targetId, patch);
        return await SaveSurfaceWorkspaceAsync(
            projectId,
            editionId,
            surfaceRole,
            new PublicationCoverDesignUpdate(
                cover.Title, cover.Subtitle, cover.Author, cover.SpineText, cover.BackCopy,
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
        scene = CoverCompositionFactory.KeepArtworkBehindCopy(scene);
        ValidateAuthoringUpdate(update);
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var storedEdition = await GetEditionAsync(projectId, editionId, cancellationToken, tracked: true);
        var edition = await GetEffectiveEditionAsync(projectId, editionId, cancellationToken);
        ValidateProduct(update, edition);
        PublicationEditionService.EnsureDraft(storedEdition);
        var design = await db.PublicationCoverDesigns.FirstOrDefaultAsync(
            candidate => candidate.EditionId == editionId,
            cancellationToken);
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
        ValidateAuthoringScene(scene, edition, template);
        await ValidateSceneAssetsAsync(projectId, scene, cancellationToken);
        design.Title = update.Title.Trim();
        design.Subtitle = update.Subtitle.Trim();
        design.Author = update.Author.Trim();
        design.SpineText = update.SpineText.Trim();
        design.BackCopy = update.BackCopy.Trim();
        design.BackgroundColor = update.BackgroundColor.Trim().ToLowerInvariant();
        design.BarcodeMode = update.BarcodeMode;
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
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await ViewAsync(await GetEffectiveEditionAsync(projectId, editionId, cancellationToken), design, cancellationToken, surfaceRole);
    }

    public async Task<PublicationCoverDesignView> UpdateSceneAsync(
        Guid projectId,
        Guid editionId,
        string sceneJson,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var scene = System.Text.Json.JsonSerializer.Deserialize<CompositionScene>(sceneJson, ManuscriptCodec.JsonOptions)
            ?? throw new InvalidDataException("Cover composition is empty.");
        scene = CoverCompositionFactory.KeepArtworkBehindCopy(scene);
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var storedEdition = await GetEditionAsync(projectId, editionId, cancellationToken, tracked: true);
        var edition = await GetEffectiveEditionAsync(projectId, editionId, cancellationToken);
        PublicationEditionService.EnsureDraft(storedEdition);
        var design = await db.PublicationCoverDesigns.FirstOrDefaultAsync(item => item.EditionId == editionId, cancellationToken)
            ?? await DefaultAsync(projectId, edition, lockCoreLayers: false, cancellationToken);
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
        await db.SaveChangesAsync(cancellationToken);
        return await ViewAsync(await GetEffectiveEditionAsync(projectId, editionId, cancellationToken), design, cancellationToken);
    }

    public async Task<CompositionMutationStage> StageSceneAsync(
        Guid projectId,
        Guid conversationId,
        Guid editionId,
        long expectedRevision,
        CompositionScene scene,
        CancellationToken cancellationToken = default)
    {
        scene = CoverCompositionFactory.KeepArtworkBehindCopy(scene);
        if (conversationId == Guid.Empty)
            throw new ArgumentException("A conversation is required for staged cover changes.", nameof(conversationId));
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        await db.CompositionMutationStages
            .Where(item => item.ProjectId == projectId
                && (item.ExpiresAt <= DateTime.UtcNow || item.AppliedAt != null))
            .ExecuteDeleteAsync(cancellationToken);
        var edition = await GetEffectiveEditionAsync(projectId, editionId, cancellationToken);
        var design = await db.PublicationCoverDesigns.AsNoTracking()
            .FirstOrDefaultAsync(item => item.EditionId == editionId, cancellationToken)
            ?? await DefaultAsync(projectId, edition, lockCoreLayers: false, cancellationToken);
        if (design.Revision != expectedRevision)
            throw new DbUpdateConcurrencyException("The cover composition changed.");
        var template = await TemplateAsync(edition, design, cancellationToken);
        scene = ReflowToCurrentGeometry(edition, design, template, scene, out _);
        ValidateAuthoringScene(scene, edition, template);
        await ValidateSceneAssetsAsync(projectId, scene, cancellationToken);
        var payload = System.Text.Json.JsonSerializer.Serialize(scene, ManuscriptCodec.JsonOptions);
        var stage = new CompositionMutationStage
        {
            ProjectId = projectId,
            ConversationId = conversationId,
            TargetKind = "cover-scene",
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
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var stage = await db.CompositionMutationStages.SingleOrDefaultAsync(item =>
            item.Id == stageId
            && item.ProjectId == projectId
            && item.ConversationId == conversationId
            && item.TargetKind == "cover-scene", cancellationToken)
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
        PublicationEditionService.EnsureDraft(storedEdition);
        var design = await db.PublicationCoverDesigns.FirstOrDefaultAsync(item => item.EditionId == edition.Id, cancellationToken)
            ?? await DefaultAsync(projectId, edition, lockCoreLayers: false, cancellationToken);
        if (design.Revision != expectedRevision)
            throw new DbUpdateConcurrencyException("The cover composition changed after it was staged.");
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
        db.CompositionMutationStages.Remove(stage);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await ViewAsync(await GetEffectiveEditionAsync(projectId, edition.Id, cancellationToken), design, cancellationToken);
    }

    private async Task ValidateSceneAssetsAsync(
        Guid projectId,
        CompositionScene scene,
        CancellationToken cancellationToken)
    {
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
        var diagnostics = new List<string>();
        if (edition.Format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover && template.PageCount <= 0)
            diagnostics.Add("The full-wrap spine geometry will be finalized from the interior page count during preparation.");
        if (design.BarcodeMode == PublicationBarcodeMode.LorekeeperBarcode
            && !PublicationIsbn.IsValidIsbn13(edition.Isbn))
            diagnostics.Add("Lorekeeper barcode output requires a valid ISBN-13.");
        if (edition.Vendor == PublicationVendor.IngramSpark
            && !PublicationIsbn.IsValidIsbn13(edition.Isbn))
            diagnostics.Add("Ingram cover output requires a valid ISBN-13 barcode.");
        if (edition.Vendor == PublicationVendor.IngramSpark
            && design.BarcodeMode == PublicationBarcodeMode.VendorOverlay)
            diagnostics.Add("Ingram covers must contain Lorekeeper's ISBN-13 barcode.");
        if (template.SpineWidthInches < 0.24 && !string.IsNullOrWhiteSpace(design.SpineText))
            diagnostics.Add("Spine text is disabled below the initial 0.24-inch safety threshold.");
        if (!template.IsAcknowledged)
            diagnostics.Add("Cover geometry changed; review and acknowledge the current template before preparing files.");
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
            diagnostics.Add("Cover geometry was recalculated. Review constraint-bound objects and save the composition.");
        }
        scene = CoverCompositionFactory.KeepArtworkBehindCopy(scene);
        AddSceneDiagnostics(edition, expectedGeometry, scene, diagnostics);
        return new(
            design.Id,
            edition.Id,
            design.Title,
            design.Subtitle,
            design.Author,
            design.SpineText,
            design.BackCopy,
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
        };
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
        var product = printProducts.GetRequired(edition.PrintProductKey);
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

    internal static void AddSceneDiagnostics(
        PublicationEdition edition,
        CoverGeometry geometry,
        CompositionScene scene,
        List<string> diagnostics)
    {
        if (edition.Vendor == PublicationVendor.IngramSpark
            && CompositionSceneResolver.FindPdfxTransparencyOverlap(scene) is { } opacityOverlap)
        {
            diagnostics.Add($"Object {opacityOverlap.TransparentObjectId:N} uses opacity over lower object {opacityOverlap.LowerObjectId:N} in a form that cannot be precomposed for PDF/X-1a. Make it opaque or combine the visual artwork into one image.");
        }
        var barcode = CoverCompositionFactory.RegionBoundsPercent(CompositionRegionConstraint.BarcodeReserve, geometry);
        foreach (var item in CompositionSceneResolver.Flatten(scene).Where(item => item.Visible))
        {
            if (item.Kind == CompositionObjectKind.Text
                && item.TextBinding is not "title" and not "subtitle" and not "author" and not "spineText" and not "backCopy")
                diagnostics.Add($"Text object {item.Id:N} requires a canonical cover-copy binding.");
            if (edition.Format is not (PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover)
                && item.Kind == CompositionObjectKind.Text
                && item.TextBinding is ("spineText" or "backCopy"))
                diagnostics.Add($"Digital cover text object {item.Id:N} requires a front-cover copy binding before publishing.");
            if (edition.Format is not (PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover)
                && item.RegionConstraint is not CompositionRegionConstraint.Page
                    and not CompositionRegionConstraint.SafeArea
                    and not CompositionRegionConstraint.Front)
                diagnostics.Add($"Digital cover object {item.Id:N} requires a front-cover region before publishing.");
            if (item.Kind == CompositionObjectKind.Image && item.ImageId is null)
                diagnostics.Add($"Image object {item.Id:N} requires project artwork before publishing.");
            if (item.Kind == CompositionObjectKind.Image && !item.Decorative
                && (item.AccessibilityDecisionPending || string.IsNullOrWhiteSpace(item.AltText)))
                diagnostics.Add($"Image object {item.Id:N} requires alternative text or an explicit decorative decision.");
            if (!item.Decorative && item.SemanticRole != CompositionSemanticRole.Artifact
                && item.RegionConstraint != CompositionRegionConstraint.BarcodeReserve
                && edition.Format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover
                && Intersects(item.Bounds, barcode))
                diagnostics.Add($"Object {item.Id:N} overlaps the barcode reserve.");
            if (item.Decorative || item.SemanticRole == CompositionSemanticRole.Artifact) continue;
            var region = CoverCompositionFactory.RegionBoundsPercent(item.RegionConstraint, geometry);
            var insetX = item.RegionConstraint == CompositionRegionConstraint.Spine
                ? region.WidthPercent * .05
                : scene.Surface.SafeInsetPoints / scene.Surface.WidthPoints * 100;
            var insetY = scene.Surface.SafeInsetPoints / scene.Surface.HeightPoints * 100;
            var safe = region with
            {
                XPercent = region.XPercent + insetX,
                YPercent = region.YPercent + insetY,
                WidthPercent = Math.Max(0, region.WidthPercent - insetX * 2),
                HeightPercent = Math.Max(0, region.HeightPercent - insetY * 2),
            };
            if (!Contains(safe, item.Bounds))
                diagnostics.Add($"Object {item.Id:N} extends outside the safe area for its {item.RegionConstraint} region.");
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
            diagnostics.Add($"Object {item.Id:N} requires a unique logical reading order before publishing.");
        }
    }

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
                && artifact.RendererVersion == currentRendererVersion
                && artifact.ProfileId == edition.VendorProfileVersion)
            .OrderByDescending(artifact => artifact.CreatedAt)
            .Select(artifact => artifact.PageCount)
            .FirstOrDefaultAsync(cancellationToken) ?? 0;
        var provisionalPages = pages > 0
            ? pages
            : printProducts.GetRequired(edition.PrintProductKey).MinimumPages;
        var geometry = printGeometry.Calculate(edition, provisionalPages, surfaceRole);
        return new(pages, edition.PageWidthInches, edition.PageHeightInches, (double)geometry.BleedInches,
            (double)geometry.SpineWidthInches, (double)geometry.SurfaceWidthInches, (double)geometry.SurfaceHeightInches,
            0.25, 2, 1.2, geometry.GeometryFingerprint,
            string.Equals(geometry.GeometryFingerprint, design.AcknowledgedTemplateFingerprint, StringComparison.Ordinal));
    }

    private async Task<PublicationEdition> GetEditionAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken,
        bool tracked = false)
    {
        if (tracked
            && db.PublicationEditions.Local.FirstOrDefault(edition => edition.Id == editionId) is { } localEdition)
        {
            await db.Entry(localEdition).ReloadAsync(cancellationToken);
        }
        if (tracked
            && db.PublicationCoverDesigns.Local.FirstOrDefault(design => design.EditionId == editionId) is { } localDesign)
        {
            await db.Entry(localDesign).ReloadAsync(cancellationToken);
        }
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
            BackCopy = edition.Description,
            BarcodeMode = edition.Format is not (PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover)
                ? PublicationBarcodeMode.None
                : edition.Vendor == PublicationVendor.IngramSpark
                    ? PublicationBarcodeMode.LorekeeperBarcode
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
            && (Math.Abs(scene.Surface.WidthPoints - expected.WidthPoints) > .01
                || Math.Abs(scene.Surface.HeightPoints - expected.HeightPoints) > .01);
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

    internal static void ValidateAuthoringScene(
        CompositionScene scene,
        PublicationEdition edition,
        PublicationCoverTemplate template)
    {
        var expected = CoverCompositionFactory.Geometry(edition, template.PageCount);
        if (scene.SchemaVersion != CompositionScene.CurrentSchemaVersion
            || Math.Abs(scene.Surface.WidthPoints - expected.WidthPoints) > .01
            || Math.Abs(scene.Surface.HeightPoints - expected.HeightPoints) > .01)
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
        if (update.Title.Trim().Length > 160
            || update.Subtitle.Trim().Length > 240
            || update.Author.Trim().Length > 160
            || update.SpineText.Trim().Length > 120
            || update.BackCopy.Trim().Length > 1_800)
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
