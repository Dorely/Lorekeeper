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
    IReadOnlyList<string> Diagnostics);

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
    Task<PublicationCoverDesignView> UpdateAsync(Guid projectId, Guid editionId, PublicationCoverDesignUpdate update, CancellationToken cancellationToken = default);
    Task<PublicationCoverDesignView> SaveWorkspaceAsync(Guid projectId, Guid editionId, PublicationCoverDesignUpdate update, CompositionScene scene, CancellationToken cancellationToken = default);
    Task<PublicationCoverDesignView> UpdateSceneAsync(Guid projectId, Guid editionId, string sceneJson, long expectedRevision, CancellationToken cancellationToken = default);
    Task<PublicationCoverDesignView> PatchElementAsync(Guid projectId, Guid editionId, long expectedRevision, string targetKind, Guid targetId, CompositionElementPatch patch, CancellationToken cancellationToken = default);
    Task<CompositionMutationStage> StageSceneAsync(Guid projectId, Guid conversationId, Guid editionId, long expectedRevision, CompositionScene scene, CancellationToken cancellationToken = default);
    Task<PublicationCoverDesignView> ApplySceneStageAsync(Guid projectId, Guid conversationId, Guid stageId, long expectedRevision, CancellationToken cancellationToken = default);
}

public sealed class PublicationCoverService(
    AppDbContext db,
    IProjectMutationCoordinator projectMutations,
    IPublicationEditionService editions,
    IPublicationPressRuntime pressRuntime) : IPublicationCoverService
{
    public async Task<PublicationCoverDesignView> GetAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken = default)
    {
        var edition = await GetEditionAsync(projectId, editionId, cancellationToken);
        var design = await db.PublicationCoverDesigns.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.EditionId == editionId, cancellationToken)
            ?? Default(edition);
        return await ViewAsync(edition, design, cancellationToken);
    }

    public async Task<PublicationCoverDesignView> UpdateAsync(
        Guid projectId,
        Guid editionId,
        PublicationCoverDesignUpdate update,
        CancellationToken cancellationToken = default)
    {
        Validate(update);
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var edition = await GetEditionAsync(projectId, editionId, cancellationToken, tracked: true);
        ValidateProduct(update, edition);
        PublicationEditionService.EnsureDraft(edition);
        var design = await db.PublicationCoverDesigns
            .FirstOrDefaultAsync(candidate => candidate.EditionId == editionId, cancellationToken);
        if (design is null)
        {
            if (update.ExpectedRevision != 0)
                throw new DbUpdateConcurrencyException("The cover design changed.");
            design = Default(edition);
            db.PublicationCoverDesigns.Add(design);
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

    public async Task<PublicationCoverDesignView> SaveWorkspaceAsync(
        Guid projectId,
        Guid editionId,
        PublicationCoverDesignUpdate update,
        CompositionScene scene,
        CancellationToken cancellationToken = default)
    {
        Validate(update);
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var edition = await GetEditionAsync(projectId, editionId, cancellationToken, tracked: true);
        ValidateProduct(update, edition);
        PublicationEditionService.EnsureDraft(edition);
        var design = await db.PublicationCoverDesigns.FirstOrDefaultAsync(
            candidate => candidate.EditionId == editionId,
            cancellationToken);
        if (design is null)
        {
            if (update.ExpectedRevision != 0)
                throw new DbUpdateConcurrencyException("The cover design changed.");
            design = Default(edition);
            db.PublicationCoverDesigns.Add(design);
        }
        else if (design.Revision != update.ExpectedRevision)
        {
            throw new DbUpdateConcurrencyException("The cover design changed.");
        }
        var template = await TemplateAsync(edition, design, cancellationToken);
        ValidateScene(scene, edition, template);
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
        design.CompositionSceneJson = System.Text.Json.JsonSerializer.Serialize(scene, ManuscriptCodec.JsonOptions);
        design.Revision = checked(design.Revision + 1);
        design.UpdatedAt = DateTime.UtcNow;
        edition.SelectedCoverImageId = CompositionSceneResolver.Flatten(scene)
            .Where(item => item.Visible && item.Kind == CompositionObjectKind.Image && item.ImageId is not null)
            .OrderBy(item => item.ZIndex)
            .Select(item => item.ImageId)
            .FirstOrDefault();
        edition.Revision = checked(edition.Revision + 1);
        edition.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await ViewAsync(edition, design, cancellationToken);
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
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var edition = await GetEditionAsync(projectId, editionId, cancellationToken, tracked: true);
        PublicationEditionService.EnsureDraft(edition);
        var design = await db.PublicationCoverDesigns.FirstOrDefaultAsync(item => item.EditionId == editionId, cancellationToken)
            ?? Default(edition);
        if (design.Revision != expectedRevision)
            throw new DbUpdateConcurrencyException("The cover composition changed.");
        var template = await TemplateAsync(edition, design, cancellationToken);
        ValidateScene(scene, edition, template);
        await ValidateSceneAssetsAsync(projectId, scene, cancellationToken);
        if (db.Entry(design).State == EntityState.Detached)
            db.PublicationCoverDesigns.Add(design);
        design.CompositionSceneJson = System.Text.Json.JsonSerializer.Serialize(scene, ManuscriptCodec.JsonOptions);
        design.Revision = checked(design.Revision + 1);
        design.UpdatedAt = DateTime.UtcNow;
        edition.SelectedCoverImageId = CompositionSceneResolver.Flatten(scene)
            .Where(item => item.Visible && item.Kind == CompositionObjectKind.Image && item.ImageId is not null)
            .OrderBy(item => item.ZIndex)
            .Select(item => item.ImageId)
            .FirstOrDefault();
        edition.Revision = checked(edition.Revision + 1);
        edition.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await ViewAsync(edition, design, cancellationToken);
    }

    public async Task<CompositionMutationStage> StageSceneAsync(
        Guid projectId,
        Guid conversationId,
        Guid editionId,
        long expectedRevision,
        CompositionScene scene,
        CancellationToken cancellationToken = default)
    {
        if (conversationId == Guid.Empty)
            throw new ArgumentException("A conversation is required for staged cover changes.", nameof(conversationId));
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        await db.CompositionMutationStages
            .Where(item => item.ProjectId == projectId
                && (item.ExpiresAt <= DateTime.UtcNow || item.AppliedAt != null))
            .ExecuteDeleteAsync(cancellationToken);
        var edition = await GetEditionAsync(projectId, editionId, cancellationToken);
        var design = await db.PublicationCoverDesigns.AsNoTracking()
            .FirstOrDefaultAsync(item => item.EditionId == editionId, cancellationToken) ?? Default(edition);
        if (design.Revision != expectedRevision)
            throw new DbUpdateConcurrencyException("The cover composition changed.");
        var template = await TemplateAsync(edition, design, cancellationToken);
        ValidateScene(scene, edition, template);
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
        var edition = await GetEditionAsync(projectId, stage.TargetId, cancellationToken, tracked: true);
        PublicationEditionService.EnsureDraft(edition);
        var design = await db.PublicationCoverDesigns.FirstOrDefaultAsync(item => item.EditionId == edition.Id, cancellationToken)
            ?? Default(edition);
        if (design.Revision != expectedRevision)
            throw new DbUpdateConcurrencyException("The cover composition changed after it was staged.");
        var template = await TemplateAsync(edition, design, cancellationToken);
        ValidateScene(scene, edition, template);
        await ValidateSceneAssetsAsync(projectId, scene, cancellationToken);
        if (db.Entry(design).State == EntityState.Detached)
            db.PublicationCoverDesigns.Add(design);
        design.CompositionSceneJson = System.Text.Json.JsonSerializer.Serialize(scene, ManuscriptCodec.JsonOptions);
        design.Revision = checked(design.Revision + 1);
        design.UpdatedAt = DateTime.UtcNow;
        edition.SelectedCoverImageId = CompositionSceneResolver.Flatten(scene)
            .Where(item => item.Visible && item.Kind == CompositionObjectKind.Image && item.ImageId is not null)
            .OrderBy(item => item.ZIndex)
            .Select(item => item.ImageId)
            .FirstOrDefault();
        edition.Revision = checked(edition.Revision + 1);
        edition.UpdatedAt = DateTime.UtcNow;
        db.CompositionMutationStages.Remove(stage);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await ViewAsync(edition, design, cancellationToken);
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
        CancellationToken cancellationToken)
    {
        var template = await TemplateAsync(edition, design, cancellationToken);
        var diagnostics = new List<string>();
        if (edition.Format == PublicationEditionFormat.Paperback && template.PageCount <= 0)
            diagnostics.Add("Generate a current interior PDF before producing the full-wrap cover so its spine width is exact.");
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
            diagnostics.Add("Cover geometry changed; acknowledge the current template before proof approval.");
        var scene = string.IsNullOrWhiteSpace(design.CompositionSceneJson)
            ? CoverCompositionFactory.Create(edition, design, template.PageCount)
            : System.Text.Json.JsonSerializer.Deserialize<CompositionScene>(design.CompositionSceneJson, ManuscriptCodec.JsonOptions)
                ?? CoverCompositionFactory.Create(edition, design, template.PageCount);
        var expectedGeometry = CoverCompositionFactory.Geometry(edition, template.PageCount);
        if (Math.Abs(scene.Surface.WidthPoints - expectedGeometry.WidthPoints) > .01
            || Math.Abs(scene.Surface.HeightPoints - expectedGeometry.HeightPoints) > .01)
        {
            var oldPageCount = EstimatePageCount(edition, scene.Surface.WidthPoints);
            scene = CoverCompositionFactory.Reflow(edition, design, scene, oldPageCount, template.PageCount);
            diagnostics.Add("Cover geometry was recalculated. Review constraint-bound objects and save the composition.");
        }
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
            diagnostics);
    }

    private static void AddSceneDiagnostics(
        PublicationEdition edition,
        CoverGeometry geometry,
        CompositionScene scene,
        List<string> diagnostics)
    {
        if (edition.Vendor == PublicationVendor.IngramSpark
            && CompositionSceneResolver.FindPdfxTransparencyOverlap(scene) is { } opacityOverlap)
        {
            diagnostics.Add($"Object {opacityOverlap.TransparentObjectId:N} uses opacity over lower object {opacityOverlap.LowerObjectId:N}. PDF/X-1a requires it to be opaque or precomposed as one image.");
        }
        var barcode = CoverCompositionFactory.RegionBoundsPercent(CompositionRegionConstraint.BarcodeReserve, geometry);
        foreach (var item in CompositionSceneResolver.Flatten(scene).Where(item => item.Visible))
        {
            if (item.Kind == CompositionObjectKind.Text && string.IsNullOrWhiteSpace(item.TextBinding))
                diagnostics.Add($"Text object {item.Id:N} requires a canonical cover-copy binding.");
            if (item.Kind == CompositionObjectKind.Image && item.AccessibilityDecisionPending)
                diagnostics.Add($"Image object {item.Id:N} requires alternative text or an explicit decorative decision.");
            if (!item.Decorative && item.SemanticRole != CompositionSemanticRole.Artifact
                && item.RegionConstraint != CompositionRegionConstraint.BarcodeReserve
                && edition.Format == PublicationEditionFormat.Paperback
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
        CancellationToken cancellationToken)
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
        var bleed = edition.Bleed ? 0.125 : 0;
        var caliper = edition.Paper == PublicationPaper.Cream ? 0.0025 : 0.002252;
        var spine = pages * caliper;
        var width = edition.PageWidthInches * 2 + spine + bleed * 2;
        var height = edition.PageHeightInches + bleed * 2;
        var source = $"{edition.Vendor}|{edition.VendorProfileVersion}|{pages}|{edition.PageWidthInches:R}|{edition.PageHeightInches:R}|{bleed:R}|{caliper:R}";
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        return new(pages, edition.PageWidthInches, edition.PageHeightInches, bleed, spine, width, height, 0.25, 2, 1.2, fingerprint,
            string.Equals(fingerprint, design.AcknowledgedTemplateFingerprint, StringComparison.Ordinal));
    }

    private async Task<PublicationEdition> GetEditionAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken,
        bool tracked = false)
    {
        var editions = tracked ? db.PublicationEditions : db.PublicationEditions.AsNoTracking();
        return await editions.FirstOrDefaultAsync(
            edition => edition.Id == editionId && edition.ProjectId == projectId,
            cancellationToken) ?? throw new KeyNotFoundException("Publication edition not found.");
    }

    private static PublicationCoverDesign Default(PublicationEdition edition)
    {
        var design = new PublicationCoverDesign
        {
            EditionId = edition.Id,
            Title = edition.TitleOverride,
            Subtitle = edition.Subtitle,
            Author = edition.Author,
            SpineText = edition.TitleOverride,
            BackCopy = edition.Description,
            BarcodeMode = edition.Format == PublicationEditionFormat.Paperback
                ? PublicationBarcodeMode.LorekeeperBarcode
                : PublicationBarcodeMode.None,
        };
        design.CompositionSceneJson = System.Text.Json.JsonSerializer.Serialize(
            CoverCompositionFactory.Create(edition, design),
            ManuscriptCodec.JsonOptions);
        return design;
    }

    private static int EstimatePageCount(PublicationEdition edition, double widthPoints)
    {
        if (edition.Format != PublicationEditionFormat.Paperback)
            return 0;
        var bleed = edition.Bleed ? 9d : 0d;
        var trim = edition.PageWidthInches * 72;
        var caliperPoints = (edition.Paper == PublicationPaper.Cream ? .0025 : .002252) * 72;
        return Math.Max(0, (int)Math.Round((widthPoints - trim * 2 - bleed * 2) / caliperPoints));
    }

    private static void ValidateScene(
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
        var readingOrder = new HashSet<int>();
        foreach (var item in scene.Objects)
        {
            if (item.Id == Guid.Empty || !objectIds.Add(item.Id) || !layerIds.Contains(item.LayerId))
                throw new InvalidDataException("Cover objects require unique IDs and an existing layer.");
            if (item.Bounds.WidthPercent <= 0 || item.Bounds.HeightPercent <= 0
                || item.Bounds.XPercent < 0 || item.Bounds.YPercent < 0
                || item.Bounds.XPercent + item.Bounds.WidthPercent > 100.001
                || item.Bounds.YPercent + item.Bounds.HeightPercent > 100.001
                || item.Opacity is < 0 or > 1)
                throw new InvalidDataException($"Cover object {item.Id:N} has invalid geometry.");
            if (item.Kind == CompositionObjectKind.Image && item.ImageId is null)
                throw new InvalidDataException($"Cover image object {item.Id:N} has no project image.");
            if (item.Kind == CompositionObjectKind.Text
                && item.TextBinding is not "title" and not "subtitle" and not "author" and not "spineText" and not "backCopy")
                throw new InvalidDataException($"Cover text object {item.Id:N} requires a supported canonical copy binding.");
            if (edition.Format != PublicationEditionFormat.Paperback
                && item.Kind == CompositionObjectKind.Text
                && item.TextBinding is ("spineText" or "backCopy"))
                throw new InvalidDataException($"Digital cover text object {item.Id:N} uses a print-only canonical binding.");
            if (edition.Format != PublicationEditionFormat.Paperback
                && item.RegionConstraint is not CompositionRegionConstraint.Page
                    and not CompositionRegionConstraint.SafeArea
                    and not CompositionRegionConstraint.Front)
                throw new InvalidDataException($"Digital cover object {item.Id:N} uses a print-only region constraint.");
            if (item.StyleId is Guid styleId && !styleIds.Contains(styleId))
                throw new InvalidDataException($"Cover object {item.Id:N} references a missing object style.");
            if (item.Kind == CompositionObjectKind.Image && !item.Decorative
                && string.IsNullOrWhiteSpace(item.AltText) && !item.AccessibilityDecisionPending)
                throw new InvalidDataException($"Cover image object {item.Id:N} requires alternative text or a decorative decision.");
            if (!item.Decorative && item.SemanticRole != CompositionSemanticRole.Artifact
                && (item.ReadingOrder is null || !readingOrder.Add(item.ReadingOrder.Value)))
                throw new InvalidDataException($"Cover object {item.Id:N} requires a unique logical reading order.");
        }
    }

    private static void Validate(PublicationCoverDesignUpdate update)
    {
        if (!Enum.IsDefined(update.BarcodeMode))
            throw new ArgumentException("Barcode mode is invalid.");
        if (update.Title.Trim().Length is < 1 or > 160
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

    private static void ValidateProduct(PublicationCoverDesignUpdate update, PublicationEdition edition)
    {
        if (edition.Format != PublicationEditionFormat.Paperback
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
