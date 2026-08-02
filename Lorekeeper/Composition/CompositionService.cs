using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Publish;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace Lorekeeper.Composition;

public interface ICompositionService
{
    Task<DesignedPageCreationResult> CreateDesignedPageAsync(Guid projectId, Guid chapterId, int blockIndex, string name, Guid? editionId, long expectedRevision, CancellationToken cancellationToken = default);
    Task<DesignedPageCreationResult> CreateDesignedPageAsync(Guid projectId, Guid chapterId, int blockIndex, string name, Guid? editionId, long expectedRevision, DesignedPageIdentity identity, CancellationToken cancellationToken = default);
    Task<PageComposition?> GetAsync(Guid projectId, Guid compositionId, CancellationToken cancellationToken = default);
    Task<PageCompositionVariant> GetOrCreateVariantAsync(Guid projectId, Guid compositionId, Guid editionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PageCompositionVariant>> ListVariantsAsync(Guid projectId, Guid compositionId, Guid editionId, CancellationToken cancellationToken = default);
    Task<PageCompositionVariant> ReadVariantAsync(Guid projectId, Guid variantId, CancellationToken cancellationToken = default);
    Task<PageCompositionVariant> SelectVariantAsync(Guid projectId, Guid compositionId, Guid editionId, Guid variantId, CancellationToken cancellationToken = default);
    Task<PageCompositionVariant> SaveVariantAsync(Guid projectId, Guid variantId, long expectedRevision, CompositionScene scene, CancellationToken cancellationToken = default);
    Task<PageCompositionVariant> PatchElementAsync(Guid projectId, Guid variantId, long expectedRevision, string targetKind, Guid targetId, CompositionElementPatch patch, CancellationToken cancellationToken = default);
    Task<CompositionWorkspaceSaveResult> SaveWorkspaceAsync(Guid projectId, Guid compositionId, long expectedCompositionRevision, IReadOnlyList<ManuscriptBlock> semanticBlocks, Guid variantId, long expectedVariantRevision, CompositionScene scene, CancellationToken cancellationToken = default);
    Task<CompositionMutationStage> StageVariantAsync(Guid projectId, Guid conversationId, Guid variantId, long expectedRevision, CompositionScene scene, CancellationToken cancellationToken = default);
    Task<PageCompositionVariant> ApplyStageAsync(Guid projectId, Guid conversationId, Guid stageId, long expectedRevision, CancellationToken cancellationToken = default);
    Task<CompositionMutationStage> StageSemanticOperationsAsync(Guid projectId, Guid conversationId, Guid compositionId, long expectedRevision, IReadOnlyList<ManuscriptOperationInput> operations, CancellationToken cancellationToken = default);
    Task<CompositionSemanticMutationResult> ApplySemanticStageAsync(Guid projectId, Guid conversationId, Guid stageId, long expectedRevision, CancellationToken cancellationToken = default);
    Task<CompositionMutationStage> StageWorkspaceAsync(Guid projectId, Guid conversationId, Guid compositionId, long expectedCompositionRevision, Guid variantId, long expectedVariantRevision, IReadOnlyList<ManuscriptOperationInput> semanticOperations, CompositionScene scene, CancellationToken cancellationToken = default);
    Task<CompositionWorkspaceMutationResult> ApplyWorkspaceStageAsync(Guid projectId, Guid conversationId, Guid stageId, long expectedCompositionRevision, CancellationToken cancellationToken = default);
    Task<CompositionEditionGeometry> GetEditionGeometryAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken = default);
    Task<LayoutGenerationTargetDescriptor> DescribeGenerationTargetAsync(Guid projectId, Guid editionId, string targetKind, Guid targetId, Guid? variantId = null, CancellationToken cancellationToken = default);
    Task<LayoutValidationView> ValidateVariantAsync(Guid projectId, Guid editionId, Guid variantId, CancellationToken cancellationToken = default);
}

public sealed class CompositionService(
    AppDbContext db,
    IManuscriptService manuscripts,
    IPublicationCoverService covers,
    IProjectMutationCoordinator projectMutations) : ICompositionService
{
    private static readonly JsonSerializerOptions JsonOptions = ManuscriptCodec.JsonOptions;

    public async Task<DesignedPageCreationResult> CreateDesignedPageAsync(
        Guid projectId,
        Guid chapterId,
        int blockIndex,
        string name,
        Guid? editionId,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        await CreateDesignedPageCoreAsync(
            projectId,
            chapterId,
            blockIndex,
            name,
            editionId,
            expectedRevision,
            identity: null,
            cancellationToken);

    public async Task<DesignedPageCreationResult> CreateDesignedPageAsync(
        Guid projectId,
        Guid chapterId,
        int blockIndex,
        string name,
        Guid? editionId,
        long expectedRevision,
        DesignedPageIdentity identity,
        CancellationToken cancellationToken = default) =>
        await CreateDesignedPageCoreAsync(
            projectId,
            chapterId,
            blockIndex,
            name,
            editionId,
            expectedRevision,
            identity,
            cancellationToken);

    private async Task<DesignedPageCreationResult> CreateDesignedPageCoreAsync(
        Guid projectId,
        Guid chapterId,
        int blockIndex,
        string name,
        Guid? editionId,
        long expectedRevision,
        DesignedPageIdentity? identity,
        CancellationToken cancellationToken)
    {
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var chapter = await db.Chapters.AsNoTracking().SingleOrDefaultAsync(item => item.Id == chapterId && item.ProjectId == projectId, cancellationToken)
            ?? throw new KeyNotFoundException("Chapter was not found in this project.");
        if (chapter.ManuscriptRevision != expectedRevision)
            throw new ManuscriptRevisionConflictException(expectedRevision, chapter.ManuscriptRevision);
        var composition = new PageComposition
        {
            Id = identity?.CompositionId ?? Guid.NewGuid(),
            ProjectId = projectId,
            ChapterId = chapterId,
            Name = string.IsNullOrWhiteSpace(name) ? "Designed page" : name.Trim(),
        };
        composition.SemanticManuscriptJson = ManuscriptCodec.Serialize(ManuscriptCodec.CreateEmpty(composition.Id));
        db.PageCompositions.Add(composition);
        PageCompositionVariant? variant = null;
        if (editionId is Guid selectedEditionId)
        {
            var edition = await db.PublicationEditions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == selectedEditionId && item.ProjectId == projectId, cancellationToken)
                ?? throw new KeyNotFoundException("Edition was not found in this project.");
            variant = new PageCompositionVariant
            {
                Composition = composition,
                CompositionId = composition.Id,
                GeometryKey = GeometryKey(edition),
                SceneJson = JsonSerializer.Serialize(CreatePageScene(edition), JsonOptions),
            };
            db.PageCompositionVariants.Add(variant);
        }
        await db.SaveChangesAsync(cancellationToken);
        var manuscript = await manuscripts.ApplyPersistedUnderProjectMutationLeaseAsync(
            chapterId,
            expectedRevision,
            [new InsertManuscriptBlock(
                blockIndex,
                ManuscriptBlockType.DesignedPage,
                string.Empty,
                ManuscriptStyleRoles.DesignedPage,
                PageCompositionId: composition.Id,
                BlockId: identity?.BlockId)],
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await manuscripts.RefreshDerivedStateAsync(chapterId, cancellationToken);
        return new DesignedPageCreationResult(composition, variant, manuscript.Snapshot, manuscript.ChangedBlockIds.Single());
    }

    public async Task<PageComposition?> GetAsync(
        Guid projectId,
        Guid compositionId,
        CancellationToken cancellationToken = default)
    {
        return await db.PageCompositions.AsNoTracking()
            .Include(item => item.Variants.OrderBy(variant => variant.GeometryKey))
            .SingleOrDefaultAsync(item => item.Id == compositionId && item.ProjectId == projectId, cancellationToken);
    }

    public async Task<PageCompositionVariant> GetOrCreateVariantAsync(
        Guid projectId,
        Guid compositionId,
        Guid editionId,
        CancellationToken cancellationToken = default)
    {
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var composition = await db.PageCompositions.SingleOrDefaultAsync(
            item => item.Id == compositionId && item.ProjectId == projectId,
            cancellationToken) ?? throw new KeyNotFoundException("Page composition was not found in this project.");
        var edition = await db.PublicationEditions.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == editionId && item.ProjectId == projectId,
            cancellationToken) ?? throw new KeyNotFoundException("Edition was not found in this project.");
        var candidates = await db.PageCompositionVariants
            .Where(item => item.CompositionId == composition.Id)
            .OrderByDescending(item => item.UpdatedAt)
            .ToListAsync(cancellationToken);
        var existing = candidates.FirstOrDefault(item => VariantMatchesEdition(item, edition));
        if (existing is not null)
            return existing;

        var seed = await db.CompositionMutationStages.SingleOrDefaultAsync(item =>
            item.ProjectId == projectId
            && item.ConversationId == Guid.Empty
            && item.TargetKind == "page-composition-seed"
            && item.TargetId == composition.Id,
            cancellationToken);
        var scene = seed is null
            ? CreatePageScene(edition)
            : AdaptSeedScene(seed.OperationsJson, edition);
        var variant = new PageCompositionVariant
        {
            CompositionId = composition.Id,
            GeometryKey = GeometryKey(edition, scene),
            SceneJson = SerializeAndValidate(scene, composition.SemanticManuscriptJson),
        };
        db.PageCompositionVariants.Add(variant);
        if (seed is not null)
            db.CompositionMutationStages.Remove(seed);
        await TouchProjectAsync(projectId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return variant;
    }

    public async Task<IReadOnlyList<PageCompositionVariant>> ListVariantsAsync(
        Guid projectId,
        Guid compositionId,
        Guid editionId,
        CancellationToken cancellationToken = default)
    {
        var edition = await db.PublicationEditions.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == editionId && item.ProjectId == projectId, cancellationToken)
            ?? throw new KeyNotFoundException("Edition was not found in this project.");
        return (await db.PageCompositionVariants.AsNoTracking()
            .Where(item => item.CompositionId == compositionId && item.Composition.ProjectId == projectId)
            .OrderByDescending(item => item.UpdatedAt)
            .ToListAsync(cancellationToken))
            .Where(item => VariantMatchesEdition(item, edition))
            .ToList();
    }

    public async Task<PageCompositionVariant> ReadVariantAsync(
        Guid projectId,
        Guid variantId,
        CancellationToken cancellationToken = default) =>
        await db.PageCompositionVariants.AsNoTracking().Include(item => item.Composition)
            .SingleOrDefaultAsync(item => item.Id == variantId && item.Composition.ProjectId == projectId, cancellationToken)
        ?? throw new KeyNotFoundException("Composition variant was not found in this project.");

    public async Task<PageCompositionVariant> SelectVariantAsync(
        Guid projectId,
        Guid compositionId,
        Guid editionId,
        Guid variantId,
        CancellationToken cancellationToken = default)
    {
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var edition = await db.PublicationEditions.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == editionId && item.ProjectId == projectId, cancellationToken)
            ?? throw new KeyNotFoundException("Edition was not found in this project.");
        var variant = await db.PageCompositionVariants.Include(item => item.Composition)
            .SingleOrDefaultAsync(item => item.Id == variantId && item.CompositionId == compositionId
                && item.Composition.ProjectId == projectId, cancellationToken)
            ?? throw new KeyNotFoundException("Composition variant was not found in this project.");
        if (!VariantMatchesEdition(variant, edition))
            throw new InvalidDataException("The selected variant is incompatible with this edition.");
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

        var facing = scene.Surface.Kind == CompositionSurfaceKind.FacingSpread;
        var targetWidth = edition.PageWidthInches * 72 * (facing ? 2 : 1);
        var targetHeight = edition.PageHeightInches * 72;
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
                BleedPoints = edition.Bleed ? 9 : 0,
                SafeInsetPoints = edition.PageMarginInches * 72,
                AllowIndependentPdfPage = false,
            },
            // Group children use coordinates local to their group. Mapping the group moves the
            // complete subtree; mapping its children as well would apply the transform twice.
            Objects = scene.Objects.Select(item => item.GroupId is null
                ? item with { Bounds = Map(item.Bounds) }
                : item).ToList(),
        };
    }

    public async Task<PageCompositionVariant> SaveVariantAsync(
        Guid projectId,
        Guid variantId,
        long expectedRevision,
        CompositionScene scene,
        CancellationToken cancellationToken = default)
    {
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var variant = await db.PageCompositionVariants
            .Include(item => item.Composition)
            .SingleOrDefaultAsync(item => item.Id == variantId && item.Composition.ProjectId == projectId, cancellationToken)
            ?? throw new KeyNotFoundException("Composition variant was not found in this project.");
        if (variant.Revision != expectedRevision)
            throw new CompositionRevisionConflictException(expectedRevision, variant.Revision);
        await ValidateVariantGeometryAsync(projectId, variant.GeometryKey, scene, cancellationToken);
        await ValidateSceneAssetsAsync(projectId, scene, cancellationToken);
        var geometryKey = await ExactGeometryKeyAsync(projectId, variant.GeometryKey, scene, cancellationToken);
        if (!string.Equals(geometryKey, variant.GeometryKey, StringComparison.Ordinal))
        {
            if (await db.PageCompositionVariants.AnyAsync(item => item.CompositionId == variant.CompositionId && item.GeometryKey == geometryKey, cancellationToken))
                throw new InvalidOperationException("This exact layout geometry already exists. Select that variant instead of overwriting it.");
            variant = new PageCompositionVariant
            {
                CompositionId = variant.CompositionId,
                Composition = variant.Composition,
                GeometryKey = geometryKey,
                SceneJson = SerializeAndValidate(scene, variant.Composition.SemanticManuscriptJson),
            };
            db.PageCompositionVariants.Add(variant);
        }
        else
        {
            variant.SceneJson = SerializeAndValidate(scene, variant.Composition.SemanticManuscriptJson);
            variant.Revision = checked(variant.Revision + 1);
            variant.UpdatedAt = DateTime.UtcNow;
        }
        await TouchProjectAsync(projectId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return variant;
    }

    public async Task<PageCompositionVariant> PatchElementAsync(
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
            throw new CompositionRevisionConflictException(expectedRevision, variant.Revision);
        var scene = JsonSerializer.Deserialize<CompositionScene>(variant.SceneJson, JsonOptions)
            ?? throw new InvalidDataException("The composition scene is empty.");
        scene = ApplyElementPatch(scene, targetKind, targetId, patch);
        return await SaveVariantAsync(projectId, variantId, expectedRevision, scene, cancellationToken);
    }

    internal static CompositionScene ApplyElementPatch(
        CompositionScene scene,
        string targetKind,
        Guid targetId,
        CompositionElementPatch patch) => targetKind.Trim().ToLowerInvariant() switch
        {
            "object" => scene with { Objects = PatchObject(scene.Objects, targetId, patch) },
            "guide" => scene with { Guides = PatchGuide(scene.Guides, targetId, patch) },
            "layer" => scene with { Layers = PatchLayer(scene.Layers, targetId, patch) },
            "style" => scene with { Styles = PatchStyle(scene.Styles, targetId, patch) },
            _ => throw new ArgumentException("targetKind must be object, guide, layer, or style.", nameof(targetKind)),
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
            FocalXPercent = patch.FocalXPercent ?? item.FocalXPercent,
            FocalYPercent = patch.FocalYPercent ?? item.FocalYPercent,
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

    private static IReadOnlyList<CompositionGuide> PatchGuide(
        IReadOnlyList<CompositionGuide> source,
        Guid id,
        CompositionElementPatch patch)
    {
        if (!source.Any(item => item.Id == id))
            throw new KeyNotFoundException("Composition guide was not found.");
        return source.Select(item => item.Id != id ? item : item with
        {
            Axis = patch.GuideAxis ?? item.Axis,
            PositionPercent = patch.PositionPercent ?? item.PositionPercent,
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

    public async Task<CompositionWorkspaceSaveResult> SaveWorkspaceAsync(
        Guid projectId,
        Guid compositionId,
        long expectedCompositionRevision,
        IReadOnlyList<ManuscriptBlock> semanticBlocks,
        Guid variantId,
        long expectedVariantRevision,
        CompositionScene scene,
        CancellationToken cancellationToken = default)
    {
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var variant = await db.PageCompositionVariants.Include(item => item.Composition)
            .SingleOrDefaultAsync(item => item.Id == variantId
                && item.CompositionId == compositionId
                && item.Composition.ProjectId == projectId, cancellationToken)
            ?? throw new KeyNotFoundException("Composition workspace was not found in this project.");
        var composition = variant.Composition;
        if (composition.Revision != expectedCompositionRevision)
            throw new CompositionRevisionConflictException(expectedCompositionRevision, composition.Revision);
        if (variant.Revision != expectedVariantRevision)
            throw new CompositionRevisionConflictException(expectedVariantRevision, variant.Revision);
        var semantic = new ManuscriptDocument
        {
            ManuscriptId = composition.Id,
            Revision = checked(composition.Revision + 1),
            Content = semanticBlocks.ToList(),
        };
        ValidateSemanticFragment(semantic);
        var semanticJson = ManuscriptCodec.Serialize(semantic);
        await ValidateVariantGeometryAsync(projectId, variant.GeometryKey, scene, cancellationToken);
        await ValidateSceneAssetsAsync(projectId, scene, cancellationToken);
        var sceneJson = SerializeAndValidate(scene, semanticJson);
        composition.SemanticManuscriptJson = semanticJson;
        composition.Revision = semantic.Revision;
        composition.UpdatedAt = DateTime.UtcNow;
        foreach (var other in await db.PageCompositionVariants.AsNoTracking()
            .Where(item => item.CompositionId == composition.Id && item.Id != variant.Id)
            .ToListAsync(cancellationToken))
        {
            var otherScene = JsonSerializer.Deserialize<CompositionScene>(other.SceneJson, JsonOptions)
                ?? throw new InvalidDataException("A composition variant scene is empty.");
            _ = SerializeAndValidate(otherScene, semanticJson);
        }
        var geometryKey = await ExactGeometryKeyAsync(projectId, variant.GeometryKey, scene, cancellationToken);
        if (!string.Equals(geometryKey, variant.GeometryKey, StringComparison.Ordinal))
        {
            if (await db.PageCompositionVariants.AnyAsync(item => item.CompositionId == composition.Id && item.GeometryKey == geometryKey, cancellationToken))
                throw new InvalidOperationException("This exact layout geometry already exists. Select that variant instead of overwriting it.");
            variant = new PageCompositionVariant
            {
                CompositionId = composition.Id,
                Composition = composition,
                GeometryKey = geometryKey,
                SceneJson = sceneJson,
            };
            db.PageCompositionVariants.Add(variant);
        }
        else
        {
            variant.SceneJson = sceneJson;
            variant.Revision = checked(variant.Revision + 1);
            variant.UpdatedAt = DateTime.UtcNow;
        }
        await TouchProjectAsync(projectId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await manuscripts.RefreshDerivedStateAsync(composition.ChapterId, cancellationToken);
        return new CompositionWorkspaceSaveResult(composition, variant);
    }

    public async Task<CompositionMutationStage> StageVariantAsync(
        Guid projectId,
        Guid conversationId,
        Guid variantId,
        long expectedRevision,
        CompositionScene scene,
        CancellationToken cancellationToken = default)
    {
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        await db.CompositionMutationStages
            .Where(item => item.ProjectId == projectId
                && (item.ExpiresAt <= DateTime.UtcNow || item.AppliedAt != null))
            .ExecuteDeleteAsync(cancellationToken);
        var variant = await db.PageCompositionVariants.AsNoTracking()
            .Include(item => item.Composition)
            .SingleOrDefaultAsync(item => item.Id == variantId && item.Composition.ProjectId == projectId, cancellationToken)
            ?? throw new KeyNotFoundException("Composition variant was not found in this project.");
        if (variant.Revision != expectedRevision)
            throw new CompositionRevisionConflictException(expectedRevision, variant.Revision);
        await ValidateVariantGeometryAsync(projectId, variant.GeometryKey, scene, cancellationToken);
        var payload = SerializeAndValidate(scene, variant.Composition.SemanticManuscriptJson);
        await ValidateSceneAssetsAsync(projectId, scene, cancellationToken);
        var stage = new CompositionMutationStage
        {
            ProjectId = projectId,
            ConversationId = conversationId,
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

    public async Task<PageCompositionVariant> ApplyStageAsync(
        Guid projectId,
        Guid conversationId,
        Guid stageId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var stage = await db.CompositionMutationStages.SingleOrDefaultAsync(
            item => item.Id == stageId
                && item.ProjectId == projectId
                && item.ConversationId == conversationId
                && item.TargetKind == "composition-variant",
            cancellationToken) ?? throw new KeyNotFoundException("Composition stage was not found for this conversation.");
        if (stage.AppliedAt is not null)
            throw new InvalidOperationException("Composition stages are non-replayable.");
        if (stage.ExpiresAt <= DateTime.UtcNow)
            throw new InvalidOperationException("The composition stage has expired; submit it again.");
        if (stage.ExpectedRevision != expectedRevision)
            throw new CompositionRevisionConflictException(expectedRevision, stage.ExpectedRevision);
        var payloadHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stage.OperationsJson)));
        if (!string.Equals(payloadHash, stage.PayloadSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The staged composition payload failed its integrity check.");
        var variant = await db.PageCompositionVariants.Include(item => item.Composition).SingleAsync(
            item => item.Id == stage.TargetId && item.Composition.ProjectId == projectId,
            cancellationToken);
        if (variant.Revision != expectedRevision)
            throw new CompositionRevisionConflictException(expectedRevision, variant.Revision);
        var scene = JsonSerializer.Deserialize<CompositionScene>(stage.OperationsJson, JsonOptions)
            ?? throw new InvalidDataException("The staged composition is empty.");
        await ValidateVariantGeometryAsync(projectId, variant.GeometryKey, scene, cancellationToken);
        await ValidateSceneAssetsAsync(projectId, scene, cancellationToken);
        var geometryKey = await ExactGeometryKeyAsync(projectId, variant.GeometryKey, scene, cancellationToken);
        if (!string.Equals(geometryKey, variant.GeometryKey, StringComparison.Ordinal))
        {
            if (await db.PageCompositionVariants.AnyAsync(item => item.CompositionId == variant.CompositionId && item.GeometryKey == geometryKey, cancellationToken))
                throw new InvalidOperationException("This exact layout geometry already exists. Select that variant instead of overwriting it.");
            variant = new PageCompositionVariant
            {
                CompositionId = variant.CompositionId,
                Composition = variant.Composition,
                GeometryKey = geometryKey,
                SceneJson = SerializeAndValidate(scene, variant.Composition.SemanticManuscriptJson),
            };
            db.PageCompositionVariants.Add(variant);
        }
        else
        {
            variant.SceneJson = SerializeAndValidate(scene, variant.Composition.SemanticManuscriptJson);
            variant.Revision = checked(variant.Revision + 1);
            variant.UpdatedAt = DateTime.UtcNow;
        }
        var chapterId = variant.Composition.ChapterId;
        db.CompositionMutationStages.Remove(stage);
        await TouchProjectAsync(projectId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await manuscripts.RefreshDerivedStateAsync(chapterId, cancellationToken);
        return variant;
    }

    public async Task<CompositionMutationStage> StageSemanticOperationsAsync(
        Guid projectId,
        Guid conversationId,
        Guid compositionId,
        long expectedRevision,
        IReadOnlyList<ManuscriptOperationInput> operations,
        CancellationToken cancellationToken = default)
    {
        if (operations.Count is < 1 or > 200)
            throw new ArgumentException("A semantic composition stage requires 1 to 200 focused manuscript operations.");
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        await db.CompositionMutationStages
            .Where(item => item.ProjectId == projectId && (item.ExpiresAt <= DateTime.UtcNow || item.AppliedAt != null))
            .ExecuteDeleteAsync(cancellationToken);
        var composition = await db.PageCompositions.AsNoTracking().Include(item => item.Variants)
            .SingleOrDefaultAsync(item => item.Id == compositionId && item.ProjectId == projectId, cancellationToken)
            ?? throw new KeyNotFoundException("Page composition was not found in this project.");
        if (composition.Revision != expectedRevision)
            throw new CompositionRevisionConflictException(expectedRevision, composition.Revision);
        var current = ManuscriptCodec.Deserialize(composition.SemanticManuscriptJson, composition.Id, composition.Revision);
        var preview = ManuscriptOperations.Apply(current, ManuscriptOperationInput.ToOperations(operations)).Document;
        ValidateSemanticFragment(preview);
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
            TargetKind = "composition-semantic",
            TargetId = compositionId,
            ExpectedRevision = expectedRevision,
            OperationsJson = payload,
            PayloadSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload))),
            ExpiresAt = DateTime.UtcNow.AddMinutes(30),
        };
        db.CompositionMutationStages.Add(stage);
        await db.SaveChangesAsync(cancellationToken);
        return stage;
    }

    public async Task<CompositionSemanticMutationResult> ApplySemanticStageAsync(
        Guid projectId,
        Guid conversationId,
        Guid stageId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var stage = await db.CompositionMutationStages.SingleOrDefaultAsync(item => item.Id == stageId
            && item.ProjectId == projectId && item.ConversationId == conversationId
            && item.TargetKind == "composition-semantic", cancellationToken)
            ?? throw new KeyNotFoundException("Semantic composition stage was not found for this conversation.");
        if (stage.AppliedAt is not null)
            throw new InvalidOperationException("Composition stages are non-replayable.");
        if (stage.ExpiresAt <= DateTime.UtcNow)
            throw new InvalidOperationException("The composition stage has expired; submit it again.");
        if (stage.ExpectedRevision != expectedRevision)
            throw new CompositionRevisionConflictException(expectedRevision, stage.ExpectedRevision);
        var payloadHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stage.OperationsJson)));
        if (!string.Equals(payloadHash, stage.PayloadSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The staged composition payload failed its integrity check.");
        var composition = await db.PageCompositions.Include(item => item.Variants)
            .SingleAsync(item => item.Id == stage.TargetId && item.ProjectId == projectId, cancellationToken);
        if (composition.Revision != expectedRevision)
            throw new CompositionRevisionConflictException(expectedRevision, composition.Revision);
        var inputs = JsonSerializer.Deserialize<List<ManuscriptOperationInput>>(stage.OperationsJson, JsonOptions)
            ?? throw new InvalidDataException("The staged semantic operations are empty.");
        var current = ManuscriptCodec.Deserialize(composition.SemanticManuscriptJson, composition.Id, composition.Revision);
        var applied = ManuscriptOperations.Apply(current, ManuscriptOperationInput.ToOperations(inputs));
        ValidateSemanticFragment(applied.Document);
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
        var chapterId = composition.ChapterId;
        db.CompositionMutationStages.Remove(stage);
        await TouchProjectAsync(projectId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await manuscripts.RefreshDerivedStateAsync(chapterId, cancellationToken);
        return new CompositionSemanticMutationResult(composition, applied.ChangedBlockIds);
    }

    public async Task<CompositionMutationStage> StageWorkspaceAsync(
        Guid projectId,
        Guid conversationId,
        Guid compositionId,
        long expectedCompositionRevision,
        Guid variantId,
        long expectedVariantRevision,
        IReadOnlyList<ManuscriptOperationInput> semanticOperations,
        CompositionScene scene,
        CancellationToken cancellationToken = default)
    {
        if (semanticOperations.Count > 200)
            throw new ArgumentException("A composition workspace stage accepts at most 200 focused semantic operations.");
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        await db.CompositionMutationStages
            .Where(item => item.ProjectId == projectId && (item.ExpiresAt <= DateTime.UtcNow || item.AppliedAt != null))
            .ExecuteDeleteAsync(cancellationToken);
        var variant = await db.PageCompositionVariants.AsNoTracking().Include(item => item.Composition)
            .SingleOrDefaultAsync(item => item.Id == variantId && item.CompositionId == compositionId
                && item.Composition.ProjectId == projectId, cancellationToken)
            ?? throw new KeyNotFoundException("Composition workspace was not found in this project.");
        if (variant.Composition.Revision != expectedCompositionRevision)
            throw new CompositionRevisionConflictException(expectedCompositionRevision, variant.Composition.Revision);
        if (variant.Revision != expectedVariantRevision)
            throw new CompositionRevisionConflictException(expectedVariantRevision, variant.Revision);
        var current = ManuscriptCodec.Deserialize(
            variant.Composition.SemanticManuscriptJson,
            compositionId,
            expectedCompositionRevision);
        var applied = semanticOperations.Count == 0
            ? (Document: current, ChangedBlockIds: (IReadOnlyList<string>)[])
            : ManuscriptOperations.Apply(current, ManuscriptOperationInput.ToOperations(semanticOperations));
        ValidateSemanticFragment(applied.Document);
        await ValidateVariantGeometryAsync(projectId, variant.GeometryKey, scene, cancellationToken);
        await ValidateSceneAssetsAsync(projectId, scene, cancellationToken);
        _ = SerializeAndValidate(scene, ManuscriptCodec.Serialize(applied.Document));
        foreach (var other in await db.PageCompositionVariants.AsNoTracking()
            .Where(item => item.CompositionId == compositionId && item.Id != variantId)
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
            TargetKind = "composition-workspace",
            TargetId = compositionId,
            ExpectedRevision = expectedCompositionRevision,
            OperationsJson = payload,
            PayloadSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload))),
            ExpiresAt = DateTime.UtcNow.AddMinutes(30),
        };
        db.CompositionMutationStages.Add(stage);
        await db.SaveChangesAsync(cancellationToken);
        return stage;
    }

    public async Task<CompositionWorkspaceMutationResult> ApplyWorkspaceStageAsync(
        Guid projectId,
        Guid conversationId,
        Guid stageId,
        long expectedCompositionRevision,
        CancellationToken cancellationToken = default)
    {
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var stage = await db.CompositionMutationStages.SingleOrDefaultAsync(item => item.Id == stageId
            && item.ProjectId == projectId && item.ConversationId == conversationId
            && item.TargetKind == "composition-workspace", cancellationToken)
            ?? throw new KeyNotFoundException("Composition workspace stage was not found for this conversation.");
        if (stage.ExpiresAt <= DateTime.UtcNow)
            throw new InvalidOperationException("The composition workspace stage has expired; submit it again.");
        if (stage.ExpectedRevision != expectedCompositionRevision)
            throw new CompositionRevisionConflictException(expectedCompositionRevision, stage.ExpectedRevision);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stage.OperationsJson)));
        if (!string.Equals(hash, stage.PayloadSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The staged composition workspace payload failed its integrity check.");
        var payload = JsonSerializer.Deserialize<CompositionWorkspaceStagePayload>(stage.OperationsJson, JsonOptions)
            ?? throw new InvalidDataException("The staged composition workspace payload is empty.");
        var variant = await db.PageCompositionVariants.Include(item => item.Composition)
            .SingleOrDefaultAsync(item => item.Id == payload.VariantId && item.CompositionId == stage.TargetId
                && item.Composition.ProjectId == projectId, cancellationToken)
            ?? throw new KeyNotFoundException("The staged composition workspace no longer exists.");
        if (variant.Composition.Revision != expectedCompositionRevision)
            throw new CompositionRevisionConflictException(expectedCompositionRevision, variant.Composition.Revision);
        if (variant.Revision != payload.ExpectedVariantRevision)
            throw new CompositionRevisionConflictException(payload.ExpectedVariantRevision, variant.Revision);
        var current = ManuscriptCodec.Deserialize(variant.Composition.SemanticManuscriptJson, stage.TargetId, expectedCompositionRevision);
        var applied = payload.SemanticOperations.Count == 0
            ? (Document: current, ChangedBlockIds: (IReadOnlyList<string>)[])
            : ManuscriptOperations.Apply(current, ManuscriptOperationInput.ToOperations(payload.SemanticOperations));
        ValidateSemanticFragment(applied.Document);
        var semanticJson = ManuscriptCodec.Serialize(applied.Document);
        await ValidateVariantGeometryAsync(projectId, variant.GeometryKey, payload.Scene, cancellationToken);
        await ValidateSceneAssetsAsync(projectId, payload.Scene, cancellationToken);
        var sceneJson = SerializeAndValidate(payload.Scene, semanticJson);
        foreach (var other in await db.PageCompositionVariants.AsNoTracking()
            .Where(item => item.CompositionId == stage.TargetId && item.Id != variant.Id)
            .ToListAsync(cancellationToken))
        {
            var otherScene = JsonSerializer.Deserialize<CompositionScene>(other.SceneJson, JsonOptions)
                ?? throw new InvalidDataException("A composition variant scene is empty.");
            _ = SerializeAndValidate(otherScene, semanticJson);
        }
        variant.Composition.SemanticManuscriptJson = semanticJson;
        variant.Composition.Revision = applied.Document.Revision;
        variant.Composition.UpdatedAt = DateTime.UtcNow;
        var exactKey = await ExactGeometryKeyAsync(projectId, variant.GeometryKey, payload.Scene, cancellationToken);
        if (!string.Equals(exactKey, variant.GeometryKey, StringComparison.Ordinal))
        {
            if (await db.PageCompositionVariants.AnyAsync(item => item.CompositionId == stage.TargetId && item.GeometryKey == exactKey, cancellationToken))
                throw new InvalidOperationException("This exact layout geometry already exists. Select that variant instead of overwriting it.");
            variant = new PageCompositionVariant
            {
                CompositionId = stage.TargetId,
                Composition = variant.Composition,
                GeometryKey = exactKey,
                SceneJson = sceneJson,
            };
            db.PageCompositionVariants.Add(variant);
        }
        else
        {
            variant.SceneJson = sceneJson;
            variant.Revision = checked(variant.Revision + 1);
            variant.UpdatedAt = DateTime.UtcNow;
        }
        db.CompositionMutationStages.Remove(stage);
        await TouchProjectAsync(projectId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await manuscripts.RefreshDerivedStateAsync(variant.Composition.ChapterId, cancellationToken);
        return new CompositionWorkspaceMutationResult(variant.Composition, variant, applied.ChangedBlockIds);
    }

    public async Task<LayoutGenerationTargetDescriptor> DescribeGenerationTargetAsync(
        Guid projectId,
        Guid editionId,
        string targetKind,
        Guid targetId,
        Guid? variantId = null,
        CancellationToken cancellationToken = default)
    {
        var edition = await db.PublicationEditions.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == editionId && item.ProjectId == projectId,
            cancellationToken) ?? throw new KeyNotFoundException("Edition was not found in this project.");
        var normalizedKind = targetKind.Trim().ToLowerInvariant();
        if (normalizedKind is "page-surface" or "page-frame" && variantId is null)
            throw new ArgumentException("Page layout generation targets require the exact composition variant ID.", nameof(variantId));
        if (normalizedKind is not ("page-surface" or "page-frame") && variantId is not null)
            throw new ArgumentException("variantId is valid only for page-surface and page-frame targets.", nameof(variantId));
        var (width, height, regions, diagnostics) = normalizedKind switch
        {
            "figure" => await ResolveFigureTargetAsync(projectId, edition, targetId, cancellationToken),
            "page-surface" => await ResolvePageSurfaceTargetAsync(projectId, edition, targetId, variantId!.Value, cancellationToken),
            "page-frame" => await ResolvePageFrameTargetAsync(projectId, edition, targetId, variantId!.Value, cancellationToken),
            "cover-surface" => await ResolveCoverSurfaceTargetAsync(projectId, edition, targetId, cancellationToken),
            "cover-frame" => await ResolveCoverFrameTargetAsync(projectId, edition, targetId, cancellationToken),
            _ => throw new ArgumentException("targetKind must be figure, page-surface, page-frame, cover-surface, or cover-frame.", nameof(targetKind)),
        };
        var gcd = GreatestCommonDivisor((int)Math.Round(width * 1000), (int)Math.Round(height * 1000));
        var pixelsPerInch = edition.Format == PublicationEditionFormat.Paperback ? 300 : 180;
        var aspect = $"{(int)Math.Round(width * 1000) / gcd}:{(int)Math.Round(height * 1000) / gcd}";
        var providerCanvas = ProviderCanvas(width, height);
        var descriptorDiagnostics = diagnostics.ToList();
        var (canvasWidth, canvasHeight) = providerCanvas switch
        {
            "landscape" => (1536d, 1024d),
            "portrait" => (1024d, 1536d),
            _ => (1024d, 1024d),
        };
        var providerDpi = Math.Min(canvasWidth / Math.Max(.01, width), canvasHeight / Math.Max(.01, height));
        if (providerDpi + .5 < pixelsPerInch)
            descriptorDiagnostics.Add($"The provider canvas supplies approximately {providerDpi:0} effective DPI for this target; publication validation expects {pixelsPerInch} DPI. Generate panels or frames separately, or provide a higher-resolution source.");
        var geometryFingerprint = TargetGeometryFingerprint(
            edition,
            normalizedKind,
            targetId,
            variantId,
            width,
            height,
            regions);
        return new LayoutGenerationTargetDescriptor(
            edition.Id,
            variantId,
            geometryFingerprint,
            normalizedKind,
            targetId,
            width,
            height,
            aspect,
            (int)Math.Ceiling(width * pixelsPerInch),
            (int)Math.Ceiling(height * pixelsPerInch),
            providerCanvas,
            pixelsPerInch,
            regions,
            descriptorDiagnostics);
    }

    public async Task<CompositionEditionGeometry> GetEditionGeometryAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken = default)
    {
        var edition = await db.PublicationEditions.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == editionId && item.ProjectId == projectId,
            cancellationToken) ?? throw new KeyNotFoundException("Edition was not found in this project.");
        return new CompositionEditionGeometry(
            edition.PageWidthInches * 72,
            edition.PageHeightInches * 72,
            edition.Format == PublicationEditionFormat.DigitalPdf && edition.AllowDesignedPageOverrides);
    }

    public async Task<LayoutValidationView> ValidateVariantAsync(
        Guid projectId,
        Guid editionId,
        Guid variantId,
        CancellationToken cancellationToken = default)
    {
        var edition = await db.PublicationEditions.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == editionId && item.ProjectId == projectId,
            cancellationToken) ?? throw new KeyNotFoundException("Edition was not found in this project.");
        var variant = await db.PageCompositionVariants.AsNoTracking().Include(item => item.Composition)
            .SingleOrDefaultAsync(item => item.Id == variantId && item.Composition.ProjectId == projectId, cancellationToken)
            ?? throw new KeyNotFoundException("Composition variant was not found in this project.");
        var diagnostics = new List<LayoutValidationDiagnostic>();
        if (!VariantMatchesEdition(variant, edition))
            diagnostics.Add(new("error", "GEOMETRY_VARIANT_MISMATCH", "The variant does not belong to the selected edition geometry."));
        var scene = JsonSerializer.Deserialize<CompositionScene>(variant.SceneJson, JsonOptions)
            ?? throw new InvalidDataException("The composition scene is empty.");
        var semantic = ManuscriptCodec.Deserialize(variant.Composition.SemanticManuscriptJson);
        try
        {
            ValidateVariantGeometry(edition, scene);
            Validate(scene, semantic);
            await ValidateSceneAssetsAsync(projectId, scene, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            diagnostics.Add(new("error", "COMPOSITION_INVALID", exception.Message));
        }
        if (edition.Vendor == PublicationVendor.IngramSpark
            && CompositionSceneResolver.FindPdfxTransparencyOverlap(scene) is { } opacityOverlap)
        {
            diagnostics.Add(new(
                "error",
                "PDFX_TRANSPARENCY_OVERLAP",
                $"Object {opacityOverlap.TransparentObjectId:N} uses opacity over lower object {opacityOverlap.LowerObjectId:N}. PDF/X-1a can flatten opacity only against the page substrate while preserving editable text and vector content; make it opaque or precompose the overlapping artwork as one image.",
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
        foreach (var item in visibleText)
        {
            if (string.IsNullOrWhiteSpace(item.TextBinding) && item.ContentReferences.Count == 0)
                diagnostics.Add(new("error", "TEXT_UNBOUND", "Text frame is not bound to semantic composition content.", item.Id));
            if (item.ContentReferences.Select(reference => reference.BlockId).Distinct(StringComparer.Ordinal).Skip(1).Any())
                diagnostics.Add(new("error", "TEXT_SEMANTIC_ROLE_MIXED", "A text frame may bind ranges from only one semantic block so its PDF and EPUB role remains unambiguous.", item.Id));
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
        foreach (var item in flattened.Where(item => item.Kind == CompositionObjectKind.Text && IsOutputVisible(scene, item)))
        {
            string text;
            try { text = ManuscriptRangeResolver.ResolveText(semantic, item.ContentReferences); }
            catch (InvalidDataException) { continue; }
            var width = scene.Surface.WidthPoints * item.Bounds.WidthPercent / 100;
            var height = scene.Surface.HeightPoints * item.Bounds.HeightPercent / 100;
            var charactersPerLine = Math.Max(1, (int)(width / Math.Max(1, item.FontSizePoints * .55)));
            var lines = Math.Max(1, (int)Math.Ceiling((double)text.Length / charactersPerLine));
            if (lines * item.FontSizePoints * item.LineHeight > height)
                diagnostics.Add(new("error", "TEXT_OVERFLOW", "Text is likely to overflow its frame at the current typography.", item.Id));
        }

        var imageIds = flattened.Where(item => item.Kind == CompositionObjectKind.Image && item.ImageId is not null)
            .Select(item => item.ImageId!.Value).Distinct().ToList();
        var assets = await db.PublishAssets.AsNoTracking().Where(item => item.ProjectId == projectId && imageIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var requiredDpi = edition.Format == PublicationEditionFormat.Paperback ? 300d : 180d;
        foreach (var item in flattened.Where(item => item.Kind == CompositionObjectKind.Image && item.ImageId is not null && IsOutputVisible(scene, item)))
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

    private async Task<(double Width, double Height, IReadOnlyList<LayoutGenerationRegionDescriptor> Regions, IReadOnlyList<string> Diagnostics)> ResolveFigureTargetAsync(
        Guid projectId,
        PublicationEdition edition,
        Guid targetId,
        CancellationToken cancellationToken)
    {
        var blockId = targetId.ToString("N");
        var chapters = await db.Chapters.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .Select(item => item.ManuscriptJson)
            .ToListAsync(cancellationToken);
        var figure = chapters.Select(json => ManuscriptCodec.Deserialize(json).Content
                .FirstOrDefault(block => block.Type == ManuscriptBlockType.Figure
                    && string.Equals(block.Id, blockId, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(block => block is not null)
            ?? throw new KeyNotFoundException("Figure target was not found in this project.");
        var presentation = figure.FigurePresentation ?? new FigurePresentation();
        var pageWidth = edition.PageWidthInches + (presentation.Placement == FigurePlacementIntent.FullBleed && edition.Bleed ? .25 : 0);
        var pageHeight = edition.PageHeightInches + (presentation.Placement == FigurePlacementIntent.FullBleed && edition.Bleed ? .25 : 0);
        var contentWidth = Math.Max(.25, edition.PageWidthInches - edition.PageMarginInches * 2);
        var contentHeight = Math.Max(.25, edition.PageHeightInches - edition.PageMarginInches * 2);
        var width = presentation.Placement == FigurePlacementIntent.FullBleed
            ? pageWidth
            : contentWidth * Math.Clamp(presentation.WidthPercent, 5, 100) / 100;
        var height = presentation.Placement switch
        {
            FigurePlacementIntent.FullBleed => pageHeight,
            FigurePlacementIntent.DedicatedPage => contentHeight * .75,
            _ => Math.Min(contentHeight * .34, width * 1.25),
        };
        var regions = presentation.Placement == FigurePlacementIntent.FullBleed
            ? PageRegions(CreatePageScene(edition), includeReservedText: false)
            : presentation.Placement == FigurePlacementIntent.DedicatedPage
                ? PageRegions(CreatePageScene(edition), includeReservedText: true)
                : Array.Empty<LayoutGenerationRegionDescriptor>();
        return (width, height, regions, []);
    }

    private async Task<(double Width, double Height, IReadOnlyList<LayoutGenerationRegionDescriptor> Regions, IReadOnlyList<string> Diagnostics)> ResolvePageSurfaceTargetAsync(
        Guid projectId,
        PublicationEdition edition,
        Guid compositionId,
        Guid variantId,
        CancellationToken cancellationToken)
    {
        var (_, scene) = await ReadExactVariantSceneAsync(projectId, edition, variantId, cancellationToken);
        var ownsComposition = await db.PageCompositionVariants.AsNoTracking()
            .AnyAsync(item => item.Id == variantId && item.CompositionId == compositionId, cancellationToken);
        if (!ownsComposition)
            throw new KeyNotFoundException("The selected layout variant does not belong to this page composition.");
        return (scene.Surface.WidthPoints / 72, scene.Surface.HeightPoints / 72, PageRegions(scene, includeReservedText: true), []);
    }

    private async Task<(double Width, double Height, IReadOnlyList<LayoutGenerationRegionDescriptor> Regions, IReadOnlyList<string> Diagnostics)> ResolvePageFrameTargetAsync(
        Guid projectId,
        PublicationEdition edition,
        Guid frameId,
        Guid variantId,
        CancellationToken cancellationToken)
    {
        var (_, scene) = await ReadExactVariantSceneAsync(projectId, edition, variantId, cancellationToken);
        var frame = CompositionSceneResolver.Flatten(scene)
            .FirstOrDefault(item => item.Id == frameId && item.Kind == CompositionObjectKind.Image);
        if (frame is null)
            throw new KeyNotFoundException("Image frame target was not found in the selected composition variant.");
        return FrameDimensions(
            scene,
            frame,
            PageRegions(scene, includeReservedText: true));
    }

    private async Task<(PageCompositionVariant Variant, CompositionScene Scene)> ReadExactVariantSceneAsync(
        Guid projectId,
        PublicationEdition edition,
        Guid variantId,
        CancellationToken cancellationToken)
    {
        var variant = await db.PageCompositionVariants.AsNoTracking()
            .Include(item => item.Composition)
            .SingleOrDefaultAsync(item => item.Id == variantId && item.Composition.ProjectId == projectId, cancellationToken)
            ?? throw new KeyNotFoundException("Composition variant was not found in this project.");
        if (!VariantMatchesEdition(variant, edition))
            throw new InvalidOperationException("The selected composition variant is not compatible with this edition geometry.");
        var scene = JsonSerializer.Deserialize<CompositionScene>(variant.SceneJson, JsonOptions)
            ?? throw new InvalidDataException("The composition scene is empty.");
        return (variant, scene);
    }

    private async Task<(double Width, double Height, IReadOnlyList<LayoutGenerationRegionDescriptor> Regions, IReadOnlyList<string> Diagnostics)> ResolveCoverSurfaceTargetAsync(
        Guid projectId,
        PublicationEdition edition,
        Guid targetId,
        CancellationToken cancellationToken)
    {
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
        return (JsonSerializer.Deserialize<CompositionScene>(cover.CompositionSceneJson, JsonOptions)
            ?? throw new InvalidDataException("The cover composition scene is empty."), cover.Template.PageCount);
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
            ? ["Keep essential artwork and text outside the bleed, gutter, spine folds, and barcode reserve."]
            : ["Keep essential artwork and text inside the digital cover safe area."];

    public static string GeometryKey(PublicationEdition edition) => GeometryKey(edition, CreatePageScene(edition));

    public static string GeometryKey(PublicationEdition edition, CompositionScene scene)
    {
        var canonical = GeometryCanonical(edition)
            + (edition.Format == PublicationEditionFormat.DigitalPdf && edition.AllowDesignedPageOverrides
                ? "|independent-pages"
                : string.Empty)
            + FormattableString.Invariant(
                $"|{scene.Surface.Kind}|{scene.Surface.OutputPageMode}|{scene.Surface.WidthPoints:F4}|{scene.Surface.HeightPoints:F4}|{scene.Surface.BleedPoints:F4}|{scene.Surface.SafeInsetPoints:F4}|{scene.Surface.AllowIndependentPdfPage}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..24];
    }

    public static bool VariantMatchesEdition(PageCompositionVariant variant, PublicationEdition edition)
    {
        try
        {
            var scene = JsonSerializer.Deserialize<CompositionScene>(variant.SceneJson, JsonOptions);
            if (scene is null) return false;
            ValidateVariantGeometry(edition, scene);
            return string.Equals(variant.GeometryKey, GeometryKey(edition, scene), StringComparison.Ordinal)
                // Accept the pre-exact-geometry key only at the isolated upgrade boundary.
                || string.Equals(variant.GeometryKey, LegacyEditionOnlyGeometryKey(edition), StringComparison.Ordinal)
                || string.Equals(variant.GeometryKey, LegacyGeometryKey(edition), StringComparison.Ordinal);
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
        var sourceSceneJson = await db.PageCompositionVariants.AsNoTracking()
            .Where(item => item.GeometryKey == currentKey)
            .Select(item => item.SceneJson)
            .FirstOrDefaultAsync(cancellationToken);
        var sourceScene = sourceSceneJson is null
            ? null
            : JsonSerializer.Deserialize<CompositionScene>(sourceSceneJson, JsonOptions);
        var editions = await db.PublicationEditions.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .ToListAsync(cancellationToken);
        var edition = editions.FirstOrDefault(item =>
        {
            try
            {
                if (sourceScene is not null)
                    ValidateVariantGeometry(item, sourceScene);
                ValidateVariantGeometry(item, scene);
                return (sourceScene is not null && string.Equals(currentKey, GeometryKey(item, sourceScene), StringComparison.Ordinal))
                    || string.Equals(currentKey, LegacyEditionOnlyGeometryKey(item), StringComparison.Ordinal)
                    || string.Equals(currentKey, LegacyGeometryKey(item), StringComparison.Ordinal);
            }
            catch (InvalidDataException) { return false; }
        }) ?? throw new InvalidDataException("The composition variant does not match a current edition geometry.");
        return GeometryKey(edition, scene);
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
        $"{edition.Format}|{edition.PageWidthInches:F4}|{edition.PageHeightInches:F4}|{edition.PageMarginInches:F4}|{edition.Bleed}|{edition.Binding}|{edition.VendorProfileVersion}");

    public static CompositionScene CreatePageScene(PublicationEdition edition) => new()
    {
        Surface = new CompositionSurface
        {
            WidthPoints = edition.PageWidthInches * 72,
            HeightPoints = edition.PageHeightInches * 72,
            BleedPoints = edition.Bleed ? 9 : 0,
            SafeInsetPoints = edition.PageMarginInches * 72,
            AllowIndependentPdfPage = edition.Format == PublicationEditionFormat.DigitalPdf
                && edition.AllowDesignedPageOverrides,
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
        var editions = await db.PublicationEditions.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .ToListAsync(cancellationToken);
        var edition = editions.FirstOrDefault(item =>
        {
            try
            {
                ValidateVariantGeometry(item, scene);
                return string.Equals(GeometryKey(item, scene), geometryKey, StringComparison.Ordinal)
                    || string.Equals(LegacyEditionOnlyGeometryKey(item), geometryKey, StringComparison.Ordinal)
                    || string.Equals(LegacyGeometryKey(item), geometryKey, StringComparison.Ordinal);
            }
            catch (InvalidDataException) { return false; }
        })
            ?? throw new InvalidDataException("The composition variant does not match a current edition geometry.");
        ValidateVariantGeometry(edition, scene);
    }

    internal static void ValidateVariantGeometry(PublicationEdition edition, CompositionScene scene)
    {
        var leafWidth = edition.PageWidthInches * 72;
        var leafHeight = edition.PageHeightInches * 72;
        var expectedWidth = scene.Surface.Kind == CompositionSurfaceKind.FacingSpread
            ? leafWidth * 2
            : leafWidth;
        var independent = scene.Surface.Kind == CompositionSurfaceKind.IndependentPage;
        if (independent && (edition.Format != PublicationEditionFormat.DigitalPdf || !edition.AllowDesignedPageOverrides))
            throw new InvalidDataException("Independent page geometry is enabled only for Digital PDF editions that allow Designed Page overrides.");
        if (!independent && (Math.Abs(scene.Surface.WidthPoints - expectedWidth) > .01
            || Math.Abs(scene.Surface.HeightPoints - leafHeight) > .01))
            throw new InvalidDataException("The composition surface must use the edition's exact leaf or facing-spread geometry.");
        if (edition.Format == PublicationEditionFormat.Paperback && independent)
            throw new InvalidDataException("Print editions require consistent physical leaf dimensions.");
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
        var semanticIds = semantic.Content.Select(block => block.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var item in scene.Objects)
        {
            if (item.Id == Guid.Empty || !objectIds.Add(item.Id) || !layerIds.Contains(item.LayerId))
                throw new InvalidDataException("Composition objects require unique IDs and an existing layer.");
            if (item.Bounds.WidthPercent is <= 0 or > 400 || item.Bounds.HeightPercent is <= 0 or > 400
                || item.Opacity is < 0 or > 1 || item.BackgroundOpacity is < 0 or > 1
                || item.LetterSpacingEm is < -1 or > 10
                || item.FocalXPercent is < 0 or > 100 || item.FocalYPercent is < 0 or > 100)
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
            if (item.ContentReferences.Any(reference => !semanticIds.Contains(reference.BlockId)))
                throw new InvalidDataException($"Text object {item.Id:N} references content outside its composition document.");
            if (item.Kind == CompositionObjectKind.Text
                && item.ContentReferences.Select(reference => reference.BlockId).Distinct(StringComparer.Ordinal).Skip(1).Any())
                throw new InvalidDataException($"Text object {item.Id:N} may bind ranges from only one semantic block.");
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
                    && item.ContentReferences.FirstOrDefault() is { } reference
                    && blocks.TryGetValue(reference.BlockId, out var block)
                ? item with { SemanticRole = SemanticRole(block) }
                : item).ToList(),
        };
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

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
            (left, right) = (right, left % right);
        return Math.Max(left, 1);
    }

    private static string ProviderCanvas(double width, double height)
    {
        var ratio = width / height;
        return ratio >= 1.35 ? "landscape" : ratio <= .74 ? "portrait" : "square";
    }

    private static string TargetGeometryFingerprint(
        PublicationEdition edition,
        string targetKind,
        Guid targetId,
        Guid? variantId,
        double width,
        double height,
        IReadOnlyList<LayoutGenerationRegionDescriptor> regions)
    {
        var canonical = new StringBuilder()
            .Append(GeometryCanonical(edition)).Append('|')
            .Append(targetKind).Append('|').Append(targetId.ToString("N")).Append('|')
            .Append(variantId?.ToString("N") ?? "-").Append('|')
            .Append(width.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
            .Append(height.ToString("F6", System.Globalization.CultureInfo.InvariantCulture));
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

    private async Task TouchProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects.SingleAsync(item => item.Id == projectId, cancellationToken);
        project.UpdatedAt = DateTime.UtcNow;
    }
}

public sealed class CompositionRevisionConflictException(long expectedRevision, long actualRevision)
    : InvalidOperationException($"Composition revision conflict: expected {expectedRevision}, current revision is {actualRevision}.")
{
    public long ExpectedRevision { get; } = expectedRevision;
    public long ActualRevision { get; } = actualRevision;
}

public sealed record DesignedPageCreationResult(
    PageComposition Composition,
    PageCompositionVariant? Variant,
    ManuscriptSnapshot Manuscript,
    string BlockId);

public sealed record DesignedPageIdentity(Guid CompositionId, string BlockId);

public sealed record CompositionWorkspaceSaveResult(
    PageComposition Composition,
    PageCompositionVariant Variant);

public sealed record CompositionSemanticMutationResult(
    PageComposition Composition,
    IReadOnlyList<string> ChangedBlockIds);

public sealed record CompositionWorkspaceMutationResult(
    PageComposition Composition,
    PageCompositionVariant Variant,
    IReadOnlyList<string> ChangedBlockIds);

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
    double? FocalXPercent = null,
    double? FocalYPercent = null,
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
