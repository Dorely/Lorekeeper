using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lorekeeper.Authoring;
using Lorekeeper.EditorChat;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Publish;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace Lorekeeper.Composition;

public interface IDesignedPageService
{
    Task<DesignedPageView> CreateAsync(Guid projectId, EditorContentTarget target, string name, DesignedPageInitialContent? initialContent = null, CancellationToken cancellationToken = default);
    Task<DesignedPageCreationResult> CreateDesignedPageAsync(EditorContentTarget target, Guid projectId, Guid chapterId, int blockIndex, string name, long expectedRevision, DesignedPageInitialContent? initialContent = null, CancellationToken cancellationToken = default);
    Task<DesignedPageCreationResult> CreateDesignedPageAsync(EditorContentTarget target, Guid projectId, Guid chapterId, int blockIndex, string name, long expectedRevision, DesignedPageIdentity identity, DesignedPageInitialContent? initialContent = null, CancellationToken cancellationToken = default);
    Task<DesignedPagePlacementMutationResult> CreateAndPlaceAsync(EditorContentTarget target, Guid projectId, DesignedPageContainer container, int blockIndex, string name, long expectedRevision, DesignedPageInitialContent? initialContent = null, IReadOnlyList<ManuscriptBlock>? semanticBlocks = null, CompositionScene? authoredScene = null, bool replaceContainerContent = false, CancellationToken cancellationToken = default);
    Task<DesignedPageView?> GetAsync(Guid projectId, Guid pageId, EditorContentTarget target, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DesignedPageView>> ListAsync(Guid projectId, EditorContentTarget target, CancellationToken cancellationToken = default);
    Task<DesignedPageVariant> GetOrCreateAuthoringVariantAsync(Guid projectId, Guid contentId, CancellationToken cancellationToken = default);
    Task<DesignedPageVariant> PreviewEditionVariantAsync(Guid projectId, Guid contentId, Guid editionId, CancellationToken cancellationToken = default);
    Task<DesignedPageVariant> SelectAuthoringVariantAsync(Guid projectId, Guid contentId, Guid variantId, CancellationToken cancellationToken = default);
    Task<DesignedPageVariant> GetOrCreateVariantAsync(Guid projectId, Guid contentId, Guid editionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DesignedPageVariant>> ListVariantsAsync(Guid projectId, Guid contentId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DesignedPageVariant>> ListVariantsAsync(Guid projectId, Guid contentId, Guid editionId, CancellationToken cancellationToken = default);
    Task<DesignedPageVariant> ReadVariantAsync(Guid projectId, Guid variantId, CancellationToken cancellationToken = default);
    Task<DesignedPageVariant> SelectVariantAsync(Guid projectId, Guid contentId, Guid editionId, Guid variantId, CancellationToken cancellationToken = default);
    Task<DesignedPageVariant> SaveVariantAsync(EditorContentTarget target, Guid projectId, Guid variantId, long expectedRevision, CompositionScene scene, CancellationToken cancellationToken = default);
    Task<DesignedPageVariant> PatchElementAsync(EditorContentTarget target, Guid projectId, Guid variantId, long expectedRevision, string targetKind, Guid targetId, CompositionElementPatch patch, CancellationToken cancellationToken = default);
    Task<CompositionImagePlacementResult> AddImageObjectAsync(EditorContentTarget target, Guid projectId, Guid variantId, long expectedRevision, Guid imageId, FigureImageFit fit, string? altText, bool decorative, CompositionBounds? bounds = null, int? readingOrder = null, CancellationToken cancellationToken = default);
    Task<DesignedPageWorkspaceSaveResult> SaveWorkspaceAsync(EditorContentTarget target, Guid projectId, Guid contentId, long expectedContentRevision, IReadOnlyList<ManuscriptBlock> semanticBlocks, Guid variantId, long expectedVariantRevision, CompositionScene scene, CancellationToken cancellationToken = default);
    Task<DesignedPageSemanticMutationResult> ApplyAuthoringOperationsAsync(EditorContentTarget target, Guid projectId, Guid contentId, long expectedRevision, IReadOnlyList<ManuscriptOperation> operations, CancellationToken cancellationToken = default);
    Task<DesignedPageSemanticMutationResult> ReplaceAuthoringDocumentAsync(EditorContentTarget target, Guid projectId, Guid contentId, long expectedRevision, ManuscriptDocument document, CancellationToken cancellationToken = default);
    Task RefreshAuthoringDerivedStateAsync(EditorContentTarget target, Guid projectId, Guid contentId, CancellationToken cancellationToken = default);
    Task<CompositionMutationStage> StageVariantAsync(EditorContentTarget target, Guid projectId, Guid conversationId, Guid variantId, long expectedRevision, CompositionScene scene, CancellationToken cancellationToken = default);
    Task<DesignedPageVariant> ApplyStageAsync(EditorContentTarget target, Guid projectId, Guid conversationId, Guid stageId, long expectedRevision, CancellationToken cancellationToken = default);
    Task<CompositionMutationStage> StageSemanticOperationsAsync(EditorContentTarget target, Guid projectId, Guid conversationId, Guid contentId, long expectedRevision, IReadOnlyList<ManuscriptOperationInput> operations, CancellationToken cancellationToken = default);
    Task<DesignedPageSemanticMutationResult> ApplySemanticStageAsync(EditorContentTarget target, Guid projectId, Guid conversationId, Guid stageId, long expectedRevision, CancellationToken cancellationToken = default);
    Task<CompositionMutationStage> StageWorkspaceAsync(EditorContentTarget target, Guid projectId, Guid conversationId, Guid contentId, long expectedContentRevision, Guid variantId, long expectedVariantRevision, IReadOnlyList<ManuscriptOperationInput> semanticOperations, CompositionScene scene, CancellationToken cancellationToken = default);
    Task<DesignedPageWorkspaceMutationResult> ApplyWorkspaceStageAsync(EditorContentTarget target, Guid projectId, Guid conversationId, Guid stageId, long expectedContentRevision, CancellationToken cancellationToken = default);
    Task<CompositionEditionGeometry> GetEditionGeometryAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken = default);
    Task<CompositionEditionGeometry> GetAuthoringGeometryAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<LayoutGenerationTargetDescriptor> DescribeAuthoringGenerationTargetAsync(Guid projectId, string targetKind, Guid targetId, Guid? variantId = null, CancellationToken cancellationToken = default, CompositionBounds? surfaceBounds = null);
    Task<LayoutGenerationTargetDescriptor> DescribeGenerationTargetAsync(Guid projectId, Guid editionId, string targetKind, Guid targetId, Guid? variantId = null, CancellationToken cancellationToken = default, CompositionBounds? surfaceBounds = null);
    Task<LayoutValidationView> ValidateAuthoringVariantAsync(Guid projectId, Guid variantId, CancellationToken cancellationToken = default);
    Task<LayoutValidationView> ValidateVariantAsync(Guid projectId, Guid editionId, Guid variantId, CancellationToken cancellationToken = default);
    Task<DesignedPagePlacementMutationResult> PlaceAsync(EditorContentTarget target, Guid projectId, Guid pageId, DesignedPageContainer container, int blockIndex, long expectedRevision, string? placementId = null, CancellationToken cancellationToken = default);
    Task<DesignedPagePlacementMutationResult> RemovePlacementAsync(EditorContentTarget target, Guid projectId, string placementId, DesignedPageContainer container, long expectedRevision, CancellationToken cancellationToken = default);
    Task<DesignedPagePlacementMutationResult> MovePlacementAsync(EditorContentTarget target, Guid projectId, string placementId, DesignedPageContainer source, long expectedSourceRevision, DesignedPageContainer destination, int destinationIndex, long expectedDestinationRevision, CancellationToken cancellationToken = default);
    Task<DesignedPageView> DuplicateAsync(Guid projectId, Guid pageId, EditorContentTarget target, string? name = null, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid projectId, Guid pageId, bool clearAffectedHistory = false, CancellationToken cancellationToken = default);
    Task<DesignedPageView> EnsureReleaseOverrideAsync(Guid projectId, Guid pageId, Guid editionId, CancellationToken cancellationToken = default);
    Task<DesignedPageView> ResetReleaseOverrideAsync(Guid projectId, Guid pageId, Guid editionId, CancellationToken cancellationToken = default);
}

public sealed class DesignedPageService(
    IAppDatabaseOperationFactory database,
    IManuscriptService manuscripts,
    IPublicationCoverService covers,
    IPublicationEffectiveConfigurationResolver effectiveConfigurations,
    IAuthoringDeltaHistoryRuntime deltaHistory,
    IAuthoringMutationContextAccessor authoringMutationContext,
    IAuthoringGenerationService authoringGenerations,
    ICompositionCanvasPreviewService canvasPreviews,
    IEditorContestMutationGuard contestGuard) : IDesignedPageService
{
    private static readonly JsonSerializerOptions JsonOptions = ManuscriptCodec.JsonOptions;

    private async Task EnsureChapterMutationAllowedAsync(
        Guid projectId,
        DesignedPageContent composition,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        await EnsureEditableTargetAsync(
            operation.Db,
            projectId,
            composition.EditionId is Guid editionId
                ? EditorContentTarget.ForEdition(editionId)
                : EditorContentTarget.Core,
            cancellationToken);
        if (await operation.Db.DesignedPagePlacementReferences.AsNoTracking().AnyAsync(
            item => item.ProjectId == projectId
                && item.DesignedPageId == composition.DesignedPageId
                && item.ContainerKind == DesignedPageContainerKind.Chapter,
            cancellationToken))
        {
            await contestGuard.EnsureMutationAllowedAsync(projectId, cancellationToken);
        }
    }

    private async Task RecordCompositionMutationAsync(
        Guid projectId,
        DesignedPageContent composition,
        string label,
        CancellationToken cancellationToken)
    {
        if (authoringMutationContext.IsHistorySuppressed)
            return;
        await authoringGenerations.InvalidateAsync(
            projectId,
            [composition.EditionId is Guid editionId
                ? $"release:{editionId:D}:designed-page-content:{composition.Id:D}"
                : $"designed-page-content:{composition.Id:D}"],
            cancellationToken);
    }

    public async Task<DesignedPageCreationResult> CreateDesignedPageAsync(
        EditorContentTarget target,
        Guid projectId,
        Guid chapterId,
        int blockIndex,
        string name,
        long expectedRevision,
        DesignedPageInitialContent? initialContent = null,
        CancellationToken cancellationToken = default) =>
        await CreateDesignedPageCoreAsync(target, projectId, chapterId, blockIndex, name, expectedRevision, null, initialContent, cancellationToken);

    public async Task<DesignedPageCreationResult> CreateDesignedPageAsync(
        EditorContentTarget target,
        Guid projectId,
        Guid chapterId,
        int blockIndex,
        string name,
        long expectedRevision,
        DesignedPageIdentity identity,
        DesignedPageInitialContent? initialContent = null,
        CancellationToken cancellationToken = default) =>
        await CreateDesignedPageCoreAsync(target, projectId, chapterId, blockIndex, name, expectedRevision, identity, initialContent, cancellationToken);

    public async Task<DesignedPagePlacementMutationResult> CreateAndPlaceAsync(
        EditorContentTarget target,
        Guid projectId,
        DesignedPageContainer container,
        int blockIndex,
        string name,
        long expectedRevision,
        DesignedPageInitialContent? initialContent = null,
        IReadOnlyList<ManuscriptBlock>? semanticBlocks = null,
        CompositionScene? authoredScene = null,
        bool replaceContainerContent = false,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenWriteAsync(projectId, cancellationToken);
        operation.ShareWithNestedOperations();
        if (container.Kind == DesignedPageContainerKind.Chapter)
            await contestGuard.EnsureMutationAllowedAsync(projectId, cancellationToken);
        var db = operation.Db;
        await EnsureEditableTargetAsync(db, projectId, target, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var page = new DesignedPage
        {
            ProjectId = projectId,
            Name = string.IsNullOrWhiteSpace(name) ? "Designed page" : name.Trim(),
            ScopeEditionId = target.EditionId,
        };
        var content = new DesignedPageContent
        {
            Id = page.Id,
            ProjectId = projectId,
            DesignedPageId = page.Id,
            Page = page,
            EditionId = target.EditionId,
        };
        var semantic = new ManuscriptDocument
        {
            ManuscriptId = content.Id,
            Content = semanticBlocks?.ToList() ?? [],
        };
        content.SemanticManuscriptJson = ManuscriptCodec.Serialize(semantic);
        var scene = authoredScene ?? (target.EditionId is Guid editionId
            ? await CreateInitialPageSceneAsync(
                projectId,
                (await effectiveConfigurations.ResolveReleaseAsync(projectId, editionId, cancellationToken)).Edition,
                initialContent,
                cancellationToken)
            : await CreateInitialPageSceneAsync(
                projectId,
                await RequirePageSetupUnderLeaseAsync(projectId, cancellationToken),
                initialContent,
                cancellationToken));
        await ValidateSceneAssetsAsync(projectId, scene, cancellationToken);
        var variant = new DesignedPageVariant
        {
            ContentId = content.Id,
            Content = content,
            GeometryKey = SceneGeometryKey(scene),
            SceneJson = SerializeAndValidate(scene, content.SemanticManuscriptJson),
        };
        var activeVariantId = variant.Id;
        content.Variants.Add(variant);
        page.Contents.Add(content);
        db.DesignedPages.Add(page);
        await db.SaveChangesAsync(cancellationToken);
        content.ActiveVariantId = activeVariantId;
        await db.SaveChangesAsync(cancellationToken);

        var placementId = Guid.NewGuid().ToString("N");
        var revision = await ApplyContainerOperationsAsync(
            db,
            target,
            projectId,
            container,
            expectedRevision,
            [new InsertManuscriptBlock(blockIndex, ManuscriptBlockType.DesignedPage, string.Empty,
                ManuscriptStyleRoles.DesignedPage, DesignedPageId: page.Id, BlockId: placementId)],
            cancellationToken,
            replaceContainerContent);
        var historyStage = await StageContainerHistoryAsync(
            db, projectId, revision, "Create and place Designed Page", cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (historyStage is not null)
                deltaHistory.Discard(historyStage.StageId);
            throw;
        }
        ConfirmContainerHistory(historyStage);
        if (container.Kind == DesignedPageContainerKind.Chapter)
            await manuscripts.RefreshDerivedStateAsync(target, container.Id, cancellationToken);
        return new DesignedPagePlacementMutationResult(new DesignedPageView(page, content, false), placementId, [revision]);
    }

    private async Task<DesignedPageCreationResult> CreateDesignedPageCoreAsync(
        EditorContentTarget target,
        Guid projectId,
        Guid chapterId,
        int blockIndex,
        string name,
        long expectedRevision,
        DesignedPageIdentity? identity,
        DesignedPageInitialContent? initialContent,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        await contestGuard.EnsureMutationAllowedAsync(projectId, cancellationToken);
        var db = databaseOperation.Db;
        await EnsureEditableTargetAsync(db, projectId, target, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var chapter = await db.Chapters.AsNoTracking().SingleOrDefaultAsync(item => item.Id == chapterId && item.ProjectId == projectId, cancellationToken)
            ?? throw new KeyNotFoundException("Chapter was not found in this project.");
        var effective = await manuscripts.GetManuscriptAsync(target, chapterId, cancellationToken)
            ?? throw new KeyNotFoundException("Chapter manuscript was not found in this project.");
        if (effective.Revision != expectedRevision)
            throw new ManuscriptRevisionConflictException(expectedRevision, effective.Revision);
        var pageId = identity?.PageId ?? Guid.NewGuid();
        var page = new DesignedPage
        {
            Id = pageId,
            ProjectId = projectId,
            Name = string.IsNullOrWhiteSpace(name) ? "Designed page" : name.Trim(),
            ScopeEditionId = target.EditionId,
        };
        var composition = new DesignedPageContent
        {
            Id = pageId,
            ProjectId = projectId,
            DesignedPageId = pageId,
            Page = page,
            EditionId = target.EditionId,
        };
        composition.SemanticManuscriptJson = ManuscriptCodec.Serialize(ManuscriptCodec.CreateEmpty(composition.Id));
        db.DesignedPages.Add(page);
        db.DesignedPageContents.Add(composition);
        var setup = target.EditionId is Guid editionId
            ? (await effectiveConfigurations.ResolveReleaseAsync(projectId, editionId, cancellationToken)).Edition
            : null;
        var scene = setup is null
            ? await CreateInitialPageSceneAsync(projectId, await RequirePageSetupUnderLeaseAsync(projectId, cancellationToken), initialContent, cancellationToken)
            : await CreateInitialPageSceneAsync(projectId, setup, initialContent, cancellationToken);
        var variant = new DesignedPageVariant
        {
            Content = composition,
            ContentId = composition.Id,
            GeometryKey = SceneGeometryKey(scene),
            SceneJson = SerializeAndValidate(scene, composition.SemanticManuscriptJson),
        };
        var activeVariantId = variant.Id;
        db.DesignedPageVariants.Add(variant);
        await db.SaveChangesAsync(cancellationToken);
        composition.ActiveVariantId = activeVariantId;
        await db.SaveChangesAsync(cancellationToken);
        var container = new DesignedPageContainer(DesignedPageContainerKind.Chapter, chapterId);
        var beforeDocument = effective.Document;
        ManuscriptMutationResult manuscript;
        using (authoringMutationContext.SuppressHistory())
        {
            manuscript = await manuscripts.ApplyPersistedUnderProjectMutationLeaseAsync(
                target,
                chapterId,
                expectedRevision,
                [new InsertManuscriptBlock(
                    blockIndex,
                    ManuscriptBlockType.DesignedPage,
                    string.Empty,
                    ManuscriptStyleRoles.DesignedPage,
                    DesignedPageId: page.Id,
                    BlockId: identity?.PlacementId)],
                cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
        var historyStage = await StageContainerHistoryAsync(
            db,
            projectId,
            new DesignedPageContainerRevision(
                container,
                manuscript.Snapshot.Revision,
                manuscript.Snapshot.Document,
                beforeDocument,
                ContainerTargetId(target, container)),
            "Create and place Designed Page",
            cancellationToken);
        try
        {
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (historyStage is not null)
                deltaHistory.Discard(historyStage.StageId);
            throw;
        }
        ConfirmContainerHistory(historyStage);
        if (chapterId is Guid owningChapterId)
            await manuscripts.RefreshDerivedStateAsync(target, owningChapterId, cancellationToken);
        return new DesignedPageCreationResult(page, composition, variant, manuscript.Snapshot, manuscript.ChangedBlockIds.Single());
    }

    private async Task<CompositionScene> CreateInitialPageSceneAsync(
        Guid projectId,
        PublicationEdition edition,
        DesignedPageInitialContent? initialContent,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var layoutMode = initialContent?.LayoutMode ?? DesignedPageLayoutMode.SinglePage;
        var scene = CreatePageScene(edition, layoutMode);
        if (initialContent?.ImageId is not Guid imageId)
            return scene;

        var image = await db.PublishAssets.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == imageId
                && item.ProjectId == projectId
                && (item.ContentType == "image/png" || item.ContentType == "image/jpeg"),
            cancellationToken) ?? throw new KeyNotFoundException("The initial Designed Page artwork was not found in this project.");
        var altText = initialContent.Decorative
            ? string.Empty
            : string.IsNullOrWhiteSpace(initialContent.AltText)
                ? image.AltText.Trim()
                : initialContent.AltText.Trim();
        var layer = scene.Layers.Single();
        scene = scene with
        {
            Objects =
            [
                new CompositionObject
                {
                    Id = Guid.NewGuid(),
                    LayerId = layer.Id,
                    Kind = CompositionObjectKind.Image,
                    Name = image.FileName,
                    ImageId = image.Id,
                    ImageFit = initialContent.ImageFit,
                    CropXPercent = Math.Clamp(initialContent.CropXPercent, 0, 100),
                    CropYPercent = Math.Clamp(initialContent.CropYPercent, 0, 100),
                    AltText = altText,
                    Decorative = initialContent.Decorative,
                    AccessibilityDecisionPending = !initialContent.Decorative && string.IsNullOrWhiteSpace(altText),
                    SemanticRole = initialContent.Decorative
                        ? CompositionSemanticRole.Artifact
                        : CompositionSemanticRole.Figure,
                    ReadingOrder = initialContent.Decorative ? null : 1,
                },
            ],
        };
        await ValidateSceneAssetsAsync(projectId, scene, cancellationToken);
        ValidateVariantGeometry(edition, scene);
        return scene;
    }

    private async Task<CompositionScene> CreateInitialPageSceneAsync(
        Guid projectId,
        ProjectPageSetup setup,
        DesignedPageInitialContent? initialContent,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var scene = CreatePageScene(setup, initialContent?.LayoutMode ?? DesignedPageLayoutMode.SinglePage);
        if (initialContent?.ImageId is not Guid imageId)
            return scene;

        var image = await db.PublishAssets.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == imageId && item.ProjectId == projectId
                && (item.ContentType == "image/png" || item.ContentType == "image/jpeg"),
            cancellationToken) ?? throw new KeyNotFoundException("The initial Designed Page artwork was not found in this project.");
        var altText = initialContent.Decorative
            ? string.Empty
            : string.IsNullOrWhiteSpace(initialContent.AltText) ? image.AltText.Trim() : initialContent.AltText.Trim();
        var layer = scene.Layers.Single();
        scene = scene with
        {
            Objects =
            [
                new CompositionObject
                {
                    Id = Guid.NewGuid(),
                    LayerId = layer.Id,
                    Kind = CompositionObjectKind.Image,
                    Name = image.FileName,
                    ImageId = image.Id,
                    ImageFit = initialContent.ImageFit,
                    CropXPercent = Math.Clamp(initialContent.CropXPercent, 0, 100),
                    CropYPercent = Math.Clamp(initialContent.CropYPercent, 0, 100),
                    AltText = altText,
                    Decorative = initialContent.Decorative,
                    AccessibilityDecisionPending = !initialContent.Decorative && string.IsNullOrWhiteSpace(altText),
                    SemanticRole = initialContent.Decorative ? CompositionSemanticRole.Artifact : CompositionSemanticRole.Figure,
                    ReadingOrder = initialContent.Decorative ? null : 1,
                },
            ],
        };
        await ValidateSceneAssetsAsync(projectId, scene, cancellationToken);
        return scene;
    }

    public async Task<DesignedPageVariant> GetOrCreateAuthoringVariantAsync(
        Guid projectId,
        Guid contentId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var current = await db.DesignedPageContents.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == contentId && item.ProjectId == projectId, cancellationToken)
            ?? throw new KeyNotFoundException("Designed Page content was not found in this project.");
        await EnsureChapterMutationAllowedAsync(projectId, current, cancellationToken);
        if (current.EditionId is not null)
            throw new InvalidOperationException("Edition-owned Designed Pages require the selected release geometry.");
        var targetSetup = await ResolveCompositionSetupAsync(current, cancellationToken);
        var latestVariantId = await db.DesignedPageVariants.AsNoTracking()
            .Where(item => item.ContentId == contentId)
            .OrderByDescending(item => item.UpdatedAt)
            .Select(item => (Guid?)item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (current.ActiveVariantId is Guid currentActiveId
            && latestVariantId == currentActiveId)
        {
            var active = await ReadVariantAsync(projectId, currentActiveId, cancellationToken);
            if (targetSetup is not null)
            {
                var activeScene = JsonSerializer.Deserialize<CompositionScene>(active.SceneJson, JsonOptions)
                    ?? throw new InvalidDataException("The composition scene is empty.");
                var adapted = AdaptAuthoringScene(activeScene, targetSetup.PageWidthInches, targetSetup.PageHeightInches, targetSetup.PageMarginInches);
                if (string.Equals(active.GeometryKey, SceneGeometryKey(adapted), StringComparison.Ordinal))
                    return active;
            }
        }

        var composition = await db.DesignedPageContents.AsTracking()
            .Include(item => item.Variants)
            .SingleOrDefaultAsync(item => item.Id == contentId && item.ProjectId == projectId, cancellationToken)
            ?? throw new KeyNotFoundException("Designed Page content was not found in this project.");
        await EnsureChapterMutationAllowedAsync(projectId, composition, cancellationToken);
        if (composition.EditionId is not null)
            throw new InvalidOperationException("Edition-owned Designed Pages cannot select a Core authoring variant.");
        var setupUnderLease = await ResolveCompositionSetupAsync(composition, cancellationToken)
            ?? await RequirePageSetupUnderLeaseAsync(projectId, cancellationToken);
        // Before active-layout persistence was enforced, changing surface geometry could
        // save a newer variant without updating ActiveVariantId. Treat the most
        // recently edited variant as the user's selected authoring layout and repair the
        // active pointer while normalizing it.
        var existing = composition.Variants.OrderByDescending(item => item.UpdatedAt).FirstOrDefault();
        if (existing is not null)
            return await NormalizeAuthoringVariantAsync(composition, existing, setupUnderLease, cancellationToken);

        var scene = CreatePageScene(setupUnderLease);
        var variant = new DesignedPageVariant
        {
            ContentId = composition.Id,
            GeometryKey = SceneGeometryKey(scene),
            SceneJson = SerializeAndValidate(scene, composition.SemanticManuscriptJson),
        };
        db.DesignedPageVariants.Add(variant);
        composition.ActiveVariantId = variant.Id;
        composition.UpdatedAt = DateTime.UtcNow;
        await TouchProjectAsync(projectId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return variant;
    }

    private async Task<DesignedPageVariant> NormalizeAuthoringVariantAsync(
        DesignedPageContent composition,
        DesignedPageVariant source,
        ProjectPageSetup setup,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(composition.ProjectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        await EnsureChapterMutationAllowedAsync(composition.ProjectId, composition, cancellationToken);
        var db = databaseOperation.Db;
        var scene = JsonSerializer.Deserialize<CompositionScene>(source.SceneJson, JsonOptions)
            ?? throw new InvalidDataException("The composition scene is empty.");
        var adapted = AdaptAuthoringScene(scene, setup.PageWidthInches, setup.PageHeightInches, setup.PageMarginInches);
        var geometryKey = SceneGeometryKey(adapted);
        var sceneJson = SerializeAndValidate(adapted, composition.SemanticManuscriptJson);
        var target = composition.Variants.SingleOrDefault(item =>
            item.Id != source.Id && string.Equals(item.GeometryKey, geometryKey, StringComparison.Ordinal));
        if (target is null
            && string.Equals(source.GeometryKey, geometryKey, StringComparison.Ordinal)
            && string.Equals(source.SceneJson, sceneJson, StringComparison.Ordinal))
        {
            composition.ActiveVariantId = source.Id;
            composition.UpdatedAt = DateTime.UtcNow;
            await TouchProjectAsync(composition.ProjectId, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return source;
        }
        if (target is null)
        {
            target = source;
            target.GeometryKey = geometryKey;
        }

        target.SceneJson = sceneJson;
        target.Revision = checked(target.Revision + 1);
        target.UpdatedAt = DateTime.UtcNow;
        composition.ActiveVariantId = target.Id;
        composition.UpdatedAt = DateTime.UtcNow;
        await TouchProjectAsync(composition.ProjectId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return target;
    }

    private async Task<ProjectPageSetup> RequirePageSetupUnderLeaseAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var setup = await db.ProjectPageSetups.SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken);
        if (setup is not null)
            return setup;
        setup = new ProjectPageSetup { ProjectId = projectId };
        db.ProjectPageSetups.Add(setup);
        return setup;
    }

    private async Task<ProjectPageSetup?> ResolveCompositionSetupAsync(
        DesignedPageContent composition,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(composition.ProjectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        if (composition.EditionId is Guid editionId)
        {
            var edition = (await effectiveConfigurations.ResolveReleaseAsync(
                composition.ProjectId, editionId, cancellationToken)).Edition;
            var core = await db.ProjectPageSetups.AsNoTracking().SingleOrDefaultAsync(
                item => item.ProjectId == composition.ProjectId,
                cancellationToken) ?? new ProjectPageSetup { ProjectId = composition.ProjectId };
            return new ProjectPageSetup
            {
                ProjectId = composition.ProjectId,
                PageWidthInches = edition.PageWidthInches,
                PageHeightInches = edition.PageHeightInches,
                PageMarginInches = edition.PageMarginInches,
                BodyFontSizePoints = core.BodyFontSizePoints,
                BodyLineHeight = core.BodyLineHeight,
                Revision = edition.Revision,
            };
        }
        return await db.ProjectPageSetups.AsNoTracking().SingleOrDefaultAsync(
            item => item.ProjectId == composition.ProjectId,
            cancellationToken);
    }

    public async Task<DesignedPageVariant> SelectAuthoringVariantAsync(
        Guid projectId,
        Guid contentId,
        Guid variantId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var composition = await db.DesignedPageContents.AsTracking()
            .Include(item => item.Variants).SingleOrDefaultAsync(
            item => item.Id == contentId && item.ProjectId == projectId,
            cancellationToken) ?? throw new KeyNotFoundException("Designed Page content was not found in this project.");
        await EnsureChapterMutationAllowedAsync(projectId, composition, cancellationToken);
        var variant = composition.Variants.SingleOrDefault(item => item.Id == variantId)
            ?? throw new KeyNotFoundException("Composition variant was not found in this project.");
        var setup = await RequirePageSetupUnderLeaseAsync(projectId, cancellationToken);
        return await NormalizeAuthoringVariantAsync(composition, variant, setup, cancellationToken);
    }

    public async Task<DesignedPageVariant> GetOrCreateVariantAsync(
        Guid projectId,
        Guid contentId,
        Guid editionId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var composition = await db.DesignedPageContents.AsTracking().SingleOrDefaultAsync(
            item => item.Id == contentId && item.ProjectId == projectId,
            cancellationToken) ?? throw new KeyNotFoundException("Designed Page content was not found in this project.");
        await EnsureChapterMutationAllowedAsync(projectId, composition, cancellationToken);
        var edition = (await effectiveConfigurations.ResolveReleaseAsync(projectId, editionId, cancellationToken)).Edition;
        if (composition.EditionId != edition.Id)
            throw new InvalidOperationException("The Designed Page does not belong to the selected release content.");
        var hasChapterPlacement = await db.DesignedPagePlacementReferences.AsNoTracking().AnyAsync(
            item => item.ProjectId == projectId
                && item.DesignedPageId == composition.DesignedPageId
                && item.ContainerKind == DesignedPageContainerKind.Chapter
                && item.EditionId == edition.Id,
            cancellationToken);
        if (edition.Status == PublicationEditionStatus.Archived
            || (!edition.EditionSpecificContentEnabled && hasChapterPlacement))
            throw new InvalidOperationException("Edition-specific content is not editable for this release.");
        var candidates = await db.DesignedPageVariants
            .Where(item => item.ContentId == composition.Id)
            .OrderByDescending(item => item.UpdatedAt)
            .ToListAsync(cancellationToken);
        var seed = await db.CompositionMutationStages.SingleOrDefaultAsync(item =>
            item.ProjectId == projectId
            && item.ConversationId == Guid.Empty
            && item.TargetKind == "designed-page-seed"
            && item.TargetId == composition.Id,
            cancellationToken);
        var authoring = candidates.FirstOrDefault(item => item.Id == composition.ActiveVariantId)
            ?? candidates.FirstOrDefault();
        if (authoring is not null && VariantMatchesEdition(authoring, edition))
        {
            if (composition.ActiveVariantId != authoring.Id || seed is not null)
            {
                composition.ActiveVariantId = authoring.Id;
                if (seed is not null)
                    db.CompositionMutationStages.Remove(seed);
                await TouchProjectAsync(projectId, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }
            return authoring;
        }

        var scene = authoring is not null
            ? AdaptSeedScene(authoring.SceneJson, edition)
            : seed is not null
                ? AdaptSeedScene(seed.OperationsJson, edition)
                : CreatePageScene(edition);
        var geometryKey = GeometryKey(edition, scene);
        var sceneJson = SerializeAndValidate(scene, composition.SemanticManuscriptJson);
        var variant = candidates.FirstOrDefault(item =>
            string.Equals(item.GeometryKey, geometryKey, StringComparison.Ordinal));
        if (variant is null)
        {
            variant = new DesignedPageVariant
            {
                ContentId = composition.Id,
                GeometryKey = geometryKey,
                SceneJson = sceneJson,
            };
            db.DesignedPageVariants.Add(variant);
        }
        else if (!string.Equals(variant.SceneJson, sceneJson, StringComparison.Ordinal))
        {
            // A release customization can inherit historical layouts for several exact
            // geometries. Materialize its release layout from the currently selected Core
            // design instead of silently reviving an older matching-geometry scene.
            variant.SceneJson = sceneJson;
            variant.Revision = checked(variant.Revision + 1);
            variant.UpdatedAt = DateTime.UtcNow;
        }
        composition.ActiveVariantId = variant.Id;
        if (seed is not null)
            db.CompositionMutationStages.Remove(seed);
        await TouchProjectAsync(projectId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return variant;
    }

    public async Task<DesignedPageVariant> PreviewEditionVariantAsync(
        Guid projectId,
        Guid contentId,
        Guid editionId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var composition = await db.DesignedPageContents.AsNoTracking()
            .Include(item => item.Variants)
            .SingleOrDefaultAsync(item => item.Id == contentId && item.ProjectId == projectId, cancellationToken)
            ?? throw new KeyNotFoundException("Designed Page content was not found in this project.");
        if (composition.EditionId is not null)
            throw new InvalidOperationException("Only an inherited Core Designed Page can use a transient release preview.");
        var edition = (await effectiveConfigurations.ResolveReleaseAsync(projectId, editionId, cancellationToken)).Edition;
        if (edition.Status == PublicationEditionStatus.Archived)
            throw new KeyNotFoundException("The editable publication release was not found.");
        var source = composition.Variants.SingleOrDefault(item => item.Id == composition.ActiveVariantId)
            ?? composition.Variants.OrderByDescending(item => item.UpdatedAt).FirstOrDefault();
        var scene = source is null
            ? CreatePageScene(edition)
            : AdaptSeedScene(source.SceneJson, edition);
        return new DesignedPageVariant
        {
            Id = source?.Id ?? Guid.NewGuid(),
            ContentId = composition.Id,
            Content = composition,
            GeometryKey = GeometryKey(edition, scene),
            SceneJson = SerializeAndValidate(scene, composition.SemanticManuscriptJson),
            Revision = source?.Revision ?? 0,
            CreatedAt = source?.CreatedAt ?? DateTime.UtcNow,
            UpdatedAt = source?.UpdatedAt ?? DateTime.UtcNow,
        };
    }

    public async Task<IReadOnlyList<DesignedPageVariant>> ListVariantsAsync(
        Guid projectId,
        Guid contentId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        return await db.DesignedPageVariants.AsNoTracking()
                    .Where(item => item.ContentId == contentId
                        && item.Content.ProjectId == projectId)
                    .OrderByDescending(item => item.UpdatedAt)
                    .ToListAsync(cancellationToken);
    }
    public async Task<IReadOnlyList<DesignedPageVariant>> ListVariantsAsync(
        Guid projectId,
        Guid contentId,
        Guid editionId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var edition = (await effectiveConfigurations.ResolveReleaseAsync(projectId, editionId, cancellationToken)).Edition;
        return (await db.DesignedPageVariants.AsNoTracking()
            .Where(item => item.ContentId == contentId
                && item.Content.ProjectId == projectId)
            .OrderByDescending(item => item.UpdatedAt)
            .ToListAsync(cancellationToken))
            .Where(item => VariantMatchesEdition(item, edition))
            .ToList();
    }

    public async Task<DesignedPageVariant> ReadVariantAsync(
        Guid projectId,
        Guid variantId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        return await db.DesignedPageVariants.AsNoTracking().Include(item => item.Content)
                    .SingleOrDefaultAsync(item => item.Id == variantId
                        && item.Content.ProjectId == projectId, cancellationToken)
                ?? throw new KeyNotFoundException("Composition variant was not found in this project.");
    }
    public async Task<DesignedPageVariant> SelectVariantAsync(
        Guid projectId,
        Guid contentId,
        Guid editionId,
        Guid variantId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var edition = (await effectiveConfigurations.ResolveReleaseAsync(projectId, editionId, cancellationToken)).Edition;
        var variant = await db.DesignedPageVariants.AsTracking().Include(item => item.Content)
            .SingleOrDefaultAsync(item => item.Id == variantId && item.ContentId == contentId
                && item.Content.ProjectId == projectId
               , cancellationToken)
            ?? throw new KeyNotFoundException("Composition variant was not found in this project.");
        await EnsureChapterMutationAllowedAsync(projectId, variant.Content, cancellationToken);
        if (!VariantMatchesEdition(variant, edition))
            throw new InvalidDataException("The selected variant is incompatible with this edition.");
        variant.Content.ActiveVariantId = variant.Id;
        variant.UpdatedAt = DateTime.UtcNow;
        await TouchProjectAsync(projectId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return variant;
    }

    internal static CompositionScene AdaptSeedScene(string sceneJson, PublicationEdition edition)
    {
        var scene = JsonSerializer.Deserialize<CompositionScene>(sceneJson, JsonOptions)
            ?? throw new InvalidDataException("The composition seed scene is empty.");
        if (scene.Surface.Kind == CompositionSurfaceKind.IndependentPage
            && edition.Format == PublicationEditionFormat.DigitalPdf
            && edition.AllowDesignedPageOverrides)
            return scene with { Surface = scene.Surface with { OutputPageMode = CompositionOutputPageMode.SingleSurface, AllowIndependentPdfPage = true } };

        return AdaptScene(
            scene,
            edition.PageWidthInches,
            edition.PageHeightInches,
            edition.PageMarginInches,
            edition.Bleed);
    }

    internal static CompositionScene AdaptAuthoringScene(
        CompositionScene scene,
        double pageWidthInches,
        double pageHeightInches,
        double pageMarginInches) =>
        AdaptScene(scene, pageWidthInches, pageHeightInches, pageMarginInches, bleed: false);

    private static CompositionScene AdaptScene(
        CompositionScene scene,
        double pageWidthInches,
        double pageHeightInches,
        double pageMarginInches,
        bool bleed)
    {
        var facing = scene.Surface.Kind == CompositionSurfaceKind.FacingSpread;
        var targetWidth = pageWidthInches * 72 * (facing ? 2 : 1);
        var targetHeight = pageHeightInches * 72;
        if (Math.Abs(scene.Surface.WidthPoints - targetWidth) <= .01
            && Math.Abs(scene.Surface.HeightPoints - targetHeight) <= .01
            && Math.Abs(scene.Surface.SafeInsetPoints - pageMarginInches * 72) <= .01
            && scene.Surface.Kind is CompositionSurfaceKind.SinglePage or CompositionSurfaceKind.FacingSpread
            && scene.Surface.OutputPageMode == CompositionOutputPageMode.EditionLeaves
            && !scene.Surface.AllowIndependentPdfPage
            && Math.Abs(scene.Surface.BleedPoints - (bleed ? 9 : 0)) <= .01)
            return scene;

        var scale = Math.Min(targetWidth / scene.Surface.WidthPoints, targetHeight / scene.Surface.HeightPoints);
        var contentWidth = scene.Surface.WidthPoints * scale;
        var contentHeight = scene.Surface.HeightPoints * scale;
        var offsetX = (targetWidth - contentWidth) / 2;
        var offsetY = (targetHeight - contentHeight) / 2;
        CompositionBounds Map(CompositionBounds bounds) => new()
        {
            XPercent = (offsetX + bounds.XPercent / 100 * contentWidth) / targetWidth * 100,
            YPercent = (offsetY + bounds.YPercent / 100 * contentHeight) / targetHeight * 100,
            WidthPercent = bounds.WidthPercent / 100 * contentWidth / targetWidth * 100,
            HeightPercent = bounds.HeightPercent / 100 * contentHeight / targetHeight * 100,
        };
        return scene with
        {
            Surface = scene.Surface with
            {
                Kind = facing ? CompositionSurfaceKind.FacingSpread : CompositionSurfaceKind.SinglePage,
                OutputPageMode = CompositionOutputPageMode.EditionLeaves,
                WidthPoints = targetWidth,
                HeightPoints = targetHeight,
                BleedPoints = bleed ? 9 : 0,
                SafeInsetPoints = pageMarginInches * 72,
                AllowIndependentPdfPage = false,
            },
            // Group children use coordinates local to their group. Mapping the group moves the
            // complete subtree; mapping its children as well would apply the transform twice.
            Objects = scene.Objects.Select(item => item.GroupId is null
                ? item with { Bounds = Map(item.Bounds) }
                : item).ToList(),
        };
    }

    public async Task<DesignedPageVariant> SaveVariantAsync(
        EditorContentTarget target,
        Guid projectId,
        Guid variantId,
        long expectedRevision,
        CompositionScene scene,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        scene = scene with { Guides = [] };
        var beforeHistory = await AuthoringSnapshotCodec.CaptureDesignedPageAsync(db, projectId, contentId: await db.DesignedPageVariants.AsNoTracking()
            .Where(item => item.Id == variantId)
            .Select(item => item.ContentId)
            .SingleAsync(cancellationToken), selectionJson: "", cancellationToken);
        var variant = await db.DesignedPageVariants.AsTracking()
            .Include(item => item.Content).ThenInclude(item => item.Variants)
            .Include(item => item.Content).ThenInclude(item => item.Page)
            .SingleOrDefaultAsync(item => item.Id == variantId
                && item.Content.ProjectId == projectId, cancellationToken)
            ?? throw new KeyNotFoundException("Composition variant was not found in this project.");
        if (variant.Revision != expectedRevision)
            throw new DesignedPageRevisionConflictException(expectedRevision, variant.Revision);
        EnsureTarget(target, variant.Content);
        await EnsureChapterMutationAllowedAsync(projectId, variant.Content, cancellationToken);
        await ValidateVariantGeometryAsync(projectId, variant.GeometryKey, scene, cancellationToken);
        await ValidateSceneAssetsAsync(projectId, scene, cancellationToken);
        var geometryKey = await ExactGeometryKeyAsync(projectId, variant.GeometryKey, scene, cancellationToken);
        if (!string.Equals(geometryKey, variant.GeometryKey, StringComparison.Ordinal))
        {
            variant = await CreateOrReactivateVariantAsync(
                variant.Content,
                geometryKey,
                SerializeAndValidate(scene, variant.Content.SemanticManuscriptJson),
                cancellationToken);
        }
        else
        {
            variant.SceneJson = SerializeAndValidate(scene, variant.Content.SemanticManuscriptJson);
            variant.Revision = checked(variant.Revision + 1);
            variant.UpdatedAt = DateTime.UtcNow;
        }
        variant.Content.ActiveVariantId = variant.Id;
        await TouchProjectAsync(projectId, cancellationToken);
        var afterHistory = AuthoringSnapshotCodec.CaptureDesignedPage(variant.Content);
        if (string.Equals(beforeHistory, afterHistory, StringComparison.Ordinal))
        {
            db.ChangeTracker.Clear();
            return await db.DesignedPageVariants.AsNoTracking()
                .Include(item => item.Content)
                .SingleAsync(item => item.Id == variantId && item.Content.ProjectId == projectId, cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
        await RecordCompositionMutationAsync(projectId, variant.Content, "Edit page layout", CancellationToken.None);
        return variant;
    }

    private async Task<DesignedPageVariant> CreateOrReactivateVariantAsync(
        DesignedPageContent composition,
        string geometryKey,
        string sceneJson,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(composition.ProjectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var existing = await db.DesignedPageVariants.AsTracking().SingleOrDefaultAsync(
            item => item.ContentId == composition.Id && item.GeometryKey == geometryKey,
            cancellationToken);
        if (existing is not null)
            throw new InvalidOperationException("This exact layout geometry already exists. Select that variant instead of overwriting it.");

        var variant = new DesignedPageVariant
        {
            ContentId = composition.Id,
            Content = composition,
            GeometryKey = geometryKey,
            SceneJson = sceneJson,
        };
        db.DesignedPageVariants.Add(variant);
        return variant;
    }

    public async Task<DesignedPageVariant> PatchElementAsync(
        EditorContentTarget target,
        Guid projectId,
        Guid variantId,
        long expectedRevision,
        string targetKind,
        Guid targetId,
        CompositionElementPatch patch,
        CancellationToken cancellationToken = default)
    {
        var variant = await ReadVariantAsync(projectId, variantId, cancellationToken);
        if (variant.Revision != expectedRevision)
            throw new DesignedPageRevisionConflictException(expectedRevision, variant.Revision);
        EnsureTarget(target, variant.Content);
        var scene = JsonSerializer.Deserialize<CompositionScene>(variant.SceneJson, JsonOptions)
            ?? throw new InvalidDataException("The composition scene is empty.");
        scene = ApplyElementPatch(scene, targetKind, targetId, patch);
        return await SaveVariantAsync(target, projectId, variantId, expectedRevision, scene, cancellationToken);
    }

    public async Task<CompositionImagePlacementResult> AddImageObjectAsync(
        EditorContentTarget target,
        Guid projectId,
        Guid variantId,
        long expectedRevision,
        Guid imageId,
        FigureImageFit fit,
        string? altText,
        bool decorative,
        CompositionBounds? bounds = null,
        int? readingOrder = null,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        if (imageId == Guid.Empty)
            throw new ArgumentException("A project image is required.", nameof(imageId));
        if (!decorative && string.IsNullOrWhiteSpace(altText))
            throw new ArgumentException("Provide alternative text or explicitly mark the artwork decorative.", nameof(altText));
        if (!await db.PublishAssets.AsNoTracking().AnyAsync(asset => asset.ProjectId == projectId && asset.Id == imageId, cancellationToken))
            throw new KeyNotFoundException("Project image was not found.");

        var variant = await ReadVariantAsync(projectId, variantId, cancellationToken);
        if (variant.Revision != expectedRevision)
            throw new DesignedPageRevisionConflictException(expectedRevision, variant.Revision);
        EnsureTarget(target, variant.Content);
        var scene = JsonSerializer.Deserialize<CompositionScene>(variant.SceneJson, JsonOptions)
            ?? throw new InvalidDataException("Composition scene is empty.");
        var mutation = AddImageObjectToScene(scene, imageId, fit, altText, decorative, bounds, readingOrder);
        var saved = await SaveVariantAsync(
            target,
            projectId,
            variantId,
            expectedRevision,
            mutation.Scene,
            cancellationToken);
        return new CompositionImagePlacementResult(saved, mutation.ObjectId);
    }

    public static CompositionSceneImageMutation AddImageObjectToScene(
        CompositionScene scene,
        Guid imageId,
        FigureImageFit fit,
        string? altText,
        bool decorative,
        CompositionBounds? bounds = null,
        int? readingOrder = null)
    {
        if (imageId == Guid.Empty)
            throw new ArgumentException("A project image is required.", nameof(imageId));
        if (!decorative && string.IsNullOrWhiteSpace(altText))
            throw new ArgumentException("Provide alternative text or explicitly mark the artwork decorative.", nameof(altText));

        var layers = scene.Layers.ToList();
        if (layers.Count == 0)
            layers.Add(new CompositionLayer(Guid.NewGuid(), "Artwork", 0));
        var objectId = Guid.NewGuid();
        int? effectiveReadingOrder = decorative
            ? null
            : readingOrder ?? scene.Objects.Where(item => item.ReadingOrder is not null).Select(item => item.ReadingOrder!.Value).DefaultIfEmpty().Max() + 1;
        var imageObject = new CompositionObject
        {
            Id = objectId,
            LayerId = layers.OrderByDescending(item => item.Order).First().Id,
            Kind = CompositionObjectKind.Image,
            Name = "Artwork",
            ImageId = imageId,
            ImageFit = fit,
            Bounds = bounds ?? new CompositionBounds { XPercent = 5, YPercent = 5, WidthPercent = 90, HeightPercent = 90 },
            AltText = decorative ? string.Empty : altText!.Trim(),
            Decorative = decorative,
            AccessibilityDecisionPending = false,
            SemanticRole = decorative ? CompositionSemanticRole.Artifact : CompositionSemanticRole.Figure,
            ReadingOrder = effectiveReadingOrder,
            ZIndex = scene.Objects.Select(item => item.ZIndex).DefaultIfEmpty(-1).Max() + 1,
        };
        return new CompositionSceneImageMutation(
            scene with { Layers = layers, Objects = [.. scene.Objects, imageObject] },
            objectId);
    }

    internal static CompositionScene ApplyElementPatch(
        CompositionScene scene,
        string targetKind,
        Guid targetId,
        CompositionElementPatch patch) => targetKind.Trim().ToLowerInvariant() switch
        {
            "object" => scene with { Objects = PatchObject(scene.Objects, targetId, patch) },
            "layer" => scene with { Layers = PatchLayer(scene.Layers, targetId, patch) },
            "style" => scene with { Styles = PatchStyle(scene.Styles, targetId, patch) },
            _ => throw new ArgumentException("targetKind must be object, layer, or style. Page guides are computed overlays.", nameof(targetKind)),
        };

    private static IReadOnlyList<CompositionObject> PatchObject(
        IReadOnlyList<CompositionObject> source,
        Guid id,
        CompositionElementPatch patch)
    {
        if (!source.Any(item => item.Id == id))
            throw new KeyNotFoundException("Composition object was not found.");
        return source.Select(item => item.Id != id ? item : item with
        {
            Name = patch.Name ?? item.Name,
            LayerId = patch.LayerId ?? item.LayerId,
            Bounds = patch.Bounds ?? item.Bounds,
            RotationDegrees = patch.RotationDegrees ?? item.RotationDegrees,
            Opacity = patch.Opacity ?? item.Opacity,
            ZIndex = patch.Order ?? item.ZIndex,
            Visible = patch.Visible ?? item.Visible,
            Locked = patch.Locked ?? item.Locked,
            StyleId = patch.ClearStyle ? null : patch.StyleId ?? item.StyleId,
            ImageId = patch.ImageId ?? item.ImageId,
            ImageFit = patch.ImageFit ?? item.ImageFit,
            CropXPercent = patch.CropXPercent ?? item.CropXPercent,
            CropYPercent = patch.CropYPercent ?? item.CropYPercent,
            ContentReferences = patch.ContentReferences ?? item.ContentReferences,
            FontFamilyKey = patch.FontFamilyKey ?? item.FontFamilyKey,
            FontWeight = patch.FontWeight ?? item.FontWeight,
            Italic = patch.Italic ?? item.Italic,
            FontSizePoints = patch.FontSizePoints ?? item.FontSizePoints,
            LineHeight = patch.LineHeight ?? item.LineHeight,
            LetterSpacingEm = patch.LetterSpacingEm ?? item.LetterSpacingEm,
            FillColor = patch.FillColor ?? item.FillColor,
            BackgroundColor = patch.BackgroundColor ?? item.BackgroundColor,
            BackgroundOpacity = patch.BackgroundOpacity ?? item.BackgroundOpacity,
            StrokeColor = patch.StrokeColor ?? item.StrokeColor,
            StrokeWidthPoints = patch.StrokeWidthPoints ?? item.StrokeWidthPoints,
            TextAlignment = patch.TextAlignment ?? item.TextAlignment,
            VerticalAlignment = patch.VerticalAlignment ?? item.VerticalAlignment,
            TextShadow = patch.TextShadow ?? item.TextShadow,
            AltText = patch.AccessibilityDecisionPending == true || patch.Decorative == true
                ? string.Empty
                : patch.AltText ?? item.AltText,
            Decorative = patch.AccessibilityDecisionPending == true
                ? false
                : patch.AltText is not null ? false : patch.Decorative ?? item.Decorative,
            AccessibilityDecisionPending = patch.AccessibilityDecisionPending
                ?? (patch.AltText is not null || patch.Decorative == true ? false : item.AccessibilityDecisionPending),
            Language = patch.Language ?? item.Language,
            SemanticRole = patch.SemanticRole ?? item.SemanticRole,
            ReadingOrder = patch.ClearReadingOrder ? null : patch.ReadingOrder ?? item.ReadingOrder,
            RegionConstraint = patch.RegionConstraint ?? item.RegionConstraint,
            GroupId = patch.ClearGroup ? null : patch.GroupId ?? item.GroupId,
        }).ToList();
    }

    private static IReadOnlyList<CompositionLayer> PatchLayer(
        IReadOnlyList<CompositionLayer> source,
        Guid id,
        CompositionElementPatch patch)
    {
        if (!source.Any(item => item.Id == id))
            throw new KeyNotFoundException("Composition layer was not found.");
        return source.Select(item => item.Id != id ? item : item with
        {
            Name = patch.Name ?? item.Name,
            Order = patch.Order ?? item.Order,
            Visible = patch.Visible ?? item.Visible,
            Locked = patch.Locked ?? item.Locked,
        }).ToList();
    }

    private static IReadOnlyList<CompositionObjectStyle> PatchStyle(
        IReadOnlyList<CompositionObjectStyle> source,
        Guid id,
        CompositionElementPatch patch)
    {
        if (!source.Any(item => item.Id == id))
            throw new KeyNotFoundException("Composition object style was not found.");
        return source.Select(item => item.Id != id ? item : item with
        {
            Name = patch.Name ?? item.Name,
            FontFamilyKey = patch.FontFamilyKey ?? item.FontFamilyKey,
            FontWeight = patch.FontWeight ?? item.FontWeight,
            Italic = patch.Italic ?? item.Italic,
            FontSizePoints = patch.FontSizePoints ?? item.FontSizePoints,
            LineHeight = patch.LineHeight ?? item.LineHeight,
            LetterSpacingEm = patch.LetterSpacingEm ?? item.LetterSpacingEm,
            FillColor = patch.FillColor ?? item.FillColor,
            BackgroundColor = patch.BackgroundColor ?? item.BackgroundColor,
            BackgroundOpacity = patch.BackgroundOpacity ?? item.BackgroundOpacity,
            StrokeColor = patch.StrokeColor ?? item.StrokeColor,
            StrokeWidthPoints = patch.StrokeWidthPoints ?? item.StrokeWidthPoints,
            TextAlignment = patch.TextAlignment ?? item.TextAlignment,
            VerticalAlignment = patch.VerticalAlignment ?? item.VerticalAlignment,
            TextShadow = patch.TextShadow ?? item.TextShadow,
        }).ToList();
    }

    public async Task<DesignedPageWorkspaceSaveResult> SaveWorkspaceAsync(
        EditorContentTarget target,
        Guid projectId,
        Guid contentId,
        long expectedContentRevision,
        IReadOnlyList<ManuscriptBlock> semanticBlocks,
        Guid variantId,
        long expectedVariantRevision,
        CompositionScene scene,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        AuthoringDeltaHistoryStage? historyStage = null;
        var variant = await db.DesignedPageVariants.AsTracking().Include(item => item.Content)
            .ThenInclude(item => item.Variants)
            .SingleOrDefaultAsync(item => item.Id == variantId
                && item.ContentId == contentId
                && item.Content.ProjectId == projectId
               , cancellationToken)
            ?? throw new KeyNotFoundException("Composition workspace was not found in this project.");
        var composition = variant.Content;
        EnsureTarget(target, composition);
        await EnsureChapterMutationAllowedAsync(projectId, composition, cancellationToken);
        if (composition.Revision != expectedContentRevision
            || variant.Revision != expectedVariantRevision)
            return new DesignedPageWorkspaceSaveResult(composition, variant, Applied: false);
        var beforeSemantic = ManuscriptCodec.Deserialize(
            composition.SemanticManuscriptJson,
            composition.Id,
            composition.Revision);
        var beforeScene = JsonSerializer.Deserialize<CompositionScene>(variant.SceneJson, JsonOptions)
            ?? throw new InvalidDataException("The Designed Page scene is empty.");
        var semantic = new ManuscriptDocument
        {
            ManuscriptId = composition.Id,
            Revision = checked(composition.Revision + 1),
            Content = semanticBlocks.ToList(),
        };
        ValidateSemanticFragment(semantic);
        var semanticJson = ManuscriptCodec.Serialize(semantic);
        await Lorekeeper.Citations.CitationReferenceValidator.ValidateAsync(db, projectId, semantic, cancellationToken);
        await ValidateVariantGeometryAsync(projectId, variant.GeometryKey, scene, cancellationToken);
        await ValidateSceneAssetsAsync(projectId, scene, cancellationToken);
        var sceneJson = SerializeAndValidate(scene, semanticJson);
        composition.SemanticManuscriptJson = semanticJson;
        composition.Revision = semantic.Revision;
        composition.UpdatedAt = DateTime.UtcNow;
        foreach (var other in await db.DesignedPageVariants.AsNoTracking()
            .Where(item => item.ContentId == composition.Id && item.Id != variant.Id)
            .ToListAsync(cancellationToken))
        {
            var otherScene = JsonSerializer.Deserialize<CompositionScene>(other.SceneJson, JsonOptions)
                ?? throw new InvalidDataException("A composition variant scene is empty.");
            _ = SerializeAndValidate(otherScene, semanticJson);
        }
        var geometryKey = await ExactGeometryKeyAsync(projectId, variant.GeometryKey, scene, cancellationToken);
        if (!string.Equals(geometryKey, variant.GeometryKey, StringComparison.Ordinal))
        {
            variant = await CreateOrReactivateVariantAsync(composition, geometryKey, sceneJson, cancellationToken);
        }
        else
        {
            variant.SceneJson = sceneJson;
            variant.Revision = checked(variant.Revision + 1);
            variant.UpdatedAt = DateTime.UtcNow;
        }
        composition.ActiveVariantId = variant.Id;
        await TouchProjectAsync(projectId, cancellationToken);
        var manuscriptDelta = AuthoringBatchReducer.CreateCanonicalDelta(beforeSemantic, semantic);
        var canvasDelta = CompositionCanvasDeltaAdapter.Create(beforeScene, scene);
        var forward = manuscriptDelta.Forward.Concat(canvasDelta.Forward).ToList();
        var inverse = canvasDelta.Inverse.Concat(manuscriptDelta.Inverse).ToList();
        if (forward.Count == 0)
        {
            db.ChangeTracker.Clear();
            var currentVariant = await db.DesignedPageVariants.AsNoTracking()
                .Include(item => item.Content)
                .ThenInclude(item => item.Variants)
                .SingleAsync(item => item.Id == variantId && item.ContentId == contentId, cancellationToken);
            return new DesignedPageWorkspaceSaveResult(currentVariant.Content, currentVariant);
        }
        var authoringTargetId = composition.EditionId is Guid editionId
            ? $"release:{editionId:D}:designed-page-content:{composition.Id:D}"
            : $"designed-page-content:{composition.Id:D}";
        if (!authoringMutationContext.IsHistorySuppressed)
        {
            var generations = await AuthoringPersistence.ReadGenerationsAsync(
                db,
                projectId,
                [authoringTargetId],
                cancellationToken);
            historyStage = deltaHistory.Stage(
                projectId,
                [authoringTargetId],
                generations,
                "Edit page",
                forward,
                inverse,
                null,
                null);
        }
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (historyStage is not null)
                deltaHistory.Discard(historyStage.StageId);
            throw;
        }
        if (historyStage is not null)
        {
            try
            {
                _ = deltaHistory.Confirm(historyStage.StageId);
            }
            catch
            {
                deltaHistory.Clear(authoringTargetId);
            }
        }
        await RefreshPlacedChaptersAsync(target, projectId, composition.DesignedPageId, cancellationToken);
        return new DesignedPageWorkspaceSaveResult(composition, variant);
    }

    public async Task<DesignedPageSemanticMutationResult> ApplyAuthoringOperationsAsync(
        EditorContentTarget target,
        Guid projectId,
        Guid contentId,
        long expectedRevision,
        IReadOnlyList<ManuscriptOperation> operations,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var content = await db.DesignedPageContents.AsTracking()
            .Include(item => item.Variants)
            .SingleOrDefaultAsync(item => item.ProjectId == projectId && item.Id == contentId, cancellationToken)
            ?? throw new KeyNotFoundException("Designed Page content was not found.");
        EnsureTarget(target, content);
        await EnsureChapterMutationAllowedAsync(projectId, content, cancellationToken);
        if (content.Revision != expectedRevision)
            throw new DesignedPageRevisionConflictException(expectedRevision, content.Revision);
        var current = ManuscriptCodec.Deserialize(content.SemanticManuscriptJson, content.Id, content.Revision);
        var applied = ManuscriptOperations.Apply(current, operations);
        if (applied.ChangedBlockIds.Count == 0)
            return new DesignedPageSemanticMutationResult(content, []);
        var document = applied.Document with { Revision = checked(content.Revision + 1) };
        await Lorekeeper.Citations.CitationReferenceValidator.ValidateAsync(db, projectId, document, cancellationToken);
        var manuscriptJson = ManuscriptCodec.Serialize(document);
        foreach (var variant in content.Variants)
        {
            var scene = JsonSerializer.Deserialize<CompositionScene>(variant.SceneJson, JsonOptions)
                ?? throw new InvalidDataException("The Designed Page scene is empty.");
            _ = SerializeAndValidate(scene, manuscriptJson);
        }
        content.SemanticManuscriptJson = manuscriptJson;
        content.Revision = document.Revision;
        content.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await RecordCompositionMutationAsync(projectId, content, "Edit page content", CancellationToken.None);
        if (!authoringMutationContext.IsHistorySuppressed)
            await RefreshPlacedChaptersAsync(target, projectId, content.DesignedPageId, cancellationToken);
        return new DesignedPageSemanticMutationResult(content, applied.ChangedBlockIds);
    }

    public async Task<DesignedPageSemanticMutationResult> ReplaceAuthoringDocumentAsync(
        EditorContentTarget target,
        Guid projectId,
        Guid contentId,
        long expectedRevision,
        ManuscriptDocument document,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var content = await db.DesignedPageContents.AsTracking()
            .Include(item => item.Variants)
            .SingleOrDefaultAsync(item => item.ProjectId == projectId && item.Id == contentId, cancellationToken)
            ?? throw new KeyNotFoundException("Designed Page content was not found.");
        EnsureTarget(target, content);
        await EnsureChapterMutationAllowedAsync(projectId, content, cancellationToken);
        if (content.Revision != expectedRevision)
            throw new DesignedPageRevisionConflictException(expectedRevision, content.Revision);
        var replacement = document with { ManuscriptId = content.Id, Revision = checked(content.Revision + 1) };
        await Lorekeeper.Citations.CitationReferenceValidator.ValidateAsync(db, projectId, replacement, cancellationToken);
        var manuscriptJson = ManuscriptCodec.Serialize(replacement);
        foreach (var variant in content.Variants)
        {
            var scene = JsonSerializer.Deserialize<CompositionScene>(variant.SceneJson, JsonOptions)
                ?? throw new InvalidDataException("The Designed Page scene is empty.");
            _ = SerializeAndValidate(scene, manuscriptJson);
        }
        content.SemanticManuscriptJson = manuscriptJson;
        content.Revision = replacement.Revision;
        content.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        if (!authoringMutationContext.IsHistorySuppressed)
        {
            await authoringGenerations.InvalidateAsync(
                projectId,
                [$"designed-page-content:{content.Id:D}"],
                cancellationToken);
        }
        if (!authoringMutationContext.IsHistorySuppressed)
            await RefreshPlacedChaptersAsync(target, projectId, content.DesignedPageId, cancellationToken);
        return new DesignedPageSemanticMutationResult(
            content,
            replacement.Content.Select(item => item.Id).ToList());
    }

    public async Task RefreshAuthoringDerivedStateAsync(
        EditorContentTarget target,
        Guid projectId,
        Guid contentId,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        var pageId = await operation.Db.DesignedPageContents.AsNoTracking()
            .Where(item => item.ProjectId == projectId && item.Id == contentId)
            .Select(item => item.DesignedPageId)
            .SingleOrDefaultAsync(cancellationToken);
        if (pageId == Guid.Empty)
            throw new KeyNotFoundException("Designed Page content was not found.");
        await operation.DisposeAsync();
        await RefreshPlacedChaptersAsync(target, projectId, pageId, cancellationToken);
    }

    public async Task<CompositionMutationStage> StageVariantAsync(
        EditorContentTarget target,
        Guid projectId,
        Guid conversationId,
        Guid variantId,
        long expectedRevision,
        CompositionScene scene,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        scene = scene with { Guides = [] };
        var variant = await db.DesignedPageVariants.AsNoTracking()
            .Include(item => item.Content)
            .SingleOrDefaultAsync(item => item.Id == variantId
                && item.Content.ProjectId == projectId, cancellationToken)
            ?? throw new KeyNotFoundException("Composition variant was not found in this project.");
        if (variant.Revision != expectedRevision)
            throw new DesignedPageRevisionConflictException(expectedRevision, variant.Revision);
        EnsureTarget(target, variant.Content);
        await EnsureChapterMutationAllowedAsync(projectId, variant.Content, cancellationToken);
        await db.CompositionMutationStages
            .Where(item => item.ProjectId == projectId
                && (item.ExpiresAt <= DateTime.UtcNow || item.AppliedAt != null))
            .ExecuteDeleteAsync(cancellationToken);
        await ValidateVariantGeometryAsync(projectId, variant.GeometryKey, scene, cancellationToken);
        var payload = SerializeAndValidate(scene, variant.Content.SemanticManuscriptJson);
        await ValidateSceneAssetsAsync(projectId, scene, cancellationToken);
        var stage = new CompositionMutationStage
        {
            ProjectId = projectId,
            ConversationId = conversationId,
            ContentTargetKind = target.Kind.ToString(),
            ContentTargetEditionId = target.EditionId,
            TargetKind = "composition-variant",
            TargetId = variantId,
            ExpectedRevision = expectedRevision,
            OperationsJson = payload,
            PayloadSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload))),
            ExpiresAt = DateTime.UtcNow.AddMinutes(30),
        };
        db.CompositionMutationStages.Add(stage);
        await db.SaveChangesAsync(cancellationToken);
        return stage;
    }

    public async Task<DesignedPageVariant> ApplyStageAsync(
        EditorContentTarget target,
        Guid projectId,
        Guid conversationId,
        Guid stageId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var stage = await db.CompositionMutationStages.SingleOrDefaultAsync(
            item => item.Id == stageId
                && item.ProjectId == projectId
                && item.ConversationId == conversationId
                && item.ContentTargetKind == target.Kind.ToString()
                && item.ContentTargetEditionId == target.EditionId
                && item.TargetKind == "composition-variant",
            cancellationToken) ?? throw new KeyNotFoundException("Composition stage was not found for this conversation.");
        if (stage.AppliedAt is not null)
            throw new InvalidOperationException("Composition stages are non-replayable.");
        if (stage.ExpiresAt <= DateTime.UtcNow)
            throw new InvalidOperationException("The composition stage has expired; submit it again.");
        if (stage.ExpectedRevision != expectedRevision)
            throw new DesignedPageRevisionConflictException(expectedRevision, stage.ExpectedRevision);
        var payloadHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stage.OperationsJson)));
        if (!string.Equals(payloadHash, stage.PayloadSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The staged composition payload failed its integrity check.");
        var variant = await db.DesignedPageVariants.AsTracking().Include(item => item.Content)
            .ThenInclude(item => item.Variants)
            .Include(item => item.Content).ThenInclude(item => item.Page)
            .SingleAsync(
            item => item.Id == stage.TargetId
                && item.Content.ProjectId == projectId,
            cancellationToken);
        var beforeHistory = AuthoringSnapshotCodec.CaptureDesignedPage(variant.Content);
        if (variant.Revision != expectedRevision)
            throw new DesignedPageRevisionConflictException(expectedRevision, variant.Revision);
        EnsureTarget(target, variant.Content);
        await EnsureChapterMutationAllowedAsync(projectId, variant.Content, cancellationToken);
        var scene = JsonSerializer.Deserialize<CompositionScene>(stage.OperationsJson, JsonOptions)
            ?? throw new InvalidDataException("The staged composition is empty.");
        await ValidateVariantGeometryAsync(projectId, variant.GeometryKey, scene, cancellationToken);
        await ValidateSceneAssetsAsync(projectId, scene, cancellationToken);
        var geometryKey = await ExactGeometryKeyAsync(projectId, variant.GeometryKey, scene, cancellationToken);
        if (!string.Equals(geometryKey, variant.GeometryKey, StringComparison.Ordinal))
        {
            variant = await CreateOrReactivateVariantAsync(
                variant.Content,
                geometryKey,
                SerializeAndValidate(scene, variant.Content.SemanticManuscriptJson),
                cancellationToken);
        }
        else
        {
            variant.SceneJson = SerializeAndValidate(scene, variant.Content.SemanticManuscriptJson);
            variant.Revision = checked(variant.Revision + 1);
            variant.UpdatedAt = DateTime.UtcNow;
        }
        variant.Content.ActiveVariantId = variant.Id;
        var pageId = variant.Content.DesignedPageId;
        db.CompositionMutationStages.Remove(stage);
        await TouchProjectAsync(projectId, cancellationToken);
        var afterHistory = AuthoringSnapshotCodec.CaptureDesignedPage(variant.Content);
        if (string.Equals(beforeHistory, afterHistory, StringComparison.Ordinal))
        {
            db.ChangeTracker.Clear();
            db.Entry(stage).State = EntityState.Deleted;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return await db.DesignedPageVariants.AsNoTracking()
                .Include(item => item.Content)
                .SingleAsync(item => item.Id == stage.TargetId && item.Content.ProjectId == projectId, cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await RecordCompositionMutationAsync(projectId, variant.Content, "Edit page layout", CancellationToken.None);
        await RefreshPlacedChaptersAsync(target, projectId, pageId, cancellationToken);
        return variant;
    }

    public async Task<CompositionMutationStage> StageSemanticOperationsAsync(
        EditorContentTarget target,
        Guid projectId,
        Guid conversationId,
        Guid contentId,
        long expectedRevision,
        IReadOnlyList<ManuscriptOperationInput> operations,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        if (operations.Count is < 1 or > 200)
            throw new ArgumentException("A semantic composition stage requires 1 to 200 focused manuscript operations.");
        var composition = await db.DesignedPageContents.AsNoTracking()
            .Include(item => item.Variants)
            .SingleOrDefaultAsync(item => item.Id == contentId && item.ProjectId == projectId
               , cancellationToken)
            ?? throw new KeyNotFoundException("Designed Page content was not found in this project.");
        if (composition.Revision != expectedRevision)
            throw new DesignedPageRevisionConflictException(expectedRevision, composition.Revision);
        EnsureTarget(target, composition);
        await EnsureChapterMutationAllowedAsync(projectId, composition, cancellationToken);
        await db.CompositionMutationStages
            .Where(item => item.ProjectId == projectId && (item.ExpiresAt <= DateTime.UtcNow || item.AppliedAt != null))
            .ExecuteDeleteAsync(cancellationToken);
        var current = ManuscriptCodec.Deserialize(composition.SemanticManuscriptJson, composition.Id, composition.Revision);
        var preview = ManuscriptOperations.Apply(current, ManuscriptOperationInput.ToOperations(operations)).Document;
        ValidateSemanticFragment(preview);
        await Lorekeeper.Citations.CitationReferenceValidator.ValidateAsync(db, projectId, preview, cancellationToken);
        var previewJson = ManuscriptCodec.Serialize(preview);
        foreach (var variant in composition.Variants)
        {
            var scene = JsonSerializer.Deserialize<CompositionScene>(variant.SceneJson, JsonOptions)
                ?? throw new InvalidDataException("A composition variant scene is empty.");
            _ = SerializeAndValidate(scene, previewJson);
        }
        var payload = JsonSerializer.Serialize(operations, JsonOptions);
        var stage = new CompositionMutationStage
        {
            ProjectId = projectId,
            ConversationId = conversationId,
            ContentTargetKind = target.Kind.ToString(),
            ContentTargetEditionId = target.EditionId,
            TargetKind = "composition-semantic",
            TargetId = contentId,
            ExpectedRevision = expectedRevision,
            OperationsJson = payload,
            PayloadSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload))),
            ExpiresAt = DateTime.UtcNow.AddMinutes(30),
        };
        db.CompositionMutationStages.Add(stage);
        await db.SaveChangesAsync(cancellationToken);
        return stage;
    }

    public async Task<DesignedPageSemanticMutationResult> ApplySemanticStageAsync(
        EditorContentTarget target,
        Guid projectId,
        Guid conversationId,
        Guid stageId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var stage = await db.CompositionMutationStages.SingleOrDefaultAsync(item => item.Id == stageId
            && item.ProjectId == projectId && item.ConversationId == conversationId
            && item.ContentTargetKind == target.Kind.ToString()
            && item.ContentTargetEditionId == target.EditionId
            && item.TargetKind == "composition-semantic", cancellationToken)
            ?? throw new KeyNotFoundException("Semantic composition stage was not found for this conversation.");
        if (stage.AppliedAt is not null)
            throw new InvalidOperationException("Composition stages are non-replayable.");
        if (stage.ExpiresAt <= DateTime.UtcNow)
            throw new InvalidOperationException("The composition stage has expired; submit it again.");
        if (stage.ExpectedRevision != expectedRevision)
            throw new DesignedPageRevisionConflictException(expectedRevision, stage.ExpectedRevision);
        var payloadHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stage.OperationsJson)));
        if (!string.Equals(payloadHash, stage.PayloadSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The staged composition payload failed its integrity check.");
        var composition = await db.DesignedPageContents.AsTracking()
            .Include(item => item.Variants)
            .Include(item => item.Page)
            .SingleAsync(item => item.Id == stage.TargetId && item.ProjectId == projectId
               , cancellationToken);
        var beforeHistory = AuthoringSnapshotCodec.CaptureDesignedPage(composition);
        if (composition.Revision != expectedRevision)
            throw new DesignedPageRevisionConflictException(expectedRevision, composition.Revision);
        EnsureTarget(target, composition);
        await EnsureChapterMutationAllowedAsync(projectId, composition, cancellationToken);
        var inputs = JsonSerializer.Deserialize<List<ManuscriptOperationInput>>(stage.OperationsJson, JsonOptions)
            ?? throw new InvalidDataException("The staged semantic operations are empty.");
        var current = ManuscriptCodec.Deserialize(composition.SemanticManuscriptJson, composition.Id, composition.Revision);
        var applied = ManuscriptOperations.Apply(current, ManuscriptOperationInput.ToOperations(inputs));
        ValidateSemanticFragment(applied.Document);
        await Lorekeeper.Citations.CitationReferenceValidator.ValidateAsync(db, projectId, applied.Document, cancellationToken);
        var semanticJson = ManuscriptCodec.Serialize(applied.Document);
        foreach (var variant in composition.Variants)
        {
            var scene = JsonSerializer.Deserialize<CompositionScene>(variant.SceneJson, JsonOptions)
                ?? throw new InvalidDataException("A composition variant scene is empty.");
            _ = SerializeAndValidate(scene, semanticJson);
        }
        composition.SemanticManuscriptJson = semanticJson;
        composition.Revision = applied.Document.Revision;
        composition.UpdatedAt = DateTime.UtcNow;
        var pageId = composition.DesignedPageId;
        db.CompositionMutationStages.Remove(stage);
        await TouchProjectAsync(projectId, cancellationToken);
        var afterHistory = AuthoringSnapshotCodec.CaptureDesignedPage(composition);
        if (string.Equals(beforeHistory, afterHistory, StringComparison.Ordinal))
        {
            db.ChangeTracker.Clear();
            db.Entry(stage).State = EntityState.Deleted;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var currentComposition = await db.DesignedPageContents.AsNoTracking()
                .Include(item => item.Variants)
                .SingleAsync(item => item.Id == stage.TargetId && item.ProjectId == projectId, cancellationToken);
            return new DesignedPageSemanticMutationResult(currentComposition, []);
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await RecordCompositionMutationAsync(projectId, composition, "Edit page text", CancellationToken.None);
        await RefreshPlacedChaptersAsync(target, projectId, pageId, cancellationToken);
        return new DesignedPageSemanticMutationResult(composition, applied.ChangedBlockIds);
    }

    public async Task<CompositionMutationStage> StageWorkspaceAsync(
        EditorContentTarget target,
        Guid projectId,
        Guid conversationId,
        Guid contentId,
        long expectedContentRevision,
        Guid variantId,
        long expectedVariantRevision,
        IReadOnlyList<ManuscriptOperationInput> semanticOperations,
        CompositionScene scene,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        scene = scene with { Guides = [] };
        if (semanticOperations.Count > 200)
            throw new ArgumentException("A composition workspace stage accepts at most 200 focused semantic operations.");
        var variant = await db.DesignedPageVariants.AsNoTracking().Include(item => item.Content)
            .SingleOrDefaultAsync(item => item.Id == variantId && item.ContentId == contentId
                && item.Content.ProjectId == projectId
               , cancellationToken)
            ?? throw new KeyNotFoundException("Composition workspace was not found in this project.");
        if (variant.Content.Revision != expectedContentRevision)
            throw new DesignedPageRevisionConflictException(expectedContentRevision, variant.Content.Revision);
        if (variant.Revision != expectedVariantRevision)
            throw new DesignedPageRevisionConflictException(expectedVariantRevision, variant.Revision);
        EnsureTarget(target, variant.Content);
        await EnsureChapterMutationAllowedAsync(projectId, variant.Content, cancellationToken);
        await db.CompositionMutationStages
            .Where(item => item.ProjectId == projectId && (item.ExpiresAt <= DateTime.UtcNow || item.AppliedAt != null))
            .ExecuteDeleteAsync(cancellationToken);
        var current = ManuscriptCodec.Deserialize(
            variant.Content.SemanticManuscriptJson,
            contentId,
            expectedContentRevision);
        var applied = semanticOperations.Count == 0
            ? (Document: current, ChangedBlockIds: (IReadOnlyList<string>)[])
            : ManuscriptOperations.Apply(current, ManuscriptOperationInput.ToOperations(semanticOperations));
        ValidateSemanticFragment(applied.Document);
        await ValidateVariantGeometryAsync(projectId, variant.GeometryKey, scene, cancellationToken);
        await Lorekeeper.Citations.CitationReferenceValidator.ValidateAsync(db, projectId, applied.Document, cancellationToken);
        await ValidateSceneAssetsAsync(projectId, scene, cancellationToken);
        _ = SerializeAndValidate(scene, ManuscriptCodec.Serialize(applied.Document));
        foreach (var other in await db.DesignedPageVariants.AsNoTracking()
            .Where(item => item.ContentId == contentId && item.Id != variantId)
            .ToListAsync(cancellationToken))
        {
            var otherScene = JsonSerializer.Deserialize<CompositionScene>(other.SceneJson, JsonOptions)
                ?? throw new InvalidDataException("A composition variant scene is empty.");
            _ = SerializeAndValidate(otherScene, ManuscriptCodec.Serialize(applied.Document));
        }
        var payload = JsonSerializer.Serialize(new CompositionWorkspaceStagePayload(
            variantId,
            expectedVariantRevision,
            semanticOperations,
            scene), JsonOptions);
        var stage = new CompositionMutationStage
        {
            ProjectId = projectId,
            ConversationId = conversationId,
            ContentTargetKind = target.Kind.ToString(),
            ContentTargetEditionId = target.EditionId,
            TargetKind = "composition-workspace",
            TargetId = contentId,
            ExpectedRevision = expectedContentRevision,
            OperationsJson = payload,
            PayloadSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload))),
            ExpiresAt = DateTime.UtcNow.AddMinutes(30),
        };
        db.CompositionMutationStages.Add(stage);
        await db.SaveChangesAsync(cancellationToken);
        return stage;
    }

    public async Task<DesignedPageWorkspaceMutationResult> ApplyWorkspaceStageAsync(
        EditorContentTarget target,
        Guid projectId,
        Guid conversationId,
        Guid stageId,
        long expectedContentRevision,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var stage = await db.CompositionMutationStages.SingleOrDefaultAsync(item => item.Id == stageId
            && item.ProjectId == projectId && item.ConversationId == conversationId
            && item.ContentTargetKind == target.Kind.ToString()
            && item.ContentTargetEditionId == target.EditionId
            && item.TargetKind == "composition-workspace", cancellationToken)
            ?? throw new KeyNotFoundException("Composition workspace stage was not found for this conversation.");
        if (stage.ExpiresAt <= DateTime.UtcNow)
            throw new InvalidOperationException("The composition workspace stage has expired; submit it again.");
        if (stage.ExpectedRevision != expectedContentRevision)
            throw new DesignedPageRevisionConflictException(expectedContentRevision, stage.ExpectedRevision);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stage.OperationsJson)));
        if (!string.Equals(hash, stage.PayloadSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The staged composition workspace payload failed its integrity check.");
        var payload = JsonSerializer.Deserialize<CompositionWorkspaceStagePayload>(stage.OperationsJson, JsonOptions)
            ?? throw new InvalidDataException("The staged composition workspace payload is empty.");
        var variant = await db.DesignedPageVariants.AsTracking().Include(item => item.Content)
            .ThenInclude(item => item.Variants)
            .Include(item => item.Content).ThenInclude(item => item.Page)
            .SingleOrDefaultAsync(item => item.Id == payload.VariantId && item.ContentId == stage.TargetId
                && item.Content.ProjectId == projectId
               , cancellationToken)
            ?? throw new KeyNotFoundException("The staged composition workspace no longer exists.");
        var beforeHistory = AuthoringSnapshotCodec.CaptureDesignedPage(variant.Content);
        if (variant.Content.Revision != expectedContentRevision)
            throw new DesignedPageRevisionConflictException(expectedContentRevision, variant.Content.Revision);
        if (variant.Revision != payload.ExpectedVariantRevision)
            throw new DesignedPageRevisionConflictException(payload.ExpectedVariantRevision, variant.Revision);
        EnsureTarget(target, variant.Content);
        await EnsureChapterMutationAllowedAsync(projectId, variant.Content, cancellationToken);
        var current = ManuscriptCodec.Deserialize(variant.Content.SemanticManuscriptJson, stage.TargetId, expectedContentRevision);
        var applied = payload.SemanticOperations.Count == 0
            ? (Document: current, ChangedBlockIds: (IReadOnlyList<string>)[])
            : ManuscriptOperations.Apply(current, ManuscriptOperationInput.ToOperations(payload.SemanticOperations));
        ValidateSemanticFragment(applied.Document);
        var semanticJson = ManuscriptCodec.Serialize(applied.Document);
        await Lorekeeper.Citations.CitationReferenceValidator.ValidateAsync(db, projectId, applied.Document, cancellationToken);
        await ValidateVariantGeometryAsync(projectId, variant.GeometryKey, payload.Scene, cancellationToken);
        await ValidateSceneAssetsAsync(projectId, payload.Scene, cancellationToken);
        var sceneJson = SerializeAndValidate(payload.Scene, semanticJson);
        foreach (var other in await db.DesignedPageVariants.AsNoTracking()
            .Where(item => item.ContentId == stage.TargetId && item.Id != variant.Id)
            .ToListAsync(cancellationToken))
        {
            var otherScene = JsonSerializer.Deserialize<CompositionScene>(other.SceneJson, JsonOptions)
                ?? throw new InvalidDataException("A composition variant scene is empty.");
            _ = SerializeAndValidate(otherScene, semanticJson);
        }
        variant.Content.SemanticManuscriptJson = semanticJson;
        variant.Content.Revision = applied.Document.Revision;
        variant.Content.UpdatedAt = DateTime.UtcNow;
        var exactKey = await ExactGeometryKeyAsync(projectId, variant.GeometryKey, payload.Scene, cancellationToken);
        if (!string.Equals(exactKey, variant.GeometryKey, StringComparison.Ordinal))
        {
            variant = await CreateOrReactivateVariantAsync(variant.Content, exactKey, sceneJson, cancellationToken);
        }
        else
        {
            variant.SceneJson = sceneJson;
            variant.Revision = checked(variant.Revision + 1);
            variant.UpdatedAt = DateTime.UtcNow;
        }
        variant.Content.ActiveVariantId = variant.Id;
        db.CompositionMutationStages.Remove(stage);
        await TouchProjectAsync(projectId, cancellationToken);
        var afterHistory = AuthoringSnapshotCodec.CaptureDesignedPage(variant.Content);
        if (string.Equals(beforeHistory, afterHistory, StringComparison.Ordinal))
        {
            db.ChangeTracker.Clear();
            db.Entry(stage).State = EntityState.Deleted;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var currentVariant = await db.DesignedPageVariants.AsNoTracking()
                .Include(item => item.Content)
                .ThenInclude(item => item.Variants)
                .SingleAsync(item => item.Id == payload.VariantId && item.ContentId == stage.TargetId, cancellationToken);
            return new DesignedPageWorkspaceMutationResult(currentVariant.Content, currentVariant, []);
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await RecordCompositionMutationAsync(projectId, variant.Content, "Edit page layout", CancellationToken.None);
        await RefreshPlacedChaptersAsync(target, projectId, variant.Content.DesignedPageId, cancellationToken);
        return new DesignedPageWorkspaceMutationResult(variant.Content, variant, applied.ChangedBlockIds);
    }

    public async Task<LayoutGenerationTargetDescriptor> DescribeGenerationTargetAsync(
        Guid projectId,
        Guid editionId,
        string targetKind,
        Guid targetId,
        Guid? variantId = null,
        CancellationToken cancellationToken = default,
        CompositionBounds? surfaceBounds = null)
    {
        var edition = (await effectiveConfigurations.ResolveReleaseAsync(projectId, editionId, cancellationToken)).Edition;
        var normalizedKind = NormalizeGenerationTargetKind(targetKind);
        if (normalizedKind is not ("cover-surface" or "cover-frame" or "page-surface" or "page-frame"))
            throw new ArgumentException("Publication-edition generation targets are cover or Designed Page surfaces and frames.", nameof(targetKind));
        if (normalizedKind is "page-surface" or "page-frame" && variantId is null)
            throw new ArgumentException("Designed Page generation targets require the exact release composition variant ID.", nameof(variantId));
        if (normalizedKind is "cover-surface" or "cover-frame" && variantId is not null)
            throw new ArgumentException("Cover targets do not use a Designed Page layout variant.", nameof(variantId));
        var (width, height, regions, diagnostics) = normalizedKind switch
        {
            "cover-surface" => await ResolveCoverSurfaceTargetAsync(projectId, edition, targetId, cancellationToken),
            "cover-frame" => await ResolveCoverFrameTargetAsync(projectId, edition, targetId, cancellationToken),
            "page-surface" => await ResolveEditionPageSurfaceTargetAsync(projectId, edition, targetId, variantId!.Value, cancellationToken),
            _ => await ResolveEditionPageFrameTargetAsync(projectId, edition, targetId, variantId!.Value, cancellationToken),
        };
        var gcd = GreatestCommonDivisor((int)Math.Round(width * 1000), (int)Math.Round(height * 1000));
        var pixelsPerInch = edition.Format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover ? 300 : 180;
        var aspect = $"{(int)Math.Round(width * 1000) / gcd}:{(int)Math.Round(height * 1000) / gcd}";
        var requestedRaster = ResolveRasterOrUnsupported(width, height);
        var recommendedRaster = ResolveNearestRasterOrUnsupported(width * pixelsPerInch, height * pixelsPerInch);
        var geometryFingerprint = TargetGeometryFingerprint(
            edition,
            normalizedKind,
            targetId,
            variantId,
            width,
            height,
            regions);
        var descriptor = new LayoutGenerationTargetDescriptor(
            edition.Id,
            variantId,
            geometryFingerprint,
            "publication-edition",
            normalizedKind,
            targetId,
            width,
            height,
            aspect,
            recommendedRaster.Width,
            recommendedRaster.Height,
            requestedRaster.Width,
            requestedRaster.Height,
            RasterLabel(requestedRaster),
            pixelsPerInch,
            regions,
            diagnostics);
        return ApplySurfaceBounds(descriptor, normalizedKind, surfaceBounds);
    }

    public async Task<LayoutGenerationTargetDescriptor> DescribeAuthoringGenerationTargetAsync(
        Guid projectId,
        string targetKind,
        Guid targetId,
        Guid? variantId = null,
        CancellationToken cancellationToken = default,
        CompositionBounds? surfaceBounds = null)
    {
        var setup = await ReadPageSetupAsync(projectId, cancellationToken);
        var normalizedKind = NormalizeGenerationTargetKind(targetKind);
        if (normalizedKind is "page-surface" or "page-frame" && variantId is null)
            throw new ArgumentException("Page layout generation targets require the exact composition variant ID.", nameof(variantId));
        if (normalizedKind is not ("project-page" or "page-surface" or "page-frame" or "figure" or "core-cover-surface" or "core-cover-frame"))
            throw new ArgumentException("Authoring targets must be a project page, Figure, Designed Page surface/frame, or Core cover surface/frame.", nameof(targetKind));
        if (normalizedKind is "figure" or "project-page" or "core-cover-surface" or "core-cover-frame" && variantId is not null)
            throw new ArgumentException("Project-page, Figure, and Core cover targets do not use a page variant.", nameof(variantId));
        if (normalizedKind == "project-page" && targetId != projectId)
            throw new ArgumentException("A project-page target ID must be the current project ID.", nameof(targetId));
        if (normalizedKind == "core-cover-surface" && targetId != projectId)
            throw new ArgumentException("A Core cover surface target ID must be the current project ID.", nameof(targetId));

        var (width, height, regions, diagnostics) = normalizedKind switch
        {
            "project-page" => (setup.PageWidthInches, setup.PageHeightInches, PageRegions(CreatePageScene(setup), true), []),
            "figure" => await ResolveAuthoringFigureTargetAsync(projectId, setup, targetId, cancellationToken),
            "page-surface" => await ResolveAuthoringPageSurfaceTargetAsync(projectId, targetId, variantId!.Value, cancellationToken),
            "core-cover-surface" => await ResolveCoreCoverSurfaceTargetAsync(projectId, setup, cancellationToken),
            "core-cover-frame" => await ResolveCoreCoverFrameTargetAsync(projectId, setup, targetId, cancellationToken),
            _ => await ResolveAuthoringPageFrameTargetAsync(projectId, targetId, variantId!.Value, cancellationToken),
        };
        var gcd = GreatestCommonDivisor((int)Math.Round(width * 1000), (int)Math.Round(height * 1000));
        var aspect = $"{(int)Math.Round(width * 1000) / gcd}:{(int)Math.Round(height * 1000) / gcd}";
        var requestedRaster = ResolveRasterOrUnsupported(width, height);
        var recommendedRaster = ResolveNearestRasterOrUnsupported(width * 300, height * 300);
        var fingerprint = TargetGeometryFingerprint(
            $"project:{setup.Revision}:{setup.PageWidthInches:F4}:{setup.PageHeightInches:F4}:{setup.PageMarginInches:F4}",
            normalizedKind,
            targetId,
            variantId,
            width,
            height,
            regions);
        var descriptor = new LayoutGenerationTargetDescriptor(
            null,
            variantId,
            fingerprint,
            "project-authoring",
            normalizedKind,
            targetId,
            width,
            height,
            aspect,
            recommendedRaster.Width,
            recommendedRaster.Height,
            requestedRaster.Width,
            requestedRaster.Height,
            RasterLabel(requestedRaster),
            300,
            regions,
            diagnostics);
        return ApplySurfaceBounds(descriptor, normalizedKind, surfaceBounds);
    }

    public async Task<CompositionEditionGeometry> GetEditionGeometryAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken = default)
    {
        var edition = (await effectiveConfigurations.ResolveReleaseAsync(projectId, editionId, cancellationToken)).Edition;
        return new CompositionEditionGeometry(
            edition.PageWidthInches * 72,
            edition.PageHeightInches * 72,
            edition.Format == PublicationEditionFormat.DigitalPdf && edition.AllowDesignedPageOverrides);
    }

    public async Task<CompositionEditionGeometry> GetAuthoringGeometryAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var setup = await ReadPageSetupAsync(projectId, cancellationToken);
        return new CompositionEditionGeometry(setup.PageWidthInches * 72, setup.PageHeightInches * 72, false);
    }

    private async Task<ProjectPageSetup> ReadPageSetupAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        return await db.ProjectPageSetups.AsNoTracking().SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken)
                ?? (await db.Projects.AsNoTracking().AnyAsync(item => item.Id == projectId, cancellationToken)
                    ? new ProjectPageSetup { ProjectId = projectId }
                    : throw new KeyNotFoundException("Project was not found."));
    }
    private static string NormalizeGenerationTargetKind(string value)
    {
        var key = new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return key switch
        {
            "projectpage" => "project-page",
            "figure" => "figure",
            "pageframe" => "page-frame",
            "pagesurface" => "page-surface",
            "coverframe" => "cover-frame",
            "coversurface" => "cover-surface",
            "corecoverframe" => "core-cover-frame",
            "corecoversurface" => "core-cover-surface",
            _ => value.Trim().ToLowerInvariant(),
        };
    }

    public async Task<LayoutValidationView> ValidateVariantAsync(
        Guid projectId,
        Guid editionId,
        Guid variantId,
        CancellationToken cancellationToken = default) =>
        await ValidateVariantCoreAsync(projectId, editionId, variantId, cancellationToken);

    public async Task<LayoutValidationView> ValidateAuthoringVariantAsync(
        Guid projectId,
        Guid variantId,
        CancellationToken cancellationToken = default) =>
        await ValidateVariantCoreAsync(projectId, editionId: null, variantId, cancellationToken);

    private async Task<LayoutValidationView> ValidateVariantCoreAsync(
        Guid projectId,
        Guid? editionId,
        Guid variantId,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var edition = editionId is Guid requestedEditionId
            ? await db.PublicationEditions.AsNoTracking().SingleOrDefaultAsync(
                item => item.Id == requestedEditionId && item.ProjectId == projectId,
                cancellationToken) ?? throw new KeyNotFoundException("Edition was not found in this project.")
            : null;
        var variant = await db.DesignedPageVariants.AsNoTracking().Include(item => item.Content)
            .SingleOrDefaultAsync(item => item.Id == variantId
                && item.Content.ProjectId == projectId, cancellationToken)
            ?? throw new KeyNotFoundException("Composition variant was not found in this project.");
        var diagnostics = new List<LayoutValidationDiagnostic>();
        if (edition is not null && !VariantMatchesEdition(variant, edition))
            diagnostics.Add(new("error", "GEOMETRY_VARIANT_MISMATCH", "The variant does not belong to the selected release geometry."));
        var scene = JsonSerializer.Deserialize<CompositionScene>(variant.SceneJson, JsonOptions)
            ?? throw new InvalidDataException("The composition scene is empty.");
        var semantic = ManuscriptCodec.Deserialize(variant.Content.SemanticManuscriptJson);
        try
        {
            if (edition is not null) ValidateVariantGeometry(edition, scene);
            Validate(scene, semantic);
            await ValidateSceneAssetsAsync(projectId, scene, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            diagnostics.Add(new("error", "COMPOSITION_INVALID", exception.Message));
        }
        if (edition?.Vendor == PublicationVendor.IngramSpark
            && CompositionSceneResolver.FindPdfxTransparencyOverlap(scene) is { } opacityOverlap)
        {
            diagnostics.Add(new(
                "error",
                "PDFX_TRANSPARENCY_OVERLAP",
                $"Object {opacityOverlap.TransparentObjectId:N} uses opacity over lower object {opacityOverlap.LowerObjectId:N} in a form that cannot be precomposed while preserving editable semantic text and vector content. Make it opaque or combine the visual artwork into one image.",
                opacityOverlap.TransparentObjectId));
        }

        var flattened = CompositionSceneResolver.Flatten(scene);
        foreach (var item in flattened.Where(item => item.Kind == CompositionObjectKind.Image && IsOutputVisible(scene, item)))
        {
            if (!item.Decorative && (item.AccessibilityDecisionPending || string.IsNullOrWhiteSpace(item.AltText)))
                diagnostics.Add(new("error", "ALT_DECISION_PENDING", "Image alternative text or an explicit decorative decision is required.", item.Id));
        }
        var visibleText = flattened
            .Where(item => item.Kind == CompositionObjectKind.Text && IsOutputVisible(scene, item))
            .ToList();
        var semanticById = semantic.Content.ToDictionary(block => block.Id, StringComparer.Ordinal);
        foreach (var item in visibleText)
        {
            if (string.IsNullOrWhiteSpace(item.TextBinding) && item.ContentReferences.Count == 0)
                diagnostics.Add(new("error", "TEXT_UNBOUND", "Text frame is not bound to semantic composition content.", item.Id));
            if (item.ContentReferences.Count > 0
                && item.ContentReferences.All(reference => semanticById.ContainsKey(reference.BlockId))
                && ResolveCommonTextSemanticRole(item, semanticById) is null)
            {
                diagnostics.Add(new(
                    "error",
                    "TEXT_SEMANTIC_ROLE_MIXED",
                    "A text frame may bind multiple semantic blocks only when they share one PDF and EPUB role.",
                    item.Id));
            }
        }
        try
        {
            var unplaced = ManuscriptRangeResolver.ValidateCoverage(
                semantic,
                visibleText.Select(item => item.ContentReferences));
            if (unplaced.Count > 0)
                diagnostics.Add(new("error", "CONTENT_UNPLACED", $"{unplaced.Count} semantic content block(s) contain unplaced text."));
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(new("error", "CONTENT_BINDING_INVALID", exception.Message));
        }
        var semanticObjects = flattened
            .Where(item => IsOutputVisible(scene, item) && !item.Decorative && item.SemanticRole != CompositionSemanticRole.Artifact)
            .ToList();
        foreach (var item in semanticObjects.Where(item => item.ReadingOrder is null))
            diagnostics.Add(new("error", "READING_ORDER_MISSING", "Semantic object requires a logical reading-order position.", item.Id));
        foreach (var duplicate in semanticObjects.Where(item => item.ReadingOrder is not null).GroupBy(item => item.ReadingOrder!.Value).Where(group => group.Count() > 1))
        {
            foreach (var item in duplicate)
                diagnostics.Add(new("error", "READING_ORDER_DUPLICATE", $"Reading-order position {duplicate.Key} is assigned more than once.", item.Id));
        }
        var measuredPreview = await canvasPreviews.RenderSceneAsync(
            projectId,
            variant.Id,
            variant.Revision,
            scene,
            semantic,
            CompositionCanvasPreviewMode.Clean,
            cancellationToken);
        diagnostics.AddRange(measuredPreview.Diagnostics
            .Where(item => item.Code == "TEXT_OVERFLOW")
            .Select(item => new LayoutValidationDiagnostic(
                "error",
                item.Code,
                "Text exceeds its frame at the current typography.",
                item.ObjectId)));

        var imageIds = flattened.Where(item => item.Kind == CompositionObjectKind.Image && item.ImageId is not null)
            .Select(item => item.ImageId!.Value).Distinct().ToList();
        var assets = await db.PublishAssets.AsNoTracking().Where(item => item.ProjectId == projectId && imageIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var requiredDpi = edition?.Format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover
            ? 300d
            : 180d;
        foreach (var item in flattened.Where(item => edition is not null
            && item.Kind == CompositionObjectKind.Image
            && item.ImageId is not null
            && IsOutputVisible(scene, item)))
        {
            if (!assets.TryGetValue(item.ImageId!.Value, out var asset)) continue;
            using var bitmap = SKBitmap.Decode(asset.Data);
            if (bitmap is null) continue;
            var widthInches = scene.Surface.WidthPoints / 72 * item.Bounds.WidthPercent / 100;
            var heightInches = scene.Surface.HeightPoints / 72 * item.Bounds.HeightPercent / 100;
            var dpi = Math.Min(bitmap.Width / Math.Max(.01, widthInches), bitmap.Height / Math.Max(.01, heightInches));
            if (dpi < requiredDpi)
                diagnostics.Add(new("warning", "IMAGE_DPI_LOW", $"Image resolves to approximately {dpi:0} DPI; this edition expects {requiredDpi:0} DPI.", item.Id));
        }
        var projectFontIds = scene.Objects.Select(item => ParseProjectFontId(item.FontFamilyKey))
            .Concat(scene.Styles.Select(style => ParseProjectFontId(style.FontFamilyKey)))
            .Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
        if (projectFontIds.Count > 0)
        {
            var fonts = await db.ProjectFontFamilies.AsNoTracking().Include(item => item.Faces)
                .Where(item => item.ProjectId == projectId && projectFontIds.Contains(item.Id)).ToListAsync(cancellationToken);
            foreach (var fontId in projectFontIds)
            {
                var font = fonts.FirstOrDefault(item => item.Id == fontId);
                if (font is null || !font.EmbeddingRightsConfirmed || font.Faces.Count == 0)
                    diagnostics.Add(new("error", "FONT_NOT_EMBEDDABLE", "A selected project font is missing, has no face, or lacks an embedding-rights declaration."));
            }
        }
        return new(
            variant.Id,
            variant.Revision,
            diagnostics.Count(item => item.Severity == "error"),
            diagnostics.Count(item => item.Severity == "warning"),
            diagnostics.Take(12).ToList());
    }

    private static Guid? ParseProjectFontId(string key) =>
        key.StartsWith("project:", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(key["project:".Length..], out var id) ? id : null;

    private async Task<(double Width, double Height, IReadOnlyList<LayoutGenerationRegionDescriptor> Regions, IReadOnlyList<string> Diagnostics)> ResolveAuthoringFigureTargetAsync(
        Guid projectId,
        ProjectPageSetup setup,
        Guid targetId,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var blockId = targetId.ToString("N");
        var chapters = await db.Chapters.AsNoTracking().Where(item => item.ProjectId == projectId)
            .Select(item => item.ManuscriptJson).ToListAsync(cancellationToken);
        var figure = chapters.Select(json => ManuscriptCodec.Deserialize(json).Content.FirstOrDefault(block =>
                block.Type == ManuscriptBlockType.Figure && string.Equals(block.Id, blockId, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(block => block is not null)
            ?? throw new KeyNotFoundException("Figure target was not found in this project.");
        var presentation = figure.FigurePresentation ?? new FigurePresentation();
        var (width, height) = LayoutImageSizeResolver.ResolveFlowingFigurePhysicalSize(
            setup.PageWidthInches,
            setup.PageHeightInches,
            setup.PageMarginInches,
            presentation);
        var scene = CreatePageScene(setup);
        var regions = presentation.Placement is FigurePlacementIntent.FullBleed or FigurePlacementIntent.DedicatedPage
            ? PageRegions(scene, includeReservedText: presentation.Placement == FigurePlacementIntent.DedicatedPage)
            : Array.Empty<LayoutGenerationRegionDescriptor>();
        return (width, height, regions, []);
    }

    private async Task<(double Width, double Height, IReadOnlyList<LayoutGenerationRegionDescriptor> Regions, IReadOnlyList<string> Diagnostics)> ResolveAuthoringPageSurfaceTargetAsync(
        Guid projectId,
        Guid contentId,
        Guid variantId,
        CancellationToken cancellationToken)
    {
        var (variant, scene) = await ReadAuthoringVariantSceneAsync(projectId, variantId, cancellationToken);
        if (variant.ContentId != contentId)
            throw new KeyNotFoundException("The selected layout variant does not belong to this page composition.");
        return (scene.Surface.WidthPoints / 72, scene.Surface.HeightPoints / 72, PageRegions(scene, true), []);
    }

    private async Task<(double Width, double Height, IReadOnlyList<LayoutGenerationRegionDescriptor> Regions, IReadOnlyList<string> Diagnostics)> ResolveAuthoringPageFrameTargetAsync(
        Guid projectId,
        Guid frameId,
        Guid variantId,
        CancellationToken cancellationToken)
    {
        var (_, scene) = await ReadAuthoringVariantSceneAsync(projectId, variantId, cancellationToken);
        var frame = CompositionSceneResolver.Flatten(scene).FirstOrDefault(item => item.Id == frameId && item.Kind == CompositionObjectKind.Image)
            ?? throw new KeyNotFoundException("Image frame target was not found in the selected composition variant.");
        return FrameDimensions(scene, frame, PageRegions(scene, true));
    }

    private async Task<(double Width, double Height, IReadOnlyList<LayoutGenerationRegionDescriptor> Regions, IReadOnlyList<string> Diagnostics)> ResolveEditionPageSurfaceTargetAsync(
        Guid projectId,
        PublicationEdition edition,
        Guid contentId,
        Guid variantId,
        CancellationToken cancellationToken)
    {
        var (variant, scene) = await ReadEditionVariantSceneAsync(projectId, edition, variantId, cancellationToken);
        if (variant.ContentId != contentId)
            throw new KeyNotFoundException("The selected release layout variant does not belong to this page composition.");
        return (scene.Surface.WidthPoints / 72, scene.Surface.HeightPoints / 72, PageRegions(scene, true), []);
    }

    private async Task<(double Width, double Height, IReadOnlyList<LayoutGenerationRegionDescriptor> Regions, IReadOnlyList<string> Diagnostics)> ResolveEditionPageFrameTargetAsync(
        Guid projectId,
        PublicationEdition edition,
        Guid frameId,
        Guid variantId,
        CancellationToken cancellationToken)
    {
        var (_, scene) = await ReadEditionVariantSceneAsync(projectId, edition, variantId, cancellationToken);
        var frame = CompositionSceneResolver.Flatten(scene).FirstOrDefault(item => item.Id == frameId && item.Kind == CompositionObjectKind.Image)
            ?? throw new KeyNotFoundException("Image frame target was not found in the selected release composition variant.");
        return FrameDimensions(scene, frame, PageRegions(scene, true));
    }

    private async Task<(DesignedPageVariant Variant, CompositionScene Scene)> ReadEditionVariantSceneAsync(
        Guid projectId,
        PublicationEdition edition,
        Guid variantId,
        CancellationToken cancellationToken)
    {
        var (variant, scene) = await ReadAuthoringVariantSceneAsync(projectId, variantId, cancellationToken);
        if (variant.Content.EditionId != edition.Id)
            throw new InvalidOperationException("The composition variant does not belong to the selected release content.");
        if (!VariantMatchesEdition(variant, edition))
            throw new InvalidOperationException("The composition variant does not match the selected release geometry.");
        return (variant, scene);
    }

    private async Task<(DesignedPageVariant Variant, CompositionScene Scene)> ReadAuthoringVariantSceneAsync(
        Guid projectId,
        Guid variantId,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var variant = await db.DesignedPageVariants.AsNoTracking().Include(item => item.Content)
            .SingleOrDefaultAsync(item => item.Id == variantId
                && item.Content.ProjectId == projectId, cancellationToken)
            ?? throw new KeyNotFoundException("Composition variant was not found in this project.");
        var scene = JsonSerializer.Deserialize<CompositionScene>(variant.SceneJson, JsonOptions)
            ?? throw new InvalidDataException("The composition scene is empty.");
        return (variant, scene);
    }

    private async Task<(double Width, double Height, IReadOnlyList<LayoutGenerationRegionDescriptor> Regions, IReadOnlyList<string> Diagnostics)> ResolveCoreCoverSurfaceTargetAsync(
        Guid projectId,
        ProjectPageSetup setup,
        CancellationToken cancellationToken)
    {
        var scene = await ReadCoreCoverSceneAsync(projectId, cancellationToken);
        var edition = CoreCoverEdition(projectId, setup);
        return (scene.Surface.WidthPoints / 72, scene.Surface.HeightPoints / 72, CoverRegions(scene, edition), []);
    }

    private async Task<(double Width, double Height, IReadOnlyList<LayoutGenerationRegionDescriptor> Regions, IReadOnlyList<string> Diagnostics)> ResolveCoreCoverFrameTargetAsync(
        Guid projectId,
        ProjectPageSetup setup,
        Guid frameId,
        CancellationToken cancellationToken)
    {
        var scene = await ReadCoreCoverSceneAsync(projectId, cancellationToken);
        var frame = CompositionSceneResolver.Flatten(scene).FirstOrDefault(
            item => item.Id == frameId && item.Kind == CompositionObjectKind.Image)
            ?? throw new KeyNotFoundException("Core cover image frame was not found.");
        return FrameDimensions(scene, frame, CoverRegions(scene, CoreCoverEdition(projectId, setup)));
    }

    private async Task<CompositionScene> ReadCoreCoverSceneAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var sceneJson = await db.PublicationBookCoverDesigns.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .Select(item => item.CompositionSceneJson)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(sceneJson))
            throw new KeyNotFoundException("Core cover was not found. Open Publish once to initialize Core Book.");
        var scene = JsonSerializer.Deserialize<CompositionScene>(sceneJson, JsonOptions)
            ?? throw new InvalidDataException("The Core cover composition is empty.");
        return CoverCompositionFactory.KeepArtworkBehindCopy(scene);
    }

    private static PublicationEdition CoreCoverEdition(Guid projectId, ProjectPageSetup setup) => new()
    {
        ProjectId = projectId,
        Name = "Core Book",
        Format = PublicationEditionFormat.DigitalPdf,
        PageWidthInches = setup.PageWidthInches,
        PageHeightInches = setup.PageHeightInches,
        PageMarginInches = setup.PageMarginInches,
        BodyFontSizePoints = setup.BodyFontSizePoints,
        BodyLineHeight = setup.BodyLineHeight,
    };

    private async Task<(double Width, double Height, IReadOnlyList<LayoutGenerationRegionDescriptor> Regions, IReadOnlyList<string> Diagnostics)> ResolveCoverSurfaceTargetAsync(
        Guid projectId,
        PublicationEdition edition,
        Guid targetId,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        if (targetId != edition.Id)
        {
            var ownedCover = await db.PublicationCoverDesigns.AsNoTracking()
                .AnyAsync(item => item.Id == targetId && item.EditionId == edition.Id, cancellationToken);
            if (!ownedCover)
                throw new KeyNotFoundException("Cover surface target was not found for this edition.");
        }
        var (scene, _) = await ReadCoverSceneAsync(projectId, edition, requireCurrentInterior: true, cancellationToken);
        return (scene.Surface.WidthPoints / 72, scene.Surface.HeightPoints / 72, CoverRegions(scene, edition), CoverDiagnostics(edition));
    }

    private async Task<(double Width, double Height, IReadOnlyList<LayoutGenerationRegionDescriptor> Regions, IReadOnlyList<string> Diagnostics)> ResolveCoverFrameTargetAsync(
        Guid projectId,
        PublicationEdition edition,
        Guid frameId,
        CancellationToken cancellationToken)
    {
        var (scene, pageCount) = await ReadCoverSceneAsync(projectId, edition, requireCurrentInterior: false, cancellationToken);
        var frame = CompositionSceneResolver.Flatten(scene).FirstOrDefault(item => item.Id == frameId && item.Kind == CompositionObjectKind.Image)
            ?? throw new KeyNotFoundException("Cover image frame target was not found for this edition.");
        if (edition.Format == PublicationEditionFormat.Paperback
            && pageCount <= 0
            && frame.RegionConstraint is not CompositionRegionConstraint.Front and not CompositionRegionConstraint.Back)
            throw new InvalidOperationException("A current interior page count is required for full-wrap, spine, gutter, or barcode-dependent cover generation.");
        return FrameDimensions(scene, frame, CoverRegions(scene, edition));
    }

    private async Task<(CompositionScene Scene, int PageCount)> ReadCoverSceneAsync(
        Guid projectId,
        PublicationEdition edition,
        bool requireCurrentInterior,
        CancellationToken cancellationToken)
    {
        var cover = await covers.GetAsync(projectId, edition.Id, cancellationToken);
        if (requireCurrentInterior && edition.Format == PublicationEditionFormat.Paperback && cover.Template.PageCount <= 0)
            throw new InvalidOperationException("A current interior page count is required before generating full-wrap cover artwork.");
        var scene = JsonSerializer.Deserialize<CompositionScene>(cover.CompositionSceneJson, JsonOptions)
            ?? throw new InvalidDataException("The cover composition scene is empty.");
        return (CoverCompositionFactory.KeepArtworkBehindCopy(scene), cover.Template.PageCount);
    }

    private static (double Width, double Height, IReadOnlyList<LayoutGenerationRegionDescriptor> Regions, IReadOnlyList<string> Diagnostics) FrameDimensions(
        CompositionScene scene,
        CompositionObject frame,
        IReadOnlyList<LayoutGenerationRegionDescriptor> surfaceRegions)
    {
        var width = scene.Surface.WidthPoints / 72 * frame.Bounds.WidthPercent / 100;
        var height = scene.Surface.HeightPoints / 72 * frame.Bounds.HeightPercent / 100;
        var regions = surfaceRegions
            .Select(region => ToFrameLocalRegion(region, frame.Bounds))
            .Where(region => region is not null)
            .Select(region => region!)
            .ToList();
        return (width, height, regions, []);
    }

    private static LayoutGenerationTargetDescriptor ApplySurfaceBounds(
        LayoutGenerationTargetDescriptor descriptor,
        string normalizedTargetKind,
        CompositionBounds? surfaceBounds)
    {
        if (surfaceBounds is null)
            return descriptor;
        if (normalizedTargetKind is not ("page-surface" or "cover-surface" or "core-cover-surface"))
            throw new ArgumentException("Surface bounds require a Designed Page, release cover, or Core cover surface target.", nameof(surfaceBounds));

        ValidateSurfaceBounds(surfaceBounds);
        var width = descriptor.WidthInches * surfaceBounds.WidthPercent / 100;
        var height = descriptor.HeightInches * surfaceBounds.HeightPercent / 100;
        var requestedRaster = ResolveRasterOrUnsupported(width, height);
        var recommendedRaster = ResolveNearestRasterOrUnsupported(
            width * descriptor.EffectiveDpiExpectation,
            height * descriptor.EffectiveDpiExpectation);
        var regions = descriptor.Regions
            .Select(region => ToFrameLocalRegion(region, surfaceBounds))
            .Where(region => region is not null)
            .Select(region => region!)
            .ToList();
        var fingerprint = TargetGeometryFingerprint(
            descriptor.GeometryKey,
            descriptor.TargetKind,
            descriptor.TargetId,
            descriptor.VariantId,
            width,
            height,
            regions,
            surfaceBounds);
        return descriptor with
        {
            GeometryKey = fingerprint,
            WidthInches = width,
            HeightInches = height,
            AspectRatio = AspectLabel(width, height),
            RecommendedWidthPixels = recommendedRaster.Width,
            RecommendedHeightPixels = recommendedRaster.Height,
            RequestedWidthPixels = requestedRaster.Width,
            RequestedHeightPixels = requestedRaster.Height,
            RequestedRaster = RasterLabel(requestedRaster),
            Regions = regions,
            SurfaceBounds = surfaceBounds,
        };
    }

    private static LayoutImageSize ResolveRasterOrUnsupported(double width, double height)
    {
        try { return LayoutImageSizeResolver.Resolve(width, height); }
        catch (ArgumentException) { return new LayoutImageSize(0, 0); }
    }

    private static LayoutImageSize ResolveNearestRasterOrUnsupported(double widthPixels, double heightPixels)
    {
        try { return LayoutImageSizeResolver.ResolveNearest(widthPixels, heightPixels); }
        catch (ArgumentException) { return new LayoutImageSize(0, 0); }
    }

    private static string RasterLabel(LayoutImageSize raster) =>
        raster.Width > 0 && raster.Height > 0 ? raster.Size : "unsupported";

    private static void ValidateSurfaceBounds(CompositionBounds bounds)
    {
        if (!double.IsFinite(bounds.XPercent)
            || !double.IsFinite(bounds.YPercent)
            || !double.IsFinite(bounds.WidthPercent)
            || !double.IsFinite(bounds.HeightPercent)
            || bounds.XPercent < 0
            || bounds.YPercent < 0
            || bounds.WidthPercent <= 0
            || bounds.HeightPercent <= 0
            || bounds.XPercent + bounds.WidthPercent > 100
            || bounds.YPercent + bounds.HeightPercent > 100)
        {
            throw new ArgumentException("Surface bounds must use finite positive percentage dimensions inside the 0-100 surface.", nameof(bounds));
        }
    }

    private static LayoutGenerationRegionDescriptor? ToFrameLocalRegion(
        LayoutGenerationRegionDescriptor region,
        CompositionBounds frame)
    {
        var left = Math.Max(region.Bounds.XPercent, frame.XPercent);
        var top = Math.Max(region.Bounds.YPercent, frame.YPercent);
        var right = Math.Min(
            region.Bounds.XPercent + region.Bounds.WidthPercent,
            frame.XPercent + frame.WidthPercent);
        var bottom = Math.Min(
            region.Bounds.YPercent + region.Bounds.HeightPercent,
            frame.YPercent + frame.HeightPercent);
        if (right <= left || bottom <= top) return null;
        return region with
        {
            Bounds = new CompositionBounds
            {
                XPercent = (left - frame.XPercent) / frame.WidthPercent * 100,
                YPercent = (top - frame.YPercent) / frame.HeightPercent * 100,
                WidthPercent = (right - left) / frame.WidthPercent * 100,
                HeightPercent = (bottom - top) / frame.HeightPercent * 100,
            },
        };
    }

    private static IReadOnlyList<LayoutGenerationRegionDescriptor> PageRegions(
        CompositionScene scene,
        bool includeReservedText)
    {
        var regions = new List<LayoutGenerationRegionDescriptor>
        {
            new("trim", "Trim boundary", new CompositionBounds(), false),
        };
        var safeX = Math.Clamp(scene.Surface.SafeInsetPoints / scene.Surface.WidthPoints * 100, 0, 49);
        var safeY = Math.Clamp(scene.Surface.SafeInsetPoints / scene.Surface.HeightPoints * 100, 0, 49);
        regions.Add(new("safe-area", "Safe content area", new CompositionBounds
        {
            XPercent = safeX,
            YPercent = safeY,
            WidthPercent = 100 - safeX * 2,
            HeightPercent = 100 - safeY * 2,
        }, false));
        if (scene.Surface.BleedPoints > 0)
            regions.Add(new("bleed", "Bleed boundary", new CompositionBounds(), false));
        if (scene.Surface.Kind == CompositionSurfaceKind.FacingSpread)
        {
            var gutterWidth = Math.Clamp(scene.Surface.SafeInsetPoints / scene.Surface.WidthPoints * 200, .5, 10);
            regions.Add(new("gutter", "Facing-page gutter", new CompositionBounds
            {
                XPercent = 50 - gutterWidth / 2,
                WidthPercent = gutterWidth,
            }, true));
        }
        if (includeReservedText)
        {
            regions.AddRange(CompositionSceneResolver.Flatten(scene)
                .Where(item => item.Visible && item.Kind == CompositionObjectKind.Text)
                .Select(item => new LayoutGenerationRegionDescriptor(
                    "reserved-text",
                    string.IsNullOrWhiteSpace(item.Name) ? "Reserved text frame" : $"Reserved text: {item.Name}",
                    item.Bounds,
                    true)));
        }
        return regions;
    }

    private static IReadOnlyList<LayoutGenerationRegionDescriptor> CoverRegions(
        CompositionScene scene,
        PublicationEdition edition)
    {
        var regions = PageRegions(scene, includeReservedText: true).ToList();
        if (edition.Format != PublicationEditionFormat.Paperback)
        {
            regions.Add(new("front", "Front cover", new CompositionBounds(), false));
            return regions;
        }
        var surfaceWidth = scene.Surface.WidthPoints;
        var surfaceHeight = scene.Surface.HeightPoints;
        var bleed = scene.Surface.BleedPoints;
        var trimWidth = edition.PageWidthInches * 72;
        var trimHeight = edition.PageHeightInches * 72;
        var spine = Math.Max(0, surfaceWidth - trimWidth * 2 - bleed * 2);
        CompositionBounds Bounds(double x, double y, double width, double height) => new()
        {
            XPercent = x / surfaceWidth * 100,
            YPercent = y / surfaceHeight * 100,
            WidthPercent = width / surfaceWidth * 100,
            HeightPercent = height / surfaceHeight * 100,
        };
        regions.Add(new("back", "Back cover", Bounds(bleed, bleed, trimWidth, trimHeight), false));
        regions.Add(new("spine", "Spine", Bounds(bleed + trimWidth, bleed, Math.Max(spine, .01), trimHeight), true));
        regions.Add(new("front", "Front cover", Bounds(surfaceWidth - bleed - trimWidth, bleed, trimWidth, trimHeight), false));
        regions.Add(new("barcode-reserve", "Barcode reserve", Bounds(bleed + 18, Math.Max(bleed, surfaceHeight - bleed - 104.4), 144, 86.4), true));
        return regions;
    }

    private static IReadOnlyList<string> CoverDiagnostics(PublicationEdition edition) =>
        edition.Format == PublicationEditionFormat.Paperback
            ? ["Keep essential copy and focal content outside the bleed, gutter, spine folds, and barcode placement area; background artwork may continue through the barcode area."]
            : ["Keep essential artwork and text inside the digital cover safe area."];

    public static string GeometryKey(PublicationEdition edition) => GeometryKey(edition, CreatePageScene(edition));

    public static string GeometryKey(PublicationEdition edition, CompositionScene scene)
        => SceneGeometryKey(scene);

    public static string SceneGeometryKey(CompositionScene scene)
    {
        var canonical = FormattableString.Invariant(
            $"authoring|{scene.Surface.Kind}|{scene.Surface.OutputPageMode}|{scene.Surface.WidthPoints:F4}|{scene.Surface.HeightPoints:F4}|{scene.Surface.BleedPoints:F4}|{scene.Surface.SafeInsetPoints:F4}|{scene.Surface.AllowIndependentPdfPage}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..24];
    }

    public static bool VariantMatchesEdition(DesignedPageVariant variant, PublicationEdition edition)
    {
        try
        {
            var scene = JsonSerializer.Deserialize<CompositionScene>(variant.SceneJson, JsonOptions);
            if (scene is null) return false;
            ValidateVariantGeometry(edition, scene);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private async Task<string> ExactGeometryKeyAsync(
        Guid projectId,
        string currentKey,
        CompositionScene scene,
        CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return SceneGeometryKey(scene);
    }

    internal static string LegacyEditionOnlyGeometryKey(PublicationEdition edition)
    {
        var canonical = GeometryCanonical(edition)
            + (edition.Format == PublicationEditionFormat.DigitalPdf && edition.AllowDesignedPageOverrides
                ? "|independent-pages"
                : string.Empty);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..24];
    }

    internal static string LegacyGeometryKey(PublicationEdition edition)
    {
        var canonical = GeometryCanonical(edition);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..24];
    }

    private static string GeometryCanonical(PublicationEdition edition) => FormattableString.Invariant(
        $"{edition.Format}|{edition.PageWidthInches:F4}|{edition.PageHeightInches:F4}|{edition.PageMarginInches:F4}|{edition.Bleed}|{edition.PrintArtifactProfileKey}|{edition.PrintCoverMode}|{edition.VendorProfileVersion}");

    public static CompositionScene CreatePageScene(
        PublicationEdition edition,
        DesignedPageLayoutMode layoutMode = DesignedPageLayoutMode.SinglePage) => new()
        {
            Surface = new CompositionSurface
            {
                Kind = layoutMode == DesignedPageLayoutMode.FacingSpread
                ? CompositionSurfaceKind.FacingSpread
                : CompositionSurfaceKind.SinglePage,
                WidthPoints = edition.PageWidthInches * 72 * (layoutMode == DesignedPageLayoutMode.FacingSpread ? 2 : 1),
                HeightPoints = edition.PageHeightInches * 72,
                BleedPoints = edition.Bleed ? 9 : 0,
                SafeInsetPoints = edition.PageMarginInches * 72,
                AllowIndependentPdfPage = edition.Format == PublicationEditionFormat.DigitalPdf
                && edition.AllowDesignedPageOverrides,
            },
            Layers = [new CompositionLayer(Guid.NewGuid(), "Content", 0)],
        };

    public static CompositionScene CreatePageScene(
        ProjectPageSetup setup,
        DesignedPageLayoutMode layoutMode = DesignedPageLayoutMode.SinglePage) => new()
        {
            Surface = new CompositionSurface
            {
                Kind = layoutMode == DesignedPageLayoutMode.FacingSpread
                ? CompositionSurfaceKind.FacingSpread
                : CompositionSurfaceKind.SinglePage,
                WidthPoints = setup.PageWidthInches * 72 * (layoutMode == DesignedPageLayoutMode.FacingSpread ? 2 : 1),
                HeightPoints = setup.PageHeightInches * 72,
                SafeInsetPoints = setup.PageMarginInches * 72,
            },
            Layers = [new CompositionLayer(Guid.NewGuid(), "Content", 0)],
        };

    private static string SerializeAndValidate(CompositionScene scene, string semanticJson)
    {
        Validate(scene, ManuscriptCodec.Deserialize(semanticJson));
        return JsonSerializer.Serialize(scene, JsonOptions);
    }

    private async Task ValidateSceneAssetsAsync(
        Guid projectId,
        CompositionScene scene,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var imageIds = scene.Objects
            .Where(item => item.Visible && item.Kind == CompositionObjectKind.Image && item.ImageId is not null)
            .Select(item => item.ImageId!.Value)
            .Distinct()
            .ToArray();
        if (imageIds.Length == 0) return;
        var owned = await db.PublishAssets.AsNoTracking()
            .CountAsync(item => item.ProjectId == projectId
                && imageIds.Contains(item.Id)
                && (item.ContentType == "image/png" || item.ContentType == "image/jpeg"), cancellationToken);
        if (owned != imageIds.Length)
            throw new InvalidDataException("The composition references an image outside this project or a non-publication PNG/JPEG asset.");
    }

    private async Task ValidateVariantGeometryAsync(
        Guid projectId,
        string geometryKey,
        CompositionScene scene,
        CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        if (scene.Surface.WidthPoints is < 72 or > 3456 || scene.Surface.HeightPoints is < 72 or > 3456)
            throw new InvalidDataException("The composition surface dimensions are outside the supported authoring range.");
        if (string.IsNullOrWhiteSpace(geometryKey))
            throw new InvalidDataException("The composition geometry fingerprint is missing.");
    }

    internal static void ValidateVariantGeometry(PublicationEdition edition, CompositionScene scene)
    {
        var leafWidth = edition.PageWidthInches * 72;
        var leafHeight = edition.PageHeightInches * 72;
        var print = edition.Format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover;
        var expectedWidth = scene.Surface.Kind == CompositionSurfaceKind.FacingSpread
            ? leafWidth * 2
            : leafWidth;
        var independent = scene.Surface.Kind == CompositionSurfaceKind.IndependentPage;
        if (independent && (edition.Format != PublicationEditionFormat.DigitalPdf || !edition.AllowDesignedPageOverrides))
            throw new InvalidDataException("Independent page geometry is enabled only for Digital PDF editions that allow Designed Page overrides.");
        if (!independent && (Math.Abs(scene.Surface.WidthPoints - expectedWidth) > .01
            || Math.Abs(scene.Surface.HeightPoints - leafHeight) > .01))
            throw new InvalidDataException("The composition surface must use the edition's exact leaf or facing-spread geometry.");
        if (print && independent)
            throw new InvalidDataException("Print editions require consistent physical leaf dimensions.");
        if (print && scene.Surface.OutputPageMode != CompositionOutputPageMode.EditionLeaves)
            throw new InvalidDataException("Print Designed Pages must output one trim-sized leaf or two edition leaves for a facing spread.");
    }

    internal static void Validate(
        CompositionScene scene,
        ManuscriptDocument semantic,
        bool allowCanonicalTextBindings = false)
    {
        ValidateSemanticFragment(semantic);
        if (scene.SchemaVersion != CompositionScene.CurrentSchemaVersion)
            throw new InvalidDataException("Unsupported composition scene schema version.");
        if (scene.Surface.WidthPoints is <= 0 or > 2_880 || scene.Surface.HeightPoints is <= 0 or > 2_880)
            throw new InvalidDataException("Composition surfaces must be no larger than 40 inches on either side.");
        if (scene.Layers.Count is < 1 or > 64
            || scene.Styles.Count > 128
            || scene.Guides.Count > 256
            || scene.Objects.Count > 2_000
            || semantic.Content.Count > 2_000
            || scene.Objects.Any(item => item.ContentReferences.Count > 128))
            throw new InvalidDataException("Composition limits are 64 layers, 128 styles, 256 guides, 2,000 objects, 2,000 semantic blocks, and 128 ranges per text frame.");
        var layerIds = scene.Layers.Select(layer => layer.Id).ToHashSet();
        if (layerIds.Count != scene.Layers.Count || layerIds.Contains(Guid.Empty))
            throw new InvalidDataException("Composition layer IDs must be non-empty and unique.");
        var styleIds = scene.Styles.Select(style => style.Id).ToHashSet();
        if (styleIds.Count != scene.Styles.Count || styleIds.Contains(Guid.Empty)
            || scene.Styles.Any(style => string.IsNullOrWhiteSpace(style.Name)
                || style.FontSizePoints is < 4 or > 288
                || style.LineHeight is < .5 or > 4
                || style.LetterSpacingEm is < -1 or > 10
                || style.BackgroundOpacity is < 0 or > 1
                || style.StrokeWidthPoints is < 0 or > 72))
            throw new InvalidDataException("Composition styles require unique IDs, names, and valid typography and stroke values.");
        var objectIds = new HashSet<Guid>();
        var readingOrder = new HashSet<int>();
        var semanticById = semantic.Content.ToDictionary(block => block.Id, StringComparer.Ordinal);
        foreach (var item in scene.Objects)
        {
            if (item.Id == Guid.Empty || !objectIds.Add(item.Id) || !layerIds.Contains(item.LayerId))
                throw new InvalidDataException("Composition objects require unique IDs and an existing layer.");
            if (!double.IsFinite(item.Bounds.XPercent)
                || !double.IsFinite(item.Bounds.YPercent)
                || !double.IsFinite(item.Bounds.WidthPercent)
                || !double.IsFinite(item.Bounds.HeightPercent)
                || item.Bounds.WidthPercent is <= 0 or > 400 || item.Bounds.HeightPercent is <= 0 or > 400
                || item.Opacity is < 0 or > 1 || item.BackgroundOpacity is < 0 or > 1
                || item.LetterSpacingEm is < -1 or > 10
                || item.CropXPercent is < 0 or > 100 || item.CropYPercent is < 0 or > 100)
                throw new InvalidDataException($"Composition object {item.Id:N} has out-of-range geometry.");
            if (!item.Decorative && item.SemanticRole != CompositionSemanticRole.Artifact
                && (item.ReadingOrder is null || !readingOrder.Add(item.ReadingOrder.Value)))
                throw new InvalidDataException($"Semantic composition object {item.Id:N} requires a unique reading order.");
            if (item.Kind == CompositionObjectKind.Image && item.ImageId is null)
                throw new InvalidDataException($"Image object {item.Id:N} requires a project image.");
            if (item.Kind == CompositionObjectKind.Text && !allowCanonicalTextBindings
                && (!string.IsNullOrWhiteSpace(item.TextBinding) || item.ContentReferences.Count == 0))
                throw new InvalidDataException($"Text object {item.Id:N} must bind semantic composition content and cannot store literal page copy.");
            if (item.StyleId is Guid styleId && !styleIds.Contains(styleId))
                throw new InvalidDataException($"Composition object {item.Id:N} references a missing object style.");
            if (item.Kind == CompositionObjectKind.Image && !item.Decorative
                && string.IsNullOrWhiteSpace(item.AltText) && !item.AccessibilityDecisionPending)
                throw new InvalidDataException($"Image object {item.Id:N} requires alternative text or a decorative decision.");
            if (item.ContentReferences.Any(reference => !semanticById.ContainsKey(reference.BlockId)))
                throw new InvalidDataException($"Text object {item.Id:N} references content outside its composition document.");
            if (item.Kind == CompositionObjectKind.Text
                && item.ContentReferences.Count > 0
                && ResolveCommonTextSemanticRole(item, semanticById) is null)
            {
                throw new InvalidDataException(
                    $"Text object {item.Id:N} may bind multiple semantic blocks only when they share one PDF and EPUB role.");
            }
        }
        _ = ManuscriptRangeResolver.ValidateCoverage(
            semantic,
            scene.Objects.Where(item => item.Kind == CompositionObjectKind.Text).Select(item => item.ContentReferences));
        if (scene.Guides.Any(guide => guide.Id == Guid.Empty || guide.PositionPercent is < 0 or > 100)
            || scene.Guides.Select(guide => guide.Id).Distinct().Count() != scene.Guides.Count)
            throw new InvalidDataException("Composition guides require unique IDs and positions from 0 to 100 percent.");
        var groups = scene.Objects.Where(item => item.Kind == CompositionObjectKind.Group).ToDictionary(item => item.Id);
        foreach (var item in scene.Objects.Where(item => item.GroupId is not null))
        {
            if (!groups.TryGetValue(item.GroupId!.Value, out var group)
                || group.GroupId is not null
                || group.LayerId != item.LayerId)
                throw new InvalidDataException($"Composition object {item.Id:N} has an invalid group owner.");
        }
        if (groups.Values.Any(group => scene.Objects.Count(item => item.GroupId == group.Id) < 2))
            throw new InvalidDataException("A composition group must own at least two objects.");
    }

    private static void ValidateSemanticFragment(ManuscriptDocument semantic)
    {
        if (semantic.Content.Any(block => block.Type is ManuscriptBlockType.Figure or ManuscriptBlockType.DesignedPage))
            throw new InvalidDataException("Designed Page semantic content supports text, headings, lists, quotes, and scene breaks; visual blocks belong in the composition scene.");
    }

    internal static CompositionScene WithDerivedTextSemanticRoles(
        CompositionScene scene,
        ManuscriptDocument semantic)
    {
        var blocks = semantic.Content.ToDictionary(block => block.Id, StringComparer.Ordinal);
        return scene with
        {
            Objects = scene.Objects.Select(item => item.Kind == CompositionObjectKind.Text
                    && ResolveCommonTextSemanticRole(item, blocks) is { } role
                ? item with { SemanticRole = role }
                : item).ToList(),
        };
    }

    private static CompositionSemanticRole? ResolveCommonTextSemanticRole(
        CompositionObject item,
        IReadOnlyDictionary<string, ManuscriptBlock> blocks)
    {
        var roles = item.ContentReferences
            .Select(reference => blocks.GetValueOrDefault(reference.BlockId))
            .Where(block => block is not null)
            .Select(block => SemanticRole(block!))
            .Distinct()
            .Take(2)
            .ToList();
        return roles.Count == 1 ? roles[0] : null;
    }

    private static CompositionSemanticRole SemanticRole(ManuscriptBlock block) => block.Type switch
    {
        ManuscriptBlockType.Heading when block.HeadingLevel is null or <= 1 => CompositionSemanticRole.Heading1,
        ManuscriptBlockType.Heading when block.HeadingLevel == 2 => CompositionSemanticRole.Heading2,
        ManuscriptBlockType.Heading => CompositionSemanticRole.Heading3,
        ManuscriptBlockType.Paragraph when string.Equals(block.StyleRole, ManuscriptStyleRoles.FigureCaption, StringComparison.OrdinalIgnoreCase)
            => CompositionSemanticRole.Caption,
        _ => CompositionSemanticRole.Paragraph,
    };

    internal static bool IsOutputVisible(CompositionScene scene, CompositionObject item) =>
        item.Visible
        && scene.Layers.Any(layer => layer.Id == item.LayerId && layer.Visible)
        && (item.GroupId is not Guid groupId
            || scene.Objects.FirstOrDefault(candidate => candidate.Id == groupId) is { Visible: true });

    private static string AspectLabel(double width, double height)
    {
        var scaledWidth = (long)Math.Round(width * 1_000_000);
        var scaledHeight = (long)Math.Round(height * 1_000_000);
        var gcd = GreatestCommonDivisor(scaledWidth, scaledHeight);
        return $"{scaledWidth / gcd}:{scaledHeight / gcd}";
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
            (left, right) = (right, left % right);
        return Math.Max(left, 1);
    }

    private static long GreatestCommonDivisor(long left, long right)
    {
        while (right != 0)
            (left, right) = (right, left % right);
        return Math.Max(left, 1);
    }

    private static string TargetGeometryFingerprint(
        PublicationEdition edition,
        string targetKind,
        Guid targetId,
        Guid? variantId,
        double width,
        double height,
        IReadOnlyList<LayoutGenerationRegionDescriptor> regions,
        CompositionBounds? surfaceBounds = null)
        => TargetGeometryFingerprint(GeometryCanonical(edition), targetKind, targetId, variantId, width, height, regions, surfaceBounds);

    private static string TargetGeometryFingerprint(
        string geometrySource,
        string targetKind,
        Guid targetId,
        Guid? variantId,
        double width,
        double height,
        IReadOnlyList<LayoutGenerationRegionDescriptor> regions,
        CompositionBounds? surfaceBounds = null)
    {
        var canonical = new StringBuilder()
            .Append(geometrySource).Append('|')
            .Append(targetKind).Append('|').Append(targetId.ToString("N")).Append('|')
            .Append(variantId?.ToString("N") ?? "-").Append('|')
            .Append(width.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
            .Append(height.ToString("F6", System.Globalization.CultureInfo.InvariantCulture));
        if (surfaceBounds is not null)
        {
            canonical.Append("|surface-bounds|")
                .Append(surfaceBounds.XPercent.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                .Append(surfaceBounds.YPercent.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                .Append(surfaceBounds.WidthPercent.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                .Append(surfaceBounds.HeightPercent.ToString("F6", System.Globalization.CultureInfo.InvariantCulture));
        }
        foreach (var region in regions.OrderBy(item => item.Kind, StringComparer.Ordinal).ThenBy(item => item.Label, StringComparer.Ordinal))
        {
            canonical.Append('|').Append(region.Kind).Append('|').Append(region.Label).Append('|')
                .Append(region.Bounds.XPercent.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                .Append(region.Bounds.YPercent.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                .Append(region.Bounds.WidthPercent.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                .Append(region.Bounds.HeightPercent.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                .Append(region.KeepClear);
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))[..24];
    }

    public async Task<DesignedPageView?> GetAsync(
        Guid projectId,
        Guid pageId,
        EditorContentTarget target,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        var page = await operation.Db.DesignedPages.AsNoTracking()
            .Include(item => item.PlacementReferences)
            .Include(item => item.Contents)
                .ThenInclude(item => item.Variants.OrderBy(variant => variant.GeometryKey))
            .SingleOrDefaultAsync(item => item.ProjectId == projectId && item.Id == pageId, cancellationToken);
        return page is null ? null : SelectEffective(page, target);
    }

    public async Task<DesignedPageView> CreateAsync(
        Guid projectId,
        EditorContentTarget target,
        string name,
        DesignedPageInitialContent? initialContent = null,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenWriteAsync(projectId, cancellationToken);
        operation.ShareWithNestedOperations();
        var db = operation.Db;
        await EnsureEditableTargetAsync(db, projectId, target, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var page = new DesignedPage
        {
            ProjectId = projectId,
            ScopeEditionId = target.EditionId,
            Name = string.IsNullOrWhiteSpace(name) ? "Designed page" : name.Trim(),
        };
        var content = new DesignedPageContent
        {
            Id = page.Id,
            ProjectId = projectId,
            DesignedPageId = page.Id,
            Page = page,
            EditionId = target.EditionId,
            SemanticManuscriptJson = ManuscriptCodec.Serialize(ManuscriptCodec.CreateEmpty(page.Id)),
        };
        var edition = target.EditionId is Guid editionId
            ? (await effectiveConfigurations.ResolveReleaseAsync(projectId, editionId, cancellationToken)).Edition
            : null;
        var scene = edition is null
            ? await CreateInitialPageSceneAsync(projectId, await RequirePageSetupUnderLeaseAsync(projectId, cancellationToken), initialContent, cancellationToken)
            : await CreateInitialPageSceneAsync(projectId, edition, initialContent, cancellationToken);
        var variant = new DesignedPageVariant
        {
            Content = content,
            ContentId = content.Id,
            GeometryKey = SceneGeometryKey(scene),
            SceneJson = SerializeAndValidate(scene, content.SemanticManuscriptJson),
        };
        var activeVariantId = variant.Id;
        content.Variants.Add(variant);
        page.Contents.Add(content);
        db.DesignedPages.Add(page);
        await TouchProjectAsync(projectId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        content.ActiveVariantId = activeVariantId;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new DesignedPageView(page, content, false);
    }

    public async Task<IReadOnlyList<DesignedPageView>> ListAsync(
        Guid projectId,
        EditorContentTarget target,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        var pages = await operation.Db.DesignedPages.AsNoTracking()
            .Include(item => item.PlacementReferences)
            .Include(item => item.Contents)
                .ThenInclude(item => item.Variants.OrderBy(variant => variant.GeometryKey))
            .Where(item => item.ProjectId == projectId
                && (item.ScopeEditionId == null || item.ScopeEditionId == target.EditionId))
            .OrderBy(item => item.Name)
            .ThenBy(item => item.CreatedAt)
            .ToListAsync(cancellationToken);
        return pages.Select(page => SelectEffective(page, target)).ToList();
    }

    public async Task<DesignedPageView> EnsureReleaseOverrideAsync(
        Guid projectId,
        Guid pageId,
        Guid editionId,
        CancellationToken cancellationToken = default)
    {
        var target = EditorContentTarget.ForEdition(editionId);
        await using var operation = await database.OpenWriteAsync(projectId, cancellationToken);
        operation.ShareWithNestedOperations();
        var db = operation.Db;
        await EnsureEditableTargetAsync(db, projectId, target, cancellationToken);
        var page = await db.DesignedPages.AsTracking()
            .Include(item => item.Contents)
                .ThenInclude(item => item.Variants)
            .SingleOrDefaultAsync(item => item.ProjectId == projectId && item.Id == pageId, cancellationToken)
            ?? throw new KeyNotFoundException("The Designed Page was not found in this project.");
        if (page.ScopeEditionId is Guid scopeId && scopeId != editionId)
            throw new InvalidOperationException("This release-only Designed Page is not available to the selected release.");
        var existing = page.Contents.SingleOrDefault(item => item.EditionId == editionId);
        if (existing is not null)
            return new DesignedPageView(page, existing, false);
        if (page.ScopeEditionId == editionId)
            throw new InvalidDataException("The release-only Designed Page is missing its content.");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        _ = await db.PublicationEditions.AsNoTracking().SingleOrDefaultAsync(
            item => item.ProjectId == projectId && item.Id == editionId,
            cancellationToken) ?? throw new KeyNotFoundException("The publication release was not found in this project.");
        var source = page.Contents.SingleOrDefault(item => item.EditionId == null)
            ?? throw new InvalidDataException("The Designed Page is missing Core content.");
        var contentId = Guid.NewGuid();
        var semantic = ManuscriptCodec.Deserialize(source.SemanticManuscriptJson) with { ManuscriptId = contentId };
        var content = new DesignedPageContent
        {
            Id = contentId,
            ProjectId = projectId,
            DesignedPageId = page.Id,
            Page = page,
            EditionId = editionId,
            SemanticManuscriptJson = ManuscriptCodec.Serialize(semantic),
            AccessibilityDescription = source.AccessibilityDescription,
            Revision = source.Revision,
        };
        var variantIds = new Dictionary<Guid, Guid>();
        foreach (var sourceVariant in source.Variants)
        {
            var variant = new DesignedPageVariant
            {
                Content = content,
                ContentId = content.Id,
                GeometryKey = sourceVariant.GeometryKey,
                SceneJson = sourceVariant.SceneJson,
                Revision = sourceVariant.Revision,
            };
            content.Variants.Add(variant);
            variantIds[sourceVariant.Id] = variant.Id;
        }
        var activeVariantId = source.ActiveVariantId is Guid activeId
            && variantIds.TryGetValue(activeId, out var clonedActiveId)
                ? clonedActiveId
                : (Guid?)null;
        db.DesignedPageContents.Add(content);
        page.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        content.ActiveVariantId = activeVariantId;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await RefreshPlacedChaptersAsync(target, projectId, page.Id, cancellationToken);
        return new DesignedPageView(page, content, false);
    }

    public async Task<DesignedPageView> ResetReleaseOverrideAsync(
        Guid projectId,
        Guid pageId,
        Guid editionId,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenWriteAsync(projectId, cancellationToken);
        operation.ShareWithNestedOperations();
        var db = operation.Db;
        await EnsureEditableTargetAsync(db, projectId, EditorContentTarget.ForEdition(editionId), cancellationToken);
        var page = await db.DesignedPages.AsTracking().Include(item => item.Contents)
            .SingleOrDefaultAsync(item => item.ProjectId == projectId && item.Id == pageId, cancellationToken)
            ?? throw new KeyNotFoundException("The Designed Page was not found in this project.");
        if (page.ScopeEditionId is not null)
            throw new InvalidOperationException("A release-only Designed Page has no Core content to inherit.");
        var core = page.Contents.SingleOrDefault(item => item.EditionId == null)
            ?? throw new InvalidDataException("The Designed Page is missing Core content.");
        var release = page.Contents.SingleOrDefault(item => item.EditionId == editionId);
        if (release is not null)
            db.DesignedPageContents.Remove(release);
        page.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        if (release is not null)
            deltaHistory.Clear($"release:{editionId:D}:designed-page-content:{release.Id:D}");
        await RefreshPlacedChaptersAsync(EditorContentTarget.ForEdition(editionId), projectId, pageId, cancellationToken);
        return new DesignedPageView(page, core, true);
    }

    public async Task<DesignedPageView> DuplicateAsync(
        Guid projectId,
        Guid pageId,
        EditorContentTarget target,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenWriteAsync(projectId, cancellationToken);
        var db = operation.Db;
        await EnsureEditableTargetAsync(db, projectId, target, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var sourcePage = await db.DesignedPages.AsNoTracking()
            .Include(item => item.Contents)
                .ThenInclude(item => item.Variants)
            .SingleOrDefaultAsync(item => item.ProjectId == projectId && item.Id == pageId, cancellationToken)
            ?? throw new KeyNotFoundException("The Designed Page was not found in this project.");
        var source = SelectEffective(sourcePage, target).Content;
        var duplicate = new DesignedPage
        {
            ProjectId = projectId,
            ScopeEditionId = target.EditionId,
            Name = string.IsNullOrWhiteSpace(name) ? $"{sourcePage.Name} copy" : name.Trim(),
        };
        var content = CloneContentForPage(source, duplicate, target.EditionId, duplicate.Id);
        var activeVariantId = content.ActiveVariantId;
        content.ActiveVariantId = null;
        duplicate.Contents.Add(content);
        db.DesignedPages.Add(duplicate);
        await db.SaveChangesAsync(cancellationToken);
        content.ActiveVariantId = activeVariantId;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new DesignedPageView(duplicate, content, false);
    }

    public async Task DeleteAsync(
        Guid projectId,
        Guid pageId,
        bool clearAffectedHistory = false,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenWriteAsync(projectId, cancellationToken);
        var db = operation.Db;
        var page = await db.DesignedPages.AsTracking()
            .Include(item => item.PlacementReferences)
            .Include(item => item.Contents)
            .SingleOrDefaultAsync(item => item.ProjectId == projectId && item.Id == pageId, cancellationToken)
            ?? throw new KeyNotFoundException("The Designed Page was not found in this project.");
        var releaseIds = page.Contents
            .Where(item => item.EditionId.HasValue)
            .Select(item => item.EditionId!.Value)
            .ToHashSet();
        if (releaseIds.Count > 0 && await db.PublicationEditions.AsNoTracking().AnyAsync(
            item => item.ProjectId == projectId
                && releaseIds.Contains(item.Id)
                && item.Status == PublicationEditionStatus.Archived,
            cancellationToken))
        {
            throw new InvalidOperationException("Archived publication releases are read-only. Clone the release before deleting this Designed Page.");
        }
        if (page.PlacementReferences.Count != 0)
            throw new InvalidOperationException("Remove every live placement before deleting this Designed Page.");
        var contentTargets = page.Contents
            .Select(content => content.EditionId is Guid editionId
                ? $"release:{editionId:D}:designed-page-content:{content.Id:D}"
                : $"designed-page-content:{content.Id:D}")
            .ToList();
        var ownHistory = new List<string>();
        foreach (var target in contentTargets)
        {
            var state = deltaHistory.Read(target).State;
            if (state.CanUndo || state.CanRedo)
                ownHistory.Add(target);
        }
        var dependentHistory = deltaHistory.FindDependentTargets(
            projectId, AuthoringHistoryDependencyKind.DesignedPage, pageId);
        var affectedHistoryCount = ownHistory
            .Concat(dependentHistory)
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (affectedHistoryCount > 0 && !clearAffectedHistory)
        {
            throw new InvalidOperationException(
                $"AUTHORING_HISTORY_DEPENDENCY: This Designed Page is retained by {affectedHistoryCount} current in-process Undo/Redo histor{(affectedHistoryCount == 1 ? "y" : "ies")}. Delete it and clear the affected history?");
        }
        db.DesignedPages.Remove(page);
        await db.SaveChangesAsync(cancellationToken);
        foreach (var target in contentTargets)
            deltaHistory.Clear(target);
        if (dependentHistory.Count > 0)
            deltaHistory.ClearDependentTargets(projectId, AuthoringHistoryDependencyKind.DesignedPage, pageId);
    }

    public async Task<DesignedPagePlacementMutationResult> PlaceAsync(
        EditorContentTarget target,
        Guid projectId,
        Guid pageId,
        DesignedPageContainer container,
        int blockIndex,
        long expectedRevision,
        string? placementId = null,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenWriteAsync(projectId, cancellationToken);
        operation.ShareWithNestedOperations();
        var db = operation.Db;
        await EnsureEditableTargetAsync(db, projectId, target, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var page = await db.DesignedPages.AsTracking().Include(item => item.Contents)
            .SingleOrDefaultAsync(item => item.ProjectId == projectId && item.Id == pageId, cancellationToken)
            ?? throw new KeyNotFoundException("The Designed Page was not found in this project.");
        var view = SelectEffective(page, target);
        var actualPlacementId = string.IsNullOrWhiteSpace(placementId) ? Guid.NewGuid().ToString("N") : placementId;
        if (await db.DesignedPagePlacementReferences.AnyAsync(
            item => item.ProjectId == projectId
                && item.ContainerKind == container.Kind
                && item.ContainerId == container.Id
                && item.EditionId == target.EditionId
                && item.Id == actualPlacementId,
            cancellationToken))
            throw new InvalidOperationException($"Designed Page placement {actualPlacementId} already exists.");
        var revision = await ApplyContainerOperationsAsync(
            db,
            target,
            projectId,
            container,
            expectedRevision,
            [new InsertManuscriptBlock(blockIndex, ManuscriptBlockType.DesignedPage, string.Empty,
                ManuscriptStyleRoles.DesignedPage, DesignedPageId: pageId, BlockId: actualPlacementId)],
            cancellationToken);
        var historyStage = await StageContainerHistoryAsync(
            db, projectId, revision, "Place Designed Page", cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (historyStage is not null)
                deltaHistory.Discard(historyStage.StageId);
            throw;
        }
        ConfirmContainerHistory(historyStage);
        if (container.Kind == DesignedPageContainerKind.Chapter)
            await manuscripts.RefreshDerivedStateAsync(target, container.Id, cancellationToken);
        return new DesignedPagePlacementMutationResult(view, actualPlacementId, [revision]);
    }

    public async Task<DesignedPagePlacementMutationResult> RemovePlacementAsync(
        EditorContentTarget target,
        Guid projectId,
        string placementId,
        DesignedPageContainer container,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenWriteAsync(projectId, cancellationToken);
        operation.ShareWithNestedOperations();
        var db = operation.Db;
        await EnsureEditableTargetAsync(db, projectId, target, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var reference = await db.DesignedPagePlacementReferences
            .SingleOrDefaultAsync(item => item.Id == placementId
                && item.ProjectId == projectId
                && item.ContainerKind == container.Kind
                && item.ContainerId == container.Id
                && item.EditionId == target.EditionId, cancellationToken)
            ?? throw new KeyNotFoundException("The Designed Page placement was not found.");
        EnsurePlacementContainer(reference, target, container);
        var revision = await ApplyContainerOperationsAsync(db, target, projectId, container, expectedRevision,
            [new DeleteManuscriptBlock(placementId)], cancellationToken);
        var view = await RequireEffectiveViewAsync(db, projectId, reference.DesignedPageId, target, cancellationToken);
        var historyStage = await StageContainerHistoryAsync(
            db, projectId, revision, "Remove Designed Page placement", cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (historyStage is not null)
                deltaHistory.Discard(historyStage.StageId);
            throw;
        }
        ConfirmContainerHistory(historyStage);
        if (container.Kind == DesignedPageContainerKind.Chapter)
            await manuscripts.RefreshDerivedStateAsync(target, container.Id, cancellationToken);
        return new DesignedPagePlacementMutationResult(view, placementId, [revision]);
    }

    public async Task<DesignedPagePlacementMutationResult> MovePlacementAsync(
        EditorContentTarget target,
        Guid projectId,
        string placementId,
        DesignedPageContainer source,
        long expectedSourceRevision,
        DesignedPageContainer destination,
        int destinationIndex,
        long expectedDestinationRevision,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenWriteAsync(projectId, cancellationToken);
        operation.ShareWithNestedOperations();
        var db = operation.Db;
        await EnsureEditableTargetAsync(db, projectId, target, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var reference = await db.DesignedPagePlacementReferences
            .SingleOrDefaultAsync(item => item.Id == placementId
                && item.ProjectId == projectId
                && item.ContainerKind == source.Kind
                && item.ContainerId == source.Id
                && item.EditionId == target.EditionId, cancellationToken)
            ?? throw new KeyNotFoundException("The Designed Page placement was not found.");
        EnsurePlacementContainer(reference, target, source);
        var movedPageId = reference.DesignedPageId;
        var revisions = new List<DesignedPageContainerRevision>();
        AuthoringDeltaHistoryStage? compoundHistory = null;
        if (source == destination)
        {
            revisions.Add(await ApplyContainerOperationsAsync(db, target, projectId, source, expectedSourceRevision,
                [new MoveManuscriptBlock(placementId, destinationIndex)], cancellationToken));
            compoundHistory = await StageContainerHistoryAsync(
                db,
                projectId,
                revisions.Single(),
                "Move Designed Page placement",
                cancellationToken);
        }
        else
        {
            var sourceTargetId = ContainerTargetId(target, source);
            var destinationTargetId = ContainerTargetId(target, destination);
            var sourceDocument = await ReadContainerDocumentAsync(db, target, projectId, source, cancellationToken);
            var movedBlockIndex = sourceDocument.Content.FindIndex(item => item.Id == placementId);
            if (movedBlockIndex < 0)
                throw new KeyNotFoundException("The Designed Page placement block was not found in its container.");
            var movedBlock = sourceDocument.Content[movedBlockIndex];
            using (authoringMutationContext.SuppressHistory())
            {
                revisions.Add(await ApplyContainerOperationsAsync(db, target, projectId, source, expectedSourceRevision,
                    [new DeleteManuscriptBlock(placementId)], cancellationToken));
                revisions.Add(await ApplyContainerOperationsAsync(db, target, projectId, destination, expectedDestinationRevision,
                    [new InsertManuscriptBlock(destinationIndex, ManuscriptBlockType.DesignedPage, string.Empty,
                        ManuscriptStyleRoles.DesignedPage, DesignedPageId: movedPageId, BlockId: placementId)], cancellationToken));
            }
            var targetIds = new[] { sourceTargetId, destinationTargetId };
            var generations = await AuthoringPersistence.ReadGenerationsAsync(
                db,
                projectId,
                targetIds,
                cancellationToken);
            compoundHistory = deltaHistory.Stage(
                projectId,
                targetIds,
                generations,
                "Move Designed Page placement",
                [
                    new(0, "deleteBlock", BlockId: placementId,
                        ExpectedElementFingerprint: AuthoringBatchReducer.Fingerprint(movedBlock)),
                    new(1, "insertDesignedPagePlacement", PlacementBlockId: placementId, Index: destinationIndex,
                        PageId: movedPageId),
                ],
                [
                    new(0, "restoreBlock", BlockId: placementId, Index: movedBlockIndex,
                        CanonicalBlock: movedBlock),
                    new(1, "deleteBlock", BlockId: placementId),
                ],
                null,
                null);
        }
        var view = await RequireEffectiveViewAsync(db, projectId, movedPageId, target, cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (compoundHistory is not null)
                deltaHistory.Discard(compoundHistory.StageId);
            throw;
        }
        ConfirmContainerHistory(compoundHistory);
        if (source.Kind == DesignedPageContainerKind.Chapter)
            await manuscripts.RefreshDerivedStateAsync(target, source.Id, cancellationToken);
        if (destination.Kind == DesignedPageContainerKind.Chapter && destination != source)
            await manuscripts.RefreshDerivedStateAsync(target, destination.Id, cancellationToken);
        return new DesignedPagePlacementMutationResult(view, placementId, revisions);
    }

    private static string ContainerTargetId(
        EditorContentTarget target,
        DesignedPageContainer container) => container.Kind switch
        {
            DesignedPageContainerKind.Chapter when target.EditionId is Guid editionId =>
                $"release:{editionId:D}:chapter:{container.Id:D}",
            DesignedPageContainerKind.Chapter => $"chapter:{container.Id:D}",
            DesignedPageContainerKind.PublicationSection when target.EditionId is Guid editionId =>
                $"release:{editionId:D}:section:{container.Id:D}",
            DesignedPageContainerKind.PublicationSection => $"publication-section:{container.Id:D}",
            _ => throw new ArgumentOutOfRangeException(nameof(container)),
        };

    private async Task<ManuscriptDocument> ReadContainerDocumentAsync(
        AppDbContext db,
        EditorContentTarget target,
        Guid projectId,
        DesignedPageContainer container,
        CancellationToken cancellationToken)
    {
        if (container.Kind == DesignedPageContainerKind.Chapter)
        {
            return (await manuscripts.GetManuscriptAsync(target, container.Id, cancellationToken)
                ?? throw new KeyNotFoundException("The Designed Page chapter container was not found.")).Document;
        }
        var section = await db.PublicationSections.AsNoTracking().SingleOrDefaultAsync(
            item => item.ProjectId == projectId
                && item.Id == container.Id
                && item.EditionId == target.EditionId,
            cancellationToken) ?? throw new KeyNotFoundException("The Designed Page publication-section container was not found.");
        return ManuscriptCodec.Deserialize(section.ManuscriptJson, section.Id, section.Revision);
    }

    private async Task<DesignedPageContainerRevision> ApplyContainerOperationsAsync(
        AppDbContext db,
        EditorContentTarget target,
        Guid projectId,
        DesignedPageContainer container,
        long expectedRevision,
        IReadOnlyList<ManuscriptOperation> operations,
        CancellationToken cancellationToken,
        bool replaceContainerContent = false)
    {
        if (container.Id == Guid.Empty)
            throw new ArgumentException("A Designed Page container ID is required.", nameof(container));
        if (container.Kind == DesignedPageContainerKind.Chapter)
        {
            var chapterBeforeDocument = await ReadContainerDocumentAsync(
                db, target, projectId, container, cancellationToken);
            ManuscriptMutationResult mutation;
            using (authoringMutationContext.SuppressHistory())
            {
                mutation = await manuscripts.ApplyPersistedUnderProjectMutationLeaseAsync(
                    target, container.Id, expectedRevision, operations, cancellationToken);
            }
            return new DesignedPageContainerRevision(
                container,
                mutation.Snapshot.Revision,
                mutation.Snapshot.Document,
                chapterBeforeDocument,
                ContainerTargetId(target, container));
        }
        var section = await db.PublicationSections.SingleOrDefaultAsync(
            item => item.ProjectId == projectId && item.Id == container.Id,
            cancellationToken) ?? throw new KeyNotFoundException("The publication section was not found in this project.");
        if (section.EditionId != target.EditionId)
            throw new InvalidOperationException("The publication section does not belong to the selected content target.");
        if (section.Revision != expectedRevision)
            throw new ManuscriptRevisionConflictException(expectedRevision, section.Revision);
        var document = ManuscriptCodec.Deserialize(section.ManuscriptJson, section.Id, section.Revision);
        var beforeDocument = document;
        if (replaceContainerContent)
            document = document with { Content = [] };
        var applied = ManuscriptOperations.Apply(document, operations);
        section.ManuscriptJson = ManuscriptCodec.Serialize(applied.Document);
        section.Revision = applied.Document.Revision;
        section.UpdatedAt = DateTime.UtcNow;
        await SyncPublicationSectionPlacementsAsync(db, section, applied.Document, cancellationToken);
        return new DesignedPageContainerRevision(
            container,
            section.Revision,
            applied.Document,
            beforeDocument,
            ContainerTargetId(target, container));
    }

    private async Task<AuthoringDeltaHistoryStage?> StageContainerHistoryAsync(
        AppDbContext db,
        Guid projectId,
        DesignedPageContainerRevision revision,
        string actionLabel,
        CancellationToken cancellationToken)
    {
        if (authoringMutationContext.IsHistorySuppressed)
            return null;
        var delta = AuthoringBatchReducer.CreateCanonicalDelta(revision.BeforeDocument, revision.Document);
        if (delta.Forward.Count == 0)
            return null;
        var generations = await AuthoringPersistence.ReadGenerationsAsync(
            db, projectId, [revision.TargetId], cancellationToken);
        return deltaHistory.Stage(
            projectId,
            [revision.TargetId],
            generations,
            actionLabel,
            delta.Forward,
            delta.Inverse,
            null,
            null);
    }

    private void ConfirmContainerHistory(AuthoringDeltaHistoryStage? stage)
    {
        if (stage is not null)
            _ = deltaHistory.Confirm(stage.StageId);
    }

    private static async Task SyncPublicationSectionPlacementsAsync(
        AppDbContext db,
        PublicationSection section,
        ManuscriptDocument document,
        CancellationToken cancellationToken)
    {
        var desired = document.Content
            .Where(block => block.Type == ManuscriptBlockType.DesignedPage)
            .ToList();
        if (desired.GroupBy(block => block.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new InvalidDataException("Designed Page placement IDs must be unique within the publication section.");
        var desiredIds = desired.Select(block => block.Id).ToHashSet(StringComparer.Ordinal);
        var stored = await db.DesignedPagePlacementReferences
            .Where(item => item.ProjectId == section.ProjectId
                && item.ContainerKind == DesignedPageContainerKind.PublicationSection
                && item.ContainerId == section.Id
                && item.EditionId == section.EditionId)
            .ToListAsync(cancellationToken);
        var current = stored.Concat(db.DesignedPagePlacementReferences.Local.Where(item =>
                item.ProjectId == section.ProjectId
                && item.ContainerKind == DesignedPageContainerKind.PublicationSection
                && item.ContainerId == section.Id
                && item.EditionId == section.EditionId
                && db.Entry(item).State != EntityState.Deleted))
            .DistinctBy(item => item.ReferenceId)
            .ToList();
        foreach (var obsolete in current.Where(item => !desiredIds.Contains(item.Id)))
            db.DesignedPagePlacementReferences.Remove(obsolete);
        var now = DateTime.UtcNow;
        foreach (var block in desired)
        {
            var reference = current.SingleOrDefault(item => item.Id == block.Id);
            if (reference is null)
            {
                reference = new DesignedPagePlacementReference
                {
                    Id = block.Id,
                    ProjectId = section.ProjectId,
                    ContainerKind = DesignedPageContainerKind.PublicationSection,
                    ContainerId = section.Id,
                    EditionId = section.EditionId,
                    CreatedAt = now,
                };
                db.DesignedPagePlacementReferences.Add(reference);
                current.Add(reference);
            }
            reference.DesignedPageId = block.DesignedPageId!.Value;
            reference.ManuscriptRevision = document.Revision;
            reference.UpdatedAt = now;
        }
    }

    private static void EnsurePlacementContainer(
        DesignedPagePlacementReference reference,
        EditorContentTarget target,
        DesignedPageContainer container)
    {
        if (reference.ContainerKind != container.Kind || reference.ContainerId != container.Id
            || reference.EditionId != target.EditionId)
        {
            throw new InvalidOperationException("The Designed Page placement does not belong to the selected manuscript container.");
        }
    }

    private static DesignedPageContent CloneContentForPage(
        DesignedPageContent source,
        DesignedPage page,
        Guid? editionId,
        Guid contentId)
    {
        var semantic = ManuscriptCodec.Deserialize(source.SemanticManuscriptJson) with { ManuscriptId = contentId };
        var content = new DesignedPageContent
        {
            Id = contentId,
            ProjectId = page.ProjectId,
            DesignedPageId = page.Id,
            Page = page,
            EditionId = editionId,
            SemanticManuscriptJson = ManuscriptCodec.Serialize(semantic),
            AccessibilityDescription = source.AccessibilityDescription,
            Revision = source.Revision,
        };
        var ids = new Dictionary<Guid, Guid>();
        foreach (var sourceVariant in source.Variants)
        {
            var variant = new DesignedPageVariant
            {
                Content = content,
                ContentId = content.Id,
                GeometryKey = sourceVariant.GeometryKey,
                SceneJson = sourceVariant.SceneJson,
                Revision = sourceVariant.Revision,
            };
            ids[sourceVariant.Id] = variant.Id;
            content.Variants.Add(variant);
        }
        if (source.ActiveVariantId is Guid activeId && ids.TryGetValue(activeId, out var clonedId))
            content.ActiveVariantId = clonedId;
        return content;
    }

    private static DesignedPageView SelectEffective(DesignedPage page, EditorContentTarget target)
    {
        if (page.ScopeEditionId is Guid scopeEditionId && scopeEditionId != target.EditionId)
            throw new InvalidOperationException("This release-only Designed Page is not available to the selected content target.");
        var exact = page.Contents.SingleOrDefault(item => item.EditionId == target.EditionId);
        if (exact is not null)
            return new DesignedPageView(page, exact, false);
        var core = page.Contents.SingleOrDefault(item => item.EditionId == null)
            ?? throw new InvalidDataException("The Designed Page has no content for the selected target.");
        return new DesignedPageView(page, core, target.EditionId is not null);
    }

    private static async Task<DesignedPageView> RequireEffectiveViewAsync(
        AppDbContext db,
        Guid projectId,
        Guid pageId,
        EditorContentTarget target,
        CancellationToken cancellationToken)
    {
        var page = await db.DesignedPages.AsTracking().Include(item => item.Contents)
            .SingleOrDefaultAsync(item => item.ProjectId == projectId && item.Id == pageId, cancellationToken)
            ?? throw new KeyNotFoundException("The Designed Page was not found in this project.");
        return SelectEffective(page, target);
    }

    private static void EnsureTarget(EditorContentTarget target, DesignedPageContent composition)
    {
        if (target.EditionId != composition.EditionId)
            throw new InvalidOperationException("The composition does not belong to the selected Editor content target.");
    }

    private static async Task EnsureEditableTargetAsync(
        AppDbContext db,
        Guid projectId,
        EditorContentTarget target,
        CancellationToken cancellationToken)
    {
        if (target.EditionId is not Guid editionId)
            return;
        var status = await db.PublicationEditions.AsNoTracking()
            .Where(item => item.ProjectId == projectId && item.Id == editionId)
            .Select(item => (PublicationEditionStatus?)item.Status)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException("The publication release was not found in this project.");
        if (status == PublicationEditionStatus.Archived)
            throw new InvalidOperationException("Archived publication releases are read-only. Clone this release to make changes.");
    }

    private async Task RefreshPlacedChaptersAsync(
        EditorContentTarget target,
        Guid projectId,
        Guid pageId,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        var db = operation.Db;
        var references = await db.DesignedPagePlacementReferences.AsNoTracking()
            .Where(item => item.ProjectId == projectId
                && item.DesignedPageId == pageId
                && item.ContainerKind == DesignedPageContainerKind.Chapter)
            .ToListAsync(cancellationToken);
        var coreChapterIds = references
            .Where(item => item.EditionId == null)
            .Select(item => item.ContainerId)
            .ToHashSet();
        var jobs = new HashSet<(Guid? EditionId, Guid ChapterId)>();
        if (target.EditionId is Guid targetEditionId)
        {
            var overriddenChapters = await db.PublicationEditionChapterOverrides.AsNoTracking()
                .Where(item => item.EditionId == targetEditionId && coreChapterIds.Contains(item.ChapterId))
                .Select(item => item.ChapterId)
                .ToHashSetAsync(cancellationToken);
            foreach (var chapterId in references
                .Where(item => item.EditionId == targetEditionId)
                .Select(item => item.ContainerId)
                .Concat(coreChapterIds.Where(item => !overriddenChapters.Contains(item))))
            {
                jobs.Add((targetEditionId, chapterId));
            }
        }
        else
        {
            foreach (var chapterId in coreChapterIds)
                jobs.Add((null, chapterId));
            var pageOverrideEditionIds = await db.DesignedPageContents.AsNoTracking()
                .Where(item => item.ProjectId == projectId
                    && item.DesignedPageId == pageId
                    && item.EditionId != null)
                .Select(item => item.EditionId!.Value)
                .ToHashSetAsync(cancellationToken);
            var editionIds = await db.PublicationEditions.AsNoTracking()
                .Where(item => item.ProjectId == projectId && !pageOverrideEditionIds.Contains(item.Id))
                .Select(item => item.Id)
                .ToListAsync(cancellationToken);
            var chapterOverrides = await db.PublicationEditionChapterOverrides.AsNoTracking()
                .Where(item => editionIds.Contains(item.EditionId) && coreChapterIds.Contains(item.ChapterId))
                .Select(item => new { item.EditionId, item.ChapterId })
                .ToListAsync(cancellationToken);
            var overrideKeys = chapterOverrides
                .Select(item => (item.EditionId, item.ChapterId))
                .ToHashSet();
            foreach (var editionId in editionIds)
            {
                foreach (var chapterId in references
                    .Where(item => item.EditionId == editionId)
                    .Select(item => item.ContainerId)
                    .Concat(coreChapterIds.Where(chapterId => !overrideKeys.Contains((editionId, chapterId)))))
                {
                    jobs.Add((editionId, chapterId));
                }
            }
        }
        await operation.DisposeAsync();
        foreach (var (editionId, chapterId) in jobs)
        {
            var refreshTarget = editionId is Guid value
                ? EditorContentTarget.ForEdition(value)
                : EditorContentTarget.Core;
            await manuscripts.RefreshDerivedStateAsync(refreshTarget, chapterId, cancellationToken);
        }
    }

    private async Task TouchProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var project = await db.Projects.SingleAsync(item => item.Id == projectId, cancellationToken);
        project.UpdatedAt = DateTime.UtcNow;
    }

}

public sealed class DesignedPageRevisionConflictException(long expectedRevision, long actualRevision)
    : InvalidOperationException($"Designed Page revision conflict: expected {expectedRevision}, current revision is {actualRevision}.")
{
    public long ExpectedRevision { get; } = expectedRevision;
    public long ActualRevision { get; } = actualRevision;
}

public sealed record DesignedPageCreationResult(
    DesignedPage Page,
    DesignedPageContent Content,
    DesignedPageVariant? Variant,
    ManuscriptSnapshot Manuscript,
    string PlacementId);

public sealed record DesignedPageIdentity(Guid PageId, string PlacementId);

public sealed record DesignedPageView(
    DesignedPage Page,
    DesignedPageContent Content,
    bool IsInherited);

public readonly record struct DesignedPageContainer(DesignedPageContainerKind Kind, Guid Id)
{
    public static DesignedPageContainer Chapter(Guid id) => new(DesignedPageContainerKind.Chapter, id);
    public static DesignedPageContainer PublicationSection(Guid id) => new(DesignedPageContainerKind.PublicationSection, id);
}

public sealed record DesignedPageContainerRevision(
    DesignedPageContainer Container,
    long Revision,
    ManuscriptDocument Document,
    ManuscriptDocument BeforeDocument,
    string TargetId);

public sealed record DesignedPagePlacementMutationResult(
    DesignedPageView Page,
    string PlacementId,
    IReadOnlyList<DesignedPageContainerRevision> Containers);

public sealed record DesignedPageWorkspaceSaveResult(
    DesignedPageContent Content,
    DesignedPageVariant Variant,
    bool Applied = true);

public sealed record DesignedPageSemanticMutationResult(
    DesignedPageContent Content,
    IReadOnlyList<string> ChangedBlockIds);

public sealed record DesignedPageWorkspaceMutationResult(
    DesignedPageContent Content,
    DesignedPageVariant Variant,
    IReadOnlyList<string> ChangedBlockIds);

public sealed record CompositionImagePlacementResult(
    DesignedPageVariant Variant,
    Guid ObjectId);

public sealed record CompositionSceneImageMutation(
    CompositionScene Scene,
    Guid ObjectId);

internal sealed record CompositionWorkspaceStagePayload(
    Guid VariantId,
    long ExpectedVariantRevision,
    IReadOnlyList<ManuscriptOperationInput> SemanticOperations,
    CompositionScene Scene);

public sealed record CompositionElementPatch(
    string? Name = null,
    Guid? LayerId = null,
    CompositionBounds? Bounds = null,
    double? RotationDegrees = null,
    double? Opacity = null,
    int? Order = null,
    bool? Visible = null,
    bool? Locked = null,
    Guid? StyleId = null,
    bool ClearStyle = false,
    Guid? ImageId = null,
    FigureImageFit? ImageFit = null,
    double? CropXPercent = null,
    double? CropYPercent = null,
    IReadOnlyList<ManuscriptRangeReference>? ContentReferences = null,
    string? FontFamilyKey = null,
    int? FontWeight = null,
    bool? Italic = null,
    double? FontSizePoints = null,
    double? LineHeight = null,
    double? LetterSpacingEm = null,
    string? FillColor = null,
    string? BackgroundColor = null,
    double? BackgroundOpacity = null,
    string? StrokeColor = null,
    double? StrokeWidthPoints = null,
    CompositionTextAlignment? TextAlignment = null,
    CompositionVerticalAlignment? VerticalAlignment = null,
    CompositionTextShadow? TextShadow = null,
    string? AltText = null,
    bool? Decorative = null,
    bool? AccessibilityDecisionPending = null,
    string? Language = null,
    CompositionSemanticRole? SemanticRole = null,
    int? ReadingOrder = null,
    bool ClearReadingOrder = false,
    CompositionRegionConstraint? RegionConstraint = null,
    Guid? GroupId = null,
    bool ClearGroup = false,
    CompositionGuideAxis? GuideAxis = null,
    double? PositionPercent = null);
